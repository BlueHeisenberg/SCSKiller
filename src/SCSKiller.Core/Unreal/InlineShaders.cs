using System.Buffers.Binary;
using System.IO.Compression;

namespace SCSKiller.Core.Unreal;

/// <summary>Shaders cooked into the packages that own them (bShareMaterialShaderCode=False: materials, material instances,
/// Niagara scripts, the global shader cache). Carved from the package bytes by structure, never by parsing properties, so
/// no .usmap mappings and no per-game code. A raw "DXBC" scan doesn't work: the code is compressed (LZ4 keeps a literal
/// "DXBC" header in front of a compressed body). Layouts, each checked by decompressing to an FShaderCode that holds a
/// DXBC container:
///   UE 4.25+ FShaderMapResourceCode, one per inline shader map: ResourceHash, TArray&lt;hash&gt; ShaderHashes (K), then K
///   entries; the two K's 20*K bytes apart (8*K in 5.8+: 8-byte hashes) anchor it. Entry 'A' (4.25-5.4): [i32 N][N bytes]
///   [i32 uncompressed size][u8 frequency]. Entry 'B' (5.5+): a 12-byte header buffer [i64 12][i32 uncompressed size]
///   [i32 code size][u8 frequency][3 pad], [i64 N][N bytes], in 5.6+ then an FCompressedBuffer of symbols (magic
///   0xb7756362, big-endian, total size at +24).
///   'Z' (4.2x, before FShaderMapResourceCode): each FShaderResource's code as [i32 N][zlib stream of N bytes]; no map
///   boundary, so these are all map 0 (one per package).
/// ponytail: a 4.2x code that zlib couldn't shrink is stored raw and isn't carved (LIS Remastered's packages hold no raw
/// DXBC at all); add a raw-container layout if a game shows some.</summary>
public static class InlineShaders
{
    /// <summary>One carved shader: its inline shader map (1-based, 0 = 'Z'), where its entry starts in the package bytes and
    /// the entry layout (<see cref="Decode"/> takes both), and its FShaderCode (container + UE's optional-data trailer).</summary>
    public readonly record struct Entry(int Map, int Offset, char Format, byte[] Code);

    /// <summary>Calls <paramref name="found"/> with each shader in a package's files (.uasset, then .uexp), one at a time:
    /// offsets and map numbers run on across the files as in their concatenation (no entry spans the header/export
    /// boundary). Returns the entries of maps that hold D3D shaders but didn't decode themselves (should be 0; maps with no
    /// D3D shader at all are other platforms', e.g. Vulkan, or false anchors, and are dropped).</summary>
    public static int Carve(IReadOnlyList<byte[]> parts, Action<Entry> found)
    {
        int undecoded = 0, map = 0, bias = 0;
        foreach (var d in parts)
        {
            undecoded += Carve(d, bias, ref map, found);
            bias += d.Length;
        }
        return undecoded;
    }

    static int Carve(byte[] d, int bias, ref int map, Action<Entry> found)
    {
        var blocks = new List<(int Start, int End)>();
        var undecoded = 0;
        for (var p = 0; p + 8 <= d.Length; p++)
        {
            var k = I32(d, p);
            if (k is < 1 or > 65535) continue;
            foreach (var h in HashSizes)
            {
                var q = p + 4 + (long)h * k;
                if (q + 4 > d.Length || I32(d, (int)q) != k || Chain(d, (int)q + 4, k) is not { } entries) continue;
                map++;
                var decoded = 0;
                foreach (var (off, fmt) in entries)
                    if (Decode(d, off, fmt) is { } code) { decoded++; found(new(map, bias + off, fmt, code)); }
                if (decoded == 0) continue; // a false anchor may overlap a real one: keep scanning inside it
                undecoded += entries.Count - decoded;
                var end = Layout(d, entries[^1].Off, entries[^1].Fmt).Len + entries[^1].Off;
                blocks.Add((p, end));
                p = end - 1;
                break;
            }
        }
        for (var i = d.AsSpan().IndexOf((byte)0x78); i >= 0; i = d.AsSpan(i + 1).IndexOf((byte)0x78) is var j and >= 0 ? i + 1 + j : -1)
            if (i >= 4 && Layout(d, i - 4, 'Z').Len > 0 && !blocks.Any(b => i >= b.Start && i < b.End) && Decode(d, i - 4, 'Z') is { } code)
                found(new(0, bias + i - 4, 'Z', code)); // (a zlib-compressed 'A' entry has the same shape: it's its map's)
        return undecoded;
    }

