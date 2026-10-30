using System.Runtime.InteropServices;

namespace SCSKiller.Core.Vendors;

/// <summary>Wine, as Proton runs SCSKiller and the games on Linux. Its DXGI is DXVK's, whose user-mode driver version is
/// DXVK's own and never changes; the driver's is the host's, read through Vulkan (winevulkan reaches the host driver).</summary>
public static class Wine
{
    public static bool Running { get; } = NativeLibrary.TryLoad("ntdll.dll", out var ntdll) && NativeLibrary.TryGetExport(ntdll, "wine_get_version", out _);

    /// <summary>The Linux path of a Windows one (Wine's wine_get_unix_file_name); null outside Wine.</summary>
    public static unsafe string? UnixPath(string windowsPath)
    {
        if (!Running || !NativeLibrary.TryLoad("kernel32.dll", out var k32) || !NativeLibrary.TryGetExport(k32, "wine_get_unix_file_name", out var f)) return null;
        nint p;
        fixed (char* w = windowsPath) p = ((delegate* unmanaged<char*, nint>)f)(w);
        if (p == 0) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { HeapFree(GetProcessHeap(), 0, p); }
    }

    /// <summary>The Windows path of a Linux one (Wine's wine_get_dos_file_name, Z: unless a drive maps it); null outside
    /// Wine or for an empty path.</summary>
    public static unsafe string? DosPath(string unixPath)
    {
        if (unixPath.Length == 0 || !Running || !NativeLibrary.TryLoad("kernel32.dll", out var k32) || !NativeLibrary.TryGetExport(k32, "wine_get_dos_file_name", out var f)) return null;
        var utf8 = System.Text.Encoding.UTF8.GetBytes(unixPath + '\0');
        nint p;
        fixed (byte* u = utf8) p = ((delegate* unmanaged<byte*, nint>)f)(u);
        if (p == 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { HeapFree(GetProcessHeap(), 0, p); }
    }

    [DllImport("kernel32.dll")] static extern nint GetProcessHeap();
    [DllImport("kernel32.dll")] static extern bool HeapFree(nint heap, uint flags, nint p);

    /// <summary>The files of the warm's app name the game reads: whichever number its warm got.</summary>
    public const int WarmFiles = 8;

    /// <summary>Under Wine NVIDIA's driver keys its shader cache on the process image, Wine's own for every Windows process,
    /// and gives each process holding the cache the next numbered file; and a variable a Windows process sets never reaches
    /// the driver of a process it starts (measured: the driver sees the Linux environment Wine started with). So the warm
    /// runs in a Wine session started with <c>Warm</c>, under an app name of its own, and the game, with <c>Game</c>,
    /// writes a file of its own and reads every numbered file of the warm's. vkd3d-proton's cache, named by the exe, is
    /// shared. <paramref name="unixRoot"/>: the game's cache folder (nvidia/ and vkd3d/ in it). <paramref name="size"/>: what
    /// the driver keeps there (<see cref="NvidiaLinux.CacheSize"/>, <see cref="NvidiaLinux.PlacedCacheSize"/>).</summary>
    public static (List<(string Name, string Value)> Warm, List<(string Name, string Value)> Game) CacheEnvironment(string unixRoot, string exe, long size = NvidiaLinux.CacheSize)
    {
        var stem = Stem(exe);
        List<(string, string)> common = [
            ("__GL_SHADER_DISK_CACHE_PATH", unixRoot + "/nvidia"),
            ("__GL_SHADER_DISK_CACHE_SIZE", size.ToString(System.Globalization.CultureInfo.InvariantCulture)),   // the default deletes whole files past about 1 GiB; a big game's warm is more
            ("VKD3D_SHADER_CACHE_PATH", unixRoot + "/vkd3d")];
        var warm = WarmAppName(exe);
        return ([.. common, ("__GL_SHADER_DISK_CACHE_APP_NAME", warm)],
            [.. common, ("__GL_SHADER_DISK_CACHE_APP_NAME", stem),
                ("__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME", string.Join(';', Enumerable.Range(0, WarmFiles).Select(i => warm + i)))]);
    }

    /// <summary>The app name the warm of exe file <paramref name="exe"/> writes NVIDIA's cache under (<see cref="CacheEnvironment"/>).</summary>
    public static string WarmAppName(string exe) => $"scskiller_{Stem(exe)}";

    static string Stem(string exe) =>
        new(Path.GetFileNameWithoutExtension(exe).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    /// <summary>The game's cache folder for <see cref="CacheEnvironment"/>, proton-cache in its data folder
    /// <paramref name="gameDir"/>, made with its nvidia and vkd3d folders unless <paramref name="make"/> is false: NVIDIA's
    /// driver keeps no cache in a folder that doesn't exist.</summary>
    public static string CacheRoot(string gameDir, bool make = true)
    {
        var root = Path.GetFullPath(Path.Combine(gameDir, "proton-cache"));
        if (make)
            foreach (var d in new[] { "nvidia", "vkd3d" }) Directory.CreateDirectory(Path.Combine(root, d));
        return root;
    }

    /// <summary>VkPhysicalDeviceProperties.driverVersion as the driver numbers itself: NVIDIA packs 10, 8, 8 and 6 bits
    /// ("615.71.09"), Mesa and the others VK_MAKE_API_VERSION's 10.10.12.</summary>
    public static string FormatDriverVersion(uint vendorId, uint v) => vendorId == (uint)GpuVendor.Nvidia
        ? $"{v >> 22}.{(v >> 14) & 0xff:00}.{(v >> 6) & 0xff:00}"
        : $"{v >> 22}.{(v >> 12) & 0x3ff}.{v & 0xfff}";

    /// <summary>The host driver's version of each Vulkan device by vendor and device id; empty outside Wine or when
    /// Vulkan can't say.</summary>
    public static unsafe Dictionary<(uint Vendor, uint Device), string> HostDriverVersions()
    {
        var found = new Dictionary<(uint, uint), string>();
        if (!Running || !NativeLibrary.TryLoad("vulkan-1.dll", out var vk)) return found;
        try
        {
            if (!NativeLibrary.TryGetExport(vk, "vkCreateInstance", out var create) || !NativeLibrary.TryGetExport(vk, "vkDestroyInstance", out var destroy)
                || !NativeLibrary.TryGetExport(vk, "vkEnumeratePhysicalDevices", out var enumerate)
                || !NativeLibrary.TryGetExport(vk, "vkGetPhysicalDeviceProperties", out var properties))
                return found;
            var info = stackalloc nint[8];   // VkInstanceCreateInfo: VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO and nothing else
            new Span<nint>(info, 8).Clear();
            *(int*)info = 1;
            nint instance;
            if (((delegate* unmanaged<nint*, void*, nint*, int>)create)(info, null, &instance) != 0) return found;
            try
            {
                const int Max = 16;
                uint n = Max;
                var devices = stackalloc nint[Max];
                if (((delegate* unmanaged<nint, uint*, nint*, int>)enumerate)(instance, &n, devices) < 0) return found;   // VK_INCOMPLETE past 16 is fine
                var props = stackalloc uint[512];   // VkPhysicalDeviceProperties (824 bytes): apiVersion, driverVersion, vendorID, deviceID first
                for (var i = 0; i < n; i++)
                {
                    ((delegate* unmanaged<nint, uint*, void>)properties)(devices[i], props);
                    found[(props[2], props[3])] = FormatDriverVersion(props[2], props[1]);
                }
            }
            finally { ((delegate* unmanaged<nint, void*, void>)destroy)(instance, null); }
        }
        finally { NativeLibrary.Free(vk); }
        return found;
    }
}
