using System.Runtime.InteropServices;

namespace SCSKiller.Core.App;

public static partial class ProcessMemory
{
    const int TaskVmInfo = 22, FootprintOffset = 144;   // TASK_VM_INFO; task_vm_info.phys_footprint (rev1, mach/task_info.h)

    /// <summary>phys_footprint of task_info(TASK_VM_INFO): what Activity Monitor shows as the process's memory. 0 when the call
    /// fails, can't be bound, or the kernel's struct is older than rev1.</summary>
    static unsafe long Footprint()
    {
        var info = stackalloc uint[64];
        uint count = 64;
        try
        {
            if (task_info(MachTask.Self, TaskVmInfo, info, &count) != 0 || count * 4 < FootprintOffset + 8) return 0;
        }
        catch (Exception e) when (e is TypeInitializationException or EntryPointNotFoundException or DllNotFoundException) { return 0; }
        return *(long*)((byte*)info + FootprintOffset);
    }

    // the callable mach_task_self() adds a send right each call: taken once, on first use (never on Linux)
    static class MachTask { public static readonly uint Self = mach_task_self(); }

    [DllImport("libc")] static extern uint mach_task_self();
    [DllImport("libc")] static extern unsafe int task_info(uint task, int flavor, uint* info, uint* count);
}
