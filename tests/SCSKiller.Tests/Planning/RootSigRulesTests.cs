using System.Runtime.InteropServices;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using static SCSKiller.Core.Planning.RootSig;
using static SCSKiller.Core.Unreal.ShaderContainer;

namespace SCSKiller.Tests.Planning;

/// <summary>Each stock Unreal rule on fixtures carrying real shaders' counts (dumps in out\&lt;game&gt;\bytecode.jsonl), against
/// root signatures worked out by hand from Epic's D3D12RHI source at each release tag. Every blob must also be accepted by the
/// D3D12 runtime (serialize + CreateRootSignature on the real device; no GPU compile).</summary>
public class RootSigRulesTests
{
    static ShaderInfo S(Stage stage, int cb, int srv, int uav, int sampler, int flags = 0) =>
        new("", stage, "", 0, new ResourceCounts(cb, srv, uav, sampler, flags), [], [], []);

    // counts of real shaders: Life is Strange: Reunion (5.5), Palworld (5.1), Darwin's Paradox (5.3)
    static readonly ShaderInfo Vs = S(Stage.Vertex, 1, 0, 0, 0);                          // LiS b9c484ee13cf vs_5_0
    static readonly ShaderInfo VsUav = S(Stage.Vertex, 2, 4, 1, 0);                       // LiS beca429740cc vs_5_0
    static readonly ShaderInfo Ps = S(Stage.Pixel, 2, 4, 4, 1);                           // Palworld d4be3efca187 ps_5_0
    static readonly ShaderInfo Gs = S(Stage.Geometry, 3, 1, 0, 0);                        // Darwin 0f7fdc2aa0ef gs_5_0
    static readonly ShaderInfo Cs = S(Stage.Compute, 1, 10, 2, 2);                        // Palworld e3d4837fb416 cs_6_0
    static readonly ShaderInfo CsRootConstants = S(Stage.Compute, 4, 16, 6, 3, UeFlags.RootConstants);  // LiS 3ea1d61a8ebe cs_6_6, usage 56
    static readonly ShaderInfo CsDiagnostic = S(Stage.Compute, 1, 0, 1, 0, UeFlags.DiagnosticBuffer);   // LiS 5dc55188e57d cs_6_6, usage 65
    static readonly ShaderInfo Ms = S(Stage.Mesh, 4, 10, 0, 1, UeFlags.RootConstants);    // LiS f64395b3d3f2 ms_6_6, usage 40

    static Dictionary<Stage, ShaderInfo> P(params ShaderInfo[] s) => s.ToDictionary(x => x.Stage);

    // row helpers: table (visibility, range type, count, flags), root CBV (visibility, register)
    static uint[] T(uint vis, uint type, uint n) => [0, vis, type, n, 0, 0, type switch { 0 => 5u, 3 => 1u, _ => 3u }];
    static uint[] Cbv(uint vis, uint reg) => [2, vis, reg, 0, 8];
    const uint Px = 5, Vx = 1, Gx = 4, Mx = 7, All = 0, Srv = 0, Uav = 1, Smp = 3;
    static readonly uint[] RootConstants = [1, 0, 0, 3, 4], Diagnostic = [4, 0, 0, 999, 2], Ags = [4, 0, 0, 0x7FFF0ADE, 2];

    static void Check(Rule r, Dictionary<Stage, ShaderInfo> stages, bool meshTier, uint flags, params uint[][] rows)
    {
        var d = Build(r, stages, meshTier);
        Assert.Equal(new Desc(flags, [.. rows]).Key, d.Key);
        var blob = Serialize(d, StaticSamplers(r));
        Assert.Equal(StaticSamplers(r), Samplers(blob));
        if (D3D12Runtime.Available) Assert.Equal(0, D3D12Runtime.CreateRootSignature(blob));
    }

