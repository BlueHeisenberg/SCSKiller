using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.UI;

namespace SCSKiller.App;

// View models are computed views over IScsKiller: Refresh() re-reads the facade and tells x:Bind to update everything.
public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed() => PropertyChanged?.Invoke(this, new(string.Empty));
    protected void Changed(params string[] names) { foreach (var n in names) PropertyChanged?.Invoke(this, new(n)); }
}

static class Fmt
{
    public static string RecorderDll(GameState s) => s.AgsHost ? ScsKiller.AgsName : s.Records11 ? ScsKiller.Dll11 : ScsKiller.Records9(s.Engine) ? ScsKiller.Dll9 : ScsKiller.Dll12;
    /// <summary>What the recorder adds next to the game: its dll, and the preloader that loads it before DLSS.</summary>
    public static string RecorderFiles(GameState s) => s.Preload is { } p ? $"{RecorderDll(s)} and {p}" : RecorderDll(s);
    public static string N(long? n) => n?.ToString("N0") ?? Format.Dash;
    /// <summary>"today at 20:14", "yesterday at 20:14", else "on 26/09/2026" (the culture's short date).</summary>
    public static string When(DateTimeOffset at)
    {
        var t = at.LocalDateTime;
        return t.Date == DateTime.Today ? Loc.T("Fmt_TodayAt", t.ToString("t")) : t.Date == DateTime.Today.AddDays(-1) ? Loc.T("Fmt_YesterdayAt", t.ToString("t")) : Loc.T("Fmt_On", t.ToString("d"));
    }
    public static string KeptSize(KeptCache k) => $"{Format.Bytes(k.Bytes)} · " + (k.Frees > 0 ? Loc.T("Fmt_Frees", Format.Bytes(k.Frees)) : Loc.T("Fmt_TakesNoSpace"));
    public static string Vendor(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => "NVIDIA", GpuVendor.Amd => "AMD", GpuVendor.Intel => "Intel", _ => "GPU",
    };
    public static string Engine(EngineInfo? e) => e is null ? Loc.T("Fmt_UnknownEngine") : Format.Engine(e, e.Family == "Unreal" ? "UE" : null) + Guessed(e);
    public static string Guessed(EngineInfo e) => e.VersionGuessed ? Loc.T("Fmt_Guessed") : "";
    public static Style Style(string key) => (Style)Application.Current.Resources[key];
    /// <summary>"Severe shader-compilation stutter: reason", then the source and when it was checked; null when not listed.</summary>
    public static string? StutterTip(GameState s) => s.KnownStutter is { } k
        ? Loc.T(k.Severity == StutterSeverity.Severe ? "Fmt_StutterSevere" : "Fmt_StutterModerate", k.Reason, k.Source, $"{k.Date}") : null;
    /// <summary>Library sections, in list order. GOG, Ubisoft, Battle.net, PURPLE, HoYoPlay, Gaijin and Riot Client games are Store.Other: told apart by their id prefix.</summary>
    public static readonly string[] Stores = ["Steam", "Epic Games", "Xbox / Game Pass", "EA app", "GOG", "Ubisoft Connect", "Battle.net", "PURPLE", "HoYoPlay", "Gaijin", "Riot Client", Loc.T("Store_Other"), AddedByYou];
    public static readonly string AddedByYou = Loc.T("Store_AddedByYou");
    public static string StoreName(Game g) => g.Store switch
    {
        Store.Steam => "Steam", Store.Epic => "Epic Games", Store.Xbox => "Xbox / Game Pass", Store.EA => "EA app", Store.Manual => AddedByYou,
        _ => g.Id[..Math.Max(0, g.Id.IndexOf(':'))] switch { "gog" => "GOG", "ubisoft" => "Ubisoft Connect", "battlenet" => "Battle.net", "purple" => "PURPLE", "hoyoplay" => "HoYoPlay", "gryphline" => "GRYPHLINK", "gaijin" => "Gaijin", "riot" => "Riot Client", _ => Loc.T("Store_Other") },
    };
    /// <summary>Measured driver cache when known, else the estimate.</summary>
    public static string Cache(GameState s) => s.DiskBytes is { } c ? Format.Bytes(c) : s.EstimatedCacheBytes is { } b ? "≈ " + Format.Bytes(b) : Format.Dash;
    /// <summary>The last compile's measured time, else the estimate: the estimate's rate is measured on a cold cache only.</summary>
    public static TimeSpan? CompileTime(GameState s) => s.LastWarmTime ?? s.EstimatedWarmTime;
    /// <summary>Tooltip for an estimated (not measured) cache value; null when measured.</summary>
    public static string? CacheTip(GameState s) => s.DiskBytes == null && s.EstimatedCacheBytes != null ? Loc.T("Fmt_EstimatedCache") : null;
    /// <summary>One middleware tag: what it compiles and its DLLs.</summary>
    public static string TagTip(MiddlewareTag t) => Format.Middleware(t, App.Core.Vendor.Vendor == GpuVendor.Nvidia) + "\n    " + string.Join(", ", t.Dlls);
    public static bool Active(QueueItem q) => q.Stage is not (QueueStage.Done or QueueStage.Failed or QueueStage.Stopped);
    public static void PauseOrResume(QueueItem? running)
    {
        if (running?.Stage == QueueStage.Paused) App.Core.ResumeQueue(); else App.Core.PauseQueue();
    }

    /// <summary>The queue page's run order: the running compile (number 1), then the waiting ones; plan checks left out.</summary>
    public static (QueueItem? Running, List<QueueItem> Waiting) RunOrder(IEnumerable<QueueItem> all)
    {
        var compiles = all.Where(q => !q.PlanCheck).ToList();
        return (compiles.FirstOrDefault(Format.Running), compiles.Where(q => q.Stage == QueueStage.Waiting).ToList());
    }
    public static string Stage(QueueItem q) => q.Stage switch
    {
        QueueStage.Indexing => Loc.T("Queue_ReadingShaders"), QueueStage.Planning => Loc.T("Queue_BuildingPlan"), QueueStage.Materializing => Loc.T("Queue_PreparingFiles"),
        QueueStage.Paused => q.Note is { } n ? n : Loc.T("Queue_Paused"),   // the note is a whole "Paused…" sentence (ScsKiller's pause reasons)
        QueueStage.Merging => q.Note ?? Format.MergingStage,
        QueueStage.Saving => Format.SavingStage,
        QueueStage.Finishing => Format.FinishingStage,
        QueueStage.Warming when q.Note is { } n => Loc.T("Queue_CompilingWith", n),   // stalled (ScsKiller.StalledNote)
        QueueStage.Warming when Tail(q) => Loc.T("Queue_CompilingLast"),
        _ => Loc.T("Queue_Compiling"),
    };
    /// <summary>"54.8% · 1,234 of 5,000 pipelines · 480/s" (reading shaders: packages, a plan or file preparation step: its own count,
    /// "12,340 recording entries read" while its total is unknown); null before the warm knows its total.</summary>
    public static string? ProgressText(QueueItem q) => q.Stage is QueueStage.Saving or QueueStage.Finishing ? q.Note   // elapsed time, no share
        : q is { Stage: QueueStage.Planning or QueueStage.Materializing, Progress: { Note: { } step } s } ? s.Total > 0 ? $"{Percent(q):0.0}% · {s.Done:N0} of {s.Total:N0} {step}" : $"{s.Done:N0} {step}"
        : q.Progress is { Total: > 0 } p
        ? q.Stage == QueueStage.Merging ? $"{Percent(q):0.0}% · {Format.Bytes(p.Done)} of {Format.Bytes(p.Total)}"
        : $"{Percent(q):0.0}% · {p.Done:N0} of {p.Total:N0} {Loc.T(q.Stage == QueueStage.Indexing ? "Fmt_Packages" : "Fmt_Pipelines")}" + (p.PerSecond > 0 && !ScsKiller.Stalled(q) ? $" · {p.PerSecond:N0}/s" : "") + (p.Failed > 0 ? $" · {Loc.T("Fmt_FailedShort", p.Failed)}" : "") + (p.Skipped > 0 ? $" · {Loc.T("Fmt_SkippedShort", p.Skipped)}" : "")   // short (the Done note explains them); a count wraps whole
            + (ScsKiller.MemoryLimitNote(p) is { } m ? " · " + m : "")
        : null;
    /// <summary>The warm's last pipelines in flight: the rate says under 2 s, but the slowest ones are still compiling.</summary>
    static bool Tail(QueueItem q) => q is { Stage: QueueStage.Warming, Progress: { Total: > 0, PerSecond: > 0 } p } && (p.Total - p.Done) / p.PerSecond < 2;
    /// <summary>"started 21:14" (local time); null before the compile's first stage.</summary>
    public static string? Started(QueueItem q) => q.StartedAt is { } t ? Loc.T("Fmt_StartedAt", t.LocalDateTime.ToString("t")) : null;
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
            || App.Core.QueueRunning && compiles.FirstOrDefault(q => q.Stage == QueueStage.Waiting && !ScsKiller.WaitsOnItsOwn(q))?.GameId == gameId;
    }

    public static readonly string CompilingTip = Loc.T("Queue_CompilingTip");

    /// <summary>What IScsKiller doesn't carry, from the real core or the design data.</summary>
    public static ManualSource? Added => App.Core is ScsKiller k ? k.Manual : (App.Core as Design.FakeScsKiller)?.Manual;
    public static string? ScanProgress => App.Core is ScsKiller k ? k.ScanProgress : (App.Core as Design.FakeScsKiller)?.ScanProgress;
    public static string? AfterUpdateLine => App.Core is ScsKiller k ? k.AfterUpdateLine : (App.Core as Design.FakeScsKiller)?.AfterUpdateLine;
    public static string? FoldLine(string gameId) => App.Core is ScsKiller k ? k.FoldLine(gameId) : (App.Core as Design.FakeScsKiller)?.FoldLine(gameId);
    public static void Rename(string gameId, string name)
    {
        if (App.Core is ScsKiller k) k.RenameManualGame(gameId, name);
        else (App.Core as Design.FakeScsKiller)?.RenameManualGame(gameId, name);
    }


    /// <summary>Starts the game through its store (<see cref="StoreLaunch"/>); the error to show, or null. The design data starts nothing.</summary>
    public static string? Play(GameState s)
    {
        var g = s.Game;
        if (StoreLaunch.OwnLauncherOnly(s)) return Loc.T("Play_StartFromLauncher", g.Name);
        if (App.Core is Design.FakeScsKiller) return null;
        try
        {
            if (StoreLaunch.Command(g) is not { } command) return Loc.T("Play_CouldntFind", StoreName(g), g.Name);
            System.Diagnostics.Process.Start(command)?.Dispose();
            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return g.Store == Store.Manual ? Loc.T("Play_CouldntStartPlain", g.Name, e.Message) : Loc.T("Play_CouldntStartVia", g.Name, StoreName(g), e.Message);
        }
    }

    /// <summary>The Play button's tooltip when it can start the game.</summary>
    public static string PlayVia(Game g) => g.Store == Store.Manual ? Loc.T("Play_StartExe", Path.GetFileName(g.ExePath))
        // the store starts the collection, which opens the game played last
        : Collections.Member(g.Id) != null ? Loc.T("Play_ThroughThenPick", StoreName(g), g.Name)
        : Loc.T("Play_Through", StoreName(g));
}

/// <summary>Folds a burst of Core events into one run on the UI thread: a scan raises GameChanged once per game (62 at
/// once on a real library), and a page refresh per event kept the UI thread busy for seconds. Request from any thread.</summary>
public sealed class Coalesced(DispatcherQueue ui, Action run)
{
    int pending;

    public void Request()
    {
        if (Interlocked.Exchange(ref pending, 1) == 0 && !ui.TryEnqueue(() => { Volatile.Write(ref pending, 0); run(); }))
            Volatile.Write(ref pending, 0);   // the window is closing
    }
}

/// <summary>A read that must not run on the UI thread, such as the driver cache's usage and limit (NVIDIA's limit loads the
/// driver's profile database: ~130 ms measured; the usage walks the cache folder). <paramref name="read"/> runs on a
/// background thread, <paramref name="apply"/> on the UI thread. One read at a time: requests meanwhile fold into one more
/// read, so results apply in order. A read that throws changes nothing and is logged once until it reads again: thrown
/// from this async void, it would end the app. Request on the UI thread.</summary>
public sealed class BackgroundRead<T>(string name, Func<T> read, Action<T> apply)
{
    bool busy, again;
    string? failed;

    public async void Request()
    {
        if (busy) { again = true; return; }
        busy = true;
        try
        {
            do
            {
                again = false;
                T value;
                try { value = await Task.Run(read); }
                catch (OperationCanceledException) { continue; }
                catch (Exception e)
                {
                    if (e.Message != failed) App.Log?.Report($"{name} read failed: {e.Message}");
                    failed = e.Message;
                    continue;
                }
                failed = null;
                apply(value);
            } while (again);
        }
        finally { busy = false; }
    }
}

/// <summary>Game icons: a packaged (Xbox / Game Pass) game's own logo, else the exe's shell thumbnail (its own icon); null
/// when the exe isn't there or the icon failed to load after retries (the row shows its tile). UI thread only.</summary>
static class Icons
{
    // The stream stays open as long as its image: XAML may decode again from it (another size: Library 32 px, detail 56 px).
    static readonly Dictionary<string, (BitmapImage Image, IDisposable? Source)> cache = [];
    static readonly HashSet<string> failed = [];

    /// <summary>An icon failed after its row was built: Get now returns null for it, so rebuild the rows (UI thread).</summary>
    public static event Action? Failed;

    public static ImageSource? Get(string exePath)
    {
        if (failed.Contains(exePath)) return null;
        if (cache.TryGetValue(exePath, out var entry)) return entry.Image;   // no disk look per row rebuild: a busy disk stalls the UI thread
        if (!File.Exists(exePath)) return null;
        var bmp = new BitmapImage();
        bmp.ImageFailed += (_, _) => Fail(exePath);   // a decode failure
        cache[exePath] = (bmp, null);
        _ = LoadAsync(exePath, bmp);
        return bmp;
    }

