using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.Tank;

/// <summary>Blizzard's static TACT container, as Overwatch's Steam install has it: <c>data\.build.config</c> and the data
/// files <c>data\data.CCC.AAA</c>, each a run of BLTE blobs back to back (measured: no gaps, no padding). A blob is "BLTE",
/// a u32 BE header size, then (header size &gt; 0) a flags byte, a u24 BE chunk count and per chunk a u32 BE encoded size,
/// a u32 BE decoded size and its MD5 (24 bytes; 40 with flags 0x10); then the chunks, each a mode byte and its data: 'N'
/// plain, 'Z' zlib, 'E' encrypted (TACT keys), '4' and 'F' (other codecs, unused by Overwatch's data). Read without the
/// encoding or root manifests (the root is encrypted per build): every blob is reached by walking the files.</summary>
public static class StaticContainer
{
    public const string Dir = "data", BuildConfig = ".build.config";

    /// <summary>Chunks one blob may have, and how big a decoded asset may get, before it's skipped as not a shader.</summary>
    const int MaxChunks = 1 << 16;
    public const int MaxAsset = 64 << 20;

    public readonly record struct Chunk(int Encoded, int Decoded);

    /// <summary>One blob: where it starts in its file, its header size and its chunks.</summary>
    public sealed record Blob(long Offset, int HeaderSize, Chunk[] Chunks)
    {
        public long Length => HeaderSize + Chunks.Sum(c => (long)c.Encoded);
    }

    /// <summary>The data files, by name; the encoding file (data.000.000) and the rest alike.</summary>
    public static IEnumerable<FileInfo> Files(string installDir) =>
        Directory.Exists(Path.Combine(installDir, Dir))
            ? new DirectoryInfo(Path.Combine(installDir, Dir)).EnumerateFiles("data.*.*", new EnumerationOptions { IgnoreInaccessible = true })
                .Where(f => IsDataName(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal)
            : [];

    static bool IsDataName(string n) =>
        n.Length == 12 && n.StartsWith("data.") && char.IsAsciiDigit(n[5]) && char.IsAsciiDigit(n[6]) && char.IsAsciiDigit(n[7]) && n[8] == '.'
        && char.IsAsciiDigit(n[9]) && char.IsAsciiDigit(n[10]) && char.IsAsciiDigit(n[11]);

    /// <summary>build-config's values (key = value lines, '#' comments), the first one of each key; empty when it can't be read.</summary>
    public static Dictionary<string, string> ReadBuildConfig(string installDir)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(installDir, Dir, BuildConfig)))
                if (!line.StartsWith('#') && line.IndexOf('=') is var eq and > 0)
                    values.TryAdd(line[..eq].Trim(), line[(eq + 1)..].Trim());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
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
            at += blob.Length;
        }
    }

    /// <summary>The blob that starts at <paramref name="at"/>; null when none does, or it runs past the file.</summary>
    public static Blob? BlobAt(SafeFileHandle h, long at, long length)
    {
        var head = new byte[8];
        if (at < 0 || at + 16 > length || RandomAccess.Read(h, head, at) < 8 || !head.AsSpan(0, 4).SequenceEqual("BLTE"u8)) return null;
        var size = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(4));
        if (size < 12 || at + size > length) return null;
        var table = new byte[size - 8];
        if (RandomAccess.Read(h, table, at + 8) < table.Length) return null;
        var n = (int)(BinaryPrimitives.ReadUInt32BigEndian(table) & 0xFFFFFF);
        var entry = table[0] == 0x10 ? 40 : 24;
        if (n < 1 || n > MaxChunks || 4 + n * entry != table.Length) return null;
        var chunks = new Chunk[n];
        long total = size;
        for (var i = 0; i < n; i++)
        {
            var e = table.AsSpan(4 + i * entry);
            var (enc, dec) = (BinaryPrimitives.ReadUInt32BigEndian(e), BinaryPrimitives.ReadUInt32BigEndian(e[4..]));
            if (enc < 1 || enc > int.MaxValue || dec > int.MaxValue) return null;
            chunks[i] = new((int)enc, (int)dec);
            total += enc;
        }
        return at + total <= length ? new Blob(at, size, chunks) : null;
    }

    /// <summary>The first <paramref name="dest"/>.Length bytes the blob decodes to; fewer when it's shorter, its first chunk
    /// isn't plain or zlib, or doesn't decode.</summary>
    public static int Peek(SafeFileHandle h, Blob b, Span<byte> dest)
    {
        var c = b.Chunks[0];
        // a zlib chunk's first bytes need its Huffman tables: a prefix is enough unless they are unusually long
        foreach (var take in c.Encoded > 16 << 10 ? new[] { 16 << 10, c.Encoded } : [c.Encoded])
        {
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
                        var got = z.ReadAtLeast(dest, dest.Length, throwOnEndOfStream: false);
                        if (got == dest.Length || take == c.Encoded) return got;
                    }
                    catch (InvalidDataException) when (take < c.Encoded) { }   // the prefix ended inside its tables
                    catch (InvalidDataException) { return 0; }
                    break;
                default: return 0;
            }
        }
        return 0;
    }

    /// <summary>The blob's decoded bytes; null when a chunk isn't plain or zlib (encrypted), doesn't decode to its size, or
    /// the whole is over <see cref="MaxAsset"/>.</summary>
    public static byte[]? Decode(SafeFileHandle h, Blob b)
    {
        var total = b.Chunks.Sum(c => (long)c.Decoded);
        if (total is > MaxAsset or 0) return null;
        var raw = new byte[b.Length - b.HeaderSize];
        if (RandomAccess.Read(h, raw, b.Offset + b.HeaderSize) < raw.Length) return null;
        var output = new byte[total];
        int from = 0, to = 0;
        foreach (var c in b.Chunks)
        {
            var chunk = raw.AsSpan(from, c.Encoded);
            from += c.Encoded;
            switch (chunk[0])
            {
                case (byte)'N':
                    if (chunk.Length - 1 != c.Decoded) return null;
                    chunk[1..].CopyTo(output.AsSpan(to));
                    break;
                case (byte)'Z':
                    try
                    {
                        using var z = new ZLibStream(new MemoryStream(raw, from - c.Encoded + 1, c.Encoded - 1), CompressionMode.Decompress);
                        if (z.ReadAtLeast(output.AsSpan(to, c.Decoded), c.Decoded, throwOnEndOfStream: false) != c.Decoded) return null;
                    }
                    catch (InvalidDataException) { return null; }
                    break;
                default: return null;
            }
            to += c.Decoded;
        }
        return output;
    }

    public static SafeFileHandle Open(string path) => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
