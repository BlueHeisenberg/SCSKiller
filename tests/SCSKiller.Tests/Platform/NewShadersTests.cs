using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

public class NewShadersTests
{
    const string Driver = "100.01";
    static readonly DateTimeOffset At = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    static readonly DateTimeOffset Now = At.AddDays(3);

    static GameState S(string id, long recorded = 0, long? planNew = null, GameStatus status = GameStatus.Stale, AntiCheat antiCheat = AntiCheat.None,
        bool playing = false, string driver = Driver, DateTimeOffset? warmedAt = null, long plan = 0, string reason = "") =>
        new GameState(new Game(id, id, Store.Steam, "", ""), null, antiCheat, status, reason, null, plan > 0 ? new PlanStats(plan, 0, 0, 0, false) : null,
                null, null, driver, warmedAt ?? At, null, false, null)
            with { RecordedSinceWarm = recorded, NewPipelines = planNew, Playing = playing };

    static (IReadOnlyList<GameState> Due, Dictionary<string, string> Notified) Due(Dictionary<string, string> notified, params GameState[] games) =>
        DueAt(Now, notified, games);

    static (IReadOnlyList<GameState> Due, Dictionary<string, string> Notified) DueAt(DateTimeOffset now, Dictionary<string, string> notified, params GameState[] games) =>
        NewShaders.Due(games, [], notified, DriverStale(games), now);

    // as ScsKiller.DriverStaleGames gives them: warmed for another driver
    static HashSet<string> DriverStale(GameState[] games) => games.Where(s => s.WarmedDriverVersion != Driver).Select(s => s.Game.Id).ToHashSet();

    [Fact]
    public void Only_compiled_games_with_something_new_on_this_driver_are_told_about()
    {
        var (due, notified) = Due([],
            S("plan-check-nothing-new", planNew: 0, status: GameStatus.Warmed),
            S("never-compiled", recorded: 25_000, status: GameStatus.Ready) with { WarmedAt = null },
            S("unsupported", recorded: 25_000, status: GameStatus.Unsupported),
            S("anti-cheat", recorded: 25_000, antiCheat: AntiCheat.EasyAntiCheat),
            S("driver-stale", recorded: 25_000, driver: "99.00"),
            S("recorded", recorded: 1_500),
            S("plan-check-found-more", planNew: 1_000));
        Assert.Equal(["recorded", "plan-check-found-more"], due.Select(s => s.Game.Id));
        Assert.Equal(["recorded", "plan-check-found-more"], notified.Keys);
    }

    [Fact]
    public void Several_games_come_in_one_list_and_each_change_is_told_once()
    {
        var (due, notified) = Due([], S("a", recorded: 1_500), S("b", recorded: 500, planNew: 2_000));
        Assert.Equal(2, due.Count);
        Assert.Equal(2_500, NewShaders.Count(due[1]));

        (due, notified) = Due(notified, S("a", recorded: 1_500), S("b", recorded: 500, planNew: 2_000));
        Assert.Empty(due);

        (due, notified) = DueAt(Now.AddDays(1), notified, S("a", recorded: 3_500), S("b", recorded: 500, planNew: 2_000));
        Assert.Equal("a", Assert.Single(due).Game.Id);

        (due, notified) = DueAt(Now.AddDays(2), notified, S("a", recorded: 3_500), S("b", warmedAt: At.AddDays(1)));   // b compiled again: nothing new
        Assert.Empty(due);
        Assert.Equal(["a"], notified.Keys);
    }

