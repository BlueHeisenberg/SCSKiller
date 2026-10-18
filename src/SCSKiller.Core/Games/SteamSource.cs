using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

/// <summary>Installed Steam games from libraryfolders.vdf + appmanifest_*.acf. Tools and applications installed like
/// games (SteamVR, Lossless Scaling, redistributables, runtimes) are left out by Steam's own app type, from
/// appcache/appinfo.vdf; if that file is missing or unreadable everything installed is kept.</summary>
public sealed class SteamSource(string? steamRoot = null) : IGameSource
{
    public Store Store => Store.Steam;

    (string Stamp, HashSet<uint> Asked, Dictionary<uint, string> Types)? _types;   // appinfo.vdf is only re-read when it changes

    /// <summary>Games listed before, by id: a game's exe is reused while its build and install folder are the same and the
    /// exe is there, instead of looking through its folders again.</summary>
    public IReadOnlyDictionary<string, Game>? Known { get; set; }

    /// <summary>A library or game folder that couldn't be read (a broken junction): skipped, the rest still listed.</summary>
    public IProgress<string>? Log { get; set; }

    public IReadOnlyList<Game> Discover()
    {
        var root = steamRoot ?? Root();
        var installed = new List<(uint Id, Game Game)>();
        foreach (var lib in Libraries(root))
            try
            {
                var apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
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
                        if (exe == null) continue;
                        installed.Add((id, new Game($"steam:{id}", V("name") ?? dir, Store.Steam, install, exe, build)));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"Steam: {acf} skipped: {e.Message}"); }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"Steam: library {lib} skipped: {e.Message}"); }
        var types = TypesOf(Path.Combine(root, "appcache", "appinfo.vdf"), installed.Select(a => a.Id).ToHashSet());
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
        var libraries = File.Exists(vdf) ? Values(File.ReadAllText(vdf), "path").ToList() : [];
        if (libraries.Count == 0) libraries.Add(root);
        return libraries.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The Steam app whose install folder holds <paramref name="path"/> (a game's install folder or exe). A path in
    /// a steamapps\common folder is in that library whether or not libraryfolders.vdf lists it (as the proxy decides it): the
    /// appmanifest beside it that names its folder gives the app. Elsewhere in a library Steam lists: no app. Null id with
    /// inLibrary: Steam's, but no manifest names its folder.</summary>
    public static (uint? Id, bool InLibrary) AppAt(string root, string path)
    {
        var full = Path.GetFullPath(path);
        var at = full.IndexOf(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return (null, Libraries(root).Any(lib => full.StartsWith(Path.TrimEndingDirectorySeparator(lib) + '\\', StringComparison.OrdinalIgnoreCase)));
        var apps = full[..(at + @"\steamapps".Length)];
        var folder = full[(at + @"\steamapps\common\".Length)..].Split('\\')[0];
        foreach (var acf in Directory.Exists(apps) ? Directory.EnumerateFiles(apps, "appmanifest_*.acf") : [])
        {
            var text = File.ReadAllText(acf);
            if (Values(text, "installdir").FirstOrDefault() is { } dir && dir.Equals(folder, StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(Values(text, "appid").FirstOrDefault(), out var id)) return (id, true);
        }
        return (null, true);
    }

    /// <summary>Steam's folder: the registry's SteamPath, else the default install folder.</summary>
    public static string Root() => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string ?? @"C:\Program Files (x86)\Steam";

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
