using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.App;

/// <summary>The glibc calls the Linux build needs and .NET doesn't wrap.</summary>
static partial class Libc
{
    /// <summary>A file's statx fields: its device, inode and change time.</summary>
    public readonly record struct FileStat(uint DevMajor, uint DevMinor, ulong Inode, long ChangedSeconds, uint ChangedNanoseconds);

    /// <summary><paramref name="path"/> with every symlink resolved; null when it can't be resolved.</summary>
    public static string? RealPath(string path)
    {
        var p = realpath(path, 0);
        if (p == 0) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { free(p); }
    }

    public static FileStat? Stat(string path) => Stat(AtFdCwd, path, 0);

    /// <summary>The identity of what the name <paramref name="path"/> holds, a symlink there not followed and nothing opened:
    /// an open of a file this process holds under a lease breaks it. Null when the name holds nothing.</summary>
    public static FileStat? StatNoFollow(string path) => Stat(AtFdCwd, path, AtSymlinkNoFollow);

    /// <summary>The path's owner and st_mode (its type included), a symlink not followed; null when they can't be read.</summary>
    public static unsafe (uint Uid, uint Mode)? Lstat(string path)
    {
        var buf = stackalloc byte[256];
        if (statx(AtFdCwd, path, AtSymlinkNoFollow, StatxType | StatxMode | StatxUid, buf) != 0) return null;
        return (*(uint*)(buf + 20), *(ushort*)(buf + 28));   // stx_uid, stx_mode
    }

    /// <summary>The errno of reading <paramref name="path"/>'s access ACL (lgetxattr of system.posix_acl_access, a symlink
    /// not followed), 0 when it has one: <see cref="NoData"/> when it has none, <see cref="NotSupported"/> when its file
    /// system keeps none.</summary>
    public static int AccessAcl(string path) => lgetxattr(path, "system.posix_acl_access", 0, 0) >= 0 ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>ENODATA and ENOTSUP.</summary>
    public const int NoData = ENoData, NotSupported = ENotSup;

    /// <summary>The file's modification time in seconds and nanoseconds, its symlinks followed; null when it can't be had.</summary>
    public static unsafe (long Seconds, uint Nanoseconds)? Modified(string path)
    {
        var buf = stackalloc byte[256];
        if (statx(AtFdCwd, path, 0, StatxMtime, buf) != 0) return null;
        return (*(long*)(buf + 112), *(uint*)(buf + 120));   // stx_mtime
    }

    /// <summary>A file's birth time as statx gave it: <see cref="Error"/> is its errno, and <see cref="Mask"/> (stx_mask)
    /// lacks <see cref="StatxBtime"/> when the file system keeps none.</summary>
    public readonly record struct Birth(int Error, uint Mask, long Seconds, uint Nanoseconds);

    /// <summary><paramref name="path"/>'s birth time, its symlinks followed.</summary>
    public static unsafe Birth Born(string path)
    {
        var buf = stackalloc byte[256];
        if (statx(AtFdCwd, path, 0, StatxBtime, buf) != 0) return new(Marshal.GetLastPInvokeError(), 0, 0, 0);
        return new(0, *(uint*)buf, *(long*)(buf + 80), *(uint*)(buf + 88));   // stx_mask, stx_btime
    }

    public const uint StatxBtime = 0x800;

    /// <summary>The id of the mount holding <paramref name="path"/>, its symlinks followed, as /proc/self/mountinfo numbers
    /// mounts; null when it can't be had (a kernel before 5.8 has no STATX_MNT_ID).</summary>
    public static unsafe ulong? MountId(string path)
    {
        var buf = stackalloc byte[256];
        if (statx(AtFdCwd, path, 0, StatxMntId, buf) != 0 || (*(uint*)buf & StatxMntId) == 0) return null;
        return *(ulong*)(buf + 144);   // stx_mnt_id
    }

    /// <summary>The device number of the block device <paramref name="path"/> names, its symlinks followed; null when it
    /// isn't one or can't be read.</summary>
    public static unsafe (uint Major, uint Minor)? BlockDevice(string path)
    {
        var buf = stackalloc byte[256];
        if (statx(AtFdCwd, path, 0, StatxType, buf) != 0 || (*(ushort*)(buf + 28) & 0xF000) != 0x6000) return null;   // stx_mode's S_IFMT is S_IFBLK
        return (*(uint*)(buf + 128), *(uint*)(buf + 132));   // stx_rdev_major, stx_rdev_minor
    }

