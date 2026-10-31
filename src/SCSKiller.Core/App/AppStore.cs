using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SCSKiller.Core.App;

/// <summary>What SCSKiller remembers per game (games\&lt;id&gt;\state.json).</summary>
public sealed class GameRecord
{
    public string? IndexContentHash { get; set; }
    public List<string>? IndexLegacyHashes { get; set; }     // ShaderIndex.LegacyHashes of that index: community entries are looked up by them too
    public string? IndexGameVersion { get; set; }           // Game.Version when IndexContentHash was taken (sharing needs the pair)
    public string? IndexExeStamp { get; set; }              // exe size + write time when IndexContentHash was taken
    public string? KeysIndexHash { get; set; }              // the index whose shaders the recorder's keys file names; null: none
    public int? ShaderCount { get; set; }
    public Plan? Plan { get; set; }
    public DateTimeOffset? PlanBuiltAt { get; set; }
    public int PlanVersion { get; set; }                     // Planner.Version the plan was built with
    public int WarmedPlanVersion { get; set; }               // PlanVersion of the plan the last complete warm replayed
    public int PlanCheckFailed { get; set; }                 // Planner.Version a background plan check failed at: not retried by itself; 0 = none
    public string? IndexFailedStamp { get; set; }            // ExeStamp of the build a recorder's background index failed on: not read again until it changes
    public int? PlanReaderVersion { get; set; }              // IEngineReader.ReaderVersion of the index the plan was built from; null = 1
    public int? WarmedReaderVersion { get; set; }            // PlanReaderVersion of the plan the last complete warm replayed
    public string? PlanRules { get; set; }                   // RulesFile.Hash of the rules the plan was built with; null = the embedded rules
    public string? WarmedPlanRules { get; set; }             // PlanRules of the plan the last complete warm replayed
    public string? IdleHeldBack { get; set; }                // DriverId|RulesFile.Hash a "when idle" warm was held back for (ScsKiller.RulesGrew); null = none
    public string? PlanItems { get; set; }                   // ScsKiller.PlanFingerprint of plan.bin's records (null: not taken, or not readable)
    public string? WarmedPlanItems { get; set; }             // PlanItems of the plan the last complete warm replayed
    public long? WarmedPlanCount { get; set; }               // ...and its pipelines (ScsKiller.PlanItems); null = not recorded
    public string? PlanKeysFile { get; set; }                // the plan's planner-made pipelines (KeyFiles), in the game's folder; null = not kept
    public string? WarmKeysFile { get; set; }                // what the last complete warm replayed (KeyFiles); null = a warm from before they were kept
    public PendingCount? Pending { get; set; }               // the last count of pipelines new since the warm (ScsKiller.PendingOf)
    public bool PlanPerStage { get; set; }                  // the plan has each stage unit once, not every pairing (per-stage cache, not Maximum)
    public bool? PlanMaximumCapped { get; set; }            // the plan was built with Maximum's per-game cap (PlanBuilder.MaximumExtras); null = before it
    public long? PlanCacheBudget { get; set; }              // VendorCaps.WarmCacheBudget the plan was built with (ScsKiller.PlanCaps); null = none, or before it was kept
    public bool PlanD3DSCacheCharged { get; set; }          // under that budget each pipeline counted the Windows shader cache too (ScsKiller.D3DSCachePsoBytes)
    public bool WarmedPerStage { get; set; }                // PlanPerStage of the plan the last complete warm replayed
    public string? WarmedDelivery { get; set; }             // the game's Advanced Shader Delivery the last complete warm left out (D3DSCache.Delivery.Stamp); null = none
    public string? PlanMiddleware { get; set; }             // MiddlewarePacks.Fingerprint when the plan was built (DLL versions + pack sizes)
    public string? PlanCommunity { get; set; }              // the community recording (object id) the plan was built with; null = none
    public string? PlanOfficial { get; set; }               // the official recording (object id) the plan was built with; null = none
    public string? PlanSteam { get; set; }                  // ScsKiller.SteamDownloads when the plan was built; null = none
    public string? PlanPsoCache { get; set; }               // ScsKiller.PsoCacheInUse when the plan was built; null = none
    public string? PlanMaps { get; set; }                   // ScsKiller.MapsFingerprint of the index the plan was built from
    public string? D3DSCacheFolder { get; set; }            // the game's own D3DSCache folder (named by its exe path), found again once emptied
    public string? D3DSCacheExe { get; set; }               // the exe path that folder's dbs name
    public bool WarmedD3DSCache { get; set; }               // the last complete warm's D3DSCache dbs were kept, not deleted
    public Dictionary<string, long>? WarmedD3DSCacheBytes { get; set; }   // NVIDIA: the game's D3DSCache dbs and their lengths after that warm's fold; null = not recorded
    public DateTimeOffset? WarmedD3DSCacheAt { get; set; }  // ...when they were read: a db made after it holds none of the warm's
    public string? D3DSCacheMade { get; set; }              // the D3DSCache folder SCSKiller made before the game's first run
    public string? D3DSCacheLinked { get; set; }            // why the last fold didn't follow the game's linked D3DSCache folder, for the game page
    public Dictionary<string, string>? D3DSCacheRefused { get; set; }   // staged or waiting folder -> both folders' db stamps and why the last merge refused it
    public string? TrimmedLogged { get; set; }              // the trimmed cache reason app.log last reported: once per eviction, not per start
    public DateTime? D3DSCacheSince { get; set; }           // staged warms' runtime cache folders made since then aren't folded yet
    public bool D3DSCacheStrandChecked { get; set; }        // AdoptStranded looked once for a 1.3.1 warm's unfolded staged folders
    public bool CompiledUntold { get; set; }                // a compile the user started ended before its fold put every db in: no "is compiled" yet
    public bool D3DSCacheDamaged { get; set; }              // a staged db failed SQLite's check at the fold: compile again
    public Dictionary<string, string>? D3DSCacheChecked { get; set; }   // the game's D3DSCache dbs by path as the last fold checked or wrote them (D3DSCache.CheckStamp), a refused identity under "<path>|identity"
    public IdentityStamp? D3DSCacheStamp { get; set; }      // the identity the last fold that put dbs in stamped, and why (outcome reports)
    public DateTimeOffset? D3DSCacheStampAt { get; set; }   // ...when
    public string? D3DSCacheStampFolder { get; set; }       // ...in which D3DSCache folder (a made one before the game named it)
    public IdentityReport? D3DSCacheLaunch { get; set; }    // that stamp and the game's adapter db identity at the exit of the first run after the warm's fold
    public DateTimeOffset? D3DSCacheLaunchAt { get; set; }  // ...the D3DSCacheStampAt it was read for
    public FoldKeys? D3DSCacheFoldKeys { get; set; }       // NVIDIA: the game's adapter db right after the fold that put the last complete warm's dbs in
    public HitCheck? HitCheck { get; set; }                 // the first run after that fold, read at its exit (outcome reports)
    public DateTimeOffset? HitCheckAt { get; set; }         // ...the D3DSCacheFoldKeys.At it was read for
    public PlayWindow? HitCheckRun { get; set; }           // ...the run it was read at the exit of: a csv report carries it only for that launch
    public long ResumeAt { get; set; }                       // Done of a stopped warm of the current plan
    public long ResumeItems { get; set; }                    // items the stopped segments of that warm created...
    public double ResumeSeconds { get; set; }                // ...and their time: a complete warm's time and rate include them
    public long ResumeFailed { get; set; }                   // ...and their failures: a resumed warm counts only its own
    public long ResumeDriverSkipped { get; set; }            // ...and their skips (WarmResult.DriverSkipped)
    public long ResumeKnown { get; set; }                    // ...of those, the known ones (WarmResult.Known)
    public long ResumeInvalid { get; set; }                  // ...and their Vulkan pipelines never created (WarmResult.Invalid)
    public long ResumeUnsupported { get; set; }              // ...and those needing a feature the GPU lacks (WarmResult.Unsupported)
    public string? ResumeGpu { get; set; }                   // ...on this driver (ScsKiller.DriverId): another one's cache lacks them; null = unknown, as another
    public string? ResumeLayer { get; set; }                 // ...through this ReShade layer (ReShadeInstall.Fingerprint); null = none
    public string? ResumeWork { get; set; }                  // ...in this work folder (ScsKiller.WorkOrder); null = not taken, the plan's rebuild starts over
    public string? ResumeExe { get; set; }                   // ...staged as this exe name (another name's cache lacks them); null = unknown, as another
    public string? ResumeAgs { get; set; }                   // ...registering this AGS app name ("" = none; another name's cache lacks them); null = an earlier build's
    public string? ResumeDelivery { get; set; }              // ...leaving out what this Advanced Shader Delivery served (D3DSCache.Delivery.Stamp); null = none
    public bool ResumeAftermath { get; set; }                // ...starting NVIDIA Aftermath as the game does (its shader debug info is another key)
    public bool ResumeLeftOut { get; set; }                  // ...leaving out what earlier warms compiled (CompiledItems): its item numbers count in that set
    public long ResumeSkipEnd { get; set; }                  // where the window the last give-up skipped ends (ScsKiller.GiveUpSkip)...
    public long ResumeSkipWidth { get; set; }                // ...and its items
    public string? WarmedDriverVersion { get; set; }         // shown; earlier builds also compared it
    public string? WarmedDriverId { get; set; }              // what staleness compares (ScsKiller.DriverId); null = an earlier build's warm
    public DateTimeOffset? WarmedAt { get; set; }
    public TimeSpan? LastWarmTime { get; set; }
    public long? LastCacheGrowthBytes { get; set; }
    public long? LastWarmFailed { get; set; }                // the last complete warm: PSOs the driver rejected
    public long? LastWarmSkipped { get; set; }               // ...and PSOs skipped: a shader not in this install (never replayed)
    public long? LastWarmNeedsRecording { get; set; }        // ...of those, flagged by the community recording: only a recording here has them
    public long? LastWarmCrashed { get; set; }               // ...and items skipped because they crash the GPU driver
    public long? LastWarmCrashedBefore { get; set; }         // ...of those, the crash keys an earlier compile on this driver found
    public long? LastWarmDriverSkipped { get; set; }         // ...and items skipped for that compile (WarmResult.DriverSkipped)
    public long? LastWarmKnown { get; set; }                 // ...of those, the ones that crashed this driver before (WarmResult.Known)
    public long? LastWarmUnbuilt { get; set; }               // ...and D3D11 items SCSKiller can't build a draw for yet (ScsKiller.Unbuilt11)
    public long? LastWarmInvalid { get; set; }               // ...and Vulkan pipelines whose shaders fail SPIR-V validation (WarmResult.Invalid)
    public long? LastWarmUnsupported { get; set; }           // ...and pipelines needing a feature the GPU lacks (WarmResult.Unsupported)
    public long? LastWarmTried { get; set; }                 // ...of the items it replayed (WarmResult.Total: what earlier compiles created is left out)
    public int? LastWarmFailedBy { get; set; }               // ...what most of its failures were (ScsKiller.TopFailKind); null = not known
    public long? WarmedRoomLeft { get; set; }                // ...and it ended where the compile's cache held no more (WarmResult.Full): the items it didn't reach, 0 = not known; null = it reached the end
    public HashSet<string> CrashKeys { get; set; } = [];     // record keys of items whose create removed the D3D12 device or faulted in the driver: every warm skips them
    public string? CrashKeysDriver { get; set; }             // the driver they crashed (ScsKiller.DriverId): another driver's are dropped at its first compile, which tries them again
    public HashSet<string> KnownKeys { get; set; } = [];     // record keys of items that hung or faulted KnownKeysDriver (WarmResult.Culprits): its warms skip them, at most ScsKiller.MaxKnownKeys
    public string? KnownKeysDriver { get; set; }             // ScsKiller.DriverId: another driver's are dropped at its first compile, which tries them again
    public string? WarmedIndexHash { get; set; }
    public string? WarmedExeStamp { get; set; }             // exe size + write time at the warm: a game patch changes it
    public string? WarmedGameVersion { get; set; }          // Game.Version (store build id) at the warm; preferred over the exe stamp
    public string? WarmedLayer { get; set; }                // the ReShade layer the warm ran through (ReShadeInstall.Fingerprint); null = none
    public string? WarmedIndexStamp { get; set; }           // IEngineReader.IndexStamp when the warm's index was taken; null = ""
    public double? BytesPerPso { get; set; }                // measured by the last complete warm onto a cold cache (ScsKiller.ColdWarm)
    public double? PsoPerSecond { get; set; }               // ...and this, or a stopped one's until a warm completes
    public string? PsoPerSecondDriver { get; set; }         // the driver it was measured on (GpuSnapshot.Id); null = WarmedDriverId
    public DateTimeOffset? RecordingImportedAt { get; set; }  // when an import last added records
    public string? RecordingInbox { get; set; }             // the game folder's scskiller.db (size:write ticks) when last imported
    public string? RecordingIndexHash { get; set; }         // the index build whose shaders the recording names by hash only; null: not checked since an import
    public Dictionary<string, string> RecorderFiles { get; set; } = [];   // file name in the exe folder -> SHA-256 we installed (scskiller.ini: AppStore.LoadInis)
    public bool IniIsUsers { get; set; }                     // an earlier build's: the scskiller.ini there is the user's (now its folder's IniOwner)
    public string? RecorderExe { get; set; }                 // the game's exe while a recorder is installed: uninstall finds it without a scan
    public string? RecorderInstallDir { get; set; }          // and its install root, for the running check
    public string? VulkanLayer { get; set; }                 // the Vulkan recorder's manifest registered for this game (VulkanLayer); null = none
    public string? VulkanLayerSha { get; set; }              // ...and the SHA-256 of the layer's copy beside it
    public List<string> VulkanInboxes { get; set; } = [];   // the layer's inboxes the exe had while it recorded (its file key changes when Steam replaces it)
    public Dictionary<string, string>? VulkanCacheEnv { get; set; }   // the last session's driver-cache variables (VulkanLayer.CacheEnv); null = none
    public string? VulkanDevice { get; set; }   // the last session's Vulkan device (VulkanLayer.Device); null = none recorded
    public DateTimeOffset? RanElevated { get; set; }   // the last launch seen ran as administrator, which loads no Vulkan recorder; null = not seen so
    public string? RecorderMoveFrom { get; set; }            // a recorder moving next to the game's exe: the old exe, until SCSKiller's files are out of its folder
    public string? RunsExe { get; set; }                    // the exe of its install the game was seen running when discovery named another: used instead (ScsKiller.Following)
    public string? RunsExeFrom { get; set; }                // ...discovery's exe then, and the build (store version, else RunsExe's size and write time):
    public string? RunsExeBuild { get; set; }               // either changed, RunsExe is dropped
    public DateTimeOffset? RunsExeAt { get; set; }          // when the watcher last followed it: the launch it ran in wasn't recorded,
    public string? RunsExeWas { get; set; }                 // ...the recorder was stamped for this exe then
    public bool RunsExeProven { get; set; }                 // ...an Unreal game's exe beside its Shipping exe that ran alone and made a device (ScsKiller.FollowUnrealOther)
    public bool RunsExeRenderer { get; set; }               // ...a renderer the game's exe starts below its folder (ScsKiller.FollowRenderer): never followed away from
    public string? RecorderMoveTo { get; set; }              // ...and the game's exe it moves next to: the uninstall hook's running check covers both
    public DateTimeOffset? RecorderGoneSince { get; set; }   // the scan that first saw the game uninstalled with the recorder still in its folder (ScsKiller.CleanUninstalled)
    // null = not migrated: the first reconcile makes an installed recorder of ours On (the user put it there), else Default
    public RecorderOverride? Recorder { get; set; }
    public bool RecordAlongsideMod { get; set; }             // opt-in: install the recorder where a mod's d3d12.dll is, chained to it
    public ChainedDll? RecorderChained { get; set; }         // that mod's d3d12.dll, renamed for the chain: put back when the recorder goes
    public HashSet<string> RecorderOrphans { get; set; } = [];   // ...left unused there: the mod's uninstall deleted the proxy in its place (ScsKiller.Orphaned)
    public bool RecorderRollback { get; set; }               // a failed install couldn't take the proxy out: the next reconcile does
    public bool RecorderClaimed { get; set; }                // taken over from another copy: its recorder is replaced by this build's, even a newer build's
    public string? RecorderFailure { get; set; }             // the last install or update's failure as the game page shows it (ScsKiller.InstallFailed); null = none
    public bool OfflineRecord { get; set; }                  // opt-in: offline sessions without EasyAntiCheat may be started (Games.OfflineEac)
    public bool AlwaysRecord { get; set; }                   // opt-in: every launch records as a confirmed risk session would (ScsKiller.SetAlwaysRecord)
    public AntiCheat AlwaysRecordShown { get; set; }         // ...the anti-cheat the user confirmed it with: another one found turns it off
    public OfflineSession? OfflineSession { get; set; }      // one was started and its folder isn't back to how it was yet
    public bool KeysPending { get; set; }                    // the keys file wasn't rewritten while the game ran: the next refresh or reconcile does
    public HashSet<string> CacheKeys { get; set; } = [];     // driver-cache application keys seen open by this game's warms or the game (IAppCache)
    public HashSet<string> GameKeys { get; set; } = [];      // ...of them, the ones the game's own process held open
    public Dictionary<string, string> GameKeyDrivers { get; set; } = [];   // ...each one's driver when last held: "<DriverId>|<driver version>"; none = unknown (an earlier build's)
    public string? HeldKeys { get; set; }                    // AMD: the D3D12 ones its process held at the last sighting that held any, comma-separated (a string: arrays merge as sets); null = none seen since this was kept
    public HashSet<string>? WarmedKeys { get; set; }         // ...the ones the last complete warm held open; null = not recorded
    public HashSet<string>? WarmedFiles { get; set; }        // names of those keys' cache files after that warm (AMD: D3D12 ones); null = not recorded
    public Dictionary<string, long>? WarmedFileBytes { get; set; }   // ...and the length of each then; null = not recorded
    public Dictionary<string, long>? WarmedFileUsed { get; set; }    // ...and NVIDIA: the bytes each held then (NvidiaAppCache.UsedBytes), where readable; null = not recorded
    public Dictionary<string, string>? WarmedFileIds { get; set; }   // ...and AMD: the file id of each then (AmdKept.Id), which a file made again under the name doesn't keep; null = not recorded
    public string? WarmedSteamParcId { get; set; }           // AMD, a Vulkan warm into Steam's AMDv1 (ScsKiller.SteamParc): its .parc's file id then; null = not recorded
    public long? WarmedSteamParcBytes { get; set; }          // ...and its length then
    // The exe file name exactly as the game's own process was launched (its case can differ from the file on disk, and AMD's
    // cache key is case-sensitive): from the recorder's #session marker or the running game's module path; null = not seen.
    // Only kept when it equals the install's exe file name apart from case. Warms stage this name (ScsKiller.WarmExeName).
    public string? LaunchedExeName { get; set; }
    public DateTimeOffset? LaunchedExeSeenAt { get; set; }  // when LaunchedExeName was seen: a later sighting replaces it
    public string? WarmedExeName { get; set; }              // the name the last complete warm staged; null = the install's file name
    public bool WarmedAftermath { get; set; }               // NVIDIA: the last complete warm started Aftermath as the game does (NvAftermath.Of)
    public string? AftermathRetry { get; set; }             // NVIDIA: "<driver id>|<exe>" of the last complete warm that tried Aftermath after a miss (NvAftermath.Retry): once per driver and exe, a guessed one (0xa) "<build>|<exe>": once per build
    public long? AftermathPlainHits { get; set; }           // ...a guessed one: the hits of the plain compile's first launch, which its own first launch must beat; null once judged
    public bool AftermathLost { get; set; }                 // ...it didn't: the next compile is plain (until a complete one)
    public string? WarmedAgsApp { get; set; }               // AMD: the AGS app name the last complete warm registered; null = a plain device
    public bool AgsMissed { get; set; }                     // a launch after such a warm showed it missed the game: warms stay plain until the game is seen holding a D3D12 key
    public string? AgsSeenApp { get; set; }                 // AMD: the AGS app name the game's own process registered (the recorder's #ags line); null = none seen
    public string? AgsSeenEngine { get; set; }              // ...and its engine name
    public uint? AgsSeenAppVersion { get; set; }            // ...and its app and engine versions; null = unspecified (seen by an earlier build)
    public uint? AgsSeenEngineVersion { get; set; }
    public bool AgsIneligible { get; set; }                 // AMD: anti-cheat was seen in this game; never on the AGS hook path again (unless a risk session)
    public bool Careful { get; set; }                       // AMD: compile in passes on few threads (ScsKiller.CarefulThreads)
    public NewShadersMode? NewShaders { get; set; }         // the game page's override of Settings.OnNewShaders; null = the global setting
    public bool? Maximum { get; set; }                      // Maximum mode for this game alone (ScsKiller.SetGameMaximum); null = Settings.MaximumPlans
    public bool? RayTracing { get; set; }                   // ray tracing state objects and collections for this game (ScsKiller.SetGameRayTracing); null = Settings.CompileRayTracing
    public bool? Traversal { get; set; }                    // the traversal stutter fix for this game (ScsKiller.SetGameTraversal); null = Settings.Traversal
    public bool LeftOutOfAddAll { get; set; }               // "Add all ready" skips the game (ScsKiller.SetGameLeftOutOfAddAll)
    public bool NoSharedHashes { get; set; }                // "Use shared shader hashes" off for this game: nothing is downloaded for it, its plans leave the downloads out (ScsKiller.SetGameUseShared)
    public bool SharedHashesOn { get; set; }                // ...on for this game whatever Settings.UseCommunityDb holds; neither = the setting
    public int? PlanCacheCapGB { get; set; }                // CacheCapGB the plan was built with
    public int? CacheCapGB { get; set; }                    // what a compile's plan may add to the game's shader cache (ScsKiller.SetGameCacheCap); null = no cap of the user's
    public bool Hidden { get; set; }                        // removed from the library by the user: never listed again until shown (ScsKiller.HideGame)
    public bool PlanRayTracing { get; set; } = true;        // the plan was built with them (ScsKiller.RayTracing); true for a record from before the setting
    public bool WarmedRayTracing { get; set; } = true;      // PlanRayTracing of the plan the last complete warm replayed
    public bool WarmedCareful { get; set; }                 // the last complete warm was careful
    public StoppedCreated? StoppedCreated { get; set; }     // what stopped or failed compiles created (ScsKiller.StoppedFile) was for this; null = none on record
    public LaunchCheck? FirstLaunch { get; set; }           // the game's first launch after the last complete warm (AMD, NVIDIA); null = not yet
    public DateTimeOffset? UnreachedWarm { get; set; }      // NVIDIA: the WarmedAt of a warm that launch showed the game's lookups miss (ScsKiller.IsUnreached)
    public DateTimeOffset? UnreachedRecheckedAt { get; set; }   // the start of the last later launch judged against that verdict; null = none since
    public DateTimeOffset? UnreachedLifted { get; set; }    // the UnreachedWarm whose verdict a later launch lifted: it used the compile
    public DateTimeOffset? CacheClearedAt { get; set; }     // the app last deleted the game's driver cache: launches before it left none of their compiles there
    public PlayWindow? LastPlay { get; set; }               // the last run of the game the app watched from start to exit
    public bool RecordedLong { get; set; }                   // a recorded launch of ScsKiller.EnoughRecording or more since the recording was last cleared
    public bool RtUnseen { get; set; }                       // a recorded launch of ScsKiller.EnoughRecording built no ray tracing state object, and none since did
    public DxApi? ApiOverride { get; set; }                  // the user's DirectX (IScsKiller.SetApiOverride); null = Auto
    public string? ChosenExe { get; set; }                   // a store's game: the exe the user picked, relative to its install (ScsKiller.SetGameExe); null = Auto
    public string? PlanApi { get; set; }                     // ScsKiller.ApiKey of the engine the plan was built for; null = a plan from before it was kept
    public Dictionary<string, JsonObject>? ApiCompiles { get; set; }   // the compiles of the game's other graphics APIs, by ScsKiller.ApiKey: ScsKiller.ApiFields (ScsKiller.SwitchApi); null = none
    public string? PlanEngine { get; set; }                  // ScsKiller.EngineKey of the engine the plan was built for; null = a plan from before it was kept
    public int? PlanFiltering { get; set; }                  // the player's texture filtering the plan was built at (ScsKiller.Filtering, Maximum mode aside): another re-plans
    public string? CompileFailedBuild { get; set; }          // Game.Version ("" without one) whose last compile failed or was refused; null = none since the last complete one
    public BeforeAfterRecord? BeforeAfter { get; set; }      // a before and after recording in progress, or the last one done; null = none
}