    [Fact]
    public void Fewer_new_pipelines_than_the_minimum_are_not_told_about_and_compile_quietly_when_automatic()
    {
        // 1% of the plan, at least 500
        Assert.Equal(500, NewShaders.Minimum(null));
        Assert.Equal(500, NewShaders.Minimum(new PlanStats(2_484, 0, 0, 0, false)));
        Assert.Equal(4_906, NewShaders.Minimum(new PlanStats(490_695, 0, 0, 0, false)));
        GameState[] games =
        [
            S("small-499", recorded: 499, plan: 2_484),   // its page still says 499 new
            S("small-500", recorded: 500, plan: 2_484),
            S("big-4905", recorded: 4_905, plan: 490_695),
            S("big-4906", recorded: 4_906, plan: 490_695),
            S("planner", planNew: 2, plan: 109_245, reason: "SCSKiller can now compile 2 more pipelines for this game"),   // told as before
        ];
        Assert.Equal(["small-500", "big-4906", "planner"], Due([], games).Due.Select(s => s.Game.Id));
        var (notify, compile, _) = NewShaders.Plan(games, [], new Dictionary<string, string>(), DriverStale(games), Now, _ => NewShadersMode.CompileAndNotify, compiling: false);
        Assert.Equal(["small-499", "small-500", "big-4905", "big-4906", "planner"], compile.Select(s => s.Game.Id));
        Assert.Equal(["small-500", "big-4906", "planner"], notify.Select(s => s.Game.Id));
    }

    [Fact]
    public void A_game_is_told_about_at_most_once_a_day()
    {
        var (due, notified) = Due([], S("a", recorded: 1_500));
        Assert.Single(due);
        var told = notified["a"];

        (due, notified) = DueAt(Now.AddHours(23), notified, S("a", recorded: 4_500));
        Assert.Empty(due);
        Assert.Equal(told, notified["a"]);   // kept as told: the count since is told once the day is over

        (due, notified) = DueAt(Now.AddHours(23.5), notified, S("a"));   // nothing new for a while: the day still counts
        Assert.Empty(due);
        Assert.Equal(told, notified["a"]);

        (due, notified) = DueAt(Now.AddHours(23.9), notified, S("a", recorded: 4_500));
        Assert.Empty(due);

        (due, notified) = DueAt(Now.AddHours(24), notified, S("a", recorded: 4_500));
        Assert.Single(due);

        (due, _) = DueAt(Now.AddHours(25), notified,
            S("a", recorded: 10_000, reason: "SCSKiller can now compile 1,100 more pipelines for this game"));   // a newer planner: as before
        Assert.Single(due);
    }

    /// <summary>An entry kept before told times were (a recount after an update can change every count): told when it is
    /// first read, so no game is told again for that day, and the time is kept from then on.</summary>
    [Fact]
    public void An_entry_from_before_told_times_counts_as_told_now()
    {
        var key = NewShaders.Key(S("a", recorded: 1_500), new HashSet<string>())!;
        var (due, notified) = Due(new() { ["a"] = key }, S("a", recorded: 1_500));
        Assert.Empty(due);
        Assert.Equal($"{key}|{Now.UtcTicks}|p", notified["a"]);

        (due, notified) = Due(new() { ["a"] = key }, S("a", recorded: 10_000));
        Assert.Empty(due);
        Assert.Equal($"{key}|{Now.UtcTicks}|p", notified["a"]);

        (due, _) = DueAt(Now.AddDays(1), notified, S("a", recorded: 10_000));
        Assert.Single(due);

        (due, notified) = Due(new() { ["a"] = key }, S("a", recorded: 1_500, reason: "SCSKiller can now compile 300 more pipelines for this game"));
        Assert.Empty(due);   // what it told may have been this compile's planner notification
        Assert.Equal($"{key}|{Now.UtcTicks}|p", notified["a"]);
    }

    /// <summary>The minimum is on what is new since the game was told: a few more a day later aren't told about (The
    /// Witcher 3: 366 upscaler pipelines told, 373 after a pack update). Compiled since, the count starts over.</summary>
    [Fact]
    public void A_game_is_told_again_only_when_enough_more_is_new()
    {
        var (due, notified) = Due([], S("a", recorded: 1_830, plan: 124_965));
        Assert.Single(due);

        (due, notified) = DueAt(Now.AddDays(2), notified, S("a", recorded: 1_865, plan: 124_965));
        Assert.Empty(due);
        Assert.StartsWith($"{At.UtcTicks}|1830|", notified["a"]);   // what was told stays the baseline

        (due, notified) = DueAt(Now.AddDays(3), notified, S("a", recorded: 1_830 + 1_249, plan: 124_965));
        Assert.Single(due);

        (due, _) = DueAt(Now.AddDays(5), notified, S("a", recorded: 1_500, plan: 124_965, warmedAt: At.AddDays(4)));
        Assert.Single(due);   // compiled since: 300 new since that compile
    }

