using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Tank;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Tank;

/// <summary>Overwatch on this machine (read-only; skipped when absent). Steam's install: found before the carver, marked
/// as anti-cheat, its DirectX 11 shaders indexed from every data file and planned. Battle.net's (CASC, not read): marked as
/// anti-cheat, not taken for a static container.</summary>
[Trait("Needs", "Game")]
public class TankGameTests(ITestOutputHelper output)
{
    [Fact]
    public void IndexesOverwatchsDirectX11ShadersOnSteam()
    {
        if (new SteamSource().Discover().FirstOrDefault(g => g.Id == "steam:2357570") is not { } game
            || !File.Exists(Path.Combine(game.InstallDir, StaticContainer.Dir, StaticContainer.BuildConfig)))
        {
            output.WriteLine("skipped: Overwatch isn't installed from Steam");
            return;
        }
        output.WriteLine($"{game.InstallDir}: exe {game.ExePath}");
        Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(game));
        var engine = SCSKiller.Core.App.ScsKiller.DefaultReaders(Ff7.TempDir("tank-readers")).Detect(game)!;   // every reader, in scan order
        output.WriteLine(engine.ToString());
        Assert.Equal(TankReader.Family, engine.Family);
        foreach (var (name, caps) in new[] { ("NVIDIA", Ff7.Nvidia), ("AMD", Ff7.Amd) })
            output.WriteLine($"check ({name}): {new Planner().Check(game, engine, null, caps)}");

        var reader = new TankReader();
        var sw = Stopwatch.StartNew();
        var log = new List<string>();
        var index = reader.Index(game, engine, new Log(l => { log.Add(l); output.WriteLine(l); }), CancellationToken.None);
        output.WriteLine($"index {sw.Elapsed.TotalSeconds:F1}s: {index.Shaders.Count} shaders, {index.Maps.Count} maps");
        Assert.True(index.Shaders.Count > 50_000);
        Assert.DoesNotContain(log, l => l.Contains("warning"));
        Assert.All(index.Maps, m => Assert.Equal(TankReader.Platform, m.Platform));
        Assert.All(index.Shaders.Values, s => Assert.True(Planner.IsD3D11(s)));

        var some = index.Shaders.Keys.Take(64).ToHashSet();
        var got = 0;
        reader.ReadShaders(game, engine, some, (_, _) => got++, CancellationToken.None);
        Assert.Equal(some.Count, got);
    }

    [Fact]
    public void OverwatchFromBattleNetIsAntiCheatAndNotAStaticContainer()
    {
        if (new BattleNetSource().Discover().FirstOrDefault(g => g.Name.Contains("Overwatch", StringComparison.OrdinalIgnoreCase)) is not { } game)
        {
            output.WriteLine("skipped: Overwatch isn't installed from Battle.net");
            return;
        }
        output.WriteLine($"{game.Id} {game.InstallDir}: exe {game.ExePath}");
        Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(game));
        Assert.Null(new TankReader().Detect(game));
    }

    sealed class Log(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }
}