    static async Task LoadAsync(string path, BitmapImage bmp)
    {
        var logo = await Task.Run(() => PackageLogo(path));
        for (var attempt = 1; ; attempt++)
        {
            IRandomAccessStream? stream = null;
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(logo ?? path);
                stream = logo != null ? await file.OpenReadAsync()
                    : await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 64) ?? throw new IOException("no thumbnail");
                cache[path] = (bmp, stream);
                await bmp.SetSourceAsync(stream);
                return;
            }
            catch (Exception)
            {
                stream?.Dispose();
                // The shell often answers E_PENDING (0x8000000A) at first, while many icons are asked for at once (seen on
                // Elden Ring's and SCSKiller's own exe): retry for ~8 s before the tile. An unreadable exe fails every time.
                if (attempt == 5) { Fail(path); return; }
                await Task.Delay(250 << attempt);
            }
        }
    }

    /// <summary>The Square44x44Logo of the package the exe sits in (appxmanifest.xml in a parent folder): a GDK game's exe
    /// often has no icon of its own (Atomic Heart), and the shell then shows the generic application icon. Null when not
    /// packaged, unreadable (WindowsApps) or the logo is only there scale-qualified.</summary>
    static string? PackageLogo(string exePath)
    {
        try
        {
            for (var dir = Path.GetDirectoryName(exePath); dir != null; dir = Path.GetDirectoryName(dir))
            {
                var manifest = Path.Combine(dir, "appxmanifest.xml");
                if (!File.Exists(manifest)) continue;
                var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(manifest), "Square44x44Logo=\"([^\"]+)\"");
                return m.Success && Path.Combine(dir, m.Groups[1].Value) is var logo && File.Exists(logo) ? logo : null;
            }
        }
        catch (Exception) { }
        return null;
    }

    static void Fail(string path)
    {
        if (!failed.Add(path)) return;
        if (cache.Remove(path, out var entry)) entry.Source?.Dispose();
        Failed?.Invoke();
    }
}

/// <summary>A small tag on a library row (a middleware label, or "+N").</summary>
public sealed record TagChip(string Text, string Tip, double Width = 90, double Side = 6)   // Width: the text's most, trimmed past it
{
    public Thickness Pad => new(Side, 0, Side, 0);
}

/// <summary>A game's row in the Library. The list keeps one row per game and <see cref="Update"/>s it in place, so the
/// row's container, the scroll position and hover survive a re-evaluation.</summary>
public sealed class GameRow(GameState s, bool queued = false, bool compiling = false) : Bindable
{
    static readonly Color[] Tiles =
    [
        Color.FromArgb(255, 0x3b, 0x4a, 0x8c), Color.FromArgb(255, 0x6b, 0x3b, 0x5e), Color.FromArgb(255, 0x2f, 0x6b, 0x4f),
        Color.FromArgb(255, 0x2f, 0x5f, 0x73), Color.FromArgb(255, 0x7a, 0x4a, 0x2a), Color.FromArgb(255, 0x5a, 0x6b, 0x2f),
        Color.FromArgb(255, 0x3d, 0x55, 0x66), Color.FromArgb(255, 0x5c, 0x3b, 0x7a),
    ];

    public GameState State => s;
    public bool Playing => s.Playing;
    public string Id => s.Game.Id;
    public string Name => s.Game.Name;
    public ImageSource? Icon { get; private set; } = Icons.Get(s.Game.ExePath);
    public bool NoIcon => Icon is null;
    public Brush Tile { get; } = TileOf(s.Game.Name);
    public string Initials => InitialsOf(s.Game.Name);
    public static Brush TileOf(string name) => new SolidColorBrush(Tiles[name.Sum(c => c) % Tiles.Length]);
    public static string InitialsOf(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsUpper(w[0]) || char.IsDigit(w[0])).ToArray();
        return words.Length >= 2 ? $"{words[0][0]}{words[1][0]}" : name[..Math.Min(2, name.Length)].ToUpperInvariant();
    }
    public string Sub => $"{Fmt.Engine(s.Engine)} · {StoreName}";
    public string StoreName { get; } = Fmt.StoreName(s.Game);
    /// <summary>Search: the name, the store as players name it, or the engine ("UE 5", "Unreal"), case-insensitive.</summary>
    public bool Matches(string term) => Name.Contains(term, StringComparison.OrdinalIgnoreCase) || StoreName.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Fmt.Engine(s.Engine).Contains(term, StringComparison.OrdinalIgnoreCase) || s.Engine?.Family.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
    public bool HasTags => s.Middleware is { Count: > 0 } || ModChip(s) != null || ScsKiller.DeliveryChip(s) != null;
    public bool HasTagLine => HasTags || IsKnownStutter || IsNoStutter || HasBaChip;
    /// <summary>A before and after recording in progress; the tile has no button for it, the game page has.</summary>
    public string BaChip => BeforeAfterUi.Voided(s.BeforeAfter) ? "" : s.BeforeAfter?.Stage switch
    {
        BeforeAfterStage.AfterReady => Loc.T("Ba_ChipAfterReady"),
        BeforeAfterStage.Compiling => Loc.T("Ba_ChipCompiling"),
        BeforeAfterStage.BeforePlaying or BeforeAfterStage.AfterPlaying => Loc.T("Ba_ChipPlaying"),
        _ => "",
    };
    public bool HasBaChip => BaChip.Length > 0;
    public bool IsKnownStutter => s.KnownStutter != null;
    public string? StutterTip => Fmt.StutterTip(s);
    /// <summary>On game-verdicts.json as no-stutter: compiling stays possible, but the bulk adds leave it out.</summary>
    public bool IsNoStutter => s.NoStutter != null;
    public string? NoStutterTip => s.NoStutter;
    // The shader mod, then the upscalers: the first three, then "+N" with the rest in its tooltip. After the stutter chip
    // only one more chip fits before the Shaders column: the mod, else several upscalers become "N upscalers".
    const int MaxTags = 3;
    public List<TagChip> Tags { get; private set; } = TagsOf(s);
    static List<TagChip> TagsOf(GameState s)
    {
        var m = s.Middleware ?? [];
        var mod = ModChip(s);
        var asd = ScsKiller.DeliveryChip(s) is { } d ? new TagChip(d, ScsKiller.DeliveryTip, 190, 4) : null;
        List<TagChip> tags = [.. mod != null ? [mod] : Array.Empty<TagChip>(), .. asd != null ? [asd] : Array.Empty<TagChip>(), .. m.Select(t => new TagChip(t.Label, Fmt.TagTip(t)))];
        return s.KnownStutter != null || s.NoStutter != null ? tags.Count > 1 ? [mod ?? asd ?? new TagChip(Loc.T("Tag_Upscalers", m.Count), string.Join("\n", m.Select(Fmt.TagTip)))] : tags
            : tags.Count <= MaxTags ? tags
            : [.. tags.Take(MaxTags), new TagChip($"+{tags.Count - MaxTags}", string.Join("\n", tags.Skip(MaxTags).Select(t => t.Tip)))];
    }
    /// <summary>The shader mod by name, else plain ReShade; null = neither, or not looked (anti-cheat).</summary>
    internal static TagChip? ModChip(GameState s) => s.ShaderMod is { } mod
        ? new(mod, s.ShaderModBlocks ? Loc.T("Mod_Stops") : s.ShaderModLayer ? Loc.T("Mod_Through") : Loc.T("Mod_Few"))
        : s.ReShade ? new("ReShade", Loc.T("Mod_ReShadeNote")) : null;
    public string Shaders => Fmt.N(s.ShaderCount);
    public string Pipelines => Fmt.N(ScsKiller.PlanPipelines(s));
    public string Cache => Fmt.Cache(s);
    public string? CacheTip => Fmt.CacheTip(s);
    public Style CacheStyle => Fmt.Style(CacheTip != null ? "Secondary" : "BodyTextBlockStyle");
    public string Time => Format.Duration(Fmt.CompileTime(s));

    bool Partly => ScsKiller.IsPartlyWarmed(s);
    bool PartlyCompiled => ScsKiller.IsPartlyCompiled(s);
    public string StatusText => s.Status switch
    {
        _ when s.CompileUnreached => Loc.T("GameStatus_CompileDidntHelp"),
        _ when PartlyCompiled => Loc.T("GameStatus_PartlyCompiled"),
        _ when ScsKiller.NotReached(s.StatusReason) => Loc.T("GameStatus_NotReachedYet"),
        GameStatus.Warmed when Partly => Loc.T("GameStatus_PartlyWarmed"),
        GameStatus.Warmed when s.Plan?.RecordedOnly == true => Loc.T("GameStatus_WarmedRecordedOnly"),
        GameStatus.Warmed => Loc.T("GameStatus_Warmed"),
        GameStatus.Ready => Loc.T("GameStatus_ReadyToCompile"),
        _ when ScsKiller.NeedsOfflineSession(s) => Loc.T("GameStatus_NeedsOfflineSession"),
        _ when ScsKiller.NeedsSteamStart(s) => Loc.T("GameStatus_StartOnceFromSteam"),
        GameStatus.NeedsRecording when ScsKiller.NeverRecorded(s) => ScsKiller.NothingRecordedTitle,
        GameStatus.NeedsRecording => ScsKiller.RecordedEnough(s) ? Loc.T("GameStatus_NeedsRecording") : Loc.T("GameStatus_Needs5MinRecording"),
        GameStatus.Stale => Loc.T("GameStatus_NeedsRebuilding"),
        _ when s.StatusReason == ScsKiller.CheckingReason => Loc.T("GameStatus_Checking"),
        _ when s.StatusReason == ScsKiller.NotCheckedReason => Loc.T("GameStatus_NotChecked"),
        _ when s.StatusReason == ScsKiller.CantReachReason => Loc.T("GameStatus_NotCompatible"),
        _ => s.ShaderModBlocks ? Loc.T("GameStatus_NotCompiled") : s.Engine?.Encrypted == true ? Loc.T("GameStatus_Encrypted") : s.NotPlanned ? Loc.T("GameStatus_NotSupported") : Loc.T("GameStatus_NotSupportedYet"),
    };
    /// <summary>The whole reason: the row's tooltip and the game page's status text.</summary>
    public string FullNote => s.Status switch
    {
        _ when s.CompileUnreached || PartlyCompiled || ScsKiller.NotReached(s.StatusReason) => s.StatusReason,
        GameStatus.Warmed when Partly => Loc.T("Note_WarmedPartly", s.WarmedDriverVersion, $"{s.Careful!.LaunchCompiled * 100:0}"),
        GameStatus.Warmed => Loc.T("Note_WarmedDriver", s.WarmedDriverVersion) + (ScsKiller.IsPartial(s.Plan) ? Loc.T("Note_RecordingRest")
            : ScsKiller.RtAfterRecording(s) ? " · " + ScsKiller.RtAfterRecordingNote : "") + Format.KeyNote(s),
        _ when ScsKiller.NeedsOfflineSession(s) || ScsKiller.NeedsSteamStart(s) => s.StatusReason,
        GameStatus.NeedsRecording when s.AnyAntiCheat && !s.RiskEligible => Loc.T("Note_AntiCheatBlocks", ScsKiller.AntiCheatName(s)),
        GameStatus.NeedsRecording when s.RecordingPaused => ScsKiller.PausedNote(App.Core.Settings),
        GameStatus.NeedsRecording when ScsKiller.NeverRecorded(s) => s.RecorderRefused!,
        GameStatus.NeedsRecording when s.RecorderInstalled && !ScsKiller.RecordedEnough(s) => Format.RecorderOnNote(s),
        _ => s.StatusReason,
    };
    /// <summary>The row's note under the status: a few words (<see cref="Format.ShortNote"/>); null when the status says it all.</summary>
    public string? Note => Format.ShortNote(s);
    // a mod the compile runs through takes the note's place; the tooltip keeps the full reason
    string? ListNote => s.ShaderModLayer ? Loc.T(s.Status == GameStatus.Warmed ? "Note_CompiledThrough" : "Note_CompilesThrough", s.ShaderMod) : Note;
    public string RowNote => Playing ? Loc.T("Common_PlayingNow") + (ListNote is { } n ? " · " + n : "") : ListNote ?? "";
    public bool HasRowNote => RowNote.Length > 0;
    public string RowTip => (Playing ? Loc.T("Common_PlayingNow") + " · " : "") + FullNote;
    public Style StatusStyle => Fmt.Style(s.Status switch
    {
        _ when s.CompileUnreached || PartlyCompiled || ScsKiller.NotReached(s.StatusReason) => "StatusWarn",
        GameStatus.Warmed when Partly => "StatusWarn",
        GameStatus.Warmed => "StatusAccent",
        GameStatus.Ready => "StatusPrimary",
        GameStatus.Unsupported => "StatusMuted",
        _ => "StatusWarn",
    });

    // One action per status (Main.dc.html): Details / Add to queue / Record / disabled Why?
    public bool Queued => queued;
    public bool InAddAllReady => ScsKiller.InAddAllReady(s);
    public bool IsAdd => s.Status is GameStatus.Ready or GameStatus.Stale && !s.CompileUnreached;
    public string AddText => queued ? Loc.T("Common_InQueue") : Loc.T("Common_AddToQueue");
    public bool CanAdd => !queued;
    public bool IsRecord => s.Status == GameStatus.NeedsRecording && !s.RecorderInstalled && s.NoStutter == null && !ScsKiller.NeedsOfflineSession(s) && !ScsKiller.NeedsSteamStart(s) && !s.RiskEligible && !Shared;
    bool Shared => Collections.RecordedBy(s.Game.Id) != null;   // recorded by its collection's first game's recorder, switched on that game's page
    public bool CanRecord => !s.AnyAntiCheat;
    public bool IsDetails => s.Status == GameStatus.Warmed || ScsKiller.IsUnchecked(s) || s.CompileUnreached || ScsKiller.NeedsOfflineSession(s) || ScsKiller.NeedsSteamStart(s) || (s.Status == GameStatus.NeedsRecording && (s.RecorderInstalled || s.NoStutter != null || s.RiskEligible || Shared));
    public bool IsWhy => s.Status == GameStatus.Unsupported && !ScsKiller.IsUnchecked(s);
    public string Reason => s.StatusReason;

    // Play goes through the store (a game the user added: its exe); never for an anti-cheat game (SCSKiller never launches them)
    public bool CanLaunch { get; private set; } = BeforeAfterUi.Launchable(s);
    public bool Compiling => compiling;
    public bool CanPlay => !Playing && !compiling;
    public string PlayText => Playing ? Loc.T("Play_Running") : Loc.T("Play_Play");
    public string PlayTip => Playing ? Loc.T("Play_RunningTip", Name) : compiling ? Fmt.CompilingTip : Fmt.PlayVia(s.Game);

    public override string ToString() => $"{Name}, {StatusText}, {RowTip}";   // list item name for screen readers

    /// <summary>The game's latest state; no change raised when it is the one shown. A re-evaluated state is unequal even
    /// with nothing new (its lists are other instances): the bindings then set the same values again, which shows nothing.</summary>
    public void Update(GameState now, bool nowQueued, bool nowCompiling)
    {
        var icon = Icons.Get(now.Game.ExePath);
        if (now == s && nowQueued == queued && nowCompiling == compiling && icon == Icon) return;
        (s, queued, compiling, Icon) = (now, nowQueued, nowCompiling, icon);
        CanLaunch = BeforeAfterUi.Launchable(s);
        // a new list makes the row build its chips again
        if (TagsOf(s) is var tags && !tags.SequenceEqual(Tags)) Tags = tags;
        Changed();
    }
}

