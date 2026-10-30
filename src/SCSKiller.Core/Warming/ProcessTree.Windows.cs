using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SCSKiller.Core.Warming;

static partial class ProcessTree
{
    public static partial void Suspend(int rootPid, bool suspend)
    {
        foreach (var pid in WithDescendants(rootPid))
        {
            var h = OpenProcess(0x0800 /* PROCESS_SUSPEND_RESUME */, false, pid);
            if (h == 0) continue;   // exited meanwhile
            _ = suspend ? NtSuspendProcess(h) : NtResumeProcess(h);
            CloseHandle(h);
        }
    }

    public static partial List<(int Pid, int Parent, string Exe)> Snapshot()
    {
        var all = new List<(int, int, string)>();
        var snap = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == -1) throw new Win32Exception();
        try
        {
            var e = new ProcessEntry32 { Size = Marshal.SizeOf<ProcessEntry32>() };
            for (var ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e)) all.Add((e.Pid, e.ParentPid, e.ExeFile));
        }
        finally { CloseHandle(snap); }
        return all;
    }

    // NtQuerySystemInformation(SystemProcessIdInformation) gives its NT path: null when no drive letter maps its volume
    public static partial string? ImagePath(int pid)
    {
        unsafe
        {
            const int SystemProcessIdInformation = 88;
            var name = new char[32768];
            fixed (char* buffer = name)
            {
                // SYSTEM_PROCESS_ID_INFORMATION (x64): ProcessId, then UNICODE_STRING { u16 Length, u16 MaximumLength, PWSTR Buffer at 8 }
                var info = stackalloc byte[24];
                *(nint*)info = pid;
                *(ushort*)(info + 8) = 0;
                *(ushort*)(info + 10) = (ushort)(name.Length * 2 - 2);
                *(char**)(info + 16) = buffer;
                if (NtQuerySystemInformation(SystemProcessIdInformation, info, 24, out _) < 0) return null;
                return DosPath(new string(buffer, 0, *(ushort*)(info + 8) / 2), DosDevices());
            }
        }
    }

    static IEnumerable<(string Drive, string Device)> DosDevices()
    {
        var target = new char[1024];
        foreach (var root in Environment.GetLogicalDrives())
        {
            var drive = root.TrimEnd(Path.DirectorySeparatorChar);
            if (QueryDosDeviceW(drive, target, target.Length) > 0) yield return (drive, new string(target).Split((char)0)[0]);
        }
    }

    // FILETIME
    internal static partial long? Created(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return null;
        try { return GetProcessTimes(h, out var created, out _, out _, out _) ? created : null; }
        finally { CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry32
    {
        public int Size, Usage, Pid;
        public nint DefaultHeapId;
        public int ModuleId, Threads, ParentPid, PriClassBase, Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern nint CreateToolhelp32Snapshot(uint flags, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(nint snap, ref ProcessEntry32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(nint snap, ref ProcessEntry32 e);
    [DllImport("kernel32.dll")] static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
    [DllImport("ntdll.dll")] static extern unsafe int NtQuerySystemInformation(int infoClass, void* info, int length, out int returned);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int QueryDosDeviceW(string device, char[] target, int max);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(nint h, out long created, out long exited, out long kernel, out long user);
    [DllImport("ntdll.dll")] static extern int NtSuspendProcess(nint h);
    [DllImport("ntdll.dll")] static extern int NtResumeProcess(nint h);
}
