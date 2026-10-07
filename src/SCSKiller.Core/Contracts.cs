// Shared contracts; numbering and semantics are documented in ARCHITECTURE.md.
namespace SCSKiller.Core;

public enum Store { Steam, Epic, Xbox, Other, EA, Manual }   // Manual: added by the user (ManualSource)

/// <summary>A game install. <see cref="Id"/> is stable across runs ("steam:2909400", "epic:&lt;AppName&gt;").
/// <see cref="ExePath"/> is the process that creates the D3D12 device (for Unreal the *-Win64-Shipping.exe or the
/// fork's equivalent under Binaries/Win64), never a launcher stub; its file name is the driver-cache identity.</summary>
public sealed record Game(string Id, string Name, Store Store, string InstallDir, string ExePath,
    string? Version = null);   // store build id (Steam buildid, Epic AppVersionString): a change means the game was patched

public interface IGameSource
{
    Store Store { get; }
    IReadOnlyList<Game> Discover();
}

public enum AntiCheat { None, EasyAntiCheat, BattlEye, Other }

public sealed record EngineInfo(
    string Family,          // "Unreal", "Carved", "FromSoftware", "Unity", "RE Engine"
    string Version,         // "4.26", "5.1"
    string? Fork,           // engine-reader specific fork id, e.g. CUE4Parse "GAME_FinalFantasy7Rebirth"
    string GraphicsApi,     // "D3D12", "D3D11", "Vulkan…" or "D3D11 or D3D12" (see Planner.Check)
    bool Encrypted,         // shader content unreadable without a key
    string? Unsupported,    // why it can't be indexed (e.g. "shaders stored inside materials"); null = indexable
    bool NoRtPipelines = false,    // the game never builds a ray tracing state object (Unreal: r.RayTracing or r.RayTracing.AllowPipeline=0): its DXIL libraries go unused
    bool NoRayTracing = false);    // ray tracing is off altogether (Unreal: r.RayTracing=0), inline too; implies NoRtPipelines

/// <summary>Shader stage, numbered like D3D12_PIPELINE_STATE_SUBOBJECT_TYPE (the proxy's db uses the same numbers).</summary>
public enum Stage { Vertex = 1, Pixel = 2, Domain = 3, Hull = 4, Geometry = 5, Compute = 6, Amplification = 24, Mesh = 25, Library = 100 }

/// <summary>A signature element. <paramref name="ReadMask"/>: the components an input is actually read (DXBC ISGN
/// ReadWriteMask, DXIL ISG1 AlwaysReads), 0xFF = unknown (outputs, or a reader that doesn't parse it).
/// <paramref name="Interpolation"/>: a pixel shader input's interpolation mode (DXIL PSV0: 1 constant, 2 linear,
/// 3 linear centroid, 4 noperspective, ...), 0 = unknown. AMD compiles a VS for how its PS consumes it (ARCHITECTURE.md).</summary>
public sealed record SigElement(string Semantic, int Index, int Register, byte Mask, int SysValue, int CompType,
    byte ReadMask = SigElement.UnknownReadMask, int Interpolation = 0)
{
    public const byte UnknownReadMask = 0xFF;
}
public sealed record Binding(string Class, int Space, int Lower, int Count);   // Class cbv|srv|uav|sampler, Count -1 = unbounded
public sealed record ResourceCounts(int Cb, int Srv, int Uav, int Sampler,      // UE's packed per-stage counts
    int Flags = 0);   // UE5 root-signature inputs from the shader's optional data (Unreal.UeFlags); 0 elsewhere

/// <summary>One unique shader. <see cref="Sha1"/> = lowercase hex SHA-1 of the DXBC/DXIL container bytes (sliced to
/// the container size at offset 24), the same key the proxy records.</summary>
public sealed record ShaderInfo(string Sha1, Stage Stage, string ShaderModel, int Size, ResourceCounts Counts,
    IReadOnlyList<Binding> Bindings, IReadOnlyList<SigElement> Inputs, IReadOnlyList<SigElement> Outputs,
    int GsInputPrimitive = 0,   // geometry shaders: D3D_PRIMITIVE of the input (1 point, 2 line, 3 triangle, 6 line_adj, 7 tri_adj); 0 otherwise
    string? RootSignature = null,   // SHA-1 of the root-signature blob the game creates from the one the shader carries (RTS0), servable through ReadShaders; null = none
    bool InlineRayTracing = false,   // traces rays inline (RayQuery: Dxbc.InlineRayTracing)
    byte[]? EngineHeader = null);   // the engine's own header stored beside the shader, read by its root-signature rule (Dagor: dxil::ShaderHeader)

