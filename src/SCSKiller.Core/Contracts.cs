// Shared contracts. ARCHITECTURE.md maps the components behind them.
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
    bool NoRayTracing = false,     // ray tracing is off altogether (Unreal: r.RayTracing=0), inline too; implies NoRtPipelines
    bool AftermathShaderDebug = false,    // not set or read: a warm's Aftermath follows only the game's own run (NvAftermath.Resolve), never its config
    bool VersionGuessed = false,          // Version is a guess (Unreal: UnrealReader.DetectEngine's last fallback)
    bool ApiGuessed = false,              // GraphicsApi is the engine's default for Version, nothing in the game names one (Unreal: UnrealRhi.Resolve)
    string? Game = null,                  // "<Game.Id>[@<Game.Version>]", for an engine whose root-signature rule is one game's (Nixxes ports: RuleMatch.Games)
    int? MaxAnisotropy = null,            // the player's texture filtering from the game's config (Unreal: UnrealRhi.MaxAnisotropy); null = not known
    string? Note = null,                  // a line for the game page about what the compile leaves out (FromSoftware: an archive no key opens)
    bool OffersVulkan = false,            // the game can also run on Vulkan, whatever GraphicsApi says (Unity's build list, Source 2): the game page offers it
    bool AftermathOff = false,   // not set or read, as AftermathShaderDebug
    bool KeyMissing = false,              // game files known to hold shaders stay encrypted, no key SCSKiller has opens them: the rest compiles, the user can add the key (Unreal: AES)
    bool KeyOptional = false);            // other game files stay encrypted, none known to hold shaders: the user can still add the key

/// <summary>Shader stage, numbered like D3D12_PIPELINE_STATE_SUBOBJECT_TYPE (the proxy's db uses the same numbers).</summary>
public enum Stage { Vertex = 1, Pixel = 2, Domain = 3, Hull = 4, Geometry = 5, Compute = 6, Amplification = 24, Mesh = 25, Library = 100 }

/// <summary>A signature element. <paramref name="ReadMask"/>: the components an input is actually read (DXBC ISGN
/// ReadWriteMask, DXIL ISG1 AlwaysReads), or an output actually writes (its mask without the NeverWrites components),
/// 0xFF = unknown (a reader that doesn't parse it).
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
    string? RootSignature = null,   // SHA-1 of the root-signature blob the game creates from the one the shader carries (RTS0), servable through ReadShaders and in ShaderIndex.RootSignatures; null = none
    bool InlineRayTracing = false,   // traces rays inline (RayQuery: Dxbc.InlineRayTracing)
    byte[]? EngineHeader = null,   // the engine's own header stored beside the shader, read by its root-signature rule (Dagor: dxil::ShaderHeader)
    int Payload = 0,   // amplification shaders: the payload bytes they send; mesh shaders: the bytes they take (PSV0); the runtime rejects a pair that differs
    bool ViewId = false);   // reads SV_ViewID (Dxbc.ViewId): the game creates it with view instancing

/// <summary>A group of shaders that can combine. <see cref="IsPipeline"/>: the game shipped this exact stage set as one
/// pipeline (e.g. a PSO cache record), so no pairing is needed. <see cref="RootSignature"/>: the root signature the game
/// creates that pipeline with, or every pipeline of the map's shaders (SHA-1, in ShaderIndex.RootSignatures and servable
/// through ReadShaders), taken over the shaders' own: a shader the game draws with several root signatures carries none.
/// <see cref="Layout"/>: the input layout the game creates that pipeline with (an Unreal pipeline cache's vertex declaration).
/// <see cref="Copy"/>: "&lt;path&gt;|&lt;container&gt;" of a library that several containers hold at one path (the mount order's
/// pick's path on every copy), on that pick and on its other copies (<see cref="ShaderIndex.OtherCopies"/>); null otherwise.</summary>
public sealed record ShaderMap(string Hash, string Library, string Platform, IReadOnlyList<string> Shaders, bool IsPipeline = false,
    string? RootSignature = null, IReadOnlyList<Planning.PsoDb.LayoutElem>? Layout = null, string? Copy = null);

public sealed record ShaderIndex(
    string ContentHash,     // changes when the game's shader libraries change (game patch) -> plans built on it are stale
    IReadOnlyList<string> Platforms,                     // e.g. PCD3D_SM5, PCD3D_SM6
    IReadOnlyDictionary<string, ShaderInfo> Shaders,     // by Sha1
    IReadOnlyList<ShaderMap> Maps,
    IReadOnlyDictionary<string, byte[]>? RootSignatures = null,   // every ShaderInfo.RootSignature's blob (or its RTS0 part), by that SHA-1: the planner's coverage check reads them
    IReadOnlyList<string>? LegacyHashes = null,   // ContentHash as earlier versions may have taken it (UnrealReader: tied copies in another order); none: only ContentHash
    IReadOnlyList<ShaderMap>? OtherCopies = null)   // the maps of a library's other copies at the same path (UnrealReader: a base pak and its patches), each with its ShaderMap.Copy: a recording may show the game reads one of them
{
    /// <summary>The ContentHash of an index without shaders, SHA-1 of nothing: every such game has it, so it fingerprints no build.</summary>
    public const string EmptyContentHash = "da39a3ee5e6b4b0d3255bfef95601890afd80709";

    public static bool IsEmpty(string? contentHash) => string.Equals(contentHash, EmptyContentHash, StringComparison.OrdinalIgnoreCase);
}

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
    /// <summary><see cref="IndexStamp(Game)"/> for a game detected as <paramref name="engine"/> (null: not known).</summary>
    string IndexStamp(Game game, EngineInfo? engine) => IndexStamp(game);
    /// <summary>Goes up when Index or IndexStamp read the same game files otherwise: a warm from an older version is
    /// re-planned while idle instead of counting as changed game shaders.</summary>
    int ReaderVersion(EngineInfo engine) => 1;
    ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct);
    /// <summary>With <paramref name="progress"/>: the items read of the total, as the reader goes (Unreal: packages).</summary>
    ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, IProgress<(int Done, int Total)>? progress, CancellationToken ct) => Index(game, engine, log, ct);
    /// <summary>Streams container bytes (hash convention above) for the requested shaders; the sink is called sequentially.</summary>
    void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct);
}

/// <summary>Every reader's parallel loop: on the caller's scheduler (a compile's below-normal pool, sized to its threads)
/// and at most as wide, never wider than the machine.</summary>
public static class ReaderLoops
{
    public static int Width => Math.Min(Environment.ProcessorCount, TaskScheduler.Current.MaximumConcurrencyLevel);
    public static ParallelOptions Options(CancellationToken ct = default) =>
        new() { TaskScheduler = TaskScheduler.Current, CancellationToken = ct, MaxDegreeOfParallelism = Width };
}

// Adding a GPU vendor = one IGpuVendorBackend + its VendorCaps; the planner and warmer only read the caps.
public enum GpuVendor { Unknown = 0, Nvidia = 0x10DE, Amd = 0x1002, Intel = 0x8086, Qualcomm = 0x5143 }

/// <param name="Dx12">D3D12CreateDevice accepts the adapter at feature level 11_0 (<see cref="GpuBackends.Adapters"/>)</param>
public sealed record GpuInfo(GpuVendor Vendor, string Name, string DriverVersion, long AdapterLuid, ulong DedicatedVideoMemory, bool Dx12 = true);

/// <summary>The adapter the user picked (<see cref="Settings.Gpu"/>): matched by its LUID while it holds, else by vendor,
/// device id and name (a reboot or driver reload gives a new LUID).</summary>
public sealed record GpuChoice(long AdapterLuid, GpuVendor Vendor, uint DeviceId, string Name);

