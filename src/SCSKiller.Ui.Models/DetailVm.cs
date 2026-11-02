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

    public string StatusTitle => Running is { } r ? Fmt.Stage(r) : place.Item != null ? Loc.T("Detail_InQueuePosition", place.Number) : Row.StatusText;
    public string StatusReason => Running is { } r && (Fmt.ProgressText(r) is { } p
            ? p + (r.Progress is { PerSecond: > 0 } && Fmt.Left(r, s) is { } t ? Loc.T("Detail_TimeLeft", Format.Duration(t)) : "") + (Fmt.Started(r) is { } at ? " · " + at : "")
            : Fmt.Started(r)) is { } line
        ? Sentence(line)
        : place.Item is { Stage: QueueStage.Waiting, Note: { } n } ? Sentence(n)
        : Fmt.FoldLine(id) ?? GameReason;
    string GameReason => Sentence(s.Status switch
    {
        _ when ScsKiller.CacheOffRefused(Host.Core.Queue, id) => ScsKiller.CacheOffReason,
        _ when s.CompileUnreached => s.StatusReason + (s.WarmedAt != null ? ". " + Sentence(ScsKiller.ClearForGameNote) : ""),
        _ when ScsKiller.IsPartlyCompiled(s) => ScsKiller.RefusedNote(s) + Loc.T("Detail_UpdateCouldFix"),
        GameStatus.Warmed when ScsKiller.IsPartlyWarmed(s) => s.StatusReason,
        GameStatus.Warmed => (Partial || RtAfter || KeyMissing ? "" : Loc.T("Detail_CacheCompletePrefix"))
            + Loc.T(ScsKiller.BothApis(s.Plan) ? "Detail_CompiledBothApis" : "Detail_CompiledFor", s.WarmedDriverVersion)
            + (s.WarmedAt is { } t ? Loc.T("Detail_OnAt", t.LocalDateTime.ToString("g")) : "")
            + (RtAfter ? ". " + Sentence(ScsKiller.RtAfterRecordingNote) : "") + (KeyMissing ? ". " + Sentence(ScsKiller.KeyForFullCoverage) : ""),
        // a cache here: played since a clear or a stopped compile; then the planner's note when the row has one to say
        GameStatus.Ready => (s.CacheOnDisk > 0 ? Loc.T("Detail_NotFullyCompiled", Format.Bytes(s.CacheOnDisk))
            : s.LastWarmTime == null ? Loc.T("Detail_NotCompiledYet") : Loc.T("Detail_CacheEmptyAgain"))
            + (Row.Note != null ? ". " + Sentence(s.StatusReason) : ""),
        GameStatus.NeedsRecording => HasDbTeaser ? NoDbNote(Row.FullNote) : Row.FullNote,   // the teaser says it
        _ when s.Engine?.Encrypted == true => Loc.T("Detail_NeedsAesKey"),   // the title: "Encrypted game files"
        _ => HasDbTeaser ? NoDbNote(s.StatusReason) : s.StatusReason,
    })
        + (IsPartlyCompiled || s.CompileUnreached ? "." : "") + Format.ModNote(s);
    static string NoDbNote(string r) => r.Replace(". " + Sentence(ScsKiller.InDbSupportersNote), "").Replace(". " + Sentence(ScsKiller.InDbNote), "");
    public bool IsPartlyCompiled => ScsKiller.IsPartlyCompiled(s);
    public bool NeedsAesKey => ScsKiller.NeedsAesKey(s);
    public bool HasAesKey => Host.Core.HasEncryptionKey(id);
    public bool CanChangeAesKey => HasAesKey && !NeedsAesKey;
    public bool NeedsGameKey => ScsKiller.NeedsGameKey(s);
    public static readonly string RemoveAesKeyNote = Loc.T("Detail_RemoveAesKeyNote");
    public string PartlyCompiledWhy => s.LastWarmFailedBy switch
    {
        0 or 1 => Loc.T("Detail_PartlyWhyDriver", Host.Platform.Runtime),
        2 => Loc.T("Detail_PartlyWhyFiles"),
        _ => Loc.T("Detail_PartlyWhyOther"),
    };

    // The manifest is public: a PC without "db" sees that the community database covers the game, and where to get it.
    // Shown, it is the page's only mention of the database.
    public bool HasDbTeaser => s is { InCommunityDb: true, Community: null } && Host.Account?.HasDb != true
        && (s.Status == GameStatus.NeedsRecording || RtAfter || s.Status == GameStatus.Unsupported && !NoAntiCheat && s.StatusReason.StartsWith("needs a recording, which", StringComparison.Ordinal));
    public string DbTeaser => Loc.T(s.CommunityDbPsos == 1 ? "Detail_DbTeaserOne" : "Detail_DbTeaserMany", $"{s.CommunityDbPsos:N0}");
    public string DbTeaserLink => Host.Account?.SignedIn == true ? Loc.T("Detail_PatreonMembership") : Loc.T("Settings_SignInPatreon");
    public bool CanCompile => s.Status is GameStatus.Ready or GameStatus.Warmed or GameStatus.Stale || RtPartial;
    public bool ShowMaxCompile => CanCompile && ScsKiller.MaxCompileAdds(Host.Core.Vendor.Caps, s.Engine);
    public static readonly string MaxCompileWhy = Loc.T("Detail_MaxCompileWhy");
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
        if (r is { Ok: false }) { Error = Loc.T("Detail_NotChanged", r.Message); Changed(); }
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
    static readonly string BaBusyTip = Loc.T("Detail_BaBusyTip");
    public string? CompileTip => BaInProgressAny ? BaBusyTip : null;
    public bool CanClearCache => HasCache && !BaInProgressAny;
    bool CompilingSoon => Fmt.CompilingSoon(Host.Core.Queue, id);
    public bool ShowPlay => Row.CanLaunch;
    public bool CanPlay => !s.Playing && !CompilingSoon;
    public string PlayText => s.Playing ? Loc.T("Play_Running") : Loc.T("Play_Play");
    public string PlayTip => s.Playing ? Loc.T("Play_RunningTip", s.Game.Name) : CompilingSoon ? Fmt.CompilingTip : Fmt.PlayVia(s.Game);
    public bool IsManual => s.Game.Store == Store.Manual;
    public bool CanChooseExe => !IsManual && Collections.Member(s.Game.Id) == null;
    public bool ExeChosen => CanChooseExe && s.ExeChosen;
    public string RemoveTip => IsManual ? Loc.T("Detail_ForgetGame")
        : Loc.T("Detail_HideGame");
    /// <summary>The confirmation's text for a store's game, which is hidden.</summary>
    public string HideNote => Loc.T("Detail_HideNote")
        + (BaInProgressAny ? Loc.T("Detail_HideNoteBa") : "")
        + (s.RecorderInstalled ? Loc.T("Detail_HideNoteRecorder") : "");
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
        null => Loc.T("Ba_Title"),
        _ when BaVoided => Loc.T("Detail_StartOver"),
        { Stage: BeforeAfterStage.BeforeArmed, ClearPending: true } => Loc.T("Detail_BaClearRetry"),
        { Stage: BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed } => BaLaunchable ? Loc.T("Play_StartExe", Name) : Loc.T("Detail_StartAsUsual", Name),
        { Stage: BeforeAfterStage.BeforePlaying or BeforeAfterStage.AfterPlaying } p => p.Minutes * 60 - p.PlayedSeconds is var left and > 0
            ? Loc.T("Detail_BaPlayingLeft", $"{(int)left / 60}:{(int)left % 60:00}") : Loc.T("Detail_BaTimesUp"),
        { Stage: BeforeAfterStage.Compiling or BeforeAfterStage.AfterReady } => Loc.T("Detail_BaContinue"),
        _ => Loc.T("Detail_BaView"),
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
    public string? BaTip => Ba == null ? blocked ?? Loc.T("Detail_BeforeAfterDesc")
        : Ba.Stage is BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed && !BaLaunchable ? Loc.T("Detail_StartAsUsualTip", Name) : null;
    public string BaNote => Ba switch
    {
        null => BaStart.Note,
        { Problem: { } problem } => problem,
        { Stage: BeforeAfterStage.BeforeArmed } b => Loc.T("Detail_BaPlayMinutes", b.Minutes),
        { Stage: BeforeAfterStage.AfterArmed } a => Loc.T("Detail_BaLoadSame", a.Minutes),
        { Stage: BeforeAfterStage.BeforePlaying or BeforeAfterStage.AfterPlaying } => Loc.T("Detail_BaCloseWhenTime"),
        { Stage: BeforeAfterStage.Compiling } => Loc.T("Detail_BaWaitingCompile"),
        { Stage: BeforeAfterStage.AfterReady } => Loc.T("Detail_BaCompiledNext"),
        _ => "",
    };
    public bool HasBaNote => ShowBa && BaNote.Length > 0;
    /// <summary>The compile failed or was stopped: the note says why.</summary>
    public bool BaCompileAgain => Ba is { Stage: BeforeAfterStage.Compiling, Problem: not null } && !BaVoided && !Queued;

    public string AddText => Queued ? Loc.T("Common_InQueue") : OffersCareful ? Loc.T("Detail_CompileCarefully") : RtPartial ? Loc.T("Detail_CompileNoRtPartial") : Loc.T("Common_Compile");
    /// <summary>Partly warmed by a fast compile: the compile button turns the careful compile on first.</summary>
    public bool OffersCareful => ScsKiller.IsPartlyWarmed(s) && s.Careful is { On: false };

    // AMD's careful compile: a switch in the compile card, off while the game is queued (a running compile keeps its schedule)
    public bool HasCareful => s.Careful != null && CanCompile;
    public bool? CarefulPending { get; set; }
    public bool LeftOutOn => LeftOutPending ?? s.LeftOutOfAddAll;
    public bool? LeftOutPending { get; set; }
    public bool SharedOn => SharedPending ?? s.SharedHashes;
    public bool? SharedPending { get; set; }
    public string SharedNote => s.SharedChoice == null ? Loc.T("Detail_FollowsSettings") : Loc.T("Detail_SetForThisGame");
    public bool ShowSharedFollow => SharedPending == null && s.SharedChoice != null;
    public bool CarefulOn => CarefulPending ?? s.Careful?.On == true;
    public bool CanToggleCareful => CarefulPending == null && !Queued;
    public string CarefulNote => Loc.T("Detail_CarefulPrefix") + (s.Careful is { Recorded: 0 } ? Loc.T("Detail_CarefulNoRecording")
        : Loc.T("Detail_CarefulThreads", ScsKiller.AmdCarefulThreads)
          + (s.Careful?.Estimate is { } est ? Loc.T("Detail_AboutTime", Format.Duration(est)) + (s.Careful.On || Fmt.CompileTime(s) is not { } fast ? "." : Loc.T("Detail_InsteadOf", Format.Duration(fast))) : ""))
        + (s.Careful?.LaunchCompiled is { } lc ? Loc.T("Detail_CarefulLaunchPct", $"{lc * 100:0}") : "");
    // AMD: the compiled cache kept aside, in the compile card
    public bool HasKept => s.Kept != null && Host.Core.Vendor.Vendor == GpuVendor.Amd && Host.Core.Settings.KeepCachesAside;
    public string KeptSize => s.Kept is { } k ? Fmt.KeptSize(k) : "";
    public string KeptState => s.Kept switch
    {
        null => "",
        { Unkept: > 0 and var n, HeldBy: var who } => Loc.T(n == 1 ? "Detail_KeptFileOne" : "Detail_KeptFilesMany", n, who ?? Loc.T("Detail_AnotherApp")),
        { Frees: > 0 } when s.AntiCheat != AntiCheat.None => Loc.T("Detail_KeptPutBackSoon"),   // no Play button, VAC keeps it
        { Frees: > 0 } => Loc.T("Detail_KeptRemoved"),
        { PutBackAt: { } at, Late: true } => Loc.T("Detail_KeptPutBackLate", Fmt.When(at)),
        { PutBackAt: { } at } => Loc.T("Detail_KeptPutBackEarly", Fmt.When(at)),
        _ => Loc.T("Detail_KeptInCache"),
    };

    // 0 = the global setting, then NewShadersMode's values in order
    public static readonly string[] NewShadersLabels =
        [Loc.T("Common_Off"), Loc.T("Settings_NotifyMe"), Loc.T("Settings_NewShadersNotify"), Loc.T("Settings_NewShadersSilent")];
    public List<string> NewShadersChoices => Same(ref newShadersChoices, [Loc.T("Detail_UseGlobalSetting", NewShadersLabels[(int)NewShaders.Global(Host.Core.Settings)]), .. NewShadersLabels]);
    // the same list while its labels hold: a new one resets the selection
    List<string>? newShadersChoices, rayTracingChoices, cacheCapChoices, traversalChoices;
    static List<string> Same(ref List<string>? kept, List<string> now) => kept != null && kept.SequenceEqual(now) ? kept : kept = now;
    public int NewShadersIndex => s.NewShadersOverride is { } m ? (int)m + 1 : 0;
    // 0 = the global setting, 1 = on, 2 = off
    public List<string> RayTracingChoices => Same(ref rayTracingChoices, [Host.Core.Settings.CompileRayTracing ? Loc.T("Traversal_DefaultOn") : Loc.T("Traversal_DefaultOff"), Loc.T("Common_On"), Loc.T("Common_Off")]);
    public int RayTracingIndex => s.RayTracingOverride switch { null => 0, true => 1, false => 2 };
    public bool ShowTraversal => TraversalFix.Applies(s, Host.Core.Vendor.Caps);
    public List<string> TraversalChoices => Same(ref traversalChoices, TraversalFix.GameChoices(Host.Core.Settings, s));
    public int TraversalIndex => TraversalFix.GameIndex(s);
    public string TraversalNote => TraversalFix.GameNote(s, Host.Core.Vendor.Gpu);
    public bool TraversalLowVram => TraversalFix.LowVram(Host.Core.Vendor.Gpu) == true;
    public string TraversalWarning => TraversalFix.LowVramWarning;
    // no plan, or one from before inline ray tracing was counted: unknown, so the choice shows
    public bool ShowRayTracing => P?.RtInline == null || s.RayTracingOverride != null || ScsKiller.HasRayTracing(P);
    public List<string> CacheCapChoices => Same(ref cacheCapChoices, [Loc.T("Common_NoLimit"), .. ScsKiller.CacheCapsGB.Select(gb => $"{gb} GB")]);
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
    public string? LaunchLineRefused => launchLine?.Refused is { } why ? Loc.T("Detail_LaunchLineRefused", why) : null;
    public bool HasLaunchLineRefused => LaunchLineRefused != null;
    bool launchCopied;
    public string CopyLaunchText => launchCopied ? Loc.T("Common_Copied") : Loc.T("Common_Copy");
    public async Task CopyLaunchLine()
    {
        if (launchLine is not { Refused: null } l) return;
        try
        {
            await Task.Run(() => Host.Core.LaunchLineOf(id, make: true));   // the folders it names, which opening the page doesn't make
            await Host.CopyText(l.Text);
            (launchCopied, Error) = (true, null);
        }
        catch (Exception e) { Error = Loc.T("Report_CouldntCopy", e.Message); }
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
        ? Loc.T("Detail_KeptCacheNote", DiskValue, Loc.T(s.NoStutter != null ? "Detail_MarkedNoStutter" : "Detail_MarkedNotSupported"))
        : null;
    public bool HasKeptCache => KeptCacheNote != null;

    // Maximum mode suggested for this game on this GPU vendor (Games.MaximumMode), or on for it alone
    public bool? MaximumPending { get; set; }
    public bool MaximumSuggested => s.MaximumSuggestion != null;
    // on for every game in Settings: the per-game switch changes nothing, so it isn't offered
    public bool HasMaximumLine => MaximumSuggested || s.MaximumForGame == true && !Host.Core.Settings.MaximumPlans;
    public string MaximumLine => MaximumSuggested
        ? Loc.T("Detail_MaximumHelps")
        : Loc.T("Detail_MaximumOn");
    public string? MaximumNote => string.Join(" ", new[] { s.MaximumSuggestion is { Length: > 0 } note ? note : null,
        MaximumSuggested && s.MaximumAddsBytes is { } adds ? Loc.T("Detail_MaximumAmdAdds", Format.Bytes(adds), Format.Bytes(AmdAppCache.DxcCacheCap)) : null }.OfType<string>()) is { Length: > 0 } t ? t : null;
    public bool HasMaximumNote => MaximumNote != null;
    public string MaximumAction => MaximumSuggested ? Loc.T("Detail_TurnOnFor") : Loc.T("Detail_TurnOffFor");
    public bool CanSetMaximum => MaximumPending == null && !Queued;
    public bool ShowClearCache => HasCache || CanCompile;
    // as Disk space shows it
    public string ClearCacheText => s.DiskBytes != null ? Loc.T("Detail_ClearCacheCount", DiskValue) : Loc.T("Detail_ClearCache");
    public string ClearCacheTip => BaInProgressAny ? BaBusyTip : HasCache ? Loc.T("Detail_ClearCacheTip", Host.Platform.SystemShaderCache ? Loc.T("Detail_AndWindows") : "")
        : Loc.T("Detail_ClearCacheTipNone");

    // Rate this compile (CompatReport) and what players with a similar system reported (CompatFeed)
    string? ReportWhyNot => CompatReport.WhyNot(s, ScsKiller.PackGpu(Host.Core.Vendor.Vendor), Host.Core.LinuxReportsRefused);
    public bool CanReport => ReportWhyNot == null;
    public string ReportTip => ReportWhyNot ?? Loc.T("Detail_ReportTip");
    public bool HasSimilar => s.Similar is SimilarSystems.Helped or SimilarSystems.NoHelp;
    public bool HasSimilarWarning => s.Similar == SimilarSystems.Warning;
    public bool HasRuntimeWarning => s.RuntimeWarning != null;
    public string RuntimeWarning => s.RuntimeWarning ?? "";
    public string SimilarText => s.Similar switch
    {
        SimilarSystems.Helped => Loc.T("Detail_SimilarHelped"),
        SimilarSystems.NoHelp => Loc.T("Detail_SimilarNoHelp"),
        SimilarSystems.Warning => Loc.T("Detail_SimilarWorse", Name),
        _ => "",
    };

    public bool IsKnownStutter => s.KnownStutter != null;
    public string StutterReason => s.KnownStutter?.Reason ?? "";
    public bool IsNoStutter => s.NoStutter != null;
    public string NoStutterReason => s.NoStutter ?? "";
    /// <summary>The source as a link when it's a URL; otherwise <see cref="StutterCitation"/> shows it as text.</summary>
    public Uri? StutterUri => Uri.TryCreate(s.KnownStutter?.Source, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u : null;
    public string StutterLinkText => StutterUri != null ? Loc.T("Detail_SourceLink", s.KnownStutter!.Date) : "";
    public string StutterCitation => s.KnownStutter is { } k && StutterUri == null ? Loc.T("Detail_SourceCitation", k.Source, k.Date) : "";
    public string EngineChip => s.Engine is { } e ? Format.Engine(e, e.Family == "Unreal" ? "Unreal Engine" : null) + Fmt.Guessed(e) + (e.Fork != null ? Loc.T("Detail_CustomFork") : "") : Loc.T("Fmt_UnknownEngine");
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
            var auto = s.Engine?.GraphicsApi.EndsWith(ScsKiller.RecordingSuffix, StringComparison.Ordinal) == true ? Loc.T("Detail_AutoFromRecording", Dx(s.Engine.GraphicsApi))
                : s.ApiDetected == Core.Unreal.UnrealRhi.Ambiguous ? Loc.T("Detail_AutoDx11And12")
                : s.ApiDetected is { } d ? Loc.T("Detail_AutoOf", Dx(d)) : Loc.T("Detail_Auto");
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
    public string AntiCheatChip => NoAntiCheat ? Loc.T("Detail_NoAntiCheat") : ScsKiller.AntiCheatName(s);
    public string AntiCheatTip => NoAntiCheat ? Loc.T("Detail_NoAntiCheatTip")
        : s.RiskEligible ? Loc.T("Detail_AntiCheatSessionTip", ScsKiller.AntiCheatName(s))
        : !Host.Platform.RecordAntiCheatGames ? NoAntiCheatSessions
        : ScsKiller.RiskNotHere(s) ?? Loc.T("Detail_AntiCheatNever", ScsKiller.AntiCheatName(s), RecorderDll);
    static string NoAntiCheatSessions => Loc.T("Detail_AntiCheatNotOnPlatform", Host.Platform.Name);
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
            .Concat(Mw.Any(t => t.Label == "DLSS") && GpuBackends.OwnDriver(Host.Core.Vendor) == GpuVendor.Nvidia ? [Loc.T("Detail_DlssByDriver")] : []).ToList() is { Count: > 0 } also
            && s.Status != GameStatus.Unsupported
        ? Loc.T("Detail_AlsoCompiles", string.Join(" · ", also)) : Loc.T("Detail_Detected", string.Join(" · ", Mw.Select(t => t.Label)));
    public string MiddlewareTip => Loc.T("Detail_MiddlewareTip0") + Loc.T("Detail_MiddlewareTip1")
        + (s.Plan is { MiddlewareItems: > 0 } p ? Loc.T("Detail_MiddlewareTipPlan", Fmt.N(p.MiddlewareItems)) : "") + ".\n\n"
        + string.Join("\n", Mw.Select(Fmt.TagTip));

    // The one tip left: a game that can't compile before a recording, or not its ray tracing (a plan's other gaps are the
    // coverage card's "what's left").
    public bool HasRecordTip => CanRecord && (s.Status == GameStatus.NeedsRecording && !ScsKiller.NeedsSteamStart(s) || RtAfter) && !RecordOn && s.NoStutter == null;
    bool Enough => ScsKiller.RecordedEnough(s);
    public string RecordTipTitle => Enough ? Loc.T("Detail_RecordMore") : Loc.T("Detail_Record5Min");
    public string RecordTip => RtPartial || RtAfter ? Loc.T("Detail_TurnOnRtPlay", TurnOnAndPlay, Enough ? "" : Loc.T("Detail_ForAbout5Min")) + ScsKiller.RtWhy(Host.Core.Vendor.Caps, s.Engine)
            + (RtAfter ? Loc.T("Detail_RtRestCompiled") : Loc.T("Detail_RtRestFiles"))
        : Enough ? Sentence(s.StatusReason) + "."
        : Loc.T("Detail_RecordTip", TurnOnAndPlay);
    // a collection's other game is recorded by its first game's recorder, switched on that game's page
    string TurnOnAndPlay => Collections.RecordedBy(s.Game.Id) is { } by ? Loc.T("Detail_TurnOnOthersPage", Collections.Member(by)!.Value.Game.Name) : Loc.T("Detail_TurnOnAndPlay");
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
    public string CoverageHeadline => P == null ? Loc.T("Detail_CoverageKnownLater")
        : s.CompileUnreached ? Loc.T("Detail_CoverageNotUsed")
        : DriverChanged ? Loc.T("Detail_CoverageOldDriver", s.WarmedDriverVersion)
        : Dx11Only && Pct is null or 100 ? Loc.T(Compiled ? "Detail_Covers" : "Detail_WillCover") + Loc.T("Detail_CoversDx11") + (Core.Planning.Planner.D3D11CoversVs(Host.Core.Vendor.Caps) || P.D3D11Draws > 0 ? "" : Loc.T("Detail_CoversExceptVs"))
        : Pct is not { } pct ? Loc.T("Detail_CoverageAgain")
        : Loc.T(Compiled || s.NewOnly ? "Detail_Covers" : s.WarmedAt != null ? "Detail_LastCovered" : "Detail_WillCover")
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
        p.Generated > 0 ? new(ScsKiller.BothApis(p) ? Loc.T("Detail_SrcDx12Files") : Loc.T("Detail_SrcGameFiles"), Fmt.N(p.Generated),
            Pct == 100 && s.ShaderCount is { } n && p.Generated > n ? Loc.T("Detail_MoreThanShaders") : null) : null,
        !Vk && FromPlay > 0 ? new(RecordingSource, Fmt.N(FromPlay), CommunityLine) : null,
        p.FromDownload > 0 ? new(Loc.T("Detail_SrcSteamDownload"), Fmt.N(p.FromDownload)) : null,
        Vk && p.FromCommunity > 0 ? new(Loc.T("Detail_SrcCommunityDb"), Fmt.N(p.FromCommunity)) : null,
        Vk && FromPlay > 0 ? new(FromPlayLabel, Fmt.N(FromPlay), CommunityLine) : null,
        p.CacheBudgetLeftOut is > 0 and var left ? new(ScsKiller.CacheBudgetTitle(Caps, s.Engine, s.PlanCacheCapGB), Fmt.N(left), ScsKiller.CacheBudgetNote(Caps, s.Engine, s.PlanCacheCapGB)) : null,
        s.Official is { } o ? new(Loc.T("Detail_SrcOfficial"), $"{o.Psos:N0} pipelines", Loc.T("Detail_SrcOfficialNote", o.DownloadedAt.LocalDateTime.ToString("d"))) : null,
        s.Community == null && s.InCommunityDb is { } inDb && !HasDbTeaser ? new(Loc.T("Detail_SrcTheCommunityDb"), Loc.T(inDb ? "Detail_SrcInDb" : "Detail_SrcNotInDb")) : null,
        p.MiddlewareItems > 0 ? new(p.MiddlewareSharedItems == 0 ? Loc.T("Detail_SrcUpscalersLearned")
            : p.MiddlewareSharedItems == p.MiddlewareItems ? Loc.T("Detail_SrcUpscalersShared") : Loc.T("Detail_SrcUpscalersBoth"), Fmt.N(p.MiddlewareItems)) : null,
        p.D3D11Shaders > 0 ? new(ScsKiller.BothApis(p) ? Loc.T("Detail_SrcDx11Files") : Loc.T("Detail_SrcDx11"), Fmt.N(p.D3D11Shaders)) : null,
        p.RtLibraries > 0 && s.RtSkipped ? new(Loc.T("Detail_RayTracing"), Loc.T("Detail_RtSkipped"))
        : p.RtLibraries > 0 ? new(Loc.T("Detail_RayTracedEffects"), s.Engine?.NoRayTracing == true ? Loc.T("Detail_RtOffInGame") : s.Engine?.NoRtPipelines == true ? Loc.T("Detail_RtInlineFiles") : p.RtUncovered == 0 ? Loc.T("Detail_RtCovered") : s.RtToPlan ? Loc.T("Detail_RtBeingChecked") : RtUnseen ? Loc.T("Detail_RtNotSeen") : RtInline ? Loc.T("Detail_RtInlineCovered") : !Rt ? Loc.T("Detail_RtMostlyCovered") : CanRecord ? Loc.T("Detail_RtNeedRecording") : Loc.T("Detail_RtNotCompiled")) : null,
    }.OfType<DetailRow>().ToList();
    public bool HasSources => Sources.Count > 0;

    // a Vulkan plan reads no game files: its pipelines are Steam's download and what play on this PC recorded, also a
    // DirectX game's under Proton made of Steam's alone. A DirectX plan with Steam's counts its download with its recorded
    bool Vk => s.Engine is { } e && Core.Vulkan.VulkanPlan.IsVulkan(e) || P is { FromDownload: not null, Generated: 0, SynthesizedTemplates: 0 };
    long FromPlay => P is { } p ? p.Recorded - (p.FromDownload ?? 0) - (p.FromCommunity ?? 0) : 0;
    string FromPlayLabel => P?.FromDownload == null ? Loc.T("Detail_SrcRecorded") : Loc.T("Detail_SrcFromPlay");   // a plan from before the split was counted

    /// <summary>The recorded row's label: whose recordings the plan's recorded pipelines come from.</summary>
    string RecordingSource
    {
        get
        {
            if (UsedCommunity == null && s.Official == null) return Loc.T("Detail_SrcYourPlay");
            string[] from = [.. (UsedCommunity ?? s.Official)!.WithLocalRecording ? [Loc.T("Detail_YourRecording")] : Array.Empty<string>(),
                .. UsedCommunity != null ? [Loc.T("Detail_TheCommunityDb")] : Array.Empty<string>(), .. s.Official != null ? [Loc.T("Detail_TheOfficialRecording")] : Array.Empty<string>()];
            var text = from.Length == 1 ? from[0] : string.Join(", ", from[..^1]) + Loc.T("Common_And") + from[^1];
            return char.ToUpperInvariant(text[0]) + text[1..];
        }
    }
    /// <summary>The community recording the plan uses: not one that doesn't match the game's files.</summary>
    CommunityInfo? UsedCommunity => s.Community is { OtherVersion: false } c ? c : null;
    /// <summary>The community database's line under the recording row; null = nothing to say.</summary>
    string? CommunityLine => UsedCommunity is { } c
        ? Loc.T("Detail_CommunityDownloaded", c.DownloadedAt.LocalDateTime.ToString("d")) + (s.RecordingSharedAt is { } at ? Loc.T("Detail_YoursShared", at.LocalDateTime.ToString("d")) : "")
        : s.RecordingSharedAt is { } at2 ? Loc.T("Detail_SharedWith", at2.LocalDateTime.ToString("d")) : null;

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
            : Loc.T("Detail_RtNotCompiledWhy", s.RiskEligible ? ScsKiller.RiskNoRecording : !NoAntiCheat ? Loc.T("Detail_WhichBlocks", ScsKiller.AntiCheatName(s)) : s.RootUnconfirmed ? ScsKiller.ManualNoRecording : s.PreloadHeld is { } held ? ScsKiller.PreloadHeldNoRecording(held, s.PreloadHeldByOptions) : ScsKiller.StreamlineNoRecording),
        LeftCase switch
        {
            Left.AntiCheat => s.RiskEligible ? Loc.T("Detail_LeftAntiCheatRisk")
                : !NoAntiCheat ? Loc.T("Detail_LeftAntiCheatBlocks", ScsKiller.AntiCheatName(s))
                : s.RootUnconfirmed ? Loc.T("Detail_LeftRootUnconfirmed")
                : s.PreloadHeld != null ? Loc.T("Detail_LeftPreloadHeld")
                : Loc.T("Detail_LeftDlssBlocks"),
            Left.EngineSlots => ScsKiller.EngineSlotsNote(RecordOn),
            Left.UnknownSlots => Loc.T("Detail_LeftUnknownSlots"),
            Left.NotSeen => ScsKiller.NotSeenNote(RecordOn, P),
            Left.Recording => Loc.T("Detail_LeftRecording"),
            Left.PlayedCompiles => Loc.T("Detail_LeftPlayedCompiles", $"{PlayCompiles(L!):N0}") + (tipAsks ? "" : Loc.T("Detail_RecordingPicksUp")),
            _ when Rt || RtUnseen || RtInline => null,   // ray tracing is what's left
            Left.PlayedClean => Loc.T("Detail_LeftPlayedClean"),
            Left.NothingKnown => Loc.T("Detail_LeftNothingKnown"),
            _ => Loc.T("Detail_LeftFewEffects") + (tipAsks ? "" : Loc.T("Detail_ShortRecordingPicksUp")),
        },
        !Guessed ? null : tipAsks ? Loc.T("Detail_GuessedShort") : Loc.T("Detail_GuessedLong"),
    }.OfType<string>());
    // per-stage plans (AMD): the planner warns that a wrong guess costs a compile in game
    bool Guessed => P is { } g && g.GuessedUnits > 0.1 * (g.ExactUnits + g.InferredUnits + g.GuessedUnits);
    // not twice: a game that needs a recording has the button in its tip
    public bool ShowLeftRecord => ShowRecordAction && !HasRecordTip && s.NoStutter == null && (LeftCase is Left.EngineSlots or Left.NotSeen or Left.PlayedCompiles or Left.PlayOnly || Guessed || Rt || RtUnseen || RtInline);
    // before the first compile there's nothing left yet. Recording on: what's missing comes from playing, without the button
    public bool ShowLeftCallout => HasPlan && (ShowLeftRecord || RecordOn && s.NoStutter == null && LeftCase is Left.NotSeen or Left.EngineSlots);
    public string LeftTitle => Rt || RtUnseen ? Loc.T("Detail_LeftTitleRt") : Loc.T("Detail_LeftTitle");

    public string CoverageValue => Pct is { } pct ? $"{pct}%" : Dx11Only && !s.CompileUnreached ? Loc.T("Detail_All") : Format.Dash;
    public string CoverageLabel => s.CompileUnreached ? Loc.T("Detail_NotUsed") : Pct != null || Dx11Only ? Loc.T("Detail_Covered") : Loc.T("Detail_NotYet");
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
    public string TimeLabel => s.LastWarmTime != null ? Loc.T("Detail_CompileTime") : Loc.T("Detail_CompileTimeEst");
    public string TimeValue => s.LastWarmTime is { TotalMinutes: >= 1, TotalHours: < 1 } t ? $"{t.Minutes} min {t.Seconds} s"   // measured: to the second
        : Format.Duration(Fmt.CompileTime(s));
    public bool HasDiskValue => s.DiskBytes != null || s.EstimatedCacheBytes != null;
    public string DiskLabel => s.DiskBytes != null ? Loc.T("Detail_DiskSpace") : Loc.T("Detail_DiskSpaceEst");
    public string DiskValue => Format.Bytes(OnDisk ?? s.EstimatedCacheBytes);
    public bool HasDiskInUse => s.CacheOnDisk != null && Format.InUseShown(OnDisk, InUse);
    public string DiskInUse => Loc.T("Detail_InUse", Format.Bytes(InUse));
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
    public string StuttersValue => StutterCount is not { } n ? "" : n == 0 ? Loc.T("Common_None") : Fmt.N(n);
    public bool HasStutters => StutterCount > 0;
    public bool NoStutters => StutterCount == 0;
    // without frame times the count is of compiles, which may or may not have stuttered
    public string StuttersLabel => F != null ? Loc.T("Detail_StuttersLastPlayed") : Loc.T("Detail_CompilesWhilePlayed");

    /// <summary>Every count, for power users.</summary>
    public IReadOnlyList<DetailRow> Details => new DetailRow?[]
    {
        P is { } p ? new(Loc.T("Detail_DtPipelinesInPlan"), Fmt.N(p.Recorded + p.Generated + p.MiddlewareItems + p.Variants)) : null,
        s.ShaderCount != null ? new(Loc.T("Detail_DtShadersInFiles"), ShaderCount) : null,
        P is { StageSets: > 0 } ? new(Loc.T("Detail_DtCombinations"), Fmt.N(P.StageSets)) : null,
        P is { LeftOut: > 0 } ? new(Loc.T("Detail_DtLeftOut"), ScsKiller.LeftOutValue(P)) : null,
        P is { StageSets: 0, Uncovered: > 0 } ? new(Loc.T("Detail_DtLeftOutSlots"), Fmt.N(P.Uncovered)) : null,
        P != null && !Vk ? new(Loc.T("Detail_SrcRecorded"), Fmt.N(FromPlay)) : null,
        P is { FromDownload: { } dl } && (Vk || dl > 0) ? new(Loc.T("Detail_SrcSteamDownload"), Fmt.N(dl)) : null,
        Vk && P != null ? new(FromPlayLabel, Fmt.N(FromPlay)) : null,
        P != null && !Vk ? new(Loc.T("Detail_DtBuiltFromFiles"), Fmt.N(P.Generated)) : null,
        P is { MiddlewareItems: > 0 } ? new(Loc.T("Detail_DtUpscalerPipelines"), Fmt.N(P.MiddlewareItems)) : null,
        P is { Variants: > 0 } ? new(Loc.T("Detail_DtOtherFiltering"), Fmt.N(P.Variants)) : null,
        P is { D3D11Shaders: > 0 } ? new(Loc.T("Detail_SrcDx11"), Fmt.N(P.D3D11Shaders)) : null,
        P is { RtLibraries: > 0 } ? new(Loc.T("Detail_DtRtLibraries"), $"{P.RtLibraries:N0}" + (s.Engine?.NoRtPipelines == true ? Loc.T("Detail_DtRtNotUsed") : s.RtSkipped ? Loc.T("Detail_DtRtSkippedSet") : P.RtUncovered > 0 ? Loc.T("Detail_DtRtNotCompiled", $"{P.RtUncovered:N0}") : "")) : null,
        P is { RtInline: > 0 } ? new(Loc.T("Detail_DtRtInline"), Fmt.N(P.RtInline)) : null,
        s.Status != GameStatus.Unsupported && s.InCommunityDb is { } db && !HasDbTeaser ? new(Loc.T("Detail_DtCommunityData"), db ? $"{s.CommunityDbPsos:N0} pipelines" : Loc.T("Detail_DtNoneYet")) : null,
        s.Community is { } c ? new(Loc.T("Detail_DtDownloadedRecording"), c.OtherVersion ? Loc.T("Detail_DtNoMatch") : $"{c.Psos:N0} pipelines") : null,
        s.Official is { } o ? new(Loc.T("Detail_SrcOfficial"), $"{o.Psos:N0} pipelines") : null,
        P != null && !Vk ? new(Loc.T("Detail_DtTemplates"), Fmt.N(P.SynthesizedTemplates)) : null,
        P != null && !Vk ? new(Loc.T("Detail_DtLayouts"), $"{P.RootSignatures:N0} · {RootSigSource(P)}") : null,
        P is { } u && u.ExactUnits + u.InferredUnits + u.GuessedUnits > 0 ? new(Loc.T("Detail_DtStageUnits"), $"{u.ExactUnits:N0} / {u.InferredUnits:N0} / {u.GuessedUnits:N0}") : null,
        P is { LayoutCoverage: > 0 } ? new(Loc.T("Detail_DtVertexLayouts"), $"{P.LayoutCoverage:P1}") : null,
        s.CacheOnDisk is { } disk ? new(Loc.T("Detail_DtDriverCache"), Loc.T("Detail_DtMeasured", Format.Bytes(disk))) : s.EstimatedCacheBytes is { } est ? new(Loc.T("Detail_DtDriverCache"), Loc.T("Detail_DtEstimated", Format.Bytes(est))) : null,
        s.WindowsCache is { } w ? new(ScsKiller.WindowsPart, Format.Bytes(w)) : null,
    }.OfType<DetailRow>().ToList();

    // "Shipped with the shaders" is the carver's case.
    string RootSigSource(PlanStats p) =>
        p.RootSigRuleVerified ? Loc.T("Detail_RsTested")
        : ScsKiller.ApiKey(s.Engine?.GraphicsApi) == "11" ? Loc.T("Detail_RsNotNeeded")
        : p is { RootSignatures: 0, Recorded: > 0 } ? Loc.T("Detail_RsInRecorded")
        : s.Engine?.Family == "Unreal" ? (p.Recorded > 0 ? Loc.T("Detail_RsLearned") : Loc.T("Detail_RsNotTested"))
        : p.Recorded > 0 ? Loc.T("Detail_RsLearned") : Loc.T("Detail_RsShipped");

    // Last compile: when, how long, what failed or was skipped and what to do; never compiled: what the first one costs.
    public bool HasCompileCard => CanCompile || s.LastWarmTime != null;
    public string CompileTitle => s.LastWarmTime == null ? Loc.T("Detail_FirstCompile") : Loc.T("Detail_LastCompile");
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
        ? Loc.T("Detail_CompileAt", Fmt.When(at)) + (s.LastWarmTime is { } t ? Loc.T("Detail_InDuration", Format.Duration(t)) : "") + Loc.T("Detail_ForDriverAt", s.WarmedDriverVersion)
          + (ScsKiller.NothingFailed(s) ? Loc.T("Detail_NothingFailed") : "")   // null = not known (a warm from before these were kept): no claim
          + (ScsKiller.RefusedLine(s) is { } refused ? " " + refused : "")
          + (OnDisk is { } c ? Loc.T("Detail_UsesDisk", Format.Bytes(c)) : "")
          + " " + OwnShaderStep
        : s.LastWarmTime is { } last ? Loc.T("Detail_CacheClearedLast", Format.Duration(last))
        : (s.EstimatedWarmTime is { } est ? Loc.T("Detail_TakesAbout", Format.Duration(est)) : Loc.T("Detail_RunsInBackground"))
          + (s.EstimatedCacheBytes is { } b ? Loc.T("Detail_AddsEstimated", Format.Bytes(b)) : "");
    // no game file says whether a game runs its own precompile at startup: said on every compiled game's page, not its row
    public static readonly string OwnShaderStep = Loc.T("Detail_OwnShaderStep");
    public string SkippedTitle => Loc.T("Detail_NSkipped", $"{Skipped + Crashed + DriverSkipped + Unbuilt + Invalid + Unsupported:N0}");
    long NeedsRecording => Math.Min(Skipped, s.LastWarmNeedsRecording ?? 0);   // of Skipped: flagged by the community recording
    long NotInGame => Skipped - NeedsRecording;
    public string? SkippedText => s.WarmedAt == null || Skipped + Crashed + DriverSkipped + Unbuilt + Invalid + Unsupported == 0 ? null
        : string.Join(" ", new[]
        {
            NotInGame > 0 && P is { FromDownload: > 0, Generated: 0 } ? Loc.T("Detail_SkippedDownload", $"{NotInGame:N0}", ScsKiller.DownloadSkipped)
            : NotInGame > 0 ? Loc.T(NotInGame == 1 ? "Detail_SkippedNotInGameOne" : "Detail_SkippedNotInGameMany", $"{NotInGame:N0}") : null,
            Crashed - CrashedBefore is > 0 and var crashedNow ? Loc.T(crashedNow == 1 ? "Detail_SkippedCrashOne" : "Detail_SkippedCrashMany", $"{crashedNow:N0}") : null,
            DriverSkipped - Known is > 0 and var now ? Loc.T(now == 1 ? "Detail_SkippedDriverOne" : "Detail_SkippedDriverMany", $"{now:N0}") : null,
            Known + CrashedBefore > 0 ? ScsKiller.KnownNote(Known + CrashedBefore) + "." : null,
            Unbuilt > 0 ? Loc.T(Unbuilt == 1 ? "Detail_SkippedUnbuiltOne" : "Detail_SkippedUnbuiltMany", $"{Unbuilt:N0}") : null,
            Invalid > 0 ? Loc.T(Invalid == 1 ? "Detail_SkippedInvalidOne" : "Detail_SkippedInvalidMany", $"{Invalid:N0}") : null,
            Unsupported > 0 ? Loc.T(Unsupported == 1 ? "Detail_SkippedUnsupportedOne" : "Detail_SkippedUnsupportedMany", $"{Unsupported:N0}") : null,
            NeedsRecording > 0 ? Loc.T(NeedsRecording == 1 ? "Detail_SkippedNeedsRecordingOne" : "Detail_SkippedNeedsRecordingMany", $"{NeedsRecording:N0}") : null,
        }.OfType<string>());
    public bool HasSkipped => SkippedText != null;

    // Last play session (SessionStats, no timestamp): one bar, three segments, under the Record switch. Ready = from the
    // game's library or the driver cache; ray tracing state objects have their own line, so Requests isn't used.
    SessionStats? L => s.LastSession;
    public bool HasSession => L != null;
    public bool ShowSessionHint => L == null && CanRecord && SessionHint != null && !FramesOnly;
    public string? SessionHint => Format.SessionHint(s);
    public string SessionHeader => L is { } l ? Loc.T("Detail_SessionHeader", Format.Duration(F?.Duration ?? l.Duration)) : "";
    // ray tracing state objects included, so the headline agrees with the ray tracing line under it
    static long PlayCompiles(SessionStats l) => l.Compiles + l.StateObjectsCompiled;
    public string SessionVerdict
    {
        get
        {
            if (L is not { } l) return "";
            long play = PlayCompiles(l), started = l.StartupCompiles + l.StateObjectsStartupCompiled, ready = l.FromGameLibrary + l.CacheHits + l.StateObjectsReady;
            if (play + started == 0)
                return ready > 0 ? Loc.T("Detail_SvAllReady", $"{ready:N0}") : Loc.T("Detail_SvNothing");
            // the longest compile call, of the PSOs only; whether a frame stuttered is the frame report's to say
            string worst = l.StateObjectsCompiled > 0 ? "" : play == 1 ? Loc.T("Detail_SvOneTook", $"{l.WorstCompileMs:0}") : Loc.T("Detail_SvLongest", $"{l.WorstCompileMs:0}");
            return (play == 0 ? Loc.T("Detail_SvNothing") : Loc.T("Detail_SvNPlayed", $"{play:N0}", worst) + ".")
                + (started > 0 ? Loc.T("Detail_SvStartup", $"{started:N0}") : "")
                + (ready > 0 ? Loc.T("Detail_SvReady", $"{ready:N0}") : "");
        }
    }
    public bool HasStateObjectLine => L is { } l && l.StateObjectsReady + l.StateObjectsCompiled + l.StateObjectsStartupCompiled > 0;
    public string StateObjectLine => L is { } l ? Loc.T("Detail_StateObjectLine", $"{l.StateObjectsReady:N0}", $"{l.StateObjectsCompiled:N0}")
        + (l.StateObjectsStartupCompiled > 0 ? Loc.T("Detail_StateObjectStartup", $"{l.StateObjectsStartupCompiled:N0}") : ".") : "";
    public bool HasRayQueryLine => L?.RayQueryRecompiles > 0;
    public string RayQueryLine => L is { RayQueryRecompiles: > 0 and var n }
        ? Loc.T(n == 1 ? "Detail_RayQueryOne" : "Detail_RayQueryMany", $"{n:N0}") : "";

    // The last launch's frame times (FrameLog): its shader stutters, other hitches and loading, the 1% low of play.
    public FrameReport? F => s.LastFrames;
    List<Hitch> ShaderHitches => F?.Hitches.Where(h => h.Cause == HitchCause.Shader).ToList() ?? [];
    public bool HasFrames => F != null;
    public string FramesVerdict => F is not { } f ? "" : Format.Frames(f, ShaderHitches, Unsplit);
    // a DirectX 11 session (the frames-only recorder's too) has no creates: its hitches aren't split by cause
    bool Unsplit => FramesOnly || F is { D3D11: true };
    public bool SplitFrames => !Unsplit;
    public string OtherLegend => Unsplit ? Loc.T("Detail_StutterLegend") : Loc.T("Graph_OtherHitch");
    public string GraphEnd => Format.Duration(F?.Duration);
    public string FramesSummary => F is not { } f ? ""
        : Loc.T("Detail_FramesSummary", Format.Duration(f.Startup), $"{InPlay(f).Count(h => h.Ms >= 50):N0}", $"{InPlay(f).Count(h => h.Ms >= 100):N0}");
    // the same list while the report is the same: a new one makes the slow-frames list rebuild every row at each refresh
    public IReadOnlyList<DetailRow> HitchRows => hitchRows.Report == F ? hitchRows.Rows
        : (hitchRows = (F, F?.Hitches.Select(h => new DetailRow($"{Clock(h.At)}  {(Unsplit && h.Cause == HitchCause.Other ? Loc.T("Detail_StutterLegend") : Cause(h.Cause))}", Ms(h.Ms))).ToList() ?? [])).Rows;
    (FrameReport? Report, IReadOnlyList<DetailRow> Rows) hitchRows = (null, []);
    public static string HitchTip(Hitch h, bool neutral = false) => $"{Clock(h.At)} · {Ms(h.Ms)} · {(neutral ? Loc.T("Detail_HitchLegend") : Cause(h.Cause))}";
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
    static string Cause(HitchCause c) => c switch { HitchCause.Shader => Loc.T("Graph_ShaderCompile"), HitchCause.Other => Loc.T("Graph_OtherHitch"), HitchCause.Quitting => Loc.T("Graph_Quitting"),
        HitchCause.LoadingShaders => Loc.T("Graph_LoadingCompiling"), _ => Loc.T("Graph_Loading") };

    /// <summary>The session bar's three segments, as star widths.</summary>
    public double LibraryWidth => L?.FromGameLibrary ?? 0;
    public double HitsWidth => L?.CacheHits ?? 0;
    public double CompilesWidth => L?.Compiles ?? 0;
    public double CompilesMinWidth => L?.Compiles > 0 ? 4 : 0;
    public string FromLibrary => Fmt.N(L?.FromGameLibrary);
    public string CacheHits => Fmt.N(L?.CacheHits);
    public string Compiles => Fmt.N(L?.Compiles);
    public string SessionLabel => L is { } l
        ? Loc.T("Detail_SessionLabel", $"{l.Requests:N0}", $"{l.FromGameLibrary:N0}", $"{l.CacheHits:N0}", $"{l.Compiles:N0}")
          + (l.StartupCompiles > 0 ? Loc.T("Detail_SessionStartup", $"{l.StartupCompiles:N0}") : "")
          + (l.RayQueryRecompiles > 0 ? Loc.T("Detail_SessionRayQuery", $"{l.RayQueryRecompiles:N0}") : "")
          + (l.StateObjectsReady + l.StateObjectsCompiled + l.StateObjectsStartupCompiled > 0 ? Loc.T("Detail_SessionStateObjects", $"{l.StateObjectsReady:N0}", $"{l.StateObjectsCompiled:N0}") : "")
          + (l.StateObjectsStartupCompiled > 0 ? Loc.T("Detail_SessionStartup", $"{l.StateObjectsStartupCompiled:N0}") : "")
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
    public string UseDefaultText => Loc.T("Detail_UseDefaultChoice", Loc.T(ScsKiller.RecordsByDefault(Host.Core.Settings, s) ? "Settings_OnSetting" : "Settings_OffSetting"));
    string RecorderDll => Fmt.RecorderDll(s);
    string Kept => ScsKiller.SessionKeptNote(s) is { } kept ? " " + kept : "";
    // another copy's recorder in the folder is said on every line the switch shows: a skip's line has no room for the reconcile's note
    public string RecordNote => RecordNoteText is var t && s.RecorderForeign && !t.Contains(Sentence(ScsKiller.ForeignNote)) ? $"{t} {ScsKiller.ForeignHereNote}" : t;
    string RecordNoteText => ScsKiller.CrashNote(s.Game) is { } crash ? crash
        : Risk
        ? (s.AlwaysRecord ? Loc.T("Detail_EveryLaunchRecorded") : Loc.T("Detail_EachSessionConfirmed")) + Kept
        : ShowOffline
        ? Loc.T("Detail_OfflineBlocksHint", ScsKiller.AntiCheatName(s)) + Kept
        : s.RiskNoLaunch ? ScsKiller.NoLaunchNote(Fmt.StoreName(s.Game))
        : !NoAntiCheat
        ? Host.Platform.RecordAntiCheatGames ? ScsKiller.RiskNotHere(s) ?? Loc.T("Detail_RecorderNotAvailableTamper", ScsKiller.AntiCheatName(s), RecorderDll) : NoAntiCheatSessions
        : PairFramesOnly ? Loc.T("Detail_RecorderFramesOnly")
        : s.RecorderSkip == ScsKiller.SkipManual ? Loc.T("Detail_RecorderConfirmFolder")
        : s.RecorderSkip == ScsKiller.SkipModNotChainable ? Loc.T("Detail_RecorderNotAvailable", ScsKiller.NotChainableReason(s.RecorderMod, RecorderDll))
        : s.RecorderSkip == ScsKiller.SkipAgsMod && s.AgsMod is { } agsMod && !(agsMod == "ReShade" && s.ShaderModLayer && s.ShaderModAsD3D12) ? Loc.T("Detail_RecorderNotAvailableTwo", ScsKiller.SkipAgsMod, ScsKiller.AgsModHint(agsMod))   // else ShaderModNote says it
        : s.RecorderSkip is { } skip ? Loc.T("Detail_RecorderNotAvailable", skip)
        : (s.Engine is { } vk && Core.Vulkan.VulkanPlan.IsVulkan(vk) ? ScsKiller.VulkanRecorderNote
            : FramesOnly ? Loc.T("Detail_RecorderAddsFramesOnly", Fmt.RecorderFiles(s))
            : Loc.T("Detail_RecorderAdds", Fmt.RecorderFiles(s)))
          + Loc.T("Detail_RecorderRemoveAnyTime")
          + (s.RecorderNote is { } note && note != s.RecorderFailure ? Loc.T("Detail_Paren", Sentence(note)) : "")
          + (s.RecorderRefused is { } why && !(s.Status == GameStatus.NeedsRecording && ScsKiller.NeverRecorded(s)) ? " " + why : "");   // else the status says it

    // A mod's d3d12.dll where the recorder goes (ReShade, a wrapper): off = the game isn't recorded; on = the recorder chains to it
    public bool HasMod => s.RecorderMod != null && s.RecorderSkip is not (ScsKiller.SkipAntiCheat or ScsKiller.SkipShaderMod or ScsKiller.SkipManual or ScsKiller.SkipUnsupported or ScsKiller.SkipNotDx12 or ScsKiller.SkipStreamline or ScsKiller.SkipCrashes);
    public string AlongsideTitle => Loc.T("Detail_AlongsideTitle", s.RecorderMod);
    public string AlongsideNote => Loc.T("Detail_AlongsideNote", s.RecorderMod);
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
        ? s.OfflineRunning ? Loc.T("Detail_SessionRunning") : Loc.T("Detail_RecordSessionRisk")
        : s.OfflineRunning ? Loc.T("Detail_OfflineRunning") : Loc.T("Detail_RecordOfflineRisk");
    public string OfflineRiskText => Risk ? RiskText(s) : OfflineRisk;
    public static readonly string OfflineRisk = Loc.T("Detail_OfflineRisk");
    public static string RiskText(GameState g) => Loc.T("Detail_RiskText", ScsKiller.AntiCheatName(g))
        + Loc.T("Detail_RiskTextBanned");
    public string? SessionNote => s.SessionNote;
    public bool HasSessionNote => s.SessionNote != null;
    public string? RecorderFailure => s.RecorderFailure is { } f ? Loc.T("Detail_RecorderFailure", f) : null;
    public bool HasRecorderFailure => s.RecorderFailure != null;

    public bool HasRecording => s.RecordingBytes > 0;
    public string RecordingSize => Loc.T("Detail_RecordingSize", Format.Bytes(s.RecordingBytes));
    public bool RecordingPaused => s.RecordingPaused;
    public string RecordingPausedText => ScsKiller.PausedNote(Host.Core.Settings) + Loc.T("Detail_RaiseLimitOrClear");
    public bool ClearingRecording { get; set; }
    public bool CanClearRecording => !ClearingRecording && !s.Playing;
    public string ClearRecordingTip => s.Playing ? Loc.T("Detail_CloseGameFirst") : Loc.T("Detail_ClearRecordingTip");

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
