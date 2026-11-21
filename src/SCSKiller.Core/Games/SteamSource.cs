using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.Games;

/// <summary>Installed Steam games from libraryfolders.vdf + appmanifest_*.acf. Tools and applications installed like
/// games (SteamVR, Lossless Scaling, redistributables, runtimes) are left out by Steam's own app type, from
/// appcache/appinfo.vdf, except a Tool whose exe is an Unreal game build (a game's benchmark); if that file is missing or
/// unreadable everything installed is kept.</summary>
public sealed partial class SteamSource(string? steamRoot = null) : IGameSource, ILeavesOut
{
    public Store Store => Store.Steam;

    public Func<string, bool> LeftOut { get; set; } = _ => false;

    (string Stamp, HashSet<uint> Asked, Dictionary<uint, string> Types)? _types;   // appinfo.vdf is only re-read when it changes

    readonly System.Collections.Concurrent.ConcurrentDictionary<uint, byte> _noExeLogged = new(), _typeLogged = new();   // once per app and process, not every scan

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
        foreach (var lib in Libraries(root).Where(l => !LeftOut(l)))
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
                        if (LeftOut(install)) continue;
                        var build = V("buildid");
                        if (Collections.Expand(new Game($"steam:{id}", V("name") ?? dir, Store.Steam, install, "", build)) is { Count: > 0 } games)
                        {
                            installed.AddRange(games.Where(g => !LeftOut(g.InstallDir)).Select(g => (id, g)));
                            continue;
                        }
                        var exe = Known?.GetValueOrDefault($"steam:{id}") is { Version: { } was } k && was == build && k.InstallDir.Equals(install, StringComparison.OrdinalIgnoreCase)
                            && File.Exists(k.ExePath) && !GameFiles.NotTheGameExe(install, k.ExePath) ? k.ExePath
                            : GameFiles.FindExe(install, steamLaunch: LaunchExe(Apps(AppInfoPath(root), new HashSet<uint> { id })?.GetValueOrDefault(id), Values(text, "BetaKey").LastOrDefault()));   // MountedConfig's, after UserConfig's
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
        var played = new List<Game>();
        foreach (var (id, game) in installed)
            if (types?.TryGetValue(id, out var t) != true || IsGameType(t!) || t!.Equals("tool", StringComparison.OrdinalIgnoreCase) && GameFiles.IsGameShipping(game.ExePath))
                played.Add(game);
            else if (_typeLogged.TryAdd(id, 0)) Log?.Report($"Steam: {game.Name} ({id}) left out: Steam lists it as type {t}");
        return played;
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
        var stamp = AppInfoStamp(appinfo);
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
        // Steam's folder without steamapps (a game's bundled platform\steam) is no library, as the proxy's vac_now takes it
        if (libraries.Count == 0 && !Absent(Path.Combine(root, "steamapps"))) libraries.Add(root);
        return libraries.Select(Path.GetFullPath).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
    }

    static bool Absent(string path)
    {
        try { File.GetAttributes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return false;
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

    /// <summary>The app that owns the steamapps/common folder <paramref name="path"/> is in, as the proxy's folder_owner picks
    /// it when several apps' manifests name the folder: the only one with bytes there (SizeOnDisk), fully installed
    /// (StateFlags 4). <paramref name="app"/> when its own manifest has bytes there, when one app or none names the folder,
    /// or when the owner can't be told.</summary>
    internal static uint? FolderOwner(string path, uint? app)
    {
        var full = Path.GetFullPath(path);
        var at = full.IndexOf(CommonPart, StringComparison.OrdinalIgnoreCase);
        if (at < 0 || app == null) return app;
        var apps = full[..(at + AppsPart.Length)];
        var folder = full[(at + CommonPart.Length)..].Split(Path.DirectorySeparatorChar)[0];
        // folder names as the proxy matches them: a Linux library's "Game" and "game" are two folders
        var named = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool Names(string text) => Values(text, "installdir").FirstOrDefault() is { } dir && dir.Equals(folder, named);
        // an app with bytes there is never another's: no other can be the only one with bytes
        if (Path.Combine(apps, $"appmanifest_{app}.acf") is var own && File.Exists(own) && File.ReadAllText(own) is var mine && Names(mine) && Claim(mine) > 0) return app;
        var claims = new Dictionary<uint, int>();
        foreach (var acf in Directory.Exists(apps) ? Manifests(apps) : [])
        {
            var text = File.ReadAllText(acf);
            if (Names(text) && uint.TryParse(Values(text, "appid").FirstOrDefault(), out var id))
                claims[id] = Math.Max(claims.GetValueOrDefault(id), Claim(text));
        }
        return claims.Count > 1 && claims.Where(c => c.Value > 0).ToList() is [{ Value: 2 } owner] ? owner.Key : app;
    }

    // the proxy's folder_claim: 2 with bytes there and fully installed, 1 with bytes otherwise, 0 without
    static int Claim(string acf) =>
        Values(acf, "SizeOnDisk").ToList() is [{ Length: > 0 } size] && size.All(char.IsAsciiDigit) && size.Any(c => c != '0')
            ? (Values(acf, "StateFlags").ToList() is [{ Length: <= 10 } f] && ulong.TryParse(f, NumberStyles.None, CultureInfo.InvariantCulture, out var flags) && (flags & 4) != 0 ? 2 : 1)
            : 0;

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

    // the app and its compatdata folder (CompatDataOf)
    internal static (uint App, string Data)? CompatData(Game game, string? steamRoot)
    {
        var root = steamRoot ?? Root();
        var full = Path.GetFullPath(game.InstallDir);
        var at = full.IndexOf(CommonPart, StringComparison.OrdinalIgnoreCase);
        if (at < 0 || (AppId(game.Id) ?? AppAt(root, full).Id) is not { } app) return null;
        return (app, CompatDataOf(full[..(at + AppsPart.Length)], app, root));
    }

    /// <summary>The compatdata folder of <paramref name="app"/>, installed in the steamapps folder <paramref name="apps"/>:
    /// that library's when it holds a prefix (pfx/system.reg), else the first of Steam's folder and its other libraries
    /// that does, as Steam may make it there; <paramref name="apps"/>'s when none does.</summary>
    internal static string CompatDataOf(string apps, uint app, string steamRoot)
    {
        var name = app.ToString(CultureInfo.InvariantCulture);
        var own = Path.Combine(apps, "compatdata", name);
        static bool Made(string data) => File.Exists(Path.Combine(data, "pfx", "system.reg"));
        if (Made(own)) return own;
        foreach (var lib in Libraries(steamRoot).Prepend(Path.GetFullPath(steamRoot)).Distinct(StringComparer.Ordinal))
            if (Path.Combine(lib, "steamapps", "compatdata", name) is var data && Made(data)) return data;
        return own;
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

    /// <summary><see cref="Root()"/>, with which value gave it (<paramref name="from"/>, null where the OS has no such choice).</summary>
    public static partial string Root(out string? from);

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

    /// <summary>The executable, as Steam writes it, of the Windows launch entry (appinfo config/launch) a 64-bit Steam starts for
    /// <paramref name="branch"/> (an appmanifest's BetaKey; none: public): the branch's own entries first, then type "default".
    /// Null without one.</summary>
    internal static string? LaunchExe(Dictionary<string, object>? app, string? branch)
    {
        if (app?.GetValueOrDefault("config") is not Dictionary<string, object> config || config.GetValueOrDefault("launch") is not Dictionary<string, object> launch) return null;
        static string? Of(Dictionary<string, object> e, string key) => (e.GetValueOrDefault("config") as Dictionary<string, object>)?.GetValueOrDefault(key) as string;
        branch = string.IsNullOrEmpty(branch) ? "public" : branch;
        return launch.Values.OfType<Dictionary<string, object>>()
            .Where(e => (Of(e, "oslist") is not { } os || os.Contains("windows", StringComparison.OrdinalIgnoreCase)) && Of(e, "osarch") != "32"
                && (Of(e, "betakey") is not { } key || key.Equals(branch, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => Of(e, "betakey") != null).ThenByDescending(e => "default".Equals(e.GetValueOrDefault("type") as string, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.GetValueOrDefault("executable") as string).FirstOrDefault(e => !string.IsNullOrEmpty(e));
    }

    /// <summary>The appinfo object (common, config...) of the given app ids (null: all) from appinfo.vdf (binary KeyValues, v28 or v29),
    /// streamed: other apps are skipped by their size, or from the shards beside it (<see cref="ShardDir"/>), which win
    /// whenever all 256 read. Null when neither is there or understood; an app that is absent or whose entry doesn't parse
    /// is just missing from the result.</summary>
    public static Dictionary<uint, Dictionary<string, object>>? Apps(string path, IReadOnlySet<uint>? ids) => Apps(path, ids, null);

    /// <param name="states">filled with each read app's info state (the entry's first field: 2 = Steam has its info, 1 = none)</param>
    /// <param name="failed">called with the reason when the result is null</param>
    public static Dictionary<uint, Dictionary<string, object>>? Apps(string path, IReadOnlySet<uint>? ids, Dictionary<uint, uint>? states, Action<string>? failed = null)
    {
        // the source Steam wrote last decides; an appinfo.vdf written later than every shard is read alone, unreadable or not
        if (path.Length > 0 && Directory.Exists(ShardDir(path)) && !LegacyNewer(path))
        {
            string? why = null;
            if (Shards(ShardDir(path), ids, states, e => why = e) is { } sharded) return sharded;
            // an incomplete or damaged set yields to appinfo.vdf
            if (!File.Exists(path)) { failed?.Invoke(why ?? "unreadable"); return null; }
            states?.Clear();
        }
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

    /// <summary>The folder of the 256 shards a Steam client writes in place of <paramref name="appinfo"/> (appinfo.vdf), which it
    /// deletes on converting: appinfo_00.vdf to appinfo_ff.vdf. The shards are there when appinfo_00.vdf is.</summary>
    public static string ShardDir(string appinfo) => Path.Combine(Path.GetDirectoryName(appinfo) ?? "", "appinfo");

    internal static string ShardPath(string dir, int i) => Path.Combine(dir, $"appinfo_{i:x2}.vdf");

    /// <summary>appinfo.vdf was written in a later second than every shard beside it, as the proxy compares them (whole
    /// seconds). A tie goes to the shards: Steam converts appinfo.vdf into them, never the other way.</summary>
    internal static bool LegacyNewer(string appinfo)
    {
        if (new FileInfo(appinfo) is not { Exists: true } legacy) return false;
        var newest = long.MinValue;
        for (int i = 0; i < 256; i++)
            if (new FileInfo(ShardPath(ShardDir(appinfo), i)) is { Exists: true } f) newest = Math.Max(newest, Seconds(f.LastWriteTimeUtc));
        return Seconds(legacy.LastWriteTimeUtc) > newest;
    }

    static long Seconds(DateTime utc) => (long)Math.Floor((decimal)(utc.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond);

    /// <summary>Changes whenever Steam's app info at <paramref name="appinfo"/> (appinfo.vdf or its shards) changes; "" when
    /// neither is there.</summary>
    public static string AppInfoStamp(string appinfo)
    {
        if (appinfo.Length == 0) return "";
        var legacy = new FileInfo(appinfo);
        var dir = ShardDir(appinfo);
        // a file that can't be checked never matches an earlier stamp, so no cached read stands for it
        string Unknown() => $"?{System.Diagnostics.Stopwatch.GetTimestamp()}";
        var shards = new StringBuilder();
        if (Directory.Exists(dir))
            for (int i = 0; i < 256; i++)
                shards.Append(new FileInfo(ShardPath(dir, i)) is { Exists: true } f ? $"{i}:{f.Length}:{f.LastWriteTimeUtc.Ticks}," : Absent(ShardPath(dir, i)) ? $"{i}:-," : $"{i}:{Unknown()},");
        else if (!Absent(dir)) shards.Append(Unknown());
        var file = legacy.Exists ? $"{legacy.Length}:{legacy.LastWriteTimeUtc.Ticks}" : Absent(appinfo) ? "" : Unknown();
        return shards.Length == 0 && file.Length == 0 ? "" : $"{shards}|{file}";
    }

    // the shard set last read whole: the shard that lists each app, and the apps listed more than once
    static (string Stamp, Dictionary<uint, int> Shard, HashSet<uint> Twice)? shardIndex;

    /// <summary><see cref="Apps(string, IReadOnlySet{uint}?, Dictionary{uint, uint}?, Action{string}?)"/> from the shards in
    /// <paramref name="dir"/>: null unless all 256 read whole. A shard: a 44-byte header (u32 magic 0x0756442A, universe,
    /// shard index, shard count 256, apps, index bytes, keys, key bytes, data bytes, 8 more bytes), the index (per app u32 id,
    /// payload offset into the data, payload size, 50 bytes whose byte 20 is 0 when Steam has the app's info, u32 n and n
    /// u32s, 13 bytes, a NUL-terminated string), the key table (NUL-terminated names) and the data (per app a SHA-1, then
    /// binary KeyValues whose keys index the shard's key table). Which shard holds an app isn't derived from its id. An app
    /// listed twice, or with an entry over 16 MiB, is listed without usable info, as the proxy's damaged entry.</summary>
    static Dictionary<uint, Dictionary<string, object>>? Shards(string dir, IReadOnlySet<uint>? ids, Dictionary<uint, uint>? states, Action<string> failed)
    {
        try
        {
            var stamp = dir + "|" + AppInfoStamp(Path.Combine(Path.GetDirectoryName(dir) ?? "", "appinfo.vdf"));
            (string Stamp, Dictionary<uint, int> Shard, HashSet<uint> Twice) known;
            lock (indexGate) known = shardIndex is { } k && k.Stamp == stamp ? k : default;
            var read = new Dictionary<int, (List<(uint Id, int Offset, int Size, uint State)> Entries, string[] Keys, byte[] Bytes)>();
            if (known.Shard == null)
            {
                var shard = new Dictionary<uint, int>();
                var twice = new HashSet<uint>();
                for (int i = 0; i < 256; i++)
                {
                    read[i] = Shard(dir, i);
                    foreach (var e in read[i].Entries)
                        if (!shard.TryAdd(e.Id, i)) twice.Add(e.Id);
                }
                // an app moved between shards while they were read would be missed
                if (dir + "|" + AppInfoStamp(Path.Combine(Path.GetDirectoryName(dir) ?? "", "appinfo.vdf")) != stamp) throw new IOException($"{dir}: changed while read");
                known = (stamp, shard, twice);
                lock (indexGate) shardIndex = known;
            }
            else
                // a reused index still needs every shard readable, as the proxy reads them all
                for (int i = 0; i < 256; i++) File.OpenHandle(ShardPath(dir, i), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete).Dispose();
            var result = new Dictionary<uint, Dictionary<string, object>>();
            foreach (var group in (ids ?? (IEnumerable<uint>)known.Shard.Keys).Where(known.Shard.ContainsKey).Distinct().GroupBy(id => known.Shard[id]))
            {
                // the shard read again whole: an index is never paired with another write's data
                var (entries, keys, bytes) = read.TryGetValue(group.Key, out var had) ? had : Shard(dir, group.Key);
                foreach (var id in group)
                {
                    var at = entries.FindIndex(x => x.Id == id);
                    if (at < 0)
                    {
                        if (states != null) states[id] = 1;   // gone from its shard since the index: listed without usable info
                        continue;
                    }
                    var e = entries[at];
                    if (states != null) states[id] = e.State;
                    if (known.Twice.Contains(id) || entries.FindLastIndex(x => x.Id == id) != at || e.Size + 20 > 16 << 20) continue;
                    try
                    {
                        var kv = Object(new BinaryReader(new MemoryStream(bytes, e.Offset, e.Size, writable: false)), keys);
                        result[id] = kv.GetValueOrDefault("appinfo") as Dictionary<string, object> ?? kv;
                    }
                    catch (Exception x) when (x is InvalidDataException or EndOfStreamException or IndexOutOfRangeException) { }
                }
            }
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            failed(e.Message);
            return null;   // FileNotFoundException and EndOfStreamException are IOExceptions
        }
    }

    /// <summary>Shard <paramref name="i"/> in <paramref name="dir"/>, read whole once and checked (see <see cref="Shards"/>):
    /// each app's payload (offset into the bytes, past the SHA-1) and info state, the key table, the bytes.</summary>
    static (List<(uint Id, int Offset, int Size, uint State)> Entries, string[] Keys, byte[] Bytes) Shard(string dir, int i)
    {
        var path = ShardPath(dir, i);
        byte[] b;
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
        {
            if (f.Length < 44 || f.Length > (192L << 20) + 44) throw new InvalidDataException($"{path}: damaged header");
            b = new byte[f.Length];
            f.ReadExactly(b);
            // read twice: a shard Steam rewrites in place meanwhile can't mix two writes
            var again = new byte[b.Length];
            f.Position = 0;
            f.ReadExactly(again);
            if (f.Length != b.Length || !again.AsSpan().SequenceEqual(b)) throw new IOException($"{path}: changed while read");
        }
        uint U(long at) => BitConverter.ToUInt32(b, (int)at);
        if (U(0) != 0x0756442A) throw new InvalidDataException($"{path}: unknown format");
        uint apps = U(16), indexBytes = U(20), keyCount = U(24), keyBytes = U(28), dataBytes = U(32);
        if (U(8) != i || U(12) != 256 || 44L + indexBytes + keyBytes + dataBytes != b.Length || apps > indexBytes / 80 || keyCount > keyBytes
            || indexBytes > 64 << 20 || keyBytes > 64 << 20)
            throw new InvalidDataException($"{path}: damaged header");
        long end = 44L + indexBytes, data = end + keyBytes, at = 44;
        var entries = new List<(uint, int, int, uint)>((int)apps);
        for (uint n = 0; n < apps; n++)
        {
            if (end - at < 66) throw new InvalidDataException($"{path}: damaged index");
            uint id = U(at), offset = U(at + 4), size = U(at + 8), extra = U(at + 62);
            if (size < 20 || (long)offset + size > dataBytes || extra > indexBytes / 4 || end - at - 66 < 4L * extra + 14) throw new InvalidDataException($"{path}: damaged index");
            var from = at + 66 + 4L * extra + 13;
            var z = Array.IndexOf(b, (byte)0, (int)from, (int)(end - from));
            if (z < 0) throw new InvalidDataException($"{path}: damaged index");
            entries.Add((id, (int)(data + offset + 20), (int)size - 20, b[at + 32] == 0 ? 2u : 1u));
            at = z + 1;
        }
        if (at != end) throw new InvalidDataException($"{path}: damaged index");
        var names = new List<string>();
        for (long p = end; p < data;)
        {
            var z = Array.IndexOf(b, (byte)0, (int)p, (int)(data - p));
            if (z < 0) throw new InvalidDataException($"{path}: damaged key table");
            names.Add(Encoding.UTF8.GetString(b, (int)p, (int)(z - p)));
            p = z + 1;
        }
        if (names.Count != keyCount) throw new InvalidDataException($"{path}: damaged key table");
        return (entries, [.. names], b);
    }

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
                case 0: o[key] = o.GetValueOrDefault(key) is Dictionary<string, object> had ? Merge(had, Object(r, keys, depth + 1)) : Object(r, keys, depth + 1); break;
                case 1: Keep(o, key, CString(r)); break;
                case 2: Keep(o, key, r.ReadInt32()); break;
                case 3 or 4 or 6: r.ReadInt32(); break;          // float, pointer, color
                case 7 or 10: r.ReadInt64(); break;              // uint64, int64
                default: throw new InvalidDataException($"KeyValues type {t}");
            }
        }
        return o;
    }

    // a key listed twice keeps both objects' keys, as the proxy's walk sees a category_8 in either
    static Dictionary<string, object> Merge(Dictionary<string, object> into, Dictionary<string, object> from)
    {
        foreach (var (k, v) in from)
            if (into.GetValueOrDefault(k) is Dictionary<string, object> a && v is Dictionary<string, object> b) Merge(a, b);
            else Keep(into, k, v);
        return into;
    }

    // a value never takes an object's place
    static void Keep(Dictionary<string, object> o, string key, object value)
    {
        if (o.GetValueOrDefault(key) is not Dictionary<string, object>) o[key] = value;
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
/// steamapps/compatdata/&lt;app&gt;/pfx (<see cref="SteamSource.CompatDataOf"/>). <paramref name="Tool"/>: the compatibility tool Steam runs the
/// game with, by its internal name (<see cref="SteamSource.CompatTool"/>). <paramref name="LastRun"/>: the Proton that last
/// ran the prefix, as its config_info names it. Each null when Steam's files don't say.</summary>
public sealed record ProtonPrefix(uint AppId, string Folder, string? Tool, string? LastRun);
