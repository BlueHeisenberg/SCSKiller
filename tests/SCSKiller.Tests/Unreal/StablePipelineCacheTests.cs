using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using SCSKiller.Tests.Planning;
using static SCSKiller.Tests.Planning.ExactLayoutsTests;

namespace SCSKiller.Tests.Unreal;

public class StablePipelineCacheTests
{
    /// <summary>A file laid out as UE writes it: header, PSO bodies (only their type is read: 64 bytes, no declaration), TOC, EOF-MARK.</summary>
    internal static byte[] File(uint version, params (uint Key, uint Type, byte[][] Shaders)[] psos) =>
        File(version, 0, false, psos.Select(p => (p.Key, p.Type, p.Shaders, (byte[]?)null)).ToArray());

    /// <summary>With graphics bodies as UE writes them: the type, <paramref name="hashes"/> FSHAHashes (the PSO's, the rest zero),
    /// the vertex declaration (<paramref name="Elements"/>: 8 bytes each, count first), then 300 bytes of state. <paramref name="fork"/>:
    /// Kuro's TOC (no last used time per entry, an int32 0 before EOF-MARK).</summary>
    internal static byte[] File(uint version, int hashes, bool fork, params (uint Key, uint Type, byte[][] Shaders, byte[]? Elements)[] psos)
    {
        var body = new MemoryStream();
        var w = new BinaryWriter(body);
        w.Write(0x5049504543414348UL); w.Write(version); w.Write(0u); w.Write((byte)49); w.Write(new byte[16]);
        var v17 = version == 17; // UE 4.25: no last GC time, TOCSTART, no shared guid, no last used time
        w.Write(0UL); // table offset (patched below)
        if (!v17) w.Write(0L); // last GC time
        var offsets = psos.Select(p =>
        {
            var o = body.Position;
            w.Write(p.Type);
            if (p.Elements == null) w.Write(new byte[60]);
            else
            {
                foreach (var s in p.Shaders) w.Write(s);
                w.Write(new byte[20 * (hashes - p.Shaders.Length)]);
                w.Write(p.Elements.Length / 8); w.Write(p.Elements); w.Write(new byte[300]);
            }
            return (Offset: o, Size: body.Position - o);
        }).ToList();
        var toc = body.Position;
        w.Write(v17 ? 0x544F435354415254UL : 0x544F435354415232UL);
        if (!v17) { w.Write((byte)1); w.Write(new byte[16]); }
        w.Write(2u); w.Write(psos.Length);
        for (var i = 0; i < psos.Length; i++)
        {
            w.Write(psos[i].Key); w.Write((ulong)offsets[i].Offset); w.Write((ulong)offsets[i].Size); w.Write(new byte[16]); w.Write(new byte[36]);
            w.Write(psos[i].Shaders.Length);
            foreach (var s in psos[i].Shaders) w.Write(s);
            w.Write(0UL); w.Write((ushort)0);
            if (!v17 && !fork) w.Write(0L);
        }
        if (fork) w.Write(0);
        w.Write(0x454F462D4D41524BUL);
        var b = body.ToArray();
        BitConverter.GetBytes((ulong)toc).CopyTo(b, 33);
        return b;
    }

    /// <summary>An FVertexElement: stream, offset, EVertexElementType, attribute index, stride, per instance.</summary>
    static byte[] E(byte stream, byte offset, byte type, byte attribute, ushort stride, bool instance = false) =>
        [stream, offset, type, attribute, (byte)stride, (byte)(stride >> 8), (byte)(instance ? 1 : 0), 0];

