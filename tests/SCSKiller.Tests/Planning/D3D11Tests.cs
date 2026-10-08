using System.Diagnostics;
using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Planning;

/// <summary>D3D11 plans (NVIDIA: the driver caches per shader, keyed on the exe name): every shader of the game's SM5
/// platform once, as '1' items (u32 stage + sha1 of a 'B' blob in gen.db). A game that may run on either API gets both.
/// AMD: the pixel and compute shaders only (Planner.D3D11Warms).</summary>
public class D3D11Tests(ITestOutputHelper output)
{
    static readonly EngineInfo Ue426 = new("Unreal", "4.26", null, "D3D11", false, null);
    const string All11 = "compiles every DirectX 11 shader", Amd11 = "compiles every DirectX 11 pixel and compute shader";

    [Fact]
    public void CheckCompilesDirectX11OnNvidia()
    {
        var p = new Planner();
        PlanCheck C(string api, string version = "4.26", VendorCaps? caps = null) =>
            p.Check(Ff7.Game, Ue426 with { GraphicsApi = api, Version = version }, null, caps ?? Ff7.Nvidia);
        Assert.Equal(new PlanCheck(Readiness.Ready, All11), C("D3D11"));
        Assert.Equal(new PlanCheck(Readiness.Ready, All11 + " (user setting)"), C("D3D11 (user setting)"));
        Assert.Equal(new PlanCheck(Readiness.Ready, All11), C("D3D11", "5.1")); // no root signatures: any engine version
        const string FromSource = Planner.Untested; // RootSig.RuleFor, RootSig.Verified
        Assert.Equal(new PlanCheck(Readiness.Ready, FromSource), C("D3D12", "4.25"));
        Assert.Equal(new PlanCheck(Readiness.Ready, "no recording needed"), C("D3D12")); // 4.26: confirmed by two forks' recordings
        Assert.Equal(new PlanCheck(Readiness.Ready, FromSource), C("D3D12", "5.8")); // newer than any rule: the newest, not tested
        Assert.Equal(new PlanCheck(Readiness.Unsupported, "runs on Vulkan"), C("Vulkan"));
        Assert.Equal(new PlanCheck(Readiness.Ready, $"{FromSource}; also {All11} (the game may run on either)"), C(UnrealRhi.Ambiguous, "4.25"));
        Assert.Equal(new PlanCheck(Readiness.Ready, $"{All11} (the game may run on either); for DirectX 12, {Planner.Record}"),
            C(UnrealRhi.Ambiguous, "4.19"));
        // AMD compiles a VS with its input layout: its pixel and compute shaders only
        Assert.Equal(new PlanCheck(Readiness.Ready, Amd11), C("D3D11", caps: Ff7.Amd));
        Assert.StartsWith($"{Amd11} (the game may run on either)", C(UnrealRhi.Ambiguous, "4.19", Ff7.Amd).Reason);
        // a vendor whose D3D11 cache isn't measured
        var other = Ff7.Nvidia with { Profile = "unmeasured" };
        Assert.Equal(new PlanCheck(Readiness.Unsupported, "runs on DirectX 11"), C("D3D11", caps: other));
        Assert.Equal(new PlanCheck(Readiness.Ready, $"{FromSource}; helps only when played on DirectX 12 (the game may run on DirectX 11)"), C(UnrealRhi.Ambiguous, "4.25", other));
    }

    static readonly SigElement Pos = new("SV_Position", 0, 0, 0xF, 1, 3), Uv = new("TEXCOORD", 0, 1, 0x3, 0, 3), Target = new("SV_Target", 0, 0, 0xF, 64, 3);

