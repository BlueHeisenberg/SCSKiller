using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.NaughtyDog;
using SCSKiller.Core.Nixxes;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.NaughtyDog;

/// <summary>The Last of Us Part I and UNCHARTED: Legacy of Thieves Collection's two games on this machine (read-only;
/// skipped when absent): found before the carver, their material and global shaders indexed with the root signatures they
/// ship, no ray tracing, a recording needed.</summary>
[Trait("Needs", "Game")]
public class NaughtyDogGameTests(ITestOutputHelper output)
{
    [Fact]
    public void IndexesTheLastOfUsPartI()
    {
        var dir = TestEnv.GameDir("The Last of Us Part I");
        var game = new Game("steam:1888930", "The Last of Us Part I", Store.Steam, dir, Path.Combine(dir, "tlou-i.exe"));
        if (!File.Exists(Path.Combine(dir, @"build\pc\main\shaders.psarc"))) return;
        // the probe's counts: 149,178 distinct DXIL containers, 338 root signatures, no ray tracing
        Check(game, 140_000, 300);
    }

    // the probe's counts: 348,843 distinct DXIL containers between the two, about 261,000 Uncharted 4's
    [Fact]
    public void IndexesUncharted4() => Uncharted("steam:1659420", "UNCHARTED 4: A Thief's End", 250_000);

    [Fact]
    public void IndexesTheLostLegacy() => Uncharted("steam:1659420:tll", "UNCHARTED: The Lost Legacy", 80_000);

    void Uncharted(string id, string name, int minShaders)
    {
        // Steam's own listing: the collection's game and its exe, as a scan finds them
        if (new SteamSource().Discover().FirstOrDefault(g => g.Id == id) is not { } game) return;
        output.WriteLine($"{game.InstallDir}: {game.Name}, exe {game.ExePath}");
        Assert.Equal(name, game.Name);
        Assert.Contains(Path.GetFileName(game.ExePath), Collections.Member(id)!.Value.Game.Exes);
        Check(game, minShaders, 1);
    }

    void Check(Game game, int minShaders, int minRootSignatures)
    {
        TestEnv.BuildCodecs();
        Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game));
        Assert.False(BootPrecompiles.Embedded.Has(game));
        var sw = Stopwatch.StartNew();
        var engine = SCSKiller.Core.App.ScsKiller.DefaultReaders(Ff7.TempDir("naughtydog-readers")).Detect(game)!;   // every reader, in scan order
        output.WriteLine($"detect {sw.Elapsed.TotalSeconds:F1}s: {engine}");
        Assert.Equal(new EngineInfo(NaughtyDogReader.Family, NaughtyDogReader.Version, null, "D3D12", false, null), engine);
        Assert.Equal(Readiness.NeedsRecording, new Planner().Check(game, engine, null, Ff7.Nvidia).Readiness);

        CheckTocs(game.InstallDir, "*.psarc", output);

        var reader = new NaughtyDogReader();
        var log = new List<string>();
        var index = reader.Index(game, engine, new Log(l => { log.Add(l); output.WriteLine(l); }), CancellationToken.None);
        Assert.True(index.Shaders.Count > minShaders);
        Assert.True(index.Shaders.Values.Select(s => s.RootSignature).OfType<string>().Distinct().Count() >= minRootSignatures);
        Assert.Contains(index.Shaders.Values, s => s.Stage == Stage.Pixel && s.RootSignature != null);
        Assert.DoesNotContain(index.Shaders.Values, s => s.Stage is Stage.Library or Stage.Mesh or Stage.Amplification || s.InlineRayTracing);
        Assert.DoesNotContain(log, l => l.Contains("warning"));

        // VSs reading no vertex attribute (AMD's "no vertex input"): an input signature of system values only, or none
        var vs = index.Shaders.Values.Where(s => s.Stage == Stage.Vertex).ToList();
        var pulling = vs.Where(s => s.Inputs.All(i => i.SysValue != 0)).ToList();
        output.WriteLine($"{pulling.Count} of {vs.Count} VS read no vertex attribute");
        if (pulling.FirstOrDefault() is { } one)
            reader.ReadShaders(game, engine, new HashSet<string> { one.Sha1 }, (_, b) => output.WriteLine(
                $"e.g. {one.Sha1}: parts {string.Join(',', Enumerable.Range(0, BitConverter.ToInt32(b, 28)).Select(i => Encoding.ASCII.GetString(b, BitConverter.ToInt32(b, 32 + 4 * i), 4)))}; inputs {string.Join(", ", one.Inputs.Select(i => $"{i.Semantic}{i.Index} sv{i.SysValue}"))}"), CancellationToken.None);

        // what pairing each package's VSs and PSs by linkage plans: the number to check before planning without a recording
        sw.Restart();
        var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia, Ff7.TempDir("naughtydog-plan-" + game.Id.Replace(':', '-')), new Log(output.WriteLine), CancellationToken.None);
        output.WriteLine($"plan {sw.Elapsed.TotalSeconds:F1}s: {plan.Stats}");
        Assert.True(plan.Stats.Generated > 0);
    }

    /// <summary>Every PSARC archive under <paramref name="dir"/> (plain or in a DSAR file) opens under <see cref="Psarc.MaxToc"/>;
    /// logs the largest TOC against it.</summary>
    internal static void CheckTocs(string dir, string pattern, ITestOutputHelper output)
    {
        var tocs = Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).Select(f =>
        {
            using (var a = Psarc.Open(f)) Assert.True(a != null, $"{f} doesn't open");
            var head = new byte[16];
            using (var d = Dsar.Open(f))
                if (d != null) d.Read(0, head);
                else using (var s = File.OpenRead(f)) s.ReadExactly(head);
            return (File: Path.GetRelativePath(dir, f), Toc: BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(12)));
        }).ToList();
        var largest = tocs.MaxBy(t => t.Toc);
        output.WriteLine($"{tocs.Count} archives; the largest TOC {largest.Toc:N0} bytes ({largest.File}), the cap {Psarc.MaxToc:N0}");
    }

    sealed class Log(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }
}
