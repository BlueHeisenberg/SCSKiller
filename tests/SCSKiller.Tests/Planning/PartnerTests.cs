using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using static SCSKiller.Core.Planning.PsoDb;
using static SCSKiller.Tests.Planning.ExactLayoutsTests;

namespace SCSKiller.Tests.Planning;

/// <summary>AMD compiles a VS for how its PS consumes the VS's outputs (selftest probe 6 "deadps"; FF7 session 2's A/B,
/// where every PSO still at 30-50 ms after its units' cover was a VS unit compiled behind another PS): whether the PS's
/// color output reaches a render target, which components it reads, and their interpolation.</summary>
public class PartnerTests
{
    static readonly string Rs = Hash("rs");
    static readonly ShaderInfo Vs = Shader("p-vs", Stage.Vertex, [In("POSITION", 0, 0, 7)], [new("SV_Position", 0, 0, 0xF, 1, 3), new("TEXCOORD", 0, 1, 3, 0, 3)]);
    static readonly ShaderInfo Ps = Shader("p-ps", Stage.Pixel, [new("SV_Position", 0, 0, 0xF, 1, 3), new("TEXCOORD", 0, 1, 3, 0, 3)], [Target(0)]);
    static readonly ShaderInfo Ps2 = Shader("p-ps2", Stage.Pixel, Ps.Inputs, [Target(0)]);
    static readonly List<LayoutElem> Layout = [new("POSITION", 0, 6, 0)];

    static ExactLayouts Facts(bool partnerReads = false) => ExactLayouts.Build([], new Dictionary<string, byte[]>(), UnitPolicy.Amd with { PartnerReads = partnerReads },
        new[] { Vs, Ps, Ps2 }.ToDictionary(s => s.Sha1));
    static Dictionary<int, string> With(ShaderInfo ps) => new() { [(int)Stage.Vertex] = Vs.Sha1, [(int)Stage.Pixel] = ps.Sha1 };
    /// <summary>The partner key of <paramref name="ps"/> reading <paramref name="read"/> of its TEXCOORD0 with that interpolation mode.</summary>
    static string Key(ShaderInfo ps, byte read, int mode) => ExactLayouts.PartnerKey(ps with
    {
        Inputs = ps.Inputs.Select(i => i.SysValue != 0 ? i with { ReadMask = 0xF } : i with { ReadMask = read, Interpolation = mode }).ToList(),
    })!;
    static Unit VsUnit(ExactLayouts x, ShaderInfo ps, string shape) => UnitCover.Units(x, With(ps), Rs, Layout, 3, shape).Single(u => u.Stage == Stage.Vertex);

    [Fact]
    public void AVertexShaderBehindAPixelShaderWithoutRenderTargetIsItsOwnUnit()
    {
        var x = Facts();
        Assert.NotEqual(VsUnit(x, Ps, ""), VsUnit(x, Ps, "fp16"));  // 0 RT: the PS's color output is dead
        Assert.Equal(VsUnit(x, Ps, "fp16"), VsUnit(x, Ps, "fp16/m0")); // a write mask of 0 is CHEAP for the VS (probe)

        // a PS drawn both ways: two VS units, each picked with a shape of its binding
        var cover = new UnitCover(x);
        var picks = cover.Cover(With(Ps), Rs, [Layout], [3], ["fp16", ""]);
        Assert.Equal(2, picks.Count);
        Assert.Equal(2, cover.Covered.Count(u => u.Stage == Stage.Vertex));
        Assert.Equal(["", "fp16"], picks.Select(p => p.Shape).Order());
        Assert.Empty(cover.Cover(With(Ps), Rs, [Layout], [3], ["fp16", ""]));
    }

    [Fact]
    public void AVertexShaderKeysOnHowItsPixelShaderReadsItsOutputsWhereKnown()
    {
        var x = Facts(partnerReads: true); // AMD's policy has it on (the index's SigElements carry read masks + interpolation)
        Assert.Equal(VsUnit(x, Ps, "fp16"), VsUnit(x, Ps2, "fp16")); // nothing known about either PS: one unit
        x.PartnerKeys[Ps.Sha1] = Key(Ps, 3, 2);
        x.PartnerKeys[Ps2.Sha1] = Key(Ps2, 3, 1); // nointerpolation
        Assert.Equal("TEXCOORD0:3/2", x.PartnerKeys[Ps.Sha1]); // system values left out
        Assert.NotEqual(VsUnit(x, Ps, "fp16"), VsUnit(x, Ps2, "fp16"));
        x.PartnerKeys[Ps2.Sha1] = Key(Ps2, 1, 2); // reads .x only
        Assert.NotEqual(VsUnit(x, Ps, "fp16"), VsUnit(x, Ps2, "fp16"));
        x.PartnerKeys[Ps2.Sha1] = x.PartnerKeys[Ps.Sha1];
        Assert.Equal(VsUnit(x, Ps, "fp16"), VsUnit(x, Ps2, "fp16")); // consumes the same: the VS is shared
        Assert.DoesNotContain("|r:", UnitCover.Units(ExactLayouts.Build([], new Dictionary<string, byte[]>(), UnitPolicy.Nvidia, new[] { Vs, Ps }.ToDictionary(s => s.Sha1)), With(Ps), Rs, Layout, 3, "fp16").First().Key);
    }

