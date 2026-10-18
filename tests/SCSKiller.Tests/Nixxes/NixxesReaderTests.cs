using System.Buffers.Binary;
using System.Security.Cryptography;
using K4os.Compression.LZ4;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.NaughtyDog;
using SCSKiller.Core.Nixxes;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Carved;
using SCSKiller.Tests.Planning;
using static SCSKiller.Tests.NaughtyDog.NaughtyDogReaderTests;

namespace SCSKiller.Tests.Nixxes;

/// <summary>The Nixxes reader on synthetic installs laid out as Ghost of Tsushima's (PSARC archives in DSAR files),
/// Ratchet &amp; Clank: Rift Apart's (whole DSAR streams, DXBC and DXIL twins, shaders in the exe) and HELLDIVERS 2's
/// (DSAR bundles, GameGuard), with shaders compiled here (<see cref="Hlsl"/>) or built by hand (DXIL).</summary>
public class NixxesReaderTests
{
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    /// <summary>A DSAR file over <paramref name="stream"/> in blocks of <paramref name="block"/> bytes: an all-zero block a
    /// gap (codec 254, nothing stored), every third block stored (codec 0), the others LZ4 (codec 3).</summary>
    internal static byte[] Dsar(byte[] stream, int block = 4096)
    {
        var blocks = new List<(long Start, int Size, byte[] Data, byte Codec)>();
        for (var at = 0; at < stream.Length; at += block)
        {
            var b = stream.AsSpan(at, Math.Min(block, stream.Length - at)).ToArray();
            if (b.All(x => x == 0)) { blocks.Add((at, b.Length, [], 254)); continue; }
            if (blocks.Count % 3 == 2) { blocks.Add((at, b.Length, b, 0)); continue; }
            var packed = new byte[LZ4Codec.MaximumOutputSize(b.Length)];
            blocks.Add((at, b.Length, packed[..LZ4Codec.Encode(b, packed)], 3));
        }
        var header = 32 + 32 * blocks.Count;
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write("DSAR"u8); w.Write((ushort)3); w.Write((ushort)1); w.Write(blocks.Count); w.Write(header); w.Write((long)stream.Length); w.Write(0L);
        long fileAt = header;
        foreach (var (start, size, data, codec) in blocks)
        {
            w.Write(start); w.Write(fileAt); w.Write(size); w.Write(data.Length); w.Write(codec); w.Write(new byte[7]);
            fileAt += data.Length;
        }
        foreach (var b in blocks) w.Write(b.Data);
        return o.ToArray();
    }

    /// <summary>A DXIL container: a program header for <paramref name="kind"/> (0 PS, 1 VS, 5 CS) at shader model 6.0, made
    /// distinct by <paramref name="seed"/>.</summary>
    internal static byte[] Dxil(int kind, int seed)
    {
        byte[] part = [.. "DXIL"u8, .. BitConverter.GetBytes(8), .. BitConverter.GetBytes(kind << 16 | 0x60), .. BitConverter.GetBytes(seed)];
        var size = 36 + part.Length;
        return [.. "DXBC"u8, .. new byte[16], .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(size), .. BitConverter.GetBytes(1),
            .. BitConverter.GetBytes(36), .. part];
    }

    static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    /// <summary>A package: a KCAP header, then the containers 16-byte aligned, the first past a DSAR block boundary.</summary>
    static byte[] Xpps(params byte[][] containers) =>
        Concat([[.. "KCAP"u8, 0x1f, 0, 0x0b, 0, .. new byte[5000]], .. containers.Select(c => Concat(c, new byte[(16 - c.Length % 16) % 16]))]);

