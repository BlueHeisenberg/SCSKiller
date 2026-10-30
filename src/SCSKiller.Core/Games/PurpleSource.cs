using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

/// <summary>Installed NCSOFT PURPLE games from the uninstall entries PURPLE writes for each game (HKLM\...\Uninstall\NC &lt;code&gt;:
/// DisplayName, InstallLocation, UninstallString "...\PurpleLauncher.exe" --uninstall &lt;code&gt;). The launcher's own entry
/// uninstalls with Uninstall.exe, so it doesn't match. PURPLE's local game list is a per-account cache, so the code's
/// VersionInfo_&lt;code&gt;.xml in the game folder is what says the download finished; its &lt;Version&gt; is the build.</summary>
public sealed class PurpleSource(IEnumerable<(string Code, string Name, string InstallDir)>? entries = null) : IGameSource
{
    public Store Store => Store.Other;

    public IReadOnlyList<Game> Discover()
    {
        var games = new List<Game>();
        foreach (var (code, name, install) in entries ?? (OperatingSystem.IsWindows() ? Entries() : []))
            try
            {
                if (!Directory.Exists(install)) continue;
                var info = Path.Combine(install, $"VersionInfo_{code}.xml");
                if (!File.Exists(info)) continue;
                string? version;
                try { version = XDocument.Load(info).Root?.Element("Version")?.Value.Trim(); }
                catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException) { continue; }   // rewritten while PURPLE patches
                if (GameFiles.FindExe(install) is not { } exe) continue;
                games.Add(new Game($"purple:{code}", name, Store.Other, install, exe, string.IsNullOrEmpty(version) ? null : version));
            }
            catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("PURPLE", $"purple:{code}", e); }
        return games;
    }

    /// <summary>PurpleLauncher.exe, from the command of PURPLE's nc-purple: protocol; null when PURPLE isn't installed.</summary>
    [SupportedOSPlatform("windows")]
    public static string? LauncherExe() =>
        LauncherIn(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\nc-purple\shell\open\command", null, null) as string);

    /// <summary>The program a handler command starts (quoted, or unquoted up to ".exe"), if it is an existing PurpleLauncher.exe.</summary>
    internal static string? LauncherIn(string? command)
    {
        if (command == null) return null;
        var m = Regex.Match(command, @"^\s*(?:""(?<exe>[^""]+)""|(?<exe>.+?\.exe)(?=\s|$))", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var exe = m.Groups["exe"].Value;
        return Path.GetFileName(exe).Equals("PurpleLauncher.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe) ? exe : null;
    }

    [SupportedOSPlatform("windows")]
    static IEnumerable<(string, string, string)> Entries()
    {
        var entries = new List<(string, string, string)>();
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        if (root == null) return entries;
        foreach (var sub in root.GetSubKeyNames())
            try
            {
                using var key = root.OpenSubKey(sub);
                if (key?.GetValue("UninstallString") is not string uninst
                    || Regex.Match(uninst, @"PurpleLauncher\.exe""?\s+--uninstall\s+""?([\w.-]+)", RegexOptions.IgnoreCase) is not { Success: true } m) continue;
                if (key.GetValue("InstallLocation") is not string install || install.Length == 0) continue;
                var code = m.Groups[1].Value;
                entries.Add((code, key.GetValue("DisplayName") as string ?? code, install));
            }
            catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("PURPLE", sub, e); }
        return entries;
    }
}
