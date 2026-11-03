using System.Text.Json;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.Games;

/// <summary>The Windows games Faugus Launcher runs through umu on a Proton build: games.json in its data folder
/// ($XDG_DATA_HOME/faugus-launcher, else its Flatpak's), each game's path, prefix and runner, "Default" naming config.json's
/// default-runner (<see cref="PrefixOf"/>). Id "faugus:&lt;gameid&gt;", a game of this PC's own as one added by hand: its exe
/// must resolve to a game folder of its own (<see cref="ManualSource.Resolve"/>, <see cref="ManualSource.RootProblem"/>)
/// outside the system's, Steam's and Wine's folders (<see cref="SteamShortcutSource.StartProblem"/>). Store.Other, told
/// apart by its id. Only read.</summary>
public sealed class FaugusSource(IReadOnlyList<(string Data, string Config, string? Cache)>? folders = null, string? steamRoot = null) : IGameSource, ILeavesOut
{
    public Store Store => Store.Other;

    public const string IdPrefix = "faugus:", Flatpak = "io.github.Faugus.faugus-launcher";

    /// <summary>A game left out, and why.</summary>
    public IProgress<string>? Log { get; set; }

    public Func<string, bool> LeftOut { get; set; } = _ => false;

    public static bool Is(string gameId) => gameId.StartsWith(IdPrefix, StringComparison.Ordinal) && gameId.Length > IdPrefix.Length;

    /// <summary>Faugus's data and config folders, and the XDG_CACHE_HOME its games run with (null: the user's own): the native
    /// Faugus's first, then its Flatpak's.</summary>
    public static IReadOnlyList<(string Data, string Config, string? Cache)> Folders(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        static string Xdg(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } x && Path.IsPathRooted(x) ? x : fallback;
        var app = Path.Combine(home, ".var", "app", Flatpak);
        return [(Path.Combine(Xdg("XDG_DATA_HOME", Path.Combine(home, ".local", "share")), "faugus-launcher"), Path.Combine(Xdg("XDG_CONFIG_HOME", Path.Combine(home, ".config")), "faugus-launcher"), null),
            (Path.Combine(app, "data", "faugus-launcher"), Path.Combine(app, "config", "faugus-launcher"), Path.Combine(app, "cache"))];
    }

    public IReadOnlyList<Game> Discover()
    {
        var games = new List<Game>();
        var root = steamRoot ?? SteamSource.Root();
        foreach (var f in folders ?? Folders())
            foreach (var e in Entries(f.Data))
                if (Runner(e, f.Config) is ("Linux-Native" or "Steam") and var runner) Log?.Report($"Faugus: {e.Title} isn't listed: Faugus runs it with {runner}");
                else if (LeftOut(e.Exe)) continue;
                else if (GameOf(e, root, out var why) is { } g) games.Add(g);
                else Log?.Report($"Faugus: {e.Title} isn't listed: {why}");
        return games.DistinctBy(g => g.Id).ToList();
    }

    /// <summary>The variable Faugus starts a game of its own with that names it (runner.py): FAUGUSID=&lt;gameid&gt;.</summary>
    public static (string Name, string Value)? LaunchVariable(Game g) => Is(g.Id) ? ("FAUGUSID", g.Id[IdPrefix.Length..]) : null;

    /// <summary>What Faugus's own desktop shortcut for the game runs: --game &lt;gameid&gt; from the exe's folder, through the
    /// Flatpak's launcher when only its games.json lists the game. Null when no games.json lists it, or that launcher isn't
    /// installed (faugus-launcher on PATH, else the Flatpak and the flatpak command).</summary>
    public static System.Diagnostics.ProcessStartInfo? Launch(Game g, IReadOnlyList<(string Data, string Config, string? Cache)>? folders = null)
    {
        if (!Is(g.Id)) return null;
        var id = g.Id[IdPrefix.Length..];
        static string? OnPath(string name) =>
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            foreach (var f in folders ?? Folders())
                if (Entries(f.Data).FirstOrDefault(x => x.Id == id) is { } e)
                    return f.Cache == null   // only the Flatpak's folders have a cache of their own
                        ? OnPath("faugus-launcher") is { } native ? new(native, ["--game", id]) { WorkingDirectory = Path.GetDirectoryName(e.Exe) } : null
                        : OnPath("flatpak") is { } flatpak && new[] { "/var/lib/flatpak/app", Path.Combine(home, ".local", "share", "flatpak", "app") }.Any(d => Directory.Exists(Path.Combine(d, Flatpak)))
                            ? new(flatpak, ["run", "--command=/app/bin/faugus-launcher", Flatpak, "--game", id]) { WorkingDirectory = Path.GetDirectoryName(e.Exe) } : null;
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }

