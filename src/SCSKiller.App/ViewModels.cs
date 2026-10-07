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
}

static class Fmt
{
    public static string N(long? n) => n?.ToString("N0") ?? Format.Dash;
    /// <summary>"today at 20:14", "yesterday at 20:14", else "on 26/09/2026" (the culture's short date).</summary>
    public static string When(DateTimeOffset at)
    {
        var t = at.LocalDateTime;
        return t.Date == DateTime.Today ? $"today at {t:t}" : t.Date == DateTime.Today.AddDays(-1) ? $"yesterday at {t:t}" : $"on {t:d}";
    }
    public static string Vendor(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => "NVIDIA", GpuVendor.Amd => "AMD", GpuVendor.Intel => "Intel", _ => "GPU",
    };
    public static string Engine(EngineInfo? e) => e is null ? "Unknown engine" : e.Family == "Unreal" ? $"UE {e.Version}" : $"{e.Family} {e.Version}";
    public static string AntiCheatName(AntiCheat a) => a switch
    {
        AntiCheat.EasyAntiCheat => "EasyAntiCheat", AntiCheat.BattlEye => "BattlEye", _ => "anti-cheat",
    };
    public static Style Style(string key) => (Style)Application.Current.Resources[key];
    /// <summary>"Severe shader-compilation stutter: reason", then the source and when it was checked; null when not listed.</summary>
    public static string? StutterTip(GameState s) => s.KnownStutter is { } k
        ? $"{(k.Severity == StutterSeverity.Severe ? "Severe" : "Moderate")} shader-compilation stutter: {k.Reason}.\nSource: {k.Source} (checked {k.Date})" : null;
    /// <summary>Library sections, in list order. GOG, Ubisoft, Battle.net, PURPLE, HoYoPlay and Gaijin games are Store.Other: told apart by their id prefix.</summary>
    public static readonly string[] Stores = ["Steam", "Epic Games", "Xbox / Game Pass", "EA app", "GOG", "Ubisoft Connect", "Battle.net", "PURPLE", "HoYoPlay", "Gaijin", "Other", AddedByYou];
    public const string AddedByYou = "Added by you";
    public static string StoreName(Game g) => g.Store switch
    {
        Store.Steam => "Steam", Store.Epic => "Epic Games", Store.Xbox => "Xbox / Game Pass", Store.EA => "EA app", Store.Manual => AddedByYou,
        _ => g.Id[..Math.Max(0, g.Id.IndexOf(':'))] switch { "gog" => "GOG", "ubisoft" => "Ubisoft Connect", "battlenet" => "Battle.net", "purple" => "PURPLE", "hoyoplay" => "HoYoPlay", "gaijin" => "Gaijin", _ => "Other" },
    };
    /// <summary>Measured driver cache when known, else the estimate.</summary>
    public static string Cache(GameState s) => s.CacheOnDisk is { } c ? Format.Bytes(c) : s.EstimatedCacheBytes is { } b ? "≈ " + Format.Bytes(b) : Format.Dash;
    /// <summary>Tooltip for an estimated (not measured) cache value; null when measured.</summary>
    public static string? CacheTip(GameState s) => s.CacheOnDisk == null && s.EstimatedCacheBytes != null ? "Estimated driver cache if compiled" : null;
    /// <summary>One middleware tag: what it compiles and its DLLs.</summary>
    public static string TagTip(MiddlewareTag t) => Format.Middleware(t) + "\n    " + string.Join(", ", t.Dlls);
    public static bool Active(QueueItem q) => q.Stage is not (QueueStage.Done or QueueStage.Failed or QueueStage.Stopped);
    public static void PauseOrResume(QueueItem? running)
    {
        if (running?.Stage == QueueStage.Paused) App.Core.ResumeQueue(); else App.Core.PauseQueue();
    }

    /// <summary>The game's compile runs (or is paused), or is the next the running queue starts: Play waits, since the warm
    /// stages a process under the game's exe name. A stopped queue's items and "when idle" items don't block it.</summary>
    public static bool CompilingSoon(IEnumerable<QueueItem> queue, string gameId)
    {
        var compiles = queue.Where(q => !q.PlanCheck).ToList();
        return compiles.Any(q => q.GameId == gameId && Format.Running(q))
            || App.Core.QueueRunning && compiles.FirstOrDefault(q => q.Stage == QueueStage.Waiting && q.Note != ScsKiller.WhenIdleNote)?.GameId == gameId;
    }

    public const string CompilingTip = "Compiling: play when it finishes";


