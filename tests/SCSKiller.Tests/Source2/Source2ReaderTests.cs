using System.Buffers.Binary;
using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Source2;
using SCSKiller.Tests.Dagor;
using SCSKiller.Tests.Planning;
using ValvePak;
using ZstdSharp;

namespace SCSKiller.Tests.Source2;

/// <summary>A synthetic Source 2 install: game\bin\win64 with engine2.dll and a materialsystem2.dll carrying a zstd dictionary,
/// and shaders_pc_dir.vpk packages of version 72 .vcs files (a resource, then bytecode blocks compressed with that dictionary).</summary>
public class Source2ReaderTests
{
    static readonly byte[] Vs = DagorReaderTests.Vs, Ps = DagorReaderTests.Ps, Cs = DagorReaderTests.Cs, Gs = DagorReaderTests.Gs;

    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    /// <summary>A trained dictionary of exactly the 64 KiB the reader takes from the DLL.</summary>
    static readonly byte[] Dict = Train();

    static byte[] Train()
    {
        var rng = new Random(72);
        var samples = Enumerable.Range(0, 4000).Select(i => Enumerable.Range(0, 512).Select(_ => (byte)(rng.Next(24) + (i % 7) * 30)).ToArray()).ToList();
        var dict = DictBuilder.TrainFromBuffer(samples, 1 << 16).ToArray();
        Assert.Equal(1 << 16, dict.Length);
        return dict;
    }

