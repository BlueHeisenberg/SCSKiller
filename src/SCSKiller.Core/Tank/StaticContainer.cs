using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Tank;

/// <summary>Blizzard's static TACT container, as Overwatch's Steam install has it: <c>data\.build.config</c> and the data
/// files <c>data\data.CCC.AAA</c>, each a run of BLTE blobs back to back (measured: no gaps, no padding). A blob is "BLTE",
/// a u32 BE header size (counted from the blob's start), then a flags byte, a u24 BE chunk count and per chunk a u32 BE
/// encoded size, a u32 BE decoded size and its MD5 (24 bytes; 40 with flags 0x10); then the chunks, each a mode byte and
/// its data: 'N' plain, 'Z' zlib, 'E' encrypted (TACT keys), '4' and 'F' (other codecs, unused by Overwatch's data). Read
/// without the encoding or root manifests (the root is encrypted per build): every blob is reached by walking the files.
/// Every size is checked against the file and the limits below before anything is allocated.</summary>
public static class StaticContainer
{
    public const string Dir = "data", BuildConfig = ".build.config";

    /// <summary>Chunks one blob may have, and how big a decoded asset may get, before it's skipped as not a shader.</summary>
    const int MaxChunks = 1 << 16;
    public const int MaxAsset = 64 << 20;
    /// <summary>A text file read whole (build config, the game's settings); a bigger one isn't read.</summary>
    const int MaxText = 1 << 20;

    public readonly record struct Chunk(int Encoded, int Decoded);

    /// <summary>One blob: where it starts in its file, its header size and its chunks.</summary>
    public sealed record Blob(long Offset, int HeaderSize, Chunk[] Chunks)
    {
        public long Encoded => Chunks.Sum(c => (long)c.Encoded);
        public long Length => HeaderSize + Encoded;
    }