/// <summary>What a vendor's D3D12 shader cache does. Measured, never assumed: NVIDIA values come from the NVIDIA probes.</summary>
public sealed record VendorCaps(
    string Profile,                 // e.g. "nvidia-1"; stored in plans, a different profile makes a plan stale
    bool CacheKeyedByExeName,       // true: warm from a staged exe named like the game (no injection); false: in-game warm only
    bool StateIndependentCache,     // true: fixed-function state + input layout don't change the compiled result -> synthesized templates allowed
    bool CacheSizeConfigurable,
    bool PerStageCache = false,     // true: each stage is compiled and cached on its own (+ root signature, + for VS its read input
                                    // layout/topology on AMD): a plan needs each stage unit once, not every VS x PS pair
    RtCacheGranularity RtCacheGranularity = RtCacheGranularity.WholeObject,  // what the driver caches of a ray tracing state object
    bool PackageKeyed = false,      // true: a packaged (Xbox) game's cache key follows its package identity: its warm runs with it
    long? PerGameCacheCap = null,   // the driver evicts a game's own entries past this many bytes (Intel, NVIDIA on Linux); null = no per-game cap
    string? VulkanKey = null,       // what the vendor's Vulkan pipeline cache is keyed on for a replay to reach it ("exe-name", "app-name", "content"); null = no Vulkan warm
    long? VulkanCacheCap = null,    // the bytes of a Vulkan warm a game reads back (NVIDIA on Linux: what one cache file indexes); null = no cap
    long? WarmCacheBudget = null,   // what a plan may fill where less than PerGameCacheCap (NVIDIA on Linux: the warm's files rewritten without a dictionary, beside the game's own); null = PerGameCacheCap
    long? PsoBytes = null);         // what one pipeline takes of that budget as the game's own compile measured it; null = PlanBuilder.PsoBytes

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
    /// <summary>Switches it the way the NVIDIA App does (driver setting + the driver's idle task), touching only the part
    /// that differs from the wanted state. Returns the outcome for the user. Global, needs admin: callers must have the
    /// user's explicit OK.</summary>
    string SetAutoShaderCompilation(AutoShaderCompilation level) =>
        throw new NotSupportedException($"Auto Shader Compilation is an NVIDIA feature ({Gpu.Name})");
}

/// <summary>NVIDIA App's Automatic Shader Compilation: Off, or on with that "System Utilization" (the App's default is Medium).</summary>
public enum AutoShaderCompilation { Off, Low, Medium, High }
/// <summary>TaskReady: the driver's idle compile task is enabled, runs the active driver's NvOSC.exe and matches Level (the
/// setting alone compiles nothing). TaskEnabled: an enabled task by that name exists, ready or not.</summary>
public sealed record AutoShaderState(AutoShaderCompilation Level, bool TaskReady, bool TaskEnabled)
{
    /// <summary>Already what switching to <paramref name="level"/> would leave: on = the setting and a ready task, off = the
    /// setting and no enabled task.</summary>
    public bool Is(AutoShaderCompilation level) =>
        Level == level && (level == AutoShaderCompilation.Off ? !TaskEnabled : TaskReady);
}

/// <summary>A driver cache split into per-application files (NVIDIA DXCache, AMD DxcCache/DxCache). A key names one
/// application's files; which keys belong to a game is learned from the files a process named like it holds open.
/// Exposed as <see cref="IGpuVendorBackend.AppCache"/> (null = the vendor's cache isn't per application).</summary>
public interface IAppCache
{
    IReadOnlyList<FileInfo> FilesOf(IEnumerable<string> keys);
    /// <summary><see cref="FilesOf"/> for a delete: every file, or InvalidOperationException when the listing can't be complete.</summary>
    IReadOnlyList<FileInfo> FilesToDelete(IEnumerable<string> keys) => FilesOf(keys);
    long SizeOf(IEnumerable<string> keys);
    /// <summary>Keys of the cache files a running process with this exe file name has open now (no content read).</summary>
    IReadOnlySet<string> KeysOpenBy(string exeFileName);
    /// <summary>The keys of every file in the cache (names only).</summary>
    IReadOnlySet<string> AllKeys();
    /// <summary>Deletes every file of these keys, or none: InvalidOperationException ("files in use by ...") when one is
    /// open. Returns how many were deleted.</summary>
    int Delete(IEnumerable<string> keys);
}

/// <summary>What a game's Steam launch options carry for it to read SCSKiller's cache (<see cref="IScsKiller.LaunchLineOf"/>):
/// <paramref name="Text"/>, the variables scskiller proton-env prints, quoted for the shell, then %command%.
/// <paramref name="Set"/>: the options set each of them to its value already. <paramref name="Refused"/>: why no options can
/// carry them, so none are offered.</summary>
public sealed record LaunchLine(string Text, bool Set, string? Refused = null);

/// <summary>One kind of a game's shader cache on disk (<see cref="IScsKiller.GameCaches"/>).</summary>
public sealed record CachePart(string Name, IReadOnlyList<string> Files, long Bytes);

/// <summary>A scskiller.db written by the proxy in record mode (format: proxy/proxy.cpp header).</summary>
public sealed record Recording(string DbPath)
{
    /// <summary>A Vulkan plan of several archives, best first (Steam's pipeline cache on Linux): set, it replaces DbPath's.</summary>
    public IReadOnlyList<Vulkan.FozSource>? VulkanSources { get; init; }
    /// <summary>The game's recorded Vulkan device (<see cref="Vulkan.VulkanLayer.Device"/>); null: none.</summary>
    public string? VulkanDevice { get; init; }
    /// <summary>The game's last launch ran with its Vulkan cache in Steam's folder for this vendor (<see cref="Vulkan.VulkanPlan.SteamCache"/>).</summary>
    public bool VulkanSteamCache { get; init; }
    /// <summary>The ray tracing state objects only the community recording has (<see cref="Planning.Planner.CommunityLeftOut"/>);
    /// null: none, or a GPU whose driver caches whole state objects only.</summary>
    public IReadOnlySet<string>? CommunityStateObjects { get; init; }
    /// <summary>This PC's recorded texture filtering settings (sampler sets, hex), last recorded first
    /// (<see cref="Planning.SamplerVariants.Ordered"/>), and how many of them a plan compiles; null: every one the recording has.</summary>
    public (IReadOnlyList<string> Sets, int Keep)? Filtering { get; init; }
    /// <summary>The recorded pipelines left out as another GPU vendor's (<see cref="Planning.Planner.OtherVendors"/>): the rule
    /// rebuilt each one's root signature for that vendor, so they count for its check.</summary>
    public int OtherVendors { get; init; }
}

public enum Readiness { Ready, NeedsRecording, Unsupported }
public sealed record PlanCheck(Readiness Readiness, string Reason);

public sealed record PlanStats(long Recorded, long Generated, long SynthesizedTemplates, long RootSignatures, bool RootSigRuleVerified,
    long ExactUnits = 0, long InferredUnits = 0, long GuessedUnits = 0,   // per-stage plans: where each unit's state came from
    double LayoutCoverage = 0,    // per-stage plans needing real layouts: share of the index's VSs resolved from a recording
    long D3D11Shaders = 0,        // D3D11 items (each shader once, no pipelines); not in Generated
    long MiddlewareItems = 0,     // recorded middleware PSOs seeded from packs (not in the recording); not in Generated.
                                  // Warm items = Recorded + Generated + D3D11Shaders + MiddlewareItems + Variants (about: a pack entry whose
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
    long? RtInline = null,        // the index's shaders on the plan's platform that trace rays inline, for Unreal 5 or a game without DXIL libraries (0 elsewhere; null = a plan from before this was counted)
    string? RsRule = null,        // the engine's root-signature rule, its id in plan-rules.json; null = none, or a plan from before this was kept
    long? BuiltOk = null,         // recorded root signatures the rule rebuilt byte for byte...
    long? BuiltN = null,          // ...of those it was checked on (null = a plan from before this was kept)
    long D3D11Draws = 0,          // of D3D11Shaders, the vertex stages drawn with a recorded layout ('3' items, Draws11)
    long Variants = 0,            // PSOs compiled again at other texture filtering settings (Planning.SamplerVariants): a warm item each, never counted as pipelines
    long? CacheBudgetLeftOut = null, // per-stage plans where state is in the cache key (AMD, Intel): cover PSOs left out to stay under PlanBuilder.CacheBudgetOf; NVIDIA on Linux: generated PSOs past VendorCaps.WarmCacheBudget; Vulkan plans under VendorCaps.VulkanCacheCap: pipelines past it, charged with what they are created from; null = another vendor, or a plan from before the budget
    bool RecordedOnly = false,    // the full plan was over its budget (Planner.Build): only the recorded pipelines and the packs' are planned
    string? MaxShaderModel = null, // the newest DXIL shader model of the shaders the plan compiles ("6.6"); null = none, or a plan from before this was kept
    long DescriptorHeap = 0,      // Vulkan plans the game reads under another app name (NVIDIA on Linux): pipelines using descriptor heaps, in LeftOut (misses: the driver serves them from the game's own cache file alone)
    long? FromDownload = null,   // Vulkan plans: of Recorded, the pipelines read from Valve's download (FozOrigin.SteamDownload), the rest from play on this PC; null = another platform, or a plan from before this was counted
    long? FromCommunity = null,   // Vulkan plans: of Recorded, the pipelines read from the community's entry (FozOrigin.Community); null = none
    // a learned lookup the rule rebuilt enough of (PlanBuilder.RuleFallback): stage sets planned with the rule's root signature, 0 when
    // it has none; null = no such lookup, or a plan from before this fallback
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] long? RuleFallback = null);

