namespace SCSKiller.Core.Vendors;

/// <summary>Intel GPUs, experimental and opt-in (<see cref="Settings.ExperimentalIntel"/>). Caps measured on Intel Arc only
/// (ARCHITECTURE.md, Intel): the D3D12 and D3D11 caches are keyed on the exe file name, path-independent, per stage
/// (<see cref="Planning.UnitPolicy.Intel"/>) but with the PS on its exact render-target formats and blend desc, and capped
/// at <see cref="PerGameCap"/> per game. DriverVersion is DXGI's user-mode version, which is Intel's own notation.</summary>
public sealed class IntelBackend(GpuInfo dxgi) : IGpuVendorBackend, IRefreshableGpu
{
    public const string Profile = "intel-1";

    /// <summary>Past it the driver evicts that game's own entries. Not configurable.</summary>
    public const long PerGameCap = 512L << 20;

    public GpuVendor Vendor => GpuVendor.Intel;
    public GpuInfo Gpu { get; private set; } = dxgi;

    public bool Refresh(GpuInfo adapter)
    {
        Gpu = adapter;
        return adapter.DriverVersion.Length > 0;
    }

    public string FallbackVersion(string umd) => umd;

    // WholeObject: flat state objects share nothing with cached collections, so only recorded objects are replayed.
    public VendorCaps Caps { get; } = new(Profile, CacheKeyedByExeName: true, StateIndependentCache: false, CacheSizeConfigurable: false,
        PerStageCache: true, RtCacheGranularity: RtCacheGranularity.WholeObject, PerGameCacheCap: PerGameCap);

    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>One file per exe name, named by a 64-hex hash that isn't derivable from the name alone.</summary>
    public static string CacheDir => Path.Combine(LocalAppData + "Low", "Intel", "ShaderCache");

    public CacheUsage GetCacheUsage() => new(CacheDir, AmdBackend.Bytes(CacheDir), UpperBound: false);

    public CacheLimit? GetCacheLimit() => null;

    public void SetCacheLimit(CacheLimit limit) =>
        throw new NotSupportedException($"the Intel shader cache size is not configurable ({Gpu.Name})");
}
