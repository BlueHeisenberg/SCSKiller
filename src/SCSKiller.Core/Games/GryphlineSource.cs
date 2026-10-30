using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

/// <summary>Games installed by GRYPHLINK, Gryphline's launcher (Hypergryph's in China): each folder of the launcher's
/// games folder (&lt;launcher root&gt;\games\EndField Game) that holds a game exe. The launcher is found through the URL
/// protocols it registers (HKCU or HKLM Software\Classes\gryphlinelauncher* or hypergryphlauncher*, shell\open\command),
/// its root being the command's folder or that folder's parent. A game installed elsewhere is added by hand.</summary>
public sealed class GryphlineSource(IEnumerable<string>? launcherExes = null) : IGameSource
{
    public Store Store => Store.Other;

    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase) { ["Endfield"] = "Arknights: Endfield" };

    public IReadOnlyList<Game> Discover()
    {
        var games = new List<Game>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var exe in launcherExes ?? (OperatingSystem.IsWindows() ? LauncherExes() : []))
        {
            var dir = Path.GetDirectoryName(exe);
            foreach (var root in new[] { dir, Path.GetDirectoryName(dir ?? "") }.OfType<string>().Where(r => r.Length > 0))
            {
                var folder = Path.Combine(root, "games");
                if (!Directory.Exists(folder)) continue;
                string[] installs;
                try { installs = Directory.GetDirectories(folder); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var install in installs)
                    try
                    {
                        if (!seen.Add(GameFiles.DirKey(install)) || GameFiles.FindExe(install) is not { } game) continue;
                        var name = Path.GetFileNameWithoutExtension(game);
                        games.Add(new Game($"gryphline:{name.ToLowerInvariant()}", Names.GetValueOrDefault(name) ?? Path.GetFileName(install), Store.Other, install, game));
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("GRYPHLINK", install, e); }
            }
        }
        return games;
    }

    [SupportedOSPlatform("windows")]
    static IEnumerable<string> LauncherExes()
    {
        var exes = new List<string>();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            try
            {
                using var classes = hive.OpenSubKey(@"Software\Classes");
                if (classes == null) continue;
                foreach (var name in classes.GetSubKeyNames().Where(n => n.StartsWith("gryphlinelauncher", StringComparison.OrdinalIgnoreCase)
                             || n.StartsWith("hypergryphlauncher", StringComparison.OrdinalIgnoreCase)))
                {
                    using var command = classes.OpenSubKey($@"{name}\shell\open\command");
                    if (command?.GetValue(null) is string line && ExeOf(line) is { } exe && File.Exists(exe)) exes.Add(exe);
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return exes.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The exe a shell command line starts: its quoted first part, else up to the first ".exe".</summary>
    internal static string? ExeOf(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"')) return command.IndexOf('"', 1) is var end and > 1 ? command[1..end] : null;
        return command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) is var at and >= 0 ? command[..(at + 4)] : null;
    }
}
