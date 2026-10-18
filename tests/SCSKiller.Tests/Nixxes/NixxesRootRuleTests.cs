using System.Security.Cryptography;
using System.Text.Json;
using SCSKiller.Core;
using SCSKiller.Core.Nixxes;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Planning;
using static SCSKiller.Tests.Planning.ExactLayoutsTests;

namespace SCSKiller.Tests.Nixxes;

/// <summary>Ghost of Tsushima's and Ratchet &amp; Clank's root-signature rules (plan-rules.json) against the roots the games
/// created in recordings (Fixtures\Nixxes\roots: each blob as passed to CreateRootSignature, rules.json their SHA-1s).</summary>
public class NixxesRootRuleTests
{
    /// <summary>The store id and build a shipped rule is pinned to.</summary>
    internal static string Pinned(string id) => RulesFile.Embedded.Rules.Single(r => r.Id == id).Match.Games!.Single();

    static readonly EngineInfo GoT = new(NixxesReader.Family, NixxesReader.Version, NixxesReader.SuckerPunch, "D3D12", false, null, Game: Pinned("nixxes-got"));
    static readonly EngineInfo RaC = new(NixxesReader.Family, NixxesReader.Version, NixxesReader.Insomniac, "D3D12", false, null, Game: Pinned("nixxes-rc"));

    static string Fixture(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nixxes", "roots", file);
    static byte[] Root(string name) => File.ReadAllBytes(Fixture(name + ".rts0"));
    static string Sha(string name) => PsoDb.Hex(SHA1.HashData(Root(name)));

    static readonly SigElement Pos = new("SV_Position", 0, 0, 0xF, 1, 3), Uv = new("TEXCOORD", 0, 1, 0x3, 0, 3);

    static ShaderInfo S(string name, Stage stage, string model, Binding[] bindings, SigElement[]? inputs = null, SigElement[]? outputs = null, int gsInput = 0) =>
        Shader(name, stage, inputs ?? [], outputs, gsInput) with { ShaderModel = model, Bindings = bindings };

    /// <summary>Each description, serialized by the runtime with the rule's static samplers, is the blob the game passed to
    /// CreateRootSignature, byte for byte, in the rule's order.</summary>
    [FixtureTheory("Nixxes")]
    [InlineData("nixxes-got", "got-R6 got-R2 got-R1")]
    [InlineData("nixxes-rc", "rc-R4 rc-R2 rc-R5 rc-R1")]
    public void TheDescriptionsSerializeToTheRecordedRoots(string id, string roots)
    {
        var rule = RulesFile.Embedded.Rules.Single(r => r.Id == id);
        Assert.True(rule.Verified);
        var shas = JsonDocument.Parse(File.ReadAllText(Fixture("rules.json"))).RootElement.GetProperty("games").GetProperty(id == "nixxes-got" ? "got" : "rc").GetProperty("roots");
        var names = roots.Split(' ');
        Assert.Equal(names.Length, rule.RootSignature!.ByStages!.Length);
        foreach (var (b, name) in rule.RootSignature.ByStages.Zip(names))
        {
            var blob = RootSig.Serialize(b.Desc!.ToDesc(), RootSig.StaticSamplers(rule));
            Assert.Equal(Root(name), blob);
            Assert.Equal(shas.GetProperty(name.Split('-')[1]).GetProperty("sha1").GetString(), PsoDb.Hex(SHA1.HashData(blob)));
        }
    }

    /// <summary>A served copy of the rule is dropped alone, never planned wrong: by an app without rs.desc.v2 (its
    /// requires), and when it keys games or a predicated entry without that capability, has a predicated entry without a
    /// description, or leaves an allowed stage set to predicated entries only. One pinned to a store build matches it only.</summary>
    [Fact]
    public void AServedRuleIsDroppedWhenItCantBeReadAsMeant()
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(SCSKiller.Core.App.ContentFile.Embedded("SCSKiller.Core.Planning.plan-rules.json"))!;
        var rc = doc["rules"]!.AsArray().Single(r => (string)r!["id"]! == "nixxes-rc")!;
        string? Dropped(Action<System.Text.Json.Nodes.JsonNode> change)
        {
            var r = rc.DeepClone();
            change(r);
            var f = RulesFile.TryParse(new System.Text.Json.Nodes.JsonObject { ["rules"] = new System.Text.Json.Nodes.JsonArray(r) }.ToJsonString())!;
            return f.Rules.Count == 1 ? null : Assert.Single(f.Dropped);
        }
        Assert.Null(Dropped(_ => { }));
        Assert.Equal("nixxes-rc: needs rs.desc.v3", Dropped(r => r["requires"]!.AsArray().Add("rs.desc.v3")));
        Assert.Equal("nixxes-rc: bad match", Dropped(r => r["requires"] = new System.Text.Json.Nodes.JsonArray("rs.desc.v1", "stagesets.v1")));
        Assert.Equal("nixxes-rc: bad match", Dropped(r => r["match"]!["games"] = new System.Text.Json.Nodes.JsonArray()));
        Assert.Equal("nixxes-rc: bad byStages", Dropped(r => r["rootSignature"]!["byStages"]![0]!.AsObject().Remove("desc")));
        Assert.Equal("nixxes-rc: bad byStages: a stage set with no description", Dropped(r => r["rootSignature"]!["byStages"]![3]!["dxbc"] = true));
        Assert.Equal("nixxes-rc: malformed", Dropped(r => r["rootSignature"]!["byStages"]![0]!["sm5"] = true));

        // pinned to a build: another build, or an unknown one, gets no rule
        var pinned = rc.DeepClone();
        pinned["match"]!["games"] = new System.Text.Json.Nodes.JsonArray("steam:1895880@100");
        var rules = RulesFile.TryParse(new System.Text.Json.Nodes.JsonObject { ["rules"] = new System.Text.Json.Nodes.JsonArray(pinned) }.ToJsonString())!;
        Assert.Equal("nixxes-rc", rules.RuleFor(RaC with { Game = "steam:1895880@100" })?.Id);
        Assert.Null(rules.RuleFor(RaC with { Game = "steam:1895880@101" }));
        Assert.Null(rules.RuleFor(RaC));
        Assert.Null(RulesFile.Embedded.RuleFor(RaC with { Game = "steam:1895880@101" }));
    }

