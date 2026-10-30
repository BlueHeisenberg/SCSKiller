using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Ui.Models;

public static class Fmt
{
    public static string RecorderDll(GameState s) => s.AgsHost ? ScsKiller.AgsName : s.Records11 ? ScsKiller.Dll11 : ScsKiller.Records9(s.Engine) ? ScsKiller.Dll9 : ScsKiller.Dll12;
    /// <summary>What the recorder adds next to the game: its dll, and the preloader that loads it before DLSS.</summary>
    public static string RecorderFiles(GameState s) => s.Preload is { } p ? $"{RecorderDll(s)} and {p}" : RecorderDll(s);
    /// <summary>The file's name in a Windows or a Linux path: the design data's paths are Windows ones everywhere.</summary>
    public static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
    public static string N(long? n) => n?.ToString("N0") ?? Format.Dash;
    /// <summary>"today at 20:14", "yesterday at 20:14", else "on 26/09/2026" (the culture's short date).</summary>
    public static string When(DateTimeOffset at)
    {
        var t = at.LocalDateTime;
        return t.Date == DateTime.Today ? $"today at {Time(t, "t")}" : t.Date == DateTime.Today.AddDays(-1) ? $"yesterday at {Time(t, "t")}" : $"on {t:d}";
    }
    /// <summary>A time in the culture's pattern; ICU's narrow no-break space before AM or PM, which Selawik lacks, as a space.</summary>
    public static string Time(DateTime t, string format) => t.ToString(format).Replace('\u202F', ' ');
    public static string KeptSize(KeptCache k) => $"{Format.Bytes(k.Bytes)} · " + (k.Frees > 0 ? $"frees {Format.Bytes(k.Frees)}" : "takes no extra space");
    public static string Vendor(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => "NVIDIA", GpuVendor.Amd => "AMD", GpuVendor.Intel => "Intel", _ => "GPU",
    };
    public static string Engine(EngineInfo? e) => e is null ? "Unknown engine" : Format.Engine(e, e.Family == "Unreal" ? "UE" : null) + Guessed(e);
    public static string Guessed(EngineInfo e) => e.VersionGuessed ? " (guessed)" : "";
    /// <summary>"Severe shader-compilation stutter: reason", then the source and when it was checked; null when not listed.</summary>
    public static string? StutterTip(GameState s) => s.KnownStutter is { } k
        ? $"{(k.Severity == StutterSeverity.Severe ? "Severe" : "Moderate")} shader-compilation stutter: {k.Reason}.\nSource: {k.Source} (checked {k.Date})" : null;
    /// <summary>Library sections, in list order. Steam's non-Steam games, GOG, Ubisoft, Battle.net, PURPLE, HoYoPlay, Gaijin and Riot Client games are Store.Other: told apart by their id prefix.</summary>
    public static readonly string[] Stores = ["Steam", SteamShortcut, "Epic Games", "Xbox / Game Pass", "EA app", "GOG", "Ubisoft Connect", "Battle.net", "PURPLE", "HoYoPlay", "Gaijin", "Riot Client", "Other", AddedByYou];
    public const string AddedByYou = "Added by you", SteamShortcut = "Steam (non-Steam game)";
    public static string StoreName(Game g) => g.Store switch
    {
        Store.Steam => "Steam", Store.Epic => "Epic Games", Store.Xbox => "Xbox / Game Pass", Store.EA => "EA app", Store.Manual => AddedByYou,
        _ => g.Id[..Math.Max(0, g.Id.IndexOf(':'))] switch { "steam-shortcut" => SteamShortcut, "gog" => "GOG", "ubisoft" => "Ubisoft Connect", "battlenet" => "Battle.net", "purple" => "PURPLE", "hoyoplay" => "HoYoPlay", "gryphline" => "GRYPHLINK", "gaijin" => "Gaijin", "riot" => "Riot Client", _ => "Other" },
    };
    /// <summary>The stores the Library's summary says games of these <see cref="StoreName"/>s were found in: a non-Steam game's
    /// is Steam, a game added by hand's none.</summary>
    public static string FoundIn(IEnumerable<string> storeNames)
    {
        var found = storeNames.Select(n => n == SteamShortcut ? "Steam" : n).ToHashSet();
        var stores = Stores.Where(n => n != AddedByYou && found.Contains(n)).ToList();
        return stores.Count > 2 ? $"{stores.Count} stores" : string.Join(" and ", stores);
    }
    /// <summary>Measured driver cache when known, else the estimate.</summary>
    public static string Cache(GameState s) => s.DiskBytes is { } c ? Format.Bytes(c) : s.EstimatedCacheBytes is { } b ? "≈ " + Format.Bytes(b) : Format.Dash;
    /// <summary>The last compile's measured time, else the estimate: the estimate's rate is measured on a cold cache only.</summary>
    public static TimeSpan? CompileTime(GameState s) => s.LastWarmTime ?? s.EstimatedWarmTime;
    /// <summary>Tooltip for an estimated (not measured) cache value; null when measured.</summary>
    public static string? CacheTip(GameState s) => s.DiskBytes == null && s.EstimatedCacheBytes != null ? "Estimated driver cache if compiled" : null;
    /// <summary>One middleware tag: what it compiles and its DLLs.</summary>
    public static string TagTip(MiddlewareTag t) => Format.Middleware(t, GpuBackends.OwnDriver(Host.Core.Vendor) == GpuVendor.Nvidia) + "\n    " + string.Join(", ", t.Dlls);
    /// <summary>The title bar's button while a downloaded update is ready (<paramref name="ready"/>), and while the restart
    /// waits for the compile (<paramref name="restarting"/>): then off, so a second click can't start another restart.</summary>
    public static (bool Visible, bool Enabled, string Label, string Tip) RestartToUpdate(string? ready, string? problem, bool installsAutomatically, bool restarting) =>
        (ready != null || restarting, !restarting, restarting ? "Finishing the compile…" : "Restart to update",
            problem ?? $"SCSKiller {ready} is ready. Restarting stops a running compile safely (the driver saves its cache first) and continues it afterwards." +
            (installsAutomatically ? " Otherwise it installs the next time SCSKiller starts or quits." : ""));
    public static bool Active(QueueItem q) => q.Stage is not (QueueStage.Done or QueueStage.Failed or QueueStage.Stopped);
    public static void PauseOrResume(QueueItem? running)
    {
        if (running?.Stage == QueueStage.Paused) Host.Core.ResumeQueue(); else Host.Core.PauseQueue();
    }

