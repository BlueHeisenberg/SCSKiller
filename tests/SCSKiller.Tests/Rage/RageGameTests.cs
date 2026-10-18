using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Rage;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Rage;

/// <summary>GTA V Enhanced on this machine (read-only; skipped when absent): BattlEye detected, found before the carver,
/// every pass an exact pipeline with its shaders' own root signatures, planned on NVIDIA without a recording.</summary>
[Trait("Needs", "Game")]
public class RageGameTests(ITestOutputHelper output)
{
    [Fact]
    public void PlansGtaVEnhancedWithoutARecording()
    {
        var dir = TestEnv.GameDir("Grand Theft Auto V Enhanced");
        var game = new Game("steam:3240220", "Grand Theft Auto V Enhanced", Store.Steam, dir, Path.Combine(dir, "GTA5_Enhanced.exe"));
        if (!File.Exists(Path.Combine(dir, @"update\update2.rpf"))) return;
        Assert.Equal(AntiCheat.BattlEye, GameFiles.DetectAntiCheat(game));
        var reader = new RageReader();
        var sw = Stopwatch.StartNew();
        var engine = SCSKiller.Core.App.ScsKiller.DefaultReaders(Ff7.TempDir("rage-readers")).Detect(game)!;   // every reader, in scan order
        output.WriteLine($"detect {sw.Elapsed.TotalSeconds:F1}s: {engine}");
        Assert.Equal(new EngineInfo(RageReader.Family, RageReader.Version, null, "D3D12", false, null), engine);
        Assert.Equal(new PlanCheck(Readiness.Ready, Planner.NoRecording), new Planner().Check(game, engine, null, Ff7.Nvidia));

        var log = new List<string>();
        var index = reader.Index(game, engine, new Log(l => { log.Add(l); output.WriteLine(l); }), CancellationToken.None);
        Assert.True(index.Shaders.Count > 12_000);
        Assert.All(index.Shaders.Values, s => Assert.NotNull(s.RootSignature));
        Assert.Contains(index.Shaders.Values, s => s.InlineRayTracing);
        // the pass decode: every slot's shader has that slot's stage, and the VS of every untessellated pass feeds its PS
        Assert.True(index.Maps.Count(m => m.IsPipeline) > 8_500);
        Assert.DoesNotContain(log, l => l.Contains("another stage") || l.Contains("doesn't feed") || l.Contains("without a pass table"));

        foreach (var caps in new[] { Ff7.Nvidia, Ff7.Nvidia with { PerStageCache = true } })
        {
            sw.Restart();
            var plan = new Planner().Build(game, engine, index, null, caps, Ff7.TempDir($"rage-gta5-{caps.PerStageCache}"), new Log(output.WriteLine), CancellationToken.None);
            output.WriteLine($"plan (per stage {caps.PerStageCache}) {sw.Elapsed.TotalSeconds:F1}s: {plan.Stats}");
            Assert.Equal(0, plan.Stats.Uncovered);
            Assert.True(plan.Stats.Generated > 8_000);
        }
    }

    /// <summary>The whole plan, materialized from the install and replayed by scskiller_warm on WARP (the runtime's validation
    /// on the CPU, no GPU driver cache). Manual: SCSKILLER_WARP_TESTS=1 and this checkout's proxy built. The work folder holds
    /// game shader bytes and is deleted.</summary>
    [Fact]
    public void PlanReplaysOnWarp()
    {
        var dir0 = TestEnv.GameDir("Grand Theft Auto V Enhanced");
        var game = new Game("steam:3240220", "Grand Theft Auto V Enhanced", Store.Steam, dir0, Path.Combine(dir0, "GTA5_Enhanced.exe"));
        var warm = Path.Combine(Ff7.ProxyBin, "scskiller_warm.exe");
        if (Environment.GetEnvironmentVariable("SCSKILLER_WARP_TESTS") != "1" || !File.Exists(Path.Combine(dir0, @"update\update2.rpf")) || !File.Exists(warm)) return;
        var reader = new RageReader();
        var engine = reader.Detect(game)!;
        var index = reader.Index(game, engine, null, CancellationToken.None);
        var dir = Ff7.TempDir("rage-warp");
        try
        {
            var planner = new Planner();
            var plan = planner.Build(game, engine, index, null, Ff7.Nvidia, dir, new Log(output.WriteLine), CancellationToken.None);
            var work = Path.Combine(dir, "work");
            planner.Materialize(plan, game, engine, reader, null, work, CancellationToken.None);
            Ff7.CheckWarmReady(work);
            var sw = Stopwatch.StartNew();
            var psi = new ProcessStartInfo(warm, [work, $"scsk_rage_warp_{Random.Shared.Next(100000, 999999)}.exe", "--threads", "8", "--adapter-luid", FromSoft.FromSoftGameTests.WarpLuid().ToString("x")])
                { StandardOutputEncoding = System.Text.Encoding.UTF8, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            string o;
            using (var p = Process.Start(psi)!) { o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
            var lines = o.Split('\n').Select(l => l.Trim()).ToList();
            var done = lines.Where(l => l.StartsWith('{')).Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
                .LastOrDefault(e => e.GetProperty("event").GetString() == "done");
            Assert.True(done.ValueKind == System.Text.Json.JsonValueKind.Object, o);
            var log = Path.Combine(TestEnv.WarmStage(o, work), "scskiller.log");
            output.WriteLine($"{sw.Elapsed.TotalSeconds:F0}s {lines.FirstOrDefault(l => l.Contains("\"start\""))} {done}\n"
                + (File.Exists(log) ? string.Join('\n', File.ReadLines(log).Where(l => (l.Contains("fail", StringComparison.OrdinalIgnoreCase) || l.Contains("d3d12 debug")) && !l.Contains("severity 2")).Take(60)) : ""));
            // WARP runs 4 lanes: it rejects the 17 compute shaders with [WaveSize(32)]. It also rejects 4 tessellated passes of
            // normal_spec_batch with no debug-layer message; their stages link and the same shaders pass in other pipelines.
            Assert.Equal(21, done.GetProperty("failed").GetInt64());
        }
        finally { Directory.Delete(dir, true); }
    }

    sealed class Log(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }
}
