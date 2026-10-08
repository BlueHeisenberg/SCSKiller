using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Tank;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Tank;

/// <summary>Overwatch's Steam install on this machine (read-only; skipped when absent): found before the carver, marked as
/// anti-cheat, its DirectX 11 shaders indexed from every data file and planned on NVIDIA.</summary>
[Trait("Needs", "Game")]
public class TankGameTests(ITestOutputHelper output)
{
    [Fact]
    public void IndexesOverwatchsDirectX11Shaders()
    {
        var dir = TestEnv.GameDir("Overwatch");
        var game = new Game("steam:2357570", "Overwatch", Store.Steam, dir, Path.Combine(dir, "Overwatch.exe"));
        if (!File.Exists(Path.Combine(dir, StaticContainer.Dir, StaticContainer.BuildConfig))) return;
        var engine = new EngineReaders((TankReader.Family, new TankReader()), (CarvedReader.Family, new CarvedReader())).Detect(game)!;
        output.WriteLine($"{engine}; anti-cheat {GameFiles.DetectAntiCheat(game)}");
        Assert.Equal(TankReader.Family, engine.Family);
        Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(game));

        var reader = new TankReader();
        var sw = Stopwatch.StartNew();
        var index = reader.Index(game, engine, new Progress<string>(output.WriteLine), CancellationToken.None);
        output.WriteLine($"index {sw.Elapsed.TotalSeconds:F1}s: {index.Shaders.Count} shaders, {index.Maps.Count} maps");
        Assert.True(index.Shaders.Count > 50_000);
        Assert.All(index.Maps, m => Assert.Equal(TankReader.Platform, m.Platform));
        Assert.All(index.Shaders.Values, s => Assert.True(Planner.IsD3D11(s)));

        var some = index.Shaders.Keys.Take(64).ToHashSet();
        var got = 0;
        reader.ReadShaders(game, engine, some, (_, _) => got++, CancellationToken.None);
        Assert.Equal(some.Count, got);
    }
}