/// <summary>A game's before and after recording (ScsKiller.BeforeAfter). A run is the first launch whose #session stamp is
/// after <see cref="ArmedAt"/> (while Compiling, when the compile was asked for); <see cref="Before"/> and <see cref="After"/> are the launch stamps of the pinned snapshots
/// of the runs that counted. <see cref="DriverId"/> and <see cref="Build"/> at the before run: either changing voids the pair.
/// <see cref="Dirty"/>: the game ran outside the flow after the compile started. <see cref="ClearPending"/>: BeforeArmed
/// with the cache not cleared, so no launch counts until a clear succeeds. <see cref="Survivors"/>: the driver-cache keys
/// that had files right after the before run's clear; a key the game then holds open among them means it started with
/// old files, and the before run is played again. <see cref="ClearGap"/>: that clear didn't know every key of the game
/// (DriverKeys' gap, or on AMD a DirectX 11 game's DxCache key).</summary>
public sealed class BeforeAfterRecord
{
    public BeforeAfterStage Stage { get; set; }
    public int Minutes { get; set; } = 5;
    public DateTimeOffset ArmedAt { get; set; }
    public string? DriverId { get; set; }
    public string? Build { get; set; }
    public bool Dirty { get; set; }
    public long? Before { get; set; }
    public long? After { get; set; }
    public string? Problem { get; set; }
    public bool ClearPending { get; set; }
    public bool Max { get; set; }   // the after run's compile is Max compile's (ScsKiller.CompileMax)
    public bool Overlay { get; set; } = true;   // the in-game countdown, for both runs: a pair never mixes its cost in one run only
    public HashSet<string>? Survivors { get; set; }
    public bool? ClearGap { get; set; }
    public bool DxShared { get; set; }   // AMD, DirectX 11: a DxCache key made during the before run couldn't be tied to the game, so the game's key stayed unknown
}

