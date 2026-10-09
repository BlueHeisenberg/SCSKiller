using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.Compression;
using Microsoft.Win32.SafeHandles;

namespace SCSKiller.Core.FromSoft;

/// <summary>FromSoftware's containers, read-only and in memory (layouts as documented by SoulsFormats,
/// github.com/soulsmods/SoulsFormatsNEXT, and UXM Selective Unpack; re-implemented here, nothing copied):
///   - BHD5 (the archive header next to each .bdt): RSA-"encrypted" with the game's private key, so the published public key
///     decrypts it (raw RSA, 256-byte blocks -> 255 bytes); buckets of file headers (name hash, size, offset in the .bdt,
///     optional AES-128-ECB key + encrypted ranges);
///   - DCX: one compressed file (DFLT = zlib, KRAK = Oodle Kraken, ZSTD), big-endian header, payload at 0x4C;
///   - BND4: a binder of named files.</summary>
public static class Souls
{
    /// <param name="AesRanges">[start, end) byte ranges of the entry encrypted with <paramref name="AesKey"/></param>
    public sealed record Entry(ulong Hash, long Offset, long Size, byte[]? AesKey, (long Start, long End)[] AesRanges);

    /// <summary>Raw RSA with the public key (PKCS#1 RSAPublicKey DER, base64: a PEM "RSA PUBLIC KEY" body): c^e mod n per
    /// 256-byte block, 255 bytes out.</summary>
    public static byte[] DecryptBhd(ReadOnlySpan<byte> file, string key)
    {
        using var rsa = RSA.Create();
        rsa.ImportRSAPublicKey(Convert.FromBase64String(key), out _);
        var p = rsa.ExportParameters(false);
        BigInteger n = new(p.Modulus, isUnsigned: true, isBigEndian: true), e = new(p.Exponent, isUnsigned: true, isBigEndian: true);
        int inSize = p.Modulus!.Length, outSize = inSize - 1;
        var input = file.ToArray();
        var output = new byte[input.Length / inSize * outSize];
        Parallel.For(0, input.Length / inSize, new ParallelOptions { TaskScheduler = TaskScheduler.Current }, k => // ~0.35 ms a block (Elden Ring's Data2.bhd: 28k blocks)
        {
            var m = BigInteger.ModPow(new BigInteger(input.AsSpan(k * inSize, inSize), isUnsigned: true, isBigEndian: true), e, n);
            var b = m.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (b.Length > outSize) throw new InvalidDataException("not this archive's key");
            b.CopyTo(output, k * outSize + outSize - b.Length); // left-padded: leading zero bytes are part of the plaintext
        });
        return output;
    }

    /// <summary>Every file header of a decrypted BHD5 (DS3 and later: salt, SHA and AES records).
    /// <paramref name="hash64"/>: Elden Ring and later (64-bit name hashes, 64-bit bucket table).</summary>
    public static List<Entry> ParseBhd(ReadOnlySpan<byte> b, bool hash64)
    {
        if (!b.StartsWith("BHD5"u8) || b[4] != 0xFF) throw new InvalidDataException("not a little-endian BHD5 header");
        var wide = b.Length > 0x28 && I32(b, 0x14) == 0 && I32(b, 0x1C) == 0; // 64-bit bucket count + offset
        long count = wide ? I64(b, 0x10) : I32(b, 0x10), at = wide ? I64(b, 0x18) : I32(b, 0x14);
        var list = new List<Entry>();
        for (var k = 0; k < count; k++, at += wide ? 16 : 8)
        {
            var n = I32(b, (int)at);
            var h = (int)(wide ? I64(b, (int)at + 8) : I32(b, (int)at + 4));
            for (var i = 0; i < n; i++, h += 40)
            {
                ulong hash; long size, off, aes;
                if (hash64) (hash, size, off, aes) = (U64(b, h), I32(b, h + 12), I64(b, h + 16), I64(b, h + 32));
                else (hash, size, off, aes) = (U32(b, h), I64(b, h + 32), I64(b, h + 8), I64(b, h + 24));
                if (size <= 0) size = I32(b, h + (hash64 ? 8 : 4)); // unpadded size unknown: the padded one
                byte[]? key = null;
                var ranges = Array.Empty<(long, long)>();
                if (aes != 0)
                {
                    key = b.Slice((int)aes, 16).ToArray();
                    ranges = new (long, long)[I32(b, (int)aes + 16)];
                    for (var r = 0; r < ranges.Length; r++) ranges[r] = (I64(b, (int)aes + 20 + 16 * r), I64(b, (int)aes + 28 + 16 * r));
                }
                list.Add(new Entry(hash, off, size, key, ranges));
            }
        }
        return list;
    }

    /// <summary>The archive name hash: lower case, '/' separators, a leading '/'; h = h * 37 + c (32-bit) or h * 133 + c
    /// (64-bit, Elden Ring and later).</summary>
    public static ulong NameHash(string path, bool hash64)
    {
        var s = path.Trim().Replace('\\', '/').ToLowerInvariant();
        if (!s.StartsWith('/')) s = "/" + s;
        if (hash64) return s.Aggregate(0UL, (h, c) => h * 0x85 + c);
        return s.Aggregate(0U, (h, c) => h * 37 + c);
    }