/// <summary>Hash-only plan (no game bytes), persisted at <see cref="FilePath"/> in the planner's format.</summary>
public sealed record Plan(string GameId, string IndexContentHash, string Platform, string VendorProfile, PlanStats Stats, string FilePath);

public interface IPlanner
{
    PlanCheck Check(Game game, EngineInfo engine, Recording? recording, VendorCaps caps);
    /// <param name="maximum">With a per-stage cache (<see cref="VendorCaps.PerStageCache"/>) a plan needs every stage unit
    /// once; maximum also emits every stage set whose units are already covered (every pairing pre-linked), with the same
    /// per-unit state. Without one, plans are whole pipelines either way.</param>
    /// <param name="rayTracing">false: no ray tracing collections, and the libraries count as covered</param>
    Plan Build(Game game, EngineInfo engine, ShaderIndex index, Recording? recording, VendorCaps caps, string outDir,
        IProgress<string>? log, CancellationToken ct, bool maximum = false, bool rayTracing = true);
    /// <summary>Writes a warm-ready work folder: scskiller.db (the recording, or empty) + scskiller_gen.db
    /// (shader bytes pulled from the game through <paramref name="reader"/>, templates, root signatures, plan items).</summary>
    void Materialize(Plan plan, Game game, EngineInfo engine, IEngineReader reader, Recording? recording, string workDir, CancellationToken ct);
}

// Warming drives the native scskiller_warm.exe (protocol in ARCHITECTURE.md).
public enum WarmPriority { BelowNormal, Idle }
public sealed record WarmOptions(int Threads, WarmPriority Priority, long StartAt = 0,
    int MemoryMB = 0,    // the staged warm process's private memory budget (scskiller_warm --memory-mb); 0 = none
    IReadOnlyCollection<string>? SkipKeys = null,   // record keys of items that crashed the GPU driver before: never replayed
    int CarefulThreads = 0,    // a careful warm's passes (a pass file in the work folder): their threads; 0 = Threads
    bool Vulkan = false,       // a Vulkan plan: the work folder's scskiller.foz is replayed (scskiller_warm --vulkan)
    IReadOnlyDictionary<string, string>? CacheEnv = null,   // Vulkan: the driver-cache variables the game last ran with (Steam's redirect), set as they were; a Steam game's DXVK: Steam's
    long? CacheCap = null,    // NVIDIA on Linux: what the compile's cache folder may hold (NvidiaLinux.CacheSize); null = that
    bool D3DSCacheNoFlush = false,   // NVIDIA from 621: the warm skips d3dscache.dll's flushes while it creates (scskiller_warm --d3dscache-noflush)
    string? Delivery = null,    // the game's Advanced Shader Delivery (D3DSCache.Delivery.Stamp) the warm leaves out, only while it is still that one; null = none
    IReadOnlyCollection<string>? KnownKeys = null);   // record keys of items that crashed or hung this driver in an earlier compile: skipped, counted in WarmResult.Known
/// <param name="Failed">PSOs the driver rejected (only that: one naming a shader this install lacks is never replayed)</param>
/// <param name="Skipped">PSOs of the plan / recording not replayed because a shader is not in this install (another game
/// build, built at run time, a middleware DLL without it); not in <paramref name="Total"/>. Set by the app from
/// Materialize, 0 from the warmer.</param>
public sealed record WarmProgress(long Done, long Total, long Failed, double PerSecond, long CacheGrowthBytes = 0, long Skipped = 0,
    string? Note = null,   // what the warm is doing besides compiling, e.g. "retrying ray tracing with fewer threads (8)"; null = nothing
    int Workers = 0, int Threads = 0,   // the threads the warm's memory budget lets run now, of the ones asked for; 0 = not reported
    long DriverSkipped = 0,    // see WarmResult.DriverSkipped
    long Invalid = 0,          // see WarmResult.Invalid
    long Unsupported = 0);     // see WarmResult.Unsupported
public enum WarmOutcome { Completed, Stopped, Failed }
/// <param name="Failed">PSOs the driver rejected</param>
/// <param name="Skipped">PSOs not replayed: a shader not in this install (see <see cref="WarmProgress.Skipped"/>)</param>
/// <param name="Crashed">record keys of this plan's items that crash the GPU driver (their create removed the D3D12 device):
/// never replayed, in Done but not in Failed; null = none</param>
/// <param name="DriverSkipped">items skipped for this compile: a pipeline whose create hung the driver, a ray tracing state
/// object whose create faulted or hung it, and the state objects built on one. In Done, not in Failed; counted again by each
/// compile that skips them.</param>
/// <param name="Resumable">a Failed warm that stopped at a clean point (it gave up on device removals or hangs, or its host let
/// no further process start): Done is where the next compile goes on</param>
/// <param name="Full">a Completed warm that ended before the plan's end because the compile's cache holds no more (NVIDIA on
/// Linux): why. Done is what it reached, Total - Done wasn't compiled. Null: it reached the end.</param>
/// <param name="Invalid">Vulkan pipelines naming a shader module that fails SPIR-V validation, never created (Steam's replayer
/// skips them too). In Done, not in Failed or DriverSkipped.</param>
/// <param name="Unsupported">pipelines needing a feature the GPU lacks, never created: Vulkan's by Fossilize's feature filter,
/// D3D12's that the driver refused for blending on a render target format it can't blend.
/// In Done, not in Failed, DriverSkipped or Invalid.</param>
/// <param name="Cached">D3D12 pipelines created without a compile stored, counted only while the game's Advanced Shader
/// Delivery is registered for the compile: the delivery served them. In Done.</param>
/// <param name="Known">of DriverSkipped, the items of <see cref="WarmOptions.KnownKeys"/> and the state objects built on one</param>
/// <param name="Culprits">record keys of the items this compile skipped for hanging or faulting the driver (or its process
/// dying on them alone), not what builds on them; null = none</param>
public sealed record WarmResult(WarmOutcome Outcome, long Done, long Total, long Failed, TimeSpan Elapsed, long CacheGrowthBytes, string LogPath, string? Error,
    long Skipped = 0, IReadOnlyCollection<string>? Crashed = null, long DriverSkipped = 0, bool Resumable = false, bool AftermathDropped = false,
    IReadOnlyCollection<string>? CacheKeysAdded = null,   // cache keys of files Core added during the run, no warm process having had them open (NVIDIA on Linux: GlCacheNoDict)
    string? Full = null, long Invalid = 0, long Unsupported = 0,
    bool D3DSCacheNoFlush = false,   // the warm's last process skipped d3dscache.dll's flushes (WarmOptions.D3DSCacheNoFlush and its hook went in)
    long Cached = 0, long Known = 0, IReadOnlyCollection<string>? Culprits = null,
    bool Unnamed = false);   // a process died or was ended: what it didn't create is in no failed-keys file

