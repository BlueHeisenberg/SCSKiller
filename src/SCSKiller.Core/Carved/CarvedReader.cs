using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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

    /// <summary>Asobo's engine (A Plague Tale: Requiem), a fork of the carved family by its root files, for its root-signature rule.</summary>
    public const string Zouna = "Zouna";

    static string? ForkOf(string installDir) =>
        File.Exists(GameFiles.Find(installDir, "All.psc")) && File.Exists(GameFiles.Find(installDir, "LangDef.tsc")) ? Zouna : null;
    public const string Dx9Reason = "runs on DirectX 9: compiling it waits for DirectX 9 support in SCSKiller";

    /// <summary>The status line of a DirectX 9 game (<see cref="OldApi"/>), null for any other API.</summary>
    public static string? OldDxReason(string api) => api.StartsWith("D3D9", StringComparison.Ordinal) ? Dx9Reason : null;

    /// <summary>A DirectX 10 game's API: the D3D11 compile fills the driver cache entries its SM 4 shaders read (NVIDIA, measured).</summary>
    public const string Dx10 = "D3D11 (runs on DirectX 10)";
    public const string RuntimeShadersReason = "This game builds its shaders while it runs, so there's nothing to compile ahead";

    public static readonly string[] Families = [Family, RedEngine4, Northlight2];

    public const string NoRawShaders = "no raw DXBC/DXIL shaders in its files", Packed = "compressed or packed: needs an engine reader";

    /// <summary>A DirectX 12 game whose files hold none, or fewer than <see cref="MinGraphics"/>, of the shaders the carver
    /// can read, or a Godot game (<see cref="GodotReason"/>): it plans from a recording's own shader bytes alone
    /// (Planner.Check), so its index is empty.</summary>
    public static bool RecordedOnly(EngineInfo? engine) =>
        engine?.Unsupported is { } u && (u.EndsWith(Packed, StringComparison.Ordinal) || u == GodotReason) && engine.GraphicsApi.Contains("D3D12");

    /// <summary>The engine a carved game is built with, by a file only it ships: REDengine 4 (Cyberpunk 2077)
    /// engine\shader_final.cache; Northlight 2 (Alan Wake 2, CONTROL Resonant) .rmdtoc archive tables. Else <see cref="Family"/>.</summary>
    /// <summary>The engine is one this reader found.</summary>
    public static bool Carved(EngineInfo e) => e.Family is Family or RedEngine4 or Northlight2;

    public static string Fingerprint(string installDir) =>
        File.Exists(GameFiles.Find(installDir, @"engine\shader_final.cache")) ? RedEngine4
        : IsNorthlight2(installDir) ? Northlight2 : Family;

    internal static bool IsNorthlight2(string installDir)
    {
        try { return GameFiles.EnumerateFiles(installDir, "*.rmdtoc", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, AttributesToSkip = 0 }).Any(); }
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

    public int ReaderVersion(EngineInfo engine) => engine.Family == Family && engine.Version.EndsWith(EmbeddedRootSignatures) ? 3 : 2;

    public EngineInfo? Detect(Game game) => Detect(game, out _);

    /// <param name="notes">what was found where (for diagnostics)</param>
    public EngineInfo? Detect(Game game, out string notes)
    {
        notes = "";
        if (!Directory.Exists(game.InstallDir)) return null;
        if (GodotApi(game.ExePath, () => UnrealRhi.LaunchOptions(game)) is { } godot) return new EngineInfo(Fingerprint(game.InstallDir), "-", null, godot, false, godot == "OpenGL" ? GodotOpenGl : GodotReason);
        var seen = new HashSet<string>();
        var perFile = new Dictionary<string, int>();
        int graphics = 0, compute = 0, dxil = 0, rts0 = 0;
        var dcs = DcsSaved(game);
        var read = Walk(game.InstallDir, detect: true, (f, _, c) =>
        {
            var kind = Dxbc.Kind(c);
            if (kind < 0 || !seen.Add(Convert.ToHexStringLower(SHA1.HashData(c)))) return;
            perFile[f.FullName] = perFile.GetValueOrDefault(f.FullName) + 1;
            if (Dxbc.IsGraphics(kind)) graphics++;
            else if (kind == 5) compute++;
            if (!Dxbc.Part(c, "DXIL"u8).IsEmpty) dxil++;
            if (!Dxbc.Part(c, "RTS0"u8).IsEmpty) rts0++;
        }, () => graphics >= MinGraphics, also: DcsFiles(game, dcs));
        var programs = seen.Count;
        notes = $"{read / 1e6:F0} MB read; " + string.Join("; ", perFile.OrderByDescending(p => p.Value).Take(3)
            .Select(p => $"{Rel(game, dcs, p.Key)}: {p.Value}"));
        var version = (dxil == programs ? "DXIL" : dxil == 0 ? "DXBC" : "DXIL+DXBC") + (programs > 0 && (rts0 >= 0.9 * programs || DawnStores.Read(game.InstallDir) != null) ? EmbeddedRootSignatures : "");
        var unsupported = graphics >= MinGraphics ? null
            : dcs != null ? "no compiled shaders yet: play DCS once so it builds them"
            : programs == 0 ? $"{NoRawShaders} ({read / 1e9:F1} GB sampled): shaders are {Packed}"
            : graphics == 0 ? $"only {compute} raw compute shaders: graphics shaders are {Packed}"
            : $"only {graphics} raw graphics shaders: the rest are {Packed}";
        var api = GraphicsApi(ApiExe(game), dxil > 0);
        // a DirectX exe that loads vulkan-1.dll too runs either (BeamNG.drive's renderer setting): a recording tells which
        var vulkanToo = api.Contains("D3D1", StringComparison.Ordinal) && NamesApi(game.ExePath, []) == true;
        return new EngineInfo(Fingerprint(game.InstallDir), programs == 0 ? "-" : version, ForkOf(game.InstallDir), api, false, OldDxReason(api) is { } old ? old : api == "OpenGL" ? "runs on OpenGL" : unsupported != null && api == "D3D11" && Fna(game) ? RuntimeShadersReason : unsupported,
            ApiGuessed: vulkanToo, OffersVulkan: vulkanToo);
    }

    /// <summary>A Godot export's renderer: "OpenGL" for Godot 3 and for Godot 4's Compatibility renderer, else Godot 4's rendering
    /// device driver, "D3D12" or "Vulkan" (the engine's default: the editor writes d3d12 into the projects it creates since 4.6).
    /// Settings by precedence: <paramref name="launch"/>'s --rendering-method / --rendering-driver, override.cfg beside the exe,
    /// the pack's override.cfg, its project.binary. The pack is &lt;exe&gt;.pck beside the exe, or embedded, which then ends the
    /// exe with its size (8 bytes) and "GDPC". Null: not Godot.</summary>
    internal static string? GodotApi(string exe, Func<string>? launch = null)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var pck = GameFiles.ChangeExtension(exe, ".pck");
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
            if (!b[..4].SequenceEqual("GDPC"u8)) return null;
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b[8..]) < 4) return "OpenGL";
            try { GodotPackSettings(f, at, settings); }
            catch (Exception e) when (e is IOException or ArgumentException or OverflowException) { }   // unreadable: the defaults
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        try { if (GodotOverride(exe) is { Exists: true, Length: <= 1 << 20 } cfg) GodotCfg(File.ReadAllText(cfg.FullName), settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var options = launch?.Invoke() ?? "";
        string? Arg(string name) => Regex.Match(options, $@"(?:^|\s){name}[\s=]+""?(\w+)") is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;
        string? Setting(string key) => settings.GetValueOrDefault(key + ".windows") ?? settings.GetValueOrDefault(key);
        var method = Arg("--rendering-method") ?? Setting("rendering/renderer/rendering_method");
        var driver = Arg("--rendering-driver") ?? (method == "gl_compatibility" ? "opengl3" : Setting("rendering/rendering_device/driver"));
        return driver?.StartsWith("opengl3", StringComparison.Ordinal) == true ? "OpenGL" : driver == "d3d12" ? "D3D12" : "Vulkan";
    }

    public const string GodotReason = NoRawShaders + ": Godot builds its shaders while it runs", GodotOpenGl = "runs on OpenGL (Godot)";

    static FileInfo? GodotOverride(string exe) => Path.GetDirectoryName(exe) is { Length: > 0 } dir ? new FileInfo(GameFiles.Find(dir, "override.cfg")) : null;

    /// <summary>The rendering settings of the pack's project.binary, then of its override.cfg (file_access_pack.cpp: format 2 has
    /// the directory after the header, 3 at an offset). An encrypted directory or file, or a sparse bundle, is left out.</summary>
    static void GodotPackSettings(FileStream f, long at, Dictionary<string, string> settings)
    {
        using var r = new BinaryReader(f, Encoding.UTF8, leaveOpen: true);
        f.Position = at + 4;
        var format = r.ReadUInt32();
        f.Position += 12;   // Godot major, minor, patch
        var flags = r.ReadUInt32();
        var fileBase = r.ReadInt64();
        if (format is not (2 or 3) || (flags & 0b101) != 0) return;
        if (format == 3 || (flags & 2) != 0) fileBase += at;   // relative file base
        f.Position = format == 3 ? r.ReadInt64() + at : f.Position + 64;
        (long Off, long Size)? project = null, overrides = null;
        for (var n = r.ReadUInt32(); n > 0 && (project == null || overrides == null); n--)
        {
            var len = r.ReadInt32();
            if (len is < 0 or > 4096) return;
            var path = Encoding.UTF8.GetString(r.ReadBytes(len)).TrimEnd('\0');
            var (off, size) = (r.ReadInt64(), r.ReadInt64());
            f.Position += 16;   // MD5
            if ((r.ReadUInt32() & 0b11) != 0 || size is < 0 or > 16 << 20) continue;   // encrypted, removed
            path = path.StartsWith("res://", StringComparison.Ordinal) ? path[6..] : path;
            if (path == "project.binary") project = (off, size);
            else if (path == "override.cfg") overrides = (off, size);
        }
        byte[] Read((long Off, long Size) e) { f.Position = fileBase + e.Off; return r.ReadBytes((int)e.Size); }
        if (project is { } p) GodotBinary(Read(p), settings);
        if (overrides is { } o) GodotCfg(Encoding.UTF8.GetString(Read(o)), settings);
    }

    /// <summary>project.binary: "ECFG", a count, then each setting's u32-length key and u32-length Variant. A String or StringName
    /// Variant is its type (4, 21) in the low byte of a u32, a u32 length and UTF-8 (marshalls.cpp).</summary>
    static void GodotBinary(byte[] b, Dictionary<string, string> settings)
    {
        if (b.Length < 8 || !b.AsSpan(0, 4).SequenceEqual("ECFG"u8)) return;
        var pos = 8;
        int I32(int at) => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at, 4));
        for (var n = I32(4); n > 0 && pos + 8 <= b.Length; n--)
        {
            var key = Encoding.UTF8.GetString(b, pos + 4, I32(pos));
            pos += 4 + I32(pos);
            var len = I32(pos);
            var value = b.AsSpan(pos + 4, len);
            pos += 4 + len;
            if (key.StartsWith("rendering/", StringComparison.Ordinal) && value.Length >= 8 && value[0] is 4 or 21)
                settings[key] = Encoding.UTF8.GetString(value.Slice(8, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(value[4..])));
        }
    }

    /// <summary>An override.cfg: "[section]" lines, then key=value lines, a String value quoted, ";" starting a comment.</summary>
    static void GodotCfg(string text, Dictionary<string, string> settings)
    {
        var section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) section = line[1..^1].Trim();
            else if (line.IndexOf('=') is > 0 and var eq)
                settings[(section.Length > 0 ? section + "/" : "") + line[..eq].Trim()] = line[(eq + 1)..].Trim() is var v && v.StartsWith('"')
                    ? v[1..].Split('"')[0] : v.Split(';')[0].Trim();
        }
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        if (RecordedOnly(engine)) return new ShaderIndex(ShaderIndex.EmptyContentHash, [Platform], new Dictionary<string, ShaderInfo>(), []);
        var sw = Stopwatch.StartNew();
        var locs = new Dictionary<string, Loc>();
        var shaders = new Dictionary<string, ShaderInfo>();
        var rootSigs = new RootSigCarriers();
        var perFile = new Dictionary<string, List<(long Off, int Size, int Kind, string Sha)>>();
        var platformOf = new Dictionary<string, string>(); // shaders a 32-lane GPU can't run (see LanePlatform)
        int graphics = 0, bad = 0;
        var dcs = DcsSaved(game);
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
        }, () => graphics >= MinGraphics, ct, DcsFiles(game, dcs));

        rootSigs.Apply(shaders);
        var dawn = DawnStores.Read(game.InstallDir);
        var dawnFile = dawn == null ? null : perFile.Keys.FirstOrDefault(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(dawn.ShaderFile), StringComparison.OrdinalIgnoreCase));
        var dawnNote = "";
        var dawnRuns = dawnFile == null ? null : DawnRuns(dawn!, perFile[dawnFile], shaders, locs, out dawnNote);

        var maps = new List<ShaderMap>();
        var mapHashes = new HashSet<string>();
        int records = 0;
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        void Stamp(string path, string rel)
        {
            var f = new FileInfo(path);
            content.AppendData(Encoding.UTF8.GetBytes(ContentStamps.Of(rel, f.FullName)));
        }
        foreach (var (path, seq) in perFile)
        {
            var rel = Rel(game, dcs, path);
            Stamp(path, rel);
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
        if (dawnRuns != null)
            foreach (var (path, rel) in new[] { (dawn!.RootFile, DawnStores.RootRel), (dawn.PsoFile, DawnStores.PsoRel) })
                Stamp(path, GameFiles.Rel(game.InstallDir, path, path, rel));
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
        if (RecordedOnly(engine)) return;
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
        var all = GameFiles.EnumerateFiles(new DirectoryInfo(dir), "*", opts).Where(f => f.Length >= 64 && !NotShaders.Contains(f.Extension) && !InMiddleware(dir, f.FullName)).ToList();
        bool Named(FileInfo f) => Path.GetRelativePath(dir, f.FullName) is var rel
            && (rel.Contains("shader", StringComparison.OrdinalIgnoreCase) || rel.Contains("pso", StringComparison.OrdinalIgnoreCase));
        return (all.Where(Named).OrderBy(f => f.FullName, GameFiles.PathOrder).ToList(),
            all.Where(f => !Named(f) && f.Length >= 64 << 10).OrderBy(f => f.Length).ThenBy(f => f.FullName, GameFiles.PathOrder).ToList());
    }

    /// <summary>Carves the install; returns bytes read. Named files: whole (Detect: sampled). Other files only while
    /// <paramref name="enough"/> is false after the named ones: sampled; Index carves a file whole when its sample holds
    /// 16+ containers. Detect stops once <paramref name="enough"/>. Unreadable files are skipped.
    /// ponytail: budget-bounded sampling of unnamed files (512 MB Detect, 4 GB Index); an engine that hides a small shader
    /// pack among many big unnamed files can be missed. Add a speed-hint glob for it if one ever is.</summary>
    static long Walk(string dir, bool detect, Found found, Func<bool> enough, CancellationToken ct = default, List<FileInfo>? also = null)
    {
        var (named, other) = Files(dir);
        named.InsertRange(0, also ?? []);
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

    /// <summary>DCS World ships HLSL only and compiles it at run time into Saved Games\&lt;variant&gt;\fxo and metashaders2:
    /// that folder, and how map paths spell it (never the user's own path). The variant is the word in the install's
    /// dcs_variant.txt ("openbeta": DCS.openbeta), else DCS. Null: not DCS (DCS.exe in the install's bin or bin-mt).
    /// Saved Games is the Windows known folder, SCSKILLER_TEST_SAVED_GAMES when set (a test run's scratch copy).</summary>
    internal static (string Dir, string Rel)? DcsSaved(Game game)
    {
        if (!Path.GetFileName(game.ExePath).Equals("DCS.exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(Path.GetDirectoryName(game.ExePath)) is not { } bin
            || !bin.Equals("bin", StringComparison.OrdinalIgnoreCase) && !bin.Equals("bin-mt", StringComparison.OrdinalIgnoreCase)) return null;
        string variant;
        try { variant = File.ReadAllText(GameFiles.Find(game.InstallDir, "dcs_variant.txt")).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { variant = ""; }
        var name = variant.Length == 0 ? "DCS" : $"DCS.{variant}";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.')) name = "DCS";
        return (Path.Combine(SavedGames(), name), $@"<SavedGames>\{name}\");
    }

    static string SavedGames()
    {
        if (Environment.GetEnvironmentVariable("SCSKILLER_TEST_SAVED_GAMES") is { Length: > 0 } test) return test;
        if (OperatingSystem.IsWindows() && SHGetKnownFolderPath(in SavedGamesId, 0, 0, out var p) == 0)
        {
            try { if (Marshal.PtrToStringUni(p) is { Length: > 0 } dir) return dir; }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
    }

    static readonly Guid SavedGamesId = new("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4");

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);

    static readonly Regex AutoUpdateVersion = new(@"""version""\s*:\s*""([\d.]+)""", RegexOptions.CultureInvariant);

    /// <summary>The installed DCS build from the install's autoupdate.cfg, null when unreadable.</summary>
    static Version? DcsInstalled(Game game)
    {
        try { return AutoUpdateVersion.Match(File.ReadAllText(GameFiles.Find(game.InstallDir, "autoupdate.cfg"))) is { Success: true } m && Version.TryParse(m.Groups[1].Value, out var v) ? v : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static readonly string[] DcsFolders = ["fxo", "metashaders2"];

    /// <summary>The files of <see cref="DcsSaved"/>'s folders, an .fxo only when it was built by the installed DCS build
    /// (an update leaves the older ones unused), else the newest build any .fxo names when the install names none. A .meta2 names no build.</summary>
    internal static List<FileInfo> DcsFiles(Game game, (string Dir, string Rel)? dcs)
    {
        if (dcs is not { } d) return [];
        var files = DcsListing(d.Dir).Where(f => f.Length >= 64).OrderBy(f => f.FullName, GameFiles.PathOrder).ToList();
        var builds = files.Where(f => f.Extension.Equals(".fxo", StringComparison.OrdinalIgnoreCase)).ToDictionary(f => f, FxoBuild);
        var want = DcsInstalled(game) ?? builds.Values.OfType<Version>().DefaultIfEmpty().Max();   // null: no header read, every .fxo stays
        return files.Where(f => !builds.TryGetValue(f, out var b) || b == want).ToList();
    }

    /// <summary>The files of the DCS folders under <paramref name="dir"/>; a folder gone or unreadable meanwhile counts as empty.</summary>
    static List<FileInfo> DcsListing(string dir) => DcsFolders.SelectMany(f =>
    {
        try { return new DirectoryInfo(Path.Combine(dir, f)).GetFiles("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }).ToList();

    /// <summary>The DCS build an .fxo was compiled by: its header's u32-length string after a u16 (measured "2.9.30.28718").</summary>
    internal static Version? FxoBuild(FileInfo f)
    {
        Span<byte> h = stackalloc byte[38];
        try
        {
            using var s = f.OpenRead();
            if (s.ReadAtLeast(h, h.Length, throwOnEndOfStream: false) < h.Length) return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var n = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(h[2..]);
        return n is > 0 and <= 32 && Version.TryParse(Encoding.ASCII.GetString(h.Slice(6, (int)n)), out var v) ? v : null;
    }

    static string Rel(Game game, (string Dir, string Rel)? dcs, string path) =>
        dcs is { } d && path.StartsWith(d.Dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? d.Rel + GameFiles.Rel(d.Dir, path) : GameFiles.Rel(game.InstallDir, path);

    /// <summary>DCS's compiled shader folders by name, size and write time and the installed build (it picks the .fxo): a play
    /// that compiles more, or an update, re-plans.</summary>
    public string IndexStamp(Game game) => (DcsSaved(game) is { } d
        ? DcsInstalled(game) + ";" + string.Join(';', DcsListing(d.Dir).OrderBy(f => f.FullName, StringComparer.Ordinal).Select(f => $"{f.Directory!.Name}/{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}"))
        : "") + LumaStamp(game);

    public const string LumaTag = "sfluma:";

    /// <summary>Starfield Luma's plugin, its .toml (the render targets it upgrades) and Data\shadersfx (the shaders it replaces,
    /// carved as named files) by name, size and write time: installing, changing or removing it makes a compile stale. "": none.</summary>
    static string LumaStamp(Game game)
    {
        if (ReShade.StarfieldLuma(game) is not { } plugin) return "";
        var sfx = new DirectoryInfo(GameFiles.Find(game.InstallDir, Path.Combine("Data", "shadersfx")));
        var files = new[] { plugin, new FileInfo(GameFiles.ChangeExtension(plugin.FullName, ".toml")) }.Where(f => f.Exists)
            .Concat(sfx.Exists ? GameFiles.EnumerateFiles(sfx, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }) : []);
        return LumaTag + string.Join(';', files.Select(f => $"{GameFiles.Rel(game.InstallDir, f.FullName)}|{f.Length}|{f.LastWriteTimeUtc.Ticks}").Order(StringComparer.Ordinal));
    }

    /// <summary>A Godot game's also by its launch options and the override.cfg beside its exe (<see cref="GodotApi"/>).</summary>
    public string DetectStamp(Game game, EngineInfo? engine) => IndexStamp(game)
        + (engine?.Unsupported is GodotReason or GodotOpenGl && GodotOverride(game.ExePath) is var cfg
            ? $"|{UnrealRhi.LaunchOptions(game)}|{(cfg is { Exists: true } ? $"{cfg.Length}|{cfg.LastWriteTimeUtc.Ticks}" : "")}" : "");

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
    /// D3D11 / Vulkan when that's all they import (Vulkan also when the exe loads it at runtime, <see cref="LoadsVulkan"/>), D3D11 also when the exe itself
    /// imports d3d11.dll and neither imports nor names d3d12.dll (no DXIL);
    /// else <see cref="RuntimeApi"/>, "D3D11 or D3D12" when nothing is readable. Middleware's own shaders and runtime (<see cref="InMiddleware"/>) don't count,
    /// nor a Wine prefix's in the game folder (<see cref="GameFiles.InWinePrefix"/>).</summary>
    static string GraphicsApi(Game game, bool dxil)
    {
        // a DLL or Agility runtime someone added beside a D3D11-only exe doesn't make it run D3D12 (a mod's)
        if (!dxil && ApiImports(game.ExePath) is { } own && own.Contains("D3D11") && !own.Contains("D3D12") && NamesApi(game.ExePath, D3D12Names) != false) return "D3D11";
        var apis = Imports(game);
        var core = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true, AttributesToSkip = 0 };
        bool d3d12 = dxil || apis.Contains("D3D12") || GameFiles.EnumerateFiles(game.InstallDir, "D3D12Core.dll", core)
            .Any(p => !InMiddleware(game.InstallDir, p) && !GameFiles.InWinePrefix(game.InstallDir, game.ExePath, p));
        bool d3d11 = apis.Contains("D3D11");
        return d3d12 ? (d3d11 && !dxil ? UnrealRhi.Ambiguous : "D3D12") : d3d11 ? "D3D11" : apis.Contains("Vulkan") ? "Vulkan" : OldApi(game) is { } old ? Wrapped(game, old)
            : RuntimeApi(game) ?? UnrealRhi.Ambiguous;
    }

    // FNA compiles its effects at run time (MojoShader): no DXBC ever sits in the files.
    static bool Fna(Game game) => Path.GetDirectoryName(game.ExePath) is { } dir && Directory.Exists(dir) && GameFiles.EnumerateFiles(dir, "FNA3D.dll").Any();

    /// <summary>The API of a game that loads it at run time, by the files beside its exe: FNA3D's first driver on Windows is
    /// D3D11 with SDL2 (with SDL3 it may be SDL_GPU's Vulkan or D3D12), MonoGame's is D3D11 with SharpDX (WindowsDX), else OpenGL (DesktopGL). id Tech names its Vulkan exe *x64vk,
    /// which an Xbox install doesn't let anyone read. Null: none of them.</summary>
    static string? RuntimeApi(Game game)
    {
        if (Path.GetDirectoryName(game.ExePath) is not { } dir || !Directory.Exists(dir)) return null;
        bool Has(string pattern) => GameFiles.EnumerateFiles(dir, pattern).Any();
        return Path.GetFileNameWithoutExtension(game.ExePath).EndsWith("x64vk", StringComparison.OrdinalIgnoreCase) ? "Vulkan"
            : Has("FNA3D.dll") && !Has("SDL3.dll") ? "D3D11"
            : Has("MonoGame.Framework.dll") ? Has("SharpDX*.dll") ? "D3D11" : "OpenGL"
            : null;
    }

    /// <summary>"D3D10" or "D3D9": the exe, or a non-middleware DLL next to it or in its bin folder (Source 1 keeps its renderer
    /// there), imports d3d10.dll / d3d10_1.dll or d3d9.dll, and none of them imports a later API; else the exe or such a DLL
    /// named for DirectX 9 loads one by its entry point and names no D3D11 or D3D12 runtime. Null: neither.</summary>
    static string? OldApi(Game game)
    {
        if (Path.GetDirectoryName(game.ExePath) is not { } dir || !Directory.Exists(dir)) return null;
        var bin = Directory.EnumerateDirectories(dir, "bin", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true }).FirstOrDefault();
        var dlls = GameFiles.EnumerateFiles(dir, "*.dll").Concat(bin != null ? GameFiles.EnumerateFiles(bin, "*.dll", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true }) : [])
            .Where(p => !Middleware.Any(m => Path.GetFileName(p).StartsWith(m, StringComparison.OrdinalIgnoreCase)));
        string? old = null;
        foreach (var p in dlls.Prepend(game.ExePath))
            try
            {
                var imports = PeImports(p, out var agility);
                if (agility || imports.Any(ApiDlls.ContainsKey)) return null;
                if (imports.Any(D3D10Dlls.Contains)) old = "D3D10";
                else if (old == null && imports.Contains("d3d9.dll", StringComparer.OrdinalIgnoreCase)) old = "D3D9";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) { }
        if (old != null) return old;
        // ponytail: only DLLs named for the API are read whole (Source 1's shaderapidx9.dll), add others when a renderer is missed
        foreach (var p in dlls.Where(p => Path.GetFileName(p).Contains("dx9", StringComparison.OrdinalIgnoreCase)).Prepend(game.ExePath))
            // NamesApi is false for an unreadable file too: only a readable one is read for names
            if (ApiImports(p) != null && NamesApi(p, [.. D3D10Entry, .. D3D9Entry]) == false && NamesApi(p, D3DRuntimes) == null)
                return NamesApi(p, D3D10Entry) == false ? "D3D10" : "D3D9";
        return null;
    }

    /// <summary>The API a DirectX 9 or 10 game runs on: Vulkan with DXVK beside the exe (its d3d9.dll, or d3d10core.dll,
    /// d3d11.dll or dxgi.dll), DirectX 12 when the exe calls Direct3DCreate9On12, else D3D9 or <see cref="Dx10"/>.</summary>
    static string Wrapped(Game game, string old)
    {
        var dir = Path.GetDirectoryName(game.ExePath)!;
        string[] dxvk = old == "D3D9" ? ["d3d9.dll"] : ["d3d10core.dll", "d3d11.dll", "dxgi.dll"];
        if (dxvk.Any(n => ReShade.IsDxvk(GameFiles.Find(dir, n)))) return "Vulkan (DXVK)";
        return old == "D3D9" && ApiImports(game.ExePath) != null && NamesApi(game.ExePath, D3D9On12Entry) == false ? "D3D12 (D3D9On12)" : old == "D3D10" ? Dx10 : old;
    }

    /// <summary>The game as its API is read: with the one other exe beside its exe that imports a graphics API when its own
    /// imports and names none (a launcher or a packed exe: Fahrenheit.exe beside FahrenheitSub.exe). Its exe stays the
    /// warm's name.</summary>
    static Game ApiExe(Game game)
    {
        // a .NET exe's API is its runtime's (RuntimeApi)
        if (Path.GetDirectoryName(game.ExePath) is not { } dir || !Directory.Exists(dir) || ImportsApi(game.ExePath) != false
            || PeImports(game.ExePath, out _).Contains("mscoree.dll", StringComparer.OrdinalIgnoreCase)) return game;
        var others = GameFiles.EnumerateFiles(dir, "*.exe").Where(p => !SameDir(p, game.ExePath) && ImportsApi(p) == true).Take(2).ToList();
        return others is [var one] && NamesApi(game.ExePath, [.. D3DNames, .. D3D10Entry, .. D3D9Entry]) == null ? game with { ExePath = one } : game;
    }

    /// <summary>The binary imports D3D9, D3D10, D3D11, D3D12 or Vulkan; null when it can't be read.</summary>
    static bool? ImportsApi(string path)
    {
        try { return PeImports(path, out var agility) is var dlls && (agility || dlls.Any(d => ApiDlls.ContainsKey(d) || D3D10Dlls.Contains(d) || d.Equals("d3d9.dll", StringComparison.OrdinalIgnoreCase))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) { return null; }
    }

    static readonly HashSet<string> D3D10Dlls = new(["d3d10.dll", "d3d10_1.dll"], StringComparer.OrdinalIgnoreCase);

    static readonly Dictionary<string, string> ApiDlls = new(StringComparer.OrdinalIgnoreCase)
        { ["d3d12.dll"] = "D3D12", ["d3d11.dll"] = "D3D11", ["vulkan-1.dll"] = "Vulkan" };

    /// <summary>Middleware that imports a graphics API without being the renderer.</summary>
    static readonly string[] Middleware = ["eossdk", "libxess", "dstorage", "gfsdk", "nvngx", "sl.", "amd_", "ffx", "nvlowlatency", "galaxy",
        "steam_api", "discord", "overlay", "igxess", "renderdoc", "reshade", "dxgi", "d3d1", "d3d9", "opengl32", "vulkan-1", "libegl", "libglesv2",
        "vk_swiftshader", "d3dcompiler", "dxcompiler", "dxil", "cef", "nvapi", "nvtt", "physx", "bink", "xess", "fsr", "dlss", "streamline",
        "gameoverlay", "twitch", "nvrtx", "nri", "nrd", "nrc", "ags", "libxell", "pix", "winpix", "aftermath", "gfnruntime", "optiscaler", "helixsr"];

    /// <summary>Middleware that ships its own shaders or D3D12 runtime under its name, whatever API the game runs on
    /// (HelixSR\helixsr_kernels.pak holds DXIL, OptiScaler's D3D12_Optiscaler\D3D12Core.dll): a file with it in its path
    /// below the install is out of the shader walk and of the API.</summary>
    static readonly string[] MiddlewareFolders = ["helixsr", "optiscaler"];

    static bool InMiddleware(string dir, string path) =>
        Path.GetRelativePath(dir, path).Split('\\', '/').Any(p => MiddlewareFolders.Any(m => p.Contains(m, StringComparison.OrdinalIgnoreCase)));

    /// <summary>APIs imported by the exe and the non-middleware DLLs next to it (+ "D3D12" for an exported D3D12SDKVersion),
    /// or "Vulkan" when none is and the exe loads it at runtime (<see cref="LoadsVulkan"/>) or, naming no API itself, starts a
    /// Vulkan renderer in a subfolder (<see cref="VulkanBelow"/>). Empty when unreadable (Xbox/GDK exes are ACL-protected).
    /// Read in a game with anti-cheat too: only for its API, which a risk session's recorder follows.</summary>
    static HashSet<string> Imports(Game game)
    {
        if (Path.GetDirectoryName(game.ExePath) is not { } dir || !Directory.Exists(dir)) return [];
        var apis = ImportsBeside(game, dir);
        if (apis.Count == 0 && NamesApi(game.ExePath) is var named && (named == true || named == null && VulkanBelow(game, dir))) apis.Add("Vulkan");
        return apis;
    }

    /// <summary>The game's exe names no graphics API, nor does any non-middleware DLL beside it import one, and it starts a
    /// Vulkan renderer below its folder (<see cref="VulkanBelow"/>): the renderer is another process.</summary>
    internal static bool StartsVulkanRenderer(Game game) =>
        Path.GetDirectoryName(game.ExePath) is { } dir && Directory.Exists(dir) && ImportsBeside(game, dir).Count == 0 && NamesApi(game.ExePath) == null && VulkanBelow(game, dir);

    /// <summary>The binary imports vulkan-1.dll.</summary>
    internal static bool ImportsVulkan(string path) => ApiImports(path)?.Contains("Vulkan") == true;

    static HashSet<string> ImportsBeside(Game game, string dir)
    {
        var apis = new HashSet<string>();
        var bins = GameFiles.EnumerateFiles(dir, "*.dll").Where(p => !Middleware.Any(m => Path.GetFileName(p).StartsWith(m, StringComparison.OrdinalIgnoreCase)));
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

    static readonly EnumerationOptions Below = new() { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    /// <summary>An exe in a subfolder of the game's imports vulkan-1.dll, and no exe there imports D3D11 or D3D12, nor names it
    /// when it imports no API, nor does a non-middleware DLL beside one of them: a launcher that starts its Vulkan renderer
    /// (the Bloodborne port's engine\out\bb-probe.exe).</summary>
    static bool VulkanBelow(Game game, string dir)
    {
        bool vulkan = false;
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // ponytail: the first 64 exes below, enough for a launcher's renderer and its tools
        foreach (var p in GameFiles.EnumerateFiles(dir, "*.exe", Below).Where(p => !SameDir(Path.GetDirectoryName(p)!, dir) && !InMiddleware(dir, p)
                     && !Middleware.Any(m => Path.GetFileName(p).StartsWith(m, StringComparison.OrdinalIgnoreCase)) && !GameFiles.InWinePrefix(game.InstallDir, game.ExePath, p)).Take(64))
        {
            if (ApiImports(p) is not { } a) continue;
            if (a.Contains("D3D11") || a.Contains("D3D12")) return false;
            // a DirectX game exe that loads D3D at runtime; a Vulkan renderer may name DXGI (bb-probe.exe does), not this
            if (a.Count == 0 && NamesApi(p, D3DRuntimes) == false) return false;
            vulkan |= a.Contains("Vulkan");
            folders.Add(Path.GetDirectoryName(p)!);
        }
        return vulkan && folders.All(f => GameFiles.EnumerateFiles(f, "*.dll").Where(p => !Middleware.Any(m => Path.GetFileName(p).StartsWith(m, StringComparison.OrdinalIgnoreCase)))
            .All(p => ApiImports(p) is not { } a || !a.Contains("D3D11") && !a.Contains("D3D12")));
    }

    static bool SameDir(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>The APIs a binary imports (D3D12 also for an exported D3D12SDKVersion); null when it can't be read.</summary>
    static HashSet<string>? ApiImports(string path)
    {
        try
        {
            var dlls = PeImports(path, out var agility);
            var apis = dlls.Select(d => ApiDlls.GetValueOrDefault(d)).OfType<string>().ToHashSet();
            if (agility) apis.Add("D3D12");
            return apis;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) { return null; }
    }

    // lowercase; a DLL name also in UTF-16, as LoadLibraryW takes it (GetProcAddress names are ASCII only)
    static readonly byte[][] VulkanLoader = [A("vulkan-1.dll"), W("vulkan-1.dll")], VulkanEntry = [A("vkcreateinstance"), A("vkgetinstanceprocaddr")],
        D3DNames = [A("d3d12.dll"), W("d3d12.dll"), A("d3d11.dll"), W("d3d11.dll"), A("dxgi.dll"), W("dxgi.dll"), A("d3d12createdevice"), A("d3d11createdevice"), A("createdxgifactory")],
        D3DRuntimes = [A("d3d12.dll"), W("d3d12.dll"), A("d3d11.dll"), W("d3d11.dll"), A("d3d12createdevice"), A("d3d11createdevice")],
        D3D12Names = [A("d3d12.dll"), W("d3d12.dll"), A("d3d12createdevice")];
    // GetProcAddress names, lowercase: D3D10CreateDevice1 and *AndSwapChain, Direct3DCreate9Ex and Direct3DCreate9On12 too
    static readonly byte[][] D3D10Entry = [A("d3d10createdevice")], D3D9Entry = [A("direct3dcreate9")], D3D9On12Entry = [A("direct3dcreate9on12")];
    static byte[] A(string s) => Encoding.ASCII.GetBytes(s);
    static byte[] W(string s) => Encoding.Unicode.GetBytes(s);

    /// <summary>The exe names vulkan-1.dll and a Vulkan loader entry point, and no D3D or DXGI runtime (any case): it loads
    /// Vulkan at runtime (Enshrouded imports no graphics API).</summary>
    internal static bool LoadsVulkan(string exe) => NamesApi(exe) == true;

    /// <summary>True as <see cref="LoadsVulkan"/>; false: the exe names one of <paramref name="d3d"/> (a D3D or DXGI runtime),
    /// or can't be read; null: neither. Read in blocks, exes run to hundreds of MB.</summary>
    static bool? NamesApi(string exe, byte[][]? d3d = null)
    {
        d3d ??= D3DNames;
        const int Overlap = 32;   // > the longest name, so one cut by a block end is seen whole next time
        try
        {
            using var h = File.OpenHandle(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
            var buf = new byte[Block + Overlap];
            bool loader = false, entry = false;
            var kept = 0;
            for (long pos = 0; RandomAccess.Read(h, buf.AsSpan(kept, Block), pos) is var n and > 0; pos += n)
            {
                var span = buf.AsSpan(0, kept + n);
                foreach (ref var b in span[kept..])
                    if ((uint)(b - 'A') <= 'Z' - 'A') b |= 0x20;
                if (Has(span, d3d)) return false;
                loader |= Has(span, VulkanLoader);
                entry |= Has(span, VulkanEntry);
                kept = Math.Min(Overlap, span.Length);
                span[^kept..].CopyTo(buf);
            }
            return loader && entry ? true : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }

        static bool Has(ReadOnlySpan<byte> span, byte[][] names)
        {
            foreach (var name in names)
                if (span.IndexOf(name) >= 0) return true;
            return false;
        }
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
