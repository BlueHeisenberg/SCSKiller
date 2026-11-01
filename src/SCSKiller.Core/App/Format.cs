using SCSKiller.Core.Localization;
namespace SCSKiller.Core.App;

/// <summary>Text the app and the CLI show.</summary>
public static class Format
{
    /// <summary>The game page's line before a session with the recorder; null for a game whose recorder can't go in.</summary>
    public static string? SessionHint(GameState s) => s.RecorderSkip is ScsKiller.SkipUnsupported or ScsKiller.SkipVulkanSteamOnly ? null
        : Loc.T("Format_SessionHint");

    public const string Dash = "—";

    /// <summary>The Library cache card's line while the NVIDIA shader cache is turned off (<see cref="ScsKiller.CacheOff"/>); null otherwise.</summary>
    public static string? CacheOffLine(GpuVendor vendor, CacheLimit? limit) => ScsKiller.CacheOff(vendor, limit) ? Loc.T("Format_CacheOff") : null;

    /// <summary>The driver cache's warning (the Library tile's icon, the Cache page's line): <see cref="CacheOffLine"/>, the driver's
    /// default limit, or over 75% of the limit; null otherwise. <paramref name="used"/>: what the limit counts, the fullest game's
    /// when the limit is each game's own (<paramref name="perGame"/>).</summary>
    public static string? CacheWarning(GpuVendor vendor, CacheLimit? limit, long used, bool perGame) =>
        CacheOffLine(vendor, limit) ?? (limit?.Bytes is not { } max ? null
        : limit.IsDriverDefault ? Loc.T("Format_CacheDriverDefault")
        : max > 0 && used * 4 > max * 3 ? perGame ? Loc.T("Format_CacheNearlyFullGame") : Loc.T("Format_CacheNearlyFull") : null);

    /// <summary>The cache's line on the Library and the Cache page, from sizes the app already measured: the driver cache against
    /// its limit, AMD's DirectX 11 cache apart (<paramref name="capped"/>: its DirectX 12 one, which the limit counts) and the Windows
    /// shader cache (NVIDIA 621 and later), led by what they hold on disk together.</summary>
    public static string CacheSplit(CacheUsage usage, CacheLimit? limit, long? capped, long windows)
    {
        var driver = capped ?? usage.BytesOnDisk;
        var dx11 = capped is { } c ? Math.Max(0, usage.BytesOnDisk - c) : 0;
        List<string> parts = [Loc.F("Format_DriverCache", Bytes(driver)) + (limit is null ? Loc.T("Format_LimitUnknown") : limit.Bytes is { } l ? Loc.F("Format_OfItsLimit", Bytes(l)) : Loc.T("Format_NoLimit"))];
        if (dx11 > 0) parts.Add($"DirectX 11 {Bytes(dx11)}");
        if (windows > 0) parts.Add($"Windows {Bytes(windows)}");
        if (parts.Count > 1) parts.Insert(0, Loc.F("Format_OnDisk", Bytes(driver + dx11 + windows)));
        return string.Join(" · ", parts);
    }

    /// <summary>The tooltip of <see cref="CacheSplit"/>'s line when the driver's size is only an upper bound; null otherwise.</summary>
    public static string? CacheUpperNote(CacheUsage usage) => usage.UpperBound ? Loc.T("Format_CacheUpperNote") : null;

    /// <summary>Locales group digits with U+00A0 or U+202F, which UI fonts draw wide or as a fallback glyph: a plain space instead, on every thread.</summary>
    public static void PlainGroupSeparator()
    {
        var c = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.CurrentCulture.Clone();
        var n = c.NumberFormat;
        if (n.NumberGroupSeparator is not (" " or " ")) return;
        n.NumberGroupSeparator = n.CurrencyGroupSeparator = n.PercentGroupSeparator = " ";
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.CurrentCulture = c;
    }

    /// <summary>"3.2 GB" from 1 GiB up, else whole MB; a dash for null.</summary>
    public static string Bytes(long? b) => b switch
    {
        null => Dash,
        >= 1L << 30 => $"{b / (double)(1L << 30):0.#} GB",
        _ => $"{b / (double)(1 << 20):0} MB",
    };