    /// <summary>The title of another games.json entry Faugus would start with <paramref name="v"/> too, in any of its folders: one
    /// of the same gameid, or one whose variables set it, its settings' (<see cref="Env"/>) or on a Proton its prefix's, as umu
    /// sets them (<see cref="LauncherPrefix.Exports"/>). Null when there's none, <paramref name="unread"/> when a games.json
    /// couldn't be read.</summary>
    public static string? Twin(Game g, (string Name, string Value) v, out bool unread, IReadOnlyList<(string Data, string Config, string? Cache)>? folders = null)
    {
        unread = false;
        var id = Is(g.Id) ? g.Id[IdPrefix.Length..] : null;
        var own = false;
        try
        {
            foreach (var f in folders ?? Folders())
            {
                var file = Path.Combine(f.Data, "games.json");
                if (File.Exists(file) && HeroicSource.Json(file) is not { ValueKind: JsonValueKind.Array }) unread = true;
                foreach (var e in Entries(f.Data))
                {
                    if (e.Id == id && !own) { own = true; continue; }
                    var env = Env(e, f.Config);
                    if (("FAUGUSID", e.Id) == v || env.Contains(v)
                        || e.Prefix is { Length: > 0 } prefix && Runner(e, f.Config) is not ("Linux-Native" or "Steam") && LauncherPrefix.Exports(prefix, true, true, e.Exe, null, env).Contains(v))
                        return e.Title;
                }
            }
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { unread = true; }
        return null;
    }

    internal sealed record Entry(string Id, string Title, string Exe, string? Prefix, string? Runner, string? Args = null, string? GameArgs = null);

    /// <summary>games.json's games with a gameid that is one file name and a path; none when it can't be read.</summary>
    static List<Entry> Entries(string data)
    {
        var list = new List<Entry>();
        if (HeroicSource.Json(Path.Combine(data, "games.json")) is not { ValueKind: JsonValueKind.Array } all) return list;
        foreach (var g in all.EnumerateArray())
            if (g.ValueKind == JsonValueKind.Object && HeroicSource.Str(g, "gameid") is { } id && SafeId(id) && HeroicSource.Str(g, "path") is { Length: > 0 } exe)
                list.Add(new(id, HeroicSource.Str(g, "title") is { Length: > 0 } t ? t : id, Home(exe), HeroicSource.Str(g, "prefix") is { } p ? Home(p) : null, HeroicSource.Str(g, "runner"),
                    HeroicSource.Str(g, "launch_arguments"), HeroicSource.Str(g, "game_arguments")));
        return list;
    }

    // Faugus expands a leading ~ in a game's path and prefix
    static string Home(string path) => path.StartsWith("~/", StringComparison.Ordinal) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..] : path;

