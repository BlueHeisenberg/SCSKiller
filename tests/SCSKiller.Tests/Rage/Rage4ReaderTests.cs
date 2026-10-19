using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Rage;
using SCSKiller.Tests.Planning;

namespace SCSKiller.Tests.Rage;

public class Rage4ReaderTests
{
    [Fact]
    public void DetectsGtaIvDxvkAndTracksItsFxcFiles()
    {
        var install = Ff7.TempDir("rage4-synthetic");
        var dir = Path.Combine(install, "GTAIV");
        var shaders = Path.Combine(dir, "common", "shaders");
        Directory.CreateDirectory(shaders);
        File.WriteAllBytes(Path.Combine(shaders, "a.fxc"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(dir, "vulkan.dll"), [1]);
        File.WriteAllText(Path.Combine(dir, "d3d9.cfg"), "API=1\n");
        var game = new Game(Rage4Reader.SteamId, "Grand Theft Auto IV: The Complete Edition", Store.Steam, install, Path.Combine(dir, "GTAIV.exe"));

        var reader = new Rage4Reader();
        var engine = reader.Detect(game)!;
        Assert.Equal(Rage4Reader.Family, engine.Family);
        Assert.Equal(Rage4Reader.Version, engine.Version);
        Assert.Equal(Rage4Reader.Api, engine.GraphicsApi);
        Assert.Null(engine.Unsupported);
        Assert.Equal(Rage4Reader.Family, ScsKiller.DefaultReaders(Ff7.TempDir("rage4-readers")).Detect(game)!.Family);
        Assert.Equal(Readiness.Ready, new Planner().Check(game, engine, null, Ff7.Nvidia).Readiness);
        Assert.Equal(Readiness.Unsupported, new Planner().Check(game, engine, null, Ff7.Amd).Readiness);

        var stamp = reader.IndexStamp(game);
        File.WriteAllBytes(Path.Combine(shaders, "b.fxc"), [4, 5, 6, 7]);
        Assert.NotEqual(stamp, reader.IndexStamp(game));
        Assert.Null(reader.Detect(game with { Id = "other:12210" }));

        File.WriteAllText(Path.Combine(dir, "d3d9.cfg"), "API=0\n");
        var direct3D = reader.Detect(game)!;
        Assert.Equal("D3D9", direct3D.GraphicsApi);
        Assert.Contains("Vulkan", direct3D.Unsupported);
    }
}
