using System.Globalization;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.App;

/// <summary>Text the app and the CLI show.</summary>
public static class Format
{
    public const string Dash = "—";

    /// <summary>"3.2 GB" from 1 GiB up, else whole MB; a dash for null.</summary>
    public static string Bytes(long? b) => b switch
    {
        null => Dash,
        >= 1L << 30 => Loc.Format($"{b / (double)(1L << 30):0.#} GB"),
        _ => Loc.Format($"{b / (double)(1 << 20):0} MB"),
    };

    /// <summary><see cref="ScsKiller.Duration"/> in the UI language (that one stays English: reasons and logs); a dash for null.</summary>
    public static string Duration(TimeSpan? t) => t is not { } v ? Dash
        : v.TotalMinutes < 1 ? Loc.Format($"{Math.Max(1, (int)v.TotalSeconds)} s")
        : v.TotalHours < 1 ? Loc.Format($"{(int)Math.Round(v.TotalMinutes)} min")
        : v.Minutes > 0 ? Loc.Format($"{(int)v.TotalHours} h {v.Minutes} min") : Loc.Format($"{(int)v.TotalHours} h");

    /// <summary>The item is being compiled: any stage before it finishes, paused included.</summary>
    public static bool Running(QueueItem q) => q.Stage is QueueStage.Indexing or QueueStage.Planning or QueueStage.Materializing
        or QueueStage.Warming or QueueStage.Paused;

    /// <summary>The queue's background plan-check summary, from typed stages rather than its invariant diagnostic text.</summary>
    public static string? QueuePlanChecks(IEnumerable<QueueItem> queue) =>
        queue.Count(q => q.PlanCheck && q.Stage is not (QueueStage.Done or QueueStage.Failed or QueueStage.Stopped)) is var n and > 0
            ? Loc.Format($"Checking {n} game{(n == 1 ? "" : "s")} for more to compile (while idle)") : null;

    /// <summary>The current queue stage, with known notes localized only for display.</summary>
    public static string QueueActivity(QueueItem? q) => q?.Stage switch
    {
        QueueStage.Indexing => Loc.Text("Reading shaders"),
        QueueStage.Planning => Loc.Text("Building plan"),
        QueueStage.Materializing => Loc.Text("Preparing files"),
        QueueStage.Paused => q.Note != null ? Loc.Format($"Paused: {QueueExplanation(q, null)}") : Loc.Text("Paused"),
        QueueStage.Warming when q.Note != null => Loc.Format($"Compiling: {QueueExplanation(q, null)}"),
        _ => Loc.Text("Compiling"),
    };

    /// <summary>A queue row's display text. Original errors, notes and scheduling predicates remain unchanged.</summary>
    public static string QueueNote(QueueItem q, GameState? g = null) => q.Stage switch
    {
        QueueStage.Failed => Loc.Format($"Failed: {Loc.Text(q.Error ?? "")}"),
        QueueStage.Stopped when q.Progress is { } p => Loc.Format($"Stopped at {p.Done:N0} of {p.Total:N0}: add it again to continue") + (q.Note != null ? " · " + QueueExplanation(q, g) : ""),
        QueueStage.Stopped => Loc.Text("Stopped"),
        QueueStage.Done => q.Note != null ? Loc.Text("Done · ") + QueueExplanation(q, g) : Loc.Text("Done"),
        _ when q.Note != null => QueueExplanation(q, g),
        _ => g?.Plan is { } plan ? Loc.Format($"{plan.Recorded + plan.Generated + plan.MiddlewareItems:N0} pipelines{(plan.D3D11Shaders > 0 ? Loc.Format($" + {plan.D3D11Shaders:N0} DirectX 11 shaders") : "")}") : Loc.Text("plan is built first"),
    };

    static string QueueExplanation(QueueItem q, GameState? g)
    {
        if (q.NoteFormat is { } message && q.Note == message.ToString()) return Loc.Format(message);
        long crashed = g?.LastWarmCrashed ?? 0;
        if (q.Stage is QueueStage.Done or QueueStage.Stopped && q.Progress is { } p
            && q.Note == ScsKiller.WarmCounts(p.Failed, p.Skipped, crashed))
            return string.Join(Loc.Text(", "), new[]
            {
                p.Failed > 0 ? Loc.Format($"{p.Failed} failed (the driver rejected them)") : null,
                p.Skipped > 0 ? Loc.Format($"{p.Skipped} skipped (a shader not in this install)") : null,
                crashed > 0 ? Loc.Format($"{crashed} skipped ({(crashed == 1 ? "it crashes" : "they crash")} the GPU driver)") : null,
            }.OfType<string>());
        return Loc.Text(q.Note ?? "");
    }