/// <summary>One store's section of the library list (the rows shown after the search filter).</summary>
public sealed class StoreGroup(string store) : ObservableCollection<GameRow>
{
    public const string Recommended = "Recommended to compile";   // the group's key, as LoadLibrarySort stores it: the shown name is translated
    public string Store => store == Recommended ? Loc.T("Library_Recommended") : store;
    public string CountText => Loc.T(Count == 1 ? "Library_CountGame" : "Library_CountGames", Count) + (store == Recommended ? RecommendedNote() : "");
    // a row updated in place (compiled, cleared) changes the count of compiled ones without a collection change
    public void Recount() => OnPropertyChanged(new PropertyChangedEventArgs(nameof(CountText)));
    string RecommendedNote() => this.Count(r => r.State.Status == GameStatus.Warmed) is var done and > 0
        ? Loc.T("Library_RecommendedNote", done) : Loc.T("Library_RecommendedNoteNone");
}

public sealed record HiddenRow(string Id, string Name, string Store)
{
    public string ShowName => Loc.T("Library_ShowInLibrary", Name);
}

public sealed class LibraryVm : Bindable
{
    static readonly LibraryScan firstScan = new();
    readonly BackgroundRead<(CacheUsage Usage, CacheLimit? Limit, long? Capped, long OldDriver, long? Fullest)> cache;

    public LibraryVm() => cache = new("Library cache", () => (App.Core.Vendor.GetCacheUsage(), App.Core.Vendor.GetCacheLimit(), (App.Core.Vendor as AmdBackend)?.AppCache.DxcBytes(),
        App.Core.OldDriverBytes(), (App.Core.Vendor as IntelBackend)?.AppCache.Fullest()), ShowCache);

    public IReadOnlyList<GameRow> Games { get; private set; } = [];   // every game, sorted; the list shows Groups
    /// <summary>The list: rows matching <see cref="Filter"/> per store, empty stores left out. Filtering reuses the rows
    /// (no icon reads, no IO).</summary>
    public ObservableCollection<StoreGroup> Groups { get; } = [];
    string filter = "";
    public string Filter { get => filter; set { if (value.Trim() != filter) { filter = value.Trim(); ApplyFilter(); Changed(); } } }
    LibraryShow show;
    public static readonly string[] ShowLabels =
        [Loc.T("Library_ShowAll"), Loc.T("Library_ShowNotSupported"), Loc.T("Library_ShowReadyToCompile"), Loc.T("Library_ShowCompiled"), Loc.T("Library_ShowCompiledByScs")];
    public int ShowIndex { get => (int)show; set { if (value >= 0 && (LibraryShow)value != show) { show = (LibraryShow)value; ApplyFilter(); Changed(); } } }
    int shown;
    public bool Filtering => filter.Length > 0 || show != LibraryShow.All;
    public bool Searching => filter.Length > 0;
    public string ShownText => Loc.T("Library_ShownCount", shown, Games.Count);
    public bool NoMatch => Filtering && Games.Count > 0 && shown == 0;
    public string NoMatchText => Searching ? Loc.T("Library_NoMatch", filter) : Loc.T("Library_NoStatusMatch");
    /// <summary>The games the user removed from the library, listed while <see cref="ShowHidden"/> is on.</summary>
    public IReadOnlyList<HiddenRow> Hidden { get; private set; } = [];
    public bool HasHidden => Hidden.Count > 0;
    bool showHidden;
    public bool ShowHidden { get => showHidden; set { if (value != showHidden) { showHidden = value; Changed(); } } }
    public bool HiddenShown => showHidden && HasHidden;
    public async void ShowInLibrary(HiddenRow r)
    {
        try { await Task.Run(() => App.Core.ShowGame(r.Id)); }
        catch (ArgumentException) { }   // shown again meanwhile
        Refresh();
    }

    LibrarySort sort = App.Store.LoadLibrarySort();
    /// <summary>A column header's click: its first order, then the reverse. Status goes back to the default order.</summary>
    public void SortBy(LibrarySort first, LibrarySort second)
    {
        sort = sort == first ? second : first;
        try { App.Store.SaveLibrarySort(sort); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // sorted all the same, just not remembered
        Refresh();
    }
    string Arrow(LibrarySort up, LibrarySort down) => sort == up ? " ↑" : sort == down ? " ↓" : "";
    public string GameHeader => Loc.T("Library_HeaderGame") + Arrow(LibrarySort.NameAscending, LibrarySort.NameDescending);
    public string PipelinesHeader => Loc.T("Library_HeaderPipelines") + Arrow(LibrarySort.PipelinesAscending, LibrarySort.PipelinesDescending);
    public string CacheHeader => Loc.T("Library_HeaderCache") + Arrow(LibrarySort.CacheAscending, LibrarySort.CacheDescending);
    public bool Scanning { get; private set; }
    int refreshing;   // Rescan calls not finished: the scan, then a user refresh's server fetches
    public bool Refreshing => refreshing > 0;
    string? refreshNote;
    public string? RefreshNote => (ScanEmpty ? null : Fmt.AfterUpdateLine) ?? refreshNote;
    public bool HasRefreshNote => RefreshNote != null;
    public string? Error { get; private set; }
    public bool HasError => Error != null;
    public string? GpuNotice => Format.GpuNotice(App.Core.Vendor.Gpu, App.Core.Settings.GpuNoticeDismissed, App.Core.Vendor is IntelBackend);
    public bool HasGpuNotice => GpuNotice != null;
    public void DismissGpuNotice()
    {
        App.Core.Settings = App.Core.Settings with { GpuNoticeDismissed = App.Core.Vendor.Gpu.Name };
        Changed();
    }

    public string Summary { get; private set; } = "";
    public int ReadyCount { get; private set; }     // ready, not queued yet and not a no-stutter game
    public int WaitingCount { get; private set; }
    public string AddAllText => Loc.T("Library_AddAllReady", ReadyCount);
    public bool CanAddAll => ReadyCount > 0 && !Scanning;
    public int RecommendedCount { get; private set; }   // stutters without a compile, ready and not queued yet
    public string AddRecommendedText => Loc.T("Library_AddAllRecommended", RecommendedCount);
    public bool CanAddRecommended => RecommendedCount > 0 && !Scanning;
    public bool CannotAddRecommended => !CanAddRecommended;   // the plain, disabled twin of the accent button (theme brushes stay in XAML)
    public string CompileQueueText => Loc.T("Library_CompileQueueCount", WaitingCount);
    public bool CanCompileQueue => WaitingCount > 0;

    public string CacheUsed { get; private set; } = "";
    public string CacheLimitText { get; private set; } = "";
    public double CachePercent { get; private set; }
    public bool CacheWarn { get; private set; }
    public string CacheWarnText { get; private set; } = "";
    public bool CanRaiseLimit => App.Core.Vendor.Caps.CacheSizeConfigurable;
    /// <summary>Below this, what an older NVIDIA driver left (old-type files of desktop apps, a few MB) isn't worth a line.</summary>
    const long OldDriverShown = 1L << 30;
    public string? OldDriverNote { get; private set; }
    public bool HasOldDriver => OldDriverNote != null;

    public string DriverMode => DriverUpdateChoice.Of(App.Core.Settings) switch
    {
        0 => Loc.T("Settings_AskMeFirst"), 1 => Loc.T("Settings_WhenIdleChoice"), 2 => Loc.T("Settings_RightAwayChoice"), _ => Loc.T("Common_Off"),
    };
    public string DriverModeNote => DriverUpdateChoice.Of(App.Core.Settings) switch
    {
        0 => Loc.T("Settings_DriverAskNote"),
        1 => Loc.T("Settings_DriverIdleNote"),
        2 => Loc.T("Settings_DriverRightAwayNote"),
        _ => Loc.T("Settings_DriverOffNote"),
    };
    public string Threads => App.Core.Settings.Threads.ToString();
    public string ThreadsOf => Loc.T("Library_OfThreads", Environment.ProcessorCount);
    public string SpeedNote { get; private set; } = "";

    public void Load()
    {
        Refresh();
        if (firstScan.Start()) Rescan(force: false);
    }


    // The scan message waits 300 ms so a cached scan doesn't flicker it; the list fills when the scan ends.
    bool slowScan, forced;
    public bool ScanEmpty => slowScan && Games.Count == 0;
    public bool ScanBusy => slowScan && Games.Count > 0;
    public string ScanEmptyNote => Fmt.AfterUpdateLine ?? Loc.T("Library_ScanIntro",
        string.Join(", ", ScanStores[..^1]) + Loc.T("Common_And") + ScanStores[^1]);
    static readonly string[] ScanStores = ["Steam", "Epic", "Xbox", "EA", "GOG", "Ubisoft Connect", "Battle.net", "PURPLE", "HoYoPlay", "Gaijin", "Riot Client"];
    public string ScanTitle => Fmt.ScanProgress ?? Loc.T("Library_ScanChangedTitle");
    public string ScanEmptyTitle => Fmt.ScanProgress ?? Loc.T("Library_ScanLooking");
    /// <summary>A scan's step: the last list, which the first scan puts up before it reads the games, else the progress line.</summary>
    public void ScanStep()
    {
        if (Games.Count == 0 && App.Core.Games.Count > 0) Refresh();
        else Changed();
    }
    public string ScanBusyNote => forced ? Loc.T("Library_RescanNote")
        : Loc.T("Library_ScanStep");

    public async void Rescan(bool force = true, bool userRequested = false)
    {
        Scanning = true; refreshing++; forced = force; Error = refreshNote = null; Refresh();   // the summary says "Looking for games…" while nothing is listed
        var scan = force ? App.Core.RescanAsync(CancellationToken.None, userRequested) : App.Core.ScanAsync(CancellationToken.None, userRequested);
        if (await Task.WhenAny(scan, Task.Delay(300)) != scan) { slowScan = true; Changed(); }
        try { await scan; firstScan.Ended(ok: true); }
        catch (Exception e) { Error = Loc.T("Library_ScanFailed", e.Message); firstScan.Ended(ok: false); }
        Scanning = slowScan = false;
        Refresh();
        if (userRequested && Error == null && App.Core is ScsKiller k)
            refreshNote = await k.ServerRefresh switch
            {
                ScsKiller.ServerCheck.TooSoon => Loc.T("Library_ServerTooSoon"),
                ScsKiller.ServerCheck.Unreachable => Loc.T("Library_ServerUnreachable"),
                _ => null,
            };
        refreshing--;
        Changed();
    }

    public void Refresh()
    {
        var core = App.Core;
        var list = Library.Sorted(core.Games, sort).ToList();
        var queue = core.Queue.Where(q => !q.PlanCheck).ToList();
        var queued = queue.Where(Fmt.Active).Select(q => q.GameId).ToHashSet();
        var rows = Games.ToDictionary(r => r.Id);
        Games = [.. list.Select(g =>
        {
            bool q = queued.Contains(g.Game.Id), compiling = Fmt.CompilingSoon(queue, g.Game.Id);
            if (!rows.TryGetValue(g.Game.Id, out var row)) return new GameRow(g, q, compiling);
            row.Update(g, q, compiling);
            return row;
        })];
        ApplyFilter();
        Hidden = [.. core.HiddenGames.Select(g => new HiddenRow(g.Id, g.Name, Fmt.StoreName(g)))];

        int ready = list.Count(g => g.Status is GameStatus.Ready or GameStatus.Stale);
        ReadyCount = Games.Count(r => r.InAddAllReady && !r.Queued);
        RecommendedCount = RecommendedToAdd().Count;
        WaitingCount = queue.Count(q => q.Stage == QueueStage.Waiting);
        var stores = Fmt.Stores.Where(n => n != Fmt.AddedByYou && Games.Any(r => r.StoreName == n)).ToList();
        var storeText = stores.Count > 2 ? Loc.T("Library_StoresCount", stores.Count) : string.Join(Loc.T("Common_And"), stores);
        bool allUnreal = list.Count > 0 && list.All(g => g.Engine?.Family == "Unreal");
        Summary = Scanning && list.Count == 0 ? Loc.T("Library_Looking")
            : Loc.T(list.Count == 1 ? "Library_SummaryOne" : "Library_SummaryMany",
                (allUnreal ? Loc.T("Library_UnrealEnginePrefix") : "") + list.Count,
                storeText.Length > 0 ? Loc.T("Library_FoundIn", storeText) : "", ready);

        cache.Request();

        var rate = ScsKiller.MeasuredRate(list);
        SpeedNote = Loc.T(core.Settings.Priority == WarmPriority.BelowNormal ? "Settings_BelowNormalShort" : "Settings_IdleShort")
                    + (rate is { } r ? Loc.T("Library_MeasuredRate", $"{r:N0}") : "");
        Changed();
    }

    /// <summary>Games -> Groups: same order within a store (<see cref="SortBy"/>). Sections and rows are kept and only moved,
    /// added or removed (<see cref="ListSync"/>): a Reset scrolls the list to the top and builds every row again.</summary>
    void ApplyFilter()
    {
        var visible = Games.Where(r => (filter.Length == 0 || r.Matches(filter)) && Library.Shows(r.State, show)).ToList();
        // known-stutter games go on top (StutterList.Recommended), out of their store's section, compiled or not
        var byId = visible.ToDictionary(r => r.Id);
        var recommended = StutterList.Recommended(visible.Select(r => r.State)).Select(s => byId[s.Game.Id]).ToList();
        if (sort != LibrarySort.Status) recommended = visible.Intersect(recommended).ToList();   // in the column's order
        var groups = visible.Except(recommended).GroupBy(r => r.StoreName)
            .OrderBy(g => Array.IndexOf(Fmt.Stores, g.Key)).Select(g => (g.Key, Rows: g.ToList())).ToList();
        if (recommended.Count > 0) groups.Insert(0, (StoreGroup.Recommended, recommended));
        shown = groups.Sum(g => g.Rows.Count);
        var wanted = groups.ToDictionary(g => g.Key, g => g.Rows.ToHashSet());
        // a row leaves its old section before it joins its new one: in two sections at once it would get two containers
        foreach (var have in Groups)
            for (int i = have.Count - 1; i >= 0; i--)
                if (!wanted.TryGetValue(have.Store, out var keep) || !keep.Contains(have[i])) have.RemoveAt(i);
        var kept = Groups.ToDictionary(g => g.Store);
        ListSync.Sync(Groups, [.. groups.Select(g => kept.GetValueOrDefault(g.Key) ?? new StoreGroup(g.Key))]);
        foreach (var (group, (_, rows)) in Groups.Zip(groups))
        {
            ListSync.Sync(group, rows);
            group.Recount();
        }
    }

    // Capped: AMD's limit is of its DirectX 12 cache alone, which the usage counts with the DirectX 11 one. Fullest: Intel's
    // limit is each game's own, so the fullest game is the one that nears it
    void ShowCache((CacheUsage Usage, CacheLimit? Limit, long? Capped, long OldDriver, long? Fullest) c)
    {
        var (usage, limit, capped, old, _) = c;
        var fullest = limit?.Bytes != null ? c.Fullest : null;
        OldDriverNote = old >= OldDriverShown ? Loc.T("Library_OldDriverNote", Format.Bytes(old)) : null;
        // the Cache page's total: NVIDIA from 621 keeps part of the cache in the games' D3DSCache folders
        var windows = fullest == null ? App.Core.WindowsCacheBytes : 0;
        var up = usage.UpperBound ? "≤ " : "";
        CacheUsed = fullest is { } f ? Format.Bytes(f) : up + Format.Bytes(usage.BytesOnDisk + windows);
        CacheLimitText = windows > 0 ? Loc.T("Cache_DriverLabel", up + Format.Bytes(usage.BytesOnDisk)) + (limit is null ? Loc.T("Cache_LimitUnknownSuffix") : limit.Bytes is { } wl ? Loc.T("Cache_OfLimitSuffix", Format.Bytes(wl)) : Loc.T("Cache_NoLimitSuffix"))
            : limit is null ? Loc.T("Cache_LimitUnknown") : limit.Bytes is { } lb ? fullest is { } fl ? Loc.T("Cache_FullestLimit", Format.Bytes(fl), Format.Bytes(lb))
            : capped is { } cb ? Loc.T("Cache_Dx12OfLimit", Format.Bytes(cb), Format.Bytes(lb)) : Loc.T("Cache_OfLimit", Format.Bytes(lb)) : Loc.T("Cache_NoLimit");
        CachePercent = limit?.Bytes is { } max && max > 0 ? Math.Min(100, 100.0 * (fullest ?? capped ?? usage.BytesOnDisk) / max) : 0;
        CacheWarn = limit?.Bytes is not null && (limit.IsDriverDefault || CachePercent > 75);
        CacheWarnText = limit?.IsDriverDefault == true ? Loc.T("Cache_WarnDefaultLimit") : fullest != null ? Loc.T("Cache_WarnFullest") : Loc.T("Cache_WarnNearlyFull");
        if (Format.CacheOffLine(App.Core.Vendor.Vendor, limit) is { } off) (CacheWarn, CacheWarnText) = (true, off);
        Changed();
    }

    /// <summary>The Recommended section's games that can be queued now (not the ones that need a recording), every row, not the filtered view.</summary>
    List<GameRow> RecommendedToAdd()
    {
        var byId = Games.ToDictionary(r => r.Id);
        return StutterList.Recommended(Games.Select(r => r.State)).Select(s => byId[s.Game.Id]).Where(r => r.IsAdd && !r.Queued).ToList();
    }

    public void AddAllRecommended()
    {
        foreach (var g in RecommendedToAdd()) App.Core.Enqueue(g.Id);
        Refresh();
    }

    public async void Play(GameRow row)
    {
        Error = await BeforeAfterUi.PlayAsync(row.State);
        Changed();
    }

    public void AddAllReady()
    {
        foreach (var g in Games.Where(r => r.InAddAllReady && !r.Queued).ToList()) App.Core.Enqueue(g.Id);
        Refresh();
    }
}

public sealed class DetailVm(string id) : Bindable
{
    GameState s = App.Core.Games.First(g => g.Game.Id == id);
    public GameRow Row { get; private set; } = new(App.Core.Games.First(g => g.Game.Id == id));
    public string? Error { get; set; }
    public bool HasError => Error != null;

