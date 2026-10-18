using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Vendors;
using static SCSKiller.Core.BeforeAfterStage;

namespace SCSKiller.Tests.Platform;

// The before and after recording's state machine on the fake game: the watcher's clock and process list are the test's.
public partial class AppTests
{
    sealed class Pair
    {
        public required ScsKiller K;
        public required string Id, Exe, ExeDir, DriverCache;
        public readonly HashSet<string> Running = new(StringComparer.OrdinalIgnoreCase);
        // in the past: a compile's WarmedAt (the wall clock) is after every ArmedAt
        public DateTimeOffset At = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

        public BeforeAfterRecord? Record => K.Store.LoadGame(Id).BeforeAfter;
        public BeforeAfterInfo? Shown => K.Games.Single(s => s.Game.Id == Id).BeforeAfter;

        public void Poll(double seconds = 3)
        {
            At = At.AddSeconds(seconds);
            K.PollGames();
        }

        /// <summary>A launch of <paramref name="playSeconds"/> of play as the recorder writes it: a new frame log (it keeps the
        /// last launch only), a csv launch with the same stamp; the game fills its driver cache again.</summary>
        public long Launch(double playSeconds)
        {
            var stamp = At.AddSeconds(1).ToUnixTimeMilliseconds();
            var ends = Enumerable.Range(1, (int)((playSeconds + 15) * 1000 / 16)).Select(i => 10.0 + 16.0 * i);   // the last 10 s are the quit window
            File.WriteAllBytes(Path.Combine(ExeDir, FrameLog.FileName), FrameLogTests.Launch(stamp, 10_000, ends));
            File.AppendAllText(Path.Combine(ExeDir, "scskiller_creates.csv"), $"#session,{stamp},{Exe}\n#clock,10.0\n20.0,S,1,1,0.5\n");
            File.WriteAllBytes(DriverCache, new byte[65536]);
            return stamp;
        }

        public void Exit()
        {
            Running.Clear();
            for (int i = 0; i < ScsKiller.ExitPolls; i++) Poll();
        }

        public long Play(double playSeconds)
        {
            Running.Add(Exe);
            Poll();
            var stamp = Launch(playSeconds);
            for (double t = 0; t < playSeconds; t += 60) Poll(60);
            Exit();
            return stamp;
        }

        /// <summary>The queue is held (paused) except here: a compile the flow queues is seen waiting until the test lets it run.</summary>
        public async Task Compiled()
        {
            K.ResumeQueue();
            await K.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
            K.PauseQueue();
        }
    }

