using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unreal;
using static SCSKiller.Core.Unity.UnityFiles;

namespace SCSKiller.Core.Unity;

/// <summary>Unity games (UnityPlayer.dll + a *_Data folder). Shaders live in serialized Shader objects (class 48) as a
/// per-platform LZ4 blob, in loose files (globalgamemanagers.assets, resources.assets, sharedassets*, level*,
/// Resources/unity_builtin_extra) and in UnityFS bundles anywhere under *_Data (Addressables / AssetBundles in
/// StreamingAssets): <see cref="UnityFiles"/>. The d3d11 platform's blob holds DXBC containers that both the DX11 and the
/// DX12 player run (Unity has no separate Windows DX12 shader platform), so the index is one DXBC set; ComputeShader
/// objects (72) keep theirs raw. No root signatures ship (Unity builds them at run time): DX12 needs a recording.
/// Maps: one per Shader object (its passes and keyword variants are what pair), platform PCD3D_SM5 (PCD3D_SM6 when DXIL),
/// so the planner treats them like Unreal's shader maps. EngineInfo: Family "Unity", Version "2022.3.62f2".</summary>
public sealed class UnityReader : IEngineReader
{
    public const string Family = "Unity";

    /// <summary>Where Unity players keep their Player.log: %USERPROFILE%\AppData\LocalLow.</summary>
    public string LocalLow { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");

    public sealed record Loc(string Path, string? Node, long Start, long Size, int ClassId);

    /// <summary>Where each container was found, per game id, from the last Index (see CarvedReader: in memory only).</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Loc>> located = new();

    /// <summary>The folder holding UnityPlayer.dll and its *_Data folder (the exe's own name first): the exe's folder, the
    /// install folder, or one or two levels below it (Xbox "Content\", a zip-shaped "Normal\").</summary>
    public static (string Player, string Data)? Find(Game game)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true, AttributesToSkip = 0 };
        IEnumerable<string> dirs = [Path.GetDirectoryName(game.ExePath) ?? "", game.InstallDir];
        if (Directory.Exists(game.InstallDir)) dirs = dirs.Concat(Directory.EnumerateDirectories(game.InstallDir, "*", opts));
        foreach (var dir in dirs.Where(d => d.Length > 0 && File.Exists(Path.Combine(d, "UnityPlayer.dll"))))
        {
            var own = Path.Combine(dir, Path.GetFileNameWithoutExtension(game.ExePath) + "_Data");
            var data = Directory.EnumerateDirectories(dir, "*_Data").OrderBy(d => !d.Equals(own, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "globalgamemanagers")) || File.Exists(Path.Combine(d, "data.unity3d")));
            if (data != null) return (dir, data);
        }
        return null;
    }

    public EngineInfo? Detect(Game game)
    {
        if (Find(game) is not var (player, data)) return null;
        string? version = null;
        int[]? apis = null;
        int probed = 0, readable = 0; // a few built-in Shader objects: a layout this reader can't read fails here, not as an empty index
        foreach (var src in Sources(data, onlyManagers: true))
            using (src)
            {
                version ??= src.File.UnityVersion;
                if (src.File.Objects.FirstOrDefault(o => o.ClassId == BuildSettingsClass) is { } bs) apis ??= GraphicsApis(src.Read(bs.Start, (int)bs.Size));
                foreach (var o in src.File.Objects.Where(o => o.ClassId == ShaderClass).Take(3 - probed))
                {
                    probed++;
                    try { if (ShaderBlob(src.Read(o.Start, (int)o.Size)) != null) readable++; }
                    catch (Exception e) when (e is IOException or InvalidDataException) { }
                }
            }
        if (string.IsNullOrEmpty(version)) version = FileVersionInfo.GetVersionInfo(Path.Combine(player, "UnityPlayer.dll")) is { FileMajorPart: > 0 } v
            ? $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}" : "?";
        var lastRun = LastRun(PlayerLog(data));
        return new EngineInfo(Family, version, null, Api(apis, lastRun), false,
            probed > 0 && readable == 0 ? $"Unity {version}: none of {probed} built-in shaders has DirectX (d3d11) code this reader can read" : null,
            ApiGuessed: ApiGuessed(apis, lastRun));
    }

    /// <summary>The API the last run used (Player.log), else the first of the build's list; "D3D11 or D3D12" when neither is
    /// readable. A launch option (-force-d3d12 / -force-d3d11) overrides the list; only a log shows that.</summary>
    public static string Api(int[]? apis, string? lastRun) =>
        lastRun != null ? $"{lastRun} (last run)"
        : apis is [var first, ..] ? first switch { 2 => "D3D11", 18 => "D3D12", 21 => "Vulkan", _ => "OpenGL" }
        : UnrealRhi.Ambiguous;

    /// <summary>The build offers DirectX 11 and 12 and no log shows which ran: the first of its list is a guess (a launch
    /// option or the game's own menu may pick the other).</summary>
    public static bool ApiGuessed(int[]? apis, string? lastRun) => lastRun == null && apis is [2 or 18, ..] && apis.Contains(2) && apis.Contains(18);

    static readonly Regex LogApi = new(@"^\s*Version:\s+(Direct3D 11|Direct3D 12|Vulkan)", RegexOptions.Multiline);

    /// <summary><see cref="LocalLow"/>\&lt;company&gt;\&lt;product&gt;\Player.log (from *_Data\app.info); null when app.info doesn't name it.</summary>
    string? PlayerLog(string data)
    {
        try { return File.ReadAllLines(Path.Combine(data, "app.info")) is [var company, var product, ..] ? Path.Combine(LocalLow, company, product, "Player.log") : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The Player.log <see cref="Detect"/> reads the last run's API from, by size and write time: a new run redoes it.</summary>
    public string DetectStamp(Game game, EngineInfo? engine)
    {
        if (engine != null && engine.Family != Family) return "";
        if (!dataDirs.TryGetValue(game.ExePath, out var data) && Find(game) is var (_, found))
            dataDirs[game.ExePath] = data = found;   // a miss isn't kept: the install may be mid-update
        var log = data == null ? null : PlayerLog(data) is { } p ? new FileInfo(p) : null;
        return log is { Exists: true } ? $"{log.Length}|{log.LastWriteTimeUtc.Ticks}" : "";
    }

    readonly ConcurrentDictionary<string, string> dataDirs = new(StringComparer.OrdinalIgnoreCase);

    static string? LastRun(string? log)
    {
        try
        {
            if (log == null || !File.Exists(log)) return null;
            using var s = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            var head = new char[256 << 10];
            var m = LogApi.Match(new string(head, 0, s.ReadBlock(head)));
            return !m.Success ? null : m.Groups[1].Value switch { "Direct3D 11" => "D3D11", "Direct3D 12" => "D3D12", _ => "Vulkan" };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var data = Find(game)?.Data ?? throw new DirectoryNotFoundException($"{game.InstallDir}: no Unity data folder");
        var locs = new Dictionary<string, Loc>();
        var shaders = new Dictionary<string, ShaderInfo>();
        var maps = new List<ShaderMap>();
        var mapHashes = new HashSet<string>();
        var bundles = new HashSet<Bundle>();
        int files = 0, shaderObjs = 0, computeObjs = 0, noBlob = 0, unreadable = 0, bad = 0;
        foreach (var src in Sources(data, onlyManagers: false))
            using (src)
            {
                ct.ThrowIfCancellationRequested();
                files++;
                if (src.In != null) bundles.Add(src.In);
                var rel = Path.GetRelativePath(data, src.Path) + (src.Node != null ? $"|{src.Node}" : "");
                foreach (var o in src.File.Objects.Where(o => o.ClassId is ShaderClass or ComputeShaderClass))
                {
                    if (o.ClassId == ShaderClass) shaderObjs++; else computeObjs++;
                    List<byte[]>? programs;
                    try { programs = Programs(src.Read(o.Start, (int)o.Size), o.ClassId)?.ToList(); }
                    catch (Exception e) when (e is IOException or InvalidDataException) { unreadable++; continue; } // a bad bundle block
                    if (programs == null) { noBlob++; continue; }
                    var shas = new List<string>();
                    var dxil = false;
                    foreach (var c in programs)
                    {
                        var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                        if (locs.TryAdd(sha, new Loc(src.Path, src.Node, o.Start, o.Size, o.ClassId)))
                            try
                            {
                                if (Dxbc.Kind(c) >= 0 && ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info) shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                            }
                            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { bad++; }
                        if (!shaders.ContainsKey(sha)) continue;
                        shas.Add(sha);
                        dxil |= !Dxbc.Part(c, "DXIL"u8).IsEmpty;
                    }
                    shas = shas.Distinct().ToList();
                    var h = CarvedReader.Sha1Hex(string.Join(',', shas.Order(StringComparer.Ordinal)));
                    if (shas.Count > 0 && mapHashes.Add(h)) maps.Add(new ShaderMap(h, rel, dxil ? "PCD3D_SM6" : "PCD3D_SM5", shas));
                }
            }
        located[game.Id] = locs;

        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var path in locs.Values.Select(l => l.Path).Distinct().Order(StringComparer.OrdinalIgnoreCase))
        {
            var f = new FileInfo(path);
            content.AppendData(Encoding.UTF8.GetBytes($"{Path.GetRelativePath(data, path)}|{f.Length}|{f.LastWriteTimeUtc.Ticks}\n"));
        }
        log?.Report($"{files} serialized files ({bundles.Count} bundles, {bundles.Sum(b => b.BlocksDecompressed)} blocks decompressed): {shaderObjs} Shader objects"
            + $"{(noBlob > 0 ? $" ({noBlob} without d3d11 code)" : "")}{(unreadable > 0 ? $" ({unreadable} unreadable)" : "")}, {computeObjs} ComputeShaders -> {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"){(bad > 0 ? $", {bad} unparseable" : "")}, {maps.Count} maps ({sw.Elapsed.TotalSeconds:F1}s)");
        var platforms = maps.Select(m => m.Platform).Distinct().Order(StringComparer.Ordinal).ToList();
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), platforms, shaders, maps);
    }

    /// <summary>Re-reads each requested shader's object and sinks its containers; one whose bytes changed since (game patched)
    /// isn't found and fails at replay.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locs))
        {
            Index(game, engine, null, ct);
            locs = located[game.Id];
        }
        var sent = new HashSet<string>();
        foreach (var file in sha1s.Where(locs.ContainsKey).Select(s => locs[s]).Distinct().GroupBy(l => (l.Path, l.Node)))
        {
            using var src = Open(file.Key.Path, file.Key.Node);
            if (src == null) continue;
            foreach (var l in file.OrderBy(l => l.Start))
            {
                ct.ThrowIfCancellationRequested();
                List<byte[]> programs;
                try { programs = Programs(src.Read(l.Start, (int)l.Size), l.ClassId)?.ToList() ?? []; }
                catch (Exception e) when (e is IOException or InvalidDataException) { continue; }
                foreach (var c in programs)
                    if (Convert.ToHexStringLower(SHA1.HashData(c)) is var sha && sha1s.Contains(sha) && sent.Add(sha)) sink(sha, c);
            }
        }
    }

    /// <summary>The containers of a Shader (its d3d11 blob, decompressed) or ComputeShader (raw) object; null = a Shader
    /// without d3d11 code.</summary>
    static IEnumerable<byte[]>? Programs(byte[] obj, int cls) =>
        cls == ComputeShaderClass ? Dxbc.Containers(obj).Select(c => c.Container)
        : ShaderBlob(obj)?.SelectMany(b => Dxbc.Containers(b).Select(c => c.Container));

    /// <summary>One serialized file, loose or a node of <paramref name="In"/>; disposing it closes <paramref name="Owner"/>.</summary>
    public sealed record Source(string Path, string? Node, SerializedFile File, ReadAt Read, Bundle? In, IDisposable? Owner) : IDisposable
    {
        public void Dispose() => Owner?.Dispose();
    }

    static readonly string[] Managers = ["globalgamemanagers", "globalgamemanagers.assets", "mainData"];

    static bool IsResource(string name) => name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every serialized file under the data folder, loose or inside a UnityFS bundle (each file's first bytes decide,
    /// not its name). <paramref name="onlyManagers"/>: globalgamemanagers(.assets) only (loose or in data.unity3d).</summary>
    static IEnumerable<Source> Sources(string data, bool onlyManagers)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = !onlyManagers, IgnoreInaccessible = true, AttributesToSkip = 0, MaxRecursionDepth = 8 };
        var paths = Directory.EnumerateFiles(data, "*", opts).Where(p => !IsResource(p)).Order(StringComparer.OrdinalIgnoreCase);
        foreach (var path in onlyManagers ? paths.Where(p => Managers.Append("data.unity3d").Contains(System.IO.Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)) : paths)
        {
            if (Open(path, null) is { } loose) { yield return loose; continue; }
            Bundle? b;
            try { b = Bundle.Open(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { continue; }
            if (b == null) continue;
            using (b)
                foreach (var n in b.Nodes.Where(n => !IsResource(n.Path) && (!onlyManagers || Managers.Contains(n.Path, StringComparer.OrdinalIgnoreCase))))
                {
                    SerializedFile? sf;
                    try { sf = ReadSerialized(b.Reader(n), n.Size); }
                    catch (InvalidDataException) { sf = null; }
                    if (sf != null) yield return new Source(path, n.Path, sf, b.Reader(n), b, null);
                }
        }
    }

    /// <summary>A loose serialized file, or one node of a bundle; null when it isn't one (or is gone).</summary>
    static Source? Open(string path, string? node)
    {
        try
        {
            if (node != null)
            {
                var bundle = Bundle.Open(path);
                if (bundle?.Nodes.FirstOrDefault(n => n.Path == node) is { } nd && ReadSerialized(bundle.Reader(nd), nd.Size) is { } f)
                    return new Source(path, node, f, bundle.Reader(nd), bundle, bundle);
                bundle?.Dispose();
                return null;
            }
            var h = System.IO.File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = RandomAccess.GetLength(h);
            byte[] Read(long off, int count)
            {
                var b = new byte[Math.Max(0, Math.Min(count, len - off))];
                return RandomAccess.Read(h, b, off) == b.Length ? b : b[..0];
            }
            if (ReadSerialized(Read, len) is { } sf) return new Source(path, null, sf, Read, null, h);
            h.Dispose();
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }
}
