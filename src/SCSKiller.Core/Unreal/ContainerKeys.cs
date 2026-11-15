using System.Collections.Concurrent;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.GameTypes.Theia.Encryption;
using CUE4Parse.GameTypes.Theia.Readers;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Unreal;

/// <summary>Keys a container needs before its index reads, taken from the game's own key list (<see cref="UnrealKeys"/>):
/// the Theia key of a container with a .meta beside it (ARC Raiders, MARVEL Tokon, Highguard) and Embark's .utoc key (ARC
/// Raiders, The Finals). A Paks folder's keys are offered before its containers are read, and the one that decrypts is
/// kept for the folder.</summary>
static class ContainerKeys
{
    // replaced whole by Offer: a lookup still running on the old keys keeps its result in the old object
    sealed class Offered(string dir, IReadOnlyList<UnrealKey> keys)
    {
        public readonly IReadOnlyList<UnrealKey> Keys = keys;
        public readonly Lazy<byte[]?> Theia = new(() => FindTheia(dir, keys));
        public volatile FAesKey? TocKey;
    }

    static readonly ConcurrentDictionary<string, Offered> offered = new(StringComparer.OrdinalIgnoreCase);
    static readonly AsyncLocal<(string Dir, IReadOnlyList<UnrealKey> Keys)?> submitted = new();

    static string Dir(string paks) => Path.GetFullPath(paks).TrimEnd('\\', '/');

    public static void Offer(string paks, IReadOnlyList<UnrealKey> keys)
    {
        var dir = Dir(paks);
        if (offered.TryGetValue(dir, out var had) && had.Keys.SequenceEqual(keys)) return;
        offered[dir] = new Offered(dir, keys);
    }

    /// <summary>Runs <paramref name="check"/> with <paramref name="keys"/> beside the folder's offered ones, on this call
    /// only: keys the user submits are tried before they are stored.</summary>
    public static T With<T>(string paks, IReadOnlyList<UnrealKey> keys, Func<T> check)
    {
        var was = submitted.Value;
        submitted.Value = (Dir(paks), keys);
        try { return check(); }
        finally { submitted.Value = was; }
    }

    static IReadOnlyList<UnrealKey> Extra(string dir) => submitted.Value is { } s && string.Equals(s.Dir, dir, StringComparison.OrdinalIgnoreCase) ? s.Keys : [];

    /// <summary>Theia-protected: its .meta holds the per-page keys.</summary>
    public static bool Protected(string container) => File.Exists(container + ".meta");

    /// <summary>A fork whose .utoc is encrypted after its header with a key of its own.</summary>
    public static bool IsEmbark(EGame game) => game is EGame.GAME_ArcRaiders or EGame.GAME_TheFinals;

    static (string Dir, Offered Keys)? Folder(string container)
    {
        for (var d = Path.GetDirectoryName(Path.GetFullPath(container)); d != null; d = Path.GetDirectoryName(d))
            if (offered.TryGetValue(d, out var o)) return (d, o);
        return null;
    }

    static byte[]? Theia(string container) => Folder(container) is not (var dir, var o) ? null
        : o.Theia.Value ?? (Extra(dir) is { Count: > 0 } extra ? FindTheia(dir, extra) : null);

    /// <summary>Whether <paramref name="key"/> is the Theia key of the folder's protected containers.</summary>
    public static bool IsTheia(string paks, FAesKey key) => FindTheia(Dir(paks), [new UnrealKey(key, null)]) != null;

    // the key that decrypts the start of the folder's smallest protected .utoc to the IoStore magic
    static byte[]? FindTheia(string dir, IReadOnlyList<UnrealKey> keys)
    {
        if (keys.Count == 0) return null;
        try
        {
            if (GameFiles.EnumerateFiles(dir, "*.utoc", SearchOption.AllDirectories).Where(Protected).MinBy(p => new FileInfo(p).Length) is not { } utoc) return null;
            var meta = File.ReadAllBytes(utoc + ".meta");
            var head = Read(utoc, 16);
            foreach (var k in keys)
            {
                var b = head.ToArray();
                new TheiaDecryptor(meta, new FileInfo(utoc).Length, k.Aes.Key).DecryptRangeInPlace(b, 0);
                if (b.AsSpan().SequenceEqual("-==--==--==--==-"u8)) return k.Aes.Key;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { }   // a .meta of another build
        return null;
    }

    static byte[] Read(string path, int count)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var b = new byte[count];
        s.ReadAtLeast(b, count, false);
        return b;
    }

    /// <summary>The container's first <paramref name="count"/> bytes, decrypted when it is protected, zero past its end. Null
    /// when it is protected and no offered key opens it.</summary>
    public static byte[]? Head(string container, int count)
    {
        var b = Read(container, count);
        if (!Protected(container)) return b;
        if (Theia(container) is not { } key) return null;
        try
        {
            var length = new FileInfo(container).Length;
            new TheiaDecryptor(File.ReadAllBytes(container + ".meta"), length, key).DecryptRangeInPlace(b.AsSpan(0, (int)Math.Min(count, length)), 0);
            return b;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException) { return null; }
    }

    /// <summary>A protected container as an archive that decrypts as it reads.</summary>
    public static FArchive Archive(string container, VersionContainer versions) =>
        new FTheiaArchive(container, Theia(container) ?? throw new InvalidAesKeyException("a Theia container needs the game's Theia key"), versions);

    /// <summary>The .utoc opened with Embark's .utoc key: the folder's kept one, else the first offered key it
    /// <see cref="Fits"/>.</summary>
    public static IoStoreReader Embark(string utoc, Func<FAesKey, IoStoreReader> open)
    {
        if (Folder(utoc) is (var dir, var o))
            foreach (var key in new[] { o.TocKey }.OfType<FAesKey>().Concat(o.Keys.Concat(Extra(dir)).Select(k => k.Aes)).DistinctBy(k => k.KeyString))
                if (Fits(utoc, key, open) is { } r)
                {
                    if (o.Keys.Any(k => k.Aes.KeyString == key.KeyString)) o.TocKey = key;   // a submitted key is kept once stored
                    return r;
                }
        throw new InvalidAesKeyException("this game's .utoc needs its .utoc key");
    }

    /// <summary>The .utoc read with <paramref name="key"/> when its compressed blocks all lie inside its .ucas: a wrong key
    /// decrypts the TOC into random offsets. Null otherwise.</summary>
    public static IoStoreReader? Fits(string utoc, FAesKey key, Func<FAesKey, IoStoreReader> open)
    {
        IoStoreReader? r = null;
        try
        {
            r = open(key);
            var h = r.TocResource.Header;
            var room = h.PartitionCount <= 1 ? new FileInfo(Path.ChangeExtension(utoc, ".ucas")).Length : (long)Math.Min((double)h.PartitionCount * h.PartitionSize, long.MaxValue);
            var blocks = r.TocResource.CompressionBlocks;
            if (blocks.Length > 0 && blocks.All(b => b.Offset + b.CompressedSize <= room)) return r;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        r?.Dispose();
        return null;
    }
}
