using System.Buffers.Binary;
using System.Numerics;
using ZstdSharp.Unsafe;

namespace SCSKiller.Core.Vendors;

/// <summary>A file set of NVIDIA's Linux shader cache (GLCache, traced from the driver's read path): &lt;app name&gt;&lt;n&gt;.toc
/// and .bin both start with the same 32-byte header ("CDVN", u32 0x00030000, the folder hashes). The .toc is 24-byte records
/// (key[16], u32 offset of the entry in the .bin, u32 payload size), so a .bin is indexed up to 4 GiB. An entry is u32
/// 0x9846a19d, the key, a u64 MurmurHash3_x86_64 (2010, seed 0) of the payload and the payload's u32 size, then the payload:
/// u32 uncompressed size and one zstd frame. The driver checks all four before it decompresses. Once a set holds about 72 MiB
/// uncompressed the driver trains a 2 MiB zstd dictionary, keeps it as the entry whose key ends in "_NVDICT_", and compresses
/// later entries with it: their frames name it. In its default folder, without __GL_SHADER_DISK_CACHE_PATH, the driver writes
/// version 4 (measured): the same header with u32 0x00040000, the same entries, and 32-byte .toc records (key[16], u64 offset,
/// u64 payload size).</summary>
static class GlCacheFile
{
    public const int HeaderSize = 32, RecordSize = 24, RecordSize4 = 32, EntryHeaderSize = 32;
    public const uint EntryMagic = 0x9846a19d, Version = 0x00030000, Version4 = 0x00040000, DictMagic = 0xEC30A437;

    static ReadOnlySpan<byte> FileMagic => "CDVN"u8;
    static ReadOnlySpan<byte> DictTail => "_NVDICT_"u8;

    /// <summary>A .toc record: the entry of <see cref="Key"/> at <see cref="Offset"/> in the .bin, its payload <see cref="Size"/>
    /// bytes. A version 3 .toc holds the offset's low 32 bits: past 4 GiB only a walk of the .bin gives it.</summary>
    public readonly record struct Record(byte[] Key, long Offset, uint Size)
    {
        public long Length => EntryHeaderSize + (long)Size;
        public bool IsDictionary => Key.AsSpan(8).SequenceEqual(DictTail);
    }