    static bool SafeId(string id) => id.Length is > 0 and <= 128 && id != "." && id != ".." && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    static Game? GameOf(Entry e, string steamRoot, out string? why)
    {
        why = null;
        try
        {
            if (!e.Exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                why = $"{Path.GetFileName(e.Exe)} isn't a Windows program";
                return null;
            }
            if (SteamShortcutSource.StartProblem(steamRoot, e.Exe) is { } bad)
            {
                why = bad;
                return null;
            }
            var r = ManualSource.Resolve(e.Exe);
            why = SteamShortcutSource.StartProblem(steamRoot, r.InstallDir) ?? SteamShortcutSource.StartProblem(steamRoot, r.Exe) ?? ManualSource.RootProblem(r.InstallDir, r.Exe, []);
            return why == null ? new Game(IdPrefix + e.Id, e.Title, Store.Other, r.InstallDir, r.Exe) : null;
        }
        catch (ArgumentException x) { why = x.Message; }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or NotSupportedException) { why = x.Message; }
        catch (Exception x) when (x is not OperationCanceledException) { why = $"{x.GetType().Name}: {x.Message}"; }
        return null;
    }

    /// <summary>Where Faugus runs a game of its own, read from its files now: its prefix, on the Proton its runner names
    /// (<see cref="Build"/>). Null with the reason when it can't be told or the runner is no Proton; null with no reason for a
    /// game of no Faugus.</summary>
    public static LauncherPrefix? PrefixOf(Game g, out string? why, IReadOnlyList<(string Data, string Config, string? Cache)>? folders = null)
    {
        why = null;
        if (!Is(g.Id)) return null;
        var id = g.Id[IdPrefix.Length..];
        try
        {
            foreach (var f in folders ?? Folders())
            {
                if (Entries(f.Data).FirstOrDefault(x => x.Id == id) is not { } e) continue;
                if (e.Prefix is not { Length: > 0 } prefix || !Path.IsPathFullyQualified(prefix))
                {
                    why = "Faugus names no Wine prefix for it";
                    return null;
                }
                var runner = Runner(e, f.Config);
                if (runner is not { Length: > 0 })
                {
                    why = "Faugus names no runner for it: pick a Proton in its settings";
                    return null;
                }
                if (runner is "Linux-Native" or "Steam")
                {
                    why = $"Faugus runs it with {runner}, not on a Proton";
                    return null;
                }
                if (Build(runner) is not { } proton)
                {
                    why = $"its runner {runner} isn't installed where Faugus looks for it";
                    return null;
                }
                return new LauncherPrefix("Faugus", Path.TrimEndingDirectorySeparator(prefix), proton, true, f.Cache, Env(e, f.Config), Args: e.GameArgs);
            }
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            why = $"Faugus's files couldn't be read: {x.Message}";
            return null;
        }
        why = "Faugus doesn't list it anymore";
        return null;
    }

    /// <summary>The variables Faugus gives a game: envar.json's KEY=VALUE lines, then the KEY=VALUE words of its
    /// launch_arguments, which Faugus puts before the command for the shell (an unquoted leading ~/ the shell expands, the quotes it takes off).</summary>
    static List<(string Name, string Value)> Env(Entry e, string config)
    {
        var env = new List<(string Name, string Value)>();
        if (HeroicSource.Json(Path.Combine(config, "envar.json")) is { ValueKind: JsonValueKind.Array } lines)
            foreach (var l in lines.EnumerateArray())
                if (l.ValueKind == JsonValueKind.String && l.GetString()!.Split('=', 2) is [var k, var v] && k.Trim().Length > 0) env.Add((k.Trim(), v.Trim()));
        foreach (Match word in Regex.Matches(e.Args ?? "", @"(?:[^\s'""]+|'[^']*'|""[^""]*"")+"))
            if (!word.Value.StartsWith('-') && word.Value.Split('=', 2) is [{ Length: > 0 } k, var v])
            {
                var unquoted = Regex.Replace(v, @"'([^']*)'|""([^""]*)""", q => q.Groups[1].Value + q.Groups[2].Value);
                env.Add((k, v.StartsWith("~/", StringComparison.Ordinal) ? Home(unquoted) : unquoted));
            }
        return env;
    }

    /// <summary>The game's runner, "Default" as config.json's default-runner.</summary>
    static string? Runner(Entry e, string config) =>
        e.Runner != "Default" ? e.Runner
        : HeroicSource.Json(Path.Combine(config, "config.json")) is { ValueKind: JsonValueKind.Object } c ? HeroicSource.Str(c, "default-runner")?.Trim('"') : null;

    /// <summary>The Proton folder of a runner as Faugus picks it, the first of these that is a folder: the runner's own path,
    /// Proton-CachyOS (System) in $XDG_DATA_DIRS, else a build by that name in the native, then the Flatpak Steam's
    /// compatibilitytools.d. Null when that folder holds no Proton.</summary>
    static string? Build(string runner)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        IEnumerable<string> dirs = Path.IsPathFullyQualified(runner) ? [runner]
            : runner == "Proton-CachyOS (System)" ? (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } x ? x : "/usr/local/share:/usr/share")
                .Split(':', StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d, "steam", "compatibilitytools.d", "proton-cachyos-slr"))
            : runner.IndexOfAny(['/', '\\']) >= 0 || runner is "." or ".." ? []
            : new[] { Path.Combine(home, ".local", "share", "Steam"), Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam") }
                .Select(s => Path.Combine(s, "compatibilitytools.d", runner));
        return dirs.FirstOrDefault(Directory.Exists) is { } dir && File.Exists(Path.Combine(dir, "proton")) ? dir : null;
    }
}
