using System.Globalization;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

public sealed partial class SteamSource
{
    public static partial string Root() => Root(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Steam's folder for the home folder <paramref name="home"/>: ~/.steam/root resolved (Steam links it to the
    /// folder it runs from), else the Flatpak Steam's, else the Snap Steam's (each keeps its own ~ in its sandbox folder),
    /// else ~/.local/share/Steam.</summary>
    internal static string Root(string home) =>
        Libc.RealPath(Path.Combine(home, ".steam", "root"))
        ?? new[] { Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"), Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam") }
            .FirstOrDefault(Directory.Exists)
        ?? Path.Combine(home, ".local", "share", "Steam");

    /// <summary>The native Steam's folder: <see cref="Root(string)"/> without the Flatpak and Snap Steams.</summary>
    internal static string NativeRoot(string home) =>
        Libc.RealPath(Path.Combine(home, ".steam", "root")) ?? Path.Combine(home, ".local", "share", "Steam");

    public static string NativeRoot() => NativeRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>The sandbox Steam's folder <paramref name="root"/> runs its games in: "Flatpak" or "Snap"; null for the native Steam.</summary>
    public static string? Sandbox(string root, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return root.StartsWith(FlatpakHome(home) + "/", StringComparison.Ordinal) ? "Flatpak"
            : root.StartsWith(Path.Combine(home, "snap", "steam") + "/", StringComparison.Ordinal) ? "Snap" : null;
    }

    static string FlatpakHome(string home) => Path.Combine(home, ".var", "app", "com.valvesoftware.Steam");

    /// <summary>The Flatpak Steam's own folder (~/.var/app/com.valvesoftware.Steam), which its sandbox can't change, when
    /// <paramref name="root"/> is that Steam's; null for any other Steam.</summary>
    internal static string? SandboxHome(string root) =>
        Sandbox(root) == "Flatpak" ? FlatpakHome(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) : null;

    // the Snap Steam's home is a real folder of the user's, ~/snap/steam/common: its paths need no mapping
    private static partial string? LibraryPath(string root, string path) => FlatpakPath(root, path);

    /// <summary><paramref name="path"/> as the Flatpak Steam of <paramref name="root"/> names it, as the host opens it: a
    /// folder of the home folder is in Steam's sandbox folder (~/.var/app/com.valvesoftware.Steam), unless only the home's own
    /// one is there, which Steam was given access to. Any other path, and any other Steam's, as it is.</summary>
    internal static string FlatpakPath(string root, string path, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sandbox = FlatpakHome(home);
        if (!root.StartsWith(sandbox + "/", StringComparison.Ordinal) || path.StartsWith(sandbox + "/", StringComparison.Ordinal)
            || path != home && !path.StartsWith(home + "/", StringComparison.Ordinal)) return path;
        var inside = Path.Join(sandbox, Path.GetRelativePath(home, path));
        return Path.Exists(inside) || !Path.Exists(path) ? inside : path;
    }

    /// <summary>A Steam is installed: <see cref="Root()"/> has a library.</summary>
    public static bool Installed() => Directory.Exists(Path.Combine(Root(), "steamapps"));

    /// <summary>The native Steam is installed: its folder has a library.</summary>
    internal static bool NativeInstalled(string home) => Directory.Exists(Path.Combine(NativeRoot(home), "steamapps"));

