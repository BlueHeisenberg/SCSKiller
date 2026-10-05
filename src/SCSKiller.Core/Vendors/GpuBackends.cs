using System.Runtime.InteropServices;

namespace SCSKiller.Core.Vendors;

public static class GpuBackends
{
    /// <summary>Backend for the primary discrete adapter. Adding a vendor = one case here.</summary>
    public static IGpuVendorBackend Detect()
    {
        var gpu = PrimaryAdapter() ?? new GpuInfo(GpuVendor.Unknown, "no D3D adapter", "", 0, 0);
        try
        {
            return gpu.Vendor switch
            {
                GpuVendor.Nvidia => new NvidiaBackend(gpu),
                GpuVendor.Amd => new AmdBackend(gpu),
                GpuVendor.Intel when IntelBackend.Enabled => new IntelBackend(gpu),
                _ => new UnsupportedVendor(gpu),
            };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return new UnsupportedVendor(gpu);   // e.g. NVIDIA GPU without a working nvapi64.dll
        }
    }

    /// <summary>The non-software adapter with the most dedicated VRAM (what games render on), the same rule as
    /// scskiller_warm. DriverVersion is the user-mode driver file version (vendor backends replace it).</summary>
    public static GpuInfo? PrimaryAdapter() => Primary(Adapters());

    public static GpuInfo? Primary(IReadOnlyList<DxgiAdapter> adapters) => adapters.MaxBy(a => a.Gpu.DedicatedVideoMemory)?.Gpu;

    /// <summary>The non-software adapters, in DXGI's order.</summary>
    public static unsafe IReadOnlyList<DxgiAdapter> Adapters()
    {
        var all = new List<DxgiAdapter>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IDXGIFactory1
        if (CreateDXGIFactory1(&iid, out var factory) < 0) return all;
        try
        {
            var enumAdapters1 = (delegate* unmanaged<nint, uint, nint*, int>)(*(nint**)factory)[12];
            nint adapter;
            for (uint i = 0; enumAdapters1(factory, i, &adapter) >= 0; i++)
            {
                var vt = *(nint**)adapter;
                AdapterDesc1 d;
                ((delegate* unmanaged<nint, AdapterDesc1*, int>)vt[10])(adapter, &d);   // GetDesc1
                var dxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");     // IDXGIDevice: yields the UMD version
                long umd;
                var version = ((delegate* unmanaged<nint, Guid*, long*, int>)vt[9])(adapter, &dxgiDevice, &umd) >= 0   // CheckInterfaceSupport
                    ? $"{umd >> 48 & 0xFFFF}.{umd >> 32 & 0xFFFF}.{umd >> 16 & 0xFFFF}.{umd & 0xFFFF}" : "";
                ((delegate* unmanaged<nint, uint>)vt[2])(adapter);   // Release
                if ((d.Flags & 2) != 0) continue;   // DXGI_ADAPTER_FLAG_SOFTWARE
                var vendor = Enum.IsDefined(typeof(GpuVendor), (int)d.VendorId) ? (GpuVendor)d.VendorId : GpuVendor.Unknown;
                all.Add(new(new GpuInfo(vendor, new string(d.Description), version, ((long)d.LuidHigh << 32) | d.LuidLow, d.DedicatedVideoMemory),
                    d.DeviceId, d.SubSysId));
            }
            return all;
        }
        finally { ((delegate* unmanaged<nint, uint>)(*(nint**)factory)[2])(factory); }
    }

    [DllImport("dxgi.dll")] static extern unsafe int CreateDXGIFactory1(Guid* riid, out nint factory);

    unsafe struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }
}

/// <summary>A DXGI adapter. A driver reload gives it a new LUID; the PCI device and subsystem ids stay.</summary>
public sealed record DxgiAdapter(GpuInfo Gpu, uint DeviceId, uint SubSysId);

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

/// <summary>Intel (unless <see cref="IntelBackend.Enabled"/>) / unknown until measured: no cache assumptions, warming
/// reports Unsupported via the caps.</summary>
public sealed class UnsupportedVendor(GpuInfo gpu) : IGpuVendorBackend, IRefreshableGpu
{
    public GpuVendor Vendor => Gpu.Vendor;
    public GpuInfo Gpu { get; private set; } = gpu;
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
