using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.ReEngine;
using SCSKiller.Tests.Carved;

namespace SCSKiller.Tests.ReEngine;

/// <summary>Tiny synthetic RE Engine packages built here: header, every feature block, the table's XOR layer, the three
/// compressions and chunked entries; then the reader end to end on a fake install (patch override, config.ini API). The
/// table key is made with a modulus generated here (no game's).</summary>
public class RePakTests
{
    static readonly BigInteger Modulus = new(RSA.Create(1024).ExportParameters(false).Modulus, isUnsigned: true, isBigEndian: true);

    /// <param name="SizeInPacked">a chunked entry that gives its length as its packed size, its size 0</param>
    public sealed record E(ulong Hash, byte[] Data, int Compression = 0, bool Chunked = false, bool SizeInPacked = false);

    /// <summary>A KPKA v4.2 package. Chunked entries go to a chunk table of <paramref name="block"/>-byte chunks (every
    /// other one stored, the rest zstd; the last padded to the block size, as the format requires).</summary>
    public static byte[] Pak(ushort features, int block, params E[] entries)
    {
        var data = new MemoryStream();
        var chunks = new List<(long Start, int Packed)>();
        var table = new byte[48 * entries.Length];
        long head = 16 + table.Length + ((features & RePak.ExtraU32) != 0 ? 4 : 0) + ((features & RePak.ExtraData) != 0 ? 9 : 0)
            + ((features & RePak.EncryptedTable) != 0 ? 128 : 0) + ((features & RePak.RemapTable) != 0 ? 8 + 16 * Remaps : 0);
        var chunked = entries.Where(e => e.Chunked).Sum(e => (e.Data.Length + block - 1) / block);
        if ((features & RePak.ChunkTable) != 0) head += 8 + 8 * chunked;
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            long offset = head + data.Length, packed;
            ulong attr = (ulong)e.Compression;
            if (e.Chunked)
            {
                offset = chunks.Count;
                attr = 1UL << 24;
                for (var at = 0; at < e.Data.Length; at += block)
                {
                    var b = new byte[block];
                    e.Data.AsSpan(at, Math.Min(block, e.Data.Length - at)).CopyTo(b);
                    var z = chunks.Count % 2 == 1 ? b : new ZstdSharp.Compressor().Wrap(b).ToArray();
                    chunks.Add((head + data.Length, z.Length));
                    data.Write(z);
                }
                packed = e.SizeInPacked ? e.Data.Length : 0;
            }
            else
            {
                var z = Compress(e.Data, e.Compression);
                data.Write(z);
                packed = z.Length;
            }
            var t = table.AsSpan(48 * i);
            BinaryPrimitives.WriteUInt32LittleEndian(t, (uint)e.Hash);
            BinaryPrimitives.WriteUInt32LittleEndian(t[4..], (uint)(e.Hash >> 32));
            BinaryPrimitives.WriteInt64LittleEndian(t[8..], offset);
            BinaryPrimitives.WriteInt64LittleEndian(t[16..], packed);
            BinaryPrimitives.WriteInt64LittleEndian(t[24..], e.SizeInPacked ? 0 : e.Data.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(t[32..], attr);
        }
        var o = new MemoryStream();
        o.Write("KPKA"u8);
        o.Write([4, 2]);
        o.Write(BitConverter.GetBytes(features));
        o.Write(BitConverter.GetBytes(entries.Length));
        o.Write(BitConverter.GetBytes(0x12345678));
        var raw = new byte[128];
        if ((features & RePak.EncryptedTable) != 0)
        {
            new Random(7).NextBytes(raw);
            raw[127] = 0x10; // below the modulus
            RePak.Xor(table, RePak.Key(raw, Modulus)); // XOR is its own inverse: the reader undoes exactly this
        }
        o.Write(table);
        if ((features & RePak.ExtraU32) != 0) o.Write(BitConverter.GetBytes(0xAABBCCDD));
        if ((features & RePak.ExtraData) != 0) o.Write(new byte[9]);
        if ((features & RePak.RemapTable) != 0)
        {
            o.Write(BitConverter.GetBytes(Remaps));
            o.Write(BitConverter.GetBytes(1 << 19)); // the unused u32: not 0, to show it is ignored
            var r = new byte[16 * Remaps];
            new Random(3).NextBytes(r);
            o.Write(r);
        }
        if ((features & RePak.EncryptedTable) != 0) o.Write(raw);
        if ((features & RePak.ChunkTable) != 0)
        {
            o.Write(BitConverter.GetBytes(block));
            o.Write(BitConverter.GetBytes(chunks.Count));
            foreach (var (start, n) in chunks) { o.Write(BitConverter.GetBytes((uint)start)); o.Write(BitConverter.GetBytes((uint)n << 10 | 0x3)); }
        }
        Assert.Equal(head, o.Length);
        data.Position = 0;
        data.CopyTo(o);
        return o.ToArray();
    }

