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
    public static string RecorderDll(GameState s) => s.AgsHost ? ScsKiller.AgsName : s.Records11 ? ScsKiller.Dll11 : ScsKiller.Dll12;
    public static string N(long? n) => n?.ToString("N0") ?? Format.Dash;
    /// <summary>"today at 20:14", "yesterday at 20:14", else "on 26/09/2026" (the culture's short date).</summary>
    public static string When(DateTimeOffset at)
    {
        var t = at.LocalDateTime;
        return t.Date == DateTime.Today ? $"today at {t:t}" : t.Date == DateTime.Today.AddDays(-1) ? $"yesterday at {t:t}" : $"on {t:d}";
    }
    public static string KeptSize(KeptCache k) => $"{Format.Bytes(k.Bytes)} · " + (k.Frees > 0 ? $"frees {Format.Bytes(k.Frees)}" : "takes no extra space");
    public static string Vendor(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => "NVIDIA", GpuVendor.Amd => "AMD", GpuVendor.Intel => "Intel", _ => "GPU",
    };
    public static string Engine(EngineInfo? e) => e is null ? "Unknown engine" : Format.Engine(e, e.Family == "Unreal" ? "UE" : null) + Guessed(e);
    public static string Guessed(EngineInfo e) => e.VersionGuessed ? " (guessed)" : "";
    public static Style Style(string key) => (Style)Application.Current.Resources[key];
    /// <summary>"Severe shader-compilation stutter: reason", then the source and when it was checked; null when not listed.</summary>
    public static string? StutterTip(GameState s) => s.KnownStutter is { } k
        ? $"{(k.Severity == StutterSeverity.Severe ? "Severe" : "Moderate")} shader-compilation stutter: {k.Reason}.\nSource: {k.Source} (checked {k.Date})" : null;
    /// <summary>Library sections, in list order. GOG, Ubisoft, Battle.net, PURPLE, HoYoPlay, Gaijin and Riot Client games are Store.Other: told apart by their id prefix.</summary>
    public static readonly string[] Stores = ["Steam", "Epic Games", "Xbox / Game Pass", "EA app", "GOG", "Ubisoft Connect", "Battle.net", "PURPLE", "HoYoPlay", "Gaijin", "Riot Client", "Other", AddedByYou];
    public const string AddedByYou = "Added by you";
    public static string StoreName(Game g) => g.Store switch
    {
        Store.Steam => "Steam", Store.Epic => "Epic Games", Store.Xbox => "Xbox / Game Pass", Store.EA => "EA app", Store.Manual => AddedByYou,
        _ => g.Id[..Math.Max(0, g.Id.IndexOf(':'))] switch { "gog" => "GOG", "ubisoft" => "Ubisoft Connect", "battlenet" => "Battle.net", "purple" => "PURPLE", "hoyoplay" => "HoYoPlay", "gaijin" => "Gaijin", "riot" => "Riot Client", _ => "Other" },
    };
    /// <summary>Measured driver cache when known, else the estimate.</summary>
    public static string Cache(GameState s) => s.CacheOnDisk is { } c ? Format.Bytes(c) : s.EstimatedCacheBytes is { } b ? "≈ " + Format.Bytes(b) : Format.Dash;
    /// <summary>The last compile's measured time, else the estimate: the estimate's rate is measured on a cold cache only.</summary>
    public static TimeSpan? CompileTime(GameState s) => s.LastWarmTime ?? s.EstimatedWarmTime;
    /// <summary>Tooltip for an estimated (not measured) cache value; null when measured.</summary>
    public static string? CacheTip(GameState s) => s.CacheOnDisk == null && s.EstimatedCacheBytes != null ? "Estimated driver cache if compiled" : null;
    /// <summary>One middleware tag: what it compiles and its DLLs.</summary>
    public static string TagTip(MiddlewareTag t) => Format.Middleware(t) + "\n    " + string.Join(", ", t.Dlls);
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
        QueueStage.Indexing => "Reading shaders", QueueStage.Planning => "Building plan", QueueStage.Materializing => "Preparing files",
        QueueStage.Paused => q.Note is { } n ? "Paused: " + n : "Paused",
        QueueStage.Warming when q.Note is { } n => "Compiling: " + n,   // stalled (ScsKiller.StalledNote)
        _ => "Compiling",
    };
    /// <summary>"54.8% · 1,234 of 5,000 pipelines · 480/s" (reading shaders: packages); null before the warm knows its total.</summary>
    public static string? ProgressText(QueueItem q) => q.Progress is { Total: > 0 } p
        ? $"{Percent(q):0.0}% · {p.Done:N0} of {p.Total:N0} {(q.Stage == QueueStage.Indexing ? "packages" : "pipelines")}" + (p.PerSecond > 0 && !ScsKiller.Stalled(q) ? $" · {p.PerSecond:N0}/s" : "") + (p.Failed > 0 ? $" · {p.Failed:N0} failed" : "") + (p.Skipped > 0 ? $" · {p.Skipped:N0} skipped" : "")   // short (the Done note explains them); a count wraps whole
            + (ScsKiller.MemoryLimitNote(p) is { } m ? " · " + m : "")
        : null;
    public static double Percent(QueueItem? q) => q?.Progress is { Total: > 0 } p ? 100.0 * p.Done / p.Total : 0;
    public static bool Indeterminate(QueueItem? q) => q != null && q.Progress is not { Total: > 0 };
    public static TimeSpan? Left(QueueItem q, GameState? g) => ScsKiller.Stalled(q) ? null   // no estimate for a warm that stopped moving
        : q.Progress is { PerSecond: > 0 } p ? TimeSpan.FromSeconds((p.Total - p.Done) / p.PerSecond)
        : g is not null ? g.EstimatedWarmTime ?? g.LastWarmTime : null;

    /// <summary>The game's compile runs (or is paused), or is the next the running queue starts: Play waits, since the warm
    /// stages a process under the game's exe name. A stopped queue's items and "when idle" items don't block it.</summary>
    public static bool CompilingSoon(IEnumerable<QueueItem> queue, string gameId)
    {
        var compiles = queue.Where(q => !q.PlanCheck).ToList();
        return compiles.Any(q => q.GameId == gameId && Format.Running(q))
            || App.Core.QueueRunning && compiles.FirstOrDefault(q => q.Stage == QueueStage.Waiting && q.Note != ScsKiller.WhenIdleNote)?.GameId == gameId;
    }

    public const string CompilingTip = "Compiling: play when it finishes";

    /// <summary>What IScsKiller doesn't carry, from the real core or the design data.</summary>
    public static ManualSource? Added => App.Core is ScsKiller k ? k.Manual : (App.Core as Design.FakeScsKiller)?.Manual;
    public static string? ScanProgress => App.Core is ScsKiller k ? k.ScanProgress : (App.Core as Design.FakeScsKiller)?.ScanProgress;
    public static string? AfterUpdateLine => App.Core is ScsKiller k ? k.AfterUpdateLine : (App.Core as Design.FakeScsKiller)?.AfterUpdateLine;
    public static void Rename(string gameId, string name)
    {
        if (App.Core is ScsKiller k) k.RenameManualGame(gameId, name);
        else (App.Core as Design.FakeScsKiller)?.RenameManualGame(gameId, name);
    }


    /// <summary>Starts the game through its store (<see cref="StoreLaunch"/>); the error to show, or null. The design data starts nothing.</summary>
    public static string? Play(GameState s)
    {
        var g = s.Game;
        if (StoreLaunch.OwnLauncherOnly(s)) return $"Start {g.Name} from its own launcher";
        if (App.Core is Design.FakeScsKiller) return null;
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
    public static string PlayVia(Game g) => g.Store == Store.Manual ? $"Start {Path.GetFileName(g.ExePath)}"
        // the store starts the collection, which opens the game played last
        : Collections.Member(g.Id) != null ? $"Play through {StoreName(g)}, then pick {g.Name} in the game's menu if another game opens"
        : $"Play through {StoreName(g)}";
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
/// read, so results apply in order. Request on the UI thread.</summary>
public sealed class BackgroundRead<T>(Func<T> read, Action<T> apply)
{
    bool busy, again;

    public async void Request()
    {
        if (busy) { again = true; return; }
        busy = true;
        try
        {
            do
            {
                again = false;
                apply(await Task.Run(read));
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
        if (failed.Contains(exePath) || !File.Exists(exePath)) return null;
        if (!cache.TryGetValue(exePath, out var entry))
        {
            var bmp = new BitmapImage();
            bmp.ImageFailed += (_, _) => Fail(exePath);   // a decode failure
            cache[exePath] = entry = (bmp, null);
            _ = LoadAsync(exePath, bmp);
        }
        return entry.Image;
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
public sealed record TagChip(string Text, string Tip);

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
    public bool HasTags => s.Middleware is { Count: > 0 } || ModChip(s) != null;
    public bool HasTagLine => HasTags || IsKnownStutter || IsNoStutter || HasBaChip;
    /// <summary>A before and after recording in progress; the tile has no button for it, the game page has.</summary>
    public string BaChip => BeforeAfterUi.Voided(s.BeforeAfter) ? "" : s.BeforeAfter?.Stage switch
    {
        BeforeAfterStage.AfterReady => "After run ready",
        BeforeAfterStage.Compiling => "Before and after: compiling",
        BeforeAfterStage.BeforePlaying or BeforeAfterStage.AfterPlaying => "Before and after: playing",
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
        List<TagChip> tags = [.. mod != null ? [mod] : Array.Empty<TagChip>(), .. m.Select(t => new TagChip(t.Label, Fmt.TagTip(t)))];
        return s.KnownStutter != null || s.NoStutter != null ? tags.Count > 1 ? [mod ?? new TagChip($"{m.Count} upscalers", string.Join("\n", m.Select(Fmt.TagTip)))] : tags
            : tags.Count <= MaxTags ? tags
            : [.. tags.Take(MaxTags), new TagChip($"+{tags.Count - MaxTags}", string.Join("\n", tags.Skip(MaxTags).Select(t => t.Tip)))];
    }
    /// <summary>The shader mod by name, else plain ReShade; null = neither, or not looked (anti-cheat).</summary>
    internal static TagChip? ModChip(GameState s) => s.ShaderMod is { } mod
        ? new(mod, s.ShaderModBlocks ? "Stops this game compiling" : s.ShaderModLayer ? "Compiled through your mod" : "A few modded shaders aren't precompiled")
        : s.ReShade ? new("ReShade", "Changes no pipelines") : null;
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
        _ when s.CompileUnreached => "The compile didn't help",
        _ when PartlyCompiled => "Partly compiled",
        GameStatus.Warmed when Partly => "Partly warmed",
        GameStatus.Warmed when s.Plan?.RecordedOnly == true => "Warmed (recorded pipelines only)",
        GameStatus.Warmed => "Warmed",
        GameStatus.Ready => "Ready to compile",
        _ when ScsKiller.NeedsOfflineSession(s) => "Needs an offline session",
        GameStatus.NeedsRecording when ScsKiller.NeverRecorded(s) => ScsKiller.NothingRecordedTitle,
        GameStatus.NeedsRecording => ScsKiller.RecordedEnough(s) ? "Needs a recording" : "Needs a 5-min recording",
        GameStatus.Stale => "Needs rebuilding",
        _ when s.StatusReason == ScsKiller.CheckingReason => "Checking…",
        _ when s.StatusReason == ScsKiller.NotCheckedReason => "Not checked",
        _ when s.StatusReason == ScsKiller.CantReachReason => "Not compatible",
        _ => s.ShaderModBlocks ? "Not compiled" : s.Engine?.Encrypted == true ? "Encrypted game files" : s.NotPlanned ? "Not supported" : "Not supported yet",
    };
    /// <summary>The whole reason: the row's tooltip and the game page's status text.</summary>
    public string FullNote => s.Status switch
    {
        _ when s.CompileUnreached || PartlyCompiled => s.StatusReason,
        GameStatus.Warmed when Partly => $"driver {s.WarmedDriverVersion} · {s.Careful!.LaunchCompiled * 100:0}% still compiled at its first launch",
        GameStatus.Warmed => $"driver {s.WarmedDriverVersion}" + (ScsKiller.IsPartial(s.Plan) ? " · a recording compiles the rest"
            : ScsKiller.RtAfterRecording(s) ? " · " + ScsKiller.RtAfterRecordingNote : ""),
        _ when ScsKiller.NeedsOfflineSession(s) => s.StatusReason,
        GameStatus.NeedsRecording when s.AnyAntiCheat && !s.RiskEligible => $"{ScsKiller.AntiCheatName(s)} blocks recording",
        GameStatus.NeedsRecording when s.RecordingPaused => ScsKiller.PausedNote(App.Core.Settings),
        GameStatus.NeedsRecording when ScsKiller.NeverRecorded(s) => s.RecorderRefused!,
        GameStatus.NeedsRecording when s.RecorderInstalled && !ScsKiller.RecordedEnough(s) => Format.RecorderOnNote(s),
        _ => s.StatusReason,
    };
    /// <summary>The row's note under the status: a few words (<see cref="Format.ShortNote"/>); null when the status says it all.</summary>
    public string? Note => Format.ShortNote(s);
    // a mod the compile runs through takes the note's place; the tooltip keeps the full reason
    string? ListNote => s.ShaderModLayer ? $"{(s.Status == GameStatus.Warmed ? "Compiled" : "Compiles")} through {s.ShaderMod}" : Note;
    public string RowNote => Playing ? "Playing now" + (ListNote is { } n ? " · " + n : "") : ListNote ?? "";
    public bool HasRowNote => RowNote.Length > 0;
    public string RowTip => (Playing ? "Playing now · " : "") + FullNote;
    public Style StatusStyle => Fmt.Style(s.Status switch
    {
        _ when s.CompileUnreached || PartlyCompiled => "StatusWarn",
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
    public string AddText => queued ? "In queue" : "Add to queue";
    public bool CanAdd => !queued;
    public bool IsRecord => s.Status == GameStatus.NeedsRecording && !s.RecorderInstalled && s.NoStutter == null && !ScsKiller.NeedsOfflineSession(s) && !s.RiskEligible && !Shared;
    bool Shared => Collections.RecordedBy(s.Game.Id) != null;   // recorded by its collection's first game's recorder, switched on that game's page
    public bool CanRecord => !s.AnyAntiCheat;
    public bool IsDetails => s.Status == GameStatus.Warmed || ScsKiller.IsUnchecked(s) || s.CompileUnreached || ScsKiller.NeedsOfflineSession(s) || (s.Status == GameStatus.NeedsRecording && (s.RecorderInstalled || s.NoStutter != null || s.RiskEligible || Shared));
    public bool IsWhy => s.Status == GameStatus.Unsupported && !ScsKiller.IsUnchecked(s);
    public string Reason => s.StatusReason;

    // Play goes through the store (a game the user added: its exe); never for an anti-cheat game (SCSKiller never launches them)
    public bool CanLaunch { get; private set; } = BeforeAfterUi.Launchable(s);
    public bool Compiling => compiling;
    public bool CanPlay => !Playing && !compiling;
    public string PlayText => Playing ? "Running" : "Play";
    public string PlayTip => Playing ? $"{Name} is running" : compiling ? Fmt.CompilingTip : Fmt.PlayVia(s.Game);

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
    public const string Recommended = "Recommended to compile";
    public string Store => store;
    public string CountText => $"{Count} game{(Count == 1 ? "" : "s")}" + (store == Recommended ? RecommendedNote() : "");
    // a row updated in place (compiled, cleared) changes the count of compiled ones without a collection change
    public void Recount() => OnPropertyChanged(new PropertyChangedEventArgs(nameof(CountText)));
    string RecommendedNote() => this.Count(r => r.State.Status == GameStatus.Warmed) is var done and > 0
        ? $" · stutters without a compile · {done} compiled" : " · stutters without a compile";
}

public sealed record HiddenRow(string Id, string Name, string Store)
{
    public string ShowName => $"Show {Name} in library";
}

public sealed class LibraryVm : Bindable
{
    static readonly LibraryScan firstScan = new();
    readonly BackgroundRead<(CacheUsage Usage, CacheLimit? Limit, long? Capped, long OldDriver)> cache;

    public LibraryVm() => cache = new(() => (App.Core.Vendor.GetCacheUsage(), App.Core.Vendor.GetCacheLimit(), (App.Core.Vendor as AmdBackend)?.AppCache.DxcBytes(),
        App.Core.OldDriverBytes()), ShowCache);

    public IReadOnlyList<GameRow> Games { get; private set; } = [];   // every game, sorted; the list shows Groups
    /// <summary>The list: rows matching <see cref="Filter"/> per store, empty stores left out. Filtering reuses the rows
    /// (no icon reads, no IO).</summary>
    public ObservableCollection<StoreGroup> Groups { get; } = [];
    string filter = "";
    public string Filter { get => filter; set { if (value.Trim() != filter) { filter = value.Trim(); ApplyFilter(); Changed(); } } }
    LibraryShow show;
    public static readonly string[] ShowLabels = ["All games", "Not supported", "Ready to compile", "Compiled", "Compiled by SCSKiller"];
    public int ShowIndex { get => (int)show; set { if (value >= 0 && (LibraryShow)value != show) { show = (LibraryShow)value; ApplyFilter(); Changed(); } } }
    int shown;
    public bool Filtering => filter.Length > 0 || show != LibraryShow.All;
    public bool Searching => filter.Length > 0;
    public string ShownText => $"{shown} of {Games.Count} shown";
    public bool NoMatch => Filtering && Games.Count > 0 && shown == 0;
    public string NoMatchText => Searching ? $"No games match “{filter}”" : "No games with this status";
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
    public string GameHeader => "Game" + Arrow(LibrarySort.NameAscending, LibrarySort.NameDescending);
    public string PipelinesHeader => "Pipelines" + Arrow(LibrarySort.PipelinesAscending, LibrarySort.PipelinesDescending);
    public string CacheHeader => "Cache" + Arrow(LibrarySort.CacheAscending, LibrarySort.CacheDescending);
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
    public string AddAllText => $"Add all ready ({ReadyCount})";
    public bool CanAddAll => ReadyCount > 0 && !Scanning;
    public int RecommendedCount { get; private set; }   // stutters without a compile, ready and not queued yet
    public string AddRecommendedText => $"Add all recommended ({RecommendedCount})";
    public bool CanAddRecommended => RecommendedCount > 0 && !Scanning;
    public bool CannotAddRecommended => !CanAddRecommended;   // the plain, disabled twin of the accent button (theme brushes stay in XAML)
    public string CompileQueueText => $"Compile queue ({WaitingCount})";
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

    public string DriverMode => App.Core.Settings.OnDriverUpdate switch
    {
        DriverUpdateMode.Ask => "Ask me first", DriverUpdateMode.WhenIdle => "When the PC is idle", _ => "Off",
    };
    public string DriverModeNote => App.Core.Settings.OnDriverUpdate switch
    {
        DriverUpdateMode.Ask => "A notification offers compile now, when idle, or skip",
        DriverUpdateMode.WhenIdle => "Rebuilds on its own while you're away from the PC",
        _ => "Rebuild games yourself from the Library",
    };
    public string Threads => App.Core.Settings.Threads.ToString();
    public string ThreadsOf => $"of {Environment.ProcessorCount} threads";
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
    public string ScanEmptyNote => Fmt.AfterUpdateLine ?? "Checking Steam, Epic, Xbox, EA, GOG, Ubisoft Connect, Battle.net, PURPLE, HoYoPlay, Gaijin and Riot Client, then which engine each game uses. "
        + "The first scan reads every game's files, so it can take a minute.";
    public string ScanTitle => Fmt.ScanProgress ?? "Checking for changed games…";
    public string ScanEmptyTitle => Fmt.ScanProgress ?? "Looking for your games…";
    /// <summary>A scan's step: the last list, which the first scan puts up before it reads the games, else the progress line.</summary>
    public void ScanStep()
    {
        if (Games.Count == 0 && App.Core.Games.Count > 0) Refresh();
        else Changed();
    }
    public string ScanBusyNote => forced ? "Re-reading every game's engine and anti-cheat. Each game updates as it's checked."
        : "Each game updates as it's checked.";

    public async void Rescan(bool force = true, bool userRequested = false)
    {
        Scanning = true; refreshing++; forced = force; Error = refreshNote = null; Refresh();   // the summary says "Looking for games…" while nothing is listed
        var scan = force ? App.Core.RescanAsync(CancellationToken.None, userRequested) : App.Core.ScanAsync(CancellationToken.None, userRequested);
        if (await Task.WhenAny(scan, Task.Delay(300)) != scan) { slowScan = true; Changed(); }
        try { await scan; firstScan.Ended(ok: true); }
        catch (Exception e) { Error = "Scan failed: " + e.Message; firstScan.Ended(ok: false); }
        Scanning = slowScan = false;
        Refresh();
        if (userRequested && Error == null && App.Core is ScsKiller k)
            refreshNote = await k.ServerRefresh switch
            {
                ScsKiller.ServerCheck.TooSoon => "Checked the server a few minutes ago.",
                ScsKiller.ServerCheck.Unreachable => "Couldn't reach the SCSKiller server. The lists stay as they were.",
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
        var storeText = stores.Count > 2 ? $"{stores.Count} stores" : string.Join(" and ", stores);
        bool allUnreal = list.Count > 0 && list.All(g => g.Engine?.Family == "Unreal");
        Summary = Scanning && list.Count == 0 ? "Looking for games…"
            : $"{list.Count} {(allUnreal ? "Unreal Engine " : "")}game{(list.Count == 1 ? "" : "s")} found{(storeText.Length > 0 ? " in " + storeText : "")} · {ready} ready to compile";

        cache.Request();

        var rate = ScsKiller.MeasuredRate(list);
        SpeedNote = (core.Settings.Priority == WarmPriority.BelowNormal ? "Below-normal priority" : "Idle priority")
                    + (rate is { } r ? $" · about {r:N0} pipelines/s measured" : "");
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

    // Capped: AMD's limit is of its DirectX 12 cache alone, which the usage counts with the DirectX 11 one
    void ShowCache((CacheUsage Usage, CacheLimit? Limit, long? Capped, long OldDriver) c)
    {
        var (usage, limit, capped, old) = c;
        OldDriverNote = old >= OldDriverShown ? $"{Format.Bytes(old)} the new driver no longer uses" : null;
        CacheUsed = (usage.UpperBound ? "≤ " : "") + Format.Bytes(usage.BytesOnDisk);
        CacheLimitText = limit is null ? "limit unknown" : limit.Bytes is { } lb ? capped is { } cb ? $"· DirectX 12 {Format.Bytes(cb)} of {Format.Bytes(lb)} limit" : $"of {Format.Bytes(lb)} limit" : "no limit";
        CachePercent = limit?.Bytes is { } max && max > 0 ? Math.Min(100, 100.0 * (capped ?? usage.BytesOnDisk) / max) : 0;
        CacheWarn = limit?.Bytes is not null && (limit.IsDriverDefault || CachePercent > 75);
        CacheWarnText = limit?.IsDriverDefault == true ? "Driver default limit may evict games" : "The cache is nearly full";
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
        blocked = Blocked(id);
        Changed();
    }

    static string Sentence(string t) => t.Length > 0 ? char.ToUpperInvariant(t[0]) + t[1..] : t;
    bool Partial => ScsKiller.IsPartial(s.Plan);
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
        _ when s.CompileUnreached || ScsKiller.IsPartlyCompiled(s) => "",
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

    public string StatusTitle => Running is { } r ? Fmt.Stage(r) : place.Item != null ? $"In queue (position {place.Number})" : Row.StatusText;
    public string StatusReason => Running is { } r && Fmt.ProgressText(r) is { } p
        ? p + (r.Progress is { PerSecond: > 0 } && Fmt.Left(r, s) is { } t ? $" · {Format.Duration(t)} left" : "")
        : GameReason;
    string GameReason => Sentence(s.Status switch
    {
        _ when s.CompileUnreached => s.StatusReason + (s.WarmedAt != null ? ". " + Sentence(ScsKiller.ClearForGameNote) : ""),
        _ when ScsKiller.IsPartlyCompiled(s) => ScsKiller.RefusedNote(s) + ". An SCSKiller update could fix it",
        GameStatus.Warmed when ScsKiller.IsPartlyWarmed(s) => s.StatusReason,
        GameStatus.Warmed => $"compiled for {(ScsKiller.BothApis(s.Plan) ? "DirectX 11 and DirectX 12, for " : "")}driver {s.WarmedDriverVersion}" + (s.WarmedAt is { } t ? $" on {t.LocalDateTime:d}" : "")
            + (RtAfter ? ". " + Sentence(ScsKiller.RtAfterRecordingNote) : ""),
        // a cache here: played since a clear or a stopped compile; then the planner's note when the row has one to say
        GameStatus.Ready => (s.CacheOnDisk > 0 ? $"not fully compiled yet: {Format.Bytes(s.CacheOnDisk)} of it is in the driver cache"
            : s.LastWarmTime == null ? "not compiled yet" : "its shader cache is empty: compile it again")
            + (Row.Note != null ? ". " + Sentence(s.StatusReason) : ""),
        GameStatus.NeedsRecording => HasDbTeaser ? NoDbNote(Row.FullNote) : Row.FullNote,   // the teaser says it
        _ when s.Engine?.Encrypted == true => "needs the game's AES key",   // the title: "Encrypted game files"
        _ => HasDbTeaser ? NoDbNote(s.StatusReason) : s.StatusReason,
    })
        + (IsPartlyCompiled || s.CompileUnreached ? "." : "") + Format.ModNote(s);
    static string NoDbNote(string r) => r.Replace("; " + ScsKiller.InDbSupportersNote, "").Replace("; " + ScsKiller.InDbNote, "");
    public bool IsPartlyCompiled => ScsKiller.IsPartlyCompiled(s);
    public bool NeedsAesKey => ScsKiller.NeedsAesKey(s);
    public const string PartlyCompiledWhy = "SCSKiller compiled this game, but the graphics driver refused a large part of it. This happens when the driver or Windows can't build pipelines the way the game does. The refused ones are built by the game the first time it needs them, which is when they can stutter. SCSKiller compiles them again after a driver update.";

    // The manifest is public: a PC without "db" sees that the community database covers the game, and where to get it.
    // Shown, it is the page's only mention of the database.
    public bool HasDbTeaser => s is { InCommunityDb: true, Community: null } && !App.Account.HasDb
        && (s.Status == GameStatus.NeedsRecording || RtAfter || s.Status == GameStatus.Unsupported && !NoAntiCheat && s.StatusReason.StartsWith("needs a recording, which", StringComparison.Ordinal));
    public string DbTeaser => $"In the community database: {s.CommunityDbPsos:N0} pipeline{(s.CommunityDbPsos == 1 ? "" : "s")} recorded by other players. Patreon supporters compile them without recording.";
    public string DbTeaserLink => App.Account.SignedIn ? "Patreon membership" : "Sign in with Patreon";
    public bool CanCompile => s.Status is GameStatus.Ready or GameStatus.Warmed or GameStatus.Stale || RtPartial;
    public const string MaxCompileWhy = "Compiles every combination of this game's shaders, not just each one once. It takes longer and uses more disk space, but potentially fewer shaders compile while you play. Seems to help most on AMD.";
    public string? GuessNote => s.Engine is { } e && s.Status is GameStatus.Ready or GameStatus.Warmed or GameStatus.Stale ? ScsKiller.GuessNote(e) : null;
    public bool HasGuessNote => GuessNote != null;
    // warmed: nothing urgent, unless only partly (the careful compile is offered); RT: Record comes first; no shader stutter: not needed
    public Style CompileStyle => Fmt.Style((OffersCareful || s.Status != GameStatus.Warmed && !RtPartial) && s.NoStutter == null ? "AccentButtonStyle" : "DefaultButtonStyle");
    bool Queued => App.Core.Queue.Any(q => q.GameId == id && Fmt.Active(q) && !q.PlanCheck);
    public bool CanAdd => !Queued && !BaInProgressAny;
    /// <summary>A pair before Done: a compile or clear of the user's own would spoil a run (Core refuses them too).</summary>
    bool BaInProgressAny => s.BeforeAfter is { Stage: not BeforeAfterStage.Done };
    const string BaBusyTip = "A before and after recording is in progress";
    public string? CompileTip => BaInProgressAny ? BaBusyTip : null;
    public bool CanClearCache => HasCache && !BaInProgressAny;
    bool CompilingSoon => Fmt.CompilingSoon(App.Core.Queue, id);
    public bool ShowPlay => Row.CanLaunch;
    public bool CanPlay => !s.Playing && !CompilingSoon;
    public string PlayText => s.Playing ? "Running" : "Play";
    public string PlayTip => s.Playing ? $"{s.Game.Name} is running" : CompilingSoon ? Fmt.CompilingTip : Fmt.PlayVia(s.Game);
    public bool IsManual => s.Game.Store == Store.Manual;
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
    string? blocked = Blocked(id);
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
    (bool Shown, bool Enabled, string Note) BaStart => BeforeAfter.StartButton(App.Core.Settings.BeforeAfter, blocked);
    public bool ShowBa => Ba != null ? App.Core.Settings.BeforeAfter : BaStart.Shown;
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
    public Style BaStyle => Fmt.Style(BaVoided || Ba?.Stage is BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed or BeforeAfterStage.AfterReady ? "AccentButtonStyle" : "DefaultButtonStyle");
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
    public bool CarefulOn => CarefulPending ?? s.Careful?.On == true;
    public bool CanToggleCareful => CarefulPending == null && !Queued;
    public string CarefulNote => "Only for games whose compile fails. " + (s.Careful is { Recorded: 0 } ? "Compiles the pipelines recorded in the game in passes on few threads, so the driver keeps more of them. This game has no recording yet, so there is nothing to compile carefully."
        : $"Compiles the pipelines recorded in the game in passes on {ScsKiller.AmdCarefulThreads} threads, so the driver keeps more of them. The rest compile at full speed."
          + (s.Careful?.Estimate is { } est ? $" About {Format.Duration(est)}" + (s.Careful.On || Fmt.CompileTime(s) is not { } fast ? "." : $" instead of {Format.Duration(fast)}.") : ""))
        + (s.Careful?.LaunchCompiled is { } lc ? $" After the last compile, the game's first launch still compiled {lc * 100:0}% of its pipelines." : "");
    // AMD: the compiled cache kept aside, in the compile card
    public bool HasKept => s.Kept != null && App.Core.Vendor.Vendor == GpuVendor.Amd && App.Core.Settings.KeepCachesAside;
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
    public List<string> NewShadersChoices => [$"Use the global setting ({NewShadersLabels[(int)NewShaders.Global(App.Core.Settings)]})", .. NewShadersLabels];
    public int NewShadersIndex => s.NewShadersOverride is { } m ? (int)m + 1 : 0;
    // 0 = the global setting, 1 = on, 2 = off
    public List<string> RayTracingChoices => [$"Default ({(App.Core.Settings.CompileRayTracing ? "on" : "off")})", "On", "Off"];
    public int RayTracingIndex => s.RayTracingOverride switch { null => 0, true => 1, false => 2 };
    // no plan, or one from before inline ray tracing was counted: unknown, so the choice shows
    public bool ShowRayTracing => P?.RtInline == null || s.RayTracingOverride != null || ScsKiller.HasRayTracing(P);
    // Windows' shader cache of the game, read off the UI thread: Clear cache clears it too, so it can be the only cache to clear.
    BackgroundRead<long>? otherCaches;
    long otherBytes;
    public void ReadCaches() => (otherCaches ??= new(OtherCacheBytes, b => { otherBytes = b; Changed(); })).Request();
    long OtherCacheBytes()
    {
        try { return App.Core.GameCaches(id).Where(p => p.Name != ScsKiller.DriverPart).Sum(p => p.Bytes); }
        catch (Exception) { return 0; }   // e.g. the game is gone after a rescan
    }
    public bool HasCache => s.CacheOnDisk != null || otherBytes > 0 || s.Status is GameStatus.Warmed or GameStatus.Stale;
    // compiled before a verdict changed: the cache stays until the user clears it
    public string? KeptCacheNote => (s.Status == GameStatus.Unsupported || s.NoStutter != null) && s.WarmedAt != null && s.CacheOnDisk > 0
        ? $"SCSKiller compiled {DiskValue} of cache for this game before it was marked {(s.NoStutter != null ? "as having no shader stutter" : "not supported")}."
        : null;
    public bool HasKeptCache => KeptCacheNote != null;

    // Maximum mode suggested for this game on this GPU vendor (Games.MaximumMode), or on for it alone
    public bool? MaximumPending { get; set; }
    public bool MaximumSuggested => s.MaximumSuggestion != null;
    // on for every game in Settings: the per-game switch changes nothing, so it isn't offered
    public bool HasMaximumLine => MaximumSuggested || s.MaximumForGame == true && !App.Core.Settings.MaximumPlans;
    public string MaximumLine => MaximumSuggested
        ? "Maximum mode helps this game: it also compiles every pairing of its shaders, so fewer compile while you play."
        : "Maximum mode is on for this game. Its compiles take longer and use more disk space.";
    public string? MaximumNote => string.Join(" ", new[] { s.MaximumSuggestion is { Length: > 0 } note ? note : null,
        MaximumSuggested && s.MaximumAddsBytes is { } adds ? $"On AMD it adds up to {Format.Bytes(adds)} to the driver's shared {Format.Bytes(AmdAppCache.DxcCacheCap)} cache." : null }.OfType<string>()) is { Length: > 0 } t ? t : null;
    public bool HasMaximumNote => MaximumNote != null;
    public string MaximumAction => MaximumSuggested ? "Turn on for this game" : "Turn off for this game";
    public bool CanSetMaximum => MaximumPending == null && !Queued;
    public bool ShowClearCache => HasCache || CanCompile;
    // the driver cache, as Disk space shows it; the dialog lists Windows' cache with its own size
    public string ClearCacheText => s.CacheOnDisk != null ? $"Clear cache ({DiskValue})" : "Clear cache";
    public string ClearCacheTip => BaInProgressAny ? BaBusyTip : HasCache ? "Deletes this game's shader caches (the driver's and Windows', and the game's own if you choose) so its next run starts cold"
        : "SCSKiller learns this game's driver cache files the first time it compiles it or sees it running";

    // Rate this compile (CompatReport) and what players with a similar system reported (CompatFeed)
    string? ReportWhyNot => CompatReport.WhyNot(s, ScsKiller.PackGpu(App.Core.Vendor.Vendor));
    public bool CanReport => ReportWhyNot == null;
    public string ReportTip => ReportWhyNot ?? "Tell other players how this game ran after SCSKiller compiled it";
    public bool HasSimilar => s.Similar is SimilarSystems.Helped or SimilarSystems.NoHelp;
    public bool HasSimilarWarning => s.Similar == SimilarSystems.Warning;
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
    public bool ShowApiPicker => s is { ApiDetected: not null } or { ApiOverride: not null } && s.Engine is { Unsupported: null, Encrypted: false };
    public bool? ApiPending { get; set; }
    public bool CanPickApi => ApiPending == null && !App.Core.Queue.Any(q => q.GameId == id && Format.Running(q));   // its plan check too: the core refuses a change meanwhile
    public int ApiIndex => s.ApiOverride switch { DxApi.D3D11 => 1, DxApi.D3D12 => 2, _ => 0 };
    string[]? apiItems;
    public string[] ApiItems
    {
        get
        {
            var auto = s.Engine?.GraphicsApi.EndsWith(ScsKiller.RecordingSuffix, StringComparison.Ordinal) == true ? $"Auto ({Dx(s.Engine.GraphicsApi)} from your recording)"
                : s.ApiDetected == SCSKiller.Core.Unreal.UnrealRhi.Ambiguous ? "Auto (DirectX 11 and 12)"
                : s.ApiDetected is { } d ? $"Auto ({Dx(d)})" : "Auto";
            if (apiItems?[0] != auto) apiItems = [auto, "DirectX 11", "DirectX 12"];   // the same array while the label holds: a new one resets the selection
            return apiItems;
        }
    }
    static string Dx(string api) => api.Contains("D3D12") ? "DirectX 12" : "DirectX 11";
    public string? ApiOverruled => ScsKiller.ApiOverruled(s);
    public bool HasApiOverruled => ApiOverruled != null;
    public string AntiCheatChip => NoAntiCheat ? "No anti-cheat" : ScsKiller.AntiCheatName(s);
    public string AntiCheatTip => NoAntiCheat ? "No anti-cheat found: this game can be recorded"
        : s.RiskEligible ? $"This game contains {ScsKiller.AntiCheatName(s)}. The recorder runs only in a session you confirm, at your own risk."
        : $"{ScsKiller.AntiCheatName(s)} treats an extra {RecorderDll} as tampering: this game is never recorded";
    public bool HasMiddleware => s.Middleware is { Count: > 0 };
    public bool HasModChip => GameRow.ModChip(s) != null;
    public string ModChip => GameRow.ModChip(s)?.Text ?? "";
    public string ModTip => GameRow.ModChip(s)?.Tip ?? "";
    IReadOnlyList<MiddlewareTag> Mw => s.Middleware ?? [];
    /// <summary>"Also compiles" only names tags with known pipelines (and DLSS, by the driver); else what was detected.</summary>
    public string MiddlewareChip => Mw.Where(t => t.Pipelines > 0).Select(t => t.Label)
            .Concat(Mw.Any(t => t.Label == "DLSS") ? ["DLSS (by the driver)"] : []).ToList() is { Count: > 0 } also
        ? "Also compiles: " + string.Join(" · ", also) : "Detected: " + string.Join(" · ", Mw.Select(t => t.Label));
    public string MiddlewareTip => "Upscaler DLLs next to the game. Their pipelines are compiled with it once a recording of any game has seen that DLL version"
        + ": on this PC, or for FSR and XeSS on any PC that shares them"
        + (s.Plan is { MiddlewareItems: > 0 } p ? $" (this plan: {Fmt.N(p.MiddlewareItems)} of theirs)" : "") + ".\n\n"
        + string.Join("\n", Mw.Select(Fmt.TagTip));

    // The one tip left: a game that can't compile before a recording, or not its ray tracing (a plan's other gaps are the
    // coverage card's "what's left").
    public bool HasRecordTip => CanRecord && (s.Status == GameStatus.NeedsRecording || RtAfter) && !RecordOn && s.NoStutter == null;
    bool Enough => ScsKiller.RecordedEnough(s);
    public string RecordTipTitle => Enough ? "Record more play" : "Record 5 minutes of play";
    public string RecordTip => RtPartial || RtAfter ? $"{TurnOnAndPlay} with ray tracing on{(Enough ? "" : " for about 5 minutes")}. " + ScsKiller.RtWhy(App.Core.Vendor.Caps, s.Engine)
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
        : Dx11Only && Pct is null or 100 ? (Compiled ? "Covers" : "Will cover") + " every DirectX 11 shader in the game files" + (SCSKiller.Core.Planning.Planner.D3D11CoversVs(App.Core.Vendor.Caps) || P.D3D11Draws > 0 ? "" : " but the vertex shaders")
        : Pct is not { } pct ? "Measured again on the next compile"
        : (Compiled || s.NewOnly ? "Covers" : s.WarmedAt != null ? "Its last compile covered" : "Will cover")
          + (pct == 100 ? " all the shader combinations found in this game" : $" about {pct}% of the shader combinations found in this game");
    bool Rt => ScsKiller.NeedsRtRecording(s);
    bool RtUnseen => s.RtUnseen;
    bool RtInline => ScsKiller.RtInlineCovers(P);
    bool Compiled => s.WarmedAt != null && s.Status != GameStatus.Stale;

    /// <summary>Where the plan's pipelines come from, in a player's words; the recording's row carries the community note,
    /// and without one the community database's status is its own row. Ray tracing: covered or what it needs.</summary>
    public IReadOnlyList<DetailRow> Sources => P is not { } p ? [] : new DetailRow?[]
    {
        p.Generated > 0 ? new(ScsKiller.BothApis(p) ? "DirectX 12, from the game files" : "Found in the game files", Fmt.N(p.Generated)) : null,
        p.Recorded > 0 ? new(RecordingSource, Fmt.N(p.Recorded), CommunityLine) : null,
        p.CacheBudgetLeftOut is > 0 and var left ? new(ScsKiller.CacheBudgetTitle(App.Core.Vendor.Caps), Fmt.N(left), ScsKiller.CacheBudgetNote(App.Core.Vendor.Caps)) : null,
        s.Official is { } o ? new("Official recording", $"{o.Psos:N0} pipelines", $"Published by SCSKiller for this version, downloaded {o.DownloadedAt.LocalDateTime:d}") : null,
        s.Community == null && s.InCommunityDb is { } inDb && !HasDbTeaser ? new("The community database", inDb ? "has a recording for this version" : "not in it yet") : null,
        p.MiddlewareItems > 0 ? new(p.MiddlewareSharedItems == 0 ? "Its upscalers, learned from recordings"
            : p.MiddlewareSharedItems == p.MiddlewareItems ? "Its upscalers, from shared packs" : "Its upscalers, from recordings and shared packs", Fmt.N(p.MiddlewareItems)) : null,
        p.D3D11Shaders > 0 ? new(ScsKiller.BothApis(p) ? "DirectX 11, from the game files" : "DirectX 11 shaders", Fmt.N(p.D3D11Shaders)) : null,
        p.RtLibraries > 0 && s.RtSkipped ? new("Ray tracing", "skipped (your setting)")
        : p.RtLibraries > 0 ? new("Ray-traced effects", s.Engine?.NoRayTracing == true ? "off in this game" : s.Engine?.NoRtPipelines == true ? "inline, from the game files" : p.RtUncovered == 0 ? "covered" : s.RtToPlan ? "being checked" : RtUnseen ? "not seen while recording" : RtInline ? "inline ones covered" : !Rt ? "mostly covered" : CanRecord ? "need a recording" : "not compiled") : null,
    }.OfType<DetailRow>().ToList();
    public bool HasSources => Sources.Count > 0;

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
        RtUnseen ? Sentence(ScsKiller.RtUnseenNote) + "." : RtInline ? Sentence(ScsKiller.RtInlineNote) + "." : !Rt || tipAsks ? null : CanRecord ? (HasDbTeaser ? ScsKiller.RtNeedsRecording : ScsKiller.RtNote(s.InCommunityDb, App.Account.HasDb)) + "."
            : $"Ray-traced effects aren't compiled: they need a recording, {(s.RiskEligible ? ScsKiller.RiskNoRecording : !NoAntiCheat ? $"which {ScsKiller.AntiCheatName(s)} blocks" : s.RootUnconfirmed ? ScsKiller.ManualNoRecording : ScsKiller.StreamlineNoRecording)}.",
        LeftCase switch
        {
            Left.AntiCheat => s.RiskEligible ? "Anything else compiles while you play: a session on this page can record it, at your own risk."
                : !NoAntiCheat ? $"Anything else compiles while you play: {ScsKiller.AntiCheatName(s)} blocks the recording that would find it."
                : s.RootUnconfirmed ? "Anything else compiles while you play: confirm the game's folder (Game folder… above) to record what's missing."
                : "Anything else compiles while you play: DLSS on NVIDIA blocks the recording that would find it.",
            Left.EngineSlots => ScsKiller.EngineSlotsNote(RecordOn),
            Left.UnknownSlots => "The rest use shader slots SCSKiller can't rebuild yet, so they still compile while you play.",
            Left.NotSeen => ScsKiller.NotSeenNote(RecordOn),
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
    public string TimeLabel => s.LastWarmTime != null ? "Compile time" : "Compile time (estimate)";
    public string TimeValue => s.LastWarmTime is { TotalMinutes: >= 1, TotalHours: < 1 } t ? $"{t.Minutes} min {t.Seconds} s"   // measured: to the second
        : Format.Duration(Fmt.CompileTime(s));
    public bool HasDiskValue => s.CacheOnDisk != null || s.EstimatedCacheBytes != null;
    public string DiskLabel => s.CacheOnDisk != null ? "Disk space" : "Disk space (estimate)";
    public string DiskValue => Format.Bytes(OnDisk ?? s.EstimatedCacheBytes);
    // the kept-aside copy of what the driver removed takes disk space too
    long? OnDisk => s.CacheOnDisk + (s.Kept?.Frees ?? 0);
    public bool HasShaderCount => s.ShaderCount != null;
    public string ShaderCount => Fmt.N(s.ShaderCount);
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
        P is { } p ? new("Pipelines in the plan", Fmt.N(p.Recorded + p.Generated + p.MiddlewareItems)) : null,
        s.ShaderCount != null ? new("Shaders in the game files", Fmt.N(s.ShaderCount)) : null,
        P is { StageSets: > 0 } ? new("Shader combinations found", Fmt.N(P.StageSets)) : null,
        P is { LeftOut: > 0 } ? new("Left out", ScsKiller.LeftOutValue(P)) : null,
        P is { StageSets: 0, Uncovered: > 0 } ? new("Left out for shader slots SCSKiller can't rebuild", Fmt.N(P.Uncovered)) : null,
        P != null ? new("Recorded pipelines", Fmt.N(P.Recorded)) : null,
        P != null ? new("Built from the game files", Fmt.N(P.Generated)) : null,
        P is { MiddlewareItems: > 0 } ? new("Upscaler pipelines", Fmt.N(P.MiddlewareItems)) : null,
        P is { D3D11Shaders: > 0 } ? new("DirectX 11 shaders", Fmt.N(P.D3D11Shaders)) : null,
        P is { RtLibraries: > 0 } ? new("Ray tracing shader libraries", $"{P.RtLibraries:N0}" + (s.Engine?.NoRtPipelines == true ? " (not used by the game)" : s.RtSkipped ? " (skipped, your setting)" : P.RtUncovered > 0 ? $" ({P.RtUncovered:N0} not compiled)" : "")) : null,
        P is { RtInline: > 0 } ? new("Shaders that trace rays inline", Fmt.N(P.RtInline)) : null,
        s.InCommunityDb is { } db && !HasDbTeaser ? new("Community data for your version", db ? $"{s.CommunityDbPsos:N0} pipelines" : "none yet") : null,
        s.Community is { } c ? new("Downloaded community recording", c.OtherVersion ? "doesn't match your game's files" : $"{c.Psos:N0} pipelines") : null,
        s.Official is { } o ? new("Official recording", $"{o.Psos:N0} pipelines") : null,
        P != null ? new("Synthesized pipeline templates", Fmt.N(P.SynthesizedTemplates)) : null,
        P != null ? new("Shader layouts", $"{P.RootSignatures:N0} · {RootSigSource(P)}") : null,
        P is { } u && u.ExactUnits + u.InferredUnits + u.GuessedUnits > 0 ? new("Stage units exact / inferred / guessed", $"{u.ExactUnits:N0} / {u.InferredUnits:N0} / {u.GuessedUnits:N0}") : null,
        P is { LayoutCoverage: > 0 } ? new("Vertex layouts from a recording", $"{P.LayoutCoverage:P1}") : null,
        s.CacheOnDisk != null || s.EstimatedCacheBytes != null ? new("Driver cache", Fmt.Cache(s) + (s.CacheOnDisk != null ? " measured" : " estimated")) : null,
    }.OfType<DetailRow>().ToList();


    // "Shipped with the shaders" is the carver's case.
    string RootSigSource(PlanStats p) =>
        p.RootSigRuleVerified ? "tested on this engine version"
        : p.RootSignatures == 0 ? "not needed (DirectX 11)"
        : s.Engine?.Family == "Unreal" ? (p.Recorded > 0 ? "learned from recording" : "not tested on this engine version yet")
        : p.Recorded > 0 ? "learned from recording" : "shipped with the shaders";

    // Last compile: when, how long, what failed or was skipped and what to do; never compiled: what the first one costs.
    public bool HasCompileCard => CanCompile || s.LastWarmTime != null;
    public string CompileTitle => s.LastWarmTime == null ? "First compile" : "Last compile";
    long Failed => s.LastWarmFailed ?? 0;
    long Skipped => s.LastWarmSkipped ?? 0;
    long Crashed => s.LastWarmCrashed ?? 0;
    long DriverSkipped => s.LastWarmDriverSkipped ?? 0;
    long Unbuilt => s.LastWarmUnbuilt ?? 0;
    public string CompileText => s.WarmedAt is { } at
        ? $"Compiled {Fmt.When(at)}" + (s.LastWarmTime is { } t ? $" in {Format.Duration(t)}" : "") + $", for driver {s.WarmedDriverVersion}."
          + (ScsKiller.NothingFailed(s) ? " Nothing failed." : "")   // null = not known (a warm from before these were kept): no claim
          + (ScsKiller.RefusedLine(s) is { } refused ? " " + refused : "")
          + (OnDisk is { } c ? $" It uses {Format.Bytes(c)} of disk space." : "")
          + " " + OwnShaderStep
        : s.LastWarmTime is { } last ? $"Its shader cache was cleared. The last compile took {Format.Duration(last)}."
        : (s.EstimatedWarmTime is { } est ? $"It takes about {Format.Duration(est)} and runs in the background, so you can keep using the PC." : "It runs in the background, so you can keep using the PC.")
          + (s.EstimatedCacheBytes is { } b ? $" It adds about {Format.Bytes(b)} to the driver's shader cache." : "");
    // no game file says whether a game runs its own precompile at startup: said on every compiled game's page, not its row
    public const string OwnShaderStep = "The game may still run its own shader step when it starts.";
    public string SkippedTitle => $"{Skipped + Crashed + DriverSkipped + Unbuilt:N0} skipped";
    long NeedsRecording => Math.Min(Skipped, s.LastWarmNeedsRecording ?? 0);   // of Skipped: flagged by the community recording
    long NotInGame => Skipped - NeedsRecording;
    public string? SkippedText => s.WarmedAt == null || Skipped + Crashed + DriverSkipped + Unbuilt == 0 ? null
        : string.Join(" ", new[]
        {
            NotInGame > 0 ? $"{NotInGame:N0} {(NotInGame == 1 ? "was" : "were")} skipped: {(NotInGame == 1 ? "its shader isn't" : "their shaders aren't")} in this version of the game. Nothing to do." : null,
            Crashed > 0 ? $"{Crashed:N0} {(Crashed == 1 ? "was" : "were")} skipped: {(Crashed == 1 ? "it crashes" : "they crash")} the GPU driver. If the game uses {(Crashed == 1 ? "it" : "them")}, it compiles {(Crashed == 1 ? "it" : "them")} itself." : null,
            DriverSkipped > 0 ? $"{DriverSkipped:N0} {(DriverSkipped == 1 ? "was" : "were")} skipped: the GPU driver crashed or hung on {(DriverSkipped == 1 ? "it or on what it builds" : "them or on what they build")} on." : null,
            Unbuilt > 0 ? $"{Unbuilt:N0} {(Unbuilt == 1 ? "was" : "were")} skipped: SCSKiller can't build {(Unbuilt == 1 ? "it" : "them")} yet." : null,
            NeedsRecording > 0 ? $"{NeedsRecording:N0} need{(NeedsRecording == 1 ? "s" : "")} a recording on this PC: the game or a mod builds {(NeedsRecording == 1 ? "its shaders" : "their shaders")} while it runs, so they're in no game file. Play with \"Record while I play\" on to catch them." : null,
        }.OfType<string>());
    public bool HasSkipped => SkippedText != null;

    // Last play session (SessionStats, no timestamp): one bar, three segments, under the Record switch. Ready = from the
    // game's library or the driver cache; ray tracing state objects have their own line, so Requests isn't used.
    SessionStats? L => s.LastSession;
    public bool HasSession => L != null;
    public bool ShowSessionHint => L == null && CanRecord;
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
    public string FramesVerdict => F is not { } f ? "" : Format.Frames(f, ShaderHitches);
    public string GraphEnd => Format.Duration(F?.Duration);
    public string FramesSummary => F is not { } f ? ""
        : $"Startup (the game's own shader precompile and first load): {Format.Duration(f.Startup)}. In play: {InPlay(f).Count(h => h.Ms >= 50):N0} frames of 50 ms or more, "
          + $"{InPlay(f).Count(h => h.Ms >= 100):N0} of 100 ms or more. Frame times stay on this PC.";
    // the same list while the report is the same: a new one makes the slow-frames list rebuild every row at each refresh
    public IReadOnlyList<DetailRow> HitchRows => hitchRows.Report == F ? hitchRows.Rows
        : (hitchRows = (F, F?.Hitches.Select(h => new DetailRow($"{Clock(h.At)}  {Cause(h.Cause)}", Ms(h.Ms))).ToList() ?? [])).Rows;
    (FrameReport? Report, IReadOnlyList<DetailRow> Rows) hitchRows = (null, []);
    public static string HitchTip(Hitch h, bool neutral = false) => $"{Clock(h.At)} · {Ms(h.Ms)} · {(neutral ? "hitch" : Cause(h.Cause))}";
    static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    public bool HasHitchRows => F?.Hitches.Count > 0;

    // Earlier launches with frame times (FrameSessions), newest first; the one the section shows is left out. Read again
    // only when the game's state changes.
    public IReadOnlyList<DetailRow> EarlierSessions => earlier.For == s ? earlier.Rows
        : (earlier = (s, App.Core.FrameHistory(id).Where(e => e.LaunchUnixMs != F?.LaunchUnixMs).Select(SessionRow).ToList())).Rows;
    (GameState? For, IReadOnlyList<DetailRow> Rows) earlier = (null, []);
    public bool HasEarlierSessions => EarlierSessions.Count > 0;
    // a cleared recording takes the last launch's frames, not the history
    public bool HasFramesCard => HasFrames || HasEarlierSessions;
    static DetailRow SessionRow(FrameSession e) => new(
        $"{DateTimeOffset.FromUnixTimeMilliseconds(e.LaunchUnixMs).LocalDateTime:g} · {Format.Duration(e.Duration)}",
        Format.Session(e));

    // the frames of play, between startup and quitting, whatever slowed them (a load in play too)
    static IEnumerable<Hitch> InPlay(FrameReport f) => f.Hitches.Where(h => FrameLog.InPlay(h.At.TotalMilliseconds, h.Ms, f.Startup.TotalMilliseconds) && h.Cause != HitchCause.Quitting);
    static string Ms(double ms) => Format.Ms(ms);
    static string Cause(HitchCause c) => c switch { HitchCause.Shader => "shader compile", HitchCause.Other => "other hitch", HitchCause.Quitting => "quitting",
        HitchCause.LoadingShaders => "loading, compiling shaders", _ => "loading" };

    public GridLength LibraryWidth => new(L?.FromGameLibrary ?? 0, GridUnitType.Star);
    public GridLength HitsWidth => new(L?.CacheHits ?? 0, GridUnitType.Star);
    public GridLength CompilesWidth => new(L?.Compiles ?? 0, GridUnitType.Star);
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
    // NVIDIA, DirectX 11, during a before and after pair: the recorder is in, but records nothing
    bool PairFramesOnly => s.Records11 && ScsKiller.Frames11(s.Engine, App.Core.Vendor.Caps);
    public bool RecordOn => ShowOffline ? OfflineOn : !PairFramesOnly && (RecordPending ?? s.RecorderEffective);
    public bool CanToggleRecord => ShowOffline ? OfflinePending == null && !s.OfflineRunning
        : RecordPending == null && AlongsidePending == null && NoAntiCheat && s.RecorderSkip == null && !PairFramesOnly;
    public bool ShowUseDefault => RecordPending == null && s.RecorderOverride != RecorderOverride.Default && s.RecorderSkip == null && !PairFramesOnly && !s.RecorderForeign;
    public string UseDefaultText => $"Use default ({(App.Core.Settings.RecordAllGames && s.NoStutter == null ? "on" : "off")})";
    string RecorderDll => Fmt.RecorderDll(s);
    string Kept => ScsKiller.SessionKeptNote(s) is { } kept ? " " + kept : "";
    public string RecordNote => ScsKiller.CrashNote(s.Game) is { } crash ? crash
        : Risk
        ? "Each session is confirmed before it starts." + Kept
        : ShowOffline
        ? $"{ScsKiller.AntiCheatName(s)} blocks the recorder, but you can enable it at your own risk with an offline session." + Kept
        : s.RiskNoLaunch ? ScsKiller.NoLaunchNote(Fmt.StoreName(s.Game))
        : !NoAntiCheat
        ? $"Not available: {ScsKiller.AntiCheatName(s)} treats an extra {RecorderDll} as tampering, so this game is compiled from its files only."
        : PairFramesOnly ? "Frame times only, for the before and after recording."
        : s.RecorderSkip == ScsKiller.SkipManual ? "Not available until you confirm the game's folder (Game folder… above): SCSKiller checks all of it for anti-cheat before it records."
        : s.RecorderSkip == ScsKiller.SkipModNotChainable ? $"Not available: {ScsKiller.NotChainableReason(s.RecorderMod, RecorderDll)}."
        : s.RecorderSkip is { } skip ? $"Not available: {skip}."
        : $"Adds a small {RecorderDll} next to the game to catch anything the plan missed and time each frame, so this page shows what stuttered. Remove any time. If a game has anti-cheat SCSKiller didn't detect, don't turn this on."
          + (s.RecorderNote is { } note ? $" ({Sentence(note)})" : "")
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

    public bool HasRecording => s.RecordingBytes > 0;
    public string RecordingSize => $"The recording uses {Format.Bytes(s.RecordingBytes)} (in the game folder and SCSKiller's copy)";
    public bool RecordingPaused => s.RecordingPaused;
    public string RecordingPausedText => ScsKiller.PausedNote(App.Core.Settings) + ". Raise the limit in Settings, or clear the recording.";
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
    public string TopLabel => $"Move {Name} to the top";
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

    public QueueVm() => cache = new(() => (App.Core.Vendor.GetCacheUsage(), App.Core.Vendor.GetCacheLimit(), (App.Core.Vendor as AmdBackend)?.AppCache.DxcBytes()),
        c => { driver = c; ShowCache(); Changed(); });
    Settings S { get => App.Core.Settings; set { App.Core.Settings = value; Changed(); } }

    public ObservableCollection<QueueRow> Waiting { get; } = [];
    public ObservableCollection<QueueRow> Finished { get; } = [];
    public bool Dragging { get; set; }   // don't rebuild Waiting under a drag in progress

    public QueueRow Current { get; private set; } = new("", 1, "", "", "", "", "", QueueStage.Waiting);
    public bool HasCurrent => cur != null;
    public bool Idle => cur == null;
    public bool Empty => cur == null && Waiting.Count == 0;
    public bool HasFinished => Finished.Count > 0;
    public bool CanClear => Waiting.Count > 0 || Finished.Count > 0;
    public bool CanStart => !App.Core.QueueRunning && Waiting.Count > 0;
    public bool CanRaiseLimit => ScsKiller.CanRaiseCacheLimit(App.Core.Vendor.Caps, driver?.Limit);
    public string Summary { get; private set; } = "";
    public string? PlanChecks { get; private set; }   // ScsKiller.PlanCheckLine
    public bool HasPlanChecks => PlanChecks != null;

    public string CurrentDetail => cur is null ? "" : Fmt.Stage(cur) + (Fmt.ProgressText(cur) is { } p ? " · " + p : "");
    public bool Indeterminate => Fmt.Indeterminate(cur);
    public double Percent => Fmt.Percent(cur);
    public string PauseText => cur?.Stage == QueueStage.Paused ? "Resume" : "Pause";
    public bool CacheWarn { get; private set; }
    public string CacheWarnText { get; private set; } = "";

    public double Threads { get => Used; set { if ((int)value != Used) S = S with { Threads = (int)value }; } }
    int Used => ScsKiller.UsedThreads(S.Threads);
    public string ThreadsText => $"{Used} of {Environment.ProcessorCount}";
    public double MaxThreads => Environment.ProcessorCount;
    public int Priority { get => (int)S.Priority; set { if (value >= 0 && value != (int)S.Priority) S = S with { Priority = (WarmPriority)value }; } }
    public string CpuSummary => $"CPU use: {Used} of {Environment.ProcessorCount} threads, {(S.Priority == WarmPriority.BelowNormal ? "below-normal" : "idle")} priority";

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
            var time = q.Stage == QueueStage.Done ? Format.Duration(g?.LastWarmTime) : Left(q) is { } t ? Format.Duration(t) + (q.Progress?.PerSecond > 0 ? " left" : "") : Format.Dash;
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
        Summary = pending.Count == 0 ? "Nothing queued"
            : $"{pending.Count} game{(pending.Count == 1 ? "" : "s")} · "
              + (total > TimeSpan.Zero ? $"about {Format.Duration(total)} in total{(unknown ? ", plus plans to build" : "")}" : "time known once the plans are built")
              + (cur == null && !core.QueueRunning ? " · not started" : "");

        planned = pending.Sum(q => games.GetValueOrDefault(q.GameId)?.EstimatedCacheBytes ?? 0);
        var queued = pending.Select(q => games.GetValueOrDefault(q.GameId)).OfType<GameState>().ToList();
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
            : ScsKiller.QueueCacheWarning(sizes, d.Usage.BytesOnDisk, planned, d.Limit?.Bytes, App.Core.Vendor.Vendor)) ?? "";
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
        QueueStage.Stopped when q.Progress is { } p => $"Stopped at {p.Done:N0} of {p.Total:N0}: add it again to continue" + (q.Note is { } n ? " · " + n : ""),
        QueueStage.Stopped => "Stopped",
        QueueStage.Done => q.Note is { } n ? "Done · " + n : "Done",
        _ when q.Note != null => q.Note,
        _ => g?.Plan is { } plan ? $"{plan.Recorded + plan.Generated + plan.MiddlewareItems:N0} pipelines{(plan.D3D11Shaders > 0 ? $" + {plan.D3D11Shaders:N0} DirectX 11 shaders" : "")}" : "plan is built first",
    };

    public void PauseOrResume() => Fmt.PauseOrResume(cur);
}

public sealed class SettingsVm : Bindable
{
    public static readonly (string Label, CacheLimit Limit)[] Sizes =
    [
        ("16 GB (driver default)", new(16L << 30, true)), ("32 GB", new(32L << 30, false)),
        ("100 GB", new(100L << 30, false)), ("Unlimited", new(null, false)),
    ];
    public static readonly string[] SizeLabels = Sizes.Select(s => s.Label).ToArray();

    Settings S { get => App.Core.Settings; set { App.Core.Settings = value; Refresh(); } }
    IGpuVendorBackend V => App.Core.Vendor;
    // The driver cache's limit and usage, the drive's free space and NVIDIA's Auto Shader Compilation, read off the UI thread; null until the first read.
    (CacheLimit? Limit, CacheUsage Usage, string Free, AutoShaderState? Auto)? read;
    readonly BackgroundRead<(CacheLimit?, CacheUsage, string, AutoShaderState?)> cache;
    long? amdUsed;   // AMD: the DirectX 12 cache, which its fixed limit counts

    public SettingsVm()
    {
        cache = new(() =>
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
            if (read == null) SizeIndex = Array.FindIndex(Sizes, s => !s.Limit.IsDriverDefault && s.Limit == r.Item1) is >= 0 and var i ? i : 2;
            // The checkbox and level show the driver's state after every read (an apply's re-read included); Medium when off.
            AutoOn = r.Item4 is { Level: not AutoShaderCompilation.Off };
            AutoLevelIndex = r.Item4 is { Level: not AutoShaderCompilation.Off and var l } ? (int)l - 1 : 1;
            read = r;
            Changed();
        });
        cache.Request();
    }

    public string CacheTitle => $"{Fmt.Vendor(V.Vendor)} shader cache size";
    public bool IsNvidia => V.Vendor == GpuVendor.Nvidia;

    public static readonly string[] AutoLevels = ["Low", "Medium", "High"];   // AutoShaderCompilation.Low..High, as the NVIDIA App names them
    public bool? AutoOn { get; set; }
    public int AutoLevelIndex { get; set; } = 1;
    public AutoShaderCompilation AutoChoice => AutoOn == true ? (AutoShaderCompilation)(Math.Clamp(AutoLevelIndex, 0, 2) + 1) : AutoShaderCompilation.Off;
    public string AutoShaderText => read == null ? Format.Dash : read.Value.Auto switch
    {
        null => "Couldn't read the setting",
        { Level: AutoShaderCompilation.Off } => "Now: Off",
        { TaskReady: true, Level: var l } => $"Now: On, {l}",
        _ => "Setting on but the NVIDIA task isn't set up: click Apply to repair",
    };
    public string? AutoMessage { get; set; }
    public Microsoft.UI.Xaml.Controls.InfoBarSeverity AutoSeverity { get; set; }
    public bool HasAutoMessage => AutoMessage != null;
    public string CacheIntro => (IsNvidia ? "Global driver setting, the same one as NVIDIA App › Graphics › Shader Cache Size. " : "Global driver setting. ")
        + (KeptOn ? "When the cache is full the driver deletes older entries. Kept games are put back." : "When the cache is full the driver deletes older entries, and those games stutter again.");
    public bool Configurable => V.Caps.CacheSizeConfigurable;
    public bool NotConfigurable => !Configurable;
    public string NotConfigurableText => V.Vendor == GpuVendor.Amd
        ? $"The AMD driver's DirectX 12 shader cache is fixed at {Format.Bytes(AmdAppCache.DxcCacheCap)}, with no size setting. "
          + "Past it, the driver removes the least recently used games' caches when a game starts" + (KeptOn ? ". Kept games are put back." : ", and SCSKiller shows those games as needing a compile again.")
        : V.Vendor == GpuVendor.Intel
        ? $"The Intel driver keeps up to {Format.Bytes(IntelBackend.PerGameCap)} of shaders per game, with no size setting. "
          + "Past it, the driver drops that game's older shaders, so SCSKiller compiles the most likely ones first."
        : $"SCSKiller can't change the cache size of {Fmt.Vendor(V.Vendor)} drivers yet, so it only shows what is used. "
          + "If games get evicted, raise the limit in your GPU vendor's control panel.";

    public string CurrentLimit => read == null ? Format.Dash : read.Value.Limit switch
    {
        null => "Unknown",
        { Bytes: null } => "Unlimited",
        { Bytes: 0 } => "Disabled",
        { Bytes: { } b, IsDriverDefault: var d } => Format.Bytes(b) + (d ? " (driver default)" : ""),
    };
    public string Used => read is { Usage: var u } ? (u.UpperBound ? "≤ " : "") + Format.Bytes(u.BytesOnDisk) : Format.Dash;
    public bool HasAmdUse => amdUsed != null;
    public double AmdPercent => amdUsed is { } b ? Math.Min(100, 100.0 * b / AmdAppCache.DxcCacheCap) : 0;
    public string AmdUse => amdUsed is { } b ? $"DirectX 12 cache: {Format.Bytes(b)} of {Format.Bytes(AmdAppCache.DxcCacheCap)}"
        + (b >= AmdAppCache.DxcCacheCap * 9 / 10 ? ". Nearly full: the driver removes the least recently used games' caches when a game starts." : "") : "";
    public bool IsAmd => V.Vendor == GpuVendor.Amd;
    bool KeptOn => IsAmd && S.KeepCachesAside;
    public bool KeepCachesAside { get => S.KeepCachesAside; set { if (value != S.KeepCachesAside) S = S with { KeepCachesAside = value }; } }
    public static IReadOnlyList<GameState> KeptGames => App.Core.Games.Where(g => g.Kept != null).OrderByDescending(g => g.Kept!.Frees).ThenBy(g => g.Game.Name).ToList();
    public bool HasKept => S.KeepCachesAside && KeptGames.Count > 0;
    public string KeptSummary => KeptGames is var k && k.Count > 0
        ? $"{k.Count} game{(k.Count == 1 ? "" : "s")}, {Format.Bytes(k.Sum(g => g.Kept!.Bytes))} kept aside · frees {Format.Bytes(k.Sum(g => g.Kept!.Frees))}" : "";
    // through a link (the cache moved to another drive with a junction): the drive its files are on
    static string DriveOf(CacheUsage? u) => Path.GetPathRoot(u?.Path is { Length: > 0 } p ? GameFiles.FinalPath(p) ?? p : null) is { Length: > 0 } r ? r
        : Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!;
    string? drive;   // read with the usage: resolving a link opens the folder
    public string FreeLabel => $"Free on {(drive ?? DriveOf(null)).TrimEnd('\\')}";
    public string Free => read?.Free ?? Format.Dash;

    public int SizeIndex { get; set; } = 2;
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
        get => (int)S.OnDriverUpdate;
        set
        {
            if (value < 0 || value == DriverMode) return;
            S = S with { OnDriverUpdate = (DriverUpdateMode)value };
            try { App.Core.ApplyDriverUpdateMode(); }  // user action: registers or removes the sign-in check
            catch (Exception e) { ApplyMessage = "Couldn't update the sign-in check: " + e.Message; ApplySeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error; Changed(); }
        }
    }
    public bool? PauseWhileGaming { get => S.PauseWhileGaming; set { if (value is { } v && v != S.PauseWhileGaming) S = S with { PauseWhileGaming = v }; } }
    public double BackgroundThreads { get => UsedBackground; set { if ((int)value != UsedBackground) S = S with { BackgroundThreads = (int)value }; } }
    int UsedBackground => ScsKiller.UsedThreads(S.BackgroundThreads);
    public string BackgroundThreadsText => $"{UsedBackground} of {Environment.ProcessorCount}";
    public double MaxThreads => Environment.ProcessorCount;
    public bool HasLastRebuilt => LastRebuilt != "";
    public string LastRebuilt => App.Core.Games.Where(g => g.WarmedAt != null && g.WarmedDriverVersion != null).MaxBy(g => g.WarmedAt)?.WarmedDriverVersion is { } v ? $"Last rebuilt for driver {v}" : "";

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
                    or ScsKiller.SkipVulkanMod or ScsKiller.SkipNeedsAdmin or ScsKiller.SkipPackageD3D12 or ScsKiller.SkipStreamline)
                .GroupBy(g => g.RecorderSkip).OrderByDescending(x => x.Count()).Select(x => $" · {x.Count()} skipped: {x.Key}");
            int n = games.Count(g => g.RecorderInstalled);
            return $"Recording in {n} game{(n == 1 ? "" : "s")}" + string.Concat(skipped);
        }
    }

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
        ? $"Recordings use {Format.Bytes(r.Sum(g => g.RecordingBytes))} in {r.Count} game{(r.Count == 1 ? "" : "s")}" : "No recordings on disk";
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
    public bool? ScanAtStart { get => S.ScanAtStart; set { if (value is { } v && v != S.ScanAtStart) S = S with { ScanAtStart = v }; } }
    public bool? HddMode { get => S.HddMode; set { if (value is { } v && v != S.HddMode) S = S with { HddMode = v }; } }
    public bool? CloseQuits { get => S.CloseQuits; set { if (value is { } v && v != S.CloseQuits) S = S with { CloseQuits = v }; } }
    public bool HasIntelGpu { get; } = App.Core.HasIntelGpu;
    public bool? ExperimentalIntel { get => S.ExperimentalIntel; set { if (value is { } v && v != S.ExperimentalIntel) S = S with { ExperimentalIntel = v }; } }
    // The GPU picker, shown only with more than one GPU SCSKiller compiles for; the first entry is the default rule.
    public IReadOnlyList<GpuChoice> GpuChoices { get; } = App.Core.GpuChoices;
    public bool HasGpuChoices => GpuChoices.Count > 1;
    public IReadOnlyList<string> GpuLabels => ["Automatic: the GPU with the most video memory", .. GpuChoices.Select(c => c.Name)];
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

    public static readonly string[] Themes = ["Use Windows setting", "Light", "Dark"];   // AppTheme.System..Dark
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


    public double Threads { get => UsedThreads; set { if ((int)value != UsedThreads) S = S with { Threads = (int)value }; } }
    int UsedThreads => ScsKiller.UsedThreads(S.Threads);
    public string ThreadsText => $"{UsedThreads} of {Environment.ProcessorCount}";
    /// <summary>0 = Auto; 1 means 2 (the smallest budget offered).</summary>
    public double MaxCompileMemory { get => ShownMemory; set { var v = (int)value == 1 ? 2 : (int)value; if ((int)value != ShownMemory && v != S.MaxCompileMemoryGB) S = S with { MaxCompileMemoryGB = v }; } }
    int ShownMemory => Math.Min(S.MaxCompileMemoryGB, (int)MaxCompileMemoryMax);
    public string MaxCompileMemoryNote => $"Auto picks the best limit for this PC's memory ({ScsKiller.AutoCompileMemoryGB()} GB here, with {ScsKiller.RamGB()} GB of RAM). Set it lower to keep more memory free while compiling, higher to compile faster.";
    public double MaxCompileMemoryMax => ScsKiller.MaxCompileMemoryGB(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    public string MaxCompileMemoryText => S.MaxCompileMemoryGB > 0 ? $"{S.MaxCompileMemoryGB} GB" : $"Auto ({ScsKiller.AutoCompileMemoryGB()} GB)";
    public string PriorityNote => (S.Priority == WarmPriority.BelowNormal ? "Below-normal priority." : "Idle priority.") + " Fewer threads = slower compile, quieter PC.";
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
    public string? DbNote => !A.SignedIn ? "The list of games it covers is checked for everyone. The shared hashes need a Patreon subscription."
        : A.Status is not { } st ? null   // not read yet: the status line above says "Checking…"
        : !A.HasDb ? "Your Patreon membership doesn't include it right now. What was downloaded before keeps working."
        : (App.Core as ScsKiller)?.Community?.Problem;
    public bool HasDbNote => DbNote != null;

    public string Benefits => A.Status switch
    {
        null => A.Busy ? "Checking your supporter status…" : "Supporter status not checked yet.",
        { Benefits: { } b } => b,   // the flag names live in Core (AccountStatus) so they're testable
        _ => "No supporter benefits right now. They start once your Patreon membership is active: then click Refresh status.",
    };

    // Update channel: a picker only when the token grants more than stable.
    static readonly Dictionary<string, string> ChannelNames = new()
    {
        [UpdateChannels.Stable] = "Stable", [UpdateChannels.Beta] = "Beta (Patreon supporter)",
        [UpdateChannels.Alpha] = "Alpha (Patreon backer)", [UpdateChannels.Internal] = "Internal",
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
    public string ChannelNote => Updater.Checking ? "Checking for updates…"
        : Updater.Ready is { } v ? AutoInstall.ReadyNote(v, Updater.InstallsAutomatically)
        : (Updater.InstallsAutomatically ? "Updates download in the background and install the next time SCSKiller starts or quits, never during a compile or a game."
            : "Updates download in the background, and Restart to update installs them.") + " Leaving an early channel keeps this build until Stable passes it.";
    public string? UpdateProblem => Updater.Problem;
    public bool HasUpdateProblem => Updater.Problem != null;
}

/// <summary>A cache folder on the Cache page.</summary>
public sealed record CacheFolderRow(string Label, string Path, string Size, string? Note)
{
    public bool HasNote => Note != null;
}

/// <summary>A cache item; Folder: where to open it, null when its files are in a folder listed above.</summary>
public sealed record CacheItemRow(string Name, string Size, string? Folder, string? Note, CacheItem Item, CacheGroupKind Kind, bool Enabled)
{
    public bool HasFolder => Folder != null;
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
    readonly BackgroundRead<CacheInventory> cache;

    public CacheVm()
    {
        cache = new(() => App.Core.CacheInventory(), r => { read = r; Changed(); });
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
    public string Total => read == null ? "Reading the caches…" : $"{Format.Bytes(read.Groups.Sum(g => g.Bytes))} of shader caches";
    public bool NoFolders => read is { Folders.Count: 0 };

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
            Result = $"Deleted {Format.Bytes(r.Bytes)} in {r.Files} file{(r.Files == 1 ? "" : "s")}."
                + (r.Skipped.Count > 0 ? $" Skipped {r.Skipped.Count}:\n" + string.Join("\n", r.Skipped) : "");
            ResultSeverity = r.Skipped.Count > 0 ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;
        }
        catch (Exception e)
        {
            (Result, ResultSeverity) = ($"Nothing was deleted: {e.Message}", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        }
        Deleting = false;
        Refresh();
        Changed();
    }

    long OldDriver => read?.Bytes(CacheGroupKind.OldDriver) ?? 0;
    public bool HasOldDriver => OldDriver > 0;
    public string OldDriverText => $"{Format.Bytes(OldDriver)} of cache files are from before the current driver was installed on {read?.DriverInstalled?.ToLocalTime():d}. It doesn't read them.";
    public string FreeText => $"Free {Format.Bytes(OldDriver)}";
    public IReadOnlyList<CacheItem> OldDriverItems => read?.Groups.Where(g => g.Kind == CacheGroupKind.OldDriver).SelectMany(g => g.Items).ToList() ?? [];

    public IReadOnlyList<CacheFolderRow> Folders => read?.Folders.Select(f => new CacheFolderRow(f.Label, f.Path,
        f.Cap is { } cap ? $"{Format.Bytes(f.Bytes)} of {Format.Bytes(cap)}" : Format.Bytes(f.Bytes),
        f.TargetMissing ? "This folder is a link whose target is missing"
        : f.Cap is { } full && f.Bytes >= full * 9 / 10 ? "Nearly full: the driver removes the least recently used games' caches when a game starts"
        : f.Final != null ? $"Stored in {f.Final} through a link"
        : !f.Exists ? "Not created yet" : null)).ToList() ?? [];

    public IReadOnlyList<CacheGroupRow> Groups => read?.Groups.Select(g =>
    {
        var (title, caption) = Texts(g.Kind, read.DriverInstalled);
        var items = g.Items.Select(i => new CacheItemRow(ItemName(g.Kind, i), Format.Bytes(i.Bytes), Outside(i.Files), i.Note, i, g.Kind, CanDelete)).ToList();
        return new CacheGroupRow(title, Format.Bytes(g.Bytes), caption, g.Note, items, g.Kind != CacheGroupKind.Unattributed, g, CanDelete);
    }).ToList() ?? [];

    static string ItemName(CacheGroupKind kind, CacheItem i) => kind == CacheGroupKind.Unattributed && i.Name.Length == 8 && i.Name.All(char.IsAsciiHexDigit) ? $"Unknown app {i.Name}" : i.Name;

    public static (string Title, string Caption) Texts(CacheGroupKind kind, DateTime? installed) => kind switch
    {
        CacheGroupKind.Installed => ("Installed games", "Games in your library, by the cache keys SCSKiller learned for them."),
        CacheGroupKind.Uninstalled => ("Uninstalled games", "Games SCSKiller compiled or saw running that aren't in your library now. Named by their exe."),
        CacheGroupKind.OldDriver => ("Old driver", $"Written before the current driver was installed on {installed?.ToLocalTime():d}. It doesn't read them."),
        CacheGroupKind.OldBuild => ("Earlier SCSKiller compiles", "Left by earlier compiles. The games don't read them."),
        CacheGroupKind.OtherVendor => ("Other GPU vendor", "Caches for another brand of GPU than the one SCSKiller compiles for."),
        _ => ("Other apps and games", "Desktop apps, and games SCSKiller hasn't compiled or seen running. A game moves to its group once SCSKiller sees it running."),
    };

    /// <summary>The confirm's text for deleting <paramref name="items"/> of one group: what happens after.</summary>
    public static string Consequence(CacheGroupKind kind, IReadOnlyList<CacheItem> items) => kind switch
    {
        CacheGroupKind.Installed or CacheGroupKind.Uninstalled =>
            $"These games compile their shaders again on their next compile, and stutter until then: {string.Join(", ", items.Select(i => i.Name))}.",
        CacheGroupKind.OldDriver => "The current driver doesn't read these files. Nothing needs to compile again.",
        CacheGroupKind.OldBuild => "The games don't read these files. Nothing needs to compile again.",
        CacheGroupKind.OtherVendor => "Apps that run on that GPU build their cache again as they run.",
        _ => "These apps and games build their cache again as they run, and may stutter until they have.",
    } + string.Concat(items.Select(i => i.Note).OfType<string>().Select(n => " " + n)) + " Files in use are skipped.";

    /// <summary>The folder holding all these files, when it isn't one of the cache folders listed above.</summary>
    string? Outside(IReadOnlyList<CacheFile> files)
    {
        if (files.Count == 0 || Path.GetDirectoryName(files[0].Path) is not { } dir) return null;
        while (!files.All(f => GameFiles.Inside(dir, f.Path)) && Path.GetDirectoryName(dir) is { } up) dir = up;
        return read!.Folders.Any(f => string.Equals(GameFiles.DirKey(f.Path), GameFiles.DirKey(dir), StringComparison.OrdinalIgnoreCase)) ? null : dir;
    }
}