/// <summary>A group of shaders that can combine. <see cref="IsPipeline"/>: the game shipped this exact stage set as one
/// pipeline (e.g. a PSO cache record), so no pairing is needed. <see cref="Layout"/>: the input layout the game creates that
/// pipeline with (a shipped PSO cache's vertex declaration), as exact for its vertex shader as a recording's; null = unknown.</summary>
public sealed record ShaderMap(string Hash, string Library, string Platform, IReadOnlyList<string> Shaders, bool IsPipeline = false,
    IReadOnlyList<Planning.PsoDb.LayoutElem>? Layout = null);

public sealed record ShaderIndex(
    string ContentHash,     // changes when the game's shader libraries change (game patch) -> plans built on it are stale
    IReadOnlyList<string> Platforms,                     // e.g. PCD3D_SM5, PCD3D_SM6
    IReadOnlyDictionary<string, ShaderInfo> Shaders,     // by Sha1
    IReadOnlyList<ShaderMap> Maps);

/// <summary>One per engine family.</summary>
public interface IEngineReader
{
    EngineInfo? Detect(Game game);   // null = not this engine
    /// <summary>What Detect reads outside the game files (user settings, logs, the store's launch config), as a stamp: a
    /// cached Detect result is redone when it changes. <paramref name="engine"/>: the engine detected before, null when
    /// not known (every reader's stamp).</summary>
    string DetectStamp(Game game, EngineInfo? engine) => "";
    /// <summary>The game files Index reads when a user setting picks them (not fixed by the exe or the store build), as a stamp
    /// (path, size, write time): a warm is stale once it changes. Cheap, called on every evaluation; "" when there are none.</summary>
    string IndexStamp(Game game) => "";
    ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct);
    /// <summary>Streams container bytes (hash convention above) for the requested shaders; the sink is called sequentially.</summary>
    void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct);
}

// Adding a GPU vendor = one IGpuVendorBackend + its VendorCaps; the planner and warmer only read the caps.
public enum GpuVendor { Unknown = 0, Nvidia = 0x10DE, Amd = 0x1002, Intel = 0x8086, Qualcomm = 0x5143 }

public sealed record GpuInfo(GpuVendor Vendor, string Name, string DriverVersion, long AdapterLuid, ulong DedicatedVideoMemory);

/// <summary>What a vendor's D3D12 shader cache does. Measured, never assumed: NVIDIA values come from the NVIDIA probes.</summary>
public sealed record VendorCaps(
    string Profile,                 // e.g. "nvidia-1"; stored in plans, a different profile makes a plan stale
    bool CacheKeyedByExeName,       // true: warm from a staged exe named like the game (no injection); false: in-game warm only
    bool StateIndependentCache,     // true: fixed-function state + input layout don't change the compiled result -> synthesized templates allowed
    bool CacheSizeConfigurable,
    bool PerStageCache = false,     // true: each stage is compiled and cached on its own (+ root signature, + for VS its read input
                                    // layout/topology on AMD): a plan needs each stage unit once, not every VS x PS pair
    RtCacheGranularity RtCacheGranularity = RtCacheGranularity.WholeObject,  // what the driver caches of a ray tracing state object
    bool PackageKeyed = false);     // true: a packaged (Xbox) game's cache key follows its package identity: its warm runs with it

/// <summary>What a vendor's driver caches of a DXR state object (selftest dxr, ARCHITECTURE.md). <see cref="WholeObject"/>: only an
/// exact repeat of the whole object hits (AMD; the safe default): the warm replays recorded objects. <see cref="Collection"/>:
/// each collection is cached on its own and linking cached ones is cheap (NVIDIA): the planner may synthesize collections.</summary>
public enum RtCacheGranularity { WholeObject, Collection }

public sealed record CacheUsage(string Path, long BytesOnDisk, bool UpperBound);   // NVIDIA pre-sizes files: upper bound only
/// <summary>Bytes: null = unlimited, 0 = cache disabled. IsDriverDefault: no override set; Bytes is then the driver's default size.</summary>
public sealed record CacheLimit(long? Bytes, bool IsDriverDefault);

