using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.Compression;
using K4os.Compression.LZ4;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.NaughtyDog;

/// <summary>A PlayStation archive (PSARC) as Naughty Dog's PC ports ship it, read-only and in memory (measured on The Last of
/// Us Part II Remastered):
///   - the file may be wrapped in DirectStorage blocks ("DSAR": u32 version, u32 block count, u32 header size, u64 unwrapped
///     size, 8 bytes; per block u64 unwrapped offset, u64 file offset, u32 unwrapped size, u32 stored size, u8 codec
///     (0 stored, 3 LZ4), 7 bytes), which unwrap to the PSARC;
///   - PSARC, big-endian: "PSAR", u16 major, u16 minor, codec ("zlib", "oodl"), u32 TOC length, u32 entry size, u32 entry
///     count, u32 block size, u32 flags; entries of MD5(name)[16], u32 first block, u40 size, u40 offset; then one stored size
///     per block (as many bytes as the block size needs; 0 = a whole block), a block stored whole when its stored size is its
///     unpacked size. Entry 0 is the manifest: the names, '\n' or '\0' apart, matched to entries by MD5 (of the name as
///     written, upper- or lower-cased).
/// Not thread-safe: one instance per thread.</summary>
public sealed class Psarc : IDisposable
{
    public sealed record Entry(int Index, string Name, long Size, int Block, long Offset);

    const int MaxBlock = 16 << 20;
    public const long MaxEntry = 1L << 30;

    readonly SafeFileHandle h;
    readonly Dsar? dsar;
    readonly string codec;
    readonly int blockSize;
    readonly int[] stored;

    public string Path { get; }
    public IReadOnlyList<Entry> Entries { get; }

