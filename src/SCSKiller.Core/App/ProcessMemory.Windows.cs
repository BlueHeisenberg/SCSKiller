using System.Runtime.InteropServices;

namespace SCSKiller.Core.App;

public static partial class ProcessMemory
{
    public static partial void Trim() => SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);

    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(nint process, nint min, nint max);
}
