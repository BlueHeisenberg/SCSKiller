using System.Runtime.InteropServices;

namespace SCSKiller.Core.Vendors;

public static partial class GpuBackends
{
    // NVIDIA's own driver (also when Vulkan doesn't name it), and Mesa's RADV, ANV and NVK, without Intel's switch.
    // The other drivers' caches aren't measured.
    public static partial bool HasBackend(DxgiAdapter adapter, bool intel) => NvidiaOwn(adapter) || (adapter.Gpu.Vendor, adapter.Driver) is
        (GpuVendor.Amd, MesaLinux.Radv) or (GpuVendor.Intel, MesaLinux.Anv) or (GpuVendor.Nvidia, MesaLinux.Nvk);

    /// <summary>ANV compiles through Mesa's path as RADV does, with no switch.</summary>
    public const bool IntelSwitch = false;

    static bool NvidiaOwn(DxgiAdapter a) => (a.Gpu.Vendor, a.Driver) is (GpuVendor.Nvidia, 0 or MesaLinux.NvidiaProprietary);

    /// <summary>The backend of <paramref name="adapter"/>'s driver (<see cref="HasBackend"/>), else none with the reason
    /// (<see cref="MesaLinux.Refusal"/>).</summary>
    public static IGpuVendorBackend For(DxgiAdapter adapter, bool intel = false) =>
        !HasBackend(adapter, intel) ? new UnsupportedVendor(adapter.Gpu, MesaLinux.Refusal(adapter))
        : NvidiaOwn(adapter) ? new NvidiaLinuxBackend(adapter.Gpu) : new MesaLinuxBackend(adapter.Gpu, driver: adapter.Driver);

    public static partial string? DriverNote(IGpuVendorBackend backend) => backend is MesaLinuxBackend { Driver: MesaLinux.Anv or MesaLinux.Nvk } m
        ? $"{m.Gpu.Name} compiles through the Mesa path on Mesa's {(m.Driver == MesaLinux.Anv ? "ANV" : "NVK")} driver, whose cache behaviour SCSKiller hasn't measured" : null;

    /// <summary>The backend of an adapter whose Vulkan driver isn't known.</summary>
    public static IGpuVendorBackend For(GpuInfo gpu, bool intel = false) => For(new DxgiAdapter(gpu, 0, 0), intel);

    /// <summary>The Vulkan devices but the CPU ones, in Vulkan's order: what Proton's DXGI lists. DriverVersion is the driver's
    /// own (<see cref="Wine.FormatDriverVersion"/>), on RADV, ANV and NVK with the driver build's tag (<see cref="MesaVersion"/>),
    /// AdapterLuid the PCI address (<see cref="PciAddress"/>, 0 when the driver doesn't say it), DedicatedVideoMemory a discrete
    /// GPU's largest device-local heap, Driver the VkDriverId (0 when the driver doesn't say it). Vulkan has no subsystem id: 0.</summary>
    public static unsafe IReadOnlyList<DxgiAdapter> Adapters()
    {
        var all = new List<DxgiAdapter>();
        if (!NativeLibrary.TryLoad("libvulkan.so.1", out var vk)) return all;
        try
        {
            nint Export(string name) => NativeLibrary.TryGetExport(vk, name, out var f) ? f : 0;
            var (create, destroy, enumerate, properties) = (Export("vkCreateInstance"), Export("vkDestroyInstance"),
                Export("vkEnumeratePhysicalDevices"), Export("vkGetPhysicalDeviceProperties"));
            var (properties2, memory, extensions) = (Export("vkGetPhysicalDeviceProperties2"), Export("vkGetPhysicalDeviceMemoryProperties"),
                Export("vkEnumerateDeviceExtensionProperties"));
            if (create == 0 || destroy == 0 || enumerate == 0 || properties == 0 || memory == 0) return all;

            var app = stackalloc byte[48];   // VkApplicationInfo
            new Span<byte>(app, 48).Clear();
            *(uint*)(app + 44) = 1 << 22 | 1 << 12;   // apiVersion 1.1: vkGetPhysicalDeviceProperties2
            var info = stackalloc byte[64];   // VkInstanceCreateInfo
            new Span<byte>(info, 64).Clear();
            *(int*)info = 1;
            *(byte**)(info + 24) = app;
            nint instance;
            if (((delegate* unmanaged<void*, void*, nint*, int>)create)(info, null, &instance) != 0) return all;
            try
            {
                uint n = 0;
                if (((delegate* unmanaged<nint, uint*, nint*, int>)enumerate)(instance, &n, null) != 0 || n == 0) return all;
                var devices = new nint[n];
                fixed (nint* d = devices)
                    if (((delegate* unmanaged<nint, uint*, nint*, int>)enumerate)(instance, &n, d) < 0) return all;
                var props = stackalloc byte[824];   // VkPhysicalDeviceProperties
                var heaps = stackalloc byte[520];   // VkPhysicalDeviceMemoryProperties
                foreach (var device in devices.AsSpan(0, (int)n))
                {
                    ((delegate* unmanaged<nint, byte*, void>)properties)(device, props);
                    var (api, driver, vendorId, deviceId, type) = (*(uint*)props, *(uint*)(props + 4), *(uint*)(props + 8), *(uint*)(props + 12), *(int*)(props + 16));
                    if (type == 4) continue;   // VK_PHYSICAL_DEVICE_TYPE_CPU
                    var name = Marshal.PtrToStringUTF8((nint)(props + 20)) ?? "";
                    ulong vram = 0;
                    if (type != 1)   // an integrated GPU's heaps are system memory
                    {
                        ((delegate* unmanaged<nint, byte*, void>)memory)(device, heaps);
                        for (uint i = 0, count = Math.Min(*(uint*)(heaps + 260), 16); i < count; i++)
                            if ((*(uint*)(heaps + 264 + i * 16 + 8) & 1) != 0) vram = Math.Max(vram, *(ulong*)(heaps + 264 + i * 16));   // DEVICE_LOCAL
                    }
                    var has = api >= (1 << 22 | 1 << 12) && properties2 != 0 && extensions != 0 ? Extensions(extensions, device) : [];
                    var luid = has.Contains("VK_EXT_pci_bus_info") ? PciAddressOf(properties2, device) : 0;
                    var id = has.Contains("VK_KHR_driver_properties") ? DriverIdOf(properties2, device) : 0;
                    var vendor = Enum.IsDefined(typeof(GpuVendor), (int)vendorId) ? (GpuVendor)vendorId : GpuVendor.Unknown;
                    var version = Wine.FormatDriverVersion(id == MesaLinux.Nvk ? 0 : vendorId, driver);   // NVK numbers itself as Mesa does
                    if (id is MesaLinux.Radv or MesaLinux.Anv or MesaLinux.Nvk) version = MesaVersion(version, new ReadOnlySpan<byte>(props + 276, 16));   // pipelineCacheUUID
                    all.Add(new(new GpuInfo(vendor, name, version, luid, vram), deviceId, 0, id));
                }
                return all;
            }
            finally { ((delegate* unmanaged<nint, void*, void>)destroy)(instance, null); }
        }
        finally { NativeLibrary.Free(vk); }
    }

