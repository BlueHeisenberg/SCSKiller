using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using CUE4Parse.Compression;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Nixxes;

namespace SCSKiller.Core.Decima;

/// <summary>A Decima archive (.bin), read at random as its unpacked stream. Header, 40 bytes: u32 magic, u32 key, u64 file
/// size, u64 data size, u64 file entry count, u32 chunk entry count, u32 max chunk size. Then 32-byte file entries (u32
/// index, u32 key, u64 path hash, u64 offset, u32 size, u32 key; not needed to carve) and 32-byte chunk entries (u64
/// unpacked offset, u32 unpacked size, u32 key, u64 packed offset, u32 packed size, u32 key). Chunks are Oodle blocks.
/// <see cref="Encrypted"/> archives (Death Stranding) XOR the header past the key, each chunk entry and each chunk's data
/// with hashes of <see cref="Salts"/>; <see cref="Plain"/> ones (Horizon Zero Dawn) don't.</summary>
public sealed class DecimaArchive : IDisposable
{
    public const uint Plain = 0x20304050, Encrypted = 0x21304050;
    public const int HeaderSize = 40, EntrySize = 32;
    /// <summary>A bigger chunk is malformed (measured: 256 KiB in every archive); a chunk's buffers never grow past it.</summary>
    public const int MaxChunk = 4 << 20;
    public const int MaxChunks = 1 << 21;
    /// <summary>The unpacked stream, gaps included, may be at most this many times the file (measured about 1.5): a bound on
    /// what a small file of tiny or far-off chunks makes the carver read.</summary>
    public const int MaxRatio = 64;

    readonly SafeFileHandle h;
    readonly Salts? salts;
    readonly long[] start, at;
    readonly int[] size, packed;
    readonly uint[] key;
    int cached = -1;
    readonly byte[] block, raw;

    /// <summary>The unpacked stream's length.</summary>
    public long Length { get; }
    public int Chunks => start.Length;
    public NixxesReader.Readable Readable => new(Length, Read);

    DecimaArchive(SafeFileHandle h, Salts? salts, int maxChunk, long[] start, long[] at, int[] size, int[] packed, uint[] key)
    {
        (this.h, this.salts, this.start, this.at, this.size, this.packed, this.key) = (h, salts, start, at, size, packed, key);
        Length = start.Length == 0 ? 0 : start[^1] + size[^1];
        block = new byte[maxChunk];
        raw = new byte[maxChunk];
    }

    /// <summary>The magic of the file, 0 when it can't be read.</summary>
    public static uint Magic(string path)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> b = stackalloc byte[4];
            return RandomAccess.Read(h, b, 0) == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(b) : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>The archive, or null when the file isn't one (or can't be read, or is encrypted and
    /// <paramref name="salts"/> don't open it). Its chunk table is charged to <paramref name="meter"/> before it's read.</summary>
    public static DecimaArchive? Open(string path, Salts? salts, NixxesReader.Meter? meter = null) => Open(path, salts, out _, meter);

