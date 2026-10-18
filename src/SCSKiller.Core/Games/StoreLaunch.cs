using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

/// <summary>Starts a game the way its store's own Play and desktop shortcut do, never by its exe: Steam, Epic and Ubisoft
/// Connect by their URI, Xbox by the package's app id, GOG through GOG Galaxy's run command, PURPLE through
/// PurpleLauncher.exe --game-id &lt;code&gt;. The EA app's own shortcuts run the game exe and discovery has no EA offer id; Battle.net and Riot Client games count as anti-cheat: none of these has a launch. A game the
/// user added has no store: its exe runs from its own folder, with no arguments.</summary>
public static class StoreLaunch
{
    /// <summary>The game's store has a launch; no file read beyond whether GOG Galaxy or PURPLE is installed.</summary>
    public static bool Supported(Game g) => g.Store == Store.Manual || Prefix(g) switch
    {
        "steam" or "epic" or "xbox" or "ubisoft" => true,
        "gog" => GalaxyExe() != null,
        "purple" => PurpleSource.LauncherExe() != null,
        _ => false,
    };

    /// <summary>A game the user added that contains anti-cheat: its exe may be the one its launcher starts, so Play never starts it.</summary>
    public static bool OwnLauncherOnly(GameState s) => s.Game.Store == Store.Manual && s.AntiCheat != AntiCheat.None;

    /// <summary>What the store's Play runs for the game; null when it has no launch or the store's data for it is gone.</summary>
    public static ProcessStartInfo? Command(Game g, string? epicManifests = null, string? galaxyExe = null, string? purpleLauncher = null, string? epicLauncher = null)
    {
        var id = g.Id[(g.Id.IndexOf(':') + 1)..];
        var uri = Prefix(g) switch
        {
            "steam" when SteamSource.AppId(g.Id) is { } app => $"steam://rungameid/{app}",
            "epic" => EpicUri(id, epicManifests ?? EpicSource.DefaultManifestsDir, epicLauncher ?? (epicManifests == null ? EpicSource.DefaultLauncherDir : null)),
            "xbox" => XboxSource.AppUserModelId(g) is { } app ? @"shell:AppsFolder\" + app : null,
            "ubisoft" when uint.TryParse(id, out _) => $"uplay://launch/{id}/0",
            _ => null,
        };
        if (uri != null) return new ProcessStartInfo(uri) { UseShellExecute = true };
        if (g.Store == Store.Manual) return new ProcessStartInfo(g.ExePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(g.ExePath) };
        // Galaxy's own desktop shortcut; a trailing backslash would escape the closing quote
        if (Prefix(g) == "gog" && ulong.TryParse(id, out _) && (galaxyExe ?? GalaxyExe()) is { } galaxy)
            return new ProcessStartInfo(galaxy, $"/command=runGame /gameId={id} /path=\"{g.InstallDir.TrimEnd('\\')}\"");
        // PURPLE's own desktop shortcut
        if (Prefix(g) == "purple" && Regex.IsMatch(id, @"^[\w.-]+$") && (purpleLauncher ?? PurpleSource.LauncherExe()) is { } purple)
            return new ProcessStartInfo(purple, $"--game-id {id}") { WorkingDirectory = Path.GetDirectoryName(purple) };
        return null;
    }

    static string Prefix(Game g) => g.Id[..Math.Max(0, g.Id.IndexOf(':'))];

    /// <summary>The launcher's triple from its install list or the game's classic manifest: &lt;CatalogNamespace&gt;:&lt;CatalogItemId&gt;:&lt;AppName&gt;.</summary>
    static string? EpicUri(string appName, string manifests, string? launcher)
    {
        static string Uri3(string ns, string catalog, string app) =>
            $"com.epicgames.launcher://apps/{Uri.EscapeDataString(ns)}%3A{Uri.EscapeDataString(catalog)}%3A{Uri.EscapeDataString(app)}?action=launch&silent=true";
        try
        {
            if (EpicSource.Installs(launcher).FirstOrDefault(i => i.App == appName && i.Namespace != "" && i.ItemId != "") is { } install)
                return Uri3(install.Namespace, install.ItemId, appName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (!Directory.Exists(manifests)) return null;
        foreach (var item in Directory.EnumerateFiles(manifests, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                var r = doc.RootElement;
                string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (S("AppName") != appName || S("CatalogNamespace") is not { Length: > 0 } ns || S("CatalogItemId") is not { Length: > 0 } catalog) continue;
                return Uri3(ns, catalog, appName);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    static string? GalaxyExe()
    {
        const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\GOG.com\GalaxyClient";
        if (Registry.GetValue(key + @"\paths", "client", null) is not string dir) return null;
        var exe = Path.Combine(dir, Registry.GetValue(key, "clientExecutable", null) as string ?? "GalaxyClient.exe");
        return File.Exists(exe) ? exe : null;
    }
}
