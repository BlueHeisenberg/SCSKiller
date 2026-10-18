using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Luminous;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Luminous;

/// <summary>FINAL FANTASY XV WINDOWS EDITION on this machine (Steam; read-only; skipped when absent): found before the carver,
/// every archive and binary read, planned as DirectX 11 shaders without a recording.</summary>
[Trait("Needs", "Game")]
public class LuminousGameTests(ITestOutputHelper output)
{
    [Fact]
    public void PlansFinalFantasyXvWithoutARecording()
    {
        var dir = TestEnv.GameDir("FINAL FANTASY XV");
        var game = new Game("steam:637650", "FINAL FANTASY XV WINDOWS EDITION", Store.Steam, dir, Path.Combine(dir, LuminousReader.Exe));
        if (!File.Exists(game.ExePath)) return;
        var reader = new LuminousReader();
        var sw = Stopwatch.StartNew();
        var engine = new EngineReaders((LuminousReader.Family, reader), (CarvedReader.Family, new CarvedReader())).Detect(game)!;
        output.WriteLine($"detect {sw.Elapsed.TotalSeconds:F1}s: {engine}");
        Assert.Equal(new EngineInfo(LuminousReader.Family, LuminousReader.Version, null, "D3D11", false, null), engine);
        Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game));

        var index = reader.Index(game, engine, new Progress<string>(output.WriteLine), CancellationToken.None);
        // measured: 40,663 distinct in the archives (vertex and pixel only), compute and the other stages in the exe and DLLs
        Assert.True(index.Shaders.Count > 40_663, $"{index.Shaders.Count} shaders");
        Assert.All(index.Shaders.Values, s => Assert.True(Planner.IsD3D11(s), $"{s.Sha1} {s.ShaderModel}"));
        Assert.True(index.Shaders.Values.Count(s => s.Stage == Stage.Compute) > 400);

        sw.Restart();
        var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia, Ff7.TempDir("luminous-ffxv"), new Progress<string>(output.WriteLine), CancellationToken.None);
        output.WriteLine($"plan {sw.Elapsed.TotalSeconds:F1}s: {plan.Stats}");
        Assert.True(plan.Stats.D3D11Shaders > 40_663);
        Assert.Equal(new PlanCheck(Readiness.Ready, "compiles every DirectX 11 shader"), new Planner().Check(game, engine, null, Ff7.Nvidia));
    }
}
