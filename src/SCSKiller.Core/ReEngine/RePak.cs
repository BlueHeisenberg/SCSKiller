using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using Microsoft.Win32.SafeHandles;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace SCSKiller.Core.ReEngine;

/// <summary>A Capcom RE Engine package (re_chunk_000.pak, its .patch_NNN and .sub_NNN paks, dlc\*.pak), read-only, as the
/// community tools read it (eigeen/ree-pak-rs, ree-pak-core: spec/header.rs, spec/entry.rs, read/mod.rs,
/// read/chunk_table.rs, pak/flag.rs; Ekey/REE.PAK.Tool):
///   - header, 16 bytes: "KPKA", u8 major, u8 minor, u16 features, u32 entry count, u32 fingerprint;
///   - entries: v4 48 bytes (u32 name hash lower, upper, u64 offset, u64 packed size, u64 size, u64 attributes, u64
///     checksum); v2.0 24 bytes (u64 offset, u64 size, u32 hash lower, upper; stored);
///   - then, by feature bit: 0x10 a u32, 0x04 9 bytes, 0x40 a remap table (u32 count, u32 unused, count x 16 bytes;
///     skipped, as REE.PAK.Tool and ree-pak-rs's unmerged remap patch do: entries index the 0x20 table, not this one),
///     0x08 128 bytes the table key is made from (the entry table is XOR-encrypted, see <see cref="Key"/>), 0x20 a chunk
///     table (u32 block size, u32 count, count x (u32 start low, u32 meta = packed length &lt;&lt; 10 | flags));
///   - attributes: bits 0-3 compression (0 stored, 1 raw deflate, 2 zstd), 16-23 resource encryption, 24 the offset is
///     the first chunk's index (fixed-size chunks, each zstd or stored when its packed length is the block size).
/// Names are murmur3 hashes of paths; nothing here needs them. Verified on PRAGMATA (v4.2, features 0x28 and 0x08);
/// v2.0, 0x40 (Dragon's Dogma 2: 0x68) and resource encryption (other titles) are not verified: the latter is refused
/// per entry.</summary>
public sealed class RePak : IDisposable
{
    public readonly record struct Entry(ulong Hash, long Offset, long Packed, long Size, ulong Attr)
    {
        public int Compression => (int)(Attr & 0xF);
        public int Encryption => (int)(Attr >> 16 & 0xFF);
        public bool Chunked => (Attr & 1UL << 24) != 0;
    }

    public const ushort ExtraU32 = 0x10, ExtraData = 0x04, EncryptedTable = 0x08, ChunkTable = 0x20, RemapTable = 0x40;

    /// <summary>The most bytes the entry and chunk tables may claim, checked before they're read. PRAGMATA's largest: 3.9 MB
    /// of entries (81,661), 0.5 MB of chunks (65,322 of 512 KB).</summary>
    public const int MaxTable = 64 << 20, MaxChunkTable = 32 << 20, MaxBlock = 16 << 20;

    /// <summary>The most bytes one read may return, and the largest zstd window it decodes with. PRAGMATA's largest: a 57 MB
    /// shader file, a 4 MB window (2^22).</summary>
    public const int MaxEntry = 256 << 20, MaxWindowLog = 25;

    public string Path { get; }
    public int Major { get; }
    public int Minor { get; }
    public ushort Features { get; }
    public IReadOnlyList<Entry> Entries { get; }

    readonly SafeFileHandle h;
    readonly long length;
    readonly int blockSize;
    readonly (long Start, int Packed)[] chunks = [];