    const int Remaps = 3;

    static byte[] Compress(byte[] b, int compression)
    {
        if (compression == 2) return new ZstdSharp.Compressor().Wrap(b).ToArray();
        if (compression == 0) return b;
        var m = new MemoryStream();
        using (var d = new DeflateStream(m, CompressionLevel.Optimal, leaveOpen: true)) d.Write(b);
        return m.ToArray();
    }

    static byte[] Bytes(int n, int seed)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = (byte)(i * seed % 251 / 4); // compressible
        return b;
    }

    static string Temp(string name) => Planning.Ff7.TempDir(name);

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)(RePak.EncryptedTable | RePak.ChunkTable))]  // PRAGMATA's re_chunk_000.pak
    [InlineData((ushort)(RePak.ExtraU32 | RePak.ExtraData | RePak.EncryptedTable | RePak.ChunkTable))]
    [InlineData((ushort)(RePak.RemapTable | RePak.EncryptedTable | RePak.ChunkTable))]  // Dragon's Dogma 2's
    [InlineData((ushort)(RePak.ExtraU32 | RePak.ExtraData | RePak.RemapTable | RePak.EncryptedTable | RePak.ChunkTable))]
    public void ReadsEveryEntryKind(ushort features)
    {
        var chunk = (features & RePak.ChunkTable) != 0;
        E[] entries =
        [
            new(0x1111_0000_0000_0001, Bytes(300, 3)),
            new(0x2222_0000_0000_0002, Bytes(5000, 5), 1),
            new(0x3333_0000_0000_0003, Bytes(70000, 7), 2),
            .. chunk ? new[] { new E(0x4444_0000_0000_0004, Bytes(64 * 3 + 10, 11), Chunked: true), new E(5, Bytes(64, 13), Chunked: true) } : [],
        ];
        var path = Path.Combine(Temp("repak"), "re_chunk_000.pak");
        File.WriteAllBytes(path, Pak(features, 64, entries));
        if ((features & RePak.EncryptedTable) != 0) // the modulus that opens it is picked; none, or only a wrong one: refused
        {
            Assert.Equal(RePak.NoModulus, Assert.Throws<InvalidDataException>(() => RePak.Open(path)).Message);
            Assert.Equal(RePak.NoModulus, Assert.Throws<InvalidDataException>(() => RePak.Open(path, [Modulus - 2])).Message);
        }
        using var pak = RePak.Open(path, [Modulus - 2, Modulus]);
        Assert.Equal((4, 2, features), (pak.Major, pak.Minor, pak.Features));
        Assert.Equal(entries.Select(e => e.Hash), pak.Entries.Select(e => e.Hash));
        for (var i = 0; i < entries.Length; i++)
        {
            Assert.Equal(entries[i].Data, pak.Read(pak.Entries[i]));
            Assert.Equal(entries[i].Data[..4], pak.Read(pak.Entries[i], 4)); // a prefix: what the reader probes
        }
    }

    [Fact]
    public void RefusesWhatItCannotRead()
    {
        var dir = Temp("repak-bad");
        var path = Path.Combine(dir, "a.pak");
        var b = Pak(0, 64, new E(1, Bytes(10, 3)));
        b[6] = 0x80; // an unknown feature bit
        File.WriteAllBytes(path, b);
        Assert.Contains("features", Assert.Throws<InvalidDataException>(() => RePak.Open(path)).Message);

        b = Pak(0, 64, new E(1, Bytes(10, 3)));
        b[16 + 32 + 2] = 1; // attributes bits 16-23: resource encryption
        File.WriteAllBytes(path, b);
        using var pak = RePak.Open(path);
        Assert.Throws<InvalidDataException>(() => pak.Read(pak.Entries[0]));

        // entry fields are checked at Read: offsets before the file or a chunk index past the table, sizes beyond what
        // the stored bytes or the chunks can hold
        var n = 0;
        RePak With(ushort features, E e, int at, long value)
        {
            var p = Path.Combine(dir, $"e{n++}.pak");
            var x = Pak(features, 64, e);
            BinaryPrimitives.WriteInt64LittleEndian(x.AsSpan(16 + at), value);
            File.WriteAllBytes(p, x);
            return RePak.Open(p);
        }
        var stored = new E(1, Bytes(10, 3));
        var chunked = new E(2, Bytes(100, 3), Chunked: true);
        foreach (var (features, e, at, value) in new[] { (0, stored, 8, -1L), (0, stored, 8, long.MaxValue), (0, stored, 16, -1L), (0, stored, 24, -1L),
                     (RePak.ChunkTable, chunked, 8, -1L), (RePak.ChunkTable, chunked, 8, 2L), (RePak.ChunkTable, chunked, 24, 1L << 30) })
        {
            using var bad = With((ushort)features, e, at, value);
            var before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<InvalidDataException>(() => bad.Read(bad.Entries[0]));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20);
        }
        using (var big = With(0, stored, 24, 1L << 30)) // a stored entry claiming 1 GiB: its 10 bytes, nothing allocated for the rest
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(stored.Data, big.Read(big.Entries[0]));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20);
        }
    }

    /// <summary>A compressed entry whose size claims the read budget: its real bytes, allocated as they decode; claiming more
    /// (1 GB, the array limit): refused whole, its prefix still read.</summary>
    [Theory]
    [InlineData(1, RePak.MaxEntry)]
    [InlineData(2, RePak.MaxEntry)]
    [InlineData(1, 1L << 30)]
    [InlineData(2, 1L << 30)]
    [InlineData(2, 0x7FFFFFC7L)]   // Array.MaxLength
    public void ACompressedSizeAllocatesNothingBeyondItsBytes(int compression, long size)
    {
        var e = new E(1, Bytes(3000, 3), compression);
        var b = Pak(0, 64, e);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(16 + 24), size);
        var path = Path.Combine(Temp("repak-bomb"), "a.pak");
        File.WriteAllBytes(path, b);
        using var pak = RePak.Open(path);
        var before = GC.GetAllocatedBytesForCurrentThread();
        if (size <= RePak.MaxEntry) Assert.Equal(e.Data, pak.Read(pak.Entries[0]));
        else Assert.Equal("entry over 256 MB", Assert.Throws<InvalidDataException>(() => pak.Read(pak.Entries[0])).Message);
        Assert.Equal(e.Data[..4], pak.Read(pak.Entries[0], 4));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4 << 20);
    }

    /// <summary>A zstd frame declaring a 128 MB window around 4 bytes is refused, not decoded with a 128 MB window.</summary>
    [Fact]
    public void AZstdWindowOverTheBudgetIsRefused()
    {
        byte[] frame = [0x28, 0xB5, 0x2F, 0xFD, 0x00, 0x88, 0x21, 0x00, 0x00, .. "SDF\0"u8];
        var b = Pak(0, 64, new E(1, Bytes(frame.Length, 3), 2));
        frame.CopyTo(b, 16 + 48);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(16 + 16), frame.Length);
        var path = Path.Combine(Temp("repak-bomb"), "w.pak");
        File.WriteAllBytes(path, b);
        using var pak = RePak.Open(path);
        Assert.Throws<ZstdSharp.ZstdException>(() => pak.Read(pak.Entries[0], 4));
    }

    /// <summary>A chunked entry claiming the read budget over a chunk table that holds as many chunks, all but two of them
    /// pointing past the end of the file: refused at the third chunk, having allocated for two.</summary>
    [Fact]
    public void AChunkedSizeAllocatesNothingBeyondItsChunks()
    {
        const int block = 1 << 16, n = RePak.MaxEntry / block;
        var x = Pak(RePak.ChunkTable, block, new E(1, Bytes(2 * block, 3), Chunked: true));
        const int ct = 16 + 48, shift = 8 * (n - 2);
        var o = new MemoryStream();
        o.Write(x.AsSpan(0, ct + 4));
        o.Write(BitConverter.GetBytes(n));
        for (var i = 0; i < 2; i++)
        {
            o.Write(BitConverter.GetBytes(BinaryPrimitives.ReadUInt32LittleEndian(x.AsSpan(ct + 8 + 8 * i)) + shift));
            o.Write(x.AsSpan(ct + 12 + 8 * i, 4));
        }
        for (var i = 2; i < n; i++) { o.Write(BitConverter.GetBytes(0xFFFF_0000u)); o.Write(BitConverter.GetBytes(16u << 10)); }
        o.Write(x.AsSpan(ct + 24));
        var b = o.ToArray();
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(16 + 24), RePak.MaxEntry);
        var path = Path.Combine(Temp("repak-bomb"), "c.pak");
        File.WriteAllBytes(path, b);
        using var pak = RePak.Open(path);
        Assert.Equal(Bytes(2 * block, 3)[..4], pak.Read(pak.Entries[0], 4));
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Contains("truncated", Assert.Throws<InvalidDataException>(() => pak.Read(pak.Entries[0])).Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4 << 20);
    }

    /// <summary>An entry or chunk table over its budget is refused at open, before anything is allocated for it.</summary>
    [Theory]
    [InlineData(RePak.ChunkTable, 8, RePak.MaxTable / 48 + 1, "entry table over 64 MB")]
    [InlineData(RePak.ChunkTable, 16 + 48 + 4, RePak.MaxChunkTable / 8 + 1, "chunk table over 32 MB")]
    [InlineData(RePak.ChunkTable, 16 + 48 + 4, -1, "chunk table over 32 MB")]
    public void RefusesATableOverTheBudget(ushort features, int at, int count, string message)
    {
        var b = Pak(features, 64, new E(1, Bytes(10, 3)));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), count);
        var path = Path.Combine(Temp("repak-budget"), "a.pak");
        File.WriteAllBytes(path, b);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => RePak.Open(path)).Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20);
    }

    /// <summary>A remap table whose count runs past the end of the file, or a file cut inside it, is refused at open without
    /// allocating for the count it claims.</summary>
    [Theory]
    [InlineData(0u)]   // one record more than the rest of the file holds
    [InlineData(0x1000_0000u)]
    [InlineData(uint.MaxValue)]
    [InlineData((uint)Remaps)]   // as built, the file cut inside its last record
    public void RefusesABadRemapTable(uint count)
    {
        var path = Path.Combine(Temp("repak-remap"), "a.pak");
        var b = Pak(RePak.RemapTable | RePak.EncryptedTable, 64, new E(1, Bytes(10, 3)));
        const int at = 16 + 48;
        if (count == Remaps) b = b[..(at + 8 + 16 * Remaps - 1)];
        else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), count == 0 ? (uint)(b.Length - at - 8) / 16 + 1 : count);
        File.WriteAllBytes(path, b);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Contains("remap", Assert.Throws<InvalidDataException>(() => RePak.Open(path, [Modulus])).Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20);
    }

    /// <summary>A fake install: base package with a master material ("SDF\0" + a table + two containers), a texture and a
    /// patch replacing the material; the reader indexes the patched shaders only and serves their bytes.</summary>
    [Fact]
    public void ReaderIndexesTheLastVersionOfEachShaderFile()
    {
        var dir = Temp("reengine-game");
        byte[] Sdf(params byte[][] cs) => [.. "SDF\0"u8, .. new byte[200], .. cs.SelectMany(c => c.Concat(new byte[12]))];
        var (vs1, ps1, vs2, ps2) = (Hlsl.Vs(1, Hlsl.Rs1), Hlsl.Ps(1, Hlsl.Rs1), Hlsl.Vs(2, Hlsl.Rs1), Hlsl.Ps(2, Hlsl.Rs1));
        const ulong material = 0xABCD_0000_0000_0001;
        File.WriteAllBytes(Path.Combine(dir, "re_chunk_000.pak"), Pak(RePak.EncryptedTable | RePak.ChunkTable, 256,
            new E(material, Sdf(vs1, ps1), 2), new E(7, [.. "TEX\0"u8, .. Bytes(100, 3)], 2), new E(8, Sdf(vs1), Chunked: true)));
        File.WriteAllBytes(Path.Combine(dir, "re_chunk_000.pak.patch_001.pak"), Pak(RePak.EncryptedTable, 64, new E(material, Sdf(vs2, ps2), 1)));
        File.WriteAllText(Path.Combine(dir, "config.ini"), "[Render]\nAllowMeshShader=Enable\nCapability=DirectX12\n");
        var game = new Game("test:re", "RE test", Store.Other, dir, Path.Combine(dir, "game.exe"));

        var reader = new ReEngineReader(Path.Combine(dir, "data"), Offline);
        Assert.EndsWith(reader.ModulusFile(game), reader.Detect(game)!.Unsupported); // encrypted, no modulus: says where to put it
        Directory.CreateDirectory(Path.GetDirectoryName(reader.ModulusFile(game))!);
        File.WriteAllText(reader.ModulusFile(game), "0x" + Convert.ToHexString(Modulus.ToByteArray(isUnsigned: true, isBigEndian: true)));
        var engine = reader.Detect(game)!;
        Assert.Equal(new EngineInfo(ReEngineReader.Family, "PAK 4.2", "game", "D3D12", false, null), engine);
        Assert.Null(reader.Detect(game with { InstallDir = Path.GetTempPath(), ExePath = Path.Combine(Path.GetTempPath(), "x.exe") }));

        var index = reader.Index(game, engine, null, CancellationToken.None);
        var want = new[] { vs1, vs2, ps2 }.Select(c => Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(c))).ToHashSet();
        Assert.Equal(want, index.Shaders.Keys.ToHashSet()); // ps1 was patched away; vs1 still comes with the chunked file
        Assert.Equal(2, index.Maps.Count);
        Assert.All(index.Maps, m => Assert.Equal("PCD3D_SM5", m.Platform));

        var got = new Dictionary<string, byte[]>();
        new ReEngineReader(Path.Combine(dir, "data"), Offline).ReadShaders(game, engine, want, (h, b) => got[h] = b, CancellationToken.None); // a fresh reader re-indexes
        Assert.Equal(want, got.Keys.ToHashSet());
        Assert.Equal(vs2, got[Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(vs2))]);

        File.Delete(Path.Combine(dir, "config.ini"));
        Assert.Equal("D3D11 or D3D12", reader.Detect(game)!.GraphicsApi); // DXBC and no setting: either
        Directory.Delete(dir, true);
    }

    /// <summary>A shader file whose first container is DXBC and a later one DXIL (as Resident Evil Village ships SM5 beside
    /// SM6), with no config.ini: DirectX 12, not "either".</summary>
    [Fact]
    public void A_shader_file_with_any_DXIL_is_DirectX_12()
    {
        var dir = Temp("reengine-dxil");
        byte[] part = [.. "DXIL"u8, .. BitConverter.GetBytes(8), .. BitConverter.GetBytes(6 << 16 | 0x60), .. BitConverter.GetBytes(2)];
        byte[] dxil = [.. "DXBC"u8, .. new byte[16], .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(36 + part.Length), .. BitConverter.GetBytes(1),
            .. BitConverter.GetBytes(36), .. part];
        byte[] Sdf(params byte[][] cs) => [.. "SDF\0"u8, .. new byte[200], .. cs.SelectMany(c => c.Concat(new byte[12]))];
        var game = new Game("test:re-dxil", "RE test", Store.Other, dir, Path.Combine(dir, "game.exe"));
        var reader = new ReEngineReader(Path.Combine(dir, "data"), Offline);

        File.WriteAllBytes(Path.Combine(dir, "re_chunk_000.pak"), Pak(0, 64, new E(1, Sdf(Hlsl.Vs(1, Hlsl.Rs1), dxil))));
        Assert.Equal("D3D12", reader.Detect(game)!.GraphicsApi);
        File.WriteAllBytes(Path.Combine(dir, "re_chunk_000.pak"), Pak(0, 64, new E(1, Sdf(Hlsl.Vs(1, Hlsl.Rs1), Hlsl.Ps(1, Hlsl.Rs1)))));
        Assert.Equal("D3D11 or D3D12", reader.Detect(game)!.GraphicsApi);   // DXBC only: either
        Directory.Delete(dir, true);
    }

    static string? Offline(string url) => null;

    /// <summary>ree-pak-rs's pak.rs shape (the modulus as a Rust byte array, little endian, 129 bytes; the exponent as a
    /// 4-byte one), with moduli generated here.</summary>
    static string RustSource(params BigInteger[] moduli) =>
        "use num::BigUint;\n\n" + string.Concat(moduli.Select((m, i) => $"const MODULUS{i}: [u8; 129] = [\n    "
            + string.Join(", ", m.ToByteArray(isUnsigned: true).Concat(new byte[129]).Take(129).Select(b => $"0x{b:X2}")) + ",\n];\n"))
        + "const EXPONENT: [u8; 4] = [0x01, 0x00, 0x01, 0x00];\n\nfn resize_key(key: &[u8]) -> Vec<u8> { key.to_vec() }\n";

    [Fact]
    public void ReePakModuliReadsEveryLongByteArray()
    {
        var other = new BigInteger(RSA.Create(1024).ExportParameters(false).Modulus, isUnsigned: true, isBigEndian: true);
        var got = ReEngineReader.ReePakModuli(RustSource(other, Modulus) + "let v = vec![0x1, 0xab];\nlet s: [&str; 2] = [\"a\", \"b\"];\n");
        Assert.Equal([other, Modulus], got.Select(b => new BigInteger(b, isUnsigned: true)));
        Assert.Empty(ReEngineReader.ReePakModuli("fn main() {}"));
    }

    /// <summary>An encrypted table and no modulus given: the published one is downloaded (a fake of ree-pak-rs's shape), the
    /// one that opens the base package kept in pak.modulus; offline, or none opening it, Detect names that file and nothing
    /// is written.</summary>
    [Fact]
    public void ModulusIsDownloadedWhenNoneIsGiven()
    {
        var dir = Temp("reengine-download");
        File.WriteAllBytes(Path.Combine(dir, "re_chunk_000.pak"), Pak(RePak.EncryptedTable, 64, new E(1, [.. "TEX\0"u8, .. Bytes(100, 3)])));
        var game = new Game("test:re-dl", "RE test", Store.Other, dir, Path.Combine(dir, "game.exe"));
        var data = Path.Combine(dir, "data");
        var file = new ReEngineReader(data).ModulusFile(game);
        var wrong = new BigInteger(RSA.Create(1024).ExportParameters(false).Modulus, isUnsigned: true, isBigEndian: true);

        var asked = new List<string>();
        var offline = new ReEngineReader(data, u => { asked.Add(u); return null; }).Detect(game)!.Unsupported!;
        Assert.Contains(RePak.NoModulus, offline);
        Assert.Contains("downloaded", offline);
        Assert.EndsWith(file, offline);
        Assert.Equal([ReEngineReader.ReePakRs], asked);
        Assert.StartsWith("https://raw.githubusercontent.com/eigeen/ree-pak-rs/25562e6a6f9a52a43b80d54be91e4feb7ac7668b/", ReEngineReader.ReePakRs); // pinned
        Assert.EndsWith(file, new ReEngineReader(data, _ => RustSource(wrong)).Detect(game)!.Unsupported);
        Assert.False(File.Exists(file));

        var engine = new ReEngineReader(data, _ => RustSource(wrong, Modulus)).Detect(game)!;
        Assert.DoesNotContain(RePak.NoModulus, engine.Unsupported ?? ""); // opened (no shader files in it: that's all it says)
        Assert.Equal(Modulus, new BigInteger(Convert.FromHexString(File.ReadAllText(file)), isUnsigned: true));
        Assert.Equal(engine, new ReEngineReader(data, _ => throw new InvalidOperationException("kept locally: no second download")).Detect(game));
        Directory.Delete(dir, true);
    }

    /// <summary>A chunked material whose length is in its packed size is indexed (not taken for an empty entry); an empty
    /// patch file is skipped, and a patch that can't be read makes the game unsupported, naming it, instead of indexing the
    /// shaders it replaces; a chunk
    /// table claiming a block size no package uses is refused at open.</summary>
    [Fact]
    public void ChunkedEntriesSizedByPackedLengthAndUnreadablePatches()
    {
        var dir = Temp("reengine-chunked");
        byte[] Sdf(params byte[][] cs) => [.. "SDF\0"u8, .. new byte[200], .. cs.SelectMany(c => c.Concat(new byte[12]))];
        var (vs, ps) = (Hlsl.Vs(3, Hlsl.Rs1), Hlsl.Ps(3, Hlsl.Rs1));
        File.WriteAllBytes(Path.Combine(dir, "re_chunk_000.pak"), Pak(RePak.ChunkTable, 256, new E(0xABCD, Sdf(vs, ps), Chunked: true, SizeInPacked: true)));
        var game = new Game("test:re-chunked", "RE test", Store.Other, dir, Path.Combine(dir, "game.exe"));
        var reader = new ReEngineReader(Path.Combine(dir, "data"), Offline);
        var engine = reader.Detect(game)!;
        Assert.Null(engine.Unsupported);
        Assert.Equal(new[] { vs, ps }.Select(c => Convert.ToHexStringLower(SHA1.HashData(c))).Order(), reader.Index(game, engine, null, CancellationToken.None).Shaders.Keys.Order());

        File.WriteAllBytes(Path.Combine(dir, "re_chunk_000.pak.patch_001.pak"), []);   // empty
        reader = new ReEngineReader(Path.Combine(dir, "data"), Offline);
        engine = reader.Detect(game)!;
        Assert.Null(engine.Unsupported);
        Assert.Equal(2, reader.Index(game, engine, null, CancellationToken.None).Shaders.Count);

        File.WriteAllBytes(Path.Combine(dir, "re_chunk_000.pak.patch_001.pak"), Pak(0, 64, new E(0xABCD, Sdf(ps)))[..20]);   // truncated
        Assert.StartsWith("re_chunk_000.pak.patch_001.pak: ", new ReEngineReader(Path.Combine(dir, "data"), Offline).Detect(game)!.Unsupported);

        var big = Path.Combine(dir, "big.pak");
        File.WriteAllBytes(big, Pak(RePak.ChunkTable, 1 << 30, new E(1, Bytes(10, 3))));
        Assert.Contains("chunk table", Assert.Throws<InvalidDataException>(() => RePak.Open(big)).Message);
        Directory.Delete(dir, true);
    }

    /// <summary>A base package in the clear and an encrypted patch: the downloaded modulus kept is the one that opens the patch.</summary>
    [Fact]
    public void TheDownloadedModulusIsCheckedOnThePackageThatNeedsIt()
    {
        var dir = Temp("reengine-patch-key");
        File.WriteAllBytes(Path.Combine(dir, "re_chunk_000.pak"), Pak(0, 64, new E(1, [.. "TEX\0"u8, .. Bytes(100, 3)])));
        File.WriteAllBytes(Path.Combine(dir, "re_chunk_000.pak.patch_001.pak"), Pak(RePak.EncryptedTable, 64, new E(2, [.. "TEX\0"u8, .. Bytes(100, 5)])));
        var game = new Game("test:re-patch-key", "RE test", Store.Other, dir, Path.Combine(dir, "game.exe"));
        var wrong = new BigInteger(RSA.Create(1024).ExportParameters(false).Modulus, isUnsigned: true, isBigEndian: true);
        var engine = new ReEngineReader(Path.Combine(dir, "data"), _ => RustSource(wrong, Modulus)).Detect(game)!;
        Assert.DoesNotContain(RePak.NoModulus, engine.Unsupported ?? "");
        Directory.Delete(dir, true);
    }
}
