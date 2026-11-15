using System.Buffers.Binary;
using System.IO.Compression;
using K4os.Compression.LZ4;
using Microsoft.Win32.SafeHandles;
using ZstdSharp;

namespace SCSKiller.Core.Frostbite;

/// <summary>Frostbite's cas_NN.cas archives: entries back to back, each a run of blocks with an 8-byte header:
///   - u32 big endian: the block's decompressed size in the low 24 bits, the top byte set when zstd decodes it with the
///     game's dictionary;
///   - a codec byte: 0x00 stored, 0x02 zlib, 0x09 LZ4, 0x0F zstd, 0x11 / 0x15 / 0x19 Oodle (bit 7: an obfuscated block);
///   - a flags byte 0x7N, N the bits 16-19 of the stored size; u16 big endian, its low bits.
/// The catalogs and tables that place entries (cas.cat, .toc/.sb) differ per generation and some are obfuscated: they
/// aren't read. A walk follows the headers from where it starts and, where one doesn't parse (a patch delta, an encrypted
/// entry), looks for the next block that decodes.</summary>
static class Cas
{
    /// <summary>The bounds on a block: Frostbite writes 64 KB ones, a stored size is 20 bits.</summary>
    public const int MaxRaw = 1 << 22, MaxStored = 1 << 20;
    const int Window = 4 << 20;

    /// <param name="at">the block's header offset in the file</param>
    /// <param name="raw">its bytes, valid only during the call</param>
    /// <param name="broken">the blocks before it didn't run up to it, or one of them didn't decode</param>
    /// <returns>false stops the walk</returns>
    public delegate bool Block(long at, ReadOnlySpan<byte> raw, bool broken);

    public static bool Header(ReadOnlySpan<byte> h, out int raw, out int stored, out byte codec, out bool decodable)
    {
        var size = BinaryPrimitives.ReadUInt32BigEndian(h);
        raw = (int)(size & 0xFFFFFF);
        codec = h[4];
        stored = (h[5] & 0x0F) << 16 | BinaryPrimitives.ReadUInt16BigEndian(h[6..]);
        decodable = size >> 24 == 0 && codec is 0x00 or 0x02 or 0x09 or 0x0F or 0x11 or 0x15 or 0x19;
        return (h[5] & 0xF0) == 0x70 && (codec & 0x7F) is 0x00 or 0x02 or 0x09 or 0x0F or 0x11 or 0x15 or 0x19
            && raw is > 0 and <= MaxRaw && stored > 0 && (codec != 0 || stored == raw);
    }

    sealed class Decoders : IDisposable
    {
        readonly Decompressor zstd = new();
        byte[] oodleIn = [], oodleOut = [];

        public bool Decode(byte codec, byte[] src, int off, int count, Span<byte> dst)
        {
            try
            {
                switch (codec)
                {
                    case 0x00:
                        src.AsSpan(off, count).CopyTo(dst);
                        return true;
                    case 0x02:
                        using (var z = new ZLibStream(new MemoryStream(src, off, count), CompressionMode.Decompress))
                        {
                            z.ReadExactly(dst);
                            return z.ReadByte() == -1;
                        }
                    case 0x09: return LZ4Codec.Decode(src.AsSpan(off, count), dst) == dst.Length;
                    case 0x0F: return zstd.Unwrap(src.AsSpan(off, count), dst) == dst.Length;
                    default:
                        if (oodleIn.Length < count) oodleIn = new byte[Math.Max(count, MaxStored)];
                        if (oodleOut.Length < dst.Length) oodleOut = new byte[Math.Max(dst.Length, 1 << 16)];
                        src.AsSpan(off, count).CopyTo(oodleIn);
                        Decima.DecimaArchive.Decompress(oodleIn, count, oodleOut, dst.Length);
                        oodleOut.AsSpan(0, dst.Length).CopyTo(dst);
                        return true;
                }
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException or ZstdException or ArgumentException or IndexOutOfRangeException) { return false; }
        }

        public void Dispose() => zstd.Dispose();
    }

    /// <summary>Walks the file's blocks from <paramref name="from"/> until its end, <paramref name="budget"/> bytes, or the
    /// sink stops it. <paramref name="resync"/> false: stops where a header doesn't parse instead of looking further.
    /// Returns the bytes walked.</summary>
    public static long Walk(SafeFileHandle h, long from, Block sink, CancellationToken ct, long budget = long.MaxValue, bool resync = true)
    {
        var len = RandomAccess.GetLength(h);
        var win = new byte[Window];
        long winAt = 0;
        var winLen = 0;
        var raw = new byte[MaxRaw];
        using var dec = new Decoders();
        bool Load(long pos, int n)
        {
            if (pos >= winAt && pos + n <= winAt + winLen) return true;
            if (pos + n > len) return false;
            winAt = pos;
            winLen = RandomAccess.Read(h, win.AsSpan(0, (int)Math.Min(win.Length, len - pos)), pos);
            return winLen >= n;
        }
        long pos = from, end = budget >= len - from ? len : from + budget;
        bool chained = true, broken = false;
        while (pos + 8 <= len && pos < end)
        {
            ct.ThrowIfCancellationRequested();
            if (!Load(pos, 8)) break;
            if (Header(win.AsSpan((int)(pos - winAt), 8), out var size, out var stored, out var codec, out var decodable) && Load(pos, 8 + stored))
            {
                var off = (int)(pos - winAt) + 8;
                if (decodable && dec.Decode(codec, win, off, stored, raw.AsSpan(0, size)))
                {
                    if (!sink(pos, raw.AsSpan(0, size), broken)) return pos + 8 + stored - from;
                    (pos, chained, broken) = (pos + 8 + stored, true, false);
                    continue;
                }
                // a header met by following the chain stands: its block is left out (the game's dictionary, an obfuscated one)
                if (chained)
                {
                    (pos, broken) = (pos + 8 + stored, true);
                    continue;
                }
            }
            if (!resync) break;
            (chained, broken) = (false, true);
            pos = Next(pos + 1);
        }
        return Math.Min(pos, len) - from;

        // the next offset whose codec and flags bytes could start a header
        long Next(long p)
        {
            while (p + 8 <= len && p < end)
            {
                ct.ThrowIfCancellationRequested();
                if (!Load(p, 8)) return len;
                var s = win.AsSpan((int)(p - winAt), winLen - (int)(p - winAt));
                for (var i = 0; i + 8 <= s.Length; i++)
                    if ((s[i + 5] & 0xF0) == 0x70 && (s[i + 4] & 0x7F) is 0x00 or 0x02 or 0x09 or 0x0F or 0x11 or 0x15 or 0x19) return p + i;
                p += Math.Max(1, s.Length - 7);
            }
            return len;
        }
    }
}