    /// <param name="why">when null is returned, why, in a few words</param>
    public static DecimaArchive? Open(string path, Salts? salts, out string why, NixxesReader.Meter? meter = null)
    {
        SafeFileHandle h;
        try { h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { why = "can't be read"; return null; }
        try
        {
            if (Parse(h, salts, meter, out why) is { } a) return a;
        }
        catch (Exception e) when (e is IOException or ArgumentException or OverflowException) { why = "can't be read"; }
        h.Dispose();
        return null;
    }

    public const string WrongSize = "its header gives another file size";

    public record struct Header(uint Magic, long FileSize, long DataSize, long Files, uint Chunks, uint MaxChunk);

    /// <summary>The header, decrypted with <paramref name="salts"/> when encrypted (null salts: an encrypted header isn't read).</summary>
    public static Header? ReadHeader(ReadOnlySpan<byte> head, Salts? salts)
    {
        if (head.Length < HeaderSize) return null;
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(head);
        if (magic is not (Plain or Encrypted) || magic == Encrypted && salts == null) return null;
        Span<byte> b = stackalloc byte[32];
        head[8..HeaderSize].CopyTo(b);
        if (magic == Encrypted)
        {
            var k = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
            salts!.Xor(b[..16], k);
            salts.Xor(b[16..], k + 1);
        }
        return new Header(magic, BinaryPrimitives.ReadInt64LittleEndian(b), BinaryPrimitives.ReadInt64LittleEndian(b[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(b[16..]), BinaryPrimitives.ReadUInt32LittleEndian(b[24..]), BinaryPrimitives.ReadUInt32LittleEndian(b[28..]));
    }

    static DecimaArchive? Parse(SafeFileHandle h, Salts? salts, NixxesReader.Meter? meter, out string why)
    {
        var length = RandomAccess.GetLength(h);
        var head = new byte[HeaderSize];
        why = "not a Decima archive";
        if (RandomAccess.Read(h, head, 0) != HeaderSize || ReadHeader(head, salts) is not { } hd) return null;
        var enc = hd.Magic == Encrypted;
        // the file size is the check a wrong salt fails
        why = WrongSize;
        if (hd.FileSize != length) return null;
        // table sizes in subtraction form, so no product of file values wraps
        why = "its header's counts don't fit the file";
        if (hd.Chunks > MaxChunks || hd.MaxChunk is 0 or > MaxChunk || hd.Files < 0 || hd.Files > (length - HeaderSize) / EntrySize - hd.Chunks) return null;
        why = "over the read budget";
        if (meter?.Take(EntrySize * (long)hd.Chunks) == false) return null;
        why = "its chunk table is truncated or out of bounds";
        var tableAt = HeaderSize + EntrySize * hd.Files;
        var n = (int)hd.Chunks;
        var (start, at, size, packed, key) = (new List<long>(), new List<long>(), new List<int>(), new List<int>(), new List<uint>());
        var slice = new byte[EntrySize * Math.Min(4096, n)];
        for (var i = 0; i < n;)
        {
            var k = Math.Min(4096, n - i);
            if (RandomAccess.Read(h, slice.AsSpan(0, EntrySize * k), tableAt + EntrySize * (long)i) != EntrySize * k) return null;
            for (var j = 0; j < k; j++, i++)
            {
                var e = slice.AsSpan(EntrySize * j, EntrySize);
                if (enc) salts!.XorEntry(e);
                long s = BinaryPrimitives.ReadInt64LittleEndian(e), o = BinaryPrimitives.ReadInt64LittleEndian(e[16..]);
                uint z = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]), p = BinaryPrimitives.ReadUInt32LittleEndian(e[24..]);
                // s bounded first, so s + z can't wrap; one chunk's worth of slack for a small archive of a well-compressed chunk
                if (s < 0 || s / MaxRatio > length || z is 0 || z > hd.MaxChunk || s + z - MaxChunk > MaxRatio * length
                    || p is 0 or > MaxChunk || o < HeaderSize || o > length - p) return null;
                start.Add(s); at.Add(o); size.Add((int)z); packed.Add((int)p); key.Add(BinaryPrimitives.ReadUInt32LittleEndian(e[12..]));
            }
        }
        var order = Enumerable.Range(0, n).OrderBy(i => start[i]).ToArray();
        for (var i = 1; i < n; i++)
            if (start[order[i]] < start[order[i - 1]] + size[order[i - 1]]) { why = "its chunks overlap"; return null; }
        why = "";
        return new DecimaArchive(h, enc ? salts : null, Math.Max(size.DefaultIfEmpty().Max(), packed.DefaultIfEmpty().Max()),
            [.. order.Select(i => start[i])], [.. order.Select(i => at[i])], [.. order.Select(i => size[i])], [.. order.Select(i => packed[i])],
            [.. order.Select(i => key[i])]);
    }

    /// <summary>Fills <paramref name="output"/> from <paramref name="from"/>, decompressing only the chunks it covers; bytes no
    /// chunk covers read as zeros. InvalidDataException past the end, or for a chunk that doesn't decompress to its size.</summary>
    public void Read(long from, Span<byte> output)
    {
        if (from < 0 || from > Length || output.Length > Length - from) throw new InvalidDataException("past the archive's end");
        var i = Array.BinarySearch(start, from);
        if (i < 0) i = ~i - 1;
        var pos = from;
        while (output.Length > 0)
        {
            if (i < 0 || pos >= start[i] + size[i]) i++;
            if (pos < start[i])
            {
                var gap = (int)Math.Min(output.Length, start[i] - pos);
                output[..gap].Clear();
                output = output[gap..];
                pos += gap;
                continue;
            }
            var lo = (int)(pos - start[i]);
            var n = Math.Min(output.Length, size[i] - lo);
            Chunk(i).AsSpan(lo, n).CopyTo(output);
            output = output[n..];
            pos += n;
            i++;
        }
    }

    /// <summary>Chunk <paramref name="i"/> unpacked (the one buffer; valid until the next call).</summary>
    byte[] Chunk(int i)
    {
        if (cached == i) return block;
        cached = -1;
        var src = raw.AsSpan(0, packed[i]);
        ReadRaw(src, at[i]);
        if (salts != null) salts.XorChunk(src, PlainEntry(start[i], size[i], key[i]));
        Decompress(raw, packed[i], block, size[i]);
        cached = i;
        return block;
    }

    /// <summary>The smallest chunk stored smaller than it unpacks (Oodle-compressed, not a stored block); null if none.</summary>
    internal (int Chunk, int Packed)? Compressed()
    {
        (int, int)? best = null;
        for (var i = 0; i < Chunks; i++)
            if (packed[i] < size[i] && (best == null || packed[i] < best.Value.Item2)) best = (i, packed[i]);
        return best;
    }

    /// <summary>Chunk <paramref name="i"/> as stored (still encrypted), its unpacked size and the plain entry it's keyed by.</summary>
    internal (byte[] Packed, int Size, byte[] Entry) Raw(int i)
    {
        if ((uint)i >= (uint)Chunks) throw new InvalidDataException("no such chunk");   // the file may have changed since the index was taken
        var b = new byte[packed[i]];
        ReadRaw(b, at[i]);
        return (b, size[i], PlainEntry(start[i], size[i], key[i]));
    }

    void ReadRaw(Span<byte> b, long offset)
    {
        int n;
        try { n = RandomAccess.Read(h, b, offset); }
        catch (IOException e) { throw new InvalidDataException("archive unreadable", e); }
        if (n != b.Length) throw new InvalidDataException("archive truncated");
    }

    /// <summary>The first 16 bytes of a decrypted chunk entry, which chunk data is keyed by.</summary>
    internal static byte[] PlainEntry(long start, int size, uint key)
    {
        var e = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(e, start);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(8), size);
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(12), key);
        return e;
    }

    /// <summary>Decodes an Oodle block to exactly <paramref name="want"/> bytes into <paramref name="into"/>.</summary>
    internal static void Decompress(byte[] src, int count, byte[] into, int want)
    {
        long n = want;
        try
        {
            App.Codecs.Load(zlib: false);
            // CUE4Parse only logs a short decode: the native decoder's count is checked here
            if (OodleHelper.Instance is { } native) n = native.Decompress(src.AsSpan(0, count), into.AsSpan(0, want));
            else Compression.Decompress(src, 0, count, into, 0, want, CompressionMethod.Oodle);
        }
        catch (Exception e) when (e is not InvalidDataException) { throw new InvalidDataException("an Oodle chunk doesn't decompress", e); }
        if (n != want) throw new InvalidDataException("an Oodle chunk doesn't decompress to its size");
    }

    public void Dispose() => h.Dispose();

    /// <summary>The two salts of an encrypted archive: <see cref="Iv"/>, the last 12 bytes of the 16 hashed with a key word
    /// (header and chunk entries), and <see cref="Data"/>, the 16 bytes XORed into the hash of a chunk entry (chunk data). Read
    /// from the game's exe by <see cref="DecimaKeys"/>, never shipped.</summary>
    public sealed class Salts
    {
        public byte[] Iv { get; }
        public byte[] Data { get; }

        public Salts(byte[] iv, byte[] data)
        {
            if (iv.Length != 12 || data.Length != 16) throw new ArgumentException("salts are 12 and 16 bytes");
            (Iv, Data) = (iv, data);
        }

        /// <summary>XORs 16 bytes with Murmur3 of {key, Iv}.</summary>
        public void Xor(Span<byte> b16, uint key)
        {
            Span<byte> iv = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(iv, key);
            Iv.CopyTo(iv[4..]);
            Span<byte> m = stackalloc byte[16];
            Murmur3(iv, m);
            for (var i = 0; i < 16; i++) b16[i] ^= m[i];
        }

        /// <summary>A chunk entry: each half XORed with its own key word (offset 12 of the half), which stays in the clear.</summary>
        public void XorEntry(Span<byte> e32)
        {
            for (var half = 0; half < 32; half += 16)
            {
                var e = e32.Slice(half, 16);
                var k = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
                Xor(e, k);
                BinaryPrimitives.WriteUInt32LittleEndian(e[12..], k);
            }
        }

        /// <summary>Chunk data: XOR with MD5(Murmur3(the plain entry's first 16 bytes) XOR <see cref="Data"/>), repeated.</summary>
        public void XorChunk(Span<byte> data, ReadOnlySpan<byte> plainEntry16)
        {
            Span<byte> k = stackalloc byte[16];
            ChunkKey(plainEntry16, k);
            XorRepeated(data, k);
        }

        internal void ChunkKey(ReadOnlySpan<byte> plainEntry16, Span<byte> key16)
        {
            Span<byte> m = stackalloc byte[16];
            Murmur3(plainEntry16, m);
            for (var i = 0; i < 16; i++) m[i] ^= Data[i];
            MD5.HashData(m, key16);
        }

        internal static void XorRepeated(Span<byte> data, ReadOnlySpan<byte> key16)
        {
            for (var i = 0; i < data.Length; i++) data[i] ^= key16[i & 15];
        }
    }

    /// <summary>MurmurHash3 x64 128 (seed 42) of exactly 16 bytes, as Decima hashes its key blocks.</summary>
    public static void Murmur3(ReadOnlySpan<byte> b16, Span<byte> out16)
    {
        const ulong c1 = 0x87c37b91114253d5, c2 = 0x4cf5ad432745937f;
        ulong h1 = 42, h2 = 42;
        ulong k1 = BinaryPrimitives.ReadUInt64LittleEndian(b16), k2 = BinaryPrimitives.ReadUInt64LittleEndian(b16[8..]);
        k1 *= c1; k1 = BitOperations.RotateLeft(k1, 31); k1 *= c2; h1 ^= k1;
        h1 = BitOperations.RotateLeft(h1, 27); h1 += h2; h1 = h1 * 5 + 0x52dce729;
        k2 *= c2; k2 = BitOperations.RotateLeft(k2, 33); k2 *= c1; h2 ^= k2;
        h2 = BitOperations.RotateLeft(h2, 31); h2 += h1; h2 = h2 * 5 + 0x38495ab5;
        h1 ^= 16; h2 ^= 16;
        h1 += h2; h2 += h1;
        h1 = Fmix(h1); h2 = Fmix(h2);
        h1 += h2; h2 += h1;
        BinaryPrimitives.WriteUInt64LittleEndian(out16, h1);
        BinaryPrimitives.WriteUInt64LittleEndian(out16[8..], h2);
    }

    static ulong Fmix(ulong k)
    {
        k ^= k >> 33; k *= 0xff51afd7ed558ccd;
        k ^= k >> 33; k *= 0xc4ceb9fe1a85ec53;
        return k ^ k >> 33;
    }
}