    /// <summary>The queue page's run order: the running compile (number 1), then the waiting ones; plan checks left out.</summary>
    public static (QueueItem? Running, List<QueueItem> Waiting) RunOrder(IEnumerable<QueueItem> all)
    {
        var compiles = all.Where(q => !q.PlanCheck).ToList();
        return (compiles.FirstOrDefault(Format.Running), compiles.Where(q => q.Stage == QueueStage.Waiting).ToList());
    }
    public static string Stage(QueueItem q) => q.Stage switch
    {
        QueueStage.Indexing => "Reading shaders", QueueStage.Planning => "Building plan", QueueStage.Materializing => "Preparing files",
        QueueStage.Paused => q.Note is { } n ? n.StartsWith("paused ", StringComparison.Ordinal) ? "P" + n[1..] : "Paused: " + n : "Paused",   // "paused until the PC is idle" reads alone in the queue row
        QueueStage.Merging => q.Note ?? Format.MergingStage,
        QueueStage.Saving => Format.SavingStage,
        QueueStage.Finishing => Format.FinishingStage,
        QueueStage.Warming when q.Note is { } n => "Compiling: " + n,   // stalled (ScsKiller.StalledNote)
        QueueStage.Warming when Tail(q) => "Compiling the last pipelines",
        _ => "Compiling",
    };
    /// <summary>"54.8% · 1,234 of 5,000 pipelines · 480/s" (reading shaders: packages, a plan or file preparation step: its own count,
    /// "12,340 recording entries read" while its total is unknown); null before the warm knows its total.</summary>
    public static string? ProgressText(QueueItem q) => q.Stage is QueueStage.Saving or QueueStage.Finishing ? q.Note   // elapsed time, no share
        : q is { Stage: QueueStage.Planning or QueueStage.Materializing, Progress: { Note: { } step } s } ? s.Total > 0 ? $"{Percent(q):0.0}% · {s.Done:N0} of {s.Total:N0} {step}" : $"{s.Done:N0} {step}"
        : q.Progress is { Total: > 0 } p
        ? q.Stage == QueueStage.Merging ? $"{Percent(q):0.0}% · {Format.Bytes(p.Done)} of {Format.Bytes(p.Total)}"
        : $"{Percent(q):0.0}% · {p.Done:N0} of {p.Total:N0} {(q.Stage == QueueStage.Indexing ? "packages" : "pipelines")}" + (p.PerSecond > 0 && !ScsKiller.Stalled(q) ? $" · {p.PerSecond:N0}/s" : "") + (p.Failed > 0 ? $" · {p.Failed:N0} failed" : "") + (p.Skipped > 0 ? $" · {p.Skipped:N0} skipped" : "")   // short (the Done note explains them); a count wraps whole
            + (ScsKiller.MemoryLimitNote(p) is { } m ? " · " + m : "")
        : null;
    /// <summary>The warm's last pipelines in flight: the rate says under 2 s, but the slowest ones are still compiling.</summary>
    static bool Tail(QueueItem q) => q is { Stage: QueueStage.Warming, Progress: { Total: > 0, PerSecond: > 0 } p } && (p.Total - p.Done) / p.PerSecond < 2;
    /// <summary>"started 21:14" (local time); null before the compile's first stage.</summary>
    public static string? Started(QueueItem q) => q.StartedAt is { } t ? $"started {t.LocalDateTime:t}" : null;
    public static double Percent(QueueItem? q) => q?.Progress is { Total: > 0 } p ? 100.0 * p.Done / p.Total : 0;
    public static bool Indeterminate(QueueItem? q) => q != null && q.Progress is not { Total: > 0 };
    public static TimeSpan? Left(QueueItem q, GameState? g) => ScsKiller.Stalled(q) || Tail(q) || q.Stage is not (QueueStage.Planning or QueueStage.Materializing) && q.Progress is { Total: > 0 } d && d.Done >= d.Total ? null   // no estimate for a warm that stopped moving, nor once it's through
        : q.Progress is { PerSecond: > 0 } p ? TimeSpan.FromSeconds((p.Total - p.Done) / p.PerSecond)
        : q.Stage is QueueStage.Merging or QueueStage.Saving or QueueStage.Finishing ? null   // no rate yet, or nothing to count down
        : g is not null ? g.EstimatedWarmTime ?? g.LastWarmTime : null;

