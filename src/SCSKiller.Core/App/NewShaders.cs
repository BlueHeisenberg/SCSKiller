namespace SCSKiller.Core.App;

/// <summary>When the app tells about compiled games that have pipelines to compile again: their recording (this PC's or
/// the community database's) or a newer planner found ones the last warm didn't compile. Driver-stale games are the
/// driver-update notification's. A game without shader stutter (Games.GameVerdicts) is never told about.</summary>
public static class NewShaders
{
    public static long Count(GameState s) => (s.NewPipelines ?? 0) + s.RecordedSinceWarm;

    /// <summary>The fewest new pipelines that make a compiled game "Needs rebuilding" and get it told about: 1% of its plan,
    /// at least <see cref="MinNew"/>. Fewer keep it Warmed (its page still shows them) and its next compile takes them.</summary>
    public static long Minimum(PlanStats? p) => Math.Max((p is null ? 0 : p.Recorded + p.Generated + p.D3D11Shaders + p.MiddlewareItems) / 100, MinNew);

    public const long MinNew = 500;

    /// <summary>Growth that skips the day's wait, in minimums: 20% of the plan, at least 10,000. A day of play and community
    /// downloads stays under it, a first big community recording doesn't (+32,249 on an 8,281-pipeline plan).</summary>
    public const int Jump = 20;

    /// <summary>A rebuilt plan's "can now compile N more" (ScsKiller.PlannerChanged): told once per compile, whatever the
    /// count and the day. Not countable ("can now compile more of this game"), its count is the last plan's.</summary>
    static bool PlannerChanged(GameState s) => s.Status == GameStatus.Stale && s.StatusReason.StartsWith("SCSKiller can now compile ", StringComparison.Ordinal)
        && !s.StatusReason.StartsWith("SCSKiller can now compile more", StringComparison.Ordinal);

    /// <summary>What a notification about the game says, as the notified store keeps it; null when there is nothing to tell.</summary>
    public static string? Key(GameState s, IReadOnlySet<string> driverStale) =>
        s is { Status: GameStatus.Warmed or GameStatus.Stale, AntiCheat: AntiCheat.None, WarmedAt: { } at, NoStutter: null, CompileUnreached: false } && !driverStale.Contains(s.Game.Id)
        && Count(s) is var n and > 0 ? $"{at.UtcTicks}|{n}" : null;

    /// <summary>A notified store entry: <see cref="Key"/>'s warm and count, when it was told (UTC ticks), whether that was
    /// the compile's planner notification, and how many automatic compiles its pipelines had (0 to 2: "a" once, "A" done
    /// with them), and whether that automatic compile still waits for idle ("w"). An entry kept before told times were:
    /// told at <paramref name="now"/>, of either kind.</summary>
    readonly record struct Told(long Warm, long Count, long At, bool Planner, int Auto = 0, bool Waiting = false)
    {
        public static Told? Parse(string? entry, DateTimeOffset now) =>
            entry?.Split('|') is [var w, var n, .. var rest] && long.TryParse(w, out var warm) && long.TryParse(n, out var count)
                ? rest is [var t, ..] && long.TryParse(t, out var at)
                    ? new Told(warm, count, at, rest is [_, var f] && f.Contains('p'), rest is [_, var g] ? g.Contains('A') ? 2 : g.Contains('a') ? 1 : 0 : 0,
                        rest is [_, var h] && h.Contains('w'))
                    : new Told(warm, count, now.UtcTicks, true) : null;

        public override string ToString() => $"{Warm}|{Count}|{At}" + (Planner || Auto > 0 || Waiting ? "|" + (Planner ? "p" : "") + (Auto switch { 1 => "a", 2 => "A", _ => "" }) + (Waiting ? "w" : "") : "");
    }

    public static NewShadersMode Global(Settings s) => s.OnNewShaders ?? (s.NotifyNewShaders ? NewShadersMode.Notify : NewShadersMode.Off);

    public static NewShadersMode ModeOf(GameState s, Settings settings) => s.NewShadersOverride ?? Global(settings);

    /// <summary>The games to notify about now and the notified store to keep (<see cref="Plan"/> with every game on Notify).</summary>
    public static (IReadOnlyList<GameState> Due, Dictionary<string, string> Notified) Due(IEnumerable<GameState> games,
        IEnumerable<QueueItem> queue, IReadOnlyDictionary<string, string> notified, IReadOnlySet<string> driverStale, DateTimeOffset now)
    {
        var p = Plan(games, queue, notified, driverStale, now, _ => NewShadersMode.Notify, compiling: false);
        return (p.Notify, p.Notified);
    }