    /// <summary>Stock UE's five hashes (4.26: VS, PS, GS, HS, DS) and Kuro's seven (plus UE 5's MS, AS) are told apart by
    /// the one fixed body size each gives; the declaration translates as UE's D3D12 RHI does (ATTRIBUTE semantics, DXGI
    /// formats, the stream as the slot, per-instance at step rate 1). Compute and ray tracing PSOs have none; a graphics
    /// PSO created with no vertex declaration an empty one.</summary>
    [Theory]
    [InlineData(22, 5, false)] [InlineData(22, 7, false)] [InlineData(22, 7, true)] [InlineData(17, 5, false)] [InlineData(28, 5, false)]
    public void ReadsEachGraphicsPsosVertexDeclaration(uint version, int hashes, bool fork)
    {
        byte[] pos = E(0, 0, 3, 0, 12), uv = E(1, 8, 12, 1, 16), inst = E(2, 0, 4, 8, 64, true);
        var f = File(version, hashes, fork, (1, 1, [H(1), H(2)], [.. pos, .. uv, .. inst]), (2, 1, [H(3), H(2)], []), (3, 0, [H(4)], null), (4, 1, [H(5)], [.. pos]));
        var psos = StablePipelineCache.Read(f)!;
        Assert.Equal([1u, 2u, 3u, 4u], psos.Select(p => p.Key));
        Assert.Equal([Hex(1), Hex(2)], psos[0].Shaders);
        Assert.Equal([new StablePipelineCache.Element(0, 0, 3, 0, 12, false), new(1, 8, 12, 1, 16, false), new(2, 0, 4, 8, 64, true)], psos[0].Layout);
        Assert.Empty(psos[1].Layout!);
        Assert.Null(psos[2].Layout);
        Assert.Equal([new StablePipelineCache.Element(0, 0, 3, 0, 12, false)], psos[3].Layout);
        // as UE's D3D12 RHI translates it: ATTRIBUTE<attribute>, DXGI format, the stream as the slot, per-instance at step rate 1
        Assert.Equal([new PsoDb.LayoutElem("ATTRIBUTE", 0, 6, 0, 0), new("ATTRIBUTE", 1, 34, 8, 1), new("ATTRIBUTE", 8, 2, 0, 2, 1, 1)], StablePipelineCache.InputLayout(psos[0]));
        Assert.Empty(StablePipelineCache.InputLayout(psos[1])!);
        Assert.Null(StablePipelineCache.InputLayout(psos[2]));
        Assert.Equal((6u, 34u, 2u, 87u, 24u, 42u, 0u, 0u), (StablePipelineCache.DxgiFormat(3), StablePipelineCache.DxgiFormat(12), StablePipelineCache.DxgiFormat(4),
            StablePipelineCache.DxgiFormat(8), StablePipelineCache.DxgiFormat(19), StablePipelineCache.DxgiFormat(20), StablePipelineCache.DxgiFormat(0), StablePipelineCache.DxgiFormat(21)));
    }

    /// <summary>A fork TOC whose entries carry a guid (not Wuthering Waves', whose are zero): the stock reading takes guid
    /// bytes for a body size and throws, which must not end the read before the fork layout gets its try.</summary>
    [Fact]
    public void ForkTocWithEntryGuidsIsRead()
    {
        var f = File(22, 7, true, (1, 1, [H(1), H(2)], [.. E(0, 0, 3, 0, 12)]), (2, 1, [H(3)], []));
        var toc = (int)BitConverter.ToUInt64(f, 33) + 8 + 17 + 4 + 4;
        f.AsSpan(toc + 4 + 16, 16).Fill(0xAB); // the first entry's guid
        var psos = StablePipelineCache.Read(f)!;
        Assert.Equal([1u, 2u], psos.Select(p => p.Key));
        Assert.Single(psos[0].Layout!);
    }

