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

    public IReadOnlyList<Game> Discover()
    {
        var root = steamRoot ?? Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                   ?? @"C:\Program Files (x86)\Steam";
        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        var libraries = File.Exists(vdf) ? Values(File.ReadAllText(vdf), "path").ToList() : [];
        if (libraries.Count == 0) libraries.Add(root);

        var installed = new List<(uint Id, Game Game)>();
        foreach (var lib in libraries.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(acf);
                string? V(string key) => Values(text, key).FirstOrDefault();
                if (!uint.TryParse(V("appid"), out var id) || V("installdir") is not { } dir) continue;
                if (!int.TryParse(V("StateFlags"), out var flags) || (flags & 4) == 0) continue;   // 4 = fully installed
                var install = Path.Combine(apps, "common", dir);
                var build = V("buildid");
                var exe = Known?.GetValueOrDefault($"steam:{id}") is { Version: { } was } k && was == build && k.InstallDir.Equals(install, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(k.ExePath) && !GameFiles.NotTheGameExe(install, k.ExePath) ? k.ExePath : GameFiles.FindExe(install);   // an exe picked before a rule ruled it out
                if (exe == null) continue;
                installed.Add((id, new Game($"steam:{id}", V("name") ?? dir, Store.Steam, install, exe, build)));
            }
        }
        var types = TypesOf(Path.Combine(root, "appcache", "appinfo.vdf"), installed.Select(a => a.Id).ToHashSet());
        return installed.Where(a => types?.TryGetValue(a.Id, out var t) != true || IsGameType(t!)).Select(a => a.Game).ToList();
    }

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

    /// <summary>The appinfo object (common, config...) of the given app ids from appinfo.vdf (binary KeyValues, v28 or v29),
    /// streamed: other apps are skipped by their size. Null when the file is missing or not
    /// understood; an app that is absent or whose entry doesn't parse is just missing from the result.</summary>
    public static Dictionary<uint, Dictionary<string, object>>? Apps(string path, IReadOnlySet<uint> ids)
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
                    foreach (var id in ids)
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
            else if (magic != 0x07564428) return null;
            var index = new Dictionary<uint, (long, uint)>();
            for (uint id; (id = r.ReadUInt32()) != 0;)
            {
                var size = r.ReadUInt32();
                index[id] = (f.Position, size);
                if (!ids.Contains(id)) { f.Seek(size, SeekOrigin.Current); continue; }
                Add(id, r.ReadBytes((int)size), keys);
            }
            lock (indexGate) indexed = (stamp, index, keys);
            return apps;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            return null;   // EndOfStreamException is an IOException
        }
    }

    // the file last read whole: where each app's entry is, so that another app is read alone while the file stays the same
    static (string Stamp, Dictionary<uint, (long Offset, uint Size)> Index, string[]? Keys)? indexed;
    static readonly Lock indexGate = new();

    /// <summary>A binary KeyValues object up to its end marker: nested objects and strings kept, numbers skipped.</summary>
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
                case 2 or 3 or 4 or 6: r.ReadInt32(); break;   // int, float, pointer, color
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