    [Theory]
    [InlineData("4.22", null, Rule.Ue422)] [InlineData("4.23", null, Rule.Ue422)] [InlineData("4.24", null, Rule.Ue422)]
    [InlineData("4.25", null, Rule.Ue425)] [InlineData("4.26", null, Rule.Ue426)] [InlineData("4.27", null, Rule.Ue426)]
    [InlineData("4.26", "GAME_FinalFantasy7Rebirth", Rule.Ff7)] [InlineData("4.26", "GAME_StellarBlade", Rule.Ue426)]
    [InlineData("4.26", "GAME_WutheringWaves", Rule.Kuro)] [InlineData("4.27", "GAME_WutheringWaves", Rule.Ue426)]
    [InlineData("5.0", null, Rule.Ue50)] [InlineData("5.1", "GAME_Palworld", Rule.Ue51)] [InlineData("5.2", null, Rule.Ue51)]
    [InlineData("5.3", null, Rule.Ue51)] [InlineData("5.4", null, Rule.Ue54)] [InlineData("5.5", null, Rule.Ue55)]
    [InlineData("5.6", null, Rule.Ue55)] [InlineData("5.7", null, Rule.Ue55)] [InlineData("5.8", null, Rule.Ue58)] [InlineData("5.12", null, Rule.Ue58)] [InlineData("6.0", null, Rule.Ue58)]
    [InlineData("4.20", null, Rule.Ue420)] [InlineData("4.21", null, Rule.Ue421)] [InlineData("4.19", null, null)] [InlineData("6.1", null, null)] [InlineData("GAME_Something", null, null)]
    public void RuleForMapsVersions(string version, string? fork, Rule? expected)
    {
        Assert.Equal(expected, RuleFor(new EngineInfo("Unreal", version, fork, "D3D12", false, null)));
        Assert.Null(RuleFor(new EngineInfo("Carved", version, null, "D3D12", false, null)));
    }

    /// <summary>4.22-5.0: every present stage gets full-size SRV and sampler tables (48 SRVs before 4.25), pixel/compute a
    /// UAV table too, whatever the shader binds; absent stages are denied, hull/domain included (UE 4).</summary>
    [Fact]
    public void Ue4AndUe50TablesAreAlwaysFull()
    {
        uint[][] vsps(uint srv) => [T(Px, Srv, srv), T(Px, Smp, 16), T(Px, Uav, 16), T(Vx, Srv, srv), T(Vx, Smp, 16), Cbv(Px, 0), Cbv(Px, 1), Cbv(Vx, 0)];
        Check(Rule.Ue422, P(Vs, Ps), false, 0x1D, vsps(48)); // IA | deny HS, DS, GS
        Check(Rule.Ue425, P(Vs, Ps), false, 0x1D, vsps(64));
        Check(Rule.Ue426, P(Vs, Ps), false, 0x1D, vsps(64));
        Check(Rule.Ue50, P(Vs, Ps), true, 0x311, vsps(64));  // UE 5: no hull/domain; SM6 denies mesh/amplification
        Check(Rule.Ue50, P(Vs, Ps), false, 0x11, vsps(64));
        Check(Rule.Ue426, P(Vs, Gs, Ps), false, 0xD,
            T(Px, Srv, 64), T(Px, Smp, 16), T(Px, Uav, 16), T(Vx, Srv, 64), T(Vx, Smp, 16), T(Gx, Srv, 64), T(Gx, Smp, 16),
            Cbv(Px, 0), Cbv(Px, 1), Cbv(Vx, 0), Cbv(Gx, 0), Cbv(Gx, 1), Cbv(Gx, 2));
        Check(Rule.Ue426, P(Cs), false, 0x3E, T(All, Srv, 64), T(All, Smp, 16), T(All, Uav, 16), Cbv(All, 0)); // compute denies every graphics stage
        // Kuro's 4.26 fork: 4.26's rows, plus the mesh/amplification deny bits on every root signature (Wuthering Waves' recording:
        // 90/90 distinct root signatures), whatever the tier
        Check(Rule.Kuro, P(Vs, Ps), false, 0x31D, vsps(64));
        Check(Rule.Kuro, P(Vs, Ps), true, 0x31D, vsps(64));
        Check(Rule.Kuro, P(Cs), false, 0x33E, T(All, Srv, 64), T(All, Smp, 16), T(All, Uav, 16), Cbv(All, 0));
        Check(Rule.Ue422, P(Cs), false, 0x3E, T(All, Srv, 48), T(All, Smp, 16), T(All, Uav, 16), Cbv(All, 0));
        Check(Rule.Ue50, P(CsDiagnostic), false, 0x32, T(All, Srv, 64), T(All, Smp, 16), T(All, Uav, 16), Cbv(All, 0), Diagnostic);
    }

