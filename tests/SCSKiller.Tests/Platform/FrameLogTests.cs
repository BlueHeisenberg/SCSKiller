using SCSKiller.Core;
using SCSKiller.Core.App;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Platform;

public class FrameLogTests(ITestOutputHelper output) : IDisposable
{
    readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "scskiller-frames-" + Guid.NewGuid().ToString("N")[..8])).FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    static FrameReport Report(long launch, double minutes, double low, int stutters) =>
        new(TimeSpan.FromMinutes(minutes), TimeSpan.FromSeconds(30), 1000, low,
            [.. Enumerable.Range(0, stutters).Select(i => new Hitch(TimeSpan.FromMinutes(1 + i * 0.01), 80, HitchCause.Shader)),
             new Hitch(TimeSpan.FromSeconds(5), 300, HitchCause.Loading), new Hitch(TimeSpan.FromMinutes(2), 60, HitchCause.Other)], [], launch);

    /// <summary>The frame history keeps each launch once (updated while it grows), the newest <see cref="FrameSessions.Keep"/>,
    /// newest first; it skips a log without a launch stamp, reads a broken file as none, and touches no other file of the
    /// game's folder (a pinned before-and-after snapshot under sessions\ stays).</summary>
    [Fact]
    public void The_frame_history_keeps_the_newest_launches_and_nothing_else()
    {
        var store = new AppStore(Path.Combine(_dir, "data"));
        var game = store.GameDir("steam:1");
        var pinned = Path.Combine(Directory.CreateDirectory(Path.Combine(game, "sessions")).FullName, "1.json");
        File.WriteAllText(pinned, "{\"pinned\":true}");
        Assert.Empty(FrameSessions.Read(store, "steam:1"));

        Assert.True(FrameSessions.Note(store, "steam:1", Report(1000, 10, 40, 3)));
        Assert.False(FrameSessions.Note(store, "steam:1", Report(1000, 10, 40, 3)));
        Assert.True(FrameSessions.Note(store, "steam:1", Report(1000, 12, 41, 4)));   // the same launch, grown
        Assert.Equal([new FrameSession(1000, TimeSpan.FromMinutes(12), 41, 4)], FrameSessions.Read(store, "steam:1"));
        Assert.False(FrameSessions.Note(store, "steam:1", Report(0, 5, 30, 1)));

        for (var t = 2000L; t < 2000 + FrameSessions.Keep * 1000; t += 1000) FrameSessions.Note(store, "steam:1", Report(t, 1, 60, 0));
        var kept = FrameSessions.Read(store, "steam:1");
        Assert.Equal(FrameSessions.Keep, kept.Count);
        Assert.Equal(FrameSessions.Keep * 1000 + 1000L, kept[0].LaunchUnixMs);
        Assert.DoesNotContain(kept, e => e.LaunchUnixMs == 1000);
        FrameSessions.Note(store, "steam:1", Report(1500, 1, 60, 0));   // older than all kept: not kept
        Assert.DoesNotContain(FrameSessions.Read(store, "steam:1"), e => e.LaunchUnixMs == 1500);
        Assert.Equal("{\"pinned\":true}", File.ReadAllText(pinned));
        Assert.Equal(["1.json"], Directory.GetFiles(Path.GetDirectoryName(pinned)!).Select(Path.GetFileName));

        File.WriteAllText(Path.Combine(game, "frame-history.json"), "[{\"LaunchUnixMs\": ");
        Assert.Empty(FrameSessions.Read(store, "steam:1"));
        File.WriteAllText(Path.Combine(game, "frame-history.json"), "[null, 5]");
        Assert.Empty(FrameSessions.Read(store, "steam:1"));
        File.WriteAllText(Path.Combine(game, "frame-history.json"),   // past DateTimeOffset's range, negative: left out
            "[{\"LaunchUnixMs\": 253402300800000, \"Duration\": \"00:01:00\"}, {\"LaunchUnixMs\": 5, \"Duration\": \"-00:01:00\"}, {\"LaunchUnixMs\": 7, \"Duration\": \"00:01:00\"}]");
        Assert.Equal([7L], FrameSessions.Read(store, "steam:1").Select(e => e.LaunchUnixMs));
        Assert.True(FrameSessions.Note(store, "steam:1", Report(9000, 1, 60, 0)));
        Assert.Equal([9000L, 7L], FrameSessions.Read(store, "steam:1").Select(e => e.LaunchUnixMs));
    }

    /// <summary>A launch record (unix ms, us since the recorder loaded) and frames that return at <paramref name="endsMs"/>,
    /// as the proxy writes them: microsecond deltas, a skip record for a gap past 28 bits.</summary>
    internal static byte[] Launch(long unixMs, long loadedUs, IEnumerable<double> endsMs, uint chain = 0)
    {
        var b = new List<byte>();
        void U32(uint v) => b.AddRange(BitConverter.GetBytes(v));
        U32(0xFFFFFFFF);
        foreach (var v in new[] { unixMs, loadedUs, 123456789L, 10_000_000L }) b.AddRange(BitConverter.GetBytes(v));
        long last = loadedUs;
        foreach (var e in endsMs)
        {
            long us = (long)Math.Round(e * 1000), d = us - last;
            last = us;
            while (d >> 28 != 0) { long ms = d / 1000; U32(0xF0000000u | (uint)ms); d -= ms * 1000; }
            U32(chain << 28 | (uint)d);
        }
        return [.. b];
    }

    static IEnumerable<double> Every10Ms(double from, double to) { for (var t = from; t < to; t += 10) yield return t; }

    /// <summary>A slow frame is judged on the creates that end in it, with their time in the frame: a 10.5 s create on a
    /// worker thread doesn't make the two slow frames it overlaps shader stutters, a create across two slow frames counts
    /// once, for the frame it ends in, and one that ends after a frame doesn't count for it.</summary>
    [Fact]
    public void A_create_counts_only_for_the_slow_frame_it_ends_in()
    {
        var ends = new List<double>();
        double t = 0;
        foreach (var (at, ms) in new[] { (10_260.0, 174.0), (15_950, 100), (20_000, 100), (20_100, 100), (30_000, 80) })
        {
            for (; t < at; t += 10) ends.Add(t);
            ends.Add(at);
            t = at + ms;
        }
        for (; t < 80_000; t += 10) ends.Add(t);
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, Launch(1_000_050, 0, ends));
        var csv = WarmedLaunch([(17_160, "R,0,0,10513.000"), (20_190, "S,0,0,180.000"), (30_150, "S,0,0,200.000")]);

        var r = FrameLog.Read(bin, csv)!;
        Assert.Equal([(10.26, HitchCause.Other), (15.95, HitchCause.Other), (20.0, HitchCause.Other), (20.1, HitchCause.Shader), (30.0, HitchCause.Other)],
            r.Hitches.Select(h => (h.At.TotalSeconds, h.Cause)));
    }

    /// <summary>A play cap (a before and after run's length) ends the report at the frame where that much play was reached:
    /// play time, frames, hitches, the 1% low, the graph and the creates of play count only frames before it.</summary>
    [Fact]
    public void A_play_cap_counts_only_the_play_before_it()
    {
        var ends = Every10Ms(0, 30_000).Append(30_000).Append(30_200).Concat(Every10Ms(30_210, 90_000))
            .Append(90_000).Append(90_200).Concat(Every10Ms(90_210, 120_000)).ToList();
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, Launch(1_000_050, 0, ends));
        var csv = WarmedLaunch([(30_150, "S,0,0,200.000"), (90_150, "S,0,0,200.000")]);

        var whole = FrameLog.Read(bin, csv)!;
        Assert.Equal(2, whole.Hitches.Count(h => h.Cause == HitchCause.Shader));
        Assert.Equal(2, whole.Creates!.PlayCompiles);

        var cut = FrameLog.Read(bin, csv, playCapMs: 60_000)!;
        double at = cut.Duration.TotalMilliseconds - FrameLog.QuitMs;   // the cut: the graph's play ends there
        Assert.InRange(at, 60_000, 60_500);
        Assert.InRange(cut.PlaySeconds, 60, 60.2);
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(cut.Hitches).At);
        Assert.Equal(1, cut.Creates!.PlayCompiles);
        Assert.Equal(ends.SkipLast(1).Count(s => s < at), cut.Frames);
        Assert.True(cut.Low1PctFps > whole.Low1PctFps);
        Assert.Equal(TimeSpan.Zero, cut.Startup);   // the first present, not the startup's end

        var longer = FrameLog.Read(bin, csv, playCapMs: 600_000)!;   // shorter than the cap: whole, its startup counted too
        Assert.Equal((whole.Duration, whole.Frames, whole.Hitches.Count), (longer.Duration, longer.Frames, longer.Hitches.Count));
        Assert.Equal(whole.PlaySeconds + whole.Startup.TotalSeconds, longer.PlaySeconds, 3);
    }

    /// <summary>A before and after run counts from its first present for the play cap, wherever the startup ends: a cold
    /// run's long precompile and a warm run's short start give the same window over the same frames.</summary>
    [Fact]
    public void A_before_and_after_run_counts_from_its_first_frame_whatever_its_startup()
    {
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, Launch(1_000_050, 0, Every10Ms(0, 200_000)));
        var reports = new List<(FrameReport Whole, FrameReport Cut)>();
        foreach (var precompile in new[] { 100, 0 })
        {
            var csv = WarmedLaunch(Enumerable.Range(0, precompile * 120).Select(i => (i / 120 * 1000 + i % 120 * 8 + 1.0, "S,0,0,5.000")));
            reports.Add((FrameLog.Read(bin, csv)!, FrameLog.Read(bin, csv, playCapMs: 60_000)!));
        }
        var (cold, warm) = (reports[0], reports[1]);
        Assert.True(cold.Whole.Startup > TimeSpan.FromSeconds(90) && warm.Whole.Startup < TimeSpan.FromSeconds(5));
        foreach (var (_, cut) in reports)
        {
            Assert.Equal(TimeSpan.Zero, cut.Startup);
            Assert.InRange(cut.PlaySeconds, 60, 60.02);
        }
        Assert.Equal(cold.Cut.Duration, warm.Cut.Duration);
        Assert.Equal(cold.Cut.Frames, warm.Cut.Frames);
    }

    /// <summary>A first frame of 8 s (an Unreal 5 game's load after its first present) is the first frame of a before and
    /// after run's play: in its play time, its average and its 1% low, a loading frame and not a stutter.</summary>
    [Fact]
    public void A_long_first_frame_is_in_a_before_and_after_run()
    {
        var bin = Path.Combine(_dir, FrameLog.FileName);
        var csv = WarmedLaunch([.. Enumerable.Range(0, 150).Select(i => (2_000 + i * 40.0, "S,1,1,0.500")), (5_000, "S,0,0,200.000")]);
        var reports = new List<FrameReport>();
        foreach (var first in new[] { 1_000.0, 8_990 })
        {
            File.WriteAllBytes(bin, Launch(1_000_050, 0, Every10Ms(9_000, 200_000).Prepend(first)));
            reports.Add(FrameLog.Read(bin, csv, playCapMs: 60_000)!);
        }
        var (slow, fast) = (reports[0], reports[1]);
        Assert.Equal(TimeSpan.FromMilliseconds(1_000), slow.Startup);
        Assert.InRange(slow.PlaySeconds, 60, 60.02);
        Assert.Equal(fast.Frames, slow.Frames + 799);   // 8 s of 10 ms frames the slow run has as one
        Assert.True(slow.Low1PctFps < fast.Low1PctFps / 2);
        Assert.Equal(HitchCause.Loading, Assert.Single(slow.Hitches, h => h.Ms >= 8_000).Cause);
        Assert.DoesNotContain(slow.Hitches, h => h.Cause is HitchCause.Shader or HitchCause.Other);
    }

    /// <summary>The csv's last #session line is found from the end, wherever the 64 KB chunks it is read in cut it, and reading
    /// from there gives that launch alone: the one a running game's frame log belongs to.</summary>
    [Fact]
    public void The_last_launch_is_read_from_the_end_of_the_csv()
    {
        var csv = Path.Combine(_dir, "tail.csv");
        var first = "#session,1,a.exe\n#clock,0.0\n" + string.Concat(Enumerable.Repeat("1.0,G,0,0,5.000,k,0.1,1,1\n", 4000));
        for (int pad = 65536 - 100; pad < 65536 - 20; pad++)
        {
            var text = first + "#session,2,b.exe\n#clock,0.0\n2.0,G,0,0,1.000,k,0.1,1,1\n#" + new string('x', pad) + "\n";
            File.WriteAllText(csv, text);
            long at = SessionLog.LastSessionAt(csv);
            Assert.Equal(text.LastIndexOf("\n#session,", StringComparison.Ordinal) + 1, at);
            Assert.Equal("b.exe", Assert.Single(SessionLog.Launches(csv, at)).Exe);
        }
        File.WriteAllText(csv, first);
        Assert.Equal(0, SessionLog.LastSessionAt(csv));
    }

    /// <summary>Slow frames get their cause from the creates that overlap them on the recorder's clock: a compile of 10 ms or
    /// more (not a RayQuery PSO at the driver's floor, not a ray tracing state object under SessionLog.StateObjectCompileMs) is a shader stutter,
    /// none is another hitch, the startup (with a burst following it closely; a slow frame with no create after it is play) or
    /// a load in play of 100 fast creates or more is loading; cold compiles filling a frame make it "loading, compiling
    /// shaders" in startup and a shader stutter in play, one in
    /// the last 10 s is quitting, and a frame of 5 s or more
    /// with no create is a pause, left out. The last launch of the game's exe is reported; a later one of another exe isn't.</summary>
    [Fact]
    public void Slow_frames_are_shader_compiles_other_hitches_or_loading()
    {
        var csv = new List<string> { "#session,1000000,Game.exe" };
        for (int i = 0; i < 200; i++) csv.Add($"{10 * i:0.0},S,1,1,50.000,{i:x40},0.010,7,0");   // startup: 0-2 s
        for (int i = 0; i < 5; i++) csv.Add($"{600 + 20 * i:0.0},S,1,1,200.000,{i + 4000:x40},0.010,7,0");   // cold compiles in its first slow frame
        csv.Add("15040.0,S,1,1,60.000,aa,0.010,8,0");    // compile inside the 15000 ms frame
        csv.Add("18040.0,C,1,1,40.000,bb,0.010,8,0");    // RayQuery floor inside the 18000 ms frame
        csv.Add("27040.0,R,1,1,23.500,cc,0.010,8,0");    // a cached state object inside the 27000 ms frame
        csv.Add("28040.0,A,0,0,57.500,ee,0.010,8,0");    // a single-material addition compiled inside the 28000 ms frame (The Witcher 3)
        csv.Add("23040.0,A,1,1,70.000,dd,0.010,8,0");    // a state object compiled inside the 23000 ms frame
        for (int i = 0; i < 120; i++) csv.Add($"{24100 + i:0.0},S,1,1,1.000,{i + 1000:x40},0.010,9,0");   // a load
        for (int i = 0; i < 110; i++) csv.Add($"{25100 + i:0.0},S,1,1,150.000,{i + 3000:x40},0.010,9,0");   // a load in play that compiles
        for (int i = 0; i < 150; i++) csv.Add($"{7100 + i * 0.5:0.0},S,1,1,1.000,{i + 2000:x40},0.010,9,0");   // a second startup burst
        // rows in time order here; out-of-order rows: Rows_slightly_out_of_order_stay_in_their_launch
        File.WriteAllLines(Path.Combine(_dir, "scskiller_creates.csv"),
            [csv[0], .. csv.Skip(1).OrderBy(r => double.Parse(r.Split(',')[0], System.Globalization.CultureInfo.InvariantCulture)), "#session,1000000000,Other.exe"]);

        var ends = new List<double>();
        void Frame(double ms) => ends.Add(ends[^1] + ms);
        ends.Add(500);
        Frame(500);                                             // at 500: startup
        foreach (var (at, ms) in new[] { (8500.0, 700.0), (15000, 80), (18000, 70), (21000, 120), (23000, 90), (24000, 300), (25000, 400), (27000, 60), (28000, 58), (29000, 400_000), (429_500, 80) })
        {
            foreach (var t in Every10Ms(ends[^1] + 10, at + 0.5)) ends.Add(t);
            ends[^1] = at;
            Frame(ms);
        }
        foreach (var t in Every10Ms(ends[^1] + 10, ends[^1] + 1000)) ends.Add(t);
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, [.. Launch(999_000, 100_000, [200, 300, 400]), .. Launch(1_000_050, 0, ends),
            .. Launch(1_000_000_010, 0, [1, 2, 3])]);

        var r = FrameLog.Read(bin, Path.Combine(_dir, "scskiller_creates.csv"), "game.exe", new HashSet<string> { "bb" })!;
        Assert.Equal(TimeSpan.FromMilliseconds(8000), r.Startup);   // the burst 5 s after the first quiet second; the frame with no create after it is play
        Assert.Equal(ends.Count - 1, r.Frames);
        Assert.Equal(TimeSpan.FromMilliseconds(ends[^1]), r.Duration);
        Assert.Equal([(500.0, 500.0, HitchCause.LoadingShaders), (8500, 700, HitchCause.Other), (15000, 80, HitchCause.Shader), (18000, 70, HitchCause.Other),
            (21000, 120, HitchCause.Other), (23000, 90, HitchCause.Shader), (24000, 300, HitchCause.Loading), (25000, 400, HitchCause.Shader), (27000, 60, HitchCause.Other), (28000, 58, HitchCause.Shader),
            (429_500, 80, HitchCause.Quitting)],
            r.Hitches.Select(h => (Math.Round(h.At.TotalMilliseconds, 3), Math.Round(h.Ms, 3), h.Cause)));
        var play = ends.Zip(ends.Skip(1), (a, b) => (a, ms: b - a)).Where(f => f.a >= 8000 && f.a < ends[^1] - 10_000 && f.ms < 5000).Select(f => f.ms).OrderDescending().ToList();
        var slow = play.Take(play.Count / 100).ToList();
        Assert.Equal(1000 * slow.Count / slow.Sum(), r.Low1PctFps, 6);
        Assert.Equal(FrameLog.GraphColumns, r.Peaks.Count);
        Assert.Equal(300, r.Peaks[(int)(24000 / ends[^1] * FrameLog.GraphColumns)], 3);   // each slice keeps its longest frame

        Assert.Null(FrameLog.Read(Path.Combine(_dir, "none.bin"), Path.Combine(_dir, "scskiller_creates.csv")));
    }

    /// <summary>A startup burst that never goes quiet before its creates end is startup through its last second: a slow
    /// frame there that cold compiles fill is loading, not a shader stutter.</summary>
    [Fact]
    public void A_burst_that_runs_to_the_last_create_is_startup_to_its_end()
    {
        var csv = new List<string> { "#session,1000000,Game.exe" };
        for (int i = 0; i < 200; i++) csv.Add($"{10 * i:0.0},S,0,0,{(i % 2 == 0 ? "900.000" : "0.500")}");
        File.WriteAllLines(Path.Combine(_dir, "scskiller_creates.csv"), csv);
        var ends = Every10Ms(0, 1500).Append(1800).Concat(Every10Ms(1810, 20_000)).ToList();
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, Launch(1_000_050, 0, ends));

        var r = FrameLog.Read(bin, Path.Combine(_dir, "scskiller_creates.csv"))!;
        Assert.Equal(TimeSpan.FromSeconds(2), r.Startup);
        Assert.Equal([(1490.0, 310.0, HitchCause.LoadingShaders)], r.Hitches.Select(h => (Math.Round(h.At.TotalMilliseconds, 3), Math.Round(h.Ms, 3), h.Cause)));
    }

    /// <summary>Cache hits in play at a steady 30 a second don't hold startup open: it ends with the burst's compiles, and a
    /// compile a minute later is in play, a shader stutter in the frame report and the session's one compile.</summary>
    [Fact]
    public void A_trickle_of_cache_hits_in_play_ends_the_startup()
    {
        var csv = new List<string> { "#session,1000000,Game.exe" };
        for (int i = 0; i < 200; i++) csv.Add($"{10 * i:0.0},S,0,0,{(i % 2 == 0 ? "900.000" : "0.500")}");
        for (int s = 2; s < 60; s++)
            for (int j = 0; j < 30; j++) csv.Add($"{s * 1000 + j * 33:0.0},S,1,1,{(s == 59 && j == 21 ? "200.000" : "0.500")}");   // a compile ending at 59.7 s
        csv.Add("#end,1080000");
        var path = Path.Combine(_dir, "scskiller_creates.csv");
        File.WriteAllLines(path, csv);
        var ends = Every10Ms(0, 59_500).Append(59_500).Append(59_800).Concat(Every10Ms(59_810, 80_000)).ToList();
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, Launch(1_000_050, 0, ends));

        var r = FrameLog.Read(bin, path)!;
        Assert.Equal(TimeSpan.FromSeconds(2), r.Startup);
        Assert.Equal([(59_500.0, 300.0, HitchCause.Shader)], r.Hitches.Select(h => (Math.Round(h.At.TotalMilliseconds, 3), Math.Round(h.Ms, 3), h.Cause)));
        var l = SessionLog.Read(path).Last!;
        Assert.Equal((1L, 200.0, 100L), (l.Compiles, l.WorstCompileMs, l.StartupCompiles));
    }

    /// <summary>A warmed run's precompile is all cache hits: its burst is startup, and the hits after it, with no compile,
    /// aren't.</summary>
    [Fact]
    public void A_warmed_precompile_of_cache_hits_is_the_startup()
    {
        var csv = new List<string> { "#session,1000000,Game.exe" };
        for (int i = 0; i < 600; i++) csv.Add($"{i * 10 / 3.0:0.0},S,1,1,0.500");   // 300 a second for 2 s
        for (int s = 2; s < 20; s++)
            for (int j = 0; j < 20; j++) csv.Add($"{s * 1000 + j * 50:0.0},S,1,1,0.500");
        var path = Path.Combine(_dir, "scskiller_creates.csv");
        File.WriteAllLines(path, csv);
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, Launch(1_000_050, 0, Every10Ms(0, 40_000)));
        Assert.Equal(TimeSpan.FromSeconds(2), FrameLog.Read(bin, path)!.Startup);
    }

    /// <summary>A launch's creates as the recorder writes them, in time order: a warmed precompile of 300 cache hits a second
    /// for 2 s, then <paramref name="rest"/> (t_ms, "kind,known,tuple_known,ms").</summary>
    string WarmedLaunch(IEnumerable<(double T, string Row)> rest)
    {
        var rows = Enumerable.Range(0, 600).Select(i => (T: i * 10 / 3.0, Row: "S,1,1,0.500")).Concat(rest).OrderBy(r => r.T);
        var path = Path.Combine(_dir, "scskiller_creates.csv");
        File.WriteAllLines(path, ["#session,1000000,Game.exe", .. rows.Select(r => FormattableString.Invariant($"{r.T:0.0},{r.Row}"))]);
        return path;
    }

    /// <summary>Frames of 10 ms to <paramref name="to"/> ms, but one of <paramref name="ms"/> from <paramref name="at"/>.</summary>
    string FramesWithOneSlow(double at, double ms, double to)
    {
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, Launch(1_000_050, 0, Every10Ms(0, at).Append(at).Append(at + ms).Concat(Every10Ms(at + ms + 10, to))));
        return bin;
    }

    static IEnumerable<(double, string)> PerSecond(int from, int to, int n, string row) =>
        Enumerable.Range(from, to - from).SelectMany(s => Enumerable.Range(0, n).Select(j => (s * 1000 + j * 1000.0 / n, row)));

    /// <summary>Loads from the game's pipeline library aren't compiles, in the frame report as in the last session: 30 a
    /// second at 4 ms after the startup don't hold it open, so both put a compile a minute later in play.</summary>
    [Fact]
    public void Library_loads_hold_neither_reports_startup_open()
    {
        var csv = WarmedLaunch([.. PerSecond(2, 60, 30, "s,1,1,4.000"), (59_693, "S,0,0,200.000")]);
        var r = FrameLog.Read(FramesWithOneSlow(59_500, 300, 80_000), csv)!;
        Assert.Equal(TimeSpan.FromSeconds(2), r.Startup);
        Assert.Equal([HitchCause.Shader], r.Hitches.Select(h => h.Cause));
        var l = SessionLog.Read(csv).Last!;
        Assert.Equal((1L, 200.0, 0L), (l.Compiles, l.WorstCompileMs, l.StartupCompiles));
    }

    /// <summary>A burst extends startup only when it starts within 10 s of the first quiet window, never chained from one
    /// burst to the next: traversal bursts every 10 s in play leave a compile in one of them in play.</summary>
    [Fact]
    public void Bursts_extend_startup_only_from_its_first_quiet_window()
    {
        var bursts = new[] { 10, 20, 30, 40, 50, 60 }.SelectMany(s => Enumerable.Range(0, 120)
            .Select(m => (s * 1000 + m * 8.0, s == 50 && m == 87 ? "S,0,0,200.000" : "S,1,1,0.500")));   // a compile ending at 50.696 s
        var csv = WarmedLaunch([.. PerSecond(2, 70, 30, "S,1,1,0.500"), .. bursts]);
        var r = FrameLog.Read(FramesWithOneSlow(50_500, 300, 80_000), csv)!;
        Assert.Equal(TimeSpan.FromSeconds(11), r.Startup);
        Assert.Equal([HitchCause.Shader], r.Hitches.Select(h => h.Cause));
        var l = SessionLog.Read(csv).Last!;
        Assert.Equal((1L, 200.0, 0L), (l.Compiles, l.WorstCompileMs, l.StartupCompiles));
    }

    /// <summary>Create timing alone can't tell a menu's background compiles from play: in doubt they count as play. A
    /// launch that compiles without a quiet window is all startup.</summary>
    [Fact]
    public void Background_compiles_after_the_startup_count_as_play_and_nonstop_compiling_is_all_startup()
    {
        var menu = PerSecond(2, 20, 30, "S,1,1,0.500").Concat(new[] { 6693.0, 10_693, 14_693, 18_693 }.Select(t => (t, "S,0,0,200.000")));
        var l = SessionLog.Read(WarmedLaunch([.. menu, (30_700, "S,0,0,200.000")])).Last!;
        Assert.Equal((5L, 0L), (l.Compiles, l.StartupCompiles));

        var csv = Path.Combine(_dir, "scskiller_creates.csv");
        File.WriteAllLines(csv, ["#session,1000000,Game.exe", .. Enumerable.Range(0, 200).Select(i => $"{10 * i:0.0},S,0,0,{(i % 2 == 0 ? "900.000" : "0.500")}"),
            .. PerSecond(2, 60, 30, "S,0,0,25.000").Select(c => $"{c.Item1:0.0},{c.Item2}"), "59990.0,S,0,0,200.000"]);
        l = SessionLog.Read(csv).Last!;
        Assert.Equal((0L, 1841L), (l.Compiles, l.StartupCompiles));
    }

    /// <summary>The slow-frame extension (a frame with no create right after startup) never moves startup over a compile
    /// the last session counts as play.</summary>
    [Fact]
    public void The_slow_frame_extension_stops_at_a_compile_in_play()
    {
        var csv = WarmedLaunch([(2300, "S,0,0,200.000"), (15_000, "S,1,1,0.500")]);
        var ends = Every10Ms(0, 2100).Append(2100).Append(2400).Concat(Every10Ms(2410, 3000)).Append(3000).Append(6000).Concat(Every10Ms(6010, 20_000));
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, Launch(1_000_050, 0, ends));
        var r = FrameLog.Read(bin, csv)!;
        Assert.Equal(TimeSpan.FromSeconds(2), r.Startup);
        Assert.Equal([(2100.0, HitchCause.Shader), (3000, HitchCause.Other)], r.Hitches.Select(h => (Math.Round(h.At.TotalMilliseconds, 3), h.Cause)));
        Assert.Equal((1L, 0L), (SessionLog.Read(csv).Last!.Compiles, SessionLog.Read(csv).Last!.StartupCompiles));
    }

    /// <summary>The 10 s for a burst to extend startup count from the first quiet point in real time: a burst starting
    /// 10.5 s after it is play.</summary>
    [Fact]
    public void A_burst_starting_past_the_10_s_is_play()
    {
        var csv = WarmedLaunch(Enumerable.Range(0, 100).Select(i => (12_500 + i * 0.1, "S,0,0,100.000")));
        Assert.Equal((100L, 0L), (SessionLog.Read(csv).Last!.Compiles, SessionLog.Read(csv).Last!.StartupCompiles));
    }

    /// <summary>A launch that quits while still busy has no quiet window: the seconds after its end aren't quiet ones, so
    /// it is all startup.</summary>
    [Fact]
    public void A_launch_that_ends_while_busy_is_all_startup()
    {
        var csv = Path.Combine(_dir, "scskiller_creates.csv");
        File.WriteAllLines(csv, ["#session,2000,Game.exe", .. PerSecond(0, 60, 30, "S,0,0,25.000").Select(c => $"{c.Item1:0.0},{c.Item2}"),
            "60000.0,S,0,0,25.000", "60033.3,S,0,0,25.000", "#end,62050"]);
        var l = SessionLog.Read(csv).Last!;
        Assert.Equal((0L, 1802L), (l.Compiles, l.StartupCompiles));
    }

    /// <summary>A launch that compiled without a quiet window has no play: the game page says so in place of its stutters
    /// and a 1% low of 0 fps. A launch with play keeps both.</summary>
    [Fact]
    public void A_launch_that_compiled_the_whole_time_says_so_instead_of_a_1_percent_low()
    {
        var csv = Path.Combine(_dir, "scskiller_creates.csv");
        File.WriteAllLines(csv, ["#session,1000000,Game.exe", .. PerSecond(0, 120, 30, "S,0,0,25.000").Select(c => FormattableString.Invariant($"{c.Item1:0.0},{c.Item2}"))]);
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, Launch(1_000_050, 0, Every10Ms(0, 120_000)));
        var r = FrameLog.Read(bin, csv)!;
        Assert.Equal(0, r.PlaySeconds);
        Assert.Equal("2 min, loading or compiling shaders the whole time.", Format.Frames(r, []));
        Assert.Equal("loading or compiling shaders the whole time", Format.Session(FrameSessions.Of(r)));

        var played = FrameLog.Read(FramesWithOneSlow(10_000, 200, 70_000), WarmedLaunch([(15_000, "S,1,1,0.500")]))!;
        Assert.True(played.PlaySeconds > 0);
        Assert.Equal($"1 min: no stutters from shader compiles, 1 other hitch, 1% low {played.Low1PctFps:0} fps.", Format.Frames(played, []));
        Assert.Equal($"1% low {played.Low1PctFps:0} fps · no stutters", Format.Session(FrameSessions.Of(played)));
    }

    /// <summary>One convention for both reports: a compile ending exactly at the end of startup, and the frame it fills,
    /// are startup's.</summary>
    [Fact]
    public void A_compile_ending_at_the_startup_boundary_is_startup_in_both_reports()
    {
        var csv = WarmedLaunch([(2000, "S,0,0,200.000"), (15_000, "S,1,1,0.500")]);
        var r = FrameLog.Read(FramesWithOneSlow(1800, 200, 20_000), csv)!;
        Assert.Equal(TimeSpan.FromSeconds(2), r.Startup);
        Assert.Equal([HitchCause.LoadingShaders], r.Hitches.Select(h => h.Cause));
        Assert.Equal((0L, 1L), (SessionLog.Read(csv).Last!.Compiles, SessionLog.Read(csv).Last!.StartupCompiles));
    }

    /// <summary>Both reports split the csv into launches the same way: rows after a launch's #end are another process's, a
    /// launch without a stamp, which the frame report doesn't match and the last session reads on its own.</summary>
    [Fact]
    public void Rows_after_an_end_marker_are_a_launch_of_their_own_in_both_reports()
    {
        var csv = Path.Combine(_dir, "scskiller_creates.csv");
        File.WriteAllLines(csv, ["#session,1000000,Game.exe", .. Enumerable.Range(0, 200).Select(i => $"{10 * i:0.0},S,0,0,{(i % 2 == 0 ? "900.000" : "0.500")}"),
            "#end,1060000", "61000.0,S,0,0,200.000"]);
        var r = FrameLog.Read(FramesWithOneSlow(60_900, 300, 80_000), csv)!;
        Assert.Equal(TimeSpan.FromSeconds(2), r.Startup);
        Assert.Equal([HitchCause.Other], r.Hitches.Select(h => h.Cause));   // the compile after #end isn't this launch's
        var (last, exe, _) = SessionLog.Read(csv);
        Assert.Equal(1, last!.Requests);
        Assert.Equal("Game.exe", exe!.Name);
    }

    /// <summary>The csv's times count from the recorder's load, its stamps from the first device: #clock puts the stamps on
    /// the csv's clock, so an #end 6 s after a #session taken at t_ms 20 s ends the launch at 26 s.</summary>
    [Fact]
    public void Unix_stamps_go_onto_the_recorders_clock()
    {
        var csv = Path.Combine(_dir, "scskiller_creates.csv");
        string[] rows = [.. PerSecond(20, 22, 30, "S,0,0,25.000").Select(c => $"{c.Item1:0.0},{c.Item2}"), "22000.0,S,0,0,25.000", "22033.0,S,0,0,25.000"];
        File.WriteAllLines(csv, ["#session,1020000,Game.exe", "#clock,20000.0", .. rows, "#end,1026000,26000.0"]);
        Assert.Equal((1L, TimeSpan.FromSeconds(26)), (SessionLog.Read(csv).Last!.Compiles, SessionLog.Read(csv).Last!.Duration));
        File.WriteAllLines(csv, ["#session,1020000,Game.exe", "#clock,20000.0", .. rows, "#end,1026000"]);   // #end's stamp alone
        Assert.Equal(1, SessionLog.Read(csv).Last!.Compiles);
        var played = new PlayWindow(DateTimeOffset.FromUnixTimeMilliseconds(1_019_000), DateTimeOffset.FromUnixTimeMilliseconds(1_026_000));
        File.WriteAllLines(csv, ["#session,1020000,Game.exe", "#clock,20000.0", .. rows]);   // no #end: the watched exit
        Assert.Equal(1, SessionLog.Read(csv, played: played).Last!.Compiles);
    }

    /// <summary>A recorder that writes #clock stamps the csv launch and the frames file alike: a frames file of an earlier
    /// launch, 6 s before, isn't this launch's, so its frames don't lend this one a quiet window it never had.</summary>
    [Fact]
    public void A_launch_never_borrows_another_launchs_frames()
    {
        var csv = Path.Combine(_dir, "scskiller_creates.csv");
        File.WriteAllLines(csv, ["#session,1000000,Game.exe", "#clock,10.0", .. Every10Ms(10, 5000).Select(t => $"{t:0.0},S,1,1,0.500"), "#end,1005000,5000.0",
            "#session,1006000,Game.exe", "#clock,10.0", .. Enumerable.Range(0, 600).Select(i => $"{10 + i * 10 / 3.0:0.0},S,1,1,0.500"),
            "2033.0,S,0,0,200.000", "#end,1008050,2050.0"]);
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, Launch(1_000_000, 10_000, Every10Ms(10, 5600)));   // the first launch's, past the second's 2-5 s window
        var frames = FrameLog.Read(bin, csv);
        var l = SessionLog.Read(csv, frames: frames).Last!;
        Assert.Equal((0L, 1L), (l.Compiles, l.StartupCompiles));   // it quit busy: all startup
    }

    /// <summary>Frame generation switched on 1 s in: the game's original swap chain until then, then frame generation's
    /// swap chain (tagged) has the game's frames, and the swap chain it presents twice as often has the displayed ones.
    /// The report is the game's frames; the displayed frames are every other swap chain's, with their times. A tag no
    /// reader knows yet is skipped.</summary>
    [Fact]
    public void Frame_generation_reports_the_games_frames_and_keeps_the_displayed()
    {
        var (bytes, events) = FrameGenerationLaunch([0xF0000000u | 20]);
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, bytes);
        var r = FrameLog.Read(bin, Path.Combine(_dir, "none.csv"))!;
        Assert.Equal(99 + 100 - 1, r.Frames);
        Assert.Equal(99 + 200, r.DisplayedFrames);
        Assert.Equal(events.Where(e => e.Chain != 2).Select(e => Math.Round(e.T, 3)), r.Displayed!.Select(t => Math.Round(t, 3)));
        Assert.Equal(1992, r.Duration.TotalMilliseconds, 3);   // the last game frame: no time from the tags
        File.WriteAllBytes(bin, Launch(1_000_000, 0, Every10Ms(10, 1000)));
        var plain = FrameLog.Read(bin, Path.Combine(_dir, "none.csv"))!;   // no frame generation
        Assert.Equal((0L, null), (plain.DisplayedFrames, plain.Displayed));
    }

    /// <summary>Frame generation's swap chain made again 2 s in (a resize): both tagged swap chains are the game's frames,
    /// and the swap chain they present stays the displayed one.</summary>
    [Fact]
    public void Frame_generation_made_again_keeps_both_its_swap_chains_as_the_games_frames()
    {
        var (bytes, _) = FrameGenerationLaunch(again: true);
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, bytes);
        var r = FrameLog.Read(bin, Path.Combine(_dir, "none.csv"))!;
        Assert.Equal(99 + 100 + 100 - 1, r.Frames);
        Assert.Equal(99 + 400, r.DisplayedFrames);
        Assert.Equal(2992, r.Duration.TotalMilliseconds, 3);
    }

    /// <summary>A downgraded app (1.2's reader, below) on a frame log with frame generation's tag: the tag is a gap of 2 ms
    /// to it, so every frame is there, on its own swap chain, at most 2 ms late.</summary>
    [Fact]
    public void A_reader_from_before_tags_takes_the_frame_generation_tag_for_a_tiny_gap()
    {
        var (bytes, events) = FrameGenerationLaunch();
        var old = Read12(bytes);
        foreach (var chain in new uint[] { 0, 1, 2 })
        {
            var want = events.Where(e => e.Chain == chain).Select(e => e.T).ToList();
            Assert.Equal(want.Count, old[chain].Count);
            Assert.All(want.Zip(old[chain]), p => Assert.InRange(p.Second - p.First, -0.001, 2.001));
        }
    }

    /// <summary>A launch as the proxy writes it with frame generation switched on 1 s in: swap chain 0 every 10 ms until
    /// then, then frame generation's (2, tagged before its first frame) every 10 ms and the one it presents (1) every 5 ms.
    /// <paramref name="again"/>: frame generation's swap chain made again at 2 s (3, tagged), 1 s more of both.</summary>
    static (byte[] Bytes, List<(double T, uint Chain)> Events) FrameGenerationLaunch(uint[]? moreTags = null, bool again = false)
    {
        var events = Every10Ms(10, 1000).Select(t => (T: t, Chain: 0u)).Concat(Every10Ms(1000, 2000).Select(t => (T: t + 2, Chain: 2u)))
            .Concat(Enumerable.Range(0, again ? 400 : 200).Select(i => (T: 1001 + 5.0 * i, Chain: 1u)))
            .Concat(again ? Every10Ms(2000, 3000).Select(t => (T: t + 2, Chain: 3u)) : []).OrderBy(e => e.T).ToList();
        var b = new List<byte>();
        void U32(uint v) => b.AddRange(BitConverter.GetBytes(v));
        U32(0xFFFFFFFF);
        foreach (var v in new[] { 1_000_000L, 0L, 123456789L, 10_000_000L }) b.AddRange(BitConverter.GetBytes(v));
        long last = 0;
        var tagged = new HashSet<uint>();
        foreach (var (t, chain) in events)
        {
            if (chain >= 2 && tagged.Add(chain))
            {
                U32(0xF0000000u | chain);
                foreach (var tag in moreTags ?? []) U32(tag);
            }
            long us = (long)Math.Round(t * 1000);
            U32(chain << 28 | (uint)(us - last));
            last = us;
        }
        return ([.. b], events);
    }

    /// <summary>1.2's FrameLog.ReadFrames loop, every swap chain's frames of the last launch.</summary>
    internal static Dictionary<uint, List<double>> Read12(byte[] data)
    {
        long t = 0;
        double start = 0;
        Dictionary<uint, List<double>>? chains = null;
        for (int i = 0; i + 4 <= data.Length; i += 4)
        {
            uint r = BitConverter.ToUInt32(data, i);
            if (r == 0xFFFFFFFF)
            {
                if (i + 36 > data.Length) break;
                start = BitConverter.ToInt64(data, i + 12) / 1000.0;
                (t, chains) = (0, []);
                i += 32;
            }
            else if (chains == null) continue;
            else if (r >> 28 == 15) t += (r & 0x0FFFFFFF) * 1000L;
            else
            {
                t += r & 0x0FFFFFFF;
                if (!chains.TryGetValue(r >> 28, out var c)) chains[r >> 28] = c = [];
                c.Add(start + t / 1000.0);
            }
        }
        return chains ?? [];
    }

    /// <summary>Another exe's launch after the game's isn't the game's last session, as it isn't its frame report.</summary>
    [Fact]
    public void The_last_session_is_the_exes_own()
    {
        var csv = Path.Combine(_dir, "scskiller_creates.csv");
        File.WriteAllLines(csv, ["#session,1000000,Game.exe", "1.0,S,1,1,0.500", "#end,1060000", "#session,2000000,Other.exe", "1.0,S,0,0,25.000", "#end,2060000"]);
        var l = SessionLog.Read(csv, "game.exe").Last!;
        Assert.Equal((1L, 0L), (l.Requests, l.Compiles));
        Assert.Equal(1, SessionLog.Read(csv).Last!.Compiles);   // no exe given: the last launch, whoever's
    }

    /// <summary>A slow load from the game's own pipeline library isn't a compile, in the frame report as in the session.</summary>
    [Fact]
    public void A_library_load_is_no_shader_stutter()
    {
        var csv = WarmedLaunch([(30_100, "s,1,1,200.000"), (50_000, "S,1,1,0.500")]);
        var r = FrameLog.Read(FramesWithOneSlow(29_900, 300, 60_000), csv)!;
        Assert.Equal([HitchCause.Other], r.Hitches.Select(h => h.Cause));
        Assert.Equal((1L, 0L), (SessionLog.Read(csv).Last!.FromGameLibrary, SessionLog.Read(csv).Last!.Compiles));
    }

    /// <summary>A shader freeze of 5 s or more is play, in the 1% low; only a frame of 5 s with no compile (a pause) is
    /// left out, but in a before and after run, which counts everything.</summary>
    [Fact]
    public void A_long_shader_freeze_counts_in_the_1_percent_low()
    {
        var csv = WarmedLaunch([(36_000, "S,0,0,6000.000"), (60_000, "S,1,1,0.500")]);
        var r = FrameLog.Read(FramesWithOneSlow(30_000, 6000, 80_000), csv)!;
        Assert.Equal([HitchCause.Shader], r.Hitches.Select(h => h.Cause));
        Assert.True(r.Low1PctFps < 20, $"1% low {r.Low1PctFps}");
        var paused = FrameLog.Read(FramesWithOneSlow(30_000, 6000, 80_000), WarmedLaunch([(60_000, "S,1,1,0.500")]))!;
        Assert.Empty(paused.Hitches);
        Assert.True(paused.Low1PctFps > 90, $"1% low {paused.Low1PctFps}");
        var counted = FrameLog.Read(FramesWithOneSlow(30_000, 6000, 80_000), WarmedLaunch([(60_000, "S,1,1,0.500")]), playCapMs: 600_000)!;
        Assert.Equal([HitchCause.Other], counted.Hitches.Select(h => h.Cause));
        Assert.True(counted.Low1PctFps < 20, $"1% low {counted.Low1PctFps}");
        Assert.Equal(paused.PlaySeconds + paused.Startup.TotalSeconds + 6, counted.PlaySeconds, 3);
    }

    /// <summary>An ordinary hitch after the startup is play, however close: a hitch every second leaves the startup at the
    /// shared boundary, and never chains it on.</summary>
    [Fact]
    public void Hitches_after_the_startup_never_chain_into_it()
    {
        var csv = WarmedLaunch([]);
        var ends = new List<double>();
        for (double t = 0; t < 80_000; t += 10)
            if (t % 1000 != 0 || t < 3000 || t > 59_000) ends.Add(t);
            else { ends.Add(t); ends.Add(t + 100); t += 100; }   // a 100 ms frame starting at every second from 3 s to 59 s
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, Launch(1_000_050, 0, ends));
        var r = FrameLog.Read(bin, csv)!;
        Assert.Equal(TimeSpan.FromSeconds(2), r.Startup);
        Assert.Equal(57, r.Hitches.Count(h => h.Cause == HitchCause.Other));
        Assert.True(r.Low1PctFps > 0);
    }

    /// <summary>A slow frame with no create that the startup boundary falls inside is startup's to its end, within the 10 s
    /// after the first quiet point that bursts get; one past it, or one after the boundary, is play.</summary>
    [Theory]
    [InlineData(1998, 1000, 2998, "Loading")]     // after the precompile's last create, across the 2 s boundary: startup to its end
    [InlineData(1998, 11_000, 2000, "")]          // would end past 2 s + 10 s: play (a pause, so no hitch)
    [InlineData(3000, 100, 2000, "Other")]        // a hitch at 3 s: play
    [InlineData(4000, 26_000, 2000, "")]          // 4-30 s: play (a pause)
    public void A_slow_frame_extends_the_startup_only_across_its_boundary_and_within_the_allowance(double at, double ms, double startup, string cause)
    {
        var csv = WarmedLaunch([(70_000, "S,1,1,0.500")]);
        var r = FrameLog.Read(FramesWithOneSlow(at, ms, 90_000), csv)!;
        Assert.Equal(TimeSpan.FromMilliseconds(startup), r.Startup);
        Assert.Equal(cause == "" ? [] : [Enum.Parse<HitchCause>(cause)], r.Hitches.Select(h => h.Cause));
    }

    /// <summary>A burst within the 10 s after the first quiet point extends startup no further than those 10 s, and not
    /// at all when a compile in play came before it.</summary>
    [Fact]
    public void Bursts_stay_inside_the_allowance_and_after_no_play_compile()
    {
        var late = WarmedLaunch([.. PerSecond(11, 20, 100, "S,0,0,25.000"), (90_000, "S,1,1,0.500")]);
        var l = SessionLog.Read(late).Last!;
        Assert.Equal((101L, 799L), (l.StartupCompiles, l.Compiles));   // the 11 s burst to 12 s (the one ending at 12.000 s too), the rest play
        var toExit = WarmedLaunch([.. PerSecond(11, 30, 100, "S,0,0,25.000")]);
        Assert.Equal(101, SessionLog.Read(toExit).Last!.StartupCompiles);   // runs to the exit: still 12 s, not infinity

        var after = WarmedLaunch([(3300, "S,0,0,200.000"), .. PerSecond(8, 9, 100, "S,1,1,0.500"), (60_000, "S,1,1,0.500")]);
        Assert.Equal((1L, 0L), (SessionLog.Read(after).Last!.Compiles, SessionLog.Read(after).Last!.StartupCompiles));
        var r = FrameLog.Read(FramesWithOneSlow(3100, 300, 70_000), after)!;
        Assert.Equal(TimeSpan.FromSeconds(2), r.Startup);
        Assert.Equal([HitchCause.Shader], r.Hitches.Select(h => h.Cause));
    }

    /// <summary>Lone creates before a precompile (a splash screen's) don't start the startup's clock: it counts from the
    /// first busy second, so a precompile running 12 s is startup to its end, and a frame it fills is loading.</summary>
    [Fact]
    public void Startup_counts_from_the_first_busy_second()
    {
        var csv = Path.Combine(_dir, "scskiller_creates.csv");
        File.WriteAllLines(csv, ["#session,1000000,Game.exe", "500.0,G,0,0,0.300", "3000.0,C,0,0,0.300",
            .. PerSecond(5, 17, 150, "S,0,0,150.000").Concat(PerSecond(17, 60, 5, "S,1,1,0.500")).Select(c => $"{c.Item1:0.0},{c.Item2}")]);
        var r = FrameLog.Read(FramesWithOneSlow(12_000, 400, 70_000), csv)!;
        Assert.Equal(TimeSpan.FromSeconds(17), r.Startup);
        Assert.Equal([HitchCause.LoadingShaders], r.Hitches.Select(h => h.Cause));
        Assert.Equal((0L, 1800L), (SessionLog.Read(csv).Last!.Compiles, SessionLog.Read(csv).Last!.StartupCompiles));
    }

    /// <summary>A precompile starting more than 10 s after the first create doesn't start the startup: it is play.</summary>
    [Fact]
    public void A_precompile_past_10_s_from_the_first_create_is_play()
    {
        var csv = Path.Combine(_dir, "scskiller_creates.csv");
        File.WriteAllLines(csv, ["#session,1000000,Game.exe", "500.0,G,0,0,0.300",
            .. PerSecond(12, 20, 150, "S,0,0,150.000").Concat(PerSecond(20, 60, 5, "S,1,1,0.500")).Select(c => $"{c.Item1:0.0},{c.Item2}")]);
        Assert.Equal((1200L, 0L), (SessionLog.Read(csv).Last!.Compiles, SessionLog.Read(csv).Last!.StartupCompiles));
    }

    /// <summary>A create of 3-10 ms between the startup and a burst within its 10 s is as likely a hit slowed by
    /// contention: it doesn't make the burst play. One of 10 ms or more does.</summary>
    [Theory]
    [InlineData("5.000", 8000)]
    [InlineData("12.000", 2000)]
    [InlineData("10.000", 2000)]
    public void Only_a_compile_of_10_ms_before_a_burst_makes_it_play(string ms, double startup)
    {
        var csv = WarmedLaunch([(4000, $"S,0,0,{ms}"), .. PerSecond(7, 8, 150, "S,1,1,0.500"), (60_000, "S,1,1,0.500")]);
        Assert.Equal(TimeSpan.FromMilliseconds(startup), FrameLog.Read(FramesWithOneSlow(30_000, 100, 70_000), csv)!.Startup);
    }

    /// <summary>A slow frame in play whose compiles ran on 8 threads or more, none of them one that presents, is a parallel
    /// precache or load, even when cold compiles fill it; fewer threads, a create on the presenting thread, or a recorder
    /// that doesn't write threads make it a shader stutter. Cache hits on many threads stay another hitch.</summary>
    [Theory]
    [InlineData(8, -1, "Loading")]
    [InlineData(8, 3, "Shader")]
    [InlineData(7, -1, "Shader")]
    [InlineData(0, -1, "Shader")]   // tid,presents not written
    [InlineData(8, -1, "Other", "0.500")]   // cache hits only
    public void A_parallel_load_in_play_is_loading(int threads, int presenter, string cause, string ms = "150.000")
    {
        var creates = Enumerable.Range(0, Math.Max(threads, 8)).Select(i => (30_050.0 + i * 0.1,
            $"S,0,0,{ms}" + (threads == 0 ? "" : $",{i + 4000:x40},0.010,{100 + i % threads},{(i == presenter ? 1 : 0)}")));
        var csv = WarmedLaunch([.. creates, (60_000, "S,1,1,0.500")]);
        var r = FrameLog.Read(FramesWithOneSlow(30_000, 100, 70_000), csv)!;
        Assert.Equal([Enum.Parse<HitchCause>(cause)], r.Hitches.Select(h => h.Cause));
    }

    /// <summary>A frame is play's by its end, in its cause, the play counts and the 1% low alike: one across the startup
    /// boundary that a compile fills is a shader stutter in play and the 1% low.</summary>
    [Fact]
    public void A_frame_across_the_boundary_is_plays_everywhere()
    {
        var csv = WarmedLaunch([(2100, "S,0,0,200.000"), (60_000, "S,1,1,0.500")]);
        var r = FrameLog.Read(FramesWithOneSlow(1998, 202, 70_000), csv)!;
        Assert.Equal(TimeSpan.FromSeconds(2), r.Startup);
        var h = Assert.Single(r.Hitches);
        Assert.Equal(HitchCause.Shader, h.Cause);
        Assert.True(FrameLog.InPlay(h.At.TotalMilliseconds, h.Ms, r.Startup.TotalMilliseconds));
        Assert.True(r.Low1PctFps < 90, $"1% low {r.Low1PctFps}");   // the 202 ms frame is in it (100 fps without it)
    }

    /// <summary>An older recorder's frames (no #clock) belong to a launch only when it is the only one stamped within 10 s:
    /// two launches 6 s apart show no frames next to either.</summary>
    [Fact]
    public void Legacy_frames_near_two_launches_belong_to_neither()
    {
        var csv = Path.Combine(_dir, "scskiller_creates.csv");
        File.WriteAllLines(csv, ["#session,1000000,Game.exe", "10.0,S,1,1,0.500", "#end,1005000", "#session,1006000,Game.exe", "20.0,S,0,0,25.000", "#end,1008050"]);
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, Launch(1_000_050, 0, Every10Ms(0, 5000)));
        var frames = FrameLog.Read(bin, csv);
        SessionLog.Read(csv, out var match, frames: frames);
        Assert.False(match);
        File.WriteAllLines(csv, ["#session,1000000,Game.exe", "10.0,S,1,1,0.500", "#end,1005000", "#session,1020000,Game.exe", "20.0,S,0,0,25.000", "#end,1022050"]);
        SessionLog.Read(csv, out match, frames: FrameLog.Read(bin, csv));
        Assert.False(match);   // the frames are the first launch's, the session the second's
        File.WriteAllLines(csv, ["#session,1000000,Game.exe", "10.0,S,1,1,0.500", "#end,1005000"]);
        SessionLog.Read(csv, out match, frames: FrameLog.Read(bin, csv));
        Assert.True(match);
    }

    /// <summary>A frame of 5 s is a pause only when nothing compiled under it: a short compile keeps it in play.</summary>
    [Fact]
    public void A_long_frame_with_any_compile_is_no_pause()
    {
        var csv = WarmedLaunch([(31_000, "S,0,0,5.000"), (60_000, "S,1,1,0.500")]);
        var r = FrameLog.Read(FramesWithOneSlow(30_000, 6000, 80_000), csv)!;
        Assert.Equal([HitchCause.Other], r.Hitches.Select(h => h.Cause));
        Assert.True(r.Low1PctFps < 20, $"1% low {r.Low1PctFps}");
    }

    /// <summary>The frame report's length is the launch's shared end, as the last session's play time: an #end after the
    /// last frame counts.</summary>
    [Fact]
    public void The_frame_reports_length_is_the_launchs_end()
    {
        var csv = Path.Combine(_dir, "scskiller_creates.csv");
        File.WriteAllLines(csv, ["#session,1000000,Game.exe", "#clock,0.0", "10.0,S,1,1,0.500", "#end,1090000,90000.0"]);
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, Launch(1_000_000, 0, Every10Ms(0, 80_000)));
        var r = FrameLog.Read(bin, csv)!;
        Assert.Equal(TimeSpan.FromSeconds(90), r.Duration);
        Assert.Equal(r.Duration, SessionLog.Read(csv, frames: r).Last!.Duration);
    }

    /// <summary>The recorder stamps a row when its create returns, before the row's lock, so concurrent creates land a
    /// little out of order (The Witcher 3's csv: 68718.7 after 68718.8). A launch with a #session stays one launch: its
    /// frames still match it and every create counts.</summary>
    [Fact]
    public void Rows_slightly_out_of_order_stay_in_their_launch()
    {
        var csv = Path.Combine(_dir, "scskiller_creates.csv");
        File.WriteAllLines(csv, ["#session,1000000,Game.exe", "#clock,0.0", "10.0,S,1,1,0.500,aa", "60000.2,S,0,0,30.000,bb", "60000.1,S,0,0,40.000,cc",
            "60000.3,S,1,1,0.500,dd", "#end,1090000,90000.0"]);
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, Launch(1_000_000, 0, Every10Ms(0, 80_000)));
        var r = FrameLog.Read(bin, csv, "game.exe")!;
        var last = SessionLog.Read(csv, out var framesMatch, "game.exe", frames: r).Last!;
        Assert.True(framesMatch);
        Assert.Equal((4L, 2L), (last.Requests, last.Compiles));
        Assert.Equal(TimeSpan.FromSeconds(90), last.Duration);
        File.AppendAllLines(csv, ["#session,2000000,Other.exe"]);
        Assert.Equal([10.0, 60000.1, 60000.2, 60000.3], SessionLog.Launches(csv).First().Creates.Select(c => c.T));
        File.WriteAllLines(csv, ["10.0,S,1,1,0.500", "20.0,S,1,1,0.500", "5.0,S,1,1,0.500"]); // an older proxy's restart, no markers: two launches
        Assert.Equal(1L, SessionLog.Read(csv).Last!.Requests);
    }

    /// <summary>Play time is the summed play frames (startup, the quit window and pauses left out), the average FPS their
    /// count over it, and the create stats take only the report's launch from a csv of several, split at its startup: hits
    /// and compiles in play, PSO compiles in startup, library loads, state objects, a RayQuery PSO at its floor ready.</summary>
    [Fact]
    public void Play_time_and_create_stats_are_the_reports_launch_split_at_its_startup()
    {
        var csv = Path.Combine(_dir, "scskiller_creates.csv");
        string[] first = ["#session,900000,Game.exe", "#clock,0.0", "#swapchain,1920,1080,24", "10.0,S,0,0,25.000", "20.0,s,1,1,1.000", "#end,960000,60000.0"];
        var rows = Enumerable.Range(0, 600).Select(i => (T: i * 10 / 3.0, Row: "S,0,0,4.000"))
            .Concat([(20_000, "S,1,1,0.500"), (20_100, "S,0,0,30.000"), (20_200, "C,0,0,40.000,rq"), (20_300, "s,1,1,4.000"), (20_400, "R,1,1,10.000"),
                (20_500, "A,0,0,70.000"), (25_000, "S,1,1,0.500")]);
        File.WriteAllLines(csv, [.. first, "#session,1000000,Game.exe", "#clock,0.0", "#swapchain,2560,1440,28", "#future,1,2", "#overlay", "#swapchain,3840,2160,10",
            .. rows.OrderBy(r => r.T).Select(r => FormattableString.Invariant($"{r.T:0.0},{r.Row}")), "#end,1090000,90000.0"]);
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, Launch(1_000_000, 0, Every10Ms(0, 40_000).Append(46_000).Concat(Every10Ms(46_010, 90_000))));   // a 6 s pause at 40 s
        var r = FrameLog.Read(bin, csv, "Game.exe", new HashSet<string> { "rq" })!;
        Assert.Equal(TimeSpan.FromSeconds(2), r.Startup);
        Assert.Equal(90 - 2 - 10 - 6, r.PlaySeconds, 1);
        Assert.Equal(100, r.AvgFps, 1);
        Assert.Equal(new CreateStats(PlayHits: 3, PlayCompiles: 1, StartupCompiles: 600, Library: 1, StateObjectsReady: 1, StateObjectsCompiled: 1), r.Creates);
        Assert.Equal(0.75, r.Creates!.ReadyShare);
        var launch = SessionLog.Launches(csv).Last();
        Assert.Equal(("3840x2160 fmt 10", true, 1090000L), (launch.SwapChain, launch.Overlay, launch.End));
        Assert.Equal(("1920x1080 fmt 24", false), (SessionLog.Launches(csv).First().SwapChain, SessionLog.Launches(csv).First().Overlay));
        Assert.Equal(1, SessionLog.Read(csv, rayQuery: new HashSet<string> { "rq" }).Last!.Compiles);   // the extra # lines change nothing else
    }

    /// <summary>SILENT HILL: Townfall's recorder files, read only: the last launch has frames and at least one hitch.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void Townfall_last_launch()
    {
        var game = new SCSKiller.Core.Games.SteamSource().Discover().FirstOrDefault(g => g.Id == "steam:1636440");
        var dir = game == null ? "" : Path.GetDirectoryName(game.ExePath)!;
        if (!File.Exists(Path.Combine(dir, FrameLog.FileName))) return;
        var r = FrameLog.Read(Path.Combine(dir, FrameLog.FileName), Path.Combine(dir, "scskiller_creates.csv"), Path.GetFileName(game!.ExePath))!;
        output.WriteLine($"{r.Duration}, startup {r.Startup}, {r.Frames} frames, 1% low {r.Low1PctFps:0.0}");
        foreach (var h in r.Hitches) output.WriteLine($"  {h.At:mm\\:ss\\.f} {h.Ms,8:0.0} ms {h.Cause}");
        Assert.True(r.Frames > 0);
    }

    /// <summary>An hour at 300 fps (the recorder's file is a few MB; a full benchmark is a few minutes) with 41,000 creates:
    /// the game page's report and its graph columns cost far less CPU than the budget (measured about 0.15 s).</summary>
    [Fact]
    public void An_hour_of_frames_reads_within_its_cpu_budget()
    {
        var rnd = new Random(1);
        var ends = new List<double>(1_080_001) { 0 };
        for (int i = 1; i <= 1_080_000; i++) ends.Add(ends[^1] + (i % 2000 == 0 ? 80 + rnd.Next(200) : 3.33));
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, Launch(1_000_000, 0, ends));
        var csv = Path.Combine(_dir, "scskiller_creates.csv");
        File.WriteAllLines(csv, ["#session,1000000,Game.exe", "#clock,0",
            .. Enumerable.Range(0, 41_000).Select(i => FormattableString.Invariant($"{i * 87.8:0.0},S,1,1,{(i % 10 == 0 ? 40 : 1.2):0.000},{i:x40},0.010,7,0"))]);

        var cpu = ThreadCpuTime();
        var r = FrameLog.Read(bin, csv, "Game.exe")!;
        cpu = ThreadCpuTime() - cpu;
        output.WriteLine($"{cpu.TotalMilliseconds:0} ms cpu, {r.Frames:N0} frames, {r.Hitches.Count} hitches");
        Assert.Equal(1_080_000, r.Frames);
        Assert.Equal(FrameLog.GraphColumns, r.Peaks.Count);
        Assert.InRange(r.Peaks.Max(), 80, 280);   // each column keeps its longest frame: no spike averaged away
        Assert.True(cpu < TimeSpan.FromSeconds(2), $"{cpu.TotalMilliseconds:0} ms cpu");
    }

    /// <summary>A launch with frame generation, whose frames the recorder doesn't log, per second as its csv ran on AMD:
    /// startup 0-8 s, quiet logos, the game's own precompile at 22.5-55 s (60 creates in its first half second, then 110 or
    /// 310 a second, on 47 threads; 2,100 compiles of 100-819 ms), the title, a new game's loading screen at 126-132 s on 30
    /// threads, the intro, then play from 383.5 s: 234 creates on 3 threads, 4 compiles of 100 ms or more.
    /// <paramref name="presents"/>: the render thread has presented (a recorder that logs frames).</summary>
    string PrecompileLaunch(bool presents)
    {
        var rows = new List<string> { "#session,1000000,SHf-Win64-Shipping.exe", "#clock,0.0" };
        int key = 0;
        static bool Spread(int i, int n, int k) => (long)(i + 1) * k / n > (long)i * k / n;   // k of n, evenly
        static IEnumerable<double> Even(double from, double to, int n) => Enumerable.Range(0, n).Select(i => from + i * (to - from) / n);
        void Span(IEnumerable<double> times, int compiles, Func<int, double> compileMs, Func<int, long> tid)
        {
            var ts = times.ToList();
            for (int i = 0, j = 0; i < ts.Count; i++)
            {
                bool compile = Spread(i, ts.Count, compiles);
                long t = tid(i);
                rows.Add(FormattableString.Invariant($"{ts[i]:0.0},S,1,1,{(compile ? compileMs(j++) : 0.5):0.000},{key++:x40},0.010,{t},{(presents && t == 7 ? 1 : 0)}"));
            }
        }
        Span(Even(0, 8000, 1820), 1525, j => 5 + j % 60, i => 100 + i % 31);
        Span(Even(22_500, 23_000, 60).Concat(Enumerable.Range(23, 32).SelectMany(s => Even(s * 1000, s * 1000 + 999, (s % 2 == 0 ? 310 : 110) + (s == 54 ? 1 : 0)))),
            6500, j => j == 0 ? 819 : Spread(j, 6500, 2100) ? 100 + j % 700 : 5 + j % 90, i => 1000 + i % 47);
        Span(Even(77_000, 78_000, 20), 15, _ => 10, i => 7 + i % 3);
        Span(Even(126_000, 132_000, 1917), 1100, j => 5 + j % 80, i => 2000 + i % 30);
        Span(Even(140_000, 380_000, 27), 20, _ => 8, i => 7 + i % 2);
        int[] slow = [120, 150, 200, 240];
        Span(Even(383_500, 1_100_000, 234), 136, j => j % 34 == 0 ? slow[j / 34] : 5 + j % 40, i => 7 + i % 3);
        rows.Add("#end,2200000,1200000.0");
        var path = Path.Combine(_dir, "scskiller_creates.csv");
        File.WriteAllLines(path, rows);
        return path;
    }

    /// <summary>Without frames, a second of 100 creates or more on 8 threads or more, none that presents, is a precompile
    /// or load, and so is a second next to one on such threads: its compiles count with the startup's, not play's. With
    /// frames the same launch counts the same, and its slow frames there are loading.</summary>
    [Fact]
    public void A_precompile_or_load_after_startup_is_not_play()
    {
        var csv = PrecompileLaunch(presents: false);
        var l = SessionLog.Read(csv, "SHf-Win64-Shipping.exe").Last!;
        output.WriteLine($"{l.Compiles} in play (worst {l.WorstCompileMs} ms), {l.StartupCompiles} started or loaded, {l.FromGameLibrary + l.CacheHits} ready");
        Assert.Equal((10_799L, 171L, 9_125L, 1_503L, 240.0), (l.Requests, l.Compiles, l.StartupCompiles, l.FromGameLibrary + l.CacheHits, l.WorstCompileMs));
        // the first launch after a compile still counts every compile: what the compile left is what it measures
        var first = SessionLog.Read(csv, "SHf-Win64-Shipping.exe", DateTimeOffset.FromUnixTimeMilliseconds(999_000)).First!;
        Assert.Equal((1_503L, 9_296L), (first.Hits, first.Compiles));

        csv = PrecompileLaunch(presents: true);
        var bigs = SessionLog.Launches(csv).Single().Creates.Where(c => c.T > 383_000 && c.Ms >= 100).ToList();
        var ends = new List<double> { 0 };
        while (ends[^1] < 1_190_000)
        {
            double t = ends[^1], next = t + 16;
            foreach (var from in new[] { 22_500.0, 126_000 }) if (t < from && next > from) next = from;
            int big = bigs.FindIndex(c => c.T - c.Ms <= next && t < c.T);
            // 4 fps behind the progress bar and the loading screen; one slow frame under each of play's long compiles
            ends.Add(t is >= 22_500 and < 55_000 or >= 126_000 and < 132_000 ? t + 250 : big >= 0 ? bigs[big].T + 10 : next);
        }
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, Launch(1_000_000, 0, ends));
        var r = FrameLog.Read(bin, csv, "SHf-Win64-Shipping.exe")!;
        Assert.Equal(TimeSpan.FromSeconds(8), r.Startup);
        Assert.Equal(130 + 24, r.Hitches.Count(h => h.Cause == HitchCause.Loading));
        Assert.Equal(bigs.Count, r.Hitches.Count(h => h.Cause == HitchCause.Shader));
        Assert.Equal(130 + 24 + bigs.Count, r.Hitches.Count);
        Assert.Equal(l, SessionLog.Read(csv, out var match, "SHf-Win64-Shipping.exe", frames: r).Last);
        Assert.True(match);
    }

    /// <summary>Both the rate and the threads make a load: 99 creates a second on 8 threads, 100 on 7, or 100 with one on
    /// the presenting thread stay play.</summary>
    [Theory]
    [InlineData(100, 8, 0, 0L)]
    [InlineData(99, 8, 0, 297L)]
    [InlineData(100, 7, 0, 300L)]
    [InlineData(100, 8, 1, 300L)]
    public void A_load_takes_its_rate_and_threads(int perSecond, int threads, int presenting, long play)
    {
        var csv = WarmedLaunch(Enumerable.Range(0, 3 * perSecond).Select(i => (30_000 + i * 1000.0 / perSecond,
            FormattableString.Invariant($"S,0,0,20.000,{i:x40},0.010,{100 + i % threads},{(i % perSecond < presenting ? 1 : 0)}"))));
        var l = SessionLog.Read(csv).Last!;
        Assert.Equal((play, 3L * perSecond - play), (l.Compiles, l.StartupCompiles));
    }

    // Read runs synchronously on the calling thread, so this ignores machine load and other parallel tests.
    static TimeSpan ThreadCpuTime()
    {
        GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user);
        return TimeSpan.FromTicks(kernel + user);
    }

    [System.Runtime.InteropServices.DllImport("kernel32")] static extern nint GetCurrentThread();
    [System.Runtime.InteropServices.DllImport("kernel32")] static extern bool GetThreadTimes(nint thread, out long create, out long exit, out long kernel, out long user);
}
