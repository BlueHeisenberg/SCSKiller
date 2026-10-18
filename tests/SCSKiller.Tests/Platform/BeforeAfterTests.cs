using System.Globalization;
using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

public class BeforeAfterTests : IDisposable
{
    readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "scskiller-ba-" + Guid.NewGuid().ToString("N")[..8])).FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    const long Unix = 1_700_000_000_000;

    /// <summary>One launch of "Game.exe" as the recorder writes it: a 2 s precompile of cache hits, then play at 100 fps with
    /// <paramref name="hits"/> cache hits a second, a 300 ms frame filled by a 200 ms compile for each shader stutter, and the last 10 s quitting.
    /// <paramref name="fastCompiles"/>: 5 ms compiles spread over play that make no frame slow.</summary>
    SessionSnapshot Run(long unix, int stutters, double playSeconds, int hits = 5, int fastCompiles = 0, int library = 0, string? swapChain = null,
        bool end = true, bool overlay = false, PlayWindow? played = null, bool frameGen = false)
    {
        double last = 2000 + playSeconds * 1000 + 10_000;
        var rows = new List<(double T, string Row)>();
        rows.AddRange(Enumerable.Range(0, 600).Select(i => (i * 10 / 3.0, "S,1,1,0.500")));
        for (int s = 2; s * 1000 < last; s++) rows.AddRange(Enumerable.Range(0, hits).Select(j => (s * 1000 + j * 200.0 + 50, "S,1,1,0.500")));
        var slow = Enumerable.Range(0, stutters).Select(i => Math.Round((5000 + i * (playSeconds * 1000 - 5000) / stutters) / 10) * 10).ToHashSet();
        rows.AddRange(slow.Select(at => (at + 250, "S,0,0,200.000")));
        rows.AddRange(Enumerable.Range(0, fastCompiles).Select(i => (4000 + i * (playSeconds * 1000 - 4000) / fastCompiles, "S,0,0,5.000")));
        rows.AddRange(Enumerable.Range(0, library).Select(i => (4100 + i * 991.0, "s,1,1,4.000")));
        var csv = Path.Combine(_dir, "scskiller_creates.csv");
        File.WriteAllLines(csv, [$"#session,{unix},Game.exe", "#clock,0.0",
            .. swapChain == null ? [] : new[] { $"#swapchain,{swapChain}" }, .. overlay ? new[] { "#overlay" } : [], .. frameGen ? new[] { "#framegen,1" } : [],
            .. rows.OrderBy(r => r.T).Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.T:0.0},{r.Row}")),
            .. end ? new[] { string.Create(CultureInfo.InvariantCulture, $"#end,{unix + (long)last},{last:0.0}") } : []]);
        var ends = new List<double>();
        for (double t = 0; t < last; t += 10)
        {
            ends.Add(t);
            if (slow.Contains(t)) ends.Add(t += 300);
        }
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, FrameLogTests.Launch(unix, 0, ends));
        var r = FrameLog.Read(bin, csv, "Game.exe")!;
        return Snapshots.Of(r, played, "driver-1", "610.88", "build-7", null);
    }

    [Fact]
    public void A_snapshot_round_trips_and_keeps_its_pin_when_the_launch_grows()
    {
        var store = new AppStore(_dir);
        var s = Run(Unix, 4, 200, swapChain: "2560,1440,28", overlay: true);
        Snapshots.Write(store, "steam:1", s);
        var back = Snapshots.Read(store, "steam:1", Unix)!;
        Assert.Equal((s.DurationMs, s.Frames, s.PlaySeconds, s.Hitches.Count, s.Peaks.Count, s.Creates), (back.DurationMs, back.Frames, back.PlaySeconds, back.Hitches.Count, back.Peaks.Count, back.Creates));
        Assert.Equal(("2560x1440 fmt 28", true, true, "driver-1", "610.88", "build-7"), (back.SwapChain, back.Overlay, back.CleanExit, back.DriverId, back.DriverVersion, back.Build));
        Assert.Contains("\"v\":1", File.ReadAllText(Path.Combine(store.GameDir("steam:1"), "sessions", $"{Unix}.json")));

        Assert.True(Snapshots.Pin(store, "steam:1", Unix, "before"));
        Assert.False(Snapshots.Pin(store, "steam:1", Unix + 1, "after"));
        Snapshots.Write(store, "steam:1", s with { DurationMs = s.DurationMs + 1000, Frames = s.Frames + 100 });
        Assert.Equal((true, "before", s.Frames + 100), (Snapshots.Read(store, "steam:1", Unix)!.Pinned, Snapshots.Read(store, "steam:1", Unix)!.Tag, Snapshots.Read(store, "steam:1", Unix)!.Frames));
    }

    /// <summary>The scan after every app start evaluates every game again: the same launch at the same length, frame count
    /// and exit isn't written again.</summary>
    [Fact]
    public void The_same_launch_is_not_rewritten()
    {
        var store = new AppStore(_dir);
        var s = Run(Unix, 1, 60);
        Snapshots.Write(store, "g", s);
        var file = new FileInfo(Path.Combine(store.GameDir("g"), "sessions", $"{Unix}.json"));
        var written = file.LastWriteTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Snapshots.Write(store, "g", s with { WarmedAt = DateTimeOffset.UnixEpoch });
        file.Refresh();
        Assert.Equal(written, file.LastWriteTimeUtc);
        Snapshots.Write(store, "g", s with { CleanExit = !s.CleanExit });   // the watcher saw the exit after the last frame
        file.Refresh();
        Assert.NotEqual(written, file.LastWriteTimeUtc);
    }

    /// <summary>The newest <see cref="Snapshots.Keep"/> unpinned snapshots stay, pinned ones on top; unpinning an old one
    /// lets it go.</summary>
    [Fact]
    public void Snapshots_are_pruned_to_the_newest_unpinned_and_pinned_ones_stay()
    {
        var store = new AppStore(_dir);
        var s = Run(Unix, 0, 30);
        Snapshots.Write(store, "g", s with { LaunchUnixMs = 1 });
        Snapshots.Pin(store, "g", 1, "before");
        for (long l = 2; l <= Snapshots.Keep + 5; l++) Snapshots.Write(store, "g", s with { LaunchUnixMs = l });
        var kept = Snapshots.List(store, "g").Select(x => x.LaunchUnixMs).ToList();
        Assert.Equal([.. Enumerable.Range(6, Snapshots.Keep).Select(i => (long)i).Reverse(), 1L], kept);
        Snapshots.Pin(store, "g", 1, null);
        Assert.Null(Snapshots.Read(store, "g", 1));
        Assert.Empty(Snapshots.List(store, "other"));
    }

    /// <summary>A cold run and a warmed run of the same game: the cold one's play compiles are shader stutters, the warmed one
    /// loads them from the cache. Play, frozen time, worst stutter and the ready share come from play only.</summary>
    [Fact]
    public void A_cold_and_a_warmed_run_compare_over_play()
    {
        var before = Run(Unix, 20, 300, hits: 1, fastCompiles: 1000);
        var after = Run(Unix + 1_000_000, 1, 290, hits: 5);
        Assert.Equal(300, before.PlaySeconds, 0);
        Assert.Equal(98, before.AvgFps, 0);   // the 20 stutters' 6 s hold 20 frames
        Assert.Equal((20, 1000L + 20), (before.Hitches.Count(h => h.Cause == HitchCause.Shader), before.Creates!.PlayCompiles));
        Assert.Null(BeforeAfter.RunProblem(before, before: true));
        Assert.Null(BeforeAfter.RunProblem(after, before: false));

        var c = BeforeAfter.Compare(before, after);
        Assert.Equal((20, 1), (c.Before.ShaderStutters, c.After.ShaderStutters));
        Assert.Equal((6.0, 0.3), (Math.Round(c.Before.FrozenSeconds, 3), Math.Round(c.After.FrozenSeconds, 3)));
        Assert.Equal(300, c.Before.WorstStutterMs, 3);
        Assert.InRange(c.Before.ReadyShare!.Value, 0.2, 0.3);
        Assert.InRange(c.After.ReadyShare!.Value, 0.99, 1);
        Assert.False(c.PerTenMinutes);   // 300 and 290 s
        Assert.False(c.AlreadySmooth);
        Assert.Equal(["Frame generation was off in both runs."], c.Notes);
        Assert.Matches(@"^Shader compilation stutter in Game: 20 → 1 in 5 minutes of play after SCSKiller precompiled its shaders\. 99% of pipelines were ready instead of 2\d%\. scskiller\.com$",
            BeforeAfter.ShareText("Game", c));
        Assert.True(BeforeAfter.Compare(before, Run(Unix + 2_000_000, 1, 200)).PerTenMinutes);
    }

    /// <summary>Runs of 5:46 and 6:42: the share text names the card's counts and the card's minutes, not counts scaled to
    /// the shorter run.</summary>
    [Fact]
    public void The_share_text_reads_the_cards_counts_when_the_runs_differ_in_length()
    {
        var c = BeforeAfter.Compare(Run(Unix, 11, 346), Run(Unix + 1_000_000, 10, 402));
        Assert.True(c.PerTenMinutes);
        Assert.Equal((11, 10), (c.Before.ShaderStutters, c.After.ShaderStutters));
        Assert.Equal((6, 7), BeforeAfter.PlayMinutes(c));
        Assert.StartsWith("Shader compilation stutter in Game: 11 → 10 in 6 and 7 minutes of play after", BeforeAfter.ShareText("Game", c));
    }

    [Fact]
    public void A_short_run_or_one_without_frames_doesnt_count()
    {
        Assert.Equal("The before run had 2 min of play, play at least 3 min.", BeforeAfter.RunProblem(Run(Unix, 3, 150), before: true));
        Assert.Equal("The after run had under a minute of play, play at least 3 min.", BeforeAfter.RunProblem(Run(Unix, 0, 40), before: false));
        Assert.Equal("The before run has no frame times", BeforeAfter.RunProblem(null, before: true));
    }

    /// <summary>The report's warnings: library loads only the after run had, another resolution, the countdown in one run
    /// only, a before run that didn't close normally, and whether frame generation was on in each run. A partly cached
    /// before run isn't one: a run that started with old driver-cache files is played again, so its ready pipelines are the
    /// driver's reuse within the launch.</summary>
    [Fact]
    public void Confounders_get_one_line_each()
    {
        var before = Run(Unix, 10, 240, hits: 5, swapChain: "2560,1440,28", end: false);
        var after = Run(Unix + 1_000_000, 0, 240, library: 30, swapChain: "1920,1080,28", overlay: true, frameGen: true);
        // a #framegen line alone is a module that loaded (Unreal loads DLSS-G's with the option off): no frame generation line
        Assert.Equal((null, false), (after.FrameGen, before.FrameGen));
        Assert.DoesNotContain(BeforeAfter.Compare(before, after).Notes, n => n.StartsWith("Frame generation"));
        var on = after with { FrameGen = true };   // generated frames were presented
        var notes = BeforeAfter.Compare(before, on).Notes;
        Assert.Equal(5, notes.Count);
        Assert.Contains("Frame generation was on in the after run only.", notes);
        Assert.Equal("Frame generation was on in the before run only.", BeforeAfter.Compare(on, before).Notes[^1]);
        Assert.Equal("Frame generation was on in both runs.", BeforeAfter.Compare(on, on).Notes[^1]);
        Assert.DoesNotContain(notes, n => n.Contains("cold"));
        Assert.Contains("AMD's DirectX 11 driver cache can't be checked, so the before run may not have been fully cold",
            BeforeAfter.Compare(before, after, notCold: true).Notes);   // a clear that didn't know a cache SCSKiller can't watch
        Assert.Contains("The game's DirectX 11 driver cache couldn't be told from another app's, so the after run may have used the before run's",
            BeforeAfter.Compare(before, after, afterHelped: true).Notes);
        Assert.Contains("The after run loaded 30 more pipelines from the game's own cache, not counted for SCSKiller", notes);
        Assert.Contains("The runs had different resolutions or display formats: 2560x1440 fmt 28 before, 1920x1080 fmt 28 after", notes);
        Assert.Contains("The in-game countdown was on in one run only", notes);
        Assert.Contains("The game didn't close normally in the before run", notes);
        Assert.All(notes, n => Assert.DoesNotContain(";", n));

        // the watcher saw the run exit: a game that terminates itself (no #end) closed normally; one size unknown: no warning
        var watched = new PlayWindow(DateTimeOffset.FromUnixTimeMilliseconds(Unix - 1000), DateTimeOffset.FromUnixTimeMilliseconds(Unix + 300_000));
        var quiet = BeforeAfter.Compare(Run(Unix, 10, 240, hits: 0, fastCompiles: 200, end: false, played: watched), Run(Unix + 1_000_000, 0, 240, swapChain: "1920,1080,28")).Notes;
        Assert.Equal(["Frame generation was off in both runs."], quiet);
    }

    /// <summary>Under 3 shader stutters before: already smooth, never "100% fewer"; no ready share known: that sentence is
    /// dropped.</summary>
    [Fact]
    public void An_already_smooth_game_gets_its_own_share_text()
    {
        var c = BeforeAfter.Compare(Run(Unix, 2, 300, hits: 1, fastCompiles: 250), Run(Unix + 1_000_000, 0, 300));
        Assert.True(c.AlreadySmooth);
        Assert.Matches(@"^Game was already smooth here: SCSKiller took it from 5\d% to 100% of pipelines ready\. scskiller\.com$", BeforeAfter.ShareText("Game", c));
        var unknown = c with { Before = c.Before with { ReadyShare = null } };
        Assert.Equal("Game was already smooth here: 2 → 0 shader compilation stutters in 5 minutes of play with SCSKiller. scskiller.com", BeforeAfter.ShareText("Game", unknown));
        var cold = c with { AlreadySmooth = false, After = c.After with { ReadyShare = null } };
        Assert.Equal("Shader compilation stutter in Game: 2 → 0 in 5 minutes of play after SCSKiller precompiled its shaders. scskiller.com", BeforeAfter.ShareText("Game", cold));
    }

    [Fact]
    public void The_share_text_stays_under_280_characters_whatever_the_name()
    {
        var c = BeforeAfter.Compare(Run(Unix, 20, 300, hits: 1, fastCompiles: 250), Run(Unix + 1_000_000, 1, 300));
        foreach (var name in new[] { "G", new string('x', 120), new string('y', 400), new string('龍', 200) })
        {
            var text = BeforeAfter.ShareText(name, c);
            Assert.True(BeforeAfter.PostLength(text) <= 280, $"{BeforeAfter.PostLength(text)}: {text}");
            Assert.EndsWith("scskiller.com", text);
            Assert.Contains("of pipelines were ready", text);
        }
        var smooth = c with { AlreadySmooth = true };
        Assert.True(BeforeAfter.PostLength(BeforeAfter.ShareText(new string('z', 400), smooth)) <= 280);
        Assert.Equal(266 + 2 + 1 + 23, BeforeAfter.PostLength(new string('a', 266) + "→ scskiller.com"));
        Assert.Equal(5 + 23 + 2 + 2 + 1 + 23, BeforeAfter.PostLength("S.T. GOG.com) → scskiller.com"));   // a domain in the name counts 23 too
        var gog = BeforeAfter.ShareText("S.T.A.L.K.E.R. 2: Heart of Chornobyl - Ultimate Edition (GOG.com, Ukrainian audio, high quality graphics, no frame generation)", c);
        Assert.True(BeforeAfter.PostLength(gog) <= 280, gog);
    }
}
