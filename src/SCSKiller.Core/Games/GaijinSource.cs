using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

/// <summary>Games installed with Gaijin's own launcher, from HKCU\Software\Gaijin\&lt;project&gt; ("InstallDir"). The exe is
/// the one BattlEye\BELauncher.ini starts (64BitExe): the install root holds the launcher, which FindExe would take, and
/// the anti-cheat there keeps it from reading the exes to look further.</summary>
public sealed class GaijinSource : IGameSource
{
    public Store Store => Store.Other;

    public IReadOnlyList<Game> Discover() => OperatingSystem.IsWindows() ? Installed() : [];

    [SupportedOSPlatform("windows")]
    static List<Game> Installed()
    {
        var games = new List<Game>();
        using var root = Registry.CurrentUser.OpenSubKey(@"Software\Gaijin");
        if (root == null) return games;
        foreach (var project in root.GetSubKeyNames())
            try
            {
                using var key = root.OpenSubKey(project);
                if (key?.GetValue("InstallDir") is not string install || !Directory.Exists(install) || Exe(install) is not { } exe) continue;
                games.Add(new Game($"gaijin:{project.ToLowerInvariant()}", Regex.Replace(project, "(?<=[a-z])(?=[A-Z])", " "), Store.Other, install, exe));
            }
            catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("Gaijin", $"gaijin:{project}", e); }
        return games;
    }

    /// <summary>BELauncher.ini's 64BitExe, when it is a file inside the install; null otherwise.</summary>
    internal static string? Exe(string install)
    {
        var ini = Path.Combine(install, "BattlEye", "BELauncher.ini");
        string[] lines;
        try { lines = File.ReadAllLines(ini); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var rel = lines.Select(l => l.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0].Trim().Equals("64BitExe", StringComparison.OrdinalIgnoreCase))?[1].Trim();
        if (string.IsNullOrEmpty(rel) || Path.IsPathRooted(rel)) return null;
        var exe = Path.GetFullPath(GameFiles.Under(install, rel));
        return GameFiles.Inside(install, exe) && File.Exists(exe) ? exe : null;
    }
}
