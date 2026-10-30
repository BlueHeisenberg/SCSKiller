using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.Vendors;

public static partial class AppCacheFiles
{
    /// <summary>Keys of <paramref name="files"/> (path, key) opened by a running process with this exe file name (from the
    /// process snapshot: none opened).</summary>
    public static IReadOnlySet<string> KeysOpenBy(string exeFileName, IEnumerable<(string Path, string Key)> files)
    {
        var pids = Processes().Where(p => p.Exe.Equals(exeFileName, StringComparison.OrdinalIgnoreCase))
            .Select(p => (nuint)p.Pid).ToHashSet();
        var keys = new HashSet<string>();
        if (pids.Count == 0) return keys;
        foreach (var (path, key) in files)
            if (!keys.Contains(key) && ProcessesUsing(path).Overlaps(pids)) keys.Add(key);
        return keys;
    }

    /// <summary>Deletes all <paramref name="files"/> or none: InvalidOperationException ("files in use by ...") when one
    /// is open (the driver holds a running app's files). All files are opened exclusively first, so nothing can open them
    /// between the check and the delete. Returns how many were deleted.</summary>
    public static int DeleteAll(IReadOnlyList<FileInfo> files, IReadOnlySet<string>? steams = null)
    {
        RefuseSteams(files, steams);
        var held = new List<SafeFileHandle>();
        try
        {
            foreach (var f in files)
            {
                // DELETE, not read: an AMD cache file can carry a higher no-read-up label, yet this process may delete it
                var h = CreateFileW(f.FullName, 0x10000 /* DELETE */, 4 /* FILE_SHARE_DELETE */, 0, 3 /* OPEN_EXISTING */, 0, 0);
                if (!h.IsInvalid) { held.Add(h); continue; }
                var e = Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error(), -1)!;   // -1: not the thread's stale COM error info
                h.Dispose();
                var users = ProcessesUsing(f.FullName).Select(Describe).ToList();
                throw new InvalidOperationException(users.Count > 0 ? $"files in use by {string.Join(", ", users)} ({f.Name})" : Unheld(f, e), e);
            }
            foreach (var f in files) { f.Refresh(); if (f.IsReadOnly) f.IsReadOnly = false; }   // Delete refuses a read-only file: none is deleted before all can be
            foreach (var f in files) f.Delete();   // gone once our handles close
            return files.Count;
        }
        finally { foreach (var h in held) h.Dispose(); }
    }

    /// <summary>Refused with no process holding it: AMD driver cache files can stay so across restarts until its Reset Shader Cache.</summary>
    static string Unheld(FileInfo f, Exception e) =>
        $"{(e is UnauthorizedAccessException ? "access denied to" : "no process found holding")} {f.Name} (0x{e.HResult:X8})"
        + (f.Directory is { Name: "DxcCache" or "DxCache", Parent.Name: "AMD" } ? ". Reset Shader Cache in AMD Software removes it" : "");

    static string Describe(nuint pid) =>
        Warming.ProcessTree.Snapshot().FirstOrDefault(p => (nuint)p.Pid == pid).Exe is { } exe ? $"{exe} (pid {pid})" : $"pid {pid}";   // exited meanwhile

    public static HashSet<nuint> ProcessesUsing(string path) => Using(path) ?? [];

    /// <summary>A process has the file open; null when that can't be told.</summary>
    public static bool? InUse(string path) => Using(path) is { } pids ? pids.Count > 0 : null;

    static HashSet<nuint>? Using(string path)
    {
        var pids = new HashSet<nuint>();
        using var h = CreateFileW(path, 0x80 /* FILE_READ_ATTRIBUTES */, 7 /* share all */, 0, 3 /* OPEN_EXISTING */, 0, 0);
        if (h.IsInvalid) return null;
        var buf = new nuint[64];   // FILE_PROCESS_IDS_USING_FILE_INFORMATION: count, pad, pids
        for (int status; ; buf = new nuint[buf.Length * 4])
        {
            status = NtQueryInformationFile(h, out _, buf, buf.Length * nint.Size, 47 /* FileProcessIdsUsingFileInformation */);
            if (status == unchecked((int)0xC0000004) /* STATUS_INFO_LENGTH_MISMATCH */ && buf.Length < 1 << 16) continue;
            if (status < 0) return null;
            break;
        }
        for (int i = 0; i < (int)(uint)buf[0]; i++) pids.Add(buf[1 + i]);
        return pids;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationFile(SafeFileHandle h, out IoStatusBlock iosb, [Out] nuint[] info, int length, int infoClass);
    struct IoStatusBlock { public nint Status, Information; }
}
