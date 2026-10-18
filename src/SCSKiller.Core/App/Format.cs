namespace SCSKiller.Core.App;

/// <summary>Text the app and the CLI show.</summary>
public static class Format
{
    public const string Dash = "—";

    /// <summary>"3.2 GB" from 1 GiB up, else whole MB; a dash for null.</summary>
    public static string Bytes(long? b) => b switch
    {
        null => Dash,
        >= 1L << 30 => $"{b / (double)(1L << 30):0.#} GB",
        _ => $"{b / (double)(1 << 20):0} MB",
    };

    /// <summary><see cref="ScsKiller.Duration"/>; a dash for null.</summary>
    public static string Duration(TimeSpan? t) => t is { } v ? ScsKiller.Duration(v) : Dash;

    public static string Ms(double ms) => ms >= 1000 ? $"{ms / 1000:0.0} s" : $"{ms:0} ms";

    // FrameLog's 1% low is 0 only when play is empty: the startup never found a quiet window
    public static bool NoPlay(double low1PctFps) => low1PctFps <= 0;
    const string NoPlayText = "loading or compiling shaders the whole time";

    /// <summary>"5 min: 3 stutters from shader compiles (worst 1.3 s), 2 other hitches, 1% low 35 fps. Loading: 4 slow
    /// frames compiling shaders (worst 9.4 s), 12 others." (compiling shaders: startup only)</summary>
    public static string Frames(FrameReport f, IReadOnlyList<Hitch> shader)
    {
        int other = f.Hitches.Count(h => h.Cause == HitchCause.Other), loading = f.Hitches.Count(h => h.Cause == HitchCause.Loading);
        var compiling = f.Hitches.Where(h => h.Cause == HitchCause.LoadingShaders).ToList();
        return (NoPlay(f.Low1PctFps) ? $"{Duration(f.Duration)}, {NoPlayText}." : $"{Duration(f.Duration)}: " + (shader.Count == 0 ? "no stutters from shader compiles"
                : $"{shader.Count:N0} stutter{(shader.Count == 1 ? "" : "s")} from shader compiles (worst {Ms(shader.Max(h => h.Ms))})")
            + (other == 0 ? ", no other hitches" : $", {other:N0} other hitch{(other == 1 ? "" : "es")}")
            + $", 1% low {f.Low1PctFps:0} fps.")
            + (compiling.Count + loading == 0 ? "" : " Loading: " + string.Join(", ", new[]
            {
                compiling.Count == 0 ? null : $"{compiling.Count:N0} slow frame{(compiling.Count == 1 ? "" : "s")} compiling shaders (worst {Ms(compiling.Max(h => h.Ms))})",
                loading == 0 ? null : compiling.Count == 0 ? $"{loading:N0} slow frame{(loading == 1 ? "" : "s")}" : $"{loading:N0} other{(loading == 1 ? "" : "s")}",
            }.OfType<string>()) + ".");
    }

    /// <summary>An earlier launch in the frame history: "1% low 35 fps · 3 stutters".</summary>
    public static string Session(FrameSession e) => NoPlay(e.Low1PctFps) ? NoPlayText
        : $"1% low {e.Low1PctFps:0} fps · " + (e.Stutters == 0 ? "no stutters" : $"{e.Stutters:N0} stutter{(e.Stutters == 1 ? "" : "s")}");

    /// <summary>"Carved DXBC", "Unity 2022.3.10"; just the family (or <paramref name="family"/>) when the reader found no
    /// version: Carved and FromSoft say "-", Unity "?".</summary>
    public static string Engine(EngineInfo e, string? family = null) =>
        e.Version is "" or "-" or "?" ? family ?? e.Family : $"{family ?? e.Family} {e.Version}";

    /// <summary>The item is being compiled: any stage before it finishes, paused included.</summary>
    public static bool Running(QueueItem q) => q.Stage is QueueStage.Indexing or QueueStage.Planning or QueueStage.Materializing
        or QueueStage.Warming or QueueStage.Paused;

    /// <summary>The notification area's status: a line, and the compile's share done (null = no progress to show).
    /// <paramref name="running"/>: the queue's running item (<see cref="Running"/>), <paramref name="name"/> its game's.</summary>
    public static (string Line, double? Done) TrayStatus(QueueItem? running, string? name, bool quitting)
    {
        double? done = running is { Progress: { Total: > 0 } p } ? Math.Clamp((double)p.Done / p.Total, 0, 1) : null;
        return quitting ? ("Finishing: the driver is saving the shader cache", null)
            : running == null ? ("Idle", null)
            : running.PlanCheck ? ("Checking games for more to compile", null)
            : running.Stage == QueueStage.Paused ? ($"Paused: {name}", done)
            : running.Stage == QueueStage.Warming && done is { } d ? ($"Compiling {name}, {100 * d:0}%", d)
            : ($"Preparing {name}", null);
    }

