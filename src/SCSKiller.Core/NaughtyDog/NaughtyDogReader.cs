using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.NaughtyDog;

/// <summary>Naughty Dog's engine as its PC ports ship it (measured: The Last of Us Part II Remastered): every shader sits in
/// the PSARC archives (<see cref="Psarc"/>) under build\pc, as an "ndshader" record (<see cref="Records"/>) inside the level
/// and actor packages (*.pak) and, for the engine's own passes, as one file each (shaders\bytecode\*.cxo/.pxo/.vxo/.gxo).
/// Each record carries its DXIL container and, for pixel and compute shaders, the root signature the game creates from it
/// (an RTS0-only container): vertex shaders carry none and take their pixel shader's. Nothing names which VS meets which PS
/// (default.pso, the shipped pipeline list, names them by Naughty Dog's own hash, which few shipped shaders match), so each
/// package is one pool, paired by linkage, and the single-shader files of an archive one more.
/// EngineInfo: Family "NaughtyDog", Version "DXIL+RTS0".</summary>
public sealed class NaughtyDogReader : IEngineReader
{
    public const string Family = "NaughtyDog", Version = "DXIL" + CarvedReader.EmbeddedRootSignatures;
    const string Build = "build";

    /// <summary>Record header: "ndshader", u32 version, u32 flags (low byte: the header size), 16 bytes of the engine's hashes,
    /// u32 shader size, u32 root-signature size; the shader, then its root signature.</summary>
    const int HeaderSize = 40;

    /// <summary>Archives Detect opens, smallest first, to find one record.</summary>
    const int DetectArchives = 8;

    public sealed record Loc(string Archive, int Entry, int Offset, int Size);

    /// <summary>Where each container is (by SHA-1), per game id, from the last Index. ponytail: in memory only, like the
    /// carver's; ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    public EngineInfo? Detect(Game game)
    {
        foreach (var f in Archives(game).OrderBy(f => f.Length).Take(DetectArchives))
        {
            Psarc p;
            try { p = new Psarc(f.FullName); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException) { continue; }
            using var _ = p;
            foreach (var e in p.Entries.Where(e => Scanned(e.Name)).OrderBy(e => e.Size).Take(16))
                if (p.Read(e) is var b && Records(b, b.Length).Any())
                    return new EngineInfo(Family, Version, null, "D3D12", false, null);
        }
        return null;
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var archives = Archives(game).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).ToList();
        var work = new List<(FileInfo Archive, Psarc.Entry Entry)>();
        foreach (var f in archives)
            try
            {
                using var p = new Psarc(f.FullName);
                work.AddRange(p.Entries.Where(e => Scanned(e.Name) && e.Size <= Psarc.MaxEntry).Select(e => (f, e)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException) { }

        var found = new List<(string Sha, Loc At, string? Rs)>[work.Count];
        var infos = new ConcurrentDictionary<string, ShaderInfo?>();
        var rsLocs = new ConcurrentDictionary<string, Loc>();
        var rsBlobs = new ConcurrentDictionary<string, byte[]>();   // a few hundred, for the planner's guard
        var lanePlatform = new ConcurrentDictionary<string, string>();   // shaders a 32-lane GPU can't run (CarvedReader.LanePlatform)
        long read = 0;
        int bad = 0;
        using var workers = new Workers();
        Parallel.For(0, work.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) },
            workers.Take, (i, _, w) =>
            {
                var (f, e) = work[i];
                workers.Unpack(w, w.Archive(f.FullName), e, (b, n) => Scan(i, f, e, b, n));
                return w;
            }, workers.Return);