    RePak(string path, IReadOnlyList<BigInteger> moduli)
    {
        Path = path;
        h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            var len = length = RandomAccess.GetLength(h);
            var hd = Bytes(0, 16);
            if (!hd.AsSpan(0, 4).SequenceEqual("KPKA"u8)) throw new InvalidDataException("not a KPKA package");
            (Major, Minor, Features) = (hd[4], hd[5], BinaryPrimitives.ReadUInt16LittleEndian(hd.AsSpan(6)));
            var count = BinaryPrimitives.ReadInt32LittleEndian(hd.AsSpan(8));
            if (Major is not (2 or 4) || Minor > 2) throw new InvalidDataException($"package version {Major}.{Minor} is not supported");
            if ((Features & ~(ExtraU32 | ExtraData | EncryptedTable | ChunkTable | RemapTable)) != 0) throw new InvalidDataException($"unknown package features 0x{Features:x}");
            var size = Major == 2 && Minor == 0 ? 24 : 48;
            if ((uint)count * (long)size > MaxTable) throw new InvalidDataException($"entry table over {MaxTable >> 20} MB");
            if (count * size > len - 16) throw new InvalidDataException("entry table past the end of the file");
            var stored = Bytes(16, count * size);
            var table = stored;
            long at = 16 + stored.Length + ((Features & ExtraU32) != 0 ? 4 : 0) + ((Features & ExtraData) != 0 ? 9 : 0);
            if ((Features & RemapTable) != 0)
            {
                // the second u32: a block size to REE.PAK.Tool, the count's high half to ree-pak-rs; neither tool uses it
                var n = BinaryPrimitives.ReadUInt32LittleEndian(Bytes(at, 8));
                if (n > (len - at - 8) / 16) throw new InvalidDataException("bad remap table");
                at += 8 + 16L * n;
            }
            if ((Features & EncryptedTable) != 0)
            {
                var raw = Bytes(at, 128);
                at += 128;
                // the modulus that opens it: every entry's sizes are plausible (a wrong key leaves random 64-bit values)
                table = moduli.Select(m => { var t = stored.ToArray(); Xor(t, Key(raw, m)); return t; })
                    .FirstOrDefault(t => Enumerable.Range(0, count).All(i => BinaryPrimitives.ReadUInt64LittleEndian(t.AsSpan(i * size + (size == 24 ? 0 : 16))) < 1UL << 40
                        && BinaryPrimitives.ReadUInt64LittleEndian(t.AsSpan(i * size + (size == 24 ? 8 : 24))) < 1UL << 40))
                    ?? throw new InvalidDataException(NoModulus);
            }
            if ((Features & ChunkTable) != 0)
            {
                var ct = Bytes(at, 8);
                blockSize = BinaryPrimitives.ReadInt32LittleEndian(ct);
                var n = BinaryPrimitives.ReadInt32LittleEndian(ct.AsSpan(4));
                if ((uint)n * 8L > MaxChunkTable) throw new InvalidDataException($"chunk table over {MaxChunkTable >> 20} MB");
                if (blockSize is <= 0 or > MaxBlock || 8L * n > len - at - 8) throw new InvalidDataException("bad chunk table");
                var raw = Bytes(at + 8, n * 8);
                chunks = new (long, int)[n];
                long high = 0;
                uint prev = n > 0 ? BinaryPrimitives.ReadUInt32LittleEndian(raw) : 0;
                for (var i = 0; i < n; i++)
                {
                    var lo = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8 * i));
                    if (lo < prev) high += 1L << 32; // starts are u32s that wrap at 4 GiB and only grow
                    chunks[i] = (high | lo, (int)(BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8 * i + 4)) >> 10));
                    prev = lo;
                }
            }
            var entries = new Entry[count];
            for (var i = 0; i < count; i++)
            {
                var at0 = i * size;
                ulong U64(int o) => BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(at0 + o));
                entries[i] = size == 24
                    ? new(U64(16), (long)U64(0), (long)U64(8), (long)U64(8), 0)
                    : new(U64(0), (long)U64(8), (long)U64(16), (long)U64(24), U64(32));
            }
            Entries = entries;
        }
        catch { h.Dispose(); throw; }
    }

    /// <summary>Opens the package and reads its entry table; InvalidDataException when it isn't one this reads, or its table
    /// is encrypted and none of <paramref name="moduli"/> opens it.</summary>
    public static RePak Open(string path, IReadOnlyList<BigInteger>? moduli = null) => new(path, moduli ?? []);

    public const string NoModulus = "the entry table is encrypted and no table-key modulus opens it";

    public void Dispose() => h.Dispose();

    /// <summary>The XOR key from the package's 128 stored bytes: raw^65537 mod <paramref name="modulus"/> (the game's public
    /// table-key modulus: not shipped, see ReEngineReader), little endian.</summary>
    public static byte[] Key(ReadOnlySpan<byte> raw, BigInteger modulus)
    {
        var k = BigInteger.ModPow(new BigInteger(raw, isUnsigned: true), 65537, modulus).ToByteArray(isUnsigned: true);
        if (k.Length < 32) Array.Resize(ref k, 32);
        return k;
    }

    /// <summary>b[i] ^= i + k[i % 32] * k[i % 29] (low byte); its own inverse.</summary>
    public static void Xor(Span<byte> b, byte[] k)
    {
        for (var i = 0; i < b.Length; i++) b[i] ^= (byte)(i + k[i % 32] * k[i % 29]);
    }

    /// <summary>The entry's bytes, decompressed, at most <paramref name="max"/> of them (a prefix decodes only what it
    /// needs). InvalidDataException for a resource-encrypted entry, an unknown compression or more than <see cref="MaxEntry"/>
    /// bytes asked for.</summary>
    public byte[] Read(Entry e, int max = int.MaxValue)
    {
        if (e.Encryption != 0) throw new InvalidDataException($"resource-encrypted entry (type {e.Encryption})");
        var total = e.Chunked && e.Size == 0 ? e.Packed : e.Size;
        if (e.Offset < 0 || e.Packed < 0 || total < 0 || !e.Chunked && e.Packed > length - e.Offset) throw new InvalidDataException("entry outside the file");
        if (total > Array.MaxLength || e.Chunked && total > Math.Max(chunks.Length - e.Offset, 0) * (long)blockSize) throw new InvalidDataException("entry too large");
        var stored = e.Compression == 0 && !e.Chunked;
        // a stored entry is never longer than its stored bytes, whatever its size says
        var want = (int)Math.Min(Math.Min(total, max), stored ? e.Packed : total);
        if (want > MaxEntry) throw new InvalidDataException($"entry over {MaxEntry >> 20} MB");
        using Stream s = e.Chunked ? new Chunks(this, e.Offset) : e.Compression switch
        {
            0 => new Region(h, e.Offset, e.Packed),
            1 => new DeflateStream(new Region(h, e.Offset, e.Packed), CompressionMode.Decompress),
            2 => Zstd(new Region(h, e.Offset, e.Packed)),
            var c => throw new InvalidDataException($"unknown compression {c}"),
        };
        // decoded bytes grow the buffer, not the size field: a few bytes claiming 256 MB allocate 1 MB
        var b = new byte[stored ? want : Math.Min(want, 1 << 20)];
        var n = 0;
        while ((n += s.ReadAtLeast(b.AsSpan(n), b.Length - n, throwOnEndOfStream: false)) == b.Length && n < want)
            Array.Resize(ref b, (int)Math.Min(want, 2L * b.Length));
        return n == b.Length ? b : b[..n];
    }

    static DecompressionStream Zstd(Stream s)
    {
        var z = new DecompressionStream(s);
        z.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, MaxWindowLog);
        return z;
    }

    byte[] Bytes(long at, int n)
    {
        var b = new byte[n];
        if (RandomAccess.Read(h, b, at) != n) throw new InvalidDataException($"{System.IO.Path.GetFileName(Path)}: truncated at {at}");
        return b;
    }

    /// <summary>A read-only window of the file; the handle is shared, so readers on other threads don't interfere.</summary>
    sealed class Region(SafeFileHandle h, long start, long length) : Source
    {
        long pos;
        public override int Read(Span<byte> buffer)
        {
            var n = (int)Math.Min(buffer.Length, length - pos);
            if (n <= 0) return 0;
            n = RandomAccess.Read(h, buffer[..n], start + pos);
            pos += n;
            return n;
        }
    }

    /// <summary>A chunked entry's bytes from chunk <paramref name="next"/> on, a block at a time; past the chunk table, an error.</summary>
    sealed class Chunks(RePak pak, long next) : Source
    {
        readonly Decompressor zstd = new();
        byte[] block = [];
        int pos;

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            if (pos == block.Length)
            {
                if (next >= pak.chunks.Length) throw new InvalidDataException($"chunk {next} past the chunk table");
                if (block.Length == 0) block = new byte[pak.blockSize];
                var (start, packed) = pak.chunks[next];
                var src = pak.Bytes(start, packed);
                if (packed == pak.blockSize) src.CopyTo(block, 0);
                else if (zstd.Unwrap(src, block) != pak.blockSize) throw new InvalidDataException($"chunk {next} doesn't decode to the block size");
                (next, pos) = (next + 1, 0);
            }
            var n = Math.Min(buffer.Length, block.Length - pos);
            block.AsSpan(pos, n).CopyTo(buffer);
            pos += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) zstd.Dispose();
            base.Dispose(disposing);
        }
    }

    abstract class Source : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
