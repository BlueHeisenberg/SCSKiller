using System.Globalization;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Ui.Models;

/// <summary>The status card's icon, which the UI draws from its icon font.</summary>
public enum StatusIcon { Warning, Warmed, Ready, Record, Blocked }

public sealed class DetailVm(string id) : Bindable
{
    GameState s = Host.Core.Games.First(g => g.Game.Id == id);
    public GameRow Row { get; private set; } = new(Host.Core.Games.First(g => g.Game.Id == id));
    public string? Error { get; set; }
    public bool HasError => Error != null;

    public void Refresh()
    {
        place = InQueue(id);
        var now = Host.Core.Games.FirstOrDefault(g => g.Game.Id == id);
        if (now != null && s.Playing && !now.Playing) ReadCaches();   // the game exited: its Windows and pipeline caches grew
        if (now != null && (now != s || Row.Icon != Host.Icons.Get(now.Game.ExePath))) { s = now; Row = new GameRow(now); }   // or its icon failed: the tile
        ReadBlocked();
        ReadLaunchLine();
        ReadSteamCacheNote();
        Changed();
    }

    static string Sentence(string t) => t.Length > 0 ? char.ToUpperInvariant(t[0]) + t[1..] : t;
    bool Partial => ScsKiller.IsPartial(s.Plan);
    bool KeyMissing => s.Engine?.KeyMissing == true;
    /// <summary>Needs a recording for its ray tracing only: the rest compiles (a partial compile, offered but not the primary action).</summary>
    bool RtPartial => s.Status == GameStatus.NeedsRecording && ScsKiller.NeedsRtRecording(s);
    /// <summary>Compiled, and a recording would add its ray tracing: the record tip, not the warning callout.</summary>
    bool RtAfter => ScsKiller.RtAfterRecording(s);
    public bool NoAntiCheat => !s.AnyAntiCheat;
    /// <summary>The recorder may go next to the game: no anti-cheat, not a game added by hand whose folder isn't confirmed, and
    /// not one whose first d3d12.dll Streamline loads on NVIDIA.</summary>
    bool CanRecord => NoAntiCheat && !s.RootUnconfirmed && !s.StreamlineFirst;

    public string Name => s.Game.Name;
    public string Sub => $"{Fmt.FileName(s.Game.ExePath)} · {Fmt.StoreName(s.Game)}";   // engine and API are tags

    // Status: the state in its colour and why; the actions sit beside it.
    public StatusIcon StatusIcon => s.Status switch
    {
        _ when s.CompileUnreached || ScsKiller.IsPartlyCompiled(s) || ScsKiller.NeedsSteamStart(s) => StatusIcon.Warning,
        GameStatus.Warmed => StatusIcon.Warmed, GameStatus.Ready => StatusIcon.Ready, GameStatus.NeedsRecording => StatusIcon.Record,
        GameStatus.Stale => StatusIcon.Warning, _ => StatusIcon.Blocked,
    };
    // The queue page's progress for this game: its number in the run order while it waits, its stage and progress while it runs.
    (QueueItem? Item, int Number) place = InQueue(id);
    static (QueueItem?, int) InQueue(string id)
    {
        var (running, waiting) = Fmt.RunOrder(Host.Core.Queue);
        if (running?.GameId == id) return (running, 1);
        int i = waiting.FindIndex(q => q.GameId == id);
        return i < 0 ? (null, 0) : (waiting[i], i + (running != null ? 2 : 1));
    }
    /// <summary>A warm's tick: only the progress changes, unless the item started, finished or left the queue.</summary>
    public void RefreshProgress()
    {
        var was = place.Item?.Stage;
        place = InQueue(id);
        if (place.Item?.Stage != was) Refresh();
        else Changed(nameof(StatusTitle), nameof(StatusReason), nameof(Percent), nameof(Indeterminate));
    }
    QueueItem? Running => place.Item is { } q && Format.Running(q) ? q : null;
    public bool IsCompiling => Running != null;
    public double Percent => Fmt.Percent(Running);
    public bool Indeterminate => Fmt.Indeterminate(Running);

    public string StatusTitle => Running is { } r ? Fmt.Stage(r) : place.Item != null ? $"In queue (position {place.Number})" : Row.StatusText;
    public string StatusReason => Running is { } r && (Fmt.ProgressText(r) is { } p
            ? p + (r.Progress is { PerSecond: > 0 } && Fmt.Left(r, s) is { } t ? $" · {Format.Duration(t)} left" : "") + (Fmt.Started(r) is { } at ? " · " + at : "")
            : Fmt.Started(r)) is { } line
        ? Sentence(line)
        : place.Item is { Stage: QueueStage.Waiting, Note: { } n } ? Sentence(n)
        : Fmt.FoldLine(id) ?? GameReason;
    string GameReason => Sentence(s.Status switch
    {
        _ when ScsKiller.CacheOffRefused(Host.Core.Queue, id) => ScsKiller.CacheOffReason,
        _ when s.CompileUnreached => s.StatusReason + (s.WarmedAt != null ? ". " + Sentence(ScsKiller.ClearForGameNote) : ""),
        _ when ScsKiller.IsPartlyCompiled(s) => ScsKiller.RefusedNote(s) + ". An SCSKiller update could fix it",
        GameStatus.Warmed when ScsKiller.IsPartlyWarmed(s) => s.StatusReason,
        GameStatus.Warmed => (Partial || RtAfter || KeyMissing ? "" : "cache complete, ") + $"compiled for {(ScsKiller.BothApis(s.Plan) ? "DirectX 11 and DirectX 12, for " : "")}driver {s.WarmedDriverVersion}" + (s.WarmedAt is { } t ? $" on {t.LocalDateTime:g}" : "")
            + (RtAfter ? ". " + Sentence(ScsKiller.RtAfterRecordingNote) : "") + (KeyMissing ? ". " + Sentence(ScsKiller.KeyForFullCoverage) : ""),
        // a cache here: played since a clear or a stopped compile; then the planner's note when the row has one to say
        GameStatus.Ready => (s.CacheOnDisk > 0 ? $"not fully compiled yet: {Format.Bytes(s.CacheOnDisk)} of it is in the driver cache"
            : s.LastWarmTime == null ? "not compiled yet" : "its shader cache is empty: compile it again")
            + (Row.Note != null ? ". " + Sentence(s.StatusReason) : ""),
        GameStatus.NeedsRecording => HasDbTeaser ? NoDbNote(Row.FullNote) : Row.FullNote,   // the teaser says it
        _ when s.Engine?.Encrypted == true => "needs the game's AES key",   // the title: "Encrypted game files"
        _ => HasDbTeaser ? NoDbNote(s.StatusReason) : s.StatusReason,
    })
        + (IsPartlyCompiled || s.CompileUnreached ? "." : "") + Format.ModNote(s);
    static string NoDbNote(string r) => r.Replace(". " + Sentence(ScsKiller.InDbSupportersNote), "").Replace(". " + Sentence(ScsKiller.InDbNote), "");
    public bool IsPartlyCompiled => ScsKiller.IsPartlyCompiled(s);
    public bool NeedsAesKey => ScsKiller.NeedsAesKey(s);
    public bool HasAesKey => Host.Core.HasEncryptionKey(id);
    public bool CanChangeAesKey => HasAesKey && !NeedsAesKey;
    public bool NeedsGameKey => ScsKiller.NeedsGameKey(s);
    public const string RemoveAesKeyNote = "The key is deleted from this PC. The compiled cache stays. The next compile plans without the shaders the key unlocked, and if the game's exe holds the key, SCSKiller finds it again.";
    public string PartlyCompiledWhy => s.LastWarmFailedBy switch
    {
        0 or 1 => $"SCSKiller compiled this game, but the graphics driver refused a large part of it. This happens when the driver or {Host.Platform.Runtime} can't build pipelines the way the game does. The refused ones are built by the game the first time it needs them, which is when they can stutter. SCSKiller compiles them again after a driver update.",
        2 => "SCSKiller compiled this game, but it couldn't build a large part of it from the game's files. The game builds those itself the first time it needs them, which is when they can stutter.",
        _ => "SCSKiller compiled this game, but a large part of it couldn't be compiled. The game builds those itself the first time it needs them, which is when they can stutter. SCSKiller compiles them again after a driver update.",
    };

