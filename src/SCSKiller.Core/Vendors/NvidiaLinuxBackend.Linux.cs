using SCSKiller.Core.App;

namespace SCSKiller.Core.Vendors;

/// <summary>NVIDIA's Linux driver, under which Proton runs the games and the warm. Each game's cache is a folder of its own
/// (<see cref="NvidiaLinuxAppCache"/>) capped at <see cref="NvidiaLinux.PlacedCacheSize"/> (<see cref="NvidiaLinux.CacheSize"/>
/// for a game on SCSKiller's old launch line), a size SCSKiller sets, not the user.
/// The driver version is Vulkan's, which names the user-mode driver this process would load.</summary>
public sealed class NvidiaLinuxBackend(GpuInfo adapter, string? gamesDir = null, string moduleVersionFile = NvidiaLinux.ModuleVersionFile)
    : IGpuVendorBackend, IRefreshableGpu
{
    public GpuVendor Vendor => GpuVendor.Nvidia;
    public GpuInfo Gpu { get; private set; } = adapter;
    public VendorCaps Caps => NvidiaLinux.Caps;

    public NvidiaLinuxAppCache AppCache { get; } = new(gamesDir ?? Path.Combine(AppStore.DefaultDir, "games"));
    IAppCache? IGpuVendorBackend.AppCache => AppCache;

    /// <summary>False (incomplete) while the loaded kernel module isn't the user-mode driver's version: an update that
    /// waits for a reboot.</summary>
    public bool Refresh(GpuInfo adapter)
    {
        Gpu = adapter;
        return adapter.DriverVersion.Length > 0 && ModuleVersion() is { } module && NvidiaLinux.SameVersion(module, adapter.DriverVersion);
    }

    public string FallbackVersion(string umd) => umd;

    string? ModuleVersion()
    {
        try { return NvidiaLinux.ModuleVersion(File.ReadAllText(moduleVersionFile)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public CacheUsage GetCacheUsage() => new(AppCache.GamesDir, AppCache.Usage(), UpperBound: false);

    public CacheLimit? GetCacheLimit() => new(NvidiaLinux.PlacedCacheSize, IsDriverDefault: false);

    public void SetCacheLimit(CacheLimit limit) =>
        throw new NotSupportedException($"on Linux SCSKiller gives each game {Format.Bytes(NvidiaLinux.PlacedCacheSize)} of NVIDIA shader cache ({Gpu.Name})");
}