    /// <summary>4.20/4.21 (Epic's 4.20.3/4.21.2 source): 4.22's layout (32 SRVs in 4.20, 48 in 4.21), but the input-layout flag
    /// only with a non-empty vertex declaration, taken as a VS that reads vertex input.</summary>
    [Fact]
    public void Ue420And421SetTheInputLayoutFlagOnlyForVertexInput()
    {
        var vsReads = Vs with { Inputs = [new SigElement("ATTRIBUTE", 0, 0, 0xF, 0, 3)] };
        uint[][] vsps(uint srv) => [T(Px, Srv, srv), T(Px, Smp, 16), T(Px, Uav, 16), T(Vx, Srv, srv), T(Vx, Smp, 16), Cbv(Px, 0), Cbv(Px, 1), Cbv(Vx, 0)];
        Check(Rule.Ue421, P(vsReads, Ps), false, 0x1D, vsps(48));   // IA | deny HS, DS, GS: 4.22's
        Check(Rule.Ue421, P(Vs, Ps), false, 0x1C, vsps(48));        // no vertex input: no IA flag
        Check(Rule.Ue422, P(Vs, Ps), false, 0x1D, vsps(48));        // 4.22: a declaration is always bound
        Check(Rule.Ue420, P(vsReads, Ps), false, 0x1D, vsps(32));
        Check(Rule.Ue421, P(Cs), false, 0x3E, T(All, Srv, 48), T(All, Smp, 16), T(All, Uav, 16), Cbv(All, 0));
    }

    /// <summary>A project may raise MAX_SRVS; a shader binding past the rule's means it did: the next size UE ships.</summary>
    [Fact]
    public void MaxSrvsFollowTheShadersWhenTheyNeedMore()
    {
        ShaderInfo[] upTo(int srv) => [Ps, S(Stage.Pixel, 1, srv, 0, 1)];
        Assert.Equal(0u, MaxSrvsFor(Rule.Ue421, upTo(48)));
        Assert.Equal(64u, MaxSrvsFor(Rule.Ue421, upTo(58)));    // Tiny Tina's Wonderlands: up to t57
        Assert.Equal(48u, MaxSrvsFor(Rule.Ue420, upTo(40)));
        Assert.Equal(0u, MaxSrvsFor(Rule.Ue426, upTo(80)));     // 64 already: nothing bigger to infer
        Assert.Equal(0u, MaxSrvsFor(Rule.Ff7, upTo(80)));
        var d = Build(Rule.Ue421, P(Vs, Ps), false, 64);
        Assert.Equal(64u, d.Rows[0][3]);
    }

