using SCSKiller.Core;
using SCSKiller.Core.Planning;
using Xunit.Abstractions;
using static SCSKiller.Core.Planning.PsoDb;
using static SCSKiller.Tests.Planning.ExactLayoutsTests;

namespace SCSKiller.Tests.Planning;

/// <summary>The planner's per-stage branch (<see cref="VendorCaps.PerStageCache"/>, AMD): stage units covered once, each PSO
/// a synthesized stream carrying the state its units are keyed on.</summary>
public class PerStagePlanTests(ITestOutputHelper output)
{
    static readonly EngineInfo Ue426 = new("Unreal", "4.26", "GAME_FinalFantasy7Rebirth", "D3D12", false, null);

    static readonly SigElement PosOut = new("SV_Position", 0, 0, 0xF, 1, 3), UvOut = new("TEXCOORD", 0, 1, 0x3, 0, 3);
    static ShaderInfo Vs(string name, params SigElement[] inputs) => Shader(name, Stage.Vertex, inputs, [PosOut, UvOut]);
    static ShaderInfo Ps(string name, params SigElement[] outputs) => Shader(name, Stage.Pixel, [PosOut, UvOut], outputs);

    // VS1 recorded (L0); VS2 has VS1's input signature (L1, inferred); VS3 reads an element never recorded (guessed);
    // PS1 recorded to fp16 and PS3 to R32_FLOAT with write mask 0; PS2 (PS1's outputs) is inferred fp16
    static readonly ShaderInfo Vs1 = Vs("vs1", In("POSITION", 0, 0, 7), In("TEXCOORD", 0, 1, 3));
    static readonly ShaderInfo Vs2 = Vs("vs2", In("POSITION", 0, 0, 7), In("TEXCOORD", 0, 1, 3));
    static readonly ShaderInfo Vs3 = Vs("vs3", In("BLENDINDICES", 0, 0, 0xF, 1));
    static readonly ShaderInfo Ps1 = Ps("ps1", Target(0)), Ps2 = Ps("ps2", Target(0)), Ps3 = Ps("ps3", Target(0), Target(1));
    static readonly ShaderInfo Cs1 = Shader("cs1", Stage.Compute, []);
    static readonly List<LayoutElem> Layout1 = [new("POSITION", 0, 6, 0), new("TEXCOORD", 0, 34, 12, 1), new("COLOR", 0, 28, 16)]; // COLOR isn't read

    static ShaderIndex Index() => new("synthetic", ["PCD3D_SM6"], new[] { Vs1, Vs2, Vs3, Ps1, Ps2, Ps3, Cs1 }.ToDictionary(s => s.Sha1),
        [new ShaderMap("m", "Game", "PCD3D_SM6", new[] { Vs1, Vs2, Vs3, Ps1, Ps2, Ps3, Cs1 }.Select(s => s.Sha1).ToList())]);

    /// <summary>The root signature the planner builds for a stage set (FF7's rule), as the recording would carry it.</summary>
    static (string Sha, byte[] Blob) Rs(params ShaderInfo[] stages)
    {
        var b = RootSig.Serialize(RootSig.Build(Rules.Ff7, stages.ToDictionary(s => s.Stage), true), RootSig.StaticSamplers(Rules.Ff7));
        return (Hex(System.Security.Cryptography.SHA1.HashData(b)), b);
    }

    static string Recording(string dir)
    {
        var path = Path.Combine(dir, "recording.db");
        var (rsA, blobA) = Rs(Vs1, Ps1);
        var (rsB, blobB) = Rs(Vs1, Ps3);
        using var f = File.Create(path);
        WriteBlob(f, rsA, blobA);
        if (rsB != rsA) WriteBlob(f, rsB, blobB);
        Write(f, 'S', Gfx(rsA, Vs1, Ps1, Layout1, [R16G16B16A16Float]).Payload);
        Write(f, 'S', Gfx(rsB, Vs1, Ps3, Layout1, [41, 41], [0xF, 0]).Payload); // R32_FLOAT x2, RT 1 masked off
        return path;
    }

    /// <summary>The planner holds a recording's root signatures and reads its other blobs back from the file when it needs
    /// them: a plan from a proxy db is, byte for byte, the plan from a compact copy, whose blobs it holds whole.</summary>
    [Fact]
    public void A_plan_from_a_proxy_db_is_the_plan_from_its_blobs_held_whole()
    {
        var dir = Ff7.TempDir("perstage-blobs");
        var raw = Recording(dir);
        var big = new byte[(64 << 10) + 1000];
        new Random(1).NextBytes(big);
        var runtime = Shader("runtime ps", Stage.Pixel, [PosOut, UvOut], [Target(0)]);   // in no file of the install
        using (var f = new FileStream(raw, FileMode.Append))
        {
            WriteBlob(f, Vs1.Sha1, [.. "DXBC"u8, .. big]);   // over what is held
            WriteBlob(f, Ps1.Sha1, "DXBC ps1"u8);
            WriteBlob(f, Ps1.Sha1, "DXBC ps1"u8);   // twice
            WriteBlob(f, Ps2.Sha1, []);   // a hash only, then its bytes
            WriteBlob(f, Ps2.Sha1, "DXBC ps2"u8);
            WriteBlob(f, runtime.Sha1, "DXBC runtime"u8);
            Write(f, 'S', Gfx(Rs(Vs1, Ps1).Sha, Vs1, runtime, Layout1, [R16G16B16A16Float]).Payload);
            f.Write([(byte)'S', 9, 0]);   // a torn tail
        }
        var compact = Path.Combine(dir, "compact.db");
        WriteCompact(compact, Read(raw));
        foreach (var caps in new[] { Ff7.Nvidia, Ff7.Amd })
        {
            var plans = new[] { raw, compact }.Select(db => new Planner().Build(Ff7.Game, Ue426, Index(), new Recording(db), caps, Path.Combine(dir, caps.Profile + Path.GetFileName(db)), null, CancellationToken.None)).ToList();
            Assert.Equal(plans[0].Stats, plans[1].Stats);
            Assert.Equal(PlanFile.Read(plans[0].FilePath).Records.Select(r => r.Key), PlanFile.Read(plans[1].FilePath).Records.Select(r => r.Key));
        }
    }

