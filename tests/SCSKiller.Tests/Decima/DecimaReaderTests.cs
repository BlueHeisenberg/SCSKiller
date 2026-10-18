using System.Buffers.Binary;
using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Decima;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Carved;
using SCSKiller.Tests.Planning;
using SCSKiller.Tests.Platform;

namespace SCSKiller.Tests.Decima;

/// <summary>The Decima reader on synthetic installs laid out as Horizon Zero Dawn's (plain archives) and Death Stranding's
/// (encrypted archives, salts in a made-up exe), with shaders compiled here (<see cref="Hlsl"/>) and synthetic salts.</summary>
public class DecimaReaderTests
{
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));
    static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];
    static byte[] Fill(int n, byte b) => Enumerable.Repeat(b, n).ToArray();

    /// <summary>Oodle's stored blocks: a 2-byte header (uncompressed, Kraken) before every 256 KiB.</summary>
    static byte[] Oodle(byte[] raw) => Concat([.. raw.Chunk(256 << 10).Select(b => Concat([0xCC, 0x06], b))]);

    /// <summary>An archive over <paramref name="stream"/> in chunks of <paramref name="chunk"/> bytes, stored Oodle blocks,
    /// the chunk table in reverse order; encrypted with <paramref name="salts"/> when given. Then, with <paramref name="kraken"/>,
    /// a Kraken-compressed chunk (<see cref="CodecCpuTests.Kraken"/>), which the salt scan checks a data salt with.</summary>
    internal static byte[] Bin(byte[] stream, int chunk = 4096, DecimaArchive.Salts? salts = null, int files = 3, bool kraken = true)
    {
        var raws = stream.Chunk(chunk).Concat(kraken ? new[] { CodecCpuTests.Text } : []).ToList();
        var packed = raws.Select((r, i) => kraken && i == raws.Count - 1 ? Convert.FromBase64String(CodecCpuTests.Kraken) : Oodle(r)).ToList();
        var dataAt = DecimaArchive.HeaderSize + DecimaArchive.EntrySize * (long)(files + raws.Count);
        var head = new byte[DecimaArchive.HeaderSize];
        const uint key = 0x0BADC0DE;
        BinaryPrimitives.WriteUInt32LittleEndian(head, salts == null ? DecimaArchive.Plain : DecimaArchive.Encrypted);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), key);
        BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(8), dataAt + packed.Sum(p => p.Length));
        BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(16), stream.Length);
        BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(24), files);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(32), raws.Count);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(36), Math.Max(chunk, CodecCpuTests.Text.Length));
        if (salts != null)
        {
            salts.Xor(head.AsSpan(8, 16), key);
            salts.Xor(head.AsSpan(24, 16), key + 1);
        }
        var table = new List<byte[]>();
        var at = dataAt;
        for (var i = 0; i < raws.Count; i++)
        {
            var e = new byte[DecimaArchive.EntrySize];
            BinaryPrimitives.WriteInt64LittleEndian(e, (long)i * chunk);   // the last chunk at the stream's end: a gap before it when short
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(8), raws[i].Length);
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(12), 7 * i + 1);
            BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(16), at);
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(24), packed[i].Length);
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(28), 11 * i + 3);
            at += packed[i].Length;
            if (salts != null)
            {
                salts.XorChunk(packed[i], e.AsSpan(0, 16));
                salts.XorEntry(e);
            }
            table.Add(e);
        }
        table.Reverse();
        return Concat([head, new byte[DecimaArchive.EntrySize * files], .. table, .. packed]);
    }

    /// <summary>Salts of distinct random bytes (a real salt is random; the scan skips runs and text).</summary>
    static DecimaArchive.Salts NewSalts()
    {
        var b = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        RandomNumberGenerator.Shuffle(b.AsSpan());
        return new DecimaArchive.Salts(b[..12], b[12..28]);
    }

    /// <summary>A made-up exe: random bytes, with the salts as u32 arrays at 4-byte aligned offsets when given.</summary>
    static byte[] Exe(DecimaArchive.Salts? salts)
    {
        var exe = RandomNumberGenerator.GetBytes(256 << 10);
        if (salts != null)
        {
            salts.Iv.CopyTo(exe, 0x1234 * 4 + 4);   // the first word is the key's place
            salts.Data.CopyTo(exe, 0x9876 * 4);
        }
        return exe;
    }

    static byte[] Programs(byte[] vs, byte[] ps, byte[] cs, byte[] vs2, byte[] ps2, int gap = DecimaReader.ProgramGap) => Concat(
        Fill(1000, 0x11), vs, Fill(gap, 0xAB), ps, Fill(77, 0), cs, Fill(5000, 0x22),
        vs2, Fill(gap - 1, 0xAB), ps2,   // not a program: one byte short
        Fill(3000, 0), vs, Fill(gap, 0xCD), ps);

    [Fact]
    public void IndexesHorizonZeroDawnsProgramsAsPipelines()
    {
        TestEnv.BuildCodecs();
        var (vs, ps, cs, vs2, ps2) = (Hlsl.Vs(1, null), Hlsl.Ps(1, null), Hlsl.Cs(1), Hlsl.Vs(2, null), Hlsl.Ps(2, null));
        var dir = Ff7.TempDir("decima-hzd");
        var packed = Path.Combine(dir, "Packed_DX12");
        Directory.CreateDirectory(packed);
        File.WriteAllBytes(Path.Combine(packed, "Patch.bin"), Bin(Programs(vs, ps, cs, vs2, ps2)));
        File.WriteAllBytes(Path.Combine(packed, "Initial.bin"), Bin(Concat(new byte[300], vs, new byte[DecimaReader.ProgramGap], ps), chunk: 1000));
        File.WriteAllBytes(Path.Combine(packed, "Other.bin"), Concat(new byte[64], Hlsl.Ps(3, null)));   // an unknown magic: not read
        File.WriteAllBytes(Path.Combine(packed, "Empty.bin"), Bin([], kraken: false));   // the smallest: no chunks
        File.WriteAllBytes(Path.Combine(dir, "HorizonZeroDawn.exe"), "MZ"u8.ToArray());
        var game = new Game("steam:1151640", "Horizon Zero Dawn Complete Edition", Store.Steam, dir, Path.Combine(dir, "HorizonZeroDawn.exe"));

        var engine = SCSKiller.Core.App.ScsKiller.DefaultReaders(Ff7.TempDir("decima-readers")).Detect(game)!;   // before the carver
        Assert.Equal(new EngineInfo(DecimaReader.Family, DecimaReader.Version, DecimaReader.HorizonZeroDawn, "D3D12", false, null), engine);
        // no root signature ships and no rule builds one: a recording first
        Assert.Equal(new PlanCheck(Readiness.NeedsRecording, Planner.Record), new Planner().Check(game, engine, null, Ff7.Nvidia));
        Assert.Equal(Readiness.NeedsRecording, new Planner().Check(game, engine, null, Ff7.Amd).Readiness);

        var log = new List<string>();
        var index = new DecimaReader(Ff7.TempDir("decima-data")).Index(game, engine, new Progress(log.Add), CancellationToken.None);
        Assert.Equal(["warning: 1 VS not followed by a PS 48 bytes later", "warning: 1 .bin files of an unknown archive version skipped"], Warnings(log));
        Assert.Equal(new[] { vs, ps, cs, vs2, ps2 }.Select(Sha).Order(), index.Shaders.Keys.Order());
        Assert.All(index.Shaders.Values, s => Assert.Null(s.RootSignature));
        Assert.Equal(new[] { $@"Packed_DX12\Initial.bin|{Sha(vs)},{Sha(ps)}|True", $@"Packed_DX12\Patch.bin|{Sha(cs)}|True", $@"Packed_DX12\Patch.bin|{Sha(vs2)},{Sha(ps2)}|False" },
            index.Maps.Select(m => $"{m.Library}|{string.Join(',', m.Shaders)}|{m.IsPipeline}"));

        var got = new Dictionary<string, byte[]>();
        new DecimaReader().ReadShaders(game, engine, new[] { vs, ps2, cs }.Select(Sha).ToHashSet(), (h, b) => got[h] = b, CancellationToken.None);   // re-indexes
        Assert.Equal(new[] { vs, ps2, cs }.Select(Sha).Order(), got.Keys.Order());
        Assert.All(got, g => Assert.Equal(g.Key, Sha(g.Value)));
    }

    [Fact]
    public void ReadsDeathStrandingWithTheSaltsInItsExe()
    {
        TestEnv.BuildCodecs();
        var salts = NewSalts();
        var (vs, ps, cs, vs2, ps2) = (Hlsl.Vs(4, null), Hlsl.Ps(4, null), Hlsl.Cs(4), Hlsl.Vs(5, null), Hlsl.Ps(5, null));
        var dir = Ff7.TempDir("decima-ds");
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        File.WriteAllBytes(Path.Combine(dir, @"data\0123456789abcdef0123456789abcdef.bin"), Bin(Programs(vs, ps, cs, vs2, ps2, DecimaReader.DeathStrandingProgramGap), salts: salts));
        // Horizon Zero Dawn's gap isn't a program here
        File.WriteAllBytes(Path.Combine(dir, @"data\fedcba9876543210fedcba9876543210.bin"), Bin(Concat(new byte[5000], vs, Fill(DecimaReader.ProgramGap, 0xAB), ps2), chunk: 2048, salts: salts));
        File.WriteAllBytes(Path.Combine(dir, @"data\000000000000000000000000000000.bin"), Bin([], salts: salts, kraken: false));   // the smallest: no chunks
        File.WriteAllBytes(Path.Combine(dir, "ds.exe"), Exe(salts));
        var game = new Game("steam:1850570", "DEATH STRANDING DIRECTOR'S CUT", Store.Steam, dir, Path.Combine(dir, "ds.exe"));
        var data = Ff7.TempDir("decima-ds-data");

        var reader = new DecimaReader(data);
        var engine = reader.Detect(game, out var notes)!;
        Assert.Equal(new EngineInfo(DecimaReader.Family, DecimaReader.Version, DecimaReader.DeathStranding, "D3D12", false, null), engine);
        Assert.StartsWith("found in the exe", notes);
        var keyFile = Path.Combine(new SCSKiller.Core.App.AppStore(data).GameDir(game.Id), "decima.key");
        Assert.Equal(Convert.ToHexString([.. salts.Iv, .. salts.Data]), File.ReadAllText(keyFile));
        Assert.Equal(engine, reader.Detect(game, out notes));
        Assert.Equal("stored salts", notes);
        Assert.Equal(new PlanCheck(Readiness.NeedsRecording, Planner.Record), new Planner().Check(game, engine, null, Ff7.Nvidia));

        var log = new List<string>();
        var index = reader.Index(game, engine, new Progress(log.Add), CancellationToken.None);
        Assert.Equal(new[] { vs, ps, cs, vs2, ps2 }.Select(Sha).Order(), index.Shaders.Keys.Order());
        // a VS and the PS 52 bytes after it are a program; a byte short, or 48 bytes, isn't
        Assert.Equal([$"{Sha(vs)},{Sha(ps)}"], index.Maps.Where(m => m.IsPipeline && m.Shaders.Count > 1).Select(m => string.Join(',', m.Shaders)));
        Assert.Equal(["warning: 2 VS not followed by a PS 52 bytes later"], Warnings(log));
        var got = new Dictionary<string, byte[]>();
        new DecimaReader(data).ReadShaders(game, engine, new[] { ps, cs }.Select(Sha).ToHashSet(), (h, b) => got[h] = b, CancellationToken.None);
        Assert.Equal(new[] { ps, cs }.Select(Sha).Order(), got.Keys.Order());

        // an exe without them: the game files stay encrypted, and the scan isn't run again for that exe
        File.WriteAllBytes(game.ExePath, Exe(null));
        var other = Ff7.TempDir("decima-ds-nosalts");
        engine = new DecimaReader(other).Detect(game, out notes)!;
        Assert.True(engine.Encrypted);
        Assert.StartsWith("not found: no header salt", notes);
        Assert.Equal(Readiness.Unsupported, new Planner().Check(game, engine, null, Ff7.Nvidia).Readiness);
        new DecimaReader(other).Detect(game, out var again);
        Assert.Equal(notes, again);
        Assert.Throws<InvalidDataException>(() => new DecimaReader(other).Index(game, engine, null, CancellationToken.None));
    }

    /// <summary>The crypto matches the probe's Python on the same synthetic salts (none of them a game's).</summary>
    [Fact]
    public void HashesAndDecryptsAsThePublishedAlgorithm()
    {
        var m = new byte[16];
        DecimaArchive.Murmur3([.. Enumerable.Range(0, 16).Select(i => (byte)i)], m);
        Assert.Equal("29de86174ffab552401e4260c55b4d3c", Convert.ToHexStringLower(m));
        DecimaArchive.Murmur3(Fill(16, 0xFF), m);
        Assert.Equal("77b94bfbd80adb2ed564312b76aabb14", Convert.ToHexStringLower(m));

        static byte[] Words(params uint[] w) => [.. w.SelectMany(BitConverter.GetBytes)];
        var salts = new DecimaArchive.Salts(Words(0x11223344, 0x55667788, 0x99AABBCC), Words(0xA1B2C3D4, 0x0BADF00D, 0xCAFEBABE, 0x01234567));
        var header = new byte[32];
        salts.Xor(header.AsSpan(0, 16), 0x12345678);
        salts.Xor(header.AsSpan(16), 0x12345679);
        Assert.Equal("0939a5aef765f9cc396138046723537b0e791d756817dd7f790e0aac0c81ca45", Convert.ToHexStringLower(header));
        var entry = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        salts.XorEntry(entry);
        Assert.Equal("c7d05a65d21d9e645dc7df320c0d0e0fdc45966659aad4380c71dd521c1d1e1f", Convert.ToHexStringLower(entry));
        var key = new byte[16];
        salts.ChunkKey([.. Enumerable.Range(0, 16).Select(i => (byte)i)], key);
        Assert.Equal("6dda66f7005a10a1b5cc6c78b6b24250", Convert.ToHexStringLower(key));
    }

    /// <summary>Counts, sizes and offsets the file can't hold are rejected before anything is allocated for them.</summary>
    [Fact]
    public void RejectsMalformedArchivesWithoutBigAllocations()
    {
        TestEnv.BuildCodecs();
        var good = Bin(Concat(new byte[5000], Hlsl.Ps(6, null)), chunk: 1024);
        var dir = Ff7.TempDir("decima-malformed");
        string Write(byte[] b, string name) { var p = Path.Combine(dir, name); File.WriteAllBytes(p, b); return p; }
        Assert.NotNull(DecimaArchive.Open(Write(good, "good.bin"), null));

        static byte[] Edit(byte[] b, Action<byte[]> edit) { var c = b.ToArray(); edit(c); return c; }
        const int table = DecimaArchive.HeaderSize + 3 * DecimaArchive.EntrySize;   // the first (last-chunk) entry
        var bad = new Dictionary<string, byte[]>
        {
            ["magic"] = Edit(good, b => b[0] ^= 1),
            ["file size"] = Edit(good, b => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), b.Length + 1)),
            ["file entries"] = Edit(good, b => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24), long.MaxValue / 16)),
            ["negative file entries"] = Edit(good, b => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24), -1)),
            ["chunk entries"] = Edit(good, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), uint.MaxValue)),
            ["chunk entries past the file"] = Edit(good, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), 1000)),
            ["max chunk"] = Edit(good, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(36), uint.MaxValue)),
            ["chunk over max"] = Edit(good, b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(table + 8), BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(36)) + 1)),
            ["empty chunk"] = Edit(good, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(table + 8), 0)),
            ["packed past the end"] = Edit(good, b => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(table + 16), b.Length - 10)),
            ["packed offset wraps"] = Edit(good, b => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(table + 16), long.MaxValue)),
            ["negative packed offset"] = Edit(good, b => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(table + 16), -1)),
            ["packed size"] = Edit(good, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(table + 24), uint.MaxValue)),
            ["negative unpacked offset"] = Edit(good, b => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(table), long.MinValue)),
            ["far-off chunk"] = Edit(good, b => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(table), 1L << 60)),
            ["chunk end wraps"] = Edit(good, b => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(table), long.MaxValue)),
            ["overlapping chunks"] = Edit(good, b => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(table), 1024)),
            ["truncated table"] = good[..(table + 10)],
            ["truncated header"] = good[..20],
        };
        foreach (var (name, b) in bad)
        {
            var path = Write(b, name + ".bin");
            var before = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(DecimaArchive.Open(path, null) == null, name);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20, name);
        }

        // a small file of tiny chunks that would unpack to far more than it holds
        var bomb = new List<byte[]>();
        var head = new byte[DecimaArchive.HeaderSize];
        const int n = 4096;
        var length = DecimaArchive.HeaderSize + DecimaArchive.EntrySize * n + 2;
        BinaryPrimitives.WriteUInt32LittleEndian(head, DecimaArchive.Plain);
        BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(8), length);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(32), n);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(36), DecimaArchive.MaxChunk);
        bomb.Add(head);
        for (var i = 0; i < n; i++)
        {
            var e = new byte[DecimaArchive.EntrySize];
            BinaryPrimitives.WriteInt64LittleEndian(e, (long)i * DecimaArchive.MaxChunk);
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(8), DecimaArchive.MaxChunk);
            BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(16), length - 2);
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(24), 2);
            bomb.Add(e);
        }
        bomb.Add([0xCC, 0x06]);
        Assert.Null(DecimaArchive.Open(Write(Concat([.. bomb]), "bomb.bin"), null));
    }

    /// <summary>A chunk that doesn't decompress fails its reads, and the index keeps the shaders before it with a warning.</summary>
    [Fact]
    public void KeepsTheShadersBeforeAChunkThatDoesntDecompress()
    {
        TestEnv.BuildCodecs();
        var (ps, ps2) = (Hlsl.Ps(7, null), Hlsl.Ps(8, null));
        var stream = Concat(ps, new byte[8192], ps2);
        var b = Bin(stream, chunk: 1024);
        var chunks = (stream.Length + 1023) / 1024 + 1;
        var at = ps.Length + 8192;
        b[DecimaArchive.HeaderSize + DecimaArchive.EntrySize * (3 + chunks) + at / 1024 * 1026] = 0xFF;   // ps2's chunk: not an Oodle block
        var dir = Ff7.TempDir("decima-broken");
        Directory.CreateDirectory(Path.Combine(dir, "Packed_DX12"));
        File.WriteAllBytes(Path.Combine(dir, @"Packed_DX12\Patch.bin"), b);
        using (var a = DecimaArchive.Open(Path.Combine(dir, @"Packed_DX12\Patch.bin"), null)!)
        {
            Assert.Throws<InvalidDataException>(() => a.Read(at, new byte[1]));
            var head = new byte[ps.Length];
            a.Read(0, head);
            Assert.Equal(ps, head);
        }
        var game = new Game("steam:1151640", "Horizon Zero Dawn Complete Edition", Store.Steam, dir, Path.Combine(dir, "HorizonZeroDawn.exe"));
        var engine = new DecimaReader(Ff7.TempDir("decima-broken-data")).Detect(game)!;
        var log = new List<string>();
        var index = new DecimaReader().Index(game, engine, new Progress(log.Add), CancellationToken.None);
        Assert.Equal([Sha(ps)], index.Shaders.Keys);
        Assert.Equal(["warning: 1 archives stop at a chunk that doesn't decompress to its size (the shaders before it are kept)"], Warnings(log));
    }

    /// <summary>The log's warnings, without the timing that ends the line.</summary>
    static string[] Warnings(List<string> log) =>
        [.. log.SelectMany(l => System.Text.RegularExpressions.Regex.Replace(l, @" \([\d.]+s\)$", "").Split("; ")).Where(w => w.StartsWith("warning:"))];

    /// <summary>Each real-data shape the reader doesn't expect is a warning in the index log, or the reason the game can't
    /// be indexed: a header's file size that isn't the file's, a chunk decoding to another size, a .bin of another
    /// version, the read budget hit.</summary>
    [Fact]
    public void WarnsAboutEveryUnexpectedShape()
    {
        TestEnv.BuildCodecs();
        var (ps, ps2) = (Hlsl.Ps(10, null), Hlsl.Ps(11, null));
        var dir = Ff7.TempDir("decima-shapes");
        var packed = Path.Combine(dir, "Packed_DX12");
        Directory.CreateDirectory(packed);
        var game = new Game("steam:1151640", "Horizon Zero Dawn Complete Edition", Store.Steam, dir, Path.Combine(dir, "HorizonZeroDawn.exe"));
        string[] Index(DecimaReader? reader = null)
        {
            var log = new List<string>();
            (reader ?? new DecimaReader()).Index(game, new DecimaReader().Detect(game)!, new Progress(log.Add), CancellationToken.None);
            return Warnings(log);
        }

        // only .bin files of another version: not indexable, and said so
        File.WriteAllBytes(Path.Combine(packed, "Future.bin"), Concat([0x60, 0x40, 0x30, 0x20], new byte[100]));
        Assert.Equal("an archive version SCSKiller doesn't read", new DecimaReader().Detect(game)!.Unsupported);

        // the header's file size isn't the file's
        var wrongSize = Bin(Concat(ps, new byte[DecimaReader.ProgramGap]));
        BinaryPrimitives.WriteInt64LittleEndian(wrongSize.AsSpan(8), wrongSize.Length + 1);
        File.WriteAllBytes(Path.Combine(packed, "Patch.bin"), wrongSize);
        Assert.Equal($"its archives don't open ({DecimaArchive.WrongSize})", new DecimaReader().Detect(game)!.Unsupported);
        File.WriteAllBytes(Path.Combine(packed, "Initial.bin"), Bin(ps));
        Assert.Equal(["warning: 1 archives don't open: its header gives another file size", "warning: 1 .bin files of an unknown archive version skipped"], Index());
        File.Delete(Path.Combine(packed, "Patch.bin"));
        File.Delete(Path.Combine(packed, "Future.bin"));
        Assert.Empty(Index());

        // a chunk that decodes to fewer bytes than its entry says: the last one, the Kraken chunk, first in the reversed table
        var shortChunk = Bin(Concat(ps2, new byte[100]), chunk: 4096);
        var entry = DecimaArchive.HeaderSize + 3 * DecimaArchive.EntrySize + 8;
        BinaryPrimitives.WriteInt32LittleEndian(shortChunk.AsSpan(entry), BinaryPrimitives.ReadInt32LittleEndian(shortChunk.AsSpan(entry)) + 1);
        File.WriteAllBytes(Path.Combine(packed, "Remainder.bin"), shortChunk);
        Assert.Equal(["warning: 1 archives stop at a chunk that doesn't decompress to its size (the shaders before it are kept)"], Index());
        File.Delete(Path.Combine(packed, "Remainder.bin"));

        // headers claiming containers past the window's end, read whole again and again: the budget stops the scan
        var bogus = Concat("DXBC"u8.ToArray(), new byte[16], BitConverter.GetBytes(1), BitConverter.GetBytes(32 << 10), BitConverter.GetBytes(1), BitConverter.GetBytes(0xFFFFFF));
        File.WriteAllBytes(Path.Combine(packed, "DLC1.bin"), Bin(Concat(ps2, Concat([.. Enumerable.Repeat(bogus, 100)]), new byte[64 << 10])));
        Assert.Equal(["warning: 1 archives hit the read budget, the rest of them not scanned"], Index(new DecimaReader { Window = 4096 }));
    }

    /// <summary>The real-install check lets through only a few lone VS.</summary>
    [Fact]
    public void GameTestsAllowOnlyAFewLoneVsAndUnlinkedPrograms()
    {
        var max = DecimaGameTests.MaxKnownWarning;
        DecimaGameTests.OnlyKnownWarnings(["Horizon Zero Dawn: 39 archives: 39994 shaders; warning: 9 programs whose VS doesn't feed their PS, pooled instead; warning: 2 VS not followed by a PS 48 bytes later (101.1s)"]);
        DecimaGameTests.OnlyKnownWarnings(["Death Stranding: 144 archives: 15549 shaders; warning: 3 VS not followed by a PS 52 bytes later (93.8s)"]);
        foreach (var bad in new[]
                 {
                     $"x; warning: {max + 1} VS not followed by a PS 48 bytes later (1.0s)",
                     $"x; warning: {max + 1} programs whose VS doesn't feed their PS, pooled instead (1.0s)",
                     "x; warning: only 0 of 619499 VS have a PS 48 bytes later: no programs, VS and PS pooled per archive (1.0s)",
                     "x; warning: 2 VS not followed by a PS 48 bytes later; warning: 1 archives hit the read budget, the rest of them not scanned (1.0s)",
                 })
            Assert.ThrowsAny<Exception>(() => DecimaGameTests.OnlyKnownWarnings([bad]));
    }

    /// <summary>Where most VS lack their PS at the game's gap, the layout isn't that one: no program is trusted, and the
    /// log says so in one warning instead of a count of lone VS.</summary>
    [Fact]
    public void FallsBackToPoolsWhenMostVsLackTheirPs()
    {
        TestEnv.BuildCodecs();
        var (vs, ps, vs2, ps2) = (Hlsl.Vs(12, null), Hlsl.Ps(12, null), Hlsl.Vs(13, null), Hlsl.Ps(13, null));
        var dir = Ff7.TempDir("decima-fallback");
        Directory.CreateDirectory(Path.Combine(dir, "Packed_DX12"));
        File.WriteAllBytes(Path.Combine(dir, @"Packed_DX12\Patch.bin"), Bin(Concat(
            vs, Fill(DecimaReader.ProgramGap, 1), ps, Fill(100, 0),
            vs2, Fill(20, 2), ps2, Fill(100, 0), vs2, Fill(20, 2), ps2, Fill(100, 0), vs2, Fill(20, 2), ps2)));
        var game = new Game("steam:1151640", "Horizon Zero Dawn Complete Edition", Store.Steam, dir, Path.Combine(dir, "HorizonZeroDawn.exe"));
        var log = new List<string>();
        var index = new DecimaReader().Index(game, new DecimaReader().Detect(game)!, new Progress(log.Add), CancellationToken.None);
        Assert.Equal(["warning: only 1 of 4 VS have a PS 48 bytes later: no programs, VS and PS pooled per archive"], Warnings(log));
        Assert.DoesNotContain(index.Maps, m => m.Shaders.Count > 1 && m.IsPipeline);
        Assert.Equal(new[] { vs, ps, vs2, ps2 }.Select(Sha).Order(), index.Maps.Single(m => !m.IsPipeline).Shaders.Order());
    }

    sealed class Progress(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }
}
