using System.Globalization;
using System.Text.Json;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Vendors;

/// <summary>The GPU a game's own launch runs it on: the variables of its Steam launch options (a non-Steam game's own) or its
/// launcher's settings that Mesa's device selection layer and the Vulkan loader read. Only the forms that name one GPU
/// whatever the order the game's Vulkan lists them in are told: the drivers VK_DRIVER_FILES (else VK_ICD_FILENAMES) loads,
/// then MESA_VK_DEVICE_SELECT's vendor:device or else DRI_PRIME's PCI address or vendor:device. A numeric DRI_PRIME picks by
/// the display a process connects to, which SCSKiller can't see: told only on a PC of one GPU.</summary>
public static class LaunchGpu
{
    /// <summary>Whether SCSKiller's environment (<paramref name="get"/>) hides GPUs the game's own variables
    /// (<paramref name="game"/>) may not: a driver list or a device choice that exposes one GPU alone, set to another value
    /// than the game's, Mesa's forced default device, or a numeric DRI_PRIME exposing one GPU alone, whose GPU depends on the
    /// display a process connects to.</summary>
    public static bool Hides(Func<string, string?> get, IReadOnlyList<(string Name, string Value)> game)
    {
        string? Game(string name) => game.LastOrDefault(v => v.Name == name).Value;
        if (get("MESA_VK_DEVICE_SELECT_FORCE_DEFAULT_DEVICE") is { Length: > 0 }) return true;
        if (get("DRI_PRIME") is { } prime && prime.EndsWith('!') && int.TryParse(prime.TrimEnd('!'), NumberStyles.None, CultureInfo.InvariantCulture, out _)) return true;
        return new[] { "VK_DRIVER_FILES", "VK_ICD_FILENAMES", "MESA_VK_DEVICE_SELECT", "DRI_PRIME" }.Any(name => get(name) is { Length: > 0 } own
            && (name.StartsWith("VK_", StringComparison.Ordinal) || own.EndsWith('!')) && own != Game(name));
    }

    public static bool Is(string name) => name is "MESA_VK_DEVICE_SELECT" or "DRI_PRIME" or "VK_DRIVER_FILES" or "VK_ICD_FILENAMES";

    /// <summary>The game's own GPU variables: a Steam game's or a non-Steam game's from its launch options in
    /// <paramref name="steamRoot"/>, a launcher's game's from the launcher's settings; none for any other game.</summary>
    public static List<(string Name, string Value)> Of(Game g, string steamRoot) => [.. Own(g, steamRoot).Where(v => Is(v.Name))];

    /// <summary>The game's own VKD3D_CONFIG, read where <see cref="Of"/> reads; null: none. Its options change the driver
    /// keys of vkd3d-proton's pipelines (descriptor_heap does), so a warm in another mode fills entries the game never reads.</summary>
    public static (string Name, string Value)? Vkd3dConfig(Game g, string steamRoot) =>
        Own(g, steamRoot).Where(v => v.Name == "VKD3D_CONFIG").Select(v => ((string, string)?)v).LastOrDefault();

    static IEnumerable<(string Name, string Value)> Own(Game g, string steamRoot)
    {
        var id = SteamSource.AppId(g.Id) ?? SteamShortcuts.AppId(g.Id);
        var options = SteamSource.AppId(g.Id) is { } app ? SteamSource.LaunchOptions(steamRoot, app)
            : SteamShortcuts.AppId(g.Id) is { } shortcut ? SteamShortcuts.Find(steamRoot, shortcut)?.LaunchOptions : null;
        if (options == null) return LauncherPrefix.Is(g) && LauncherPrefix.Of(g, out _) is { Env: { } env } ? env : [];
        var own = SteamLaunchOptions.Variables(options, out _);
        // Armada's wrapper sets the game's Turnip build itself (its tweaks' env applies over the options' variables)
        return ArmadaLaunch.Wraps(options) && id is { } appId ? [.. own, .. ArmadaLaunch.Variables(appId, own)] : own;
    }

