using System.Runtime.InteropServices;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

/// <summary>The Windows games Lutris installed on its Wine runner: pga.db's installed rows (runner wine), each game's
/// games/&lt;configpath&gt;.yml for its exe, prefix and Wine version (<see cref="PrefixOf"/>). Lutris's folders are its own
/// ($XDG_DATA_HOME/lutris, its configs there or in $XDG_CONFIG_HOME/lutris), else its Flatpak's. Id "lutris:&lt;pga id&gt;", a
/// game of this PC's own as one added by hand: its exe must resolve to a game folder of its own
/// (<see cref="ManualSource.Resolve"/>, <see cref="ManualSource.RootProblem"/>) outside the system's, Steam's and Wine's
/// folders (<see cref="SteamShortcutSource.StartProblem"/>). Store.Other, told apart by its id. Only read.</summary>
public sealed class LutrisSource(IReadOnlyList<(string Data, string Config, string? Cache)>? folders = null, string? steamRoot = null) : IGameSource
{
    public Store Store => Store.Other;

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
                if (GameOf(f, r, root, out var why) is { } g) games.Add(g);
                else Log?.Report($"Lutris: {r.Name} isn't listed: {why}");
        return games.DistinctBy(g => g.Id).ToList();
    }

    const int MaxRows = 65536;

    internal sealed record Row(long Id, string Name, string ConfigPath, string? Directory);

    /// <summary>pga.db's installed games on the Wine runner, of a db of at most 64 MB and its first <see cref="MaxRows"/>;
    /// none when it can't be read.</summary>
    static List<Row> Installed(string data)
    {
        var rows = new List<Row>();
        var db = Path.Combine(data, "pga.db");
        try
        {
            if (!File.Exists(db) || new FileInfo(db) is var info && (info.LinkTarget != null || info.Length > 64 << 20)) return rows;
            using (var regular = GameFiles.OpenRegular(db)) if (regular == null) return rows;   // a FIFO or device by that name
            D3DSCache.Query(db, "select id, name, configpath, directory from games where installed = 1 and runner = 'wine' and configpath is not null", st =>
            {
                var name = Marshal.PtrToStringUTF8(D3DSCache.sqlite3_column_text(st, 1));
                if (rows.Count < MaxRows && Marshal.PtrToStringUTF8(D3DSCache.sqlite3_column_text(st, 2)) is { Length: > 0 } config && config.IndexOfAny(['/', '\\']) < 0 && config != "..")
                    rows.Add(new(D3DSCache.sqlite3_column_int64(st, 0), name ?? config, config, Marshal.PtrToStringUTF8(D3DSCache.sqlite3_column_text(st, 3))));
            });
        }
        catch (Exception e) when (e is DllNotFoundException or IOException or UnauthorizedAccessException) { }
        return rows;
    }

    /// <summary>The game's yml: in the data folder (Lutris 0.5.13 and later), else the config folder.</summary>
    static Dictionary<string, string>? Config((string Data, string Config, string? Cache) f, string configPath) =>
        new[] { Path.Combine(f.Data, "games", configPath + ".yml"), Path.Combine(f.Config, "games", configPath + ".yml") }.FirstOrDefault(File.Exists) is { } yml
            ? Yaml(yml) : null;

    static Game? GameOf((string Data, string Config, string? Cache) f, Row r, string steamRoot, out string? why)
    {
        why = null;
        try
        {
            if (Config(f, r.ConfigPath) is not { } yml || Exe(yml, r) is not { } exe)
            {
                why = "its Lutris config names no exe";
                return null;
            }
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
        var under = r.Directory is { Length: > 0 } d && Path.IsPathFullyQualified(d) ? d : yml.GetValueOrDefault("game.prefix");
        return under != null && Path.IsPathFullyQualified(under) ? GameFiles.Find(under, exe) : null;
    }

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
            if (Config(f, row.ConfigPath) is not { } yml)
            {
                why = "its Lutris config is gone";
                return null;
            }
            if (yml.GetValueOrDefault("game.prefix") is not { Length: > 0 } prefix || !Path.IsPathFullyQualified(prefix))
            {
                why = "Lutris names no Wine prefix for it";
                return null;
            }
            var runner = new[] { Path.Combine(f.Data, "runners", "wine.yml"), Path.Combine(f.Config, "runners", "wine.yml") }.FirstOrDefault(File.Exists) is { } r ? Yaml(r) : [];
            var version = yml.GetValueOrDefault("wine.version") ?? runner.GetValueOrDefault("wine.version");
            if (version is not { Length: > 0 })
            {
                why = "Lutris names no Wine version for it: pick one in its Runner options";
                return null;
            }
            if (Build(f.Data, version, yml.GetValueOrDefault("wine.custom_wine_path") ?? runner.GetValueOrDefault("wine.custom_wine_path"), steamRoot ?? SteamSource.Root()) is not var (bin, proton))
            {
                why = $"its Wine version {version} isn't installed where Lutris keeps its builds";
                return null;
            }
            return new LauncherPrefix("Lutris", Path.TrimEndingDirectorySeparator(prefix), bin, proton, f.Cache);
        }
        why = "Lutris doesn't list it as installed anymore";
        return null;
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
    /// "  key: value" under it, quotes taken off. Lists, deeper levels and anything else are left out.</summary>
    internal static Dictionary<string, string> Yaml(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? section = null;
        if (SteamSource.Small(path) is not { } text) return values;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.TrimStart().StartsWith('#')) continue;
            if (!char.IsWhiteSpace(line[0]))
            {
                section = line.EndsWith(':') && !line.Contains(' ') ? line[..^1] : null;
                continue;
            }
            var indent = line.Length - line.TrimStart(' ').Length;
            if (section == null || indent != 2 || line.TrimStart().StartsWith('-')) continue;
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0) continue;
            var value = line[(colon + 2)..].Trim();
            if (value.Length >= 2 && (value[0] == '\'' && value[^1] == '\'' || value[0] == '"' && value[^1] == '"'))
                value = value[0] == '\'' ? value[1..^1].Replace("''", "'") : value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
            values[$"{section}.{line[indent..colon]}"] = value;
        }
        return values;
    }
}