/// <summary>The exe discovery took for a game whose source named <paramref name="Named"/>, its Unreal Shipping exe or the named
/// one (<see cref="Games.GameFiles.GameExe"/>), while the source names the same exe and <paramref name="Stamp"/> (the store's
/// build and the install root's entries) is unchanged.</summary>
public sealed record ShippingPick(string Named, string? Stamp, string Exe);

/// <summary>A run of the game as the app's watcher saw it: not running at <paramref name="From"/>, last seen running at
/// <paramref name="To"/> (both within a poll of the real start and exit).</summary>
public sealed record PlayWindow(DateTimeOffset From, DateTimeOffset To);

/// <summary>A launch's driver creates from the recorder's csv (<see cref="SessionLog"/>): cache hits and compiles, started at
/// <paramref name="At"/>.</summary>
/// <summary>What stopped or failed compiles' created PSOs (<see cref="ScsKiller.StoppedFile"/>) went into: the driver (ScsKiller.DriverId),
/// the game's shaders (ShaderIndex.ContentHash), and the staged exe name, layer (ReShadeInstall.Fingerprint) and AGS app ("" = none),
/// each in the cache's key. Only compiles without NVIDIA Aftermath or Advanced Shader Delivery keep one. <paramref name="Bytes"/>:
/// the game's D3DSCache dbs' lengths at <paramref name="At"/>, when a fold put every staged db in (<paramref name="Folded"/>);
/// before the last stop's fold, the earlier fold's, which that fold checks before it takes the new ones.</summary>
public sealed record StoppedCreated(string Driver, string Index, string Exe, string? Layer, string Ags,
    IReadOnlyDictionary<string, long>? Bytes = null, DateTimeOffset? At = null, bool Folded = false);