public interface IGpuVendorBackend
{
    GpuVendor Vendor { get; }
    GpuInfo Gpu { get; }            // DriverVersion in the vendor's own notation ("610.88")
    VendorCaps Caps { get; }
    CacheUsage GetCacheUsage();
    CacheLimit? GetCacheLimit();                 // null = not readable
    void SetCacheLimit(CacheLimit limit);        // global driver setting, needs admin: callers must have the user's explicit OK
    /// <summary>Per-application cache files (NVIDIA DXCache, AMD DxcCache/DxCache); null when the vendor's cache isn't
    /// split per application.</summary>
    IAppCache? AppCache => null;
    /// <summary>NVIDIA App's "Automatic Shader Compilation (Beta)": the driver setting plus whether the driver's idle compile
    /// task is ready; null = not readable or not this vendor.</summary>
    AutoShaderState? GetAutoShaderCompilation() => null;
    /// <summary>Switches it the way the NVIDIA App does (driver setting + the driver's idle task). Global, needs admin:
    /// callers must have the user's explicit OK.</summary>
    void SetAutoShaderCompilation(AutoShaderCompilation level) =>
        throw new NotSupportedException($"Auto Shader Compilation is an NVIDIA feature ({Gpu.Name})");
}

/// <summary>NVIDIA App's Automatic Shader Compilation: Off, or on with that "System Utilization" (the App's default is Medium).</summary>
public enum AutoShaderCompilation { Off, Low, Medium, High }
/// <summary>TaskReady: the driver's idle compile task exists and is enabled (the setting alone compiles nothing).</summary>
public sealed record AutoShaderState(AutoShaderCompilation Level, bool TaskReady);

/// <summary>A driver cache split into per-application files (NVIDIA DXCache, AMD DxcCache/DxCache). A key names one
/// application's files; which keys belong to a game is learned from the files a process named like it holds open.
/// Exposed as <see cref="IGpuVendorBackend.AppCache"/> (null = the vendor's cache isn't per application).</summary>
public interface IAppCache
{
    IReadOnlyList<FileInfo> FilesOf(IEnumerable<string> keys);
    long SizeOf(IEnumerable<string> keys);
    /// <summary>Keys of the cache files a running process with this exe file name has open now (no content read).</summary>
    IReadOnlySet<string> KeysOpenBy(string exeFileName);
    /// <summary>Deletes every file of these keys, or none: InvalidOperationException ("files in use by ...") when one is
    /// open. Returns how many were deleted.</summary>
    int Delete(IEnumerable<string> keys);
}

/// <summary>One kind of a game's shader cache on disk (<see cref="IScsKiller.GameCaches"/>).</summary>
public sealed record CachePart(string Name, IReadOnlyList<string> Files, long Bytes);

/// <summary>A scskiller.db written by the proxy in record mode (format: proxy/proxy.cpp header).</summary>
public sealed record Recording(string DbPath);

public enum Readiness { Ready, NeedsRecording, Unsupported }
public sealed record PlanCheck(Readiness Readiness, string Reason);

public sealed record PlanStats(long Recorded, long Generated, long SynthesizedTemplates, long RootSignatures, bool RootSigRuleVerified,
    long ExactUnits = 0, long InferredUnits = 0, long GuessedUnits = 0,   // per-stage plans: where each unit's state came from
    double LayoutCoverage = 0,    // per-stage plans needing real layouts: share of the index's VSs with a known layout (a recording's, or the shipped pipeline cache's)
    long D3D11Shaders = 0,        // D3D11 items (each shader once, no pipelines); not in Generated
    long MiddlewareItems = 0,     // recorded middleware PSOs seeded from packs (not in the recording); not in Generated.
                                  // Warm items = Recorded + Generated + D3D11Shaders + MiddlewareItems (about: a pack entry whose
                                  // shader the install's DLL copy lacks is skipped at materialize; a ray generation library's
                                  // collection may be created once per payload, RtCollections.Payloads)
    long Uncovered = 0,           // stage sets left out: no root signature SCSKiller can build covers their shaders (rs_uncovered)
    long RtLibraries = 0,         // DXIL libraries (ray tracing shaders) in the game's index on the plan's platform
    long RtUncovered = 0,         // of those, the ones this plan can't compile: in no synthesized collection and no recorded ray
                                  // tracing state object (0 when the recording has RayQuery shaders and no state object: inline ray tracing)
    long StageSets = 0,           // distinct stage sets the planner found in the game files (0 = a plan from before this was counted)
    long LeftOut = 0,             // of those, the ones not in the plan for any reason (no root signature, no template, Uncovered, stream output)
    long MiddlewareSharedItems = 0, // of MiddlewareItems, the ones only a shared pack (downloaded from the community database) had
    long RtStateObjects = 0,      // ray tracing state objects of the recording the plan replays (0 = none recorded, or a plan from before this was counted)
    long? RtInline = null);       // Unreal 5: the index's shaders on the plan's platform that trace rays inline (0 elsewhere; null = a plan from before this was counted)