public interface IWarmRun
{
    Task<WarmResult> Completion { get; }
    void Pause();
    void Resume();
    /// <summary>Graceful: in-flight compiles finish and the process exits normally so the driver writes its cache.
    /// Resume later with <see cref="WarmOptions.StartAt"/> = the stopped run's Done.</summary>
    void Stop();
    /// <summary>The last process's done event: it now exits while the driver writes its cache. Null: not known for this run.</summary>
    Task? LastDone => null;
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
    long StartupCompiles = 0,       // compiles while the game started up or loaded (FrameLog.StartupEnd, LoadSeconds), not in Compiles or WorstCompileMs
    long StateObjectsStartupCompiled = 0);   // state objects compiled while it started up or loaded, not in StateObjectsCompiled

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
    bool RecorderForeign = false,     // another SCSKiller copy placed the recorder in its folder: taken over once this copy wants it
    string? RecorderSkip = null,      // ScsKiller.Skip*; null = compatible
    string? RecorderNote = null,      // the last reconcile's pending or failed change; null = none
    string? RecorderFailure = null,   // the last install or update's failure while the recorder is wanted (GameRecord.RecorderFailure); null = none
    string? RecorderRefused = null,   // why the game's last launch wasn't recorded (ScsKiller.Refused, ScsKiller.NeverSaw); null = it recorded, or no launch since
    string? RecorderMod = null,       // what a foreign d3d12.dll in the game folder (or the one chained to it) calls itself; null = none
    string? AgsMod = null,            // "ReShade" or "OptiScaler" as the d3d12.dll behind ScsKiller.SkipAgsMod; null = another mod or none
    bool RecordAlongsideMod = false,  // the user chose to record alongside it (IScsKiller.SetRecordAlongsideMod)
    long? LastWarmNeedsRecording = null,   // of LastWarmSkipped, those the community recording flags as built at run time or by a mod
    long? LastWarmCrashed = null,   // the last complete warm: items skipped because they crash the GPU driver
    long? LastWarmCrashedBefore = null,   // ...of those, the crash keys an earlier compile on this driver found
    long? LastWarmDriverSkipped = null,   // ...and items it skipped for that compile (WarmResult.DriverSkipped)
    long? LastWarmKnown = null,           // ...of those, the ones that crashed this driver before (WarmResult.Known)
    CarefulCompile? Careful = null,        // AMD's careful compile (ScsKiller.CarefulThreads); null = not this vendor's
    long RecordingBytes = 0,        // the recorder's data files in the game folder plus SCSKiller's copy of its recording
    bool RecordingPaused = false,   // the recorder is in and its recording reached Settings.RecordingLimitMB: no new records
    long RecordedSinceWarm = 0,     // pipelines its recordings and packs have that the last complete warm didn't compile, apart from NewPipelines (derived)
    int CommunityDbPsos = 0,        // the manifest entry's pipelines while InCommunityDb
    double? PsoPerSecond = null,   // the game's last complete warm onto a cold cache (ScsKiller.ColdWarm), or a stopped one's until one completes; null = none measured
    FrameReport? LastFrames = null,    // the last launch's frame times (FrameLog); null = none measured
    bool ReShade = false,              // ReShade next to the game (Games.ReShade.Detect), with or without a ShaderMod
    string? ShaderMod = null,          // a ReShade add-on that changes the game's pipelines (Games.ReShade.Detect); null = none
    bool ShaderModBlocks = false,      // ...adds to every root signature, in a layer a copy can't reproduce (ReShadeInstall.Block): never compiled, recorded or shared
    bool ShaderModLayer = false,       // ...compiles through a copy of the layer (ScsKiller.LayerFor)
    bool ShaderModAsD3D12 = false,     // ...with ReShade installed as d3d12.dll: the recorder records under it only when chained
    bool RootUnconfirmed = false,      // a game the user added whose folder they haven't confirmed: never recorded (ScsKiller.SkipManual)
    bool StreamlineFirst = false,      // on NVIDIA, Streamline loads the first d3d12.dll (GameFiles.StreamlineFirst) and no preloader can go in (ScsKiller.PreloadName): never recorded (ScsKiller.SkipStreamline)
    string? Preload = null,            // NVIDIA, Streamline first: the name the recorder's preloader goes in under beside it (ScsKiller.PreloadName); null = none
    string? PreloadHeld = null,        // NVIDIA, Streamline first: the names it could go in under, when other files hold them all (ScsKiller.PreloadHeldSkip)
    bool PreloadHeldByOptions = false, // ...or the names WINEDLLOVERRIDES in its launch options has Wine load its own of, when they block the rest
    bool AgsHost = false,              // AMD, and AGS makes the device (GameFiles.AgsMakesTheDevice): the recorder goes in as amd_ags_x64.dll (ScsKiller.AgsName)
    bool Vac = false,                  // Valve Anti-Cheat (Games.Vac): never recorded, compiled from its files like any game
    bool Records11 = false,            // the recorder goes in as d3d11.dll (ScsKiller.Records11, ScsKiller.Frames11)
    bool RtUnseen = false,             // recorded long enough without ray tracing (GameRecord.RtUnseen) and planned since: its uncovered ray tracing isn't asked for
    bool RtToPlan = false,             // its plan asks for a ray tracing recording and a newer recording waits for the plan check (ScsKiller.RtPlanCheck)
    bool RtSkipped = false,            // its compiles leave ray tracing state objects and collections out (Settings.CompileRayTracing or RayTracingOverride)
    bool? RayTracingOverride = null,   // the game page's override of Settings.CompileRayTracing; null = the global setting
    bool? TraversalOverride = null,    // the game page's override of Settings.Traversal; null = the global setting
    bool LeftOutOfAddAll = false,      // the game page's "Leave out of Add all ready" (GameRecord.LeftOutOfAddAll)
    bool NoSharedHashes = false,       // the game page's "Use shared shader hashes" is off (GameRecord.NoSharedHashes)
    int? CacheCapGB = null,            // the game page's compile size limit (GameRecord.CacheCapGB); null = none
    int? PlanCacheCapGB = null,        // the limit the plan was built with (GameRecord.PlanCacheCapGB): what its left-out row names
    bool RecordedEnough = false,       // GameRecord.RecordedLong: asking for "5 minutes" again says nothing
    bool OfflineEligible = false,      // an EasyAntiCheat game of Games.OfflineEac on D3D12, an OS with offline sessions (none on Linux yet): an offline session may be offered
    bool OfflineRecord = false,        // the user allowed offline sessions for it (IScsKiller.SetOfflineRecording)
    bool OfflineRunning = false,       // a session SCSKiller started runs, or its files aren't out of the game folder yet (not one waiting for an Always record launch)
    bool AlwaysRecord = false,         // RiskEligible with "Always record" on (IScsKiller.SetAlwaysRecord): every launch records as a confirmed session would
    CommunityInfo? Official = null,    // the maintainer's official recording in use (ScsKiller.OfficialAllowed); null = none
    string? NoStutter = null,          // why it has no shader stutter (Games.GameVerdicts); null = not listed, or KnownStutter is
    bool CompileUnreached = false,     // NVIDIA: the warm's pipelines didn't serve the game (ScsKiller.IsUnreached): never queued but by hand
    bool NotPlanned = false,           // Unsupported by an "unsupported" verdict, or for a recording its anti-cheat blocks: not planned, unlike "unsupported-yet"
    bool NewOnly = false,             // Stale only for pipelines its last warm didn't compile (ScsKiller.StaleReason past WarmChanged): the rest still serve
    NewShadersMode? NewShadersOverride = null,    // null = the global setting
    DxApi? ApiOverride = null,         // the user's graphics API for the game (IScsKiller.SetApiOverride); null = Auto
    string? ApiDetected = null,        // what detection found while a choice or a recording may replace it; null = not a DirectX engine, nothing to choose
    string? MaximumSuggestion = null,  // Maximum mode is suggested for it on this GPU vendor and off (Games.MaximumMode): the entry's note, "" without one; null = not suggested
    bool? MaximumForGame = null,       // true: Maximum mode is on for this game alone (IScsKiller.SetGameMaximum); null = it follows Settings.MaximumPlans
    long? MaximumAddsBytes = null,     // AMD, Maximum off: at most what Maximum would add to the shared cache for this game (ScsKiller.MaximumAdds); null = not AMD, on, or no plan yet
    DateTimeOffset? PlayedAt = null,   // the start of the game's last run seen (the watcher's, or the recorder's first launch after the last complete warm); null = none seen, or that warm was of another build
    App.SimilarSystems? Similar = null,   // the public compat aggregate's line for this game on this GPU (App.CompatFeed); null = none
    bool CompileFailed = false,        // the last compile of the installed build failed or was refused (GameRecord.CompileFailedBuild)
    bool RiskEligible = false,         // Settings.RecordAntiCheatGames on, an OS with risk sessions (none on macOS yet, on Linux the Vulkan route's and a Steam launch's DirectX 12 under Proton, ScsKiller.ProtonDx12), anti-cheat or VAC detected, an engine on D3D12 or where a D3D11 recording records (ScsKiller.Records11Api), supported or not, no server verdict against it, a store launch SCSKiller can start (StoreLaunch.Supported), not OfflineEligible: a session in a game with its anti-cheat may be offered
    bool RiskNoLaunch = false,         // RiskEligible but for its store's launch: the page says why there is no session
    string? SessionNote = null,        // the last risk session ended with nothing recorded (ScsKiller.SessionNote); null = nothing to show
    BeforeAfterInfo? BeforeAfter = null,   // a before and after recording in progress or done (GameRecord.BeforeAfter); null = none
    KeptCache? Kept = null,   // AMD: its compiled cache kept aside (Settings.KeepCachesAside); null = none kept
    long? LastWarmUnbuilt = null,   // the last complete warm: D3D11 items SCSKiller can't build a draw for yet (ScsKiller.Unbuilt11)
    long? LastWarmInvalid = null,   // ...and Vulkan pipelines whose shaders fail SPIR-V validation (WarmResult.Invalid)
    long? LastWarmUnsupported = null,   // ...and pipelines needing a feature the GPU lacks (WarmResult.Unsupported)
    long? LastWarmTried = null,     // ...of the items it replayed (GameRecord.LastWarmTried); null = not known
    int? LastWarmFailedBy = null,   // ...what most of its failures were (ScsKiller.TopFailKind); null = not known
    bool Dxvk = false,              // DXVK as its d3d11.dll or dxgi.dll (Games.ReShade.Dxvk): it runs on Vulkan, a compile isn't used
    bool DxvkCompiled = false,      // ... except DXVK's d3d11.dll on NVIDIA, and on AMD for a Steam game: the warm runs through it (Warmer.DxvkDll)
    bool MfgUnlocker = false,       // The Witcher 3 with MFG Unlocker beside its exe (Games.ReShade.MfgUnlocker): its Path Traced Hair refuses the recorder's device hooks
    bool StarfieldLuma = false,     // Starfield Luma's SFSE plugin or ASI build (Games.ReShade.StarfieldLuma)
    string? RuntimeWarning = null,  // the warm's D3D12 runtime can't compile the plan's newest shaders (Warmer.RuntimeWarning); null = it can, not known, or anti-cheat (not read)
    long? CacheInUse = null,   // NVIDIA: of CacheOnDisk, what its files hold (NvidiaAppCache.UsedOf); null = not read, or another vendor
    long? WindowsCache = null,   // NVIDIA from 621: the bytes of its own D3DSCache folders on record, not in CacheOnDisk; null = none
    long? DeliveredShaders = null,   // the bytes of its Advanced Shader Delivery dbs (D3DSCache.Delivered); null = none
    bool? DeliveryFits = null,   // one of them was compiled by this driver (D3DSCache.Fits); null = not known, or none
    bool D3DSCacheCleanup = false,   // its D3DSCache dbs are trimmed (ScsKiller.D3DSCacheTrimmed) and Windows' automatic cleanup empties D3DSCache (D3DSCache.CleanedAutomatically)
    IReadOnlyList<ApiCompile>? KeptApis = null,   // complete compiles kept for its other graphics APIs (GameRecord.ApiCompiles); null = none
    bool ExeChosen = false)   // a store's game runs the exe the user picked (IScsKiller.SetGameExe)
{
    /// <summary>What its compiled cache takes on disk: the driver's part and Windows'. Null = unknown.</summary>
    public long? DiskBytes => CacheOnDisk is null && WindowsCache is null ? null : (CacheOnDisk ?? 0) + (WindowsCache ?? 0);

    /// <summary>A client anti-cheat (any value but None, Other included) or VAC: never recorded outside a confirmed risk or offline session.</summary>
    public bool AnyAntiCheat => AntiCheat != AntiCheat.None || Vac;
}

