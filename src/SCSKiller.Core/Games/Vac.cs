using System.Diagnostics;

namespace SCSKiller.Core.Games;

/// <summary>Valve Anti-Cheat. It runs in Steam's client, not from the game's files, so no marker finds it, and it bans for a
/// foreign DLL in the game's process. The recorder is never installed in a VAC game; compiling from its files is unaffected
/// (VAC is not an <see cref="AntiCheat"/> verdict). A game is Steam's by its id, or by its folder being in a Steam library
/// whatever store found it. A Steam game is VAC when Steam marks it (appinfo.vdf common/category/category_8, the store's
/// "Valve Anti-Cheat enabled"), when it is one of Valve's multiplayer titles (Deadlock carries no flag), and whenever Steam's
/// data can't say: no manifest names its folder, appinfo.vdf missing or unreadable, the app absent from it or without its
/// info. A game outside every Steam library is not VAC. Under Wine (Proton) Steam's data is the Linux Steam's
/// (<see cref="SteamSource.AppInfoPath(string)"/>). The proxy checks the same flag itself at each launch (proxy.cpp
/// vac_now), against the app id the ini gives it (<see cref="AppId"/>).</summary>
public static class Vac
{
    /// <summary>Valve's multiplayer titles with VAC: Counter-Strike, Condition Zero, Counter-Strike: Source, Day of Defeat:
    /// Source, Half-Life 2: Deathmatch, Team Fortress 2, Left 4 Dead 1 and 2, Dota 2, Counter-Strike 2, Garry's Mod, Deadlock.</summary>
    public static readonly IReadOnlySet<uint> Valve = new HashSet<uint> { 10, 80, 240, 300, 320, 440, 500, 550, 570, 730, 4000, 1422450 };

    /// <summary>A read of appinfo.vdf that failed is tried again after this, the games blocked meanwhile.</summary>
    static readonly TimeSpan RetryFailed = TimeSpan.FromSeconds(30);

    static readonly object Gate = new();
    static (string Path, string Stamp, Dictionary<uint, SteamApp>? Apps, string? Error, Stopwatch Age)? _cache;

    /// <param name="Vac">true = Steam marks it VAC, false = Steam's info says it isn't, null = no usable info</param>
    /// <param name="LaunchExes">the file names of the exes its launch options start, every branch's</param>
    sealed record SteamApp(bool? Vac, string[] LaunchExes);

    /// <summary>The Steam app the game is: by its "steam:" id, else by the library folder it's in. Steam: whether it is in
    /// Steam at all (false: outside every Steam library, VAC doesn't apply); a null id with Steam = no manifest names it.</summary>
    public static uint? AppId(Game g, string steamRoot, out bool steam)
    {
        steam = true;
        if (g.Id.StartsWith("steam:", StringComparison.Ordinal)) return SteamSource.AppId(g.Id);
        foreach (var path in new[] { g.InstallDir, g.ExePath })
        {
            try
            {
                if (SteamSource.AppAt(steamRoot, path) is { InLibrary: true } at) return at.Id;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // can't tell: Steam's when the path is in a steamapps folder (blocked), else not (a game clearly elsewhere)
                if (path.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) return null;
            }
        }
        steam = false;
        return null;
    }

    /// <summary>The game is VAC. <paramref name="steamRoot"/>, <paramref name="appinfo"/>: Steam's folder and its appinfo.vdf
    /// (default: Steam's own).</summary>
    public static bool Is(Game g, string? appinfo = null, string? steamRoot = null)
    {
        var root = steamRoot ?? SteamSource.Root();
        var id = AppId(g, root, out var steam);
        if (!steam) return false;
        if (id is not { } app) return true;
        if (Valve.Contains(app)) return true;
        return Apps(appinfo ?? SteamSource.AppInfoPath(root)) is not { } apps || apps.GetValueOrDefault(app)?.Vac != false;
    }

    /// <summary>The file names of the exes Steam's launch options start for the game's app, in every branch (appinfo.vdf
    /// config/launch/*/executable): a launcher such as start_protected_game.exe names the game's anti-cheat even when the
    /// install doesn't (<see cref="GameFiles.DetectAntiCheat"/>). Empty when Steam's data can't say.</summary>
    public static IEnumerable<string> LaunchExes(Game g, string? steamRoot = null)
    {
        var root = steamRoot ?? SteamSource.Root();
        return AppId(g, root, out _) is { } app && Apps(SteamSource.AppInfoPath(root))?.GetValueOrDefault(app) is { } a ? a.LaunchExes : [];
    }

    /// <summary>Why Steam's appinfo.vdf can't be read, with its path; null when it reads.</summary>
    public static string? ReadError(string? steamRoot = null)
    {
        var path = SteamSource.AppInfoPath(steamRoot ?? SteamSource.Root());
        if (path.Length == 0) return "appinfo.vdf: STEAM_COMPAT_CLIENT_INSTALL_PATH has no Windows path";
        lock (Gate)
            return Apps(path) == null && _cache is { Error: { } error } c && c.Path == path ? $"{path}: {error}" : null;
    }

    /// <summary>Every app of appinfo.vdf. VAC: null without a common section, or with neither categories nor complete info.
    /// Null when the file can't be read. Read again when it changes; a failed read again after <see cref="RetryFailed"/>.</summary>
    static Dictionary<uint, SteamApp>? Apps(string path)
    {
        if (path.Length == 0) return null;
        var f = new FileInfo(path);
        var stamp = f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "";
        lock (Gate)
        {
            if (_cache is { } c && c.Path == path && c.Stamp == stamp && (c.Apps != null || c.Age.Elapsed < RetryFailed)) return c.Apps;
            var states = new Dictionary<uint, uint>();
            string? error = f.Exists ? null : "missing";
            var apps = (f.Exists ? SteamSource.Apps(path, null, states, e => error = e) : null)
                ?.ToDictionary(a => a.Key, a => new SteamApp(Verdict(a.Value, states.GetValueOrDefault(a.Key)), LaunchExes(a.Value)));
            _cache = (path, stamp, apps, apps == null ? error ?? "unreadable" : null, Stopwatch.StartNew());
            return apps;
        }
    }

    static bool? Verdict(Dictionary<string, object> app, uint state) =>
        app.GetValueOrDefault("common") is not Dictionary<string, object> common ? null
        : common.GetValueOrDefault("category") is Dictionary<string, object> cat ? cat.ContainsKey("category_8")
        : state == 2 ? false : null;   // 2: Steam has the app's info, and it lists no categories

    static string[] LaunchExes(Dictionary<string, object> app) =>
        app.GetValueOrDefault("config") is Dictionary<string, object> config && config.GetValueOrDefault("launch") is Dictionary<string, object> launch
            ? [.. launch.Values.OfType<Dictionary<string, object>>().Select(l => l.GetValueOrDefault("executable")).OfType<string>()
                .Select(e => e.Split('\\', '/')[^1]).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)]
            : [];
}