    /// <summary>A pixel shader without inputs (FF7 ships one): its PSV0 ends after the semantic index table, with no
    /// signature element size to read.</summary>
    [Fact]
    public void APixelShaderWithoutInputsParses()
    {
        (string, byte[])[] parts =
        [
            ("ISG1", [0, 0, 0, 0, 8, 0, 0, 0]),
            ("DXIL", [0x66, 0, 0, 0]),   // pixel shader 6.6
            ("PSV0", [52, 0, 0, 0, .. new byte[52], 0, 0, 0, 0, 8, 0, 0, 0, .. new byte[8], 0, 0, 0, 0]),   // runtime info, no resources, strings, no indexes
        ];
        var c = new List<byte>([.. "DXBC"u8, .. new byte[16], 1, 0, 0, 0, 0, 0, 0, 0, (byte)parts.Length, 0, 0, 0]);
        var at = 32 + 4 * parts.Length;
        foreach (var (_, data) in parts) { c.AddRange(BitConverter.GetBytes(at)); at += 8 + data.Length; }
        foreach (var (name, data) in parts) c.AddRange([.. System.Text.Encoding.ASCII.GetBytes(name), .. BitConverter.GetBytes(data.Length), .. data]);
        var bytes = c.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(24), bytes.Length);
        var ps = ShaderContainer.Parse(bytes, "x", new ResourceCounts(0, 0, 0, 0))!;
        Assert.Equal((Stage.Pixel, "ps_6_6", 0), (ps.Stage, ps.ShaderModel, ps.Inputs.Count));
        Assert.Empty(ShaderContainer.InputInterpolation(bytes));
    }

    /// <summary>NVIDIA's key is the shader + root signature: the index's read masks and interpolation modes (and AMD's partner
    /// keys) must not move NVIDIA's FF7 plan. Pinned from 31d616a, before SigElement carried them: the PSO count and a hash of
    /// every plan record (templates and items, order-independent).</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void Ff7NvidiaPlanDoesNotMove()
    {
        if (!Ff7.HasIndex) return;
        var dir = Ff7.TempDir("partner-nv");
        var plan = new Planner().Build(Ff7.Game, new("Unreal", "4.26", "GAME_FinalFantasy7Rebirth", "D3D12", false, null), Ff7.Index(),
            new Recording(Ff7.RecordingDb), Ff7.Nvidia, dir, null, CancellationToken.None);
        var records = PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag != 'B').Select(r => r.Key).Order(StringComparer.Ordinal);
        var hash = Hex(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.ASCII.GetBytes(string.Join('\n', records))));
        Assert.Equal((NvidiaGenerated, NvidiaHash), (plan.Stats.Generated, hash));
    }

    const long NvidiaGenerated = 121487;
    const string NvidiaHash = "7f870fa5ecf0bedfdc7fbea5c22ea4667c97979f";

    /// <summary>FF7's recorded DXIL pixel shaders (DXBC has no PSV0: another layer's SM5 shaders are left out): PSV0 gives
    /// every named input an interpolation mode, and the partner key covers every non-system input.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void Ff7PixelShadersHaveInterpolationModes()
    {
        if (!File.Exists(Ff7.AmdSessionDb)) return;
        var blobs = Read(Ff7.AmdSessionDb).Where(r => r.Tag == 'B').ToDictionary(r => Hex(r.Payload.AsSpan(0, 20)), r => r.Payload[20..]);
        var x = ExactLayouts.Build(Read(Ff7.AmdSessionDb).Where(r => r.Tag != 'B'), blobs, UnitPolicy.Amd);
        var pss = x.Shaders.Values.Where(s => s.Stage == Stage.Pixel && s.ShaderModel.StartsWith("ps_6") && s.Inputs.Any(i => i.SysValue == 0) && blobs.ContainsKey(s.Sha1)).ToList();
        Assert.NotEmpty(pss);
        foreach (var ps in pss)
        {
            var modes = ShaderContainer.InputInterpolation(blobs[ps.Sha1]);
            Assert.All(ps.Inputs.Where(i => i.SysValue == 0), i => Assert.InRange(modes[(i.Semantic, i.Index)], 1, 8));
            Assert.Equal(ps.Inputs.Count(i => i.SysValue == 0), x.PartnerKeys[ps.Sha1].Split(',').Length);
            // the parser fills the signature itself: read mask known, interpolation = PSV0's, the same partner key
            var parsed = ShaderContainer.Parse(blobs[ps.Sha1], ps.Sha1, new ResourceCounts(0, 0, 0, 0))!;
            Assert.All(parsed.Inputs, i => Assert.NotEqual(SigElement.UnknownReadMask, i.ReadMask));
            Assert.All(parsed.Inputs.Where(i => i.SysValue == 0), i => Assert.Equal(modes[(i.Semantic, i.Index)], i.Interpolation));
            Assert.All(parsed.Outputs, o => Assert.Equal((byte)o.Mask, o.ReadMask));
            Assert.Equal(x.PartnerKeys[ps.Sha1], ExactLayouts.PartnerKey(parsed));
        }
    }
}
