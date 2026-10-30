namespace SCSKiller.Core.App;

public static partial class Elevated
{
    public static partial bool IsAdmin => Libc.IsRoot;
    public static partial string CurrentUserSid => Libc.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static partial bool? OfProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle process, bool deniedIsElevated) => null;
}
