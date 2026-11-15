using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

/// <summary>A file's line in the content hash the community database keys a build by: its relative path (lower case, '\';
/// only the file name when it is outside the install), size and the SHA-1 of its first and last <see cref="EdgeBytes"/>
/// (the whole file when smaller). Never a write time or a folder: every install of the same files gives the same line.</summary>
public static class ContentStamps
{
    public const int EdgeBytes = 64 << 10;

    /// <summary>games\&lt;id&gt;\ file of the edge hashes by rel, size, write time and file identity (change time and file id,
    /// <see cref="KeyFiles.Identity"/>): local only, never shared.</summary>
    public const string CacheFile = "content-edges.txt";

    static readonly AsyncLocal<Cache?> current = new();

    public static string Of(string rel, string path)
    {
        rel = Rel(rel);
        try
        {
            var f = new FileInfo(path);
            if (!f.Exists) return $"{rel}|gone\n";
            var size = f.Length;
            var cache = current.Value;
            var key = cache == null ? null : KeyFiles.Identity(path) is var id && Usable(id) ? $"{size}|{f.LastWriteTimeUtc.Ticks}|{id}" : null;
            if (key == null || cache!.Known(rel, key) is not { } sha)
            {
                using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (RandomAccess.GetLength(h) != size) return $"{rel}|gone\n";   // changing while read: no build to key
                sha = Edges(h, size);
                if (key != null) cache!.Add(rel, key, sha);
            }
            return $"{rel}|{size}|{sha}\n";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"{rel}|gone\n"; }
    }

    /// <summary><see cref="Of(string, string)"/> of a file open as <paramref name="h"/>, read through it, never cached.</summary>
    public static string Of(string rel, Microsoft.Win32.SafeHandles.SafeFileHandle h)
    {
        rel = Rel(rel);
        try
        {
            var size = RandomAccess.GetLength(h);
            return $"{rel}|{size}|{Edges(h, size)}\n";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"{rel}|gone\n"; }
    }

    static string Rel(string rel)
    {
        rel = rel.Replace('/', '\\');
        if (rel.StartsWith("..", StringComparison.Ordinal) || rel.StartsWith('\\') || rel.Contains(':')) rel = rel[(rel.LastIndexOf('\\') + 1)..];
        return rel.ToLowerInvariant();
    }

    /// <summary>Both a change time and a file id (FAT has neither): else a replaced file could keep the old edges.</summary>
    static bool Usable(string id) => id.Split('/') is [{ Length: > 0 } changed, { Length: > 0 }] && changed != "0";

    static string Edges(Microsoft.Win32.SafeHandles.SafeFileHandle h, long size)
    {
        var head = (int)Math.Min(size, EdgeBytes);
        var tail = (int)Math.Min(size - head, EdgeBytes);
        var b = new byte[head + tail];
        Read(h, b.AsSpan(0, head), 0);
        Read(h, b.AsSpan(head), size - tail);
        return Convert.ToHexStringLower(SHA1.HashData(b));
    }

    static void Read(Microsoft.Win32.SafeHandles.SafeFileHandle h, Span<byte> into, long at)
    {
        for (var done = 0; done < into.Length;)
            done += RandomAccess.Read(h, into[done..], at + done) is > 0 and var n ? n : throw new EndOfStreamException();
    }

    /// <summary>Stamps taken in this flow until disposed reuse the edge hashes of <paramref name="gameDir"/>'s
    /// <see cref="CacheFile"/> while a file's size, write time and identity stay the same; the file is rewritten at the end
    /// with the stamps taken. A missing or damaged file (its first line is the SHA-1 of the rest) gives the same stamps, read again.</summary>
    public static IDisposable Cached(string gameDir) => new Cache(Path.Combine(gameDir, CacheFile));

    sealed class Cache : IDisposable
    {
        readonly string file;
        readonly Cache? outer;
        readonly Dictionary<string, (string Key, string Sha)> loaded = new(StringComparer.Ordinal);
        readonly ConcurrentDictionary<string, (string Key, string Sha)> taken = new(StringComparer.Ordinal);

        public Cache(string file)
        {
            this.file = file;
            try
            {
                var text = File.ReadAllText(file);
                var body = text.IndexOf('\n') + 1;
                if (body > 0 && text[..(body - 1)] == Sum(text[body..]))
                    foreach (var line in text[body..].Split('\n', StringSplitOptions.RemoveEmptyEntries))   // size|ticks|identity|sha1|rel
                        if (line.Split('|') is [var size, var ticks, { Length: > 0 } id, { Length: 40 } sha, { Length: > 0 } rel])
                            loaded[rel] = ($"{size}|{ticks}|{id}", sha);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { loaded.Clear(); }
            outer = current.Value;
            current.Value = this;
        }

        public string? Known(string rel, string key) =>
            (taken.TryGetValue(rel, out var e) || loaded.TryGetValue(rel, out e)) && e.Key == key ? Add(rel, key, e.Sha) : null;

        public string Add(string rel, string key, string sha)
        {
            taken[rel] = (key, sha);
            return sha;
        }

        static string Sum(string body) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(body)));

        public void Dispose()
        {
            current.Value = outer;
            if (taken.Count == loaded.Count && taken.All(t => loaded.TryGetValue(t.Key, out var l) && l == t.Value)) return;
            var body = new StringBuilder();
            foreach (var (rel, (key, sha)) in taken.OrderBy(t => t.Key, StringComparer.Ordinal)) body.Append($"{key}|{sha}|{rel}\n");
            var text = body.ToString();
            try { AppStore.WriteAtomic(file, Encoding.UTF8.GetBytes($"{Sum(text)}\n{text}")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // the next index reads them again
        }
    }
}
