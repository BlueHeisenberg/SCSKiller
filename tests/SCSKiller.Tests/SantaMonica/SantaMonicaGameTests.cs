using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.SantaMonica;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.SantaMonica;

/// <summary>God of War Ragnarök and God of War (2018) on this machine (read-only; skipped when absent): no anti-cheat, found
/// before the carver. Ragnarök: every program an exact pipeline with the root signature its record names, planned on NVIDIA
/// without a recording. 2018: every DirectX 11 shader once.</summary>
[Trait("Needs", "Game")]
public class SantaMonicaGameTests(ITestOutputHelper output)
{
    [Fact]
    public void PlansGodOfWarRagnarokWithoutARecording()
    {
        // Steam's own listing: the folder name and the exe the app picks, as a scan finds them
        if (new SteamSource().Discover().FirstOrDefault(g => g.Id == "steam:2322010") is not { } game
            || !File.Exists(Path.Combine(game.InstallDir, @"exec\wad\pc_le\root.shaderpack")))
        {
            output.WriteLine("skipped: God of War Ragnarök isn't installed");
            return;
        }
        output.WriteLine($"{game.InstallDir}: exe {game.ExePath}");
        Assert.Equal("GoWR.exe", Path.GetFileName(game.ExePath), ignoreCase: true);
        Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game));
        Assert.False(BootPrecompiles.Embedded.Has(game));
        var sw = Stopwatch.StartNew();
        var engine = SCSKiller.Core.App.ScsKiller.DefaultReaders(Ff7.TempDir("santamonica-readers")).Detect(game)!;   // every reader, in scan order
        output.WriteLine($"detect {sw.Elapsed.TotalSeconds:F1}s: {engine}");
        Assert.Equal(new EngineInfo(SantaMonicaReader.Family, SantaMonicaReader.Version, null, "D3D12", false, null), engine);
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.NoRecording), new Planner().Check(game, engine, null, Ff7.Nvidia));
        Assert.Equal(Readiness.NeedsRecording, new Planner().Check(game, engine, null, Ff7.Amd).Readiness);

        var reader = new SantaMonicaReader();
        var log = new List<string>();
        var index = reader.Index(game, engine, new Log(l => { log.Add(l); output.WriteLine(l); }), CancellationToken.None);
        // the probe's counts: 33,907 shaders and 65 root-signature containers in 25 packs, 56,096 program records
        Assert.True(index.Shaders.Count > 30_000);
        Assert.True(index.Shaders.Values.Select(s => s.RootSignature).OfType<string>().Distinct().Count() > 50);
        Assert.True(index.Maps.Count(m => m.IsPipeline) > 50_000);
        Assert.DoesNotContain(index.Shaders.Values, s => s.Stage is Stage.Library or Stage.Mesh or Stage.Amplification or Stage.Geometry);
        Assert.DoesNotContain(log, l => l.Contains("warning") || l.Contains("left out"));   // every root signature covers its programs, every chain links

        foreach (var (name, caps) in new[] { ("NVIDIA", Ff7.Nvidia), ("NVIDIA per stage", Ff7.Nvidia with { PerStageCache = true }), ("AMD", Ff7.Amd) })
        {
            sw.Restart();
            var plan = new Planner().Build(game, engine, index, null, caps, Ff7.TempDir($"santamonica-plan-{caps.Profile}-{caps.PerStageCache}"), new Log(output.WriteLine), CancellationToken.None);
            output.WriteLine($"plan ({name}) {sw.Elapsed.TotalSeconds:F1}s: {plan.Stats}");
            Assert.True(plan.Stats.Generated > 0);
        }
    }

    [Fact]
    public void IndexesGodOfWar2018AsDirectX11()
    {
        if (new SteamSource().Discover().FirstOrDefault(g => g.Id == "steam:1593500") is not { } game
            || !File.Exists(Path.Combine(game.InstallDir, @"exec\wad\pc_le\root.shaderpack")))
        {
            output.WriteLine("skipped: God of War (2018) isn't installed");
            return;
        }
        output.WriteLine($"{game.InstallDir}: exe {game.ExePath}");
        Assert.Equal("GoW.exe", Path.GetFileName(game.ExePath), ignoreCase: true);
        Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game));
        var sw = Stopwatch.StartNew();
        var engine = SCSKiller.Core.App.ScsKiller.DefaultReaders(Ff7.TempDir("santamonica-gow2018-readers")).Detect(game)!;
        output.WriteLine($"detect {sw.Elapsed.TotalSeconds:F1}s: {engine}");
        Assert.Equal(new EngineInfo(SantaMonicaReader.Family, SantaMonicaReader.Version1, null, "D3D11", false, null), engine);
        foreach (var caps in new[] { Ff7.Nvidia, Ff7.Amd }) output.WriteLine($"{caps.Profile}: {new Planner().Check(game, engine, null, caps)}");

        var log = new List<string>();
        var index = new SantaMonicaReader().Index(game, engine, new Log(l => { log.Add(l); output.WriteLine(l); }), CancellationToken.None);
        // the probe's counts: 25,351 DXBC shaders (24,657 PS, 407 VS, 287 CS) in 19 packs
        Assert.True(index.Shaders.Count > 20_000);
        Assert.All(index.Shaders.Values, s => Assert.True(Planner.IsD3D11(s)));
        Assert.DoesNotContain(log, l => l.Contains("unparseable") || l.Contains("another version"));

        foreach (var (name, caps) in new[] { ("NVIDIA", Ff7.Nvidia), ("AMD", Ff7.Amd) })
        {
            sw.Restart();
            var plan = new Planner().Build(game, engine, index, null, caps, Ff7.TempDir($"santamonica-gow2018-plan-{caps.Profile}"), new Log(output.WriteLine), CancellationToken.None);
            output.WriteLine($"plan ({name}) {sw.Elapsed.TotalSeconds:F1}s: {plan.Stats}");
            Assert.True(plan.Stats.D3D11Shaders > 0);
        }
    }

    sealed class Log(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }
}
