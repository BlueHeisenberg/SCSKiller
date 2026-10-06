using System.Buffers.Binary;
using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using static SCSKiller.Core.Planning.PsoDb;
using static SCSKiller.Tests.Planning.ExactLayoutsTests;

namespace SCSKiller.Tests.Planning;

/// <summary>Intel's per-stage policy, from selftest probe 6 on an Arc B580 (ARCHITECTURE.md, Intel): the VS ignores layout and
/// topology, the PS keys on exact render-target formats and the blend desc.</summary>
public class IntelPolicyTests
{
    static readonly VendorCaps Intel = new("intel-1", true, false, false, PerStageCache: true, RtCacheGranularity: RtCacheGranularity.Collection);

    /// <summary>A canonical D3D12_BLEND_DESC with render target 0 blending ONE / INV_SRC_ALPHA (enable = 1) or off.</summary>
    static byte[] Blend(uint enable)
    {
        var b = new byte[BlendDescSize];
        uint[] rt0 = [enable, 0, 2, 6, 1, 1, 6, 1, 4, 0xF];
        for (var i = 0; i < rt0.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8 + 4 * i), rt0[i]);
        return b;
    }

    static readonly ShaderInfo Vs = Shader("intel-vs", Stage.Vertex, [In("POSITION", 0, 0, 7), In("TEXCOORD", 0, 1, 3)]);
    static readonly ShaderInfo Ps = Shader("intel-ps", Stage.Pixel, [], [Target(0)]);
    static readonly Dictionary<int, string> VsPs = new() { [(int)Stage.Vertex] = Vs.Sha1, [(int)Stage.Pixel] = Ps.Sha1 };

    static Rec Pso(uint rt, byte[] blend, List<LayoutElem>? layout = null, uint topo = 3) =>
        new('S', Stream(Hash("intel-rs"), VsPs, layout ?? [new("POSITION", 0, 6, 0), new("TEXCOORD", 0, 16, 12)], topo, [rt], 0, blend: blend,
            depthStencil: new byte[DepthStencilDesc1Size]));

    [Fact]
    public void The_intel_profile_picks_the_intel_policy()
    {
        Assert.Same(UnitPolicy.Intel, UnitPolicy.For(Intel));
        Assert.Same(UnitPolicy.Amd, UnitPolicy.For(new VendorCaps("amd-1", true, false, false, PerStageCache: true)));
        Assert.Same(UnitPolicy.Nvidia, UnitPolicy.For(new VendorCaps("nvidia-1", true, true, true, PerStageCache: true)));
        Assert.Null(UnitPolicy.For(Intel with { PerStageCache = false }));
        Assert.True(Planner.D3D11Cache(Intel));
    }

    [Fact]
    public void A_pixel_shader_unit_keys_on_exact_formats_and_blend()
    {
        // RGBA8 (28) and R10G10B10A2 (24) are one AMD shape; on Intel each recompiles the PS, and so does each blend
        var recs = new List<Rec> { Pso(28, Blend(1)), Pso(24, Blend(1)), Pso(28, Blend(0)), Pso(28, Blend(1)) };
        var intel = ExactLayouts.Build(recs, new Dictionary<string, byte[]>(), UnitPolicy.Intel, new[] { Vs, Ps }.ToDictionary(s => s.Sha1));
        var amd = ExactLayouts.Build(recs, new Dictionary<string, byte[]>(), UnitPolicy.Amd, new[] { Vs, Ps }.ToDictionary(s => s.Sha1));

        var shapes = intel.Shapes(Ps);
        Assert.Equal(Provenance.Exact, shapes.Provenance);
        Assert.Equal(3, shapes.Value.Count);
        Assert.Single(amd.Shapes(Ps).Value);
        Assert.Equal(3, UnitCover.RecordedUnits(intel).Distinct().Count(u => u.Stage == Stage.Pixel));
        Assert.Single(UnitCover.RecordedUnits(amd).Distinct(), u => u.Stage == Stage.Pixel);

        foreach (var shape in shapes.Value)   // each shape links back to its own recorded blend and formats
        {
            Assert.EndsWith("/b" + Hex(SHA1.HashData(intel.Link(Ps, shape)!.Blend))[..16], shape);
            Assert.StartsWith(intel.ShapeExample[shape].Formats[0] + "/b", shape);
        }
        Assert.Equal("", ExactLayouts.ExactShape(ParseState(new Rec('S', Stream(Hash("intel-rs"), VsPs, [], 3, [], 0)))!));   // no RT bound
    }

    [Fact]
    public void A_vertex_shader_unit_ignores_layout_and_topology_but_not_what_follows_it()
    {
        List<LayoutElem> half = [new("POSITION", 0, 10, 0), new("TEXCOORD", 0, 34, 8)];
        var recs = new List<Rec> { Pso(28, Blend(1)), Pso(28, Blend(1), half), Pso(28, Blend(1), topo: 2) };
        var shaders = new[] { Vs, Ps }.ToDictionary(s => s.Sha1);
        var intel = ExactLayouts.Build(recs, new Dictionary<string, byte[]>(), UnitPolicy.Intel, shaders);
        var amd = ExactLayouts.Build(recs, new Dictionary<string, byte[]>(), UnitPolicy.Amd, shaders);
        Assert.Single(UnitCover.RecordedUnits(intel).Distinct(), u => u.Stage == Stage.Vertex);
        Assert.Equal(3, UnitCover.RecordedUnits(amd).Distinct().Count(u => u.Stage == Stage.Vertex));

        // a depth pass (VS alone) is its own VS compile on Intel (probe 6 vsonly)
        var depth = new Rec('S', Stream(Hash("intel-rs"), new Dictionary<int, string> { [(int)Stage.Vertex] = Vs.Sha1 },
            [new("POSITION", 0, 6, 0), new("TEXCOORD", 0, 16, 12)], 3, [], D32Float));
        var withDepth = ExactLayouts.Build([.. recs, depth], new Dictionary<string, byte[]>(), UnitPolicy.Intel, shaders);
        Assert.Equal(2, UnitCover.RecordedUnits(withDepth).Distinct().Count(u => u.Stage == Stage.Vertex));
    }
}
