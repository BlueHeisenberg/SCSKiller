using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.Nixxes;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Nixxes;

/// <summary>Ghost of Tsushima DIRECTOR'S CUT, Ratchet &amp; Clank: Rift Apart and HELLDIVERS 2 on this machine (read-only;
/// skipped when absent): found before the carver, their shaders indexed, no root signature shipped. GoT and R&amp;C plan
/// from their files with their rule (plan-rules.json): ready without a recording on NVIDIA, after one on AMD.
/// HELLDIVERS 2 has no rule and needs a recording, which its GameGuard blocks.</summary>
[Trait("Needs", "Game")]
public class NixxesGameTests(ITestOutputHelper output)
{
    [Fact]
    public void IndexesGhostOfTsushima() =>
        // the probe's counts: 20,784 distinct DXIL containers (11,127 PS, 7,346 CS, 2,282 VS, 18 DS, 11 HS)
        Check("steam:2215430", @"cache_pc\psarc\gapack_misc_a.psarc", "GhostOfTsushima.exe", NixxesReader.SuckerPunch, AntiCheat.None, 20_000, "nixxes-got");

    [Fact]
    public void IndexesRatchetAndClankRiftApart() =>
        // the probe's counts: 9,765 DXIL shaders and 392 ray tracing libraries in the archives, more in the exe
        Check("steam:1895880", "toc", "RiftApart.exe", NixxesReader.Insomniac, AntiCheat.None, 9_000, "nixxes-rc");

    [Fact]
    public void IndexesHelldivers2() =>
        // the probe's counts: 16,231 distinct DXBC containers
        Check("steam:553850", @"data\bundles.00.nxa", "helldivers2.exe", NixxesReader.Stingray, AntiCheat.Other, 15_000);

    void Check(string id, string marker, string exe, string fork, AntiCheat antiCheat, int minShaders, string? rule = null)
    {
        // Steam's own listing: the folder name and the exe the app picks, as a scan finds them
        if (new SteamSource().Discover().FirstOrDefault(g => g.Id == id) is not { } game || !File.Exists(Path.Combine(game.InstallDir, marker)))
        {
            output.WriteLine($"skipped: {id} isn't installed");
            return;
        }
        output.WriteLine($"{game.InstallDir}: exe {game.ExePath}, Steam build {game.Version}");
        Assert.Equal(exe, Path.GetFileName(game.ExePath), ignoreCase: true);
        Assert.Equal(antiCheat, GameFiles.DetectAntiCheat(game));
        var sw = Stopwatch.StartNew();
        var engine = SCSKiller.Core.App.ScsKiller.DefaultReaders(Ff7.TempDir("nixxes-readers")).Detect(game)!;   // every reader, in scan order
        output.WriteLine($"detect {sw.Elapsed.TotalSeconds:F1}s: {engine}");
        Assert.Equal(new EngineInfo(NixxesReader.Family, NixxesReader.Version, fork, "D3D12", false, null, Game: game.Version is { } v ? $"{id}@{v}" : id), engine);
        Assert.Equal(rule, RulesFile.Embedded.RuleFor(engine)?.Id);
        foreach (var (name, caps) in new[] { ("NVIDIA", Ff7.Nvidia), ("AMD", Ff7.Amd) })
        {
            var check = new Planner().Check(game, engine, null, caps);
            output.WriteLine($"check ({name}): {check}");
            Assert.Equal(rule != null && caps.StateIndependentCache ? new PlanCheck(Readiness.Ready, Planner.NoRecording) : new PlanCheck(Readiness.NeedsRecording, Planner.Record), check);
        }

        if (fork == NixxesReader.SuckerPunch) NaughtyDog.NaughtyDogGameTests.CheckTocs(Path.Combine(game.InstallDir, @"cache_pc\psarc"), "*.psarc", output);

        var log = new List<string>();
        var index = new NixxesReader().Index(game, engine, new Log(l => { log.Add(l); output.WriteLine(l); }), CancellationToken.None);
        Assert.True(index.Shaders.Count > minShaders);
        Assert.DoesNotContain(log, l => l.Contains("warning"));
        Assert.All(index.Shaders.Values, s => Assert.Null(s.RootSignature));
        output.WriteLine($"{index.Maps.Count} maps ({index.Maps.Count(m => m.IsPipeline)} exact), largest pool {index.Maps.Max(m => m.Shaders.Count)}; platforms {string.Join(", ", index.Platforms)}");

        // NVIDIA's backend plans per stage; whole pipelines too, for the recorded plans' PSO counts
        foreach (var (name, caps) in new[] { ("NVIDIA", Ff7.Nvidia with { PerStageCache = true }), ("NVIDIA whole pipelines", Ff7.Nvidia), ("AMD", Ff7.Amd) })
        {
            sw.Restart();
            log.Clear();
            var plan = new Planner { Rules = RulesFile.Embedded }.Build(game, engine, index, null, caps,
                Ff7.TempDir($"nixxes-plan-{fork.Replace(' ', '-')}-{caps.Profile}-{caps.PerStageCache}"), new Log(l => { log.Add(l); output.WriteLine(l); }), CancellationToken.None);
            var s = plan.Stats;
            output.WriteLine($"plan ({name}) {sw.Elapsed.TotalSeconds:F1}s: rule {s.RsRule}, Generated {s.Generated:N0}, LeftOut {s.LeftOut:N0}, Uncovered {s.Uncovered:N0}, of {s.StageSets:N0} stage sets, {s.RootSignatures} roots");
            Assert.DoesNotContain(log, l => l.Contains("rs_unknown") || l.Contains("rs_unserializable"));
            Assert.Equal(rule, s.RsRule);
            if (rule == null || !caps.StateIndependentCache) continue;
            Assert.True(s.RootSigRuleVerified);
            Assert.True(s.Generated > 0);
        }
    }

    sealed class Log(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }
}
