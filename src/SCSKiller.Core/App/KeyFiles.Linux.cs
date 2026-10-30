namespace SCSKiller.Core.App;

public static partial class KeyFiles
{
    public static partial string Identity(string path) => Format(Libc.Stat(path));

    private static partial string Identity(Microsoft.Win32.SafeHandles.SafeFileHandle h) => Format(Libc.Stat(h));

    static string Format(Libc.FileStat? s) =>
        s is { } f ? $"{f.ChangedSeconds}.{f.ChangedNanoseconds:D9}/{f.DevMajor:x}:{f.DevMinor:x}-{f.Inode:x16}" : "";
}
