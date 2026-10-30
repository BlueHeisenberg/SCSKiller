using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

/// <summary>Installed Ubisoft Connect games from HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\&lt;id&gt;
/// ("InstallDir"); the exe is picked from the install dir the same heuristic way Steam/Epic do (Connect doesn't
/// keep a per-game manifest as convenient as GOG's or Epic's, just the install path).</summary>
public sealed class UbisoftSource : IGameSource
{
    public Store Store => Store.Other;

    public IReadOnlyList<Game> Discover() => OperatingSystem.IsWindows() ? Installed() : [];

    [SupportedOSPlatform("windows")]
    static List<Game> Installed()
    {
        var games = new List<Game>();
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
        if (root == null) return games;
        foreach (var id in root.GetSubKeyNames())
            try
            {
                using var key = root.OpenSubKey(id);
                if (key?.GetValue("InstallDir") is not string install || !Directory.Exists(install)) continue;
                if (GameFiles.FindExe(install) is not { } exe) continue;
                games.Add(new Game($"ubisoft:{id}", GameFiles.FolderName(install), Store.Other, install, exe));
            }
            catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("Ubisoft Connect", $"ubisoft:{id}", e); }
        return games;
    }
}