/// <summary>A complete compile kept for a graphics API the game doesn't run now (<see cref="App.ScsKiller.ApiKey"/>), on
/// <paramref name="DriverVersion"/>; <paramref name="CurrentDriver"/>: the driver in use now.</summary>
public sealed record ApiCompile(string Api, DateTimeOffset WarmedAt, string? DriverVersion, bool CurrentDriver);

/// <summary>A game's compiled AMD cache files kept aside, so they can go back after the driver removes them. Bytes: all of
/// them. Frees: what only the kept copy holds (removing it frees that). PutBackAt: the last time missing files went back, Late: after
/// the game had started. Unkept: files another app had open when they were due (HeldBy), so they stay only in the driver's cache.</summary>
public sealed record KeptCache(long Bytes, long Frees, DateTimeOffset? PutBackAt = null, int Unkept = 0, string? HeldBy = null, bool Late = false);

/// <summary>A launch's frame times from the recorder (<see cref="App.FrameLog"/>): its length, the startup stretch before
/// play (the game's own precompile and first load), the 1% low of play, every frame of 50 ms or more, and for a graph
/// the longest frame of each of <see cref="App.FrameLog.GraphColumns"/> equal slices of the launch (0 = no frame started in it).</summary>
public sealed record FrameReport(TimeSpan Duration, TimeSpan Startup, long Frames, double Low1PctFps, IReadOnlyList<Hitch> Hitches,
    IReadOnlyList<float> Peaks,
    long LaunchUnixMs = 0,    // the frame log's launch stamp: SessionLog takes its Duration (the last frame) for the launch's end
    double PlaySeconds = 0,   // play: after startup, before the quit window, pauses left out (FrameLog.InPlay)
    double AvgFps = 0,        // play frames / PlaySeconds
    CreateStats? Creates = null,   // the launch's csv creates split at startup; null = no csv launch
    long DisplayedFrames = 0,    // frame generation was on: the frames displayed, generated ones too (Frames: the game's own)
    IReadOnlyList<double>? Displayed = null,   // their ends, ms on the recorder's clock
    bool D3D11 = false);   // the csv's #api,d3d11: no create rows, so no hitch is a shader one

/// <summary>A launch's creates (<see cref="App.SessionLog"/>'s kinds and rules) split at <see cref="FrameReport.Startup"/>:
/// cache hits and compiles in play, compiles in startup, loads from the game's own pipeline library, ray tracing state
/// objects ready or compiled.</summary>
public sealed record CreateStats(long PlayHits, long PlayCompiles, long StartupCompiles, long Library, long StateObjectsReady, long StateObjectsCompiled)
{
    /// <summary>Pipelines ready from the cache in play; null = none created in play.</summary>
    public double? ReadyShare => PlayHits + PlayCompiles > 0 ? (double)PlayHits / (PlayHits + PlayCompiles) : null;
}

/// <summary>games\&lt;id&gt;\sessions\&lt;launch unix ms&gt;.json: one launch's frame report with its context, kept for
/// comparing launches. Its own fields, not a nested <see cref="FrameReport"/>, so the file stays small whatever the report
/// grows. <paramref name="SwapChain"/>: the csv's last <c>#swapchain</c> line ("2560x1440 fmt 28"); null = none written.
/// <paramref name="Tag"/>: "before" or "after" while a before and after pair pins it.</summary>
public sealed record SessionSnapshot(int V, long LaunchUnixMs, double DurationMs, double StartupMs, long Frames, double Low1PctFps,
    double PlaySeconds, double AvgFps, IReadOnlyList<Hitch> Hitches, IReadOnlyList<float> Peaks, CreateStats? Creates,
    string? DriverId, string? DriverVersion, string? Build, DateTimeOffset? WarmedAt, string? SwapChain,
    bool CleanExit = false,   // the csv has the launch's #end
    bool Overlay = false,     // the in-game countdown drew in this launch
    bool Pinned = false, string? Tag = null,
    bool D3D11 = false,       // the csv's #api,d3d11: the recorder ran as d3d11.dll, no create rows
    bool? FrameGen = false)   // on: FSR 3's swap chain presented generated frames, null: only a #framegen line (DLSS-G or XeSS loaded, on or off unseen)
{
    public const int Version = 1;
}

public enum BeforeAfterStage { BeforeArmed, BeforePlaying, Compiling, AfterReady, AfterArmed, AfterPlaying, Done }

/// <summary>A game's before and after recording as the game page shows it: the stage, the play time asked for, the last
/// problem (why a run didn't count, why the button waits), and in a playing stage the play counted so far.</summary>
public sealed record BeforeAfterInfo(BeforeAfterStage Stage, int Minutes, string? Problem, double PlayedSeconds,
    bool ClearPending = false);   // BeforeArmed but the cache couldn't be cleared: not armed, ContinueBeforeAfter retries the clear