    /// <summary>"FSR4: 80 known pipelines, compiled with the game", "DLSS: compiled by the NVIDIA driver itself",
    /// "XeSS: detected; added after a recording sees them".</summary>
    public static string Middleware(MiddlewareTag t) => t.Label + (t.Pipelines > 0 ? $": {t.Pipelines:N0} known pipelines, compiled with the game"
        : t.Label == "DLSS" ? ": compiled by the NVIDIA driver itself" : ": detected, added after a recording sees them");

    /// <summary>The Library's notice that this GPU can't compile, or on Intel that its support is experimental
    /// (<paramref name="intel"/>: the Intel backend is in use); null on NVIDIA and AMD, or once closed for this GPU
    /// (<paramref name="dismissedFor"/>: <see cref="Settings.GpuNoticeDismissed"/>).</summary>
    public static string? GpuNotice(GpuInfo gpu, string? dismissedFor, bool intel = false) => gpu.Vendor is GpuVendor.Nvidia or GpuVendor.Amd || dismissedFor == gpu.Name ? null
        : gpu.Vendor switch
        {
            GpuVendor.Intel when intel => "Intel support is experimental. DirectX 12 games need a recording first.",
            GpuVendor.Intel => "Intel support is experimental and off. Turn it on in Settings.",
            GpuVendor.Qualcomm => "SCSKiller doesn't compile on Qualcomm GPUs yet: how Qualcomm's driver caches shaders hasn't been measured.",
            _ => "SCSKiller doesn't compile on this GPU yet: it only knows how NVIDIA and AMD drivers cache shaders.",
        };

    /// <summary>A Library row's note: a few words for the status's reason (the whole one is in the row's tooltip and on the
    /// game's page); null when the status title says it. Read from the core's reasons (ScsKiller.Evaluate, Planner.Check,
    /// the engine readers); an unknown one shows its first clause.</summary>
    public static string? ShortNote(GameState s)
    {
        var r = s.StatusReason;
        bool Has(string part) => r.Contains(part, StringComparison.Ordinal);
        bool Starts(string part) => r.StartsWith(part, StringComparison.Ordinal);
        var partly = ScsKiller.IsPartlyWarmed(s);
        return s.Status switch
        {
            _ when s.CompileUnreached => "This game needs a custom loader",
            GameStatus.Warmed when r == ScsKiller.OverCacheLimitReason => "Too big for the shader cache",
            GameStatus.Warmed when r == ScsKiller.CacheFullReason => "Shader cache full",
            GameStatus.Warmed when ScsKiller.IsPartlyCompiled(s) => Sentence(ScsKiller.PartlyCompiledNote(s)),
            GameStatus.Warmed when partly => $"Driver {s.WarmedDriverVersion} · {s.Careful!.LaunchCompiled * 100:0}% still compiled",
            GameStatus.Warmed when ScsKiller.RtAfterRecording(s) && s.NoStutter == null => s.RecorderInstalled ? "Recorder on: play with ray tracing"
                : ScsKiller.RecordedEnough(s) ? "Ray tracing needs a recording" : "Ray tracing needs a 5-min recording",
            GameStatus.Warmed when ScsKiller.BothApis(s.Plan) => "DirectX 11 and 12" + (ScsKiller.IsPartial(s.Plan) ? " · partly covered" : ""),
            GameStatus.Warmed => $"Driver {s.WarmedDriverVersion}" + (ScsKiller.IsPartial(s.Plan) ? " · partly covered" : ""),
            GameStatus.Stale => StaleNote(r),
            GameStatus.NeedsRecording when r == ScsKiller.OfflineSessionNote => r,
            GameStatus.NeedsRecording when s.NoStutter != null && !s.RecorderInstalled => "Compiling isn't needed",
            GameStatus.NeedsRecording when s.RecordingPaused => "Recording paused: limit reached",
            GameStatus.NeedsRecording when ScsKiller.NeverRecorded(s) => null,   // the title: ScsKiller.NothingRecordedTitle
            GameStatus.NeedsRecording when s.RecorderInstalled && !ScsKiller.RecordedEnough(s) => Has(Planning.Planner.Dx12Only) ? "Recorder on: play on DirectX 12" : "Recorder on: play 5 minutes",
            GameStatus.NeedsRecording when Starts(ScsKiller.RtNeedsRecording) => "For ray-traced effects" + (s.InCommunityDb == true ? " · " + InDb(r) : ""),
            GameStatus.NeedsRecording when s.InCommunityDb == true => Sentence(InDb(r)),
            GameStatus.NeedsRecording when Starts("the recording has no draws") => "Play into the game world",
            GameStatus.NeedsRecording when s.RecorderInstalled => Sentence(FirstClause(r)),
            GameStatus.NeedsRecording when s.RecorderEffective && Games.Collections.RecordedBy(s.Game.Id) != null => "Recorder on: play 5 minutes",   // its collection's first game's
            GameStatus.NeedsRecording when s.RiskEligible => "Record a session on its page",
            GameStatus.NeedsRecording when s.RecorderSkip != null => "Recorder not available",
            GameStatus.NeedsRecording => "Turn on Record and play",
            GameStatus.Ready when Has("ray-traced effects aren't compiled") => !s.AnyAntiCheat || s.RiskEligible ? "Ray tracing not compiled" : $"Ray tracing blocked by {ScsKiller.AntiCheatName(s)}",
            GameStatus.Ready when Has(ScsKiller.RtUnseenNote) => "No ray tracing seen while recording",
            GameStatus.Ready when Has(ScsKiller.RtInlineNote) => "Path tracing needs a recording",
            GameStatus.Ready when ScsKiller.IsPartial(s.Plan) => "Partly covered",
            GameStatus.Ready when Has(Planning.Planner.UntestedNote) => "Not tested on this engine version",
            GameStatus.Ready when Has("for DirectX 12, ") => !s.AnyAntiCheat || s.RiskEligible ? "DirectX 11, DirectX 12 needs a recording" : $"DirectX 11, DirectX 12 blocked by {ScsKiller.AntiCheatName(s)}",
            GameStatus.Ready when Has("also compiles every DirectX 11 shader") => "DirectX 11 and 12",
            GameStatus.Ready when Starts("compiles every DirectX 11 shader") => "DirectX 11",
            GameStatus.Ready => null,
            _ when r == Games.BootPrecompiles.Reason => "Builds its shaders at the main menu",
            _ when s.ShaderModBlocks => $"{s.ShaderMod} changes all its pipelines",
            _ when s.AnyAntiCheat && Starts("needs a recording, which") => $"Blocked by {ScsKiller.AntiCheatName(s)}" + (s.InCommunityDb == true ? " · " + InDb(r) : ""),
            _ when Has(ScsKiller.ManualNoRecording) => "Needs a recording: confirm its folder",
            _ when s.Engine?.Encrypted == true => null,   // the title: "Encrypted game files"
            _ when r is Codecs.NoOodleDll or Codecs.NoNativeOodle => r,
            _ when s.Engine?.Unsupported is { } u => u.StartsWith("no D3D shaders", StringComparison.Ordinal) ? "No DirectX shaders" : "Engine not supported yet",
            _ when r == ScsKiller.CheckingReason => null,   // the title: "Checking…"
            _ when r == ScsKiller.NotCheckedReason => "Open to check",
            _ when s.Engine == null => r == "engine not supported yet" ? "Engine not supported yet" : "Couldn't read the game files",
            _ when Starts("runs on ") => "Runs on " + FirstClause(r["runs on ".Length..]),
            _ when Starts("not supported on this GPU") => "Not supported on this GPU yet",
            _ => Sentence(FirstClause(r)),
        };
    }

