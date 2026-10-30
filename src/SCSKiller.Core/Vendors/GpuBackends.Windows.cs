using System.Runtime.InteropServices;

namespace SCSKiller.Core.Vendors;

public static partial class GpuBackends
{
    // every vendor's: an Intel pick without Settings.ExperimentalIntel is applied, and For makes it Unsupported
    public static partial bool HasBackend(DxgiAdapter adapter, bool intel) => true;

    /// <summary>Intel GPUs compile only with <see cref="Settings.ExperimentalIntel"/> (<see cref="IntelBackend"/>).</summary>
    public const bool IntelSwitch = true;

    public static partial string? DriverNote(IGpuVendorBackend backend) => null;

    public static IGpuVendorBackend For(DxgiAdapter adapter, bool intel = false) => For(adapter.Gpu, intel);

    /// <summary>The backend of <paramref name="gpu"/>. Adding a vendor = one case here.</summary>
    public static IGpuVendorBackend For(GpuInfo gpu, bool intel = false)
    {
        try
        {
            return gpu.Vendor switch
            {
                GpuVendor.Nvidia => new NvidiaBackend(gpu),
                GpuVendor.Amd => new AmdBackend(gpu),
                GpuVendor.Intel when intel => new IntelBackend(gpu),
                _ => new UnsupportedVendor(gpu),
            };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return new UnsupportedVendor(gpu);   // e.g. NVIDIA GPU without a working nvapi64.dll
        }
    }

    /// <summary>The non-software adapters, in DXGI's order. Under Wine, DriverVersion is the host driver's (<see cref="Wine"/>).</summary>
    public static unsafe IReadOnlyList<DxgiAdapter> Adapters()
    {
        var all = new List<DxgiAdapter>();
        var host = Wine.HostDriverVersions();
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
                var luid = ((long)d.LuidHigh << 32) | d.LuidLow;
                if (!Dx12Of.TryGetValue(luid, out var dx12)) Dx12Of[luid] = dx12 = SupportsD3D12(adapter);
                ((delegate* unmanaged<nint, uint>)vt[2])(adapter);   // Release
                if ((d.Flags & 2) != 0) continue;   // DXGI_ADAPTER_FLAG_SOFTWARE
                if (host.TryGetValue((d.VendorId, d.DeviceId), out var hostVersion)) version = hostVersion;
                var vendor = Enum.IsDefined(typeof(GpuVendor), (int)d.VendorId) ? (GpuVendor)d.VendorId : GpuVendor.Unknown;
                all.Add(new(new GpuInfo(vendor, new string(d.Description), version, luid, d.DedicatedVideoMemory, dx12),
                    d.DeviceId, d.SubSysId));
            }
            return all;
        }
        finally { ((delegate* unmanaged<nint, uint>)(*(nint**)factory)[2])(factory); }
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<long, bool> Dx12Of = new();

    // a null device out-pointer only asks whether the adapter could create one; no device is made
    static unsafe bool SupportsD3D12(nint adapter)
    {
        var iid = new Guid("189819f1-1db6-4b57-be54-1821339b85f7");   // ID3D12Device
        try { return D3D12CreateDevice(adapter, 0xb000, &iid, null) >= 0; }   // D3D_FEATURE_LEVEL_11_0
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    [DllImport("d3d12.dll")] static extern unsafe int D3D12CreateDevice(nint adapter, int minimumFeatureLevel, Guid* riid, nint* device);

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
