namespace SCSKiller.Core.App;

static partial class IoErrors
{
    internal static partial bool HeldElsewhere(IOException e) => (e.HResult & 0xFFFF) is 32 or 33;   // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
}
