using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.Luminous;

/// <summary>A Luminous Studio archive (.earc, magic "CRAF"), version 3.20 as FINAL FANTASY XV WINDOWS EDITION ships every one
/// (measured: 42,465 archives). Little endian. Header (0x40): magic, u32 version (top bit: protected), u32 entry count, u32 block
/// size, u32 entry table offset, u32 URI list offset, u32 path list offset, u32 data offset, u32 flags, u32 chunk size in KiB,
/// u64 archive hash. Entry (0x28): u64 URI and type hash, u32 size, u32 stored size, u32 flags (2: compressed), u32 URI offset,
/// u64 data offset, u32 path offset, u8 localization type, u8 locale, u16 key. URIs are null-terminated UTF-8 at their file
/// offset. A compressed entry is chunks of at most the chunk size decompressed: u32 compressed length, u32 decompressed length,
/// a zlib stream, padding to 4 bytes.</summary>
public static class Earc
{
    public const uint Magic = 0x46415243;   // "CRAF"
    public const uint Version = 0x30014;
    const uint ProtectedBit = 0x80000000, Compressed = 2;
    const int HeaderSize = 0x40, EntrySize = 0x28;
    const int MaxEntries = 1 << 22, MaxUris = 256 << 20, MaxChunk = 64 << 20;

    /// <summary>Longest URI taken (measured: the game's longest .sb URI is 97 bytes), and the most URI bytes one archive's matches
    /// may decode (shadergen's 125,943 .sb URIs: 8.7 MB), since entries can all point at one long string.</summary>
    const int MaxUri = 1024, MaxFoundUris = 64 << 20;

    /// <summary>Largest entry <see cref="Read"/> takes, as a DXBC container's limit.</summary>
    public const int MaxEntry = Carved.Dxbc.MaxSize;

    public sealed record Entry(string Uri, uint Flags, long Offset, int Size, int Stored, ushort Key, int ChunkSize);

