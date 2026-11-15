using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Frostbite;

/// <summary>EA's Frostbite (Mass Effect: Andromeda, Need for Speed, Battlefield, Dead Space). Recognized by Data\layout.toc
/// beside cas_NN.cas archives. Shaders are DXBC (D3D11) or DXIL (D3D12) containers inside the archives' compressed entries:
/// every archive's blocks are decoded (<see cref="Cas"/>) and the containers carved from the decoded stream, so no
/// generation's catalogs are read. One pool per archive entry. The containers carry no root signature: a D3D12 game's
/// plan needs a recording, whose shaders the index then supplies by hash.
/// An install where Index finds none keeps the recorded-only path: the failed Index leaves a marker for these layout files
/// (<see cref="Fallback"/>), and Detect then gives an engine the planner treats as recorded only, until they change.
/// EngineInfo: Family "Frostbite", Version "CAS".</summary>
public sealed class FrostbiteReader(string? dataDir = null) : IEngineReader
{
    public const string Family = "Frostbite", Version = "CAS";
    const string Layout = @"Data\layout.toc";
    const long DetectBudget = 32L << 20;
    const int MaxContainer = 16 << 20;   // Dxbc.MaxSize is 64 MB: a stray "DXBC" in the stream would hold that much
    const int MaxPerArchive = 1 << 20;   // distinct containers an archive's pools may name: decoded data can repeat tiny ones without end

    /// <param name="Block">the file offset of the header of the block the container starts in</param>
    /// <param name="Offset">where it starts in the decoded stream from that block</param>
    public sealed record Loc(string Path, long Block, int Offset, int Size);

    /// <summary>Where each container is, per game id, from the last Index. ponytail: in memory only, like the carver's;
    /// ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    readonly AppStore store = new(dataDir ?? AppStore.DefaultDir);
    const string FallbackFile = "frostbite-fallback.txt";
    public const string FallbackReason = "no shader found in its archives: " + CarvedReader.Packed;
    public const string Unreadable = "archives that don't read: " + CarvedReader.Packed;