    public void Refresh()
    {
        place = InQueue(id);
        var now = App.Core.Games.FirstOrDefault(g => g.Game.Id == id);
        if (now != null && s.Playing && !now.Playing) ReadCaches();   // the game exited: its Windows and pipeline caches grew
        if (now != null && (now != s || Row.Icon != Icons.Get(now.Game.ExePath))) { s = now; Row = new GameRow(now); }   // or its icon failed: the tile
        ReadBlocked();
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
    public string Sub => $"{Path.GetFileName(s.Game.ExePath)} · {Fmt.StoreName(s.Game)}";   // engine and API are tags

    // Status: the state in its colour and why; the actions sit beside it.
    public string StatusGlyph => s.Status switch
    {
        _ when s.CompileUnreached || ScsKiller.IsPartlyCompiled(s) || ScsKiller.NeedsSteamStart(s) => "",
        GameStatus.Warmed => "", GameStatus.Ready => "", GameStatus.NeedsRecording => "", GameStatus.Stale => "", _ => "",
    };
    // The queue page's progress for this game: its number in the run order while it waits, its stage and progress while it runs.
    (QueueItem? Item, int Number) place = InQueue(id);
    static (QueueItem?, int) InQueue(string id)
    {
        var (running, waiting) = Fmt.RunOrder(App.Core.Queue);
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
        _ when ScsKiller.CacheOffRefused(App.Core.Queue, id) => ScsKiller.CacheOffReason,
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
    public bool HasAesKey => App.Core.HasEncryptionKey(id);
    public bool CanChangeAesKey => HasAesKey && !NeedsAesKey;
    public bool NeedsGameKey => ScsKiller.NeedsGameKey(s);
    public static readonly string RemoveAesKeyNote = Loc.T("Detail_RemoveAesKeyNote");
    public string PartlyCompiledWhy => s.LastWarmFailedBy switch
    {
        0 or 1 => Loc.T("Detail_PartlyWhyDriver", "Windows"),
        2 => Loc.T("Detail_PartlyWhyFiles"),
        _ => Loc.T("Detail_PartlyWhyOther"),
    };

    // The manifest is public: a PC without "db" sees that the community database covers the game, and where to get it.
    // Shown, it is the page's only mention of the database.
    public bool HasDbTeaser => s is { InCommunityDb: true, Community: null } && !App.Account.HasDb
        && (s.Status == GameStatus.NeedsRecording || RtAfter || s.Status == GameStatus.Unsupported && !NoAntiCheat && s.StatusReason.StartsWith("needs a recording, which", StringComparison.Ordinal));
    public string DbTeaser => Loc.T(s.CommunityDbPsos == 1 ? "Detail_DbTeaserOne" : "Detail_DbTeaserMany", $"{s.CommunityDbPsos:N0}");
    public string DbTeaserLink => App.Account.SignedIn ? Loc.T("Detail_PatreonMembership") : Loc.T("Settings_SignInPatreon");
    public bool CanCompile => s.Status is GameStatus.Ready or GameStatus.Warmed or GameStatus.Stale || RtPartial;
    public bool ShowMaxCompile => CanCompile && ScsKiller.MaxCompileAdds(App.Core.Vendor.Caps, s.Engine);
    public static readonly string MaxCompileWhy = Loc.T("Detail_MaxCompileWhy");
    public string? GuessNote => s.Engine is { } e && s.Status is GameStatus.Ready or GameStatus.Warmed or GameStatus.Stale ? ScsKiller.GuessNote(e) : null;
    public bool HasGuessNote => GuessNote != null;
    public string? DeliveryLine => ScsKiller.DeliveryLine(s);
    public bool HasDeliveryLine => DeliveryLine != null;
    public string? CleanupLine => ScsKiller.CleanupLine(s);
    public bool HasCleanupLine => CleanupLine != null;
    public string? VulkanRtNote => ScsKiller.VulkanRtNote(s, App.Core.Vendor.Vendor);
    public bool HasVulkanRtNote => VulkanRtNote != null;
    // warmed: nothing urgent, unless only partly (the careful compile is offered); RT: Record comes first; no shader stutter: not needed
    public Style CompileStyle => Fmt.Style((OffersCareful || s.Status != GameStatus.Warmed && !RtPartial) && s.NoStutter == null ? "AccentButtonStyle" : "DefaultButtonStyle");
    bool Queued => App.Core.Queue.Any(q => q.GameId == id && Fmt.Active(q) && !q.PlanCheck);
    public bool CanAdd => !Queued && !BaInProgressAny;
    /// <summary>A pair before Done: a compile or clear of the user's own would spoil a run (Core refuses them too).</summary>
    bool BaInProgressAny => s.BeforeAfter is { Stage: not BeforeAfterStage.Done };
    static readonly string BaBusyTip = Loc.T("Detail_BaBusyTip");
    public string? CompileTip => BaInProgressAny ? BaBusyTip : null;
    public bool CanClearCache => HasCache && !BaInProgressAny;
    bool CompilingSoon => Fmt.CompilingSoon(App.Core.Queue, id);
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
        if (!App.Core.Settings.BeforeAfter) return null;
        try
        {
            var why = App.Core.BeforeAfterBlocked(id);
            return why != null && why == App.Core.Games.First(g => g.Game.Id == id).StatusReason ? Sentence(why) : why;   // a status reason starts lowercase
        }
        catch (Exception e) { return BeforeAfterUi.Describe(e); }
    }
    BeforeAfterInfo? Ba => s.BeforeAfter;
    (bool Shown, bool Enabled, string Note) BaStart => BeforeAfter.StartButton(App.Core.Settings.BeforeAfter, blockedKnown ? blocked : "");
    public bool ShowBa => Ba != null ? App.Core.Settings.BeforeAfter : BaStart.Shown;
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
    public Style BaStyle => Fmt.Style(BaVoided || Ba?.Stage is BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed or BeforeAfterStage.AfterReady ? "AccentButtonStyle" : "DefaultButtonStyle");
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
    public bool HasKept => s.Kept != null && App.Core.Vendor.Vendor == GpuVendor.Amd && App.Core.Settings.KeepCachesAside;
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
    public List<string> NewShadersChoices => [Loc.T("Detail_UseGlobalSetting", NewShadersLabels[(int)NewShaders.Global(App.Core.Settings)]), .. NewShadersLabels];
    public int NewShadersIndex => s.NewShadersOverride is { } m ? (int)m + 1 : 0;
    // 0 = the global setting, 1 = on, 2 = off
    public List<string> RayTracingChoices => [Loc.T("Detail_DefaultChoice", Loc.T(App.Core.Settings.CompileRayTracing ? "Settings_OnSetting" : "Settings_OffSetting")), Loc.T("Settings_On"), Loc.T("Common_Off")];
    public int RayTracingIndex => s.RayTracingOverride switch { null => 0, true => 1, false => 2 };
    public bool ShowTraversal => TraversalFix.Applies(s, App.Core.Vendor.Caps);
    public List<string> TraversalChoices => TraversalFix.GameChoices(App.Core.Settings, s);
    public int TraversalIndex => TraversalFix.GameIndex(s);
    public string TraversalNote => TraversalFix.GameNote(s, App.Core.Vendor.Gpu);
    public bool TraversalLowVram => TraversalFix.LowVram(App.Core.Vendor.Gpu) == true;
    public string TraversalWarning => TraversalFix.LowVramWarning;
    // no plan, or one from before inline ray tracing was counted: unknown, so the choice shows
    public bool ShowRayTracing => P?.RtInline == null || s.RayTracingOverride != null || ScsKiller.HasRayTracing(P);
    public List<string> CacheCapChoices => [Loc.T("Common_NoLimit"), .. ScsKiller.CacheCapsGB.Select(gb => $"{gb} GB")];
    public int CacheCapIndex => s.CacheCapGB is { } gb ? Array.IndexOf(ScsKiller.CacheCapsGB, gb) + 1 : 0;
    public bool ShowCacheCap => ScsKiller.HasCacheCap(s);
    // Windows' shader cache of the game, read off the UI thread: Clear cache clears it too, so it can be the only cache to clear.
    BackgroundRead<long>? otherCaches;
    long otherBytes;
    public void ReadCaches() => (otherCaches ??= new("game caches", OtherCacheBytes, b => { otherBytes = b; Changed(); })).Request();
    long OtherCacheBytes()
    {
        try { return App.Core.GameCaches(id).Where(p => p.Name != ScsKiller.DriverPart).Sum(p => p.Bytes); }
        catch (Exception) { return 0; }   // e.g. the game is gone after a rescan
    }
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
    public bool HasMaximumLine => MaximumSuggested || s.MaximumForGame == true && !App.Core.Settings.MaximumPlans;
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
    public string ClearCacheTip => BaInProgressAny ? BaBusyTip : HasCache ? Loc.T("Detail_ClearCacheTip", Loc.T("Detail_AndWindows"))
        : Loc.T("Detail_ClearCacheTipNone");

    // Rate this compile (CompatReport) and what players with a similar system reported (CompatFeed)
    string? ReportWhyNot => CompatReport.WhyNot(s, ScsKiller.PackGpu(App.Core.Vendor.Vendor));
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
        || s.Engine is { Encrypted: false } e && SCSKiller.Core.Carved.CarvedReader.Carved(e) && s.ApiDetected == SCSKiller.Core.Unreal.UnrealRhi.Ambiguous;
    public bool? ApiPending { get; set; }
    public bool CanPickApi => ApiPending == null && !App.Core.Queue.Any(q => q.GameId == id && Format.Running(q));   // its plan check too: the core refuses a change meanwhile
    IReadOnlyList<DxApi> ApiChoices => ScsKiller.ApiChoices(s, App.Core.Vendor.Caps);
    public int ApiIndex => s.ApiOverride is { } a ? ApiChoices.ToList().IndexOf(a) + 1 : 0;
    /// <summary>The API of the picker's item <paramref name="i"/>; null = Auto.</summary>
    public DxApi? ApiAt(int i) => i <= 0 ? null : ApiChoices[i - 1];
    string[]? apiItems;
    public string[] ApiItems
    {
        get
        {
            var auto = s.Engine?.GraphicsApi.EndsWith(ScsKiller.RecordingSuffix, StringComparison.Ordinal) == true ? Loc.T("Detail_AutoFromRecording", Dx(s.Engine.GraphicsApi))
                : s.ApiDetected == SCSKiller.Core.Unreal.UnrealRhi.Ambiguous ? Loc.T("Detail_AutoDx11And12")
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
        : Loc.T("Detail_AntiCheatNever", ScsKiller.AntiCheatName(s), RecorderDll);
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
            .Concat(Mw.Any(t => t.Label == "DLSS") && App.Core.Vendor.Vendor == GpuVendor.Nvidia ? [Loc.T("Detail_DlssByDriver")] : []).ToList() is { Count: > 0 } also
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
    public string RecordTip => RtPartial || RtAfter ? Loc.T("Detail_TurnOnRtPlay", TurnOnAndPlay, Enough ? "" : Loc.T("Detail_ForAbout5Min")) + ScsKiller.RtWhy(App.Core.Vendor.Caps, s.Engine)
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
        : Dx11Only && Pct is null or 100 ? Loc.T(Compiled ? "Detail_Covers" : "Detail_WillCover") + Loc.T("Detail_CoversDx11") + (SCSKiller.Core.Planning.Planner.D3D11CoversVs(App.Core.Vendor.Caps) || P.D3D11Draws > 0 ? "" : Loc.T("Detail_CoversExceptVs"))
        : Pct is not { } pct ? Loc.T("Detail_CoverageAgain")
        : Loc.T(Compiled || s.NewOnly ? "Detail_Covers" : s.WarmedAt != null ? "Detail_LastCovered" : "Detail_WillCover")
          + ScsKiller.CoverageWhat(s, pct);
    // the headline's own words under the ring while no recording has confirmed the engine rule
    public bool HasRuleNote => Pct == 100 && !Dx11Only && ScsKiller.RuleUnconfirmed(s);
    bool Rt => ScsKiller.NeedsRtRecording(s);
    bool RtUnseen => s.RtUnseen;
    bool RtInline => ScsKiller.RtInlineCovers(P);
    bool Compiled => s.WarmedAt != null && s.Status != GameStatus.Stale;

    /// <summary>Where the plan's pipelines come from, in a player's words; the recording's row carries the community note,
    /// and without one the community database's status is its own row. Ray tracing: covered or what it needs.</summary>
    public IReadOnlyList<DetailRow> Sources => P is not { } p ? [] : new DetailRow?[]
    {
        p.Generated > 0 ? new(ScsKiller.BothApis(p) ? Loc.T("Detail_SrcDx12Files") : Loc.T("Detail_SrcGameFiles"), Fmt.N(p.Generated),
            Pct == 100 && s.ShaderCount is { } n && p.Generated > n ? Loc.T("Detail_MoreThanShaders") : null) : null,
        p.Recorded > 0 && !Vk ? new(RecordingSource, Fmt.N(p.Recorded), CommunityLine) : null,
        Vk && p.FromDownload > 0 ? new(Loc.T("Detail_SrcSteamDownload"), Fmt.N(p.FromDownload)) : null,
        Vk && p.FromCommunity > 0 ? new(Loc.T("Detail_SrcCommunityDb"), Fmt.N(p.FromCommunity)) : null,
        Vk && FromPlay > 0 ? new(FromPlayLabel, Fmt.N(FromPlay), CommunityLine) : null,
        p.CacheBudgetLeftOut is > 0 and var left ? new(ScsKiller.CacheBudgetTitle(App.Core.Vendor.Caps, s.Engine, s.PlanCacheCapGB), Fmt.N(left), ScsKiller.CacheBudgetNote(App.Core.Vendor.Caps, s.Engine, s.PlanCacheCapGB)) : null,
        s.Official is { } o ? new(Loc.T("Detail_SrcOfficial"), $"{o.Psos:N0} pipelines", Loc.T("Detail_SrcOfficialNote", o.DownloadedAt.LocalDateTime.ToString("d"))) : null,
        s.Community == null && s.InCommunityDb is { } inDb && !HasDbTeaser ? new(Loc.T("Detail_SrcTheCommunityDb"), Loc.T(inDb ? "Detail_SrcInDb" : "Detail_SrcNotInDb")) : null,
        p.MiddlewareItems > 0 ? new(p.MiddlewareSharedItems == 0 ? Loc.T("Detail_SrcUpscalersLearned")
            : p.MiddlewareSharedItems == p.MiddlewareItems ? Loc.T("Detail_SrcUpscalersShared") : Loc.T("Detail_SrcUpscalersBoth"), Fmt.N(p.MiddlewareItems)) : null,
        p.D3D11Shaders > 0 ? new(ScsKiller.BothApis(p) ? Loc.T("Detail_SrcDx11Files") : Loc.T("Detail_SrcDx11"), Fmt.N(p.D3D11Shaders)) : null,
        p.RtLibraries > 0 && s.RtSkipped ? new(Loc.T("Detail_RayTracing"), Loc.T("Detail_RtSkipped"))
        : p.RtLibraries > 0 ? new(Loc.T("Detail_RayTracedEffects"), s.Engine?.NoRayTracing == true ? Loc.T("Detail_RtOffInGame") : s.Engine?.NoRtPipelines == true ? Loc.T("Detail_RtInlineFiles") : p.RtUncovered == 0 ? Loc.T("Detail_RtCovered") : s.RtToPlan ? Loc.T("Detail_RtBeingChecked") : RtUnseen ? Loc.T("Detail_RtNotSeen") : RtInline ? Loc.T("Detail_RtInlineCovered") : !Rt ? Loc.T("Detail_RtMostlyCovered") : CanRecord ? Loc.T("Detail_RtNeedRecording") : Loc.T("Detail_RtNotCompiled")) : null,
    }.OfType<DetailRow>().ToList();
    public bool HasSources => Sources.Count > 0;

    // a Vulkan plan reads no game files: its pipelines are Steam's download and what play on this PC recorded
    bool Vk => s.Engine is { } e && Core.Vulkan.VulkanPlan.IsVulkan(e);
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
        RtUnseen ? Sentence(ScsKiller.RtUnseenNote) + "." : RtInline ? Sentence(ScsKiller.RtInlineNote) + "." : !Rt || tipAsks ? null : CanRecord ? (HasDbTeaser ? ScsKiller.RtNeedsRecording : ScsKiller.RtNote(s.InCommunityDb, App.Account.HasDb)) + "."
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
    // A geometry can't be shared between two Paths: each arc gets its own (120 px box, 12 px stroke).
    public Geometry? GoodArc => GoodCoverage ? Arc() : null;
    public Geometry? LowArc => LowCoverage ? Arc() : null;
    Geometry Arc()
    {
        const double c = 60, r = 54;
        double a = Math.Min(Pct ?? 0, 99.99) / 100 * 2 * Math.PI;
        var figure = new PathFigure { StartPoint = new(c, c - r) };
        figure.Segments.Add(new ArcSegment { Point = new(c + r * Math.Sin(a), c - r * Math.Cos(a)), Size = new(r, r), IsLargeArc = a > Math.PI, SweepDirection = SweepDirection.Clockwise });
        var g = new PathGeometry();
        g.Figures.Add(figure);
        return g;
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
        P != null && !Vk ? new(Loc.T("Detail_SrcRecorded"), Fmt.N(P.Recorded)) : null,
        Vk && P is { FromDownload: { } dl } ? new(Loc.T("Detail_SrcSteamDownload"), Fmt.N(dl)) : null,
        Vk && P is { FromCommunity: { } fc } ? new(Loc.T("Detail_SrcCommunityDb"), Fmt.N(fc)) : null,
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
        s.DeliveredShaders is { } asd ? new(Loc.T("Detail_DtAsdShaders"), Format.Bytes(asd)) : null,
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
            NotInGame > 0 && P is { FromDownload: > 0 } ? Loc.T("Detail_SkippedDownload", $"{NotInGame:N0}", ScsKiller.DownloadSkipped)
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
        : (earlier = (s, App.Core.FrameHistory(id).Where(e => e.LaunchUnixMs != F?.LaunchUnixMs).Select(e => SessionRow(e, FramesOnly)).ToList())).Rows;   // each row's own D3D11 decides the rest
    (GameState? For, IReadOnlyList<DetailRow> Rows) earlier = (null, []);
    public bool HasEarlierSessions => EarlierSessions.Count > 0;
    // a cleared recording takes the last launch's frames, not the history
    public bool HasFramesCard => HasFrames || HasEarlierSessions;
    static DetailRow SessionRow(FrameSession e, bool unsplit) => new(
        $"{DateTimeOffset.FromUnixTimeMilliseconds(e.LaunchUnixMs).LocalDateTime:g} · {Format.Duration(e.Duration)}",
        Format.Session(e, unsplit));

    // the frames of play, between startup and quitting, whatever slowed them (a load in play too)
    static IEnumerable<Hitch> InPlay(FrameReport f) => f.Hitches.Where(h => FrameLog.InPlay(h.At.TotalMilliseconds, h.Ms, f.Startup.TotalMilliseconds) && h.Cause != HitchCause.Quitting);
    static string Ms(double ms) => Format.Ms(ms);
    static string Cause(HitchCause c) => c switch { HitchCause.Shader => Loc.T("Graph_ShaderCompile"), HitchCause.Other => Loc.T("Graph_OtherHitch"), HitchCause.Quitting => Loc.T("Graph_Quitting"),
        HitchCause.LoadingShaders => Loc.T("Graph_LoadingCompiling"), _ => Loc.T("Graph_Loading") };

    public GridLength LibraryWidth => new(L?.FromGameLibrary ?? 0, GridUnitType.Star);
    public GridLength HitsWidth => new(L?.CacheHits ?? 0, GridUnitType.Star);
    public GridLength CompilesWidth => new(L?.Compiles ?? 0, GridUnitType.Star);
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
    public bool FramesOnly => ScsKiller.Frames11Play(s.Engine, App.Core.Vendor.Caps);
    // its switch waits for the pair to end
    bool PairFramesOnly => ScsKiller.Frames11(s.Engine, App.Core.Vendor.Caps) && s.BeforeAfter is { Stage: not BeforeAfterStage.Done };
    public bool RecordOn => ShowOffline ? OfflineOn : !PairFramesOnly && (RecordPending ?? s.RecorderEffective);
    public bool CanToggleRecord => ShowOffline ? OfflinePending == null && !s.OfflineRunning
        : RecordPending == null && AlongsidePending == null && NoAntiCheat && s.RecorderSkip == null && !PairFramesOnly;
    public bool ShowUseDefault => RecordPending == null && s.RecorderOverride != RecorderOverride.Default && s.RecorderSkip == null && !PairFramesOnly && !s.RecorderForeign;
    public string UseDefaultText => Loc.T("Detail_UseDefaultChoice", Loc.T(ScsKiller.RecordsByDefault(App.Core.Settings, s) ? "Settings_OnSetting" : "Settings_OffSetting"));
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
        ? Loc.T("Detail_RecorderNotAvailableTamper", ScsKiller.AntiCheatName(s), RecorderDll)
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
    public string RecordingPausedText => ScsKiller.PausedNote(App.Core.Settings) + Loc.T("Detail_RaiseLimitOrClear");
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
        try { await Task.Run(() => App.Core.RefreshGame(id)); Error = null; }
        catch (Exception e) { Error = e.Message; }
        Refreshing = false;
        ReadCaches();
        Refresh();
    }
}


/// <summary>A label and its value (the detail page's sources and Details); Note: a line under the label.</summary>
public sealed record DetailRow(string Label, string Value, string? Note = null)
{
    public bool HasNote => Note != null;
}

/// <summary>A queue row: the running item (1), a waiting item (2..) or a finished one (Number 0).</summary>
public sealed record QueueRow(string Id, int Number, string Name, string Exe, string Note, string Time, string Cache, QueueStage Stage,
    bool First = false, bool Last = false)
{
    public ImageSource? Icon { get; } = Icons.Get(Exe);   // a field: an icon that failed makes the row unequal, so Sync replaces it
    public bool NoIcon => Icon is null;
    public Brush Tile => GameRow.TileOf(Name);
    public string Initials => Name.Length > 0 ? GameRow.InitialsOf(Name) : "";
    public bool CanUp => !First;
    public bool CanDown => !Last;
    public string Glyph => Stage switch { QueueStage.Done => "\uE73E", QueueStage.Failed => "\uE7BA", _ => "\uE71A" };   // check, warning, stop
    public string TopLabel => Loc.T("Queue_MoveTopName", Name);
    public string BottomLabel => Loc.T("Queue_MoveBottomName", Name);
    public string UpLabel => Loc.T("Queue_MoveUpName", Name);
    public string DownLabel => Loc.T("Queue_MoveDownName", Name);
    public string RemoveLabel => Loc.T("Queue_RemoveName", Name);
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

    public QueueVm() => cache = new("Queue cache", () => (App.Core.Vendor.GetCacheUsage(), App.Core.Vendor.GetCacheLimit(), (App.Core.Vendor as AmdBackend)?.AppCache.DxcBytes()),
        c => { driver = c; ShowCache(); Changed(); });
    Settings S { get => App.Core.Settings; set { App.Core.Settings = value; Changed(); } }

    public ObservableCollection<QueueRow> Waiting { get; } = [];
    public ObservableCollection<QueueRow> Finished { get; } = [];
    public bool Dragging { get; set; }   // don't rebuild Waiting under a drag in progress

    public QueueRow Current { get; private set; } = new("", 1, "", "", "", "", "", QueueStage.Waiting);
    public bool HasCurrent => cur != null;
    public bool Idle => cur == null && !App.Core.QueueRunning;   // a running queue's button is Stop, also while it waits to start an item
    public bool CanStop => cur != null || App.Core.QueueRunning;
    public bool Empty => cur == null && Waiting.Count == 0;
    public bool HasFinished => Finished.Count > 0;
    public bool CanClear => Waiting.Count > 0 || Finished.Count > 0;
    public bool CanStart => !App.Core.QueueRunning && Waiting.Count > 0;
    public bool CanRaiseLimit => ScsKiller.CanRaiseCacheLimit(App.Core.Vendor.Caps, driver?.Limit);
    public string Summary { get; private set; } = "";
    public string? PlanChecks { get; private set; }   // ScsKiller.PlanCheckLine
    public bool HasPlanChecks => PlanChecks != null;

    public string CurrentDetail => cur is null ? "" : Fmt.Stage(cur) + (Fmt.ProgressText(cur) is { } p ? " · " + p : "") + (Fmt.Started(cur) is { } at ? " · " + at : "");
    public bool Indeterminate => Fmt.Indeterminate(cur);
    public double Percent => Fmt.Percent(cur);
    public string PauseText => cur?.Stage == QueueStage.Paused ? Loc.T("Common_Resume") : Loc.T("Common_Pause");
    public bool CacheWarn { get; private set; }
    public string CacheWarnText { get; private set; } = "";
    public string RuntimeWarnText { get; private set; } = "";
    public bool RuntimeWarn => RuntimeWarnText.Length > 0;

    public double Threads { get => Used; set { if ((int)value != Used) S = S with { Threads = (int)value }; } }
    int Used => ScsKiller.UsedThreads(S.Threads);
    public string ThreadsText => Loc.T("Queue_ThreadsOf", Used, Environment.ProcessorCount);
    public double MaxThreads => Environment.ProcessorCount;
    public int Priority { get => (int)S.Priority; set { if (value >= 0 && value != (int)S.Priority) S = S with { Priority = (WarmPriority)value }; } }
    public string CpuSummary => Loc.T("Queue_CpuSummary", Used, Environment.ProcessorCount, Loc.T(S.Priority == WarmPriority.BelowNormal ? "Common_BelowNormalLower" : "Common_IdleLower"));

    public void Refresh()
    {
        var core = App.Core;
        var all = core.Queue;
        var queue = all.Where(q => !q.PlanCheck).ToList();
        PlanChecks = ScsKiller.PlanCheckLine(all);
        var games = core.Games.ToDictionary(g => g.Game.Id);

        TimeSpan? Left(QueueItem q) => Fmt.Left(q, games.GetValueOrDefault(q.GameId));
        QueueRow Row(QueueItem q, int number, bool first = false, bool last = false)
        {
            var g = games.GetValueOrDefault(q.GameId);
            var time = q.Stage == QueueStage.Done ? Format.Duration(g?.LastWarmTime) : Left(q) is { } t ? Format.Duration(t) + (q.Progress?.PerSecond > 0 ? Loc.T("Queue_LeftSuffix") : "") : Format.Dash;
            return new(q.GameId, number, g?.Game.Name ?? q.GameId, g?.Game.ExePath ?? "", NoteOf(q, g), time, g is null ? Format.Dash : Fmt.Cache(g), q.Stage, first, last);
        }

        (cur, var waiting) = Fmt.RunOrder(queue);
        if (cur != null) Current = Row(cur, 1);
        int first = cur != null ? 2 : 1;
        if (!Dragging) Sync(Waiting, waiting.Select((q, i) => Row(q, first + i, i == 0, i == waiting.Count - 1)));
        Sync(Finished, queue.Where(q => !Fmt.Active(q)).Select(q => Row(q, 0)));

        var pending = queue.Where(Fmt.Active).ToList();
        var total = TimeSpan.FromTicks(pending.Sum(q => Left(q)?.Ticks ?? 0));
        bool unknown = pending.Any(q => Left(q) is null && !ScsKiller.Stalled(q));
        Summary = pending.Count == 0 ? Loc.T("Queue_NothingQueued")
            : Loc.T(pending.Count == 1 ? "Queue_SummaryOne" : "Queue_SummaryMany", pending.Count,
              total > TimeSpan.Zero ? Loc.T("Queue_AboutTotal", Format.Duration(total)) + (unknown ? Loc.T("Queue_PlusPlans") : "") : Loc.T("Queue_TimeKnownOncePlans"))
              + (cur == null && !core.QueueRunning ? Loc.T("Queue_NotStarted") : "");

        planned = pending.Sum(q => games.GetValueOrDefault(q.GameId)?.EstimatedCacheBytes ?? 0);
        var queued = pending.Select(q => games.GetValueOrDefault(q.GameId)).OfType<GameState>().ToList();
        RuntimeWarnText = ScsKiller.QueueRuntimeWarning(queued);
        growth = queued.Select(g => (g.Game.Name, ScsKiller.CacheGrowth(g))).ToList();
        sizes = queued.Select(g => (ScsKiller.CacheGrowth(g), g.EstimatedCacheBytes ?? 0)).ToList();
        ShowCache();
        Changed();
        cache.Request();
    }

    void ShowCache()
    {
        if (driver is not { } d) return;   // not read yet
        CacheWarnText = (d.Capped is { } used
            ? ScsKiller.LargeCompileWarning(sizes, App.Core.Vendor.Vendor) ?? AmdAppCache.QueueWarning(used, growth)   // one game over the cap is always over what's free too
            : ScsKiller.QueueCacheWarning(sizes, d.Usage.BytesOnDisk, planned, d.Limit?.Bytes, App.Core.Vendor.Vendor, App.Core.Vendor.Caps.PerGameCacheCap != null)) ?? "";
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
        QueueStage.Failed => Loc.T("Queue_FailedWith", q.Error),
        QueueStage.Stopped when q.Progress is { } p => Loc.T("Queue_StoppedAt", $"{p.Done:N0}", $"{p.Total:N0}") + (q.Note is { } n ? " · " + n : ""),
        QueueStage.Stopped => Loc.T("Queue_Stopped"),
        QueueStage.Done => q.Note is { } n ? Loc.T("Queue_DoneWith", n) : Loc.T("Queue_Done"),
        _ when q.Note != null => q.Note,
        _ => g?.Plan is { } plan ? Loc.T("Queue_NPipelines", $"{plan.Recorded + plan.Generated + plan.MiddlewareItems:N0}") + (plan.D3D11Shaders > 0 ? Loc.T("Queue_PlusDx11", $"{plan.D3D11Shaders:N0}") : "") : Loc.T("Queue_PlanFirst"),
    };

    public void PauseOrResume() => Fmt.PauseOrResume(cur);
}

/// <summary>A drive of Settings' per-drive HDD mode.</summary>
public sealed class HddDriveRow(string drive, bool on, Action<string, bool> set)
{
    public string Drive => drive;
    public bool? On { get => on; set { if (value is { } v && v != on) set(drive, v); } }
}

public sealed class SettingsVm : Bindable
{
    public static readonly (string Label, CacheLimit Limit)[] Sizes =
    [
        ("512 MB", new(512L << 20, false)), ("1 GB", new(1L << 30, false)), ("4 GB", new(4L << 30, false)), ("5 GB", new(5L << 30, false)),
        ("10 GB", new(10L << 30, false)), ("12 GB", new(12L << 30, false)), (Loc.T("Cache_SizeDriverDefault", "16 GB"), new(16L << 30, true)), ("24 GB", new(24L << 30, false)),
        ("32 GB", new(32L << 30, false)), ("48 GB", new(48L << 30, false)), ("64 GB", new(64L << 30, false)), ("100 GB", new(100L << 30, false)),
        (Loc.T("Common_Unlimited"), new(null, false)),
    ];
    static readonly int DefaultSize = Array.FindIndex(Sizes, s => s.Limit.Bytes == 100L << 30);
    public static readonly string[] SizeLabels = Sizes.Select(s => s.Label).ToArray();

    Settings S { get => App.Core.Settings; set { App.Core.Settings = value; Refresh(); } }
    IGpuVendorBackend V => App.Core.Vendor;
    // The driver cache's limit and usage, the drive's free space and NVIDIA's Auto Shader Compilation, read off the UI thread; null until the first read.
    (CacheLimit? Limit, CacheUsage Usage, string Free, AutoShaderState? Auto)? read;
    readonly BackgroundRead<(CacheLimit?, CacheUsage, string, AutoShaderState?)> cache;
    long? amdUsed;   // AMD: the DirectX 12 cache, which its fixed limit counts

    public SettingsVm()
    {
        cache = new("Settings cache", () =>
        {
            var usage = V.GetCacheUsage();
            string free;
            drive = DriveOf(usage);
            amdUsed = (V as AmdBackend)?.AppCache.DxcBytes();
            try { free = Format.Bytes(new DriveInfo(drive).AvailableFreeSpace); } catch (Exception) { free = Format.Dash; }
            return (V.GetCacheLimit(), usage, free, V.GetAutoShaderCompilation());
        }, r =>
        {
            // Preselect the current custom limit, else 100 GB (the mockup's suggestion over the 16 GB default).
            if (read == null) SizeIndex = Array.FindIndex(Sizes, s => !s.Limit.IsDriverDefault && s.Limit == r.Item1) is >= 0 and var i ? i : DefaultSize;
            // The checkbox and level show the driver's state after every read (an apply's re-read included); Medium when off.
            AutoOn = r.Item4 is { Level: not AutoShaderCompilation.Off };
            AutoLevelIndex = r.Item4 is { Level: not AutoShaderCompilation.Off and var l } ? (int)l - 1 : 1;
            read = r;
            Changed();
        });
        cache.Request();
    }

    public string CacheTitle => Loc.T("Cache_VendorSizeTitle", Fmt.Vendor(V.Vendor));
    public bool IsNvidia => V.Vendor == GpuVendor.Nvidia;

    public static readonly string[] AutoLevels = [Loc.T("Common_Low"), Loc.T("Common_Medium"), Loc.T("Common_High")];   // AutoShaderCompilation.Low..High, as the NVIDIA App names them
    public bool? AutoOn { get; set; }
    public int AutoLevelIndex { get; set; } = 1;
    public AutoShaderCompilation AutoChoice => AutoOn == true ? (AutoShaderCompilation)(Math.Clamp(AutoLevelIndex, 0, 2) + 1) : AutoShaderCompilation.Off;
    public string AutoShaderText => read == null ? Format.Dash : read.Value.Auto switch
    {
        null => Loc.T("Cache_AutoCantRead"),
        { Level: AutoShaderCompilation.Off } => Loc.T("Cache_AutoNowOff"),
        { TaskReady: true, Level: var l } => Loc.T("Cache_AutoNowOn", l.ToString()),
        _ => Loc.T("Cache_AutoTaskBroken"),
    };
    public string? AutoMessage { get; set; }
    public Microsoft.UI.Xaml.Controls.InfoBarSeverity AutoSeverity { get; set; }
    public bool HasAutoMessage => AutoMessage != null;
    public string CacheIntro => PerGame ? Loc.T("Cache_IntroPerGame")
        : (IsNvidia ? Loc.T("Cache_IntroNvidia") : Loc.T("Cache_IntroGlobal"))
        + (KeptOn ? Loc.T("Cache_IntroKept") : Loc.T("Cache_IntroFull"));
    public bool Configurable => V.Caps.CacheSizeConfigurable;
    public bool NotConfigurable => !Configurable;
    public string? AmdAlwaysOn => ScsKiller.AmdAlwaysOnLine(V);
    public bool HasAmdAlwaysOn => AmdAlwaysOn != null;
    public string NotConfigurableText => V.Vendor == GpuVendor.Amd
        ? Loc.T("Cache_AmdFixedA", Format.Bytes(AmdAppCache.DxcCacheCap))
          + Loc.T("Cache_AmdFixedB") + (KeptOn ? Loc.T("Cache_KeptBack") : Loc.T("Cache_AmdShowStale"))
        : V.Vendor == GpuVendor.Intel
        ? Loc.T("Cache_IntelFixedA", Format.Bytes(IntelBackend.PerGameCap))
          + Loc.T("Cache_IntelFixedB")
        : Loc.T("Cache_CantChangeA", Fmt.Vendor(V.Vendor))
          + Loc.T("Cache_CantChangeB");

    public string CurrentLimit => read == null ? Format.Dash : read.Value.Limit switch
    {
        null => Loc.T("Cache_Unknown"),
        { Bytes: null } => Loc.T("Common_Unlimited"),
        { Bytes: 0 } => Loc.T("Cache_Disabled"),
        { Bytes: { } b, IsDriverDefault: var d } => Format.Bytes(b) + (PerGame ? Loc.T("Cache_PerGameSuffix") : "") + (d ? Loc.T("Cache_DriverDefaultSuffix") : ""),
    };
    bool PerGame => V.Caps.PerGameCacheCap != null;
    public string Used => read is { Usage: var u } ? (u.UpperBound ? "≤ " : "") + Format.Bytes(u.BytesOnDisk) : Format.Dash;
    public bool HasAmdUse => amdUsed != null;
    public double AmdPercent => amdUsed is { } b ? Math.Min(100, 100.0 * b / AmdAppCache.DxcCacheCap) : 0;
    public string AmdUse => amdUsed is { } b ? Loc.T("Cache_AmdDx12Use", Format.Bytes(b), Format.Bytes(AmdAppCache.DxcCacheCap))
        + (b >= AmdAppCache.DxcCacheCap * 9 / 10 ? Loc.T("Cache_AmdNearlyFull") : "") : "";
    public bool IsAmd => V.Vendor == GpuVendor.Amd;
    bool KeptOn => IsAmd && S.KeepCachesAside;
    public bool KeepCachesAside { get => S.KeepCachesAside; set { if (value != S.KeepCachesAside) S = S with { KeepCachesAside = value }; } }
    public static IReadOnlyList<GameState> KeptGames => App.Core.Games.Where(g => g.Kept != null).OrderByDescending(g => g.Kept!.Frees).ThenBy(g => g.Game.Name).ToList();
    public bool HasKept => S.KeepCachesAside && KeptGames.Count > 0;
    public string KeptSummary => KeptGames is var k && k.Count > 0
        ? Loc.T(k.Count == 1 ? "Cache_KeptSummaryOne" : "Cache_KeptSummaryMany", k.Count, Format.Bytes(k.Sum(g => g.Kept!.Bytes)), Format.Bytes(k.Sum(g => g.Kept!.Frees))) : "";
    // through a link (the cache moved to another drive with a junction): the drive its files are on
    static string DriveOf(CacheUsage? u) => Path.GetPathRoot(u?.Path is { Length: > 0 } p ? GameFiles.FinalPath(p) ?? p : null) is { Length: > 0 } r ? r
        : Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!;
    string? drive;   // read with the usage: resolving a link opens the folder
    public string FreeLabel => Loc.T("Cache_FreeOn", (drive ?? DriveOf(null)).TrimEnd('\\'));
    public string Free => read?.Free ?? Format.Dash;

    public int SizeIndex { get; set; } = DefaultSize;
    public string? ApplyMessage { get; set; }
    public Microsoft.UI.Xaml.Controls.InfoBarSeverity ApplySeverity { get; set; }
    public bool HasApplyMessage => ApplyMessage != null;
    public void Refresh()
    {
        Changed();
        cache.Request();
    }

    public int DriverMode
    {
        get => DriverUpdateChoice.Of(S);
        set
        {
            if (value < 0 || value == DriverMode) return;
            S = DriverUpdateChoice.With(S, value);
            try { App.Core.ApplyDriverUpdateMode(); }  // user action: registers or removes the sign-in check
            catch (Exception e) { ApplyMessage = Loc.T("Settings_CantUpdateCheck", e.Message); ApplySeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error; Changed(); }
        }
    }
    public bool? PauseWhileGaming { get => S.PauseWhileGaming; set { if (value is { } v && v != S.PauseWhileGaming) S = S with { PauseWhileGaming = v }; } }
    public double BackgroundThreads { get => UsedBackground; set { if ((int)value != UsedBackground) S = S with { BackgroundThreads = (int)value }; } }
    int UsedBackground => ScsKiller.UsedThreads(S.BackgroundThreads);
    public string BackgroundThreadsText => Loc.T("Queue_ThreadsOf", UsedBackground, Environment.ProcessorCount);
    public double MaxThreads => Environment.ProcessorCount;
    public bool HasLastRebuilt => LastRebuilt != "";
    public string LastRebuilt => App.Core.Games.Where(g => g.WarmedAt != null && g.WarmedDriverVersion != null).MaxBy(g => g.WarmedAt)?.WarmedDriverVersion is { } v ? Loc.T("Settings_LastRebuilt", v) : "";

    public bool? MaximumPlans { get => S.MaximumPlans; set { if (value is { } v && v != S.MaximumPlans) S = S with { MaximumPlans = v }; } }
    public bool? CompileRayTracing { get => S.CompileRayTracing; set { if (value is { } v && v != S.CompileRayTracing) S = S with { CompileRayTracing = v }; } }
    public bool? ShareRecordings { get => S.ShareRecordings; set { if (value is { } v && v != S.ShareRecordings) S = S with { ShareRecordings = v }; } }
    public bool? ShareDiagnostics { get => S.ShareDiagnostics; set { if (value is { } v && v != S.ShareDiagnostics) S = S with { ShareDiagnostics = v }; } }
    public bool? ActiveCheck { get => Core.App.ActiveCheck.Allowed(S); set { if (value is { } v && v != Core.App.ActiveCheck.Allowed(S)) S = Core.App.ActiveCheck.Answered(S, v); } }
    public void GamesChanged() => Changed();   // no driver-cache re-read (Refresh)
    public bool RecordAllGames { get => S.RecordAllGames; set { if (value != S.RecordAllGames) S = S with { RecordAllGames = value }; } }   // the core reconciles off this thread
    public bool? RecordAntiCheatGames { get => S.RecordAntiCheatGames; set { if (value is { } v && v != S.RecordAntiCheatGames) S = S with { RecordAntiCheatGames = v }; } }   // off: the core clears each game's switch off this thread
    /// <summary>"Recording in 23 games · 5 skipped: anti-cheat"; not-DX12 and unsupported games aren't counted.</summary>
    public string RecordingCount
    {
        get
        {
            var games = App.Core.Games;
            var skipped = games.Where(g => g.RecorderSkip is ScsKiller.SkipAntiCheat or ScsKiller.SkipShaderMod or ScsKiller.SkipForeignDll or ScsKiller.SkipModNotChainable
                    or ScsKiller.SkipVulkanMod or ScsKiller.SkipNeedsAdmin or ScsKiller.SkipPackageD3D12 or ScsKiller.SkipStreamline or ScsKiller.SkipShaderInjector or ScsKiller.SkipAgsMod)
                .GroupBy(g => g.RecorderSkip).OrderByDescending(x => x.Count()).Select(x => Loc.T("Settings_SkippedReason", x.Count(), x.Key));
            int n = games.Count(g => g.RecorderInstalled);
            return Loc.T(n == 1 ? "Settings_RecordingInOne" : "Settings_RecordingInMany", n) + string.Concat(skipped);
        }
    }

    public static readonly string[] TraversalChoices = TraversalFix.SettingsChoices;
    public Visibility TraversalVisibility => TraversalFix.Available ? Visibility.Visible : Visibility.Collapsed;
    public int TraversalIndex
    {
        get => TraversalFix.SettingsIndex(S);
        set { if (value >= 0 && value != TraversalFix.SettingsIndex(S)) S = TraversalFix.Answered(S, TraversalFix.SettingsAt(value)); }   // the core rewrites the recorders' inis
    }
    bool TraversalLow => TraversalFix.LowVram(App.Core.Vendor.Gpu) == true;
    public string TraversalNote => TraversalFix.SettingsLine + (TraversalLow ? "" : " " + TraversalFix.VramLine) + " " + TraversalFix.PerGameLine;
    public Visibility TraversalWarningVisibility => TraversalLow ? Visibility.Visible : Visibility.Collapsed;
    public string TraversalWarning => TraversalFix.LowVramWarning;

    public static readonly int[] RecordingLimits = ScsKiller.RecordingLimits;
    public static readonly string[] RecordingLimitLabels = RecordingLimits.Select(ScsKiller.LimitText).ToArray();
    public int RecordingLimitIndex
    {
        get => Array.IndexOf(RecordingLimits, S.RecordingLimitMB);   // -1: a value set elsewhere, shown blank
        set { if (value >= 0 && RecordingLimits[value] != S.RecordingLimitMB) S = S with { RecordingLimitMB = RecordingLimits[value] }; }   // the core reconciles off this thread
    }
    /// <summary>Each game with a recording on disk, largest first: its size, and whether it reached the limit.</summary>
    public IReadOnlyList<DetailRow> Recordings => App.Core.Games.Where(g => g.RecordingBytes > 0).OrderByDescending(g => g.RecordingBytes)
        .Select(g => new DetailRow(g.Game.Name, Format.Bytes(g.RecordingBytes), g.RecordingPaused ? ScsKiller.PausedNote(S) : null)).ToList();
    public bool HasRecordings => App.Core.Games.Any(g => g.RecordingBytes > 0);
    public string RecordingSpace => App.Core.Games.Where(g => g.RecordingBytes > 0).ToList() is { Count: > 0 } r
        ? Loc.T(r.Count == 1 ? "Settings_RecordingSpaceOne" : "Settings_RecordingSpaceMany", Format.Bytes(r.Sum(g => g.RecordingBytes)), r.Count) : Loc.T("Settings_NoRecordings");
    public bool? StartWithWindows
    {
        get => S.StartWithWindows;
        set
        {
            if (value is not { } v || v == S.StartWithWindows) return;
            S = S with { StartWithWindows = v };
            App.ApplyStartWithWindows(byUser: true);
        }
    }
    public bool? BeforeAfter { get => S.BeforeAfter; set { if (value is { } v && v != S.BeforeAfter) S = S with { BeforeAfter = v }; } }
    public int NewShadersChoice
    {
        get => (int)NewShaders.Global(S);
        set
        {
            if (value < 0 || value == NewShadersChoice) return;
            S = S with { OnNewShaders = (NewShadersMode)value };
            App.NewShadersChanged();
        }
    }
    public bool? NewShadersRightAway { get => S.NewShadersRightAway; set { if (value is { } v && v != S.NewShadersRightAway) S = S with { NewShadersRightAway = v }; } }
    public bool? ScanAtStart { get => S.ScanAtStart; set { if (value is { } v && v != S.ScanAtStart) S = S with { ScanAtStart = v }; } }
    public bool? HddMode { get => S.HddMode; set { if (value is { } v && v != S.HddMode) S = S with { HddMode = v }; } }
    public IReadOnlyList<HddDriveRow> HddDrives => [.. ScsKiller.HddDriveChoices(App.Core.Games, S)
        .Select(d => new HddDriveRow(d, S.HddDrives?.Contains(d, StringComparer.OrdinalIgnoreCase) == true, (d, on) => S = ScsKiller.WithHddDrive(S, d, on)))];
    public bool ShowsHddDrives => !S.HddMode && (HddDrives.Count > 1 || S.HddDrives is { Count: > 0 });
    public bool? CompileNewGames
    {
        get => S.CompileNewGames;
        set
        {
            if (value is not { } v || v == S.CompileNewGames) return;
            S = S with { CompileNewGames = v };
            App.NewShadersChanged();
        }
    }
    public bool? CloseQuits { get => S.CloseQuits; set { if (value is { } v && v != S.CloseQuits) S = S with { CloseQuits = v }; } }
    public bool HasIntelGpu { get; } = App.Core.HasIntelGpu;
    public bool? ExperimentalIntel { get => S.ExperimentalIntel; set { if (value is { } v && v != S.ExperimentalIntel) S = S with { ExperimentalIntel = v }; } }
    // The GPU picker, shown only with more than one GPU SCSKiller compiles for; the first entry is the default rule.
    public IReadOnlyList<GpuChoice> GpuChoices { get; } = App.Core.GpuChoices;
    public bool HasGpuChoices => GpuChoices.Count > 1;
    public IReadOnlyList<string> GpuLabels => [Loc.T("Settings_GpuAutomatic"), .. GpuChoices.Select(c => c.Name)];
    public int GpuIndex
    {
        get => S.Gpu is not { } c ? 0 : GpuChoices.ToList() is var all && all.IndexOf(c) is >= 0 and var at ? at + 1
            : all.FindIndex(g => (g.Vendor, g.DeviceId, g.Name) == (c.Vendor, c.DeviceId, c.Name)) + 1;   // as GpuBackends.Chosen: 0 when gone
        set
        {
            if (value < 0 || value == GpuIndex) return;
            S = S with { Gpu = value == 0 ? null : GpuChoices[value - 1] };
        }
    }

    public static readonly string[] Themes = [Loc.T("Common_UseWindowsSetting"), Loc.T("Common_Light"), Loc.T("Common_Dark")];   // AppTheme.System..Dark
    public int ThemeIndex
    {
        get => (int)(S.Theme ?? AppTheme.System);
        set
        {
            if (value < 0 || value == ThemeIndex) return;
            S = S with { Theme = value == 0 ? null : (AppTheme)value };
            App.Main.ApplyTheme(S.Theme);
        }
    }

    // 0: the system's language; then Loc.Languages in their order. The texts load once at start (Loc.Load): a change here runs then.
    public static readonly string[] LanguageNames = [Loc.T("Common_UseWindowsSetting"), .. Loc.Languages.Select(l => l.NativeName)];
    public int LanguageIndex
    {
        get { for (var i = 0; i < Loc.Languages.Count; i++) if (Loc.Languages[i].Code == S.Language) return i + 1; return 0; }
        set { if (value >= 0 && value != LanguageIndex) S = S with { Language = value == 0 ? null : Loc.Languages[value - 1].Code }; }
    }
    /// <summary>True while the picked language isn't the running one: it applies at the next start.</summary>
    public bool LanguageRestarts => (S.Language ?? Loc.Resolve(null)) != Loc.Current;


    public double Threads { get => UsedThreads; set { if ((int)value != UsedThreads) S = S with { Threads = (int)value }; } }
    int UsedThreads => ScsKiller.UsedThreads(S.Threads);
    public string ThreadsText => Loc.T("Queue_ThreadsOf", UsedThreads, Environment.ProcessorCount);
    /// <summary>0 = Auto; 1 means 2 (the smallest budget offered).</summary>
    public double MaxCompileMemory { get => ShownMemory; set { var v = (int)value == 1 ? 2 : (int)value; if ((int)value != ShownMemory && v != S.MaxCompileMemoryGB) S = S with { MaxCompileMemoryGB = v }; } }
    int ShownMemory => Math.Min(S.MaxCompileMemoryGB, (int)MaxCompileMemoryMax);
    public string MaxCompileMemoryNote => Loc.T("Settings_MaxCompileMemoryNote", ScsKiller.AutoCompileMemoryGB(), ScsKiller.RamGB());
    public double MaxCompileMemoryMax => ScsKiller.MaxCompileMemoryGB(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    public string MaxCompileMemoryText => S.MaxCompileMemoryGB > 0 ? $"{S.MaxCompileMemoryGB} GB" : Loc.T("Settings_AutoGb", ScsKiller.AutoCompileMemoryGB());
    public string PriorityNote => Loc.T(S.Priority == WarmPriority.BelowNormal ? "Settings_BelowNormalNote" : "Settings_IdleNote") + Loc.T("Settings_FewerThreads");
}

/// <summary>Settings' Account card: a view over <see cref="App.Account"/>, which changes on worker threads.</summary>
public sealed class AccountVm : Bindable
{
    static Account A => App.Account;
    readonly Coalesced changed;
    public AccountVm() => changed = new(App.Main.DispatcherQueue, Changed);

    /// <summary>While the page is shown. Signed in with nothing read yet this session: reads the status.</summary>
    public void Watch(bool on)
    {
        A.Changed -= changed.Request;
        Updater.Changed -= changed.Request;
        if (!on) return;
        A.Changed += changed.Request;
        Updater.Changed += changed.Request;
        Changed();
        if (A.SignedIn && A.Status == null && !A.Busy) _ = A.RefreshAsync();
    }

    public bool SignedIn => A.SignedIn;
    public bool SignedOut => !A.SignedIn;
    public bool SigningIn => A.SigningIn;
    public bool Busy => A.Busy;
    public bool Idle => !A.Busy;
    public string? Problem => A.Problem;
    public bool HasProblem => A.Problem != null;
    public bool OffersSharing => A.OffersSharing(App.Core.Settings);
    public void Refresh() => Changed();   // the settings changed (OffersSharing)

    // "Download shared shader hashes": enabled only with "db" (follows sign-in, sign-out, refresh and a lapse through
    // Changed); the setting keeps its value. One quiet line under it: why it's off, or its last failure.
    public bool HasDb => A.HasDb;
    // Unticked without "db" whatever the setting says; the binding writes that false back, which must not reach the setting.
    public bool? UseCommunityDb
    {
        get => A.HasDb && App.Core.Settings.UseCommunityDb;
        set
        {
            if (!A.HasDb || value is not { } v || v == App.Core.Settings.UseCommunityDb) return;
            App.Core.Settings = App.Core.Settings with { UseCommunityDb = v };
            Changed();
        }
    }
    public string? DbNote => !A.SignedIn ? Loc.T("Settings_DbNoteSignedOut")
        : A.Status is not { } st ? null   // not read yet: the status line above says "Checking…"
        : !A.HasDb ? Loc.T("Settings_DbNoteNoInclude")
        : (App.Core as ScsKiller)?.Community?.Problem;
    public bool HasDbNote => DbNote != null;

    public string Benefits => A.Status switch
    {
        null => A.Busy ? Loc.T("Settings_CheckingStatus") : Loc.T("Settings_StatusNotChecked"),
        { Benefits: { } b } => b,   // the flag names live in Core (AccountStatus) so they're testable
        _ => Loc.T("Settings_NoBenefits"),
    };

    // Update channel: a picker only when the token grants more than stable.
    static readonly Dictionary<string, string> ChannelNames = new()
    {
        [UpdateChannels.Stable] = Loc.T("Settings_ChannelStable"), [UpdateChannels.Beta] = Loc.T("Settings_ChannelBeta"),
        [UpdateChannels.Alpha] = Loc.T("Settings_ChannelAlpha"), [UpdateChannels.Internal] = Loc.T("Settings_ChannelInternal"),
    };
    IReadOnlyList<string> Offered => UpdateChannels.Offered(A.Status?.Ent);
    string Chosen => UpdateChannels.Effective(App.Core.Settings.UpdateChannel, AppVersion.Current.Channel, A.Status?.Ent);
    List<string> labels = [];
    public List<string> ChannelLabels => labels.SequenceEqual(Offered.Select(c => ChannelNames[c])) ? labels : labels = Offered.Select(c => ChannelNames[c]).ToList();   // same list: the ComboBox keeps its selection
    public bool ShowsChannels => A.SignedIn && Offered.Count > 1;
    public bool CanPickChannel => !Updater.Restarting;
    public bool CanSignOut => Idle && !Updater.Restarting;   // signing out can change the channel a restart applies
    public int ChannelIndex
    {
        get => Math.Max(0, Offered.ToList().IndexOf(Chosen));
        set
        {
            if (value < 0 || value >= Offered.Count || Offered[value] == Chosen || Updater.Restarting) return;   // -1: the list was replaced
            App.Core.Settings = App.Core.Settings with { UpdateChannel = Offered[value] };
            _ = Updater.CheckAsync();
            Changed();
        }
    }
    /// <summary>"Go back to stable now": on an installed pre-release build, whatever the account.</summary>
    public bool OffersBackToStable => Updater.Installed && AppVersion.Current.Channel != UpdateChannels.Stable;
    public bool ShowsUpdates => ShowsChannels || OffersBackToStable;
    public string ChannelNote => Updater.Checking ? Loc.T("Settings_CheckingUpdates")
        : Updater.Ready is { } v ? AutoInstall.ReadyNote(v, Updater.InstallsAutomatically)
        : (Updater.InstallsAutomatically ? Loc.T("Settings_UpdateAutoNote")
            : Loc.T("Settings_UpdateManualNote")) + Loc.T("Settings_EarlyChannelKeeps");
    public string? UpdateProblem => Updater.Problem;
    public bool HasUpdateProblem => Updater.Problem != null;
}

/// <summary>A cache folder on the Cache page.</summary>
public sealed record CacheFolderRow(string Label, string Path, string Size, string? Note)
{
    public bool HasNote => Note != null;
}

/// <summary>A cache item; Folders: where its files are (<see cref="CacheInventory.FoldersOf"/>).</summary>
public sealed record CacheItemRow(string Name, string Size, IReadOnlyList<string> Folders, string? Note, CacheItem Item, CacheGroupKind Kind, bool Enabled, ImageSource? Icon = null)
{
    public bool NoIcon => Icon == null;
    public bool HasFolder => Folders.Count > 0;
    public bool HasNote => Note != null;
}

public sealed record CacheGroupRow(string Title, string Size, string Caption, string? Note, IReadOnlyList<CacheItemRow> Items, bool Expanded,
    CacheGroup Group, bool Enabled)
{
    public bool HasNote => Note != null;
}

/// <summary>The Cache page: a read of <see cref="IScsKiller.CacheInventory"/> off the UI thread, and its deletes.</summary>
public sealed class CacheVm : Bindable
{
    CacheInventory? read;
    HashSet<string> games = [];   // the library the last read attributed by
    string split = "";
    readonly BackgroundRead<(CacheInventory Inventory, CacheUsage Usage, CacheLimit? Limit, long? Capped)> cache;

    public CacheVm()
    {
        cache = new("Cache page", () => (App.Core.CacheInventory(), App.Core.Vendor.GetCacheUsage(), App.Core.Vendor.GetCacheLimit(), (App.Core.Vendor as AmdBackend)?.AppCache.DxcBytes()),
            r =>
            {
                read = r.Inventory;
                split = Format.CacheSplit(r.Usage, r.Limit, r.Capped, App.Core.WindowsCacheBytes);
                Changed();
            });
        Refresh();
    }

    public void Refresh()
    {
        games = App.Core.Games.Select(g => g.Game.Id).ToHashSet();
        cache.Request();
    }

    /// <summary>A scan that lists other games (the first one after start among them) moves caches between groups.</summary>
    public void GamesChanged()
    {
        if (!games.SetEquals(App.Core.Games.Select(g => g.Game.Id))) Refresh();
    }

    public CacheInventory? Inventory => read;
    public string Total => read == null ? Loc.T("Cache_Reading") : split;
    public bool NoFolders => read is { Folders.Count: 0, Groups.Count: 0 };

    public bool Deleting { get; private set; }
    public bool CanDelete => read != null && !Deleting;
    public bool CanClearEverything => CanDelete && read!.Groups.Count > 0;
    public string? Result { get; private set; }
    public bool HasResult => Result != null;
    public Microsoft.UI.Xaml.Controls.InfoBarSeverity ResultSeverity { get; private set; }

    /// <summary>Deletes off the UI thread, then reads the caches again: the page shows what is left, not a subtraction.</summary>
    public async Task DeleteAsync(IReadOnlyList<CacheItem> items)
    {
        (Deleting, Result) = (true, null);
        Changed();
        try
        {
            var r = await Task.Run(() => App.Core.DeleteCaches(items));
            Result = Loc.T(r.Files == 1 ? "Cache_DeletedOne" : "Cache_DeletedMany", Format.Bytes(r.Bytes), r.Files)
                + (r.Skipped.Count > 0 ? Loc.T("Cache_SkippedFiles", r.Skipped.Count) + ":\n" + string.Join("\n", r.Skipped) : "");
            ResultSeverity = r.Skipped.Count > 0 ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;
        }
        catch (Exception e)
        {
            (Result, ResultSeverity) = (Loc.T("Cache_NothingDeleted", e.Message), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        }
        Deleting = false;
        Refresh();
        Changed();
    }

    long OldDriver => read?.Bytes(CacheGroupKind.OldDriver) ?? 0;
    public bool HasOldDriver => OldDriver > 0;
    public string OldDriverText => Loc.T("Cache_OldDriverText", Format.Bytes(OldDriver), read?.DriverInstalled?.ToLocalTime().ToString("d") ?? "");
    public string FreeText => Loc.T("Cache_FreeText", Format.Bytes(OldDriver));
    public IReadOnlyList<CacheItem> OldDriverItems => read?.Groups.Where(g => g.Kind == CacheGroupKind.OldDriver).SelectMany(g => g.Items).ToList() ?? [];

    public IReadOnlyList<CacheFolderRow> Folders => read?.Folders.Select(f => new CacheFolderRow(f.Label, f.Path,
        f.Cap is { } cap ? $"{Format.Bytes(f.Bytes)} of {Format.Bytes(cap)}" : Format.Bytes(f.Bytes),
        f.TargetMissing ? Loc.T("Cache_FolderTargetMissing")
        : f.Cap is { } full && f.Bytes >= full * 9 / 10 ? Loc.T("Cache_FolderNearlyFull")
        : f.Final != null ? Loc.T("Cache_FolderStoredVia", f.Final)
        : !f.Exists ? Loc.T("Cache_FolderNotCreated") : null)).ToList() ?? [];

    public IReadOnlyList<CacheGroupRow> Groups => read?.Groups.Select(g =>
    {
        var (title, caption) = Texts(g.Kind, read.DriverInstalled);
        var items = g.Items.Select(i => new CacheItemRow(ItemName(g.Kind, i), Format.OnDisk(i.Bytes, i.InUse), read.FoldersOf(i), i.Note, i, g.Kind, CanDelete && !i.Kept, g.Kind == CacheGroupKind.Installed ? GameIcon(i) : null)).ToList();
        return new CacheGroupRow(title, Format.OnDisk(g.Bytes, g.InUse), caption, g.Note, items, g.Kind != CacheGroupKind.Unattributed, g, CanDelete);
    }).ToList() ?? [];

    // The Library's icon source and cache, so no new extraction path.
    static ImageSource? GameIcon(CacheItem i) =>
        i.GameIds.Select(id => App.Core.Games.FirstOrDefault(g => g.Game.Id == id)).FirstOrDefault(g => g != null) is { } game ? Icons.Get(game.Game.ExePath) : null;

    /// <summary>An icon failed after its row was built: the rows read again.</summary>
    public void IconsChanged() => Changed();

    static string ItemName(CacheGroupKind kind, CacheItem i) => kind == CacheGroupKind.Unattributed && i.Name.Length == 8 && i.Name.All(char.IsAsciiHexDigit) ? Loc.T("Cache_UnknownApp", i.Name) : i.Name;

    public static (string Title, string Caption) Texts(CacheGroupKind kind, DateTime? installed) => kind switch
    {
        CacheGroupKind.Installed => (Loc.T("Cache_GInstalledTitle"), Loc.T("Cache_GInstalledCaption")),
        CacheGroupKind.Uninstalled => (Loc.T("Cache_GUninstalledTitle"), Loc.T("Cache_GUninstalledCaption")),
        CacheGroupKind.OldDriver => (Loc.T("Cache_GOldDriverTitle"), Loc.T("Cache_GOldDriverCaption", installed?.ToLocalTime().ToString("d") ?? "")),
        CacheGroupKind.OldBuild => (Loc.T("Cache_GOldBuildTitle"), Loc.T("Cache_GOldBuildCaption")),
        CacheGroupKind.OtherVendor => (Loc.T("Cache_GOtherVendorTitle"), Loc.T("Cache_GOtherVendorCaption")),
        _ => (Loc.T("Cache_GOtherTitle"), Loc.T("Cache_GOtherCaption")),
    };

    /// <summary>The confirm's text for deleting <paramref name="items"/> of one group: what happens after.</summary>
    public static string Consequence(CacheGroupKind kind, IReadOnlyList<CacheItem> items) => kind switch
    {
        CacheGroupKind.Installed or CacheGroupKind.Uninstalled =>
            Loc.T("Cache_DeleteGames", string.Join(", ", items.Select(i => i.Name))),
        CacheGroupKind.OldDriver => Loc.T("Cache_DeleteOldDriver"),
        CacheGroupKind.OldBuild => Loc.T("Cache_DeleteOldBuild"),
        CacheGroupKind.OtherVendor => Loc.T("Cache_DeleteOtherVendor"),
        _ => Loc.T("Cache_DeleteOther"),
    } + string.Concat(items.Select(i => i.Note).OfType<string>().Select(n => " " + n)) + Loc.T("Cache_FilesInUseSkipped");
}
