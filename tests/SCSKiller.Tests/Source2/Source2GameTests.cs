using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Source2;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Source2;

/// <summary>Counter-Strike 2 and Deadlock on this machine (Steam; read-only; skipped when absent): found before the carver,
/// their shader files readable, every DirectX VFX program indexed.</summary>
[Trait("Needs", "Game")]
public class Source2GameTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Counter-Strike Global Offensive", "steam:730", "cs2.exe", 100_000)]
    [InlineData("Deadlock", "steam:1422450", "deadlock.exe", 10_000)]
    public void IndexesTheGame(string folder, string id, string exe, int atLeast)
    {
        var dir = TestEnv.GameDir(folder);
        if (!File.Exists(Path.Combine(dir, @"game\bin\win64\engine2.dll"))) return;
        var game = new Game(id, folder, Store.Steam, dir, Path.Combine(dir, @"game\bin\win64", exe));
        var reader = new Source2Reader();
        var engine = new EngineReaders((Source2Reader.Family, reader), (CarvedReader.Family, new CarvedReader())).Detect(game)!;
        output.WriteLine(engine.ToString());
        Assert.Equal(Source2Reader.Family, engine.Family);
        Assert.Null(engine.Unsupported);
        if (engine.GraphicsApi == "D3D11")
            Assert.Equal(Readiness.Ready, new Planner().Check(game, engine, null, Ff7.Nvidia).Readiness);
        var sw = Stopwatch.StartNew();
        var index = reader.Index(game, engine, new Progress<string>(output.WriteLine), CancellationToken.None);
        output.WriteLine($"index {sw.Elapsed.TotalSeconds:F1}s");
        Assert.True(index.Shaders.Count >= atLeast, $"{index.Shaders.Count} shaders");
        Assert.Contains(index.Shaders.Values, s => s.Stage == Stage.Vertex);
        Assert.Contains(index.Shaders.Values, s => s.Stage == Stage.Pixel);
        Assert.All(index.Maps, m => Assert.NotEmpty(m.Shaders));
    }
}
