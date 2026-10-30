namespace SCSKiller.Core.Games;

/// <summary>The games added to Steam as non-Steam games (<see cref="SteamShortcuts"/>) whose exe is a Windows program: on
/// Linux the way to add a game no store lists. One Steam runs without a compatibility tool of its own is listed not ready
/// (<see cref="NotReady"/>) until Steam maps one. A shortcut to any other program is a Linux program, with nothing to
/// compile: not listed. Its exe must be a 64-bit Windows program that resolves to a game folder of its own as a game
/// added by hand does (<see cref="ManualSource.Resolve"/>, <see cref="ManualSource.RootProblem"/>), and it, that folder
/// and the shortcut's start folder must be outside the system's folders, Steam's own files and Wine's Windows folder
/// (<see cref="PathProblem"/>): the recorder goes in beside the exe. Store.Other, told apart by its id.</summary>
public sealed class SteamShortcutSource(string? steamRoot = null) : IGameSource
{
    public Store Store => Store.Other;

    /// <summary>A shortcut or a shortcuts.vdf left out, and why.</summary>
    public IProgress<string>? Log { get; set; }

    /// <summary>The status reason of a listed game Steam runs without Proton: never planned or compiled.</summary>
    public const string NotReadyReason = "pick a Proton in its Properties, Compatibility";

    public const string NoProton = "Steam runs it without Proton: " + NotReadyReason;

    string Root => steamRoot ?? SteamSource.Root();

    public IReadOnlyList<Game> Discover()
    {
        var root = Root;
        var games = new List<Game>();
        foreach (var s in SteamShortcuts.All(root, why => Log?.Report($"Steam: {why}")))
            if (GameOf(root, s, out var why) is { } g) games.Add(g);
            else if (why != null) Log?.Report($"Steam: the non-Steam game {s.Name} isn't listed: {why}");
        return games;
    }

    /// <summary>Why a game of this source isn't ready: Steam runs it without Proton. Read from Steam's config at each call,
    /// so it is ready once Steam maps a tool to it. Null for any other game.</summary>
    public string? NotReady(Game g) => SteamShortcuts.AppId(g.Id) is { } app && !Proton(Root, app) ? NotReadyReason : null;

    // Steam Play's default is for Steam's own titles only
    static bool Proton(string root, uint app) => SteamSource.CompatTool(root, app, orDefault: false) != null;

    /// <summary>The game a shortcut to a Windows program is, whether or not Steam runs it through Proton; null with why when
    /// it isn't one SCSKiller lists. The reason is null for a shortcut whose exe is no .exe and that Steam runs without
    /// Proton: a Linux program.</summary>
    internal static Game? GameOf(string root, SteamShortcut s, out string? why)
    {
        why = null;
        if (!s.Exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            if (Proton(root, s.AppId)) why = $"{Path.GetFileName(s.Exe)} isn't a Windows program";
            return null;
        }
        try
        {
            var steam = SteamFolders(root);
            if (PathProblem(s.Exe, steam) is { } exe)
            {
                why = exe;
                return null;
            }
            if (s.StartDir.Length > 0 && PathProblem(s.StartDir, steam) is { } start)
            {
                why = $"its start folder: {start}";
                return null;
            }
            var e = ManualSource.Resolve(s.Exe);
            why = PathProblem(e.InstallDir, steam) ?? PathProblem(e.Exe, steam) ?? ManualSource.RootProblem(e.InstallDir, e.Exe, []);
            return why == null ? new Game(SteamShortcuts.IdOf(s.AppId), s.Name.Length > 0 ? s.Name : e.Name, Store.Other, e.InstallDir, e.Exe) : null;
        }
        catch (ArgumentException e) { why = e.Message; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { why = e.Message; }
        catch (Exception e) when (e is not OperationCanceledException) { why = $"{e.GetType().Name}: {e.Message}"; }
        return null;
    }

    /// <summary>Why a shortcut's start folder <paramref name="start"/> isn't one SCSKiller lists the shortcut with
    /// (<see cref="PathProblem"/>); null when it is.</summary>
    internal static string? StartProblem(string root, string start) => PathProblem(start, SteamFolders(root));

    /// <summary>Steam's install and every library's steamapps folder, real paths.</summary>
    static (string Root, List<string> Apps) SteamFolders(string root)
    {
        static string Real(string f) => GameFiles.FinalPath(f) ?? Path.GetFullPath(f);
        return (Real(root), SteamSource.Libraries(root).Select(l => Real(Path.Combine(l, "steamapps"))).Distinct(StringComparer.Ordinal).ToList());
    }

    // the system's own folders: never a game's
    static readonly string[] SystemFolders = ["/bin", "/boot", "/etc", "/lib", "/lib32", "/lib64", "/libx32", "/proc", "/sbin", "/snap", "/sys", "/usr"];

    /// <summary>Why <paramref name="path"/> (an exe or a folder of a shortcut's) isn't in a folder of games; null when it is.
    /// Judged by its real path, links resolved: refused when it doesn't exist, isn't a full path, is a drive's root or a
    /// Wine drive C, is in the system's folders or in Wine's Windows folder, or is in Steam's install or a library's
    /// steamapps folder (<paramref name="steam"/>) anywhere but a prefix's drive C, which holds games installed into a
    /// prefix. One in a library's steamapps/common is a Steam game's.</summary>
    internal static string? PathProblem(string path, (string Root, List<string> Apps) steam)
    {
        if (!Path.IsPathFullyQualified(path)) return $"{path} isn't a full path";
        if (GameFiles.FinalPath(path) is not { } real) return $"{path} doesn't exist";
        if (Path.GetPathRoot(real) == real) return $"{real} is a whole drive";
        bool In(string dir) => real == dir || real.StartsWith(Path.TrimEndingDirectorySeparator(dir) + "/", StringComparison.Ordinal);
        if (SystemFolders.Any(In)) return $"{real} is in the system's folders";
        var parts = real.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var c = 0; c < parts.Length; c++)   // every drive C on the way: a prefix may sit in another's Windows folder
            if (parts[c] == "drive_c")
            {
                if (c == parts.Length - 1) return $"{real} is a whole Wine drive";
                if (parts[c + 1].Equals("windows", StringComparison.OrdinalIgnoreCase)) return $"{real} is in Wine's Windows folder";
            }
        if (steam.Apps.FirstOrDefault(In) is { } apps)
            return Path.GetRelativePath(apps, real).Split('/') switch
            {
                ["common", _, ..] => $"{real} is a Steam game's folder: Steam lists that game",
                ["compatdata", _, "pfx", "drive_c", _, ..] => null,
                _ => $"{real} is in Steam's own folder",
            };
        return In(steam.Root) ? $"{real} is in Steam's own folder" : null;
    }
}