    /// <summary>The root each stage set gets is the one the game picks: GoT a compute shader declaring nothing R6, any other
    /// compute R2, graphics R1; R&amp;C a DXBC shader anywhere R4, then compute R2, tessellation R5, VS and VS+PS R1. A stage
    /// set no recording confirmed (a VS alone in GoT, a geometry shader in R&amp;C, where R6 fits but every recorded GS
    /// pipeline had a shader of no game file) has none.</summary>
    [FixtureFact("Nixxes")]
    public void PicksTheRootTheGamePicks()
    {
        string? Picked(EngineInfo e, params ShaderInfo[] shaders)
        {
            var rule = RulesFile.Embedded.RuleFor(e)!;
            if (!RootSig.InStageSets(rule, shaders.Select(s => s.Stage))) return null;
            return PsoDb.Hex(SHA1.HashData(RootSig.Serialize(RootSig.Build(rule, shaders.ToDictionary(s => s.Stage), false), RootSig.StaticSamplers(rule))));
        }
        Binding[] cb = [new("cbv", 0, 0, 1)];
        ShaderInfo vs = S("vs", Stage.Vertex, "vs_6_6", cb), ps = S("ps", Stage.Pixel, "ps_6_6", cb), hs = S("hs", Stage.Hull, "hs_6_6", cb),
            ds = S("ds", Stage.Domain, "ds_6_6", cb), gs = S("gs", Stage.Geometry, "gs_6_6", cb), cs = S("cs", Stage.Compute, "cs_6_6", cb),
            bare = S("cs-bare", Stage.Compute, "cs_6_6", []), vs5 = S("vs5", Stage.Vertex, "vs_5_0", cb), ps5 = S("ps5", Stage.Pixel, "ps_5_0", cb);

        Assert.Equal(Sha("got-R6"), Picked(GoT, bare));
        Assert.Equal(Sha("got-R2"), Picked(GoT, cs));
        Assert.Equal(Sha("got-R1"), Picked(GoT, vs, ps));
        Assert.Equal(Sha("got-R1"), Picked(GoT, vs, hs, ds, ps));
        Assert.Null(Picked(GoT, vs));

        Assert.Equal(Sha("rc-R4"), Picked(RaC, vs5, ps5));
        Assert.Equal(Sha("rc-R4"), Picked(RaC, vs5, ps));
        Assert.Equal(Sha("rc-R2"), Picked(RaC, cs));
        Assert.Equal(Sha("rc-R2"), Picked(RaC, bare));
        Assert.Equal(Sha("rc-R5"), Picked(RaC, vs, hs, ds, ps));
        Assert.Equal(Sha("rc-R1"), Picked(RaC, vs, ps));
        Assert.Equal(Sha("rc-R1"), Picked(RaC, vs));
        Assert.Null(Picked(RaC, vs, gs, ps));
        Assert.Null(Picked(RaC, vs, hs, ds));
    }