    static readonly int[] HashSizes = [20, 8];

    /// <summary>The FShaderCode of the entry at <paramref name="offset"/>, or null if it isn't one holding a DXBC container.</summary>
    public static byte[]? Decode(byte[] d, int offset, char format)
    {
        var (len, at, n, size) = Layout(d, offset, format);
        if (len == 0) return null;
        try
        {
            var code = format == 'Z' ? Inflate(d, at, n) : n == size ? d[at..(at + n)] : UnrealReader.Decompress(d, at, n, size);
            return ShaderContainer.Offset(code, out _) >= 0 ? code : null;
        }
        catch (Exception) { return null; } // not compressed data after all
    }

    /// <summary>K entries in a row from <paramref name="e"/>, all of one layout ('B' starts with the i64 12), or null.</summary>
    static List<(int Off, char Fmt)>? Chain(byte[] d, int e, int k)
    {
        var fmt = e + 8 <= d.Length && I64(d, e) == 12 ? 'B' : 'A';
        var list = new List<(int, char)>(); // most anchors are false: no array until an entry holds
        for (var i = 0; i < k; i++)
        {
            var len = Layout(d, e, fmt).Len;
            if (len == 0) return null;
            list.Add((e, fmt));
            e += len;
        }
        return list;
    }

    const int MaxCode = 64 << 20, MaxRatio = 64; // bounds what a false anchor can make us allocate

    /// <summary>(entry length, code offset, stored code length, uncompressed length: -1 unknown); length 0 = not an entry.</summary>
    static (int Len, int At, int N, int Size) Layout(byte[] d, int e, char fmt)
    {
        long end = d.Length;
        switch (fmt)
        {
            case 'A':
            {
                if (e + 9 > end) break;
                var n = I32(d, e);
                if (n <= 0 || e + 9L + n > end) break;
                var size = I32(d, e + 4 + n);
                if (size < n || size > MaxCode || size > (long)MaxRatio * n || d[e + 8 + n] >= 16) break; // frequency < SF_NumFrequencies
                return (9 + n, e + 4, n, size);
            }
            case 'B':
            {
                if (e + 28 > end || I64(d, e) != 12) break;
                int size = I32(d, e + 8), codeSize = I32(d, e + 12);
                var n = I64(d, e + 20);
                if (n <= 0 || n > size || size > MaxCode || size > MaxRatio * n || codeSize > size || d[e + 16] >= 16 || e + 28 + n > end) break;
                var len = 28 + (int)n;
                if (e + len + 32 <= end && BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(e + len)) == 0xb7756362)
                {
                    var symbols = BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(e + len + 24));
                    if (symbols < 64 || (ulong)(e + len) + symbols > (ulong)end) break;
                    len += (int)symbols;
                }
                return (len, e + 28, (int)n, size);
            }
            case 'Z':
            {
                if (e + 6 > end) break;
                var n = I32(d, e);
                if (n < 3 || e + 4L + n > end || d[e + 4] != 0x78 || d[e + 5] is not (0x01 or 0x5E or 0x9C or 0xDA)) break;
                return (4 + n, e + 4, n, -1);
            }
        }
        return default;
    }

    static byte[] Inflate(byte[] d, int at, int n)
    {
        using var z = new ZLibStream(new MemoryStream(d, at, n), CompressionMode.Decompress);
        using var o = new MemoryStream();
        var limit = Math.Min(MaxCode, (long)MaxRatio * n);
        Span<byte> buf = stackalloc byte[16 << 10]; // every candidate stream gets here: no heap buffer per call
        for (int got; (got = z.Read(buf)) > 0;)
        {
            if (o.Length + got > limit) throw new InvalidDataException("inflates past the code bound");
            o.Write(buf[..got]);
        }
        return o.ToArray();
    }

    static int I32(byte[] d, int o) => BitConverter.ToInt32(d, o);
    static long I64(byte[] d, int o) => BitConverter.ToInt64(d, o);
}