/// <param name="Warmed">NVIDIA: the warm's own pipelines the launch created (ScsKiller.WarmedCreates' Seen); null = not judged</param>
/// <param name="WarmedCold">...and of them, the ones created cold (at least ScsKiller.UnreachedMedianMs)</param>
public sealed record LaunchCheck(DateTimeOffset At, long Hits, long Compiles, long? Warmed = null, long? WarmedCold = null)
{
    public double Compiled => Hits + Compiles > 0 ? (double)Compiles / (Hits + Compiles) : 0;
}

/// <summary>A mod's d3d12.dll renamed to <paramref name="Name"/> in the exe folder, with the SHA-256 of its bytes.</summary>
public sealed record ChainedDll(string Name, string Sha256);

/// <summary>Whose a folder's scskiller.ini is (recorder-inis.json, by the folder's full path). <paramref name="Tracked"/>:
/// the SHA-256 of SCSKiller's own ("a|b": either; "": not judged yet, it couldn't be read); <paramref name="Users"/>: the one
/// there is the user's, until SCSKiller writes its own there; <paramref name="Remove"/>: SCSKiller's is to be taken out; <paramref name="Chain"/> and
/// <paramref name="Install"/>: the next= and the install root= its writer gave it.</summary>
public sealed record IniOwner(string Dir, string? Tracked = null, bool Users = false, bool Remove = false, string? Chain = null, string? Install = null);