    /// <summary>A .toc's header and records; null, with the reason, for a file of another format. Version 4 only with
    /// <paramref name="v4"/>.</summary>
    public static (byte[] Header, List<Record> Records)? ReadToc(byte[] toc, out string? why, bool v4 = false)
    {
        var size = IsHeader(toc, v4) ? RecordSizeOf(toc) : RecordSize;
        why = toc.Length < HeaderSize || (toc.Length - HeaderSize) % size != 0 ? $"its .toc is {toc.Length} bytes, not a header and whole records"
            : !IsHeader(toc, v4) ? $"its .toc header isn't NVIDIA's version 3{(v4 ? " or 4" : "")}"
            : null;
        if (why != null) return null;
        var records = new List<Record>((toc.Length - HeaderSize) / size);
        for (var at = HeaderSize; at < toc.Length; at += size)
        {
            if (size == RecordSize4)
            {
                var (offset, length) = (BinaryPrimitives.ReadUInt64LittleEndian(toc.AsSpan(at + 16)), BinaryPrimitives.ReadUInt64LittleEndian(toc.AsSpan(at + 24)));
                if (offset > long.MaxValue || length > uint.MaxValue)
                {
                    why = "its .toc names an offset or size no .bin holds";
                    return null;
                }
                records.Add(new(toc[at..(at + 16)], (long)offset, (uint)length));
            }
            else records.Add(new(toc[at..(at + 16)], BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(at + 16)), BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(at + 20))));
        }
        return (toc[..HeaderSize], records);
    }

    public static bool IsHeader(ReadOnlySpan<byte> h, bool v4 = false) => h.Length >= HeaderSize && h[..4].SequenceEqual(FileMagic)
        && BinaryPrimitives.ReadUInt32LittleEndian(h[4..]) is var version && (version == Version || v4 && version == Version4);

    /// <summary>The .toc record size of a file with header <paramref name="h"/>: version 4's, else version 3's.</summary>
    public static int RecordSizeOf(ReadOnlySpan<byte> h) => h.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(h[4..]) == Version4 ? RecordSize4 : RecordSize;

    /// <summary>A .toc record of <paramref name="record"/>'s size (<see cref="RecordSizeOf"/>): the entry of <paramref name="key"/>
    /// at <paramref name="offset"/> in the .bin, its payload <paramref name="size"/> bytes.</summary>
    public static void WriteRecord(Span<byte> record, ReadOnlySpan<byte> key, long offset, long size)
    {
        key.CopyTo(record);
        if (record.Length == RecordSize4)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(record[16..], (ulong)offset);
            BinaryPrimitives.WriteUInt64LittleEndian(record[24..], (ulong)size);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(record[16..], (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(record[20..], (uint)size);
        }
    }

    /// <summary>The entry <paramref name="r"/> names, read as the driver checks it: its header and payload as
    /// <paramref name="entry"/> holds them.</summary>
    public static bool Checks(ReadOnlySpan<byte> entry, Record r) =>
        entry.Length == r.Length && BinaryPrimitives.ReadUInt32LittleEndian(entry) == EntryMagic && entry[4..20].SequenceEqual(r.Key)
        && BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]) == r.Size && BinaryPrimitives.ReadUInt64LittleEndian(entry[20..]) == Murmur(entry[EntryHeaderSize..]);

    /// <summary>An entry of <paramref name="key"/> holding <paramref name="payload"/>.</summary>
    public static byte[] Entry(ReadOnlySpan<byte> key, ReadOnlySpan<byte> payload)
    {
        var e = new byte[EntryHeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(e, EntryMagic);
        key.CopyTo(e.AsSpan(4, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(20), Murmur(payload));
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(28), (uint)payload.Length);
        payload.CopyTo(e.AsSpan(EntryHeaderSize));
        return e;
    }

    /// <summary>A payload's zstd frame: the dictionary it names (0: none), its content size (-1: not stored), whether it
    /// carries a checksum, and its length; null when the payload isn't a u32 and one zstd frame.</summary>
    public readonly record struct Frame(uint DictId, long ContentSize, bool Checksum, long Length);

    public static unsafe Frame? FrameOf(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        var frame = payload[4..];
        fixed (byte* p = frame)
        {
            ZSTD_frameHeader h;
            if (Methods.ZSTD_getFrameHeader(&h, p, (nuint)frame.Length) != 0 || h.frameType != ZSTD_frameType_e.ZSTD_frame) return null;
            var length = Methods.ZSTD_findFrameCompressedSize(p, (nuint)frame.Length);
            if (Methods.ZSTD_isError(length)) return null;
            return new(h.dictID, h.frameContentSize == ulong.MaxValue ? -1 : (long)h.frameContentSize, h.checksumFlag != 0, (long)length);
        }
    }

    /// <summary>The dictionary a payload's frame names (0: none), read from the frame's header alone, which the payload's
    /// first 22 bytes hold; null when it doesn't start with one.</summary>
    public static unsafe uint? FrameDictId(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        var frame = payload[4..];
        fixed (byte* p = frame)
        {
            ZSTD_frameHeader h;
            return Methods.ZSTD_getFrameHeader(&h, p, (nuint)frame.Length) == 0 && h.frameType == ZSTD_frameType_e.ZSTD_frame ? h.dictID : null;
        }
    }

    /// <summary>The id a zstd dictionary names itself by; null when it isn't one.</summary>
    public static uint? DictionaryId(ReadOnlySpan<byte> dict) =>
        dict.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(dict) == DictMagic ? BinaryPrimitives.ReadUInt32LittleEndian(dict[4..]) : null;

    /// <summary>MurmurHash3_x86_64 as SMHasher's 2010 beta had it (c1 and c2 change every block, h1 | h2 &lt;&lt; 32), seed 0:
    /// the checksum the driver keeps of each payload.</summary>
    public static ulong Murmur(ReadOnlySpan<byte> data)
    {
        uint h1 = 0x8de1c3ac, h2 = 0xbab98226, c1 = 0x95543787, c2 = 0x2ad7eb25;
        var blocks = data.Length / 8;
        for (var i = 0; i < blocks; i++)
            Mix(BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 8)..]), BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 8 + 4)..]), ref h1, ref h2, ref c1, ref c2);
        if ((data.Length & 7) != 0)
        {
            Span<byte> tail = stackalloc byte[8];
            tail.Clear();
            data[(blocks * 8)..].CopyTo(tail);
            Mix(BinaryPrimitives.ReadUInt32LittleEndian(tail), BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]), ref h1, ref h2, ref c1, ref c2);
        }
        h2 ^= (uint)data.Length;
        h1 += h2;
        h2 += h1;
        h1 = Fmix(h1);
        h2 = Fmix(h2);
        h1 += h2;
        h2 += h1;
        return h1 | (ulong)h2 << 32;
    }

    static void Mix(uint k1, uint k2, ref uint h1, ref uint h2, ref uint c1, ref uint c2)
    {
        k1 *= c1; k1 = BitOperations.RotateLeft(k1, 11); k1 *= c2; h1 ^= k1; h1 += h2;
        h2 = BitOperations.RotateLeft(h2, 17);
        k2 *= c2; k2 = BitOperations.RotateLeft(k2, 11); k2 *= c1; h2 ^= k2; h2 += h1;
        h1 = h1 * 3 + 0x52dce729; h2 = h2 * 3 + 0x38495ab5;
        c1 = c1 * 5 + 0x7b7d159c; c2 = c2 * 5 + 0x6bce6396;
    }

    static uint Fmix(uint h)
    {
        h ^= h >> 16; h *= 0x85ebca6b; h ^= h >> 13; h *= 0xc2b2ae35; h ^= h >> 16;
        return h;
    }
}