    // The manifest is public: a PC without "db" sees that the community database covers the game, and where to get it.
    // Shown, it is the page's only mention of the database.
    public bool HasDbTeaser => s is { InCommunityDb: true, Community: null } && Host.Account?.HasDb != true
        && (s.Status == GameStatus.NeedsRecording || RtAfter || s.Status == GameStatus.Unsupported && !NoAntiCheat && s.StatusReason.StartsWith("needs a recording, which", StringComparison.Ordinal));
    public string DbTeaser => $"In the community database: {s.CommunityDbPsos:N0} pipeline{(s.CommunityDbPsos == 1 ? "" : "s")} recorded by other players. Patreon supporters compile them without recording.";
    public string DbTeaserLink => Host.Account?.SignedIn == true ? "Patreon membership" : "Sign in with Patreon";
    public bool CanCompile => s.Status is GameStatus.Ready or GameStatus.Warmed or GameStatus.Stale || RtPartial;
    public bool ShowMaxCompile => CanCompile && ScsKiller.MaxCompileAdds(Host.Core.Vendor.Caps, s.Engine);
    public const string MaxCompileWhy = "Compiles every combination of this game's shaders, not just each one once. It takes longer and uses more disk space, but potentially fewer shaders compile while you play. Seems to help most on AMD.";
    public string? GuessNote => s.Engine is { } e && s.Status is GameStatus.Ready or GameStatus.Warmed or GameStatus.Stale ? ScsKiller.GuessNote(e) : null;
    public bool HasGuessNote => GuessNote != null;
    public string? DeliveryLine => ScsKiller.DeliveryLine(s);
    public bool HasDeliveryLine => DeliveryLine != null;
    // read live: a game's state keeps the flag until its next refresh
    public string? CleanupLine => D3DSCache.CleanupKept() == true ? null : ScsKiller.CleanupLine(s);
    public bool HasCleanupLine => CleanupLine != null;
    public bool CanKeepShaders { get; private set; } = true;
    public async Task KeepShadersAsync()
    {
        CanKeepShaders = false;
        Changed();
        var r = await SettingsVm.KeepShadersAsync(true);
        CanKeepShaders = true;
        if (r is { Ok: false }) { Error = "Not changed: " + r.Message; Changed(); }
        else RefreshGame();
    }
    public string? VulkanRtNote => ScsKiller.VulkanRtNote(s, GpuBackends.OwnDriver(Host.Core.Vendor));
    public bool HasVulkanRtNote => VulkanRtNote != null;
    // warmed: nothing urgent, unless only partly (the careful compile is offered); RT: Record comes first; no shader stutter: not needed
    public bool CompileAccent => (OffersCareful || s.Status != GameStatus.Warmed && !RtPartial) && s.NoStutter == null;
    bool Queued => Host.Core.Queue.Any(q => q.GameId == id && Fmt.Active(q) && !q.PlanCheck);
    public bool CanAdd => !Queued && !BaInProgressAny;
    /// <summary>A pair before Done: a compile or clear of the user's own would spoil a run (Core refuses them too).</summary>
    bool BaInProgressAny => s.BeforeAfter is { Stage: not BeforeAfterStage.Done };
    const string BaBusyTip = "A before and after recording is in progress";
    public string? CompileTip => BaInProgressAny ? BaBusyTip : null;
    public bool CanClearCache => HasCache && !BaInProgressAny;
    bool CompilingSoon => Fmt.CompilingSoon(Host.Core.Queue, id);
    public bool ShowPlay => Row.CanLaunch;
    public bool CanPlay => !s.Playing && !CompilingSoon;
    public string PlayText => s.Playing ? "Running" : "Play";
    public string PlayTip => s.Playing ? $"{s.Game.Name} is running" : CompilingSoon ? Fmt.CompilingTip : Fmt.PlayVia(s.Game);
    public bool IsManual => s.Game.Store == Store.Manual;
    public bool CanChooseExe => !IsManual && Collections.Member(s.Game.Id) == null;
    public bool ExeChosen => CanChooseExe && s.ExeChosen;
    public string RemoveTip => IsManual ? "Forget this game. Its recorder leaves the game folder, and the game's own files stay."
        : "Hide this game. Its files, saves and compiled cache stay.";
    /// <summary>The confirmation's text for a store's game, which is hidden.</summary>
    public string HideNote => "It stays out of the list and out of automatic compiles. Its files, saves and compiled cache stay. Show hidden on the Library page brings it back."
        + (BaInProgressAny ? " The before and after recording in progress is cancelled." : "")
        + (s.RecorderInstalled ? " The recorder is turned off and leaves the game folder first." : "");
    public async void Play()
    {
        Error = await BeforeAfterUi.PlayAsync(s);
        Changed();
    }
    // Before and after recording (doc 6.5): one button in the status card, its note under it, a menu beside it once started
    // off the UI thread: it loads game records and lists processes. Until the first read is in, the button is off with no note
    string? blocked;
    bool blockedKnown;
    BackgroundRead<string?>? blockedRead;
    void ReadBlocked() => (blockedRead ??= new("game blocked", () => Blocked(id), b =>
    {
        if (blockedKnown && b == blocked) return;
        (blocked, blockedKnown) = (b, true);
        Changed();
    })).Request();
    static string? Blocked(string id)
    {
        if (!Host.Core.Settings.BeforeAfter) return null;
        try
        {
            var why = Host.Core.BeforeAfterBlocked(id);
            return why != null && why == Host.Core.Games.First(g => g.Game.Id == id).StatusReason ? Sentence(why) : why;   // a status reason starts lowercase
        }
        catch (Exception e) { return BeforeAfterUi.Describe(e); }
    }
    BeforeAfterInfo? Ba => s.BeforeAfter;
    (bool Shown, bool Enabled, string Note) BaStart => BeforeAfter.StartButton(Host.Core.Settings.BeforeAfter, blockedKnown ? blocked : "");
    public bool ShowBa => Ba != null ? Host.Core.Settings.BeforeAfter : BaStart.Shown;
    public bool BaHasMenu => ShowBa && Ba != null;
    public bool BaVoided => BeforeAfterUi.Voided(Ba);
    public bool BaInProgress => Ba is { Stage: not BeforeAfterStage.Done };   // voided too: Cancel stays
    public bool BaDone => Ba is { Stage: BeforeAfterStage.Done } && !BaVoided;
    bool BaLaunchable => BeforeAfterUi.Launchable(s);
    public string BaText => Ba switch
    {
        null => "Before and after",
        _ when BaVoided => "Start over",
        { Stage: BeforeAfterStage.BeforeArmed, ClearPending: true } => "Clear the cache and try again",
        { Stage: BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed } => BaLaunchable ? $"Start {Name}" : $"Start {Name} as usual",
        { Stage: BeforeAfterStage.BeforePlaying or BeforeAfterStage.AfterPlaying } p => p.Minutes * 60 - p.PlayedSeconds is var left and > 0
            ? $"Before and after: playing, {(int)left / 60}:{(int)left % 60:00} left" : "Before and after: time's up, close the game",
        { Stage: BeforeAfterStage.Compiling or BeforeAfterStage.AfterReady } => "Continue with the after recording",
        _ => "View before and after",
    };
    public bool BaEnabled => Ba?.Stage switch
    {
        null => BaStart.Enabled,
        _ when BaVoided || Ba!.ClearPending => !s.Playing,
        BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed => BaLaunchable && !s.Playing,
        BeforeAfterStage.AfterReady => !s.Playing,
        BeforeAfterStage.Done => true,
        _ => false,
    };
    public bool BaAccent => BaVoided || Ba?.Stage is BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed or BeforeAfterStage.AfterReady;
    public string? BaTip => Ba == null ? blocked ?? "Measure this game cold, compile it, measure it again, and get a report you can share"
        : Ba.Stage is BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed && !BaLaunchable ? $"Start {Name} as usual: SCSKiller sees it start" : null;
    public string BaNote => Ba switch
    {
        null => BaStart.Note,
        { Problem: { } problem } => problem,
        { Stage: BeforeAfterStage.BeforeArmed } b => $"Play {b.Minutes} minutes from a save you can load again, then close the game",
        { Stage: BeforeAfterStage.AfterArmed } a => $"Load the same save and play the same {a.Minutes} minutes",
        { Stage: BeforeAfterStage.BeforePlaying or BeforeAfterStage.AfterPlaying } => "Close the game when the time is up",
        { Stage: BeforeAfterStage.Compiling } => "Waiting for the compile",
        { Stage: BeforeAfterStage.AfterReady } => "Compiled: next, the after run",
        _ => "",
    };
    public bool HasBaNote => ShowBa && BaNote.Length > 0;
    /// <summary>The compile failed or was stopped: the note says why.</summary>
    public bool BaCompileAgain => Ba is { Stage: BeforeAfterStage.Compiling, Problem: not null } && !BaVoided && !Queued;

    public string AddText => Queued ? "In queue" : OffersCareful ? "Compile carefully" : RtPartial ? "Compile without ray tracing (partial)" : "Compile";
    /// <summary>Partly warmed by a fast compile: the compile button turns the careful compile on first.</summary>
    public bool OffersCareful => ScsKiller.IsPartlyWarmed(s) && s.Careful is { On: false };

    // AMD's careful compile: a switch in the compile card, off while the game is queued (a running compile keeps its schedule)
    public bool HasCareful => s.Careful != null && CanCompile;
    public bool? CarefulPending { get; set; }
    public bool LeftOutOn => LeftOutPending ?? s.LeftOutOfAddAll;
    public bool? LeftOutPending { get; set; }
    public bool SharedOn => SharedPending ?? s.SharedHashes;
    public bool? SharedPending { get; set; }
    public string SharedNote => s.SharedChoice == null ? "Follows Settings" : "Set for this game";
    public bool ShowSharedFollow => SharedPending == null && s.SharedChoice != null;
    public bool CarefulOn => CarefulPending ?? s.Careful?.On == true;
    public bool CanToggleCareful => CarefulPending == null && !Queued;
    public string CarefulNote => "Only for games whose compile fails. " + (s.Careful is { Recorded: 0 } ? "Compiles the pipelines recorded in the game in passes on few threads, so the driver keeps more of them. This game has no recording yet, so there is nothing to compile carefully."
        : $"Compiles the pipelines recorded in the game in passes on {ScsKiller.AmdCarefulThreads} threads, so the driver keeps more of them. The rest compile at full speed."
          + (s.Careful?.Estimate is { } est ? $" About {Format.Duration(est)}" + (s.Careful.On || Fmt.CompileTime(s) is not { } fast ? "." : $" instead of {Format.Duration(fast)}.") : ""))
        + (s.Careful?.LaunchCompiled is { } lc ? $" After the last compile, the game's first launch still compiled {lc * 100:0}% of its pipelines." : "");
    // AMD: the compiled cache kept aside, in the compile card
    public bool HasKept => s.Kept != null && Host.Core.Vendor.Vendor == GpuVendor.Amd && Host.Core.Settings.KeepCachesAside;
    public string KeptSize => s.Kept is { } k ? Fmt.KeptSize(k) : "";
    public string KeptState => s.Kept switch
    {
        null => "",
        { Unkept: > 0 and var n, HeldBy: var who } => $"{n} file{(n == 1 ? " was" : "s were")} in use by {who ?? "another app"} and {(n == 1 ? "isn't" : "aren't")} kept aside.",
        { Frees: > 0 } when s.AntiCheat != AntiCheat.None => "Put back within a few seconds after the game starts. Keep SCSKiller running.",   // no Play button, VAC keeps it
        { Frees: > 0 } => "The driver removed part of it. Put back when you press Play.",
        { PutBackAt: { } at, Late: true } => $"Put back {Fmt.When(at)}, after the game had started. Some shaders may have compiled again.",
        { PutBackAt: { } at } => $"Put back {Fmt.When(at)}, before the game started.",
        _ => "In the driver's cache now.",
    };

