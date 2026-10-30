namespace SCSKiller.Core.App;

static partial class IoErrors
{
    // .NET's FileShare.None is an flock there: a held one fails with EWOULDBLOCK as the HResult
    internal static partial bool HeldElsewhere(IOException e) => e.HResult == 11;
}
