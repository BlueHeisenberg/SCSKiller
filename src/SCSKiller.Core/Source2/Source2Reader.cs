using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unreal;
using ValvePak;
using ValveResourceFormat.CompiledShader;

namespace SCSKiller.Core.Source2;

/// <summary>Valve's Source 2 (Counter-Strike 2, Deadlock, Dota 2): game\bin\win64\engine2.dll, the shaders in each
/// game\&lt;mod&gt;\shaders_pc_dir.vpk. Every VFX program there has a file per stage (name_pc_50_ps.vcs: features, vs, ps, gs,
/// hs, ds, cs, psrs) whose static combos hold the DXBC of their dynamic combos, read with ValveResourceFormat (vcs 72, the
/// one Counter-Strike 2 ships, is a resource file with a KV3 block and the bytecode after it). One map per VFX program,
/// platform PCD3D_SM5: the game runs on DirectX 11 or Vulkan, never DirectX 12. GraphicsApi: the Steam launch options'
/// -vulkan/-dx11, else a mod's cfg\boot.vcfg DefaultRenderSystemOption (Deadlock's launcher writes "-vulkan" there),
/// else D3D11. EngineInfo: Family "Source 2", Version "vcs &lt;n&gt;" (the first program's).</summary>
public sealed class Source2Reader : IEngineReader
{
    public const string Family = "Source 2", Platform = "PCD3D_SM5";
    /// <summary>Newest vcs version ValveResourceFormat reads (VfxProgramData.ThrowIfNotSupported).</summary>
    public const int MaxVcsVersion = 72;

    const int VcsMagic = 0x32736376;   // "vcs2": the binary layout before resource files (version 70)

    /// <summary>A program's stage file on DirectX: the program name, then the stage (features and psrs hold no bytecode).</summary>
    static readonly Regex StageFile = new(@"^(?<program>.+)_pc_\d+_(?:vs|ps|gs|hs|ds|cs)\.vcs$", RegexOptions.IgnoreCase);

    public EngineInfo? Detect(Game game)
    {
        if (!File.Exists(Path.Combine(game.InstallDir, "game", "bin", "win64", "engine2.dll"))) return null;
        var api = Api(game);
        var paks = ShaderPaks(game).ToList();
        if (paks.Count == 0) return new EngineInfo(Family, "", null, api, false, "no shaders_pc_dir.vpk in its mods (a Vulkan-only install?)");
        var version = paks.Select(FirstVersion).FirstOrDefault(v => v > 0);
        return new EngineInfo(Family, version > 0 ? $"vcs {version}" : "", null, api, false,
            version == 0 ? "no DirectX shader programs in its shaders_pc_dir.vpk"
            : version > MaxVcsVersion ? $"shader files of vcs version {version}: SCSKiller reads up to {MaxVcsVersion}" : null);
    }

