using System.Globalization;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Vendors;

/// <summary>Mesa's Vulkan drivers on Linux, RADV (AMD), ANV (Intel) and NVK (NVIDIA), as Proton runs the games and the warm on them. Mesa
/// keys its disk cache on what it compiles and the driver's build, never on a process or an app name (Steam's pre-cache
/// relies on it): a warm that writes the folder the game reads is found by the game, so the warm and the game take the same variables
/// (<see cref="CacheEnvironment"/>). The folder is the one Steam gives the game (<see cref="SteamCache"/>). ANV and NVK take
/// RADV's path with their cache behaviour unmeasured (<see cref="GpuBackends.DriverNote"/>).</summary>
public static class MesaLinux
{
    public const string Profile = "mesa-linux-1";

    /// <summary>Whole pipelines from a recording with draws, as on NVIDIA's Linux driver (<see cref="NvidiaLinux.Caps"/>): what
    /// Mesa keys under vkd3d-proton is unmeasured. CacheKeyedByExeName: content keys reach the game's cache from the staged
    /// exe as from any process. A Vulkan game's, native or under Proton, is warmed by the native replayer under the game's
    /// own variables (<see cref="Vulkan.VulkanPlan.ContentKey"/>), every pipeline of its plan: the single file has no size
    /// limit (Mesa's disk_cache.c reads no MESA_SHADER_CACHE_MAX_SIZE for it) and 64-bit offsets, so no plan has a cache budget.</summary>
    public static VendorCaps Caps { get; } = new(Profile, CacheKeyedByExeName: true, StateIndependentCache: false, CacheSizeConfigurable: false,
        PerStageCache: false, VulkanKey: Vulkan.VulkanPlan.ContentKey);

    public static bool Is(VendorCaps caps) => caps.Profile == Profile;

    /// <summary>VkDriverId values (VkPhysicalDeviceDriverProperties::driverID).</summary>
    public const uint AmdProprietary = 1, AmdOpenSource = 2, Radv = 3, NvidiaProprietary = 4, Anv = 6, Llvmpipe = 13, Nvk = 24;

    /// <summary>Why SCSKiller doesn't compile on <paramref name="a"/>'s Vulkan driver, for the Library's notice; null for RADV,
    /// ANV, NVK and NVIDIA's own driver (also when Vulkan doesn't name NVIDIA's), and for an unknown vendor, whose notice says it.</summary>
    public static string? Refusal(DxgiAdapter a) => (a.Gpu.Vendor, a.Driver) switch
    {
        (_, Llvmpipe) => "SCSKiller doesn't compile on a software renderer.",
        (GpuVendor.Amd, AmdOpenSource) => "SCSKiller compiles on AMD GPUs with Mesa's RADV driver, not with AMDVLK.",
        (GpuVendor.Amd, AmdProprietary) => "SCSKiller compiles on AMD GPUs with Mesa's RADV driver, not with AMD's own Vulkan driver.",
        (GpuVendor.Nvidia, not (0 or NvidiaProprietary or Nvk)) => "SCSKiller compiles on NVIDIA GPUs with NVIDIA's own driver or Mesa's NVK only.",
        (GpuVendor.Amd, 0) or (GpuVendor.Intel, 0) => "SCSKiller can't tell which Vulkan driver runs this GPU, so it doesn't compile on it.",
        (GpuVendor.Amd, not Radv) => "SCSKiller compiles on AMD GPUs with Mesa's RADV driver only.",
        (GpuVendor.Intel, not Anv) => "SCSKiller compiles on Intel GPUs with Mesa's ANV driver only.",
        _ => null,
    };

    /// <summary>The variables that point Mesa at a Steam game's cache, as Steam sets them for the game: the game's folder in the
    /// shadercache of its library's steamapps folder <paramref name="steamApps"/>, in Mesa's single-file (Fossilize) form.
    /// Steam's own pre-cache writes there with them, and a game Steam launches under Proton gets the same (measured). Steam's
    /// read-only caches are read, not compiled again: Mesa skips one whose files aren't there.</summary>
    public static List<(string Name, string Value)> SteamCache(string steamApps, uint appId) =>
        [("MESA_SHADER_CACHE_DIR", Path.Combine(steamApps, "shadercache", appId.ToString(CultureInfo.InvariantCulture))), ("MESA_DISK_CACHE_SINGLE_FILE", "1"),
            ("MESA_DISK_CACHE_READ_ONLY_FOZ_DBS", "steam_cache,steam_precompiled")];

    /// <summary>A file of Steam's own in a game's Mesa folder: its pre-cache's read-only databases, steam_cache, steam_precompiled
    /// and steam_precompiled_&lt;hash&gt; (measured), which Steam replaces and rotates.</summary>
    public static bool SteamsFile(string name) => name.StartsWith("steam_", StringComparison.Ordinal);