    /// <summary>A version <paramref name="version"/> .vcs: a resource of 64 bytes, then one block per entry of <paramref name="blocks"/>.</summary>
    static byte[] Vcs(byte[][] blocks, int version = 72, int kind = 3)
    {
        var f = new MemoryStream();
        var head = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(head, 64);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(4), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(6), (ushort)version);
        f.Write(head);
        using var c = new Compressor();
        c.LoadDictionary(Dict);
        foreach (var b in blocks)
        {
            var frame = c.Wrap(b).ToArray();
            f.Write(BitConverter.GetBytes(-kind));
            f.Write(BitConverter.GetBytes(b.Length));
            f.Write(BitConverter.GetBytes(frame.Length));
            f.Write(frame);
        }
        return f.ToArray();
    }

    static Game Install(string name, Dictionary<string, Dictionary<string, byte[]>> mods, bool dictionary = true)
    {
        var dir = Ff7.TempDir(name);
        var bin = Directory.CreateDirectory(Path.Combine(dir, "game", "bin", "win64")).FullName;
        File.WriteAllBytes(Path.Combine(bin, "engine2.dll"), [0]);
        File.WriteAllBytes(Path.Combine(bin, "materialsystem2.dll"), [.. new byte[4096], .. dictionary ? Dict : [], .. new byte[999]]);
        foreach (var (mod, files) in mods)
        {
            Directory.CreateDirectory(Path.Combine(dir, "game", mod));
            using var pak = new Package();
            foreach (var (path, bytes) in files) pak.AddFile(path, bytes);
            pak.Write(Path.Combine(dir, "game", mod, "shaders_pc_dir.vpk"));
        }
        return new Game("test:source2", "Test", Store.Other, dir, Path.Combine(bin, "cs2.exe"));
    }

    [Fact]
    public void IndexesTheDxbcOfEveryStaticComboOnePerProgram()
    {
        var game = Install("source2-index", new()
        {
            ["csgo"] = new()
            {
                ["shaders/vfx/water_pc_50_vs.vcs"] = Vcs([Vs]),
                ["shaders/vfx/water_pc_50_ps.vcs"] = Vcs([[.. Ps, .. Gs], Ps]),   // two static combos, the first with two shaders
                ["shaders/vfx/water_pc_50_features.vcs"] = Vcs([]),
            },
            ["core"] = new() { ["shaders/vfx/blur_pc_50_cs.vcs"] = Vcs([Cs]) },
        });
        var reader = new Source2Reader();
        var engine = reader.Detect(game)!;
        Assert.Equal(new EngineInfo(Source2Reader.Family, "72", null, "D3D11", false, null), engine);
        var index = reader.Index(game, engine, null, CancellationToken.None);
        Assert.Equal(new[] { Vs, Ps, Gs, Cs }.Select(Sha).Order(), index.Shaders.Keys.Order());
        Assert.Equal([[Sha(Vs), Sha(Ps), Sha(Gs)], [Sha(Cs)]], index.Maps.Select(m => m.Shaders.ToList()));
        Assert.All(index.Maps, m => Assert.Equal(Source2Reader.Platform, m.Platform));

        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(game, engine, new HashSet<string> { Sha(Gs), Sha(Cs), new('0', 40) }, (h, b) => got.Add(h, b), CancellationToken.None);
        Assert.Equal(Gs, got[Sha(Gs)]);
        Assert.Equal(Cs, got[Sha(Cs)]);
        Assert.Equal(2, got.Count);

        // DirectX 11: no root signatures; NVIDIA warms every stage, AMD the PS and CS
        Assert.Equal(new PlanCheck(Readiness.Ready, "compiles every DirectX 11 shader"), new Planner().Check(game, engine, null, Ff7.Nvidia));
        Assert.Equal(new PlanCheck(Readiness.Ready, $"compiles every DirectX 11 shader but the vertex shaders; {Planner.D3D11VsRecord}"), new Planner().Check(game, engine, null, Ff7.Amd));
    }

    [Fact]
    public void ADictionaryTheGameDoesntCarryFailsTheIndex()
    {
        var game = Install("source2-nodict", new() { ["csgo"] = new() { ["shaders/vfx/water_pc_50_ps.vcs"] = Vcs([Ps]) } }, dictionary: false);
        var reader = new Source2Reader();
        var e = Assert.Throws<InvalidDataException>(() => reader.Index(game, reader.Detect(game)!, null, CancellationToken.None));
        Assert.Contains(BinaryPrimitives.ReadUInt32LittleEndian(Dict.AsSpan(4)).ToString("x8"), e.Message);
    }

    [Fact]
    public void ABlockThatDoesntReadEndsItsFile()
    {
        var cut = Vcs([Vs, Gs]);
        var game = Install("source2-cut", new() { ["csgo"] = new() { ["shaders/vfx/a_pc_50_vs.vcs"] = cut[..^4], ["shaders/vfx/b_pc_50_ps.vcs"] = Vcs([Ps]) } });
        var reader = new Source2Reader();
        var log = new List<string>();
        var engine = reader.Detect(game)!;
        var index = reader.Index(game, engine, new SyncLog(log), CancellationToken.None);
        Assert.Equal(new[] { Vs, Ps }.Select(Sha).Order(), index.Shaders.Keys.Order());
        Assert.Contains(log, l => l.StartsWith("warning: shaders/vfx/a_pc_50_vs.vcs: "));
        var got = new List<string>();
        reader.ReadShaders(game, engine, new HashSet<string>(index.Shaders.Keys), (h, _) => got.Add(h), CancellationToken.None);
        Assert.Equal(index.Shaders.Keys.Order(), got.Order());
    }

    sealed class SyncLog(List<string> lines) : IProgress<string> { public void Report(string value) => lines.Add(value); }

    [Fact]
    public void OtherVersionsAndNonSource2InstallsAreNotRead()
    {
        Assert.Null(new Source2Reader().Detect(new Game("test:none", "None", Store.Other, Ff7.TempDir("source2-none"), "x.exe")));
        var v73 = Install("source2-v73", new() { ["csgo"] = new() { ["shaders/vfx/water_pc_50_ps.vcs"] = Vcs([Ps], version: 73) } });
        Assert.Equal("shader files version 73: SCSKiller reads versions 70 to 72", new Source2Reader().Detect(v73)!.Unsupported);
        var none = Install("source2-novpk", []);
        Assert.Equal("no shaders_pc_dir.vpk", new Source2Reader().Detect(none)!.Unsupported);
    }

    [Theory]
    [InlineData(null, "", "D3D11")]
    [InlineData("\"boot\"\n{\n\t\"DefaultRenderSystemOption\"\t\t\"-vulkan\"\n}", "", "Vulkan (boot.vcfg)")]
    [InlineData("\"boot\"\n{\n\t\"UILanguage\"\t\t\"english\"\n\t\"DefaultRenderSystemOption\"\t\t\"-dx11\"\n}", "", "D3D11 (boot.vcfg)")]
    [InlineData("\"boot\"\n{\n\t\"DefaultRenderSystemOption\"\t\t\"-vulkan\"\n}", "-novid -dx11", "D3D11 (launch option)")]
    [InlineData(null, "-vulkan -novid", "Vulkan (launch option)")]
    public void TheApiComesFromTheLaunchOptionsThenBootVcfg(string? boot, string launch, string api)
    {
        var game = Install("source2-api", new() { ["citadel"] = new() { ["shaders/vfx/water_pc_50_ps.vcs"] = Vcs([Ps]) } });
        if (boot != null) File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(game.InstallDir, "game", "citadel", "cfg")).FullName, "boot.vcfg"), boot);
        Assert.Equal(api, Source2Reader.Api(game, launch));
        if (api.StartsWith("Vulkan"))
            Assert.Equal(new PlanCheck(Readiness.Unsupported, $"runs on {api}"), new Planner().Check(game, new EngineInfo(Source2Reader.Family, "72", null, api, false, null), null, Ff7.Nvidia));
    }
}