    /// <summary><see cref="ScsKiller.PausedNote"/> in the UI language.</summary>
    public static string PausedNote(Settings s) =>
        Loc.Format($"Recording paused: limit reached ({(s.RecordingLimitMB <= 0 ? Loc.Text("Unlimited") : ScsKiller.LimitText(s.RecordingLimitMB))})");

    /// <summary>A game's status reason in the UI language: the new-pipelines count (<see cref="NewPipelinesNote"/>), else
    /// <see cref="Reason(string)"/>.</summary>
    public static string Reason(GameState s) => NewPipelinesNote(s) ?? Reason(s.StatusReason);

    /// <summary>One of the core's English messages (a status reason, a queue note or error, a recorder note) in the UI
    /// language: the whole message when it is a resource, else each "; " clause, else the first <see cref="ReasonTemplates"/>
    /// it matches, its arguments translated the same way. Anything unknown, and everything in English, comes back as it
    /// is; the stored message never changes. FormatTests feeds it the core's own messages, so a reworded one shows.</summary>
    public static string Reason(string reason)
    {
        if (reason.Length == 0) return reason;
        var translated = Loc.Text(reason);
        if (translated != reason) return translated;
        var key = char.ToUpperInvariant(reason[0]) + reason[1..];
        translated = Loc.Text(key);
        if (translated != key) return translated;
        if (reason.Contains("; ", StringComparison.Ordinal))
        {
            var clauses = reason.Split("; ");
            var shown = clauses.Select(Reason).ToArray();
            return shown.SequenceEqual(clauses) ? reason : string.Join(Loc.Text("; "), shown);   // nothing known: as it is
        }
        // the longest template that matches: "…, in about {1}" also matches "…, in about {1}: the next compile is careful"
        var (template, pattern) = ReasonPatterns.Where(p => p.Pattern.IsMatch(reason)).OrderByDescending(p => p.Template.Length).FirstOrDefault();
        if (template != null && Loc.Text(template) is var text && text != template)
            return string.Format(CultureInfo.CurrentCulture, text, pattern.Match(reason).Groups.Cast<Group>().Skip(1).Select(g => (object)Reason(g.Value)).ToArray());
        return DurationText(reason) ?? reason;
    }

    /// <summary>The core's messages with a part that varies (a name, a count, a version), capitalized like the resources;
    /// matched whatever the case of the first letter.</summary>
    public static readonly IReadOnlyList<string> ReasonTemplates =
    [
        "Couldn't read its files: {0}",
        "Runs on {0}",
        "{0} (last run)",
        "Needs a recording, which {0} blocks",
        "Needs a recording, which {0} blocks unless you record an offline session (game page)",
        "No raw DXBC/DXIL shaders in its files ({0} GB sampled): shaders are compressed or packed: needs an engine reader",
        "Only {0} raw compute shaders: graphics shaders are compressed or packed: needs an engine reader",
        "Only {0} raw graphics shaders: the rest are compressed or packed: needs an engine reader",
        "Shader dump version {0}: SCSKiller reads version {1}",
        "Listed with {0}, but the game runs {1}",
        "Unity {0}: none of {1} built-in shaders has DirectX (d3d11) code this reader can read",
        "No D3D shaders ({0})",
        "No shader containers in its packages' material files (commonest files: {0})",
        "Luma isn't supported with {0} yet",
        "ReShade must be next to the game's exe to compile through {0}",
        "Set LoadReshade=true in OptiScaler.ini to compile through {0}",
        "Fix the section headers in OptiScaler.ini to compile through {0}",
        "Rename ReShade to dxgi.dll next to the exe to compile through {0}",
        "NvOSC.exe {0} did not finish within a minute",
        "The setting was saved, but NvOSC.exe {0} failed (exit code {1})",
        "Driver changed: {0} -> {1}",
        "Warmed for driver {0}",
        "Partly warmed for driver {0}: {1}% of the {2} pipelines its first launch created still compiled",
        "Partly warmed for driver {0}: {1}% of the {2} pipelines its first launch created still compiled, even after a careful compile",
        "A careful compile ({0} threads, in passes) reaches more of them",
        "A careful compile ({0} threads, in passes) reaches more of them, in about {1}",
        "A careful compile ({0} threads, in passes) reaches more of them: the next compile is careful",
        "A careful compile ({0} threads, in passes) reaches more of them, in about {1}: the next compile is careful",
        "Compiles {0} pipelines",
        "{0} more shader combinations use shader slots SCSKiller can't rebuild yet",
        "Ray-traced effects aren't compiled: they need a recording, which {0} blocks",
        "Ray-traced effects aren't compiled: they need a recording, which {0} blocks unless you record an offline session (game page)",
        "{0} is installed as d3d12.dll, and the recorder can't run alongside it: renamed it stops working",
        "Restart SCSKiller to use {0}",
        "SCSKiller can't rebuild {0}'s ray tracing layout from the game files yet.",
        "The community database had a problem (error {0}). It retries at the next scan.",
        "The community database refused the request (error {0}).",
    ];

