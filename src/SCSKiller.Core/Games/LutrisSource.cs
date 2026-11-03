using System.Runtime.InteropServices;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

/// <summary>The Windows games Lutris installed on its Wine runner: pga.db's installed rows (runner wine), each game's
/// games/&lt;configpath&gt;.yml for its exe, prefix and Wine version (<see cref="PrefixOf"/>). Lutris's folders are its own
/// ($XDG_DATA_HOME/lutris, its configs there or in $XDG_CONFIG_HOME/lutris), else its Flatpak's. Id "lutris:&lt;pga id&gt;", a
/// game of this PC's own as one added by hand: its exe must resolve to a game folder of its own
/// (<see cref="ManualSource.Resolve"/>, <see cref="ManualSource.RootProblem"/>) outside the system's, Steam's and Wine's
/// folders (<see cref="SteamShortcutSource.StartProblem"/>). Store.Other, told apart by its id. Only read.</summary>
public sealed class LutrisSource(IReadOnlyList<(string Data, string Config, string? Cache)>? folders = null, string? steamRoot = null) : IGameSource, ILeavesOut
{
    public Store Store => Store.Other;

    public Func<string, bool> LeftOut { get; set; } = _ => false;

    public const string IdPrefix = "lutris:", Flatpak = "net.lutris.Lutris";

    /// <summary>A game left out, and why.</summary>
    public IProgress<string>? Log { get; set; }

    public static bool Is(string gameId) => gameId.StartsWith(IdPrefix, StringComparison.Ordinal) && long.TryParse(gameId.AsSpan(IdPrefix.Length), out _);

    /// <summary>Lutris's data and config folders, and the XDG_CACHE_HOME its games run with (null: the user's own): the native
    /// Lutris's first, then its Flatpak's.</summary>
    public static IReadOnlyList<(string Data, string Config, string? Cache)> Folders(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        static string Xdg(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } x && Path.IsPathRooted(x) ? x : fallback;
        var app = Path.Combine(home, ".var", "app", Flatpak);
        return [(Path.Combine(Xdg("XDG_DATA_HOME", Path.Combine(home, ".local", "share")), "lutris"), Path.Combine(Xdg("XDG_CONFIG_HOME", Path.Combine(home, ".config")), "lutris"), null),
            (Path.Combine(app, "data", "lutris"), Path.Combine(app, "config", "lutris"), Path.Combine(app, "cache"))];
    }

    public IReadOnlyList<Game> Discover()
    {
        var games = new List<Game>();
        var root = steamRoot ?? SteamSource.Root();
        foreach (var f in folders ?? Folders())
            foreach (var r in Installed(f.Data))
                if (GameOf(f, r, root, LeftOut, out var why) is { } g) games.Add(g);
                else if (why != null) Log?.Report($"Lutris: {r.Name} isn't listed: {why}");
        return games.DistinctBy(g => g.Id).ToList();
    }

    const int MaxRows = 65536;

    internal sealed record Row(long Id, string Name, string ConfigPath, string? Directory);

    /// <summary>pga.db's installed games on the Wine runner, of a db of at most 64 MB and its first <see cref="MaxRows"/>;
    /// none when it can't be read.</summary>
    static List<Row> Installed(string data)
    {
        var rows = new List<Row>();
        Query(data, "select id, name, configpath, directory from games where installed = 1 and runner = 'wine' and configpath is not null", st =>
        {
            var name = Marshal.PtrToStringUTF8(D3DSCache.sqlite3_column_text(st, 1));
            if (rows.Count < MaxRows && Marshal.PtrToStringUTF8(D3DSCache.sqlite3_column_text(st, 2)) is { Length: > 0 } config && Safe(config))
                rows.Add(new(D3DSCache.sqlite3_column_int64(st, 0), name ?? config, config, Marshal.PtrToStringUTF8(D3DSCache.sqlite3_column_text(st, 3))));
        });
        return rows;
    }

    /// <summary>A configpath or runner that is one file name.</summary>
    static bool Safe(string name) => name.IndexOfAny(['/', '\\']) < 0 && name is not ("." or "..");