    /// <summary>"64 GB on disk, 33 GB in use" while the files hold at least 1 GB and a tenth less than their size on disk
    /// (<see cref="InUseShown"/>), else <see cref="Bytes"/> of <paramref name="onDisk"/>.</summary>
    public static string OnDisk(long? onDisk, long? inUse) => InUseShown(onDisk, inUse) ? $"{Bytes(onDisk)} on disk, {Bytes(inUse)} in use" : Bytes(onDisk);

    /// <summary>NVIDIA's file lengths step up to twice what the files hold: worth showing from 1 GB and a tenth apart.</summary>
    public static bool InUseShown(long? onDisk, long? inUse) => onDisk is { } d && inUse is { } u && d - u >= 1L << 30 && u <= d - d / 10;

    /// <summary><see cref="ScsKiller.Duration"/>; a dash for null.</summary>
    public static string Duration(TimeSpan? t) => t is { } v ? ScsKiller.Duration(v) : Dash;

    public static string Ms(double ms) => ms >= 1000 ? $"{ms / 1000:0.0} s" : $"{ms:0} ms";

    // FrameLog's 1% low is 0 only when play is empty: the startup never found a quiet window
    public static bool NoPlay(double low1PctFps) => low1PctFps <= 0;
    const string NoPlayText = "loading or compiling shaders the whole time";

    /// <summary>"5 min: 3 stutters from shader compiles (worst 1.3 s), 2 other hitches, 1% low 35 fps. Loading: 4 slow
    /// frames compiling shaders (worst 9.4 s), 12 others." (compiling shaders: startup only). <paramref name="unsplit"/>: a
    /// DirectX 11 session has no creates, so "5 min: 3 stutters (worst 1.3 s), 1% low 35 fps. DirectX 11: ..."</summary>
    public static string Frames(FrameReport f, IReadOnlyList<Hitch> shader, bool unsplit = false)
    {
        int other = f.Hitches.Count(h => h.Cause == HitchCause.Other), loading = f.Hitches.Count(h => h.Cause == HitchCause.Loading);
        var compiling = f.Hitches.Where(h => h.Cause == HitchCause.LoadingShaders).ToList();
        return (NoPlay(f.Low1PctFps) ? $"{Duration(f.Duration)}, {NoPlayText}." : $"{Duration(f.Duration)}: " + (unsplit
                ? other == 0 ? "no stutters" : $"{other:N0} stutter{(other == 1 ? "" : "s")} (worst {Ms(f.Hitches.Where(h => h.Cause == HitchCause.Other).Max(h => h.Ms))})"
                : (shader.Count == 0 ? "no stutters from shader compiles"
                : $"{shader.Count:N0} stutter{(shader.Count == 1 ? "" : "s")} from shader compiles (worst {Ms(shader.Max(h => h.Ms))})")
            + (other == 0 ? ", no other hitches" : $", {other:N0} other hitch{(other == 1 ? "" : "es")}"))
            + $", 1% low {f.Low1PctFps:0} fps." + (unsplit ? $" {BeforeAfter.Dx11Label}." : ""))
            + (compiling.Count + loading == 0 ? "" : " Loading: " + string.Join(", ", new[]
            {
                compiling.Count == 0 ? null : $"{compiling.Count:N0} slow frame{(compiling.Count == 1 ? "" : "s")} compiling shaders (worst {Ms(compiling.Max(h => h.Ms))})",
                loading == 0 ? null : compiling.Count == 0 ? $"{loading:N0} slow frame{(loading == 1 ? "" : "s")}" : $"{loading:N0} other{(loading == 1 ? "" : "s")}",
            }.OfType<string>()) + ".");
    }

    /// <summary>An earlier launch in the frame history: "1% low 35 fps · 3 stutters". <paramref name="unsplit"/> or a DirectX 11
    /// launch: its stutters aren't counted (<see cref="FrameSession.Stutters"/> counts shader ones).</summary>
    public static string Session(FrameSession e, bool unsplit = false) => NoPlay(e.Low1PctFps) ? NoPlayText
        : $"1% low {e.Low1PctFps:0} fps" + (unsplit || e.D3D11 ? "" : " · " + (e.Stutters == 0 ? "no stutters" : $"{e.Stutters:N0} stutter{(e.Stutters == 1 ? "" : "s")}"));

