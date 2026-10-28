using System.Diagnostics;
using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.BlackSpace;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.BlackSpace;

/// <summary>Crimson Desert on this machine (Steam; read-only; skipped when absent): found before the carver, its shader group
/// indexed, a recording needed to plan; what the carver alone finds, for comparison; and what each map key would give the
/// planner (<see cref="BlackSpaceReader.MapKey"/>).</summary>
[Trait("Needs", "Game")]
public class BlackSpaceGameTests(ITestOutputHelper output)
{
    static Game? CrimsonDesert()
    {
        var dir = TestEnv.GameDir("Crimson Desert");
        return Directory.Exists(Path.Combine(dir, "meta")) ? new Game("steam:3321460", "Crimson Desert", Store.Steam, dir, Path.Combine(dir, @"bin64\CrimsonDesert.exe")) : null;
    }

    [Fact]
    public void IndexesCrimsonDesert()
    {
        if (CrimsonDesert() is not { } game) return;
        var reader = new BlackSpaceReader();
        var sw = Stopwatch.StartNew();
        var engine = new EngineReaders((BlackSpaceReader.Family, reader), (CarvedReader.Family, new CarvedReader())).Detect(game)!;
        output.WriteLine($"detect {sw.Elapsed.TotalSeconds:F1}s: {engine}; anti-cheat {GameFiles.DetectAntiCheat(game)}");
        Assert.Equal(BlackSpaceReader.Family, engine.Family);
        Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game));
        var check = new Planner().Check(game, engine, null, Ff7.Nvidia);
        output.WriteLine($"check without a recording: {check}");
        Assert.Equal(Readiness.NeedsRecording, check.Readiness);
        sw.Restart();
        var index = reader.Index(game, engine, new Progress<string>(output.WriteLine), CancellationToken.None);
        output.WriteLine($"index {sw.Elapsed.TotalSeconds:F1}s, {index.Maps.Count} maps");
        Assert.True(index.Shaders.Count > 25_000);   // October 2026: 77,247 .padxil files, 30,510 distinct containers
        Assert.Contains(index.Shaders.Values, s => s.Stage == Stage.Vertex);
        Assert.Contains(index.Shaders.Values, s => s.Stage == Stage.Pixel);

        sw.Restart();
        var carved = new CarvedReader().Detect(game, out var notes);
        output.WriteLine($"the carver alone, {sw.Elapsed.TotalSeconds:F1}s: {carved?.ToString() ?? "not detected"}; {notes}");
    }

    sealed class Log(Action<string> a) : IProgress<string> { public void Report(string v) => a(v); }   // in order, unlike Progress

    static readonly VendorCaps Nvidia = Ff7.Nvidia with { PerStageCache = true };   // as NvidiaBackend's

    /// <summary>The NVIDIA plan with this PC's recording (SCSKILLER_BLACKSPACE_RECORDING, else the app's
    /// games\steam_3321460\recording.db; skipped without one), planned from a copy: what the learned root signature lookup
    /// covers and what it leaves out. Output, and a plan that compiles something.</summary>
    [Fact]
    public void PlansCrimsonDesertWithARecording()
    {
        var db = Environment.GetEnvironmentVariable("SCSKILLER_BLACKSPACE_RECORDING")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCSKiller", "games", "steam_3321460", "recording.db");
        if (CrimsonDesert() is not { } game || !File.Exists(db)) return;
        var dir = Ff7.TempDir("blackspace-plan");
        var copy = Path.Combine(dir, "recording.db");
        File.Copy(db, copy, true);
        var reader = new BlackSpaceReader();
        var engine = reader.Detect(game)!;
        var index = reader.Index(game, engine, null, CancellationToken.None);
        var check = new Planner().Check(game, engine, new Recording(copy), Nvidia);
        output.WriteLine($"check with the recording: {check}");
        var sw = Stopwatch.StartNew();
        var plan = new Planner().Build(game, engine, index, new Recording(copy), Nvidia, Path.Combine(dir, "plan"), new Log(output.WriteLine), CancellationToken.None);
        output.WriteLine($"plan {sw.Elapsed.TotalSeconds:F1}s, {new FileInfo(plan.FilePath).Length >> 10} KiB: {plan.Stats}");
        Assert.True(plan.Stats.Recorded + plan.Stats.Generated > 0);
    }

    /// <summary>Per map key: the VS->PS pairs whose signatures link inside a map (what StageSets enumerates before a
    /// recording narrows it to recorded resource counts), and the PS no VS of its map feeds (never warmed). Output only.</summary>
    [Fact]
    public void MapKeySurvey()
    {
        if (CrimsonDesert() is not { } game) return;
        var group = BlackSpaceReader.ShaderGroup(game.InstallDir)!;
        var pamt = Pamt.Parse(File.ReadAllBytes(Path.Combine(group, Pamt.FileName)));
        var folder = pamt.Folders.First(f => f.Name == BlackSpaceReader.ShaderFolder);
        var named = new List<(string[] Fields, string Sha, ShaderInfo S)>();   // a container under several names: once per map
        var sw = Stopwatch.StartNew();
        foreach (var archive in pamt.Files.Skip(folder.FirstFile).Take(folder.FileCount).Where(e => e.Flags is Pamt.Stored or Pamt.Lz4).GroupBy(e => e.Archive))
        {
            var paz = Path.Combine(group, $"{archive.Key}.paz");
            using var h = File.OpenHandle(paz);
            foreach (var e in archive.OrderBy(e => e.Offset))
            {
                var fields = Path.GetFileNameWithoutExtension(e.Name).Split('_');
                if (fields.Length != 7 || fields[2] is not ("0" or "4")) continue;   // VS and PS
                if (BlackSpaceReader.Container(h, new(paz, e.Offset, (int)e.StoredSize, (int)e.Size, e.Flags)) is not { } c) continue;
                var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                try { if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } s) named.Add((fields, sha, s)); }
                catch (Exception x) when (x is ArgumentException or IndexOutOfRangeException) { }
            }
        }
        int Distinct(Stage st) => named.Where(n => n.S.Stage == st).Select(n => n.Sha).Distinct().Count();
        output.WriteLine($"{named.Count(n => n.S.Stage == Stage.Vertex)} VS and {named.Count(n => n.S.Stage == Stage.Pixel)} PS files, "
            + $"{Distinct(Stage.Vertex)} and {Distinct(Stage.Pixel)} distinct ({sw.Elapsed.TotalSeconds:F1}s)");
        int[][] keys = [[0], [0, 4], [0, 6], []];
        foreach (var key in keys)
        {
            long pairs = 0;
            int lonely = 0, maps = 0;
            foreach (var byKey in named.GroupBy(n => string.Join('_', key.Select(i => n.Fields[i]))))
            {
                var map = byKey.DistinctBy(n => n.Sha).ToList();
                maps++;
                var vsBySig = map.Where(n => n.S.Stage == Stage.Vertex && Planner.Rasterizable(n.S)).ToLookup(n => Planner.Sig(n.S.Outputs));
                foreach (var ps in map.Where(n => n.S.Stage == Stage.Pixel))
                {
                    var fed = vsBySig[Planner.Sig(ps.S.Inputs)].Count(v => Planner.Links(v.S, ps.S));
                    pairs += fed;
                    if (fed == 0) lonely++;
                }
            }
            output.WriteLine($"fields {(key.Length == 0 ? "none (one map)" : string.Join('+', key))}: {maps} maps, {pairs:N0} linked VS->PS pairs, {lonely} PS fed by no VS of its map");
        }
    }
}
