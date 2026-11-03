using System.Text.Json;

namespace SCSKiller.Core.Games;

/// <summary>The Windows games Heroic Games Launcher installed with one of its runners: Epic's through Legendary
/// (legendaryConfig/legendary/installed.json), GOG's (gog_store/installed.json, the exe from the game's goggame-&lt;id&gt;.info
/// as <see cref="GogSource"/> reads it), Amazon's through Nile (nile_config/nile/installed.json, the exe from the game's
/// fuel.json), and the ones the user added to Heroic (sideload_apps/library.json, its exe resolved as one added by hand:
/// <see cref="ManualSource.Resolve"/>). Heroic's folder is its own (~/.config/heroic), else its Flatpak's. Ids are the Windows stores' (epic:, gog:),
/// amazon: and heroic:. Each game runs in the Wine prefix and on the Wine or Proton build its GamesConfig/&lt;app&gt;.json names, else
/// Heroic's defaults (<see cref="PrefixOf"/>). The exe must be in its install folder, and both outside the system's, Steam's
/// and Wine's folders (<see cref="SteamShortcutSource.StartProblem"/>). Only read.</summary>
public sealed class HeroicSource(HeroicSource.Runner runner, IReadOnlyList<string>? folders = null, string? steamRoot = null) : IGameSource, ILeavesOut
{
    public enum Runner { Legendary, Gog, Nile, Sideload }

    public static readonly Runner[] Runners = [Runner.Legendary, Runner.Gog, Runner.Nile, Runner.Sideload];

    public Store Store => runner == Runner.Legendary ? Store.Epic : Store.Other;

    public Func<string, bool> LeftOut { get; set; } = _ => false;

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
        : gameId.StartsWith("amazon:", StringComparison.Ordinal) ? Runner.Nile : gameId.StartsWith("heroic:", StringComparison.Ordinal) ? Runner.Sideload : null;

    /// <summary>A game the user added to Heroic: its exe and its origin are the user's.</summary>
    public static bool Sideload(string gameId) => RunnerOf(gameId) == Runner.Sideload;

    /// <summary>The runner's name in heroic://launch.</summary>
    public static string RunnerName(Runner r) => r switch { Runner.Legendary => "legendary", Runner.Gog => "gog", Runner.Sideload => "sideload", _ => "nile" };

    static string IdPrefix(Runner r) => r switch { Runner.Legendary => "epic:", Runner.Gog => "gog:", Runner.Sideload => "heroic:", _ => "amazon:" };

    public IReadOnlyList<Game> Discover()
    {
        var games = new List<Game>();
        var root = steamRoot ?? SteamSource.Root();
        foreach (var folder in folders ?? Folders())
            foreach (var (app, title, listedInstall, listedExe, version) in Installed(folder, runner, (_, install) => LeftOut(install)))
                try
                {
                    var (install, exe) = (listedInstall, listedExe);
                    if (runner == Runner.Sideload && exe != null)
                    {
                        var r = ManualSource.Resolve(exe);
                        (install, exe) = ManualSource.RootProblem(r.InstallDir, r.Exe, []) == null ? (r.InstallDir, r.Exe) : (install, null);
                    }
                    if (Directory.Exists(install) && exe != null && File.Exists(exe) && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        && GameFiles.Inside(install, exe) && SteamShortcutSource.StartProblem(root, install) == null && SteamShortcutSource.StartProblem(root, exe) == null)
                        games.Add(new Game(IdPrefix(runner) + app, title, Store, Path.TrimEndingDirectorySeparator(install), exe, version));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
                catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("Heroic", IdPrefix(runner) + app, e); }
        return games.DistinctBy(g => g.Id).ToList();
    }

