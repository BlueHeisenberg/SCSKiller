using Microsoft.Win32;

namespace SCSKiller.Core.App;

/// <summary>An unpackaged app's toast activator: HKCU Software\Classes\AppUserModelId\&lt;aumid&gt; names a COM class
/// (CustomActivator) whose LocalServer32 is the exe a toast click starts while the app isn't running. The Windows App SDK
/// keeps a registration it finds, so one written by an older copy in another folder outlives every update.</summary>
public static class ToastActivator
{
    /// <summary>Velopack's shortcuts carry "velopack." + packId as their AUMID, and the toasts of an app started from them too.</summary>
    public const string VelopackAumid = "velopack.SCSKiller.App";

    /// <summary>Points the activator of each of <paramref name="aumids"/> at <paramref name="exe"/>, its arguments kept, when
    /// it starts some copy of SCSKiller.exe: another app's class is never touched. <paramref name="classes"/> is
    /// HKCU\Software\Classes. True if one changed.</summary>
    public static bool Repair(RegistryKey classes, IEnumerable<string> aumids, string exe)
    {
        bool changed = false;
        foreach (var aumid in aumids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var app = classes.OpenSubKey($@"AppUserModelId\{aumid}");
            if (app?.GetValue("CustomActivator") is not string clsid || clsid.IndexOfAny(['\\', '/']) >= 0) continue;
            using var server = classes.OpenSubKey($@"CLSID\{clsid}\LocalServer32", writable: true);
            if (server?.GetValue(null) is not string command || !Ours(command)) continue;
            // COM starts ServerExecutable when it's there, with the command line as its arguments
            var named = server.GetValue("ServerExecutable") as string;
            if (named != null && !Ours(named)) continue;
            // quoted: an unquoted path with a space can start another exe
            if (!command.TrimStart().StartsWith('"') || !WindowsStartup.PointsAt(command, exe))
            {
                server.SetValue(null, Rewrite(command, exe));
                changed = true;
            }
            if (named != null && !string.Equals(named.Trim().Trim('"'), exe, StringComparison.OrdinalIgnoreCase))
            {
                server.SetValue("ServerExecutable", exe);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary><paramref name="command"/> with its exe replaced by <paramref name="exe"/>, quoted, its arguments kept.</summary>
    public static string Rewrite(string command, string exe)
    {
        var c = command.Trim();
        return $"\"{exe}\"{c[ExeEnd(c)..]}";
    }

    static bool Ours(string command)
    {
        var c = command.Trim();
        return Path.GetFileName(c[..ExeEnd(c)].Trim('"')).Equals("SCSKiller.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where the exe's path ends in a trimmed command line: its closing quote, else ".exe", else the first space.</summary>
    static int ExeEnd(string c)
    {
        if (c.StartsWith('"')) return c.IndexOf('"', 1) is var q and > 0 ? q + 1 : c.Length;   // an unclosed quote: all of it
        if (c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) is var e and >= 0) return e + 4;
        return c.IndexOf(' ') is var s and >= 0 ? s : c.Length;
    }
}
