using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.NaughtyDog;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Carved;
using SCSKiller.Tests.Planning;

namespace SCSKiller.Tests.NaughtyDog;

/// <summary>The Naughty Dog reader on a synthetic install laid out as The Last of Us Part I's: PSARC archives of 64 KB zlib or
/// stored blocks, entries named by MD5 in another order than the name list, ndshader records in packages, with shaders
/// compiled here (<see cref="Hlsl"/>).</summary>
public class NaughtyDogReaderTests
{
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    /// <summary>"ndshader", version 3, flags, a 16-byte hash, the sizes, the shader, its root signature.</summary>
    internal static byte[] Record(byte[] shader, byte[]? rs = null) =>
        [.. "ndshader"u8, .. BitConverter.GetBytes(3), .. BitConverter.GetBytes(0), .. new byte[16],
         .. BitConverter.GetBytes(shader.Length), .. BitConverter.GetBytes(rs?.Length ?? 0), .. shader, .. rs ?? []];

    /// <summary>A PSARC 1.4 archive: the name list, then the entries in reverse order; every other block stored.</summary>
    internal static byte[] Archive(params (string Name, byte[] Data)[] files)
    {
        const int bs = 1 << 16;
        var names = Encoding.UTF8.GetBytes(string.Join('\n', files.Select(f => f.Name)));
        var entries = new List<(byte[] Md5, byte[] Data)> { (new byte[16], names) };
        entries.AddRange(Enumerable.Reverse(files).Select(f => (MD5.HashData(Encoding.UTF8.GetBytes(f.Name)), f.Data)));
        var sizes = new List<ushort>();
        var body = new MemoryStream();
        var toc = new List<(byte[] Md5, int Block, long Size, long Offset)>();
        foreach (var (md5, data) in entries)
        {
            toc.Add((md5, sizes.Count, data.Length, body.Position));
            for (var at = 0; at < data.Length || at == 0 && data.Length == 0; at += bs)
            {
                var block = data.AsSpan(at, Math.Min(bs, data.Length - at)).ToArray();
                var z = new MemoryStream();
                using (var w = new ZLibStream(z, CompressionLevel.Optimal)) w.Write(block);
                var packed = z.ToArray();
                var store = sizes.Count % 2 == 1 || packed.Length >= block.Length;
                body.Write(store ? block : packed);
                sizes.Add((ushort)(store ? block.Length == bs ? 0 : block.Length : packed.Length));
                if (data.Length == 0) break;
            }
        }
        var tocLength = 32 + 30 * toc.Count + 2 * sizes.Count;
        var o = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); o.Write(b); }
        void U40(long v) { o.WriteByte((byte)(v >> 32)); U32((uint)v); }
        o.Write("PSAR"u8); o.Write([0, 1, 0, 4]); o.Write("zlib"u8);
        U32((uint)tocLength); U32(30); U32((uint)toc.Count); U32(bs); U32(0);
        foreach (var (md5, block, size, offset) in toc) { o.Write(md5); U32((uint)block); U40(size); U40(tocLength + offset); }
        foreach (var s in sizes) { o.WriteByte((byte)(s >> 8)); o.WriteByte((byte)s); }
        o.Write(body.ToArray());
        return o.ToArray();
    }

    [Fact]
    public void IndexesEachPackageAsAPoolWithItsShadersRootSignatures()
    {
        var (vs, ps, cs, other) = (Hlsl.Vs(1, null), Hlsl.Ps(1, null), Hlsl.Cs(1), Hlsl.Ps(2, null));
        var (rsGraphics, rsCompute) = (Hlsl.RootSignature(Hlsl.Rs1), Hlsl.RootSignature("DescriptorTable(UAV(u0))"));
        var global = Hlsl.Cs(2);
        var dir = Ff7.TempDir("naughtydog-synthetic");
        var main = Path.Combine(dir, @"build\pc\main");
        Directory.CreateDirectory(main);
        // past the first 64 KB block, after a magic whose sizes frame nothing: the records span a block boundary
        var pak = Concat(new byte[70_000], "ndshader"u8.ToArray(), new byte[40], Record(vs), new byte[7], Record(ps, rsGraphics), Record(cs, rsCompute));
        File.WriteAllBytes(Path.Combine(main, "shaders.psarc"), Archive(("shaders/global-cs.o", Concat(new byte[12], Record(global, rsCompute)))));
        File.WriteAllBytes(Path.Combine(main, "common.psarc"), Archive(
            ("common/level-a.pak", pak),
            ("common/notes.txt", Record(other, rsGraphics)),
            ("matdict1/psodict-matdict-a-chunk-0-.pak", Record(other, rsGraphics)),
            ("texturedict3/common-dict.pak", Record(other, rsGraphics))));
        var game = new Game("steam:1888930", "The Last of Us Part I", Store.Steam, dir, Path.Combine(dir, "tlou-i.exe"));

        var reader = new NaughtyDogReader();
        var engine = reader.Detect(game)!;
        Assert.Equal(new EngineInfo(NaughtyDogReader.Family, NaughtyDogReader.Version, null, "D3D12", false, null), engine);
        Assert.Equal(new PlanCheck(Readiness.NeedsRecording, Planner.Record), new Planner().Check(game, engine, null, Ff7.Nvidia));

        var log = new List<string>();
        var index = reader.Index(game, engine, new Progress(log.Add), CancellationToken.None);
        Assert.DoesNotContain(log, l => l.Contains("warning"));
        Assert.Equal(new[] { vs, ps, cs, global }.Select(Sha).Order(), index.Shaders.Keys.Order());
        Assert.Null(index.Shaders[Sha(vs)].RootSignature);
        Assert.Equal(Sha(rsGraphics), index.Shaders[Sha(ps)].RootSignature);
        Assert.Equal(Sha(rsCompute), index.Shaders[Sha(cs)].RootSignature);
        Assert.Equal(new[] { new[] { vs, ps, cs }.Select(Sha).ToList(), [Sha(global)] }.Select(s => string.Join(',', s)).Order(),
            index.Maps.Select(m => string.Join(',', m.Shaders)).Order());
        Assert.All(index.Maps, m => Assert.False(m.IsPipeline));

        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(game, engine, new[] { vs, ps, rsGraphics, global }.Select(Sha).ToHashSet(), (h, b) => got[h] = b, CancellationToken.None);
        Assert.Equal(new[] { vs, ps, rsGraphics, global }.Select(Sha).Order(), got.Keys.Order());
        Assert.All(got, g => Assert.Equal(g.Key, Sha(g.Value)));

        // the PS's root signature covers the stage set its VS feeds
        var planner = new Planner();
        var planDir = Ff7.TempDir("naughtydog-plan");
        var plan = planner.Build(game, engine, index, null, Ff7.Nvidia, planDir, null, CancellationToken.None);
        Assert.True(plan.Stats.Generated >= 3, plan.Stats.ToString());
        // the shipped RTS0-only containers are what the warm creates the root signatures from
        var work = Path.Combine(planDir, "work");
        planner.Materialize(plan, game, engine, new NaughtyDogReader(), null, work, CancellationToken.None);
        Ff7.CheckWarmReady(work);
        Assert.Null(new NaughtyDogReader().Detect(game with { InstallDir = main }));
    }

    /// <summary>UNCHARTED: Legacy of Thieves Collection: two games under Uncharted4_data\build\pc, each with its shaders.psarc
    /// and its material shaders in updated-fx-paks2.psarc. No ndshader records: shaders.psarc entries are bare containers, a
    /// package's follow its tables, each after 64 bytes of u32s (0x00012345 markers, 0x99cb1b69 last); a PS carries its
    /// root signature as an RTS0 part. Listed as its two games (<see cref="Collections"/>), each reads only its own folder;
    /// an install in no collection reads both.</summary>
    [Fact]
    public void IndexesBothUnchartedGamesWithTheRootSignaturesTheirShadersCarry()
    {
        var (vs, ps, shared, tllPs) = (Hlsl.Vs(1, null), Hlsl.Ps(1, Hlsl.Rs1), Hlsl.Ps(2, Hlsl.Rs1), Hlsl.Ps(3, Hlsl.Rs2));
        var (global, globalPs, level, sound) = (Hlsl.Cs(1), Hlsl.Ps(6, Hlsl.Rs2), Hlsl.Ps(4, Hlsl.Rs1), Hlsl.Ps(5, Hlsl.Rs1));
        var tables = Concat("EFFECT\0EFFECT_TABLE\0PAK_LOGIN_TABLE\0RAW_DATA\0VRAM_DESC_TABLE\0"u8.ToArray(), new byte[300]);
        byte[] Inline(byte[] c) => Concat([.. Enumerable.Repeat(BitConverter.GetBytes(0x00012345), 15).SelectMany(b => b)], BitConverter.GetBytes(0x99cb1b69), c);
        var dir = Ff7.TempDir("naughtydog-uncharted");
        var pc = Path.Combine(dir, @"Uncharted4_data\build\pc");
        foreach (var (game, own) in new[] { ("uncharted4", ps), ("thelostlegacy", tllPs) })
        {
            Directory.CreateDirectory(Path.Combine(pc, game));
            File.WriteAllBytes(Path.Combine(pc, game, "shaders.psarc"), Archive(("shaders/global.cxo", global), ("shaders/global.pxo", globalPs)));
            File.WriteAllBytes(Path.Combine(pc, game, "updated-fx-paks2.psarc"), Archive(($"fx/{game}-winfx.pak", Concat(tables, Inline(vs), Inline(own), Inline(shared)))));
            File.WriteAllBytes(Path.Combine(pc, game, "pak54.psarc"), Archive(("levels/a.pak", level)));   // no shaders there: not read
        }
        Directory.CreateDirectory(Path.Combine(pc, "main"));
        File.WriteAllBytes(Path.Combine(pc, "main", "sound1.psarc"), Archive(("sound/a.pak", sound)));   // no shaders.psarc: not a game folder
        Directory.CreateDirectory(Path.Combine(dir, @"build\pc\main"));   // an empty one at the root doesn't hide the others
        File.WriteAllBytes(Path.Combine(dir, "tll-l.exe"), []);
        var uncharted = new Game("test:uncharted", "UNCHARTED: Legacy of Thieves Collection", Store.Steam, dir, Path.Combine(dir, "tll-l.exe"));

        var reader = new NaughtyDogReader();
        var engine = reader.Detect(uncharted)!;
        Assert.Equal(new EngineInfo(NaughtyDogReader.Family, NaughtyDogReader.Version, null, "D3D12", false, null), engine);
        var index = reader.Index(uncharted, engine, null, CancellationToken.None);
        Assert.Equal(new[] { vs, ps, shared, tllPs, global, globalPs }.Select(Sha).Order(), index.Shaders.Keys.Order());
        Assert.Null(index.Shaders[Sha(vs)].RootSignature);
        Assert.Null(index.Shaders[Sha(global)].RootSignature);
        // one root signature per RTS0 part, served as the first shader carrying it
        var (rs1, rs2) = (index.Shaders[Sha(ps)].RootSignature!, index.Shaders[Sha(tllPs)].RootSignature!);
        Assert.Equal(rs1, index.Shaders[Sha(shared)].RootSignature);
        Assert.Contains(rs1, new[] { ps, shared }.Select(Sha));
        Assert.Equal(rs2, index.Shaders[Sha(globalPs)].RootSignature);
        Assert.Contains(rs2, new[] { tllPs, globalPs }.Select(Sha));
        Assert.Equal(2, index.Maps.Count(m => m.Library.EndsWith("-winfx.pak")));
        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(uncharted, engine, new[] { rs1, rs2 }.ToHashSet(), (h, b) => got[h] = b, CancellationToken.None);
        Assert.Equal(new[] { rs1, rs2 }.Order(), got.Keys.Order());

        var planner = new Planner();
        var planDir = Ff7.TempDir("naughtydog-uncharted-plan");
        var plan = planner.Build(uncharted, engine, index, null, Ff7.Nvidia, planDir, null, CancellationToken.None);
        Assert.True(plan.Stats.Generated >= 3, plan.Stats.ToString());
        var work = Path.Combine(planDir, "work");
        planner.Materialize(plan, uncharted, engine, new NaughtyDogReader(), null, work, CancellationToken.None);
        Ff7.CheckWarmReady(work);

        foreach (var (id, own) in new[] { ("steam:1659420", ps), ("steam:1659420:tll", tllPs) })
        {
            var game = uncharted with { Id = id };
            Assert.Equal(engine, reader.Detect(game));
            var one = reader.Index(game, engine, null, CancellationToken.None);
            Assert.Equal(new[] { vs, own, shared, global, globalPs }.Select(Sha).Order(), one.Shaders.Keys.Order());
            Assert.NotEqual(index.ContentHash, one.ContentHash);
            Assert.Single(one.Maps, m => m.Library.EndsWith("-winfx.pak"));
        }
    }

    /// <summary>A stage set takes its PS's root signature (a VS carries none): a VS binding b1, which the PS's doesn't have,
    /// isn't paired with it (the runtime would reject the PSO). The index has the bytes of every root signature it names, one
    /// an entry read before holds bare too.</summary>
    [Fact]
    public void AVsThePsRootSignatureDoesNotCoverIsNotPaired()
    {
        var (vs, ps, rs) = (Hlsl.Vs(1, null), Hlsl.Ps(1, null), Hlsl.RootSignature(Hlsl.Rs1));
        var b1 = Hlsl.Compile("""
            cbuffer D : register(b1) { float4 k; };
            float4 main(float3 p : POSITION, float2 uv : TEXCOORD0, out float2 ouv : TEXCOORD0) : SV_Position { ouv = uv; return float4(p, 1) + k; }
            """, "main", "vs_5_1");
        var dir = Ff7.TempDir("naughtydog-uncovered");
        var main = Path.Combine(dir, @"build\pc\main");
        Directory.CreateDirectory(main);
        File.WriteAllBytes(Path.Combine(main, "shaders.psarc"), Archive(("shaders/level-a.pak", Concat(Record(vs), Record(b1), Record(ps, rs)))));
        File.WriteAllBytes(Path.Combine(main, "common.psarc"), Archive(("common/level-b.pak", rs)));   // read first, bare: not a shader
        var game = new Game("steam:1888930", "The Last of Us Part I", Store.Steam, dir, Path.Combine(dir, "tlou-i.exe"));
        var reader = new NaughtyDogReader();
        var engine = reader.Detect(game)!;
        var index = reader.Index(game, engine, null, CancellationToken.None);
        Assert.Equal(Sha(rs), index.Shaders[Sha(ps)].RootSignature);
        Assert.All(index.Shaders.Values.Select(s => s.RootSignature).OfType<string>(), h => Assert.Contains(h, index.RootSignatures!.Keys));

        var log = new List<string>();
        var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia, Ff7.TempDir("naughtydog-uncovered-plan"), new Progress(log.Add), CancellationToken.None);
        var pairs = PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == 'S').Select(r => PsoDb.Parse(r).Stages.Values.ToHashSet())
            .Concat(PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == 'P').Select(r => PsoDb.ParseItem(r.Payload).Stages.Values.ToHashSet())).ToList();
        Assert.Contains(pairs, s => s.SetEquals([Sha(vs), Sha(ps)]));
        Assert.DoesNotContain(pairs, s => s.Contains(Sha(b1)));
        Assert.Contains(log, l => l.StartsWith("plan:") && l.Contains("own_rs_uncovered 1"));
        Assert.Contains(log, l => l.StartsWith("1 stage sets left out: the root signature the game ships with them doesn't cover their shaders (Vertex cbv space 0"));
    }

    /// <summary>The Last of Us Part I with a CS shipped under two root signatures (one again in another package) and a PS
    /// under two, in packages of common.psarc.</summary>
    static (Game Game, byte[] Cs, byte[] Ps, byte[][] CsRoots, byte[][] PsRoots) SeveralRoots(string name, int csRoots)
    {
        var (cs, ps, vs) = (Hlsl.Cs(1), Hlsl.Ps(1, null), Hlsl.Vs(1, null));
        var roots = new[] { "DescriptorTable(UAV(u0))", "CBV(b0), DescriptorTable(UAV(u0))", "DescriptorTable(UAV(u0)), CBV(b0)" }.Take(csRoots).Select(Hlsl.RootSignature).ToArray();
        var psRoots = new[] { Hlsl.RootSignature(Hlsl.Rs1), Hlsl.RootSignature(Hlsl.Rs2) };
        var dir = Ff7.TempDir(name);
        var main = Path.Combine(dir, @"build\pc\main");
        Directory.CreateDirectory(main);
        File.WriteAllBytes(Path.Combine(main, "shaders.psarc"), Archive(("shaders/global-cs.o", Record(Hlsl.Cs(2), roots[0]))));
        File.WriteAllBytes(Path.Combine(main, "common.psarc"), Archive(
            ("common/level-a.pak", Concat(Record(vs), Record(ps, psRoots[0]), Concat(roots.Select(r => Record(cs, r)).ToArray()))),
            ("common/level-b.pak", Concat(Record(cs, roots[0]), Record(ps, psRoots[1])))));
        return (new Game("steam:1888930", "The Last of Us Part I", Store.Steam, dir, Path.Combine(dir, "tlou-i.exe")), cs, ps, roots, psRoots);
    }

    /// <summary>A compute pipeline is its CS and its root signature: a CS shipped with two is two exact pipelines, each root
    /// signature served by ReadShaders; one seen again collapses. A PS shipped with two keeps the first (the files don't
    /// pair it with a VS), counted in the log.</summary>
    [Fact]
    public void AComputeShaderWithSeveralRootSignaturesIsAPipelinePerRootSignature()
    {
        var (game, cs, ps, csRoots, psRoots) = SeveralRoots("naughtydog-roots", 2);
        var reader = new NaughtyDogReader();
        var engine = reader.Detect(game)!;
        var log = new List<string>();
        var index = reader.Index(game, engine, new Progress(log.Add), CancellationToken.None);
        Assert.DoesNotContain(log, l => l.Contains("warning"));
        Assert.Contains(log, l => l.Contains("; 1 compute shaders ship with several root signatures: a pipeline per root signature, 2 in all")
            && l.Contains("; 1 Pixel shaders ship with several root signatures: the first kept"));
        var pipelines = index.Maps.Where(m => m.IsPipeline).ToList();
        Assert.All(pipelines, m => Assert.Equal([Sha(cs)], m.Shaders));
        Assert.Equal(csRoots.Select(Sha).Order(), pipelines.Select(m => m.RootSignature!).Order());
        Assert.Contains(index.Shaders[Sha(cs)].RootSignature, csRoots.Select(Sha));
        Assert.Contains(index.Shaders[Sha(ps)].RootSignature, psRoots.Select(Sha));
        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(game, engine, csRoots.Select(Sha).ToHashSet(), (h, b) => got[h] = b, CancellationToken.None);
        Assert.Equal(csRoots.Select(Sha).Order(), got.Keys.Order());

        var planner = new Planner();
        var planDir = Ff7.TempDir("naughtydog-roots-plan");
        foreach (var caps in new[] { Ff7.Nvidia, Ff7.Nvidia with { PerStageCache = true }, Ff7.Amd })
        {
            var plan = planner.Build(game, engine, index, null, caps, Path.Combine(planDir, $"{caps.Profile}-{caps.PerStageCache}"), null, CancellationToken.None);
            Assert.Equal(csRoots.Select(r => (Sha(r), Sha(cs))).Order(), RootSigGuardTests.Psos(plan).Where(p => p.Stages == Sha(cs)).Distinct().Order());
            if (caps != Ff7.Nvidia) continue;
            var work = Path.Combine(planDir, "work");
            planner.Materialize(plan, game, engine, new NaughtyDogReader(), null, work, CancellationToken.None);
            Ff7.CheckWarmReady(work);
        }
    }

    /// <summary>A compute shader's root signatures past its cap, or past the cap of extra ones in all, are left out of the
    /// index with a warning; a PS's extra ones don't count against it.</summary>
    [Fact]
    public void RootSignaturesPastTheCapsAreLeftOut()
    {
        var (game, cs, _, csRoots, _) = SeveralRoots("naughtydog-roots-cap", 3);
        foreach (var (reader, kept) in new[] { (new NaughtyDogReader { MaxRootsPerShader = 2 }, 2), (new NaughtyDogReader { MaxExtraRoots = 1 }, 2), (new NaughtyDogReader { MaxExtraRoots = 0 }, 0) })
        {
            var log = new List<string>();
            var index = reader.Index(game, reader.Detect(game)!, new Progress(log.Add), CancellationToken.None);
            Assert.Contains(log, l => l.Contains("; warning: 1 shaders ship with more root signatures than kept"));
            var pipelines = index.Maps.Where(m => m.IsPipeline).ToList();
            Assert.Equal(kept, pipelines.Count);
            Assert.All(pipelines, m => Assert.Contains(m.RootSignature, csRoots.Select(Sha)));
            Assert.Equal(kept, pipelines.Select(m => m.RootSignature).Distinct().Count());
            Assert.Contains(index.Shaders[Sha(cs)].RootSignature, csRoots.Select(Sha));
            Assert.Equal(Math.Max(kept, 1), csRoots.Select(Sha).Count(index.RootSignatures!.ContainsKey));   // one left out isn't kept either
        }
    }

    [Fact]
    public void AMagicWhoseSizesFrameNoContainerIsNoRecord()
    {
        var ps = Hlsl.Ps(1, null);
        var rs = Hlsl.RootSignature(Hlsl.Rs1);
        var good = Record(ps, rs);
        Assert.Equal(new[] { (40, ps.Length, rs.Length) }, NaughtyDogReader.Records(good));
        var wrongRs = good.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(wrongRs.AsSpan(36), rs.Length - 4);
        Assert.Empty(NaughtyDogReader.Records(wrongRs));
        Assert.Equal(new[] { (3 + 40 + 40, ps.Length, 0) }, NaughtyDogReader.Records(Concat([1, 2, 3], "ndshader"u8.ToArray(), new byte[32], Record(ps))));
    }

    [Fact]
    public void ReadsARangeAcrossBlocks()
    {
        var data = new byte[200_000];
        new Random(7).NextBytes(data.AsSpan(0, 100_000));   // incompressible half: stored blocks either way
        var path = Path.Combine(Ff7.TempDir("naughtydog-psarc"), "a.psarc");
        File.WriteAllBytes(path, Archive(("x/a.pak", data), ("x/b.pak", [5, 6])));
        using var a = Psarc.Open(path)!;
        Assert.Equal(new[] { "x/b.pak", "x/a.pak" }, a.Names.Skip(1));
        Assert.Equal(data.AsSpan(65_000, 70_000).ToArray(), a.Read(2, 65_000, 70_000));
        Assert.Equal(data, a.Read(2, 0, data.Length));
        Assert.Throws<InvalidDataException>(() => a.Read(3, 0, 1));   // an index from before the archive changed
        Assert.Null(Psarc.Open(Path.Combine(Path.GetDirectoryName(path)!, "none.psarc")));
    }

    /// <summary>A missing entry or a range past one's end is refused before anything is allocated for it, by either Read,
    /// and a start near long.MaxValue doesn't wrap past the check.</summary>
    [Fact]
    public void RefusesABadRangeBeforeAllocatingIt()
    {
        var path = Path.Combine(Ff7.TempDir("naughtydog-psarc-range"), "a.psarc");
        File.WriteAllBytes(path, Archive(("x/a.pak", new byte[100])));
        using var a = Psarc.Open(path)!;
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => a.Read(a.Count, 0, 1 << 30));
        Assert.Throws<InvalidDataException>(() => a.Read(1, 0, 1 << 30));
        Assert.Throws<InvalidDataException>(() => a.Read(1, 1, -1));
        Assert.Throws<InvalidDataException>(() => a.Read(1, long.MaxValue - 10, 1 << 30));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1 << 20, $"{allocated} bytes allocated");
        Assert.Throws<InvalidDataException>(() => a.Read(1, long.MaxValue - 10, new byte[16]));
        Assert.Throws<InvalidDataException>(() => a.Read(1, 90, new byte[11]));
        Assert.Equal(new byte[10], a.Read(1, 90, 10));
    }

    /// <summary>An archive named "a.pak" whose entry claims <paramref name="size"/> bytes of full blocks of
    /// <paramref name="bs"/> bytes starting at the archive's end.</summary>
    static byte[] Unstored(int bs, long size)
    {
        var (name, width) = ("a.pak"u8.ToArray(), bs <= 1 << 16 ? 2 : 3);
        var tocLength = 32 + 2 * 30 + width * (1 + (int)((size + bs - 1) / bs));
        var b = new byte[tocLength + name.Length];
        "PSAR"u8.CopyTo(b);
        b[5] = 1; b[7] = 4;
        "zlib"u8.CopyTo(b.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), (uint)tocLength);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), 30);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), 2);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(24), (uint)bs);
        void Entry(int i, ReadOnlySpan<byte> md5, uint first, long size, long offset)
        {
            var e = b.AsSpan(32 + 30 * i);
            md5.CopyTo(e);
            BinaryPrimitives.WriteUInt32BigEndian(e[16..], first);
            e[20] = (byte)(size >> 32); BinaryPrimitives.WriteUInt32BigEndian(e[21..], (uint)size);
            e[25] = (byte)(offset >> 32); BinaryPrimitives.WriteUInt32BigEndian(e[26..], (uint)offset);
        }
        Entry(0, new byte[16], 0, name.Length, tocLength);
        Entry(1, MD5.HashData(name), 1, size, b.Length);
        b[32 + 60 + width - 1] = (byte)name.Length;   // the name list stored; the entry's blocks all full (0)
        name.CopyTo(b, tocLength);
        return b;
    }

    /// <summary>An entry whose blocks would start at the archive's end is refused before a buffer is allocated for them, by
    /// either Read: 1 GB of 64 KB blocks (a 32,867-byte file), one byte in a 16 MB block (103 bytes).</summary>
    [Fact]
    public void RefusesAnEntryWhoseBlocksArentStored()
    {
        var dir = Ff7.TempDir("naughtydog-psarc-unstored");
        foreach (var (bs, size, length) in new[] { (1 << 16, 1L << 30, 32_867), (1 << 24, 1L, 103) })
        {
            var b = Unstored(bs, size);
            Assert.Equal(length, b.Length);
            var path = Path.Combine(dir, $"{bs}.psarc");
            File.WriteAllBytes(path, b);
            using var a = Psarc.Open(path)!;
            Assert.Equal("a.pak", a.Names[1]);
            var output = new byte[Math.Min(size, 1 << 20)];
            var before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<InvalidDataException>(() => a.Read(1, 0, (int)size));
            Assert.Throws<InvalidDataException>(() => a.Read(1, 0, output));
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 1 << 20, $"{allocated} bytes allocated");
        }
    }

    /// <summary>A name list of millions of repeated names (8 MB, compressing well) makes one string per entry, not one per
    /// name.</summary>
    [Fact]
    public void ANameListMakesAStringPerEntryNotPerName()
    {
        const int repeats = 4 << 20;
        var path = Path.Combine(Ff7.TempDir("naughtydog-psarc-names"), "a.psarc");
        File.WriteAllBytes(path, Archive(("x", [1]), (string.Concat(Enumerable.Repeat("x\n", repeats)), [2])));
        var before = GC.GetAllocatedBytesForCurrentThread();
        using var a = Psarc.Open(path)!;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("x", a.Names);
        Assert.True(allocated < 32 << 20, $"{allocated} bytes allocated");   // splitting into strings: over 150 MB
    }

    static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    sealed class Progress(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }
}