    static List<(Rec Rec, PsoState State)> Psos(Plan plan) =>
        PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == 'S').Select(r => (r, ParseState(r) ?? new PsoState(Parse(r).Rs, Parse(r).Stages, [], 0, [], [], [], 0, 1))).ToList();

    [Fact]
    public void CoversEachStageUnitOnceWithItsState()
    {
        var dir = Ff7.TempDir("perstage");
        var log = new List<string>();
        var plan = new Planner().Build(Ff7.Game, Ue426, Index(), new Recording(Recording(dir)), Ff7.Amd, Path.Combine(dir, "plan"),
            new SyncLog(s => { log.Add(s); output.WriteLine(s); }), CancellationToken.None);
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        Assert.DoesNotContain(body, r => r.Tag is 'P' or 'G' or 'C'); // every PSO is its own synthesized stream
        var psos = Psos(plan);
        foreach (var (r, s) in psos) output.WriteLine($"{string.Join('+', s.Stages.Keys)} {string.Join(',', s.Stages.Values.Select(h => h[..4]))} topo {s.Topology} rt [{string.Join(',', s.RtFormats)}] masks [{string.Join(',', s.RtWriteMasks)}] dsv {s.Dsv} layout {L(s.Layout)}");
        string? Sha(PsoState s, Stage st) => s.Stages.GetValueOrDefault((int)st);

        // the units, each once: VS 1-3 alone (depth passes, a D32 depth buffer, no RT), VS 1-3 before a PS, PS 1-3, CS 1;
        // VS1+PS1 and VS1+PS3 are recorded (they replay as they are): not planned
        Assert.Equal(new[] { Vs1, Vs2, Vs3 }.Select(v => v.Sha1).Order(), psos.Where(p => p.State.Stages.Count == 1 && Sha(p.State, Stage.Vertex) != null).Select(p => Sha(p.State, Stage.Vertex)!).Order());
        Assert.All(psos.Where(p => p.State.Stages.Count == 1 && Sha(p.State, Stage.Vertex) != null), p => Assert.Equal((0, D32Float), (p.State.RtFormats.Length, p.State.Dsv)));
        Assert.Single(psos, p => Sha(p.State, Stage.Compute) == Cs1.Sha1);
        var pairs = psos.Where(p => Sha(p.State, Stage.Pixel) != null).ToList();
        Assert.DoesNotContain(pairs, p => Sha(p.State, Stage.Vertex) == Vs1.Sha1 && Sha(p.State, Stage.Pixel) is var ps && (ps == Ps1.Sha1 || ps == Ps3.Sha1));
        Assert.Equal(new[] { Vs2.Sha1, Vs3.Sha1 }.Order(), pairs.Select(p => Sha(p.State, Stage.Vertex)!).Distinct().Order()); // VS1's "ps" unit is recorded
        Assert.Equal((2L, 3L, 2L), (plan.Stats.ExactUnits, plan.Stats.InferredUnits, plan.Stats.GuessedUnits)); // VS1 alone + CS / VS2 x2 + PS2 / VS3 x2
        Assert.Equal(new[] { Ps2.Sha1 }, pairs.Select(p => Sha(p.State, Stage.Pixel)!).Except([Ps1.Sha1, Ps3.Sha1]).Distinct());
        Assert.Equal(3 + 1 + 2, psos.Count); // VS alone x3, CS, then VS2+PS2 (two new units in one PSO) and VS3 with a covered PS: 6, not 9 pairs

        // state: VS2 gets VS1's recorded read layout (COLOR, unread, left out), VS3 a guess; RT formats / masks per shape
        var read1 = ExactLayouts.ReadLayout(Layout1, Vs1);
        Assert.All(psos.Where(p => Sha(p.State, Stage.Vertex) is { } v && v != Vs3.Sha1), p => Assert.Equal(L(read1), L(p.State.Layout)));
        Assert.All(psos.Where(p => Sha(p.State, Stage.Vertex) == Vs3.Sha1), p => Assert.Equal(L(ExactLayouts.ReadLayout(Planner.VsLayout(Vs3), Vs3)), L(p.State.Layout)));
        Assert.All(pairs, p => Assert.Equal(3u, p.State.Topology));
        foreach (var (_, s) in pairs)
            if (Sha(s, Stage.Pixel) == Ps3.Sha1) Assert.Equal(([41u, 41u], new byte[] { 0xF, 0 }), (s.RtFormats, s.RtWriteMasks));
            else Assert.Equal([R16G16B16A16Float], s.RtFormats);

        // guesses last
        var firstGuess = psos.FindIndex(p => Sha(p.State, Stage.Vertex) == Vs3.Sha1);
        Assert.All(psos.Skip(firstGuess), p => Assert.Equal(Vs3.Sha1, Sha(p.State, Stage.Vertex)));

        // stats: VS3's two units are the only guesses; 2 of 3 VSs resolved from the recording (L0 VS1, L1 VS2): a warning
        Assert.Equal((2L, psos.Count), (plan.Stats.Recorded, (int)plan.Stats.Generated));
        Assert.Equal(2, plan.Stats.GuessedUnits);
        Assert.Equal(2.0 / 3, plan.Stats.LayoutCoverage, 6);
        Assert.Equal(3 + 2 + 1 + 1, plan.Stats.ExactUnits + plan.Stats.InferredUnits + plan.Stats.GuessedUnits); // VS alone x3, VS2/VS3 before a PS, PS2, CS
        Assert.Contains(log, l => l.StartsWith("warning: only 67%"));
        Assert.Contains(log, l => l.StartsWith("per-stage (amd): "));
    }

    /// <summary>Maximum on AMD adds at most as many pairings as the plan replays otherwise; NVIDIA's cache doesn't grow with them.</summary>
    [Fact]
    public void MaximumOnAmdAddsAtMostThePlansOwnSize()
    {
        var vss = Enumerable.Range(0, 6).Select(i => Vs($"vs-many{i}", In("POSITION", 0, 0, 7), In("TEXCOORD", 0, 1, 3))).ToList();
        var pss = Enumerable.Range(0, 6).Select(i => Ps($"ps-many{i}", Target(0))).ToList();
        var all = new[] { Vs1, Ps1, Ps3 }.Concat(vss).Concat(pss).ToList();
        var index = new ShaderIndex("synthetic", ["PCD3D_SM6"], all.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM6", all.Select(s => s.Sha1).ToList())]);
        var dir = Ff7.TempDir("perstage-maxcap");
        var rec = new Recording(Recording(dir));
        var log = new List<string>();
        Plan Build(VendorCaps caps, bool maximum) => new Planner().Build(Ff7.Game, Ue426, index, rec, caps, Path.Combine(dir, $"{caps.Profile}-{maximum}"),
            new SyncLog(log.Add), CancellationToken.None, maximum);
        var nvidia = Ff7.Nvidia with { PerStageCache = true };
        var (amd, amdMax, nvMax) = (Build(Ff7.Amd, false), Build(Ff7.Amd, true), Build(nvidia, true));
        var planned = (int)(amd.Stats.Generated + amd.Stats.Recorded);
        Assert.Equal(planned, PlanBuilder.MaximumExtras(Ff7.Amd, planned));
        Assert.Equal(amd.Stats.Generated + planned, amdMax.Stats.Generated);   // 7 x 9 stage sets: the cap binds
        Assert.Contains(log, l => l.StartsWith("per-stage (amd)") && l.Contains($"more left out, at most {planned} for this game"));
        Assert.DoesNotContain(log, l => l.StartsWith("per-stage (nvidia)") && l.Contains("left out"));
        var nv = Build(nvidia, false);
        Assert.True(nvMax.Stats.Generated - nv.Stats.Generated > nv.Stats.Generated + nv.Stats.Recorded);   // not capped
        Assert.Equal((int)(PlanBuilder.ExtraBudget / PlanBuilder.ExtraBytes), PlanBuilder.MaximumExtras(Ff7.Amd, int.MaxValue));
        Assert.Equal(int.MaxValue, PlanBuilder.MaximumExtras(nvidia, 10));
    }

    /// <summary>An AMD plan over its cache budget keeps the recorded PSOs, then a unit of every shader, then the VS units of
    /// the read keys most pixel shaders share; at or under it, it is the plan the default budget builds. NVIDIA has no budget.</summary>
    [Fact]
    public void An_amd_plan_over_its_cache_budget_keeps_the_likeliest_psos()
    {
        static ShaderInfo Reads(string name, byte mask) => Shader(name, Stage.Pixel, [PosOut, UvOut with { ReadMask = mask }], [Target(0)]);
        var vss = Enumerable.Range(0, 4).Select(i => Vs($"vs-budget{i}", In("POSITION", 0, 0, 7), In("TEXCOORD", 0, 1, 3))).ToList();
        var common = Enumerable.Range(0, 5).Select(i => Reads($"ps-common{i}", 3)).ToList();
        ShaderInfo[] rare = [Reads("ps-rare1", 1), Reads("ps-rare2", 2)];
        var all = new[] { Vs1, Ps1, Ps3 }.Concat(vss).Concat(rare).Concat(common).ToList();   // rare first: the walk order would keep them
        var index = new ShaderIndex("synthetic", ["PCD3D_SM6"], all.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM6", all.Select(s => s.Sha1).ToList())]);
        var dir = Ff7.TempDir("perstage-budget");
        var rec = new Recording(Recording(dir));
        var log = new List<string>();
        Plan Build(VendorCaps caps, long psos, bool maximum = false) => new Planner { CacheBudget = psos * PlanBuilder.PsoBytes }.Build(Ff7.Game, Ue426, index, rec, caps,
            Path.Combine(dir, $"{caps.Profile}-{caps.StateIndependentCache}-{psos}-{maximum}"), new SyncLog(log.Add), CancellationToken.None, maximum);
        List<string> Keys(Plan p) => PlanFile.Read(p.FilePath).Records.Select(r => r.Key).Order(StringComparer.Ordinal).ToList();

        var full = new Planner().Build(Ff7.Game, Ue426, index, rec, Ff7.Amd, Path.Combine(dir, "default"), new SyncLog(log.Add), CancellationToken.None);
        var planned = full.Stats.Recorded + full.Stats.Generated;
        var exact = Build(Ff7.Amd, planned);
        Assert.Equal(full.Stats, exact.Stats);
        Assert.Equal(Keys(full), Keys(exact));
        Assert.Equal(0, full.Stats.CacheBudgetLeftOut);
        Assert.DoesNotContain(log, l => l.StartsWith("cache budget"));

        const int drop = 4;
        var capped = Build(Ff7.Amd, planned - drop);
        Assert.Equal((full.Stats.Recorded, full.Stats.Generated - drop, (long?)drop), (capped.Stats.Recorded, capped.Stats.Generated, capped.Stats.CacheBudgetLeftOut));
        Assert.Contains(log, l => l.StartsWith("cache budget (amd-1)") && l.Contains($"{drop} left out"));
        var kept = Psos(capped);
        Assert.Equal(Psos(full).SelectMany(p => p.State.Stages.Values).ToHashSet(), kept.SelectMany(p => p.State.Stages.Values).ToHashSet());   // every shader compiles
        var dropped = Psos(full).Where(p => !Keys(capped).Contains(p.Rec.Key)).ToList();
        Assert.Equal(drop, dropped.Count);
        Assert.All(dropped, p => Assert.Contains(p.State.Stages[(int)Stage.Pixel], rare.Select(r => r.Sha1)));   // the read keys one PS has
        Assert.Equal(capped.Stats.Generated, Build(Ff7.Amd, planned - drop, maximum: true).Stats.Generated);   // no room for Maximum's pairings
        Assert.Equal(full.Stats.Generated + 2, Build(Ff7.Amd, planned + 2, maximum: true).Stats.Generated);   // they fill what is left under it
        Assert.Equal(PlanBuilder.CacheBudget, Core.Vendors.AmdAppCache.DxcCacheCap);
        var perGame = new Planner().Build(Ff7.Game, Ue426, index, rec, Ff7.Amd with { PerGameCacheCap = (planned - drop) * PlanBuilder.PsoBytes },
            Path.Combine(dir, "per-game"), null, CancellationToken.None);
        Assert.Equal((long?)drop, perGame.Stats.CacheBudgetLeftOut);   // a per-game cap (Intel) is the budget

        var nvidia = Ff7.Nvidia with { PerStageCache = true };
        Assert.Equal(new Planner().Build(Ff7.Game, Ue426, index, rec, nvidia, Path.Combine(dir, "nv"), null, CancellationToken.None).Stats, Build(nvidia, 1).Stats);
        Assert.Null(Build(nvidia, 1).Stats.CacheBudgetLeftOut);
    }

    /// <summary>An AMD plan from before the budget is built again only when it is over it, counting its filtering copies.</summary>
    [Fact]
    public void Only_an_amd_plan_from_before_the_cache_budget_and_over_it_is_built_again()
    {
        PlanStats Plan(long generated, long variants = 0, long? leftOut = null) => new(1, generated, 0, 0, true, Variants: variants, CacheBudgetLeftOut: leftOut);
        var over = PlanBuilder.CachePsos;   // with the one recorded PSO
        Assert.True(Core.App.ScsKiller.OverCacheBudget(Ff7.Amd, Plan(over)));
        Assert.True(Core.App.ScsKiller.OverCacheBudget(Ff7.Amd, Plan(over - 10, variants: 10)));
        Assert.False(Core.App.ScsKiller.OverCacheBudget(Ff7.Amd, Plan(over - 1)));
        Assert.False(Core.App.ScsKiller.OverCacheBudget(Ff7.Amd, Plan(over, leftOut: 0)));   // built with the budget: Maximum's pairings on top
        Assert.False(Core.App.ScsKiller.OverCacheBudget(Ff7.Nvidia with { PerStageCache = true }, Plan(over)));
        Assert.False(Core.App.ScsKiller.OverCacheBudget(Ff7.Amd, null));
    }

    [Fact]
    public void WholePipelinesWithoutAPerStageCache()
    {
        var dir = Ff7.TempDir("perstage-off");
        var plan = new Planner().Build(Ff7.Game, Ue426, Index(), new Recording(Recording(dir)), Ff7.Amd with { PerStageCache = false }, Path.Combine(dir, "plan"), null, CancellationToken.None);
        Assert.Contains(PlanFile.Read(plan.FilePath).Records, r => r.Tag == 'P'); // recorded templates with shaders swapped, every pair
        Assert.Equal((0L, 0L, 0L, 0.0), (plan.Stats.ExactUnits, plan.Stats.InferredUnits, plan.Stats.GuessedUnits, plan.Stats.LayoutCoverage));
    }

    /// <summary>A shader using AMD's AGS intrinsics (a UAV in space 0x7FFF0ADE) is left out on NVIDIA (no UE 4 root signature
    /// has the slot: E_INVALIDARG, and the game never uses that permutation there), counted as "vendor_extension". AMD gets past
    /// that filter, but a UE 4 root signature has no AGS slot there either, so the pre-emit guard leaves it out ("rs_uncovered").</summary>
    [Fact]
    public void AgsShadersOnlyOnAmd()
    {
        var ags = Shader("csAgs", Stage.Compute, []) with { Bindings = [new Binding("uav", 0x7FFF0ADE, 0, 1)] };
        var all = new[] { Vs1, Ps1, Cs1, ags };
        var index = new ShaderIndex("synthetic", ["PCD3D_SM6"], all.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM6", all.Select(s => s.Sha1).ToList())]);
        var dir = Ff7.TempDir("perstage-ags");
        foreach (var (caps, stat) in new[] { (Ff7.Nvidia, "vendor_extension 1"), (Ff7.Amd, "rs_uncovered 1") })
        {
            var log = new List<string>();
            var plan = new Planner().Build(Ff7.Game, Ue426, index, new Recording(Recording(dir)), caps, Path.Combine(dir, caps.Profile), new SyncLog(log.Add), CancellationToken.None);
            Assert.DoesNotContain(Psos(plan), p => p.State.Stages.ContainsValue(ags.Sha1));
            Assert.Contains(Psos(plan), p => p.State.Stages.ContainsValue(Cs1.Sha1));
            Assert.Contains(log, l => l.Contains(stat));
        }
    }

    /// <summary>The plan's recorded pipelines are the ones the warm replays on this GPU: a record naming a shader neither the
    /// recording nor the install has is skipped, and AMD's AGS permutation is never created on NVIDIA.</summary>
    [Fact]
    public void RecordedCountsOnlyWhatReplaysHere()
    {
        var ags = Shader("csAgs2", Stage.Compute, []) with { Bindings = [new Binding("uav", 0x7FFF0ADE, 0, 1)] };
        var all = new[] { Vs1, Ps1, Ps3, Cs1, ags };
        var index = new ShaderIndex("synthetic", ["PCD3D_SM6"], all.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM6", all.Select(s => s.Sha1).ToList())]);
        var dir = Ff7.TempDir("perstage-recorded");
        var db = Recording(dir);
        var (rs, blob) = Rs(Cs1);
        using (var f = new FileStream(db, FileMode.Append))
        {
            WriteBlob(f, rs, blob);
            Write(f, 'C', Compute(rs, ags.Sha1));
            Write(f, 'C', Compute(rs, new string('9', 40)));   // built at run time on another PC
        }
        foreach (var (caps, n) in new[] { (Ff7.Nvidia, 2L), (Ff7.Amd, 3L) })
            Assert.Equal(n, new Planner().Build(Ff7.Game, Ue426, index, new Recording(db), caps, Path.Combine(dir, caps.Profile), null, CancellationToken.None).Stats.Recorded);
    }

    /// <summary>[WaveSize] compute on the readers' "&lt;platform&gt; wave&lt;N&gt;" platforms: NVIDIA (32 lanes only) plans none of
    /// it, AMD (32 and 64 lanes) the platforms whose range takes 64.</summary>
    /// <summary>No root-signature rule (RE Engine): a stage set resolves only by its counts as a recorded PSO has them, and a
    /// material file can link thousands of VSs with thousands of PSs. Only what a recorded counts key has is paired: 12
    /// stage sets, not 9M.</summary>
    [Fact]
    public void LearnedLookupPairsOnlyRecordedCounts()
    {
        var re = new EngineInfo("RE Engine", "PAK 4.2", null, "D3D12", false, null);
        ShaderInfo[] vss = [.. Enumerable.Range(0, 3000).Select(i => Vs($"re-vs{i}", In("POSITION", 0, 0, 7)) with { Counts = new(1, i % 1000, 0, 0) })];
        ShaderInfo[] pss = [.. Enumerable.Range(0, 3000).Select(i => Ps($"re-ps{i}", Target(0)) with { Counts = new(1, i % 1000, 0, 0) })];
        var all = vss.Concat(pss).ToList();
        var index = new ShaderIndex("synthetic", ["PCD3D_SM6"], all.ToDictionary(s => s.Sha1), [new ShaderMap("m", "re_chunk_000.pak|0", "PCD3D_SM6", all.Select(s => s.Sha1).ToList())]);
        var dir = Ff7.TempDir("perstage-learned");
        var db = Path.Combine(dir, "recording.db");
        var (rs, blob) = Rs(Cs1); // not what the fallback rule builds for a VS+PS: the lookup is learned
        using (var f = File.Create(db))
        {
            WriteBlob(f, rs, blob);
            Write(f, 'S', Gfx(rs, vss[0], pss[0], Layout1, [R16G16B16A16Float]).Payload);
        }
        var counted = new[] { 0, 1000, 2000 }.SelectMany(i => new[] { vss[i].Sha1, pss[i].Sha1 }).ToHashSet();
        foreach (var caps in new[] { Ff7.Nvidia with { PerStageCache = true }, Ff7.Amd })
        {
            var plan = new Planner().Build(Ff7.Game, re, index, new Recording(db), caps, Path.Combine(dir, caps.Profile), null, CancellationToken.None);
            // VS 0, 1000, 2000 alone and with PS 0, 1000, 2000 (their counts are recorded), and each other shader once, left out
            Assert.Equal(3 + 3 * 3 + (6000 - 6), plan.Stats.StageSets);
            Assert.Equal(6000 - 6 + 3, plan.Stats.LeftOut); // and the 3 VSs alone: a learned lookup has no VS-only key
            var psos = Psos(plan);
            Assert.NotEmpty(psos);
            Assert.All(psos, p => Assert.Subset(counted, p.State.Stages.Values.ToHashSet()));
        }
    }

    /// <summary>A learned lookup's coverage counts what it doesn't pair: a PS whose VSs were never recorded with its counts
    /// (Q links only to A; A and Q are each recorded, not together) is left out, and a DXIL library, no raster stage set,
    /// isn't counted at all.</summary>
    [Fact]
    public void LearnedLookupCountsUnrecordedPairsAsLeftOut()
    {
        var re = new EngineInfo("RE Engine", "PAK 4.2", null, "D3D12", false, null);
        var extra = new SigElement("TEXCOORD", 1, 2, 0x3, 0, 3);
        var vsA = Shader("la-vsA", Stage.Vertex, [In("POSITION", 0, 0, 7)], [PosOut, UvOut, extra]) with { Counts = new(1, 1, 0, 0) };
        var vsB = Vs("la-vsB", In("POSITION", 0, 0, 7)) with { Counts = new(1, 2, 0, 0) };
        var psP = Ps("la-psP", Target(0)) with { Counts = new(1, 2, 0, 0) };
        var psX = Ps("la-psX", Target(0)) with { Counts = new(1, 1, 0, 0) };   // links to B, recorded only with A
        var psQ = Shader("la-psQ", Stage.Pixel, [PosOut, UvOut, extra], [Target(0)]) with { Counts = new(1, 2, 0, 0) };   // links to A, recorded only with B
        var lib = Shader("la-lib", Stage.Library, []);
        var dir = Ff7.TempDir("perstage-learned-left");
        var db = Path.Combine(dir, "recording.db");
        var (rs, blob) = Rs(Cs1);
        using (var f = File.Create(db))
        {
            WriteBlob(f, rs, blob);
            Write(f, 'S', Gfx(rs, vsB, psP, Layout1, [R16G16B16A16Float]).Payload);
            Write(f, 'S', Gfx(rs, vsA, psX, Layout1, [R16G16B16A16Float]).Payload);
        }
        foreach (var withLib in new[] { false, true })
        {
            ShaderInfo[] all = withLib ? [vsA, vsB, psP, psX, psQ, lib] : [vsA, vsB, psP, psX, psQ];
            var index = new ShaderIndex("synthetic", ["PCD3D_SM6"], all.ToDictionary(s => s.Sha1), [new ShaderMap("m", "re_chunk_000.pak|0", "PCD3D_SM6", all.Select(s => s.Sha1).ToList())]);
            var plan = new Planner().Build(Ff7.Game, re, index, new Recording(db), Ff7.Nvidia with { PerStageCache = true }, Path.Combine(dir, $"lib{withLib}"), null, CancellationToken.None);
            // A and B alone (no VS-only key), B+P, then X and Q left out once each
            Assert.Equal((5L, 4L), (plan.Stats.StageSets, plan.Stats.LeftOut));
        }
    }

    [Fact]
    public void WaveSizePlatformsOnlyOnAmd()
    {
        ShaderInfo w64 = Shader("cs-wave64", Stage.Compute, []), w64To128 = Shader("cs-wave64-128", Stage.Compute, []), w16 = Shader("cs-wave16", Stage.Compute, []);
        var all = new[] { Vs1, Ps1, Cs1, w64, w64To128, w16 };
        var index = new ShaderIndex("synthetic", ["PCD3D_SM6", "PCD3D_SM6 wave16", "PCD3D_SM6 wave64", "PCD3D_SM6 wave64-128"], all.ToDictionary(s => s.Sha1),
            [new ShaderMap("m", "Game", "PCD3D_SM6", [Vs1.Sha1, Ps1.Sha1, Cs1.Sha1]), new ShaderMap("m", "Game", "PCD3D_SM6 wave64", [w64.Sha1]),
             new ShaderMap("m", "Game", "PCD3D_SM6 wave64-128", [w64To128.Sha1]), new ShaderMap("m", "Game", "PCD3D_SM6 wave16", [w16.Sha1])]);
        var dir = Ff7.TempDir("perstage-wave");
        foreach (var (caps, want) in new[] { (Ff7.Nvidia, new[] { Cs1 }), (Ff7.Amd, new[] { Cs1, w64, w64To128 }) })
        {
            var plan = new Planner().Build(Ff7.Game, Ue426, index, new Recording(Recording(dir)), caps, Path.Combine(dir, caps.Profile), null, CancellationToken.None);
            var body = PlanFile.Read(plan.FilePath).Records.ToList();
            bool Planned(ShaderInfo s) => body.Any(r => r.Payload.AsSpan().IndexOf(Convert.FromHexString(s.Sha1)) >= 0);
            Assert.Equal(want.Select(s => s.Sha1).Order(), new[] { Cs1, w64, w64To128, w16 }.Where(Planned).Select(s => s.Sha1).Order());
        }
    }

    /// <summary>A recording mostly of [WaveSize] shaders still plans on its base platform: a wave partition counts for it, so
    /// the base platform's unrecorded pairs plan, and NVIDIA still plans no wave64 shader.</summary>
    [Fact]
    public void AWavePartitionCountsForItsBasePlatform()
    {
        ShaderInfo[] waves = [Shader("cs-w1", Stage.Compute, []), Shader("cs-w2", Stage.Compute, []), Shader("cs-w3", Stage.Compute, [])];
        var w4 = Shader("cs-w4", Stage.Compute, []);
        ShaderInfo[] all = [Vs1, Ps1, Ps2, w4, .. waves];
        var index = new ShaderIndex("synthetic", ["PCD3D_SM6", "PCD3D_SM6 wave64"], all.ToDictionary(s => s.Sha1),
            [new ShaderMap("m", "Game", "PCD3D_SM6", [Vs1.Sha1, Ps1.Sha1, Ps2.Sha1]), new ShaderMap("w", "Game", "PCD3D_SM6 wave64", [w4.Sha1, .. waves.Select(w => w.Sha1)])]);
        var dir = Ff7.TempDir("perstage-wave-base");
        var path = Path.Combine(dir, "recording.db");
        using (var f = File.Create(path))
        {
            var (rs, blob) = Rs(Vs1, Ps1);
            var (cs, csBlob) = Rs(waves[0]);
            WriteBlob(f, rs, blob);
            WriteBlob(f, cs, csBlob);
            Write(f, 'S', Gfx(rs, Vs1, Ps1, Layout1, [R16G16B16A16Float]).Payload);
            foreach (var w in waves) Write(f, 'C', Compute(cs, w.Sha1)); // 3 wave64 shaders recorded, 2 of the base platform
        }
        foreach (var caps in new[] { Ff7.Nvidia, Ff7.Amd })
        {
            var log = new List<string>();
            var plan = new Planner().Build(Ff7.Game, Ue426, index, new Recording(path), caps, Path.Combine(dir, caps.Profile), new SyncLog(log.Add), CancellationToken.None);
            var body = PlanFile.Read(plan.FilePath).Records.ToList();
            bool Planned(ShaderInfo s) => body.Any(r => r.Tag is 'S' or 'P' && r.Payload.AsSpan().IndexOf(Convert.FromHexString(s.Sha1)) >= 0);
            Assert.Contains(log, l => l.Contains("platform PCD3D_SM6;"));
            Assert.True(Planned(Ps2), string.Join(" | ", log));
            Assert.Equal(caps == Ff7.Amd, Planned(w4));
        }
    }

    /// <summary>A shared recording's PSO naming a shader neither the install nor the recording has (another build's, built at
    /// run time) is skipped when materialized, so it doesn't cover its other shaders' units: VS A, recorded only before such a
    /// PS, is still planned with the root signature it was recorded with.</summary>
    [Fact]
    public void ARecordedPsoThatCantReplayCoversNothing()
    {
        var gone = Ps("ps-gone", Target(0));
        var vsA = Vs("vsA", In("POSITION", 0, 0, 7), In("TEXCOORD", 0, 1, 3));
        var index = new ShaderIndex("synthetic", ["PCD3D_SM6"], new[] { vsA, Vs1, Ps1 }.ToDictionary(s => s.Sha1),
            [new ShaderMap("m", "Game", "PCD3D_SM6", [vsA.Sha1, Vs1.Sha1, Ps1.Sha1])]);
        var (rs, blob) = Rs(Vs1, Ps1);
        foreach (var local in new[] { false, true })
        {
            var dir = Ff7.TempDir("perstage-unreplayable");
            var path = Path.Combine(dir, "recording.db");
            using (var f = File.Create(path))
            {
                WriteBlob(f, rs, blob);
                if (local) WriteBlob(f, gone.Sha1, [.. "DXBC"u8, .. new byte[28]]); // this PC's recording has its bytes
                Write(f, 'S', Gfx(rs, vsA, gone, Layout1, [R16G16B16A16Float]).Payload);
                Write(f, 'S', Gfx(rs, Vs1, Ps1, Layout1, [R16G16B16A16Float]).Payload);
            }
            var plan = new Planner().Build(Ff7.Game, Ue426, index, new Recording(path), Ff7.Nvidia with { PerStageCache = true }, Path.Combine(dir, "plan"), null, CancellationToken.None);
            Assert.Equal(!local, Psos(plan).Any(p => p.State.Stages.ContainsValue(vsA.Sha1) && p.State.Rs == rs));
        }
    }

    /// <summary>FF7 Rebirth planned for AMD from the AMD session: how the NVIDIA machine's recording (another player, another
    /// part of the game) is covered by the plan's units plus the session's own (it replays as it is), per stage.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void Ff7AmdPlanCoversTheNvidiaRecording()
    {
        if (!Ff7.HasIndex || !File.Exists(Ff7.AmdSessionDb)) return;
        var index = Ff7.GsIndex();
        var dir = Ff7.TempDir("perstage-ff7");
        var plan = new Planner().Build(Ff7.Game, Ue426, index, new Recording(Ff7.AmdSessionDb), Ff7.Amd, dir, new SyncLog(output.WriteLine), CancellationToken.None);
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        var streams = body.Where(r => r.Tag == 'S').ToList();
        Assert.Equal(plan.Stats.Generated, streams.Count);
        Assert.DoesNotContain(body, r => r.Tag == 'P');

        // unit keys: root signatures by content (the plan's and the session's blobs), shader signatures from the index
        var x = ExactLayouts.FromDb(Ff7.RecordingDb, UnitPolicy.Amd, index.Shaders);
        var session = ExactLayouts.FromDb(Ff7.AmdSessionDb, UnitPolicy.Amd, index.Shaders);
        var rootSigs = session.Graphics.Select(s => s.Rs).Concat(session.Compute.Select(c => c.Rs)).ToHashSet();
        foreach (var r in body.Where(r => r.Tag == 'B')) x.AddRootSignature(Hex(r.Payload.AsSpan(0, 20)), r.Payload[20..]);
        foreach (var r in PsoDb.Read(Ff7.AmdSessionDb).Where(r => r.Tag == 'B' && rootSigs.Contains(Hex(r.Payload.AsSpan(0, 20)))))
            x.AddRootSignature(Hex(r.Payload.AsSpan(0, 20)), r.Payload[20..]);
        var covered = new HashSet<Unit>();
        foreach (var r in streams)
            if (ParseState(r) is { } s) covered.UnionWith(UnitCover.UnitsOf(x, s));
            else { var c = Parse(r); covered.Add(new Unit(Stage.Compute, c.Stages[(int)Stage.Compute], x.RsKey(c.Rs))); }
        foreach (var s in session.Graphics) covered.UnionWith(UnitCover.UnitsOf(x, s));
        foreach (var c in session.Compute) covered.Add(new Unit(Stage.Compute, c.Stages[(int)Stage.Compute], x.RsKey(c.Rs)));

        var want = UnitCover.RecordedUnits(x).Where(u => index.Shaders.ContainsKey(u.Shader)).ToHashSet(); // library shaders only
        var rows = want.GroupBy(u => u.Stage).OrderBy(g => g.Key).Select(g => (Stage: g.Key, Units: g.Count(), Hit: g.Count(covered.Contains))).ToList();
        output.WriteLine($"{plan.Stats}; NVIDIA recording's units covered: {string.Join(", ", rows.Select(r => $"{r.Stage} {r.Hit}/{r.Units}"))}, "
            + $"total {rows.Sum(r => r.Hit)}/{rows.Sum(r => r.Units)}");
        foreach (var u in want.Where(u => !covered.Contains(u) && u.Stage is Stage.Vertex or Stage.Pixel).Take(20)) output.WriteLine($"  missed {u.Stage} {u.Shader[..8]} {u.Key}");
        Assert.All(rows.Where(r => r.Stage is not (Stage.Vertex or Stage.Pixel)), r => Assert.Equal(r.Units, r.Hit)); // keyed on the root signature alone
        Assert.True(plan.Stats.LayoutCoverage >= 0.8);
        Assert.True(rows.Sum(r => r.Hit) >= 0.97 * rows.Sum(r => r.Units)); // 989/1001 (VS 219/227, PS 309/313)
    }

    /// <summary>Maximum mode on FF7: every stage set as well, and still exactly the per-stage plan's units, under both
    /// policies. Under NVIDIA's (once it reports a per-stage cache) it is today's whole-pipeline plan: the same (root
    /// signature, stages) tuples, as many PSOs.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void Ff7MaximumAddsEveryStageSetButNoUnit()
    {
        if (!Ff7.HasIndex || !File.Exists(Ff7.AmdSessionDb)) return;
        var index = Ff7.GsIndex();
        var dir = Ff7.TempDir("perstage-max");
        var today = new Planner().Build(Ff7.Game, Ue426, index, new Recording(Ff7.RecordingDb), Ff7.Nvidia, Path.Combine(dir, "today"), null, CancellationToken.None);
        foreach (var (name, caps, rec) in new[] { ("nvidia", Ff7.Nvidia with { PerStageCache = true }, Ff7.RecordingDb), ("amd", Ff7.Amd, Ff7.AmdSessionDb) })
        {
            var policy = UnitPolicy.For(caps)!;
            Plan Build(bool maximum) => new Planner().Build(Ff7.Game, Ue426, index, new Recording(rec), caps, Path.Combine(dir, $"{name}-{maximum}"), new SyncLog(output.WriteLine), CancellationToken.None, maximum);
            var (units, max) = (Build(false), Build(true));
            HashSet<Unit> UnitsOf(Plan plan)
            {
                var x = ExactLayouts.FromDb(rec, policy, index.Shaders);
                var body = PlanFile.Read(plan.FilePath).Records.ToList();
                foreach (var r in body.Where(r => r.Tag == 'B')) x.AddRootSignature(Hex(r.Payload.AsSpan(0, 20)), r.Payload[20..]);
                var set = UnitCover.RecordedUnits(x).ToHashSet();
                foreach (var r in body.Where(r => r.Tag == 'S'))
                    if (ParseState(r) is { } s) set.UnionWith(UnitCover.UnitsOf(x, s));
                    else { var c = Parse(r); set.Add(new Unit(Stage.Compute, c.Stages[(int)Stage.Compute], x.RsKey(c.Rs))); }
                return set;
            }
            var (u1, u2) = (UnitsOf(units), UnitsOf(max));
            output.WriteLine($"{name} policy: per-stage {units.Stats.Generated} PSOs, maximum {max.Stats.Generated} PSOs; units {u1.Count} / {u2.Count}");
            Assert.True(u1.SetEquals(u2)); // per-stage still compiles every unit
            Assert.True(max.Stats.Generated > units.Stats.Generated);
            Assert.Equal((units.Stats.ExactUnits, units.Stats.InferredUnits, units.Stats.GuessedUnits), (max.Stats.ExactUnits, max.Stats.InferredUnits, max.Stats.GuessedUnits));
            if (name != "nvidia") continue;
            var tuples = PlanFile.Read(max.FilePath).Records.Where(r => r.Tag == 'S').Select(r => Parse(r).Tuple).ToHashSet();
            var todays = Ff7.PlanTuples(today.FilePath);
            output.WriteLine($"today's pair plan: {today.Stats.Generated} PSOs, {todays.Count} tuples; maximum {tuples.Count} tuples, only today {todays.Except(tuples).Count()}, only maximum {tuples.Except(todays).Count()}");
            Assert.True(todays.SetEquals(tuples));
            Assert.Equal(today.Stats.Generated, max.Stats.Generated);
        }
    }

    /// <summary>A PS no VS of its own map feeds (Elden Ring's gxflvershader PSs, drawn behind material VSs of other bundles)
    /// gets the first VS of another map that links it, once, when it carries its own root signature; one without is left
    /// out (its root signature would be the VS's guess).</summary>
    [Fact]
    public void AnUnfedPixelShaderGetsOneVsFromAnotherMap()
    {
        var blob = Rts0(0);
        var rs = Hex(System.Security.Cryptography.SHA1.HashData(blob));
        var col = new SigElement("COLOR", 0, 1, 0xF, 0, 3);
        ShaderInfo orphan = Ps("ps-orphan", Target(0)) with { RootSignature = rs }, bare = Ps("ps-bare", Target(0)) with { Inputs = [PosOut, UvOut, col] };
        ShaderInfo own = Shader("vs-own", Stage.Vertex, [], [PosOut, col]) with { RootSignature = rs };
        ShaderInfo first = Vs("vs-first") with { RootSignature = rs }, second = Vs("vs-second") with { RootSignature = rs };
        ShaderInfo other = Shader("vs-other", Stage.Vertex, [], [PosOut, UvOut, col]) with { RootSignature = rs };
        var engine = new EngineInfo("FromSoftware", "DXIL+RTS0", "Elden Ring", "D3D12", false, null);
        var index = new ShaderIndex("synthetic", ["D3D12"], new[] { orphan, bare, own, first, second, other }.ToDictionary(s => s.Sha1),
            [new ShaderMap("flver", "flver", "D3D12", [orphan.Sha1, bare.Sha1, own.Sha1]), new ShaderMap("bdle", "bdle", "D3D12", [first.Sha1, second.Sha1, other.Sha1])], new Dictionary<string, byte[]> { [rs] = blob });
        var dir = Ff7.TempDir("perstage-orphan");
        var log = new List<string>();
        var plan = new Planner().Build(Ff7.Game, engine, index, null, Ff7.Nvidia with { PerStageCache = true }, dir, new SyncLog(log.Add), CancellationToken.None);
        var pairs = Psos(plan).Select(p => p.State.Stages).Where(s => s.ContainsKey((int)Stage.Pixel)).ToList();
        var paired = Assert.Single(pairs, s => s[(int)Stage.Pixel] == orphan.Sha1);
        Assert.Equal(first.Sha1, paired[(int)Stage.Vertex]);
        Assert.DoesNotContain(pairs, s => s[(int)Stage.Pixel] == bare.Sha1);
        Assert.Contains(log, l => l.StartsWith("plan:") && l.Contains("orphan_ps_paired 1"));
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; the test reads the log right after Build.</summary>
    sealed class SyncLog(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }
}
