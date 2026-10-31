using System.Collections.ObjectModel;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Ui.Models;

/// <summary>A queue row: the running item (1), a waiting item (2..) or a finished one (Number 0).</summary>
public sealed record QueueRow(string Id, int Number, string Name, string Exe, string Note, string Time, string Cache, QueueStage Stage,
    bool First = false, bool Last = false)
{
    public object? Icon { get; } = Host.Icons.Get(Exe);   // a field: an icon that failed makes the row unequal, so Sync replaces it
    public bool NoIcon => Icon is null;
    /// <summary>The initials' tile colour, ARGB.</summary>
    public uint Tile => GameRow.TileOf(Name);
    public string Initials => Name.Length > 0 ? GameRow.InitialsOf(Name) : "";
    public bool CanUp => !First;
    public bool CanDown => !Last;
    public string TopLabel => $"Move {Name} to the top";
    public string BottomLabel => $"Move {Name} to the bottom";
    public string UpLabel => $"Move {Name} up";
    public string DownLabel => $"Move {Name} down";
    public string RemoveLabel => $"Remove {Name} from the queue";
    public override string ToString() => Number > 0 ? $"{Number}. {Name}, {Note}, {Time}, {Cache}" : $"{Name}, {Note}";   // screen readers
}

public sealed class QueueVm : Bindable
{
    QueueItem? cur;
    long planned;
    List<(string Game, long Bytes)> growth = [];
    List<(long Growth, long Estimate)> sizes = [];
    (CacheUsage Usage, CacheLimit? Limit, long? Capped)? driver;
    readonly BackgroundRead<(CacheUsage, CacheLimit?, long?)> cache;

    public QueueVm() => cache = new("Queue cache", () => (Host.Core.Vendor.GetCacheUsage(), Host.Core.Vendor.GetCacheLimit(), (Host.Core.Vendor as AmdBackend)?.AppCache.DxcBytes()),
        c => { driver = c; ShowCache(); Changed(); });
    static Settings S { get => Host.Core.Settings; set => Host.Core.Settings = value; }

    public ObservableCollection<QueueRow> Waiting { get; } = [];
    public ObservableCollection<QueueRow> Finished { get; } = [];
    /// <summary>Folds the queue doesn't run (the app's start, the game's exit): the compile's last step.</summary>
    public ObservableCollection<QueueRow> Folds { get; } = [];
    public bool HasFolds => Folds.Count > 0;
    public bool Dragging { get; set; }   // don't rebuild Waiting under a drag in progress

    public QueueRow Current { get; private set; } = new("", 1, "", "", "", "", "", QueueStage.Waiting);
    public bool HasCurrent => cur != null;
    public bool Idle => cur == null && !Host.Core.QueueRunning;   // a running queue's button is Stop, also while it waits to start an item
    public bool CanStop => cur != null || Host.Core.QueueRunning;
    public bool Empty => cur == null && Waiting.Count == 0 && Folds.Count == 0;
    public bool HasFinished => Finished.Count > 0;
    public bool CanClear => Waiting.Count > 0 || Finished.Count > 0;
    public bool CanStart => !Host.Core.QueueRunning && Waiting.Count > 0;
    public bool CanRaiseLimit => ScsKiller.CanRaiseCacheLimit(Host.Core.Vendor.Caps, driver?.Limit);
    public string Summary { get; private set; } = "";
    public string? PlanChecks { get; private set; }   // ScsKiller.PlanCheckLine
    public bool HasPlanChecks => PlanChecks != null;

    public string CurrentDetail => cur is null ? "" : Fmt.Stage(cur) + (Fmt.ProgressText(cur) is { } p ? " · " + p : "") + (Fmt.Started(cur) is { } at ? " · " + at : "");
    public bool Indeterminate => Fmt.Indeterminate(cur);
    public double Percent => Fmt.Percent(cur);
    public string PauseText => cur?.Stage == QueueStage.Paused ? "Resume" : "Pause";
    public bool CacheWarn { get; private set; }
    public string CacheWarnText { get; private set; } = "";
    public string RuntimeWarnText { get; private set; } = "";
    public bool RuntimeWarn => RuntimeWarnText.Length > 0;

    public double Threads { get => Used; set { if ((int)value != Used) { S = S with { Threads = (int)value }; Changed(); } } }
    static int Used => ScsKiller.UsedThreads(S.Threads);
    public string ThreadsText => $"{Used} of {Environment.ProcessorCount}";
    public double MaxThreads => Environment.ProcessorCount;
    public int Priority { get => (int)S.Priority; set { if (value >= 0 && value != (int)S.Priority) { S = S with { Priority = (WarmPriority)value }; Changed(); } } }
    public string CpuSummary => $"CPU use: {Used} of {Environment.ProcessorCount} threads, {(S.Priority == WarmPriority.BelowNormal ? "below-normal" : "idle")} priority";