    // 0 = the global setting, then NewShadersMode's values in order
    public static readonly string[] NewShadersLabels = ["Off", "Notify me", "Compile automatically and notify me", "Compile automatically, silently"];
    public List<string> NewShadersChoices => Same(ref newShadersChoices, [$"Use the global setting ({NewShadersLabels[(int)NewShaders.Global(Host.Core.Settings)]})", .. NewShadersLabels]);
    // the same list while its labels hold: a new one resets the selection
    List<string>? newShadersChoices, rayTracingChoices, cacheCapChoices, traversalChoices;
    static List<string> Same(ref List<string>? kept, List<string> now) => kept != null && kept.SequenceEqual(now) ? kept : kept = now;
    public int NewShadersIndex => s.NewShadersOverride is { } m ? (int)m + 1 : 0;
    // 0 = the global setting, 1 = on, 2 = off
    public List<string> RayTracingChoices => Same(ref rayTracingChoices, [$"Default ({(Host.Core.Settings.CompileRayTracing ? "on" : "off")})", "On", "Off"]);
    public int RayTracingIndex => s.RayTracingOverride switch { null => 0, true => 1, false => 2 };
    public bool ShowTraversal => TraversalFix.Applies(s, Host.Core.Vendor.Caps);
    public List<string> TraversalChoices => Same(ref traversalChoices, TraversalFix.GameChoices(Host.Core.Settings, s));
    public int TraversalIndex => TraversalFix.GameIndex(s);
    public string TraversalNote => TraversalFix.GameNote(s, Host.Core.Vendor.Gpu);
    public bool TraversalLowVram => TraversalFix.LowVram(Host.Core.Vendor.Gpu) == true;
    public string TraversalWarning => TraversalFix.LowVramWarning;
    // no plan, or one from before inline ray tracing was counted: unknown, so the choice shows
    public bool ShowRayTracing => P?.RtInline == null || s.RayTracingOverride != null || ScsKiller.HasRayTracing(P);
    public List<string> CacheCapChoices => Same(ref cacheCapChoices, ["No limit", .. ScsKiller.CacheCapsGB.Select(gb => $"{gb} GB")]);
    public int CacheCapIndex => s.CacheCapGB is { } gb ? Array.IndexOf(ScsKiller.CacheCapsGB, gb) + 1 : 0;
    public bool ShowCacheCap => ScsKiller.HasCacheCap(s);
    // Windows' shader cache of the game, read off the UI thread: Clear cache clears it too, so it can be the only cache to clear.
    BackgroundRead<long>? otherCaches;
    long otherBytes;
    public void ReadCaches() => (otherCaches ??= new("game caches", OtherCacheBytes, b => { otherBytes = b; Changed(); })).Request();
    long OtherCacheBytes()
    {
        try { return Host.Core.GameCaches(id).Where(p => p.Name != ScsKiller.DriverPart).Sum(p => p.Bytes); }
        catch (Exception) { return 0; }   // e.g. the game is gone after a rescan
    }
    // read off the UI thread: it reads Steam's localconfig.vdf
    LaunchLine? launchLine;
    BackgroundRead<LaunchLine?>? launchRead;
    void ReadLaunchLine()
    {
        if (!Host.Platform.SteamLaunchLine) return;
        (launchRead ??= new("launch line", () => LaunchLineOf(id), l =>
        {
            if (l == launchLine) return;
            launchLine = l;
            Changed();
        })).Request();
    }
    static LaunchLine? LaunchLineOf(string id)
    {
        try { return Host.Core.LaunchLineOf(id); }
        catch (Exception) { return null; }   // e.g. on Mesa, a game outside the Steam libraries
    }
    public bool HasLaunchLine => launchLine != null;
    public bool OffersLaunchLine => launchLine is { Refused: null };
    public string LaunchLineText => launchLine?.Text ?? "";
    public bool LaunchLineSet => launchLine?.Set == true;
    public bool CanCopyLaunchLine => launchLine is { Set: false, Refused: null };
    public string? LaunchLineRefused => launchLine?.Refused is { } why ? $"No launch options to paste: {why}." : null;
    public bool HasLaunchLineRefused => LaunchLineRefused != null;
    bool launchCopied;
    public string CopyLaunchText => launchCopied ? "Copied" : "Copy";
    public async Task CopyLaunchLine()
    {
        if (launchLine is not { Refused: null } l) return;
        try
        {
            await Task.Run(() => Host.Core.LaunchLineOf(id, make: true));   // the folders it names, which opening the page doesn't make
            await Host.CopyText(l.Text);
            (launchCopied, Error) = (true, null);
        }
        catch (Exception e) { Error = "Couldn't copy: " + e.Message; }
        Changed();
    }
    // read off the UI thread: it reads Steam's localconfig.vdf and the placement's manifest
    string? steamCacheNote;
    BackgroundRead<string?>? steamNoteRead;
    void ReadSteamCacheNote()
    {
        if (!Host.Platform.SteamLaunchLine) return;
        (steamNoteRead ??= new("Steam note", () =>
        {
            try { return Host.Core.SteamCacheNote(id); }
            catch (Exception) { return null; }   // e.g. the game is gone after a rescan
        }, n =>
        {
            if (n == steamCacheNote) return;
            steamCacheNote = n;
            Changed();
        })).Request();
    }
    public string SteamCacheNote => steamCacheNote ?? "";
    public bool HasSteamCacheNote => steamCacheNote != null;

    public bool HasCache => s.DiskBytes != null || otherBytes > 0 || s.Status is GameStatus.Warmed or GameStatus.Stale;
    // compiled before a verdict changed: the cache stays until the user clears it
    public string? KeptCacheNote => (s.Status == GameStatus.Unsupported || s.NoStutter != null) && s.WarmedAt != null && s.CacheOnDisk > 0
        ? $"SCSKiller compiled {DiskValue} of cache for this game before it was marked {(s.NoStutter != null ? "as having no shader stutter" : "not supported")}."
        : null;
    public bool HasKeptCache => KeptCacheNote != null;