    /// <summary>The runner's Windows installs in Heroic's folder <paramref name="folder"/>, with <paramref name="any"/> those of
    /// any platform and a game added to Heroic marked not installed too, DLC and app names that aren't one file name
    /// (<see cref="SafeApp"/>) left out, and those <paramref name="skip"/> takes by app and install folder left unread; none
    /// when its files can't be read, and then <paramref name="unread"/> is called.</summary>
    static List<(string App, string Title, string Install, string? Exe, string? Version)> Installed(string folder, Runner runner, Func<string, string, bool> skip,
        bool any = false, Action? unread = null)
    {
        var found = new List<(string, string, string, string?, string?)>();
        try
        {
            var file = runner switch
            {
                Runner.Legendary => Path.Combine(folder, "legendaryConfig", "legendary", "installed.json"),
                Runner.Gog => Path.Combine(folder, "gog_store", "installed.json"),
                Runner.Nile => Path.Combine(folder, "nile_config", "nile", "installed.json"),
                _ => Path.Combine(folder, "sideload_apps", "library.json"),
            };
            // one being written, locked or cut off reads as none
            var there = File.Exists(file);
            var doc = Json(file);
            if (there && doc == null) unread?.Invoke();
            switch (runner)
            {
                case Runner.Legendary when doc is { ValueKind: JsonValueKind.Object } all:
                    foreach (var p in all.EnumerateObject())
                        try
                        {
                            if (p.Value.ValueKind == JsonValueKind.Object && !True(p.Value, "is_dlc") && (any || Str(p.Value, "platform") == "Windows") && Str(p.Value, "install_path") is { } install && !skip(p.Name, install))
                                found.Add((p.Name, Str(p.Value, "title") ?? p.Name, install, GameFiles.FindExe(install, Str(p.Value, "executable") is { Length: > 0 } e ? e : null),
                                    Str(p.Value, "version")));
                        }
                        catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("Heroic", "epic:" + p.Name, e); }
                    break;
                case Runner.Gog when doc is { ValueKind: JsonValueKind.Object } root
                                     && root.TryGetProperty("installed", out var list) && list.ValueKind == JsonValueKind.Array:
                    foreach (var g in list.EnumerateArray())
                        try
                        {
                            if (g.ValueKind != JsonValueKind.Object || True(g, "is_dlc") || !any && !"windows".Equals(Str(g, "platform"), StringComparison.OrdinalIgnoreCase)
                                || Str(g, "appName") is not { } app || Str(g, "install_path") is not { } install || skip(app, install)) continue;
                            var info = GameFiles.Find(install, $"goggame-{app}.info");
                            var (title, exe, version) = (GameFiles.FolderName(install), (string?)null, Str(g, "version"));
                            if (File.Exists(info) && SteamSource.Small(info) is { } text)
                                try { (title, exe, version) = GogSource.ParseInfo(text, install, title); }
                                catch (Exception x) when (x is JsonException or InvalidOperationException) { }   // a task that isn't an object
                            found.Add((app, title, install, exe ?? GameFiles.FindExe(install, Str(g, "executable") is { Length: > 0 } e ? e : null), version));
                        }
                        catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("Heroic", file, e); }
                    break;
                case Runner.Nile when doc is { ValueKind: JsonValueKind.Array } list:
                    var titles = new Dictionary<string, string>();
                    if (Json(Path.Combine(folder, "nile_config", "nile", "library.json")) is { ValueKind: JsonValueKind.Array } library)
                        foreach (var l in library.EnumerateArray())
                            if (l.ValueKind == JsonValueKind.Object && l.TryGetProperty("product", out var p) && p.ValueKind == JsonValueKind.Object
                                && Str(p, "id") is { } id && Str(p, "title") is { } t)
                                titles.TryAdd(id, t);
                    foreach (var g in list.EnumerateArray())
                        try
                        {
                            if (g.ValueKind == JsonValueKind.Object && Str(g, "id") is { } app && Str(g, "path") is { } install && !skip(app, install))
                                found.Add((app, titles.GetValueOrDefault(app) ?? GameFiles.FolderName(install), install, GameFiles.FindExe(install, FuelCommand(install)), Str(g, "version")));
                        }
                        catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("Heroic", file, e); }
                    break;
                case Runner.Sideload when doc is { ValueKind: JsonValueKind.Object } root
                                          && root.TryGetProperty("games", out var list) && list.ValueKind == JsonValueKind.Array:
                    foreach (var g in list.EnumerateArray())
                        try
                        {
                            if (g.ValueKind == JsonValueKind.Object && (any || !(g.TryGetProperty("is_installed", out var on) && on.ValueKind == JsonValueKind.False))
                                && Str(g, "app_name") is { } app && g.TryGetProperty("install", out var i) && i.ValueKind == JsonValueKind.Object
                                && Str(i, "platform") is { } platform && (any || platform.Equals("windows", StringComparison.OrdinalIgnoreCase) || platform.Equals("win32", StringComparison.OrdinalIgnoreCase))
                                && Str(i, "executable") is { Length: > 0 } exe && Path.IsPathFullyQualified(exe)
                                && (Str(g, "folder_name") is { Length: > 0 } f && Path.IsPathFullyQualified(f) ? f : Path.GetDirectoryName(exe)!) is var install && !skip(app, install))
                                found.Add((app, Str(g, "title") is { Length: > 0 } t ? t : GameFiles.FolderName(install), install, exe, null));
                        }
                        catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("Heroic", file, e); }
                    break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { unread?.Invoke(); }
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
                if (!Installed(folder, runner, (other, _) => other != app).Any(i => i.App == app)) continue;
                var cache = folder.StartsWith(Path.Combine(home, ".var", "app", Flatpak) + "/", StringComparison.Ordinal) ? Path.Combine(home, ".var", "app", Flatpak, "cache") : null;
                var setting = Settings(folder, app);
                var prefix = WinePrefixOf(setting, home);
                if (prefix == null || !Path.IsPathFullyQualified(prefix))
                {
                    why = "Heroic names no Wine prefix for it: start it once from Heroic";
                    return null;
                }
                if (setting("wineVersion") is not { ValueKind: JsonValueKind.Object } wine || Str(wine, "bin") is not { Length: > 0 } bin || Str(wine, "type") is not { } type)
                {
                    why = "Heroic names no Wine or Proton for it: pick one in its settings";
                    return null;
                }
                bin = bin.Replace("'", "");   // as Heroic runs it
                var env = Env(setting);
                var umu = Umu(folder, setting, home);
                if (type is "wine" or "proton") return new LauncherPrefix("Heroic", Path.TrimEndingDirectorySeparator(prefix), type == "proton" ? Path.GetDirectoryName(bin)! : bin, type == "proton", cache, env, umu,
                    setting("launcherArgs") is { ValueKind: JsonValueKind.String } a ? a.GetString() : null);
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

    /// <summary>A game's setting as Heroic reads it: its GamesConfig/&lt;app&gt;.json's, else config.json's defaultSettings'.</summary>
    static Func<string, JsonElement?> Settings(string folder, string app)
    {
        var game = Json(Path.Combine(folder, "GamesConfig", app + ".json")) is { ValueKind: JsonValueKind.Object } c && c.TryGetProperty(app, out var s)
            && s.ValueKind == JsonValueKind.Object ? s : (JsonElement?)null;
        var defaults = Json(Path.Combine(folder, "config.json")) is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("defaultSettings", out var ds)
            && ds.ValueKind == JsonValueKind.Object ? ds : (JsonElement?)null;
        return name => game is { } gs && gs.TryGetProperty(name, out var v) ? v : defaults is { } dd && dd.TryGetProperty(name, out var dv) ? dv : null;
    }

    /// <summary>The winePrefix setting, a leading ~ the home folder, as Heroic sets it for the game; null when there's none.</summary>
    static string? WinePrefixOf(Func<string, JsonElement?> setting, string home) =>
        setting("winePrefix") is { ValueKind: JsonValueKind.String } p && p.GetString() is { } prefix ? prefix.StartsWith('~') ? home + prefix[1..] : prefix : null;

    /// <summary>The variable Heroic starts a game of its own with that names it alone, from its files now (launcher.ts): a store
    /// game's HEROIC_APP_NAME=&lt;app&gt;. A game added to Heroic gets none of Heroic's own, so it's what its runner sets from
    /// the game's settings: under umu STEAM_COMPAT_INSTALL_PATH (umu's own: the folder of the exe library.json names, as
    /// Python's pathlib makes it absolute, unless the game's variables set it), on a Proton without umu
    /// STEAM_COMPAT_DATA_PATH, on a Wine WINEPREFIX, both the winePrefix setting. Null with the reason when it can't be told.</summary>
    public static (string Name, string Value)? LaunchVariable(Game g, out string? why, string? home = null)
    {
        why = null;
        if (RunnerOf(g.Id) is not { } runner) return null;
        var app = g.Id[IdPrefix(runner).Length..];
        if (runner != Runner.Sideload) return ("HEROIC_APP_NAME", app);
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (PrefixOf(g, out why, home) is not { } p) return null;
        try
        {
            // the first folder that lists it, as PrefixOf reads
            foreach (var folder in Folders(home))
            {
                if (Installed(folder, runner, (other, _) => other != app).FirstOrDefault(i => i.App == app) is not { App: not null } listed) continue;
                if (p.Proton && p.Umu)
                    return p.Env?.LastOrDefault(e => e.Name == "STEAM_COMPAT_INSTALL_PATH") is { Name: not null, Value.Length: > 0 } set ? set
                        : listed.Exe is { } exe ? ("STEAM_COMPAT_INSTALL_PATH", PathlibParent(exe)) : null;
                return WinePrefixOf(Settings(folder, app), home) is { } prefix ? (p.Proton ? "STEAM_COMPAT_DATA_PATH" : "WINEPREFIX", prefix) : null;
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

    /// <summary>The title of another game Heroic would start with <paramref name="v"/> too (<see cref="Exports"/>): of any
    /// runner and platform in any of its folders, and any game when <paramref name="g"/> isn't Heroic's. Null when there's
    /// none, <paramref name="unread"/> when Heroic's files couldn't be read.</summary>
    public static string? Twin(Game g, (string Name, string Value) v, out bool unread, string? home = null)
    {
        var bad = false;
        unread = false;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var runner = RunnerOf(g.Id);
        var app = runner is { } r0 ? g.Id[IdPrefix(r0).Length..] : null;
        try
        {
            var folders = Folders(home);
            var own = runner is { } mine ? folders.FirstOrDefault(f => Installed(f, mine, (other, _) => other != app).Any(i => i.App == app)) : null;
            foreach (var folder in folders)
                foreach (var r in Runners)
                    foreach (var e in Installed(folder, r, (_, _) => false, any: true, unread: () => bad = true))
                        if (!(folder == own && r == runner && e.App == app) && Exports(folder, r, e.App, e.Install, e.Exe, home).Contains(v)) return e.Title;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { bad = true; }
        unread = bad;
        return null;
    }

    /// <summary>What Heroic and its runner start a game with that can name it (launcher.ts): a store game's HEROIC_APP_NAME, the
    /// game's own variables, and its prefix's on a Wine or a Proton (<see cref="LauncherPrefix.Exports"/>), a store game's
    /// install folder as STEAM_COMPAT_INSTALL_PATH.</summary>
    static List<(string Name, string Value)> Exports(string folder, Runner runner, string app, string install, string? exe, string home)
    {
        var setting = Settings(folder, app);
        var env = Env(setting);
        List<(string Name, string Value)> list = runner != Runner.Sideload ? [("HEROIC_APP_NAME", app)] : [];
        if (WinePrefixOf(setting, home) is { } prefix && setting("wineVersion") is { ValueKind: JsonValueKind.Object } wine && Str(wine, "type") is "wine" or "proton")
            list.AddRange(LauncherPrefix.Exports(prefix, Str(wine, "type") == "proton", Umu(folder, setting, home), exe, runner != Runner.Sideload ? install : null, env));
        else list.AddRange(env);
        return list;
    }

    /// <summary>Heroic's enviromentOptions of a game, each value with its surrounding double quotes taken off.</summary>
    static List<(string Name, string Value)> Env(Func<string, JsonElement?> setting) =>
        setting("enviromentOptions") is { ValueKind: JsonValueKind.Array } options
            ? options.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.Object && Str(o, "key") is { Length: > 0 })
                .Select(o => (Str(o, "key")!, Str(o, "value") is { } v && v.Length > 0 && v[0] == '"' && v[^1] == '"' ? v.Trim('"') : Str(o, "value") ?? "")).ToList()
            : [];

    /// <summary>Heroic runs the game through umu, as its isUmuSupported: unless turned off for the game or not downloaded (a
    /// Flatpak's PATH has no umu-run).</summary>
    static bool Umu(string folder, Func<string, JsonElement?> setting, string home) =>
        setting("disableUMU") is not { ValueKind: JsonValueKind.True }
        && (!folder.StartsWith(Path.Combine(home, ".var", "app", Flatpak) + "/", StringComparison.Ordinal) || File.Exists(Path.Combine(folder, "tools", "runtimes", "umu", "umu_run.py")));

    /// <summary>The folder of a full path as Python's pathlib gives it (Path(p).absolute().parent): empty and "." parts out,
    /// a leading "//" kept as it is.</summary>
    internal static string PathlibParent(string path)
    {
        var root = path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("///", StringComparison.Ordinal) ? "//" : "/";
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToList();
        return root + string.Join('/', parts.Take(Math.Max(0, parts.Count - 1)));
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