/// <summary>An offline session SCSKiller started without EasyAntiCheat: the game, its exe and install, the process it started
/// (its pid and creation FILETIME; 0 = none yet), and whether it was resumed (else it is still suspended, or was). The files
/// it placed are <see cref="GameRecord.RecorderFiles"/>, by hash; <paramref name="Created"/> names the files in the exe's
/// folder it creates itself, each recorded before it exists (none existed before the session; temp names, a
/// steam_appid.txt it added), which go by name whatever they hold, each leaving the list once gone. A 1.2 session's
/// journal is read the same way (its Original is ignored).</summary>
public sealed record OfflineSession(string GameId, string Exe, string InstallDir, int Pid = 0, long Started = 0, bool Resumed = false,
    string[]? Created = null,
    bool Risk = false);   // a session in a game with anti-cheat (IScsKiller.StartRiskSession): its signed ini carries risk=1

public sealed record KeptList(string Build, string? Driver, List<GameState> Games);

/// <summary>window.json: the main window's restored bounds in workspace pixels (GetWindowPlacement's), and whether it was
/// maximized.</summary>
public sealed record WindowBounds(int Left, int Top, int Width, int Height, bool Maximized)
{
    /// <summary>From GetWindowPlacement's showCmd and flags: minimized counts as maximized when it restores to maximized.</summary>
    public static bool WasMaximized(int showCmd, int flags) => showCmd == 3 /* SW_SHOWMAXIMIZED */ || showCmd == 2 /* SW_SHOWMINIMIZED */ && (flags & 2 /* WPF_RESTORETOMAXIMIZED */) != 0;
}

/// <summary>Pipelines new since the last complete warm, as counted from the files and keys <paramref name="Key"/> stamps.</summary>
public sealed record PendingCount(string Key, long Recorded, long? Planned, bool Unknown, long Upscaler = 0);

/// <summary>The expensive part of a scan (engine detection, planner check, anti-cheat), reused while <see cref="Key"/>
/// (exe stamp, store version, vendor profile, recording, SCSKiller build) is unchanged. <paramref name="Clean"/>: the
/// <see cref="ScsKiller.FolderStamp"/> at the last full anti-cheat check that found none.</summary>
public sealed record Evaluation(string Key, EngineInfo? Engine, AntiCheat AntiCheat, PlanCheck Check, string? Clean = null,
    bool StreamlineFirst = false, IReadOnlyList<string>? StreamlineFiles = null, string? StreamlineStamp = null,   // GameFiles.StreamlineFirst
    string? ApiDetected = null,    // GameState.ApiDetected; Engine has the API the choice or the recording gave
    string? Found = null, string? FoundAt = null)   // an anti-cheat verdict's ScsKiller.FolderStamp and the marker it found; null: not known
{
    public bool Vac { get; init; }   // made while Steam's data named it VAC (Games.Vac): evaluated again once it no longer does
}

/// <summary>%LOCALAPPDATA%\SCSKiller, or the portable build's data\: settings.json, scan.json, dismissed.json + games\&lt;id&gt;\state.json.</summary>
public sealed class AppStore(string dataDir)
{
    /// <summary>%LOCALAPPDATA%\SCSKiller, or data\ beside the portable build's SCSKiller.exe; SCSKILLER_DATA_DIR (tests on
    /// a PC whose own data must stay untouched) moves either. SCSKiller's uninstall knows only the default folder, so with it
    /// moved a recorder goes into a game only with SCSKILLER_TEST_RECORDER=1 too (<see cref="ScsKiller"/>'s install), and the
    /// test takes it out itself. The recorder's key, revocation list and refusals are each copy's own, in its data folder
    /// (<see cref="ScsKiller.RecorderDir"/>).</summary>
    public static string DefaultDir => Moved ?? _defaultDir.Value;

    /// <summary>SCSKILLER_DATA_DIR's folder; null = the default.</summary>
    public static string? Moved => Environment.GetEnvironmentVariable("SCSKILLER_DATA_DIR") is { Length: > 0 } d ? Path.GetFullPath(d) : null;