    /// <summary>Starts the game through its store (<see cref="StoreLaunch"/>); the error to show, or null. The design data starts nothing.</summary>
    public static string? Play(Game g)
    {
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
    public static string PlayVia(Game g) => g.Store == Store.Manual ? $"Start {Path.GetFileName(g.ExePath)}" : $"Play through {StoreName(g)}";
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

public sealed class GameRow(GameState s, bool queued = false, bool compiling = false)
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
    public ImageSource? Icon { get; } = Icons.Get(s.Game.ExePath);
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
    public bool HasTags => s.Middleware is { Count: > 0 };
    public bool HasTagLine => HasTags || IsKnownStutter;
    public bool IsKnownStutter => s.KnownStutter != null;
    public string? StutterTip => Fmt.StutterTip(s);
    // The first three, then "+N" with the rest in its tooltip. After "Known to stutter" only one more chip fits before the
    // Shaders column: several become "N upscalers".
    const int MaxTags = 3;
    public List<TagChip> Tags => s.Middleware is not { Count: > 0 } m ? []
        : IsKnownStutter && m.Count > 1 ? [new TagChip($"{m.Count} upscalers", string.Join("\n", m.Select(Fmt.TagTip)))]
        : [.. m.Take(MaxTags).Select(t => new TagChip(t.Label, Fmt.TagTip(t))),
           .. m.Count > MaxTags ? [new TagChip($"+{m.Count - MaxTags}", string.Join("\n", m.Skip(MaxTags).Select(Fmt.TagTip)))] : Array.Empty<TagChip>()];
    public string Shaders => Fmt.N(s.ShaderCount);
    public string Pipelines => s.Plan is { } p ? Fmt.N(p.Recorded + p.Generated + p.MiddlewareItems) : Format.Dash;
    public string Cache => Fmt.Cache(s);
    public string? CacheTip => Fmt.CacheTip(s);
    public Style CacheStyle => Fmt.Style(CacheTip != null ? "Secondary" : "BodyTextBlockStyle");
    public string Time => Format.Duration(s.EstimatedWarmTime);

    bool Partly => ScsKiller.IsPartlyWarmed(s);
    public string StatusText => s.Status switch
    {
        GameStatus.Warmed when Partly => "Partly warmed",
        GameStatus.Warmed => "Warmed",
        GameStatus.Ready => "Ready to compile",
        GameStatus.NeedsRecording => ScsKiller.RecordedEnough(s) ? "Needs a recording" : "Needs a 5-min recording",
        GameStatus.Stale => "Needs rebuilding",
        _ => s.ShaderModBlocks ? "Not compiled" : s.Engine?.Encrypted == true ? "Encrypted game files" : "Not supported yet",
    };
    /// <summary>The whole reason: the row's tooltip and the game page's status text.</summary>
    public string FullNote => s.Status switch
    {
        GameStatus.Warmed when Partly => $"driver {s.WarmedDriverVersion} · {s.Careful!.LaunchCompiled * 100:0}% still compiled at its first launch",
        GameStatus.Warmed => $"driver {s.WarmedDriverVersion}" + (ScsKiller.IsPartial(s.Plan) ? " · a recording compiles the rest"
            : ScsKiller.RtAfterRecording(s) ? " · " + ScsKiller.RtAfterRecordingNote : ""),
        GameStatus.NeedsRecording when s.AntiCheat != AntiCheat.None => $"{Fmt.AntiCheatName(s.AntiCheat)} blocks recording",
        GameStatus.NeedsRecording when s.RecordingPaused => ScsKiller.PausedNote(App.Core.Settings),
        GameStatus.NeedsRecording when s.RecorderInstalled && !ScsKiller.RecordedEnough(s) => "recorder on: play for about 5 minutes",
        _ => s.StatusReason,
    } + ModNote(s);
    /// <summary>A shader mod that doesn't block the game: "; RenoDX changes this game's pipelines: ...".</summary>
    internal static string ModNote(GameState s) => s is { ShaderMod: not null, ShaderModBlocks: false } ? "; " + ScsKiller.ShaderModNote(s) : "";
    /// <summary>The row's note under the status: a few words (<see cref="Format.ShortNote"/>); null when the status says it all.</summary>
    public string? Note => Format.ShortNote(s);
    public string RowNote => Playing ? "Playing now" + (Note is { } n ? " · " + n : "") : Note ?? "";
    public bool HasRowNote => RowNote.Length > 0;
    public string RowTip => (Playing ? "Playing now · " : "") + FullNote;
    public Style StatusStyle => Fmt.Style(s.Status switch
    {
        GameStatus.Warmed when Partly => "StatusWarn",
        GameStatus.Warmed => "StatusAccent",
        GameStatus.Ready => "StatusPrimary",
        GameStatus.Unsupported => "StatusMuted",
        _ => "StatusWarn",
    });

    // One action per status (Main.dc.html): Details / Add to queue / Record / disabled Why?
    public bool Queued => queued;
    public bool IsAdd => s.Status is GameStatus.Ready or GameStatus.Stale;
    public string AddText => queued ? "In queue" : "Add to queue";
    public bool CanAdd => !queued;
    public bool IsRecord => s.Status == GameStatus.NeedsRecording && !s.RecorderInstalled;
    public bool CanRecord => s.AntiCheat == AntiCheat.None;
    public bool IsDetails => s.Status == GameStatus.Warmed || (s.Status == GameStatus.NeedsRecording && s.RecorderInstalled);
    public bool IsWhy => s.Status == GameStatus.Unsupported;
    public bool IsEncrypted => s.Engine?.Encrypted == true;
    public string Reason => s.StatusReason;

    // Play goes through the store (a game the user added: its exe); never for an anti-cheat game (SCSKiller never launches them)
    public bool CanLaunch { get; } = s.AntiCheat == AntiCheat.None && StoreLaunch.Supported(s.Game);
    public bool Compiling => compiling;
    public bool CanPlay => !Playing && !compiling;
    public string PlayText => Playing ? "Running" : "Play";
    public string PlayTip => Playing ? $"{Name} is running" : compiling ? Fmt.CompilingTip : Fmt.PlayVia(s.Game);

    public override string ToString() => $"{Name}, {StatusText}, {RowTip}";   // list item name for screen readers
}

/// <summary>One store's section of the library list (the rows shown after the search filter).</summary>
public sealed class StoreGroup(string store, IEnumerable<GameRow> rows) : ObservableCollection<GameRow>(rows)
{
    public const string Recommended = "Recommended to compile";
    public string Store => store;
    public string CountText => $"{Count} game{(Count == 1 ? "" : "s")}" + (store == Recommended ? RecommendedNote() : "");
    // a row replaced in place (compiled, cleared) changes the count of compiled ones
    protected override void OnCollectionChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        base.OnCollectionChanged(e);
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(CountText)));
    }
    string RecommendedNote() => this.Count(r => r.State.Status == GameStatus.Warmed) is var done and > 0
        ? $" · known to stutter · {done} compiled" : " · known to stutter";
}

public sealed class LibraryVm : Bindable
{
    static bool scanned;
    readonly BackgroundRead<(CacheUsage Usage, CacheLimit? Limit, long? Capped)> cache;

    public LibraryVm() => cache = new(() => (App.Core.Vendor.GetCacheUsage(), App.Core.Vendor.GetCacheLimit(), (App.Core.Vendor as AmdBackend)?.AppCache.DxcBytes()), ShowCache);

    public ObservableCollection<GameRow> Games { get; } = [];   // every game, sorted; the list shows Groups
    /// <summary>The list: rows matching <see cref="Filter"/> per store, empty stores left out. Filtering reuses the rows
    /// (no icon reads, no IO).</summary>
    public ObservableCollection<StoreGroup> Groups { get; } = [];
    string filter = "";
    public string Filter { get => filter; set { if (value.Trim() != filter) { filter = value.Trim(); ApplyFilter(); Changed(); } } }
    int shown;
    public bool Filtering => filter.Length > 0;
    public string ShownText => $"{shown} of {Games.Count} shown";
    public bool NoMatch => Filtering && Games.Count > 0 && shown == 0;
    public string NoMatchText => $"No games match “{filter}”";
    public bool Scanning { get; private set; }
    int refreshing;   // Rescan calls not finished: the scan, then a user refresh's server fetches
    public bool Refreshing => refreshing > 0;
    public string? RefreshNote { get; private set; }
    public bool HasRefreshNote => RefreshNote != null;
    public string? Error { get; private set; }
    public bool HasError => Error != null;
    public string? GpuNotice => Format.GpuNotice(App.Core.Vendor.Gpu, App.Core.Settings.GpuNoticeDismissed);
    public bool HasGpuNotice => GpuNotice != null;
    public void DismissGpuNotice()
    {
        App.Core.Settings = App.Core.Settings with { GpuNoticeDismissed = App.Core.Vendor.Gpu.Name };
        Changed();
    }