    // Maximum mode suggested for this game on this GPU vendor (Games.MaximumMode), or on for it alone
    public bool? MaximumPending { get; set; }
    public bool MaximumSuggested => s.MaximumSuggestion != null;
    // on for every game in Settings: the per-game switch changes nothing, so it isn't offered
    public bool HasMaximumLine => MaximumSuggested || s.MaximumForGame == true && !Host.Core.Settings.MaximumPlans;
    public string MaximumLine => MaximumSuggested
        ? "Maximum mode helps this game: it also compiles every pairing of its shaders, so fewer compile while you play."
        : "Maximum mode is on for this game. Its compiles take longer and use more disk space.";
    public string? MaximumNote => string.Join(" ", new[] { s.MaximumSuggestion is { Length: > 0 } note ? note : null,
        MaximumSuggested && s.MaximumAddsBytes is { } adds ? $"On AMD it adds up to {Format.Bytes(adds)} to the driver's shared {Format.Bytes(AmdAppCache.DxcCacheCap)} cache." : null }.OfType<string>()) is { Length: > 0 } t ? t : null;
    public bool HasMaximumNote => MaximumNote != null;
    public string MaximumAction => MaximumSuggested ? "Turn on for this game" : "Turn off for this game";
    public bool CanSetMaximum => MaximumPending == null && !Queued;
    public bool ShowClearCache => HasCache || CanCompile;
    // as Disk space shows it
    public string ClearCacheText => s.DiskBytes != null ? $"Clear cache ({DiskValue})" : "Clear cache";
    public string ClearCacheTip => BaInProgressAny ? BaBusyTip : HasCache ? $"Deletes this game's shader caches (the driver's{(Host.Platform.SystemShaderCache ? " and Windows'" : "")}, and the game's own if you choose) so its next run starts cold"
        : "SCSKiller learns this game's driver cache files the first time it compiles it or sees it running";

    // Rate this compile (CompatReport) and what players with a similar system reported (CompatFeed)
    string? ReportWhyNot => CompatReport.WhyNot(s, ScsKiller.PackGpu(Host.Core.Vendor.Vendor), Host.Core.LinuxReportsRefused);
    public bool CanReport => ReportWhyNot == null;
    public string ReportTip => ReportWhyNot ?? "Tell other players how this game ran after SCSKiller compiled it";
    public bool HasSimilar => s.Similar is SimilarSystems.Helped or SimilarSystems.NoHelp;
    public bool HasSimilarWarning => s.Similar == SimilarSystems.Warning;
    public bool HasRuntimeWarning => s.RuntimeWarning != null;
    public string RuntimeWarning => s.RuntimeWarning ?? "";
    public string SimilarText => s.Similar switch
    {
        SimilarSystems.Helped => "Users with a similar system reported that compiling removed or reduced the stutter in this game.",
        SimilarSystems.NoHelp => "Users with a similar system reported that compiling did not help in this game.",
        SimilarSystems.Warning => $"Users with a similar system reported that {Name} ran worse after compiling.",
        _ => "",
    };

    public bool IsKnownStutter => s.KnownStutter != null;
    public string StutterReason => s.KnownStutter?.Reason ?? "";
    public bool IsNoStutter => s.NoStutter != null;
    public string NoStutterReason => s.NoStutter ?? "";
    /// <summary>The source as a link when it's a URL; otherwise <see cref="StutterCitation"/> shows it as text.</summary>
    public Uri? StutterUri => Uri.TryCreate(s.KnownStutter?.Source, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u : null;
    public string StutterLinkText => StutterUri != null ? $"Source ({s.KnownStutter!.Date})" : "";
    public string StutterCitation => s.KnownStutter is { } k && StutterUri == null ? $"Source: {k.Source} ({k.Date})" : "";
    public string EngineChip => s.Engine is { } e ? Format.Engine(e, e.Family == "Unreal" ? "Unreal Engine" : null) + Fmt.Guessed(e) + (e.Fork != null ? " · custom fork" : "") : "Unknown engine";
    public string ApiChip => s.Engine?.GraphicsApi.Replace("D3D", "DirectX ") ?? "";
    public bool HasApi => ApiChip.Length > 0 && !ShowApiPicker;

    // DirectX picker: every DirectX game (GameState.ApiDetected), or a choice is kept; the chip's place
    // or a carved game whose API nothing tells, packed shaders too: it may run Vulkan at run time (SDL)
    public bool ShowApiPicker => s is { ApiDetected: not null } or { ApiOverride: not null } && s.Engine is { Unsupported: null, Encrypted: false }
        || s.Engine is { Encrypted: false } e && Core.Carved.CarvedReader.Carved(e) && s.ApiDetected == Core.Unreal.UnrealRhi.Ambiguous;
    public bool? ApiPending { get; set; }
    public bool CanPickApi => ApiPending == null && !Host.Core.Queue.Any(q => q.GameId == id && Format.Running(q));   // its plan check too: the core refuses a change meanwhile
    IReadOnlyList<DxApi> ApiChoices => ScsKiller.ApiChoices(s, Host.Core.Vendor.Caps);
    public int ApiIndex => s.ApiOverride is { } a ? ApiChoices.ToList().IndexOf(a) + 1 : 0;
    /// <summary>The API of the picker's item <paramref name="i"/>; null = Auto.</summary>
    public DxApi? ApiAt(int i) => i <= 0 ? null : ApiChoices[i - 1];
    string[]? apiItems;
    public string[] ApiItems
    {
        get
        {
            var auto = s.Engine?.GraphicsApi.EndsWith(ScsKiller.RecordingSuffix, StringComparison.Ordinal) == true ? $"Auto ({Dx(s.Engine.GraphicsApi)} from your recording)"
                : s.ApiDetected == Core.Unreal.UnrealRhi.Ambiguous ? "Auto (DirectX 11 and 12)"
                : s.ApiDetected is { } d ? $"Auto ({Dx(d)})" : "Auto";
            string[] items = [auto, .. ApiChoices.Select(ScsKiller.ApiName)];
            if (apiItems == null || !apiItems.SequenceEqual(items)) apiItems = items;   // the same array while the labels hold: a new one resets the selection
            return apiItems;
        }
    }
    static string Dx(string api) => ScsKiller.ApiName(api);
    public string? ApiOverruled => ScsKiller.ApiOverruled(s);
    public bool HasApiOverruled => ApiOverruled != null;
    public string? ApiKept => ScsKiller.KeptApisNote(s);
    public bool HasApiKept => ApiKept != null;
    public string AntiCheatChip => NoAntiCheat ? "No anti-cheat" : ScsKiller.AntiCheatName(s);
    public string AntiCheatTip => NoAntiCheat ? "No anti-cheat found: this game can be recorded"
        : s.RiskEligible ? $"This game contains {ScsKiller.AntiCheatName(s)}. The recorder runs only in a session you confirm, at your own risk."
        : !Host.Platform.RecordAntiCheatGames ? NoAntiCheatSessions
        : ScsKiller.RiskNotHere(s) ?? $"{ScsKiller.AntiCheatName(s)} treats an extra {RecorderDll} as tampering: this game is never recorded";
    static string NoAntiCheatSessions => $"Recording anti-cheat games isn't available on {Host.Platform.Name} yet.";
    public bool HasMiddleware => s.Middleware is { Count: > 0 };
    public bool HasModChip => GameRow.ModChip(s) != null;
    public string ModChip => GameRow.ModChip(s)?.Text ?? "";
    public string ModTip => GameRow.ModChip(s)?.Tip ?? "";
    public bool HasDeliveryChip => ScsKiller.DeliveryChip(s) != null;
    public string DeliveryChip => ScsKiller.DeliveryChip(s) ?? "";
    public string DeliveryTip => ScsKiller.DeliveryTip;
    IReadOnlyList<MiddlewareTag> Mw => s.Middleware ?? [];
    /// <summary>"Also compiles" only names tags with known pipelines (and DLSS on NVIDIA, by the driver) of a game that compiles; else what was detected.</summary>
    public string MiddlewareChip => Mw.Where(t => t.Pipelines > 0).Select(t => t.Label)
            .Concat(Mw.Any(t => t.Label == "DLSS") && GpuBackends.OwnDriver(Host.Core.Vendor) == GpuVendor.Nvidia ? ["DLSS (by the driver)"] : []).ToList() is { Count: > 0 } also
            && s.Status != GameStatus.Unsupported
        ? "Also compiles: " + string.Join(" · ", also) : "Detected: " + string.Join(" · ", Mw.Select(t => t.Label));
    public string MiddlewareTip => "Upscaler DLLs next to the game. Their pipelines are compiled with it once a recording of any game has seen that DLL version"
        + ": on this PC, or for FSR and XeSS on any PC that shares them"
        + (s.Plan is { MiddlewareItems: > 0 } p ? $" (this plan: {Fmt.N(p.MiddlewareItems)} of theirs)" : "") + ".\n\n"
        + string.Join("\n", Mw.Select(Fmt.TagTip));

    // The one tip left: a game that can't compile before a recording, or not its ray tracing (a plan's other gaps are the
    // coverage card's "what's left").
    public bool HasRecordTip => CanRecord && (s.Status == GameStatus.NeedsRecording && !ScsKiller.NeedsSteamStart(s) || RtAfter) && !RecordOn && s.NoStutter == null;
    bool Enough => ScsKiller.RecordedEnough(s);
    public string RecordTipTitle => Enough ? "Record more play" : "Record 5 minutes of play";
    public string RecordTip => RtPartial || RtAfter ? $"{TurnOnAndPlay} with ray tracing on{(Enough ? "" : " for about 5 minutes")}. " + ScsKiller.RtWhy(Host.Core.Vendor.Caps, s.Engine)
            + (RtAfter ? " Everything else is compiled." : " Everything else compiles from the game files already.")
        : Enough ? Sentence(s.StatusReason) + "."
        : $"{TurnOnAndPlay} as usual for about 5 minutes. SCSKiller learns this game's shader layout from it, then it can compile.";
    // a collection's other game is recorded by its first game's recorder, switched on that game's page
    string TurnOnAndPlay => Collections.RecordedBy(s.Game.Id) is { } by ? $"Turn on recording on {Collections.Member(by)!.Value.Game.Name}'s page and play this game" : "Turn on recording and play";
    public bool ShowRecordAction => !RecordOn && CanToggleRecord && NoAntiCheat;   // an anti-cheat game records only through its session card

    // Shader coverage (PlanStats), in words: how much of what the planner found gets compiled, where it comes from, what's
    // left and what helps; every count in Details. No plan yet on a game that can compile: the card says when it's measured.
    PlanStats? P => s.Plan;
    // a compile that didn't help: the ring is greyed out, nothing of it counts for the game
    int? Pct => DriverChanged ? null : ScsKiller.ShownPercent(s);
    // the last compile filled the old driver's cache, which the new driver doesn't use
    bool DriverChanged => s.Status == GameStatus.Stale && s.StatusReason.StartsWith("driver changed:", StringComparison.Ordinal);
    public bool HasCoverageCard => ScsKiller.ShowsCoverage(s) || CanCompile;
    public bool HasPlan => P != null;
    public bool HasMeter => Pct != null;
    public bool LowCoverage => !s.CompileUnreached && (Pct is { } pct ? pct < 90 : Partial);   // more than 10% left out, like ScsKiller.IsPartial (the test for a plan without the count)
    public bool GoodCoverage => HasMeter && !LowCoverage;
    bool Dx11Only => P is { StageSets: 0, Generated: 0, D3D11Shaders: > 0 };
    public string CoverageHeadline => P == null ? "Known once its first compile starts"
        : s.CompileUnreached ? "Its compile isn't used by this game"
        : DriverChanged ? $"Its last compile was for driver {s.WarmedDriverVersion}"
        : Dx11Only && Pct is null or 100 ? (Compiled ? "Covers" : "Will cover") + " every DirectX 11 shader in the game files" + (Core.Planning.Planner.D3D11CoversVs(Host.Core.Vendor.Caps) || P.D3D11Draws > 0 ? "" : " but the vertex shaders")
        : Pct is not { } pct ? "Measured again on the next compile"
        : (Compiled || s.NewOnly ? "Covers" : s.WarmedAt != null ? "Its last compile covered" : "Will cover")
          + ScsKiller.CoverageWhat(s, pct);
    // the headline's own words under the ring while no recording has confirmed the engine rule
    public bool HasRuleNote => Pct == 100 && !Dx11Only && ScsKiller.RuleUnconfirmed(s);
    bool Rt => ScsKiller.NeedsRtRecording(s);
    bool RtUnseen => s.RtUnseen;
    bool RtInline => ScsKiller.RtInlineCovers(P);
    bool Compiled => s.WarmedAt != null && s.Status != GameStatus.Stale;
    VendorCaps Caps => Host.Core is ScsKiller k ? k.CapsOf(s.Game.Id) : Host.Core.Vendor.Caps;

    /// <summary>Where the plan's pipelines come from, in a player's words; the recording's row carries the community note,
    /// and without one the community database's status is its own row. Ray tracing: covered or what it needs.</summary>
    public IReadOnlyList<DetailRow> Sources => P is not { } p ? [] : new DetailRow?[]
    {
        p.Generated > 0 ? new(ScsKiller.BothApis(p) ? "DirectX 12, from the game files" : "Found in the game files", Fmt.N(p.Generated),
            Pct == 100 && s.ShaderCount is { } n && p.Generated > n ? "Each shader is used in many combinations, so there are more of these than shaders in the game." : null) : null,
        !Vk && FromPlay > 0 ? new(RecordingSource, Fmt.N(FromPlay), CommunityLine) : null,
        p.FromDownload > 0 ? new("From Steam's shader download", Fmt.N(p.FromDownload)) : null,
        Vk && p.FromCommunity > 0 ? new("From the community database", Fmt.N(p.FromCommunity)) : null,
        Vk && FromPlay > 0 ? new(FromPlayLabel, Fmt.N(FromPlay), CommunityLine) : null,
        p.CacheBudgetLeftOut is > 0 and var left ? new(ScsKiller.CacheBudgetTitle(Caps, s.Engine, s.PlanCacheCapGB), Fmt.N(left), ScsKiller.CacheBudgetNote(Caps, s.Engine, s.PlanCacheCapGB)) : null,
        s.Official is { } o ? new("Official recording", $"{o.Psos:N0} pipelines", $"Published by SCSKiller for this version, downloaded {o.DownloadedAt.LocalDateTime:d}") : null,
        s.Community == null && s.InCommunityDb is { } inDb && !HasDbTeaser ? new("The community database", inDb ? "has a recording for this version" : "not in it yet") : null,
        p.MiddlewareItems > 0 ? new(p.MiddlewareSharedItems == 0 ? "Its upscalers, learned from recordings"
            : p.MiddlewareSharedItems == p.MiddlewareItems ? "Its upscalers, from shared packs" : "Its upscalers, from recordings and shared packs", Fmt.N(p.MiddlewareItems)) : null,
        p.D3D11Shaders > 0 ? new(ScsKiller.BothApis(p) ? "DirectX 11, from the game files" : "DirectX 11 shaders", Fmt.N(p.D3D11Shaders)) : null,
        p.RtLibraries > 0 && s.RtSkipped ? new("Ray tracing", "skipped (your setting)")
        : p.RtLibraries > 0 ? new("Ray-traced effects", s.Engine?.NoRayTracing == true ? "off in this game" : s.Engine?.NoRtPipelines == true ? "inline, from the game files" : p.RtUncovered == 0 ? "covered" : s.RtToPlan ? "being checked" : RtUnseen ? "not seen while recording" : RtInline ? "inline ones covered" : !Rt ? "mostly covered" : CanRecord ? "need a recording" : "not compiled") : null,
    }.OfType<DetailRow>().ToList();
    public bool HasSources => Sources.Count > 0;

    // a Vulkan plan reads no game files: its pipelines are Steam's download and what play on this PC recorded, also a
    // DirectX game's under Proton made of Steam's alone. A DirectX plan with Steam's counts its download with its recorded
    bool Vk => s.Engine is { } e && Core.Vulkan.VulkanPlan.IsVulkan(e) || P is { FromDownload: not null, Generated: 0, SynthesizedTemplates: 0 };
    long FromPlay => P is { } p ? p.Recorded - (p.FromDownload ?? 0) - (p.FromCommunity ?? 0) : 0;
    string FromPlayLabel => P?.FromDownload == null ? "Recorded pipelines" : "From your play";   // a plan from before the split was counted

    /// <summary>The recorded row's label: whose recordings the plan's recorded pipelines come from.</summary>
    string RecordingSource
    {
        get
        {
            if (UsedCommunity == null && s.Official == null) return "Your play recordings";
            string[] from = [.. (UsedCommunity ?? s.Official)!.WithLocalRecording ? ["your recording"] : Array.Empty<string>(),
                .. UsedCommunity != null ? ["the community database"] : Array.Empty<string>(), .. s.Official != null ? ["the official recording"] : Array.Empty<string>()];
            var text = from.Length == 1 ? from[0] : string.Join(", ", from[..^1]) + " and " + from[^1];
            return char.ToUpperInvariant(text[0]) + text[1..];
        }
    }
    /// <summary>The community recording the plan uses: not one that doesn't match the game's files.</summary>
    CommunityInfo? UsedCommunity => s.Community is { OtherVersion: false } c ? c : null;
    /// <summary>The community database's line under the recording row; null = nothing to say.</summary>
    string? CommunityLine => UsedCommunity is { } c
        ? $"Community recording downloaded {c.DownloadedAt.LocalDateTime:d}" + (s.RecordingSharedAt is { } at ? $" · yours shared {at.LocalDateTime:d}" : "")
        : s.RecordingSharedAt is { } at2 ? $"Shared with the community database {at2.LocalDateTime:d}" : null;

    // What's left and what helps. LeftOut is mostly Uncovered (root signature slots SCSKiller can't build) or, with a learned
    // lookup, stage sets no recording showed yet (no root signature / template): a longer recording is what helps there.
    // Ray tracing the plan can't compile comes first, in the status's words (ScsKiller.RtNote).
    enum Left { AntiCheat, EngineSlots, UnknownSlots, NotSeen, Recording, PlayedClean, PlayedCompiles, NothingKnown, PlayOnly }
    long Missing => P is { } m ? Math.Max(m.LeftOut, m.Uncovered) : 0;   // a plan without LeftOut: Uncovered only
    Left LeftCase => !CanRecord ? Left.AntiCheat
        : Missing > 0 ? P!.Uncovered * 2 >= Missing ? P.Recorded == 0 ? Left.EngineSlots : Left.UnknownSlots : Left.NotSeen
        : RecordOn ? Left.Recording
        : L is { } l ? PlayCompiles(l) == 0 ? Left.PlayedClean : Left.PlayedCompiles   // what was measured beats what's likely
        : P is { Recorded: > 0 } ? Left.NothingKnown
        : Left.PlayOnly;
    public string LeftText => LeftSentences(tipAsks: false);
    /// <summary>tipAsks: the record tip already gives the ray tracing note and the recording advice.</summary>
    string LeftSentences(bool tipAsks) => string.Join(" ", new string?[]
    {
        RtUnseen ? Sentence(ScsKiller.RtUnseenNote) + "." : RtInline ? Sentence(ScsKiller.RtInlineNote) + "." : !Rt || tipAsks ? null : CanRecord ? (HasDbTeaser ? ScsKiller.RtNeedsRecording : ScsKiller.RtNote(s.InCommunityDb, Host.Account?.HasDb == true)) + "."
            : $"Ray-traced effects aren't compiled: they need a recording, {(s.RiskEligible ? ScsKiller.RiskNoRecording : !NoAntiCheat ? $"which {ScsKiller.AntiCheatName(s)} blocks" : s.RootUnconfirmed ? ScsKiller.ManualNoRecording : s.PreloadHeld is { } held ? ScsKiller.PreloadHeldNoRecording(held, s.PreloadHeldByOptions) : ScsKiller.StreamlineNoRecording)}.",
        LeftCase switch
        {
            Left.AntiCheat => s.RiskEligible ? "Anything else compiles while you play: a session on this page can record it, at your own risk."
                : !NoAntiCheat ? $"Anything else compiles while you play: {ScsKiller.AntiCheatName(s)} blocks the recording that would find it."
                : s.RootUnconfirmed ? "Anything else compiles while you play: confirm the game's folder (Game folder… above) to record what's missing."
                : s.PreloadHeld != null ? "Anything else compiles while you play."
                : "Anything else compiles while you play: DLSS on NVIDIA blocks the recording that would find it.",
            Left.EngineSlots => ScsKiller.EngineSlotsNote(RecordOn),
            Left.UnknownSlots => "The rest use shader slots SCSKiller can't rebuild yet, so they still compile while you play.",
            Left.NotSeen => ScsKiller.NotSeenNote(RecordOn, P),
            Left.Recording => "Recording is on: anything new you play is added the next time it compiles.",
            Left.PlayedCompiles => $"Last time you played, {PlayCompiles(L!):N0} still had to compile." + (tipAsks ? "" : " Recording picks them up."),
            _ when Rt || RtUnseen || RtInline => null,   // ray tracing is what's left
            Left.PlayedClean => "Nothing was missing last time you played.",
            Left.NothingKnown => "Nothing SCSKiller knows of is missing.",
            _ => "A few effects are only put together while you play." + (tipAsks ? "" : " A short recording picks them up."),
        },
        !Guessed ? null : tipAsks ? "Some shader states are guesses." : "Some shader states are guesses. A longer recording makes them exact.",
    }.OfType<string>());
    // per-stage plans (AMD): the planner warns that a wrong guess costs a compile in game
    bool Guessed => P is { } g && g.GuessedUnits > 0.1 * (g.ExactUnits + g.InferredUnits + g.GuessedUnits);
    // not twice: a game that needs a recording has the button in its tip
    public bool ShowLeftRecord => ShowRecordAction && !HasRecordTip && s.NoStutter == null && (LeftCase is Left.EngineSlots or Left.NotSeen or Left.PlayedCompiles or Left.PlayOnly || Guessed || Rt || RtUnseen || RtInline);
    // before the first compile there's nothing left yet. Recording on: what's missing comes from playing, without the button
    public bool ShowLeftCallout => HasPlan && (ShowLeftRecord || RecordOn && s.NoStutter == null && LeftCase is Left.NotSeen or Left.EngineSlots);
    public string LeftTitle => Rt || RtUnseen ? "Ray-traced effects aren't compiled yet" : "Some shaders still compile while you play";

    public string CoverageValue => Pct is { } pct ? $"{pct}%" : Dx11Only && !s.CompileUnreached ? "All" : Format.Dash;
    public string CoverageLabel => s.CompileUnreached ? "not used" : Pct != null || Dx11Only ? "covered" : "not yet";
    /// <summary>The ring's arc as path data, in a 120 px box with a 12 px stroke; null when it isn't drawn in that colour.</summary>
    public string? GoodArc => GoodCoverage ? Arc() : null;
    public string? LowArc => LowCoverage ? Arc() : null;
    string? Arc()
    {
        const double c = 60, r = 54;
        double a = Math.Min(Pct ?? 0, 99.99) / 100 * 2 * Math.PI;
        if (a == 0) return null;   // a zero-length arc's round caps would draw a dot
        return string.Create(CultureInfo.InvariantCulture, $"M {c},{c - r} A {r},{r} 0 {(a > Math.PI ? 1 : 0)} 1 {c + r * Math.Sin(a):0.###},{c - r * Math.Cos(a):0.###}");
    }
    public bool HasTimeValue => Fmt.CompileTime(s) != null;
    public string TimeLabel => s.LastWarmTime != null ? "Compile time" : "Compile time (estimate)";
    public string TimeValue => s.LastWarmTime is { TotalMinutes: >= 1, TotalHours: < 1 } t ? $"{t.Minutes} min {t.Seconds} s"   // measured: to the second
        : Format.Duration(Fmt.CompileTime(s));
    public bool HasDiskValue => s.DiskBytes != null || s.EstimatedCacheBytes != null;
    public string DiskLabel => s.DiskBytes != null ? "Disk space" : "Disk space (estimate)";
    public string DiskValue => Format.Bytes(OnDisk ?? s.EstimatedCacheBytes);
    public bool HasDiskInUse => s.CacheOnDisk != null && Format.InUseShown(OnDisk, InUse);
    public string DiskInUse => $"{Format.Bytes(InUse)} in use";
    // Windows' dbs counted whole: what they hold isn't read
    long? InUse => s.CacheInUse + (s.WindowsCache ?? 0);
    // the kept-aside copy of what the driver removed takes disk space too
    long? OnDisk => s.DiskBytes + (s.Kept?.Frees ?? 0);
    public bool HasShaderCount => s.ShaderCount != null;
    public string ShaderCount => Fmt.N(ScsKiller.PageShaders(s));
    public string? ShaderCountTip => ScsKiller.AllShadersTip(s);
    // measured frames when the recorder timed them, else the compile count
    long? StutterCount => F is { } f ? Format.NoPlay(f.Low1PctFps) ? null : ShaderHitches.Count : L is { } l ? PlayCompiles(l) : null;
    public bool HasStutterTile => HasSession && StutterCount != null;
    public string StuttersValue => StutterCount is not { } n ? "" : n == 0 ? "None" : Fmt.N(n);
    public bool HasStutters => StutterCount > 0;
    public bool NoStutters => StutterCount == 0;
    // without frame times the count is of compiles, which may or may not have stuttered
    public string StuttersLabel => F != null ? "Stutters last time you played" : "Compiles while you played";

    /// <summary>Every count, for power users.</summary>
    public IReadOnlyList<DetailRow> Details => new DetailRow?[]
    {
        P is { } p ? new("Pipelines in the plan", Fmt.N(p.Recorded + p.Generated + p.MiddlewareItems + p.Variants)) : null,
        s.ShaderCount != null ? new("Shaders in the game files", ShaderCount) : null,
        P is { StageSets: > 0 } ? new("Shader combinations found", Fmt.N(P.StageSets)) : null,
        P is { LeftOut: > 0 } ? new("Left out", ScsKiller.LeftOutValue(P)) : null,
        P is { StageSets: 0, Uncovered: > 0 } ? new("Left out for shader slots SCSKiller can't rebuild", Fmt.N(P.Uncovered)) : null,
        P != null && !Vk ? new("Recorded pipelines", Fmt.N(FromPlay)) : null,
        P is { FromDownload: { } dl } && (Vk || dl > 0) ? new("From Steam's shader download", Fmt.N(dl)) : null,
        Vk && P != null ? new(FromPlayLabel, Fmt.N(FromPlay)) : null,
        P != null && !Vk ? new("Built from the game files", Fmt.N(P.Generated)) : null,
        P is { MiddlewareItems: > 0 } ? new("Upscaler pipelines", Fmt.N(P.MiddlewareItems)) : null,
        P is { Variants: > 0 } ? new("At other texture filtering settings", Fmt.N(P.Variants)) : null,
        P is { D3D11Shaders: > 0 } ? new("DirectX 11 shaders", Fmt.N(P.D3D11Shaders)) : null,
        P is { RtLibraries: > 0 } ? new("Ray tracing shader libraries", $"{P.RtLibraries:N0}" + (s.Engine?.NoRtPipelines == true ? " (not used by the game)" : s.RtSkipped ? " (skipped, your setting)" : P.RtUncovered > 0 ? $" ({P.RtUncovered:N0} not compiled)" : "")) : null,
        P is { RtInline: > 0 } ? new("Shaders that trace rays inline", Fmt.N(P.RtInline)) : null,
        s.Status != GameStatus.Unsupported && s.InCommunityDb is { } db && !HasDbTeaser ? new("Community data for your version", db ? $"{s.CommunityDbPsos:N0} pipelines" : "none yet") : null,
        s.Community is { } c ? new("Downloaded community recording", c.OtherVersion ? "doesn't match your game's files" : $"{c.Psos:N0} pipelines") : null,
        s.Official is { } o ? new("Official recording", $"{o.Psos:N0} pipelines") : null,
        P != null && !Vk ? new("Synthesized pipeline templates", Fmt.N(P.SynthesizedTemplates)) : null,
        P != null && !Vk ? new("Shader layouts", $"{P.RootSignatures:N0} · {RootSigSource(P)}") : null,
        P is { } u && u.ExactUnits + u.InferredUnits + u.GuessedUnits > 0 ? new("Stage units exact / inferred / guessed", $"{u.ExactUnits:N0} / {u.InferredUnits:N0} / {u.GuessedUnits:N0}") : null,
        P is { LayoutCoverage: > 0 } ? new("Vertex layouts from a recording", $"{P.LayoutCoverage:P1}") : null,
        s.CacheOnDisk is { } disk ? new("Driver cache", Format.Bytes(disk) + " measured") : s.EstimatedCacheBytes is { } est ? new("Driver cache", "≈ " + Format.Bytes(est) + " estimated") : null,
        s.WindowsCache is { } w ? new(ScsKiller.WindowsPart, Format.Bytes(w)) : null,
    }.OfType<DetailRow>().ToList();

    // "Shipped with the shaders" is the carver's case.
    string RootSigSource(PlanStats p) =>
        p.RootSigRuleVerified ? "tested on this engine version"
        : ScsKiller.ApiKey(s.Engine?.GraphicsApi) == "11" ? "not needed (DirectX 11)"
        : p is { RootSignatures: 0, Recorded: > 0 } ? "in the recorded pipelines"
        : s.Engine?.Family == "Unreal" ? (p.Recorded > 0 ? "learned from recording" : "not tested on this engine version yet")
        : p.Recorded > 0 ? "learned from recording" : "shipped with the shaders";

    // Last compile: when, how long, what failed or was skipped and what to do; never compiled: what the first one costs.
    public bool HasCompileCard => CanCompile || s.LastWarmTime != null;
    public string CompileTitle => s.LastWarmTime == null ? "First compile" : "Last compile";
    long Failed => s.LastWarmFailed ?? 0;
    long Skipped => s.LastWarmSkipped ?? 0;
    long Crashed => s.LastWarmCrashed ?? 0;
    long DriverSkipped => s.LastWarmDriverSkipped ?? 0;
    long Known => Math.Min(DriverSkipped, s.LastWarmKnown ?? 0);   // of DriverSkipped
    long CrashedBefore => Math.Min(Crashed, s.LastWarmCrashedBefore ?? 0);   // of Crashed
    long Unbuilt => s.LastWarmUnbuilt ?? 0;
    long Invalid => s.LastWarmInvalid ?? 0;
    long Unsupported => s.LastWarmUnsupported ?? 0;
    public string CompileText => s.WarmedAt is { } at
        ? $"Compiled {Fmt.When(at)}" + (s.LastWarmTime is { } t ? $" in {Format.Duration(t)}" : "") + $", for driver {s.WarmedDriverVersion}."
          + (ScsKiller.NothingFailed(s) ? " Nothing failed." : "")   // null = not known (a warm from before these were kept): no claim
          + (ScsKiller.RefusedLine(s) is { } refused ? " " + refused : "")
          + (OnDisk is { } c ? $" It uses {Format.Bytes(c)} of disk space." : "")
          + " " + OwnShaderStep
        : s.LastWarmTime is { } last ? $"Its shader cache was cleared. The last compile took {Format.Duration(last)}."
        : (s.EstimatedWarmTime is { } est ? $"It takes about {Format.Duration(est)} and runs in the background, so you can keep using the PC." : "It runs in the background, so you can keep using the PC.")
          + (s.EstimatedCacheBytes is { } b ? $" It adds an estimated {Format.Bytes(b)} to the driver's shader cache." : "");
    // no game file says whether a game runs its own precompile at startup: said on every compiled game's page, not its row
    public const string OwnShaderStep = "The game may still run its own shader step when it starts.";
    public string SkippedTitle => $"{Skipped + Crashed + DriverSkipped + Unbuilt + Invalid + Unsupported:N0} skipped";
    long NeedsRecording => Math.Min(Skipped, s.LastWarmNeedsRecording ?? 0);   // of Skipped: flagged by the community recording
    long NotInGame => Skipped - NeedsRecording;
    public string? SkippedText => s.WarmedAt == null || Skipped + Crashed + DriverSkipped + Unbuilt + Invalid + Unsupported == 0 ? null
        : string.Join(" ", new[]
        {
            NotInGame > 0 && P is { FromDownload: > 0, Generated: 0 } ? $"{NotInGame:N0} skipped: {ScsKiller.DownloadSkipped}. Playing with the recorder on adds them."
            : NotInGame > 0 ? $"{NotInGame:N0} {(NotInGame == 1 ? "was" : "were")} skipped: {(NotInGame == 1 ? "its shader isn't" : "their shaders aren't")} in this version of the game. Nothing to do." : null,
            Crashed - CrashedBefore is > 0 and var crashedNow ? $"{crashedNow:N0} {(crashedNow == 1 ? "was" : "were")} skipped: {(crashedNow == 1 ? "it crashes" : "they crash")} the GPU driver. If the game uses {(crashedNow == 1 ? "it" : "them")}, it compiles {(crashedNow == 1 ? "it" : "them")} itself." : null,
            DriverSkipped - Known is > 0 and var now ? $"{now:N0} {(now == 1 ? "was" : "were")} skipped: the GPU driver crashed or hung on {(now == 1 ? "it or on what it builds" : "them or on what they build")} on, or {(now == 1 ? "it wasn't" : "they weren't")} tried past a spot where it kept failing." : null,
            Known + CrashedBefore > 0 ? ScsKiller.KnownNote(Known + CrashedBefore) + "." : null,
            Unbuilt > 0 ? $"{Unbuilt:N0} {(Unbuilt == 1 ? "was" : "were")} skipped: SCSKiller can't build {(Unbuilt == 1 ? "it" : "them")} yet." : null,
            Invalid > 0 ? $"{Invalid:N0} skipped, {(Invalid == 1 ? "its shaders fail" : "their shaders fail")} Vulkan validation (Steam skips {(Invalid == 1 ? "it" : "them")} too)." : null,
            Unsupported > 0 ? $"{Unsupported:N0} skipped, this GPU doesn't support what {(Unsupported == 1 ? "it needs" : "they need")}." : null,
            NeedsRecording > 0 ? $"{NeedsRecording:N0} need{(NeedsRecording == 1 ? "s" : "")} a recording on this PC: the game or a mod builds {(NeedsRecording == 1 ? "its shaders" : "their shaders")} while it runs, so they're in no game file. Play with \"Record while I play\" on to catch them." : null,
        }.OfType<string>());
    public bool HasSkipped => SkippedText != null;

    // Last play session (SessionStats, no timestamp): one bar, three segments, under the Record switch. Ready = from the
    // game's library or the driver cache; ray tracing state objects have their own line, so Requests isn't used.
    SessionStats? L => s.LastSession;
    public bool HasSession => L != null;
    public bool ShowSessionHint => L == null && CanRecord && SessionHint != null && !FramesOnly;
    public string? SessionHint => Format.SessionHint(s);
    public string SessionHeader => L is { } l ? $"Last time you played · {Format.Duration(F?.Duration ?? l.Duration)}" : "";
    // ray tracing state objects included, so the headline agrees with the ray tracing line under it
    static long PlayCompiles(SessionStats l) => l.Compiles + l.StateObjectsCompiled;
    public string SessionVerdict
    {
        get
        {
            if (L is not { } l) return "";
            long play = PlayCompiles(l), started = l.StartupCompiles + l.StateObjectsStartupCompiled, ready = l.FromGameLibrary + l.CacheHits + l.StateObjectsReady;
            if (play + started == 0)
                return ready > 0 ? $"Nothing had to compile while you played: all {ready:N0} shader combinations the game used were ready." : "Nothing had to compile while you played.";
            // the longest compile call, of the PSOs only; whether a frame stuttered is the frame report's to say
            string worst = l.StateObjectsCompiled > 0 ? "" : play == 1 ? $" (it took {l.WorstCompileMs:0} ms)" : $" (the longest took {l.WorstCompileMs:0} ms)";
            return (play == 0 ? "Nothing had to compile while you played." : $"{play:N0} had to compile while you played{worst}.")
                + (started > 0 ? $" {started:N0} compiled while the game started or loaded." : "")
                + (ready > 0 ? $" The other {ready:N0} were ready." : "");
        }
    }
    public bool HasStateObjectLine => L is { } l && l.StateObjectsReady + l.StateObjectsCompiled + l.StateObjectsStartupCompiled > 0;
    public string StateObjectLine => L is { } l ? $"Ray tracing pipelines: {l.StateObjectsReady:N0} ready, {l.StateObjectsCompiled:N0} compiled while you played"
        + (l.StateObjectsStartupCompiled > 0 ? $", {l.StateObjectsStartupCompiled:N0} while the game started or loaded." : ".") : "";
    public bool HasRayQueryLine => L?.RayQueryRecompiles > 0;
    public string RayQueryLine => L is { RayQueryRecompiles: > 0 and var n }
        ? $"{n:N0} ray-traced pipeline{(n == 1 ? "" : "s")} the driver partly recompiles every launch." : "";

    // The last launch's frame times (FrameLog): its shader stutters, other hitches and loading, the 1% low of play.
    public FrameReport? F => s.LastFrames;
    List<Hitch> ShaderHitches => F?.Hitches.Where(h => h.Cause == HitchCause.Shader).ToList() ?? [];
    public bool HasFrames => F != null;
    public string FramesVerdict => F is not { } f ? "" : Format.Frames(f, ShaderHitches, Unsplit);
    // a DirectX 11 session (the frames-only recorder's too) has no creates: its hitches aren't split by cause
    bool Unsplit => FramesOnly || F is { D3D11: true };
    public bool SplitFrames => !Unsplit;
    public string OtherLegend => Unsplit ? "stutter" : "other hitch";
    public string GraphEnd => Format.Duration(F?.Duration);
    public string FramesSummary => F is not { } f ? ""
        : $"Startup (the game's own shader precompile and first load): {Format.Duration(f.Startup)}. In play: {InPlay(f).Count(h => h.Ms >= 50):N0} frames of 50 ms or more, "
          + $"{InPlay(f).Count(h => h.Ms >= 100):N0} of 100 ms or more. Frame times stay on this PC.";
    // the same list while the report is the same: a new one makes the slow-frames list rebuild every row at each refresh
    public IReadOnlyList<DetailRow> HitchRows => hitchRows.Report == F ? hitchRows.Rows
        : (hitchRows = (F, F?.Hitches.Select(h => new DetailRow($"{Clock(h.At)}  {(Unsplit && h.Cause == HitchCause.Other ? "stutter" : Cause(h.Cause))}", Ms(h.Ms))).ToList() ?? [])).Rows;
    (FrameReport? Report, IReadOnlyList<DetailRow> Rows) hitchRows = (null, []);
    public static string HitchTip(Hitch h, bool neutral = false) => $"{Clock(h.At)} · {Ms(h.Ms)} · {(neutral ? "hitch" : Cause(h.Cause))}";
    static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    public bool HasHitchRows => F?.Hitches.Count > 0;

    // Earlier launches with frame times (FrameSessions), newest first; the one the section shows is left out. Read again
    // only when the game's state changes.
    public IReadOnlyList<DetailRow> EarlierSessions => earlier.For == s ? earlier.Rows
        : (earlier = (s, Host.Core.FrameHistory(id).Where(e => e.LaunchUnixMs != F?.LaunchUnixMs).Select(e => SessionRow(e, FramesOnly)).ToList())).Rows;   // each row's own D3D11 decides the rest
    (GameState? For, IReadOnlyList<DetailRow> Rows) earlier = (null, []);
    public bool HasEarlierSessions => EarlierSessions.Count > 0;
    // a cleared recording takes the last launch's frames, not the history
    public bool HasFramesCard => HasFrames || HasEarlierSessions;
    static DetailRow SessionRow(FrameSession e, bool unsplit) => new(
        $"{Fmt.Time(DateTimeOffset.FromUnixTimeMilliseconds(e.LaunchUnixMs).LocalDateTime, "g")} · {Format.Duration(e.Duration)}",
        Format.Session(e, unsplit));

    // the frames of play, between startup and quitting, whatever slowed them (a load in play too)
    static IEnumerable<Hitch> InPlay(FrameReport f) => f.Hitches.Where(h => FrameLog.InPlay(h.At.TotalMilliseconds, h.Ms, f.Startup.TotalMilliseconds) && h.Cause != HitchCause.Quitting);
    static string Ms(double ms) => Format.Ms(ms);
    static string Cause(HitchCause c) => c switch { HitchCause.Shader => "shader compile", HitchCause.Other => "other hitch", HitchCause.Quitting => "quitting",
        HitchCause.LoadingShaders => "loading, compiling shaders", _ => "loading" };

    /// <summary>The session bar's three segments, as star widths.</summary>
    public double LibraryWidth => L?.FromGameLibrary ?? 0;
    public double HitsWidth => L?.CacheHits ?? 0;
    public double CompilesWidth => L?.Compiles ?? 0;
    public double CompilesMinWidth => L?.Compiles > 0 ? 4 : 0;
    public string FromLibrary => Fmt.N(L?.FromGameLibrary);
    public string CacheHits => Fmt.N(L?.CacheHits);
    public string Compiles => Fmt.N(L?.Compiles);
    public string SessionLabel => L is { } l
        ? $"{l.Requests:N0} requests: {l.FromGameLibrary:N0} from the game's own library, {l.CacheHits:N0} already in the driver cache, {l.Compiles:N0} compiled during play"
          + (l.StartupCompiles > 0 ? $", {l.StartupCompiles:N0} while the game started or loaded" : "")
          + (l.RayQueryRecompiles > 0 ? $", {l.RayQueryRecompiles:N0} ray-traced pipelines the driver partly recompiles every launch" : "")
          + (l.StateObjectsReady + l.StateObjectsCompiled + l.StateObjectsStartupCompiled > 0 ? $". Ray tracing pipelines: {l.StateObjectsReady:N0} ready, {l.StateObjectsCompiled:N0} compiled during play" : "")
          + (l.StateObjectsStartupCompiled > 0 ? $", {l.StateObjectsStartupCompiled:N0} while the game started or loaded" : "")
        : "";

    public bool? RecordPending { get; set; }   // the state a recorder change that is running (off the UI thread) asked for
    // NVIDIA, DirectX 11: the recorder times frames and records nothing
    public bool FramesOnly => ScsKiller.Frames11Play(s.Engine, Host.Core.Vendor.Caps);
    // its switch waits for the pair to end
    bool PairFramesOnly => ScsKiller.Frames11(s.Engine, Host.Core.Vendor.Caps) && s.BeforeAfter is { Stage: not BeforeAfterStage.Done };
    public bool RecordOn => ShowOffline ? OfflineOn : !PairFramesOnly && (RecordPending ?? s.RecorderEffective);
    public bool CanToggleRecord => ShowOffline ? OfflinePending == null && !s.OfflineRunning
        : RecordPending == null && AlongsidePending == null && NoAntiCheat && s.RecorderSkip == null && !PairFramesOnly;
    public bool ShowUseDefault => RecordPending == null && s.RecorderOverride != RecorderOverride.Default && s.RecorderSkip == null && !PairFramesOnly && !s.RecorderForeign;
    public string UseDefaultText => $"Use default ({(ScsKiller.RecordsByDefault(Host.Core.Settings, s) ? "on" : "off")})";
    string RecorderDll => Fmt.RecorderDll(s);
    string Kept => ScsKiller.SessionKeptNote(s) is { } kept ? " " + kept : "";
    public string RecordNote => ScsKiller.CrashNote(s.Game) is { } crash ? crash
        : Risk
        ? (s.AlwaysRecord ? "Every launch is recorded." : "Each session is confirmed before it starts.") + Kept
        : ShowOffline
        ? $"{ScsKiller.AntiCheatName(s)} blocks the recorder, but you can enable it at your own risk with an offline session." + Kept
        : s.RiskNoLaunch ? ScsKiller.NoLaunchNote(Fmt.StoreName(s.Game))
        : !NoAntiCheat
        ? Host.Platform.RecordAntiCheatGames ? ScsKiller.RiskNotHere(s) ?? $"Not available: {ScsKiller.AntiCheatName(s)} treats an extra {RecorderDll} as tampering, so this game is compiled from its files only." : NoAntiCheatSessions
        : PairFramesOnly ? "Frame times only, for the before and after recording."
        : s.RecorderSkip == ScsKiller.SkipManual ? "Not available until you confirm the game's folder (Game folder… above): SCSKiller checks all of it for anti-cheat before it records."
        : s.RecorderSkip == ScsKiller.SkipModNotChainable ? $"Not available: {ScsKiller.NotChainableReason(s.RecorderMod, RecorderDll)}."
        : s.RecorderSkip == ScsKiller.SkipAgsMod && s.AgsMod is { } agsMod && !(agsMod == "ReShade" && s.ShaderModLayer && s.ShaderModAsD3D12) ? $"Not available: {ScsKiller.SkipAgsMod}. {ScsKiller.AgsModHint(agsMod)}"   // else ShaderModNote says it
        : s.RecorderSkip is { } skip ? $"Not available: {skip}."
        : (s.Engine is { } vk && Core.Vulkan.VulkanPlan.IsVulkan(vk) ? ScsKiller.VulkanRecorderNote
            : FramesOnly ? $"Adds a small {Fmt.RecorderFiles(s)} next to the game that times each frame for the graph above and records nothing else."
            : $"Adds a small {Fmt.RecorderFiles(s)} next to the game to catch anything the plan missed and time each frame, so this page shows what stuttered.")
          + " Remove any time. If a game has anti-cheat SCSKiller didn't detect, don't turn this on."
          + (s.RecorderNote is { } note && note != s.RecorderFailure ? $" ({Sentence(note)})" : "")
          + (s.RecorderRefused is { } why && !(s.Status == GameStatus.NeedsRecording && ScsKiller.NeverRecorded(s)) ? " " + why : "");   // else the status says it

    // A mod's d3d12.dll where the recorder goes (ReShade, a wrapper): off = the game isn't recorded; on = the recorder chains to it
    public bool HasMod => s.RecorderMod != null && s.RecorderSkip is not (ScsKiller.SkipAntiCheat or ScsKiller.SkipShaderMod or ScsKiller.SkipManual or ScsKiller.SkipUnsupported or ScsKiller.SkipNotDx12 or ScsKiller.SkipStreamline or ScsKiller.SkipCrashes);
    public string AlongsideTitle => $"Record alongside {s.RecorderMod}";
    public string AlongsideNote => $"{s.RecorderMod}'s d3d12.dll is renamed while the recorder is in, and restored exactly as it was when it's removed.";
    public bool? AlongsidePending { get; set; }
    public bool AlongsideOn => AlongsidePending ?? s.RecordAlongsideMod;
    public bool CanToggleAlongside => AlongsidePending == null && RecordPending == null;

    // An eligible EasyAntiCheat game (Games.OfflineEac) or an anti-cheat game allowed in Settings (RiskEligible): the card's
    // switch allows sessions, each confirmed before it starts
    public bool ShowOffline => s.OfflineEligible || s.RiskEligible;
    public bool Risk => s.RiskEligible;
    public bool? OfflinePending { get; set; }
    public bool OfflineOn => OfflinePending ?? s.OfflineRecord;
    public bool ShowOfflineOn => ShowOffline && OfflineOn;
    public bool OfflineStarting { get; set; }
    public bool CanStartOffline => s.OfflineRecord && OfflinePending == null && !s.OfflineRunning && !s.Playing && !OfflineStarting;
    // a game a session is offered for, with its switch on: every launch records without the per-launch confirmation
    public bool ShowAlways => Risk && OfflineOn;
    public bool? AlwaysPending { get; set; }
    public bool AlwaysOn => AlwaysPending ?? s.AlwaysRecord;
    public bool CanToggleAlways => AlwaysPending == null && OfflinePending == null && !OfflineStarting;
    public bool ShowSessionButton => !(Risk && AlwaysOn) || s.OfflineRunning;
    public string OfflineButtonText => Risk
        ? s.OfflineRunning ? "Session running…" : "Record a session (at your own risk)"
        : s.OfflineRunning ? "Offline session running…" : "Record offline without EasyAntiCheat (at your own risk)";
    public string OfflineRiskText => Risk ? RiskText(s) : OfflineRisk;
    public const string OfflineRisk = "Starts the game offline without EasyAntiCheat. SCSKiller removes its files when the game exits (after a crash: "
        + "at the next logon). If any are left when you play online, you could be banned. Steam must be running. Don't uninstall SCSKiller mid-session.";
    public static string RiskText(GameState g) => $"This game contains {ScsKiller.AntiCheatName(g)}. If you enable the recorder, it's at your own risk. "
        + "SCSKiller isn't responsible if your account gets banned.";
    public string? SessionNote => s.SessionNote;
    public bool HasSessionNote => s.SessionNote != null;
    public string? RecorderFailure => s.RecorderFailure is { } f ? $"Recorder {f}." : null;
    public bool HasRecorderFailure => s.RecorderFailure != null;

    public bool HasRecording => s.RecordingBytes > 0;
    public string RecordingSize => $"The recording uses {Format.Bytes(s.RecordingBytes)} (in the game folder and SCSKiller's copy)";
    public bool RecordingPaused => s.RecordingPaused;
    public string RecordingPausedText => ScsKiller.PausedNote(Host.Core.Settings) + ". Raise the limit in Settings, or clear the recording.";
    public bool ClearingRecording { get; set; }
    public bool CanClearRecording => !ClearingRecording && !s.Playing;
    public string ClearRecordingTip => s.Playing ? "Close the game first" : "Deletes the recorded pipelines. The recorder stays in";

    public bool Playing => s.Playing;
    public bool Refreshing { get; private set; }
    public bool CanRefresh => !Refreshing;
    public async void RefreshGame()
    {
        if (Refreshing) return;
        Refreshing = true;
        Changed();
        try { await Task.Run(() => Host.Core.RefreshGame(id)); Error = null; }
        catch (Exception e) { Error = e.Message; }
        Refreshing = false;
        ReadCaches();
        Refresh();
    }
}

/// <summary>A label and its value (the game page's sources and Details); Note: a line under the label.</summary>
public sealed record DetailRow(string Label, string Value, string? Note = null)
{
    public bool HasNote => Note != null;
}