    /// <summary>The game's compile runs (or is paused), or is the next the running queue starts: Play waits, since the warm
    /// stages a process under the game's exe name. A stopped queue's items and "when idle" items don't block it.</summary>
    public static bool CompilingSoon(IEnumerable<QueueItem> queue, string gameId)
    {
        var compiles = queue.Where(q => !q.PlanCheck).ToList();
        return compiles.Any(q => q.GameId == gameId && Format.Running(q))
            || Host.Core.QueueRunning && compiles.FirstOrDefault(q => q.Stage == QueueStage.Waiting && !ScsKiller.WaitsOnItsOwn(q))?.GameId == gameId;
    }

    public const string CompilingTip = "Compiling: play when it finishes";

    /// <summary>What IScsKiller doesn't carry, from the real core or the design data.</summary>
    public static ManualSource? Added => Host.Core is ScsKiller k ? k.Manual : Host.Design?.Manual;
    public static string? ScanProgress => Host.Core is ScsKiller k ? k.ScanProgress : Host.Design?.ScanProgress;
    public static string? AfterUpdateLine => Host.Core is ScsKiller k ? k.AfterUpdateLine : Host.Design?.AfterUpdateLine;
    public static string? FoldLine(string gameId) => Host.Core is ScsKiller k ? k.FoldLine(gameId) : Host.Design?.FoldLine(gameId);
    public static void Rename(string gameId, string name)
    {
        if (Host.Core is ScsKiller k) k.RenameManualGame(gameId, name);
        else Host.Design?.RenameManualGame(gameId, name);
    }

    /// <summary>Starts the game through its store (<see cref="StoreLaunch"/>); the error to show, or null. The design data starts nothing.</summary>
    public static string? Play(GameState s)
    {
        var g = s.Game;
        if (StoreLaunch.OwnLauncherOnly(s)) return $"Start {g.Name} from its own launcher";
        if (Host.Design != null) return null;
        try
        {
            if (StoreLaunch.Command(g) is not { } command) return $"Couldn't find how {StoreName(g)} starts {g.Name}: refresh the library.";
            System.Diagnostics.Process.Start(command)?.Dispose();
            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"Couldn't start {g.Name}{(g.Store == Store.Manual ? "" : " through " + StoreName(g))}: {e.Message}";
        }
    }

    /// <summary>The Play button's tooltip when it can start the game.</summary>
    public static string PlayVia(Game g) => g.Store == Store.Manual ? $"Start {FileName(g.ExePath)}"
        // the store starts the collection, which opens the game played last
        : Collections.Member(g.Id) != null ? $"Play through {StoreName(g)}, then pick {g.Name} in the game's menu if another game opens"
        : $"Play through {StoreName(g)}";
}
