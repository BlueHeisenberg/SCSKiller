using Microsoft.Win32;

namespace SCSKiller.Core.App;

/// <summary>What the Windows App SDK's toast registration leaves for an unpackaged app: HKCU's AppUserModelId\&lt;aumid&gt;
/// (its CustomActivator names the COM server), CLSID\{guid}\LocalServer32 starting the exe, and the icon it copied.</summary>
public static class ToastRegistration
{
    public const string AumidKeys = @"Software\Classes\AppUserModelId\", ClsidKeys = @"Software\Classes\CLSID\";

    /// <summary>The uninstall's step: removes <paramref name="aumid"/>'s key, its icon when it is the SDK's copy for that
    /// AUMID, and its COM server only when that starts <paramref name="appExe"/>. Other identities' keys are never read.
    /// <paramref name="read"/> is (key, value name, "" for the default), <paramref name="deleteKey"/> deletes a key tree
    /// under HKCU: the registry, or fakes in tests.</summary>
    public static void Remove(string? aumid, string? appExe, Func<string, string, string?> read, Action<string> deleteKey, Action<string> deleteFile, Action<string> log)
    {
        if (string.IsNullOrEmpty(aumid) || aumid.IndexOfAny(['\\', '/']) >= 0 || aumid is "." or ".." || appExe == null) return;
        var key = AumidKeys + aumid;
        var activator = read(key, "CustomActivator");
        var icon = read(key, "IconUri");
        var server = Guid.TryParse(activator, out var clsid) ? ClsidKeys + clsid.ToString("B").ToUpperInvariant() : null;
        // only the SDK's quoted form: an unquoted command's exe is ambiguous ("a.exe.b.exe")
        var command = server == null ? null : read(server + @"\LocalServer32", "")?.TrimStart();
        var ours = command != null && command.StartsWith('"') && WindowsStartup.PointsAt(command, appExe);

        Try(() => deleteKey(key), activator != null || icon != null ? $"Removed toast registration {aumid}" : null);
        if (server != null)
        {
            if (ours) Try(() => deleteKey(server), $"Removed toast COM server {server}");
            else log($"Kept toast COM server {server}: it starts another exe");
        }
        // the SDK copies the icon to %LOCALAPPDATA%\Microsoft\WindowsAppSDK\<aumid>.png
        if (icon != null && string.Equals(icon, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsAppSDK", aumid + ".png"), StringComparison.OrdinalIgnoreCase))
            Try(() => deleteFile(icon), null);

        void Try(Action step, string? done)
        {
            try { step(); if (done != null) log(done); }
            catch (Exception e) { log($"Toast registration of {aumid}: {e.GetType().Name}: {e.Message}"); }
        }
    }

    public static string? ReadRegistry(string key, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        return k?.GetValue(name) as string;
    }

    public static void DeleteRegistry(string key) => Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
}