    /// <summary>A rebuilt plan's notification skips the minimum and the day once per compile. A count that changes after
    /// it (a community download lands seconds later) waits like any other: FF7 Rebirth was told twice in two seconds.</summary>
    [Fact]
    public void A_rebuilt_plan_is_told_once_per_compile()
    {
        const string Planner = "SCSKiller can now compile 1,244 more pipelines for this game";
        var (due, notified) = Due([], S("a", recorded: 5_890, planNew: 6_220, reason: Planner));
        Assert.Single(due);
        Assert.EndsWith("|p", notified["a"]);

        (due, notified) = DueAt(Now.AddSeconds(2), notified, S("a", recorded: 5_905, planNew: 6_220, reason: Planner));
        Assert.Empty(due);

        (due, _) = DueAt(Now.AddHours(1), notified, S("a", planNew: 138_235, warmedAt: At.AddDays(1),
            reason: "SCSKiller can now compile 27,647 more pipelines for this game"));
        Assert.Single(due);   // another compile's plan, within the day
    }

    /// <summary>Before its plan is rebuilt, a newer planner's game counts what the last plan has: told as any other.</summary>
    [Fact]
    public void A_plan_not_rebuilt_yet_is_not_the_planners_notification()
    {
        var (due, _) = Due([], S("a", recorded: 35, reason: "SCSKiller can now compile more of this game"));
        Assert.Empty(due);
    }

    [Fact]
    public void A_running_game_waits_for_its_exit()
    {
        var (due, notified) = Due([], S("a", recorded: 1_500, playing: true));
        Assert.Empty(due);
        Assert.Empty(notified);

        (due, _) = Due(notified, S("a", recorded: 1_500));
        Assert.Equal("a", Assert.Single(due).Game.Id);
    }

    [Fact]
    public void A_game_queued_for_a_compile_is_not_told_about()
    {
        GameState[] games = [S("waiting", recorded: 500), S("idle", recorded: 500), S("plan-check", recorded: 500), S("done", recorded: 500)];
        QueueItem[] queue =
        [
            new("waiting", QueueStage.Waiting, null, null),
            new("idle", QueueStage.Warming, null, null, "starts when the PC is idle"),
            new("plan-check", QueueStage.Waiting, null, null, PlanCheck: true),   // a plan rebuild compiles nothing
            new("done", QueueStage.Done, null, null),
        ];
        var (due, notified) = NewShaders.Due(games, queue, new Dictionary<string, string>(), DriverStale(games), Now);
        Assert.Equal(["plan-check", "done"], due.Select(s => s.Game.Id));
        Assert.Equal(["plan-check", "done"], notified.Keys);
    }

    static (IReadOnlyList<GameState> Notify, IReadOnlyList<GameState> Compile, Dictionary<string, string> Notified) Plan(DateTimeOffset now,
        Dictionary<string, string> notified, NewShadersMode mode, params GameState[] games) =>
        NewShaders.Plan(games, [], notified, DriverStale(games), now, s => s.NewShadersOverride ?? mode, compiling: false);

    static string[] Ids(IEnumerable<GameState> games) => games.Select(s => s.Game.Id).ToArray();

    [Fact]
    public void Each_mode_notifies_compiles_or_both_and_a_game_override_wins()
    {
        var (notify, compile, notified) = Plan(Now, [], NewShadersMode.Notify,
            S("global"),
            S("off", recorded: 1_500) with { NewShadersOverride = NewShadersMode.Off },
            S("notify", recorded: 1_500) with { NewShadersOverride = NewShadersMode.Notify },
            S("both", recorded: 1_500) with { NewShadersOverride = NewShadersMode.CompileAndNotify },
            S("silent", recorded: 1_500) with { NewShadersOverride = NewShadersMode.CompileSilently });
        Assert.Equal(["notify", "both"], Ids(notify));
        Assert.Equal(["both", "silent"], Ids(compile));
        Assert.Equal(["notify", "both", "silent"], notified.Keys);

        var off = AppStore.DefaultSettings with { NotifyNewShaders = false };
        Assert.Equal(NewShadersMode.Notify, NewShaders.Global(AppStore.DefaultSettings));
        Assert.Equal(NewShadersMode.Off, NewShaders.Global(off));
        Assert.Equal(NewShadersMode.CompileSilently, NewShaders.Global(off with { OnNewShaders = NewShadersMode.CompileSilently }));
        Assert.Equal(NewShadersMode.Notify, NewShaders.ModeOf(S("a") with { NewShadersOverride = NewShadersMode.Notify }, off));
        Assert.Equal(NewShadersMode.Off, NewShaders.ModeOf(S("a"), off));
    }