    /// <summary>"Carved DXBC", "Unity 2022.3.10"; just the family (or <paramref name="family"/>) when the reader found no
    /// version: Carved and FromSoft say "-", Unity "?".</summary>
    public static string Engine(EngineInfo e, string? family = null) =>
        e.Version is "" or "-" or "?" ? family ?? e.Family : $"{family ?? e.Family} {e.Version}";

    public static string MergingStage => Loc.T("Format_Stage_Merging");
    public static string WritingStage => Loc.T("Format_Stage_Writing");
    public static string SavingStage => Loc.T("Format_Stage_Saving");
    public static string FinishingStage => Loc.T("Format_Stage_Finishing");

    /// <summary>The item is being compiled: any stage before it finishes, paused included.</summary>
    public static bool Running(QueueItem q) => q.Stage is QueueStage.Indexing or QueueStage.Planning or QueueStage.Materializing
        or QueueStage.Warming or QueueStage.Merging or QueueStage.Saving or QueueStage.Finishing or QueueStage.Paused;

    /// <summary>A running item's progress report: only its count changed, not its stage (a warm's, a fold's, a plan step's).</summary>
    public static bool ProgressTick(QueueItem q) => q.Stage is QueueStage.Warming or QueueStage.Merging or QueueStage.Saving or QueueStage.Finishing
        || q is { Stage: QueueStage.Planning or QueueStage.Materializing, Progress.Note: not null };

    /// <summary>The notification area's status: a line, and the compile's share done (null = no progress to show).
    /// <paramref name="running"/>: the queue's running item (<see cref="Running"/>), <paramref name="name"/> its game's.</summary>
    public static (string Line, double? Done) TrayStatus(QueueItem? running, string? name, bool quitting)
    {
        double? done = running is { Progress: { Total: > 0 } p } ? Math.Clamp((double)p.Done / p.Total, 0, 1) : null;
        return quitting ? (Loc.T("Format_Tray_Saving"), null)
            : running == null ? (Loc.T("Format_Tray_Idle"), null)
            : running.PlanCheck ? (Loc.T("Format_Tray_Checking"), null)
            : running.Stage == QueueStage.Paused ? (Loc.F("Format_Tray_Paused", name ?? ""), done)
            : running.Stage == QueueStage.Warming && done is { } d ? (Loc.F("Format_Tray_Compiling", name ?? "", $"{100 * d:0}%"), d)
            : running is { Stage: QueueStage.Merging, Note: { } writing } ? ($"{writing}: {name}", null)
            : running.Stage == QueueStage.Merging ? ($"{MergingStage}: {name}" + (done is { } m ? $", {100 * m:0}%" : ""), done)
            : running.Stage == QueueStage.Saving ? ($"{SavingStage}: {name}", null)
            : running.Stage == QueueStage.Finishing ? ($"{FinishingStage}: {name}", null)
            : (Loc.F("Format_Tray_Preparing", name ?? ""), null);
    }

    /// <summary>"FSR4: 80 known pipelines, compiled with the game", "DLSS: compiled by the NVIDIA driver itself",
    /// "XeSS: detected; added after a recording sees them". <paramref name="nvidia"/>: this PC's GPU, the only one DLSS runs on.</summary>
    public static string Middleware(MiddlewareTag t, bool nvidia = true) => t.Label + (t.Pipelines > 0 ? $": {t.Pipelines:N0} known pipelines, compiled with the game"
        : t.Label == "DLSS" ? nvidia ? ": compiled by the NVIDIA driver itself" : ": runs only on NVIDIA" : ": detected, added after a recording sees them");

