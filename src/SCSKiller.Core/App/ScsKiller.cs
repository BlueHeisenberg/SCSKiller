using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SCSKiller.Core.Carved;
using SCSKiller.Core.FromSoft;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using SCSKiller.Core.Unity;
using SCSKiller.Core.Vendors;
using SCSKiller.Core.Warming;

namespace SCSKiller.Core.App;

/// <summary>The facade the GUI and CLI use: discovery, per-game state, the sequential compile queue, the recorder.</summary>
public sealed partial class ScsKiller : IScsKiller
{
    public const double DefaultBytesPerPso = 24 * 1024, DefaultPsoPerSecond = 450;

    /// <summary>What compiling the game adds to the driver cache: its estimate less what its keys already hold.</summary>
    public static long CacheGrowth(GameState s) => Math.Max(0, (s.EstimatedCacheBytes ?? 0) - (s.CacheOnDisk ?? 0));

    /// <summary>The pipelines a compile of the plan creates, as the Library counts them: less the ones that crash this driver.</summary>
    public static long? PlanPipelines(GameState s) => s.Plan is { } p ? Math.Max(0, p.Recorded + p.Generated + p.MiddlewareItems - (s.LastWarmCrashed ?? 0)) : null;

    /// <summary>One game's compile past this gets a warning of its own: as much as the AMD driver's whole DirectX 12 cache.</summary>
    public const long LargeCompile = AmdAppCache.DxcCacheCap;

    /// <summary>A warning about the queued game whose compile adds the most (<see cref="CacheGrowth"/>), when that is over
    /// <see cref="LargeCompile"/>; else null. It names that game's own cache estimate, as the Library shows it, in whole GB.
    /// On AMD the driver's cache can't hold it all.</summary>
    public static string? LargeCompileWarning(IEnumerable<(long Growth, long Estimate)> queue, GpuVendor vendor) =>
        LargeCompileGb(queue) is { } gb ? $"This compile adds about {gb:0} GB. {LargeCacheCost}"
            + (vendor == GpuVendor.Amd ? $" It's more than the AMD driver's {App.Format.Bytes(AmdAppCache.DxcCacheCap)} cache can hold." : "") : null;

    const string LargeCacheCost = "Very large caches can make the game start slower and use more memory.";

    static double? LargeCompileGb(IEnumerable<(long Growth, long Estimate)> queue) =>
        queue.DefaultIfEmpty().MaxBy(q => q.Growth) is (> LargeCompile, var estimate) ? Math.Round(estimate / (double)(1L << 30), MidpointRounding.AwayFromZero) : null;

    /// <summary>The queue's cache warning where the driver has a size limit of its own (NVIDIA; null = none): a large compile
    /// (<see cref="LargeCompileWarning"/>), saying the limit when the queue goes past it, else the cache close to the limit
    /// after the queue (<paramref name="planned"/>: the queued games' estimates). Null when neither applies.</summary>
    public static string? QueueCacheWarning(IReadOnlyCollection<(long Growth, long Estimate)> queue, long usage, long planned, long? limit, GpuVendor vendor)
    {
        if (limit is not { } max) return LargeCompileWarning(queue, vendor);
        if (LargeCompileGb(queue) is { } gb && usage + planned > max)
            return $"This compile adds about {gb:0} GB and takes the cache past your {App.Format.Bytes(max)} limit, so older games may be evicted. {LargeCacheCost}";
        return LargeCompileWarning(queue, vendor)
            ?? (planned > 0 && usage + planned > 0.9 * max ? $"After this queue the cache is close to its {App.Format.Bytes(max)} limit, older games may be evicted." : null);
    }
    static readonly byte[] ProxyMarker = "SCSKiller_StartWarm"u8.ToArray();   // an export only our proxy d3d12.dll has
    const string RecorderIni = "[scskiller]\r\n; written by SCSKiller: record the pipelines this game creates. Removed by 'uninstall recorder'.\r\nmode=record\r\n";
    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500), AttributionInterval = TimeSpan.FromSeconds(3);
    // A new SCSKiller version may detect engines or plan differently: cached scan results from another one are redone.
    // "1.0.0+<commit>": the same for the app and the CLI built from one commit (their Core.dll bytes differ, so no MVID). A
    // release's informational version has no commit (publish.ps1), and two builds of one tag may differ: the commit is added.
    internal static readonly string CoreBuild = BuildOf(typeof(ScsKiller).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "",
        typeof(ScsKiller).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(m => m.Key == "SourceRevisionId")?.Value);

    internal static string BuildOf(string version, string? commit) =>
        string.IsNullOrEmpty(commit) || version.EndsWith("+" + commit, StringComparison.Ordinal) ? version : $"{version}+{commit}";

    readonly IReadOnlyList<IGameSource> _sources;
    readonly IEngineReader _reader;
    readonly IPlanner _planner;
    readonly IWarmer _warmer;
    readonly string? _proxyDll;
    readonly object _lock = new(), _scanLock = new();
    readonly List<QueueItem> _queue = [];
    readonly HashSet<string> _whenIdle = [];         // queued with EnqueueWhenIdle
    readonly HashSet<string> _planOnly = [];         // queued by CheckPlans: rebuild the plan, no warm
    readonly HashSet<string> _prompt = [];           // plan checks of an older planner's plan: they don't wait for idle (WaitsForIdle)
    readonly ManualResetEventSlim _go = new(true);   // reset = queue paused
    List<GameState> _games = [];
    Dictionary<string, Evaluation>? _scan;
    readonly Dictionary<string, long> _scanStarted = [];   // per game, when the evaluation in _scan started or anti-cheat was found (_evaluations)
    long _evaluations;
    Settings? _settings;
    Task? _worker;
    bool _running;                                   // StartQueue was called and normal (not "when idle") items are left
    string? _current;
    QueueStage _stage;
    IWarmRun? _run;
    CancellationTokenSource? _itemCts;
    volatile string? _pauseWhy;                      // why Watch suspended the running warm (game running, user at the PC)
    // How running games' processes were launched, by exe file name (case-insensitive), when it differs from the install's
    // file name in case: noted by Running(), taken into the game's record by MergeLaunched.
    readonly ConcurrentDictionary<string, LaunchedExe> _launched = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, bool> _driverStale = [];   // game id -> warmed for another driver, by the evaluation of its state in _games; under _lock

    public ScsKiller(IEnumerable<IGameSource> sources, IGpuVendorBackend vendor, IEngineReader reader, IPlanner planner, IWarmer warmer,
        string dataDir, string? proxyDll)
    {
        _sources = sources.ToList();
        Vendor = vendor;
        _reader = reader;
        _planner = planner;
        _warmer = warmer;
        Store = new AppStore(dataDir);
        Middleware.LoadImages(ImagesFile);
        ReShade.LoadProbes(ProbesFile);
        if (StutterList.Cached(StutterFile) is { } cached) StutterList.Current = cached;
        if (ConfirmedEngines.Cached(ConfirmedFile) is { } confirmed) ConfirmedEngines.Current = confirmed;
        if (RulesTrust.Cached(GameVerdicts.Name, GameVerdicts.TryParse, VerdictsFile, null) is { } verdicts) GameVerdicts.Current = verdicts;
        _proxyDll = proxyDll;
        AppCache = vendor.AppCache;
        RunningGameExes = DiscoveredGamesRunning;
        OfflineRuns = s => OthersRun(s, Processes(true));
    }


    public static ScsKiller CreateDefault()
    {
        var vendor = GpuBackends.Detect();
        var k = new ScsKiller([new SteamSource(), new EpicSource(), new XboxSource(), new GogSource(), new UbisoftSource(), new BattleNetSource(), new PurpleSource(), new HoYoPlaySource(), new EaSource(), new GaijinSource(),
                new ManualSource(new AppStore(AppStore.DefaultDir))],
            vendor, DefaultReaders(), new Planner(Path.Combine(AppStore.DefaultDir, "packs"), SharedPackDir(AppStore.DefaultDir, vendor.Vendor)), new Warmer(vendor),
            AppStore.DefaultDir, NativeTools.Find("d3d12.dll")) { Adapters = GpuBackends.Adapters };
        k.RedetectGpu();   // DriverId from DXGI before anything compares it
        return k;
    }

    /// <summary>The GPU vendor in a shared pack's key (<see cref="HashOnly.PackKey"/>); null = a vendor without shared packs.</summary>
    public static string? PackGpu(GpuVendor v) => v switch { GpuVendor.Nvidia => "nvidia", GpuVendor.Amd => "amd", _ => null };

    /// <summary>Where the shared packs downloaded for this GPU vendor live: community\packs\&lt;gpu&gt;\&lt;vendor&gt;\.</summary>
    public static string? SharedPackDir(string dataDir, GpuVendor v) => PackGpu(v) is { } gpu ? Path.Combine(dataDir, "community", "packs", gpu) : null;

    /// <summary>Unreal first, then the engines whose archives the carver can't see into (FromSoftware, Unity, RE Engine,
    /// REDengine 3, Dagor, Tank) or whose pipelines and root signatures it doesn't know (Northlight), then the generic raw DXBC/DXIL carver.</summary>
    public static IEngineReader DefaultReaders() =>
        new EngineReaders(("Unreal", new UnrealReader(AppStore.DefaultDir)), (FromSoftReader.Family, new FromSoftReader(AppStore.DefaultDir)),
            (UnityReader.Family, new UnityReader()), (ReEngine.ReEngineReader.Family, new ReEngine.ReEngineReader(AppStore.DefaultDir)),
            (RedEngine.RedEngineReader.Family, new RedEngine.RedEngineReader()), (Dagor.DagorReader.Family, new Dagor.DagorReader()), (Northlight.NorthlightReader.Family, new Northlight.NorthlightReader()),
            (Tank.TankReader.Family, new Tank.TankReader()), (CarvedReader.Family, new CarvedReader()));

    public IGpuVendorBackend Vendor { get; }
    public AppStore Store { get; }

    /// <summary>DXGI's adapters now (<see cref="GpuBackends.Adapters"/>); null = the GPU isn't re-detected.</summary>
    public Func<IReadOnlyList<DxgiAdapter>>? Adapters { get; set; }
    DxgiAdapter? _known;          // Vendor's adapter as last listed
    bool _resolved;               // the vendor's version read for _known was complete
    DateTimeOffset? _goneSince;   // Vendor's adapter hasn't been listed since
    readonly object _gpuLock = new();
    /// <summary>A re-detection found another driver or LUID for <see cref="Vendor"/>'s adapter (its Gpu has it), or
    /// <see cref="GpuRestartNote"/> changed. Raised on the detecting thread.</summary>
    public event Action? GpuChanged;
    /// <summary>Vendor's adapter has been gone for <see cref="GpuGoneAfter"/> while another is there: the backend, its caches,
    /// the warm exe and the shared packs are chosen when the process starts, so SCSKiller asks for a restart instead.</summary>
    public string? GpuRestartNote { get; private set; }
    /// <summary>While a driver installs, DXGI lists the adapter without it, or not at all, for a while.</summary>
    public static readonly TimeSpan GpuGoneAfter = TimeSpan.FromMinutes(15);

    /// <summary>One DXGI enumeration; when <see cref="Vendor"/>'s adapter has another driver or LUID, or its last version read
    /// was incomplete, the vendor's driver version is read again into <see cref="Vendor"/> in place. Before every scan and
    /// compile, and every <see cref="GpuCheckInterval"/> while <see cref="WatchGames"/> runs. True when
    /// <see cref="GpuChanged"/> was raised.</summary>
    public bool RedetectGpu()
    {
        GpuInfo before;
        lock (_gpuLock)
            try
            {
                if (Adapters?.Invoke() is not { } all) return false;
                (before, var note, var id) = (Vendor.Gpu, GpuRestartNote, DriverId);
                if (Followed(all) is { } now)
                {
                    (_goneSince, GpuRestartNote) = (null, null);
                    if (now != _known || !_resolved) (_resolved, _known) = (Vendor is not IRefreshableGpu gpu || gpu.Refresh(now.Gpu), now);
                    if (now.Gpu.DriverVersion.Length > 0)   // none: the last one stays
                        _id = $"{(int)now.Gpu.Vendor:x4}:{now.DeviceId:x4}:{now.SubSysId:x8}:{now.Gpu.DriverVersion}";
                    if (_resolved && _id != null) _completeId = _id;
                }
                else if (Clock() - (_goneSince ??= Clock()) >= GpuGoneAfter && GpuBackends.Primary(all) is { } other)
                    GpuRestartNote = $"Restart SCSKiller to use {other.Name}";
                if (GpuRestartNote == note && Vendor.Gpu == before && DriverId == id) return false;
            }
            catch (Exception e)
            {
                Log?.Report($"re-detecting the GPU failed: {e.Message}");
                return false;
            }
        Log?.Report(GpuRestartNote ?? $"GPU: {before.Name}, driver {before.DriverVersion} -> {Vendor.Gpu.Name}, driver {Vendor.Gpu.DriverVersion}");
        GpuChanged?.Invoke();
        return true;
    }

    /// <summary>The driver a warm compiles for, as staleness, resume points and crash keys compare it: DXGI's vendor, device and
    /// subsystem ids and user-mode version, in one format on every vendor and new with every install (not the LUID: every
    /// boot assigns one, and the cache outlives it). <see cref="IGpuVendorBackend.Gpu"/>'s DriverVersion is the label shown.
    /// Null = not known yet (no DXGI version in this process so far): nothing is judged stale, adopted or reset.</summary>
    public string? DriverId => _id;
    string? _id, _completeId;   // DriverId; and the one at the last complete version read

    /// <summary>The GPU as one check left it, read under the lock: what a decision compares and what it records.</summary>
    /// <param name="Complete">the label's last read was complete</param>
    /// <param name="Moved">DriverId changed since the last complete read: an incomplete label may be the old driver's</param>
    /// <param name="Dxgi">DXGI's user-mode version</param>
    /// <param name="Fallback">the vendor's fallback form of it (<see cref="IRefreshableGpu.FallbackVersion"/>)</param>
    sealed record GpuSnapshot(GpuInfo Gpu, string? Id, bool Complete, bool Moved, string? Dxgi, string? Fallback);

    GpuSnapshot Snapshot()
    {
        lock (_gpuLock)
        {
            var dxgi = _known?.Gpu.DriverVersion is { Length: > 0 } v ? v : null;
            return new(Vendor.Gpu, _id, _known == null || _resolved, _id != _completeId, dxgi,
                dxgi != null && Vendor is IRefreshableGpu r ? r.FallbackVersion(dxgi) : null);
        }
    }

    /// <summary>A record's driver is the current one: by <see cref="DriverId"/>, or for a record from before it (null), by
    /// the version string earlier builds stored, in either form they had for this driver. The shown version counts when
    /// its read was complete, or, judging only, when the driver didn't move since one was; <paramref name="adopting"/>
    /// needs a complete one. An unknown DriverId judges nothing: current.</summary>
    static bool CurrentDriver(GpuSnapshot s, string? id, string? version, bool adopting = false) =>
        s.Id == null || (id != null ? id == s.Id
            : version != null && (version == s.Dxgi || version == s.Fallback
                                  || (version == s.Gpu.DriverVersion && (s.Complete || (!adopting && !s.Moved)))));

    /// <summary>An earlier build's record whose driver is the current one takes <see cref="DriverId"/>: a later change of
    /// the version string (the registry readable again) is no driver change. Checked and taken from one snapshot; not while
    /// DriverId is unknown or the version read is incomplete: the next evaluation tries again.</summary>
    static bool AdoptDriverId(GameRecord r, GpuSnapshot s)
    {
        if (s.Id == null) return false;
        bool warmed = r.WarmedAt != null && r.WarmedDriverId == null && CurrentDriver(s, null, r.WarmedDriverVersion, adopting: true);
        bool crashed = r.CrashKeysDriver != null && r.CrashKeysDriver != s.Id && CurrentDriver(s, null, r.CrashKeysDriver, adopting: true);
        if (warmed) r.WarmedDriverId = s.Id;
        if (crashed) r.CrashKeysDriver = s.Id;
        return warmed | crashed;
    }

    /// <summary>Skips an earlier build saved under the version string (StaleKey's first part), rewritten once to
    /// DriverId when that string is the current driver's, from one snapshot as <see cref="AdoptDriverId"/>.</summary>
    void AdoptDismissals(GpuSnapshot s)
    {
        if (s.Id == null) return;
        var dismissed = Store.LoadDismissed();
        var changed = false;
        foreach (var (game, key) in dismissed.ToList())
            if (key.Split('|', 2) is [var driver, var build] && driver != s.Id && CurrentDriver(s, null, driver, adopting: true))
                (dismissed[game], changed) = ($"{s.Id}|{build}", true);
        if (changed) Store.SaveDismissed(dismissed);
    }

    /// <summary>Vendor's adapter in <paramref name="all"/>: the same LUID, or after a driver reload (a new LUID) the same
    /// vendor, device and subsystem ids; the first time, the same vendor and name. Never just the primary one: on a PC with
    /// two GPUs of a vendor, the other one isn't where the caches were compiled.</summary>
    DxgiAdapter? Followed(IReadOnlyList<DxgiAdapter> all) =>
        all.FirstOrDefault(a => a.Gpu.AdapterLuid == Vendor.Gpu.AdapterLuid)
        ?? all.FirstOrDefault(a => _known is { } k ? (a.Gpu.Vendor, a.DeviceId, a.SubSysId) == (k.Gpu.Vendor, k.DeviceId, k.SubSysId)
                                                   : (a.Gpu.Vendor, a.Gpu.Name) == (Vendor.Vendor, Vendor.Gpu.Name));

    /// <summary>Background runs (scheduled re-warm, <c>compile --idle</c>): idle priority, Settings.BackgroundThreads, and
    /// pause while another discovered game is running if Settings.PauseWhileGaming (a warm always stops while its own
    /// game runs and continues afterwards, whatever the setting). Items queued with EnqueueWhenIdle are
    /// background runs regardless.</summary>
    public bool Background { get; set; }
    public int? ThreadsOverride { get; set; }
    public IProgress<string>? Log { get; set; }
    /// <summary>After a scan, queue a "when idle" plan rebuild (no warm) for each warmed game whose plan an older planner
    /// built (<see cref="Planner.Version"/>): it tells whether the new planner compiles anything the warm didn't. The app
    /// sets it; the CLI doesn't (its queue waits for what it was asked to do).</summary>
    public bool CheckPlans { get; set; }
    /// <summary>Only the app sets it: the CLI and the scheduled task never install recorders.</summary>
    public bool ManageRecorders { get; set; }
    /// <summary>No extension, case-insensitive; no process is opened. Replaceable for tests.</summary>
    public Func<IReadOnlySet<string>> ProcessNames { get; set; } = RunningProcessNames;
    /// <summary>The process list the watcher and the offline session cleanup read (<see cref="RunsFromItsFolder"/>,
    /// <see cref="DiscoveredGamesRunning"/>, <see cref="OfflineRuns"/>);
    /// fresh: a snapshot taken now, else one up to a second old. Replaceable for tests.</summary>
    internal Func<bool, List<(int Pid, int Parent, string Exe)>> Processes { get; set; } = fresh => fresh ? ProcessTree.Snapshot() : ProcessTree.RecentSnapshot();
    /// <summary>A process's image path, read without opening the process; null when it can't be told. Replaceable for tests.</summary>
    internal Func<int, string?> ProcessPath { get; set; } = ProcessTree.ImagePath;
    /// <summary>The driver cache's per-application files, for attributing and clearing a game's share; null = the vendor's
    /// cache isn't per application. Replaceable for tests.</summary>
    public IAppCache? AppCache { get; set; }
    /// <summary>Roots of D3DSCache and of the folders games write their own shader caches in. Replaceable for tests.</summary>
    public string LocalAppData { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string MyGames { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games");
    public string ProgramData { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    /// <summary>The community database's read side (the app sets it: it holds the sign-in); null = local only. What was
    /// downloaded is used either way while Settings.UseCommunityDb is on.</summary>
    public Community? Community { get; set; }
    /// <summary>The last scan's community pass (manifest check and downloads), which runs after the scan returns.</summary>
    public Task CommunitySync { get; private set; } = Task.CompletedTask;
    /// <summary>Anonymous uploads of this PC's recordings while Settings.ShareRecordings is on (the app sets it); null = none.</summary>
    public Sharing? Sharing { get; set; }
    /// <summary>The queued sharing passes (<see cref="StartSharing"/>), one at a time.</summary>
    public Task SharingPass { get; private set; } = Task.CompletedTask;
    /// <summary>The anonymous daily check after a scan (the app sets it); null = none.</summary>
    public ActiveCheck? ActiveCheck { get; set; }
    /// <summary>The last scan's <see cref="ActiveCheck"/>, which runs after the scan returns.</summary>
    public Task ActiveCheckSent { get; private set; } = Task.CompletedTask;
    /// <summary>Where the known-stutter and confirmed-engines lists are fetched from after a scan (the app sets it); null = the cached or embedded lists only.</summary>
    public RouteFailover? ContentRoutes { get; set; }
    /// <summary>The access token and its entitlements, for the update channel's own game verdicts (the app sets it); null or a
    /// failure = the public list only.</summary>
    public Func<Task<(string? Token, IReadOnlyCollection<string>? Ent)>>? ChannelAccess { get; set; }
    /// <summary>The last scan's check of both lists, which runs after the scan returns.</summary>
    public Task StutterUpdate { get; private set; } = Task.CompletedTask;

    /// <summary>How long the user has been away from keyboard and mouse (GetLastInputInfo). Replaceable for tests.</summary>
    public Func<TimeSpan> IdleTime { get; set; } = UserIdleTime;
    /// <summary>"When idle" items start, and resume, once the user has been away this long; input pauses them within a second.</summary>
    public TimeSpan IdleAfter { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>A warm whose done count hasn't moved for this long (not paused) shows "no progress for N min" instead of
    /// an estimate. Replaceable for tests.</summary>
    public TimeSpan StallAfter { get; set; } = TimeSpan.FromSeconds(60);

    public Settings Settings
    {
        get => _settings ??= Store.LoadSettings() is var s && s.RecordingLimitMB == 256 && !s.RecordingLimitChosen
            ? s with { RecordingLimitMB = AppStore.DefaultSettings.RecordingLimitMB }   // the earlier default, saved with any other setting
            : s.RecordingLimitMB > 0 && RecordingLimits.FirstOrDefault(l => l >= s.RecordingLimitMB) is var next && next != s.RecordingLimitMB
                ? s with { RecordingLimitMB = next, RecordingLimitChosen = true } : s;   // a size that is no choice (the user's): the next one up, or unlimited
        set
        {
            if (value.RecordingLimitMB != Settings.RecordingLimitMB) value = value with { RecordingLimitChosen = true };
            var maximumChanged = value.MaximumPlans != Settings.MaximumPlans;
            var communityOn = value.UseCommunityDb && !Settings.UseCommunityDb;
            var shareOn = value.ShareRecordings && !Settings.ShareRecordings;
            var welcomed = value.WelcomeSeen && !Settings.WelcomeSeen;   // the first scan ran while the welcome was open
            var limitChanged = value.RecordingLimitMB != Settings.RecordingLimitMB;
            var recordersChanged = value.RecordAllGames != Settings.RecordAllGames || limitChanged;
            maximumChanged |= value.UseCommunityDb != Settings.UseCommunityDb;   // the recording planned from, too
            Store.SaveSettings(value);
            _settings = value;
            // Stale reasons (and RecordingPaused) depend on it. Re-evaluated off the caller's thread (the GUI's: Evaluate does
            // disk IO per game), one pass at a time so a quick toggle back ends on the current setting; the new states come as GameChanged.
            if (maximumChanged || limitChanged)
                SettingsRefresh = Task.Run(() =>
                {
                    lock (_refreshLock)
                        try { foreach (var g in Games) Refresh(g.Game); }
                        catch (Exception e) { Log?.Report($"re-evaluating games for the new settings failed: {e.Message}"); }
                });
            if (communityOn) StartCommunitySync();
            if (shareOn) StartSharing();
            if (welcomed && ActiveCheck is { } active) ActiveCheckSent = Task.Run(() => active.SendAsync());
            if (recordersChanged && ManageRecorders)
                Task.Run(() =>
                {
                    try { ReconcileRecorders(); }
                    catch (Exception e) { Log?.Report($"reconciling recorders failed: {e.Message}"); }
                });
        }
    }
    readonly object _refreshLock = new();

    /// <summary>The last settings change's re-evaluation of every game (<see cref="Settings"/>).</summary>
    public Task SettingsRefresh { get; private set; } = Task.CompletedTask;

    public IReadOnlyList<GameState> Games { get { lock (_lock) return _games.ToList(); } }
    public event Action<GameState>? GameChanged;
    public IReadOnlyList<QueueItem> Queue { get { lock (_lock) return _queue.ToList(); } }
    public event Action<QueueItem>? QueueChanged;

    /// <summary>Completes when the queue has nothing left to run.</summary>
    public Task WhenQueueIdle() { lock (_lock) return _worker ?? Task.CompletedTask; }

    /// <summary>Discovery (cheap) + per-game state. Engine detection, the planner check and anti-cheat detection are
    /// reused from scan.json unless the game is new or its exe, store version or recording changed.
    /// <paramref name="userRequested"/>: the user's refresh, which also fetches the server's lists and manifest whatever
    /// their age and runs <see cref="UserFetch"/>, at most once per <see cref="UserFetchEvery"/> (<see cref="ServerRefresh"/>).</summary>
    public Task<IReadOnlyList<GameState>> ScanAsync(CancellationToken ct, bool userRequested = false) => Scan(false, userRequested, ct);

    /// <summary>ScanAsync that redoes engine detection, the planner check and anti-cheat detection for every game.</summary>
    public Task<IReadOnlyList<GameState>> RescanAsync(CancellationToken ct, bool userRequested = false) => Scan(true, userRequested, ct);

    // /v1/content/ and the manifest allow 30 requests an hour per IP, and a refresh takes two content files: 12 of 30 at most
    public static readonly TimeSpan UserFetchEvery = TimeSpan.FromMinutes(10);
    string UserFetchFile => Path.Combine(Store.DataDir, "user-fetch.txt");   // its write time: a restart keeps the window
    DateTime _userFetched;
    List<Game>? _freshGames;   // a user's refresh's community pass, queued behind the running one and not started yet
    /// <summary>The app's own fetches on a user's refresh (the access token, the update check), alongside the lists and the manifest.</summary>
    public Func<Task>? UserFetch { get; set; }
    public enum ServerCheck { Done, TooSoon, Unreachable }
    /// <summary>The last user refresh's server fetches, which run after <see cref="ScanAsync"/> returns: completes once all
    /// of them have. TooSoon: inside <see cref="UserFetchEvery"/>, nothing fetched; Unreachable: no known-stutter list came.</summary>
    public Task<ServerCheck> ServerRefresh { get; private set; } = Task.FromResult(ServerCheck.Done);

    bool TakeUserFetch()
    {
        lock (_scanLock)
        {
            var saved = File.GetLastWriteTimeUtc(UserFetchFile);   // 1601 when missing
            var age = DateTime.UtcNow - (saved > _userFetched ? saved : _userFetched);
            if (age >= TimeSpan.Zero && age < UserFetchEvery) return false;   // a clock set back doesn't hold it off
            _userFetched = DateTime.UtcNow;
            try { File.WriteAllText(UserFetchFile, ""); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // this process still keeps the window
            return true;
        }
    }

    async Task<ServerCheck> Fetched(Task<bool> lists, Task community)
    {
        var app = UserFetch is { } f ? Task.Run(f) : Task.CompletedTask;
        try { await Task.WhenAll(lists, community, app); }
        catch (Exception e) { Log?.Report($"refreshing the server's data failed: {e.Message}"); }
        return lists.IsCompletedSuccessfully && !lists.Result ? ServerCheck.Unreachable : ServerCheck.Done;
    }

    Task<IReadOnlyList<GameState>> Scan(bool force, bool userRequested, CancellationToken ct) =>
        Task.Run(() => userRequested ? Scanned(force, userRequested, ct) : LowIo(() => Scanned(force, userRequested, ct)), ct);

    IReadOnlyList<GameState> Scanned(bool force, bool userRequested, CancellationToken ct)
    {
        var started = Interlocked.Increment(ref _scans);   // before discovery: a later scan's list is at least as new
        CleanOfflineSessions();   // before any game is read or written
        RedetectGpu();   // a driver updated while the app runs: the states below compare against it
        AdoptDismissals(Snapshot());
        if (userRequested)
        {
            Middleware.ForgetScans();
            ReShade.ForgetProbes();
        }
        if (!force && !userRequested && !Settings.ScanAtStart && Store.LoadList() is { } kept && kept.Build == CoreBuild && kept.Driver == DriverId
            && Store.LoadDiscovered(CoreBuild) is { Count: > 0 } listed)
            return Listed(started, kept.Games, listed);
        var (found, unresolved) = Discover(Store.LoadDiscovered(CoreBuild), s => s.Discover(), fresh: userRequested);
        var states = new List<GameState>();
        var tickets = new List<long>();
        var driverStale = new List<bool>();
        bool detected = false;
        foreach (var g in found.DistinctBy(g => g.Id).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            tickets.Add(Ticket());
            var fresh = false;
            var stale = false;
            states.Add(unresolved.TryGetValue(g.Id, out var why) ? Unread(g, why, out stale) : Evaluate(g, force, out fresh, out stale));
            driverStale.Add(stale);
            detected |= fresh;
        }
        lock (_lock) Publish(started, Newest(states, tickets, driverStale));
        var running = Running();   // notes how running games were launched (the real check only opens processes named like a game)
        var learned = LearnKeysOfRunning(running);
        for (int i = 0; i < states.Count; i++)
            if (!unresolved.ContainsKey(states[i].Game.Id) && (_launched.ContainsKey(Path.GetFileName(states[i].Game.ExePath)) || learned.Contains(states[i].Game.Id)))
            {
                tickets[i] = Ticket();
                states[i] = Evaluate(states[i].Game, false, out _, out var stale);
                driverStale[i] = stale;
            }
        lock (_lock) Publish(started, Newest(states, tickets, driverStale));
        foreach (var s in states) GameChanged?.Invoke(s);
        if (CheckPlans)
            foreach (var s in states)
                if (s.Engine != null && s.Status != GameStatus.Unsupported && Store.LoadGame(s.Game.Id) is var r && NeedsPlanCheck(s.Game, r, s.Engine)) CheckPlan(s.Game.Id, OlderPlanner(s.Game, r));
        if (detected) ReleaseMemory();   // engine detection mounts the game's archives
        if (ManageRecorders)
        {
            ReconcileRecorders();
            states = Games.ToList();
        }
        lock (_scanLock)   // admission and ServerRefresh together: a denied click never replaces a pending refresh
        {
            var fetch = userRequested && TakeUserFetch();
            StartCommunitySync(states, fetch);
            var lists = StartStutterUpdate(fetch);
            // inside the window while the last refresh's fetches run: those, not a TooSoon that ends the wait early
            if (userRequested) ServerRefresh = fetch ? Fetched(lists, CommunitySync) : ServerRefresh.IsCompleted ? Task.FromResult(ServerCheck.TooSoon) : ServerRefresh;
        }
        StartSharing(states.Select(s => s.Game));
        StartMigration(states);
        if (ActiveCheck is { } active) ActiveCheckSent = Task.Run(() => active.SendAsync());
        RedetectInBackground();
        SaveDllReads();
        KeepList();
        RequestCollect();
        return states;
    }

    /// <summary>A scan with Settings.ScanAtStart off: the last list, with the games of the stores that tell a new install or
    /// build without a look through its folders (Steam, the Xbox app, the games added by hand) listed again, and a game read
    /// again only when it isn't the one listed (another build, exe or folder).</summary>
    IReadOnlyList<GameState> Listed(long started, List<GameState> kept, List<Game> before)
    {
        var (found, unresolved) = Discover(before, s => s.Store is Core.Store.Steam or Core.Store.Xbox or Core.Store.Manual ? s.Discover() : before.Where(g => g.Store == s.Store).ToList());
        var was = kept.DistinctBy(s => s.Game.Id).ToDictionary(s => s.Game.Id);
        var gpu = Snapshot();
        var (states, tickets, driverStale) = (new List<GameState>(), new List<long>(), new List<bool>());
        foreach (var g in found.DistinctBy(g => g.Id).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            tickets.Add(Ticket());
            if (unresolved.TryGetValue(g.Id, out var why))
            {
                states.Add(Unread(g, why, out var unread));
                driverStale.Add(unread);
            }
            else if (was.GetValueOrDefault(g.Id) is { } listed && listed.Game == g)
            {
                var rec = Store.LoadGame(g.Id);
                states.Add(listed with { Playing = IsPlaying(g.Id) });
                driverStale.Add(rec.WarmedAt != null && !CurrentDriver(gpu, rec.WarmedDriverId, rec.WarmedDriverVersion));
            }
            else
            {
                states.Add(Evaluate(g, false, out _, out var stale));
                driverStale.Add(stale);
            }
        }
        lock (_lock) Publish(started, Newest(states, tickets, driverStale));
        foreach (var s in states) GameChanged?.Invoke(s);
        StartStutterUpdate(false);
        if (ActiveCheck is { } active) ActiveCheckSent = Task.Run(() => active.SendAsync());
        KeepList();
        return states;
    }

    /// <summary>The list as it is now, for a start with Settings.ScanAtStart off.</summary>
    void KeepList()
    {
        lock (_keepLock)
            try { Store.SaveList(new KeptList(CoreBuild, DriverId, Games.ToList())); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"couldn't save the list of games: {e.Message}"); }
    }
    // the snapshot and its write together: an older list never replaces a newer one
    readonly object _keepLock = new();

    /// <summary>The games the sources list (<paramref name="list"/>), each with the exe it runs (<see cref="Following"/>;
    /// <paramref name="unresolved"/>: why it couldn't be checked, by game id).
    /// An install folder listed by an earlier source isn't listed again by a later one (an EA game bought on Steam has the
    /// EA installer's files too): source order decides whose id, and so whose saved state, the game keeps. A game the
    /// user added yields to a store's whose install holds its exe (the store's copy installed since).</summary>
    /// <param name="fresh">the user's refresh: every Unreal game's Shipping exe is looked for again (<see cref="ShippingPick"/>)</param>
    (List<Game> Found, Dictionary<string, Exception> Unresolved) Discover(List<Game> before, Func<IGameSource, IReadOnlyList<Game>> list, bool fresh = false)
    {
        var picks = fresh ? [] : Store.LoadShippingPicks(CoreBuild);
        var picked = new Dictionary<string, ShippingPick>();
        Game Shipping(Game g)
        {
            if (GameFiles.IsShipping(g.ExePath)) return g;
            var stamp = RootStamp(g);
            if (stamp == null || picks.GetValueOrDefault(g.Id) is not { } pick || pick.Named != g.ExePath || pick.Stamp != stamp
                || pick.Exe != g.ExePath && !File.Exists(pick.Exe))
                pick = new ShippingPick(g.ExePath, stamp, ShippingExe(g.InstallDir, g.ExePath));
            picked[g.Id] = pick;
            return pick.Exe != g.ExePath ? g with { ExePath = pick.Exe } : g;
        }
        var found = new List<Game>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var known = before.DistinctBy(g => g.Id).ToDictionary(g => g.Id);
        foreach (var source in _sources)
            if (source is SteamSource steam) steam.Known = known;
            else if (source is XboxSource xbox) xbox.Known = known;
        foreach (var source in _sources)
            try
            {
                var named = list(source);
                foreach (var g in named)   // the folders of both the exe the user picked and the one it runs
                    if (g.Store == Core.Store.Manual && found.FirstOrDefault(f => GameFiles.Inside(f.InstallDir, g.ExePath)) is { } owner)
                        foreach (var at in new[] { g, Shipping(g) }.Distinct()) AdoptManual(at, owner);
                var listed = named.Select(Shipping).ToList();
                var games = listed.Where(g => !claimed.Contains(GameFiles.DirKey(g.InstallDir))
                    && !(g.Store == Core.Store.Manual && found.Any(f => GameFiles.Inside(f.InstallDir, g.ExePath)))).ToList();
                found.AddRange(games);
                claimed.UnionWith(games.Select(g => GameFiles.DirKey(g.InstallDir)));
            }
            catch (Exception e) { Log?.Report($"{source.Store}: discovery failed: {e.Message}"); }
        if (found.Count != known.Count || found.Any(g => known.GetValueOrDefault(g.Id) != g)
            || picked.Count != picks.Count || picked.Any(p => picks.GetValueOrDefault(p.Key) != p.Value))
            try { Store.SaveDiscovered(CoreBuild, found, picked); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"couldn't save the games found: {e.Message}"); }
        var unresolved = new Dictionary<string, Exception>();
        found = found.Select(g =>
        {
            try { return Following(g); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                unresolved[g.Id] = e;
                // the saved resolution, unchecked: discovery's exe would evaluate, and its recorder move, elsewhere
                return Store.LoadGame(g.Id).RunsExe is { } exe ? g with { ExePath = exe } : g;
            }
        }).ToList();
        return (found, unresolved);
    }

    /// <summary>The game with its Unreal Shipping exe whatever exe its store or the user named (<see cref="GameFiles.GameExe"/>):
    /// also a state saved on a bootstrap stub, so its recorder moves (Reconcile) and its compile shows stale (WarmChanged).</summary>
    Game Unreal(Game g) => ShippingExe(g.InstallDir, g.ExePath) is var exe && exe != g.ExePath ? g with { ExePath = exe } : g;

    /// <summary><see cref="GameFiles.GameExe"/>, an exe it can't tell logged.</summary>
    string ShippingExe(string installDir, string exe) => ShippingLookup?.Invoke(installDir, exe) ?? GameFiles.GameExe(installDir, exe, m => Log?.Report(m));

    /// <summary>Tests: stands for <see cref="GameFiles.GameExe"/>, to count its folder walks.</summary>
    internal Func<string, string, string>? ShippingLookup { get; set; }

    /// <summary>The store's build and the install root's own entries (as <see cref="FolderStamp"/> reads them): what an update or
    /// a reinstall changes, so a scan reuses the Shipping exe found before (<see cref="ShippingPick"/>) without walking the
    /// install. Null when the root can't be listed or holds a write time FileInfo can't read (the scan says why, later).</summary>
    static string? RootStamp(Game g)
    {
        try { return Stamp(g.Version, GameFiles.DirKey(g.InstallDir)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>What was read of the games' DLLs, for the next start (<see cref="Middleware.LoadImages"/>, <see cref="ReShade.LoadProbes"/>).</summary>
    void SaveDllReads()
    {
        try
        {
            Middleware.SaveImages(ImagesFile);
            ReShade.SaveProbes(ProbesFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"couldn't save what was read of the games' DLLs: {e.Message}"); }
    }

    string StutterFile => Path.Combine(Store.DataDir, "known-stutter.json");
    string VerdictsFile => Path.Combine(Store.DataDir, GameVerdicts.FileName);
    ContentTrust RulesTrust => new(Store, ContentKeys);
    /// <summary>The keys signed content files must verify with: <see cref="ContentTrust.RulesKeys"/> (tests pin their own).</summary>
    public IReadOnlyDictionary<string, string> ContentKeys { get; set; } = ContentTrust.RulesKeys;
    string ImagesFile => Path.Combine(Store.DataDir, "middleware.json");
    string ProbesFile => Path.Combine(Store.DataDir, "reshade.json");
    string ConfirmedFile => Path.Combine(Store.DataDir, "confirmed-engines.json");
    DateTime _stutterTried;

    /// <summary>At most about once a day unless <paramref name="force"/>d (a user's refresh, which runs after a check
    /// already under way): a server list, when valid, replaces the one in use (and the cached copy), and the games it
    /// changes come as GameChanged. The check: true unless no known-stutter list came.</summary>
    Task<bool> StartStutterUpdate(bool force)
    {
        if (ContentRoutes is not { } routes) return Task.FromResult(true);
        lock (_scanLock)
        {
            var saved = File.GetLastWriteTimeUtc(StutterFile);   // 1601 when missing
            var last = saved > _stutterTried ? saved : _stutterTried;
            // 20 h: a daily start at about the same time still checks
            if (!force && (!StutterUpdate.IsCompleted || DateTime.UtcNow - last < TimeSpan.FromHours(20))) return Task.FromResult(true);
            _stutterTried = DateTime.UtcNow;
            var check = StutterUpdate.ContinueWith(async _ =>
            {
                var list = await StutterList.FetchAsync(StutterFile, routes);
                if (list != null) UseStutterList(list);
                if (await ConfirmedEngines.FetchAsync(ConfirmedFile, routes) is { } confirmed) UseConfirmedEngines(confirmed);
                (string? Token, IReadOnlyCollection<string>? Ent)? access = null;
                try { access = ChannelAccess == null ? (null, null) : await ChannelAccess(); }
                catch (Exception) { }   // not known: the public list, and the channel copies kept count
                var read = access is { } known ? VerdictChannels(UpdateChannels.Effective(Settings.UpdateChannel, AppVersion.Current.Channel, known.Ent)) : null;
                // none of the channels read verifies: the app's own
                UseVerdicts(await RulesTrust.FetchAsync(GameVerdicts.Name, GameVerdicts.TryParse, VerdictsFile, read, access?.Token, routes) ?? GameVerdicts.Embedded);
                return list != null;
            }, TaskScheduler.Default).Unwrap();
            StutterUpdate = check;
            return check;
        }
    }

    void UseConfirmedEngines(ConfirmedEngines list)
    {
        ConfirmedEngines.Current = list;
        lock (_refreshLock)
            foreach (var s in Games)
                if (s.StatusReason.Contains(Planner.UntestedNote) && RootSig.Verified(s.Engine!)) Refresh(s.Game);
    }

    // the recorder's default and the status follow the list, so a game it changes is re-evaluated and reconciled
    void UseVerdicts(GameVerdicts list)
    {
        var was = GameVerdicts.Current;
        GameVerdicts.Current = list;
        List<string> changed;
        lock (_refreshLock)
        {
            changed = Games.Where(s => list.NoStutter(s.Game, s.Engine) != was.NoStutter(s.Game, s.Engine)
                || list.Unsupported(s.Game, s.Engine) != was.Unsupported(s.Game, s.Engine)).Select(s => s.Game.Id).ToList();
            foreach (var s in Games.Where(s => changed.Contains(s.Game.Id))) Refresh(s.Game);
        }
        if (ManageRecorders)
            foreach (var id in changed) ReconcileRecorders(id);
        if (changed.Count > 0) KeepList();   // a start without a scan restores the kept states
    }

    /// <summary>The channel copies of the game verdicts an update channel reads, as for the news: internal reads alpha's.</summary>
    static string[] VerdictChannels(string channel) => channel switch
    {
        UpdateChannels.Beta => [UpdateChannels.Beta],
        UpdateChannels.Alpha or UpdateChannels.Internal => [UpdateChannels.Alpha],
        _ => [],
    };

    static string? NoStutterOf(Game g, EngineInfo? engine, KnownStutter? known) => known == null ? GameVerdicts.Current.NoStutter(g, engine) : null;

    void UseStutterList(StutterList list)
    {
        var changed = new List<GameState>();
        lock (_lock)
        {
            StutterList.Current = list;
            for (int i = 0; i < _games.Count; i++)
                if (list.Find(_games[i].Game) is var k && k != _games[i].KnownStutter)
                    changed.Add(_games[i] = _games[i] with { KnownStutter = k });
        }
        foreach (var s in changed) GameChanged?.Invoke(s);
        // a no-stutter verdict yields to a known-stutter entry, and the recorder's default follows it
        var verdict = changed.Where(s => NoStutterOf(s.Game, s.Engine, s.KnownStutter) != s.NoStutter).Select(s => s.Game).ToList();
        lock (_refreshLock)
            foreach (var g in verdict) Refresh(g);
        if (ManageRecorders)
            foreach (var g in verdict) ReconcileRecorders(g.Id);
        if (changed.Count > 0) KeepList();
    }

    /// <summary><paramref name="ct"/> cancelled (a stopped compile's refresh), before or while the import or the keys write
    /// waits for the recording lock: they're skipped, and the next refresh does them.</summary>
    /// <param name="driverStale">warmed for another driver, from the same snapshot as the state: published with it (Newest, Refresh)</param>
    GameState Evaluate(Game g, bool force, out bool fresh, out bool driverStale, CancellationToken ct = default)
    {
        try
        {
            var s = EvaluateGame(g, force, out fresh, out driverStale, ct);
            _unread.TryRemove(g.Id, out _);
            return s;
        }
        // one game's unreadable files (a file time past the year 9999 throws) never fail the scan of the others
        catch (Exception e) when (e is not OperationCanceledException)
        {
            fresh = false;
            return Unread(g, e, out driverStale);
        }
    }

    /// <summary>Game ids whose last evaluation failed: <see cref="Reconcile"/> leaves their recorder as it is until one
    /// succeeds, since an error may pass (a locked file) and the state it would act on is the last one, not the game's now.</summary>
    readonly ConcurrentDictionary<string, bool> _unread = new();

    /// <summary>A game whose evaluation failed: its last state with the error shown, else a bare unsupported one.</summary>
    GameState Unread(Game g, Exception e, out bool driverStale)
    {
        Log?.Report($"{g.Name}: couldn't be read: {e.Message}");
        _unread[g.Id] = true;
        var reason = $"couldn't read its files: {e.Message}";
        GameState? last;
        lock (_lock) (last, driverStale) = (_games.FirstOrDefault(s => s.Game.Id == g.Id), _driverStale.GetValueOrDefault(g.Id));
        return last != null ? last with { Game = g, Status = GameStatus.Unsupported, StatusReason = reason, NotPlanned = false }   // an error may pass: "Not supported yet"
            : new GameState(g, null, AntiCheat.None, GameStatus.Unsupported, reason, null, null, null, null, null, null, null, false, null) { RecorderSkip = SkipUnsupported };
    }

    GameState EvaluateGame(Game g, bool force, out bool fresh, out bool driverStale, CancellationToken ct)
    {
        var rec = Store.LoadGame(g.Id);
        var gpuNow = Snapshot();
        var exeDir = Path.GetDirectoryName(g.ExePath)!;
        var cap = CarefulThreads(Vendor.Vendor);
        // a launch that started before the warm ended judged an earlier warm (one played while this one compiled)
        var earlier = rec.FirstLaunch is { } fl && fl.At < rec.WarmedAt;
        if (earlier) rec.FirstLaunch = null;
        var nvidia = Vendor.Vendor == GpuVendor.Nvidia;
        var judge = (cap != null || nvidia) && rec.FirstLaunch == null ? rec.WarmedAt : null;
        // the RayQuery floor is measured on NVIDIA only
        var rayQuery = nvidia ? SessionLog.ReadRayQueryKeys(RayQueryKeysPath(g.Id)) : null;
        // the game folder's, else the last offline session's, kept in the data folder when its files left the game's
        var sessionDir = SessionFiles.Any(f => File.Exists(Path.Combine(exeDir, f))) ? exeDir : OfflineSessionDir(Store, g.Id);
        var frames = Frames(sessionDir, Path.GetFileName(g.ExePath), rayQuery, rec.LastPlay);
        var (session, marker, first) = SessionLog.Read(Path.Combine(sessionDir, "scskiller_creates.csv"), out var framesMatch, Path.GetFileName(g.ExePath), judge, MinJudgedCreates, rayQuery, rec.LastPlay, frames);
        if (session != null && !framesMatch) frames = null;   // another launch's frames: none shown next to this one's counts
        if (first != null && !nvidia) rec.FirstLaunch = first;   // NVIDIA's waits for the launch's recording, imported below
        var (rtUnseenWas, longWas) = (rec.RtUnseen, rec.RecordedLong);
        rec.RtUnseen = RtUnseen(session, rec.RtUnseen);
        rec.RecordedLong |= session?.Duration >= EnoughRecording;
        if (first != null && AgsLaunchMissed(rec)) { rec.AgsMissed = true; Log?.Report($"{g.Name}: {AgsMissedReason(rec)}"); }
        var keysOf = rec.KeysIndexHash;
        var (imported, keysPending, upkept) = (false, rec.KeysPending, false);
        try
        {
            // the game was updated (no longer this build's shaders), or a rewrite waited for it to exit
            if (keysPending || keysOf != null && !IndexIsInstalled(g, rec)) WriteKeys(g, rec, ct);
            imported = ImportRecording(g, rec, ct: ct);
            upkept = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        // the recording's upkeep never keeps the evaluation (and its anti-cheat check) from running: retried next time
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { Log?.Report($"{g.Name}: the recording's upkeep failed: {e.Message}"); }
        if (first != null && nvidia && !upkept) first = null;   // judged by a later evaluation, with the launch's records
        else if (first != null && nvidia)
        {
            rec.FirstLaunch = first;
            if (WarmedCreates(g, rec, first, Path.Combine(sessionDir, "scskiller_creates.csv"), rayQuery, gpuNow) is { } w && Unreached(w))
            {
                rec.UnreachedWarm = rec.WarmedAt;
                Log?.Report($"{g.Name}: {UnreachedReason} ({w.N:N0} warmed pipelines at its first launch, median {w.MedianMs:0} ms)");
            }
        }
        try
        {
            if (imported | MergeLaunched(g, rec, marker) | AdoptDriverId(rec, gpuNow) | first != null | earlier | rec.KeysIndexHash != keysOf | rec.KeysPending != keysPending | rec.RtUnseen != rtUnseenWas | rec.RecordedLong != longWas) Store.SaveGame(g.Id, rec);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{g.Name}: couldn't save its record: {e.Message}"); }
        long? psos = PlanItems(rec.Plan?.Stats);
        long? recorded = rec.Plan?.Stats.Recorded;
        TimeSpan? fast = psos is { } n ? TimeSpan.FromSeconds(n / (rec.PsoPerSecond ?? WarmRate)) : null;
        TimeSpan? careful = cap != null && psos is { } np && recorded is { } nr ? TimeSpan.FromSeconds(nr / DefaultCarefulPsoPerSecond + (np - nr) / (rec.PsoPerSecond ?? WarmRate)) : null;
        var (_, engine, antiCheat, check, _, streamlineFirst, _, _) = Evaluated(g, rec, force, out fresh);
        var slFirst = streamlineFirst && Vendor.Vendor == GpuVendor.Nvidia;
        driverStale = rec.WarmedAt != null && !CurrentDriver(gpuNow, rec.WarmedDriverId, rec.WarmedDriverVersion);
        var counted = rec.Pending;
        var pending = PendingOf(g, rec);   // after the import and the scan's engine: derived from what they and any download left
        if (rec.Pending != counted)
            try { Store.SaveGame(g.Id, rec); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{g.Name}: couldn't save its record: {e.Message}"); }
        if (engine != null && check.Reason.Contains(Planner.Untested) && RootSig.Verified(engine))
            check = check with { Reason = check.Reason.Replace(Planner.Untested, Planner.NoRecording) };
        else if (antiCheat != AntiCheat.None) check = check with { Reason = check.Reason.Replace(Planner.Untested, Planner.UntestedNote) };
        var manifest = LocalManifest();
        var entry = manifest != null ? DbEntry(manifest, g, rec) : null;
        bool? inDb = manifest != null ? entry != null : null;
        // a recording newer than the plan may have the ray tracing (inline, or state objects): its plan check tells
        var rtToPlan = RtPlanCheck(rec, engine);
        var rtUnseen = NeedsRtRecording(rec.Plan?.Stats) && !rtToPlan && rec.RtUnseen;
        var rt = NeedsRtRecording(rec.Plan?.Stats) && !rtToPlan && !rec.RtUnseen;
        var unseen = rtUnseen ? "; " + RtUnseenNote : RtInlineCovers(rec.Plan?.Stats) ? "; " + RtInlineNote : "";
        var reshade = antiCheat == AntiCheat.None ? ReShade.Detect(g) : null;   // no binary read in an anti-cheat install
        var shaderMod = reshade?.ShaderMod;
        if (reshade is { Blocks: true })
        {
            TakeOutNow(g, ShaderModReason(reshade));   // the command line doesn't reconcile
            rec = Store.LoadGame(g.Id);   // what the removal left
        }
        var dll = Path.Combine(exeDir, "d3d12.dll");
        bool ours;
        try { ours = IsOurProxy(dll); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ours = rec.RecorderFiles.ContainsKey("d3d12.dll"); }   // held by the game
        // the recorder is never installed next to anti-cheat, nor in a game the user added before they confirm its folder
        var unconfirmed = Unconfirmed(g);
        var noRecording = antiCheat != AntiCheat.None ? $"which {(antiCheat == AntiCheat.Other ? "its anti-cheat" : antiCheat)} blocks"
              + (OfflineEac.Of(g, antiCheat, engine) != null ? " unless you record an offline session (game page)" : "")
            : unconfirmed ? ManualNoRecording : slFirst ? StreamlineNoRecording : null;
        var rejected = Rejected(rec.LastWarmFailed, rec.LastWarmCrashed);
        var partlyCompiled = IsPartlyCompiled(rejected, psos);
        // its rejects explain a first launch that still compiles: not a cache that ignores the warm
        var unreached = IsUnreached(rec) && nvidia && !partlyCompiled;
        var (status, reason) = check.Readiness switch
        {
            _ when GameVerdicts.Current.Unsupported(g, engine) is { } why => (GameStatus.Unsupported, why.Text),
            _ when reshade is { Blocks: true } => (GameStatus.Unsupported, ShaderModReason(reshade)),
            Readiness.Unsupported => (GameStatus.Unsupported, check.Reason),
            // no plan source at all: the game files, this PC's recording and the community's were each checked
            Readiness.NeedsRecording when OfflineEac.Of(g, antiCheat, engine) != null && !File.Exists(RecordingPath(g.Id)) && CommunityInUse(g.Id) == null
                => (GameStatus.NeedsRecording, OfflineSessionNote),   // a recording without draws keeps the arm below
            Readiness.NeedsRecording when noRecording != null => (GameStatus.Unsupported, $"needs a recording, {noRecording}" + DbNote(inDb)),
            Readiness.NeedsRecording => (GameStatus.NeedsRecording, check.Reason + DbNote(inDb)),
            // a recording can't help a game the warm doesn't reach
            _ when unreached => (rec.WarmedAt == null ? GameStatus.Ready : GameStatus.Warmed, UnreachedReason),
            // the plan compiles the rest, but not the game's ray tracing: that needs a recording (compiling stays possible, partial)
            _ when rt && noRecording == null && rec.WarmedAt == null => (GameStatus.NeedsRecording, RtNote(inDb)),
            _ when rec.WarmedAt == null => (GameStatus.Ready, RtBlocked(rt, noRecording, PartialNote(rec.Plan?.Stats) ?? check.Reason) + unseen),
            _ when StaleReason(g, rec, pending, gpuNow) is { } why => (GameStatus.Stale, why),
            _ => (GameStatus.Warmed, RtBlocked(rt, noRecording, (partlyCompiled ? $"partly compiled for driver {rec.WarmedDriverVersion}: {RefusedNote(rejected ?? 0, psos ?? 0)}"
                    : cap is { } t && PartlyWarmedNote(rec, t, careful) is { } partly
                    ? $"partly warmed for driver {rec.WarmedDriverVersion}: {partly}" : $"warmed for driver {rec.WarmedDriverVersion}")
                + (PartialNote(rec.Plan?.Stats) is { } partial ? "; " + partial : "")) + unseen),
        };
        return WithRecorder(new GameState(g, engine, antiCheat, status, reason, rec.ShaderCount, rec.Plan?.Stats,
            psos * (long)(rec.BytesPerPso ?? DefaultBytesPerPso),
            rec.Careful && careful != null ? careful : fast,
            rec.WarmedDriverVersion, rec.WarmedAt, rec.LastWarmTime, ours,
            session,
            rec.CacheKeys.Count > 0 && AppCache != null ? AppCache.SizeOf(rec.CacheKeys) : null,
            rec.LaunchedExeName,
            // ponytail: re-detected on every evaluation (one directory listing); cache with the scan if listings get slow
            antiCheat == AntiCheat.None ? Middleware.Tags(Middleware.Detect(exeDir), d => SeedsPacks(g) ? (_planner as Planner)?.PackPipelines(d, OnAmd) ?? 0 : 0) : null,
            rec.LastWarmFailed, rec.LastWarmSkipped, StutterList.Current.Find(g),
            CommunityInUse(g.Id) is { } c ? new CommunityInfo(c.Psos, c.DownloadedAt, File.Exists(RecordingPath(g.Id))) : null,
            Sharing.Shared(Store.GameDir(g.Id))?.At, inDb,
            pending.Planned,
            IsPlaying(g.Id)) with { LastWarmNeedsRecording = rec.LastWarmNeedsRecording, LastWarmCrashed = rec.LastWarmCrashed,
                Careful = cap != null ? new CarefulCompile(rec.Careful, rec.FirstLaunch?.Compiled, careful, recorded) : null,
                RecordedSinceWarm = pending.Recorded, NewOnly = status == GameStatus.Stale && !pending.Unknown && WarmChanged(g, rec, gpuNow) == null, CommunityDbPsos = entry?.Psos ?? 0, PsoPerSecond = rec.PsoPerSecond,
                LastFrames = frames, ShaderMod = shaderMod?.Mod, ShaderModBlocks = reshade?.Blocks == true, ShaderModLayer = reshade?.Layered == true,
                ShaderModAsD3D12 = reshade is { Layered: true, AsD3D12: true }, RtUnseen = rtUnseen, RtToPlan = rtToPlan, RecordedEnough = rec.RecordedLong, RootUnconfirmed = unconfirmed,
                StreamlineFirst = slFirst && engine != null && check.Readiness != Readiness.Unsupported,
                NoStutter = NoStutterOf(g, engine, StutterList.Current.Find(g)), CacheInUse = CacheInUse(rec.CacheKeys),
                CompileUnreached = unreached && status != GameStatus.Unsupported, CacheFileFull = nvidia && AppCache is NvidiaAppCache nv && nv.NearlyFull(rec.CacheKeys),
                NotPlanned = GameVerdicts.Current.Unsupported(g, engine) is { } verdict ? verdict.Kind == GameVerdicts.UnsupportedVerdict
                    : status == GameStatus.Unsupported && reshade is not { Blocks: true } && antiCheat != AntiCheat.None   // a recording its anti-cheat blocks
                      && (check.Readiness == Readiness.NeedsRecording || RootSig.PlannedOnlyFromARecording(engine?.Fork)) },
            rec, ours, exeDir);
    }

    GameState WithRecorder(GameState s, GameRecord rec, bool ours, string exeDir)
    {
        var skip = RecorderSkip(s, ModSkip(s.Game, exeDir, rec, ours)) ?? _recorderSkips.GetValueOrDefault(s.Game.Id);
        var o = rec.Recorder ?? (ours ? RecorderOverride.On : RecorderOverride.Default);
        var db = Length(Path.Combine(exeDir, "scskiller.db"));
        return s with { RecorderOverride = o, RecorderSkip = skip, RecorderEffective = RecorderEffective(o, Settings.RecordAllGames && s.NoStutter == null, skip),
            RecorderNote = _recorderNotes.GetValueOrDefault(s.Game.Id), RecorderMod = ModName(exeDir, rec, ours),
            RecorderRefused = !ours ? null : LastLaunch(s.Game, rec, exeDir),
            RecordAlongsideMod = rec.RecordAlongsideMod,
            RecordingBytes = RecordingFiles(s.Game).Where(f => Path.GetFileName(f) != FrameLog.FileName).Sum(Length),
            RecordingPaused = ours && DbCap(s.Game) is { } cap && db >= cap,
            OfflineEligible = OfflineEac.Of(s.Game, s.AntiCheat, s.Engine) != null && GameVerdicts.Current.Unsupported(s.Game, s.Engine) == null, OfflineRecord = rec.OfflineRecord,
            OfflineRunning = OfflineLive(s.Game.Id) || rec.OfflineSession != null };
    }

    static long Length(string path) => new FileInfo(path) is { Exists: true } f ? f.Length : 0;

    /// <summary>What <see cref="ClearRecording"/> deletes: the recorder's output in the game folder and SCSKiller's copy of it
    /// (an offline session's report too).</summary>
    IEnumerable<string> RecordingFiles(Game g) => [.. RecorderDataFiles.Select(f => Path.Combine(Path.GetDirectoryName(g.ExePath)!, f)), RecordingPath(g.Id),
        .. SessionFiles.Select(f => Path.Combine(OfflineSessionDir(Store, g.Id), f)),
        .. new[] { "recording.all.db", "recording.all.db.key" }.Select(f => Path.Combine(Store.GameDir(g.Id), f))];   // until migrated

    static readonly string[] RecorderDataFiles = ["scskiller.db", "scskiller_creates.csv", "scskiller.log", FrameLog.FileName];

    readonly ConcurrentDictionary<string, (long Bin, DateTime Written, long Csv, PlayWindow? Played, FrameReport? Report)> _frames = new();

    /// <summary>The frame log's report, read again only when it, the creates csv or the watched run changed: a scan
    /// evaluates every game.</summary>
    FrameReport? Frames(string exeDir, string exe, IReadOnlySet<string>? rayQuery, PlayWindow? played)
    {
        var (bin, csv) = (new FileInfo(Path.Combine(exeDir, FrameLog.FileName)), Path.Combine(exeDir, "scskiller_creates.csv"));
        if (!bin.Exists) return null;
        var stamp = (bin.Length, bin.LastWriteTimeUtc, Length(csv), played);
        if (_frames.TryGetValue(bin.FullName, out var c) && (c.Bin, c.Written, c.Csv, c.Played) == stamp) return c.Report;
        FrameReport? r;
        try { r = FrameLog.Read(bin.FullName, csv, exe, rayQuery, played); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        _frames[bin.FullName] = (stamp.Length, stamp.LastWriteTimeUtc, stamp.Item3, played, r);
        return r;
    }

    /// <summary>The size the game folder's scskiller.db may reach (the proxy's max_db_bytes): what the limit leaves beside the
    /// imported recording (<paramref name="stored"/>). An import moves the db's records into it, where they take less room,
    /// and empties the db. Null = unlimited.</summary>
    public static long? DbCap(long limitBytes, long stored) => limitBytes > 0 ? Math.Max(0, limitBytes - stored) : null;

    long? DbCap(Game g) => DbCap(Settings.RecordingLimitMB * (1L << 20), Length(RecordingPath(g.Id)));

    /// <summary>The choices for <see cref="Settings.RecordingLimitMB"/>.</summary>
    public static readonly int[] RecordingLimits = [32, 128, 256, 512, 1024, 0];

    /// <summary>"256 MB", "1 GB"; "Unlimited" for 0.</summary>
    public static string LimitText(int mb) => mb <= 0 ? "Unlimited" : mb % 1024 == 0 ? $"{mb / 1024} GB" : $"{mb} MB";

    public static string PausedNote(Settings s) => $"Recording paused: limit reached ({LimitText(s.RecordingLimitMB)})";

    /// <summary>The last build can't compile the game's ray tracing and no recording has any: more than 10% of its DXIL
    /// libraries have no synthesized collection and no recorded state object (<see cref="PlanStats.RtUncovered"/>): AMD (its
    /// driver caches only the exact objects a game builds), or an engine whose collection layout SCSKiller can't rebuild from
    /// its files (Unreal 5). A recording with ray tracing in it already is one: what it left uncovered shows as a count only.
    /// Known only after a build, like <see cref="IsPartial"/>.</summary>
    public static bool NeedsRtRecording(PlanStats? p) => RtLeft(p) && p!.RtInline is not > 0;

    /// <summary>More than 10% of the libraries uncovered and no recorded state object, whatever the inline ray tracing.</summary>
    static bool RtLeft(PlanStats? p) => p is { RtUncovered: > 0, RtStateObjects: 0 } && p.RtUncovered > 0.1 * p.RtLibraries;

    /// <summary>Unreal 5 whose ray tracing is inline (<see cref="PlanStats.RtInline"/>, compiled from the game files): its
    /// uncovered DXIL libraries don't need a recording, and the status says what only one would add (<see cref="RtInlineNote"/>).</summary>
    public static bool RtInlineCovers(PlanStats? p) => RtLeft(p) && p!.RtInline > 0;

    public const string RtInlineNote = "ray tracing that uses separate pipelines (such as path tracing) is compiled only from a recording";

    /// <summary>The same for a game's state: not while a newer recording waits for its plan (<see cref="GameState.RtToPlan"/>),
    /// nor once it was recorded long enough without ray tracing (<see cref="GameState.RtUnseen"/>).</summary>
    public static bool NeedsRtRecording(GameState s) => NeedsRtRecording(s.Plan) && !s.RtUnseen && !s.RtToPlan;

    /// <summary>The plan asks for a ray tracing recording and a recording was imported after it was built, (Unreal 5) it
    /// was built before inline ray tracing was counted, or the engine now says the game builds no state object: the plan
    /// check (<see cref="CheckPlans"/>) or the next compile plans it again.</summary>
    static bool RtPlanCheck(GameRecord r, EngineInfo? e) => NeedsRtRecording(r.Plan?.Stats)
        && (r.RecordingImportedAt > r.PlanBuiltAt || r.Plan!.Stats.RtInline == null && e is { Family: "Unreal" } && e.Version.StartsWith('5') || e is { NoRtPipelines: true });

    /// <summary>A recorded launch this long shows what the player's setup uses (the "about 5 minutes" the app asks for).</summary>
    public static readonly TimeSpan EnoughRecording = TimeSpan.FromMinutes(5);

    /// <summary>A recorded launch was <see cref="EnoughRecording"/> or longer since the recording was last cleared: asking for "5 minutes" again says nothing.</summary>
    public static bool RecordedEnough(GameState s) => s.RecordedEnough;

    public const string NothingRecordedTitle = "Played, nothing recorded";
    public const string NeverSawNote = "The recorder didn't load in the last launch. Another tool may load DirectX 12 before it. Please report it with the game's name.";
    public const string GameChangedNote = "The game changed since the recorder was set up. It is set up again: play again.";
    const string RanOtherExe = "The last launch ran ";
    public static string RanOtherExeNote(string exe) => $"{RanOtherExe}{exe}, not the exe the recorder was set up for. It is set up for it now: play again.";

    /// <summary>The last launch was played with the recorder in and recorded nothing: another exe of the folder ran, the game
    /// changed since it was armed, or the recorder never loaded. The list says so instead of asking for a recording again.</summary>
    public static bool NeverRecorded(GameState s) => s is { RecorderInstalled: true, RecorderSkip: null, RecorderRefused: { } r }
        && (r is NeverSawNote or GameChangedNote || r.StartsWith(RanOtherExe, StringComparison.Ordinal));

    /// <summary>What the game page says about the last launch when it wasn't recorded (<see cref="GameState.RecorderRefused"/>);
    /// null = it recorded, or no launch since. A switch to another exe of the folder in that launch wins: the recorder loaded
    /// into it and refused it (or couldn't tell).</summary>
    static string? LastLaunch(Game g, GameRecord rec, string exeDir)
    {
        var file = LedgerFile(g.ExePath) + ".refused";
        // one from before the last watched run is another launch's
        var refused = rec.LastPlay is { } last && File.Exists(file) && File.GetLastWriteTimeUtc(file) < last.From.UtcDateTime ? null : Refused(g.ExePath);
        var neverSaw = NeverSaw(g.ExePath, exeDir, rec.LastPlay);
        if ((refused == "not armed" || neverSaw) && rec is { RunsExeAt: { } at, LastPlay: { } run, RunsExeWas: { } was } && run.From <= at && at <= run.To
            && rec.RunsExe == g.ExePath && !Path.GetFileName(was).Equals(Path.GetFileName(g.ExePath), StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(was)!.Equals(exeDir, StringComparison.OrdinalIgnoreCase))
            return RanOtherExeNote(Path.GetFileName(g.ExePath));
        if (neverSaw) return NeverSawNote;
        return refused switch
        {
            null => null,
            "not armed" or "disarmed while deciding" => GameChangedNote,   // armed again by the watcher's check of the change
            var why => $"The last launch wasn't recorded: {why}.",
        };
    }

    /// <summary><see cref="GameRecord.RtUnseen"/> after the last recorded launch: a ray tracing state object in it clears it, a
    /// launch of <see cref="EnoughRecording"/> without one sets it (ray tracing off in the game: no recording will ever have one).</summary>
    internal static bool RtUnseen(SessionStats? last, bool before) => last switch
    {
        null => before,
        { StateObjectsReady: > 0 } or { StateObjectsCompiled: > 0 } or { StateObjectsStartupCompiled: > 0 } => false,
        _ => before || last.Duration >= EnoughRecording,
    };

    /// <summary>The end of a Ready or Warmed reason when <see cref="GameState.RtUnseen"/> keeps a plan's uncovered ray tracing from being asked for.</summary>
    public const string RtUnseenNote = "no ray tracing seen while recording: turn it on in the game and play to cover it";

    /// <summary>The status reason of a game whose ray tracing needs a recording (<see cref="NeedsRtRecording"/>).</summary>
    public static string RtNote(bool? inCommunityDb) => RtNeedsRecording + (inCommunityDb == null ? ", or a community recording for this version" : DbNote(inCommunityDb));

    public const string RtNeedsRecording = "Ray-traced effects need one short recording";

    /// <summary>The end of a NeedsRecording reason when the manifest has an entry for the build, whatever the sign-in (the
    /// manifest is public; downloads need "db"). Short: the Library row shows three lines.</summary>
    public const string InDbNote = "in the community database";

    static string DbNote(bool? inDb) => inDb switch { true => "; " + InDbNote, false => "; not in the community database yet", null => "" };

    /// <summary>The manifest's entry for the installed build: by the index's content hash while the index is of this build
    /// (or, without a store build id, from before the exe stamp was kept), else by the store build's alias.</summary>
    static CommunityEntry? DbEntry(CommunityManifest m, Game g, GameRecord rec) =>
        (IndexIsInstalled(g, rec) || g.Version == null && rec.IndexExeStamp == null ? m.Find(g, rec.IndexContentHash) : null) ?? m.Find(g, null);

    /// <summary>Why a game's ray tracing needs a recording on this PC (<see cref="NeedsRtRecording"/>), for the detail page.</summary>
    public static string RtWhy(VendorCaps caps, EngineInfo? e) => caps.RtCacheGranularity == RtCacheGranularity.WholeObject
        ? "This GPU's driver reuses ray tracing pipelines only exactly as the game builds them, and which ones it builds isn't in its files."
        : $"SCSKiller can't rebuild {(e is { } x ? Format.Engine(x) : "this engine")}'s ray tracing layout from the game files yet.";

    static string RtBlocked(bool rt, string? noRecording, string reason) => !rt ? reason
        : noRecording != null ? $"{reason}; ray-traced effects aren't compiled: they need a recording, {noRecording}" : $"{reason}; {RtAfterRecordingNote}";

    /// <summary>The end of a Warmed reason when a recording would add the game's ray tracing: compiled, the recording offered.</summary>
    public const string RtAfterRecordingNote = "ray tracing needs a 5-min recording";

    public static bool RtAfterRecording(GameState s) => s.Status == GameStatus.Warmed && s.StatusReason.Contains(RtAfterRecordingNote, StringComparison.Ordinal);

    /// <summary>The game page's note under a compilable status when the plan rests on a guess: a guessed Unreal version
    /// changes DirectX 12 root signatures (DirectX 11 has none), a DirectX that is the engine's default may not be the
    /// one the game runs. Null: nothing guessed that matters.</summary>
    public static string? GuessNote(EngineInfo e) => (e.VersionGuessed, e.ApiGuessed) switch
    {
        (true, true) => "Unreal version and DirectX",
        (true, false) when e.GraphicsApi.Contains("D3D12") => "Unreal version",
        (false, true) => "DirectX",
        _ => null,
    } is { } what ? what + " guessed, so the compile could be wrong." : null;

    /// <summary>Why a game the user added has no recorder yet, after "needs a recording, ".</summary>
    public const string ManualNoRecording = "once you confirm its game folder", StreamlineNoRecording = "which DLSS on NVIDIA blocks";
    /// <summary>An EasyAntiCheat game that an offline session can record (<see cref="OfflineEac"/>), with no recording yet:
    /// NeedsRecording with this reason, "Needs an offline session".</summary>
    public const string OfflineSessionNote = "EasyAntiCheat blocks the recorder, but you can record at your own risk with an offline session.";

    /// <summary>The game waits for an offline session (<see cref="OfflineSessionNote"/>).</summary>
    public static bool NeedsOfflineSession(GameState s) => s.Status == GameStatus.NeedsRecording && s.StatusReason == OfflineSessionNote;

    /// <summary>The last community manifest fetched (community\manifest.bin), re-read when the file changes; null when there is none.</summary>
    CommunityManifest? LocalManifest()
    {
        var f = new FileInfo(Path.Combine(Store.DataDir, "community", "manifest.bin"));
        var stamp = f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "";
        lock (_scanLock)
        {
            if (stamp != _manifestStamp)
            {
                _manifestStamp = stamp;
                try { _manifest = f.Exists ? CommunityManifest.Parse(File.ReadAllBytes(f.FullName)) : null; }
                catch (Exception x) when (x is IOException or InvalidDataException or UnauthorizedAccessException) { _manifest = null; }
            }
            return _manifest;
        }
    }
    CommunityManifest? _manifest;
    string? _manifestStamp;

    /// <summary>The last build left more than 10% of the game's stage sets out (<see cref="PlanStats.Uncovered"/>: no root
    /// signature SCSKiller can build covers them; Hogwarts Legacy without a recording: 182857 left out, 41422 planned). The
    /// share is taken against the planned PSOs (at most one per stage set), so it errs toward "partial". Known only after a
    /// build: <see cref="GameRecord.Plan"/> keeps the last build's stats, which is what <see cref="Evaluate"/> reads.</summary>
    public static bool IsPartial(PlanStats? p) => p is { Uncovered: > 0 } && p.Uncovered > 0.1 * (p.Uncovered + p.Recorded + p.Generated);

    /// <summary>A plan with DirectX 12 pipelines and every DirectX 11 shader: the game runs on either.</summary>
    public static bool BothApis(PlanStats? p) => p is { D3D11Shaders: > 0 } && p.Recorded + p.Generated + p.MiddlewareItems > 0;

    /// <summary>The warm's items (<see cref="PlanStats"/>' comment); null without a plan.</summary>
    public static long? PlanItems(PlanStats? p) => p is { } x ? x.Recorded + x.Generated + x.D3D11Shaders + x.MiddlewareItems + x.Variants : null;

    /// <summary>Of the plan's items, rejected or crashed: completed warms measured at most 0.4%, a warm without the game's Agility SDK 70%.</summary>
    public const double PartlyCompiledShare = 0.1;

    /// <summary>Unknown counts (a warm from before they were kept, or none) make no claim.</summary>
    public static bool IsPartlyCompiled(long? rejected, long? items) => rejected > PartlyCompiledShare * items;
    static long? Rejected(long? failed, long? crashed) => failed + (crashed ?? 0);
    public static bool IsPartlyCompiled(GameState s) => s is { Status: GameStatus.Warmed, CompileUnreached: false }
        && IsPartlyCompiled(Rejected(s.LastWarmFailed, s.LastWarmCrashed), PlanItems(s.Plan));
    /// <summary>The Library's line: "the driver skipped 180,000 of 258,210 pipelines"</summary>
    public static string PartlyCompiledNote(GameState s) => PartlyCompiledNote(Rejected(s.LastWarmFailed, s.LastWarmCrashed) ?? 0, PlanItems(s.Plan) ?? 0);
    static string PartlyCompiledNote(long rejected, long items) => $"the driver skipped {rejected:N0} of {items:N0} pipelines";
    /// <summary>The page's and the CLI's: "the driver refused 180,000 of 258,210 pipelines, so those can still stutter the first time"</summary>
    public static string RefusedNote(GameState s) => RefusedNote(Rejected(s.LastWarmFailed, s.LastWarmCrashed) ?? 0, PlanItems(s.Plan) ?? 0);
    static string RefusedNote(long rejected, long items) => $"the driver refused {rejected:N0} of {items:N0} pipelines, so those can still stutter the first time";
    /// <summary>A partly compiled game's share of the plan's items the driver compiled, floored; null otherwise.</summary>
    public static int? CompiledPercent(GameState s) => IsPartlyCompiled(s) && PlanItems(s.Plan) is { } n && Rejected(s.LastWarmFailed, s.LastWarmCrashed) is { } r
        ? (int)Math.Floor(100.0 * Math.Max(0, n - r) / n) : null;

    /// <summary>A game stale only for new pipelines (<see cref="GameState.NewOnly"/>): the share of its plan's items its last
    /// warm compiled, floored; null otherwise. Counted pipelines against plan items: an estimate, never 100.</summary>
    public static int? CompiledOfPlanPercent(GameState s) => s is { Status: GameStatus.Stale, NewOnly: true } && PlanItems(s.Plan) is > 0 and var n
        && NewShaders.Count(s) is > 0 and var m ? (int)Math.Floor(100.0 * Math.Max(0, n - m) / n) : null;

    /// <summary>AMD's careful compile (<see cref="CarefulCompile"/>): the thread cap of its recorded passes, null on other
    /// vendors. Measured on AMD (Tiny Tina's Wonderlands' recording in sibling passes, the first launch after the warm,
    /// two runs each): 4 threads median 3.4 ms, 44% of the creates under 2 ms; 2 threads 7.3 ms, 36%.</summary>
    public static int? CarefulThreads(GpuVendor vendor) => vendor == GpuVendor.Amd ? AmdCarefulThreads : null;
    public const int AmdCarefulThreads = 4;
    /// <summary>A careful compile's rate for the recorded PSOs (AMD, 4 threads, sibling passes: the recording above, 18,115 PSOs
    /// in 271 s); the plan's other items at the game's fast rate.</summary>
    public const double DefaultCarefulPsoPerSecond = 67;
    /// <summary>A warmed game whose first launch still compiled more than this share of its creates (<see cref="SessionLog"/>'s
    /// compile mark, over 3 ms) is partly warmed; judged on a launch of at least <see cref="MinJudgedCreates"/> creates.</summary>
    public const double PartlyWarmedShare = 0.2;
    public const long MinJudgedCreates = 100;

    public static bool IsPartlyWarmed(LaunchCheck? launch) => launch != null && launch.Compiles > PartlyWarmedShare * (launch.Hits + launch.Compiles);
    public static bool IsPartlyWarmed(GameState s) => s.Status == GameStatus.Warmed && s.Careful?.LaunchCompiled > PartlyWarmedShare;

    /// <summary>A partly warmed game's (<see cref="IsPartlyWarmed"/>) note: what its first launch found and what a careful
    /// compile would do; null otherwise.</summary>
    static string? PartlyWarmedNote(GameRecord r, int threads, TimeSpan? careful) => r.FirstLaunch is not { } l || !IsPartlyWarmed(l) ? null
        : $"{l.Compiled * 100:0}% of the {l.Hits + l.Compiles:N0} pipelines its first launch created still compiled"
          + (r.WarmedCareful ? ", even after a careful compile"
              : r.Plan?.Stats.Recorded == 0 ? "; a careful compile only changes how the recorded pipelines compile, and there is no recording yet (turn recording on and play)"
              : $"; a careful compile ({threads} threads, in passes) reaches more of them" + (careful is { } t ? $", in about {Duration(t)}" : "")
                + (r.Careful ? ": the next compile is careful" : ""));

    /// <summary>"45 s", "8 min", "2 h 5 min".</summary>
    public static string Duration(TimeSpan t) => t.TotalMinutes < 1 ? $"{Math.Max(1, (int)t.TotalSeconds)} s"
        : t.TotalHours < 1 ? $"{(int)Math.Round(t.TotalMinutes)} min"
        : $"{(int)t.TotalHours} h" + (t.Minutes > 0 ? $" {t.Minutes} min" : "");

    /// <summary>The share of the stage sets found in the game files that the plan compiles (<see cref="PlanStats.StageSets"/>
    /// minus <see cref="PlanStats.LeftOut"/>); null when not counted (no plan, a DirectX 11-only plan, a plan from before
    /// the count). Only what the planner found: combinations a game assembles at run time aren't in it.</summary>
    public static double? Coverage(PlanStats? p) => p is { StageSets: > 0 } ? (double)(p.StageSets - p.LeftOut) / p.StageSets : null;

    /// <summary><see cref="Coverage"/> as a whole percent a player reads: floored, and never 100 while anything is left out.</summary>
    public static int? CoveragePercent(PlanStats? p) => Coverage(p) is { } c ? Math.Min((int)Math.Floor(c * 100), p!.LeftOut > 0 ? 99 : 100) : null;

    /// <summary>The status note of a partial plan (<see cref="IsPartial"/>): what it compiles and what would add the rest.</summary>
    public static string? PartialNote(PlanStats? p) => !IsPartial(p) ? null
        : p!.Recorded == 0 ? $"compiles {p.Generated:N0} pipelines; a 5-minute recording lets SCSKiller rebuild the rest"
        : $"compiles {p.Recorded + p.Generated:N0} pipelines; {p.Uncovered:N0} more shader combinations use shader slots SCSKiller can't rebuild yet";

    /// <summary>A queue line's progress (CLI): " done/total (N failed[, M skipped: not in this install]) rate/s", then for
    /// Done/Stopped the note (<see cref="WarmCounts"/>); "" without progress.</summary>
    public static string ProgressText(QueueItem q)
    {
        if (q.Progress is not { } x) return "";
        var counts = $"{x.Failed} failed" + (x.Skipped > 0 ? $", {x.Skipped} skipped: not in this install" : "");
        return $" {x.Done}/{x.Total} ({counts}) {x.PerSecond:0}/s" + (q.Stage is QueueStage.Done or QueueStage.Stopped or QueueStage.Warming && q.Note is { } n ? " - " + n : "");
    }

    /// <summary>A warming item's note when its done count stopped moving (<see cref="StallAfter"/>): "no progress for 3 min".</summary>
    public static string StalledNote(TimeSpan since) => $"no progress for {Math.Max(1, (int)since.TotalMinutes)} min";

    /// <summary>A warming item that stopped moving: its note says for how long, and it has no time estimate.</summary>
    public static bool Stalled(QueueItem q) => q.Stage == QueueStage.Warming && q.Note?.StartsWith("no progress") == true;

    /// <summary>What a warm's counts say beyond the compiled ones; null when all are 0. Failed = the driver rejected it;
    /// skipped = never replayed; crashed = never replayed because its create removed the device (crashed the GPU driver).</summary>
    public static string? WarmCounts(long failed, long skipped, long crashed = 0) =>
        string.Join(", ", new[]
        {
            failed > 0 ? $"{failed} failed (the driver rejected them)" : null,
            skipped > 0 ? $"{skipped} skipped (a shader not in this install)" : null,
            crashed > 0 ? $"{crashed} skipped ({(crashed == 1 ? "it crashes" : "they crash")} the GPU driver)" : null,
        }.OfType<string>()) is { Length: > 0 } s ? s : null;

    /// <summary>Warm rate (PSO/s) before this PC measured one, per vendor: NVIDIA 450 (30 threads: 450-1350);
    /// AMD 160 (~8 threads, from the AMD smoke and per-stage A/B runs: a cold PSO there is ~10 ms).</summary>
    public static double DefaultWarmRate(GpuVendor vendor) => vendor == GpuVendor.Amd ? DefaultAmdPsoPerSecond : DefaultPsoPerSecond;

    /// <summary>The compile memory budget "Auto" picks (GB) from the PC's total physical memory: one step per common size,
    /// with the thresholds between sizes (a 16 GB PC reports a little less).</summary>
    public static int AutoCompileMemoryGB(long totalPhysicalBytes) => (totalPhysicalBytes / (double)(1L << 30)) switch
    {
        <= 20 => 2,
        <= 28 => 4,
        <= 40 => 6,
        <= 56 => 8,
        _ => 16,
    };

    public static int CompileMemoryGB(Settings s) => s.MaxCompileMemoryGB > 0 ? s.MaxCompileMemoryGB : AutoCompileMemoryGB();

    public static int AutoCompileMemoryGB() => AutoCompileMemoryGB(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    public const double DefaultAmdPsoPerSecond = 160;

    /// <summary>The estimate's rate for a game without its own measured warm: the first complete warm measured on this PC for
    /// this vendor (<see cref="SeedWarmRate"/>), else <see cref="DefaultWarmRate"/>.</summary>
    double WarmRate => (_warmRate ??= Store.LoadWarmRates().GetValueOrDefault(Vendor.Vendor)) is > 0 and var r ? r : DefaultWarmRate(Vendor.Vendor);
    double? _warmRate;

    /// <summary>A warm that measures the compile rate: the game's driver cache held nothing for this driver when it started
    /// (never warmed, cleared, or a new driver, which drops the old cache). A re-warm on the same driver mostly hits, at
    /// several times the rate.</summary>
    public static bool ColdWarm(GameRecord rec, string? driverId) => rec.WarmedAt == null || driverId == null || rec.WarmedDriverId != driverId;

    /// <summary>The games' measured cold rates as one: their plans' items over the time each takes at its rate. Null: none measured.</summary>
    public static double? MeasuredRate(IEnumerable<GameState> games)
    {
        var m = games.Where(g => g is { PsoPerSecond: > 0, Plan: not null })
            .Select(g => (Items: (double)PlanItems(g.Plan)!.Value, Rate: g.PsoPerSecond!.Value))
            .Where(g => g.Items > 0).ToList();
        return m.Count > 0 ? m.Sum(g => g.Items) / m.Sum(g => g.Items / g.Rate) : null;
    }

    /// <summary>Keeps the first complete warm's rate as this vendor's rate (later warms keep their own per game).</summary>
    void SeedWarmRate(double psoPerSecond)
    {
        var rates = Store.LoadWarmRates();
        if (rates.ContainsKey(Vendor.Vendor)) return;
        rates[Vendor.Vendor] = psoPerSecond;
        Store.SaveWarmRates(rates);
        _warmRate = psoPerSecond;
    }

    /// <summary>The expensive part of a game's state, cached in scan.json per game. A cached "no anti-cheat" is checked
    /// again each time where an update puts one (the install root's and exe folder's own entries, names only), and for a
    /// game that has the recorder or may get it (<see cref="RecorderMayGoIn"/>) fully once those entries changed
    /// (<see cref="FullCheckOnChange"/>). A cache from another SCSKiller build is used as it is and the game detected again
    /// in the background (<see cref="RedetectInBackground"/>).</summary>
    Evaluation Evaluated(Game g, GameRecord rec, bool force, out bool fresh)
    {
        var key = string.Join('|', ExeStamp(g), GameFiles.DirKey(g.InstallDir), g.Version, Vendor.Caps.Profile, rec.RecordingImportedAt?.UtcTicks, CoreBuild, CommunityInUse(g.Id)?.Object);
        Evaluation? hit = null;
        long started;
        lock (_scanLock)
        {
            _scan ??= Store.LoadScan();
            _scan.TryGetValue(g.Id, out hit);
            started = ++_evaluations;
        }
        // read before Detect: what changes while it runs is seen by the next scan
        key += DetectStamp(g, hit?.Engine);
        fresh = force || hit == null || hit.Key != key;
        if (fresh && !force && hit != null && OnlyBuildDiffers(hit.Key, key))
        {
            _redetect[g.Id] = g;
            fresh = false;
        }
        if (!fresh)
        {
            if (hit!.AntiCheat != AntiCheat.None) TakeOutNow(g, $"{hit.AntiCheat} found");
            else if ((GameFiles.DetectAntiCheat(g, quick: true) is var near and not AntiCheat.None ? near
                         : RecorderMayGoIn(g, rec, hit.Engine) ? FullCheckOnChange(g) : AntiCheat.None) is not AntiCheat.None and var found)
            {
                AntiCheatFound(g, found);
                return hit with { AntiCheat = found };
            }
            if (Streamline(g, hit) is var now && !ReferenceEquals(now, hit))   // a DLL it depends on changed, not the exe
                lock (_scanLock)
                {
                    if (_scan.TryGetValue(g.Id, out var cached) && ReferenceEquals(cached, hit)) _scan[g.Id] = now;
                    Store.SaveScan(_scan);
                }
            return now;
        }
        var (gen, folders) = (InstallGen(g), FolderStamp(g));   // before Detect and the check, as the key
        EngineInfo? engine = null;
        PlanCheck check;
        try
        {
            engine = _reader.Detect(g);
            check = engine == null ? new(Readiness.Unsupported, "engine not supported yet")
                : CheckRecordings(g, engine);
        }
        catch (Exception e) { check = new(Readiness.Unsupported, e.Message); }
        var ev = Streamline(g, new Evaluation(key, engine, GameFiles.DetectAntiCheat(g), check));   // last: anti-cheat that appeared during Detect counts
        if (ev.AntiCheat != AntiCheat.None) AntiCheatFound(g, ev.AntiCheat, ev);
        else
        {
            EvaluateStep?.Invoke("checked");
            lock (_scanLock)
                if (_scanStarted.GetValueOrDefault(g.Id) < started)   // older than the cached verdict or an anti-cheat finding: kept out
                {
                    _verdicts.TryRemove(g.Id, out _);   // a full scan that started after any finding found none: clean again
                    _scanStarted[g.Id] = started;
                    _scan[g.Id] = ev with { Clean = InstallGen(g) == gen ? folders : null };   // an install change meanwhile: walked again
                    Store.SaveScan(_scan);
                }
        }
        return ev;
    }

    /// <summary><paramref name="ev"/> with <see cref="GameFiles.StreamlineFirst"/> read again when a file it was read from
    /// changed (the key holds only the exe's stamp); the same instance otherwise. Never read in an anti-cheat install.</summary>
    static Evaluation Streamline(Game g, Evaluation ev)
    {
        if (ev.AntiCheat != AntiCheat.None) return ev.StreamlineFirst ? ev with { StreamlineFirst = false } : ev;
        if (ev.StreamlineFiles is { } files && GameFiles.Stamp(files) == ev.StreamlineStamp) return ev;
        var read = new List<string>();
        var first = GameFiles.StreamlineFirst(g.ExePath, read) == true;
        return ev with { StreamlineFirst = first, StreamlineFiles = read, StreamlineStamp = GameFiles.Stamp(read) };
    }

    /// <summary>Keys that differ only in the SCSKiller build (the key's sixth field).</summary>
    static bool OnlyBuildDiffers(string cached, string now)
    {
        var (a, b) = (cached.Split('|'), now.Split('|'));
        if (a.Length != b.Length || a.Length < 6) return false;
        a[5] = b[5] = "";
        return a.SequenceEqual(b);
    }

    // game id -> a game whose scan cache another build wrote: detected again by RedetectInBackground
    readonly ConcurrentDictionary<string, Game> _redetect = new();
    Task _redetecting = Task.CompletedTask;

    /// <summary>Detects again, one game at a time at background priority, the games a scan listed from another build's cache.</summary>
    void RedetectInBackground()
    {
        lock (_scanLock)
            if (!_redetect.IsEmpty && _redetecting.IsCompleted)
                _redetecting = Task.Run(() => LowIo(() =>
                {
                    foreach (var id in _redetect.Keys)
                        if (_redetect.TryRemove(id, out var g) && !_removed.Contains(id))
                            try { Refresh(Games.FirstOrDefault(s => s.Game.Id == id)?.Game ?? g, force: true); }
                            catch (Exception e) { Log?.Report($"{g.Name}: detecting it again failed: {e.Message}"); }
                    ReleaseMemory();
                    SaveDllReads();
                    return 0;
                }));
    }

    /// <summary>The store's build and the own entries of the install root and the exe's folder (files by name, size and write
    /// time, the exe among them; folders by name), hashed: what a game update or an anti-cheat install changes, read without
    /// walking the tree. The recorder's files and data files (<see cref="DataFile"/>) are left out: they change as the game
    /// runs. Null when a folder can't be listed.</summary>
    internal static string? FolderStamp(Game g) => Stamp(g.Version, GameFiles.DirKey(g.InstallDir), GameFiles.DirKey(Path.GetDirectoryName(g.ExePath)!));

    static string? Stamp(string? version, params string[] dirs)
    {
        var text = new System.Text.StringBuilder(version).Append('|');
        try
        {
            foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                text.Append('>').Append(dir);
                foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", AllEntries).OrderBy(e => e.Name, StringComparer.Ordinal))
                    if (e is not FileInfo f) text.Append('|').Append(e.Name);
                    else if (!RecorderOwnFiles.Contains(f.Name) && !DataFile(f.Name)) text.Append('|').Append(f.Name).Append(':').Append(f.Length).Append(':').Append(f.LastWriteTimeUtc.Ticks);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
        return Convert.ToHexStringLower(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));
    }

    static readonly EnumerationOptions AllEntries = new() { IgnoreInaccessible = false, AttributesToSkip = 0 };

    /// <summary>The full anti-cheat check (<see cref="FullCheck"/>), unless the game's <see cref="FolderStamp"/> is the one
    /// its last clean full check read.</summary>
    AntiCheat FullCheckOnChange(Game g) => CleanSince(g, FolderStamp(g)) ? AntiCheat.None : FullCheck(g);

    /// <summary><see cref="FullAntiCheatCheck"/>; none found: the stamp read before it is kept as the game's clean one.</summary>
    AntiCheat FullCheck(Game g)
    {
        var (gen, folders) = (InstallGen(g), FolderStamp(g));
        var found = FullAntiCheatCheck(g);
        if (found != AntiCheat.None || folders == null) return found;
        lock (_scanLock)
            if (_scan?.GetValueOrDefault(g.Id) is { AntiCheat: AntiCheat.None } ev && !_verdicts.ContainsKey(g.Id) && ev.Clean != folders
                && InstallGen(g) == gen)   // a change or a disarm during the walk: not known clean
            {
                _scan[g.Id] = ev with { Clean = folders };
                try { Store.SaveScan(_scan); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{g.Name}: couldn't save the scan cache: {e.Message}"); }
            }
        return found;
    }

    /// <summary>The game is no longer known clean: the next scan, reconcile or pass walks its install in full. In memory at
    /// once; saved with <paramref name="save"/>.</summary>
    void NotClean(Game g, bool save = true)
    {
        lock (_scanLock)
        {
            if ((_scan ??= Store.LoadScan()).GetValueOrDefault(g.Id) is not { Clean: not null } ev) return;
            _scan[g.Id] = ev with { Clean = null };
            if (save)
                try { Store.SaveScan(_scan); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{g.Name}: couldn't save the scan cache: {e.Message}"); }
        }
    }

    /// <summary>The game's last full anti-cheat check found none, with its folders as <paramref name="folders"/>, and nothing
    /// was found since.</summary>
    bool CleanSince(Game g, string? folders)
    {
        lock (_scanLock)
            return folders != null && _scan?.GetValueOrDefault(g.Id) is { AntiCheat: AntiCheat.None } ev && ev.Clean == folders && !_verdicts.ContainsKey(g.Id);
    }

    /// <summary>Runs <paramref name="f"/> on this thread in background mode: low I/O and memory priority, so a scan never
    /// competes with a game or the desktop for the disk.</summary>
    static T LowIo<T>(Func<T> f)
    {
        var on = SetThreadPriority(GetCurrentThread(), 0x00010000);   // THREAD_MODE_BACKGROUND_BEGIN; fails when already in it
        try { return f(); }
        finally { if (on) SetThreadPriority(GetCurrentThread(), 0x00020000); }
    }

    /// <summary>Runs <paramref name="f"/> on this thread at below-normal priority when <paramref name="low"/>: no await
    /// inside, so the pool thread is back at its own priority before it runs anything else.</summary>
    static T Lowered<T>(bool low, Func<T> f)
    {
        if (!low) return f();
        var was = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        try { return f(); }
        finally { Thread.CurrentThread.Priority = was; }
    }

    [DllImport("kernel32.dll")] static extern bool SetThreadPriority(nint thread, int priority);
    [DllImport("kernel32.dll")] static extern nint GetCurrentThread();

    string IndexStamp(Game g)
    {
        try { return _reader.IndexStamp(g); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "?"; }
    }

    string DetectStamp(Game g, EngineInfo? engine)
    {
        try { return "|" + _reader.DetectStamp(g, engine); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "|?"; }
    }

    /// <summary>The recorder is in (or on its way out of) the game, by its record or on disk, or the engine allows it
    /// (<see cref="RecorderSkip"/>): its retention and eligibility need the full anti-cheat check.</summary>
    static bool RecorderMayGoIn(Game g, GameRecord rec, EngineInfo? engine) =>
        rec.RecorderFiles.Count > 0 || rec.RecorderChained != null || rec.RecorderExe != null || rec.RecorderMoveFrom != null
        || engine is { Unsupported: null } && engine.GraphicsApi.Contains("D3D12") || ProxyOnDisk(g) || ProxyOnDisk(RecordedAt(g, rec));

    /// <summary>Our proxy next to the exe; a d3d12.dll that can't be read counts as ours.</summary>
    static bool ProxyOnDisk(Game g)
    {
        try { return IsOurProxy(Path.Combine(Path.GetDirectoryName(g.ExePath)!, "d3d12.dll")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <param name="pending"><see cref="PendingOf"/>, when the caller has it</param>
    /// <param name="gpu">the evaluation's snapshot, so its reason and driver verdict agree; null = now</param>
    string? StaleReason(Game g, GameRecord r, Pending? pending = null, GpuSnapshot? gpu = null)
    {
        var p = pending ?? PendingOf(g, r);
        return WarmChanged(g, r, gpu)
            ?? (p.Unknown ? (p.Recorded + (p.Planned ?? 0) is > 0 and var u ? $"{u:N0} pipeline{(u == 1 ? "" : "s")} to compile again" : "compile again")
                + ": what the last compile replayed is no longer known" : null)
            ?? (r.WarmedPlanVersion != Planner.Version ? PlannerChanged(r, p.Planned)
                : Settings.MaximumPlans && r.WarmedPerStage ? "Maximum mode: every shader pairing is still to compile" : null)   // Maximum -> standard: nothing new
            ?? (p.Recorded + (r.WarmedPlanVersion == Planner.Version ? p.Planned ?? 0 : 0) is > 0 and var n
                ? $"{n:N0} new {NewKind(n, p)}; compile again to include them" : null);
    }

    /// <summary>What <paramref name="n"/> new pipelines are: all recorded, all from upscaler packs, or a mix.</summary>
    static string NewKind(long n, Pending p) => (n == 1 ? "pipeline" : "pipelines") is var w && n == p.Upscaler ? "upscaler " + w
        : n == p.Recorded - p.Upscaler ? w + " recorded" : p.Upscaler > 0 ? $"{w} ({p.Upscaler:N0} upscaler)" : w;

    /// <summary>A newer planner than the warmed plan's. Its rebuild (RunItem) counts the pipelines the warm didn't compile;
    /// none marks the warm current then, so a count here is more than 0. Not rebuilt yet, or not countable (no readable
    /// warmed plan): it may compile more.</summary>
    static string? PlannerChanged(GameRecord r, long? planned) =>
        r.PlanVersion == Planner.Version && planned is { } n ? n > 0 ? $"SCSKiller can now compile {n:N0} more pipeline{(n == 1 ? "" : "s")} for this game" : null
        : "SCSKiller can now compile more of this game";

    /// <summary>Why the warm no longer matches what the game uses, whatever the plan: driver, exe name, game build, shaders.</summary>
    string? WarmChanged(Game g, GameRecord r, GpuSnapshot? snapshot = null) =>
        (snapshot ?? Snapshot()) is var gpu && !CurrentDriver(gpu, r.WarmedDriverId, r.WarmedDriverVersion) ? $"driver changed: {r.WarmedDriverVersion} -> {gpu.Gpu.DriverVersion}"
        : TrimmedReason(r) is { } trimmed ? trimmed
        : WarmMissesGame(r) ? MissesGameReason
        : r.AgsMissed && r.WarmedAgsApp != null ? AgsMissedReason(r)
        : LayerNow(g) is var layer && layer != r.WarmedLayer ? (layer == null ? "the HDR mod the last compile ran through is gone" : r.WarmedLayer == null ? NotThroughLayerReason : "the HDR mod changed since the compile")
        : r.WarmedExeName is { } other && !other.Equals(Path.GetFileName(g.ExePath), StringComparison.OrdinalIgnoreCase)
            ? $"the last compile filled the cache of {other}; the game runs {Path.GetFileName(g.ExePath)}"
        : WarmExeName(g, r) is var runs && (r.WarmedExeName ?? Path.GetFileName(g.ExePath)) is var warmed && runs != warmed && CaseMatters(g, r, warmed)
            ? $"the game runs as {runs}, the warm filled the cache of {warmed} (the driver keys it on the exe name's exact case)"
        : IndexStamp(g) != (r.WarmedIndexStamp ?? "") ? IndexChangedReason
        : g.Version != null && r.WarmedGameVersion != null ? (g.Version != r.WarmedGameVersion ? $"game updated since the warm (build {r.WarmedGameVersion} -> {g.Version})" : IndexChanged(r))
        : r.WarmedExeStamp != ExeStamp(g) ? "game updated since the warm"   // no store version (older state, other stores): the exe
        : IndexChanged(r);

    /// <summary>Warmed, stale only because an older planner built its plan, and not rebuilt since (<see cref="CheckPlans"/>);
    /// or a recording waits for the plan to tell its ray tracing (<see cref="RtPlanCheck"/>).</summary>
    bool NeedsPlanCheck(Game g, GameRecord r, EngineInfo? e) => OlderPlanner(g, r) || RtPlanCheck(r, e);

    bool OlderPlanner(Game g, GameRecord r) => r.WarmedAt != null && r.WarmedPlanVersion != Planner.Version && r.PlanVersion != Planner.Version && WarmChanged(g, r) == null;

    /// <summary>A "when idle" plan rebuild without a warm, unless the game is in the queue already (a failed or stopped
    /// item too: not retried on every scan). <paramref name="prompt"/>: an older planner's plan (an app update), rebuilt
    /// without waiting for idle, so its page and count are the new planner's soon after the update.</summary>
    void CheckPlan(string gameId, bool prompt = false)
    {
        lock (_lock)
            if (_queue.Any(q => q.GameId == gameId && q.Stage != QueueStage.Done)) return;
        Add(gameId, whenIdle: true, planCheck: true, prompt);
    }

    /// <summary>The plan checks (<see cref="QueueItem.PlanCheck"/>) still to run or running, as the one line the queue
    /// shows for them; null = none.</summary>
    public static string? PlanCheckLine(IEnumerable<QueueItem> queue) =>
        queue.Where(q => q.PlanCheck && !Finished(q.Stage)).ToList() is { Count: > 0 and var n } checks
            ? $"Checking {n} game{(n == 1 ? "" : "s")} for more to compile" + (checks.All(q => q.Note == WhenIdleNote) ? " (while idle)" : "") : null;

    /// <summary>The keys (sha1 of tag + payload) of what plan.bin has the warm create: every record but the 'B' root
    /// signature blobs, which compile nothing by themselves (an item's key covers its root signature's hash). Null when it
    /// isn't a readable plan.</summary>
    static HashSet<string>? PlanKeys(string path)
    {
        try { return PlanFile.Read(path).Records.Where(r => r.Tag != 'B').Select(r => r.Key).ToHashSet(); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>What a plan compiles, independent of record order and of its header (paths, times): a hash of its sorted
    /// <see cref="PlanKeys"/>.</summary>
    static string PlanFingerprint(IEnumerable<string> keys) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(string.Concat(keys.Order(StringComparer.Ordinal)))));

    /// <summary>The index's shader maps, independent of their order. The ContentHash leaves some out (a shipped pipeline
    /// cache's, an inline shader's bytes) because the community database finds a build by it; a plan reads them all.</summary>
    static string MapsFingerprint(ShaderIndex index) =>
        PlanFingerprint(index.Maps.Select(m => Convert.ToHexStringLower(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{m.Hash}|{m.Library}|{m.Platform}|{m.IsPipeline}|{string.Join(',', m.Shaders)}")))));

    static string? IndexChanged(GameRecord r) => r.IndexContentHash != r.WarmedIndexHash ? IndexChangedReason : null;

    const string IndexChangedReason = "game shaders changed since the warm";

    /// <summary>A warm without a layer (also every warm from before compiles ran through one) and a layer now.</summary>
    internal const string NotThroughLayerReason = "the last compile didn't run through the game's HDR mod";

    /// <summary>NVIDIA's DXCache key ignores the exe name's case (measured: same files in upper case); AMD's DxcCache key is
    /// case-sensitive (FNV-1a of the UTF-16 name as launched). Other vendors: unmeasured, assume it matters.</summary>
    bool CaseSensitiveCache => Vendor.Vendor != GpuVendor.Nvidia;

    /// <summary>Whether warming this game under another case of its exe name fills another cache. Only for a name-hashed
    /// key: on AMD an app profile (FF7 Rebirth, Cyberpunk, Elden Ring...) gives every case of the name one fixed key, which
    /// shows as learned D3D12 keys without the warmed name's hash (<see cref="AmdAppCache.IsNameHashed"/>), and a game that
    /// registers an AGS app name gets that name's key. Nothing learned yet: assume it matters.</summary>
    bool CaseMatters(Game g, GameRecord r, string warmedExeName) =>
        CaseSensitiveCache && r.WarmedAgsApp == null && AmdAppCache.IsNameHashed(r.CacheKeys, warmedExeName) != false;

    /// <summary>The exe file name a warm of this game stages: the name its process was seen launched with (the recorder's
    /// #session marker, the running game's module path), else the install's file name. They differ at most in case.</summary>
    public static string WarmExeName(Game g, GameRecord r) =>
        r.LaunchedExeName is { } n && n.Equals(Path.GetFileName(g.ExePath), StringComparison.OrdinalIgnoreCase) ? n : Path.GetFileName(g.ExePath);

    /// <summary>Takes the latest sighting of how the game's process was launched (the recorder's #session marker, or a
    /// running process noted by Running) into the record; only names equal to the install's apart from case count. A new
    /// name drops a stopped warm's resume point: what it replayed went into the other name's cache. True if changed.</summary>
    bool MergeLaunched(Game g, GameRecord rec, LaunchedExe? marker)
    {
        var disk = Path.GetFileName(g.ExePath);
        var before = WarmExeName(g, rec);
        bool changed = false;
        foreach (var seen in new[] { marker, _launched.GetValueOrDefault(disk) })
        {
            if (seen == null || !seen.Name.Equals(disk, StringComparison.OrdinalIgnoreCase) || seen.At <= rec.LaunchedExeSeenAt) continue;
            (rec.LaunchedExeName, rec.LaunchedExeSeenAt, changed) = (seen.Name, seen.At, true);
        }
        if (WarmExeName(g, rec) != before && CaseMatters(g, rec, before)) rec.ResumeAt = 0;
        return changed;
    }

    /// <summary>Learns the driver-cache keys running games' own processes hold open (the same attribution as a warm's, see
    /// RunItem): on AMD the key can come from an app profile rather than the exe name, so the files the game really
    /// uses are the only authority. Returns the ids of games that got a new key (saved).</summary>
    HashSet<string> LearnKeysOfRunning(IReadOnlySet<string> running)
    {
        var learned = new HashSet<string>();
        if (AppCache == null || running.Count == 0) return learned;
        foreach (var s in Games)
        {
            var exe = Path.GetFileName(s.Game.ExePath);
            if (!running.Contains(exe) || s.AntiCheat != AntiCheat.None) continue;   // nothing near an anti-cheat game while it runs
            var rec = Store.LoadGame(s.Game.Id);
            if (LearnKeys(s.Game, rec, WarmExeName(s.Game, rec)) is var added && added.Count == 0) continue;
            Store.SaveGame(s.Game.Id, rec);
            learned.Add(s.Game.Id);
        }
        return learned;
    }

    /// <summary>Adds the keys the game's own process (named <paramref name="exe"/>) holds open to the game's record (not
    /// saved); returns the new ones and logs when the D3D12 key isn't the name's hash (an AMD app profile).</summary>
    IReadOnlySet<string> LearnKeys(Game g, GameRecord rec, string exe) => LearnKeys(g, rec, exe, OpenKeys(g, exe));

    IReadOnlySet<string> LearnKeys(Game g, GameRecord rec, string exe, IReadOnlySet<string> open)
    {
        var added = open.Where(k => !rec.GameKeys.Contains(k)).ToHashSet();
        NoteProfile(g, exe, added.Where(k => !rec.CacheKeys.Contains(k)).ToList());
        rec.CacheKeys.UnionWith(added);
        rec.GameKeys.UnionWith(added);
        return added;
    }

    IReadOnlySet<string> OpenKeys(Game g, string exe)
    {
        try { return AppCache?.KeysOpenBy(exe) ?? new HashSet<string>(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{g.Name}: cache attribution: {e.Message}"); return new HashSet<string>(); }
    }

    /// <summary>Samples the keys a game holds open while it's played, until it has some open: a warm running under another
    /// identity than the game's (an Xbox game on NVIDIA) fills other keys, so only the game's own process tells its cache.</summary>
    async Task LearnWhilePlaying(Game g)
    {
        while (IsPlaying(g.Id))
        {
            var exe = WarmExeName(g, Store.LoadGame(g.Id));
            if (OpenKeys(g, exe) is { Count: > 0 } open)
            {
                var rec = Store.LoadGame(g.Id);
                if (LearnKeys(g, rec, exe, open).Count > 0) { Store.SaveGame(g.Id, rec); Refresh(g); }
                return;
            }
            await Task.Delay(AttributionInterval);
        }
    }

    /// <summary>The game's own keys are known and the last complete warm filled none of them: the warm went to another cache.</summary>
    public static bool WarmMissesGame(GameRecord r) =>
        r.GameKeys.Count > 0 && (r.WarmedKeys ?? r.CacheKeys.Except(r.GameKeys).ToHashSet()) is { Count: > 0 } warmed && !warmed.Overlaps(r.GameKeys);

    /// <summary>A warm under an AGS app name whose game's own keys aren't known, and the game's first launch after it still
    /// compiled most of its pipelines (<see cref="IsPartlyWarmed(LaunchCheck?)"/>): taken as a miss, so the next warm is plain.</summary>
    public static bool AgsLaunchMissed(GameRecord r) =>
        r.WarmedAgsApp != null && !r.GameKeys.Any(k => k.StartsWith(AmdAppCache.D3D12Prefix, StringComparison.Ordinal)) && IsPartlyWarmed(r.FirstLaunch);

    static string AgsMissedReason(GameRecord r) =>
        $"the compile didn't reach this game's cache (it registered the AGS app name {r.WarmedAgsApp}"
        + (r.FirstLaunch is { } l ? $"; the first launch still compiled {l.Compiled * 100:0}% of its pipelines" : "")
        + "): the next compile runs without it";

    public const string MissesGameReason = "the compile didn't reach this game's cache: the game uses another driver-cache key";
    public const string UnreachedReason = "the compile didn't help this game on NVIDIA, it needs a custom loader";
    public const string ClearForGameNote = "its compiled cache isn't used, clear it to free the space";
    public const string CacheFileFullNote = "its NVIDIA cache file is nearly full: clear its cache to give the game room";

    /// <summary>Hits stay under it: a cache hit under a 70-thread precompile took 5 ms at the median (graphics 0.6 ms). A
    /// warm the game's lookups miss: 42-140 ms.</summary>
    public const double UnreachedMedianMs = 20;

    /// <summary>NVIDIA: the game's first launch after the warm still compiled what the warm compiled
    /// (<see cref="WarmedCreates(Game, GameRecord, LaunchCheck, string, IReadOnlySet{string}?, GpuSnapshot)"/>).</summary>
    public static bool Unreached((int N, double MedianMs) warmed) => warmed.N >= MinJudgedCreates && warmed.MedianMs >= UnreachedMedianMs;

    /// <summary>The verdict is about the warm it judged (<see cref="GameRecord.UnreachedWarm"/>): a later complete warm ends it,
    /// Clear cache doesn't.</summary>
    public static bool IsUnreached(GameRecord r) => r.UnreachedWarm is { } judged && (r.WarmedAt == null || r.WarmedAt <= judged);

    /// <summary>The warmed pipelines <paramref name="first"/> created and their median create time; null when the warm
    /// can't be judged: the game is Stale (<see cref="StaleReason"/>: a new driver compiles everything again, the warm
    /// missed the game's key, or there is more to compile), a file of its keys was made after it (a cache reset the game
    /// refilled), or its inputs aren't known.</summary>
    (int N, double MedianMs)? WarmedCreates(Game g, GameRecord rec, LaunchCheck first, string csv, IReadOnlySet<string>? rayQuery, GpuSnapshot gpu)
    {
        if (rec.WarmKeysFile is not { } wf || StaleReason(g, rec, gpu: gpu) != null
            || rec.WarmedAt is not { } at || AppCache?.FilesOf(rec.WarmedKeys ?? rec.CacheKeys).Any(f => f.CreationTimeUtc > at.UtcDateTime) == true) return null;
        try
        {
            if (KeyFiles.Set(Path.Combine(Store.GameDir(g.Id), wf)) is not { } baseline) return null;
            var warmed = WarmedRecords(InputsNow(g, rec), baseline, CrashInputs(CrashKeysNow(rec), rec.Plan?.FilePath), rayQuery);
            return WarmedCreates(SessionLog.Launches(csv), first.At.ToUnixTimeMilliseconds(), warmed, rayQuery);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    /// <summary>The record keys whose every input the warm took (<paramref name="baseline"/>, its key file): on NVIDIA a
    /// recorded pipeline is its stages, so it counts once the warm compiled all of them. Crash inputs never count, nor a stage
    /// a RayQuery PSO has (<paramref name="rayQuery"/>): a pipeline recorded since with that shader is one too, at the floor.</summary>
    internal static HashSet<string> WarmedRecords(IEnumerable<string> inputs, IReadOnlySet<string> baseline, IReadOnlySet<string> crash,
        IReadOnlySet<string>? rayQuery = null)
    {
        var all = new Dictionary<string, bool>();
        foreach (var i in inputs)
        {
            var records = WarmInputs.Records(i).ToList();
            bool taken = WarmInputs.Taken(baseline, i) && !crash.Contains(WarmInputs.Key(i)) && !records.Any(r => crash.Contains(r) || rayQuery?.Contains(r) == true);
            foreach (var r in records) all[r] = all.GetValueOrDefault(r, true) && taken;
        }
        return [.. all.Where(x => x.Value).Select(x => x.Key)];
    }

    /// <summary>Of the launch that started at <paramref name="start"/>: the first create of each warmed pipeline no earlier
    /// launch in the csv created (the game's own cache may hold those), and their median. Library loads, ray tracing state
    /// objects (a cached one takes 6-23 ms) and RayQuery PSOs (their floor reaches 88 ms) are left out.</summary>
    internal static (int N, double MedianMs) WarmedCreates(IEnumerable<SessionLog.CsvLaunch> launches, long start, IReadOnlySet<string> warmed,
        IReadOnlySet<string>? rayQuery)
    {
        var before = new HashSet<string>();
        foreach (var l in launches)
        {
            if (l.Start == start)
            {
                var ms = l.Creates.Where(c => c.Key != null && char.IsUpper(c.Kind) && c.Kind is not ('R' or 'A')).DistinctBy(c => c.Key)
                    .Where(c => warmed.Contains(c.Key!) && !before.Contains(c.Key!) && rayQuery?.Contains(c.Key!) != true).Select(c => c.Ms).Order().ToList();
                return (ms.Count, ms.Count > 0 ? ms[ms.Count / 2] : 0);
            }
            before.UnionWith(l.Creates.Select(c => c.Key).OfType<string>());
        }
        return (0, 0);
    }

    public const string TrimmedPartReason = "part of this game's shader cache was removed by the driver's size limit; compile again",
        TrimmedAllReason = "this game's shader cache was removed (by the driver's size limit or a shader cache reset); compile again";

    /// <summary>AMD: a file the last complete warm left in DxcCache is gone (the driver trims least recently used files past
    /// its cap, a reset removes them all). Elsewhere a key's files have no per-warm names: only all of them gone shows.</summary>
    string? TrimmedReason(GameRecord r) => AppCache switch
    {
        AmdAppCache amd => r.WarmedFiles is { Count: > 0 } files && amd.Missing(files) is { Count: > 0 } gone
            ? gone.Count == files.Count ? TrimmedAllReason : TrimmedPartReason : null,
        { } cache => r.WarmedKeys is { Count: > 0 } keys && cache.FilesOf(keys).Count == 0 ? TrimmedAllReason : null,
        null => null,
    };

    void NoteProfile(Game g, string exe, IEnumerable<string> keys)
    {
        if (AmdAppCache.IsNameHashed(keys, exe) == false)
            Log?.Report($"{g.Name}: the driver keys {exe}'s cache as {string.Join(", ", keys.Where(k => k.StartsWith(AmdAppCache.D3D12Prefix)))}, " +
                        $"not its name hash {AmdAppCache.DxcKey(exe)} " +
                        (Ags(g) is { } a && keys.Contains(AgsKey(g)) ? $"(its AGS app name {a.App}" : "(an app profile") + ": the exe name's case doesn't matter)");
    }

    /// <summary>On AMD, what the game probably registers with AGS (<see cref="AmdAgs.Of"/>); a hint, see <see cref="AgsFor"/>.</summary>
    AgsRegistration? Ags(Game g)
    {
        if (Vendor.Vendor != GpuVendor.Amd) return null;
        EngineInfo? engine;
        lock (_scanLock) engine = _scan?.GetValueOrDefault(g.Id)?.Engine;   // the scan's cache: Games may not hold the game yet
        return AmdAgs.Of(g, engine);
    }

    /// <summary>The AGS app-name key expected for the game (<see cref="Ags"/>), null when it doesn't apply.</summary>
    public string? AgsKey(string gameId) => AgsKey(Find(gameId).Game);

    string? AgsKey(Game g) => Ags(g) is { } a ? AmdAppCache.AgsKey(Path.GetFileName(g.ExePath), a.App) : null;

    /// <summary>The registration a warm of this game uses, only where it is proven to reach the game's cache: the game's own
    /// process was seen holding the AGS key and not its plain key, or, before the game is seen, the app name is a measured
    /// one (<see cref="AmdAppCache.ProvenAgsApp"/>), its exe was read and no launch showed a miss. Else null: a plain device,
    /// as without AGS.</summary>
    AgsRegistration? AgsFor(Game g, GameRecord r) => AgsFor(Ags(g), WarmExeName(g, r), r);

    public static AgsRegistration? AgsFor(AgsRegistration? a, string warmExeName, GameRecord r)
    {
        if (a == null) return null;
        var seen = r.GameKeys.Where(k => k.StartsWith(AmdAppCache.D3D12Prefix, StringComparison.Ordinal)).ToList();
        if (seen.Count > 0)
            return seen.Contains(AmdAppCache.AgsKey(warmExeName, a.App)) && !seen.Contains(AmdAppCache.HintKey(warmExeName)) ? a : null;
        return !a.ExeUnread && !r.AgsMissed && AmdAppCache.ProvenAgsApp(a.App) ? a : null;
    }

    /// <summary>The AGS registration the next warm of this game uses (<see cref="AgsFor"/>); null = a plain device.</summary>
    public AgsRegistration? WarmAgs(string gameId) => AgsFor(Find(gameId).Game, Store.LoadGame(gameId));

    static string ExeStamp(Game g) => new FileInfo(g.ExePath) is { Exists: true } f ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "";

    public IReadOnlyList<GameState> StaleGames() => Games.Where(s => s.Status == GameStatus.Stale).ToList();
    public IReadOnlyList<GameState> DriverStaleGames()
    {
        lock (_lock) return _games.Where(s => s.Status == GameStatus.Stale && _driverStale.GetValueOrDefault(s.Game.Id) && s.NoStutter == null).ToList();
    }

    // What the user skipped: the driver and the game build each stale game was offered for.
    string StaleKey(GameState s) => $"{DriverId}|{s.Game.Version ?? ExeStamp(s.Game)}";

    public const string DriverPart = "Driver cache", WindowsPart = "Windows shader cache", PipelinePart = "Game's pipeline cache",
        PrecachePart = "Game's shader precache";

    /// <summary><paramref name="gamePrecache"/>: also what the game writes itself and rebuilds at its next start (Unreal's user
    /// pipeline cache, *.ushaderprecache files): <see cref="PipelinePart"/>, <see cref="PrecachePart"/>.</summary>
    public IReadOnlyList<CachePart> GameCaches(string gameId, bool gamePrecache = false)
    {
        var s = Find(gameId);
        var rec = Store.LoadGame(gameId);
        var parts = new List<CachePart>();
        if (AppCache != null && DriverKeys(s.Game, rec).Keys is { Count: > 0 } keys) parts.Add(Part(DriverPart, AppCache.FilesOf(keys)));
        if (s.AntiCheat != AntiCheat.None) return parts;   // anti-cheat: nothing near the game, only the driver's cache
        parts.Add(Part(WindowsPart, D3DSCache.FoldersOf(Path.Combine(LocalAppData, "D3DSCache"), s.Game).SelectMany(D3DSCache.CacheFiles)));
        if (!gamePrecache) return parts.Where(p => p.Files.Count > 0).ToList();
        // two games with one project name share the file name: whose it is is unknown, skipped
        if (UnrealUserCache.Project(s.Game.ExePath) is { } project
            && !Games.Any(o => o.Game.Id != gameId && string.Equals(UnrealUserCache.Project(o.Game.ExePath), project, StringComparison.OrdinalIgnoreCase)))
            parts.Add(Part(PipelinePart, UnrealUserCache.FilesOf(s.Game, SavedRoots(s.Game)).Select(f => new FileInfo(f))));
        var others = Games.Where(o => o.Game.Id != gameId).SelectMany(o => UnrealUserCache.Folders(o.Game)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        parts.Add(Part(PrecachePart, UnrealUserCache.PrecacheFilesOf(s.Game, [.. SavedRoots(s.Game), ProgramData],
            UnrealUserCache.Folders(s.Game).Where(n => !others.Contains(n))).Select(f => new FileInfo(f))));
        return parts.Where(p => p.Files.Count > 0).ToList();
    }

    static CachePart Part(string name, IEnumerable<FileInfo> files)
    {
        var list = files.ToList();
        return new(name, list.Select(f => f.FullName).ToList(), list.Sum(f => f.Length));
    }

    /// <summary>An Xbox game's Saved may also be in its package's own %LOCALAPPDATA%.</summary>
    string[] SavedRoots(Game g) => g.Store == Core.Store.Xbox && g.Id.StartsWith("xbox:", StringComparison.Ordinal)
        ? [LocalAppData, MyGames, Path.Combine(LocalAppData, "Packages", AppStore.Segment(g.Id["xbox:".Length..]), "LocalCache", "Local")]
        : [LocalAppData, MyGames];

    /// <summary>True with driver keys attributed even if nothing is left on disk. Refuses while a process named like the exe
    /// runs (the game or another SCSKiller's warm: names only, none opened), and when another game shares a driver key
    /// (<see cref="SharedWith"/>: an AMD app profile gives several exes one key): the delete would silently cold it.</summary>
    public bool ClearGameCache(string gameId, bool gamePrecache = false)
    {
        var s = Find(gameId);
        var rec = Store.LoadGame(gameId);
        var exe = Path.GetFileName(s.Game.ExePath);
        lock (_lock)
            if (_current == gameId) throw new InvalidOperationException($"a compile of {s.Game.Name} is in progress");
        if (GameRunning(s.Game)) throw new InvalidOperationException($"{s.Game.Name} is running");
        var keys = DriverKeys(s.Game, rec).Keys;
        var driver = AppCache != null && keys.Count > 0;
        if (driver && SharedWith(gameId, keys) is { Count: > 0 } shared)
            throw new InvalidOperationException($"{s.Game.Name}'s driver cache is shared with " +
                string.Join(", ", shared.Select(x => $"{x.Name} ({string.Join(", ", x.Keys)})")) +
                ": the driver gives their exes the same key (an app profile, or the same exe name), so clearing it would clear theirs too");
        var parts = GameCaches(gameId, gamePrecache);
        if (!driver && parts.Count == 0) return false;
        AppCacheFiles.DeleteAll(parts.SelectMany(p => p.Files).Select(f => new FileInfo(f)).ToList());   // throws "files in use by <process>"
        foreach (var dir in parts.Where(p => p.Name == WindowsPart).SelectMany(p => p.Files).Select(Path.GetDirectoryName).Distinct())
            try { Directory.Delete(dir!); }
            catch (IOException) { }   // not empty: a file appeared meanwhile, left alone
        if (!driver) return true;
        // the keys stay: they are this exe name's, so the cache the game builds by itself still counts in CacheOnDisk
        if (!IsUnreached(rec)) rec.UnreachedWarm = null;   // a verdict on an older warm: with WarmedAt gone it would apply again
        (rec.WarmedAt, rec.WarmedDriverVersion, rec.WarmedDriverId, rec.LastWarmTime, rec.LastCacheGrowthBytes, rec.ResumeAt) = (null, null, null, null, null, 0);
        (rec.LastWarmFailed, rec.LastWarmSkipped, rec.LastWarmNeedsRecording, rec.LastWarmCrashed) = (null, null, null, null);
        (rec.WarmedCareful, rec.FirstLaunch, rec.WarmedFiles) = (false, null, null);
        Store.SaveGame(gameId, rec);
        Refresh(s.Game);
        return true;
    }

    public void SetCarefulCompile(string gameId, bool on)
    {
        var s = Find(gameId);
        if (CarefulThreads(Vendor.Vendor) == null) throw new InvalidOperationException($"the careful compile is for AMD drivers, not {Vendor.Gpu.Name}");
        lock (_lock)
            if (_current == gameId) throw new InvalidOperationException($"a compile of {s.Game.Name} is in progress");
        var rec = Store.LoadGame(gameId);
        if (rec.Careful == on) return;
        (rec.Careful, rec.ResumeAt) = (on, 0);   // a stopped warm's resume point counts the other schedule's items
        Store.SaveGame(gameId, rec);
        Refresh(s.Game);
    }

    /// <summary>Other discovered games whose driver cache is (or would be) the same files as this game's: keys learned for
    /// both, or a D3D12 key that the other game's exe name maps to (<see cref="AmdAppCache.HintKey"/>: a known AMD app
    /// profile such as every "ff7rebirth*" exe sharing 6b2fcd83, or the same exact name), even before it was warmed.</summary>
    List<(string Name, List<string> Keys)> SharedWith(string gameId, IReadOnlySet<string> keys)
    {
        var shared = new List<(string, List<string>)>();
        var exe = Path.GetFileName(Find(gameId).Game.ExePath);
        foreach (var o in Games)
        {
            if (o.Game.Id == gameId) continue;
            var other = Store.LoadGame(o.Game.Id);
            var hint = AmdAppCache.HintKey(WarmExeName(o.Game, other));
            var ags = AgsFor(o.Game, other) != null ? AgsKey(o.Game) : null;
            // NVIDIA keys every file by the exe name, whatever its case
            var sameName = AppCache is not AmdAppCache && Path.GetFileName(o.Game.ExePath).Equals(exe, StringComparison.OrdinalIgnoreCase);
            var both = keys.Where(k => sameName || other.CacheKeys.Contains(k) || k == hint || k == ags).Order().ToList();
            if (both.Count > 0) shared.Add((o.Game.Name, both));
        }
        return shared;
    }

    /// <summary>The driver-cache keys Clear cache covers: the learned ones, and on AMD, unless a learned D3D12 key shows an
    /// app profile, the name hash of each case the exe was seen under that has files (the key of a name without a
    /// profile). Gap: which of the game's driver cache may be left, null when none.</summary>
    (HashSet<string> Keys, string? Gap) DriverKeys(Game g, GameRecord rec)
    {
        var keys = rec.CacheKeys.ToHashSet();
        var exe = Path.GetFileName(g.ExePath);
        if (AppCache is not AmdAppCache amd)
            return (keys, AppCache == null || keys.Count > 0 ? null
                : $"the driver cache key of {exe} isn't learned yet (SCSKiller learns it when it compiles the game or sees it running): the driver cache is not cleared");
        var names = new[] { exe, rec.LaunchedExeName, rec.WarmedExeName }.OfType<string>().Distinct().ToList();
        var dxc = keys.Where(k => k.StartsWith(AmdAppCache.D3D12Prefix, StringComparison.Ordinal)).ToList();
        // the AGS app name's key is only a hint: cleared once a process is seen holding it, like any key
        var ags = AgsFor(g, rec) is { } a && AgsKey(g) is { } ak && !keys.Contains(ak) && (dxc.Count == 0 || amd.FilesOf([ak]).Count > 0)
            ? $"the D3D12 key {ak} that {g.Name} gets from its AGS app name {a.App} isn't learned yet (SCSKiller learns it when it compiles the game or sees it running): it is not cleared"
            : null;
        if (dxc.Count > 0 && !names.Any(n => dxc.Contains(AmdAppCache.DxcKey(n)))) return (keys, ags);
        keys.UnionWith(names.Select(AmdAppCache.HintKey).Where(k => amd.FilesOf([k]).Count > 0));
        return (keys, dxc.Count > 0 || ags != null ? ags
            : $"the D3D12 driver cache key of {exe} isn't learned yet: clearing takes the key the driver gives that exe name; a driver app profile's key or a D3D11 key would be missed");
    }

    /// <summary>Which of the game's driver cache <see cref="ClearGameCache"/> may miss (a key not learned yet); null = none.</summary>
    public string? DriverCacheGap(string gameId) => DriverKeys(Find(gameId).Game, Store.LoadGame(gameId)).Gap;

    public bool SetEncryptionKey(string gameId, string key)
    {
        var game = Games.FirstOrDefault(s => s.Game.Id == gameId)?.Game ?? throw new ArgumentException($"unknown game {gameId}");
        return (_reader as UnrealReader ?? (_reader as EngineReaders)?.Get<UnrealReader>()) is { } u && u.SetKey(game, key);
    }

    public void DismissStale() => Store.SaveDismissed(StaleGames().ToDictionary(s => s.Game.Id, StaleKey));

    /// <summary>The driver-update notification decision: Ask mode and a driver-stale game that wasn't skipped (DismissStale)
    /// for this driver and game build. StaleGames() keeps listing skipped games for the UI.</summary>
    public bool ShouldNotifyStale()
    {
        if (Settings.OnDriverUpdate != DriverUpdateMode.Ask) return false;
        var skipped = Store.LoadDismissed();
        return DriverStaleGames().Any(s => !skipped.TryGetValue(s.Game.Id, out var k) || k != StaleKey(s));
    }

    /// <summary>Registers the logon/idle re-warm task (it runs the published CLI) for Ask and WhenIdle, removes it for Off.
    /// For the Settings page, after the user changes OnDriverUpdate.</summary>
    public void ApplyDriverUpdateMode()
    {
        if (Settings.OnDriverUpdate == DriverUpdateMode.Off) ScheduledTask.Unregister();
        else ScheduledTask.Register(ScheduledTask.TaskExe() ?? throw new FileNotFoundException(@"the command-line tool (cli\scskiller.exe) is not next to the app"));
    }

    void Refresh(Game g, CancellationToken ct = default, bool force = false)
    {
        var ticket = Ticket();
        var s = Evaluate(g, force, out _, out var driverStale, ct);
        lock (_lock)
        {
            if (_evaluatedAt.GetValueOrDefault(g.Id) > ticket || _removed.Contains(g.Id) || StaleCopy(g)) return;   // an evaluation started later is in place
            (_evaluatedAt[g.Id], _driverStale[g.Id]) = (ticket, driverStale);
            s = WithVerdict(s);
            var i = _games.FindIndex(x => x.Game.Id == g.Id);
            if (i >= 0) _games[i] = s; else _games.Add(s);
        }
        GameChanged?.Invoke(s);
        // a game's exit imports its recording: its plan check runs without waiting for a scan
        if (CheckPlans && s is { Engine: not null, Status: not GameStatus.Unsupported, RtToPlan: true }) CheckPlan(g.Id);
    }

    // game id -> the last anti-cheat verdict AntiCheatFound recorded, until a full evaluation finds none
    readonly ConcurrentDictionary<string, AntiCheat> _verdicts = new();

    /// <summary>An evaluation that read the game before a verdict was recorded never shows it clean.</summary>
    GameState WithVerdict(GameState s) =>
        s.AntiCheat == AntiCheat.None && _verdicts.TryGetValue(s.Game.Id, out var v) ? s with { AntiCheat = v } : s;

    // Evaluations overlap (a scan, a game's exit, a compile's end): each takes a ticket before it reads anything, and a
    // state is stored only over one from an older ticket, so a slow one never replaces what a later one read.
    long _tickets;
    readonly Dictionary<string, long> _evaluatedAt = [];   // game id -> ticket of its state in _games; under _lock

    long Ticket() => Interlocked.Increment(ref _tickets);

    /// <summary>Under _lock: the scan's states (and driver-stale verdicts), each game's kept only if no later evaluation was
    /// stored meanwhile.</summary>
    long _scans, _listed;   // scans started; the start of the scan whose discovery _games lists (under _lock)

    /// <summary>A scan's states replace the list unless a scan started after it listed games first (one added meanwhile): then
    /// they only update the games that list has. Under _lock.</summary>
    void Publish(long started, List<GameState> states)
    {
        if (started >= _listed)
        {
            (_listed, _games) = (started, states);
            return;
        }
        foreach (var s in states)
            if (_games.FindIndex(x => x.Game.Id == s.Game.Id) is var i and >= 0) _games[i] = s;
    }

    List<GameState> Newest(List<GameState> states, List<long> tickets, List<bool> driverStale)
    {
        var now = new List<GameState>(states.Count);
        for (int i = 0; i < states.Count; i++)
        {
            var id = states[i].Game.Id;
            if (_removed.Contains(id)) continue;
            if (StaleCopy(states[i].Game))   // read before its folder changed: the state already listed (the new folder's) stays
            {
                if (_games.Find(x => x.Game.Id == id) is { } listed) now.Add(listed);
                continue;
            }
            if (_evaluatedAt.GetValueOrDefault(id) > tickets[i] && _games.Find(x => x.Game.Id == id) is { } newer) states[i] = newer;
            else (_evaluatedAt[id], _driverStale[id]) = (tickets[i], driverStale[i]);
            now.Add(WithVerdict(states[i]));
        }
        return now;
    }

    /// <summary>The game as its store lists it now. The scan's copy keeps the build id it had when the app started, so a game
    /// updated since then would be compiled, and its warm recorded and shared, under the old build. One store's discovery:
    /// only before a compile and after the game exits.</summary>
    Game Current(Game g)
    {
        foreach (var source in _sources.Where(s => s.Store == g.Store))
            try
            {
                if (source.Discover().FirstOrDefault(x => x.Id == g.Id) is { } now) return Following(Unreal(now));
            }
            catch (Exception e) { Log?.Report($"{source.Store}: discovery failed: {e.Message}"); }
        return Following(g);
    }

    /// <summary>The game with the exe it was seen running (<see cref="GameRecord.RunsExe"/>) when that is another file of its
    /// install than discovery's (Stellar Blade: discovery once took the copy in PatchData). A game added by hand follows
    /// it only inside the folder the user confirmed.</summary>
    /// A followed exe is dropped once it's gone, discovery names another exe, or the game's build changes (an update may
    /// have moved the launch target).</summary>
    Game Following(Game g)
    {
        var rec = Store.LoadGame(g.Id);
        if (rec.RunsExe is not { } exe) return g;
        if (!File.Exists(exe) || !GameFiles.Inside(g.InstallDir, exe) || rec.RunsExeFrom != g.ExePath || rec.RunsExeBuild != Build(g, exe))
        {
            (rec.RunsExe, rec.RunsExeFrom, rec.RunsExeBuild, rec.RunsExeAt, rec.RunsExeWas) = (null, null, null, null, null);
            Store.SaveGame(g.Id, rec);
            Log?.Report($"{g.Name}: no longer following {exe}: the game changed since it ran from there");
            return g;
        }
        return Unconfirmed(g) ? g : g with { ExePath = exe };
    }

    static string Build(Game g, string exe) => g.Version ?? (new FileInfo(exe) is { Exists: true } f ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "");

    /// <summary>A started game whose process runs another exe of its install than the game's: the record follows it, and the
    /// recorder moves next to it once the game exits (Reconcile), or is armed for it there when it shares the game exe's
    /// folder under another name. No process is opened: the paths of those named like the game's exe (else like another
    /// exe of its folder) come from the system's process list (<see cref="ProcessPath"/>), and only after a fresh
    /// anti-cheat check of the install finds it clean.</summary>
    /// <returns>True once decided: the game's exe runs, another was followed, or the game is never followed. False: nothing
    /// it could be runs yet (a launcher of its folder starts it later) or several copies run: asked again at the next poll.</returns>
    bool FollowRunningExe(GameState s, DateTimeOffset at)
    {
        var g = s.Game;
        if (s.AntiCheat != AntiCheat.None || Unconfirmed(g)) return true;
        var name = Path.GetFileName(g.ExePath);
        var dir = Path.GetDirectoryName(g.ExePath)!;
        var all = Processes(false);
        var named = all.Where(p => p.Exe.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        // two builds side by side (ACValhalla.exe, ACValhalla_Plus.exe): the recorder there loads into either
        var others = all.Where(p => !p.Exe.Equals(name, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(dir, p.Exe)) && !LauncherOrHelper(g, p.Exe)).ToList();
        if (named.Count + others.Count == 0) return false;
        if (GameFiles.DetectAntiCheat(g, quick: true) != AntiCheat.None) return true;   // an update since the last scan may have added one
        // inside the install only: never a staged warm, which runs from the data folder (OurCopy)
        var paths = named.Select(p => ProcessPath(p.Pid)).OfType<string>().Where(p => GameFiles.Inside(g.InstallDir, p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0)
            paths = others.Select(p => ProcessPath(p.Pid)).OfType<string>().Where(p => dir.Equals(Path.GetDirectoryName(p), StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Contains(g.ExePath, StringComparer.OrdinalIgnoreCase)) return true;   // as recorded
        if (paths is not [var runs]) return false;
        var rec = Store.LoadGame(g.Id);
        rec.RunsExeFrom = rec.RunsExe != null ? rec.RunsExeFrom : g.ExePath;   // discovery's, also when a followed exe is followed again
        (rec.RunsExe, rec.RunsExeBuild, rec.RunsExeAt, rec.RunsExeWas) = (runs, Build(g, runs), at, g.ExePath);
        Store.SaveGame(g.Id, rec);
        var there = dir.Equals(Path.GetDirectoryName(runs), StringComparison.OrdinalIgnoreCase) ? "is set up for it" : "moves there";
        Log?.Report($"{g.Name}: runs {runs}, not {g.ExePath}: following it (the recorder {there} once the game exits)");
        RecorderLog($"{g.Name}: the game runs {runs}, not {g.ExePath}");
        return true;
    }

    /// <summary>A process name <paramref name="g"/>'s exe folder may hold that is never the game: what discovery never picks.</summary>
    static bool LauncherOrHelper(Game g, string exe) =>
        XboxSource.LauncherOrUtility(exe) || GameFiles.NotTheGameExe(g.InstallDir, Path.Combine(Path.GetDirectoryName(g.ExePath)!, exe));

    GameState Find(string gameId) => Games.FirstOrDefault(s => s.Game.Id == gameId)
                                     ?? throw new ArgumentException($"unknown game '{gameId}' (scan first)");

    string RecordingPath(string gameId) => Path.Combine(Store.GameDir(gameId), "recording.db");

    /// <summary>A game the user added that a store now lists (its install holds the exe, and the store's id wins): its
    /// recording, and what its recorder recorded in its folder, are merged into the store game's recording, so the game plans
    /// from them. Its recorder comes out of its own folder by its own record (<see cref="TakeOutNow"/>), never merged into
    /// the store game's: another added copy may have recorded another folder. Until its folder is clean (the game runs, a
    /// file is held) it stays pending, and the next scan tries again.</summary>
    void AdoptManual(Game added, Game owner)
    {
        var (from, to) = (RecordingPath(added.Id), RecordingPath(owner.Id));
        var dir = Path.GetDirectoryName(added.ExePath)!;
        try
        {
            if (File.Exists(from))
                using (Recordings.Lock(to))
                using (Recordings.Lock(from))
                {
                    Directory.CreateDirectory(Store.GameDir(owner.Id));
                    Recordings.Merge(to, from, null);
                    Imported(owner);
                    File.Delete(from);
                    RecorderLog($"{owner.Name}: the recording made when you added it by hand now belongs to its store entry");
                }
            var was = Store.LoadGame(added.Id);
            if (was.RecorderFiles.Count > 0 || was.RecorderChained != null || was.RecorderExe != null || ProxyOnDisk(added) || ProxyOnDisk(RecordedAt(added, was)))
            {
                TakeOutNow(added, "its store entry took it over");
                was = Store.LoadGame(added.Id);
                if (was.RecorderFiles.Count > 0 || was.RecorderChained != null || was.RecorderRollback || ProxyOnDisk(added) || ProxyOnDisk(RecordedAt(added, was))) return;
            }
            if (!RecorderDataFiles.Concat(Recordings.AppFiles).Append(ArmedFile).Any(f => File.Exists(Path.Combine(dir, f))) || GameRunning(added)) return;
            var inbox = new FileInfo(Path.Combine(dir, "scskiller.db"));
            var unread = inbox is { Exists: true, Length: > 0 } && $"{inbox.Length}:{inbox.LastWriteTimeUtc.Ticks}" != was.RecordingInbox;
            Directory.CreateDirectory(Store.GameDir(owner.Id));
            if (RemoveRecorderData(dir, to, was.RecordingInbox, () => GameRunning(added)))
            {
                File.Delete(Path.Combine(dir, ArmedFile));
                if (unread) Imported(owner);
                RecorderLog($"{owner.Name}: the recorder's data files removed ({dir}), left by the copy you added by hand");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RecorderLog($"{owner.Name}: couldn't clear what SCSKiller kept for it as a game you added (tried again at the next scan): {e.Message}");
        }
    }

    /// <summary>The scan's cached check is keyed on <see cref="GameRecord.RecordingImportedAt"/>.</summary>
    void Imported(Game g)
    {
        var rec = Store.LoadGame(g.Id);
        rec.RecordingImportedAt = DateTimeOffset.Now;
        Store.SaveGame(g.Id, rec);
    }

    string RayQueryKeysPath(string gameId) => Path.Combine(Store.GameDir(gameId), "rayquery.keys");

    /// <summary>The planner's check on the union of this PC's recording and the community database's, without writing it:
    /// the union has draws when either has.</summary>
    PlanCheck CheckRecordings(Game g, EngineInfo engine)
    {
        var local = File.Exists(RecordingPath(g.Id)) ? new Recording(RecordingPath(g.Id)) : null;
        var community = CommunityInUse(g.Id) != null ? new Recording(Path.Combine(Store.GameDir(g.Id), "community.db")) : null;
        var check = _planner.Check(g, engine, local ?? community, Vendor.Caps);
        return check.Readiness == Readiness.NeedsRecording && local != null && community != null ? _planner.Check(g, engine, community, Vendor.Caps) : check;
    }

    /// <summary>The recording to plan and warm from, as a proxy db in <paramref name="work"/>: this PC's, merged with the
    /// community database's when one is in use, with every shader it names by hash read back from the
    /// install (the game's index, then the middleware DLLs next to its exe). Null = no recording.</summary>
    Recording? PrepareRecording(Game game, EngineInfo engine, ShaderIndex index, string work, CancellationToken ct)
    {
        var local = RecordingPath(game.Id);
        var community = CommunityInUse(game.Id) != null ? Path.Combine(Store.GameDir(game.Id), "community.db") : null;
        if (!File.Exists(local) && community == null) return null;
        Directory.CreateDirectory(work);
        var src = local;
        var foreign = ForeignVariants(game);
        if (community != null) Community.Union(local, community, src = Path.Combine(work, "merged.db"), foreign);
        if (foreign.Count > 0) Log?.Report($"{game.Name}: {foreign.Count:N0} community recording pipelines made at another texture filtering setting than yours left out");
        var db = Path.Combine(work, "recording.db");
        var r = Rehydrate.Run(src, db, game, engine, _reader, index, null, ct, h => Middleware.Blobs(game, h));
        if (src != local) File.Delete(src);
        if (r.Found > 0 || !r.Complete)
            Log?.Report($"{game.Name}: recording rehydrated from the install ({r.Found} shaders"
                + (r.FoundInMiddleware > 0 ? $", {r.FoundInMiddleware} of them from middleware DLLs" : "")
                + (r.Complete ? ")" : $", {r.Missing.Count} not in this install: those PSOs are skipped)"));
        return new Recording(db);
    }

    CommunityDownload? CommunityInUse(string gameId) => Settings.UseCommunityDb ? Community.Downloaded(Store.GameDir(gameId)) : null;

    /// <summary>After a scan, the setting turned on, or a sign-in with "db" (the app): in the background unless a pass is
    /// running (default: every scanned game); see <see cref="SyncCommunity(IReadOnlyList{Game})"/>. <paramref name="fresh"/>
    /// (a user's refresh): after the running pass, which may be past its manifest check, with the manifest checked again;
    /// a later scan's games replace that pass's until it starts.</summary>
    public void StartCommunitySync(IEnumerable<GameState>? states = null, bool fresh = false)
    {
        var games = (states ?? Games).Where(s => s.Engine != null).Select(s => s.Game).ToList();
        lock (_scanLock)
        {
            if (Community is not { } community) return;
            if (fresh)
            {
                // one pass queued at most, however many refreshes wait behind a long one: the latest scan's games
                var queued = _freshGames != null;
                _freshGames = games;
                if (!queued)
                    CommunitySync = CommunitySync.ContinueWith(_ =>
                    {
                        List<Game> next;
                        lock (_scanLock) (next, _freshGames) = (_freshGames!, null);
                        community.Expire();
                        return SyncCommunity(next);
                    }, TaskScheduler.Default).Unwrap();
            }
            else if (_freshGames != null) _freshGames = games;   // a refresh's pass not started yet takes the newer scan
            else if (CommunitySync.IsCompleted) CommunitySync = Task.Run(() => SyncCommunity(games));
        }
    }

    /// <summary>The shared packs (<see cref="SyncPacks"/>, whatever the settings and the sign-in), then with
    /// Settings.UseCommunityDb the manifest, then a download for every game whose build has an entry other than the one it has; each game
    /// that got one, or whose entry the manifest check changed (signed out nothing downloads), is re-evaluated. Never throws.</summary>
    async Task SyncCommunity(IReadOnlyList<Game> games)
    {
        try
        {
            foreach (var g in await SyncPacks(games, CancellationToken.None)) Refresh(g);
            if (!Settings.UseCommunityDb) return;
            foreach (var g in games)
                if (await SyncCommunity(g, null, CancellationToken.None) || DbEntryChanged(g)) Refresh(g);
        }
        catch (Exception e) { Log?.Report($"community database: {e.Message}"); }
        finally { RequestCollect(); }   // a download's check reads the whole recording
    }

    /// <summary>Downloads the shared pack of every upscaler DLL version (<see cref="Middleware.SharedVendors"/>) next to these
    /// games' exes that the manifest lists for this GPU vendor and that isn't downloaded yet, matched locally by its pack key:
    /// the requests name only packs this PC has a DLL for, and nothing about its games. Returns the games whose pack changed.</summary>
    async Task<List<Game>> SyncPacks(IReadOnlyList<Game> games, CancellationToken ct)
    {
        var changed = new List<Game>();
        if (Community is not { } community || (_planner as Planner)?.SharedPacks is not { } shared || PackGpu(Vendor.Vendor) is not { } gpu) return changed;
        var dlls = games.SelectMany(g => Dlls(g).Where(d => d.Packable && Middleware.SharedVendors.Contains(d.Vendor)).Select(d => (Game: g, Dll: d))).ToList();
        // no DLL is hashed unless the manifest lists a pack at all
        if (dlls.Count == 0 || await community.ManifestAsync(ct) is not { HasPacks: true } manifest) return changed;
        var keyed = dlls.Select(x => (x.Game, x.Dll, Image: Middleware.TryScan(x.Dll.Path))).Where(x => x.Image != null)
            .Select(x => (x.Game, x.Dll, x.Image, Key: HashOnly.PackKey(gpu, x.Dll.Vendor, x.Dll.Name, x.Image!.ContentHash))).ToList();
        foreach (var pack in keyed.GroupBy(x => x.Key))
        {
            var (_, d, image, key) = pack.First();
            if (manifest.FindPack(key) is not { } e || shared.Load(d, image!)?.Header.Object == e.Object) continue;
            var users = pack.Select(x => x.Game).DistinctBy(g => g.Id).ToList();
            if (await community.DownloadPackAsync(e, d, image!, shared, gpu == "amd", ct) is not { } n)
            {
                if (community.Problem is { } why) Log?.Report($"{d.Name}: shared pack: {why}");
                continue;
            }
            Log?.Report($"{d.Name} ({image!.ContentHash[..12]}): shared pack downloaded ({n:N0} pipelines, {e.Uploaders} contributor{(e.Uploaders == 1 ? "" : "s")})");
            changed.AddRange(users.Where(g => !changed.Any(c => c.Id == g.Id)));
        }
        return changed;
    }

    bool OnAmd => Vendor.Caps.Profile.StartsWith("amd");

    /// <summary>The game's plans take pack entries (<see cref="Planner.SeedsPacks"/>), by the engine the scan found.</summary>
    bool SeedsPacks(Game g)
    {
        EngineInfo? engine;
        lock (_scanLock) engine = _scan?.GetValueOrDefault(g.Id)?.Engine;
        return engine != null && Planner.SeedsPacks(engine);
    }

    /// <summary>The middleware next to the exe (<see cref="Middleware.Detect(string)"/>) of a game whose last check found no
    /// anti-cheat: that verdict, not another walk of the install.</summary>
    List<MiddlewareDll> Dlls(Game g)
    {
        lock (_scanLock)
            if ((_scan ??= Store.LoadScan()).GetValueOrDefault(g.Id) is not { AntiCheat: AntiCheat.None } || _verdicts.ContainsKey(g.Id)) return [];
        return Middleware.Detect(Path.GetDirectoryName(g.ExePath)!);
    }

    /// <summary>The entries of the pack of this DLL version that seed a plan on this GPU (<see cref="MiddlewarePacks.Runs"/>),
    /// and the root signatures the pack keeps.</summary>
    (List<PsoDb.Rec> Entries, List<string> RootSignatures) PackSeeds(MiddlewarePacks packs, MiddlewareDll d) =>
        d.Packable && Directory.Exists(Path.Combine(packs.Dir, d.Vendor)) ? packs.Seeds(d, OnAmd) : ([], []);   // no pack of its vendor: not hashed

    /// <summary>The inputs a compile would take now (<see cref="WarmInputs"/>): the recording, the community recording while
    /// one is in use, the plan, and for a D3D12 game the entries of its DLLs' packs this GPU runs. Cached on all of them.</summary>
    HashSet<string> InputsNow(Game g, GameRecord r)
    {
        var (inputs, extra) = InputFiles(g, r);
        return KeyFiles.Derived(Store.GameDir(g.Id), inputs, extra, () =>
            WarmInputs.Of(RecordedNow(g, r), r.Plan?.FilePath, SeedsNow(g).SelectMany(s => s.Entries), Elsewhere(g, r)));
    }

    /// <summary>The files <see cref="InputsNow"/> derives from, and what else it depends on.</summary>
    (string?[] Inputs, string Extra) InputFiles(Game g, GameRecord r)
    {
        var dir = Store.GameDir(g.Id);
        var community = CommunityInUse(g.Id) != null ? Path.Combine(dir, "community.db") : null;
        var dlls = Dlls(g).Where(d => d.Packable).ToList();
        var packs = _planner is Planner p && SeedsPacks(g) ? new[] { p.Packs, p.SharedPacks }.OfType<MiddlewarePacks>().ToList() : [];
        var packFiles = packs.SelectMany(x => dlls.Where(d => Directory.Exists(Path.Combine(x.Dir, d.Vendor)))
            .Select(d => Middleware.TryScan(d.Path) is { } i ? x.PathOf(d.Vendor, d.Name, i.ContentHash) : null));
        return ([RecordingPath(g.Id), community, r.Plan?.FilePath, Path.Combine(dir, Sharing.ShippedFile), .. dlls.Select(d => d.Path), .. packFiles],
            $"{IndexIsInstalled(g, r)}|{r.IndexContentHash}|{OnAmd}|{Vendor.Vendor}");
    }

    /// <summary>What the packs of the DLLs next to the game's exe seed on this GPU (<see cref="PackSeeds"/>).</summary>
    List<(List<PsoDb.Rec> Entries, List<string> RootSignatures)> SeedsNow(Game g) =>
        _planner is Planner p && SeedsPacks(g)
            ? [.. new[] { p.Packs, p.SharedPacks }.OfType<MiddlewarePacks>().SelectMany(x => Dlls(g).Where(d => d.Packable).Select(d => PackSeeds(x, d)))]
            : [];

    /// <summary>The blobs on disk a warm of this game finds outside its recordings and plan, for the count and the warm's
    /// snapshot alike: the build's shaders (index.shaders; unknown: taken as there), the DLLs next to its exe and the root
    /// signatures of what their packs seed.</summary>
    Func<string, bool> Elsewhere(Game g, GameRecord r)
    {
        var shipped = Shipped(g, r);
        var images = Dlls(g).Where(d => d.Packable).Select(d => Middleware.TryScan(d.Path)).OfType<MiddlewareImage>().ToList();
        var roots = SeedsNow(g).SelectMany(s => s.RootSignatures).ToHashSet();
        return h => shipped?.Contains(h) != false || roots.Contains(h) || images.Any(i => i.Containers.ContainsKey(h));
    }

    /// <summary>The recordings' part of <see cref="InputsNow"/>, read now.</summary>
    WarmInputs.Recorded RecordedNow(Game g, GameRecord r) =>
        WarmInputs.Recorded.Read([RecordingPath(g.Id), .. CommunityInUse(g.Id) != null ? new[] { Path.Combine(Store.GameDir(g.Id), "community.db") } : []], Elsewhere(g, r),
            Vendor.Vendor == GpuVendor.Nvidia, ForeignVariants(g));

    /// <summary>The community recording's records made at another texture filtering setting than this PC's recording
    /// (<see cref="SamplerVariants.Foreign"/>): neither compiled nor counted. None without this PC's recording.</summary>
    HashSet<string> ForeignVariants(Game g)
    {
        var (local, community) = (RecordingPath(g.Id), CommunityInUse(g.Id) != null ? Path.Combine(Store.GameDir(g.Id), "community.db") : null);
        return community == null || !File.Exists(local) ? []
            : KeyFiles.Derived(Store.GameDir(g.Id) + "|variants", [local, community], "", () => SamplerVariants.Foreign(local, [community]));
    }

    /// <summary>The pipelines every warm on this driver skips: they crash it.</summary>
    IReadOnlySet<string> CrashKeysNow(GameRecord r) => CrashKeysCurrent(r, Snapshot()) ? r.CrashKeys : new HashSet<string>();

    /// <summary>The crash keys are this driver's: by DriverId, or an earlier build's version string the snapshot judges
    /// current (adopted only from a complete read: until then they are kept, not cleared). Unknown DriverId: kept.</summary>
    static bool CrashKeysCurrent(GameRecord r, GpuSnapshot s) => r.CrashKeysDriver == s.Id || CurrentDriver(s, null, r.CrashKeysDriver);

    /// <summary>New pipelines since the last complete warm, derived whenever the game is evaluated: the inputs a compile
    /// would take now (<see cref="InputsNow"/>) that the warm didn't take when it started (its key file), less the
    /// pipelines that crash this driver; split into what the planner made (<paramref name="Planned"/>, the plan's key file;
    /// null without a readable one) and the rest (<paramref name="Recorded"/>). A warm key file missing or damaged is an
    /// unknown baseline (<paramref name="Unknown"/>): everything counts, as for a warm from before warms kept one.</summary>
    readonly record struct Pending(long Recorded, long? Planned, bool Unknown = false, long Upscaler = 0);   // Upscaler: of Recorded, the packs' entries

    /// <summary>Crash keys as warm inputs: the warm names a crashed PSO by its record key, and a plan input is keyed by its
    /// 'N' record when it has one (<see cref="Planner.PlanInputs"/>).</summary>
    internal static IReadOnlySet<string> CrashInputs(IReadOnlySet<string> crash, string? planFile) => crash.Count == 0 ? crash
        : crash.Union(Planner.PlanInputs(Planner.PlanBody(planFile)).Where(x => crash.Contains(x.Rec.Key)).Select(x => x.Key)).ToHashSet();

    Pending PendingOf(Game g, GameRecord r)
    {
        if (r.WarmedAt == null) return new(0, null);
        var dir = Store.GameDir(g.Id);
        var key = PendingStamp(g, r);   // before anything is read: what changes meanwhile is counted again next time
        if (key != null && r.Pending is { } known && known.Key == key) return new(known.Recorded, known.Planned, known.Unknown, known.Upscaler);
        var warmed = r.WarmKeysFile is { } wf ? KeyFiles.Set(Path.Combine(dir, wf)) : null;
        var made = r.PlanKeysFile is { } pf ? KeyFiles.Set(Path.Combine(dir, pf)) : null;
        var crash = CrashInputs(CrashKeysNow(r), r.Plan?.FilePath);
        var inputs = InputsNow(g, r);
        var fresh = inputs.Where(i => !WarmInputs.Lacks(i) && !crash.Contains(WarmInputs.Key(i)) && !WarmInputs.Records(i).Any(crash.Contains) && (warmed == null || !WarmInputs.Taken(warmed, i)))
            .Select(i => WarmInputs.Records(i).First()).Distinct().ToList();   // a pipeline with two new stages is one
        var packs = SeedsNow(g).SelectMany(x => x.Entries).Select(e => e.Key)
            .Concat(Planner.PlanBody(r.Plan?.FilePath).Where(x => x.Tag == 'M').Select(x => MiddlewarePacks.Unwrap(x).Entry.Key)).ToHashSet();
        var one = fresh.Count > 0 ? PipelineKeys(g, r) : [];
        string P(string k) => one.GetValueOrDefault(k, k);   // a pipeline at two texture filtering settings is one
        var news = fresh.Where(k => made?.Contains(k) != true).ToList();
        var pending = new Pending(news.Select(P).Distinct().Count(), made == null ? null : fresh.Where(made.Contains).Select(P).Distinct().Count(), warmed == null,
            news.Where(packs.Contains).Select(P).Distinct().Count());
        if (key != null && inputs.Count > 0) r.Pending = new(key, pending.Recorded, pending.Planned, pending.Unknown, pending.Upscaler);   // none: maybe a read that failed
        return pending;
    }

    /// <summary>The records of the recordings (less <see cref="ForeignVariants"/>) and the plan that have static samplers, each
    /// to its <see cref="SamplerVariants.Pipeline"/> key, and a plan's NVAPI state record to its pipeline's with that state:
    /// the inputs a pipeline has at each texture filtering setting, as one.</summary>
    Dictionary<string, string> PipelineKeys(Game g, GameRecord r)
    {
        var foreign = ForeignVariants(g);
        var (records, nvs, blobs) = (new List<PsoDb.Rec>(), new List<PsoDb.Rec>(), new Dictionary<string, byte[]>());
        try
        {
            foreach (var x in new[] { RecordingPath(g.Id), CommunityInUse(g.Id) != null ? Path.Combine(Store.GameDir(g.Id), "community.db") : null }
                         .Where(File.Exists).SelectMany(f => PsoDb.Read(f!)).Concat(Planner.PlanBody(r.Plan?.FilePath)))
                if (x.Tag == 'B') { if (x.Payload.Length > 20 && Carved.Dxbc.IsRootSignatureOnly(x.Payload.AsSpan(20))) blobs[PsoDb.Hex(x.Payload.AsSpan(0, 20))] = x.Payload[20..]; }
                else if (x.Tag == 'N') nvs.Add(x);
                else if (!foreign.Contains(x.Key)) records.Add(x);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return []; }
        var map = new Dictionary<string, string>();
        foreach (var x in records)
            if (SamplerVariants.Pipeline(x, blobs.GetValueOrDefault) is { } p) map[x.Key] = p;
        foreach (var n in nvs.Where(n => n.Payload.Length == PsoDb.NvState.Size))
            if (map.GetValueOrDefault(PsoDb.Hex(n.Payload.AsSpan(0, 20))) is { } p)
                map[n.Key] = PsoDb.Hex(SHA1.HashData([(byte)'N', .. Convert.FromHexString(p), .. n.Payload[20..]]));
        return map;
    }

    /// <summary>What <see cref="PendingOf"/> counts from: the inputs, the warm's and the plan's key files, the crash keys;
    /// null when one can't be read.</summary>
    string? PendingStamp(Game g, GameRecord r)
    {
        var dir = Store.GameDir(g.Id);
        var (inputs, extra) = InputFiles(g, r);
        try
        {
            return KeyFiles.DerivedStamp([.. inputs, r.WarmKeysFile is { } wf ? Path.Combine(dir, wf) : null, r.PlanKeysFile is { } pf ? Path.Combine(dir, pf) : null],
                $"{extra}|{string.Join(',', CrashKeysNow(r).Order())}|pipelines");   // the rule's mark: a count stored under another is redone
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The plan's records the planner made: neither a pack entry nor in the recording prepared for it in <paramref name="work"/>.</summary>
    static HashSet<string> PlannerMade(Plan plan, string work)
    {
        var rec = Path.Combine(work, "recording.db");
        var recorded = File.Exists(rec) ? PipelineKeys(rec) : [];
        return [.. Planner.PlanInputs(Planner.PlanBody(plan.FilePath)).Where(x => x.Rec.Tag != 'M').Select(x => x.Key).Where(k => !recorded.Contains(k))];
    }

    /// <summary>The keys of what a plan replays, the pack entries' own keys included (not their 'M' wrappers'); empty when unreadable.</summary>
    static HashSet<string> PlanPipelineKeys(string path)
    {
        try
        {
            return PlanFile.Read(path).Records.Where(r => r.Tag != 'B').Select(r => r.Tag == 'M' ? MiddlewarePacks.Unwrap(r).Entry.Key : r.Key).ToHashSet();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { return []; }
    }

    bool DbEntryChanged(Game g)
    {
        if (Games.FirstOrDefault(s => s.Game.Id == g.Id) is not { } s) return false;
        var m = LocalManifest();
        var e = m != null ? DbEntry(m, g, Store.LoadGame(g.Id)) : null;
        return s.InCommunityDb != (m != null ? e != null : null) || s.CommunityDbPsos != (e?.Psos ?? 0);
    }

    /// <summary>One game: by its fresh index's content hash when given, else by its store build (no build id: its last
    /// index). True when a new recording was downloaded.</summary>
    async Task<bool> SyncCommunity(Game g, string? contentHash, CancellationToken ct)
    {
        if (Community is not { } community || !Settings.UseCommunityDb || await community.ManifestAsync(ct) is not { } manifest) return false;
        var entry = contentHash != null ? manifest.Find(g, contentHash) : DbEntry(manifest, g, Store.LoadGame(g.Id));
        var dir = Store.GameDir(g.Id);
        if (entry == null || entry.Object == Community.Downloaded(dir)?.Object) return false;
        // a compile saves its own record after this, and plans and warms the download anyway
        if (await community.DownloadAsync(entry, dir, ct) is not { } got)
        {
            if (community.Problem is { } why) Log?.Report($"{g.Name}: community database: {why}");
            return false;
        }
        Log?.Report($"{g.Name}: community recording downloaded ({got.Psos:N0} pipelines, {entry.Uploaders} contributor{(entry.Uploaders == 1 ? "" : "s")})");
        return true;
    }

    /// <summary>Queues a background pass that shares these games' recordings (default: every game) when
    /// Settings.ShareRecordings is on: after a scan, a recording import, a game's exit, a compile's index, and the setting turned on.
    /// Never blocks the caller; failures are only logged.</summary>
    public void StartSharing(IEnumerable<Game>? games = null)
    {
        if (Sharing is not { } sharing || !Settings.ShareRecordings) return;
        var list = (games ?? Games.Select(s => s.Game)).ToList();
        lock (_scanLock) SharingPass = SharingPass.ContinueWith(_ => Share(sharing, list), TaskScheduler.Default).Unwrap();
    }

    async Task Share(Sharing sharing, List<Game> games)
    {
        sharing.Waiting = null;
        await SharePacks(sharing);
        string? logged = null;   // a back-off's problem once per pass, not once per game
        foreach (var g in games)
            try
            {
                // a store build key is published as an alias: a game the user added has none, only this PC's path hash
                if (g.Store == Core.Store.Manual) continue;
                var rec = Store.LoadGame(g.Id);
                // Only with the content hash of exactly this build: an older build's would alias the new build to the old entry.
                // ponytail: a store without build ids (no store build key) doesn't share; add when the server takes a key without one
                if (g.Version is not { } v || rec.IndexGameVersion != v || rec.IndexContentHash is not { Length: 40 } hash) continue;
                if (BlockingMod(g) != null) continue;   // read now: the state may not be published yet
                if (await sharing.ShareAsync(Store.GameDir(g.Id), hash, () => UploadMetaOf(g, v, hash),
                        () => Dlls(g).Where(d => d.Packable).SelectMany(d => Middleware.Scan(d.Path).Containers.Keys), LayerMadeNow) is { } got)
                {
                    Log?.Report($"{g.Name}: recording shared ({got.Psos:N0} pipelines, {got.NewPsos:N0} new to the community database)");
                    Refresh(g);
                }
                else if (sharing.Problem is { } why && why != logged) Log?.Report($"{g.Name}: {logged = why}");
            }
            catch (Exception e) { Log?.Report($"{g.Name}: sharing the recording failed: {e.Message}"); }
        if (sharing.Waiting is { } wait) Log?.Report($"sharing waits for a recording to be whole (a game still recording?): {wait}");
        RequestCollect();
    }

    /// <summary>What a layer made, from every recording here (one may have a record without its 'W', another with it) and the
    /// packs' list; read before each upload's payload, and throws when one can't be read: nothing is shared then.</summary>
    HashSet<string> LayerMadeNow()
    {
        var keys = Recordings.LayerMadeOnDisk(Store.DataDir);
        if ((_planner as Planner)?.Packs is { } packs) keys.UnionWith(packs.LayerMade());
        return keys;
    }

    /// <summary>This PC's packs of the shared vendors that gained records since they were last uploaded (<see cref="Sharing.SharePacksAsync"/>).</summary>
    async Task SharePacks(Sharing sharing)
    {
        if ((_planner as Planner)?.Packs is not { } packs || PackGpu(Vendor.Vendor) is not { } gpu) return;
        try
        {
            // every recording's, also one whose import hasn't run since (a crash between its merge and the packs' exclusion)
            try { packs.Exclude(Recordings.LayerMadeOnDisk(Store.DataDir)); }
            catch (Recordings.IncompleteLayerList e) { sharing.Waiting = e.Message; return; }
            foreach (var (dll, n) in await sharing.SharePacksAsync(packs.Dir, gpu, AppVersion.Current.ToString(), LayerMadeNow))
                Log?.Report($"{dll}: upscaler pack shared ({n:N0} pipelines)");
            if (sharing.Problem is { } why) Log?.Report($"sharing upscaler packs: {why}");
        }
        catch (Exception e) { Log?.Report($"sharing upscaler packs failed: {e.Message}"); }
    }

    UploadMeta UploadMetaOf(Game g, string version, string contentHash)
    {
        static string? Clip(string? s) => s is { Length: > 128 } ? s[..128] : s;
        EngineInfo? engine;   // the scan's cache: Games is still empty during an app's first scan
        lock (_scanLock) engine = _scan?.GetValueOrDefault(g.Id)?.Engine;
        return new($"{g.Id}@{version}", contentHash, Clip(engine is { } e ? $"{e.Family} {e.Version} {e.Fork}".TrimEnd() : null),
            Vendor.Vendor.ToString().ToLowerInvariant(), AppVersion.Current.ToString(),
            [.. Dlls(g).Where(d => d.Packable).Take(64).Select(d => new UploadDll(Clip(d.Name)!, Middleware.Scan(d.Path).ContentHash))]);
    }

    /// <summary>Imports the game folder's scskiller.db (the recorder's inbox) into SCSKiller's copy when it changed since the
    /// last import, then empties it once nothing has it open (<see cref="Recordings"/>). Whatever the inbox holds is merged
    /// by record key, so a recording that started over loses nothing. A copy stored before compact recordings (a proxy db,
    /// or its merge with the community's in recording.all.db) is left to <see cref="MigrateRecordings"/> unless
    /// <paramref name="migrate"/>. Waits for the recording lock until <paramref name="ct"/> is cancelled (it then throws).</summary>
    bool ImportRecording(Game g, GameRecord rec, bool migrate = false, CancellationToken ct = default)
    {
        var inbox = new FileInfo(Path.Combine(Path.GetDirectoryName(g.ExePath)!, "scskiller.db"));
        var store = RecordingPath(g.Id);
        var all = Path.Combine(Store.GameDir(g.Id), "recording.all.db");
        var keysFile = Path.Combine(inbox.DirectoryName!, Recordings.KeysFile);
        // keys naming more than the index's shaders with no recording (the data folder was deleted): the recorder would skip blobs nothing has
        if (!File.Exists(store) && File.Exists(keysFile) && Length(keysFile) != 8 + Math.Max(0, Length(Path.Combine(Store.GameDir(g.Id), Sharing.ShippedFile)) - 20)) WriteKeys(g, rec, ct);
        var legacy = Legacy(g.Id);
        if (legacy && !migrate) return false;
        var stamp = inbox.Exists ? $"{inbox.Length}:{inbox.LastWriteTimeUtc.Ticks}" : null;
        var pending = inbox is { Exists: true, Length: > 0 } && stamp != rec.RecordingInbox;
        if (!pending && legacy)
            using (Recordings.Lock(store, ct: ct))   // decided under it too: another process may have just imported into the copy
                if (Length(store) == 0 && Legacy(g.Id))   // only a community merge (or an empty copy): nothing of this PC's to keep
                {
                    try { foreach (var f in new[] { all, all + ".key", store }) File.Delete(f); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{g.Name}: could not delete {all}: {e.Message}"); return false; }
                    return true;
                }
        if (!pending && !legacy)
        {
            if (inbox is not { Exists: true, Length: > 0 }) return false;
            // imported while the game held it: emptied once it's gone, and nothing in its folder while it runs
            using (Recordings.Lock(store, ct: ct))
            {
                if (!GameFolderWrite(g) || !Recordings.Rotate(inbox.FullName, inbox.Length)) return false;
                if (rec.KeysPending) WriteKeys(g, rec, ct);   // imported while the game ran: its keys waited too
            }
            rec.RecordingInbox = null;
            return true;
        }
        int added;
        try
        {
            using (Recordings.Lock(store, ct: ct))
            {
                Directory.CreateDirectory(Store.GameDir(g.Id));
                var shipped = Shipped(g, rec);
                var keys = Recordings.Merge(store, pending ? inbox.FullName : null, shipped == null ? null : shipped.Contains, out var nvAdded);
                File.Delete(all);
                File.Delete(all + ".key");
                added = keys.Count;
                (rec.RecordingInbox, rec.RecordingIndexHash) = (stamp, shipped != null ? rec.IndexContentHash : null);
                // an inbox whose records the copy already has (not a migration's: it imported them as a copy): merged by an
                // import that ended before its record was saved
                // NVAPI state alone counts: synthesized PSOs take the recording's (PlanBuilder.RasterNv)
                if (keys.Count > 0 || nvAdded || pending && !legacy && PsoDb.Read(inbox.FullName).Any(r => r.Tag is not ('B' or 'N' or 'L' or 'W'))) rec.RecordingImportedAt = DateTimeOffset.Now;
                WriteKeys(g, rec);
                // a layer's records (the 'W' may come after an earlier import put them in a pack) never stay in a shared pack
                // a list that can't be written now fails closed: sharing reads every recording's 'W' again (LayerMadeNow)
                try { if ((_planner as Planner)?.Packs is { } packs) packs.Exclude(Recordings.LayerMade(store)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { Log?.Report($"{g.Name}: upscaler packs not updated: {e.Message}"); }
                // emptied: what it gets next is new, whatever its size and write time. The import is saved first: a crash
                // after an emptied inbox would leave the record (and the scan's cached readiness) without it
                if (pending && GameFolderWrite(g))
                {
                    Store.SaveGame(g.Id, rec);
                    InboxRotating?.Invoke();
                    if (Recordings.Rotate(inbox.FullName, inbox.Length)) rec.RecordingInbox = null;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log?.Report($"{g.Name}: could not import {inbox.FullName}: {e.Message}");
            return false;
        }
        if (added > 0) StartSharing([g]);   // a recording session ended
        UpdateRecorderIni(g, rec);
        return true;
    }

    /// <summary>Tests: a compile has read its recordings and is about to prepare them (under the recording lock).</summary>
    internal Action? RecordingsRead;

    /// <summary>Tests: an import is about to empty the inbox it merged.</summary>
    internal Action? InboxRotating;

    /// <summary>Tests: a migration has read the game's record (<see cref="MigrateRecordings"/>).</summary>
    internal Action? MigrationLoaded;

    internal bool HoldsRecorderLock => Monitor.IsEntered(_recorderLock);

    /// <summary>The game's recording is stored as before compact recordings: <see cref="MigrateRecordings"/> converts it.</summary>
    bool Legacy(string gameId) => File.Exists(RecordingPath(gameId)) && !PsoDb.IsCompact(RecordingPath(gameId))
                                  || File.Exists(Path.Combine(Store.GameDir(gameId), "recording.all.db"));

    /// <summary>The install is the build last indexed, by what tells a game update (as <see cref="WarmChanged"/>): the
    /// store's build id, or without one the exe's size and write time.</summary>
    static bool IndexIsInstalled(Game g, GameRecord r) =>
        g.Version != null && r.IndexGameVersion != null ? g.Version == r.IndexGameVersion : r.IndexExeStamp == ExeStamp(g);

    /// <summary>The shaders the install gives back, by the index of exactly this build (index.shaders); null when that isn't
    /// known (the game changed since it was indexed, or never was), and the recording then keeps every shader's bytes.</summary>
    HashSet<string>? Shipped(Game g, GameRecord rec) =>
        IndexIsInstalled(g, rec) && rec.IndexContentHash is { } hash ? Sharing.Shipped(Store.GameDir(g.Id), hash) : null;

    /// <summary>Writes the recorder's <see cref="Recordings.KeysFile"/> when the recorder is ours: what it needn't record again,
    /// and the last index's shaders, which it names by hash only while the install is that build
    /// (<see cref="IndexIsInstalled"/>, noted in <see cref="GameRecord.KeysIndexHash"/>). Without the file the recorder
    /// records everything with its bytes, which the next import drops. Without the recorder the file is deleted. Returns
    /// the records it leaves out (<see cref="Recordings.WriteKeys"/>). Nothing while the game runs (<see cref="GameFolderWrite"/>),
    /// checked again once the lock is held: <see cref="GameRecord.KeysPending"/>, and the refresh or reconcile after it
    /// exits writes it.</summary>
    int WriteKeys(Game g, GameRecord rec, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(g.ExePath)!;
        var keys = Path.Combine(dir, Recordings.KeysFile);
        if (!GameFolderWrite(g)) return KeysLater(rec);
        using var gate = Recordings.Lock(RecordingPath(g.Id), ct: ct);   // the keys file's writers too: one temp name in the game folder
        if (!GameFolderWrite(g)) return KeysLater(rec);   // the game may have started during the wait
        try
        {
            var dll = Path.Combine(dir, "d3d12.dll");
            if (IsOurProxy(dll))
            {
                var shipped = rec.IndexContentHash is { } h ? Sharing.Shipped(Store.GameDir(g.Id), h) : null;
                var installed = shipped != null && IndexIsInstalled(g, rec);
                var later = false;   // the game started before the publish
                var left = Recordings.WriteKeys(RecordingPath(g.Id), shipped, keys, installed,
                    () =>
                    {
                        if (GameFolderWrite(g)) return IsOurProxy(dll);
                        later = true;
                        return false;
                    });
                (rec.KeysIndexHash, rec.KeysPending) = (installed ? rec.IndexContentHash : null, later);   // cleared once written
                if (!later) PublishWarmed(g, dir);
                return left;
            }
            File.Delete(keys);
            File.Delete(Path.Combine(dir, Recordings.WarmedFile));
            (rec.KeysIndexHash, rec.KeysPending) = (null, false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            rec.KeysPending = true;   // the next refresh tries again
            RecorderLog($"{g.Name}: couldn't write {keys}: {e.Message}");
        }
        return 0;
    }

    /// <summary>The last complete compile's <see cref="Recordings.WarmedFile"/> next to the recorder, so its csv's known
    /// columns mean "compiled"; none without one. Called with the recording lock held, the game not running.</summary>
    void PublishWarmed(Game g, string dir)
    {
        var src = Path.Combine(Store.GameDir(g.Id), Recordings.WarmedFile);
        var dst = Path.Combine(dir, Recordings.WarmedFile);
        if (!File.Exists(src)) { File.Delete(dst); return; }
        try
        {
            File.Copy(src, dst + ".tmp", true);   // a temp name in Recordings.AppFiles: cleaned up with the recorder, never an install change
            File.Move(dst + ".tmp", dst, true);
        }
        finally { File.Delete(dst + ".tmp"); }
    }

    static int KeysLater(GameRecord rec)
    {
        rec.KeysPending = true;
        return 0;
    }

    /// <summary>Indexes the installed build and saves it in the record: its shader list (<see cref="Sharing.ShippedFile"/>),
    /// the recording without the bytes the install gives back, and the keys file naming its shaders.</summary>
    ShaderIndex Index(Game g, EngineInfo engine, GameRecord rec, CancellationToken ct)
    {
        var stamp = ExeStamp(g);   // before the read: an update while it runs shows as another build
        var index = _reader.Index(g, engine, Log, ct);
        (rec.IndexContentHash, rec.ShaderCount, rec.IndexGameVersion, rec.IndexExeStamp) = (index.ContentHash, index.Shaders.Count, g.Version, stamp);
        Sharing.SaveShipped(Store.GameDir(g.Id), index);
        CompactRecording(g, rec, index, ct);
        Store.SaveGame(g.Id, rec);
        return index;
    }

    /// <summary>The background indexes of games armed for recording before their build was indexed, one at a time.</summary>
    public Task RecorderIndexing { get; private set; } = Task.CompletedTask;
    readonly ConcurrentDictionary<string, CancellationTokenSource> _recorderIndexes = new();

    /// <summary>The exe too: a store's build id is only the listed one until the next scan, and the watcher re-arms an
    /// update before that.</summary>
    static bool Indexed(Game g, GameRecord rec) => rec.IndexContentHash != null && IndexIsInstalled(g, rec) && rec.IndexExeStamp == ExeStamp(g);

    /// <summary>An armed recorder of a build with no index records every shader with its bytes (about 36 KB each: thousands of
    /// them filled the recording limit within minutes) until a compile indexes it: indexed in the background instead, so the
    /// keys file names the shipped shaders from the next launch. Cancelled when the game or its compile starts; a game whose
    /// shaders can't be indexed records as before.</summary>
    void IndexForRecorder(Game g)
    {
        if (!ManageRecorders || Games.FirstOrDefault(s => s.Game.Id == g.Id) is not { Engine: { Unsupported: null, Encrypted: false } }
            || Indexed(g, Store.LoadGame(g.Id))) return;
        var cts = new CancellationTokenSource();
        if (!_recorderIndexes.TryAdd(g.Id, cts)) return;
        lock (_recorderIndexes)
            RecorderIndexing = RecorderIndexing.ContinueWith(_ => RunRecorderIndex(g.Id, cts.Token), CancellationToken.None,
                TaskContinuationOptions.LongRunning, TaskScheduler.Default);
    }

    void RunRecorderIndex(string id, CancellationToken ct)
    {
        string name = id;
        Game? again = null;
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;   // its own thread (LongRunning), as the warm's default priority
            while (Settings.PauseWhileGaming && _playing.Count > 0 && !ct.IsCancellationRequested) Thread.Sleep(Poll);   // the watcher's: no process list per wait
            if (ct.IsCancellationRequested || Games.FirstOrDefault(s => s.Game.Id == id) is not { Engine: { Unsupported: null, Encrypted: false } engine, Game: var g }) return;
            name = g.Name;
            if (GameRunning(g)) return;
            using var own = CompileLock(id);
            if (own == null) return;   // a compile indexes it
            var rec = Store.LoadGame(id);
            if (Indexed(g, rec)) return;
            var index = Index(g, engine, rec, ct);
            Log?.Report($"{name}: {index.Shaders.Count:N0} shaders read from the game's files: the recorder records them by hash only");
            again = Indexed(g, rec) ? null : g;   // the exe changed during the read: its re-arm found this job running
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log?.Report($"{name}: couldn't read the game's shaders before recording: {e.Message}"); }
        finally
        {
            _recorderIndexes.TryRemove(id, out _);
            RequestCollect();
        }
        if (again != null) IndexForRecorder(again);
    }

    /// <summary>After indexing a build the recording wasn't checked against: shader bytes the index now has are dropped, and
    /// the keys file names this build's shaders and stops naming records whose shaders it no longer has.</summary>
    void CompactRecording(Game g, GameRecord rec, ShaderIndex index, CancellationToken ct = default)
    {
        var same = rec.RecordingIndexHash == index.ContentHash;
        if (same && rec.KeysIndexHash == index.ContentHash || Legacy(g.Id)) return;
        try
        {
            using (Recordings.Lock(RecordingPath(g.Id), ct: ct))
            {
                if (!same && File.Exists(RecordingPath(g.Id))) Recordings.Merge(RecordingPath(g.Id), null, index.Shaders.ContainsKey);
                rec.RecordingIndexHash = index.ContentHash;
                if (WriteKeys(g, rec) is > 0 and var gone)
                    Log?.Report($"{g.Name}: {gone} recorded pipelines name shaders this build doesn't ship: they're skipped when compiling, and recorded again if the game still creates them");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { Log?.Report($"{g.Name}: compacting the recording failed: {e.Message}"); }
    }

    /// <summary>The keys (as <see cref="PsoDb.Rec.Key"/>) of a recording's pipeline records: every record but the 'B' blobs,
    /// the 'N' NVAPI states, a community recording's 'L' flags and the 'W' pairs of a layer's records.</summary>
    static HashSet<string> PipelineKeys(string db) => PsoDb.Read(db).Where(r => r.Tag is not ('B' or 'N' or 'L' or 'W')).Select(r => r.Key).ToHashSet();

    /// <summary>The last scan's pass converting recordings stored before compact recordings (<see cref="MigrateRecordings"/>).</summary>
    public Task RecordingMigration { get; private set; } = Task.CompletedTask;

    void StartMigration(IEnumerable<GameState> states)
    {
        var games = states.Select(s => s.Game).Where(g => Legacy(g.Id)).ToList();
        lock (_scanLock)
            if (games.Count > 0 && RecordingMigration.IsCompleted) RecordingMigration = Task.Run(() => MigrateRecordings(games));
    }

    /// <summary>Once per game: its proxy-db copy becomes a compact one, the game folder's scskiller.db is imported into it
    /// and emptied, and recording.all.db goes. Never while a compile runs (it waits for the queue) or the game runs (the next
    /// scan resumes); the old file is replaced only once the new one reads back the same (<see cref="PsoDb.WriteCompact"/>).</summary>
    void MigrateRecordings(IReadOnlyList<Game> games)
    {
        foreach (var g in games)
        {
            while (true)
            {
                Task queue;
                lock (_lock)
                {
                    if (_current == null) break;
                    queue = _worker ?? Task.CompletedTask;
                }
                queue.ContinueWith(_ => { }).Wait(TimeSpan.FromSeconds(1));
            }
            if (GameRunning(g)) continue;
            try
            {
                // it writes the keys file, the ini and the record, as an install does: one at a time, or the install fails on a held ini
                lock (_recorderLock)
                {
                    var rec = Store.LoadGame(g.Id);
                    MigrationLoaded?.Invoke();
                    var before = RecordingFiles(g).Sum(Length);
                    if (!ImportRecording(g, rec, migrate: true)) continue;
                    Store.SaveGame(g.Id, rec);
                    Log?.Report($"{g.Name}: recording stored compactly ({before >> 20} MB -> {RecordingFiles(g).Sum(Length) >> 20} MB)");
                }
                Refresh(g);
            }
            catch (Exception e) { Log?.Report($"{g.Name}: storing the recording compactly failed: {e.Message}"); }
        }
    }

    public static bool IsOurProxy(string dll) => File.Exists(dll) && File.ReadAllBytes(dll).AsSpan().IndexOf(ProxyMarker) >= 0;

    static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f));
    }

    // Recorder skip reasons: short and stable, the Settings line counts games by them.
    public const string SkipAntiCheat = "anti-cheat", SkipForeignDll = "another d3d12.dll is already there",
        SkipModNotChainable = "another d3d12.dll is there that stops working renamed",
        SkipVulkanMod = "vkd3d-proton runs the game on Vulkan, whose pipelines a D3D12 warm doesn't compile",
        SkipNeedsAdmin = "the game folder needs administrator", SkipNotDx12 = "not DirectX 12", SkipUnsupported = "not supported yet",
        SkipPackageD3D12 = "a d3d12.dll in the game's package folder loads instead of the recorder",
        SkipShaderMod = "an HDR mod changes every pipeline", SkipStreamline = "DLSS on NVIDIA loads DirectX 12 before the recorder can",
        SkipManual = "game folder not confirmed";

    /// <summary>Why a game whose ReShade add-on adds to every root signature, in a layer a copy can't reproduce
    /// (<see cref="ReShadeInstall.Blocks"/>), isn't compiled: the case and its fix, one line.</summary>
    public static string ShaderModReason(string mod, LayerBlock why) => why switch
    {
        LayerBlock.Luma => $"Luma isn't supported with {mod} yet",
        LayerBlock.NotBesideExe => $"ReShade must be next to the game's exe to compile through {mod}",
        LayerBlock.OptiScalerOff => $"Set LoadReshade=true in OptiScaler.ini to compile through {mod}",
        LayerBlock.OptiScalerIni => $"Fix the section headers in OptiScaler.ini to compile through {mod}",
        _ => $"Rename ReShade to dxgi.dll next to the exe to compile through {mod}",
    };

    static string ShaderModReason(ReShadeInstall r) => ShaderModReason(r.ShaderMod!.Mod!, r.Block);

    /// <summary>The blocking install (<see cref="ReShadeInstall.Blocks"/>), read now; null = none, or an anti-cheat install (not read).</summary>
    static ReShadeInstall? BlockingMod(Game g) =>
        GameFiles.DetectAntiCheat(g, quick: true) == AntiCheat.None && ReShade.Detect(g) is { Blocks: true } r ? r : null;

    /// <summary>A ReShade add-on that changes the game's pipelines and doesn't block it (<see cref="GameState.ShaderMod"/>).</summary>
    public static string ShaderModNote(GameState s) => !s.ShaderModLayer ? $"{s.ShaderMod} replaces some shaders: those few pipelines aren't precompiled"
        : $"{s.ShaderMod} changes this game's pipelines: compiles run through a copy of ReShade and {s.ShaderMod}, and " +
        (s.ShaderModAsD3D12 ? "with ReShade installed as d3d12.dll, the ones it adds while the game draws are recorded only with Record alongside ReShade on"
            : "the ones it adds while the game draws are recorded as you play");

    /// <summary>The game's ReShade layer copied into the warm's work folder (<see cref="Warmer.Layer"/>), with what a warm
    /// through it depends on; null = no add-on that changes pipelines (or anti-cheat: nothing of the install is read).</summary>
    public (string Dir, string Fingerprint)? LayerFor(Game g, string workDir)
    {
        if (GameFiles.DetectAntiCheat(g) != AntiCheat.None || ReShade.Detect(g) is not { Fingerprint: { } fp } r) return null;
        return (ReShade.Stage(r, Path.Combine(workDir, "layer")), fp);
    }

    /// <summary>What a warm through the game's layer depends on now (<see cref="ReShadeInstall.Fingerprint"/>); null = none.</summary>
    string? LayerNow(Game g) => (Games.FirstOrDefault(s => s.Game.Id == g.Id)?.AntiCheat ?? AntiCheat.None) == AntiCheat.None ? ReShade.Detect(g)?.Fingerprint : null;

    /// <summary>Null = compatible. Writability is only known by writing (Reconcile remembers a refusal), except WindowsApps.
    /// <paramref name="modSkip"/>: <see cref="ModSkip"/>.</summary>
    public static string? RecorderSkip(GameState s, string? modSkip) =>
        s.AntiCheat != AntiCheat.None ? SkipAntiCheat   // any value but None, "Other" included
        : s.ShaderModBlocks ? SkipShaderMod
        : s.RootUnconfirmed ? SkipManual
        : s.StreamlineFirst ? SkipStreamline   // its status is Unsupported for this reason, not "not supported yet"
        : s.Engine == null || s.Status == GameStatus.Unsupported ? SkipUnsupported
        : !s.Engine.GraphicsApi.Contains("D3D12") ? SkipNotDx12   // the proxy is d3d12.dll; "D3D11 or D3D12" may run on it
        : modSkip != null ? modSkip   // ReShade, OptiScaler, another wrapper: never replaced, chained only when the user asks
        : s.Game.ExePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) ? SkipNeedsAdmin
        : null;

    /// <summary>The name a mod's d3d12.dll gets when the recorder chains to it (the proxy's scskiller.ini next=). It keeps the
    /// d3d12 prefix for a wrapper that reads its role from its own name by prefix (ReShade's d3d*).</summary>
    public const string ChainName = "d3d12.scskiller-next.dll";

    /// <summary>A mod's d3d12.dll in the recorder's place (or chained to it): null = none, or the user chose to record alongside
    /// it (<see cref="GameRecord.RecordAlongsideMod"/>) and it can be chained; else the skip reason. Turning the choice off
    /// while chained gives a skip, so Reconcile removes the recorder and puts the mod back. Any d3d12.dll in an Xbox app
    /// game's package root loads before the exe's folder's: <see cref="SkipPackageD3D12"/>.</summary>
    static string? ModSkip(Game g, string dir, GameRecord rec, bool ours)
    {
        if (ReShade.PackageRoot(g) is { } root && !root.Equals(GameFiles.DirKey(dir), StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(root, "d3d12.dll"))) return SkipPackageD3D12;
        var dll = Path.Combine(dir, "d3d12.dll");
        var mod = rec.RecorderChained is { } c ? Path.Combine(dir, c.Name) : !ours && File.Exists(dll) ? dll : null;
        return mod == null ? null : !rec.RecordAlongsideMod ? SkipForeignDll
            : ChainBlocker(mod) switch { null => null, SkipVulkanMod => SkipVulkanMod, _ => SkipModNotChainable };
    }

    /// <summary>Why a mod's d3d12.dll can't be chained (renamed to <see cref="ChainName"/>); null = it can. OptiScaler and
    /// Special K pick their role from their own file name, so renamed they'd stop working. vkd3d-proton (its own strings
    /// name it): <see cref="SkipVulkanMod"/>.</summary>
    public static string? ChainBlocker(string dll)
    {
        string? export, product;
        bool vkd3d;
        try
        {
            (export, product) = (Middleware.ExportName(dll), FileVersionInfo.GetVersionInfo(dll).ProductName);
            vkd3d = File.ReadAllBytes(dll).AsSpan().IndexOf("vkd3d-proton"u8) >= 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"{Path.GetFileName(dll)} can't be read"; }
        foreach (var n in new[] { export, product })
            if (n != null && new[] { "OptiScaler", "SpecialK", "Special K" }.Any(m => n.StartsWith(m, StringComparison.OrdinalIgnoreCase)))
                return $"{n} picks its role from its file name";
        return vkd3d ? SkipVulkanMod : null;
    }

    /// <summary>Why the mod's d3d12.dll (<see cref="GameState.RecorderMod"/>) can't be chained, as the game's page says it
    /// (<see cref="SkipModNotChainable"/>). OptiScaler picks its role from its file name (renamed, it does nothing) and works
    /// under the other names it supports, beside the recorder.</summary>
    public static string NotChainableReason(string? mod) =>
        mod?.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase) == true
            ? "OptiScaler is installed as d3d12.dll, and renamed it stops working (it picks its role from its file name). To record alongside it, rename it to dxgi.dll (or winmm.dll or version.dll if a dxgi.dll is already there): OptiScaler supports those names"
            : $"{mod ?? "another mod"} is installed as d3d12.dll, and the recorder can't run alongside it: renamed it stops working";

    /// <summary>What the game folder's d3d12.dll that isn't SCSKiller's (or the one chained to it) calls itself, for "Record
    /// alongside &lt;mod&gt;"; null = none there.</summary>
    public string? RecorderMod(string gameId)
    {
        var dir = Path.GetDirectoryName(Find(gameId).Game.ExePath)!;
        return ModName(dir, Store.LoadGame(gameId), IsOurProxy(Path.Combine(dir, "d3d12.dll")));
    }

    static string? ModName(string dir, GameRecord rec, bool ours)
    {
        var dll = Path.Combine(dir, rec.RecorderChained?.Name ?? "d3d12.dll");
        if (!File.Exists(dll) || rec.RecorderChained == null && ours) return null;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(dll);
            return new[] { v.ProductName, v.FileDescription, Middleware.ExportName(dll) }.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim() ?? "d3d12.dll";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "d3d12.dll"; }
    }

    public void SetRecordAlongsideMod(string gameId, bool on)
    {
        lock (_recorderLock)
        {
            var s = Find(gameId);
            if (Unconfirmed(s.Game) && on) return;   // not recorded before its folder is confirmed: nothing of the recorder is saved for it
            var rec = Store.LoadGame(gameId);
            rec.RecordAlongsideMod = on;
            Store.SaveGame(gameId, rec);
            if (!Reconcile(s)) Refresh(s.Game);
        }
    }

    public static bool RecorderEffective(RecorderOverride o, bool recordAllGames, string? skip) =>
        skip == null && (o == RecorderOverride.On || (o == RecorderOverride.Default && recordAllGames));

    readonly object _recorderLock = new();
    // game id -> SkipNeedsAdmin (the last write was refused); game id -> the last reconcile's pending or failed change
    readonly ConcurrentDictionary<string, string> _recorderSkips = new(), _recorderNotes = new();

    public void InstallRecorder(string gameId)
    {
        SetRecorderOverride(gameId, RecorderOverride.On);
        var s = Find(gameId);
        if (!s.RecorderInstalled || s.RecorderNote != null)   // installed with a note: an update waits or failed
            throw new InvalidOperationException($"{s.Game.Name}: recorder not {(s.RecorderInstalled ? "updated" : "installed")}: {s.RecorderNote ?? s.RecorderSkip}");
    }

    public bool ClearRecording(string gameId)
    {
        var s = Find(gameId);
        lock (_lock)
            if (_current == gameId) throw new InvalidOperationException($"a compile of {s.Game.Name} is in progress");
        lock (_recorderLock)
        using (Recordings.Lock(RecordingPath(gameId)))   // a migration or import mid-write would put the recording back
        {
            if (!GameFolderWrite(s.Game))
                throw new InvalidOperationException($"{s.Game.Name} is running");
            var files = RecordingFiles(s.Game).Select(f => new FileInfo(f)).Where(f => f.Exists).ToList();
            if (files.Count == 0) return false;
            AppCacheFiles.DeleteAll(files);   // all or none; throws "files in use by <process>"
            var rec = Store.LoadGame(gameId);
            // a changed recording: the next compile re-plans, and the scan's planner check runs again without it
            (rec.RecordingImportedAt, rec.RecordingInbox, rec.RecordingIndexHash, rec.RecordedLong) = (DateTimeOffset.Now, null, null, false);
            rec.LastPlay = null;   // its log is gone with the rest: NeverSaw would take the run for one the recorder never saw
            WriteKeys(s.Game, rec);   // it named the deleted copy's blobs and records
            Store.SaveGame(gameId, rec);
            RecorderLog($"{s.Game.Name}: recording cleared ({string.Join(", ", files.Select(f => f.FullName))})");
            UpdateRecorderIni(s.Game, rec);
            Refresh(s.Game);
            return true;
        }
    }

    public void UninstallRecorder(string gameId)
    {
        SetRecorderOverride(gameId, RecorderOverride.Off);
        if (Find(gameId) is { RecorderInstalled: true } s)
            throw new InvalidOperationException($"{s.Game.Name}: recorder not removed: {s.RecorderNote}");
    }

    ManualSource? Manual => _sources.OfType<ManualSource>().FirstOrDefault();

    // games the user removed: a scan or refresh that read them before the removal doesn't list them again (under _lock)
    readonly HashSet<string> _removed = [];

    /// <summary>A game the user added whose folder isn't the one they confirmed, or that has none confirmed yet.</summary>
    bool Unconfirmed(Game g) => g.Store == Core.Store.Manual && Manual?.Confirmed(g) != true;

    /// <summary>Tests: runs during a folder change, after the old attestation is revoked and before the new folder is saved.</summary>
    internal Action? FolderChanging { get; set; }

    /// <summary>A game added by hand as discovered before the user changed its folder.</summary>
    bool StaleCopy(Game g) => g.Store == Core.Store.Manual && Manual?.Stale(g) == true;

    /// <summary>A store's game the exe belongs to: its own exe, or one inside its install.</summary>
    GameState? Listed(string exe) => Games.FirstOrDefault(s => s.Game.Store != Core.Store.Manual
        && (string.Equals(s.Game.ExePath, exe, StringComparison.OrdinalIgnoreCase) || GameFiles.Inside(s.Game.InstallDir, exe)));

    public ManualAdd PreviewManualGame(string exePath)
    {
        if (Manual == null) throw new InvalidOperationException("adding games isn't available here");
        // a store's game first: its launcher may be what Resolve can't tell apart
        if (Listed(Path.GetFullPath(exePath)) is { } owner) return new(owner.Game, true);
        var entry = ManualSource.Resolve(exePath);
        if (Listed(entry.Exe) is { } listed) return new(listed.Game, true);
        return Manual.Entries().FirstOrDefault(e => ManualSource.IdOf(e.Exe) == ManualSource.IdOf(entry.Exe)) is { } had
            ? new(ManualSource.ToGame(had), true) : new(ManualSource.ToGame(entry), false);
    }

    public string? ManualFolderProblem(string exePath, string installDir) =>
        ManualSource.RootProblem(installDir, exePath, Games.Select(s => s.Game));

    public ManualAdd AddManualGame(string exePath, string? installDir = null)
    {
        var manual = Manual ?? throw new InvalidOperationException("adding games isn't available here");
        if (Listed(Path.GetFullPath(exePath)) is { } owner) return new(owner.Game, true);
        // an added game's own exe keeps its entry: resolved again, a patch's bigger exe would make another game
        var entry = manual.Entries().FirstOrDefault(e => string.Equals(e.Exe, Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
            ?? ManualSource.Resolve(exePath);
        if (Listed(entry.Exe) is { } listed) return new(listed.Game, true);
        if (installDir != null)
        {
            if (ManualFolderProblem(entry.Exe, installDir) is { } why) throw new ArgumentException(why);
            entry = entry with { InstallDir = GameFiles.DirKey(installDir), Confirmed = true };
        }
        ManualAdd added;
        lock (_recorderLock)
        {
            // another folder than the one checked: no arming from the first step until the new folder is saved and published
            // (a check that starts meanwhile still sees the old one), a change counted (one under way is refused), the old
            // attestation revoked, then the new folder saved
            var id = ManualSource.IdOf(entry.Exe);
            var had = installDir != null ? manual.Entries().FirstOrDefault(e => ManualSource.IdOf(e.Exe) == id) : null;
            var changing = had != null && !GameFiles.DirKey(had.InstallDir).Equals(entry.InstallDir, StringComparison.OrdinalIgnoreCase);
            if (changing) _disarmWork.AddOrUpdate(id, 1, (_, n) => n + 1);
            try
            {
                if (changing)
                {
                    CountChange(id);
                    Disarm(ManualSource.ToGame(had!));
                    FolderChanging?.Invoke();
                }
                var (game, existed) = manual.Add(entry);
                added = new(game, existed);
            }
            finally { if (changing) _disarmWork.AddOrUpdate(id, 0, (_, n) => n - 1); }
        }
        lock (_lock) _removed.Remove(added.Game.Id);
        Log?.Report($"{added.Game.Name}: {(added.Existed ? "game folder set" : "added to the library")} ({added.Game.ExePath}, "
            + $"{(entry.Confirmed ? "confirmed" : "suggested")} folder {added.Game.InstallDir})");
        if (added.Existed && entry.Confirmed && Games.Any(s => s.Game.Id == added.Game.Id))
        {
            Refresh(added.Game);   // a new evaluation (its folder is in the scan's key): the full check covers the confirmed folder
            if (ManageRecorders) ReconcileRecorders(added.Game.Id);
        }
        return added;
    }

    /// <summary>Under the game's compile.lock, so no compile of it runs in this process or another meanwhile. A game the user
    /// added never gets the recorder; one a development build gave it is taken out first (refused while the game runs),
    /// else nothing in its folder is touched.</summary>
    public void RemoveManualGame(string gameId)
    {
        var manual = Manual;
        var g = Games.FirstOrDefault(s => s.Game.Id == gameId)?.Game ?? manual?.Entries().Select(ManualSource.ToGame).FirstOrDefault(x => x.Id == gameId);
        if (manual == null || g is not { Store: Core.Store.Manual }) throw new InvalidOperationException("only a game you added can be removed from the library");
        using var own = CompileLock(gameId) ?? throw new InvalidOperationException($"a compile of {g.Name} is in progress");
        lock (_recorderLock)
        {
            var rec = Store.LoadGame(gameId);
            var dirs = new[] { rec.RecorderExe ?? rec.RecorderMoveFrom, g.ExePath }.OfType<string>().Select(Path.GetDirectoryName).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
            bool Left(string dir) => IsOurProxy(Path.Combine(dir, "d3d12.dll")) || RecorderDataFiles.Concat(Recordings.AppFiles).Any(f => File.Exists(Path.Combine(dir, f)));
            if (rec.RecorderFiles.Count > 0 || rec.RecorderChained != null || dirs.Any(Left))
            {
                if (GameRunning(g)) throw new InvalidOperationException($"{g.Name} is running: close the game first");
                foreach (var dir in dirs)   // as SCSKiller's own uninstall (RemoveAllRecorders)
                {
                    RemoveRecorder(dir, rec, g.Name, RecorderLog);
                    Store.SaveGame(gameId, rec);
                    if (!RemoveRecorderData(dir, RecordingPath(gameId), rec.RecordingInbox, () => GameRunning(g)))
                        throw new InvalidOperationException($"{g.Name} started: close the game first");
                }
                (rec.RecorderMoveFrom, rec.RecorderMoveTo, rec.RecorderInstallDir) = (null, null, null);
                Store.SaveGame(gameId, rec);
                RecorderLog($"{g.Name}: recorder removed before the game left the library ({string.Join(", ", dirs)})");
            }
        }
        var dropped = new List<QueueItem>();
        lock (_lock)
        {
            if (_current == gameId) throw new InvalidOperationException($"a compile of {g.Name} is in progress");
            _whenIdle.Remove(gameId);
            _planOnly.Remove(gameId);
            _prompt.Remove(gameId);
            dropped.AddRange(_queue.Where(q => q.GameId == gameId));
            _queue.RemoveAll(q => q.GameId == gameId);
            _removed.Add(gameId);
            _games.RemoveAll(s => s.Game.Id == gameId);
        }
        manual.Remove(gameId);
        Log?.Report($"{g.Name}: removed from the library");
        foreach (var q in dropped) QueueChanged?.Invoke(q);
    }

    public void SetRecorderOverride(string gameId, RecorderOverride value)
    {
        lock (_recorderLock)
        {
            var s = Find(gameId);
            if (Unconfirmed(s.Game) && value == RecorderOverride.On) return;   // not before its folder is confirmed; Off takes one out
            var rec = Store.LoadGame(gameId);
            rec.Recorder = value;
            Store.SaveGame(gameId, rec);
            if (!Reconcile(s)) Refresh(s.Game);   // the override shows either way
        }
    }

    public void ReconcileRecorders(string? gameId = null)
    {
        lock (_recorderLock)
            foreach (var s in Games.Where(s => gameId == null || s.Game.Id == gameId))
                Reconcile(s);
    }

    /// <summary>True when the game was re-evaluated; a running game's change waits for it to exit.</summary>
    bool Reconcile(GameState s)
    {
        var (g, id) = (s.Game, s.Game.Id);
        if (OfflineLive(id) || _unread.ContainsKey(id)) return false;   // its own end takes it out; a failed evaluation changes nothing
        var dir = Path.GetDirectoryName(g.ExePath)!;
        var dll = Path.Combine(dir, "d3d12.dll");
        var rec = Store.LoadGame(id);
        bool ours = IsOurProxy(dll), changed = false;
        // a recorder next to another exe than the game's (its launcher) is never loaded: it moves. Both folders are
        // recorded first, before anything waits for the game (a removal pending too), so the uninstall hook guards both;
        // kept until the old folder's recording is imported and its files are gone
        var was = rec.RecorderMoveFrom ?? rec.RecorderExe;
        var wasDir = was == null ? null : Path.GetDirectoryName(was);
        bool move = wasDir != null && !wasDir.Equals(dir, StringComparison.OrdinalIgnoreCase) && Directory.Exists(wasDir)
            && (rec.RecorderMoveFrom != null || rec.RecorderFiles.Count > 0 || rec.RecorderChained != null || IsOurProxy(Path.Combine(wasDir, "d3d12.dll")));
        if (move && (rec.RecorderMoveFrom == null || rec.RecorderMoveTo != g.ExePath))
        {
            (rec.RecorderMoveFrom, rec.RecorderMoveTo) = (was, g.ExePath);
            Store.SaveGame(id, rec);
        }
        if (rec.RecorderRollback || _rollbacks.ContainsKey(id))   // a failed install's leftovers go first, before any save, switch On or not
        {
            var left = GameRunning(g) ? "removed when the game exits"
                : TakeOut(g, rec, "a failed install's recorder") ? null : "couldn't remove: the game holds it";
            if (left == null) _recorderNotes.TryRemove(id, out _); else _recorderNotes[id] = left;
            Refresh(g);
            return true;
        }
        if (rec.Recorder == null)   // not migrated: a recorder the user installed stays theirs
        {
            rec.Recorder = ours ? RecorderOverride.On : RecorderOverride.Default;
            Store.SaveGame(id, rec);
            changed = true;
        }
        bool moved = false;
        if (move)
        {
            var old = RecordedAt(g, rec);
            string? left = null;
            _moving[id] = g;   // until the move is done, every check on the old folder (after each lock wait too) takes in the new one
            try
            {
                if (GameRunning(old)) left = "moves when the game exits";   // also after the record's save waited for its lock
                else
                {
                    if (s.AntiCheat == AntiCheat.None) MoveSessionFiles(wasDir, dir);   // nothing of ours into an anti-cheat game's folder
                    if (!Uninstall(old, rec))
                        left = GameRunning(old) ? "moves when the game exits" : $"couldn't move: the recording in {wasDir} couldn't be imported";
                }
                if (left == null)
                {
                    (rec.RecorderMoveFrom, rec.RecorderMoveTo) = (null, null);
                    Store.SaveGame(id, rec);
                    RecorderLog($"{g.Name}: recorder removed ({wasDir}): the game runs {g.ExePath}");
                    (ours, moved, changed) = (IsOurProxy(dll), true, true);
                    // the old folder's watcher would take the install's files in the new one for a change and keep it unarmed
                    if (WatchKey(old) != WatchKey(g))
                        lock (_installWatchers)
                            if (_installWatchers.Remove(WatchKey(old), out var w)) Unwatch(w);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                left = "couldn't move: " + e.Message;
                RecorderLog($"{g.Name}: recorder {left}");
            }
            finally { _moving.TryRemove(id, out _); }
            if (left != null)
            {
                if (left != _recorderNotes.GetValueOrDefault(id)) (_recorderNotes[id], changed) = (left, true);
                if (changed) Refresh(g);
                return changed;
            }
        }
        else if (rec.RecorderMoveFrom != null)   // its folder is gone, or the game's exe is back in it
        {
            (rec.RecorderMoveFrom, rec.RecorderMoveTo) = (null, null);
            if (rec.RecorderExe == null) rec.RecorderInstallDir = null;
            Store.SaveGame(id, rec);
        }
        if ((ours || rec.RecorderFiles.Count > 0 || rec.RecorderChained != null) && (rec.RecorderExe != g.ExePath || rec.RecorderInstallDir != g.InstallDir))
        {
            (rec.RecorderExe, rec.RecorderInstallDir) = (g.ExePath, g.InstallDir);
            Store.SaveGame(id, rec);
        }
        // a recorder kept as it is gets no Install and its checks: it rests on a full check now, not on the scan's verdict
        // arming order: the watcher, then the change count, then the full check; a change during it keeps it unarmed
        long gen = 0;
        bool clean = false;
        if (s.AntiCheat == AntiCheat.None && (ours || rec.RecorderFiles.Count > 0 || rec.RecorderChained != null))
        {
            WatchOrDisarm(g);
            gen = InstallGen(g);
            if (FullCheckOnChange(g) is not AntiCheat.None and var found)
            {
                AntiCheatFound(g, found);   // takes it out, or when the game exits
                Refresh(g);
                return true;
            }
            clean = true;
        }
        bool want = RecorderEffective(rec.Recorder.Value, Settings.RecordAllGames && s.NoStutter == null, RecorderSkip(s, ModSkip(g, dir, rec, ours)));
        // another SCSKiller build's proxy, never a newer one's (a release's under a dev build, all 0.0.0.0)
        bool update = want && ours && ProxySha() is { } sha && Sha256(dll) != sha && FileVersion(dll) <= FileVersion(_proxyDll!);
        string? note = null;
        bool reinstall = want && moved && (!ours || ProxySha() != null && FileVersion(dll) <= FileVersion(_proxyDll!));   // as update: never over a newer build's
        if (want != ours || update || reinstall || (!want && (rec.RecorderFiles.Count > 0 || rec.RecorderChained != null)))
        {
            if (GameRunning(g))
                note = update ? "updates when the game exits" : want ? "installs when the game exits" : "removed when the game exits";
            else
                try
                {
                    if (want)
                    {
                        WatchOrDisarm(g);
                        gen = InstallGen(g);
                        Install(g, rec);
                        Arm(g, gen);   // Install's full checks were clean
                    }
                    else Uninstall(g, rec);
                    _recorderSkips.TryRemove(id, out _);
                    RecorderLog($"{g.Name}: recorder {(update ? "updated" : want ? "installed" : "removed")} ({dir})");
                    changed = true;
                }
                catch (UnauthorizedAccessException e) when (update)   // the old recorder still works: not a skip
                {
                    note = "couldn't update: " + e.Message;
                    RecorderLog($"{g.Name}: recorder {note}");
                }
                catch (UnauthorizedAccessException)
                {
                    if (_recorderSkips.GetValueOrDefault(id) != SkipNeedsAdmin)   // logged once, re-tried on every reconcile
                    {
                        _recorderSkips[id] = SkipNeedsAdmin;
                        RecorderLog($"{g.Name}: recorder not installed: {SkipNeedsAdmin} ({dir})");
                        changed = true;
                    }
                }
                catch (Exception e) when (e is IOException or InvalidOperationException)
                {
                    note = (update ? "couldn't update: " : want ? "couldn't install: " : "couldn't remove: ") + e.Message;
                    RecorderLog($"{g.Name}: recorder {note}");
                }
        }
        else if (want && !GameRunning(g))
        {
            if (rec.KeysPending)   // a rewrite that waited for the game to exit
                try
                {
                    WriteKeys(g, rec);
                    Store.SaveGame(id, rec);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecorderLog($"{g.Name}: couldn't write the keys file: {e.Message}"); }
            UpdateRecorderIni(g, rec);
            if (clean) Arm(g, gen);
        }
        // no recorder of ours here: what one left (an earlier version's recorder off, a file that was locked) goes
        else if (!want && !ours && rec.RecorderExe == null && RecorderDataFiles.Concat(Recordings.AppFiles).Any(f => File.Exists(Path.Combine(dir, f)))
                 && !GameRunning(g))
            try
            {
                if (RemoveRecorderData(dir, RecordingPath(id), rec.RecordingInbox, () => GameRunning(g)))
                {
                    RecorderLog($"{g.Name}: the recorder's data files removed ({dir})");
                    changed = true;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }   // the next scan tries again
        if (note != _recorderNotes.GetValueOrDefault(id))
        {
            if (note == null) _recorderNotes.TryRemove(id, out _); else _recorderNotes[id] = note;
            changed = true;
        }
        if (changed) Refresh(g);
        return changed;
    }

    static readonly string[] SessionFiles = ["scskiller_creates.csv", "scskiller.log", FrameLog.FileName];

    /// <summary>The last session's csv, log and frame log go along with a recorder that moves, so the game page keeps its
    /// report; of two with the same name the newer by write time stays.</summary>
    static void MoveSessionFiles(string from, string to)
    {
        foreach (var name in SessionFiles)
        {
            var src = new FileInfo(Path.Combine(from, name));
            if (!src.Exists) continue;
            var dst = new FileInfo(Path.Combine(to, name));
            if (dst.Exists && dst.LastWriteTimeUtc >= src.LastWriteTimeUtc) src.Delete();
            else src.MoveTo(dst.FullName, overwrite: true);
        }
    }

    /// <summary>Also to recorders.log in the data folder: the app has no other log.</summary>
    void RecorderLog(string line)
    {
        Log?.Report(line);
        RecordersLog(Store.DataDir, line);
    }


    static void RecordersLog(string dataDir, string line) => AppendLog(dataDir, "recorders.log", line);

    // lines come from background tasks, the CLI and the cleanup helper's process at once, maybe in another session (a portable
    // data folder): one writer at a time per log, so a rotation never races an append or another rotation; an unshared append
    // loses one to a sharing violation. maxBytes > 0: a file that reached it becomes <file>.1 (the one before goes), so the log
    // keeps two files at most. Never throws: without the lock (another user's, or not taken in time) it appends, unrotated.
    public static void AppendLog(string dataDir, string file, string line, long maxBytes = 0)
    {
        Mutex? gate = null;
        var held = false;
        try
        {
            var path = Path.GetFullPath(Path.Combine(dataDir, file));
            try
            {
                gate = new Mutex(false, LogMutexName(path));
                try { held = gate.WaitOne(TimeSpan.FromSeconds(1)); }
                catch (AbandonedMutexException) { held = true; }   // a writer died holding it: the file is whole, line by line
            }
            catch (Exception) { }
            Directory.CreateDirectory(dataDir);   // a first start: nothing made the data folder yet
            if (held && maxBytes > 0 && new FileInfo(path) is { Exists: true } full && full.Length >= maxBytes)
                try { File.Move(path, path + ".1", overwrite: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // a reader holds it: appended to
            for (var attempt = 1; ; attempt++)
                try
                {
                    using var f = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    f.Write(System.Text.Encoding.UTF8.GetBytes($"{DateTimeOffset.Now:u} {line}{Environment.NewLine}"));
                    break;
                }
                catch (IOException) when (attempt < 5) { Thread.Sleep(20); }   // a reader that shares no writes holds it a moment
        }
        catch (Exception) { }   // a log line never fails what it logs
        finally
        {
            if (held)
                try { gate!.ReleaseMutex(); }
                catch (Exception) { }
            gate?.Dispose();
        }
    }

    internal static string LogMutexName(string path) =>
        @"Global\SCSKiller.Log." + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..32];

    string? _proxySha;
    string? ProxySha() => _proxyDll == null || !File.Exists(_proxyDll) ? null : _proxySha ??= Sha256(_proxyDll);

    static Version FileVersion(string path) =>
        FileVersionInfo.GetVersionInfo(path) is var v ? new(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart) : new();

    /// <summary>Running processes' names without the extension, from the snapshot: none opened.</summary>
    static IReadOnlySet<string> RunningProcessNames() =>
        ProcessTree.Snapshot().Select(p => Path.GetFileNameWithoutExtension(p.Exe)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    void Install(Game g, GameRecord rec)
    {
        RefuseAntiCheat(g, rec);   // fresh, not the cached scan: a patch may have added one
        if (!GameFolderWrite(g)) throw new InvalidOperationException($"{g.Name} is running");
        var src = _proxyDll ?? throw new FileNotFoundException("the proxy d3d12.dll was not found next to the app");
        var dir = Path.GetDirectoryName(g.ExePath)!;
        var dll = Path.Combine(dir, "d3d12.dll");
        var ini = Path.Combine(dir, "scskiller.ini");
        bool iniOurs = !File.Exists(ini) || (rec.RecorderFiles.TryGetValue("scskiller.ini", out var h) && h == Sha256(ini));
        bool chain = File.Exists(dll) && !IsOurProxy(dll);
        (rec.RecorderExe, rec.RecorderInstallDir) = (g.ExePath, g.InstallDir);
        if (chain)
        {
            if (!rec.RecordAlongsideMod)
                throw new InvalidOperationException($"{dll} already exists and is not SCSKiller's (another mod or wrapper). Not replacing it.");
            if (ChainBlocker(dll) is { } why) throw new InvalidOperationException($"{dll}: {why}. Not renaming it.");
            if (!iniOurs) throw new InvalidOperationException($"{ini} is not SCSKiller's: the chain to {dll} needs its own. Not renaming it.");
            var to = Path.Combine(dir, ChainName);
            if (File.Exists(to)) throw new InvalidOperationException($"{to} already exists. Not renaming {dll} over it.");
            rec.RecorderChained = new ChainedDll(ChainName, Sha256(dll));
            InstallStep?.Invoke("chain");
            Store.SaveGame(g.Id, rec);   // before the rename: no renamed file is ever missing from the manifest
            if (!GameFolderWrite(g))   // it may have started while the save waited for the record's lock
            {
                rec.RecorderChained = null;
                Store.SaveGame(g.Id, rec);
                throw new InvalidOperationException($"{g.Name} is running");
            }
            File.Move(dll, to);
        }
        InstallStep?.Invoke("copy");
        if (!GameFolderWrite(g))
        {
            if (chain)   // this attempt's rename (an update keeps the chain it found): put back after the game exits
            {
                (rec.RecorderRollback, _rollbacks[g.Id]) = (true, true);
                Store.SaveGame(g.Id, rec);
            }
            throw new InvalidOperationException($"{g.Name} is running");
        }
        try { Place(dll, temp => File.Copy(src, temp, overwrite: true), replace: true); }
        catch when (chain)
        {
            File.Move(Path.Combine(dir, ChainName), dll);
            rec.RecorderChained = null;
            Store.SaveGame(g.Id, rec);
            throw;
        }
        // from here on the proxy is in the game folder: whatever happens, the full check runs and the manifest is saved
        var antiCheat = AntiCheat.None;
        Exception? failed = null;
        try
        {
            rec.RecorderFiles["d3d12.dll"] = Sha256(dll);
            InstallStep?.Invoke("copied");
            antiCheat = GameFiles.DetectAntiCheat(g, quick: true);   // an update may have added one since the check, likely next to the exe
            if (antiCheat == AntiCheat.None && !GameFolderWrite(g)) throw new InvalidOperationException($"{g.Name} started while the recorder was installed");
            if (antiCheat == AntiCheat.None && iniOurs)
            {
                Place(ini, temp => File.WriteAllText(temp, IniText(g, rec)), replace: true);
                rec.RecorderFiles["scskiller.ini"] = Sha256(ini);
            }   // else: the user's own scskiller.ini, left alone and not tracked
        }
        catch (Exception e) { failed = e; }
        // the full check before anything that may wait for a lock (the keys file's, the record's)
        if (antiCheat == AntiCheat.None) antiCheat = GameFiles.DetectAntiCheat(g);
        if (antiCheat == AntiCheat.None && failed == null)
            try
            {
                InstallStep?.Invoke("keys");
                WriteKeys(g, rec);   // a recorder installed again records only what's new
                Store.SaveGame(g.Id, rec);
                // last, after every wait: an update may have added anti-cheat while this one waited for a lock
                antiCheat = GameFiles.DetectAntiCheat(g);
                if (antiCheat == AntiCheat.None)
                {
                    lock (_scanLock) _scan?.Remove(g.Id);   // the next refresh evaluates the game again, its anti-cheat too
                    return;
                }
            }
            catch (Exception e) { failed = e; }
        if (antiCheat != AntiCheat.None)
        {
            AntiCheatFound(g, antiCheat, installing: rec);
            throw AntiCheatRefusal(g, antiCheat);
        }
        TakeOut(g, rec, $"the recorder install failed ({failed!.Message})");
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
    }

    /// <summary>Whether SCSKiller may write into the game's folder now: not while the game runs (<see cref="GameRunning"/>;
    /// the staged warm runs under its name too). Every write there checks it: the proxy's copy (<see cref="Install"/>),
    /// <see cref="TakeOut"/>, the keys file (<see cref="WriteKeys"/>), the ini (<see cref="UpdateRecorderIni"/>), the
    /// inbox's rotation (<see cref="ImportRecording"/>), <see cref="ClearRecording"/> and the reconcile's install and
    /// removal; the uninstall hook uses <see cref="Running"/>, the same check without the watcher.</summary>
    bool GameFolderWrite(Game g) => !GameRunning(g);

    /// <summary>The one answer to "is this game running" before touching it: the watcher sees it playing (which uses
    /// <see cref="RunsFromItsFolder"/> too), or <see cref="Running"/> (the uninstall hook's check) with
    /// <see cref="ProcessNames"/> (replaceable for tests), less the names only programs elsewhere run under
    /// (<see cref="OnlyElsewhere"/>). No process is opened.</summary>
    bool GameRunning(Game g) => IsPlaying(g.Id) || Running(g.ExePath, g.InstallDir, ProcessNames(), OnlyElsewhere)
                                || _moving.TryGetValue(g.Id, out var to) && to != g && Running(to.ExePath, to.InstallDir, ProcessNames(), OnlyElsewhere);

    /// <summary>Every running process named <paramref name="name"/> runs an exe outside <paramref name="roots"/>, read from
    /// the system's process list (<see cref="ProcessPath"/>): another program named like an exe of the game's folders, which
    /// may run all the time (Gaijin's gjagent, also in War Thunder's root). A path that can't be told counts as inside.</summary>
    bool OnlyElsewhere(string name, string?[] roots) =>
        Processes(true).Where(p => Path.GetFileNameWithoutExtension(p.Exe).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } named
        && named.All(p => Outside(p.Pid, roots));

    /// <summary>The process's image is known to be outside every root, also as each root resolves through junctions and
    /// symlinks (the image path is the resolved one). A path or root it can't tell: not outside.</summary>
    bool Outside(int pid, string?[] roots)
    {
        if (ProcessPath(pid) is not { } path) return false;
        foreach (var root in roots.OfType<string>().Where(Directory.Exists))
            if (GameFiles.Inside(root, path) || GameFiles.FinalPath(root) is not { } real || GameFiles.Inside(real, path)) return false;
        return true;
    }

    /// <summary>Game id -> the game where a recorder moves to: while it moves, every check on the old folder takes in the new.</summary>
    readonly ConcurrentDictionary<string, Game> _moving = new();

    /// <summary>The game where its recorder is recorded to be (<see cref="GameRecord.RecorderMoveFrom"/>, else
    /// <see cref="GameRecord.RecorderExe"/>), which may be another exe than discovery's.</summary>
    static Game RecordedAt(Game g, GameRecord rec) =>
        (rec.RecorderMoveFrom ?? rec.RecorderExe) is { } exe ? g with { ExePath = exe, InstallDir = rec.RecorderInstallDir ?? g.InstallDir } : g;

    /// <summary>The watcher's folder check: it only shows what plays, so a snapshot up to a second old will do, and a staged
    /// warm (<see cref="OurCopy"/>, named like the game) isn't the game playing. Write guards count it all the same.
    /// A process named like another exe of the folders counts only if it runs from them (<see cref="Outside"/>).</summary>
    bool RunsFromItsFolder(Game g)
    {
        string?[] roots = [g.InstallDir, Path.GetDirectoryName(g.ExePath)];
        if (ExeNamesIn(roots) is not { } names) return true;   // can't tell: as if it runs
        if (names.Count == 0) return false;
        var all = Processes(false);
        var exe = Path.GetFileName(g.ExePath);
        return all.Any(p => names.Contains(Path.GetFileNameWithoutExtension(p.Exe)) && !OurCopy(p, all)
                            && (p.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase) || !Outside(p.Pid, roots)));
    }

    /// <summary>A process SCSKiller runs under a game's exe name: a staged warm, which runs from a game's work\stage-*
    /// folder (scskiller_warm.exe stages it there). Told by its image path, read without opening it, so a process the
    /// staged copy starts from its own image, one left after its parent exited, or one whose parent's id Windows reused or
    /// a packaged app's activator started tells the same. Only when the path can't be read (it exited, or no drive letter
    /// maps its volume) its parent tells: scskiller_warm.exe.</summary>
    bool OurCopy((int Pid, int Parent, string Exe) p, List<(int Pid, int Parent, string Exe)> all)
    {
        if (ProcessPath(p.Pid) is not { } path)
            return all.Any(w => w.Pid == p.Parent && w.Exe.Equals("scskiller_warm.exe", StringComparison.OrdinalIgnoreCase));
        var games = Path.Combine(Store.DataDir, "games");
        return Staged(games, path) || GameFiles.FinalPath(games) is { } real && Staged(real, path);

        static bool Staged(string games, string path) => GameFiles.Inside(games, path)
            && Path.GetRelativePath(games, path).Split(Path.DirectorySeparatorChar) is [_, var work, var stage, _, ..]
            && work.Equals("work", StringComparison.OrdinalIgnoreCase) && stage.StartsWith("stage-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The names (without extensions) of the exes in <paramref name="dirs"/>, which a running game's process may be
    /// named like (a game a launcher started under another exe name too). Null when a folder can't be listed.</summary>
    static HashSet<string>? ExeNamesIn(string?[] dirs)
    {
        try
        {
            return dirs.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.exe")).Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Takes out what a failed install placed or an anti-cheat game holds: our proxy (by its marker, whether the
    /// manifest was saved or not), the files the manifest names, the keys file, and puts a chained mod back. False when it
    /// can't go yet (the game runs, or holds the proxy): pending (<see cref="GameRecord.RecorderRollback"/>, and in memory in
    /// case the record can't be saved), and the next reconcile does it first. The manifest's folder goes first (where the
    /// record says the recorder is, <see cref="RecordedAt"/>), then the exe's, if it's another, for a proxy of ours there.</summary>
    bool TakeOut(Game g, GameRecord rec, string cause)
    {
        // a recorder by another exe than the game's: its folder stays recorded (a move) so its recording is imported and its
        // data files go, which the removal leaves
        if (rec.RecorderMoveFrom == null && rec.RecorderExe is { } recorded
            && !Path.GetDirectoryName(recorded)!.Equals(Path.GetDirectoryName(g.ExePath), StringComparison.OrdinalIgnoreCase))
        {
            (rec.RecorderMoveFrom, rec.RecorderMoveTo) = (recorded, g.ExePath);
            try { Store.SaveGame(g.Id, rec); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecorderLog($"{g.Name}: couldn't save the recorder's record: {e.Message}"); }
        }
        var at = RecordedAt(g, rec);
        var dirs = new[] { Path.GetDirectoryName(at.ExePath)!, Path.GetDirectoryName(g.ExePath)! }.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool done;
        if (!GameFolderWrite(g) || !GameFolderWrite(at))
        {
            (rec.RecorderRollback, _rollbacks[g.Id], done) = (true, true, false);
            RecorderLog($"{g.Name}: {cause}: the recorder is removed when the game exits");
        }
        else try
        {
            foreach (var dir in dirs)
            {
                RemoveRecorder(dir, rec, g.Name, RecorderLog);
                // not through WriteKeys: no wait for the recording lock with a proxy maybe still in place
                foreach (var f in Recordings.AppFiles)
                    try { File.Delete(Path.Combine(dir, f)); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            (rec.KeysIndexHash, rec.RecorderRollback, done) = (null, false, true);
            _rollbacks.TryRemove(g.Id, out _);
            RecorderLog($"{g.Name}: {cause}: recorder removed ({string.Join(", ", dirs)})");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            (rec.RecorderRollback, _rollbacks[g.Id], done) = (true, true, false);
            RecorderLog($"{g.Name}: {cause}; removing the recorder failed ({e.Message}): removed when the game exits");
        }
        try { Store.SaveGame(g.Id, rec); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecorderLog($"{g.Name}: couldn't save the recorder's record: {e.Message}"); }
        return done;
    }

    /// <summary>The one place an anti-cheat verdict lands (the full scan, a cached verdict, the refresh's name probe, the
    /// install's checks): the verdict in memory first (the scan's cache and the live game state; it can't fail), then the
    /// recorder out (<see cref="TakeOutNow"/>), whatever comes after (a stop, a cancelled scan, a scan cache that
    /// can't be written), then the verdict saved; a failed save is logged. <paramref name="installing"/>: Install's own
    /// record, not saved yet; else the record is read under the recorder lock.</summary>
    void AntiCheatFound(Game g, AntiCheat found, Evaluation? fresh = null, GameRecord? installing = null)
    {
        Disarm(g);   // first: a launch from now on is a pass-through, whatever the removal does
        bool changed;
        lock (_scanLock)
        {
            _scan ??= Store.LoadScan();
            changed = fresh != null || _scan.GetValueOrDefault(g.Id) is { } ev && ev.AntiCheat != found;
            if (fresh != null) _scan[g.Id] = fresh;
            else if (changed) _scan[g.Id] = _scan[g.Id] with { AntiCheat = found };
            _scanStarted[g.Id] = ++_evaluations;   // sticky: only an evaluation started after it can clear it
            _verdicts[g.Id] = found;   // an older evaluation's state published later keeps it (WithVerdict)
        }
        lock (_lock)
            if (_games.FindIndex(x => x.Game.Id == g.Id) is var i and >= 0 && _games[i].AntiCheat != found) _games[i] = _games[i] with { AntiCheat = found };
        TakeOutNow(g, $"{found} found", installing);
        if (!changed) return;
        lock (_scanLock)
            try { Store.SaveScan(_scan); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecorderLog($"{g.Name}: couldn't save that it uses {found}: {e.Message}"); }
    }

    /// <summary>Our recorder out of a game with anti-cheat or a mod that changes every pipeline, now (<see cref="TakeOut"/>:
    /// no wait for the recording lock, the inbox kept for the next import), or, while the game runs or holds the proxy, as
    /// soon as it exits. Under the recorder lock, with the record read there (or Install's, which holds the lock). Any
    /// failure, the check of what's ours included (a dll locked against reading), leaves the removal pending.</summary>
    void TakeOutNow(Game g, string why, GameRecord? installing = null)
    {
        if (OfflineLive(g.Id)) return;   // its own end takes it out
        Disarm(g);   // first: whatever the removal does, a launch from now on is a pass-through
        lock (_recorderLock)
        {
            var rec = installing ?? Store.LoadGame(g.Id);
            try
            {
                if (!IsOurProxy(Path.Combine(Path.GetDirectoryName(g.ExePath)!, "d3d12.dll")) && !IsOurProxy(Path.Combine(Path.GetDirectoryName(RecordedAt(g, rec).ExePath)!, "d3d12.dll"))
                    && rec.RecorderFiles.Count == 0 && rec.RecorderChained == null) return;
                TakeOut(g, rec, why);
            }
            catch (Exception e)
            {
                (rec.RecorderRollback, _rollbacks[g.Id]) = (true, true);
                RecorderLog($"{g.Name}: {why}; removing the recorder failed ({e.Message}): it's tried again at the next reconcile");
                try { Store.SaveGame(g.Id, rec); }
                catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }   // in memory
            }
        }
    }

    /// <summary>Games whose failed install left the proxy in place (<see cref="GameRecord.RecorderRollback"/>), in case
    /// the record couldn't be saved.</summary>
    readonly ConcurrentDictionary<string, bool> _rollbacks = new();

    /// <summary>Called by Install with "chain" (before it saves a mod's rename), "copy" (after its anti-cheat check, before
    /// it copies the proxy), "copied" and "keys" (after its full checks, before the keys file and the record) (tests).</summary>
    public Action<string>? InstallStep { get; set; }

    /// <summary>Called by a scan's evaluation with "checked" (its full anti-cheat check came back clean, before it stores
    /// the verdict) (tests).</summary>
    public Action<string>? EvaluateStep { get; set; }

    /// <summary>Throws when a fresh scan finds anti-cheat (or can't read the whole install), after <see cref="AntiCheatFound"/>:
    /// an older recorder already there (an update) goes too.</summary>
    void RefuseAntiCheat(Game g, GameRecord rec)
    {
        var antiCheat = GameFiles.DetectAntiCheat(g);
        if (antiCheat == AntiCheat.None) return;
        AntiCheatFound(g, antiCheat, installing: rec);
        throw AntiCheatRefusal(g, antiCheat);
    }

    static InvalidOperationException AntiCheatRefusal(Game g, AntiCheat antiCheat) =>
        new($"{g.Name} uses {antiCheat}: a d3d12.dll in its folder could get the account banned. Not installing.");

    string IniText(Game g, GameRecord rec)
    {
        var dir = Path.GetDirectoryName(g.ExePath)!;
        var next = rec.RecorderChained is { } c && File.Exists(Path.Combine(dir, c.Name)) ? c.Name : null;
        return RecorderIni
            + (DbCap(g) is { } cap
                ? $"; the recording limit per game (Settings): no new records once scskiller.db has this many bytes\r\nmax_db_bytes={cap}\r\n" : "")
            + (next == null ? "" : $"; the game's own d3d12.dll (a mod), renamed by SCSKiller and put back when the recorder is removed\r\nnext={next}\r\n");
    }

    /// <summary>Rewrites SCSKiller's own scskiller.ini when its limit line is out of date (the setting changed, an import
    /// grew the copy, a clear), or writes it when our proxy has none; a user's own ini is left alone. The proxy reads it when
    /// the game starts.</summary>
    void UpdateRecorderIni(Game g, GameRecord rec)
    {
        if (!GameFolderWrite(g)) return;   // the next refresh after it exits
        try
        {
            var dir = Path.GetDirectoryName(g.ExePath)!;
            var ini = Path.Combine(dir, "scskiller.ini");
            // missing next to our proxy: an install cut off before it wrote it (Install takes a missing ini for its own)
            if (File.Exists(ini) ? !rec.RecorderFiles.TryGetValue("scskiller.ini", out var h) || h != Sha256(ini) : !IsOurProxy(Path.Combine(dir, "d3d12.dll"))) return;
            var text = IniText(g, rec);
            if (File.Exists(ini) && File.ReadAllText(ini) == text) return;
            Place(ini, temp => File.WriteAllText(temp, text), replace: true);
            rec.RecorderFiles["scskiller.ini"] = Sha256(ini);
            Store.SaveGame(g.Id, rec);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecorderLog($"{g.Name}: couldn't update the recording limit: {e.Message}"); }
    }

    /// <summary>The recording is imported, then the recorder's data files are deleted. False when the game started before
    /// they were: they're left.</summary>
    bool Uninstall(Game g, GameRecord rec)
    {
        var dir = Path.GetDirectoryName(g.ExePath)!;
        RemoveRecorder(dir, rec, g.Name, RecorderLog);
        ImportRecording(g, rec, migrate: true);
        Store.SaveGame(g.Id, rec);
        try
        {
            if (RemoveRecorderData(dir, RecordingPath(g.Id), rec.RecordingInbox, () => GameRunning(g))) return true;
            RecorderLog($"{g.Name}: the game started: the recorder's data files left in {dir} for the next reconcile");
            return false;
        }
        catch (InvalidDataException e) { RecorderLog($"{g.Name}: left the recorder's data files in {dir}: {e.Message}"); }
        return false;
    }

    /// <summary>Deletes any proxy of ours and puts a chained mod back, first and whatever else fails; then the other files
    /// Install wrote whose hash still matches (one changed since, or unreadable, stays: without the proxy it does nothing).</summary>
    static void RemoveRecorder(string dir, GameRecord rec, string name, Action<string> log)
    {
        // first: whatever stays behind is a pass-through (its ledger entry first). A failure never keeps the proxy from going
        // (the watcher retries it)
        foreach (var file in (rec.RecorderExe is { } exe ? ArmedLedgers(exe) : []).Append(Path.Combine(dir, ArmedFile)))
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"{name}: couldn't delete {file}: {e.Message}"); }
        // the proxy by our export, whatever the manifest says: also one installed before SCSKiller tracked its files, or by hand
        var dll = Path.Combine(dir, "d3d12.dll");
        if (IsOurProxy(dll)) File.Delete(dll);
        else if (File.Exists(dll) && rec.RecorderFiles.ContainsKey("d3d12.dll")) log($"{name}: left {dll}: not SCSKiller's");
        rec.RecorderFiles.Remove("d3d12.dll");
        if (rec.RecorderChained is { } c)
        {
            var from = Path.Combine(dir, c.Name);
            if (!File.Exists(from)) log($"{name}: {from} (the mod's d3d12.dll SCSKiller renamed) is gone");
            else if (File.Exists(dll)) log($"{name}: left {from}: another d3d12.dll is in its place");
            else
            {
                if (Sha256(from) != c.Sha256) log($"{name}: {from} changed since SCSKiller renamed it (the mod updated itself?): put back as it is");
                File.Move(from, dll);
            }
            rec.RecorderChained = null;
        }
        foreach (var (file, hash) in rec.RecorderFiles.ToList())
        {
            var path = Path.Combine(dir, file);
            try
            {
                if (File.Exists(path) && Sha256(path) == hash) File.Delete(path);
                else if (File.Exists(path)) log($"{name}: left {path}: changed since SCSKiller installed it");
            }
            catch (Exception e) { log($"{name}: left {path}: {e.Message}"); }
            rec.RecorderFiles.Remove(file);
        }
        (rec.RecorderExe, rec.RecorderInstallDir) = (null, rec.RecorderMoveFrom != null ? rec.RecorderInstallDir : null);   // a pending move's root: the hook's running check
    }

    static readonly TimeSpan UninstallBudget = TimeSpan.FromSeconds(20);   // Velopack 1.2.158 kills its uninstall hook at 60 s

    /// <summary>SCSKiller's own uninstall: takes the recorder out of every game folder the data folder
    /// records one in (<see cref="GameRecord.RecorderExe"/>, no scan), then its data files once the data folder's
    /// recording holds the game's scskiller.db. A running game's folder is left. What it did goes to recorders.log. No step
    /// waits for a lock past <paramref name="budget"/> (<see cref="UninstallBudget"/> when not given).</summary>
    public static void RemoveAllRecorders(AppStore store, IReadOnlySet<string>? running = null, TimeSpan? budget = null) =>
        RemoveAllRecorders(store, running == null ? RunningProcessNames : () => running, budget);

    /// <summary><paramref name="running"/>: the running processes' names, asked again for every check.</summary>
    public static void RemoveAllRecorders(AppStore store, Func<IReadOnlySet<string>> running, TimeSpan? budget)
    {
        var clock = Stopwatch.StartNew();
        var limit = budget ?? UninstallBudget;
        TimeSpan Left() => limit - clock.Elapsed is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero;
        bool Late() => clock.Elapsed > limit;
        void Log(string line) => RecordersLog(store.DataDir, "uninstall: " + line);
        var games = Path.Combine(store.DataDir, "games");
        var removed = new List<(string Id, string Dir, Func<bool> Runs)>();
        foreach (var id in (Directory.Exists(games) ? Directory.GetDirectories(games) : []).Select(d => AppStore.GameId(Path.GetFileName(d))).Order())
            try
            {
                var rec = store.LoadGame(id);
                // a move's old folder, or an offline session's whose files went first, may still hold the data files
                if ((rec.RecorderExe ?? rec.RecorderMoveFrom ?? rec.OfflineSession?.Exe) is not { } exe) continue;
                var dir = Path.GetDirectoryName(exe)!;
                var installDir = rec.RecorderInstallDir ?? rec.OfflineSession?.InstallDir;
                var moveTo = rec.RecorderMoveTo;   // a move pending: the game may run from its new folder
                // the record's folders may be stale (the game's exe moved since): any exe of the install counts, its
                // names read once; null: not read whole (in time), as if it runs
                HashSet<string>? tree = installDir == null ? [] : InstallExeNames(installDir, Late);
                bool Runs() => running() is var names && (Running(exe, installDir, names) || moveTo != null && Running(moveTo, installDir, names)
                                                          || tree == null || tree.Overlaps(names));
                if (clock.Elapsed > limit) Log($"out of time: recorder left in {dir}");
                else if (tree == null) Log($"{installDir} not read whole in time: recorder left in {dir}");
                else if (Runs()) Log($"{exe} is running: recorder left in {dir}");
                else
                {
                    RemoveRecorder(dir, rec, dir, Log);
                    store.SaveGame(id, rec, Left());
                    removed.Add((id, dir, Runs));
                    Log($"recorder removed from {dir}");
                }
            }
            catch (Exception e) { Log($"{id}: {e.Message}"); }
        foreach (var (id, dir, runs) in removed)
            try
            {
                if (clock.Elapsed > limit) Log($"out of time: the recorder's data files left in {dir}");
                else if (!RemoveRecorderData(dir, Path.Combine(store.GameDir(id), "recording.db"), store.LoadGame(id).RecordingInbox, runs, Left()))
                    Log($"the game started: the recorder's data files left in {dir}");
            }
            catch (Exception e) { Log($"{dir}: {e.Message}"); }
    }

    /// <summary>The files the proxy writes, and its keys file. scskiller.db goes once <paramref name="recording"/> holds it:
    /// merged into it first unless it was imported as it is (<paramref name="imported"/>: its size and write time then).
    /// False, nothing deleted, when the game <paramref name="runs"/> once the merge is done.</summary>
    static bool RemoveRecorderData(string dir, string recording, string? imported, Func<bool> runs, TimeSpan? wait = null)
    {
        if (!Directory.Exists(dir)) return true;
        var src = new FileInfo(Path.Combine(dir, "scskiller.db"));
        if (src is { Exists: true, Length: > 0 } && $"{src.Length}:{src.LastWriteTimeUtc.Ticks}" != imported)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(recording)!);
            using (Recordings.Lock(recording, wait)) Recordings.Merge(recording, src.FullName, null);
        }
        if (runs()) return false;   // it may have started while the merge waited for the lock
        Exception? first = null;
        foreach (var f in RecorderDataFiles.Concat(Recordings.AppFiles))   // a locked file doesn't keep the others
            try { File.Delete(Path.Combine(dir, f)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { first ??= e; }
        if (first != null) throw first;
        return true;
    }

    /// <summary>The running check of <see cref="GameRunning"/> without the watcher: a process named like the exe
    /// (<paramref name="names"/>, without extensions), or like an exe of the install root or the exe's folder. Without a
    /// recorded install root (a record from an older build), the exe's folder and the three above it: Unreal's
    /// Binaries\Win64 sits three below the root. <paramref name="elsewhere"/>: a folder exe's name that only another
    /// program runs under (<see cref="OnlyElsewhere"/>); without it (the uninstall hook) any process with the name counts.</summary>
    static bool Running(string exe, string? installDir, IReadOnlySet<string> names, Func<string, string?[], bool>? elsewhere = null)
    {
        var dir = Path.GetDirectoryName(exe)!;
        var roots = installDir != null ? [installDir, dir]
            : new[] { dir, Path.GetDirectoryName(dir), Path.GetDirectoryName(Path.GetDirectoryName(dir)),
                      Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(dir))) };
        return names.Contains(Path.GetFileNameWithoutExtension(exe)) || ExeNamesIn(roots) is not { } exes
               || exes.Any(n => names.Contains(n) && elsewhere?.Invoke(n, roots) != true);
    }

    /// <summary>The names (without extensions) of every exe in the install's whole tree, which a running process may be
    /// named like. Null when the tree can't be read whole: an unreadable folder, more than <see cref="GameFiles.MaxEntries"/>
    /// entries, or <paramref name="late"/> before the end.</summary>
    internal static HashSet<string>? InstallExeNames(string installDir, Func<bool> late)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(installDir)) return names;
        var seen = 0;
        try
        {
            foreach (var f in new DirectoryInfo(installDir).EnumerateFileSystemInfos("*", InstallTree))
            {
                if (++seen > GameFiles.MaxEntries || late()) return null;
                if (f is FileInfo && f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) names.Add(Path.GetFileNameWithoutExtension(f.Name));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
        return names;
    }

    static readonly EnumerationOptions InstallTree = new() { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 };

    // _queue is kept in run order: the running item first, then the waiting ones, then the finished ones (Done, Failed,
    // Stopped) until the next StartQueue. Normal items run only while QueueRunning; "when idle" items run by themselves
    // whenever the PC is idle, after any normal item that is waiting while the queue runs (one item at a time).

    public bool QueueRunning { get { lock (_lock) return _running; } }

    public bool Compiling { get { lock (_lock) return _current != null; } }

    /// <summary>Adds the game at the end of the waiting items. Runs when the queue is started (or right away if it is
    /// running). On a queued "when idle" item: it becomes a normal item (runs with the queue, in the foreground).</summary>
    public void Enqueue(string gameId) => Add(gameId, whenIdle: false);

    /// <summary>A background rebuild (idle priority, Settings.BackgroundThreads, paused while a game runs if
    /// PauseWhileGaming) that starts once the user is idle for <see cref="IdleAfter"/>, pauses on input and resumes when
    /// idle again, whether or not the queue was started. Enqueue or ResumeQueue on it = run it in the foreground.</summary>
    public void EnqueueWhenIdle(string gameId) => Add(gameId, whenIdle: true);

    /// <summary>A waiting "when idle" item's <see cref="QueueItem.Note"/>: it doesn't run with the queue.</summary>
    public const string WhenIdleNote = "starts when the PC is idle";

    static bool Finished(QueueStage s) => s is QueueStage.Done or QueueStage.Failed or QueueStage.Stopped;

    int WaitingStart() => _queue.Count > 0 && _queue[0].GameId == _current ? 1 : 0;   // under _lock

    void Add(string gameId, bool whenIdle, bool planCheck = false, bool prompt = false)
    {
        if (!planCheck && Games.FirstOrDefault(s => s.Game.Id == gameId) is { } listed && GameVerdicts.Current.Unsupported(listed.Game, listed.Engine) != null) return;
        QueueItem item;
        lock (_lock)
        {
            if (_removed.Contains(gameId)) return;   // a scan that read it before its removal asks for a plan check
            var i = _queue.FindIndex(q => q.GameId == gameId);
            bool queued = i >= 0 && !Finished(_queue[i].Stage);
            bool promote = !whenIdle && _whenIdle.Remove(gameId);
            if (!planCheck) promote |= _planOnly.Remove(gameId);   // a compile, not a plan check: listed with the queue
            if (queued && !promote) return;
            if (planCheck) _planOnly.Add(gameId);
            if (planCheck && prompt) _prompt.Add(gameId);
            else _prompt.Remove(gameId);
            if (whenIdle) _whenIdle.Add(gameId);
            if (queued) _queue[i] = item = _queue[i] with { Error = null, Note = !whenIdle ? null : _queue[i].Stage == QueueStage.Waiting ? WhenIdleNote : _queue[i].Note, PlanCheck = false };
            else
            {
                if (i >= 0) _queue.RemoveAt(i);   // finished before: queued again
                item = new QueueItem(gameId, QueueStage.Waiting, null, null, whenIdle && !prompt ? WhenIdleNote : null, planCheck);
                var end = _queue.FindIndex(q => Finished(q.Stage));
                _queue.Insert(end < 0 ? _queue.Count : end, item);
            }
            if (whenIdle || _running) _worker ??= Task.Run(Work);
        }
        QueueChanged?.Invoke(item);
    }

    /// <summary>Clears Done and Failed items, puts Stopped ones back first among the waiting (they continue where they
    /// stopped), then runs the normal waiting items in order; the queue stops when none is left. A "when idle" item that
    /// is running goes on in the foreground so the queue isn't stuck behind it.</summary>
    public void StartQueue()
    {
        List<QueueItem> changed;
        lock (_lock)
        {
            changed = _queue.Where(q => q.Stage is QueueStage.Done or QueueStage.Failed).ToList();
            var stopped = _queue.Where(q => q.Stage == QueueStage.Stopped).Select(q => q with { Stage = QueueStage.Waiting, Error = null, Note = null }).ToList();
            _queue.RemoveAll(q => Finished(q.Stage));
            _queue.InsertRange(WaitingStart(), stopped);
            RunWaiting();
            changed.AddRange(_queue);
        }
        foreach (var q in changed) QueueChanged?.Invoke(q);   // removed ones too, so a list view refreshes
    }

    public void Compile(string gameId)
    {
        Enqueue(gameId);
        QueueItem? item;
        lock (_lock)
        {
            if (_running) return;
            RunWaiting();
            item = _queue.FirstOrDefault(q => q.GameId == gameId);
        }
        if (item != null) QueueChanged?.Invoke(item);   // QueueRunning changed
    }

    /// <summary>Runs the waiting items (under _lock); a running "when idle" item goes on in the foreground.</summary>
    void RunWaiting()
    {
        if (_current != null) _whenIdle.Remove(_current);
        _running = _queue.Any(q => q.Stage == QueueStage.Waiting && !_whenIdle.Contains(q.GameId));
        if (_running) _worker ??= Task.Run(Work);
    }

    /// <summary>Moves a waiting item to position <paramref name="index"/> among the listed waiting items, plan checks left
    /// out as the lists leave them out (0 = next, clamped).</summary>
    public void MoveInQueue(string gameId, int index)
    {
        QueueItem item;
        lock (_lock)
        {
            var i = _queue.FindIndex(q => q.GameId == gameId);
            if (i < 0 || _queue[i].Stage != QueueStage.Waiting) return;
            item = _queue[i];
            _queue.RemoveAt(i);
            int first = WaitingStart();
            var listed = Enumerable.Range(first, _queue.Count - first).Where(j => _queue[j] is { Stage: QueueStage.Waiting, PlanCheck: false }).ToList();
            index = Math.Clamp(index, 0, listed.Count);
            _queue.Insert(index < listed.Count ? listed[index] : listed.Count > 0 ? listed[^1] + 1 : first, item);
        }
        QueueChanged?.Invoke(item);
    }

    /// <summary>Drops the item; the running one is stopped gracefully and the queue goes on with the next.</summary>
    public void Remove(string gameId)
    {
        QueueItem item;
        bool running;
        lock (_lock)
        {
            _whenIdle.Remove(gameId);
            _planOnly.Remove(gameId);
            _prompt.Remove(gameId);
            var i = _queue.FindIndex(q => q.GameId == gameId);
            if (i < 0) return;
            item = _queue[i];
            _queue.RemoveAt(i);
            running = _current == gameId;
        }
        QueueChanged?.Invoke(item);
        if (running) StopCurrent();
    }

    public void PauseQueue()
    {
        _go.Reset();
        IWarmRun? run;
        lock (_lock) run = _run;
        run?.Pause();
        SetCurrent(QueueStage.Paused);
    }

    /// <summary>Also means "run it now" for a "when idle" item. A warm stays suspended while a game runs (Watch resumes it).</summary>
    public void ResumeQueue()
    {
        IWarmRun? run;
        lock (_lock)
        {
            if (_current != null) _whenIdle.Remove(_current);
            run = _run;
        }
        _go.Set();
        if (run == null) SetCurrent(_stage);   // a running warm is resumed by Watch within Poll
    }

    /// <summary>Graceful stop of the running item (Stopped, resumable), and the queue stops: the waiting items wait for
    /// the next StartQueue. "When idle" items still run by themselves.</summary>
    public void StopQueue()
    {
        lock (_lock) _running = false;
        StopCurrent();
    }

    void StopCurrent()
    {
        CancellationTokenSource? cts;
        IWarmRun? run;
        lock (_lock) cts = _itemCts;
        cts?.Cancel();
        lock (_lock) run = _run;   // read after the cancel: a run registered later sees the cancelled token (RunItem)
        run?.Stop();
    }

    void Set(QueueItem item)
    {
        lock (_lock)
        {
            var i = _queue.FindIndex(q => q.GameId == item.GameId);
            if (i < 0) return;   // removed while running
            item = item with { PlanCheck = _queue[i].PlanCheck };   // RunItem's items don't carry it; only Add changes it
            _queue.RemoveAt(i);
            _queue.Insert(Finished(item.Stage) ? _queue.Count : i, item);   // finished items go to the end
        }
        QueueChanged?.Invoke(item);
    }

    void SetCurrent(QueueStage stage, string? note = null)
    {
        QueueItem? item;
        lock (_lock) item = _queue.FirstOrDefault(q => q.GameId == _current);
        if (item != null) Set(item with { Stage = stage, Note = note });
    }

    bool UserIdle => IdleTime() >= IdleAfter;

    /// <summary>A prompt plan check (<see cref="_prompt"/>) waits only while a game is played, as the recorder's index does.</summary>
    bool WaitsForIdle(string id)
    {
        bool prompt;
        lock (_lock)
        {
            if (!_whenIdle.Contains(id)) return false;
            prompt = _prompt.Contains(id);
        }
        return prompt ? Settings.PauseWhileGaming && _playing.Count > 0 : !UserIdle;
    }

    async Task Work()
    {
        while (true)
        {
            string? id = null;
            QueueItem? stopped = null;
            CancellationToken ct = default;
            bool exit;
            lock (_lock)
            {
                var waiting = _queue.Where(q => q.Stage == QueueStage.Waiting).Select(q => q.GameId).ToList();
                var next = _running ? waiting.FirstOrDefault(g => !_whenIdle.Contains(g)) : null;
                if (_running && next == null) (_running, stopped) = (false, _queue.FirstOrDefault());   // nothing left: the queue stops
                var idle = waiting.Where(_whenIdle.Contains).ToList();
                if (exit = next == null && idle.Count == 0) _worker = null;
                else if ((id = next ?? idle.FirstOrDefault(g => !WaitsForIdle(g))) != null)
                {
                    var i = _queue.FindIndex(q => q.GameId == id);
                    var item = _queue[i];
                    _queue.RemoveAt(i);
                    _queue.Insert(0, item);   // the running item comes first
                    (_current, _stage) = (id, QueueStage.Waiting);   // what a resume shows until the item's first stage
                    _itemCts = new CancellationTokenSource();
                    ct = _itemCts.Token;
                }
            }
            if (stopped != null) QueueChanged?.Invoke(stopped);   // QueueRunning changed
            if (exit) break;
            if (id == null) { await Task.Delay(Poll); continue; }   // only "when idle" items left and the user is here
            if (_recorderIndexes.TryGetValue(id, out var indexing)) indexing.Cancel();   // its compile indexes it
            try
            {
                // no update applies under a running item, and no item starts while one is handed over (Busy.TryHold)
                IDisposable? hold;
                while ((hold = Busy.TryHold(Store.DataDir, Clock())) == null && !ct.IsCancellationRequested) await Task.Delay(Poll);
                // the app, the CLI and the scheduled task share the game's plan.bin and work\: one compile of it at a time
                using (hold)
                {
                    FileStream? own = null;
                    for (var waited = false; hold != null && (own = CompileLock(id)) == null && !ct.IsCancellationRequested; waited = true)
                    {
                        if (!waited) SetCurrent(QueueStage.Waiting, AnotherCompileNote);
                        await Task.Delay(Poll);
                    }
                    if (own == null) Set(new QueueItem(id, QueueStage.Stopped, null, null, null));   // stopped while it waited
                    else using (own) await RunItem(id, ct);
                }
            }
            catch (Exception e)   // the worker goes on: a faulted one stays in _worker and nothing would run again
            {
                Log?.Report($"{id}: {e.Message}");
                QueueItem? item;
                lock (_lock) item = _queue.FirstOrDefault(q => q.GameId == id);
                if (item != null && !Finished(item.Stage)) Set(item with { Stage = QueueStage.Failed, Error = e.Message });
            }
            finally { lock (_lock) (_current, _run, _itemCts) = (null, null, null); }
        }
        ReleaseMemory();
    }

    /// <summary>A waiting item's <see cref="QueueItem.Note"/> while another process compiles the game.</summary>
    public const string AnotherCompileNote = "waits for another SCSKiller compile of this game";

    /// <summary>games\&lt;id&gt;\compile.lock, open with no sharing for the whole item; null while another process has it.
    /// A handle, not <see cref="AppStore.PathGate"/>: it is released on another thread after the item's awaits.</summary>
    FileStream? CompileLock(string id)
    {
        Directory.CreateDirectory(Store.GameDir(id));
        try { return new FileStream(Path.Combine(Store.GameDir(id), "compile.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) when ((e.HResult & 0xFFFF) is 32 or 33) { return null; }   // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
    }

    /// <summary>The index, plan and materialize buffers are garbage once the queue is done: give the memory back to
    /// Windows instead of keeping the peak committed while the app sits idle.</summary>
    static void ReleaseMemory() => GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

    /// <summary>Between stages: waits while the queue is paused, or while the user is at the PC for a "when idle" item.
    /// ponytail: index/plan/materialize run to the end of the stage once started (only the warm suspends mid-way); pass
    /// a pause token into the reader/planner if a stage ever gets long.</summary>
    async Task Gate(string id, CancellationToken ct)
    {
        while (!_go.IsSet || WaitsForIdle(id)) await Task.Delay(Poll, ct);
    }

    /// <summary>index -> plan if missing/stale -> materialize into games\&lt;id&gt;\work -> warm -> record -> delete work.</summary>
    async Task RunItem(string id, CancellationToken ct)
    {
        WarmProgress? progress = null;
        long lastDone = -1, crashed = 0;
        var advanced = Stopwatch.StartNew();   // since the done count last moved (or the warm was paused)
        void Stage(QueueStage s, string? error = null)
        {
            if (s is not QueueStage.Paused) _stage = s;
            bool paused = s is not (QueueStage.Done or QueueStage.Failed or QueueStage.Stopped) && (!_go.IsSet || _pauseWhy != null);
            if (paused || (progress?.Done ?? -1) != lastDone)
            {
                lastDone = progress?.Done ?? -1;
                advanced.Restart();
            }
            // a finished warm's note: what failed (the driver rejected it) and what was skipped (a shader not in this install);
            // a warm that stopped moving: for how long (the estimate would be a guess)
            var note = paused ? _pauseWhy : s is QueueStage.Done or QueueStage.Stopped && progress is { } p ? WarmCounts(p.Failed, p.Skipped, crashed)
                : s == QueueStage.Warming && advanced.Elapsed > StallAfter ? StalledNote(advanced.Elapsed)
                : s == QueueStage.Warming ? progress?.Note : null;   // e.g. "retrying ray tracing with fewer threads (8)"
            Set(new QueueItem(id, paused ? QueueStage.Paused : s, progress, error, note));
        }
        var state = Games.FirstOrDefault(s => s.Game.Id == id);
        var installed = state == null ? null : Current(state.Game);
        // the state may be of an older build than the one installed now: its engine and planner check, cached unless the exe or build changed
        var now = installed == null ? null : Evaluated(installed, Store.LoadGame(id), false, out _);
        // a game needing a recording only for its ray tracing still compiles the rest (a partial compile); the planner's own verdict first
        string? NotReady() => state == null || installed == null ? "unknown game (scan first)"
            : now?.Engine == null || now.Check.Readiness != Readiness.Ready ? $"not ready: {now?.Check.Reason}"
            : BlockingMod(installed) is { } mod ? $"not ready: {ShaderModReason(mod)}"
            : state.Status is GameStatus.Unsupported || state.Status == GameStatus.NeedsRecording && !NeedsRtRecording(state) ? $"not ready: {state.StatusReason}"
            : null;
        if (NotReady() != null && now?.Check.Readiness == Readiness.Ready)   // the state may be older than the check (another process imported a recording)
        {
            Refresh(installed!, ct);
            state = Games.FirstOrDefault(s => s.Game.Id == id);
        }
        if (NotReady() is { } notReady)
        {
            Stage(QueueStage.Failed, notReady);
            return;
        }
        var engine = now!.Engine!;
        var game = installed!;
        bool background;
        lock (_lock) background = Background || _whenIdle.Contains(id);
        var rec = Store.LoadGame(id);
        var work = Path.Combine(Store.GameDir(id), "work");
        var started = DateTime.UtcNow;
        bool checking, refreshed = false;
        lock (_lock) checking = _planOnly.Contains(id);
        void Close()   // before Done shows: a notification check then reads the warm's state, never one evaluated before it ended
        {
            refreshed = true;
            try { Refresh(game, ct); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{game.Name}: refreshing failed: {e.Message}"); }
        }
        try
        {
            Stage(QueueStage.Indexing);
            await Gate(id, ct);
            var indexStamp = IndexStamp(game);   // before the index: a change while it runs shows at the next evaluation
            var index = Lowered(checking, () => Index(game, engine, rec, ct));
            // a newer community recording for exactly this build: fetched first, within a short wait (never holds a compile up)
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                wait.CancelAfter(TimeSpan.FromSeconds(20));
                try { await SyncCommunity(game, index.ContentHash, wait.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Log?.Report($"{game.Name}: community database: no answer in time, compiling without it"); }
            }
            // prepared once, for the plan and for the warm: the planner reads the recorded shaders' bytes too
            var prepared = false;
            Recording? recording = null;
            WarmInputs.Recorded? recorded = null;   // the warm's recorded inputs, read before the recordings are prepared
            Recording? Prepared()
            {
                if (prepared) return recording;
                if (Directory.Exists(work)) Directory.Delete(work, true);
                using (Recordings.Lock(RecordingPath(id), ct: ct))   // no import (any process's) between the snapshot's read and the preparation's
                {
                    recorded = RecordedNow(game, rec);
                    RecordingsRead?.Invoke();
                    (prepared, recording) = (true, PrepareRecording(game, engine, index, work, ct));
                }
                return recording;
            }
            // middleware packs (another game's recording may have grown one this install's DLLs match): a change re-plans
            var packs = _planner as Planner;
            bool current = false;   // this rebuild found the warm still current: nothing to warm
            var shared = packs?.SharedFingerprint(game);   // before the build: a download during it must still re-plan
            var maps = MapsFingerprint(index);
            if (PlanIsStale(id, rec, engine) || rec.PlanMaps != maps || (packs != null && packs.PackFingerprint(game, shared) != (rec.PlanMiddleware ?? "")))
            {
                Stage(QueueStage.Planning);
                await Gate(id, ct);
                rec.PlanPerStage = PerStagePlans;
                rec.Plan = Lowered(checking, () => _planner.Build(game, engine, index, Prepared(), Vendor.Caps, Store.GameDir(id), Log, ct, Settings.MaximumPlans));
                (rec.PlanBuiltAt, rec.PlanVersion, rec.ResumeAt) = (DateTimeOffset.Now, Planner.Version, 0);
                rec.PlanMaps = maps;
                rec.PlanMiddleware = packs is null ? null : packs.SeededFingerprint(rec.Plan) ?? packs.PackFingerprint(game, shared);   // what the build read: it may have promoted into one
                rec.PlanCommunity = CommunityInUse(id)?.Object;
                var keys = PlanKeys(rec.Plan.FilePath);
                rec.PlanItems = keys == null ? null : PlanFingerprint(keys);
                rec.PlanKeysFile = KeyFiles.Write(Store.GameDir(id), "plan", PlannerMade(rec.Plan, work));
                // a newer planner that compiles nothing the warm didn't (the same records, or fewer): the warm is current
                if (current = PendingOf(game, rec).Planned == 0 && rec.WarmedPlanVersion != Planner.Version && StaleReason(game, rec) == null)
                    (rec.WarmedPlanVersion, rec.WarmedPlanItems) = (rec.PlanVersion, rec.PlanItems);
                Store.SaveGame(id, rec);
                Store.PruneKeyFiles(id);   // after the save: the state never names a file that is gone
            }
            bool planOnly;
            lock (_lock)
            {
                planOnly = _planOnly.Remove(id);
                _prompt.Remove(id);
            }
            if (current || planOnly)
            {
                Set(new QueueItem(id, QueueStage.Done, null, null, current ? "already compiled: the new plan adds nothing"
                    : rec.WarmedAt != null && StaleReason(game, rec) is { } why ? why : "plan built"));
                return;
            }
            Stage(QueueStage.Materializing);
            await Gate(id, ct);
            Prepared();
            // the warm's inputs as it starts, read as the count reads them: what it prepared, and its plan; never read again
            var inputs = WarmInputs.Of(recorded!, rec.Plan!.FilePath, [], Elsewhere(game, rec));
            if (recorded!.LaunchOnly > 0)
                Log?.Report($"{game.Name}: {recorded.LaunchOnly:N0} recorded ray tracing pipelines are compiled but never count as new: the game names them anew each launch, and this GPU's driver reuses one only exactly as it was built");
            // ponytail: re-materialized on every run (also after a stop); keep work\ across a stop if that gets slow
            if (_planner is Planner planner) planner.Log = Log;   // its middleware-pack line
            AgsRegistration? agsUsed = null;
            _planner.Materialize(rec.Plan!, game, engine, _reader, recording, work, ct);
            (string Dir, string Fingerprint)? layer = null;   // a copy of the game's ReShade layer, made as each warm process starts
            if (_warmer is Warmer warmer) (warmer.Log, warmer.Ags, warmer.Layer) = (Log, g => agsUsed = AgsFor(g, rec), (_, _) => layer?.Dir);
            // the plan's own too: a synthesized compute stream has the record key of the game's create (UE 5.6, NVIDIA)
            SessionLog.WriteRayQueryKeys([Path.Combine(work, "scskiller.db"), Path.Combine(work, "scskiller_gen.db")], RayQueryKeysPath(id));
            var skipped = Planner.SkippedIn(work);   // not replayed (a shader not in this install): reported apart from failed
            var needsRecording = Planner.NeedsRecordingIn(work);
            var cap = rec.Careful ? CarefulThreads(Vendor.Vendor) : null;
            var carefulPasses = cap != null ? WarmPasses.Write(work) : 0;   // none without recorded PSOs
            if (carefulPasses > 0) Log?.Report($"{game.Name}: careful compile: the recorded PSOs in {carefulPasses} passes on at most {cap} threads, then the rest");

            Stage(QueueStage.Warming);
            // Attribution: the driver-cache files a process named like the game (the staged warm) has open, sampled on its own
            // task while the warm runs (back to back until found, then every few seconds), never in the progress callback: a
            // sample opens every cache file (measured 6-10 s on a 42 GB NVIDIA cache), and progress lines queued up behind it.
            // ponytail: only files written since the warm started if a sample's cost ever matters beyond this.
            var exe = WarmExeName(game, rec);
            var cacheKeys = new HashSet<string>();
            Task Attribution(IWarmRun run, string exe) => AppCache is not { } cache ? Task.CompletedTask : Task.Run(async () =>
            {
                while (!run.Completion.IsCompleted)
                {
                    try { cacheKeys.UnionWith(cache.KeysOpenBy(exe)); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{game.Name}: cache attribution: {e.Message}"); }
                    await Task.WhenAny(run.Completion, Task.Delay(cacheKeys.Count > 0 ? AttributionInterval : Poll));
                }
            });
            // The game and its warm must not run together: the second process of an exe name gets a second, separate set of
            // cache files (ARCHITECTURE.md), so the game would play cold and the warm would fill files it never reads. The
            // warm waits while the game runs, and stops (resumable) when it starts; then it goes on from there.
            while (true)
            {
                await WhileGameRuns(exe, game.Name, ct, () => { if (LearnKeys(game, rec, WarmExeName(game, rec)).Count > 0) Store.SaveGame(id, rec); });
                await Gate(id, ct);
                // Stage the name the game is launched with: AMD keys its cache on the exact case (the game may just have run)
                if (MergeLaunched(game, rec, null)) Store.SaveGame(id, rec);
                exe = WarmExeName(game, rec);
                RedetectGpu();   // the warm targets the adapter by LUID, which a driver update replaces
                // what this warm launches on and is recorded for, even if a check during it sees a newer one; an unknown
                // driver (DXGI gave no version yet) resets nothing and records none
                var snap = Snapshot();
                var (gpu, driverId) = (snap.Gpu, snap.Id);
                if (AdoptDriverId(rec, snap)) Store.SaveGame(id, rec);   // an earlier build's keys for this driver are kept
                if (rec.CrashKeys.Count > 0 && !CrashKeysCurrent(rec, snap))
                {
                    Log?.Report($"{game.Name}: retrying the {rec.CrashKeys.Count} pipelines that crashed another driver on driver {gpu.DriverVersion}");
                    rec.CrashKeys.Clear();
                    Store.SaveGame(id, rec);
                }
                // also a continuation after yielding to the game; unknown (an earlier build's stop) counts as another one
                if (driverId != null && rec.ResumeAt > 0 && rec.ResumeGpu != driverId)
                {
                    Log?.Report($"{game.Name}: the stopped compile was for {(rec.ResumeGpu == null ? "an unknown" : "another")} driver: starting over on driver {gpu.DriverVersion}");
                    (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed) = (0, 0, 0, 0);
                    Store.SaveGame(id, rec);
                }
                // now, not before the waits (for the game, idle, a pause): ReShade or its add-ons may have changed meanwhile
                if (BlockingMod(game) is { } mod) throw new NotReadyException($"not ready: {ShaderModReason(mod)}");
                layer = LayerFor(game, work);
                if (layer != null) Log?.Report($"{game.Name}: compiling through a copy of the game's ReShade layer");
                // the stopped part's pipelines went through another layer, or none: they aren't this one's
                if (rec.ResumeAt > 0 && rec.ResumeLayer != layer?.Fingerprint)
                {
                    Log?.Report($"{game.Name}: the stopped compile ran through {(rec.ResumeLayer == null ? "no" : "another")} ReShade layer: starting over");
                    (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed) = (0, 0, 0, 0);
                    Store.SaveGame(id, rec);
                }
                var threads = ThreadsOverride ?? (background ? Settings.BackgroundThreads : Settings.Threads);
                var options = new WarmOptions(threads, background ? WarmPriority.Idle : Settings.Priority, rec.ResumeAt, CompileMemoryGB(Settings) * 1024,
                    rec.CrashKeys.Count > 0 ? [.. rec.CrashKeys] : null, cap is { } most ? Math.Min(threads, most) : 0);
                if (options.StartAt == 0) (rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed) = (0, 0, 0);   // a resume point reset elsewhere drops its segments too
                var staged = game with { ExePath = Path.Combine(Path.GetDirectoryName(game.ExePath)!, exe) };
                var reporter = new Reporter<WarmProgress>(p => { progress = p with { Failed = p.Failed + rec.ResumeFailed, Skipped = skipped }; Stage(QueueStage.Warming); });
                var run = _warmer is Warmer real ? real.Start(staged, work, options, reporter, gpu) : _warmer.Start(staged, work, options, reporter);
                lock (_lock) _run = run;
                var attribution = Attribution(run, exe);
                if (ct.IsCancellationRequested) run.Stop();   // stopped while it was starting
                if (!_go.IsSet) run.Pause();
                var (result, yielded) = await Watch(run, id, background, exe, game.Name);
                await attribution;   // a sample may be running: its keys count (cacheKeys is read below)
                result = result with { Failed = rec.ResumeFailed + result.Failed, Skipped = skipped };   // a resumed warm counts only its own
                progress = new WarmProgress(result.Done, result.Total, result.Failed, progress?.PerSecond ?? 0, result.CacheGrowthBytes, result.Skipped);
                // Saved below: whatever the outcome, those files are this game's. A D3D12 key other than the name hash is an
                // AMD app profile's; kept like any other (IsNameHashed reads the case rule from it).
                NoteProfile(game, exe, cacheKeys.Where(k => !rec.CacheKeys.Contains(k)).ToList());
                rec.CacheKeys.UnionWith(cacheKeys);
                if (result.Crashed is { Count: > 0 })   // whatever the outcome: a later compile or resume must not crash on them again
                    rec.CrashKeysDriver = driverId ?? rec.CrashKeysDriver;
                rec.CrashKeys.UnionWith(result.Crashed ?? []);
                crashed = result.Crashed?.Count ?? 0;
                var (items, seconds) = (rec.ResumeItems + result.Done - options.StartAt, rec.ResumeSeconds + result.Elapsed.TotalSeconds);
                if (yielded && result.Outcome == WarmOutcome.Stopped && !ct.IsCancellationRequested)
                {
                    (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed, rec.ResumeGpu, rec.ResumeLayer) = (result.Done, items, seconds, result.Failed, driverId, layer?.Fingerprint);
                    Store.SaveGame(id, rec);
                    continue;
                }
                switch (result.Outcome)
                {
                    case WarmOutcome.Completed:
                        bool cold = ColdWarm(rec, driverId);   // before the fields it reads are set to this warm
                        (rec.WarmedDriverId, rec.WarmedDriverVersion) = (driverId, gpu.DriverVersion);
                        (rec.WarmedPlanVersion, rec.WarmedExeName, rec.WarmedPerStage) = (rec.PlanVersion, exe, rec.PlanPerStage);
                        (rec.WarmedAgsApp, rec.AgsMissed) = (agsUsed?.App, rec.AgsMissed && agsUsed == null);   // after a miss, AGS returns only once the game's keys prove it
                        rec.WarmedPlanItems = rec.PlanItems;
                        rec.WarmedLayer = layer?.Fingerprint;
                        (rec.WarmedAt, rec.LastWarmTime, rec.LastCacheGrowthBytes) = (DateTimeOffset.Now, TimeSpan.FromSeconds(seconds), result.CacheGrowthBytes);
                        (rec.LastWarmFailed, rec.LastWarmSkipped, rec.LastWarmNeedsRecording, rec.WarmedKeys) = (result.Failed, result.Skipped, needsRecording, [.. cacheKeys]);
                        rec.WarmedFiles = AppCache is AmdAppCache amd ? amd.D3D12FileNames(cacheKeys) : null;
                        rec.LastWarmCrashed = crashed;
                        // what the recorder's csv counts as known from now on; the next refresh (this compile's closing one)
                        // puts it next to the recorder with the keys file, under the recording lock this compile mustn't wait for
                        try
                        {
                            AppStore.WriteAtomic(Path.Combine(Store.GameDir(id), Recordings.WarmedFile),
                                Recordings.Warmed([Path.Combine(work, "scskiller.db"), Path.Combine(work, "scskiller_gen.db")],
                                    Directory.EnumerateFiles(work, Recordings.FailedFile, SearchOption.AllDirectories)));   // every warm process's
                            rec.KeysPending = true;
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { Log?.Report($"{game.Name}: couldn't note what the compile created: {e.Message}"); }
                        // what this warm replayed: what is new after it is derived against it (PendingOf)
                        rec.WarmKeysFile = KeyFiles.Write(Store.GameDir(id), "warm", inputs.Select(WarmInputs.Token));
                        (rec.WarmedIndexHash, rec.WarmedExeStamp, rec.WarmedGameVersion, rec.WarmedIndexStamp) = (index.ContentHash, ExeStamp(game), game.Version, indexStamp);
                        (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed) = (0, 0, 0, 0);
                        (rec.WarmedCareful, rec.FirstLaunch) = (carefulPasses > 0, null);   // judged again by the next launch
                        if (cold && items >= 1000 && seconds > 1 && carefulPasses == 0)   // a careful warm's rate mixes both schedules
                        {
                            rec.PsoPerSecond = items / seconds;
                            SeedWarmRate(rec.PsoPerSecond.Value);
                            var n = result.Done - options.StartAt;   // the growth is the last segment's
                            if (result.CacheGrowthBytes > 0 && n > 0) rec.BytesPerPso = (double)result.CacheGrowthBytes / n;
                        }
                        Store.SaveGame(id, rec);
                        Store.PruneKeyFiles(id);
                        KeepRejectsLog(work, id, result.Failed);
                        Close();
                        Stage(QueueStage.Done);
                        break;
                    case WarmOutcome.Stopped:
                        (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed, rec.ResumeGpu, rec.ResumeLayer) = (result.Done, items, seconds, result.Failed, driverId, layer?.Fingerprint);
                        Store.SaveGame(id, rec);
                        Stage(QueueStage.Stopped);
                        break;
                    default:
                        Store.SaveGame(id, rec);
                        Stage(QueueStage.Failed, result.Error + KeptLog(result.LogPath, id));
                        break;
                }
                break;
            }
        }
        catch (OperationCanceledException) { Stage(QueueStage.Stopped); }
        catch (Exception e) { Stage(QueueStage.Failed, e is NotReadyException ? e.Message : CompileError(id, _stage, e)); }
        finally
        {
            // the cleanup, a log line of it included, never keeps the refresh and the reconcile (its anti-cheat check) from running
            try
            {
                if (_warmer is Warmer done) done.Ags = null;   // its closure holds this compile's whole index and recording
                try { if (Directory.Exists(work)) Directory.Delete(work, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"could not delete {work}: {e.Message}"); }
                // the runtime's DXBC conversions keyed to a staged exe's path: a new folder for every warm process, never read again
                try
                {
                    foreach (var d in D3DSCache.Made(Path.Combine(LocalAppData, "D3DSCache"), work, started))
                        try { Directory.Delete(d, true); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"could not delete {d}: {e.Message}"); }
                }
                catch (Exception e) { Log?.Report($"{game.Name}: D3DSCache not cleaned: {e.Message}"); }
            }
            finally
            {
                // a stop, before or during it, skips what waits for the recording lock (the next scan imports): no shutdown waits.
                // Anti-cheat it finds takes the recorder out all the same (AntiCheatFound).
                if (!refreshed) Close();
                StartSharing([game]);   // the content hash of this build is known now
                // the staged warm runs under the game's exe name, so a reconcile meanwhile waited for it
                if (ManageRecorders && !ct.IsCancellationRequested)
                    try { ReconcileRecorders(id); }
                    catch (Exception e) { Log?.Report($"{game.Name}: reconciling the recorder failed: {e.Message}"); }
            }
        }
    }

    /// <summary>A verdict found mid-compile (a mod that blocks it), not an error: no compile-error.log.</summary>
    sealed class NotReadyException(string message) : InvalidOperationException(message);

    /// <summary>A failed compile's error: the stage it failed in, with the whole exception (its stack is nowhere else) kept in
    /// compile-error.log beside warm-failed.log for a bug report; the next failure overwrites it.</summary>
    string CompileError(string id, QueueStage stage, Exception e)
    {
        while (e is AggregateException { InnerExceptions.Count: 1 } a) e = a.InnerExceptions[0];
        var error = $"{(stage is QueueStage.Indexing or QueueStage.Planning or QueueStage.Materializing or QueueStage.Warming ? $"{stage} failed: " : "")}{e.Message}";
        var kept = Path.Combine(Store.GameDir(id), "compile-error.log");
        try { AppStore.WriteAtomic(kept, System.Text.Encoding.UTF8.GetBytes($"{DateTimeOffset.Now:O}\r\nSCSKiller {AppVersion.Current}\r\nstage: {stage}\r\n\r\n{e}\r\n")); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { return error; }
        return $"{error} (log: {kept})";
    }

    /// <summary>A failed warm's log copied out of work\, which the item deletes: " (log: path)", "" when there is none.</summary>
    string KeptLog(string log, string id)
    {
        var kept = Path.Combine(Store.GameDir(id), "warm-failed.log");
        try
        {
            // a killed warm never moved its log up from the staged exe's folder inside its stage folder
            if ((File.Exists(log) ? log : Directory.EnumerateFiles(Path.GetDirectoryName(log)!, "scskiller.log", SearchOption.AllDirectories).FirstOrDefault()) is not { } from) return "";
            File.Copy(from, kept, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return ""; }
        return $" (log: {kept})";
    }

    /// <summary>The logs of a completed warm the driver rejected PSOs in, every process's, copied out of work\ to
    /// warm-rejects.log; a clean warm removes the last one's.</summary>
    void KeepRejectsLog(string work, string id, long failed)
    {
        var kept = Path.Combine(Store.GameDir(id), "warm-rejects.log");
        try
        {
            if (failed == 0) { File.Delete(kept); return; }
            // a retry or a resume after the game ran is another process, with its own stage folder and log
            var logs = Directory.EnumerateFiles(work, "scskiller.log", SearchOption.AllDirectories).OrderBy(File.GetLastWriteTimeUtc).ToList();
            if (logs.Count > 0) File.WriteAllLines(kept, logs.SelectMany(File.ReadLines));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"keeping the warm's rejects log failed: {e.Message}"); }
    }

    bool PlanIsStale(string id, GameRecord r, EngineInfo? e) => r.Plan is not { } p || !File.Exists(p.FilePath) || p.IndexContentHash != r.IndexContentHash
                                      || p.VendorProfile != Vendor.Caps.Profile || r.RecordingImportedAt > r.PlanBuiltAt
                                      || r.PlanVersion != Planner.Version || r.PlanPerStage != PerStagePlans || r.PlanCommunity != CommunityInUse(id)?.Object || RtPlanCheck(r, e);

    /// <summary>Plans have each stage unit once: the vendor caches per stage and Maximum mode is off. A pairing plan warmed
    /// earlier already holds every unit, so going per-stage rebuilds the plan but needs no re-warm (see StaleReason).</summary>
    bool PerStagePlans => Vendor.Caps.PerStageCache && !Settings.MaximumPlans;

    /// <summary>Waits for the run. Stops it gracefully (Yielded) when a process named like the warm's exe starts: the game
    /// itself, or another discovered game of that exe name, even while the run is paused. Keeps it suspended while the
    /// queue is paused, while another discovered game runs (background runs with PauseWhileGaming) or, for a "when idle"
    /// item, while the user is at the PC. Checked every 0.5 s, so input pauses it within a second.</summary>
    async Task<(WarmResult Result, bool Yielded)> Watch(IWarmRun run, string id, bool background, string exe, string name)
    {
        string? applied = "";   // "" = nothing applied yet; null = running
        bool yielded = false;
        while (await Task.WhenAny(run.Completion, Task.Delay(Poll)) != run.Completion)
        {
            if (yielded) continue;
            var running = Running();
            if (running.Contains(exe))
            {
                yielded = true;
                _pauseWhy = StoppedFor(name, exe);
                SetCurrent(QueueStage.Paused, _pauseWhy);
                run.Stop();   // graceful (resumes a suspended run first): the driver writes and releases the game's files
                continue;
            }
            var playing = background && Settings.PauseWhileGaming ? GameNameIn(running) : null;
            _pauseWhy = playing != null ? $"paused while {playing} is running" : WaitsForIdle(id) ? "paused until the PC is idle" : null;
            var want = _go.IsSet ? _pauseWhy : _pauseWhy ?? "paused";
            if (want == applied) continue;
            applied = want;
            if (want == null) { run.Resume(); SetCurrent(QueueStage.Warming); }
            else { run.Pause(); SetCurrent(QueueStage.Paused, _pauseWhy); }
        }
        _pauseWhy = null;
        return (await run.Completion, yielded);
    }

    /// <summary>Names the processes of that exe name, SCSKiller's staged warms left out, with their paths when they can be
    /// read: one the user doesn't know of (a leftover, a copy elsewhere) can then be found in Task Manager.</summary>
    string StoppedFor(string name, string exe)
    {
        var all = Processes(true);
        var held = all.Where(p => p.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase) && !OurCopy(p, all)).Take(3)
            .Select(p => ProcessPath(p.Pid) is { } path ? $"process {p.Pid}: {path}" : $"process {p.Pid}").ToList();
        return $"stopped while {name} is running: continues when it exits" + (held.Count > 0 ? $" ({string.Join(", ", held)})" : "");
    }

    /// <summary>Waits while a process named like the warm's exe runs (see RunItem), shown as Paused with the reason;
    /// <paramref name="learn"/> attributes the cache files the game holds open meanwhile (every few seconds).</summary>
    async Task WhileGameRuns(string exe, string name, CancellationToken ct, Action? learn = null)
    {
        if (!Running().Contains(exe)) return;
        try
        {
            _pauseWhy = StoppedFor(name, exe);
            SetCurrent(QueueStage.Paused, _pauseWhy);
            Stopwatch? sampled = null;
            while (Running().Contains(exe))
            {
                if (learn != null && (sampled == null || sampled.Elapsed >= AttributionInterval)) { sampled = Stopwatch.StartNew(); learn(); }
                await Task.Delay(Poll, ct);
            }
        }
        finally
        {
            _pauseWhy = null;
            SetCurrent(_go.IsSet ? _stage : QueueStage.Paused);   // the reason is gone (a stop sets Stopped next)
        }
    }

    /// <summary>Exe file names of the discovered games that are running (a case-insensitive set), SCSKiller's staged warm
    /// copies (same file name, run from under DataDir) excluded. A name is in the case the process was launched with when
    /// that is known, else the install's file name. Replaceable for tests.</summary>
    public Func<IReadOnlySet<string>> RunningGameExes { get; set; }

    /// <summary><see cref="RunningGameExes"/>, noting each name that differs in case from its game's exe file name: the
    /// game was launched under that name (read into the game's record by MergeLaunched).</summary>
    IReadOnlySet<string> Running()
    {
        var running = RunningGameExes();
        foreach (var name in running)
            if (Games.Any(s => Path.GetFileName(s.Game.ExePath) is var disk && disk != name && disk.Equals(name, StringComparison.OrdinalIgnoreCase)))
                _launched.AddOrUpdate(name, n => new LaunchedExe(n, DateTimeOffset.Now), (n, old) => old.Name == n ? old : new LaunchedExe(n, DateTimeOffset.Now));
        return running;
    }

    /// <summary>Staged warms (<see cref="OurCopy"/>) left out. No process is opened, so a launched name's case comes from
    /// the recorder's #session marker (MergeLaunched), not from here.</summary>
    IReadOnlySet<string> DiscoveredGamesRunning()
    {
        var disk = Games.Select(s => Path.GetFileName(s.Game.ExePath)).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(n => n, StringComparer.OrdinalIgnoreCase);
        var all = Processes(true);   // fresh: one per poll
        return all.Where(p => disk.ContainsKey(p.Exe) && !OurCopy(p, all)).Select(p => disk[p.Exe])   // the install's case
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    string? GameNameIn(IReadOnlySet<string> runningExes) =>
        Games.FirstOrDefault(s => runningExes.Contains(Path.GetFileName(s.Game.ExePath)))?.Game.Name;

    // Polled: WMI's Win32_ProcessStartTrace would push starts but needs admin (access denied unelevated).
    volatile HashSet<string> _playing = [];                 // game ids; replaced, never changed in place (read from any thread)
    readonly Dictionary<string, int> _absent = [];          // playing game id -> polls in a row its exe wasn't running
    readonly HashSet<string> _toFollow = [];                // playing game ids whose running exe isn't decided yet (FollowRunningExe)
    readonly Dictionary<string, PlayWindow> _runs = [];     // playing game id -> its run so far (GameRecord.LastPlay once it exits)
    DateTimeOffset? _lastPoll;

    /// <summary>The watcher's clock. Replaceable for tests.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    /// <summary>Replaceable for tests.</summary>
    public TimeSpan WatchInterval { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Polls in a row without the exe before a game has exited: a game restarting itself isn't an exit.</summary>
    public const int ExitPolls = 2;
    /// <summary>How often <see cref="WatchGames"/> re-detects the GPU: nothing else notices a driver update while the app
    /// waits in the notification area.</summary>
    public static readonly TimeSpan GpuCheckInterval = TimeSpan.FromMinutes(5);

    public bool IsPlaying(string gameId) => _playing.Contains(gameId);

    /// <summary>A discovered game runs now (a fresh process list) or the watcher saw one still running. Until a scan has
    /// listed the games, true: none is known not to run.</summary>
    public bool GameRunning() => Volatile.Read(ref _listed) == 0 || RunningGameExes().Count > 0 || _playing.Count > 0;

    /// <summary>How often <see cref="WatchGames"/> checks every game with the recorder in for anti-cheat in full (about 3 ms
    /// a game), besides the next poll after its install changes.</summary>
    public static readonly TimeSpan RecorderCheckInterval = TimeSpan.FromMinutes(2);

    /// <summary>How long <see cref="WatchGames"/> keeps the key sets no evaluation asked for (<see cref="KeyFiles.DropIdle"/>).</summary>
    public static readonly TimeSpan KeyCacheIdle = TimeSpan.FromMinutes(1);

    /// <summary>Nobody sees the app now (its window hidden or minimized), so the idle collection's pause goes unnoticed. The
    /// command line has no window. Replaceable: the app sets it.</summary>
    public Func<bool> Unseen { get; set; } = () => true;

    /// <summary>How long the user is away before the idle collection runs with the window open: its pause is about 10 ms
    /// with nothing live, 130 ms with a compile's index live (measured), so not while someone is clicking.</summary>
    public static readonly TimeSpan CollectAfterIdle = TimeSpan.FromSeconds(30);

    volatile bool _collect;

    /// <summary>Heavy work ended (a scan, a background pass, the key sets dropped): the watcher's next idle look collects.</summary>
    void RequestCollect() => _collect = true;

    internal bool CollectPending => _collect;

    /// <summary>Runs after the idle collection while the window is hidden or minimized. Replaceable for tests.</summary>
    public Action TrimWorkingSet { get; set; } = ProcessMemory.Trim;

    /// <summary>The idle collection runs now: something asked for it, no compile runs, and the window is hidden or minimized
    /// or the user has been away <see cref="CollectAfterIdle"/>.</summary>
    internal static bool CollectNow(bool pending, bool compiling, bool unseen, TimeSpan userIdle) =>
        pending && !compiling && (unseen || userIdle >= CollectAfterIdle);

    public Task WatchGames(CancellationToken ct) => Task.Run(async () =>
    {
        var gpuChecked = Stopwatch.StartNew();
        var recordersChecked = Stopwatch.StartNew();
        Task? check = null;
        var wasUnseen = false;
        while (!ct.IsCancellationRequested)
        {
            try { PollGames(); }
            catch (Exception e) { Log?.Report($"watching games: {e.Message}"); }
            if (check is not { IsCompleted: false })   // the whole pass off the loop: a slow install never holds the polls back
            {
                var all = Settings.ScanAtStart && recordersChecked.Elapsed >= RecorderCheckInterval;
                if (all) recordersChecked.Restart();
                check = Task.Run(() => CheckRecorderGames(all));
                _ = check.ContinueWith(t => Log?.Report($"checking recorder games for anti-cheat: {t.Exception!.InnerException?.Message}"), TaskContinuationOptions.OnlyOnFaulted);
            }
            if (gpuChecked.Elapsed >= GpuCheckInterval)
            {
                RedetectGpu();
                gpuChecked.Restart();
            }
            bool compiling;
            lock (_lock) compiling = _current != null;
            if (!compiling && KeyFiles.DropIdle(KeyCacheIdle)) RequestCollect();
            // idle, nothing allocates enough for a full collection, and a background one keeps the pages committed (measured:
            // 410 MB freed stayed committed through 30 s of small allocations); the blocking one pauses the app
            var unseen = Unseen();
            if (unseen && !wasUnseen) RequestCollect();   // just hidden or minimized: collected, then trimmed
            wasUnseen = unseen;
            if (CollectNow(_collect, compiling, unseen, IdleTime()))
            {
                _collect = false;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                if (unseen) TrimWorkingSet();
            }
            try { await Task.Delay(WatchInterval, ct); }
            catch (OperationCanceledException) { }
        }
        if (check != null)
            try { await check; }
            catch (Exception) { }   // logged by its continuation
        StopWatchingInstalls();
    });

    /// <summary>The install watchers end (the app exits; tests: the app closed). What they armed stays armed.</summary>
    internal void StopWatchingInstalls()
    {
        lock (_installWatchers)
        {
            foreach (var w in _installWatchers.Values) Unwatch(w);
            _installWatchers.Clear();
        }
    }

    /// <summary>A watcher ends: a change counted, so an arming that took the count while it ran needs a new full check.</summary>
    void Unwatch((List<FileSystemWatcher>? Watchers, int Tries, string GameId, string?[]? Ids) w)
    {
        w.Watchers?.ForEach(x => x.Dispose());
        CountChange(w.GameId);
    }

    /// <summary>The watcher's anti-cheat pass (one at a time). Games with the recorder in: in full when <paramref name="all"/>,
    /// else those whose install had a file or folder created or renamed since the last pass (an update adding anti-cheat
    /// while the app waits in the notification area; the event itself disarmed the recorder, <see cref="ArmedFile"/>). Each
    /// install's check runs on its own and is handled as it ends; one that doesn't end within <see cref="RecorderCheckTimeout"/>
    /// is <see cref="AntiCheat.Other"/>. Clean: armed again; a hit: the recorder goes. A game with anti-cheat whose recorder
    /// or armed file is still on disk (a removal that failed) is tried again every <see cref="RemovalRetryInterval"/>; one
    /// whose armed file couldn't be revoked at all, first in every pass.</summary>
    public async Task CheckRecorderGames(bool all)
    {
        CleanOfflineSessions();
        // the watchers of games no longer with the recorder (or whose recorder moved) end; not while a reconcile or install
        // holds the recorders (its game's state may not show the recorder yet): the next pass
        foreach (var g in _revocationPending.Values)   // a held armed file: revoked again, before anything is armed, and its recorder taken out
        {
            Disarm(g);
            if (Games.FirstOrDefault(s => s.Game.Id == g.Id) is { AntiCheat: not AntiCheat.None } s)
                try { TakeOutNow(s.Game, $"{s.AntiCheat} found"); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecorderLog($"{s.Game.Name}: removing the recorder failed: {e.Message}"); }
        }
        if (Monitor.TryEnter(_recorderLock))
            try
            {
                var keep = Games.Where(s => s.RecorderInstalled && s.AntiCheat == AntiCheat.None).Select(s => WatchKey(s.Game)).ToHashSet();
                lock (_installWatchers)
                    foreach (var key in _installWatchers.Keys.Where(k => !keep.Contains(k)).ToList())
                        if (_installWatchers.Remove(key, out var w)) Unwatch(w);
            }
            finally { Monitor.Exit(_recorderLock); }
        var states = Games;
        var games = states.Where(s => s.RecorderInstalled && s.AntiCheat == AntiCheat.None).ToDictionary(s => WatchKey(s.Game));
        foreach (var s in games.Values) WatchOrDisarm(s.Game);   // made, or made again after a failure (bounded); before the change counts below
        foreach (var (key, s) in games)
            if (!ExeAsArmed(s.Game))   // written in place: no name event; the proxy's fingerprint refuses it, this checks and arms it anew
            {
                DisarmFromWatcher(s.Game, $"{s.Game.ExePath} changed");
                _installChanged[key] = true;
            }
        // an event: checked. A new watcher (the install may have changed unwatched) and the full pass leave out a game armed
        // since its folders last checked clean
        var pending = games.Values.Select(s => (s.Game, Flag: _installChanged.TryRemove(WatchKey(s.Game), out var ev) ? ev : (bool?)null))
            .Where(c => c.Flag == true || (c.Flag == false || all ? !(Armed(c.Game) && CleanSince(c.Game, FolderStamp(c.Game))) : UnarmedDue(c.Game)))
            .Select(c => (c.Game, Gen: InstallGen(c.Game))).Select(c => (c.Game, c.Gen, Walk: Task.Run(() => LowIo(() => FullCheck(c.Game)))))
            .ToList();
        var deadline = Task.Delay(RecorderCheckTimeout);
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending.Select(p => p.Walk).Append(deadline));
            var ended = done == deadline ? pending.ToList() : pending.Where(p => p.Walk.IsCompleted).ToList();
            foreach (var p in ended)
            {
                pending.Remove(p);
                var found = p.Walk.IsCompletedSuccessfully ? p.Walk.Result : AntiCheat.Other;   // out of time or failed: not known clean
                if (found == AntiCheat.None) Arm(p.Game, p.Gen, recheck: true);
                else
                {
                    AntiCheatFound(p.Game, found);
                    Refresh(p.Game);
                }
            }
        }
        foreach (var s in states.Where(s => s.AntiCheat != AntiCheat.None))
        {
            if (_removalTried.TryGetValue(s.Game.Id, out var at) && at.Elapsed < RemovalRetryInterval) continue;
            var rec = Store.LoadGame(s.Game.Id);
            if (rec.RecorderFiles.Count == 0 && rec.RecorderChained == null && !ProxyOnDisk(s.Game) && !ProxyOnDisk(RecordedAt(s.Game, rec))
                && !ArmedExes(s.Game, rec).Any(e => ArmedLedgers(e).Any(File.Exists) || File.Exists(ArmedFileOf(e)))) continue;
            _removalTried[s.Game.Id] = Stopwatch.StartNew();
            Disarm(s.Game);   // each on its own: a file that can't be deleted never keeps the other from going
            try { TakeOutNow(s.Game, $"{s.AntiCheat} found"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecorderLog($"{s.Game.Name}: removing the recorder failed: {e.Message}"); }
            Refresh(s.Game);
        }
    }

    /// <summary>A watcher's anti-cheat checks that take longer are <see cref="AntiCheat.Other"/>: no recorder. Replaceable for tests.</summary>
    public TimeSpan RecorderCheckTimeout { get; set; } = GameFiles.Budget + TimeSpan.FromSeconds(5);
    /// <summary>Replaceable for tests.</summary>
    public TimeSpan RemovalRetryInterval { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>The watcher's full anti-cheat check. Replaceable for tests.</summary>
    internal Func<Game, AntiCheat> FullAntiCheatCheck { get; set; } = g => GameFiles.DetectAntiCheat(g);
    /// <summary>Makes an install watcher for a folder. Replaceable for tests.</summary>
    internal Func<string, FileSystemWatcher> MakeWatcher { get; set; } = dir => new FileSystemWatcher(dir);
    const int MaxWatchTries = 5;

    /// <summary>A recorder location's watcher key: the game, its install and the exe's folder (where the recorder is).</summary>
    static string WatchKey(Game g) => $"{g.Id}|{g.InstallDir}|{Path.GetDirectoryName(g.ExePath)}";

    /// <summary>The folders <see cref="GameFiles.DetectAntiCheat"/> walks: the install, and the exe's folder when outside it.</summary>
    static IEnumerable<string> WatchedRoots(Game g)
    {
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(g.InstallDir));
        var exeDir = Path.GetDirectoryName(Path.GetFullPath(g.ExePath))!;
        var inside = exeDir.Equals(install, StringComparison.OrdinalIgnoreCase) || exeDir.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return inside ? [install] : [install, exeDir];
    }

    /// <summary>The game's recorder location has a working install watcher: made now if missing or failed, a bounded number of
    /// times. False: none, so a change there couldn't disarm it, and it isn't armed.</summary>
    bool Watched(Game g)
    {
        var key = WatchKey(g);
        lock (_installWatchers)
        {
            var w = _installWatchers.GetValueOrDefault(key);
            var ids = WatchedRoots(g).Select(DirectoryId).ToArray();   // before a watcher is made: one made on a newer folder differs next time
            if (w.Watchers != null && !_watchFailed.ContainsKey(key))
            {
                if (w.Ids != null && Array.TrueForAll(ids, i => i != null) && ids.SequenceEqual(w.Ids)) return true;
                Disarm(g);   // a root replaced at its path (renamed aside, another put there) or unreadable: the watcher follows the old one
            }
            if (w.Tries >= MaxWatchTries) return false;
            Unwatch(w with { GameId = g.Id });   // made or made again: the time without one counts as a change
            _watchFailed.TryRemove(key, out _);
            var made = WatchInstall(key, g);
            _installWatchers[key] = (made, w.Tries + 1, g.Id, ids);
            return made != null;
        }
    }

    /// <summary>The files the app and the proxy write next to the exe: none is an anti-cheat marker.</summary>
    static readonly HashSet<string> RecorderOwnFiles = new([.. RecorderDataFiles, .. Recordings.AppFiles, "d3d12.dll", "scskiller.ini", ChainName, ArmedFile,
        "d3d12.dll" + TempSuffix, "scskiller.ini" + TempSuffix], StringComparer.OrdinalIgnoreCase);

    /// <summary>The file types a game or a mod writes as it runs that anti-cheat never ships as: logs, settings and presets,
    /// pages, images, saves, dumps, shader caches. Every other type counts, an unknown one included.</summary>
    static readonly HashSet<string> DataTypes = new([".log", ".ini", ".txt", ".json", ".cfg", ".xml", ".csv", ".md", ".html",
        ".png", ".jpg", ".jpeg", ".bmp", ".dds", ".tga", ".sav", ".tmp", ".bak", ".dmp", ".cache", ".cache2", ".pdb", ".error"], StringComparer.OrdinalIgnoreCase);

    /// <summary>A <see cref="DataTypes"/> file by its name alone, no anti-cheat marker's name (<see cref="GameFiles.DetectAntiCheat"/>
    /// goes by names only); ReShade's ReShade.log1 to .log9 too (its log while another holds ReShade.log), and Ubisoft
    /// Connect's install state, which it writes at every launch.</summary>
    static bool DataFile(string name)
    {
        var ext = Path.GetExtension(name);
        return GameFiles.Marker(name) == AntiCheat.None
            && (DataTypes.Contains(ext) || ext.Length == 5 && ext.StartsWith(".log", StringComparison.OrdinalIgnoreCase) && ext[4] is >= '1' and <= '9'
                || name.Equals("uplay_install.state", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A created or renamed entry that can't bring anti-cheat, so the install watcher doesn't disarm for it: a
    /// <see cref="DataFile"/>, an empty folder without a marker's name (one moved in carries its contents, which raise no
    /// events of their own; files made in a new one raise theirs), or anything without a marker's name in a sym folder under
    /// <paramref name="root"/>: the symbol store an Unreal crash reporter's debugger fills with copies of system dlls.</summary>
    internal static bool Harmless(string path, string? root = null)
    {
        if (GameFiles.Marker(Path.GetFileName(path)) != AntiCheat.None) return false;
        if (root != null && Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Contains("sym", StringComparer.OrdinalIgnoreCase)) return true;
        try
        {
            var a = File.GetAttributes(path);
            if ((a & FileAttributes.ReparsePoint) != 0) return false;
            return (a & FileAttributes.Directory) != 0 ? !Directory.EnumerateFileSystemEntries(path).Any() : DataFile(Path.GetFileName(path));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return true; }   // gone (a temp file renamed): what it became raises its own event
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    List<FileSystemWatcher>? WatchInstall(string key, Game g)
    {
        _installChanged.TryAdd(key, false);   // looked at once: it may have changed unwatched
        var exeDir = Path.GetDirectoryName(Path.GetFullPath(g.ExePath))!;
        // one of those names next to the exe, and a regular file now: a folder so named (one moved in carries its contents,
        // which raise no events of their own), a link or a path that can't be read is not ignored
        bool OwnFile(string path)
        {
            if (!Path.GetDirectoryName(path)!.Equals(exeDir, StringComparison.OrdinalIgnoreCase) || !RecorderOwnFiles.Contains(Path.GetFileName(path))) return false;
            try { return (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0; }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return true; }   // gone (a temp file renamed): what it became raises its own event
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
        }
        var made = new List<FileSystemWatcher>();
        try
        {
            foreach (var root in WatchedRoots(g))
            {
                var w = MakeWatcher(root);
                made.Add(w);
                w.IncludeSubdirectories = true;
                w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName;
                void Changed(object? _, FileSystemEventArgs e)
                {
                    // a recorder file renamed away by someone else disarms; its own files next to the exe (an install, its
                    // arming, a session) and what can't carry anti-cheat (a mod's log, an ini, a save) don't
                    var old = (e as RenamedEventArgs)?.OldFullPath;
                    if (OwnFile(e.FullPath)) return;
                    var ownRenamed = old != null && Path.GetDirectoryName(old)!.Equals(exeDir, StringComparison.OrdinalIgnoreCase) && RecorderOwnFiles.Contains(Path.GetFileName(old));
                    if (!ownRenamed && Harmless(e.FullPath, root)) return;
                    DisarmFromWatcher(g, ownRenamed ? $"{old} renamed to {e.FullPath}" : $"{e.FullPath} {(old != null ? "renamed" : "created")}");   // in the event itself, before any check: a launch from now on is a pass-through
                    _installChanged[key] = true;
                }
                w.Created += Changed;
                w.Renamed += Changed;
                w.Error += (_, _) =>   // events lost: disarmed, checked, and the watcher made again
                {
                    DisarmFromWatcher(g, "the install watcher lost events");
                    (_installChanged[key], _watchFailed[key]) = (true, true);
                };
                w.EnableRaisingEvents = true;
            }
            return made;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            made.ForEach(x => x.Dispose());
            return null;
        }
    }

    // WatchKey of a game with the recorder in -> its watchers, how many times they were made, and the watched folders' identities
    internal readonly Dictionary<string, (List<FileSystemWatcher>? Watchers, int Tries, string GameId, string?[]? Ids)> _installWatchers = [];

    /// <summary>A folder's identity: its volume serial and file ID (a folder renamed aside and another put at its path
    /// differ), else its creation time; null when it can't be read.</summary>
    static string? DirectoryId(string dir)
    {
        using (var h = CreateFileW(dir, 0x80 /* FILE_READ_ATTRIBUTES */, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, 0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, 0))
            if (!h.IsInvalid && GetFileInformationByHandle(h, out var i)) return $"{i.VolumeSerial:x8}:{i.IndexHigh:x8}{i.IndexLow:x8}";
        try { return Directory.Exists(dir) ? $"c{Directory.GetCreationTimeUtc(dir).Ticks}" : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    struct ByHandleFileInformation
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh, VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access, FileShare share, nint security, FileMode mode, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, out ByHandleFileInformation info);
    // watch key -> true: a change event; false: a new watcher
    readonly ConcurrentDictionary<string, bool> _installChanged = new(), _watchFailed = new();
    readonly ConcurrentDictionary<string, Stopwatch> _removalTried = new();   // game id -> the last retry of a removal anti-cheat asked for

    /// <summary>Next to our proxy: [scskiller] armed=1, the check's time and the exe's fingerprint, written after a clean full
    /// anti-cheat check of the install while a watcher of it runs, and deleted on any change there or any finding. The
    /// proxy records only when it says so at the game's first device; missing or anything else is a pass-through.</summary>
    public const string ArmedFile = "scskiller.armed";
    readonly ConcurrentDictionary<string, long> _installGen = new();   // game id -> install changes and findings seen (each disarms)
    readonly object _armLock = new();

    internal long InstallGen(Game g) => _installGen.GetValueOrDefault(g.Id);

    void CountChange(string gameId) => _installGen.AddOrUpdate(gameId, 1, (_, n) => n + 1);

    /// <summary><see cref="Watched"/>; without a working watcher, disarmed (an armed file an earlier session left included).</summary>
    bool WatchOrDisarm(Game g)
    {
        if (Watched(g)) return true;
        Disarm(g);
        return false;
    }

    /// <summary>The exes the game's recorder may be armed for: the game's, and the one its recorder was installed next to.</summary>
    static IEnumerable<string> ArmedExes(Game g, GameRecord? rec) =>
        new[] { g, rec == null ? g : RecordedAt(g, rec) }.Select(x => x.ExePath).Distinct(StringComparer.OrdinalIgnoreCase);

    static string ArmedFileOf(string exe) => Path.Combine(Path.GetDirectoryName(exe)!, ArmedFile);

    /// <summary>Revoked first in the app's ledger (its own folder, which nothing in a game holds open), then next to the exe.
    /// Only a ledger entry that can't be revoked leaves the game revocation-pending: without it the proxy admits nothing.</summary>
    void Disarm(Game g)
    {
        if (OfflineLive(g.Id)) return;   // bound to the process an offline session started: it admits no other
        CountChange(g.Id);   // before the lock: an Arm writing now sees it and takes its file back
        NotClean(g);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var left = RevokeAt(g, [g.ExePath], done);   // the location the caller knows at once: the game's record may be held, and loads slowly then
        GameRecord? rec = null;
        try { rec = Store.LoadGame(g.Id); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        left.AddRange(RevokeAt(g, ArmedExes(g, rec), done));   // then where the record says the recorder was installed
        lock (_armLock)
        {
            if (left.Count == 0) _revocationPending.TryRemove(g.Id, out _);
            else if (_revocationPending.TryAdd(g.Id, g)) RecorderLog($"{g.Name}: couldn't disarm the recorder ({string.Join(", ", left)}): held open; tried again at every watcher pass");
        }
    }

    /// <summary>The install watcher's disarm. In the event (notifications are handled one at a time) only what needs no game
    /// record: the watched exe's ledger entry, mark and armed file. The rest (where the record says the recorder was
    /// installed, the pending decision) on a background task; the game isn't armed until it's done.</summary>
    void DisarmFromWatcher(Game g, string why)
    {
        _disarmWork.AddOrUpdate(g.Id, 1, (_, n) => n + 1);   // first: an arming from now on refuses
        var was = Armed(g);
        CountChange(g.Id);
        NotClean(g, save: false);   // saved by the disarm below
        if (RevokeAt(g, [g.ExePath], new(StringComparer.OrdinalIgnoreCase)).Count > 0) _revocationPending.TryAdd(g.Id, g);
        Task.Run(() =>
        {
            try { Disarm(g); }
            finally { _disarmWork.AddOrUpdate(g.Id, 0, (_, n) => n - 1); }
        });
        if (was) Task.Run(() => RecorderLog($"{g.Name}: recorder disarmed until the install is checked again: {why}"));   // a slow log sink never holds up the next event's revocation
    }

    readonly ConcurrentDictionary<string, int> _disarmWork = new();   // game id -> the watcher's disarms still running in the background

    internal bool DisarmQueued(Game g) => _disarmWork.GetValueOrDefault(g.Id) > 0;

    /// <summary>Revokes the attestations of <paramref name="exes"/> not in <paramref name="done"/>: the ledger entries whose
    /// revocation failed (a mark made beside each).</summary>
    List<string> RevokeAt(Game g, IEnumerable<string> exes, HashSet<string> done)
    {
        var left = new List<string>();
        lock (_armLock)
            foreach (var exe in exes.Where(done.Add))
            {
                left.AddRange(RevokeLedgers(exe));
                if (!Revoke(ArmedFileOf(exe), "[scskiller]\r\narmed=0\r\n"u8))
                    RecorderLog($"{g.Name}: couldn't revoke {ArmedFileOf(exe)} (held open): its ledger entry is what the proxy needs");
            }
        return left;
    }

    /// <summary>Revokes every ledger entry of the exe (<see cref="ArmedLedgers"/>): the ones whose revocation failed, a
    /// mark made beside each.</summary>
    internal static List<string> RevokeLedgers(string exe)
    {
        var left = new List<string>();
        foreach (var ledger in ArmedLedgers(exe))
            if (!Revoke(ledger, "[scskiller]\r\nnonce=\r\n"u8))
            {
                left.Add(ledger);
                try { File.WriteAllText(ledger + ".revoked", ""); }   // a new file, made even beside a locked entry: the proxy refuses on it
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        return left;
    }

    /// <summary>No valid attestation left at <paramref name="file"/>: deleted; else (held open without delete sharing, by an
    /// antivirus or an indexer) emptied and rewritten as <paramref name="revoked"/>; else renamed aside. False: none worked.</summary>
    static bool Revoke(string file, ReadOnlySpan<byte> revoked)
    {
        if (!File.Exists(file)) return true;
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try
        {
            using var f = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            f.SetLength(0);   // first: read meanwhile, it's empty, a pass-through
            f.Write(revoked);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try
        {
            File.Move(file, $"{file}.{Guid.NewGuid():N}.revoked");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return !File.Exists(file);
    }

    /// <summary>Games whose ledger entry couldn't be revoked (held open, locked): never armed, revoked again at every watcher
    /// pass until it is.</summary>
    readonly ConcurrentDictionary<string, Game> _revocationPending = new();

    internal bool RevocationPending(string gameId) => _revocationPending.ContainsKey(gameId);

    /// <summary>After a clean full check that started at <paramref name="gen"/>: armed, unless no watcher runs for the game's
    /// recorder location, the install changed (or anti-cheat was found) since, or our proxy isn't there. A game added by hand
    /// is armed only for the folder the user confirmed: the one the check covered.</summary>
    void Arm(Game g, long gen, bool recheck = false)
    {
        if (Unconfirmed(g))
        {
            Disarm(g);
            return;
        }
        if (DisarmQueued(g)) return;
        if (!WatchOrDisarm(g))
        {
            ArmFailed(g, "no install watcher could be started");
            return;
        }
        // a check that began before the game was followed to another exe; read outside the lock, which revocations take
        var rec = Store.LoadGame(g.Id);
        if (rec.RunsExe is { } runs && !runs.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase)) return;
        var followedFrom = rec.RunsExeWas is { } old && !old.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase)
            && !Games.Any(s => s.Game.Id != g.Id && s.Game.ExePath.Equals(old, StringComparison.OrdinalIgnoreCase)) ? old : null;
        var armed = false;
        lock (_armLock)
        {
            if (InstallGen(g) != gen || _revocationPending.ContainsKey(g.Id) || DisarmQueued(g)) return;
            if (g.Store == Core.Store.Manual && Manual?.ConfirmedNow(g) != true) return;   // the folder checked is still the one confirmed
            try
            {
                if (!IsOurProxy(Path.Combine(Path.GetDirectoryName(g.ExePath)!, "d3d12.dll")) || BlockingMod(g) != null) return;   // a blocking HDR mod: never recorded
                var was = Armed(g) && ExeAsArmed(g);
                DeleteRevocationMark(g.ExePath);   // first; one that can't go keeps it unarmed
                WriteAttestation(g.ExePath);
                if (InstallGen(g) != gen || DisarmQueued(g)) RevokeLedgers(g.ExePath);   // a change counted while it was written
                else
                {
                    _armFailures.TryRemove(g.Id, out _);
                    if (followedFrom != null) DropFollowedFrom(followedFrom);
                    if (!was || recheck) RecorderLog($"{g.Name}: recorder armed for {Path.GetFileName(g.ExePath)}" + (was ? " again: its folders changed since their last check" : ""));
                    armed = true;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ArmFailed(g, e.Message); }   // not armed: a pass-through until the next pass arms it
        }
        if (armed) IndexForRecorder(g);
    }

    /// <summary>Why the proxy passed the exe's last launch through, from the file it leaves beside the exe's ledger entry
    /// ("&lt;unix ms&gt; &lt;reason&gt;"; an admitted launch deletes it), as the proxy wrote it; null = none.</summary>
    internal static string? Refused(string exe)
    {
        string text;
        try { text = File.ReadAllText(LedgerFile(exe) + ".refused"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return text.Split(' ', 2) is [_, var w] && w.Trim() is { Length: > 0 } why ? why : null;
    }

    /// <summary>The recorder was in place before the last watched run started, yet wrote neither its log nor a refusal during
    /// it: no D3D12 device of the game reached it.</summary>
    internal static bool NeverSaw(string exe, string exeDir, PlayWindow? run)
    {
        if (run is not { } p) return false;
        var from = p.From.UtcDateTime;
        static DateTime? Written(string f) => File.Exists(f) ? File.GetLastWriteTimeUtc(f) : null;
        var dll = Path.Combine(exeDir, "d3d12.dll");
        // the creation time: a copy keeps the build's write time
        return File.Exists(dll) && File.GetCreationTimeUtc(dll) < from
            && !(Written(Path.Combine(exeDir, "scskiller.log")) >= from) && !(Written(LedgerFile(exe) + ".refused") >= from);
    }

    /// <summary>False when the exe's armed file names another size or write time than the exe has now (the proxy refuses it).</summary>
    static bool ExeAsArmed(Game g) => ArmedAs(g.ExePath) != false;

    /// <summary>Whether the armed file beside <paramref name="exe"/> names it as it is now; null when it says nothing is armed,
    /// is missing or can't be read.</summary>
    static bool? ArmedAs(string exe)
    {
        try
        {
            var lines = SharedLines(ArmedFileOf(exe)).ToList();
            string? Value(string k) => lines.FirstOrDefault(l => l.StartsWith(k + "=", StringComparison.Ordinal))?[(k.Length + 1)..];
            if (Value("armed") != "1") return null;
            var f = new FileInfo(exe);
            return Value("exe_size") == f.Length.ToString() && Value("exe_time") == f.LastWriteTimeUtc.ToFileTimeUtc().ToString();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The exe the game was followed away from, once it is armed for the one it runs: its ledger entries and the
    /// proxy's refusal beside them go, unless another game has that exe or the armed file beside it still names it.</summary>
    void DropFollowedFrom(string old)
    {
        if (ArmedAs(old) == true) return;
        RevokeLedgers(old);
        foreach (var ledger in LedgerFiles(old))
            try { File.Delete(ledger + ".refused"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Armed now: the exe's ledger entry and its armed file are there (their nonces are the proxy's check).</summary>
    static bool Armed(Game g) => File.Exists(LedgerFile(g.ExePath)) && File.Exists(ArmedFileOf(g.ExePath));

    /// <summary>An installed recorder that isn't armed gets a full check for arming at a pass between the full ones: every
    /// <see cref="UnarmedRetryInterval"/>, <see cref="UnarmedRetries"/> times, then only at the full passes. Never one that
    /// is unarmed on purpose (not confirmed, a blocking HDR mod, a proxy not ours).</summary>
    bool UnarmedDue(Game g)
    {
        if (Armed(g))
        {
            _unarmedRetry.TryRemove(g.Id, out _);
            return false;
        }
        var (at, tries) = _unarmedRetry.GetValueOrDefault(g.Id);
        if (tries >= UnarmedRetries || at != null && at.Elapsed < UnarmedRetryInterval) return false;
        _unarmedRetry[g.Id] = (Stopwatch.StartNew(), tries + 1);
        try { return !Unconfirmed(g) && IsOurProxy(Path.Combine(Path.GetDirectoryName(g.ExePath)!, "d3d12.dll")) && BlockingMod(g) == null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    readonly ConcurrentDictionary<string, (Stopwatch? At, int Tries)> _unarmedRetry = new();   // game id -> its last check for arming
    /// <summary>Replaceable for tests.</summary>
    public TimeSpan UnarmedRetryInterval { get; set; } = TimeSpan.FromSeconds(30);
    const int UnarmedRetries = 3;

    void ArmFailed(Game g, string why)
    {
        if (_armFailures.TryGetValue(g.Id, out var was) && was == why) return;   // retried at every pass: logged once
        _armFailures[g.Id] = why;
        RecorderLog($"{g.Name}: couldn't arm the recorder ({why}): tried again at every watcher pass");
    }

    readonly ConcurrentDictionary<string, string> _armFailures = new();   // game id -> the last arming failure logged

    /// <summary>The app's ledger of armed exes, outside every game folder: an entry per exe holds the nonce its
    /// <see cref="ArmedFile"/> must hold too. The proxy finds it through FOLDERID_LocalAppData.</summary>
    internal static string LedgerDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCSKiller", "armed");   // replaceable for tests

    /// <summary>Deletes the exe's revocation mark, on which the proxy refuses. Throws when it can't.</summary>
    internal static void DeleteRevocationMark(string exe)
    {
        Directory.CreateDirectory(LedgerDir);   // File.Delete throws on a missing folder: a fresh PC has no ledger yet
        foreach (var ledger in LedgerFiles(exe)) File.Delete(ledger + ".revoked");
    }

    /// <summary>The exe's ledger entries, as the proxy names them (SHA-1 in hex): of its file identity (FILE_ID_INFO, the
    /// volume serial and 128-bit file id: the same through any link, where a packaged app's process names its exe by the
    /// package's WindowsApps link, not the folder the store installed it to), then of its final path, then of the path as
    /// given (UTF-16LE, A-Z lowered). The proxy takes the first it can read; one for each covers a proxy that can't read
    /// the identity or the final path, and one from before them.</summary>
    internal static IReadOnlyList<string> LedgerFiles(string exe)
    {
        var path = Path.GetFullPath(exe);
        var keys = new List<string>();
        if (GameFiles.Identity(path) is var (id, final))
        {
            if (id != null) keys.Add(Convert.ToHexStringLower(SHA1.HashData(id)));
            if (final != null) keys.Add(PathKey(final));
        }
        keys.Add(PathKey(path));
        return [.. keys.Distinct().Select(k => Path.Combine(LedgerDir, k))];
    }

    /// <summary>The ledger entry the proxy reads when it can read what the app can (<see cref="LedgerFiles"/>' first).</summary>
    internal static string LedgerFile(string exe) => LedgerFiles(exe)[0];

    /// <summary><see cref="LedgerFiles"/>, and every other entry armed for the exe by its exe= line: one keyed on a file
    /// or a link target the path no longer reaches.</summary>
    static IEnumerable<string> ArmedLedgers(string exe)
    {
        var path = Path.GetFullPath(exe);
        var named = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(LedgerDir).Where(f => Path.GetFileName(f).Length == 40))
                try
                {
                    if (SharedLines(f).Any(l => l.StartsWith("exe=", StringComparison.Ordinal) && string.Equals(l[4..], path, StringComparison.OrdinalIgnoreCase)))
                        named.Add(f);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // held open: revoked if it is one of LedgerFiles
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return LedgerFiles(exe).Concat(named).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The file's lines, read with every sharing: another process's revocation (delete, empty, rename) never fails on
    /// this read. The ledger is read by every SCSKiller process and the command line at once.</summary>
    internal static IEnumerable<string> SharedLines(string path)
    {
        using var r = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (r.ReadLine() is { } line) yield return line;
    }

    static string PathKey(string path)
    {
        var lowered = string.Create(path.Length, path, (to, p) => { for (var i = 0; i < p.Length; i++) to[i] = p[i] is >= 'A' and <= 'Z' ? (char)(p[i] + 32) : p[i]; });
        return Convert.ToHexStringLower(SHA1.HashData(System.Text.Encoding.Unicode.GetBytes(lowered)));
    }

    /// <summary>Arms the exe: a fresh nonce in its ledger entries first, then in <see cref="ArmedFile"/> next to it.
    /// <paramref name="process"/>: for that process alone (its pid and creation FILETIME), an offline session's.</summary>
    internal static void WriteAttestation(string exe, (int Pid, long Created)? process = null)
    {
        var nonce = RandomNumberGenerator.GetHexString(32, lowercase: true);
        var bound = process is { } p ? $"pid={p.Pid}\r\npid_time={p.Created}\r\n" : "";
        Directory.CreateDirectory(LedgerDir);
        foreach (var ledger in LedgerFiles(exe)) File.WriteAllText(ledger, $"[scskiller]\r\nnonce={nonce}\r\nexe={Path.GetFullPath(exe)}\r\n{bound}");
        File.WriteAllText(ArmedFileOf(exe), ArmedText(exe, nonce) + bound);
    }

    /// <summary><see cref="ArmedFile"/>'s text for the install as it is now: the ledger's nonce, and the exe's size and write
    /// time (FILETIME, UTC), which the proxy compares with its process's exe, so an update while the app was closed (no
    /// watcher) isn't armed.</summary>
    internal static string ArmedText(string exe, string nonce)
    {
        var f = new FileInfo(exe);
        return "[scskiller]\r\n; written by SCSKiller after a clean anti-cheat check of the install, deleted when it changes: "
            + $"the recorder records only while this says armed=1, its ledger entry has this nonce and the exe is as it was then\r\narmed=1\r\nchecked={DateTimeOffset.UtcNow:O}\r\n"
            + $"nonce={nonce}\r\nexe_size={f.Length}\r\nexe_time={f.LastWriteTimeUtc.ToFileTimeUtc()}\r\n";
    }

    /// <summary>A start only raises GameChanged (nothing re-read: the recorder may be writing). One caller at a time.</summary>
    public void PollGames()
    {
        var games = Games;
        if (games.Count == 0) return;
        var at = Clock();
        var running = new HashSet<string>(Running(), StringComparer.OrdinalIgnoreCase);
        // also a game a launcher started under another exe name of its folders
        var now = games.Where(s => running.Contains(Path.GetFileName(s.Game.ExePath)) || RunsFromItsFolder(s.Game)).Select(s => s.Game.Id).ToHashSet();
        var was = _playing;
        var started = now.Where(id => !was.Contains(id)).ToList();
        var ended = new List<string>();
        foreach (var id in was)
            if (now.Contains(id)) _absent.Remove(id);
            else if ((_absent[id] = _absent.GetValueOrDefault(id) + 1) >= ExitPolls) ended.Add(id);
        // a run already going at the first poll has no known start: its window would take in older launches too
        foreach (var id in started) if (_lastPoll is { } before) _runs[id] = new PlayWindow(before, at);
        foreach (var id in started) if (_recorderIndexes.TryGetValue(id, out var indexing)) indexing.Cancel();
        foreach (var id in now) if (_runs.TryGetValue(id, out var w)) _runs[id] = w with { To = at };
        _lastPoll = at;
        // asked at every poll while the game runs until decided: a launcher of its folder may start the game minutes later
        _toFollow.UnionWith(started);
        _toFollow.IntersectWith(now);
        foreach (var s in games.Where(s => _toFollow.Contains(s.Game.Id)))
            try
            {
                if (FollowRunningExe(s, at)) _toFollow.Remove(s.Game.Id);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _toFollow.Remove(s.Game.Id);
                Log?.Report($"{s.Game.Name}: couldn't record the exe it runs: {e.Message}");
            }
        if (started.Count + ended.Count == 0) return;
        foreach (var id in ended)
            if (_runs.Remove(id, out var run))
            {
                var rec = Store.LoadGame(id);
                rec.LastPlay = run;
                Store.SaveGame(id, rec);
            }
        foreach (var id in ended) _absent.Remove(id);
        _playing = [.. was.Except(ended), .. started];
        foreach (var id in started)
        {
            GameState? state = null;
            lock (_lock)
                if (_games.FindIndex(x => x.Game.Id == id) is var i and >= 0) _games[i] = state = _games[i] with { Playing = true };
            if (state != null) GameChanged?.Invoke(state);
            if (AppCache != null && state is { AntiCheat: AntiCheat.None }) _ = Task.Run(() => LearnWhilePlaying(state.Game));
        }
        foreach (var s in games.Where(s => ended.Contains(s.Game.Id)))
        {
            var g = Current(s.Game);   // a store updates a game before it starts: its first exit after that shows it Stale
            Refresh(g);
            StartSharing([g]);   // also without a new recording: the command line's compile indexes a build but can't share
            if (ManageRecorders)   // a recorder change that waited for the game to exit (Reconcile)
                try { ReconcileRecorders(g.Id); }
                catch (Exception e) { Log?.Report($"{g.Name}: reconciling the recorder failed: {e.Message}"); }
        }
        if (ended.Count > 0) KeepList();
    }

    public void RefreshGame(string gameId) => Refresh(Find(gameId).Game);

    /// <summary>NVIDIA: how much of the game's driver-cache files is filled (<see cref="NvidiaAppCache.UsedBytes"/>); null elsewhere.</summary>
    long? CacheInUse(IReadOnlyCollection<string> keys) => AppCache is NvidiaAppCache nv && keys.Count > 0 ? nv.UsedOf(keys) : null;

    public void RefreshCacheSizes()
    {
        if (AppCache is not { } cache) return;
        foreach (var s in Games)
        {
            var keys = Store.LoadGame(s.Game.Id).CacheKeys;
            if (keys.Count == 0 || (cache.SizeOf(keys), CacheInUse(keys)) is var (size, used) && size == s.CacheOnDisk && used == s.CacheInUse) continue;
            var now = s with { CacheOnDisk = size, CacheInUse = used };
            lock (_lock)
            {
                var i = _games.FindIndex(x => x.Game.Id == s.Game.Id);
                if (i < 0 || _games[i] != s) continue;   // re-evaluated meanwhile: that state is newer
                _games[i] = now;
            }
            GameChanged?.Invoke(now);
        }
    }

    static TimeSpan UserIdleTime()
    {
        var info = new LastInputInfo { Size = 8 };
        // No input info (not an interactive session): don't hold background work back forever; games still pause it.
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.MaxValue;
    }

    struct LastInputInfo { public uint Size, Time; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInputInfo info);

    sealed class Reporter<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
