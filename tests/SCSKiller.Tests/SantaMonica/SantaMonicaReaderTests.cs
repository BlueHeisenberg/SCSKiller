using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using K4os.Compression.LZ4;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.SantaMonica;
using SCSKiller.Core.Unreal;
using SCSKiller.Tests.Carved;
using SCSKiller.Tests.Planning;

namespace SCSKiller.Tests.SantaMonica;

/// <summary>The Santa Monica reader on synthetic installs laid out as God of War Ragnarök's (SHDRPACK 11 packs in LZ4 frames
/// of 64 KB independent blocks under exec\wad\pc_le) and God of War (2018)'s (SHDRPACK 1), with shaders compiled here
/// (<see cref="Hlsl"/>) and made-up hashes.</summary>
public class SantaMonicaReaderTests
{
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    /// <summary>A shader of the pack: its hash, its type (0 VS, 1 PS, 2 VS for tessellation, 3 HS, 4 DS, 5 CS) and container.</summary>
    internal sealed record S(ulong Hash, int Type, byte[] Container);

    /// <summary>A program: its root signature's hash and its shaders' hashes per slot (VS, PS, tessellation VS, HS, DS, CS).</summary>
    internal sealed record P(ulong Root, params ulong[] Slots);

    /// <summary>A SHDRPACK 11 laid out as the game's: header, shader table, shader data (after <paramref name="pad"/> bytes,
    /// so containers cross block boundaries), root-signature table and data, programs, passes (none).</summary>
    internal static byte[] Pack(S[] shaders, (ulong Hash, byte[] Container)[] roots, P[] programs, int pad = 70_000, int gap = 0, int spread = 0)
    {
        var data = new MemoryStream();
        data.Write(new byte[pad]);
        var offsets = new List<long>();
        foreach (var s in shaders)
        {
            offsets.Add(data.Position);
            data.Write(s.Container);
            data.Write(new byte[(16 - data.Position % 16) % 16]);
            data.Write(new byte[spread]);
        }
        data.Write(new byte[gap]);
        var rootData = new MemoryStream();
        var rootAt = new List<long>();
        foreach (var r in roots) { rootAt.Add(rootData.Position); rootData.Write(r.Container); rootData.Write(new byte[(16 - rootData.Position % 16) % 16]); }

        long shaderTable = ShaderPack11.HeaderSize, dataAt = shaderTable + shaders.Length * ShaderPack11.ShaderRecord;
        long rootTable = dataAt + data.Length, rootDataAt = rootTable + roots.Length * ShaderPack11.RootRecord, programTable = rootDataAt + rootData.Length;
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write("SHDRPACK"u8);
        foreach (var v in new long[] { 11, shaders.Length, roots.Length, programs.Length, 0, shaderTable, rootTable, programTable,
                     programTable + programs.Length * ShaderPack11.ProgramRecord, dataAt, rootDataAt, 0, 0, 0, data.Length })
            w.Write(v);
        for (var i = 0; i < shaders.Length; i++)
        {
            var s = shaders[i];
            w.Write(s.Hash); w.Write(s.Type); w.Write(s.Container.Length); w.Write((uint)offsets[i]);
            w.Write(s.Type == 5 ? 64 : 0);   // what the game's compute records hold there
            w.Write(new byte[12]);
            w.Write(Name($"test_{s.Type}_{i}", 68));
        }
        w.Write(data.ToArray());
        for (var i = 0; i < roots.Length; i++) { w.Write(roots[i].Hash); w.Write((uint)rootAt[i]); w.Write(roots[i].Container.Length); }
        w.Write(rootData.ToArray());
        foreach (var p in programs)
        {
            var r = new byte[ShaderPack11.ProgramRecord];
            BinaryPrimitives.WriteUInt64LittleEndian(r, 0xC0DE0000UL + (ulong)o.Position);
            BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(8), p.Root);
            for (var k = 0; k < 6; k++) BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(ShaderPack11.SlotsAt + 8 * k), k < p.Slots.Length ? p.Slots[k] : 0);
            Name("test_program", 64).CopyTo(r, 0xE4);
            w.Write(r);
        }
        return o.ToArray();
    }

    static byte[] Name(string s, int size)
    {
        var b = new byte[size];
        Encoding.ASCII.GetBytes(s).CopyTo(b, 0);
        return b;
    }

    /// <summary>An LZ4 frame of independent blocks with the content size; block <paramref name="stored"/> is stored
    /// uncompressed; <paramref name="flg"/> overrides the flags (the content size is written when they have it).</summary>
    internal static byte[] Frame(byte[] data, int sizeCode = 4, bool checksums = false, int stored = 1, byte? flg = null)
    {
        var bs = 1 << (2 * sizeCode + 8);
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(0x184D2204u);
        var f = flg ?? (byte)(0x40 | 0x20 | (checksums ? 0x10 : 0) | 0x08);
        w.Write(f);
        w.Write((byte)(sizeCode << 4));
        if ((f & 0x08) != 0) w.Write((long)data.Length);
        w.Write((byte)0);
        for (int at = 0, k = 0; at < data.Length; at += bs, k++)
        {
            var block = data.AsSpan(at, Math.Min(bs, data.Length - at));
            var packed = new byte[LZ4Codec.MaximumOutputSize(block.Length)];
            var n = LZ4Codec.Encode(block, packed);
            if (k == stored || n <= 0 || n >= block.Length) { w.Write((uint)block.Length | 0x80000000u); w.Write(block); }
            else { w.Write(n); w.Write(packed, 0, n); }
            if (checksums) w.Write(0);
        }
        w.Write(0);
        return o.ToArray();
    }

    static string Hs => """
        struct CP { float4 p : POSITION; };
        struct PC { float e[3] : SV_TessFactor; float i : SV_InsideTessFactor; };
        PC pc(InputPatch<CP, 3> ip) { PC o; o.e[0] = o.e[1] = o.e[2] = 1; o.i = 1; return o; }
        [domain("tri")] [partitioning("integer")] [outputtopology("triangle_cw")] [outputcontrolpoints(3)] [patchconstantfunc("pc")]
        CP main(InputPatch<CP, 3> ip, uint i : SV_OutputControlPointID) { return ip[i]; }
        """;

    static string Ds => """
        struct CP { float4 p : POSITION; };
        struct PC { float e[3] : SV_TessFactor; float i : SV_InsideTessFactor; };
        [domain("tri")] float4 main(PC pc, float3 uvw : SV_DomainLocation, const OutputPatch<CP, 3> op) : SV_Position { return op[0].p * uvw.x + op[1].p * uvw.y + op[2].p * uvw.z; }
        """;

    static Game GameAt(string dir) => new("steam:2322010", "God of War Ragnarök", Store.Steam, dir, Path.Combine(dir, "GoWR.exe"));

    [Fact]
    public void IndexesEachProgramAsAPipelineWithItsRootSignature()
    {
        var (vs, ps1, ps2, depthVs, cs) = (Hlsl.Vs(1, null), Hlsl.Ps(1, null), Hlsl.Ps(2, null), Hlsl.Vs(2, null), Hlsl.Cs(1));
        var ls = Hlsl.Compile("float4 main(float4 p : POSITION) : POSITION { return p; }", "main", "vs_5_1");
        var (hs, ds) = (Hlsl.Compile(Hs, "main", "hs_5_1"), Hlsl.Compile(Ds, "main", "ds_5_1"));
        var levelPs = Hlsl.Ps(3, null);
        var (rs1, rs2, rsCompute) = (Hlsl.RootSignature(Hlsl.Rs1), Hlsl.RootSignature(Hlsl.Rs2), Hlsl.RootSignature("DescriptorTable(UAV(u0))"));
        var dir = Ff7.TempDir("santamonica-synthetic");
        var packs = Path.Combine(dir, @"exec\wad\pc_le");
        Directory.CreateDirectory(packs);
        File.WriteAllBytes(Path.Combine(packs, "root.shaderpack"), Frame(Pack(
            [new(0x11, 0, vs), new(0x12, 1, ps1), new(0x13, 1, ps2), new(0x14, 0, depthVs), new(0x15, 5, cs), new(0x16, 2, ls), new(0x17, 3, hs), new(0x18, 4, ds)],
            [(0xA1, rs1), (0xA2, rs2), (0xA3, rsCompute)],
            [new(0xA1, 0x11, 0x12), new(0xA2, 0x11, 0x13), new(0xA1, 0x14), new(0xA3, 0, 0, 0, 0, 0, 0x15), new(0xA1, 0, 0, 0x16, 0x17, 0x18),
             new(0xA1, 0x11, 0x12)])));   // the same program again: one pipeline
        // a level's pack: its own PS with the VS and root signature of root.shaderpack
        File.WriteAllBytes(Path.Combine(packs, "010_level.shaderpack"), Frame(Pack([new(0x21, 1, levelPs)], [], [new(0xA2, 0x11, 0x21)]), checksums: true));
        var game = GameAt(dir);

        var reader = new SantaMonicaReader();
        var engine = reader.Detect(game)!;
        Assert.Equal(new EngineInfo(SantaMonicaReader.Family, SantaMonicaReader.Version, null, "D3D12", false, null), engine);
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.NoRecording), new Planner().Check(game, engine, null, Ff7.Nvidia));
        Assert.Equal(Readiness.NeedsRecording, new Planner().Check(game, engine, null, Ff7.Amd).Readiness);

        var log = new List<string>();
        var index = reader.Index(game, engine, new Progress(log.Add), CancellationToken.None);
        Assert.DoesNotContain(log, l => l.Contains("warning"));
        Assert.Equal(new[] { vs, ps1, ps2, depthVs, cs, ls, hs, ds, levelPs }.Select(Sha).Order(), index.Shaders.Keys.Order());
        // the VS is drawn with two root signatures: its PSs carry them
        Assert.Null(index.Shaders[Sha(vs)].RootSignature);
        Assert.Equal(Sha(rs1), index.Shaders[Sha(ps1)].RootSignature);
        Assert.Equal(Sha(rs2), index.Shaders[Sha(ps2)].RootSignature);
        Assert.Equal(Sha(rs2), index.Shaders[Sha(levelPs)].RootSignature);
        Assert.Equal(Sha(rs1), index.Shaders[Sha(depthVs)].RootSignature);
        Assert.Equal(Sha(rsCompute), index.Shaders[Sha(cs)].RootSignature);
        Assert.All(new[] { ls, hs, ds }, s => Assert.Equal(Sha(rs1), index.Shaders[Sha(s)].RootSignature));
        Assert.All(index.Shaders.Values.Select(s => s.RootSignature).OfType<string>(), h => Assert.Contains(h, index.RootSignatures!.Keys));   // the planner checks coverage with them
        Assert.Equal(new[] { new[] { vs, ps1 }, [vs, ps2], [depthVs], [cs], [ls, hs, ds], [vs, levelPs] }.Select(m => string.Join(',', m.Select(Sha).Order())).Order(),
            index.Maps.Select(m => string.Join(',', m.Shaders.Order())).Order());
        Assert.All(index.Maps, m => Assert.True(m.IsPipeline));
        Assert.Contains(log, l => l.Contains("2 shader packs") && l.Contains("3 root signatures") && l.Contains("7 programs -> 6 distinct pipelines"));

        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(game, engine, new[] { vs, levelPs, rs2, hs }.Select(Sha).ToHashSet(), (h, b) => got[h] = b, CancellationToken.None);
        Assert.Equal(new[] { vs, levelPs, rs2, hs }.Select(Sha).Order(), got.Keys.Order());
        Assert.All(got, g => Assert.Equal(g.Key, Sha(g.Value)));

        var planner = new Planner();
        var planDir = Ff7.TempDir("santamonica-plan");
        var plan = planner.Build(game, engine, index, null, Ff7.Nvidia, planDir, null, CancellationToken.None);
        Assert.Equal(6, plan.Stats.Generated);
        Assert.Equal(0, plan.Stats.Uncovered);
        // the shipped RTS0-only containers are what the warm creates the root signatures from
        var work = Path.Combine(planDir, "work");
        planner.Materialize(plan, game, engine, new SantaMonicaReader(), null, work, CancellationToken.None);
        Ff7.CheckWarmReady(work);
    }

    [Fact]
    public void ProgramsNamingWhatNoPackHasAreLeftOutWithAWarning()
    {
        var (vs, ps) = (Hlsl.Vs(1, null), Hlsl.Ps(1, null));
        var dir = Ff7.TempDir("santamonica-missing");
        var packs = Path.Combine(dir, @"exec\wad\pc_le");
        Directory.CreateDirectory(packs);
        File.WriteAllBytes(Path.Combine(packs, "root.shaderpack"), Frame(Pack([new(1, 0, vs), new(2, 1, ps), new(4, 5, Hlsl.Cs(1))], [(9, Hlsl.RootSignature(Hlsl.Rs1))],
            [new(9, 1, 2), new(8, 1, 2), new(9, 1, 3), new(9, 2, 1), new(9, 1, 2, 0, 0, 0, 4)])));
        var game = GameAt(dir);
        var reader = new SantaMonicaReader();
        var log = new List<string>();
        var index = reader.Index(game, reader.Detect(game)!, new Progress(log.Add), CancellationToken.None);
        Assert.Single(index.Maps);
        var line = Assert.Single(log);
        Assert.Contains("1 programs name a root signature no pack has", line);
        Assert.Contains("1 programs name a shader no pack has", line);
        Assert.Contains("1 programs name a shader of another stage", line);
        Assert.Contains("1 programs aren't a pipeline", line);
        // a program naming a root no pack has still names its shaders: neither gets a root signature
        Assert.Null(index.Shaders[Sha(vs)].RootSignature);
        Assert.Null(index.Shaders[Sha(ps)].RootSignature);
    }

    static string Packs(string name)
    {
        var packs = Path.Combine(Ff7.TempDir(name), @"exec\wad\pc_le");
        Directory.CreateDirectory(packs);
        return packs;
    }

    static (ShaderIndex Index, string Log) IndexOne(string packs, S[] shaders, (ulong, byte[])[] roots, P[] programs, SantaMonicaReader? reader = null)
    {
        File.WriteAllBytes(Path.Combine(packs, "root.shaderpack"), Frame(Pack(shaders, roots, programs)));
        return IndexAll(packs, reader);
    }

    static (ShaderIndex Index, string Log) IndexAll(string packs, SantaMonicaReader? reader = null)
    {
        var game = GameAt(Path.GetFullPath(Path.Combine(packs, @"..\..\..")));
        reader ??= new SantaMonicaReader();
        var log = new List<string>();
        var index = reader.Index(game, reader.Detect(game)!, new Progress(log.Add), CancellationToken.None);
        return (index, Assert.Single(log));
    }

    [Fact]
    public void AShaderGetsARootSignatureOnlyWhenEveryProgramNamingItAgrees()
    {
        var (vs, ps) = (Hlsl.Vs(1, null), Hlsl.Ps(1, null));
        var (rsA, rsB) = (Hlsl.RootSignature(Hlsl.Rs1), Hlsl.RootSignature(Hlsl.Rs2));
        // the second program is left out (its PS isn't in any pack), but its root signature still counts for its VS
        var (index, _) = IndexOne(Packs("santamonica-agree"), [new(1, 0, vs), new(2, 1, ps)], [(9, rsA), (7, rsB)], [new(9, 1, 2), new(7, 1, 3)]);
        Assert.Null(index.Shaders[Sha(vs)].RootSignature);
        Assert.Equal(Sha(rsA), index.Shaders[Sha(ps)].RootSignature);
        Assert.Single(index.Maps);
    }

    /// <summary>Two programs of the same VS and PS with two root signatures: neither shader gets one, each program is a map
    /// with its own, and the plan has both PSOs (NVIDIA whole pipelines and per stage, AMD per stage), each warm-ready.</summary>
    [Fact]
    public void TheSameShadersUnderTwoRootSignaturesAreTwoPipelines()
    {
        var (vs, ps) = (Hlsl.Vs(1, null), Hlsl.Ps(1, null));
        var (rsA, rsB) = (Hlsl.RootSignature(Hlsl.Rs1), Hlsl.RootSignature(Hlsl.Rs2));
        var packs = Packs("santamonica-two-roots");
        var (index, line) = IndexOne(packs, [new(1, 0, vs), new(2, 1, ps)], [(9, rsA), (7, rsB)], [new(9, 1, 2), new(7, 1, 2)]);
        Assert.Null(index.Shaders[Sha(vs)].RootSignature);
        Assert.Null(index.Shaders[Sha(ps)].RootSignature);
        Assert.Equal(new[] { Sha(rsA), Sha(rsB) }.Order(), index.Maps.Select(m => m.RootSignature!).Order());
        Assert.Contains("2 programs -> 2 distinct pipelines, 0 shaders given their root signature, 2 used with several", line);

        var game = GameAt(Path.GetFullPath(Path.Combine(packs, @"..\..\..")));
        var engine = new SantaMonicaReader().Detect(game)!;
        foreach (var caps in new[] { Ff7.Nvidia, Ff7.Nvidia with { PerStageCache = true }, Ff7.Amd })
        {
            var planner = new Planner();
            var planDir = Ff7.TempDir($"santamonica-two-roots-plan-{caps.Profile}-{caps.PerStageCache}");
            var plan = planner.Build(game, engine, index, null, caps, planDir, null, CancellationToken.None);
            Assert.Equal((2L, 0L, 0L), (plan.Stats.Generated, plan.Stats.LeftOut, plan.Stats.Uncovered));
            var roots = PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == 'S').Select(r => PsoDb.Parse(r).Rs)
                .Concat(PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == 'P').Select(r => PsoDb.ParseItem(r.Payload).Rs));
            Assert.Equal(new[] { Sha(rsA), Sha(rsB) }.Order(), roots.Order());
            var work = Path.Combine(planDir, "work");
            planner.Materialize(plan, game, engine, new SantaMonicaReader(), null, work, CancellationToken.None);
            Ff7.CheckWarmReady(work);
        }
    }

    [Fact]
    public void AProgramWhoseRootSignatureDoesntCoverItsShadersIsLeftOut()
    {
        var cs = Hlsl.Cs(1);   // binds u0
        var (index, line) = IndexOne(Packs("santamonica-uncovered"), [new(1, 5, cs)], [(9, Hlsl.RootSignature("CBV(b0)"))], [new(9, 0, 0, 0, 0, 0, 1)]);
        Assert.Empty(index.Maps);
        Assert.Contains("1 programs left out: their root signature doesn't cover their shaders", line);
    }

    /// <summary>A DXBC container of these parts (no hash).</summary>
    static byte[] Container(params (string FourCC, byte[] Data)[] parts)
    {
        var size = 32 + 4 * parts.Length + parts.Sum(x => 8 + x.Data.Length);
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write("DXBC"u8); w.Write(new byte[16]); w.Write(1); w.Write(size); w.Write(parts.Length);
        var at = 32 + 4 * parts.Length;
        foreach (var x in parts) { w.Write(at); at += 8 + x.Data.Length; }
        foreach (var x in parts) { w.Write(Encoding.ASCII.GetBytes(x.FourCC)); w.Write(x.Data.Length); w.Write(x.Data); }
        return o.ToArray();
    }

    static byte[] U32s(params uint[] v) => [.. v.SelectMany(BitConverter.GetBytes)];

    /// <summary>An ISGN part: one element per name offset (relative to the name block after the elements), then the names.</summary>
    static byte[] Isgn(int[] nameAt, byte[] names)
    {
        var block = 8 + 24 * nameAt.Length;
        return [.. U32s((uint)nameAt.Length, 8), .. nameAt.SelectMany(n => U32s((uint)(block + n), 0, 0, 3, 0, 0x0F0F)), .. names];
    }

    static readonly byte[] Shex = U32s(0x00010050, 2);   // vs_5_0, no instructions

    [Fact]
    public void SignaturesOverTheElementCapOrDecodingOverlappingNamesAreRejected()
    {
        // past the element cap, sharing one short name: only the cap rejects it
        var many = Container(("ISGN", Isgn(new int[2000], "A\0"u8.ToArray())), ("SHEX", Shex));
        // under the cap, each element at the next byte of one 1 MiB name: 1,000 suffixes would decode about 1 GB
        var name = Enumerable.Repeat((byte)'A', 1 << 20).Append((byte)0).ToArray();
        var overlapping = Container(("ISGN", Isgn([.. Enumerable.Range(0, 1000)], name)), ("SHEX", Shex));
        var vs = Hlsl.Vs(1, null);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var (index, line) = IndexOne(Packs("santamonica-names"), [new(1, 0, many), new(2, 0, overlapping), new(3, 0, vs)], [], []);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 128 << 20);
        Assert.Equal([Sha(vs)], index.Shaders.Keys);
        Assert.Contains("2 unparseable", line);

        // decoding stops at the budget: one 2 MiB string, not the next suffix (another 2 MiB) before the check
        Assert.Throws<ArgumentException>(() => ShaderContainer.Parse(overlapping, "x", new(0, 0, 0, 0)));   // warmed
        before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ArgumentException>(() => ShaderContainer.Parse(overlapping, "x", new(0, 0, 0, 0)));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 3L << 20);
        Assert.Throws<ArgumentException>(() => ShaderContainer.Parse(many, "x", new(0, 0, 0, 0)));
    }

    [Fact]
    public void InterpolationModesKeyInputsWithoutCopyingTheirName()
    {
        // PSV0: RuntimeInfo1 (32 bytes) with one input element, no resources, a 1 MiB name, 255 rows of indexes 0-254
        var name = Enumerable.Repeat((byte)'a', 1 << 20).Concat(new byte[4]).ToArray();
        var element = new byte[16];
        element[8] = 255;    // rows
        element[13] = 2;     // linear
        var runtime = new byte[32];
        runtime[28] = 1;     // SigInputElements
        byte[] psv = [.. U32s(32), .. runtime, .. U32s(0, (uint)name.Length), .. name, .. U32s(255), .. Enumerable.Range(0, 255).SelectMany(i => U32s((uint)i)),
            .. U32s(16), .. element];
        var c = Container(("PSV0", psv));
        ShaderContainer.InputInterpolation(c);   // warmed
        var before = GC.GetAllocatedBytesForCurrentThread();
        var modes = ShaderContainer.InputInterpolation(c);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 3L << 20);   // one 2 MiB name, not 255 keys of it
        Assert.Equal(255, modes.Count);
        Assert.Equal(2, modes[(new string('A', 1 << 20), 254)]);
    }

    [Fact]
    public void LongNamesAreReadWhole()
    {
        var semantic = new string('S', 300);
        var vs = Container(("ISGN", Isgn([0, 0], Encoding.ASCII.GetBytes(semantic + "\0"))), ("SHEX", Shex));
        var info = ShaderContainer.Parse(vs, "x", new(0, 0, 0, 0))!;
        Assert.Equal([semantic, semantic], info.Inputs.Select(i => i.Semantic));
        Assert.Same(info.Inputs[0].Semantic, info.Inputs[1].Semantic);   // one name, decoded once

        // a DXIL library whose RDAT names a ray generation function of 300 characters
        var function = new string('f', 300);
        var strings = Encoding.ASCII.GetBytes("\0" + function + "\0");
        byte[] functions = [.. U32s(1, 28), .. U32s(0, 1, 0, 0, 7, 16, 8)];
        byte[] Part(uint type, byte[] data) => [.. U32s(type, (uint)data.Length), .. data];
        var (stringPart, functionPart) = (Part(1, strings), Part(4, functions));
        byte[] rdat = [.. U32s(0x10, 2, 16, (uint)(16 + stringPart.Length)), .. stringPart, .. functionPart];
        var lib = Container(("RDAT", rdat), ("DXIL", U32s(0x00060063, 0)));
        Assert.Equal(function, Assert.Single(ShaderContainer.Rdat(lib).Functions).Name);
        Assert.Equal(Stage.Library, ShaderContainer.Parse(lib, "y", new(0, 0, 0, 0))!.Stage);
    }

    [Fact]
    public void AShaderWhoseRegisterCountsOverflowIsUnparseable()
    {
        // DXIL program header of a VS, and PSV0 with two CBV ranges of int.MaxValue and 1 registers
        var psv = U32s(0, 2, 16, 2, 0, 0, 0x7FFFFFFE, 2, 0, 1, 1);
        var overflowing = Container(("PSV0", psv), ("DXIL", U32s(0x00010060, 0)));
        var vs = Hlsl.Vs(1, null);
        var (index, line) = IndexOne(Packs("santamonica-overflow"), [new(1, 0, overflowing), new(2, 0, vs)], [], []);
        Assert.Equal([Sha(vs)], index.Shaders.Keys);
        Assert.Contains("1 unparseable", line);
    }

    [Fact]
    public void AStageReadingItsInputsFromOtherRegistersDoesntLink()
    {
        var ls = Hlsl.Compile("void main(float4 p : POSITION, out float4 a : POSITION, out float4 b : TEXCOORD0) { a = p; b = p * 2; }", "main", "vs_5_1");
        string Tess(string cp) => $$"""
            struct CP { {{cp}} };
            struct PC { float e[3] : SV_TessFactor; float i : SV_InsideTessFactor; };
            PC pc(InputPatch<CP, 3> ip) { PC o; o.e[0] = o.e[1] = o.e[2] = 1; o.i = 1; return o; }
            [domain("tri")] [partitioning("integer")] [outputtopology("triangle_cw")] [outputcontrolpoints(3)] [patchconstantfunc("pc")]
            CP hs(InputPatch<CP, 3> ip, uint i : SV_OutputControlPointID) { return ip[i]; }
            [domain("tri")] float4 ds(PC pc, float3 uvw : SV_DomainLocation, const OutputPatch<CP, 3> op) : SV_Position { return op[0].p * uvw.x + op[1].t * uvw.y + op[2].p * uvw.z; }
            """;
        var (same, swapped) = (Tess("float4 p : POSITION; float4 t : TEXCOORD0;"), Tess("float4 t : TEXCOORD0; float4 p : POSITION;"));
        var (hs, ds) = (Hlsl.Compile(same, "hs", "hs_5_1"), Hlsl.Compile(same, "ds", "ds_5_1"));
        var (index, line) = IndexOne(Packs("santamonica-registers"),
            [new(1, 2, ls), new(2, 3, hs), new(3, 4, ds), new(4, 3, Hlsl.Compile(swapped, "hs", "hs_5_1")), new(5, 4, Hlsl.Compile(swapped, "ds", "ds_5_1"))],
            [(9, Hlsl.RootSignature(Hlsl.Rs1))], [new(9, 0, 0, 1, 2, 3), new(9, 0, 0, 1, 4, 5)]);
        Assert.Equal(new[] { ls, hs, ds }.Select(Sha).Order(), Assert.Single(index.Maps).Shaders.Order());
        Assert.Contains("1 programs left out: their stages don't link", line);
    }

    [Fact]
    public void ADomainShaderReadingItsControlPointsFromOtherRegistersDoesntLink()
    {
        var ls = Hlsl.Compile("void main(float4 p : POSITION, out float4 a : POSITION, out float4 b : TEXCOORD0) { a = p; b = p * 2; }", "main", "vs_5_1");
        // the HS takes and writes POSITION r0, TEXCOORD0 r1; the DS reads them the other way round
        string Tess(string ds) => $$"""
            struct CP { float4 p : POSITION; float4 t : TEXCOORD0; };
            struct CPD { {{ds}} };
            struct PC { float e[3] : SV_TessFactor; float i : SV_InsideTessFactor; };
            PC pc(InputPatch<CP, 3> ip) { PC o; o.e[0] = o.e[1] = o.e[2] = 1; o.i = 1; return o; }
            [domain("tri")] [partitioning("integer")] [outputtopology("triangle_cw")] [outputcontrolpoints(3)] [patchconstantfunc("pc")]
            CP hs(InputPatch<CP, 3> ip, uint i : SV_OutputControlPointID) { return ip[i]; }
            [domain("tri")] float4 ds(PC pc, float3 uvw : SV_DomainLocation, const OutputPatch<CPD, 3> op) : SV_Position { return op[0].p * uvw.x + op[1].t * uvw.y + op[2].p * uvw.z; }
            """;
        var (same, swapped) = (Tess("float4 p : POSITION; float4 t : TEXCOORD0;"), Tess("float4 t : TEXCOORD0; float4 p : POSITION;"));
        var (hs, ds) = (Hlsl.Compile(same, "hs", "hs_5_1"), Hlsl.Compile(same, "ds", "ds_5_1"));
        var (index, line) = IndexOne(Packs("santamonica-ds-registers"),
            [new(1, 2, ls), new(2, 3, hs), new(3, 4, ds), new(4, 4, Hlsl.Compile(swapped, "ds", "ds_5_1"))],
            [(9, Hlsl.RootSignature(Hlsl.Rs1))], [new(9, 0, 0, 1, 2, 3), new(9, 0, 0, 1, 2, 4)]);
        Assert.Equal(new[] { ls, hs, ds }.Select(Sha).Order(), Assert.Single(index.Maps).Shaders.Order());
        Assert.Contains("1 programs left out: their stages don't link", line);
    }

    [Fact]
    public void ADamagedPackKeepsItsProgramsRootEvidenceAndNoneOfItsContainers()
    {
        var (vs, ps1, early, late) = (Hlsl.Vs(1, null), Hlsl.Ps(1, null), Hlsl.Ps(2, null), Hlsl.Ps(3, null));
        var (rsA, rsB) = (Hlsl.RootSignature(Hlsl.Rs1), Hlsl.RootSignature(Hlsl.Rs2));
        var packs = Packs("santamonica-damaged");
        File.WriteAllBytes(Path.Combine(packs, "root.shaderpack"), Frame(Pack([new(1, 0, vs), new(2, 1, ps1)], [(0xA, rsA)], [new(0xA, 1, 2)])));
        // the level pack: its first PS in its first block (readable, read first), its second in its third block (broken),
        // its root signature and programs past them
        var level = Frame(Pack([new(3, 1, early), new(4, 1, late)], [(0xB, rsB)], [new(0xB, 1, 3)], pad: 1000, spread: 140_000), stored: -1);
        BreakBlock(level, 2);
        File.WriteAllBytes(Path.Combine(packs, "010_level.shaderpack"), level);
        var (index, line) = IndexAll(packs);
        Assert.Null(index.Shaders[Sha(vs)].RootSignature);   // named with root B too
        Assert.Equal(Sha(rsA), index.Shaders[Sha(ps1)].RootSignature);
        Assert.Equal(new[] { vs, ps1 }.Select(Sha).Order(), index.Shaders.Keys.Order());   // not the readable one either
        Assert.Contains("1 packs don't decompress", line);
    }

    /// <summary>Overwrites block <paramref name="k"/> of a frame written by <see cref="Frame"/> (content size, no checksums).</summary>
    static void BreakBlock(byte[] frame, int k)
    {
        var pos = 15;
        for (var i = 0; ; i++)
        {
            var size = (int)(BitConverter.ToUInt32(frame, pos) & 0x7FFFFFFF);
            if (i == k) { frame.AsSpan(pos + 4, size).Fill(0xFF); return; }
            pos += 4 + size;
        }
    }

    [Fact]
    public void PacksPastTheRecordCapArentRead()
    {
        var (vs, ps) = (Hlsl.Vs(1, null), Hlsl.Ps(1, null));
        var pack = Pack([new(1, 0, vs), new(2, 1, ps)], [(9, Hlsl.RootSignature(Hlsl.Rs1))], [new(9, 1, 2)]);   // 4 records
        var path = Path.Combine(Packs("santamonica-cap-read"), "root.shaderpack");
        File.WriteAllBytes(path, Frame(pack));
        using (var f = Lz4Frame.Open(path)!)
        {
            long budget = 3;
            Assert.Null(ShaderPack11.Read(f, ref budget));
            budget = 4;
            Assert.NotNull(ShaderPack11.Read(f, ref budget));
        }
        // cumulative over packs: the first fits, the second doesn't
        var packs = Packs("santamonica-cap");
        File.WriteAllBytes(Path.Combine(packs, "a.shaderpack"), Frame(pack));
        File.WriteAllBytes(Path.Combine(packs, "b.shaderpack"), Frame(Pack([new(3, 1, Hlsl.Ps(2, null))], [], [new(9, 1, 3)])));
        var (index, line) = IndexAll(packs, new SantaMonicaReader { MaxRecords = 5 });
        Assert.Contains("1 packs over the cap of 5 table records not read", line);
        Assert.Single(index.Maps);
        (index, line) = IndexAll(packs, new SantaMonicaReader { MaxRecords = 6 });
        Assert.DoesNotContain("warning", line);
        Assert.Equal(2, index.Maps.Count);
    }

    [Fact]
    public void AMiddleBlockThatDecompressesShortIsAReadError()
    {
        var path = Path.Combine(Ff7.TempDir("santamonica-short-decode"), "a.shaderpack");
        var half = new byte[30_000];
        new Random(3).NextBytes(half);
        var packed = new byte[LZ4Codec.MaximumOutputSize(half.Length)];
        var n = LZ4Codec.Encode(half, packed);   // incompressible: big enough for a full block, decompressing to 30,000 bytes
        var o = new MemoryStream();
        o.Write(Frame(new byte[1]).AsSpan(0, 15));
        o.Write(BitConverter.GetBytes(n)); o.Write(packed, 0, n);
        o.Write(BitConverter.GetBytes(0x80000001u)); o.WriteByte(0);
        o.Write(BitConverter.GetBytes(0));
        var bytes = o.ToArray();
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(6), (1 << 16) + 1);
        File.WriteAllBytes(path, bytes);
        using var f = Lz4Frame.Open(path)!;
        Assert.Throws<InvalidDataException>(() => f.Read(0, 10));
    }

    [Fact]
    public void ACompressedMiddleBlockTooShortForAFullBlockIsRejected()
    {
        var path = Path.Combine(Ff7.TempDir("santamonica-short"), "a.shaderpack");
        var o = new MemoryStream();
        o.Write(Frame(new byte[1]).AsSpan(0, 15));   // the header of a frame with the content size
        o.Write(BitConverter.GetBytes(100)); o.Write(new byte[100]);   // compressed, 100 bytes
        o.Write(BitConverter.GetBytes(0x80000001u)); o.WriteByte(0);   // stored, the last
        o.Write(BitConverter.GetBytes(0));
        var bytes = o.ToArray();
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(6), (1 << 16) + 1);
        File.WriteAllBytes(path, bytes);
        Assert.Null(Lz4Frame.Open(path));
    }

    [Fact]
    public void AProgramWhoseStagesDontLinkIsLeftOut()
    {
        var vsUint = Hlsl.Compile("void main(float4 p : POSITION, out float4 o : SV_Position, out uint t : TEXCOORD0) { o = p; t = 3; }", "main", "vs_5_1");
        var psFloat = Hlsl.Compile("float4 main(float4 pos : SV_Position, float t : TEXCOORD0) : SV_Target { return t; }", "main", "ps_5_1");
        var psUint = Hlsl.Compile("float4 main(float4 pos : SV_Position, nointerpolation uint t : TEXCOORD0) : SV_Target { return t; }", "main", "ps_5_1");
        var ls = Hlsl.Compile("float4 main(float4 p : POSITION) : POSITION { return p; }", "main", "vs_5_1");
        var (hs, ds) = (Hlsl.Compile(Hs, "main", "hs_5_1"), Hlsl.Compile(Ds, "main", "ds_5_1"));
        var psTex = Hlsl.Ps(1, null);   // reads TEXCOORD0, which the DS doesn't write
        var (index, line) = IndexOne(Packs("santamonica-link"),
            [new(1, 0, vsUint), new(2, 1, psFloat), new(3, 1, psUint), new(4, 2, ls), new(5, 3, hs), new(6, 4, ds), new(7, 1, psTex)],
            [(9, Hlsl.RootSignature(Hlsl.Rs1))],
            [new(9, 1, 2), new(9, 1, 3), new(9, 0, 7, 4, 5, 6), new(9, 0, 0, 4, 5, 6)]);
        Assert.Equal(new[] { new[] { vsUint, psUint }, [ls, hs, ds] }.Select(m => string.Join(',', m.Select(Sha).Order())).Order(),
            index.Maps.Select(m => string.Join(',', m.Shaders.Order())).Order());
        Assert.Contains("2 programs left out: their stages don't link", line);
    }

    [Fact]
    public void AHeaderClaimingMoreRecordsThanTheFrameHoldsFailsWithoutAllocatingThem()
    {
        var packs = Packs("santamonica-claims");
        // a valid frame of zeros long enough for the claimed table: the first record's size is 0, so reading stops there
        var data = new byte[24 << 20];
        "SHDRPACK"u8.CopyTo(data);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), 11);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(0x10), 200_000);
        foreach (var at in new[] { 0x30, 0x38, 0x40, 0x48, 0x50, 0x58 }) BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(at), 128);
        var path = Path.Combine(packs, "root.shaderpack");
        File.WriteAllBytes(path, Frame(data, stored: -1));
        using (var f = Lz4Frame.Open(path)!)
        {
            long budget = 1 << 22;
            var before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Null(ShaderPack11.Read(f, ref budget));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 8 << 20);
        }
        // one-byte blocks can't decompress to a full block: the frame isn't read
        var head = Frame(data.AsSpan(0, 1 << 16).ToArray(), flg: 0x40 | 0x20);
        var o = new MemoryStream();
        o.Write(head.AsSpan(0, head.Length - 4));   // all but the end mark
        for (var i = 0; i < 1000; i++) { o.Write(BitConverter.GetBytes(0x80000001u)); o.WriteByte(0); }
        o.Write(BitConverter.GetBytes(0u));
        File.WriteAllBytes(path, o.ToArray());
        Assert.Null(Lz4Frame.Open(path));
    }

    [Fact]
    public void DetectsOnlyVersion11PacksInLz4Frames()
    {
        var dir = Ff7.TempDir("santamonica-detect");
        var packs = Path.Combine(dir, @"exec\wad\pc_le");
        Directory.CreateDirectory(packs);
        var reader = new SantaMonicaReader();
        Assert.Null(reader.Detect(GameAt(Path.Combine(dir, "none"))));
        Assert.Null(reader.Detect(GameAt(dir)));
        // version 11 not in a frame, version 1 in one: neither is how the games ship them
        var v11 = new byte[256];
        "SHDRPACK"u8.CopyTo(v11);
        v11[8] = 11;
        File.WriteAllBytes(Path.Combine(packs, "root.shaderpack"), v11);
        Assert.Null(reader.Detect(GameAt(dir)));
        v11[8] = 1;
        File.WriteAllBytes(Path.Combine(packs, "root.shaderpack"), Frame(v11));
        Assert.Null(reader.Detect(GameAt(dir)));
        v11[8] = 11;
        File.WriteAllBytes(Path.Combine(packs, "root.shaderpack"), Frame(v11));
        Assert.Null(reader.Detect(GameAt(dir) with { InstallDir = packs }));
        Assert.NotNull(reader.Detect(GameAt(dir)));
    }

    /// <summary>God of War (2018)'s SHDRPACK 1: header, 112-byte records, each shader one raw LZ4 block.</summary>
    internal static byte[] Pack1(params (int Type, byte[] Container)[] shaders)
    {
        var blocks = shaders.Select(s =>
        {
            var b = new byte[LZ4Codec.MaximumOutputSize(s.Container.Length)];
            return b[..LZ4Codec.Encode(s.Container, b)];
        }).ToList();
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write("SHDRPACK"u8);
        foreach (var v in new long[] { 1, 0, 0, shaders.Length, shaders.Sum(s => s.Container.Length), blocks.Sum(b => b.Length) }) w.Write(v);
        w.Write(new byte[ShaderPack1.HeaderSize - o.Length]);
        var at = 0;
        for (var i = 0; i < shaders.Length; i++)
        {
            w.Write(0x5000UL + (ulong)i); w.Write(shaders[i].Type); w.Write(blocks[i].Length); w.Write(shaders[i].Container.Length); w.Write(at);
            w.Write(new byte[24]);
            w.Write(Name($"test_{i}", 64));
            at += blocks[i].Length;
        }
        foreach (var b in blocks) w.Write(b);
        return o.ToArray();
    }

    [Fact]
    public void IndexesGodOfWar2018PacksAsDirectX11Pools()
    {
        var vs = Hlsl.Compile("float4 main(float3 p : POSITION) : SV_Position { return float4(p, 1); }", "main", "vs_5_0");
        var ps = Hlsl.Compile("float4 main(float4 p : SV_Position) : SV_Target { return p * 2; }", "main", "ps_5_0");
        var cs = Hlsl.Compile("RWBuffer<float> u : register(u0); [numthreads(64, 1, 1)] void main(uint i : SV_DispatchThreadID) { u[i] = i; }", "main", "cs_5_0");
        var levelPs = Hlsl.Compile("float4 main(float4 p : SV_Position) : SV_Target { return p * 3; }", "main", "ps_5_0");
        var dir = Ff7.TempDir("santamonica-gow2018");
        var packs = Path.Combine(dir, @"exec\wad\pc_le");
        Directory.CreateDirectory(packs);
        File.WriteAllBytes(Path.Combine(packs, "root.shaderpack"), Pack1((2, vs), (1, ps), (0, cs)));
        File.WriteAllBytes(Path.Combine(packs, "010_forest_010.shaderpack"), Pack1((1, levelPs), (1, ps)));
        var game = new Game("steam:1593500", "God of War", Store.Steam, dir, Path.Combine(dir, "GoW.exe"));

        var reader = new SantaMonicaReader();
        var engine = reader.Detect(game)!;
        Assert.Equal(new EngineInfo(SantaMonicaReader.Family, SantaMonicaReader.Version1, null, "D3D11", false, null), engine);
        Assert.Equal(new PlanCheck(Readiness.Ready, "compiles every DirectX 11 shader"), new Planner().Check(game, engine, null, Ff7.Nvidia));

        var log = new List<string>();
        var index = reader.Index(game, engine, new Progress(log.Add), CancellationToken.None);
        Assert.Equal(new[] { vs, ps, cs, levelPs }.Select(Sha).Order(), index.Shaders.Keys.Order());
        Assert.All(index.Shaders.Values, s => Assert.True(Planner.IsD3D11(s)));
        Assert.Equal(new[] { new[] { vs, ps, cs }, [levelPs, ps] }.Select(m => string.Join(',', m.Select(Sha).Order())).Order(),
            index.Maps.Select(m => string.Join(',', m.Shaders.Order())).Order());
        Assert.Contains(log, l => l.StartsWith("2 shader packs") && l.Contains("4 DirectX 11 shaders"));

        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(game, engine, new[] { vs, levelPs }.Select(Sha).ToHashSet(), (h, b) => got[h] = b, CancellationToken.None);
        Assert.Equal(new[] { vs, levelPs }.Select(Sha).Order(), got.Keys.Order());
        Assert.All(got, g => Assert.Equal(g.Key, Sha(g.Value)));

        var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia, Ff7.TempDir("santamonica-gow2018-plan"), null, CancellationToken.None);
        Assert.True(plan.Stats.D3D11Shaders >= 3, plan.Stats.ToString());
    }

    [Fact]
    public void ReadsARangeAcrossBlocks()
    {
        var data = new byte[300_000];
        new Random(7).NextBytes(data.AsSpan(0, 100_000));   // incompressible: stored blocks
        for (var i = 100_000; i < data.Length; i++) data[i] = (byte)(i / 7);
        var path = Path.Combine(Ff7.TempDir("santamonica-lz4"), "a.shaderpack");
        foreach (var (checksums, stored) in new[] { (false, 1), (true, 3) })
        {
            File.WriteAllBytes(path, Frame(data, checksums: checksums, stored: stored));
            using var f = Lz4Frame.Open(path)!;
            Assert.Equal(data.Length, f.Length);
            Assert.Equal(data.AsSpan(65_000, 70_000).ToArray(), f.Read(65_000, 70_000));
            Assert.Equal(data.AsSpan(200_000).ToArray(), f.Read(200_000, 100_000));
            Assert.Equal(data, f.Read(0, data.Length));
            Assert.Throws<InvalidDataException>(() => f.Read(299_999, 2));
        }
        // linked blocks or a dictionary: not read; a content size the blocks don't make: not a frame
        File.WriteAllBytes(path, Frame(data, flg: 0x40 | 0x08));
        Assert.Null(Lz4Frame.Open(path));
        File.WriteAllBytes(path, Frame(data, flg: 0x40 | 0x20 | 0x08 | 0x01));
        Assert.Null(Lz4Frame.Open(path));
        var frame = Frame(data);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(6), data.Length + 1);
        File.WriteAllBytes(path, frame);
        Assert.Null(Lz4Frame.Open(path));
        Assert.Null(Lz4Frame.Open(Path.Combine(Path.GetDirectoryName(path)!, "none.shaderpack")));
    }

    sealed class Progress(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }
}