    /// <summary>The Library's notice that this GPU can't compile, or on Intel that its support is experimental
    /// (<paramref name="intel"/>: the Intel backend is in use); null on NVIDIA and AMD, or once closed for this GPU
    /// (<paramref name="dismissedFor"/>: <see cref="Settings.GpuNoticeDismissed"/>). <paramref name="why"/>: the backend's own
    /// reason (<see cref="Vendors.UnsupportedVendor.Why"/>), on any vendor.</summary>
    public static string? GpuNotice(GpuInfo gpu, string? dismissedFor, bool intel = false, string? why = null) => dismissedFor == gpu.Name ? null
        : why ?? (gpu.Vendor is GpuVendor.Nvidia or GpuVendor.Amd ? null : gpu.Vendor switch
        {
            GpuVendor.Intel when intel => "Intel support is experimental. DirectX 12 games need a recording first.",
            GpuVendor.Intel => "Intel support is experimental and off. Turn it on in Settings.",
            GpuVendor.Qualcomm => "SCSKiller doesn't compile on Qualcomm GPUs yet: how Qualcomm's driver caches shaders hasn't been measured.",
            _ => "SCSKiller doesn't compile on this GPU yet: it only knows how NVIDIA and AMD drivers cache shaders.",
        });

    /// <summary>A Library row's note: a few words for the status's reason (the whole one is in the row's tooltip and on the
    /// game's page); null when the status title says it. Read from the core's reasons (ScsKiller.Evaluate, Planner.Check,
    /// the engine readers); an unknown one shows its first clause.</summary>
    public static string? ShortNote(GameState s)
    {
        var r = s.StatusReason;
        bool Has(string part) => r.Contains(part, StringComparison.OrdinalIgnoreCase);   // a joined clause starts uppercase
        bool Starts(string part) => r.StartsWith(part, StringComparison.Ordinal);
        var partly = ScsKiller.IsPartlyWarmed(s);
        return s.Status switch
        {
            _ when s.CompileUnreached => Loc.T("Format_ShortNote_CustomLoader"),
            GameStatus.Warmed when r is ScsKiller.OverCacheLimitReason or ScsKiller.OverGameCapReason or ScsKiller.OverCompileFolderReason => Loc.T("Format_ShortNote_TooBig"),
            GameStatus.Warmed when r == ScsKiller.CacheFullReason => Loc.T("Format_ShortNote_CacheFull"),
            _ when ScsKiller.NotReached(r) => Loc.T("Format_ShortNote_NotInGameCache"),
            _ when ScsKiller.LinkedAway(r) => Sentence(r),
            GameStatus.Warmed when ScsKiller.IsPartlyCompiled(s) => Sentence(ScsKiller.PartlyCompiledNote(s)),
            GameStatus.Warmed when partly => $"Driver {s.WarmedDriverVersion} · {s.Careful!.LaunchCompiled * 100:0}% still compiled",
            GameStatus.Warmed when ScsKiller.RtAfterRecording(s) && s.NoStutter == null => (s.RecorderInstalled ? Loc.T("Format_ShortNote_RecorderRt")
                : ScsKiller.RecordedEnough(s) ? Loc.T("Format_ShortNote_RtNeedsRecording") : Loc.T("Format_ShortNote_RtNeeds5Min")) + KeyNote(s),
            GameStatus.Warmed when ScsKiller.BothApis(s.Plan) => Loc.T("Format_ShortNote_Dx11And12") + (ScsKiller.IsPartial(s.Plan) ? Loc.T("Format_ShortNote_PartlyCovered") : "") + KeyNote(s),
            GameStatus.Warmed => $"Driver {s.WarmedDriverVersion}" + (ScsKiller.IsPartial(s.Plan) ? Loc.T("Format_ShortNote_PartlyCovered") : "") + KeyNote(s),
            GameStatus.Stale => StaleNote(r),
            GameStatus.NeedsRecording when r == ScsKiller.OfflineSessionNote => r,
            GameStatus.NeedsRecording when r is Vulkan.VulkanPlan.StartOnce or ScsKiller.LauncherClearsReason => null,   // the title says it
            GameStatus.NeedsRecording when s.NoStutter != null && !s.RecorderInstalled => Loc.T("Format_ShortNote_CompilingNotNeeded"),
            GameStatus.NeedsRecording when s.RecordingPaused => Loc.T("Format_ShortNote_RecordingPaused"),
            GameStatus.NeedsRecording when ScsKiller.NeverRecorded(s) => null,   // the title: ScsKiller.NothingRecordedTitle
            GameStatus.NeedsRecording when s.RecorderInstalled && !ScsKiller.RecordedEnough(s) => Has(Planning.Planner.Dx12Only) ? Loc.T("Format_ShortNote_RecorderDx12") : Loc.T("Format_ShortNote_Recorder5Min"),
            GameStatus.NeedsRecording when Starts(ScsKiller.RtNeedsRecording) => Loc.T("Format_ShortNote_ForRt") + (s.InCommunityDb == true ? " · " + InDb(r) : ""),
            GameStatus.NeedsRecording when s.InCommunityDb == true => Sentence(InDb(r)),
            GameStatus.NeedsRecording when Starts("the recording has no draws") => Loc.T("Format_ShortNote_PlayIntoWorld"),
            GameStatus.NeedsRecording when s.RecorderInstalled => Sentence(FirstClause(r)),
            GameStatus.NeedsRecording when s.RecorderEffective && Games.Collections.RecordedBy(s.Game.Id) != null => Loc.T("Format_ShortNote_Recorder5Min"),   // its collection's first game's
            GameStatus.NeedsRecording when s.RiskEligible => Loc.T("Format_ShortNote_RecordSession"),
            GameStatus.NeedsRecording when s.RecorderSkip != null => Loc.T("Format_ShortNote_RecorderNotAvailable"),
            GameStatus.NeedsRecording => Loc.T("Format_ShortNote_TurnOnRecord"),
            GameStatus.Ready when Has("ray-traced effects aren't compiled") => !s.AnyAntiCheat || s.RiskEligible ? Loc.T("Format_ShortNote_RtNotCompiled") : Loc.F("Format_ShortNote_RtBlockedByAntiCheat", ScsKiller.AntiCheatName(s)),
            GameStatus.Ready when Has(ScsKiller.RtUnseenNote) => Loc.T("Format_ShortNote_NoRtSeen"),
            GameStatus.Ready when Has(ScsKiller.RtInlineNote) => Loc.T("Format_ShortNote_PathTracingNeedsRecording"),
            GameStatus.Ready when s.Engine?.KeyMissing == true => Sentence(ScsKiller.KeyForFullCoverage),
            GameStatus.Ready when ScsKiller.IsPartial(s.Plan) => Loc.T("Format_ShortNote_PartlyCoveredAlone"),
            GameStatus.Ready when Has(Planning.Planner.UntestedNote) => Loc.T("Format_ShortNote_UntestedEngine"),
            GameStatus.Ready when Has("for DirectX 12, ") => !s.AnyAntiCheat || s.RiskEligible ? Loc.T("Format_ShortNote_Dx11Dx12NeedsRecording") : Loc.F("Format_ShortNote_Dx11Dx12BlockedByAntiCheat", ScsKiller.AntiCheatName(s)),
            GameStatus.Ready when Has("also compiles every DirectX 11 shader") => Loc.T("Format_ShortNote_Dx11And12"),
            GameStatus.Ready when Starts("compiles every DirectX 11 shader") => "DirectX 11",
            GameStatus.Ready => null,
            _ when r == Games.BootPrecompiles.Reason => Loc.T("Format_ShortNote_MainMenuShaders"),
            _ when s.ShaderModBlocks => Loc.F("Format_ShaderModChangesPipelines", s.ShaderMod),
            _ when s.AnyAntiCheat && Starts("needs a recording, which") => Loc.F("Format_BlockedByAntiCheat", ScsKiller.AntiCheatName(s)) + (s.InCommunityDb == true ? " · " + InDb(r) : ""),
            _ when Has(ScsKiller.ManualNoRecording) => Loc.T("Format_ShortNote_NeedsFolderConfirm"),
            _ when s.Engine?.Encrypted == true => null,   // the title: "Encrypted game files"
            _ when r is Codecs.NoOodleDll or Codecs.NoNativeOodle => r,
            _ when r.EndsWith(Planning.Planner.VulkanNotOnGpu, StringComparison.Ordinal) => Loc.T("Format_ShortNote_VulkanNotSupported"),
            _ when s.Engine?.Unsupported is { } u => u.StartsWith("no D3D shaders", StringComparison.Ordinal) ? Loc.T("Format_ShortNote_NoDxShaders") : Loc.T("Format_ShortNote_EngineNotSupported"),
            _ when r == ScsKiller.CheckingReason => null,   // the title: "Checking…"
            _ when r == ScsKiller.NotCheckedReason => Loc.T("Format_ShortNote_OpenToCheck"),
            _ when s.Engine == null => r == "engine not supported yet" ? Loc.T("Format_ShortNote_EngineNotSupported") : Loc.T("Format_ShortNote_CouldntReadFiles"),
            _ when Starts("runs on ") => Loc.T("Format_ShortNote_RunsOn") + FirstClause(r["runs on ".Length..]),
            _ when Starts("not supported on this GPU") => Loc.T("Format_ShortNote_GpuNotSupported"),
            _ => Sentence(FirstClause(r)),
        };
    }

