using SCSKiller.Core.App;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Vendors;

/// <summary>Mesa's RADV, ANV and NVK drivers (<paramref name="driver"/>, a VkDriverId), under which Proton runs the games
/// and the warm (<see cref="MesaLinux"/>). Each Steam game's cache is its folder in Steam's shadercache
/// (<see cref="MesaLinuxAppCache"/>) of the libraries of <paramref name="steamRoot"/> (default: Steam's, <see cref="SteamSource.Root()"/>), one file
/// with no size limit (<see cref="MesaLinux.CacheSize"/>). The driver version is Vulkan's with the driver build's tag
/// (<see cref="GpuBackends.MesaVersion"/>): Mesa's cache keys change with every build.</summary>
public sealed class MesaLinuxBackend(GpuInfo adapter, string? steamRoot = null, uint driver = MesaLinux.Radv) : IGpuVendorBackend, IRefreshableGpu
{
    readonly string _root = steamRoot ?? SteamSource.Root();

    public uint Driver => driver;

    public GpuVendor Vendor => Gpu.Vendor;
    public GpuInfo Gpu { get; private set; } = adapter;
    public VendorCaps Caps => MesaLinux.Caps;

    public MesaLinuxAppCache AppCache => field ??= new(strict => MesaLinux.SteamGames(_root, strict));
    IAppCache? IGpuVendorBackend.AppCache => AppCache;

    /// <summary>Each process loads the installed Mesa: a new build has nothing to wait for, unlike NVIDIA's kernel module.</summary>
    public bool Refresh(GpuInfo adapter)
    {
        Gpu = adapter;
        return adapter.DriverVersion.Length > 0;
    }

    public string FallbackVersion(string umd) => umd;

    public CacheUsage GetCacheUsage() => new(Path.Combine(_root, "steamapps", "shadercache"), AppCache.Usage(), UpperBound: false);

    public CacheLimit? GetCacheLimit() => new(null, IsDriverDefault: false);

    public void SetCacheLimit(CacheLimit limit) =>
        throw new NotSupportedException($"on Linux Mesa's shader cache has no size limit ({Gpu.Name})");
}