    [Fact]
    public void No_automatic_compile_while_the_game_runs()
    {
        var (notify, compile, notified) = Plan(Now, [], NewShadersMode.CompileSilently, S("a", recorded: 1_500, playing: true));
        Assert.Empty(notify);
        Assert.Empty(compile);

        (_, compile, _) = Plan(Now, notified, NewShadersMode.CompileSilently, S("a", recorded: 1_500));
        Assert.Equal(["a"], Ids(compile));
    }

    [Fact]
    public void No_automatic_compile_for_unsupported_partly_compiled_or_unreached_games()
    {
        var partly = S("partly", recorded: 1_500, status: GameStatus.Warmed, plan: 50_000) with { LastWarmFailed = 25_000 };
        var (notify, compile, _) = Plan(Now, [], NewShadersMode.CompileAndNotify,
            S("unsupported", recorded: 1_500, status: GameStatus.Unsupported),
            S("unreached", recorded: 1_500) with { CompileUnreached = true },
            partly,
            partly with { Game = partly.Game with { Id = "partly-stale" }, Status = GameStatus.Stale },
            S("compiles", recorded: 1_500));
        Assert.Equal(["compiles"], Ids(compile));
        Assert.Equal(["partly", "partly-stale", "compiles"], Ids(notify));   // told instead

        (notify, compile, _) = Plan(Now, [], NewShadersMode.CompileSilently, partly);
        Assert.Empty(compile);
        Assert.Equal(["partly"], Ids(notify));
    }

    [Fact]
    public void No_automatic_compile_while_another_compile_runs_or_the_game_is_queued()
    {
        GameState[] games = [S("a", recorded: 1_500)];
        var (notify, compile, notified) = NewShaders.Plan(games, [], new Dictionary<string, string>(), DriverStale(games), Now,
            _ => NewShadersMode.CompileAndNotify, compiling: true);
        Assert.Empty(notify);
        Assert.Empty(compile);
        Assert.Empty(notified);   // nothing marked told: it compiles once the other compile is done

        (_, compile, _) = NewShaders.Plan(games, [new QueueItem("a", QueueStage.Waiting, null, null, ScsKiller.WhenIdleNote)], notified,
            DriverStale(games), Now, _ => NewShadersMode.CompileAndNotify, compiling: false);
        Assert.Empty(compile);

        (_, compile, _) = NewShaders.Plan(games, [], notified, DriverStale(games), Now, _ => NewShadersMode.CompileAndNotify, compiling: false);
        Assert.Equal(["a"], Ids(compile));
    }

    [Fact]
    public void A_burst_of_new_pipelines_queues_one_compile()
    {
        var compiles = 0;
        Dictionary<string, string> notified = [];
        // the recording's import, then community downloads minutes apart; the compile is done 20 minutes in
        var compiled = Now.AddMinutes(20);
        foreach (var (minutes, recorded, warmed) in new[] { (0, 1_500, At), (2, 3_250, At), (30, 600, compiled), (45, 4_500, compiled), (90, 7_000, compiled) })
        {
            var (_, compile, keep) = Plan(Now.AddMinutes(minutes), notified, NewShadersMode.CompileSilently, S("a", recorded: recorded, warmedAt: warmed));
            (compiles, notified) = (compiles + compile.Count, keep);
        }
        Assert.Equal(1, compiles);

        var (_, next, _) = Plan(Now.AddDays(1), notified, NewShadersMode.CompileSilently, S("a", recorded: 7_000, warmedAt: compiled));
        Assert.Single(next);   // what came after it, once the day is over
    }