/// <summary>One run's numbers over play (<see cref="App.FrameLog"/>'s definitions), as the report shows them.
/// <paramref name="Hitches"/>, <paramref name="HitchSeconds"/>, <paramref name="WorstHitchMs"/>: every frame of 50 ms or
/// more in play, shader or not (a DirectX 11 run has no split).</summary>
public sealed record RunStats(double PlaySeconds, int ShaderStutters, double FrozenSeconds, double WorstStutterMs,
    double? ReadyShare, long PlayCompiles, double Low1PctFps, double AvgFps, int OtherHitches,
    int Hitches = 0, double HitchSeconds = 0, double WorstHitchMs = 0);

/// <summary>The pair compared: both runs, whether counts are also shown per 10 minutes (play times differ by more than
/// 10%), whether the before run barely stuttered from shaders (under 3), and the report's warnings, one line each.
/// <paramref name="Dx11"/>: a run was recorded on DirectX 11 (<see cref="SessionSnapshot.D3D11"/>), which compiles at the
/// first draw, so shader stutters can't be told from other hitches and there is no ready share.</summary>
public sealed record Comparison(RunStats Before, RunStats After, bool PerTenMinutes, bool AlreadySmooth, IReadOnlyList<string> Notes, bool Dx11 = false);

public sealed record BeforeAfterResult(SessionSnapshot Before, SessionSnapshot After, Comparison Compare);
/// <summary>One launch's frame report in short (FrameSessions): when, how long, its 1% low and shader compilation stutters.</summary>
public sealed record FrameSession(long LaunchUnixMs, TimeSpan Duration, double Low1PctFps, int Stutters, bool D3D11 = false);

/// <summary>A frame of 50 ms or more, <paramref name="At"/> into the launch.</summary>
public sealed record Hitch(TimeSpan At, double Ms, HitchCause Cause);

/// <summary>Shader: a pipeline compile overlapped the frame. Other: no create did (streaming, the CPU, anything else).
/// Loading: startup, a load in play that created many pipelines, all fast, or one that compiled on many threads, none
/// the presenting one; LoadingShaders: a startup frame that cold
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

/// <summary>The graphics API a user picks for a game (<see cref="GameState.ApiOverride"/>). Stored by value: add at the end only.</summary>
public enum DxApi { D3D11, D3D12, Vulkan }

/// <summary>A recording downloaded from the community database: its PSO records, when it came, and
/// whether this PC's own recording is merged with it. OtherVersion: it doesn't match the game's files (another
/// build with none of its shaders here, or an index without shaders), so it isn't used.</summary>
public sealed record CommunityInfo(int Psos, DateTimeOffset DownloadedAt, bool WithLocalRecording, bool OtherVersion = false);

/// <summary>A game with documented shader-compilation stutter: how bad, one line why, the source (a URL or a citation),
/// and when the entry was last checked (yyyy-MM-dd).</summary>
public sealed record KnownStutter(StutterSeverity Severity, string Reason, string Source, string Date);
public enum StutterSeverity { Moderate, Severe }

/// <summary>Detected middleware as a player names it ("OptiScaler (FSR4)", "XeSS", "DLSS"), its DLL file names and the
/// PSOs its packs hold for those DLL versions: 0 = detected, compiled only after a recording sees them.</summary>
public sealed record MiddlewareTag(string Label, IReadOnlyList<string> Dlls, int Pipelines);

public enum DriverUpdateMode { Ask, WhenIdle, Off }

/// <summary>The Settings page's driver-update choices in order: Ask, When idle, Right away (When idle with
/// <see cref="Settings.DriverRightAway"/>), Off.</summary>
public static class DriverUpdateChoice
{
    public static int Of(Settings s) => s.OnDriverUpdate switch
    {
        DriverUpdateMode.Ask => 0, DriverUpdateMode.WhenIdle => s.DriverRightAway ? 2 : 1, _ => 3,
    };

    public static Settings With(Settings s, int choice) => s with
    {
        OnDriverUpdate = choice switch { 0 => DriverUpdateMode.Ask, 1 or 2 => DriverUpdateMode.WhenIdle, _ => DriverUpdateMode.Off },
        DriverRightAway = choice == 2,
    };
}

/// <summary>The app's colours: System follows Windows' app mode.</summary>
public enum AppTheme { System, Light, Dark }

public enum NewShadersMode { Off, Notify, CompileAndNotify, CompileSilently }

/// <summary>Whether the recorder places small resources in pre-made heaps (TraversalFix). Recommended: an unreleased build's, read as Off.</summary>
public enum TraversalMode { Off, Recommended, All }

public sealed record Settings(int Threads, WarmPriority Priority, DriverUpdateMode OnDriverUpdate, int BackgroundThreads,
    bool PauseWhileGaming,
    bool UseCommunityDb = true,   // download from the community shader hash database (signed in with "db") and use what was downloaded;
                                  // renamed from CommunityPlans (never settable, saved false) so existing settings.json start on
    bool MaximumPlans = false,    // also plan every stage pairing, not just every stage (per-stage caches reuse a stage in any pairing)
    bool StartWithWindows = true,  // the app starts in the notification area at sign-in (WindowsStartup)
    int MaxCompileMemoryGB = 0,    // the warm's memory budget; 0 = Auto (ScsKiller.AutoCompileMemoryGB: from the PC's RAM)
    bool WelcomeSeen = false,      // the first-run welcome dialog was closed (App.ShowWelcomeAsync)
    bool ShareRecordings = false,   // opted in to sharing recordings (welcome, Settings): anonymous uploads of this PC's recordings (ScsKiller.Sharing)
    bool ShareDiagnostics = false,  // opted in (welcome, Diagnostics.Asks, or Settings after its disclosure) to a compile's diagnostics when the driver rejected pipelines or the warm failed (ScsKiller.Diagnostics)
    bool DiagnosticsAsked = false,  // the diagnostics choice was answered, in the welcome or its own one-time question (Diagnostics.Asks): never asked again
    bool SharePromptDismissed = false,   // the post-sign-in share prompt was answered, either way (Account.OffersSharing): never asked again
    string? UpdateChannel = null,   // "stable" | "beta" | "alpha" | "internal"; null = the running build's own (UpdateChannels.Effective caps it by entitlement)
    bool RecordAllGames = true,     // unless a game's RecorderOverride says otherwise
    int RecordingLimitMB = 1024,    // per game: the recorder's db plus SCSKiller's copy of it; 0 = unlimited
    bool NotifyNewShaders = true,   // a notification when compiled games have new pipelines to compile (NewShaders)
    bool ActiveCheck = false,       // the anonymous daily check that counts active installs (ScsKiller.ActiveCheck), ticked in the welcome or Settings; off = nothing is sent
    bool ActiveCheckAsked = false,  // the daily check was chosen, in the welcome, its one-time question or Settings: nothing is sent before (ActiveCheck.Allowed)
    string? GpuNoticeDismissed = null,   // the GPU name whose "doesn't compile on this GPU" notice was closed (Format.GpuNotice)
    bool InstallUpdatesAutomatically = true,   // a downloaded update installs at the next start or quit (AutoInstall); off = only "Restart to update"
    bool ScanAtStart = true,   // a scan the user didn't ask for reads every game again where it changed; off = the last list (ScsKiller.Listed)
    bool CloseQuits = false,  // the window's close button quits like the tray's Quit (WindowClose); off = it hides to the notification area
    bool RecordingLimitChosen = false,   // the user picked RecordingLimitMB: a stored 256 without it is the earlier default (ScsKiller.Settings)
    bool TrayNoticeShown = false,   // the "still running" notification was shown (App.HideToTray): never again
    bool MaxCompileConfirmed = false,   // the game page's Max compile was confirmed once (DetailPage.OnMaxCompile): never asked again
    AppTheme? Theme = null,        // the app's colours; null = Windows' setting
    NewShadersMode? OnNewShaders = null,   // null = from NotifyNewShaders: false -> Off, true -> Notify
    GpuChoice? Gpu = null,         // the adapter SCSKiller compiles for (GpuBackends.Pick); null = the one with the most video memory
    bool RecordAntiCheatGames = false,   // sessions confirmed per launch may record anti-cheat games beside their anti-cheat (IScsKiller.StartRiskSession); off clears every game's switch
    bool CompileRayTracing = true,   // off: compiles leave ray tracing state objects and collections out, unless a game's RayTracingOverride says otherwise
    bool BeforeAfter = true,          // the game page offers the before and after recording
    bool BeforeAfterIntroSeen = false,   // its intro was shown once; "How it works" shows it again
    bool BeforeAfterSound = true,     // a sound when a run's play time is reached
    bool BeforeAfterOverlay = true,   // the countdown drawn in the game (BeforeAfterRecord.Overlay), the intro's last choice
    bool BeforeAfterMax = false,      // the after run's Max compile (BeforeAfterRecord.Max), the intro's last choice where it was offered
    bool BeforeAfterShowGpu = true,   // the report's image names the GPU
    bool ExperimentalIntel = false,   // compile on Intel GPUs (IntelBackend); the backend is chosen at start
    bool KeepCachesAside = true,   // AMD: keep each compiled game's cache files aside and put them back when the driver removed them
    bool CompatReportSent = false,   // a compatibility report was sent from this install: Rate this compile's popup drops its disclosure (DetailPage.ReportDialog)
    bool HddMode = false,   // a game never checked is read only when its page opens or it compiles (ScsKiller.CheckGame), never by a scan
    bool CompileNewGames = false,   // never-compiled games Add all ready would take compile when idle (NewShaders.NewGames)
    bool DriverRightAway = false,   // OnDriverUpdate WhenIdle: the rebuild starts at once (ScsKiller.RewarmRightAway); a field, not a mode: an older build reads it as When idle
    bool NewShadersRightAway = false,   // an automatic compile of new shaders starts at once (NewShaders.Queue), not when idle
    bool StartAtSignInGranted = false,   // a Flatpak, which can't read its sign-in entry: the Background portal left it on at the last answer (LinuxStartup.ApplyFlatpak)
    IReadOnlyList<string>? LeftOutFolders = null,   // a store's game installed in one of these folders or under it is never listed or read (ScsKiller.LeftOut)
    IReadOnlyList<string>? HddDrives = null,   // HddMode for the games on these drives only (GameFiles.DriveOf); HddMode on = every drive
    TraversalMode Traversal = TraversalMode.Off,   // the traversal stutter fix's games, unless a game's TraversalOverride says otherwise (TraversalFix)
    bool TraversalAsked = false);   // its one-time question was answered (TraversalFix.Asks): never asked again