    /// <summary>Mesa's driver version names a release, not a build: a rebuild (another LLVM, a distribution's patch) changes
    /// every cache key, and the pipeline cache UUID, which Mesa derives from the build, with them. The version with that UUID's
    /// first 4 bytes ("26.2.4 (63f230b3)").</summary>
    public static string MesaVersion(string version, ReadOnlySpan<byte> pipelineCacheUuid) => $"{version} ({Convert.ToHexStringLower(pipelineCacheUuid[..4])})";

    static unsafe HashSet<string> Extensions(nint enumerate, nint device)
    {
        uint n = 0;
        if (((delegate* unmanaged<nint, byte*, uint*, byte*, int>)enumerate)(device, null, &n, null) != 0) return [];
        var all = new byte[n * 260];   // VkExtensionProperties: char[256] name, uint specVersion
        var names = new HashSet<string>(StringComparer.Ordinal);
        fixed (byte* p = all)
        {
            if (((delegate* unmanaged<nint, byte*, uint*, byte*, int>)enumerate)(device, null, &n, p) < 0) return [];
            for (var i = 0; i < n; i++)
                if (Marshal.PtrToStringUTF8((nint)(p + i * 260)) is { } name) names.Add(name);
        }
        return names;
    }

    static unsafe uint DriverIdOf(nint properties2, nint device)
    {
        var driver = stackalloc byte[536];   // VkPhysicalDeviceDriverProperties
        new Span<byte>(driver, 536).Clear();
        *(int*)driver = 1000196000;
        var props = stackalloc byte[840];   // VkPhysicalDeviceProperties2
        new Span<byte>(props, 840).Clear();
        *(int*)props = 1000059001;
        *(byte**)(props + 8) = driver;
        ((delegate* unmanaged<nint, byte*, void>)properties2)(device, props);
        return *(uint*)(driver + 16);
    }

    static unsafe long PciAddressOf(nint properties2, nint device)
    {
        var pci = stackalloc byte[32];   // VkPhysicalDevicePCIBusInfoPropertiesEXT
        new Span<byte>(pci, 32).Clear();
        *(int*)pci = 1000212000;
        var props = stackalloc byte[840];   // VkPhysicalDeviceProperties2
        new Span<byte>(props, 840).Clear();
        *(int*)props = 1000059001;
        *(byte**)(props + 8) = pci;
        ((delegate* unmanaged<nint, byte*, void>)properties2)(device, props);
        return PciAddress(*(uint*)(pci + 16), *(uint*)(pci + 20), *(uint*)(pci + 24), *(uint*)(pci + 28));
    }
}