/// <summary>Hash-only plan (no game bytes), persisted at <see cref="FilePath"/> in the planner's format.</summary>
public sealed record Plan(string GameId, string IndexContentHash, string Platform, string VendorProfile, PlanStats Stats, string FilePath);

public interface IPlanner
{
    PlanCheck Check(Game game, EngineInfo engine, Recording? recording, VendorCaps caps);
    /// <param name="maximum">With a per-stage cache (<see cref="VendorCaps.PerStageCache"/>) a plan needs every stage unit
    /// once; maximum also emits every stage set whose units are already covered (every pairing pre-linked), with the same
    /// per-unit state. Without one, plans are whole pipelines either way.</param>
    Plan Build(Game game, EngineInfo engine, ShaderIndex index, Recording? recording, VendorCaps caps, string outDir,
        IProgress<string>? log, CancellationToken ct, bool maximum = false);
    /// <summary>Writes a warm-ready work folder: scskiller.db (the recording, or empty) + scskiller_gen.db
    /// (shader bytes pulled from the game through <paramref name="reader"/>, templates, root signatures, plan items).</summary>
    void Materialize(Plan plan, Game game, EngineInfo engine, IEngineReader reader, Recording? recording, string workDir, CancellationToken ct);
}

// Warming drives the native scskiller_warm.exe (protocol in ARCHITECTURE.md).
public enum WarmPriority { BelowNormal, Idle }
public sealed record WarmOptions(int Threads, WarmPriority Priority, long StartAt = 0,
    int MemoryMB = 0,    // the staged warm process's private memory budget (scskiller_warm --memory-mb); 0 = none
    IReadOnlyCollection<string>? SkipKeys = null,   // record keys of items that crashed the GPU driver before: never replayed
    int CarefulThreads = 0);   // a careful warm's passes (a pass file in the work folder): their threads; 0 = Threads
/// <param name="Failed">PSOs the driver rejected (only that: one naming a shader this install lacks is never replayed)</param>
/// <param name="Skipped">PSOs of the plan / recording not replayed because a shader is not in this install (another game
/// build, built at run time, a middleware DLL without it); not in <paramref name="Total"/>. Set by the app from
/// Materialize, 0 from the warmer.</param>
public sealed record WarmProgress(long Done, long Total, long Failed, double PerSecond, long CacheGrowthBytes = 0, long Skipped = 0,
    string? Note = null);   // what the warm is doing besides compiling, e.g. "retrying ray tracing with fewer threads (8)"; null = nothing
public enum WarmOutcome { Completed, Stopped, Failed }
/// <param name="Failed">PSOs the driver rejected</param>
/// <param name="Skipped">PSOs not replayed: a shader not in this install (see <see cref="WarmProgress.Skipped"/>)</param>
/// <param name="Crashed">record keys of this plan's items that crash the GPU driver (their create removed the D3D12 device):
/// never replayed, in Done but not in Failed; null = none</param>
public sealed record WarmResult(WarmOutcome Outcome, long Done, long Total, long Failed, TimeSpan Elapsed, long CacheGrowthBytes, string LogPath, string? Error,
    long Skipped = 0, IReadOnlyCollection<string>? Crashed = null);

public interface IWarmRun
{
    Task<WarmResult> Completion { get; }
    void Pause();
    void Resume();
    /// <summary>Graceful: in-flight compiles finish and the process exits normally so the driver writes its cache.
    /// Resume later with <see cref="WarmOptions.StartAt"/> = the stopped run's Done.</summary>
    void Stop();
}

public interface IWarmer
{
    IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress);
}

public enum GameStatus { Unsupported, NeedsRecording, Ready, Warmed, Stale }   // Stale: driver or game changed since the warm

public sealed record SessionStats(TimeSpan Duration, long Requests, long FromGameLibrary, long CacheHits, long Compiles, double WorstCompileMs,
    long RayQueryRecompiles = 0,    // NVIDIA: creates of compiled RayQuery PSOs at the driver's floor (SessionLog.RayQueryFloorMs), not in Compiles
    long StateObjectsReady = 0,     // ray tracing state object creates (CreateStateObject, AddToStateObject) under SessionLog.StateObjectCompileMs; in Requests only
    long StateObjectsCompiled = 0,  // ...and from it: compiled during play
    long StartupCompiles = 0,       // compiles while the game started up (FrameLog.StartupEnd), not in Compiles or WorstCompileMs
    long StateObjectsStartupCompiled = 0);   // state objects compiled while it started up, not in StateObjectsCompiled