/// <summary>Merging: a finished warm's dbs go into the game's D3DSCache folder (NVIDIA from 621), its progress in the bytes its checks and inserts read.</summary>
public enum QueueStage { Waiting, Indexing, Planning, Materializing, Warming, Merging, Paused, Done, Failed, Stopped, Saving, Finishing }
public sealed record QueueItem(string GameId, QueueStage Stage, WarmProgress? Progress, string? Error,
    string? Note = null,   // why it waits or is paused ("paused while <game> is running"); Done/Stopped: ScsKiller.WarmCounts (failed, skipped)
    bool PlanCheck = false,   // a background plan rebuild the app queued itself (ScsKiller.CheckPlans), not a compile: lists leave it out
    DateTimeOffset? StartedAt = null);   // its first running stage; null while it waits (ScsKiller.Set keeps it)

/// <summary><see cref="IScsKiller.AddManualGame"/>'s result: the added game, or the listed one the exe belongs to.</summary>
public sealed record ManualAdd(Game Game, bool Existed);

/// <summary>One game of <see cref="IScsKiller.RemoveRecordersFromAllGames"/>: its id (its data folder's name when it isn't
/// listed), the folder its record names the recorder in, and why something of SCSKiller's is left there; null when nothing is.</summary>
public sealed record RecorderRemoval(string GameId, string Folder, string? Left);

/// <summary>What <see cref="IScsKiller.RemoveRecordersFromAllGames"/> did: each game whose record names a recorder, the Vulkan
/// recorders' registrations it took out (on Windows their manifests, on Linux their implicit_layer.d entries), and why
/// some of those couldn't be taken out; null when none is left.</summary>
public sealed record RecorderRemovals(IReadOnlyList<RecorderRemoval> Games, IReadOnlyList<string> VulkanLayers, string? VulkanLeft = null);

public interface IScsKiller
{
    IGpuVendorBackend Vendor { get; }
    /// <summary>The adapters SCSKiller can compile for, when there are several to pick from (<see cref="Settings.Gpu"/>); else none.</summary>
    IReadOnlyList<GpuChoice> GpuChoices => [];
    /// <summary>DXGI lists an Intel adapter: Settings offers <see cref="Settings.ExperimentalIntel"/>.</summary>
    bool HasIntelGpu => false;
    /// <summary>The game's last launches with frame times, newest first (FrameSessions).</summary>
    IReadOnlyList<FrameSession> FrameHistory(string gameId) => [];
    /// <summary>What the game's Steam launch options need for it to read SCSKiller's compiled cache; null when it needs
    /// nothing there. <paramref name="make"/>: also makes the folders the line names, which the driver needs before the game
    /// runs. Reads Steam's files: not on the UI thread.</summary>
    LaunchLine? LaunchLineOf(string gameId, bool make = false) => null;
    /// <summary>One line on the compiled cache SCSKiller puts in Steam's own cache folder for the game, which the game reads
    /// with no launch options; null when it puts none there. Reads Steam's files: not on the UI thread.</summary>
    string? SteamCacheNote(string gameId) => null;
    Settings Settings { get; set; }

    // userRequested: the user's refresh, which also fetches the server's data whatever its age (ScsKiller.UserFetchEvery)
    Task<IReadOnlyList<GameState>> ScanAsync(CancellationToken ct, bool userRequested = false);     // cheap: reuses saved per-game results
    Task<IReadOnlyList<GameState>> RescanAsync(CancellationToken ct, bool userRequested = false);   // re-checks every game
    IReadOnlyList<GameState> Games { get; }
    /// <summary>The games' own D3DSCache folders, which the driver's cache usage doesn't count.</summary>
    long WindowsCacheBytes => Games.Sum(g => g.WindowsCache ?? 0);
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
    /// <summary><see cref="Compile"/> with the Maximum plan (every stage pairing) for this one compile, whatever the game's or
    /// Settings' Maximum mode; nothing is kept for the game, the next <see cref="Compile"/> is a standard one.</summary>
    void CompileMax(string gameId);
    /// <summary>Reorders a waiting item; 0 = next to run. The running item doesn't move.</summary>
    void MoveInQueue(string gameId, int index);
    void EnqueueWhenIdle(string gameId);         // same, but runs as a background rebuild only while the PC is idle
    void EnqueueRightAway(string gameId);        // the background rebuild without the wait for idle: it waits only while a game runs
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

    /// <summary>An Unreal game has a stored AES key (the user's, or one found in its exe).</summary>
    bool HasEncryptionKey(string gameId);

    /// <summary>Forgets the game's stored AES key. Call RefreshGame afterwards: the game is read again without it.</summary>
    void RemoveEncryptionKey(string gameId);

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