    static readonly EnumerationOptions Deep = new() { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    /// <summary>The archives, when the install has Data\layout.toc.</summary>
    internal static List<string> Archives(string installDir) =>
        Directory.Exists(installDir) && File.Exists(GameFiles.Find(installDir, Layout))
            ? [.. GameFiles.EnumerateFiles(installDir, "cas_*.cas", Deep).Order(StringComparer.OrdinalIgnoreCase)] : [];

    (string Path, string Stamp) Fallback(Game game) =>
        (Path.Combine(store.GameDir(game.Id), FallbackFile), string.Join('|', GameFiles.EnumerateFiles(game.InstallDir, "layout.toc", Deep)
            .Order(StringComparer.OrdinalIgnoreCase).Select(f => new FileInfo(f)).Select(i => $"{GameFiles.Rel(game.InstallDir, i.FullName)}:{i.Length}:{i.LastWriteTimeUtc.Ticks}")));

    bool FellBack(Game game)
    {
        var (path, stamp) = Fallback(game);
        try { return File.Exists(path) && File.ReadAllText(path) == stamp; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public string DetectStamp(Game game, EngineInfo? engine) => FellBack(game) ? "frostbite-fallback" : "";

    public EngineInfo? Detect(Game game)
    {
        var archives = Archives(game.InstallDir);
        if (archives.Count == 0) return null;
        if (FellBack(game)) return new(Family, Version, null, CarvedReader.Api(game), false, FallbackReason);
        // the start of the smallest archives decodes: the codecs are ones this reader has
        bool decoded = false, dxil = false;
        foreach (var f in archives.Select(f => new FileInfo(f)).Where(f => f.Length > 0).OrderBy(f => f.Length).Take(3))
        {
            try
            {
                using var h = File.OpenHandle(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var carver = new Carver(f.FullName, (c, _, _) => dxil |= !Dxbc.Part(c, "DXIL"u8).IsEmpty);
                Cas.Walk(h, 0, (at, raw, broken) => { decoded = true; carver.Add(at, raw, broken); return true; }, CancellationToken.None, DetectBudget);
                carver.Flush();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return new(Family, Version, null, dxil ? "D3D12" : CarvedReader.Api(game), false, decoded ? null : Unreadable);
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => Index(game, engine, log, null, ct);

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        if (CarvedReader.RecordedOnly(engine)) return Empty;
        try { return Read(game, log, progress, ct); }
        catch (InvalidDataException e)
        {
            var (path, stamp) = Fallback(game);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, stamp);
            log?.Report($"{game.Name}: {e.Message}: planned from its recording alone");
            return Empty;
        }
    }

    static ShaderIndex Empty => new(ShaderIndex.EmptyContentHash, [CarvedReader.Platform], new Dictionary<string, ShaderInfo>(), []);

    sealed record Found(string Sha, Loc At, long Entry, ShaderInfo? Info, bool Bad);

    ShaderIndex Read(Game game, IProgress<string>? log, IProgress<(int, int)>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var archives = Archives(game.InstallDir);
        var perFile = new List<Found>[archives.Count];
        long read = 0, breaks = 0;
        int done = 0, over = 0;
        Parallel.For(0, archives.Count, ReaderLoops.Options(ct), i =>
        {
            var list = perFile[i] = [];
            var path = archives[i];
            var seen = new HashSet<string>();
            var pooled = new HashSet<(long, string)>();
            var carver = new Carver(path, (c, at, entry) =>
            {
                var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                if (!pooled.Add((entry, sha)) || pooled.Count > MaxPerArchive) return;
                if (!seen.Add(sha))
                {
                    list.Add(new(sha, at, entry, null, false));   // its pool's member, parsed where first seen
                    return;
                }
                ShaderInfo? info = null;
                var bad = false;
                if (Dxbc.Kind(c) >= 0)
                    try { info = ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } s ? s with { Counts = CarvedReader.Counts(s.Bindings), RootSignature = null } : null; }
                    catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { bad = true; }   // valid container, odd program: not usable
                list.Add(new(sha, at, entry, info, bad));
            });
            long n = 0, b = 0;
            try
            {
                using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
                n = Cas.Walk(h, 0, (at, raw, broken) => { b += broken ? 1 : 0; carver.Add(at, raw, broken); return pooled.Count <= MaxPerArchive; }, ct);
                carver.Flush();
                if (pooled.Count > MaxPerArchive) Interlocked.Increment(ref over);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { list.Clear(); }   // gone or held: left out, the content hash says so
            Interlocked.Add(ref read, n);
            Interlocked.Add(ref breaks, b);
            progress?.Report((Interlocked.Increment(ref done), archives.Count));
        });

        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var maps = new List<ShaderMap>();
        int containers = 0, bad = 0;
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        for (var i = 0; i < archives.Count; i++)
        {
            if (perFile[i].Count == 0) continue;
            var rel = GameFiles.Rel(game.InstallDir, archives[i]);
            content.AppendData(Encoding.UTF8.GetBytes(ContentStamps.Of(rel, archives[i])));
            foreach (var entry in perFile[i].GroupBy(f => f.Entry))
            {
                foreach (var f in entry)
                {
                    containers++;
                    if (!locs.TryAdd(f.Sha, f.At)) continue;
                    if (f.Info != null) shaders[f.Sha] = f.Info;
                    bad += f.Bad ? 1 : 0;
                }
                var lib = FormattableString.Invariant($"{rel}@{entry.Key}");
                if (entry.Select(f => f.Sha).Where(shaders.ContainsKey).Distinct().ToList() is { Count: > 0 } pool)
                    maps.Add(new ShaderMap(CarvedReader.Sha1Hex(lib), lib, CarvedReader.Platform, pool));
            }
        }
        log?.Report(FormattableString.Invariant($"{archives.Count} archives, {read / 1e9:F2} GB walked, {breaks:N0} breaks: {containers:N0} containers -> {shaders.Count:N0} shaders (")
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"){(bad > 0 ? $", {bad} unparseable" : "")}, {maps.Count} pools, no root signatures"
            + (over > 0 ? $"; warning: {over} archives hold over {MaxPerArchive:N0} containers, the rest of each left out" : "") + FormattableString.Invariant($" ({sw.Elapsed.TotalSeconds:F1}s)"));
        if (shaders.Count == 0) throw new InvalidDataException("no shader in its archives");
        located[game.Id] = locs;
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), [CarvedReader.Platform], shaders, maps);
    }

    /// <summary>Decodes the blocks from each requested container's once more; one whose bytes changed since (game patched)
    /// isn't sent and fails at replay.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (CarvedReader.RecordedOnly(engine)) return;
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            if (!located.TryGetValue(game.Id, out locs)) return;
        }
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Path))
            try
            {
                using var h = File.OpenHandle(file.Key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                foreach (var (sha, at) in file.OrderBy(x => x.At.Block))
                    if (Slice(h, at, ct) is { } c && Convert.ToHexStringLower(SHA1.HashData(c)) == sha) sink(sha, c);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The container's bytes, decoded from its first block on; null when the blocks no longer run so.</summary>
    internal static byte[]? Slice(Microsoft.Win32.SafeHandles.SafeFileHandle h, Loc at, CancellationToken ct)
    {
        var c = new byte[at.Size];
        int skip = at.Offset, got = 0;
        var first = true;
        Cas.Walk(h, at.Block, (_, raw, broken) =>
        {
            if (broken && !first) return false;
            first = false;
            var from = Math.Min(skip, raw.Length);
            skip -= from;
            var n = Math.Min(raw.Length - from, c.Length - got);
            raw.Slice(from, n).CopyTo(c.AsSpan(got));
            return (got += n) < c.Length;
        }, ct, resync: false);
        return got == c.Length ? c : null;
    }

    /// <summary>Carves containers out of a decoded block stream. A container may run over several blocks; a break in the
    /// stream drops what was held. An entry is a run of blocks ended by one smaller than the largest seen (Frostbite fills
    /// every block of an entry but its last). ponytail: entries guessed from block sizes, read cas.cat/.toc offsets if
    /// pools need to be exact.</summary>
    internal sealed class Carver(string path, Action<ReadOnlySpan<byte>, Loc, long> found)
    {
        byte[] buf = new byte[1 << 20];
        int len, scan;
        readonly List<(int Start, long At, long Entry)> blocks = [];
        long entry = -1;
        int largest, last;

        bool pending;
        const int MaxHeld = 4096;   // blocks one container may span: 16 MB in 64 KB blocks is 256

        public void Add(long at, ReadOnlySpan<byte> raw, bool broken)
        {
            if (broken)
            {
                Flush();
                (len, scan, last, entry) = (0, 0, 0, -1);
                blocks.Clear();
            }
            if (entry < 0 || last < largest) entry = at;
            largest = Math.Max(largest, raw.Length);
            last = raw.Length;
            if (len + raw.Length > buf.Length) Array.Resize(ref buf, Math.Max(buf.Length * 2, len + raw.Length));
            blocks.Add((len, at, entry));
            raw.CopyTo(buf.AsSpan(len));
            len += raw.Length;
            var keep = Carve();
            Drop(keep);
        }

        /// <summary>The stream ends here (a break, the archive's end): a container still waiting for bytes isn't one, the
        /// bytes after its magic are carved.</summary>
        public void Flush()
        {
            while (pending)
            {
                scan++;
                Carve();
            }
        }

        /// <summary>Carves from <see cref="scan"/>; returns the first offset still needed (a container not yet whole).</summary>
        int Carve()
        {
            pending = false;
            var s = buf.AsSpan(0, len);
            while (s[scan..].IndexOf("DXBC"u8) is var k and >= 0)
            {
                var at = scan + k;
                var size = len - at < 32 ? -1 : Dxbc.HeaderSize(s.Slice(at, 32));
                if (size == 0 || size > MaxContainer) { scan = at + 1; continue; }
                if (size < 0 || len - at < size)
                {
                    if (blocks.Count - BlockAt(at) <= MaxHeld) { pending = true; return scan = at; }
                    scan = at + 1;
                    continue;
                }
                var c = s.Slice(at, size);
                if (!Dxbc.Valid(c)) { scan = at + 1; continue; }
                var b = blocks[BlockAt(at)];
                found(c, new Loc(path, b.At, at - b.Start, size), b.Entry);
                scan = at + size;
            }
            return scan = Math.Max(scan, len - 3);
        }

        /// <summary>The last block starting at or before <paramref name="pos"/> (the blocks are in stream order).</summary>
        int BlockAt(int pos)
        {
            int lo = 0, hi = blocks.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (blocks[mid].Start <= pos) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        void Drop(int keep)
        {
            var first = BlockAt(keep);
            if (first <= 0) return;
            var cut = blocks[first].Start;
            blocks.RemoveRange(0, first);
            for (var i = 0; i < blocks.Count; i++) blocks[i] = blocks[i] with { Start = blocks[i].Start - cut };
            Buffer.BlockCopy(buf, cut, buf, 0, len - cut);
            (len, scan) = (len - cut, scan - cut);
        }
    }
}
