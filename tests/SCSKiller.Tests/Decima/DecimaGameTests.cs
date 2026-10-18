using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Decima;
using SCSKiller.Core.Games;
using SCSKiller.Core.Nixxes;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Decima;

/// <summary>Horizon Zero Dawn Complete Edition and DEATH STRANDING DIRECTOR'S CUT on this machine (read-only; skipped when
/// absent): found before the carver, every archive opening within the caps, their shaders indexed as VS+PS programs, no
/// ray tracing, a recording needed. Each test keeps its data folder
/// (the salts found, for Death Stranding) until its next run.</summary>
[Trait("Needs", "Game")]
public class DecimaGameTests(ITestOutputHelper output)
{
    [Fact]
    public void IndexesHorizonZeroDawn()
    {
        if (new SteamSource().Discover().FirstOrDefault(g => g.Id == "steam:1151640") is not { } game) return;
        // the probe's counts: 39,994 distinct containers (25,127 CS, 9,630 PS, 5,227 VS, 9 GS)
        Check(game, DecimaReader.HorizonZeroDawn, 39_000);
    }

    [Fact]
    public void IndexesDeathStrandingDirectorsCut()
    {
        if (new SteamSource().Discover().FirstOrDefault(g => g.Id == "steam:1850570") is not { } game) return;
        // the probe's counts: 15,549 distinct containers (9,583 PS, 4,956 VS, 1,000 CS, 10 GS)
        Check(game, DecimaReader.DeathStranding, 15_000);
    }

    static string DataDir(Game game) => Ff7.TempDir("decima-game-" + game.Id.Replace(':', '-'));