    /// <summary>The launch options and every mod's boot.vcfg: they pick the render system.</summary>
    public string DetectStamp(Game game, EngineInfo? engine) =>
        UnrealRhi.LaunchOptions(game) + "|" + string.Join(';', BootConfigs(game).Select(f => new FileInfo(f)).Select(f => $"{f.FullName}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"));

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var shaders = new Dictionary<string, ShaderInfo>();
        var maps = new Dictionary<string, ShaderMap>();
        var stamp = new StringBuilder();
        int bad = 0, failed = 0;
        string? firstError = null;
        foreach (var path in ShaderPaks(game))
        {
            var rel = Path.GetRelativePath(game.InstallDir, path);
            // the dir VPK lists every file with its CRC: its bytes change exactly when a shader file does
            using (var f = Open(path)) stamp.Append($"{rel}|{Convert.ToHexStringLower(SHA1.HashData(f))}\n");
            var programs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var files = 0;
            foreach (var (name, program, data) in StageFiles(path, ct))
            {
                files++;
                var shas = programs.TryGetValue(program, out var l) ? l : programs[program] = [];
                List<byte[]> containers;
                try { containers = Containers(name, data, ct).ToList(); }   // all or none: ReadShaders skips a file that fails
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    failed++;
                    firstError ??= $"{name}: {e.Message}";
                    continue;
                }
                foreach (var c in containers)
                {
                    var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                    try
                    {
                        if (!shaders.ContainsKey(sha) && ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info)
                            shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                    }
                    catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { bad++; } // valid container, odd program: not usable
                    if (shaders.ContainsKey(sha)) shas.Add(sha);
                }
            }
            foreach (var (program, list) in programs)
            {
                var shas = list.Distinct().ToList();
                if (shas.Count == 0) continue;
                var h = CarvedReader.Sha1Hex($"{Platform}|{string.Join(',', shas)}");
                maps.TryAdd(h, new ShaderMap(h, $"{rel}:{program}", Platform, shas));
            }
            log?.Report($"{rel}: {files} stage files of {programs.Count} programs");
        }
        if (failed > 0) log?.Report($"{failed} stage files not read, the first: {firstError}");
        if (shaders.Count == 0 && failed > 0) throw new InvalidDataException($"no stage file could be read ({firstError})");
        log?.Report($"{shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"){(bad > 0 ? $", {bad} unparseable" : "")} -> {maps.Count} maps ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(CarvedReader.Sha1Hex(stamp.ToString()), maps.Count > 0 ? [Platform] : [], shaders, maps.Values.ToList());
    }

    /// <summary>Reads the stage files again (about a minute for Counter-Strike 2) and serves each requested shader once.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        var left = new HashSet<string>(sha1s);
        foreach (var path in ShaderPaks(game))
            foreach (var (name, _, data) in StageFiles(path, ct))
            {
                List<byte[]> containers;
                try { containers = Containers(name, data, ct).ToList(); }
                catch (Exception e) when (e is not OperationCanceledException) { continue; }   // Index left it out too
                foreach (var c in containers)
                    if (Convert.ToHexStringLower(SHA1.HashData(c)) is var sha && left.Remove(sha))
                    {
                        sink(sha, c);
                        if (left.Count == 0) return;
                    }
            }
    }

    /// <summary>Every mod's shaders_pc_dir.vpk (game\csgo, game\core, ...), in name order.</summary>
    internal static IEnumerable<string> ShaderPaks(Game game)
    {
        var root = Path.Combine(game.InstallDir, "game");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, "shaders_pc_dir.vpk")).Where(File.Exists)
            .Order(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The DirectX stage files of a shader VPK: file name, VFX program and bytes, one at a time.</summary>
    static IEnumerable<(string Name, string Program, byte[] Data)> StageFiles(string path, CancellationToken ct)
    {
        using var package = new Package();
        package.Read(path);
        if (package.Entries?.GetValueOrDefault("vcs") is not { } entries) yield break;
        foreach (var entry in entries.OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.GetFileName();
            if (StageFile.Match(name) is not { Success: true } m) continue;
            package.ReadEntry(entry, out var data);
            yield return (name, m.Groups["program"].Value, data);
        }
    }

    /// <summary>The DXBC containers of every dynamic combo of every static combo of one stage file.</summary>
    internal static IEnumerable<byte[]> Containers(string name, byte[] data, CancellationToken ct)
    {
        using var program = new VfxProgramData();
        program.Read(name, new MemoryStream(data, writable: false));
        foreach (var entry in program.StaticComboEntries.Values)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var file in entry.Unserialize().ShaderFiles)
                if (file is { Bytecode.Length: > 0 })
                    foreach (var (_, c) in Dxbc.Containers(file.Bytecode)) yield return c;
        }
    }

    /// <summary>The vcs version of a VPK's first stage file, 0 when it has none or it can't be read: a resource file
    /// (version 70 on) has its version at 6, the older binary layout ("vcs2") at 4.</summary>
    static int FirstVersion(string path)
    {
        try
        {
            var first = StageFiles(path, CancellationToken.None).FirstOrDefault();
            if (first.Data is not { Length: >= 8 } d) return 0;
            return BinaryPrimitives.ReadInt32LittleEndian(d) == VcsMagic ? BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(4)) : BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(6));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return 0; }
    }

    /// <summary>"Vulkan (why)" or "D3D11[ (why)]": -vulkan or -dx11 in the launch options, else the first boot.vcfg that
    /// sets DefaultRenderSystemOption.</summary>
    internal static string Api(Game game)
    {
        var options = UnrealRhi.LaunchOptions(game);
        foreach (Match m in Regex.Matches(options, @"(?:^|\s)-(vulkan|dx11)(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.RightToLeft))
            return m.Groups[1].Value.Equals("vulkan", StringComparison.OrdinalIgnoreCase) ? "Vulkan (launch option -vulkan)" : "D3D11 (launch option -dx11)";
        foreach (var file in BootConfigs(game))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            var m = Regex.Match(text, @"""DefaultRenderSystemOption""\s+""\s*-?(\w+)", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var where = Path.GetRelativePath(game.InstallDir, file);
            return m.Groups[1].Value.Equals("vulkan", StringComparison.OrdinalIgnoreCase) ? $"Vulkan ({where})" : "D3D11";
        }
        return "D3D11";
    }

    static IEnumerable<string> BootConfigs(Game game)
    {
        var root = Path.Combine(game.InstallDir, "game");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, "cfg", "boot.vcfg")).Where(File.Exists).Order(StringComparer.OrdinalIgnoreCase);
    }

    static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