public sealed record GameState(
    Game Game, EngineInfo? Engine, AntiCheat AntiCheat, GameStatus Status, string StatusReason,
    int? ShaderCount, PlanStats? Plan, long? EstimatedCacheBytes, TimeSpan? EstimatedWarmTime,
    string? WarmedDriverVersion, DateTimeOffset? WarmedAt, TimeSpan? LastWarmTime, bool RecorderInstalled, SessionStats? LastSession,
    long? CacheOnDisk = null,    // driver-cache bytes attributed to this game (measured from its warms); null = unknown
    string? LaunchedExeName = null,   // the exe file name in the case the game really launches with (AMD's cache key is case-sensitive)
    IReadOnlyList<MiddlewareTag>? Middleware = null,   // upscalers/middleware next to the exe (Middleware.Tags); null = not looked (anti-cheat)
    long? LastWarmFailed = null,    // the last complete warm: PSOs the driver rejected; null = not known (no warm since this was kept)
    long? LastWarmSkipped = null,   // ...and PSOs skipped because a shader is not in this install (never replayed)
    KnownStutter? KnownStutter = null,   // on the known shader-compilation-stutter list (Games.StutterList); null = not listed
    CommunityInfo? Community = null,    // a community database recording in use (Settings.UseCommunityDb); null = none
    DateTimeOffset? RecordingSharedAt = null,   // when this PC's recording was last shared (Settings.ShareRecordings); null = never
    bool? InCommunityDb = null,   // the community database's manifest (the last one fetched) has an entry for this build; null = not known
    long? NewPipelines = null,   // the plan's planner-made records the last complete warm didn't take; null = not known
    bool Playing = false,       // always false in the CLI (no game watcher)
    RecorderOverride RecorderOverride = RecorderOverride.Default,
    bool RecorderEffective = false,   // should be installed (it may not be yet: RecorderNote)
    string? RecorderSkip = null,      // ScsKiller.Skip*; null = compatible
    string? RecorderNote = null,      // the last reconcile's pending or failed change; null = none
    string? RecorderRefused = null,   // why the recorder passed the game's last launch through (ScsKiller.Refused); null = it recorded, or no launch since
    string? RecorderMod = null,       // what a foreign d3d12.dll in the game folder (or the one chained to it) calls itself; null = none
    bool RecordAlongsideMod = false,  // the user chose to record alongside it (IScsKiller.SetRecordAlongsideMod)
    long? LastWarmNeedsRecording = null,   // of LastWarmSkipped, those the community recording flags as built at run time or by a mod
    long? LastWarmCrashed = null,   // the last complete warm: items skipped because they crash the GPU driver
    CarefulCompile? Careful = null,        // AMD's careful compile (ScsKiller.CarefulThreads); null = not this vendor's
    long RecordingBytes = 0,        // the recorder's data files in the game folder plus SCSKiller's copy of its recording
    bool RecordingPaused = false,   // the recorder is in and its recording reached Settings.RecordingLimitMB: no new records
    long RecordedSinceWarm = 0,     // pipelines its recordings and packs have that the last complete warm didn't compile, apart from NewPipelines (derived)
    int CommunityDbPsos = 0,        // the manifest entry's pipelines while InCommunityDb
    double? PsoPerSecond = null,   // the game's last complete warm onto a cold cache (ScsKiller.ColdWarm); null = none measured
    FrameReport? LastFrames = null,    // the last launch's frame times (FrameLog); null = none measured
    string? ShaderMod = null,          // a ReShade add-on that changes the game's pipelines (Games.ReShade.Detect); null = none
    bool ShaderModBlocks = false,      // ...adds to every root signature, in a layer a copy can't reproduce (ReShadeInstall.Block): never compiled, recorded or shared
    bool ShaderModLayer = false,       // ...compiles through a copy of the layer (ScsKiller.LayerFor)
    bool ShaderModAsD3D12 = false,     // ...with ReShade installed as d3d12.dll: the recorder records under it only when chained
    bool RootUnconfirmed = false,      // a game the user added whose folder they haven't confirmed: never recorded (ScsKiller.SkipManual)
    bool RtUnseen = false,             // recorded long enough without ray tracing (GameRecord.RtUnseen) and planned since: its uncovered ray tracing isn't asked for
    bool RtToPlan = false,             // its plan asks for a ray tracing recording and a newer recording waits for the plan check (ScsKiller.RtPlanCheck)
    bool RecordedEnough = false,       // GameRecord.RecordedLong: asking for "5 minutes" again says nothing
    bool OfflineEligible = false,      // an EasyAntiCheat game of Games.OfflineEac on D3D12: an offline session may be offered
    bool OfflineRecord = false,        // the user allowed offline sessions for it (IScsKiller.SetOfflineRecording)
    bool OfflineRunning = false);      // a session SCSKiller started runs, or its files aren't out of the game folder yet

