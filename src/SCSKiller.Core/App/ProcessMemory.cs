using System.Diagnostics;
using System.Globalization;

namespace SCSKiller.Core.App;

/// <summary>This process's own memory.</summary>
public static partial class ProcessMemory
{
    /// <summary>Hands the working set back to Windows (the pages stay committed and come back on use): the window is hidden
    /// or minimized. Measured on the idle app: 210 MB to 11 MB, 16 MB again 15 s later.</summary>
    public static partial void Trim();

    /// <summary>The log file <see cref="Line"/> goes to in the data folder, on request (`SCSKiller.exe --memory`).</summary>
    public const string LogFile = "memory.log";

    /// <summary>What this process holds, counts only: the working set, private bytes, the managed heap by generation after
    /// the last collection, what the GC keeps committed and how much of it is free, the cached key sets, threads, handles,
    /// JIT-compiled methods.</summary>
    public static string Line()
    {
        using var p = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        string Gen(int i) => i < gc.GenerationInfo.Length ? $"{gc.GenerationInfo[i].SizeAfterBytes >> 20}" : "-";
        return string.Create(CultureInfo.InvariantCulture,
            $"working set {p.WorkingSet64 >> 20} MB, private {p.PrivateMemorySize64 >> 20} MB, managed heap {GC.GetTotalMemory(false) >> 20} MB"
            + $" (after the last collection: gen0 {Gen(0)}, gen1 {Gen(1)}, gen2 {Gen(2)}, large {Gen(3)}, pinned {Gen(4)} MB),"
            + $" GC committed {gc.TotalCommittedBytes >> 20} MB of which {gc.FragmentedBytes >> 20} MB free, cached keys {KeyFiles.CachedKeys:N0},"
            + $" threads {p.Threads.Count}, handles {p.HandleCount}, JIT {System.Runtime.JitInfo.GetCompiledMethodCount():N0} methods");
    }
}