    [Fact]
    public void What_was_only_told_about_compiles_once_the_game_compiles_automatically()
    {
        var (_, compile, notified) = Plan(Now, [], NewShadersMode.Notify, S("a", recorded: 1_500));
        Assert.Empty(compile);

        (_, compile, notified) = Plan(Now.AddHours(1), notified, NewShadersMode.CompileSilently, S("a", recorded: 1_500));
        Assert.Empty(compile);   // the day still counts

        (_, compile, notified) = Plan(Now.AddDays(1), notified, NewShadersMode.CompileSilently, S("a", recorded: 1_500));
        Assert.Single(compile);

        (_, compile, notified) = Plan(Now.AddDays(2), notified, NewShadersMode.CompileSilently, S("a", recorded: 1_500));
        Assert.Single(compile);   // not compiled a day later (the app quit before idle, or it was withdrawn): queued once more

        (var told, compile, _) = Plan(Now.AddDays(3), notified, NewShadersMode.CompileSilently, S("a", recorded: 1_500));
        Assert.Empty(compile);
        Assert.Empty(told);

        // left out of the automatic compile and told instead: told again only for more, as before
        var partly = S("p", recorded: 1_500, status: GameStatus.Warmed, plan: 50_000) with { LastWarmFailed = 25_000 };
        (var notify, _, notified) = Plan(Now, [], NewShadersMode.CompileSilently, partly);   // a fresh store
        Assert.Single(notify);
        (notify, _, _) = Plan(Now.AddDays(2), notified, NewShadersMode.CompileSilently, partly);
        Assert.Empty(notify);
    }

    [Fact]
    public void A_queued_automatic_compile_is_withdrawn_when_the_setting_no_longer_compiles()
    {
        GameState[] games = [S("auto"), S("off") with { NewShadersOverride = NewShadersMode.Off }, S("notify"), S("started"), S("promoted"), S("gone")];
        QueueItem[] queue =
        [
            new("auto", QueueStage.Waiting, null, null, ScsKiller.WhenIdleNote),
            new("off", QueueStage.Waiting, null, null, ScsKiller.WhenIdleNote),
            new("notify", QueueStage.Waiting, null, null, ScsKiller.WhenIdleNote),
            new("started", QueueStage.Warming, null, null),
            new("promoted", QueueStage.Waiting, null, null),   // the user made it a normal item
        ];
        var settings = AppStore.DefaultSettings with { OnNewShaders = NewShadersMode.CompileSilently };
        var (withdraw, forget) = NewShaders.Withdrawn(["auto", "off", "started", "promoted", "gone"], queue,
            games.Select(s => s.Game.Id == "notify" ? s with { NewShadersOverride = NewShadersMode.Notify } : s), settings);
        Assert.Equal(["off"], withdraw);
        Assert.Equal(["started", "promoted", "gone"], forget);

        (withdraw, _) = NewShaders.Withdrawn(["auto", "notify"], queue, games, settings with { OnNewShaders = NewShadersMode.Notify });
        Assert.Equal(["auto", "notify"], withdraw);
    }

    [Fact]
    public void An_automatic_compile_that_didnt_lower_the_count_is_not_repeated()
    {
        var (_, compile, notified) = Plan(Now, [], NewShadersMode.CompileSilently, S("a", recorded: 1_500));
        Assert.Single(compile);

        // compiled, and the same 300 are still new (pipelines the compile can't replay): told, not compiled again
        var compiled = Now.AddHours(1);
        (var notify, compile, notified) = Plan(Now.AddDays(1), notified, NewShadersMode.CompileSilently, S("a", recorded: 1_500, warmedAt: compiled));
        Assert.Empty(compile);
        Assert.Single(notify);

        (notify, compile, notified) = Plan(Now.AddDays(3), notified, NewShadersMode.CompileSilently, S("a", recorded: 1_500, warmedAt: compiled));
        Assert.Empty(compile);
        Assert.Empty(notify);

        (_, compile, _) = Plan(Now.AddDays(4), notified, NewShadersMode.CompileSilently, S("a", recorded: 1_500 + 500, warmedAt: compiled));
        Assert.Single(compile);   // enough more since: compiled again

        // a compile that lowered the count: the rest compiles as usual
        (_, _, notified) = Plan(Now, [], NewShadersMode.CompileSilently, S("b", recorded: 4_500));
        (_, compile, _) = Plan(Now.AddDays(1), notified, NewShadersMode.CompileSilently, S("b", recorded: 1_000, warmedAt: compiled));
        Assert.Single(compile);
    }