/// <summary>A launch's frame times from the recorder (<see cref="App.FrameLog"/>): its length, the startup stretch before
/// play (the game's own precompile and first load), the 1% low of play, every frame of 50 ms or more, and for a graph
/// the longest frame of each of <see cref="App.FrameLog.GraphColumns"/> equal slices of the launch (0 = no frame started in it).</summary>
public sealed record FrameReport(TimeSpan Duration, TimeSpan Startup, long Frames, double Low1PctFps, IReadOnlyList<Hitch> Hitches,
    IReadOnlyList<float> Peaks,
    long LaunchUnixMs = 0);   // the frame log's launch stamp: SessionLog takes its Duration (the last frame) for the launch's end

/// <summary>A frame of 50 ms or more, <paramref name="At"/> into the launch.</summary>
public sealed record Hitch(TimeSpan At, double Ms, HitchCause Cause);

/// <summary>Shader: a pipeline compile overlapped the frame. Other: no create did (streaming, the CPU, anything else).
/// Loading: startup, or a load in play that created many pipelines, all fast; LoadingShaders: a startup frame that cold
/// compiles filled (in play such a frame is a Shader stutter).
/// Quitting: the last seconds before the game's last frame.</summary>
public enum HitchCause { Shader, Other, Loading, Quitting, LoadingShaders }

/// <summary>AMD's careful compile (ARCHITECTURE.md): the recorded PSOs in passes, one process each, on few threads, because the driver
/// keeps fewer and differently keyed entries from a fast compile. <paramref name="On"/>: the game's compiles run it.
/// <paramref name="LaunchCompiled"/>: the share of the creates at the game's first launch after its last complete compile
/// that still compiled (null = not judged yet). <paramref name="Estimate"/>: how long a careful compile of the plan takes.
/// <paramref name="Recorded"/>: the plan's recorded PSOs, the ones compiled carefully (0 = nothing to do carefully; null
/// = no plan yet).</summary>
public sealed record CarefulCompile(bool On, double? LaunchCompiled, TimeSpan? Estimate, long? Recorded);

/// <summary>A game's recorder choice: Default follows <see cref="Settings.RecordAllGames"/>; On/Off override it.</summary>
public enum RecorderOverride { Default, On, Off }

/// <summary>A recording downloaded from the community database (docs/plan-db.md): its PSO records, when it came, and
/// whether this PC's own recording is merged with it.</summary>
public sealed record CommunityInfo(int Psos, DateTimeOffset DownloadedAt, bool WithLocalRecording);

/// <summary>A game with documented shader-compilation stutter: how bad, one line why, the source (a URL or a citation),
/// and when the entry was last checked (yyyy-MM-dd).</summary>
public sealed record KnownStutter(StutterSeverity Severity, string Reason, string Source, string Date);
public enum StutterSeverity { Moderate, Severe }

/// <summary>Detected middleware as a player names it ("OptiScaler (FSR4)", "XeSS", "DLSS"), its DLL file names and the
/// PSOs its packs hold for those DLL versions: 0 = detected, compiled only after a recording sees them.</summary>
public sealed record MiddlewareTag(string Label, IReadOnlyList<string> Dlls, int Pipelines);

public enum DriverUpdateMode { Ask, WhenIdle, Off }

public sealed record Settings(int Threads, WarmPriority Priority, DriverUpdateMode OnDriverUpdate, int BackgroundThreads,
    bool PauseWhileGaming,
    bool UseCommunityDb = true,   // download from the community shader hash database (signed in with "db") and use what was downloaded;
                                  // renamed from CommunityPlans (never settable, saved false) so existing settings.json start on
    bool MaximumPlans = false,    // also plan every stage pairing, not just every stage (per-stage caches reuse a stage in any pairing)
    bool StartWithWindows = true,  // the app starts in the notification area at sign-in (WindowsStartup)
    int MaxCompileMemoryGB = 0,    // the warm's memory budget; 0 = Auto (ScsKiller.AutoCompileMemoryGB: from the PC's RAM)
    bool WelcomeSeen = false,      // the first-run welcome dialog was closed (App.ShowWelcome)
    bool ShareRecordings = false,   // opted in to sharing recordings (welcome, Settings): anonymous uploads of this PC's recordings (ScsKiller.Sharing)
    bool SharePromptDismissed = false,   // the post-sign-in share prompt was answered, either way (Account.OffersSharing): never asked again
    string? UpdateChannel = null,   // "stable" | "beta" | "alpha" | "internal"; null = the running build's own (UpdateChannels.Effective caps it by entitlement)
    bool RecordAllGames = true,     // unless a game's RecorderOverride says otherwise
    int RecordingLimitMB = 256,     // per game: the recorder's db plus SCSKiller's copy of it; 0 = unlimited
    bool NotifyNewShaders = true,   // a notification when compiled games have new pipelines to compile (NewShaders)
    bool ActiveCheck = true,        // the anonymous daily check that counts active installs (ScsKiller.ActiveCheck); off = nothing is sent
    string? GpuNoticeDismissed = null,   // the GPU name whose "doesn't compile on this GPU" notice was closed (Format.GpuNotice)
    bool InstallUpdatesAutomatically = true,   // a downloaded update installs at the next start or quit (AutoInstall); off = only "Restart to update"
    bool ScanAtStart = true,   // a scan the user didn't ask for reads every game again where it changed; off = the last list (ScsKiller.Listed)
    bool CloseQuits = false);  // the window's close button quits like the tray's Quit (WindowClose); off = it hides to the notification area

