using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SCSKiller.Core.App;

/// <summary>Record-key sets read from files (recordings, packs, a plan's and a warm's key files), cached while the file is
/// the same: its size, write time, change time and file id, and a hash of its first and last 4 KB. Every writer SCSKiller owns also calls
/// <see cref="Forget"/>. At most <see cref="MaxKeys"/> keys stay cached, the least recently used sets going first; a
/// larger set is read each time.</summary>
public static partial class KeyFiles
{
    /// <summary>About 30 MB of key strings (126 B a key). Settable for tests: the bound changes what is read again, never a result.</summary>
    public static long MaxKeys { get; internal set; } = 250_000;
    const int Sample = 4096;

    sealed class Entry(string stamp, HashSet<string> keys)
    {
        public readonly string Stamp = stamp;
        public readonly HashSet<string> Keys = keys;
        public long Used;
    }

    static readonly Dictionary<string, Entry> cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Lock gate = new();
    static long count, uses, lastUse;

    public static long CachedKeys { get { lock (gate) return count; } }

    /// <summary><paramref name="read"/>'s keys of the file (shared: never changed by a caller); none when it's missing or
    /// unreadable. <paramref name="variant"/>: another reading of the same file. <paramref name="failOpen"/> false: an
    /// unreadable file throws instead.</summary>
    public static HashSet<string> Keys(string path, Func<string, HashSet<string>> read, string? variant = null, bool failOpen = true)
    {
        var full = Path.GetFullPath(path);
        try { return Stamp(full, strict: !failOpen) is { } stamp ? Cached(variant == null ? full : full + "|" + variant, stamp, () => read(full)) : []; }
        catch (Exception e) when (failOpen && e is IOException or InvalidDataException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>A set computed from several files, cached while every one of them is the same (each by its size, write
    /// time and sample, a missing one as missing) and <paramref name="extra"/> too; none when one can't be read.</summary>
    public static HashSet<string> Derived(string name, IEnumerable<string?> inputs, string extra, Func<HashSet<string>> compute)
    {
        try { return Cached("derived|" + name, DerivedStamp(inputs, extra), compute); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>What <see cref="Derived"/> keys a set on: each input's stamp (a missing one as missing) and <paramref name="extra"/>.</summary>
    public static string DerivedStamp(IEnumerable<string?> inputs, string extra) =>
        string.Join("|", inputs.Select(i => i == null ? "-" : Stamp(Path.GetFullPath(i)) ?? "missing").Append(extra));

    static HashSet<string> Cached(string name, string stamp, Func<HashSet<string>> read)
    {
        lock (gate)
        {
            lastUse = Environment.TickCount64;
            if (cache.TryGetValue(name, out var hit) && hit.Stamp == stamp)
            {
                hit.Used = ++uses;
                Evict();   // a bound lowered since
                return hit.Keys;
            }
        }
        var keys = read();
        if (keys.Count > MaxKeys) return keys;
        lock (gate)
        {
            if (cache.Remove(name, out var old)) count -= old.Keys.Count;
            cache[name] = new Entry(stamp, keys) { Used = ++uses };
            count += keys.Count;
            Evict();
        }
        return keys;
    }

    /// <summary>The least recently used sets go while the total is over the bound; the caller holds the gate.</summary>
    static void Evict()
    {
        while (count > MaxKeys && cache.Count > 0)
        {
            var lru = cache.MinBy(e => e.Value.Used);
            cache.Remove(lru.Key);
            count -= lru.Value.Keys.Count;
        }
    }

    /// <summary>Drops every cached set once none was asked for in <paramref name="idle"/>: an evaluation burst (a scan) reads
    /// them again in one go, and the app idles in the notification area for hours. True when it dropped any.</summary>
    public static bool DropIdle(TimeSpan idle)
    {
        lock (gate)
        {
            if (cache.Count == 0 || !Idle(lastUse, Environment.TickCount64, idle)) return false;
            cache.Clear();
            count = 0;
            return true;
        }
    }

    /// <summary>The cache was last asked for at <paramref name="lastUse"/> and it is <paramref name="now"/> (milliseconds).</summary>
    internal static bool Idle(long lastUse, long now, TimeSpan idle) => now - lastUse >= idle.TotalMilliseconds;

    /// <summary>A file written here: its cached sets (every variant) are read again.</summary>
    public static void Forget(string path)
    {
        var full = Path.GetFullPath(path);
        lock (gate)
            foreach (var k in cache.Keys.Where(k => k.Equals(full, StringComparison.OrdinalIgnoreCase) || k.StartsWith(full + "|", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                count -= cache[k].Keys.Count;
                cache.Remove(k);
            }
    }

    /// <summary><paramref name="strict"/>: null only when the file isn't there; one that can't be opened throws (Exists
    /// is false for a file it may not look at).</summary>
    internal static string? Stamp(string path, bool strict = false)
    {
        if (!strict && !File.Exists(path)) return null;
        FileStream f;
        try { f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        using (f)
        {
            var head = new byte[(int)Math.Min(Sample, f.Length)];
            f.ReadExactly(head);
            var tail = new byte[(int)Math.Min(Sample, Math.Max(0, f.Length - head.Length))];
            f.Seek(-tail.Length, SeekOrigin.End);
            f.ReadExactly(tail);
            return $"{f.Length}:{File.GetLastWriteTimeUtc(f.SafeFileHandle).Ticks}:{Identity(f.SafeFileHandle)}:{Convert.ToHexStringLower(SHA1.HashData([.. head, .. tail]))}";
        }
    }

    /// <summary>The file's change time (NTFS's, or the inode's), volume or device and file id, read without its data: any write or replacement
    /// moves the change time, which tools that set file times (an archive's extraction) can't set back. "" when the file
    /// can't be opened; a part the file system doesn't give is left empty.</summary>
    public static partial string Identity(string path);

    private static partial string Identity(Microsoft.Win32.SafeHandles.SafeFileHandle h);

    static readonly HashSet<string> Damaged = [];

    /// <summary>A key file's set (sorted 20-byte keys); null when it's missing, can't be read or isn't what its name says.</summary>
    public static HashSet<string>? Set(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            return Stamp(full) is { } stamp && Cached(full, stamp, () => Valid(full, out var bytes) ? [.. bytes.Chunk(20).Select(k => Convert.ToHexStringLower(k))] : Damaged) is var keys
                && !ReferenceEquals(keys, Damaged) ? keys : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static string Name(string prefix, byte[] bytes) => $"{prefix}-{Convert.ToHexStringLower(SHA256.HashData(bytes))[..16]}.keys";

    /// <summary>The file holds whole keys whose hash is the one in its name.</summary>
    static bool Valid(string path, out byte[] bytes)
    {
        bytes = File.ReadAllBytes(path);
        var name = Path.GetFileName(path);
        return bytes.Length % 20 == 0 && name.LastIndexOf('-') is var dash and > 0 && name.Equals(Name(name[..dash], bytes), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Writes <paramref name="keys"/> as <c>&lt;prefix&gt;-&lt;hash of the contents&gt;.keys</c> in <paramref name="dir"/>
    /// (again when that file is damaged) and returns that file name: a file never changes once written, so state naming it
    /// always names what it was saved with. An existing one gets a new write time: a prune leaves it the hour its state
    /// takes to name it. Under the lock of the folder's record, which a prune holds (<see cref="AppStore.PruneKeyFiles"/>).</summary>
    public static string Write(string dir, string prefix, IEnumerable<string> keys)
    {
        byte[] bytes = [.. keys.Order(StringComparer.Ordinal).SelectMany(Convert.FromHexString)];
        var path = Path.Combine(dir, Name(prefix, bytes));
        AppStore.Locked(Path.Combine(dir, "state.json"), () =>
        {
            if (!File.Exists(path) || !Valid(path, out _)) AppStore.WriteAtomic(path, bytes);
            else
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                Forget(path);
            }
        });
        return Path.GetFileName(path);
    }

    /// <summary>Tests: runs with the folder after a prune lists its files, before it deletes any.</summary>
    internal static Action<string>? PruneListed;

    /// <summary>Key files (and the temp files of interrupted writes) of the game folder that <paramref name="keep"/> doesn't
    /// name and that are older than <paramref name="age"/>: one another process just wrote is named by its state soon.
    /// The caller holds the state's lock and read <paramref name="keep"/> under it.</summary>
    internal static void Prune(string dir, TimeSpan age, params string?[] keep)
    {
        if (!Directory.Exists(dir)) return;
        var before = DateTime.UtcNow - age;
        foreach (var f in new[] { "plan-*.keys", "warm-*.keys", "plan-*.keys.*.tmp", "warm-*.keys.*.tmp" }.SelectMany(p => Directory.EnumerateFiles(dir, p))
                     .Where(f => !keep.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase) && File.GetLastWriteTimeUtc(f) < before).ToList()
                     .Also(() => PruneListed?.Invoke(dir)))
            try
            {
                if (File.GetLastWriteTimeUtc(f) >= before) continue;   // reused since it was listed
                File.Delete(f);
                Forget(f);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

static class ListAlso
{
    public static List<T> Also<T>(this List<T> list, Action? then)
    {
        then?.Invoke();
        return list;
    }
}