    /// <summary>5.1+: a table only for the types the stage binds, so a stage binding nothing but CBs keeps only its root CBVs
    /// and one binding nothing at all is denied. 5.4: 32-sampler tables, root constants. 5.5: vertex UAVs.</summary>
    [Fact]
    public void Ue5TablesFollowTheCounts()
    {
        Check(Rule.Ue51, P(Vs, Ps), false, 0x11, T(Px, Srv, 64), T(Px, Smp, 16), T(Px, Uav, 16), Cbv(Px, 0), Cbv(Px, 1), Cbv(Vx, 0));
        Check(Rule.Ue54, P(Vs, Ps), true, 0x311, T(Px, Srv, 64), T(Px, Smp, 32), T(Px, Uav, 16), Cbv(Px, 0), Cbv(Px, 1), Cbv(Vx, 0));
        Check(Rule.Ue51, P(Vs, Gs, Ps), false, 0x1, T(Px, Srv, 64), T(Px, Smp, 16), T(Px, Uav, 16), T(Gx, Srv, 64),
            Cbv(Px, 0), Cbv(Px, 1), Cbv(Vx, 0), Cbv(Gx, 0), Cbv(Gx, 1), Cbv(Gx, 2));
        Check(Rule.Ue54, P(VsUav, Ps), false, 0x11, T(Px, Srv, 64), T(Px, Smp, 32), T(Px, Uav, 16), T(Vx, Srv, 64),
            Cbv(Px, 0), Cbv(Px, 1), Cbv(Vx, 0), Cbv(Vx, 1));
        Check(Rule.Ue55, P(VsUav, Ps), false, 0x11, T(Px, Srv, 64), T(Px, Smp, 32), T(Px, Uav, 16), T(Vx, Srv, 64), T(Vx, Uav, 16),
            Cbv(Px, 0), Cbv(Px, 1), Cbv(Vx, 0), Cbv(Vx, 1));
        var rc = new[] { T(All, Srv, 64), T(All, Smp, 32), T(All, Uav, 16), Cbv(All, 0), Cbv(All, 1), Cbv(All, 2), Cbv(All, 3) };
        Check(Rule.Ue54, P(CsRootConstants), true, 0x332, [.. rc, RootConstants]);
        Check(Rule.Ue51, P(CsRootConstants), true, 0x332, T(All, Srv, 64), T(All, Smp, 16), T(All, Uav, 16), Cbv(All, 0), Cbv(All, 1), Cbv(All, 2), Cbv(All, 3));
        Check(Rule.Ue55, P(CsDiagnostic), false, 0x32, T(All, Uav, 16), Cbv(All, 0), Diagnostic);
        Check(Rule.Ue55, P(Ms, Ps), true, 0x112, // no input layout without a vertex shader; deny VS, GS, amplification
            T(Px, Srv, 64), T(Px, Smp, 32), T(Px, Uav, 16), T(Mx, Srv, 64), T(Mx, Smp, 32),
            Cbv(Px, 0), Cbv(Px, 1), Cbv(Mx, 0), Cbv(Mx, 1), Cbv(Mx, 2), Cbv(Mx, 3), RootConstants);
    }

    /// <summary>5.8 (D3D12RootSignature.cpp, D3D12Util.cpp at 5.8.3): mesh and amplification shaders get UAV tables, and any
    /// stage with an NVIDIA vendor extension adds one table of u0 space 1001 (offset 0, no range flags, visible to all) after
    /// the root constants and before the diagnostic UAV, whatever the GPU: the extension is optional. 5.5-5.7 have neither.</summary>
    [Fact]
    public void Ue58MeshUavsAndNvExtensionTable()
    {
        var msUav = S(Stage.Mesh, 1, 2, 3, 1);
        var asUav = S(Stage.Amplification, 1, 0, 2, 0);
        const uint Ax = 6;
        Check(Rule.Ue58, P(asUav, msUav, Ps), true, 0x12, // deny VS, GS
            T(Px, Srv, 64), T(Px, Smp, 32), T(Px, Uav, 16), T(Mx, Srv, 64), T(Mx, Smp, 32), T(Mx, Uav, 16), T(Ax, Uav, 16),
            Cbv(Px, 0), Cbv(Px, 1), Cbv(Mx, 0), Cbv(Ax, 0));
        Check(Rule.Ue55, P(msUav, Ps), true, 0x112,
            T(Px, Srv, 64), T(Px, Smp, 32), T(Px, Uav, 16), T(Mx, Srv, 64), T(Mx, Smp, 32), Cbv(Px, 0), Cbv(Px, 1), Cbv(Mx, 0));
        uint[] nv = [0, All, Uav, 1, 0, 1001, 0, 0];
        var all = UeFlags.AmdIntrinsics | UeFlags.RootConstants | UeFlags.NvIntrinsics | UeFlags.DiagnosticBuffer;
        Check(Rule.Ue58, P(S(Stage.Compute, 0, 0, 1, 0, all)), false, 0x32, T(All, Uav, 16), Ags, RootConstants, nv, Diagnostic);
        Check(Rule.Ue55, P(S(Stage.Compute, 0, 0, 1, 0, all)), false, 0x32, T(All, Uav, 16), Ags, RootConstants, Diagnostic);
        Check(Rule.Ue58, P(Vs, S(Stage.Pixel, 2, 4, 4, 1, UeFlags.NvIntrinsics)), false, 0x11,
            T(Px, Srv, 64), T(Px, Smp, 32), T(Px, Uav, 16), Cbv(Px, 0), Cbv(Px, 1), Cbv(Vx, 0), nv);
        var blob = Serialize(Build(Rule.Ue58, P(S(Stage.Compute, 0, 0, 0, 0, UeFlags.NvIntrinsics)), false), StaticSamplers(Rule.Ue58));
        Assert.Contains((0u, 1u, 0u, 1u, 1001u, true), Parse(blob).Slots);
    }