    /// <summary>No declarations when the bodies can't tell the hash count apart: another shape (six hashes: neither count
    /// leaves one fixed size), or every body with the same element count (either count fits; an empty layout taken as exact
    /// would stop a VS with vertex input from getting a guessed one). A type UE's RHI has no format for leaves that PSO's
    /// layout out.</summary>
    [Fact]
    public void NoDeclarationWhereTheBodiesCantBeTold()
    {
        var six = StablePipelineCache.Read(File(22, 6, false, (1, 1, [H(1), H(2)], [.. E(0, 0, 3, 0, 12)]), (2, 1, [H(3)], []), (3, 0, [H(4)], null)))!;
        Assert.Equal([Hex(1), Hex(2)], six[0].Shaders);
        Assert.All(six, p => Assert.Null(p.Layout));
        var alike = StablePipelineCache.Read(File(22, 7, false, (1, 1, [H(1), H(2)], [.. E(0, 0, 3, 0, 12)]), (2, 1, [H(3)], [.. E(0, 0, 2, 0, 8)])))!;
        Assert.All(alike, p => Assert.Null(p.Layout));
        var unknown = StablePipelineCache.Read(File(22, 5, false, (1, 1, [H(1)], [.. E(0, 0, 3, 0, 12), .. E(0, 12, 21, 1, 16)]), (2, 1, [H(2)], [])))!;
        Assert.Equal(2, unknown[0].Layout!.Count);
        Assert.Null(StablePipelineCache.InputLayout(unknown[0]));
    }

    static byte[] H(byte b) => Enumerable.Repeat(b, 20).ToArray();
    static string Hex(byte b) => Convert.ToHexStringLower(H(b));

    [Fact]
    public void ReadsEachPsosTypeAndShaderHashes()
    {
        var psos = StablePipelineCache.Read(File(28, (7, 1, [H(1), H(2)]), (8, 0, [H(3)]), (9, 2, [H(4)])))!;
        Assert.Equal([7u, 8u, 9u], psos.Select(p => p.Key));
        Assert.Equal([StablePipelineCache.PsoType.Graphics, StablePipelineCache.PsoType.Compute, StablePipelineCache.PsoType.RayTracing], psos.Select(p => p.Type));
        Assert.Equal([Hex(1), Hex(2)], psos[0].Shaders);
        Assert.Equal([Hex(4)], psos[2].Shaders);
    }

    /// <summary>UE 4.25 writes version 17 (Returnal).</summary>
    [Fact]
    public void ReadsUe425sVersion17()
    {
        var psos = StablePipelineCache.Read(File(17, (7, 1, [H(1), H(2)]), (8, 0, [H(3)])))!;
        Assert.Equal([7u, 8u], psos.Select(p => p.Key));
        Assert.Equal([StablePipelineCache.PsoType.Graphics, StablePipelineCache.PsoType.Compute], psos.Select(p => p.Type));
        Assert.Equal([Hex(1), Hex(2)], psos[0].Shaders);
    }

    [Fact]
    public void RefusesOtherVersionsAndBrokenFiles()
    {
        Assert.NotNull(StablePipelineCache.Read(File(22, (1, 1, [H(1)]))));
        Assert.Null(StablePipelineCache.Read(File(21, (1, 1, [H(1)]))));
        Assert.Null(StablePipelineCache.Read(File(16, (1, 1, [H(1)]))));
        Assert.Null(StablePipelineCache.Read(File(29, (1, 1, [H(1)]))));
        var f = File(28, (1, 1, [H(1)]));
        Assert.Null(StablePipelineCache.Read(f.AsSpan(0, f.Length - 1)));
        Assert.Null(StablePipelineCache.Read(f.Concat(new byte[8]).ToArray())); // no EOF-MARK at the end
    }

