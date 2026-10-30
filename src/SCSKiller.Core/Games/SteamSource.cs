using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.Games;

/// <summary>Installed Steam games from libraryfolders.vdf + appmanifest_*.acf. Tools and applications installed like
/// games (SteamVR, Lossless Scaling, redistributables, runtimes) are left out by Steam's own app type, from
/// appcache/appinfo.vdf; if that file is missing or unreadable everything installed is kept.</summary>
public sealed partial class SteamSource(string? steamRoot = null) : IGameSource
{
    public Store Store => Store.Steam;

    (string Stamp, HashSet<uint> Asked, Dictionary<uint, string> Types)? _types;   // appinfo.vdf is only re-read when it changes

    readonly System.Collections.Concurrent.ConcurrentDictionary<uint, byte> _noExeLogged = new();   // once per app and process, not every scan

    /// <summary>Games listed before, by id: a game's exe is reused while its build and install folder are the same and the
    /// exe is there, instead of looking through its folders again.</summary>
    public IReadOnlyDictionary<string, Game>? Known { get; set; }

    /// <summary>A library or game folder that couldn't be read (a broken junction): skipped, the rest still listed.</summary>
    public IProgress<string>? Log { get; set; }

    public IReadOnlyList<Game> Discover()
    {
        var root = steamRoot ?? Root();
        var installed = new List<(uint Id, Game Game)>();
        var noExe = new List<(uint Id, string Name, string Install)>();
        foreach (var lib in Libraries(root))
            try
            {
                var apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (var acf in Manifests(apps))
                    try
                    {
                        var text = File.ReadAllText(acf);
                        string? V(string key) => Values(text, key).FirstOrDefault();
                        if (!uint.TryParse(V("appid"), out var id) || V("installdir") is not { } dir) continue;
                        if (!int.TryParse(V("StateFlags"), out var flags) || (flags & 4) == 0) continue;   // 4 = fully installed
                        var install = Path.Combine(apps, "common", dir);
                        var build = V("buildid");
                        if (Collections.Expand(new Game($"steam:{id}", V("name") ?? dir, Store.Steam, install, "", build)) is { Count: > 0 } games)
                        {
                            installed.AddRange(games.Select(g => (id, g)));
                            continue;
                        }
                        var exe = Known?.GetValueOrDefault($"steam:{id}") is { Version: { } was } k && was == build && k.InstallDir.Equals(install, StringComparison.OrdinalIgnoreCase)
                            && File.Exists(k.ExePath) && !GameFiles.NotTheGameExe(install, k.ExePath) ? k.ExePath : GameFiles.FindExe(install);
                        if (exe == null)
                        {
                            noExe.Add((id, V("name") ?? dir, install));
                            continue;
                        }
                        installed.Add((id, new Game($"steam:{id}", V("name") ?? dir, Store.Steam, install, exe, build)));
                    }
                    // one unreadable game never drops the store, and keeps its records: CleanUninstalled needs its manifest and exe gone
                    catch (Exception e) when (e is not OperationCanceledException) { Log?.Report($"Steam: {acf} skipped: {e.GetType().Name}: {e.Message}"); }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"Steam: library {lib} skipped: {e.Message}"); }
        var types = TypesOf(Path.Combine(root, "appcache", "appinfo.vdf"), installed.Select(a => a.Id).Concat(noExe.Select(a => a.Id)).ToHashSet());
        foreach (var (id, name, install) in noExe)   // a redistributable or soundtrack has no exe by design
            if ((types?.TryGetValue(id, out var t) != true || IsGameType(t!)) && _noExeLogged.TryAdd(id, 0))
                Log?.Report($"Steam: {name} ({id}) left out: no game exe found in {install}");
        return installed.Where(a => types?.TryGetValue(a.Id, out var t) != true || IsGameType(t!)).Select(a => a.Game).ToList();
    }

    /// <summary>The Steam app id in a game id: "steam:&lt;app&gt;", or "steam:&lt;app&gt;:&lt;game&gt;" for a game of a
    /// collection (<see cref="Collections"/>); null for any other id.</summary>
    public static uint? AppId(string gameId) =>
        gameId.StartsWith("steam:", StringComparison.Ordinal) && gameId.AsSpan(6) is var rest
        && uint.TryParse(rest[..(rest.IndexOf(':') is var i and >= 0 ? i : rest.Length)], System.Globalization.NumberStyles.None, null, out var id) ? id : null;

    /// <summary>Steam's app types that are played: "Game" and "Demo" (not Tool, Application, Config, DLC, Music, Video...).</summary>
    public static bool IsGameType(string type) => type.Equals("game", StringComparison.OrdinalIgnoreCase) || type.Equals("demo", StringComparison.OrdinalIgnoreCase);

    Dictionary<uint, string>? TypesOf(string appinfo, HashSet<uint> ids)
    {
        var f = new FileInfo(appinfo);
        var stamp = f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "";
        lock (_gate)
        {
            if (_types is { } c && c.Stamp == stamp && ids.IsSubsetOf(c.Asked)) return c.Types;
            var types = AppTypes(appinfo, ids);
            _types = types == null ? null : (stamp, ids, types);
            return types;
        }
    }

    readonly object _gate = new();

    /// <summary>common/type of the given app ids from appinfo.vdf; see <see cref="Apps"/>.</summary>
    public static Dictionary<uint, string>? AppTypes(string path, IReadOnlySet<uint> ids)
    {
        if (Apps(path, ids) is not { } apps) return null;
        var types = new Dictionary<uint, string>();
        foreach (var (id, app) in apps)
            if (app.GetValueOrDefault("common") is Dictionary<string, object> common && common.GetValueOrDefault("type") is string type) types[id] = type;
        return types;
    }

    /// <summary>Steam's library folders (libraryfolders.vdf; Steam's own folder when it lists none), full paths.</summary>
    public static IReadOnlyList<string> Libraries(string root)
    {
        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        var libraries = File.Exists(vdf) ? Values(File.ReadAllText(vdf), "path").Select(p => LibraryPath(root, p)).OfType<string>().ToList() : [];
        if (libraries.Count == 0) libraries.Add(root);
        return libraries.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A library path of libraryfolders.vdf as this OS opens it: the Flatpak Steam's, under the home folder it sees,
    /// in its sandbox folder. Null when it names no folder here.</summary>
    private static partial string? LibraryPath(string root, string path);

    /// <summary>The Steam app whose install folder holds <paramref name="path"/> (a game's install folder or exe). A path in
    /// a steamapps\common folder is in that library whether or not libraryfolders.vdf lists it (as the proxy decides it): the
    /// appmanifest beside it that names its folder gives the app. Elsewhere in a library Steam lists: no app. Null id with
    /// inLibrary: Steam's, but no manifest names its folder.</summary>
    public static (uint? Id, bool InLibrary) AppAt(string root, string path)
    {
        var full = Path.GetFullPath(path);
        var at = full.IndexOf(CommonPart, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return (null, Libraries(root).Any(lib => full.StartsWith(Path.TrimEndingDirectorySeparator(lib) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        var apps = full[..(at + AppsPart.Length)];
        var folder = full[(at + CommonPart.Length)..].Split(Path.DirectorySeparatorChar)[0];
        foreach (var acf in Directory.Exists(apps) ? Manifests(apps) : [])
        {
            var text = File.ReadAllText(acf);
            if (Values(text, "installdir").FirstOrDefault() is { } dir && dir.Equals(folder, StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(Values(text, "appid").FirstOrDefault(), out var id)) return (id, true);
        }
        return (null, true);
    }

    // Windows lists an NTFS folder by name, the order a library's games are found in: elsewhere they're sorted so
    static IEnumerable<string> Manifests(string apps) => OperatingSystem.IsWindows() ? Directory.EnumerateFiles(apps, "appmanifest_*.acf")
        : Directory.EnumerateFiles(apps, "appmanifest_*.acf").Order(GameFiles.PathOrder);

    /// <summary>"\steamapps" and "\steamapps\common\" with this system's separator.</summary>
    internal static readonly string AppsPart = Path.DirectorySeparatorChar + "steamapps", CommonPart = $"{AppsPart}{Path.DirectorySeparatorChar}common{Path.DirectorySeparatorChar}";

    /// <summary>The Proton prefix Steam runs the game in (<see cref="ProtonPrefix"/>), the app by the game's "steam:" id, else
    /// by the manifest that names its folder (<see cref="AppAt"/>). Null when there is none: the game isn't in a
    /// steamapps/common folder, no app is known, or Steam never made its prefix (it never ran under Proton). Only read.</summary>
    /// <param name="steamRoot">Steam's folder, for config/config.vdf (default: Steam's own)</param>
    public static ProtonPrefix? PrefixOf(Game game, string? steamRoot = null)
    {
        try
        {
            var root = steamRoot ?? Root();
            if (CompatData(game, root) is not { } compat) return null;
            var (app, data) = compat;
            var pfx = Path.Combine(data, "pfx");
            return Directory.Exists(pfx) ? new ProtonPrefix(app, pfx, CompatTool(root, app), LastProton(data)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary><see cref="PrefixOf"/>'s folder whether Steam made it yet or not (it does at the game's first start under
    /// Proton); null when the game isn't in a steamapps/common folder or no app is known. Only read.</summary>
    internal static string? PrefixFolder(Game game, string? steamRoot = null)
    {
        try { return CompatData(game, steamRoot) is { } compat ? Path.Combine(compat.Data, "pfx") : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    // the app and its compatdata folder in the library that holds the game; Steam's folder only for an app found by its manifest
    static (uint App, string Data)? CompatData(Game game, string? steamRoot)
    {
        var full = Path.GetFullPath(game.InstallDir);
        var at = full.IndexOf(CommonPart, StringComparison.OrdinalIgnoreCase);
        if (at < 0 || (AppId(game.Id) ?? AppAt(steamRoot ?? Root(), full).Id) is not { } app) return null;
        return (app, Path.Combine(full[..(at + AppsPart.Length)], "compatdata", app.ToString()));
    }

    /// <summary>config/config.vdf's CompatToolMapping: the app's tool, else the one Steam Play runs every other title with
    /// (the "0" entry) unless <paramref name="orDefault"/> is false; null without either, or when the file can't be read.</summary>
    internal static string? CompatTool(string root, uint app, bool orDefault = true)
    {
        Dictionary<string, object>? mapping;
        try
        {
            var config = Path.Combine(root, "config", "config.vdf");
            mapping = File.Exists(config) && Small(config) is { } text ? Child(KeyValues(text), "InstallConfigStore", "Software", "Valve", "Steam", "CompatToolMapping") : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        string? Name(string id) => Child(mapping, id)?.GetValueOrDefault("name") is string { Length: > 0 } name ? name : null;   // "": no tool set
        return Name(app.ToString()) ?? (orDefault ? Name("0") : null);
    }

    /// <summary>compatdata/&lt;app&gt;/config_info's first line: the Proton that last ran the prefix.</summary>
    static string? LastProton(string data)
    {
        var info = Path.Combine(data, "config_info");
        try { return File.Exists(info) && Small(info)?.Split('\n')[0].Trim() is { Length: > 0 } version ? version : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The text of a regular file of at most 16 MB; null for a bigger one, a FIFO or a device.</summary>
    internal static string? Small(string path)
    {
        using var f = GameFiles.OpenRegular(path);
        if (f == null) return null;
        var length = f.Length;
        if (length > 16 << 20) return null;
        var bytes = new byte[length];
        var n = f.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        using var text = new StreamReader(new MemoryStream(bytes, 0, n));   // a BOM read as File.ReadAllText reads it
        return text.ReadToEnd();
    }

    /// <summary>The account id of Steam's most recent user (config/loginusers.vdf's MostRecent, else its latest sign-in): the
    /// name of its userdata folder, a SteamID64's low 32 bits. Null when Steam's files don't say.</summary>
    internal static uint? RecentAccount(string root)
    {
        var logins = Path.Combine(root, "config", "loginusers.vdf");
        if (!File.Exists(logins) || Small(logins) is not { } text || Child(KeyValues(text), "users") is not { } users) return null;
        // Steam doesn't always write MostRecent: the latest sign-in then
        static string? Value(object user, string key) => (user as Dictionary<string, object>)?.GetValueOrDefault(key) as string;
        var recent = users.Where(u => u.Value is Dictionary<string, object> && ulong.TryParse(u.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .OrderByDescending(u => Value(u.Value, "MostRecent") == "1")
            .ThenByDescending(u => long.TryParse(Value(u.Value, "Timestamp"), NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? t : 0)
            .Select(u => ulong.Parse(u.Key, CultureInfo.InvariantCulture)).FirstOrDefault();
        return recent == 0 ? null : (uint)(recent & 0xFFFFFFFF);
    }

    static Dictionary<string, object>? Child(Dictionary<string, object>? o, params string[] path)
    {
        foreach (var key in path) o = o?.GetValueOrDefault(key) as Dictionary<string, object>;
        return o;
    }

    /// <summary>A text KeyValues file (VDF) as nested objects of strings, keys case-insensitive, the first of a repeated key
    /// kept, quoted text unescaped as <see cref="Values"/> does; conditions ([$WIN32]) and // comments skipped, a key without
    /// a value dropped. Nothing past an object nested deeper than 64 is read.</summary>
    internal static Dictionary<string, object> KeyValues(string vdf)
    {
        var at = 0;
        (string Text, bool Brace)? Next()
        {
            while (at < vdf.Length)
                if (char.IsWhiteSpace(vdf[at])) at++;
                else if (vdf[at] == '/' && at + 1 < vdf.Length && vdf[at + 1] == '/') at = vdf.IndexOf('\n', at) is var n and >= 0 ? n : vdf.Length;
                else if (vdf[at] == '[') at = vdf.IndexOf(']', at) is var e and >= 0 ? e + 1 : vdf.Length;
                else break;
            if (at >= vdf.Length) return null;
            if (vdf[at] is '{' or '}') return (vdf[at++].ToString(), true);
            var text = new StringBuilder();
            if (vdf[at] == '"')
            {
                for (at++; at < vdf.Length && vdf[at] != '"'; at++) text.Append(vdf[at] == '\\' && at + 1 < vdf.Length ? vdf[++at] : vdf[at]);
                at++;
            }
            else
                while (at < vdf.Length && !char.IsWhiteSpace(vdf[at]) && vdf[at] is not ('{' or '}' or '"')) text.Append(vdf[at++]);
            return (text.ToString(), false);
        }
        Dictionary<string, object> Object(int depth)
        {
            var o = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (Next() is ({ } key, false) && Next() is ({ } value, var brace) && !(brace && value == "}"))
                if (!brace) o.TryAdd(key, value);
                else if (depth < 64) o.TryAdd(key, Object(depth + 1));
                else at = vdf.Length;
            return o;
        }
        return Object(0);
    }

    /// <summary>Steam's folder: the one the user's Steam runs from, else its default install folder.</summary>
    public static partial string Root();

    /// <summary>Steam's appinfo.vdf for the Steam folder <paramref name="root"/>. Under Wine (Proton) Steam's data is the one
    /// of the Steam that started the game (STEAM_COMPAT_CLIENT_INSTALL_PATH, through Wine's drives), even when a Steam in the
    /// prefix has its own copy, which can be stale. Never the game's library: it can be an abandoned Steam's folder. The
    /// variable empty (umu leaves it so): the root's, which Proton's stand-in Steam doesn't have. "" when the variable has no
    /// Windows path: nothing reads, so every Steam game is VAC.</summary>
    public static string AppInfoPath(string root) =>
        AppInfoPath(root, Vendors.Wine.Running, Environment.GetEnvironmentVariable("STEAM_COMPAT_CLIENT_INSTALL_PATH"), Vendors.Wine.DosPath);

    internal static string AppInfoPath(string root, bool wine, string? client, Func<string, string?> dos) =>
        !wine || string.IsNullOrEmpty(client) ? Path.Combine(root, "appcache", "appinfo.vdf")
        : dos(client) is { Length: > 0 } steam ? Path.Combine(steam, "appcache", "appinfo.vdf") : "";

    /// <summary>The appinfo object (common, config...) of the given app ids (null: all) from appinfo.vdf (binary KeyValues, v28 or v29),
    /// streamed: other apps are skipped by their size. Null when the file is missing or not
    /// understood; an app that is absent or whose entry doesn't parse is just missing from the result.</summary>
    public static Dictionary<uint, Dictionary<string, object>>? Apps(string path, IReadOnlySet<uint>? ids) => Apps(path, ids, null);

    /// <param name="states">filled with each read app's info state (the entry's first field: 2 = Steam has its info, 1 = none)</param>
    /// <param name="failed">called with the reason when the result is null</param>
    public static Dictionary<uint, Dictionary<string, object>>? Apps(string path, IReadOnlySet<uint>? ids, Dictionary<uint, uint>? states, Action<string>? failed = null)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var r = new BinaryReader(f);
            var stamp = $"{Path.GetFullPath(path)}|{f.Length}|{File.GetLastWriteTimeUtc(f.SafeFileHandle).Ticks}";
            var apps = new Dictionary<uint, Dictionary<string, object>>();
            void Add(uint id, byte[] bytes, string[]? keys)
            {
                var entry = new BinaryReader(new MemoryStream(bytes, writable: false));
                if (states != null) states[id] = entry.ReadUInt32();
                entry.BaseStream.Position = 4 + 4 + 8 + 20 + 4 + 20;   // infoState, lastUpdated, picsToken, sha1, changeNumber, binary sha1
                try
                {
                    var kv = Object(entry, keys);
                    apps[id] = kv.GetValueOrDefault("appinfo") as Dictionary<string, object> ?? kv;
                }
                catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IndexOutOfRangeException) { }
            }
            lock (indexGate)
                if (indexed is { } known && known.Stamp == stamp)
                {
                    foreach (var id in ids ?? (IEnumerable<uint>)known.Index.Keys)
                        if (known.Index.TryGetValue(id, out var at))
                        {
                            f.Position = at.Offset;
                            Add(id, r.ReadBytes((int)at.Size), known.Keys);
                        }
                    return apps;
                }
            var magic = r.ReadUInt32();
            r.ReadUInt32();   // universe
            string[]? keys = null;
            if (magic == 0x07564429)   // v29: keys are indexes into a string table at the end
            {
                var table = r.ReadInt64();
                var start = f.Position;
                f.Position = table;
                keys = new string[r.ReadUInt32()];
                for (int i = 0; i < keys.Length; i++) keys[i] = CString(r);
                f.Position = start;
            }
            else if (magic != 0x07564428)
            {
                failed?.Invoke($"unknown format 0x{magic:x8}");
                return null;
            }
            var index = new Dictionary<uint, (long, uint)>();
            for (uint id; (id = r.ReadUInt32()) != 0;)
            {
                var size = r.ReadUInt32();
                index[id] = (f.Position, size);
                if (ids?.Contains(id) == false) { f.Seek(size, SeekOrigin.Current); continue; }
                Add(id, r.ReadBytes((int)size), keys);
            }
            lock (indexGate) indexed = (stamp, index, keys);
            return apps;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            failed?.Invoke(e.Message);
            return null;   // EndOfStreamException is an IOException
        }
    }

    // the file last read whole: where each app's entry is, so that another app is read alone while the file stays the same
    static (string Stamp, Dictionary<uint, (long Offset, uint Size)> Index, string[]? Keys)? indexed;
    static readonly Lock indexGate = new();

    /// <summary>A binary KeyValues object up to its end marker: nested objects, strings and 32-bit ints kept, other numbers skipped.</summary>
    static Dictionary<string, object> Object(BinaryReader r, string[]? keys, int depth = 0)
    {
        if (depth > 64) throw new InvalidDataException("KeyValues nested deeper than 64");
        var o = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        for (byte t; (t = r.ReadByte()) != 8;)
        {
            var key = keys == null ? CString(r) : keys[r.ReadInt32()];
            switch (t)
            {
                case 0: o[key] = Object(r, keys, depth + 1); break;
                case 1: o[key] = CString(r); break;
                case 2: o[key] = r.ReadInt32(); break;
                case 3 or 4 or 6: r.ReadInt32(); break;          // float, pointer, color
                case 7 or 10: r.ReadInt64(); break;              // uint64, int64
                default: throw new InvalidDataException($"KeyValues type {t}");
            }
        }
        return o;
    }

    static string CString(BinaryReader r)
    {
        var bytes = new List<byte>();
        for (byte b; (b = r.ReadByte()) != 0;) bytes.Add(b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>Every value of <paramref name="key"/> in a text KeyValues (VDF/ACF) file, unescaped.</summary>
    public static IEnumerable<string> Values(string vdf, string key) =>
        Regex.Matches(vdf, $@"""{Regex.Escape(key)}""\s+""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase)
            .Select(m => Regex.Replace(m.Groups[1].Value, @"\\(.)", "$1"));
}

/// <summary>A Steam game's Wine prefix under Proton (<see cref="SteamSource.PrefixOf"/>). <paramref name="Folder"/>:
/// steamapps/compatdata/&lt;app&gt;/pfx in the game's library. <paramref name="Tool"/>: the compatibility tool Steam runs the
/// game with, by its internal name (<see cref="SteamSource.CompatTool"/>). <paramref name="LastRun"/>: the Proton that last
/// ran the prefix, as its config_info names it. Each null when Steam's files don't say.</summary>
public sealed record ProtonPrefix(uint AppId, string Folder, string? Tool, string? LastRun);