    /// <summary>Far more than the minimum skips the day, once per jump: Monster Hunter Wilds got a 32,249-pipeline community
    /// recording hours after its automatic compile.</summary>
    [Fact]
    public void A_big_jump_is_compiled_and_told_within_the_day()
    {
        var (_, compile, notified) = Plan(Now, [], NewShadersMode.CompileAndNotify, S("a", recorded: 600, plan: 8_281));
        Assert.Single(compile);

        var compiled = Now.AddHours(1);
        (var notify, compile, notified) = Plan(Now.AddHours(11), notified, NewShadersMode.CompileAndNotify, S("a", recorded: 32_249, plan: 8_281, warmedAt: compiled));
        Assert.Single(compile);
        Assert.Single(notify);

        (notify, compile, _) = Plan(Now.AddHours(12), notified, NewShadersMode.CompileAndNotify, S("a", recorded: 32_249 + 9_999, plan: 8_281, warmedAt: compiled));
        Assert.Empty(compile);   // the next jump counts from this one
        Assert.Empty(notify);

        // told only: 20 times the minimum (20% of the plan, at least 10,000)
        Assert.Equal(10_000, NewShaders.Jump * NewShaders.Minimum(null));
        (var due, notified) = Due([], S("b", recorded: 1_500));
        Assert.Single(due);
        (due, _) = DueAt(Now.AddHours(3), notified, S("b", recorded: 11_499));
        Assert.Empty(due);
        (due, _) = DueAt(Now.AddHours(3), notified, S("b", recorded: 11_500));
        Assert.Single(due);
    }

    /// <summary>An automatic compile still waiting for idle when the app quits is queued again at the next start, without a
    /// try or a notification counted. One that started counts, and the day holds it as before.</summary>
    [Fact]
    public void An_automatic_compile_lost_to_a_quit_is_queued_again_at_the_next_start()
    {
        GameState[] games = [S("a", recorded: 1_500)];
        (IReadOnlyList<GameState> Notify, IReadOnlyList<GameState> Compile, Dictionary<string, string> Notified) Check(DateTimeOffset now,
            Dictionary<string, string> notified, DateTimeOffset session, QueueItem[]? queue = null, string[]? left = null) =>
            NewShaders.Plan(games, queue ?? [], notified, DriverStale(games), now, _ => NewShadersMode.CompileAndNotify, compiling: false,
                left?.ToHashSet(), session);

        var (notify, compile, notified) = Check(Now, [], session: Now);
        Assert.Single(compile);
        Assert.Single(notify);

        for (var restart = 1; restart <= 3; restart++)   // each start finds it gone from the queue
        {
            var start = Now.AddHours(restart);
            (notify, compile, notified) = Check(start, notified, session: start);
            Assert.Single(compile);
            Assert.Empty(notify);   // told when it was first queued
        }

        var session = Now.AddHours(3);
        (_, compile, notified) = Check(session.AddMinutes(1), notified, session, [new("a", QueueStage.Waiting, null, null, ScsKiller.WhenIdleNote)]);
        Assert.Empty(compile);   // still waiting in this session

        (_, _, notified) = Check(session.AddMinutes(2), notified, session, [new("a", QueueStage.Indexing, null, null)], left: ["a"]);
        (_, compile, _) = Check(Now.AddHours(4), notified, session: Now.AddHours(4));
        Assert.Empty(compile);   // it started before the quit: tried, the day holds it

        (_, compile, _) = Check(session.AddDays(1), notified, session: session.AddDays(1));
        Assert.Single(compile);   // its second try
    }
}
