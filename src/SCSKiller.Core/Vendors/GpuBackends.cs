namespace SCSKiller.Core.Vendors;

public static partial class GpuBackends
{
    /// <summary>Backend for the picked adapter (<see cref="Pick"/>). <paramref name="intel"/>: <see cref="Settings.ExperimentalIntel"/>.</summary>
    public static IGpuVendorBackend Detect(GpuChoice? choice = null, bool intel = false) =>
        Pick(Adapters(), choice, intel) is { } picked ? For(picked, intel) : For(new GpuInfo(GpuVendor.Unknown, "no D3D adapter", "", 0, 0), intel);

    /// <summary>The non-software adapter with the most dedicated VRAM (what games render on), the same rule as
    /// scskiller_warm. DriverVersion is the user-mode driver file version (vendor backends replace it).</summary>
    public static GpuInfo? PrimaryAdapter() => Primary(Adapters());

    public static GpuInfo? Primary(IReadOnlyList<DxgiAdapter> adapters) => adapters.MaxBy(a => a.Gpu.DedicatedVideoMemory)?.Gpu;

    /// <summary>The adapter the user picked (<see cref="Chosen"/>), else the one with the most video memory of those this OS has a
    /// backend for (<see cref="HasBackend"/>), else of all.</summary>
    public static DxgiAdapter? Pick(IReadOnlyList<DxgiAdapter> adapters, GpuChoice? choice, bool intel = false) =>
        Chosen(adapters, choice, intel) ?? adapters.Where(a => HasBackend(a, intel)).MaxBy(a => a.Gpu.DedicatedVideoMemory)
        ?? adapters.MaxBy(a => a.Gpu.DedicatedVideoMemory);

    /// <summary>The listed adapter <paramref name="choice"/> names, of those this OS has a backend for: by its LUID (with its
    /// vendor and device id) while this boot keeps it, else by vendor, device id and name; of identical cards, the first DXGI
    /// lists. Null when none is. A stored pick without one stays stored, for a build or driver that has it.</summary>
    public static DxgiAdapter? Chosen(IReadOnlyList<DxgiAdapter> adapters, GpuChoice? choice, bool intel = false)
    {
        if (choice is not { } c) return null;
        // Linux lists a GPU once per Vulkan driver (RADV and AMDVLK): the same LUID and device id
        var backed = adapters.Where(a => HasBackend(a, intel)).ToList();
        return backed.FirstOrDefault(a => (a.Gpu.AdapterLuid, a.Gpu.Vendor, a.DeviceId) == (c.AdapterLuid, c.Vendor, c.DeviceId))
            ?? backed.FirstOrDefault(a => (a.Gpu.Vendor, a.DeviceId, a.Gpu.Name) == (c.Vendor, c.DeviceId, c.Name));
    }

    /// <summary>The adapters SCSKiller compiles for (NVIDIA, AMD, and Intel with <paramref name="intel"/> where it takes
    /// <see cref="IntelSwitch"/>, of those this OS has a backend for): what a pick is offered from.</summary>
    public static IEnumerable<DxgiAdapter> Usable(IReadOnlyList<DxgiAdapter> adapters, bool intel = false) =>
        adapters.Where(a => (a.Gpu.Vendor is GpuVendor.Nvidia or GpuVendor.Amd || (intel || !IntelSwitch) && a.Gpu.Vendor == GpuVendor.Intel) && HasBackend(a, intel));

    /// <summary>The vendor whose own driver compiles: none under Mesa, which has no AGS, DxcCache, Aftermath, NVAPI or
    /// DLSS, whatever GPU it runs.</summary>
    public static GpuVendor OwnDriver(IGpuVendorBackend backend) => MesaLinux.Is(backend.Caps) ? GpuVendor.Unknown : backend.Vendor;

    /// <summary>The log's line for a backend on a driver whose cache behaviour SCSKiller hasn't measured; null otherwise.</summary>
    public static partial string? DriverNote(IGpuVendorBackend backend);

    /// <summary>This OS has a backend for <paramref name="adapter"/>: Windows for every vendor's, Linux for its Vulkan driver's
    /// too. <paramref name="intel"/>: <see cref="Settings.ExperimentalIntel"/>.</summary>
    public static partial bool HasBackend(DxgiAdapter adapter, bool intel);

    public static GpuChoice ChoiceOf(DxgiAdapter a) => new(a.Gpu.AdapterLuid, a.Gpu.Vendor, a.DeviceId, a.Gpu.Name);

    /// <summary>The adapter id Linux gives in place of a LUID: the PCI address (domain, bus, device, function, as
    /// <c>dddd:bb:dd.f</c> packs them), which stays across boots and driver reloads.</summary>
    public static long PciAddress(uint domain, uint bus, uint device, uint function) =>
        (long)domain << 16 | (bus & 0xff) << 8 | (device & 0x1f) << 3 | function & 7;
}

/// <summary>A DXGI adapter. A driver reload gives it a new LUID; the PCI device and subsystem ids stay. <paramref name="Driver"/>:
/// on Linux the Vulkan driver's id (VkDriverId, <see cref="MesaLinux"/>), 0 when Vulkan doesn't say or on Windows.</summary>
public sealed record DxgiAdapter(GpuInfo Gpu, uint DeviceId, uint SubSysId, uint Driver = 0);

/// <summary>A backend whose <see cref="IGpuVendorBackend.Gpu"/> follows a driver update in place, so whatever holds the
/// backend (the warmer's adapter LUID, staleness, the window) sees the new driver.</summary>
public interface IRefreshableGpu
{
    /// <summary><paramref name="adapter"/>: the backend's adapter as DXGI lists it now. False when the driver version
    /// couldn't be fully read (a fallback was used): the caller asks again at its next check.</summary>
    bool Refresh(GpuInfo adapter);
    /// <summary>The version earlier builds published when the vendor's own was unreadable, from DXGI's user-mode version
    /// <paramref name="umd"/>: what their records may hold for the current driver.</summary>
    string FallbackVersion(string umd);
}

/// <summary>Intel (unless <see cref="Settings.ExperimentalIntel"/>) / unknown until measured: no cache assumptions, warming
/// reports Unsupported via the caps. <paramref name="why"/>: the Library's notice when the vendor's own doesn't say it (a
/// driver SCSKiller doesn't compile on, <see cref="MesaLinux.Refusal"/>).</summary>
public sealed class UnsupportedVendor(GpuInfo gpu, string? why = null) : IGpuVendorBackend, IRefreshableGpu
{
    public GpuVendor Vendor => Gpu.Vendor;
    public GpuInfo Gpu { get; private set; } = gpu;
    public string? Why { get; } = why;
    public bool Refresh(GpuInfo adapter)
    {
        Gpu = adapter;
        return true;
    }
    public string FallbackVersion(string umd) => umd;
    public VendorCaps Caps { get; } = new("unsupported", false, false, false);
    public CacheUsage GetCacheUsage() => new("", 0, true);
    public CacheLimit? GetCacheLimit() => null;
    public void SetCacheLimit(CacheLimit limit) => throw new NotSupportedException($"cache size is not configurable for {Gpu.Name}");
}
