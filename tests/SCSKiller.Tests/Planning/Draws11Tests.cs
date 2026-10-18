using SCSKiller.Core.Games;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Tests.Planning;

/// <summary>D3D11 vertex-stage items for AMD (Draws11): recorded draws once per AMD key, the recorded layouts projected
/// onto the VSs no draw recorded.</summary>
public class Draws11Tests
{
    static SigElement In(string sem, int index, int reg, byte mask, int type = 3, byte read = 0xF) => new(sem, index, reg, mask, 0, type, read);

    static ShaderInfo Vs(string sha, params SigElement[] inputs) => new(sha, Stage.Vertex, "vs_5_0", 64, new(1, 1, 0, 1), [], inputs, []);

    const uint F32x3 = 6, F32x2 = 16, F16x2 = 34, U8x4 = 30, Append = ExactLayouts.AppendAligned;

    [Fact]
    public void Recorded_draws_plan_once_per_AMD_key_and_their_layouts_reach_unrecorded_vertex_shaders()
    {
        string H(char c) => new(c, 40);
        var a = Vs(H('a'), In("POSITION", 0, 0, 7), In("TEXCOORD", 0, 1, 3));
        var b = Vs(H('b'), In("POSITION", 0, 0, 7), In("TEXCOORD", 0, 1, 3), In("COLOR", 0, 2, 0xF, read: 0));   // COLOR declared, unread
        var c = Vs(H('c'), In("POSITION", 0, 0, 7), In("BLENDINDICES", 0, 1, 0xF, type: 1));   // a uint input no recorded layout has
        var d = Vs(H('d'), In("position", 0, 0, 7), In("texcoord", 0, 1, 3));                 // another case: semantics match anyway
        var infos = new[] { a, b, c, d }.ToDictionary(s => s.Sha1);
        List<LayoutElem> L(params LayoutElem[] e) => [.. e];
        var full = L(new("POSITION", 0, F32x3, 0), new("TEXCOORD", 0, F32x2, Append), new("COLOR", 0, U8x4, Append));
        var half = L(new("POSITION", 0, F32x3, 0), new("TEXCOORD", 0, F16x2, 0, Slot: 1));
        var recorded = new List<Draw11>
        {
            new(a.Sha1, Zero, Zero, Zero, 4, true, full),
            new(a.Sha1, Zero, Zero, Zero, 4, true, full),     // the same draw again
            new(a.Sha1, Zero, Zero, Zero, 2, true, full),     // lines: another compile
            new(b.Sha1, Zero, Zero, Zero, 4, true, full),
            new(b.Sha1, Zero, Zero, Zero, 4, true, L(new("POSITION", 0, F32x3, 0), new("TEXCOORD", 0, F32x2, 12), new("COLOR", 0, F16x2, 20))),  // unread COLOR's format only: the same key
            new(a.Sha1, Zero, Zero, Zero, 4, true, half),
            new(H('x'), Zero, Zero, Zero, 4, true, full),     // a VS nobody knows: left out
        };
        var r = Draws11.Plan(recorded, h => infos.GetValueOrDefault(h), [a.Sha1, b.Sha1, c.Sha1, d.Sha1]);
        Assert.Equal(4, r.Exact);
        // the recorded draw's layout keeps only what the VS declares, at resolved offsets
        Assert.Equal(L(new("POSITION", 0, F32x3, 0), new("TEXCOORD", 0, F32x2, 12)), r.Items[0].Layout);
        // d: both recorded layouts, the most drawn first; c: none fits (a uint input)
        Assert.Equal((2, 1), (r.Projected, r.VssProjected));
        var dItems = r.Items.Where(i => i.Vs == d.Sha1).ToList();
        Assert.Equal([F32x2, F16x2], dItems.Select(i => i.Layout.Single(e => e.Semantic == "TEXCOORD").Format));
        Assert.All(dItems, i => Assert.True(i.Topology == Draws11.TriangleList && i.Ps && i.Gs == Zero && i.Hs == Zero));
        Assert.DoesNotContain(r.Items, i => i.Vs == c.Sha1);
        // the item's bytes round-trip
        Assert.Equal(r.Items[0], ParseDraw11(Draw11Bytes(r.Items[0])) with { Layout = r.Items[0].Layout });
        Assert.Equal(r.Items[0].Layout, ParseDraw11(Draw11Bytes(r.Items[0])).Layout);
    }

