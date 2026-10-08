using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unreal;
using ValveResourceFormat.CompiledShader;
#if VALVEPAK6
using ValvePak;
#else
using SteamDatabase.ValvePak;
#endif

namespace SCSKiller.Core.Source2;

/// <summary>Valve's Source 2 (Counter-Strike 2, Deadlock, Dota 2): compiled VFX programs in game\&lt;mod&gt;\shaders_pc_dir.vpk.
/// Each &lt;program&gt;_pc_&lt;sm&gt;_&lt;stage&gt;.vcs holds static combos, each holding the DXBC of its dynamic combos;
/// ValveResourceFormat parses them. The Windows builds run DirectX 11 unless the launch options or a boot.vcfg pick
/// Vulkan, so this reader is D3D11 only (NVIDIA, Planner.D3D11Cache). Maps: one per VFX program with all its stages, so
/// a program's hull and domain shaders pair. EngineInfo: Family "Source 2", Version "-".</summary>
public sealed class Source2Reader : IEngineReader
{
    public const string Family = "Source 2", Platform = "PCD3D_SM5";

    public EngineInfo? Detect(Game game)
    {
        if (!File.Exists(Path.Combine(game.InstallDir, "game", "bin", "win64", "engine2.dll"))) return null;
        var vpks = Vpks(game).ToList();
        return new EngineInfo(Family, "-", null, Api(UnrealRhi.LaunchOptions(game), BootVcfgs(game).Select(Text)), false,
            vpks.Count == 0 ? "no shaders_pc_dir.vpk in the install" : Unreadable(vpks[0]));
    }