    /// <summary>The archive's version without the protected bit; null when the file isn't an archive.</summary>
    public static uint? VersionOf(string path)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[8];
            return RandomAccess.Read(h, head, 0) == 8 && BinaryPrimitives.ReadUInt32LittleEndian(head) == Magic
                ? BinaryPrimitives.ReadUInt32LittleEndian(head[4..]) & ~ProtectedBit : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The entries whose URI ends with <paramref name="suffix"/> (ordinal), in table order; null when the file isn't a
    /// version <see cref="Version"/> archive. InvalidDataException when its tables point outside the file.</summary>
    public static List<Entry>? Entries(SafeFileHandle h, string suffix)
    {
        var length = RandomAccess.GetLength(h);
        var head = new byte[HeaderSize];
        if (length < HeaderSize || RandomAccess.Read(h, head, 0) != HeaderSize || U32(head, 0) != Magic) return null;
        var version = U32(head, 4);
        if ((version & ~ProtectedBit) != Version) return null;
        uint count = U32(head, 8), table = U32(head, 16), uriList = U32(head, 20), pathList = U32(head, 24);
        var chunk = (long)U32(head, 36) * 1024;
        if (count > MaxEntries || table + (long)count * EntrySize > length || uriList > pathList || pathList > length
            || pathList - uriList > MaxUris || chunk is 0 or > MaxChunk)
            throw new InvalidDataException("archive header out of range");
        var entries = new byte[count * EntrySize];
        var uris = new byte[pathList - uriList];
        if (RandomAccess.Read(h, entries, table) != entries.Length || RandomAccess.Read(h, uris, uriList) != uris.Length)
            throw new InvalidDataException("archive tables cut short");

        var mask = (version & ProtectedBit) != 0 ? new Mask(BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(40))) : null;
        ReadOnlySpan<byte> want = Encoding.UTF8.GetBytes(suffix);
        var found = new List<Entry>();
        long uriBytes = 0;
        for (var i = 0; i < count; i++)
        {
            var e = entries.AsSpan(i * EntrySize, EntrySize);
            var hash = BinaryPrimitives.ReadUInt64LittleEndian(e);
            uint size = U32(e, 8), stored = U32(e, 12), uri = U32(e, 20);
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(e[24..]);
            if (mask != null) mask.Apply(hash, ref size, ref stored, ref offset);   // every entry: each one's keys chain from the one before
            if (uri < uriList || uri >= pathList) continue;
            var start = (int)(uri - uriList);
            ReadOnlySpan<byte> name = uris.AsSpan(start, Math.Min(MaxUri, uris.Length - start));
            var end = name.IndexOf((byte)0);
            if (end < 0 || !name[..end].EndsWith(want)) continue;
            if (size > MaxEntry || stored > MaxEntry || stored > length || offset > (ulong)(length - stored))
                throw new InvalidDataException($"entry {i} out of range");
            if ((uriBytes += end) > MaxFoundUris) throw new InvalidDataException("archive URIs over budget");
            found.Add(new Entry(Encoding.UTF8.GetString(name[..end]), U32(e, 16), (long)offset, (int)size, (int)stored,
                BinaryPrimitives.ReadUInt16LittleEndian(e[38..]), (int)chunk));
        }
        return found;
    }

    /// <summary>An entry's bytes, decompressed. InvalidDataException when they don't decode to its size.</summary>
    public static byte[] Read(SafeFileHandle h, Entry e)
    {
        var raw = new byte[e.Stored];
        if (RandomAccess.Read(h, raw, e.Offset) != raw.Length) throw new InvalidDataException($"{e.Uri}: cut short");
        if ((e.Flags & Compressed) == 0)
            return e.Size <= raw.Length ? raw[..e.Size] : throw new InvalidDataException($"{e.Uri}: cut short");
        var b = new byte[e.Size];
        for (int at = 0, p = 0; at < b.Length; p = (p + 3) & ~3)
        {
            if (p > raw.Length - 8) throw new InvalidDataException($"{e.Uri}: chunk header past the end");
            uint c = U32(raw, p), d = U32(raw, p + 4);
            if (p == 0 && e.Key != 0)
            {
                var k = ChunkKey(e.Key);
                c ^= (uint)(k >> 32);
                d ^= (uint)k;
            }
            p += 8;
            if (d == 0 || d > e.ChunkSize || d > b.Length - at || c > raw.Length - p) throw new InvalidDataException($"{e.Uri}: chunk out of range");
            try
            {
                using var z = new ZLibStream(new MemoryStream(raw, p, (int)c), CompressionMode.Decompress);
                z.ReadExactly(b.AsSpan(at, (int)d));
                if (z.ReadByte() >= 0) throw new InvalidDataException($"{e.Uri}: chunk decompresses past its length");
            }
            catch (EndOfStreamException) { throw new InvalidDataException($"{e.Uri}: chunk decompresses short"); }
            at += (int)d;
            p += (int)c;
        }
        return b;
    }

    /// <summary>A protected archive's entry keys, chained in table order from the archive hash with FNV-1a 64's basis and prime:
    /// they mask size, stored size and data offset.</summary>
    internal sealed class Mask(ulong archiveHash)
    {
        const ulong Basis = 0xCBF29CE484222325, Prime = 0x100000001B3;
        ulong sub = Basis ^ archiveHash;

        public void Apply(ulong hash, ref uint size, ref uint stored, ref ulong offset)
        {
            var k = sub * Prime ^ hash;
            size ^= (uint)(k >> 32);
            stored ^= (uint)k;
            sub = k * Prime ^ ~hash;
            offset ^= sub;
        }
    }

    /// <summary>Masks the first chunk header of an entry with a nonzero key: high half the compressed length, low half the
    /// decompressed one. Two steps of the C runtime's LCG on 64 bits.</summary>
    internal static ulong ChunkKey(ushort key)
    {
        static ulong Step(ulong x) => x * 1103515245 + 12345;
        return Step(Step(key));
    }

    static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
}
