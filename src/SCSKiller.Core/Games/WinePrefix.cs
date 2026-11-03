using System.Text;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.Games;

/// <summary>A Wine prefix by its folder <paramref name="Dir"/> (system.reg, user.reg, drive_c, dosdevices), read from its files
/// only: a Proton prefix (compatdata's pfx), a launcher's on Linux (Heroic, Lutris, Faugus), a CrossOver bottle on macOS.</summary>
public sealed record WinePrefix(string Dir)
{
    public string DriveC => Path.Combine(Dir, "drive_c");

    /// <summary>The folder of the drive_c nearest above <paramref name="path"/>; null when none is.</summary>
    public static string? Holding(string path)
    {
        for (var d = Path.GetDirectoryName(Path.GetFullPath(path)); d != null; d = Path.GetDirectoryName(d))
            if (Path.GetFileName(d) == "drive_c") return Path.GetDirectoryName(d);
        return null;
    }

    /// <summary>A prefix Wine has set up: its system.reg is there.</summary>
    public bool Made => File.Exists(Path.Combine(Dir, "system.reg"));

    /// <summary><paramref name="windows"/> ("C:\Program Files\...") as a path of the host, through the prefix's drives; null
    /// when it isn't a full path or its drive isn't there.</summary>
    public string? UnixPath(string windows)
    {
        var p = windows.Trim().Replace('/', '\\');
        if (p.Length < 2 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || p.Length > 2 && p[2] != '\\') return null;
        var drive = Path.Combine(Dir, "dosdevices", char.ToLowerInvariant(p[0]) + ":");
        if (!Directory.Exists(drive)) return null;
        var root = GameFiles.FinalPath(drive) ?? drive;
        var rest = p.Length > 3 ? p[3..].TrimEnd('\\') : "";
        return rest.Length == 0 ? root : GameFiles.Find(root, rest);
    }

    /// <summary>The folders the prefix's drives name, their links resolved.</summary>
    public IEnumerable<string> Drives()
    {
        var devices = Path.Combine(Dir, "dosdevices");
        return !Directory.Exists(devices) ? [] : Directory.EnumerateFileSystemEntries(devices, "?:").Select(GameFiles.FinalPath).OfType<string>();
    }

    /// <summary>A string value of the prefix's registry as Wine keeps it on disk: HKEY_LOCAL_MACHINE's in system.reg,
    /// HKEY_CURRENT_USER's in user.reg; null when it isn't there or isn't a string. What Wine hasn't saved yet (it writes
    /// the files a few seconds after a change, and when the prefix's wineserver stops) isn't seen.</summary>
    public string? Registry(string key, string name) => Hive(key) is (string file, var sub) ? WineReg.Value(Path.Combine(Dir, file), sub, name) : null;

    /// <summary>The subkeys of <paramref name="key"/> (as <see cref="Registry"/> names it), each by its name.</summary>
    public IReadOnlyList<string> SubKeys(string key) => Hive(key) is (string file, var sub) ? WineReg.SubKeys(Path.Combine(Dir, file), sub) : [];

