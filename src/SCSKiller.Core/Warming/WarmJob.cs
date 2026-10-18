using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SCSKiller.Core.Warming;

/// <summary>A Win32 job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. <see cref="Current"/> is this SCSKiller process's (app
/// or CLI): it holds every scskiller_warm.exe the process starts. Only this process has the handle (not inheritable), so
/// when it ends for any reason (quit, crash, Task Manager) Windows closes the handle and kills every warm and the staged
/// child named like the game. Such a survivor (a warm deadlocked in the driver ignores the stop event) would keep the
/// game's DXCache files open: the game or the next warm would get a second set. Children join the job by themselves:
/// breakaway is not allowed. Our own process is not in the job (the CLI starts the app, which must outlive it).</summary>
public sealed class WarmJob : IDisposable
{
    public static WarmJob Current { get; } = new();

    readonly nint _job;

    public WarmJob()
    {
        _job = CreateJobObjectW(0, null);   // null security attributes: the handle is not inherited by the warms
        if (_job == 0) throw new Win32Exception();
        var info = new ExtendedLimitInformation { LimitFlags = 0x2000 /* JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, no BREAKAWAY_OK */ };
        if (!SetInformationJobObject(_job, 9 /* JobObjectExtendedLimitInformation */, ref info, Marshal.SizeOf<ExtendedLimitInformation>()))
            throw new Win32Exception();
    }

    /// <summary>Puts <paramref name="pid"/> into the job, then the descendants it started before that (children started
    /// later join by themselves). Repeated until a pass finds none outside: a child not yet in may start one meanwhile.</summary>
    public void Add(int pid)
    {
        if (!Assign(pid) && !Contains(pid) && Running(pid)) throw new Win32Exception();
        for (int pass = 0; pass < 5 && ProcessTree.WithDescendants(pid).Skip(1).Count(Assign) > 0; pass++) { }
    }

    /// <summary>True if this call put it in (false: already in, exited, or refused).</summary>
    bool Assign(int pid)
    {
        var h = OpenProcess(0x0101 /* PROCESS_SET_QUOTA | PROCESS_TERMINATE */ | 0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return false;   // exited meanwhile
        try { return !(IsProcessInJob(h, _job, out var inJob) && inJob) && AssignProcessToJobObject(_job, h); }
        finally { CloseHandle(h); }
    }

    static bool Running(int pid)
    {
        var h = OpenProcess(0x1000, false, pid);
        if (h == 0) return false;
        try { return GetExitCodeProcess(h, out var code) && code == 259 /* STILL_ACTIVE */; }
        finally { CloseHandle(h); }
    }

    public bool Contains(int pid)
    {
        var h = OpenProcess(0x0400 /* PROCESS_QUERY_INFORMATION: Wine's IsProcessInJob needs it */ | 0x1000, false, pid);
        if (h == 0) return false;
        try { return IsProcessInJob(h, _job, out var inJob) && inJob; }
        finally { CloseHandle(h); }
    }

    /// <summary>Closes the handle: what this process ending does. Every process in the job is killed.</summary>
    public void Dispose() => CloseHandle(_job);

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern nint CreateJobObjectW(nint security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimitInformation info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool IsProcessInJob(nint process, nint job, out bool result);
    [DllImport("kernel32.dll")] static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(nint h, out uint code);
}
