using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Source2;
using SCSKiller.Tests.Planning;
using ValvePak;

namespace SCSKiller.Tests.Source2;

/// <summary>A Source 2 install built around ValveResourceFormat's own test files (external/ValveResourceFormat): Counter-Strike
/// 2's csgo_black_unlit, vcs 72 (a resource file with a KV3 block), in a shaders_pc_dir.vpk.</summary>
public class Source2ReaderTests
{
    static string Fixture(string name) => Path.Combine(TestEnv.RepoRoot, "external", "ValveResourceFormat", "Tests", "Files", "Shaders", name);

    /// <summary>game\bin\win64\engine2.dll, and game\csgo\shaders_pc_dir.vpk with the files under shaders\vfx\.</summary>
    static Game Install(string name, string? boot, params (string Name, byte[] Data)[] files)
    {
        var dir = Ff7.TempDir(name);
        Directory.CreateDirectory(Path.Combine(dir, "game", "bin", "win64"));
        File.WriteAllBytes(Path.Combine(dir, "game", "bin", "win64", "engine2.dll"), []);
        Directory.CreateDirectory(Path.Combine(dir, "game", "csgo", "cfg"));
        if (boot != null) File.WriteAllText(Path.Combine(dir, "game", "csgo", "cfg", "boot.vcfg"), boot);
        using var pak = new Package();
        foreach (var (n, data) in files) pak.AddFile("shaders/vfx/" + n, data);
        pak.Write(Path.Combine(dir, "game", "csgo", "shaders_pc_dir.vpk"));
        return new Game("test:source2", "Test", Store.Other, dir, Path.Combine(dir, "game", "bin", "win64", "cs2.exe"));
    }

    static (string, byte[]) File72(string name) => (name, File.ReadAllBytes(Fixture("vcs72_" + name)));

    [Fact]
    public void IndexesTheDxbcOfEveryCombo()
    {
        var game = Install("source2-index", null, File72("csgo_black_unlit_pc_50_ps.vcs"), File72("csgo_black_unlit_pc_50_features.vcs"));
        var reader = new Source2Reader();
        var engine = reader.Detect(game)!;
        Assert.Equal(new EngineInfo(Source2Reader.Family, "vcs 72", null, "D3D11", false, null), engine);
        var index = reader.Index(game, engine, null, CancellationToken.None);
        Assert.NotEmpty(index.Shaders);
        Assert.All(index.Shaders.Values, s => Assert.Equal(Stage.Pixel, s.Stage));
        Assert.All(index.Shaders.Values, s => Assert.True(Planner.IsD3D11(s), s.ShaderModel));
        var map = Assert.Single(index.Maps);   // the features file holds no bytecode: one map for the program
        Assert.Equal(Source2Reader.Platform, map.Platform);
        Assert.EndsWith("csgo_black_unlit", map.Library);
        Assert.Equal(index.Shaders.Keys.Order(), map.Shaders.Order());

        var served = new Dictionary<string, byte[]>();
        reader.ReadShaders(game, engine, index.Shaders.Keys.ToHashSet(), (sha, c) => served.Add(sha, c), CancellationToken.None);
        Assert.Equal(index.Shaders.Keys.Order(), served.Keys.Order());
        Assert.All(served, kv => Assert.Equal(kv.Key, Convert.ToHexStringLower(SHA1.HashData(kv.Value))));
        Assert.All(served.Values, c => Assert.True(Dxbc.Valid(c)));
        Assert.Equal(index.ContentHash, reader.Index(game, engine, null, CancellationToken.None).ContentHash);
    }

    [Fact]
    public void BootConfigCanMakeVulkanTheDefault()
    {
        var game = Install("source2-vulkan", "\"boot\"\n{\n\t\"DefaultRenderSystemOption\"\t\t\"-vulkan\"\n}\n", File72("csgo_black_unlit_pc_50_ps.vcs"));
        var engine = new Source2Reader().Detect(game)!;
        Assert.Equal(@"Vulkan (game\csgo\cfg\boot.vcfg)".Replace('\\', Path.DirectorySeparatorChar), engine.GraphicsApi);
        Assert.Equal("D3D11", new Source2Reader().Detect(Install("source2-dx11", "\"boot\"\n{\n\t\"DefaultRenderSystemOption\" \"-dx11\"\n}\n",
            File72("csgo_black_unlit_pc_50_ps.vcs")))!.GraphicsApi);
    }

    [Fact]
    public void NewerShaderFilesAreNamed()
    {
        var data = File.ReadAllBytes(Fixture("vcs72_csgo_black_unlit_pc_50_ps.vcs"));
        data[6] = 73;
        var engine = new Source2Reader().Detect(Install("source2-v73", null, ("csgo_black_unlit_pc_50_ps.vcs", data)))!;
        Assert.Equal("vcs 73", engine.Version);
        Assert.Equal("shader files of vcs version 73: SCSKiller reads up to 72", engine.Unsupported);
    }

    [Fact]
    public void NotSource2WithoutEngine2()
    {
        var dir = Ff7.TempDir("source2-none");
        Assert.Null(new Source2Reader().Detect(new Game("test:none", "Test", Store.Other, dir, Path.Combine(dir, "game.exe"))));
    }
}
