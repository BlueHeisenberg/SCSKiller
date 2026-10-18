using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.ForzaTech;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.ForzaTech;

/// <summary>Forza Horizon 6 on this machine (Xbox app or Steam; read-only; skipped when absent): found before the carver,
/// every zip read, raster and compute planned without a recording.</summary>
[Trait("Needs", "Game")]
public class ForzaTechGameTests(ITestOutputHelper output)
{
    [Fact]
    public void PlansForzaHorizon6WithoutARecording()
    {
        var dir = TestEnv.GameDir("Forza Horizon 6");   // the Xbox app's folder, else Steam's
        if (!Directory.Exists(dir)) dir = TestEnv.GameDir("ForzaHorizon6");
        if (Directory.Exists(Path.Combine(dir, "Content"))) dir = Path.Combine(dir, "Content");
        var game = new Game("xbox:Microsoft.ForteBaseGame_8wekyb3d8bbwe", "Forza Horizon 6", Store.Xbox, dir, Path.Combine(dir, "forzahorizon6.exe"));
        if (!File.Exists(Path.Combine(dir, @"media\ShadersDXIL.zip"))) return;
        var reader = new ForzaTechReader();
        var sw = Stopwatch.StartNew();
        var engine = new EngineReaders((ForzaTechReader.Family, reader), (CarvedReader.Family, new CarvedReader())).Detect(game)!;
        output.WriteLine($"detect {sw.Elapsed.TotalSeconds:F1}s: {engine}");
        Assert.Equal(new EngineInfo(ForzaTechReader.Family, "pcdxil+RTS0", null, "D3D12", false, null), engine);

        var index = reader.Index(game, engine, new Progress<string>(output.WriteLine), CancellationToken.None);
        // measured on build 3.461.691: 32,528 raster and compute shaders with RTS0, 3,150 ray tracing libraries
        Assert.True(index.Shaders.Values.Count(s => s.Stage != Stage.Library && s.RootSignature != null) > 32_000);
        Assert.True(index.Shaders.Values.Count(s => s.Stage == Stage.Library) > 3_000);
        Assert.True(index.Maps.Count(m => m.IsPipeline && m.Shaders.Count == 2) > 13_000);

        sw.Restart();
        var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia, Ff7.TempDir("forzatech-fh6"), new Progress<string>(output.WriteLine), CancellationToken.None);
        output.WriteLine($"plan {sw.Elapsed.TotalSeconds:F1}s: {plan.Stats}");
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.NoRecording), new Planner().Check(game, engine, null, Ff7.Nvidia));
    }
}