    static ShaderInfo S(string name, Stage stage, string model, IReadOnlyList<SigElement>? inputs = null, IReadOnlyList<SigElement>? outputs = null) =>
        new(Convert.ToHexStringLower(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(name))), stage, model, 64, new(1, 1, 0, 1), [], inputs ?? [], outputs ?? []);

    /// <summary>A UE 4.26 game with SM6 (DX12) and SM5 (DX11 and DX12) libraries. Ambiguous: SM6 PSOs + every SM5 shader
    /// as a D3D11 item, once (the HS and DS as one '2' pair); D3D11: the items only; D3D12: the PSOs only.</summary>
    [Fact]
    public void AmbiguousGamePlansBothKinds()
    {
        ShaderInfo[] sm6 = [S("vs6", Stage.Vertex, "vs_6_6", [], [Pos, Uv]), S("ps6", Stage.Pixel, "ps_6_6", [Pos, Uv], [Target]), S("cs6", Stage.Compute, "cs_6_6")];
        ShaderInfo[] sm5 = [S("vs5", Stage.Vertex, "vs_5_0", [], [Pos, Uv]), S("ps5", Stage.Pixel, "ps_5_0", [Pos, Uv], [Target]), S("gs5", Stage.Geometry, "gs_5_0"),
            S("hs5", Stage.Hull, "hs_5_0"), S("ds5", Stage.Domain, "ds_5_0"), S("cs5", Stage.Compute, "cs_5_0")];
        var index = new ShaderIndex("synthetic", ["PCD3D_SM6", "PCD3D_SM5"], sm6.Concat(sm5).ToDictionary(s => s.Sha1),
        [
            new("m6", "Global", "PCD3D_SM6", sm6.Select(s => s.Sha1).ToList()),
            new("m5a", "Global", "PCD3D_SM5", sm5.Select(s => s.Sha1).ToList()),
            new("m5b", "Game", "PCD3D_SM5", [sm5[1].Sha1]), // shared across maps: still once
        ]);
        var dir = Ff7.TempDir("d3d11-both");
        (List<PsoDb.Rec> Body, Plan Plan) Build(string api)
        {
            var plan = new Planner().Build(Ff7.Game, Ue426 with { GraphicsApi = api }, index, null, Ff7.Nvidia, Path.Combine(dir, api.Replace(' ', '_')), new Progress<string>(output.WriteLine), CancellationToken.None);
            return (PlanFile.Read(plan.FilePath).Records.ToList(), plan);
        }
        static HashSet<string> Psos(List<PsoDb.Rec> body) => body.Where(r => r.Tag is 'S' or 'P').SelectMany(r => r.Tag == 'P' ? PsoDb.ParseItem(r.Payload).Stages.Values : PsoDb.Parse(r).Stages.Values).ToHashSet();
        static List<(Stage, string)> Items(List<PsoDb.Rec> body) => body.Where(r => r.Tag == '1').Select(r => ((Stage)BitConverter.ToUInt32(r.Payload), PsoDb.Hex(r.Payload.AsSpan(4, 20))))
            .Concat(body.Where(r => r.Tag == '2').SelectMany(r => new[] { (Stage.Hull, PsoDb.Hex(r.Payload.AsSpan(0, 20))), (Stage.Domain, PsoDb.Hex(r.Payload.AsSpan(20, 20))) })).ToList();
        var expected = sm5.Where(s => s.Stage is not (Stage.Hull or Stage.Domain)).Concat(sm5.Where(s => s.Stage is Stage.Hull or Stage.Domain)).Select(s => (s.Stage, s.Sha1)).ToList(); // HS + DS: one '2' pair

        var (both, plan) = Build(UnrealRhi.Ambiguous);
        Assert.Equal(sm6.Select(s => s.Sha1).ToHashSet(), Psos(both)); // DX12: the SM6 library
        Assert.Equal(expected, Items(both));
        Assert.Equal(((long)both.Count(r => r.Tag is 'S' or 'P'), (long)sm5.Length - 1), (plan.Stats.Generated, plan.Stats.D3D11Shaders)); // D3D11 items ('1' + '2') counted on their own
        Assert.Equal("PCD3D_SM6 + D3D11 PCD3D_SM5", plan.Platform);

        var (dx11, plan11) = Build("D3D11");
        Assert.Equal(expected, Items(dx11));
        Assert.All(dx11, r => Assert.Contains(r.Tag, "12"));
        Assert.Equal("D3D11 PCD3D_SM5", plan11.Platform);
        Assert.Empty(Items(Build("D3D12").Body));

        // AMD: the pixel and compute shaders, no VS, GS or HS+DS pair (they need the game's layouts or draw behind a generated VS)
        var amd = new Planner().Build(Ff7.Game, Ue426, index, null, Ff7.Amd, Path.Combine(dir, "amd"), new Progress<string>(output.WriteLine), CancellationToken.None);
        Assert.Equal(sm5.Where(s => s.Stage is Stage.Pixel or Stage.Compute).Select(s => (s.Stage, s.Sha1)), Items(PlanFile.Read(amd.FilePath).Records.ToList()));
        Assert.Equal(2, amd.Stats.D3D11Shaders);

        // materialized: each D3D11 item's blob in gen.db, pulled through the reader
        var work = Path.Combine(dir, "work");
        new Planner().Materialize(plan, Ff7.Game, Ue426, new FakeBytes(), null, work, CancellationToken.None);
        Assert.Equal("b4ea484f4f1015b7db42e1d51bbadbe95d85f1e6744d87a73d82b883cf3f7013", MaterializeOutputTests.Digest(work));
        Ff7.CheckWarmReady(work);
        var gen = PsoDb.Read(Path.Combine(work, "scskiller_gen.db")).ToList();
        Assert.Equal(sm5.Select(s => s.Sha1).Order(), gen.Where(r => r.Tag == 'B').Select(r => PsoDb.Hex(r.Payload.AsSpan(0, 20))).Where(sm5.Select(s => s.Sha1).Contains).Order());
        Assert.Equal(Items(both), Items(gen));
    }

    sealed class FakeBytes : IEngineReader
    {
        public EngineInfo? Detect(Game game) => null;
        public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => throw new NotSupportedException();
        public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
        {
            foreach (var h in sha1s) sink(h, Convert.FromHexString(h));
        }
    }

    /// <summary>This machine's installs (skipped when absent): Ready on NVIDIA, a plan of every shader of the SM5 platform once,
    /// and a sample materialized through the real reader, byte-exact.</summary>
    [Trait("Needs", "Game")]
    [Theory]
    [InlineData("Orcs Must Die! 3", "D3D11")]                  // UE 4.26, shader libraries
    [InlineData("Life is Strange Remastered", "D3D11")]        // UE 4.23, shaders inside the packages
    [InlineData("Palworld", UnrealRhi.Ambiguous)]              // UE 5.1, SM5 + SM6
    [InlineData("Dying Light", "D3D11")]                       // carver: raw DXBC containers
    public void InstalledGamePlansItsSm5Shaders(string name, string api)
    {
        Game? game;
        try { game = new SteamSource().Discover().FirstOrDefault(g => g.Name == name); }
        catch (Exception) { return; } // no Steam
        if (game == null) return;
        var data = Ff7.TempDir("d3d11-" + name.Split(' ')[0]);
        var reader = new EngineReaders(("Unreal", new UnrealReader(data)), (CarvedReader.Family, new CarvedReader())); // the app's chain
        var e = reader.Detect(game)!;
        var check = new Planner().Check(game, e, null, Ff7.Nvidia);
        output.WriteLine($"{name}: {e}; {check}");
        Assert.Equal(api, e.GraphicsApi);
        Assert.Equal(Readiness.Ready, check.Readiness);
        Assert.Contains(All11, check.Reason);

        var sw = Stopwatch.StartNew();
        var index = reader.Index(game, e, null, CancellationToken.None);
        var sm5 = index.Maps.Where(m => m.Platform == "PCD3D_SM5" || !index.Platforms.Contains("PCD3D_SM5")).SelectMany(m => m.Shaders).ToHashSet(); // the carver: one platform
        output.WriteLine($"index: {index.Shaders.Count} shaders ({string.Join(", ", index.Platforms)}), {sm5.Count} SM5, {sw.Elapsed.TotalSeconds:F0}s");
        var plan = new Planner().Build(game, e, index, null, Ff7.Nvidia, Path.Combine(data, "plan"), new Progress<string>(output.WriteLine), CancellationToken.None);
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        var items = body.Where(r => r.Tag == '1').Select(r => (Stage: (Stage)BitConverter.ToUInt32(r.Payload), Sha: PsoDb.Hex(r.Payload.AsSpan(4, 20)))).ToList();
        var pairs = body.Where(r => r.Tag == '2').Select(r => (Hs: PsoDb.Hex(r.Payload.AsSpan(0, 20)), Ds: PsoDb.Hex(r.Payload.AsSpan(20, 20)))).ToList(); // tessellation
        var planned = items.Select(i => i.Sha).Concat(pairs.SelectMany(p => new[] { p.Hs, p.Ds })).ToHashSet();
        output.WriteLine($"plan: {plan.Stats}, {plan.Platform}, {new FileInfo(plan.FilePath).Length / 1024} KiB, {pairs.Count} HS+DS pairs; not planned: "
            + string.Join(", ", sm5.Except(planned).GroupBy(h => $"{index.Shaders[h].Stage} {index.Shaders[h].ShaderModel}").Select(g => $"{g.Count()} {g.Key}")));
        Assert.Equal(items.Count, items.Select(i => i.Sha).Distinct().Count());
        Assert.All(planned, h => Assert.Contains(h, sm5));
        // the rest of the SM5 platform is DXIL, D3D12 only (UE puts DXR libraries and some SM 6.0 compute shaders there)
        Assert.All(sm5.Except(planned), h => Assert.Contains("_6_", index.Shaders[h].ShaderModel));
        Assert.True(planned.Count > 0.99 * sm5.Count);
        Assert.All(items, i => Assert.Equal(index.Shaders[i.Sha].Stage, i.Stage));
        Assert.All(pairs, p => Assert.Equal((Stage.Hull, Stage.Domain), (index.Shaders[p.Hs].Stage, index.Shaders[p.Ds].Stage)));
        var dx12 = body.Count(r => r.Tag is 'S' or 'P' or 'Y'); // may run on either (Palworld): DX12 PSOs too, from Unreal 5.1's rules, and its ray tracing collections
        Assert.Equal(api.Contains("D3D12"), dx12 > 0);
        Assert.Equal(((long)dx12, (long)(items.Count + pairs.Count), false), (plan.Stats.Generated, plan.Stats.D3D11Shaders, plan.Stats.RootSigRuleVerified));

        // a sample through the game's own files: every stage and some HS+DS pairs, byte-exact
        var sample = items.GroupBy(i => i.Stage).SelectMany(g => g.Take(50)).Select(i => i.Sha).ToHashSet();
        var samplePairs = pairs.Take(20).ToHashSet();
        var subset = plan with { FilePath = Path.Combine(data, "sample.bin") };
        PlanFile.Write(subset, body.Where(r => r.Tag == '1' && sample.Contains(PsoDb.Hex(r.Payload.AsSpan(4, 20)))
            || r.Tag == '2' && samplePairs.Contains((PsoDb.Hex(r.Payload.AsSpan(0, 20)), PsoDb.Hex(r.Payload.AsSpan(20, 20))))));
        var singles = sample.Count;
        sample.UnionWith(samplePairs.SelectMany(p => new[] { p.Hs, p.Ds }));
        var work = Path.Combine(data, "work");
        new Planner().Materialize(subset, game, e, reader, null, work, CancellationToken.None);
        Ff7.CheckWarmReady(work);
        var gen = PsoDb.Read(Path.Combine(work, "scskiller_gen.db")).ToList();
        var blobs = gen.Where(r => r.Tag == 'B').ToList();
        Assert.Equal(sample.Count, blobs.Count);
        Assert.All(blobs, b => Assert.Equal(PsoDb.Hex(b.Payload.AsSpan(0, 20)), Convert.ToHexStringLower(SHA1.HashData(b.Payload.AsSpan(20)))));
        Assert.Equal((singles, samplePairs.Count), (gen.Count(r => r.Tag == '1'), gen.Count(r => r.Tag == '2')));
    }
}