    public Psarc(string path)
    {
        Path = path;
        h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            var head = new byte[32];
            if (RandomAccess.Read(h, head, 0) < 32) throw new InvalidDataException("not a PSARC");
            if (head.AsSpan().StartsWith("DSAR"u8)) { dsar = new Dsar(h); Read(0, head); }
            if (!head.AsSpan().StartsWith("PSAR"u8)) throw new InvalidDataException("not a PSARC");
            codec = Encoding.ASCII.GetString(head, 8, 4);
            int toc = I(head, 12), entrySize = I(head, 16), count = I(head, 20);
            blockSize = I(head, 24);
            if (toc < 32 || entrySize < 30 || count < 1 || (long)entrySize * count > toc - 32 || blockSize is < 1 or > MaxBlock)
                throw new InvalidDataException("bad PSARC header");
            var t = new byte[toc];
            Read(0, t);
            var raw = new (byte[] Md5, int Block, long Size, long Offset)[count];
            for (var i = 0; i < count; i++)
            {
                var e = t.AsSpan(32 + i * entrySize);
                raw[i] = (e[..16].ToArray(), I(e, 16), U40(e[20..]), U40(e[25..]));
            }
            var width = 1;
            while (width < 4 && 1L << (8 * width) < blockSize) width++;
            var at = 32 + count * entrySize;
            stored = new int[(toc - at) / width];
            for (var i = 0; i < stored.Length; i++)
            {
                var v = 0;
                for (var k = 0; k < width; k++) v = v << 8 | t[at + i * width + k];
                stored[i] = v;
            }
            var names = new Dictionary<string, string>();
            if (raw[0].Size <= MaxEntry)
                foreach (var n in Encoding.UTF8.GetString(Unpack(raw[0].Block, raw[0].Size, raw[0].Offset)).Split(['\n', '\0'], StringSplitOptions.RemoveEmptyEntries))
                    foreach (var v in new[] { n, n.ToUpperInvariant(), n.ToLowerInvariant() })
                        names.TryAdd(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(v))), n);
            Entries = [.. raw.Skip(1).Select((e, i) => new Entry(i + 1, names.GetValueOrDefault(Convert.ToHexStringLower(e.Md5)) ?? Convert.ToHexStringLower(e.Md5), e.Size, e.Block, e.Offset))];
        }
        catch { h.Dispose(); throw; }
    }

    /// <summary>The PSARC magic at the start of the file, or under its DSAR wrapper.</summary>
    public static bool Is(string path)
    {
        try { using var p = new Psarc(path); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException) { return false; }
    }

    /// <summary>The entry's bytes, unpacked.</summary>
    public byte[] Read(Entry e) => e.Size > MaxEntry ? throw new InvalidDataException($"{e.Name}: {e.Size} bytes, over {MaxEntry}") : Unpack(e.Block, e.Size, e.Offset);

    /// <summary>The entry unpacked into the start of <paramref name="buffer"/>, which is replaced when too small (a caller
    /// reading many entries reuses one); returns the entry's size.</summary>
    public int Read(Entry e, ref byte[] buffer)
    {
        if (e.Size > MaxEntry) throw new InvalidDataException($"{e.Name}: {e.Size} bytes, over {MaxEntry}");
        if (buffer.Length < e.Size) buffer = new byte[(e.Size + 0xFFFFF) & ~0xFFFFFL];   // rounded up to 1 MiB: grows a few times, not per entry
        Unpack(e.Block, e.Size, e.Offset, buffer);
        return (int)e.Size;
    }

    byte[] Unpack(int block, long size, long offset)
    {
        var o = new byte[size];
        Unpack(block, size, offset, o);
        return o;
    }

    byte[]? packedBlock;

    void Unpack(int block, long size, long offset, byte[] o)
    {
        var buf = packedBlock ??= new byte[blockSize];
        for (long done = 0; done < size; block++)
        {
            if ((uint)block >= stored.Length) throw new InvalidDataException("PSARC entry past its block table");
            var want = (int)Math.Min(blockSize, size - done);
            var z = stored[block] == 0 ? blockSize : stored[block];
            var dst = o.AsSpan((int)done, want);
            if (z == want) Read(offset, dst);
            else
            {
                Read(offset, buf.AsSpan(0, z));
                Inflate(buf, z, dst);
            }
            offset += z;
            done += want;
        }
    }

    void Inflate(byte[] src, int n, Span<byte> dst)
    {
        switch (codec)
        {
            case "zlib":
                using (var s = new ZLibStream(new MemoryStream(src, 0, n), CompressionMode.Decompress)) s.ReadExactly(dst);
                return;
            case "oodl":
                App.Codecs.Load(zlib: false);   // CUE4Parse's Oodle, never the game's own DLL
                Compression.Decompress(src[..n], dst.Length, CompressionMethod.Oodle).CopyTo(dst);
                return;
            default: throw new NotSupportedException($"PSARC codec {codec}");
        }
    }

    void Read(long at, Span<byte> dst)
    {
        if (dsar != null) { dsar.Read(at, dst); return; }
        if (RandomAccess.Read(h, dst, at) != dst.Length) throw new InvalidDataException("PSARC truncated");
    }

    static int I(ReadOnlySpan<byte> b, int at) => (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(b[at..]), int.MaxValue);
    static long U40(ReadOnlySpan<byte> b) => (long)b[0] << 32 | BinaryPrimitives.ReadUInt32BigEndian(b[1..]);

    public void Dispose() => h.Dispose();

    /// <summary>The DirectStorage wrapper: unwrapped bytes on demand, the last block kept.</summary>
    sealed class Dsar
    {
        readonly SafeFileHandle h;
        readonly (long At, long Stored, int Size, int StoredSize, byte Codec)[] blocks;
        int last = -1;
        byte[] lastBytes = [], src = [];   // reused: blocks are read one after another

        public Dsar(SafeFileHandle h)
        {
            this.h = h;
            var head = new byte[32];
            RandomAccess.Read(h, head, 0);
            var n = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8));
            if (n < 1 || n > 1 << 24) throw new InvalidDataException("bad DSAR header");
            var t = new byte[n * 32];
            if (RandomAccess.Read(h, t, 32) != t.Length) throw new InvalidDataException("DSAR truncated");
            blocks = new (long, long, int, int, byte)[n];
            for (var i = 0; i < n; i++)
            {
                var e = t.AsSpan(i * 32);
                blocks[i] = (BinaryPrimitives.ReadInt64LittleEndian(e), BinaryPrimitives.ReadInt64LittleEndian(e[8..]),
                    BinaryPrimitives.ReadInt32LittleEndian(e[16..]), BinaryPrimitives.ReadInt32LittleEndian(e[20..]), e[24]);
                if (blocks[i].Size is < 0 or > MaxBlock || blocks[i].StoredSize is < 0 or > MaxBlock || i > 0 && blocks[i].At != blocks[i - 1].At + blocks[i - 1].Size)
                    throw new InvalidDataException("bad DSAR block table");
            }
        }

        public void Read(long at, Span<byte> dst)
        {
            var i = Find(at);
            while (dst.Length > 0)
            {
                if ((uint)i >= blocks.Length) throw new InvalidDataException("read past the DSAR's end");
                var b = Block(i);
                var s = (int)(at - blocks[i].At);
                var n = Math.Min(dst.Length, blocks[i].Size - s);
                b.AsSpan(s, n).CopyTo(dst);
                dst = dst[n..];
                at += n;
                i++;
            }
        }

        int Find(long at)
        {
            int lo = 0, hi = blocks.Length - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (blocks[mid].At <= at) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        byte[] Block(int i)
        {
            if (i == last) return lastBytes;
            var (_, pos, size, storedSize, codec) = blocks[i];
            if (codec is not (0 or 3) || codec == 0 && storedSize != size) throw new NotSupportedException($"DSAR codec {codec}");
            if (lastBytes.Length < size) lastBytes = new byte[size];
            last = -1;
            if (codec == 0) { if (RandomAccess.Read(h, lastBytes.AsSpan(0, size), pos) != size) throw new InvalidDataException("DSAR truncated"); }
            else
            {
                if (src.Length < storedSize) src = new byte[storedSize];
                if (RandomAccess.Read(h, src.AsSpan(0, storedSize), pos) != storedSize) throw new InvalidDataException("DSAR truncated");
                if (LZ4Codec.Decode(src.AsSpan(0, storedSize), lastBytes.AsSpan(0, size)) != size) throw new InvalidDataException("bad LZ4 block");
            }
            last = i;
            return lastBytes;
        }
    }
}