public enum QueueStage { Waiting, Indexing, Planning, Materializing, Warming, Paused, Done, Failed, Stopped }
public sealed record QueueItem(string GameId, QueueStage Stage, WarmProgress? Progress, string? Error,
    string? Note = null,   // why it waits or is paused ("paused while <game> is running"); Done/Stopped: ScsKiller.WarmCounts (failed, skipped)
    bool PlanCheck = false);   // a background plan rebuild the app queued itself (ScsKiller.CheckPlans), not a compile: lists leave it out

/// <summary><see cref="IScsKiller.AddManualGame"/>'s result: the added game, or the listed one the exe belongs to.</summary>
public sealed record ManualAdd(Game Game, bool Existed);

public interface IScsKiller
{
    IGpuVendorBackend Vendor { get; }
    Settings Settings { get; set; }

    // userRequested: the user's refresh, which also fetches the server's data whatever its age (ScsKiller.UserFetchEvery)
    Task<IReadOnlyList<GameState>> ScanAsync(CancellationToken ct, bool userRequested = false);     // cheap: reuses saved per-game results
    Task<IReadOnlyList<GameState>> RescanAsync(CancellationToken ct, bool userRequested = false);   // re-checks every game
    IReadOnlyList<GameState> Games { get; }
    event Action<GameState>? GameChanged;

    IReadOnlyList<QueueItem> Queue { get; }      // in run order
    event Action<QueueItem>? QueueChanged;
    bool QueueRunning { get; }
    /// <summary>An item is running, a stopped or removed one too until its warm has exited (the driver writes its cache).</summary>
    bool Compiling { get; }
    /// <summary>Adds the game (index + plan if stale + materialize + warm) at the end; no-op if already queued. Starts only
    /// if the queue is running (see <see cref="StartQueue"/>).</summary>
    void Enqueue(string gameId);
    /// <summary>Runs the waiting items in order until the queue is empty, then the queue stops.</summary>
    void StartQueue();
    /// <summary>The game page's compile button: <see cref="Enqueue"/>, and if the queue isn't running, run its waiting items.
    /// Unlike <see cref="StartQueue"/>, finished items stay listed and stopped ones stay stopped.</summary>
    void Compile(string gameId);
    /// <summary>Reorders a waiting item; 0 = next to run. The running item doesn't move.</summary>
    void MoveInQueue(string gameId, int index);
    void EnqueueWhenIdle(string gameId);         // same, but runs as a background rebuild only while the PC is idle
    void Remove(string gameId);
    void PauseQueue();
    void ResumeQueue();
    void StopQueue();                            // graceful stop of the running item

    /// <summary>Games warmed for an older driver (or game version) than the current one.</summary>
    IReadOnlyList<GameState> StaleGames();
    /// <summary>The stale games warmed for another driver than the current one: what the driver-update notification lists
    /// (a game stale only because SCSKiller plans more has its own "when idle" plan check).</summary>
    IReadOnlyList<GameState> DriverStaleGames();
    /// <summary>The user chose Skip: don't offer the current stale set again until the driver changes.</summary>
    void DismissStale();
    /// <summary>Ask mode and a driver-stale game (<see cref="DriverStaleGames"/>) the user hasn't skipped for this driver/game build.</summary>
    bool ShouldNotifyStale();
    /// <summary>Registers (Ask/WhenIdle) or removes (Off) the sign-in task that checks for driver updates. Call only
    /// on the user's action: it changes Task Scheduler.</summary>
    void ApplyDriverUpdateMode();