    public string Summary { get; private set; } = "";
    public int ReadyCount { get; private set; }     // ready and not queued yet
    public int WaitingCount { get; private set; }
    public string AddAllText => $"Add all ready ({ReadyCount})";
    public bool CanAddAll => ReadyCount > 0 && !Scanning;
    public int RecommendedCount { get; private set; }   // known to stutter, ready and not queued yet
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
        if (!scanned) Rescan(force: false);
    }


    // The scan message waits 300 ms so a cached scan doesn't flicker it; the list fills when the scan ends.
    bool slowScan, forced;
    public bool ScanEmpty => slowScan && Games.Count == 0;
    public bool ScanBusy => slowScan && Games.Count > 0;
    public string ScanEmptyNote => "Checking Steam, Epic, Xbox, EA, GOG, Ubisoft Connect, Battle.net, PURPLE, HoYoPlay and Gaijin, then which engine each game uses. "
        + "The first scan reads every game's files, so it can take a minute.";
    public string ScanBusyNote => forced ? "Re-reading every game's engine and anti-cheat; the list updates when it's done."
        : "The list updates when it's done.";

    public async void Rescan(bool force = true, bool userRequested = false)
    {
        Scanning = true; refreshing++; forced = force; Error = RefreshNote = null; Refresh();   // the summary says "Looking for games…" while nothing is listed
        var scan = force ? App.Core.RescanAsync(CancellationToken.None, userRequested) : App.Core.ScanAsync(CancellationToken.None, userRequested);
        if (await Task.WhenAny(scan, Task.Delay(300)) != scan) { slowScan = true; Changed(); }
        try { await scan; scanned = true; }
        catch (Exception e) { Error = "Scan failed: " + e.Message; }
        Scanning = slowScan = false;
        Refresh();
        if (userRequested && Error == null && App.Core is ScsKiller k)
            RefreshNote = await k.ServerRefresh switch
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
        var list = core.Games.OrderBy(g => g.Status switch
        {
            GameStatus.Stale => 0, GameStatus.Warmed => 1, GameStatus.Ready => 2, GameStatus.NeedsRecording => 3, _ => 4,
        }).ThenBy(g => g.Game.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var queue = core.Queue.Where(q => !q.PlanCheck).ToList();
        var queued = queue.Where(Fmt.Active).Select(q => q.GameId).ToHashSet();
        GameRow Row(GameState g) => new(g, queued.Contains(g.Game.Id), Fmt.CompilingSoon(queue, g.Game.Id));
        if (!Games.Select(r => r.Id).SequenceEqual(list.Select(g => g.Game.Id)))
        {
            Games.Clear();
            foreach (var g in list) Games.Add(Row(g));
        }
        else
        {
            for (int i = 0; i < list.Count; i++)
                if (Games[i].State != list[i] || Games[i].Queued != queued.Contains(list[i].Game.Id) || Games[i].Compiling != Fmt.CompilingSoon(queue, list[i].Game.Id)
                    || Games[i].Icon != Icons.Get(list[i].Game.ExePath)) Games[i] = Row(list[i]);
        }
        ApplyFilter();

        int ready = list.Count(g => g.Status is GameStatus.Ready or GameStatus.Stale);
        ReadyCount = Games.Count(r => r.IsAdd && !r.Queued);
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

    /// <summary>Games -> Groups: same order within a store (status, then name). The sections are rebuilt only when which
    /// rows show changes; a row whose state changed is swapped in place.</summary>
    void ApplyFilter()
    {
        var visible = Games.Where(r => filter.Length == 0 || r.Matches(filter)).ToList();
        // known-stutter games go on top (StutterList.Recommended), out of their store's section, compiled or not
        var byId = visible.ToDictionary(r => r.Id);
        var recommended = StutterList.Recommended(visible.Select(r => r.State)).Select(s => byId[s.Game.Id]).ToList();
        var groups = visible.Except(recommended).GroupBy(r => r.StoreName)
            .OrderBy(g => Array.IndexOf(Fmt.Stores, g.Key)).Select(g => (g.Key, Rows: g.ToList())).ToList();
        if (recommended.Count > 0) groups.Insert(0, (StoreGroup.Recommended, recommended));
        shown = groups.Sum(g => g.Rows.Count);
        if (groups.Select(g => g.Key).SequenceEqual(Groups.Select(g => g.Store))
            && groups.Zip(Groups).All(p => p.First.Rows.Select(r => r.Id).SequenceEqual(p.Second.Select(r => r.Id))))
        {
            foreach (var (want, have) in groups.Zip(Groups))
                for (int i = 0; i < want.Rows.Count; i++)
                    if (have[i] != want.Rows[i]) have[i] = want.Rows[i];
            return;
        }
        Groups.Clear();
        foreach (var (store, rows) in groups) Groups.Add(new StoreGroup(store, rows));
    }

    // Capped: AMD's limit is of its DirectX 12 cache alone, which the usage counts with the DirectX 11 one
    void ShowCache((CacheUsage Usage, CacheLimit? Limit, long? Capped) c)
    {
        var (usage, limit, capped) = c;
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

    public void Play(GameRow row)
    {
        Error = Fmt.Play(row.State.Game);
        Changed();
    }

    public void AddAllReady()
    {
        foreach (var g in Games.Where(r => r.IsAdd && !r.Queued).ToList()) App.Core.Enqueue(g.Id);
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
        var now = App.Core.Games.FirstOrDefault(g => g.Game.Id == id);
        if (now != null && s.Playing && !now.Playing) ReadCaches();   // the game exited: its Windows and pipeline caches grew
        if (now != null && (now != s || Row.Icon != Icons.Get(now.Game.ExePath))) { s = now; Row = new GameRow(now); }   // or its icon failed: the tile
        Changed();
    }

    static string Sentence(string t) => t.Length > 0 ? char.ToUpperInvariant(t[0]) + t[1..] : t;
    bool Partial => ScsKiller.IsPartial(s.Plan);
    /// <summary>Needs a recording for its ray tracing only: the rest compiles (a partial compile, offered but not the primary action).</summary>
    bool RtPartial => s.Status == GameStatus.NeedsRecording && ScsKiller.NeedsRtRecording(s);
    /// <summary>Compiled, and a recording would add its ray tracing: the record tip, not the warning callout.</summary>
    bool RtAfter => ScsKiller.RtAfterRecording(s);
    public bool NoAntiCheat => s.AntiCheat == AntiCheat.None;
    /// <summary>The recorder may go next to the game: no anti-cheat, and not a game added by hand whose folder isn't confirmed.</summary>
    bool CanRecord => NoAntiCheat && !s.RootUnconfirmed;

    public string Name => s.Game.Name;
    public string Sub => $"{Path.GetFileName(s.Game.ExePath)} · {Fmt.StoreName(s.Game)}";   // engine and API are tags

    // Status: the state in its colour and why; the actions sit beside it.
    public string StatusGlyph => s.Status switch
    {
        GameStatus.Warmed => "", GameStatus.Ready => "", GameStatus.NeedsRecording => "", GameStatus.Stale => "", _ => "",
    };
    public string StatusTitle => Row.StatusText;
    public string StatusReason => Sentence(s.Status switch
    {
        GameStatus.Warmed when ScsKiller.IsPartlyWarmed(s) => s.StatusReason,
        GameStatus.Warmed => $"compiled for driver {s.WarmedDriverVersion}" + (s.WarmedAt is { } t ? $" on {t.LocalDateTime:d}" : "")
            + (RtAfter ? ". " + Sentence(ScsKiller.RtAfterRecordingNote) : ""),
        // a cache here: played since a clear or a stopped compile; then the planner's note when the row has one to say
        GameStatus.Ready => (s.CacheOnDisk > 0 ? $"not fully compiled yet: {Format.Bytes(s.CacheOnDisk)} of it is in the driver cache"
            : s.LastWarmTime == null ? "not compiled yet" : "its shader cache is empty: compile it again")
            + (Row.Note != null ? ". " + Sentence(s.StatusReason) : ""),
        GameStatus.NeedsRecording => HasDbTeaser ? Row.FullNote.Replace("; " + ScsKiller.InDbNote, "") : Row.FullNote,   // the teaser says it
        _ => HasDbTeaser ? s.StatusReason.Replace("; " + ScsKiller.InDbNote, "") : s.StatusReason,
    } + (s.Status == GameStatus.NeedsRecording ? "" : GameRow.ModNote(s)));   // FullNote has it

    // The manifest is public: a PC without "db" sees that the community database covers the game, and where to get it.
    // Shown, it is the page's only mention of the database.
    public bool HasDbTeaser => s is { InCommunityDb: true, Community: null } && !App.Account.HasDb
        && (s.Status == GameStatus.NeedsRecording || RtAfter || s.Status == GameStatus.Unsupported && !NoAntiCheat && s.StatusReason.StartsWith("needs a recording, which", StringComparison.Ordinal));
    public string DbTeaser => $"In the community database: {s.CommunityDbPsos:N0} pipeline{(s.CommunityDbPsos == 1 ? "" : "s")} recorded by other players. Patreon supporters compile them without recording.";
    public string DbTeaserLink => App.Account.SignedIn ? "Patreon membership" : "Sign in with Patreon";
    public bool CanCompile => s.Status is GameStatus.Ready or GameStatus.Warmed or GameStatus.Stale || RtPartial;
    // warmed: nothing urgent, unless only partly (the careful compile is offered); RT: Record comes first
    public Style CompileStyle => Fmt.Style(OffersCareful || s.Status != GameStatus.Warmed && !RtPartial ? "AccentButtonStyle" : "DefaultButtonStyle");
    bool Queued => App.Core.Queue.Any(q => q.GameId == id && Fmt.Active(q) && !q.PlanCheck);
    public bool CanAdd => !Queued;
    bool CompilingSoon => Fmt.CompilingSoon(App.Core.Queue, id);
    public bool ShowPlay => Row.CanLaunch;
    public bool CanPlay => !s.Playing && !CompilingSoon;
    public string PlayText => s.Playing ? "Running" : "Play";
    public string PlayTip => s.Playing ? $"{s.Game.Name} is running" : CompilingSoon ? Fmt.CompilingTip : Fmt.PlayVia(s.Game);
    public bool IsManual => s.Game.Store == Store.Manual;
    public void Play()
    {
        Error = Fmt.Play(s.Game);
        Changed();
    }
    public string AddText => Queued ? "In queue" : OffersCareful ? "Compile carefully" : RtPartial ? "Compile without ray tracing (partial)" : "Compile";
    /// <summary>Partly warmed by a fast compile: the compile button turns the careful compile on first.</summary>
    public bool OffersCareful => ScsKiller.IsPartlyWarmed(s) && s.Careful is { On: false };

    // AMD's careful compile: a switch in the compile card, off while the game is queued (a running compile keeps its schedule)
    public bool HasCareful => s.Careful != null && CanCompile;
    public bool? CarefulPending { get; set; }
    public bool CarefulOn => CarefulPending ?? s.Careful?.On == true;
    public bool CanToggleCareful => CarefulPending == null && !Queued;
    public string CarefulNote => (s.Careful is { Recorded: 0 } ? "Compiles the pipelines recorded in the game in passes on few threads, so the driver keeps more of them. This game has no recording yet, so there is nothing to compile carefully."
        : $"Compiles the pipelines recorded in the game in passes on {ScsKiller.AmdCarefulThreads} threads, so the driver keeps more of them; the rest at full speed."
          + (s.Careful?.Estimate is { } est ? $" About {Format.Duration(est)}" + (s.Careful.On || s.EstimatedWarmTime is not { } fast ? "." : $" instead of {Format.Duration(fast)}.") : ""))
        + (s.Careful?.LaunchCompiled is { } lc ? $" After the last compile, the game's first launch still compiled {lc * 100:0}% of its pipelines." : "");
    // Windows' and the game's own shader caches, read off the UI thread; the driver's share is the live CacheOnDisk.
    BackgroundRead<long>? otherCaches;
    long otherBytes;
    public void ReadCaches() => (otherCaches ??= new(OtherCacheBytes, b => { otherBytes = b; Changed(); })).Request();
    long OtherCacheBytes()
    {
        try { return App.Core.GameCaches(id).Where(p => p.Name != ScsKiller.DriverPart).Sum(p => p.Bytes); }
        catch (Exception) { return 0; }   // e.g. the game is gone after a rescan: the label shows the driver's share
    }
    public bool HasCache => s.CacheOnDisk != null || otherBytes > 0 || s.Status is GameStatus.Warmed or GameStatus.Stale;
    public bool ShowClearCache => HasCache || CanCompile;
    public string ClearCacheText => s.CacheOnDisk != null || otherBytes > 0 ? $"Clear cache ({Format.Bytes((s.CacheOnDisk ?? 0) + otherBytes)})" : "Clear cache";
    public string ClearCacheTip => HasCache ? "Deletes this game's shader caches (the driver's and Windows', and the game's own if you choose) so its next run starts cold"
        : "SCSKiller learns this game's driver cache files the first time it compiles it or sees it running";

    public bool IsKnownStutter => s.KnownStutter != null;
    public string StutterReason => s.KnownStutter?.Reason ?? "";
    /// <summary>The source as a link when it's a URL; otherwise <see cref="StutterCitation"/> shows it as text.</summary>
    public Uri? StutterUri => Uri.TryCreate(s.KnownStutter?.Source, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u : null;
    public string StutterLinkText => StutterUri != null ? $"Source ({s.KnownStutter!.Date})" : "";
    public string StutterCitation => s.KnownStutter is { } k && StutterUri == null ? $"Source: {k.Source} ({k.Date})" : "";
    public string EngineChip => s.Engine is { } e ? (e.Family == "Unreal" ? "Unreal Engine " : e.Family + " ") + e.Version + (e.Fork != null ? " · custom fork" : "") : "Unknown engine";
    public string ApiChip => s.Engine?.GraphicsApi.Replace("D3D", "DirectX ") ?? "";
    public bool HasApi => ApiChip.Length > 0;
    public string AntiCheatChip => NoAntiCheat ? "No anti-cheat" : Fmt.AntiCheatName(s.AntiCheat);
    public string AntiCheatTip => NoAntiCheat ? "No anti-cheat found: this game can be recorded"
        : $"{Fmt.AntiCheatName(s.AntiCheat)} treats an extra d3d12.dll as tampering: this game is never recorded";
    public bool HasMiddleware => s.Middleware is { Count: > 0 };
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
    public bool HasRecordTip => CanRecord && (s.Status == GameStatus.NeedsRecording || RtAfter) && !RecordOn;
    bool Enough => ScsKiller.RecordedEnough(s);
    public string RecordTipTitle => Enough ? "Record more play" : "Record 5 minutes of play";
    public string RecordTip => RtPartial || RtAfter ? $"Turn on recording and play with ray tracing on{(Enough ? "" : " for about 5 minutes")}. " + ScsKiller.RtWhy(App.Core.Vendor.Caps, s.Engine)
            + (RtAfter ? " Everything else is compiled." : " Everything else compiles from the game files already.")
        : Enough ? Sentence(s.StatusReason) + "."
        : "Turn on recording and play as usual for about 5 minutes. SCSKiller learns this game's shader layout from it, then it can compile.";
    public bool ShowRecordAction => !RecordOn && CanToggleRecord;

    // Shader coverage (PlanStats), in words: how much of what the planner found gets compiled, where it comes from, what's
    // left and what helps; every count in Details. No plan yet on a game that can compile: the card says when it's measured.
    PlanStats? P => s.Plan;
    int? Pct => ScsKiller.CoveragePercent(P);
    public bool HasCoverageCard => HasPlan || CanCompile;
    public bool HasPlan => P != null;
    public bool HasMeter => Pct != null;
    public bool LowCoverage => Pct is { } pct ? pct < 90 : Partial;   // more than 10% left out, like ScsKiller.IsPartial (the test for a plan without the count)
    public bool GoodCoverage => HasMeter && !LowCoverage;
    bool Dx11Only => P is { StageSets: 0, Generated: 0, D3D11Shaders: > 0 };
    public string CoverageHeadline => P == null ? "Known once its first compile starts"
        : Dx11Only ? (Compiled ? "Covers" : "Will cover") + " every DirectX 11 shader in the game files"
        : Pct is not { } pct ? "Measured again on the next compile"
        : (s.Status == GameStatus.Stale ? s.NewPipelines > 0 ? "Its new plan covers" : "Its last plan covered" : Compiled ? "Covers" : "Will cover")
          + (pct == 100 ? " all the shader combinations found in this game" : $" about {pct}% of the shader combinations found in this game");
    bool Rt => ScsKiller.NeedsRtRecording(s);
    bool RtUnseen => s.RtUnseen;
    bool RtInline => ScsKiller.RtInlineCovers(P);
    bool Compiled => s.WarmedAt != null && s.Status != GameStatus.Stale;

    /// <summary>Where the plan's pipelines come from, in a player's words; the recording's row carries the community note,
    /// and without one the community database's status is its own row. Ray tracing: covered or what it needs.</summary>
    public IReadOnlyList<DetailRow> Sources => P is not { } p ? [] : new DetailRow?[]
    {
        p.Generated > 0 ? new("Found in the game files", Fmt.N(p.Generated)) : null,
        p.Recorded > 0 ? new(s.Community switch
        {
            null => "Your play recordings",
            { WithLocalRecording: true } => "Your recording and the community database",
            _ => "The community database",
        }, Fmt.N(p.Recorded), CommunityLine) : null,
        s.Community == null && s.InCommunityDb is { } inDb && !HasDbTeaser ? new("The community database", inDb ? "has a recording for this version" : "not in it yet") : null,
        p.MiddlewareItems > 0 ? new(p.MiddlewareSharedItems == 0 ? "Its upscalers, learned from recordings"
            : p.MiddlewareSharedItems == p.MiddlewareItems ? "Its upscalers, from shared packs" : "Its upscalers, from recordings and shared packs", Fmt.N(p.MiddlewareItems)) : null,
        p.D3D11Shaders > 0 ? new("DirectX 11 shaders", Fmt.N(p.D3D11Shaders)) : null,
        p.RtLibraries > 0 ? new("Ray-traced effects", s.Engine?.NoRayTracing == true ? "off in this game" : s.Engine?.NoRtPipelines == true ? "inline, from the game files" : p.RtUncovered == 0 ? "covered" : s.RtToPlan ? "being checked" : RtUnseen ? "not seen while recording" : RtInline ? "inline ones covered" : !Rt ? "mostly covered" : CanRecord ? "need a recording" : "not compiled") : null,
    }.OfType<DetailRow>().ToList();
    public bool HasSources => Sources.Count > 0;
    /// <summary>The community database's line under the recording row; null = nothing to say.</summary>
    string? CommunityLine => s.Community is { } c
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
        RtUnseen ? Sentence(ScsKiller.RtUnseenNote) + "." : RtInline ? Sentence(ScsKiller.RtInlineNote) + "." : !Rt || tipAsks ? null : CanRecord ? (HasDbTeaser ? ScsKiller.RtNeedsRecording : ScsKiller.RtNote(s.InCommunityDb)) + "."
            : $"Ray-traced effects aren't compiled: they need a recording, {(NoAntiCheat ? ScsKiller.ManualNoRecording : $"which {Fmt.AntiCheatName(s.AntiCheat)} blocks")}.",
        LeftCase switch
        {
            Left.AntiCheat => NoAntiCheat ? "Anything else compiles while you play: confirm the game's folder (Game folder… above) to record what's missing."
                : $"Anything else compiles while you play: {Fmt.AntiCheatName(s.AntiCheat)} blocks the recording that would find it.",
            Left.EngineSlots => "The rest use shader slots this game's engine adds." + (tipAsks ? "" : " A 5-minute recording lets SCSKiller rebuild them."),
            Left.UnknownSlots => "The rest use shader slots SCSKiller can't rebuild yet, so they still compile while you play.",
            Left.NotSeen => "SCSKiller hasn't seen how the game sets the rest up yet." + (tipAsks ? "" : " Playing longer with recording on teaches it."),
            Left.Recording => "Recording is on: anything new you play is added the next time it compiles.",
            Left.PlayedCompiles => $"Last time you played, {PlayCompiles(L!):N0} still had to compile." + (tipAsks ? "" : " Recording picks them up."),
            _ when Rt || RtUnseen || RtInline => null,   // ray tracing is what's left
            Left.PlayedClean => "Nothing was missing last time you played.",
            Left.NothingKnown => "Nothing SCSKiller knows of is missing.",
            _ => "A few effects are only put together while you play." + (tipAsks ? "" : " A short recording picks them up."),
        },
        !Guessed ? null : tipAsks ? "Some shader states are guesses." : "Some shader states are guesses; a longer recording makes them exact.",
    }.OfType<string>());
    // per-stage plans (AMD): the planner warns that a wrong guess costs a compile in game
    bool Guessed => P is { } g && g.GuessedUnits > 0.1 * (g.ExactUnits + g.InferredUnits + g.GuessedUnits);
    // not twice: a game that needs a recording has the button in its tip
    public bool ShowLeftRecord => ShowRecordAction && !HasRecordTip && (LeftCase is Left.EngineSlots or Left.NotSeen or Left.PlayedCompiles or Left.PlayOnly || Guessed || Rt || RtUnseen || RtInline);
    public bool ShowLeftCallout => HasPlan && ShowLeftRecord;   // before the first compile there's nothing left yet
    public string LeftTitle => Rt || RtUnseen ? "Ray-traced effects aren't compiled yet" : "Some shaders still compile while you play";

    public string CoverageValue => Pct is { } pct ? $"{pct}%" : Dx11Only ? "All" : Format.Dash;
    public string CoverageLabel => Pct != null || Dx11Only ? "covered" : "not yet";
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
    public bool HasTimeValue => s.LastWarmTime != null || s.EstimatedWarmTime != null;
    public string TimeLabel => s.LastWarmTime != null ? "Compile time" : "Compile time (estimate)";
    public string TimeValue => s.LastWarmTime is { TotalMinutes: >= 1, TotalHours: < 1 } t ? $"{t.Minutes} min {t.Seconds} s"   // measured: to the second
        : Format.Duration(s.LastWarmTime ?? s.EstimatedWarmTime);
    public bool HasDiskValue => s.CacheOnDisk != null || s.EstimatedCacheBytes != null;
    public string DiskLabel => s.CacheOnDisk != null ? "Disk space" : "Disk space (estimate)";
    public string DiskValue => Format.Bytes(s.CacheOnDisk ?? s.EstimatedCacheBytes);
    public bool HasShaderCount => s.ShaderCount != null;
    public string ShaderCount => Fmt.N(s.ShaderCount);
    // measured frames when the recorder timed them, else the compile count
    long? StutterCount => F != null ? ShaderHitches.Count : L is { } l ? PlayCompiles(l) : null;
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
        P is { LeftOut: > 0 } ? new("Left out", Fmt.N(P.LeftOut) + (P.Uncovered == P.LeftOut ? " (shader slots SCSKiller can't rebuild)" : P.Uncovered > 0 ? $" ({P.Uncovered:N0} for shader slots SCSKiller can't rebuild)" : "")) : null,
        P is { StageSets: 0, Uncovered: > 0 } ? new("Left out for shader slots SCSKiller can't rebuild", Fmt.N(P.Uncovered)) : null,
        P != null ? new("Recorded pipelines", Fmt.N(P.Recorded)) : null,
        P != null ? new("Built from the game files", Fmt.N(P.Generated)) : null,
        P is { MiddlewareItems: > 0 } ? new("Upscaler pipelines", Fmt.N(P.MiddlewareItems)) : null,
        P is { D3D11Shaders: > 0 } ? new("DirectX 11 shaders", Fmt.N(P.D3D11Shaders)) : null,
        P is { RtLibraries: > 0 } ? new("Ray tracing shader libraries", $"{P.RtLibraries:N0}" + (s.Engine?.NoRtPipelines == true ? " (not used by the game)" : P.RtUncovered > 0 ? $" ({P.RtUncovered:N0} not compiled)" : "")) : null,
        P is { RtInline: > 0 } ? new("Shaders that trace rays inline", Fmt.N(P.RtInline)) : null,
        s.InCommunityDb is { } db && !HasDbTeaser ? new("In the community database", db ? $"{s.CommunityDbPsos:N0} pipelines" : "not yet") : null,
        s.Community is { } c ? new("Community recording", $"{c.Psos:N0} pipelines") : null,
        P != null ? new("Synthesized pipeline templates", Fmt.N(P.SynthesizedTemplates)) : null,
        P != null ? new("Shader layouts", $"{P.RootSignatures:N0} · {RootSigSource(P)}") : null,
        P is { } u && u.ExactUnits + u.InferredUnits + u.GuessedUnits > 0 ? new("Stage units exact / inferred / guessed", $"{u.ExactUnits:N0} / {u.InferredUnits:N0} / {u.GuessedUnits:N0}") : null,
        P is { LayoutCoverage: > 0 } ? new("Vertex layouts known", $"{P.LayoutCoverage:P1}") : null,
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
    public string CompileText => s.WarmedAt is { } at
        ? $"Compiled {Fmt.When(at)}" + (s.LastWarmTime is { } t ? $" in {Format.Duration(t)}" : "") + $", for driver {s.WarmedDriverVersion}."
          + (s.LastWarmFailed != null && Failed == 0 && Skipped == 0 && Crashed == 0 ? " Nothing failed." : "")   // null = not known (a warm from before these were kept): no claim
          + (Failed > 0 ? $" The driver skipped {Failed:N0} combination{(Failed == 1 ? "" : "s")}." : "")
          + (s.CacheOnDisk is { } c ? $" It uses {Format.Bytes(c)} of disk space." : "")
        : s.LastWarmTime is { } last ? $"Its shader cache was cleared. The last compile took {Format.Duration(last)}."
        : (s.EstimatedWarmTime is { } est ? $"It takes about {Format.Duration(est)} and runs in the background, so you can keep using the PC." : "It runs in the background, so you can keep using the PC.")
          + (s.EstimatedCacheBytes is { } b ? $" It adds about {Format.Bytes(b)} to the driver's shader cache." : "");
    public string SkippedTitle => $"{Skipped + Crashed:N0} skipped";
    long NeedsRecording => Math.Min(Skipped, s.LastWarmNeedsRecording ?? 0);   // of Skipped: flagged by the community recording
    long NotInGame => Skipped - NeedsRecording;
    public string? SkippedText => s.WarmedAt == null || Skipped + Crashed == 0 ? null
        : string.Join(" ", new[]
        {
            NotInGame > 0 ? $"{NotInGame:N0} {(NotInGame == 1 ? "was" : "were")} skipped: {(NotInGame == 1 ? "its shader isn't" : "their shaders aren't")} in this version of the game. Nothing to do." : null,
            Crashed > 0 ? $"{Crashed:N0} {(Crashed == 1 ? "was" : "were")} skipped: {(Crashed == 1 ? "it crashes" : "they crash")} the GPU driver. If the game uses {(Crashed == 1 ? "it" : "them")}, it compiles {(Crashed == 1 ? "it" : "them")} itself." : null,
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
                + (started > 0 ? $" {started:N0} compiled while the game started." : "")
                + (ready > 0 ? $" The other {ready:N0} were ready." : "");
        }
    }
    public bool HasStateObjectLine => L is { } l && l.StateObjectsReady + l.StateObjectsCompiled + l.StateObjectsStartupCompiled > 0;
    public string StateObjectLine => L is { } l ? $"Ray tracing pipelines: {l.StateObjectsReady:N0} ready, {l.StateObjectsCompiled:N0} compiled while you played"
        + (l.StateObjectsStartupCompiled > 0 ? $", {l.StateObjectsStartupCompiled:N0} while the game started." : ".") : "";
    public bool HasRayQueryLine => L?.RayQueryRecompiles > 0;
    public string RayQueryLine => L is { RayQueryRecompiles: > 0 and var n }
        ? $"{n:N0} ray-traced pipeline{(n == 1 ? "" : "s")} the driver partly recompiles every launch." : "";

    // The last launch's frame times (FrameLog): its shader stutters, other hitches and loading, the 1% low of play.
    public FrameReport? F => s.LastFrames;
    List<Hitch> ShaderHitches => F?.Hitches.Where(h => h.Cause == HitchCause.Shader).ToList() ?? [];
    public bool HasFrames => F != null;
    public string FramesVerdict => F is not { } f ? "" : FramesText(f, ShaderHitches);
    public string GraphEnd => Format.Duration(F?.Duration);
    public string FramesSummary => F is not { } f ? ""
        : $"Startup (the game's own shader precompile and first load): {Format.Duration(f.Startup)}. In play: {InPlay(f).Count(h => h.Ms >= 50):N0} frames of 50 ms or more, "
          + $"{InPlay(f).Count(h => h.Ms >= 100):N0} of 100 ms or more. Frame times stay on this PC.";
    // the same list while the report is the same: a new one makes the slow-frames list rebuild every row at each refresh
    public IReadOnlyList<DetailRow> HitchRows => hitchRows.Report == F ? hitchRows.Rows
        : (hitchRows = (F, F?.Hitches.Select(h => new DetailRow($"{Clock(h.At)}  {Cause(h.Cause)}", Ms(h.Ms))).ToList() ?? [])).Rows;
    (FrameReport? Report, IReadOnlyList<DetailRow> Rows) hitchRows = (null, []);
    public static string HitchTip(Hitch h) => $"{Clock(h.At)} · {Ms(h.Ms)} · {Cause(h.Cause)}";
    static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    public bool HasHitchRows => F?.Hitches.Count > 0;

    // the frames of play, between startup and quitting, whatever slowed them (a load in play too)
    static IEnumerable<Hitch> InPlay(FrameReport f) => f.Hitches.Where(h => FrameLog.InPlay(h.At.TotalMilliseconds, h.Ms, f.Startup.TotalMilliseconds) && h.Cause != HitchCause.Quitting);
    static string Ms(double ms) => ms >= 1000 ? $"{ms / 1000:0.0} s" : $"{ms:0} ms";
    static string Cause(HitchCause c) => c switch { HitchCause.Shader => "shader compile", HitchCause.Other => "other hitch", HitchCause.Quitting => "quitting",
        HitchCause.LoadingShaders => "loading, compiling shaders", _ => "loading" };

    /// <summary>"5 min: 3 stutters from shader compiles (worst 1.3 s), 2 other hitches, 1% low 35 fps. Loading: 4 slow
    /// frames compiling shaders (worst 9.4 s), 12 others." (compiling shaders: startup only)</summary>
    public static string FramesText(FrameReport f, IReadOnlyList<Hitch> shader)
    {
        int other = f.Hitches.Count(h => h.Cause == HitchCause.Other), loading = f.Hitches.Count(h => h.Cause == HitchCause.Loading);
        var compiling = f.Hitches.Where(h => h.Cause == HitchCause.LoadingShaders).ToList();
        return $"{Format.Duration(f.Duration)}: " + (shader.Count == 0 ? "no stutters from shader compiles"
                : $"{shader.Count:N0} stutter{(shader.Count == 1 ? "" : "s")} from shader compiles (worst {Ms(shader.Max(h => h.Ms))})")
            + (other == 0 ? ", no other hitches" : $", {other:N0} other hitch{(other == 1 ? "" : "es")}")
            + $", 1% low {f.Low1PctFps:0} fps."
            + (compiling.Count + loading == 0 ? "" : " Loading: " + string.Join(", ", new[]
            {
                compiling.Count == 0 ? null : $"{compiling.Count:N0} slow frame{(compiling.Count == 1 ? "" : "s")} compiling shaders (worst {Ms(compiling.Max(h => h.Ms))})",
                loading == 0 ? null : compiling.Count == 0 ? $"{loading:N0} slow frame{(loading == 1 ? "" : "s")}" : $"{loading:N0} other{(loading == 1 ? "" : "s")}",
            }.OfType<string>()) + ".");
    }

    public GridLength LibraryWidth => new(L?.FromGameLibrary ?? 0, GridUnitType.Star);
    public GridLength HitsWidth => new(L?.CacheHits ?? 0, GridUnitType.Star);
    public GridLength CompilesWidth => new(L?.Compiles ?? 0, GridUnitType.Star);
    public double CompilesMinWidth => L?.Compiles > 0 ? 4 : 0;
    public string FromLibrary => Fmt.N(L?.FromGameLibrary);
    public string CacheHits => Fmt.N(L?.CacheHits);
    public string Compiles => Fmt.N(L?.Compiles);
    public string SessionLabel => L is { } l
        ? $"{l.Requests:N0} requests: {l.FromGameLibrary:N0} from the game's own library, {l.CacheHits:N0} already in the driver cache, {l.Compiles:N0} compiled during play"
          + (l.StartupCompiles > 0 ? $", {l.StartupCompiles:N0} while the game started" : "")
          + (l.RayQueryRecompiles > 0 ? $", {l.RayQueryRecompiles:N0} ray-traced pipelines the driver partly recompiles every launch" : "")
          + (l.StateObjectsReady + l.StateObjectsCompiled + l.StateObjectsStartupCompiled > 0 ? $"; ray tracing pipelines {l.StateObjectsReady:N0} ready, {l.StateObjectsCompiled:N0} compiled during play" : "")
          + (l.StateObjectsStartupCompiled > 0 ? $", {l.StateObjectsStartupCompiled:N0} while the game started" : "")
        : "";

    public bool? RecordPending { get; set; }   // the state a recorder change that is running (off the UI thread) asked for
    public bool RecordOn => ShowOffline ? OfflineOn : RecordPending ?? s.RecorderEffective;
    public bool CanToggleRecord => ShowOffline ? OfflinePending == null && !s.OfflineRunning
        : RecordPending == null && AlongsidePending == null && NoAntiCheat && s.RecorderSkip == null;
    public bool ShowUseDefault => RecordPending == null && s.RecorderOverride != RecorderOverride.Default && s.RecorderSkip == null;
    public string UseDefaultText => $"Use default ({(App.Core.Settings.RecordAllGames ? "on" : "off")})";
    public string RecordNote => ShowOffline
        ? $"{Fmt.AntiCheatName(s.AntiCheat)} treats an extra d3d12.dll as tampering, so this game is compiled from its files only."
        : !NoAntiCheat
        ? $"Not available: {Fmt.AntiCheatName(s.AntiCheat)} treats an extra d3d12.dll as tampering, so this game is compiled from its files only."
        : s.RecorderSkip == ScsKiller.SkipManual ? "Not available until you confirm the game's folder (Game folder… above): SCSKiller checks all of it for anti-cheat before it records."
        : s.RecorderSkip == ScsKiller.SkipModNotChainable ? $"Not available: {ScsKiller.NotChainableReason(s.RecorderMod)}."
        : s.RecorderSkip is { } skip ? $"Not available: {skip}."
        : "Adds a small d3d12.dll next to the game to catch anything the plan missed and time each frame, so this page shows what stuttered. Remove any time."
          + (s.RecorderNote is { } note ? $" ({Sentence(note)})" : "")
          + (s.RecorderRefused is { } why ? $" The last launch wasn't recorded: {why}." : "");

    // A mod's d3d12.dll where the recorder goes (ReShade, a wrapper): off = the game isn't recorded; on = the recorder chains to it
    public bool HasMod => s.RecorderMod != null && s.RecorderSkip is not (ScsKiller.SkipAntiCheat or ScsKiller.SkipShaderMod or ScsKiller.SkipManual or ScsKiller.SkipUnsupported or ScsKiller.SkipNotDx12);
    public string AlongsideTitle => $"Record alongside {s.RecorderMod}";
    public string AlongsideNote => $"{s.RecorderMod}'s d3d12.dll is renamed while the recorder is in, and restored exactly as it was when it's removed.";
    public bool? AlongsidePending { get; set; }
    public bool AlongsideOn => AlongsidePending ?? s.RecordAlongsideMod;
    public bool CanToggleAlongside => AlongsidePending == null && RecordPending == null;

    // An eligible EasyAntiCheat game (Games.OfflineEac): the card's switch allows offline sessions, each confirmed before it starts
    public bool ShowOffline => s.OfflineEligible;
    public bool? OfflinePending { get; set; }
    public bool OfflineOn => OfflinePending ?? s.OfflineRecord;
    public bool ShowOfflineOn => ShowOffline && OfflineOn;
    public bool OfflineStarting { get; set; }
    public bool CanStartOffline => s.OfflineRecord && OfflinePending == null && !s.OfflineRunning && !s.Playing && !OfflineStarting;
    public string OfflineButtonText => s.OfflineRunning ? "Offline session running…" : "Record offline without EasyAntiCheat (at your own risk)";
    public string OfflineRiskText => OfflineRisk;
    public const string OfflineRisk = "Starts the game offline without EasyAntiCheat. SCSKiller removes its files when the game exits (after a crash: "
        + "at the next logon). If any are left when you play online, you could be banned. Steam must be running; don't uninstall SCSKiller mid-session.";

    public bool HasRecording => s.RecordingBytes > 0;
    public string RecordingSize => $"The recording uses {Format.Bytes(s.RecordingBytes)} (in the game folder and SCSKiller's copy)";
    public bool RecordingPaused => s.RecordingPaused;
    public string RecordingPausedText => ScsKiller.PausedNote(App.Core.Settings) + ". Raise the limit in Settings, or clear the recording.";
    public bool ClearingRecording { get; set; }
    public bool CanClearRecording => !ClearingRecording && !s.Playing;
    public string ClearRecordingTip => s.Playing ? "Close the game first" : "Deletes the recorded pipelines; the recorder stays in";

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
    public bool CanStart => !App.Core.QueueRunning && Waiting.Count > 0;
    public bool CanRaiseLimit => App.Core.Vendor is not AmdBackend;   // AMD's limit is fixed (AmdAppCache.DxcCacheCap)
    public string Summary { get; private set; } = "";
    public string? PlanChecks { get; private set; }   // ScsKiller.PlanCheckLine
    public bool HasPlanChecks => PlanChecks != null;

    public string CurrentDetail
    {
        get
        {
            string stage = cur?.Stage switch
            {
                QueueStage.Indexing => "Reading shaders", QueueStage.Planning => "Building plan", QueueStage.Materializing => "Preparing files",
                QueueStage.Paused => cur.Note is { } n ? "Paused: " + n : "Paused",
                QueueStage.Warming when cur.Note is { } n => "Compiling: " + n,   // stalled (ScsKiller.StalledNote)
                _ => "Compiling",
            };
            return cur?.Progress is { Total: > 0 } p
                ? $"{stage} · {Percent:0.0}% · {p.Done:N0} of {p.Total:N0} pipelines" + (p.PerSecond > 0 && !ScsKiller.Stalled(cur) ? $" · {p.PerSecond:N0}/s" : "") + (p.Failed > 0 ? $" · {p.Failed:N0} failed" : "") + (p.Skipped > 0 ? $" · {p.Skipped:N0} skipped" : "")   // short (the Done note explains them); a count wraps whole
                : stage;
        }
    }
    public bool Indeterminate => cur != null && cur.Progress is not { Total: > 0 };
    public double Percent => cur?.Progress is { Total: > 0 } p ? 100.0 * p.Done / p.Total : 0;
    public string PauseText => cur?.Stage == QueueStage.Paused ? "Resume" : "Pause";
    public bool CacheWarn { get; private set; }
    public string CacheWarnText { get; private set; } = "";

    public double Threads { get => S.Threads; set { if ((int)value != S.Threads) S = S with { Threads = (int)value }; } }
    public string ThreadsText => $"{S.Threads} of {Environment.ProcessorCount}";
    public double MaxThreads => Environment.ProcessorCount;
    public int Priority { get => (int)S.Priority; set { if (value >= 0 && value != (int)S.Priority) S = S with { Priority = (WarmPriority)value }; } }
    public string CpuSummary => $"CPU use: {S.Threads} of {Environment.ProcessorCount} threads, {(S.Priority == WarmPriority.BelowNormal ? "below-normal" : "idle")} priority";

    public void Refresh()
    {
        var core = App.Core;
        var all = core.Queue;
        var queue = all.Where(q => !q.PlanCheck).ToList();
        PlanChecks = ScsKiller.PlanCheckLine(all);
        var games = core.Games.ToDictionary(g => g.Game.Id);

        TimeSpan? Left(QueueItem q) => ScsKiller.Stalled(q) ? null   // no estimate for a warm that stopped moving
            : q.Progress is { PerSecond: > 0 } p ? TimeSpan.FromSeconds((p.Total - p.Done) / p.PerSecond)
            : games.GetValueOrDefault(q.GameId) is { } g ? g.EstimatedWarmTime ?? g.LastWarmTime : null;
        QueueRow Row(QueueItem q, int number, bool first = false, bool last = false)
        {
            var g = games.GetValueOrDefault(q.GameId);
            var time = q.Stage == QueueStage.Done ? Format.Duration(g?.LastWarmTime) : Left(q) is { } t ? Format.Duration(t) + (q.Progress?.PerSecond > 0 ? " left" : "") : Format.Dash;
            return new(q.GameId, number, g?.Game.Name ?? q.GameId, g?.Game.ExePath ?? "", NoteOf(q, g), time, g is null ? Format.Dash : Fmt.Cache(g), q.Stage, first, last);
        }

        cur = queue.FirstOrDefault(Format.Running);
        if (cur != null) Current = Row(cur, 1);
        var waiting = queue.Where(q => q.Stage == QueueStage.Waiting).ToList();
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
        growth = pending.Select(q => games.GetValueOrDefault(q.GameId)).OfType<GameState>().Select(g => (g.Game.Name, ScsKiller.CacheGrowth(g))).ToList();
        ShowCache();
        Changed();
        cache.Request();
    }

    void ShowCache()
    {
        if (driver is not { } d) return;   // not read yet
        if (d.Capped is { } used)
        {
            CacheWarnText = AmdAppCache.QueueWarning(used, growth) ?? "";
            CacheWarn = CacheWarnText.Length > 0;
            return;
        }
        var (usage, limit) = (d.Usage.BytesOnDisk, d.Limit?.Bytes);
        CacheWarn = limit is { } max && planned > 0 && usage + planned > 0.9 * max;
        CacheWarnText = limit is { } l2 ? $"After this queue the cache is close to its {Format.Bytes(l2)} limit; older games may be evicted." : "";
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

    public SettingsVm()
    {
        cache = new(() =>
        {
            var usage = V.GetCacheUsage();
            string free;
            try { free = Format.Bytes(new DriveInfo(DriveOf(usage)).AvailableFreeSpace); } catch (Exception) { free = Format.Dash; }
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
        _ => "Setting on but the NVIDIA task is missing: click Apply to repair",
    };
    public string? AutoMessage { get; set; }
    public Microsoft.UI.Xaml.Controls.InfoBarSeverity AutoSeverity { get; set; }
    public bool HasAutoMessage => AutoMessage != null;
    public string CacheIntro => (IsNvidia ? "Global driver setting, the same one as NVIDIA App › Graphics › Shader Cache Size. " : "Global driver setting. ")
        + "When the cache is full the driver deletes older entries, and those games stutter again.";
    public bool Configurable => V.Caps.CacheSizeConfigurable;
    public bool NotConfigurable => !Configurable;
    public string NotConfigurableText => V.Vendor == GpuVendor.Amd
        ? $"The AMD driver's DirectX 12 shader cache is fixed at {Format.Bytes(AmdAppCache.DxcCacheCap)}; it has no size setting. "
          + "Past it, the driver removes the least recently used games' caches when a game starts, and SCSKiller shows those games as needing a compile again."
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
    static string DriveOf(CacheUsage? u) => Path.GetPathRoot(u?.Path) is { Length: > 0 } r ? r : Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!;
    public string FreeLabel => $"Free on {DriveOf(read?.Usage).TrimEnd('\\')}";
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
    public double BackgroundThreads { get => S.BackgroundThreads; set { if ((int)value != S.BackgroundThreads) S = S with { BackgroundThreads = (int)value }; } }
    public string BackgroundThreadsText => $"{S.BackgroundThreads} of {Environment.ProcessorCount}";
    public double MaxThreads => Environment.ProcessorCount;
    public bool HasLastRebuilt => LastRebuilt != "";
    public string LastRebuilt => App.Core.Games.Where(g => g.WarmedDriverVersion != null).MaxBy(g => g.WarmedAt)?.WarmedDriverVersion is { } v ? $"Last rebuilt for driver {v}" : "";

    public bool? MaximumPlans { get => S.MaximumPlans; set { if (value is { } v && v != S.MaximumPlans) S = S with { MaximumPlans = v }; } }
    public bool? ShareRecordings { get => S.ShareRecordings; set { if (value is { } v && v != S.ShareRecordings) S = S with { ShareRecordings = v }; } }
    public bool? ActiveCheck { get => S.ActiveCheck; set { if (value is { } v && v != S.ActiveCheck) S = S with { ActiveCheck = v }; } }
    public void GamesChanged() => Changed();   // no driver-cache re-read (Refresh)
    public bool RecordAllGames { get => S.RecordAllGames; set { if (value != S.RecordAllGames) S = S with { RecordAllGames = value }; } }   // the core reconciles off this thread
    /// <summary>"Recording in 23 games · 5 skipped: anti-cheat"; not-DX12 and unsupported games aren't counted.</summary>
    public string RecordingCount
    {
        get
        {
            var games = App.Core.Games;
            var skipped = games.Where(g => g.RecorderSkip is ScsKiller.SkipAntiCheat or ScsKiller.SkipShaderMod or ScsKiller.SkipForeignDll or ScsKiller.SkipModNotChainable
                    or ScsKiller.SkipVulkanMod or ScsKiller.SkipNeedsAdmin or ScsKiller.SkipPackageD3D12)
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
            App.ApplyStartWithWindows();   // the sign-in entry: written or removed now (real data only)
        }
    }
    public bool? NotifyNewShaders { get => S.NotifyNewShaders; set { if (value is { } v && v != S.NotifyNewShaders) S = S with { NotifyNewShaders = v }; } }
    public bool? ScanAtStart { get => S.ScanAtStart; set { if (value is { } v && v != S.ScanAtStart) S = S with { ScanAtStart = v }; } }
    public bool? CloseQuits { get => S.CloseQuits; set { if (value is { } v && v != S.CloseQuits) S = S with { CloseQuits = v }; } }


    public double Threads { get => S.Threads; set { if ((int)value != S.Threads) S = S with { Threads = (int)value }; } }
    public string ThreadsText => $"{S.Threads} of {Environment.ProcessorCount}";
    /// <summary>0 = Auto; 1 means 2 (the smallest budget offered).</summary>
    public double MaxCompileMemory { get => S.MaxCompileMemoryGB; set { var v = (int)value == 1 ? 2 : (int)value; if (v != S.MaxCompileMemoryGB) S = S with { MaxCompileMemoryGB = v }; } }
    public string MaxCompileMemoryNote => $"Auto picks the best limit for this PC's memory ({ScsKiller.AutoCompileMemoryGB()} GB here, with {Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (double)(1L << 30))} GB of RAM). Set it lower to keep more memory free while compiling.";
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
    public string? DbNote => !A.SignedIn ? "The list of games it covers is checked for everyone; the shared hashes need a Patreon subscription."
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

    // Update channel (docs/patreon-and-updates.md §4.5 item 5): a picker only when the token grants more than stable.
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
        : Updater.Ready is { } v ? AutoInstall.ReadyNote(v, App.Core.Settings)
        : (App.Core.Settings.InstallUpdatesAutomatically ? "Updates download in the background and install the next time SCSKiller starts or quits, never during a compile or a game."
            : "Updates download in the background; Restart to update installs them.") + " Leaving an early channel keeps this build until Stable passes it.";
    public string? UpdateProblem => Updater.Problem;
    public bool HasUpdateProblem => Updater.Problem != null;
}