    /// <summary>The extras UE 5 appends after the CBVs, in its order (AGS, root constants, diagnostic buffer), and the
    /// directly-indexed heap flags of bindless shaders; UE 4 has none of them.</summary>
    [Fact]
    public void Ue5ExtrasAndBindlessFlags()
    {
        var all = UeFlags.AmdIntrinsics | UeFlags.RootConstants | UeFlags.DiagnosticBuffer | UeFlags.BindlessResources | UeFlags.BindlessSamplers;
        Check(Rule.Ue55, P(S(Stage.Compute, 0, 0, 0, 0, all)), false, 0xC32, Ags, RootConstants, Diagnostic);
        Check(Rule.Ue51, P(S(Stage.Compute, 0, 0, 0, 0, all)), false, 0xC32, Ags, Diagnostic);
        Check(Rule.Ue426, P(S(Stage.Compute, 0, 0, 0, 0, all)), false, 0x3E, T(All, Srv, 64), T(All, Smp, 16), T(All, Uav, 16));
        Check(Rule.Ue51, P(Vs, S(Stage.Pixel, 1, 0, 0, 0, UeFlags.BindlessResources)), false, 0x411, Cbv(Px, 0), Cbv(Vx, 0));
    }

    /// <summary>A UE 4 fork's bindless SRVs (Respawn's, Jedi: Survivor): per bindless group of spaces 10g..10g+4 the shader
    /// declares, one five-range unbounded table right after its stage's UAV table, groups ascending, ranges at offset 0. A
    /// shader without one (every stock UE 4 shader) gets the stock signature.</summary>
    [Fact]
    public void Ue4BindlessGroupsGetOneTableEach()
    {
        static uint[] Group(uint vis, uint g) => [0, vis, .. Enumerable.Range(0, 5).SelectMany(k => new uint[] { 0, uint.MaxValue, 0, 10 * g + (uint)k, 5 })];
        var ps = S(Stage.Pixel, 1, 1, 0, 1) with { Bindings = [new("cbv", 0, 0, 1), new("srv", 0, 0, 1), new("srv", 11, 0, -1)] };
        var cs = S(Stage.Compute, 1, 1, 1, 0) with { Bindings = [new("srv", 42, 0, -1), new("srv", 11, 0, -1), new("srv", 13, 0, -1), new("srv", 31, 0, -1)] };
        Check(Rule.Ue426, P(Vs, ps), false, 0x1D, T(Px, Srv, 64), T(Px, Smp, 16), T(Px, Uav, 16), Group(Px, 1), T(Vx, Srv, 64), T(Vx, Smp, 16), Cbv(Px, 0), Cbv(Vx, 0));
        Check(Rule.Ue426, P(cs), false, 0x3E, T(All, Srv, 64), T(All, Smp, 16), T(All, Uav, 16), Group(All, 1), Group(All, 3), Group(All, 4), Cbv(All, 0));
        var rts0 = Serialize(Build(Rule.Ue426, P(Vs, ps), false), Ue426Samplers);
        var off = BitConverter.ToInt32(rts0, 32) + 8;                        // RTS0 part: param 3 is the group table
        var table = BitConverter.ToInt32(rts0, off + BitConverter.ToInt32(rts0, off + 8) + 12 * 3 + 8);
        Assert.Equal(5, BitConverter.ToInt32(rts0, off + table));
        var ranges = BitConverter.ToInt32(rts0, off + table + 4);
        Assert.All(Enumerable.Range(0, 5), k => Assert.Equal(0, BitConverter.ToInt32(rts0, off + ranges + 24 * k + 20))); // offset 0, not APPEND
    }

