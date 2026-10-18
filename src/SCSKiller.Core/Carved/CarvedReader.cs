using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Games;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Carved;

/// <summary>Any engine that ships raw DXBC/DXIL containers in its files (measured: Starfield, Cyberpunk 2077, Resonance,
/// Gears of War: Reloaded, Sea of Thieves). Carve, validate (<see cref="Dxbc"/>), reflect
/// (<see cref="ShaderContainer"/>). No per-game code:
///   - files: shader/PSO-named ones first, whole; the others, sampled (head/middle/tail), only while fewer than
///     <see cref="MinGraphics"/> graphics shaders are found (Index carves a sampled file whole once its sample holds shaders);
///   - maps: one pool per file (pairs by linkage), or, when a file is a list of pipeline records (the containers of one
///     pipeline back to back, records apart: Resonance *.psocache, Gears *.ushaderprecache, SoT *.upsoprecache), one
///     IsPipeline map per record;
///   - root signatures: a shader's RTS0 part, served as the game's RTS0-only container with that part when it ships one
///     (Starfield ships one per root signature), else as the first shader carrying it (CreateRootSignature takes any
///     container with an RTS0 part). Games whose shaders carry them plan without a recording (Version "…+RTS0"), as do
///     those whose pipeline records name them (Dawn, <see cref="DawnStores"/>: each record an IsPipeline map).
/// EngineInfo: Family "Carved", or an engine recognized by its files (<see cref="Fingerprint"/>), so a planning rule can
/// match it; Version "DXIL" / "DXBC" / "DXIL+DXBC" [+ "+RTS0"].</summary>
public sealed class CarvedReader : IEngineReader
{
    public const string Family = "Carved", EmbeddedRootSignatures = "+RTS0", Platform = "D3D12";
    public const string RedEngine4 = "REDengine 4", Northlight2 = "Northlight 2";

    public static readonly string[] Families = [Family, RedEngine4, Northlight2];

    /// <summary>The engine a carved game is built with, by a file only it ships: REDengine 4 (Cyberpunk 2077)
    /// engine\shader_final.cache; Northlight 2 (Alan Wake 2, CONTROL Resonant) .rmdtoc archive tables. Else <see cref="Family"/>.</summary>
    public static string Fingerprint(string installDir) =>
        File.Exists(Path.Combine(installDir, "engine", "shader_final.cache")) ? RedEngine4
        : IsNorthlight2(installDir) ? Northlight2 : Family;