    /// <summary>A shader mod that doesn't block the game, on its own line after its reason: "RenoDX changes this game's
    /// pipelines: ...". None for an unsupported game: its status and reason alone.</summary>
    public static string ModNote(GameState s) =>
        (s is { ShaderMod: not null, ShaderModBlocks: false } && s.Status != GameStatus.Unsupported ? "\n" + ScsKiller.ShaderModNote(s) : "")
        + (s.Dxvk && s.Status != GameStatus.Unsupported ? "\n" + (s.DxvkCompiled ? ScsKiller.DxvkCompiledNote : ScsKiller.DxvkNote) : "");

    /// <summary>Recorder in, not long enough yet: the DirectX 12 hint stays, a DirectX 11 run never loads the recorder.</summary>
    public static string RecorderOnNote(GameState s) => "recorder on: play for about 5 minutes"
        + (s.StatusReason.Contains(Planning.Planner.Dx12Only, StringComparison.Ordinal) ? ", " + Planning.Planner.Dx12Only : "");

    static string StaleNote(string r) =>
        r.StartsWith("driver changed:", StringComparison.Ordinal) ? $"Driver {r[(r.LastIndexOf(' ') + 1)..]} cleared its cache"
        : r == ScsKiller.TrimmedPartReason ? "Part of its shader cache is gone"
        : r == ScsKiller.TrimmedAllReason ? "Its shader cache was removed"
        : r.StartsWith("the compile didn't reach", StringComparison.Ordinal) ? "The compile missed the game's cache"
        : r.StartsWith("the game runs as", StringComparison.Ordinal) ? "The exe name's case changed"
        : r.StartsWith("the last compile filled the cache of", StringComparison.Ordinal) ? "Compiled for another exe"
        : r.StartsWith("game updated", StringComparison.Ordinal) ? "Game updated"
        : r.StartsWith("game shaders changed", StringComparison.Ordinal) ? "Game shaders changed"
        : r.StartsWith("SCSKiller can now compile", StringComparison.Ordinal) ? "SCSKiller can compile more"
        : r.StartsWith("Maximum mode", StringComparison.Ordinal) ? "Maximum mode: more to compile"
        : Sentence(FirstClause(r));   // "N new pipelines recorded"

    static string FirstClause(string t) => t.Split([": ", "; ", " ("], 2, StringSplitOptions.None)[0];
    static string Sentence(string t) => t.Length > 0 ? char.ToUpperInvariant(t[0]) + t[1..] : t;
    static string InDb(string reason) => reason.Contains(ScsKiller.InDbSupportersNote, StringComparison.Ordinal) ? ScsKiller.InDbSupportersNote : ScsKiller.InDbNote;
}
