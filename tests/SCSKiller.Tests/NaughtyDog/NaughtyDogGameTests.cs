using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.NaughtyDog;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.NaughtyDog;

/// <summary>The Last of Us Part II Remastered on this machine (Steam; read-only; skipped when absent): found by the default
/// readers, every shader read from its archives, planned without a recording on NVIDIA with every pixel and compute shader's
/// own root signature.</summary>
[Trait("Needs", "Game")]
[Collection(TimingCollection.Name)]
public class NaughtyDogGameTests(ITestOutputHelper output)
{
    [Fact]
    public void PlansTheLastOfUsPartIIWithoutARecording()
    {
        var dir = TestEnv.GameDir("The Last of Us Part II");
        var game = new Game("steam:2531310", "The Last of Us Part II Remastered", Store.Steam, dir, Path.Combine(dir, "tlou-ii.exe"));
        if (!Directory.Exists(Path.Combine(dir, "build"))) return;
        var sw = Stopwatch.StartNew();
        var engine = ScsKiller.DefaultReaders().Detect(game)!;
        output.WriteLine($"detect {sw.Elapsed.TotalSeconds:F1}s: {engine}; anti-cheat {GameFiles.DetectAntiCheat(game)}");
        Assert.Equal(new EngineInfo(NaughtyDogReader.Family, NaughtyDogReader.Version, null, "D3D12", false, null), engine);
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.NoRecording), new Planner().Check(game, engine, null, Ff7.Nvidia));

        var reader = new NaughtyDogReader();
        sw.Restart();
        var index = reader.Index(game, engine, new Progress<string>(output.WriteLine), CancellationToken.None);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        output.WriteLine($"index {sw.Elapsed.TotalSeconds:F1}s, peak working set {Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MB, heap after {GC.GetTotalMemory(true) >> 20} MB");
        Assert.True(index.Shaders.Count > 100_000);
        // two of the engine's own pixel shaders ship without one (Ps_CopyRefraction and GS_ExpandLineToQuad's)
        Assert.True(index.Shaders.Values.Count(s => s.Stage is Stage.Pixel or Stage.Compute && s.RootSignature == null) <= 2);

        // a sample of every stage and root signature, read back from the archives
        var sample = index.Shaders.Values.GroupBy(s => s.Stage).SelectMany(g => g.Take(200)).SelectMany(s => new[] { s.Sha1, s.RootSignature }).OfType<string>().ToHashSet();
        var got = new Dictionary<string, byte[]>();
        sw.Restart();
        reader.ReadShaders(game, engine, sample, (h, b) => got.Add(h, b), CancellationToken.None);
        output.WriteLine($"read {got.Count} of {sample.Count} sampled containers back in {sw.Elapsed.TotalSeconds:F1}s");
        Assert.Equal(sample.Count, got.Count);

        foreach (var maximum in new[] { false, true })
        {
            sw.Restart();
            var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia with { PerStageCache = true }, Ff7.TempDir($"naughtydog-tlou2-{maximum}"),
                new Progress<string>(output.WriteLine), CancellationToken.None, maximum);
            output.WriteLine($"plan (maximum {maximum}) {sw.Elapsed.TotalSeconds:F1}s, {new FileInfo(plan.FilePath).Length >> 10} KiB: {plan.Stats}");
            Assert.Equal(0, plan.Stats.Uncovered);
        }
    }
}