    public static bool NativeInstalled() => NativeInstalled(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>The launch options Steam's most recent user set for app <paramref name="app"/> (that user's localconfig.vdf);
    /// null when none are set or Steam's files can't be read.</summary>
    internal static string? LaunchOptions(string root, uint app)
    {
        try
        {
            if (RecentAccount(root) is not { } recent) return null;
            var config = Path.Combine(root, "userdata", recent.ToString(CultureInfo.InvariantCulture), "config", "localconfig.vdf");
            return File.Exists(config) && Small(config) is { } local
                ? Child(KeyValues(local), "UserLocalConfigStore", "Software", "Valve", "Steam", "apps", app.ToString(CultureInfo.InvariantCulture))?.GetValueOrDefault("LaunchOptions") as string
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The app whose launch a process of Steam's reaper runs (ubuntu12_32/reaper SteamLaunch AppId=&lt;app&gt; [Install=1]
    /// -- &lt;the launch's command&gt;), given its command line: the game's, or the install script's Steam runs first. Null for
    /// any other command line.</summary>
    internal static uint? LaunchingApp(IReadOnlyList<string> argv)
    {
        if (argv.Count < 3 || Path.GetFileName(argv[0]) != "reaper" || Path.GetFileName(Path.GetDirectoryName(argv[0])) != "ubuntu12_32") return null;
        var own = argv.TakeWhile(a => a != "--").ToList();
        var launch = own.IndexOf("SteamLaunch");
        if (launch < 1) return null;
        foreach (var a in own.Skip(launch + 1))
            if (a.StartsWith("AppId=", StringComparison.Ordinal) && uint.TryParse(a.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                return id;
        return null;
    }

    /// <summary>The compatdata/&lt;app&gt;/config_info's second line: the fonts folder of the Proton that last ran the
    /// prefix, inside that Proton's folder; null when it doesn't lead to one.</summary>
    internal static string? LastProtonFolder(string data)
    {
        var fonts = ConfigInfo(data, 1);
        if (fonts == null || !Path.IsPathRooted(fonts)) return null;
        // pressure-vessel mounts the host under /run/host, and Proton writes its paths from inside it
        string[] paths = fonts.StartsWith("/run/host/", StringComparison.Ordinal) ? [fonts, fonts["/run/host".Length..]] : [fonts];
        foreach (var path in paths)
        {
            // <proton>/files/share/fonts/ (dist/ in older Protons)
            var dir = Path.TrimEndingDirectorySeparator(path);
            for (var up = 0; up < 4 && dir != null; up++, dir = Path.GetDirectoryName(dir))
                if (File.Exists(Path.Combine(dir, "proton"))) return dir;
        }
        return null;
    }

    /// <summary>The prefix version the Proton in <paramref name="proton"/> writes into config_info and upgrades a prefix
    /// to when it differs (CURRENT_PREFIX_VERSION in its proton script, not the build its version file names); null when
    /// the script doesn't say.</summary>
    internal static string? PrefixVersion(string proton)
    {
        try
        {
            if (Small(Path.Combine(proton, "proton")) is not { } script) return null;
            foreach (var line in script.Split('\n'))
                if (PrefixVersionIn(line) is { } version) return version;
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The build name the version file in <paramref name="proton"/> gives after its time ("1760000000 GE-Proton11-7"):
    /// 1-40 letters, digits and ._+-, else null.</summary>
    internal static string? ProtonBuild(string proton)
    {
        try
        {
            var words = Small(Path.Combine(proton, "version"))?.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            return words is [var time, var name] && time.All(char.IsAsciiDigit) && name.Length <= 40
                && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-') ? name : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The value a line assigns to CURRENT_PREFIX_VERSION from its very start (an indented one is a function's):
    /// in double or single quotes or bare, with spaces around the =, trailing spaces or \r and a # comment after it. Null
    /// for any other line, and for an empty value.</summary>
    static string? PrefixVersionIn(string line)
    {
        const string Name = "CURRENT_PREFIX_VERSION";
        if (!line.StartsWith(Name, StringComparison.Ordinal)) return null;
        var rest = line.AsSpan(Name.Length).TrimStart(" \t");
        if (rest.IsEmpty || rest[0] != '=') return null;
        rest = rest[1..].TrimStart(" \t");
        ReadOnlySpan<char> value, after;
        if (!rest.IsEmpty && rest[0] is '"' or '\'')
        {
            var end = rest[1..].IndexOf(rest[0]);
            if (end < 0) return null;
            value = rest.Slice(1, end);
            after = rest[(end + 2)..];
        }
        else
        {
            var end = rest.IndexOfAny(" \t\r#");
            value = end < 0 ? rest : rest[..end];
            after = end < 0 ? [] : rest[end..];
        }
        after = after.TrimStart(" \t\r");
        return value.IsEmpty || !after.IsEmpty && after[0] != '#' ? null : value.ToString();
    }

    static string? ConfigInfo(string data, int line)
    {
        var path = Path.Combine(data, "config_info");
        try { return File.Exists(path) && Small(path)?.Split('\n').ElementAtOrDefault(line)?.Trim() is { Length: > 0 } l ? l : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The system's compatibilitytools.d folders Steam reads besides its own, and STEAM_EXTRA_COMPAT_TOOLS_PATHS's.</summary>
    internal static IEnumerable<string> SystemToolFolders() =>
        ["/usr/share/steam/compatibilitytools.d", "/usr/local/share/steam/compatibilitytools.d",
            .. (Environment.GetEnvironmentVariable("STEAM_EXTRA_COMPAT_TOOLS_PATHS") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>The folder of the compatibility tool Steam names <paramref name="name"/> (CompatToolMapping): a
    /// compatibilitytool.vdf's in Steam's compatibilitytools.d or <paramref name="systemFolders"/> (in a tool's own folder, or
    /// in compatibilitytools.d itself, by its install_path), else Valve's tool of that name or alias, a Steam app (appinfo's
    /// Steam Play manifests). Null when none is installed.</summary>
    internal static string? ToolFolder(string root, string name, IEnumerable<string>? systemFolders = null)
    {
        foreach (var tools in new[] { Path.Combine(root, "compatibilitytools.d") }.Concat(systemFolders ?? SystemToolFolders()))
            try
            {
                if (!Directory.Exists(tools)) continue;
                foreach (var vdf in new[] { Path.Combine(tools, "compatibilitytool.vdf") }.Concat(Directory.EnumerateDirectories(tools).Order(StringComparer.Ordinal)
                             .Select(d => Path.Combine(d, "compatibilitytool.vdf"))))
                    if (File.Exists(vdf) && Small(vdf) is { } text && Child(KeyValues(text), "compatibilitytools", "compat_tools") is { } all
                        && Child(all, name)?.GetValueOrDefault("install_path") is string { Length: > 0 } install
                        && Path.GetFullPath(Path.Combine(Path.GetDirectoryName(vdf)!, install)) is var folder && Directory.Exists(folder))
                        return Path.TrimEndingDirectorySeparator(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return ValveTool(root, name) is { } app ? AppFolder(root, app) : null;
    }

    const uint SteamPlayManifests = 891390;

    /// <summary>The app id of Valve's compatibility tool <paramref name="name"/> (a key of appinfo's Steam Play manifests, or
    /// one of its aliases); null when appinfo doesn't list it.</summary>
    static uint? ValveTool(string root, string name)
    {
        if (Apps(Path.Combine(root, "appcache", "appinfo.vdf"), new HashSet<uint> { SteamPlayManifests }) is not { } apps
            || Child(apps.GetValueOrDefault(SteamPlayManifests), "extended", "compat_tools") is not { } tools) return null;
        uint? Id(object? tool) => tool is Dictionary<string, object> t && t.GetValueOrDefault("appid") is int id and > 0 ? (uint)id : null;
        return Id(tools.GetValueOrDefault(name))
            ?? tools.Values.Where(t => t is Dictionary<string, object> d && d.GetValueOrDefault("aliases") is string aliases
                && aliases.Split(',').Contains(name, StringComparer.OrdinalIgnoreCase)).Select(Id).FirstOrDefault(i => i != null);
    }

    /// <summary>The install folder of Steam app <paramref name="app"/> in any of Steam's libraries, when its manifest says it's
    /// fully installed; null otherwise.</summary>
    internal static string? AppFolder(string root, uint app)
    {
        foreach (var lib in Libraries(root))
            try
            {
                var acf = Path.Combine(lib, "steamapps", $"appmanifest_{app}.acf");
                if (!File.Exists(acf) || Small(acf) is not { } text) continue;
                if (!int.TryParse(Values(text, "StateFlags").FirstOrDefault(), out var flags) || (flags & 4) == 0) continue;
                if (Values(text, "installdir").FirstOrDefault() is { Length: > 0 } dir && Path.Combine(lib, "steamapps", "common", dir) is var folder && Directory.Exists(folder))
                    return folder;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>The Steam Linux Runtime Proton <paramref name="proton"/> runs in (its toolmanifest.vdf's require_tool_appid):
    /// the folder holding its _v2-entry-point; null with the reason when there is none.</summary>
    internal static string? RuntimeOf(string root, string proton, out string? why)
    {
        why = null;
        string? id = null;
        try
        {
            var manifest = Path.Combine(proton, "toolmanifest.vdf");
            if (File.Exists(manifest) && Small(manifest) is { } text) id = Child(KeyValues(text), "manifest")?.GetValueOrDefault("require_tool_appid") as string;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (!uint.TryParse(id, out var app))
        {
            why = $"the Proton in {proton} doesn't run in Steam's Linux runtime (Proton 5.13 and later do)";
            return null;
        }
        if (AppFolder(root, app) is not { } runtime)
        {
            why = $"Steam Linux Runtime (app {app}), which its Proton needs, isn't installed: start the game once from Steam";
            return null;
        }
        if (File.Exists(Path.Combine(runtime, EntryPoint))) return runtime;
        why = $"{runtime} has no {EntryPoint}";
        return null;
    }

    /// <summary>The runtime's launcher, as Steam runs a tool in it.</summary>
    public const string EntryPoint = "_v2-entry-point";
}
