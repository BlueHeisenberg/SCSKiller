namespace SCSKiller.Core.App;

static partial class IoErrors
{
    internal static partial bool HeldElsewhere(IOException e) => (e.HResult & 0xFFFF) is 32 or 33;   // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
    internal static partial bool DiskFull(IOException e) => (e.HResult & 0xFFFF) is 0x70 or 0x27;   // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
}
