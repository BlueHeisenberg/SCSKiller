using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.App;

public static partial class Elevated
{
    public static partial bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static partial string CurrentUserSid => WindowsIdentity.GetCurrent().User!.Value;

    // An elevated process's token refuses a medium-integrity reader: access denied counts as elevated.
    public static partial bool? OfProcess(SafeProcessHandle process, bool deniedIsElevated)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
            return deniedIsElevated && Marshal.GetLastPInvokeError() == AccessDenied ? true : null;
        using (token)
            return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _) ? elevated != 0 : null;
    }

    const uint TokenQuery = 0x8;
    const int TokenElevation = 20, AccessDenied = 5;

    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out int info, int length, out int returned);
}
