using Microsoft.Win32;

namespace SCSKiller.Core.App;

/// <summary>"Start SCSKiller when Windows starts" (<see cref="Settings.StartWithWindows"/>): the HKCU Run value that starts
/// the app hidden in the notification area at sign-in. An installed copy and portable or test copies share the one value,
/// so a copy changes it only when it points at that copy's exe, or when the user turns the setting on or off there.</summary>
public static class WindowsStartup
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "SCSKiller", TrayArg = "--tray";

    public static string Command(string appExe) => $"\"{appExe}\" {TrayArg}";

    /// <summary>The user turned the setting on or off in this copy (or the uninstall turns it off): on writes this exe's
    /// command, off removes the value only if it points at <paramref name="appExe"/>. <paramref name="read"/>/<paramref name="write"/>
    /// are the value (null = absent; a value that isn't a string comes back as its text): the registry, or a fake in tests.
    /// True if it changed.</summary>
    public static bool Set(bool on, string appExe, Func<string?> read, Action<string?> write)
    {
        var value = read();
        var want = on ? Command(appExe) : null;
        if (value == want || !on && !PointsAt(value, appExe)) return false;
        write(want);
        return true;
    }

    /// <summary>App start: a value that points at <paramref name="appExe"/> follows the setting; another copy's value is
    /// never touched while its exe exists (<paramref name="exists"/>: File.Exists, or a fake in tests). An
    /// <paramref name="installed"/> copy whose setting is on writes its own command over a missing value, or over one whose
    /// fully qualified exe is gone.</summary>
    public static bool Reconcile(bool on, string appExe, bool installed, Func<string?> read, Action<string?> write, Func<string, bool> exists)
    {
        var value = read();
        var want = on ? Command(appExe) : null;
        var restore = on && installed && (value == null || Target(value) is { } target && !exists(target));
        if (value == want || !restore && !PointsAt(value, appExe)) return false;
        write(want);
        return true;
    }

    /// <summary>Whether a Run command starts <paramref name="exe"/>: the same full path, ignoring quotes, arguments, case
    /// and the \\?\ prefix.</summary>
    public static bool PointsAt(string? command, string exe) =>
        Target(command) is { } p && Normalize(exe) is { } e && string.Equals(p, e, StringComparison.OrdinalIgnoreCase);

    /// <summary>The full path of the exe a Run command starts; null when it isn't a fully qualified path.</summary>
    static string? Target(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = command.Trim();
        int end = c.IndexOf('"', 1), exeEnd = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        var path = c[0] == '"' ? end > 0 ? c[1..end] : c[1..]
            : exeEnd >= 0 ? c[..(exeEnd + 4)] : c.Split(' ')[0];   // unquoted: Windows takes the path through ".exe", spaces and all
        return Normalize(path);
    }

    static string? Normalize(string path)
    {
        path = path.Trim();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\")) path = path[4..];
        if (!Path.IsPathFullyQualified(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public static string? ReadRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) switch { null => null, string s => s, var other => other.ToString() ?? "" };
    }

    public static void WriteRegistry(string? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (value == null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        else key.SetValue(ValueName, value);
    }
}
