using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

/// <summary>Installed Battle.net games from the ordinary Windows uninstall registry (product.db is a protobuf the
/// Battle.net Agent owns; the uninstall entries it also maintains give the same install dir without needing to
/// parse that format). Filtered to Publisher "Blizzard Entertainment", excluding the Battle.net client itself.
/// The id uses the product code from the entry's own uninstall command (--uid=&lt;code&gt;, e.g. "prometheus" for
/// Overwatch) when present, since that's Blizzard's own stable per-game id.</summary>
public sealed class BattleNetSource : IGameSource
{
    public Store Store => Store.Other;

    public IReadOnlyList<Game> Discover() => OperatingSystem.IsWindows() ? Installed() : [];

    [SupportedOSPlatform("windows")]
    static List<Game> Installed()
    {
        var games = new List<Game>();
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        if (root == null) return games;
        foreach (var sub in root.GetSubKeyNames())
            try
            {
                using var key = root.OpenSubKey(sub);
                if (key?.GetValue("Publisher") as string != "Blizzard Entertainment") continue;
                if (key.GetValue("DisplayName") is not string name || name == "Battle.net") continue;
                if (key.GetValue("InstallLocation") is not string install || !Directory.Exists(install)) continue;
                if (GameFiles.FindExe(install) is not { } exe) continue;
                var code = key.GetValue("UninstallString") is string uninst ? Regex.Match(uninst, @"--uid=(\S+)").Groups[1].Value : "";
                if (code.Length == 0) code = Regex.Replace(name, @"\W+", "").ToLowerInvariant();
                games.Add(new Game($"battlenet:{code}", name, Store.Other, install, exe));
            }
            catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("Battle.net", sub, e); }
        return games;
    }
}
