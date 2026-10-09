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

    /// <summary>"Carved DXBC", "Unity 2022.3.10"; just the family (or <paramref name="family"/>) when the reader found no
    /// version: Carved and FromSoft say "-", Unity "?".</summary>
    public static string Engine(EngineInfo e, string? family = null) =>
        e.Version is "" or "-" or "?" ? family ?? e.Family : $"{family ?? e.Family} {e.Version}";

    /// <summary>The item is being compiled: any stage before it finishes, paused included.</summary>
    public static bool Running(QueueItem q) => q.Stage is QueueStage.Indexing or QueueStage.Planning or QueueStage.Materializing
        or QueueStage.Warming or QueueStage.Paused;

    /// <summary>"FSR4: 80 known pipelines, compiled with the game", "DLSS: compiled by the NVIDIA driver itself",
    /// "XeSS: detected; added after a recording sees them".</summary>
    public static string Middleware(MiddlewareTag t) => t.Label + (t.Pipelines > 0 ? $": {t.Pipelines:N0} known pipelines, compiled with the game"
        : t.Label == "DLSS" ? ": compiled by the NVIDIA driver itself" : ": detected; added after a recording sees them");

    /// <summary>The Library's notice that this GPU can't compile; null on NVIDIA and AMD, or once closed for this GPU
    /// (<paramref name="dismissedFor"/>: <see cref="Settings.GpuNoticeDismissed"/>).</summary>
    public static string? GpuNotice(GpuInfo gpu, string? dismissedFor) => gpu.Vendor is GpuVendor.Nvidia or GpuVendor.Amd || dismissedFor == gpu.Name ? null
        : gpu.Vendor switch
        {
            GpuVendor.Intel => "SCSKiller doesn't compile on Intel GPUs yet: how Intel's driver caches shaders hasn't been measured. Support is planned.",
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
            GameStatus.NeedsRecording when Starts(ScsKiller.RtNeedsRecording) => "For ray-traced effects" + (s.InCommunityDb == true ? " · in the community database" : ""),
            GameStatus.NeedsRecording when s.InCommunityDb == true => "In the community database",
            GameStatus.NeedsRecording when Starts("the recording has no draws") => "Play into the game world",
            GameStatus.NeedsRecording when s.RecorderInstalled => Sentence(FirstClause(r)),
            GameStatus.NeedsRecording when s.RecorderSkip != null => "Recorder not available",
            GameStatus.NeedsRecording => "Turn on Record and play",
            GameStatus.Ready when Has("ray-traced effects aren't compiled") => s.AntiCheat == AntiCheat.None ? "Ray tracing not compiled" : $"Ray tracing blocked by {AntiCheatName(s.AntiCheat)}",
            GameStatus.Ready when Has(ScsKiller.RtUnseenNote) => "No ray tracing seen while recording",
            GameStatus.Ready when Has(ScsKiller.RtInlineNote) => "Path tracing needs a recording",
            GameStatus.Ready when ScsKiller.IsPartial(s.Plan) => "Partly covered",
            GameStatus.Ready when Has(Planning.Planner.UntestedNote) => "Not tested on this engine version",
            GameStatus.Ready when Has("for DirectX 12, ") => "DirectX 11; DirectX 12 needs a recording",
            GameStatus.Ready when Has("also compiles every DirectX 11 shader") => "DirectX 11 and 12",
            GameStatus.Ready when Starts("compiles every DirectX 11 shader") => "DirectX 11",
            GameStatus.Ready => null,
            _ when s.ShaderModBlocks => $"{s.ShaderMod} changes all its pipelines",
            _ when s.AntiCheat != AntiCheat.None && Starts("needs a recording, which") => $"Blocked by {AntiCheatName(s.AntiCheat)}" + (s.InCommunityDb == true ? " · in the community database" : ""),
            _ when Has(ScsKiller.ManualNoRecording) => "Needs a recording: confirm its folder",
            _ when s.Engine?.Encrypted == true => null,   // the title: "Encrypted game files"
            _ when s.Engine?.Unsupported is { } u => u.StartsWith("no D3D shaders", StringComparison.Ordinal) ? "No DirectX shaders" : "Engine not supported yet",
            _ when s.Engine == null => r == "engine not supported yet" ? "Engine not supported yet" : "Couldn't read the game files",
            _ when Starts("runs on ") => "Runs on " + FirstClause(r["runs on ".Length..]),
            _ when Starts("not supported on this GPU") => "Not supported on this GPU yet",
            _ => Sentence(FirstClause(r)),
        };
    }

    /// <summary>A shader mod that doesn't block the game, after its reason: "; RenoDX changes this game's pipelines: ...".
    /// None for an unsupported game: its status and reason alone.</summary>
    public static string ModNote(GameState s) =>
        s is { ShaderMod: not null, ShaderModBlocks: false } && s.Status != GameStatus.Unsupported ? "; " + ScsKiller.ShaderModNote(s) : "";

    /// <summary>Recorder in, not long enough yet: the DirectX 12 hint stays, a DirectX 11 run never loads the recorder.</summary>
    public static string RecorderOnNote(GameState s) => "recorder on: play for about 5 minutes"
        + (s.StatusReason.Contains(Planning.Planner.Dx12Only, StringComparison.Ordinal) ? ", " + Planning.Planner.Dx12Only : "");

    static string StaleNote(string r) =>
        r.StartsWith("driver changed:", StringComparison.Ordinal) ? $"Driver {r[(r.LastIndexOf(' ') + 1)..]} cleared its cache"
        : r == ScsKiller.TrimmedPartReason ? "The driver trimmed its cache"
        : r == ScsKiller.TrimmedAllReason ? "Its shader cache was removed"
        : r.StartsWith("the compile didn't reach", StringComparison.Ordinal) ? "The compile missed the game's cache"
        : r.StartsWith("the game runs as", StringComparison.Ordinal) ? "The exe name's case changed"
        : r.StartsWith("game updated", StringComparison.Ordinal) ? "Game updated"
        : r.StartsWith("game shaders changed", StringComparison.Ordinal) ? "Game shaders changed"
        : r.StartsWith("SCSKiller can now compile", StringComparison.Ordinal) ? "SCSKiller can compile more"
        : r.StartsWith("Maximum mode", StringComparison.Ordinal) ? "Maximum mode: more to compile"
        : Sentence(FirstClause(r));   // "N new pipelines recorded"

    static string FirstClause(string t) => t.Split([": ", "; ", " ("], 2, StringSplitOptions.None)[0];
    static string Sentence(string t) => t.Length > 0 ? char.ToUpperInvariant(t[0]) + t[1..] : t;

    static string AntiCheatName(AntiCheat a) => a switch { AntiCheat.EasyAntiCheat => "EasyAntiCheat", AntiCheat.BattlEye => "BattlEye", _ => "anti-cheat" };
}