    /// <summary>Runs <paramref name="sql"/> on the folder's pga.db, of at most 64 MB; false when it isn't there or can't be read.</summary>
    static bool Query(string data, string sql, Action<nint> row)
    {
        var db = Path.Combine(data, "pga.db");
        try
        {
            if (!File.Exists(db)) return true;
            if (new FileInfo(db) is var info && (info.LinkTarget != null || info.Length > 64 << 20)) return false;
            using (var regular = GameFiles.OpenRegular(db)) if (regular == null) return false;   // a FIFO or device by that name
            return D3DSCache.Query(db, sql, row);
        }
        catch (Exception e) when (e is DllNotFoundException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The name of another game Lutris would start with <paramref name="v"/> too, in any of its folders: one of any
    /// runner, installed or not, each started with GAME_NAME, with a directory GAME_DIRECTORY (game.py) and its settings'
    /// variables (<see cref="Env"/>), an installed Wine one with its prefix's too (<see cref="LauncherPrefix.Exports"/>). Any
    /// game when <paramref name="g"/> isn't Lutris's. Null when there's none, <paramref name="unread"/> when a folder's files
    /// couldn't be read.</summary>
    public static string? Twin(Game g, (string Name, string Value) v, out bool unread, IReadOnlyList<(string Data, string Config, string? Cache)>? folders = null, string? steamRoot = null)
    {
        unread = false;
        long? id = Is(g.Id) ? long.Parse(g.Id.AsSpan(IdPrefix.Length)) : null;
        var all = folders ?? Folders();
        var own = id is { } mine ? all.FirstOrDefault(f => Installed(f.Data).Any(r => r.Id == mine)).Data : null;
        string? twin = null;
        foreach (var f in all)
        {
            var self = f.Data == own;
            var others = new List<(string Name, string Config, string Runner)>();
            if (!Query(f.Data, "select id, name, directory, configpath, runner from games", st =>
                {
                    if (twin != null || self && D3DSCache.sqlite3_column_int64(st, 0) == id) return;
                    var name = Marshal.PtrToStringUTF8(D3DSCache.sqlite3_column_text(st, 1));
                    var dir = Marshal.PtrToStringUTF8(D3DSCache.sqlite3_column_text(st, 2));
                    if (v == ("GAME_NAME", name) || dir is { Length: > 0 } && v == ("GAME_DIRECTORY", dir)) twin = name ?? dir;
                    else if (others.Count < MaxRows && Marshal.PtrToStringUTF8(D3DSCache.sqlite3_column_text(st, 3)) is { Length: > 0 } config && Safe(config))
                        others.Add((name ?? config, config, Marshal.PtrToStringUTF8(D3DSCache.sqlite3_column_text(st, 4)) ?? ""));
                }))
                unread = true;
            if (twin != null) return twin;
            foreach (var o in others)
                try
                {
                    if (Config(f, o.Config) is { } yml && Env(f, Runner(f, o.Runner), yml).Contains(v)) return o.Name;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { unread = true; }
            foreach (var r in Installed(f.Data))
                if (!(self && r.Id == id))
                    try
                    {
                        if (Config(f, r.ConfigPath) is { } yml && PrefixAt(f, yml, out _, steamRoot ??= SteamSource.Root()) is { } p
                            && LauncherPrefix.Exports(yml["game.prefix"], p.Proton, p.Umu, Exe(yml, r), null, p.Env ?? []).Contains(v))
                            return r.Name;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { unread = true; }
        }
        return null;
    }

    /// <summary>The variable Lutris starts a game of its own with that names it (game.py): GAME_DIRECTORY=&lt;its pga.db
    /// directory&gt; when it has one, else GAME_NAME=&lt;its name&gt;, from the first folder that lists it now. Null with the
    /// reason when it can't be told.</summary>
    public static (string Name, string Value)? LaunchVariable(Game g, out string? why, IReadOnlyList<(string Data, string Config, string? Cache)>? folders = null)
    {
        why = null;
        if (!Is(g.Id)) return null;
        var id = long.Parse(g.Id.AsSpan(IdPrefix.Length));
        foreach (var f in folders ?? Folders())
            if (Installed(f.Data).FirstOrDefault(r => r.Id == id) is { } r)
                return r.Directory is { Length: > 0 } d ? ("GAME_DIRECTORY", d) : ("GAME_NAME", r.Name);
        why = "Lutris doesn't list it as installed anymore";
        return null;
    }

    /// <summary>The game's yml: in the data folder (Lutris 0.5.13 and later), else the config folder.</summary>
    static Dictionary<string, string>? Config((string Data, string Config, string? Cache) f, string configPath) =>
        new[] { Path.Combine(f.Data, "games", configPath + ".yml"), Path.Combine(f.Config, "games", configPath + ".yml") }.FirstOrDefault(File.Exists) is { } yml
            ? Yaml(yml) : null;

    static Game? GameOf((string Data, string Config, string? Cache) f, Row r, string steamRoot, Func<string, bool> leftOut, out string? why)
    {
        why = null;
        try
        {
            var config = Config(f, r.ConfigPath);
            if (config != null && Under(config, r) is { } under && leftOut(Path.Combine(under, config["game.exe"].Replace('\\', '/')))) return null;
            if (config is not { } yml || Exe(yml, r) is not { } exe)
            {
                why = "its Lutris config names no exe";
                return null;
            }
            if (leftOut(exe)) return null;
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                why = $"{Path.GetFileName(exe)} isn't a Windows program";
                return null;
            }
            if (SteamShortcutSource.StartProblem(steamRoot, exe) is { } bad)
            {
                why = bad;
                return null;
            }
            var e = ManualSource.Resolve(exe);
            why = SteamShortcutSource.StartProblem(steamRoot, e.InstallDir) ?? SteamShortcutSource.StartProblem(steamRoot, e.Exe) ?? ManualSource.RootProblem(e.InstallDir, e.Exe, []);
            return why == null ? new Game(IdPrefix + r.Id, r.Name, Store.Other, e.InstallDir, e.Exe) : null;
        }
        catch (ArgumentException e) { why = e.Message; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { why = e.Message; }
        catch (Exception e) when (e is not OperationCanceledException) { why = $"{e.GetType().Name}: {e.Message}"; }
        return null;
    }

    /// <summary>game.exe, a relative one under the game's folder (pga's directory), else its prefix, as Lutris runs it.</summary>
    static string? Exe(Dictionary<string, string> yml, Row r)
    {
        if (yml.GetValueOrDefault("game.exe") is not { Length: > 0 } exe) return null;
        if (Path.IsPathFullyQualified(exe)) return exe;
        return Under(yml, r) is { } under ? GameFiles.Find(under, exe) : null;
    }

    /// <summary>The folder a relative game.exe is under (<see cref="Exe"/>); null for a full path or none.</summary>
    static string? Under(Dictionary<string, string> yml, Row r) =>
        yml.GetValueOrDefault("game.exe") is { Length: > 0 } exe && !Path.IsPathFullyQualified(exe)
        && (r.Directory is { Length: > 0 } d && Path.IsPathFullyQualified(d) ? d : yml.GetValueOrDefault("game.prefix")) is { } under && Path.IsPathFullyQualified(under) ? under : null;

    /// <summary>Where Lutris runs a game of its own, read from its files now: game.prefix, on wine.version's build (else the
    /// Wine runner's default version): a Wine build's bin/wine, or a Proton build's folder, from Lutris's runners/wine and
    /// runners/proton, then Steam's compatibilitytools.d; "custom" its custom_wine_path, "system" the wine on PATH. Null with
    /// the reason when it can't be told; null with no reason for a game of no Lutris.</summary>
    public static LauncherPrefix? PrefixOf(Game g, out string? why, IReadOnlyList<(string Data, string Config, string? Cache)>? folders = null, string? steamRoot = null)
    {
        why = null;
        if (!Is(g.Id)) return null;
        var id = long.Parse(g.Id.AsSpan(IdPrefix.Length));
        try
        {
            return PrefixIn(id, out why, folders, steamRoot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            why = $"Lutris's files couldn't be read: {e.Message}";
            return null;
        }
    }

    static LauncherPrefix? PrefixIn(long id, out string? why, IReadOnlyList<(string Data, string Config, string? Cache)>? folders, string? steamRoot)
    {
        why = null;
        foreach (var f in folders ?? Folders())
        {
            if (Installed(f.Data).FirstOrDefault(r => r.Id == id) is not { } row) continue;
            if (Config(f, row.ConfigPath) is { } yml) return PrefixAt(f, yml, out why, steamRoot ?? SteamSource.Root());
            why = "its Lutris config is gone";
            return null;
        }
        why = "Lutris doesn't list it as installed anymore";
        return null;
    }

    /// <summary>Where Lutris runs the game of <paramref name="yml"/> in its folder <paramref name="f"/> (<see cref="PrefixOf"/>).</summary>
    static LauncherPrefix? PrefixAt((string Data, string Config, string? Cache) f, Dictionary<string, string> yml, out string? why, string steamRoot)
    {
        why = null;
        if (yml.GetValueOrDefault("game.prefix") is not { Length: > 0 } prefix || !Path.IsPathFullyQualified(prefix))
        {
            why = "Lutris names no Wine prefix for it";
            return null;
        }
        var runner = Runner(f, "wine");
        var version = yml.GetValueOrDefault("wine.version") ?? runner.GetValueOrDefault("wine.version");
        if (version is not { Length: > 0 })
        {
            why = "Lutris names no Wine version for it: pick one in its Runner options";
            return null;
        }
        if (Build(f.Data, version, yml.GetValueOrDefault("wine.custom_wine_path") ?? runner.GetValueOrDefault("wine.custom_wine_path"), steamRoot) is not var (bin, proton))
        {
            why = $"its Wine version {version} isn't installed where Lutris keeps its builds";
            return null;
        }
        return new LauncherPrefix("Lutris", Path.TrimEndingDirectorySeparator(prefix), bin, proton, f.Cache, Env(f, runner, yml), Args: yml.GetValueOrDefault("game.args"));
    }

    /// <summary>The runner's yml (runners/&lt;runner&gt;.yml), in the data folder, else the config folder; empty when there's none.</summary>
    static Dictionary<string, string> Runner((string Data, string Config, string? Cache) f, string runner) =>
        runner.Length > 0 && Safe(runner) && new[] { Path.Combine(f.Data, "runners", runner + ".yml"), Path.Combine(f.Config, "runners", runner + ".yml") }.FirstOrDefault(File.Exists) is { } r
            ? Yaml(r) : [];

    /// <summary>The variables Lutris gives a game: system.env of system.yml, the runner's and the game's, a later one's variable
    /// over an earlier one's, as Lutris merges them.</summary>
    static List<(string Name, string Value)> Env((string Data, string Config, string? Cache) f, Dictionary<string, string> runner, Dictionary<string, string> yml)
    {
        var system = new[] { Path.Combine(f.Data, "system.yml"), Path.Combine(f.Config, "system.yml") }.FirstOrDefault(File.Exists) is { } s ? Yaml(s) : [];
        return new[] { system, runner, yml }.SelectMany(y => y.Where(v => v.Key.StartsWith("system.env.", StringComparison.Ordinal)).Select(v => (v.Key["system.env.".Length..], v.Value))).ToList();
    }

    static (string Bin, bool Proton)? Build(string data, string version, string? custom, string steamRoot)
    {
        if (version == "custom") return custom is { Length: > 0 } c && Path.IsPathFullyQualified(c) && File.Exists(c) ? (c, false) : null;
        if (version == "system")
            return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d, "wine")).FirstOrDefault(File.Exists) is { } w
                ? (w, false) : null;
        if (version.IndexOfAny(['/', '\\']) >= 0 || version is "." or "..") return null;
        foreach (var dir in new[] { Path.Combine(data, "runners", "wine", version), Path.Combine(data, "runners", "proton", version), Path.Combine(steamRoot, "compatibilitytools.d", version) })
            if (File.Exists(Path.Combine(dir, "proton"))) return (dir, true);
            else if (File.Exists(Path.Combine(dir, "bin", "wine"))) return (Path.Combine(dir, "bin", "wine"), false);
        return null;
    }

    /// <summary>A Lutris yml's scalars two levels deep, as "section.key": a section is a line "name:" at the start, a value
    /// "  key: value" under it, quotes taken off. A map under "  name:" one level deeper as "section.name.key". Lists, deeper
    /// levels and anything else are left out.</summary>
    internal static Dictionary<string, string> Yaml(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? section = null, sub = null;
        if (SteamSource.Small(path) is not { } text) return values;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.TrimStart().StartsWith('#')) continue;
            if (!char.IsWhiteSpace(line[0]))
            {
                (section, sub) = (line.EndsWith(':') && !line.Contains(' ') ? line[..^1] : null, null);
                continue;
            }
            var indent = line.Length - line.TrimStart(' ').Length;
            // a map one level down (system's env) as section.sub.key
            if (indent == 2) sub = line.EndsWith(':') && !line[2..].Contains(' ') ? line[2..^1] : null;
            if (section == null || indent != 2 && (indent != 4 || sub == null) || line.TrimStart().StartsWith('-')) continue;
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0) continue;
            var value = line[(colon + 2)..].Trim();
            if (value.Length >= 2 && (value[0] == '\'' && value[^1] == '\'' || value[0] == '"' && value[^1] == '"'))
                value = value[0] == '\'' ? value[1..^1].Replace("''", "'") : value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
            values[indent == 2 ? $"{section}.{line[indent..colon]}" : $"{section}.{sub}.{line[indent..colon]}"] = value;
        }
        return values;
    }
}