    void SetNewShadersMode(string gameId, NewShadersMode? value);

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
    /// <summary>A fresh full anti-cheat check of the game's install, for the confirmation to name what is there now.</summary>
    Task<AntiCheat> CheckAntiCheatAsync(string gameId);
    /// <summary>Puts the recorder in, starts the game through its normal launch with its anti-cheat present, and records that
    /// one process at the user's own risk (risk=1 in the attestation). Throws InvalidOperationException, with nothing
    /// written, unless the OS has risk sessions, the user confirmed this launch, Settings.RecordAntiCheatGames is on, the game is
    /// <see cref="GameState.RiskEligible"/> with its switch on and not running, and a fresh full check still finds
    /// <paramref name="shown"/>. The task ends once the game exited and its folder is back to how it was.</summary>
    Task StartRiskSession(string gameId, bool confirmed, AntiCheat shown);
    /// <summary>"Always record" for a <see cref="GameState.RiskEligible"/> game with its switch on: on (once the user confirmed
    /// <paramref name="shown"/>, and a fresh full check still finds it), SCSKiller keeps a session ready while it runs and
    /// binds the game's next launch, again after each one; off takes out the waiting session's files, a running one's at
    /// its end. Throws InvalidOperationException for an unconfirmed or ineligible game.</summary>
    void SetAlwaysRecord(string gameId, bool on, bool confirmed, AntiCheat shown);
    /// <summary>A running game's folder is left alone: call again when it exits. Null = every game.</summary>
    void ReconcileRecorders(string? gameId = null);
    /// <summary>SCSKiller's own uninstall of the recorders, run now (<see cref="App.ScsKiller.RemoveAllRecorders"/>), for an
    /// uninstall that can't run it. From then on this process installs no recorder until it starts again: a game turned on
    /// gets <see cref="App.ScsKiller.RemovedFromAllNote"/>, and removals and Off go on. It first waits for the recorders' work
    /// in progress (a compile's import may hold it). A cancel leaves the games not reached yet as they are. Not on the UI
    /// thread.</summary>
    RecorderRemovals RemoveRecordersFromAllGames(CancellationToken ct = default) => new([], []);
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
    /// <summary>Leaves a store's game out of the library and of everything automatic until <see cref="ShowGame"/>; nothing of
    /// it is deleted. A before and after recording in progress is cancelled and the recorder taken out first (its switch goes
    /// Off). InvalidOperationException while it runs or compiles, when the recorder can't come out, or for a game the user added.</summary>
    void HideGame(string gameId);
    /// <summary>Lists a hidden game again. ArgumentException for a game that isn't hidden.</summary>
    void ShowGame(string gameId);
    /// <summary>Leaves the folder and everything under it out of the library (<see cref="Settings.LeftOutFolders"/>), after taking
    /// the recorder out of the listed games there, as <see cref="HideGame"/> does. A game the user added stays. ArgumentException
    /// for a path that isn't a folder, InvalidOperationException as HideGame's. The next scan drops the games.</summary>
    void LeaveOutFolder(string folder) => Settings = Settings with { LeftOutFolders = [.. Settings.LeftOutFolders ?? [], folder] };
    /// <summary>Takes the folder out of <see cref="Settings.LeftOutFolders"/>: the next scan lists its games again.</summary>
    void ScanFolderAgain(string folder) =>
        Settings = Settings with { LeftOutFolders = [.. (Settings.LeftOutFolders ?? []).Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))] };
    /// <summary>The hidden games the last scan found installed.</summary>
    IReadOnlyList<Game> HiddenGames { get; }
    /// <summary>Deletes the recorder's data files (scskiller.db, its csv and log in the game folder, SCSKiller's copies of
    /// the recording), never the recorder itself; the next compile plans from the game files. False when there was nothing
    /// to delete. Throws InvalidOperationException while the game runs or a compile of it is in progress.</summary>
    bool ClearRecording(string gameId);
    /// <summary>Every driver cache file on the PC in one item, grouped by whose it is (the Cache page). Reads names, sizes
    /// and write times only, and D3DSCache dbs: not on the UI thread.</summary>
    App.CacheInventory CacheInventory();
    /// <summary>Deletes the Cache page's items as it read them (<see cref="App.ScsKiller.DeleteCaches"/>): what changed, is in
    /// use, or belongs to a running or compiling game is skipped and listed.</summary>
    App.CacheDeleteResult DeleteCaches(IReadOnlyList<App.CacheItem> items);
    /// <summary>Bytes of the cache files an older NVIDIA driver wrote; 0 on other vendors.</summary>
    long OldDriverBytes();
    /// <summary>Deletes the game's kept-aside cache files (<see cref="GameState.Kept"/>), freeing <see cref="KeptCache.Frees"/>.</summary>
    void DropKeptCache(string gameId) { }
    /// <summary>Before the game is launched: its kept-aside files the driver removed go back. Never throws.</summary>
    void PutBackKept(string gameId) { }
    /// <summary>The game's DirectX over what detection found (<see cref="GameState.ApiDetected"/>); null = Auto. Where
    /// detection only guessed, a recording of this PC that shows the API wins over it. The plan is rebuilt for it. Throws
    /// InvalidOperationException while a compile of the game is in progress.</summary>
    void SetApiOverride(string gameId, DxApi? value);
    /// <summary>A store's game runs <paramref name="exe"/>, a file of its install, over the exe SCSKiller picks; null = Auto. Kept
    /// across scans, store updates and SCSKiller builds, and an Unreal bootstrap still resolves to its Shipping exe. ArgumentException
    /// for an exe outside the install, a game the user added or one of a collection, InvalidOperationException while it compiles.</summary>
    void SetGameExe(string gameId, string? exe) => throw new NotSupportedException();
    /// <summary>Maximum mode on or off for this game alone (<see cref="GameState.MaximumSuggestion"/>'s "Turn on for this
    /// game"); InvalidOperationException while a compile of it runs.</summary>
    void SetGameMaximum(string gameId, bool on);
    /// <summary>Whether the game's compiles include ray tracing state objects and collections; null = Settings.CompileRayTracing.</summary>
    void SetGameRayTracing(string gameId, bool? on);
    /// <summary>The game's traversal stutter fix (<see cref="App.TraversalFix"/>); null = Settings.Traversal. Its recorder's ini is written again.</summary>
    void SetGameTraversal(string gameId, bool? on);
    /// <summary>What a compile's plan may add to the game's shader cache, in GB; null = no limit. The plan is rebuilt at the next compile.</summary>
    void SetGameCacheCap(string gameId, int? gb);
    /// <summary>Whether "Add all ready" skips the game; adding it by hand still works.</summary>
    void SetGameLeftOutOfAddAll(string gameId, bool on);
    /// <summary>The game page's "Use shared shader hashes": off, nothing is downloaded for the game and its plans leave the downloads out.</summary>
    void SetGameUseShared(string gameId, bool on);
    /// <summary>Why the game can't start a before and after recording now, one line; null = it can.</summary>
    string? BeforeAfterBlocked(string gameId);
    /// <summary>Clears the game's caches (the game's own too), remembers the driver and the build, and arms the before run:
    /// the app starts the game next. Throws InvalidOperationException with the reason (<see cref="BeforeAfterBlocked"/>).</summary>
    /// <paramref name="max"/>: the after run's compile is Max compile's, where it adds anything (ScsKiller.MaxCompileAdds).
    void StartBeforeAfter(string gameId, int minutes, bool overlay = true, bool max = false);
    /// <summary>Before each run's launch: installs or checks the recorder (in and armed). Throws InvalidOperationException
    /// with why it isn't.</summary>
    void BeforeAfterRecorderReady(string gameId);
    /// <summary>AfterReady: arms the after run (the app starts the game next), or, when the game ran outside the flow since
    /// the compile, clears and compiles again first.</summary>
    void ContinueBeforeAfter(string gameId);
    /// <summary>Done: clears and compiles again, then a new after run.</summary>
    void RecordAfterAgain(string gameId);
    /// <summary>Ends the pair at any stage ("Cancel", "Start over") and unpins its snapshots; <paramref name="compile"/>
    /// queues a compile (a cancelled flow left the cache cleared).</summary>
    void EndBeforeAfter(string gameId, bool compile);
    /// <summary>The pair's two snapshots compared; null unless Done.</summary>
    BeforeAfterResult? BeforeAfterReport(string gameId);
    /// <summary>The compatibility report this PC sent for the game's installed build; null = none. Reads a file.</summary>
    App.CompatSent? ReportSent(string gameId);
    /// <summary>Sends the game's compatibility report (<see cref="App.CompatReport"/>): only on the user's Send rating, whatever
    /// Settings.ShareRecordings holds. Remembered for the build once the server took it. Null when sent, else why not in plain
    /// words. InvalidOperationException when the game can't be reported (<see cref="App.CompatReport.WhyNot"/>).</summary>
    Task<string?> SendReportAsync(string gameId, App.CompatChoice choice, bool frameGen, bool upscaler, bool mods, CancellationToken ct = default);
    /// <summary>This start's server refused a compatibility report from Linux: it takes none yet.</summary>
    bool LinuxReportsRefused => false;
    /// <summary>"Report a problem"'s text for the game (<see cref="App.ProblemReport.Build"/>): this PC's versions, <paramref name="page"/>
    /// as its page shows it and, with <paramref name="withLog"/>, app.log's latest lines about it. Reads files.</summary>
    string ProblemReport(string gameId, string store, IReadOnlyList<(string Label, string Value)> page, bool withLog);
    /// <summary>Sends <paramref name="report"/> as shown, only on the user's Send (<see cref="App.ProblemReport.SendAsync"/>), and
    /// keeps the id the server gave it in <see cref="ProblemsSent"/>.</summary>
    Task<(string? Id, string? Problem)> SendProblemAsync(string gameId, string message, string report, CancellationToken ct = default);
    /// <summary>The problem reports sent from this PC, newest first. Reads a file.</summary>
    IReadOnlyList<App.SentProblem> ProblemsSent();
}