    /// <summary>Avalanche's 4.27 fork (Hogwarts Legacy, 54657/54657 recorded root signatures): a shader declaring an unbounded
    /// SRV t0 in space 4..9 gets one single-range table per space from 4 up to its highest, right after its stage's SRV table;
    /// the marker anywhere in the game makes every SRV table 128 (no shader binds more than 48). Without it: stock, 64.</summary>
    [Fact]
    public void Ue427SpaceBindlessTablesAndTheirSrvSize()
    {
        static uint[] Space(uint vis, uint k) => [0, vis, 0, uint.MaxValue, 0, k, 5];
        static uint[] T128(uint vis) => [0, vis, 0, 128, 0, 0, 5];
        var vs = S(Stage.Vertex, 1, 2, 0, 1) with { Bindings = [new("srv", 4, 0, -1)] };
        var ps = S(Stage.Pixel, 1, 1, 0, 1) with { Bindings = [new("srv", 7, 0, -1)] }; // only space 7: still 4, 5, 6, 7
        var cs = S(Stage.Compute, 0, 1, 1, 0) with { Bindings = [new("srv", 5, 0, -1), new("srv", 4, 0, -1)] };
        Assert.Equal(128u, MaxSrvsFor(Rule.Ue426, [Vs, ps]));
        Assert.Equal(0u, MaxSrvsFor(Rule.Ue426, [Vs, Ps]));
        Assert.Equal(0u, MaxSrvsFor(Rule.Ue55, [ps])); // UE 5 has its own bindless
        void Fork(Dictionary<Stage, ShaderInfo> st, uint flags, params uint[][] rows)
        {
            var d = Build(Rule.Ue426, st, false, 128);
            Assert.Equal(new Desc(flags, [.. rows]).Key, d.Key);
            if (D3D12Runtime.Available) Assert.Equal(0, D3D12Runtime.CreateRootSignature(Serialize(d, Ue426Samplers)));
        }
        Fork(P(vs, ps), 0x1D, T128(Px), Space(Px, 4), Space(Px, 5), Space(Px, 6), Space(Px, 7), T(Px, Smp, 16), T(Px, Uav, 16),
            T128(Vx), Space(Vx, 4), T(Vx, Smp, 16), Cbv(Px, 0), Cbv(Vx, 0));
        Fork(P(cs), 0x3E, T128(All), Space(All, 4), Space(All, 5), T(All, Smp, 16), T(All, Uav, 16));
        Fork(P(Vs), 0x3D, T128(Vx), T(Vx, Smp, 16), Cbv(Vx, 0)); // no marker in this shader: no bindless table, the game's 128 still
    }

