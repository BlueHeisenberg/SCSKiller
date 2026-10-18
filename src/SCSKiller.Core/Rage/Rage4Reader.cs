namespace SCSKiller.Core.Rage;

/// <summary>GTA IV / RAGE 4 PC: loose RAGE FXC effects containing D3D9 SM3 bytecode. When FusionFix selects its
/// Vulkan renderer, vulkan.dll is DXVK's 32-bit D3D9 implementation. The dedicated x86 warm backend replays these
/// shaders through that exact DLL under the GTAIV.exe process name, filling NVIDIA's Vulkan shader cache.</summary>
public sealed class Rage4Reader : IEngineReader
{
    public const string Family = "RAGE D3D9";
    public const string Version = "GTA IV FXC";
    public const string Api = "D3D9 (DXVK)";
    public const string SteamId = "steam:12210";

    static string ExeDir(Game game) => Path.GetDirectoryName(Path.GetFullPath(game.ExePath))!;

    public EngineInfo? Detect(Game game)
    {
        if (!string.Equals(game.Id, SteamId, StringComparison.OrdinalIgnoreCase)) return null;
        var dir = ExeDir(game);
        var shaders = Path.Combine(dir, "common", "shaders");
        if (!Directory.Exists(shaders) || !Directory.EnumerateFiles(shaders, "*.fxc", SearchOption.AllDirectories).Any())
            return new(Family, Version, null, "D3D9", false, "GTA IV FXC shaders were not found");

        var dxvk = Path.Combine(dir, "vulkan.dll");
        var cfg = Path.Combine(dir, "d3d9.cfg");
        var vulkan = File.Exists(dxvk) && File.Exists(cfg) && File.ReadLines(cfg).Any(l =>
            l.Trim().Equals("API=1", StringComparison.OrdinalIgnoreCase));
        return vulkan
            ? new(Family, Version, null, Api, false, null, NoRtPipelines: true, NoRayTracing: true)
            : new(Family, Version, null, "D3D9", false, "GTA IV support requires FusionFix with Graphics API set to Vulkan (DXVK)", NoRtPipelines: true, NoRayTracing: true);
    }

    public string DetectStamp(Game game, EngineInfo? engine)
    {
        try
        {
            var dir = ExeDir(game);
            return string.Join("|", new[] { "vulkan.dll", "d3d9.cfg" }.Select(n => Path.Combine(dir, n)).Select(p =>
                File.Exists(p) ? $"{p}:{new FileInfo(p).Length}:{File.GetLastWriteTimeUtc(p).Ticks}" : $"{p}:-"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return ""; }
    }

    public string IndexStamp(Game game)
    {
        try
        {
            var dir = ExeDir(game);
            var files = Directory.EnumerateFiles(Path.Combine(dir, "common", "shaders"), "*.fxc", SearchOption.AllDirectories)
                .Concat(Directory.Exists(Path.Combine(dir, "update", "common", "shaders"))
                    ? Directory.EnumerateFiles(Path.Combine(dir, "update", "common", "shaders"), "*.fxc", SearchOption.AllDirectories)
                    : []);
            long bytes = 0, latest = 0; int count = 0;
            foreach (var f in files) { var i = new FileInfo(f); count++; bytes += i.Length; latest = Math.Max(latest, i.LastWriteTimeUtc.Ticks); }
            return $"{count}:{bytes}:{latest}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return ""; }
    }

    public int ReaderVersion(EngineInfo engine) => 1;

    // The dedicated D3D9/DXVK backend consumes the FXC files directly, so the generic D3D11/12 planner never uses this index.
    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) =>
        new(ShaderIndex.EmptyContentHash, ["D3D9_SM3_DXVK"], new Dictionary<string, ShaderInfo>(), []);

    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
}