    /// <summary>An entry's bytes from its .bdt, AES ranges decrypted.</summary>
    public static byte[] Read(SafeFileHandle bdt, Entry e)
    {
        var padded = e.AesRanges.Length == 0 ? e.Size : Math.Max(e.Size, e.AesRanges.Max(r => r.End));
        var b = new byte[padded];
        if (RandomAccess.Read(bdt, b, e.Offset) != b.Length) throw new EndOfStreamException("archive entry past the end of its .bdt");
        if (e.AesKey != null)
        {
            using var aes = Aes.Create();
            aes.Key = e.AesKey;
            foreach (var (s, end) in e.AesRanges.Where(r => r.Start >= 0 && r.End > r.Start))
                aes.DecryptEcb(b.AsSpan((int)s, (int)(end - s)), b.AsSpan((int)s), PaddingMode.None);
        }
        return b.Length == e.Size ? b : b[..(int)e.Size];
    }

    public static bool IsDcx(ReadOnlySpan<byte> b) => b.Length >= 0x4C && b.StartsWith("DCX\0"u8);

    /// <summary>The codec FourCC of a DCX file ("DFLT", "KRAK", "ZSTD", "EDGE").</summary>
    public static string DcxCodec(ReadOnlySpan<byte> b) => Encoding.ASCII.GetString(b.Slice(0x28, 4));

    /// <summary>Decompresses a DCX file (DFLT, KRAK, ZSTD; EDGE is PS3-only and not read).</summary>
    public static byte[] Dcx(ReadOnlySpan<byte> b)
    {
        if (!IsDcx(b) || !b.Slice(0x18, 4).SequenceEqual("DCS\0"u8) || !b.Slice(0x44, 4).SequenceEqual("DCA\0"u8)) throw new InvalidDataException("not a DCX file");
        int size = (int)BinaryPrimitives.ReadUInt32BigEndian(b[0x1C..]), packed = (int)BinaryPrimitives.ReadUInt32BigEndian(b[0x20..]);
        var data = b.Slice(0x4C, Math.Min(packed, b.Length - 0x4C));
        var codec = DcxCodec(b);
        byte[] output;
        switch (codec)
        {
            case "DFLT":
                if (size < 0 || size > 1032L * data.Length) throw new InvalidDataException("DCX size past what its deflate data can hold"); // deflate expands at most 1032:1
                output = new byte[size];
                using (var z = new ZLibStream(new MemoryStream(data.ToArray()), CompressionMode.Decompress)) z.ReadExactly(output);
                return output;
            case "KRAK":
                InitOodle();
                return Compression.Decompress(data.ToArray(), size, CompressionMethod.Oodle);
            case "ZSTD":
                return Compression.Decompress(data.ToArray(), size, CompressionMethod.Zstd);
            default:
                throw new NotSupportedException($"DCX codec {codec}");
        }
    }

    /// <summary>CUE4Parse's Oodle (<see cref="App.Codecs"/>, like UnrealReader's), never the game's own oo2core DLL.</summary>
    static void InitOodle() => App.Codecs.Load(zlib: false);

    public static bool IsBnd4(ReadOnlySpan<byte> b) => b.Length >= 0x40 && b.StartsWith("BND4"u8);

    /// <summary>The files of a little-endian BND4 binder: (name, bytes); a DCX-compressed file (per-file flag) decompressed.</summary>
    public static IEnumerable<(string Name, byte[] Data)> Bnd4(byte[] b)
    {
        if (!IsBnd4(b) || b[9] != 0) throw new InvalidDataException("not a little-endian BND4 binder");
        bool bitBigEndian = b[10] == 0, unicode = b[0x30] != 0;
        var raw = b[0x31];
        var format = bitBigEndian || (raw & 1) != 0 && (raw & 0x80) == 0 ? raw : Reverse(raw); // SoulsFormats Binder.ReadFormat
        bool ids = (format & 0x02) != 0, names = (format & 0x0C) != 0, longOffsets = (format & 0x10) != 0, compression = (format & 0x20) != 0;
        int count = I32(b, 0x0C), headerSize = (int)I64(b, 0x20);
        for (var i = 0; i < count; i++)
        {
            var h = 0x40 + i * headerSize;
            var flags = bitBigEndian ? b[h] : Reverse(b[h]);
            var size = I64(b, h + 8);
            var p = h + 16 + (compression ? 8 : 0);
            long offset = longOffsets ? I64(b, p) : U32(b, p);
            p += (longOffsets ? 8 : 4) + (ids ? 4 : 0);
            var name = names ? Str(b, (int)U32(b, p), unicode) : $"#{i}";
            var data = b.AsSpan((int)offset, (int)size);
            yield return (name, (flags & 1) != 0 ? Dcx(data) : data.ToArray());
        }
    }

    static string Str(byte[] b, int at, bool unicode)
    {
        if (!unicode)
        {
            var end = Array.IndexOf(b, (byte)0, at);
            return Encoding.Latin1.GetString(b, at, end - at); // Shift-JIS in the game; file names are ASCII
        }
        var n = 0;
        while (at + 2 * n + 1 < b.Length && (b[at + 2 * n] | b[at + 2 * n + 1]) != 0) n++;
        return Encoding.Unicode.GetString(b, at, 2 * n);
    }

    static byte Reverse(byte x) => (byte)((x * 0x0202020202UL & 0x010884422010UL) % 1023);

    static int I32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b[o..]);
    static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    static long I64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt64LittleEndian(b[o..]);
    static ulong U64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
}