    static readonly (string Template, Regex Pattern)[] ReasonPatterns = ReasonTemplates.Select(template => (template,
        new Regex("\\A" + string.Join("(.+?)", Regex.Split(template, @"\{\d+\}").Select(Regex.Escape)) + "\\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking))).ToArray();

    /// <summary>A <see cref="ScsKiller.Duration"/> inside a message ("in about 2 h 5 min") as <see cref="Duration"/> shows it.</summary>
    static string? DurationText(string t)
    {
        static int N(Group g) => int.Parse(g.Value, CultureInfo.InvariantCulture);
        return Regex.Match(t, @"\A(0|[1-9]\d{0,8}) (s|min|h)(?: (0|[1-9]\d{0,8}) min)?\z", RegexOptions.CultureInvariant) is not { Success: true } m ? null
            : m.Groups[2].Value == "h" ? m.Groups[3].Success ? Loc.Format($"{N(m.Groups[1])} h {N(m.Groups[3])} min") : Loc.Format($"{N(m.Groups[1])} h")
            : m.Groups[3].Success ? null
            : m.Groups[2].Value == "min" ? Loc.Format($"{N(m.Groups[1])} min") : Loc.Format($"{N(m.Groups[1])} s");
    }

    /// <summary>"FSR4: 80 known pipelines, compiled with the game", "DLSS: compiled by the NVIDIA driver itself",
    /// "XeSS: detected; added after a recording sees them".</summary>
    public static string Middleware(MiddlewareTag t) => t.Label + (t.Pipelines > 0 ? Loc.Format($": {t.Pipelines:N0} known pipelines, compiled with the game")
        : t.Label == "DLSS" ? Loc.Text(": compiled by the NVIDIA driver itself") : Loc.Text(": detected; added after a recording sees them"));

    /// <summary>The Library's notice that this GPU can't compile; null on NVIDIA and AMD, or once closed for this GPU
    /// (<paramref name="dismissedFor"/>: <see cref="Settings.GpuNoticeDismissed"/>).</summary>
    public static string? GpuNotice(GpuInfo gpu, string? dismissedFor) => gpu.Vendor is GpuVendor.Nvidia or GpuVendor.Amd || dismissedFor == gpu.Name ? null
        : gpu.Vendor switch
        {
            GpuVendor.Intel => Loc.Text("SCSKiller doesn't compile on Intel GPUs yet: how Intel's driver caches shaders hasn't been measured. Support is planned."),
            GpuVendor.Qualcomm => Loc.Text("SCSKiller doesn't compile on Qualcomm GPUs yet: how Qualcomm's driver caches shaders hasn't been measured."),
            _ => Loc.Text("SCSKiller doesn't compile on this GPU yet: it only knows how NVIDIA and AMD drivers cache shaders."),
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
            GameStatus.Warmed when partly => Loc.Format($"Driver {s.WarmedDriverVersion} · {s.Careful!.LaunchCompiled * 100:0}% still compiled"),
            GameStatus.Warmed when ScsKiller.RtAfterRecording(s) => s.RecorderInstalled ? Loc.Text("Recorder on: play with ray tracing")
                : ScsKiller.RecordedEnough(s) ? Loc.Text("Ray tracing needs a recording") : Loc.Text("Ray tracing needs a 5-min recording"),
            GameStatus.Warmed => Loc.Format($"Driver {s.WarmedDriverVersion}") + (ScsKiller.IsPartial(s.Plan) ? Loc.Text(" · partly covered") : ""),
            GameStatus.Stale => NewPipelinesNote(s, brief: true) ?? StaleNote(r),
            GameStatus.NeedsRecording when s.RecordingPaused => Loc.Text("Recording paused: limit reached"),
            GameStatus.NeedsRecording when s.RecorderInstalled && !ScsKiller.RecordedEnough(s) => Loc.Text("Recorder on: play 5 minutes"),
            GameStatus.NeedsRecording when Starts(ScsKiller.RtNeedsRecording) => Loc.Text("For ray-traced effects") + (s.InCommunityDb == true ? Loc.Text(" · in the community database") : ""),
            GameStatus.NeedsRecording when s.InCommunityDb == true => Loc.Text("In the community database"),
            GameStatus.NeedsRecording when Starts("the recording has no draws") => Loc.Text("Play into the game world"),
            GameStatus.NeedsRecording when s.RecorderInstalled => Sentence(FirstClause(r)),
            GameStatus.NeedsRecording => Loc.Text("Turn on Record and play"),
            GameStatus.Ready when Has("ray-traced effects aren't compiled") => s.AntiCheat == AntiCheat.None ? Loc.Text("Ray tracing not compiled") : Loc.Format($"Ray tracing blocked by {AntiCheatName(s.AntiCheat)}"),
            GameStatus.Ready when Has(ScsKiller.RtUnseenNote) => Loc.Text("No ray tracing seen while recording"),
            GameStatus.Ready when Has(ScsKiller.RtInlineNote) => Loc.Text("Path tracing needs a recording"),
            GameStatus.Ready when ScsKiller.IsPartial(s.Plan) => Loc.Text("Partly covered"),
            GameStatus.Ready when Has(Planning.Planner.UntestedNote) => Loc.Text("Not tested on this engine version"),
            GameStatus.Ready when Has("for DirectX 12, ") => Loc.Text("DirectX 11; DirectX 12 needs a recording"),
            GameStatus.Ready when Has("also compiles every DirectX 11 shader") => Loc.Text("DirectX 11 and 12"),
            GameStatus.Ready when Starts("compiles every DirectX 11 shader") => "DirectX 11",
            GameStatus.Ready => null,
            _ when s.ShaderModBlocks => Loc.Format($"{s.ShaderMod} changes all its pipelines"),
            _ when s.AntiCheat != AntiCheat.None && Starts("needs a recording, which") => Loc.Format($"Blocked by {AntiCheatName(s.AntiCheat)}") + (s.InCommunityDb == true ? Loc.Text(" · in the community database") : ""),
            _ when Has(ScsKiller.ManualNoRecording) => Loc.Text("Needs a recording: confirm its folder"),
            _ when s.Engine?.Encrypted == true => null,   // the title: "Encrypted game files"
            _ when s.Engine?.Unsupported is { } u => u.StartsWith("no D3D shaders", StringComparison.Ordinal) ? Loc.Text("No DirectX shaders") : Loc.Text("Engine not supported yet"),
            _ when s.Engine == null => r == "engine not supported yet" ? Loc.Text("Engine not supported yet") : Loc.Text("Couldn't read the game files"),
            _ when Starts("runs on ") => Loc.Text("Runs on ") + FirstClause(r["runs on ".Length..]),
            _ when Starts("not supported on this GPU") => Loc.Text("Not supported on this GPU yet"),
            _ => Sentence(FirstClause(r)),
        };
    }

    /// <summary>Display the known pending-pipeline reason from its counts, without parsing or changing stored diagnostics.</summary>
    public static string? NewPipelinesNote(GameState s, bool brief = false)
    {
        if (s.Status != GameStatus.Stale) return null;
        long count = s.RecordedSinceWarm + (s.NewPipelines ?? 0);
        string plural = count == 1 ? "" : "s";
        bool recorded = count == s.RecordedSinceWarm;
        if (count <= 0 || s.StatusReason != $"{count:N0} new pipeline{plural}{(recorded ? " recorded" : "")}; compile again to include them") return null;
        return recorded
            ? brief ? Loc.Format($"{count:N0} new pipeline{plural} recorded") : Loc.Format($"{count:N0} new pipeline{plural} recorded; compile again to include them")
            : brief ? Loc.Format($"{count:N0} new pipeline{plural}") : Loc.Format($"{count:N0} new pipeline{plural}; compile again to include them");
    }

    static string StaleNote(string r) =>
        r.StartsWith("driver changed:", StringComparison.Ordinal) ? Loc.Format($"Driver {r[(r.LastIndexOf(' ') + 1)..]} cleared its cache")
        : r == ScsKiller.TrimmedPartReason ? Loc.Text("The driver trimmed its cache")
        : r == ScsKiller.TrimmedAllReason ? Loc.Text("Its shader cache was removed")
        : r.StartsWith("the compile didn't reach", StringComparison.Ordinal) ? Loc.Text("The compile missed the game's cache")
        : r.StartsWith("the game runs as", StringComparison.Ordinal) ? Loc.Text("The exe name's case changed")
        : r.StartsWith("game updated", StringComparison.Ordinal) ? Loc.Text("Game updated")
        : r.StartsWith("game shaders changed", StringComparison.Ordinal) ? Loc.Text("Game shaders changed")
        : r.StartsWith("SCSKiller can now compile", StringComparison.Ordinal) ? Loc.Text("SCSKiller can compile more")
        : r.StartsWith("Maximum mode", StringComparison.Ordinal) ? Loc.Text("Maximum mode: more to compile")
        : Sentence(FirstClause(r));   // "N new pipelines recorded"

    static string FirstClause(string t) => t.Split([": ", "; ", " ("], 2, StringSplitOptions.None)[0];
    static string Sentence(string t) => t.Length > 0 ? char.ToUpperInvariant(t[0]) + t[1..] : t;

    static string AntiCheatName(AntiCheat a) => a switch { AntiCheat.EasyAntiCheat => "EasyAntiCheat", AntiCheat.BattlEye => "BattlEye", _ => Loc.Text("anti-cheat") };
}
