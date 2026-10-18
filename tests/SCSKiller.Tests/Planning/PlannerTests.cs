using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Planning;

public class PlannerTests(ITestOutputHelper output)
{
    static readonly EngineInfo Ue426 = new("Unreal", "4.26", "GAME_FinalFantasy7Rebirth", "D3D12", false, null);
    static readonly VendorCaps StateDependent = new("test", true, false, false);

    [Fact]
    public void CheckFollowsTheReadinessRules()
    {
        var p = new Planner();
        var game = Ff7.Game;
        Assert.Equal(new PlanCheck(Readiness.Ready, "no recording needed"), p.Check(game, Ue426, null, Ff7.Nvidia));
        Assert.Equal(new PlanCheck(Readiness.Ready, "no recording needed"), p.Check(game, Ue426 with { Version = "5.1", Fork = null }, null, Ff7.Nvidia)); // Oblivion Remastered's recording
        Assert.Equal(new PlanCheck(Readiness.Ready, "no recording needed"), p.Check(game, Ue426 with { Version = "5.6", Fork = null }, null, Ff7.Nvidia)); // SILENT HILL: Townfall's
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.Untested), p.Check(game, Ue426 with { Version = "5.2", Fork = null }, null, Ff7.Nvidia));
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.Untested), p.Check(game, Ue426 with { Fork = "GAME_StellarBlade" }, null, Ff7.Nvidia));
        Assert.Equal(new PlanCheck(Readiness.NeedsRecording, "turn on recording and play for about 5 minutes"), p.Check(game, Ue426 with { Version = "4.19", Fork = null }, null, Ff7.Nvidia));
        Assert.Equal("not tested on this engine version yet: playing with recording on improves it", Planner.Untested);
        Assert.Equal(Readiness.NeedsRecording, p.Check(game, Ue426, null, StateDependent).Readiness);
        Assert.Equal(new PlanCheck(Readiness.Unsupported, "shaders stored inside materials"), p.Check(game, Ue426 with { Unsupported = "shaders stored inside materials" }, null, Ff7.Nvidia));
        Assert.Equal(Readiness.Unsupported, p.Check(game, Ue426 with { Encrypted = true }, null, Ff7.Nvidia).Readiness);
        Assert.Equal(new PlanCheck(Readiness.Unsupported, "not supported on this GPU yet"), p.Check(game, Ue426, null, Ff7.Nvidia with { CacheKeyedByExeName = false }));
        var db = Path.GetTempFileName();
        File.WriteAllBytes(db, [(byte)'C', 40, 0, 0, 0, .. new byte[40]]);
        Assert.Equal(Readiness.Ready, p.Check(game, Ue426 with { Version = "4.19", Fork = null }, new Recording(db), Ff7.Nvidia).Readiness); // no rule, but a recording
    }

    /// <summary>A material PS nothing in its own map feeds gets every linking global VS; a fed one doesn't.</summary>
    [Fact]
    public void GlobalVertexShadersFeedUnfedMaterialPixelShaders()
    {
        static SigElement Pos(int reg) => ExactLayoutsTests.In("SV_Position", 0, reg, 0xF, 3, 1);
        var tile = ExactLayoutsTests.In("MACRO_TILE_INDEX", 0, 0, 1, 1);
        var uv = ExactLayoutsTests.In("TEXCOORD", 0, 0);
        ShaderInfo S(string n, Stage st, SigElement[] i, SigElement[] o) => ExactLayoutsTests.Shader(n, st, i, o);
        var nanite = S("g-nanite-vs", Stage.Vertex, [], [tile, Pos(1)]);
        var screen = S("g-screen-vs", Stage.Vertex, [], [uv, Pos(1)]);
        var naniteBusy = S("g-nanite-vs-2", Stage.Vertex, [], [tile, Pos(1)]);
        var lonely = S("m-nanite-ps", Stage.Pixel, [tile], [ExactLayoutsTests.Target(0)]);
        var vs = S("m-vs", Stage.Vertex, [ExactLayoutsTests.In("POSITION", 0, 0)], [uv, Pos(1)]);
        var ps = S("m-ps", Stage.Pixel, [uv], [ExactLayoutsTests.Target(0)]);
        var all = new[] { nanite, screen, naniteBusy, lonely, vs, ps };
        var index = new ShaderIndex("lonely", ["PCD3D_SM6"], all.ToDictionary(s => s.Sha1), [
            new ShaderMap("g1", "Global", "PCD3D_SM6", [nanite.Sha1, screen.Sha1]), new ShaderMap("g2", "Global", "PCD3D_SM6", [naniteBusy.Sha1]),
            new ShaderMap("a", "Game", "PCD3D_SM6", [lonely.Sha1]), new ShaderMap("b", "Game", "PCD3D_SM6", [vs.Sha1, ps.Sha1])]);
        var plan = new Planner().Build(Ff7.Game, new EngineInfo("Unreal", "5.1", null, "D3D12", false, null), index, null, Ff7.Nvidia, Ff7.TempDir("lonely-ps"), null, CancellationToken.None);
        var pairs = PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag is 'S' or 'P').Select(r => r.Tag == 'S' ? PsoDb.Parse(r).Stages : PsoDb.ParseItem(r.Payload).Stages)
            .Where(s => s.Count == 2).Select(s => (s[(int)Stage.Vertex], s[(int)Stage.Pixel])).ToHashSet();
        Assert.True(pairs.SetEquals([(nanite.Sha1, lonely.Sha1), (naniteBusy.Sha1, lonely.Sha1), (vs.Sha1, ps.Sha1)]), string.Join("; ", pairs)); // not (screen, ps)
    }

    /// <summary>The first stage set of a shape becomes its template, so the plan follows the index's map order: maps sharing a
    /// hash (one package in two paks) take theirs from <see cref="UnrealReader.Canonical"/>, not from thread timing. The order
    /// of the shader dictionary doesn't matter.</summary>
    [Fact]
    public void PlanFollowsTheCanonicalMapOrder()
    {
        var uv = ExactLayoutsTests.In("TEXCOORD", 0, 0);
        var pos = ExactLayoutsTests.In("SV_Position", 0, 1, 0xF, 3, 1);
        ShaderInfo Vs(string n) => ExactLayoutsTests.Shader(n, Stage.Vertex, [ExactLayoutsTests.In("POSITION", 0, 0)], [uv, pos]);
        ShaderInfo Ps(string n) => ExactLayoutsTests.Shader(n, Stage.Pixel, [uv], [ExactLayoutsTests.Target(0)]);
        var all = new[] { Vs("o-vs-a"), Ps("o-ps-a"), Vs("o-vs-b"), Ps("o-ps-b") };
        var a = new ShaderMap("pkg", "pkg", "PCD3D_SM6", [all[0].Sha1, all[1].Sha1]);
        var b = a with { Shaders = [all[2].Sha1, all[3].Sha1] };
        var dir = Ff7.TempDir("map-order");
        HashSet<string> Plan(string name, IReadOnlyList<ShaderMap> maps, IEnumerable<ShaderInfo> shaders)
        {
            var index = new ShaderIndex("order", ["PCD3D_SM6"], shaders.ToDictionary(s => s.Sha1), maps);
            var plan = new Planner().Build(Ff7.Game, new EngineInfo("Unreal", "5.1", null, "D3D12", false, null), index, null, Ff7.Nvidia,
                Path.Combine(dir, name), null, CancellationToken.None);
            return [.. PlanFile.Read(plan.FilePath).Records.Select(r => $"{r.Tag}{r.Key}")];
        }
        var ab = Plan("ab", UnrealReader.Canonical([a, b]), all);
        Assert.True(ab.SetEquals(Plan("ba", UnrealReader.Canonical([b, a]), all)));
        Assert.True(ab.SetEquals(Plan("ba-reversed", UnrealReader.Canonical([b, a]), all.Reverse())));
        Assert.False(Plan("raw-ab", [a, b], all).SetEquals(Plan("raw-ba", [b, a], all)));
    }

    /// <summary>A state-dependent cache (AMD) needs a recording, and one with draws: the input layouts come from it.</summary>
    [Fact]
    public void StateDependentCacheNeedsARecordingWithDraws()
    {
        var p = new Planner();
        Assert.Equal(new PlanCheck(Readiness.NeedsRecording, Planner.Record),
            p.Check(Ff7.Game, Ue426, null, Ff7.Amd));
        var dir = Ff7.TempDir("check-draws");
        var db = Path.Combine(dir, "recording.db");
        using (var f = File.Create(db)) PsoDb.Write(f, 'C', new byte[40]); // a compute PSO: the menu, shader warm-up
        Assert.Equal(new PlanCheck(Readiness.NeedsRecording, "the recording has no draws: play into the game world"), p.Check(Ff7.Game, Ue426, new Recording(db), Ff7.Amd));
        using (var f = new FileStream(db, FileMode.Append))
            PsoDb.Write(f, 'S', PsoDb.Stream(PsoDb.Zero, new Dictionary<int, string> { [(int)Stage.Vertex] = new('1', 40) }, [], 3, [], PsoDb.D32Float));
        Assert.Equal(new PlanCheck(Readiness.Ready, "planned from a recording"), p.Check(Ff7.Game, Ue426, new Recording(db), Ff7.Amd));
        Assert.Equal(new PlanCheck(Readiness.Ready, "planned from a recording"), p.Check(Ff7.Game, Ue426 with { Version = "4.19", Fork = null }, new Recording(db), Ff7.Nvidia));
    }

    /// <summary>The UE 4.26 rule + D3D12SerializeVersionedRootSignature rebuild every recorded root signature byte for byte,
    /// with the built-in static samplers (what recording-free mode uses).</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void RootSignaturesRebuildByteExact()
    {
        if (!Ff7.HasIndex) return;
        var bc = Ff7.Index().Shaders;
        var recs = PsoDb.Read(Ff7.RecordingDb).ToList();
        var blobs = recs.Where(r => r.Tag == 'B').ToDictionary(r => PsoDb.Hex(r.Payload.AsSpan(0, 20)), r => r.Payload[20..]);
        int ok = 0, n = 0;
        foreach (var r in recs.Where(r => r.Tag != 'B'))
        {
            var pso = PsoDb.Parse(r);
            n++;
            if (!pso.Stages.Values.All(bc.ContainsKey) || !blobs.TryGetValue(pso.Rs, out var blob)) continue;
            Assert.Equal(RootSig.Ue426Samplers, RootSig.Samplers(blob));
            var built = RootSig.Serialize(RootSig.BuildUe(pso.Stages.ToDictionary(s => (Stage)s.Key, s => bc[s.Value])), RootSig.Ue426Samplers);
            if (built.AsSpan().SequenceEqual(blob)) ok++;
        }
        output.WriteLine($"root signatures rebuilt byte-exact: {ok}/{n} recorded PSOs");
        Assert.Equal(n, ok);
    }

    /// <summary>A plan built from the first 551 recorded PSOs covers the new tuples recorded after it.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void HoldoutCoverage()
    {
        if (!Ff7.HasIndex) return;
        var dir = Ff7.TempDir("holdout");
        const int basis = 551;
        var first = Ff7.Truncated(basis, Path.Combine(dir, "first.db"));
        var plan = new Planner().Build(Ff7.Game, Ue426, Ff7.Index(), new Recording(first), StateDependent, dir, null, CancellationToken.None);
        var predicted = Ff7.PlanTuples(plan.FilePath);
        var recs = PsoDb.Read(Ff7.RecordingDb).Where(r => r.Tag != 'B').Select(r => PsoDb.Parse(r).Tuple).ToList();
        var seen = recs.Take(basis).ToHashSet();
        var later = recs.Skip(basis).Where(t => !seen.Contains(t)).ToList();
        var hit = later.Count(predicted.Contains);
        output.WriteLine($"{recs.Count - basis} PSOs recorded after the plan; {later.Count} with a new tuple; plan coverage {hit}/{later.Count}; plan {plan.Stats.Generated} PSOs");
        Assert.Equal(later.Count, hit);
    }

    /// <summary>With a recording: scskiller.db is the recording, gen.db holds blobs + items and no template copies.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void MaterializesWithRecording()
    {
        if (!Ff7.HasIndex) return;
        var dir = Ff7.TempDir("materialize");
        var full = new Planner().Build(Ff7.Game, Ue426, Ff7.Index(), new Recording(Ff7.RecordingDb), StateDependent, dir, null, CancellationToken.None);
        var body = PlanFile.Read(full.FilePath).Records.ToList();
        var plan = full with { FilePath = Path.Combine(dir, "subset.bin") };
        PlanFile.Write(plan, body.Where(r => r.Tag != 'P').Concat(body.Where(r => r.Tag == 'P').Take(500))); // small: every item pulls shaders
        var work = Path.Combine(dir, "work");
        new Planner().Materialize(plan, Ff7.Game, Ue426, Ff7.Shaders(), new Recording(Ff7.RecordingDb), work, CancellationToken.None);
        Assert.Equal(File.ReadAllBytes(Ff7.RecordingDb), File.ReadAllBytes(Path.Combine(work, "scskiller.db")));
        var gen = PsoDb.Read(Path.Combine(work, "scskiller_gen.db")).ToList();
        Assert.DoesNotContain(gen, r => r.Tag is 'G' or 'C' or 'S');
        Assert.Equal(500, gen.Count(r => r.Tag == 'P'));
        Ff7.CheckWarmReady(work);
    }

    /// <summary>Recording-free mode on FF7: synthesized templates only; how much of the real recording it predicts.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void RecordingFreeCoversTheRecording()
    {
        if (!Ff7.HasIndex) return;
        var dir = Ff7.TempDir("recfree");
        var p = new Planner();
        Assert.Equal(Readiness.Ready, p.Check(Ff7.Game, Ue426, null, Ff7.Nvidia).Readiness);
        var plan = p.Build(Ff7.Game, Ue426, Ff7.GsIndex(), null, Ff7.Nvidia, dir, new Progress<string>(output.WriteLine), CancellationToken.None);
        var tuples = Ff7.PlanTuples(plan.FilePath);
        var bc = Ff7.GsIndex().Shaders;
        var recorded = PsoDb.Read(Ff7.RecordingDb).Where(r => r.Tag != 'B').Select(PsoDb.Parse).Where(x => x.Stages.Values.All(bc.ContainsKey)).ToList();
        var byShape = recorded.GroupBy(r => string.Join('+', r.Stages.Keys)).Select(g => $"{g.Key}: {g.Count(r => tuples.Contains(r.Tuple))}/{g.Count()}");
        output.WriteLine($"{plan.Stats}, plan file {new FileInfo(plan.FilePath).Length / 1024} KiB; recorded tuples covered: {string.Join(", ", byShape)}");
        Assert.Equal(plan.Stats.Generated, tuples.Count + PlanFile.Read(plan.FilePath).Records.Count(r => r.Tag == 'Y')); // + FF7's 9 DXIL libraries' collections (UE 4.26's rule, no recording)
        // the only misses: a PS that reads none of its VS/GS outputs (FF7: 2 of 994, not paired on purpose, see Planner)
        var missed = recorded.Where(r => !tuples.Contains(r.Tuple)).ToList();
        Assert.All(missed, r => Assert.True(r.Stages.TryGetValue((int)Stage.Pixel, out var ps) && bc[ps].Inputs.All(e => e.SysValue != 0)));
        Assert.True(missed.Count <= 2);
    }

    /// <summary>A GS PSO gets the topology type of the GS's input (FF7: 35 triangle GSs, 1 point GS; its recording only has
    /// triangle GS PSOs). With a recording: the matching recorded template, or none without synthesized templates.
    /// Without: a synthesized template of that topology.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void GsChainsGetTheirInputTopology()
    {
        if (!Ff7.HasIndex) return;
        var bc = Ff7.GsIndex().Shaders;
        var gsPrims = bc.Values.Where(s => s.Stage == Stage.Geometry).GroupBy(s => s.GsInputPrimitive).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(new Dictionary<int, int> { [3] = 35, [1] = 1 }, gsPrims); // DXIL: PSV0 runtime info
        foreach (var (rec, caps) in new[] { (new Recording(Ff7.RecordingDb), StateDependent), (null, Ff7.Nvidia) })
        {
            var plan = new Planner().Build(Ff7.Game, Ue426, Ff7.GsIndex(), rec, caps, Ff7.TempDir("gstopo"), null, CancellationToken.None);
            var body = PlanFile.Read(plan.FilePath).Records.ToList();
            var templates = body.Where(r => r.Tag is 'G' or 'S').ToDictionary(r => r.Key, PsoDb.Parse);
            var gs = body.Where(r => r.Tag == 'P').Select(r => PsoDb.ParseItem(r.Payload)).Select(i => (templates[i.Template].Topology, i.Stages))
                .Concat(body.Where(r => r.Tag == 'S').Select(PsoDb.Parse).Select(t => (t.Topology, t.Stages)))
                .Where(x => x.Stages.ContainsKey((int)Stage.Geometry)).Select(x => (x.Topology, Prim: bc[x.Stages[(int)Stage.Geometry]].GsInputPrimitive)).ToList();
            output.WriteLine($"{(rec == null ? "recording-free" : "with recording")}: {gs.Count} GS PSOs, "
                + string.Join(", ", gs.GroupBy(x => x).Select(g => $"input {g.Key.Prim} -> topology {g.Key.Topology}: {g.Count()}")));
            Assert.All(gs, x => Assert.Equal(x.Prim == 1 ? 1u : 3u, x.Topology));
            Assert.Equal(rec == null, gs.Any(x => x.Prim == 1)); // with the recording: no point template to reuse, none synthesized
        }
    }

    /// <summary>A carved library's pool: <paramref name="n"/> VSs and as many PSs whose signatures all link, n x n stage sets.</summary>
    static ShaderIndex Pool(int n)
    {
        var uv = ExactLayoutsTests.In("TEXCOORD", 0, 0);
        var pos = ExactLayoutsTests.In("SV_Position", 0, 1, 0xF, 3, 1);
        var all = Enumerable.Range(0, n).SelectMany(i => new[] {
            ExactLayoutsTests.Shader($"pool-vs{i}", Stage.Vertex, [ExactLayoutsTests.In("POSITION", 0, 0)], [uv, pos]),
            ExactLayoutsTests.Shader($"pool-ps{i}", Stage.Pixel, [uv, pos], [ExactLayoutsTests.Target(0)]) }).ToList();
        return new ShaderIndex("pool" + n, ["PCD3D_SM6"], all.ToDictionary(s => s.Sha1), [new ShaderMap("pool", "Game", "PCD3D_SM6", [.. all.Select(s => s.Sha1)])]);
    }

    static readonly EngineInfo Ue51 = new("Unreal", "5.1", null, "D3D12", false, null);

    /// <summary>A plan over its memory or time budget fails with the reason, on both kinds of cache, and leaves no plan file;
    /// one under it is the plan an unlimited build makes, and its log says what it took.</summary>
    [Theory]
    [InlineData("nvidia")]
    [InlineData("amd")]
    public void A_plan_over_its_budget_fails_with_the_reason_and_writes_nothing(string vendor)
    {
        var caps = vendor == "amd" ? Ff7.Amd : Ff7.Nvidia;
        var index = Pool(40);
        var dir = Ff7.TempDir("plan-budget-" + vendor);
        foreach (var (planner, why) in new[] { (new Planner { MemoryBudget = 1 }, "the plan outgrew its memory limit of 0.0 GB on this PC at "),
                     (new Planner { TimeBudget = TimeSpan.Zero }, "the plan took over 0 minutes at ") })
        {
            var e = Assert.Throws<PlanTooBigException>(() => planner.Build(Ff7.Game, Ue51, index, null, caps, Path.Combine(dir, "over"), null, CancellationToken.None));
            Assert.StartsWith(why, e.Message);
            Assert.EndsWith(" stage sets: this game's shaders combine into more pipelines than a compile can hold", e.Message);
            Assert.False(Directory.Exists(Path.Combine(dir, "over")) && Directory.EnumerateFileSystemEntries(Path.Combine(dir, "over")).Any());
        }
        // a build checks once more before it writes, however few stage sets it walked
        Assert.Throws<PlanTooBigException>(() => new Planner { MemoryBudget = 1 }.Build(Ff7.Game, Ue51, Pool(1), null, caps, Path.Combine(dir, "small"), null, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(dir, "small", "plan.bin")));
        var log = new List<string>();
        var under = new Planner().Build(Ff7.Game, Ue51, index, null, caps, Path.Combine(dir, "under"), new SyncLog(log.Add), CancellationToken.None);
        var unlimited = new Planner { MemoryBudget = long.MaxValue, TimeBudget = TimeSpan.MaxValue }.Build(Ff7.Game, Ue51, index, null, caps, Path.Combine(dir, "unlimited"), null, CancellationToken.None);
        Assert.Equal(unlimited.Stats, under.Stats);
        Assert.Equal(PlanFile.Read(unlimited.FilePath).Records.Select(r => r.Key), PlanFile.Read(under.FilePath).Records.Select(r => r.Key));
        Assert.Contains(log, l => l.StartsWith("plan: built in ") && l.Contains(" GB limit"));
    }

    /// <summary>Over budget with the server's rules, the build fails: it doesn't plan the whole library again with the app's own.</summary>
    [Fact]
    public void A_plan_over_its_budget_with_the_servers_rules_fails_without_the_embedded_rules()
    {
        var log = new List<string>();
        var ue = RootSig.RuleFor(Ue51)!;
        var rules = RulesFile.WithServer(RulesFile.TryParse(System.Text.Json.JsonSerializer.Serialize(new { rules = new[] { ue with { Id = ue.Id + "-server" } } },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }))!);
        Assert.NotEqual(RulesFile.Embedded.Hash(Ue51), rules.Hash(Ue51));
        Assert.Throws<PlanTooBigException>(() => new Planner { Rules = rules, MemoryBudget = 1 }.Build(Ff7.Game, Ue51, Pool(40), null, Ff7.Nvidia,
            Ff7.TempDir("plan-budget-rules"), new SyncLog(log.Add), CancellationToken.None));
        Assert.DoesNotContain(log, l => l.Contains("keeps the app's own rules"));
    }

    sealed class SyncLog(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }
}
