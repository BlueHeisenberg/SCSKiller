namespace SCSKiller.Core.Vendors;

/// <summary>Intel (Arc and Xe graphics). Experimental and opt-in (<see cref="Enabled"/>): nothing here is measured yet.
/// tools/intel-arc/measure.ps1 runs the probes that decide each cap (selftest fields/dxr/bindless, probe11); until their
/// results are in, the caps are the most conservative ones the planner supports: the cache is assumed keyed on the exe
/// file name (what a staged warm needs at all), state-dependent and per pipeline, and ray tracing objects whole, so a warm
/// replays exactly the pipelines a recording saw and nothing synthesized.
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

    // The profile is stored in plans: measured caps get a new one, so every plan made under these is rebuilt.
    public VendorCaps Caps { get; } = new("intel-0", CacheKeyedByExeName: true, StateIndependentCache: false, CacheSizeConfigurable: false,
        PerStageCache: false, RtCacheGranularity: RtCacheGranularity.WholeObject);

    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Where Intel's D3D12 cache may be (unmeasured: measure.ps1 lists what its probes write). LocalLow is
    /// LocalAppData's sibling.</summary>
    public static IReadOnlyList<string> CacheDirs =>
        [Path.Combine(LocalAppData + "Low", "Intel", "ShaderCache"), Path.Combine(LocalAppData, "Intel", "ShaderCache")];

    /// <summary>Every candidate folder's bytes; <see cref="CacheUsage.Path"/> is the first one that exists.</summary>
    public CacheUsage GetCacheUsage() =>
        new(CacheDirs.FirstOrDefault(Directory.Exists) ?? CacheDirs[0], CacheDirs.Sum(AmdBackend.Bytes), UpperBound: false);

    public CacheLimit? GetCacheLimit() => null;

    public void SetCacheLimit(CacheLimit limit) =>
        throw new NotSupportedException($"the Intel shader cache size is not configurable ({Gpu.Name})");
}
