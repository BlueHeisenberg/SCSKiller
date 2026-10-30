using System.Globalization;
using System.Text;

namespace SCSKiller.Core.Games;

/// <summary>A game added to Steam with "Add a Non-Steam Game", as a user's shortcuts.vdf keeps it. <paramref name="AppId"/>:
/// the id Steam gave it, high bit set, which names its compatdata folder and keys its compatibility tool.
/// <paramref name="Exe"/> and <paramref name="StartDir"/> without the quotes Steam keeps them in.</summary>
public sealed record SteamShortcut(uint AppId, string Name, string Exe, string StartDir, string LaunchOptions);

/// <summary>Steam's non-Steam games: each Steam user's userdata/&lt;account id&gt;/config/shortcuts.vdf, binary KeyValues.
/// Game id "steam-shortcut:&lt;app id&gt;": a shortcut's id has its high bit set, so it is never a Steam app's, and it is
/// this PC's own, so like a game added by hand it has no build to share. Only read.</summary>
public static class SteamShortcuts
{
    public const string IdPrefix = "steam-shortcut:";

    /// <summary>The bounds a shortcuts.vdf is read within: a bigger file, more shortcuts, deeper nesting or a longer string
    /// is refused whole.</summary>
    internal const int MaxBytes = 16 << 20, MaxShortcuts = 65536, MaxDepth = 32, MaxString = 64 << 10;

    public static string IdOf(uint app) => IdPrefix + app.ToString(CultureInfo.InvariantCulture);