        void Scan(int i, FileInfo f, Psarc.Entry e, byte[] b, int n)
        {
            Interlocked.Add(ref read, n);
            var list = found[i] = [];
            foreach (var r in Records(b, n))
            {
                var c = b.AsSpan(r.Shader, r.ShaderSize);
                var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                string? rs = null;
                if (r.RsSize > 0)
                {
                    rs = Convert.ToHexStringLower(SHA1.HashData(b.AsSpan(r.Rs, r.RsSize)));
                    if (rsLocs.TryAdd(rs, new Loc(f.FullName, e.Index, r.Rs, r.RsSize))) rsBlobs[rs] = b.AsSpan(r.Rs, r.RsSize).ToArray();
                }
                if (!infos.ContainsKey(sha))
                {
                    ShaderInfo? info = null;
                    try { info = ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)); }
                    catch (Exception x) when (x is ArgumentException or IndexOutOfRangeException) { Interlocked.Increment(ref bad); } // valid container, odd program
                    if (info != null && CarvedReader.LanePlatform(Dxbc.WaveLanes(c)) is { } lanes) lanePlatform[sha] = lanes;
                    infos.TryAdd(sha, info);
                }
                list.Add((sha, new Loc(f.FullName, e.Index, r.Shader, r.ShaderSize), rs));
            }
        }

        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>(rsLocs);
        var rsOf = new Dictionary<string, Dictionary<string, int>>();
        var maps = new List<ShaderMap>();
        var platforms = new SortedSet<string>(StringComparer.Ordinal) { CarvedReader.Platform };
        string PlatformOf(string sha) => lanePlatform.GetValueOrDefault(sha, CarvedReader.Platform);
        var loose = new Dictionary<string, List<string>>();
        for (var i = 0; i < work.Count; i++)
        {
            var (f, e) = work[i];
            var rel = Path.GetRelativePath(game.InstallDir, f.FullName);
            foreach (var (sha, at, rs) in found[i])
            {
                if (infos[sha] is null) continue;
                locs.TryAdd(sha, at);
                if (rs != null) CollectionsMarshal.GetValueRefOrAddDefault(rsOf.TryGetValue(sha, out var n) ? n : rsOf[sha] = [], rs, out _)++;
            }
            var shas = found[i].Where(x => infos[x.Sha] != null).Select(x => x.Sha).Distinct().ToList();
            if (shas.Count == 0) continue;
            if (!Package(e.Name)) { (loose.TryGetValue(rel, out var l) ? l : loose[rel] = []).AddRange(shas); continue; }
            foreach (var pool in shas.GroupBy(PlatformOf))
                maps.Add(Map($"{rel}:{e.Name}", pool.Key, [.. pool]));
        }
        foreach (var (rel, shas) in loose)
            foreach (var pool in shas.Distinct().GroupBy(PlatformOf))
                maps.Add(Map(rel, pool.Key, [.. pool]));
        ShaderMap Map(string library, string platform, List<string> shas)
        {
            platforms.Add(platform);
            return new ShaderMap(CarvedReader.Sha1Hex(platform == CarvedReader.Platform ? library : $"{library}|{platform}"), library, platform, shas);
        }

        var several = 0;
        foreach (var sha in locs.Keys.Where(infos.ContainsKey))
        {
            var info = infos[sha]!;
            // a shader stored with several root signatures (measured: 425 pixel and compute shaders of 144,690) takes one that
            // covers what it declares, the one it's stored with most first (its partner VS carries none: the pick is the pair's)
            string? rs = null;
            if (rsOf.TryGetValue(sha, out var count))
            {
                var order = count.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Key).ToList();
                rs = order.Count == 1 ? order[0] : order.FirstOrDefault(h => Planning.RootSig.Uncovered(Planning.RootSig.Parse(rsBlobs[h]), info.Stage, info) == null) ?? order[0];
            }
            if (count is { Count: > 1 }) several++;
            shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings), RootSignature = rs };
        }
        if (shaders.Count == 0) throw new InvalidDataException($"{Build}: no shader in its archives: scan the game again");
        located[game.Id] = locs;

        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var f in archives) content.AppendData(Encoding.UTF8.GetBytes($"{Path.GetRelativePath(game.InstallDir, f.FullName)}|{f.Length}|{f.LastWriteTimeUtc.Ticks}\n"));
        log?.Report($"{archives.Count} archives, {work.Count} packages and shader files, {read / 1e9:F1} GB unpacked: {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"), {rsLocs.Count} root signatures, {maps.Count} pools" + (several > 0 ? $"; {several} shaders stored with several root signatures given their most common one" : "")
            + (bad > 0 ? $"; {bad} unparseable" : "") + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), [.. platforms], shaders, maps, rsBlobs);
    }

    /// <summary>Unpacks each package holding a requested container once and re-slices it; one whose bytes changed since (game
    /// patched) is skipped.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        var gate = new object();
        using var workers = new Workers();
        var groups = sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => (x.At.Archive, x.At.Entry))
            .OrderBy(g => g.Key.Archive, StringComparer.Ordinal).ThenBy(g => g.Key.Entry).ToList();
        Parallel.ForEach(groups, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) }, workers.Take, (g, _, w) =>
        {
            try
            {
                var p = w.Archive(g.Key.Archive);
                if (g.Key.Entry <= p.Entries.Count)
                    workers.Unpack(w, p, p.Entries[g.Key.Entry - 1], (b, n) =>
                    {
                        foreach (var (sha, at) in g)
                            if (at.Offset + (long)at.Size <= n && b.AsSpan(at.Offset, at.Size) is var c && Convert.ToHexStringLower(SHA1.HashData(c)) == sha)
                            {
                                var copy = c.ToArray();
                                lock (gate) sink(sha, copy);
                            }
                    });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { } // gone or changed since the index
            return w;
        }, workers.Return);
    }

    /// <summary>Entries bigger than this unpack one at a time, into one shared buffer: The Last of Us Part II's packages reach
    /// 669 MB, and a buffer that size per thread made a 5 GB peak working set for a 0.5 GB index.</summary>
    const int SmallEntry = 64 << 20;

    /// <summary>The <see cref="Worker"/>s of one parallel loop, kept for its tasks (the loop retires and starts tasks as it
    /// runs; each new one would open its archive and grow its buffer again), and the buffer of the big entries.</summary>
    sealed class Workers : IDisposable
    {
        readonly ConcurrentBag<Worker> idle = [];
        readonly Lock big = new();
        byte[] bigBuffer = [];

        public Worker Take() => idle.TryTake(out var w) ? w : new Worker();
        public void Return(Worker w) => idle.Add(w);

        /// <summary>The entry unpacked, passed to <paramref name="use"/> with its size (the buffer is reused after it returns).</summary>
        public void Unpack(Worker w, Psarc p, Psarc.Entry e, Action<byte[], int> use)
        {
            if (e.Size <= SmallEntry)
            {
                var n = p.Read(e, ref w.Buffer);   // may replace the buffer
                use(w.Buffer, n);
                return;
            }
            lock (big)
            {
                var n = p.Read(e, ref bigBuffer);
                use(bigBuffer, n);
            }
        }

        public void Dispose() { foreach (var w in idle) w.Dispose(); }
    }

    /// <summary>One task's open archive and buffer for small entries. One archive at a time (the work is in archive order):
    /// each holds its block tables, megabytes for the biggest.</summary>
    sealed class Worker : IDisposable
    {
        Psarc? open;
        public byte[] Buffer = [];

        public Psarc Archive(string path)
        {
            if (open?.Path != path) { open?.Dispose(); open = null; open = new Psarc(path); }
            return open;
        }

        public void Dispose() => open?.Dispose();
    }

    public readonly record struct Record(int Shader, int ShaderSize, int Rs, int RsSize);

    /// <summary>Every well-formed ndshader record in the first <paramref name="n"/> bytes: its shader container (DXBC/DXIL
    /// with a program) and its root-signature container (RsSize 0: none).</summary>
    public static IEnumerable<Record> Records(byte[] b, int n)
    {
        for (var i = 0; i < n && b.AsSpan(i, n - i).IndexOf("ndshader"u8) is var k and >= 0;)
        {
            var at = i + k;
            i = at + 1;
            if (at + HeaderSize > n || b[at + 12] != HeaderSize) continue;
            int size = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at + 32)), rsSize = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at + 36));
            int s = at + HeaderSize, r = s + size;
            if (size <= 0 || rsSize < 0 || (long)s + size + rsSize > n) continue;
            if (!Dxbc.Valid(b.AsSpan(s, size)) || Dxbc.Kind(b.AsSpan(s, size)) < 0) continue;
            if (rsSize > 0 && !Dxbc.RootSignatureValid(b.AsSpan(r, rsSize))) rsSize = 0;
            yield return new Record(s, size, r, rsSize);
            i = r + rsSize;
        }
    }

    /// <summary>Entries that hold records: packages (*.pak; but texture dictionaries, measured 45 GB of the game's 113 GB of
    /// packages without a shader) and the engine's single-shader files (*.cxo, .pxo, .vxo, .gxo; their names end "xo").</summary>
    static bool Scanned(string name) =>
        Package(name) && !name.Contains("texturedict", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(name) is { Length: 4 } x && x.EndsWith("xo", StringComparison.OrdinalIgnoreCase);

    static bool Package(string name) => name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);

    static IEnumerable<FileInfo> Archives(Game game)
    {
        var dir = Path.Combine(game.InstallDir, Build);
        return Directory.Exists(dir)
            ? new DirectoryInfo(dir).EnumerateFiles("*.psarc", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true })
            : [];
    }
}