    /// <summary>A 10-byte 'p' in the stock order (Hogwarts Legacy) or with a 16-bit SRV count (Jedi: Survivor): the shaders'
    /// own b registers decide, for all of them together.</summary>
    [Fact]
    public void WideCountsFollowTheShadersBindings()
    {
        static byte[] Code(params byte[] p) { byte[] t = [(byte)'p', .. BitConverter.GetBytes(p.Length), .. p]; return [9, .. t, .. BitConverter.GetBytes(t.Length + 4)]; }
        static ShaderInfo Info(string sha, byte[] code, int cbs) => new(sha, Stage.Pixel, "ps_6_1", 0, UeCounts(code), [.. Enumerable.Range(0, cbs).Select(b => new Binding("cbv", 0, b, 1))], [], []);
        byte[] jedi = Code(1, 2, 0x2C, 0x01, 3, 4, 0, 0, 1, 0xDB), hogwarts = Code(0, 2, 5, 3, 4, 0, 1, 0, 4, 0); // Jedi: 300 SRVs, 3 CBs, 4 UAVs
        foreach (var (code, wins, expected) in new[] { (jedi, true, new ResourceCounts(3, 300, 4, 2)), (hogwarts, false, new ResourceCounts(3, 5, 4, 2)) })
        {
            var info = Info("a", code, 3);
            var shaders = new System.Collections.Concurrent.ConcurrentDictionary<string, ShaderInfo> { ["a"] = info };
            var w = new WideCounts();
            w.See(code, info);
            Assert.Equal(wins, w.Apply(shaders));
            Assert.Equal(expected, shaders["a"].Counts);
        }
    }

    /// <summary>The optional-data trailer: 'p' usage flags, 'x' code features (uint16), 'v' vendor extensions (AMD, NVIDIA).</summary>
    [Fact]
    public void ReaderExposesUe5Flags()
    {
        static byte[] Entry(char k, byte[] b) => [(byte)k, .. BitConverter.GetBytes(b.Length), .. b];
        byte[] vendor = [.. BitConverter.GetBytes(1), .. BitConverter.GetBytes(0x1002u), 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 2];
        byte[] trailer = [.. Entry('p', [0x48, 1, 2, 3, 4]), .. Entry('x', [0x60, 0x01]), .. Entry('v', vendor)];
        byte[] code = [9, 9, 9, .. trailer, .. BitConverter.GetBytes(trailer.Length + 4)];
        Assert.Equal(new ResourceCounts(3, 2, 4, 1, 31), ShaderContainer.UeCounts(code, ue5: true)); // RC | diagnostic | bindless x2 | AMD
        Assert.Equal(new ResourceCounts(3, 2, 4, 1), ShaderContainer.UeCounts(code));                  // UE 4: none of these exist
        byte[] both = [.. BitConverter.GetBytes(2), .. BitConverter.GetBytes(0x10DEu), 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 2, .. BitConverter.GetBytes(0x1002u), 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 2];
        byte[] nv = [.. Entry('p', [0x00, 1, 2, 3, 4]), .. Entry('v', both)];
        byte[] nvCode = [9, .. nv, .. BitConverter.GetBytes(nv.Length + 4)];
        Assert.Equal(UeFlags.NvIntrinsics | UeFlags.AmdIntrinsics, ShaderContainer.UeCounts(nvCode, ue5: true, nvExtension: true).Flags);
        // before 5.8 the NVIDIA bit changes no root signature: it's left out, so an NVIDIA variant has the same learned-lookup
        // key as the AMD-only variant a recording covered
        byte[] amd = [.. Entry('p', [0x00, 1, 2, 3, 4]), .. Entry('v', vendor)];
        byte[] amdCode = [9, .. amd, .. BitConverter.GetBytes(amd.Length + 4)];
        SortedDictionary<Stage, ShaderInfo> Set(byte[] c, bool ue58) => new() { [Stage.Pixel] = S(Stage.Pixel, 0, 0, 0, 0) with { Counts = ShaderContainer.UeCounts(c, ue5: true, nvExtension: ue58) } };
        Assert.Equal(Planner.CountsKey(Set(amdCode, false)), Planner.CountsKey(Set(nvCode, false)));
        Assert.NotEqual(Planner.CountsKey(Set(amdCode, true)), Planner.CountsKey(Set(nvCode, true)));
        byte[] plain = [.. Entry('p', [0x01, 1, 2, 3, 4])];
        Assert.Equal(new ResourceCounts(3, 2, 4, 1), ShaderContainer.UeCounts([.. plain, .. BitConverter.GetBytes(plain.Length + 4)], ue5: true));
    }
}

