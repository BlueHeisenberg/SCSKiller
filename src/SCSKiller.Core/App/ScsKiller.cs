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
using SCSKiller.Core.Vulkan;
using SCSKiller.Core.Warming;

namespace SCSKiller.Core.App;

/// <summary>The facade the GUI and CLI use: discovery, per-game state, the sequential compile queue, the recorder.</summary>
public sealed partial class ScsKiller : IScsKiller
{
    public const double DefaultBytesPerPso = 24 * 1024, DefaultPsoPerSecond = 450;

    /// <summary>The cache a plan item adds until a compile measures it. Wuthering Waves (Kuro's fork): 32.3 GB measured for its
    /// 742,531 raster pipelines, up to 80 GB estimated with its 125,035 ray tracing collections, so its estimate is that upper end.
    /// Marvel Rivals: 34 KB to 53 KB per pipeline measured (96 GB for 1,824,233), its estimate the upper end too.</summary>
    public static double DefaultBytesPerPsoOf(EngineInfo? e) => e?.Fork switch
    {
        "GAME_WutheringWaves" => 80.0 * (1L << 30) / (738_342 + 125_035),
        "GAME_MarvelRivals" => 96e9 / 1_824_233,
        _ => DefaultBytesPerPso,
    };

    /// <summary>What compiling the game adds to the driver cache: its estimate less what its keys already hold.</summary>
    public static long CacheGrowth(GameState s) => Math.Max(0, (s.EstimatedCacheBytes ?? 0) - (s.CacheOnDisk ?? 0));

    /// <summary>The pipelines a compile of the plan creates, as the Library counts them: less the ones that crash this driver.</summary>
    /// <summary>Whether "Add all ready" adds the game: ready or stale, not a no-stutter game, not left out on its page.</summary>
    public static bool InAddAllReady(GameState s) => s.Status is GameStatus.Ready or GameStatus.Stale && !s.CompileUnreached && s.NoStutter == null && !s.LeftOutOfAddAll;

    public static long? PlanPipelines(GameState s) => s.Plan is { } p ? Math.Max(0, p.Recorded + p.Generated + p.MiddlewareItems - (s.LastWarmCrashed ?? 0)) : null;

    /// <summary>One game's compile past this gets a warning of its own: as much as the AMD driver's whole DirectX 12 cache.</summary>
    public const long LargeCompile = AmdAppCache.DxcCacheCap;

    /// <summary>The queue's warning: a line per queued game with a <see cref="GameState.RuntimeWarning"/>; empty = none.</summary>
    public static string QueueRuntimeWarning(IEnumerable<GameState> queued) =>
        string.Join("\n", queued.Where(g => g.RuntimeWarning != null).Select(g => $"{g.Game.Name}: {g.RuntimeWarning}"));

    /// <summary>A warning about the queued game whose compile adds the most (<see cref="CacheGrowth"/>), when that is over
    /// <see cref="LargeCompile"/>; else null. It names that game's own cache estimate, as the Library shows it, in whole GB.
    /// On AMD the driver's cache can't hold it all.</summary>
    public static string? LargeCompileWarning(IEnumerable<(long Growth, long Estimate)> queue, GpuVendor vendor, long? ram = null) =>
        LargeCompileGb(queue) is { } gb ? $"This compile adds about {gb:0} GB. {LargeCacheCost(queue, vendor, ram)}"
            + (vendor == GpuVendor.Amd ? $" It's more than the AMD driver's {App.Format.Bytes(AmdAppCache.DxcCacheCap)} cache can hold." : "") : null;

    const string LargeCacheSlower = "Very large caches can make the game start slower and use more memory.";
    public const string LargeCacheOverRam = "In game, the driver keeps about half of this cache in memory, which is more than this PC's RAM leaves free.";
    /// <summary>What the game and Windows use beside the driver's cache. ponytail: one figure for every game, per game if a
    /// measured footprint is ever kept.</summary>
    public const long GameFootprint = 16L << 30;

    /// <summary>The large-compile warning's cost: on NVIDIA, where the game's process keeps about half the cache in memory
    /// (Wuthering Waves: a 16 GB working set at 32 GB of cache), when that and <see cref="GameFootprint"/> are more than
    /// the RAM (<paramref name="ram"/>, null: this PC's), else the general one.</summary>
    static string LargeCacheCost(IEnumerable<(long Growth, long Estimate)> queue, GpuVendor vendor, long? ram) =>
        vendor == GpuVendor.Nvidia && queue.DefaultIfEmpty().MaxBy(q => q.Growth).Estimate / 2 + GameFootprint > (ram ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)
            ? LargeCacheOverRam : LargeCacheSlower;

    static double? LargeCompileGb(IEnumerable<(long Growth, long Estimate)> queue) =>
        queue.DefaultIfEmpty().MaxBy(q => q.Growth) is (> LargeCompile, var estimate) ? Math.Round(estimate / (double)(1L << 30), MidpointRounding.AwayFromZero) : null;

    /// <summary>The queue's cache warning where the driver has a size limit of its own (NVIDIA; null = none): one game's estimate
    /// over the limit, a large compile (<see cref="LargeCompileWarning"/>), saying the limit when the queue goes past it, else the cache close to the limit
    /// after the queue (<paramref name="planned"/>: the queued games' estimates). Null when neither applies.
    /// <paramref name="perGame"/>: the limit is each game's own (<see cref="VendorCaps.PerGameCacheCap"/>), which no other game fills.</summary>
    public static string? QueueCacheWarning(IReadOnlyCollection<(long Growth, long Estimate)> queue, long usage, long planned, long? limit, GpuVendor vendor,
        bool perGame = false, long? ram = null)
    {
        if (limit is not { } max) return LargeCompileWarning(queue, vendor, ram);
        if (vendor == GpuVendor.Nvidia && queue.Select(q => q.Estimate).DefaultIfEmpty().Max() is var own && own > max)   // one game alone can't fit
            return perGame
                ? $"This compile adds about {Math.Round(own / (double)(1L << 30), MidpointRounding.AwayFromZero):0} GB, more than the {App.Format.Bytes(max)} "
                    + $"SCSKiller's compile folder holds for one game. The rest compiles while you play. {LargeCacheSlower}"
                : $"This compile adds about {Math.Round(own / (double)(1L << 30), MidpointRounding.AwayFromZero):0} GB, more than your {App.Format.Bytes(max)} NVIDIA shader cache limit. "
                + $"The driver would throw most of it away. Raise the limit in Settings before you compile. {LargeCacheSlower}";
        if (perGame) return LargeCompileWarning(queue, vendor == GpuVendor.Amd ? GpuVendor.Unknown : vendor, ram);   // a per-game cap is never AMD's DxcCache
        if (LargeCompileGb(queue) is { } gb && usage + planned > max)
            return $"This compile adds about {gb:0} GB and takes the cache past your {App.Format.Bytes(max)} limit, so older games may be evicted. {LargeCacheCost(queue, vendor, ram)}";
        return LargeCompileWarning(queue, vendor, ram)
            ?? (planned > 0 && usage + planned > 0.9 * max ? $"After this queue the cache is close to its {App.Format.Bytes(max)} limit, older games may be evicted." : null);
    }
    /// <summary>The queue warning's "Raise the limit": a limit the app sets that isn't Unlimited. Unread (null) may still be raised.</summary>
    public static bool CanRaiseCacheLimit(VendorCaps caps, CacheLimit? limit) => caps.CacheSizeConfigurable && limit is not { Bytes: null };
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
    /// <summary>The recorder built as an AMD AGS wrapper (proxy/CMakeLists.txt scskiller_ags), installed as <see cref="AgsName"/>.</summary>
    internal string? AgsDll { get; set; }
    readonly object _lock = new(), _scanLock = new();
    readonly List<QueueItem> _queue = [];
    readonly HashSet<string> _whenIdle = [];         // queued with EnqueueWhenIdle
    readonly HashSet<string> _rightAway = [];        // of _whenIdle, queued with EnqueueRightAway: they don't wait for idle (WaitsForIdle)
    readonly HashSet<string> _planOnly = [];         // queued by CheckPlans: rebuild the plan, no warm
    readonly HashSet<string> _maxNext = [];          // CompileMax: the next complete warm compiles the Maximum plan
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
    volatile bool _foldHalt;                         // a stop or a quit since the compile's fold began: it rolls back
    string? _yielding;                               // the plan check cancelled for a user's compile: queued again
    Task? _abandoned;                                // that check's read or plan, left to end on its own: it is queued again after it
    readonly HashSet<string> _checkBatch = [];       // the plan checks queued since none was pending: AfterUpdateLine's count
    volatile string? _pauseWhy;                      // why Watch suspended the running warm (game running, user at the PC)
    readonly ConcurrentDictionary<string, long> _ownGameSeen = new(StringComparer.OrdinalIgnoreCase);   // warm exe -> when its game was last seen running during its compile
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
        if ((reader as UnrealReader ?? (reader as EngineReaders)?.Get<UnrealReader>()) is { } unreal) unreal.UserCaches = g => UnrealUserCache.FilesOf(g, SavedRoots(g));
        _planner = planner;
        if (planner is Planner p) p.UsesShared = UsesShared;
        _warmer = warmer;
        Store = new AppStore(dataDir);
        DeleteLedger();   // first: every 1.2 proxy is a pass-through from here on, whatever fails later
        try { MigrateInis(Store); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecordersLog(Store.DataDir, $"the records' scskiller.ini entries weren't moved yet: {e.Message}"); }
        D3DSCache.Warn = m => Log?.Report(m);
        GameFiles.Warn = m => Log?.Report(m);
        D3DSCache.SweepWarmRegistrations();
        Middleware.LoadImages(ImagesFile);
        ReShade.LoadProbes(ProbesFile);
        if (StutterList.Cached(StutterFile) is { } cached) StutterList.Current = cached;
        if (ConfirmedEngines.Cached(ConfirmedFile) is { } confirmed) ConfirmedEngines.Current = confirmed;
        if (BootPrecompiles.Cached(PrecompilesFile) is { } precompiles) BootPrecompiles.Current = precompiles;
        if (RulesTrust.Cached(GameVerdicts.Name, GameVerdicts.TryParse, VerdictsFile, null) is { } verdicts) GameVerdicts.Current = verdicts;
        if (RulesTrust.Cached(MaximumMode.Name, MaximumMode.TryParse, MaximumFile, null) is { } maximum) MaximumMode.Current = maximum;
        if (RulesTrust.Cached(GameExes.Name, GameExes.TryParse, ExesFile, null) is { } exes) GameExes.Current = exes;
        if (RulesFile.Cached(RulesPath, RulesTrust, null) is { } rules) RulesFile.Current = RulesFile.WithServer(rules);
        News = News.Cached(NewsPath, RulesTrust, null) ?? News.Empty;
        _compat = CompatFeed.Cached(CompatTrust, CompatFile);
        _proxyDll = proxyDll;
        AppCache = vendor.AppCache;
        RunningGameExes = DiscoveredGamesRunning;
        OfflineRuns = s => OthersRun(s, Processes(true));
        _startGpu = Settings.Gpu;
    }


    public static partial ScsKiller CreateDefault();

    /// <summary>The backend for <paramref name="choice"/>, else the adapter the user picked in the default data dir's settings
    /// (<see cref="Settings.Gpu"/>).</summary>
    public static IGpuVendorBackend DetectGpu(GpuChoice? choice = null)
    {
        var s = new AppStore(AppStore.DefaultDir).LoadSettings();
        return GpuBackends.Detect(choice ?? s.Gpu, s.ExperimentalIntel);
    }

    /// <summary>The CLI's option for the adapter (its LUID, hex) a command acts on instead of the picked one.</summary>
    public const string AdapterArg = "--adapter-luid";

    /// <summary>The GPU vendor in a shared pack's key (<see cref="HashOnly.PackKey"/>); null = a vendor without shared packs.</summary>
    public static string? PackGpu(GpuVendor v) => v switch { GpuVendor.Nvidia => "nvidia", GpuVendor.Amd => "amd", GpuVendor.Intel => "intel", _ => null };

    /// <summary>The GPU vendor recordings and launch reports are shared with: lowercase, as the server takes it ("intel").</summary>
    public static string ShareVendor(GpuVendor v) => v.ToString().ToLowerInvariant();

    /// <summary>A driver version as the server takes it: letters, digits and ._+-, at most 32 ("26.8.1 (32.0.31041.1004)" is
    /// "26.8.1+32.0.31041.1004"); null when nothing is left.</summary>
    public static string? ShareDriverVersion(string v) =>
        System.Text.RegularExpressions.Regex.Replace(v, "[^A-Za-z0-9._+-]+", "+").Trim('+') is { Length: > 0 and <= 32 } s ? s : null;

    /// <summary>Where the shared packs downloaded for this GPU vendor live: community\packs\&lt;gpu&gt;\&lt;vendor&gt;\.</summary>
    public static string? SharedPackDir(string dataDir, GpuVendor v) => PackGpu(v) is { } gpu ? Path.Combine(dataDir, "community", "packs", gpu) : null;

    /// <summary>Unreal first, then the engines whose archives the carver can't see into (FromSoftware, Unity, RE Engine,
    /// REDengine 3, Dagor, Source 2, RAGE, Naughty Dog, Santa Monica, Nixxes, Tank, Decima, Luminous, ForzaTech, BlackSpace, StarEngine, Swarm, Snowdrop) or whose pipelines and root signatures it doesn't know (Northlight), then the generic raw DXBC/DXIL carver.</summary>
    public static IEngineReader DefaultReaders(string? dataDir = null)
    {
        dataDir ??= AppStore.DefaultDir;
        var carved = new CarvedReader();
        return new EngineReaders([("Unreal", new UnrealReader(dataDir)), (FromSoftReader.Family, new FromSoftReader(dataDir)),
            (UnityReader.Family, new UnityReader()), (ReEngine.ReEngineReader.Family, new ReEngine.ReEngineReader(dataDir)),
            (RedEngine.RedEngineReader.Family, new RedEngine.RedEngineReader()), (Dagor.DagorReader.Family, new Dagor.DagorReader()), (Northlight.NorthlightReader.Family, new Northlight.NorthlightReader()),
            (Source2.Source2Reader.Family, new Source2.Source2Reader()), (Rage.RageReader.Family, new Rage.RageReader()),
            (NaughtyDog.NaughtyDogReader.Family, new NaughtyDog.NaughtyDogReader()),
            (SantaMonica.SantaMonicaReader.Family, new SantaMonica.SantaMonicaReader()),
            (Nixxes.NixxesReader.Family, new Nixxes.NixxesReader()),
            (Tank.TankReader.Family, new Tank.TankReader()),
            (Decima.DecimaReader.Family, new Decima.DecimaReader(dataDir)),
            (Luminous.LuminousReader.Family, new Luminous.LuminousReader()),
            (ForzaTech.ForzaTechReader.Family, new ForzaTech.ForzaTechReader()),
            (BlackSpace.BlackSpaceReader.Family, new BlackSpace.BlackSpaceReader(dataDir)),
            (StarEngine.StarEngineReader.Family, new StarEngine.StarEngineReader()),
            (Swarm.SwarmReader.Family, new Swarm.SwarmReader()),
            (Snowdrop.SnowdropReader.Family, new Snowdrop.SnowdropReader(dataDir)),
            .. CarvedReader.Families.Select(f => (f, (IEngineReader)carved))]);
    }

    public IGpuVendorBackend Vendor { get; }
    public AppStore Store { get; }

    /// <summary>DXGI's adapters now (<see cref="GpuBackends.Adapters"/>); null = the GPU isn't re-detected.</summary>
    public Func<IReadOnlyList<DxgiAdapter>>? Adapters { get; set; }
    DxgiAdapter? _known;          // Vendor's adapter as last listed
    GpuChoice? _startGpu;         // Settings.Gpu when the backend was chosen
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
                    _goneSince = null;
                    // the backend is chosen at start: a pick (or one back from being gone) that lands on another adapter needs a restart
                    GpuRestartNote = (Settings.Gpu != null || _startGpu != null) && GpuBackends.Pick(all, Settings.Gpu, Settings.ExperimentalIntel) is { } picked && picked.Gpu.AdapterLuid != now.Gpu.AdapterLuid
                        ? $"Restart SCSKiller to use {picked.Gpu.Name}" : null;
                    if (now != _known || !_resolved) (_resolved, _known) = (Vendor is not IRefreshableGpu gpu || gpu.Refresh(now.Gpu), now);
                    if (now.Gpu.DriverVersion.Length > 0)   // none: the last one stays
                        _id = $"{(int)now.Gpu.Vendor:x4}:{now.DeviceId:x4}:{now.SubSysId:x8}:{now.Gpu.DriverVersion}";
                    if (_resolved && _id != null) _completeId = _id;
                }
                else if (Clock() - (_goneSince ??= Clock()) >= GpuGoneAfter && GpuBackends.Pick(all, Settings.Gpu, Settings.ExperimentalIntel) is { } other)
                    GpuRestartNote = $"Restart SCSKiller to use {other.Gpu.Name}";
                if (GpuRestartNote == note && Vendor.Gpu == before && DriverId == id) return false;
            }
            catch (Exception e)
            {
                Log?.Report($"re-detecting the GPU failed: {e.Message}");
                return false;
            }
        Log?.Report(GpuRestartNote ?? $"GPU: {before.Name}, driver {before.DriverVersion} -> {Vendor.Gpu.Name}, driver {Vendor.Gpu.DriverVersion}");
        DropStaleKept();
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
        // NVIDIA from 621 keeps compiled pipelines in D3DSCache, whose dbs a warm before WarmedD3DSCache deleted: compile again
        // a plan of DirectX 11 shaders only had none there: those are in the DXCache exe-name key, which such a warm kept
        bool lost = OperatingSystem.IsWindows() && s.Gpu.Vendor == GpuVendor.Nvidia && r is { WarmedAt: not null, WarmedD3DSCache: false }
            && r.WarmedDriverId == s.Id && r.Plan?.Platform != VulkanPlan.Platform
            && r.Plan?.Stats is not { D3D11Shaders: > 0, Recorded: 0, Generated: 0, MiddlewareItems: 0 } && NvidiaMajor(r.WarmedDriverVersion) >= 621;
        // the driver made that launch miss, not the game: no Aftermath retry, no unreached verdict
        if (lost) (r.WarmedDriverId, r.AftermathRetry, r.AftermathPlainHits, r.UnreachedWarm) = (LostD3DSCache, null, null, null);
        return warmed | crashed | lost;
    }

    const string LostD3DSCache = "d3dscache-deleted";

    /// <summary>621 of "621.01", or of DXGI's "32.0.16.2101".</summary>
    static int? NvidiaMajor(string? version) => version?.Split('.') switch
    {
        [var major, _] when int.TryParse(major, out var m) => m,
        [_, _, { Length: > 0 } c, var d] when int.TryParse(c[^1..] + d, out var n) => n / 100,
        _ => null,
    };

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
    /// <summary>The adapters to pick from (<see cref="GpuBackends.Usable"/>) when there are several; else none.</summary>
    public IReadOnlyList<GpuChoice> GpuChoices =>
        (Adapters?.Invoke() ?? []) is var all && GpuBackends.Usable(all, Settings.ExperimentalIntel).ToList() is { Count: > 1 } usable ? [.. usable.Select(GpuBackends.ChoiceOf)] : [];

    public bool HasIntelGpu => GpuBackends.IntelSwitch && (Adapters?.Invoke() ?? []).Any(a => a.Gpu.Vendor == GpuVendor.Intel);

    /// <summary><see cref="GpuBackends.OwnDriver"/>: Mesa has no careful compile on AMD, and no NVIDIA driver feature on NVK.</summary>
    GpuVendor OwnDriver => GpuBackends.OwnDriver(Vendor);

    /// <summary>The adapter the backend runs on, as listed now: of those with a backend first, as a GPU Linux lists once per
    /// Vulkan driver is listed with the same LUID by each (<see cref="GpuBackends.Chosen"/>). A Mesa backend follows only
    /// those: RADV's, ANV's or NVK's, never AMDVLK's.</summary>
    DxgiAdapter? Followed(IReadOnlyList<DxgiAdapter> all)
    {
        var backed = Following([.. all.Where(a => GpuBackends.HasBackend(a, Settings.ExperimentalIntel))]);
        return MesaLinux.Is(Vendor.Caps) ? backed : backed ?? Following(all);
    }

    DxgiAdapter? Following(IReadOnlyList<DxgiAdapter> all) =>
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
    /// built, or with other rules than the game gets now (<see cref="Planner.Version"/>, <see cref="RulesFile.Hash"/>): it
    /// tells whether the new plan compiles anything the warm didn't. The app sets it; the CLI doesn't (its queue waits for
    /// what it was asked to do).</summary>
    public bool CheckPlans { get; set; }

    /// <summary>A fixed <see cref="NewShaders.Minimum"/> for the status (1: every new pipeline counts); null: the plan's.</summary>
    internal long? NewMinimum { get; init; }
    /// <summary>Only the app sets it: the CLI and the scheduled task never install recorders. Off for good once the recorder
    /// was removed from every game (<see cref="RemoveRecordersFromAllGames(CancellationToken)"/>), which no install follows.</summary>
    public bool ManageRecorders { get => _manageRecorders && !_removedFromAll; set => _manageRecorders = value; }
    bool _manageRecorders;
    volatile bool _removedFromAll;
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
    /// <summary>SCSKILLER_TEST_APPDATA: a test run's stand-in for %USERPROFILE%\AppData.</summary>
    public string LocalAppData { get; set; } = Environment.GetEnvironmentVariable("SCSKILLER_TEST_APPDATA") is { Length: > 0 } test
        ? Path.Combine(test, "Local") : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string MyGames { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games");
    public string Documents { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public string RoamingAppData { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public string ProgramData { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    /// <summary>The community database's read side (the app sets it: it holds the sign-in); null = local only. What was
    /// downloaded is used either way while Settings.UseCommunityDb is on.</summary>
    public Community? Community { get; set; }
    /// <summary>The user is entitled to "db", or signed in with entitlements not read yet: Settings.UseCommunityDb is theirs to
    /// turn off. Everyone else gets the official recordings any install may download whatever that setting holds.</summary>
    public Func<bool> DbEntitled { get; set; } = () => false;
    /// <summary>The last scan's community pass (manifest check and downloads), which runs after the scan returns.</summary>
    public Task CommunitySync { get; private set; } = Task.CompletedTask;
    /// <summary>Anonymous uploads of this PC's recordings while Settings.ShareRecordings is on (the app sets it); null = none.</summary>
    public Sharing? Sharing { get; set; }
    /// <summary>The queued sharing passes (<see cref="StartSharing"/>), one at a time.</summary>
    public Task SharingPass { get; private set; } = Task.CompletedTask;
    /// <summary>A compile's diagnostics while Settings.ShareDiagnostics is on (the app sets it); null = none.</summary>
    public Diagnostics? Diagnostics { get; set; }
    /// <summary>The last compile's diagnostics, which are sent after it ends.</summary>
    public Task DiagnosticsSent { get; private set; } = Task.CompletedTask;
    /// <summary>The anonymous daily check after a scan (the app sets it); null = none.</summary>
    public ActiveCheck? ActiveCheck { get; set; }
    /// <summary>The last scan's <see cref="ActiveCheck"/>, which runs after the scan returns.</summary>
    public Task ActiveCheckSent { get; private set; } = Task.CompletedTask;
    /// <summary>Where the known-stutter and confirmed-engines lists are fetched from after a scan (the app sets it); null = the cached or embedded lists only.</summary>
    public RouteFailover? ContentRoutes { get; set; }
    /// <summary>The access token and its entitlement flags, for the planning rules, news and game verdicts of the channels
    /// they read (the app sets it); null, or no token: signed out, stable's only. When it throws, what is read isn't known
    /// and every channel's kept copy stays.</summary>
    public Func<Task<(string? Token, IReadOnlyCollection<string>? Ent)>>? ChannelAccess { get; set; }
    /// <summary>The last scan's check of both lists, which runs after the scan returns.</summary>
    public Task StutterUpdate { get; private set; } = Task.CompletedTask;

    /// <summary>How long the user has been away from keyboard and mouse (GetLastInputInfo, on Linux the desktop's idle monitor or logind). Replaceable for tests.</summary>
    public Func<TimeSpan> IdleTime { get; set; } = UserIdleTime;
    /// <summary>"When idle" items start, and resume, once the user has been away this long; input pauses them within a second.</summary>
    public TimeSpan IdleAfter { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>A warm whose done count hasn't moved for this long (not paused) shows "no progress for N min" instead of
    /// an estimate. Replaceable for tests.</summary>
    public TimeSpan StallAfter { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>A compile its own game stopped continues once the game has stayed closed this long: a game that crashed at
    /// start is often started again at once, and must not start into its compile again. Replaceable for tests.</summary>
    public TimeSpan GameClosedFor { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>The monotonic clock <see cref="GameClosedFor"/> is counted on. Replaceable for tests.</summary>
    internal Func<long> Timestamp { get; set; } = Stopwatch.GetTimestamp;

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
            var checkOn = ActiveCheck.Allowed(value) && !ActiveCheck.Allowed(Settings);   // chosen after this start's scan
            var limitChanged = value.RecordingLimitMB != Settings.RecordingLimitMB;
            var recordersChanged = value.RecordAllGames != Settings.RecordAllGames || limitChanged || value.Traversal != Settings.Traversal;   // traversal: the inis' keys
            var channelChanged = value.UpdateChannel != Settings.UpdateChannel;
            var gpuChanged = value.Gpu != Settings.Gpu;
            var riskOff = !value.RecordAntiCheatGames && Settings.RecordAntiCheatGames;
            var keptOff = !value.KeepCachesAside && Settings.KeepCachesAside;
            maximumChanged |= value.KeepCachesAside != Settings.KeepCachesAside;   // Trimmed counts kept files
            maximumChanged |= value.UseCommunityDb != Settings.UseCommunityDb;   // the recording planned from, too
            maximumChanged |= value.RecordAntiCheatGames != Settings.RecordAntiCheatGames;   // GameState.RiskEligible
            maximumChanged |= value.CompileRayTracing != Settings.CompileRayTracing;
            maximumChanged |= value.HddMode != Settings.HddMode || !(value.HddDrives ?? []).SequenceEqual(Settings.HddDrives ?? []);   // off: the games never checked are read now
            Store.SaveSettings(value);
            _settings = value;
            // Stale reasons (and RecordingPaused) depend on it. Re-evaluated off the caller's thread (the GUI's: Evaluate does
            // disk IO per game), one pass at a time so a quick toggle back ends on the current setting; the new states come as GameChanged.
            if (maximumChanged || limitChanged)
                SettingsRefresh = Task.Run(() =>
                {
                    lock (_refreshLock)
                        try
                        {
                            if (riskOff) RiskOff();
                            if (keptOff) DropStaleKept();
                            foreach (var g in Games) Refresh(g.Game);
                        }
                        catch (Exception e) { Log?.Report($"re-evaluating games for the new settings failed: {e.Message}"); }
                });
            if (channelChanged) lock (_channelLock) ApplyChannels(false);
            if (gpuChanged) Task.Run(RedetectGpu);   // the restart note
            if (communityOn) StartCommunitySync();
            if (shareOn) StartSharing();
            if (checkOn && ActiveCheck is { } active) ActiveCheckSent = Task.Run(() => active.SendAsync());
            if (recordersChanged && ManageRecorders)
                Task.Run(() =>
                {
                    try { ReconcileRecorders(); }
                    catch (Exception e) { Log?.Report($"reconciling recorders failed: {e.Message}"); }
                });
        }
    }
    readonly object _refreshLock = new();
    readonly object _scanLoop = new();

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
        Task.Run(() =>
        {
            var update = Store.LoadList() is { } kept && kept.Build != CoreBuild;
            if (update) Interlocked.Increment(ref _updateScans);
            Interlocked.Increment(ref _scanning);   // never waits for the recorder lock: a reconcile holding it may wait on this scan
            try { return userRequested ? Scanned(force, userRequested, ct) : LowIo(() => Scanned(force, userRequested, ct)); }
            finally
            {
                Interlocked.Decrement(ref _scanning);
                if (update) Interlocked.Decrement(ref _updateScans);
                Progress(null);
            }
        }, ct);

    int _updateScans;   // scans that found the list of another build: the first after an app update
    int _scanning;
    long _discoveryFailed;   // the start of the last scan whose discovery failed (under _lock)

    public const string AfterUpdateText = "Checking your games after the update. This can take a few minutes.";
    /// <summary>The Library's line for the work nobody started: the first scan after an app update, a D3DSCache fold, the plan
    /// checks (while one runs or an older planner's are due), the recorders' shader reads, the games detected again after an update; null = none.</summary>
    public string? AfterUpdateLine
    {
        get
        {
            if (Volatile.Read(ref _updateScans) > 0) return AfterUpdateText;
            if (_folds.Keys.FirstOrDefault(f => !f.Queued && f.Percent >= 0) is { } fold) return FoldText(fold.Name + "'s", fold.Percent, fold.Writing);
            lock (_lock)
                if (_queue.Where(q => q.PlanCheck && !Finished(q.Stage)).ToList() is { Count: > 0 } checks && checks.Any(q => q.GameId == _current || _prompt.Contains(q.GameId)))
                {
                    var all = Math.Max(_checkBatch.Count, checks.Count);
                    return $"Updating plans: {all - checks.Count} of {all} game{(all == 1 ? "" : "s")}";
                }
            if (Volatile.Read(ref _readingIndexes) > 0) return $"Reading game shaders for the recorder: {_recorderIndexes.Count} left";
            return _redetecting.IsCompleted ? null : $"Checking games again after the update: {_redetect.Count + 1} left";
        }
    }

    /// <summary>What the scan running does now ("Scanning Steam… 120 games found"); null = no scan runs.</summary>
    public string? ScanProgress { get; private set; }
    /// <summary><see cref="ScanProgress"/> changed, or the last list was put up at the start of a scan. Raised on the scan's thread.</summary>
    public event Action? ScanProgressed;

    void Progress(string? text)
    {
        ScanProgress = text;
        ScanProgressed?.Invoke();
    }

    /// <summary>A source as the Library names its store.</summary>
    static string SourceName(IGameSource s) => s switch
    {
        SteamSource => "Steam", EpicSource => "Epic Games", XboxSource => "Xbox", EaSource => "EA app", GogSource => "GOG",
        UbisoftSource => "Ubisoft Connect", BattleNetSource => "Battle.net", PurpleSource => "PURPLE", HoYoPlaySource => "HoYoPlay", HeroicSource => "Heroic", LutrisSource => "Lutris", FaugusSource => "Faugus", GryphlineSource => "GRYPHLINK",
        GaijinSource => "Gaijin", RiotSource => "Riot Client", ManualSource => "your games and folders", SteamShortcutSource => "Steam's non-Steam games",
        _ => s.Store.ToString(),
    };

    IReadOnlyList<GameState> Scanned(bool force, bool userRequested, CancellationToken ct)
    {
        var started = Interlocked.Increment(ref _scans);   // before discovery: a later scan's list is at least as new
        var discovering = Ticket();   // a game evaluated after it (Add game) stays listed though discovery missed it
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
            return Listed(started, discovering, kept.Games, listed);
        // the first scan: the last list shows until this one's replaces it, as a start with ScanAtStart off would show it
        bool Empty() { lock (_lock) return _listed == 0 && _games.Count == 0; }
        if (Empty() && Store.LoadList() is { } last && last.Build == CoreBuild && last.Driver == DriverId)
        {
            bool shown;
            lock (_lock)
                if (shown = _listed == 0 && _games.Count == 0)
                    _games = last.Games.Where(x => !Unlisted(x.Game.Id)).Select(x => x with { Playing = IsPlaying(x.Game.Id) }).ToList();
            if (shown) Progress(ScanProgress);
        }
        var (found, unresolved, failed) = Discover(Store.LoadDiscovered(CoreBuild), s => s.Discover(), fresh: userRequested);
        if (failed) lock (_lock) _discoveryFailed = Math.Max(_discoveryFailed, started);
        var states = new List<GameState>();
        var tickets = new List<long>();
        var driverStale = new List<bool>();
        bool detected = false;
        var each = Visible(found).DistinctBy(g => g.Id).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        // listed before any game is read: a game with no state yet shows as being checked until its evaluation replaces it
        lock (_lock) Publish(started, discovering, [.. each.Where(g => !Unlisted(g.Id)).Select(g => _games.Find(x => x.Game.Id == g.Id) ?? Unchecked(g, CheckingReason))]);
        Progress(ScanProgress);
        lock (_scanLoop)   // a scan started meanwhile (Add game) waits here: the games this one reads are cached for it
            foreach (var g in each)
            {
                ct.ThrowIfCancellationRequested();
                Progress($"Checking each game… {states.Count + 1} of {each.Count}");
                var ticket = Ticket();
                tickets.Add(ticket);
                var fresh = false;
                var stale = false;
                var s = unresolved.TryGetValue(g.Id, out var why) ? Unread(g, why, out stale) : Evaluate(g, force, out fresh, out stale, capped: true);
                states.Add(s);
                driverStale.Add(stale);
                detected |= fresh;
                GameState? shown = null;
                lock (_lock)
                    if (!Unlisted(g.Id) && !StaleCopy(g) && _evaluatedAt.GetValueOrDefault(g.Id) < ticket && !(IsUnchecked(s) && _evaluatedAt.ContainsKey(g.Id))
                        && _games.FindIndex(x => x.Game.Id == g.Id) is var i and >= 0)
                    {
                        if (!IsUnchecked(s)) (_evaluatedAt[g.Id], _driverStale[g.Id]) = (ticket, stale);
                        _games[i] = shown = WithVerdict(s);
                    }
                if (shown != null) GameChanged?.Invoke(shown);
            }
        lock (_lock) Publish(started, discovering, Newest(states, tickets, driverStale));
        if (states.Any(s => s.Vac) && VacReadError() is { } unread) Log?.Report($"Steam's {unread}. Every Steam game counts as VAC until it reads.");
        var running = Running();   // notes how running games were launched (the real check only opens processes named like a game)
        var learned = LearnKeysOfRunning(running);
        for (int i = 0; i < states.Count; i++)
            if (!unresolved.ContainsKey(states[i].Game.Id) && (_launched.ContainsKey(Path.GetFileName(states[i].Game.ExePath)) || learned.Contains(states[i].Game.Id)))
            {
                tickets[i] = Ticket();
                states[i] = Evaluate(states[i].Game, false, out _, out var stale, capped: true);
                driverStale[i] = stale;
            }
        lock (_lock)
        {
            Publish(started, discovering, Newest(states, tickets, driverStale));
            states = states.Where(s => !Unlisted(s.Game.Id)).ToList();   // removed or hidden while it was read: nothing below touches it
        }
        foreach (var s in states) GameChanged?.Invoke(s);
        BeforeAfterCatchUp(states);
        if (CheckPlans)
            foreach (var s in states)
                if (!Hdd(s.Game) && s.Engine != null && s.Status != GameStatus.Unsupported && Store.LoadGame(s.Game.Id) is var r && NeedsPlanCheck(s.Game, r, s.Engine)) CheckPlan(s.Game.Id, OlderPlanner(s.Game, r, s.Engine));
        if (detected) ReleaseMemory();   // engine detection mounts the game's archives
        if (ManageRecorders)
        {
            ReconcileRecorders();
            if (!failed) CleanUninstalled(found);
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
    /// build without a look through its folders (Steam and its non-Steam games, the Xbox app, the games added by hand) listed
    /// again, and a game read again only when it isn't the one listed (another build, exe or folder).</summary>
    IReadOnlyList<GameState> Listed(long started, long discovering, List<GameState> kept, List<Game> before)
    {
        var (found, unresolved, failed) = Discover(before, s => s.Store is Core.Store.Steam or Core.Store.Xbox or Core.Store.Manual || s is SteamShortcutSource or HeroicSource or LutrisSource or FaugusSource ? s.Discover()
            : before.Where(g => g.Store == s.Store && !SteamShortcuts.Is(g.Id)).ToList());
        if (failed) lock (_lock) _discoveryFailed = Math.Max(_discoveryFailed, started);
        var was = kept.DistinctBy(s => s.Game.Id).ToDictionary(s => s.Game.Id);
        var gpu = Snapshot();
        var (states, tickets, driverStale) = (new List<GameState>(), new List<long>(), new List<bool>());
        foreach (var g in Visible(found).DistinctBy(g => g.Id).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            tickets.Add(Ticket());
            if (unresolved.TryGetValue(g.Id, out var why))
            {
                states.Add(Unread(g, why, out var unread));
                driverStale.Add(unread);
            }
            else if (was.GetValueOrDefault(g.Id) is { } listed && listed.Game == g && !IsUnchecked(listed))
            {
                var rec = Store.LoadGame(g.Id);
                states.Add(listed with { Playing = IsPlaying(g.Id), NewShadersOverride = rec.NewShaders, RtSkipped = !RayTracing(rec), RayTracingOverride = rec.RayTracing, TraversalOverride = rec.Traversal, LeftOutOfAddAll = rec.LeftOutOfAddAll, NoSharedHashes = rec.NoSharedHashes, CacheCapGB = rec.CacheCapGB, PlanCacheCapGB = rec.PlanCacheCapGB, ExeChosen = rec.ChosenExe != null });
                driverStale.Add(rec.WarmedAt != null && !CurrentDriver(gpu, rec.WarmedDriverId, rec.WarmedDriverVersion));
            }
            else
            {
                states.Add(Evaluate(g, false, out _, out var stale, capped: true));
                driverStale.Add(stale);
            }
        }
        lock (_lock)
        {
            Publish(started, discovering, Newest(states, tickets, driverStale));
            states = states.Where(s => !Unlisted(s.Game.Id)).ToList();   // removed or hidden while it was read: nothing below touches it
        }
        foreach (var s in states) GameChanged?.Invoke(s);
        BeforeAfterCatchUp(states);
        if (ManageRecorders)
        {
            // the pair's recorder follows its stage, also when it ended while the app was closed
            foreach (var s in states.Where(s => Frames11(s.Engine, Vendor.Caps)))
                try { ReconcileRecorders(s.Game.Id); }
                catch (Exception e) { Log?.Report($"{s.Game.Name}: reconciling the recorder failed: {e.Message}"); }
            if (!failed) CleanUninstalled(found);
            SweepVulkan();
        }
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
    /// <paramref name="unresolved"/>: why it couldn't be checked, by game id; Failed: a source's discovery threw).
    /// An install folder listed by an earlier source isn't listed again by a later one (an EA game bought on Steam has the
    /// EA installer's files too): source order decides whose id, and so whose saved state, the game keeps. A game the
    /// user added yields to a store's whose install holds its exe (the store's copy installed since).</summary>
    /// <param name="fresh">the user's refresh: every Unreal game's Shipping exe is looked for again (<see cref="ShippingPick"/>)</param>
    (List<Game> Found, Dictionary<string, Exception> Unresolved, bool Failed) Discover(List<Game> before, Func<IGameSource, IReadOnlyList<Game>> list, bool fresh = false)
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
        var failed = false;
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var known = before.DistinctBy(g => g.Id).ToDictionary(g => g.Id);
        foreach (var source in _sources)
            if (source is SteamSource steam) (steam.Known, steam.Log) = (known, Log);
            else if (source is XboxSource xbox) xbox.Known = known;
            else if (source is ManualSource manual) manual.Known = known;
            else if (source is SteamShortcutSource shortcuts) shortcuts.Log = Log;
            else if (source is LutrisSource lutris) lutris.Log = Log;
            else if (source is FaugusSource faugus) faugus.Log = Log;
        foreach (var source in _sources)
            try
            {
                Progress($"Scanning {SourceName(source)}… {found.Count} games found");
                var named = list(source);
                foreach (var g in named)   // the folders of both the exe the user picked and the one it runs; a folder's game is only left out
                    if (g.Store == Core.Store.Manual && !(source is ManualSource m && m.InFolder(g.Id)) && found.FirstOrDefault(f => GameFiles.Inside(f.InstallDir, g.ExePath)) is { } owner)
                        foreach (var at in new[] { g, Shipping(g) }.Distinct()) AdoptManual(at, owner);
                var listed = named.Select(Shipping).ToList();
                // EA's entry for an EA game bought on Steam names the app's folder, Steam's games of it their own folders
                static IEnumerable<string> Claims(Game g) => new[] { g.InstallDir, Collections.AppDir(g) }.Select(GameFiles.DirKey);
                var games = listed.Where(g => !Claims(g).Any(claimed.Contains)
                    && !(g.Store == Core.Store.Manual && found.Any(f => GameFiles.Inside(f.InstallDir, g.ExePath)))).ToList();
                found.AddRange(games);
                claimed.UnionWith(games.SelectMany(Claims));
            }
            catch (Exception e)
            {
                failed = true;
                Log?.Report($"{source.Store}: discovery failed: {e.Message}");
            }
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
                return Store.LoadGame(g.Id) is var r && r.ChosenExe is { } rel && g.Store != Core.Store.Manual ? g with { ExePath = Path.GetFullPath(Path.Combine(g.InstallDir, rel)) }
                    : g.Store != Core.Store.Manual && GameExes.Current.Exe(g, () => null) is { } listed && Path.GetFullPath(Path.Combine(g.InstallDir, listed)) is var at
                        && GameFiles.InsideNoLinks(g.InstallDir, at) ? g with { ExePath = at }
                    : r is { RunsExe: { } exe } && !LeavesUnreal(g.ExePath, exe, r.RunsExeProven) ? g with { ExePath = exe } : g;
            }
        }).ToList();
        return (found, unresolved, failed);
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
    /// install, and the EasyAntiCheat settings beside the named exe (<see cref="GameFiles.EacExecutable"/>). Null when the root
    /// can't be listed or holds a write time FileInfo can't read (the scan says why, later).</summary>
    static string? RootStamp(Game g)
    {
        try { return Stamp(g.Version, GameFiles.DirKey(g.InstallDir)) is { } root ? root + GameFiles.EacSettingsStamp(g.ExePath) : null; }
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
    string ImagesFile => Path.Combine(Store.DataDir, "middleware.json");
    string ProbesFile => Path.Combine(Store.DataDir, "reshade.json");
    string ConfirmedFile => Path.Combine(Store.DataDir, "confirmed-engines.json");
    string PrecompilesFile => Path.Combine(Store.DataDir, "precompiles-at-boot.json");
    string VerdictsFile => Path.Combine(Store.DataDir, GameVerdicts.FileName);
    string MaximumFile => Path.Combine(Store.DataDir, MaximumMode.FileName);
    string ExesFile => Path.Combine(Store.DataDir, GameExes.FileName);
    string RulesPath => Path.Combine(Store.DataDir, "plan-rules.json");
    string NewsPath => Path.Combine(Store.DataDir, "news.json");
    ContentTrust RulesTrust => new(Store, ContentKeys);
    /// <summary>The keys signed content files must verify with: <see cref="ContentTrust.RulesKeys"/> (tests pin their own).</summary>
    public IReadOnlyDictionary<string, string> ContentKeys { get; set; } = ContentTrust.RulesKeys;
    /// <summary>The keys compat.json must verify with: <see cref="ContentTrust.CompatKeys"/> (tests pin their own). None: it
    /// isn't fetched. Setting it reads the kept copy again with them.</summary>
    public IReadOnlyDictionary<string, string> CompatKeys
    {
        get;
        set
        {
            field = value;
            _compat = CompatFeed.Cached(CompatTrust, CompatFile);
        }
    } = ContentTrust.CompatKeys;
    string CompatFile => Path.Combine(Store.DataDir, CompatFeed.FileName);
    ContentTrust CompatTrust => new(Store, CompatKeys, CompatFeed.Name, CompatFeed.MaxBytes);
    CompatFeed? _compat;   // the verified public aggregate; null = none

    /// <summary>The signed news.json in use, fetched with the lists; <see cref="NewsChanged"/> after each check and change of channel.</summary>
    public News News { get; private set; }
    public event Action? NewsChanged;
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
                if (await BootPrecompiles.FetchAsync(PrecompilesFile, routes) is { } precompiles) UseBootPrecompiles(precompiles);
                (string? Token, IReadOnlyCollection<string>? Ent)? access = null;
                try { access = ChannelAccess == null ? (null, null) : await ChannelAccess(); }
                catch (Exception) { }   // not known: stable's still come
                (string[] Rules, string[] News)? read;
                lock (_channelLock)
                {
                    if (access is { } known) _channelEnt = (true, known.Ent);
                    read = ChannelsRead();
                }
                // the news' channels; none of them verifying: the app's own list
                UseVerdicts(await RulesTrust.FetchAsync(GameVerdicts.Name, GameVerdicts.TryParse, VerdictsFile, read?.News, access?.Token, routes) ?? GameVerdicts.Embedded);
                UseMaximum(await RulesTrust.FetchAsync(MaximumMode.Name, MaximumMode.TryParse, MaximumFile, read?.News, access?.Token, routes) ?? MaximumMode.Embedded);
                UseExes(await RulesTrust.FetchAsync(GameExes.Name, GameExes.TryParse, ExesFile, read?.News, access?.Token, routes) ?? GameExes.Embedded);
                if (CompatKeys.Count > 0) UseCompat(await CompatFeed.FetchAsync(CompatTrust, CompatFile, routes));
                var rules = await RulesFile.FetchAsync(RulesPath, RulesTrust, routes, access?.Token, read?.Rules);
                var news = await News.FetchAsync(NewsPath, RulesTrust, routes, access?.Token, read?.News);
                lock (_channelLock)
                    if (_channelEnt.Known) ApplyChannels(true);   // the entitlements may have changed during the fetch
                    else
                    {
                        UseRules(rules);
                        UseNews(news);
                    }
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

    void UseBootPrecompiles(BootPrecompiles list)
    {
        var was = BootPrecompiles.Current;
        BootPrecompiles.Current = list;
        lock (_refreshLock)
            foreach (var s in Games)
                if (list.Has(s.Game) != was.Has(s.Game)) Refresh(s.Game);
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

    void UseMaximum(MaximumMode list)
    {
        var was = MaximumMode.Current;
        MaximumMode.Current = list;
        var vendor = PackGpu(Vendor.Vendor);
        List<GameState> changed;
        lock (_refreshLock)
        {
            changed = Games.Where(s => list.For(s.Game, vendor) != was.For(s.Game, vendor)).ToList();
            foreach (var s in changed) Refresh(s.Game);
        }
        if (changed.Count > 0) KeepList();   // a start without a scan restores the kept states
    }

    // a game whose listed exe changed goes the way of a user's own exe change; one compiling, once its compile ends
    void UseExes(GameExes list)
    {
        var was = GameExes.Current;
        GameExes.Current = list;
        foreach (var s in Games.Where(s => list.Exe(s.Game, () => ScanEngine(s.Game.Id)) != was.Exe(s.Game, () => ScanEngine(s.Game.Id))))
        {
            lock (_lock)
                if (_current == s.Game.Id)
                {
                    _exesWaiting[s.Game.Id] = 0;
                    continue;
                }
            ListedExeChanged(s.Game);
        }
    }

    readonly ConcurrentDictionary<string, byte> _exesWaiting = new();

    void ListedExeChanged(Game g)
    {
        try { ExeChanged(g, "from the exe list"); }
        catch (Exception e) when (e is not OperationCanceledException) { Log?.Report($"{g.Name}: the exe list's change waits for the next scan: {e.Message}"); }
    }

    void UseCompat(CompatFeed? feed)
    {
        _compat = feed;
        List<GameState> changed;
        lock (_refreshLock)
        {
            changed = Games.Where(s => SimilarOf(s.Game) != s.Similar).ToList();
            foreach (var s in changed) Refresh(s.Game);
        }
        if (changed.Count > 0) KeepList();   // a start without a scan restores the kept states
    }

    SimilarSystems? SimilarOf(Game g) => _compat?.For(g, PackGpu(Vendor.Vendor), GpuModel());

    static string? NoStutterOf(Game g, EngineInfo? engine, KnownStutter? known) => known == null ? GameVerdicts.Current.NoStutter(g, engine) : null;

    readonly object _channelLock = new();
    (bool Known, IReadOnlyCollection<string>? Ent) _channelEnt;   // under _channelLock, as the channels last applied
    string[]? _rulesRead, _newsRead;

    /// <summary>The account's entitlements are known or changed (<paramref name="ent"/> null: signed out): the kept planning
    /// rules and news of the channels no longer read are dropped, and the newest of the others is used. The update channel's
    /// change does the same for the news.</summary>
    public void UseChannels(IReadOnlyCollection<string>? ent)
    {
        lock (_channelLock)
        {
            _channelEnt = (true, ent);
            ApplyChannels(false);
        }
    }

    /// <summary>A Flatpak or a package (null: neither) reads its news and verdicts on its build's channel, never on one an
    /// AppImage run chose, which stays in the settings for the AppImage. Tests replace both.</summary>
    internal Installation? InstalledAs { get; set; } = Installation.ThisElsewhere;
    internal string BuildChannel { get; set; } = AppVersion.Current.Channel;

    /// <summary>The channels whose rules and news are read; null until the entitlements are known. Under _channelLock.</summary>
    internal (string[] Rules, string[] News)? ChannelsRead() => _channelEnt is (true, var ent)
        ? (RulesFile.Channels(ent), News.Channels(UpdateChannels.Effective(InstalledAs is { UpdatedElsewhere: true } ? null : Settings.UpdateChannel, BuildChannel, ent)))
        : null;

    /// <summary>Under _channelLock: the kept copies of the channels read, when they changed or <paramref name="always"/>.</summary>
    void ApplyChannels(bool always)
    {
        if (ChannelsRead() is not { } read) return;
        if (always || _rulesRead == null || !read.Rules.SequenceEqual(_rulesRead)) UseRules(RulesFile.Cached(RulesPath, RulesTrust, _rulesRead = read.Rules));
        if (always || _newsRead == null || !read.News.SequenceEqual(_newsRead))
        {
            UseNews(News.Cached(NewsPath, RulesTrust, _newsRead = read.News));
            UseVerdicts(RulesTrust.Cached(GameVerdicts.Name, GameVerdicts.TryParse, VerdictsFile, read.News) ?? GameVerdicts.Embedded);   // the news' channels
            UseMaximum(RulesTrust.Cached(MaximumMode.Name, MaximumMode.TryParse, MaximumFile, read.News) ?? MaximumMode.Embedded);
            UseExes(RulesTrust.Cached(GameExes.Name, GameExes.TryParse, ExesFile, read.News) ?? GameExes.Embedded);
        }
    }

    void UseNews(News? news)
    {
        News = news ?? News.Empty;
        NewsChanged?.Invoke();
    }

    /// <summary>The server's rules in use over the embedded ones (null: the embedded ones alone): the games whose matching
    /// rules changed are evaluated again (readiness, the plan to rebuild) and, like after a planner update, a warmed one gets
    /// a plan rebuild when idle.</summary>
    void UseRules(RulesFile? server)
    {
        var before = Games.Where(s => s.Engine != null).ToDictionary(s => s.Game.Id, s => RulesFile.Current.Hash(s.Engine!));
        RulesFile.Current = server == null ? RulesFile.Embedded : RulesFile.WithServer(server);
        lock (_refreshLock)
            foreach (var s in Games)
                if (before.TryGetValue(s.Game.Id, out var h) && h != RulesFile.Current.Hash(s.Engine!))
                {
                    Refresh(s.Game);
                    if (CheckPlans && !Hdd(s.Game) && s.Status != GameStatus.Unsupported && NeedsPlanCheck(s.Game, Store.LoadGame(s.Game.Id), s.Engine)) CheckPlan(s.Game.Id);
                }
    }

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
    /// <param name="capped">a scan's or a page's read: at most <see cref="EvaluateCap"/> (<see cref="Capped"/>). Only from a thread
    /// that holds no lock the evaluation takes, since it runs on another thread.</param>
    GameState Evaluate(Game g, bool force, out bool fresh, out bool driverStale, CancellationToken ct = default, bool capped = false)
    {
        if (Hdd(g) && !_asked.ContainsKey(g.Id) && !Checked(g))
        {
            (fresh, driverStale) = (false, false);
            return Unchecked(g, NotCheckedReason);
        }
        return capped ? Capped(g, force, out fresh, out driverStale) : Read(g, force, out fresh, out driverStale, ct, default);
    }

    /// <param name="abandoned">cancelled once its <see cref="Capped"/> wait gave up: nothing of the evaluation is written after</param>
    GameState Read(Game g, bool force, out bool fresh, out bool driverStale, CancellationToken ct, CancellationToken abandoned)
    {
        try
        {
            var s = EvaluateGame(g, force, out fresh, out driverStale, ct, abandoned);
            abandoned.ThrowIfCancellationRequested();
            _unread.TryRemove(g.Id, out _);
            return s;
        }
        // one game's unreadable files (a file time past the year 9999 throws) never fail the scan of the others
        catch (Exception e) when (e is not OperationCanceledException)
        {
            abandoned.ThrowIfCancellationRequested();
            fresh = false;
            return Unread(g, e, out driverStale);
        }
    }

    public const string TimedOutReason = "couldn't be read in time, retried at the next scan";
    static readonly TimeSpan Cap = TimeSpan.FromMinutes(5), HddCap = TimeSpan.FromMinutes(20);

    // game id -> its evaluations that outlived the cap (a scan's and a page's may overlap): never waited for again, none
    // started while one still runs
    readonly Dictionary<string, Thread[]> _stuck = [];
    // game id -> how long its last evaluation past the cap took in the end: a slow but healthy read gets the time next scan
    readonly ConcurrentDictionary<string, TimeSpan> _lateTook = new();

    /// <summary>Up to 4x the cap after a read that ended late, so a slow drive's game is read in the end.</summary>
    TimeSpan EvaluateCap(Game g)
    {
        var cap = Hdd(g) ? HddCap : Cap;
        return _lateTook.TryGetValue(g.Id, out var took) ? TimeSpan.FromTicks(Math.Clamp(2 * took.Ticks, cap.Ticks, 4 * cap.Ticks)) : cap;
    }

    /// <summary>The evaluation on a thread of its own: one still running after <see cref="EvaluateCap"/> (a read that never
    /// returns) is left, the game shows <see cref="TimedOutReason"/> and the caller goes on. Its result is dropped and its
    /// writes stop (<see cref="Read"/>'s abandoned token), all but the recorder's removal from a game it finds anti-cheat or
    /// a blocking mod in. While it runs the game isn't evaluated again.</summary>
    GameState Capped(Game g, bool force, out bool fresh, out bool driverStale)
    {
        fresh = false;
        var cap = EvaluateCap(g);
        var abandon = new CancellationTokenSource();
        var low = _lowIo;
        GameState? s = null;
        (bool Fresh, bool Stale) outs = default;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? error = null;
        var clock = Stopwatch.StartNew();
        var thread = new Thread(() =>
        {
            try
            {
                GameState Run() => Read(g, force, out outs.Fresh, out outs.Stale, abandon.Token, abandon.Token);
                s = low ? LowIo(Run) : Run();
            }
            catch (Exception e) { error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
            if (!abandon.IsCancellationRequested) return;
            _lateTook[g.Id] = clock.Elapsed;
            Log?.Report($"{g.Name} ({g.Id}): its check ended after {clock.Elapsed.TotalMinutes:0.#} minutes, past the limit: dropped, read again at the next scan");
        }) { IsBackground = true, Name = "evaluate" };
        bool busy;
        lock (_stuck)   // the check and the start against a timed-out thread's entry: none starts once one is stuck
            if (!(busy = _stuck.TryGetValue(g.Id, out var stuck) && stuck.Any(t => t.IsAlive))) thread.Start();
        if (busy)
        {
            Log?.Report($"{g.Name} ({g.Id}): its last check still hasn't ended, not read this time");
            return Unread(g, TimedOutReason, out driverStale);
        }
        if (thread.Join(cap))
        {
            error?.Throw();
            (fresh, driverStale) = outs;
            return s!;
        }
        lock (_stuck) _stuck[g.Id] = [.. _stuck.GetValueOrDefault(g.Id, []).Where(t => t.IsAlive), thread];
        abandon.Cancel();
        Log?.Report($"{g.Name} ({g.Id}): its check didn't end within {cap.TotalMinutes:0} minutes, skipped until the next scan");
        return Unread(g, TimedOutReason, out driverStale);
    }

    /// <summary>Game ids whose last evaluation failed: <see cref="Reconcile"/> leaves their recorder as it is until one
    /// succeeds, since an error may pass (a locked file) and the state it would act on is the last one, not the game's now.</summary>
    readonly ConcurrentDictionary<string, bool> _unread = new();

    /// <summary>A game whose evaluation failed: its last state with the error shown, else a bare unsupported one.</summary>
    GameState Unread(Game g, Exception e, out bool driverStale)
    {
        var codec = Codecs.Reason(e);
        Log?.Report($"{g.Name}: couldn't be read: {(codec == null ? e.Message : e.ToString())}");
        return Unread(g, codec ?? $"couldn't read its files: {e.Message}", out driverStale);
    }

    GameState Unread(Game g, string reason, out bool driverStale)
    {
        _unread[g.Id] = true;
        GameState? last;
        lock (_lock) (last, driverStale) = (_games.FirstOrDefault(s => s.Game.Id == g.Id), _driverStale.GetValueOrDefault(g.Id));
        return last != null ? last with { Game = g, Status = GameStatus.Unsupported, StatusReason = reason, NotPlanned = false }   // an error may pass: "Not supported yet"
            : new GameState(g, null, AntiCheat.None, GameStatus.Unsupported, reason, null, null, null, null, null, null, null, false, null) { RecorderSkip = SkipUnsupported };
    }

    /// <summary>A game listed before a scan reached it.</summary>
    public const string CheckingReason = "checking its files";
    /// <summary>HDD mode: a game never checked, read only when its page opens or it compiles.</summary>
    public const string NotCheckedReason = "not checked yet: open it to check";

    /// <summary>A state no evaluation read (<see cref="Unchecked"/>): Reconcile and the code near a running game never act on it.</summary>
    public static bool IsUnchecked(GameState s) => s.StatusReason is CheckingReason or NotCheckedReason;

    /// <summary>The game was checked once (its scan cache entry), or a recorder of ours is on its record: HDD mode reads it as
    /// any game, so a recorder's game always gets its anti-cheat check.</summary>
    bool Checked(Game g)
    {
        lock (_scanLock)
            if ((_scan ??= Store.LoadScan()).ContainsKey(g.Id)) return true;
        return RecorderOnRecord(Store.LoadGame(g.Id));
    }

    static bool RecorderOnRecord(GameRecord rec) => rec.RecorderFiles.Count > 0 || rec.RecorderChained != null || rec.RecorderExe != null || rec.RecorderMoveFrom != null;

    /// <summary>A game's state before anything of it was read: its anti-cheat isn't known, so nothing goes near it.</summary>
    GameState Unchecked(Game g, string reason) =>
        new GameState(g, null, AntiCheat.None, GameStatus.Unsupported, reason, null, null, null, null, null, null, null, false, null,
            KnownStutter: StutterList.Current.Find(g), Playing: IsPlaying(g.Id)) { RecorderSkip = SkipUnsupported };

    /// <summary>The game page opened: a game no evaluation read yet is read now, ahead of the scan. In HDD mode every game is
    /// evaluated again, so a failed check is retried and a plan check it needs is queued now (none is queued on its own there).</summary>
    public void CheckGame(string gameId)
    {
        if (Games.FirstOrDefault(s => s.Game.Id == gameId) is not { } s) return;
        if (IsUnchecked(s) || Hdd(s.Game)) RefreshAsked(s.Game);
    }

    /// <summary>HDD mode applies to the game: <see cref="Settings.HddMode"/>, or its drive is one of <see cref="Settings.HddDrives"/>.</summary>
    bool Hdd(Game g) => Settings.HddMode || Settings.HddDrives is { Count: > 0 } drives && GameFiles.DriveOf(g.InstallDir) is { } d
        && drives.Contains(d, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>The drives the listed games are on, and those <see cref="Settings.HddDrives"/> names: Settings' per-drive HDD mode.</summary>
    public static IReadOnlyList<string> HddDriveChoices(IEnumerable<GameState> games, Settings s)
    {
        var roots = GameFiles.DriveRoots();
        return [.. games.Select(g => GameFiles.DriveOf(g.Game.InstallDir, roots)).OfType<string>().Concat(s.HddDrives ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary><paramref name="s"/> with HDD mode on or off for <paramref name="drive"/>.</summary>
    public static Settings WithHddDrive(Settings s, string drive, bool on) =>
        s with { HddDrives = [.. (s.HddDrives ?? []).Where(d => !d.Equals(drive, StringComparison.OrdinalIgnoreCase)).Concat(on ? [drive] : [])] };

    readonly ConcurrentDictionary<string, bool> _asked = new();   // games the user asked for: read in HDD mode too
    readonly ConcurrentDictionary<(string Game, string Why), bool> _agsUnfit = new();   // why the AGS wrapper doesn't fit, logged once each

    void RefreshAsked(Game g)
    {
        _asked[g.Id] = true;
        try { Refresh(g, prompt: true, capped: true); }
        finally { _asked.TryRemove(g.Id, out _); }
    }

    GameState EvaluateGame(Game g, bool force, out bool fresh, out bool driverStale, CancellationToken ct, CancellationToken abandoned)
    {
        var rec = Store.LoadGame(g.Id);
        var gpuNow = Snapshot();
        var exeDir = Path.GetDirectoryName(g.ExePath)!;
        // an earlier build's mark with no recorder folder named (MigrateInis): the game's exe folder's
        if (rec.IniIsUsers && (rec.RecorderExe ?? rec.RecorderMoveFrom ?? rec.OfflineSession?.Exe) == null && Directory.Exists(exeDir))
        {
            abandoned.ThrowIfCancellationRequested();
            SetIni(Store, exeDir, e => e ?? new IniOwner(exeDir, Users: true));   // unless a newer build's entry says whose it is since
            abandoned.ThrowIfCancellationRequested();
            rec.IniIsUsers = false;
            Store.SaveGame(g.Id, rec);
        }
        var cap = CarefulThreads(OwnDriver);
        // a launch that started before the warm ended judged an earlier warm (one played while this one compiled)
        var earlier = rec.FirstLaunch is { } fl && fl.At < rec.WarmedAt;
        if (earlier) rec.FirstLaunch = null;
        var nvidia = OwnDriver == GpuVendor.Nvidia;
        // the verdict stands until a later launch shows the compile used: each one is judged, one per evaluation
        var recheck = nvidia && rec.FirstLaunch != null && IsUnreached(rec);
        var judge = (cap != null || nvidia) && rec.FirstLaunch == null ? rec.WarmedAt : recheck ? rec.UnreachedRecheckedAt is { } seen && seen > rec.FirstLaunch!.At ? seen : rec.FirstLaunch!.At : null;
        // the RayQuery floor is measured on NVIDIA only
        var rayQuery = nvidia ? SessionLog.ReadRayQueryKeys(RayQueryKeysPath(g.Id)) : null;
        var sessionDir = SessionDir(g);
        var frames = Frames(sessionDir, Path.GetFileName(g.ExePath), rayQuery, rec.LastPlay);
        var (session, marker, first) = SessionLog.Read(Path.Combine(sessionDir, "scskiller_creates.csv"), out var framesMatch, out var agsSeen, Path.GetFileName(g.ExePath), judge, MinJudgedCreates, rayQuery, rec.LastPlay, frames);
        if (session != null && !framesMatch) frames = null;   // another launch's frames: none shown next to this one's counts
        if (frames != null)
            try
            {
                var run = PairCapMs(rec, frames) is { } playCap
                    ? FrameLog.Read(Path.Combine(sessionDir, FrameLog.FileName), Path.Combine(sessionDir, "scskiller_creates.csv"), Path.GetFileName(g.ExePath), rayQuery, rec.LastPlay, playCap)
                    : frames;
                if (run?.LaunchUnixMs == frames.LaunchUnixMs)
                {
                    var snapshot = Snapshots.Of(run, rec.LastPlay, gpuNow.Id, gpuNow.Gpu.DriverVersion, g.Version ?? ExeStamp(g), rec.WarmedAt);
                    abandoned.ThrowIfCancellationRequested();
                    Snapshots.Write(Store, g.Id, snapshot);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { Log?.Report($"{g.Name}: couldn't save the session snapshot: {e.Message}"); }
        abandoned.ThrowIfCancellationRequested();
        if (frames != null) NoteFrames(g, frames);
        if (first != null && !nvidia && AmdAlwaysOn11(rec))
        {
            if (_alwaysOnNoted.TryAdd((g.Id, first.At), true)) Log?.Report($"{g.Name}: its launch isn't judged: AMD's shader cache is set to Always on");
            first = null;
        }
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
            imported = ImportRecording(g, rec, ct: ct) | ImportVulkan(g, rec, ct);
            upkept = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        // the recording's upkeep never keeps the evaluation (and its anti-cheat check) from running: retried next time
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { Log?.Report($"{g.Name}: the recording's upkeep failed: {e.Message}"); }
        if (first != null && nvidia && !upkept) first = null;   // judged by a later evaluation, with the launch's records
        else if (first != null && recheck)
        {
            var w = WarmedCreates(g, rec, first, Path.Combine(sessionDir, "scskiller_creates.csv"), rayQuery, gpuNow);
            if (w != null) rec.UnreachedRecheckedAt = first.At;   // one that can't be judged now (a key file held, more to compile) is judged again
            if (w is { } later && later.N >= MinJudgedCreates && later.MedianMs < UnreachedMedianMs)
            {
                rec.UnreachedLifted = rec.UnreachedWarm;   // not a cleared UnreachedWarm: a save that lands late can't lift a newer verdict
                Log?.Report($"{g.Name}: a later launch used the compile ({later.N:N0} warmed pipelines it created first, median {later.MedianMs:0} ms)");
            }
        }
        else if (first != null && nvidia)
        {
            rec.FirstLaunch = first;
            var w = WarmedCreates(g, rec, first, Path.Combine(sessionDir, "scskiller_creates.csv"), rayQuery, gpuNow);
            // how much of the warm the launch missed, also while more waits to compile: what decides a full replay (FullReplayReason)
            if ((w ?? WarmedCreates(g, rec, first, Path.Combine(sessionDir, "scskiller_creates.csv"), rayQuery, gpuNow, verdict: false)) is { Seen: >= (int)MinJudgedCreates } share)
                rec.FirstLaunch = first with { Warmed = share.Seen, WarmedCold = share.Cold };
            var missed = w is { } judged && Unreached((judged.N, judged.MedianMs));
            // a guess is judged only by a launch of its own build: another build's compiles plain first (AftermathRetries)
            if (rec is { WarmedAftermath: true, AftermathPlainHits: { } plain } && SameKey(rec.AftermathRetry, AftermathRetryKey(g.Version ?? ExeStamp(g), WarmExeName(g, rec))))
            {
                EngineInfo? scanned;
                lock (_scanLock) scanned = (_scan ??= Store.LoadScan()).GetValueOrDefault(g.Id)?.Engine;
                // a key the launch reset (Trimmed) loses an inferred try too: its entries went with it
                var reset = NvAftermath.Inferred(scanned, NvAftermath.Facts(Path.Combine(sessionDir, "scskiller.log"))) && Trimmed(g.Id, rec) != null;
                (rec.AftermathLost, rec.AftermathPlainHits) = (first.Hits <= plain || missed || reset, null);
            }
            if (missed)
            {
                (rec.UnreachedWarm, rec.UnreachedRecheckedAt) = (rec.WarmedAt, null);
                Log?.Report($"{g.Name}: {UnreachedReason} ({w!.Value.N:N0} warmed pipelines at its first launch, median {w.Value.MedianMs:0} ms)");
            }
        }
        try
        {
            var changed = imported | MergeLaunched(g, rec, marker) | MergeAgsSeen(g, rec, agsSeen) | AdoptDriverId(rec, gpuNow) | first != null | earlier | rec.KeysIndexHash != keysOf | rec.KeysPending != keysPending | rec.RtUnseen != rtUnseenWas | rec.RecordedLong != longWas | PruneGoneKeys(rec);
            abandoned.ThrowIfCancellationRequested();
            if (changed) Store.SaveGame(g.Id, rec);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{g.Name}: couldn't save its record: {e.Message}"); }
        var (_, engine, antiCheat, check, _, streamlineFirst, _, _, apiDetected, _, _) = Evaluated(g, rec, force, out fresh, abandoned);
        try { if (SwitchApi(g, engine, held: () => { lock (_lock) return _current == g.Id; }, seen: rec)) rec = Store.LoadGame(g.Id); }   // its compile switches when it starts
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log?.Report($"{g.Name}: keeping its compile for the other graphics API failed, tried again next time: {e.Message}");
            rec = Store.LoadGame(g.Id);   // as the switch left it
        }
        long? psos = PlanItems(rec.Plan?.Stats);
        long? recorded = rec.Plan?.Stats.Recorded;
        TimeSpan? fast = psos is { } n ? TimeSpan.FromSeconds(n / (RateOn(rec, gpuNow.Id) ?? WarmRate(gpuNow.Id))) : null;
        TimeSpan? careful = cap != null && psos is { } np && recorded is { } nr ? TimeSpan.FromSeconds(nr / DefaultCarefulPsoPerSecond + (np - nr) / (RateOn(rec, gpuNow.Id) ?? WarmRate(gpuNow.Id))) : null;
        driverStale = rec.WarmedAt != null && !CurrentDriver(gpuNow, rec.WarmedDriverId, rec.WarmedDriverVersion);
        var counted = rec.Pending;
        var pending = PendingOf(g, rec, engine);   // after the import and the scan's engine: derived from what they and any download left
        abandoned.ThrowIfCancellationRequested();
        if (rec.Pending != counted)
            try { Store.SaveGame(g.Id, rec); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{g.Name}: couldn't save its record: {e.Message}"); }
        var vac = VacCheck(g);
        if (vac && (rec.RecorderFiles.Count > 0 || rec.RecorderChained != null || rec.VulkanLayer != null || ProxyOnDisk(g)))
        {
            TakeOutNow(g, "VAC found");   // the command line doesn't reconcile
            rec = Store.LoadGame(g.Id);   // what the removal left
        }
        // anti-cheat seen: off the AGS app-name path for good (a risk session is let through separately, AgsEligible)
        if ((antiCheat != AntiCheat.None && !_unknown.ContainsKey(g.Id) || vac) && !rec.AgsIneligible && OwnDriver == GpuVendor.Amd)   // a check that didn't finish isn't a sighting
        {
            abandoned.ThrowIfCancellationRequested();
            rec.AgsIneligible = true;
            Log?.Report($"{g.Name}: {(antiCheat != AntiCheat.None ? "anti-cheat" : "VAC")} found; the AGS app-name hook is off for it");
            try { Store.SaveGame(g.Id, rec); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{g.Name}: couldn't save its record: {e.Message}"); }
        }
        if (engine != null && check.Reason.Contains(Planner.Untested) && RootSig.Verified(engine))
            check = check with { Reason = check.Reason.Replace(Planner.Untested, Planner.NoRecording) };
        else if (antiCheat != AntiCheat.None || vac) check = check with { Reason = check.Reason.Replace(Planner.Untested, Planner.UntestedNote) };
        var manifest = rec.NoSharedHashes ? null : LocalManifest();
        var entry = manifest != null ? DbEntry(manifest, g, rec, FullEntries) : null;
        bool? inDb = manifest != null && !CarvedReader.RecordedOnly(engine) ? entry != null : null;   // hash-only entries can't plan it
        var entitled = DbEntitled();
        // a recording newer than the plan may have the ray tracing (inline, or state objects): its plan check tells
        var rtToPlan = RtPlanCheck(rec, engine);
        var rtSkipped = !RayTracing(rec);   // nothing asked for it until the plan check or the next compile leaves it out
        var rtUnseen = NeedsRtRecording(rec.Plan?.Stats) && !rtToPlan && rec.RtUnseen && !rtSkipped;
        var rt = NeedsRtRecording(rec.Plan?.Stats) && !rtToPlan && !rec.RtUnseen && !rtSkipped;
        var unseen = rtUnseen ? RtUnseenNote : RtInlineCovers(rec.Plan?.Stats) && !rtSkipped ? RtInlineNote : null;
        var keyNote = engine is { KeyMissing: true } ? KeyForFullCoverage : null;
        var reshade = ReShade.Detect(g, engine);
        var shaderMod = reshade?.ShaderMod;
        if (reshade is { Blocks: true } && antiCheat == AntiCheat.None)   // an anti-cheat game's recorder is left to its risk session
        {
            TakeOutNow(g, ShaderModReason(reshade), anyCopys: false);   // the command line doesn't reconcile
            rec = Store.LoadGame(g.Id);   // what the removal left
        }
        abandoned.ThrowIfCancellationRequested();
        string? agsUnfit = null;
        var agsHost = OwnDriver == GpuVendor.Amd && antiCheat == AntiCheat.None && GameFiles.AgsMakesTheDevice(g.ExePath, AgsDll, out agsUnfit);
        // AGS 6 was never wrapped: its games record through d3d12.dll as before, only an AGS 5 update is worth a line
        if (agsUnfit != null && !agsUnfit.StartsWith("version 6", StringComparison.Ordinal) && !abandoned.IsCancellationRequested && _agsUnfit.TryAdd((g.Id, agsUnfit), true))
            Log?.Report($"{g.Name}: its AMD AGS library ({agsUnfit}) isn't one the recorder can wrap, so this game isn't recorded on AMD");
        var dllName = RecorderDll(rec, engine, Vendor.Caps, agsHost);
        var dll = Path.Combine(exeDir, dllName);
        var by = Collections.RecordedBy(g.Id);
        bool ours, shared = false;
        try
        {
            ours = IsOurProxy(dll) && !Foreign(exeDir);
            abandoned.ThrowIfCancellationRequested();
            _unreadProxies.TryRemove(g.Id, out _);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)   // held by the game
        {
            abandoned.ThrowIfCancellationRequested();
            _unreadProxies[g.Id] = true;
            ours = (by != null ? Store.LoadGame(by) : rec).RecorderFiles.ContainsKey(dllName) && !Foreign(exeDir);
        }
        if (by != null) (shared, ours) = (ours, false);   // the first game's recorder, on for this game too
        else if (VulkanRoute(g, engine)) ours = VulkanIn(g, rec) && !Foreign(exeDir);   // its recorder is the layer's registration
        // the recorder is never installed next to anti-cheat, nor in a game the user added before they confirm its folder
        var unconfirmed = Unconfirmed(g);
        // the layer route loads before any DirectX: Streamline's d3d12.dll is in the way of the proxy only
        var slFirst = streamlineFirst && OwnDriver == GpuVendor.Nvidia && dllName == Dll12 && !VulkanRoute(g, engine);
        // the preloader loads the recorder first, under Proton too (Wine takes the DLL beside the exe before its own): blocked only without one
        IReadOnlySet<string>? builtin = null;
        if (slFirst && antiCheat == AntiCheat.None && !vac) WineBuiltinFirst(g, ref builtin);
        var (preload, preloadHeld, heldByOptions) = slFirst && !LocalRedirect(g) && antiCheat == AntiCheat.None && !vac ? PreloadName(g, rec, builtin) : (null, null, false);
        var slBlocks = slFirst && preload == null;
        // a risk session can record it: no claim that its anti-cheat blocks the recorder
        var riskOffered = (antiCheat != AntiCheat.None || vac) && OfflineEntry(g, antiCheat, engine) == null && RiskOffered(g, engine, anyAntiCheat: true);
        var noRecording = riskOffered ? RiskNoRecording
            : antiCheat != AntiCheat.None ? $"which {(antiCheat == AntiCheat.Other ? "its anti-cheat" : antiCheat)} blocks"
              + (OfflineEntry(g, antiCheat, engine) != null ? " unless you record an offline session (game page)" : "")
            : vac ? "which VAC blocks" : unconfirmed ? ManualNoRecording : slBlocks ? preloadHeld is { } held ? PreloadHeldNoRecording(held, heldByOptions) : StreamlineNoRecording : null;
        // only the recorder is out of reach: the game files' plan stands as for any game, never Unsupported for it
        var noRecordingStatus = riskOffered || noRecording != null && slBlocks && antiCheat == AntiCheat.None && !vac && !unconfirmed ? GameStatus.NeedsRecording : GameStatus.Unsupported;
        // a session that records DirectX 11 (RiskDll) can record them, else nothing can
        var sessionRecords11 = (antiCheat != AntiCheat.None || vac) && RiskDll(engine, Vendor.Caps) == Dll11
            && (OfflineEntry(g, antiCheat, engine) != null || RiskOffered(g, engine, anyAntiCheat: true));
        if (noRecording != null && !sessionRecords11 && check.Reason.EndsWith(". " + Format.Sentence(Planner.D3D11VsRecord)))
            check = check with { Reason = check.Reason[..^(Planner.D3D11VsRecord.Length + 2)] };
        if (noRecording != null)   // a game that may run on either: DirectX 12's part can't come from a recording either
            check = check with { Reason = check.Reason.Replace("For DirectX 12, " + Planner.Record, "For DirectX 12, needs a recording, " + noRecording) };
        var rejected = Rejected(rec.LastWarmFailed, rec.LastWarmCrashed);
        var partlyCompiled = IsPartlyCompiled(rejected, psos);
        // its rejects explain a first launch that still compiles: not a cache that ignores the warm
        // a warm that will start NVIDIA Aftermath as the game does isn't judged by one that didn't
        var aftermathRetry = nvidia && !rec.WarmedAftermath && AftermathOf(g, rec, engine).Retry;
        // a verdict on one driver's cache says nothing about the next one's: a new driver recompiles as any game does
        var unreached = IsUnreached(rec) && nvidia && !driverStale && !partlyCompiled && !AftermathChanged(g, rec, engine) && !aftermathRetry;
        var trimmed = rec.WarmedAt != null ? Trimmed(g.Id, rec) : null;
        abandoned.ThrowIfCancellationRequested();
        // once per change: a driver that evicts at its cap (AMD) logs nothing itself
        if (trimmed != rec.TrimmedLogged)
        {
            rec.TrimmedLogged = trimmed;
            try
            {
                AppStore.Locked(Path.Combine(Store.GameDir(g.Id), "state.json"), () =>
                {
                    var r = Store.LoadGame(g.Id);
                    r.TrimmedLogged = trimmed;
                    Store.SaveGame(g.Id, r);
                }, TimeSpan.FromSeconds(2));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // logged again at the next evaluation
            if (trimmed != null)
                Log?.Report($"{g.Name}: {(trimmed == TrimmedAllReason ? "its" : "part of its")} compiled shader cache is gone" + (KeptReason(rec) is { } full ? $", {full}" : ""));
        }
        var (status, reason) = check.Readiness switch
        {
            _ when GameVerdicts.Current.Unsupported(g, engine) is { } why => (GameStatus.Unsupported, why.Text),
            _ when BootPrecompiles.Current.Has(g) => (GameStatus.Unsupported, BootPrecompiles.Reason),
            _ when reshade is { Blocks: true } => (GameStatus.Unsupported, ShaderModReason(reshade)),
            Readiness.Unsupported => (GameStatus.Unsupported, check.Reason),
            _ when SourceNotReady(g) is { } why => (GameStatus.Unsupported, why),
            _ when SandboxCompileOff(g) is { } why => (GameStatus.Unsupported, why),
            // the 64-bit warm fills the 64-bit driver's cache, a 32-bit game reads another
            _ when GameFiles.Is32Bit(g.ExePath) => (GameStatus.Unsupported, ThirtyTwoBitReason),
            _ when CantReach(g, rec, gpuNow, engine) => (GameStatus.Unsupported, CantReachReason),
            _ when LauncherClearsNext(g) => (GameStatus.NeedsRecording, LauncherClearsReason),
            Readiness.NeedsRecording when check.Reason == VulkanPlan.StartOnce => (GameStatus.NeedsRecording, VulkanPlan.StartOnce),
            // no plan source at all: the game files, this PC's recording and the community's were each checked
            Readiness.NeedsRecording when OfflineEntry(g, antiCheat, engine) != null && !File.Exists(RecordingPath(g.Id)) && CommunityInUse(g.Id) == null
                => (GameStatus.NeedsRecording, OfflineSessionNote),   // a recording without draws keeps the arm below
            Readiness.NeedsRecording when noRecording != null => (noRecordingStatus, Format.Then($"needs a recording, {noRecording}", DbNote(inDb, entitled))),
            Readiness.NeedsRecording when by != null && Collections.Member(by)!.Value.Game.Name is var byName => (GameStatus.NeedsRecording,
                Format.Then(check.Reason.Replace(Planner.Record, shared ? SharedOn(byName) : RecordOnFirst(byName)), DbNote(inDb, entitled))),
            Readiness.NeedsRecording => (GameStatus.NeedsRecording, Format.Then(check.Reason, DbNote(inDb, entitled))),
            // a current plan with nothing in it: a compile has nothing to compile, only a recording can add to it
            _ when EmptyPlan(g, rec, engine) => engine == null || !RecordsApi(g, engine) ? (GameStatus.Unsupported, NoShadersReason)
                : noRecording != null ? (noRecordingStatus, Format.Then($"needs a recording, {noRecording}", NoShadersReason))
                : (GameStatus.NeedsRecording, Format.Then(NoShadersReason, Planner.Record)),
            // a recording can't help a game the warm doesn't reach
            _ when unreached => (rec.WarmedAt == null ? GameStatus.Ready : GameStatus.Warmed, UnreachedReason),
            _ when aftermathRetry && rec.WarmedAt == null => (GameStatus.Ready, AftermathRetryReason),
            // the plan compiles the rest, but not the game's ray tracing: that needs a recording (compiling stays possible, partial)
            _ when rt && noRecording == null && rec.WarmedAt == null => (GameStatus.NeedsRecording, RtNote(inDb, entitled)),
            _ when rec.WarmedAt == null => (GameStatus.Ready, Format.Then(Format.Then(RtBlocked(rt, noRecording, PartialNote(rec.Plan?.Stats) ?? check.Reason), unseen), keyNote)),
            _ when rec.D3DSCacheDamaged => (GameStatus.Stale, D3DSCacheDamagedReason),
            _ when StaleReason(g, rec, engine, pending, gpuNow, status: true) is { } why => (GameStatus.Stale, why),
            _ when D3DSCachePending(g.Id, rec) && rec.D3DSCacheLinked is { } linked => (GameStatus.Warmed, D3DSCacheLinkedReason + linked),
            _ when D3DSCachePending(g.Id, rec) || MovedD3DSCache(g, rec) != null => (GameStatus.Warmed, D3DSCachePendingReason),
            _ when trimmed != null && KeptReason(rec) is { } kept => (GameStatus.Warmed, kept),
            _ => (GameStatus.Warmed, Format.Then(RtBlocked(rt, noRecording, Format.Then(Format.Then(partlyCompiled ? $"partly compiled for driver {rec.WarmedDriverVersion}: {RefusedNote(rec.LastWarmFailed ?? 0, rec.LastWarmCrashed ?? 0, Tried(rec.LastWarmTried ?? rec.WarmedPlanCount, rec.Plan?.Stats))}"
                    : cap is { } t && PartlyWarmedNote(rec, t, careful) is { } partly
                    ? $"partly warmed for driver {rec.WarmedDriverVersion}: {partly}" : $"warmed for driver {rec.WarmedDriverVersion}",
                PartialNote(rec.Plan?.Stats)), RoomNote(rec))), unseen is null ? keyNote : Format.Then(unseen, keyNote))),
        };
        if (status is GameStatus.Ready or GameStatus.Stale && PlanGrew(rec)) reason = Format.Then(reason, PlanGrewReason);
        // the running launch isn't the last play yet: its counts so far would stay on the page until its exit is read
        var playing = IsPlaying(g.Id);
        GameState? listed = null;
        if (playing) lock (_lock) listed = _games.Find(x => x.Game.Id == g.Id);
        var state = WithRecorder(new GameState(g, engine, antiCheat, status, reason,
                Collections.Member(g.Id) != null && !Indexed(g, rec) ? null : rec.ShaderCount,   // an index of another exe: the whole install's, from before the split
                rec.Plan?.Stats,
            psos * (long)(rec.BytesPerPso ?? DefaultBytesPerPsoOf(engine)),
            rec.Careful && careful != null ? careful : fast,
            rec.WarmedDriverVersion, rec.WarmedAt, rec.LastWarmTime, ours,
            Frames11Play(engine, Vendor.Caps) ? null : playing ? listed?.LastSession : session,   // the frames-only recorder writes no creates
            CacheOnDiskOf(g, engine, rec),
            rec.LaunchedExeName,
            // ponytail: re-detected on every evaluation (one directory listing); cache with the scan if listings get slow
            antiCheat == AntiCheat.None ? Middleware.Tags(Middleware.Detect(exeDir), d => SeedsPacks(g) ? (_planner as Planner)?.PackPipelines(d, OnAmd) ?? 0 : 0) : null,
            rec.LastWarmFailed, rec.LastWarmSkipped, StutterList.Current.Find(g),
            CommunityKept(g.Id) is { } c ? new CommunityInfo(c.Psos, c.DownloadedAt, File.Exists(RecordingPath(g.Id)), OtherVersion(c, g.Id, rec)) : null,
            Sharing.Shared(Store.GameDir(g.Id))?.At, inDb,
            pending.Planned,
            playing) with { Kept = KeptState(g.Id), CacheInUse = CacheInUseOf(g, engine, rec), WindowsCache = WindowsCacheOf(g, rec), DeliveredShaders = D3DSCache.Delivered(g.ExePath)?.Bytes is > 0 and var asd ? asd : null, DeliveryFits = DeliveryFitsOf(g),
                D3DSCacheCleanup = trimmed != null && D3D12Kept(rec) != null && D3DSCacheTrimmed(g.Id, rec) != null && D3DSCache.CleanedAutomatically(), KeptApis = KeptApis(rec, gpuNow), LastWarmNeedsRecording = rec.LastWarmNeedsRecording, LastWarmCrashed = rec.LastWarmCrashed, LastWarmDriverSkipped = rec.LastWarmDriverSkipped, LastWarmKnown = rec.LastWarmKnown, LastWarmCrashedBefore = rec.LastWarmCrashedBefore,
                LastWarmUnbuilt = rec.LastWarmUnbuilt, LastWarmInvalid = rec.LastWarmInvalid, LastWarmUnsupported = rec.LastWarmUnsupported,
                LastWarmTried = rec.LastWarmTried ?? rec.WarmedPlanCount, LastWarmFailedBy = rec.LastWarmFailedBy,
                Careful = cap != null ? new CarefulCompile(rec.Careful, rec.FirstLaunch?.Compiled, careful, recorded) : null,
                NewShadersOverride = rec.NewShaders, RtSkipped = rtSkipped, RayTracingOverride = rec.RayTracing, TraversalOverride = rec.Traversal, LeftOutOfAddAll = rec.LeftOutOfAddAll, NoSharedHashes = rec.NoSharedHashes, CacheCapGB = rec.CacheCapGB, PlanCacheCapGB = rec.PlanCacheCapGB,
                ApiOverride = rec.ApiOverride, ApiDetected = apiDetected, ExeChosen = rec.ChosenExe != null,
                RecordedSinceWarm = pending.Recorded, NewOnly = status == GameStatus.Stale && !pending.Unknown && WarmChanged(g, rec, engine, gpuNow) == null, CommunityDbPsos = entry?.Psos ?? 0, PsoPerSecond = RateOn(rec, gpuNow.Id),
                Official = OfficialInUse(g.Id) is { } o ? new CommunityInfo(o.Psos, o.DownloadedAt, File.Exists(RecordingPath(g.Id))) : null,
                LastFrames = playing ? listed?.LastFrames : frames, ReShade = reshade != null, ShaderMod = shaderMod?.Mod, ShaderModBlocks = reshade?.Blocks == true, ShaderModLayer = reshade?.Layered == true,
                ShaderModAsD3D12 = reshade is { Layered: true, AsD3D12: true }, RtUnseen = rtUnseen, RtToPlan = rtToPlan, RecordedEnough = rec.RecordedLong, RootUnconfirmed = unconfirmed, Vac = vac, Records11 = As11(engine, rec, Vendor.Caps),
                StreamlineFirst = slBlocks && engine != null && check.Readiness != Readiness.Unsupported, Preload = preload, PreloadHeld = preloadHeld, PreloadHeldByOptions = heldByOptions, AgsHost = agsHost,
                Dxvk = antiCheat == AntiCheat.None && OperatingSystem.IsWindows() && !(engine != null && VulkanPlan.IsVulkan(engine)) && ReShade.Dxvk(exeDir),   // under Proton every DirectX 11 game is DXVK's
                DxvkCompiled = antiCheat == AntiCheat.None && Warmer.DxvkDll(OwnDriver, g.ExePath, DxvkSteamDir(g) != null) != null,
                MfgUnlocker = antiCheat == AntiCheat.None && Path.GetFileName(g.ExePath).Equals("witcher3.exe", StringComparison.OrdinalIgnoreCase) && ReShade.MfgUnlocker(exeDir),
                StarfieldLuma = ReShade.StarfieldLuma(g) != null,
                RuntimeWarning = AmdAlwaysOn11(rec) ? AmdAlwaysOnWarning : !gpuNow.Gpu.Dx12 && engine?.GraphicsApi.StartsWith("D3D12", StringComparison.Ordinal) == true ? Warmer.NoDx12Warning
                    : antiCheat == AntiCheat.None ? Warmer.RuntimeWarning(rec.Plan?.Stats.MaxShaderModel, g.ExePath, Environment.OSVersion.Version.Build) : null,
                NoStutter = NoStutterOf(g, engine, StutterList.Current.Find(g)),
                CompileUnreached = unreached && status != GameStatus.Unsupported,
                MaximumSuggestion = MaximumSuggestion(g, rec) is { } suggested ? suggested.Note ?? "" : null, MaximumForGame = rec.Maximum,
                MaximumAddsBytes = Maximum(rec) ? null : MaximumAdds(rec.Plan?.Stats),
                // a run of an earlier build doesn't speak for this one
                PlayedAt = rec.WarmedGameVersion != null && rec.WarmedGameVersion != g.Version ? null : new[] { rec.LastPlay?.From, rec.FirstLaunch?.At }.Max(),
                Similar = SimilarOf(g), CompileFailed = rec.CompileFailedBuild != null && rec.CompileFailedBuild == (g.Version ?? ""),
                NotPlanned = GameVerdicts.Current.Unsupported(g, engine) is { } verdict ? verdict.Kind == GameVerdicts.UnsupportedVerdict
                    : status == GameStatus.Unsupported && reshade is not { Blocks: true } && antiCheat != AntiCheat.None   // a recording its anti-cheat blocks
                      && check.Readiness == Readiness.NeedsRecording },
            rec, ours, exeDir, dllName, shared);
        abandoned.ThrowIfCancellationRequested();
        return BeforeAfterNow(state, rec);
    }

    /// <summary>Why the source that lists <paramref name="g"/> holds it back now (<see cref="SteamShortcutSource.NotReady"/>);
    /// null when none does.</summary>
    string? SourceNotReady(Game g) => SteamShortcuts.Is(g.Id) ? _sources.OfType<SteamShortcutSource>().Select(s => s.NotReady(g)).FirstOrDefault(why => why != null) : null;

    GameState WithRecorder(GameState s, GameRecord rec, bool ours, string exeDir, string dllName, bool shared = false)
    {
        var by = Collections.RecordedBy(s.Game.Id);   // the folder's recorder and its DLL are the collection's first game's
        var foreign = Foreign(exeDir);
        var proxy = ours || foreign;   // another copy's recorder is no mod
        var vulkan = VulkanRoute(s.Game, s.Engine);   // no DLL of the recorder in the game's folder: no mod is in its way
        var skip = by != null && RecorderSkip(s, null) is not (SkipAntiCheat or SkipUnsupported or SkipNotDx12 or SkipCrashes or SkipIntelDriver)
            ? SharedRecorder(Collections.Member(by)!.Value.Game.Name)
            : RecorderSkip(s, by != null || vulkan ? null : ModSkip(s.Game, exeDir, rec, proxy, dllName)) ?? _recorderSkips.GetValueOrDefault(s.Game.Id);
        var o = rec.Recorder ?? (ours ? RecorderOverride.On : RecorderOverride.Default);
        var elsewhere = by == null && (foreign || Foreign(RecordedAt(s.Game, rec)));
        var db = Length(Path.Combine(exeDir, "scskiller.db"));
        var offline = OfflineEntry(s.Game, s.AntiCheat, s.Engine) != null && GameVerdicts.Current.Unsupported(s.Game, s.Engine) == null;
        var effective = by != null ? shared : RecorderWanted(o, RecordsByDefault(Settings, s), skip, PairFrames11(s.Engine, rec, Vendor.Caps));
        return s with { RecorderOverride = o, RecorderSkip = skip, RecorderEffective = effective,
            RecorderForeign = elsewhere, RecorderFailure = effective && by == null ? rec.RecorderFailure : null,
            RecorderNote = _recorderNotes.GetValueOrDefault(s.Game.Id)
                ?? (o == RecorderOverride.Off && rec.RecorderOrphans.Any(File.Exists) ? ModRemovedNote : null), RecorderMod = by != null || vulkan ? null : ModName(exeDir, rec, proxy, dllName),
            AgsMod = skip == SkipAgsMod ? AgsModName(Path.Combine(exeDir, Dll12)) : null,
            RecorderRefused = !ours ? null : LastLaunch(s.Game, rec, exeDir, dllName, s.Engine?.GraphicsApi),
            RecordAlongsideMod = rec.RecordAlongsideMod,
            RecordingBytes = RecordingFiles(s.Game).Where(f => Path.GetFileName(f) != FrameLog.FileName).Sum(Length),
            RecordingPaused = ours && DbCap(s.Game) is { } cap && db >= cap,
            OfflineEligible = offline, OfflineRecord = rec.OfflineRecord, AlwaysRecord = rec.AlwaysRecord,
            OfflineRunning = (OfflineLive(s.Game.Id) || rec.OfflineSession != null) && !_armed.ContainsKey(s.Game.Id),
            RiskEligible = !offline && RiskOffered(s), RiskNoLaunch = !offline && RiskOfferedButLaunch(s.Game, s.Engine, s.AnyAntiCheat) && !StoreLaunches(s.Game), SessionNote = _sessionNotes.GetValueOrDefault(s.Game.Id) };
    }

    static long Length(string path) => new FileInfo(path) is { Exists: true } f ? f.Length : 0;

    /// <summary>A process has the file open for writing, as the recorder holds its scskiller.db while the game runs (denying
    /// other writers), so the read-only open an import makes fails.</summary>
    static bool Writing(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException e) when (e.HResult == unchecked((int)0x80070020)) { return true; }   // ERROR_SHARING_VIOLATION
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }   // the import reports it
    }

    /// <summary>What <see cref="ClearRecording"/> deletes: the recorder's output in the game folder and SCSKiller's copy of it
    /// (an offline session's report too).</summary>
    IEnumerable<string> RecordingFiles(Game g) => [.. (Collections.RecordedBy(g.Id) != null || Foreign(g) ? [] : RecorderDataFiles).Select(f => Path.Combine(Path.GetDirectoryName(g.ExePath)!, f)), RecordingPath(g.Id),
        FozPath(g.Id), .. Collections.RecordedBy(g.Id) != null || Foreign(g) ? [] : VulkanFiles(g),
        .. SessionFiles.Select(f => Path.Combine(OfflineSessionDir(Store, g.Id), f)),
        .. new[] { "recording.all.db", "recording.all.db.key" }.Select(f => Path.Combine(Store.GameDir(g.Id), f))];   // until migrated

    static readonly string[] RecorderDataFiles = ["scskiller.db", "scskiller_creates.csv", "scskiller.log", FrameLog.FileName];

    readonly ConcurrentDictionary<string, (long Bin, DateTime Written, long Csv, PlayWindow? Played, FrameReport? Report)> _frames = new();

    /// <summary>The frame log's report, read again only when it, the creates csv or the watched run changed: a scan
    /// evaluates every game. <paramref name="playCapMs"/>: <see cref="FrameLog.Read"/>'s, cached apart.</summary>
    FrameReport? Frames(string exeDir, string exe, IReadOnlySet<string>? rayQuery, PlayWindow? played, double? playCapMs = null)
    {
        var (bin, csv) = (new FileInfo(Path.Combine(exeDir, FrameLog.FileName)), Path.Combine(exeDir, "scskiller_creates.csv"));
        if (!bin.Exists) return null;
        var stamp = (bin.Length, bin.LastWriteTimeUtc, Length(csv), played);
        var key = $"{bin.FullName}|{playCapMs}";
        if (_frames.TryGetValue(key, out var c) && (c.Bin, c.Written, c.Csv, c.Played) == stamp) return c.Report;
        FrameReport? r;
        try { r = FrameLog.Read(bin.FullName, csv, exe, rayQuery, played, playCapMs); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        _frames[key] = (stamp.Length, stamp.LastWriteTimeUtc, stamp.Item3, played, r);
        return r;
    }

    /// <summary>The launch into the game's frame history (<see cref="FrameSessions"/>), once per change of its summary: every
    /// scan evaluates every game.</summary>
    void NoteFrames(Game g, FrameReport r)
    {
        var s = FrameSessions.Of(r);
        if (_noted.TryGetValue(g.Id, out var was) && was == s) return;
        try
        {
            FrameSessions.Note(Store, g.Id, r);
            _noted[g.Id] = s;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{g.Name}: couldn't keep its frame times: {e.Message}"); }
    }
    readonly ConcurrentDictionary<string, FrameSession> _noted = new();

    public IReadOnlyList<FrameSession> FrameHistory(string gameId) => FrameSessions.Read(Store, gameId);

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

    /// <summary>The plan saw ray tracing: DXIL libraries in the index, recorded state objects or inline RayQuery shaders.</summary>
    /// <summary>The game's compile size limit in bytes when it is below what the vendor's cache gives one game's plan; else null.</summary>
    static long? UserCap(VendorCaps caps, int? capGB) => capGB is { } gb && (long)gb << 30 is var user && user < VendorBudget(caps) ? user : null;

    /// <summary>What the vendor's cache gives one game's plan (<see cref="PlanBuilder.CacheBudgetOf"/> where a plan is trimmed to it); long.MaxValue where none is.</summary>
    static long VendorBudget(VendorCaps caps) =>
        caps.WarmCacheBudget ?? caps.PerGameCacheCap ?? (caps is { PerStageCache: true, StateIndependentCache: false } ? PlanBuilder.CacheBudget : long.MaxValue);

    /// <summary>The game page's compile size limits, in GB.</summary>
    public static readonly int[] CacheCapsGB = [1, 2, 4, 8, 16, 32];

    /// <summary>The game page offers <see cref="GameState.CacheCapGB"/>: a DirectX 12 plan's generated pipelines are what it trims.</summary>
    public static bool HasCacheCap(GameState s) => s.CacheCapGB != null || s.Engine is { } e && e.GraphicsApi.Contains("D3D12") && !VulkanPlan.IsVulkan(e);

    public static bool HasRayTracing(PlanStats? p) => p is { RtLibraries: > 0 } or { RtStateObjects: > 0 } or { RtInline: > 0 };

    public const string RtInlineNote = "ray tracing that uses separate pipelines (such as path tracing) is compiled only from a recording";

    /// <summary>The same for a game's state: not while a newer recording waits for its plan (<see cref="GameState.RtToPlan"/>),
    /// nor once it was recorded long enough without ray tracing (<see cref="GameState.RtUnseen"/>).</summary>
    public static bool NeedsRtRecording(GameState s) => NeedsRtRecording(s.Plan) && !s.RtUnseen && !s.RtToPlan && !s.RtSkipped;

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
    public const string NeverSawNote = "The recorder never loaded in the last launch. Please report it with the game's name.";
    public const string NeverSawDx11Note = "The recorder didn't load in the last launch. The game may have run on DirectX 11.";
    public const string NeverSawDx12Note = "The recorder didn't load in the last launch. The game may have run on DirectX 12.";
    public const string GameChangedNote = "The game changed since the recorder was set up. It is set up again: play again.";
    public const string CollectionSwitchNote = "The last launch wasn't recorded: the recorder was set up for the game played before. It is set up for this game now: play again.";
    const string CollectionSwitchRefusal = "stamped for another game of its collection";   // the app's rewrite of the proxy's "exe not as checked"
    const string RanOtherExe = "The last launch ran ";
    public static string RanOtherExeNote(string exe) => $"{RanOtherExe}{exe}, not the exe the recorder was set up for. It is set up for it now: play again.";
    public static string RanOtherExeKeptNote(string exe) => $"{RanOtherExe}{exe}, not the exe the recorder was set up for.";

    /// <summary>The last launch was played with the recorder in and recorded nothing: another exe of the folder ran, the game
    /// changed since it was stamped, or the recorder never loaded. The list says so instead of asking for a recording again.</summary>
    public static bool NeverRecorded(GameState s) => s is { RecorderInstalled: true, RecorderSkip: null, RecorderRefused: { } r }
        && (r is NeverSawNote or NeverSawDx11Note or GameChangedNote or CollectionSwitchNote || r.StartsWith(RanOtherExe, StringComparison.Ordinal));

    /// <summary>What the game page says about the last launch when it wasn't recorded (<see cref="GameState.RecorderRefused"/>);
    /// null = it recorded, or no launch since. A switch to another exe of the folder in that launch wins: the recorder loaded
    /// into it and refused it (or couldn't tell).</summary>
    static string? LastLaunch(Game g, GameRecord rec, string exeDir, string dllName, string? api)
    {
        // the folder's recorder is stamped for another game of its collection: this exe was passed through on purpose (the
        // store's exe handing over to that game), and a missing refusal isn't a recorder that never loaded
        if (Collections.ExesBeside(g.ExePath).Any(x => !x.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase) && Attested(g with { ExePath = x }))) return null;
        var file = RefusedFile(g.ExePath);
        // one from before the last watched run is another launch's
        var refused = rec.LastPlay is { } last && File.Exists(file) && File.GetLastWriteTimeUtc(file) < last.From.UtcDateTime ? null : Refused(g.ExePath);
        var neverSaw = NeverSaw(g.ExePath, exeDir, dllName, rec.LastPlay);
        if ((Unstamped(refused) || neverSaw) && rec is { RunsExeAt: { } at, LastPlay: { } run, RunsExeWas: { } was } && run.From <= at && at <= run.To
            && rec.RunsExe == g.ExePath && !Path.GetFileName(was).Equals(Path.GetFileName(g.ExePath), StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(was)!.Equals(exeDir, StringComparison.OrdinalIgnoreCase))
            return RanOtherExeNote(Path.GetFileName(g.ExePath));
        // the recorder loaded into another exe of the folder, which wasn't followed (FollowUnrealOther)
        if (neverSaw && RefusedBeside(g.ExePath, exeDir, rec.LastPlay!) is { } other) return RanOtherExeKeptNote(Path.GetFileName(other));
        if (neverSaw) return dllName == Dll12 && ApiKey(api) == "11+12" ? NeverSawDx11Note : NeverSawNote;
        return refused switch
        {
            null => null,
            CollectionSwitchRefusal => CollectionSwitchNote,
            _ when Unstamped(refused) || refused == "scskiller.ini changed while deciding" => GameChangedNote,   // stamped again by the next clean check
            "loaded from another folder than the exe" => "The last launch wasn't recorded: a mod loaded a copy of the recorder from another folder, where it can't record.",
            var why => NotRecordedNote(why),
        };
    }

    static readonly string[] ManifestRefusals = ["VAC: a Steam manifest can't be read: ", "VAC: a Steam manifest is malformed: ", "VAC: a Steam manifest names no single folder and app: "];

    /// <summary>The note for a refusal the proxy wrote. One naming a Steam manifest's path says to move that file aside: a bare reason names no file.</summary>
    internal static string NotRecordedNote(string why) => $"The last launch wasn't recorded: {why}."
        + (ManifestRefusals.Any(m => why.StartsWith(m, StringComparison.Ordinal)) ? " Move it out of that folder and start the game again." : "");

    static string? RefusedBeside(string exe, string exeDir, PlayWindow run)
    {
        try
        {
            return Directory.EnumerateFiles(exeDir, "*.exe").FirstOrDefault(x => !x.Equals(exe, StringComparison.OrdinalIgnoreCase)
                && RefusedFile(x) is var f && File.Exists(f) && File.GetLastWriteTimeUtc(f) >= run.From.UtcDateTime);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The proxy's refusals of an ini without a valid stamp for the exe that ran.</summary>
    static bool Unstamped(string? why) => why is "exe not as checked" or "no install root" or "not signed by SCSKiller";

    /// <summary><see cref="GameRecord.RtUnseen"/> after the last recorded launch: a ray tracing state object in it clears it, a
    /// launch of <see cref="EnoughRecording"/> without one sets it (ray tracing off in the game: no recording will ever have one).</summary>
    internal static bool RtUnseen(SessionStats? last, bool before) => last switch
    {
        null => before,
        { StateObjectsReady: > 0 } or { StateObjectsCompiled: > 0 } or { StateObjectsStartupCompiled: > 0 } => false,
        _ => before || last.Duration >= EnoughRecording,
    };

    /// <summary>The game page's line and the compile's summary when <see cref="RayTracing"/> is off.</summary>
    public const string RtSkippedNote = "Ray tracing skipped (your setting)";
    /// <summary>The stale reason when it is on again after a compile without it.</summary>
    public const string RtToCompileReason = "ray tracing shaders are still to compile";
    /// <summary>The end of a Ready or Warmed reason when <see cref="GameState.RtUnseen"/> keeps a plan's uncovered ray tracing from being asked for.</summary>
    public const string RtUnseenNote = "no ray tracing seen while recording: turn it on in the game and play to cover it";

    /// <summary>The status reason of a game whose ray tracing needs a recording (<see cref="NeedsRtRecording"/>).</summary>
    public static string RtNote(bool? inCommunityDb, bool entitled) => inCommunityDb == null
        ? RtNeedsRecording + ", or a community recording for this version" : Format.Then(RtNeedsRecording, DbNote(inCommunityDb, entitled));

    public const string RtNeedsRecording = "Ray-traced effects need one short recording";

    /// <summary>The end of a NeedsRecording reason when the manifest has an entry for the build, whatever the sign-in (the
    /// manifest is public; downloads need "db"). Short: the Library row shows three lines.</summary>
    public const string InDbNote = "in the community database";
    /// <summary><see cref="InDbNote"/> for a user not entitled to "db": the entry is public, the download isn't.</summary>
    public const string InDbSupportersNote = InDbNote + " for Patreon supporters";

    static string? DbNote(bool? inDb, bool entitled) => inDb switch { true => entitled ? InDbNote : InDbSupportersNote, false => "not in the community database yet", null => null };

    /// <summary>The manifest's entry for the installed build: by the index's content hash while the index is of this build
    /// (or, without a store build id, from before the exe stamp was kept), else by the store build's alias.</summary>
    static CommunityEntry? DbEntry(CommunityManifest m, Game g, GameRecord rec, bool full) =>
        IndexIsInstalled(g, rec) || g.Version == null && rec.IndexExeStamp == null ? m.FindRichest(g, rec.IndexContentHash, rec.IndexLegacyHashes, full) : m.Find(g, null, full);

    /// <summary>An entry's 'X' (500,000 records) is read on NVIDIA only: AMD planning memory is over budget at that size.</summary>
    bool FullEntries => OwnDriver == GpuVendor.Nvidia;

    /// <summary>The manifest's official recording for the installed build, matched as <see cref="DbEntry"/> matches.</summary>
    static OfficialEntry? OfficialEntry(CommunityManifest m, Game g, GameRecord rec) =>
        (IndexIsInstalled(g, rec) || g.Version == null && rec.IndexExeStamp == null ? m.FindOfficial(g, rec.IndexContentHash) : null) ?? m.FindOfficial(g, null);

    /// <summary>Why a game's ray tracing needs a recording on this PC (<see cref="NeedsRtRecording"/>), for the detail page.</summary>
    public static string RtWhy(VendorCaps caps, EngineInfo? e) => caps.RtCacheGranularity == RtCacheGranularity.WholeObject
        ? "This GPU's driver reuses ray tracing pipelines only exactly as the game builds them, and which ones it builds isn't in its files."
        : $"SCSKiller can't rebuild {(e is { } x ? Format.Engine(x) : "this engine")}'s ray tracing layout from the game files yet.";

    static string RtBlocked(bool rt, string? noRecording, string reason) => !rt ? reason
        : Format.Then(reason, noRecording != null ? $"ray-traced effects aren't compiled: they need a recording, {noRecording}" : RtAfterRecordingNote);

    /// <summary>The end of a Warmed reason when a recording would add the game's ray tracing: compiled, the recording offered.</summary>
    public const string RtAfterRecordingNote = "ray tracing needs a 5-min recording";

    public static bool RtAfterRecording(GameState s) => s.Status == GameStatus.Warmed && s.StatusReason.Contains(RtAfterRecordingNote, StringComparison.OrdinalIgnoreCase);

    /// <summary>The game page's line for a Vulkan game whose plan has ray tracing or RayQuery pipelines, on NVIDIA: its
    /// driver keeps no ray tracing pipeline on disk and RayQuery ones only sometimes (measured), so the warm replays them
    /// and they compile again in the game. Null otherwise.</summary>
    public static string? VulkanRtNote(GameState s, GpuVendor vendor) =>
        vendor == GpuVendor.Nvidia && s.Engine is { } e && VulkanPlan.IsVulkan(e) && s.Plan is { RtStateObjects: > 0 } or { RtInline: > 0 }
            ? "On NVIDIA, a Vulkan game's ray tracing shaders still compile while you play." : null;

    /// <summary>The game page's note under a compilable status when the plan rests on a guess: a guessed Unreal version
    /// changes DirectX 12 root signatures (DirectX 11 has none), a DirectX that is the engine's default may not be the
    /// one the game runs. Else the reader's <see cref="EngineInfo.Note"/>. Null: nothing to say.</summary>
    public static string? GuessNote(EngineInfo e) => (e.VersionGuessed, e.ApiGuessed) switch
    {
        (true, true) => "Unreal version and " + (e.OffersVulkan ? "graphics API" : "DirectX"),
        (true, false) when e.GraphicsApi.Contains("D3D12") => "Unreal version",
        (false, true) => e.OffersVulkan ? "Graphics API" : "DirectX",
        _ => null,
    } is { } what ? what + " guessed, so the compile could be wrong." : e.Note;

    /// <summary>The graphics API a game is planned and recorded for: where detection guessed the engine's default
    /// (<see cref="EngineInfo.ApiGuessed"/>), this PC's recording that shows the API decides, else the user's choice, which
    /// also outranks a sure detection. Detected: what detection found, null for an engine that is neither DirectX nor Vulkan
    /// (nothing to choose). Ambiguous stays both without a choice: both cover whatever a recording shows.</summary>
    (EngineInfo Engine, string? Detected) WithApiChoice(Game g, EngineInfo e, DxApi? choice)
    {
        // a Vulkan game on a vendor without a Vulkan warm has nothing to choose
        if (ApiKey(e.GraphicsApi) is not { } key || key == "vk" && Vendor.Caps.VulkanKey == null && choice == null) return (e, null);
        // a game that runs either API (CarvedReader's OffersVulkan) records what the user played last: their pick wins
        var proved = e.ApiGuessed && !(choice != null && e.OffersVulkan && CarvedReader.Carved(e)) ? RecordedApi(g.Id) : null;
        if (proved == "Vulkan" && Vendor.Caps.VulkanKey == null) proved = null;
        if (proved != null) return (e with { GraphicsApi = proved + RecordingSuffix, ApiGuessed = false }, e.GraphicsApi);
        return (choice is { } c ? e with { GraphicsApi = c + ChoiceSuffix, ApiGuessed = false } : e, e.GraphicsApi);
    }

    public const string RecordingSuffix = " (recording)", ChoiceSuffix = " (your choice)";

    /// <summary>The game page's line for a Vulkan game whose exe name another listed Vulkan game has (Star Citizen's LIVE and
    /// PTU) on a vendor that keys its Vulkan cache on the exe name: one cache file serves both. Null: none.</summary>
    public static string? VulkanCacheShared(GameState s, IEnumerable<GameState> games, VendorCaps caps)
    {
        if (caps.VulkanKey != VulkanPlan.ExeNameKey || s.Engine is not { } e || !VulkanPlan.IsVulkan(e)) return null;
        var exe = Path.GetFileName(s.Game.ExePath);
        var others = games.Where(o => o.Game.Id != s.Game.Id && o.Engine is { } oe && VulkanPlan.IsVulkan(oe)
            && Path.GetFileName(o.Game.ExePath).Equals(exe, StringComparison.OrdinalIgnoreCase)).Select(o => o.Game.Name).ToList();
        return others.Count == 0 ? null : $"Shares its driver cache with {string.Join(" and ", others)}, so a compile of either fills it for both.";
    }

    /// <summary>The API this PC's recordings show: the DirectX of recording.db (<see cref="Planner.RecordedApi"/>) or Vulkan for
    /// a recording.foz with a pipeline, the one written last when both; null = neither.</summary>
    string? RecordedApi(string gameId)
    {
        var (db, foz) = (new FileInfo(RecordingPath(gameId)), new FileInfo(FozPath(gameId)));
        var dx = db.Exists ? Planner.RecordedApi(db.FullName) : null;
        var vk = foz.Exists && VulkanPlan.Check(new Recording(foz.FullName)).Readiness == Readiness.Ready ? "Vulkan" : null;
        return dx != null && vk != null ? (foz.LastWriteTimeUtc > db.LastWriteTimeUtc ? vk : dx) : dx ?? vk;
    }

    /// <summary>The APIs the game page's picker offers after Auto: DirectX 11 and 12, and Vulkan where the vendor has a Vulkan
    /// warm (<see cref="VendorCaps.VulkanKey"/>), also where detection found none: a game that starts its renderer in another
    /// process can hide it. A Vulkan choice already made stays listed.</summary>
    public static IReadOnlyList<DxApi> ApiChoices(GameState s, VendorCaps caps) =>
        caps.VulkanKey != null || s.ApiOverride == DxApi.Vulkan
            ? [DxApi.D3D11, DxApi.D3D12, DxApi.Vulkan] : [DxApi.D3D11, DxApi.D3D12];

    public static string ApiName(DxApi api) => api switch { DxApi.D3D11 => "DirectX 11", DxApi.D3D12 => "DirectX 12", _ => "Vulkan" };

    /// <summary>A <see cref="EngineInfo.GraphicsApi"/> as the game page names it; "D3D11 or D3D12" as DirectX 12.</summary>
    public static string ApiName(string api) => api.StartsWith("Vulkan", StringComparison.Ordinal) ? "Vulkan" : api.Contains("D3D12") ? "DirectX 12" : "DirectX 11";

    /// <summary>The API a plan for <paramref name="api"/> (<see cref="EngineInfo.GraphicsApi"/>) covers, as
    /// <see cref="PlanBuilder"/> reads it: "11", "12" or "11+12", "vk" for Vulkan; null = none of them. Why the API was decided takes no part.</summary>
    public static string? ApiKey(string? api) => api == null ? null : api.StartsWith("Vulkan", StringComparison.Ordinal) ? "vk"
        : (api.StartsWith("D3D11") ? "11" : "") + (api.StartsWith("D3D11") && api.Contains("D3D12") ? "+" : "") + (api.Contains("D3D12") ? "12" : "") is { Length: > 0 } k ? k : null;

    /// <summary>The game page's line when the user's API isn't the one used: a recording shows another (only a guessed
    /// API gives way to one). Null: the choice is used, or there is none.</summary>
    public static string? ApiOverruled(GameState s)
    {
        if (s is not { ApiOverride: { } choice, Engine: { } e } || e.GraphicsApi.EndsWith(ChoiceSuffix, StringComparison.Ordinal)) return null;
        if (ApiKey(e.GraphicsApi) == ApiKey(choice.ToString())) return null;
        return $"Your recording shows {ApiName(e.GraphicsApi)}, so your {ApiName(choice)} choice isn't used.";
    }

    /// <summary>Why a game the user added has no recorder yet, after "needs a recording, ".</summary>
    public const string ManualNoRecording = "once you confirm its game folder", StreamlineNoRecording = "which the recorder can't make in this game yet";
    /// <summary>The same for a game a risk session is offered for (<see cref="GameState.RiskEligible"/>).</summary>
    public const string RiskNoRecording = "from a session on the game page, at your own risk";

    /// <summary>The session card's line for an engine SCSKiller doesn't support: nothing compiles from the recording yet. Null:
    /// a supported engine.</summary>
    public static string? SessionKeptNote(GameState s) => s.Engine switch
    {
        { Encrypted: true } => "Its recording is kept for later. It compiles once you add the game's AES key.",
        { Unsupported: not null } e when !CarvedReader.RecordedOnly(e) => "This game isn't supported yet, so its recording is kept for later and nothing compiles from it now.",
        _ => null,
    };

    /// <summary>The game page's line for a game a risk session would be offered for but for its store (<see cref="GameState.RiskNoLaunch"/>).</summary>
    public static string NoLaunchNote(string store) => $"No recording session: SCSKiller can't start games from {store}.";
    /// <summary>An EasyAntiCheat game that an offline session can record (<see cref="OfflineEac"/>), with no recording yet:
    /// NeedsRecording with this reason, "Needs an offline session".</summary>
    public const string OfflineSessionNote = "EasyAntiCheat blocks the recorder, but you can record at your own risk with an offline session.";

    /// <summary>The game waits for an offline session (<see cref="OfflineSessionNote"/>).</summary>
    public static bool NeedsOfflineSession(GameState s) => s.Status == GameStatus.NeedsRecording && s.StatusReason == OfflineSessionNote;

    /// <summary>The game waits for its first start from Steam (<see cref="VulkanPlan.StartOnce"/>, <see cref="LauncherClearsReason"/>), not for a recording.</summary>
    public static bool NeedsSteamStart(GameState s) => s.Status == GameStatus.NeedsRecording && s.StatusReason is VulkanPlan.StartOnce or LauncherClearsReason;

    public const string LauncherClearsReason = "start it once from Steam, then compile: its launcher clears the shader cache at the first start of a new install or season";

    /// <summary>Marvel Rivals' launcher deletes the game's driver cache files when the season it marks in meta_info isn't the
    /// current one (none on a new install), then marks it: a compile before its first start since the game's last update
    /// is lost. Unknown (no version.json, a time that can't be read) doesn't hold the compile back.</summary>
    bool LauncherClearsNext(Game g)
    {
        if (!OperatingSystem.IsWindows() || g.Id != "steam:2767030") return false;
        try
        {
            return File.GetLastWriteTimeUtc(Path.Combine(g.InstallDir, "MarvelGame", "version.json")) is var updated && updated.Year > 1601
                && File.GetLastWriteTimeUtc(Path.Combine(LocalAppData, "MarvelRivals_Launcher", "meta_info")) < updated;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public const string KeyForFullCoverage = "needs the game's AES key for full coverage";

    /// <summary>An Unreal game with encrypted files whose key SCSKiller doesn't have: <see cref="SetEncryptionKey"/> takes the user's own.
    /// Other engines' encryption isn't an AES key it can take.</summary>
    public static bool NeedsAesKey(GameState s) => s.Engine is { Family: "Unreal" } e && (e.Encrypted || e.KeyMissing || e.KeyOptional);

    /// <summary>A BlackSpace game without the key that opens its root signatures: <see cref="SetEncryptionKey"/> takes the
    /// user's own. Without it the game compiles from a recording.</summary>
    public static bool NeedsGameKey(GameState s) => s.Engine is { } e && BlackSpace.BlackSpaceReader.NeedsKey(e);

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
    /// <summary>The items the last warm replayed: the plan's but what earlier compiles created (<see cref="GameRecord.LastWarmTried"/>),
    /// or the plan's for a warm from before that was kept.</summary>
    static long Tried(long? tried, PlanStats? plan) => tried ?? PlanItems(plan) ?? 0;
    static string Uncompiled(long n, long of) => $"{n:N0}{(of > 0 ? $" of {of:N0}" : "")} pipeline{(n == 1 && of == 0 ? "" : "s")} couldn't be compiled";
    /// <summary>The Library's line: "180,000 of 258,210 pipelines couldn't be compiled"</summary>
    public static string PartlyCompiledNote(GameState s) => Uncompiled(Rejected(s.LastWarmFailed, s.LastWarmCrashed) ?? 0, Tried(s.LastWarmTried, s.Plan));
    /// <summary>The page's and the CLI's: "180,000 of 258,210 pipelines couldn't be compiled, so those can still stutter the first time"</summary>
    public static string RefusedNote(GameState s) => RefusedNote(s.LastWarmFailed ?? 0, s.LastWarmCrashed ?? 0, Tried(s.LastWarmTried, s.Plan));
    static string RefusedNote(long failed, long crashed, long items) => (failed > 0 ? Uncompiled(failed, items) + (crashed > 0 ? $" and the driver crashed on {crashed:N0}" : "")
        : $"the GPU driver crashed on {crashed:N0} of {items:N0} pipelines") + ", so those can still stutter the first time";
    /// <summary>The compile card's line on what the last warm couldn't compile, with what most of it was (<see cref="TopFailKind"/>);
    /// null when nothing failed.</summary>
    public static string? RefusedLine(GameState s) => s.LastWarmFailed is not > 0 ? null
        : Uncompiled(s.LastWarmFailed.Value, Tried(s.LastWarmTried, s.Plan)) + s.LastWarmFailedBy switch
        {
            0 => ": the driver refused their root signatures.",
            1 => ": the driver refused them.",
            2 => ": SCSKiller couldn't build them from the game's files.",
            _ => ".",
        };

    /// <summary>The last warm is known and neither failed nor crashed; skipped pipelines (not in this game version) aren't failures.</summary>
    public static bool NothingFailed(GameState s) => s.LastWarmFailed != null && s.LastWarmFailed + (s.LastWarmCrashed ?? 0) == 0;

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
              : r.Plan?.Stats.Recorded == 0 ? ". A careful compile only changes how the recorded pipelines compile, and there is no recording yet (turn recording on and play)"
              : $". A careful compile ({threads} threads, in passes) reaches more of them" + (careful is { } t ? $", in about {Duration(t)}" : "")
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

    /// <summary>The game page's coverage ring, one measure in every state: <see cref="Coverage"/> (1 when the stage sets
    /// aren't counted) times the plan's items the last warm left in the cache (less the ones the driver refused, that crash it
    /// or that it skipped for hanging it, the D3D11 ones SCSKiller can't build yet, the Vulkan ones whose shaders fail validation
    /// or that need a feature the GPU lacks) over the plan's items: the last compile's own result. Pipelines new since it
    /// (<see cref="NewShaders.Count"/>) are the status's line, never a lower ring. Floored; null without a plan or when the compile doesn't serve the game.</summary>
    public static int? ShownPercent(GameState s)
    {
        if (s.CompileUnreached || s.Plan is not { } p || PlanItems(p) is not { } n || n <= 0) return null;
        var (covered, sets) = p.StageSets > 0 ? (p.StageSets - p.LeftOut, p.StageSets) : (1L, 1L);   // in integers: 0.29 * 100 floors to 28 in doubles
        var cached = Math.Max(0, n - (Rejected(s.LastWarmFailed, s.LastWarmCrashed) ?? 0) - (s.LastWarmDriverSkipped ?? 0) - (s.LastWarmUnbuilt ?? 0) - (s.LastWarmInvalid ?? 0)
            - (s.LastWarmUnsupported ?? 0));
        return (int)(covered * (Int128)100 * cached / ((Int128)sets * n));
    }

    /// <summary>The game page shows the plan's coverage: never for a Not supported game, which no compile will use, unless
    /// its cache compiled before the verdict is still there.</summary>
    public static bool ShowsCoverage(GameState s) => s.Plan != null && (s.Status != GameStatus.Unsupported || s.WarmedAt != null);

    /// <summary>The game page's row for pipelines the plan of a game on <paramref name="engine"/> leaves out for the cache budget (<see cref="PlanStats.CacheBudgetLeftOut"/>).</summary>
    public static string CacheBudgetTitle(VendorCaps caps, EngineInfo? engine = null, int? capGB = null) => VulkanCap(caps, engine) != null ? $"More than one {PerGameVendor(caps)} shader cache file holds"
        : UserCap(caps, capGB) != null ? "More than this game's compile size limit"
        : caps.PerGameCacheCap != null && caps.Profile == NvidiaLinux.Profile ? "More than SCSKiller's compile folder holds"
        : caps.PerGameCacheCap != null ? $"More than the {PerGameVendor(caps)} shader cache keeps per game"
        : "More than the AMD shader cache holds";

    /// <summary>Why the plan of a game on <paramref name="engine"/> leaves pipelines out for the cache budget (<see cref="PlanStats.CacheBudgetLeftOut"/>), on the game page.</summary>
    public static string CacheBudgetNote(VendorCaps caps, EngineInfo? engine = null, int? capGB = null) => (VulkanCap(caps, engine) is { } file
            ? $"This game has more pipelines than one {PerGameVendor(caps)} shader cache file holds ({App.Format.Bytes(file)}). "
            : UserCap(caps, capGB) is { } user
            ? $"This game has more pipelines than its compile size limit ({App.Format.Bytes(user)}). "
            : caps.PerGameCacheCap is { } folder && caps.Profile == NvidiaLinux.Profile
            ? $"This game has more pipelines than SCSKiller's compile folder holds for it ({App.Format.Bytes(folder)}"
                + (folder == NvidiaLinux.PlacedCacheSize ? $", {SteamFilesFit}). " : "). ")
            : caps.PerGameCacheCap is { } cap
            ? $"This game has more pipelines than the {PerGameVendor(caps)} driver keeps for one game ({App.Format.Bytes(cap)}). "
            : $"This game has more pipelines than the AMD driver's whole shader cache can hold ({App.Format.Bytes(AmdAppCache.DxcCacheCap)}). ")
        + "So SCSKiller compiles the ones most likely to be used, recorded ones first. The rest compile while you play.";

    // NvidiaLinux.PlacedCacheSize: what the placer fits in Steam's files, each read up to NvidiaLinux.CacheFileIndexed
    static readonly string SteamFilesFit = $"what fits in Steam's shader cache files of {App.Format.Bytes(NvidiaLinux.CacheFileIndexed)} each";

    static long? VulkanCap(VendorCaps caps, EngineInfo? engine) => engine != null && VulkanPlan.IsVulkan(engine) ? caps.VulkanCacheCap : null;

    static string PerGameVendor(VendorCaps caps) => caps.Profile switch { NvidiaLinux.Profile => "NVIDIA", MesaLinux.Profile => "Mesa", _ when caps.VulkanKey == VulkanPlan.DeviceKey => "AMD", _ => "Intel" };

    /// <summary>The game page's "Left out" row: <see cref="PlanStats.LeftOut"/> and why. Past the Uncovered ones, they're
    /// stage sets no recording showed yet (no root signature or template). A Vulkan plan's are ray tracing and descriptor heap
    /// pipelines (<see cref="PlanStats.DescriptorHeap"/>).</summary>
    public static string LeftOutValue(PlanStats p)
    {
        if (p.DescriptorHeap > 0)
            return $"{p.LeftOut:N0} (" + (p.LeftOut > p.DescriptorHeap ? $"{p.LeftOut - p.DescriptorHeap:N0} ray tracing, {p.DescriptorHeap:N0} " : "")
                + "using descriptor heaps, which the driver reads only from the game's own shader cache)";
        var notSeen = p.LeftOut - p.Uncovered;
        return $"{p.LeftOut:N0} (" + (notSeen <= 0 ? "shader slots SCSKiller can't rebuild"
            : p.Uncovered > 0 ? $"{p.Uncovered:N0} for shader slots SCSKiller can't rebuild, {notSeen:N0} not seen in a recording yet"
            : "not seen in a recording yet") + ")";
    }

    /// <summary>The game page's line for a plan whose left-out stage sets no recording showed yet.</summary>
    public static string NotSeenNote(bool recordOn) => "SCSKiller hasn't seen how the game sets the rest up yet. "
        + (recordOn ? "Keep playing with recording on. What it sees is added the next time it compiles." : "Playing longer with recording on teaches it.");

    /// <summary>The game page's line for a plan whose left-out stage sets use root signature slots the engine adds.</summary>
    public static string EngineSlotsNote(bool recordOn) => "The rest use shader slots this game's engine adds. "
        + (recordOn ? "Keep playing with recording on. They're rebuilt the next time it compiles." : "A 5-minute recording lets SCSKiller rebuild them.");

    /// <summary>The last compile ended where the game's compile cache held no more (<see cref="GameRecord.WarmedRoomLeft"/>); null when it didn't.</summary>
    static string? RoomNote(GameRecord r) => r.WarmedRoomLeft is not { } left ? null
        : left > 0 ? $"{left:N0} pipelines didn't fit in the game's compiled cache and compile while you play"
        : "the game's compiled cache is full, so new pipelines compile while you play";

    /// <summary>The status note of a partial plan (<see cref="IsPartial"/>): what it compiles and what would add the rest.</summary>
    public static string? PartialNote(PlanStats? p) => !IsPartial(p) ? null
        : p!.Recorded == 0 ? $"compiles {p.Generated:N0} pipelines. A 5-minute recording lets SCSKiller rebuild the rest"
        : $"compiles {p.Recorded + p.Generated:N0} pipelines. {p.Uncovered:N0} more shader combinations use shader slots SCSKiller can't rebuild yet";

    /// <summary>A queue line's progress (CLI): " done/total (N failed[, M skipped: not in this install]) rate/s" (reading shaders: " done/total packages",
    /// a plan or file preparation step: " 42.0% done/total shader groups paired", " done recording entries read" while its total is unknown), then for
    /// Done/Stopped the note (<see cref="WarmCounts"/>); "" without progress.</summary>
    public static string ProgressText(QueueItem q)
    {
        if (q is { Stage: QueueStage.Merging, Note: { } writing }) return ": " + writing;
        if (q.Stage is QueueStage.Saving or QueueStage.Finishing)
            return $": {(q.Stage == QueueStage.Saving ? App.Format.SavingStage : App.Format.FinishingStage)}" + (q.Note is { } elapsed ? ", " + elapsed : "");
        if (q.Progress is not { } x) return "";
        if (q.Stage == QueueStage.Indexing) return $" {x.Done}/{x.Total} packages";
        if (q is { Stage: QueueStage.Planning or QueueStage.Materializing } && x.Note is { } step) return x.Total > 0 ? $" {100.0 * x.Done / x.Total:0.0}% {x.Done}/{x.Total} {step}" : $" {x.Done} {step}";
        if (q.Stage == QueueStage.Merging) return $": {App.Format.MergingStage}" + (x.Total <= 0 ? "" : $", {100.0 * x.Done / x.Total:0}%, {x.Done >> 20}/{x.Total >> 20} MB"
            + (x.PerSecond > 0 && x.Done < x.Total ? $", {Duration(TimeSpan.FromSeconds((x.Total - x.Done) / x.PerSecond))} left" : ""));
        var counts = $"{x.Failed} failed" + (x.Skipped > 0 ? $", {x.Skipped} skipped: not in this install" : "");
        return $" {x.Done}/{x.Total} ({counts}) {x.PerSecond:0}/s" + (MemoryLimitNote(x) is { } m ? ", " + m : "")
            + (q.Stage is QueueStage.Done or QueueStage.Stopped or QueueStage.Warming && q.Note is { } n ? " - " + n : "");
    }

    /// <summary>"memory limit: 4 of 32 threads" while the warm's memory budget runs fewer threads than asked; null otherwise.</summary>
    public static string? MemoryLimitNote(WarmProgress p) => p is { Workers: > 0 } && p.Workers < p.Threads ? $"memory limit: {p.Workers} of {p.Threads} threads" : null;

    /// <summary>A warming item's note when its done count stopped moving (<see cref="StallAfter"/>): "no progress for 3 min".</summary>
    public static string StalledNote(TimeSpan since) => $"no progress for {Math.Max(1, (int)since.TotalMinutes)} min";

    /// <summary>A warming item that stopped moving: its note says for how long, and it has no time estimate.</summary>
    public static bool Stalled(QueueItem q) => q.Stage == QueueStage.Warming && q.Note?.StartsWith("no progress") == true;

    static string Took(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m{t.Seconds:00}s" : t.TotalMinutes >= 1 ? $"{t.Minutes}m{t.Seconds:00}s" : $"{(int)t.TotalSeconds}s";

    /// <summary>What a warm's counts say beyond the compiled ones; null when all are 0. Failed = not created, for what
    /// <paramref name="failedBy"/> (<see cref="TopFailKind"/>) says, no reason when it is null;
    /// skipped = never replayed; crashed = never replayed because its create removed the device (crashed the GPU driver);
    /// driverSkipped = <see cref="WarmResult.DriverSkipped"/>; unbuilt = <see cref="Unbuilt11"/>, not in failed; invalid =
    /// <see cref="WarmResult.Invalid"/>; unsupported = <see cref="WarmResult.Unsupported"/>; known = <see cref="WarmResult.Known"/>, of driverSkipped;
    /// crashedBefore = of crashed, the crash keys an earlier compile on this driver found.</summary>

    public static string? WarmCounts(long failed, long skipped, long crashed = 0, long driverSkipped = 0, long unbuilt = 0, PlanStats? plan = null, long invalid = 0,
        long unsupported = 0, long known = 0, int? failedBy = null, long crashedBefore = 0) =>
        string.Join(", ", new[]
        {
            failed > 0 ? $"{failed} failed" + failedBy switch
            {
                0 => " (the driver refused their root signatures)",
                1 => " (the driver rejected them)",
                2 => " (SCSKiller couldn't build them from the game's files)",
                _ => "",
            } : null,
            skipped > 0 ? $"{skipped} skipped ({SkippedWhy(plan)})" : null,
            crashed - crashedBefore is > 0 and var crashedNow ? $"{crashedNow} skipped ({(crashedNow == 1 ? "it crashes" : "they crash")} the GPU driver)" : null,
            driverSkipped - known is > 0 and var now ? $"{now} skipped for this compile (the GPU driver crashed or hung on {(now == 1 ? "it" : "them")} or on what {(now == 1 ? "it builds" : "they build")} on, or {(now == 1 ? "it wasn't" : "they weren't")} tried past a spot where it kept failing)" : null,
            unbuilt > 0 ? $"{unbuilt} skipped (SCSKiller can't build {(unbuilt == 1 ? "it" : "them")} yet)" : null,
            invalid > 0 ? $"{invalid} skipped ({InvalidWhy(invalid)})" : null,
            unsupported > 0 ? $"{unsupported} skipped ({UnsupportedWhy(unsupported)})" : null,
            known + crashedBefore > 0 ? KnownNote(known + crashedBefore) : null,
        }.OfType<string>()) is { Length: > 0 } s ? s : null;

    /// <summary>Items skipped because they crashed or hung this driver in an earlier compile (<see cref="WarmResult.Known"/>, and
    /// crash keys found before).</summary>
    public static string KnownNote(long known) => $"{known:N0} skipped: they crashed this driver before";

    /// <summary>The most keys <see cref="GameRecord.KnownKeys"/> holds: past it a crash or hang is skipped for its compile only.</summary>
    public const int MaxKnownKeys = 1000;

    /// <summary>A re-compile that replayed only what earlier compiles on this driver didn't create (<paramref name="keptEarlier"/>
    /// left out), where the last one refused at least as many and the driver refused every one again; null otherwise.</summary>
    public static string? RetriedNote(long total, long failed, long keptEarlier, long refusedBefore) =>
        keptEarlier > 0 && total > 0 && failed == total && refusedBefore >= total
            ? $"0 of {total:N0} retried pipelines created, the driver refuses them every time. The {keptEarlier:N0} compiled earlier are kept." : null;

    /// <summary>Why <see cref="WarmResult.Invalid"/> pipelines were skipped.</summary>
    public static string InvalidWhy(long n) => n == 1 ? "its shaders fail Vulkan validation, Steam skips it too" : "their shaders fail Vulkan validation, Steam skips them too";

    /// <summary>Why <see cref="WarmResult.Unsupported"/> pipelines were skipped.</summary>
    public static string UnsupportedWhy(long n) => n == 1 ? "this GPU doesn't support what it needs" : "this GPU doesn't support what they need";

    /// <summary>Why a warm's skipped pipelines were never replayed. A Vulkan plan with Valve's download
    /// (<see cref="PlanStats.FromDownload"/>): the download names shader modules it doesn't carry.</summary>
    public static string SkippedWhy(PlanStats? p) => p is { FromDownload: > 0, Generated: 0 } ? DownloadSkipped : "a shader not in this install";
    public const string DownloadSkipped = "Steam's download names shaders it doesn't include";

    /// <summary>Warm rate (PSO/s) before this PC measured one, per vendor: NVIDIA 450 (30 threads: 450-1350);
    /// AMD 160 (~8 threads, from the AMD smoke and per-stage A/B runs: a cold PSO there is ~10 ms).</summary>
    public static double DefaultWarmRate(GpuVendor vendor) => vendor == GpuVendor.Amd ? DefaultAmdPsoPerSecond : DefaultPsoPerSecond;

    /// <summary>The compile memory budget "Auto" picks (GB) from the PC's total physical memory: one step per common size,
    /// with the thresholds between sizes (a 16 GB PC reports a little less), a quarter of it from 64 GB.</summary>
    public static int AutoCompileMemoryGB(long totalPhysicalBytes) => (totalPhysicalBytes / (double)(1L << 30)) switch
    {
        <= 20 => 2,
        <= 28 => 4,
        <= 40 => 6,
        <= 56 => 8,
        var gb => Math.Max(16, (int)(gb / 4)),
    };

    /// <summary>The threads a run uses for a thread setting: at most this PC's. A slider shows this, so a setting above it stays saved until the slider moves.</summary>
    public static int UsedThreads(int setting) => Math.Min(setting, Environment.ProcessorCount);

    public static int CompileMemoryGB(Settings s) => s.MaxCompileMemoryGB > 0 ? s.MaxCompileMemoryGB : AutoCompileMemoryGB();

    public static int AutoCompileMemoryGB() => AutoCompileMemoryGB(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    /// <summary>The PC's visible RAM in whole GB, rounded down: what Settings names.</summary>
    public static int RamGB(long totalPhysicalBytes) => (int)(totalPhysicalBytes >> 30);

    public static int RamGB() => RamGB(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    /// <summary>The most "Maximum memory for compiling" offers: the RAM less 2 GB that Windows and the desktop keep using
    /// while the compile runs, at least 16 GB.</summary>
    public static int MaxCompileMemoryGB(long totalPhysicalBytes) => Math.Max(16, RamGB(totalPhysicalBytes) - 2);
    public const double DefaultAmdPsoPerSecond = 160;

    /// <summary>The estimate's rate for a game without its own measured warm: the games' measured rates as one
    /// (<see cref="MeasuredRate"/>), else the first complete warm measured on this PC on this driver (<see cref="SeedWarmRate"/>),
    /// else <see cref="DefaultWarmRate"/>.</summary>
    double WarmRate(string? driverId) => MeasuredRate(Games) ?? ((_warmRates ??= Store.LoadWarmRates()).GetValueOrDefault(RateKey(driverId, SkipD3DSCacheFlush())) is > 0 and var r ? r : DefaultWarmRate(Vendor.Vendor));
    Dictionary<string, double>? _warmRates;

    string RateKey(string? driverId, bool noflush) => $"{Vendor.Vendor}|{RateDriver(driverId, noflush)}";

    /// <summary>The driver id a measured rate is kept under: with skipped D3DSCache flushes a compile runs at another rate.</summary>
    static string? RateDriver(string? driverId, bool noflush) => driverId != null && noflush ? driverId + "|noflush" : driverId;

    /// <summary>The game's measured rate when it was measured on this driver: NVIDIA 621 compiles at a fraction of 617's.</summary>
    double? RateOn(GameRecord r, string? driverId) => driverId != null && (r.PsoPerSecondDriver ?? r.WarmedDriverId) == RateDriver(driverId, SkipD3DSCacheFlush()) ? r.PsoPerSecond : null;

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

    /// <summary>Keeps the last cold warm's rate on a driver as this vendor's rate there (the warms keep their own per game).</summary>
    void SeedWarmRate(double psoPerSecond, string? driverId, bool noflush)
    {
        if (driverId == null) return;
        var rates = Store.LoadWarmRates();
        rates[RateKey(driverId, noflush)] = psoPerSecond;
        Store.SaveWarmRates(rates);
        _warmRates = rates;
    }

    // SCSKILLER_TEST_EVAL_DELAY=<game id>=<seconds>,...: a read that hangs that long, before the engine detection (E2E)
    static readonly Dictionary<string, int> TestDelay = (Environment.GetEnvironmentVariable("SCSKILLER_TEST_EVAL_DELAY") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=')).Where(p => p.Length == 2 && int.TryParse(p[1], out _))
        .DistinctBy(p => p[0]).ToDictionary(p => p[0], p => int.Parse(p[1]));

    /// <summary>The expensive part of a game's state, cached in scan.json per game. A cached "no anti-cheat" is checked
    /// again each time where an update puts one (the install root's and exe folder's own entries, names only), and for a
    /// game that has the recorder or may get it (<see cref="RecorderMayGoIn"/>) fully once those entries changed
    /// (<see cref="FullCheckOnChange"/>). A cache from another SCSKiller build is used as it is and the game detected again
    /// in the background (<see cref="RedetectInBackground"/>).</summary>
    Evaluation Evaluated(Game g, GameRecord rec, bool force, out bool fresh, CancellationToken abandoned = default)
    {
        abandoned.ThrowIfCancellationRequested();
        var key = string.Join('|', ExeStamp(g), GameFiles.DirKey(g.InstallDir), g.Version, Vendor.Caps.Profile, rec.RecordingImportedAt?.UtcTicks, CoreBuild, CommunityObjects(g.Id))
            + (OfficialInUse(g.Id) is { } official ? "|official:" + official.Object : "")
            + (rec.ApiOverride is { } api ? "|api:" + api : "")   // absent when Auto: the keys of games without a choice stay as they were
            + (rec.ChosenExe != null || GameExes.Current.Exe(g, () => ScanEngine(g.Id)) != null ? "|exe:" + g.ExePath : "");
        Evaluation? hit = null;
        long started;
        abandoned.ThrowIfCancellationRequested();
        lock (_scanLock)
        {
            _scan ??= Store.LoadScan();
            _scan.TryGetValue(g.Id, out hit);
            started = ++_evaluations;
        }
        // read before Detect: what changes while it runs is seen by the next scan
        key += DetectStamp(g, hit?.Engine) + PsoDiskCacheStamp(g, hit?.Engine) + (File.Exists(PsoCacheUnusedPath(g.Id)) ? "|psocache-unused" : "");   // the marker a compile writes flips readiness
        SteamStamp(g, hit?.Engine, ref key);
        var stamped = key + RulesStamp(hit?.Engine);
        fresh = force || hit == null || hit.Key != stamped;
        if (fresh && !force && hit != null && (OnlyBuildDiffers(hit.Key, stamped) || OnlyBuildDiffers(hit.Key, key)))   // key: a cache without the rules' stamp (1.2's)
        {
            abandoned.ThrowIfCancellationRequested();
            _redetect[g.Id] = g;
            fresh = false;
        }
        if (!fresh && hit!.AntiCheat != AntiCheat.None && VerdictMoved(g, hit)) fresh = true;
        if (!fresh && hit!.Vac && !VacCheck(g)) fresh = true;   // a VAC only assumed (Steam's data unread) left a stale detection
        if (!fresh)
        {
            if (hit!.AntiCheat != AntiCheat.None) TakeOutNow(g, $"{hit.AntiCheat} found");
            else if (_unknown.ContainsKey(g.Id)) return hit with { AntiCheat = AntiCheat.Other };   // walked again by the pass, not by every evaluation
            else
            {
                var quickLate = false;
                if ((GameFiles.DetectAntiCheat(g, quick: true, late: () => quickLate = true) is var near and not AntiCheat.None ? near
                        : RecorderMayGoIn(g, rec, hit.Engine, Vendor.Caps) ? FullCheckOnChange(g) : AntiCheat.None) is not AntiCheat.None and var found)
                {
                    AntiCheatFound(g, found ?? AntiCheat.Other, unfinished: found == null || quickLate);
                    return hit with { AntiCheat = found ?? AntiCheat.Other };
                }
            }
            if (Streamline(g, hit) is var now && !ReferenceEquals(now, hit) && !abandoned.IsCancellationRequested)   // a DLL it depends on changed, not the exe
                lock (_scanLock)
                {
                    if (_scan.TryGetValue(g.Id, out var cached) && ReferenceEquals(cached, hit)) _scan[g.Id] = now;
                    Store.SaveScan(_scan);
                }
            return now;
        }
        var (gen, folders) = (RemovalGen(g.Id), FolderStamp(g));   // before Detect and the check, as the key
        EngineInfo? engine = null;
        string? detected = null;
        PlanCheck check;
        try
        {
            if (TestDelay.GetValueOrDefault(g.Id) is > 0 and var wait) Thread.Sleep(TimeSpan.FromSeconds(wait));
            engine = _reader.Detect(g);
            if (engine != null) (engine, detected) = WithApiChoice(g, engine, rec.ApiOverride);
            abandoned.ThrowIfCancellationRequested();
            if (rec is { Plan: not null, PlanApi: null } && hit?.Engine is { } before && ApiKey(before.GraphicsApi) != ApiKey(engine?.GraphicsApi))
            {
                rec.PlanApi = ApiKey(before.GraphicsApi);   // a plan from before PlanApi, built for the API the cache had: stale now
                Store.SaveGame(g.Id, rec);
            }
            if (rec is { Plan: not null, PlanEngine: null } && hit?.Engine is { } was && engine != null && EngineKey(was) != EngineKey(engine))
            {
                abandoned.ThrowIfCancellationRequested();
                rec.PlanEngine = EngineKey(was);   // a plan from before PlanEngine, built for the engine the cache had: stale now
                Store.SaveGame(g.Id, rec);
            }
            check = engine == null ? new(Readiness.Unsupported, Directory.Exists(g.InstallDir) ? "engine not supported yet" : ReadFailed)
                : CheckRecordings(g, engine);
        }
        catch (Exception e) when (!abandoned.IsCancellationRequested)
        {
            Log?.Report($"{g.Name}: checking it failed: {e}");
            check = new(Readiness.Unsupported, Codecs.Reason(e) ?? (engine == null ? ReadFailed : e.Message));
            if (IoErrors.Busy(e))
            {
                key = "";   // kept only until the next evaluation reads the files again
                // the game or its launcher holds its files: the last answer stands until a read succeeds
                if (hit?.Engine != null) (engine, check, detected) = (hit.Engine, hit.Check, hit.ApiDetected);
            }
        }
        string? foundAt = null;
        var late = false;
        var ev = Streamline(g, new Evaluation(key + RulesStamp(engine), engine, GameFiles.DetectAntiCheat(g, at: p => foundAt = p, late: () => late = true), check, ApiDetected: detected) { Vac = VacCheck(g) });   // last: anti-cheat that appeared during Detect counts
        if (ev.AntiCheat != AntiCheat.None) AntiCheatFound(g, ev.AntiCheat, ev with { Found = late ? null : folders, FoundAt = foundAt }, unfinished: late);   // late: checked in full again at the next evaluation
        else
        {
            EvaluateStep?.Invoke("checked");
            abandoned.ThrowIfCancellationRequested();
            lock (_scanLock)
                if (_scanStarted.GetValueOrDefault(g.Id) < started)   // older than the cached verdict or an anti-cheat finding: kept out
                {
                    _verdicts.TryRemove(g.Id, out _);   // a full scan that started after any finding found none: clean again
                    _unknown.TryRemove(g.Id, out _);
                    _scanStarted[g.Id] = started;
                    _scan[g.Id] = ev with { Clean = RemovalGen(g.Id) == gen ? folders : null };   // a finding meanwhile: walked again
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
        var first = GameFiles.StreamlineFirst(g.ExePath, GameFiles.SystemFolder(g), read) == true;
        return ev with { StreamlineFirst = first, StreamlineFiles = read, StreamlineStamp = GameFiles.Stamp(read) };
    }

    /// <summary>A cached anti-cheat verdict no longer rests on what it was found in: the folders' <see cref="FolderStamp"/>
    /// changed (or was never kept: an earlier build's verdict, or one found outside a scan's evaluation, which knows no
    /// marker path), or the marker it found is gone. The
    /// game is evaluated again, its full check included, so a marker still anywhere in it keeps the verdict. Folders that
    /// can't be listed keep it too.</summary>
    static bool VerdictMoved(Game g, Evaluation hit) =>
        FolderStamp(g) is { } now && (now != hit.Found || hit.FoundAt is { } at && !Path.Exists(at));

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

    /// <summary>The reason a game shows when detecting its engine threw (the exception goes to the log).</summary>
    public const string ReadFailed = "couldn't read the game files";

    /// <summary>Detects again, one game at a time at background priority, the games a scan listed from another build's cache.</summary>
    void RedetectInBackground()
    {
        lock (_scanLock)
            if (!_redetect.IsEmpty && _redetecting.IsCompleted)
            {
                _redetecting = Task.Run(() => LowIo(() =>
                {
                    foreach (var id in _redetect.Keys)
                        if (_redetect.TryRemove(id, out var g) && !UnlistedNow(id))
                        {
                            ScanProgressed?.Invoke();   // AfterUpdateLine's count
                            try { Refresh(Games.FirstOrDefault(s => s.Game.Id == id)?.Game ?? g, force: true); }
                            catch (Exception e) { Log?.Report($"{g.Name}: detecting it again failed: {e.Message}"); }
                        }
                    ReleaseMemory();
                    SaveDllReads();
                    return 0;
                }));
                _ = _redetecting.ContinueWith(_ => ScanProgressed?.Invoke(), TaskScheduler.Default);   // its line goes
            }
    }

    /// <summary>The store's build, the anti-cheat markers (a build that knows more walks the game again), the exes Steam's launch
    /// options start (<see cref="Vac.LaunchExes"/>, which Steam updates without touching the install) and the own entries of
    /// the install root and the exe's folder (files by name, size and write time, the exe among them; folders by name),
    /// hashed: what a game update or an anti-cheat install changes, read without walking the tree. The recorder's files and
    /// data files (<see cref="DataFile"/>) are left out: they change as the game runs. Null when a folder or Steam's data can't be read.</summary>
    internal static string? FolderStamp(Game g)
    {
        string launches;
        try { launches = string.Join('|', Vac.LaunchExes(g)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
        return Stamp(g.Version + "|" + GameFiles.MarkersStamp + "|" + launches, GameFiles.DirKey(g.InstallDir), GameFiles.DirKey(Path.GetDirectoryName(g.ExePath)!));
    }

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
    AntiCheat? FullCheckOnChange(Game g) => CleanSince(g, FolderStamp(g)) ? AntiCheat.None : FullCheck(g);

    /// <summary><see cref="FullAntiCheatCheck"/>; none found: the stamp read before it is kept as the game's clean one. Null:
    /// it didn't finish in time.</summary>
    AntiCheat? FullCheck(Game g)
    {
        var (gen, folders) = (RemovalGen(g.Id), FolderStamp(g));
        var found = FullAntiCheatCheck(g);
        if (found != AntiCheat.None || folders == null) return found;
        lock (_scanLock)
            if (_scan?.GetValueOrDefault(g.Id) is { AntiCheat: AntiCheat.None } ev && !_verdicts.ContainsKey(g.Id) && ev.Clean != folders
                && RemovalGen(g.Id) == gen)   // a finding during the walk: not known clean
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
        var (on, was) = (BackgroundBegin(), _lowIo);
        _lowIo = true;
        try { return f(); }
        finally
        {
            _lowIo = was;
            if (on) BackgroundEnd();
        }
    }
    [ThreadStatic] static bool _lowIo;   // this thread runs in LowIo: Capped's thread runs so too

    /// <summary>Runs <paramref name="f"/> on the <see cref="BelowNormalScheduler"/> of <paramref name="threads"/>, so the readers'
    /// Parallel loops (<see cref="ReaderLoops"/>) are below normal and that wide too: on the pool, a big game's index takes every
    /// core at normal priority and a low-end PC stops responding. <paramref name="lowIo"/>: also low I/O priority, which made
    /// a user's materialize up to 4x slower while other I/O ran. The caller continues on the pool, not on that thread.</summary>
    static Task<T> Lowered<T>(int threads, bool lowIo, Func<T> f, bool capped = false) =>
        Task.Factory.StartNew(() =>
        {
            PlanBudget.Background.Value = capped;
            return f();
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach | TaskCreationOptions.RunContinuationsAsynchronously, BelowNormalScheduler.For(threads, lowIo));

    /// <summary>This thread in background mode, low I/O priority (and on Windows memory priority); false when it already was.</summary>
    private static partial bool BackgroundBegin();

    private static partial void BackgroundEnd();

    string IndexStamp(Game g, EngineInfo? engine)
    {
        try { return _reader.IndexStamp(g, engine); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "?"; }
    }

    // the readiness check depends on the rules matching the engine
    static string RulesStamp(EngineInfo? e) => e == null ? "" : "|" + RulesFile.Current.Hash(e);

    string DetectStamp(Game g, EngineInfo? engine)
    {
        try { return "|" + _reader.DetectStamp(g, engine); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "|?"; }
    }

    /// <summary>The recorder is in (or on its way out of) the game, by its record or on disk, or the engine allows it
    /// (<see cref="RecorderSkip"/>): its retention and eligibility need the full anti-cheat check.</summary>
    static bool RecorderMayGoIn(Game g, GameRecord rec, EngineInfo? engine, VendorCaps caps) =>
        rec.RecorderFiles.Count > 0 || rec.RecorderChained != null || rec.RecorderExe != null || rec.RecorderMoveFrom != null
        || engine is { Unsupported: null } && engine.GraphicsApi.Contains("D3D12") || Records11(engine, caps) || Frames11(engine, caps) || Records9(engine) || ProxyOnDisk(g) || ProxyOnDisk(RecordedAt(g, rec));

    /// <summary>Our proxy next to the exe (either name); a proxy dll that can't be read counts as ours.</summary>
    static bool ProxyOnDisk(Game g) => ProxyOnDisk(Path.GetDirectoryName(g.ExePath)!);

    /// <summary>Our proxy or its preloader in <paramref name="dir"/> under any of their names; a dll that can't be read counts as ours.</summary>
    static bool ProxyOnDisk(string dir)
    {
        try { return OurProxyIn(dir) || OurPreloadIn(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <param name="pending"><see cref="PendingOf"/>, when the caller has it</param>
    /// <param name="gpu">the evaluation's snapshot, so its reason and driver verdict agree; null = now</param>
    /// <param name="status">for the game's status: new pipelines under <see cref="NewShaders.Minimum"/> keep it Warmed (a
    /// compile still takes them)</param>
    string? StaleReason(Game g, GameRecord r, EngineInfo? e, Pending? pending = null, GpuSnapshot? gpu = null, bool status = false)
    {
        var p = pending ?? PendingOf(g, r, e);
        return WarmChanged(g, r, e, gpu)
            ?? (p.Unknown ? (p.Recorded + (p.Planned ?? 0) is > 0 and var u ? $"{u:N0} pipeline{(u == 1 ? "" : "s")} to compile again" : "compile again")
                + ": what the last compile replayed is no longer known" : null)
            ?? (!CurrentPlanner(r.WarmedPlanVersion, r.WarmedPlanRules, r.WarmedReaderVersion, e) ? PlannerChanged(r, p.Planned, e)
                : Maximum(r) && r.WarmedPerStage ? "Maximum mode: every shader pairing is still to compile"   // Maximum -> standard: nothing new
                : RayTracing(r) && !r.WarmedRayTracing && r.Plan?.Stats.RtLibraries > 0 ? RtToCompileReason : null)   // on -> off: nothing new
            ?? (p.Recorded + (CurrentPlanner(r.WarmedPlanVersion, r.WarmedPlanRules, r.WarmedReaderVersion, e) ? p.Planned ?? 0 : 0) is > 0 and var n && (!status || n >= (NewMinimum ?? NewShaders.Minimum(r.Plan?.Stats)))
                ? $"{n:N0} new {NewKind(n, p)}. " + (status && Replays(g, r, gpu ?? Snapshot()) is { } all ? RedoesNote + all : "Compile again to include them") : null)
            ?? BudgetGrewReason(g.Id, r);
    }

    /// <summary>Why a plan that <see cref="BudgetGrew"/> is stale until a compile rebuilds it: the PSOs the room the cache
    /// gained holds, at most the ones the plan left out.</summary>
    string? BudgetGrewReason(string id, GameRecord r) =>
        BudgetGrew(id, r) && r.Plan?.Stats.CacheBudgetLeftOut is { } left && PlanCaps(id, r) is var caps && (caps.WarmCacheBudget ?? VendorBudget(caps)) is var now
        && PlanBuilder.PsoBytesOf(caps) is var pso && pso + (ChargeDropped(r) ? D3DSCachePsoBytes : 0) is var was
        && Math.Min(left, Math.Max(1, now / pso - (r.PlanCacheBudget ?? Vendor.Caps.WarmCacheBudget ?? now) / was)) is var more
            ? $"the game's cache has room for up to {more:N0} more pipeline{(more == 1 ? "" : "s")}. Compile again to include them" : null;

    /// <summary>The status's new pipelines are compiled with more (<see cref="FullReplayReason"/>, D3D11 items:
    /// <see cref="CompiledItems"/>); null: only the new ones. Past <see cref="WarmChanged"/>, which StaleReason already ruled out.</summary>
    string? Replays(Game g, GameRecord r, GpuSnapshot snap)
    {
        if (r.Plan?.Stats is not { } p || PlanItems(p) is not > 0) return null;
        var d3d11 = p.D3D11Shaders > 0 ? $"its {p.D3D11Shaders:N0} DirectX 11 shaders" : null;
        if (d3d11 != null && !BothApis(p)) return d3d11;
        return !File.Exists(Path.Combine(Store.GameDir(g.Id), CompiledItems.File)) || ColdWarm(r, snap.Id) || ReplayAgain(r) != null
            || CacheFilesChangedNow(g.Id, r, snap) ? "the whole game" : d3d11;
    }

    internal const string RedoesNote = "Compiling again redoes ";

    // cache files changed without a record change show at the next pending count, driver or warm
    bool CacheFilesChangedNow(string gameId, GameRecord r, GpuSnapshot snap)
    {
        if (r.Pending?.Key is not { } pk) return CacheFilesChanged(gameId, r) != null;
        var key = $"{pk}|{snap.Id}|{r.WarmedAt?.UtcTicks}";
        if (_cacheFilesChanged.TryGetValue(gameId, out var c) && c.Key == key) return c.Changed;
        var changed = CacheFilesChanged(gameId, r) != null;
        _cacheFilesChanged[gameId] = (key, changed);
        return changed;
    }

    readonly ConcurrentDictionary<string, (string Key, bool Changed)> _cacheFilesChanged = new();

    /// <summary>What <paramref name="n"/> new pipelines are: all recorded, all from upscaler packs, or a mix.</summary>
    static string NewKind(long n, Pending p) => (n == 1 ? "pipeline" : "pipelines") is var w && n == p.Upscaler ? "upscaler " + w
        : n == p.Recorded - p.Upscaler ? w + " recorded" : p.Upscaler > 0 ? $"{w}, {p.Upscaler:N0} from upscalers" : w;

    /// <summary>A newer planner or engine reader than the warmed plan's, or other rules (<see cref="CurrentPlanner"/>). Its rebuild (RunItem)
    /// counts the pipelines the warm didn't compile; none marks the warm current then, so a count here is more than 0. Not
    /// rebuilt yet: nothing until its plan check tells. Rebuilt but not countable (no readable warmed plan): it may compile more.</summary>
    string? PlannerChanged(GameRecord r, long? planned, EngineInfo? e) =>
        !CurrentPlanner(r.PlanVersion, r.PlanRules, r.PlanReaderVersion, e) ? (r.PlanCheckFailed == Planner.Version ? PlanCheckFailedReason : null)
        : planned is not { } n ? "SCSKiller can now compile more of this game"
        : n > 0 ? n * 2 > PlanItems(r.Plan?.Stats) ? $"{PlanReplacedReason} ({n:N0} pipeline{(n == 1 ? "" : "s")})"
            : $"SCSKiller can now compile {n:N0} more pipeline{(n == 1 ? "" : "s")} for this game" : null;

    /// <summary>A rebuilt plan of which more than half is new to the last compile: it replaces that compile rather than adds to it.</summary>
    public const string PlanReplacedReason = "The plan changed, compile again";

    /// <summary>Why the warm no longer matches what the game uses, whatever the plan: driver, exe name, game build, shaders.
    /// Shaders that read otherwise only because the engine reader changed since the warm (<see cref="Reread"/>) are the
    /// planner's change, not the game's.</summary>
    string? WarmChanged(Game g, GameRecord r, EngineInfo? e, GpuSnapshot? snapshot = null) =>
        (snapshot ?? Snapshot()) is var gpu && !CurrentDriver(gpu, r.WarmedDriverId, r.WarmedDriverVersion) ? $"driver changed: {r.WarmedDriverVersion} -> {gpu.Gpu.DriverVersion}"
        : TrimmedReason(g.Id, r) is { } trimmed ? trimmed
        : WarmMissesGame(r, HeldOnDriver(r, snapshot ?? Snapshot())) ? MissesGameReason
        : r.AgsMissed && r.WarmedAgsApp != null ? AgsMissedReason(r)
        : r.WarmedAt != null && AgsFor(g, r, recover: true)?.App is { } ags && ags != r.WarmedAgsApp ? MissesGameReason
        : AftermathChanged(g, r, e) ? (r.WarmedAftermath ? (r.AftermathLost ? AftermathLostReason : "the last compile started NVIDIA Aftermath, the game no longer does") : AftermathOf(g, r, e).Retry ? AftermathRetryReason : AftermathReason)
        : LayerNow(g) is var layer && (layer == null ? r.WarmedLayer != null : !layer.Matches(r.WarmedLayer)) ? (layer == null ? "the HDR mod the last compile ran through is gone" : r.WarmedLayer == null ? NotThroughLayerReason : "the HDR mod changed since the compile")
        : r.WarmedExeName is { } other && !other.Equals(Path.GetFileName(g.ExePath), StringComparison.OrdinalIgnoreCase)
            ? $"the last compile filled the cache of {other}, but the game runs {Path.GetFileName(g.ExePath)}"
        : WarmExeName(g, r) is var runs && (r.WarmedExeName ?? Path.GetFileName(g.ExePath)) is var warmed && runs != warmed && CaseMatters(g, r, warmed)
            ? $"the game runs as {runs}, the warm filled the cache of {warmed} (the driver keys it on the exe name's exact case)"
        : !Reread(r, e) && IndexStamp(g, e) is var stamp && stamp != (r.WarmedIndexStamp ?? "")
            ? (stamp + r.WarmedIndexStamp).Contains(CarvedReader.LumaTag, StringComparison.Ordinal) ? LumaChangedReason : IndexChangedReason
        : g.Version != null && r.WarmedGameVersion != null ? (g.Version != r.WarmedGameVersion ? $"game updated since the warm (build {r.WarmedGameVersion} -> {g.Version})" : IndexChanged(r, e))
        : r.WarmedExeStamp != ExeStamp(g) ? "game updated since the warm"   // no store version (older state, other stores): the exe
        : IndexChanged(r, e);

    /// <summary>The warm read the game with another version of its engine reader than the one in use (<see cref="IEngineReader.ReaderVersion"/>).</summary>
    bool Reread(GameRecord r, EngineInfo? e) => e != null && (r.WarmedReaderVersion ?? 1) != _reader.ReaderVersion(e);

    /// <summary>Warmed and not otherwise stale, with a plan an older planner or other rules than the game gets now built (the
    /// warm's rules back in use over a plan of others too: it counts nothing until rebuilt, <see cref="PendingOf"/>), or
    /// without the community or official recording in use (<see cref="CheckPlans"/>); or a recording waits for the plan to
    /// tell its ray tracing (<see cref="RtPlanCheck"/>).</summary>
    bool NeedsPlanCheck(Game g, GameRecord r, EngineInfo? e) => r.PlanCheckFailed != Planner.Version && (
        r.WarmedAt != null && (!CurrentPlanner(r.PlanVersion, r.PlanRules, r.PlanReaderVersion, e) || r.PlanCommunity != CommunityObjects(g.Id)
            || r.PlanOfficial != OfficialInUse(g.Id)?.Object || r.PlanSteam != SteamDownloads(g, e) || r.PlanPsoCache != PsoCacheInUse(g, e) || OtherApi(r, e) || OtherEngine(r, e) || r.PlanFiltering != Filtering(g.Id, false, e)
            || r.PlanRayTracing != RayTracing(r) && r.Plan?.Stats is { RtLibraries: > 0 } or { RtStateObjects: > 0 }
            || r.PlanMiddleware?.Contains("|shared:") == true && !UsesShared(g.Id) || RuleFallbackDue(r)) && WarmChanged(g, r, e) == null
        || RtPlanCheck(r, e));

    /// <summary>The plan was built for another DirectX than the engine has now: a choice, a recording, or the game's log or
    /// launch options changed it. A plan from before PlanApi was kept counts as current.</summary>
    static bool OtherApi(GameRecord r, EngineInfo? e) => r.PlanApi != null && e != null && r.PlanApi != ApiKey(e.GraphicsApi);

    /// <summary>The plan was built for another engine version or fork than the game has now (a game update, or a detection
    /// that reads it otherwise). A plan from before PlanEngine was kept counts as current.</summary>
    static bool OtherEngine(GameRecord r, EngineInfo? e) => r.PlanEngine != null && e != null && r.PlanEngine != EngineKey(e);

    static string EngineKey(EngineInfo e) => $"{e.Family}|{e.Version}|{e.Fork}";

    /// <summary>Warmed, with a plan an older planner built (an app update) and not otherwise stale: its plan check doesn't
    /// wait for idle (<see cref="CheckPlan"/>).</summary>
    bool OlderPlanner(Game g, GameRecord r, EngineInfo? e) => r.WarmedAt != null && r.WarmedPlanVersion != Planner.Version && r.PlanVersion != Planner.Version && WarmChanged(g, r, e) == null;

    /// <summary>A plan of <paramref name="version"/> built with the rules of hash <paramref name="rules"/> from an index of
    /// engine reader version <paramref name="reader"/> is what the planner builds for the engine now (<see cref="Planner.Version"/>,
    /// <see cref="RulesFile.Hash"/>, <see cref="IEngineReader.ReaderVersion"/>). A record without a hash has the embedded rules,
    /// without a reader version version 1; without an engine only the planner's version counts.</summary>
    bool CurrentPlanner(int version, string? rules, int? reader, EngineInfo? e) =>
        version == Planner.Version && (e == null || (rules ?? RulesFile.Embedded.Hash(e)) == RulesFile.Current.Hash(e) && (reader ?? 1) == _reader.ReaderVersion(e));

    /// <summary>Why a "when idle" warm doesn't start: the plan's rules aren't the last warm's, and it adds more pipelines than
    /// that warm replayed. The user gets them as the usual offer of more pipelines. Null: it may start (also when the warm's
    /// baseline isn't known: StaleReason already says so).</summary>
    string? RulesGrew(Game g, GameRecord r, EngineInfo e)
    {
        var embedded = RulesFile.Embedded.Hash(e);
        if ((r.PlanRules ?? embedded) == (r.WarmedPlanRules ?? embedded) || r.WarmKeysFile is not { } wf
            || KeyFiles.Set(Path.Combine(Store.GameDir(g.Id), wf)) is not { } warmed || PendingOf(g, r, e).Planned is not { } n || n <= warmed.Count) return null;
        return $"SCSKiller can now compile {n:N0} more pipeline{(n == 1 ? "" : "s")} for this game: more than its last compile, so not while idle";
    }

    public const string PlanCheckFailedReason = "SCSKiller couldn't update this game's plan in the background. A compile will update it";

    /// <summary>A failed background job's mark in the game's record (<see cref="GameRecord.PlanCheckFailed"/>,
    /// <see cref="GameRecord.IndexFailedStamp"/>), so a restart doesn't run it again.</summary>
    void SaveFailed(string id, Action<GameRecord> mark)
    {
        try
        {
            AppStore.Locked(Path.Combine(Store.GameDir(id), "state.json"), () =>
            {
                var r = Store.LoadGame(id);
                mark(r);
                Store.SaveGame(id, r);
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{id}: couldn't save the failed background job: {e.Message}"); }
    }

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

    /// <summary>What the queue's Clear queue (<paramref name="only"/> null) or Remove selected removes: the listed items but
    /// the running one, which only its own Stop and remove stops.</summary>
    public static IReadOnlyList<string> BulkRemovable(IEnumerable<QueueItem> queue, IReadOnlySet<string>? only = null) =>
        queue.Where(q => !q.PlanCheck && !Format.Running(q) && (only == null || only.Contains(q.GameId))).Select(q => q.GameId).ToList();

    /// <summary>The keys (sha1 of tag + payload) of what plan.bin has the warm create: every record but the 'B' root
    /// signature blobs, which compile nothing by themselves (an item's key covers its root signature's hash). Null when it
    /// isn't a readable plan.</summary>
    static HashSet<string>? PlanKeys(string path)
    {
        try { return PlanFile.Read(path).Records.Where(r => r.Tag != 'B').Select(r => r.Key).ToHashSet(); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>A hash of the work folder's records in file order but the shader blobs: the warm numbers its items in that
    /// order, so a stopped warm's resume point holds only for the same one. Null when a file isn't readable or doesn't end
    /// at a record's end.</summary>
    static string? WorkOrder(IEnumerable<string> dbs)
    {
        try
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var (head, skip) = (new byte[5], new byte[1 << 16]);
            foreach (var db in dbs)
            {
                h.AppendData("|"u8);   // which file an item is in counts too: careful passes take the recorded ones apart
                if (!File.Exists(db)) continue;
                // blobs are read through, not sought past: a seek per blob measured 6-45 times slower from a cold cache
                using var f = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                for (var end = f.Length; f.Position < end;)
                {
                    if (f.ReadAtLeast(head, 5, false) < 5) return null;
                    var len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(1));
                    if (head[0] == 0 || len > end - f.Position || (head[0] == 'B' ? len < 20 : len > 1 << 30)) return null;
                    if (head[0] == 'B')
                    {
                        for (long left = len; left > 0; left -= f.ReadAtLeast(skip.AsSpan(0, (int)Math.Min(skip.Length, left)), 1)) { }
                        continue;
                    }
                    var rec = new byte[len + 1];   // PsoDb.Rec.Key: sha1 of tag + payload
                    rec[0] = head[0];
                    f.ReadExactly(rec, 1, (int)len);
                    h.AppendData(System.Text.Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA1.HashData(rec))));
                }
            }
            return Convert.ToHexStringLower(h.GetHashAndReset());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    (string Key, string Order)? _workOrder;   // the last WorkOrder read, by its files' paths, lengths and write times

    /// <summary><see cref="WorkOrder"/>, read again only when a work db's length or write time changed: every writer of
    /// them (materialize, the leave-out) writes a new file, which takes seconds. A null isn't kept.</summary>
    string? CachedWorkOrder(string[] dbs, string name)
    {
        var key = string.Join('|', dbs.Select(d => new FileInfo(d) is { Exists: true } f ? $"{f.FullName}:{f.Length}:{f.LastWriteTimeUtc.Ticks}" : $"{Path.GetFullPath(d)}:-"));
        if (_workOrder is { } kept && kept.Key == key) return kept.Order;
        var clock = Stopwatch.StartNew();
        var order = WorkOrder(dbs);
        Log?.Report($"{name}: the work folder read for the resume check in {clock.Elapsed.TotalSeconds:0.0} s{(order == null ? ", not readable" : "")}");
        _workOrder = order == null ? null : (key, order);
        return order;
    }

    /// <summary>What a plan compiles, independent of record order and of its header (paths, times): a hash of its sorted
    /// <see cref="PlanKeys"/>.</summary>
    static string PlanFingerprint(IEnumerable<string> keys) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(string.Concat(keys.Order(StringComparer.Ordinal)))));

    /// <summary>The index's shader maps, independent of their order. The ContentHash leaves some out (a shipped pipeline
    /// cache's, an inline shader's bytes) because the community database finds a build by it; a plan reads them all.</summary>
    static string MapsFingerprint(ShaderIndex index) =>
        PlanFingerprint(index.Maps.Select(m => Convert.ToHexStringLower(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{m.Hash}|{m.Library}|{m.Platform}|{m.IsPipeline}|{string.Join(',', m.Shaders)}{(m.RootSignature is { } rs ? "|" + rs : "")}{(m.Layout is { } l ? "|" + Convert.ToHexStringLower(PsoDb.LayoutBytes(l)) : "")}")))));

    string? IndexChanged(GameRecord r, EngineInfo? e) => !Reread(r, e) && r.IndexContentHash != r.WarmedIndexHash ? IndexChangedReason : null;

    const string IndexChangedReason = "game shaders changed since the warm";
    const string LumaChangedReason = "Luma was installed, changed or removed since the last compile";

    /// <summary>A warm without a layer (also every warm from before compiles ran through one) and a layer now.</summary>
    internal const string NotThroughLayerReason = "the last compile didn't run through the game's HDR mod";

    /// <summary>NVIDIA's DXCache key ignores the exe name's case (measured: same files in upper case); AMD's DxcCache key is
    /// case-sensitive (FNV-1a of the UTF-16 name as launched). Other vendors: unmeasured, assume it matters.</summary>
    bool CaseSensitiveCache => OwnDriver != GpuVendor.Nvidia;

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

    /// <summary>On AMD, takes the AGS registration the recorder saw the game's process make into the record: its compiles
    /// register the same (<see cref="AgsFor"/>). True if changed.</summary>
    bool MergeAgsSeen(Game g, GameRecord rec, AgsRegistration? seen)
    {
        if (OwnDriver != GpuVendor.Amd || seen == null || (seen.App, seen.Engine, seen.AppVersion, seen.EngineVersion)
            == (rec.AgsSeenApp, rec.AgsSeenEngine, rec.AgsSeenAppVersion ?? AmdAgs.UnspecifiedVersion, rec.AgsSeenEngineVersion ?? AmdAgs.UnspecifiedVersion)) return false;
        (rec.AgsSeenApp, rec.AgsSeenEngine, rec.AgsSeenAppVersion, rec.AgsSeenEngineVersion) = (seen.App, seen.Engine, seen.AppVersion, seen.EngineVersion);
        Log?.Report($"{g.Name}: the game registered the AGS app name {seen.App} (engine {seen.Engine}), so its compiles register the same "
            + "unless the game is seen holding its exe name's key");
        return true;
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
            if (!running.Contains(exe) || s.AntiCheat != AntiCheat.None || IsUnchecked(s)) continue;   // nothing near an anti-cheat game while it runs
            var rec = Store.LoadGame(s.Game.Id);
            if (!LearnKeys(s.Game, rec, WarmExeName(s.Game, rec))) continue;
            Store.SaveGame(s.Game.Id, rec);
            learned.Add(s.Game.Id);
        }
        return learned;
    }

    /// <summary>Adds the keys the game's own process (named <paramref name="exe"/>) holds open to the game's record (not
    /// saved); returns the new ones and logs when the D3D12 key isn't the name's hash (an AMD app profile).</summary>
    bool LearnKeys(Game g, GameRecord rec, string exe) => LearnKeys(g, rec, exe, OpenKeys(g, exe));

    /// <summary>Returns whether the record changed (new keys, or other D3D12 keys held than at the last sighting).</summary>
    bool LearnKeys(Game g, GameRecord rec, string exe, IReadOnlySet<string> open)
    {
        var added = open.Where(k => !rec.GameKeys.Contains(k)).ToHashSet();
        NoteProfile(g, exe, added.Where(k => !rec.CacheKeys.Contains(k)).ToList());
        rec.CacheKeys.UnionWith(added);
        rec.GameKeys.UnionWith(added);
        var stamped = StampDriver(rec, open);
        var held = string.Join(',', open.Where(k => k.StartsWith(AmdAppCache.D3D12Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        var moved = held.Length > 0 && held != rec.HeldKeys;
        if (moved) rec.HeldKeys = held;
        return added.Count > 0 || moved || stamped;
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
                if (LearnKeys(g, rec, exe, open)) { Store.SaveGame(g.Id, rec); Refresh(g); }
                OldCacheWhilePlaying(g);
                return;
            }
            await Task.Delay(AttributionInterval);
        }
    }

    /// <summary>The game's own keys are known and the last complete warm filled none of them: the warm went to another cache.
    /// Only the warm's keys of a kind the game holds count (an AMD D3D11 game's dx: key, never the warm's D3D12 device's dxc:),
    /// so a warm with none of them, or no complete warm on record, is unknown, not a miss. <paramref name="gameKeys"/>: the
    /// game's keys held on this driver (<see cref="HeldOnDriver"/>): another driver may key the game otherwise.</summary>
    public static bool WarmMissesGame(GameRecord r, IReadOnlySet<string> gameKeys) =>
        r.WarmedKeys is { } warmed && warmed.Where(k => gameKeys.Any(g => KeyKind(g) == KeyKind(k))).ToHashSet(warmed.Comparer) is { Count: > 0 } same
        && !same.Overlaps(gameKeys);

    /// <summary>The game's learned keys (<see cref="GameRecord.GameKeys"/>) its process last held on the current driver; a key
    /// without a stamp (<see cref="GameRecord.GameKeyDrivers"/>, learned by an earlier build) is of an unknown driver, and so
    /// is the current one without a driver id unless the stamp's version is the one read now.</summary>
    static HashSet<string> HeldOnDriver(GameRecord r, GpuSnapshot gpu) =>
        r.GameKeys.Where(k => r.GameKeyDrivers.GetValueOrDefault(k) is { } d && d.IndexOf('|') is var bar and >= 0
            && (bar > 0 ? d[..bar] : null, bar + 1 < d.Length ? d[(bar + 1)..] : null) is var (id, version)
            && (gpu.Id != null ? CurrentDriver(gpu, id, version) : version != null && version == gpu.Gpu.DriverVersion)).ToHashSet();

    /// <summary>Stamps these keys, held by the game's process now, with the current driver; true if a stamp changed.</summary>
    bool StampDriver(GameRecord rec, IEnumerable<string> keys)
    {
        var gpu = Snapshot();
        var stamp = $"{gpu.Id}|{gpu.Gpu.DriverVersion}";
        var changed = false;
        foreach (var k in keys)
            if (rec.GameKeyDrivers.GetValueOrDefault(k) != stamp) (rec.GameKeyDrivers[k], changed) = (stamp, true);
        return changed;
    }

    static string KeyKind(string key) => key[..(key.IndexOf(':') + 1)];

    /// <summary>The last complete warm missed the game (<see cref="WarmMissesGame"/>) and the next would run the same way: same
    /// driver (kept through Clear cache), exe name and AGS registration. A packaged DirectX game is left out: its warm's
    /// package activation can fail one time and not the next (a Vulkan warm runs without the package).</summary>
    bool CantReach(Game g, GameRecord r, GpuSnapshot gpu, EngineInfo? engine) =>
        WarmMissesGame(r, HeldOnDriver(r, gpu)) && !(Vendor.Caps.PackageKeyed && g.Store == Core.Store.Xbox && !(engine is { } e && VulkanPlan.IsVulkan(e)))
        && (r.WarmedDriverId == null && r.WarmedDriverVersion == null || CurrentDriver(gpu, r.WarmedDriverId, r.WarmedDriverVersion)) && AgsFor(g, r, recover: true)?.App == r.WarmedAgsApp
        && WarmExeName(g, r) == (r.WarmedExeName ?? Path.GetFileName(g.ExePath));

    /// <summary>A warm under an AGS app name whose game's own keys aren't known, and the game's first launch after it still
    /// compiled most of its pipelines (<see cref="IsPartlyWarmed(LaunchCheck?)"/>): taken as a miss, so the next warm is plain.</summary>
    public static bool AgsLaunchMissed(GameRecord r) =>
        r.WarmedAgsApp != null && !r.GameKeys.Any(k => k.StartsWith(AmdAppCache.D3D12Prefix, StringComparison.Ordinal)) && IsPartlyWarmed(r.FirstLaunch);

    static string AgsMissedReason(GameRecord r) =>
        $"the compile didn't reach this game's cache (it registered the AGS app name {r.WarmedAgsApp}"
        + (r.FirstLaunch is { } l ? $", and the first launch still compiled {l.Compiled * 100:0}% of its pipelines" : "")
        + "): the next compile runs without it";

    public const string MissesGameReason = "the compile didn't reach this game's cache: the game uses another driver-cache key";
    public const string CantReachReason = "the compile can't reach this game: it reads its shaders from a different driver cache than the one SCSKiller fills. Clear its cache to free the space. A driver update lets it compile again";
    public const string ThirtyTwoBitReason = "32-bit games aren't supported yet";
    public const string UnreachedReason = "the compile didn't help this game on NVIDIA, it needs a custom loader";
    public const string NoShadersReason = "SCSKiller found no shaders it can compile for this game yet", NothingToCompileReason = "there was nothing to compile: " + NoShadersReason;
    public const string ClearForGameNote ="its compiled cache isn't used, clear it to free the space";

    /// <summary>Hits stay under it: a cache hit under a 70-thread precompile took 5 ms at the median (graphics 0.6 ms). A
    /// warm the game's lookups miss: 42-140 ms.</summary>
    public const double UnreachedMedianMs = 20;

    /// <summary>NVIDIA: the game's first launch after the warm still compiled what the warm compiled
    /// (<see cref="WarmedCreates(Game, GameRecord, LaunchCheck, string, IReadOnlySet{string}?, GpuSnapshot)"/>).</summary>
    public static bool Unreached((int N, double MedianMs) warmed) => warmed.N >= MinJudgedCreates && warmed.MedianMs >= UnreachedMedianMs;

    /// <summary>The verdict is about the warm it judged (<see cref="GameRecord.UnreachedWarm"/>): a later complete warm ends it,
    /// as a later launch that used the compile does (<see cref="GameRecord.UnreachedLifted"/>). Clear cache doesn't.</summary>
    public static bool IsUnreached(GameRecord r) => r.UnreachedWarm is { } judged && (r.WarmedAt == null || r.WarmedAt <= judged) && r.UnreachedLifted != judged;

    /// <summary>The warmed pipelines <paramref name="first"/> created and their median create time; null when the warm
    /// can't be judged: the game is Stale (<see cref="StaleReason"/>: a new driver compiles everything again, the warm
    /// missed the game's key, or there is more to compile), its plan's check for a newer planner hasn't run (it may plan
    /// what the game created), a file of its keys was made after it (a cache reset the game refilled) or when isn't known, or
    /// its inputs aren't known.</summary>
    /// <param name="verdict">false: also while the game is Stale or its plan's check hasn't run, for how much of the warm a launch missed</param>
    (int N, double MedianMs, int Seen, int Cold)? WarmedCreates(Game g, GameRecord rec, LaunchCheck first, string csv, IReadOnlySet<string>? rayQuery, GpuSnapshot gpu,
        bool verdict = true)
    {
        EngineInfo? engine;
        lock (_scanLock) engine = (_scan ??= Store.LoadScan()).GetValueOrDefault(g.Id)?.Engine;   // the kept scan's: Evaluate judges before it reads its own
        if (rec.WarmKeysFile is not { } wf || verdict && (StaleReason(g, rec, engine, gpu: gpu) != null || !CurrentPlanner(rec.WarmedPlanVersion, rec.WarmedPlanRules, rec.WarmedReaderVersion, engine))
            || rec.WarmedAt is not { } at || AppCache?.FilesOf(rec.WarmedKeys ?? rec.CacheKeys).Any(f => !(FileTimes.CreatedUtc(f) <= at.UtcDateTime)) == true) return null;
        try
        {
            if (KeyFiles.Set(Path.Combine(Store.GameDir(g.Id), wf)) is not { } baseline) return null;
            var (inputs, crash) = (InputsNow(g, rec), CrashInputs(CrashKeysNow(rec), rec.Plan?.FilePath));
            var warmed = WarmedRecords(inputs, baseline, crash, rayQuery);
            var cleared = rec.CacheClearedAt?.ToUnixTimeMilliseconds() ?? 0;
            return WarmedCreates(SessionLog.Launches(csv).Where(l => !(l.Start < cleared)), first.At.ToUnixTimeMilliseconds(), warmed, rayQuery,
                rayQuery is { Count: > 0 } ? WarmedRecords(inputs, baseline, crash) : warmed);
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
            bool taken = WarmInputs.Taken(baseline, i, crash.Contains) && !crash.Contains(WarmInputs.Key(i)) && !records.Any(r => crash.Contains(r) || rayQuery?.Contains(r) == true);
            foreach (var r in records) all[r] = all.GetValueOrDefault(r, true) && taken;
        }
        return [.. all.Where(x => x.Value).Select(x => x.Key)];
    }

    /// <summary>Of the launch that started at <paramref name="start"/>: the first create of each warmed pipeline no earlier
    /// launch in the csv created (the game's own cache may hold those), and their median. Library loads, ray tracing state
    /// objects (a cached one takes 6-23 ms) and RayQuery PSOs (their floor reaches 88 ms) are left out.</summary>
    /// <remarks>Seen and Cold: every warmed pipeline the launch created, earlier launches' and every kind but library loads
    /// included, and how many of them took at least <see cref="UnreachedMedianMs"/>: a loss shows in any of them.</remarks>
    /// <param name="all">the warmed pipelines RayQuery's included, for Seen and Cold; null = <paramref name="warmed"/></param>
    internal static (int N, double MedianMs, int Seen, int Cold) WarmedCreates(IEnumerable<SessionLog.CsvLaunch> launches, long start, IReadOnlySet<string> warmed,
        IReadOnlySet<string>? rayQuery, IReadOnlySet<string>? all = null)
    {
        var before = new HashSet<string>();
        foreach (var l in launches)
        {
            if (l.Start == start)
            {
                var ms = l.Creates.Where(c => c.Key != null && char.IsUpper(c.Kind) && c.Kind is not ('R' or 'A')).DistinctBy(c => c.Key)
                    .Where(c => warmed.Contains(c.Key!) && !before.Contains(c.Key!) && rayQuery?.Contains(c.Key!) != true).Select(c => c.Ms).Order().ToList();
                var seen = l.Creates.Where(c => c.Key != null && char.IsUpper(c.Kind) && (all ?? warmed).Contains(c.Key)).GroupBy(c => c.Key).Select(c => c.Max(x => x.Ms)).ToList();
                return (ms.Count, ms.Count > 0 ? ms[ms.Count / 2] : 0, seen.Count, seen.Count(m => m >= UnreachedMedianMs));
            }
            before.UnionWith(l.Creates.Select(c => c.Key).OfType<string>());
        }
        return (0, 0, 0, 0);
    }

    public const string TrimmedPartReason = "part of this game's compiled shader cache is gone, compile again",
        TrimmedAllReason = "this game's compiled shader cache is gone, compile again";

    /// <summary>AMD: a warmed file is gone. NVIDIA and others: every key file is gone, or a warmed file is gone or holds less (<see cref="Shrunk"/>).</summary>
    public static string LinkTargetMissing(string dir) =>
        $"the driver's cache folder {dir} is a link whose target is missing: reconnect that drive, or remove the link so the driver makes the folder again";

    public const string AmdAlwaysOnWarning = "AMD's shader cache is set to Always on, which DirectX 11 compiles don't reach. Set it back to AMD optimized.";

    /// <summary><see cref="AmdAlwaysOnWarning"/> while the AMD adapter's shader cache mode is Always on (<see cref="AmdBackend.CacheAlwaysOn"/>), else null.</summary>
    public static string? AmdAlwaysOnLine(IGpuVendorBackend v) => OperatingSystem.IsWindows() && v is AmdBackend a && AmdBackend.CacheAlwaysOn(a.Gpu.Name) ? AmdAlwaysOnWarning : null;

    /// <summary>A plan with DirectX 11 shaders under <see cref="AmdAlwaysOnLine"/>: its launches miss the compile because of the mode.</summary>
    bool AmdAlwaysOn11(GameRecord r) => r.Plan?.Stats.D3D11Shaders > 0 && AmdAlwaysOnLine(Vendor) != null;

    readonly ConcurrentDictionary<(string Game, DateTimeOffset At), bool> _alwaysOnNoted = new();   // launches left unjudged, logged once each

    public const string CacheOffReason = "the NVIDIA shader cache is turned off, set Shader Cache Size to a size in the NVIDIA Control Panel, then compile";

    /// <summary>Only an explicit Disabled (0) in the base profile: the driver keeps nothing a compile makes. Unreadable never is.</summary>
    public static bool CacheOff(GpuVendor vendor, CacheLimit? limit) => vendor == GpuVendor.Nvidia && limit is { Bytes: 0 };

    string? CacheOffRefusal() => OperatingSystem.IsWindows() && CacheOff(Vendor.Vendor, Vendor.GetCacheLimit()) ? CacheOffReason : null;

    /// <summary>The game's last compile was refused for <see cref="CacheOffReason"/>, while the queue still lists it.</summary>
    public static bool CacheOffRefused(IEnumerable<QueueItem> queue, string gameId) =>
        queue.Any(q => q.GameId == gameId && q is { Stage: QueueStage.Failed, Error: CacheOffReason });

    public const string OverCacheLimitReason = "this game's shaders don't fit the driver's shader cache, raise its size",
        OverGameCapReason = "this game's shaders are more than the driver's shader cache keeps for one game",
        OverCompileFolderReason = "this game's shaders are more than SCSKiller's compile folder holds for one game",
        CacheFullReason = "the driver's shader cache is full, so it removed older games' files";

    /// <summary>Null while compiling again wouldn't keep the files (<see cref="KeptReason"/>).</summary>
    string? TrimmedReason(string gameId, GameRecord r) => Trimmed(gameId, r) is { } t && KeptReason(r) == null ? t : null;

    /// <summary>Why a trimmed cache offers no compile: the game's warmed cache is bigger than the driver's size limit
    /// (<see cref="OverCacheLimit"/>), which prunes it again after every compile, or AMD's cache is full
    /// (<see cref="AmdAppCache.Full"/>): a compile evicts another game's files, which then asks for one. Null: it may.</summary>
    string? KeptReason(GameRecord r) => AppCache is AmdAppCache amd ? OperatingSystem.IsWindows() && amd.Full() ? CacheFullReason : null
        : OverCacheLimit(r) ? Vendor.Caps.PerGameCacheCap == null ? OverCacheLimitReason
            : Vendor.Caps.Profile == NvidiaLinux.Profile ? OverCompileFolderReason : OverGameCapReason : null;

    /// <summary>NVIDIA and others with a size limit: the last warm's growth or its files reached the limit. AMD's plan
    /// stays under its cap (<see cref="PlanBuilder.CacheBudget"/>).</summary>
    bool OverCacheLimit(GameRecord r) => AppCache is not AmdAppCache && WarmedCacheBytes(r) is var size && Vendor.GetCacheLimit() is { Bytes: { } max } && size >= max;

    /// <summary>What the last complete warm left in the cache. With NVIDIA's held bytes recorded, those (a file unread: its
    /// length): a file's length steps up to twice what it holds, and the growth counts lengths. Else the growth or the lengths.</summary>
    internal static long WarmedCacheBytes(GameRecord r) => r is { WarmedFileUsed: { } used, WarmedFileBytes: { Count: > 0 } files }
        ? files.Sum(f => used.TryGetValue(f.Key, out var u) ? u : f.Value)
        : Math.Max(r.LastCacheGrowthBytes ?? 0, r.WarmedFileBytes?.Values.Sum() ?? 0);

    /// <summary>Null while the cache folder is a link whose target is missing: the files are out of reach, not trimmed. On
    /// AMD a file kept aside isn't (<see cref="KeptSame"/>). On Mesa never: Steam's pre-cache replaces and rotates its own
    /// files in the game's folder (<see cref="MesaLinux.SteamsFile"/>).</summary>
    string? Trimmed(string gameId, GameRecord r) => AppCacheFiles.MissingTarget(AppCache) != null ? null : SteamVulkanGone(gameId, r) ?? AppCache switch
    {
        MesaLinuxAppCache => null,
        AmdAppCache amd => r.WarmedFiles is { Count: > 0 } files && amd.Missing(files) is { Count: > 0 } missing && Keeping(gameId) is var kept
            && missing.Where(n => !KeptSame(kept, r, n)).ToList() is { Count: > 0 } gone
            ? gone.Count == files.Count ? TrimmedAllReason : TrimmedPartReason : null,
        { } cache when D3D12Kept(r) is { } st => !DxCacheCounts(r, st) ? D3DSCacheTrimmed(gameId, r)
            : (DxCacheTrimmed(cache, r), D3DSCacheTrimmed(gameId, r)) switch
            {
                (TrimmedAllReason, TrimmedAllReason) => TrimmedAllReason,
                (null, null) => null,
                _ => TrimmedPartReason,
            },
        { } cache => DxCacheTrimmed(cache, r),
        null => null,
    };

    static string? DxCacheTrimmed(IAppCache cache, GameRecord r) => r.WarmedKeys is { Count: > 0 } keys && cache.FilesOf(keys) is var now
        ? now.Count == 0 ? TrimmedAllReason
        : r.WarmedFileBytes is { Count: > 0 } was && was.Any(w => !now.Any(n => n.Name.Equals(w.Key, StringComparison.OrdinalIgnoreCase) && !Shrunk(n, r))) ? TrimmedPartReason
        : null
        : null;

    /// <summary>NVIDIA from 621 keeps a D3D12 game's compiled pipelines in its D3DSCache folder: the plan has D3D12 items and
    /// the last complete warm, on 621 or later, kept its dbs there. Null: the DXCache files tell.</summary>
    PlanStats? D3D12Kept(GameRecord r) => OperatingSystem.IsWindows() && AppCache is NvidiaAppCache
        && r is { WarmedD3DSCache: true, Plan.Stats: { } st } && st.Recorded + st.Generated + st.MiddlewareItems > 0
        && NvidiaMajor(r.WarmedDriverVersion) >= 621 ? st : null;

    /// <summary>Measured on 621: the game reads PSOs only from its D3DSCache folder, ray tracing state objects and D3D11 shaders
    /// only from NVIDIA's files. The D3D11 side counts unless the game is known to run D3D12: the user picked it, or the first
    /// launch after the warm created pipelines (the recorder writes create rows under d3d12.dll only).</summary>
    static bool DxCacheCounts(GameRecord r, PlanStats st) => r.WarmedRayTracing && (st.RtStateObjects > 0 || st.RtLibraries > st.RtUncovered)
        || st.D3D11Shaders > 0 && !(r.ApiOverride == DxApi.D3D12 || r.FirstLaunch is { } l && l.Hits + l.Compiles > 0);

    /// <summary>Every folder on record (a moved install's older one too) holds no db and none waits for it (Windows Disk
    /// Cleanup empties D3DSCache): all gone. The dbs the last warm left (<see cref="GameRecord.WarmedD3DSCacheBytes"/>) lost
    /// more than <see cref="TrimmedShare"/> of their length, a db gone or made again since (the game rebuilt it cold) counting
    /// as empty: part gone. Only lengths and creation times are read, never the dbs. Null while a compile of the game runs or
    /// a folder can't be read.</summary>
    string? D3DSCacheTrimmed(string gameId, GameRecord r)
    {
        var own = Store.GameDir(gameId);
        var work = Path.Combine(own, "work");
        if (D3DSCacheNames(r) is not { Count: > 0 } names || !NoCompile(work) || D3DSCacheDbs(names) is not { } now) return null;
        // per db: one db's growth doesn't hide another's loss
        long Held(string name) => now.Where(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !(d.Made > r.WarmedD3DSCacheAt?.UtcDateTime))
            .Select(d => d.Bytes).DefaultIfEmpty().Max();
        var t = now.Count == 0 ? Holds(Path.Combine(own, D3DSCacheWaiting), "*", dirs: true) == false ? TrimmedAllReason : null
            : r.WarmedD3DSCacheBytes is { Count: > 0 } was && was.Sum(w => Math.Min(Held(w.Key), w.Value)) < was.Values.Sum() * (1 - TrimmedShare)
                ? TrimmedPartReason : null;
        return NoCompile(work) ? t : null;
    }

    static List<string> D3DSCacheNames(GameRecord r) => new[] { r.D3DSCacheFolder, r.D3DSCacheMade }.OfType<string>().Where(IsFolderName).ToList();

    /// <summary>The <c>*.dxcache</c> dbs of the named D3DSCache folders, with their lengths and creation times; null when a folder can't be read.</summary>
    List<(string Name, long Bytes, DateTime? Made)>? D3DSCacheDbs(IEnumerable<string> names)
    {
        var dbs = new List<(string Name, long Bytes, DateTime? Made)>();
        foreach (var n in names)
            try
            {
                foreach (var f in new DirectoryInfo(Path.Combine(LocalAppData, "D3DSCache", n)).EnumerateFiles("*.dxcache"))
                    dbs.Add((f.Name, f.Length, FileTimes.CreatedUtc(f)));
            }
            catch (DirectoryNotFoundException) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return dbs;
    }

    static Dictionary<string, long> Lengths(List<(string Name, long Bytes, DateTime? Made)> dbs) =>
        dbs.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Max(d => d.Bytes), StringComparer.OrdinalIgnoreCase);

    /// <summary>The files of the game's own D3DSCache folders (<see cref="GameD3DSCacheDirs"/>) on any driver and of any size:
    /// what Clear cache deletes there. Only listed, never opened: the game may have its dbs open.</summary>
    List<FileInfo> GameD3DSCacheAll(Game g, GameRecord r)
    {
        var files = new List<FileInfo>();
        if (!OperatingSystem.IsWindows()) return files;
        foreach (var dir in GameD3DSCacheDirs(g, r))
            if (Directory.Exists(dir) && !IsLink(dir))
                try { files.AddRange(D3DSCache.CacheFiles(dir)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return files;
    }

    /// <summary>NVIDIA from 621: <see cref="GameD3DSCacheAll"/> as part of the game's compiled cache. None on other drivers.</summary>
    List<FileInfo> GameD3DSCacheFiles(Game g, GameRecord r)
    {
        if (AppCache is not NvidiaAppCache || NvidiaMajor(Vendor.Gpu.DriverVersion) is not >= 621) return [];
        var files = GameD3DSCacheAll(g, r);
        // below 1 MB: dbs without pipelines, as a DirectX 11 game leaves
        return files.Sum(f => f.Length) < 1L << 20 ? [] : files;
    }

    long? WindowsCacheOf(Game g, GameRecord r) => GameD3DSCacheFiles(g, r) is { Count: > 0 } f ? f.Sum(x => x.Length) : null;

    /// <summary>The folders on record and the one the runtime opens for the game's exe path, also before any is on record.</summary>
    IEnumerable<string> GameD3DSCacheDirs(Game g, GameRecord r) =>
        new[] { r.D3DSCacheFolder, r.D3DSCacheMade, LaunchExe(g) is { } exe ? D3DSCache.FolderName(exe) : null }
            .Where(n => n != null && IsFolderName(n)).Distinct().Select(n => Path.Combine(LocalAppData, "D3DSCache", n!));

    // measured: Steam starts a game under its library folder's path as libraryfolders.vdf has it, which ExePath keeps
    static string? LaunchExe(Game g) => g.Store == Core.Store.Xbox ? XboxSource.LaunchPath(g) : g.ExePath;

    /// <summary>No compile of the game runs, nor its fold (work\ goes after it): checked before the reads and after them.</summary>
    static bool NoCompile(string work) => !Directory.Exists(work) && Holds(work, "*", dirs: true) == false;

    /// <summary>Whether the folder holds a matching file (or folder); false when it doesn't exist, null when it can't be read.</summary>
    static bool? Holds(string dir, string pattern, bool dirs)
    {
        try { return (dirs ? Directory.EnumerateFileSystemEntries(dir, pattern) : Directory.EnumerateFiles(dir, pattern)).Any(); }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Share of a warmed NVIDIA file's held bytes it may lose before it counts as trimmed.</summary>
    const double TrimmedShare = 0.25;

    /// <summary>The file holds less than the last complete warm left: on NVIDIA its header's used bytes fell by more than
    /// <see cref="TrimmedShare"/>, unknown while unreadable. Recorded without used bytes: shorter.</summary>
    static bool Shrunk(FileInfo f, GameRecord r) => r.WarmedFileUsed is { } held && held.TryGetValue(f.Name, out var was)
        ? NvidiaAppCache.UsedBytes(f.FullName) is { } used && used < was * (1 - TrimmedShare)
        : f.Length < (r.WarmedFileBytes?.GetValueOrDefault(f.Name) ?? 0);

    void NoteProfile(Game g, string exe, IEnumerable<string> keys)
    {
        if (AmdAppCache.IsNameHashed(keys, exe) == false)
            Log?.Report($"{g.Name}: the driver keys {exe}'s cache as {string.Join(", ", keys.Where(k => k.StartsWith(AmdAppCache.D3D12Prefix)))}, " +
                        $"not its name hash {AmdAppCache.DxcKey(exe)} " +
                        (Ags(g) is { } a ? $"(its AGS app name {a.App}" : "(an app profile") + ": the exe name's case doesn't matter)");
    }

    /// <summary>On AMD, what the game registers with AGS: what its own process was seen registering (the recorder's #ags line,
    /// from <paramref name="seen"/> ?? <paramref name="r"/>: only an AGS DLL is hooked), else <see cref="AmdAgs.Of"/> (Unreal links
    /// AGS into the exe), else with <paramref name="r"/>, recovered from the keys its process held (<see cref="AmdAgs.Recovered"/>,
    /// which reads the exe). Whether a warm uses it: <see cref="AgsFor"/>.</summary>
    AgsRegistration? Ags(Game g, GameRecord? r = null, GameRecord? seen = null)
    {
        if (OwnDriver != GpuVendor.Amd) return null;
        if ((seen ?? r) is { AgsSeenApp: { Length: > 0 } app } s)
            return new(app, s.AgsSeenEngine ?? "", AppVersion: s.AgsSeenAppVersion ?? AmdAgs.UnspecifiedVersion, EngineVersion: s.AgsSeenEngineVersion ?? AmdAgs.UnspecifiedVersion);
        EngineInfo? engine;
        lock (_scanLock) engine = _scan?.GetValueOrDefault(g.Id)?.Engine;   // the scan's cache: Games may not hold the game yet
        return AmdAgs.Of(g, engine) ?? (r == null ? null : AmdAgs.Recovered(g, engine, WarmExeName(g, r), r.HeldKeys?.Split(',') ?? (IEnumerable<string>)r.GameKeys));
    }

    /// <summary>HINT ONLY: the key the game's AGS app name probably gives (<see cref="AmdAppCache.AgsKey"/>).</summary>
    string? AgsKey(Game g, GameRecord? r) => Ags(g, r) is { } a ? AmdAppCache.AgsKey(Path.GetFileName(g.ExePath), a.App) : null;

    /// <summary>The registration a warm of this game uses: the game's own (<see cref="Ags"/>), so the driver keys the warm's
    /// cache as the game's whatever key it gives that name. None once the game's process was last seen holding its exe name's
    /// key (no AGS, or a mod in AGS's place such as OptiScaler), and, before the game is seen holding any D3D12 key, none for
    /// an exe that can't be read or after a launch showed a miss (<see cref="AgsLaunchMissed"/>). Null: a plain device.</summary>
    AgsRegistration? AgsFor(Game g, GameRecord r, bool recover = false) => AgsFor(Ags(g, recover ? r : null, r), WarmExeName(g, r), r);

    public static AgsRegistration? AgsFor(AgsRegistration? a, string warmExeName, GameRecord r)
    {
        if (a == null) return null;
        var seen = r.GameKeys.Where(k => k.StartsWith(AmdAppCache.D3D12Prefix, StringComparison.Ordinal)).ToList();
        // its plain key held at an earlier sighting only (a launch after a driver cache wipe) doesn't rule AGS out for good
        if (seen.Count > 0)
            return (r.HeldKeys?.Split(',') ?? [.. seen]).Contains(AmdAppCache.DxcKey(warmExeName)) ? null : a;
        return !a.ExeUnread && !r.AgsMissed ? a : null;
    }

    /// <summary>The AGS registration the next warm of this game uses (<see cref="AgsFor"/>); null = a plain device.</summary>
    public AgsRegistration? WarmAgs(string gameId) => AgsFor(Find(gameId).Game, Store.LoadGame(gameId), recover: true);

    static string ExeStamp(Game g) => new FileInfo(g.ExePath) is { Exists: true } f ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "";

    public IReadOnlyList<GameState> StaleGames() => Games.Where(s => s.Status == GameStatus.Stale).ToList();
    public IReadOnlyList<GameState> DriverStaleGames()
    {
        lock (_lock) return _games.Where(s => s.Status == GameStatus.Stale && _driverStale.GetValueOrDefault(s.Game.Id) && s.NoStutter == null).ToList();
    }

    /// <summary>What the scheduled re-warm queues "when idle": <see cref="DriverStaleGames"/> but one whose idle warm was held
    /// back (<see cref="RulesGrew"/>) for this driver and these rules, until either changes. The notification and the
    /// library still list it: its cache is gone.</summary>
    public IReadOnlyList<GameState> IdleRewarmGames() =>
        DriverStaleGames().Where(s => s.Engine is not { } e || Store.LoadGame(s.Game.Id).IdleHeldBack != HeldBackKey(RulesFile.Current.Hash(e))).ToList();

    /// <summary>The driver-update choice Right away (<see cref="Settings.DriverRightAway"/>): <see cref="IdleRewarmGames"/>
    /// queued with <see cref="EnqueueRightAway"/>. How many were queued.</summary>
    public int RewarmRightAway()
    {
        if (Settings is not { OnDriverUpdate: DriverUpdateMode.WhenIdle, DriverRightAway: true }) return 0;
        var games = IdleRewarmGames();
        foreach (var s in games) EnqueueRightAway(s.Game.Id);
        return games.Count;
    }

    string HeldBackKey(string rules) => $"{DriverId}|{rules}";

    // What the user skipped: the driver and the game build each stale game was offered for.
    string StaleKey(GameState s) => $"{DriverId}|{s.Game.Version ?? ExeStamp(s.Game)}";

    /// <summary>proton-env's variables for <paramref name="g"/>, making the folders they name unless <paramref name="make"/> is
    /// false: on Mesa its cache environment (<see cref="MesaLinux.CacheEnvironment(string, Game, bool, string?)"/>, InvalidOperationException
    /// for a game Steam doesn't run), else the game's half of it, or with <paramref name="warm"/> the warm's
    /// (<see cref="Wine.CacheEnvironment"/>), in its proton-cache folder.</summary>
    public List<(string Name, string Value)> CacheEnvironment(Game g, bool warm = false, bool make = true)
    {
        var gameDir = Store.GameDir(g.Id);
        // Mesa's keys name no app: the warm and the game take the same
        if (MesaLinux.Is(Vendor.Caps)) return MesaLinux.CacheEnvironment(gameDir, g, make, (_warmer as Warmer)?.SteamRoot);
        // the install's exe name, as the Proton host sets the warm's half: the game's half reads the warm's files by it
        var (warmHalf, gameHalf) = Wine.CacheEnvironment(Wine.CacheRoot(gameDir, make), Path.GetFileName(g.ExePath));
        return warm ? warmHalf : gameHalf;
    }

    public const string DriverPart = "Driver cache", WindowsPart = "Windows shader cache", PipelinePart = "Game's pipeline cache",
        PrecachePart = "Game's shader precache", SteamPart = "Steam's shader cache", Vkd3dPart = "vkd3d-proton's cache";

    /// <summary><paramref name="gamePrecache"/>: also what the game writes itself and rebuilds at its next start
    /// (<see cref="OwnPipelineCaches"/>, *.ushaderprecache files): <see cref="PipelinePart"/>, <see cref="PrecachePart"/>.</summary>
    public IReadOnlyList<CachePart> GameCaches(string gameId, bool gamePrecache = false)
    {
        var s = Find(gameId);
        var rec = Store.LoadGame(gameId);
        var parts = new List<CachePart>();
        if (AppCache != null && DriverKeys(s.Game, rec).Keys is { Count: > 0 } keys) parts.Add(Part(DriverPart, AppCache.FilesOf(keys)));
        parts.Add(Part(SteamPart, SteamGlFiles(s.Game, s.Engine)));
        Vkd3dFiles(gameId, parts);
        // the compile's runtime cache dbs waiting for the game's folder: ours, also for an anti-cheat game
        var waiting = Path.Combine(Store.GameDir(gameId), D3DSCacheWaiting) is var w && Directory.Exists(w) && !IsLink(w)
            ? new DirectoryInfo(w).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList() : [];
        waiting.AddRange(PendingStaged(s.Game, rec).SelectMany(D3DSCache.CacheFiles));   // a clear takes them, or a later fold would bring them back
        // the game's own folders by name only, no db read, which the Cache page leaves to this delete
        var own = GameD3DSCacheAll(s.Game, rec);
        if (s.AntiCheat != AntiCheat.None)   // anti-cheat: nothing near the game, only the driver's cache
        {
            parts.Add(Part(WindowsPart, own.Concat(waiting)));
            return parts.Where(p => p.Files.Count > 0).ToList();
        }
        if (OperatingSystem.IsWindows()) GameD3DSCache(s.Game);   // remembered before a clear empties it
        // a link's files only as the fold follows it, its dbs naming the game: listed through it, the link and its folder stay
        if (OperatingSystem.IsWindows()) parts.Add(Part(WindowsPart, D3DSCache.FoldersOf(Path.Combine(LocalAppData, "D3DSCache"), s.Game)
            .Where(d => !IsLink(d) || D3DSCache.Followed(d, out _) != null && D3DSCache.ExePaths(d) is { Count: > 0 } exes && exes.All(e => D3DSCache.IsGameExe(e, s.Game)))
            .SelectMany(D3DSCache.CacheFiles).Concat(own)
            .DistinctBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).Concat(waiting)));
        if (!gamePrecache) return parts.Where(p => p.Files.Count > 0).ToList();
        // Unreal's user pipeline cache stays (UnrealUserCache.FilesOf): it lists what play created, and only playing refills it
        parts.Add(Part(PipelinePart, OwnPipelineCaches.FilesOf(s.Game, s.Engine, Documents, RoamingAppData).Select(f => new FileInfo(f))));
        parts.Add(Part(PrecachePart, PrecacheFiles(s.Game).Select(f => new FileInfo(f))));
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
        if (PairHoldsCache(gameId) is { } why) throw new InvalidOperationException(why);
        return ClearCache(gameId, gamePrecache, steam: true);
    }

    /// <summary><paramref name="steam"/>: also the game's files in Steam's GLCache folder (<see cref="SteamPart"/>), which a
    /// before and after recording's clears leave to its set-aside.</summary>
    bool ClearCache(string gameId, bool gamePrecache, bool steam = false) => ClearCache(gameId, gamePrecache, steam, locked: false, out _);

    /// <summary><paramref name="locked"/>: the game's compile lock is held for the delete even without Steam's files.
    /// <paramref name="freed"/>: the bytes and files deleted.</summary>
    bool ClearCache(string gameId, bool gamePrecache, bool steam, bool locked, out (long Bytes, int Files) freed)
    {
        freed = default;
        var s = Find(gameId);
        var exe = Path.GetFileName(s.Game.ExePath);
        lock (_lock)
            if (_current == gameId) throw new InvalidOperationException($"a compile of {s.Game.Name} is in progress");
        if (CacheInUse(s.Game)) throw new InvalidOperationException($"{s.Game.Name} is running");
        using var folds = StopFolds(s.Game);
        lock (_lock)   // again: one may have started while the folds stopped
            if (_current == gameId) throw new InvalidOperationException($"a compile of {s.Game.Name} is in progress");
        var rec = Store.LoadGame(gameId);   // after the folds, which write it
        var keys = DriverKeys(s.Game, rec).Keys;
        var driver = AppCache != null && keys.Count > 0;
        if (driver && SharedWith(gameId, keys) is { Count: > 0 } shared)
            throw new InvalidOperationException($"{s.Game.Name}'s driver cache is shared with " +
                string.Join(", ", shared.Select(x => $"{x.Name} ({string.Join(", ", x.Keys)})")) +
                ": the driver gives their exes the same key (an app profile, or the same exe name), so clearing it would clear theirs too");
        // listed once held: the Linux placer's removal rewrites Steam's files
        var steamHeld = steam && SteamGlFiles(s.Game, s.Engine).Count > 0;
        using var own = steamHeld ? SteamGlHeld(s)
            : locked ? CompileLock(gameId) ?? throw new InvalidOperationException($"a compile of {s.Game.Name} is in progress") : null;
        var parts = GameCaches(gameId, gamePrecache).Where(p => steamHeld || p.Name != SteamPart).ToList();
        if (!driver && parts.Count == 0) return false;
        var steams = parts.Where(p => p.Name == SteamPart).SelectMany(p => p.Files).ToHashSet(StringComparer.Ordinal);
        // the driver's files as a delete lists them: all, or a refusal (IAppCache.FilesToDelete). Mesa's folder holds Steam's
        // pre-cache too, which a before and after recording's clears leave to its set-aside
        List<FileInfo> files = [.. (driver && (steam || AppCache is not MesaLinuxAppCache) ? AppCache!.FilesToDelete(keys) : []).Concat(parts.Where(p => p.Name != DriverPart).SelectMany(p => p.Files).Select(f => new FileInfo(f)))];
        var bytes = files.Sum(f => f.Exists ? f.Length : 0);
        try { AppCacheFiles.DeleteAll(files, steams); }
        catch (InvalidOperationException e) { Log?.Report($"{s.Game.Name}: cache not cleared: {e.Message}"); throw; }
        freed = (bytes, files.Count);
        _foldRoom.TryRemove(gameId, out _);
        // a link's target would leave the link leading nowhere
        var linked = OperatingSystem.IsWindows() && Path.Combine(LocalAppData, "D3DSCache") is var d3ds && Directory.Exists(d3ds)
            ? Directory.EnumerateDirectories(d3ds).Where(IsLink).Select(GameFiles.FinalPath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        foreach (var dir in parts.Where(p => p.Name == WindowsPart).SelectMany(p => p.Files).Select(Path.GetDirectoryName).Distinct()
                     .Where(d => !IsLink(d!) && !linked.Contains(GameFiles.FinalPath(d!) ?? d!)))
            try { Directory.Delete(dir!); }
            catch (IOException) { }   // not empty: a file appeared meanwhile, left alone
        if (steams.Count > 0) Log?.Report($"{s.Game.Name}: {steams.Count} files of Steam's shader cache deleted");
        if (!driver && steams.Count == 0 && rec.D3DSCacheSince == null) return true;
        ForgetWarm(gameId, rec);
        Refresh(s.Game);
        return true;
    }

    /// <summary>The game's driver cache was deleted: it shows as needing a compile.</summary>
    void ForgetWarm(string gameId, GameRecord rec)
    {
        Log?.Report($"{Games.FirstOrDefault(s => s.Game.Id == gameId)?.Game.Name ?? gameId}: compiled cache cleared");
        ResetWarm(rec);
        rec.CacheClearedAt = Clock();   // the other APIs' kept compiles too (SwitchApi)
        // its staged folders went with the clear (PendingStaged): one left, or a db whose owner can't be read, stays pending for
        // the fold, never stranded
        var unread = new List<string>();
        if (Games.FirstOrDefault(s => s.Game.Id == gameId) is not { } listed || PendingStaged(listed.Game, rec, unread).Count == 0 && unread.Count == 0)
            rec.D3DSCacheSince = null;
        Store.SaveGame(gameId, rec);
        DropKept(gameId);
        SteamPlaceSoon(gameId, exited: false);
    }

    /// <summary>The record's last complete warm is gone with its files (<see cref="ForgetWarm"/>).</summary>
    void ResetWarm(GameRecord rec)
    {
        // the keys stay: they are this exe name's, so the cache the game builds by itself still counts in CacheOnDisk
        if (!IsUnreached(rec)) rec.UnreachedWarm = null;   // a verdict on an older warm: with WarmedAt gone it would apply again
        (rec.WarmedAt, rec.LastWarmTime, rec.LastCacheGrowthBytes, rec.ResumeAt, rec.StoppedCreated) = (null, null, null, 0, null);
        if (!WarmMissesGame(rec, HeldOnDriver(rec, Snapshot()))) (rec.WarmedDriverVersion, rec.WarmedDriverId) = (null, null);   // CantReach compares the driver the warm missed on
        (rec.LastWarmFailed, rec.LastWarmSkipped, rec.LastWarmNeedsRecording, rec.LastWarmCrashed, rec.LastWarmDriverSkipped, rec.LastWarmUnbuilt, rec.LastWarmInvalid,
            rec.LastWarmUnsupported, rec.LastWarmKnown, rec.LastWarmCrashedBefore) = (null, null, null, null, null, null, null, null, null, null);
        (rec.LastWarmTried, rec.LastWarmFailedBy) = (null, null);
        rec.WarmedRoomLeft = null;
        (rec.WarmedCareful, rec.FirstLaunch, rec.WarmedFiles, rec.WarmedFileIds, rec.WarmedD3DSCacheBytes, rec.WarmedD3DSCacheAt, rec.D3DSCacheFoldKeys) = (false, null, null, null, null, null, null);
        (rec.WarmedSteamParcId, rec.WarmedSteamParcBytes) = (null, null);
    }

    public void SetCarefulCompile(string gameId, bool on)
    {
        var s = Find(gameId);
        if (CarefulThreads(OwnDriver) == null) throw new InvalidOperationException($"the careful compile is for AMD drivers, not {Vendor.Gpu.Name}");
        lock (_lock)
            if (_current == gameId) throw new InvalidOperationException($"a compile of {s.Game.Name} is in progress");
        var rec = Store.LoadGame(gameId);
        if (rec.Careful == on) return;
        (rec.Careful, rec.ResumeAt) = (on, 0);   // a stopped warm's resume point counts the other schedule's items
        Store.SaveGame(gameId, rec);
        Refresh(s.Game);
    }

    public void SetNewShadersMode(string gameId, NewShadersMode? value)
    {
        var s = Find(gameId);
        var rec = Store.LoadGame(gameId);
        if (rec.NewShaders == value) return;
        rec.NewShaders = value;
        Store.SaveGame(gameId, rec);
        Refresh(s.Game);
    }

    public void SetApiOverride(string gameId, DxApi? value)
    {
        var s = Find(gameId);
        lock (_lock)
            if (_current == gameId) throw new InvalidOperationException($"a compile of {s.Game.Name} is in progress");
        var rec = Store.LoadGame(gameId);
        if (rec.ApiOverride == value) return;
        (rec.ApiOverride, rec.ResumeAt) = (value, 0);   // a stopped warm's resume point counts the other plan's items
        if (rec.Plan != null) rec.PlanApi ??= ApiKey(s.Engine?.GraphicsApi);   // a plan from before PlanApi: built for the API shown until now
        Store.SaveGame(gameId, rec);
        Refresh(s.Game, prompt: true);   // the evaluation's key has the choice: detected again with it, the plan checked
        // planned but never compiled: NeedsPlanCheck covers compiled games only
        if (CheckPlans && Store.LoadGame(gameId) is { Plan: not null, WarmedAt: null } planned && Find(gameId).Engine is { } e && OtherApi(planned, e))
            CheckPlan(gameId, prompt: true);
        lock (_recorderLock)
            if (!Reconcile(Find(gameId))) Refresh(s.Game);   // the recorder may go in or out, or as d3d11.dll on AMD; a running game's waits
    }

    /// <summary>Other discovered games whose driver cache is (or would be) the same files as this game's: keys learned for
    /// both, or a D3D12 key that the other game's exe name maps to (<see cref="AmdAppCache.HintKey"/>: a known AMD app
    /// profile such as every "ff7rebirth*" exe sharing 6b2fcd83, or the same exact name), or a key the driver gives by the exe
    /// name alone when the other exe has the same name (NVIDIA's, AMD's D3D11), even before it was warmed.</summary>
    List<(string Name, List<string> Keys)> SharedWith(string gameId, IReadOnlySet<string> keys)
    {
        var shared = new List<(string, List<string>)>();
        var exe = Path.GetFileName(Find(gameId).Game.ExePath);
        foreach (var o in Games)
        {
            if (o.Game.Id == gameId) continue;
            var other = Store.LoadGame(o.Game.Id);
            var hint = AmdAppCache.HintKey(WarmExeName(o.Game, other));
            var ags = AgsFor(o.Game, other) != null ? AgsKey(o.Game, null) : null;
            // NVIDIA keys every file by the exe name whatever its case, AMD's D3D11 key comes from the name too (case not measured)
            var sameName = Path.GetFileName(o.Game.ExePath).Equals(exe, StringComparison.OrdinalIgnoreCase);
            var both = keys.Where(k => sameName && (AppCache is not AmdAppCache || k.StartsWith(AmdAppCache.D3D11Prefix, StringComparison.Ordinal))
                || other.CacheKeys.Contains(k) || k == hint || k == ags).Order().ToList();
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
        if (AppCache is IntelAppCache) keys.RemoveWhere(k => IntelAppCache.Key(k) == null);   // another vendor's, learned before a GPU switch: no Intel key yet
        var exe = Path.GetFileName(g.ExePath);
        // a Vulkan game's cache is its Vulkan keys' files, or under Steam's redirect Steam's folder (SteamGlFiles)
        if (Games.FirstOrDefault(s => s.Game.Id == g.Id)?.Engine is { } e && VulkanPlan.IsVulkan(e))
            return (keys, AppCache == null || keys.Any(AppCacheFiles.IsVulkanKey) || SteamGlFiles(g, e).Count > 0 ? null
                : $"the Vulkan driver cache key of {exe} isn't learned yet (SCSKiller learns it when it compiles the game or sees it running): the driver cache is not cleared");
        if (AppCache is not AmdAppCache amd)
            return (keys, AppCache == null || keys.Count > 0 ? null
                : $"the driver cache key of {exe} isn't learned yet (SCSKiller learns it when it compiles the game or sees it running): the driver cache is not cleared");
        var names = new[] { exe, rec.LaunchedExeName, rec.WarmedExeName }.OfType<string>().Distinct().ToList();
        var dxc = keys.Where(k => k.StartsWith(AmdAppCache.D3D12Prefix, StringComparison.Ordinal)).ToList();
        // the AGS app name's key is only a hint: cleared once a process is seen holding it, like any key
        var ags = AgsFor(g, rec) is { } a && AgsKey(g, null) is { } ak && !keys.Contains(ak) && (dxc.Count == 0 || amd.FilesOf([ak]).Count > 0)
            ? $"the D3D12 key {ak} that {g.Name} gets from its AGS app name {a.App} isn't learned yet (SCSKiller learns it when it compiles the game or sees it running): it is not cleared"
            : null;
        if (dxc.Count > 0 && !names.Any(n => dxc.Contains(AmdAppCache.DxcKey(n)))) return (keys, ags);
        keys.UnionWith(names.Select(AmdAppCache.HintKey).Where(k => amd.FilesOf([k]).Count > 0));
        return (keys, dxc.Count > 0 || ags != null ? ags
            : $"the D3D12 driver cache key of {exe} isn't learned yet: clearing takes the key the driver gives that exe name, so a driver app profile's key or a D3D11 key would be missed");
    }

    /// <summary>Which of the game's driver cache <see cref="ClearGameCache"/> may miss (a key not learned yet); null = none.</summary>
    public string? DriverCacheGap(string gameId) => DriverKeys(Find(gameId).Game, Store.LoadGame(gameId)).Gap;

    public bool SetEncryptionKey(string gameId, string key)
    {
        var state = Games.FirstOrDefault(s => s.Game.Id == gameId) ?? throw new ArgumentException($"unknown game {gameId}");
        var game = state.Game;
        if (state.Engine?.Family == BlackSpace.BlackSpaceReader.Family)
            return (_reader as BlackSpace.BlackSpaceReader ?? (_reader as EngineReaders)?.Get<BlackSpace.BlackSpaceReader>()) is { } b && b.SetKey(game, key);
        return (_reader as UnrealReader ?? (_reader as EngineReaders)?.Get<UnrealReader>()) is { } u && u.SetKey(game, key);
    }

    UnrealReader? UnrealOf(string gameId) => Find(gameId).Engine?.Family == "Unreal" ? _reader as UnrealReader ?? (_reader as EngineReaders)?.Get<UnrealReader>() : null;

    public bool HasEncryptionKey(string gameId) => UnrealOf(gameId) is { } u && u.HasKey(Find(gameId).Game);

    public void RemoveEncryptionKey(string gameId)
    {
        if (UnrealOf(gameId) is { } u) u.RemoveKey(Find(gameId).Game);
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

    /// <summary>Registers the logon/idle re-warm task (it runs the published CLI) for Ask and WhenIdle, removes it for Off;
    /// on Linux the user service (RewarmService). For the Settings page, after the user changes OnDriverUpdate.</summary>
    public void ApplyDriverUpdateMode()
    {
        if (!OperatingSystem.IsWindows()) ApplyRewarmService();
        else if (Settings.OnDriverUpdate == DriverUpdateMode.Off) ScheduledTask.Unregister();
        else ScheduledTask.Register(ScheduledTask.TaskExe() ?? throw new FileNotFoundException(@"the command-line tool (cli\scskiller.exe) is not next to the app"));
    }

    partial void ApplyRewarmService();

    /// <param name="prompt">a plan check it queues doesn't wait for idle (<see cref="CheckPlan"/>): the user asked for the change</param>
    /// <param name="capped">the page's check (<see cref="Evaluate"/>)</param>
    void Refresh(Game g, CancellationToken ct = default, bool force = false, bool prompt = false, bool capped = false)
    {
        var ticket = Ticket();
        var s = Evaluate(g, force, out _, out var driverStale, ct, capped);
        GameState? before;
        lock (_lock)
        {
            if (_evaluatedAt.GetValueOrDefault(g.Id) > ticket || Unlisted(g.Id) || StaleCopy(g)) return;   // an evaluation started later is in place
            if (IsUnchecked(s) && _evaluatedAt.ContainsKey(g.Id)) return;   // not read: what a read found stays
            if (!IsUnchecked(s)) (_evaluatedAt[g.Id], _driverStale[g.Id]) = (ticket, driverStale);
            s = WithVerdict(s);
            var i = _games.FindIndex(x => x.Game.Id == g.Id);
            before = i >= 0 ? _games[i] : null;
            if (i >= 0) _games[i] = s; else _games.Add(s);
        }
        GameChanged?.Invoke(s);
        if (before?.Status != s.Status || before.WarmedAt != s.WarmedAt || before.StatusReason != s.StatusReason) KeepList();   // a start with ScanAtStart off lists these
        // a collection's other games show whether its first game's recorder is in
        if (before?.RecorderInstalled != s.RecorderInstalled && Collections.Member(g.Id) is { } m && m.All[0].Id == g.Id)
            foreach (var other in m.All.Skip(1).Select(e => Games.FirstOrDefault(x => x.Game.Id == e.Id)?.Game).OfType<Game>())
                Refresh(other, ct);
        // a game's exit imports its recording, a community download lands after the scan: neither waits for the next scan
        if (CheckPlans && (prompt || !Hdd(g)) && s is { Engine: not null, Status: not GameStatus.Unsupported } && (s.RtToPlan || NeedsPlanCheck(g, Store.LoadGame(g.Id), s.Engine)))
            CheckPlan(g.Id, prompt);
    }

    // game id -> the last anti-cheat verdict AntiCheatFound recorded, until a full evaluation finds none
    readonly ConcurrentDictionary<string, AntiCheat> _verdicts = new();
    // game ids whose anti-cheat check didn't finish: kept out, not saved as a verdict, until a full walk begun after it finds none
    readonly ConcurrentDictionary<string, bool> _unknown = new();

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

    /// <summary>A scan's states replace the list unless that scan or a later one listed games first (Scanned lists them before
    /// it reads them): then they only update the games that list has, so a game added meanwhile stays. Under _lock.</summary>
    void Publish(long started, long discovering, List<GameState> states)
    {
        if (started > _listed)
        {
            // a game evaluated since discovery began and not in it (Add game) stays
            (_listed, _games) = (started, [.. states, .. _games.Where(g => _evaluatedAt.GetValueOrDefault(g.Game.Id) > discovering && !Unlisted(g.Game.Id) && !states.Any(s => s.Game.Id == g.Game.Id))]);
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
            if (Unlisted(id)) continue;
            if (StaleCopy(states[i].Game))   // read before its folder changed: the state already listed (the new folder's) stays
            {
                if (_games.Find(x => x.Game.Id == id) is { } listed) now.Add(listed);
                continue;
            }
            if ((_evaluatedAt.GetValueOrDefault(id) > tickets[i] || IsUnchecked(states[i]) && _evaluatedAt.ContainsKey(id)) && _games.Find(x => x.Game.Id == id) is { } newer) states[i] = newer;
            else if (!IsUnchecked(states[i])) (_evaluatedAt[id], _driverStale[id]) = (tickets[i], driverStale[i]);
            now.Add(WithVerdict(states[i]));
        }
        return now;
    }

    /// <summary>The game as its store lists it now. The scan's copy keeps the build id it had when the app started, so a game
    /// updated since then would be compiled, and its warm recorded and shared, under the old build. One store's discovery:
    /// only before a compile and after the game exits.</summary>
    Game Current(Game g) => Discovered(g) ?? Following(g);

    /// <summary><see cref="Current"/> without its fallback: null when its store doesn't list the game now (an update under
    /// way, uninstalled) or can't be read.</summary>
    Game? Discovered(Game g)
    {
        foreach (var source in _sources.Where(s => s.Store == g.Store))
            try
            {
                if (source.Discover().FirstOrDefault(x => x.Id == g.Id) is { } now) return Following(Unreal(now));
            }
            catch (Exception e) { Log?.Report($"{source.Store}: discovery failed: {e.Message}"); }
        return null;
    }

    /// <summary>The game with the exe it was seen running (<see cref="GameRecord.RunsExe"/>) when that is another file of its
    /// install than discovery's (Stellar Blade: discovery once took the copy in PatchData). A game added by hand follows
    /// it only inside the folder the user confirmed.</summary>
    /// A followed exe is dropped once it's gone, discovery names another exe, the game's build changes (an update may
    /// have moved the launch target), or it is a helper (<see cref="GameFiles.IsHelper"/>).</summary>
    Game Following(Game g)
    {
        var rec = Store.LoadGame(g.Id);
        if (Chosen(g, rec) is { } chosen) return chosen;
        if (Listed(g) is { } listed) return listed;
        if (rec.RunsExe is not { } exe) return g;
        var leaves = LeavesUnreal(g.ExePath, exe, rec.RunsExeProven);
        if (!File.Exists(exe) || !GameFiles.Inside(g.InstallDir, exe) || rec.RunsExeFrom != g.ExePath || rec.RunsExeBuild != Build(g, exe)
            || Collections.Exes(g)?.Contains(Path.GetFileName(exe), StringComparer.OrdinalIgnoreCase) == false || leaves || GameFiles.IsHelper(exe))
        {
            (rec.RunsExe, rec.RunsExeFrom, rec.RunsExeBuild, rec.RunsExeAt, rec.RunsExeWas, rec.RunsExeProven, rec.RunsExeRenderer) = (null, null, null, null, null, false, false);
            Store.SaveGame(g.Id, rec);
            Log?.Report(leaves ? $"{g.Name}: no longer following {exe}: {NotUnrealReason(g)}"
                : GameFiles.IsHelper(exe) ? $"{g.Name}: no longer following {exe}: a helper, not the game"
                : $"{g.Name}: no longer following {exe}: the game changed since it ran from there");
            return g;
        }
        return Unconfirmed(g) ? g : g with { ExePath = exe };
    }

    /// <summary>A store's game with the exe the user picked (<see cref="SetGameExe"/>), through <see cref="ShippingExe"/> as a
    /// store-named exe is; null = Auto. A pick that's gone goes back to Auto.</summary>
    Game? Chosen(Game g, GameRecord rec)
    {
        if (rec.ChosenExe is not { } rel || g.Store == Core.Store.Manual) return null;
        var exe = Path.GetFullPath(Path.Combine(g.InstallDir, rel));
        if (File.Exists(exe) && GameFiles.Inside(g.InstallDir, exe)) return g with { ExePath = ShippingExe(g.InstallDir, exe) };
        rec.ChosenExe = null;
        Store.SaveGame(g.Id, rec);
        Log?.Report($"{g.Name}: the exe you chose ({exe}) is gone, back to Auto");
        return null;
    }

    public void SetGameExe(string gameId, string? exe)
    {
        var g = Find(gameId).Game;
        if (g.Store == Core.Store.Manual) throw new ArgumentException("a game you added takes another exe through Add a game");
        if (Collections.Member(gameId) != null) throw new ArgumentException($"{g.Name} is one of a collection's games: it keeps its exe");
        string? rel = null;
        if (exe != null)
        {
            var full = Path.GetFullPath(exe);
            if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(full) || !GameFiles.Inside(g.InstallDir, full))
                throw new ArgumentException($"Pick an exe inside {g.InstallDir}.");
            rel = Path.GetRelativePath(GameFiles.DirKey(g.InstallDir), full);   // kept when the store moves the install
        }
        lock (_lock)
            if (_current == gameId) throw new InvalidOperationException($"a compile of {g.Name} is in progress");
        var rec = Store.LoadGame(gameId);
        if (rec.ChosenExe == rel) return;
        rec.ChosenExe = rel;
        Store.SaveGame(gameId, rec);
        ExeChanged(g, rel == null ? "back to Auto" : "chosen");
    }

    void ExeChanged(Game g, string how)
    {
        var now = Current(g);
        Log?.Report($"{g.Name}: exe {how}: {now.ExePath}");
        Refresh(now, prompt: true);   // the evaluation's key has the exe: detected again, the plan checked
        lock (_recorderLock)   // also in the CLI: the recorder moves to the new exe's folder, a running game's waits
            if (!Reconcile(Find(g.Id))) Refresh(now);
        KeepList();
    }

    /// <summary>A store's game with its exe from game-exes.json (<see cref="GameExes"/>) when that file is in its install, reached
    /// through no link, then through <see cref="ShippingExe"/> as a store-named exe is; null = detection's. One that isn't there is logged once.</summary>
    Game? Listed(Game g)
    {
        if (g.Store == Core.Store.Manual || GameExes.Current.Exe(g, () => ScanEngine(g.Id)) is not { } rel) return null;
        var exe = Path.GetFullPath(Path.Combine(g.InstallDir, rel));
        if (GameFiles.InsideNoLinks(g.InstallDir, exe)) return g with { ExePath = ShippingExe(g.InstallDir, exe) };
        if (_exesSkipped.TryAdd($"{g.Id}|{rel}", 0)) Log?.Report($"{g.Name}: the exe list names {rel}, not a file of its install: kept {g.ExePath}");
        return null;
    }

    readonly ConcurrentDictionary<string, byte> _exesSkipped = new();

    // the engine the scan last read: the list is applied before the game is read with its exe
    EngineInfo? ScanEngine(string id)
    {
        lock (_scanLock) return (_scan ??= Store.LoadScan()).GetValueOrDefault(id)?.Engine;
    }

    /// <summary>An Unreal game runs its &lt;Project&gt;-&lt;Platform&gt;-Shipping.exe, or its exe in &lt;Project&gt;\Binaries\Win*: another
    /// exe seen running beside it, or one outside such a folder (Hogwarts Legacy's root stub of the same name), is a launcher
    /// or a helper, never the game. <paramref name="proven"/>: an exe beside the Shipping exe that proved to be the game
    /// (<see cref="FollowUnrealOther"/>: Jedi: Fallen Order's starwarsjedifallenorder.exe).</summary>
    static bool LeavesUnreal(string from, string to, bool proven = false) =>
        GameFiles.IsShipping(from) && !GameFiles.IsShipping(to) && !(proven && SameFolder(from, to))
        || GameFiles.InBinaries(from) && !GameFiles.InBinaries(to);

    static bool SameFolder(string a, string b) => string.Equals(Path.GetDirectoryName(a), Path.GetDirectoryName(b), StringComparison.OrdinalIgnoreCase);

    static string NotUnrealReason(Game g) =>
        $"the game is {Path.GetFileName(g.ExePath)}, an Unreal {(GameFiles.IsShipping(g.ExePath) ? "Shipping exe" : "game exe in a Binaries folder")}";

    static string Build(Game g, string exe) => g.Version ?? (new FileInfo(exe) is { Exists: true } f ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "");

    /// <summary>A started game whose process runs another exe of its install than the game's: the record follows it, and the
    /// recorder moves next to it once the game exits (Reconcile), or is stamped for it there when it shares the game exe's
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
        // a followed renderer stays the game's: bb-start.exe beside the Bloodborne port's renderer is its launcher
        if (Store.LoadGame(g.Id) is { RunsExeRenderer: true, RunsExe: { } renderer } && renderer.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase)) return true;
        if (StartsRenderer(g)) return FollowRenderer(g, all, at);
        var named = all.Where(p => p.Exe.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        // two builds side by side (ACValhalla.exe, ACValhalla_Plus.exe): the recorder there loads into either
        var others = all.Where(p => !p.Exe.Equals(name, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(dir, p.Exe)) && !LauncherOrHelper(g, p.Exe)
            && Collections.Exes(g)?.Contains(p.Exe, StringComparer.OrdinalIgnoreCase) != false).ToList();   // a collection's other games' exes aren't this one's
        if (named.Count + others.Count == 0) return false;
        if (GameFiles.DetectAntiCheat(g, quick: true) != AntiCheat.None) return true;   // an update since the last scan may have added one
        // a proven follow gives way to the Shipping exe once it runs, asked all session long
        var proven = Store.LoadGame(g.Id) is { RunsExeProven: true, RunsExeFrom: { } from } && GameFiles.IsShipping(from) ? from : null;
        if (proven is { } shipping
            && all.Any(p => p.Exe.Equals(Path.GetFileName(shipping), StringComparison.OrdinalIgnoreCase) && shipping.Equals(ProcessPath(p.Pid), StringComparison.OrdinalIgnoreCase)))
        {
            Follow(g, shipping, at, proven: false);
            return true;
        }
        // inside the install only: never a staged warm, which runs from the data folder (OurCopy)
        // never a launcher beside an Unreal game (LeavesUnreal): asked again, it may start the game or another build later
        var paths = named.Select(p => ProcessPath(p.Pid)).OfType<string>().Where(p => GameFiles.Inside(g.InstallDir, p) && !LeavesUnreal(g.ExePath, p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0)
        {
            var beside = others.Select(p => ProcessPath(p.Pid)).OfType<string>()
                .Where(p => dir.Equals(Path.GetDirectoryName(p), StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            paths = beside.Where(p => !LeavesUnreal(g.ExePath, p)).ToList();
            // only one other exe beside the Shipping exe: it may be the game (FollowUnrealOther decides once the game exits),
            // in a run watched from its start only: one going at the app's first poll may have run the Shipping exe before
            if (paths.Count == 0 && beside is [var other] && _runs.ContainsKey(g.Id)) _unrealOther.TryAdd(g.Id, new UnrealOther(other, at));
        }
        if (paths.Contains(g.ExePath, StringComparer.OrdinalIgnoreCase)) return proven == null;   // as recorded
        if (paths is not [var runs]) return false;
        Follow(g, runs, at, proven: false);
        return true;
    }

    /// <summary><see cref="CarvedReader.StartsVulkanRenderer"/> of the game's exe, read once per run.</summary>
    bool StartsRenderer(Game g)
    {
        if (!_startsRenderer.TryGetValue(g.Id, out var starts)) _startsRenderer[g.Id] = starts = CarvedReader.StartsVulkanRenderer(g);
        return starts;
    }

    /// <summary>A started game whose exe starts its Vulkan renderer in another process (<see cref="StartsRenderer"/>): follows
    /// the one process that a process at the game's exe path started, through any chain of processes, whose image is below
    /// the exe's folder inside the install, isn't a helper and imports vulkan-1.dll, once the same process is seen at two
    /// polls in a row (a launcher's short Vulkan probe beside it isn't). Paths come from <see cref="ProcessPath"/>; only the
    /// game's own process tree is opened, for its creation times.</summary>
    /// <returns>True once followed or never to be (anti-cheat found). False: asked again at the next poll while the game runs.</returns>
    bool FollowRenderer(Game g, List<(int Pid, int Parent, string Exe)> all, DateTimeOffset at)
    {
        var name = Path.GetFileName(g.ExePath);
        var roots = all.Where(p => p.Exe.Equals(name, StringComparison.OrdinalIgnoreCase) && g.ExePath.Equals(ProcessPath(p.Pid), StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Pid).ToHashSet();
        var linked = new HashSet<int>(roots);
        for (var more = linked.Count > 0; more;)
        {
            more = false;
            foreach (var p in all) more |= linked.Contains(p.Parent) && linked.Add(p.Pid);
        }
        // creation times rule out a parent pid reused since: only the game's tree is opened for them, never an anti-cheat game's
        var created = all.Where(p => linked.Contains(p.Pid)).Select(p => (p.Pid, p.Parent, Created: ProcessTree.Created(p.Pid))).ToList();
        var tree = roots.SelectMany(r => ProcessTree.Tree(r, created)).ToHashSet();
        var renderers = all.Where(p => tree.Contains(p.Pid) && !roots.Contains(p.Pid)).Select(p => (p.Pid, Path: ProcessPath(p.Pid)))
            .Where(p => p.Path is { } path && GameFiles.Inside(Path.GetDirectoryName(g.ExePath)!, path) && !SameFolder(g.ExePath, path)
                        && GameFiles.Inside(g.InstallDir, path) && !GameFiles.IsHelper(path) && CarvedReader.ImportsVulkan(path)).ToList();
        if (renderers is not [var one])
        {
            _rendererSeen.Remove(g.Id);
            return false;
        }
        var seen = _rendererSeen.GetValueOrDefault(g.Id);
        _rendererSeen[g.Id] = (one.Pid, one.Path!);
        if (seen.Pid != one.Pid || !one.Path!.Equals(seen.Exe, StringComparison.OrdinalIgnoreCase)) return false;
        _rendererSeen.Remove(g.Id);
        if (GameFiles.DetectAntiCheat(g, quick: true) != AntiCheat.None) return true;   // an update since the last scan may have added one
        Follow(g, one.Path, at, proven: false, renderer: true);
        return true;
    }

    void Follow(Game g, string runs, DateTimeOffset at, bool proven, bool renderer = false)
    {
        var dir = Path.GetDirectoryName(g.ExePath)!;
        var rec = Store.LoadGame(g.Id);
        rec.RunsExeFrom = rec.RunsExe != null ? rec.RunsExeFrom : g.ExePath;   // discovery's, also when a followed exe is followed again
        (rec.RunsExe, rec.RunsExeBuild, rec.RunsExeAt, rec.RunsExeWas, rec.RunsExeProven, rec.RunsExeRenderer) = (runs, Build(g, runs), at, g.ExePath, proven, renderer);
        Store.SaveGame(g.Id, rec);
        var there = dir.Equals(Path.GetDirectoryName(runs), StringComparison.OrdinalIgnoreCase) ? "is set up for it" : "moves there";
        Log?.Report($"{g.Name}: runs {runs}, not {g.ExePath}: following it (the recorder {there} once the game exits)");
        RecorderLog($"{g.Name}: the game runs {runs}, not {g.ExePath}");
    }

    /// <summary>A playing Unreal game seen running only <see cref="Exe"/>, another exe of its Shipping exe's folder, from
    /// <see cref="At"/>: whether the Shipping exe ran too and whether that exe held driver-cache files (made a device).</summary>
    sealed class UnrealOther(string exe, DateTimeOffset at)
    {
        public string Exe { get; } = exe;
        public DateTimeOffset At { get; } = at;
        public bool ShippingRan { get; set; }
        public bool Device { get; set; }
        public DateTimeOffset Sampled { get; set; } = DateTimeOffset.MinValue;
    }

    /// <summary>Every poll of a playing game with an <see cref="UnrealOther"/>, until it exits: a launcher may start the
    /// Shipping exe later, after it exits itself. A refusal counts only within this run's window: a run already going at
    /// the app's first poll has none, and an older refusal is another launch's.</summary>
    void WatchUnrealOther(Game g, UnrealOther o, DateTimeOffset at)
    {
        if (o.ShippingRan) return;
        var name = Path.GetFileName(g.ExePath);
        if (Processes(false).Any(p => p.Exe.Equals(name, StringComparison.OrdinalIgnoreCase) && g.ExePath.Equals(ProcessPath(p.Pid), StringComparison.OrdinalIgnoreCase)))
            o.ShippingRan = true;
        else if (!o.Device && _runs.TryGetValue(g.Id, out var run) && RefusedFile(o.Exe) is var refusal && File.Exists(refusal)
                 && File.GetLastWriteTimeUtc(refusal) >= run.From.UtcDateTime)
            o.Device = true;
        else if (!o.Device && AppCache != null && at - o.Sampled >= AttributionInterval)
            (o.Sampled, o.Device) = (at, OpenKeys(g, Path.GetFileName(o.Exe)).Count > 0);
    }

    /// <summary>At the game's exit: an Unreal game whose Shipping exe never ran while another exe of its folder did, and made a
    /// device (it held driver-cache files, or the recorder refused it during the run), runs that exe: Jedi: Fallen Order's
    /// stores start starwarsjedifallenorder.exe beside SwGame-Win64-Shipping.exe. A launcher shows no device; one that runs
    /// first starts the Shipping exe.</summary>
    void FollowUnrealOther(Game g, UnrealOther o)
    {
        if (!GameFiles.IsShipping(g.ExePath) || !SameFolder(g.ExePath, o.Exe) || !File.Exists(o.Exe)) return;
        if (o.ShippingRan || !o.Device)
        {
            Log?.Report($"{g.Name}: ran {o.Exe}, not following it: " + (o.ShippingRan ? $"{Path.GetFileName(g.ExePath)} ran too" : "it made no device"));
            return;
        }
        Follow(g, o.Exe, o.At, proven: true);
    }

    /// <summary>A process name <paramref name="g"/>'s exe folder may hold that is never the game: what discovery never picks.</summary>
    static bool LauncherOrHelper(Game g, string exe) =>
        XboxSource.LauncherOrUtility(exe) || GameFiles.NotTheGameExe(g.InstallDir, Path.Combine(Path.GetDirectoryName(g.ExePath)!, exe));

    GameState Find(string gameId) => Games.FirstOrDefault(s => s.Game.Id == gameId)
                                     ?? throw new ArgumentException($"unknown game '{gameId}' (scan first)");

    /// <summary>A game the user added (by its exe or in a folder) gets another name; its id, which everything SCSKiller keeps
    /// of it is stored under, stays. InvalidOperationException for a store's game, ArgumentException for an empty name.</summary>
    public void RenameManualGame(string gameId, string name)
    {
        if (Find(gameId).Game.Store != Core.Store.Manual || Manual is not { } manual) throw new InvalidOperationException("only a game you added can be renamed");
        manual.Rename(gameId, name);
        GameState? now = null;
        lock (_lock)
            if (_games.FindIndex(x => x.Game.Id == gameId) is var i and >= 0)
                now = _games[i] = _games[i] with { Game = _games[i].Game with { Name = name.Trim() } };
        if (now != null) GameChanged?.Invoke(now);
        KeepList();
    }

    string RecordingPath(string gameId) => Path.Combine(Store.GameDir(gameId), "recording.db");

    /// <summary>A Vulkan game's recording (<see cref="VulkanPlan"/>), kept under <see cref="RecordingPath"/>'s lock.</summary>
    string FozPath(string gameId) => Path.Combine(Store.GameDir(gameId), VulkanPlan.RecordingFile);

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
            if (Foreign(dir)) return;
            var was = Store.LoadGame(added.Id);
            if (was.RecorderFiles.Count > 0 || was.RecorderChained != null || was.RecorderExe != null || ProxyOnDisk(added) || ProxyOnDisk(RecordedAt(added, was)))
            {
                TakeOutNow(added, "its store entry took it over", anyCopys: false);
                was = Store.LoadGame(added.Id);
                if (was.RecorderFiles.Count > 0 || was.RecorderChained != null || was.RecorderRollback || ProxyOnDisk(added) || ProxyOnDisk(RecordedAt(added, was))) return;
            }
            if (!RecorderDataFiles.Concat(Recordings.AppFiles).Append(ArmedFile).Append(LedgerStamp).Any(f => File.Exists(Path.Combine(dir, f))) || GameRunning(added)) return;
            var inbox = new FileInfo(Path.Combine(dir, "scskiller.db"));
            var unread = inbox is { Exists: true, Length: > 0 } && $"{inbox.Length}:{inbox.LastWriteTimeUtc.Ticks}" != was.RecordingInbox;
            Directory.CreateDirectory(Store.GameDir(owner.Id));
            if (RemoveRecorderData(dir, to, was.RecordingInbox, () => GameRunning(added), IniPending(Store, dir)))
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

    /// <summary>A Vulkan game's recording: SCSKiller's (recording.foz), then Steam's archives of the game where they are read
    /// (<see cref="SteamSources"/>), then the community's download (<see cref="VulkanInUse"/>), with the device and the cache
    /// folder its last launch had. Null: neither of the first two.</summary>
    Recording? VulkanRecording(Game g)
    {
        var ours = File.Exists(FozPath(g.Id)) ? FozPath(g.Id) : null;
        List<FozSource>? steam = null;
        SteamSources(g, ours, ref steam);
        // the community's pipelines name shader modules only an archive of this PC holds: never alone
        if (VulkanInUse(g.Id) != null && (ours != null || steam is { Count: > 0 }))
            (steam ??= [new(ours!, FozOrigin.Ours)]).Add(new(Path.Combine(Store.GameDir(g.Id), Community.VulkanFile), FozOrigin.Community));
        var rec = Store.LoadGame(g.Id);
        var r = steam is { Count: > 0 } ? new Recording(FozPath(g.Id)) { VulkanSources = steam } : ours == null ? null : new Recording(ours);
        return r == null ? null : r with { VulkanDevice = rec.VulkanDevice, VulkanSteamCache = VulkanPlan.CachePath(rec.VulkanCacheEnv, OwnDriver) != null };
    }

    /// <summary>Sets <paramref name="sources"/> to Steam's archives of the game's Vulkan pipelines, <paramref name="ours"/>
    /// first; left null where none are read.</summary>
    partial void SteamSources(Game g, string? ours, ref List<FozSource>? sources);

    /// <summary>Adds to a Vulkan game's check key what its Steam archives are now, where they are read (<see cref="SteamSources"/>),
    /// and to a DirectX game's under Proton (<see cref="SteamDxSources"/>).</summary>
    partial void SteamStamp(Game g, EngineInfo? engine, ref string key);

    /// <summary>Sets <paramref name="sources"/> to Steam's archives of a DirectX game's pipelines as vkd3d-proton or DXVK made
    /// them under Proton (Linux); left null where none are read.</summary>
    partial void SteamDxSources(Game g, EngineInfo engine, ref List<FozSource>? sources);

    List<FozSource>? SteamDx(Game g, EngineInfo engine)
    {
        List<FozSource>? sources = null;
        if (!VulkanPlan.IsVulkan(engine)) SteamDxSources(g, engine, ref sources);
        return sources;
    }

    /// <summary>The game page's line on the compiled cache SCSKiller keeps in Steam's own cache folder for the game; null
    /// when it keeps none there.</summary>
    public string? SteamCacheNote(string gameId)
    {
        string? note = null;
        SteamCacheNoteOf(gameId, ref note);
        return note;
    }

    /// <summary>Puts the game's compiled cache in Steam's folder for it once, or takes it out (<paramref name="remove"/>):
    /// whether that's done, the game page's line and what each file holds. InvalidOperationException for a game whose cache
    /// doesn't go there.</summary>
    public (bool Done, string Status, IReadOnlyList<string> Details) SteamPlaceNow(string gameId, bool remove, CancellationToken ct = default)
    {
        var s = Find(gameId);
        (bool, string, IReadOnlyList<string>)? report = null;
        SteamPlaceOnce(s, remove, ct, ref report);
        return report ?? throw new InvalidOperationException($"{s.Game.Name}: only a Steam game or a non-Steam game added to Steam that Proton runs, or a Vulkan one, on NVIDIA's Linux driver or on Mesa, gets its cache in Steam's folder");
    }

    partial void SteamCacheNoteOf(string gameId, ref string? note);

    /// <summary>Adds <see cref="Vkd3dPart"/>: vkd3d-proton's cache the compile writes in the game's data folder.</summary>
    partial void Vkd3dFiles(string gameId, List<CachePart> parts);

    /// <summary>What the game's compile cache folder may hold when the game reads its cache from elsewhere, so the folder's size
    /// never trims it (NVIDIA on Linux, <see cref="Vendors.NvidiaLinux.PlacedCacheSize"/>); unset: the vendor's.</summary>
    partial void CompileCacheCap(GameState s, ref long? cap);

    partial void SteamPlaceOnce(GameState s, bool remove, CancellationToken ct, ref (bool, string, IReadOnlyList<string>)? report);

    /// <summary>Starts what keeps the games' caches in Steam's folders, ending with <paramref name="ct"/>.</summary>
    partial void SteamPlaceStart(CancellationToken ct, ref Task? task);

    /// <summary>The game's cache in Steam's folder is looked at again: now after a compile or a delete, a while after the
    /// game <paramref name="exited"/>, as Steam merges its cache then.</summary>
    partial void SteamPlaceSoon(string gameId, bool exited);

    /// <summary>The game started: its cache in Steam's folder is looked at while it plays, which learns the file the game
    /// writes there.</summary>
    partial void SteamPlaceStarted(string gameId);

    /// <summary>The game's *.ushaderprecache files (<see cref="UnrealUserCache.PrecacheFilesOf"/>) under its own folder names
    /// only: a name another game also goes by may be that game's folder.</summary>
    IReadOnlyList<string> PrecacheFiles(Game g, string pattern = "*.ushaderprecache", bool noLinks = false)
    {
        var others = Games.Where(o => o.Game.Id != g.Id).SelectMany(o => UnrealUserCache.Folders(o.Game)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return UnrealUserCache.PrecacheFilesOf(g, [.. SavedRoots(g), ProgramData], UnrealUserCache.Folders(g).Where(n => !others.Contains(n)), pattern, noLinks);
    }

    /// <summary>The PSO disk caches an Unreal 4 game's D3D12 RHI wrote on this PC (<see cref="PsoDiskCache"/>, not its driver
    /// blob file); none for another engine.</summary>
    IReadOnlyList<string> PsoDiskCaches(Game g, EngineInfo? engine)
    {
        if (engine?.Family != "Unreal" || !engine.Version.StartsWith('4')) return [];
        try
        {
            return [.. PrecacheFiles(g, noLinks: true).Concat(PrecacheFiles(g, "*.upsoprecache", noLinks: true))
                .Where(f => Path.GetFileName(f) is var n && (n.Contains("D3DGraphics", StringComparison.OrdinalIgnoreCase) || n.Contains("D3DCompute", StringComparison.OrdinalIgnoreCase))
                    && PsoDiskCache.Is(f))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }   // a folder gone while listed: no path in a log
    }

    /// <summary>The scan's key part for <see cref="PsoDiskCaches"/>: each file's name, size and write time.</summary>
    string PsoDiskCacheStamp(Game g, EngineInfo? engine) => string.Concat(PsoDiskCaches(g, engine).Select(f =>
    {
        try { return new FileInfo(f) is var i ? $"|pso:{i.Name}:{i.Length}:{i.LastWriteTimeUtc.Ticks}" : ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }   // gone since it was found
    }));

    /// <summary>The stamp of the PSO disk caches a plan of the game takes (<see cref="PsoDiskCacheRecording"/>): only a game
    /// this PC never recorded has them in its plan. Null: none taken.</summary>
    string? PsoCacheInUse(Game g, EngineInfo? engine) =>
        File.Exists(RecordingPath(g.Id)) || PsoDiskCacheStamp(g, engine) is not { Length: > 0 } stamp ? null : stamp;

    /// <summary>Holds the <see cref="PsoCacheInUse"/> stamp of files none of whose pipelines is this install's: the game stays
    /// asking for a recording until they change.</summary>
    string PsoCacheUnusedPath(string gameId) => Path.Combine(Store.GameDir(gameId), "psocache.unused");

    /// <summary>The planner's check on the union of this PC's recording, the community database's and the official one,
    /// without writing it: the union has draws when any has.</summary>
    PlanCheck CheckRecordings(Game g, EngineInfo engine)
    {
        if (VulkanPlan.IsVulkan(engine)) return _planner.Check(g, engine, VulkanRecording(g), Vendor.Caps);
        var steam = SteamDx(g, engine);
        var recordings = new[] { RecordingPath(g.Id) }.Concat(CarvedReader.RecordedOnly(engine) ? [] : Downloads(g.Id)).Where(File.Exists).Select(f => new Recording(f) { VulkanSources = steam }).ToList();
        var check = _planner.Check(g, engine, recordings.FirstOrDefault() ?? (steam == null ? null : new Recording(RecordingPath(g.Id)) { VulkanSources = steam }), Vendor.Caps);
        foreach (var r in recordings.Skip(1))
            if (check.Readiness == Readiness.NeedsRecording) check = _planner.Check(g, engine, r, Vendor.Caps);
        // the game's own PSO disk cache stands in for a recording: every pipeline it created, with its state
        if (check.Readiness == Readiness.NeedsRecording && RootSig.RuleFor(engine) != null && PsoCacheInUse(g, engine) is { } stamp
            && !(File.Exists(PsoCacheUnusedPath(g.Id)) && File.ReadAllText(PsoCacheUnusedPath(g.Id)) == stamp))
            check = new(Readiness.Ready, Planner.OwnPipelineCache);
        return check;
    }

    /// <summary>The recording to plan and warm from, as a proxy db in <paramref name="work"/>: this PC's, merged with the
    /// community database's and the official one when in use, with every shader it names by hash read
    /// back from the install (the game's index, then the middleware DLLs next to its exe). Null = no recording.</summary>
    /// <param name="rayTracing">false: the ray tracing state objects are left out (<see cref="RayTracing"/>)</param>
    /// <param name="every">Maximum mode: every texture filtering setting the recordings have (<see cref="ForeignVariants"/>)</param>
    Recording? PrepareRecording(Game game, EngineInfo engine, ShaderIndex index, string work, bool rayTracing, bool every, CancellationToken ct)
    {
        if (VulkanPlan.IsVulkan(engine)) return VulkanRecording(game);   // read as is: imports only append
        var local = RecordingPath(game.Id);
        var downloads = CarvedReader.RecordedOnly(engine) ? [] : Downloads(game.Id);   // hash-only: no install to read their shaders from
        var steam = SteamDx(game, engine);
        if (!File.Exists(local) && PsoDiskCacheRecording(game, engine, index, work, ct) is { } own) downloads.Add(own);
        if (!File.Exists(local) && downloads.Count == 0) return steam == null ? null : new Recording(local) { VulkanSources = steam };
        Directory.CreateDirectory(work);
        var src = local;
        var foreign = ForeignVariants(game, every, engine);
        if (downloads.Count > 0 && Community.Union(local, downloads, src = Path.Combine(work, "merged.db"), foreign) is > 0 and var bad)
            Log?.Report($"{game.Name}: {bad:N0} downloaded shader or root signature blobs don't match their hash: left out, this PC's own copies kept");
        // each kept setting copies every generated PSO: two of a recording's settings outside Maximum mode, three in it
        var filtering = SamplerVariants.Ordered(File.Exists(local) ? local : src) is { } sets ? (sets, every ? 3 : 2) : ((IReadOnlyList<string>, int)?)null;
        if (foreign.Count > 0) Log?.Report($"{game.Name}: {foreign.Count:N0} community recording pipelines made at another texture filtering setting than the one compiled left out"
            + (every || File.Exists(local) ? "" : " (Maximum mode compiles them)"));
        var dx11 = Planner.D3D11Decided(engine);
        var otherVendors = Planner.OtherVendors(engine, index, Vendor.Caps, src);
        var leftVendor = 0;
        if (!rayTracing || dx11 || otherVendors != null)
        {
            var kept = Path.Combine(work, "kept.db");
            long left = 0, left11 = 0;
            using (var f = new BufferedStream(File.Create(kept), 1 << 20))
                foreach (var x in PsoDb.Read(src))
                    if (dx11 && Planner.LeftOutOnD3D11(x)) left11 += x.Tag is 'G' or 'C' or 'S' || PsoDb.IsStateObject(x.Tag) ? 1 : 0;
                    else if (!rayTracing && Planner.LeftOutWithoutRt(x)) left++;
                    else if (otherVendors?.Invoke(x) == true) leftVendor++;
                    else PsoDb.Write(f, x.Tag, x.Payload);
            if (src != local) File.Delete(src);
            src = kept;
            if (left > 0) Log?.Report($"{game.Name}: {RtSkippedNote}, {left:N0} recorded ray tracing state objects left out");
            if (left11 > 0) Log?.Report($"{game.Name}: compiled for DirectX 11, {left11:N0} recorded DirectX 12 pipelines left out");
            if (leftVendor > 0) Log?.Report($"{game.Name}: {leftVendor:N0} recorded pipelines another GPU vendor's game creates left out");
        }
        var db = Path.Combine(work, "recording.db");
        var r = Rehydrate.Run(src, db, game, engine, _reader, index, null, ct, h => Middleware.Blobs(game, h), Planner.Steps.Value);
        if (src != local) File.Delete(src);
        var dir = Store.GameDir(game.Id);
        if (CommunityInUse(game.Id) is { } c && !string.Equals(c.ContentHash, index.ContentHash, StringComparison.OrdinalIgnoreCase)
            && MatchesNothing(Path.Combine(dir, "community.db"), foreign, rayTracing, r.Missing))
        {
            try { Community.Write(Path.Combine(dir, "community.json"), c with { MatchesNothingIn = index.ContentHash }); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // still in use: its pipelines are skipped as before
            if (CommunityInUse(game.Id) == null)
            {
                Log?.Report($"{game.Name}: the community recording has none of this install's shaders (another version of the game): not used");
                return PrepareRecording(game, engine, index, work, rayTracing, every, ct);
            }
        }
        if (r.Found > 0 || !r.Complete)
            Log?.Report($"{game.Name}: recording rehydrated from the install ({r.Found} shaders"
                + (r.FoundInMiddleware > 0 ? $", {r.FoundInMiddleware} of them from middleware DLLs" : "")
                + (r.Complete ? ")" : $", {r.Missing.Count} not in this install: those PSOs are skipped)"));
        return new Recording(db) { VulkanSources = steam, Filtering = filtering, OtherVendors = leftVendor, CommunityStateObjects = rayTracing && !dx11 && Planner.RtCollectionCache(Vendor.Caps) ? CommunityOnlyStateObjects(game.Id, local) : null };
    }

    /// <summary>The game's PSO disk caches (<see cref="PsoDiskCaches"/>) written as a recording in <paramref name="work"/>, for a
    /// game this PC never recorded; null when there are none or none of their PSOs is this install's. Never shared: sharing
    /// takes recording.db alone.</summary>
    string? PsoDiskCacheRecording(Game game, EngineInfo engine, ShaderIndex index, string work, CancellationToken ct)
    {
        if (PsoCacheInUse(game, engine) is not { } stamp || PsoDiskCaches(game, engine) is not { Count: > 0 } files || RootSig.RuleFor(engine) is not { } rule) return null;
        Directory.CreateDirectory(work);
        var db = Path.Combine(work, "psocache.db");
        var w = PsoDiskCache.ToRecording(files, index, engine, rule, Vendor.Caps.Profile.StartsWith("nvidia"), db, ct);
        Log?.Report($"{game.Name}: the game's own pipeline cache: {w.Graphics:N0} graphics and {w.Compute:N0} compute pipelines"
            + (w.NotInIndex + w.NoRootSignature + w.Rejected + w.UnreadableFiles > 0 ? $", {w.NotInIndex:N0} with a shader not in this install, {w.NoRootSignature:N0} without a root signature, {w.Rejected:N0} unreadable left out"
                + (w.UnreadableFiles > 0 ? $", {w.UnreadableFiles} of its files unreadable" : "") : ""));
        var unused = PsoCacheUnusedPath(game.Id);
        try
        {
            if (w.Graphics > 0) File.Delete(unused);
            else File.WriteAllText(unused, stamp);   // compute alone plans nothing a vendor needs a recording for
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (w.Graphics + w.Compute > 0) return db;
        File.Delete(db);
        return null;
    }

    /// <summary>The community recording's state objects that neither this PC's recording nor the official one has.</summary>
    HashSet<string>? CommunityOnlyStateObjects(string gameId, string local)
    {
        if (CommunityInUse(gameId) == null) return null;
        var dir = Store.GameDir(gameId);
        var others = new HashSet<string>();
        foreach (var f in new[] { local, OfficialInUse(gameId) != null ? Path.Combine(dir, "official.db") : "" }.Where(File.Exists))
            foreach (var x in PsoDb.Read(f))
                if (PsoDb.IsStateObject(x.Tag)) others.Add(x.Key);
        return PsoDb.Read(Path.Combine(dir, "community.db")).Where(x => PsoDb.IsStateObject(x.Tag) && !others.Contains(x.Key)).Select(x => x.Key).ToHashSet();
    }

    /// <summary>Every shader the community recording's records in the union name, less the blobs it carries itself, is one
    /// <paramref name="missing"/> from the rehydrated union: none came from the install, its middleware DLLs or this PC's recording.</summary>
    static bool MatchesNothing(string communityDb, IReadOnlySet<string> leftOut, bool rayTracing, IReadOnlyList<string> missing)
    {
        var own = PsoDb.Read(communityDb).Where(x => x.Tag == 'B').Select(x => PsoDb.Hex(x.Payload.AsSpan(0, 20))).ToHashSet();
        var named = Rehydrate.References(PsoDb.Read(communityDb).Where(x => !leftOut.Contains(x.Key) && (rayTracing || !Planner.LeftOutWithoutRt(x))));
        named.ExceptWith(own);
        return named.Count > 0 && named.IsSubsetOf(missing);
    }

    /// <summary>The community recording downloaded for the game while the setting is on, whether this install matches it or not.</summary>
    CommunityDownload? CommunityKept(string gameId) =>
        Settings.UseCommunityDb && UsesShared(gameId) && Community.Downloaded(Store.GameDir(gameId)) is { } d && (!d.Full || FullEntries) ? d : null;

    /// <summary><see cref="CommunityKept"/> unless it was made for another build and has none of the installed build's shaders,
    /// or was made for an index without shaders (<see cref="OtherVersion"/>):
    /// every plan, warm, pending count and texture filtering choice reads the downloads through it.</summary>
    CommunityDownload? CommunityInUse(string gameId) => CommunityKept(gameId) is { } d && !OtherVersion(d, gameId) ? d : null;

    bool OtherVersion(CommunityDownload d, string gameId, GameRecord? rec = null) => ShaderIndex.IsEmpty(d.ContentHash)
        || d.MatchesNothingIn is { } hash && hash == (rec ?? Store.LoadGame(gameId)).IndexContentHash;

    readonly ConcurrentDictionary<string, (long, DateTime)> _drawsChecked = new(StringComparer.OrdinalIgnoreCase);   // draws.db read whole since its last write

    /// <summary>The downloaded draws entry while it reads as one (<see cref="Community.Check"/>); a damaged copy is deleted,
    /// so planning goes on with the other recordings and the next sync downloads it again.</summary>
    CommunityDownload? DrawsInUse(string gameId)
    {
        var dir = Store.GameDir(gameId);
        if (!Settings.UseCommunityDb || !UsesShared(gameId) || Community.DownloadedDraws(dir) is not { } d) return null;
        var db = new FileInfo(Path.Combine(dir, "draws.db"));
        try
        {
            var stamp = (db.Length, db.LastWriteTimeUtc);
            if (_drawsChecked.TryGetValue(db.FullName, out var ok) && ok == stamp) return d;
            if (Community.Check(File.ReadAllBytes(db.FullName), draws: true) != null)
            {
                _drawsChecked[db.FullName] = stamp;
                return d;
            }
            Log?.Report($"{Games.FirstOrDefault(s => s.Game.Id == gameId)?.Game.Name ?? gameId}: the downloaded DirectX 11 layouts were damaged and are deleted; they download again at the next sync");
            File.Delete(Path.Combine(dir, "draws.json"));
            File.Delete(db.FullName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>What a plan's <see cref="GameRecord.PlanCommunity"/> names: the community recording's object, and the D3D11 draws'.</summary>
    string? CommunityObjects(string gameId) => (CommunityInUse(gameId)?.Object, DrawsInUse(gameId)?.Object, VulkanInUse(gameId)?.Object) switch
    {
        (var c, null, null) => c,
        (var c, var d, null) => $"{c}|draws:{d}",
        (var c, var d, var v) => $"{c}{(d != null ? "|draws:" + d : "")}|vulkan:{v}",
    };

    /// <summary>The Vulkan entry downloaded for the game while the setting is on.</summary>
    CommunityDownload? VulkanInUse(string gameId) => Settings.UseCommunityDb && UsesShared(gameId) ? Community.DownloadedVulkan(Store.GameDir(gameId)) : null;

    bool ScannedVulkan(Game g)
    {
        lock (_scanLock) return _scan?.GetValueOrDefault(g.Id)?.Engine is { } e && VulkanPlan.IsVulkan(e);
    }

    /// <summary>The official recording downloaded for the game, used as <see cref="OfficialAllowed"/> says.</summary>
    CommunityDownload? OfficialInUse(string gameId) =>
        UsesShared(gameId) && Community.DownloadedOfficial(Store.GameDir(gameId)) is { } d && OfficialAllowed(d.Visibility) ? d : null;

    readonly ConcurrentDictionary<string, (DateTime At, bool Uses)> _usesShared = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The game's "Use shared shader hashes" (<see cref="GameRecord.NoSharedHashes"/>), read again whenever its record changed.</summary>
    bool UsesShared(string gameId)
    {
        var at = File.GetLastWriteTimeUtc(Path.Combine(Store.GameDir(gameId), "state.json"));
        if (_usesShared.TryGetValue(gameId, out var known) && known.At == at) return known.Uses;
        var uses = !Store.LoadGame(gameId).NoSharedHashes;
        _usesShared[gameId] = (at, uses);
        return uses;
    }

    /// <summary>With Settings.UseCommunityDb, or one for everyone while the user isn't entitled to "db": the setting is
    /// shown off and can't be changed without it.</summary>
    bool OfficialAllowed(OfficialVisibility? v) => Settings.UseCommunityDb || v == OfficialVisibility.Everyone && !DbEntitled();

    /// <summary>The downloaded recordings in use: community.db, official.db, then draws.db.</summary>
    List<string> Downloads(string gameId) =>
        [.. CommunityInUse(gameId) != null ? [Path.Combine(Store.GameDir(gameId), "community.db")] : Array.Empty<string>(),
         .. OfficialInUse(gameId) != null ? [Path.Combine(Store.GameDir(gameId), "official.db")] : Array.Empty<string>(),
         .. DrawsInUse(gameId) != null ? [Path.Combine(Store.GameDir(gameId), "draws.db")] : Array.Empty<string>()];

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
    /// Settings.UseCommunityDb (or without "db", for the official recordings for everyone) the manifest, then a download for every
    /// game whose build has an entry or official recording other than the one it has; each game that got one, or whose entry
    /// the manifest check changed, is re-evaluated. Never throws.</summary>
    async Task SyncCommunity(IReadOnlyList<Game> games)
    {
        try
        {
            foreach (var g in await SyncPacks(games, CancellationToken.None)) Refresh(g);
            if (!Settings.UseCommunityDb && DbEntitled()) return;
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
        var dlls = games.Where(g => UsesShared(g.Id)).SelectMany(g => Dlls(g).Where(d => d.Packable && Middleware.SharedVendors.Contains(d.Vendor)).Select(d => (Game: g, Dll: d))).ToList();
        // no DLL is hashed unless the manifest lists a pack at all
        if (dlls.Count == 0 || await community.ManifestAsync(ct) is not { HasPacks: true } manifest) return changed;
        var keyed = dlls.Select(x => (x.Game, x.Dll, Image: Middleware.TryScan(x.Dll.Path))).Where(x => x.Image != null)
            .Select(x => (x.Game, x.Dll, x.Image, Key: HashOnly.PackKey(gpu, x.Dll.Vendor, x.Dll.Name, x.Image!.ContentHash))).ToList();
        foreach (var pack in keyed.GroupBy(x => x.Key))
        {
            var (_, d, image, key) = pack.First();
            if (manifest.FindPack(key) is not { } e || shared.Load(d, image!)?.Header.Object == e.Object) continue;
            var users = pack.Select(x => x.Game).DistinctBy(g => g.Id).ToList();
            if (!users.Any(g => UsesShared(g.Id))) continue;   // switched off meanwhile
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

    /// <summary>The inputs a compile would take now (<see cref="WarmInputs"/>): the recording, the community and official
    /// recordings while in use, the plan, and for a D3D12 game the entries of its DLLs' packs this GPU runs. Cached on all of them.</summary>
    /// <param name="e">the evaluation's engine; null = the kept scan's</param>
    HashSet<string> InputsNow(Game g, GameRecord r, EngineInfo? e = null)
    {
        var (inputs, extra) = InputFiles(g, r, e);
        return KeyFiles.Derived(Store.GameDir(g.Id), inputs, extra, () =>
            WarmInputs.Of(RecordedNow(g, r, e: e), r.Plan?.FilePath, SeedsNow(g).SelectMany(s => s.Entries), Elsewhere(g, r)));
    }

    /// <summary>The files <see cref="InputsNow"/> derives from, and what else it depends on.</summary>
    (string?[] Inputs, string Extra) InputFiles(Game g, GameRecord r, EngineInfo? e = null)
    {
        var dir = Store.GameDir(g.Id);
        var dlls = Dlls(g).Where(d => d.Packable).ToList();
        var packs = _planner is Planner p && SeedsPacks(g) ? new[] { p.Packs, UsesShared(g.Id) ? p.SharedPacks : null }.OfType<MiddlewarePacks>().ToList() : [];
        var packFiles = packs.SelectMany(x => dlls.Where(d => Directory.Exists(Path.Combine(x.Dir, d.Vendor)))
            .Select(d => Middleware.TryScan(d.Path) is { } i ? x.PathOf(d.Vendor, d.Name, i.ContentHash) : null));
        return ([RecordingPath(g.Id), .. Downloads(g.Id), r.Plan?.FilePath, Path.Combine(dir, Sharing.ShippedFile), .. dlls.Select(d => d.Path), .. packFiles],
            $"{IndexIsInstalled(g, r)}|{r.IndexContentHash}|{OnAmd}|{Vendor.Vendor}|{(RayTracing(r) ? "" : "no-rt")}{(D3D11Decided(g.Id, e) ? "|dx11" : "")}{(Maximum(r) ? "|max" : "")}{(Filtering(g.Id, Maximum(r), e) is { } af ? "|af" + af : "")}");
    }

    /// <summary>What the packs of the DLLs next to the game's exe seed on this GPU (<see cref="PackSeeds"/>).</summary>
    List<(List<PsoDb.Rec> Entries, List<string> RootSignatures)> SeedsNow(Game g) =>
        _planner is Planner p && SeedsPacks(g)
            ? [.. new[] { p.Packs, UsesShared(g.Id) ? p.SharedPacks : null }.OfType<MiddlewarePacks>().SelectMany(x => Dlls(g).Where(d => d.Packable).Select(d => PackSeeds(x, d)))]
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
    /// <param name="rayTracing">the compile's <see cref="GameRecord.PlanRayTracing"/>; null = <see cref="RayTracing"/> now</param>
    /// <param name="every">the compile's Maximum mode; null = <see cref="Maximum"/> now</param>
    /// <param name="e">the compile's engine; null = the kept scan's</param>
    WarmInputs.Recorded RecordedNow(Game g, GameRecord r, bool? rayTracing = null, bool? every = null, EngineInfo? e = null) =>
        WarmInputs.Recorded.Read([RecordingPath(g.Id), .. Downloads(g.Id)], Elsewhere(g, r), OwnDriver == GpuVendor.Nvidia, ForeignVariants(g, every ?? Maximum(r), e),
            rayTracing ?? RayTracing(r), D3D11Decided(g.Id, e));

    /// <summary><see cref="Planner.D3D11Decided"/> for the game. <paramref name="e"/>: null = the kept scan's engine.</summary>
    bool D3D11Decided(string id, EngineInfo? e)
    {
        if (e == null) lock (_scanLock) e = (_scan ??= Store.LoadScan()).GetValueOrDefault(id)?.Engine;
        return e != null && Planner.D3D11Decided(e);
    }

    /// <summary>The downloaded recordings' records made at another texture filtering setting than the one a compile takes
    /// (<see cref="SamplerVariants.Foreign"/>): neither compiled nor counted. The setting is this PC's recording's, else the
    /// downloads' most common at the player's texture filtering (<see cref="Filtering"/>), else theirs; with <paramref name="every"/>
    /// (Maximum mode) every one without this PC's recording. <paramref name="e"/>: the compile's engine; null = the kept scan's.</summary>
    HashSet<string> ForeignVariants(Game g, bool every, EngineInfo? e = null)
    {
        var (local, downloads, af) = (RecordingPath(g.Id), Downloads(g.Id), Filtering(g.Id, every, e));
        return downloads.Count == 0 || every && !File.Exists(local) ? []
            : KeyFiles.Derived(Store.GameDir(g.Id) + (every ? "|variants-every" : "|variants") + af, [local, .. downloads], "", () => SamplerVariants.Foreign(local, downloads, every, af));
    }

    /// <summary>The player's texture filtering (<see cref="EngineInfo.MaxAnisotropy"/>) that picks the downloads' setting a
    /// compile takes, and Snowdrop's rule's samplers; null when none is picked by it: not known, no downloads, or Maximum mode (<paramref name="every"/>).
    /// <paramref name="e"/>: null = the kept scan's engine.</summary>
    int? Filtering(string id, bool every, EngineInfo? e = null)
    {
        if (every) return null;
        if (e == null) lock (_scanLock) e = (_scan ??= Store.LoadScan()).GetValueOrDefault(id)?.Engine;
        return e?.Family == Snowdrop.SnowdropReader.Family || Downloads(id).Count > 0 || File.Exists(RecordingPath(id)) ? e?.MaxAnisotropy : null;   // Snowdrop's rule builds its own samplers with it
    }

    public const string PlanGrewReason = "This plan is much larger than your last compile of this game.";

    /// <summary>The plan is more than <see cref="PlanGrowth"/> times the last complete compile's of the same build.</summary>
    static bool PlanGrew(GameRecord r) => r.WarmedPlanCount is > 0 and var w && r.WarmedIndexHash == r.IndexContentHash && PlanItems(r.Plan?.Stats) > PlanGrowth * w;

    /// <summary>A new plan more than this many times the last complete compile's of the same build logs its inputs (<see cref="LogPlanInputs"/>).</summary>
    public const int PlanGrowth = 5;

    /// <summary>What a plan <see cref="PlanGrowth"/> times the last complete compile's (<paramref name="was"/> pipelines) was
    /// built from, in app.log lines naming the game, which a problem report carries: its counts, this PC's recording, the
    /// downloads in use and the game's data folder.</summary>
    void LogPlanInputs(Game g, Plan plan, long was)
    {
        var p = plan.Stats;
        static string N(long? n) => n is { } v ? v.ToString("N0") : "-";
        // each line within what a problem report keeps of it, after app.log's time stamp
        void Lines(string head, IEnumerable<string> items)
        {
            var (prefix, line) = ($"{g.Name}: {head}: ", "");
            foreach (var item in items)
            {
                if (line != "" && prefix.Length + line.Length + 2 + item.Length > App.ProblemReport.MaxLineChars - 25) { Log?.Report(prefix + line); line = ""; }
                line += (line == "" ? "" : ", ") + item;
            }
            if (line != "") Log?.Report(prefix + line);
        }
        Log?.Report($"{g.Name}: this plan has {N(PlanItems(p))} pipelines, more than {PlanGrowth} times the {N(was)} of the last complete compile of this build. What it was planned from:");
        Lines("plan", [$"{N(p.StageSets)} stage sets", $"{N(p.RootSignatures)} root signatures", $"vertex layouts from a recording {p.LayoutCoverage:P0}",
            $"stage units exact/inferred/guessed {N(p.ExactUnits)}/{N(p.InferredUnits)}/{N(p.GuessedUnits)}", $"{N(p.Variants)} again at other texture filtering settings",
            $"{N(p.Recorded)} recorded", $"{N(p.Generated)} generated", $"{N(p.SynthesizedTemplates)} synthesized templates", $"{N(p.MiddlewareItems)} middleware", $"{N(p.D3D11Shaders)} DirectX 11"]);
        try
        {
            var local = new FileInfo(RecordingPath(g.Id));
            Lines("plan inputs", [local.Exists ? $"recording.db {local.Length:N0} B, {N(PsoDb.Read(local.FullName).LongCount(r => r.Tag != 'B'))} records" : "recording.db none",
                $"community {CommunityInUse(g.Id)?.Object ?? "not in use"}", $"official {OfficialInUse(g.Id)?.Object ?? "not in use"}", $"DirectX 11 layouts {DrawsInUse(g.Id)?.Object ?? "not in use"}"]);
            var dir = new DirectoryInfo(Store.GameDir(g.Id));
            if (dir.Exists)
                Lines("data folder", dir.EnumerateFileSystemInfos().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList()
                    .Select(f => $"{f.Name} {(f is FileInfo fi ? $"{fi.Length:N0} B" : "folder")} {f.LastWriteTimeUtc:yyyy-MM-dd HH:mm}Z"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { Log?.Report($"{g.Name}: plan inputs not readable: {e.Message}"); }
    }

    /// <summary>The keys of what hung or faulted this driver in an earlier compile, for the warm to skip. Another driver's are
    /// dropped: this compile tries each again, and the ones that crash again are kept for this driver. An unknown driver skips none.</summary>
    IReadOnlyCollection<string>? KnownKeysFor(GameRecord r, string? driverId, bool vulkan, string name)
    {
        if (driverId == null || vulkan || r.KnownKeys.Count == 0) return null;
        if (r.KnownKeysDriver == driverId) return [.. r.KnownKeys];
        Log?.Report($"{name}: trying again the {r.KnownKeys.Count:N0} pipelines that crashed or hung another driver");
        (r.KnownKeys, r.KnownKeysDriver) = ([], null);
        return null;
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

    /// <param name="e">the game's engine: what a plan built with other rules than it gets now made counts neither as planned
    /// (null) nor as recorded, so rules B's plan never counts against a warm of rules A in use again; null: only the
    /// planner version counts</param>
    Pending PendingOf(Game g, GameRecord r, EngineInfo? e)
    {
        if (r.WarmedAt == null) return new(0, null);
        var dir = Store.GameDir(g.Id);
        var key = PendingStamp(g, r, e);   // before anything is read: what changes meanwhile is counted again next time
        if (key != null && r.Pending is { } known && known.Key == key) return new(known.Recorded, known.Planned, known.Unknown, known.Upscaler);
        var warmed = r.WarmKeysFile is { } wf ? KeyFiles.Set(Path.Combine(dir, wf)) : null;
        var made = r.PlanKeysFile is { } pf ? KeyFiles.Set(Path.Combine(dir, pf)) : null;
        var crash = CrashInputs(CrashKeysNow(r), r.Plan?.FilePath);
        var inputs = InputsNow(g, r, e);
        var fresh = WarmInputs.Fresh(inputs, warmed, crash.Contains);
        var seeds = SeedsNow(g).SelectMany(x => x.Entries).ToList();
        if (warmed != null && fresh.Count > 0 && OwnDriver == GpuVendor.Nvidia)
            fresh = [.. fresh.Except(WarmInputs.StagesTaken(r.Plan?.FilePath, seeds, fresh.ToHashSet(), warmed))];
        var packs = seeds.Select(e => e.Key)
            .Concat(Planner.PlanBody(r.Plan?.FilePath).Where(x => x.Tag == 'M').Select(x => MiddlewarePacks.Unwrap(x).Entry.Key)).ToHashSet();
        var one = fresh.Count > 0 ? PipelineKeys(g, r, e) : [];
        string P(string k) => one.GetValueOrDefault(k, k);   // a pipeline at two texture filtering settings is one
        var news = fresh.Where(k => made?.Contains(k) != true).ToList();
        var pending = new Pending(news.Select(P).Distinct().Count(),
            made == null || !CurrentPlanner(r.PlanVersion, r.PlanRules, r.PlanReaderVersion, e) ? null : fresh.Where(made.Contains).Select(P).Distinct().Count(), warmed == null,
            news.Where(packs.Contains).Select(P).Distinct().Count());
        // none: maybe a read that failed; a warm key file there but unread may be one too, counted again next time
        if (key != null && inputs.Count > 0 && !(warmed == null && r.WarmKeysFile is { } kf && File.Exists(Path.Combine(dir, kf))))
            r.Pending = new(key, pending.Recorded, pending.Planned, pending.Unknown, pending.Upscaler);
        return pending;
    }

    /// <summary>The records of the recordings (less <see cref="ForeignVariants"/>) and the plan that have static samplers, each
    /// to its <see cref="SamplerVariants.Pipeline"/> key, and a plan's NVAPI state record to its pipeline's with that state:
    /// the inputs a pipeline has at each texture filtering setting, as one.</summary>
    Dictionary<string, string> PipelineKeys(Game g, GameRecord r, EngineInfo? engine)
    {
        var foreign = ForeignVariants(g, Maximum(r), engine);
        var (records, nvs, blobs) = (new List<PsoDb.Rec>(), new List<PsoDb.Rec>(), new Dictionary<string, byte[]>());
        try
        {
            foreach (var x in Downloads(g.Id).Prepend(RecordingPath(g.Id))
                         .Where(File.Exists).SelectMany(f => PsoDb.Read(f)).Concat(Planner.PlanBody(r.Plan?.FilePath)))
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
    /// whether the plan's planner and rules are current (<paramref name="e"/>); null when one can't be read.</summary>
    string? PendingStamp(Game g, GameRecord r, EngineInfo? e)
    {
        var dir = Store.GameDir(g.Id);
        var (inputs, extra) = InputFiles(g, r, e);
        try
        {
            return KeyFiles.DerivedStamp([.. inputs, r.WarmKeysFile is { } wf ? Path.Combine(dir, wf) : null, r.PlanKeysFile is { } pf ? Path.Combine(dir, pf) : null],
                $"{extra}|{string.Join(',', CrashKeysNow(r).Order())}|{CurrentPlanner(r.PlanVersion, r.PlanRules, r.PlanReaderVersion, e)}|planned-units");   // the rule's mark: a count stored under another is redone
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { return null; }
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
        var rec = Store.LoadGame(g.Id);
        var m = rec.NoSharedHashes ? null : LocalManifest();
        var e = m != null ? DbEntry(m, g, rec, FullEntries) : null;
        return s.InCommunityDb != (m != null ? e != null : null) || s.CommunityDbPsos != (e?.Psos ?? 0);
    }

    readonly HashSet<string> _restateFailed = [];   // games whose failed visibility update was reported

    /// <summary>A download a sync held for the game's compile (<see cref="HoldDownload"/>): its object, its cut (<see
    /// cref="CommunityEntry.Full"/>) and the replace that writes it.</summary>
    sealed record HeldDownload(string Object, bool Full, Action Replace);

    /// <summary>The games whose compile has read or is about to read its downloads, each with the last download a sync held
    /// meanwhile per file ("community", "official", "draws").</summary>
    readonly Dictionary<string, Dictionary<string, HeldDownload>> _heldDownloads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the game's compile reads its downloads: <paramref name="replace"/> runs when it ends (<see
    /// cref="ApplyHeld"/>), instead of one held before for the same file.</summary>
    bool HoldDownload(string id, string file, string obj, bool full, Action replace)
    {
        lock (_heldDownloads)
        {
            if (!_heldDownloads.TryGetValue(id, out var held)) return false;
            held[file] = new(obj, full, replace);
            return true;
        }
    }

    /// <summary>The download held for the game's <paramref name="file"/>, if any.</summary>
    HeldDownload? Held(string id, string file)
    {
        lock (_heldDownloads) return _heldDownloads.TryGetValue(id, out var held) ? held.GetValueOrDefault(file) : null;
    }

    /// <summary>The game no longer compiling: its held downloads, taken out (none: an empty list).</summary>
    List<HeldDownload> Unregister(string id)
    {
        lock (_heldDownloads) return _heldDownloads.Remove(id, out var held) ? [.. held.Values] : [];
    }

    /// <summary>The downloads held while the game compiled, written now and the game refreshed. Under the recording lock,
    /// taken before the game stops holding: a sync's later download, held meanwhile or written after, is never overwritten by
    /// an older one. After a stop, not while the lock is held elsewhere (no shutdown waits), and a stop during the wait ends
    /// it: dropped, the next sync downloads them again. Whatever fails, the game never stays registered.</summary>
    void ApplyHeld(Game g, CancellationToken ct)
    {
        List<HeldDownload>? held = null;
        try
        {
            lock (_heldDownloads)
                if (!_heldDownloads.TryGetValue(g.Id, out var any) || any.Count == 0)
                {
                    held = Unregister(g.Id);   // nothing to write: no wait for the lock
                    return;
                }
            // the recording lock, then the monitor: the order a download and RunItem take them in
            using (ct.IsCancellationRequested ? Recordings.Lock(RecordingPath(g.Id), TimeSpan.Zero) : Recordings.Lock(RecordingPath(g.Id), ct: ct))
            {
                held = Unregister(g.Id);
                foreach (var h in held) h.Replace();
            }
            Log?.Report($"{g.Name}: community download{(held.Count == 1 ? "" : "s")} held during the compile applied");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            held ??= Unregister(g.Id);
            Log?.Report($"{g.Name}: community download{(held.Count == 1 ? "" : "s")} held during the compile not applied " +
                $"({(e is OperationCanceledException ? "stopped while it waited for the recording" : e.Message)}), it downloads again at the next sync");
            return;
        }
        finally
        {
            if (held == null) Unregister(g.Id);   // any other failure: registered, it would hold later downloads for a compile that ended
        }
        try { Refresh(g, ct); }   // as a sync that downloads refreshes the game
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{g.Name}: refreshing failed: {e.Message}"); }
    }

    /// <summary>One game: by its fresh index's content hash when given, else by its store build (no build id: its last
    /// index). True when a new community or official recording was downloaded, or the official one's visibility changed. An
    /// official recording the manifest no longer has stays as downloaded. Without a content hash (a background pass), a
    /// download for a game whose compile reads its downloads is held until that compile ends, and doesn't count.</summary>
    async Task<bool> SyncCommunity(Game g, string? contentHash, CancellationToken ct, IReadOnlyList<string>? legacyHashes = null)
    {
        if (Community is not { } community || !UsesShared(g.Id) || !Settings.UseCommunityDb && DbEntitled() || await community.ManifestAsync(ct) is not { } manifest) return false;
        var rec = contentHash != null ? null : Store.LoadGame(g.Id);
        var entry = rec == null ? manifest.FindRichest(g, contentHash, legacyHashes, FullEntries) : DbEntry(manifest, g, rec, FullEntries);
        var official = rec == null ? manifest.FindOfficial(g, contentHash) ?? manifest.FindOfficial(g, null) : OfficialEntry(manifest, g, rec);
        // only where the plan warms vertex shaders from recorded layouts (PlanBuilder's Draws11); no alias: the installed index's build
        var draws = Planner.D3D11Stages(Vendor.Caps) is { } stages11 && !stages11.Contains(Stage.Vertex)
            && (contentHash ?? (IndexIsInstalled(g, rec!) ? rec!.IndexContentHash : null)) is { Length: 40 } build ? manifest.FindDraws(build) : null;
        var dir = Store.GameDir(g.Id);
        var got = false;
        var held = false;   // the last download's replace waits for the game's compile
        // a compile's own sync, by its fresh index, comes before it reads the downloads
        Func<Action, bool>? Hold(string file, string obj, bool full = false) =>
            contentHash == null ? replace => held = HoldDownload(g.Id, file, obj, full, replace) : null;
        // a background pass: the download held for the compile is what the file will be, not downloaded again every pass
        bool HeldAlready(string file, string obj, bool full = false) => contentHash == null && Held(g.Id, file) is { } h && h.Object == obj && h.Full == full;
        const string untilCompiled = ", held until the compile ends";
        // a compile saves its own record after this, and plans and warms the download anyway
        // a download kept as 'X' where the 'E' is read goes again, also when both cuts name the same object
        if (UsesShared(g.Id) && Settings.UseCommunityDb && entry != null && Community.Downloaded(dir) is var had && (entry.Object != had?.Object || had?.Full == true && !entry.Full)
            && !HeldAlready("community", entry.Object, entry.Full))
        {
            if (await community.DownloadAsync(entry, dir, ct, Hold("community", entry.Object, entry.Full)) is { } d)
            {
                Log?.Report($"{g.Name}: community recording downloaded ({d.Psos:N0} pipelines, {entry.Uploaders} contributor{(entry.Uploaders == 1 ? "" : "s")}){(held ? untilCompiled : "")}");
                got |= !held;
            }
            else if (community.Problem is { } why) Log?.Report($"{g.Name}: community database: {why}");
        }
        if (UsesShared(g.Id) && Settings.UseCommunityDb && draws != null && draws.Object != Community.DownloadedDraws(dir)?.Object && !HeldAlready("draws", draws.Object))
        {
            if (await community.DownloadDrawsAsync(draws, dir, ct, Hold("draws", draws.Object)) is { } d)
            {
                Log?.Report($"{g.Name}: community DirectX 11 layouts downloaded ({d.Psos:N0}){(held ? untilCompiled : "")}");
                got |= !held;
            }
            else if (community.Problem is { } why) Log?.Report($"{g.Name}: community database: {why}");
        }
        // a Steam build's Vulkan pipelines: planned only beside an archive of this PC (VulkanRecording)
        if (UsesShared(g.Id) && Settings.UseCommunityDb && VulkanLayer.AppId(g) != null && g.Version is { } version && ScannedVulkan(g)
            && manifest.FindVulkan($"{g.Id}@{version}") is { } vk && vk.Object != Community.DownloadedVulkan(dir)?.Object && !HeldAlready("vulkan", vk.Object))
        {
            if (await community.DownloadVulkanAsync(vk, dir, ct, Hold("vulkan", vk.Object)) is { } d)
            {
                Log?.Report($"{g.Name}: community Vulkan pipelines downloaded ({d.Psos:N0}){(held ? untilCompiled : "")}");
                got |= !held;
            }
            else if (community.Problem is { } why) Log?.Report($"{g.Name}: community database: {why}");
        }
        if (official != null)   // whatever this install may download: it decides the use
            try
            {
                if (Community.Restate(dir, official, ct)) got = true;
                lock (_restateFailed) _restateFailed.Remove(g.Id);
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException)   // kept as it is, tried again at the next sync
            {
                bool first;
                lock (_restateFailed) first = _restateFailed.Add(g.Id);
                if (first) Log?.Report($"{g.Name}: official recording: its visibility couldn't be updated ({x.Message})");
            }
        if (UsesShared(g.Id) && official != null && OfficialAllowed(official.Visibility) && official.Object != Community.DownloadedOfficial(dir)?.Object
            && !HeldAlready("official", official.Object))
        {
            if (await community.DownloadOfficialAsync(official, dir, ct, Hold("official", official.Object)) is { } d)
            {
                Log?.Report($"{g.Name}: official recording downloaded ({d.Psos:N0} pipelines){(held ? untilCompiled : "")}");
                got |= !held;
            }
            else if (community.Problem is { } why) Log?.Report($"{g.Name}: official recording: {why}");
        }
        return got;
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
                var rec = Store.LoadGame(g.Id);
                // a Vulkan recording has no content hash: its entry is the Steam build's, which the recorder needs anyway
                if (VulkanLayer.AppId(g) != null && g.Version is { } build && File.Exists(FozPath(g.Id)) && BlockingMod(g) == null)
                {
                    if (await sharing.ShareVulkanAsync(Store.GameDir(g.Id), $"{g.Id}@{build}", () => UploadMetaOf(g, rec, build, ShaderIndex.EmptyContentHash)) is { } vk)
                        Log?.Report($"{g.Name}: Vulkan recording shared ({vk.Psos:N0} entries, {vk.NewPsos:N0} new to the community database)");
                    else if (sharing.Problem is { } vkWhy && vkWhy != logged) Log?.Report($"{g.Name}: {logged = vkWhy}");
                }
                // Only with the content hash of exactly this build: an older build's would alias the new build to the old entry.
                // Without a store build id (other stores, a game added by hand) only while the index is of this exe, and with no
                // store build key: no alias to move, and no game id, which for a game added by hand is this PC's path hash.
                var v = g.Store == Core.Store.Manual ? null : g.Version;
                if ((v != null ? rec.IndexGameVersion != v : !Indexed(g, rec)) || rec.IndexContentHash is not { Length: 40 } hash) continue;
                if (rec.ShaderCount == 0 || ShaderIndex.IsEmpty(hash)) continue;   // no shaders: the hash is every such game's
                if (BlockingMod(g) != null) continue;   // read now: the state may not be published yet
                if (v == null && !await sharing.AcceptsAsync("versionless")) continue;
                if (await sharing.ShareAsync(Store.GameDir(g.Id), hash, () => UploadMetaOf(g, rec, v, hash),
                        () => Dlls(g).Where(d => d.Packable).SelectMany(d => Middleware.Scan(d.Path).Containers.Keys), LayerMadeNow,
                        OwnPlanCheck(rec, hash) is { } own ? $"{own.RsRule}:{own.BuiltOk}/{own.BuiltN}@{rec.PlanVersion}" : null) is { } got)
                {
                    Log?.Report($"{g.Name}: recording shared ({got.Psos:N0} pipelines, {got.NewPsos:N0} new to the community database)");
                    if (sharing.Problem is { } note && note != logged) Log?.Report($"{g.Name}: {logged = note}");   // shared, with a caveat
                    Refresh(g);
                }
                else if (sharing.Problem is { } why && why != logged) Log?.Report($"{g.Name}: {logged = why}");
                // the warm's build: a pending launch after a game update ran a build the warm didn't compile
                // not while the exit is still being seen: the csv may end the launch before its exit's reads are in the record
                if (rec is { WarmedAt: { } warmed, Plan: { } plan, WarmedPlanVersion: > 0 } && plan.IndexContentHash == hash && rec.WarmedIndexHash == hash
                    && !IsPlaying(g.Id))
                {
                    if (AmdAlwaysOn11(rec))
                    {
                        if (_alwaysOnNoted.TryAdd((g.Id, warmed), true)) Log?.Report($"{g.Name}: the launch after the compile isn't reported: AMD's shader cache is set to Always on");
                        continue;
                    }
                    rec = Store.LoadGame(g.Id);   // the exit's reads, saved before the game left IsPlaying
                    // a fold after this warm and the run after it: an older one's stamp says nothing about the dbs this warm put in
                    var identity = rec is { D3DSCacheLaunch: { } l, D3DSCacheLaunchAt: { } at } && at >= warmed && await sharing.AcceptsAsync("identity") ? l : null;
                    var hits = rec is { HitCheck: { } h, HitCheckAt: { } hat } && hat >= warmed && await sharing.AcceptsAsync("hitcheck") ? h : null;
                    var csv = Path.Combine(SessionDir(g), "scskiller_creates.csv");
                    OutcomeReport About() => new(hash, ShareVendor(Vendor.Vendor), GpuModel(), DriverVersion(), AppVersion.Current.ToString(), plan.Stats.RsRule,
                        rec.WarmedPlanVersion, Identity: identity, HitCheck: hits);
                    // a run the recorder didn't log (off, or an anti-cheat game) reports its hit check alone
                    if (await sharing.ReportAsync(Store.GameDir(g.Id), warmed.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture), () => OutcomeReport.Build(
                            csv, Path.GetFileName(g.ExePath), warmed, rec.LastPlay, OwnDriver == GpuVendor.Nvidia ? SessionLog.ReadRayQueryKeys(RayQueryKeysPath(g.Id)) : null,
                            About(), MinJudgedCreates, rec.HitCheckRun) ?? OutcomeReport.HitCheckOnly(csv, Path.GetFileName(g.ExePath), warmed, About())))
                        Log?.Report($"{g.Name}: the first launch after the warm reported");
                }
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

    /// <summary>Where a game's recorder session files are: the game folder's or the layer's inbox, the one whose session files
    /// were written last where both hold some (a game on both APIs; a tie keeps the game folder's), else the last session's,
    /// kept in the data folder when its files left the game's (an offline session, or the recorder taken out).</summary>
    string SessionDir(Game g)
    {
        var exeDir = Path.GetDirectoryName(g.ExePath)!;
        static bool Has(string dir) => SessionFiles.Any(f => File.Exists(Path.Combine(dir, f)));
        static DateTime Written(string dir) => SessionFiles.Select(f => Path.Combine(dir, f)).Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
        var vk = VulkanInboxDir(g) is { } inbox && Has(inbox) ? inbox : null;
        return Has(exeDir) ? vk != null && Written(vk) > Written(exeDir) ? vk : exeDir : vk ?? OfflineSessionDir(Store, g.Id);
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

    // the adapter's name only (no LUID, ids or memory size), as sharing sends it
    string? GpuModel() => Vendor.Gpu.Name.Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 128)].TrimEnd() : null;
    string? DriverVersion() => ShareDriverVersion(Vendor.Gpu.DriverVersion);

    // a plan built with a community download checked other devices' records too: only this PC's recording counts
    static PlanStats? OwnPlanCheck(GameRecord rec, string contentHash) =>
        rec.Plan is { Stats.BuiltN: not null } p && p.IndexContentHash == contentHash && rec.PlanCommunity == null ? p.Stats : null;

    /// <summary>"Family Version Fork" of the game's engine, at most 128 characters; null when not detected.</summary>
    string? EngineLabel(Game g)
    {
        EngineInfo? engine;   // the scan's cache: Games is still empty during an app's first scan
        lock (_scanLock) engine = _scan?.GetValueOrDefault(g.Id)?.Engine;
        return engine is { } e ? $"{e.Family} {e.Version} {e.Fork}".TrimEnd() is var s && s.Length > 128 ? s[..128].TrimEnd() : s : null;
    }

    UploadMeta UploadMetaOf(Game g, GameRecord rec, string? version, string contentHash)
    {
        static string? Clip(string? s) => s is { Length: > 128 } ? s[..128] : s;
        var own = OwnPlanCheck(rec, contentHash);
        return new(version != null ? $"{g.Id}@{version}" : null, contentHash, EngineLabel(g),
            ShareVendor(Vendor.Vendor), AppVersion.Current.ToString(),
            [.. Dlls(g).Where(d => d.Packable).Take(64).Select(d => new UploadDll(Clip(d.Name)!, Middleware.Scan(d.Path).ContentHash))],
            own?.RsRule, own?.BuiltOk, own?.BuiltN, own != null ? rec.PlanVersion : null, DriverVersion(), GpuModel());
    }

    /// <summary>Imports the game folder's scskiller.db (the recorder's inbox) into SCSKiller's copy when it changed since the
    /// last import, then empties it once nothing has it open (<see cref="Recordings"/>). Whatever the inbox holds is merged
    /// by record key, so a recording that started over loses nothing. A copy stored before compact recordings (a proxy db,
    /// or its merge with the community's in recording.all.db) is left to <see cref="MigrateRecordings"/> unless
    /// <paramref name="migrate"/>. Waits for the recording lock until <paramref name="ct"/> is cancelled (it then throws).</summary>
    bool ImportRecording(Game g, GameRecord rec, bool migrate = false, CancellationToken ct = default)
    {
        if (Collections.RecordedBy(g.Id) != null) return false;   // its records come from its collection's first game's imports
        var foreign = Foreign(g);   // another copy's inbox is never read; this copy's stored recording is still upkept
        var inbox = new FileInfo(Path.Combine(Path.GetDirectoryName(g.ExePath)!, "scskiller.db"));
        var store = RecordingPath(g.Id);
        var all = Path.Combine(Store.GameDir(g.Id), "recording.all.db");
        var keysFile = Path.Combine(inbox.DirectoryName!, Recordings.KeysFile);
        // keys naming more than the index's shaders with no recording (the data folder was deleted): the recorder would skip blobs nothing has
        if (!File.Exists(store) && File.Exists(keysFile) && Length(keysFile) != 8 + Math.Max(0, Length(Path.Combine(Store.GameDir(g.Id), Sharing.ShippedFile)) - 20)) WriteKeys(g, rec, ct);
        var legacy = Legacy(g.Id);
        if (legacy && !migrate) return false;
        var stamp = inbox.Exists && !foreign ? $"{inbox.Length}:{inbox.LastWriteTimeUtc.Ticks}" : null;
        var pending = !foreign && inbox is { Exists: true, Length: > 0 } && stamp != rec.RecordingInbox;
        if (pending && Writing(inbox.FullName)) return false;   // imported once the game lets go of it
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
            if (foreign || inbox is not { Exists: true, Length: > 0 }) return false;
            // imported while the game held it: emptied once it's gone, and nothing in its folder while it runs
            using (Recordings.Lock(store, ct: ct))
            {
                if (!GameFolderWrite(g) || Foreign(g) || !Recordings.Rotate(inbox.FullName, inbox.Length)) return false;   // taken over while this waited for the lock
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
                if (pending && Foreign(g)) return false;   // taken over while this waited for the lock
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
        if (Collections.Member(g.Id) != null) SplitCollection(g);
        return true;
    }

    /// <summary>Hands the records of a collection's other games (<see cref="Collections"/>) from its first game's recording,
    /// where the shared recorder's imports land, to theirs: each other game gets the pipelines whose shaders its build all
    /// ships, and the first keeps the rest, those its own build ships too included. A game whose index isn't this build's
    /// gets none until it is indexed; the first then keeps them all.</summary>
    void SplitCollection(Game first)
    {
        if (Collections.Member(first.Id) is not { } m || m.All[0].Id != first.Id || Legacy(first.Id)) return;
        var store = RecordingPath(first.Id);
        static IEnumerable<string>? Shaders(PsoDb.Rec r)
        {
            try { return PsoDb.Parse(r).Stages.Values; }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException or OverflowException) { return null; }
        }
        var got = new List<string>();
        try
        {
            using var gate = Recordings.Lock(store);
            if (!File.Exists(store)) return;
            var own = Shipped(first, Store.LoadGame(first.Id));
            var moved = new HashSet<string>();
            // from the install, not the list: an import during the first scan runs before the list is published
            foreach (var g in Collections.Expand(first).Skip(1))
            {
                if (Legacy(g.Id) || Shipped(g, Store.LoadGame(g.Id)) is not { } theirs) continue;
                var take = PsoDb.Read(store).Where(r => r.Tag is 'G' or 'C' or 'S' && Shaders(r) is { } s && s.All(theirs.Contains)).ToList();
                // the NVAPI states and layer pairs of any record go too (keyed by pipeline: extra ones name none of its)
                var meta = PsoDb.Read(store).Where(r => r.Tag is 'N' or 'W').ToList();
                if (take.Count + meta.Count == 0) continue;
                var named = Rehydrate.References(take);
                var inbox = Path.Combine(Store.GameDir(first.Id), $"split-{Environment.ProcessId}.tmp");
                int added;
                bool nvAdded;
                try
                {
                    PsoDb.WriteCompact(inbox, PsoDb.Read(store).Where(r => r.Tag == 'B' && named.Contains(PsoDb.Hex(r.Payload.AsSpan(0, 20)))).Concat(meta).Concat(take));
                    using (Recordings.Lock(RecordingPath(g.Id)))
                    {
                        Directory.CreateDirectory(Store.GameDir(g.Id));
                        added = Recordings.Merge(RecordingPath(g.Id), inbox, theirs.Contains, out nvAdded).Count;
                    }
                }
                finally { File.Delete(inbox); }
                if (added > 0 || nvAdded)
                {
                    Imported(g);
                    got.Add(g.Id);
                    if (added > 0) Log?.Report($"{g.Name}: {added:N0} pipelines from {first.Name}'s recorder");
                }
                if (own != null) moved.UnionWith(take.Where(r => !Shaders(r)!.All(own.Contains)).Select(r => r.Key));
            }
            // its blobs stay: the keys file may name them, and the recorder then records them by hash
            if (moved.Count > 0)
            {
                PsoDb.WriteCompact(store, PsoDb.Read(store).Where(r => r.Tag == 'B' || !moved.Contains(r.Key)).ToList());
                KeyFiles.Forget(store);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { Log?.Report($"{first.Name}: handing its collection's records on failed: {e.Message}"); }
        foreach (var id in got)
            if (Games.FirstOrDefault(s => s.Game.Id == id)?.Game is { } listed) Refresh(listed);
    }

    /// <summary>Tests: a compile has read its recordings and is about to prepare them (under the recording lock).</summary>
    internal Action? RecordingsRead;

    /// <summary>Tests: a warm's cache attribution took a sample of the files open by its exe name.</summary>
    internal Action? CacheSampled;

    /// <summary>Tests: an import is about to empty the inbox it merged.</summary>
    internal Action? InboxRotating;

    /// <summary>Tests: a migration has read the game's record (<see cref="MigrateRecordings"/>).</summary>
    internal Action? MigrationLoaded;

    internal bool HoldsRecorderLock => Monitor.IsEntered(_recorderLock);

    /// <summary>The game's recording is stored as before compact recordings: <see cref="MigrateRecordings"/> converts it.</summary>
    /// <summary>The game folder's scskiller.db holds records no import took yet, as <see cref="ImportRecording"/> tells them.</summary>
    bool InboxPending(Game g, GameRecord rec) => Collections.RecordedBy(g.Id) == null && !Foreign(g)
        && new FileInfo(Path.Combine(Path.GetDirectoryName(g.ExePath)!, "scskiller.db")) is { Exists: true, Length: > 0 } inbox
        && $"{inbox.Length}:{inbox.LastWriteTimeUtc.Ticks}" != rec.RecordingInbox;

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
        if (Collections.RecordedBy(g.Id) is { } by)   // the shared recorder's file names the collection's: rewritten by its first game's next pass
        {
            var first = Store.LoadGame(by);
            if (!first.KeysPending)
            {
                first.KeysPending = true;
                Store.SaveGame(by, first);
            }
            rec.KeysPending = false;
            return 0;
        }
        var dir = Path.GetDirectoryName(g.ExePath)!;
        var keys = Path.Combine(dir, Recordings.KeysFile);
        if (Foreign(dir)) return 0;
        if (!GameFolderWrite(g)) return KeysLater(rec);
        using var gate = Recordings.Lock(RecordingPath(g.Id), ct: ct);   // the keys file's writers too: one temp name in the game folder
        if (!GameFolderWrite(g)) return KeysLater(rec);   // the game may have started during the wait
        if (Foreign(dir)) return 0;   // or another copy took the recorder over
        try
        {
            var dll = ProxyNames.Select(n => Path.Combine(dir, n)).FirstOrDefault(IsOurProxy);   // ours, under any name
            if (dll != null)
            {
                var shipped = rec.IndexContentHash is { } h ? Sharing.Shipped(Store.GameDir(g.Id), h) : null;
                var installed = shipped != null && IndexIsInstalled(g, rec);
                if (installed && Collections.Member(g.Id) is { } m)   // the shared recorder records its collection's other games too
                    foreach (var o in m.All.Skip(1).Select(e => Games.FirstOrDefault(s => s.Game.Id == e.Id)?.Game).OfType<Game>())
                        if (Shipped(o, Store.LoadGame(o.Id)) is { } more) shipped!.UnionWith(more);
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
        // a collection's recorder records its games' launches: what any of their last compiles created
        var src = (Collections.Member(g.Id)?.All.Select(e => e.Id) ?? [g.Id]).Select(id => Path.Combine(Store.GameDir(id), Recordings.WarmedFile)).Where(File.Exists).ToList();
        var dst = Path.Combine(dir, Recordings.WarmedFile);
        if (src.Count == 0) { File.Delete(dst); return; }
        // a .tmp temp is a data file: never an install change
        Place(dst, src is [var one] ? CopyOf(one) : f => f.Write(Recordings.UnionWarmed(src)), replace: true, suffix: ".tmp");
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
        ShaderIndex index;
        if (VulkanPlan.IsVulkan(engine))   // planned from its recording alone
        {
            index = new ShaderIndex(ShaderIndex.EmptyContentHash, [], new Dictionary<string, ShaderInfo>(), []);
            (rec.IndexContentHash, rec.IndexLegacyHashes, rec.ShaderCount, rec.IndexGameVersion, rec.IndexExeStamp) = (index.ContentHash, null, 0, g.Version, stamp);
            Store.SaveGame(g.Id, rec);
            return index;
        }
        PlanBudget.UserMemory = (long)Settings.MaxCompileMemoryGB << 30;
        try { using (ContentStamps.Cached(Store.GameDir(g.Id))) index = _reader.Index(g, engine, Log, new Reporter<(int Done, int Total)>(r => ReadProgress(g.Id, r.Done, r.Total)), ct); }
        finally { RequestCollect(); }
        (rec.IndexContentHash, rec.IndexLegacyHashes, rec.ShaderCount, rec.IndexGameVersion, rec.IndexExeStamp) = (index.ContentHash, index.LegacyHashes is { Count: > 0 } l ? [.. l] : null, index.Shaders.Count, g.Version, stamp);
        Sharing.SaveShipped(Store.GameDir(g.Id), index);
        CompactRecording(g, rec, index, ct);
        Store.SaveGame(g.Id, rec);
        // a recording from before this game's index
        if (Collections.RecordedBy(g.Id) is { } by) { if (Games.FirstOrDefault(s => s.Game.Id == by)?.Game is { } first) SplitCollection(first); }
        else if (Collections.Member(g.Id) != null) SplitCollection(g);
        return index;
    }

    /// <summary>The running item's read count while it reads its shaders; a background index's is shown nowhere.</summary>
    void ReadProgress(string id, int done, int total)
    {
        QueueItem item;
        lock (_lock)   // checked and replaced at once: a pause meanwhile keeps its stage
        {
            var i = _current == id ? _queue.FindIndex(q => q.GameId == id && q.Stage == QueueStage.Indexing) : -1;
            if (i < 0 || _queue[i].Progress?.Done > done) return;   // workers report out of order
            _queue[i] = item = _queue[i] with { Progress = new WarmProgress(done, total, 0, 0) };
        }
        QueueChanged?.Invoke(item);
    }

    int _stepsRun;   // the run whose plan steps show: an abandoned plan check's build reports nothing

    /// <summary>The running item's plan or file preparation step count (<see cref="Planner.Steps"/>), at most every 250 ms
    /// for the same step. One that fails is logged once and never fails the plan.</summary>
    Action<string, long, long> PlanSteps(string id)
    {
        var run = Interlocked.Increment(ref _stepsRun);
        var (last, at, failed) = ("", 0L, 0);
        return (step, done, total) =>
        {
            var now = Environment.TickCount64;
            if (step == last && now - at < 250 || run != Volatile.Read(ref _stepsRun)) return;
            (last, at) = (step, now);
            try
            {
                QueueItem item;
                lock (_lock)
                {
                    var i = _current == id ? _queue.FindIndex(q => q.GameId == id && q.Stage is QueueStage.Planning or QueueStage.Materializing) : -1;
                    if (i < 0) return;
                    _queue[i] = item = _queue[i] with { Progress = new WarmProgress(done, total, 0, 0, Note: step) };
                }
                QueueChanged?.Invoke(item);
            }
            catch (Exception e) when (Interlocked.Exchange(ref failed, 1) == 0) { Log?.Report($"plan progress: {e.Message}"); }
            catch (Exception) { }
        };
    }

    /// <summary>Game id -> a background index a launch cancelled or put off: the watcher's next pass with no game playing
    /// starts it again (<see cref="RetryIndexes"/>).</summary>
    readonly ConcurrentDictionary<string, bool> _indexRetry = new();
    long _indexRetried;   // Environment.TickCount64 of the last retry
    /// <summary>Replaceable for tests. A game the process list keeps naming (a helper of its folder that always runs) is
    /// tried again at this pace, not at every pass.</summary>
    public TimeSpan IndexRetryInterval { get; set; } = TimeSpan.FromMinutes(1);

    void RetryIndexes()
    {
        if (_playing.Count > 0 || _indexRetry.IsEmpty || Environment.TickCount64 - Interlocked.Read(ref _indexRetried) < IndexRetryInterval.TotalMilliseconds) return;
        Interlocked.Exchange(ref _indexRetried, Environment.TickCount64);
        foreach (var id in _indexRetry.Keys)
            if (_indexRetry.TryRemove(id, out _) && Games.FirstOrDefault(s => s.Game.Id == id)?.Game is { } g) IndexForRecorder(g);
    }

    /// <summary>The background indexes of games stamped for recording before their build was indexed, one at a time.</summary>
    public Task RecorderIndexing { get; private set; } = Task.CompletedTask;
    readonly ConcurrentDictionary<string, CancellationTokenSource> _recorderIndexes = new();

    /// <summary>Another SCSKiller process runs a queue item (<see cref="Busy"/>, which this one holds too).</summary>
    internal Func<bool> OtherCompile { get; set; } = () => Busy.IsHeld();

    /// <summary>A background index waits while it's true: an item runs or the started queue has more.</summary>
    bool AnyCompileRunning() => Compiling || QueueRunning || OtherCompile();

    readonly ConcurrentDictionary<string, byte> _parkedIndexes = new();   // stopped for a compile: queued again to wait for the queue
    int _readingIndexes;   // past the wait: a compile in this process starts only once it is 0

    void ParkIndex(string id, CancellationTokenSource cts)
    {
        _parkedIndexes[id] = 0;
        cts.Cancel();
    }

    /// <summary>Below-normal threads, as the warm's. The engine readers' Parallel loops run on the caller's scheduler
    /// (<see cref="TaskScheduler.Current"/>), so their workers do too. Measured: an index on normal-priority threads took
    /// about 18% off a compile's rate.</summary>
    sealed class BelowNormalScheduler : TaskScheduler
    {
        static readonly Dictionary<(int, bool), BelowNormalScheduler> pools = [];
        // at least 2: a recorder index waits on one while a compile runs, the compile's lowered read needs another
        public static BelowNormalScheduler For(int threads, bool lowIo)
        {
            var key = (Math.Max(2, UsedThreads(threads)), lowIo);
            lock (pools) return pools.TryGetValue(key, out var p) ? p : pools[key] = new(key.Item1, lowIo);
        }
        [ThreadStatic] static bool ours;
        readonly BlockingCollection<Task> tasks = [];
        readonly int threads;

        BelowNormalScheduler(int threads, bool lowIo)
        {
            this.threads = threads;
            for (int i = 0; i < threads; i++)
                new Thread(() =>
                {
                    if (lowIo) BackgroundBegin();
                    (ours, Thread.CurrentThread.Priority) = (true, ThreadPriority.BelowNormal);
                    foreach (var t in tasks.GetConsumingEnumerable()) TryExecuteTask(t);
                }) { IsBackground = true, Name = "SCSKiller background index" }.Start();
        }

        public override int MaximumConcurrencyLevel => threads;
        protected override void QueueTask(Task task) => tasks.Add(task);
        protected override bool TryExecuteTaskInline(Task task, bool queued) => ours && TryExecuteTask(task);   // a queued one runs once: a worker's later try is a no-op
        protected override IEnumerable<Task> GetScheduledTasks() => tasks.ToArray();
    }

    /// <summary>The exe too: a store's build id is only the listed one until the next scan, and the watcher re-arms an
    /// update before that.</summary>
    static bool Indexed(Game g, GameRecord rec) => rec.IndexContentHash != null && IndexIsInstalled(g, rec) && rec.IndexExeStamp == ExeStamp(g);

    /// <summary>A stamped recorder of a build with no index records every shader with its bytes (about 36 KB each: thousands of
    /// them filled the recording limit within minutes) until a compile indexes it: indexed in the background instead, so the
    /// keys file names the shipped shaders from the next launch. Cancelled when the game or its compile starts; a game whose
    /// shaders can't be indexed records as before.</summary>
    void IndexForRecorder(Game g)
    {
        if (!ManageRecorders || Games.FirstOrDefault(s => s.Game.Id == g.Id) is not { Engine: { Unsupported: null, Encrypted: false } }
            || Store.LoadGame(g.Id) is var rec && (Indexed(g, rec) || rec.IndexFailedStamp == ExeStamp(g))) return;
        var cts = new CancellationTokenSource();
        if (!_recorderIndexes.TryAdd(g.Id, cts)) return;
        lock (_recorderIndexes)
            RecorderIndexing = RecorderIndexing.ContinueWith(_ => RunRecorderIndex(g.Id, cts), CancellationToken.None,
                TaskContinuationOptions.None, BelowNormalScheduler.For(Settings.BackgroundThreads, lowIo: true));
    }

    void RunRecorderIndex(string id, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        string name = id;
        string? stamp = null;   // the build read, once the read starts
        Game? again = null;
        bool parked = false, reading = false;
        try
        {
            while ((Settings.PauseWhileGaming && _playing.Count > 0 || AnyCompileRunning()) && !ct.IsCancellationRequested) Thread.Sleep(Poll);   // the watcher's: no process list per wait
            Interlocked.Increment(ref _readingIndexes);
            reading = true;
            ScanProgressed?.Invoke();   // AfterUpdateLine
            if (AnyCompileRunning()) ParkIndex(id, cts);   // after the count: a compile starting meanwhile either sees it or is seen here
            ct.ThrowIfCancellationRequested();
            using var watch = new Timer(_ => { if (AnyCompileRunning()) ParkIndex(id, cts); }, null, Poll, Poll);   // a compile starting in another process stops it
            if (Games.FirstOrDefault(s => s.Game.Id == id) is not { Engine: { Unsupported: null, Encrypted: false } engine, Game: var g }) return;
            name = g.Name;
            if (GameRunning(g))
            {
                _indexRetry[id] = true;
                return;
            }
            using var own = CompileLock(id);
            if (own == null) return;   // a compile indexes it
            var rec = Store.LoadGame(id);
            if (Indexed(g, rec)) return;
            stamp = ExeStamp(g);
            var index = Index(g, engine, rec, ct);
            Log?.Report($"{name}: {index.Shaders.Count:N0} shaders read from the game's files: the recorder records them by hash only");
            again = Indexed(g, rec) ? null : g;   // the exe changed during the read: its re-arm found this job running
        }
        catch (OperationCanceledException) { _indexRetry[id] = true; }
        catch (Exception e)
        {
            Log?.Report($"{name}: couldn't read the game's shaders before recording: {e.Message}");
            if (stamp != null) SaveFailed(id, r => r.IndexFailedStamp = stamp);   // not read again at every start: a compile reads it
        }
        finally
        {
            if (reading)
            {
                Interlocked.Decrement(ref _readingIndexes);
                ScanProgressed?.Invoke();
            }
            parked = _parkedIndexes.TryRemove(id, out _) && again == null;
            _recorderIndexes.TryRemove(id, out _);
            RequestCollect();
        }
        if (again != null) IndexForRecorder(again);
        else if (parked && Games.FirstOrDefault(s => s.Game.Id == id) is { } state) IndexForRecorder(state.Game);
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
        var games = states.Select(s => s.Game).Where(g =>
        {
            try { return Legacy(g.Id); }
            catch (IOException) { return false; }   // read outside the recording lock, a writer may hold it: the next scan checks again
        }).ToList();
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

    /// <summary>The file is our proxy: its bytes have an export only ours has. IOException or UnauthorizedAccessException
    /// when it is there and can't be read (<see cref="ProxyAt"/>).</summary>
    // on Linux the read keeps .NET's shared flock, so a writer's FileShare.None fails it as on Windows: no half-written read
    public static bool IsOurProxy(string dll) => File.Exists(dll) && File.ReadAllBytes(dll).AsSpan().IndexOf(ProxyMarker) >= 0;

    /// <summary><see cref="IsOurProxy"/>, null when the file is there and can't be read (another process holds it locked
    /// against reading, or this user may not read it): it may be ours.</summary>
    static bool? ProxyAt(string dll)
    {
        try { return IsOurProxy(dll); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f));
    }

    // Recorder skip reasons: short and stable, the Settings line counts games by them.
    public const string SkipAntiCheat = "anti-cheat", SkipForeignDll = "a mod's DLL is in the recorder's place",
        SkipModNotChainable = "a mod's DLL is in the recorder's place and stops working renamed",
        SkipVulkanMod = "vkd3d-proton or DXVK runs the game on Vulkan, whose pipelines a DirectX warm doesn't compile",
        SkipNeedsAdmin = "the game folder needs administrator", SkipNotDx12 = "not DirectX 12", SkipUnsupported = "not supported yet",
        SkipPackageD3D12 = "a DLL in the game's package folder loads instead of the recorder",
        SkipShaderMod = "an HDR mod changes every pipeline", SkipStreamline = "the recorder can't load before this game's DirectX 12 yet",
        SkipManual = "game folder not confirmed", SkipCrashes = "the recorder crashes this game, so it's off for now",
        SkipShaderInjector = "Shader Injector hooks the same functions as the recorder, so recording stays off while it's installed",
        SkipAgsMod = "a mod's d3d12.dll doesn't load beside the recorder in this game",
        SkipIntelDriver = "this game loads its own copy of the Intel driver, so SCSKiller doesn't compile or record it";

    /// <summary>The skip of a collection's other games (<see cref="Collections"/>): the folder's recorder is the first game's.</summary>
    public static string SharedRecorder(string first) => $"it shares {first}'s recorder, so turn recording on there";

    /// <summary><see cref="Planner.Record"/> for a collection's other game: its recording is switched on the first game's page.</summary>
    public static string RecordOnFirst(string first) => $"turn on recording on {first}'s page and play this game for about 5 minutes";

    /// <summary><see cref="Planner.Record"/> for a collection's other game while its first game's recorder is in.</summary>
    public static string SharedOn(string first) => $"{first}'s recorder is on: play this game for about 5 minutes";

    /// <summary>Why a game whose ReShade add-on adds to every root signature, in a layer a copy can't reproduce
    /// (<see cref="ReShadeInstall.Blocks"/>), isn't compiled: the case and its fix, one line.</summary>
    public static string ShaderModReason(string mod, LayerBlock why) => why switch
    {
        LayerBlock.NotBesideExe => $"ReShade must be next to the game's exe to compile through {mod}",
        LayerBlock.OptiScalerOff => $"Set LoadReshade=true in OptiScaler.ini to compile through {mod}",
        LayerBlock.OptiScalerIni => $"Fix the section headers in OptiScaler.ini to compile through {mod}",
        LayerBlock.ProxyLibrary => $"Give the DLL ReShade.ini's ProxyLibrary loads a name the game loads by itself (OptiScaler: winmm.dll), then set EnableProxyLibrary=0, to compile through {mod}",
        LayerBlock.SpecialKCompatibility => $"Turn off Compatibility Mode for ReShade in Special K's Plug-Ins to compile through {mod}",
        LayerBlock.SpecialKAddons => $"Keep one copy of each add-on Special K loads to compile through {mod}",
        LayerBlock.SpecialKSystem => $"Compiling through {mod} with Special K's ReShade isn't supported on this system yet",
        LayerBlock.SpecialK => $"Have Special K load one ReShade, ReShade64.dll next to the exe, to compile through {mod}",
        _ => $"Rename ReShade to dxgi.dll next to the exe to compile through {mod}",
    };

    static string ShaderModReason(ReShadeInstall r) => ShaderModReason(r.ShaderMod!.Mod!, r.Block);

    /// <summary>The blocking install (<see cref="ReShadeInstall.Blocks"/>), read now; null = none.</summary>
    ReShadeInstall? BlockingMod(Game g) => ReShadeOf(g) is { Blocks: true } r ? r : null;

    /// <summary>The game's ReShade (<see cref="ReShade.Detect"/>) for the API its last evaluation found.</summary>
    ReShadeInstall? ReShadeOf(Game g)
    {
        EngineInfo? engine;
        lock (_scanLock) engine = (_scan ??= Store.LoadScan()).GetValueOrDefault(g.Id)?.Engine;
        return ReShade.Detect(g, engine);
    }

    public const string MfgUnlockerNote = "MFG Unlocker's Path Traced Hair turns off while the recorder is in the game folder";

    public const string StarfieldLumaNote = "Luma changes some of this game's shaders. Compiles include them, and recording while you play covers the rest";

    /// <summary>Whether a game in the exe's folder loads a non-stock Intel user-mode driver: a DLL-redirection folder
    /// (a folder named after the exe with .local added) there holds igd12um* files. Any exe's counts, since a
    /// collection's games share the folder and its recorder. Only ever read: no warm runs against the stock driver and no
    /// recorder goes beside it.</summary>
    public static bool IntelLocalDriver(Game g)
    {
        static bool Is(string path, string ext) => string.Equals(Path.GetExtension(path), ext, StringComparison.OrdinalIgnoreCase);
        static bool Holds(string local)   // each on its own: one unreadable folder never hides another's driver
        {
            try { return Directory.EnumerateFiles(local, "igd12um*").Any(); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { return false; }
        }
        try
        {
            var dir = Path.GetDirectoryName(g.ExePath);
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) && Directory.EnumerateDirectories(dir, "*.exe.*").Any(d => Is(d, ".local") && Is(Path.GetFileNameWithoutExtension(d), ".exe") && Holds(d));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;   // a bad or unreadable path is never a refusal the user must read here
        }
    }

    /// <summary>A ReShade add-on that changes the game's pipelines and doesn't block it (<see cref="GameState.ShaderMod"/>).</summary>
    public static string ShaderModNote(GameState s) => !s.ShaderModLayer ? $"{s.ShaderMod} replaces some shaders: those few pipelines aren't precompiled"
        : s.AnyAntiCheat ? $"{s.ShaderMod} changes this game's pipelines: compiles run through a copy of ReShade and {s.ShaderMod}"
        : $"{s.ShaderMod} changes this game's pipelines: compiles run through a copy of ReShade and {s.ShaderMod}, and " +
        (s.ShaderModAsD3D12 && s.AgsHost ? "with ReShade installed as d3d12.dll, the ones it adds while the game draws aren't recorded, rename it to dxgi.dll to record them"
            : s.ShaderModAsD3D12 ? "with ReShade installed as d3d12.dll, the ones it adds while the game draws are recorded only with Record alongside ReShade on"
            : "the ones it adds while the game draws are recorded as you play");

    /// <summary>The game's ReShade layer copied into the warm's work folder (<see cref="Warmer.Layer"/>), with what a warm
    /// through it depends on; null = no add-on that changes pipelines.</summary>
    public (string Dir, string Fingerprint, string WriteTime)? LayerFor(Game g, string workDir)
    {
        if (ReShadeOf(g) is not { Fingerprint: { } fp } r) return null;
        return (ReShade.Stage(r, Path.Combine(workDir, "layer")), fp, r.WriteTimeFingerprint!);
    }

    /// <summary>NVIDIA: how the game starts Aftermath when that changes the driver's key (<see cref="NvAftermath.Of"/>), its
    /// recorder log read from <see cref="SessionDir"/>, else the init a compile after a miss tries (<see cref="AftermathRetries"/>,
    /// Retry true); null otherwise.</summary>
    (AftermathInit? Init, bool Retry) AftermathOf(Game g, GameRecord r, EngineInfo? e)
    {
        if (OwnDriver != GpuVendor.Nvidia) return (null, false);
        var log = Path.Combine(SessionDir(g), "scskiller.log");
        var retries = AftermathRetries(g, r, e);
        if (!retries && !NvAftermath.MayStart(g, e, log) || NvAftermath.DllFor(g) is not { } dll) return (null, false);
        // flags inferred from the log are tried once per build and judged by its first launch like a guess
        if (NvAftermath.Resolve(g, e, log, dll) is ({ } init, var inferred))
            return !inferred ? (init, false)
                : SameKey(r.AftermathRetry, AftermathRetryKey(g.Version ?? ExeStamp(g), WarmExeName(g, r))) && (r.AftermathLost || !r.WarmedAftermath) ? (null, false) : (init, true);
        return retries && NvAftermath.Retry(g, log, dll) is { } retry ? (retry, true) : (null, false);
    }

    AftermathInit? Aftermath(Game g, GameRecord r, EngineInfo? e) => AftermathOf(g, r, e).Init;

    /// <summary>A compile tries Aftermath as <see cref="NvAftermath.Retry"/> starts it: the game's launch didn't use the last
    /// complete compile (<see cref="IsUnreached"/>), which didn't start it, filled the game's own keys and wasn't rejected
    /// in part (<see cref="IsPartlyCompiled(long?, long?)"/>), on DirectX 12, and no compile tried it on this driver for
    /// this exe (a guess: once per build, after a compile of this build whose first launch it is judged by); or the last
    /// complete compile tried it for this exe, its first launch didn't show a guess lost (<see cref="GameRecord.AftermathLost"/>)
    /// and a guess still to be judged is of this build.</summary>
    bool AftermathRetries(Game g, GameRecord r, EngineInfo? e)
    {
        var (exe, build) = (WarmExeName(g, r), g.Version ?? ExeStamp(g));
        if (r.WarmedAftermath) return r.AftermathRetry?.EndsWith("|" + exe, StringComparison.OrdinalIgnoreCase) == true && !r.AftermathLost
            && !(r.AftermathPlainHits != null && !SameKey(r.AftermathRetry, AftermathRetryKey(build, exe)));
        var guess = AftermathGuess(g, e);
        return IsUnreached(r) && !SameKey(r.AftermathRetry, AftermathRetryKey(guess ? build : Snapshot().Id, exe))
            && !(guess && (r.FirstLaunch == null || (r.WarmedGameVersion ?? r.WarmedExeStamp) != build))
            && r.GameKeys.Overlaps(r.WarmedKeys ?? r.CacheKeys)
            && !IsPartlyCompiled(Rejected(r.LastWarmFailed, r.LastWarmCrashed), PlanItems(r.Plan?.Stats))
            && e?.GraphicsApi != "D3D11" && !(e != null && VulkanPlan.IsVulkan(e));
    }

    static string AftermathRetryKey(string? driverOrBuild, string exe) => $"{driverOrBuild}|{exe}";

    /// <summary>The exe name's case can change between sightings (<see cref="GameRecord.LaunchedExeName"/>).</summary>
    static bool SameKey(string? key, string other) => string.Equals(key, other, StringComparison.OrdinalIgnoreCase);

    /// <summary>The flags aren't the game's own logged call: <see cref="NvAftermath.Retry"/>'s guess (no DX12_Initialize in the newest
    /// session) or <see cref="NvAftermath.Inferred"/>. Either is judged by its first launch (AftermathLost).</summary>
    bool AftermathGuess(Game g, EngineInfo? e) => NvAftermath.Facts(Path.Combine(SessionDir(g), "scskiller.log")) is var f && (f.Features == null || NvAftermath.Inferred(e, f));

    /// <summary>The next warm would start Aftermath and the last complete one didn't, or the other way round.</summary>
    bool AftermathChanged(Game g, GameRecord r, EngineInfo? e) => r.WarmedAt != null && (Aftermath(g, r, e) != null) != r.WarmedAftermath;

    internal const string AftermathReason = "the game starts NVIDIA Aftermath with shader debug info, the last compile didn't";
    public const string AftermathRetryReason = "the next compile starts NVIDIA Aftermath as the game does";
    public const string AftermathLostReason = "NVIDIA Aftermath didn't help the last compile, the next one runs without it";

    /// <summary>What a warm through the game's layer depends on now (<see cref="ReShadeInstall.Fingerprint"/>); null = none.</summary>
    ReShadeInstall? LayerNow(Game g) => ReShadeOf(g) is { Fingerprint: not null } r ? r : null;

    /// <summary>Null = compatible. Writability is only known by writing (Reconcile remembers a refusal), except WindowsApps.
    /// <paramref name="modSkip"/>: <see cref="ModSkip"/>.</summary>
    public static string? RecorderSkip(GameState s, string? modSkip) =>
        s.AnyAntiCheat ? SkipAntiCheat
        : IntelLocalDriver(s.Game) ? SkipIntelDriver
        : RecorderCrashes(s.Game) ? SkipCrashes
        : s.ShaderModBlocks ? SkipShaderMod
        : s.RootUnconfirmed ? SkipManual
        : SteamSandboxSkip is { } sandboxed && (s.Game.Store == Core.Store.Steam || SteamShortcuts.Is(s.Game.Id)) ? sandboxed
        : s.StreamlineFirst ? s.PreloadHeld is { } held ? PreloadHeldSkip(held, s.PreloadHeldByOptions) : SkipStreamline   // this reason, not "not supported yet"
        : s.Engine == null || s.Status == GameStatus.Unsupported && s.StatusReason != CantReachReason && !Records9(s.Engine) ? SkipUnsupported   // its recorder learns the launched exe name's case, which may let a compile reach it
        : VulkanPlan.IsVulkan(s.Engine) ? (VulkanLayer.Key(s.Game) == null ? SkipVulkanSteamOnly : null)   // the layer: no mod's DLL is in its way
        : !s.Engine.GraphicsApi.Contains("D3D12") && !s.Records11 && !Records9(s.Engine) ? SkipNotDx12   // "D3D11 or D3D12" may run on d3d12.dll
        : modSkip != null ? modSkip   // ReShade, OptiScaler, another wrapper: never replaced, chained only when the user asks
        : s.Game.ExePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) ? SkipNeedsAdmin
        : null;

    /// <summary>Why the recorder stays out of Steam's games when Steam runs them in a Flatpak or Snap sandbox, which doesn't
    /// see the recorder folder (its key, ledger and inbox are in the data folder): set where the app starts, null for the
    /// native Steam.</summary>
    internal static string? SteamSandboxSkip { get; set; }

    /// <summary>Why no game of Steam's can be compiled when Steam runs them in the Flatpak sandbox (their status says so,
    /// <see cref="Warming.WarmHost.FlatpakSteamGames"/>): set where the app starts, null for any other Steam and where they can.</summary>
    internal static string? SteamSandboxCompile { get; set; }

    public const string SkipSandboxed = "recording games of the Flatpak or Snap Steam isn't available yet",
        SkipFlatpak = "recording games from the Flatpak Steam isn't available yet";

    string? SandboxCompileOff(Game g) => SteamSandboxCompile is { } why && (g.Store == Core.Store.Steam || SteamShortcuts.Is(g.Id)) ? why : null;

    // PRAGMATA's protection crashes it while our d3d12.dll is in its folder, armed or not
    internal static bool RecorderCrashes(Game g) => g.Id is "steam:3357650" or "steam:700330"
        || NoRecorderExes.Contains(Path.GetFileNameWithoutExtension(g.ExePath) is var n && n.EndsWith("_Plus", StringComparison.OrdinalIgnoreCase) ? n[..^5] : n);

    // Assassin's Creed Shadows, Mirage, Black Flag Resynced (the exes Steam launches) and SCP: Secret Laboratory (any dx*.dll) don't start with it,
    // and Zenless Zone Zero (Steam and HoYoPlay) closes itself with its client "running abnormally"
    static readonly HashSet<string> NoRecorderExes = new(["ACShadows", "ACMirage", "ACBlackFlag", "SCPSL", "ZenlessZoneZero"], StringComparer.OrdinalIgnoreCase);   // _Plus: the Ubisoft+ build


    /// <summary>The page's recorder line for a game the recorder breaks (<see cref="RecorderCrashes"/>), ahead of any
    /// anti-cheat line; null = not one.</summary>
    public static string? CrashNote(Game g) => RecorderCrashes(g) ? $"Not available: {g.Name} doesn't start with the recorder in its folder." : null;

    /// <summary>The name a mod's d3d12.dll gets when the recorder chains to it (the proxy's scskiller.ini next=). It keeps the
    /// d3d12 prefix for a wrapper that reads its role from its own name by prefix (ReShade's d3d*). <see cref="ChainName11"/>:
    /// the same for a mod's d3d11.dll.</summary>
    public const string ChainName = "d3d12.scskiller-next.dll", ChainName11 = "d3d11.scskiller-next.dll";

    /// <summary>The recorder's file names: one proxy dll, which records D3D11 when it is loaded as d3d11.dll.</summary>
    public const string Dll12 = "d3d12.dll", Dll11 = "d3d11.dll", Dll9 = "d3d9.dll", DllVulkan = VulkanLayer.Dll;   // the Vulkan one is registered, in the game folder only for a risk session

    /// <summary>The recorder in front of AMD's AGS (<see cref="GameState.AgsHost"/>): the AGS wrapper takes the game's
    /// amd_ags_x64.dll name, and the game's own AGS is renamed to the name the wrapper links it by, <see cref="AgsChainName"/>.
    /// <see cref="AgsRealName"/>: the name earlier builds' wrappers link, which a game may ship itself (Cyberpunk 2077 2.5).</summary>
    public const string AgsName = "amd_ags_x64.dll", AgsChainName = "amd_ags_x64_scskiller.dll", AgsRealName = "amd_ags_real.dll";

    /// <summary>The name the AGS wrapper at <paramref name="wrapper"/> links the game's AGS by, read from its imports, never
    /// from which file is there. An IOException when it can't be read or links neither: nothing is touched until it can.</summary>
    internal static string AgsLinked(string wrapper)
    {
        try
        {
            using var pe = PeFile.Open(wrapper);
            return PeFile.ImportedFunctions(pe, AgsChainName).Any() ? AgsChainName
                : PeFile.ImportedFunctions(pe, AgsRealName).Any() ? AgsRealName
                : throw new IOException($"{wrapper} links no AGS SCSKiller knows");
        }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException) { throw new IOException($"{wrapper} can't be read: {e.Message}", e); }
    }

    /// <summary>Every name our proxy goes in under.</summary>
    static readonly string[] ProxyNames = [Dll12, Dll11, Dll9, AgsName];

    /// <summary>The names the recorder's preloader (proxy/preload.cpp) goes in under beside a Streamline game's exe on NVIDIA,
    /// in the order one is picked. None is a KnownDLL, so the exe's static import loads the copy beside it at process start,
    /// and its DllMain loads the recorder before Streamline's first d3d12.dll.</summary>
    public static readonly string[] PreloadNames = ["winmm.dll", "version.dll", "dinput8.dll"];

    readonly ConcurrentDictionary<string, string> _preloadSha = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, byte> _preloadLocal = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>DLL redirection (&lt;exe&gt;.local, a file or a folder) hands the preloader its own load of System32's DLL, and
    /// the game then fails to start: no preloader goes in beside it. Logged once per exe.</summary>
    bool LocalRedirect(Game g)
    {
        if (!Path.Exists(g.ExePath + ".local")) return false;
        if (_preloadLocal.TryAdd(g.ExePath, 0)) RecorderLog($"{g.Name}: no preloader: {g.ExePath}.local redirects its DLL loads");
        return true;
    }

    /// <summary>This build's preloader under <paramref name="name"/>: preload\ beside the proxy, out of the app's own search
    /// path, or for another System32 DLL's name the one <see cref="GenericPreload"/> writes.</summary>
    string? PreloadSource(string name, Game g) => _proxyDll == null ? null
        : PreloadNames.Contains(name, StringComparer.OrdinalIgnoreCase) ? Path.Combine(Path.GetDirectoryName(_proxyDll)!, "preload", name) : GenericPreload(g, name);

    // only a hash of a file there is kept: a preload\ that comes later is still found
    string? PreloadSha(string name, Game g) => PreloadSource(name, g) is { } p && File.Exists(p) ? _preloadSha.GetOrAdd(p, Sha256) : null;

    readonly ConcurrentDictionary<(string System, string Name), string?> _genericPreload = new();

    /// <summary>The preloader for <paramref name="name"/>, a DLL the game's exe imports from its system folder: written under
    /// the data folder (<see cref="PreloadPe"/>) once per process and System32 DLL. Null when the name is unfit
    /// (<see cref="PreloadPe.Unfit"/>) or the DLL can't be stood in for. Only a written preloader or an unfit DLL is kept: a
    /// missing file or a failed read or write (an antivirus holding it) is tried again on the next evaluation.</summary>
    string? GenericPreload(Game g, string name)
    {
        var lower = name.ToLowerInvariant();
        if (PreloadPe.Unfit(lower) || GameFiles.SystemFolder(g) is not { } system) return null;
        var key = (system, lower);
        if (_genericPreload.TryGetValue(key, out var kept)) return kept;
        var template = _proxyDll == null ? null : Path.Combine(Path.GetDirectoryName(_proxyDll)!, "preload", PreloadNames[0]);
        var real = GameFiles.Find(system, lower);
        try
        {
            if (template == null || !File.Exists(template) || !File.Exists(real)) return null;
            if (PreloadPe.Build(File.ReadAllBytes(template), real) is not { } bytes) return _genericPreload[key] = null;
            var path = Path.Combine(Store.DataDir, "preload", Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant(), lower);
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Place(path, f => f.Write(bytes), replace: true);
            }
            return _genericPreload[key] = path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) { return null; }
    }

    // SCSKILLER_TEST_PRELOAD_GENERIC=1: an exe that imports one of PreloadNames gets another name too (E2E)
    static readonly bool TestPreloadGeneric = Environment.GetEnvironmentVariable("SCSKILLER_TEST_PRELOAD_GENERIC") == "1";

    /// <summary>A name on the record that a preloader went in under: one of <see cref="PreloadNames"/>, or another DLL's
    /// that isn't the proxy's or one of the recorder's own files.</summary>
    static bool PreloadKey(string name) => PreloadNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !ProxyNames.Contains(name, StringComparer.OrdinalIgnoreCase) && !RecorderOwnFiles.Contains(name);

    // the preloader's FileDescription (proxy/version.rc) as its version resource holds it, in a file of at most 64 KB
    static readonly byte[] PreloadMarker = System.Text.Encoding.Unicode.GetBytes("SCSKiller recorder preloader");

    /// <summary>A preloader of any SCSKiller build, tracked or not (IOException when it can't be read).</summary>
    public static bool IsOurPreload(string path) =>
        new FileInfo(path) is { Exists: true, Length: < 65536 } && File.ReadAllBytes(path).AsSpan().IndexOf(PreloadMarker) >= 0;

    /// <summary>Place's temp name for <paramref name="name"/>: <c>&lt;name&gt;.&lt;32 hex digits&gt;.scskiller-new</c>.</summary>
    static bool PlaceTemp(string file, string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(file, $@"^{System.Text.RegularExpressions.Regex.Escape(name)}\.[0-9a-f]{{32}}{System.Text.RegularExpressions.Regex.Escape(TempSuffix)}$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Our preloader in the folder under any of its names (IOException when one can't be read).</summary>
    static bool OurPreloadIn(string dir) => PreloadNames.Any(n => IsOurPreload(Path.Combine(dir, n)));

    /// <summary>The name of our preloader in the folder; one that can't be read counts as ours. Null: none.</summary>
    static string? PreloadIn(string dir) => PreloadNames.FirstOrDefault(n =>
    {
        try { return IsOurPreload(Path.Combine(dir, n)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    });

    /// <summary>A file of <paramref name="name"/> beside the exe that is neither what this record placed there nor a
    /// preloader of SCSKiller's: the game's or a mod's. Under another DLL's name than <see cref="PreloadNames"/> only the
    /// bytes on the record with the marker are SCSKiller's. One that can't be read is: a name on the record proves nothing of
    /// what is there now.</summary>
    static bool PreloadTaken(string dir, string name, GameRecord rec)
    {
        var path = Path.Combine(dir, name);
        try
        {
            if (PreloadNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                return File.Exists(path) && Sha256(path) != rec.RecorderFiles.GetValueOrDefault(name) && !IsOurPreload(path);
            // every spelling: a case-sensitive folder (Proton) may hold the game's own beside the exe's import spelling
            return GameFiles.FindAll(dir, name).Any(p => File.Exists(p) && !(Sha256(p) == rec.RecorderFiles.GetValueOrDefault(name) && IsOurPreload(p)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>The preloader's name for a Streamline game, among the <see cref="PreloadNames"/> its exe imports statically,
    /// or, when it imports none of them or other files hold every one, the other DLLs it imports that a preloader can stand in
    /// for (<see cref="GenericPreload"/>): the one placed while it is still ours, else the first no other file holds and
    /// <paramref name="builtin"/> (DLLs Wine loads its own of for the game, <see cref="WineBuiltinFirst"/>) leaves alone. Held:
    /// those names, when other files hold every one of them and no other DLL fits, else (ByOptions) the ones
    /// <paramref name="builtin"/> takes, when it and other files leave none. A name held by the game's or a mod's file is
    /// never used, renamed or deleted: loaders pick their role from their own file name. Both null: the exe imports none of them, or can't be read.</summary>
    (string? Name, string? Held, bool ByOptions) PreloadName(Game g, GameRecord rec, IReadOnlySet<string>? builtin = null)
    {
        var exe = g.ExePath;
        List<string> imports;
        try
        {
            using var pe = PeFile.Open(exe);
            // the preloader is x64: beside a 32-bit exe its import would fail to load
            if (pe.PEHeaders.CoffHeader.Machine != System.Reflection.PortableExecutable.Machine.Amd64) return (null, null, false);
            imports = CarvedReader.PeImports(pe, delayed: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) { return (null, null, false); }
        var names = PreloadNames.Where(n => imports.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        var dir = Path.GetDirectoryName(exe)!;
        var pick = Pick(names);
        // mod loaders hold every one of them (RED4ext's winmm, CET's version): another import stands in
        if (names.Count == 0 || pick is { Held: not null, ByOptions: false } || TestPreloadGeneric)
        {
            // a name another file has beside the exe is passed over, and held only when another file has every one
            var generic = Pick([.. imports.Where(n => GenericPreload(g, n) != null).Distinct(StringComparer.OrdinalIgnoreCase)]);
            if (generic.Name != null || names.Count == 0 || TestPreloadGeneric) return generic;
        }
        return pick;

        (string? Name, string? Held, bool ByOptions) Pick(List<string> list)
        {
            var overridden = list.Where(n => builtin?.Contains(Path.GetFileNameWithoutExtension(n)) == true).ToList();
            var free = list.Except(overridden).Where(n => !PreloadTaken(dir, n, rec)).ToList();
            return list.Count == 0 ? (null, null, false)
                : free.Count == 0 ? overridden.Count > 0 ? (null, Listed(overridden), true) : (null, Listed(list), false)
                : (free.FirstOrDefault(rec.RecorderFiles.ContainsKey) ?? free[0], null, false);
        }

        static string Listed(List<string> n) => n.Count == 1 ? n[0] : $"{string.Join(", ", n[..^1])} and {n[^1]}";
    }

    /// <summary>The DLLs (lower case, no extension) Wine loads its own of for <paramref name="g"/>, or none of, whatever is
    /// beside the exe: under Proton, what WINEDLLOVERRIDES in its launch options sets builtin first or off. Null elsewhere.</summary>
    partial void WineBuiltinFirst(Game g, ref IReadOnlySet<string>? dlls);

    /// <summary><see cref="PreloadHeldSkip"/> as the status says it, after "needs a recording, ".</summary>
    public static string PreloadHeldNoRecording(string held, bool byOptions = false) => byOptions ? "which WINEDLLOVERRIDES in its launch options blocks"
        : held.Contains(" and ")
        ? $"which another program's {held} block"
        : $"which another program's {held} blocks";

    /// <summary>The recorder's skip in a Streamline game whose every preloader name another file holds, or the game's
    /// WINEDLLOVERRIDES takes (<paramref name="byOptions"/>, <see cref="PreloadName"/>).</summary>
    public static string PreloadHeldSkip(string held, bool byOptions = false) => byOptions
        ? $"WINEDLLOVERRIDES in the game's launch options loads Wine's own {held}, and the recorder needs {(held.Contains(" and ") ? "one of those names" : "that name")} to load before DLSS"
        : held.Contains(" and ")
        ? $"{held} next to the game are another program's, and the recorder needs one of those names to load before DLSS"
        : $"{held} next to the game is another program's, and the recorder needs that name to load before DLSS";

    /// <summary>The preloader placed isn't the one <paramref name="preload"/> asks for: another name, gone, or another build's.</summary>
    bool PreloadStale(Game g, string dir, GameRecord rec, string? preload) =>
        rec.RecorderFiles.Keys.Any(n => !n.Equals(preload, StringComparison.OrdinalIgnoreCase) && PreloadKey(n))
        || preload != null && (!File.Exists(Path.Combine(dir, preload)) || PreloadSha(preload, g) is { } sha && Sha256(Path.Combine(dir, preload)) != sha);

    /// <summary>Install's preloader step: one of another name placed before goes (only with the bytes placed), then
    /// <paramref name="preload"/> goes in, never over another program's file. Its manifest entry is saved first: a copy cut
    /// short is still SCSKiller's to remove.</summary>
    void PlacePreload(Game g, GameRecord rec, string dir, string? preload)
    {
        // a copy a killed install cut short: Place's own temp name only, one another writer holds stays
        foreach (var n in PreloadNames.Concat(rec.RecorderFiles.Keys.Where(PreloadKey)).Append(preload ?? PreloadNames[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            foreach (var t in Directory.EnumerateFiles(dir, n + ".*" + TempSuffix).Where(t => PlaceTemp(Path.GetFileName(t), n) && (File.GetAttributes(t) & FileAttributes.ReparsePoint) == 0).ToList())
                try { File.Delete(t); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecorderLog($"{g.Name}: left {t}: {e.Message}"); }
        foreach (var n in rec.RecorderFiles.Keys.Where(n => !n.Equals(preload, StringComparison.OrdinalIgnoreCase) && PreloadKey(n)).ToList())
        {
            var old = Path.Combine(dir, n);
            if (File.Exists(old) && Sha256(old) == rec.RecorderFiles[n] && IsOurPreload(old)) File.Delete(old);
            else if (File.Exists(old)) RecorderLog($"{g.Name}: left {old}: changed since SCSKiller installed it");
            DropStored(dir, n, rec.RecorderFiles[n]);
            rec.RecorderFiles.Remove(n);
        }
        if (preload == null) return;
        if (PreloadTaken(dir, preload, rec)) throw new InvalidOperationException($"{Path.Combine(dir, preload)} already exists and is not SCSKiller's (another mod or the game's own). Not replacing it.");
        var src = PreloadSource(preload, g) is { } p && File.Exists(p) ? p : throw new FileNotFoundException($"the recorder's preloader {preload} was not found next to the app");
        var sha = Sha256(src);
        // another build's bytes under another DLL's name: only the hash on the record proves them ours, so they go before it changes
        if (rec.RecorderFiles.GetValueOrDefault(preload) is { } was && was != sha)
        {
            var dest = Path.Combine(dir, preload);
            if (!PreloadNames.Contains(preload, StringComparer.OrdinalIgnoreCase) && File.Exists(dest) && Sha256(dest) == was && IsOurPreload(dest)) File.Delete(dest);
            DropStored(dir, preload, was);
        }
        rec.RecorderFiles[preload] = sha;
        Store.SaveGame(g.Id, rec);
        if (!GameFolderWrite(g)) throw new InvalidOperationException($"{g.Name} started while the recorder was installed");
        Place(Path.Combine(dir, preload), CopyOf(src), replace: true);
    }

    /// <summary>The copy of a preloader a game made in its _storage_ folder (Onimusha), when its bytes are <paramref name="sha"/>.</summary>
    static void DropStored(string dir, string name, string sha)
    {
        var storage = Path.Combine(dir, "_storage_");
        var stored = Path.Combine(storage, name);
        if (!Reparse(storage) && !MarkedElsewhere(storage) && File.Exists(stored) && Sha256(stored) == sha && IsOurPreload(stored)) File.Delete(stored);
    }

    /// <summary>A DirectX 11 game (not one that may run on DirectX 12) on a GPU whose D3D11 vertex shaders need the game's own
    /// input layouts (AMD, <see cref="Planner.D3D11CoversVs"/>): the recorder goes in as d3d11.dll.</summary>
    /// <summary>The DirectX 9 recorder goes in as d3d9.dll: a DirectX 9 game on Windows, while SCSKILLER_TEST_D3D9_RECORDER=1.</summary>
    // ponytail: test-only until the DirectX 9 compile plans and warms its recordings, then AMD's DirectX 9 games
    // Windows only: Wine's d3d9.dll may lack exports the proxy forwards, and a missing one fails its load
    public static bool Records9(EngineInfo? engine) =>
        OperatingSystem.IsWindows() && engine is { } e && e.GraphicsApi.StartsWith("D3D9", StringComparison.Ordinal) && Environment.GetEnvironmentVariable("SCSKILLER_TEST_D3D9_RECORDER") == "1";

    public static bool Records11(EngineInfo? engine, VendorCaps caps) => engine is { Unsupported: null } && Records11Api(engine, caps);

    /// <summary><see cref="Records11"/> by the game's API alone: what a risk or offline session's recorder loads in.</summary>
    static bool Records11Api(EngineInfo? e, VendorCaps caps) =>
        e is not null && e.GraphicsApi.StartsWith("D3D11") && !e.GraphicsApi.Contains("D3D12") && !Planner.D3D11CoversVs(caps);

    /// <summary>A DirectX 11 game on a GPU whose staged D3D11 warm needs no recording (NVIDIA, Intel): the recorder goes in
    /// as d3d11.dll with frames_only=1 (frame times, nothing recorded), on NVIDIA for regular play too
    /// (<see cref="Frames11Play"/>), on Intel only while a before and after pair of the game is in progress.</summary>
    public static bool Frames11(EngineInfo? engine, VendorCaps caps) =>
        engine is { Unsupported: null } e && e.GraphicsApi.StartsWith("D3D11") && !e.GraphicsApi.Contains("D3D12") && Planner.D3D11Stages(caps)?.Contains(Stage.Vertex) == true;

    /// <summary><see cref="Frames11"/> outside a pair too: NVIDIA.</summary>
    public static bool Frames11Play(EngineInfo? engine, VendorCaps caps) => Frames11(engine, caps) && caps.Profile != Vendors.IntelBackend.Profile;

    /// <summary>The recorder goes in as d3d11.dll: <see cref="Records11"/>, <see cref="Frames11Play"/>, or <see cref="Frames11"/>
    /// from BeforeArmed through AfterPlaying.</summary>
    static bool As11(EngineInfo? engine, GameRecord rec, VendorCaps caps) =>
        Records11(engine, caps) || Frames11Play(engine, caps) || PairFrames11(engine, rec, caps);

    /// <summary><see cref="Frames11"/> with a before and after pair from BeforeArmed through AfterPlaying.</summary>
    static bool PairFrames11(EngineInfo? engine, GameRecord rec, VendorCaps caps) =>
        Frames11(engine, caps) && rec.BeforeAfter is { Stage: not BeforeAfterStage.Done };

    /// <summary><see cref="RecorderEffective"/>, but during a pair (<paramref name="pairFrames11"/>, <see cref="PairFrames11"/>)
    /// "Record in all compatible games" doesn't apply to the frames-only recorder: only the game's Off or <paramref name="skip"/> keeps it out.</summary>
    static bool RecorderWanted(RecorderOverride o, bool recordAllGames, string? skip, bool pairFrames11) =>
        pairFrames11 ? skip == null && o != RecorderOverride.Off : RecorderEffective(o, recordAllGames, skip);

    /// <summary>Why the <see cref="Frames11"/> recorder couldn't go in for a pair; null = it would, unless the game's switch is Off.</summary>
    string? PairRecorderSkip(GameState s, GameRecord rec)
    {
        var dir = Path.GetDirectoryName(s.Game.ExePath)!;
        bool ours;
        try { ours = IsOurProxy(Path.Combine(dir, Dll11)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ours = false; }   // a foreign d3d11.dll: a skip
        return RecorderSkip(s with { Records11 = true }, ModSkip(s.Game, dir, rec, ours, Dll11)) ?? _recorderSkips.GetValueOrDefault(s.Game.Id);
    }

    /// <summary>The recorder's file name in the game's exe folder: the one installed (by its manifest) until it's removed,
    /// else <see cref="AgsName"/> where <paramref name="ags"/> (<see cref="GameState.AgsHost"/>), d3d11.dll where
    /// <see cref="As11"/>, else d3d12.dll.</summary>
    static string RecorderDll(GameRecord rec, EngineInfo? engine, VendorCaps caps, bool ags) =>
        rec.RecorderFiles.ContainsKey(AgsName) || rec.RecorderChained?.Name is AgsChainName or AgsRealName ? AgsName
        : rec.RecorderFiles.ContainsKey(Dll11) || rec.RecorderChained?.Name == ChainName11 ? Dll11
        : rec.RecorderFiles.ContainsKey(Dll12) || rec.RecorderChained != null ? Dll12
        : rec.RecorderFiles.ContainsKey(Dll9) ? Dll9
        : ags ? AgsName : As11(engine, rec, caps) ? Dll11 : Records9(engine) ? Dll9 : Dll12;

    /// <summary>A d3d11.dll recorder on a GPU whose D3D11 warm needs no input layouts records frame times only.</summary>
    static bool FramesOnly(string dll, VendorCaps caps) => dll == Dll11 && Planner.D3D11CoversVs(caps);

    /// <summary>Why our d3d11.dll in the game's folder must not be armed on a <see cref="FramesOnly"/> GPU: the game's switch
    /// and "Record in all compatible games" keep it out and no pair is in progress, or no scskiller.ini of ours with
    /// frames_only=1 is next to it (without it the proxy records in full). Null = it may. A dll or ini that can't be read refuses.</summary>
    string? FramesOnlyRefusal(Game g)
    {
        if (!Planner.D3D11CoversVs(Vendor.Caps)) return null;
        var dir = Path.GetDirectoryName(g.ExePath)!;
        try
        {
            if (!IsOurProxy(Path.Combine(dir, Dll11))) return null;
            var rec = Store.LoadGame(g.Id);
            var s = Games.FirstOrDefault(x => x.Game.Id == g.Id);
            var pair = PairFrames11(s?.Engine, rec, Vendor.Caps);
            if (!pair && !Frames11Play(s?.Engine, Vendor.Caps) || !RecorderWanted(rec.Recorder ?? RecorderOverride.Default, s != null && RecordsByDefault(Settings, s), null, pair))
                return "its frame times recorder is off for this game";
            var ini = Path.Combine(dir, "scskiller.ini");
            return File.Exists(ini) && GetIni(Store, dir)?.Tracked is { Length: > 0 } tracked && tracked.Split('|').Contains(Sha256(ini))
                && File.ReadAllLines(ini).Contains("frames_only=1") ? null : "its scskiller.ini doesn't keep the d3d11.dll to frame times";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "its d3d11.dll or scskiller.ini can't be read: " + e.Message; }
    }

    static string ChainOf(string dll) => dll == Dll11 ? ChainName11 : dll == AgsName ? AgsChainName : ChainName;

    static string RoleOf(ChainedDll c) => c.Name == ChainName11 ? Dll11 : c.Name is AgsChainName or AgsRealName ? AgsName : Dll12;

    /// <summary>Our proxy placed in a chained mod's place and gone from it: a mod's uninstall deletes its DLL by name. The mod
    /// is never put back and the recorder never goes in around it (<see cref="GameRecord.RecorderOrphans"/>). Only a chain
    /// that was complete counts: its scskiller.ini names the mod, written after the proxy's copy, so a failed install's
    /// rename is still put back. A removal cut off before the mod was back left its proxy parked (<see cref="RemoveRecorder"/>).</summary>
    static bool Orphaned(string dir, GameRecord rec)
    {
        if (rec.RecorderChained is not { } c || Path.Combine(dir, RoleOf(c)) is var dll && (File.Exists(dll) || File.Exists(dll + TempSuffix))) return false;
        try { return File.ReadAllLines(Path.Combine(dir, "scskiller.ini")).Contains($"next={c.Name}"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }   // put back, as before
    }

    public const string DxvkNote = "DXVK runs this game on Vulkan, so the compile isn't used",
        DxvkCompiledNote = "DXVK runs this game on Vulkan, and the compile covers it";

    public const string ModRemovedNote = "a mod SCSKiller recorded alongside was removed, so recording is off for this game";

    /// <summary>Our proxy in the folder under any of its names (IOException when one can't be read).</summary>
    public static bool OurProxyIn(string dir) => ProxyNames.Any(n => IsOurProxy(Path.Combine(dir, n)));

    /// <summary>A mod's d3d12.dll in the recorder's place (or chained to it): null = none, or the user chose to record alongside
    /// it (<see cref="GameRecord.RecordAlongsideMod"/>) and it can be chained; else the skip reason. Turning the choice off
    /// while chained gives a skip, so Reconcile removes the recorder and puts the mod back. A DLL of the recorder's name in an Xbox app
    /// game's package root loads before the exe's folder's: <see cref="SkipPackageD3D12"/>.</summary>
    static string? ModSkip(Game g, string dir, GameRecord rec, bool ours, string name)
    {
        if (ShaderInjectorIn(dir) is var injector && (injector ?? !ours)) return SkipShaderInjector;   // unreadable: neither put in nor taken out
        if (ReShade.PackageRoot(g) is { } root && !root.Equals(GameFiles.DirKey(dir), StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(root, name))) return SkipPackageD3D12;
        var dll = Path.Combine(dir, name);
        if (name == AgsName) return AgsModSkip(g.ExePath);   // the game's own AGS, renamed only when Install finds the build the wrapper fits
        var mod = rec.RecorderChained is { } c ? Path.Combine(dir, c.Name) : !ours && File.Exists(dll) ? dll : null;
        return mod == null ? null : !rec.RecordAlongsideMod || name == Dll9 ? SkipForeignDll   // the DirectX 9 recorder chains to nothing
            : ChainBlocker(mod) switch { null => null, SkipVulkanMod => SkipVulkanMod, _ => SkipModNotChainable };
    }

    // The AGS wrapper loads System32's d3d12.dll by full path at the game's start, so a later load of a mod's d3d12.dll by name
    // gets System32's. An exe that imports d3d12.dll maps the mod's before any DllMain runs, so the mod loads beside the wrapper.
    static string? AgsModSkip(string exe)
    {
        var d12 = Path.Combine(Path.GetDirectoryName(exe)!, Dll12);
        if (!File.Exists(d12) || ProxyAt(d12) == true || ImportsD3D12(exe)) return null;
        return ChainBlocker(d12) == SkipVulkanMod ? SkipVulkanMod : SkipAgsMod;
    }

    // the loader maps the exe's static imports and theirs before any DllMain: one level beside the exe, AGS left out
    static bool ImportsD3D12(string exe)
    {
        if (StaticImports(exe) is not { } dlls) return false;
        var dir = Path.GetDirectoryName(exe)!;
        return dlls.Contains(Dll12, StringComparer.OrdinalIgnoreCase)
            || dlls.Where(d => !new[] { AgsName, AgsChainName, AgsRealName }.Contains(d, StringComparer.OrdinalIgnoreCase) && d.IndexOfAny(['\\', '/', ':']) < 0)
                .Select(d => Path.Combine(dir, d)).Where(File.Exists)
                .Any(d => StaticImports(d)?.Contains(Dll12, StringComparer.OrdinalIgnoreCase) == true);
    }

    static List<string>? StaticImports(string pe)
    {
        try { return CarvedReader.PeImports(pe, out _, delayed: false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) { return null; }
    }

    /// <summary>ReShade or OptiScaler as the d3d12.dll <see cref="AgsModSkip"/> keeps the recorder out for; null = another mod.</summary>
    static string? AgsModName(string dll)
    {
        try
        {
            var names = new[] { PeVersionInfo.GetVersionInfo(dll).ProductName, Middleware.ExportName(dll) };
            return names.Any(n => n?.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase) == true) ? "OptiScaler"
                : names.Any(n => n == "ReShade") ? "ReShade" : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static string AgsModHint(string mod) => $"Rename {mod}'s d3d12.dll to dxgi.dll to record this game.";

    /// <summary>Shader Injector's dsound.dll and ShaderInjector folder beside the exe. It inline-hooks the D3D12CreateDevice of
    /// whatever "d3d12.dll" loaded (the recorder there), then the device methods the recorder already hooks. Builds before 1.5.1
    /// have no version resource, only the name in their strings. Null: its dsound.dll can't be read.</summary>
    internal static bool? ShaderInjectorIn(string exeDir)
    {
        var dll = Path.Combine(exeDir, "dsound.dll");
        if (!Directory.Exists(Path.Combine(exeDir, "ShaderInjector")) || !File.Exists(dll)) return false;
        try
        {
            var v = PeVersionInfo.GetVersionInfo(dll);
            if (new[] { v.ProductName, v.FileDescription }.Any(n => n?.StartsWith("Shader Injector", StringComparison.OrdinalIgnoreCase) == true)) return true;
            var bytes = File.ReadAllBytes(dll).AsSpan();
            return bytes.IndexOf("ShaderInjector"u8) >= 0 || bytes.IndexOf(MemoryMarshal.AsBytes("ShaderInjector".AsSpan())) >= 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Why a mod's d3d12.dll can't be chained (renamed to <see cref="ChainName"/>); null = it can. OptiScaler and
    /// Special K pick their role from their own file name, so renamed they'd stop working. vkd3d-proton or DXVK (their own
    /// strings name them): <see cref="SkipVulkanMod"/>.</summary>
    public static string? ChainBlocker(string dll)
    {
        string? export, product;
        bool vkd3d;
        try
        {
            (export, product) = (Middleware.ExportName(dll), PeVersionInfo.GetVersionInfo(dll).ProductName);
            var bytes = File.ReadAllBytes(dll).AsSpan();
            vkd3d = bytes.IndexOf("vkd3d-proton"u8) >= 0 || bytes.IndexOf("DXVK_HUD"u8) >= 0 || bytes.IndexOf("DXVK_LOG_LEVEL"u8) >= 0;   // variables DXVK reads (its dxgi.dll has no DXVK_HUD): its own build, not a wrapper naming it
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
    public static string NotChainableReason(string? mod, string dll = Dll12) =>
        mod?.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase) == true
            ? $"OptiScaler is installed as {dll}, and renamed it stops working (it picks its role from its file name). To record alongside it, rename it to dxgi.dll (or winmm.dll or version.dll if a dxgi.dll is already there): OptiScaler supports those names"
            : $"{mod ?? "another mod"} is installed as {dll}, and the recorder can't run alongside it: renamed it stops working";

    /// <summary>What the game folder's d3d12.dll that isn't SCSKiller's (or the one chained to it) calls itself, for "Record
    /// alongside &lt;mod&gt;"; null = none there. A dll that can't be read is ours if the record says so, as the game's
    /// state takes it.</summary>
    public string? RecorderMod(string gameId)
    {
        var dir = Path.GetDirectoryName(Find(gameId).Game.ExePath)!;
        var rec = Store.LoadGame(gameId);
        var name = RecorderDll(rec, Find(gameId).Engine, Vendor.Caps, Find(gameId).AgsHost);
        return ModName(dir, rec, ProxyAt(Path.Combine(dir, name)) ?? rec.RecorderFiles.ContainsKey(name), name);
    }

    static string? ModName(string dir, GameRecord rec, bool ours, string name)
    {
        if (name == AgsName) return null;   // AMD's own file, not a mod
        var dll = Path.Combine(dir, rec.RecorderChained?.Name ?? name);
        if (!File.Exists(dll) || rec.RecorderChained == null && ours) return null;
        try
        {
            var v = PeVersionInfo.GetVersionInfo(dll);
            return new[] { v.ProductName, v.FileDescription, Middleware.ExportName(dll) }.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim() ?? name;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return name; }
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

    /// <summary>Whether "Record in all compatible games" arms the game.</summary>
    public static bool RecordsByDefault(Settings settings, GameState s) => settings.RecordAllGames && s.NoStutter == null;

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

    public ManualSource? Manual => _sources.OfType<ManualSource>().FirstOrDefault();

    // games the user removed: a scan or refresh that read them before the removal doesn't list them again (under _lock)
    readonly HashSet<string> _removed = [];
    Dictionary<string, Game> _hidden = [];   // the hidden games the last scan found installed (Visible); under _lock
    long _hideChanges;                       // HideGame and ShowGame, so a scan's read of the flags never undoes one; under _lock

    /// <summary>Under _lock: removed or hidden, so nothing lists or queues the game again.</summary>
    bool Unlisted(string id) => _removed.Contains(id) || _hidden.ContainsKey(id);

    bool UnlistedNow(string id) { lock (_lock) return Unlisted(id); }

    public IReadOnlyList<Game> HiddenGames { get { lock (_lock) return [.. _hidden.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)]; } }

    /// <summary>The found games the user didn't hide or leave out (<see cref="LeftOut"/>); the hidden ones go to
    /// <see cref="HiddenGames"/> unevaluated.</summary>
    List<Game> Visible(List<Game> found)
    {
        // still listed while a recorder of ours may be in its folder: nothing else would take it out
        var leftOut = found.Where(g => LeftOut(g) && !RecorderOrLayer(Store.LoadGame(g.Id))).Select(g => g.Id).ToHashSet();
        while (true)
        {
            long seen;
            lock (_lock) seen = _hideChanges;
            var hidden = found.DistinctBy(g => g.Id).Where(g => Store.LoadGame(g.Id).Hidden).ToDictionary(g => g.Id);
            lock (_lock)
            {
                if (seen != _hideChanges) continue;
                _hidden = hidden;
                return found.Where(g => !hidden.ContainsKey(g.Id) && !leftOut.Contains(g.Id)).ToList();
            }
        }
    }

    /// <summary>A store's game installed in a folder of <see cref="Settings.LeftOutFolders"/> or under it.</summary>
    bool LeftOut(Game g) => g.Store != Core.Store.Manual && Settings.LeftOutFolders is { Count: > 0 } folders && folders.Any(f => InOrUnder(f, g.InstallDir));

    static bool InOrUnder(string folder, string dir)
    {
        try
        {
            var (f, d) = (GameFiles.DirKey(folder), GameFiles.DirKey(dir));
            return f.Length > 0 && (d.Equals(f, StringComparison.OrdinalIgnoreCase)
                || d.StartsWith(Path.EndsInDirectorySeparator(f) ? f : f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));   // a drive's root keeps its separator
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    static bool RecorderOrLayer(GameRecord rec) => RecorderOnRecord(rec) || rec.VulkanLayer != null;

    public void LeaveOutFolder(string folder)
    {
        var key = GameFiles.DirKey(folder);
        if (!Directory.Exists(key)) throw new ArgumentException($"{folder} isn't a folder");
        if (Settings.LeftOutFolders?.Any(f => InOrUnder(f, key)) == true) return;
        var inside = Games.Select(s => s.Game).Where(g => g.Store != Core.Store.Manual && InOrUnder(key, g.InstallDir)).ToList();
        var held = new List<FileStream>();
        try
        {
            foreach (var g in inside)
            {
                lock (_lock)
                    if (_current == g.Id) throw new InvalidOperationException($"a compile of {g.Name} is in progress");
                if (!RecorderOrLayer(Store.LoadGame(g.Id))) continue;
                held.Add(CompileLock(g.Id) ?? throw new InvalidOperationException($"a compile of {g.Name} is in progress"));
                TakeRecorderOut(g);
            }
            Settings = Settings with { LeftOutFolders = [.. Settings.LeftOutFolders ?? [], key] };
        }
        finally { foreach (var h in held) h.Dispose(); }
        HashSet<string> ids;
        // after a reconcile that decided before the setting: a recorder it placed keeps the game listed, the next one takes it out
        lock (_recorderLock) ids = inside.Select(g => g.Id).Where(id => !RecorderOrLayer(Store.LoadGame(id))).ToHashSet();
        var dropped = new List<QueueItem>();
        lock (_lock)
        {
            dropped.AddRange(_queue.Where(q => ids.Contains(q.GameId) && q.GameId != _current));
            _queue.RemoveAll(q => dropped.Contains(q));
            foreach (var id in ids)
            {
                _whenIdle.Remove(id);
                _planOnly.Remove(id);
                _prompt.Remove(id);
            }
            _games.RemoveAll(s => ids.Contains(s.Game.Id));
        }
        Log?.Report($"{key}: left out of the library");
        foreach (var q in dropped) QueueChanged?.Invoke(q);
        KeepList();
    }

    public void ScanFolderAgain(string folder)
    {
        Settings = Settings with { LeftOutFolders = [.. (Settings.LeftOutFolders ?? []).Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))] };
        Log?.Report($"{folder}: looked through again");
    }

    /// <summary>A game the user added whose folder isn't the one they confirmed, or that has none confirmed yet.</summary>
    bool Unconfirmed(Game g) => g.Store == Core.Store.Manual && Manual?.Confirmed(g) != true;

    /// <summary>Tests: runs during a folder change, after the old stamp is taken out and before the new folder is saved.</summary>
    internal Action? FolderChanging { get; set; }

    /// <summary>A game added by hand as discovered before the user changed its folder.</summary>
    bool StaleCopy(Game g) => g.Store == Core.Store.Manual && Manual?.Stale(g) == true;

    Game? HiddenOwner(string exe) => HiddenGames.FirstOrDefault(g => GameFiles.Inside(g.InstallDir, exe));

    /// <summary>A store's game the exe belongs to: its own exe, or one inside its install.</summary>
    GameState? Listed(string exe)
    {
        var stores = Games.Where(s => s.Game.Store != Core.Store.Manual).ToList();
        // a collection's games share an install: the one whose exe it is first
        return stores.FirstOrDefault(s => string.Equals(s.Game.ExePath, exe, StringComparison.OrdinalIgnoreCase)
                                          || Collections.Exes(s.Game)?.Contains(Path.GetFileName(exe), StringComparer.OrdinalIgnoreCase) == true && GameFiles.Inside(s.Game.InstallDir, exe))
            ?? stores.FirstOrDefault(s => GameFiles.Inside(s.Game.InstallDir, exe));
    }

    public ManualAdd PreviewManualGame(string exePath)
    {
        if (Manual == null) throw new InvalidOperationException("adding games isn't available here");
        // a store's game first: its launcher may be what Resolve can't tell apart
        if (Listed(Path.GetFullPath(exePath)) is { } owner) return new(owner.Game, true);
        if (HiddenOwner(Path.GetFullPath(exePath)) is { } hidden) return new(hidden, true);
        var entry = ManualSource.Resolve(exePath);
        if (Listed(entry.Exe) is { } listed) return new(listed.Game, true);
        var id = ManualSource.IdOf(entry.Exe);
        var shown = Games.FirstOrDefault(s => s.Game.Id == id)?.Game;   // with the name the user gave it
        return Manual.Entries().FirstOrDefault(e => ManualSource.IdOf(e.Exe) == id) is { } had ? new(shown ?? ManualSource.ToGame(had), true)
            : Manual.InFolder(id) && shown != null ? new(shown, true)
            : new(ManualSource.ToGame(entry), false);
    }

    public string? ManualFolderProblem(string exePath, string installDir) =>
        ManualSource.RootProblem(installDir, exePath, Games.Select(s => s.Game));

    public ManualAdd AddManualGame(string exePath, string? installDir = null)
    {
        var manual = Manual ?? throw new InvalidOperationException("adding games isn't available here");
        if (Listed(Path.GetFullPath(exePath)) is { } owner) return new(owner.Game, true);
        if (HiddenOwner(Path.GetFullPath(exePath)) is { } hidden) return new(hidden, true);   // its scan would list the store's game, hidden
        // an added game's own exe keeps its entry, a folder's game its exe: resolved again, a patch's bigger exe would make another game
        var full = Path.GetFullPath(exePath);
        var entry = manual.Entries().FirstOrDefault(e => string.Equals(e.Exe, full, StringComparison.OrdinalIgnoreCase))
            ?? (Games.FirstOrDefault(s => manual.InFolder(s.Game.Id) && string.Equals(s.Game.ExePath, full, StringComparison.OrdinalIgnoreCase)) is { } found
                ? new ManualEntry(full, found.Game.InstallDir, found.Game.Name) : null)
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
            // another folder than the one checked: the old folder's stamp goes first (no check that began before counts),
            // then the new folder is saved; the reconcile below checks it in full and stamps it again
            var id = ManualSource.IdOf(entry.Exe);
            var had = installDir != null ? manual.Entries().FirstOrDefault(e => ManualSource.IdOf(e.Exe) == id) : null;
            if (had != null && !GameFiles.DirKey(had.InstallDir).Equals(entry.InstallDir, StringComparison.OrdinalIgnoreCase))
            {
                var old = ManualSource.ToGame(had);
                NewRemoval(old);
                var rec = Store.LoadGame(id);
                var oldDir = Path.GetDirectoryName(old.ExePath)!;
                if (!Foreign(oldDir)) StripIni(oldDir, Store, rec.RecorderChained?.Name, old.InstallDir, old.Name, RecorderLog);   // another copy's since: its own
                Store.SaveGame(id, rec);
                FolderChanging?.Invoke();
            }
            var (game, existed) = manual.Add(entry);
            added = new(game, existed);
        }
        lock (_lock) _removed.Remove(added.Game.Id);
        Log?.Report($"{added.Game.Name}: {(added.Existed ? "game folder set" : "added to the library")} ({added.Game.ExePath}, "
            + $"{(entry.Confirmed ? "confirmed" : "suggested")} folder {added.Game.InstallDir})");
        if (Games.FirstOrDefault(s => s.Game.Id == added.Game.Id) is not { } shown || IsUnchecked(shown))
            RefreshAsked(Following(Unreal(added.Game)));   // listed now, not after a scan that may be reading every game
        else if (entry.Confirmed)   // a folder's game listed already, though its entry is new
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
            var named = new[] { rec.RecorderExe ?? rec.RecorderMoveFrom, g.ExePath }.OfType<string>().Select(e => Path.GetDirectoryName(e)!).ToList();
            var dirs = new[] { rec.RecorderExe, rec.RecorderMoveFrom, rec.RecorderMoveTo, g.ExePath }.OfType<string>().Select(Path.GetDirectoryName).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(d => !Directory.Exists(d) || !Foreign(d)).ToList();
            bool Left(string dir) => OurProxyIn(dir) || OurPreloadIn(dir) || RecorderDataFiles.Concat(Recordings.AppFiles).Append(LedgerStamp).Any(f => File.Exists(Path.Combine(dir, f)));
            if (rec.RecorderFiles.Count > 0 || rec.RecorderChained != null || rec.RecorderRollback || dirs.Any(Left) || dirs.Any(d => !Directory.Exists(d) && !Gone(d) || IniPending(Store, d)))
            {
                if (GameRunning(g)) throw new InvalidOperationException($"{g.Name} is running: close the game first");
                rec.RecorderRollback = false;   // set again by a removal whose ini store failed
                foreach (var dir in dirs)   // as SCSKiller's own uninstall (RemoveAllRecorders)
                {
                    var more = RemoveRecorder(dir, rec, Store, g.Name, RecorderLog, g.InstallDir);
                    Store.SaveGame(gameId, rec);
                    if (more && named.Contains(dir, StringComparer.OrdinalIgnoreCase)
                        && !RemoveRecorderData(dir, RecordingPath(gameId), rec.RecordingInbox, () => GameRunning(g), rec.RecorderRollback || IniPending(Store, dir)) && GameRunning(g))
                        throw new InvalidOperationException($"{g.Name} started: close the game first");
                }
                if (rec.RecorderRollback || dirs.Any(d => IniPending(Store, d)))   // the game and the record keep the folder until it's out
                {
                    Store.SaveGame(gameId, rec);
                    throw new InvalidOperationException($"{g.Name}: the recorder's scskiller.ini can't be taken out yet: try again once nothing holds it");
                }
                // a folder that couldn't be reached (a drive away) stays on the record: SCSKiller's uninstall, or the game
                // added again, takes it out, checked to be that folder still
                var away = dirs.Where(d => !Directory.Exists(d) && !Gone(d)).ToList();
                bool Away(string? exe) => exe != null && away.Contains(Path.GetDirectoryName(exe)!, StringComparer.OrdinalIgnoreCase);
                if (!Away(rec.RecorderMoveFrom)) (rec.RecorderMoveFrom, rec.RecorderMoveTo) = (null, null);
                if (!Away(rec.RecorderExe) && !Away(rec.RecorderMoveFrom)) rec.RecorderInstallDir = null;
                Store.SaveGame(gameId, rec);
                RecorderLog($"{g.Name}: recorder removed before the game left the library ({string.Join(", ", dirs.Except(away))})"
                    + (away.Count > 0 ? $"; left in {string.Join(", ", away)} until it can be reached" : ""));
            }
        }
        DropKept(gameId);
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
        DeleteSessionSnapshots(gameId);
        Log?.Report($"{g.Name}: removed from the library");
        foreach (var q in dropped) QueueChanged?.Invoke(q);
    }

    public void HideGame(string gameId)
    {
        var g = Find(gameId).Game;
        if (g.Store == Core.Store.Manual) throw new InvalidOperationException("a game you added is removed, not hidden");
        using var own = CompileLock(gameId) ?? throw new InvalidOperationException($"a compile of {g.Name} is in progress");
        TakeRecorderOut(g);
        // saved before _hideChanges moves: a scan that reads the flags again then sees it
        var rec = Store.LoadGame(gameId);
        rec.Hidden = true;
        Store.SaveGame(gameId, rec);
        var dropped = new List<QueueItem>();
        bool compiling;
        lock (_lock)
        {
            if (!(compiling = _current == gameId))
            {
                _whenIdle.Remove(gameId);
                _planOnly.Remove(gameId);
                _prompt.Remove(gameId);
                dropped.AddRange(_queue.Where(q => q.GameId == gameId));
                _queue.RemoveAll(q => q.GameId == gameId);
                _hidden[gameId] = g;
                _hideChanges++;
                _games.RemoveAll(s => s.Game.Id == gameId);
            }
        }
        if (compiling)
        {
            rec.Hidden = false;
            Store.SaveGame(gameId, rec);
            lock (_lock)
            {
                _hidden.Remove(gameId);   // a scan meanwhile may have read the flag
                _hideChanges++;
            }
            Refresh(g);
            throw new InvalidOperationException($"a compile of {g.Name} is in progress");
        }
        Log?.Report($"{g.Name}: hidden from the library");
        foreach (var q in dropped) QueueChanged?.Invoke(q);
        KeepList();
    }

    /// <summary>A hidden or left-out game's first steps, under its <see cref="CompileLock"/>: its folder is never written after.</summary>
    void TakeRecorderOut(Game g)
    {
        var gameId = g.Id;
        lock (_lock)
            if (_current == gameId) throw new InvalidOperationException($"a compile of {g.Name} is in progress");
        if (GameRunning(g)) throw new InvalidOperationException($"{g.Name} is running: close the game first");
        // the game page's own Cancel and recorder switch
        if (Store.LoadGame(gameId).BeforeAfter is { Stage: not BeforeAfterStage.Done }) EndBeforeAfter(gameId, compile: false);
        if (IsUnchecked(Find(gameId))) Refresh(g);   // a placeholder doesn't say whether the recorder is in
        if ((IsUnchecked(Find(gameId)) || _unread.ContainsKey(gameId)) && RecorderOnRecord(Store.LoadGame(gameId)))
            throw new InvalidOperationException($"{g.Name}: couldn't read its files, so its recorder can't be taken out");
        if (Find(gameId).RecorderInstalled) UninstallRecorder(gameId);
    }

    public void ShowGame(string gameId)
    {
        Game? g;
        lock (_lock) g = _hidden.GetValueOrDefault(gameId);
        if (g == null) throw new ArgumentException($"{gameId} isn't a hidden game");
        var rec = Store.LoadGame(gameId);
        rec.Hidden = false;
        Store.SaveGame(gameId, rec);
        lock (_lock)
        {
            _hidden.Remove(gameId);
            _hideChanges++;
        }
        Log?.Report($"{g.Name}: shown in the library again");
        Refresh(g);
        if (ManageRecorders) ReconcileRecorders(gameId);
        KeepList();
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

    /// <summary>Each file the record placed in <paramref name="dir"/> is there with the bytes it records, and the folder's
    /// scskiller.ini with a hash this data folder's entry tracks and the installed copy's doesn't (read only): what another
    /// copy wrote there since never matches. The record is no proof of whose it is (<see cref="NamedByInstalled"/>).</summary>
    bool PlacedAsRecorded(string dir, GameRecord rec)
    {
        try
        {
            var ini = Path.Combine(dir, "scskiller.ini");
            if (!(rec.RecorderFiles.Count > 0
                  && rec.RecorderFiles.All(f => f.Key.Equals("scskiller.ini", StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(dir, f.Key)) && Sha256(Path.Combine(dir, f.Key)) == f.Value)
                  && File.Exists(ini) && GetIni(Store, dir)?.Tracked is { Length: > 0 } tracked && tracked.Split('|').Contains(Sha256(ini))))
                return false;
            return !(GetIni(new AppStore(Path.GetDirectoryName(DefaultLedger)!), dir)?.Tracked is { Length: > 0 } installed && installed.Split('|').Contains(Sha256(ini)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>A game record in the installed copy's data folder (read only) names <paramref name="dir"/> as its recorder's,
    /// a move's old folder or an offline session's: a data folder copied from it holds the same records, so this copy's
    /// record proves nothing there. Also true when a record or the folder listing can't be read; no installed data folder
    /// or no games\ in it: false.</summary>
    static bool NamedByInstalled(string dir)
    {
        try
        {
            IEnumerable<string> games;
            try { games = Directory.EnumerateDirectories(Path.Combine(Path.GetDirectoryName(DefaultLedger)!, "games")).ToList(); }
            catch (DirectoryNotFoundException) { return false; }
            foreach (var game in games)
            {
                System.Text.Json.JsonDocument doc;
                try
                {
                    using var f = new FileStream(Path.Combine(game, "state.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    doc = System.Text.Json.JsonDocument.Parse(f);
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { continue; }   // a game folder without a record
                using (doc)
                {
                    // only these fields, as every version since 1.2.0 writes them: any other field may differ between versions
                    var root = doc.RootElement;
                    if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return true;
                    var session = root.TryGetProperty("OfflineSession", out var o) ? o : default;
                    if (session.ValueKind is not (System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Object)) return true;
                    foreach (var exe in new[] { Field(root, "RecorderExe"), Field(root, "RecorderMoveFrom"), Field(session, "Exe") })
                        if (exe != null && Path.GetFullPath(exe) is var full && SameDir(Path.GetDirectoryName(full) ?? full, dir)) return true;
                }
            }
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NotSupportedException or InvalidOperationException) { return true; }

        // a string, or null when absent or null; any other kind can't be told (InvalidOperationException)
        static string? Field(System.Text.Json.JsonElement e, string name) =>
            e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != System.Text.Json.JsonValueKind.Null ? v.GetString() : null;
    }

    /// <summary>Makes a recorder without a marker that this data folder's record placed (a 1.2.5 portable's, from before
    /// markers) this copy's: its first marker goes in, never over one put there meanwhile, its scskiller.ini is tracked as
    /// this copy's, and the reconcile after it updates it as any. Not while the game runs, nor when it chains to a mod.
    /// <paramref name="takeOver"/>: any other copy's recorder, which this copy wants, its marker replaced and its proxy
    /// replaced by this build's (<see cref="GameRecord.RecorderClaimed"/>).</summary>
    void Claim(Game g, GameRecord rec, bool takeOver = false)
    {
        var dirs = new[] { g, RecordedAt(g, rec) }.Select(x => Path.GetDirectoryName(x.ExePath)!).Distinct(StringComparer.OrdinalIgnoreCase).Where(Foreign).ToList();
        if (dirs.Count == 0) return;
        if (_removedFromAll) throw new RemovedFromAllException();   // a claim arms the recorder as an install would
        if (!GameFolderWrite(g) || !GameFolderWrite(RecordedAt(g, rec))) throw new InvalidOperationException($"{g.Name} is running: close it first");
        if (dirs.FirstOrDefault(Chained) is { } chained) throw new InvalidOperationException($"the recorder in {chained} chains to a mod");
        // that copy's layer stays registered whatever this one does, so its ini stays that copy's too
        if (VulkanLayer.AppId(g) is { } appId && VulkanLayer.Others(appId, VulkanManifest(appId)).Count > 0)
            throw new InvalidOperationException("another SCSKiller copy's Vulkan recorder is registered for this game");
        if (takeOver)   // before any marker changes: a claim cut off after one still replaces the proxy
        {
            (rec.RecorderClaimed, rec.KeysPending) = (true, true);
            Store.SaveGame(g.Id, rec);
        }
        foreach (var dir in dirs)
        {
            var stamp = Path.Combine(dir, LedgerStamp);
            if (!takeOver) CreateLedger(stamp, LedgerText(LedgerDir));
            else if (WritesLedger) Place(stamp, Text(LedgerText(LedgerDir)), replace: true);
            else File.Delete(stamp);   // the installed copy's recorders carry no marker
            // the installed copy may have taken it since the check, before the marker: then it stays the installed copy's
            if (!takeOver && (NamedByInstalled(dir) || !PlacedAsRecorded(dir, rec)))
            {
                Unstamp(dir);
                throw new InvalidOperationException("it changed while it was checked");
            }
            var ini = Path.Combine(dir, "scskiller.ini");
            if (File.Exists(ini) && File.ReadAllText(ini).StartsWith(RecorderIni, StringComparison.Ordinal))
                SetIni(Store, dir, e => (e ?? new IniOwner(dir)) with { Tracked = Sha256(ini), Users = false, Remove = false, Install = g.InstallDir });
            RecorderLog(takeOver ? $"{g.Name}: took over the recorder another SCSKiller copy placed ({dir})"
                : $"{g.Name}: marked the recorder this data folder placed before markers as this copy's ({dir})");
        }
        rec.KeysPending = true;   // the keys file was written before the marker: written again
        _recorderNotes.TryRemove(g.Id, out _);
    }

    public void ReconcileRecorders(string? gameId = null)
    {
        if (gameId != null) gameId = Collections.RecordedBy(gameId) ?? gameId;   // a collection's other game: its first game's recorder
        lock (_recorderLock)
        {
            if (_removedFromAll) return;   // also a pass that waited for the lock while the removal ran
            foreach (var s in Games.Where(s => gameId == null || s.Game.Id == gameId))
                try { Reconcile(s); }
                catch (Exception e)   // one game's failure is that game's: it stays as it is, the others go on
                {
                    var note = s.RecorderEffective ? InstallFailed(s.Game, e, s.RecorderInstalled)
                        : "left as it is after an error, tried again at the next scan: " + FailureReason(e, Path.GetDirectoryName(s.Game.ExePath)!);
                    if (note == _recorderNotes.GetValueOrDefault(s.Game.Id)) continue;
                    _recorderNotes[s.Game.Id] = note;
                    RecorderLog($"{s.Game.Name}: recorder left as it is after an error, tried again at the next scan: {e.Message} ({e})");
                    Refresh(s.Game);
                }
            if (gameId == null) SweepVulkan();   // after: a game moved from a folder that's gone is registered at its new one first
        }
    }

    /// <summary>Settings' Remove recorders (an AppImage has no uninstall): <see cref="RemoveRecordersFromAllGames(CancellationToken)"/>,
    /// after which the app quits, unless something is left.</summary>
    public RecorderRemovals RemoveRecorders() => RemoveRecordersFromAllGames();

    public RecorderRemovals RemoveRecordersFromAllGames(CancellationToken ct = default)
    {
        _removedFromAll = true;
        RecorderRemovals removed;
        lock (_recorderLock)
        {
            removed = RemoveRecordersFromAllGames(Store, ProcessNames, ct);
            _recorderNotes.Clear();   // what a reconcile was to do next; under the lock, so a change waiting for it keeps its note
        }
        var listed = Games;
        foreach (var s in listed) Refresh(s.Game);
        var ids = listed.Select(s => s.Game.Id).ToLookup(id => Path.GetFileName(Store.GameDir(id)));
        return removed with { Games = [.. removed.Games.Select(r => ids[r.GameId].FirstOrDefault() is { } id ? r with { GameId = id } : r)] };
    }

    /// <summary>The user's removal from every game, the CLI's and the app's: <see cref="RemoveAllRecorders(AppStore,
    /// Func{IReadOnlySet{string}}, TimeSpan?, string, CancellationToken)"/> with time for a whole library.</summary>
    public static RecorderRemovals RemoveRecordersFromAllGames(AppStore store, Func<IReadOnlySet<string>>? running = null, CancellationToken ct = default) =>
        RemoveAllRecorders(store, running ?? RunningProcessNames, TimeSpan.FromMinutes(2), "removed from all games", ct);

    public const string RemovedFromAllNote = "recorders were removed from all games. Restart SCSKiller to record again";

    /// <summary>Install's, InstallVulkan's and an offline session's refusal once
    /// <see cref="RemoveRecordersFromAllGames(CancellationToken)"/> ran: <see cref="RemovedFromAllNote"/>. Removals and Off go on.</summary>
    sealed class RemovedFromAllException() : InvalidOperationException(RemovedFromAllNote);

    public const string ForeignNote = "placed by another SCSKiller copy, so it may still record. Turn it on here to take it over";

    /// <summary>True when the game was re-evaluated; a running game's change waits for it to exit.</summary>
    bool Reconcile(GameState s)
    {
        var (g, id) = (s.Game, s.Game.Id);
        if (OfflineLive(id) || _unread.ContainsKey(id) || IsUnchecked(s) || Collections.RecordedBy(id) != null) return false;   // its own end takes it out; a failed evaluation changes nothing
        var dir = Path.GetDirectoryName(g.ExePath)!;
        var rec = Store.LoadGame(id);
        // a recorder without a marker that this data folder's record placed, its files still the bytes it records, that no
        // record of the installed copy names (a 1.2.5 portable's, from before markers): this copy's
        if (!DefaultCopy && rec.RecorderExe is { } placed && rec.RecorderChained == null && rec.RecorderMoveFrom == null
            && Path.GetDirectoryName(placed) is { } placedIn && SameDir(placedIn, dir) && !File.Exists(Path.Combine(dir, LedgerStamp)) && Foreign(dir) && !Chained(dir)
            && PlacedAsRecorded(dir, rec) && !NamedByInstalled(dir))
        {
            if (!GameFolderWrite(g)) return false;   // taken over when the game exits
            try
            {
                ClaimStep?.Invoke();
                Claim(g, rec);
                Store.SaveGame(id, rec);
                Refresh(g);   // its page still shows the folder as another copy's
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { RecorderLog($"{g.Name}: couldn't mark the recorder in {dir} as this copy's: {e.Message}"); }   // left as another copy's
        }
        // another copy's recorder this copy wants (the switch, Record all games) becomes this copy's
        string? notTaken = null;
        if ((Foreign(g) || Foreign(RecordedAt(g, rec))) && !LeftOut(g)
            && RecorderWanted(rec.Recorder ?? RecorderOverride.Default, RecordsByDefault(Settings, s), s.RecorderSkip, PairFrames11(s.Engine, rec, Vendor.Caps)))
            try
            {
                Claim(g, rec, takeOver: true);
                Store.SaveGame(id, rec);
                Refresh(g);   // its page still shows the folder as another copy's
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { notTaken = "couldn't take over the recorder another SCSKiller copy placed: " + e.Message; }
        if (Foreign(g) || Foreign(RecordedAt(g, rec)))   // never adopted, armed, updated or emptied here
        {
            var foreignNote = notTaken ?? ForeignNote;
            if (_recorderNotes.GetValueOrDefault(id) == foreignNote) return false;
            _recorderNotes[id] = foreignNote;
            RecordersLog(Store.DataDir, $"{g.Name}: recorder {foreignNote}");
            Refresh(g);
            return true;
        }
        var name = RecorderDll(rec, s.Engine, Vendor.Caps, s.AgsHost);
        var dll = Path.Combine(dir, name);
        var read = ProxyAt(dll);
        // a recorder next to another exe than the game's (its launcher) is never loaded: it moves. Both folders are
        // recorded first, before anything waits for the game (a removal pending too), so the uninstall hook guards both;
        // kept until the old folder's recording is imported and its files are gone
        var was = rec.RecorderMoveFrom ?? rec.RecorderExe;
        var wasDir = was == null ? null : Path.GetDirectoryName(was);
        bool move = wasDir != null && !wasDir.Equals(dir, StringComparison.OrdinalIgnoreCase) && (Directory.Exists(wasDir) || !Gone(wasDir))
            && (rec.RecorderMoveFrom != null || rec.RecorderFiles.Count > 0 || rec.RecorderChained != null || rec.VulkanLayer != null || ProxyOnDisk(wasDir) || IniPending(Store, wasDir));
        if (move && (rec.RecorderMoveFrom == null || rec.RecorderMoveTo != g.ExePath))
        {
            (rec.RecorderMoveFrom, rec.RecorderMoveTo) = (was, g.ExePath);
            Store.SaveGame(id, rec);
        }
        if (rec.RecorderRollback || _rollbacks.ContainsKey(id))   // a failed install's leftovers go first, before any save, switch On or not
        {
            var left = GameRunning(g) ? "removed when the game exits"
                : TakeOut(g, rec, "a failed install's recorder") ? null : "couldn't remove all of it yet: tried again";
            if (left == null) _recorderNotes.TryRemove(id, out _); else _recorderNotes[id] = left;
            Refresh(g);
            return true;
        }
        // a dll that can't be read may be ours: nothing goes in or out until it reads (the rollback's removal reads its own),
        // but anti-cheat is looked for in full as for a recorder kept, and a finding takes out what is known ours now
        if (read is not { } ours)
        {
            if (s.AntiCheat == AntiCheat.None && FullCheck(g) is not AntiCheat.None and var found)
            {
                AntiCheatFound(g, found ?? AntiCheat.Other, unfinished: found == null);
                Refresh(g);
                return true;
            }
            var why = $"waits until {dll} can be read";
            if (why == _recorderNotes.GetValueOrDefault(id)) return false;
            _recorderNotes[id] = why;
            RecorderLog($"{g.Name}: recorder {why}");
            Refresh(g);
            return true;
        }
        var changed = false;
        if (rec.Recorder == null)   // not migrated: a recorder the user installed stays theirs
        {
            var found = ours && DefaultCopy && !s.AnyAntiCheat ? FullCheck(g) : AntiCheat.None;
            rec.Recorder = ours && DefaultCopy && !s.AnyAntiCheat && found == AntiCheat.None ? RecorderOverride.On : RecorderOverride.Default;
            Store.SaveGame(id, rec);
            changed = true;
            if (found is not AntiCheat.None)
            {
                AntiCheatFound(g, found ?? AntiCheat.Other, unfinished: found == null);   // takes it out, or when the game exits
                Refresh(g);
                return true;
            }
        }
        bool moved = false;
        if (move)
        {
            var old = RecordedAt(g, rec);
            string? left = null;
            _moving[id] = g;   // until the move is done, every check on the old folder (after each lock wait too) takes in the new one
            try
            {
                if (!Directory.Exists(wasDir)) left = $"moves when {wasDir} can be reached";   // a drive away: nothing there is known gone
                else if (GameRunning(old)) left = "moves when the game exits";   // also after the record's save waited for its lock
                else
                {
                    if (s.AntiCheat == AntiCheat.None)   // nothing of ours into an anti-cheat game's folder
                    {
                        StampLedger(dir);   // first: session files there without this copy's marker would make the folder another copy's
                        MoveSessionFiles(wasDir, dir);
                    }
                    if (!Uninstall(old, rec))
                        left = GameRunning(old) ? "moves when the game exits" : IniPending(Store, wasDir) ? $"moves once the scskiller.ini in {wasDir} can be taken out"
                            : $"couldn't move: the recording in {wasDir} couldn't be imported";
                }
                if (left == null)
                {
                    (rec.RecorderMoveFrom, rec.RecorderMoveTo) = (null, null);
                    Store.SaveGame(id, rec);
                    RecorderLog($"{g.Name}: recorder removed ({wasDir}): the game runs {g.ExePath}");
                    (ours, moved, changed) = (IsOurProxy(dll), true, true);
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
        var both = BothApis(s, rec);
        if (!both && (rec.VulkanLayer != null || VulkanRoute(g, s.Engine))) return ReconcileVulkan(s, rec, changed);
        // a recording that showed Vulkan keeps the DirectX recorder of what detection found: the game may switch back
        if (both && VulkanPlan.IsVulkan(s.Engine!)) s = s with { Engine = s.Engine! with { GraphicsApi = s.ApiDetected! } };
        // our proxy under another of its names that the record lost (a version that didn't know that name): taken out
        bool stray = ProxyNames.Any(n =>
        {
            try { return n != name && IsOurProxy(Path.Combine(dir, n)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }   // never blocks the recorder's own removal
        });
        if ((ours || stray || rec.RecorderFiles.Count > 0 || rec.RecorderChained != null) && (rec.RecorderExe != g.ExePath || rec.RecorderInstallDir != g.InstallDir))
        {
            (rec.RecorderExe, rec.RecorderInstallDir) = (g.ExePath, g.InstallDir);
            Store.SaveGame(id, rec);
        }
        // a recorder kept as it is gets no Install and its checks: it rests on a full walk now, not on the scan's verdict or
        // its cached clean walk (no watcher sees anti-cheat put deep in the install while the app was closed). The stamp is
        // taken first: an exe that changes during the check doesn't match it
        string? stamp = null;
        long gen = 0;
        bool clean = false;
        if (s.AntiCheat == AntiCheat.None && (ours || rec.RecorderFiles.Count > 0 || rec.RecorderChained != null))
        {
            (stamp, gen) = (Stamp(ArmTarget(g).ExePath), RemovalGen(id));
            if (FullCheck(g) is not AntiCheat.None and var found)
            {
                AntiCheatFound(g, found ?? AntiCheat.Other, unfinished: found == null);   // takes it out, or when the game exits
                Refresh(g);
                return true;
            }
            clean = true;
        }
        var as11 = As11(s.Engine, rec, Vendor.Caps);   // the pair's stage in rec, which s may predate
        bool want = !LeftOut(g) && !Orphaned(dir, rec) && !(s.Engine is { } vk && VulkanPlan.IsVulkan(vk)) && RecorderWanted(rec.Recorder.Value, RecordsByDefault(Settings, s), RecorderSkip(s with { Records11 = as11 }, ModSkip(g, dir, rec, ours, name)), PairFrames11(s.Engine, rec, Vendor.Caps));
        // another SCSKiller build's proxy, never a newer one's (a release's under a dev build, all 0.0.0.0), unless a before and
        // after run needs this build's (its panel and frame times are this build's), or this copy took it over
        bool mine = _pairInstall.ContainsKey(id) || rec.BeforeAfter is { Stage: not BeforeAfterStage.Done } || rec.RecorderClaimed;
        bool update = want && ours && ProxySha(name, g) is { } sha && (FileVersion(dll) <= FileVersion(ProxySource(name, g)!) || mine)
                      && (Sha256(dll) != sha || PreloadStale(g, dir, rec, s.Preload));
        string? note = null;
        bool reinstall = want && moved && (!ours || ProxySha(name, g) != null && (FileVersion(dll) <= FileVersion(ProxySource(name, g)!) || mine));   // as update
        // installed under another name (the game's API, its AGS or the GPU changed): out, then in under the right one
        var target = s.AgsHost ? AgsName : as11 ? Dll11 : Records9(s.Engine) ? Dll9 : Dll12;
        // an earlier build's AGS chain (AgsRealName): out, which puts the game's AGS back, then in under AgsChainName. The
        // wrapper there imports that name, so the two files can't be swapped one by one
        bool oldAgs = name == AgsName && (rec.RecorderChained?.Name == AgsRealName || update && AgsLinked(dll) == AgsRealName);
        bool rename = want && (ours || rec.RecorderFiles.Count > 0 || rec.RecorderChained != null) && (name != target || oldAgs);
        // a removal whose ini couldn't be taken out yet left the folder on the record: tried again here
        bool iniLeft = !want && rec.RecorderExe != null && Path.GetDirectoryName(rec.RecorderExe)!.Equals(dir, StringComparison.OrdinalIgnoreCase) && IniPending(Store, dir);
        if (want != ours || update || reinstall || rename || stray || (!want && (rec.RecorderFiles.Count > 0 || rec.RecorderChained != null)) || iniLeft)
        {
            if (GameRunning(g))
            {
                note = update ? "updates when the game exits" : want ? "installs when the game exits" : "removed when the game exits";
                if (!want && name == Dll11 && Frames11(s.Engine, Vendor.Caps)) Revoke(dir, true, RecorderLog);   // no next launch is admitted
            }
            else
                try
                {
                    if (want)
                    {
                        (stamp, gen) = (Stamp(ArmTarget(g).ExePath), RemovalGen(id));
                        if ((rename || stray) && !Uninstall(g, rec))
                            throw new InvalidOperationException(IniPending(Store, dir) ? "its scskiller.ini can't be taken out yet" : "the recording couldn't be imported");
                        Install(g, rec, rename ? target : name, s.Preload);
                        if (!Attest(ArmTarget(g), rec, stamp, gen)) note = ForeignIni(dir, rec);   // Install's full checks were clean
                    }
                    else Uninstall(g, rec);
                    if (rec.RecorderClaimed)
                    {
                        rec.RecorderClaimed = false;
                        Store.SaveGame(id, rec);
                    }
                    _recorderSkips.TryRemove(id, out _);
                    KeepFailure(id, rec, null);
                    RecorderLog($"{g.Name}: recorder {(update ? "updated" : want ? "installed" : "removed")} ({dir})");
                    changed = true;
                }
                catch (UnauthorizedAccessException e) when (update)   // the old recorder still works: not a skip
                {
                    RecorderLog($"{g.Name}: recorder couldn't update: {e.Message}");
                    note = InstallFailed(g, e, true);
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
                catch (RemovedFromAllException e) { note = e.Message; }
                catch (Exception e) when (e is IOException or InvalidOperationException)
                {
                    note = (update ? "couldn't update: " : want ? "couldn't install: " : "couldn't remove: ") + e.Message;
                    RecorderLog($"{g.Name}: recorder {note}");
                    note = want ? InstallFailed(g, e, update) : "couldn't remove: " + FailureReason(e, dir);
                }
        }
        else if (want && !GameRunning(g) && _removedFromAll) note = RemovedFromAllNote;   // a recorder the removal left (its game ran then): never armed again, Off takes it out
        else if (want && !GameRunning(g))
        {
            if (ProxySha(name, g) != null && (s.Preload == null || File.Exists(PreloadSource(s.Preload, g))))   // in place and current, its source still there
                changed |= KeepFailure(id, rec, null);
            if (rec.KeysPending || rec.RecorderClaimed)   // a rewrite that waited for the game to exit, a takeover's recorder already this build's
                try
                {
                    if (rec.KeysPending) WriteKeys(g, rec);
                    rec.RecorderClaimed = false;
                    Store.SaveGame(id, rec);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecorderLog($"{g.Name}: couldn't write the keys file: {e.Message}"); }
            // an install cut off before its ini: written first, unsigned, so the frames-only check sees its frames_only=1
            if (clean && !File.Exists(Path.Combine(dir, "scskiller.ini"))) UpdateRecorderIni(g, rec, "");
            if (clean && !Attest(ArmTarget(g), rec, stamp, gen)) note = ForeignIni(dir, rec);
            else UpdateRecorderIni(g, rec);
        }
        // no recorder of ours here: what one left (an earlier version's recorder off, a file that was locked) goes
        else if (!want && !ours && rec.RecorderExe == null && RecorderDataFiles.Concat(Recordings.AppFiles).Append(LedgerStamp).Any(f => File.Exists(Path.Combine(dir, f)))
                 && !GameRunning(g))
            try
            {
                if (RemoveRecorderData(dir, RecordingPath(id), rec.RecordingInbox, () => GameRunning(g), rec.RecorderRollback || IniPending(Store, dir)))
                {
                    RecorderLog($"{g.Name}: the recorder's data files removed ({dir})");
                    changed = true;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }   // the next scan tries again
        if (both) note ??= ReconcileLayerBeside(g, rec, want && rec.RecorderExe is { } armed && SameDir(Path.GetDirectoryName(armed)!, dir), ref changed);
        if (!want) KeepFailure(id, rec, null);
        if (note != _recorderNotes.GetValueOrDefault(id))
        {
            if (note == null) _recorderNotes.TryRemove(id, out _); else _recorderNotes[id] = note;
            changed = true;
        }
        if (changed) Refresh(g);
        return changed;
    }

    /// <summary>An install or update that failed while the recorder is wanted: on the record (<see cref="GameRecord.RecorderFailure"/>)
    /// for the game page until one succeeds, and the reconcile's note.</summary>
    string InstallFailed(Game g, Exception e, bool update)
    {
        var line = $"couldn't {(update ? "update" : "install")}: {FailureReason(e, Path.GetDirectoryName(g.ExePath)!)}";
        KeepFailure(g.Id, null, line);
        _recorderSkips.TryRemove(g.Id, out _);   // an earlier access denial is no longer the reason
        return line;
    }

    /// <summary>True when the record changed. <paramref name="rec"/> null: a fresh one, so a failed step's unsaved changes stay unsaved.</summary>
    bool KeepFailure(string id, GameRecord? rec, string? line)
    {
        try
        {
            rec ??= Store.LoadGame(id);
            if (rec.RecorderFailure == line) return false;
            rec.RecorderFailure = line;
            Store.SaveGame(id, rec);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }   // the next attempt keeps it
    }

    /// <summary>Why an install failed, in plain words. The system's own messages are in the user's language: those are
    /// told by their Win32 code only.</summary>
    internal static string FailureReason(Exception e, string dir)
    {
        if (e.HResult is unchecked((int)0x800700E1) or unchecked((int)0x800700E2))   // ERROR_VIRUS_INFECTED, ERROR_VIRUS_DELETED
            return "Windows Security or another antivirus removed the recorder file";
        if (e is FileNotFoundException { FileName: var f } && (f == null || !f.StartsWith(Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            return "the recorder file is missing from SCSKiller's folder";
        if (e is IOException or UnauthorizedAccessException && e.HResult >>> 16 == 0x8007)   // FACILITY_WIN32
            return (e.HResult & 0xFFFF) switch
            {
                5 => "Windows denied access to the game's folder",
                32 or 33 => "a file in the game's folder is in use",
                39 or 112 => "the disk is full",
                var code => $"Windows error {code}",
            };
        if (e is System.ComponentModel.Win32Exception w) return $"Windows error {w.NativeErrorCode}";
        var text = e.Message.Replace(Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase);   // our own English text: its first sentence
        return text.IndexOf(". ", StringComparison.Ordinal) is > 0 and var end ? text[..end] : text.TrimEnd('.');
    }

    static readonly string[] SessionFiles = ["scskiller_creates.csv", "scskiller.log", FrameLog.FileName];

    /// <summary>The last session's csv, log and frame log go along with a recorder that moves, so the game page keeps its
    /// report; of two with the same name the newer by write time stays.</summary>
    static void MoveSessionFiles(string from, string to)
    {
        RefuseForeign(from);
        foreach (var name in SessionFiles)
        {
            var src = new FileInfo(Path.Combine(from, name));
            if (!src.Exists) continue;
            var dst = new FileInfo(Path.Combine(to, name));
            if (dst.Exists && dst.LastWriteTimeUtc >= src.LastWriteTimeUtc) src.Delete();
            else src.MoveTo(dst.FullName, overwrite: true);
        }
    }

    /// <summary>Also to recorders.log in the data folder, where the uninstall hook and the offline cleanup write too.</summary>
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
    // False: the line wasn't written.
    public static bool AppendLog(string dataDir, string file, string line, long maxBytes = 0)
    {
        Mutex? gate = null;
        var held = false;
        try
        {
            var path = Path.GetFullPath(Path.Combine(dataDir, file));
            try
            {
                gate = NamedMutex.Open(LogMutexName(path));
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
                    return true;
                }
                catch (IOException) when (attempt < 5) { Thread.Sleep(20); }   // a reader that shares no writes holds it a moment
        }
        catch (Exception) { return false; }   // a log line never fails what it logs
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

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _proxySha = new(StringComparer.OrdinalIgnoreCase);
    string? ProxySha(string name = Dll12, Game? g = null) => ProxySource(name, g) is not { } src || !File.Exists(src) ? null
        : _proxySha.GetOrAdd(src, Sha256);

    /// <summary>The app's copy of the recorder that goes in as <paramref name="name"/>: the AGS wrapper for <see cref="AgsName"/>,
    /// the DirectX 9 recorder for <see cref="Dll9"/>, its x86 build for a 32-bit <paramref name="g"/>.</summary>
    string? ProxySource(string name, Game? g = null) => name == AgsName ? AgsDll
        : name == Dll9 ? _proxyDll == null ? null : g != null && GameFiles.Is32Bit(g.ExePath) ? Path.Combine(Path.GetDirectoryName(_proxyDll)!, "x86", Dll9) : Path.Combine(Path.GetDirectoryName(_proxyDll)!, Dll9)
        : _proxyDll;

    static Version FileVersion(string path) =>
        PeVersionInfo.GetVersionInfo(path) is var v ? new(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart) : new();

    /// <summary>Running processes' names without the extension, from the snapshot: none opened.</summary>
    static IReadOnlySet<string> RunningProcessNames() =>
        ProcessTree.Snapshot().Select(p => Path.GetFileNameWithoutExtension(p.Exe)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>A recorder goes into a game only with the data folder SCSKiller's uninstall knows (<see cref="AppStore.Moved"/>),
    /// or with SCSKILLER_TEST_RECORDER=1.</summary>
    internal static bool RecorderInstallAllowed(string dataDir, string? moved, string? testFlag) =>
        moved == null || !Path.GetFullPath(dataDir).Equals(moved, StringComparison.OrdinalIgnoreCase) || testFlag == "1";

    /// <param name="name">the recorder's file name in the game (<see cref="RecorderDll(GameRecord, EngineInfo?, VendorCaps)"/>)</param>
    /// <param name="preload">the preloader's name (<see cref="GameState.Preload"/>); null: none, and one placed before goes</param>
    void Install(Game g, GameRecord rec, string name, string? preload)
    {
        if (_removedFromAll) throw new RemovedFromAllException();   // also a change that waited for the lock while the removal ran
        if (!RecorderInstallAllowed(Store.DataDir, AppStore.Moved, Environment.GetEnvironmentVariable("SCSKILLER_TEST_RECORDER")))
            throw new InvalidOperationException("the data folder is moved (SCSKILLER_DATA_DIR), which SCSKiller's uninstall doesn't know: set SCSKILLER_TEST_RECORDER=1 to install a recorder anyway, and take it out yourself");
        RefuseAntiCheat(g, rec);   // fresh, not the cached scan: a patch may have added one
        if (!GameFolderWrite(g)) throw new InvalidOperationException($"{g.Name} is running");
        var src = ProxySource(name, g) ?? throw new FileNotFoundException($"the recorder for {name} was not found next to the app");
        var dir = Path.GetDirectoryName(g.ExePath)!;
        var dll = Path.Combine(dir, name);
        var ini = Path.Combine(dir, "scskiller.ini");
        var (iniState, iniJudged) = JudgeOwnIni(g, rec, dir, rec.RecorderChained?.Name);
        if (iniState == IniState.Unknown) throw new IOException($"{ini} can't be read: not installing until it can");
        bool iniOurs = iniState is IniState.Absent or IniState.Ours;
        bool chain = File.Exists(dll) && !IsOurProxy(dll);
        if (chain && name == Dll9) throw new InvalidOperationException($"{dll} is another mod's or the game's own: the DirectX 9 recorder goes in front of nothing. Not installing.");
        // the wrapper loads only beside the game's AGS: renamed here, or already there under the name this build's links
        var agsChained = Path.Combine(dir, AgsChainName);
        if (name == AgsName && !chain && (!File.Exists(agsChained) || File.Exists(dll) && AgsLinked(dll) != AgsChainName))
            throw new InvalidOperationException($"{dll}: the game's own AGS isn't there to put the recorder in front of. Not installing.");
        if (name == AgsName && AgsModSkip(g.ExePath) is { } agsMod) throw new InvalidOperationException($"{Path.Combine(dir, Dll12)}: {agsMod}. Not installing.");   // a mod put back by a rename's removal
        if (name == AgsName && GameFiles.AgsUnfit(chain ? dll : agsChained, g.ExePath, src) is { } agsUnfit)
            throw new InvalidOperationException($"{dll}: the game's AGS ({agsUnfit}) isn't one the recorder can wrap. Not installing.");
        if (!iniOurs && FramesOnly(name, Vendor.Caps)) throw new InvalidOperationException($"{ini} is not SCSKiller's: the frame times recorder needs its own. Not installing.");
        if (preload != null && PreloadTaken(dir, preload, rec)) throw new InvalidOperationException($"{Path.Combine(dir, preload)} already exists and is not SCSKiller's (another mod or the game's own). Not installing.");
        if (preload != null && !File.Exists(PreloadSource(preload, g))) throw new FileNotFoundException($"the recorder's preloader {preload} was not found next to the app");
        (rec.RecorderExe, rec.RecorderInstallDir) = (g.ExePath, g.InstallDir);
        StampLedger(dir);   // first: a recorder cut off halfway in is still this copy's
        // read before anything is renamed; saved with the chain, before any rename or copy: a stop after the copy leaves the
        // name and kind on record (RecorderDll)
        var srcSha = Sha256(src);
        var had = rec.RecorderFiles.TryGetValue(name, out var hadSha);
        if (chain)
        {
            // the game's own AGS, only a build the wrapper fits (its version and exports): anything else, a mod's too, stays
            if (name == AgsName ? GameFiles.AgsUnfit(dll, g.ExePath, src) != null : !rec.RecordAlongsideMod)
                throw new InvalidOperationException($"{dll} already exists and is not SCSKiller's (another mod or wrapper). Not replacing it.");
            if (name != AgsName && ChainBlocker(dll) is { } why) throw new InvalidOperationException($"{dll}: {why}. Not renaming it.");
            if (!iniOurs) throw new InvalidOperationException($"{ini} is not SCSKiller's: the chain to {dll} needs its own. Not renaming it.");
            var to = Path.Combine(dir, ChainOf(name));
            if (File.Exists(to)) throw new InvalidOperationException($"{to} already exists. Not renaming {dll} over it.");
            rec.RecorderChained = new ChainedDll(ChainOf(name), Sha256(dll));
            rec.RecorderFiles[name] = srcSha;
            InstallStep?.Invoke("chain");
            Store.SaveGame(g.Id, rec);   // before the rename: no renamed file is ever missing from the manifest
            if (!GameFolderWrite(g))   // it may have started while the save waited for the record's lock
            {
                rec.RecorderChained = null;
                if (had) rec.RecorderFiles[name] = hadSha!; else rec.RecorderFiles.Remove(name);
                Store.SaveGame(g.Id, rec);
                throw new InvalidOperationException($"{g.Name} is running");
            }
            File.Delete(ini);   // ours: a next= left by a removal cut off before it went would make this attempt's rename look complete (Orphaned)
            File.Move(dll, to);
        }
        else
        {
            rec.RecorderFiles[name] = srcSha;
            Store.SaveGame(g.Id, rec);
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
        try
        {
            // the file renamed, not only the one checked: an update may have swapped it in between
            if (name == AgsName && GameFiles.AgsUnfit(agsChained, g.ExePath, src) is { } unfit)
                throw new InvalidOperationException($"{dll}: the game's AGS changed while the recorder went in ({unfit}). Not installing.");
            Place(dll, CopyOf(src), replace: true);
        }
        catch when (chain)
        {
            File.Move(Path.Combine(dir, ChainOf(name)), dll);
            rec.RecorderChained = null;
            Store.SaveGame(g.Id, rec);
            throw;
        }
        // from here on the proxy is in the game folder: whatever happens, the full check runs and the manifest is saved
        var antiCheat = AntiCheat.None;
        var late = false;
        Exception? failed = null;
        try
        {
            rec.RecorderFiles[name] = Sha256(dll);
            InstallStep?.Invoke("copied");
            antiCheat = GameFiles.DetectAntiCheat(g, quick: true, late: () => late = true);   // an update may have added one since the check, likely next to the exe
            if (antiCheat == AntiCheat.None && !GameFolderWrite(g)) throw new InvalidOperationException($"{g.Name} started while the recorder was installed");
            if (antiCheat == AntiCheat.None) PlacePreload(g, rec, dir, preload);
            if (antiCheat == AntiCheat.None && iniOurs)
            {
                Store.SaveGame(g.Id, rec);   // the folder and the dll on the record before the ini exists: the uninstall hook finds them
                // changed while its entry was saved: the user's, left alone
                if (!WriteIni(dir, IniText(g, rec), iniJudged, rec.RecorderChained?.Name, g.InstallDir, () => GameFolderWrite(g),
                        step => { if (step == "saved") InstallStep?.Invoke("ini"); }, install: true)
                    && !GameFolderWrite(g))
                    throw new InvalidOperationException($"{g.Name} started while the recorder was installed");
            }   // else: the user's own scskiller.ini, left alone
        }
        catch (Exception e) { failed = e; }
        // the full check before anything that may wait for a lock (the keys file's, the record's)
        if (antiCheat == AntiCheat.None) antiCheat = GameFiles.DetectAntiCheat(g, late: () => late = true);
        if (antiCheat == AntiCheat.None && failed == null)
            try
            {
                InstallStep?.Invoke("keys");
                WriteKeys(g, rec);   // a recorder installed again records only what's new
                Store.SaveGame(g.Id, rec);
                // last, after every wait: an update may have added anti-cheat while this one waited for a lock
                antiCheat = GameFiles.DetectAntiCheat(g, late: () => late = true);
                if (antiCheat == AntiCheat.None)
                {
                    lock (_scanLock) _scan?.Remove(g.Id);   // the next refresh evaluates the game again, its anti-cheat too
                    return;
                }
            }
            catch (Exception e) { failed = e; }
        if (antiCheat != AntiCheat.None)
        {
            AntiCheatFound(g, antiCheat, installing: rec, unfinished: late);
            throw AntiCheatRefusal(g, antiCheat, late);
        }
        TakeOut(g, rec, $"the recorder install failed ({failed!.Message})");
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
    }

    /// <summary>Whether SCSKiller may write into the game's folder now: not while the game runs (<see cref="GameRunning"/>;
    /// the staged warm runs under its name too). Every write there checks it: the proxy's copy (<see cref="Install"/>),
    /// <see cref="TakeOut"/>, the keys file (<see cref="WriteKeys"/>), the ini (<see cref="UpdateRecorderIni"/>), the
    /// inbox's rotation (<see cref="ImportRecording"/>), <see cref="ClearRecording"/> and the reconcile's install and
    /// removal; the uninstall hook uses <see cref="Running"/>, the same check without the watcher. The one write that doesn't
    /// wait: <see cref="StripIni"/>, SCSKiller's own ini out when anti-cheat is found.</summary>
    bool GameFolderWrite(Game g) => !GameRunning(g);

    /// <summary>The one answer to "is this game running" before touching it: the watcher sees it playing (which uses
    /// <see cref="RunsFromItsFolder"/> too), or <see cref="Running"/> (the uninstall hook's check) with
    /// <see cref="ProcessNames"/> (replaceable for tests), less the names only programs elsewhere run under
    /// (<see cref="OnlyElsewhere"/>). No process is opened.</summary>
    bool GameRunning(Game g) => IsPlaying(g.Id) || RunsUnwatched(g);

    /// <summary><see cref="GameRunning"/> for the caches outside the game's folder (D3DSCache, the driver's): an Always record
    /// session still waiting for its launch doesn't run the game, and its folder stays guarded by <see cref="GameRunning"/>.</summary>
    bool CacheInUse(Game g) => IsPlaying(g.Id) || RunsUnwatched(g, waitingRuns: _armed.ContainsKey(g.Id) && D3DSCacheInGame(g));

    /// <summary>A D3DSCache folder of the game (by its dbs, on record, or named for its exe, as the fold finds them) the user
    /// linked into the game's own folders: written only when they may be.</summary>
    bool D3DSCacheInGame(Game g) => D3DSCache.FoldersOf(Path.Combine(LocalAppData, "D3DSCache"), g).Concat(GameD3DSCacheDirs(g, Store.LoadGame(g.Id)))
        .Select(d => D3DSCache.Followed(d, out _) ?? d)
        .Any(d => new[] { g.InstallDir, Path.GetDirectoryName(g.ExePath)! }.Any(own => GameFiles.Inside(own, Path.Combine(d, "x"))));

    /// <summary><see cref="GameRunning"/> less the watcher's own list, which still holds a game while its exit is handled.</summary>
    // _offline: its session's process, which the list may not show (just resumed, or exited before its cleanup ran)
    bool RunsUnwatched(Game g, bool waitingRuns = true) => _offline.ContainsKey(g.Id) && (waitingRuns || !_armed.ContainsKey(g.Id)) || Running(g.ExePath, g.InstallDir, ProcessNames(), OnlyElsewhere)
                                  || _moving.TryGetValue(g.Id, out var to) && to != g && Running(to.ExePath, to.InstallDir, ProcessNames(), OnlyElsewhere);

    /// <summary>Every running process named <paramref name="name"/> runs an exe outside <paramref name="roots"/>, read from
    /// the system's process list (<see cref="ProcessPath"/>): another program named like an exe of the game's folders, which
    /// may run all the time (Gaijin's gjagent, also in War Thunder's root). A path that can't be told counts as inside, and a
    /// staged warm (<see cref="OurCopy"/>) as the game.</summary>
    bool OnlyElsewhere(string name, string?[] roots) =>
        Processes(true) is var all
        && all.Where(p => Path.GetFileNameWithoutExtension(p.Exe).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } named
        && named.All(p => Outside(p.Pid, roots) && !OurCopy(p, all));

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
    /// A process named like another exe of the folders counts only if it runs from them (<see cref="Outside"/>). A game of a
    /// collection runs only as its own exes.</summary>
    bool RunsFromItsFolder(Game g)
    {
        string?[] roots = [g.InstallDir, Path.GetDirectoryName(g.ExePath)];
        if ((Collections.Exes(g) is { } own ? own.Select(n => Path.GetFileNameWithoutExtension(n)).ToHashSet(StringComparer.OrdinalIgnoreCase) : ExeNamesIn(roots)) is not { } names)
            return true;   // can't tell: as if it runs
        if (names.Count == 0) return false;
        var all = Processes(false);
        var exe = Path.GetFileName(g.ExePath);
        return all.Any(p => names.Contains(Path.GetFileNameWithoutExtension(p.Exe)) && NotOurs(p, all)
                            && (p.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase) || !Outside(p.Pid, roots)));
    }

    /// <summary>A process SCSKiller runs under a game's exe name: a staged warm, a copy of scskiller_warm.exe that it runs
    /// from &lt;data folder&gt;\games\&lt;id&gt;\work\stage-&lt;its pid&gt;-&lt;n&gt;\. Told without opening the process, and
    /// never by anything a game's folder could carry:
    /// - it runs from this data folder's work\stage-* folders, so a process the staged copy starts from its own image, one
    ///   left after its warm exited, or one a packaged app's activator started is ours too;
    /// - another SCSKiller's (portable, another data folder): its stage folder names a running scskiller_warm.exe whose image
    ///   is byte for byte its own, and neither runs from a discovered game's folders. A game's exe is never that binary.
    ///   On Linux, where the folder names Wine's pid, that warm is the Wine scskiller_warm.exe whose job token the staged
    ///   process carries (<see cref="StagingWarm"/>);
    /// - its path can't be read (it exited, or no drive letter maps its volume): its parent is a scskiller_warm.exe in this
    ///   SCSKiller's <see cref="WarmJob"/> (<see cref="OwnWarm"/>: that process is opened, never the game-named one);
    /// - on Linux, a Wine process that carries this SCSKiller's job token and is a member of its job in this data folder
    ///   (WarmJob.In), read from /proc.</summary>
    bool OurCopy((int Pid, int Parent, string Exe) p, List<(int Pid, int Parent, string Exe)> all)
    {
        var token = false;
        CarriesOurToken(p.Pid, ref token);
        if (token) return true;
        // unreadable path: a child of our warm, or (a Vulkan warm's replayer worker) of its replayer named like it
        if (ProcessPath(p.Pid) is not { } path)
            return all.Any(w => w.Pid == p.Parent && (IsWarm(w) && OwnWarm(w.Pid)
                || w.Exe.Equals(p.Exe, GameFiles.Names) && all.Any(g => g.Pid == w.Parent && IsWarm(g) && OwnWarm(g.Pid))));
        var games = Path.Combine(Store.DataDir, "games");
        if (Staged(games, path) || GameFiles.FinalPath(games) is { } real && Staged(real, path)) return true;
        if (StagedBy(path) is not { } pid) return false;
        StagingWarm(p.Pid, all, ref pid);
        if (!all.Any(w => w.Pid == pid && IsWarm(w)) || ProcessPath(pid) is not { } warm) return false;
        string?[] roots = [.. Games.SelectMany(s => new[] { s.Game.InstallDir, Path.GetDirectoryName(s.Game.ExePath) })];
        // a Vulkan warm stages the fossilize-replay.exe beside it (the segheap\ one's is in its parent folder)
        return Outside(p.Pid, roots) && Outside(pid, roots) && (SameBytes(path, warm) || Path.GetDirectoryName(warm) is { } bin
            && (SameBytes(path, Path.Combine(bin, Replayer)) || Path.GetDirectoryName(bin) is { } up && SameBytes(path, Path.Combine(up, Replayer))));

        static bool IsWarm((int Pid, int Parent, string Exe) w) => w.Exe.Equals("scskiller_warm.exe", GameFiles.Names);

        static bool Staged(string games, string path) => GameFiles.Inside(games, path, GameFiles.Names)
            && Path.GetRelativePath(games, path).Split(Path.DirectorySeparatorChar) is [_, var work, var stage, _, ..]
            && IsWorkDir(work) && stage.StartsWith("stage-", GameFiles.Names);
    }

    /// <summary>The pid a work\stage-&lt;pid&gt;-&lt;n&gt; folder above <paramref name="path"/> names (scskiller_warm's own,
    /// --stage-path folders may sit between); null when there is none.</summary>
    static int? StagedBy(string path)
    {
        for (var d = Path.GetDirectoryName(path); d != null && Path.GetDirectoryName(d) is { } up; d = up)
            if (IsWorkDir(Path.GetFileName(up))
                && Path.GetFileName(d).Split('-') is [var stage, var pid, var n] && stage.Equals("stage", GameFiles.Names)
                && int.TryParse(pid, System.Globalization.NumberStyles.None, null, out var id) && id > 0
                && n.Length > 0 && n.All(char.IsAsciiDigit))
                return id;
        return null;
    }

    /// <summary>The Vulkan warm's replayer, beside scskiller_warm.exe; staged under the game's exe name.</summary>
    const string Replayer = "fossilize-replay.exe";

    /// <summary>The files hold the same bytes; false when either can't be read. A warm binary is well under the cap.</summary>
    static bool SameBytes(string a, string b)
    {
        try
        {
            var (x, y) = (new FileInfo(a), new FileInfo(b));
            return x.Length == y.Length && x.Length <= 64 << 20 && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The process is in this SCSKiller's <see cref="WarmJob"/>, which holds every warm it starts. Replaceable for tests.</summary>
    internal Func<int, bool> OwnWarm { get; set; } = pid => WarmJob.Current.Contains(pid);

    /// <summary>Sets <paramref name="ours"/> when the process is a warm of this SCSKiller's told by the job's token.</summary>
    partial void CarriesOurToken(int pid, ref bool ours);

    /// <summary>Sets <paramref name="pid"/>, the pid a stage folder names, to the system's pid of the warm that staged the
    /// process <paramref name="staged"/>, where the two differ (Wine's pids on Linux): none when it can't be told.</summary>
    partial void StagingWarm(int staged, List<(int Pid, int Parent, string Exe)> all, ref int pid);

    /// <summary>Not <see cref="OurCopy"/>, and still running once that was told: a staged warm that exits after the list was
    /// read can't be told ours any more (on Linux its path and token go first), and isn't the game either.</summary>
    bool NotOurs((int Pid, int Parent, string Exe) p, List<(int Pid, int Parent, string Exe)> all)
    {
        if (OurCopy(p, all)) return false;
        var exited = false;
        ExitedNow(p.Pid, ref exited);
        return !exited;
    }

    /// <summary>The process has exited or is exiting as a whole. Linux only: elsewhere a listed process counts as running.</summary>
    partial void ExitedNow(int pid, ref bool exited);

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
        NewRemoval(g);
        if (rec.VulkanLayer != null && UnregisterVulkan(rec, Store, g.Name, RecorderLog))
            try { Store.SaveGame(g.Id, rec); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecorderLog($"{g.Name}: couldn't save the recorder's record: {e.Message}"); }
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
        // the record's folder and the game's own (the keys files too), then the record's other folders (our files only)
        var exeDir = Path.GetDirectoryName(g.ExePath)!;
        var folders = new[] { (Dir: Path.GetDirectoryName(at.ExePath)!, Install: rec.RecorderInstallDir ?? at.InstallDir, Keys: true), (Dir: exeDir, Install: g.InstallDir, Keys: true) }
            .Concat(new[] { rec.RecorderExe, rec.RecorderMoveTo }.OfType<string>().Select(e => (Dir: Path.GetDirectoryName(e)!, Install: rec.RecorderInstallDir ?? at.InstallDir, Keys: false)))
            .DistinctBy(f => f.Dir, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var f in folders.Where(f => Directory.Exists(f.Dir) && Foreign(f.Dir)).ToList())
        {
            folders.Remove(f);
            RecorderLog($"{g.Name}: recorder left in {f.Dir}: another SCSKiller copy's");
        }
        var dirs = folders.Select(f => f.Dir).ToList();
        bool done;
        if (!GameFolderWrite(g) || !GameFolderWrite(at))
        {
            foreach (var f in folders)   // ours, beside a running game too: its next launch is a pass-through
                StripIni(f.Dir, Store, rec.RecorderChained?.Name, f.Install, g.Name, RecorderLog);
            (rec.RecorderRollback, _rollbacks[g.Id], done) = (true, true, false);
            RecorderLog($"{g.Name}: {cause}: the recorder is removed when the game exits");
        }
        else try
        {
            var failed = false;
            rec.RecorderRollback = false;   // set again by a removal whose ini store failed
            foreach (var f in folders)
                try
                {
                    RemoveRecorder(f.Dir, rec, Store, g.Name, RecorderLog, f.Install);
                    if (!Directory.Exists(f.Dir)) continue;   // gone or away: nothing to delete (File.Delete throws on a missing folder)
                    // not through WriteKeys: no wait for the recording lock with a proxy maybe still in place
                    foreach (var file in f.Keys ? Recordings.AppFiles : [])
                        try { File.Delete(Path.Combine(f.Dir, file)); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    // else it keeps them its copy's; while its ini is still to go, the marker keeps the folder this copy's
                    if (f.Keys && !RecorderDataFiles.Concat(Recordings.AppFiles).Any(x => File.Exists(Path.Combine(f.Dir, x))) && !IniPending(Store, f.Dir) && !rec.RecorderRollback)
                        File.Delete(Path.Combine(f.Dir, LedgerStamp));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    failed = true;   // the next folder is tried all the same
                    RecorderLog($"{g.Name}: {cause}; removing the recorder from {f.Dir} failed ({e.Message}): tried again");
                }
            done = !failed && !rec.RecorderRollback && rec.RecorderFiles.Count == 0 && !dirs.Any(d => IniPending(Store, d) || !Directory.Exists(d) && !Gone(d));
            (rec.KeysIndexHash, rec.RecorderRollback) = (null, !done);
            if (done) _rollbacks.TryRemove(g.Id, out _); else _rollbacks[g.Id] = true;
            RecorderLog(done ? $"{g.Name}: {cause}: recorder removed ({string.Join(", ", dirs)})"
                : $"{g.Name}: {cause}: recorder removed ({string.Join(", ", dirs)}) but for files that can't be taken out yet: tried again later");
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
    /// record, not saved yet; else the record is read under the recorder lock. <paramref name="unfinished"/>: a check that
    /// ran out of time or failed: the recorder out all the same, but a before and after recording is kept and only an
    /// evaluation's own state is saved (<paramref name="fresh"/>), and the pass walks the game again while no compile runs.</summary>
    void AntiCheatFound(Game g, AntiCheat found, Evaluation? fresh = null, GameRecord? installing = null, bool unfinished = false)
    {
        NewRemoval(g);   // first: a stamp whose check began before this is never written
        bool changed;
        lock (_scanLock)
        {
            _scan ??= Store.LoadScan();
            if (unfinished && !_unknown.ContainsKey(g.Id) && _verdicts.TryGetValue(g.Id, out var real))   // a finding meanwhile: kept as it is
                (found, fresh, unfinished) = (real, null, false);
            changed = fresh != null || !unfinished && _scan.GetValueOrDefault(g.Id) is { } ev && ev.AntiCheat != found;
            if (fresh != null) _scan[g.Id] = fresh;
            else if (changed) _scan[g.Id] = _scan[g.Id] with { AntiCheat = found, Clean = null, Found = null, FoundAt = null };   // no marker path from this check: the next scan evaluates it in full
            _scanStarted[g.Id] = ++_evaluations;   // sticky: only an evaluation started after it can clear it
            _verdicts[g.Id] = found;   // an older evaluation's state published later keeps it (WithVerdict)
            if (unfinished) _unknown[g.Id] = true;
            else _unknown.TryRemove(g.Id, out _);
        }
        lock (_lock)
            if (_games.FindIndex(x => x.Game.Id == g.Id) is var i and >= 0 && _games[i].AntiCheat != found) _games[i] = _games[i] with { AntiCheat = found };
        TakeOutNow(g, unfinished ? "its anti-cheat check didn't finish" : $"{found} found", installing);
        // a walk out of time under a compile's disk load isn't a finding: the pair waits, its next run needs a clean walk to arm
        if (!unfinished) EndPair(g, installing);
        if (!changed) return;
        lock (_scanLock)
            try { Store.SaveScan(_scan); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecorderLog($"{g.Name}: couldn't save that it uses {found}: {e.Message}"); }
    }

    /// <summary>Anti-cheat or VAC in a game whose recorder is another SCSKiller copy's: only that recorder goes, our proxy and
    /// preloader while its scskiller.ini is held, then the ini, and its ledger once no session or data file is left there.
    /// Its session and data files stay. An ini that may arm a session at the user's own risk (Always record or a risk
    /// session: pid= or risk=, or no sig= yet while it waits for the launch), none or one that can't be read, a chained mod
    /// or a running game: all of it stays (the page says another copy placed it).</summary>
    void ForeignTakeOut(string dir, Game g, string why)
    {
        var (ini, ledger) = (Path.Combine(dir, "scskiller.ini"), Path.Combine(dir, LedgerStamp));
        var owner = "another SCSKiller copy";
        try
        {
            string? named = null;
            if (!Directory.EnumerateFileSystemEntries(dir, LedgerStamp).Any()) owner = "the installed SCSKiller copy";
            else
                try { owner = $"the SCSKiller copy of {named = File.ReadAllText(ledger).Trim()}"; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // it stays, the recorder still goes
            if (!GameFolderWrite(g) || Chained(dir) || !DeleteIfText(ini, Plain, () =>
                {
                    var storage = Path.Combine(dir, "_storage_");   // the copies a game made of its folder's DLLs (Onimusha)
                    var stored = ProxyNames.Concat(PreloadNames).Select(n => Path.Combine(storage, n)).Where(f => IsOurProxy(f) || IsOurPreload(f)).ToList();
                    if (stored.Count > 0 && (Reparse(storage) || MarkedElsewhere(storage)))
                        throw new IOException($"{storage} holds the recorder too but links elsewhere or is marked by another copy");   // the ini stays: tried again
                    // a preloader under any DLL name the game imports, not only the usual ones
                    foreach (var p in ProxyNames.Select(n => Path.Combine(dir, n)).Where(IsOurProxy)
                                 .Concat(Directory.EnumerateFiles(dir, "*.dll").Where(IsOurPreload)).Concat(stored).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                        File.Delete(p);
                }))
            {
                RecorderLog($"{g.Name}: {why}: the recorder in {dir} is {owner}'s, which may record there at the user's own risk: left as it is");
                return;
            }
            if (named != null && !RecorderDataFiles.Concat(Recordings.AppFiles).Any(f => File.Exists(Path.Combine(dir, f))))
                DeleteIfText(ledger, text => text.Trim() == named);
            RecorderLog($"{g.Name}: {why}: took {owner}'s recorder out of {dir}, its session files left");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            RecorderLog($"{g.Name}: {why}: couldn't take {owner}'s recorder out of {dir}: {e.Message}");
        }

        static bool Plain(string text)
        {
            var keys = text.Split('\n').Select(l => IniKey(l.TrimEnd('\r'))).OfType<string>().ToList();
            bool Has(string k) => keys.Contains(k, StringComparer.OrdinalIgnoreCase);
            return text.StartsWith(RecorderIni, StringComparison.Ordinal) && Has("sig") && !Has("pid") && !Has("risk");
        }
    }

    static bool Chained(string dir) => new[] { ChainName, ChainName11, AgsChainName }.Any(n => File.Exists(Path.Combine(dir, n)))
        || File.Exists(Path.Combine(dir, AgsRealName))
        && (!File.Exists(Path.Combine(dir, AgsName))   // an earlier build's AGS rename cut off before its wrapper went in
            || new[] { AgsName, AgsName + TempSuffix }.Select(n => Path.Combine(dir, n)).Any(w => IsOurProxy(w) && AgsLinked(w) == AgsRealName));

    /// <summary>Our recorder out of a game with anti-cheat or a mod that changes every pipeline, now (<see cref="TakeOut"/>:
    /// no wait for the recording lock, the inbox kept for the next import), or, while the game runs or holds the proxy, as
    /// soon as it exits. Under the recorder lock, with the record read there (or Install's, which holds the lock). Any
    /// failure, the check of what's ours included (a dll locked against reading), leaves the removal pending. For anti-cheat
    /// and VAC (<paramref name="anyCopys"/>) another SCSKiller copy's recorder too, never its session files
    /// (<see cref="ForeignTakeOut"/>): safety goes before whose it is.</summary>
    void TakeOutNow(Game g, string why, GameRecord? installing = null, bool anyCopys = true)
    {
        NewRemoval(g);
        if (OfflineLive(g.Id)) return;   // its own end takes it out
        // before the wait for the recorder lock (a compile's import may hold it for minutes): the next launch is refused
        // meanwhile, beside the game's exe and where the record says the recorder is (a followed exe, a move pending); the
        // removal's StripIni takes each folder off the list once its ini is out
        var exeDir = Path.GetDirectoryName(g.ExePath)!;
        GameRecord? recorded = null;
        try { recorded = Store.LoadGame(g.Id); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        foreach (var dir in new[] { g.ExePath, recorded?.RecorderExe, recorded?.RecorderMoveFrom, recorded?.RecorderMoveTo }.OfType<string>().Select(Path.GetDirectoryName).OfType<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase).Where(d => File.Exists(Path.Combine(d, "scskiller.ini"))))
            Revoke(dir, true, RecorderLog);
        lock (_recorderLock)
        {
            var rec = installing ?? Store.LoadGame(g.Id);
            var foreign = new[] { g, RecordedAt(g, rec) }.DistinctBy(x => Path.GetDirectoryName(x.ExePath)!, StringComparer.OrdinalIgnoreCase)
                .Where(x => Path.GetDirectoryName(x.ExePath) is var d && Directory.Exists(d) && Foreign(d)).ToList();
            if (foreign.Count > 0 && !anyCopys) return;   // its own copy takes it out
            foreach (var x in foreign) ForeignTakeOut(Path.GetDirectoryName(x.ExePath)!, x, why);
            if (Foreign(g) && Foreign(RecordedAt(g, rec))) return;
            try
            {
                // a proxy that can't be read (held by the game or another process) counts as ours: the removal takes the ini out first
                if (!ProxyOnDisk(g) && !ProxyOnDisk(RecordedAt(g, rec)) && rec.RecorderFiles.Count == 0 && rec.RecorderChained == null && rec.VulkanLayer == null)
                {
                    // nothing of the recorder: a signed ini left goes too, from every folder the record names (or its folder's
                    // entry keeps it for later)
                    if (!Foreign(exeDir)) StripIni(exeDir, Store, null, g.InstallDir, g.Name, RecorderLog);
                    foreach (var dir in new[] { rec.RecorderExe, rec.RecorderMoveFrom, rec.RecorderMoveTo }.OfType<string>().Select(e => Path.GetDirectoryName(e)!)
                                 .Where(d => !d.Equals(exeDir, StringComparison.OrdinalIgnoreCase) && !(Directory.Exists(d) && Foreign(d))).Distinct(StringComparer.OrdinalIgnoreCase))
                        StripIni(dir, Store, null, rec.RecorderInstallDir ?? g.InstallDir, g.Name, RecorderLog);
                    return;
                }
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

    /// <summary>Games whose recorder dll couldn't be read at their last evaluation: it may be ours, so the watcher checks
    /// them for anti-cheat (<see cref="RecorderIn"/>) until it reads.</summary>
    readonly ConcurrentDictionary<string, bool> _unreadProxies = new();

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
        if (VacCheck(g)) throw new InvalidOperationException($"{g.Name} uses VAC: a DLL in its folder could get the account banned. Not installing.");
        var late = false;
        var antiCheat = GameFiles.DetectAntiCheat(g, late: () => late = true);
        if (antiCheat == AntiCheat.None) return;
        AntiCheatFound(g, antiCheat, installing: rec, unfinished: late);
        throw AntiCheatRefusal(g, antiCheat, late);
    }

    static InvalidOperationException AntiCheatRefusal(Game g, AntiCheat antiCheat, bool late = false) =>
        new(late ? $"{g.Name}: its anti-cheat check didn't finish. Not installing."
            : $"{g.Name} uses {antiCheat}: a DLL in its folder could get the account banned. Not installing.");

    // The AGS hook reads the app name a game registers (AMD only). It follows the recorder's own admission: for a normal
    // session, not a VAC or Riot game and not one where anti-cheat was ever seen (AgsIneligible); for a risk/offline session
    // (the anti-cheat opt-in), wherever the recorder is admitted. No vendor line = the proxy never patches AGS there.
    internal bool AgsEligible(Game g, GameRecord rec, bool risk, bool ignoreLatch = false)
    {
        if (OwnDriver != GpuVendor.Amd) return false;
        // The recorder's admission (and so the AGS hook) is refused even under risk when VAC can't be inspected or the
        // game's folder can't be listed (the proxy's decide() returns "VAC:" or "?"). Mirror that here.
        bool vac;
        try { vac = VacCheck(g); }
        catch { return false; }
        if (!AgsFolderListable(g)) return false;
        // risk session: at the user's own risk, VAC/Riot aside. An offline EAC session (ignoreLatch) keeps the VAC/Riot and
        // inspection checks but skips the anti-cheat-seen latch, since recording it offline is the whole point.
        return risk || ((ignoreLatch || !rec.AgsIneligible) && !vac && !g.Id.StartsWith("riot:", StringComparison.Ordinal)
            && !GameFiles.RiotGames(g.InstallDir) && !GameFiles.RiotGames(g.ExePath));
    }

    // The proxy's anti_cheat_beside refuses on a listing error or past 100,000 entries ("?"); mirror that, whole folder.
    static bool AgsFolderListable(Game g)
    {
        try
        {
            long n = 0;
            foreach (var _ in Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(g.ExePath)!))
                if (++n > 99_998) return false;   // the proxy's count includes . and ..
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { return false; }
    }

    /// <summary>The ini lines the stamp signs besides the exe's (<see cref="Signed"/>, the proxy's signed_stamp): vendor=amd where
    /// the AGS hook may go (<see cref="AgsEligible"/>; <paramref name="session"/> and <paramref name="riskSession"/> as it takes
    /// them), the countdown's lines (<see cref="RunIni"/>; never in a session's ini) and the traversal_ ones
    /// (<see cref="Traversal"/>). Signed, so a hand edit can't switch any of them on.</summary>
    internal string SignedLines(Game g, GameRecord rec, bool session = false, bool riskSession = false) =>
        (AgsEligible(g, rec, risk: riskSession, ignoreLatch: session) ? "vendor=amd\r\n" : "")
        + (session || rec.OfflineSession != null ? "" : RunIni(g, rec.BeforeAfter)) + Traversal(g, rec);

    /// <summary><see cref="OverlayIni"/> for a game with no anti-cheat verdict, "" otherwise: no ini turns the countdown on
    /// in a game with anti-cheat.</summary>
    string RunIni(Game g, BeforeAfterRecord? ba) =>
        Games.FirstOrDefault(s => s.Game.Id == g.Id) is { AntiCheat: AntiCheat.None, Vac: false } && !_verdicts.ContainsKey(g.Id) ? OverlayIni(ba) : "";

    /// <summary>The in-game countdown's lines while a before and after run that shows it is armed or playing; "" otherwise.</summary>
    internal static string OverlayIni(BeforeAfterRecord? ba)
    {
        var tag = ba is not { Overlay: true } ? null : ba.Stage switch
        {
            BeforeAfterStage.BeforeArmed or BeforeAfterStage.BeforePlaying => "before",
            BeforeAfterStage.AfterArmed or BeforeAfterStage.AfterPlaying => "after",
            _ => null,
        };
        return tag == null ? "" : $"overlay=1\r\nab_play_s={ba!.Minutes * 60}\r\nab_tag={tag}\r\n";
    }

    /// <summary>The ini's stamp signed again with the <see cref="SignedLines"/> due now, when it carries a valid stamp for the
    /// exe as it is now (<see cref="AttestedStamp"/>): the exe's lines stay those its last clean check signed, so an exe
    /// changed meanwhile isn't admitted. Null otherwise (none, stale, or a session's, bound to its process).</summary>
    string? Resigned(Game g, GameRecord rec) =>
        AttestedStamp(g) is { } exe ? Signed(g, exe, SignedLines(g, rec)) : null;

    /// <summary>The recorder's scskiller.ini (root=: <see cref="RootValue"/>). <paramref name="stamp"/>: <see cref="Signed"/>'s,
    /// or "" for none (the proxy records nothing then).</summary>
    string IniText(Game g, GameRecord rec, string stamp = "") => IniText(g, rec, DbCap(g)?.ToString(), stamp);

    string IniText(Game g, GameRecord rec, string? cap, string stamp) =>
        IniOf(cap, ChainNext(Path.GetDirectoryName(g.ExePath)!, rec),
            FramesOnly(File.Exists(Path.Combine(Path.GetDirectoryName(g.ExePath)!, Dll11)) ? Dll11 : Dll12, Vendor.Caps), RootValue(g), stamp);

    /// <summary>scskiller.ini as SCSKiller writes it, from its values (null: the line isn't written): 1.0 to 1.2 wrote the header,
    /// max_db_bytes= and next=; 1.3 adds frames_only=, root= and the stamp (<see cref="Signed"/>'s lines, with
    /// <see cref="SignedLines"/>). The one writer, which <see cref="OurIniBytes"/> renders again to recognise its files.</summary>
    static string IniOf(string? cap, string? next, bool framesOnly, string? root, string stamp) =>
        RecorderIni
        + (cap == null ? "" : $"{IniComments[0]}\r\nmax_db_bytes={cap}\r\n")
        + (next == null ? "" : $"{IniComments[1]}\r\nnext={next}\r\n")
        + (framesOnly ? $"{IniComments[4]}\r\nframes_only=1\r\n" : "")
        + (root == null ? "" : $"{IniComments[2]}\r\nroot={root}\r\n")
        + (stamp == "" ? "" : IniComments[3] + "\r\n" + stamp);

    static readonly string[] IniComments =
    [
        "; the recording limit per game (Settings): no new records once scskiller.db has this many bytes",
        "; the game's own d3d12.dll (a mod), renamed by SCSKiller and put back when the recorder is removed",
        "; the game's install folder: the recorder checks its entries for anti-cheat too",
        "; the game's exe when SCSKiller last found no anti-cheat in its folders: the recorder records only while it is unchanged",
        "; frame times for a before and after recording, nothing else is recorded",
    ];

    /// <summary>The recorder's traversal_ keys (proxy/traversal.h), in the order <see cref="IniOf"/> writes them.</summary>
    internal static readonly string[] TraversalKeys =
        ["suballocate", "buffers", "textures", "upload", "readback", "uav_buffers", "uav_textures", "reuse_frames", "reuse_ms", "heap_mb", "spare_heaps", "max_pool_mb",
         "max_resource_mb", "probe_ms", "dred", "zeroed", "zeroed_textures", "zeroed_upload", "zeroed_reuse", "copy_high", "test_budget_mb"];

    /// <summary>traversal_&lt;key&gt;= lines for <paramref name="values"/> (by key without the prefix): each of
    /// <see cref="TraversalKeys"/> with a whole number as SCSKiller writes one, anything else left out.</summary>
    static string TraversalLines(IReadOnlyDictionary<string, string> values) =>
        string.Concat(TraversalKeys.Where(k => values.TryGetValue(k, out var v) && uint.TryParse(v, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var n) && n.ToString(System.Globalization.CultureInfo.InvariantCulture) == v)
            .Select(k => $"traversal_{k}={values[k]}\r\n"));

    /// <summary>SCSKILLER_TEST_TRAVERSAL ("suballocate=1;probe_ms=2"), for a test of the traversal stutter fix: the recorder's
    /// ini gets those traversal_ keys. Unset: none.</summary>
    internal string? TraversalSpec { get; set; } = Environment.GetEnvironmentVariable("SCSKILLER_TEST_TRAVERSAL");

    /// <summary>The traversal_ lines of the game's ini: <see cref="TraversalSpec"/>'s when set, else
    /// <see cref="TraversalFix.Keys"/> while the fix is on for the game (<see cref="TraversalFix.On"/>).</summary>
    string Traversal(Game g, GameRecord rec) =>
        !string.IsNullOrWhiteSpace(TraversalSpec) ? TestTraversal()
        : Games.FirstOrDefault(s => s.Game.Id == g.Id) is { } s && TraversalFix.On(Settings, s with { TraversalOverride = rec.Traversal }, Vendor.Caps) ? TraversalLines(TraversalFix.Keys) : "";

    string TestTraversal()
    {
        var spec = TraversalSpec;
        if (string.IsNullOrWhiteSpace(spec)) return "";
        var values = new Dictionary<string, string>();
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (part.IndexOf('=') is var at and > 0) values[part[..at].Trim()] = part[(at + 1)..].Trim();
        return TraversalLines(values);
    }

    /// <summary>The chained mod's name for next=, while its renamed file is there.</summary>
    static string? ChainNext(string dir, GameRecord rec) => rec.RecorderChained is { Name: not (AgsChainName or AgsRealName) } c && File.Exists(Path.Combine(dir, c.Name)) ? c.Name : null;

    /// <summary>The bytes are exactly what <see cref="IniOf"/> writes for the values parsed out of them: ASCII, CRLF lines, its
    /// comments, its key order and nothing else, numbers as it writes them and within the types it writes them from,
    /// <paramref name="chain"/> (the record's) as the only next= and <paramref name="root"/> (the install's, as
    /// <see cref="RootValue(Game)"/> gives it) as the only root=. A BOM, another encoding, whitespace, another order or any line
    /// SCSKiller doesn't write never matches. A byte-exact copy of SCSKiller's file can't be told from it.</summary>
    internal static bool OurIniBytes(byte[] bytes, string? chain, string? root)
    {
        if (bytes.Any(b => b >= 0x80)) return false;
        var text = System.Text.Encoding.ASCII.GetString(bytes);
        var values = new Dictionary<string, string>();
        foreach (var line in text.Split("\r\n"))
            if (!line.StartsWith(';') && line.IndexOf('=') is var at and > 0 && !values.TryAdd(line[..at], line[(at + 1)..])) return false;
        string? V(string key) => values.GetValueOrDefault(key);
        static bool Long(string? v) => long.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n.ToString(System.Globalization.CultureInfo.InvariantCulture) == v;
        static bool Int(string? v) => int.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n.ToString(System.Globalization.CultureInfo.InvariantCulture) == v;
        var (cap, next, frames, rooted, sig) = (V("max_db_bytes"), V("next"), V("frames_only"), V("root"), V("sig"));
        if (cap != null && !Long(cap) || next != null && next != chain || frames != null && frames != "1" || rooted != null && rooted != root) return false;
        var (vendor, overlay, play, tag) = (V("vendor"), V("overlay"), V("ab_play_s"), V("ab_tag"));
        var stamp = "";
        if (sig != null)
        {
            var (size, time, pid, started, risk) = (V("exe_size"), V("exe_time"), V("pid"), V("pid_time"), V("risk"));
            if (rooted == null || sig.Length != 40 || !sig.All(char.IsAsciiHexDigitLower) || !Long(size) || !Long(time)
                || (pid == null) != (started == null) || pid != null && (!Int(pid) || !Long(started)) || risk != null && (pid == null || risk != "1")
                || vendor != null && vendor != "amd" || (overlay == null) != (play == null) || (overlay == null) != (tag == null)
                || overlay != null && (overlay != "1" || !Int(play) || tag is not ("before" or "after")))
                return false;
            stamp = $"exe_size={size}\r\nexe_time={time}\r\n" + (pid == null ? "" : BoundTo(int.Parse(pid), long.Parse(started!))) + (risk == null ? "" : RiskLine)
                + (vendor == null ? "" : "vendor=amd\r\n") + (overlay == null ? "" : $"overlay=1\r\nab_play_s={play}\r\nab_tag={tag}\r\n")
                + TraversalLines(values.Where(v => v.Key.StartsWith("traversal_", StringComparison.Ordinal)).ToDictionary(v => v.Key["traversal_".Length..], v => v.Value))
                + $"sig={sig}\r\n";
        }
        return text == IniOf(cap, next, frames != null, rooted, stamp);
    }

    enum IniState { Absent, Ours, NotOurs, Unknown }

    static string FullDir(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

    /// <summary>The folder's <see cref="IniOwner"/> in recorder-inis.json, by its full path; null when none, or when the
    /// folder can't be reached (nothing is known about it then).</summary>
    internal static IniOwner? GetIni(AppStore store, string dir)
    {
        dir = FullDir(dir);
        return Directory.Exists(dir) ? store.LoadInis().GetValueOrDefault(dir) : null;
    }

    /// <summary>Changes the folder's <see cref="IniOwner"/> under the store's lock (<paramref name="change"/> gets null when
    /// there is none); an entry that holds nothing goes. Nothing is recorded for a folder that can't be reached: its game's
    /// record keeps naming it instead.</summary>
    internal static void SetIni(AppStore store, string dir, Func<IniOwner?, IniOwner?> change, TimeSpan? wait = null)
    {
        dir = FullDir(dir);
        if (!Directory.Exists(dir)) return;
        store.UpdateInis(all =>
        {
            var now = change(all.GetValueOrDefault(dir));
            if (now is { Tracked: not null } or { Users: true } or { Remove: true }) all[dir] = now with { Dir = dir };
            else all.Remove(dir);
        }, wait);
    }

    /// <summary>SCSKiller wants the folder's ini out and it isn't yet: it can't be read, or couldn't be deleted.</summary>
    internal static bool IniPending(AppStore store, string dir) => GetIni(store, dir) is { Remove: true };

    /// <summary>The user's from now on (also once it's untracked), until SCSKiller writes its own there: nothing of SCSKiller's to take out there.</summary>
    static void MarkUsers(AppStore store, string dir, TimeSpan? wait = null) =>
        SetIni(store, dir, e => (e ?? new IniOwner(dir)) with { Tracked = null, Users = true, Remove = false }, wait);

    /// <summary>Whose the scskiller.ini in <paramref name="dir"/> is (<see cref="IniOwner"/>, the folder's), and its bytes as
    /// read (null when absent, unreadable or over 64 KB). A tracked one is SCSKiller's only while its bytes hash to one the entry
    /// names (what SCSKiller wrote, or what it judged its own and is about to replace); otherwise, and over 64 KB (SCSKiller
    /// never writes that much), it's the user's from then on, until SCSKiller writes its own there. An untracked one, or one tracked as unknown
    /// (""), only when <see cref="OurIniBytes"/> renders its bytes again with <paramref name="chain"/> and the root of
    /// <paramref name="installDir"/> (the entry's when null; none known: an untracked root= is never SCSKiller's). One that
    /// can't be read, or a folder that can't be reached (a drive away, not gone), is unknown: never written or deleted.</summary>
    static (IniState State, byte[]? Bytes) JudgeIni(string dir, AppStore store, string? chain, string? installDir, TimeSpan? wait = null)
    {
        var ini = Path.Combine(dir, "scskiller.ini");
        var entry = GetIni(store, dir);
        byte[] bytes;
        try
        {
            if (!File.Exists(ini))
            {
                if (Directory.Exists(dir) ? !Directory.EnumerateFiles(dir, "scskiller.ini").Any() : Gone(dir))   // listed: not just unreadable
                {
                    // nothing of SCSKiller's to take out; the user's marking stays (an editor may save by replacing the file)
                    // under the lock, listed again: a write since is tracked by its writer
                    if (entry is { Tracked: not null } or { Remove: true })
                        SetIni(store, dir, e => e == null || Directory.Exists(dir) && Directory.EnumerateFiles(dir, "scskiller.ini").Any() ? e : e with { Tracked = null, Remove = false }, wait);
                    return (IniState.Absent, null);
                }
                if (!Directory.Exists(dir)) return (IniState.Unknown, null);
            }
            using var f = new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (f.Length > 65536)
            {
                MarkUsers(store, dir, wait);
                return (IniState.NotOurs, null);
            }
            bytes = new byte[f.Length];
            f.ReadExactly(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (IniState.Unknown, null); }
        if (entry?.Tracked is { Length: > 0 } tracked)
        {
            if (tracked.Split('|').Contains(Convert.ToHexString(SHA256.HashData(bytes)))) return (IniState.Ours, bytes);
            return MarkUsers(store, dir, bytes, wait);   // changed since SCSKiller wrote it
        }
        var root = (installDir ?? entry?.Install) is { } install ? RootValue(dir, install) : null;
        if (entry is not { Users: true } && OurIniBytes(bytes, chain ?? entry?.Chain, root)) return (IniState.Ours, bytes);
        // its root= can't be checked without the install it should name: not known to be the user's either
        if (entry is not { Users: true } && root == null && System.Text.Encoding.ASCII.GetString(bytes).Contains("\r\nroot=")) return (IniState.Unknown, bytes);
        // seen not to be SCSKiller's: the user's, also once edited into SCSKiller's shape
        return entry is { Users: true } ? (IniState.NotOurs, bytes) : MarkUsers(store, dir, bytes, wait);
    }

    /// <summary>The user's from now on, decided again under the store's lock, where SCSKiller writes its own ini
    /// (<see cref="WriteIni"/>): the entry was read before the bytes, so a write between the two reads is SCSKiller's when the
    /// entry tracks the bytes there now. Bytes changed again, or unreadable, are unknown: judged again later.</summary>
    static (IniState State, byte[]? Bytes) MarkUsers(AppStore store, string dir, byte[] judged, TimeSpan? wait)
    {
        var (key, ini) = (FullDir(dir), Path.Combine(dir, "scskiller.ini"));
        (IniState, byte[]?) verdict = (IniState.Unknown, null);
        if (!Directory.Exists(key)) return verdict;
        store.UpdateInis(all =>
        {
            byte[] now;
            try
            {
                using var f = new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                now = new byte[Math.Min(f.Length, 65537)];
                f.ReadExactly(now);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            var entry = all.GetValueOrDefault(key);
            if (entry?.Tracked is { Length: > 0 } t && t.Split('|').Contains(Convert.ToHexString(SHA256.HashData(now)))) verdict = (IniState.Ours, now);
            else if (now.AsSpan().SequenceEqual(judged))
            {
                all[key] = (entry ?? new IniOwner(key)) with { Dir = key, Tracked = null, Users = true, Remove = false };
                verdict = (IniState.NotOurs, judged);
            }
        }, wait);
        return verdict;
    }

    /// <summary>The ini's bytes are still <paramref name="judged"/> (null: still absent), read right before a write or a delete
    /// that rests on that judgement; false when they changed or can't be read.</summary>
    static bool IniUnchanged(string ini, byte[]? judged) => IniNow(ini, judged) == true;

    /// <summary>The ini compared with <paramref name="judged"/> (null: absent): true when the same, false when it was read and
    /// differs (one there now where none was too), null when nothing is known: it can't be read now (held by another
    /// process), or it was there and is gone.</summary>
    static bool? IniNow(string ini, byte[]? judged)
    {
        try
        {
            if (!File.Exists(ini)) return Directory.EnumerateFiles(Path.GetDirectoryName(ini)!, "scskiller.ini").Any() || judged != null ? null : true;
            using var f = new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var now = new byte[Math.Min(f.Length, 65537)];
            f.ReadExactly(now);
            return judged != null && now.AsSpan().SequenceEqual(judged);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The tracked value for an ini SCSKiller is about to write as <paramref name="text"/> over <paramref name="judged"/>
    /// (its own, null when absent): either one is SCSKiller's, so a crash between the entry's save and the write leaves it
    /// recognised.</summary>
    static string IniTracked(string text, byte[]? judged) =>
        Hex(text) + (judged == null ? "" : "|" + Convert.ToHexString(SHA256.HashData(judged)));

    /// <summary>The folder's ini is about to be written as <paramref name="text"/> over <paramref name="judged"/>: its entry
    /// names both, saved first. Then, under the store's lock (every process's ini changes take turns on it), with the entry
    /// still that and the ini read again: unchanged, it is written; read and seen changed (or there now where none was), it's
    /// the user's; unreadable, gone or not to be written now (<paramref name="still"/>), the entry is put back and it's judged
    /// again later. An update (not <paramref name="install"/>) never cancels a queued removal and runs only beside our proxy
    /// or a registered Vulkan recorder (<paramref name="vulkan"/>), both checked under the lock. True when written.</summary>
    bool WriteIni(string dir, string text, byte[]? judged, string? chain, string installDir, Func<bool> still, Action<string>? step, bool install = false, bool vulkan = false)
    {
        dir = FullDir(dir);
        if (!Directory.Exists(dir)) return false;
        RefuseForeign(dir);
        var (ini, key, intent) = (Path.Combine(dir, "scskiller.ini"), dir, IniTracked(text, judged));
        IniOwner? was = null;
        var go = false;
        Store.UpdateInis(all =>
        {
            was = all.GetValueOrDefault(key);
            if (!install && (was is { Remove: true } || !vulkan && !OurProxyIn(dir))) return;
            // another write since it judged (judged absent: the entry tracked nothing then)
            if (was?.Tracked is { Length: > 0 } t && (judged == null || !t.Split('|').Contains(Convert.ToHexString(SHA256.HashData(judged))))) return;
            all[key] = (was ?? new IniOwner(dir)) with { Dir = dir, Tracked = intent, Users = false, Remove = false, Chain = chain, Install = installDir };
            go = true;
        });
        if (!go) return false;
        step?.Invoke("saved");
        var written = false;
        Store.UpdateInis(all =>
        {
            if (all.GetValueOrDefault(key) is not { Remove: false } now || now.Tracked != intent) return;   // a removal or another write since: theirs
            switch (still() ? IniNow(ini, judged) : null)
            {
                case true:
                    Place(ini, Text(text), replace: true);
                    all[key] = now with { Tracked = Hex(text) };
                    written = true;
                    break;
                case false:
                    all[key] = now with { Tracked = null, Users = true };
                    break;
                default:
                    if (was != null) all[key] = was; else all.Remove(key);
                    break;
            }
        });
        if (written) step?.Invoke("written");
        return written;
    }

    /// <summary><see cref="JudgeIni"/> for the game's exe folder, where an ini marked the user's is SCSKiller's again when its
    /// bytes are exactly <see cref="IniText(Game, GameRecord, string?, string)"/>'s for the record now, its limit line aside
    /// (the app's setting, rewritten anyway), and its stamp's sig= is SCSKiller's for this exe and root
    /// (<see cref="SignedStampExe"/>): no one else can sign, and a user's edit of the signed values breaks the sig. Tracked from
    /// then on, read again under the store's lock.</summary>
    (IniState State, byte[]? Bytes) JudgeOwnIni(Game g, GameRecord rec, string dir, string? chain)
    {
        var (state, judged) = JudgeIni(dir, Store, chain, g.InstallDir);
        if (state != IniState.NotOurs || judged == null || GetIni(Store, dir) is not { Users: true } || judged.Any(b => b >= 0x80)) return (state, judged);
        var text = System.Text.Encoding.ASCII.GetString(judged);
        var stamp = string.Concat(text.Split("\r\n").Where(l => IniKey(l) is { } k && StampKeys.Contains(k)).Select(l => l + "\r\n"));
        var cap = text.Split("\r\n").FirstOrDefault(l => l.StartsWith("max_db_bytes=", StringComparison.Ordinal))?["max_db_bytes=".Length..];
        try
        {
            if (text != IniText(g, rec, cap, stamp) || SignedStampExe(g, stamp) == null) return (state, judged);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (state, judged); }
        var ini = Path.Combine(dir, "scskiller.ini");
        var adopted = false;
        SetIni(Store, dir, e =>
        {
            if (e is not { Users: true } || IniNow(ini, judged) != true) return e;
            adopted = true;
            return e with { Tracked = Convert.ToHexString(SHA256.HashData(judged)), Users = false, Remove = false, Chain = chain, Install = g.InstallDir };
        });
        if (!adopted) return (state, judged);
        RecorderLog($"{g.Name}: the scskiller.ini in {dir} is SCSKiller's own, signed for this game: tracked again");
        return (IniState.Ours, judged);
    }

    /// <summary>Rewrites SCSKiller's own scskiller.ini when its limit line is out of date (the setting changed, an import
    /// grew the copy, a clear), or writes it when our proxy has none; anything else is left alone (<see cref="JudgeIni"/>).
    /// The proxy reads it when the game starts. <paramref name="stamp"/>: <see cref="Attest"/>'s; null keeps the ini's own.
    /// <paramref name="still"/>: asked right before the file is written; false writes nothing.</summary>
    void UpdateRecorderIni(Game g, GameRecord rec, string? stamp = null, Func<bool>? still = null)
    {
        if (Collections.RecordedBy(g.Id) != null) return;
        if (!GameFolderWrite(g) || Foreign(g)) return;   // the next refresh after it exits
        try
        {
            var dir = Path.GetDirectoryName(g.ExePath)!;
            // only beside our proxy or for the game's Vulkan recorder (missing there: an install cut off before it wrote it),
            // and never one waiting to be taken out: only an install sets that up again
            var vulkan = VulkanIn(g, rec);
            if (!OurProxyIn(dir) && !vulkan || GetIni(Store, dir) is { Remove: true }) return;
            var (state, judged) = JudgeOwnIni(g, rec, dir, rec.RecorderChained?.Name);
            var entry = GetIni(Store, dir);
            if (state is not (IniState.Absent or IniState.Ours) || entry is { Remove: true }) return;
            var text = IniText(g, rec, stamp ?? Resigned(g, rec) ?? IniStamp(g.ExePath) ?? "");
            if (judged != null && judged.AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(text)))
            {
                if (entry?.Tracked != Hex(text))   // recognised, not tracked yet
                    SetIni(Store, dir, e => e is { Remove: true } || !OurProxyIn(dir) && !vulkan ? e : (e ?? new IniOwner(dir)) with { Tracked = Hex(text), Users = false, Chain = rec.RecorderChained?.Name, Install = g.InstallDir });
                return;
            }
            WriteIni(dir, text, judged, rec.RecorderChained?.Name, g.InstallDir, () => still?.Invoke() != false && GameFolderWrite(g), AttestStep, vulkan: vulkan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecorderLog($"{g.Name}: couldn't update the recorder's scskiller.ini: {e.Message}"); }
    }

    /// <summary>The recording is imported, then the recorder's data files are deleted. False when the game started before
    /// they were (they're left), or its ini can't be taken out yet (the record keeps the folder).</summary>
    bool Uninstall(Game g, GameRecord rec)
    {
        var dir = Path.GetDirectoryName(g.ExePath)!;
        if (!RemoveRecorder(dir, rec, Store, g.Name, RecorderLog, g.InstallDir))
        {
            Store.SaveGame(g.Id, rec);
            return Directory.Exists(dir) || Gone(dir);   // gone: done; away: later
        }
        ImportRecording(g, rec, migrate: true);
        Store.SaveGame(g.Id, rec);
        try
        {
            if (RemoveRecorderData(dir, RecordingPath(g.Id), rec.RecordingInbox, () => GameRunning(g), rec.RecorderRollback || IniPending(Store, dir)))
            {
                if (!IniPending(Store, dir)) return true;
                RecorderLog($"{g.Name}: the recorder's scskiller.ini in {dir} isn't out yet: tried again later");   // the record keeps the folder
                return false;
            }
            RecorderLog($"{g.Name}: the game started: the recorder's data files left in {dir} for the next reconcile");
            return false;
        }
        catch (InvalidDataException e) { RecorderLog($"{g.Name}: left the recorder's data files in {dir}: {e.Message}"); }
        return false;
    }

    /// <summary>Takes the recorder out of <paramref name="dir"/>: SCSKiller's ini first (<see cref="StripIni"/>), any proxy of
    /// ours (by its export, whatever the manifest says), the chained mod put back, then the files Install wrote whose hash
    /// still matches, and 1.2's scskiller.armed files. One that can't be reached stays on the record for later. The record
    /// keeps the folder while its ini is still to be taken out (<see cref="IniPending"/>), and an ini store that can't be
    /// read or saved leaves the removal pending (<see cref="GameRecord.RecorderRollback"/>). False when nothing more is to be
    /// done there now (gone, or away).</summary>
    static bool RemoveRecorder(string dir, GameRecord rec, AppStore store, string name, Action<string> log, string? installDir = null, TimeSpan? wait = null)
    {
        RefuseForeign(dir);
        DropStamps(dir);
        if (!UnregisterVulkan(rec, store, name, log)) rec.RecorderRollback = true;   // the ini still goes: the layer refuses without its stamp
        if (!Directory.Exists(dir))
        {
            if (!Gone(dir))
            {
                log($"{name}: {dir} can't be reached: the recorder's files there are left for later");   // the record keeps it all
                return false;
            }
            // gone with the game: nothing to take out
            bool At(string? exe) => exe != null && FullDir(Path.GetDirectoryName(exe)!).Equals(FullDir(dir), StringComparison.OrdinalIgnoreCase);
            if (At(rec.RecorderExe))
            {
                (rec.RecorderExe, rec.RecorderFiles, rec.RecorderChained) = (null, [], null);
                if (rec.RecorderMoveFrom == null) rec.RecorderInstallDir = null;
            }
            if (At(rec.RecorderMoveFrom)) (rec.RecorderMoveFrom, rec.RecorderMoveTo) = (null, null);
            if (At(rec.RecorderMoveTo)) rec.RecorderMoveTo = null;
            return false;
        }
        var orphan = Orphaned(dir, rec);   // before our proxy goes below
        // another copy's chain, whose entry its own record holds: named by the folder, unhashed (it may be locked), kept until it's
        // back; the game's own AGS only while our wrapper stands in for it (or is parked: a removal cut off before AGS was back)
        rec.RecorderChained ??= new[] { ChainName, ChainName11 }.Concat(new[] { AgsName, AgsName + TempSuffix }.Select(n => Path.Combine(dir, n)).Where(IsOurProxy)
            // a parked one on the record had its AGS put back: the old name may since be the game's own file
            .Where(w => !w.EndsWith(TempSuffix) || !rec.RecorderFiles.ContainsKey(Path.GetFileName(w)) || AgsLinked(w) == AgsChainName).Select(AgsLinked).Take(1)).Select(n => Path.Combine(dir, n))
            .Where(p => File.Exists(p) && !rec.RecorderOrphans.Any(o => o.Equals(p, StringComparison.OrdinalIgnoreCase))).Select(p => new ChainedDll(Path.GetFileName(p), "")).FirstOrDefault();
        // the ini first: without its stamp whatever stays behind is a pass-through. A failure never keeps the proxy from going
        var iniLeft = false;
        try { StripIni(dir, store, rec.RecorderChained?.Name, installDir ?? rec.RecorderInstallDir, name, log, wait); }
        catch (Exception e) when (e is IOException and not ForeignFolderException or UnauthorizedAccessException)
        {
            (iniLeft, rec.RecorderRollback) = (true, true);   // the record keeps the folder: the next reconcile's removal takes it out
            log($"{name}: couldn't take {Path.Combine(dir, "scskiller.ini")} out ({e.Message}): tried again later");
        }
        foreach (var file in Directory.EnumerateFiles(dir, ArmedFile + "*"))
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"{name}: couldn't delete {file}: {e.Message}"); }
        var storage = Path.Combine(dir, "_storage_");   // Onimusha copies its folder's DLLs there at start and loads them from there
        var storageOurs = !Reparse(storage) && !MarkedElsewhere(storage);
        if (!storageOurs) log($"{name}: left {storage} as it is: it links to another folder");
        foreach (var n in ProxyNames)
        {
            var proxy = Path.Combine(dir, n);
            if (IsOurProxy(proxy))
            {
                // a chained mod's place: parked, inert under that name, until the mod is back (Orphaned)
                if (rec.RecorderChained is { } chained && RoleOf(chained) == n)
                {
                    if (File.Exists(proxy + TempSuffix) && !IsOurProxy(proxy + TempSuffix))   // the removal stays pending: the ini went, so the proxy is a pass-through
                        throw new IOException($"{proxy + TempSuffix} isn't SCSKiller's: {proxy} stays until it's gone");
                    File.Move(proxy, proxy + TempSuffix, overwrite: true);
                }
                else File.Delete(proxy);
            }
            else if (File.Exists(proxy) && rec.RecorderFiles.ContainsKey(n)) log($"{name}: left {proxy}: not SCSKiller's");
            rec.RecorderFiles.Remove(n);
            var copy = Path.Combine(storage, n);
            if (storageOurs && IsOurProxy(copy)) File.Delete(copy);   // a failure throws as the proxy's does: the removal is tried again
        }
        // the preloader by its marker as the proxy; the manifest's entry goes below. A copy cut short by Place's temp name
        // alone (<name>.<guid>.scskiller-new): it may be too short to hold the marker
        foreach (var n in PreloadNames.Concat(rec.RecorderFiles.Keys.Where(PreloadKey)).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            // another DLL's name: only the bytes on the record with the marker go, beside the exe in the hash loop below
            if (PreloadNames.Contains(n, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var p in new[] { Path.Combine(dir, n) }.Concat(storageOurs ? [Path.Combine(storage, n)] : []))
                    if (IsOurPreload(p)) File.Delete(p);
            }
            else if (storageOurs && Path.Combine(storage, n) is var stored && File.Exists(stored) && Sha256(stored) == rec.RecorderFiles[n] && IsOurPreload(stored))
                File.Delete(stored);
            foreach (var p in Directory.EnumerateFiles(dir, n + ".*" + TempSuffix).Where(p => PlaceTemp(Path.GetFileName(p), n)).ToList()) File.Delete(p);
        }
        if (rec.RecorderChained is { } c)
        {
            var dll = Path.Combine(dir, RoleOf(c));   // the mod's own name, which the chain kept
            var from = Path.Combine(dir, c.Name);
            if (orphan)
            {
                rec.RecorderOrphans.Add(from);
                rec.Recorder = RecorderOverride.Off;
                log($"{name}: {dll} was deleted while the recorder ran alongside a mod (its uninstall?): {from} left unused, recording turned off");
            }
            else if (!File.Exists(from)) log($"{name}: {from} (the mod's {Path.GetFileName(dll)} SCSKiller renamed) is gone");
            else if (File.Exists(dll)) log($"{name}: left {from}: another {Path.GetFileName(dll)} is in its place");
            else
            {
                if (c.Sha256 != "" && Sha256(from) != c.Sha256) log($"{name}: {from} changed since SCSKiller renamed it (the mod updated itself?): put back as it is");
                File.Move(from, dll);
            }
            rec.RecorderChained = null;
            try
            {
                // ours goes with the hash-matched files below: one that can't be deleted stays on the record
                if (IsOurProxy(dll + TempSuffix)) rec.RecorderFiles[Path.GetFileName(dll + TempSuffix)] = Sha256(dll + TempSuffix);
                else if (File.Exists(dll + TempSuffix)) log($"{name}: left {dll + TempSuffix}: not SCSKiller's");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"{name}: left {dll + TempSuffix}: {e.Message}"); }
        }
        foreach (var (file, hash) in rec.RecorderFiles.Where(f => f.Key != "scskiller.ini").ToList())   // an ini named here (1.2's) is the store's
        {
            var path = Path.Combine(dir, file);
            try
            {
                if (File.Exists(path) && Sha256(path) == hash) File.Delete(path);
                else if (File.Exists(path)) log($"{name}: left {path}: changed since SCSKiller installed it");
            }
            catch (Exception e)
            {
                rec.RecorderRollback = true;   // kept on the record: a start of SCSKiller or the next removal tries it again
                log($"{name}: couldn't delete {path} ({e.Message}): tried again later");
                continue;
            }
            rec.RecorderFiles.Remove(file);
        }
        // the record keeps naming the folder: a later removal takes its ini out, a file it couldn't delete, or the Vulkan
        // registration that stayed
        if (iniLeft || IniPending(store, dir) || rec.RecorderFiles.Count > 0 || rec.VulkanLayer != null) return true;
        if (rec.RecorderExe is { } exe && !FullDir(Path.GetDirectoryName(exe)!).Equals(FullDir(dir), StringComparison.OrdinalIgnoreCase)) return true;   // the record's folder is another
        (rec.RecorderExe, rec.RecorderInstallDir) = (null, rec.RecorderMoveFrom != null ? rec.RecorderInstallDir : null);   // a pending move's root: the hook's running check
        return true;
    }

    static readonly TimeSpan UninstallBudget = TimeSpan.FromSeconds(20);   // Velopack 1.2.158 kills its uninstall hook at 60 s

    /// <summary>SCSKiller's own uninstall: takes the recorder out of every game folder the data folder
    /// records one in (<see cref="GameRecord.RecorderExe"/>, no scan), then its data files once the data folder's
    /// recording holds the game's scskiller.db. A running game's folder is left, and a removal a file of it held back stays
    /// pending (<see cref="GameRecord.RecorderRollback"/>). What it did goes to recorders.log and comes back by game, each
    /// named by its data folder. No step waits for a lock past <paramref name="budget"/> (<see cref="UninstallBudget"/> when
    /// not given).</summary>
    public static RecorderRemovals RemoveAllRecorders(AppStore store, IReadOnlySet<string>? running = null, TimeSpan? budget = null) =>
        RemoveAllRecorders(store, running == null ? RunningProcessNames : () => running, budget);

    /// <summary><paramref name="running"/>: the running processes' names, asked again for every check. <paramref name="cause"/>
    /// starts its recorders.log lines. A cancel is as the budget running out.</summary>
    public static RecorderRemovals RemoveAllRecorders(AppStore store, Func<IReadOnlySet<string>> running, TimeSpan? budget, string cause = "uninstall",
        CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var limit = budget ?? UninstallBudget;
        TimeSpan Left() => ct.IsCancellationRequested ? TimeSpan.Zero : limit - clock.Elapsed is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero;
        bool Late() => clock.Elapsed > limit || ct.IsCancellationRequested;
        string Over() => ct.IsCancellationRequested ? "stopped" : "out of time";
        void Log(string line) => RecordersLog(store.DataDir, $"{cause}: " + line);
        var games = Path.Combine(store.DataDir, "games");
        var layers = new List<string>();
        string? layersLeft = null;
        var report = new List<(string Id, string Dir, List<string> Left)>();
        // first and whatever runs: no launch after this loads a Vulkan recorder of this data folder
        try
        {
            foreach (var m in VulkanLayer.UnregisterUnder(Path.Combine(store.DataDir, VulkanLayer.Folder)))
            {
                layers.Add(m);
                Log($"Vulkan recorder unregistered ({m})");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            layersLeft = e.Message;   // a registration no record names stays: only the report shows it
            Log($"the Vulkan recorders' registrations: {e.Message}");
        }
        try { MigrateInis(store, Left()); }
        catch (Exception e) { Log($"the records' scskiller.ini entries: {e.Message}"); }
        var removed = new List<(string Id, string Dir, Func<bool> Runs)>();
        var named = new HashSet<string>();   // the records whose folder stays named until their data files are out
        foreach (var id in (Directory.Exists(games) ? Directory.GetDirectories(games) : []).Select(d => AppStore.GameId(Path.GetFileName(d))).Order())
        {
            List<string> why = [];
            try
            {
                var rec = store.LoadGame(id);
                // a move's old folder, or an offline session's whose files went first, may still hold the data files
                if ((rec.RecorderExe ?? rec.RecorderMoveFrom ?? rec.OfflineSession?.Exe) is not { } exe) continue;
                var dir = Path.GetDirectoryName(exe)!;
                report.Add((id, dir, why));
                var installDir = rec.RecorderInstallDir ?? rec.OfflineSession?.InstallDir;
                var moveTo = rec.RecorderMoveTo;   // a move pending: the game may run from its new folder
                // the record's folders may be stale (the game's exe moved since): any exe of the install counts, its
                // names read once; null: not read whole (in time), as if it runs
                HashSet<string>? tree = installDir == null ? [] : InstallExeNames(installDir, Late);
                bool Runs() => running() is var names && (Running(exe, installDir, names) || moveTo != null && Running(moveTo, installDir, names)
                                                          || tree == null || tree.Overlaps(names));
                if (Late()) Leave(why, Over(), $"{Over()}: recorder left in {dir}");
                else if (Foreign(dir)) Leave(why, RemovalForeign, $"recorder left in {dir}: another SCSKiller copy's");
                else if (tree == null) Leave(why, "its install folder couldn't be read in time", $"{installDir} not read whole in time: recorder left in {dir}");
                else if (Runs()) Leave(why, "the game is running", $"{exe} is running: recorder left in {dir}");
                else
                {
                    var was = (rec.RecorderExe, rec.RecorderInstallDir);
                    rec.RecorderRollback = false;   // set again by a removal held back below
                    // every folder the record names (a move's either end, a folder kept while its drive was away); the data
                    // files from the record's own folder
                    foreach (var d in new[] { exe, rec.RecorderMoveFrom, moveTo }.OfType<string>().Select(e => Path.GetDirectoryName(e)!)
                                 .Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                        try
                        {
                            if (Foreign(d)) { Leave(why, RemovalForeign, $"recorder left in {d}: another SCSKiller copy's"); continue; }   // a move's other end taken over since
                            if (RemoveRecorder(d, rec, store, d, Log, installDir, Left()) && d.Equals(dir, StringComparison.OrdinalIgnoreCase)) removed.Add((id, d, Runs));
                            if (rec.VulkanLayer != null) Leave(why, RemovalVulkan, null);   // its unregistration failed: RemoveRecorder logged it
                            if (Directory.Exists(d))
                            {
                                if (rec.RecorderFiles.Count == 0 && !IniPending(store, d)) Log($"recorder removed from {d}");
                                else Leave(why, RemovalHeld, $"recorder removed from {d} but for files that can't be taken out yet");
                            }
                            else if (!Gone(d)) Leave(why, "its folder can't be reached", null);   // RemoveRecorder logged it
                        }
                        catch (Exception e)   // the next folder is tried all the same
                        {
                            rec.RecorderRollback = true;   // a start of SCSKiller takes it out before anything else
                            Leave(why, RemovalHeld, $"{d}: {e.Message}");
                        }
                    // its data files go below: until they're out, the record names the folder for a later removal
                    if (rec.RecorderExe == null && was.RecorderExe != null && removed.Exists(r => r.Id == id))
                    {
                        (rec.RecorderExe, rec.RecorderInstallDir) = was;
                        named.Add(id);
                    }
                    store.SaveGame(id, rec, Left());
                }
            }
            catch (Exception e)
            {
                if (!report.Exists(r => r.Id == id)) report.Add((id, "", why));
                Leave(why, "its record couldn't be read or saved", $"{id}: {e.Message}");
            }
        }
        foreach (var (id, dir, runs) in removed)
        {
            var why = report.Find(r => r.Id == id).Left;
            var done = false;
            try
            {
                if (Late()) Leave(why, Over(), $"{Over()}: the recorder's data files left in {dir}");
                else if (store.LoadGame(id) is var held && !RemoveRecorderData(dir, Path.Combine(store.GameDir(id), "recording.db"), held.RecordingInbox, runs,
                             held.RecorderRollback || IniPending(store, dir), Left()))
                    Leave(why, "the game started", $"the game started: the recorder's data files left in {dir}");
                else done = true;
            }
            catch (Exception e) { Leave(why, RemovalHeld, $"{dir}: {e.Message}"); }
            try
            {
                var rec = store.LoadGame(id);
                if (!done) rec.RecorderRollback = true;   // a start of SCSKiller takes them out before anything else
                else if (named.Contains(id)) (rec.RecorderExe, rec.RecorderInstallDir) = (null, rec.RecorderMoveFrom != null ? rec.RecorderInstallDir : null);   // as RemoveRecorder
                store.SaveGame(id, rec, Left());
            }
            catch (Exception e) { Leave(why, "its record couldn't be read or saved", $"{id}: {e.Message}"); }
        }
        return new([.. report.Select(r => new RecorderRemoval(r.Id, r.Dir, r.Left.Count == 0 ? null : string.Join(", ", r.Left.Distinct())))], layers, layersLeft);

        void Leave(List<string> why, string reason, string? line)
        {
            why.Add(reason);
            if (line != null) Log(line);
        }
    }

    // what RemoveAllRecorders says is left in a game's folder
    internal const string RemovalForeign = "another SCSKiller copy placed it", RemovalHeld = "some of its files can't be taken out yet",
        RemovalVulkan = "its Vulkan recorder's registration can't be taken out yet";

    // a store's uninstall is final; an update, a repair or a drive coming back doesn't look like one twice this far apart
    internal static readonly TimeSpan GoneFor = TimeSpan.FromHours(1);

    /// <summary>The recorder's files in the folder of a game its store uninstalled, which the store left behind because of
    /// them: removed as SCSKiller's own uninstall removes them (<see cref="RemoveRecorder"/>, <see cref="RemoveRecorderData"/>),
    /// once two looks at least <see cref="GoneFor"/> apart saw the game gone (<see cref="SteamUninstalled"/>), then the
    /// folders they leave empty up to the game's install folder. Steam only: the Xbox app takes the whole folder. A game
    /// <paramref name="found"/> (by its data folder) clears the first sighting, and so does another found game whose folder
    /// or exe is in the game's install folder, or whose folder holds it (another store's install there). The record stays,
    /// as for any game not found. Null <paramref name="found"/>: the watcher's look between scans, which stands on the last
    /// scan's list, a game of it counting as found only while its exe is there (any of it still claims its folder); none
    /// while a scan runs, before one listed every source's games, or after one whose discovery failed.</summary>
    void CleanUninstalled(IReadOnlyCollection<Game>? found = null)
    {
        var games = Path.Combine(Store.DataDir, "games");
        var watching = found == null;
        var scans = Interlocked.Read(ref _scans);
        bool Scanning() => watching && (Volatile.Read(ref _scanning) > 0 || Interlocked.Read(ref _scans) != scans || !ListWhole());   // a scan since: its list may claim the folder
        if (Scanning()) return;
        // the watcher takes the lock only for a record to act on: its pass's stamps skip a pass while it is held
        if (watching && !(Directory.Exists(games) ? Directory.GetDirectories(games) : []).Any(GoneCandidate)) return;
        var listed = found ?? [.. Games.Select(s => s.Game)];
        lock (_recorderLock)
        {
            if (Scanning()) return;
            var seen = listed.Where(g => !watching || Uses(g)).Select(g => Store.GameDir(g.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in Directory.Exists(games) ? Directory.GetDirectories(games) : [])
            {
                var id = AppStore.GameId(Path.GetFileName(folder));
                try
                {
                    var rec = Store.LoadGame(id);
                    if (seen.Contains(folder) || rec.RecorderExe is not { } exe || rec.RecorderInstallDir is not { } installDir
                        || rec.RecorderMoveFrom != null || rec.OfflineSession != null || !SteamUninstalled(id, exe, installDir)
                        || listed.Any(g => Claims(g, installDir) && !Store.GameDir(g.Id).Equals(folder, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (rec.RecorderGoneSince == null) continue;
                        rec.RecorderGoneSince = null;
                        Store.SaveGame(id, rec);
                        continue;
                    }
                    var dir = Path.GetDirectoryName(exe)!;
                    var age = DateTimeOffset.UtcNow - rec.RecorderGoneSince;
                    if (age == null || age < TimeSpan.Zero)   // a clock set back starts it again
                    {
                        rec.RecorderGoneSince = DateTimeOffset.UtcNow;
                        Store.SaveGame(id, rec);
                        RecorderLog($"{id}: uninstalled by Steam, the recorder's files left in {dir}: removed {GoneFor.TotalMinutes:0} minutes or more from now");
                        continue;
                    }
                    if (age < GoneFor) continue;
                    if (Foreign(dir))
                    {
                        RecorderLog($"{id}: recorder left in {dir}: another SCSKiller copy's");
                        continue;
                    }
                    if (Links(installDir, dir) is { } link)
                    {
                        RecorderLog($"{id}: recorder left in {dir}: {link} is a link");
                        continue;
                    }
                    // asked again after every wait (the recording's lock, its merge): a reinstall meanwhile stops it
                    bool Hold() => Scanning() || Running(exe, installDir, ProcessNames()) || !SteamUninstalled(id, exe, installDir) || Links(installDir, dir) != null;
                    if (Hold()) continue;
                    var before = Ours(dir);
                    try
                    {
                        rec.RecorderRollback = false;   // set again by a removal whose ini store failed
                        if (!RemoveRecorder(dir, rec, Store, id, RecorderLog, installDir))   // gone, or away
                        {
                            Store.SaveGame(id, rec);
                            continue;
                        }
                        (rec.RecorderExe, rec.RecorderInstallDir) = (exe, installDir);   // until the data files are gone too: the next scan retries
                        Store.SaveGame(id, rec);
                        if (!RemoveRecorderData(dir, RecordingPath(id), rec.RecordingInbox, Hold, rec.RecorderRollback || IniPending(Store, dir)))
                        {
                            RecorderLog($"{id}: the recorder's data files left in {dir}: the game started or came back");
                            continue;
                        }
                        if (rec.RecorderFiles.Count > 0 || rec.RecorderRollback || IniPending(Store, dir)) continue;   // something still to take out: the next scan retries
                        (rec.RecorderExe, rec.RecorderInstallDir, rec.RecorderGoneSince) = (null, null, null);
                        Store.SaveGame(id, rec);
                    }
                    finally
                    {
                        foreach (var f in before.Where(f => !File.Exists(Path.Combine(dir, f)))) RecorderLog($"{id}: deleted {Path.Combine(dir, f)}");
                        foreach (var f in Ours(dir)) RecorderLog($"{id}: left {Path.Combine(dir, f)}");
                    }
                    for (var d = Hold() ? null : dir; d != null && (GameFiles.Inside(installDir, d) || GameFiles.DirKey(d).Equals(GameFiles.DirKey(installDir), StringComparison.OrdinalIgnoreCase))
                                      && !Directory.EnumerateFileSystemEntries(d).Any(); d = Path.GetDirectoryName(d))
                    {
                        Directory.Delete(d);
                        RecorderLog($"{id}: deleted the empty folder {d}");
                    }
                    RecorderLog($"{id}: recorder removed from {dir}: the game was uninstalled");
                }
                catch (Exception e) { RecorderLog($"{id}: the recorder's files in an uninstalled game's folder: {e.Message}"); }
            }
        }
    }

    /// <summary>The record in <paramref name="folder"/> has a first sighting to clear or keep, or looks uninstalled.</summary>
    bool GoneCandidate(string folder)
    {
        var id = AppStore.GameId(Path.GetFileName(folder));
        try
        {
            var rec = Store.LoadGame(id);
            return rec.RecorderGoneSince != null || rec.RecorderExe is { } exe && rec.RecorderInstallDir is { } dir && SteamUninstalled(id, exe, dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return true; }   // the loop below reports it
    }

    /// <summary>The list shown is a scan's whose discovery read every source.</summary>
    bool ListWhole() { lock (_lock) return _listed > _discoveryFailed; }

    /// <summary>A listed game still there: its exe is, or can't be told not to be.</summary>
    static bool Uses(Game g)
    {
        try { return g.ExePath.Length == 0 || Present(g.ExePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return true; }
    }

    static bool Claims(Game g, string installDir) =>
        g.InstallDir.Length > 0 && (GameFiles.DirKey(g.InstallDir).Equals(GameFiles.DirKey(installDir), StringComparison.OrdinalIgnoreCase)
                                    || GameFiles.Inside(installDir, g.InstallDir) || GameFiles.Inside(g.InstallDir, installDir))
        || g.ExePath.Length > 0 && GameFiles.Inside(installDir, g.ExePath);

    /// <summary>The recorder's file names (<see cref="RecorderOwnFiles"/>, and its preloader's) in <paramref name="dir"/>.</summary>
    static List<string> Ours(string dir) => [.. RecorderOwnFiles.Where(f => File.Exists(Path.Combine(dir, f))), .. PreloadNames.Where(n =>
    {
        try { return IsOurPreload(Path.Combine(dir, n)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    })];

    /// <summary>The first junction or symlink on <paramref name="dir"/>'s path, from the drive's root down, or among the
    /// recorder's files there (a library linked to another drive would take the deletes there); null when there is none.</summary>
    static string? Links(string installDir, string dir)
    {
        for (var d = GameFiles.DirKey(dir); Path.GetDirectoryName(d) != null; d = Path.GetDirectoryName(d)!)
            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) return d;
        return Ours(dir).Select(f => Path.Combine(dir, f)).FirstOrDefault(f => (File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0);
    }

    /// <summary>A symlink or a junction (any reparse point) is at <paramref name="path"/>: what it reaches is another folder's.</summary>
    static bool Reparse(string path) => Present(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <summary>The file or folder is there; false only when it is known not to be (Exists says false on an access error too).</summary>
    static bool Present(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    /// <summary>Steam says the game is uninstalled: its install folder is its own (steamapps\common\&lt;folder&gt;, named as
    /// the file system tells names apart) on a fixed, ready drive, holding <paramref name="exe"/>'s folder, which is still
    /// there and the exe isn't; the library's steamapps folder reads, and no app manifest there names the app or the folder
    /// (one being updated, repaired or moved keeps its manifest); nothing of the app is downloading or staged. A
    /// collection's game in a folder of its own is the app's folder's. Any doubt, an error included: false.</summary>
    internal static bool SteamUninstalled(string id, string exe, string installDir) => SteamUninstalled(id, exe, installDir, OnFixedDrive);

    /// <param name="onFixedDrive">the game's folder, the library's steamapps folder and the exe's folder are on a fixed,
    /// ready drive (<see cref="OnFixedDrive"/>)</param>
    internal static bool SteamUninstalled(string id, string exe, string installDir, Func<string, string, string, bool> onFixedDrive)
    {
        try
        {
            var game = id.Replace('_', ':');   // a record's folder name has '_' for ':'
            if (SteamSource.AppId(game) is not { } app) return false;
            var root = GameFiles.DirKey(Collections.AppDir(new Game(game, "", Core.Store.Steam, installDir, exe)));
            var common = Path.GetDirectoryName(root);
            var apps = Path.GetDirectoryName(common);
            if (common == null || apps == null || !Path.GetFileName(common).Equals("common", GameFiles.Names)
                || !Path.GetFileName(apps).Equals("steamapps", GameFiles.Names) || !GameFiles.Inside(root, exe, GameFiles.Names)) return false;
            if (!onFixedDrive(root, apps, Path.GetDirectoryName(Path.GetFullPath(exe))!)) return false;
            if (Present(exe) || !Directory.Exists(Path.GetDirectoryName(exe)) || !Directory.Exists(apps)) return false;
            if (Present(Path.Combine(apps, $"appmanifest_{app}.acf")) || SteamSource.AppAt(apps, root).Id != null) return false;
            var downloading = Path.Combine(apps, "downloading");
            return !Present(Path.Combine(downloading, $"{app}")) && !Present(Path.Combine(apps, "temp", $"{app}"))
                   && !(Present(downloading) && Directory.EnumerateFiles(downloading, $"state_{app}_*").Any());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { return false; }
    }

    /// <summary>The Steam library's folders are on a fixed, ready drive: on Windows <paramref name="root"/>'s drive letter
    /// names it; on Linux one mount holds all three, a local file system on no removable media.</summary>
    private static partial bool OnFixedDrive(string root, string apps, string exeDir);

    /// <summary>The files the proxy writes, and its keys file. The last session's report moves next to <paramref name="recording"/>
    /// first (<see cref="SessionDir"/>). scskiller.db goes once <paramref name="recording"/> holds it:
    /// merged into it first unless it was imported as it is (<paramref name="imported"/>: its size and write time then).
    /// False, nothing deleted, when the game <paramref name="runs"/> once the merge is done. The copy's marker goes last,
    /// unless <paramref name="keepMarker"/> (a removal still pending there: without it the folder looks like another copy's).</summary>
    static bool RemoveRecorderData(string dir, string recording, string? imported, Func<bool> runs, bool keepMarker = false, TimeSpan? wait = null)
    {
        if (!Directory.Exists(dir)) return true;
        RefuseForeign(dir);
        var src = new FileInfo(Path.Combine(dir, "scskiller.db"));
        if (src is { Exists: true, Length: > 0 } && $"{src.Length}:{src.LastWriteTimeUtc.Ticks}" != imported)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(recording)!);
            using (Recordings.Lock(recording, wait)) Recordings.Merge(recording, src.FullName, null);
        }
        if (runs()) return false;   // it may have started while the merge waited for the lock
        var keep = Path.Combine(Path.GetDirectoryName(recording)!, "offline-session");   // OfflineSessionDir, where SessionDir reads the game page's report
        if (SessionFiles.Any(f => File.Exists(Path.Combine(dir, f))))
        {
            Directory.CreateDirectory(keep);
            MoveSessionFiles(dir, keep);
        }
        Exception? first = null;
        foreach (var f in RecorderDataFiles.Concat(Recordings.AppFiles))   // a locked file doesn't keep the others
            try { File.Delete(Path.Combine(dir, f)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { first ??= e; }
        if (first != null) throw first;
        if (!keepMarker) File.Delete(Path.Combine(dir, LedgerStamp));   // last: what is left stays its copy's
        return true;
    }

    /// <summary>The running check of <see cref="GameRunning"/> without the watcher: a process named like the exe
    /// (<paramref name="names"/>, without extensions), or like an exe of the install root or the exe's folder. Without a
    /// recorded install root (a record from an older build), the exe's folder and the three above it: Unreal's
    /// Binaries\Win64 sits three below the root. <paramref name="elsewhere"/>: an exe's name (the game's own too) that only another
    /// program runs under (<see cref="OnlyElsewhere"/>); without it (the uninstall hook) any process with the name counts.</summary>
    static bool Running(string exe, string? installDir, IReadOnlySet<string> names, Func<string, string?[], bool>? elsewhere = null)
    {
        var dir = Path.GetDirectoryName(exe)!;
        var roots = installDir != null ? [installDir, dir]
            : new[] { dir, Path.GetDirectoryName(dir), Path.GetDirectoryName(Path.GetDirectoryName(dir)),
                      Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(dir))) };
        var own = Path.GetFileNameWithoutExtension(exe);
        return names.Contains(own) && elsewhere?.Invoke(own, roots) != true || ExeNamesIn(roots) is not { } exes
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
            // a link to a folder is walked on Windows alone: a Wine prefix's dosdevices links name the whole system
            foreach (var f in new System.IO.Enumeration.FileSystemEnumerable<FileSystemInfo>(installDir, (ref System.IO.Enumeration.FileSystemEntry e) => e.ToFileSystemInfo(), InstallTree)
                     { ShouldRecursePredicate = (ref System.IO.Enumeration.FileSystemEntry e) => OperatingSystem.IsWindows() || (e.Attributes & FileAttributes.ReparsePoint) == 0 })
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

    /// <summary><see cref="EnqueueWhenIdle"/> without the wait for idle: it starts at once, and waits and pauses while any
    /// listed game runs, whatever PauseWhileGaming says. A queued "when idle" item stops waiting for idle.</summary>
    public void EnqueueRightAway(string gameId) => Add(gameId, whenIdle: true, rightAway: true);

    /// <summary>A waiting "when idle" item's <see cref="QueueItem.Note"/>: it doesn't run with the queue.</summary>
    public const string WhenIdleNote = "starts when the PC is idle";

    /// <summary>A waiting right-away item's <see cref="QueueItem.Note"/>: it doesn't run with the queue either.</summary>
    public const string RightAwayNote = "starts when no game is running";

    /// <summary>A waiting item that runs by itself, not with the queue (<see cref="EnqueueWhenIdle"/>, <see cref="EnqueueRightAway"/>).</summary>
    public static bool WaitsOnItsOwn(QueueItem q) => q is { Stage: QueueStage.Waiting, Note: WhenIdleNote or RightAwayNote };

    static bool Finished(QueueStage s) => s is QueueStage.Done or QueueStage.Failed or QueueStage.Stopped;

    int WaitingStart() => _queue.Count > 0 && _queue[0].GameId == _current ? 1 : 0;   // under _lock

    /// <summary><paramref name="pair"/>: the before and after recording's own compile; any other skips a game whose before
    /// run is still to come (it would warm the cache that run must find cold).</summary>
    void Add(string gameId, bool whenIdle, bool planCheck = false, bool prompt = false, bool pair = false, bool rightAway = false)
    {
        if (!planCheck && Games.FirstOrDefault(s => s.Game.Id == gameId) is { } listed
            && (GameVerdicts.Current.Unsupported(listed.Game, listed.Engine) != null || listed.StatusReason is CantReachReason or ThirtyTwoBitReason)) return;
        if (!pair && PairHoldsCache(gameId, compiling: true) is { } why)
        {
            Log?.Report($"{gameId}: not queued: {why}");
            return;
        }
        QueueItem item;
        lock (_lock)
        {
            if (Unlisted(gameId)) return;   // a scan that read it before its removal or hiding asks for a plan check
            var i = _queue.FindIndex(q => q.GameId == gameId);
            bool queued = i >= 0 && !Finished(_queue[i].Stage);
            bool promote = !whenIdle && _whenIdle.Remove(gameId);
            if (!planCheck) promote |= _planOnly.Remove(gameId);   // a compile, not a plan check: listed with the queue
            if (rightAway && queued && _whenIdle.Contains(gameId)) promote |= !_rightAway.Contains(gameId);
            if (queued && !promote) return;
            if (planCheck && !_queue.Any(q => q.PlanCheck && !Finished(q.Stage))) _checkBatch.Clear();
            if (planCheck) _checkBatch.Add(gameId);
            if (planCheck) _planOnly.Add(gameId);
            if (planCheck && prompt) _prompt.Add(gameId);
            else _prompt.Remove(gameId);
            if (whenIdle) _whenIdle.Add(gameId);
            if (whenIdle && rightAway) _rightAway.Add(gameId);
            else _rightAway.Remove(gameId);
            if (queued) _queue[i] = item = _queue[i] with { Error = null, Note = !whenIdle ? null : _queue[i].Stage == QueueStage.Waiting ? rightAway ? RightAwayNote : WhenIdleNote : _queue[i].Note, PlanCheck = false };
            else
            {
                if (i >= 0) _queue.RemoveAt(i);   // finished before: queued again
                item = new QueueItem(gameId, QueueStage.Waiting, null, null, whenIdle && !prompt ? rightAway ? RightAwayNote : WhenIdleNote : null, planCheck);
                var end = _queue.FindIndex(q => Finished(q.Stage));
                _queue.Insert(end < 0 ? _queue.Count : end, item);
            }
            if (whenIdle || _running) _worker ??= Task.Run(Work);
        }
        QueueChanged?.Invoke(item);
        if (!whenIdle) YieldPlanCheck();
    }

    /// <summary>A running plan check of another game gives way to a compile the user started: it is cancelled and queued
    /// again (once per check), and the compile's item says what it waits for.</summary>
    void YieldPlanCheck()
    {
        string? check;
        QueueItem? waiting;
        lock (_lock)
        {
            check = _running && _current is { } c && _planOnly.Contains(c) ? c : null;
            waiting = check == null ? null : _queue.FirstOrDefault(q => q is { Stage: QueueStage.Waiting, PlanCheck: false } && q.GameId != check && !_whenIdle.Contains(q.GameId));
        }
        if (check == null || waiting == null) return;
        var name = Games.FirstOrDefault(s => s.Game.Id == check)?.Game.Name ?? check;
        Set(waiting with { Note = $"Waiting for {name} to finish reading its shaders" });
        CancellationTokenSource? cts = null;
        lock (_lock)
            if (_current == check && _yielding == null) (_yielding, cts) = (check, _itemCts);
        cts?.Cancel();   // outside _lock: the token's callbacks may run here
    }

    /// <summary>A waiting item's <see cref="QueueItem.Note"/> from the moment the queue takes it until its first stage.</summary>
    public const string StartingNote = "Starting the compile";

    readonly HashSet<(string, DateTimeOffset)> _toldEnded = [];   // a finished item's QueueChanged comes more than once

    /// <summary>The notification for a compile the user started that just finished or failed, once per compile; null for
    /// any other queue change. "When idle" compiles and plan checks end silently.</summary>
    public (string Title, string Text)? EndedNotice(QueueItem q)
    {
        if (q is not { Stage: QueueStage.Done or QueueStage.Failed, PlanCheck: false, StartedAt: { } at }) return null;
        lock (_lock)
            if (_whenIdle.Contains(q.GameId) || !_toldEnded.Add((q.GameId, at))) return null;
        var name = Games.FirstOrDefault(s => s.Game.Id == q.GameId)?.Game.Name ?? q.GameId;
        return q.Stage == QueueStage.Done ? ($"{name} is compiled", $"Started {at.LocalDateTime:t}, finished {DateTime.Now:t}")
            : ($"{name}: the compile failed", q.Error ?? $"Started {at.LocalDateTime:t}");
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
        YieldPlanCheck();
    }

    public void CompileMax(string gameId)
    {
        lock (_lock) _maxNext.Add(gameId);
        Compile(gameId);
    }

    public void Compile(string gameId)
    {
        if (PairHoldsCache(gameId, compiling: true) is { } why) throw new InvalidOperationException(why);
        Compile(gameId, pair: false);
    }

    void Compile(string gameId, bool pair)
    {
        Add(gameId, whenIdle: false, pair: pair);
        QueueItem? item;
        lock (_lock)
        {
            if (_running) return;
            RunWaiting();
            item = _queue.FirstOrDefault(q => q.GameId == gameId);
        }
        if (item != null) QueueChanged?.Invoke(item);   // QueueRunning changed
        YieldPlanCheck();
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
            _maxNext.Remove(gameId);
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
        List<QueueItem> waiting;
        lock (_lock)
        {
            _running = false;
            waiting = _queue.Where(q => q.Stage == QueueStage.Waiting && q.Note != null && q.GameId != _current && !_whenIdle.Contains(q.GameId)).ToList();
        }
        foreach (var q in waiting) Set(q with { Note = null });   // YieldPlanCheck's note
        StopCurrent();
    }

    void StopCurrent()
    {
        CancellationTokenSource? cts;
        IWarmRun? run;
        lock (_lock) cts = _itemCts;
        _foldHalt = true;
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
            item = item with { PlanCheck = _queue[i].PlanCheck,   // RunItem's items don't carry it; only Add changes it
                StartedAt = item.Stage == QueueStage.Waiting ? null : _queue[i].StartedAt ?? (App.Format.Running(item) ? DateTimeOffset.Now : null) };
            _queue.RemoveAt(i);
            _queue.Insert(Finished(item.Stage) ? _queue.Count : i, item);   // finished items go to the end
        }
        QueueChanged?.Invoke(item);
        BeforeAfterQueued(item);
    }

    void SetCurrent(QueueStage stage, string? note = null)
    {
        QueueItem? item;
        lock (_lock) item = _queue.FirstOrDefault(q => q.GameId == _current);
        // the read's package count (ReadProgress), a plan step's (PlanSteps) and the fold's bytes aren't a paused warm's pipeline count
        if (item != null) Set(item with { Stage = stage, Note = note, Progress = item.Stage is QueueStage.Indexing or QueueStage.Planning or QueueStage.Materializing or QueueStage.Merging && stage != item.Stage ? null : item.Progress });
    }

    bool UserIdle => IdleTime() >= IdleAfter;

    /// <summary>A prompt plan check (<see cref="_prompt"/>) waits only while a game is played, as the recorder's index does,
    /// a right-away item (<see cref="_rightAway"/>) only while one is.</summary>
    bool WaitsForIdle(string id)
    {
        bool prompt, rightAway;
        lock (_lock)
        {
            if (!_whenIdle.Contains(id)) return false;
            (prompt, rightAway) = (_prompt.Contains(id), _rightAway.Contains(id));
        }
        if (rightAway)
        {
            // nobody chose this compile now: it never runs beside a game, nor before a poll of the listed games saw which run.
            // The poll's stamp first: a poll writes _playing before it, so _playing is at least that poll's.
            var polled = Volatile.Read(ref _polledListed);
            var playing = _playing.Count > 0;
            var listed = Volatile.Read(ref _listed);
            return playing || listed == 0 || polled != listed;
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
            SetCurrent(QueueStage.Waiting, StartingNote);
            foreach (var (other, indexing) in _recorderIndexes) ParkIndex(other, indexing);   // its compile indexes it, the others wait for the queue
            while (Volatile.Read(ref _readingIndexes) > 0 && !ct.IsCancellationRequested) await Task.Delay(50);   // a read in progress stops at its next check
            try
            {
                // no update applies under a running item, and no item starts while one is handed over or a cache delete runs
                IDisposable? hold;
                while ((hold = HoldForCompile()) == null && !ct.IsCancellationRequested) await Task.Delay(Poll);
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
            if (_exesWaiting.TryRemove(id, out _) && Games.FirstOrDefault(s => s.Game.Id == id) is { } listed) ListedExeChanged(listed.Game);
            QueueItem? ended;
            bool yielded, prompt, check;
            lock (_lock)
            {
                ended = _queue.FirstOrDefault(q => q.GameId == id && Finished(q.Stage));
                if (yielded = _yielding == id) _yielding = null;
                (prompt, check) = (_prompt.Contains(id), _planOnly.Contains(id));
            }
            // after the user's compiles, or as the user's own compile when Compile was clicked on it meanwhile
            Task? after;
            lock (_lock) (after, _abandoned) = (_abandoned, null);
            if (yielded && ended?.Stage == QueueStage.Stopped)
                if (after == null) Add(id, whenIdle: check, planCheck: check, prompt);
                else _ = after.ContinueWith(_ => Add(id, whenIdle: check, planCheck: check, prompt), TaskScheduler.Default);
            else if (ended != null) QueueChanged?.Invoke(ended);   // Compiling changed: the item's own Done came before it did
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
        catch (IOException e) when (IoErrors.HeldElsewhere(e)) { return null; }
    }

    /// <summary>The index, plan and materialize buffers are garbage once a warm starts and once the queue is done: give the
    /// memory back to Windows instead of keeping the peak committed while the warm runs or the app sits idle.</summary>
    static void ReleaseMemory() => GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

    /// <summary>Between stages: waits while the queue is paused, or while the user is at the PC for a "when idle" item.
    /// ponytail: index/plan/materialize run to the end of the stage once started (only the warm suspends mid-way); pass
    /// a pause token into the reader/planner if a stage ever gets long.</summary>
    async Task Gate(string id, CancellationToken ct)
    {
        string? why = null;
        try
        {
            while (!_go.IsSet || WaitsForIdle(id))
            {
                if (_go.IsSet) ShowWait(QueueStage.Paused, why = IdleWaitNote(id));   // as a running warm shows it
                await Task.Delay(Poll, ct);
            }
        }
        finally { if (why != null) EndWait(why); }
    }

    public const string FoldWaitNote = "waiting for an earlier compile to go into its game's shader cache";

    string IdleWaitNote(string id)
    {
        lock (_lock) return _prompt.Contains(id) ? "paused while a game runs" : "paused until the PC is idle";
    }

    /// <summary>The running item's line says why it waits; a paused queue shows its own Paused.</summary>
    void ShowWait(QueueStage stage, string why)
    {
        QueueItem? item;
        lock (_lock) item = _queue.FirstOrDefault(q => q.GameId == _current);
        if (_go.IsSet && item != null && (item.Stage, item.Note) != (stage, why)) SetUnlessPaused(stage, why);
    }

    void EndWait(string why)
    {
        QueueItem? item;
        lock (_lock) item = _queue.FirstOrDefault(q => q.GameId == _current);
        if (item?.Note == why) SetUnlessPaused(_stage);
    }

    /// <summary>A PauseQueue between the caller's check and this keeps its Paused.</summary>
    void SetUnlessPaused(QueueStage stage, string? note = null)
    {
        SetCurrent(stage, note);
        if (!_go.IsSet) SetCurrent(QueueStage.Paused);
    }

    /// <summary>index -> plan if missing/stale -> materialize into games\&lt;id&gt;\work -> warm -> record -> delete work.</summary>
    async Task RunItem(string id, CancellationToken ct)
    {
        WarmProgress? progress = null, merging = null;   // merging: the compile's D3DSCache fold, in bytes read
        var writing = false;   // a fold step outran its estimate: no share to show until it ends
        // the last warm process's exit while the driver writes its cache, and the bookkeeping after it: elapsed time, no share
        TimedPhase? saving = null, finishing = null;
        IWarmRun? savingRun = null;
        Task? armed = null;   // the LastDone the Saving step waits on: a segment's next process brings another
        var phaseGate = new Lock();
        string? phaseNote = null;
        var bookkept = TimeSpan.Zero;
        void Finishing(bool on, TimeSpan first = default)
        {
            if (on) finishing ??= new TimedPhase(t => { phaseNote = Elapsed(bookkept + t); Stage(QueueStage.Finishing); }, first);
            else if (finishing != null)
            {
                finishing.Dispose();
                bookkept += finishing.Elapsed;
                finishing = null;
            }
        }
        // the run's LastDone starts Saving; null: the current run's again, a segment's earlier one dropped
        void ArmSaving(IWarmRun? run)
        {
            TimedPhase? dropped;
            Task? done;
            lock (phaseGate)
            {
                (dropped, saving) = (saving, null);
                run = savingRun = run ?? savingRun;
                armed = done = run?.LastDone;
            }
            dropped?.Dispose();
            if (run == null || done == null) return;
            _ = done.ContinueWith(_ =>
            {
                long Bytes()
                {
                    try { return Vendor.GetCacheUsage().BytesOnDisk; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
                }
                // this step's own: the first walk is the baseline, one walk at a time, never waited for
                long before = -1, written = 0, sampling = 0;
                void Sample()
                {
                    if (Interlocked.Exchange(ref sampling, 1) != 0) return;
                    _ = Task.Run(() =>
                    {
                        var now = Bytes();
                        if (Interlocked.CompareExchange(ref before, now, -1) != -1) Interlocked.Exchange(ref written, now - Interlocked.Read(ref before));
                        Interlocked.Exchange(ref sampling, 0);
                    });
                }
                lock (phaseGate)
                    if (savingRun == run && saving == null && armed == done)
                    {
                        Sample();
                        saving = new TimedPhase(t =>
                        {
                            Sample();
                            var grown = Interlocked.Read(ref written);
                            phaseNote = Elapsed(t) + (grown > 0 ? $", {App.Format.Bytes(grown)} written" : "");
                            Stage(QueueStage.Saving);
                        }, TimeSpan.FromMilliseconds(500));   // after the done event's own report (every 250 ms): the warm shows at 100% first
                    }
            }, TaskScheduler.Default);
        }
        TimeSpan? EndSaving()
        {
            TimedPhase? ended;
            lock (phaseGate) (ended, saving, savingRun, armed) = (saving, null, null, null);
            ended?.Dispose();
            return ended?.Elapsed;
        }
        void Bookkept()
        {
            Finishing(false);
            var name = Games.FirstOrDefault(s => s.Game.Id == id)?.Game.Name ?? id;
            Log?.Report($"{name}: the compile's bookkeeping after its warm took {bookkept.TotalSeconds:0.0} s, the D3DSCache fold not counted");
        }
        long lastDone = -1, crashed = 0, unbuilt = 0, known = 0, crashedBefore = 0;   // unbuilt: of the result's Failed (Unbuilt11); crashedBefore: of crashed
        int? failedBy = null;   // TopFailKind of the warm's logs
        long keptEarlier = 0, refusedBefore = 0;   // what earlier compiles on this driver created (left out), what the last one refused
        Stopwatch? warmClock = null;
        var rtSkipped = false;   // the warm's plan left ray tracing out
        PlanStats? warmPlan = null;
        WarmResult? ended = null;
        TimeSpan took = default;
        void Ended(string verb)
        {
            if (warmClock == null) return;
            var name = Games.FirstOrDefault(s => s.Game.Id == id)?.Game.Name ?? id;
            var line = $"compile {name}: {verb} {(verb == "done" ? "in" : "after")} {Took(ended != null ? took : warmClock.Elapsed)}";
            if (ended is { Outcome: WarmOutcome.Completed } d && RetriedNote(d.Total, d.Failed - unbuilt, keptEarlier, refusedBefore) is { } retried)
                line += ", " + retried;
            else if (ended is { } r)   // before the end, Done doesn't cover every crash-skipped key
            {
                line += $", {Math.Max(0, r.Done - r.Failed - r.DriverSkipped - r.Invalid - r.Unsupported - (r.Outcome == WarmOutcome.Completed ? crashed : 0)):N0} of {r.Total:N0} created, {r.Failed - unbuilt:N0} failed";
                if (r.Skipped > 0) line += $", {r.Skipped:N0} skipped ({SkippedWhy(warmPlan)})";
                if (crashed - crashedBefore > 0) line += $", {crashed - crashedBefore:N0} skipped (crash the GPU driver)";
                if (r.DriverSkipped - r.Known is > 0 and var now) line += $", {now:N0} skipped for this compile (the GPU driver crashed or hung on {(now == 1 ? "it or on what it builds on, or it wasn't" : "them or on what they build on, or they weren't")} tried past a spot where it kept failing)";
                if (unbuilt > 0) line += $", {unbuilt:N0} skipped (SCSKiller can't build them yet)";
                if (r.Invalid > 0) line += $", {r.Invalid:N0} skipped ({InvalidWhy(r.Invalid)})";
                if (r.Unsupported > 0) line += $", {r.Unsupported:N0} skipped ({UnsupportedWhy(r.Unsupported)})";
                if (r.Known + crashedBefore > 0) line += ", " + KnownNote(r.Known + crashedBefore);
            }
            if (ended is { Cached: > 0 } c) line += $", {Math.Min(c.Cached, c.Done):N0} needed no compile (in the game's Advanced Shader Delivery or already compiled)";
            if (rtSkipped) line += ". " + RtSkippedNote;
            warmClock = null;
            Log?.Report(line);
        }
        var advanced = Stopwatch.StartNew();   // since the done count last moved (or the warm was paused)
        Game? compiling = null;   // the installed build, once read
        void Stage(QueueStage s, string? error = null)
        {
            if (s is not QueueStage.Paused) _stage = s;
            bool paused = s is not (QueueStage.Done or QueueStage.Failed or QueueStage.Stopped or QueueStage.Merging or QueueStage.Finishing) && (!_go.IsSet || _pauseWhy != null);
            if (paused || (progress?.Done ?? -1) != lastDone)
            {
                lastDone = progress?.Done ?? -1;
                advanced.Restart();
            }
            // a finished warm's note: what failed and why, and what was skipped (a shader not in this install);
            // a warm that stopped moving: for how long (the estimate would be a guess)
            var note = paused ? _pauseWhy
                : s == QueueStage.Done && ended is { Outcome: WarmOutcome.Completed } e && RetriedNote(e.Total, e.Failed - unbuilt, keptEarlier, refusedBefore) is { } retried ? retried
                : s is QueueStage.Done or QueueStage.Stopped && progress is { } p ? WarmCounts(p.Failed, p.Skipped, crashed, p.DriverSkipped, unbuilt, warmPlan, p.Invalid, p.Unsupported, known, failedBy, crashedBefore)
                : s == QueueStage.Warming && advanced.Elapsed > StallAfter ? StalledNote(advanced.Elapsed)
                : s == QueueStage.Warming ? progress?.Note   // e.g. "retrying ray tracing with fewer threads (8)"
                : s is QueueStage.Saving or QueueStage.Finishing ? phaseNote
                : s == QueueStage.Merging && writing ? App.Format.WritingStage : null;
            Set(new QueueItem(id, paused ? QueueStage.Paused : s, s == QueueStage.Merging ? writing ? null : merging : s is QueueStage.Saving or QueueStage.Finishing && !paused ? null : progress, error, note));
            if (s == QueueStage.Failed) NoteFailed(id, compiling);
        }
        var state = Games.FirstOrDefault(s => s.Game.Id == id);
        var installed = compiling = state == null ? null : Current(state.Game);
        // the state may be of an older build than the one installed now: its engine and planner check, cached unless the exe or build changed
        var now = installed == null ? null : Evaluated(installed, Store.LoadGame(id), false, out _);
        // a game needing a recording only for its ray tracing still compiles the rest (a partial compile); the planner's own verdict first
        string? NotReady() => state == null || installed == null ? "unknown game (scan first)"
            : SandboxCompileOff(installed) is { } sandboxed ? sandboxed   // before the plan's: no recording can help
            : now?.Engine == null || now.Check.Readiness != Readiness.Ready ? $"not ready: {now?.Check.Reason}"
            : BlockingMod(installed) is { } mod ? $"not ready: {ShaderModReason(mod)}"
            : IntelLocalDriver(installed) ? $"not ready: {SkipIntelDriver}"
            : LauncherClearsNext(installed) ? $"not ready: {LauncherClearsReason}"
            : AppCacheFiles.MissingTarget(AppCache) is { } link ? $"not ready: {LinkTargetMissing(link)}"
            : state.Status is GameStatus.Unsupported || state.Status == GameStatus.NeedsRecording && !NeedsRtRecording(state) ? $"not ready: {state.StatusReason}"
            : null;
        if (NotReady() != null && now?.Check.Readiness == Readiness.Ready)   // the state may be older than the check (another process imported a recording)
        {
            Refresh(installed!, ct);
            state = Games.FirstOrDefault(s => s.Game.Id == id);
        }
        if ((NotReady() ?? (_warmer as Warmer)?.Refusal(installed!, VulkanPlan.IsVulkan(now!.Engine!))) is { } notReady)
        {
            Stage(QueueStage.Failed, notReady);
            if (installed != null) Refresh(installed, ct);   // NoteFailed's record: the compile's own refresh doesn't run
            return;
        }
        bool planCheck;
        lock (_lock) planCheck = _planOnly.Contains(id);
        if (!planCheck && CacheOffRefusal() is { } off)
        {
            Set(new QueueItem(id, QueueStage.Failed, null, off, null));   // a driver setting, not this game's failure: no NoteFailed
            return;
        }
        var engine = now!.Engine!;
        var game = installed!;
        bool whenIdle;
        lock (_lock) whenIdle = _whenIdle.Contains(id);
        // an API change no evaluation took yet: the other API's compile is kept before this one plans over it
        try { SwitchApi(game, engine); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Stage(QueueStage.Failed, $"keeping the compile for the other graphics API failed: {e.Message}");
            return;
        }
        var rec = Store.LoadGame(id);
        var work = Path.Combine(Store.GameDir(id), "work");
        string? indexed = null;   // this build's content hash, once this compile has indexed it
        var replans = 0;
        var started = DateTime.UtcNow;
        bool refreshed = false, completed = false, foldRan = false, stoppedHere = false;
        // a staged exe's own runtime cache folder, never read again: its dbs go to the game's folder, or wait for the game's
        // first run to make one (MergeD3DSCache)
        void Fold()
        {
            foldRan = true;
            _foldHalt = false;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // the compiler this driver writes, from a staged db of this warm: what a delivered db is compared with
                    if (DriverId is { } driver && D3DSCache.Made(Path.Combine(LocalAppData, "D3DSCache"), WorkDirs(Store.GameDir(id)), started)
                            .Select(D3DSCache.CompilerOf).FirstOrDefault(c => c != null) is { } compiler)
                        KeepCompiler(driver, compiler);
                    // this compile's staged folders, and an exit the app didn't see
                    Stopwatch? copying = null;
                    QueueItem? ended;   // a stopped or failed compile's row: its fold shows its share, then the row comes back
                    lock (_lock) ended = completed ? null : _queue.FirstOrDefault(q => q.GameId == id);
                    try
                    {
                        // a pause rolls the merge back and starts it again on resume, a stop or a quit leaves it for the next start or game exit
                        while (MergeD3DSCache(game, own: true, (done, total) =>
                        {
                            var seconds = (copying ??= Stopwatch.StartNew()).Elapsed.TotalSeconds;
                            merging = new WarmProgress(done, total, 0, seconds >= 1 ? done / seconds : 0);
                            Stage(QueueStage.Merging);
                        }, over =>
                        {
                            writing = over;
                            Stage(QueueStage.Merging);
                        }, () => !_go.IsSet || _foldHalt) && !_foldHalt && !ct.IsCancellationRequested)
                        {
                            SetCurrent(QueueStage.Paused);
                            while (!_go.IsSet && !_foldHalt && !ct.IsCancellationRequested) _go.Wait(Poll);
                            if (!_go.IsSet) break;
                            (copying, writing) = (null, false);
                        }
                    }
                    finally { if (ended != null) Set(ended); }
                    if (!completed && AppCache is NvidiaAppCache)
                        AppStore.Locked(Path.Combine(Store.GameDir(id), "state.json"), () =>
                        {
                            // what a stopped compile kept counts once every staged db is in: their lengths and the time, for StoppedLeftOut
                            var r = Store.LoadGame(id);
                            if (r is not { StoppedCreated: { Folded: false } s, D3DSCacheDamaged: false } || D3DSCacheDbs(D3DSCacheNames(r)) is not { } dbs) return;
                            // a db still staged or waiting goes in at a later fold, which counts the set then (the next compile's start retries)
                            var all = r.D3DSCacheSince == null && !Holds(Path.Combine(Store.GameDir(id), D3DSCacheWaiting), "*", dirs: true).GetValueOrDefault(true);
                            if (!all && dbs.Count == 0) return;   // nothing went in yet: nothing to lose
                            // without a baseline (no db at the stop) only this stop's own fold vouches, as nothing came between; a
                            // partial fold's dbs become the baseline the count checks, a db it added included
                            r.StoppedCreated = dbs.Count > 0 && (s.Bytes == null ? stoppedHere : StillHeld(s, dbs))
                                ? s with { Bytes = Lengths(dbs), At = DateTimeOffset.UtcNow, Folded = all }
                                : null;
                            Store.SaveGame(id, r);
                            if (r.StoppedCreated == null) DropStopped(id, r);
                        });
                    var after = Store.LoadGame(id);   // a later save of rec keeps what the fold left
                    (rec.D3DSCacheSince, rec.D3DSCacheDamaged, rec.WarmedDriverId) = (after.D3DSCacheSince, after.D3DSCacheDamaged, after.WarmedDriverId);
                    (rec.D3DSCacheStamp, rec.D3DSCacheStampAt, rec.D3DSCacheStampFolder) = (after.D3DSCacheStamp, after.D3DSCacheStampAt, after.D3DSCacheStampFolder);
                    rec.StoppedCreated = after.StoppedCreated;
                    if (completed && AppCache is NvidiaAppCache)
                        AppStore.Locked(Path.Combine(Store.GameDir(id), "state.json"), () =>
                        {
                            var r = Store.LoadGame(id);
                            (r.WarmedD3DSCacheBytes, r.WarmedD3DSCacheAt) = (D3DSCacheDbs(D3DSCacheNames(r))?.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(g => g.Key, g => g.Max(d => d.Bytes), StringComparer.OrdinalIgnoreCase), DateTimeOffset.UtcNow);
                            // only when this fold put every staged db in: a deferred one would land after the reading
                            r.D3DSCacheFoldKeys = r is { D3DSCacheStamp.Source: not IdentityStamp.Refused, D3DSCacheStampFolder: { } f, D3DSCacheStampAt: { } at, D3DSCacheSince: null }
                                && at >= started && IsFolderName(f) ? D3DSCache.FoldKeysOf(Path.Combine(LocalAppData, "D3DSCache", f), at) : null;
                            Store.SaveGame(id, r);
                        });
                }
            }
            catch (Exception e) { Log?.Report($"{game.Name}: D3DSCache not cleaned: {e.Message}"); }
        }
        bool Low() { lock (_lock) return Background || _planOnly.Contains(id) || _whenIdle.Contains(id); }   // read at each stage: a Compile click makes it the user's
        bool Checking() { lock (_lock) return _planOnly.Contains(id); }
        // a plan check's read or plan: a Compile click stops waiting for it at once, a read that can't stop mid-file ends on its own
        async Task<T> Yielding<T>(Task<T> stage)
        {
            if (!Checking()) return await stage;
            try { return await stage.WaitAsync(ct); }
            catch (OperationCanceledException) when (!stage.IsCompleted)
            {
                lock (_lock) _abandoned = stage.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                throw;
            }
        }
        int Threads() => ThreadsOverride ?? (Low() ? Settings.BackgroundThreads : Settings.Threads);
        void Close()   // before Done shows: a notification check then reads the warm's state, never one evaluated before it ended
        {
            refreshed = true;
            try { Refresh(game, ct); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{game.Name}: refreshing failed: {e.Message}"); }
        }
        try
        {
            Planner.Steps.Value = PlanSteps(id);
            work = FreshWork(Store.GameDir(id), Log);
            // an import that failed (the game held its scskiller.db) is retried before this plans
            if (InboxPending(game, rec))
            {
                Stage(QueueStage.Indexing);
                Refresh(game, ct);
                rec = Store.LoadGame(id);
            }
        // new rules during the run index and plan again, so a plan's recorded hash is the rules it was built with
        indexAndPlan:
            var rules = RulesFile.Current.Hash(engine);
            Stage(QueueStage.Indexing);
            await Gate(id, ct);
            var indexStamp = IndexStamp(game, engine);   // before the index: a change while it runs shows at the next evaluation
            var index = await Yielding(Lowered(Threads(), Low(), () => Index(game, engine, rec, ct), Checking()));
            indexed = index.ContentHash;
            // the shared packs of its upscaler DLLs and a newer community recording for exactly this build: fetched first, within
            // a short wait (never holds a compile up), so a compile doesn't go stale on the next scan's downloads
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                wait.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    await SyncPacks([game], wait.Token);
                    await SyncCommunity(game, index.ContentHash, wait.Token, index.LegacyHashes);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Log?.Report($"{game.Name}: community database: no answer in time, compiling without it"); }
            }
            // from here a background sync's download waits for the compile's end, and the lock lets one mid-write land first
            using (Recordings.Lock(RecordingPath(id), ct: ct))
                lock (_heldDownloads) _heldDownloads.TryAdd(id, new(StringComparer.Ordinal));
            bool maxOnce;           // CompileMax: this compile plans and warms every pairing, whatever the game's Maximum setting
            lock (_lock) maxOnce = _maxNext.Contains(id);
            // prepared once, for the plan and for the warm: the planner reads the recorded shaders' bytes too
            var prepared = false;
            Recording? recording = null;
            WarmInputs.Recorded? recorded = null;   // the warm's recorded inputs, read before the recordings are prepared
            string? preparedCommunity = null, preparedOfficial = null;   // the downloads' objects in the recording prepared
            Recording? Prepared()
            {
                if (prepared) return recording;
                if (Directory.Exists(work) && Directory.EnumerateFileSystemEntries(work).Any()) work = FreshWork(Store.GameDir(id), Log);   // a re-plan
                using (Recordings.Lock(RecordingPath(id), ct: ct))   // no import (any process's) between the snapshot's read and the preparation's
                {
                    // prepared first: it may find the community recording is for another build and leave it out of the rest
                    (prepared, recording) = (true, PrepareRecording(game, engine, index, work, rec.PlanRayTracing, Maximum(rec) || maxOnce, ct));
                    recorded = RecordedNow(game, rec, rec.PlanRayTracing, Maximum(rec) || maxOnce, engine);   // what PrepareRecording leaves out
                    RecordingsRead?.Invoke();
                    (preparedCommunity, preparedOfficial) = (CommunityObjects(id), OfficialInUse(id)?.Object);
                }
                return recording;
            }
            // middleware packs (another game's recording may have grown one this install's DLLs match): a change re-plans
            var packs = _planner as Planner;
            bool current = false;   // this rebuild found the warm still current: nothing to warm
            var shared = packs?.SharedFingerprint(game);   // before the build: a download during it must still re-plan
            var maps = MapsFingerprint(index);
            var steamNow = SteamDownloads(game, engine);   // before the build: a download landing during it re-plans
            var psoCacheNow = PsoCacheInUse(game, engine);
            if (PlanIsStale(game, rec, engine, maxOnce) || rec.PlanMaps != maps || (packs != null && packs.PackFingerprint(game, shared) != (rec.PlanMiddleware ?? "")))
            {
                Stage(QueueStage.Planning);
                await Gate(id, ct);
                (rec.PlanPerStage, rec.PlanMaximumCapped, rec.PlanRayTracing) = (PerStagePlans(rec, maxOnce), true, RayTracing(rec));
                // the last complete compile's plan of this build: a new plan many times its size logs what it was built from
                rec.WarmedPlanCount ??= rec.WarmedPlanItems != null && rec.WarmedPlanItems == rec.PlanItems ? PlanItems(rec.Plan?.Stats) : null;
                var lastComplete = rec.WarmedIndexHash == index.ContentHash ? rec.WarmedPlanCount : null;
                // a plan check builds beside the plan in use and swaps it in once built: one that fails leaves the game as it was
                var checking = Checking();
                if (rec.Plan != null && !checking)   // plan.bin is rewritten: no retry or later run may take its bytes for the plan recorded before
                {
                    rec.Plan = null;
                    Store.SaveGame(id, rec);
                }
                var planCaps = PlanCaps(id, rec);
                var plan = await Yielding(Lowered(Threads(), Low(), () => _planner.Build(game, engine, index, Prepared(), planCaps, checking ? Path.Combine(Store.GameDir(id), "check") : Store.GameDir(id), Log, ct, Maximum(rec) || maxOnce, rec.PlanRayTracing), checking));
                if (checking)
                {
                    var into = Path.Combine(Store.GameDir(id), Path.GetFileName(plan.FilePath));
                    File.Move(plan.FilePath, into, overwrite: true);
                    plan = plan with { FilePath = into };
                }
                if (RulesFile.Current.Hash(engine) != rules && ++replans <= MaxReplans)
                {
                    Log?.Report($"{game.Name}: the planning rules changed while it indexed and planned: again with the new ones");
                    goto indexAndPlan;
                }
                (rec.Plan, rec.PlanRules, rec.PlanCacheBudget, rec.PlanCacheCapGB, rec.PlanD3DSCacheCharged) = (plan, rules, planCaps.WarmCacheBudget, rec.CacheCapGB, planCaps.WarmCacheBudget != null && KeepsD3DSCache());   // the index's rules: after MaxReplans, a later change re-plans at the next compile
                (rec.PlanBuiltAt, rec.PlanVersion, rec.PlanReaderVersion, rec.PlanCheckFailed) = (DateTimeOffset.Now, Planner.Version, _reader.ReaderVersion(engine), 0);
                (rec.PlanMaps, rec.PlanApi, rec.PlanEngine, rec.PlanFiltering) = (maps, ApiKey(engine.GraphicsApi), EngineKey(engine), Filtering(id, false, engine));
                rec.PlanMiddleware = packs is null ? null : packs.SeededFingerprint(rec.Plan) ?? packs.PackFingerprint(game, shared);   // what the build read: it may have promoted into one
                // what the build read: a download landing during it leaves the plan stale
                (rec.PlanCommunity, rec.PlanOfficial, rec.PlanSteam, rec.PlanPsoCache) = (preparedCommunity, preparedOfficial, steamNow, psoCacheNow);
                if (rec.ResumeWork == null) rec.ResumeAt = 0;   // stopped by an earlier build: its items can't be compared with the new plan's
                var keys = PlanKeys(rec.Plan.FilePath);
                rec.PlanItems = keys == null ? null : PlanFingerprint(keys);
                rec.PlanKeysFile = KeyFiles.Write(Store.GameDir(id), "plan", PlannerMade(rec.Plan, work));
                // a newer planner or reader that compiles nothing the warm didn't (the same records, or fewer): the warm is current
                if (current = !maxOnce && PendingOf(game, rec, engine).Planned == 0 && !CurrentPlanner(rec.WarmedPlanVersion, rec.WarmedPlanRules, rec.WarmedReaderVersion, engine)
                              && StaleReason(game, rec, engine) == null)
                {
                    if (Reread(rec, engine)) (rec.WarmedIndexHash, rec.WarmedIndexStamp) = (index.ContentHash, indexStamp);
                    (rec.WarmedPlanVersion, rec.WarmedPlanRules, rec.WarmedReaderVersion, rec.WarmedPlanItems) = (rec.PlanVersion, rec.PlanRules, rec.PlanReaderVersion, rec.PlanItems);
                    rec.WarmedPlanCount = PlanItems(rec.Plan.Stats);
                }
                Store.SaveGame(id, rec);
                Store.PruneKeyFiles(id);   // after the save: the state never names a file that is gone
                if (lastComplete is > 0 and var was && PlanItems(rec.Plan.Stats) > PlanGrowth * was) LogPlanInputs(game, rec.Plan, was);
            }
            if (!maxOnce)   // CompileMax asked while this item planned (a plan check or idle rebuild it promoted): again, as a Maximum one
            {
                lock (_lock) maxOnce = _maxNext.Contains(id);
                if (maxOnce) goto indexAndPlan;
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
                    : rec.WarmedAt != null && StaleReason(game, rec, engine) is { } why ? why : "plan built"));
                return;
            }
            if (whenIdle && RulesGrew(game, rec, engine) is { } grew)
            {
                rec.IdleHeldBack = HeldBackKey(rec.PlanRules ?? RulesFile.Embedded.Hash(engine));   // the rules the plan recorded
                Store.SaveGame(id, rec);
                Set(new QueueItem(id, QueueStage.Done, null, null, grew));
                return;
            }
            if (CacheOffRefusal() is { } cacheOff)   // a plan check a Compile click took over
            {
                Set(new QueueItem(id, QueueStage.Failed, null, cacheOff, null));
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
            AftermathInit? aftermathUsed = null;
            var (aftermathRetry, aftermathGuess) = (false, false);
            await Lowered(Threads(), Low(), () => { _planner.Materialize(rec.Plan!, game, engine, _reader, recording, work, ct); return 0; });
            (string Dir, string Fingerprint, string WriteTime)? layer = null;   // a copy of the game's ReShade layer, made as each warm process starts
            if (_warmer is Warmer warmer) (warmer.Log, warmer.Ags, warmer.Layer, warmer.Aftermath) = (Log, g => agsUsed = AgsFor(g, rec, recover: true), (_, _) => layer?.Dir, g => { (aftermathUsed, aftermathRetry) = AftermathOf(g, rec, engine); aftermathGuess = aftermathRetry && AftermathGuess(g, engine); return aftermathUsed; });
            // the plan's own too: a synthesized compute stream has the record key of the game's create (UE 5.6, NVIDIA)
            SessionLog.WriteRayQueryKeys([Path.Combine(work, "scskiller.db"), Path.Combine(work, "scskiller_gen.db")], RayQueryKeysPath(id));
            // what complete warms on this driver already compiled is left out (CompiledItems), while nothing says the cache lost it
            var compiledFile = Path.Combine(Store.GameDir(id), CompiledItems.File);
            string[] workDbs = [Path.Combine(work, "scskiller.db"), Path.Combine(work, "scskiller_gen.db")];
            HashSet<string>? compiled = null;
            var stoppedFailed = Path.Combine(Store.GameDir(id), "stopped-" + Recordings.FailedFile);   // what stopped segments of this warm didn't create
            // work\ goes with the item while a resume starts past what it didn't create: kept until the warm completes
            void KeepStoppedFailed()
            {
                try
                {
                    using var kept = new FileStream(stoppedFailed, FileMode.Append);
                    foreach (var f in Directory.EnumerateFiles(work, Recordings.FailedFile, SearchOption.AllDirectories)) kept.Write(File.ReadAllBytes(f));
                    foreach (var f in Directory.EnumerateFiles(work, Recordings.FailedFile, SearchOption.AllDirectories)) File.Delete(f);   // a later stop of this item adds only its own
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    rec.ResumeAt = 0;
                    Log?.Report($"{game.Name}: keeping what the stopped compile didn't create failed ({e.Message}): it starts over");
                }
            }
            var stoppedFile = Path.Combine(Store.GameDir(id), StoppedFile);
            var stoppedUsed = false;   // compiled holds what stopped compiles created (StoppedLeftOut)
            // a stop's fold cut short (the game started, a db in use) leaves its record uncounted until a fold puts every db in
            if (rec.StoppedCreated is { Folded: false })
            {
                Fold();
                foldRan = false;   // this compile's own staged dbs still need theirs
            }
            if (FullReplayReason(game, rec, engine, Snapshot()) is { } all)
            {
                if (all == NothingOnRecord && StoppedLeftOut(game, rec, indexed, Snapshot().Id) is { } stopped)
                {
                    (compiled, stoppedUsed) = (stopped, true);
                    keptEarlier = rec.Plan!.Platform == VulkanPlan.Platform ? 0 : CompiledItems.LeaveOut(workDbs, stopped);
                    Log?.Report($"{game.Name}: {keptEarlier:N0} pipelines stopped compiles on this driver created are left out");
                }
                else
                {
                    if (rec.StoppedCreated is { Folded: true }) DropStopped(id, rec);   // an uncounted one may still count after its fold
                    Log?.Report($"{game.Name}: replaying every pipeline: {all}");
                }
            }
            else if (CompiledItems.Load(compiledFile) is not { } done) Log?.Report($"{game.Name}: replaying every pipeline: what earlier compiles created isn't on record");
            else
            {
                compiled = done;
                var leftOut = (rec.Plan!.Platform == VulkanPlan.Platform ? 0 : CompiledItems.LeaveOut(workDbs, done))
                    + (File.Exists(Path.Combine(work, VulkanPlan.WorkFile)) ? VulkanPlan.LeaveOut(Path.Combine(work, VulkanPlan.WorkFile), done) : 0);
                Log?.Report($"{game.Name}: {leftOut:N0} pipelines an earlier compile on this driver created are left out"
                    + (IsPartlyWarmed(rec.FirstLaunch) ? ", the game's launch after it still compiled pipelines that compile didn't have" : ""));
                (keptEarlier, refusedBefore) = (leftOut, rec.LastWarmFailed ?? 0);
            }
            var skipped = Planner.SkippedIn(work);   // not replayed (a shader not in this install): reported apart from failed
            var needsRecording = Planner.NeedsRecordingIn(work);
            var cap = rec.Careful ? CarefulThreads(OwnDriver) : null;
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
                    CacheSampled?.Invoke();
                    await Task.WhenAny(run.Completion, Task.Delay(cacheKeys.Count > 0 ? AttributionInterval : Poll));
                }
            });
            // The game and its warm must not run together: the second process of an exe name gets a second, separate set of
            // cache files (ARCHITECTURE.md), so the game would play cold and the warm would fill files it never reads. The
            // warm waits while the game runs, and stops (resumable) when it starts; then it goes on from there.
            while (true)
            {
                await WhileGameRuns(exe, game.Name, ct, () => { if (LearnKeys(game, rec, WarmExeName(game, rec))) Store.SaveGame(id, rec); });
                await Gate(id, ct);
                // Steam's replay of a game's Vulkan shaders writes the cache a Vulkan warm under its redirect would: after it
                if ((rec.Plan!.Platform == VulkanPlan.Platform || DxvkSteamDir(game) != null) && SteamReplaying())
                {
                    Log?.Report($"{game.Name}: the compile waits for Steam's shader pre-caching to finish");
                    while (SteamReplaying()) await Task.Delay(Poll, ct);
                    continue;
                }
                // the before run's Vulkan cache goes back into the folder this warm writes first
                if (rec.Plan!.Platform == VulkanPlan.Platform && PutBackGl(id) == "")
                {
                    Log?.Report($"{game.Name}: the compile waits to put back the Vulkan cache set aside for the before run");
                    while (PutBackGl(id) == "") await Task.Delay(Poll, ct);
                    continue;
                }
                // Stage the name the game is launched with: AMD keys its cache on the exact case (the game may just have run)
                if (MergeLaunched(game, rec, null)) Store.SaveGame(id, rec);
                exe = WarmExeName(game, rec);
                RedetectGpu();   // the warm targets the adapter by LUID, which a driver update replaces
                // what this warm launches on and is recorded for, even if a check during it sees a newer one; an unknown
                // driver (DXGI gave no version yet) resets nothing and records none
                var snap = Snapshot();
                var (gpu, driverId) = (snap.Gpu, snap.Id);
                if (AdoptDriverId(rec, snap)) Store.SaveGame(id, rec);   // an earlier build's keys for this driver are kept
                // also a continuation after yielding to the game; unknown (an earlier build's stop) counts as another one
                if (driverId != null && rec.ResumeAt > 0 && rec.ResumeGpu != driverId)
                {
                    Log?.Report($"{game.Name}: the stopped compile was for {(rec.ResumeGpu == null ? "an unknown" : "another")} driver: starting over on driver {gpu.DriverVersion}");
                    (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed) = (0, 0, 0, 0);
                    Store.SaveGame(id, rec);
                }
                // the waits may have brought another driver, a launch or an evicted cache: then everything goes in again
                if (compiled != null && (stoppedUsed ? StoppedLeftOut(game, rec, indexed, driverId) == null ? StoppedGone : null
                        : FullReplayReason(game, rec, engine, snap)) is { } lost)
                {
                    Log?.Report($"{game.Name}: replaying every pipeline: {lost}");
                    await Lowered(Threads(), Low(), () => { _planner.Materialize(rec.Plan!, game, engine, _reader, recording, work, ct); return 0; });
                    if (stoppedUsed) DropStopped(id, rec);
                    (compiled, keptEarlier, stoppedUsed) = (null, 0, false);
                }
                // the stopped part's item numbers count in the other set of items
                if (rec.ResumeAt > 0 && rec.ResumeLeftOut != (compiled != null))
                {
                    Log?.Report($"{game.Name}: the stopped compile {(rec.ResumeLeftOut ? "left out" : "replayed")} what was compiled before: starting over");
                    (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed) = (0, 0, 0, 0);
                    Store.SaveGame(id, rec);
                }
                // now, not before the waits (for the game, idle, a pause): ReShade or its add-ons may have changed meanwhile
                if (BlockingMod(game) is { } mod) throw new NotReadyException($"not ready: {ShaderModReason(mod)}");
                if (LauncherClearsNext(game)) throw new NotReadyException($"not ready: {LauncherClearsReason}");
                // Mesa's cache goes only where Steam points the game (the warm's host takes the same)
                try { if (MesaLinux.Is(Vendor.Caps)) MesaLinux.CacheEnvironment(Store.GameDir(id), game, make: false); }
                catch (InvalidOperationException e) { throw new NotReadyException($"not ready: {e.Message}"); }
                // queued before the pair armed, or by another process: the before run must find the cache cold
                if (PairHoldsCache(id, compiling: true) is { } pair)
                {
                    Log?.Report($"{game.Name}: not compiled: {pair}");
                    throw new NotReadyException(pair);
                }
                layer = LayerFor(game, work);
                if (layer != null) Log?.Report($"{game.Name}: compiling through a copy of the game's ReShade layer");
                // the stopped part's pipelines went through another layer, or none: they aren't this one's
                if (rec.ResumeAt > 0 && rec.ResumeLayer != layer?.Fingerprint && (layer == null || rec.ResumeLayer != layer.Value.WriteTime))
                {
                    Log?.Report($"{game.Name}: the stopped compile ran through {(rec.ResumeLayer == null ? "no" : "another")} ReShade layer: starting over");
                    (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed) = (0, 0, 0, 0);
                    Store.SaveGame(id, rec);
                }
                // case alone is MergeLaunched's to judge
                if (rec.ResumeAt > 0 && !exe.Equals(rec.ResumeExe, StringComparison.OrdinalIgnoreCase))
                {
                    Log?.Report($"{game.Name}: the stopped compile was for {rec.ResumeExe ?? "another exe"}: starting over for {exe}");
                    (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed) = (0, 0, 0, 0);
                    Store.SaveGame(id, rec);
                }
                // the stopped part's pipelines went to another AGS app name's cache (the game was seen holding its key since)
                var ags = AgsFor(game, rec, recover: true)?.App ?? "";
                var stoppedAgs = rec.ResumeAgs ?? AgsFor(game, rec)?.App ?? "";   // unknown: an earlier build's, without recovered names
                if (rec.ResumeAt > 0 && stoppedAgs != ags)
                {
                    Log?.Report($"{game.Name}: the stopped compile was for {(stoppedAgs == "" ? "the exe name's cache" : $"the AGS app name {stoppedAgs}")}: starting over");
                    (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed) = (0, 0, 0, 0);
                    Store.SaveGame(id, rec);
                }
                var aftermath = Aftermath(game, rec, engine) != null;
                if (rec.ResumeAt > 0 && rec.ResumeAftermath != aftermath)
                {
                    Log?.Report($"{game.Name}: the stopped compile ran {(rec.ResumeAftermath ? "with" : "without")} NVIDIA Aftermath: starting over");
                    (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed) = (0, 0, 0, 0);
                    Store.SaveGame(id, rec);
                }
                if (stoppedUsed && StoppedKeyChanged(rec.StoppedCreated, driverId, exe, layer?.Fingerprint, ags, aftermath,
                        OperatingSystem.IsWindows() && D3DSCache.Delivered(game.ExePath) != null) is { } otherKey)
                {
                    DropStopped(id, rec);
                    Log?.Report($"{game.Name}: replaying every pipeline: {otherKey}");
                    await Lowered(Threads(), Low(), () => { _planner.Materialize(rec.Plan!, game, engine, _reader, recording, work, ct); return 0; });
                    (compiled, keptEarlier, stoppedUsed) = (null, 0, false);
                    (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed) = (0, 0, 0, 0);   // numbered in the left-out set
                    Store.SaveGame(id, rec);
                }
                bool background;
                lock (_lock) background = Background || _whenIdle.Contains(id);   // read at the warm: a Compile click before it made the item the user's
                var threads = ThreadsOverride ?? (background ? Settings.BackgroundThreads : Settings.Threads);
                var vulkan = rec.Plan!.Platform == VulkanPlan.Platform;
                var workOrder = vulkan ? null : CachedWorkOrder(workDbs, game.Name);
                // a rebuilt plan, another recording or other shaders in the install number the items anew
                if (rec.ResumeAt > 0 && rec.ResumeWork != null && rec.ResumeWork != workOrder)
                {
                    Log?.Report($"{game.Name}: the stopped compile's pipelines changed since: starting over");
                    (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed) = (0, 0, 0, 0);
                    Store.SaveGame(id, rec);
                }
                // the stopped part left out what the delivery served then
                var delivery = OperatingSystem.IsWindows() && !vulkan ? D3DSCache.Delivered(game.ExePath)?.Stamp : null;
                if (rec.ResumeAt > 0 && rec.ResumeDelivery != null && rec.ResumeDelivery != delivery)
                {
                    Log?.Report($"{game.Name}: the game's Advanced Shader Delivery changed since the stopped compile: starting over");
                    (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed) = (0, 0, 0, 0);
                    Store.SaveGame(id, rec);
                }
                // another driver's crashers are tried again, each in the warm: the ones that crash again are kept for this driver
                if (rec.CrashKeys.Count > 0 && driverId != null && !CrashKeysCurrent(rec, snap))
                {
                    Log?.Report($"{game.Name}: trying again the {rec.CrashKeys.Count:N0} pipelines that crashed another driver");
                    (rec.CrashKeys, rec.CrashKeysDriver) = ([], null);
                }
                // a Vulkan warm has no item index to resume at: it replays its archive again, what was created costs a cache hit
                var options = new WarmOptions(threads, background ? WarmPriority.Idle : Settings.Priority, vulkan ? 0 : rec.ResumeAt, CompileMemoryGB(Settings) * 1024,
                    driverId != null && rec.CrashKeys.Count > 0 ? [.. rec.CrashKeys] : null, cap is { } most ? Math.Min(threads, most) : 0, vulkan, vulkan ? VulkanWarmEnv(game, rec) : DxvkWarmEnv(game), CompileCap(id),
                    !vulkan && SkipD3DSCacheFlush(), delivery, KnownKeysFor(rec, driverId, vulkan, game.Name));
                if (options.StartAt == 0) (rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed, rec.ResumeDriverSkipped, rec.ResumeInvalid, rec.ResumeUnsupported, rec.ResumeKnown) = (0, 0, 0, 0, 0, 0, 0);   // a resume point reset elsewhere drops its segments too
                if (options.StartAt == 0) (rec.ResumeSkipEnd, rec.ResumeSkipWidth) = (0, 0);   // a skipped window is its compile's alone
                // stopped in Steam's pipelines: the warm replays them all again, and counts them again
                else if (!vulkan && options.StartAt < SteamFirstRun.Pipelines(work)) (rec.ResumeFailed, rec.ResumeDriverSkipped, rec.ResumeInvalid, rec.ResumeUnsupported, rec.ResumeKnown) = (0, 0, 0, 0, 0);
                if (options.StartAt == 0) File.Delete(stoppedFailed);
                var staged = game with { ExePath = Path.Combine(Path.GetDirectoryName(game.ExePath)!, exe) };
                var reporter = new Reporter<WarmProgress>(p =>
                {
                    var was = progress?.Done;
                    progress = p with { Failed = p.Failed + rec.ResumeFailed, DriverSkipped = p.DriverSkipped + rec.ResumeDriverSkipped, Invalid = p.Invalid + rec.ResumeInvalid,
                        Unsupported = p.Unsupported + rec.ResumeUnsupported, Skipped = skipped };
                    if (savingRun?.LastDone is { } next && next != armed) ArmSaving(null);   // a segment's next process: its own done event ends the warm
                    if (saving == null || was != progress.Done) Stage(QueueStage.Warming);   // the done event's repeats while the process exits stay in Saving
                });
                (warmClock, rtSkipped, warmPlan) = (Stopwatch.StartNew(), !rec.PlanRayTracing, rec.Plan?.Stats);
                if (PutBack(id, late: false)) ShowKept(id);   // the warm adds to the files the game will open
                index = null!;   // the warm needs only its hash (indexed): 650 MB for a 1.7M-pipeline Unreal game
                ReleaseMemory();   // else the plan and materialize peak stays committed for the whole warm (10 GB measured)
                // a staged folder outlives a compile killed before its fold: MergeD3DSCache finds it by this
                while (!_foldGate.TryEnter(Poll)) ShowWait(QueueStage.Warming, FoldWaitNote);   // another game's fold may take minutes
                try
                {
                    lock (_lock) _warming.Add(id);
                    if (OperatingSystem.IsWindows())
                        AppStore.Locked(Path.Combine(Store.GameDir(id), "state.json"), () =>
                        {
                            var r = Store.LoadGame(id);
                            r.D3DSCacheSince ??= started;
                            Store.SaveGame(id, r);
                            rec.D3DSCacheSince = r.D3DSCacheSince;
                        });
                }
                finally { _foldGate.Exit(); }
                EndWait(FoldWaitNote);
                var run = _warmer is Warmer real ? real.Start(staged, work, options, reporter, gpu) : _warmer.Start(staged, work, options, reporter);
                lock (_lock) _run = run;
                var attribution = Attribution(run, exe);
                if (ct.IsCancellationRequested) run.Stop();   // stopped while it was starting
                if (!_go.IsSet) run.Pause();
                ArmSaving(run);
                var (result, yielded) = await Watch(run, id, exe, game.Name, vulkan);
                var exited = EndSaving();
                if (exited != null) Log?.Report($"{game.Name}: the warm process exited {exited.Value.TotalSeconds:0.0} s after its last pipeline");
                Finishing(true, exited != null ? TimeSpan.Zero : FinishingAfter);   // straight after Saving, which it replaces
                await attribution;   // a sample may be running: its keys count (cacheKeys is read below)
                cacheKeys.UnionWith(result.CacheKeysAdded ?? []);
                if (result.AftermathDropped && options.StartAt > 0)
                {
                    // the warm started over without Aftermath (WarmRun): the stopped segments filled the other key
                    options = options with { StartAt = 0 };
                    (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed, rec.ResumeDriverSkipped, rec.ResumeInvalid, rec.ResumeUnsupported, rec.ResumeKnown) = (0, 0, 0, 0, 0, 0, 0, 0);
                    File.Delete(stoppedFailed);
                    (rec.ResumeSkipEnd, rec.ResumeSkipWidth) = (0, 0);
                }
                result = result with { Failed = rec.ResumeFailed + result.Failed, DriverSkipped = rec.ResumeDriverSkipped + result.DriverSkipped, Known = rec.ResumeKnown + result.Known, Invalid = rec.ResumeInvalid + result.Invalid,
                    Unsupported = rec.ResumeUnsupported + result.Unsupported, Skipped = skipped };   // a resumed warm counts only its own
                // Failed and ResumeFailed keep them: read from this compile's logs, a stopped earlier compile's share stays failed
                var workLog = WorkLogs(work);
                unbuilt = Math.Min(result.Failed, Unbuilt11(workLog));
                failedBy = TopFailKind(workLog);
                progress = new WarmProgress(result.Done, result.Total, result.Failed - unbuilt, progress?.PerSecond ?? 0, result.CacheGrowthBytes, result.Skipped, DriverSkipped: result.DriverSkipped,
                    Invalid: result.Invalid, Unsupported: result.Unsupported);
                // Saved below: whatever the outcome, those files are this game's. A D3D12 key other than the name hash is an
                // AMD app profile's; kept like any other (IsNameHashed reads the case rule from it).
                NoteProfile(game, exe, cacheKeys.Where(k => !rec.CacheKeys.Contains(k)).ToList());
                rec.CacheKeys.UnionWith(cacheKeys);
                if (result.Crashed is { Count: > 0 })   // whatever the outcome: a later compile or resume must not crash on them again
                    rec.CrashKeysDriver = driverId ?? rec.CrashKeysDriver;
                rec.CrashKeys.UnionWith(result.Crashed ?? []);
                crashed = result.Crashed?.Count ?? 0;
                var passedKeys = new HashSet<string>(options.SkipKeys ?? [], StringComparer.OrdinalIgnoreCase);
                crashedBefore = result.Crashed?.Count(passedKeys.Contains) ?? 0;
                known = result.Known;
                if (result.Culprits is { Count: > 0 } culprits && driverId != null && !vulkan)   // whatever the outcome: a later compile on this driver skips them
                {
                    if (rec.KnownKeysDriver != driverId) (rec.KnownKeys, rec.KnownKeysDriver) = ([], driverId);
                    var room = MaxKnownKeys - rec.KnownKeys.Count;
                    var fresh = culprits.Where(k => !rec.KnownKeys.Contains(k)).ToList();
                    rec.KnownKeys.UnionWith(fresh.Take(Math.Max(0, room)));
                    if (fresh.Count > room)
                        Log?.Report($"{game.Name}: {fresh.Count - Math.Max(0, room):N0} pipelines that crashed or hung the GPU driver aren't remembered: {MaxKnownKeys:N0} are already, so they are skipped for this compile only");
                }
                // created per segment: the stopped ones' and this one's, each never below 0
                var created = Math.Max(0, options.StartAt - rec.ResumeFailed - rec.ResumeDriverSkipped - rec.ResumeInvalid - rec.ResumeUnsupported)
                    + Math.Max(0, result.Done - options.StartAt - (result.Failed - rec.ResumeFailed) - (result.DriverSkipped - rec.ResumeDriverSkipped) - (result.Invalid - rec.ResumeInvalid)
                        - (result.Unsupported - rec.ResumeUnsupported) - crashed);
                if (result is { Outcome: WarmOutcome.Completed, Failed: > 0 } && created == 0 && compiled is not { Count: > 0 })
                {
                    // nothing created, now or by an earlier compile: the game isn't compiled, and the same driver and rules give the same result
                    result = result with { Outcome = WarmOutcome.Failed, Error = AllFailedReason(work, result.Failed), Resumable = false };
                    (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed) = (0, 0, 0, 0);
                    rec.IdleHeldBack = HeldBackKey(rec.PlanRules ?? RulesFile.Embedded.Hash(engine));
                    KeepRejectsLog(work, id, result.Failed);
                }
                else if (result is { Outcome: WarmOutcome.Completed, Total: 0, Full: null } && compiled is not { Count: > 0 })
                {
                    result = result with { Outcome = WarmOutcome.Failed, Error = NothingToCompileReason, Resumable = false };
                    (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed) = (0, 0, 0, 0);
                    rec.IdleHeldBack = HeldBackKey(rec.PlanRules ?? RulesFile.Embedded.Hash(engine));
                }
                var (items, seconds) = (rec.ResumeItems + result.Done - options.StartAt, rec.ResumeSeconds + result.Elapsed.TotalSeconds);
                (ended, took) = (result, TimeSpan.FromSeconds(seconds));
                void KeepResume()
                {
                    KeepStoppedCreated();
                    // without the work's hash the resume point couldn't be checked: none
                    (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed, rec.ResumeGpu, rec.ResumeLayer, rec.ResumeExe, rec.ResumeAgs) = (workOrder != null || vulkan ? result.Done : 0, items, seconds, result.Failed, driverId, layer?.Fingerprint, exe, ags);
                    (rec.ResumeDriverSkipped, rec.ResumeKnown, rec.ResumeLeftOut, rec.ResumeAftermath, rec.ResumeWork) = (result.DriverSkipped, result.Known, compiled != null, aftermathUsed != null && !result.AftermathDropped, workOrder);
                    (rec.ResumeInvalid, rec.ResumeUnsupported) = (result.Invalid, result.Unsupported);
                    rec.ResumeDelivery = delivery;
                    // the estimate's rate until a complete warm on this driver measures one; a run ending on a retry doesn't say
                    // whether the hook skipped d3dscache's flushes, and a rate RateOn wouldn't take must not replace one it would
                    if (ColdWarm(rec, driverId) && items >= 1000 && seconds > 1 && carefulPasses == 0 && RateDriver(driverId, result.D3DSCacheNoFlush) is var rated
                        && rated == RateDriver(driverId, SkipD3DSCacheFlush()))
                        (rec.PsoPerSecond, rec.PsoPerSecondDriver) = (items / seconds, rated);
                    KeepStoppedFailed();
                }
                // what this compile created below its resume point (each process that exited names what it didn't create, a killed
                // one doesn't), while no compile on this driver completed: later compiles leave it out while StoppedLeftOut holds
                void KeepStoppedCreated()
                {
                    if (!KeepsD3DSCache() || vulkan || carefulPasses > 0 || driverId == null || indexed == null || result.Unnamed
                        || aftermathUsed != null || result.AftermathDropped || delivery != null || compiled != null && !stoppedUsed || !ColdWarm(rec, driverId)
                        || File.Exists(Path.Combine(work, VulkanPlan.WorkFile))) return;
                    var key = new StoppedCreated(driverId, indexed, exe, layer?.Fingerprint, ags);
                    try
                    {
                        // this run's processes only: an earlier segment's was kept at its own stop, unless a killed process left it unnamed
                        var made = CompiledItems.Created(workDbs, Directory.EnumerateFiles(work, Recordings.FailedFile, SearchOption.AllDirectories), options.StartAt, result.Done, psosOnly: true);
                        // the set this compile left out, while the cache still holds what the folds put in, or one no fold has
                        // counted yet whose baseline its count will check
                        var earlier = rec.StoppedCreated is { } was && was with { Bytes = null, At = null, Folded = false } == key
                            ? was.Folded ? stoppedUsed && StoppedLeftOut(game, rec, indexed, driverId) != null ? compiled : null
                                : was.Bytes != null ? CompiledItems.Load(stoppedFile) : null
                            : null;
                        if (earlier != null) made.UnionWith(earlier);
                        // the count checks the game's dbs still hold what they held then: a db made again or shorter since lost pipelines
                        var dbs = D3DSCacheDbs(D3DSCacheNames(rec));
                        key = earlier != null ? key with { Bytes = rec.StoppedCreated!.Bytes, At = rec.StoppedCreated.At }
                            : dbs is { Count: > 0 } ? key with { Bytes = Lengths(dbs), At = DateTimeOffset.UtcNow } : key;
                        AppStore.WriteAtomic(stoppedFile, CompiledItems.Bytes(made));
                        rec.StoppedCreated = key;
                        stoppedHere = true;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        rec.StoppedCreated = null;
                        Log?.Report($"{game.Name}: couldn't note what the stopped compile created: {e.Message}");
                    }
                }
                // given up where the driver kept failing: the next compile goes on past a window from there, skipped for that compile
                void SkipPast()
                {
                    var steam = SteamFirstRun.Pipelines(work);
                    var at = rec.ResumeAt;
                    // KeepResume's start-over (no work hash, or the kept keys not written) stays one
                    if (vulkan || at != result.Done || at < steam || at >= result.Total) return;
                    // the same spot again (within one window past the last one's end): twice that window
                    var width = at >= rec.ResumeSkipEnd && at < rec.ResumeSkipEnd + rec.ResumeSkipWidth ? 2 * rec.ResumeSkipWidth : GiveUpSkip;
                    var end = Math.Min(result.Total, at + width);
                    var passes = WarmPasses.Read(work);
                    try
                    {
                        // as the warm's own skips: never on record as created
                        var keys = WarmPasses.KeysOf(work, Items(at, end).Select(o => passes?.Locate(o - steam).Start ?? o - steam));
                        using var kept = new FileStream(stoppedFailed, FileMode.Append);
                        foreach (var k in keys) kept.Write(Convert.FromHexString(k));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        Log?.Report($"{game.Name}: keeping what the compile skips past failed ({e.Message}): it starts over");
                        rec.ResumeAt = 0;
                        return;
                    }
                    static IEnumerable<long> Items(long from, long to) { for (var i = from; i < to; i++) yield return i; }
                    Log?.Report($"{game.Name}: the GPU driver kept failing at pipeline {at:N0}: the next compile goes on from {end:N0}, the {end - at:N0} between skipped for that compile");
                    (rec.ResumeAt, rec.ResumeDriverSkipped, rec.ResumeSkipEnd, rec.ResumeSkipWidth) = (end, rec.ResumeDriverSkipped + end - at, end, width);
                }
                if (yielded && (result.Outcome == WarmOutcome.Stopped || result is { Outcome: WarmOutcome.Failed, Resumable: true, Error: Warmer.StuckError }) && !ct.IsCancellationRequested)
                {
                    KeepResume();
                    Store.SaveGame(id, rec);
                    Finishing(false);
                    continue;
                }
                switch (result.Outcome)
                {
                    case WarmOutcome.Completed:
                        bool cold = ColdWarm(rec, driverId);   // before the fields it reads are set to this warm
                        // the earlier warms' cache files gone, made again or shrunk during this one (AMD evicts at its cap, at
                        // device creation): what they compiled may be gone too
                        if (compiled != null && (stoppedUsed ? StoppedLeftOut(game, rec, indexed, driverId) == null ? StoppedGone : null
                                : CacheFilesChanged(id, rec, during: true)) is { } lostDuring)
                        {
                            if (stoppedUsed) DropStopped(id, rec);
                            Log?.Report($"{game.Name}: {lostDuring} during the compile: only what it created stays on record as compiled");
                            (compiled, keptEarlier) = (null, 0);
                        }
                        (rec.WarmedDriverId, rec.WarmedDriverVersion) = (driverId, gpu.DriverVersion);
                        (rec.WarmedPlanVersion, rec.WarmedPlanRules, rec.WarmedReaderVersion) = (rec.PlanVersion, rec.PlanRules, rec.PlanReaderVersion);
                        (rec.WarmedExeName, rec.WarmedPerStage, rec.WarmedRayTracing) = (exe, rec.PlanPerStage, rec.PlanRayTracing);
                        // kept what an earlier compile left out for another delivery: the next compile replays everything
                        rec.WarmedDelivery = compiled != null && rec.WarmedDelivery != null && rec.WarmedDelivery != delivery ? "" : delivery;
                        var aftermathOn = aftermathUsed != null && !result.AftermathDropped;
                        // a guess keeps the plain compile's first launch until its own launch is judged against it (EvaluateGame);
                        // one on another build after an Aftermath compile has no plain launch to beat: judged by a miss or a reset only
                        var retryKey = AftermathRetryKey(aftermathGuess ? game.Version ?? ExeStamp(game) : driverId, exe);
                        rec.AftermathPlainHits = !aftermathRetry || !aftermathOn ? null
                            : rec.WarmedAftermath ? SameKey(rec.AftermathRetry, retryKey) ? rec.AftermathPlainHits : aftermathGuess ? 0 : null
                            : aftermathGuess ? rec.FirstLaunch?.Hits ?? 0 : null;
                        (rec.WarmedAftermath, rec.AftermathLost) = (aftermathOn, false);
                        if (aftermathRetry) rec.AftermathRetry = retryKey;   // also when it was dropped: tried once
                        (rec.WarmedAgsApp, rec.AgsMissed) = (agsUsed?.App, rec.AgsMissed && agsUsed == null);   // after a miss, AGS returns only once the game's keys prove it
                        (rec.WarmedPlanItems, rec.WarmedPlanCount, rec.IdleHeldBack) = (rec.PlanItems, PlanItems(rec.Plan?.Stats), null);
                        rec.WarmedLayer = layer?.Fingerprint;
                        (rec.WarmedAt, rec.LastWarmTime, rec.LastCacheGrowthBytes) = (DateTimeOffset.Now, TimeSpan.FromSeconds(seconds), result.CacheGrowthBytes);
                        (rec.LastWarmFailed, rec.LastWarmSkipped, rec.LastWarmNeedsRecording, rec.WarmedKeys) = (result.Failed - unbuilt, result.Skipped, needsRecording, [.. cacheKeys]);
                        rec.WarmedFiles = AppCache is AmdAppCache amd ? amd.D3D12FileNames(cacheKeys)
                            : AppCache?.FilesOf(cacheKeys).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        // a name in two keys' folders (NVIDIA on Linux): the shorter one, so neither reads as shrunk
                        rec.WarmedFileBytes = AppCache?.FilesOf(cacheKeys).Where(f => rec.WarmedFiles!.Contains(f.Name)).GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.Min(f => f.Length), StringComparer.OrdinalIgnoreCase);
                        rec.WarmedFileIds = AppCache is AmdAppCache dxc ? rec.WarmedFiles.Select(n => (n, Id: AmdKept.Id(Path.Combine(dxc.DxcDir, n))))
                            .Where(x => x.Id != null).ToDictionary(x => x.n, x => x.Id!, StringComparer.OrdinalIgnoreCase) : null;
                        _ = SteamParc(id, rec, out var parc);
                        (rec.WarmedSteamParcId, rec.WarmedSteamParcBytes) = (parc?.Id, parc?.Length);
                        rec.WarmedFileUsed = AppCache is NvidiaAppCache nvCache ? rec.WarmedFiles!.Select(n => (n, Used: NvidiaAppCache.UsedBytes(Path.Combine(nvCache.Dir, n))))
                            .Where(x => x.Used != null).ToDictionary(x => x.n, x => x.Used!.Value, StringComparer.OrdinalIgnoreCase) : null;
                        KeepAside(id, rec, game.Name);
                        (rec.LastWarmCrashed, rec.LastWarmCrashedBefore, rec.LastWarmDriverSkipped, rec.LastWarmKnown, rec.LastWarmUnbuilt, rec.LastWarmInvalid, rec.LastWarmUnsupported) =
                            (crashed, crashedBefore, result.DriverSkipped, result.Known, unbuilt, result.Invalid, result.Unsupported);
                        (rec.LastWarmTried, rec.LastWarmFailedBy) = (result.Total, TopFailKind(workLog));
                        // a run with no room to start says nothing of what an earlier one left
                        rec.WarmedRoomLeft = result.Full == null ? null : result.Total > result.Done ? result.Total - result.Done : rec.WarmedRoomLeft ?? 0;
                        if (result.Full is { } full && result.Total > result.Done)
                            Log?.Report($"{game.Name}: {result.Total - result.Done:N0} of the {result.Total:N0} pipelines aren't compiled: {full}");
                        // what the recorder's csv counts as known from now on; the next refresh (this compile's closing one)
                        // puts it next to the recorder with the keys file, under the recording lock this compile mustn't wait for
                        try
                        {
                            var failedFiles = Directory.EnumerateFiles(work, Recordings.FailedFile, SearchOption.AllDirectories).Append(stoppedFailed).ToList();   // every warm process's
                            var warmedFile = Path.Combine(Store.GameDir(id), Recordings.WarmedFile);
                            // also a DirectX plan's Steam pipelines (Planner.WithSteam)
                            var vkCreated = File.Exists(Path.Combine(work, VulkanPlan.WorkFile)) ? VulkanPlan.Created(Path.Combine(work, VulkanPlan.WorkFile), failedFiles) : null;
                            AppStore.WriteAtomic(warmedFile, Recordings.Warmed(workDbs, failedFiles,
                                compiled != null && File.Exists(warmedFile) ? File.ReadAllBytes(warmedFile) : null, vkCreated));
                            rec.KeysPending = true;
                            // a resumed warm's earlier segments may have lost what they created (AMD's cap): only the last one's items go on record,
                            // its --start an item number without passes or Steam's pipelines first. A full one's work dbs hold items it never reached
                            var lastOnly = options.StartAt > 0 && carefulPasses == 0 && vkCreated == null && !options.Vulkan;
                            AppStore.WriteAtomic(compiledFile, CompiledItems.Bytes(result.Full != null || options.StartAt > 0 && !lastOnly ? compiled ?? []
                                : (options.Vulkan ? vkCreated ?? [] : CompiledItems.Created(workDbs, failedFiles, options.StartAt).Concat(vkCreated ?? [])).Union(compiled ?? [])));
                            lock (_lock) _maxNext.Remove(id);
                            File.Delete(stoppedFailed);
                            rec.StoppedCreated = null;   // in compiledFile from here on
                            File.Delete(stoppedFile);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                        {
                            Log?.Report($"{game.Name}: couldn't note what the compile created: {e.Message}");
                            try { File.Delete(compiledFile); }   // an earlier warm's must not pass this one's checks
                            catch (Exception d) when (d is IOException or UnauthorizedAccessException) { Log?.Report($"{game.Name}: {CompiledItems.File} not removed: {d.Message}"); }
                        }
                        // what this warm replayed: what is new after it is derived against it (PendingOf)
                        var warmKeys = inputs.Select(WarmInputs.Token);
                        if (OwnDriver == GpuVendor.Nvidia)   // a planned pipeline counts by its stages there (PendingOf)
                            warmKeys = warmKeys.Concat(WarmInputs.PlannedStages(rec.Plan!.FilePath, inputs, CrashInputs(CrashKeysNow(rec), rec.Plan.FilePath).Contains));
                        rec.WarmKeysFile = KeyFiles.Write(Store.GameDir(id), "warm", warmKeys.Distinct());
                        (rec.WarmedIndexHash, rec.WarmedExeStamp, rec.WarmedGameVersion, rec.WarmedIndexStamp) = (indexed!, ExeStamp(game), game.Version, indexStamp);
                        (rec.CompileFailedBuild, rec.WarmedD3DSCache, rec.WarmedD3DSCacheBytes, rec.D3DSCacheDamaged, completed) = (null, true, null, false, true);   // the bytes once the dbs are folded
                        (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds, rec.ResumeFailed, rec.ResumeSkipEnd, rec.ResumeSkipWidth) = (0, 0, 0, 0, 0, 0);
                        (rec.WarmedCareful, rec.FirstLaunch) = (carefulPasses > 0, null);   // judged again by the next launch
                        if (cold && items >= 1000 && seconds > 1 && carefulPasses == 0)   // a careful warm's rate mixes both schedules
                        {
                            // the hook's outcome, not the plan: without d3dscache's import the warm ran at the flushing rate
                            (rec.PsoPerSecond, rec.PsoPerSecondDriver) = (items / seconds, RateDriver(driverId, result.D3DSCacheNoFlush));
                            SeedWarmRate(rec.PsoPerSecond.Value, driverId, result.D3DSCacheNoFlush);
                            var n = result.Done - options.StartAt;   // the growth is the last segment's
                            if (result.CacheGrowthBytes > 0 && n > 0) rec.BytesPerPso = (double)result.CacheGrowthBytes / n;
                        }
                        Store.SaveGame(id, rec);
                        Store.PruneKeyFiles(id);
                        KeepRejectsLog(work, id, result.Failed);
                        DropFailedLog(id);
                        SendDiagnostics(game, indexed!, work, null, result.Crashed);
                        SteamPlaceSoon(id, exited: false);
                        Finishing(false);
                        Fold();   // before Done shows: a game started then gets the dbs, or waits while it runs
                        Finishing(true, FinishingAfter);
                        Close();
                        Bookkept();
                        Ended("done");
                        Stage(QueueStage.Done);
                        break;
                    case WarmOutcome.Stopped:
                        KeepResume();
                        Store.SaveGame(id, rec);
                        Bookkept();
                        Ended("stopped");
                        Stage(QueueStage.Stopped);
                        break;
                    default:
                        if (result.Resumable) KeepResume();
                        if (result.Resumable && Warmer.GaveUp(result.Error)) SkipPast();
                        Store.SaveGame(id, rec);
                        SendDiagnostics(game, indexed!, work, DiagnosticFailure.FromWarm(result.Error), result.Crashed);
                        Bookkept();
                        Ended("failed");
                        Stage(QueueStage.Failed, result.Error + KeptLog(result.LogPath, id));
                        break;
                }
                break;
            }
        }
        catch (OperationCanceledException)
        {
            EndSaving();
            Finishing(false);
            Ended("cancelled");
            Stage(QueueStage.Stopped);
        }
        catch (Exception e)
        {
            EndSaving();
            Finishing(false);
            Ended("failed");
            if (indexed != null) SendDiagnostics(game, indexed, work, DiagnosticFailure.FromException(_stage, e), null);
            if (Checking()) SaveFailed(id, r => r.PlanCheckFailed = Planner.Version);   // not retried at every start: its page asks for a compile
            Stage(QueueStage.Failed, e is NotReadyException ? e.Message : CompileError(id, _stage, e));
        }
        finally
        {
            EndSaving();
            Finishing(false);
            // the cleanup, a log line of it included, never keeps the refresh and the reconcile (its anti-cheat check) from running
            try
            {
                if (_warmer is Warmer done) done.Ags = null;   // its closure holds this compile's whole index and recording
                // a throw outside the warm leaves it running, its staged exe holding files in work\
                IWarmRun? left;
                lock (_lock) left = _run;
                if (left is { Completion.IsCompleted: false })
                    try
                    {
                        left.Stop();
                        await left.Completion;
                    }
                    catch (Exception) { }   // the compile has failed already: the warm's outcome adds nothing
                KeepLastLog(work, id);
                if (!foldRan) Fold();
                // after the fold: while work\ is there, D3DSCacheGone doesn't judge the game's folder
                try { if (Directory.Exists(work)) Directory.Delete(work, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"could not delete {work}: {e.Message}"); }
            }
            finally
            {
                // a stop, before or during it, skips what waits for the recording lock (the next scan imports): no shutdown waits.
                // Anti-cheat it finds takes the recorder out all the same (AntiCheatFound).
                lock (_lock) _warming.Remove(id);
                // a done compile's refresh came while its warm still counted, so dbs the fold left pending didn't show
                void Closing()
                {
                    try { if (!refreshed || Store.LoadGame(id) is var folded && (folded.D3DSCacheDamaged || D3DSCachePending(id, folded))) Close(); }
                    finally { ApplyHeld(game, ct); }   // after the closing refresh, which judges the compile by what it read; also when it throws
                }
                bool gaveWay;
                lock (_lock) gaveWay = _yielding == id;
                if (!gaveWay) Closing();
                else _ = Task.Run(() =>   // a plan check that gave way: the user's compile doesn't wait for its refresh (seconds on a busy disk)
                {
                    try { Closing(); }
                    catch (Exception e) { Log?.Report($"{game.Name}: refreshing failed: {e.Message}"); }
                });
                StartSharing([game]);   // the content hash of this build is known now
                // the staged warm runs under the game's exe name, so a reconcile meanwhile waited for it
                if (ManageRecorders && !ct.IsCancellationRequested)
                    try { ReconcileRecorders(id); }
                    catch (Exception e) { Log?.Report($"{game.Name}: reconciling the recorder failed: {e.Message}"); }
            }
        }
    }

    /// <summary>A game's folder of staged runtime cache folders a compile couldn't put in the game's own D3DSCache folder.</summary>
    const string D3DSCacheWaiting = "d3dscache";

    /// <summary>The compile's runtime cache dbs that waited for the game's own D3DSCache folder go in once the game made it. A
    /// folder made before the game's first run goes into the game's own once that turned out another (a launcher starting the
    /// exe under another casing of its path): the dbs stay on disk once. True when <paramref name="halt"/> stopped it: what it
    /// hadn't merged stays pending.</summary>
    bool MergeD3DSCache(Game g, bool own = false, Action<long, long>? progress = null, Action<bool>? past = null, Func<bool>? halt = null)
    {
        if (!OperatingSystem.IsWindows()) return false;
        // a game that opens its db while a merge holds it runs without its cache until it exits: never while it runs
        if (CacheInUse(g))
        {
            Log?.Report($"{g.Name} is running: the compile's D3DSCache dbs go in when it exits");
            return false;
        }
        var shown = new Folding(g.Id, g.Name, progress != null);
        _folds.TryAdd(shown, 0);
        try
        {
            // after the add: a clear either sees this fold and waits for it, or this fold sees the clear
            return !Clearing(g.Id) && FoldD3DSCache(g, own, new FoldProgress((done, total) =>
            {
                progress?.Invoke(done, total);
                if (total > 0 && (int)(100 * done / total) is var p && p != shown.Percent)
                {
                    shown.Percent = p;
                    ScanProgressed?.Invoke();
                }
            }, over =>
            {
                shown.Writing = over;
                if (shown.Percent >= 0) ScanProgressed?.Invoke();
                past?.Invoke(over);
            }), halt);
        }
        finally
        {
            _folds.TryRemove(shown, out _);
            if (shown.Percent >= 0) ScanProgressed?.Invoke();
        }
    }

    bool FoldD3DSCache(Game g, bool own, FoldProgress fold, Func<bool>? halt)
    {
        var check = Stopwatch.StartNew();
        Stopwatch? started = null;
        bool cleared = false, halted = false, low = false;
        var root = Path.Combine(LocalAppData, "D3DSCache");
        string? volume = null;
        _foldRoom.TryRemove(g.Id, out _);
        var waiting = Path.Combine(Store.GameDir(g.Id), D3DSCacheWaiting);
        WaitMoved(g, waiting);   // before anything finds the new path's folder and repoints the record away from the old one
        // a running compile's warm may still write its staged folder: only its own fold takes them. The gate keeps a compile from
        // starting between the check and the clear, which would leave its folders unfolded. Another game's fold may hold it for
        // minutes: this game's start or clear doesn't wait for that.
        var gated = false;
        while (!(gated = _foldGate.TryEnter(Poll)) && !Stop()) { }
        if (gated)
            try
            {
                if (Store.LoadGame(g.Id).D3DSCacheSince is { } since && (own || !Warming(g.Id)) && FoldStaged(g, since, Stop, fold))
                    AppStore.Locked(Path.Combine(Store.GameDir(g.Id), "state.json"), () =>
                    {
                        var r = Store.LoadGame(g.Id);
                        if (r.D3DSCacheSince == since) r.D3DSCacheSince = null;
                        Store.SaveGame(g.Id, r);
                    });
            }
            finally { _foldGate.Exit(); }
        if (Stopped()) return halted;
        if (Store.LoadGame(g.Id).D3DSCacheMade is { } made && IsFolderName(made) && D3DSCache.GameFolder(root, g) is { } real
            && !string.Equals(Path.GetFileName(real.Dir), made, StringComparison.Ordinal) && Path.Combine(root, made) is var mine
            && (!Directory.Exists(mine) || !IsLink(mine) && D3DSCache.IsGames(mine, g) == true && FilesOf(mine) is var was
                && Merge(mine, real) && FilesOf(mine) == was && !was.Split('|').Any(f => f.Length > 0 && Opened(Path.Combine(mine, f.Split(':')[0])))))
        {
            var merged = Directory.Exists(mine);
            if (merged) Directory.Delete(mine, true);   // nothing wrote or opened it during the merge
            AppStore.Locked(Path.Combine(Store.GameDir(g.Id), "state.json"), () =>
            {
                var r = Store.LoadGame(g.Id);
                (r.D3DSCacheMade, r.D3DSCacheFolder, r.D3DSCacheExe) = (null, Path.GetFileName(real.Dir), real.Exe);
                if (merged && r.WarmedD3DSCacheBytes != null) r.WarmedD3DSCacheAt = DateTimeOffset.UtcNow;   // the game made its own db after the warm, which now holds the warm's
                Store.SaveGame(g.Id, r);
            });
        }
        if (Stopped()) return halted;
        if (!Directory.Exists(waiting) || IsLink(waiting) || !Directory.EnumerateDirectories(waiting).Any() || NoteLinked(g) != null
            || (GameD3DSCache(g) ?? NewGameD3DSCache(g)) is not { } to) return false;
        DropOtherCompilers(waiting, to.Dir);
        foreach (var d in Directory.GetDirectories(waiting))
        {
            string? why = null;
            if (!IsLink(d) && !d.EndsWith(Copying, StringComparison.Ordinal) && Merge(d, to, r => why = r))
            {
                var intact = File.Exists(Path.Combine(d, MovedIntact));
                Directory.Delete(d, true);
                if (intact)
                    AppStore.Locked(Path.Combine(Store.GameDir(g.Id), "state.json"), () =>
                    {
                        var r = Store.LoadGame(g.Id);
                        if (r.WarmedD3DSCacheBytes != null) r.WarmedD3DSCacheAt = DateTimeOffset.UtcNow;   // the new path's dbs, made after the warm, now hold the warm's
                        Store.SaveGame(g.Id, r);
                    });
                continue;
            }
            if (Stopped()) return halted;
            if (why != null) Log?.Report($"{g.Name}: the compile's D3DSCache dbs still wait in {d}: {why}");
            if (why != null && D3DSCache.NoRoom(why))
            {
                NoRoom(g, retry: true);
                continue;   // nothing went in: the folder stays intact
            }
            if (File.Exists(Path.Combine(d, MovedIntact)))
                // part of it may have gone in: what the new path's folder loses before the rest goes in must not pass as the warm's
                try { File.Delete(Path.Combine(d, MovedIntact)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{g.Name}: {d}: {e.Message}"); }
        }
        return false;

        bool Merge(string from, (string Dir, string Exe) to, Action<string>? refused = null)
        {
            fold.Add(D3DSCache.MergeWork(from, to.Dir, Store.LoadGame(g.Id).D3DSCacheChecked));
            var end = fold.Total;
            try { return MergeLogged(from, to, g, Stop, fold.Went, refused, fold.Past); }
            finally { fold.To(end); }
        }

        // checked at most every 250 ms: a merge step of a big db is far shorter than the game's start before its device
        bool Stop()
        {
            if (started == null && Clearing(g.Id)) (started, cleared) = (Stopwatch.StartNew(), true);
            if (started == null && halt?.Invoke() == true) (started, halted) = (Stopwatch.StartNew(), true);
            if (started == null && check.ElapsedMilliseconds >= 250)
            {
                check.Restart();
                if (CacheInUse(g)) started = Stopwatch.StartNew();
                else if (D3DSCache.FreeBytes(volume ??= RoomDir(g)) < D3DSCache.DiskFloor) (started, low) = (Stopwatch.StartNew(), true);
            }
            return started != null;
        }

        bool Stopped()
        {
            if (started == null) return false;
            Log?.Report(cleared ? $"{g.Name}: Clear cache stopped the D3DSCache fold, its db released {started.ElapsedMilliseconds} ms after"
                : halted ? $"{g.Name}: the D3DSCache fold stopped with the queue, its db released {started.ElapsedMilliseconds} ms after"
                : low ? $"{g.Name}: the D3DSCache fold stopped below {Format.Bytes(D3DSCache.DiskFloor)} free on {D3DSCache.Drive(volume ?? root)}, its db released {started.ElapsedMilliseconds} ms after"
                : $"{g.Name} started during the D3DSCache fold: stopped, its db released {started.ElapsedMilliseconds} ms after, the rest goes in when it exits");
            if (low) NoRoom(g, retry: false);   // the poll folding again would fill the disk again
            return true;
        }
    }

    /// <summary>The staged warms' own runtime cache folders made since <paramref name="since"/>, also a killed compile's, go
    /// into the game's folder, else they wait for it in the game's <see cref="D3DSCacheWaiting"/>. False when one stays in
    /// D3DSCache: tried again at the app's next start and the game's exit. <paramref name="progress"/> gets their merges' units.</summary>
    bool FoldStaged(Game g, DateTime since, Func<bool> stop, FoldProgress? progress = null)
    {
        var root = Path.Combine(LocalAppData, "D3DSCache");
        var waiting = Path.Combine(Store.GameDir(g.Id), D3DSCacheWaiting);
        var doubt = new List<string>();
        var made = D3DSCache.Made(root, WorkDirs(Store.GameDir(g.Id)), since, doubt: doubt, game: g);
        // a folder whose db can't be read now may be one of them
        var all = doubt.Count == 0;
        foreach (var d in doubt) Log?.Report($"{g.Name}: {d} couldn't be read, tried again at the next start or game exit");
        if (made.Count > 0 && NoteLinked(g) is { } linkWhy)
        {
            Log?.Report($"{g.Name}: its D3DSCache folder {linkWhy}, which SCSKiller doesn't merge into: the compile's dbs stay in {string.Join(", ", made.Select(Path.GetFileName))}");
            return false;
        }
        if (made.Count > 0 && D3DSCache.Delivered(g.ExePath) is { } asd)
            Log?.Report($"{g.Name}: Shaders delivered by Advanced Shader Delivery: {asd.Bytes / (1024 * 1024)} MB, the compile's dbs take its identity");
        var game = progress == null ? null : GameD3DSCache(g)?.Dir;
        var units = made.ToDictionary(d => d, d => progress == null ? 0 : D3DSCache.MergeWork(d, game, Store.LoadGame(g.Id).D3DSCacheChecked));
        if (made.Count > 0) progress?.Add(units.Values.Sum());
        var end = progress?.Done ?? 0;
        foreach (var d in made)
        {
            string? why = null;
            try
            {
                var start = end;
                end += units[d];
                if (IsLink(d))
                {
                    all = false;   // a link may lead anywhere
                    Log?.Report($"{g.Name}: {d} kept: it is a link");
                    continue;
                }
                var timer = Stopwatch.StartNew();
                long added = 0;
                var to = GameD3DSCache(g) ?? NewGameD3DSCache(g);
                var known = to != null && RefusedBefore(d, to.Value, g) != null;
                if (to != null && MergeStamped(d, to.Value, g, n => added += n, stop, r => why = r,
                        progress?.Share(start, units[d], D3DSCache.MergeWork(d, to.Value.Dir, Store.LoadGame(g.Id).D3DSCacheChecked)), progress?.Past))
                {
                    Directory.Delete(d, true);
                    Log?.Report($"{g.Name}: the compile's D3DSCache dbs went into {Path.GetFileName(to.Value.Dir)}, {added} keys added ({timer.ElapsedMilliseconds} ms)");
                    continue;
                }
                why ??= "the game's own folder isn't found";
                if (stop()) return false;   // left where it is: D3DSCacheSince keeps it pending
                if (D3DSCache.NoRoom(why))
                {
                    all = false;   // never moved to the waiting folder: on another volume that is a copy
                    NoRoom(g, retry: true);
                    Log?.Report($"{g.Name}: {d} kept, tried again once there is room: {why}");
                    continue;
                }
                if (D3DSCache.CacheFiles(d).FirstOrDefault(f => Opened(f.FullName)) is { } open)
                {
                    all = false;   // a folder with a file open in it can't move
                    Log?.Report($"{g.Name}: {d} kept, tried again at the next start or game exit: {open.Name} is open ({why})");
                    continue;
                }
                // a check reads the whole db: only before a first wait, or once per refusal of the compile's db
                string? unread = null;
                var lost = !known && (to == null || D3DSCache.OwnDbRefusal(why)) ? D3DSCache.Damaged(d, out unread) : [];
                if (unread != null)
                {
                    all = false;
                    Log?.Report($"{g.Name}: {d} kept, tried again at the next start or game exit: {unread} ({why})");
                    continue;
                }
                if (lost.Count > 0)
                {
                    // what the compile wrote there is lost: the game is compiled again, cold. Recorded before the dbs go.
                    AppStore.Locked(Path.Combine(Store.GameDir(g.Id), "state.json"), () =>
                    {
                        var r = Store.LoadGame(g.Id);
                        (r.D3DSCacheDamaged, r.WarmedDriverId, r.StoppedCreated) = (true, LostD3DSCache, null);
                        Store.SaveGame(g.Id, r);
                    });
                    foreach (var f in lost.SelectMany(f => new[] { f, f + "-wal", f + "-shm" })) File.Delete(f);
                    Log?.Report($"{g.Name}: the compile's D3DSCache db {string.Join(", ", lost.Select(Path.GetFileName))} in {Path.GetFileName(d)} is damaged, so it didn't reach the game: compiling again");
                    if (!Directory.EnumerateFileSystemEntries(d).Any())
                    {
                        Directory.Delete(d);
                        continue;
                    }
                }
                if (!IsLink(waiting))
                {
                    MoveFolder(d, Path.Combine(Directory.CreateDirectory(waiting).FullName, Path.GetFileName(d)));
                    Log?.Report($"{g.Name}: the compile's D3DSCache dbs wait in {waiting}: {why}");
                }
                else
                {
                    all = false;
                    Log?.Report($"{g.Name}: {d} kept: {waiting} is a link");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                all = false;
                Log?.Report($"{g.Name}: {d} kept, tried again at the next start or game exit: {e.Message}{(why == null ? "" : $" ({why})")}");
            }
            finally { progress?.To(end); }
        }
        return all;
    }

    /// <summary>Bookkeeping shorter than this never shows as its own step, but right after Saving, which it replaces.</summary>
    static readonly TimeSpan FinishingAfter = TimeSpan.FromSeconds(2);

    static string Elapsed(TimeSpan t) => t.TotalMinutes < 1 ? $"{(int)t.TotalSeconds} s" : $"{(int)t.TotalMinutes} min {t.Seconds:00} s";

    /// <summary>A step without a share to show: <paramref name="tick"/> gets its elapsed time every second from <paramref name="first"/> on.</summary>
    sealed class TimedPhase : IDisposable
    {
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Timer timer;
        public TimeSpan Elapsed => clock.Elapsed;
        public TimedPhase(Action<TimeSpan> tick, TimeSpan first) => timer = new Timer(_ => tick(clock.Elapsed), null, first, TimeSpan.FromSeconds(1));
        public void Dispose()   // no tick after it returns
        {
            using var gone = new ManualResetEvent(false);
            if (timer.Dispose(gone)) gone.WaitOne();
            clock.Stop();
        }
    }

    /// <summary>A D3DSCache fold's (done, total) in <see cref="D3DSCache.MergeWork"/> units over its merges. A merge that ends early
    /// counts its units as done: the share and the time left are of what is still to go.</summary>
    sealed class FoldProgress(Action<long, long> report, Action<bool>? past = null)
    {
        public readonly Action<bool>? Past = past;
        long done;
        public long Done => done;
        public long Total { get; private set; }
        public void Add(long units) { Total += units; report(done, Total); }
        public void Went(long units) => To(done + units);
        public void To(long at) { if (Math.Min(at, Total) is var to && to > done) report(done = to, Total); }

        /// <summary>A merge's own units (<paramref name="work"/>, read as it starts) scaled into the <paramref name="share"/> it was
        /// given from <paramref name="at"/>: an earlier folder's db that went in makes this one's merge bigger.</summary>
        public Action<long> Share(long at, long share, long work)
        {
            long went = 0;
            return n => To(at + (long)(share * Math.Min(1.0, (went += n) / (double)Math.Max(1, work))));
        }
    }

    readonly HashSet<string> _warming = [];   // under _lock: games whose warm may still write a staged folder, until its fold
    readonly Lock _foldGate = new();

    /// <summary>A running <see cref="MergeD3DSCache"/>: its share done once it has work, -1 before. Queued: the compile's own,
    /// which its queue item shows.</summary>
    sealed class Folding(string id, string name, bool queued)
    {
        public readonly string Id = id, Name = name;
        public readonly bool Queued = queued;
        public volatile int Percent = -1;
        public volatile bool Writing;   // a step outran its estimate: Percent stands still until it ends
    }

    readonly ConcurrentDictionary<Folding, byte> _folds = new();
    readonly Dictionary<string, int> _clearing = [];   // under itself: the clears running per game, which keep its folds out

    bool Clearing(string id) { lock (_clearing) return _clearing.ContainsKey(id); }

    public const string FoldingNote = "SCSKiller is handing the last compile to the game's cache";

    /// <summary>The game page's line while a fold the queue doesn't show runs for the game; null = none.</summary>
    public string? FoldLine(string gameId) =>
        _folds.Keys.FirstOrDefault(f => f.Id == gameId && !f.Queued && f.Percent >= 0) is { } f ? FoldText("the game's", f.Percent, f.Writing)
        : _foldRoom.TryGetValue(gameId, out var room) ? $"Waiting for {Format.Bytes(room.Need)} free on {D3DSCache.Drive(room.Dir)} to hand the compile to the game's cache" : null;

    /// <summary>Folds that stopped for free space, per game. Retry: refused before writing, so the poll folds again once the
    /// volume has Need free. Else the next start or game exit does.</summary>
    readonly ConcurrentDictionary<string, (string Dir, long Need, bool Retry)> _foldRoom = new();

    /// <summary>Where a merge writes: the game's D3DSCache folder, a link followed, else the D3DSCache root.</summary>
    string RoomDir(Game g) => GameD3DSCache(g)?.Dir is { } d && D3DSCache.Followed(d, out _) is { } real ? real : Path.Combine(LocalAppData, "D3DSCache");

    /// <summary>The game's pending staged folders need room: the biggest one's merge (<see cref="D3DSCache.RoomFor"/>).</summary>
    void NoRoom(Game g, bool retry)
    {
        var root = Path.Combine(LocalAppData, "D3DSCache");
        var volume = RoomDir(g);
        var waiting = Path.Combine(Store.GameDir(g.Id), D3DSCacheWaiting);
        var dirs = D3DSCache.Made(root, WorkDirs(Store.GameDir(g.Id)), Store.LoadGame(g.Id).D3DSCacheSince ?? DateTime.MaxValue, game: g)
            .Concat(Directory.Exists(waiting) ? Directory.GetDirectories(waiting) : []);
        _foldRoom[g.Id] = (volume, dirs.Select(D3DSCache.RoomFor).DefaultIfEmpty(D3DSCache.DiskFloor).Max(), retry);
    }

    public static string FoldText(string whose, int percent, bool writing = false) =>
        writing ? $"Finishing: writing into {whose} cache" : $"Handing the compile to {whose} cache: {percent}%";

    /// <summary>Stops the game's D3DSCache folds as its start does, and keeps new ones out until disposed: a clear deletes their dbs.</summary>
    IDisposable StopFolds(Game g)
    {
        lock (_clearing) _clearing[g.Id] = _clearing.GetValueOrDefault(g.Id) + 1;
        var gone = new Released(() => { lock (_clearing) if (--_clearing[g.Id] == 0) _clearing.Remove(g.Id); });
        // a fold stops within a SQLite step, but a staged db's damage check or a copy to another volume runs to its end
        var wait = Stopwatch.StartNew();
        while (_folds.Keys.Any(f => f.Id == g.Id))
        {
            if (wait.Elapsed.TotalSeconds >= 10)
            {
                gone.Dispose();
                throw new InvalidOperationException(FoldingNote);
            }
            Thread.Sleep(50);
        }
        return gone;
    }

    sealed class Released(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    bool Warming(string id) { lock (_lock) return _warming.Contains(id); }

    /// <summary>The compile's dbs aren't all in the game's own D3DSCache folder: some wait for it, or a staged folder is left
    /// (a compile killed before its fold, a move that failed), not counting a warm's own until its fold.</summary>
    bool D3DSCachePending(string gameId, GameRecord r) => OperatingSystem.IsWindows()
        && (r.D3DSCacheSince != null && !Warming(gameId) || Holds(Path.Combine(Store.GameDir(gameId), D3DSCacheWaiting), "*", dirs: true) == true);

    /// <summary>The game's staged folders a fold still waits to take (<see cref="GameRecord.D3DSCacheSince"/>), matched as the fold matches them,
    /// with its raw read. <paramref name="doubt"/> gets the new folders with a db whose owner can't be read; one whose bytes name only this game's
    /// staged exes is among the game's, as the fold takes it.</summary>
    IReadOnlyList<string> PendingStaged(Game g, GameRecord r, ICollection<string>? doubt = null) => OperatingSystem.IsWindows() && r.D3DSCacheSince is { } since && !Warming(g.Id)
        ? [.. D3DSCache.Made(Path.Combine(LocalAppData, "D3DSCache"), WorkDirs(Store.GameDir(g.Id)), since, doubt: doubt ?? [], game: g).Where(d => !IsLink(d))] : [];   // a link may lead anywhere

    /// <summary>1.3.1 saved a complete warm before its fold and kept no pending mark, so a fold cut short left that warm's staged
    /// folders in D3DSCache, where the game never reads them. Once per such warm (no fold since recorded its dbs): the folders
    /// its own work dirs' exes made since it started get <see cref="GameRecord.D3DSCacheSince"/>, and the fold takes them.</summary>
    bool AdoptStranded(Game g)
    {
        if (!OperatingSystem.IsWindows() || AppCache is not NvidiaAppCache || Warming(g.Id)) return false;
        if (Store.LoadGame(g.Id) is not { WarmedD3DSCache: true, D3DSCacheSince: null, WarmedD3DSCacheBytes: null, D3DSCacheStrandChecked: false,
                WarmedAt: { } at, LastWarmTime: { } took }) return false;
        var doubt = new List<string>();
        // a few minutes before the warm's clock: the plan's last steps; an earlier compile's folders are hours older
        var cutoff = (at - took - TimeSpan.FromMinutes(10)).UtcDateTime;
        var made = D3DSCache.Made(Path.Combine(LocalAppData, "D3DSCache"), WorkDirs(Store.GameDir(g.Id)), cutoff, doubt: doubt, game: g);
        if (made.Count == 0 && doubt.Count > 0) return false;   // tried again at the next start; with some found, the fold retries the rest
        var since = made.Count > 0 ? cutoff : (DateTime?)null;
        var adopted = false;
        lock (_foldGate)
            AppStore.Locked(Path.Combine(Store.GameDir(g.Id), "state.json"), () =>
            {
                var r = Store.LoadGame(g.Id);
                if (r.WarmedAt != at || r.D3DSCacheSince != null || Warming(g.Id)) return;   // a compile since: its own fold decides
                (r.D3DSCacheSince, r.D3DSCacheStrandChecked, adopted) = (since, true, since != null);
                Store.SaveGame(g.Id, r);
            });
        if (adopted) Log?.Report($"{g.Name}: the compile of {at.ToUniversalTime():yyyy-MM-dd HH:mm}Z left {made.Count} staged D3DSCache folder(s) outside the game's folder: they go in now");
        return adopted;
    }

    /// <summary>A moved install's old folder (<see cref="MovedD3DSCache"/>) into the waiting folder, whose merge puts it into the
    /// new path's: when its dbs name only the old exe and none is open. One process at a time.</summary>
    void WaitMoved(Game g, string waiting)
    {
        try
        {
            AppStore.Locked(Path.Combine(Store.GameDir(g.Id), "d3dscache-move"), () =>
            {
                var moving = Store.LoadGame(g.Id);
                if (MovedD3DSCache(g, moving) is not { } moved || D3DSCache.ExePaths(moved) is not { Count: 1 } named
                    || !named.Single().Equals(moving.D3DSCacheExe, StringComparison.OrdinalIgnoreCase) || IsLink(waiting)
                    || D3DSCache.CacheFiles(moved).Any(f => Opened(f.FullName))) return;
                var name = Path.GetFileName(moved);
                // a copy to another volume that failed: only this gate's holder writes this name
                if (Directory.Exists(waiting))
                    foreach (var d in Directory.GetDirectories(waiting, "*" + name + "*" + Copying)) Directory.Delete(d, true);
                // a trim found before the move stays found: only a folder known intact counts as the warm's once merged
                var intact = NoCompile(Path.Combine(Store.GameDir(g.Id), "work")) && D3DSCacheTrimmed(g.Id, moving) == null;
                var to = MoveFolder(moved, Path.Combine(Directory.CreateDirectory(waiting).FullName, name));
                AppStore.Locked(Path.Combine(Store.GameDir(g.Id), "state.json"), () =>
                {
                    var r = Store.LoadGame(g.Id);
                    if (r.D3DSCacheFolder == name) (r.D3DSCacheFolder, r.D3DSCacheExe) = (null, null);
                    Store.SaveGame(g.Id, r);
                });
                if (intact) File.WriteAllBytes(Path.Combine(to, MovedIntact), []);
                Log?.Report($"{g.Name}: its exe moved, the compile's D3DSCache dbs from {moved} go into the new path's folder");
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log?.Report($"{g.Name}: its exe moved, the old D3DSCache folder stays for now: {e.Message}");
        }
    }

    /// <summary>The game's exe moved (a store moved its install): the folder on record, named for the old path, which is gone,
    /// still holds dbs, and the runtime opens the new path's folder. Xbox paths change with each update. No db is read.</summary>
    string? MovedD3DSCache(Game g, GameRecord r) => OperatingSystem.IsWindows() && g.Store != Core.Store.Xbox
        && r is { D3DSCacheFolder: { } name, D3DSCacheExe: { } old } && IsFolderName(name) && LaunchExe(g) is { } exe
        && Path.GetFileName(old).Equals(Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase) && D3DSCache.FolderName(exe) != name
        && !File.Exists(old) && File.Exists(exe) && Path.Combine(LocalAppData, "D3DSCache", name) is var dir && !IsLink(dir)
        && Holds(dir, "*.dxcache", dirs: false) == true ? dir : null;

    public const string D3DSCachePendingReason = "the compiled pipelines go into the game's cache when the game exits or SCSKiller starts";
    public const string D3DSCacheDamagedReason = "the compile's Windows shader cache was damaged, so compile it again";
    public const string D3DSCacheLinkedReason = "the game's shader cache folder ";

    /// <summary>The status reasons the game page shows as Not reached yet.</summary>
    public static bool NotReached(string? reason) => reason is D3DSCachePendingReason or D3DSCacheDamagedReason
        || reason?.StartsWith(D3DSCacheLinkedReason, StringComparison.Ordinal) == true;

    /// <summary>MergeD3DSCache off the caller's thread, then the game's status again.</summary>
    void MergeD3DSCacheSoon(Game g) => _ = Task.Run(() =>
    {
        try
        {
            MergeD3DSCache(g);
            RefreshGame(g.Id);
        }
        catch (Exception e) { Log?.Report($"{g.Name}: the compile's D3DSCache dbs not merged: {e.Message}"); }
    });

    /// <summary>NVIDIA from 621 keeps compiled pipelines in D3DSCache, whose fsyncs under its writer lock cap a compile:
    /// the warm skips them while it creates (<see cref="WarmOptions.D3DSCacheNoFlush"/>).</summary>
    bool SkipD3DSCacheFlush() => KeepsD3DSCache();

    /// <summary>NVIDIA from 621 keeps compiled pipelines in D3DSCache, beside its own cache.</summary>
    bool KeepsD3DSCache() => OperatingSystem.IsWindows() && OwnDriver == GpuVendor.Nvidia && NvidiaMajor(Vendor.Gpu.DriverVersion) >= 621;

    bool MergeLogged(string from, (string Dir, string Exe) to, Game g, Func<bool> stop, Action<long>? progress = null, Action<string>? refused = null,
        Action<bool>? past = null)
    {
        long added = 0;
        var timer = Stopwatch.StartNew();
        if (!MergeStamped(from, to, g, n => added += n, stop, refused, progress, past)) return false;
        Log?.Report($"{g.Name}: D3DSCache dbs went into {Path.GetFileName(to.Dir)}, {added} keys added ({timer.ElapsedMilliseconds} ms)");
        return true;
    }

    /// <summary><see cref="D3DSCache.MergeInto"/> with the game's package family, its identity decision kept for the outcome
    /// report when an adapter db went in or all stayed out for it.</summary>
    bool MergeStamped(string from, (string Dir, string Exe) to, Game g, Action<long> added, Func<bool> stop, Action<string>? refused = null,
        Action<long>? progress = null, Action<bool>? past = null)
    {
        if (RefusedBefore(from, to, g) is { } before)
        {
            refused?.Invoke(before);
            return false;
        }
        IdentityStamp? decided = null;
        var package = g.Store == Core.Store.Xbox && g.Id.StartsWith("xbox:", StringComparison.Ordinal) ? g.Id["xbox:".Length..] : null;
        var was = Store.LoadGame(g.Id).D3DSCacheChecked;
        var checkedDbs = new Dictionary<string, string>(was ?? [], StringComparer.OrdinalIgnoreCase);
        var (whys, seen) = (new List<string>(), Refusal(from, to));
        // the target, never the link: whether the db can be renamed in is decided by its real volume
        if (D3DSCache.Followed(to.Dir, out var linkWhy) is not { } dir)
        {
            refused?.Invoke($"the game's folder {linkWhy}");
            return false;
        }
        var ok = D3DSCache.MergeInto(from, dir, to.Exe, added, stop, r => { if (whys.Count == 0 || D3DSCache.NoRoom(r)) refused?.Invoke(r); whys.Add(r); }, package, s => decided = s, progress, checkedDbs, past);
        // remembered until a db of either folder changes: a check that refused reads the whole db again for the same answer
        var refusal = !ok && whys.Count > 0 && whys.All(D3DSCache.Lasting) && !stop() && Refusal(from, to) == seen ? $"{seen}\n{whys[0]}" : null;
        if (refusal != null || Store.LoadGame(g.Id).D3DSCacheRefused?.ContainsKey(from) == true)
            AppStore.Locked(Path.Combine(Store.GameDir(g.Id), "state.json"), () =>
            {
                var r = Store.LoadGame(g.Id);
                var kept = (r.D3DSCacheRefused ?? []).Where(e => !e.Key.Equals(from, StringComparison.OrdinalIgnoreCase) && Directory.Exists(e.Key)).ToDictionary(StringComparer.OrdinalIgnoreCase);
                if (refusal != null) kept[from] = refusal;
                r.D3DSCacheRefused = kept.Count > 0 ? kept : null;
                Store.SaveGame(g.Id, r);
            });
        if (decided?.Source == IdentityStamp.ByRule) Log?.Report($"{g.Name}: the compile's D3DSCache dbs took the identity of rule {decided.Rule}");
        var changed = was == null ? checkedDbs.Count > 0 : was.Count != checkedDbs.Count || was.Any(e => checkedDbs.GetValueOrDefault(e.Key) != e.Value);
        if (decided == null && !changed) return ok;
        AppStore.Locked(Path.Combine(Store.GameDir(g.Id), "state.json"), () =>
        {
            var r = Store.LoadGame(g.Id);
            if (decided != null) (r.D3DSCacheStamp, r.D3DSCacheStampAt, r.D3DSCacheStampFolder) = (decided, DateTimeOffset.UtcNow, Path.GetFileName(to.Dir));
            // only what this merge changed, and only entries no other process's fold changed meanwhile, except a full check
            // asked for again, which always lands
            var into = new Dictionary<string, string>(r.D3DSCacheChecked ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var key in (was?.Keys ?? Enumerable.Empty<string>()).Concat(checkedDbs.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
                if (checkedDbs.GetValueOrDefault(key) is var now && now != was?.GetValueOrDefault(key)
                    && (now?.StartsWith(D3DSCache.Again, StringComparison.Ordinal) == true || into.GetValueOrDefault(key) == was?.GetValueOrDefault(key)))
                    if (now == null) into.Remove(key);
                    else into[key] = now;
            r.D3DSCacheChecked = into.Count > 0 ? into : null;
            Store.SaveGame(g.Id, r);
        });
        return ok;
    }

    /// <summary>Why <see cref="MergeStamped"/> refused <paramref name="from"/> into <paramref name="to"/>, when no db of either changed
    /// since; null otherwise.</summary>
    string? RefusedBefore(string from, (string Dir, string Exe) to, Game g) =>
        Store.LoadGame(g.Id).D3DSCacheRefused?.GetValueOrDefault(from) is { } r && r.Split('\n', 2) is [var at, var why] && at == Refusal(from, to) ? why : null;

    static string Refusal(string from, (string Dir, string Exe) to) => $"{to.Exe}|{DbStamp(from)}>{DbStamp(to.Dir)}";

    /// <summary>The folder's dbs and WALs, each with its length and write time.</summary>
    static string DbStamp(string dir)
    {
        try
        {
            // a reader's open leaves an empty WAL with a new write time behind
            return string.Join('|', new DirectoryInfo(dir).GetFiles("*.dxcache*").Where(f => f.Name.EndsWith(".dxcache", StringComparison.OrdinalIgnoreCase)
                    || f.Name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) && f.Length > 0)
                .OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "?" + Guid.NewGuid(); }   // never the same twice
    }

    /// <summary>Why the fold doesn't follow the D3DSCache folder the runtime opens for the game's exe, a link (a user's junction to
    /// another drive), when the game's own folder isn't found; null when it is, or that folder isn't a link. Kept for the game page.</summary>
    string? NoteLinked(Game g)
    {
        string? why = null;
        if (GameD3DSCache(g) == null && OperatingSystem.IsWindows() && LaunchExe(g) is { } exe
            && Path.Combine(LocalAppData, "D3DSCache", D3DSCache.FolderName(exe)) is var dir && IsLink(dir))
            why = D3DSCache.Followed(dir, out var refused) is not { } real ? refused
                : InDataDir(real) ? "links into SCSKiller's data folder"
                : D3DSCache.IsGames(dir, g) == false ? "links to another game's cache" : "links to a folder SCSKiller can't tell is the game's";
        if (Store.LoadGame(g.Id).D3DSCacheLinked != why)
            AppStore.Locked(Path.Combine(Store.GameDir(g.Id), "state.json"), () =>
            {
                var r = Store.LoadGame(g.Id);
                r.D3DSCacheLinked = why;
                Store.SaveGame(g.Id, r);
            });
        return why;
    }

    /// <summary>In the data folder, by its path or its final path: a link there may lead to a waiting folder, which the fold
    /// deletes once merged.</summary>
    bool InDataDir(string path) => GameFiles.Inside(Store.DataDir, path) || GameFiles.FinalPath(Store.DataDir) is { } real && GameFiles.Inside(real, path);

    /// <summary>In the data folder: the driver (<see cref="DriverId"/>) and the compiler a staged db of our warm on it names.</summary>
    const string CompilerFile = "d3dscache-compiler.txt";

    void KeepCompiler(string driver, string compiler)
    {
        if (driver.Contains('\n') || compiler.Contains('\n')) return;
        try { ContentFile.Keep(Path.Combine(Store.DataDir, CompilerFile), driver + "\n" + compiler); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // known after the next warm
    }

    /// <summary>The compiler this driver writes, once a warm on it was folded; null before.</summary>
    string? OwnCompiler()
    {
        try { return File.ReadAllText(Path.Combine(Store.DataDir, CompilerFile)).Split('\n') is [var d, var c] && d == DriverId && c.Length > 0 ? c : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The game's Advanced Shader Delivery fits this driver (<see cref="D3DSCache.Fits"/>); null: not known, or none.</summary>
    bool? DeliveryFitsOf(Game g) => OperatingSystem.IsWindows() && D3DSCache.Delivered(g.ExePath) is { } d ? D3DSCache.Fits(d, OwnCompiler()) : null;

    /// <summary>The library's and game page's tag of a game with delivered shaders; null = none.</summary>
    public static string? DeliveryChip(GameState s) => s.DeliveredShaders is { } b ? $"Advanced Shader Delivery {Format.Bytes(b)}" : null;

    public const string DeliveryTip = "The size of the compiled shaders Windows delivered for this game";

    /// <summary>The game page's and the command line's line under the status, when a delivered db fits this driver; null = none.</summary>
    public static string? DeliveryLine(GameState s) => s is { DeliveredShaders: not null, DeliveryFits: true }
        ? "Windows already delivered part of this game's compiled shaders. SCSKiller compiles the rest." : null;

    /// <summary>The game page's and the command line's line under a cache-gone status whose D3DSCache dbs Windows' automatic
    /// cleanup may have emptied (<see cref="GameState.D3DSCacheCleanup"/>); null = none.</summary>
    public static string? CleanupLine(GameState s) => s is { Status: GameStatus.Stale, D3DSCacheCleanup: true }
        && (s.StatusReason.StartsWith(TrimmedPartReason, StringComparison.Ordinal) || s.StatusReason.StartsWith(TrimmedAllReason, StringComparison.Ordinal))
        ? "Windows' automatic disk cleanup is on and can remove compiled shaders" : null;

    /// <summary>The game's own D3DSCache folder and the exe path its dbs name, remembered: emptied by Clear cache, the
    /// runtime opens the same folder again (its name comes from the exe path), so the compile's dbs go back there.</summary>
    (string Dir, string Exe)? GameD3DSCache(Game g)
    {
        var root = Path.Combine(LocalAppData, "D3DSCache");
        var rec = Store.LoadGame(g.Id);
        if (D3DSCache.GameFolder(root, g) is { } found && D3DSCache.Followed(found.Dir, out _) is { } real && !InDataDir(real))
        {
            // a moved install's old folder stays on record until its dbs went to the waiting folder
            if ((rec.D3DSCacheFolder != Path.GetFileName(found.Dir) || rec.D3DSCacheExe != found.Exe) && MovedD3DSCache(g, rec) == null)
                AppStore.Locked(Path.Combine(Store.GameDir(g.Id), "state.json"), () =>
                {
                    var r = Store.LoadGame(g.Id);
                    (r.D3DSCacheFolder, r.D3DSCacheExe) = (Path.GetFileName(found.Dir), found.Exe);
                    Store.SaveGame(g.Id, r);
                });
            return found;
        }
        // a folder holding any db isn't emptied: another exe's, or one in doubt
        if (rec is { D3DSCacheFolder: { } name, D3DSCacheExe: { } exe } && IsFolderName(name)
            && D3DSCache.IsGameExe(exe, g) && File.Exists(exe) && Path.Combine(root, name) is var dir
            && (!Directory.Exists(dir) || D3DSCache.Followed(dir, out _) is { } emptied && !InDataDir(emptied)
                && !Directory.EnumerateFiles(emptied, "*.dxcache*").Any()))
            return (Directory.CreateDirectory(dir).FullName, exe);
        return null;
    }

    /// <summary>The folder the runtime will open for the game's exe, made empty before its first run so the compile's dbs are
    /// there when it starts, and remembered: a launch under another casing of the path gets another folder, which
    /// MergeD3DSCache folds this one into at the exit. Null when that folder exists already (another exe's, or in doubt).</summary>
    (string Dir, string Exe)? NewGameD3DSCache(Game g)
    {
        if (LaunchExe(g) is not { } exe || !File.Exists(exe)) return null;
        var name = D3DSCache.FolderName(exe);
        var dir = Path.Combine(LocalAppData, "D3DSCache", name);
        if (Directory.Exists(dir) && (IsLink(dir) || Directory.EnumerateFileSystemEntries(dir).Any())) return null;
        Directory.CreateDirectory(dir);
        AppStore.Locked(Path.Combine(Store.GameDir(g.Id), "state.json"), () =>
        {
            var r = Store.LoadGame(g.Id);
            r.D3DSCacheMade = name;
            Store.SaveGame(g.Id, r);
        });
        return (dir, exe);
    }

    static string FilesOf(string dir) => string.Join('|', new DirectoryInfo(dir).GetFiles().OrderBy(f => f.Name, StringComparer.Ordinal)
        .Select(f => $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"));

    static bool Opened(string file)
    {
        try { using var _ = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }

    static bool IsFolderName(string name) => name.Length is > 0 and <= 16 && name.All(char.IsAsciiHexDigitLower);

    /// <summary>Waiting adapter dbs of an earlier driver's compiler, where <paramref name="dir"/>'s name this driver's: no driver reads
    /// them. The other dbs beside them stay. None while this driver's compiler isn't known: a game's db that names an earlier one
    /// waits for the game's next run.</summary>
    void DropOtherCompilers(string waiting, string dir)
    {
        if (!Directory.Exists(waiting) || IsLink(waiting) || OwnCompiler() is not { } current) return;
        foreach (var d in Directory.GetDirectories(waiting))
            if (!IsLink(d) && !d.EndsWith(Copying, StringComparison.Ordinal) && D3DSCache.OtherCompiler(d, dir, current) is { Count: > 0 } old)
            {
                foreach (var f in old.SelectMany(f => new[] { f, f + "-wal", f + "-shm" })) File.Delete(f);
                if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            }
    }

    static bool IsLink(string dir) => Directory.Exists(dir) && new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint);

    const string Copying = ".copying";

    /// <summary>In a moved install's folder in the waiting folder: it had lost nothing when it moved, and no merge of it failed.</summary>
    const string MovedIntact = "scskiller-moved-intact";

    /// <summary>A staged folder into the waiting one, a copy then a delete across volumes (a data folder on another drive): where it went. An
    /// earlier compile's under the same name stays beside it: an incremental compile leaves out what that one created.</summary>
    static string MoveFolder(string from, string to)
    {
        var name = to;
        for (var n = 2; Directory.Exists(to) || Directory.Exists(to + Copying); n++) to = $"{name}-{n}";
        if (string.Equals(Path.GetPathRoot(Path.GetFullPath(from)), Path.GetPathRoot(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(from, to);
            return to;
        }
        var tmp = Directory.CreateDirectory(to + Copying).FullName;   // never merged until whole
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(tmp, Path.GetFileName(f)));
        Directory.Move(tmp, to);
        Directory.Delete(from, true);
        return to;
    }

    /// <summary>A compile of the game's listed build failed or was refused, a plan check's aside: Rate this compile opens without a
    /// launch, for "The game could not be compiled."</summary>
    void NoteFailed(string id, Game? compiled)
    {
        bool planCheck;
        lock (_lock) planCheck = _queue.FirstOrDefault(q => q.GameId == id)?.PlanCheck == true;
        if (planCheck || (compiled ?? Games.FirstOrDefault(s => s.Game.Id == id)?.Game) is not { } g) return;
        var rec = Store.LoadGame(id);
        rec.CompileFailedBuild = g.Version ?? "";
        Store.SaveGame(id, rec);
    }

    /// <summary>A verdict found mid-compile (a mod that blocks it), not an error: no compile-error.log.</summary>
    sealed class NotReadyException(string message) : InvalidOperationException(message);

    /// <summary>A failed compile's error: the stage it failed in, with the whole exception (its stack is nowhere else) kept in
    /// compile-error.log beside warm-failed.log for a bug report; the next failure overwrites it.</summary>
    string CompileError(string id, QueueStage stage, Exception e)
    {
        while (e is AggregateException { InnerExceptions.Count: 1 } a) e = a.InnerExceptions[0];
        var codec = Codecs.Reason(e);
        Log?.Report(codec != null ? $"{id}: {stage} failed: {e}" : $"{id}: {stage} failed: {e.GetType().Name}: {e.Message}");
        var error = codec ?? $"{(stage is QueueStage.Indexing or QueueStage.Planning or QueueStage.Materializing or QueueStage.Warming ? $"{stage} failed: " : "")}{e.Message}";
        var kept = Path.Combine(Store.GameDir(id), "compile-error.log");
        try { AppStore.WriteAtomic(kept, System.Text.Encoding.UTF8.GetBytes($"{DateTimeOffset.Now:O}\r\nSCSKiller {AppVersion.Current}\r\nstage: {stage}\r\n\r\n{e}\r\n")); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { return error; }
        return codec ?? $"{error} (log: {kept})";
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

    /// <summary>A completed warm's: an earlier failure's warm-failed.log no longer describes the game.</summary>
    void DropFailedLog(string id)
    {
        try { File.Delete(Path.Combine(Store.GameDir(id), "warm-failed.log")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"removing the last failed warm's log failed: {e.Message}"); }
    }

    /// <summary>With Settings.ShareDiagnostics on, a compile the driver rejected pipelines in, whose warm caught an exception
    /// or removed the device, or that failed (<paramref name="failure"/>): its <see cref="DiagnosticsReport"/>, read from its
    /// warm processes' logs before work\ goes, sent in the background. Once per compile: this runs once as it ends.</summary>
    void SendDiagnostics(Game game, string contentHash, string work, DiagnosticFailure? failure, IReadOnlyCollection<string>? crashed)
    {
        if (Diagnostics is not { } diagnostics || !Settings.ShareDiagnostics || contentHash.Length != 40) return;
        try
        {
            var lines = Directory.Exists(work) ? Directory.EnumerateFiles(work, "scskiller.log", SearchOption.AllDirectories).SelectMany(File.ReadLines).ToList() : [];
            if (DiagnosticsReport.Build(lines, failure, crashed, contentHash, EngineLabel(game),
                    ShareVendor(Vendor.Vendor), GpuModel(), DriverVersion(), AppVersion.Current.ToString()) is { } report)
                DiagnosticsSent = Task.Run(() => diagnostics.SendAsync(report));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{game.Name}: reading the compile's diagnostics failed: {e.Message}"); }
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
            var logs = Directory.EnumerateFiles(work, "scskiller.log", SearchOption.AllDirectories).OrderBy(File.GetLastWriteTimeUtc).ThenBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();
            if (logs.Count > 0) File.WriteAllLines(kept, logs.SelectMany(File.ReadLines));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"keeping the warm's rejects log failed: {e.Message}"); }
    }

    string AllFailedReason(string work, long failed) => AllFailedReason(WorkLogs(work), failed);

    /// <summary>Every warm process's log lines in work\; none when they can't be read.</summary>
    static List<string> WorkLogs(string work)
    {
        try { return Directory.EnumerateFiles(work, "scskiller.log", SearchOption.AllDirectories).SelectMany(File.ReadLines).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static readonly System.Text.RegularExpressions.Regex FailTally = new(@"warm: failed x(\d+): (.*)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    // warm11_main's tally: "warm11: PS failed: <why> xN", "warm11: batch failed: device lost xN"
    static readonly System.Text.RegularExpressions.Regex FailTally11 = new(@"warm11: (\S+) failed: (.*) x(\d+)\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A D3D11 failure of the driver or the runtime: a create it refused, an exception inside a D3D11 call, a batch
    /// on a lost or stuck device. Every other one is SCSKiller's: it couldn't build a draw for the shader.</summary>
    static bool DriverFailed11(System.Text.RegularExpressions.Match m) =>
        m.Groups[1].Value == "batch" || m.Groups[2].Value.StartsWith("the runtime rejects", StringComparison.Ordinal) || m.Groups[2].Value == "exception";

    /// <summary>Of the warm processes' D3D11 failures (<paramref name="log"/>), the ones SCSKiller can't build yet (<see cref="DriverFailed11"/>).</summary>
    public static long Unbuilt11(IEnumerable<string> log) =>
        log.Select(l => FailTally11.Match(l)).Where(m => m.Success && !DriverFailed11(m)).Sum(m => long.Parse(m.Groups[3].Value));

    /// <summary>The warm processes' "warm: failed xN: what" and "warm11: VS failed: what xN" tallies by kind: 0 the driver
    /// refused the root signature, 1 the driver or the runtime refused the pipeline, 2 SCSKiller couldn't read it, 3 a D3D11
    /// draw SCSKiller can't build yet.</summary>
    static List<(int kind, long n)> FailKinds(IEnumerable<string> log) => log.Select(l => (d3d12: FailTally.Match(l), d3d11: FailTally11.Match(l)))
        .Where(m => m.d3d12.Success || m.d3d11.Success)
        .Select(m => m.d3d12 is { Success: true } t
            ? (kind: t.Groups[2].Value.Contains("root signature creation failed") ? 0 : t.Groups[2].Value.Contains("hr=0x") ? 1 : 2, n: long.Parse(t.Groups[1].Value))
            : (kind: DriverFailed11(m.d3d11) ? 1 : 3, n: long.Parse(m.d3d11.Groups[3].Value)))
        .GroupBy(t => t.kind, (kind, ts) => (kind, n: ts.Sum(t => t.n))).ToList();

    /// <summary>The kind (<see cref="FailKinds"/>) most of a compile's failures were; null without a tally. D3D11 draws
    /// SCSKiller can't build aren't in its failed count.</summary>
    public static int? TopFailKind(IEnumerable<string> log) =>
        FailKinds(log).Where(k => k.kind != 3 && k.n > 0).OrderByDescending(k => k.n).Select(k => (int?)k.kind).FirstOrDefault();

    // "plan item on 'G' (graphics desc) hr=0x80070057", "replay of 'S' (pipeline stream, 120-byte payload) not created: shader blob not in the db"
    static readonly System.Text.RegularExpressions.Regex FailWhat = new(@"^(replay of|plan item on) '(\w)' \([^)]*\) (?:hr=0x([0-9a-f]{8})|not created: (.*))$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The warm processes' failure tallies (<paramref name="log"/>) as reason and count, most first: where the
    /// pipeline came from (recorded or synthesized), its record kind and the driver's code or SCSKiller's reason.</summary>
    public static List<(string Reason, long Count)> FailReasons(IEnumerable<string> log) => [.. log
        .Select(l => (d3d12: FailTally.Match(l), d3d11: FailTally11.Match(l)))
        .Where(m => m.d3d12.Success || m.d3d11.Success)
        .Select(m => m.d3d12 is { Success: true } t
            ? (reason: FailWhat.Match(t.Groups[2].Value) is { Success: true } w
                ? $"{(w.Groups[1].Value == "replay of" ? "recorded" : "synthesized")} {w.Groups[2].Value} {(w.Groups[3].Success ? "0x" + w.Groups[3].Value : w.Groups[4].Value)}"
                : t.Groups[2].Value.StartsWith("plan item", StringComparison.Ordinal) ? "synthesized " + t.Groups[2].Value["plan item".Length..].TrimStart(':', ' ') : t.Groups[2].Value,
                n: long.Parse(t.Groups[1].Value))
            : (reason: $"D3D11 {m.d3d11.Groups[1].Value} {m.d3d11.Groups[2].Value}", n: long.Parse(m.d3d11.Groups[3].Value)))
        .GroupBy(r => r.reason.Length > 100 ? r.reason[..100] : r.reason, (reason, rs) => (reason, rs.Sum(r => r.n)))
        .OrderByDescending(r => r.Item2).ThenBy(r => r.reason, StringComparer.Ordinal)];

    /// <summary>Why a compile created nothing, from its warm processes' tallies (<see cref="FailKinds"/>): the one reason, or the top one.</summary>
    public static string AllFailedReason(IEnumerable<string> log, long failed)
    {
        var reasons = FailKinds(log);
        var all = $"all {failed:N0} pipelines failed";
        if (reasons.Count == 0) return all;
        var top = reasons.MaxBy(r => r.n).kind;
        return reasons.Count == 1
            ? top switch
            {
                0 => $"the driver refused the root signature of all {failed:N0} pipelines",
                1 => $"the driver rejected all {failed:N0} pipelines",
                3 => $"SCSKiller can't build any of the {failed:N0} pipelines yet",
                _ => $"SCSKiller couldn't read any of the {failed:N0} pipelines",
            }
            : all + top switch
            {
                0 => ", most because the driver refused their root signature",
                1 => ", most rejected by the driver",
                3 => ", most because SCSKiller can't build them yet",
                _ => ", most because SCSKiller couldn't read them",
            };
    }

    /// <summary>The last warm's logs, every process's in order, as <see cref="LastWarmLog"/> in the game's folder (work\ goes
    /// with the item), whatever its outcome; at most <see cref="LastWarmLogBytes"/>: the start (settings, NVAPI state) and the end.</summary>
    void KeepLastLog(string work, string id)
    {
        try
        {
            if (!Directory.Exists(work)) return;
            var logs = Directory.EnumerateFiles(work, "scskiller.log", SearchOption.AllDirectories).OrderBy(File.GetLastWriteTimeUtc).ThenBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();
            if (logs.Count == 0) return;
            var all = Path.Combine(work, LastWarmLog);
            using (var o = File.Create(all))
                foreach (var l in logs) { using var f = new FileStream(l, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); f.CopyTo(o); }
            using var r = File.OpenRead(all);
            AppStore.WriteAtomic(Path.Combine(Store.GameDir(id), LastWarmLog), Bounded(r, LastWarmLogBytes));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"keeping the last warm's log failed: {e.Message}"); }
    }

    /// <summary>The folders <see cref="FreshWork"/> makes: work, or work-&lt;n&gt; when an earlier one can't be emptied.</summary>
    internal static IReadOnlyList<string> WorkDirs(string gameDir) => [.. Enumerable.Range(0, 10).Select(n => Path.Combine(gameDir, n == 0 ? "work" : $"work-{n}"))];

    /// <summary>An empty work folder in <paramref name="gameDir"/>: work\, or work-&lt;n&gt;\ while an earlier one can't be deleted
    /// (something holds a file in it), so nothing an earlier run left is read as this run's. Leftovers go when they can.</summary>
    internal static string FreshWork(string gameDir, IProgress<string>? log)
    {
        string? fresh = null;
        Exception? last = null;
        foreach (var dir in WorkDirs(gameDir))
        {
            for (var retried = false; ; retried = true)
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    if (fresh == null) { Directory.CreateDirectory(dir); fresh = dir; }
                    break;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (!retried) { Thread.Sleep(500); continue; }
                    log?.Report($"could not delete {dir}: {e.Message}");
                    last = e;
                    break;
                }
        }
        return fresh ?? throw new IOException($"no work folder can be made in {gameDir}", last);
    }

    /// <summary>work or work-&lt;n&gt;: a folder <see cref="FreshWork"/> makes.</summary>
    internal static bool IsWorkDir(string name) => name.Equals("work", GameFiles.Names)
        || name.StartsWith("work-", GameFiles.Names) && name.Length > 5 && name[5..].All(char.IsAsciiDigit);

    public const string LastWarmLog = "warm-last.log";
    public const int LastWarmLogBytes = 4 << 20;

    /// <summary><paramref name="log"/>'s bytes, or past <paramref name="max"/> its first quarter and its last, joined by a line
    /// saying how much was left out.</summary>
    internal static byte[] Bounded(Stream log, int max)
    {
        var n = log.Length;
        var (head, tail) = n <= max ? ((int)n, 0) : (max / 4, max - max / 4);
        var cut = n <= max ? [] : System.Text.Encoding.UTF8.GetBytes($"\r\n[... {n - head - tail:N0} bytes left out ...]\r\n");
        var b = new byte[head + cut.Length + tail];
        log.Position = 0;
        log.ReadExactly(b, 0, head);
        cut.CopyTo(b, head);
        log.Position = n - tail;
        log.ReadExactly(b, head + cut.Length, tail);
        return b;
    }

    /// <summary>Why a warm of the game replays everything, not only what complete warms didn't create before
    /// (<see cref="CompiledItems"/>); null: what they created may be left out. Everything goes in again on another driver or
    /// a cleared cache (<see cref="ColdWarm"/>), when the warm no longer matches the game (<see cref="WarmChanged"/>: exe
    /// name and case, game build, layer, AGS, a cache trimmed on AMD or gone on NVIDIA), with other plan settings, a careful
    /// compile, a launch that still compiled after the warm, or cache files not known or gone since the warm. Eviction
    /// inside an NVIDIA file shows only at the next judged launch.</summary>
    internal string? FullReplayReason(string gameId) { var s = Find(gameId); return FullReplayReason(s.Game, Store.LoadGame(gameId), s.Engine, Snapshot()); }

    const string NothingOnRecord = "nothing compiled on this driver is on record", StoppedGone = "what stopped compiles created may be gone";

    /// <summary>The PSOs stopped or failed compiles created (<see cref="CompiledItems"/>' format), for <see cref="GameRecord.StoppedCreated"/>.</summary>
    public const string StoppedFile = "stopped.keys";

    /// <summary>What stopped or failed compiles of the game created on this driver while no compile completed there, when a
    /// compile may leave it out: NVIDIA from 621, where each went into the game's D3DSCache dbs at its fold, every fold done
    /// and none damaged, the dbs still there, and the game's shaders as then. The exe name, layer, AGS and Aftermath the
    /// compile runs with are checked once known (<see cref="StoppedKeyChanged"/>). Null: everything is replayed.</summary>
    HashSet<string>? StoppedLeftOut(Game g, GameRecord r, string? index, string? driverId) =>
        KeepsD3DSCache() && r.StoppedCreated is { Folded: true } s && s.Driver == driverId && s.Index == index
        && r.Plan?.Platform != VulkanPlan.Platform && !r.D3DSCacheDamaged && !D3DSCachePending(g.Id, r) && CurrentD3DSCache(g, r) && StillHeld(s, D3DSCacheDbs(D3DSCacheNames(r)))
            ? CompiledItems.Load(Path.Combine(Store.GameDir(g.Id), StoppedFile)) : null;

    /// <summary>The D3DSCache folders on record are the ones the runtime opens for the game's exe as it is: a moved install's
    /// old one isn't, whether found or made before its first run.</summary>
    bool CurrentD3DSCache(Game g, GameRecord r) => LaunchExe(g) is { } exe && D3DSCache.FolderName(exe) is var now
        && (r.D3DSCacheFolder ?? now) == now && (r.D3DSCacheMade ?? now) == now;   // the name hashes the path's case too

    /// <summary>Each db <paramref name="s"/> recorded is still there, at least as long and not made again since.</summary>
    static bool StillHeld(StoppedCreated s, List<(string Name, long Bytes, DateTime? Made)>? now) =>
        s is { Bytes: { Count: > 0 } was, At: { } at } && now != null
        && was.All(w => now.Any(d => d.Name.Equals(w.Key, StringComparison.OrdinalIgnoreCase) && d.Bytes >= w.Value && d.Made is { } made && made <= at.UtcDateTime));

    /// <summary>Why what <see cref="StoppedLeftOut"/> left out isn't in the cache this compile fills; null: it is.</summary>
    static string? StoppedKeyChanged(StoppedCreated? s, string? driverId, string exe, string? layer, string ags, bool aftermath, bool delivered) =>
        s == null || s.Driver != driverId ? "the stopped compiles were for another driver"
        : !s.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase) ? $"the stopped compiles were for {s.Exe}"
        : s.Layer != layer ? "the stopped compiles ran through another ReShade layer, or none"
        : s.Ags != ags ? "the stopped compiles were for another AGS app name"
        : aftermath ? "this compile runs with NVIDIA Aftermath, which may fall back to another cache key"
        : delivered ? "Advanced Shader Delivery serves the game's pipelines"
        : null;

    /// <summary>A record <see cref="StoppedLeftOut"/> turned down is never taken again: a cache seen gone may be filled anew.</summary>
    void DropStopped(string gameId, GameRecord r)
    {
        r.StoppedCreated = null;
        try { File.Delete(Path.Combine(Store.GameDir(gameId), StoppedFile)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{StoppedFile} not removed: {e.Message}"); }
    }

    string? FullReplayReason(Game g, GameRecord r, EngineInfo? e, GpuSnapshot snap) => ColdWarm(r, snap.Id) ? (r is { WarmedDriverId: LostD3DSCache, D3DSCacheDamaged: false } ? "once, because SCSKiller 1.3.0 removed this driver's cache after its compiles" : NothingOnRecord)
        : WarmChanged(g, r, e, snap) ?? ReplayAgain(r) ?? DeliveryChanged(g, r) ?? CacheFilesChanged(g.Id, r);

    /// <summary>The last complete warm left out what the game's Advanced Shader Delivery served, and that delivery is gone or another.</summary>
    static string? DeliveryChanged(Game g, GameRecord r) =>
        r.WarmedDelivery != null && OperatingSystem.IsWindows() && D3DSCache.Delivered(g.ExePath)?.Stamp != r.WarmedDelivery ? "the game's Advanced Shader Delivery changed" : null;

    /// <summary>NVIDIA: the launch's cold creates of the warm's own pipelines stayed within <see cref="PartlyWarmedShare"/>, so
    /// what it still compiled were pipelines the warm didn't have. Not judged (AMD, too few, an earlier version's record): false.</summary>
    static bool WarmHeld(LaunchCheck? l) => l is { Warmed: { } n and >= MinJudgedCreates, WarmedCold: { } cold } && cold <= PartlyWarmedShare * n;

    /// <summary><see cref="FullReplayReason"/>'s reasons the game's record alone tells.</summary>
    static string? ReplayAgain(GameRecord r) => r.PlanPerStage != r.WarmedPerStage ? "the plan settings changed"
        : r.Careful || r.WarmedCareful ? "a careful compile"
        : IsPartlyWarmed(r.FirstLaunch) && !WarmHeld(r.FirstLaunch) || IsUnreached(r) ? "the game's launch after the last compile still compiled"
        : null;

    /// <summary>Why the last complete warm's cache files may no longer hold what it left in them: not recorded, gone (on AMD
    /// unless kept aside as the same file), made again after it or not known when made, or holding less (<see cref="Shrunk"/>). Null: as it left them. Made again is another
    /// file id where one was recorded (AMD), else a later creation time, which NTFS keeps for a name made again within 15 s.
    /// On Mesa a file gone and Steam's own files don't count: Steam's pre-cache replaces and rotates those (<see cref="MesaLinux.SteamsFile"/>).
    /// NVIDIA from 621 (<see cref="D3D12Kept"/>): the game's D3DSCache dbs, and the files only where <see cref="DxCacheCounts"/>.
    /// AMD into Steam's AMDv1: its .parc (<see cref="SteamParc"/>) gone, unreadable, another file, or shorter unless
    /// <paramref name="during"/> a warm.</summary>
    string? CacheFilesChanged(string gameId, GameRecord r, bool during = false)
    {
        // the game and the warms only grow it: a shorter file lost entries
        if (r.WarmedSteamParcId is { } was && SteamParc(gameId, r, out var parc))
            return parc is { } p && p.Id == was && (during || p.Length >= r.WarmedSteamParcBytes) ? null : SteamParcChanged;
        if (D3D12Kept(r) is { } st)
        {
            if (D3DSCacheTrimmed(gameId, r) != null) return "some of the game's D3DSCache dbs are gone or hold less";
            if (!DxCacheCounts(r, st)) return null;
        }
        if (AppCache is not { } cache || r.WarmedKeys is not { Count: > 0 } keys || r.WarmedFiles is not { Count: > 0 } files || r.WarmedFileBytes is null)
            return "the game's cache files aren't known";
        var mesa = cache is MesaLinuxAppCache;
        var now = cache.FilesOf(keys).Where(f => files.Contains(f.Name) && !(mesa && MesaLinux.SteamsFile(f.Name))).ToList();
        var kept = AppCache is AmdAppCache ? Keeping(gameId) : null;
        if (!mesa && files.Any(n => !now.Any(f => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) && !KeptSame(kept, r, n)))
            return "some of the game's cache files are gone";
        bool MadeAgain(FileInfo f) => r.WarmedFileIds?.GetValueOrDefault(f.Name) is { } was ? AmdKept.Id(f.FullName) != was   // unreadable: not shown to be the same
            : r.WarmedAt is { } at && FileTimes.CreatedUtc(f) > at.UtcDateTime;
        if (now.Any(f => MadeAgain(f) || Shrunk(f, r)))
            return "some of the game's cache files were made again since the last compile";
        if (r.WarmedAt != null && now.Any(f => r.WarmedFileIds?.GetValueOrDefault(f.Name) == null && FileTimes.CreatedUtc(f) == null))
            return MadeUnknownReason;
        return null;
    }

    internal const string MadeUnknownReason = "the file system doesn't tell when the game's cache files were made";
    const string SteamParcChanged = "Steam's cache file of the game changed";

    /// <summary>AMD, a Vulkan game whose recorded session wrote Steam's AMDv1 (<see cref="SteamVulkanDir"/>): its .parc's file id
    /// and length, which stand for the game's cache files AMD's keys don't find. Null: gone, unreadable or a link. False for another game.</summary>
    bool SteamParc(string gameId, GameRecord r, out (string Id, long Length)? parc)
    {
        parc = null;
        if (OwnDriver != GpuVendor.Amd || Games.FirstOrDefault(s => s.Game.Id == gameId) is not { } s || !VulkanRoute(s.Game, s.Engine) || SteamVulkanDir(s.Game, r) is not { } dir) return false;
        var path = Path.Combine(dir, SteamGlAppName + ".parc");
        // a link's length is the link's, not its target's
        try { parc = !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) && AmdKept.Id(path) is { } id ? (id, new FileInfo(path).Length) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return true;
    }

    /// <summary>Index-and-plan rounds a compile redoes for rules that changed during one; then it keeps the last.</summary>
    const int MaxReplans = 2;

    /// <summary>Items the next compile skips where a warm gave up on the driver's recoveries (many times the threads in
    /// flight at a crash), twice as many each time it gives up there again, so a compile always gets to the end.</summary>
    internal const long GiveUpSkip = 1000;

    /// <param name="maxOnce">the compile is a <see cref="CompileMax"/>: the plan must be a Maximum one</param>
    bool PlanIsStale(Game g, GameRecord r, EngineInfo? e, bool maxOnce = false) => PlanIsStale(g.Id, r, e, maxOnce) || r.PlanSteam != SteamDownloads(g, e) || r.PlanPsoCache != PsoCacheInUse(g, e);

    /// <summary>The plan has no items and the next compile would build it the same: this build's index, nothing newer to plan
    /// from (<see cref="PlanIsStale"/>), the same middleware packs. A warm of another plan (a re-plan without one) keeps its status.</summary>
    bool EmptyPlan(Game g, GameRecord r, EngineInfo? e) => PlanItems(r.Plan?.Stats) == 0 && (r.WarmedAt == null || r.WarmedPlanItems == r.PlanItems) && Indexed(g, r) && !PlanIsStale(g, r, e)
        && ((_planner as Planner)?.PackFingerprint(g) ?? "") == (r.PlanMiddleware ?? "");

    bool PlanIsStale(string id, GameRecord r, EngineInfo? e, bool maxOnce) => r.Plan is not { } p || !File.Exists(p.FilePath) || p.IndexContentHash != r.IndexContentHash
                                      || p.VendorProfile != Vendor.Caps.Profile || r.RecordingImportedAt > r.PlanBuiltAt
                                      || !CurrentPlanner(r.PlanVersion, r.PlanRules, r.PlanReaderVersion, e) || r.PlanPerStage != PerStagePlans(r, maxOnce) || Uncapped(r, maxOnce) || OverCacheBudget(Vendor.Caps, r.Plan?.Stats) || r.PlanCommunity != CommunityObjects(id)
                                      || r.PlanOfficial != OfficialInUse(id)?.Object || RtPlanCheck(r, e) || OtherApi(r, e) || OtherEngine(r, e) || r.PlanRayTracing != RayTracing(r)
                                      || r.PlanFiltering != Filtering(id, false, e) || BudgetGrew(id, r) || BudgetShrank(id, r) || OverCap(r, e) || RuleFallbackDue(r);

    /// <summary>A learned-lookup plan built before <see cref="PlanBuilder.RuleFallback"/>: rebuilt once, with the rule's stage sets.</summary>
    static bool RuleFallbackDue(GameRecord r) => r.Plan?.Stats is { BuiltOk: { } ok, BuiltN: { } n, RuleFallback: null } && PlanBuilder.RuleFallback(ok, n);

    /// <summary>The caps a game's plan is built with: on a vendor with a warm budget (NVIDIA on Linux), the game's compile cache
    /// (<see cref="CompileCap"/>) and what one of its PSOs took there (<see cref="GameRecord.BytesPerPso"/>).</summary>
    VendorCaps PlanCaps(string id, GameRecord r)
    {
        var caps = CapsOf(id);
        if (r.CacheCapGB is { } gb) caps = caps with { WarmCacheBudget = Math.Min(VendorBudget(caps), (long)gb << 30) };
        if (caps.WarmCacheBudget == null) return caps;
        var pso = r.BytesPerPso is { } b && b >= 1 ? (long)Math.Ceiling(b) : PlanBuilder.PsoBytesOf(caps);
        // the measured growth is the driver cache's only: the Windows shader cache keeps every pipeline too
        if (KeepsD3DSCache()) pso += D3DSCachePsoBytes;
        return pso != PlanBuilder.PsoBytesOf(caps) ? caps with { PsoBytes = pso } : caps;
    }

    /// <summary>NVIDIA from 621: what a compiled pipeline adds to the game's Windows shader cache. Measured after complete
    /// compiles: Borderlands 4 23.2 KiB, Wuthering Waves 11.9 and 26.2, Fortnite 15.1, Cyberpunk 2077 14.0, Gears of War:
    /// E-Day 66.3 (median 19.2).</summary>
    public const long D3DSCachePsoBytes = 20 << 10;

    /// <summary>The vendor's caps with <paramref name="id"/>'s compile cache folder in them (<see cref="CompileCap"/>), as its plan's cache budget sees them.</summary>
    public VendorCaps CapsOf(string id) => Vendor.Caps.WarmCacheBudget != null && CompileCap(id) is { } cap
        ? Vendor.Caps with { PerGameCacheCap = cap, WarmCacheBudget = Vendors.NvidiaLinux.BudgetOf(cap) } : Vendor.Caps;

    /// <summary>What the game's compile cache folder may hold, when it isn't the vendor's (<see cref="CompileCacheCap"/>).</summary>
    long? CompileCap(string id)
    {
        long? cap = null;
        if (Games.FirstOrDefault(x => x.Game.Id == id) is { } s) CompileCacheCap(s, ref cap);
        return cap;
    }

    /// <summary>A plan that left PSOs out for its cache budget, built under a smaller one than the game has now: rebuilt so it
    /// fills the room. Not while a stopped compile waits to go on: starting over would compile its part again.</summary>
    bool BudgetGrew(string id, GameRecord r) => r.ResumeAt == 0 && r.Plan?.Stats is { CacheBudgetLeftOut: > 0 }
        && PlanCaps(id, r) is var caps && (caps.WarmCacheBudget ?? VendorBudget(caps)) is var now && (now > (r.PlanCacheBudget ?? Vendor.Caps.WarmCacheBudget ?? now) || ChargeDropped(r));

    /// <summary>The plan counted the Windows shader cache per pipeline (<see cref="GameRecord.PlanD3DSCacheCharged"/>) and the driver no longer keeps one.</summary>
    bool ChargeDropped(GameRecord r) => r.PlanD3DSCacheCharged && !KeepsD3DSCache();

    /// <summary>The plan was built under a larger budget than the game's compile size limit (<see cref="GameRecord.CacheCapGB"/>) allows,
    /// or (NVIDIA) by a planner that left the limit out of its per-stage cover: no budget count in its stats.</summary>
    bool OverCap(GameRecord r, EngineInfo? e) => r.CacheCapGB is { } gb && ((r.PlanCacheBudget ?? long.MaxValue) > (long)gb << 30
        || Vendor.Caps is { PerStageCache: true, StateIndependentCache: true } && e != null && !VulkanPlan.IsVulkan(e) && r.Plan?.Stats is { Generated: > 0, CacheBudgetLeftOut: null });

    /// <summary>A plan built under a larger cache budget than the game has now (its cache goes where less fits): rebuilt so it fits.</summary>
    bool BudgetShrank(string id, GameRecord r) => r.ResumeAt == 0 && r.PlanCacheBudget is { } was && PlanCaps(id, r) is { WarmCacheBudget: { } now } caps
        && (now < was || KeepsD3DSCache() && !r.PlanD3DSCacheCharged)   // a plan from before the driver kept a Windows shader cache, or before it counted
        && r.Plan?.Stats is { } p && (p.Recorded + p.Generated + p.Variants) * PlanBuilder.PsoBytesOf(caps) > now;

    /// <summary>Plans have each stage unit once: the vendor caches per stage and Maximum mode is off. A pairing plan warmed
    /// earlier already holds every unit, so going per-stage rebuilds the plan but needs no re-warm (see StaleReason).</summary>
    bool PerStagePlans(GameRecord r, bool maxOnce = false) => Vendor.Caps.PerStageCache && !Maximum(r) && !maxOnce;

    /// <summary>A Maximum plan on a cache whose state is in the key (AMD) built before the per-game cap: rebuilt so the cap applies.</summary>
    bool Uncapped(GameRecord r, bool maxOnce = false) => (Maximum(r) || maxOnce) && Vendor.Caps is { PerStageCache: true, StateIndependentCache: false } && r.PlanMaximumCapped != true;

    /// <summary>A plan on a cache whose state is in the key (AMD, Intel) built before its budget (<see cref="PlanBuilder.CacheBudgetOf"/>),
    /// over it: rebuilt so the budget applies.</summary>
    internal static bool OverCacheBudget(VendorCaps caps, PlanStats? p) => caps is { PerStageCache: true, StateIndependentCache: false }
        && p is { CacheBudgetLeftOut: null } && p.Recorded + p.Generated + p.Variants > PlanBuilder.CacheBudgetOf(caps) / PlanBuilder.PsoBytes;

    /// <summary>Max compile plans something a standard compile doesn't: stage pairings, which only a per-stage cache leaves out.
    /// Vulkan plans and DirectX 11 shaders have no pairings to add.</summary>
    public static bool MaxCompileAdds(VendorCaps caps, EngineInfo? e) => caps.PerStageCache && e != null && !Vulkan.VulkanPlan.IsVulkan(e) && !Dx11Only(e);

    /// <summary>Maximum mode for the game: on for every game in Settings, or for this one (<see cref="SetGameMaximum"/>).</summary>
    bool Maximum(GameRecord r) => Settings.MaximumPlans || r.Maximum == true;

    /// <summary>The game's compiles include ray tracing state objects and collections: its override, else Settings. The one
    /// decision the plan, the recording the warm replays and the count of new pipelines follow (<see cref="Planner.LeftOutWithoutRt"/>).</summary>
    bool RayTracing(GameRecord r) => r.RayTracing ?? Settings.CompileRayTracing;

    public void SetGameRayTracing(string gameId, bool? on)
    {
        var s = Find(gameId);
        lock (_lock)
            if (_current == gameId) throw new InvalidOperationException($"a compile of {s.Game.Name} is in progress");
        var rec = Store.LoadGame(gameId);
        if (rec.RayTracing == on) return;
        rec.RayTracing = on;
        Store.SaveGame(gameId, rec);
        Refresh(s.Game);
    }

    public void SetGameTraversal(string gameId, bool? on)
    {
        lock (_recorderLock)
        {
            var s = Find(gameId);
            var rec = Store.LoadGame(gameId);
            if (rec.Traversal == on) return;
            rec.Traversal = on;
            Store.SaveGame(gameId, rec);
            if (!ManageRecorders || !Reconcile(s)) Refresh(s.Game);
        }
    }

    public void SetGameCacheCap(string gameId, int? gb)
    {
        if (gb is <= 0) throw new ArgumentOutOfRangeException(nameof(gb));
        var s = Find(gameId);
        lock (_lock)
            if (_current == gameId) throw new InvalidOperationException($"a compile of {s.Game.Name} is in progress");
        var rec = Store.LoadGame(gameId);
        if (rec.CacheCapGB == gb) return;
        rec.CacheCapGB = gb;
        Store.SaveGame(gameId, rec);
        Refresh(s.Game);
    }

    public void SetGameLeftOutOfAddAll(string gameId, bool on)
    {
        var s = Find(gameId);
        var rec = Store.LoadGame(gameId);
        if (rec.LeftOutOfAddAll == on) return;
        rec.LeftOutOfAddAll = on;
        Store.SaveGame(gameId, rec);
        Refresh(s.Game);
    }

    public void SetGameUseShared(string gameId, bool on)
    {
        var s = Find(gameId);
        var rec = Store.LoadGame(gameId);
        if (rec.NoSharedHashes == !on) return;
        rec.NoSharedHashes = !on;
        using (Recordings.Lock(RecordingPath(gameId)))   // never while a compile (any process's) prepares the downloads it labels its plan with
            Store.SaveGame(gameId, rec);
        Refresh(s.Game);   // its downloads leave or join its inputs: the plan turns stale
        var now = Find(gameId);
        // a compiled game's plan is rebuilt as the scan's plan check does, another game's next compile plans anew
        if (CheckPlans && now.Engine != null && now.Status != GameStatus.Unsupported && (NeedsPlanCheck(s.Game, rec, now.Engine)
                || rec.WarmedAt != null && ((_planner as Planner)?.PackFingerprint(s.Game) ?? "") != (rec.PlanMiddleware ?? "")))
            CheckPlan(gameId);
        if (on) StartCommunitySync([now]);
    }

    /// <summary>Turns Maximum mode on or off for the game alone (the suggestion's "Turn on for this game"). The game turns
    /// stale like a change of the setting does, and its next compile adds the pairings, capped per game.</summary>
    public void SetGameMaximum(string gameId, bool on)
    {
        var s = Find(gameId);
        lock (_lock)
            if (_current == gameId) throw new InvalidOperationException($"a compile of {s.Game.Name} is in progress");
        var rec = Store.LoadGame(gameId);
        if ((rec.Maximum == true) == on) return;
        (rec.Maximum, rec.ResumeAt) = (on ? true : null, 0);   // a stopped warm's resume point counts the other plan's items
        Store.SaveGame(gameId, rec);
        Refresh(s.Game);
        KeepList();
    }

    public CompatSent? ReportSent(string gameId) => CompatSent.For(Store.GameDir(gameId), CompatReport.BuildKey(Find(gameId).Game));

    public async Task<string?> SendReportAsync(string gameId, CompatChoice choice, bool frameGen, bool upscaler, bool mods, CancellationToken ct = default)
    {
        var listed = Find(gameId).Game;
        // the scan's copy keeps the build it had then: a game updated since is read again, so the gate sees the installed build
        if (await Task.Run(() => Discovered(listed), ct) is not { } installed) throw new InvalidOperationException(CompatReport.NoBuild);
        if (installed.Version != listed.Version) Refresh(installed);
        var s = Find(gameId);
        var vendor = PackGpu(Vendor.Vendor);
        if (CompatReport.WhyNot(s, vendor) is { } why) throw new InvalidOperationException(why);
        if (Sharing is not { } sharing) throw new InvalidOperationException("reports aren't sent from here");
        var key = CompatReport.BuildKey(s.Game);
        LinuxSystem? linux = null;
        LinuxOf(s.Game, ref linux);
        var report = new CompatReport(s.Game.Id, key, choice, frameGen, upscaler, mods, CpuModel(), GpuModel(), vendor!, DriverVersion(),
            linux == null ? CompatReport.ThisWindows : null, AppVersion.Current.ToString(), CompatReport.CompileOf(s, Settings.MaximumPlans)) { Linux = linux };
        if (await sharing.SendCompatAsync(report, ct) is { } problem) return problem;
        CompatSent.Save(Store.GameDir(gameId), key, new(choice, DateTimeOffset.UtcNow));
        return null;
    }

    public string ProblemReport(string gameId, string store, IReadOnlyList<(string Label, string Value)> page, bool withLog)
    {
        var g = Find(gameId).Game;
        if (Store.LoadGame(gameId).FirstLaunch is { } fl) page = [.. page, ("First launch after the last compile", $"{fl.Hits} cache hits, {fl.Compiles} compiles")];
        if (WhyFailed(gameId) is { } why) page = [.. page, ("Why pipelines failed", why)];
        var log = withLog ? App.ProblemReport.LogLines(Store.DataDir, [g.Name, g.Id, Path.GetFileName(Store.GameDir(g.Id)), Path.GetFileNameWithoutExtension(g.ExePath)]) : null;
        var traversal = withLog ? App.ProblemReport.TraversalLines(Path.Combine(SessionDir(g), "scskiller.log")) : null;
        LinuxSystem? linux = null;
        LinuxOf(g, ref linux);
        return App.ProblemReport.Build(AppVersion.Current.ToString(), g.Name, store, ShareVendor(Vendor.Vendor), GpuModel(),
            Vendor.Gpu.DriverVersion is { Length: > 0 } d ? d : null, CompatReport.SystemLine(linux), page, log, App.ProblemReport.Masker.ThisPc, traversal);
    }

    /// <summary>The last warm's top 3 failure reasons with their counts (<see cref="FailReasons"/> of <see cref="LastWarmLog"/>);
    /// null when it has none, or the game's record has no failure of a complete warm (a cleared cache).</summary>
    public string? WhyFailed(string gameId)
    {
        if (Store.LoadGame(gameId) is not ({ LastWarmFailed: > 0 } or { LastWarmUnbuilt: > 0 })) return null;
        try
        {
            var path = Path.Combine(Store.GameDir(gameId), LastWarmLog);
            return File.Exists(path) && FailReasons(File.ReadLines(path)) is { Count: > 0 } r ? string.Join(", ", r.Take(3).Select(x => $"{x.Reason} ×{x.Count:N0}")) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Sets <paramref name="linux"/> to the system a report about <paramref name="game"/> names on Linux.</summary>
    partial void LinuxOf(Game game, ref LinuxSystem? linux);

    public bool LinuxReportsRefused => Sharing?.RefusesLinux == true;

    readonly Lock problemsGate = new();

    public async Task<(string? Id, string? Problem)> SendProblemAsync(string gameId, string message, string report, CancellationToken ct = default)
    {
        var game = Games.FirstOrDefault(s => s.Game.Id == gameId)?.Game.Name ?? gameId;
        var sent = await App.ProblemReport.SendAsync(message, report, ct);
        if (sent.Id is { } id)
            try { lock (problemsGate) Store.SaveProblems([.. Store.LoadProblems(), new(id, game, DateTimeOffset.UtcNow)]); }
            catch (Exception) { }   // sent all the same: the popup shows the id
        return sent;
    }

    public IReadOnlyList<SentProblem> ProblemsSent() => [.. Store.LoadProblems().OrderByDescending(p => p.At)];

    /// <summary>The processor's name as the system shows it, nothing else of the CPU; null when it has none.</summary>
    private static partial string? CpuModel();

    // the server's cap
    static string? CpuName(string? name) => name?.Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 100)] : null;

    /// <summary>On a cache whose state is in the key (AMD, Intel), at most what Maximum adds for a game with this plan: the per-game
    /// cap (<see cref="PlanBuilder.MaximumExtras"/>) times the measured bytes a pairing takes. Null elsewhere or without a
    /// per-stage plan to count from.</summary>
    long? MaximumAdds(PlanStats? plan) => Vendor.Caps is { PerStageCache: true, StateIndependentCache: false } && plan is { Generated: > 0 } p
        ? PlanBuilder.MaximumExtras(Vendor.Caps, p.Recorded + p.Generated) * PlanBuilder.ExtraBytes : null;

    /// <summary>The suggestion for the game on this PC's GPU vendor, null when it isn't listed or Maximum is on for it already.</summary>
    MaximumMode.Suggestion? MaximumSuggestion(Game g, GameRecord r) => Maximum(r) ? null : MaximumMode.Current.For(g, PackGpu(Vendor.Vendor));

    /// <summary>Waits for the run. Stops it gracefully (Yielded) when a process named like the warm's exe starts: the game
    /// itself, or another discovered game of that exe name, even while the run is paused. Keeps it suspended while the
    /// queue is paused, while another discovered game runs (background runs with PauseWhileGaming) or, for a "when idle"
    /// item, while the user is at the PC. Checked every 0.5 s, so input pauses it within a second. A Vulkan run also yields
    /// to Steam's own replay (<see cref="SteamReplaying"/>), which writes the same cache.</summary>
    async Task<(WarmResult Result, bool Yielded)> Watch(IWarmRun run, string id, string exe, string name, bool vulkan = false)
    {
        string? applied = "";   // "" = nothing applied yet; null = running
        bool yielded = false;
        while (await Task.WhenAny(run.Completion, Task.Delay(Poll)) != run.Completion)
        {
            if (yielded) continue;
            var running = Running();
            var steam = vulkan && SteamReplaying();
            if (running.Contains(exe) || steam)
            {
                yielded = true;
                if (!steam) _ownGameSeen[exe] = Timestamp();
                _pauseWhy = steam ? SteamPrecachingNote : StoppedFor(name, exe);
                SetCurrent(QueueStage.Paused, _pauseWhy);
                run.Stop();   // graceful (resumes a suspended run first): the driver writes and releases the game's files
                continue;
            }
            bool background, rightAway;
            lock (_lock) (background, rightAway) = (Background || _whenIdle.Contains(id), _whenIdle.Contains(id) && _rightAway.Contains(id));   // a Compile click during the warm: no longer paused for games
            var playing = background && (Settings.PauseWhileGaming || rightAway) ? GameNameIn(running) : null;
            _pauseWhy = playing != null ? $"paused while {playing} is running" : LowDisk() ?? (WaitsForIdle(id) ? "paused until the PC is idle" : null);
            var want = _go.IsSet ? _pauseWhy : _pauseWhy ?? "paused";
            if (want == applied) continue;
            applied = want;
            if (want == null) { run.Resume(); SetCurrent(QueueStage.Warming); }
            else { run.Pause(); SetCurrent(QueueStage.Paused, _pauseWhy); }
        }
        _pauseWhy = null;
        return (await run.Completion, yielded);
    }

    /// <summary>A warm writes the driver's cache and D3DSCache under LocalAppData and its staging under the data folder.</summary>
    string? LowDisk() => new[] { LocalAppData, Store.DataDir }.FirstOrDefault(d => D3DSCache.FreeBytes(d) < D3DSCache.DiskFloor) is { } low
        ? $"paused below {Format.Bytes(D3DSCache.DiskFloor)} free on {D3DSCache.Drive(low)}" : null;

    /// <summary>Names the processes of that exe name, SCSKiller's staged warms left out, with their paths when they can be
    /// read: one the user doesn't know of (a leftover, a copy elsewhere) can then be found in Task Manager.</summary>
    string StoppedFor(string name, string exe)
    {
        var all = Processes(true);
        var held = all.Where(p => p.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase) && NotOurs(p, all)).Take(3)
            .Select(p => ProcessPath(p.Pid) is { } path ? $"process {p.Pid}: {path}" : $"process {p.Pid}").ToList();
        return $"stopped while {name} is running: continues when it exits" + (held.Count > 0 ? $" ({string.Join(", ", held)})" : "");
    }

    /// <summary>Waits while a process named like the warm's exe runs (see RunItem), and until it has stayed closed for
    /// <see cref="GameClosedFor"/>, shown as Paused with the reason; <paramref name="learn"/> attributes the cache files the
    /// game holds open meanwhile (every few seconds).</summary>
    async Task WhileGameRuns(string exe, string name, CancellationToken ct, Action? learn = null)
    {
        bool runs = false;
        bool Waits()
        {
            if (runs = Running().Contains(exe)) _ownGameSeen[exe] = Timestamp();
            return runs || _ownGameSeen.TryGetValue(exe, out var seen) && Stopwatch.GetElapsedTime(seen, Timestamp()) < GameClosedFor;
        }
        if (!Waits()) return;
        try
        {
            _pauseWhy = StoppedFor(name, exe);
            SetCurrent(QueueStage.Paused, _pauseWhy);
            Stopwatch? sampled = null;
            bool shown = true;
            while (Waits())
            {
                if (runs != shown)
                {
                    _pauseWhy = (shown = runs) ? StoppedFor(name, exe) : $"continues {GameClosedFor.TotalMinutes:0} min after {name} closes";
                    SetCurrent(QueueStage.Paused, _pauseWhy);
                }
                if (runs && learn != null && (sampled == null || sampled.Elapsed >= AttributionInterval)) { sampled = Stopwatch.StartNew(); learn(); }
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
        var running = all.Where(p => disk.ContainsKey(p.Exe) && NotOurs(p, all)).Select(p => disk[p.Exe])   // the install's case
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        AddLaunches(all, running);
        return running;
    }

    /// <summary>Adds the exes of the games a launch of is under way, before any process of theirs runs.</summary>
    partial void AddLaunches(List<(int Pid, int Parent, string Exe)> all, HashSet<string> running);

    string? GameNameIn(IReadOnlySet<string> runningExes) =>
        Games.FirstOrDefault(s => runningExes.Contains(Path.GetFileName(s.Game.ExePath)))?.Game.Name;

    // Polled: WMI's Win32_ProcessStartTrace would push starts but needs admin (access denied unelevated).
    volatile HashSet<string> _playing = [];                 // game ids; replaced, never changed in place (read from any thread)
    readonly Dictionary<string, int> _absent = [];          // playing game id -> polls in a row its exe wasn't running
    readonly Dictionary<string, bool> _startsRenderer = [];   // playing game id -> StartsRenderer, until its follow is decided
    readonly Dictionary<string, (int Pid, string Exe)> _rendererSeen = [];   // game id -> the renderer FollowRenderer saw at the last poll
    readonly Dictionary<string, UnrealOther> _unrealOther = [];   // playing Unreal games seen running only another exe of their Shipping exe's folder
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
    /// a game): with the app in the notification area nothing else takes the recorder out of a game an update gave anti-cheat
    /// before its next launch.</summary>
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
        Stopwatch? recordersChecked = null;   // the first pass once a scan listed the games: a start that reads no game walks them
        Task? check = null;
        Task? placing = null;
        SteamPlaceStart(ct, ref placing);
        var wasUnseen = false;
        while (!ct.IsCancellationRequested)
        {
            try { PollGames(); }
            catch (Exception e) { Log?.Report($"watching games: {e.Message}"); }
            // the whole pass off the loop: a slow install never holds the polls back. Also with Settings.ScanAtStart off: it is
            // what takes the recorder out of a game an update gave anti-cheat while the app runs or was closed
            if (check is not { IsCompleted: false } && (recordersChecked?.Elapsed >= RecorderCheckInterval || recordersChecked == null && Volatile.Read(ref _listed) > 0))
            {
                recordersChecked = Stopwatch.StartNew();
                check = Task.Run(WalkRecorderGames);
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
        // the app quits: one more try, no compile or clear started, and the journal keeps the rest for the next start
        try { PutBackSetAside(resume: false); }
        catch (Exception e) { Log?.Report($"putting back set-aside caches: {e.Message}"); }
        if (check != null)
            try { await check; }
            catch (Exception) { }   // logged by its continuation
        if (placing != null)
            try { await placing; }
            catch (Exception e) { Log?.Report($"keeping caches in Steam's folders: {e.Message}"); }
    });

    /// <summary>The watcher's anti-cheat pass: the offline sessions and the uninstalled games' recorders left to clean up
    /// (<see cref="CleanUninstalled"/>), then every game with the recorder in
    /// checked in full, each on its own, and, while no compile runs, every game whose check didn't finish; one that doesn't
    /// end within <see cref="RecorderCheckTimeout"/> keeps the recorder out until one does. A hit takes the recorder out, a
    /// clean check of a game whose check didn't finish puts it back as a reconcile does. A clean check of a game whose stamp in scskiller.ini is no
    /// longer its exe's (an update while the app ran) writes the stamp taken before the check (<see cref="Attest"/>). A game
    /// with anti-cheat whose recorder is still on disk (a removal that failed) is tried again every
    /// <see cref="RemovalRetryInterval"/>.</summary>
    public async Task CheckRecorderGames() => await Task.WhenAll(await WalkRecorderGames());

    /// <summary><see cref="CheckRecorderGames"/>'s walks; the offline cleanups and removals it starts run on their own and are
    /// returned, so one waiting for the recorder lock never holds the next pass back.</summary>
    internal async Task<List<Task>> WalkRecorderGames()
    {
        Task Off(Action work, string what) => Task.Run(() =>
        {
            try { work(); }
            catch (Exception e) { Log?.Report($"{what}: {e.Message}"); }
        });
        RetryIndexes();
        var removals = new List<Task> { Off(CleanOfflineSessions, "cleaning up after offline sessions") };
        removals.Add(Off(ArmAlways, "starting Always record sessions"));
        if (ManageRecorders) removals.Add(Off(() => CleanUninstalled(), "cleaning up after uninstalled games"));
        if (ManageRecorders) removals.Add(Off(SweepVulkan, "sweeping the Vulkan recorders' registrations"));
        var states = Games;
        var vac = states.Where(s => VacCheck(s.Game)).Select(s => s.Game.Id).ToHashSet();   // Steam's verdict now, not the last evaluation's
        bool compiling;
        lock (_lock) compiling = _current != null;
        var pending = states.Where(s => !IsUnchecked(s) && !vac.Contains(s.Game.Id) && Collections.RecordedBy(s.Game.Id) == null
                && (s.AntiCheat == AntiCheat.None && RecorderIn(s) || !compiling && _unknown.ContainsKey(s.Game.Id)))
            .Select(s => ArmTarget(s.Game)).Select(g => (Game: g, Stamp: Stamp(g.ExePath), Gen: RemovalGen(g.Id), Unknown: _unknown.ContainsKey(g.Id)))
            .Select(c => (c.Game, c.Stamp, c.Gen, c.Unknown, Walk: Task.Run(() => LowIo(() => FullCheck(c.Game))))).ToList();
        var deadline = Task.Delay(RecorderCheckTimeout);
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending.Select(p => p.Walk).Append(deadline));
            var ended = done == deadline ? pending.ToList() : pending.Where(p => p.Walk.IsCompleted).ToList();
            foreach (var p in ended)
            {
                pending.Remove(p);
                var unfinished = !p.Walk.IsCompletedSuccessfully || p.Walk.Result == null;
                var found = unfinished ? AntiCheat.Other : p.Walk.Result!.Value;   // out of time or failed: not known clean
                if (found == AntiCheat.None && p.Unknown)
                {
                    lock (_scanLock)
                        if (RemovalGen(p.Game.Id) == p.Gen && _unknown.TryRemove(p.Game.Id, out _)) _verdicts.TryRemove(p.Game.Id, out _);   // nothing found or given up on since the walk began
                    removals.Add(Off(() =>
                    {
                        Refresh(p.Game);
                        if (ManageRecorders) ReconcileRecorders(p.Game.Id);
                    }, $"{p.Game.Name}: checking it for anti-cheat again"));
                }
                else if (found == AntiCheat.None) Restamp(p.Game, p.Stamp, p.Gen);
                else   // each on its own: one waiting for the recorder lock never holds another game's finding back
                    removals.Add(Off(() =>
                    {
                        AntiCheatFound(p.Game, found, unfinished: unfinished);
                        Refresh(p.Game);
                    }, $"{p.Game.Name}: removing the recorder"));
            }
        }
        foreach (var s in states.Where(s => s.AntiCheat != AntiCheat.None || vac.Contains(s.Game.Id)))
        {
            if (_removalTried.TryGetValue(s.Game.Id, out var at) && at.Elapsed < RemovalRetryInterval) continue;
            var rec = Store.LoadGame(s.Game.Id);
            if (rec.RecorderFiles.Count == 0 && rec.RecorderChained == null && rec.VulkanLayer == null && !ProxyOnDisk(s.Game) && !ProxyOnDisk(RecordedAt(s.Game, rec))
                && !new[] { s.Game.ExePath, rec.RecorderExe, rec.RecorderMoveFrom, rec.RecorderMoveTo }.OfType<string>().Any(e => IniPending(Store, Path.GetDirectoryName(e)!))) continue;
            _removalTried[s.Game.Id] = Stopwatch.StartNew();
            removals.Add(Off(() =>
            {
                TakeOutNow(s.Game, s.AntiCheat != AntiCheat.None ? $"{s.AntiCheat} found" : "VAC found");
                Refresh(s.Game);
            }, $"{s.Game.Name}: removing the recorder failed"));
        }
        if (ManageRecorders)   // a game's own Intel driver copy that arrived while the app ran
            foreach (var s in states.Where(s => s.RecorderInstalled && IntelLocalDriver(s.Game)))
                removals.Add(Off(() =>
                {
                    Refresh(s.Game);
                    ReconcileRecorders(s.Game.Id);
                }, $"{s.Game.Name}: removing the recorder"));
        return removals;
    }

    /// <summary>The game's recorder may be in its folder: its state says so, its recorder dll couldn't be read when it was
    /// evaluated (it may be ours, recorded or not), or its record names files or a folder of it (a state from the kept list,
    /// a start that read no game, may be older than the record).</summary>
    bool RecorderIn(GameState s)
    {
        if (s.RecorderInstalled || _unreadProxies.ContainsKey(s.Game.Id)) return true;
        try { return Store.LoadGame(s.Game.Id) is var r && (r.RecorderFiles.Count > 0 || r.RecorderChained != null || r.RecorderExe != null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return true; }
    }

    /// <summary>The pass's clean check of a game whose signed stamp is stale or missing: <paramref name="stamp"/> signed and
    /// written now (<see cref="Attest"/>), unless the game runs (its exit's reconcile checks it again), game folders are left
    /// alone (<see cref="ManageRecorders"/>), or the recorders are busy (a compile's import): then the next pass. A pass never
    /// waits here.</summary>
    void Restamp(Game g, string? stamp, long gen)
    {
        if (!ManageRecorders || stamp == null || !Monitor.TryEnter(_recorderLock)) return;
        try
        {
            if (OfflineLive(g.Id) || !GameFolderWrite(g) || Attested(g)) return;
            var was = IniStamp(g.ExePath);
            if (Attest(g, Store.LoadGame(g.Id), stamp, gen) && was is { Length: > 0 })
                RecorderLog($"{g.Name}: the recorder's scskiller.ini didn't match {Path.GetFileName(g.ExePath)} (the game changed): checked again");
        }
        finally { Monitor.Exit(_recorderLock); }
    }

    /// <summary>A pass's anti-cheat check that takes longer didn't finish: no recorder until one does. Replaceable for tests.</summary>
    public TimeSpan RecorderCheckTimeout { get; set; } = GameFiles.Budget + TimeSpan.FromSeconds(5);
    /// <summary>Replaceable for tests.</summary>
    public TimeSpan RemovalRetryInterval { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>The full anti-cheat check of a recorder game, before its stamp is written; null: it ran out of time.
    /// Replaceable for tests.</summary>
    internal Func<Game, AntiCheat?> FullAntiCheatCheck { get; set; } = g =>
    {
        var late = false;
        var found = GameFiles.DetectAntiCheat(g, late: () => late = true);
        return late ? null : found;
    };
    /// <summary>Valve Anti-Cheat (<see cref="Vac.Is"/>): the recorder never goes in, nor is ever stamped.</summary>
    internal Func<Game, bool> VacCheck { get; set; } = g => Vac.Is(g);
    readonly ConcurrentDictionary<string, Stopwatch> _removalTried = new();   // game id -> the last retry of a removal anti-cheat asked for

    /// <summary><see cref="Vac.ReadError"/>, logged once per scan. Replaceable for tests.</summary>
    internal Func<string?> VacReadError { get; set; } = () => Vac.ReadError();

    /// <summary>The files the app and the proxy write next to the exe: none is an anti-cheat marker.</summary>
    internal static readonly HashSet<string> RecorderOwnFiles = new([.. RecorderDataFiles, .. Recordings.AppFiles, Dll12, Dll11, Dll9, "scskiller.ini", ChainName, ChainName11, AgsChainName, ArmedFile,
        DllVulkan, Dll12 + TempSuffix, Dll11 + TempSuffix, Dll9 + TempSuffix, AgsName + TempSuffix, "scskiller.ini" + TempSuffix, LedgerStamp, LedgerStamp + TempSuffix], StringComparer.OrdinalIgnoreCase);

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

    /// <summary>1.2's attestation beside the exe, which its proxy records under (with its ledger entry in the recorder folder's
    /// armed\, which every start deletes first): taken out wherever a recorder is stamped or removed.</summary>
    public const string ArmedFile = "scskiller.armed";

    /// <summary>The copy's identity in a game folder's <see cref="LedgerStamp"/>: its data folder's armed\ path (the name
    /// #544's copies, which keep a ledger there, write too, so both builds agree whose a folder's recorder is). The proxy
    /// reads the copy's <see cref="RecorderDir"/> from it.</summary>
    internal static string LedgerDir
    {
        get => _ledgerDir ??= DataLedger;   // not at type init: resolving a portable data folder copies it
        set => _ledgerDir = value;   // tests
    }

    static string? _ledgerDir;

    internal static string DataLedger => Path.Combine(AppStore.DefaultDir, "armed");

    /// <summary>The installed copy's <see cref="LedgerDir"/> (data folder %LOCALAPPDATA%\SCSKiller): a recorder without a
    /// <see cref="LedgerStamp"/> is its. Replaceable for tests.</summary>
    internal static string DefaultLedger { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCSKiller", "armed");

    /// <summary>Beside a recorder placed by a copy that isn't the installed one (a portable or test copy): that copy's
    /// <see cref="LedgerDir"/> (UTF-8, one line). It goes last, with the recorder's data files.</summary>
    public const string LedgerStamp = "scskiller.ledger";

    static bool SameDir(string a, string b) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), GameFiles.Names);

    static bool DefaultCopy => SameDir(LedgerDir, DefaultLedger);

    /// <summary>The proxy finds this copy's recorder folder only through its <see cref="LedgerStamp"/>: a copy that isn't the
    /// installed one, and every copy on Linux, whose recorder runs under Wine, where %LOCALAPPDATA% is the game prefix's.</summary>
    static bool WritesLedger => !DefaultCopy || !OperatingSystem.IsWindows();

    /// <summary>A <see cref="LedgerStamp"/>'s text: <paramref name="ledgerDir"/> as the proxy opens it.</summary>
    private static partial string LedgerText(string ledgerDir);

    /// <summary>A <see cref="LedgerStamp"/>'s text names this copy: as it writes it, or as its data folder's armed\ path.</summary>
    static bool OwnLedger(string named) => named.Equals(LedgerText(LedgerDir), GameFiles.Names) || SameDir(named, LedgerDir);

    /// <summary>The folder's recorder, or what one left there, belongs to another SCSKiller copy (another data folder): its
    /// <see cref="LedgerStamp"/> names another copy, or it has none (the installed copy's) and this copy isn't that one.
    /// Such a folder is never adopted, stamped, updated, emptied or imported from. A marker that can't be read counts as
    /// another's.</summary>
    internal static bool Foreign(string dir)
    {
        try
        {
            var stamp = Path.Combine(dir, LedgerStamp);
            // listed, not File.Exists: an entry whose attributes can't be read is read, and fails as another copy's
            bool listed;
            try { listed = Directory.EnumerateFileSystemEntries(dir, LedgerStamp).Any(); }
            catch (DirectoryNotFoundException) { return false; }   // no folder: nothing of anyone's there
            if (listed) return !OwnLedger(File.ReadAllText(stamp).Trim());
            return !DefaultCopy && (RecorderOwnFiles.Any(f => f is not (Dll12 or Dll11 or Dll9) && File.Exists(Path.Combine(dir, f))) || OurProxyIn(dir)
                // an earlier build's AGS rename cut off before its wrapper went in
                || File.Exists(Path.Combine(dir, AgsRealName)) && !File.Exists(Path.Combine(dir, AgsName)) || OurPreloadIn(dir));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return true; }
    }

    static bool Foreign(Game g) => Foreign(Path.GetDirectoryName(g.ExePath)!);

    /// <summary>Makes the folder this copy's: a copy that <see cref="WritesLedger"/> writes its <see cref="LedgerStamp"/>, the
    /// installed one on Windows deletes its own (another's put there since stays). Throws on a <see cref="Foreign"/> folder first.</summary>
    static void StampLedger(string dir)
    {
        RefuseForeign(dir);
        var stamp = Path.Combine(dir, LedgerStamp);
        if (!WritesLedger) Unstamp(dir);
        else if (File.Exists(stamp) && OwnLedger(File.ReadAllText(stamp).Trim())) Place(stamp, Text(LedgerText(LedgerDir)), replace: true);
        else CreateLedger(stamp, LedgerText(LedgerDir));
    }

    /// <summary>Every write, move or delete in a game folder for a recorder goes through this first: another copy's folder is
    /// never touched (an IOException, so callers leave everything on the record and try again later).</summary>
    static void RefuseForeign(string dir)
    {
        if (Directory.Exists(dir) && Foreign(dir)) throw new ForeignFolderException(dir);
    }

    /// <summary>A copy's first <see cref="LedgerStamp"/>: one put there since the check fails it. Created in place, not renamed
    /// there: Linux's rename replaces a file that appeared meanwhile.</summary>
    internal static void CreateLedger(string path, string text)
    {
        using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        f.Write(System.Text.Encoding.UTF8.GetBytes(text));
    }

    sealed class ForeignFolderException(string dir) : IOException($"the recorder in {dir} is another SCSKiller copy's: left as it is");

    /// <summary>A folder inside a game folder with a marker of another copy's (or one that can't be read).</summary>
    static bool MarkedElsewhere(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir, LedgerStamp).Any() && Foreign(dir); }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>This copy's <see cref="LedgerStamp"/> out of <paramref name="dir"/>, read and deleted through one handle that
    /// shares nothing, so another copy's can't take its place in between; another copy's, or one that names nothing, stays.</summary>
    static void Unstamp(string dir) => DeleteIfText(Path.Combine(dir, LedgerStamp), named =>
    {
        try { return OwnLedger(named.Trim()); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    });

    /// <summary>Deletes <paramref name="path"/> through one handle that shares nothing when its text, read there, passes
    /// <paramref name="ok"/>, after <paramref name="first"/> (which runs while nothing else can write it): never a file changed
    /// or put in its place since. False when it isn't there or is kept; throws when it can't be opened.</summary>
    static bool DeleteIfText(string path, Func<string, bool> ok, Action? first = null)
    {
        var dir = Path.GetDirectoryName(path)!;
        using var h = OpenToDelete(path, dir);
        if (h == null) return false;
        var length = RandomAccess.GetLength(h);
        if (length > 1 << 17) return false;   // longer than any ledger or ini of ours
        var bytes = new byte[length];
        for (int n, at = 0; at < bytes.Length; at += n)
            if ((n = RandomAccess.Read(h, bytes.AsSpan(at), at)) == 0) return false;
        if (!ok(System.Text.Encoding.UTF8.GetString(bytes))) return false;
        first?.Invoke();
        DeleteOpen(h, path, dir);
        return true;
    }

    /// <summary>The file opened to read and delete, sharing nothing; null when it isn't there. Throws when it can't be opened.</summary>
    private static partial Microsoft.Win32.SafeHandles.SafeFileHandle? OpenToDelete(string path, string dir);

    /// <summary>Deletes the file <see cref="OpenToDelete"/> opened, never another one put at its path since.</summary>
    private static partial void DeleteOpen(Microsoft.Win32.SafeHandles.SafeFileHandle h, string path, string dir);

    /// <summary>The recorder folder the proxy reads (recorder.key, recorder.revoked) and writes (refused\): the copy's data
    /// folder, the parent of <see cref="LedgerDir"/>, which the proxy finds through <see cref="LedgerStamp"/> (none: the
    /// installed copy's). Replaceable for tests.</summary>
    internal static string RecorderDir
    {
        get => _recorderDir ?? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(LedgerDir)))!;
        set => _recorderDir = value;
    }

    static string? _recorderDir;

    /// <summary>The exe as the proxy compares it (scskiller.ini's exe_size= and exe_time=, FILETIME UTC); null when it can't
    /// be read.</summary>
    internal static string? Stamp(string exe)
    {
        try
        {
            var f = new FileInfo(exe);
            return $"exe_size={f.Length}\r\nexe_time={f.LastWriteTimeUtc.ToFileTimeUtc()}\r\n";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The signed stamp lines of the exe's scskiller.ini (<see cref="Signed"/>); "" when it has none, null when it
    /// can't be read.</summary>
    static string? IniStamp(string exe)
    {
        try
        {
            var lines = File.ReadAllLines(Path.Combine(Path.GetDirectoryName(exe)!, "scskiller.ini"));
            return string.Concat(lines.Where(l => IniKey(l) is { } k && StampKeys.Contains(k)).Select(l => l + "\r\n"));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The game as its recorder is stamped: itself, or for the first game of a collection its exe swapped for
    /// <see cref="CollectionArmExe"/>. Every stamp site goes through it.</summary>
    Game ArmTarget(Game g) => CollectionArmExe(g) is { } exe ? g with { ExePath = exe } : g;

    /// <summary>For the first game of a collection (<see cref="Collections"/>), the exe its folder's recorder is stamped for:
    /// that of whichever of the collection's listed games ran last (its record's last play or followed exe), each game's
    /// exe one of its own in the same install; while one of them plays, the exe the folder is stamped for (else last stamped
    /// for). Null for any other game.</summary>
    internal string? CollectionArmExe(Game first)
    {
        if (Collections.Member(first.Id) is not { } m || m.All[0].Id != first.Id) return null;
        var games = Games;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first.InstallDir));
        // the store's exe starts, hands over to the other game within seconds and exits: neither moves the target mid-launch
        if (m.All.Any(e => _playing.Contains(e.Id)) && m.All.SelectMany(e => e.Exes).Select(x => Path.Combine(root, x)).ToList() is var exes
            && (exes.FirstOrDefault(x => Attested(first with { ExePath = x }))   // also after an app restart
                ?? exes.FirstOrDefault(x => x.Equals(_collectionArmed.GetValueOrDefault(first.Id), StringComparison.OrdinalIgnoreCase))) is { } held)   // also after a removal
            return held;
        (string Exe, DateTimeOffset At)? last = null;
        foreach (var e in m.All)
        {
            if (games.FirstOrDefault(s => s.Game.Id == e.Id)?.Game is not { } g
                || !GameFiles.DirKey(g.InstallDir).Equals(GameFiles.DirKey(first.InstallDir), StringComparison.OrdinalIgnoreCase)
                || !e.Exes.Contains(Path.GetFileName(g.ExePath), StringComparer.OrdinalIgnoreCase)
                || !Path.GetDirectoryName(Path.GetFullPath(g.ExePath))!.Equals(root, StringComparison.OrdinalIgnoreCase)) continue;
            var r = Store.LoadGame(e.Id);
            var at = new[] { r.LastPlay?.To, r.RunsExeAt, _endedAt.GetValueOrDefault(e.Id) }.Max() ?? DateTimeOffset.MinValue;
            if (last == null || at > last.Value.At) last = (g.ExePath, at);
        }
        return last?.Exe;
    }

    readonly ConcurrentDictionary<string, string> _collectionArmed = new();   // a collection's first game id -> the exe it was last stamped for
    // game id -> the end of its last run seen here: a run already going when the app started leaves no LastPlay
    readonly ConcurrentDictionary<string, DateTimeOffset> _endedAt = new();

    /// <summary>The exe's last launch was refused as another exe than the folder's stamp names, while that stamp, unchanged
    /// since, named another exe of its collection: the user switched games. Its refusal says so, as the stamp moves to it.</summary>
    static void MarkCollectionSwitch(Game g)
    {
        var (exe, file) = (g.ExePath, RefusedFile(g.ExePath));
        try
        {
            var at = File.GetLastWriteTimeUtc(file);
            if (Refused(exe) != "exe not as checked" || at <= File.GetLastWriteTimeUtc(Path.Combine(Path.GetDirectoryName(exe)!, "scskiller.ini"))
                || !Collections.ExesBeside(exe).Any(x => !x.Equals(exe, StringComparison.OrdinalIgnoreCase) && Attested(g with { ExePath = x }))) return;
            File.WriteAllText(file, File.ReadAllText(file).Split(' ', 2)[0] + " " + CollectionSwitchRefusal);
            File.SetLastWriteTimeUtc(file, at);   // LastLaunch and NeverSaw date the launch by it
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // after TraversalKeys, which this file declares first
    static readonly HashSet<string> SignedLineKeys = new(["vendor", "overlay", "ab_play_s", "ab_tag", .. TraversalKeys.Select(k => "traversal_" + k)], StringComparer.OrdinalIgnoreCase);

    static readonly HashSet<string> StampKeys = new(["exe_size", "exe_time", "pid", "pid_time", "risk", .. SignedLineKeys, "sig"], StringComparer.OrdinalIgnoreCase);

    /// <summary>An ini line's key as GetPrivateProfileString reads it (before the first '=', blanks trimmed, any case); null
    /// for a comment, a section or a line without '='.</summary>
    static string? IniKey(string line)
    {
        var at = line.IndexOf('=');
        var key = at < 0 ? "" : line[..at].Trim();
        return key.Length == 0 || key[0] is ';' or '[' ? null : key.ToLowerInvariant();
    }

    /// <summary><paramref name="stamp"/> (and <paramref name="bound"/>, <see cref="BoundTo"/>) with its sig=: the HMAC-SHA1
    /// under the recorder folder's recorder.key (made at the first use) of the exe's and its folder's <see cref="FileKey"/>, them,
    /// <paramref name="root"/> and the key of the folder it names from the exe's <see cref="Final"/> folder, as the proxy
    /// checks it (recorder_key.h). Only SCSKiller, which keeps the key outside every game folder, can write an ini the proxy
    /// records under, and only for that exe and that root. Root "." also keeps the HMAC by the folder's <see cref="PathFolderKey"/>
    /// in the recorder folder's stamps\, for a game whose process makes up folder identities (recorder_key.h made_up_here).</summary>
    internal static string Signed(string exe, string root, string stamp, string bound = "")
    {
        var signed = Sig(exe, root, stamp, bound);
        var dir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        if (root == "." && OperatingSystem.IsWindows() && !Vendors.Wine.Running)
            try
            {
                var here = PathFolderKey(dir);
                var line = Mac(FileKey(exe) + "\n" + here + "\n" + stamp + bound + "root=.\n" + here + "\n");
                var file = Path.Combine(RecorderDir, "stamps", here);
                StampsLocked(() =>
                {
                    // the last few: a stamp signed but not written yet stays good
                    var kept = File.Exists(file) ? File.ReadAllLines(file).Where(l => l.Length == 40 && l != line).Take(7) : [];
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    var temp = $"{file}.{Guid.NewGuid():N}";
                    File.WriteAllLines(temp, kept.Prepend(line));
                    File.Move(temp, file, overwrite: true);
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // only such a game is refused then
        return signed;
    }

    /// <summary><paramref name="change"/> to stamps\ under a mutex the app and the command line share.</summary>
    static void StampsLocked(Action change)
    {
        using var mutex = new Mutex(false, @"Local\SCSKiller-recorder-stamps");
        bool owned;
        try { owned = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new IOException("another SCSKiller process holds it");
        try { change(); }
        finally { mutex.ReleaseMutex(); }
    }

    /// <summary>The folder's path-form stamps out (<see cref="Signed"/>): a revocation or a removal. A failure leaves them, which
    /// admits nothing without a stamped ini.</summary>
    static void DropStamps(string dir)
    {
        try { StampsLocked(() => File.Delete(Path.Combine(RecorderDir, "stamps", PathFolderKey(dir)))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary><see cref="Signed(string, string, string, string)"/>'s text, nothing kept.</summary>
    static string Sig(string exe, string root, string stamp, string bound = "")
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        var rootDir = Path.TrimEndingDirectorySeparator(RootFolder(Final(dir), root));
        return stamp + bound + "sig=" + Mac(FileKey(exe) + "\n" + FileKey(dir) + "\n" + stamp + bound + "root=" + root + "\n" + FileKey(rootDir) + "\n") + "\r\n";
    }

    /// <summary><see cref="Signed(string, string, string, string)"/> for the game's exe and its install root. Throws an
    /// IOException when root= wouldn't name the install root itself where the proxy opens it (<see cref="RootNames"/>).
    /// <paramref name="keep"/> false: only checked, nothing kept in stamps\.</summary>
    internal static string Signed(Game g, string stamp, string bound = "", bool keep = true)
    {
        string here = Final(Path.GetDirectoryName(g.ExePath)!), install = Final(g.InstallDir), root = RootIni(here, install);
        if (!RootNames(here, root, install)) throw new IOException($"root= can't name {install} where the game runs: Wine would open another folder");
        return keep ? Signed(g.ExePath, root, stamp, bound) : Sig(g.ExePath, root, stamp, bound);
    }

    static string Mac(string message) => Convert.ToHexStringLower(HMACSHA1.HashData(RecorderKey(), System.Text.Encoding.Unicode.GetBytes(message)));

    /// <summary>The recorder folder's recorder.key: 64 random hex characters, made once.</summary>
    static byte[] RecorderKey()
    {
        var file = Path.Combine(RecorderDir, "recorder.key");
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(RecorderDir);
            var temp = $"{file}.{Guid.NewGuid():N}";
            WriteKey(temp, RandomNumberGenerator.GetHexString(64, lowercase: true));
            try { File.Move(temp, file); }
            catch (IOException) { File.Delete(temp); }   // another process made it first
        }
        return ReadKey(file);
    }

    /// <summary>A new recorder.key's text into <paramref name="temp"/>, a file it makes, readable by the user alone.</summary>
    internal static partial void WriteKey(string temp, string text);

    /// <summary>recorder.key's bytes. Throws when it can't be read as the user's alone.</summary>
    internal static partial byte[] ReadKey(string file);

    /// <summary>A file's or folder's key, as the proxy makes it (recorder_key.h's file_key): SHA-1 in hex of its file identity
    /// (FILE_ID_INFO: the same through any link, junction or 8.3 name, where a packaged app's process names its exe by the
    /// package's WindowsApps link, not the folder the store installed it to), else of its final path, else of the path as
    /// given (UTF-16LE, A-Z lowered).</summary>
    internal static string FileKey(string path)
    {
        path = Path.GetFullPath(path);
        if (GameFiles.Identity(path) is var (id, final))
        {
            if (id != null) return Convert.ToHexStringLower(SHA1.HashData(id));
            if (final != null) return PathKey(final);
        }
        return PathKey(path);
    }

    /// <summary>A folder's key by its path, as the proxy makes it (recorder_key.h's path_folder_key): <see cref="PathKey"/> of its
    /// <see cref="Final"/> path. The revocation list and stamps\ carry it for a game whose process makes up folder identities.</summary>
    internal static string PathFolderKey(string dir) => PathKey(Final(dir));

    static string PathKey(string path) => Convert.ToHexStringLower(SHA1.HashData(System.Text.Encoding.Unicode.GetBytes(LowerAZ(path))));

    static string LowerAZ(string path) => string.Create(path.Length, path, (to, p) => { for (var i = 0; i < p.Length; i++) to[i] = p[i] is >= 'A' and <= 'Z' ? (char)(p[i] + 32) : p[i]; });

    /// <summary>root= in the recorder's ini: the install root relative to the exe's folder, both as they are on disk
    /// (<see cref="Final"/>, as the proxy resolves them; ASCII ".." steps, which the proxy's ANSI ini read keeps), absolute on
    /// another drive (on Linux: when it isn't above the exe's folder, through Wine's Z:).</summary>
    static string RootValue(Game g) => RootValue(Path.GetDirectoryName(g.ExePath)!, g.InstallDir);

    internal static string RootValue(string exeDir, string installDir) => RootIni(Final(exeDir), Final(installDir));

    /// <summary>root= for the folder <paramref name="install"/> from <paramref name="here"/>, both final paths.</summary>
    private static partial string RootIni(string here, string install);

    /// <summary>The folder root= names from the final path <paramref name="here"/>, as Core opens it.</summary>
    private static partial string RootFolder(string here, string root);

    /// <summary><paramref name="root"/> names <paramref name="install"/> itself, from <paramref name="here"/>, where the proxy
    /// opens it.</summary>
    private static partial bool RootNames(string here, string root, string install);

    /// <summary>A folder's final path (links, junctions and 8.3 names resolved), as the proxy's final_dir reads it; the full
    /// path when it can't be had.</summary>
    static string Final(string dir) => Path.TrimEndingDirectorySeparator(GameFiles.FinalPath(dir) ?? Path.GetFullPath(dir));

    /// <summary>The exe's scskiller.ini carries a valid stamp for the exe as it is now, and its folder isn't revoked: the
    /// proxy would admit a launch there, anti-cheat aside.</summary>
    internal static bool Attested(Game g) => AttestedStamp(g) != null;

    /// <summary>The exe's lines of <see cref="Attested"/>'s stamp, as the ini has them; null when it isn't.</summary>
    static string? AttestedStamp(Game g)
    {
        try
        {
            var dir = Path.GetDirectoryName(g.ExePath)!;
            return IniStamp(g.ExePath) is { } now && SignedStampExe(g, now) is { } exe && exe == Stamp(g.ExePath)
                   && !RevokedKeys().Overlaps([FileKey(dir), PathFolderKey(dir)]) ? exe : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The exe's lines of <paramref name="stamp"/> (an ini's stamp lines, as <see cref="IniStamp"/> reads them) when
    /// its sig= is SCSKiller's for the game's exe and root, whatever exe size and time it names; null otherwise.</summary>
    static string? SignedStampExe(Game g, string stamp)
    {
        if (stamp.Length == 0) return null;
        string Lines(Func<string, bool> keep) => string.Concat(stamp.Split("\r\n").Where(l => IniKey(l) is { } k && keep(k)).Select(l => l + "\r\n"));
        var exe = Lines(k => k is "exe_size" or "exe_time");
        return stamp == Signed(g, exe, Lines(SignedLineKeys.Contains), keep: false) ? exe : null;
    }

    /// <summary>Anti-cheat findings and removals of each game (game id -> how many): a stamp is written only when none came
    /// since the check it rests on began. A collection's games share one recorder, so one count (the first game's).</summary>
    readonly ConcurrentDictionary<string, long> _removalGen = new();

    internal long RemovalGen(string gameId) => _removalGen.GetValueOrDefault(Collections.RecordedBy(gameId) ?? gameId);

    /// <summary>Called while a stamp is published with "saved" (the record saved, the ini not written yet) and "written" (tests).</summary>
    internal Action<string>? AttestStep { get; set; }

    /// <summary>Called right before Reconcile claims an unmarked recorder (tests).</summary>
    internal Action? ClaimStep { get; set; }

    /// <summary>A removal begins: no stamp from a check that began before it is written, and the game is walked in full again
    /// (in memory: the verdict the removal saves, or the reconcile's walk, covers a restart).</summary>
    void NewRemoval(Game g)
    {
        _removalGen.AddOrUpdate(Collections.RecordedBy(g.Id) ?? g.Id, 1, (_, n) => n + 1);
        NotClean(g, save: false);
    }

    /// <summary>The recorder's ini with <paramref name="stamp"/>, taken before a full anti-cheat check that came back clean
    /// (Install's, the reconcile's, the watcher pass's), signed (<see cref="Signed"/>): the one thing that lets the proxy
    /// record. Nothing is written when an anti-cheat finding or a removal came since that check began
    /// (<paramref name="gen"/>, <see cref="RemovalGen"/>), a removal waits, or the game was followed to another exe since; a
    /// game added by hand whose folder isn't the confirmed one, one with a blocking HDR mod, or a proxy that isn't ours gets an
    /// ini without a stamp. 1.2's attestation beside the exe goes. True when the ini now carries the stamp.</summary>
    bool Attest(Game g, GameRecord rec, string? stamp, long gen)
    {
        if (Collections.RecordedBy(g.Id) != null) return false;
        if (VacCheck(g))
        {
            TakeOutNow(g, "VAC found");   // out, the mod put back; a failure is tried again at each pass
            return false;
        }
        if (Foreign(g)) return false;   // that copy's to stamp or end, its pair recorder too: it may still record (ForeignNote)
        if (FramesOnlyRefusal(g) is { } refusal)
        {
            TakeOutNow(g, refusal);   // as VAC: out now or when the game exits
            return false;
        }
        bool Unstamped(string why)
        {
            if (_unstamped.TryGetValue(g.Id, out var said) && said == why) return false;   // once per reason, not at every pass
            _unstamped[g.Id] = why;
            RecorderLog($"{g.Name}: the recorder records nothing: {why}");
            return false;
        }
        // a removal pending for any game of the collection holds the shared recorder's stamp back too
        if (RemovalGen(g.Id) != gen || rec.RecorderRollback || (Collections.Member(g.Id) is { } col ? col.All.Select(e => e.Id) : [g.Id]).Any(_rollbacks.ContainsKey))
            return Unstamped("a removal came since its check began, or one waits");
        if (CollectionArmExe(g) is { } target ? !target.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase)
            : rec.RunsExe is { } runs && !runs.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase))
            return Unstamped($"the game runs another exe than {Path.GetFileName(g.ExePath)}");
        var followedFrom = rec.RunsExeWas is { } old && !old.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase)
            && !Games.Any(s => s.Game.Id != g.Id && s.Game.ExePath.Equals(old, StringComparison.OrdinalIgnoreCase)) ? old : null;
        var dir = Path.GetDirectoryName(g.ExePath)!;
        foreach (var armed in Directory.Exists(dir) ? Directory.EnumerateFiles(dir, ArmedFile + "*") : [])
            try { File.Delete(armed); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecorderLog($"{g.Name}: couldn't delete {armed}: {e.Message}"); }
        var was = Attested(g);
        string? signed = null, unsigned;
        try
        {
            unsigned = stamp == null ? $"{Path.GetFileName(g.ExePath)} couldn't be read when its check began"
                : Unconfirmed(g) || g.Store == Core.Store.Manual && Manual?.ConfirmedNow(g) != true ? SkipManual
                : !OurProxyIn(dir) && !VulkanIn(g, rec) ? $"SCSKiller's proxy isn't in {dir}"
                : BlockingMod(g) != null ? SkipShaderMod : null;
            if (unsigned == null) signed = Signed(g, stamp!, SignedLines(g, rec));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { unsigned = $"its stamp couldn't be signed: {e.Message}"; }
        if (Collections.Member(g.Id) != null) MarkCollectionSwitch(g);   // before the stamp names this exe
        UpdateRecorderIni(g, rec, signed ?? "", () => RemovalGen(g.Id) == gen);
        if (RemovalGen(g.Id) != gen)   // a finding came while the ini was written: it goes again before the lock is let go
        {
            StripIni(dir, Store, rec.RecorderChained?.Name, g.InstallDir, g.Name, RecorderLog);
            try { Store.SaveGame(g.Id, rec); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecorderLog($"{g.Name}: couldn't save the recorder's record: {e.Message}"); }
            return Unstamped("a removal came while its stamp was written");
        }
        if (signed == null) return Unstamped(unsigned ?? "its stamp couldn't be signed");
        if (IniStamp(g.ExePath) != signed)
            return Unstamped($"its scskiller.ini in {dir} couldn't be written (one that isn't SCSKiller's is left as it is)");
        IniState owner;
        try { owner = JudgeIni(dir, Store, rec.RecorderChained?.Name, g.InstallDir).State; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { owner = IniState.Unknown; RecorderLog($"{g.Name}: whose the scskiller.ini in {dir} is couldn't be recorded: {e.Message}"); }
        if (owner != IniState.Ours)   // ours by its stamp, not by ownership (another encoding of it): refused while it's there
        {
            Revoke(dir, true, RecorderLog);
            return Unstamped($"the scskiller.ini in {dir} isn't SCSKiller's (left as it is)");
        }
        _unstamped.TryRemove(g.Id, out _);
        Revoke(dir, false, RecorderLog);
        if (followedFrom != null && !Games.Any(s => s.Game.ExePath.Equals(followedFrom, StringComparison.OrdinalIgnoreCase)))
            try { File.Delete(RefusedFile(followedFrom)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var moved = false;
        if (Collections.Member(g.Id) != null)   // the folder's one stamp names this exe: the collection's other exes' refusals are stale
        {
            foreach (var x in Collections.ExesBeside(g.ExePath).Where(x => !x.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase)))
                try { File.Delete(RefusedFile(x)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            moved = !was;
            _collectionArmed[g.Id] = Path.GetFullPath(g.ExePath);
        }
        if (!was) RecorderLog($"{g.Name}: recorder stamped for {Path.GetFileName(g.ExePath)}");
        if (moved && Games.FirstOrDefault(s => s.Game.Id == g.Id)?.Game is { } listed) Refresh(listed);   // its page's last launch note reads the stamped exe
        // a collection's other games too: their shaders go in the keys file, and their records come out of this recording
        foreach (var x in Collections.Member(g.Id) is { } m ? m.All.Select(e => Games.FirstOrDefault(s => s.Game.Id == e.Id)?.Game).OfType<Game>() : [g])
            IndexForRecorder(x);
        return true;
    }

    /// <summary>The game page's note when the scskiller.ini beside the exe isn't SCSKiller's (<see cref="JudgeIni"/>): the
    /// recorder there is never stamped, and the file is left as it is.</summary>
    string? ForeignIni(string dir, GameRecord rec)
    {
        var (state, _) = JudgeIni(dir, Store, rec.RecorderChained?.Name, rec.RecorderInstallDir);
        return state == IniState.NotOurs ? NoteForeignIni : null;
    }

    internal const string NoteForeignIni = "records nothing: the scskiller.ini in its folder isn't SCSKiller's, so it's left as it is";

    readonly ConcurrentDictionary<string, string> _unstamped = new();   // game id -> why it was last logged unstamped

    /// <summary>Why the proxy passed the exe's last launch through, from the file it leaves in the recorder folder's refused\
    /// ("&lt;unix ms&gt; &lt;reason&gt;"; an admitted launch deletes it), as the proxy wrote it; null = none.</summary>
    internal static string? Refused(string exe)
    {
        string text;
        try { text = File.ReadAllText(RefusedFile(exe)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return text.Split(' ', 2) is [_, var w] && w.Trim() is { Length: > 0 } why ? why : null;
    }

    /// <summary>The proxy's refusal file for an exe: its <see cref="FileKey"/> in the recorder folder's refused\.</summary>
    internal static string RefusedFile(string exe) => Path.Combine(RecorderDir, "refused", FileKey(exe));

    /// <summary>The recorder was in place before the last watched run started (a creation time not known doesn't show it), yet
    /// wrote neither its log nor a refusal during it: no device of the game reached it.</summary>
    internal static bool NeverSaw(string exe, string exeDir, string dllName, PlayWindow? run)
    {
        if (run is not { } p) return false;
        var from = p.From.UtcDateTime;
        static DateTime? Written(string f) => File.Exists(f) ? File.GetLastWriteTimeUtc(f) : null;
        var dll = Path.Combine(exeDir, dllName);
        // the creation time: a copy keeps the build's write time
        return File.Exists(dll) && FileTimes.CreatedUtc(dll) < from
            && !(Written(Path.Combine(exeDir, "scskiller.log")) >= from) && !(Written(RefusedFile(exe)) >= from);
    }

    /// <summary>A record's scskiller.ini hash (1.2 kept it in <see cref="GameRecord.RecorderFiles"/>, beside the dll's) moves to its
    /// folder's <see cref="IniOwner"/>, the folder's entry first: one there already wins, so running it again, after a crash
    /// or in another process (the uninstall hook, an offline session's cleanup helper), changes nothing. It's still to be
    /// taken out when the record names no dll of the recorder and no offline session (whose cleanup takes it). An earlier
    /// build's mark that the ini is the user's (<see cref="GameRecord.IniIsUsers"/>) goes to the folder the record names, or,
    /// when it names none, to the game's exe folder at its next evaluation.</summary>
    internal static void MigrateInis(AppStore store, TimeSpan? wait = null)
    {
        var games = Path.Combine(store.DataDir, "games");
        foreach (var id in (Directory.Exists(games) ? Directory.GetDirectories(games) : []).Select(d => AppStore.GameId(Path.GetFileName(d))))
        {
            var rec = store.LoadGame(id);
            var hash = rec.RecorderFiles.GetValueOrDefault("scskiller.ini");
            if (hash == null && !rec.IniIsUsers) continue;
            var folder = (rec.RecorderExe ?? rec.RecorderMoveFrom ?? rec.OfflineSession?.Exe) is { } at ? Path.GetDirectoryName(at)! : null;
            if (folder != null && !Directory.Exists(folder)) continue;   // away: kept on the record for later
            if (folder != null)
            {
                // the user's mark, or SCSKiller's hash, unless a newer build's entry says whose it is since
                if (rec.IniIsUsers) SetIni(store, folder, e => e ?? new IniOwner(folder, Users: true), wait);
                else if (hash != null)
                {
                    // an offline session's goes with its cleanup, which may not have run yet (the session may still be live)
                    var remove = rec.OfflineSession == null && !rec.RecorderFiles.Keys.Any(f => f is Dll12 or Dll11 or Dll9);
                    SetIni(store, folder, e => e ?? new IniOwner(folder, hash, false, remove, rec.RecorderChained?.Name,
                        rec.RecorderInstallDir ?? rec.OfflineSession?.InstallDir), wait);
                }
            }
            else if (folder == null && rec.IniIsUsers) continue;   // no folder named: the game's next evaluation takes the mark to its exe folder
            rec.IniIsUsers = false;
            rec.RecorderFiles.Remove("scskiller.ini");
            store.SaveGame(id, rec, wait);
        }
    }

    /// <summary>1.2's ledger (this copy's <see cref="LedgerDir"/>, never another copy's), without which none of its proxies
    /// records: deleted at every start, before anything else touches a game. An entry held without delete sharing gets a new
    /// &lt;entry&gt;.revoked beside it, on which its 1.2 proxy refuses, and the next start tries again.</summary>
    void DeleteLedger()
    {
        var ledger = LedgerDir;
        try { if (Directory.Exists(ledger)) Directory.Delete(ledger, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            RecordersLog(Store.DataDir, $"couldn't delete {ledger} ({e.Message}): its entries are revoked, and it's deleted at the next start");
            try
            {
                foreach (var entry in Directory.EnumerateFiles(ledger).Where(f => Path.GetExtension(f) == ""))
                    try { File.WriteAllText(entry + ".revoked", ""); }
                    catch (Exception x) when (x is IOException or UnauthorizedAccessException) { RecordersLog(Store.DataDir, $"couldn't revoke {entry}: {x.Message}"); }
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { RecordersLog(Store.DataDir, $"couldn't list {ledger}: {x.Message}"); }
        }
    }

    /// <summary>SCSKiller's scskiller.ini out of <paramref name="dir"/>, also beside a running game: without its stamp the next
    /// launch is a pass-through. Only one <see cref="JudgeIni"/> calls SCSKiller's is deleted, its bytes read again right
    /// before; anything else is never written or deleted. The folder's entry (<see cref="IniOwner"/>) says what's left: one
    /// that can't be read and SCSKiller's that couldn't be deleted are to be taken out later, by the next removal through the
    /// game's record, which keeps naming the folder (<see cref="RemoveRecorder"/>), and their folder is on the recorder
    /// folder's revocation list (<see cref="Revoked"/>), on which the proxy refuses; so is the folder of a user's ini with a
    /// sig= key, while it's there.
    /// <paramref name="chain"/> and <paramref name="installDir"/>: what an untracked ini's next= and root= must name (the
    /// entry's when null).</summary>
    static void StripIni(string dir, AppStore store, string? chain, string? installDir, string name, Action<string> log, TimeSpan? wait = null)
    {
        RefuseForeign(dir);
        var ini = Path.Combine(dir, "scskiller.ini");
        var (state, judged) = JudgeIni(dir, store, chain, installDir, wait);
        IniOwner Pending(IniOwner? e, string? tracked) =>
            (e ?? new IniOwner(dir)) with { Tracked = tracked, Remove = true, Chain = chain ?? e?.Chain, Install = installDir ?? e?.Install };
        switch (state)
        {
            case IniState.Absent:
                Revoke(dir, false, log);
                return;
            case IniState.Unknown:
                SetIni(store, dir, e => Pending(e, e?.Tracked ?? ""), wait);   // whose it is is told later
                Revoke(dir, true, log);
                log($"{name}: couldn't read {ini}: the recorder there is refused until it can be");
                return;
            case IniState.NotOurs:   // marked the user's (JudgeIni); nothing of SCSKiller's is queued there any more
                SetIni(store, dir, e => e is { Remove: true } ? e with { Remove = false, Tracked = null } : e, wait);
                // a stamp in it (an edit of SCSKiller's, in any encoding the proxy's reader takes) may still be valid: refused while it's there
                Revoke(dir, true, log);
                return;
        }
        SetIni(store, dir, e => Pending(e, Convert.ToHexString(SHA256.HashData(judged!))), wait);   // tracked until it is out
        try
        {
            if (!IniUnchanged(ini, judged)) throw new IOException("it changed while it was checked");
            File.Delete(ini);
            SetIni(store, dir, _ => null, wait);
            Revoke(dir, false, log);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Revoke(dir, true, log);
            log($"{name}: couldn't take {ini} out ({e.Message}): the recorder there is refused until it's gone");
        }
    }

    static readonly object _revokedLock = new();

    /// <summary>The recorder folder's recorder.revoked: the <see cref="FileKey"/> and <see cref="PathFolderKey"/> of each folder
    /// whose scskiller.ini couldn't be taken out (one a line, ASCII). The proxy refuses in them whatever the ini says.</summary>
    internal static string Revoked => Path.Combine(RecorderDir, "recorder.revoked");

    /// <summary>The keys on <see cref="Revoked"/>; empty when it doesn't exist.</summary>
    static HashSet<string> RevokedKeys() => File.Exists(Revoked) ? File.ReadAllLines(Revoked).Where(l => l.Length > 0).ToHashSet() : [];

    internal const string RevokedMutex = @"Local\SCSKiller-recorder-revoked";

    /// <summary><paramref name="dir"/> onto the revocation list, or off it; the file goes once empty. Another process's change
    /// meanwhile is kept: the list is read and replaced under a mutex. A failure is logged.</summary>
    static void Revoke(string dir, bool on, Action<string> log)
    {
        string[] entries = [FileKey(dir), PathFolderKey(dir)];
        var file = Revoked;
        lock (_revokedLock)
            try
            {
                using var mutex = NamedMutex.Open(RevokedMutex);   // the app and the command line
                bool owned;
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) throw new IOException("another SCSKiller process holds it");
                try
                {
                    if (on) DropStamps(dir);   // the list holds it off anyway
                    var lines = RevokedKeys().ToList();
                    if (entries.All(e => lines.Contains(e) == on)) return;
                    lines.RemoveAll(l => entries.Contains(l));
                    if (on) lines.AddRange(entries.Distinct());
                    if (lines.Count == 0)
                    {
                        File.Delete(file);
                        return;
                    }
                    Directory.CreateDirectory(RecorderDir);
                    var temp = $"{file}.{Guid.NewGuid():N}";
                    File.WriteAllLines(temp, lines);
                    File.Move(temp, file, overwrite: true);
                }
                finally { mutex.ReleaseMutex(); }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"couldn't update {file}: {e.Message}"); }
    }

    readonly object _pollGate = new();
    long _polledAt;   // Environment.TickCount64 of the last poll
    long _polledListed;   // the _listed the last poll that ended saw: _playing holds that listing's running games

    /// <summary>A start only raises GameChanged (nothing re-read: the recorder may be writing). Callers take turns.</summary>
    public void PollGames()
    {
        lock (_pollGate)
        {
            Volatile.Write(ref _polledAt, Environment.TickCount64);
            var listed = Volatile.Read(ref _listed);
            PollNow();
            Volatile.Write(ref _polledListed, listed);
        }
    }

    void PollNow()
    {
        var games = Games;
        if (games.Count == 0) { PutBackSetAside(); return; }
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
        if (_lastPoll == null)
        {
            foreach (var s in games.Where(s => !now.Contains(s.Game.Id) && Store.LoadGame(s.Game.Id) is var r && (D3DSCachePending(s.Game.Id, r) || MovedD3DSCache(s.Game, r) != null)))
                MergeD3DSCacheSoon(s.Game);
            var idle = games.Where(s => !now.Contains(s.Game.Id)).Select(s => s.Game).ToList();
            _ = Task.Run(() =>
            {
                foreach (var g in idle)
                    try
                    {
                        if (AdoptStranded(g)) MergeD3DSCacheSoon(g);
                    }
                    catch (Exception e) { Log?.Report($"{g.Name}: the stranded D3DSCache check failed: {e.Message}"); }
            });
        }
        foreach (var (id, room) in _foldRoom)
            if (room.Retry && !now.Contains(id) && !Warming(id) && D3DSCache.FreeBytes(room.Dir) >= room.Need && games.FirstOrDefault(s => s.Game.Id == id) is { } waits
                && _foldRoom.TryRemove(id, out _))
                MergeD3DSCacheSoon(waits.Game);
        _lastPoll = at;
        // asked at every poll while the game runs until decided: a launcher of its folder may start the game minutes later
        _toFollow.UnionWith(started);
        _toFollow.IntersectWith(now);
        foreach (var id in _rendererSeen.Keys.Where(id => !_toFollow.Contains(id)).ToList()) _rendererSeen.Remove(id);
        foreach (var id in _startsRenderer.Keys.Where(id => !_toFollow.Contains(id)).ToList()) _startsRenderer.Remove(id);
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
        foreach (var (id, o) in _unrealOther)
            if (games.FirstOrDefault(x => x.Game.Id == id) is { } g) WatchUnrealOther(g.Game, o, at);
        BeforeAfterTick(games, now, at);
        if (started.Count + ended.Count == 0) { PutBackSetAside(); return; }
        foreach (var id in ended)
            if (_runs.Remove(id, out var run))
            {
                var rec = Store.LoadGame(id);
                rec.LastPlay = run;
                // the first run after the warm's fold, read before the exit's fold can change the game's db, and only from the folder
                // stamped when the run wrote it last: through another path the runtime used another one
                if (games.FirstOrDefault(x => x.Game.Id == id)?.Game is { } played)
                {
                    var identityDue = rec is { D3DSCacheStamp: not null, D3DSCacheStampAt: { } stampAt } && stampAt <= run.From && !(rec.D3DSCacheLaunchAt >= rec.WarmedAt);
                    var hitsDue = rec is { D3DSCacheFoldKeys: { } fk, Plan: not null } && fk.At <= run.From && rec.HitCheckAt != fk.At;
                    // the game's adapter db after the run, read once for every report that needs it, from the folder the run used
                    var last = (identityDue || hitsDue) && !RunsUnwatched(played, waitingRuns: false) ? D3DSCache.GameFolder(Path.Combine(LocalAppData, "D3DSCache"), played) : null;
                    bool Used(string? folder) => last != null && folder is { } f && IsFolderName(f) && string.Equals(Path.GetFileName(last.Value.Dir), f, StringComparison.OrdinalIgnoreCase);
                    var ids = new List<AppIdentity>();
                    var idDb = identityDue && Used(rec.D3DSCacheStampFolder) ? D3DSCache.AdapterDb(last!.Value.Dir) : null;
                    // the db the fold read, by name: a folder may hold another adapter's db too
                    var hitDb = hitsDue && Used(rec.D3DSCacheFoldKeys!.Folder) && Path.Combine(last!.Value.Dir, rec.D3DSCacheFoldKeys.Db) is var h && File.Exists(h) ? h : null;
                    var hits = hitDb != null ? new D3DSCache.HitCheckRead(rec.D3DSCacheFoldKeys!) : null;
                    bool[]? idRead = null, hitRead = null;
                    if (idDb != null && string.Equals(idDb, hitDb, StringComparison.OrdinalIgnoreCase))
                        (idRead, hitRead) = D3DSCache.PostExit(idDb, [D3DSCache.AppIdQuery(ids), .. hits!.Queries]) is { } r ? (r[..1], r[1..]) : (null, null);
                    else
                    {
                        if (idDb != null) idRead = D3DSCache.PostExit(idDb, D3DSCache.AppIdQuery(ids));
                        if (hitDb != null) hitRead = D3DSCache.PostExit(hitDb, hits!.Queries);
                    }
                    if (identityDue)
                        (rec.D3DSCacheLaunch, rec.D3DSCacheLaunchAt) = (IdentityReport.Of(rec.D3DSCacheStamp!, idRead is [true] && ids.Distinct().Count() == 1 ? ids[0] : null,
                            AsdReport.Of(D3DSCache.Delivered(played.ExePath), OwnCompiler())), rec.D3DSCacheStampAt);
                    if (hitsDue)
                        (rec.HitCheck, rec.HitCheckAt, rec.HitCheckRun) = (hitRead is { } ran && ran.All(x => x) ? hits!.Of(run, rec.Plan!.Stats) : null, rec.D3DSCacheFoldKeys!.At, run);
                }
                Store.SaveGame(id, rec);
            }
        foreach (var id in ended)
            if (_unrealOther.Remove(id, out var o) && games.FirstOrDefault(x => x.Game.Id == id) is { } g)
                try { FollowUnrealOther(g.Game, o); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{g.Game.Name}: couldn't record the exe it runs: {e.Message}"); }
        foreach (var id in ended)
        {
            _absent.Remove(id);
            _endedAt[id] = at;
        }
        _playing = [.. was.Except(ended), .. started];
        foreach (var id in started)
        {
            var putBack = PutBack(id, late: true);
            var pair = BeforeAfterStarted(id);   // outside the lock: it saves the record
            GameState? state = null;
            lock (_lock)
                if (_games.FindIndex(x => x.Game.Id == id) is var i and >= 0) _games[i] = state = _games[i] with { Playing = true, BeforeAfter = pair ?? _games[i].BeforeAfter };
            if (state != null) GameChanged?.Invoke(state);
            if (putBack) ShowKept(id);
            if (AppCache != null && state is { AntiCheat: AntiCheat.None } && !IsUnchecked(state)) _learning[id] = Task.Run(() => LearnWhilePlaying(state.Game));
            if (state != null) CheckElevated(state);
            SteamPlaceStarted(id);
        }
        foreach (var s in games.Where(s => ended.Contains(s.Game.Id)))
        {
            // a collection's other game: its first game's import hands it this launch's records
            if (Collections.RecordedBy(s.Game.Id) is { } by && games.FirstOrDefault(x => x.Game.Id == by) is { } first) Refresh(Current(first.Game));
            var g = Current(s.Game);   // a store updates a game before it starts: its first exit after that shows it Stale
            KeepAside(g.Id, Store.LoadGame(g.Id), g.Name);   // what the game compiled into its files, and files it made again
            Refresh(g);
            if (Games.FirstOrDefault(x => x.Game.Id == g.Id) is { RecorderRefused: { } note, OfflineRunning: false } && Store.LoadGame(g.Id).LastPlay is { } last)
            {
                // a missing file reads as 1601: no refusal
                var why = File.GetLastWriteTimeUtc(RefusedFile(g.ExePath)) >= last.From.UtcDateTime ? Refused(g.ExePath) : null;
                Log?.Report($"{g.Name}: the launch recorded nothing: " + (why != null ? $"the recorder refused it: {why}"
                    : note is NeverSawNote or NeverSawDx11Note ? "the recorder never loaded, no scskiller.log written during the run" : $"no refusal for its exe, the page says: {note}"));
            }
            BeforeAfterExited(g);
            SteamPlaceSoon(g.Id, exited: true);
            MergeD3DSCacheSoon(g);
            StartSharing([g]);   // also without a new recording: the command line's compile indexes a build but can't share
            if (ManageRecorders)   // a recorder change that waited for the game to exit (Reconcile)
                try { ReconcileRecorders(g.Id); }
                catch (Exception e) { Log?.Report($"{g.Name}: reconciling the recorder failed: {e.Message}"); }
        }
        if (ended.Count > 0) KeepList();
        PutBackSetAside();   // after this poll's starts and exits: a run going on keeps its files set aside
    }

    public void RefreshGame(string gameId) => Refresh(Find(gameId).Game);

    public void RefreshCacheSizes()
    {
        if (AppCache == null) return;
        foreach (var s in Games)
        {
            var rec = Store.LoadGame(s.Game.Id);
            if ((CacheOnDiskOf(s.Game, s.Engine, rec), CacheInUseOf(s.Game, s.Engine, rec), WindowsCacheOf(s.Game, rec)) is var (size, used, windows)
                    && size == s.CacheOnDisk && used == s.CacheInUse && windows == s.WindowsCache) continue;
            var now = s with { CacheOnDisk = size, CacheInUse = used, WindowsCache = windows };
            lock (_lock)
            {
                var i = _games.FindIndex(x => x.Game.Id == s.Game.Id);
                if (i < 0 || _games[i] != s) continue;   // re-evaluated meanwhile: that state is newer
                _games[i] = now;
            }
            GameChanged?.Invoke(now);
        }
    }

    private static partial TimeSpan UserIdleTime();

    /// <summary>Ask's sign-in check found stale games: starts this copy's app with <see cref="ScheduledTask.DriverUpdatedArg"/>,
    /// which notifies. The app it started, or null when there is none or it couldn't start.</summary>
    public static partial string? StartDriverUpdated();

    sealed class Reporter<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
