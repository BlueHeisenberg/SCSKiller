using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Source2;
using SCSKiller.Tests.Planning;

namespace SCSKiller.Tests.Source2;

/// <summary>The Source 2 reader's choice of API (launch options, boot.vcfg) and its detection on a synthetic install.</summary>
public class Source2ReaderTests
{
    const string VulkanBoot = "\"boot\"\n{\n\t\"DefaultRenderSystemOption\"\t\t\"-vulkan\"\n}\n";

    [Theory]
    [InlineData("", "", "D3D11")]
    [InlineData("-novid -vulkan", "", "Vulkan (launch options)")]
    [InlineData("", VulkanBoot, "Vulkan (boot.vcfg)")]
    [InlineData("-dx11", VulkanBoot, "D3D11")]              // the launch options win
    [InlineData("-vulkan -dx11", "", "D3D11")]              // the last one named
    [InlineData("+exec vulkan.cfg", "", "D3D11")]
    [InlineData("", "\"boot\"\n{\n//\t\"DefaultRenderSystemOption\" \"-vulkan\"\n}\n", "D3D11")]
    [InlineData("", "\"boot\"\n{\n}\n", "D3D11")]
    public void PicksTheApi(string launchOptions, string bootVcfg, string api) =>
        Assert.Equal(api, Source2Reader.Api(launchOptions, [bootVcfg]));

    [Fact]
    public void DetectsTheEngineAndItsApi()
    {
        var dir = Ff7.TempDir("source2-synthetic");
        var game = new Game("test:source2", "source2", Store.Other, dir, Path.Combine(dir, @"game\bin\win64\deadlock.exe"));
        var reader = new Source2Reader();
        Assert.Null(reader.Detect(game));

        Directory.CreateDirectory(Path.Combine(dir, @"game\bin\win64"));
        File.WriteAllBytes(Path.Combine(dir, @"game\bin\win64\engine2.dll"), []);
        Assert.Equal(new EngineInfo(Source2Reader.Family, "-", null, "D3D11", false, "no shaders_pc_dir.vpk in the install"), reader.Detect(game));
        var stamp = reader.DetectStamp(game, null);

        Directory.CreateDirectory(Path.Combine(dir, @"game\citadel\cfg"));
        File.WriteAllText(Path.Combine(dir, @"game\citadel\cfg\boot.vcfg"), VulkanBoot);
        var engine = reader.Detect(game)!;
        Assert.Equal("Vulkan (boot.vcfg)", engine.GraphicsApi);
        Assert.NotEqual(stamp, reader.DetectStamp(game, null));
        Assert.Equal(new PlanCheck(Readiness.Unsupported, "runs on Vulkan (boot.vcfg)"), new Planner().Check(game, engine with { Unsupported = null }, null, Ff7.Nvidia));
        Assert.Equal(new PlanCheck(Readiness.Ready, "compiles every DirectX 11 shader"),
            new Planner().Check(game, engine with { Unsupported = null, GraphicsApi = "D3D11" }, null, Ff7.Nvidia));
    }
}