    /// <summary>The folder Mesa keeps its cache in under these variables: its type's folder in MESA_SHADER_CACHE_DIR, as Mesa
    /// picks the type (single file, else the database, else a file per entry). Null without MESA_SHADER_CACHE_DIR.</summary>
    public static string? Folder(IEnumerable<(string Name, string Value)> cache)
    {
        var vars = cache.GroupBy(v => v.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
        if (vars.GetValueOrDefault("MESA_SHADER_CACHE_DIR") is not { Length: > 0 } dir) return null;
        bool On(string name) => vars.GetValueOrDefault(name)?.ToLowerInvariant() is "1" or "true" or "yes" or "y";
        return Path.Combine(dir, On("MESA_DISK_CACHE_SINGLE_FILE") ? "mesa_shader_cache_sf"
            : On("MESA_DISK_CACHE_DATABASE") && !On("MESA_DISK_CACHE_MULTI_FILE") ? "mesa_shader_cache_db" : "mesa_shader_cache");
    }

    /// <summary>The steamapps folder of the Steam library that holds <paramref name="installDir"/> (a game's
    /// steamapps/common/&lt;folder&gt;); null outside one.</summary>
    public static string? SteamApps(string installDir)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir)) + Path.DirectorySeparatorChar;
        var at = full.IndexOf(SteamSource.CommonPart, StringComparison.OrdinalIgnoreCase);
        return at < 0 || full.Length == at + SteamSource.CommonPart.Length ? null : full[..(at + SteamSource.AppsPart.Length)];
    }

    /// <summary>The warm's and the game's variables, the same for both: <paramref name="cache"/> (<see cref="SteamCache"/>) and
    /// vkd3d-proton's cache in the game's proton-cache folder under
    /// <paramref name="gameDir"/>, shared as on NVIDIA. Makes the folders unless <paramref name="make"/> is false: Mesa keeps no
    /// cache in a MESA_SHADER_CACHE_DIR that doesn't exist.</summary>
    public static List<(string Name, string Value)> CacheEnvironment(string gameDir, IReadOnlyList<(string Name, string Value)> cache, bool make = true)
    {
        var vkd3d = Path.GetFullPath(Path.Combine(gameDir, "proton-cache", "vkd3d"));
        if (make)
        {
            Directory.CreateDirectory(vkd3d);
            foreach (var (name, value) in cache)
                if (name == "MESA_SHADER_CACHE_DIR") Directory.CreateDirectory(value);
        }
        return [("VKD3D_SHADER_CACHE_PATH", vkd3d), .. cache];
    }

    /// <summary>The steamapps folder whose shadercache Steam gives <paramref name="game"/> a folder in, and its app id: a Steam
    /// game's library's, a non-Steam game's (<see cref="SteamShortcuts"/>) the Steam folder <paramref name="steamRoot"/>'s own
    /// (measured). Null for any other game.</summary>
    public static (string SteamApps, uint App)? SteamFolder(Game game, string steamRoot) =>
        SteamShortcuts.AppId(game.Id) is { } shortcut ? (Path.Combine(steamRoot, "steamapps"), shortcut)
        : SteamApps(game.InstallDir) is { } apps && SteamSource.AppId(game.Id) is { } app ? (apps, app) : null;

    /// <summary>A Steam game's or a non-Steam game's <see cref="CacheEnvironment"/>, its folder <see cref="SteamFolder"/>'s under
    /// <paramref name="steamRoot"/> (default: Steam's); InvalidOperationException for any other game.</summary>
    public static List<(string Name, string Value)> CacheEnvironment(string gameDir, Game game, bool make = true, string? steamRoot = null) =>
        SteamFolder(game, steamRoot ?? SteamSource.Root()) is var (apps, app) ? CacheEnvironment(gameDir, SteamCache(apps, app), make)
            : throw new InvalidOperationException($"{game.Name} isn't a game of a Steam library");

    /// <summary>The installed apps of the Steam folder <paramref name="root"/>'s libraries, by their manifests: each with its
    /// library's steamapps folder. A library that can't be listed is left out, or with <paramref name="strict"/> throws
    /// InvalidOperationException. A missing one has no apps. Then its non-Steam games, in its own steamapps folder.</summary>
    public static IEnumerable<(string SteamApps, uint AppId)> SteamGames(string root, bool strict = false)
    {
        foreach (var library in SteamSource.Libraries(root))
        {
            var apps = Path.Combine(library, "steamapps");
            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(apps, "appmanifest_*.acf").ToList(); }
            catch (DirectoryNotFoundException) { continue; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (strict) throw new InvalidOperationException($"files can't be listed ({e.Message})", e);
                continue;
            }
            foreach (var m in manifests)
                if (uint.TryParse(Path.GetFileNameWithoutExtension(m)["appmanifest_".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                    yield return (apps, id);
        }
        foreach (var s in SteamShortcuts.All(root)) yield return (Path.Combine(root, "steamapps"), s.AppId);
    }
}