    /// <summary>Whether <paramref name="path"/> itself is a symlink, and its hard link count; null when it can't be had.</summary>
    public static unsafe (bool Symlink, uint Links)? Links(string path)
    {
        var buf = stackalloc byte[256];
        if (statx(AtFdCwd, path, AtSymlinkNoFollow, StatxType | StatxNlink, buf) != 0) return null;
        return ((*(ushort*)(buf + 28) & 0xF000) == 0xA000, *(uint*)(buf + 16));   // stx_mode's S_IFMT is S_IFLNK, stx_nlink
    }

    /// <summary>A read handle on <paramref name="path"/>, its symlinks followed, when it is a regular file; null for a FIFO,
    /// socket or device, which is never opened: the check is on an O_PATH handle, and the read handle reopens that file.</summary>
    public static unsafe SafeFileHandle? OpenRegular(string path)
    {
        if (OperatingSystem.IsMacOS()) return MacOpenRegular(path);
        using var at = Open(path, OPath | OCloExec);
        var buf = stackalloc byte[256];
        if (statx((int)at.DangerousGetHandle(), "", AtEmptyPath, StatxType, buf) != 0 || (*(ushort*)(buf + 28) & 0xF000) != 0x8000) return null;   // stx_mode's S_IFMT is S_IFREG
        // O_NONBLOCK: another process's write lease fails the open instead of holding it until the lease breaks
        return Open($"/proc/self/fd/{at.DangerousGetHandle()}", ONonBlock | OCloExec);
    }