    /// <summary>The data files, by name; the encoding file (data.000.000) and the rest alike.</summary>
    public static IEnumerable<FileInfo> Files(string installDir) =>
        GameFiles.Find(installDir, Dir) is var dir && Directory.Exists(dir)
            ? GameFiles.EnumerateFiles(new DirectoryInfo(dir), "data.*.*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
                .Where(f => IsDataName(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal)
            : [];

    static bool IsDataName(string n) =>
        n.Length == 12 && n.StartsWith("data.") && char.IsAsciiDigit(n[5]) && char.IsAsciiDigit(n[6]) && char.IsAsciiDigit(n[7]) && n[8] == '.'
        && char.IsAsciiDigit(n[9]) && char.IsAsciiDigit(n[10]) && char.IsAsciiDigit(n[11]);

    /// <summary>The file's text; null when it's missing, unreadable or over <see cref="MaxText"/>.</summary>
    public static string? SmallText(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = f.Length;   // read once: the file may grow meanwhile, and what it gains isn't read
            if (length > MaxText) return null;
            var bytes = new byte[length];
            var n = f.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            using var text = new StreamReader(new MemoryStream(bytes, 0, n));
            return text.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The build config's text; null when it can't be read.</summary>
    public static string? BuildConfigText(string installDir) => SmallText(GameFiles.Find(installDir, $@"{Dir}\{BuildConfig}"));

    /// <summary>build-config's values (key = value lines, '#' comments), the first one of each key; empty when it can't be read.</summary>
    public static Dictionary<string, string> ReadBuildConfig(string installDir)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in (BuildConfigText(installDir) ?? "").Split('\n'))
            if (!line.StartsWith('#') && line.IndexOf('=') is var eq and > 0)
                values.TryAdd(line[..eq].Trim(), line[(eq + 1)..].Trim());
        return values;
    }

    /// <summary>Every blob of the file in order. Stops at the first bytes that don't start a blob with a chunk table (its size
    /// can't be known without one) or that run past the file: <paramref name="stopped"/> is then where.</summary>
    public static IEnumerable<Blob> Blobs(SafeFileHandle h, long length, Action<long>? stopped = null)
    {
        for (long at = 0; at < length;)
        {
            if (BlobAt(h, at, length) is not { } blob) { stopped?.Invoke(at); yield break; }
            yield return blob;
            at += blob.Length;   // at least 13: the walk always moves on
        }
    }

    /// <summary>The blob that starts at <paramref name="at"/>; null when none does, or it runs past the file.</summary>
    public static Blob? BlobAt(SafeFileHandle h, long at, long length)
    {
        Span<byte> head = stackalloc byte[12];
        if (at < 0 || at > length - 16 || RandomAccess.Read(h, head, at) < 12 || !head.StartsWith("BLTE"u8)) return null;
        var size = BinaryPrimitives.ReadInt32BigEndian(head[4..]);
        var n = (int)(BinaryPrimitives.ReadUInt32BigEndian(head[8..]) & 0xFFFFFF);
        var entry = head[8] == 0x10 ? 40 : 24;
        if (n < 1 || n > MaxChunks || size != 12 + n * entry || size > length - at) return null;
        var table = new byte[n * entry];
        if (RandomAccess.Read(h, table, at + 12) < table.Length) return null;
        var chunks = new Chunk[n];
        long total = size;
        for (var i = 0; i < n; i++)
        {
            var e = table.AsSpan(i * entry);
            var (enc, dec) = (BinaryPrimitives.ReadUInt32BigEndian(e), BinaryPrimitives.ReadUInt32BigEndian(e[4..]));
            if (enc < 1 || enc > int.MaxValue || dec > int.MaxValue) return null;
            chunks[i] = new((int)enc, (int)dec);
            total += enc;
        }
        return total <= length - at ? new Blob(at, size, chunks) : null;
    }

    /// <summary>The first <paramref name="dest"/>.Length bytes the blob decodes to; fewer when it's shorter, its first chunk
    /// isn't plain or zlib, or doesn't decode.</summary>
    public static int Peek(SafeFileHandle h, Blob b, Span<byte> dest)
    {
        // a deflate block's Huffman tables take a few hundred bytes: a prefix holds a small destination's bytes
        var take = Math.Min(b.Chunks[0].Encoded, 16 << 10);
        var raw = new byte[take];
        if (RandomAccess.Read(h, raw, b.Offset + b.HeaderSize) < take) return 0;
        switch (raw[0])
        {
            case (byte)'N':
                var n = Math.Min(dest.Length, take - 1);
                raw.AsSpan(1, n).CopyTo(dest);
                return n;
            case (byte)'Z':
                try
                {
                    using var z = new ZLibStream(new MemoryStream(raw, 1, take - 1), CompressionMode.Decompress);
                    return z.ReadAtLeast(dest, dest.Length, throwOnEndOfStream: false);
                }
                catch (InvalidDataException) { return 0; }
            default: return 0;
        }
    }

    /// <summary>The blob's decoded bytes; null when a chunk isn't plain or zlib (encrypted), doesn't decode to its size, or
    /// the whole, encoded or decoded, is over <see cref="MaxAsset"/>.</summary>
    public static byte[]? Decode(SafeFileHandle h, Blob b)
    {
        var total = b.Chunks.Sum(c => (long)c.Decoded);
        // zlib over already-compressed bytes grows them a little: the slack covers that and the mode bytes
        if (total is > MaxAsset or 0 || b.Encoded > MaxAsset + (1 << 20)) return null;
        var raw = new byte[b.Encoded];
        if (RandomAccess.Read(h, raw, b.Offset + b.HeaderSize) < raw.Length) return null;
        var output = new byte[total];
        int from = 0, to = 0;
        foreach (var c in b.Chunks)
        {
            var chunk = raw.AsSpan(from, c.Encoded);
            switch (chunk[0])
            {
                case (byte)'N':
                    if (chunk.Length - 1 != c.Decoded) return null;
                    chunk[1..].CopyTo(output.AsSpan(to));
                    break;
                case (byte)'Z':
                    try
                    {
                        using var z = new ZLibStream(new MemoryStream(raw, from + 1, c.Encoded - 1), CompressionMode.Decompress);
                        if (z.ReadAtLeast(output.AsSpan(to, c.Decoded), c.Decoded, throwOnEndOfStream: false) != c.Decoded) return null;
                    }
                    catch (InvalidDataException) { return null; }
                    break;
                default: return null;
            }
            from += c.Encoded;
            to += c.Decoded;
        }
        return output;
    }

    public static SafeFileHandle Open(string path) => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