    /// <summary>A shader mod that doesn't block the game, on its own line after its reason: "RenoDX changes this game's
    /// pipelines: ...". None for an unsupported game: its status and reason alone.</summary>
    public static string ModNote(GameState s) =>
        (s is { ShaderMod: not null, ShaderModBlocks: false } && s.Status != GameStatus.Unsupported ? "\n" + ScsKiller.ShaderModNote(s) : "")
        + (s.Dxvk && s.Status != GameStatus.Unsupported ? "\n" + (s.DxvkCompiled ? ScsKiller.DxvkCompiledNote : ScsKiller.DxvkNote) : "")
        + (s.MfgUnlocker ? "\n" + ScsKiller.MfgUnlockerNote : "")
        + (s.StarfieldLuma && s.Status != GameStatus.Unsupported ? "\n" + ScsKiller.StarfieldLumaNote : "");

    /// <summary>Recorder in, not long enough yet: the DirectX 12 hint stays, a DirectX 11 run never loads the recorder.</summary>
    public static string RecorderOnNote(GameState s) => "recorder on: play for about 5 minutes"
        + (s.StatusReason.Contains(Planning.Planner.Dx12Only, StringComparison.OrdinalIgnoreCase) ? ", " + Planning.Planner.Dx12Only : "");

    static string StaleNote(string r) =>
        r.StartsWith("driver changed:", StringComparison.Ordinal) ? $"Driver {r[(r.LastIndexOf(' ') + 1)..]} cleared its cache"
        : r == ScsKiller.TrimmedPartReason ? Loc.T("Format_Stale_TrimmedPart")
        : r == ScsKiller.TrimmedAllReason ? Loc.T("Format_Stale_TrimmedAll")
        : r.StartsWith("the compile didn't reach", StringComparison.Ordinal) ? Loc.T("Format_Stale_MissedCache")
        : r.StartsWith("the game runs as", StringComparison.Ordinal) ? Loc.T("Format_Stale_ExeCaseChanged")
        : r.StartsWith("the last compile filled the cache of", StringComparison.Ordinal) ? Loc.T("Format_Stale_AnotherExe")
        : r.StartsWith("game updated", StringComparison.Ordinal) ? Loc.T("Format_Stale_GameUpdated")
        : r.StartsWith("game shaders changed", StringComparison.Ordinal) ? Loc.T("Format_Stale_ShadersChanged")
        : r.StartsWith("SCSKiller can now compile", StringComparison.Ordinal) ? Loc.T("Format_Stale_CanCompileMore")
        : r.StartsWith("Maximum mode", StringComparison.Ordinal) ? Loc.T("Format_Stale_MaxModeMore")
        : r.Contains(". " + ScsKiller.RedoesNote, StringComparison.Ordinal) ? r
        : Sentence(FirstClause(r));   // "N new pipelines recorded"

