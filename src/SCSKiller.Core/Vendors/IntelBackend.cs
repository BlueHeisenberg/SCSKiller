namespace SCSKiller.Core.Vendors;

/// <summary>Intel (Arc and Xe graphics). Experimental and opt-in (<see cref="Enabled"/>) until a warm is checked on games.
/// Caps measured with tools/intel-arc/measure.ps1 on an Arc B580, driver 32.0.101.9034 (ARCHITECTURE.md, Intel): the D3D12
/// cache is keyed on the exe file name, path-independent; per stage (<see cref="Planning.UnitPolicy.Intel"/>), but the PS
/// on its exact render-target formats and blend desc, so not state-independent; ray tracing collections are cached on
/// their own and link in ~10 ms (selftest dxr: 5 collections never linked before, 1.5 s cold).
///
/// Driver version = DXGI's user-mode driver version ("32.0.101.6979"), which is Intel's own notation.</summary>
public sealed class IntelBackend(GpuInfo dxgi) : IGpuVendorBackend, IRefreshableGpu
{
    /// <summary>Set to 1 to use this backend on an Intel GPU; otherwise Intel stays <see cref="UnsupportedVendor"/>.</summary>
    public const string EnableVariable = "SCSKILLER_EXPERIMENTAL_INTEL";

    public static bool Enabled => Environment.GetEnvironmentVariable(EnableVariable) == "1";

    public GpuVendor Vendor => GpuVendor.Intel;
    public GpuInfo Gpu { get; private set; } = dxgi;

    public bool Refresh(GpuInfo adapter)
    {
        Gpu = adapter;
        return adapter.DriverVersion.Length > 0;
    }

    public string FallbackVersion(string umd) => umd;

    // The profile is stored in plans (a new one rebuilds them) and picks the per-stage policy (UnitPolicy.For).
    public VendorCaps Caps { get; } = new("intel-1", CacheKeyedByExeName: true, StateIndependentCache: false, CacheSizeConfigurable: false,
        PerStageCache: true, RtCacheGranularity: RtCacheGranularity.Collection);

    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>D3D12 and D3D11 cache: %USERPROFILE%\AppData\LocalLow\Intel\ShaderCache (LocalAppData's sibling), one file
    /// per exe name, named by a 64-hex hash that isn't derivable from the name alone, growing as entries are added.</summary>
    public static string CacheDir => Path.Combine(LocalAppData + "Low", "Intel", "ShaderCache");

    public CacheUsage GetCacheUsage() => new(CacheDir, AmdBackend.Bytes(CacheDir), UpperBound: false);

    public CacheLimit? GetCacheLimit() => null;

    public void SetCacheLimit(CacheLimit limit) =>
        throw new NotSupportedException($"the Intel shader cache size is not configurable ({Gpu.Name})");
}
