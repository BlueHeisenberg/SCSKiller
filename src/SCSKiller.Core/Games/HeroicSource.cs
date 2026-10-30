using System.Text.Json;

namespace SCSKiller.Core.Games;

/// <summary>The Windows games Heroic Games Launcher installed with one of its runners: Epic's through Legendary
/// (legendaryConfig/legendary/installed.json), GOG's (gog_store/installed.json, the exe from the game's goggame-&lt;id&gt;.info
/// as <see cref="GogSource"/> reads it), Amazon's through Nile (nile_config/nile/installed.json, the exe from the game's
/// fuel.json). Heroic's folder is its own (~/.config/heroic), else its Flatpak's. Ids are the Windows stores' (epic:, gog:)
/// and amazon:. Each game runs in the Wine prefix and on the Wine or Proton build its GamesConfig/&lt;app&gt;.json names, else
/// Heroic's defaults (<see cref="PrefixOf"/>). The exe must be in its install folder, and both outside the system's, Steam's
/// and Wine's folders (<see cref="SteamShortcutSource.StartProblem"/>). Only read.</summary>
public sealed class HeroicSource(HeroicSource.Runner runner, IReadOnlyList<string>? folders = null, string? steamRoot = null) : IGameSource
{
    public enum Runner { Legendary, Gog, Nile }

    public static readonly Runner[] Runners = [Runner.Legendary, Runner.Gog, Runner.Nile];

    public Store Store => runner == Runner.Legendary ? Store.Epic : Store.Other;

    public const string Flatpak = "com.heroicgameslauncher.hgl";

    /// <summary>Heroic's folders, the native one's first: $XDG_CONFIG_HOME/heroic (~/.config/heroic), the Flatpak's.</summary>
    public static IReadOnlyList<string> Folders(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x && Path.IsPathRooted(x) ? x : Path.Combine(home, ".config");
        return [Path.Combine(config, "heroic"), Path.Combine(home, ".var", "app", Flatpak, "config", "heroic")];
    }

    /// <summary>The runner of a game id of Heroic's; null for any other id.</summary>
    public static Runner? RunnerOf(string gameId) =>
        gameId.StartsWith("epic:", StringComparison.Ordinal) ? Runner.Legendary : gameId.StartsWith("gog:", StringComparison.Ordinal) ? Runner.Gog
        : gameId.StartsWith("amazon:", StringComparison.Ordinal) ? Runner.Nile : null;

    /// <summary>The runner's name in heroic://launch.</summary>
    public static string RunnerName(Runner r) => r switch { Runner.Legendary => "legendary", Runner.Gog => "gog", _ => "nile" };

    static string IdPrefix(Runner r) => r switch { Runner.Legendary => "epic:", Runner.Gog => "gog:", _ => "amazon:" };

    public IReadOnlyList<Game> Discover()
    {
        var games = new List<Game>();
        var root = steamRoot ?? SteamSource.Root();
        foreach (var folder in folders ?? Folders())
            foreach (var (app, title, install, exe, version) in Installed(folder, runner))
                try
                {
                    if (Directory.Exists(install) && exe != null && File.Exists(exe) && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        && GameFiles.Inside(install, exe) && SteamShortcutSource.StartProblem(root, install) == null && SteamShortcutSource.StartProblem(root, exe) == null)
                        games.Add(new Game(IdPrefix(runner) + app, title, Store, Path.TrimEndingDirectorySeparator(install), exe, version));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
                catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("Heroic", IdPrefix(runner) + app, e); }
        return games.DistinctBy(g => g.Id).ToList();
    }