    public string DetectStamp(Game game, EngineInfo? engine) => UnrealRhi.LaunchOptions(game) + string.Concat(BootVcfgs(game)
        .Select(p => new FileInfo(p)).Select(f => $"|{f.Length}:{f.LastWriteTimeUtc.Ticks}"));

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var shaders = new Dictionary<string, ShaderInfo>();
        var maps = new Dictionary<string, ShaderMap>();
        var stamp = new StringBuilder();
        var skipped = new Skipped();
        int programs = 0, bad = 0;
        foreach (var vpk in Vpks(game))
        {
            var rel = Path.GetRelativePath(game.InstallDir, vpk);
            var f = new FileInfo(vpk);
            stamp.Append($"{rel}|{f.Length}|{f.LastWriteTimeUtc.Ticks};");   // the dir VPK holds every entry's CRC
            foreach (var (program, containers) in Programs(vpk, skipped, ct))
            {
                programs++;
                var shas = new List<string>();
                foreach (var c in containers)
                {
                    var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                    try
                    {
                        if (!shaders.ContainsKey(sha) && ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info)
                            shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                    }
                    catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { bad++; }
                    if (shaders.ContainsKey(sha)) shas.Add(sha);
                }
                if (shas.Count == 0) continue;
                var h = CarvedReader.Sha1Hex($"{rel}|{program}");
                maps.TryAdd(h, new ShaderMap(h, $"{rel}:{program}", Platform, shas.Distinct().ToList()));
            }
        }
        log?.Report($"{programs} VFX programs, {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"){(bad > 0 ? $", {bad} unparseable" : "")} -> {maps.Count} maps ({sw.Elapsed.TotalSeconds:F1}s)");
        if (skipped.Count > 0) log?.Report($"{skipped.Count} vcs files or combos unreadable, first: {skipped.First}");
        var content = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(stamp.ToString())));
        return new ShaderIndex(content, [Platform], shaders, maps.Values.ToList());
    }

    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        var left = new HashSet<string>(sha1s);
        foreach (var vpk in Vpks(game))
            foreach (var (_, containers) in Programs(vpk, null, ct))
                foreach (var c in containers)
                    if (Convert.ToHexStringLower(SHA1.HashData(c)) is var sha && left.Remove(sha))
                    {
                        sink(sha, c);
                        if (left.Count == 0) return;
                    }
    }

    sealed class Skipped
    {
        public int Count;
        public string? First;
        public void Add(string what, Exception e) { Count++; First ??= $"{what}: {e.Message}"; }
    }

    static IEnumerable<(string Program, List<byte[]> Containers)> Programs(string dirVpk, Skipped? skipped, CancellationToken ct)
    {
        using var pak = new Package();
        pak.Read(dirVpk);
        if (pak.Entries == null || !pak.Entries.TryGetValue("vcs", out var entries)) yield break;
        foreach (var group in entries.Where(IsDirectX).GroupBy(e => e.FileName[..e.FileName.LastIndexOf('_')]))
        {
            var containers = new List<byte[]>();
            foreach (var e in group)
            {
                ct.ThrowIfCancellationRequested();
                pak.ReadEntry(e, out var data);
                using var program = new VfxProgramData();
                try { program.Read(e.GetFileName(), new MemoryStream(data)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { skipped?.Add(e.GetFileName(), ex); continue; }
                if (program.VcsPlatformType != VcsPlatformType.PC
                    || program.VcsProgramType is VcsProgramType.Features or VcsProgramType.PixelShaderRenderState) continue;
                foreach (var id in program.StaticComboEntries.Keys)
                {
                    ct.ThrowIfCancellationRequested();
                    VfxStaticComboData combo;
                    try { combo = program.GetStaticCombo(id); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { skipped?.Add($"{e.GetFileName()} combo {id}", ex); continue; }
                    foreach (var file in combo.ShaderFiles)
                        if (file is VfxShaderFileDXBC { Bytecode.Length: > 0 } dx)
                            containers.AddRange(Dxbc.Containers(dx.Bytecode).Select(x => x.Container));
                }
            }
            yield return (group.Key, containers);
        }
    }

    // "_pc_" is the DirectX platform; "_pcgl_" and "_vulkan_" don't match it
    static bool IsDirectX(PackageEntry e) => e.FileName.Contains("_pc_", StringComparison.Ordinal);

    /// <summary>Why the VPK's shaders can't be read, or null: its smallest DirectX vcs file is parsed, so a vcs version newer
    /// than ValveResourceFormat reads shows here instead of as an empty index.</summary>
    static string? Unreadable(string dirVpk)
    {
        try
        {
            using var pak = new Package();
            pak.Read(dirVpk);
            if (pak.Entries?.GetValueOrDefault("vcs")?.Where(IsDirectX).MinBy(x => x.Length) is not { } e)
                return $"no DirectX shaders in {Path.GetFileName(dirVpk)}";
            pak.ReadEntry(e, out var data);
            try
            {
                using var program = new VfxProgramData();
                program.Read(e.GetFileName(), new MemoryStream(data));
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return data.Length >= 8 && BitConverter.ToInt32(data, 0) == 0x32736376   // "vcs2", then the version
                    ? $"its shader files are vcs version {BitConverter.ToInt32(data, 4)}, which this build can't read yet"
                    : $"unreadable shader file {e.GetFileName()}";
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return $"{Path.GetFileName(dirVpk)} can't be read ({ex.Message})";
        }
    }

    static IEnumerable<string> Vpks(Game game) => Mods(game).Select(d => Path.Combine(d, "shaders_pc_dir.vpk")).Where(File.Exists);

    /// <summary>Each mod's cfg\boot.vcfg, core first so the game's own comes last. Deadlock writes its render system there
    /// when Vulkan is picked in its settings.</summary>
    static IEnumerable<string> BootVcfgs(Game game) => Mods(game)
        .OrderBy(d => !Path.GetFileName(d).Equals("core", StringComparison.OrdinalIgnoreCase))
        .Select(d => Path.Combine(d, "cfg", "boot.vcfg")).Where(File.Exists);

    static IEnumerable<string> Mods(Game game)
    {
        var root = Path.Combine(game.InstallDir, "game");
        return Directory.Exists(root) ? Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase) : Enumerable.Empty<string>();
    }

    static string Text(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    static readonly Regex RenderSystem = new(@"(?<!\S)-(vulkan|dx11)(?!\S)", RegexOptions.IgnoreCase);
    static readonly Regex DefaultRenderSystemOption = new(@"^\s*""DefaultRenderSystemOption""\s+""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>"D3D11", or "Vulkan (…)" naming what picked it: the launch options' last -vulkan or -dx11, else the last
    /// boot.vcfg DefaultRenderSystemOption ("-vulkan"), else DirectX 11.</summary>
    internal static string Api(string launchOptions, IEnumerable<string> bootVcfgs)
    {
        if (RenderSystem.Matches(launchOptions).LastOrDefault() is { } option) return IsVulkan(option) ? "Vulkan (launch options)" : "D3D11";
        var boot = bootVcfgs.Select(v => DefaultRenderSystemOption.Matches(v).LastOrDefault()?.Groups[1].Value).LastOrDefault(v => v != null) ?? "";
        return RenderSystem.Matches(boot).LastOrDefault() is { } b && IsVulkan(b) ? "Vulkan (boot.vcfg)" : "D3D11";
    }

    static bool IsVulkan(Match m) => m.Groups[1].Value.Equals("vulkan", StringComparison.OrdinalIgnoreCase);
}
