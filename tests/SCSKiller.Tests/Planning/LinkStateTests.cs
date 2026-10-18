using System.Buffers.Binary;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using static SCSKiller.Core.Planning.PsoDb;
using static SCSKiller.Tests.Planning.ExactLayoutsTests;

namespace SCSKiller.Tests.Planning;

/// <summary>Link state from FF7 Rebirth's AMD session 2 (after a per-stage warm): dual-source blending in the
/// PS key, the game's blend / depth state on synthesized PSOs (a ~3 ms relink otherwise), and no VS-alone PSO for a VS that
/// only feeds a GS (the runtime rejects it: the 8 failed warm items).</summary>
public class LinkStateTests
{
    static readonly EngineInfo Ue426 = new("Unreal", "4.26", "GAME_FinalFantasy7Rebirth", "D3D12", false, null);
    static readonly SigElement PosOut = new("SV_Position", 0, 0, 0xF, 1, 3), UvOut = new("TEXCOORD", 0, 1, 0x3, 0, 3);

    /// <summary>A canonical D3D12_BLEND_DESC with render target 0 set (independent blend off).</summary>
    static byte[] Blend(uint enable, uint src, uint dest, uint srcA, uint destA)
    {
        var b = new byte[BlendDescSize];
        uint[] rt0 = [enable, 0, src, dest, 1, srcA, destA, 1, 4, 0xF];
        for (var i = 0; i < rt0.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8 + 4 * i), rt0[i]);
        return b;
    }

    static readonly byte[] DualBlend = Blend(1, 2, 17, 2, 19); // ONE / INV_SRC1_COLOR, ONE / INV_SRC1_ALPHA (FF7's)
    static readonly byte[] AlphaBlend = Blend(1, 2, 6, 1, 6);  // ONE / INV_SRC_ALPHA

    /// <summary>Depth on, write on, GREATER_EQUAL (reversed Z), stencil on with read 0xFF / write 0xF0, as a DESC1.</summary>
    static readonly byte[] ReversedZ = DepthStencil(1, 1, 7, 1);

    static byte[] DepthStencil(uint depth, uint write, uint func, uint stencil)
    {
        var b = new byte[DepthStencilDesc1Size];
        BinaryPrimitives.WriteUInt32LittleEndian(b, depth);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), write);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), func);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), stencil);
        b[16] = 0xFF;
        b[17] = 0xF0;
        uint[] ops = [1, 1, 3, 8, 1, 1, 3, 8];
        for (var i = 0; i < ops.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20 + 4 * i), ops[i]);
        return b;
    }

    sealed class SyncLog(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }

    static readonly Dictionary<int, string> VsPs = new() { [(int)Stage.Vertex] = new('b', 40), [(int)Stage.Pixel] = new('c', 40) };

    [Fact]
    public void RecordedBlendAndDepthRoundTripAndDualSourceIsInTheShape()
    {
        var s = ParseState(new Rec('S', Stream(new('a', 40), VsPs, [], 3, [10], 20, blend: DualBlend, depthStencil: ReversedZ)))!;
        Assert.Equal(DualBlend, s.Blend);
        Assert.Equal(ReversedZ, s.DepthStencil);
        Assert.Equal(20u, s.Dsv);
        Assert.True(s.DualSource);
        Assert.Equal("fp16/ds", ExactLayouts.ExportShape(s));

        var plain = ParseState(new Rec('S', Stream(new('a', 40), VsPs, [], 3, [10], 20, blend: AlphaBlend, depthStencil: ReversedZ)))!;
        Assert.False(plain.DualSource);
        Assert.Equal("fp16", ExactLayouts.ExportShape(plain));
        // SRC1 factors with blending off don't blend: not dual-source; the neutral synthesized stream isn't either
        Assert.False(DualSource(Blend(0, 2, 17, 2, 19)));
        var neutral = ParseState(new Rec('S', Stream(new('a', 40), VsPs, [], 3, [10], 0)))!;
        Assert.False(neutral.DualSource);
        Assert.Equal(BlendDescSize, neutral.Blend!.Length);
        Assert.Throws<ArgumentException>(() => Stream(new('a', 40), VsPs, [], 3, [10], 0, blend: new byte[10]));
    }

    // VS1 recorded with PS1 (dual-source, reversed Z, D32S8); PS2 has PS1's outputs (inferred: same shape, same state);
    // PS3 writes a uint target nothing recorded (guessed: neutral state). VSG passes TEXCOORD to GS1 without SV_Position.
    static readonly ShaderInfo Vs1 = Shader("ls-vs1", Stage.Vertex, [In("POSITION", 0, 0, 7)], [PosOut, UvOut]);
    static readonly SigElement[] DualOut = [Target(0), Target(1)];
    static readonly ShaderInfo Ps1 = Shader("ls-ps1", Stage.Pixel, [PosOut, UvOut], DualOut), Ps2 = Shader("ls-ps2", Stage.Pixel, [PosOut, UvOut], DualOut);
    static readonly ShaderInfo Ps3 = Shader("ls-ps3", Stage.Pixel, [PosOut, UvOut], [Target(0, 1)]);
    static readonly ShaderInfo VsG = Shader("ls-vsg", Stage.Vertex, [In("POSITION", 0, 0, 7)], [UvOut with { Register = 0 }]);
    static readonly ShaderInfo Gs1 = Shader("ls-gs1", Stage.Geometry, [UvOut with { Register = 0 }], [PosOut, UvOut], gsInput: 3);

    static ShaderIndex Index()
    {
        var all = new[] { Vs1, Ps1, Ps2, Ps3, VsG, Gs1 };
        return new("synthetic", ["PCD3D_SM6"], all.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM6", all.Select(s => s.Sha1).ToList())]);
    }

    static string Recording(string dir)
    {
        var b = RootSig.Serialize(RootSig.Build(Rules.Ff7, new[] { Vs1, Ps1 }.ToDictionary(s => s.Stage), true), RootSig.StaticSamplers(Rules.Ff7));
        var rs = Hex(System.Security.Cryptography.SHA1.HashData(b));
        var path = Path.Combine(dir, "recording.db");
        using var f = File.Create(path);
        WriteBlob(f, rs, b);
        var st = new Dictionary<int, string> { [(int)Stage.Vertex] = Vs1.Sha1, [(int)Stage.Pixel] = Ps1.Sha1 };
        Write(f, 'S', Stream(rs, st, [new("POSITION", 0, 6, 0)], 3, [R16G16B16A16Float], 20, blend: DualBlend, depthStencil: ReversedZ));
        return path;
    }

    [Fact]
    public void SynthesizedPsosCarryTheRecordedLinkState()
    {
        var dir = Ff7.TempDir("linkstate");
        var plan = new Planner().Build(Ff7.Game, Ue426, Index(), new Recording(Recording(dir)), Ff7.Amd, Path.Combine(dir, "plan"), null, CancellationToken.None);
        var psos = PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == 'S').Select(r => ParseState(r)!).ToList();
        PsoState With(ShaderInfo ps) => psos.First(s => s.Stages.GetValueOrDefault((int)Stage.Pixel) == ps.Sha1); // its unit once, with VS1 or behind the GS

        var two = With(Ps2); // inferred from PS1's output signature: the dual-source shape, with PS1's blend and depth state
        Assert.Equal("fp16/ds", ExactLayouts.ExportShape(two));
        Assert.Equal(DualBlend, two.Blend);
        Assert.Equal(ReversedZ, two.DepthStencil);
        Assert.Equal(20u, two.Dsv);

        var three = With(Ps3); // guessed: the neutral state (no blend, no depth buffer)
        Assert.False(three.DualSource);
        Assert.Equal(0u, three.Dsv);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(three.Blend.AsSpan(8)));
    }

    /// <summary>A shared recording's root signature that is an empty RTS0 part, or one whose static sampler the runtime refuses,
    /// is left out with the records naming it, and a note; the rest of the game plans.</summary>
    [Fact]
    public void AMalformedRecordedRootSignatureLeavesOnlyItsRecordsOut()
    {
        // an empty RTS0 part; a 1.0 header naming one static sampler that is all zeros (filter 0, address mode 0)
        byte[] Rts0(byte[] part) =>
            [.. "DXBC"u8, .. new byte[16], 1, 0, 0, 0, .. BitConverter.GetBytes(44 + part.Length), 1, 0, 0, 0, 36, 0, 0, 0, .. "RTS0"u8, .. BitConverter.GetBytes(part.Length), .. part];
        byte[] zeroSampler = [1, 0, 0, 0, 0, 0, 0, 0, 24, 0, 0, 0, 1, 0, 0, 0, 24, 0, 0, 0, 0, 0, 0, 0, .. new byte[52]];
        foreach (var (caps, bad) in new[] { (Ff7.Amd, Rts0([])), (Ff7.Nvidia, Rts0([])), (Ff7.Amd, Rts0(zeroSampler)), (Ff7.Nvidia, Rts0(zeroSampler)) })
        {
            var dir = Ff7.TempDir("linkstate-bad-rs");
            var path = Recording(dir);
            var good = File.ReadAllBytes(path);
            var badRs = Hex(System.Security.Cryptography.SHA1.HashData(bad));
            using (var f = File.Create(path))
            {
                WriteBlob(f, badRs, bad);
                var st = new Dictionary<int, string> { [(int)Stage.Vertex] = Vs1.Sha1, [(int)Stage.Pixel] = Ps2.Sha1 };
                Write(f, 'S', Stream(badRs, st, [new("POSITION", 0, 6, 0)], 3, [R16G16B16A16Float], 20));
                f.Write(good);
            }
            var log = new List<string>();
            var plan = new Planner().Build(Ff7.Game, Ue426, Index(), new Recording(path), caps, Path.Combine(dir, "plan"), new SyncLog(log.Add), CancellationToken.None);
            Assert.Contains(log, l => l.Contains("1 malformed root signatures left out with the 1 records naming them"));
            Assert.Contains(PlanFile.Read(plan.FilePath).Records, r => r.Tag == 'S');
        }
    }

    /// <summary>A valid recorded root signature whose static sampler sits at s0 (a resource-free compute shader: the rule
    /// rebuilds it byte for byte), and another compute shader needing a sampler, for which the rule builds a sampler table at
    /// s0 that the runtime won't serialize next to that static sampler: that stage set is left out with a warning, and the
    /// rest plans.</summary>
    [Fact]
    public void ARootSignatureTheRuntimeWontSerializeLeavesOnlyItsStageSetOut()
    {
        var a = new ShaderInfo(new string('a', 40), Stage.Compute, "cs_6_0", 0, new(0, 0, 0, 0), [], [], []);
        var b = new ShaderInfo(new string('b', 40), Stage.Compute, "cs_6_0", 0, new(0, 0, 0, 1), [new("sampler", 0, 0, 1)], [], []);
        var c = new ShaderInfo(new string('c', 40), Stage.Compute, "cs_6_0", 0, new(0, 1, 0, 0), [new("srv", 0, 0, 1)], [], []);
        var sampler = RootSig.Ue426Samplers[..52];
        BinaryPrimitives.WriteUInt32LittleEndian(sampler.AsSpan(44), 0);   // space 0: s0 space 0
        var rs = RootSig.Serialize(RootSig.Build(Rules.Ff7, new Dictionary<Stage, ShaderInfo> { [Stage.Compute] = a }, true), sampler);
        Assert.True(Core.Carved.Dxbc.RootSignatureValid(rs));
        var dir = Ff7.TempDir("linkstate-unserializable");
        var path = Path.Combine(dir, "recording.db");
        using (var f = File.Create(path))
        {
            WriteBlob(f, Hex(System.Security.Cryptography.SHA1.HashData(rs)), rs);
            Write(f, 'C', Compute(Hex(System.Security.Cryptography.SHA1.HashData(rs)), a.Sha1));
        }
        var index = new ShaderIndex("synthetic", ["PCD3D_SM6"], new[] { a, b, c }.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM6", [a.Sha1, b.Sha1, c.Sha1])]);
        foreach (var caps in new[] { Ff7.Nvidia, Ff7.Amd })
        {
            var log = new List<string>();
            var plan = new Planner().Build(Ff7.Game, Ue426, index, new Recording(path), caps, Path.Combine(dir, caps.Profile), new SyncLog(log.Add), CancellationToken.None);
            var planned = PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag is 'C' or 'S' or 'P').Select(r => r.Tag == 'P' ? ParseItem(r.Payload).Stages : Parse(r).Stages).ToList();
            Assert.Contains(log, l => l.StartsWith("warning: 1 stage sets and 0 DXIL libraries left out: the runtime won't serialize"));
            Assert.DoesNotContain(planned, s => s.ContainsValue(b.Sha1));
            Assert.True(planned.Any(s => s.ContainsValue(c.Sha1)), string.Join(" | ", log));
            Assert.Equal((2L, 0L), (plan.Stats.StageSets, plan.Stats.LeftOut));   // b can't exist: not a stage set the game has
        }
    }

    /// <summary>Static samplers follow the player's texture filtering setting. A shared recording with one player at 16x
    /// anisotropic and another at 1x still verifies the rule when each record is checked with its own samplers, so a
    /// shader no recording shows is planned instead of falling back to the learned lookup (FF7 Rebirth's community entry:
    /// 1,267 of 20,983 records at 16x, a third of its stage sets left out).</summary>
    [Fact]
    public void RecordsOfOtherTextureFilteringSettingsStillVerifyTheRule()
    {
        var recorded = Enumerable.Range(0, 4).Select(i => new ShaderInfo($"{i:x}".PadLeft(40, 'a'), Stage.Compute, "cs_6_0", 0, new(0, 0, 0, 0), [], [], [])).ToList();
        var unseen = new ShaderInfo(new string('e', 40), Stage.Compute, "cs_6_0", 0, new(0, 1, 0, 0), [new("srv", 0, 0, 1)], [], []);
        var x16 = RootSig.StaticSamplers(Rules.Ff7).ToArray();
        for (var o = 0; o < x16.Length; o += 52)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(x16.AsSpan(o), 0x55);        // D3D12_FILTER_ANISOTROPIC
            BinaryPrimitives.WriteUInt32LittleEndian(x16.AsSpan(o + 20), 16);     // MaxAnisotropy
        }
        var dir = Ff7.TempDir("linkstate-samplers");
        var path = Path.Combine(dir, "recording.db");
        using (var f = File.Create(path))
            foreach (var samplers in new[] { RootSig.StaticSamplers(Rules.Ff7), x16 })
            {
                var rs = RootSig.Serialize(RootSig.Build(Rules.Ff7, new Dictionary<Stage, ShaderInfo> { [Stage.Compute] = recorded[0] }, true), samplers);
                var h = Hex(System.Security.Cryptography.SHA1.HashData(rs));
                WriteBlob(f, h, rs);
                foreach (var cs in recorded) Write(f, 'C', Compute(h, cs.Sha1));
            }
        var all = recorded.Append(unseen).ToList();
        var index = new ShaderIndex("synthetic", ["PCD3D_SM6"], all.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM6", all.Select(s => s.Sha1).ToList())]);
        foreach (var caps in new[] { Ff7.Nvidia, Ff7.Amd })
        {
            var log = new List<string>();
            var plan = new Planner().Build(Ff7.Game, Ue426, index, new Recording(path), caps, Path.Combine(dir, caps.Profile), new SyncLog(log.Add), CancellationToken.None);
            Assert.Contains(log, l => l.Contains("root sigs rebuilt from shader counts: 8/8 exact -> building"));
            Assert.Equal((true, 0L), (plan.Stats.RootSigRuleVerified, plan.Stats.LeftOut));
            var planned = PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag is 'C' or 'S' or 'P').Select(r => r.Tag == 'P' ? ParseItem(r.Payload).Stages : Parse(r).Stages);
            Assert.Contains(planned, s => s.ContainsValue(unseen.Sha1));
        }
    }

    /// <summary>UE leaves the mesh and amplification deny flags out on a GPU without mesh shaders. A shared recording mixing such
    /// a player with others still verifies the stock rule (Townfall's community entry: 9,426 of 23,937 records, 82,190 stage
    /// sets left out); a root signature differing in any other flag or row still doesn't.</summary>
    [Fact]
    public void RecordsOfAGpuWithoutMeshShadersStillVerifyTheRule()
    {
        var ue56 = new EngineInfo("Unreal", "5.6", null, "D3D12", false, null);
        var rule = RootSig.RuleFor(ue56)!;
        var recorded = Enumerable.Range(0, 4).Select(i => new ShaderInfo($"{i:x}".PadLeft(40, 'a'), Stage.Compute, "cs_6_6", 0, new(0, 0, 0, 0), [], [], [])).ToList();
        var mesh = RootSig.Build(rule, new Dictionary<Stage, ShaderInfo> { [Stage.Compute] = recorded[0] }, true);
        Assert.Equal(0x300u, mesh.Flags & 0x300);
        var noMesh = mesh with { Flags = mesh.Flags & ~0x300u };
        foreach (var (other, verified) in new[] { (noMesh, true), (noMesh with { Flags = noMesh.Flags & ~2u }, false), (noMesh with { Rows = [.. noMesh.Rows, [1, 0, 0, 3, 4]] }, false) })
        {
            var dir = Ff7.TempDir("linkstate-meshless");
            var path = Path.Combine(dir, "recording.db");
            using (var f = File.Create(path))
                foreach (var d in new[] { mesh, other })
                {
                    var rs = RootSig.Serialize(d, RootSig.StaticSamplers(rule));
                    var h = Hex(System.Security.Cryptography.SHA1.HashData(rs));
                    WriteBlob(f, h, rs);
                    foreach (var cs in recorded) Write(f, 'C', Compute(h, cs.Sha1));
                }
            var index = new ShaderIndex("synthetic", ["PCD3D_SM6"], recorded.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM6", recorded.Select(s => s.Sha1).ToList())]);
            var log = new List<string>();
            var plan = new Planner().Build(Ff7.Game, ue56, index, new Recording(path), Ff7.Nvidia, Path.Combine(dir, "plan"), new SyncLog(log.Add), CancellationToken.None);
            Assert.Contains(log, l => l.Contains(verified ? "8/8 exact -> building" : "4/8 exact -> learned lookup"));
            Assert.Equal(verified, plan.Stats.RootSigRuleVerified);
        }
    }

    [Fact]
    public void AVertexShaderWithoutPositionOnlyFeedsItsGeometryShader()
    {
        foreach (var caps in new[] { Ff7.Amd, Ff7.Nvidia })
        {
            var dir = Ff7.TempDir("linkstate-gs");
            var plan = new Planner().Build(Ff7.Game, Ue426, Index(), new Recording(Recording(dir)), caps, Path.Combine(dir, "plan"), null, CancellationToken.None);
            var body = PlanFile.Read(plan.FilePath).Records.ToList();
            var sets = body.Where(r => r.Tag == 'S').Select(r => Parse(r).Stages)
                .Concat(body.Where(r => r.Tag == 'P').Select(r => ParseItem(r.Payload).Stages)).ToList();
            Assert.DoesNotContain(sets, s => s.ContainsValue(VsG.Sha1) && !s.ContainsKey((int)Stage.Geometry)); // alone or before a PS: E_INVALIDARG
            Assert.Contains(sets, s => s.GetValueOrDefault((int)Stage.Vertex) == VsG.Sha1 && s.GetValueOrDefault((int)Stage.Geometry) == Gs1.Sha1);
            Assert.Contains(sets, s => s.Count == 1 && s.GetValueOrDefault((int)Stage.Vertex) == Vs1.Sha1); // a VS writing SV_Position: its depth pass
        }
    }
}