    internal static bool IsNorthlight2(string installDir)
    {
        try { return Directory.EnumerateFiles(installDir, "*.rmdtoc", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, AttributesToSkip = 0 }).Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Fewer raw graphics shaders than this: not how the game stores its shaders (Unity and UE ship a few raw compute
    /// or utility shaders next to packed ones; measured 0-472, the carver games 2.8k-121k).</summary>
    public const int MinGraphics = 256;

    const long DetectBudget = 512L << 20, IndexSampleBudget = 4L << 30;
    const int NamedWindow = 16 << 20, OtherWindow = 4 << 20;
    internal const int Block = 8 << 20;
    const int MaxRecordGap = 32;   // bytes between two containers of one pipeline record (measured: 0 Gears, 8 SoT, 24 Resonance; packs 16+ or 64+)

    public sealed record Loc(string Path, long Offset, int Size);

    /// <summary>Where each container was found (by SHA-1), per game id, from the last Index: ReadShaders re-slices it.
    /// ponytail: in memory only; persist beside the index if ReadShaders ever runs without an Index in the same process
    /// (it re-indexes then).</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    public int ReaderVersion(EngineInfo engine) => engine.Family == Family && engine.Version.EndsWith(EmbeddedRootSignatures) ? 2 : 1;

    public EngineInfo? Detect(Game game) => Detect(game, out _);

    /// <param name="notes">what was found where (for diagnostics)</param>
    public EngineInfo? Detect(Game game, out string notes)
    {
        notes = "";
        if (!Directory.Exists(game.InstallDir)) return null;
        if (GodotApi(game.ExePath) is { } godot) return new EngineInfo(Fingerprint(game.InstallDir), "-", null, godot, false, $"runs on {godot} (Godot)");
        var seen = new HashSet<string>();
        var perFile = new Dictionary<string, int>();
        int graphics = 0, compute = 0, dxil = 0, rts0 = 0;
        var read = Walk(game.InstallDir, detect: true, (f, _, c) =>
        {
            var kind = Dxbc.Kind(c);
            if (kind < 0 || !seen.Add(Convert.ToHexStringLower(SHA1.HashData(c)))) return;
            perFile[f.FullName] = perFile.GetValueOrDefault(f.FullName) + 1;
            if (Dxbc.IsGraphics(kind)) graphics++;
            else if (kind == 5) compute++;
            if (!Dxbc.Part(c, "DXIL"u8).IsEmpty) dxil++;
            if (!Dxbc.Part(c, "RTS0"u8).IsEmpty) rts0++;
        }, () => graphics >= MinGraphics);
        var programs = seen.Count;
        notes = $"{read / 1e6:F0} MB read; " + string.Join("; ", perFile.OrderByDescending(p => p.Value).Take(3)
            .Select(p => $"{Path.GetRelativePath(game.InstallDir, p.Key)}: {p.Value}"));
        var version = (dxil == programs ? "DXIL" : dxil == 0 ? "DXBC" : "DXIL+DXBC") + (programs > 0 && (rts0 >= 0.9 * programs || DawnStores.Read(game.InstallDir) != null) ? EmbeddedRootSignatures : "");
        const string packed = "compressed or packed: needs an engine reader";
        var unsupported = graphics >= MinGraphics ? null
            : programs == 0 ? $"no raw DXBC/DXIL shaders in its files ({read / 1e9:F1} GB sampled): shaders are {packed}"
            : graphics == 0 ? $"only {compute} raw compute shaders: graphics shaders are {packed}"
            : $"only {graphics} raw graphics shaders: the rest are {packed}";
        return new EngineInfo(Fingerprint(game.InstallDir), programs == 0 ? "-" : version, null, GraphicsApi(game, dxil > 0), false, unsupported);
    }

    /// <summary>A Godot export's renderer by its pack's Godot major version: 4 renders on Vulkan by default, 3 on OpenGL. The pack
    /// is &lt;exe&gt;.pck beside the exe, or embedded, which then ends the exe with its size (8 bytes) and "GDPC". Null: not Godot.</summary>
    internal static string? GodotApi(string exe)
    {
        try
        {
            var pck = Path.ChangeExtension(exe, ".pck");
            var embedded = !File.Exists(pck);
            using var f = File.OpenRead(embedded ? exe : pck);
            Span<byte> b = stackalloc byte[12];
            long at = 0;
            if (embedded)
            {
                if (f.Length < 24) return null;
                f.Position = f.Length - 12;
                f.ReadExactly(b);
                if (!b[8..].SequenceEqual("GDPC"u8)) return null;
                at = f.Length - 12 - System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(b);
                if (at < 0) return null;
            }
            f.Position = at;
            f.ReadExactly(b);   // "GDPC", pack format, Godot's major version
            return !b[..4].SequenceEqual("GDPC"u8) ? null : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b[8..]) >= 4 ? "Vulkan" : "OpenGL";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var locs = new Dictionary<string, Loc>();
        var shaders = new Dictionary<string, ShaderInfo>();
        var rootSigs = new RootSigCarriers();
        var perFile = new Dictionary<string, List<(long Off, int Size, int Kind, string Sha)>>();
        var platformOf = new Dictionary<string, string>(); // shaders a 32-lane GPU can't run (see LanePlatform)
        int graphics = 0, bad = 0;
        var read = Walk(game.InstallDir, detect: false, (f, off, c) =>
        {
            ct.ThrowIfCancellationRequested();
            var sha = Convert.ToHexStringLower(SHA1.HashData(c));
            var kind = Dxbc.Kind(c);
            if (!perFile.TryGetValue(f.FullName, out var seq)) perFile[f.FullName] = seq = [];
            seq.Add((off, c.Length, kind, sha));
            if (!locs.TryAdd(sha, new Loc(f.FullName, off, c.Length))) return; // same bytes seen before
            rootSigs.See(c, sha, kind);
            if (kind < 0) return;
            try
            {
                if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is not { } info) return;
                shaders[sha] = info with { Counts = Counts(info.Bindings) };
                if (Dxbc.IsGraphics(kind)) graphics++;
                if (LanePlatform(Dxbc.WaveLanes(c)) is { } lanes) platformOf[sha] = lanes;
            }
            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { bad++; } // valid container, odd program: not usable
        }, () => graphics >= MinGraphics, ct);

        rootSigs.Apply(shaders);
        var dawn = DawnStores.Read(game.InstallDir);
        var dawnFile = dawn == null ? null : perFile.Keys.FirstOrDefault(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(dawn.ShaderFile), StringComparison.OrdinalIgnoreCase));
        var dawnNote = "";
        var dawnRuns = dawnFile == null ? null : DawnRuns(dawn!, perFile[dawnFile], shaders, locs, out dawnNote);

        var maps = new List<ShaderMap>();
        var mapHashes = new HashSet<string>();
        int records = 0;
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        void Stamp(string path)
        {
            var f = new FileInfo(path);
            content.AppendData(Encoding.UTF8.GetBytes($"{Path.GetRelativePath(game.InstallDir, path)}|{f.Length}|{f.LastWriteTimeUtc.Ticks}\n"));
        }
        foreach (var (path, seq) in perFile)
        {
            var rel = Path.GetRelativePath(game.InstallDir, path);
            Stamp(path);
            if ((path == dawnFile ? dawnRuns : Records(seq)?.Select(r => (Run: r, Root: (string?)null)).ToList()) is { } runs)
                foreach (var (run, root) in runs)
                {
                    records++;
                    var h = Sha1Hex(string.Join(',', run.Order(StringComparer.Ordinal)));
                    if (mapHashes.Add($"{h}|{root}")) maps.Add(new ShaderMap(h, rel, run.Select(PlatformOf).FirstOrDefault(p => p != Platform, Platform), run, IsPipeline: true, root));
                }
            else
                foreach (var pool in seq.Where(s => shaders.ContainsKey(s.Sha)).Select(s => s.Sha).Distinct().GroupBy(PlatformOf))
                    maps.Add(new ShaderMap(Sha1Hex(pool.Key == Platform ? rel : $"{rel}|{pool.Key}"), rel, pool.Key, pool.ToList()));
        }
        if (dawnRuns != null) { Stamp(dawn!.RootFile); Stamp(dawn.PsoFile); }
        string PlatformOf(string sha) => platformOf.GetValueOrDefault(sha, Platform);
        located[game.Id] = locs;

        var rs = shaders.Values.Select(s => s.RootSignature).OfType<string>().Distinct().Count();
        log?.Report($"{perFile.Count} files with shaders, {read / 1e9:F2} GB read: {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"){(bad > 0 ? $", {bad} unparseable" : "")}, {rs} embedded root signatures, {records} pipeline records -> {maps.Count} maps ({maps.Count(m => m.IsPipeline)} exact)"
            + string.Concat(platformOf.Values.GroupBy(p => p).Select(g => $"; {g.Count()} need {g.Key}, not planned for 32-lane GPUs")) + dawnNote + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        var roots = new Dictionary<string, byte[]>(rootSigs.Blobs);
        if (dawnRuns != null) foreach (var r in dawn!.Roots.Values) roots.TryAdd(r.Sha, r.Blob);
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), [Platform, .. platformOf.Values.Distinct().Order(StringComparer.Ordinal)], shaders, maps, roots);
    }

    /// <summary>Re-slices each container at the (file, offset) the index found it; one whose bytes changed since (game
    /// patched) is skipped: it fails at replay and is counted there.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => (Sha: s, At: locs[s])).GroupBy(x => x.At.Path))
        {
            using var h = File.OpenHandle(file.Key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (var (sha, at) in file.OrderBy(x => x.At.Offset))
            {
                ct.ThrowIfCancellationRequested();
                var b = new byte[at.Size];
                if (RandomAccess.Read(h, b, at.Offset) == b.Length && Convert.ToHexStringLower(SHA1.HashData(b)) == sha) sink(sha, b);
            }
        }
    }

    /// <summary>Dawn's pipeline records (<see cref="DawnStores"/>) over the shaders carved from its rawshader.store2: each
    /// record whose shaders were all indexed, as a run with the root signature it names. A shader gets the root signature of
    /// its records, none when they use several. The root-signature containers are located for ReadShaders (the carver stops
    /// before rawroot.store2).</summary>
    static List<(List<string> Run, string? Root)> DawnRuns(DawnStores.Stores dawn, List<(long Off, int Size, int Kind, string Sha)> seq,
        Dictionary<string, ShaderInfo> shaders, Dictionary<string, Loc> locs, out string note)
    {
        var shaAt = new Dictionary<long, string>();
        foreach (var s in seq) shaAt.TryAdd(s.Off, s.Sha);
        var runs = new List<(List<string> Run, string? Root)>();
        var rootsOf = new Dictionary<string, HashSet<string>>();
        var unindexed = 0;
        foreach (var (root, ids) in dawn.Pipelines)
        {
            var run = ids.Select(id => shaAt.GetValueOrDefault(dawn.Shaders[id])).OfType<string>().Where(shaders.ContainsKey).ToList();
            if (run.Count < ids.Length) { unindexed++; continue; }
            foreach (var h in run) (rootsOf.TryGetValue(h, out var set) ? set : rootsOf[h] = []).Add(dawn.Roots[root].Sha);
            runs.Add((run, dawn.Roots[root].Sha));
        }
        foreach (var (h, set) in rootsOf) shaders[h] = shaders[h] with { RootSignature = set.Count == 1 ? set.First() : null };
        foreach (var r in dawn.Roots.Values) locs.TryAdd(r.Sha, new Loc(dawn.RootFile, r.Offset, r.Size));
        var several = rootsOf.Count(r => r.Value.Count > 1);
        note = $"; Dawn: {runs.Count} pipeline records, {rootsOf.Count - several} shaders given their root signature"
            + (several > 0 ? $", {several} used with several left without: their pipelines take their record's" : "")
            + (unindexed + dawn.Skipped > 0 ? $"; left out: {unindexed} records with a shader not indexed, {dawn.Skipped} naming another stage" : "");
        return runs;
    }

    /// <summary>Runs of containers stored back to back (at most <see cref="MaxRecordGap"/> bytes apart; root-signature-only
    /// containers don't count as stages). The file is a list of pipeline records when every run is one pipeline (distinct
    /// graphics stages including a VS or MS, or a lone CS) and some run has two stages: returns each run's shaders. Else
    /// null: a shader pack (measured: Starfield, Cyberpunk and Resonance's packs all fail the test).</summary>
    public static List<List<string>>? Records(IReadOnlyList<(long Off, int Size, int Kind, string Sha)> seq)
    {
        var runs = new List<List<(int Kind, string Sha)>>();
        var end = long.MinValue;
        foreach (var s in seq)
        {
            if (runs.Count == 0 || s.Off - end > MaxRecordGap) runs.Add([]);
            end = s.Off + s.Size;
            if (s.Kind >= 0) runs[^1].Add((s.Kind, s.Sha));
        }
        runs.RemoveAll(r => r.Count == 0);
        static bool Pipeline(List<(int Kind, string Sha)> r) => r.DistinctBy(x => x.Kind).Count() == r.Count
            && (r.All(x => Dxbc.IsGraphics(x.Kind)) && r.Any(x => x.Kind is 1 or 13) || r is [(5, _)]);
        return runs.Count > 0 && runs.All(Pipeline) && runs.Any(r => r.Count > 1) ? runs.Select(r => r.Select(x => x.Sha).ToList()).ToList() : null;
    }

    delegate void Found(FileInfo file, long offset, ReadOnlySpan<byte> container);
    internal delegate void Hit(long offset, ReadOnlySpan<byte> container);

    static readonly HashSet<string> NotShaders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".bk2", ".bik", ".usm", ".mp4", ".webm", ".wem", ".bnk", ".ogg", ".wav", ".mp3", ".fsb", ".xwm", ".awc",
        ".mkv", ".avi", ".wmv", ".flac", ".opus", ".png", ".jpg", ".dds", ".ttf", ".otf",
    };

    /// <summary>Shader/PSO-named files (the speed hint: every carver game measured names its shader files so), then the
    /// rest, smallest first (shader packs are rarely a game's biggest files).</summary>
    static (List<FileInfo> Named, List<FileInfo> Other) Files(string dir)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0, MaxRecursionDepth = 8 };
        var all = new DirectoryInfo(dir).EnumerateFiles("*", opts).Where(f => f.Length >= 64 && !NotShaders.Contains(f.Extension)).ToList();
        bool Named(FileInfo f) => Path.GetRelativePath(dir, f.FullName) is var rel
            && (rel.Contains("shader", StringComparison.OrdinalIgnoreCase) || rel.Contains("pso", StringComparison.OrdinalIgnoreCase));
        return (all.Where(Named).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).ToList(),
            all.Where(f => !Named(f) && f.Length >= 64 << 10).OrderBy(f => f.Length).ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Carves the install; returns bytes read. Named files: whole (Detect: sampled). Other files only while
    /// <paramref name="enough"/> is false after the named ones: sampled; Index carves a file whole when its sample holds
    /// 16+ containers. Detect stops once <paramref name="enough"/>. Unreadable files are skipped.
    /// ponytail: budget-bounded sampling of unnamed files (512 MB Detect, 4 GB Index); an engine that hides a small shader
    /// pack among many big unnamed files can be missed. Add a speed-hint glob for it if one ever is.</summary>
    static long Walk(string dir, bool detect, Found found, Func<bool> enough, CancellationToken ct = default)
    {
        var (named, other) = Files(dir);
        var buf = new byte[Block];
        long read = 0;
        foreach (var f in named)
        {
            if (detect && (enough() || read >= DetectBudget)) return read;
            read += Scan(f, buf, detect ? Windows(f.Length, NamedWindow) : [(0, f.Length)], (o, c) => found(f, o, c), ct);
        }
        if (enough()) return read;
        foreach (var f in other)
        {
            if (read >= (detect ? DetectBudget : IndexSampleBudget) || detect && enough()) break;
            var w = Windows(f.Length, OtherWindow);
            if (detect || w.Length == 1) { read += Scan(f, buf, w, (o, c) => found(f, o, c), ct); continue; }
            var hits = 0;
            read += Scan(f, buf, w, (_, _) => hits++, ct);
            if (hits >= 16) read += Scan(f, buf, [(0, f.Length)], (o, c) => found(f, o, c), ct);
        }
        return read;
    }

    static (long Lo, long Hi)[] Windows(long len, long w) =>
        len <= 3 * w ? [(0, len)] : [(0, w), (len / 2 - w / 2, len / 2 + w / 2), (len - w, len)];

    internal static long Scan(FileInfo f, byte[] buf, (long Lo, long Hi)[] windows, Hit hit, CancellationToken ct, int maxSize = Dxbc.MaxSize)
    {
        try
        {
            using var h = File.OpenHandle(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
            var len = RandomAccess.GetLength(h);
            return windows.Sum(w => Carve(h, len, w.Lo, Math.Min(w.Hi, len), buf, hit, ct, maxSize));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Every valid container starting in [lo, hi) of the file (it may end past hi), in file order.</summary>
    static long Carve(SafeFileHandle h, long len, long lo, long hi, byte[] buf, Hit hit, CancellationToken ct, int maxSize)
    {
        long read = 0;
        Span<byte> head = stackalloc byte[32];
        for (var pos = lo; pos < hi;)
        {
            ct.ThrowIfCancellationRequested();
            var n = RandomAccess.Read(h, buf.AsSpan(0, (int)Math.Min(buf.Length, hi - pos)), pos);
            if (n <= 0) break;
            read += n;
            var span = buf.AsSpan(0, n);
            var next = pos + n < hi && n > 3 ? pos + n - 3 : pos + n; // a "DXBC" cut by the block end is seen whole next time
            for (var i = 0; span[i..].IndexOf("DXBC"u8) is var k and >= 0;)
            {
                var at = i + k;
                var off = pos + at;
                i = at + 1;
                ReadOnlySpan<byte> hd = at + 32 <= n ? span.Slice(at, 32) : RandomAccess.Read(h, head, off) == 32 ? head : default;
                var size = Dxbc.HeaderSize(hd);
                if (size == 0 || size > maxSize || off + size > len) continue;
                ReadOnlySpan<byte> c = at + size <= n ? span.Slice(at, size) : ReadAt(h, off, size);
                if (!Dxbc.Valid(c)) continue;
                hit(off, c);
                i = Math.Min(at + size, n);
                next = Math.Max(next, off + size);
            }
            pos = next;
        }
        return read;
    }

    static byte[] ReadAt(SafeFileHandle h, long off, int size)
    {
        var b = new byte[size];
        return RandomAccess.Read(h, b, off) == size ? b : [];
    }

    /// <summary>Shaders with a [WaveSize] that excludes 32 lanes (Starfield ships wave64 twins of 302 compute shaders for AMD)
    /// go to their own platform ("D3D12 wave64"), which the planner picks only on AMD (32 and 64 lanes) when its range takes 64:
    /// NVIDIA runs 32 lanes only (the D3D12 runtime rejects them, measured). ponytail: lane ranges by vendor profile; pick
    /// platforms by the GPU's lane range (proposed VendorCaps field) when other vendors warm.</summary>
    internal static string? LanePlatform((uint Min, uint Max)? lanes) =>
        lanes is { } l && (l.Min > 32 || l.Max < 32) ? $"{Platform} wave{l.Min}{(l.Max != l.Min ? $"-{l.Max}" : "")}" : null;

    /// <summary>Per-class binding counts (the planner's learned lookup keys on them; unbounded ranges count once).</summary>
    /// <summary>Registers per class (an unbounded range counts one). ArgumentException when a class's total isn't an int:
    /// a malformed shader, which every reader skips as unparseable.</summary>
    internal static ResourceCounts Counts(IReadOnlyList<Binding> b)
    {
        int N(string cls) => b.Where(x => x.Class == cls).Sum(x => (long)Math.Max(1, x.Count)) is var n and <= int.MaxValue ? (int)n
            : throw new ArgumentException($"{cls} registers overflow");
        return new ResourceCounts(N("cbv"), N("srv"), N("uav"), N("sampler"));
    }

    internal static string Sha1Hex(string s) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>D3D12 when the game ships DXIL (SM6 runs on DX12 only), the Agility SDK runtime (D3D12Core.dll) or its own
    /// binaries import d3d12.dll / export D3D12SDKVersion; "D3D11 or D3D12" when they import d3d11.dll too (and no DXIL);
    /// D3D11 / Vulkan when that's all they import; "D3D11 or D3D12" when nothing is readable.</summary>
    static string GraphicsApi(Game game, bool dxil)
    {
        var apis = Imports(game);
        var core = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true, AttributesToSkip = 0 };
        bool d3d12 = dxil || apis.Contains("D3D12") || Directory.EnumerateFiles(game.InstallDir, "D3D12Core.dll", core).Any();
        bool d3d11 = apis.Contains("D3D11");
        return d3d12 ? (d3d11 && !dxil ? UnrealRhi.Ambiguous : "D3D12") : d3d11 ? "D3D11" : apis.Contains("Vulkan") ? "Vulkan" : UnrealRhi.Ambiguous;
    }

    static readonly Dictionary<string, string> ApiDlls = new(StringComparer.OrdinalIgnoreCase)
        { ["d3d12.dll"] = "D3D12", ["d3d11.dll"] = "D3D11", ["vulkan-1.dll"] = "Vulkan" };

    /// <summary>Middleware that imports a graphics API without being the renderer.</summary>
    static readonly string[] Middleware = ["eossdk", "libxess", "dstorage", "gfsdk", "nvngx", "sl.", "amd_", "ffx", "nvlowlatency", "galaxy",
        "steam_api", "discord", "overlay", "igxess", "renderdoc", "reshade", "dxgi", "d3d1", "d3d9", "opengl32", "vulkan-1", "libegl", "libglesv2",
        "vk_swiftshader", "d3dcompiler", "dxcompiler", "dxil", "cef", "nvapi", "nvtt", "physx", "bink", "xess", "fsr", "dlss", "streamline",
        "gameoverlay", "twitch", "nvrtx", "nri", "nrd", "nrc", "ags", "libxell", "pix", "winpix", "aftermath", "gfnruntime"];

    /// <summary>APIs imported by the exe and the non-middleware DLLs next to it (+ "D3D12" for an exported D3D12SDKVersion).
    /// Empty when unreadable (Xbox/GDK exes are ACL-protected) or the game has anti-cheat (like UnrealReader, we don't open
    /// those binaries).</summary>
    static HashSet<string> Imports(Game game)
    {
        var apis = new HashSet<string>();
        if (Path.GetDirectoryName(game.ExePath) is not { } dir || !Directory.Exists(dir) || GameFiles.DetectAntiCheat(game) != AntiCheat.None) return apis;
        var bins = Directory.EnumerateFiles(dir, "*.dll").Where(p => !Middleware.Any(m => Path.GetFileName(p).StartsWith(m, StringComparison.OrdinalIgnoreCase)));
        foreach (var p in bins.Prepend(game.ExePath))
            try
            {
                var dlls = PeImports(p, out var agility);
                foreach (var dll in dlls) if (ApiDlls.TryGetValue(dll, out var api)) apis.Add(api);
                if (agility) apis.Add("D3D12");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) { }
        return apis;
    }

    /// <summary>Import + delay-import DLL names (<paramref name="delayed"/> false: imports only); <paramref name="agility"/>: exports D3D12SDKVersion.</summary>
    public static List<string> PeImports(string path, out bool agility, bool delayed = true)
    {
        using var pe = PeFile.Open(path);
        var dlls = PeImports(pe, delayed);
        agility = PeFile.ExportNames(pe).Take(20000).Contains("D3D12SDKVersion");
        return dlls;
    }

    public static List<string> PeImports(PEReader pe, bool delayed)
    {
        var h = pe.PEHeaders.PEHeader ?? throw new BadImageFormatException("no optional header");
        var dlls = new List<string>();
        void Table(DirectoryEntry d, int stride, int nameAt, bool delay)
        {
            if (d.RelativeVirtualAddress == 0) return;
            var r = pe.GetSectionData(d.RelativeVirtualAddress).GetReader();
            while (r.RemainingBytes >= stride)
            {
                var e = r.ReadBytes(stride);
                long name = BitConverter.ToUInt32(e, nameAt);
                if (name == 0) break;
                if (delay && (e[0] & 1) == 0) name -= (long)h.ImageBase; // old-style delay descriptor: a VA
                dlls.Add(PeFile.Str(pe, (int)name));
            }
        }
        Table(h.ImportTableDirectory, 20, 12, false);
        if (delayed) Table(h.DelayImportTableDirectory, 32, 4, true);
        return dlls;
    }
}

/// <summary>Which container serves each shader's root signature (its RTS0 part): the game's RTS0-only container with
/// that part when it ships one, else the first shader carrying it.</summary>
sealed class RootSigCarriers
{
    readonly Dictionary<string, string> rsOnly = [], carrier = [], partOf = [];
    readonly Dictionary<string, byte[]> parts = [], blobs = [];

    /// <summary>Each container that can serve a root signature (every RTS0-only one, the first shader per part), by its
    /// SHA-1: its RTS0 part (<see cref="ShaderIndex.RootSignatures"/>).</summary>
    public IReadOnlyDictionary<string, byte[]> Blobs => blobs;

    /// <param name="kind"><see cref="Dxbc.Kind"/> of the container</param>
    public void See(ReadOnlySpan<byte> c, string sha, int kind)
    {
        if (Dxbc.Part(c, "RTS0"u8) is not { IsEmpty: false } rts) return;
        var part = Convert.ToHexStringLower(SHA1.HashData(rts));
        if (!parts.TryGetValue(part, out var bytes)) parts[part] = bytes = rts.ToArray();
        if (kind < 0) { rsOnly.TryAdd(part, sha); blobs[sha] = bytes; }
        else
        {
            if (carrier.TryAdd(part, sha)) blobs[sha] = bytes;
            partOf[sha] = part;
        }
    }

    public void Apply(Dictionary<string, ShaderInfo> shaders)
    {
        foreach (var (sha, part) in partOf)
            if (shaders.TryGetValue(sha, out var info)) shaders[sha] = info with { RootSignature = rsOnly.GetValueOrDefault(part) ?? carrier[part] };
    }
}