    static readonly Lazy<string> _defaultDir = new(() =>
        Resolve(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCSKiller"), PortableRoot(AppContext.BaseDirectory), out _onOldFolder));

    static IDisposable? _onOldFolder;   // held for the process's life (Resolve)

    /// <summary>The folder Velopack's portable zip was unpacked to: its .portable marker sits beside Update.exe, with the app
    /// in current\ and the CLI in current\cli\. Null for an installed or a dev build.</summary>
    public static bool IsPortable => PortableRoot(AppContext.BaseDirectory) != null;

    static IEnumerable<string> InstallRoots(string baseDir) =>
        new[] { "..", Path.Combine("..", "..") }.Select(up => Path.GetFullPath(Path.Combine(baseDir, up)));

    internal static string? PortableRoot(string baseDir) => InstallRoots(baseDir).FirstOrDefault(d => File.Exists(Path.Combine(d, ".portable")));

    public const string DevBuildRefusal = "This is a development build: set SCSKILLER_DATA_DIR to a test folder, or SCSKILLER_ALLOW_LIVE_DATA=1 to use your real data.";

    /// <summary>True for a build that would open the real %LOCALAPPDATA%\SCSKiller without being a Velopack install or portable
    /// package (a repo build, raw publish output). Windows only: the other platforms package without Update.exe.</summary>
    public static bool RefusesLiveData() => OperatingSystem.IsWindows() && Moved == null
        && Environment.GetEnvironmentVariable("SCSKILLER_ALLOW_LIVE_DATA") != "1"
        && !InstallRoots(AppContext.BaseDirectory).Any(d => File.Exists(Path.Combine(d, "Update.exe")) || File.Exists(Path.Combine(d, ".portable")));

    /// <summary>The portable build's data\. Its first start copies what an earlier portable build kept in <paramref name="local"/>
    /// and leaves that folder as it was, since an installed SCSKiller may share it. data\ counts once it holds
    /// <see cref="MigratedFile"/>, written last into a fresh data.partial that is then renamed. One process copies at a time
    /// and the others wait for it (the app and the CLI started together, also in other Windows sessions). A copy that fails
    /// (a folder that can't be written, a full disk) keeps <paramref name="local"/> for this run, and the next start copies it
    /// whole again, with what that run saved there. A data\ without the marker is moved aside to data.stale-&lt;n&gt;, never
    /// used. A process that stays on <paramref name="local"/> says so through <paramref name="onOldFolder"/>, a handle to keep
    /// open while it runs: no copy is made meanwhile, since its later saves would stay behind.</summary>
    internal static TimeSpan CopyWait = TimeSpan.FromSeconds(30);   // replaceable for tests

    internal static string CopyMutex(string portable) => $@"Global\SCSKiller-portable-copy-{PortableId(portable)}";

    internal static string OldFolderMutex(string portable) => $@"Global\SCSKiller-portable-old-folder-{PortableId(portable)}";

    static string PortableId(string portable) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(portable)).ToUpperInvariant())))[..16];

    internal static string Resolve(string local, string? portable, out IDisposable? onOldFolder)
    {
        onOldFolder = null;
        if (portable == null) return local;
        var data = Path.Combine(portable, "data");
        if (File.Exists(Path.Combine(data, MigratedFile))) return data;
        var oldFolder = OldFolderMutex(portable);
        Mutex gate;
        try { gate = NamedMutex.Open(CopyMutex(portable)); }
        catch (Exception)   // another user's, which this one can't open: no copy
        {
            onOldFolder = Hold(oldFolder);
            return local;
        }
        using (gate)
        {
            bool owned;
            try { owned = gate.WaitOne(CopyWait); }
            catch (AbandonedMutexException) { owned = true; }   // its holder died mid-copy, and this one copies again
            if (!owned)   // another process copies for longer than this one waits (Velopack's hooks get 60 s): the old folder, this run
            {
                onOldFolder = Hold(oldFolder);
                return local;
            }
            var partial = data + ".partial";
            try
            {
                if (File.Exists(Path.Combine(data, MigratedFile))) return data;
                if (Held(oldFolder))
                {
                    onOldFolder = Hold(oldFolder);
                    return local;
                }
                if (Directory.Exists(partial)) Directory.Delete(partial, true);
                Directory.CreateDirectory(partial);
                if (Directory.Exists(local))
                    foreach (var f in Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(local, f);
                        // the recorder's files aren't read from here, and a staged update, an apply marker and the locks belong to that folder's own app
                        if (rel.StartsWith("armed" + Path.DirectorySeparatorChar) || rel.StartsWith("refused" + Path.DirectorySeparatorChar)
                            || rel is "recorder.key" or "recorder.revoked" or "update-staged.json" or "applying" || rel.EndsWith(".lock")) continue;
                        var to = Path.Combine(partial, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        File.Copy(f, to);
                    }
                // a copy starts with these off: they act beyond its folder (the sign-in entry, every game's folder, its own updates)
                if (Load<JsonObject>(Path.Combine(partial, "settings.json")) is { } copied)
                {
                    foreach (var key in new[] { nameof(Settings.StartWithWindows), nameof(Settings.RecordAllGames), nameof(Settings.InstallUpdatesAutomatically) })
                        copied[key] = false;
                    Save(Path.Combine(partial, "settings.json"), copied);
                }
                File.WriteAllText(Path.Combine(partial, MigratedFile), "");
                if (Directory.Exists(data))
                {
                    var n = 1;
                    while (Directory.Exists($"{data}.stale-{n}") || File.Exists($"{data}.stale-{n}")) n++;
                    Directory.Move(data, $"{data}.stale-{n}");
                }
                Directory.Move(partial, data);
                return data;
            }
            catch (Exception)
            {
                try { Directory.Delete(partial, true); } catch (Exception) { }
                onOldFolder = Hold(oldFolder);
                return local;
            }
            finally { gate.ReleaseMutex(); }
        }

        // the open handle is the signal, not ownership: it lasts as long as any process keeps one, whichever thread made it
        static IDisposable? Hold(string name)
        {
            try { return NamedMutex.Open(name); }
            catch (Exception) { return null; }
        }

        static bool Held(string name)
        {
            try { return NamedMutex.Held(name); }
            catch (UnauthorizedAccessException) { return true; }   // another user's process holds it
        }
    }

    /// <summary>In the portable build's data\ once <see cref="Resolve"/> has copied the old folder into it.</summary>
    internal const string MigratedFile = "migrated";

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string DataDir { get; } = dataDir;

    public static Settings DefaultSettings => new(Environment.ProcessorCount, WarmPriority.BelowNormal,   // below-normal priority keeps the PC responsive on every thread
       
        DriverUpdateMode.Ask, BackgroundThreads: 8, PauseWhileGaming: true);

    public Settings LoadSettings() => Load<Settings>(Path.Combine(DataDir, "settings.json")) ?? DefaultSettings;
    public void SaveSettings(Settings s) => Save(Path.Combine(DataDir, "settings.json"), s);

    /// <summary>Null until a window was saved, or when the file can't be read or has no size: the default size then.</summary>
    public WindowBounds? LoadWindow()
    {
        try { return Load<WindowBounds>(Path.Combine(DataDir, "window.json")) is { Width: > 0, Height: > 0 } w ? w : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public void SaveWindow(WindowBounds w) => Save(Path.Combine(DataDir, "window.json"), w);

    public List<SentProblem> LoadProblems() => Load<List<SentProblem>>(Path.Combine(DataDir, "reports.json")) ?? [];
    public void SaveProblems(List<SentProblem> sent) => Save(Path.Combine(DataDir, "reports.json"), sent);

    string InisFile => Path.Combine(DataDir, "recorder-inis.json");

    /// <summary>Every folder's <see cref="IniOwner"/>, by its full path (any case).</summary>
    public Dictionary<string, IniOwner> LoadInis() => new(Load<Dictionary<string, IniOwner>>(InisFile) ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>Changes the folders' <see cref="IniOwner"/>s under a lock every process of this user shares, read again under it.</summary>
    public void UpdateInis(Action<Dictionary<string, IniOwner>> change, TimeSpan? wait = null) =>
        Locked(InisFile, () =>
        {
            var all = LoadInis();
            change(all);
            Save(InisFile, all);
        }, wait);

    public Dictionary<string, Evaluation> LoadScan() => Load<Dictionary<string, Evaluation>>(Path.Combine(DataDir, "scan.json")) ?? [];
    public void SaveScan(Dictionary<string, Evaluation> scan) => Save(Path.Combine(DataDir, "scan.json"), scan);

    /// <summary>The games the last scan's sources listed, by the SCSKiller <paramref name="build"/> that listed them: their exes
    /// are reused while a game's build is the same; another SCSKiller build's (its exe heuristics may differ) none.</summary>
    public List<Game> LoadDiscovered(string build) =>
        Load<Discovered>(Path.Combine(DataDir, "discovered.json")) is { } d && d.Build == build ? d.Games : [];
    public void SaveDiscovered(string build, List<Game> games, Dictionary<string, ShippingPick>? shipping = null) =>
        Save(Path.Combine(DataDir, "discovered.json"), new Discovered(build, games, shipping));

    /// <summary>The Shipping exes the last discovery found, by game id.</summary>
    public Dictionary<string, ShippingPick> LoadShippingPicks(string build) =>
        Load<Discovered>(Path.Combine(DataDir, "discovered.json")) is { Shipping: { } p } d && d.Build == build ? p : [];

    sealed record Discovered(string Build, List<Game> Games, Dictionary<string, ShippingPick>? Shipping = null);

    /// <summary>The games as the last scan or refresh listed them, by the SCSKiller build and GPU driver that did: what a
    /// start with Settings.ScanAtStart off shows.</summary>
    public KeptList? LoadList() => Load<KeptList>(Path.Combine(DataDir, "games.json"));
    public void SaveList(KeptList list) => Save(Path.Combine(DataDir, "games.json"), list);

    /// <summary>Entry by entry: one that doesn't parse (null, another shape) is left out, the others kept.</summary>
    public List<Games.ManualEntry> LoadManualGames() => (Load<List<JsonElement>>(Path.Combine(DataDir, "manual-games.json")) ?? [])
        .Select(e => { try { return e.Deserialize<Games.ManualEntry>(Json); } catch (JsonException) { return null; } }).OfType<Games.ManualEntry>().ToList();
    public void SaveManualGames(List<Games.ManualEntry> games) => Save(Path.Combine(DataDir, "manual-games.json"), games);

    public Games.AddedLibrary LoadAddedLibrary() => Load<Games.AddedLibrary>(Path.Combine(DataDir, "added-library.json")) ?? new();
    public void SaveAddedLibrary(Games.AddedLibrary library) => Save(Path.Combine(DataDir, "added-library.json"), library);

    public LibrarySort LoadLibrarySort()
    {
        try { return Load<LibraryView>(Path.Combine(DataDir, "library-view.json"))?.Sort ?? LibrarySort.Status; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return LibrarySort.Status; }
    }

    public void SaveLibrarySort(LibrarySort sort) => Save(Path.Combine(DataDir, "library-view.json"), new LibraryView(sort));
    sealed record LibraryView(LibrarySort Sort);

    /// <summary>Game id -> the stale key the user skipped (driver + game version).</summary>
    public Dictionary<string, string> LoadDismissed() => Load<Dictionary<string, string>>(Path.Combine(DataDir, "dismissed.json")) ?? [];
    public void SaveDismissed(Dictionary<string, string> dismissed) => Save(Path.Combine(DataDir, "dismissed.json"), dismissed);

    /// <summary>Game id -> what the new-shaders notification last told about it, and when (<see cref="NewShaders.Due"/>).</summary>
    public Dictionary<string, string> LoadNotified() => Load<Dictionary<string, string>>(Path.Combine(DataDir, "notified.json")) ?? [];
    public void SaveNotified(Dictionary<string, string> notified) => Save(Path.Combine(DataDir, "notified.json"), notified);

    /// <summary>Vendor -> warm rate (PSO/s) of the first complete warm measured on this PC, for games not warmed yet.</summary>
    // keyed "<vendor>|<driver id>"; an earlier build's vendor-only keys are never read
    public Dictionary<string, double> LoadWarmRates() => Load<Dictionary<string, double>>(Path.Combine(DataDir, "warmrates.json")) ?? [];
    public void SaveWarmRates(Dictionary<string, double> rates) => Save(Path.Combine(DataDir, "warmrates.json"), rates);

    /// <summary>Update channel -> signed_at of the newest signed feed accepted (FeedTrust: no replay of an older feed).</summary>
    public Dictionary<string, DateTimeOffset> LoadFeedTimes() => Load<Dictionary<string, DateTimeOffset>>(Path.Combine(DataDir, "feeds.json")) ?? [];
    public void SaveFeedTimes(Dictionary<string, DateTimeOffset> times) => Save(Path.Combine(DataDir, "feeds.json"), times);
    public PackageTries LoadPackageTries() => Load<PackageTries>(Path.Combine(DataDir, "package-tries.json")) is { At: not null } t ? t : new();
    public void SavePackageTries(PackageTries tries) => Save(Path.Combine(DataDir, "package-tries.json"), tries);
    public Dictionary<string, DateTimeOffset> LoadContentTimes() => Load<Dictionary<string, DateTimeOffset>>(Path.Combine(DataDir, "content-signed.json")) ?? [];
    public void SaveContentTimes(Dictionary<string, DateTimeOffset> times) => Save(Path.Combine(DataDir, "content-signed.json"), times);
    public NewsSeen LoadNewsSeen() => Load<NewsSeen>(Path.Combine(DataDir, "news-seen.json")) ?? new();
    public void SaveNewsSeen(NewsSeen seen) => Save(Path.Combine(DataDir, "news-seen.json"), seen);

    /// <summary>The game's folder under games\: the id with ':' replaced by '_', as it always was, when that is a plain
    /// folder name (<see cref="IsPlainName"/>). Launcher metadata (an Xbox Identity.Name, an EA content id, an Epic app
    /// name) may hold separators or "..": such an id gets '%' and its <see cref="Segment"/> instead, which a plain name
    /// never starts with, so the two kinds never meet and existing folders keep their names. <see cref="GameId"/> reverses it.</summary>
    public string GameDir(string gameId)
    {
        var games = Path.Combine(DataDir, "games");
        var plain = gameId.Replace(':', '_');
        var dir = Path.Combine(games, IsPlainName(plain) ? plain : EncodedPrefix + Segment(gameId));
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(dir)), Path.GetFullPath(games), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"game id {gameId} isn't a folder name");
        return dir;
    }

    // the escape character of Segment: an id from a store ("steam:", "epic:", ...) never starts with it
    const char EncodedPrefix = '%';

    /// <summary>A folder name that stays itself: not empty, no character a Windows file name can't hold, no trailing dot or space
    /// (Windows trims them, so "." and ".." too), and not starting with <see cref="EncodedPrefix"/>.</summary>
    static bool IsPlainName(string name) =>
        name.Length > 0 && name[0] != EncodedPrefix && name.IndexOfAny(BadNameChars) < 0 && name[^1] is not ('.' or ' ');

    /// <summary>The id whose <see cref="GameDir"/> is the folder <paramref name="folderName"/>: a plain folder name is its own
    /// (<see cref="GameDir"/> maps it to itself).</summary>
    public static string GameId(string folderName) =>
        folderName.StartsWith(EncodedPrefix) ? Uri.UnescapeDataString(folderName[1..]) : folderName;

    /// <summary><paramref name="name"/> as one file name, reversibly: '%', what a Windows file name can't hold and the dots and spaces
    /// Windows trims off the end (all of "." and "..") become %XX. A valid name without them is left as it is.</summary>
    public static string Segment(string name)
    {
        var s = new StringBuilder(name.Length);
        var tail = name.Length - name.AsSpan().TrimEnd(". ").Length;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '%' || i >= name.Length - tail || Array.IndexOf(BadNameChars, c) >= 0) s.Append($"%{(int)c:X2}");
            else s.Append(c);
        }
        return s.ToString();
    }

    // Windows' set on Linux too: Wine reaches a game's folder by its Windows path, where '\' separates
    static readonly char[] BadNameChars = [.. Enumerable.Range(0, 32).Select(c => (char)c), '"', '<', '>', '|', ':', '*', '?', '\\', '/'];

    // What each record handed out held when it was loaded or last saved: a save writes only what its holder changed since.
    readonly ConditionalWeakTable<GameRecord, JsonObject> _held = new();

    /// <summary>Run with the game's id before its record is read. Replaceable for tests: a record held for as long as a test needs.</summary>
    internal Action<string>? LoadingGame { get; set; }

    public GameRecord LoadGame(string gameId)
    {
        LoadingGame?.Invoke(gameId);
        var r = Load<GameRecord>(Path.Combine(GameDir(gameId), "state.json")) ?? new GameRecord();
        // a plan saved under another data folder (the portable build's copy, a moved portable folder): the one in this game's folder
        if (r.Plan is { } p && Path.GetDirectoryName(p.FilePath) != GameDir(gameId) && Path.Combine(GameDir(gameId), Path.GetFileName(p.FilePath)) is var here && File.Exists(here))
            r.Plan = p with { FilePath = here };
        _held.AddOrUpdate(r, JsonSerializer.SerializeToNode(r, Json)!.AsObject());
        return r;
    }

    /// <summary>Writes the fields <paramref name="r"/> changed since <see cref="LoadGame"/> (or its last save) onto the
    /// stored record, re-read under a lock shared by every process of this user: a path that holds a record for long (a
    /// compile) never puts back what another path saved meanwhile (a watched exit, a learned key). The record's sets
    /// merge by what was added and removed. A record that wasn't loaded here is written whole.</summary>
    public void SaveGame(string gameId, GameRecord r, TimeSpan? wait = null)
    {
        var path = Path.Combine(GameDir(gameId), "state.json");
        var now = JsonSerializer.SerializeToNode(r, Json)!.AsObject();
        Locked(path, () =>
        {
            var stored = _held.TryGetValue(r, out var was) && Load<JsonObject>(path) is { } latest ? Merge(latest, was, now) : now;
            // a key one writer pruned while another learned it: what the game or a warm held stays learned
            if (stored[nameof(GameRecord.CacheKeys)] is JsonArray cache)
                foreach (var k in new[] { nameof(GameRecord.GameKeys), nameof(GameRecord.WarmedKeys) }.SelectMany(n => stored[n] as JsonArray ?? [])
                    .Select(k => k!.GetValue<string>()).Except(cache.Select(k => k!.GetValue<string>())).ToList())
                    cache.Add(k);
            WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(stored, Json));
        }, wait);
        _held.AddOrUpdate(r, (JsonObject)now.DeepClone());
    }

    /// <summary>Deletes the game folder's key files its stored record doesn't name, read under the record's lock, and older
    /// than an hour: one another process just wrote is named by its record soon (<see cref="KeyFiles.Prune"/>).</summary>
    public void PruneKeyFiles(string gameId, TimeSpan? age = null)
    {
        var path = Path.Combine(GameDir(gameId), "state.json");
        Locked(path, () =>
        {
            var stored = Load<GameRecord>(path);
            KeyFiles.Prune(GameDir(gameId), age ?? TimeSpan.FromHours(1), [stored?.PlanKeysFile, stored?.WarmKeysFile, .. ScsKiller.ApiKeyFiles(stored)]);
        });
    }

    /// <summary>Runs <paramref name="f"/> holding the record's lock (<see cref="PathGate"/>).</summary>
    internal static void Locked(string path, Action f, TimeSpan? wait = null)
    {
        using (new PathGate(path, wait)) f();
    }

    /// <summary>Owns the lock on <paramref name="path"/> every process of this user takes turns on (the app, the CLI, the
    /// scheduled tasks, in any session): <c>&lt;path&gt;.lock</c> opened exclusively, until disposed on the thread that made
    /// it. Re-entrant per thread, checked before any wait. A holder's handle goes when its process does. Waits up to
    /// <paramref name="wait"/> (10 minutes when not given), then throws the IOException of the last try; or until
    /// <paramref name="ct"/> is cancelled.</summary>
    internal sealed class PathGate : IDisposable
    {
        readonly string _file;

        public PathGate(string path, TimeSpan? wait = null, CancellationToken ct = default)
        {
            _file = Path.GetFullPath(path) + ".lock";
            _held ??= new(StringComparer.FromComparison(Games.GameFiles.Names));   // Linux: a name in another case is another lock file
            if (_held.TryGetValue(_file, out var h))
            {
                h.Count++;
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var limit = wait ?? TimeSpan.FromMinutes(10);
            var waiting = false;
            for (var clock = System.Diagnostics.Stopwatch.StartNew(); ; ct.WaitHandle.WaitOne(20))
                try
                {
                    ct.ThrowIfCancellationRequested();
                    _held[_file] = new(new FileStream(_file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                    return;
                }
                catch (IOException e) when (IoErrors.HeldElsewhere(e) && clock.Elapsed < limit)
                {
                    if (!waiting) Waits?.Invoke(_file);
                    waiting = true;
                }
        }

        /// <summary>Tests: a gate found its lock file held by another holder and waits for it.</summary>
        internal static Action<string>? Waits;

        public void Dispose()
        {
            if (_held!.TryGetValue(_file, out var h) && --h.Count == 0)
            {
                h.Stream.Dispose();
                _held.Remove(_file);
            }
        }

        sealed class Held(FileStream stream) { public readonly FileStream Stream = stream; public int Count = 1; }

        [ThreadStatic] static Dictionary<string, Held>? _held;
    }

    static JsonObject Merge(JsonObject latest, JsonObject was, JsonObject now)
    {
        foreach (var (name, value) in now)
        {
            var before = was[name];
            if (JsonNode.DeepEquals(before, value)) continue;
            if (value is JsonArray set && latest[name] is JsonArray other)   // GameRecord's arrays are all HashSet<string>
            {
                var (had, has) = (Strings(before), Strings(set));
                latest[name] = new JsonArray([.. Strings(other).Except(had.Except(has)).Union(has.Except(had)).Select(s => (JsonNode)JsonValue.Create(s))]);
            }
            else latest[name] = value?.DeepClone();
        }
        return latest;

        static HashSet<string> Strings(JsonNode? a) => a is JsonArray items ? [.. items.Select(i => i!.GetValue<string>())] : [];
    }

    static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            // shared with the app, the CLI and the sign-in task: a reader must not block another process's replace
            using var f = Retry(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return JsonSerializer.Deserialize<T>(f, Json);
        }
        catch (JsonException) { return null; }   // corrupt file: start over rather than refuse to run
    }

    static void Save<T>(string path, T value) => WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));

    /// <summary>Replaces the file through a temp file next to it: a reader in another process (the app, the CLI, the
    /// sign-in task) sees the old file or the new one, never half of one. <paramref name="ownerOnly"/> (off Windows): the
    /// temp file is created with mode 0600 and synced before it replaces the file, so no other user can ever read it.</summary>
    internal static void WriteAtomic(string path, byte[] bytes, bool ownerOnly = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";   // per writer: two processes may save at once
        if (ownerOnly && !OperatingSystem.IsWindows())
        {
            File.Delete(tmp);   // a crashed writer's: O_EXCL below never writes into a file someone else made
            using var f = new FileStream(tmp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            f.Write(bytes);
            f.Flush(flushToDisk: true);
        }
        else File.WriteAllBytes(tmp, bytes);
        try { Retry(() => { File.Move(tmp, path, overwrite: true); return 0; }); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        KeyFiles.Forget(path);
    }

    /// <summary>Another process may be replacing the file right now: short retries for 2.5 s, then the error.</summary>
    static TR Retry<TR>(Func<TR> f)
    {
        // measured: 400 parallel saves and loads of one file wait up to 330 ms
        for (var i = 0; ; i++)
            try { return f(); }
            catch (Exception e) when (i < 100 && e is IOException or UnauthorizedAccessException) { Thread.Sleep(25); }
    }
}