    // a joined clause starts uppercase (Then), so ". " before a lowercase letter ("e.g. x") isn't a clause end
    static readonly System.Text.RegularExpressions.Regex ClauseEnd = new(@": | \(|\. (?!\p{Ll})");
    static string FirstClause(string t) => ClauseEnd.Split(t, 2)[0];
    public static string Sentence(string t) => t.Length > 0 ? char.ToUpperInvariant(t[0]) + t[1..] : t;
    /// <summary>A status clause and the next one as two sentences; <paramref name="first"/> alone without a next.</summary>
    /// <summary>" · needs the game's AES key for full coverage" for a game whose files are partly encrypted, else "".</summary>
    public static string KeyNote(GameState s) => s.Engine?.KeyMissing == true ? " · " + ScsKiller.KeyForFullCoverage : "";

    public static string Then(string first, string? next) => string.IsNullOrEmpty(next) ? first : first + (first.EndsWith('.') ? " " : ". ") + Sentence(next);
    static string InDb(string reason) => reason.Contains(ScsKiller.InDbSupportersNote, StringComparison.OrdinalIgnoreCase) ? ScsKiller.InDbSupportersNote : ScsKiller.InDbNote;

    static readonly System.Text.RegularExpressions.Regex RawShadersRegex =
        new(@"no raw DXBC/DXIL shaders in its files \(([\d,\.]+) GB sampled\): shaders are compressed or packed: needs an engine reader", System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex ComputeShadersRegex =
        new(@"only (\d+) raw compute shaders: graphics shaders are compressed or packed: needs an engine reader", System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex GraphicsShadersRegex =
        new(@"only (\d+) raw graphics shaders: the rest are compressed or packed: needs an engine reader", System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex NeedsRecordingRegex =
        new(@"needs a recording,\s*which\s+(.+?)\s+blocks(?:\.\s*(.+))?$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Translates an engine/core reason string into the user's localized language for UI presentation.</summary>
    public static string TranslateReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "";
        var r = reason.Trim();
        if (RawShadersRegex.Match(r) is { Success: true } mRaw)
            return Loc.F("Reason_NoRawShadersPacked", mRaw.Groups[1].Value);
        if (ComputeShadersRegex.Match(r) is { Success: true } mComp)
            return Loc.F("Reason_OnlyComputeShaders", mComp.Groups[1].Value);
        if (GraphicsShadersRegex.Match(r) is { Success: true } mGfx)
            return Loc.F("Reason_OnlyGraphicsShaders", mGfx.Groups[1].Value);
        if (NeedsRecordingRegex.Match(r) is { Success: true } mRec)
        {
            var antiCheat = mRec.Groups[1].Value.Trim();
            var dbPart = mRec.Groups[2].Success ? mRec.Groups[2].Value.Trim() : null;
            var baseText = Loc.F("Reason_NeedsRecordingAntiCheat", antiCheat);
            if (string.IsNullOrEmpty(dbPart)) return baseText;
            var translatedDb = dbPart switch
            {
                _ when dbPart.Equals("not in the community database yet", StringComparison.OrdinalIgnoreCase) => Loc.T("Reason_NotInCommunityDbYet"),
                _ when dbPart.Equals("in the community database for patreon supporters", StringComparison.OrdinalIgnoreCase) => Loc.T("Reason_InCommunityDbSupporters"),
                _ when dbPart.Equals("in the community database", StringComparison.OrdinalIgnoreCase) => Loc.T("Reason_InCommunityDb"),
                _ => dbPart
            };
            return baseText + ". " + Sentence(translatedDb);
        }

        return r switch
        {
            _ when r == Carved.CarvedReader.GodotReason => Loc.T("Reason_Godot"),
            _ when r == Carved.CarvedReader.Dx9Reason => Loc.T("Reason_DirectX9"),
            _ when r == Carved.CarvedReader.RuntimeShadersReason => Loc.T("Reason_RuntimeShaders"),
            _ when r == ScsKiller.CantReachReason => Loc.T("Reason_CantReach"),
            _ when r == ScsKiller.ThirtyTwoBitReason => Loc.T("Reason_ThirtyTwoBit"),
            _ when r == ScsKiller.UnreachedReason => Loc.T("Reason_Unreached"),
            _ when r == ScsKiller.NoShadersReason || r == ScsKiller.NothingToCompileReason => Loc.T("Reason_NoShaders"),
            _ when r == ScsKiller.CacheOffReason => Loc.T("Reason_CacheOff"),
            _ when r == ScsKiller.OverCacheLimitReason => Loc.T("Reason_OverCacheLimit"),
            _ when r == ScsKiller.OverGameCapReason => Loc.T("Reason_OverGameCap"),
            _ when r == ScsKiller.OverCompileFolderReason => Loc.T("Reason_OverCompileFolder"),
            _ when r == ScsKiller.CacheFullReason => Loc.T("Reason_CacheFull"),
            _ when r == ScsKiller.TimedOutReason => Loc.T("Reason_TimedOut"),
            _ when r == ScsKiller.CheckingReason => Loc.T("Reason_Checking"),
            _ when r == ScsKiller.NotCheckedReason => Loc.T("Reason_NotChecked"),
            _ when r == ScsKiller.RtToCompileReason => Loc.T("Reason_RtToCompile"),
            _ when r == ScsKiller.LauncherClearsReason => Loc.T("Reason_LauncherClears"),
            _ when r == ScsKiller.PlanReplacedReason => Loc.T("Reason_PlanReplaced"),
            _ when r == ScsKiller.PlanCheckFailedReason => Loc.T("Reason_PlanCheckFailed"),
            _ when r == ScsKiller.MissesGameReason => Loc.T("Reason_MissesGame"),
            _ when r == ScsKiller.TrimmedPartReason => Loc.T("Reason_TrimmedPart"),
            _ when r == ScsKiller.PlanGrewReason => Loc.T("Reason_PlanGrew"),
            _ when r == ScsKiller.AftermathRetryReason => Loc.T("Reason_AftermathRetry"),
            _ when r == ScsKiller.AftermathLostReason => Loc.T("Reason_AftermathLost"),
            _ when r == ScsKiller.D3DSCachePendingReason => Loc.T("Reason_D3DSCachePending"),
            _ when r == ScsKiller.D3DSCacheDamagedReason => Loc.T("Reason_D3DSCacheDamaged"),
            _ when r == Carved.CarvedReader.GodotOpenGl => Loc.T("Reason_GodotOpenGl"),
            _ when r == Games.BootPrecompiles.Reason => Loc.T("Reason_BootPrecompiles"),
            _ when r == Games.SteamShortcutSource.NotReadyReason => Loc.T("Reason_ProtonNotReady"),
            _ when r == Games.SteamShortcutSource.NoProton => Loc.T("Reason_NoProton"),
            _ when r == Planning.Planner.Dx9AmdReason => Loc.T("Reason_Dx9Amd"),
            "engine not supported yet" => Loc.T("Format_ShortNote_EngineNotSupported"),
            "runs on OpenGL" => Loc.T("Reason_RunsOnOpenGL"),
            "runs on Vulkan" => Loc.T("Reason_RunsOnVulkan"),
            "runs on DirectX 9" => Loc.T("Reason_RunsOnDirectX9"),
            "runs on DirectX 10" => Loc.T("Reason_RunsOnDirectX10"),
            _ when r.StartsWith("not supported on this GPU", StringComparison.Ordinal) => Loc.T("Reason_NotSupportedGpu"),
            _ => r
        };
    }

    /// <summary>Translates Before and after blocked note into the user's localized language.</summary>
    public static string TranslateBaBlocked(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return "";
        var n = note.Trim();
        return n switch
        {
            "Not for games with anti-cheat" => Loc.T("Ba_Blocked_AntiCheat"),
            "Before and after recording is off in Settings" => Loc.T("Ba_Blocked_SettingsOff"),
            "Only for DirectX 12 and DirectX 11 games" => Loc.T("Ba_Blocked_ApiOnly"),
            "Not for games without shader compilation stutter" => Loc.T("Ba_Blocked_NoStutter"),
            "Turn the recorder on for this game first" => Loc.T("Ba_Blocked_TurnRecorderOnFirst"),
            "A before and after recording is already in progress" => Loc.T("Ba_Blocked_AlreadyInProgress"),
            "Wait for the compile to finish" => Loc.T("Ba_Blocked_WaitForCompile"),
            _ when n.EndsWith("is running", StringComparison.OrdinalIgnoreCase) => Loc.F("Ba_Blocked_GameIsRunning", n[..^10].Trim()),
            _ when n.StartsWith("The recorder can't run in this game:", StringComparison.OrdinalIgnoreCase) => Loc.F("Ba_Blocked_RecorderCantRun", n[36..].Trim()),
            _ => TranslateReason(n)
        };
    }
}
