using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.App;

/// <summary><see cref="OpenRegular"/> and <see cref="Owner"/> on macOS, whose open flags and struct stat aren't Linux's
/// and which has neither O_PATH nor statx.</summary>
static partial class Libc
{
    const int MacONonBlock = 0x4, MacOCloExec = 0x1000000, MacStatSize = 144;

    // no O_PATH: the path is checked before the open, so a FIFO or device is never opened, and the open file again after it
    static unsafe SafeFileHandle? MacOpenRegular(string path)
    {
        var buf = stackalloc byte[MacStatSize];
        if (MacStat(path, buf) != 0) return Handle(-1, path);   // throws as Linux's open does: no file is FileNotFoundException
        if (!MacRegular(buf)) return null;
        var (dev, ino) = (*(int*)buf, *(ulong*)(buf + 8));
        var h = Handle(mac_open(path, MacONonBlock | MacOCloExec), path);
        if (MacFstat((int)h.DangerousGetHandle(), buf) == 0 && MacRegular(buf) && *(int*)buf == dev && *(ulong*)(buf + 8) == ino) return h;
        h.Dispose();
        return null;
    }

    static unsafe (uint Uid, UnixFileMode Mode)? MacOwner(int fd)
    {
        var buf = stackalloc byte[MacStatSize];
        if (MacFstat(fd, buf) != 0) return null;
        return (*(uint*)(buf + 16), (UnixFileMode)(*(ushort*)(buf + 4) & 0xFFF));   // st_uid, st_mode
    }

    static unsafe bool MacRegular(byte* stat) => (*(ushort*)(stat + 4) & 0xF000) == 0x8000;   // st_mode's S_IFMT is S_IFREG

    // x64 keeps the 32-bit-inode stat under the plain names
    static unsafe int MacStat(string path, byte* buf) => RuntimeInformation.ProcessArchitecture == Architecture.X64 ? mac_stat64(path, buf) : mac_stat(path, buf);
    static unsafe int MacFstat(int fd, byte* buf) => RuntimeInformation.ProcessArchitecture == Architecture.X64 ? mac_fstat64(fd, buf) : mac_fstat(fd, buf);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)] static extern int mac_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "stat", SetLastError = true)] static extern unsafe int mac_stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buf);
    [DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)] static extern unsafe int mac_stat64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buf);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] static extern unsafe int mac_fstat(int fd, byte* buf);
    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)] static extern unsafe int mac_fstat64(int fd, byte* buf);
}
