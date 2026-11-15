using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.App;

public static partial class Elevated
{
    public static partial bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static partial string CurrentUserSid => WindowsIdentity.GetCurrent().User!.Value;

    // an allow entry for CreateFiles on the folder itself for the user or a group of the token other than the two an elevated
    // token adds (Administrators, local administrator account), and no deny entry for them
    public static partial bool GameCanWrite(string dir)
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var sids = id.Groups!.Select(g => g.Value).Append(id.User!.Value).Where(s => s is not ("S-1-5-32-544" or "S-1-5-114")).ToHashSet();
            var allow = false;
            foreach (FileSystemAccessRule r in new DirectoryInfo(dir).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (!sids.Contains(r.IdentityReference.Value) || r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly) || (r.FileSystemRights & FileSystemRights.CreateFiles) == 0) continue;
                if (r.AccessControlType == AccessControlType.Deny) return false;
                allow = true;
            }
            return allow;
        }
        catch (SystemException) { return false; }
    }

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
