using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Vendors;

public static partial class AppCacheFiles
{
    /// <summary>A file as the kernel tells it apart: its device and inode.</summary>
    internal readonly record struct FileId(uint DevMajor, uint DevMinor, ulong Inode);

    /// <summary>Where the processes' open files are read. Tests: a folder laid out like it.</summary>
    internal static string Proc { get; set; } = "/proc";

    /// <summary>A write lease on the open file: 0, or the errno. Tests: a filesystem without leases.</summary>
    internal static Func<SafeFileHandle, int> Lease { get; set; } = TakeLease;

    /// <summary>The longest the leases are held before the last unlink starts: an open of one of the files waits until they're released.</summary>
    internal static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(1);

    /// <summary>Keys of <paramref name="files"/> (path, key) opened by a running process with this exe file name (from the
    /// process snapshot, which names a Wine process by its Windows exe), by device and inode. UnauthorizedAccessException
    /// or IOException when such a process's open files can't be read: never a set that leaves it out.</summary>
    public static IReadOnlySet<string> KeysOpenBy(string exeFileName, IEnumerable<(string Path, string Key)> files)
    {
        var open = new HashSet<FileId>();
        foreach (var p in Processes().Where(p => p.Exe.Equals(exeFileName, StringComparison.OrdinalIgnoreCase)))
            open.UnionWith(OpenFiles(p.Pid) ?? []);   // exited: holds nothing
        var keys = new HashSet<string>();
        if (open.Count == 0) return keys;
        foreach (var (path, key) in files)
            if (!keys.Contains(key) && StatAt(null, path, AtSymlinkNoFollow, out var id, out _) == 0 && open.Contains(id)) keys.Add(key);
        return keys;
    }