    /// <summary>The app id in a "steam-shortcut:" id; null for any other id, and for an app id without the high bit.</summary>
    public static uint? AppId(string gameId) =>
        gameId.StartsWith(IdPrefix, StringComparison.Ordinal)
        && uint.TryParse(gameId.AsSpan(IdPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var app) && IsShortcut(app) ? app : null;

    public static bool Is(string gameId) => AppId(gameId) != null;

    internal static bool IsShortcut(uint app) => (app & 0x80000000) != 0;

    /// <summary>The 64-bit game id steam://rungameid/ starts a shortcut by: its app id above the shortcut type 0x02000000.</summary>
    public static ulong GameId(uint app) => (ulong)app << 32 | 0x02000000;

    /// <summary>Every Steam user's shortcuts, the most recent user's first (<see cref="SteamSource.RecentAccount"/>), each app
    /// id once. A file that can't be read or is malformed is left out, its reason given to <paramref name="skipped"/>.</summary>
    public static List<SteamShortcut> All(string steamRoot, Action<string>? skipped = null)
    {
        var all = new List<SteamShortcut>();
        var seen = new HashSet<uint>();
        foreach (var vdf in Files(steamRoot))
            if (Read(vdf, out var error) is { } list) all.AddRange(list.Where(s => seen.Add(s.AppId)));
            else if (error != null) skipped?.Invoke($"{vdf} skipped: {error}");
        return all;
    }

    /// <summary>The shortcut with app id <paramref name="app"/> (<see cref="All"/>); null when no user has it.</summary>
    public static SteamShortcut? Find(string steamRoot, uint app) => All(steamRoot).FirstOrDefault(s => s.AppId == app);

    static IEnumerable<string> Files(string root)
    {
        uint? recent;
        List<uint> accounts;
        try
        {
            recent = SteamSource.RecentAccount(root);
            var userdata = Path.Combine(root, "userdata");
            accounts = Directory.Exists(userdata)
                ? Directory.EnumerateDirectories(userdata).Select(d => uint.TryParse(Path.GetFileName(d), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : (uint?)null)
                    .OfType<uint>().ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        return accounts.OrderByDescending(a => a == recent).ThenBy(a => a)
            .Select(a => Path.Combine(root, "userdata", a.ToString(CultureInfo.InvariantCulture), "config", "shortcuts.vdf")).Where(File.Exists).ToList();
    }

    /// <summary>The shortcuts of a shortcuts.vdf (<see cref="Parse"/>), read from a regular file of at most
    /// <see cref="MaxBytes"/>; null with the reason when it can't be.</summary>
    internal static List<SteamShortcut>? Read(string path, out string? error)
    {
        error = null;
        try
        {
            using var f = GameFiles.OpenRegular(path);
            if (f != null) return Read(f, out error);
            error = "not a regular file";
        }
        catch (FileNotFoundException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error = e.Message; }
        return null;
    }

    /// <summary><see cref="Read(string, out string?)"/> of an open file: read up to the length it has now, never past it
    /// while Steam writes more.</summary>
    internal static List<SteamShortcut>? Read(Stream f, out string? error)
    {
        error = null;
        var length = f.Length;
        if (length > MaxBytes)
        {
            error = $"larger than {MaxBytes >> 20} MB";
            return null;
        }
        var bytes = new byte[length];
        var n = f.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        try { return Parse(bytes.AsSpan(0, n)); }
        catch (InvalidDataException e)
        {
            error = e.Message;
            return null;
        }
    }

    const byte KvObject = 0, KvString = 1, KvInt = 2, KvEnd = 8;

    /// <summary>The shortcuts in a shortcuts.vdf: binary KeyValues whose "shortcuts" object holds one object per shortcut.
    /// Keys are matched ignoring case (older files have "appname" and "exe"), strings are UTF-8. A shortcut without an
    /// app id or an exe, or whose app id lacks the high bit, is left out. InvalidDataException for a truncated file, an
    /// unknown value type, or a file past the bounds (<see cref="MaxShortcuts"/> in all its "shortcuts" objects,
    /// <see cref="MaxDepth"/>, <see cref="MaxString"/>). An empty file has none.</summary>
    internal static List<SteamShortcut> Parse(ReadOnlySpan<byte> vdf)
    {
        var shortcuts = new List<SteamShortcut>();
        var (at, count) = (0, 0);
        // the root object's own end is optional: Steam's files end with it, a file cut right after "shortcuts" is whole
        while (at < vdf.Length && Type(vdf, ref at) is var t && t != KvEnd)
        {
            var key = String(vdf, ref at);
            if (t == KvObject && key.Equals("shortcuts", StringComparison.OrdinalIgnoreCase)) List(vdf, ref at, ref count, shortcuts);
            else Skip(vdf, ref at, t, 1);
        }
        return shortcuts;
    }

    static void List(ReadOnlySpan<byte> vdf, ref int at, ref int count, List<SteamShortcut> shortcuts)
    {
        for (byte t; (t = Type(vdf, ref at)) != KvEnd;)
        {
            String(vdf, ref at);
            if (t != KvObject)
            {
                Skip(vdf, ref at, t, 2);
                continue;
            }
            if (++count > MaxShortcuts) throw new InvalidDataException($"more than {MaxShortcuts} shortcuts");
            if (Shortcut(vdf, ref at) is { } s) shortcuts.Add(s);
        }
    }

    static SteamShortcut? Shortcut(ReadOnlySpan<byte> vdf, ref int at)
    {
        uint app = 0;
        string name = "", exe = "", start = "", options = "";
        for (byte t; (t = Type(vdf, ref at)) != KvEnd;)
        {
            var key = String(vdf, ref at);
            if (t == KvInt && key.Equals("appid", StringComparison.OrdinalIgnoreCase)) app = (uint)Int(vdf, ref at);
            else if (t == KvString)
            {
                var value = String(vdf, ref at);
                if (key.Equals("AppName", StringComparison.OrdinalIgnoreCase)) name = value;
                else if (key.Equals("Exe", StringComparison.OrdinalIgnoreCase)) exe = Unquoted(value);
                else if (key.Equals("StartDir", StringComparison.OrdinalIgnoreCase)) start = Unquoted(value);
                else if (key.Equals("LaunchOptions", StringComparison.OrdinalIgnoreCase)) options = value;
            }
            else Skip(vdf, ref at, t, 3);
        }
        return IsShortcut(app) && exe.Length > 0 ? new SteamShortcut(app, name.Trim(), exe, start, options) : null;
    }

    static string Unquoted(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
    }

    /// <summary>Past a value of type <paramref name="t"/>, an object's at nesting <paramref name="depth"/>.</summary>
    static void Skip(ReadOnlySpan<byte> vdf, ref int at, byte t, int depth)
    {
        switch (t)
        {
            case KvObject:
                if (depth >= MaxDepth) throw new InvalidDataException($"nested deeper than {MaxDepth}");
                for (byte inner; (inner = Type(vdf, ref at)) != KvEnd;)
                {
                    String(vdf, ref at);
                    Skip(vdf, ref at, inner, depth + 1);
                }
                break;
            case KvString: String(vdf, ref at); break;
            case KvInt or 3 or 4 or 6: Take(vdf, ref at, 4); break;   // int, float, pointer, color
            case 7 or 10: Take(vdf, ref at, 8); break;               // uint64, int64
            default: throw new InvalidDataException($"unknown value type {t}");
        }
    }

    static byte Type(ReadOnlySpan<byte> vdf, ref int at) => Take(vdf, ref at, 1)[0];

    static int Int(ReadOnlySpan<byte> vdf, ref int at) => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(Take(vdf, ref at, 4));

    static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> vdf, ref int at, int n)
    {
        if (vdf.Length - at < n) throw new InvalidDataException("truncated");
        at += n;
        return vdf.Slice(at - n, n);
    }

    static string String(ReadOnlySpan<byte> vdf, ref int at)
    {
        var rest = vdf[at..];
        var end = rest[..Math.Min(rest.Length, MaxString + 1)].IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException(rest.Length > MaxString ? $"a string longer than {MaxString >> 10} KB" : "truncated");
        at += end + 1;
        return Encoding.UTF8.GetString(rest[..end]);
    }
}
