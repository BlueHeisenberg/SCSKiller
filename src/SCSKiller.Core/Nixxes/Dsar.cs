using System.Buffers.Binary;
using K4os.Compression.LZ4;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.Nixxes;

/// <summary>Nixxes' block-compressed wrapper (DSAR) around a game's archives, read at random. Header: "DSAR", u16 major,
/// u16 minor, u32 block count, u32 header size, u64 unpacked size, 8 reserved bytes. Then a 32-byte record per block: u64 unpacked offset,
/// u64 file offset, u32 size, u32 packed size, u8 codec, 7 bytes of padding. Codec 3 is a raw LZ4 block, 254 a gap
/// that reads as zeros (nothing stored), 0 a stored block. Codec 2 (GDeflate, for DirectStorage) isn't decoded: a read that
/// needs one throws. Measured: the blocks are contiguous, LZ4 ones at most 320 KB, stored ones up to 900 MB.</summary>
public sealed class Dsar : IDisposable
{
    /// <summary>A bigger compressed block isn't decompressed, so a read never allocates more; stored blocks are read in place.</summary>
    public const int MaxBlock = 4 << 20;
    /// <summary>More block records is malformed (measured at most 58,763, a 1.9 MB table); the table is read and checked
    /// 4,096 records at a time, so a bad count allocates only what the records before it justify.</summary>
    public const int MaxBlocks = 1 << 18;
    const int Stored = 0, Lz4 = 3, Gap = 254;

    readonly SafeFileHandle h;
    readonly long[] start, at;
    readonly int[] size, packed;
    readonly byte[] codec;
    int cached = -1;
    byte[] block = [];

    /// <summary>The unpacked stream's length.</summary>
    public long Length { get; }

    Dsar(SafeFileHandle h, long length, long[] start, long[] at, int[] size, int[] packed, byte[] codec) =>
        (this.h, Length, this.start, this.at, this.size, this.packed, this.codec) = (h, length, start, at, size, packed, codec);

    /// <summary>The file as a DSAR stream, or null when it isn't one (or can't be read). Its block table is charged to
    /// <paramref name="meter"/> before it's read; null when it can't pay for it.</summary>
    public static Dsar? Open(string path, NixxesReader.Meter? meter = null)
    {
        SafeFileHandle h;
        try { h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var d = Open(h, meter);
        if (d == null) h.Dispose();
        return d;
    }

    /// <summary>Over an open handle, which the result then owns; null (the handle left open) when it isn't a DSAR file.</summary>
    public static Dsar? Open(SafeFileHandle h, NixxesReader.Meter? meter = null)
    {
        try { return Parse(h, meter); }
        catch (Exception e) when (e is IOException or EndOfStreamException or ArgumentException or OverflowException) { return null; }
    }

    static Dsar? Parse(SafeFileHandle h, NixxesReader.Meter? meter)
    {
        var fileLength = RandomAccess.GetLength(h);
        var head = new byte[32];
        if (RandomAccess.Read(h, head, 0) != 32 || !head.AsSpan(0, 4).SequenceEqual("DSAR"u8)) return null;
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8)), headerSize = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12));
        var length = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(16));
        if (n is 0 or > MaxBlocks || 32 + 32L * n > headerSize || headerSize > fileLength || length <= 0 || meter?.Take(32L * n) == false) return null;
        var (start, at, size, packed, codec) = (new List<long>(), new List<long>(), new List<int>(), new List<int>(), new List<byte>());
        var chunk = new byte[32 * Math.Min(4096, (int)n)];
        var end = 0L;
        for (var i = 0; i < n;)
        {
            var k = (int)Math.Min(4096, n - i);
            if (RandomAccess.Read(h, chunk.AsSpan(0, 32 * k), 32 + 32L * i) != 32 * k) return null;
            for (var j = 0; j < k; j++, i++)
            {
                var r = chunk.AsSpan(32 * j);
                long s = BinaryPrimitives.ReadInt64LittleEndian(r), o = BinaryPrimitives.ReadInt64LittleEndian(r[8..]);
                uint z = BinaryPrimitives.ReadUInt32LittleEndian(r[16..]), p = BinaryPrimitives.ReadUInt32LittleEndian(r[20..]);
                // subtraction-form bounds: no sum of two file values can wrap
                if (s < end || s > length || z is 0 or > int.MaxValue || z > length - s || p > int.MaxValue
                    || o < 0 || o > fileLength || p > fileLength - o || r[24] == Stored && p != z) return null;
                start.Add(s); at.Add(o); size.Add((int)z); packed.Add((int)p); codec.Add(r[24]);
                end = s + z;
            }
        }
        return new Dsar(h, length, [.. start], [.. at], [.. size], [.. packed], [.. codec]);
    }

    /// <summary>Fills <paramref name="output"/> from <paramref name="from"/>, decompressing only the blocks it covers; a
    /// range no block covers reads as zeros. InvalidDataException past the end, or for a block that can't be decoded.</summary>
    public void Read(long from, Span<byte> output)
    {
        if (from < 0 || from > Length || output.Length > Length - from) throw new InvalidDataException("past the DSAR stream's end");
        var i = Array.BinarySearch(start, from);
        if (i < 0) i = ~i - 1;
        var pos = from;
        while (output.Length > 0)
        {
            if (i < 0 || i < start.Length && pos >= start[i] + size[i]) i++;   // past block i: the next one, or the gap before it
            var next = i < start.Length ? start[i] : Length;
            if (pos < next)
            {
                var gap = (int)Math.Min(output.Length, next - pos);
                output[..gap].Clear();
                output = output[gap..];
                pos += gap;
                continue;
            }
            var lo = pos - start[i];
            var n = (int)Math.Min(output.Length, size[i] - lo);
            switch (codec[i])
            {
                case Gap: output[..n].Clear(); break;
                case Stored:
                    if (RandomAccess.Read(h, output[..n], at[i] + lo) != n) throw new InvalidDataException("DSAR file truncated");
                    break;
                default: Block(i).AsSpan((int)lo, n).CopyTo(output); break;
            }
            output = output[n..];
            pos += n;
            i++;
        }
    }

    byte[] Block(int i)
    {
        if (cached == i) return block;
        if (codec[i] != Lz4) throw new InvalidDataException(codec[i] == 2 ? "a GDeflate block (not decoded)" : $"a block of unknown codec {codec[i]}");
        if (size[i] > MaxBlock || packed[i] > MaxBlock) throw new InvalidDataException("an LZ4 block over " + (MaxBlock >> 20) + " MB");
        var raw = new byte[packed[i]];
        if (RandomAccess.Read(h, raw, at[i]) != raw.Length) throw new InvalidDataException("DSAR file truncated");
        var b = new byte[size[i]];
        if (LZ4Codec.Decode(raw, b) != b.Length) throw new InvalidDataException("an LZ4 block doesn't decompress to its size");
        (cached, block) = (i, b);
        return b;
    }

    public void Dispose() => h.Dispose();
}