    /// <summary>A user-supplied AES key for an encrypted game (hex). True if it opens the game's files; it's then stored
    /// locally only (never in plans, logs or anything shared). Call RescanAsync afterwards.</summary>
    bool SetEncryptionKey(string gameId, string key);

    /// <summary>What <see cref="ClearGameCache"/> deletes, by kind, empty kinds left out; an anti-cheat game's driver cache
    /// only. Reads dbs: not on the UI thread.</summary>
    IReadOnlyList<CachePart> GameCaches(string gameId, bool gamePrecache = false);

    /// <summary>All or none; false when there was nothing to delete. Refusals throw InvalidOperationException: the game is
    /// running, a compile of it is in progress, or a file is in use. <paramref name="gamePrecache"/>: also the shader and
    /// pipeline caches the game writes itself (it rebuilds them at its next start).</summary>
    bool ClearGameCache(string gameId, bool gamePrecache = false);

    /// <summary>AMD: the game's compiles run careful (<see cref="CarefulCompile"/>) or fast; InvalidOperationException on
    /// another vendor.</summary>
    void SetCarefulCompile(string gameId, bool on);

    /// <summary>Sets the override On; throws InvalidOperationException when it can't install now (a running game gets it on exit).</summary>
    void InstallRecorder(string gameId);
    /// <summary>Sets the override Off; throws while the game runs (removed on exit).</summary>
    void UninstallRecorder(string gameId);

    /// <summary>No scan; the new state comes as GameChanged. ArgumentException for an unknown game.</summary>
    void RefreshGame(string gameId);
    /// <summary>Driver-cache sizes only (cheap); GameChanged for the games whose size changed.</summary>
    void RefreshCacheSizes();
    /// <summary>Never throws for the game's state: see <see cref="GameState.RecorderSkip"/>, <see cref="GameState.RecorderNote"/>.</summary>
    void SetRecorderOverride(string gameId, RecorderOverride value);
    /// <summary>"Record alongside &lt;mod&gt;" (<see cref="GameState.RecorderMod"/>): the recorder may take a mod's d3d12.dll
    /// place, the mod renamed and put back byte for byte when the recorder goes. Never throws for the game's state.</summary>
    void SetRecordAlongsideMod(string gameId, bool on);
    /// <summary>Allows offline sessions without EasyAntiCheat for the game (<see cref="GameState.OfflineEligible"/>); throws
    /// InvalidOperationException for a game that isn't eligible.</summary>
    void SetOfflineRecording(string gameId, bool on);
    /// <summary>Puts the recorder in, starts the game's exe itself without EasyAntiCheat, and records that process only. Throws
    /// InvalidOperationException, with nothing written, unless the user confirmed this launch, allowed it for the game and
    /// the game is eligible and not running. The task ends once the game exited and its folder is back to how it was.</summary>
    Task StartOfflineSession(string gameId, bool confirmed);
    /// <summary>A running game's folder is left alone: call again when it exits. Null = every game.</summary>
    void ReconcileRecorders(string? gameId = null);
    /// <summary>What adding the exe would add, nothing saved: the resolved exe (where the recorder goes) and the suggested game
    /// folder; or, with <see cref="ManualAdd.Existed"/>, the listed game it belongs to. ArgumentException as AddManualGame.</summary>
    ManualAdd PreviewManualGame(string exePath);
    /// <summary>Why <paramref name="installDir"/> can't be the game's folder (a drive, a store's library, a folder of several
    /// games); null = it can.</summary>
    string? ManualFolderProblem(string exePath, string installDir);
    /// <summary>Adds the game whose exe the user picked (<see cref="Games.ManualSource"/>); the next scan lists it. With
    /// <paramref name="installDir"/>, the game folder the user confirmed (an added game's is replaced): the recorder may then
    /// go in, after the anti-cheat check of that whole folder. ArgumentException with the message to show when the pick
    /// can't be a game or the folder can't be its; an exe of a store's game returns that game with <see cref="ManualAdd.Existed"/>.</summary>
    ManualAdd AddManualGame(string exePath, string? installDir = null);
    /// <summary>Forgets a game the user added, after taking its recorder and the recorder's files out of the game folder;
    /// nothing else of the game is deleted. InvalidOperationException while it runs or compiles, or for a store's game.</summary>
    void RemoveManualGame(string gameId);
    /// <summary>Deletes the recorder's data files (scskiller.db, its csv and log in the game folder, SCSKiller's copies of
    /// the recording), never the recorder itself; the next compile plans from the game files. False when there was nothing
    /// to delete. Throws InvalidOperationException while the game runs or a compile of it is in progress.</summary>
    bool ClearRecording(string gameId);
}
