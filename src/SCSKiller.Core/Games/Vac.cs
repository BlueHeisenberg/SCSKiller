using System.Diagnostics;

namespace SCSKiller.Core.Games;

/// <summary>Valve Anti-Cheat. It runs in Steam's client, not from the game's files, so no marker finds it, and it bans for a
/// foreign DLL in the game's process. The recorder is never installed in a VAC game; compiling from its files is unaffected
/// (VAC is not an <see cref="AntiCheat"/> verdict). A game is Steam's by its id, or by its folder being in a Steam library
/// whatever store found it. A Steam game is VAC when Steam marks it (appinfo.vdf common/category/category_8, the store's
/// "Valve Anti-Cheat enabled"), when it is one of Valve's multiplayer titles (Deadlock carries no flag), and whenever Steam's
/// data can't say: no manifest names its folder, appinfo.vdf missing or unreadable, the app absent from it or without its
/// info. A game outside every Steam library is not VAC, unless a steam_appid.txt in its folders names an app appinfo.vdf
/// marks VAC or lists without its info (<see cref="Named"/>), as the proxy's named_vac refuses it. Under Wine (Proton) Steam's data is the Linux Steam's
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
        bool? exeWeGame = null;
        // a WeGame install folder no manifest names is no Steam game's, as the proxy's vac_now takes the exe and its final path
        bool WeGame(string path)
        {
            try
            {
                return (exeWeGame ??= WeGameSource.InSteamLibrary(g.ExePath) && GameFiles.FinalPath(g.ExePath) is { } exe
                    && WeGameSource.InSteamLibrary(exe) && Unowned(steamRoot, [Path.GetFullPath(g.ExePath), exe])) && WeGameSource.InSteamLibrary(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return false; }
        }
        foreach (var path in new[] { g.InstallDir, g.ExePath })
        {
            try
            {
                if (SteamSource.AppAt(steamRoot, path) is { InLibrary: true } at && (at.Id != null || !WeGame(path))) return at.Id;
                // through a link, as the proxy resolves the exe: a hand-added folder can be a junction into a library
                if (GameFiles.FinalPath(path) is { } real && SteamSource.AppAt(steamRoot, real) is { InLibrary: true } linked && (linked.Id != null || !WeGame(real))) return linked.Id;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // can't tell: Steam's when the path is in a steamapps folder (blocked), else not (a game clearly elsewhere)
                if (path.Contains(SteamSource.AppsPart + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            }
        }
        steam = false;
        return null;
    }

    /// <summary>No Steam manifest owns the exe at <paramref name="paths"/> (as named and its final path), as the proxy's vac_now
    /// looks for an owner: libraryfolders.vdf lists libraries, none has a name ending in a dot or a space, none without its
    /// steamapps holds a path, and in Steam's folder, each library and each path's own library every manifest reads as one
    /// AppState with one app id and folder, none names a common folder a path is in, and none's folder that is there holds a
    /// path through its final path. False when one can't be read.</summary>
    static bool Unowned(string steamRoot, string[] paths)
    {
        try { return Swept(steamRoot, paths); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return false; }
    }

    static bool Swept(string steamRoot, string[] paths)
    {
        var names = StringComparison.OrdinalIgnoreCase;
        var s = Path.DirectorySeparatorChar;
        bool Holds(string dir, string p) => p.Equals(dir, names) || p.StartsWith(Path.TrimEndingDirectorySeparator(dir) + s, names);
        // every library must be known: one libraryfolders.vdf doesn't list could hold a Steam game's folder linked here
        if (!SteamSource.Values(File.ReadAllText(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf")), "path").Any()) return false;
        var folders = new List<string>();
        var libraries = new List<string> { Path.GetFullPath(steamRoot) };
        libraries.AddRange(SteamSource.Libraries(steamRoot));
        foreach (var p in paths)
        {
            var at = p.IndexOf(SteamSource.CommonPart, names);
            if (at < 0) return false;
            libraries.Add(p[..at]);
            folders.Add(p[(at + SteamSource.CommonPart.Length)..].Split(s)[0]);
        }
        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (lib.Split(s).Any(n => n.EndsWith('.') || n.EndsWith(' '))) return false;
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps))
            {
                if (paths.Any(p => Holds(lib, p))) return false;
                continue;
            }
            foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(acf);
                var dirs = SteamSource.Values(text, "installdir").ToList();
                var ids = SteamSource.Values(text, "appid").ToList();
                if (dirs.Any(d => folders.Any(f => f.Equals(d, names)))) return false;
                if (SteamSource.KeyValues(text) is not { Count: 1 } top || !top.ContainsKey("AppState") || top["AppState"] is not Dictionary<string, object>
                    || dirs.Count != 1 || ids.Count != 1 || ids[0] is not { Length: > 0 and <= 10 } digits || !digits.All(char.IsAsciiDigit)
                    || !uint.TryParse(digits, out var id) || id == 0 || dirs[0] is "" or "." or ".." || dirs[0].IndexOfAny(['\\', '/', ':']) >= 0) return false;
                var folder = Path.Combine(apps, "common", dirs[0]);
                if (GameFiles.FinalPath(folder) is { } owner ? paths.Any(p => Holds(owner, p)) : Directory.Exists(folder) || File.Exists(folder)) return false;
            }
        }
        return true;
    }

    /// <summary>The path is in a game folder of drive C of the non-Steam game's own Proton prefix
    /// (steamapps/compatdata/&lt;its shortcut id&gt;/pfx) in Steam's folder or a library, as named and on disk: not in Wine's
    /// Windows folder nor in any Steam folder but compatdata, libraryfolders.vdf read, no library left out
    /// (<paramref name="leftOut"/>: never read), and no Steam game's folder and no other app's or shortcut's prefix (its
    /// folder, pfx or drive_c) is it or holds it on disk. A game installed into the prefix Steam runs it in, which the
    /// recorder's own check admits too (admit_posix.h vac_now). False when Steam's folders can't be read.</summary>
    static bool InOwnPrefix(uint shortcut, string path, string steamRoot, Func<string, bool>? leftOut)
    {
        var id = shortcut.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var s = Path.DirectorySeparatorChar;
        try
        {
            var full = Path.GetFullPath(path);
            if (!NamesPrefix(id, full) || GameFiles.FinalPath(full) is not { } real) return false;
            var names = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
            bool Game(string drive, string p)
            {
                var parts = p.Split(s);
                // any steamapps on the way but one to the game's own prefix: a Steam folder or prefix the libraries listed may not name
                return GameFiles.Inside(drive, p, names) && Path.GetRelativePath(drive, p).Split(s) is var rel && !Is(rel[0], "windows") && !rel.Any(n => Is(n, "steamapps"))
                    && !parts.Zip(parts.Skip(1)).Any(n => n.First == "drive_c" && Is(n.Second, "windows"))
                    && Enumerable.Range(0, parts.Length).All(i => !Is(parts[i], "steamapps") || i + 2 < parts.Length && parts[i + 1] == "compatdata" && parts[i + 2] == id);
            }
            // every library must be known: one libraryfolders.vdf doesn't list could hold a Steam game's folder linked here
            if (!SteamSource.Values(File.ReadAllText(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf")), "path").Any()) return false;
            var libraries = Libraries(steamRoot);
            if (LeftOut(libraries, leftOut)) return false;
            if (!libraries.Any(lib => Path.Combine(lib, "steamapps", "compatdata", id, "pfx", "drive_c") is var drive
                    && Game(drive, full) && GameFiles.FinalPath(drive) is { } on && Game(on, real))) return false;
            bool Holds(string? f) => f != null && (real.Equals(f, names) || real.StartsWith(Path.EndsInDirectorySeparator(f) ? f : f + s, names));
            foreach (var lib in libraries)
            {
                if (Entries(Path.Combine(lib, "steamapps", "common")).Any(f => Holds(Real(f)))) return false;
                if (Entries(Path.Combine(lib, "steamapps", "compatdata")).Any(d => Path.GetFileName(d) != id
                        && new[] { d, Path.Combine(d, "pfx"), Path.Combine(d, "pfx", "drive_c") }.Any(p => Holds(Real(p))))) return false;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return false; }
    }

    static bool NamesPrefix(string id, string full)
    {
        var s = Path.DirectorySeparatorChar;
        return full.Contains($"{s}compatdata{s}{id}{s}pfx{s}drive_c{s}", StringComparison.Ordinal);
    }

    static List<string> Libraries(string steamRoot) => SteamSource.Libraries(steamRoot).Prepend(Path.GetFullPath(steamRoot))
        .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();

    // a library whose game folders or prefixes are left out, by the folder or one above it
    static bool LeftOut(List<string> libraries, Func<string, bool>? leftOut) =>
        leftOut != null && libraries.Any(l => leftOut(Path.Combine(l, "steamapps", "common")) || leftOut(Path.Combine(l, "steamapps", "compatdata")));

    /// <summary>The game is a non-Steam game in its own prefix (<see cref="InOwnPrefix"/>) by its folder's name, and a Steam
    /// library is left out (<paramref name="leftOut"/>): it counts as VAC as that library is never read.</summary>
    public static bool PrefixLeftOut(Game g, Func<string, bool> leftOut, string? steamRoot = null)
    {
        try
        {
            return SteamShortcuts.AppId(g.Id) is { } own && NamesPrefix(own.ToString(System.Globalization.CultureInfo.InvariantCulture), Path.GetFullPath(g.InstallDir))
                && LeftOut(Libraries(steamRoot ?? SteamSource.Root()), leftOut);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return false; }
    }

    // a folder's entries, none when it isn't there; one that can't be listed throws
    static string[] Entries(string dir)
    {
        try { return Directory.GetFileSystemEntries(dir); }
        catch (DirectoryNotFoundException) { return []; }
    }

    // the path's real path, null when nothing is there (a link to nothing included); one that can't be told throws
    static string? Real(string path)
    {
        if (GameFiles.FinalPath(path) is { } real) return real;
        try { File.GetAttributes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
        return null;
    }

    /// <summary>The game is VAC. <paramref name="steamRoot"/>, <paramref name="appinfo"/>: Steam's folder and its appinfo.vdf
    /// (default: Steam's own). <paramref name="leftOut"/>: the folders the user left out, never read: a non-Steam game in
    /// its own prefix is VAC while a Steam library is left out (<see cref="InOwnPrefix"/>).</summary>
    public static bool Is(Game g, string? appinfo = null, string? steamRoot = null, Func<string, bool>? leftOut = null)
    {
        var root = steamRoot ?? SteamSource.Root();
        var path = appinfo ?? SteamSource.AppInfoPath(root);
        var id = AppId(g, root, out var steam);
        // a non-Steam game in its own prefix is no Steam app's, as outside every library
        if (steam && id == null && SteamShortcuts.AppId(g.Id) is { } own && InOwnPrefix(own, g.InstallDir, root, leftOut) && InOwnPrefix(own, g.ExePath, root, leftOut))
            steam = false;
        if (steam && (id is not { } app || Valve.Contains(app) || Apps(path) is not { } apps || apps.GetValueOrDefault(app)?.Vac != false)) return true;
        return Named(g).Any(n => Valve.Contains(n) || Apps(path)?.GetValueOrDefault(n) is { Vac: true or null });
    }

    /// <summary>The app ids the steam_appid.txt files name in the folders the proxy's vac_named reads (folders_to: the exe's
    /// folder and each one above it up to the install folder; the install folder alone besides it when it isn't above it, a root the proxy refuses),
    /// read as it reads them: leading digits after a BOM and blanks. A file without them, or one that can't be read, names none.</summary>
    internal static IEnumerable<uint> Named(Game g)
    {
        var any = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true, AttributesToSkip = 0 };
        static string Sep(string p) => Path.EndsInDirectorySeparator(p) ? p : p + Path.DirectorySeparatorChar;
        var dirs = new List<string>();
        try
        {
            var install = Path.GetFullPath(g.InstallDir);
            var here = Path.GetDirectoryName(Path.GetFullPath(g.ExePath)) ?? "";
            if (!Sep(here).StartsWith(Sep(install), StringComparison.OrdinalIgnoreCase)) dirs.AddRange([here, install]);
            else
                for (string? d = here; d != null && dirs.Count < 64; d = Path.GetDirectoryName(d))
                {
                    dirs.Add(d);
                    if (Sep(d).Length <= Sep(install).Length) break;
                }
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        foreach (var dir in dirs)
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "steam_appid.txt", any); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { files = []; }
            foreach (var f in files)
            {
                byte[] b;
                try
                {
                    if (new FileInfo(f).Length > 4096) continue;
                    b = File.ReadAllBytes(f);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                var bom = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
                var t = System.Text.Encoding.Latin1.GetString(b, bom, b.Length - bom);
                var digits = new string(t.TrimStart(' ', '\t', '\r', '\n', '\v', '\f', '+').TakeWhile(char.IsAsciiDigit).ToArray());
                if (digits.Length is > 0 and <= 10 && ulong.Parse(digits) is > 0 and <= uint.MaxValue and var v) yield return (uint)v;
            }
        }
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
            // an entry that doesn't parse is listed without usable info, as the proxy's damaged entry
            if (apps != null) foreach (var id in states.Keys) apps.TryAdd(id, new SteamApp(null, []));
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