    /// <summary>A rule is the game's own, at the build it was recorded on: another Nixxes port of the same layout (Marvel's
    /// Spider-Man Remastered), the other layout's game id, another build or an unknown one gets none, so it needs a
    /// recording. Ready without one on NVIDIA; on AMD, as for every DirectX 12 game, the vertex shaders' input layouts come
    /// from a recording first.</summary>
    [Fact]
    public void OnlyTheGameItselfIsReadyWithoutARecording()
    {
        var rules = RulesFile.Embedded;
        Assert.Equal("nixxes-got", rules.RuleFor(GoT)?.Id);
        Assert.Equal("nixxes-rc", rules.RuleFor(RaC)?.Id);
        Assert.Null(rules.RuleFor(RaC with { Game = "steam:1817070" }));
        Assert.Null(rules.RuleFor(RaC with { Game = GoT.Game }));
        Assert.Null(rules.RuleFor(GoT with { Game = null }));
        Assert.Null(rules.RuleFor(GoT with { Game = "steam:2215430" }));   // build unknown
        Assert.Null(rules.RuleFor(GoT with { Fork = NixxesReader.Stingray, Game = "steam:553850" }));

        var game = new Game("steam:2215430", "Ghost of Tsushima DIRECTOR'S CUT", Store.Steam, @"C:\none", @"C:\none\GhostOfTsushima.exe");
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.NoRecording), new Planner().Check(game, GoT, null, Ff7.Nvidia));
        Assert.Equal(new PlanCheck(Readiness.NeedsRecording, Planner.Record), new Planner().Check(game, GoT, null, Ff7.Amd));
        Assert.Equal(Readiness.NeedsRecording, new Planner().Check(game, RaC with { Game = "steam:1817070" }, null, Ff7.Nvidia).Readiness);
        // a patched build: a recording until the rule lists it
        Assert.Equal(new PlanCheck(Readiness.NeedsRecording, Planner.Record), new Planner().Check(game, GoT with { Game = "steam:2215430@1" }, null, Ff7.Nvidia));
    }

    /// <summary>Planned from the files and the rule, on both NVIDIA paths: every planned stage set names the root the game
    /// picks and the plan carries its bytes; the coverage check still runs on each (a GoT pixel shader declaring a UAV, which
    /// R1 lacks, is left out as uncovered), and a stage set no recording confirmed (a VS alone) is left out.</summary>
    [FixtureFact("Nixxes")]
    public void PlansGhostOfTsushimaWithTheGamesRootsAndChecksEveryPipeline()
    {
        Binding[] gfx = [new("cbv", 0, 0, 1), new("srv", 0, 0, 1), new("sampler", 0, 0, 1)];
        var vs = S("got-vs", Stage.Vertex, "vs_6_6", [new("cbv", 0, 0, 1)], [In("POSITION", 0, 0, 7)], [Pos, Uv]);
        var ps = S("got-ps", Stage.Pixel, "ps_6_6", gfx, [Pos, Uv], [Target(0)]);
        var psUav = S("got-ps-uav", Stage.Pixel, "ps_6_6", [.. gfx, new("uav", 0, 0, 1)], [Pos, Uv], [Target(0)]);
        var cs = S("got-cs", Stage.Compute, "cs_6_6", [new("srv", 0, 0, 1), new("uav", 0, 0, 1)]);
        var bare = S("got-cs-bare", Stage.Compute, "cs_6_6", []);
        var want = new Dictionary<string, string> { [ps.Sha1] = "got-R1", [cs.Sha1] = "got-R2", [bare.Sha1] = "got-R6" };
        var stats = Plan(GoT, "nixxes-got", [vs, ps, psUav, cs, bare], want);
        Assert.Equal((5L, 2L, 1L), (stats.StageSets, stats.LeftOut, stats.Uncovered));   // VS alone (no_rs) and VS+PS(uav)
    }

    /// <summary>R&amp;C the same way: the exe's DXBC pair gets R4, a geometry shader's stage sets are left out (no_rs).</summary>
    [FixtureFact("Nixxes")]
    public void PlansRatchetAndClankWithTheGamesRootsAndLeavesOutGeometryShaders()
    {
        Binding[] cb = [new("cbv", 0, 0, 1)];
        var vs = S("rc-vs", Stage.Vertex, "vs_6_6", cb, [In("POSITION", 0, 0, 7)], [Pos, Uv]);
        var ps = S("rc-ps", Stage.Pixel, "ps_6_6", [.. cb, new("srv", 0, 0, 4)], [Pos, Uv], [Target(0)]);
        var gs = S("rc-gs", Stage.Geometry, "gs_6_6", cb, [Pos, Uv], [Pos, Uv], gsInput: 3);
        var vs5 = S("rc-vs5", Stage.Vertex, "vs_5_0", cb, [In("POSITION", 0, 0, 7)], [Pos, Uv, new("COLOR", 0, 2, 0xF, 0, 3)]);
        var ps5 = S("rc-ps5", Stage.Pixel, "ps_5_0", [.. cb, new("srv", 0, 0, 1), new("sampler", 0, 0, 1)], [Pos, Uv, new("COLOR", 0, 2, 0xF, 0, 3)], [Target(0)]);
        var cs = S("rc-cs", Stage.Compute, "cs_6_6", [new("srv", 0, 0, 1), new("uav", 0, 0, 1)]);
        var want = new Dictionary<string, string> { [ps.Sha1] = "rc-R1", [vs.Sha1] = "rc-R1", [ps5.Sha1] = "rc-R4", [vs5.Sha1] = "rc-R4", [cs.Sha1] = "rc-R2" };
        var stats = Plan(RaC, "nixxes-rc", [vs, ps, gs, vs5, ps5, cs], want);
        Assert.Equal(0L, stats.Uncovered);
        Assert.Equal(2L, stats.LeftOut);   // VS+GS+PS and VS+GS
    }

    /// <summary>R&amp;C's material archives are one pool each whose signatures pair every VS with every PS: Maximum mode keeps
    /// that pool's units (the per-stage cover) but not its other pairings; a pool with few pairs per shader keeps them all,
    /// also those it shares with the large pool, whichever pool is walked first.</summary>
    [Fact]
    public void MaximumLeavesOutTheCrossProductOfAPoolThatPairsEverything()
    {
        Binding[] cb = [new("cbv", 0, 0, 1)];
        ShaderInfo[] Many(string pool, int n) =>
        [
            .. Enumerable.Range(0, n).Select(i => S($"{pool}-vs{i}", Stage.Vertex, "vs_6_6", cb, [In("POSITION", 0, 0, 7)], [Pos, Uv])),
            .. Enumerable.Range(0, n).Select(i => S($"{pool}-ps{i}", Stage.Pixel, "ps_6_6", [.. cb, new("srv", 0, 0, 4)], [Pos, Uv], [Target(0)])),
        ];
        var material = Many("material", 120);   // 14,400 pairs of 240 shaders: 60 per shader
        var exe = Many("exe", 5);
        ShaderInfo[] shared = [material[0], material[1], material[120], material[121]];   // 2 VS, 2 PS of the large pool
        var index = new ShaderIndex("synthetic", ["D3D12"], material.Concat(exe).ToDictionary(s => s.Sha1),
            [new ShaderMap("m", @"d\materialgraph", "D3D12", [.. material.Select(s => s.Sha1)]), new ShaderMap("e", "RiftApart.exe", "D3D12", [.. exe.Select(s => s.Sha1)]),
             new ShaderMap("s", "small", "D3D12", [.. shared.Select(s => s.Sha1)])]);
        var game = new Game(RaC.Game!.Split('@')[0], "rc", Store.Steam, @"C:\none", @"C:\none\game.exe");
        var inMaterial = material.Select(s => s.Sha1).ToHashSet();
        List<(string Vs, string Ps)> Pairs(bool maximum, List<string> log)
        {
            var plan = new Planner { Rules = RulesFile.Embedded }.Build(game, RaC, index, null, Ff7.Nvidia with { PerStageCache = true },
                Ff7.TempDir($"rc-cross-{maximum}"), new Log(log.Add), CancellationToken.None, maximum);
            return [.. PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == 'S').Select(r => PsoDb.Parse(r).Stages).Where(st => st.Count == 2)
                .Select(st => (st[(int)Stage.Vertex], st[(int)Stage.Pixel]))];
        }
        var (standard, maximum) = (new List<string>(), new List<string>());
        var cover = Pairs(false, standard);
        var all = Pairs(true, maximum);
        Assert.Equal(120, cover.Count(p => inMaterial.Contains(p.Vs)));
        Assert.Equal(25, all.Count(p => !inMaterial.Contains(p.Vs)));
        var small = (from v in shared[..2] from p in shared[2..] select (v.Sha1, p.Sha1)).ToList();
        Assert.All(small, p => Assert.Contains(p, all));
        var added = small.Count(p => !cover.Contains(p));
        Assert.Equal(120 + added, all.Count(p => inMaterial.Contains(p.Vs)));
        Assert.Contains(maximum, l => l.Contains($"{14400 - 120 - added} more left out from 1 pools whose signatures pair nearly every VS with every PS (largest 14400 pairs of 120 VS and 120 PS)"));
        Assert.DoesNotContain(standard, l => l.Contains("pair nearly every VS"));
    }

    /// <summary>Builds the plan on both NVIDIA paths and checks each planned pipeline's root against <paramref name="want"/>
    /// (by the last of its shaders that has an entry) and that its bytes are in the plan.</summary>
    static PlanStats Plan(EngineInfo engine, string id, ShaderInfo[] all, Dictionary<string, string> want)
    {
        var index = new ShaderIndex("synthetic", ["D3D12"], all.ToDictionary(s => s.Sha1), [new ShaderMap("m", "pool", "D3D12", [.. all.Select(s => s.Sha1)])]);
        var game = new Game(engine.Game!.Split('@')[0], id, Store.Steam, @"C:\none", @"C:\none\game.exe");
        PlanStats? first = null;
        foreach (var caps in new[] { Ff7.Nvidia, Ff7.Nvidia with { PerStageCache = true } })
        {
            var log = new List<string>();
            var plan = new Planner { Rules = RulesFile.Embedded }.Build(game, engine, index, null, caps, Ff7.TempDir($"{id}-{caps.PerStageCache}"), new Log(log.Add), CancellationToken.None);
            var body = PlanFile.Read(plan.FilePath).Records.ToList();
            var blobs = body.Where(r => r.Tag == 'B').ToDictionary(r => PsoDb.Hex(r.Payload.AsSpan(0, 20)), r => r.Payload[20..]);
            var planned = body.Where(r => r.Tag is 'S' or 'P').Select(r => r.Tag == 'S' ? (PsoDb.Parse(r).Rs, PsoDb.Parse(r).Stages) : (PsoDb.ParseItem(r.Payload).Rs, PsoDb.ParseItem(r.Payload).Stages)).ToList();
            Assert.NotEmpty(planned);
            foreach (var p in planned)
            {
                var root = want[p.Stages.Values.Last(want.ContainsKey)];
                Assert.Equal(Sha(root), p.Rs);
                Assert.Equal(Root(root), blobs[p.Rs]);
            }
            Assert.Equal(want.Values.Distinct().Order(), planned.Select(p => p.Rs).Distinct().Select(h => want.Values.First(n => Sha(n) == h)).Order());
            Assert.Equal(id, plan.Stats.RsRule);
            Assert.True(plan.Stats.RootSigRuleVerified);
            Assert.DoesNotContain(log, l => l.Contains("rs_unknown"));
            Assert.Equal(first?.LeftOut ?? plan.Stats.LeftOut, plan.Stats.LeftOut);
            first ??= plan.Stats;
        }
        return first!;
    }

    sealed class Log(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }
}
