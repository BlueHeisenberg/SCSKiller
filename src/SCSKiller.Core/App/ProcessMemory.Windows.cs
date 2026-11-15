using System.Runtime.InteropServices;

namespace SCSKiller.Core.App;

public static partial class ProcessMemory
{
    public static partial void Trim() => SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);

    /// <summary>The commit charge (PrivateUsage), as Process.PrivateMemorySize64 gives it without a snapshot of every process.</summary>
    public static partial long Private()
    {
        var c = new Counters { Size = (uint)Marshal.SizeOf<Counters>() };
        return GetProcessMemoryInfo(GetCurrentProcess(), ref c, c.Size) ? (long)c.PrivateUsage : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Counters   // PROCESS_MEMORY_COUNTERS_EX
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage,
            QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")] static extern bool GetProcessMemoryInfo(nint process, ref Counters counters, uint size);

    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(nint process, nint min, nint max);
}