    public void Refresh()
    {
        var core = Host.Core;
        var all = core.Queue;
        var queue = all.Where(q => !q.PlanCheck).ToList();
        PlanChecks = ScsKiller.PlanCheckLine(all);
        var games = core.Games.ToDictionary(g => g.Game.Id);

        TimeSpan? Left(QueueItem q) => Fmt.Left(q, games.GetValueOrDefault(q.GameId));
        QueueRow Row(QueueItem q, int number, bool first = false, bool last = false)
        {
            var g = games.GetValueOrDefault(q.GameId);
            var time = q.Stage == QueueStage.Done ? Format.Duration(g?.LastWarmTime) : Left(q) is { } t ? Format.Duration(t) + (q.Progress?.PerSecond > 0 ? " left" : "") : Format.Dash;
            return new(q.GameId, number, g?.Game.Name ?? q.GameId, g?.Game.ExePath ?? "", NoteOf(q, g), time, g is null ? Format.Dash : Fmt.Cache(g), q.Stage, first, last);
        }

        (cur, var waiting) = Fmt.RunOrder(queue);
        if (cur != null) Current = Row(cur, 1);
        int first = cur != null ? 2 : 1;
        if (!Dragging) Sync(Waiting, waiting.Select((q, i) => Row(q, first + i, i == 0, i == waiting.Count - 1)));
        Sync(Finished, queue.Where(q => !Fmt.Active(q)).Select(q => Row(q, 0)));
        Sync(Folds, Fmt.FoldingGames.Select(games.GetValueOrDefault).OfType<GameState>()
            .Select(g => new QueueRow(g.Game.Id, 0, g.Game.Name, g.Game.ExePath, Fmt.FoldLine(g.Game.Id) ?? "", Format.Dash, Fmt.Cache(g), QueueStage.Merging)));

        var pending = queue.Where(Fmt.Active).ToList();
        var total = TimeSpan.FromTicks(pending.Sum(q => Left(q)?.Ticks ?? 0));
        bool unknown = pending.Any(q => Left(q) is null && !ScsKiller.Stalled(q));
        Summary = pending.Count == 0 ? "Nothing queued"
            : $"{pending.Count} game{(pending.Count == 1 ? "" : "s")} · "
              + (total > TimeSpan.Zero ? $"about {Format.Duration(total)} in total{(unknown ? ", plus plans to build" : "")}" : "time known once the plans are built")
              + (cur == null && !core.QueueRunning ? " · not started" : "");

        planned = pending.Sum(q => games.GetValueOrDefault(q.GameId)?.EstimatedCacheBytes ?? 0);
        var queued = pending.Select(q => games.GetValueOrDefault(q.GameId)).OfType<GameState>().ToList();
        RuntimeWarnText = ScsKiller.QueueRuntimeWarning(queued);
        growth = [.. queued.Select(g => (g.Game.Name, ScsKiller.CacheGrowth(g)))];
        sizes = [.. queued.Select(g => (ScsKiller.CacheGrowth(g), g.EstimatedCacheBytes ?? 0))];
        ShowCache();
        Changed();
        cache.Request();
    }

    void ShowCache()
    {
        if (driver is not { } d) return;   // not read yet
        var vendor = GpuBackends.OwnDriver(Host.Core.Vendor);   // RADV has no AMD driver cache
        CacheWarnText = (d.Capped is { } used
            ? ScsKiller.LargeCompileWarning(sizes, vendor) ?? AmdAppCache.QueueWarning(used, growth)   // one game over the cap is always over what's free too
            : ScsKiller.QueueCacheWarning(sizes, d.Usage.BytesOnDisk, planned, d.Limit?.Bytes, vendor, Host.Core.Vendor.Caps.PerGameCacheCap != null)) ?? "";
        CacheWarn = CacheWarnText.Length > 0;
    }

    static void Sync(ObservableCollection<QueueRow> target, IEnumerable<QueueRow> rows)
    {
        var list = rows.ToList();
        if (list.SequenceEqual(target)) return;
        target.Clear();
        foreach (var r in list) target.Add(r);
    }

    static string NoteOf(QueueItem q, GameState? g) => q.Stage switch
    {
        QueueStage.Failed => "Failed: " + q.Error,
        QueueStage.Stopped when (Fmt.FoldLine(q.GameId) ?? (ScsKiller.NotReached(g?.StatusReason) ? Format.Sentence(g!.StatusReason) : null)) is { } fold => fold,
        QueueStage.Stopped when q.Progress is { } p => $"Stopped at {p.Done:N0} of {p.Total:N0}: add it again to continue" + (q.Note is { } n ? " · " + n : ""),
        QueueStage.Stopped => "Stopped",
        QueueStage.Done => q.Note is { } n ? "Done · " + n : "Done",
        _ when q.Note != null => q.Note,
        _ => g?.Plan is { } plan ? $"{plan.Recorded + plan.Generated + plan.MiddlewareItems:N0} pipelines{(plan.D3D11Shaders > 0 ? $" + {plan.D3D11Shaders:N0} DirectX 11 shaders" : "")}" : "plan is built first",
    };

    public void PauseOrResume() => Fmt.PauseOrResume(cur);

    /// <summary>The waiting order after a drag: the <paramref name="moved"/> rows, in their order, go to the gap before row
    /// <paramref name="gap"/> of <paramref name="order"/> (its count: the end).</summary>
    public static List<string> Dropped(IReadOnlyList<string> order, IReadOnlySet<string> moved, int gap)
    {
        var rest = order.Where(id => !moved.Contains(id)).ToList();
        rest.InsertRange(order.Take(gap).Count(id => !moved.Contains(id)), order.Where(moved.Contains));
        return rest;
    }
}