    void Check(Game game, string fork, int minShaders)
    {
        TestEnv.BuildCodecs();
        output.WriteLine($"{game.InstallDir}: exe {game.ExePath}");
        Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game));
        Assert.False(BootPrecompiles.Embedded.Has(game));
        var data = DataDir(game);
        output.WriteLine($"data folder (kept until this test runs again): {data}");
        var sw = Stopwatch.StartNew();
        var engine = SCSKiller.Core.App.ScsKiller.DefaultReaders(data).Detect(game)!;   // every reader, in scan order
        output.WriteLine($"detect {sw.Elapsed.TotalSeconds:F1}s: {engine}");
        Assert.Equal(new EngineInfo(DecimaReader.Family, DecimaReader.Version, fork, "D3D12", false, null), engine);
        Assert.Equal(new PlanCheck(Readiness.NeedsRecording, Planner.Record), new Planner().Check(game, engine, null, Ff7.Nvidia));

        var reader = new DecimaReader(data);
        var (_, archives, _) = DecimaReader.Layout(game.InstallDir)!.Value;
        var salts = new DecimaKeys(data).Stored(game);
        foreach (var f in archives)
        {
            using var a = DecimaArchive.Open(f.FullName, salts);
            Assert.True(a != null, $"{f.Name} doesn't open");
            output.WriteLine($"{f.Name}: {f.Length / 1e9:F2} GB, {a.Chunks} chunks, {a.Length / 1e9:F2} GB unpacked");
        }

        sw.Restart();
        var log = new List<string>();
        var index = reader.Index(game, engine, new Log(l => { log.Add(l); output.WriteLine(l); }), CancellationToken.None);
        output.WriteLine($"index {sw.Elapsed.TotalMinutes:F1} min");
        Assert.True(index.Shaders.Count > minShaders);
        // most VS are in a program (the probe: every VS->PS step of Death Stranding's three largest archives at its gap)
        var vs = index.Shaders.Values.Where(s => s.Stage == Stage.Vertex).Select(s => s.Sha1).ToHashSet();
        var paired = index.Maps.Where(m => m.IsPipeline && m.Shaders.Count == 2).Select(m => m.Shaders[0]).Where(vs.Contains).ToHashSet();
        output.WriteLine($"{paired.Count} of {vs.Count} VS in a VS+PS program");
        Assert.True(paired.Count >= vs.Count * 0.8, $"{paired.Count} of {vs.Count} VS in a program");
        Assert.All(index.Shaders.Values, s => Assert.Null(s.RootSignature));
        Assert.DoesNotContain(index.Shaders.Values, s => s.Stage is Stage.Library or Stage.Mesh or Stage.Amplification || s.InlineRayTracing);
        OnlyKnownWarnings(log);
    }

    /// <summary>The most of each warning real data may give: above this the program layout isn't what the reader takes it for.</summary>
    internal const int MaxKnownWarning = 64;

    /// <summary>The only warnings real data may give, each at most <see cref="MaxKnownWarning"/>: VS without their PS (Horizon
    /// Zero Dawn: 2 of about 837,000) and programs whose VS doesn't feed their PS (Horizon Zero Dawn: 9 of 13,832). Any
    /// other warning fails.</summary>
    internal static void OnlyKnownWarnings(IEnumerable<string> log)
    {
        string[] known = [@"^warning: (\d+) VS not followed by a PS \d+ bytes later$", @"^warning: (\d+) programs whose VS doesn't feed their PS, pooled instead$"];
        var warnings = log.SelectMany(l => Regex.Replace(l, @" \([\d.]+s\)$", "").Split("; ")).Where(w => w.StartsWith("warning:"));
        Assert.All(warnings, w => Assert.True(known.Select(k => Regex.Match(w, k)).FirstOrDefault(m => m.Success) is { } m
            && long.Parse(m.Groups[1].Value) <= MaxKnownWarning, w));
    }

    /// <summary>Not a check: with SCSKILLER_DECIMA_PROBE set to an output file, writes how Death Stranding's containers
    /// follow each other in its three largest archives (decrypted with the salts a scan of its exe gives, kept in memory): the
    /// count of each (stage, next stage, bytes between), then for the commonest VS-led ones the bytes between, hex, at most
    /// 128 each. Reads at most <see cref="ProbeBytes"/> of each archive. Read-only.</summary>
    [Fact]
    public void ProbesDeathStrandingProgramRecords()
    {
        if (Environment.GetEnvironmentVariable("SCSKILLER_DECIMA_PROBE") is not { Length: > 0 } outFile) return;
        if (new SteamSource().Discover().FirstOrDefault(g => g.Id == "steam:1850570") is not { } game) return;
        TestEnv.BuildCodecs();
        var (_, archives, _) = DecimaReader.Layout(game.InstallDir)!.Value;
        // the salts stay in memory: nothing about them is written
        var salts = DecimaKeys.Scan(game.ExePath, [.. archives.OrderBy(f => f.Length).Select(f => f.FullName)], out var how);
        Assert.True(salts != null, how);
        var text = new StringBuilder();
        foreach (var f in archives.OrderByDescending(f => f.Length).Take(3))
        {
            using var a = DecimaArchive.Open(f.FullName, salts)!;
            var seq = new List<(long At, int Size, int Kind)>();
            NixxesReader.Carve(a.Readable, new byte[16 << 20], (at, c) => seq.Add((at, c.Length, Dxbc.Kind(c))),
                new NixxesReader.Meter(ProbeBytes), () => seq.Count >= ProbeContainers, CancellationToken.None);
            text.AppendLine($"== {f.Name}: {a.Length / 1e9:F2} GB unpacked, {seq.Count} containers read (kind: 0 PS, 1 VS, 2 GS, 5 CS)");
            var steps = seq.Zip(seq.Skip(1), (x, y) => (From: x.Kind, To: y.Kind, Gap: y.At - x.At - x.Size, End: x.At + x.Size)).ToList();
            foreach (var g in steps.GroupBy(s => (s.From, s.To, s.Gap)).OrderByDescending(g => g.Count()).Take(30))
                text.AppendLine($"{g.Key.From}->{g.Key.To} gap {g.Key.Gap}: {g.Count()}");
            foreach (var g in steps.Where(s => s.From == 1 && s.Gap is > 0 and <= 1 << 16).GroupBy(s => (s.To, s.Gap)).OrderByDescending(g => g.Count()).Take(8))
                foreach (var s in g.Take(3))
                {
                    var b = new byte[(int)Math.Min(s.Gap, 128)];
                    a.Read(s.End, b);
                    text.AppendLine($"1->{s.To} gap {s.Gap} at {s.End}: {Convert.ToHexString(b)}");
                }
            // the bytes before the first VS records: a header there would name the program's other stages
            foreach (var v in seq.Where(x => x.Kind == 1 && x.At >= 128).Take(5))
            {
                var b = new byte[128];
                a.Read(v.At - 128, b);
                text.AppendLine($"128 bytes before VS at {v.At}: {Convert.ToHexString(b)}");
            }
        }
        File.WriteAllText(outFile, text.ToString());
        output.WriteLine($"wrote {outFile}");
    }

    const long ProbeBytes = 2L << 30;
    const int ProbeContainers = 20_000;

    sealed class Log(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }
}