    /// <summary>An AMD plan of a DirectX 11 game with a D3D11 recording: '3' items for the recorded draws and the VSs the
    /// recorded layouts fit, no '1' item for a VS with inputs, and the check names no recording to make.</summary>
    [Fact]
    public void An_AMD_plan_draws_the_recorded_vertex_stages()
    {
        string H(char c) => new(c, 40);
        var a = Vs(H('a'), In("POSITION", 0, 0, 7));
        var b = Vs(H('b'), In("POSITION", 0, 0, 7));
        var ps = new ShaderInfo(H('c'), Stage.Pixel, "ps_5_0", 64, new(1, 1, 0, 1), [], [], []);
        var index = new ShaderIndex("synthetic", ["PCD3D_SM5"], new[] { a, b, ps }.ToDictionary(s => s.Sha1), [new("m", "Global", "PCD3D_SM5", [a.Sha1, b.Sha1, ps.Sha1])]);
        var dir = Ff7.TempDir("draws11-plan");
        var db = Path.Combine(dir, "recording.db");
        using (var f = File.Create(db))
            Write(f, 'D', Draw11Bytes(new(a.Sha1, Zero, Zero, Zero, 4, true, [new("POSITION", 0, F32x3, 0)])));
        var engine = new EngineInfo("Unreal", "4.26", null, "D3D11", false, null);
        var plan = new Planner().Build(Ff7.Game, engine, index, new Recording(db), Ff7.Amd, Path.Combine(dir, "plan"), null, CancellationToken.None);
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        Assert.Equal([a.Sha1, b.Sha1], body.Where(r => r.Tag == '3').Select(r => ParseDraw11(r.Payload).Vs).Order());
        Assert.Equal([ps.Sha1], body.Where(r => r.Tag == '1').Select(r => Hex(r.Payload.AsSpan(4, 20))));
        Assert.Equal((3L, 2L), (plan.Stats.D3D11Shaders, plan.Stats.D3D11Draws));
        Assert.Equal(new PlanCheck(Readiness.Ready, "compiles every DirectX 11 shader"), new Planner().Check(Ff7.Game, engine, new Recording(db), Ff7.Amd));
        // materialized: the '3' items reach the warm with their VS's bytes, as the '1' items do
        var work = Path.Combine(dir, "work");
        new Planner().Materialize(plan, Ff7.Game, engine, new Bytes(), new Recording(db), work, CancellationToken.None);
        var gen = Read(Path.Combine(work, "scskiller_gen.db")).ToList();
        Assert.Equal(body.Where(r => r.Tag is '1' or '3').Select(r => r.Key).Order(), gen.Where(r => r.Tag is '1' or '3').Select(r => r.Key).Order());
        Assert.Subset(gen.Where(r => r.Tag == 'B').Select(r => Hex(r.Payload.AsSpan(0, 20))).ToHashSet(), new HashSet<string> { a.Sha1, b.Sha1, ps.Sha1 });
    }

    sealed class Bytes : IEngineReader
    {
        public EngineInfo? Detect(Game game) => null;
        public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => throw new NotSupportedException();
        public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
        {
            foreach (var h in sha1s) sink(h, Convert.FromHexString(h));
        }
    }

    /// <summary>D3D11 draws travel apart: a game build's entry holds none, its draws entry nothing else; a download is checked
    /// as the server checks the upload.</summary>
    [Fact]
    public void D3D11_draws_travel_in_their_own_entry()
    {
        var d = new Rec('D', Draw11Bytes(new(new string('a', 40), Zero, Zero, Zero, 4, true, [])));
        var pso = new Rec('C', Compute(Zero, new string('c', 40)));
        var both = HashOnly.Canonical([d, pso], local: false, out _);
        Assert.Equal(2, both.Count);
        Assert.Throws<InvalidDataException>(() => HashOnly.CheckNoDraws(both));
        Assert.Throws<InvalidDataException>(() => HashOnly.CheckDraws(both));
        Assert.Throws<InvalidDataException>(() => HashOnly.CheckDraw11(new Rec('D', [.. d.Payload, 0])));
        using var m = new MemoryStream();
        Write(m, d.Tag, d.Payload);
        Assert.Equal(1, Core.App.Community.Check(m.ToArray(), draws: true));
        Assert.Null(Core.App.Community.Check(m.ToArray()));
        Assert.True(HashOnly.IsDrawsKey(HashOnly.DrawsKey(new string('b', 40))));
        Assert.False(HashOnly.IsDrawsKey("steam:480@1"));
    }
}
