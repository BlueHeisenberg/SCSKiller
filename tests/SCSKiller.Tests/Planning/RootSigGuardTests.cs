using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using Xunit.Abstractions;
using static SCSKiller.Tests.Planning.ExactLayoutsTests;

namespace SCSKiller.Tests.Planning;

/// <summary>The planner's pre-emit guard (a stage set whose root signature doesn't cover a resource its shaders declare is left
/// out, "rs_uncovered") and the DXBC SV_Position register check, synthetic and on Hogwarts Legacy's install.</summary>
public class RootSigGuardTests(ITestOutputHelper output)
{
    static readonly EngineInfo Ue427 = new("Unreal", "4.27", null, "D3D12", false, null);
    static readonly SigElement PosOut = new("SV_Position", 0, 0, 0xF, 1, 3), UvOut = new("TEXCOORD", 0, 1, 0x3, 0, 3);

    /// <summary>A bindless SRV range in a space no rule knows (t0 unbounded in space 3) has no slot in the root signature:
    /// the stage sets using it are counted, not planned, on both NVIDIA paths (whole pipelines and per stage).</summary>
    [Fact]
    public void StageSetsTheRootSignatureDoesNotCoverAreLeftOut()
    {
        var vs = Shader("vs", Stage.Vertex, [In("POSITION", 0, 0, 7)], [PosOut, UvOut]) with { Counts = new(1, 0, 0, 0), Bindings = [new("cbv", 0, 0, 1)] };
        var ps = Shader("ps", Stage.Pixel, [PosOut, UvOut], [Target(0)]) with { Counts = new(1, 1, 0, 1), Bindings = [new("cbv", 0, 0, 1), new("srv", 0, 0, 1), new("sampler", 0, 0, 1)] };
        var psBindless = ps with { Sha1 = Hash("psB"), Bindings = [.. ps.Bindings, new("srv", 3, 0, -1)] };
        var cs = Shader("cs", Stage.Compute, []) with { Counts = new(0, 1, 1, 0), Bindings = [new("srv", 0, 0, 1), new("uav", 0, 0, 1), new("srv", 3, 0, -1)] };
        var all = new[] { vs, ps, psBindless, cs };
        var index = new ShaderIndex("synthetic", ["PCD3D_SM5"], all.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM5", all.Select(s => s.Sha1).ToList())]);
        var dir = Ff7.TempDir("rs-guard");
        foreach (var caps in new[] { Ff7.Nvidia, Ff7.Nvidia with { PerStageCache = true } })
        {
            var log = new List<string>();
            var plan = new Planner().Build(Ff7.Game, Ue427, index, null, caps, Path.Combine(dir, caps.PerStageCache.ToString()), new SyncLog(log.Add), CancellationToken.None);
            var body = PlanFile.Read(plan.FilePath).Records.ToList();
            var planned = body.Where(r => r.Tag is 'S' or 'G' or 'C').Select(r => PsoDb.Parse(r).Stages.Values.ToHashSet()).ToList();
            Assert.Contains(planned, s => s.SetEquals([vs.Sha1, ps.Sha1]));
            Assert.DoesNotContain(planned, s => s.Contains(psBindless.Sha1) || s.Contains(cs.Sha1));
            Assert.Contains(log, l => l.Contains("rs_uncovered 2")); // VS+PS(bindless), CS
            Assert.Equal((4L, 2L, 2L), (plan.Stats.StageSets, plan.Stats.LeftOut, plan.Stats.Uncovered)); // CS, VS alone, VS+PS, VS+PS(bindless)
            Assert.Contains(log, l => l.StartsWith("warning: 2 stage sets left out") && l.Contains("srv space 3 unbounded"));
        }
    }

    /// <summary>A stage set the recording has replays as recorded: the game created it with that root signature, so it counts
    /// as "already_recorded", not "rs_uncovered", even when the guard says the root signature doesn't cover it (here the
    /// rule's rebuild, recorded for a PS with a bindless range in space 3 the rule doesn't know). On both NVIDIA paths.</summary>
    [Fact]
    public void ARecordedStageSetIsNotLeftOutAsUncovered()
    {
        var vs = Shader("rec-vs", Stage.Vertex, [In("POSITION", 0, 0, 7)], [PosOut, UvOut]) with { Counts = new(1, 0, 0, 0), Bindings = [new("cbv", 0, 0, 1)] };
        var ps = Shader("rec-ps", Stage.Pixel, [PosOut, UvOut], [Target(0)]) with { Counts = new(1, 1, 0, 1), Bindings = [new("cbv", 0, 0, 1), new("srv", 0, 0, 1), new("sampler", 0, 0, 1)] };
        var psBindless = ps with { Sha1 = Hash("rec-psB"), Bindings = [.. ps.Bindings, new("srv", 3, 0, -1)] };
        var all = new[] { vs, ps, psBindless };
        var index = new ShaderIndex("synthetic", ["PCD3D_SM5"], all.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM5", all.Select(s => s.Sha1).ToList())]);
        var dir = Ff7.TempDir("rs-recorded");
        var cold = PlanFile.Read(new Planner().Build(Ff7.Game, Ue427, index, null, Ff7.Nvidia, Path.Combine(dir, "cold"), null, CancellationToken.None).FilePath).Records.ToList();
        var rs = PsoDb.Parse(cold.Single(r => r.Tag == 'S' && PsoDb.Parse(r).Stages.ContainsValue(ps.Sha1))).Rs;   // the rule's, same counts as psBindless's
        var recording = Path.Combine(dir, "recording.db");
        using (var f = File.Create(recording))
        {
            PsoDb.WriteBlob(f, rs, cold.Single(r => r.Tag == 'B' && PsoDb.Hex(r.Payload.AsSpan(0, 20)) == rs).Payload[20..]);
            var g = Gfx(rs, vs, psBindless, [], [PsoDb.R16G16B16A16Float]);
            PsoDb.Write(f, g.Tag, g.Payload);
        }
        foreach (var caps in new[] { Ff7.Nvidia, Ff7.Nvidia with { PerStageCache = true } })
        {
            var log = new List<string>();
            var plan = new Planner().Build(Ff7.Game, Ue427, index, new Recording(recording), caps, Path.Combine(dir, caps.PerStageCache.ToString()), new SyncLog(log.Add), CancellationToken.None);
            Assert.Contains(log, l => l.Contains("root sigs rebuilt from shader counts: 1/1 exact -> building"));
            Assert.Equal((0L, 0L, 1L), (plan.Stats.Uncovered, plan.Stats.LeftOut, plan.Stats.Recorded));
            Assert.DoesNotContain(log, l => l.Contains("rs_uncovered"));
            if (!caps.PerStageCache) Assert.Contains(log, l => l.StartsWith("plan:") && l.Contains("already_recorded 1"));
        }
    }

    /// <summary>The root signature a game ships with its shaders (<see cref="ShaderIndex.RootSignatures"/>) is checked like the
    /// rule's: a VS drawn with its PS's (Naughty Dog's VSs carry none) whose b0 that root signature shows the pixel stage only
    /// is left out ("own_rs_uncovered"), not counted as a slot SCSKiller can't rebuild; a root signature whose bytes nothing
    /// has is never taken as covering ("rs_unknown").</summary>
    [Fact]
    public void TheGamesOwnRootSignatureMustCoverEveryStageAndAnUnknownOneCoversNothing()
    {
        var blob = RootSig.Serialize(new RootSig.Desc(0, [[2, 5, 0, 0, 0]]), []);   // root CBV b0, pixel only
        var rs = PsoDb.Hex(System.Security.Cryptography.SHA1.HashData(blob));
        var unknown = Hash("own-rs-unknown");
        var vs = Shader("own-vs", Stage.Vertex, [In("POSITION", 0, 0, 7)], [PosOut, UvOut]) with { Bindings = [new("cbv", 0, 0, 1)] };
        var bare = Shader("own-vs-bare", Stage.Vertex, [In("POSITION", 0, 0, 7)], [PosOut, UvOut]);
        var ps = Shader("own-ps", Stage.Pixel, [PosOut, UvOut], [Target(0)]) with { Bindings = [new("cbv", 0, 0, 1)], RootSignature = rs };
        var orphan = ps with { Sha1 = Hash("own-ps-unknown"), RootSignature = unknown };
        var all = new[] { vs, bare, ps, orphan };
        var index = new ShaderIndex("synthetic", ["D3D12"], all.ToDictionary(s => s.Sha1), [new ShaderMap("m", "pak", "D3D12", all.Select(s => s.Sha1).ToList())],
            new Dictionary<string, byte[]> { [rs] = blob });
        var engine = new EngineInfo(SCSKiller.Core.NaughtyDog.NaughtyDogReader.Family, SCSKiller.Core.NaughtyDog.NaughtyDogReader.Version, null, "D3D12", false, null);
        var dir = Ff7.TempDir("rs-own");
        foreach (var caps in new[] { Ff7.Nvidia, Ff7.Nvidia with { PerStageCache = true } })
        {
            var log = new List<string>();
            var plan = new Planner().Build(Ff7.Game, engine, index, null, caps, Path.Combine(dir, caps.PerStageCache.ToString()), new SyncLog(log.Add), CancellationToken.None);
            var body = PlanFile.Read(plan.FilePath).Records.ToList();
            var planned = body.Where(r => r.Tag == 'S').Select(r => PsoDb.Parse(r).Stages.Values.ToHashSet())
                .Concat(body.Where(r => r.Tag == 'P').Select(r => PsoDb.ParseItem(r.Payload).Stages.Values.ToHashSet())).ToList();
            Assert.Contains(planned, s => s.SetEquals([bare.Sha1, ps.Sha1]));
            Assert.DoesNotContain(planned, s => s.Contains(vs.Sha1) && s.Contains(ps.Sha1) || s.Contains(orphan.Sha1));
            Assert.Contains(log, l => l.StartsWith("plan:") && l.Contains("own_rs_uncovered 1") && l.Contains("rs_unknown 2"));
            Assert.Contains($"1 stage sets left out: the root signature the game ships with them doesn't cover their shaders (Vertex cbv space 0, e.g. {vs.Sha1[..12]})", log);
            Assert.Contains($"warning: 2 stage sets left out: no readable bytes for their root signature in the rules, the recording or the game's index (e.g. {unknown[..12]})", log);
            Assert.Equal((6L, 5L, 0L), (plan.Stats.StageSets, plan.Stats.LeftOut, plan.Stats.Uncovered)); // each VS alone (no_rs), VS+PS, VS+PS(unknown) x2
            output.WriteLine($"{caps.PerStageCache}: {string.Join(" | ", log.Where(l => l.StartsWith("plan:") || l.Contains("stage sets left out")))}; {plan.Stats}");
        }
    }

    /// <summary>Learned root signatures (the rule doesn't rebuild the recording's): compute shaders A (t0 space 0) and B (t0
    /// space 1) have the same counts and their own root signatures; C, unrecorded with A's bindings, gets the one that covers
    /// it, A's, whichever was recorded last.</summary>
    [Fact]
    public void TheLearnedLookupTakesARootSignatureThatCovers()
    {
        ShaderInfo Cs(string name, int space) => Shader(name, Stage.Compute, []) with { ShaderModel = "cs_6_0", Counts = new(0, 1, 0, 0), Bindings = [new("srv", space, 0, 1)] };
        var (a, b, c) = (Cs("lk-a", 0), Cs("lk-b", 1), Cs("lk-c", 0));
        (string Sha, byte[] Blob) Rs(uint space) { var x = RootSig.Serialize(new RootSig.Desc(0, [[0, 0, 0, 1, 0, space, 0]]), []); return (PsoDb.Hex(System.Security.Cryptography.SHA1.HashData(x)), x); }
        var (ra, rb) = (Rs(0), Rs(1));
        var index = new ShaderIndex("synthetic", ["PCD3D_SM6"], new[] { a, b, c }.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM6", [a.Sha1, b.Sha1, c.Sha1])]);
        foreach (var order in new[] { new[] { (a, ra), (b, rb) }, [(b, rb), (a, ra)] })
        {
            var dir = Ff7.TempDir("rs-learned");
            var path = Path.Combine(dir, "recording.db");
            using (var f = File.Create(path))
                foreach (var (cs, rs) in order)
                {
                    PsoDb.WriteBlob(f, rs.Sha, rs.Blob);
                    PsoDb.Write(f, 'C', PsoDb.Compute(rs.Sha, cs.Sha1));
                }
            var log = new List<string>();
            var plan = new Planner().Build(Ff7.Game, Ue427, index, new Recording(path), Ff7.Nvidia, Path.Combine(dir, "plan"), new SyncLog(log.Add), CancellationToken.None);
            Assert.Contains(log, l => l.Contains("learned lookup"));
            var items = PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == 'P').Select(r => PsoDb.ParseItem(r.Payload)).ToList();
            Assert.Equal(ra.Sha, Assert.Single(items, i => i.Stages.ContainsValue(c.Sha1)).Rs);
            Assert.Equal(0, plan.Stats.Uncovered);
        }
    }

    /// <summary>An unbounded texture array needs a descriptor table: a root SRV at its first register doesn't serve it (the
    /// runtime rejects that), a bounded table holding it does. A (root SRV t0) and B (a table of 8 from t0) have the same
    /// counts; C, unrecorded with t0 unbounded, gets B's whichever was recorded last, and with only A's recorded none.</summary>
    [Fact]
    public void AnUnboundedRangeNeedsATableNotARootDescriptor()
    {
        ShaderInfo Cs(string name, int count) => Shader(name, Stage.Compute, []) with { ShaderModel = "cs_5_1", Counts = new(0, 1, 0, 0), Bindings = [new("srv", 0, 0, count)] };
        var (a, b, c) = (Cs("ub-a", 1), Cs("ub-b", 1), Cs("ub-c", -1));
        (string Sha, byte[] Blob) Rs(uint[] row) { var x = RootSig.Serialize(new RootSig.Desc(0, [row]), []); return (PsoDb.Hex(System.Security.Cryptography.SHA1.HashData(x)), x); }
        var (root, table) = (Rs([3, 0, 0, 0, 0]), Rs([0, 0, 0, 8, 0, 0, 0]));
        Assert.NotNull(RootSig.Uncovered(RootSig.Parse(root.Blob), Stage.Compute, c));
        Assert.Null(RootSig.Uncovered(RootSig.Parse(table.Blob), Stage.Compute, c));
        var index = new ShaderIndex("synthetic", ["PCD3D_SM5"], new[] { a, b, c }.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM5", [a.Sha1, b.Sha1, c.Sha1])]);
        foreach (var order in new[] { new[] { (a, root), (b, table) }, [(b, table), (a, root)], [(a, root)] })
        {
            var dir = Ff7.TempDir("rs-unbounded");
            var path = Path.Combine(dir, "recording.db");
            using (var f = File.Create(path))
                foreach (var (cs, rs) in order)
                {
                    PsoDb.WriteBlob(f, rs.Sha, rs.Blob);
                    PsoDb.Write(f, 'C', PsoDb.Compute(rs.Sha, cs.Sha1));
                }
            var plan = new Planner().Build(Ff7.Game, Ue427, index, new Recording(path), Ff7.Nvidia, Path.Combine(dir, "plan"), null, CancellationToken.None);
            var items = PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == 'P').Select(r => PsoDb.ParseItem(r.Payload)).ToList();
            if (order.Length == 1)
            {
                Assert.DoesNotContain(items, i => i.Stages.ContainsValue(c.Sha1));
                Assert.Equal(1, plan.Stats.Uncovered);
            }
            else Assert.Equal(table.Sha, Assert.Single(items, i => i.Stages.ContainsValue(c.Sha1)).Rs);
        }
    }

    /// <summary>DXBC links by register: a VS writing SV_RenderTargetArrayIndex in r0 and SV_POSITION in r1 can't feed a PS reading
    /// SV_Position from r0 (the runtime's linkage error; Hogwarts: 9 PSOs), so the pair isn't planned; the same pair in DXIL
    /// (linked by semantic) and a VS with SV_POSITION in r0 are.</summary>
    [Fact]
    public void DxbcSvPositionMustShareItsRegister()
    {
        var vs = Shader("vsL", Stage.Vertex, [], [new("SV_RenderTargetArrayIndex", 0, 0, 1, 4, 1), new("SV_POSITION", 0, 1, 0xF, 1, 3)]);
        var vs0 = Shader("vsL0", Stage.Vertex, [], [new("SV_POSITION", 0, 0, 0xF, 1, 3)]);
        var ps = Shader("psL", Stage.Pixel, [new("SV_Position", 0, 0, 0xF, 1, 3), new("SV_IsFrontFace", 0, 1, 1, 9, 1)], [Target(0)]);
        var dir = Ff7.TempDir("sv-position");
        bool Paired(ShaderInfo v, ShaderInfo p, string sm)
        {
            var (v2, p2) = (v with { ShaderModel = "vs" + sm }, p with { ShaderModel = "ps" + sm });
            var index = new ShaderIndex("synthetic", ["PCD3D_SM5"], new[] { v2, p2 }.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM5", [v2.Sha1, p2.Sha1])]);
            var plan = new Planner().Build(Ff7.Game, Ue427, index, null, Ff7.Nvidia, Path.Combine(dir, v.Sha1[..6] + sm), null, CancellationToken.None);
            return PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == 'S').Any(r => PsoDb.Parse(r).Stages.ContainsValue(p2.Sha1));
        }
        Assert.False(Paired(vs, ps, "_5_0"));
        Assert.True(Paired(vs, ps, "_6_0"));
        Assert.True(Paired(vs0, ps, "_5_0"));
    }

    /// <summary>Tiny Tina's Wonderlands (4.21, no recording): its version-1 shader archives are read (they have no shader maps:
    /// packages reference their shaders by hash), the 4.21 rule with 64-SRV tables (its shaders bind up to t57) covers every
    /// planned PSO (the sweep's own reader). 92901 shaders, 7939 maps, 123592 PSOs, 0 uncovered, 0 failed on WARP.
    /// Install read only.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void WonderlandsIndexesAndItsPlanIsCovered()
    {
        var game = new SteamSource().Discover().FirstOrDefault(g => g.Id == "steam:1286680");
        if (game == null || !File.Exists(game.ExePath)) return;
        var reader = new UnrealReader(Ff7.TempDir("wonderlands-data"));
        var engine = reader.Detect(game)!;
        Assert.Equal("4.21", engine.Version);
        Assert.Equal(Readiness.Ready, new Planner().Check(game, engine, null, Ff7.Nvidia).Readiness);
        var index = reader.Index(game, engine, null, CancellationToken.None);
        Assert.True(index.Shaders.Count > 50_000 && index.Maps.Count > 5_000, $"{index.Shaders.Count} shaders, {index.Maps.Count} maps");
        var log = new List<string>();
        var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia with { PerStageCache = true }, Ff7.TempDir("wonderlands-plan"), new SyncLog(log.Add), CancellationToken.None);
        foreach (var l in log) output.WriteLine(l);
        Assert.Contains(log, l => l.Contains("SRV tables of 64"));
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        var sigs = body.Where(r => r.Tag == 'B').ToDictionary(r => PsoDb.Hex(r.Payload.AsSpan(0, 20)), r => UnrealRootSigSweep.Rts0(r.Payload[20..]));
        var psos = body.Where(r => r.Tag is 'S' or 'G' or 'C').Select(PsoDb.Parse).ToList();
        var bad = psos.Count(p => p.Stages.Any(s => UnrealRootSigSweep.Mismatch(index.Shaders[s.Value], (Stage)s.Key, sigs[p.Rs]) != null));
        Assert.True(psos.Count > 100_000, $"only {psos.Count} PSOs");
        Assert.Equal((0, 0L, false), (bad, plan.Stats.Uncovered, plan.Stats.RootSigRuleVerified));
    }

    /// <summary>Tiny Tina's Wonderlands against SCSKiller's recording of it (read, never written): the stock 4.21 rule with
    /// 64-SRV tables rebuilds 18386 of its 18419 recorded root signatures byte for byte; the other 33 differ only in the
    /// input-layout flag, set for a VS that reads no vertex input (247 other such PSOs have it clear): it follows the vertex
    /// declaration bound at the draw, which the shaders don't show, so 4.21 isn't in ConfirmedEngines. Install read only;
    /// returns early without the game or recording.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void WonderlandsRecordingRebuildsByteExact()
    {
        var game = new SteamSource().Discover().FirstOrDefault(g => g.Id == "steam:1286680");
        if (game == null || !File.Exists(game.ExePath)) return;
        var reader = new UnrealReader(Ff7.TempDir("wonderlands-rec-data"));
        var engine = reader.Detect(game)!;
        Assert.Equal((Rules.Ue421, null), (RootSig.RuleFor(engine), engine.Fork));
        if (Ff7.Recording(game, engine, reader, "wonderlands-rec") is not { } db) return;
        var bc = reader.Index(game, engine, null, CancellationToken.None).Shaders;
        var recs = PsoDb.Read(db).ToList();
        var blobs = recs.Where(r => r.Tag == 'B').GroupBy(r => PsoDb.Hex(r.Payload.AsSpan(0, 20))).ToDictionary(g => g.Key, g => g.First().Payload[20..]);
        var maxSrvs = RootSig.MaxSrvsFor(Rules.Ue421, bc.Values);
        var psos = recs.Where(r => r.Tag is 'G' or 'C' or 'S').DistinctBy(r => r.Key).Select(PsoDb.Parse)
            .Where(p => p.Stages.Values.All(bc.ContainsKey) && blobs.ContainsKey(p.Rs)).ToList();
        bool Rebuilds(PsoDb.Pso p, uint flag)
        {
            var desc = RootSig.Build(Rules.Ue421, p.Stages.ToDictionary(s => (Stage)s.Key, s => bc[s.Value]), false, maxSrvs);
            return RootSig.Serialize(desc with { Flags = desc.Flags | flag }, RootSig.StaticSamplers(Rules.Ue421)).AsSpan().SequenceEqual(blobs[p.Rs]);
        }
        var bad = psos.Where(p => !Rebuilds(p, 0)).ToList();
        output.WriteLine($"root signatures rebuilt byte-exact: {psos.Count - bad.Count}/{psos.Count} recorded PSOs (SRV tables of {maxSrvs}); mismatches by stages: "
            + string.Join(", ", bad.GroupBy(p => string.Join('+', p.Stages.Keys.Select(k => (Stage)k))).Select(g => $"{g.Key} {g.Count()}")));
        Assert.True(psos.Count > 1000, $"only {psos.Count} recorded PSOs of index shaders");
        Assert.All(bad, p => Assert.True(Rebuilds(p, 1)));   // ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT
    }

    static readonly EngineInfo SantaMonica = new(SCSKiller.Core.SantaMonica.SantaMonicaReader.Family, SCSKiller.Core.SantaMonica.SantaMonicaReader.Version, null, "D3D12", false, null);

    static (string Sha, byte[] Blob) Root(params uint[][] rows)
    {
        var b = RootSig.Serialize(new RootSig.Desc(1, [.. rows]), []);
        return (PsoDb.Hex(System.Security.Cryptography.SHA1.HashData(b)), b);
    }

    /// <summary>The plan's PSOs (synthesized templates and items) as (root signature, stages).</summary>
    internal static List<(string Rs, string Stages)> Psos(Plan plan)
    {
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        return [.. body.Where(r => r.Tag is 'S' or 'G' or 'C').Select(r => PsoDb.Parse(r)).Select(p => (p.Rs, string.Join(',', p.Stages.Values.Order())))
            .Concat(body.Where(r => r.Tag == 'P').Select(r => PsoDb.ParseItem(r.Payload)).Select(p => (p.Rs, string.Join(',', p.Stages.Values.Order()))))];
    }

    static readonly VendorCaps[] AllPaths = [Ff7.Nvidia, Ff7.Nvidia with { PerStageCache = true }, Ff7.Amd];

    /// <summary>A program names its root signature (God of War Ragnarök's, Dawn's records): its VS and PS, each drawn with
    /// several, carry none, and the program's PSO takes the map's. The same shaders in two programs with two root signatures
    /// are two PSOs; a program whose root signature doesn't cover its VS's b0 (pixel only) is left out as the game's own
    /// ("own_rs_uncovered"), on every path (whole pipelines, NVIDIA and AMD per stage).</summary>
    [Fact]
    public void APipelineMapTakesTheRootSignatureItNames()
    {
        var (a, b, pixelOnly) = (Root([2, 0, 0, 0, 0]), Root([2, 0, 0, 0, 0], [2, 0, 1, 0, 0]), Root([2, 5, 0, 0, 0]));
        var vs = Shader("map-vs", Stage.Vertex, [In("POSITION", 0, 0, 7)], [PosOut, UvOut]) with { Bindings = [new("cbv", 0, 0, 1)] };
        var ps = Shader("map-ps", Stage.Pixel, [PosOut, UvOut], [Target(0)]) with { Bindings = [new("cbv", 0, 0, 1)] };
        var ps2 = ps with { Sha1 = Hash("map-ps2") };
        var index = new ShaderIndex("synthetic", ["D3D12"], new[] { vs, ps, ps2 }.ToDictionary(s => s.Sha1),
            [new ShaderMap("1", "pack", "D3D12", [vs.Sha1, ps.Sha1], true, a.Sha), new ShaderMap("1", "pack", "D3D12", [vs.Sha1, ps.Sha1], true, b.Sha),
             new ShaderMap("2", "pack", "D3D12", [vs.Sha1, ps2.Sha1], true, pixelOnly.Sha)],
            new Dictionary<string, byte[]> { [a.Sha] = a.Blob, [b.Sha] = b.Blob, [pixelOnly.Sha] = pixelOnly.Blob });
        var dir = Ff7.TempDir("map-root");
        var pair = string.Join(',', new[] { vs.Sha1, ps.Sha1 }.Order());
        foreach (var caps in AllPaths)
        {
            var log = new List<string>();
            var plan = new Planner().Build(Ff7.Game, SantaMonica, index, null, caps, Path.Combine(dir, $"{caps.Profile}-{caps.PerStageCache}"), new SyncLog(log.Add), CancellationToken.None);
            output.WriteLine($"{caps.Profile} {caps.PerStageCache}: {log.Last(l => l.StartsWith("plan:"))}");
            Assert.Equal(new[] { (a.Sha, pair), (b.Sha, pair) }.Order(), Psos(plan).Distinct().Order());
            Assert.Contains(log, l => l.StartsWith("plan:") && l.Contains("own_rs_uncovered 1") && !l.Contains("no_rs"));
            Assert.Equal((3L, 1L, 0L), (plan.Stats.StageSets, plan.Stats.LeftOut, plan.Stats.Uncovered));
        }
    }

    /// <summary>Shaders with one root signature, as the reader gives it them, plan the same PSOs whether their maps name it
    /// too or not, on every path; so do pooled maps beside them.</summary>
    [Fact]
    public void AMapNamingItsShadersOwnRootSignatureChangesNothing()
    {
        var a = Root([2, 0, 0, 0, 0]);
        var vs = Shader("same-vs", Stage.Vertex, [In("POSITION", 0, 0, 7)], [PosOut, UvOut]) with { Bindings = [new("cbv", 0, 0, 1)], RootSignature = a.Sha };
        var ps = Shader("same-ps", Stage.Pixel, [PosOut, UvOut], [Target(0)]) with { Bindings = [new("cbv", 0, 0, 1)], RootSignature = a.Sha };
        var cs = Shader("same-cs", Stage.Compute, []) with { Bindings = [new("cbv", 0, 0, 1)], RootSignature = a.Sha };
        var shaders = new[] { vs, ps, cs }.ToDictionary(s => s.Sha1);
        ShaderIndex Index(string? root) => new("synthetic", ["D3D12"], shaders,
            [new ShaderMap("1", "pack", "D3D12", [vs.Sha1, ps.Sha1], true, root), new ShaderMap("2", "pack", "D3D12", [cs.Sha1], true, root),
             new ShaderMap("3", "pool", "D3D12", [vs.Sha1, ps.Sha1, cs.Sha1])],
            new Dictionary<string, byte[]> { [a.Sha] = a.Blob });
        var dir = Ff7.TempDir("map-root-same");
        foreach (var caps in AllPaths)
        {
            var (with, without) = (Index(a.Sha), Index(null));
            Plan Build(ShaderIndex i, string name) => new Planner().Build(Ff7.Game, SantaMonica, i, null, caps, Path.Combine(dir, $"{name}-{caps.Profile}-{caps.PerStageCache}"), null, CancellationToken.None);
            var (p1, p0) = (Build(with, "with"), Build(without, "without"));
            Assert.Equal(PlanFile.Read(p0.FilePath).Records.Select(r => r.Key).Order(), PlanFile.Read(p1.FilePath).Records.Select(r => r.Key).Order());
            Assert.Equal(p0.Stats, p1.Stats);
            Assert.NotEmpty(Psos(p1));
        }
    }

    sealed class SyncLog(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }

    /// <summary>Hogwarts Legacy (a 4.27 fork, no recording) planned recording-free for NVIDIA: every PSO's root signature covers
    /// every resource its shaders declare, visible to their stage (checked by the sweep's own reader, not RootSig's). Before the
    /// guard: 159594 of 200804 failed this (its bindless SRVs in spaces 4-7), 159602 in a real warm. Install read only.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void HogwartsPlanRootSignaturesCoverTheirShaders()
    {
        var game = new SteamSource().Discover().FirstOrDefault(g => g.Id == "steam:990080");
        if (game == null || !File.Exists(game.ExePath)) return;
        var reader = new UnrealReader(Ff7.TempDir("hogwarts-data"));
        var engine = reader.Detect(game)!;
        var index = reader.Index(game, engine, null, CancellationToken.None);
        var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia with { PerStageCache = true }, Ff7.TempDir("hogwarts-plan"), new SyncLog(output.WriteLine), CancellationToken.None);
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        var sigs = body.Where(r => r.Tag == 'B').ToDictionary(r => PsoDb.Hex(r.Payload.AsSpan(0, 20)), r => UnrealRootSigSweep.Rts0(r.Payload[20..]));
        var psos = body.Where(r => r.Tag is 'S' or 'G' or 'C').Select(PsoDb.Parse).Select(p => (p.Rs, p.Stages))
            .Concat(body.Where(r => r.Tag == 'P').Select(r => PsoDb.ParseItem(r.Payload)).Select(i => (i.Rs, i.Stages))).ToList();
        var bad = psos.Select(p => p.Stages.Select(s => UnrealRootSigSweep.Mismatch(index.Shaders[s.Value], (Stage)s.Key, sigs[p.Rs])).FirstOrDefault(m => m != null))
            .OfType<string>().GroupBy(m => m).ToDictionary(g => g.Key, g => g.Count());
        output.WriteLine($"{psos.Count} PSOs; uncovered: {string.Join(", ", bad.Select(b => $"{b.Key} x{b.Value}"))}");
        Assert.True(psos.Count > 10000, $"only {psos.Count} PSOs");
        Assert.Empty(bad);
    }
}