    /// <summary>Deletes all <paramref name="files"/> or none: InvalidOperationException ("files in use by ...") when a
    /// process has one open (the driver holds a running app's files) or one can't be opened. Linux lets an open file go, so
    /// each file first gets a write lease, which the kernel grants only while no other process has the file open or mapped,
    /// whoever's it is. The leases are held until the unlinks are done: an open meanwhile waits for them, at most
    /// <see cref="MaxHold"/> and never past the kernel's lease-break-time. An open that breaks a lease stops the delete before the next
    /// unlink, and each file is moved to a name of its own and checked there before it's unlinked. Where a lease can't be taken (a
    /// filesystem without leases), a process of this user whose open files can't be read refuses the delete. NVIDIA's per-game
    /// folders and vkd3d-proton's are opened from the games folder down without following a link, Mesa's from Steam's
    /// shadercache folder down.
    /// Returns how many were deleted.</summary>
    public static int DeleteAll(IReadOnlyList<FileInfo> files, IReadOnlySet<string>? steams = null)
    {
        RefuseSteams(files, steams);
        var dirs = new Dictionary<string, SafeFileHandle>(StringComparer.Ordinal);
        var all = new List<Target>();
        try
        {
            foreach (var f in files)
            {
                var folder = Path.GetDirectoryName(f.FullName)!;
                if (!dirs.TryGetValue(folder, out var dir))
                    dirs[folder] = dir = (NvidiaLinuxAppCache.NoLinkFolders(f.FullName) ?? MesaLinuxAppCache.NoLinkFolders(f.FullName) ?? NvidiaLinuxAppCache.Vkd3dNoLinkFolders(f.FullName)) is var (root, below)
                        ? OpenFolder(root, below) : Open(null, folder, ODirectory);
                var errno = StatAt(dir, f.Name, AtSymlinkNoFollow, out var id, out var type);
                if (errno != 0 || type != SIfReg) throw new InvalidOperationException($"files can't be opened ({f.Name}: {(errno != 0 ? Marshal.GetPInvokeErrorMessage(errno) : "not a file")})");
                all.Add(new(dir, f.Name, id));
            }

            var mapped = new Dictionary<FileId, FileId>();
            foreach (var t in all) mapped.TryAdd(MapsId(t.Dir, "", AtEmptyPath, t.Id), t.Id);
            var (holding, unknown) = Scan(all.Select(t => t.Id).ToHashSet(), mapped);
            foreach (var t in all)
                if (holding.Where(h => h.Id == t.Id).Select(h => $"{h.Exe} (pid {h.Pid})").ToList() is { Count: > 0 } users)
                    throw new InvalidOperationException($"files in use by {string.Join(", ", users)} ({t.Name})");

            // from the first lease on, only these local calls run, and none starts past the hold: well under the kernel's
            // lease-break-time, past which it takes a lease back
            var hold = TimeSpan.FromTicks(Math.Min(MaxHold.Ticks, LeaseBreakTime().Ticks / 4));
            var held = Stopwatch.StartNew();
            foreach (var t in all)
            {
                if (held.Elapsed > hold) throw new InvalidOperationException($"files took too long to check ({t.Name})");
                t.File = Open(t.Dir, t.Name, ONoFollow | ONonBlock);
                if (StatAt(t.File, "", AtEmptyPath, out var id, out _) != 0 || id != t.Id) throw new InvalidOperationException($"files changed while deleting ({t.Name})");
                switch (Lease(t.File))
                {
                    case 0: t.Leased = true; break;
                    case EAgain: throw new InvalidOperationException($"files in use by another process ({t.Name})");
                    case EInval or ENoLck or EAccess or EPerm when unknown.Count == 0: break;   // no lease here: every process of ours was read
                    case EInval or ENoLck or EAccess or EPerm:
                        throw new InvalidOperationException($"files may be in use by {string.Join(", ", unknown.Select(u => $"{u.Exe} (pid {u.Pid})"))}, whose open files can't be read ({t.Name})");
                    case var errno: throw new InvalidOperationException($"files can't be checked ({t.Name}: {Marshal.GetPInvokeErrorMessage(errno)})");
                }
            }

            var deleted = 0;
            foreach (var t in all)
            {
                if (held.Elapsed > hold) throw new IOException($"deleting took too long, {deleted} of {all.Count} files deleted ({t.Name})");
                // a file renamed over the name meanwhile is moved aside with it, found there and put back
                var aside = $".scskiller-delete-{Guid.NewGuid():N}";
                if (renameat(Fd(t.Dir), t.Name, Fd(t.Dir), aside) != 0) throw new IOException($"{Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}, {deleted} of {all.Count} files deleted ({t.Name})");
                var (why, name) = ((string?)null, t.Name);
                if (StatAt(t.Dir, aside, AtSymlinkNoFollow, out var id, out _) != 0 || id != t.Id) why = "files changed while deleting";
                // an open of a leased file breaks its lease: that one, or one unlinked before it, is in use
                else if (all.FirstOrDefault(l => l.Leased && fcntl(Fd(l.File), FGetLease, 0) != FWrLck) is { } opened) (why, name) = ("files in use by another process", opened.Name);
                else if (unlinkat(Fd(t.Dir), aside, 0) != 0) why = Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError());
                if (why != null)
                {
                    var back = renameat2(Fd(t.Dir), aside, Fd(t.Dir), t.Name, RenameNoReplace) == 0;
                    throw new IOException($"{why}, {deleted} of {all.Count} files deleted ({name}{(back ? "" : $", {t.Name} left as {aside}")})");
                }
                deleted++;
            }
            return deleted;
        }
        finally
        {
            foreach (var t in all) t.File?.Dispose();   // releases its lease
            foreach (var d in dirs.Values) d.Dispose();
        }
    }

    /// <summary>Called with the path <see cref="UnlinkLeased"/> moved aside and where to, before its check. Tests: what another
    /// process does meanwhile.</summary>
    internal static Action<string, string>? MovedAside { get; set; }

    /// <summary>Unlinks <paramref name="path"/> while it names <paramref name="file"/>, open under a write lease
    /// (<see cref="TakeLease"/>), and that file is <paramref name="length"/> bytes: the name is moved to
    /// <paramref name="aside"/> beside it, checked there and unlinked, or put back. False when the name is another file's, the
    /// file is another length, or an open broke the lease. <paramref name="stranded"/>: the name was taken meanwhile, so what
    /// was moved aside stays as <paramref name="aside"/>.</summary>
    internal static bool UnlinkLeased(SafeFileHandle file, string path, long length, string aside, out bool stranded)
    {
        stranded = false;
        if (StatAt(file, "", AtEmptyPath, out var id, out _, out var size) != 0 || size != length) return false;
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var fd = openat(AtFdCwd, folder, ODirectory | OCloExec);
        if (fd < 0) return false;
        using var dir = new SafeFileHandle(fd, ownsHandle: true);
        var name = Path.GetFileName(path);
        if (renameat(fd, name, fd, aside) != 0) return false;
        MovedAside?.Invoke(path, Path.Combine(folder, aside));
        if (StatAt(dir, aside, AtSymlinkNoFollow, out var moved, out _) == 0 && moved == id && fcntl(Fd(file), FGetLease, 0) == FWrLck && unlinkat(fd, aside, 0) == 0)
            return true;
        stranded = renameat2(fd, aside, fd, name, RenameNoReplace) != 0;
        return false;
    }

    public static HashSet<nuint> ProcessesUsing(string path) =>
        StatAt(null, path, 0, out var id, out _) != 0 ? []
            : [.. Scan(new HashSet<FileId> { id }, new Dictionary<FileId, FileId> { [MapsId(null, path, 0, id)] = id }).Holding.Select(h => (nuint)h.Pid)];

    /// <summary>A process has the file open; null when that can't be told.</summary>
    public static bool? InUse(string path) => ProcessesUsing([path]) is var (holding, unknown) && holding.Count == 0 && unknown ? null : holding.Count > 0;

    /// <summary>The processes that have one of <paramref name="paths"/> open or mapped, in one pass over /proc, and whether a
    /// process of this user whose open files can't be read might too.</summary>
    public static (HashSet<nuint> Holding, bool Unknown) ProcessesUsing(IEnumerable<string> paths)
    {
        var (ids, mapped) = (new HashSet<FileId>(), new Dictionary<FileId, FileId>());
        foreach (var path in paths)
            if (StatAt(null, path, 0, out var id, out _) == 0 && ids.Add(id)) mapped[MapsId(null, path, 0, id)] = id;
        if (ids.Count == 0) return ([], false);
        var (holding, unknown) = Scan(ids, mapped);
        return ([.. holding.Select(h => (nuint)h.Pid)], unknown.Count > 0);
    }

    /// <summary>The write lease <see cref="TakeLease"/> took on the file still holds: nothing opened the file since.</summary>
    internal static bool Leased(SafeFileHandle file) => fcntl(Fd(file), FGetLease, 0) == FWrLck;

    sealed class Target(SafeFileHandle dir, string name, FileId id)
    {
        public SafeFileHandle Dir { get; } = dir;
        public string Name { get; } = name;
        public FileId Id { get; } = id;
        public SafeFileHandle? File { get; set; }
        public bool Leased { get; set; }
    }

    /// <summary>The processes that have one of <paramref name="ids"/> open or mapped (<paramref name="mapped"/>: each as maps
    /// names it, see <see cref="MapsId"/>), and the processes of this user whose open files or maps can't be read. Another
    /// user's processes are left out.</summary>
    static (List<(int Pid, string Exe, FileId Id)> Holding, List<(int Pid, string Exe)> Unknown) Scan(IReadOnlySet<FileId> ids,
        IReadOnlyDictionary<FileId, FileId> mapped)
    {
        var (holding, unknown) = (new List<(int, string, FileId)>(), new List<(int, string)>());
        var named = new Dictionary<int, string>();
        foreach (var p in Processes()) named.TryAdd(p.Pid, p.Exe);
        // the snapshot leaves out a process whose stat can't be read (hidepid, a non-dumpable process): /proc still lists it
        foreach (var pid in named.Keys.Union(ProcPids()).ToList())
        {
            var exe = named.GetValueOrDefault(pid, "a process");
            if (Ours(pid) is not true) continue;
            HashSet<FileId>? open;
            try { open = OpenFiles(pid); open?.UnionWith(MappedFiles(pid, mapped) ?? []); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { unknown.Add((pid, exe)); continue; }
            foreach (var id in open ?? []) if (ids.Contains(id)) holding.Add((pid, exe, id));
        }
        return (holding, unknown);
    }

    static List<int> ProcPids()
    {
        try { return [.. Directory.EnumerateDirectories(Proc).Select(d => int.TryParse(Path.GetFileName(d), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : 0).Where(pid => pid > 0)]; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>/proc/sys/fs/lease-break-time; the kernel's default when it can't be read.</summary>
    static TimeSpan LeaseBreakTime()
    {
        try { return TimeSpan.FromSeconds(int.Parse(File.ReadAllText(Path.Combine(Proc, "sys", "fs", "lease-break-time")).Trim(), NumberStyles.None, CultureInfo.InvariantCulture)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or OverflowException) { return TimeSpan.FromSeconds(45); }
    }

    /// <summary>Whether one of the process's uids (real, effective, saved, filesystem) is this user's: null when it has
    /// exited, true when its status can't be read.</summary>
    static bool? Ours(int pid)
    {
        string status;
        try { status = File.ReadAllText(Path.Combine(Proc, pid.ToString(CultureInfo.InvariantCulture), "status")); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
        var uids = status.Split('\n').FirstOrDefault(l => l.StartsWith("Uid:", StringComparison.Ordinal))?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return uids == null || uids.Skip(1).Contains(Libc.UserId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The files a process has open, by device and inode; null when it has exited. UnauthorizedAccessException or
    /// IOException when its open files can't be read (one closed meanwhile is left out).</summary>
    internal static HashSet<FileId>? OpenFiles(int pid)
    {
        string[] fds;
        try { fds = Directory.GetFileSystemEntries(Path.Combine(Proc, pid.ToString(CultureInfo.InvariantCulture), "fd")); }
        catch (DirectoryNotFoundException) { return null; }
        var open = new HashSet<FileId>();
        foreach (var fd in fds)
            switch (StatAt(null, fd, 0, out var id, out _))
            {
                case 0: open.Add(id); break;
                case ENoEnt: break;
                case var errno:
                    var why = $"{fd}: {Marshal.GetPInvokeErrorMessage(errno)}";
                    throw errno is EAccess or EPerm ? new UnauthorizedAccessException(why) : new IOException(why);
            }
        return open;
    }

    /// <summary>The files of <paramref name="mapped"/> the process has mapped, which outlives the fd: null when it has exited.
    /// UnauthorizedAccessException or IOException when its maps can't be read.</summary>
    static HashSet<FileId>? MappedFiles(int pid, IReadOnlyDictionary<FileId, FileId> mapped)
    {
        string maps;
        try { maps = File.ReadAllText(Path.Combine(Proc, pid.ToString(CultureInfo.InvariantCulture), "maps")); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        var found = new HashSet<FileId>();
        // start-end perms offset major:minor (hex) inode path
        foreach (var line in maps.Split('\n'))
            if (line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries) is [_, _, _, var dev, var inode, ..] && inode != "0"
                && dev.Split(':') is [var major, var minor]
                && uint.TryParse(major, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var ma)
                && uint.TryParse(minor, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var mi)
                && ulong.TryParse(inode, NumberStyles.None, CultureInfo.InvariantCulture, out var ino)
                && mapped.TryGetValue(new(ma, mi, ino), out var id))
                found.Add(id);
        return found;
    }

    /// <summary><paramref name="id"/> as /proc/pid/maps names it: with its filesystem's device, found through the mount of
    /// <paramref name="name"/> in <paramref name="dir"/>. A btrfs subvolume's files stat with a device of their own. The
    /// stat's device when the mount can't be read.</summary>
    static unsafe FileId MapsId(SafeFileHandle? dir, string name, int flags, FileId id)
    {
        var buf = stackalloc byte[256];
        if (statx(Fd(dir), name, flags, StatxMntId, buf) != 0 || (*(uint*)buf & StatxMntId) == 0) return id;
        var mount = (*(ulong*)(buf + 144)).ToString(CultureInfo.InvariantCulture);
        try
        {
            // mount-id parent-id major:minor root mount-point ...
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
                if (line.Split(' ', 4) is [var at, _, var dev, _] && at == mount && dev.Split(':') is [var major, var minor]
                    && uint.TryParse(major, NumberStyles.None, CultureInfo.InvariantCulture, out var ma)
                    && uint.TryParse(minor, NumberStyles.None, CultureInfo.InvariantCulture, out var mi))
                    return new(ma, mi, id.Inode);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return id;
    }

    internal static int TakeLease(SafeFileHandle file)
    {
        // a broken lease signals its holder: SIGURG is ignored by default, where SIGIO would end this process
        if (fcntl(Fd(file), FSetSig, SigUrg) != 0 || fcntl(Fd(file), FSetLease, FWrLck) != 0) return Marshal.GetLastPInvokeError();
        return 0;
    }

    /// <summary>The folder <paramref name="below"/> names under <paramref name="root"/>, each folder below it opened without
    /// following a link.</summary>
    static SafeFileHandle OpenFolder(string root, string[] below)
    {
        var dir = Open(null, root, ODirectory);
        foreach (var name in below)
        {
            using var parent = dir;
            dir = Open(parent, name, ODirectory | ONoFollow);
        }
        return dir;
    }

    /// <summary>Opens <paramref name="name"/> read-only in <paramref name="dir"/> (null: the working folder).</summary>
    static SafeFileHandle Open(SafeFileHandle? dir, string name, int flags)
    {
        var fd = openat(Fd(dir), name, flags | OCloExec);
        if (fd >= 0) return new SafeFileHandle(fd, ownsHandle: true);
        throw new InvalidOperationException($"files can't be opened ({Path.GetFileName(name)}: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())})");
    }

    static int StatAt(SafeFileHandle? dir, string name, int flags, out FileId id, out int type) => StatAt(dir, name, flags, out id, out type, out _);

    static unsafe int StatAt(SafeFileHandle? dir, string name, int flags, out FileId id, out int type, out long size)
    {
        // struct statx (linux/stat.h): its layout is the same on every architecture
        var buf = stackalloc byte[256];
        if (statx(Fd(dir), name, flags, StatxType | StatxIno | StatxSize, buf) != 0)
        {
            (id, type, size) = (default, 0, -1);
            return Marshal.GetLastPInvokeError();
        }
        (id, type, size) = (new(*(uint*)(buf + 136), *(uint*)(buf + 140), *(ulong*)(buf + 32)), *(ushort*)(buf + 28) & 0xF000, *(long*)(buf + 40));
        return 0;
    }

    static int Fd(SafeFileHandle? h) => h == null ? AtFdCwd : (int)h.DangerousGetHandle();

    // the asm-generic values: arm and powerpc have their own
    static readonly int ODirectory = RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64 or Architecture.Ppc64le ? 0x4000 : 0x10000;
    static readonly int ONoFollow = RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64 or Architecture.Ppc64le ? 0x8000 : 0x20000;

    const int AtFdCwd = -100, AtSymlinkNoFollow = 0x100, AtEmptyPath = 0x1000, ONonBlock = 0x800, OCloExec = 0x80000, SIfReg = 0x8000;
    const int FSetSig = 10, FSetLease = 1024, FGetLease = 1025, FWrLck = 1, SigUrg = 23, RenameNoReplace = 1;
    const int EPerm = 1, ENoEnt = 2, EAgain = 11, EAccess = 13, EInval = 22, ENoLck = 37;
    const uint StatxType = 0x1, StatxIno = 0x100, StatxSize = 0x200, StatxMntId = 0x1000;

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)] static extern int Linux_openat(int dir, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)] static extern int Linux_renameat(int oldDir, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, int newDir, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)] static extern int Linux_renameat2(int oldDir, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, int newDir, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);
    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)] static extern int Linux_unlinkat(int dir, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] static extern int Linux_fcntl(int fd, int command, int arg);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] static extern unsafe int Linux_statx(int dir, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte* buf);

    // as Libc's: macOS's flags, errno values and struct layouts differ
    static int openat(int dir, string path, int flags) => OperatingSystem.IsLinux() ? Linux_openat(dir, path, flags) : Libc.NoSuchCall();
    static int renameat(int oldDir, string oldPath, int newDir, string newPath) => OperatingSystem.IsLinux() ? Linux_renameat(oldDir, oldPath, newDir, newPath) : Libc.NoSuchCall();
    static int renameat2(int oldDir, string oldPath, int newDir, string newPath, uint flags) => OperatingSystem.IsLinux() ? Linux_renameat2(oldDir, oldPath, newDir, newPath, flags) : Libc.NoSuchCall();
    static int unlinkat(int dir, string path, int flags) => OperatingSystem.IsLinux() ? Linux_unlinkat(dir, path, flags) : Libc.NoSuchCall();
    static int fcntl(int fd, int command, int arg) => OperatingSystem.IsLinux() ? Linux_fcntl(fd, command, arg) : Libc.NoSuchCall();
    static unsafe int statx(int dir, string path, int flags, uint mask, byte* buf) => OperatingSystem.IsLinux() ? Linux_statx(dir, path, flags, mask, buf) : Libc.NoSuchCall();
}
