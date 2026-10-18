using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Carved;

/// <summary>Guardians of the Galaxy (Dawn) on this machine (read-only; skipped when absent): its bin\raw*.store2 give every
/// pipeline record its root signature, so the NVIDIA plan needs no recording.</summary>
[Trait("Needs", "Game")]
public class DawnGameTests(ITestOutputHelper output)
{
    [Fact]
    public void PlansGuardiansOfTheGalaxyWithoutARecording()
    {
        var dir = TestEnv.GameDir("Marvel's Guardians of the Galaxy");
        var game = new Game("steam:1088850", "Marvel's Guardians of the Galaxy", Store.Steam, dir, Path.Combine(dir, @"bin\gotg.exe"));
        if (!File.Exists(game.ExePath) || !File.Exists(Path.Combine(dir, @"bin\rawpso.store2")))
        {
            output.WriteLine("skipped: Guardians of the Galaxy isn't installed");
            return;
        }
        var sw = Stopwatch.StartNew();
        var readers = SCSKiller.Core.App.ScsKiller.DefaultReaders(Ff7.TempDir("dawn-game-readers"));
        var engine = readers.Detect(game)!;
        output.WriteLine($"detect {sw.Elapsed.TotalSeconds:F1}s: {engine}");
        Assert.Equal((CarvedReader.Family, "DXIL" + CarvedReader.EmbeddedRootSignatures, "D3D12", (string?)null), (engine.Family, engine.Version, engine.GraphicsApi, engine.Unsupported));

        var index = readers.Index(game, engine, new Log(output.WriteLine), CancellationToken.None);
        var rts = index.Shaders.Values.Count(s => s.RootSignature != null);
        var exact = index.Maps.Count(m => m.IsPipeline);
        output.WriteLine($"index: {index.Shaders.Count} shaders, {rts} with a root signature, {exact} exact maps of {index.Maps.Count}");
        // the probe's counts: 6,337 shaders, 6,333 with a root signature (83 distinct), 12,256 records -> 6,848 exact maps
        Assert.True(index.Shaders.Count > 6_000);
        Assert.True(rts > 6_000);
        Assert.True(exact > 6_500);

        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.NoRecording), new Planner().Check(game, engine, null, Ff7.Nvidia));
        var log = new List<string>();
        sw.Restart();
        var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia, Ff7.TempDir("dawn-game-plan"), new Log(l => { log.Add(l); output.WriteLine(l); }), CancellationToken.None);
        output.WriteLine($"plan {sw.Elapsed.TotalSeconds:F1}s: {plan.Stats}");
        // the probe's plan: 6,848 generated, 83 root signatures, nothing left out
        Assert.True(plan.Stats.Generated > 6_500);
        Assert.Equal(0, plan.Stats.Uncovered);
        Assert.DoesNotContain(log, l => l.Contains("rs_unknown"));
    }

    sealed class Log(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }
}