    /// <summary>The adapter of <paramref name="all"/> the variables run the game on; null when they pick none (the game takes
    /// the default one, as the warm does). <paramref name="why"/>: what keeps the GPU from being told (any other form, or one
    /// that matches no GPU or several), or with <paramref name="software"/> the refusal of a software renderer.</summary>
    public static DxgiAdapter? Pick(IReadOnlyList<(string Name, string Value)> vars, IReadOnlyList<DxgiAdapter> all, out string? why, out bool software)
    {
        (why, software) = (null, false);
        string? Var(string name) => vars.LastOrDefault(v => v.Name == name).Value is { Length: > 0 } v ? v : null;
        var candidates = all;
        var files = Var("VK_DRIVER_FILES") ?? Var("VK_ICD_FILENAMES");
        if (files != null)
        {
            var drivers = files.Split(':', StringSplitOptions.RemoveEmptyEntries).SelectMany(DriversIn).ToList();
            if (drivers.Count > 0 && drivers.All(d => d == MesaLinux.Llvmpipe)) return Software(out why, out software);
            candidates = [.. all.Where(a => drivers.Contains(DriverOf(a)))];
            if (drivers.Contains(0u) || candidates.Count == 0) return Unknown(files, out why);
        }
        var (select, prime) = (Var("MESA_VK_DEVICE_SELECT"), Var("DRI_PRIME"));
        if (select != null && prime != null) return Unknown($"{select} with DRI_PRIME={prime}", out why);
        Func<DxgiAdapter, bool>? match = null;
        if (select != null)
        {
            if (select.TrimEnd('!') == "cpu") return Software(out why, out software);
            match = ById(select) ?? (_ => false);
        }
        else if (prime != null)
        {
            var tag = prime.TrimEnd('!');
            if (int.TryParse(tag, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                // Mesa's layer moves away from the display's GPU, which SCSKiller can't see
                if (n > 0 && candidates.Select(a => (a.Gpu.AdapterLuid, a.Gpu.Vendor, a.DeviceId)).Distinct().Count() > 1) return Unknown($"DRI_PRIME={prime}", out why);
            }
            else match = (tag.StartsWith("pci-", StringComparison.Ordinal) ? ByPci(tag[4..]) : ById(tag)) ?? (_ => false);
        }
        if (match == null) return candidates.Count == all.Count ? null : candidates.Count == 1 ? candidates[0] : Unknown(files!, out why);
        var matched = candidates.Where(match).ToList();
        // none: a GPU SCSKiller's own environment may hide; several: two drivers of a GPU, or two of a model, in the game's order
        return matched.Count == 1 ? matched[0] : Unknown(select ?? $"DRI_PRIME={prime}", out why);
    }

    static DxgiAdapter? Software(out string? why, out bool software)
    {
        (why, software) = ("SCSKiller doesn't compile on a software renderer.", true);
        return null;
    }

    static DxgiAdapter? Unknown(string what, out string? why)
    {
        why = what;
        return null;
    }

    /// <summary>The VkDriverId an adapter's driver has, NVIDIA's own when its driver doesn't say.</summary>
    static uint DriverOf(DxgiAdapter a) => a.Driver != 0 ? a.Driver : a.Gpu.Vendor == GpuVendor.Nvidia ? MesaLinux.NvidiaProprietary : 0;

    // "vendor:device" in hex, as Mesa's layer scans it; null for any other value
    static Func<DxgiAdapter, bool>? ById(string value)
    {
        var parts = value.TrimEnd('!').Split(':');
        return parts.Length == 2 && uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vendor)
            && uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var device)
            ? a => (uint)a.Gpu.Vendor == vendor && a.DeviceId == device : null;
    }

    // "dddd_bb_dd_f", DRI_PRIME's form of a PCI address; null for any other value
    static Func<DxgiAdapter, bool>? ByPci(string value)
    {
        var p = value.Split('_');
        var x = p.Select(v => uint.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n) ? n : (uint?)null).ToList();
        return p.Length == 4 && x.All(n => n != null) && GpuBackends.PciAddress(x[0]!.Value, x[1]!.Value, x[2]!.Value, x[3]!.Value) is var pci
            ? a => a.Gpu.AdapterLuid != 0 && a.Gpu.AdapterLuid == pci : null;
    }

    /// <summary>The Turnip library the game's own driver files (<paramref name="vars"/>: VK_DRIVER_FILES, else
    /// VK_ICD_FILENAMES) load on arm64: the first manifest that is there naming an aarch64 libvulkan_freedreno.so by path
    /// (relative to the manifest's folder, as the loader takes it), its full path. Null: none (the system's driver: a bare
    /// name is the loader's search path), or not arm64. Armada names a game's picked build so (<see cref="ArmadaLaunch"/>).</summary>
    internal static string? TurnipLibrary(IReadOnlyList<(string Name, string Value)> vars)
    {
        if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.Arm64) return null;
        string? Var(string name) => vars.LastOrDefault(v => v.Name == name).Value is { Length: > 0 } v ? v : null;
        if ((Var("VK_DRIVER_FILES") ?? Var("VK_ICD_FILENAMES")) is not { } files) return null;
        foreach (var m in files.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!File.Exists(m)) continue;
            string? library = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(m));
                library = doc.RootElement.GetProperty("ICD").GetProperty("library_path").GetString();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
            if (library == null || !Path.GetFileName(library).StartsWith("libvulkan_freedreno", StringComparison.Ordinal) || !library.Contains('/')) continue;
            var path = Path.GetFullPath(library, Path.GetDirectoryName(Path.GetFullPath(m))!);
            if (Aarch64(path)) return path;
        }
        return null;
    }

    static bool Aarch64(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> h = stackalloc byte[20];
            return f.Read(h) == 20 && h[0] == 0x7F && h[1] == (byte)'E' && h[2] == (byte)'L' && h[3] == (byte)'F' && h[4] == 2 && h[18] == 0xB7 && h[19] == 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The VkDriverIds of the drivers a VK_DRIVER_FILES entry loads, a driver manifest or a folder of them, by the
    /// library each names; 0 for one SCSKiller can't tell.</summary>
    static IEnumerable<uint> DriversIn(string path)
    {
        List<string> manifests;
        try { manifests = Directory.Exists(path) ? [.. Directory.EnumerateFiles(path, "*.json").Order(StringComparer.Ordinal)] : [path]; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { manifests = [path]; }
        foreach (var m in manifests)
        {
            // the loader skips a manifest that isn't there: on arm64 Armada lists the FEX guest's x86 Turnip manifests on the
            // host too (elsewhere one still leaves the driver unknown, as before)
            if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 && !File.Exists(m)) continue;
            string? library = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(m));
                library = doc.RootElement.GetProperty("ICD").GetProperty("library_path").GetString();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
            yield return Path.GetFileName(library ?? "") switch
            {
                var f when f.StartsWith("libvulkan_radeon", StringComparison.Ordinal) => MesaLinux.Radv,
                var f when f.StartsWith("libvulkan_intel", StringComparison.Ordinal) => MesaLinux.Anv,
                var f when f.StartsWith("libvulkan_nouveau", StringComparison.Ordinal) => MesaLinux.Nvk,
                var f when f.StartsWith("libvulkan_freedreno", StringComparison.Ordinal) => MesaLinux.Turnip,
                var f when f.StartsWith("libvulkan_lvp", StringComparison.Ordinal) => MesaLinux.Llvmpipe,
                var f when f.StartsWith("libGLX_nvidia", StringComparison.Ordinal) || f.StartsWith("libnvidia-vulkan", StringComparison.Ordinal) => MesaLinux.NvidiaProprietary,
                var f when f.StartsWith("amdvlk", StringComparison.Ordinal) => MesaLinux.AmdOpenSource,
                _ => 0u,
            };
        }
    }
}