    static (string? File, string Sub) Hive(string key) =>
        key.StartsWith(@"HKEY_LOCAL_MACHINE\", StringComparison.OrdinalIgnoreCase) ? ("system.reg", key[19..])
        : key.StartsWith(@"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase) ? ("user.reg", key[18..]) : (null, key);
}

/// <summary>Wine's registry files (system.reg, user.reg): "[key\\path] stamp" sections of "name"=value lines, strings
/// quoted with Wine's escapes. Only string values are read.</summary>
static class WineReg
{
    public static string? Value(string file, string key, string name)
    {
        var want = Escape(key);
        var inKey = false;
        foreach (var line in Lines(file))
        {
            if (line.StartsWith('[')) inKey = Section(line)?.Equals(want, StringComparison.OrdinalIgnoreCase) == true;
            else if (inKey && Pair(line) is var (n, v) && n.Equals(name, StringComparison.OrdinalIgnoreCase)) return v;
        }
        return null;
    }

    public static IReadOnlyList<string> SubKeys(string file, string key)
    {
        var prefix = Escape(key) + @"\\";
        var subs = new List<string>();
        foreach (var line in Lines(file))
            if (line.StartsWith('[') && Section(line) is { } s && s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && s[prefix.Length..].Split(@"\\")[0] is { Length: > 0 } sub && !subs.Contains(Unescape(sub), StringComparer.OrdinalIgnoreCase))
                subs.Add(Unescape(sub));
        return subs;
    }

    static IEnumerable<string> Lines(string file)
    {
        try { return File.ReadAllLines(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    // [Software\\Valve\\Steam] 1713946301: the key as written, a backslash doubled
    static string? Section(string line) => line.LastIndexOf(']') is var end and > 0 ? line[1..end] : null;

    static string Escape(string key) => key.Replace(@"\", @"\\");

    static (string Name, string Value)? Pair(string line)
    {
        var m = Regex.Match(line, @"^""((?:[^""\\]|\\.)*)""=""((?:[^""\\]|\\.)*)""\s*$");
        return m.Success ? (Unescape(m.Groups[1].Value), Unescape(m.Groups[2].Value)) : null;
    }

    static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var b = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 == s.Length) { b.Append(s[i]); continue; }
            var c = s[++i];
            if (c == 'x' && Regex.Match(s[(i + 1)..], "^[0-9a-fA-F]{1,4}") is { Success: true } hex)
            {
                b.Append((char)Convert.ToInt32(hex.Value, 16));
                i += hex.Length;
            }
            else b.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0', _ => c });
        }
        return b.ToString();
    }
}

/// <summary>How a launcher other than Steam runs a Windows game (<see cref="HeroicSource"/>, <see cref="LutrisSource"/>): in the
/// Wine prefix <paramref name="Prefix"/>, the folder it sets WINEPREFIX to, on <paramref name="Bin"/>: a Wine build's wine,
/// or with <paramref name="Proton"/> a Proton build's folder, which takes the folder as its compat data (the prefix its pfx,
/// a link to the folder itself under umu). <paramref name="CacheHome"/>: the XDG_CACHE_HOME the game runs with (a
/// Flatpak's), null for the user's own. <paramref name="Env"/>: the variables the launcher's settings give the game, a later
/// one of a name over an earlier. <paramref name="Umu"/>: a Proton build runs under umu's Steam Linux Runtime, else the
/// launcher runs its proton script itself (Heroic without umu). <paramref name="Args"/>: the arguments the launcher's settings
/// give the game's exe (a -dx11 or -vulkan), as Steam's launch options do (<see cref="Unreal.UnrealRhi.LaunchOptions(Game)"/>).</summary>
public sealed record LauncherPrefix(string Launcher, string Prefix, string Bin, bool Proton, string? CacheHome, IReadOnlyList<(string Name, string Value)>? Env = null,
    bool Umu = true, string? Args = null)
{
    /// <summary>A game Heroic, Lutris or Faugus lists, or one added by hand: Linux's alone, where epic: and gog: ids are Heroic's.</summary>
    public static bool Is(Game g) => OperatingSystem.IsLinux() && (g.Store == Store.Manual || HeroicSource.RunnerOf(g.Id) != null || LutrisSource.Is(g.Id) || FaugusSource.Is(g.Id));

    /// <summary>The prefix the game's launcher runs it in, read now; null with the reason when it can't be told.</summary>
    public static LauncherPrefix? Of(Game g, out string? why)
    {
        if (Is(g)) return g.Store == Store.Manual ? Added(g, out why) : HeroicSource.RunnerOf(g.Id) != null ? HeroicSource.PrefixOf(g, out why)
            : FaugusSource.Is(g.Id) ? FaugusSource.PrefixOf(g, out why) : LutrisSource.PrefixOf(g, out why);
        why = "it isn't a game of Heroic, Lutris or Faugus";
        return null;
    }

    /// <summary>A game added by hand runs in the Wine prefix that holds its exe (<see cref="WinePrefix.Holding"/>), on the
    /// Proton its compat data's config_info names; null with the reason for any other prefix, or none.</summary>
    public static LauncherPrefix? Added(Game g, out string? why) => GameFiles.AddedPrefix(g, out why);

    /// <summary>Linux refuses an exe outside a Wine prefix: nothing would tell where or on what it runs.</summary>
    public const string OutsidePrefix = "add it to Steam as a non-Steam game, or pick an exe inside its Wine prefix";

    /// <summary>The Wine prefix's own folder.</summary>
    public string WineDir => Proton ? Path.Combine(Prefix, "pfx") : Prefix;

    /// <summary>What a launcher game in <paramref name="prefix"/> starts with that can name it: <paramref name="env"/> (the
    /// launcher's settings), a Wine's WINEPREFIX, or a Proton's STEAM_COMPAT_DATA_PATH and the WINEPREFIX Proton makes of it
    /// (its pfx/), under umu the prefix's path on disk too, and unless <paramref name="env"/> sets it STEAM_COMPAT_INSTALL_PATH:
    /// <paramref name="install"/> (the launcher's own), else umu's, the exe's folder. The prefix as written and without a
    /// trailing /, for a twin check (<see cref="HeroicSource.Twin"/>) that would rather find one too many.</summary>
    public static List<(string Name, string Value)> Exports(string prefix, bool proton, bool umu, string? exe, string? install, IReadOnlyList<(string Name, string Value)> env)
    {
        List<(string Name, string Value)> list = [.. env];
        List<string> spellings = [prefix, Path.TrimEndingDirectorySeparator(prefix)];
        if (proton && umu && GameFiles.FinalPath(prefix) is { } real) spellings.Add(real);   // umu resolves WINEPREFIX before it hands it on
        foreach (var p in spellings.Distinct())
            if (proton) list.AddRange([("STEAM_COMPAT_DATA_PATH", p), ("WINEPREFIX", p + "/pfx/")]);
            else list.Add(("WINEPREFIX", p));
        if (proton && !env.Any(e => e.Name == "STEAM_COMPAT_INSTALL_PATH") && (install ?? (umu && exe != null ? HeroicSource.PathlibParent(exe) : null)) is { } folder)
            list.Add(("STEAM_COMPAT_INSTALL_PATH", folder));
        return list;
    }
}