    /// <summary>A shipped pipeline's vertex declaration is the layout its VS is created with: exact for that VS, inferred for
    /// another VS with the same input signature, with no recording at all (AMD keys a VS on its layout).</summary>
    [Fact]
    public void AShippedPipelinesDeclarationIsItsVertexShadersLayout()
    {
        var pos = In("SV_Position", 0, 0, 0xF, 3, 1);
        SigElement[] ins = [In("ATTRIBUTE", 0, 0), In("ATTRIBUTE", 1, 1)];
        var vs = Shader("mesh-vs", Stage.Vertex, ins, [pos]) with { Counts = new(1, 1, 0, 0) };
        var twin = Shader("mesh-vs-2", Stage.Vertex, ins, [pos]) with { Counts = new(1, 1, 0, 0) };
        var other = Shader("other-vs", Stage.Vertex, [In("ATTRIBUTE", 5, 0)], [pos]) with { Counts = new(1, 1, 0, 0) };
        var ps = Shader("ps", Stage.Pixel, [pos], [new SigElement("SV_Target", 0, 0, 0xF, 0, 3)]);
        List<PsoDb.LayoutElem> layout = [new("ATTRIBUTE", 0, 6, 0, 0), new("ATTRIBUTE", 1, 34, 8, 1)];
        var maps = new ShaderMap[]
        {
            new("g", "Global", "PCD3D_SM6", [vs.Sha1, twin.Sha1, other.Sha1, ps.Sha1]),
            new("SHf_PCD3D_SM6:00000001", "PipelineCache", "PCD3D_SM6", [vs.Sha1, ps.Sha1], IsPipeline: true, Layout: layout),
        };
        var index = new ShaderIndex("stable", ["PCD3D_SM6"], new[] { vs, twin, other, ps }.ToDictionary(s => s.Sha1), maps);
        var plan = new Planner().Build(Ff7.Game, new EngineInfo("Unreal", "4.27", null, "D3D12", false, null), index, null, Ff7.Amd,
            Path.Combine(Ff7.TempDir("stable-cache-layout"), "amd"), null, CancellationToken.None);
        Assert.Equal(2.0 / 3, plan.Stats.LayoutCoverage, 3); // vs exact, twin inferred, other guessed
        var facts = ExactLayouts.Build([], new Dictionary<string, byte[]>(), UnitPolicy.Amd, index.Shaders);
        facts.AddLayout(vs.Sha1, layout);
        Assert.Equal((Provenance.Exact, Provenance.Inferred, Provenance.Guessed), (facts.Layouts(vs).Provenance, facts.Layouts(twin).Provenance, facts.Layouts(other).Provenance));
        Assert.Equal(layout, Assert.Single(facts.Layouts(twin).Value));
    }

    /// <summary>UE's draw-rectangle VS writes interpolants a post-process PS doesn't read, so no signature pairs them: only
    /// the shipped pipeline (an IsPipeline map) puts that PS in the plan with the VS's resources in its root signature.</summary>
    [Fact]
    public void AShippedPipelinePairsWhatSignaturesDont()
    {
        var pos = In("SV_Position", 0, 0, 0xF, 3, 1);
        var vs = Shader("rect-vs", Stage.Vertex, [In("SV_VertexID", 0, 0, 1, 1, 6)], [pos, In("TEXCOORD", 0, 1), In("TEXCOORD", 1, 2)]) with { Counts = new(1, 1, 0, 0) };
        var ps = Shader("depth-ps", Stage.Pixel, [pos], [new SigElement("SV_Depth", 0, -1, 1, 0, 3)]);
        var global = new ShaderMap("g", "Global", "PCD3D_SM6", [vs.Sha1, ps.Sha1]);
        var shipped = new ShaderMap("SHf_PCD3D_SM6:00000001", "PipelineCache", "PCD3D_SM6", [vs.Sha1, ps.Sha1], IsPipeline: true);
        var dir = Ff7.TempDir("stable-cache-pair");
        bool Pairs(params ShaderMap[] maps)
        {
            var index = new ShaderIndex("stable", ["PCD3D_SM6"], new[] { vs, ps }.ToDictionary(s => s.Sha1), maps);
            var plan = new Planner().Build(Ff7.Game, new EngineInfo("Unreal", "5.4", null, "D3D12", false, null), index, null, Ff7.Nvidia,
                Path.Combine(dir, maps.Length.ToString()), null, CancellationToken.None);
            return PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag is 'S' or 'P').Select(r => r.Tag == 'P' ? PsoDb.ParseItem(r.Payload).Stages : PsoDb.Parse(r).Stages)
                .Any(s => s.GetValueOrDefault((int)Stage.Vertex) == vs.Sha1 && s.GetValueOrDefault((int)Stage.Pixel) == ps.Sha1);
        }
        Assert.False(Pairs(global));
        Assert.True(Pairs(global, shipped));
    }
}