    /// <summary>A read handle on the regular file at <paramref name="path"/> itself, made the owner's alone (0600) when it
    /// isn't; null when it isn't a regular file. A symlink there fails the open (ELOOP, an IOException), as does a mode that
    /// can't be changed.</summary>
    public static unsafe SafeFileHandle? OpenOwnerOnly(string path)
    {
        var h = Open(path, ONoFollow | ONonBlock | OCloExec);
        try
        {
            var fd = (int)h.DangerousGetHandle();
            var buf = stackalloc byte[256];
            if (statx(fd, "", AtEmptyPath, StatxType | StatxMode, buf) != 0 || (*(ushort*)(buf + 28) & 0xF000) != 0x8000)   // stx_mode's S_IFMT is S_IFREG
            {
                h.Dispose();
                return null;
            }
            if ((*(ushort*)(buf + 28) & 0xFFF) != OwnerOnly && fchmod(fd, OwnerOnly) != 0)
                throw new IOException($"{path}: its mode can't be made the owner's alone: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
            return h;
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    /// <summary>rw for the owner, nothing for anyone else.</summary>
    public const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    const int OwnerOnly = (int)OwnerReadWrite;

    /// <summary>A read-write handle on <paramref name="path"/>, a regular file with one name, locked as FileStream locks
    /// FileShare.None; throws for a symlink (never followed), a hard link or a file another handle locks.</summary>
    public static unsafe SafeFileHandle OpenUnlinked(string path)
    {
        var h = Open(path, ORdWr | ONoFollow | ONonBlock | OCloExec);
        var fd = (int)h.DangerousGetHandle();
        var buf = stackalloc byte[256];
        if (statx(fd, "", AtEmptyPath, StatxType | StatxNlink, buf) != 0 || (*(ushort*)(buf + 28) & 0xF000) != 0x8000 || *(uint*)(buf + 16) != 1)   // S_IFREG, stx_nlink
        {
            h.Dispose();
            throw new IOException($"{path} is a link: not written");
        }
        if (flock(fd, LockEx | LockNb) != 0 && Marshal.GetLastPInvokeError() == EWouldBlock)   // as FileStream: a file system without locks still opens
        {
            h.Dispose();
            throw new IOException($"{path} is in use");
        }
        return h;
    }

    static SafeFileHandle Open(string path, int flags) => Handle(open(path, flags), path);

    static SafeFileHandle Handle(int fd, string path)
    {
        if (fd >= 0) return new SafeFileHandle(fd, ownsHandle: true);
        var errno = Marshal.GetLastPInvokeError();
        var why = $"{path}: {Marshal.GetPInvokeErrorMessage(errno)}";
        throw errno is EAccess or EPerm ? new UnauthorizedAccessException(why) : errno == ENoEnt ? new FileNotFoundException(why, path) : new IOException(why);
    }

    public static FileStat? Stat(SafeFileHandle h)
    {
        var added = false;
        try
        {
            h.DangerousAddRef(ref added);
            return Stat((int)h.DangerousGetHandle(), "", AtEmptyPath);
        }
        finally { if (added) h.DangerousRelease(); }
    }

    static unsafe FileStat? Stat(int dir, string path, int flags)
    {
        // struct statx (linux/stat.h): its layout is the same on every architecture
        var buf = stackalloc byte[256];
        if (statx(dir, path, flags, StatxIno | StatxCtime, buf) != 0) return null;
        return new FileStat(*(uint*)(buf + 136), *(uint*)(buf + 140), *(ulong*)(buf + 32), *(long*)(buf + 96), *(uint*)(buf + 104));
    }

    /// <summary>Gives <paramref name="from"/> the name <paramref name="to"/>, never replacing a file of that name and never
    /// copying (another file system: EXDEV): renameat2's RENAME_NOREPLACE, else link(2) and unlink(2) where the file system
    /// lacks it. The errno, 0 when done, EEXIST when <paramref name="to"/> exists.</summary>
    public static int RenameNew(string from, string to)
    {
        if (renameat2(AtFdCwd, from, AtFdCwd, to, RenameNoReplace) == 0) return 0;
        var errno = Marshal.GetLastPInvokeError();
        if (errno is not (EInval or ENoSys) || Directory.Exists(from)) return errno;   // link(2) can't take a folder
        if (link(from, to) != 0) return Marshal.GetLastPInvokeError();
        return unlink(from) == 0 ? 0 : Marshal.GetLastPInvokeError();
    }

    /// <summary>Swaps what <paramref name="a"/> and <paramref name="b"/> name in one step (renameat2's RENAME_EXCHANGE). The
    /// errno, 0 when done: EINVAL where the file system can't, ENOENT when either is missing.</summary>
    public static int Exchange(string a, string b) => renameat2(AtFdCwd, a, AtFdCwd, b, RenameExchange) == 0 ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>Makes the empty file <paramref name="to"/> share <paramref name="from"/>'s extents (FICLONE): a copy that takes
    /// no space until either changes. The errno, 0 when done: EXDEV across file systems, EOPNOTSUPP or EINVAL where the file
    /// system shares none.</summary>
    public static int Clone(SafeFileHandle to, SafeFileHandle from)
    {
        bool a = false, b = false;
        try
        {
            to.DangerousAddRef(ref a);
            from.DangerousAddRef(ref b);
            return ioctl((int)to.DangerousGetHandle(), Ficlone, (int)from.DangerousGetHandle()) == 0 ? 0 : Marshal.GetLastPInvokeError();
        }
        finally
        {
            if (a) to.DangerousRelease();
            if (b) from.DangerousRelease();
        }
    }

    // _IOW(0x94, 9, int): powerpc puts the write direction in bit 31
    static readonly nuint Ficlone = RuntimeInformation.ProcessArchitecture is Architecture.Ppc64le ? 0x80049409u : 0x40049409u;

    /// <summary>Takes flock(2)'s exclusive lock on the open file or folder without waiting, held until the handle closes;
    /// false when another handle holds it. A file system without locks takes none, as FileStream does.</summary>
    public static bool TryLock(SafeFileHandle h) => flock((int)h.DangerousGetHandle(), LockEx | LockNb) == 0 || Marshal.GetLastPInvokeError() != EWouldBlock;

    /// <summary>The folder <paramref name="below"/> names under <paramref name="root"/> (whose own links are followed), each
    /// folder below it opened without following a link and, with <paramref name="make"/>, made when missing. The first
    /// <paramref name="links"/> of them may be links when the link and the folder it opens to are both
    /// <paramref name="user"/>'s. FileNotFoundException for a folder missing and not made, IOException for anything else
    /// in the way.</summary>
    public static SafeFileHandle OpenBelow(string root, IReadOnlyList<string> below, bool make, int links = 0, uint user = 0)
    {
        var dir = Open(root, ODirectory | OCloExec);
        try
        {
            for (var i = 0; i < below.Count; i++)
            {
                var name = below[i];
                var parent = (int)dir.DangerousGetHandle();
                var fd = openat(parent, name, ODirectory | ONoFollow | OCloExec);
                var errno = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
                if (errno == ENoEnt && make && (mkdirat(parent, name, 0x1FF) == 0 || Marshal.GetLastPInvokeError() == EExist))
                    errno = (fd = openat(parent, name, ODirectory | ONoFollow | OCloExec)) < 0 ? Marshal.GetLastPInvokeError() : 0;
                var why = errno is ELoop or ENotDir ? "is a link or a file, not a folder" : null;
                if (why != null && i < links && Owner(parent, name, AtSymlinkNoFollow) is { } link && (link.Mode & 0xF000) == 0xA000)   // S_IFLNK
                {
                    // a cache moved to another disk: a link the user made to a folder of the user's
                    why = link.Uid != user ? "is a link that isn't this user's" : null;
                    if (why == null && (fd = openat(parent, name, ODirectory | OCloExec)) < 0) errno = Marshal.GetLastPInvokeError();
                    else if (why == null && Owner(fd, "", AtEmptyPath)?.Uid != user)
                    {
                        new SafeFileHandle(fd, ownsHandle: true).Dispose();
                        why = "is a link to a folder that isn't this user's";
                    }
                    else if (why == null) errno = 0;
                }
                if (errno != 0)
                {
                    var at = $"{Path.Combine(root, Path.Combine([.. below]))}: {name} {why ?? Marshal.GetPInvokeErrorMessage(errno)}";
                    throw why == null && errno == ENoEnt ? new FileNotFoundException(at) : why == null && errno is EAccess or EPerm ? new UnauthorizedAccessException(at) : new IOException(at);
                }
                dir.Dispose();
                dir = new SafeFileHandle(fd, ownsHandle: true);
            }
            return dir;
        }
        catch
        {
            dir.Dispose();
            throw;
        }
    }

    /// <summary>The owner and st_mode of <paramref name="name"/> in the open folder <paramref name="dir"/>; null when they
    /// can't be read.</summary>
    static unsafe (uint Uid, uint Mode)? Owner(int dir, string name, int flags)
    {
        var buf = stackalloc byte[256];
        if (statx(dir, name, flags, StatxType | StatxUid, buf) != 0) return null;
        return (*(uint*)(buf + 20), *(ushort*)(buf + 28));   // stx_uid, stx_mode
    }

    /// <summary>A read handle on the file at <paramref name="path"/> itself: a symlink there fails the open (ELOOP, an
    /// IOException), and another process's write lease fails it instead of holding it.</summary>
    public static SafeFileHandle OpenNoFollow(string path) => Open(path, ONoFollow | ONonBlock | OCloExec);

    /// <summary>What the name <paramref name="path"/> holds, a symlink there not followed: its identity, and a read handle
    /// when it is a regular file. A symlink, FIFO, socket, device or folder is never opened to be read: the check is on an
    /// O_PATH handle. Null when the name holds nothing.</summary>
    public static unsafe (FileStat Stat, SafeFileHandle? File)? OpenEntry(string path)
    {
        SafeFileHandle at;
        try { at = Open(path, OPath | ONoFollow | OCloExec); }
        catch (FileNotFoundException) { return null; }
        using (at)
        {
            var buf = stackalloc byte[256];
            if (statx((int)at.DangerousGetHandle(), "", AtEmptyPath, StatxType | StatxIno | StatxCtime, buf) != 0)
                throw new IOException($"{path}: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
            var stat = new FileStat(*(uint*)(buf + 136), *(uint*)(buf + 140), *(ulong*)(buf + 32), *(long*)(buf + 96), *(uint*)(buf + 104));
            // O_NONBLOCK: another process's write lease fails the open instead of holding it until the lease breaks
            return (stat, (*(ushort*)(buf + 28) & 0xF000) == 0x8000 ? Open($"/proc/self/fd/{at.DangerousGetHandle()}", ONonBlock | OCloExec) : null);   // S_IFREG
        }
    }

    /// <summary>What the name <paramref name="name"/> in the open folder <paramref name="dir"/> holds, a symlink there not
    /// followed: its type (st_mode's <see cref="ModeType"/> bits), link count, length and inode. Null when it holds nothing
    /// or can't be read.</summary>
    public static unsafe (uint Type, uint Links, long Length, ulong Inode)? EntryAt(SafeFileHandle dir, string name)
    {
        var added = false;
        try
        {
            dir.DangerousAddRef(ref added);
            var buf = stackalloc byte[256];
            if (statx((int)dir.DangerousGetHandle(), name, AtSymlinkNoFollow, StatxType | StatxNlink | StatxIno | StatxSize, buf) != 0) return null;
            return ((uint)(*(ushort*)(buf + 28) & 0xF000), *(uint*)(buf + 16), *(long*)(buf + 40), *(ulong*)(buf + 32));   // stx_mode, stx_nlink, stx_size, stx_ino
        }
        finally { if (added) dir.DangerousRelease(); }
    }

    /// <summary>A read handle on the regular file <paramref name="name"/> in the open folder <paramref name="dir"/> that has
    /// no other name, with its length: a symlink, FIFO, socket, device, folder or a hard link is never opened to be read (the
    /// check is on an O_PATH handle, which the read handle reopens). Null for anything else or nothing.</summary>
    public static unsafe (SafeFileHandle File, long Length)? OpenFileAt(SafeFileHandle dir, string name)
    {
        var added = false;
        int at;
        try
        {
            dir.DangerousAddRef(ref added);
            at = openat((int)dir.DangerousGetHandle(), name, OPath | ONoFollow | OCloExec);
        }
        finally { if (added) dir.DangerousRelease(); }
        if (at < 0) return null;
        using var path = new SafeFileHandle(at, ownsHandle: true);
        var buf = stackalloc byte[256];
        if (statx(at, "", AtEmptyPath, StatxType | StatxNlink | StatxSize, buf) != 0 || (*(ushort*)(buf + 28) & 0xF000) != 0x8000 || *(uint*)(buf + 16) != 1) return null;   // S_IFREG, stx_nlink
        var fd = open($"/proc/self/fd/{at}", ONonBlock | OCloExec);
        return fd < 0 ? null : (new SafeFileHandle(fd, ownsHandle: true), *(long*)(buf + 40));
    }

    /// <summary>The folder <paramref name="name"/> in the open folder <paramref name="dir"/>, opened without following a link,
    /// made (0700) first with <paramref name="make"/>; null when the name holds anything else or nothing.</summary>
    public static SafeFileHandle? OpenFolderAt(SafeFileHandle dir, string name, bool make = false)
    {
        var added = false;
        try
        {
            dir.DangerousAddRef(ref added);
            var parent = (int)dir.DangerousGetHandle();
            if (make) mkdirat(parent, name, ModeOwnerOnly);   // whatever is there already, the open tells
            var fd = openat(parent, name, ODirectory | ONoFollow | OCloExec);
            return fd < 0 ? null : new SafeFileHandle(fd, ownsHandle: true);
        }
        finally { if (added) dir.DangerousRelease(); }
    }

    /// <summary>Makes the folder <paramref name="name"/> (0700) in the open folder <paramref name="dir"/>, never over anything
    /// there. The errno, 0 when made: EEXIST when the name holds anything.</summary>
    public static int MakeFolderAt(SafeFileHandle dir, string name)
    {
        var added = false;
        try
        {
            dir.DangerousAddRef(ref added);
            return mkdirat((int)dir.DangerousGetHandle(), name, ModeOwnerOnly) == 0 ? 0 : Marshal.GetLastPInvokeError();
        }
        finally { if (added) dir.DangerousRelease(); }
    }

    /// <summary>Makes the file <paramref name="name"/> in the open folder <paramref name="dir"/> with <paramref name="mode"/>
    /// and opens it to read and write: O_EXCL, so never through a link nor over anything there. Null when the name holds
    /// anything already; IOException for any other failure.</summary>
    public static SafeFileHandle? CreateAt(SafeFileHandle dir, string name, UnixFileMode mode)
    {
        var added = false;
        try
        {
            dir.DangerousAddRef(ref added);
            var fd = openat((int)dir.DangerousGetHandle(), name, ORdWr | OCreat | OExcl | ONoFollow | OCloExec, (uint)mode);
            if (fd >= 0) return new SafeFileHandle(fd, ownsHandle: true);
            var errno = Marshal.GetLastPInvokeError();
            return errno == EExist ? null : throw new IOException($"{name}: {Marshal.GetPInvokeErrorMessage(errno)}");
        }
        finally { if (added) dir.DangerousRelease(); }
    }

    /// <summary>Removes the name <paramref name="name"/> from the open folder <paramref name="dir"/>, never following a link:
    /// an empty folder with <paramref name="folder"/>, else anything but a folder. The errno, 0 when removed.</summary>
    public static int UnlinkAt(SafeFileHandle dir, string name, bool folder)
    {
        var added = false;
        try
        {
            dir.DangerousAddRef(ref added);
            return unlinkat((int)dir.DangerousGetHandle(), name, folder ? AtRemoveDir : 0) == 0 ? 0 : Marshal.GetLastPInvokeError();
        }
        finally { if (added) dir.DangerousRelease(); }
    }

    /// <summary>EACCES, EPERM and ENOENT.</summary>
    public const int AccessDenied = EAccess, NotPermitted = EPerm, NoEntry = ENoEnt;

    /// <summary>The bytes the open file takes on disk (stx_blocks), which a sparse file's length overstates and a
    /// preallocated one's understates; null when it can't be read.</summary>
    public static unsafe long? Allocated(SafeFileHandle h)
    {
        var added = false;
        try
        {
            h.DangerousAddRef(ref added);
            var buf = stackalloc byte[256];
            if (statx((int)h.DangerousGetHandle(), "", AtEmptyPath, StatxBlocks, buf) != 0 || (*(uint*)buf & StatxBlocks) == 0) return null;
            return (long)Math.Min(*(ulong*)(buf + 48), long.MaxValue / 512) * 512;   // stx_blocks, in 512-byte units
        }
        finally { if (added) h.DangerousRelease(); }
    }

    /// <summary>Sets the open file's or folder's mode (fchmod). The errno, 0 when set.</summary>
    public static int Chmod(SafeFileHandle h, uint mode)
    {
        var added = false;
        try
        {
            h.DangerousAddRef(ref added);
            return fchmod((int)h.DangerousGetHandle(), (int)mode) == 0 ? 0 : Marshal.GetLastPInvokeError();
        }
        finally { if (added) h.DangerousRelease(); }
    }

    /// <summary>Sets the mode of the folder <paramref name="name"/> in the open folder <paramref name="dir"/>, never through a
    /// link: through an O_PATH handle, which needs no permission on the folder. The errno, 0 when set.</summary>
    public static int ChmodFolderAt(SafeFileHandle dir, string name, uint mode)
    {
        var added = false;
        int at;
        try
        {
            dir.DangerousAddRef(ref added);
            at = openat((int)dir.DangerousGetHandle(), name, OPath | ODirectory | ONoFollow | OCloExec);
        }
        finally { if (added) dir.DangerousRelease(); }
        if (at < 0) return Marshal.GetLastPInvokeError();
        using var path = new SafeFileHandle(at, ownsHandle: true);
        return chmod($"/proc/self/fd/{at}", mode) == 0 ? 0 : Marshal.GetLastPInvokeError();
    }

    /// <summary>Sets the open file's access and modification times to now (futimens); false when it can't.</summary>
    public static bool TouchHandle(SafeFileHandle h)
    {
        var added = false;
        try
        {
            h.DangerousAddRef(ref added);
            return futimens((int)h.DangerousGetHandle(), 0) == 0;
        }
        finally { if (added) h.DangerousRelease(); }
    }

    /// <summary>Puts the folder's entries on disk (fsync), so a rename in it is there before what follows.</summary>
    public static void SyncFolder(string path)
    {
        using var h = Open(path, OCloExec);
        if (fsync((int)h.DangerousGetHandle()) != 0) throw new IOException($"{path}: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
    }

    public const uint ModeType = 0xF000, ModeDirectory = 0x4000, ModePermissions = 0x1FF, ModeOwnerOnly = 0x1C0;

    /// <summary>Makes the folder 0700 (a umask only takes bits away) unless anything is at <paramref name="path"/> already:
    /// mkdir never follows a link nor changes what is there. Whether it worked is for an <see cref="Lstat"/> after it to say.</summary>
    public static void MakePrivateDir(string path) => mkdir(path, ModeOwnerOnly);

    /// <summary>Sends <paramref name="signal"/> to the process; false when it has exited or isn't ours.</summary>
    public static bool Signal(int pid, int signal) => kill(pid, signal) == 0;

    /// <summary>A pidfd for the process (pidfd_open): it names that process alone, never one that gets its pid later. Null
    /// with the errno in <paramref name="error"/> when none can be had: <see cref="NoSuchProcess"/> once the process has
    /// exited, others on a kernel before 5.3, under a seccomp filter or without a free descriptor.</summary>
    public static SafeFileHandle? PidFd(int pid, out int error)
    {
        var fd = syscall(PidfdOpen, pid, 0, 0);
        error = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
        return fd < 0 ? null : new SafeFileHandle((nint)fd, ownsHandle: true);
    }

    /// <summary>ESRCH.</summary>
    public const int NoSuchProcess = ESrch;

    /// <summary>Sends <paramref name="signal"/> through <paramref name="pidfd"/> (pidfd_send_signal); false when its process
    /// has exited.</summary>
    public static bool Signal(SafeFileHandle pidfd, int signal)
    {
        var added = false;
        try
        {
            pidfd.DangerousAddRef(ref added);
            return syscall(PidfdSendSignal, pidfd.DangerousGetHandle(), signal, 0, 0) == 0;
        }
        finally { if (added) pidfd.DangerousRelease(); }
    }

    // the generic table's numbers, the same on x86_64 and arm64
    const long PidfdSendSignal = 424, PidfdOpen = 434;

    public const int SigKill = 9, SigCont = 18, SigStop = 19;

    /// <summary>This thread's nice value: Linux keeps one per thread, and a process starts with the one of the thread that
    /// made it.</summary>
    public static int Nice()
    {
        var nice = getpriority(PrioProcess, 0);
        return nice == -1 && Marshal.GetLastPInvokeError() != 0 ? 0 : nice;   // -1 is also a nice value: errno tells
    }

    /// <summary>Raises this thread's nice to <paramref name="nice"/>, never lowers it: an unprivileged thread can't get a lower
    /// one back.</summary>
    public static bool LowerThreadPriority(int nice) => Nice() >= nice || setpriority(PrioProcess, 0, nice) == 0;

    public static bool IsRoot => geteuid() == 0;
    public static uint EffectiveUserId => geteuid();

    /// <summary>The owner and the permission bits of an open file; null when they can't be read.</summary>
    public static unsafe (uint Uid, UnixFileMode Mode)? Owner(SafeFileHandle h)
    {
        var added = false;
        try
        {
            h.DangerousAddRef(ref added);
            if (OperatingSystem.IsMacOS()) return MacOwner((int)h.DangerousGetHandle());
            var buf = stackalloc byte[256];
            if (statx((int)h.DangerousGetHandle(), "", AtEmptyPath, StatxMode | StatxUid, buf) != 0) return null;
            return (*(uint*)(buf + 20), (UnixFileMode)(*(ushort*)(buf + 28) & 0xFFF));   // stx_uid, stx_mode
        }
        finally { if (added) h.DangerousRelease(); }
    }

    public static uint UserId => getuid();

    /// <summary>This thread's I/O priority (ioprio_get with IOPRIO_WHO_PROCESS and its thread id); null where the syscall
    /// numbers aren't known or it fails.</summary>
    public static int? ThreadIoPriority()
    {
        if (Calls is not (long gettid, long get, _)) return null;
        var p = syscall(get, IoprioWhoProcess, syscall(gettid, 0, 0, 0), 0);
        return p < 0 ? null : (int)p;
    }

    public static bool SetThreadIoPriority(int priority) =>
        Calls is (long gettid, _, long set) && syscall(set, IoprioWhoProcess, syscall(gettid, 0, 0, 0), priority) == 0;

    /// <summary>The best-effort class's lowest level: below every process at the default priority.</summary>
    public const int IoPriorityLowest = 2 << 13 | 7;

    /// <summary>The idle class: the disk only when nothing else uses it, as ionice -c 3.</summary>
    public const int IoPriorityIdle = 3 << 13;

    static (long Gettid, long IoprioGet, long IoprioSet)? Calls => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => (186, 252, 251),
        Architecture.Arm64 => (178, 31, 30),
        _ => null,
    };

    const int PrioProcess = 0, AtFdCwd = -100, AtEmptyPath = 0x1000, AtSymlinkNoFollow = 0x100, IoprioWhoProcess = 1, OPath = 0x200000, ONonBlock = 0x800, OCloExec = 0x80000, ORdWr = 2, LockEx = 2, LockNb = 4, EWouldBlock = 11, EPerm = 1, ENoEnt = 2, EAccess = 13, ESrch = 3, ENoData = 61, ENotSup = 95, EInval = 22, ENoSys = 38, EExist = 17, ENotDir = 20, ELoop = 40, OCreat = 0x40, OExcl = 0x80, AtRemoveDir = 0x200;
    const uint RenameNoReplace = 1, RenameExchange = 2;
    const uint StatxType = 0x1, StatxMode = 0x2, StatxNlink = 0x4, StatxUid = 0x8, StatxMtime = 0x40, StatxIno = 0x100, StatxCtime = 0x80, StatxSize = 0x200, StatxBlocks = 0x400, StatxMntId = 0x1000;
    // the asm-generic value: arm and powerpc have their own
    static readonly int ONoFollow = RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64 or Architecture.Ppc64le ? 0x8000 : 0x20000;
    static readonly int ODirectory = RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64 or Architecture.Ppc64le ? 0x4000 : 0x10000;

    [DllImport("libc", SetLastError = true)] static extern nint realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint resolved);
    [DllImport("libc")] static extern void free(nint p);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)] static extern int Linux_renameat2(int fromDir, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int toDir, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);
    [DllImport("libc", SetLastError = true)] static extern int link([MarshalAs(UnmanagedType.LPUTF8Str)] string from, [MarshalAs(UnmanagedType.LPUTF8Str)] string to);
    [DllImport("libc", SetLastError = true)] static extern int unlink([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] static extern int Linux_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)] static extern int Linux_openat(int dir, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)] static extern int Linux_openat(int dir, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
    [DllImport("libc", SetLastError = true)] static extern int mkdirat(int dir, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)] static extern int Linux_unlinkat(int dir, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "futimens", SetLastError = true)] static extern int Linux_futimens(int fd, nint times);
    [DllImport("libc", SetLastError = true)] static extern int fsync(int fd);
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)] static extern int Linux_ioctl(int fd, nuint request, int arg);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] static extern unsafe int Linux_statx(int dir, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte* buf);
    [DllImport("libc", SetLastError = true)] static extern int fchmod(int fd, int mode);
    [DllImport("libc", SetLastError = true)] static extern int chmod([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc", EntryPoint = "lgetxattr", SetLastError = true)] static extern nint Linux_lgetxattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint value, nuint size);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)] static extern int Linux_flock(int fd, int operation);
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)] static extern int Linux_kill(int pid, int signal);
    [DllImport("libc", SetLastError = true)] static extern int mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc", SetLastError = true)] static extern int getpriority(int which, uint who);
    [DllImport("libc", SetLastError = true)] static extern int setpriority(int which, uint who, int prio);
    [DllImport("libc")] static extern uint geteuid();
    [DllImport("libc")] static extern uint getuid();
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] static extern long Linux_syscall(long number, long a, long b, long c);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] static extern long Linux_syscall(long number, long a, long b, long c, long d);

    // macOS's flags, errno values, struct layouts and syscall numbers differ: there these fail with its ENOSYS (78)
    static int renameat2(int fromDir, string from, int toDir, string to, uint flags) => OperatingSystem.IsLinux() ? Linux_renameat2(fromDir, from, toDir, to, flags) : NoSuchCall();
    static int open(string path, int flags) => OperatingSystem.IsLinux() ? Linux_open(path, flags) : NoSuchCall();
    static int openat(int dir, string path, int flags) => OperatingSystem.IsLinux() ? Linux_openat(dir, path, flags) : NoSuchCall();
    static int openat(int dir, string path, int flags, uint mode) => OperatingSystem.IsLinux() ? Linux_openat(dir, path, flags, mode) : NoSuchCall();
    static int unlinkat(int dir, string path, int flags) => OperatingSystem.IsLinux() ? Linux_unlinkat(dir, path, flags) : NoSuchCall();
    static int futimens(int fd, nint times) => OperatingSystem.IsLinux() ? Linux_futimens(fd, times) : NoSuchCall();
    static int ioctl(int fd, nuint request, int arg) => OperatingSystem.IsLinux() ? Linux_ioctl(fd, request, arg) : NoSuchCall();
    static unsafe int statx(int dir, string path, int flags, uint mask, byte* buf) => OperatingSystem.IsLinux() ? Linux_statx(dir, path, flags, mask, buf) : NoSuchCall();
    static nint lgetxattr(string path, string name, nint value, nuint size) => OperatingSystem.IsLinux() ? Linux_lgetxattr(path, name, value, size) : NoSuchCall();
    static int kill(int pid, int signal) => OperatingSystem.IsLinux() ? Linux_kill(pid, signal) : NoSuchCall();
    static long syscall(long number, long a, long b, long c) => OperatingSystem.IsLinux() ? Linux_syscall(number, a, b, c) : NoSuchCall();
    static long syscall(long number, long a, long b, long c, long d) => OperatingSystem.IsLinux() ? Linux_syscall(number, a, b, c, d) : NoSuchCall();
    static int flock(int fd, int operation)
    {
        var r = Linux_flock(fd, operation);
        if (r != 0 && !OperatingSystem.IsLinux() && Marshal.GetLastPInvokeError() == 35) Marshal.SetLastPInvokeError(EWouldBlock);   // macOS's EWOULDBLOCK
        return r;
    }
    internal static int NoSuchCall()
    {
        Marshal.SetLastPInvokeError(78);
        return -1;
    }
}
