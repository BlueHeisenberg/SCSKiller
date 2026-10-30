namespace SCSKiller.Core.Games;

/// <summary>Where a Steam game runs under Proton, as Steam runs it: <paramref name="CompatData"/>
/// (steamapps/compatdata/&lt;app&gt; in the game's library) and its <paramref name="Prefix"/> (pfx), the
/// <paramref name="Proton"/> folder, the Steam Linux Runtime folder holding its entry point (<paramref name="Runtime"/>), the
/// app's <paramref name="Install"/> folder and Steam's <paramref name="Libraries"/>. <paramref name="Note"/>: what to log
/// about the Proton picked, null when nothing is worth it.</summary>
public sealed record ProtonContext(uint AppId, string SteamRoot, string CompatData, string Prefix, string Proton, string Runtime, string Install,
    IReadOnlyList<string> Libraries, string? Note)
{
    /// <summary>The context of <paramref name="game"/> under the Steam folder <paramref name="steamRoot"/>; null, with the
    /// reason, when it can't run under Proton yet. The Proton is the one that last ran its prefix (config_info). When Steam
    /// maps the game to another (CompatToolMapping), or the Proton's script would set the prefix up again
    /// (<see cref="ProtonScript.Refreshes"/>: an update in place among others), the game isn't ready: verb run would change
    /// the prefix before Steam does. <see cref="SteamRoot"/> is Steam's folder as config_info names it. Only reads.</summary>
    /// <param name="toolFolders">the system's compatibilitytools.d folders (default: Steam's)</param>
    public static ProtonContext? For(Game game, string steamRoot, out string? why, IEnumerable<string>? toolFolders = null)
    {
        why = null;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.InstallDir));
            var at = (full + Path.DirectorySeparatorChar).IndexOf(SteamSource.CommonPart, StringComparison.OrdinalIgnoreCase);
            var folder = at < 0 ? "" : (full + Path.DirectorySeparatorChar)[(at + SteamSource.CommonPart.Length)..].Split(Path.DirectorySeparatorChar)[0];
            if (folder.Length == 0)
            {
                why = "it isn't in a Steam library";
                return null;
            }
            var apps = full[..(at + SteamSource.AppsPart.Length)];
            if ((SteamSource.AppId(game.Id) ?? SteamSource.AppAt(steamRoot, full).Id) is not { } app)
            {
                why = "Steam lists no app in its folder";
                return null;
            }
            return In(app, steamRoot, Path.Combine(apps, "compatdata", app.ToString()), Path.Combine(apps, "common", folder), Path.GetDirectoryName(apps)!,
                SteamSource.CompatTool(steamRoot, app), toolFolders, out why);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            why = $"Steam's files couldn't be read: {e.Message}";
            return null;
        }
    }

    /// <summary>The context of Steam's non-Steam game <paramref name="shortcut"/>, as <see cref="For"/> gives a Steam
    /// game's: its prefix is in Steam's own library (steamapps/compatdata/&lt;app id&gt;), its Proton the one Steam maps
    /// its app id to alone (Steam Play's default is for Steam's titles), its install folder its start folder, else its
    /// exe's.</summary>
    public static ProtonContext? For(SteamShortcut shortcut, string steamRoot, out string? why, IEnumerable<string>? toolFolders = null)
    {
        why = null;
        try
        {
            var app = shortcut.AppId;
            if (SteamSource.CompatTool(steamRoot, app, orDefault: false) is not { } tool)
            {
                why = SteamShortcutSource.NoProton;
                return null;
            }
            var install = shortcut.StartDir.Length > 0 ? shortcut.StartDir : Path.GetDirectoryName(shortcut.Exe)!;
            return In(app, steamRoot, ShortcutData(steamRoot, app), Path.TrimEndingDirectorySeparator(install), null,
                tool, toolFolders, out why);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            why = $"Steam's files couldn't be read: {e.Message}";
            return null;
        }
    }

    /// <summary>The context of a game another launcher runs on a Proton build (<paramref name="p"/>), as <see cref="For(Game, string, out string?, IEnumerable{string}?)"/>
    /// gives a Steam game's: its prefix is the launcher's compat data, its Proton the one the launcher names, which must be
    /// the one that last ran the prefix, in the Steam Linux Runtime Steam has for it, else umu's (Heroic's and Lutris's way of
    /// running Proton). App id 0, as umu runs it. The reasons say to start it from that launcher.</summary>
    public static ProtonContext? For(LauncherPrefix p, string install, string steamRoot, out string? why, IEnumerable<string>? toolFolders = null)
    {
        why = null;
        if (File.Exists(Path.Combine(p.Prefix, "pfx", "system.reg")) && SteamSource.LastProtonFolder(p.Prefix) == null)
        {
            why = $"its prefix doesn't name the Proton that last ran it: start it once from {p.Launcher}";
            return null;
        }
        ProtonContext? ctx = null;
        try { ctx = In(0, steamRoot, p.Prefix, Path.TrimEndingDirectorySeparator(install), null, null, toolFolders, out why, umu: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { why = $"its prefix's files couldn't be read: {e.Message}"; }
        if (ctx != null && !Same(ctx.Proton, p.Bin)) (ctx, why) = (null, $"{p.Launcher} runs it on {p.Bin} but its prefix last ran on {ctx.Proton}");
        why = why?.Replace("from Steam", "from " + p.Launcher, StringComparison.Ordinal);
        return ctx;
    }

    /// <summary>umu's Steam Linux Runtime ($XDG_DATA_HOME/umu/steamrt3, sniper), when it holds its entry point.</summary>
    static string? UmuRuntime()
    {
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x && Path.IsPathRooted(x) ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var runtime = Path.Combine(data, "umu", "steamrt3");
        return File.Exists(Path.Combine(runtime, SteamSource.EntryPoint)) ? runtime : null;
    }

    /// <param name="data">the app's compatdata folder</param>
    /// <param name="library">the game's library, when Steam's files may not list it</param>
    /// <param name="tool">the compatibility tool Steam maps the app to</param>
    static ProtonContext? In(uint app, string steamRoot, string data, string install, string? library, string? tool, IEnumerable<string>? toolFolders, out string? why, bool umu = false)
    {
        var pfx = Path.Combine(data, "pfx");
        if (!File.Exists(Path.Combine(pfx, "system.reg")))
        {
            why = "it has no Proton prefix yet: start it once from Steam";
            return null;
        }
        var mapped = tool == null ? null : SteamSource.ToolFolder(steamRoot, tool, toolFolders);
        if (mapped != null && !File.Exists(Path.Combine(mapped, "proton")))
        {
            why = $"its compatibility tool in {mapped} isn't a Proton";
            return null;
        }
        if (SteamSource.LastProtonFolder(data) is not { } proton)
        {
            why = mapped != null ? "its prefix doesn't name the Proton that last ran it: start it once from Steam"
                : tool == null ? "Steam names no Proton for it: start it once from Steam" : $"its Proton ({tool}) isn't installed: start it once from Steam";
            return null;
        }
        if (mapped != null && !Same(mapped, proton))
        {
            why = $"Steam runs it on {tool} but its prefix last ran on {proton}: start it once from Steam to move the prefix to {tool}";
            return null;
        }
        if (SteamSource.PrefixVersion(proton) is not { } version)
        {
            why = $"its Proton in {proton} doesn't say which prefix version it makes";
            return null;
        }
        var steam = steamRoot;
        if (ProtonScript.Read(proton) is not { } script)
        {
            why = $"its Proton's script in {proton} couldn't be read";
            return null;
        }
        if (script.Refreshes(data, version, ref steam) is { } refresh)
        {
            why = refresh;
            return null;
        }
        var note = mapped == null && tool != null ? $"Steam runs it on {tool}, which wasn't found, so the compile runs on the Proton that last ran its prefix ({proton})" : null;
        if ((SteamSource.RuntimeOf(steamRoot, proton, out why) ?? (umu ? UmuRuntime() : null)) is not { } runtime) return null;
        why = null;
        var libraries = SteamSource.Libraries(steamRoot).Concat(library == null ? [] : [library])
            .Select(Path.TrimEndingDirectorySeparator).Distinct(StringComparer.Ordinal).ToList();
        return new(app, steam, data, pfx, proton, runtime, install, libraries, note);
    }

    /// <summary>A non-Steam game's compatdata folder: in Steam's own library, whichever library holds the game.</summary>
    static string ShortcutData(string steamRoot, uint app) =>
        Path.Combine(steamRoot, "steamapps", "compatdata", app.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The build name of the Proton that last ran <paramref name="game"/>'s prefix (<see cref="SteamSource.ProtonBuild"/>),
    /// null when that can't be told. A non-Steam game's prefix is the one in <paramref name="steamRoot"/>'s library (default:
    /// Steam's), as <see cref="For(SteamShortcut, string, out string?, IEnumerable{string}?)"/> runs it. Only reads.</summary>
    public static string? LastVersion(Game game, string? steamRoot = null)
    {
        try
        {
            string data;
            if (SteamShortcuts.AppId(game.Id) is { } shortcut) data = ShortcutData(steamRoot ?? SteamSource.Root(), shortcut);
            else
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.InstallDir)) + Path.DirectorySeparatorChar;
                var at = full.IndexOf(SteamSource.CommonPart, StringComparison.OrdinalIgnoreCase);
                if (at < 0 || SteamSource.AppId(game.Id) is not { } app) return null;
                data = Path.Combine(full[..(at + SteamSource.AppsPart.Length)], "compatdata", app.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return SteamSource.LastProtonFolder(data) is { } proton ? SteamSource.ProtonBuild(proton) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    static bool Same(string a, string b) =>
        string.Equals(App.Libc.RealPath(a) ?? a, App.Libc.RealPath(b) ?? b, StringComparison.Ordinal);
}