    /// <summary>The games to notify about now, the ones to compile when idle, and the notified store to keep. A running game
    /// waits for its exit, a game queued for a compile isn't told about, and one is told again only when its new pipelines
    /// grew by <see cref="Minimum"/> since it was told (since its compile, for a game compiled since), and not within a day of
    /// the last time unless they grew by <see cref="Jump"/> times the minimum. A rebuilt plan's (<see cref="PlannerChanged"/>)
    /// is told once per compile whatever the count and the day.
    /// A game whose <paramref name="mode"/> compiles automatically is compiled instead, unless it is partly compiled, its
    /// last automatic compile didn't lower its count, or the same pipelines were queued twice without a compile: it is told
    /// about then. While <paramref name="compiling"/>, it waits. One queued before <paramref name="session"/> that never
    /// left the wait for idle (the app quit first) is queued again, as if it never was.
    /// <paramref name="driverStale"/>: ids of the games warmed for another driver (ScsKiller.DriverStaleGames).
    /// <paramref name="left"/>: the games whose automatic compile left the wait for idle (<see cref="Withdrawn"/>'s forget).</summary>
    public static (IReadOnlyList<GameState> Notify, IReadOnlyList<GameState> Compile, Dictionary<string, string> Notified) Plan(
        IEnumerable<GameState> games, IEnumerable<QueueItem> queue, IReadOnlyDictionary<string, string> notified,
        IReadOnlySet<string> driverStale, DateTimeOffset now, Func<GameState, NewShadersMode> mode, bool compiling,
        IReadOnlySet<string>? left = null, DateTimeOffset? session = null)
    {
        var queued = queue.Where(q => !q.PlanCheck && q.Stage is not (QueueStage.Done or QueueStage.Failed or QueueStage.Stopped))
            .Select(q => q.GameId).ToHashSet();
        var (due, compile) = (new List<GameState>(), new List<GameState>());
        var keep = new Dictionary<string, string>();
        foreach (var s in games)
        {
            var told = Told.Parse(notified.GetValueOrDefault(s.Game.Id), now);
            if (told is { Waiting: true } w && left?.Contains(s.Game.Id) == true) told = w with { Waiting = false };
            var recent = told is { } r && now.UtcTicks - r.At < TimeSpan.TicksPerDay;
            var warm = s.WarmedAt?.UtcTicks;
            var same = told is { } t && t.Warm == warm;   // the count told is this compile's
            // queued by an earlier session and never left the wait for idle: the app quit first
            var lost = told is { Waiting: true } l && same && l.At < session?.UtcTicks;
            var m = mode(s);
            if (m == NewShadersMode.Off || Key(s, driverStale) is null)
            {
                if (recent) keep[s.Game.Id] = told.ToString()!;   // the day counts from the last notification, whatever came since
                continue;
            }
            var n = Count(s);
            // a community recording or a game update, not a day's play: measured from the count told, so it bypasses once
            var jump = told is { } j && n - j.Count >= Jump * Minimum(s.Plan);
            // compiled since, and still as many to compile, give or take fewer than Minimum
            var stuck = told is { Auto: > 0 } a && !same && n >= a.Count && n - a.Count < Minimum(s.Plan);
            // automatic compiles of these pipelines without a new warm: a withdrawn one runs once more, a lost one never ran
            var tries = same && n - told!.Value.Count < Minimum(s.Plan) ? told!.Value.Auto - (lost ? 1 : 0) : 0;
            // a Stale game's last warm counts as the Warmed one's does
            var partly = ScsKiller.IsPartlyCompiled(s with { Status = GameStatus.Warmed });
            var auto = m is NewShadersMode.CompileAndNotify or NewShadersMode.CompileSilently && !stuck && !partly && tries < 2;
            var plannerDue = PlannerChanged(s) && !(same && told!.Value.Planner);
            // what was only told about still compiles once the game's setting compiles automatically
            var baseline = same && !auto ? told!.Value.Count : 0;
            // under the minimum: an automatic compile runs quietly, else nothing
            var quiet = n - baseline < Minimum(s.Plan) && !plannerDue;
            if (!plannerDue && (recent && !jump && !(lost && auto) || quiet && !auto))
            {
                if (same || recent) keep[s.Game.Id] = told.ToString()!;
                continue;
            }
            if (s.Playing || queued.Contains(s.Game.Id) || auto && compiling)
            {
                if (told != null) keep[s.Game.Id] = told.ToString()!;   // what is new now is told once this passes
                continue;
            }
            if (auto) compile.Add(s);
            if (!quiet && (!auto || m == NewShadersMode.CompileAndNotify && !lost)) due.Add(s);   // a lost one was told when queued
            keep[s.Game.Id] = new Told(warm!.Value, n, now.UtcTicks, plannerDue || same && told!.Value.Planner, auto ? tries + 1 : stuck ? 2 : tries, auto).ToString();
        }
        return (due, compile, keep);
    }

    /// <summary>Of the automatic compiles queued (<paramref name="queued"/>), the ones to take out of the queue: still waiting
    /// for idle while the game's setting no longer compiles automatically. <paramref name="forget"/>: the ones no longer
    /// waiting for idle (started, finished, or made a normal item by the user), left alone from now on.</summary>
    public static (IReadOnlyList<string> Withdraw, IReadOnlyList<string> Forget) Withdrawn(IEnumerable<string> queued,
        IEnumerable<QueueItem> queue, IEnumerable<GameState> games, Settings settings)
    {
        var (withdraw, forget) = (new List<string>(), new List<string>());
        foreach (var id in queued)
        {
            if (queue.FirstOrDefault(q => q.GameId == id) is not { Stage: QueueStage.Waiting, Note: ScsKiller.WhenIdleNote }) forget.Add(id);
            else if (games.FirstOrDefault(s => s.Game.Id == id) is not { } s
                     || ModeOf(s, settings) is not (NewShadersMode.CompileAndNotify or NewShadersMode.CompileSilently)) withdraw.Add(id);
        }
        return (withdraw, forget);
    }
}