    /// <summary>The runner's Windows installs in Heroic's folder <paramref name="folder"/>, DLC and app names that aren't one
    /// file name (<see cref="SafeApp"/>) left out; none when its files can't be read.</summary>
    static List<(string App, string Title, string Install, string? Exe, string? Version)> Installed(string folder, Runner runner)
    {
        var found = new List<(string, string, string, string?, string?)>();
        try
        {
            switch (runner)
            {
                case Runner.Legendary when Json(Path.Combine(folder, "legendaryConfig", "legendary", "installed.json")) is { ValueKind: JsonValueKind.Object } all:
                    foreach (var p in all.EnumerateObject())
                        try
                        {
                            if (p.Value.ValueKind == JsonValueKind.Object && !True(p.Value, "is_dlc") && Str(p.Value, "platform") == "Windows" && Str(p.Value, "install_path") is { } install)
                                found.Add((p.Name, Str(p.Value, "title") ?? p.Name, install, GameFiles.FindExe(install, Str(p.Value, "executable") is { Length: > 0 } e ? e : null),
                                    Str(p.Value, "version")));
                        }
                        catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("Heroic", "epic:" + p.Name, e); }
                    break;
                case Runner.Gog when Json(Path.Combine(folder, "gog_store", "installed.json")) is { ValueKind: JsonValueKind.Object } root
                                     && root.TryGetProperty("installed", out var list) && list.ValueKind == JsonValueKind.Array:
                    foreach (var g in list.EnumerateArray())
                        try
                        {
                            if (g.ValueKind != JsonValueKind.Object || True(g, "is_dlc") || !"windows".Equals(Str(g, "platform"), StringComparison.OrdinalIgnoreCase)
                                || Str(g, "appName") is not { } app || Str(g, "install_path") is not { } install) continue;
                            var info = GameFiles.Find(install, $"goggame-{app}.info");
                            var (title, exe, version) = (GameFiles.FolderName(install), (string?)null, Str(g, "version"));
                            if (File.Exists(info) && SteamSource.Small(info) is { } text)
                                try { (title, exe, version) = GogSource.ParseInfo(text, install, title); }
                                catch (Exception x) when (x is JsonException or InvalidOperationException) { }   // a task that isn't an object
                            found.Add((app, title, install, exe ?? GameFiles.FindExe(install, Str(g, "executable") is { Length: > 0 } e ? e : null), version));
                        }
                        catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("Heroic", Path.Combine(folder, "gog_store", "installed.json"), e); }
                    break;
                case Runner.Nile when Json(Path.Combine(folder, "nile_config", "nile", "installed.json")) is { ValueKind: JsonValueKind.Array } list:
                    var titles = new Dictionary<string, string>();
                    if (Json(Path.Combine(folder, "nile_config", "nile", "library.json")) is { ValueKind: JsonValueKind.Array } library)
                        foreach (var l in library.EnumerateArray())
                            if (l.ValueKind == JsonValueKind.Object && l.TryGetProperty("product", out var p) && p.ValueKind == JsonValueKind.Object
                                && Str(p, "id") is { } id && Str(p, "title") is { } t)
                                titles.TryAdd(id, t);
                    foreach (var g in list.EnumerateArray())
                        try
                        {
                            if (g.ValueKind == JsonValueKind.Object && Str(g, "id") is { } app && Str(g, "path") is { } install)
                                found.Add((app, titles.GetValueOrDefault(app) ?? GameFiles.FolderName(install), install, GameFiles.FindExe(install, FuelCommand(install)), Str(g, "version")));
                        }
                        catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("Heroic", Path.Combine(folder, "nile_config", "nile", "installed.json"), e); }
                    break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return found.Where(f => SafeApp(f.Item1)).ToList();
    }

    // an app name goes into GamesConfig/<app>.json and goggame-<app>.info
    static bool SafeApp(string app) => app.Length is > 0 and <= 128 && app != "." && app != ".." && app.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>An Amazon game's exe as its fuel.json names it (Main.Command); null when it doesn't.</summary>
    static string? FuelCommand(string install) =>
        Json(GameFiles.Find(install, "fuel.json")) is { ValueKind: JsonValueKind.Object } f && f.TryGetProperty("Main", out var main) && main.ValueKind == JsonValueKind.Object
            ? Str(main, "Command") : null;

    /// <summary>Where Heroic runs a game of its own (<see cref="RunnerOf"/>), read from its folders now: in the first folder that
    /// lists the game installed, its GamesConfig/&lt;app&gt;.json's winePrefix and wineVersion over config.json's
    /// defaultSettings. Null with the reason when it can't be told or the build is neither a Wine nor a Proton; null with no
    /// reason for a game of no runner of Heroic's.</summary>
    public static LauncherPrefix? PrefixOf(Game g, out string? why, string? home = null)
    {
        why = null;
        if (RunnerOf(g.Id) is not { } runner) return null;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var app = g.Id[IdPrefix(runner).Length..];
        try
        {
            foreach (var folder in Folders(home))
            {
                if (!Installed(folder, runner).Any(i => i.App == app)) continue;
                var cache = folder.StartsWith(Path.Combine(home, ".var", "app", Flatpak) + "/", StringComparison.Ordinal) ? Path.Combine(home, ".var", "app", Flatpak, "cache") : null;
                var game = Json(Path.Combine(folder, "GamesConfig", app + ".json")) is { ValueKind: JsonValueKind.Object } c && c.TryGetProperty(app, out var s)
                    && s.ValueKind == JsonValueKind.Object ? s : (JsonElement?)null;
                var defaults = Json(Path.Combine(folder, "config.json")) is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("defaultSettings", out var ds)
                    && ds.ValueKind == JsonValueKind.Object ? ds : (JsonElement?)null;
                JsonElement? Setting(string name) =>
                    game is { } gs && gs.TryGetProperty(name, out var v) ? v : defaults is { } dd && dd.TryGetProperty(name, out var dv) ? dv : null;
                var prefix = Setting("winePrefix") is { ValueKind: JsonValueKind.String } p ? p.GetString()! : null;
                if (prefix?.StartsWith('~') == true) prefix = home + prefix[1..];
                if (prefix == null || !Path.IsPathFullyQualified(prefix))
                {
                    why = "Heroic names no Wine prefix for it: start it once from Heroic";
                    return null;
                }
                if (Setting("wineVersion") is not { ValueKind: JsonValueKind.Object } wine || Str(wine, "bin") is not { Length: > 0 } bin || Str(wine, "type") is not { } type)
                {
                    why = "Heroic names no Wine or Proton for it: pick one in its settings";
                    return null;
                }
                bin = bin.Replace("'", "");   // as Heroic runs it
                if (type is "wine" or "proton") return new LauncherPrefix("Heroic", Path.TrimEndingDirectorySeparator(prefix), type == "proton" ? Path.GetDirectoryName(bin)! : bin, type == "proton", cache);
                why = $"Heroic runs it on {Str(wine, "name") ?? type}, which is neither a Wine nor a Proton";
                return null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            why = $"Heroic's files couldn't be read: {e.Message}";
            return null;
        }
        why = "Heroic doesn't list it as installed anymore";
        return null;
    }

    /// <summary>A JSON file's root, cloned; null when it isn't there, is over 16 MB or isn't JSON (a file being written).</summary>
    internal static JsonElement? Json(string path)
    {
        if (!File.Exists(path) || SteamSource.Small(path) is not { } text) return null;
        try
        {
            using var d = JsonDocument.Parse(text);
            return d.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    internal static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool True(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