/// <summary>Star Wars Jedi: Survivor (Respawn's fork of 4.26: 10-byte resource counts with a 16-bit SRV count, bindless SRV
/// tables) on this machine: the install indexed read-only and SCSKiller's recording of it (read,
/// never written). Returns early when either is missing.</summary>
public class JediSurvivorRootSigTests(Xunit.Abstractions.ITestOutputHelper output)
{
    const string Install = @"D:\EA\Jedi Survivor";
    static readonly string Exe = Path.Combine(Install, @"SwGame\Binaries\Win64\JediSurvivor.exe");

    /// <summary>The stock 4.26 rule + the bindless tables rebuild every recorded root signature byte for byte (23626/23626 on
    /// the recording), from counts the reader took from the wide layout.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void RootSignaturesRebuildByteExact()
    {
        if (!File.Exists(Exe)) return;
        Ff7.Codecs();
        var game = new Game("ea:198300", "STAR WARS Jedi: Survivor", Store.EA, Install, Exe);
        var reader = new UnrealReader(Ff7.TempDir("jedi-data"));
        var engine = reader.Detect(game)!;
        Assert.Equal(Rule.Ue426, RuleFor(engine));
        if (Ff7.Recording(game, engine, reader, "jedi-rec") is not { } db) return;
        var bc = reader.Index(game, engine, null, CancellationToken.None).Shaders;
        var recs = PsoDb.Read(db).ToList();
        var blobs = recs.Where(r => r.Tag == 'B').ToDictionary(r => PsoDb.Hex(r.Payload.AsSpan(0, 20)), r => r.Payload[20..]);
        int ok = 0, n = 0;
        foreach (var pso in recs.Where(r => r.Tag is 'G' or 'C' or 'S').Select(PsoDb.Parse))
        {
            if (!pso.Stages.Values.All(bc.ContainsKey) || !blobs.TryGetValue(pso.Rs, out var blob)) continue;
            n++;
            Assert.Equal(Ue426Samplers, Samplers(blob));
            if (Serialize(Build(Rule.Ue426, pso.Stages.ToDictionary(s => (Stage)s.Key, s => bc[s.Value]), false), Ue426Samplers).AsSpan().SequenceEqual(blob)) ok++;
        }
        output.WriteLine($"root signatures rebuilt byte-exact: {ok}/{n} recorded PSOs");
        Assert.True(n > 1000, $"only {n} recorded PSOs of index shaders");
        Assert.Equal(n, ok);
    }
}

/// <summary>The system D3D12 runtime on the default adapter: root signatures only, nothing compiled.</summary>
static unsafe class D3D12Runtime
{
    static readonly nint Device = Create();
    public static bool Available => Device != 0;

    static nint Create()
    {
        try
        {
            var lib = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "d3d12.dll"));
            var create = (delegate* unmanaged<nint, int, Guid*, nint*, int>)NativeLibrary.GetExport(lib, "D3D12CreateDevice");
            var iid = new Guid("189819f1-1db6-4b57-be54-1821339b85f7"); // ID3D12Device
            nint dev = 0;
            return create(0, 0xb000, &iid, &dev) >= 0 ? dev : 0; // D3D_FEATURE_LEVEL_11_0
        }
        catch (DllNotFoundException) { return 0; }
    }

    /// <summary>ID3D12Device::CreateRootSignature (vtable slot 16); the HRESULT, the object released.</summary>
    public static int CreateRootSignature(byte[] blob)
    {
        var iid = new Guid("c54a6b66-72df-4ee8-8be5-a946a1429214"); // ID3D12RootSignature
        nint rs = 0;
        int hr;
        fixed (byte* p = blob)
            hr = ((delegate* unmanaged<nint, uint, void*, nuint, Guid*, nint*, int>)(*(nint**)Device)[16])(Device, 0, p, (nuint)blob.Length, &iid, &rs);
        if (rs != 0) ((delegate* unmanaged<nint, uint>)(*(nint**)rs)[2])(rs);
        return hr;
    }
}
