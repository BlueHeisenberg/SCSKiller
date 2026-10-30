using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

/// <summary>Installed EA app games, including EA Play / Game Pass titles the EA app downloads. The EA app's own install
/// list (%ProgramData%\EA Desktop\&lt;hash&gt;\IS) is encrypted, so this reads what the EA installer leaves in every game
/// folder instead: __Installer\installerdata.xml, with the content id, title, version and the launcher as
/// "[&lt;registry value&gt;]relative\game.exe" (Jedi Survivor: [HKEY_LOCAL_MACHINE\SOFTWARE\Respawn\Jedi Survivor\Install Dir]).
/// Folders come from the EA app's install location (user.downloadinplacedir in %LOCALAPPDATA%\Electronic Arts\EA Desktop\
/// user_*.ini, default C:\Program Files\EA Games) and the uninstall entries' InstallLocation (installs made before the
/// location changed, EA games bought on Steam). Installed = the xml under its own name (while downloading every file is
/// still "*_DiP_Staged") and the launcher's registry value, which the installer's touchup step writes after the download,
/// naming this folder. The exe goes through FindExe like Epic's, so an Unreal game gets its Binaries\Win64 exe.</summary>
public sealed class EaSource(IEnumerable<string>? installDirs = null, Func<string, string?>? registry = null) : IGameSource
{
    public Store Store => Store.EA;

    public IReadOnlyList<Game> Discover()
    {
        var games = new List<Game>();
        foreach (var dir in (installDirs ?? (OperatingSystem.IsWindows() ? InstallDirs() : [])).Select(GameFiles.DirKey).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var xml = Path.Combine(dir, "__Installer", "installerdata.xml");
            if (!File.Exists(xml)) continue;
            try { if (Parse(dir, XDocument.Load(xml)) is { } g) games.Add(g); }
            catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException) { }   // destaged under us
            catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("EA app", dir, e); }
        }
        return games;
    }

    Game? Parse(string dir, XDocument doc)
    {
        var x = doc.Root!;   // Descendants: older Origin manifests (<game>) nest these differently than DiPManifest 4.0
        var launcher = x.Descendants("launcher").FirstOrDefault(l => (string?)l.Element("trial") != "1");
        var m = Regex.Match(((string?)launcher?.Element("filePath"))?.Trim() ?? "", @"^(?:\[(?<reg>[^\]]+)\])?(?<rel>.*)$");
        if (m.Groups["reg"].Success && (Read(m.Groups["reg"].Value) is not { Length: > 0 } at || !GameFiles.DirKey(at).Equals(dir, StringComparison.OrdinalIgnoreCase))) return null;
        var rel = m.Groups["rel"].Value.TrimStart('\\', '/');
        if (GameFiles.FindExe(dir, rel.Length > 0 ? rel : null) is not { } exe) return null;
        static string? En(IEnumerable<XElement>? e) => e?.OrderBy(t => (string?)t.Attribute("locale") == "en_US" ? 0 : 1).Select(t => t.Value).FirstOrDefault();
        var name = (En(x.Descendants("gameTitle")) ?? En(launcher?.Elements("name")))?.Replace("™", "").Replace("®", "").Trim();
        var id = x.Descendants("contentID").Select(c => c.Value.Trim()).FirstOrDefault(c => c.Length > 0) ?? Path.GetFileName(dir);
        return new Game($"ea:{id}", string.IsNullOrEmpty(name) ? Path.GetFileName(dir) : name, Store.EA, dir, exe,
            (string?)x.Descendants("gameVersion").FirstOrDefault()?.Attribute("version"));
    }

    string? Read(string path) => registry != null ? registry(path) : OperatingSystem.IsWindows() ? ReadRegistry(path) : null;

    /// <summary>"HKEY_LOCAL_MACHINE\Key\Value Name" from the 64-bit view, else the 32-bit one (the installer may be 32-bit
    /// and land under WOW6432Node).</summary>
    [SupportedOSPlatform("windows")]
    public static string? ReadRegistry(string path)
    {
        var parts = path.Split('\\', 2);
        var i = parts.Length == 2 ? parts[1].LastIndexOf('\\') : -1;
        RegistryHive? hive = parts[0].ToUpperInvariant() switch
        {
            "HKEY_LOCAL_MACHINE" or "HKLM" => RegistryHive.LocalMachine,
            "HKEY_CURRENT_USER" or "HKCU" => RegistryHive.CurrentUser,
            _ => null,
        };
        if (hive == null || i < 0) return null;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var b = RegistryKey.OpenBaseKey(hive.Value, view);
            using var k = b.OpenSubKey(parts[1][..i]);
            if (k?.GetValue(parts[1][(i + 1)..]) is string v) return v;
        }
        return null;
    }

    /// <summary>Folders that may hold an EA install: every subfolder of the EA app's install locations, and every
    /// uninstall entry's InstallLocation (Discover keeps those with an installerdata.xml).</summary>
    [SupportedOSPlatform("windows")]
    public static IEnumerable<string> InstallDirs()
    {
        var dirs = new List<string>();
        var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Electronic Arts", "EA Desktop");
        var libraries = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EA Games") };
        try
        {
            if (Directory.Exists(settings))
                foreach (var ini in Directory.EnumerateFiles(settings, "user_*.ini"))
                    libraries.AddRange(File.ReadLines(ini).Where(l => l.StartsWith("user.downloadinplacedir=", StringComparison.OrdinalIgnoreCase))
                        .Select(l => l[(l.IndexOf('=') + 1)..].Trim()));
        }
        catch (IOException) { }   // the EA app rewriting its settings
        foreach (var lib in libraries.Where(l => l.Length > 0 && Directory.Exists(l)))
            dirs.AddRange(Directory.EnumerateDirectories(lib));
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            foreach (var sub in root?.GetSubKeyNames() ?? [])
                try
                {
                    using var key = root!.OpenSubKey(sub);
                    if (key?.GetValue("InstallLocation") is string { Length: > 0 } at && Directory.Exists(at)) dirs.Add(at);
                }
                catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("EA app", sub, e); }
        }
        return dirs;
    }
}