    [Fact]
    public void IndexesGhostOfTsushimasPackagesEachAsAPool()
    {
        var (vs, ps, ps2, cs, notRead) = (Hlsl.Vs(1, null), Hlsl.Ps(1, null), Hlsl.Ps(2, null), Hlsl.Cs(1), Hlsl.Ps(3, null));
        var dir = Ff7.TempDir("nixxes-sucker-punch");
        var psarc = Path.Combine(dir, @"cache_pc\psarc");
        Directory.CreateDirectory(psarc);
        File.WriteAllBytes(Path.Combine(psarc, "gapack_misc_a.psarc"), Dsar(Archive(("/core_common.sprig.xpps", Xpps(vs, ps, cs)), ("/all_shaders.texmeshman", notRead))));
        File.WriteAllBytes(Path.Combine(psarc, "gapack_misc_b.psarc"), Dsar(Archive(("/bandit.xpps", Xpps(ps, ps2)))));
        File.WriteAllBytes(Path.Combine(psarc, "gapack_bitmaps_a.psarc"), Dsar(Archive(("/a.sps", notRead))));
        File.WriteAllBytes(Path.Combine(psarc, "music_e.psarc"), Archive(("/music.wem", new byte[100])));   // a plain PSARC
        File.WriteAllBytes(Path.Combine(dir, "GhostOfTsushima.exe"), notRead);
        var game = new Game("steam:2215430", "Ghost of Tsushima DIRECTOR'S CUT", Store.Steam, dir, Path.Combine(dir, "GhostOfTsushima.exe"),
            NixxesRootRuleTests.Pinned("nixxes-got").Split('@')[1]);

        var engine = SCSKiller.Core.App.ScsKiller.DefaultReaders(Ff7.TempDir("nixxes-readers")).Detect(game)!;   // before the carver
        Assert.Equal(new EngineInfo(NixxesReader.Family, NixxesReader.Version, NixxesReader.SuckerPunch, "D3D12", false, null, Game: $"{game.Id}@{game.Version}"), engine);
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.NoRecording), new Planner().Check(game, engine, null, Ff7.Nvidia));
        Assert.Equal(Readiness.NeedsRecording, new Planner().Check(game, engine, null, Ff7.Amd).Readiness);

        var reader = new NixxesReader();
        var log = new List<string>();
        var index = reader.Index(game, engine, new Progress(log.Add), CancellationToken.None);
        Assert.DoesNotContain(log, l => l.Contains("warning"));
        Assert.Equal(new[] { vs, ps, ps2, cs }.Select(Sha).Order(), index.Shaders.Keys.Order());
        Assert.All(index.Shaders.Values, s => Assert.Null(s.RootSignature));
        Assert.Equal(new[] { $@"cache_pc\psarc\gapack_misc_a.psarc:/core_common.sprig.xpps|{string.Join(',', new[] { vs, ps, cs }.Select(Sha))}",
                $@"cache_pc\psarc\gapack_misc_b.psarc:/bandit.xpps|{string.Join(',', new[] { ps, ps2 }.Select(Sha))}" },
            index.Maps.Select(m => $"{m.Library}|{string.Join(',', m.Shaders)}"));
        Assert.All(index.Maps, m => Assert.False(m.IsPipeline));

        var got = new Dictionary<string, byte[]>();
        new NixxesReader().ReadShaders(game, engine, new[] { vs, ps2, cs }.Select(Sha).ToHashSet(), (h, b) => got[h] = b, CancellationToken.None);   // re-indexes
        Assert.Equal(new[] { vs, ps2, cs }.Select(Sha).Order(), got.Keys.Order());
        Assert.All(got, g => Assert.Equal(g.Key, Sha(g.Value)));

        // the game's own roots (plan-rules.json) plan it without a recording; another game of the layout has none
        var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia, Ff7.TempDir("nixxes-plan"), null, CancellationToken.None);
        Assert.Equal("nixxes-got", plan.Stats.RsRule);
        Assert.True(plan.Stats.Generated > 0);
        var other = engine with { Game = "steam:1" };
        Assert.Equal(new PlanCheck(Readiness.NeedsRecording, Planner.Record), new Planner().Check(game with { Id = "steam:1" }, other, null, Ff7.Nvidia));
        plan = new Planner().Build(game with { Id = "steam:1" }, other, index, null, Ff7.Nvidia, Ff7.TempDir("nixxes-plan-other"), null, CancellationToken.None);
        Assert.Equal(0, plan.Stats.Generated);
    }

    /// <summary>R&amp;C's two forms of a shader: "IGSH", the two sizes, the DXBC container, then its DXIL twin.</summary>
    static byte[] Igsh(byte[] dxbc, byte[] dxil) => Concat("IGSH"u8.ToArray(), BitConverter.GetBytes(dxbc.Length), BitConverter.GetBytes(dxil.Length), dxbc, dxil);

    /// <summary>Only a DXBC container stored as a DXIL one's twin is left out: a DXBC helper with no twin, and a DXBC
    /// followed by a DXIL container without the header naming both, are kept. The exe is read only without anti-cheat,
    /// checked again when its shaders are read.</summary>
    [Fact]
    public void LeavesOutOnlyTheDxbcTwinsOfRatchetAndClank()
    {
        var (vsBc, psBc, helper, looseBc, csBc) = (Hlsl.Vs(1, null), Hlsl.Ps(1, null), Hlsl.Ps(7, null), Hlsl.Ps(8, null), Hlsl.Cs(2));
        var (vs, ps, ps2, cs, loose) = (Dxil(1, 1), Dxil(0, 1), Dxil(0, 2), Dxil(5, 1), Dxil(0, 3));
        var dir = Ff7.TempDir("nixxes-insomniac");
        Directory.CreateDirectory(Path.Combine(dir, "d"));
        File.WriteAllBytes(Path.Combine(dir, "toc"), [0x31, 0x54, 0x41, 0x44]);
        File.WriteAllBytes(Path.Combine(dir, @"d\material"), Dsar(Concat(new byte[9000], Igsh(vsBc, vs), new byte[3], Igsh(psBc, ps), helper, new byte[4], looseBc, loose)));
        File.WriteAllBytes(Path.Combine(dir, @"d\materialgraph"), Dsar(Concat(ps2, new byte[8192], ps)));
        File.WriteAllBytes(Path.Combine(dir, @"d\tex_a"), Dsar(Concat(Hlsl.Ps(9, null))));   // no shader archive: not read
        File.WriteAllBytes(Path.Combine(dir, "RiftApart.exe"), Concat("MZ"u8.ToArray(), new byte[100], cs, new byte[8], csBc));
        var game = new Game("steam:1895880", "Ratchet & Clank: Rift Apart", Store.Steam, dir, Path.Combine(dir, "RiftApart.exe"));
        Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game));

        var reader = new NixxesReader();
        var engine = reader.Detect(game)!;
        Assert.Equal(new EngineInfo(NixxesReader.Family, NixxesReader.Version, NixxesReader.Insomniac, "D3D12", false, null, Game: game.Id), engine);
        var log = new List<string>();
        var index = reader.Index(game, engine, new Progress(log.Add), CancellationToken.None);
        Assert.Equal(new[] { vs, ps, ps2, cs, helper, looseBc, loose, csBc }.Select(Sha).Order(), index.Shaders.Keys.Order());
        Assert.Contains(log, l => l.Contains("2 DXBC twins of DXIL shaders left out, 3 DXBC shaders without one kept"));
        Assert.Equal(new[] { @"d\material", @"d\materialgraph", "RiftApart.exe" }, index.Maps.Select(m => m.Library));
        Assert.Equal(new[] { vs, ps, helper, looseBc, loose }.Select(Sha), index.Maps[0].Shaders);
        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(game, engine, new[] { ps, ps2, cs }.Select(Sha).ToHashSet(), (h, b) => got[h] = b, CancellationToken.None);
        Assert.Equal(new[] { ps, ps2, cs }.Select(Sha).Order(), got.Keys.Order());

        // anti-cheat installed after the index: the exe's shaders aren't read any more
        File.WriteAllBytes(Path.Combine(dir, "GameGuard.des"), [1]);
        Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(game));
        got.Clear();
        reader.ReadShaders(game, engine, new[] { ps2, cs, csBc }.Select(Sha).ToHashSet(), (h, b) => got[h] = b, CancellationToken.None);
        Assert.Equal([Sha(ps2)], got.Keys);
        // with it from the start, the exe isn't indexed
        index = new NixxesReader().Index(game, engine, null, CancellationToken.None);
        Assert.DoesNotContain(Sha(cs), index.Shaders.Keys);
        Assert.DoesNotContain(index.Maps, m => m.Library == "RiftApart.exe");
    }

    /// <summary>HELLDIVERS 2: GameGuard is found by the anti-cheat check, its bundles are indexed (DXBC kept, no DXIL), its
    /// exe never read; no recording is possible, so the app shows it unsupported.</summary>
    [Fact]
    public void IndexesHelldiversBundlesWithoutItsExe()
    {
        var (vs, ps, cs) = (Hlsl.Vs(1, null), Hlsl.Ps(1, null), Hlsl.Cs(1));
        var dir = Ff7.TempDir("nixxes-stingray");
        Directory.CreateDirectory(Path.Combine(dir, @"bin\GameGuard"));
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        File.WriteAllBytes(Path.Combine(dir, @"bin\GameGuard\GameGuard.des"), [1]);
        File.WriteAllBytes(Path.Combine(dir, @"bin\helldivers2.exe"), Concat("MZ"u8.ToArray(), Hlsl.Ps(5, null)));
        File.WriteAllBytes(Path.Combine(dir, @"data\bundles.00.nxa"), Dsar(Concat(vs, new byte[64], ps)));
        File.WriteAllBytes(Path.Combine(dir, @"data\bundles.01.nxa"), Dsar(Concat(new byte[5000], cs)));
        File.WriteAllBytes(Path.Combine(dir, @"data\bundles.nxa"), Dsar(Concat(Hlsl.Ps(6, null))));   // the index-like one: not a bundle
        var game = new Game("steam:553850", "HELLDIVERS 2", Store.Steam, dir, Path.Combine(dir, @"bin\helldivers2.exe"));
        Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(game));

        var reader = new NixxesReader();
        var engine = reader.Detect(game)!;
        Assert.Equal(new EngineInfo(NixxesReader.Family, NixxesReader.Version, NixxesReader.Stingray, "D3D12", false, null, Game: game.Id), engine);
        Assert.Equal(Readiness.NeedsRecording, new Planner().Check(game, engine, null, Ff7.Nvidia).Readiness);
        var index = reader.Index(game, engine, null, CancellationToken.None);
        Assert.Equal(new[] { vs, ps, cs }.Select(Sha).Order(), index.Shaders.Keys.Order());
        Assert.Equal(new[] { @"data\bundles.00.nxa", @"data\bundles.01.nxa" }, index.Maps.Select(m => m.Library));
    }

    [Fact]
    public void ReadsADsarStreamAcrossGapStoredAndLz4Blocks()
    {
        var data = new byte[50_000];
        new Random(3).NextBytes(data.AsSpan(0, 20_000));
        data.AsSpan(30_000, 4000).Fill(7);
        var path = Path.Combine(Ff7.TempDir("nixxes-dsar"), "a");
        File.WriteAllBytes(path, Dsar(data));
        using var d = Core.Nixxes.Dsar.Open(path)!;
        Assert.Equal(data.Length, d.Length);
        var all = new byte[data.Length];
        d.Read(0, all);
        Assert.Equal(data, all);
        var part = new byte[10_000];
        d.Read(19_000, part);
        Assert.Equal(data.AsSpan(19_000, 10_000).ToArray(), part);
        Assert.Throws<InvalidDataException>(() => d.Read(data.Length - 1, new byte[2]));
        Assert.Null(Core.Nixxes.Dsar.Open(Path.Combine(Path.GetDirectoryName(path)!, "none")));
    }

    /// <summary>A DSAR file of explicit block records (unpacked offset, size, codec, data stored at the end of the file in
    /// order); <paramref name="at"/> overrides a block's file offset.</summary>
    static byte[] Raw(long length, params (long Start, int Size, byte Codec, byte[] Data, long? At)[] blocks)
    {
        var header = 32 + 32 * blocks.Length;
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write("DSAR"u8); w.Write((ushort)3); w.Write((ushort)1); w.Write(blocks.Length); w.Write(header); w.Write(length); w.Write(0L);
        long fileAt = header;
        foreach (var (start, size, codec, data, at) in blocks)
        {
            w.Write(start); w.Write(at ?? fileAt); w.Write(size); w.Write(data.Length); w.Write(codec); w.Write(new byte[7]);
            fileAt += data.Length;
        }
        foreach (var b in blocks) w.Write(b.Data);
        return o.ToArray();
    }

    static byte[] Lz4(byte[] b)
    {
        var packed = new byte[LZ4Codec.MaximumOutputSize(b.Length)];
        return packed[..LZ4Codec.Encode(b, packed)];
    }

    static Core.Nixxes.Dsar OpenBytes(string name, byte[] b)
    {
        var p = Path.Combine(Ff7.TempDir("nixxes-dsar-" + name), "a");
        File.WriteAllBytes(p, b);
        return Core.Nixxes.Dsar.Open(p)!;
    }

    /// <summary>Ranges no block record covers (before the first, between two, after the last) read as zeros, whatever the
    /// neighbouring blocks' codecs.</summary>
    [Fact]
    public void ReadsImplicitGapsAsZeros()
    {
        var (a, b) = ("AAAA"u8.ToArray(), "BBBB"u8.ToArray());
        foreach (var lz4 in new[] { false, true })
        {
            using var d = OpenBytes($"gaps-{lz4}", Raw(16, (2, 4, 0, a, null), (10, 4, (byte)(lz4 ? 3 : 0), lz4 ? Lz4(b) : b, null)));
            string Read(long from, int n) { var o = new byte[n]; d.Read(from, o); return string.Concat(o.Select(x => x == 0 ? '.' : (char)x)); }
            Assert.Equal("..AAAA....BBBB..", Read(0, 16));
            Assert.Equal(".", Read(0, 1));        // before the first block
            Assert.Equal("..", Read(6, 2));       // inside the gap between them
            Assert.Equal("AA....BB", Read(4, 8)); // across it
            Assert.Equal("..BB", Read(8, 4));     // from the gap into a block
            Assert.Equal("B..", Read(13, 3));     // into the gap after the last
            Assert.Equal("..", Read(14, 2));
        }
    }

    /// <summary>Offsets and sizes near long.MaxValue don't wrap past the bounds checks.</summary>
    [Fact]
    public void RejectsOffsetsThatWouldOverflow()
    {
        var dir = Ff7.TempDir("nixxes-dsar-overflow");
        Core.Nixxes.Dsar? Open(byte[] b)
        {
            var p = Path.Combine(dir, Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(p, b);
            return Core.Nixxes.Dsar.Open(p);
        }
        var data = new byte[16];
        Assert.Null(Open(Raw(64, (0, 16, 0, data, long.MaxValue - 7))));                        // file offset + packed size wraps
        Assert.Null(Open(Raw(long.MaxValue, (0, 16, 0, data, null), (long.MaxValue - 7, 16, 0, data, null))));   // unpacked offset + size wraps
        Assert.Null(Open(Raw(64, (0, 16, 0, data, -1))));
        using var d = Open(Raw(64, (0, 16, 0, data, null)))!;
        Assert.Throws<InvalidDataException>(() => d.Read(long.MaxValue - 1, new byte[4]));
        Assert.Throws<InvalidDataException>(() => d.Read(60, new byte[8]));
    }

    /// <summary>A block table over <see cref="Core.Nixxes.Dsar.MaxBlocks"/> records is refused even when the file holds it and
    /// every record is valid; one at the limit opens, or is refused unread when a meter can't pay for it.</summary>
    [Fact]
    public void RefusesABlockTableOverItsBudget()
    {
        byte[] Table(int n)
        {
            var header = 32 + 32 * n;
            var b = new byte[header];
            var w = new BinaryWriter(new MemoryStream(b));
            w.Write("DSAR"u8); w.Write((ushort)3); w.Write((ushort)1); w.Write(n); w.Write(header); w.Write((long)n); w.Write(0L);
            for (var i = 0; i < n; i++) { w.Write((long)i); w.Write(0L); w.Write(1); w.Write(1); w.Write((byte)0); w.Write(new byte[7]); }   // one stored byte each
            return b;
        }
        var dir = Ff7.TempDir("nixxes-dsar-table");
        File.WriteAllBytes(Path.Combine(dir, "at"), Table(Core.Nixxes.Dsar.MaxBlocks));
        File.WriteAllBytes(Path.Combine(dir, "over"), Table(Core.Nixxes.Dsar.MaxBlocks + 1));
        using (var d = Core.Nixxes.Dsar.Open(Path.Combine(dir, "at"))) Assert.NotNull(d);
        Assert.Null(Core.Nixxes.Dsar.Open(Path.Combine(dir, "over")));

        // the table is charged to a meter before it's read, and not read when the meter can't pay for it
        var meter = new NixxesReader.Meter(long.MaxValue);
        using (var d = Core.Nixxes.Dsar.Open(Path.Combine(dir, "at"), meter)) Assert.NotNull(d);
        Assert.Equal(32L * Core.Nixxes.Dsar.MaxBlocks, meter.Read);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(Core.Nixxes.Dsar.Open(Path.Combine(dir, "at"), new NixxesReader.Meter(32L * Core.Nixxes.Dsar.MaxBlocks - 1)));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 64 << 10, $"{allocated} bytes allocated");
    }

    /// <summary>Counts and sizes are checked against the file before anything is allocated from them; a block that can't be
    /// decoded fails the read that needs it, not the file.</summary>
    [Fact]
    public void RejectsMalformedDsarFiles()
    {
        var good = Dsar(Concat(Hlsl.Ps(1, null), new byte[5000], Hlsl.Ps(2, null)), block: 1024);
        var dir = Ff7.TempDir("nixxes-dsar-bad");
        Core.Nixxes.Dsar? Open(byte[] b)
        {
            var p = Path.Combine(dir, Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(p, b);
            return Core.Nixxes.Dsar.Open(p);
        }
        byte[] With(Action<byte[]> change) { var b = good.ToArray(); change(b); return b; }
        Assert.NotNull(Open(good));
        Assert.Null(Open(good[..20]));
        Assert.Null(Open(With(b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), int.MaxValue))));   // a block count the header can't hold
        Assert.Null(Open(With(b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), 64))));            // a header too small for its table
        Assert.Null(Open(good[..^10]));                                                                      // the last block past the file's end
        Assert.Null(Open(With(b => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(32 + 32), 0))));         // blocks out of order
        Assert.Null(Open(With(b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(32 + 16), -1))));        // a block over 2 GB

        var firstLz4 = Enumerable.Range(0, BinaryPrimitives.ReadInt32LittleEndian(good.AsSpan(8))).First(i => good[32 + 32 * i + 24] == 3);
        var at = (int)BinaryPrimitives.ReadInt64LittleEndian(good.AsSpan(32 + 32 * firstLz4 + 8));
        using (var d = Open(With(b => b.AsSpan(at, 8).Fill(0xFF)))!) Assert.Throws<InvalidDataException>(() => d.Read(1024L * firstLz4, new byte[16]));
        using (var d = Open(With(b => b[32 + 32 * firstLz4 + 24] = 2))!) Assert.Throws<InvalidDataException>(() => d.Read(1024L * firstLz4, new byte[16]));
        // a valid LZ4 block one byte over the cap is refused before its buffer is allocated; one at the cap reads
        var full = new byte[Core.Nixxes.Dsar.MaxBlock + 1];
        full.AsSpan().Fill(7);
        using (var d = Open(Dsar(full, block: full.Length))!) Assert.Throws<InvalidDataException>(() => d.Read(0, new byte[16]));
        using (var d = Open(Dsar(full[1..], block: full.Length - 1))!)
        {
            var b = new byte[16];
            d.Read(100, b);
            Assert.All(b, x => Assert.Equal(7, x));
        }
    }

    /// <summary>A corrupt archive is a warning: the others are indexed; Detect doesn't throw.</summary>
    [Fact]
    public void ACorruptArchiveDoesntStopTheIndex()
    {
        var (ps, other) = (Hlsl.Ps(1, null), Hlsl.Ps(2, null));
        var dir = Ff7.TempDir("nixxes-corrupt");
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        var bad = Dsar(Concat(new byte[100], other), block: 512);
        for (var i = 0; i < BinaryPrimitives.ReadInt32LittleEndian(bad.AsSpan(8)); i++) bad[32 + 32 * i + 24] = 2;   // GDeflate: not decoded
        File.WriteAllBytes(Path.Combine(dir, @"data\bundles.00.nxa"), bad);
        File.WriteAllBytes(Path.Combine(dir, @"data\bundles.01.nxa"), Dsar(ps));
        var game = new Game("steam:553850", "HELLDIVERS 2", Store.Steam, dir, Path.Combine(dir, "x.exe"));
        var reader = new NixxesReader();
        var engine = reader.Detect(game)!;
        var log = new List<string>();
        var index = reader.Index(game, engine, new Progress(log.Add), CancellationToken.None);
        Assert.Contains(Sha(ps), index.Shaders.Keys);
        Assert.Contains(log, l => l.Contains("warning: 1 sources don't decompress"));

        File.WriteAllBytes(Path.Combine(dir, @"data\bundles.01.nxa"), "DSAR"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => reader.Index(game, engine, null, CancellationToken.None));
        File.Delete(Path.Combine(dir, @"data\bundles.00.nxa"));
        Assert.Null(reader.Detect(game));   // no bundle opens

        // a package whose blocks don't decode isn't Ghost of Tsushima's
        var psarc = Path.Combine(dir, @"cache_pc\psarc");
        Directory.CreateDirectory(psarc);
        var archive = Dsar(Archive(("/a.xpps", Concat(new byte[100], ps))), block: 512);
        for (var i = 0; i < BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(8)); i++) archive[32 + 32 * i + 24] = 2;
        File.WriteAllBytes(Path.Combine(psarc, "gapack_misc_a.psarc"), archive);
        Assert.Null(reader.Detect(game));
    }

    /// <summary>A bundle whose later blocks don't decode keeps the shaders before them, and says so.</summary>
    [Fact]
    public void KeepsTheShadersBeforeABlockThatFails()
    {
        var (ps, other) = (Hlsl.Ps(1, null), Hlsl.Ps(2, null));
        var dir = Ff7.TempDir("nixxes-partial");
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        const int block = 1024;
        var good = (ps.Length + 100) / block + 1;
        var bundle = Dsar(Concat(new byte[100], ps, new byte[good * block - 100 - ps.Length], other), block);
        for (var i = good; i < BinaryPrimitives.ReadInt32LittleEndian(bundle.AsSpan(8)); i++) bundle[32 + 32 * i + 24] = 2;
        File.WriteAllBytes(Path.Combine(dir, @"data\bundles.00.nxa"), bundle);
        var game = new Game("steam:553850", "HELLDIVERS 2", Store.Steam, dir, Path.Combine(dir, "x.exe"));
        var reader = new NixxesReader();
        var log = new List<string>();
        var index = reader.Index(game, reader.Detect(game)!, new Progress(log.Add), CancellationToken.None);
        Assert.Equal([Sha(ps)], index.Shaders.Keys);
        Assert.Equal([Sha(ps)], index.Maps.Single().Shaders);
        Assert.Contains(log, l => l.Contains("warning: 1 sources don't decompress"));
        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(game, reader.Detect(game)!, new HashSet<string> { Sha(ps) }, (h, b) => got[h] = b, CancellationToken.None);
        Assert.Equal(ps, got[Sha(ps)]);
    }

    /// <summary>Detect reads at most 64 MB: a material archive whose first container is past that (valid otherwise) isn't
    /// confirmed; one before it is.</summary>
    [Fact]
    public void DetectStopsAtItsBudget()
    {
        var ps = Hlsl.Ps(1, null);
        foreach (var (mb, found) in new[] { (70, false), (10, true) })
        {
            var dir = Ff7.TempDir($"nixxes-budget-{mb}");
            Directory.CreateDirectory(Path.Combine(dir, "d"));
            File.WriteAllBytes(Path.Combine(dir, "toc"), [1]);
            File.WriteAllBytes(Path.Combine(dir, @"d\material"), Dsar(Concat(new byte[mb << 20], ps), block: 1 << 20));
            File.WriteAllBytes(Path.Combine(dir, @"d\materialgraph"), Dsar(new byte[4096]));
            var game = new Game("steam:1895880", "Ratchet & Clank: Rift Apart", Store.Steam, dir, Path.Combine(dir, "RiftApart.exe"));
            Assert.Equal(found, new NixxesReader().Detect(game) != null);
        }
    }

    static NixxesReader.Readable Bytes(byte[] data, long readable = long.MaxValue, Action<int>? requested = null) =>
        new(data.Length, (from, into) =>
        {
            requested?.Invoke(into.Length);
            if (from + into.Length > readable) throw new InvalidDataException("a block that doesn't decode");
            data.AsSpan((int)from, into.Length).CopyTo(into);
        });

    /// <summary>Containers crossing the window's end, a header cut by it, and a "DXBC" that frames no container; a budget
    /// is never overrun.</summary>
    [Fact]
    public void CarvesAcrossWindows()
    {
        var (a, b, c) = (Hlsl.Ps(1, null), Hlsl.Vs(1, null), Hlsl.Cs(1));
        var data = Concat(new byte[50], a, "DXBC"u8.ToArray(), new byte[11], b, new byte[1], c, "DXBC"u8.ToArray());
        foreach (var window in new[] { 64, 100, 777, a.Length + 60, data.Length })
        {
            var found = new List<(long, string)>();
            NixxesReader.Carve(Bytes(data), new byte[window], (at, x) => found.Add((at, Sha(x.ToArray()))), new NixxesReader.Meter(long.MaxValue), () => false, CancellationToken.None);
            Assert.Equal(new[] { (50L, Sha(a)), (50L + a.Length + 15, Sha(b)), (50L + a.Length + 15 + b.Length + 1, Sha(c)) }, found);
            var asked = 0L;
            var meter = new NixxesReader.Meter(window);
            NixxesReader.Carve(Bytes(data, requested: n => asked += n), new byte[window], (_, _) => { }, meter, () => false, CancellationToken.None);
            Assert.Equal(meter.Read, asked);
            Assert.True(asked <= window);
        }
    }

    /// <summary>Headers claiming the largest container there is, over and over, don't make it read past its budget: a probe
    /// the budget can't pay for isn't made.</summary>
    [Fact]
    public void CarvingChargesEveryRead()
    {
        var fake = Concat("DXBC"u8.ToArray(), new byte[16], BitConverter.GetBytes(1), BitConverter.GetBytes(Dxbc.MaxSize), BitConverter.GetBytes(1), BitConverter.GetBytes(36));
        var data = new byte[3 * Dxbc.MaxSize];
        for (var i = 0; i < 100; i++) fake.CopyTo(data, i * 64);
        var asked = 0L;
        const long budget = 64L << 20;
        var meter = new NixxesReader.Meter(budget);
        NixxesReader.Carve(Bytes(data, requested: n => asked += n), new byte[16 << 20], (_, _) => { }, meter, () => false, CancellationToken.None);
        Assert.True(asked <= budget, $"{asked} bytes asked");
        Assert.Equal(meter.Read, asked);
    }

    /// <summary>A failure at an unaligned point keeps a container ending just before it, or exactly at it (an odd offset).</summary>
    [Fact]
    public void KeepsAContainerEndingJustBeforeTheFailingBytes()
    {
        var c = Dxil(0, 1);
        Assert.Equal(52, c.Length);
        foreach (var (at, readable) in new[] { (1040, 1100), (1041, 1041 + 52) })
        {
            var data = new byte[5000];
            c.CopyTo(data, at);
            var found = new List<long>();
            Assert.Throws<InvalidDataException>(() => NixxesReader.Carve(Bytes(data, readable: readable), new byte[4096], (o, _) => found.Add(o),
                new NixxesReader.Meter(long.MaxValue), () => false, CancellationToken.None));
            Assert.Equal([(long)at], found);
        }
    }

    /// <summary>Narrowing a failing window to its readable bytes is charged too: every byte asked for is on the meter, and a
    /// budget that can't pay for the narrowing stops it.</summary>
    [Fact]
    public void ChargesTheReadsThatFindAFailure()
    {
        var c = Dxil(0, 1);
        var data = new byte[200_000];
        c.CopyTo(data, 1041);
        foreach (var budget in new long[] { 1 << 20, 3 * 4096 + 100 })
        {
            var asked = 0L;
            var meter = new NixxesReader.Meter(budget);
            var found = new List<long>();
            Assert.Throws<InvalidDataException>(() => NixxesReader.Carve(Bytes(data, readable: 1093, requested: n => asked += n), new byte[4096],
                (o, _) => found.Add(o), meter, () => false, CancellationToken.None));
            Assert.Equal(meter.Read, asked);
            Assert.True(asked > 4096 && asked <= budget, $"{asked} bytes asked");
            if (budget == 1 << 20) Assert.Equal([1041L], found);
        }
    }

    /// <summary>Cancelling stops a window's scan at its next candidate, not at the window's end.</summary>
    [Fact]
    public void CarvingStopsAtTheNextCandidateOnceCancelled()
    {
        var data = Concat(Dxil(0, 1), Dxil(0, 2), Dxil(0, 3), new byte[100]);
        using var cts = new CancellationTokenSource();
        var found = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => NixxesReader.Carve(Bytes(data), new byte[4096], (_, _) => { found++; cts.Cancel(); },
            new NixxesReader.Meter(long.MaxValue), () => false, cts.Token));
        Assert.Equal(1, found);
    }

    /// <summary>A PSARC header (big-endian): zlib, a TOC of <paramref name="toc"/> bytes, one 30-byte entry, 64 KB blocks.</summary>
    static byte[] PsarcHeader(uint toc)
    {
        var b = new byte[32];
        "PSAR"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), 4);
        "zlib"u8.CopyTo(b.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), toc);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), 30);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(24), 1 << 16);
        return b;
    }

    /// <summary>A DSAR file of a stored PSARC header and nothing else: the TOC it claims is an implicit gap, read as zeros.</summary>
    static byte[] Claiming(long toc) => Raw(toc, (0, 32, 0, PsarcHeader((uint)toc), null));

    /// <summary>A 96-byte file can't make Psarc allocate the 256 MB TOC it claims: over <see cref="Psarc.MaxToc"/> it isn't
    /// read; at the cap it's charged to the meter before it's read, allocated once (no table derived from it), and refused
    /// when the meter can't pay.</summary>
    [Fact]
    public void AWrappedArchiveCantClaimMoreMetadataThanItsBudget()
    {
        var dir = Ff7.TempDir("nixxes-psarc-claims");
        var tiny = Path.Combine(dir, "tiny.psarc");
        File.WriteAllBytes(tiny, Claiming(256L << 20));
        Assert.Equal(96, new FileInfo(tiny).Length);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(Psarc.Open(tiny));
        Assert.Null(Psarc.Open(tiny, new NixxesReader.Meter(64L << 20)));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1 << 20, $"{allocated} bytes allocated");

        var cap = Path.Combine(dir, "cap.psarc");
        File.WriteAllBytes(cap, Claiming(Psarc.MaxToc));
        var meter = new NixxesReader.Meter(long.MaxValue);
        before = GC.GetAllocatedBytesForCurrentThread();
        using (var a = Psarc.Open(cap, meter)) Assert.NotNull(a);
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < Psarc.MaxToc + (1 << 20), $"{allocated} bytes allocated");
        Assert.Equal(32 + Psarc.MaxToc, meter.Read);   // the DSAR record, then the TOC
        before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(Psarc.Open(cap, new NixxesReader.Meter(Psarc.MaxToc - 1)));
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1 << 20, $"{allocated} bytes allocated");
    }

    /// <summary>Archives that each claim most of the TOC cap share Detect's 64 MB budget with the carving: past it, no
    /// more is allocated, however many there are.</summary>
    [Fact]
    public void DetectChargesEveryArchivesMetadataToItsBudget()
    {
        const long budget = 64L << 20;
        var claim = Psarc.MaxToc / 4 * 3;
        var dir = Ff7.TempDir("nixxes-psarc-claims-detect");
        var psarc = Path.Combine(dir, @"cache_pc\psarc");
        Directory.CreateDirectory(psarc);
        var files = (int)(budget / claim) + 4;
        for (var i = 0; i < files; i++) File.WriteAllBytes(Path.Combine(psarc, $"gapack_{i}.psarc"), Claiming(claim));
        var game = new Game("steam:2215430", "Ghost of Tsushima DIRECTOR'S CUT", Store.Steam, dir, Path.Combine(dir, "GhostOfTsushima.exe"));
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(new NixxesReader().Detect(game));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True((long)files * claim > budget + (24 << 20));
        Assert.True(allocated < budget + (24 << 20), $"{allocated} bytes allocated");   // the budget, Detect's 16 MB window, slack
    }

    /// <summary>The block table is read 4,096 records at a time: a header claiming <see cref="Core.Nixxes.Dsar.MaxBlocks"/>
    /// records whose second is bad allocates a chunk, not the table; a one-record table, a record's worth.</summary>
    [Fact]
    public void ADsarBlockTableIsntAllocatedFromItsCount()
    {
        var n = Core.Nixxes.Dsar.MaxBlocks;
        var b = new byte[32 + 32 * n];
        var w = new BinaryWriter(new MemoryStream(b));
        w.Write("DSAR"u8); w.Write((ushort)3); w.Write((ushort)1); w.Write(n); w.Write(b.Length); w.Write((long)n); w.Write(0L);
        w.Write(0L); w.Write(0L); w.Write(1); w.Write(1); w.Write((byte)0);   // one stored byte; the next record starts before it ends
        var path = Path.Combine(Ff7.TempDir("nixxes-dsar-chunks"), "a");
        File.WriteAllBytes(path, b);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(Core.Nixxes.Dsar.Open(path));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 512 << 10, $"{allocated} bytes allocated");   // the table would be 8 MB to read, 6.5 MB to keep

        var one = b[..64];
        BinaryPrimitives.WriteInt32LittleEndian(one.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(one.AsSpan(12), 64);
        BinaryPrimitives.WriteInt32LittleEndian(one.AsSpan(32 + 16), 0);   // a block of no bytes
        File.WriteAllBytes(path, one);
        before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(Core.Nixxes.Dsar.Open(path));
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 16 << 10, $"{allocated} bytes allocated");
    }

    /// <summary>A plausible header whose claimed size runs into bytes that don't read doesn't hide a real container after
    /// it in the same window.</summary>
    [Fact]
    public void AHeaderIntoBadBytesDoesntHideTheNextContainer()
    {
        var c = Dxil(0, 1);
        var data = new byte[5000];
        Concat("DXBC"u8.ToArray(), new byte[16], BitConverter.GetBytes(1), BitConverter.GetBytes(3000), BitConverter.GetBytes(1), BitConverter.GetBytes(36)).CopyTo(data, 100);
        c.CopyTo(data, 200);
        var found = new List<long>();
        Assert.Throws<InvalidDataException>(() => NixxesReader.Carve(Bytes(data, readable: 2000), new byte[1024], (at, _) => found.Add(at),
            new NixxesReader.Meter(long.MaxValue), () => false, CancellationToken.None));
        Assert.Equal([200L], found);
    }

    sealed class Progress(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }
}