    /// <summary>The app starts again (the record and the game folder as the closed one left them).</summary>
    async Task Restart(Pair p, string driver = "100.01", bool scanAtStart = true)
    {
        var k = Killer(new FakeReader(Unreal), driver: driver);
        k.Settings = k.Settings with { ScanAtStart = scanAtStart };
        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));
        (k.Clock, k.RunningGameExes) = (() => p.At, () => p.Running.ToHashSet(StringComparer.OrdinalIgnoreCase));
        k.PauseQueue();
        await k.ScanAsync(default);
        p.K = k;
    }

    async Task<Pair> PairRig(IWarmer? warmer = null, Func<IReadOnlyList<DxgiAdapter>>? adapters = null)
    {
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        if (adapters != null) k.Adapters = adapters;
        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));
        var p = new Pair { K = k, Id = _game.Id, Exe = Path.GetFileName(_game.ExePath), ExeDir = _exeDir, DriverCache = CacheFile("0002a91d33333333.nvph", 65536) };
        (k.Clock, k.RunningGameExes) = (() => p.At, () => p.Running.ToHashSet(StringComparer.OrdinalIgnoreCase));
        await k.ScanAsync(default);
        var rec = k.Store.LoadGame(_game.Id);
        rec.CacheKeys.Add("33333333");   // learned: Clear cache deletes the driver's files
        k.Store.SaveGame(_game.Id, rec);
        p.Poll();   // the watcher's first poll: a later start has a known start
        k.PauseQueue();
        return p;
    }

    /// <summary>A game ready to compile and not running can start; the setting off, a pair in progress, or a "no shader
    /// stutter" verdict can't.</summary>
    [Fact]
    public async Task Before_and_after_starts_only_when_nothing_blocks_it()
    {
        var p = await PairRig();
        Assert.Null(p.K.BeforeAfterBlocked(p.Id));
        p.K.Settings = p.K.Settings with { BeforeAfter = false };
        Assert.Equal("Before and after recording is off in Settings", p.K.BeforeAfterBlocked(p.Id));
        Assert.Throws<InvalidOperationException>(() => p.K.StartBeforeAfter(p.Id, 5));
        Assert.Null(p.Record);
        p.K.Settings = p.K.Settings with { BeforeAfter = true };
        p.K.StartBeforeAfter(p.Id, 10);
        Assert.Equal("A before and after recording is already in progress", p.K.BeforeAfterBlocked(p.Id));
        p.Running.Add(p.Exe);
        p.Poll();
        Assert.Equal($"{_game.Name} is running", p.K.BeforeAfterBlocked(p.Id));
        try
        {
            Core.Games.GameVerdicts.Current = Core.Games.GameVerdicts.TryParse(Verdicts(Core.Games.GameVerdicts.NoStutterVerdict, "engine-precompiles", p.Id))!;
            await p.K.ScanAsync(default);
            Assert.Equal("Not for games without shader compilation stutter", p.K.BeforeAfterBlocked(p.Id));
        }
        finally { Core.Games.GameVerdicts.Current = Core.Games.GameVerdicts.Embedded; }
    }

    /// <summary>The game page's button with no pair: greyed with the reason while a pair can't start, hidden only with the
    /// setting off.</summary>
    [Fact]
    public async Task The_before_and_after_button_is_greyed_with_the_reason_when_a_pair_cant_start()
    {
        var p = await PairRig();
        (bool, bool, string) Button() => BeforeAfter.StartButton(p.K.Settings.BeforeAfter, p.K.BeforeAfterBlocked(p.Id));
        Assert.Equal((true, true, ""), Button());

        p.K.SetRecorderOverride(p.Id, RecorderOverride.Off);
        Assert.Equal((true, false, "Turn the recorder on for this game first"), Button());
        p.K.SetRecorderOverride(p.Id, RecorderOverride.Default);

        p.Running.Add(p.Exe);
        p.Poll();
        Assert.Equal((true, false, $"{_game.Name} is running"), Button());
        p.Exit();

        p.K.VacCheck = _ => true;
        p.K.RefreshGame(p.Id);
        Assert.Equal((true, false, "Not for games with anti-cheat"), Button());
        p.K.VacCheck = _ => false;
        p.K.RefreshGame(p.Id);

        try
        {
            Core.Games.GameVerdicts.Current = Core.Games.GameVerdicts.TryParse(Verdicts(Core.Games.GameVerdicts.NoStutterVerdict, "engine-precompiles", p.Id))!;
            await p.K.ScanAsync(default);
            Assert.Equal((true, false, "Not for games without shader compilation stutter"), Button());
        }
        finally { Core.Games.GameVerdicts.Current = Core.Games.GameVerdicts.Embedded; }
        await p.K.ScanAsync(default);
        Assert.Equal((true, true, ""), Button());

        p.K.Settings = p.K.Settings with { BeforeAfter = false };
        Assert.False(Button().Item1);
    }

    [Fact]
    public async Task Before_and_after_runs_from_a_cold_before_run_through_a_compile_to_a_report()
    {
        var warmer = new FakeWarmer();
        var p = await PairRig(warmer);
        p.K.StartBeforeAfter(p.Id, 5);
        Assert.False(File.Exists(p.DriverCache));
        var r = p.Record!;
        Assert.Equal((BeforeArmed, 5, p.At, p.K.DriverId), (r.Stage, r.Minutes, r.ArmedAt, r.DriverId));
        Assert.NotNull(r.Build);
        Assert.Equal(new BeforeAfterInfo(BeforeArmed, 5, null, 0), p.Shown);

        p.Running.Add(p.Exe);
        p.Poll();
        Assert.Equal(BeforePlaying, p.Shown!.Stage);
        var before = p.Launch(360);
        p.Poll(10);
        Assert.InRange(p.Shown!.PlayedSeconds, 300, 301);   // the countdown, from the frame log while the game runs, up to the run's length
        await p.K.ScanAsync(default);
        p.Exit();
        Assert.True(p.K.Games.Single().LastFrames!.PlaySeconds > 350);   // the game page's report of the same log isn't cut
        Assert.Equal((BeforeAfterStage.Compiling, before), (p.Record!.Stage, p.Record.Before));
        Assert.False(File.Exists(p.DriverCache));   // cleared again: the before run's own compiles never flatter the after run
        Assert.Equal("before", Snapshots.Read(p.K.Store, p.Id, before)!.Tag);

        await p.Compiled();
        Assert.Equal(new BeforeAfterInfo(AfterReady, 5, null, 0), p.Shown);
        Assert.Equal(GameStatus.Warmed, p.K.Games.Single().Status);
        Assert.Null(p.K.BeforeAfterReport(p.Id));

        p.K.ContinueBeforeAfter(p.Id);
        Assert.Equal((AfterArmed, p.At), (p.Record!.Stage, p.Record.ArmedAt));
        var after = p.Play(360);
        Assert.Equal((Done, before, after), (p.Record!.Stage, p.Record.Before, p.Record.After));
        Assert.Equal(new BeforeAfterInfo(Done, 5, null, 0), p.Shown);
        var report = p.K.BeforeAfterReport(p.Id)!;
        Assert.Equal((before, after), (report.Before.LaunchUnixMs, report.After.LaunchUnixMs));
        Assert.Equal("after", Snapshots.Read(p.K.Store, p.Id, after)!.Tag);
        Assert.Single(warmer.Started);

        p.K.RecordAfterAgain(p.Id);   // a new after run on a fresh compile
        Assert.Null(Snapshots.Read(p.K.Store, p.Id, after)!.Tag);
        await p.Compiled();
        Assert.Equal((AfterReady, before, (long?)null), (p.Record!.Stage, p.Record.Before, p.Record.After));
        Assert.Equal(2, warmer.Started.Count);
    }

    [Fact]
    public async Task Before_and_after_clears_again_and_rearms_after_a_short_before_run()
    {
        var p = await PairRig();
        p.K.StartBeforeAfter(p.Id, 5);
        var armed = p.Record!.ArmedAt;
        p.Play(120);
        var r = p.Record!;
        Assert.Equal((BeforeArmed, (long?)null), (r.Stage, r.Before));
        Assert.NotNull(r.Problem);
        Assert.True(r.ArmedAt > armed);
        Assert.Equal(r.Problem, p.Shown!.Problem);
        Assert.False(File.Exists(p.DriverCache));
        Assert.Empty(p.K.Queue);

        var before = p.Play(360);   // the retry counts
        Assert.Equal((BeforeAfterStage.Compiling, before), (p.Record!.Stage, p.Record.Before));
        await p.Compiled();
    }

    /// <summary>A key the clear didn't know (never seen, never compiled) whose files survived it: once the before run's game
    /// holds it, the run doesn't count, and the next clear deletes them. A key with no files before the clear is the run's own.</summary>
    [Fact]
    public async Task Before_and_after_plays_the_before_run_again_when_it_used_old_driver_cache_files()
    {
        var p = await PairRig();
        var old = CacheFile("0002a91d44444444.nvph", 65536);   // the game's other key, not learned yet: the clear leaves it
        p.K.StartBeforeAfter(p.Id, 5);
        Assert.True(File.Exists(old));
        Assert.Contains("44444444", p.Record!.Survivors!);
        Assert.DoesNotContain("33333333", p.Record!.Survivors!);
        void Holds(string key)   // what LearnWhilePlaying records when the game's process has the key's files open
        {
            var rec = p.K.Store.LoadGame(p.Id);
            (rec.GameKeys, rec.CacheKeys) = ([.. rec.GameKeys, key], [.. rec.CacheKeys, key]);
            p.K.Store.SaveGame(p.Id, rec);
        }
        p.Running.Add(p.Exe);
        p.Poll();
        p.Launch(360);
        Holds("44444444");
        p.Exit();
        var r = p.Record!;
        Assert.Equal((BeforeArmed, (long?)null, ScsKiller.OldCacheRedoNote), (r.Stage, r.Before, r.Problem));
        Assert.False(File.Exists(old));   // its key is known now: cleared
        Assert.DoesNotContain("44444444", r.Survivors!);

        p.Running.Add(p.Exe);
        p.Poll();
        var before = p.Launch(360);
        CacheFile("0002a91d55555555.nvph", 65536);   // a key the game makes this run: no files before the clear
        Holds("55555555");
        p.Exit();
        Assert.Equal((BeforeAfterStage.Compiling, before), (p.Record!.Stage, p.Record.Before));
        await p.Compiled();
    }

    /// <summary>Frame generation made its swap chain and the recorder has no hook on the game's frames (#frames,stopped): no
    /// frame times follow, so the play time never comes. Said while the game runs, and the run ends with it at the exit.</summary>
    [Fact]
    public async Task Before_and_after_ends_a_run_whose_frame_times_stopped_at_frame_generation()
    {
        var p = await PairRig();
        p.K.StartBeforeAfter(p.Id, 5);
        p.Running.Add(p.Exe);
        p.Poll();
        p.Launch(30);
        File.AppendAllText(Path.Combine(p.ExeDir, "scskiller_creates.csv"), "#framegen,1\n#frames,stopped\n");
        p.Poll(60);
        Assert.Equal((BeforePlaying, ScsKiller.FramesStoppedNote), (p.Record!.Stage, p.Record.Problem));
        Assert.Equal(ScsKiller.FramesStoppedNote, p.Shown!.Problem);
        p.Exit();
        var r = p.Record!;
        Assert.Equal((BeforeArmed, (long?)null, ScsKiller.FramesStoppedNote), (r.Stage, r.Before, r.Problem));
        Assert.Equal(ScsKiller.FramesStoppedNote, p.Shown!.Problem);
    }

    [Fact]
    public async Task Before_and_after_compiles_again_when_the_game_ran_during_the_compile()
    {
        var warmer = new FakeWarmer();
        var p = await PairRig(warmer);
        p.K.StartBeforeAfter(p.Id, 5);
        p.Play(360);
        Assert.Equal(BeforeAfterStage.Compiling, p.Record!.Stage);
        p.Running.Add(p.Exe);   // played outside the flow while the compile waits
        p.Poll();
        Assert.True(p.Record!.Dirty);
        p.Exit();
        Assert.Equal(BeforeAfterStage.Compiling, p.Record!.Stage);
        await p.Compiled();
        Assert.Equal(new BeforeAfterInfo(AfterReady, 5, ScsKiller.DirtyNote, 0), p.Shown);

        File.WriteAllBytes(p.DriverCache, new byte[65536]);
        p.K.ContinueBeforeAfter(p.Id);
        Assert.False(File.Exists(p.DriverCache));
        Assert.Equal((BeforeAfterStage.Compiling, false), (p.Record!.Stage, p.Record.Dirty));
        await p.Compiled();
        Assert.Equal(new BeforeAfterInfo(AfterReady, 5, null, 0), p.Shown);
        Assert.Equal(2, warmer.Started.Count);
    }

    [Fact]
    public async Task Before_and_after_is_void_after_a_driver_update()
    {
        var adapter = Gpu;
        var p = await PairRig(adapters: () => [Listed(adapter)]);
        p.K.StartBeforeAfter(p.Id, 5);
        var before = p.Play(360);
        await p.Compiled();
        Assert.Equal(AfterReady, p.Shown!.Stage);

        adapter = Gpu with { DriverVersion = "101.00", AdapterLuid = 2 };
        Assert.True(p.K.RedetectGpu());
        p.K.RefreshGame(p.Id);
        Assert.Equal(ScsKiller.DriverVoidNote, p.Shown!.Problem);
        Assert.Equal(ScsKiller.DriverVoidNote, Assert.Throws<InvalidOperationException>(() => p.K.ContinueBeforeAfter(p.Id)).Message);
        Assert.Null(p.K.BeforeAfterReport(p.Id));

        p.K.EndBeforeAfter(p.Id, compile: false);   // "Start over" ends it first
        Assert.Null(p.Record);
        Assert.Null(p.Shown);
        Assert.False(Snapshots.Read(p.K.Store, p.Id, before)!.Pinned);
        Assert.Null(p.K.BeforeAfterBlocked(p.Id));
    }

    [Fact]
    public async Task Before_and_after_waits_on_a_failed_compile_and_continue_compiles_again()
    {
        WarmResult R(WarmOutcome o, string? error = null) => new(o, 10000, 10000, 0, TimeSpan.FromSeconds(10), 1 << 20, "", error);
        var p = await PairRig(new ScriptedWarmer(R(WarmOutcome.Failed, "the driver crashed"), R(WarmOutcome.Completed)));
        p.K.StartBeforeAfter(p.Id, 5);
        p.Play(360);
        await p.Compiled();
        Assert.Equal(BeforeAfterStage.Compiling, p.Shown!.Stage);
        Assert.StartsWith("the driver crashed", p.Shown.Problem);

        p.K.ContinueBeforeAfter(p.Id);
        await p.Compiled();
        Assert.Equal(new BeforeAfterInfo(AfterReady, 5, null, 0), p.Shown);
    }

    [Fact]
    public async Task Before_and_after_loses_a_run_the_game_started_twice_in()
    {
        var p = await PairRig();
        p.K.StartBeforeAfter(p.Id, 5);
        p.Running.Add(p.Exe);
        p.Poll();
        p.Launch(360);
        p.Poll(60);
        p.Launch(360);   // the game restarted itself: the frame log holds the second launch only
        p.Poll(60);
        p.Exit();
        Assert.Equal(new BeforeAfterInfo(BeforeArmed, 5, ScsKiller.LostNote, 0), p.Shown);
        Assert.False(File.Exists(p.DriverCache));
        Assert.Empty(p.K.Queue);
    }

    [Fact]
    public async Task Before_and_after_ends_a_run_the_app_was_closed_for_at_the_next_start()
    {
        var p = await PairRig();
        p.K.StartBeforeAfter(p.Id, 5);
        p.Running.Add(p.Exe);
        p.Poll();
        var before = p.Launch(360);

        p.Running.Clear();   // the app closes, the game exits, the app starts again
        await Restart(p);
        Assert.Equal((BeforeAfterStage.Compiling, before), (p.Record!.Stage, p.Record.Before));
        Assert.False(File.Exists(p.DriverCache));
        await p.Compiled();
        Assert.Equal(AfterReady, p.Shown!.Stage);
    }

    [Fact]
    public async Task Before_and_after_notes_a_start_it_did_not_see_and_still_takes_a_later_one()
    {
        var p = await PairRig();
        p.K.StartBeforeAfter(p.Id, 5);
        p.Poll(240);
        Assert.Null(p.Shown!.Problem);
        p.Poll(61);
        Assert.Equal(new BeforeAfterInfo(BeforeArmed, 5, ScsKiller.NoStartNote, 0), p.Shown);
        Assert.Equal(ScsKiller.NoStartNote, p.Record!.Problem);

        p.Running.Add(p.Exe);
        p.Poll();
        Assert.Equal(new BeforeAfterInfo(BeforePlaying, 5, null, 0), p.Shown);
    }

    [Fact]
    public async Task Before_and_after_end_queues_a_compile_for_the_cleared_cache()
    {
        var p = await PairRig();
        p.K.StartBeforeAfter(p.Id, 5);
        p.K.EndBeforeAfter(p.Id, compile: true);
        Assert.Null(p.Record);
        Assert.Null(p.Shown);
        Assert.Contains(p.K.Queue, q => q.GameId == p.Id && q.Stage is not (QueueStage.Done or QueueStage.Failed or QueueStage.Stopped));
        await p.Compiled();
        Assert.Equal(GameStatus.Warmed, p.K.Games.Single().Status);
    }

    /// <summary>No compile from outside the flow warms the cache before the before run (a user's throws, a queued or
    /// background one skips the game), and no Clear cache drops the compile the after run measures.</summary>
    [Fact]
    public async Task Before_and_after_keeps_other_compiles_and_clears_off_its_cache()
    {
        var p = await PairRig();
        p.K.StartBeforeAfter(p.Id, 5);
        Assert.Throws<InvalidOperationException>(() => p.K.Compile(p.Id));
        p.K.Enqueue(p.Id);
        p.K.EnqueueWhenIdle(p.Id);
        Assert.Empty(p.K.Queue);

        p.Play(360);
        Assert.Equal(BeforeAfterStage.Compiling, p.Record!.Stage);
        Assert.Throws<InvalidOperationException>(() => p.K.ClearGameCache(p.Id, gamePrecache: true));
        await p.Compiled();
        Assert.Equal(AfterReady, p.Record!.Stage);
        Assert.Throws<InvalidOperationException>(() => p.K.ClearGameCache(p.Id));

        p.K.EndBeforeAfter(p.Id, compile: false);
        Assert.True(p.K.ClearGameCache(p.Id));
    }

    /// <summary>A clear that fails after a short before run arms nothing: a launch then doesn't count, and
    /// ContinueBeforeAfter retries the clear and arms once it works.</summary>
    [Fact]
    public async Task Before_and_after_arms_the_before_run_again_only_after_a_clear()
    {
        var p = await PairRig();
        p.K.StartBeforeAfter(p.Id, 5);
        p.Running.Add(p.Exe);
        p.Poll();
        p.Launch(120);
        p.Poll(60);
        using (new FileStream(p.DriverCache, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))   // the driver holds it
        {
            p.Exit();
            Assert.Equal((BeforeArmed, true), (p.Shown!.Stage, p.Shown.ClearPending));
            Assert.StartsWith("Couldn't clear the cache", p.Shown.Problem);
            p.Running.Add(p.Exe);
            p.Poll();
            Assert.Equal(BeforeArmed, p.Record!.Stage);   // played on a warm cache: not a run
            p.Exit();
            Assert.Throws<InvalidOperationException>(() => p.K.ContinueBeforeAfter(p.Id));
            Assert.True(p.Record!.ClearPending);
        }
        p.K.ContinueBeforeAfter(p.Id);
        Assert.False(File.Exists(p.DriverCache));
        Assert.Equal(new BeforeAfterInfo(BeforeArmed, 5, null, 0), p.Shown);
        var before = p.Play(360);
        Assert.Equal((BeforeAfterStage.Compiling, before), (p.Record!.Stage, p.Record.Before));
        await p.Compiled();
    }

    [Fact]
    public async Task Before_and_after_compiles_again_after_a_launch_the_app_was_closed_for()
    {
        var p = await PairRig();
        p.K.StartBeforeAfter(p.Id, 5);
        p.Play(360);
        await p.Compiled();
        Assert.Equal(AfterReady, p.Record!.Stage);

        p.At = p.At.AddMinutes(5);   // the app closed, the game played, the app starts again
        p.Launch(60);
        await Restart(p);
        Assert.True(p.Record!.Dirty);
        Assert.Equal(new BeforeAfterInfo(AfterReady, 5, ScsKiller.DirtyNote, 0), p.Shown);
        p.K.ContinueBeforeAfter(p.Id);
        Assert.Equal((BeforeAfterStage.Compiling, false), (p.Record!.Stage, p.Record.Dirty));
        await p.Compiled();
    }

    /// <summary>Also with ScanAtStart off, whose start reuses the last list and reads no game: the run is read then.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Before_and_after_takes_a_run_made_while_the_app_was_closed(bool scanAtStart)
    {
        var p = await PairRig();
        p.K.StartBeforeAfter(p.Id, 5);
        p.At = p.At.AddMinutes(1);   // the app closed, the game played, the app starts again
        var before = p.Launch(360);
        p.At = p.At.AddMinutes(7);
        await Restart(p, scanAtStart: scanAtStart);
        Assert.Equal((BeforeAfterStage.Compiling, before), (p.Record!.Stage, p.Record.Before));
        await p.Compiled();
        Assert.Equal(AfterReady, p.Record!.Stage);

        p.K.ContinueBeforeAfter(p.Id);
        p.At = p.At.AddMinutes(1);
        var after = p.Launch(360);
        p.At = p.At.AddMinutes(7);
        await Restart(p, scanAtStart: scanAtStart);
        Assert.Equal((Done, after), (p.Record!.Stage, p.Record.After));
        Assert.NotNull(p.K.BeforeAfterReport(p.Id));
    }

    /// <summary>A before run made while the app was closed, after a clear that didn't know the game's driver-cache key:
    /// nothing saw which keys it held, so it is played again; once the key is known, such a run counts.</summary>
    [Fact]
    public async Task Before_and_after_plays_a_before_run_it_did_not_see_again_when_its_clear_missed_a_key()
    {
        var p = await PairRig();
        var rec = p.K.Store.LoadGame(p.Id);
        rec.CacheKeys.Clear();   // never compiled, never seen running
        p.K.Store.SaveGame(p.Id, rec);
        p.K.StartBeforeAfter(p.Id, 5);
        Assert.True(p.Record!.ClearGap);
        p.At = p.At.AddMinutes(1);   // the app closed, the game played, the app starts again
        p.Launch(360);
        p.At = p.At.AddMinutes(7);
        await Restart(p);
        Assert.Equal((BeforeArmed, (long?)null, ScsKiller.UnseenRedoNote), (p.Record!.Stage, p.Record.Before, p.Record.Problem));

        rec = p.K.Store.LoadGame(p.Id);
        (rec.CacheKeys, rec.GameKeys) = (["33333333"], ["33333333"]);   // learned meanwhile
        p.K.Store.SaveGame(p.Id, rec);
        p.Play(10);   // a short run: the clear after it knows the key
        Assert.False(p.Record!.ClearGap);
        p.At = p.At.AddMinutes(1);
        var before = p.Launch(360);
        p.At = p.At.AddMinutes(7);
        await Restart(p);
        Assert.Equal((BeforeAfterStage.Compiling, before), (p.Record!.Stage, p.Record.Before));
        await p.Compiled();
    }

    /// <summary>A driver update after a run the app didn't see end: the run's snapshot, read after the update, can't say
    /// which driver it ran on, so the before run is played again.</summary>
    [Fact]
    public async Task Before_and_after_plays_the_before_run_again_after_a_driver_update_the_app_was_closed_for()
    {
        var p = await PairRig();
        p.K.StartBeforeAfter(p.Id, 5);
        var driver = p.Record!.DriverId;
        p.Running.Add(p.Exe);
        p.Poll();
        p.Launch(360);
        p.Running.Clear();
        await Restart(p, driver: "101.00");
        Assert.Equal(new BeforeAfterInfo(BeforeArmed, 5, ScsKiller.DriverRedoNote, 0), p.Shown);
        Assert.NotEqual(driver, p.Record!.DriverId);
        Assert.Equal(p.K.DriverId, p.Record.DriverId);
        Assert.Empty(p.K.Queue);
    }

    /// <summary>Another process (the command line) queued the game before the pair armed: its runner checks again before the warm.</summary>
    [Fact]
    public async Task Before_and_after_stops_a_compile_another_process_queued_before_the_pair()
    {
        var p = await PairRig();
        var warmer = new FakeWarmer();
        var other = Killer(new FakeReader(Unreal), warmer: warmer);   // the same data folder
        await other.ScanAsync(default);
        other.Enqueue(p.Id);
        p.K.StartBeforeAfter(p.Id, 5);
        other.StartQueue();
        await other.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(warmer.Started);
        var q = Assert.Single(other.Queue);
        Assert.Equal(QueueStage.Failed, q.Stage);
        Assert.Contains("before recording is still to come", q.Error);
    }
}
