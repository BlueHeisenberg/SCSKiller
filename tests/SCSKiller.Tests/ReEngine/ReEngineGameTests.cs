using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.ReEngine;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.ReEngine;

/// <summary>The RE Engine reader on every RE Engine install under TestEnv.SteamCommon (a folder with re_chunk_000.pak;
/// read-only; nothing when there is none): detect, anti-cheat, index, readiness, a sample of shaders served back, and an
/// offline NVIDIA plan (per-stage caps, as NvidiaBackend) into a temp folder (no warm, no recording). FF7 Rebirth's folder
/// is never touched (a game in active play). No table-key modulus is shipped and PRAGMATA's exe doesn't carry one in the
/// clear: an encrypted install downloads it (ree-pak-rs's, pinned) into a fresh data folder every run.</summary>
[Trait("Needs", "Game")]
public class ReEngineGameTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Installs() =>
        TestEnv.SteamCommon
            .SelectMany(Directory.EnumerateDirectories).Where(d => !d.EndsWith("FINAL FANTASY VII REBIRTH") && File.Exists(Path.Combine(d, "re_chunk_000.pak")))
            .Order().Select(d => new object[] { d }).DefaultIfEmpty([""]);

    [Theory]
    [MemberData(nameof(Installs))]
    public void DetectsAndIndexesInstalledGame(string installDir)
    {
        if (installDir == "") return;
        var key = Path.GetFileName(installDir);
        var game = new Game("steam:" + key, key, Store.Steam, installDir, GameFiles.FindExe(installDir)!);
        var data = Ff7.TempDir($"reengine-data-{key}");
        var reader = new ReEngineReader(data);
        var sw = Stopwatch.StartNew();
        var engine = reader.Detect(game) ?? throw new Xunit.Sdk.XunitException($"{game.InstallDir}: not detected as RE Engine");
        var detect = sw.Elapsed.TotalSeconds;
        var check = new Planner().Check(game, engine, null, Ff7.Nvidia);
        output.WriteLine($"{game.Name}: detect {detect:F2}s: {engine}; anti-cheat {GameFiles.DetectAntiCheat(game)}; check: {check}");
        if (engine.Unsupported?.Contains(RePak.NoModulus) == true)
        {
            Assert.EndsWith(reader.ModulusFile(game), engine.Unsupported); // offline: Detect says where a user can put it
            output.WriteLine($"  not indexed: the modulus couldn't be downloaded ({engine.Unsupported})");
            return;
        }
        Assert.Null(engine.Unsupported);
        // the app's reader chain (ScsKiller.DefaultReaders' order; Unreal's cache folder in temp) picks this reader
        var chain = new Core.Carved.EngineReaders(("Unreal", new Core.Unreal.UnrealReader(Ff7.TempDir("reengine-unreal"))), (Core.Unity.UnityReader.Family, new Core.Unity.UnityReader()),
            (ReEngineReader.Family, new ReEngineReader(data)), (Core.Carved.CarvedReader.Family, new Core.Carved.CarvedReader()));
        Assert.Equal(engine, chain.Detect(game));

        sw.Restart();
        var index = reader.Index(game, engine, new Progress<string>(output.WriteLine), CancellationToken.None);
        output.WriteLine($"  index {sw.Elapsed.TotalSeconds:F1}s: {index.Shaders.Count} shaders ({index.Shaders.Values.Count(s => s.RootSignature != null)} with RTS0), platforms "
            + string.Join(", ", index.Maps.GroupBy(m => m.Platform).Select(g => $"{g.Key} {g.Count()} maps")) + "; models "
            + string.Join(", ", index.Shaders.Values.GroupBy(s => s.ShaderModel).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}")));
        Assert.True(index.Shaders.Count > 0);
        if (key == "PRAGMATA") Assert.Equal(93400, index.Shaders.Count); // the downloaded modulus opens every package
        if (key.StartsWith("Resident Evil Village")) Assert.Equal("D3D12", engine.GraphicsApi); // DirectX 12 only: SM5 ships beside its SM6
        Assert.Equal(check.Readiness == Readiness.Ready, index.Shaders.Values.Count(s => s.RootSignature != null) >= 0.9 * index.Shaders.Count || RootSig.Verified(engine));

        // ReadShaders serves the indexed bytes back (a sample)
        var want = index.Shaders.Keys.Where((_, i) => i % 97 == 0).ToHashSet();
        var got = new Dictionary<string, byte[]>();
        sw.Restart();
        reader.ReadShaders(game, engine, want, (h, b) => got[h] = b, CancellationToken.None);
        output.WriteLine($"  served {got.Count}/{want.Count} ({sw.Elapsed.TotalSeconds:F1}s)");
        Assert.Equal(want.Count, got.Count);

        // offline plan without a recording (NeedsRecording games: what the planner can do before one; hash-only, temp folder)
        var dir = Ff7.TempDir($"reengine-{key}");
        sw.Restart();
        var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia with { PerStageCache = true }, dir, new Progress<string>(output.WriteLine), CancellationToken.None);
        output.WriteLine($"  plan (no recording): {plan.Stats} ({sw.Elapsed.TotalSeconds:F1}s)");
        output.WriteLine($"  index {index.ContentHash}, plan file sha256 {Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(plan.FilePath)))}");
        Directory.Delete(dir, true);
    }
}
