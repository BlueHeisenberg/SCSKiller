# SCSKiller architecture

SCSKiller compiles a game's pipelines into the GPU driver's shader cache before the game runs, so the game finds them
cached instead of compiling them mid-play. It reads the game's shaders from its files (or from a recording of a play
session), plans which pipelines to create, and replays them in a separate process that the driver treats as the game.

## Contents

- [How it works](#how-it-works)
- [Source layout](#source-layout)
- [Data locations](#data-locations)
- [Driver caches](#driver-caches)
- [Engine readers](#engine-readers)
- [Planner](#planner)
- [Readiness rules](#readiness-rules)
- [Recorder](#recorder)
- [scskiller_warm.exe protocol](#scskiller_warmexe-protocol)
- [Ray tracing state objects](#ray-tracing-state-objects)
- [scskiller_creates.csv](#scskiller_createscsv)
- [Plan file](#plan-file)
- [Clear cache](#clear-cache)

## How it works

1. **Discover.** An `IGameSource` per store finds installed games and flags anti-cheat. Whatever exe a store or the user
   names, an Unreal game's is its `<Name>-<Platform>-Shipping.exe` in `<Project>\Binaries\<Platform>` (Win64, WinGDK,
   WinGRTS) in an install with an `Engine` folder, never a bootstrap stub or launcher: the one such build there is, else
   the one the named exe's name ties; tools and servers never (`GameFiles.GameExe`, applied to every game discovery lists,
   its pick kept in `discovered.json` while the store's build and the install root's entries are unchanged).
   `ManualSource` lists the games the user added by their exe (`manual-games.json` in the data folder): the pick is resolved like a store's install
   (a launcher stub to its Shipping exe) and a game folder is suggested from its layout (above `Engine\` or `bin\`, else
   the exe's folder; nothing above it is read, since the folders beside it may be other games). The user confirms or
   changes that folder; a drive, a store's or Windows' folder of many games, a folder holding a game SCSKiller lists,
   or one whose subfolders look like several games is refused (`ManualSource.RootProblem`). With its folder confirmed
   the game follows the recorder rules of any game: the anti-cheat check covers that whole folder and the exe's
   folder, plus the names directly in each folder above it up to a drive or a folder of many (a confirmed `win64`
   still sees the `BattlEye` folder beside it), and the recorder is armed only for the folder that was checked. A new or renamed file disarms it
   until the next clean check unless its type is one anti-cheat never ships as (`ScsKiller.DataTypes`: logs, settings
   and presets, images, saves, dumps, shader caches, debug symbols; by name only) or it is in a `sym` folder (the symbol
   store an Unreal crash reporter's debugger fills with copies of system dlls); anything named like an anti-cheat marker, a folder
   moved in with contents and a changed exe disarm it too. A launch the proxy passes through leaves
   its reason beside the exe's ledger entry (`<entry>.refused`), which the game's page shows. An entry from before the
   folder could be confirmed isn't recorded until it is. It yields to a store's
   game whose install holds its exe, has no build id (the exe's size and write time mark a patch), starts its exe
   directly, uploads nothing and fills no middleware pack (the upload key is a public store build alias, which a game
   added on one PC doesn't have). Removing it takes its recorder out and forgets the entry.
   A scan reads again only what changed since an earlier one, at background I/O priority unless the user asked for it:
   a Steam or Xbox game keeps its exe while its build is the same (`discovered.json`); each game keeps its engine and
   anti-cheat verdict while its exe, store build and SCSKiller build are the same (`scan.json`; another SCSKiller
   build's is shown at once and the game detected again in the background); the DLLs beside the exe are known by their
   size, write time, NTFS change time and file id (`middleware.json`, `reshade.json`; a DLL's hash also by its first
   and last 4 KB), which a refresh the user asks for doesn't trust. An install is walked for anti-cheat in full when the
   entries of its root or exe folder changed since its last clean walk (the recorder's own files and data files aside),
   or since its install watcher saw a file created or renamed or the recorder was disarmed (either forgets the clean
   walk; one that ran across such a change isn't kept); otherwise only those entries are checked.
   With `Settings.ScanAtStart` off, a scan the user didn't ask for shows the last list (`games.json`, kept by every scan
   and every game's exit) and lists again only Steam's, the Xbox app's and the user's games, reading one again only
   when it isn't the one listed; another SCSKiller build or GPU driver scans as usual.
2. **Index.** An `IEngineReader` per engine family detects the engine and lists every shader the build ships (stage,
   SHA-1, signatures, root signature if embedded), grouped in shader maps that say which shaders can be drawn together.
3. **Plan.** The planner (`IPlanner`) turns the index, a recording if there is one, and the GPU vendor's `VendorCaps`
   into a hash-only plan: pipeline templates, root signatures and items naming shaders by SHA-1.
4. **Materialize.** The plan's shaders are read from the install into a work folder. Nothing of the game is stored
   beyond that folder, which is deleted after the warm.
5. **Warm.** `scskiller_warm.exe` stages a copy of itself named like the game's exe and creates every item on a D3D12
   (or D3D11) device. The driver keys its cache on the exe name, so the game finds those compiles cached.

Games whose files don't say enough (engines that build root signatures at run time, AMD's state-dependent cache) need a
recording: the optional recorder `d3d12.dll` captures what the game creates while it's played, and the plan replays it.

Everything vendor- or engine-specific sits behind one interface: a new GPU vendor is an `IGpuVendorBackend` and its
`VendorCaps`, a new engine an `IEngineReader`. The planner and the warmer read only the caps.

## Source layout

| Path | What |
|---|---|
| `src/SCSKiller.Core/Contracts.cs` | Shared interfaces and records |
| `src/SCSKiller.Core/Games/` | `IGameSource` for Steam, Epic Games, EA app, GOG, Ubisoft Connect, Xbox (PC), Battle.net, PURPLE, HoYoPlay, Gaijin's launcher and games the user added; anti-cheat detection |
| `src/SCSKiller.Core/Unreal/` | `IEngineReader` for Unreal Engine (through CUE4Parse) |
| `src/SCSKiller.Core/Unity/` | `IEngineReader` for Unity |
| `src/SCSKiller.Core/FromSoft/` | `IEngineReader` for FromSoftware games |
| `src/SCSKiller.Core/ReEngine/` | `IEngineReader` for Capcom's RE Engine |
| `src/SCSKiller.Core/RedEngine/` | `IEngineReader` for REDengine 3 (The Witcher 3, DX12) |
| `src/SCSKiller.Core/Northlight/` | `IEngineReader` for Remedy's Northlight (Control, DX12) |
| `src/SCSKiller.Core/Dagor/` | `IEngineReader` for Gaijin's Dagor Engine (War Thunder) |
| `src/SCSKiller.Core/Carved/` | `IEngineReader` for any game that ships raw DXBC/DXIL containers in its files |
| `src/SCSKiller.Core/Planning/` | The planner, root-signature rules, the plan and recording formats, materialization |
| `src/SCSKiller.Core/Vendors/` | NVIDIA and AMD backends and their per-application cache (`IAppCache`) |
| `src/SCSKiller.Core/Warming/` | `IWarmer`: runs `scskiller_warm.exe` and parses its output |
| `src/SCSKiller.Core/App/` | The `ScsKiller` facade, state store, queue, driver-update check, recorder install, account and community database |
| `src/SCSKiller.Cli/` | The `scskiller` command line |
| `src/SCSKiller.App/` | The WinUI 3 app (unpackaged) |
| `proxy/` | Native code: the recorder `d3d12.dll`, `scskiller_warm.exe`, and `selftest.exe`, which measures driver cache behaviour |
| `tests/SCSKiller.Tests/` | xUnit tests, one folder per area |

Building and running the tests: [CONTRIBUTING.md](CONTRIBUTING.md).

## Data locations

Everything lives under `%LOCALAPPDATA%\SCSKiller\`:

- `settings.json`.
- `games\<game id, ':' replaced by '_'>\` when that is a plain folder name; an id that isn't (separators, `..`,
  trailing dots or spaces, from launcher metadata) gets `%` and the id percent-encoded instead, a name the plain ones
  never take (`AppStore.GameDir`):
  - `state.json`: the game's record (status, learned cache keys, last warm). A save writes only the fields its
    holder changed since loading the record, onto the stored one re-read under a lock across processes (sets merge
    by what was added and removed), so a long compile never puts back what a game's exit saved meanwhile;
  - `*.lock`: the locks the app, the command line and scheduled tasks take turns on, in any session (the file opened
    exclusively), for `state.json` and `recording.db`, and `compile.lock`, held for a whole compile of the game;
  - `plan.bin`: the plan (hash-only); `plan-<hash>.keys`: its planner-made pipelines; `warm-<hash>.keys`: the inputs of
    the last complete warm (sorted 20-byte record keys; named by a hash of their contents and never rewritten, so
    `state.json`, which names the current ones, always names what it was saved with);
  - `index.*`: the cached shader index; `index.shaders`: the build's shader SHA-1s, which uploads are checked against;
  - `recording.db`: the game's recording, the only durable copy of what the recorder captured (see
    [Recorder](#recorder));
  - `community.db`: the community database's hash-only recording for the game's build, merged with `recording.db` in
    `work\` when a compile plans;
  - `work\`: the materialized plan, deleted after a warm; `warm-failed.log`: the last failed warm's log, kept from it;
    `warm-rejects.log`: the logs of the last completed warm the driver rejected PSOs in;
  - keys found for the game, kept locally only: `aes.key` (an Unreal pak key), `archive.keys` (FromSoftware archive
    keys, with the SHA-256 of the exe they came from), `pak.modulus` (RE Engine table key);
  - `inline.idx`: for an Unreal game without shader libraries, where each shader sits in its package.
- `packs\<vendor>\<dll name>-<dll sha1>.pack`: middleware packs (see [Middleware packs](#middleware-packs)).
- `community\`: the community database's manifest and downloaded recordings; `community\packs\<gpu vendor>\<vendor>\`
  the shared middleware packs downloaded for this PC's GPU vendor.
- `recorders.log`: what recorder installs and removals did.

Nothing is written into a game folder except the recorder (see [Recorder](#recorder)).

## Driver caches

What each driver keys its cache on decides everything else. These are measured with `selftest` and on games' own
pipelines, not taken from documentation. `VendorCaps` holds the result per vendor.

### NVIDIA, D3D12

- **Keyed on the exe file name**, not its path or contents, case-insensitively. A staged copy of `scskiller_warm.exe`
  named like the game fills that game's cache (`CacheKeyedByExeName`).
- **A packaged (Xbox) game is keyed on its package identity** instead (`PackageKeyed`). Its warm runs with the game's
  identity: `scskiller_warm --package <app user model id>` starts the staged copy through the desktop app activator.
  That process inherits no handles and has no console, so it finds its parent by process id and writes its output
  through named pipes the parent relays. If activation fails, the warm runs under the exe name alone, and the game shows
  as not reached once its own key is seen.
- **An NVAPI shader-extension slot is part of the key**: a PSO or ray tracing collection compiled with a slot set misses
  when created without it, and the reverse. How the slot was set (device-wide, per thread, or a PSO extension) is not
  part of the key. The recorder captures it and the warm recreates it (see [`'N'` records](#ray-tracing-state-objects)).
  When at least 99% of a recording's raster PSOs share one slot and space, every synthesized PSO of the plan gets it too
  (an `'N'` record each, with the most common creation options; `PlanBuilder.RasterNv`), and a plan input is keyed by that
  record. REDengine 3 without a recording gets slot 12, space 1, as The Witcher 3's recording has it. Never on AMD.
- **Per stage, on shaders + root signature only** (`StateIndependentCache`, `PerStageCache`). Blend, rasterizer,
  depth-stencil, render-target formats, MSAA, topology type, input layout and stream output don't change the compiled
  result; any change to the root signature's bytes recompiles every stage. A VS and a PS compiled with other partners
  link at cache-hit cost. So synthesized pipeline state is fine. One exception: view instancing recompiles, so a game
  drawing with it needs its recorded pipelines.
- Files: `%LOCALAPPDATA%\NVIDIA\DXCache\TTTTa91dKKKKKKKK.nvph`, where `KKKKKKKK` is a 32-bit hash of the exe name
  (observed, not derivable). `fc52` is the shader cache shared with D3D11, `0002` is D3D12, `c54e` holds ray tracing
  state objects. The driver keeps them open while the device lives, which is how `NvidiaAppCache` learns a game's keys.
  Files are pre-sized in powers of two, so on-disk size is an upper bound; no size cap or eviction shows up to 12 GB.
  A warmed game none of whose warm's keys has a file left (a shader cache reset) is Stale (`GameRecord.WarmedKeys`).
- **A second process with the same name running at the same time gets its own files** (key + 1). So a game and its warm
  must never run together: the queue doesn't start a warm while the game runs, and stops a running warm gracefully when
  the game starts, resuming from its `done` afterwards.
- The records store an opaque 128-bit key over the whole root signature plus the compiled code; none of the D3D12
  inputs (root signature bytes, shader containers or their hashes) can be recovered from the cache.
- After a driver update, no D3D12 cache file older than the install remains, which is why SCSKiller recompiles after
  one.
- The D3D12 runtime version is not part of the key: pipelines compiled under the system runtime hit under a game's
  Agility SDK runtime and the reverse. The warm runs on the game's Agility runtime when the game ships one
  (`scskiller_warm --d3d12`: the Agility SDK DLLs of the folder the game exe's `D3D12SDKPath` export names, a relative
  path inside the exe's folder, are staged as `D3D12\` next to the warm's exe, which exports `D3D12SDKVersion` set to
  that `D3D12Core.dll`'s version), else on the system's. A runtime that can't be staged or makes no device leaves the
  system's. An older system runtime (Windows 10's: no SM 6.6, none of the newer pipeline subobjects) rejects pipelines
  a game recorded on its own with E_INVALIDARG. A game's Agility runtime older than the system's isn't loaded at all:
  the loader takes the newer one. The warm's log names the `D3D12Core.dll` it runs on.
- Cached creates take about 0.2-1 ms, cold ones 6-70 ms. A warm replays roughly 450-1350 PSOs a second on 30 threads.
- **A compute PSO using inline ray tracing (RayQuery) is never a full hit**: once cached (by a warm, the game or an
  earlier create in the same process) it still costs about 7-15% of its cold create, 9-35 ms for an Unreal 5.6
  game's, and nothing new is written. Heap-indexed root signatures (`ResourceDescriptorHeap[]`) hit normally
  (`selftest bindless`). A compile writes the keys of the RayQuery PSOs it replays, the recording's and the plan's
  (`rayquery.keys`; a synthesized compute stream has the record key of the game's create); the session
  log counts their creates up to `SessionLog.RayQueryFloorMs` (60 ms: in play the floor stretches, SILENT HILL:
  Townfall's to 12-88 ms, its cold creates to 75 ms and more) apart, not as compiles.

### AMD, D3D12

- **Keyed on the exe file name**, in `%LOCALAPPDATA%\AMD\DxcCache\<app>.<f2>.<kind>.<build>.<slot>.parc`. `app` is
  the FNV-1a-32 of the exe name's UTF-16LE bytes, **case-sensitive**, path-independent (`AmdAppCache.DxcAppHash`).
  The name is taken as launched, so the app learns the launched case (`GameRecord.LaunchedExeName`) from the
  recorder's `#session` marker or a running game's main module path, and warms under it.
- **Driver application profiles override the key** for some games: a fixed key for any exe whose name matches, or, for
  some profiles, only when the launch path ends in the game's install layout. The staged warm therefore mirrors the
  install folder's name and the exe's path inside it (`--stage-path`). Since the name hash is only a hint, a game's
  real keys are **learned** from the cache files a process named like it holds open, during its warms and while it's
  played (`GameRecord.CacheKeys`). A game whose learned keys were never warmed is Stale: "the compile didn't reach
  this game's cache: the game uses another driver-cache key".
- **A device created through AGS with an app name is keyed on that name** (`agsDriverExtensionsDX12_CreateDevice`,
  non-empty `pAppName`): FNV-1a-32 of its UTF-16LE bytes, case-sensitive, whatever the exe's name or path. The engine
  name, versions and AGS build don't change it. A profile matched on the app name wins over everything (`Phoenix`:
  `d32786a7`; `OakGame`: `f2f80824`, even as `Wonderlands.exe`, whose own profile is `85c2b2e5`), and an exe-name
  profile wins over an unprofiled app name (`AmdAppCache.AgsKey`). Unreal 4.25-5.6 creates its device this way on AMD
  with the project name as `pAppName`; 4.20-4.24 create a plain device (only `agsInit`), though every version links
  AGS and exports its functions. Tiny Tina's Wonderlands (a 4.20 fork, project `OakGame`) holds `85c2b2e5`; SILENT
  HILL: Townfall (5.6) holds `dc72f790`, FNV-1a of `Townfall`. A warm registers the game's names (`scskiller_warm
  --ags`, `AmdAgs.Of`: Unreal 4.25 or later, AGS linked, project name known) only where that is proven
  (`ScsKiller.AgsFor`): the game's own process was seen holding that key and not its plain key, or, before the game
  is seen, the app name is a measured one (`AmdAppCache.ProvenAgsApp`). Otherwise it warms a plain device. If the
  game's keys aren't known and its first launch after an AGS warm still compiled more than `PartlyWarmedShare` of its
  pipelines, the warm counts as a miss (`GameRecord.AgsMissed`): the game is Stale ("the compile didn't reach this
  game's cache") and the next warm is plain. After an AGS warm the exe name's case doesn't matter.
- The driver writes `.parc` files **through a memory map, so their modification time doesn't change** when entries are
  added. Sizes (powers of two, doubling as they fill) do. Never use mtimes for attribution or growth.
- **The driver caps the whole DxcCache folder at 16 GiB** (`AmdAppCache.DxcCacheCap`): every `.parc` file counts, of
  every app and driver build. The cap is fixed: Adrenalin, the registry and ADLX have no size setting (only the Shader
  Cache mode, `UMD\ShaderCache`, and Reset Shader Cache). It is checked only when a D3D12 device is created; a running
  process can take the folder past it. The driver then deletes files, least recently used first by their NTFS
  LastAccessTime, one file at a time (a key can lose one of its files), until the folder is just under 16 GiB. Files a
  live device holds open are neither counted nor deleted. A device created or ending under a name refreshes that key's
  files; anything that reads a file's contents refreshes it too, so the app only lists names and sizes and queries
  attributes and handles. A warmed game with a warm's file gone is Stale (`GameRecord.WarmedFiles`), and the queue
  warns when its estimated growth (`ScsKiller.CacheGrowth`) is more than the room left (`AmdAppCache.QueueWarning`).
  `selftest dxcfill` grows the cache under a throwaway name to measure this.
- Entries are written during the run, not at exit.
- **The cache is per stage but not state-independent** (`StateIndependentCache = false`, `PerStageCache = true`). With
  the stages cached, a change costs one of three things:

  | Class | Cost | Fields |
  |---|---|---|
  | Recompile a stage | like a cold compile | the VS's declared input elements (format, offset, slot, per-instance step rate, even when unread); topology type; root signature layout (visibility, parameter order, static samplers, added parameters); a PS export format of another shape (1- and 2-channel formats, RGBA16_UNORM); write mask 0; logic op; dual-source blending; a VS-only (depth) pipeline vs VS + PS |
  | Relink | about 0.5 ms, about 3 ms with real game shaders | a VS and a PS never linked together; a render-target format of the same shape (RGBA16F, R10G10B10A2, sRGB, BGRA8, R11G11B10); RT count; MSAA; DSV format; blend enable and factors; write mask RGB |
  | Free | like a re-create | input elements the VS doesn't declare; element order; all rasterizer fields; all depth-stencil fields; blend op; sample mask; strip cut value; root signature deny flags and serialization version |

  One relink field left different costs the whole relink, so the planner copies a recorded blend, depth-stencil and DSV
  together (`ExactLayouts.Link`).
- **A VS is compiled for how its PS consumes it**: whether the PS has a render target, the PS's read masks, and its
  interpolation modes. A VS unit is keyed on those (`UnitPolicy.PartnerReads`) and on the whole root signature.
- Unreal points the UV channels a mesh lacks at its last one, so one VS meets several offsets for the same elements.
  `ExactLayouts.UvClampVariants` adds those layouts for the VS's recorded layouts.
- A cache hit under a profiled name costs up to a few ms, so on AMD "hit" means **under 3 ms**; the recorder and
  `SessionLog` use that threshold.
- Creating a D3D12 device alone opens the name's cache files, so an open file says nothing about whether a warm's
  pipelines compiled: check `failed`.
- **What the driver stores for a PSO depends on the compiling process**, not only on its desc: which stage and pipeline
  entries it keeps, and under which keys, varies with concurrency and with which sibling of a shader set compiles first.
  For some games a warm on many threads leaves most of the game's own creates compiling. So:
  - **Judged by the first launch**: after a complete AMD warm, the first recorder session that ends and holds at least
    `ScsKiller.MinJudgedCreates` creates is kept (`GameRecord.FirstLaunch`). If more than `ScsKiller.PartlyWarmedShare`
    (20%) of them compiled, the game shows "Partly warmed" and offers a careful compile.
  - **Careful compile** (`GameRecord.Careful`, the game's "Careful compile" switch, CLI `compile <game> --careful`):
    the recorded PSOs are split into passes so that no pass holds two with the same shader set (`Warming.WarmPasses`,
    at most `WarmPasses.MaxPasses` passes); each pass is its own `scskiller_warm --pass` process on at most
    `ScsKiller.AmdCarefulThreads` (4) threads, then the plan's other items run in one process at the usual thread count.
    Without a recording it's an ordinary warm. The choice persists, so a driver update's re-warm is careful too.
- Ray tracing and D3D11 are cached per name too. `%LOCALAPPDATA%\D3DSCache` is the Windows runtime's cache, not AMD's.

### D3D11

Keyed on the exe file name on both vendors. Creating a shader is lazy: the driver compiles at the first draw or
dispatch that uses it, and caches per shader, not per pipeline. On NVIDIA blend, render-target format, input layout,
depth, MSAA and SRV formats don't change the result, so SCSKiller warms D3D11 games on NVIDIA only (`Planner.D3D11Cache`).
On AMD the input layout recompiles the VS, so a D3D11 warm there would need the game's real layouts.

### Ray tracing

Both vendors cache ray tracing state objects on disk per exe name, and both hit on an exact repeat of the same object
(and of the same `AddToStateObject` chain). They differ otherwise (`VendorCaps.RtCacheGranularity`):

- **NVIDIA (`Collection`)**: collections are cached individually, and linking cached collections costs under 1 ms even
  for a combination never linked before. The key holds the library bytes, the global and local root signatures, and
  the payload and attribute sizes; not hit group or export names, nor the recursion depth. A cached state object still
  costs about 1.5 ms per shader or collection. Collections and flat pipelines share nothing. So the planner can
  synthesize one collection per DXIL library.
- **AMD (`WholeObject`)**: the key is the whole linked object. Cached parts don't help a different whole, and
  `AddToStateObject` hits only as an exact repeat. Only recorded objects can be warmed.

### Intel

Measured with `tools/intel-arc/measure.ps1` (selftest `fields`, `fields dxil`, `dxr`, `bindless`, probe11) on an Arc B580,
driver 32.0.101.9034, Windows 11. Not yet checked on games, so `IntelBackend` is used only with
`SCSKILLER_EXPERIMENTAL_INTEL=1`; without it Intel stays unsupported.

- **Keyed on the exe file name, path-independent** (probe 6, DXBC and DXIL): the baseline, 4.6 ms cold, is a 0.13 ms hit
  under the same name, a hit from another folder, and cold again under another name (`othrnam` 4.57 ms). So the staged
  warm reaches the game's cache (`CacheKeyedByExeName`). Case sensitivity is unmeasured: warms use the launched case.
- Files: `%USERPROFILE%\AppData\LocalLow\Intel\ShaderCache\<64 hex>`, one per exe name, growing as entries are added;
  the name is not a plain hash of the exe name. Each process also writes a `D3DSCache\<per-path folder>`, but the hit from
  another folder comes from the Intel file (only it grows there).
- **Per stage, not state-independent** (`PerStageCache`, `UnitPolicy.Intel`). A VS and a PS compiled with other partners
  link for free. FREE: every input layout change (formats, offsets, slots, step rates, order, unread elements),
  topology, all rasterizer fields, all depth-stencil fields, DSV format, MSAA count. A PS recompile (~2 ms): any blend
  field (factors, ops, write mask, alpha-to-coverage, independent blend, logic op), any render-target format (even one
  of the same shape), RT count, `MultisampleEnable`, `SampleMask` 0. Both stages: any root signature layout change,
  DENY flags and serialization version included (FreeRsFlags 0); a descriptor-count change in a PIXEL-only table is
  FREE. The VS, as on AMD, is compiled for what follows it (a VS alone, no RT bound, write mask 0) and for how its PS
  reads it (`PartnerReads`). So a PS unit keys on `ExactLayouts.ExactShape`: exact formats plus the blend desc.
- Compute: root signature changes recompile, flags don't. Heap-indexed (SM 6.6) and RayQuery compute PSOs are full hits
  across processes (`selftest bindless`: 470-550 ms cold, 0.23 ms in the next process), unlike NVIDIA's RayQuery floor.
- **Ray tracing (`Collection`)**: an exact repeat hits (0.3 ms), per exe name, path-independent. Five collections compiled
  in one process (1.5 s cold) link in another in 9.5 ms though never linked before. Collections and flat pipelines share
  nothing, any change to a flat pipeline (a hit group subset, names, config) compiles it whole, and `AddToStateObject`
  doesn't use a cached collection (60% of cold). Cold compiles are slow: 0.3 s for a small pipeline, 19 s for 64 hit
  groups.
- **D3D11**: keyed on the exe file name, path-independent; shaders compile at the first draw (~365 ms for probe11's
  heavy PS, 3 ms cached) and state changes cost under 1 ms, so D3D11 games are warmed as on NVIDIA (`Planner.D3D11Cache`).
- `selftest fields dxil` lost its device at view instancing with 4 views on this driver; the rows after it in p1 failed.
  The DXBC run covers them.

### Other vendors and Vulkan

Other vendors are unmeasured, so SCSKiller reports them as unsupported.

Vulkan games aren't supported. NVIDIA's driver keeps Vulkan pipelines keyed on the exe name (`NVIDIA\GLCache`), which
Steam can redirect per game; AMD's Vulkan cache (`AMD\VkCache`) is keyed on the exe's full path, so a staged warm can't
reach it there.

## Engine readers

Each reader detects its engine from the game's files, builds the shader index and reads shader bytes on demand. They
open game files read-only and never launch or attach to the game.

- **Unreal Engine** (`Unreal/`): shader libraries and shader maps through CUE4Parse, including the version-1 archives of
  UE 4.20/4.21 (`UnrealReader.OpenV1`) and games that keep shaders inline in their packages. Encrypted paks need the
  game's AES key (`aes.key`, given by the user). A shipped pipeline cache (`*.stable.upipelinecache`, file versions 17
  (UE 4.25) and 22-28, `StablePipelineCache`) names each PSO's shaders by their library hash; every graphics PSO
  becomes one exact shader map, so the planner pairs those shaders as the game does (global and post-process passes
  that no signature match pairs). They are left out of the index's content hash.
- **Unity** (`Unity/`): Shader objects in serialized files and UnityFS bundles. Their compiled programs are one LZ4 blob
  per platform; the reader finds the blob by its shape and carves it, which avoids depending on each Unity version's
  serialized layout. Windows builds ship DXBC for the `d3d11` platform, which both the D3D11 and D3D12 players
  run. The API comes from `Player.log` of the last run, else the build's API list. Unity builds root signatures at run
  time, so D3D12 Unity games need a recording.
- **FromSoftware** (`FromSoft/`): BHD5/BDT archives, DCX (zlib, Oodle, zstd) and BND4 binders; a BND3 binder is only
  carved for raw containers, so its compressed entries yield no shaders. The archives'
  public RSA keys are read from the game's exe (`SoulsKeys`), else downloaded from a pinned commit of UXM, and kept in
  `archive.keys`. Oodle comes from CUE4Parse's download, never from the game's DLL. Elden Ring creates its root
  signatures from the 1.1 ones its shaders carry (RTS0), serialized again at version 1.0 with each range at an explicit
  offset, so the reader gives each shader that blob (`RootSig.AsVersion10`; its recording: all 9,246 PSOs of shipped
  shaders byte for byte). Nightreign is taken to do the same, unverified.
- **RE Engine** (`ReEngine/`): KPKA packages with encrypted entry tables. The table key needs the game's public RSA
  modulus, which isn't on disk in the clear; it's downloaded from a pinned commit of ree-pak-rs, or given by hand in
  `pak.modulus`. Shaders are in master material files, found by their magic since file names are hashes. RE Engine
  builds root signatures at run time, so D3D12 games need a recording.
- **REDengine 3** (`RedEngine/`): The Witcher 3's DX12 caches in `content\content0`. `shaderdx12_0.cache` holds the
  material shaders (zlib) and the techniques, each naming one pipeline's shaders by key: every distinct technique is an
  exact shader map (438,220 techniques, 60,674 distinct pipelines). `staticshaderDx12_0.cache` holds the engine's own
  shaders, one pool paired by linkage. A technique plans only when each key is a shader of its slot's stage and the set
  is one the root signatures are confirmed for; its compute shader is a pipeline of its own. Counts and lengths are
  bounded by the bytes they need. A cache that doesn't read whole, or whose first techniques name no usable pipeline,
  isn't detected: the carver gets the game under its own engine. `psodx12.cache` names material shaders by an id neither cache holds, so it isn't read.
- **Northlight** (`Northlight/`): Control's effect files in `data\shaders\build\pc_dxil` (`.obj`, magic `RFX `), raw
  and uncompressed: DXBC SM5.1 vertex, pixel and compute shaders and DXIL ray tracing libraries (lib_6_3). The `.rmdp`
  resource packs hold no shaders. Each shader is stored as its size, the container and its entry point; the shaders of one
  pipeline follow each other, and any other bytes between two shaders start a new record. Every distinct record (VS+PS,
  or a lone CS) is an exact shader map, except one whose VS doesn't feed its PS register for register (the runtime
  rejects those); the libraries are one pool. Control: 1,532 shaders, 2,954 distinct pipelines, 41 libraries. The DX11
  set in `pc_dx11` (SM 5.0, the same layout) is indexed apart, one map per file on its own platform: D3D11 items only,
  never a D3D12 pipeline. The game runs on either API and its files don't say which, so it is "D3D11 or D3D12"
  (Control: 1,435 DX11 shaders).
- **Dagor** (`Dagor/`): War Thunder's shader dumps, `compiledShaders\game.ps50.shdump.bin` (DirectX 11) and
  `gameDX12.ps50.shdump.bin` (`game.compatibility*` in the game's compatibility mode), dump version 11.3 only. The body
  is zstd, each entry a zstd frame of the dump's own dictionary; a vertex entry also holds the HS, DS and GS it is drawn
  with. DirectX 11 entries are plain DXBC, one shader map each (`PCD3D_SM5`). A DirectX 12 entry's metadata holds a
  `dxil::ShaderHeader` per stage (kept as `ShaderInfo.EngineHeader`) and where its DXIL is; every vertex/pixel and
  compute pass of the shader classes is one exact pipeline (`PCD3D_SM6`). A pass drawn with the engine's null pixel
  shader is left out (which shader that is isn't in the dump). `config.blk`'s `video/driver` gives the API (`auto`:
  "D3D11 or D3D12"). The dumps compatibility mode selects are the reader's `IndexStamp`: a warm is stale ("game shaders
  changed since the warm") once they change. Gaijin's launcher installs are found from `HKCU\Software\Gaijin\<project>`,
  the exe from `BattlEye\BELauncher.ini` (`GaijinSource`).
- **Carved** (`Carved/`): any other game that ships raw DXBC/DXIL containers. Files are carved, each container
  validated and reflected; a file of pipeline records becomes one shader map per record.

Shaders are hashed the same way everywhere: SHA-1 of the container sliced to its declared size. `ShaderContainer.Parse`
reads each container's signatures, including read masks and interpolation modes, and its embedded root signature.

## Planner

`Planning/Planner` and `PlanBuilder` build the plan; `Planner.Version` is stored with each plan.

### Root signatures

Most engines don't ship root signatures: Unreal builds them from per-stage resource counts. `RootSig` rebuilds them
byte for byte from Epic's rules for UE 4.20 to 5.7, plus engine forks, detected from the shaders, never from the game's
name:

- 10-byte resource counts, whose field order is decided by the shaders' own register use (`ShaderContainer.WideCounts`);
- unbounded bindless SRV ranges in dedicated spaces, which get one table each (`RootSig.BindlessTables`,
  `RootSig.SpaceBindlessTables`);
- a raised MAX_SRVS when a shader binds more SRVs than the stock table (`RootSig.MaxSrvsFor`).

The rule follows the engine version, which a fork named like the game (a CUE4Parse `EGame`) sets to its base. A fork on
an older engine than its containers tell keeps its base (`UnrealReader.OlderBase`): Dead Island 2 is Dambuster's 4.25 in
4.27's IoStore containers, and its root signatures carry 4.25's static samplers (s1000-s1005 in space 0).

REDengine 3 has three root signatures, chosen by the pipeline's stages (`RootSig.Rule.Red3`): compute, VS (+ PS), and with
a GS, HS or DS a wider one; The Witcher 3's recording uses them for all 586 PSOs of its own shaders. Only the stage sets
it confirms get one (`RootSig.Red3Validated`); any other is left out.

Northlight builds its root signatures in code (version 1.0, read from Control's renderer DLL), one for graphics and one
for compute (`RootSig.Rule.Northlight`). Both end in a bounded table of 244,000 SRVs in space 1 that the shaders declare
unbounded; the runtime accepts an unbounded shader range in a bounded one that holds its first register.

Dagor builds each pipeline's root signature from its stages' `dxil::ShaderHeader`s (`RootSig.Rule.Dagor`,
`DagorRootSig`, ported from DagorEngine's `decode_graphics_root_signature` / `decode_compute_root_signature`): version
1.0, no static samplers; the draw-id constant, root constants, constant buffers, sampler tables, one bindless sampler
table, SRV tables, one bindless SRV table, UAV tables, the vendor-extension UAV; stages PS, VS, HS, DS, GS, the
non-pixel ones sharing descriptor offsets. Constant buffers are root CBVs, or with the engine's
`dx12/rootSignaturesUsesCBVDescriptorRanges` one table per stage (`Rule.DagorCbvRanges`, EngineInfo.Fork `cbv-ranges`).
That setting is read from flag 1 of `cache\dx12.cache`'s header, which the game writes after a DirectX 12 session;
without the file it's taken as on, as War Thunder's cache shows. The cache holds no root signature (its PSOs are in the
driver's pipeline library), so the rule isn't confirmed against the game's own. Each header's register masks equal its
DXIL's own bindings for every War Thunder shader.

`RootSig.Verified` holds for the versions and forks a real game has confirmed (`ConfirmedEngines`:
`confirmed-engines.json`, embedded, plus the entries of the copy the server serves as a content file); any other gets
the source-derived rule and the note "not tested on this engine version yet". With a recording, the rule is checked
against it first, and the
planner falls back to a lookup learned from the recording when it doesn't rebuild, keyed on each stage's resource counts: `StageSets` then
pairs only shaders whose counts (for VS → PS, whose two counts together) a recorded PSO has, as no other pair resolves (not with pooled global maps).

### Stage sets and the pre-emit guard

`StageSets` pairs shaders inside one shader map by linkage (`Planner.Links`): VS/MS → PS, VS → GS (→ PS), VS → HS →
DS (→ GS) (→ PS), AS → MS (→ PS). A VS or MS without `SV_Position` is never drawn alone or with a PS
(`Planner.Rasterizable`). Unreal 5's Nanite material pixel shaders, which no VS in their own map feeds, are paired with
the global VSs whose outputs link them. With a per-stage cache, a pixel shader still unpaired that carries its own root
signature gets the first VS of any map whose outputs link it (Elden Ring's gxflvershader pixel shaders, drawn behind
material VSs of its shaderbdle bundles).

A mesh shader's per-primitive outputs sit in its `Outputs` from `ShaderContainer.PrimitiveRow` up, and a PS packs them
after its per-vertex inputs; `Planner.MeshFeeds` matches them by semantic, index, component type and mask. In Unreal 5,
Nanite and material draws also pair shaders from different maps: a default material's MS or PS with another map's
shaders. A shader present in at least 1 of every 50 of the platform's maps is taken as shared, and `SharedAcrossMaps`
pairs every MS with every PS it feeds, and every VS with every shared PS it links to, where either side is shared
(Unreal 5 only).

The **pre-emit guard** (`PlanBuilder.Covers`, `RootSig.Uncovered`) drops a stage set whose root signature doesn't give
a shader every resource it declares; the plan log counts them (`rs_uncovered`). Compute shaders the runtime would
reject on the current vendor (a `[WaveSize]` the GPU doesn't run, AMD AGS extensions elsewhere) are left out
(`vendor_extension`). Stream output and view instancing are never synthesized; recorded pipelines keep them.

### Per-stage cover

Both vendors cache per stage, so a plan needs each stage unit once, not every VS × PS pair. `UnitPolicy` defines a unit
per vendor: NVIDIA, shader + root signature; AMD, the VS also with its declared input elements, topology, and its PS's
render-target binding and consumption. `ExactLayouts` resolves each shader's state from the recording (exact, inferred
from a shader with the same signature, or guessed), and `UnitCover` picks the fewest pipelines that cover every unit,
seeded with the recording's own units.

### Ray tracing collections

Where the vendor caches per collection (NVIDIA), `RtCollections` synthesizes one collection per DXIL library, with the
engine's global and local root signatures rebuilt from the library's resource counts and its RDAT function table.
Rules exist for UE 4.25 (its collections have no state object config), UE 4.26/4.27, UE 5.0-5.4 and the forks the root-signature rules cover; with a recording, its collections'
rule is used if at least 99% of them rebuild. The UE 5 rule is 5.1's (verified on Oblivion Remastered; 5.4 with
MAX_SAMPLERS' 32-sampler table) and applies only when the libraries have 5.1's binding shape
(`RtCollections.Ue5ShapeMismatch`): uniform buffers in space 1, hit-group index and vertex buffers t0/t1 in space 2, no
bindless heap access, no shared uniform buffers in space 4. UE 5.5/5.6's bindless ray tracing has no rule without a recording.
Northlight gets its own global and local root signatures (`RtCollections.NorthlightLocal`),
each library's payload and attributes, and a guessed recursion depth of 1; Control creates whole ray tracing pipelines,
not collections, so whether NVIDIA reuses these collections for them is unverified.

Elden Ring compiles each material's closest hit and any hit pair (one per ray payload, its `_[RT].shaderbdle` bundle)
into a collection, then links its pipelines from the loaded materials' collections plus `gxraytracing`'s libraries.
The plan has one collection per pair (`'H'`, `FromSoft.SoulsRayTracing`) in the game's shape: shader config (12, 8),
depth 1, its two version 1.0 ray tracing root signatures (in no shader; described in code), two hit groups. Its
recording's 30 collections, all payload-4 pairs, rebuild byte for byte with the game's names (the plan names them by
hash: names aren't in NVIDIA's key); the payload-12 pairs are planned the same way, unverified. The pipelines aren't
planned: which collections they link depends on what was loaded, so only an exact repeat would hit.

REDengine 3 adds its materials to a pipeline with `AddToStateObject`, many at once at startup. NVIDIA caches an addition
whole: it hits only as an exact repeat, or for a hit group compiled before as a collection (selftest dxr: `add C` 1.6 ms
against 12.3 cold; `add B`, the hit group inside another pipeline, 12.4). So the plan has one collection per material hit
group the techniques name (`'H'`, `RedEngine.RedRayTracing`): the closest and any hit libraries, the hit group, the local
root signature rebuilt from their bindings (The Witcher 3: 66 of 66 recorded byte for byte), and the global root
signature, shader config and pipeline config of the recording's additions; without a recorded addition, none (the
global root signature is a version 1.0 blob only the recording has). Measured on The Witcher 3's recorded additions under
fake exe names: single-material additions 30.2 -> 6.4 ms at the median, batches of 4 and more barely change.

### Middleware packs

Middleware (FidelityFX, OptiScaler, XeSS, DirectStorage, Streamline plugins) creates pipelines from shaders embedded in
its DLL, which no engine index contains. `Planning/Middleware.cs`:

- **Detection**: known DLL names next to the exe (OptiScaler by its PE exports, whatever its file name); never for
  anti-cheat games. Every embedded container is hashed like a game shader.
- **Promotion**: a recorded pipeline whose stages all come from one detected DLL goes into that DLL version's pack
  (vendor, DLL name, SHA-1 of the DLL), with its root signature if the blob is a serialized root signature only.
- **Seeding**: every pack for a DLL version next to a game's exe seeds its plan (record `'M'`), whichever game's
  recording filled it: this PC's packs, then the shared ones. Shader bytes come from the install's own copy of the DLL at
  materialize time. A PSO with a `[WaveSize]` the GPU doesn't run is left out (`vendor_extension`).
- **Shared packs** (docs/db-contract.md "Middleware packs"): the upscalers' packs (`Middleware.SharedVendors`: FidelityFX
  and XeSS) go through the community database, free, keyed by GPU vendor as well as DLL version: FidelityFX picks
  `[WaveSize(64)]` kernels on AMD (192 of the 472 containers of one `amd_fidelityfx_dx12.dll`) that NVIDIA's runtime
  rejects, and NVIDIA's kernels are ones AMD never uses. Every install, signed in or not, with no setting, matches the
  manifest's pack list against the DLLs next to its games locally, downloads the packs it has a DLL for (`/v1/p/`, no
  token) into `community\packs\`, keeping only the PSOs its copy of the DLL can give, and sends nothing about its games.
  With "Share anonymous shader hashes" on, a local pack of a shared vendor that gains records is uploaded under its
  pack key (`Sharing.SharePacksAsync`). A local pack keeps the GPU vendor that filled it (`PackHeader.Gpu`): a GPU change
  starts it over, a pack from before it kept one is adopted by the next promotion without the PSOs that GPU doesn't
  run, and only packs of this PC's GPU vendor are uploaded. Shared packs are never promoted into nor uploaded again. The game page says
  "Its upscalers, from shared packs" when they brought the plan's middleware pipelines (`PlanStats.MiddlewareSharedItems`).
  OptiScaler's, DirectStorage's and Streamline's packs stay on the PC; DLSS is compiled by the NVIDIA driver itself.

### Hash-only recordings and skipped items

A hash-only recording (the form shared with the community database) is rehydrated from the install before planning,
like the shaders `recording.db` names by hash: from the engine reader, then from middleware DLLs (`Rehydrate.Run`).

`Planner.Materialize` never hands the warm an item naming a shader the install doesn't have. Those are **skipped**, not
failed, and counted in `work\skipped.txt`. **Failed** means the driver rejected it. Skipped items that a community
recording flags as built at run time or by a mod (`'L'`) are counted apart: they need a recording on this PC.

### Planner updates

When a new planner version rebuilds a warmed game's plan, the new plan's records are compared with the plan the last
warm replayed (`GameRecord.PlanItems`, `WarmedPlanItems`). A plan that adds nothing keeps the game Warmed; one that
adds pipelines marks it Stale with the count ("SCSKiller can now compile N more pipelines for this game"). The app
rebuilds such plans while the PC is idle, as plan-only queue items (`QueueItem.PlanCheck`) that queue lists leave out
and count in one line instead (`ScsKiller.PlanCheckLine`: "Checking N games for more to compile (while idle)").

## Readiness rules

The planner's `Check` decides a game's status:

- Engine unsupported, or files it can't read → `Unsupported` with the reason.
- The vendor has `StateIndependentCache` and the engine version has a root-signature rule → `Ready` without a
  recording.
- A recording exists → `Ready`. Without `StateIndependentCache` (AMD) it must contain draws.
- Otherwise → `NeedsRecording`.
- A vendor without `CacheKeyedByExeName` would need an in-game warm, which isn't implemented → `Unsupported`.

After a build:

- **Partial plan**: when the pre-emit guard left out more than 10% of the stage sets, the game stays Ready but its
  reason says a recording compiles the rest (`ScsKiller.IsPartial`).
- **Ray tracing**: when more than 10% of the index's DXIL libraries have no synthesized collection and no recording has
  ray tracing, the game needs a recording for ray-traced effects; the rest still compiles (`ScsKiller.NeedsRtRecording`).
  A recording imported after the plan was built may have the ray tracing (inline RayQuery PSOs, as Unreal 5's hardware
  Lumen traces, or state objects): until the plan is rebuilt nothing is asked for, and the app queues a plan check
  (`ScsKiller.RtPlanCheck`). A recorded launch of 5 minutes or more without a ray tracing state object
  (`GameRecord.RtUnseen`, from the session csv) whose recording the plan still finds no ray tracing in means the
  player's setup doesn't use it: the game is Ready or Warmed with a note, and the page still counts the libraries as
  not compiled. A later launch with a state object clears it.
  Unreal 5 whose index has shaders that trace rays inline (`ShaderInfo.InlineRayTracing`, counted in
  `PlanStats.RtInline`) doesn't need a recording for its DXIL libraries: hardware Lumen traces inline, and the libraries
  serve passes a game may never run, such as path tracing (`ScsKiller.RtInlineCovers`). The libraries stay counted as
  uncovered, the reason says only a recording compiles them, and a recorded state object takes the DXR path. A plan
  from before this was counted that asks for a recording is planned again by the plan check. An Unreal game whose
  Windows device profile sets r.RayTracing.AllowPipeline=0 (`UnrealRhi.RtPipelinesOff`), or whose config turns ray
  tracing off (`UnrealRhi.RayTracingOff`, `EngineInfo.NoRayTracing`: r.RayTracing off as the Engine ini hierarchy and
  the user's Engine.ini resolve it, and nothing that outranks it, such as [SystemSettings], ConsoleVariables.ini, a
  device profile or the launch options, sets it otherwise), never builds a state object (`EngineInfo.NoRtPipelines`):
  its libraries count, none as uncovered, and a plan that asked for a recording is planned again.
  On AMD this always applies, since only recorded objects can be warmed. The plan's uncovered count
  (`PlanStats.RtUncovered`) is the libraries in neither a synthesized collection nor a recorded state object, also when
  the recording has ray tracing; the game page shows it.
- **New pipelines since the warm** (`ScsKiller.PendingOf`), derived whenever the game is evaluated, never counted up.
  After a complete warm, nothing that was an input to it counts; anything new, or newly given a blob it names, counts;
  a pipeline that won't compile here may count once, until the next warm takes it as an input.
  Inputs (`WarmInputs`): every record of the recording, the community recording while one is in use, the plan, and for a
  D3D12 game the entries of its DLLs' packs this GPU runs (`Planner.SeedsPacks`, `MiddlewarePacks.Runs`), each with one
  bit: whether every blob it names directly is in the recordings, the plan, the build's shaders (index.shaders; unknown:
  taken as there), the DLLs or the root signatures their packs seed. On NVIDIA a recorded ray tracing state object is one
  input per identity (`PsoDb.StateObjectIdentity`: its subobjects without an addition's base or a launch's `_LRS_` alias
  ending) and NVAPI state (the last 'N' of the recordings' union, as the warm replays it), keyed by the SHA-1 of the two:
  NVIDIA's key leaves export names out and holds the NVAPI state. The input carries its records' keys: a crash names
  one, and a key file from before identities, which holds them, takes the identity whatever state it was warmed under
  until the next warm. AMD caches a whole state object and hits only an exact repeat of it and its `AddToStateObject`
  chain, so there each record is one, except a record no later launch creates again, which is none: one with a `_LRS_`
  alias (`PsoDb.HasLaunchAlias`), or one whose base or linked collection is such a record. The warm replays those too
  and logs how many. On NVIDIA a recorded pipeline (not a ray tracing one) is one input per stage, keyed by the SHA-1 of
  the stage, its shader, the root signature and the NVAPI state, carrying the keys of the records that have it: NVIDIA
  compiles each stage on its own and leaves fixed-function state and the other stages out of its key
  (`UnitPolicy.Nvidia`), so another cull mode or pairing of stages compiled before is nothing new. One with a stream
  output declaration is one input by its key. The warm's key file holds the same reading of what
  it compiles, taken as it starts and never again: the recordings as it prepares them (imports and community downloads wait meanwhile), and its
  plan. What counts is each input the key file lacks, or holds without a blob that is at hand now (losing one doesn't
  count), less the pipelines that crash this driver, each pipeline once (its first record). Of that, the plan's planner-made records (its key
  file) are, after a newer planner rebuilt the plan, "SCSKiller can now compile N more pipelines"; otherwise they add to
  "N new pipelines; compile again to include them" ("recorded" when all are); their sum is the new-shaders
  notification's count. The result is cached on every input (each by size, write time and a hash of its first and last
  4 KB), in memory and in the game's record for the next start. A key file is read only when its contents hash to its name; one damaged, missing or unreadable, or none (a
  warm from before warms kept one), is an unknown baseline, under which everything counts and the game
  is Stale ("compile again: what the last compile replayed is no longer known"). Key files are written, and the plan and
  warm ones the stored record doesn't name deleted once an hour old with the temp files of their interrupted writes,
  under the record's lock. Cached sets (`KeyFiles`) are dropped on every write SCSKiller makes, at most 2 million keys in
  all; a larger set is computed each time. The app's watcher drops them all once none was asked for in 5 minutes, then
  runs a full compacting collection once the window is hidden or minimized (it pauses the app; a background collection
  keeps the pages committed), so a scan's peak goes back to Windows while the app idles in the notification area.
- **Partly warmed** (AMD): see [AMD, D3D12](#amd-d3d12).

## Recorder

The recorder is `proxy/`'s `d3d12.dll`, placed next to the game's exe with a `scskiller.ini`. It forwards to the system
`d3d12.dll` and records every pipeline, root signature and ray tracing state object the game creates into
`scskiller.db`, with timings in [`scskiller_creates.csv`](#scskiller_createscsv). It reads `scskiller.armed` and
`scskiller.ini`, checks for anti-cheat markers and writes its files in its own folder, as the loader reports it. When a mod
rewrites that path (REFramework names a copy under `_storage_`), it uses the exe's folder instead, but only if the mapped
image (the kernel's name for it) is the same file, by volume and file id, as `<exe folder>\<its name>`.

- **Where it's installed** (`ReconcileRecorders`, app only): in every compatible game when "Record in all compatible
  games" is on, unless the game's own switch says otherwise. Compatible means D3D12, supported, no anti-cheat of any
  kind, no foreign `d3d12.dll` (unless chained, below), none in an Xbox app game's package root (Windows loads that one
  before the exe's folder, so the recorder never would), and a folder writable without elevation. A running game's
  folder is left alone until it exits: every write there (the proxy, the ini, the keys file, the inbox's rotation, a
  removal) checks first that the game isn't running (`GameFolderWrite`, the watcher and the uninstall hook the same
  way): no process named like an exe of its install root or exe folder, so also one a launcher started under another
  name (the uninstall hook takes the install root from `GameRecord.RecorderInstallDir`, and checks again before it
  deletes the recorder's data). No process is ever opened, here or anywhere SCSKiller asks what runs (apart from the one
  SCSKiller starts for an offline session, below, whose exit its cleanup waits on): another program named like the game's exe only delays the write. One named like another exe of those folders counts only if its image is in them (also as they resolve through junctions and symlinks), read from the system's process list (a path or folder it can't tell counts as in them): a program that runs all the time elsewhere under such a name (Gaijin's `gjagent.exe`, also in War Thunder's root) isn't the game. Anti-cheat is a marker file
  or folder by name anywhere in the install (`GameFiles.DetectAntiCheat`, the only detector), hidden or system ones
  included; junctions and symlinks count by name and aren't followed (one between the install root and the exe counts
  as anti-cheat); an install it can't list whole counts as anti-cheat. The install checks again after copying (the
  install root's and exe folder's own entries), all of it whatever happened after the copy, and all of it once more as
  its last step, after every wait. No check is final against a later update: each refresh checks the install root's
  and exe folder's own entries again. Engine readers and middleware detection use the detector only to skip their own
  work: their verdict is acted on at the game's next evaluation. Every verdict an evaluation or the install meets goes
  through one place (`AntiCheatFound`), which records it in memory (the scan's cache and the game's state) and then,
  under the recorder lock, takes the recorder out, whatever follows (a stop, a cancelled scan, a scan cache that can't
  be written), or, while the game runs or holds the proxy, as soon as it exits; a failed install's leftovers go the
  same way. Removal deletes exactly the files installed, checked by hash, then the recorder's data files as the
  uninstall below does; the recording is imported first. In a folder with no recorder of ours (no proxy, no
  `RecorderExe`), every scan deletes those data files if any are left, unless the game runs. A recorder whose
  `RecorderExe` is in another folder than the game's exe (one installed next to a launcher discovery took for the
  game) is removed from there the same way, then installed next to the game's exe; the last session's csv, log and
  frame log move along (of two with the same name, the newer stays), so the game page keeps its report. While it moves,
  every running check covers both exe folders and the install root, the uninstall hook's too; both stay recorded
  (`GameRecord.RecorderMoveFrom`, `RecorderMoveTo`) until the old folder's recording is imported and its files are gone
  (a failed import or merge keeps them, and the next reconcile tries again; an anti-cheat removal records them too, and
  then nothing of ours goes into the new folder), and an anti-cheat removal
  takes the recorder out where the record says it is.
- **Following the running exe**: when a game without anti-cheat starts and a fresh quick anti-cheat check of its install
  is still clean, the watcher reads the paths of the processes named like its exe from the system's process list
  (`ProcessTree.ImagePath`: NtQuerySystemInformation, no process is opened). One copy running from another exe of the
  install than the game's is kept as `GameRecord.RunsExe` and used instead of discovery's from then on (a game added by
  hand: only once its folder is confirmed), until it's gone, discovery names another exe or the game's build changes
  (store version, else the followed exe's size and write time). The recorder then moves next to it after the game exits,
  as above. Discovery itself never takes an exe in a patcher's or installer's copy (`PatchData`, `__Installer`,
  `Backup`, `Staging`), and of Unreal exes named alike takes the one nearest the install root.
- **Uninstall** (Velopack's uninstall hook, `ScsKiller.RemoveAllRecorders`): removes the recorder the same way from
  every folder a `GameRecord.RecorderExe` names, then the recorder's `scskiller.db` once merged into `recording.db`,
  and its csv, frame log, log and keys file. A running game's folder is left, and `recorders.log` says so: running is
  also a process named like any exe in the install's whole tree (its names read once per game, the anti-cheat scan's
  entry cap; a tree it can't read whole, or by the hook's deadline, counts as running), since the record's folders may
  be another exe's than the game's. The running processes are asked again before each folder's writes.
- **Recording alongside a mod**: the recorder records under any layer that wraps the game's device: a foreign
  `d3d12.dll` it chains, or ReShade as `dxgi.dll`. A foreign `d3d12.dll` (ReShade or another wrapper) is chained only
  when the user turns that on for the game: the mod is renamed to `d3d12.scskiller-next.dll` (bytes untouched, its
  SHA-256 saved), and the recorder loads it via `next=` in `scskiller.ini`. Removal renames the mod back and never
  overwrites another file that took its place. Under a layer, the recorder hooks the device the layer returns and the
  real device under it, found through ReShade 6.8+'s `IID_UnwrappedObject`, else through `GetDevice` of a root
  signature (layers don't wrap root signatures). A layer may change a create before the driver sees it (RenoDX adds a
  root-constants parameter to every root signature and replaces shaders) and creates pipelines of its own (add-on
  pipelines, bind-time clones). The recorder records the desc the game asked for, as without a layer, and the desc the
  driver got; a `'W'` record pairs the two when they differ (see [`'W'`](#ray-tracing-state-objects)). The warm
  replays both, so a compile is right whether the layer is still installed or not. The planner takes no template,
  learned root signature or rule check from the driver's records: they replay as recorded. The driver's records may
  name root signatures and shaders that exist in no game file, so this PC's recording is their only source. Only the game's
  records are shared: a layer's output depends on its build and the user's settings, and its bytes are a mod's, so the
  `'W'` records, the records they name as the driver's, their `'N'` and the root signatures only those name are never
  uploaded (`HashOnly.Canonical`) nor put in a middleware pack. `Recordings.Layered` counts a recording's `'W'`
  records: the game's creates a layer changed and the layer's own. OptiScaler, Special K
  (they pick their role from their file name) and vkd3d-proton (it runs the game on Vulkan) are refused as the chained
  d3d12.dll; OptiScaler as dxgi.dll (or another name it supports) runs beside the recorder.
- **Offline session** (`ScsKiller.StartOfflineSession`, app only): the one case where the recorder goes into an
  anti-cheat game. Offered (`GameState.OfflineEligible`) only for a game of `Games/offline-eac.json` (Steam ids, the exe
  each is discovered with, sources; embedded, never served): EasyAntiCheat games that run offline without it when their
  exe is started directly with `steam_appid.txt` beside it, so the game doesn't restart through Steam and
  `start_protected_game.exe`. The game's verdict must be EasyAntiCheat and its engine supported on D3D12. Never
  automatic: the user allows it per game (`GameRecord.OfflineRecord`) and confirms every launch (`confirmed`, from the
  game page's dialog). Refused, with nothing written, while the game or a compile of it runs, while a recorder removal
  or revocation is pending, unless a fresh full check finds EasyAntiCheat and, with its markers ignored, nothing else,
  when any of the recorder's files is already next to the exe (a mod's `d3d12.dll` included: never alongside a mod), when
  a `steam_appid.txt` there holds another id (one with this id is the user's and stays), when ReShade is there, or when
  Steam isn't running (the game would restart through it, with EasyAntiCheat; Steam's offline mode is fine). A leftover
  `d3d12.dll` would still be loaded, as a pass-through, into a normal EasyAntiCheat launch, so the session is a journal:
  under the recorder lock, before any file is written, `GameRecord.OfflineSession` keeps the folder's entry names and
  every name the session may create there (`d3d12.dll`, `scskiller.ini`, `steam_appid.txt`, their temp names,
  `scskiller.armed`, the recorder's data files and keys file; refused if any is already there), and the manifest names
  the first three by hash for the anti-cheat removals and the uninstall hook. Recovery is in place before any file is
  published: with `CleanupHelper` set (the app), an HKCU RunOnce entry (its name starts with "!", so Windows deletes it
  only after its command ran) runs `SCSKiller.exe --offline-cleanup <id>` at the next logon, and that helper starts at
  once, no window, outliving the app; while the journal has no pid yet it waits as long as the SCSKiller that started
  it runs. A helper that doesn't start refuses the session. Each file is written to a temp name and renamed into place.
  The exe is started by SCSKiller itself with `CREATE_SUSPENDED`; its pid and creation time go into the journal and the
  attestation (`pid=` and `pid_time=`, its creation FILETIME, in the ledger entry and `scskiller.armed`), then it is
  resumed and the journal says so (`Resumed`); a failure in between ends it. A journaled process that is alive but was
  never resumed is SCSKiller's own suspended child (the app ended while it set the session up): the cleanup ends it,
  its pid and creation time checked on the handle, and the helper does so after a minute. The proxy admits a bound
  attestation only in that process, and for it drops EasyAntiCheat's names from the markers beside the exe (any other
  marker still refuses, and an anti-cheat client loaded at the first device too). Any other launch with the files there, Steam's through
  EasyAntiCheat included, is a pass-through. While the process runs (this app's, or one whose journaled pid and creation
  time match a live process, as when SCSKiller opens again while the game loads), no disarm, anti-cheat removal or
  cleanup touches its attestation; the cleanup checks that again under the recorder lock. The cleanup
  (`ScsKiller.CleanOfflineSession`, one at a time across processes) runs the moment that process exits, by its handle, in
  the app and in the helper (which opens the process only if its creation time is the journal's): the attestation is
  revoked, the journaled names a launch loads (`d3d12.dll`, `scskiller.ini`, `steam_appid.txt`, the armed file, temp
  names) are deleted first, whatever the files hold (none existed before the session), and only then is the inbox merged
  into `recording.db` and the data files deleted, which waits while another process runs from the folder (by name, not
  the session's pid). A name leaves the journal, saved, as soon as the folder, listed whole after each delete pass,
  shows it gone (before the wait for the recording), so a file put there since is never deleted. A folder that can't be
  listed proves nothing gone, unless it is gone itself while its drive is there (the nearest ancestor that can be
  listed doesn't have it): the session is then over. The folder's names are then
  compared with the kept ones (a difference is logged; nothing that isn't the session's is deleted). The session and its
  RunOnce entry stay until the journal is empty; the helper tries again every few seconds for a day (and writes the entry
  again meanwhile), and the app first in every scan and every watcher pass. An update's apply stops every process under
  the install root, the helper too, so the two exclude each other (`ScsKiller.BeginUpdate`): from the start of an apply
  (on quit, at start, "Restart to update" while it waits for the queue) no session starts, and the apply is skipped,
  and checked again right before the handover, while a session starts, its process runs or its helper does (it holds a
  named mutex). A session whose files wait for a drive that's gone doesn't hold updates back. Accepted limit: SCSKiller
  uninstalled during a session removes the recorder by the manifest, but the helper and the logon entry go with the
  app, so files a running game holds can stay until removed by hand; the game page's warning says not to. Its recording
  is shared like any other. Before the data files go, the session's report (its csv, log and frame log) moves to the
  game's data folder (`offline-session`), where the game page reads its last session and frame times while the game
  folder has none of its own, as for any recorded game; Clear recording deletes it too. On the game page, "Record while I play"'s
  switch allows offline sessions for such a game, and while it is on the card has the ban-risk warning and the button.
- **Shader mods** (`Games.ReShade.Detect`): ReShade in the exe's folder, else the install root: any DLL there whose
  version resource names ReShade, or one of its usual names (dxgi.dll, d3d12.dll, ...) holding its description or its
  add-on export. Only the build with full add-on support loads add-on files; the standard one is known by its "only
  limited add-on functionality" warning, and its add-ons never count. Its add-ons (`*.addon`, `*.addon64`) are the ones
  in ReShade.ini's `[ADDON] AddonPath`, else its folder, less `DisabledAddons`; each is classed by what it does to the
  game's pipelines. One that replaces shaders is known by a string its release builds always log where they register
  their pipeline hooks: RenoDX's `utils::shader attached.`, Luma's config-version warning; others (renodx-dlss5,
  dlssfix) are NotPipeline. Most RenoDX add-ons add a constant to every root signature the game creates
  (LayoutInjecting): the driver's cache keys on the root signature, so a compile of the shipped ones matches nothing.
  The rest only replace shaders (ReplacesShaders), as Luma does: only those shaders' pipelines change. Both RenoDX paths are in every
  build and a runtime flag picks one, so a RenoDX add-on is classed by ReShade.log when the log was written after the
  add-on file and attributes a line to it (the file a "Registered add-on" name was loaded from, then that name's
  `mods::shader::OnCreatePipelineLayout(will insert` or layout cloning); else by its build folder in
  `Games/shader-mods.json` (its file name, or its version resource's OriginalFilename); else, as is an add-on that can't
  be read, ReplacesShaders. A game with either kind (`GameState.ShaderMod`) whose layer a copy reproduces
  (`ReShadeInstall.Copyable`: ReShade in the exe's own folder, or in an Xbox app game's package root (its install folder,
  with MicrosoftGame.config, which Windows searches first), under a name the game loads by itself, dxgi.dll, d3d12.dll,
  d3d11.dll, d3d10.dll, d3d9.dll, opengl32.dll, dinput8.dll or the recorder's chain name, or ReShade64.dll beside an
  OptiScaler the game loads by itself (dxgi.dll or d3d12.dll; winmm, version, dbghelp, wininet or winhttp.dll when the
  exe imports it) whose OptiScaler.ini sets `[Plugins] LoadReshade=true`, as OptiScaler then loads it from the exe's
  folder; and no Luma add-on, whose
  shader files a copy leaves out) compiles through a copy of its layer: as each warm process starts,
  `ScsKiller.LayerFor` copies ReShade's DLL (as dxgi.dll, whatever its name in the game),
  the enabled add-ons that change pipelines and ReShade.ini without `[ADDON] AddonPath` and `[INSTALL] BasePath` into
  `work\layer\`, and `scskiller_warm --layer` stages them next to the warm's exe. ReShade starts there without a
  swap chain (add-ons load with the device) and its add-ons change the plan's and the recording's pipelines as in the
  game; what the layer made itself ('W') is created under it as recorded (see Recorder, "Recording alongside a
  mod"). Pipelines an add-on creates only while the game draws (RenoDX's bind-time clones) come from a recording
  under the layer. The warm keeps what it ran through (`GameRecord.WarmedLayer`: ReShade's DLL and those add-ons by
  name, size and write time); a game whose layer differs from it now is Stale, and a stopped compile resumes only
  through the layer it stopped in (`GameRecord.ResumeLayer`), else it starts over. With ReShade installed as d3d12.dll,
  the recorder's own name, the warm copies it the same way, and the recorder records under it only when the user
  chains it ("Record alongside ReShade"); the game page says so. ReShade64.dll that OptiScaler loads is copied with the
  whole chain, as OptiScaler's settings can change the game's root signatures (`MipmapBiasOverride` rewrites static
  samplers): OptiScaler as dxgi.dll, ReShade64.dll, the FidelityFX, XeSS and NGX libraries OptiScaler loads (from its
  OptiDllPath folder when that stays inside the exe's folder, else `OptiScaler\`, else beside the exe; PE files only, up
  to 128 MB each and 512 MB in all), and OptiScaler.ini without `[Libraries]` paths, `[Plugins] Path` and
  `[Log] LogFileName`, with `[Hotfix] CheckForUpdate=false`, so it loads and writes only in the stage and makes no
  network request; an OptiScaler.ini with a section header that isn't a whole `[name]` on its line blocks the game, as
  SimpleIni may read such a header across lines. OptiScaler and its ini are part of what the warm depends on. Any other layer
  a copy can't reproduce (`ReShadeInstall.Block`: Luma; ReShade only in another store's game's install root above
  the exe; ReShade64.dll beside an OptiScaler that doesn't load it; an .asi, ReShade64.dll or a renamed DLL another loader
  may or may not pick up): a LayoutInjecting add-on in it (`GameState.ShaderModBlocks`) makes the game Unsupported with that case and its
  fix as the reason, never queued, planned, compiled or shared, checked again right before each warm starts, and the
  recorder taken out at once (`TakeOutNow`) and kept out; a ReplacesShaders one only adds a note, and the game compiles
  without the layer. Each file is read once per size and write time, up to 128 MB; anti-cheat installs aren't read.
- **Import** (`Recordings`): the game folder's `scskiller.db` is an inbox. When it changed since the last import
  (`GameRecord.RecordingInbox`, its size and write time), its records are merged into `recording.db` by record key,
  after the ones already there. Once `recording.db` is written, the inbox is emptied, but only while nothing has it
  open (the recorder holds it for the whole session) and only at the length that was read; once emptied, whatever it
  gets next is imported. `recording.db` is written to a temp file (its own name per writer) that is read back and
  compared before it replaces the old one, all under the recording lock; a temp file found under it whose write is an
  hour old or older than the PC's start is a killed writer's, and goes.
- **Keys file**: next to `scskiller.ini` the app writes `scskiller.keys` ("SCSKKEY1", then 20-byte hashes): the shaders
  of the last index (`index.shaders`), the blobs `recording.db` holds and the key of every record that replays from the
  two. The recorder loads it in record mode and treats those as already recorded, so the emptied inbox only gets what's
  new, and a new pipeline's shipped shaders go in by hash, without their bytes: the install gives them back. A shader
  in no file of the game (built at run time, a mod's, a middleware DLL's) is recorded with its bytes. It is written
  when the recorder is installed, after an import, after a compile indexed another build and after Clear recording. A
  game never indexed, or one whose index isn't kept (`Sharing.SaveShipped`), has no shipped shaders in it and is
  recorded with every shader's bytes. A record whose shader `recording.db` names by hash and the last index no longer
  has is left out, so the recorder records it again with its bytes if the game still creates it. The shipped shaders
  are named only while the install is the build last indexed (`IndexIsInstalled`: the store's build id, or without one
  the exe's size and write time, the signals that mark a warm stale after a game update). A scan that finds another
  build rewrites the file without them (`GameRecord.KeysIndexHash` notes whose it names), and new pipelines are
  recorded with every shader's bytes until the next compile's index. A game updated and played before SCSKiller looks
  at it again records with the previous build's file: a record naming a shader the new build doesn't ship then has no
  bytes, the compile skips that record alone, and its index takes it out of the file so it is recorded again. A
  Battle.net or Ubisoft game, which has no build id, patched without its exe changing looks unchanged. Measured on
  three recordings taken as a first session, `scskiller.db` is 2-13% of its size with every shader's bytes, nearly all
  of it the records themselves; an index of 286,000 shaders makes a 5.7 MB file, which the recorder keeps as a sorted
  array (40 ms to load).
- **Stored form** (`PsoDb.WriteCompact`): `recording.db` is `\0SCSKREC`, a version byte, the length of the proxy db it
  holds, then that db Brotli-compressed (quality 6). Readers (`PsoDb.Read`) take either form; `PsoDb.CopyRaw` gives the
  proxy a plain db. A shader blob that a record names and the build's index has is left out: the install gives it back.
  Root signatures, shaders in no file of the game (built at run time, a mod's, middleware DLLs') and blobs no record
  names keep their bytes. Shaders are dropped by the index only when it's of the build installed now
  (`GameRecord.IndexGameVersion` and `index.shaders`); otherwise every byte is kept until a compile indexes the build,
  which drops what that index has (`GameRecord.RecordingIndexHash`) and rewrites the keys file. Measured on five
  recordings, 87-98% of the bytes were shaders the index has, and the rest compresses about 20 times.
- **Planning and warming from it**: a compile prepares `work\recording.db` once, before planning: `recording.db`,
  merged with `community.db` when one is in use (`Community.Union`), with every shader it names by hash read from the
  install (`Rehydrate.Run`: the engine reader, then the middleware DLLs). The planner and the warm both use that file,
  so they see the same bytes as when the recording kept them. The readiness check reads records only, from
  `recording.db` or `community.db`, without merging them.
- **Migration**: a `recording.db` stored as a plain proxy db, or a `recording.all.db` beside it, is converted once in
  the background after a scan (`ScsKiller.MigrateRecordings`), never while the game or a compile runs: the inbox is
  imported, `recording.all.db` is deleted, and the old file stays until the new one reads back the same.
- **Recording limit** (`Settings.RecordingLimitMB`, one of `ScsKiller.RecordingLimits`, default 256 MB, 0 = unlimited;
  a stored value that isn't a choice becomes the next choice up, or unlimited): covers the game folder's
  `scskiller.db` and `recording.db`. The app writes what `recording.db` leaves as
  `max_db_bytes` into its own `scskiller.ini` (`ScsKiller.DbCap`); the recorder stops appending once the db reaches it,
  a record going in whole or not at all, and still writes the csv and log. The game then shows "Recording paused: limit
  reached" (`GameState.RecordingPaused`) until an import empties the db.
- **Clear recording** (`IScsKiller.ClearRecording`, CLI `record clear <game>`): deletes the recording's files, all or
  none, never while the game runs or compiles; the recorder and its ini stay, and the keys file is rewritten. The next compile
  plans from the game's files.
- **Frame times** (`scskiller_frames.bin`, read by `FrameLog`): at the first device the recorder takes a factory from
  the process's `dxgi.dll` and hooks its `CreateSwapChain*` slots, then `Present` / `Present1` of every swap chain it
  creates, keeping the original per vtable (a wrapper's swap chain and the real one differ). A frame is the QPC at which
  the outermost present of a thread returns, as PresentMon's `FrameTime` counts (measured equal to PresentMon frame for
  frame); nested presents and `DXGI_PRESENT_TEST` aren't frames. The hook queues the timestamp and a thread writes the
  file once a second. The file is u32 records: `0xFFFFFFFF` + u64 unix ms (the csv's `#session` stamp), u64 microseconds since the recorder loaded
  (the csv's `t_ms` clock), u64 QPC, u64 QPC frequency opens a launch; top 4 bits 0-14 = a frame of that swap chain,
  the low 28 bits the microseconds since the previous record; top 4 bits 15 = no frame for the low 28 bits'
  milliseconds. The file holds the last launch that presented, replaced at its first frame (3 hours at 300 FPS is
  13 MB; a launch stops writing at 32 MB). It isn't part of the recording: not in the recording limit or the recording's
  size, never shared; Clear recording and removing the recorder delete it with the csv. `frames=0` in `scskiller.ini` turns it off (diagnostics
  only). Frame generation turns it off for the rest of the launch: with its swap chain, Steam's overlay and the `Present`
  hook call each other until the stack overflows. FSR 3's (also under OptiScaler and dlssg-to-fsr3) and XeSS's are made on
  a present queue named before the create (`AMD FSR PresentQueue`, `XefgInterpolationSwapChain::present_queue_`): a
  `CreateSwapChain*` on such a queue puts the original `Present` / `Present1` back before the swap chain is made (by
  compare-exchange: a slot another hook took after ours is left), and hooks nothing more. `OptiScaler.ini` beside the exe
  with its frame generation on hooks nothing at all, since OptiScaler as `dxgi.dll` can make that swap chain through a
  factory the recorder doesn't hook: `[FrameGen]` `Enabled=true` with `FGOutput` other than `auto` / `nofg`, or in older
  versions `FGType` `nukems`, or `FGType` `optifg` (its default) with `[OptiFG]` `Enabled=true`. Each `CreateSwapChain*` logs its queue's name. The game page's last session (`GameState.LastFrames`) reads the last launch with the creates csv of the
  same launch (the `#session` with the same stamp; from an older recorder, without `#clock`, the only one within 10 s, else none). A frame
  is **cold-filled** when its overlapping compiles of 100 ms or more (not a library load or a RayQuery PSO at the
  floor) sum to half its length or more: a compiled run's load creates stay under
  100 ms, cold compiles take 159 ms at the median. A frame of 50 ms or more is, in order:
  - during startup (from the first create to the first 3 s with fewer than 30 creates, or with no compile and no second
    of 100 creates, so a trickle of cache hits in play doesn't hold it open while a warmed run's precompile does;
    extended within one allowance, 10 s after that first quiet window: over a second of 100 creates or more whose first
    create ends in it (a title screen's second precompile) up to its own quiet window, and over a slow frame with no
    create that the boundary falls inside, to its end; never past the allowance, and not over a compile in play, nor a
    burst that comes after one. A create or frame ending exactly at its end is startup's, after it play's): **loading, compiling shaders** if cold-filled, else **loading**;
  - in the last 10 s before the last frame: **quitting**;
  - cold-filled: a **shader stutter**, however many creates overlap it (a level load that compiles is shader cost);
  - overlapped by 100 creates or more: **loading** (a load whose creates are fast);
  - overlapped by a compile of 10 ms or more (not a library load or a RayQuery PSO at the floor, a ray tracing state
    object only from 25 ms): a **shader stutter**;
  - else an **other hitch**, left out from 5 s when nothing compiled under it (a pause).

  Startup comes from create timing alone, which can't tell a menu's background compiles from play: when in doubt it
  counts as play, since a hitch the user saw is better reported than hidden. A launch that never goes quiet (quit
  during startup, or compiling nonstop) is all startup: a window cut off by the launch's end (the latest of its `#end`,
  the watched exit, its last frame and its last create) isn't a quiet one, unless it is the first. The last session's
  counts use the same startup, from the same end.

  Play is what lies between startup and quitting, a frame by its end as above; the 1% low is of its frames but the pauses. A compile on a worker thread that the
  render thread waits for is still a shader stutter, so the csv's `presents` column isn't used.
- The recorder loads NVAPI only on NVIDIA and only in a run it admitted (no anti-cheat running), to record the
  shader-extension state pipelines are created with.

## scskiller_warm.exe protocol

```
scskiller_warm.exe <workdir> <game exe file name> [--threads N] [--priority below|idle] [--start N]
                   [--stop-event <name>] [--adapter-luid <hex>] [--rt-threads N] [--skip i,j,...] [--memory-mb N]
                   [--package <app user model id>] [--stage-path <install folder>\<exe dir in the install>\<exe>]
                   [--skip-keys <sha1 hex>,...] [--isolate i,j,...] [--pass K]
                   [--ags <amd_ags_x64.dll> --ags-app <name> --ags-engine <name>]
```

- `workdir` holds `scskiller.db` (the recording, may be missing) and `scskiller_gen.db` (the plan's templates and
  items). Staging goes to a new folder of the run's own, `<workdir>\stage-<pid>-<n>\` (never one that exists): a copy of
  `scskiller_warm.exe` named `<game exe>`, the proxy `d3d12.dll`, and the two databases. The staged copy runs as a child
  process and prints the output. When it exits the staged inputs are deleted, and the folder keeps the proxy's outputs:
  `scskiller.log`, `scskiller_creates.csv`. The first line, the `stage` event, names the folder. Nothing else in `workdir`
  is written or deleted; the app deletes its whole work folder after a compile.
- `--stage-path`: a relative path ending in `<game exe>`, without `.` or `..` parts, for AMD's path-matched profiles.
  The child is staged and launched under `<staging folder>\<its folders>\`, and its outputs move up to the staging
  folder when it exits. A path that would reach MAX_PATH stages without it. Ignored with `--package`.
- `--ags <dll> --ags-app <name> --ags-engine <name>` (AMD): the child creates its device through that AGS 6 DLL with
  those names, as Unreal does (see [AMD, D3D12](#amd-d3d12)). The DLL's imports are resolved from System32 only. If AGS
  fails, a stderr line says why and the child creates a plain device. The app passes it only where the registration is
  proven (`ScsKiller.AgsFor`), with the game's own `amd_ags_x64.dll` when it is AGS 6, else the one in `native\` (AMD's
  signed release, pinned by hash).
- `--pass K` (AMD careful compile): `<workdir>\scskiller_pass.bin` holds one byte per item, its pass (1-32, or 255 for
  the rest). The process creates only pass K's items and counts the others done, so `done`, `total` and `--start` keep
  their meaning.
- stdout carries one JSON object per line and nothing else (usage and other text go to stderr):
  - `{"event":"stage","stage":"<staging folder>"}` first, as soon as the folder exists
  - `{"event":"start","total":117197,"adapter":"<DXGI adapter description>","exe":"game.exe"}`
  - `{"event":"progress","done":64210,"total":117197,"failed":3,"rate":480.2}` about every 500 ms
  - `{"event":"done","done":117197,"total":117197,"failed":5,"seconds":288.1,"stopped":false,"crashed":[]}`
  - `{"event":"retry","from":F,"rtThreads":N,"failedItem":X,...}` (see [Faults and retries](#faults-and-retries))
  - `{"event":"error","message":"..."}`
- `done` counts from item 0, including items skipped by `--start`; `failed` and `rate` (items per second) are this
  run's. Replay order is deterministic (database file order), so after a stop, pass its `done` as the next `--start`.
- `--stop-event`: a named manual-reset event. When set, workers stop taking items, in-flight compiles finish, `done` is
  printed with `"stopped":true`, exit code 0. Never kill the process to stop it: the driver may not write its cache. A run
  stopped after its driver was poisoned or its device removed prints the first item it left unfinished as `done`.
- Pause: the caller suspends the process. The child sees the heartbeat stop within about a second and its workers wait.
  If `scskiller_warm.exe` dies, the child stops gracefully.
- `--threads N`: default logical CPUs - 2. `--priority below` (default): below-normal worker threads; `idle`: idle
  priority class and threads.
- Heap: on NVIDIA the warmer runs `native\segheap\scskiller_warm.exe`, the same code whose manifest selects the segment
  heap (`Warmer.ExeFor`); it stages the proxy from `native\`. On the NT heap NVIDIA's compiler threads wait on the
  process heap's lock: a 60,000-pipeline warm on 32 threads took 742 s at 27% CPU, 227 s at 87% on the segment heap.
  AMD's compiler doesn't wait there, and on the segment heap an 84,000-pipeline AMD warm took about 25% longer (147 s
  against 118 s), so AMD and other vendors keep the NT heap.
- `--adapter-luid <hex>`: `(HighPart << 32) | LowPart`; default the hardware adapter with the most dedicated VRAM.
- `--package`: run the staged copy with that app's package identity (see [NVIDIA, D3D12](#nvidia-d3d12)). The child
  then gets none of the caller's environment.
- Exit codes: 0 completed or stopped; 1 failure, after an `error` line; 3 after a `retry` line.
- D3D12 failures are counted by record kind and cause at the end of the log. `SCSKILLER_D3D12_DEBUG=1` turns on the
  D3D12 debug layer, `SCSKILLER_D3D11_DEBUG=1` the D3D11 one.

### Memory budget

`--memory-mb N` (0 = none) limits the staged process's private memory, checked every 100 ms. Shader bytes aren't loaded:
both databases are mapped read-only and each blob is a view into the file. Parked ray tracing objects get a quarter of
the budget (at least 256 MB) and are released when it's exceeded; if the process is still over, the number of running
workers drops by one every 5 s and rises again after 30 s under 80% of the budget. Loading the records and the device
take about 1.3 GB, the floor of what a budget can hold; the driver's cache write at exit comes on top.

The app passes `Settings.MaxCompileMemoryGB`, whose Auto value scales with physical memory: 2 GB up to 20 GB of RAM,
4 up to 28, 6 up to 40, 8 up to 56, else 16 (`ScsKiller.AutoCompileMemoryGB`).

### Faults and retries

A driver fault or hang in a ray tracing state object never stops the warm or skips items silently:

- State objects are never released on worker threads during the run: they are parked and released in batches on one
  thread while no create runs (`SCSKILLER_WARM_RT_PARK_MB`, default 2048). This avoids a driver race on concurrent
  releases.
- Every driver call on a state object is SEH-guarded. After the first fault or hang, the process's driver isn't trusted
  for ray tracing: later state objects are left unfinished, PSOs go on, and the run ends with a `retry` line. The warmer
  then starts a new process from `from` with a quarter of the ray tracing threads (32 → 8 → 2 → 1). Only an object that
  faults with one ray tracing thread counts as failed, and later processes skip it (`--skip`).
- A worker stuck in one item for over 60 s (2 s once stopping) is abandoned; a stuck PSO counts as failed and a new
  worker carries on, at most 8 times. Time the process spends suspended (a pause) doesn't count.
- **A removed device** (`DXGI_ERROR_DEVICE_REMOVED`) makes every later create in that process fail. The replay checks
  `GetDeviceRemovedReason` after a failed create and every 100 ms; once removed, it takes no more items and ends with
  a `retry` line whose `reason` is `removed`. The items whose create failed on the removed device are the suspects: one
  alone is blamed and its record key listed in `crashed`; several go to `isolate`, and the next process (`--isolate`)
  creates them one at a time before its workers start, to find the one that removes the device. `--skip-keys` items are
  never created and count in `done` but not in `failed`. The warmer relaunches with every key seen so far, at most
  `Warmer.MaxRecoveries` (20) times per compile. The app keeps the keys per game and driver (`GameRecord.CrashKeys`),
  skips them in every later compile on that driver, and reports them apart ("N skipped (they crash the GPU driver)").
  A compile on another driver gives each one retry.
- After its last line the child signals `Local\SCSKiller.Final.<scskiller_warm's pid>` (created before the child
  starts); if it hasn't exited `SCSKILLER_WARM_EXIT_S` (default 600) seconds later, not counting a pause, it's terminated
  and the run returns 3.

### D3D11 items

D3D11 items live in `scskiller_gen.db` only and are replayed after all D3D12 items, with the same counting, stop and
resume rules.

- `'1'` (24 bytes): `u32 stage` (1 VS, 2 PS, 3 DS, 4 HS, 5 GS, 6 CS) + the SHA-1 of a `'B'` blob holding a DXBC
  container. One item per shader.
- `'2'` (40 bytes): a HS SHA-1 + a DS SHA-1, a tessellation pair from one shader map whose control points link. Every
  HS and DS is warmed in at least one pair.
- Each item is one draw or dispatch into a 1×1 target (`proxy/warm11.cpp`): a VS with an input layout built from its
  input signature, a PS behind a generated pass-through VS, a pair behind a pass-through VS with the right patch list.
  Every declared resource slot gets a dummy of its kind. The draw uses `DrawInstancedIndirect` / `DispatchIndirect`
  with zero counts in a GPU buffer, so the driver compiles but no game shader runs.
- One device per thread, at most 4. A call taking over 60 s abandons its worker, as for D3D12.

## Ray tracing state objects

The recorder hooks `ID3D12Device5::CreateStateObject` and `ID3D12Device7::AddToStateObject`; their vtable slots are
checked against `d3d12.h` at compile time (`proxy/vtslots.cpp`). Records are replayed exactly, in file order.

- `'R'` = CreateStateObject: u32 `D3D12_STATE_OBJECT_TYPE`, u32 n, then n subobjects in the game's order, each u32
  `D3D12_STATE_SUBOBJECT_TYPE` + its canonical form:
  - `STATE_OBJECT_CONFIG` u32 flags; `NODE_MASK` u32; `RAYTRACING_PIPELINE_CONFIG` u32 depth; `..._CONFIG1` u32 depth,
    u32 flags; `RAYTRACING_SHADER_CONFIG` u32 payload, u32 attributes;
  - `GLOBAL_` / `LOCAL_ROOT_SIGNATURE`: root signature sha1[20] (a `'B'` blob);
  - `DXIL_LIBRARY`: library sha1[20] (a `'B'` blob, hashed like a shader) + exports;
  - `EXISTING_COLLECTION`: the record key[20] of the collection's own `'R'` + exports;
  - `SUBOBJECT_TO_EXPORTS_ASSOCIATION`: u32 index of the associated subobject + names;
    `DXIL_SUBOBJECT_TO_EXPORTS_ASSOCIATION`: str subobject name + names;
  - `HIT_GROUP`: str export, u32 type, str any hit, str closest hit, str intersection;
  - exports = u32 n + n × (str name, str rename, u32 flags); names = u32 n + n × str; str = u32 UTF-16 length
    (0xFFFFFFFF = null) + the characters.
- `'A'` = AddToStateObject: the grown object's record key[20] (an `'R'` or another `'A'`) + the addition's body as
  above. The result takes the `'A'` record's key, so chains are recorded link by link.
- `'N'` = the NVAPI state a record (of any tag) was created with: its key[20], u32 shader-extension slot (~0u = none),
  u32 space, u32 scope (1 device, 2 thread, 3 PSO extension), u32 `NvAPI_D3D12_SetCreatePipelineStateOptions` flags.
  Written once per record and state, after the record. The warm sets the state on the creating thread around that
  create.
- `'L'` (hash-only recordings only) = a record's key[20] whose shaders the build doesn't ship: built at run time or by a
  mod.
- `'W'` = a create a layer wrapping the device changed: the key[20] of the record the driver got, then the key[20] of the
  record the game asked for (all zero: the layer's own create). Written only when the two differ; both are records of
  the same db. Merged into `recording.db` by key and named in the keys file like other records; never a warm item and
  never shared.
- Not recorded: work graphs, generic programs, unknown subobjects, and anything built on an object the recorder
  didn't record.
- Replay: each record is created once, by the worker that takes it or earlier by one whose record builds on it, so
  parallel workers and `--start` work. A record whose dependency failed fails too. On a device without `Device5` /
  `Device7` every one fails with `E_NOINTERFACE`.

### Stream output

A pipeline with a stream-output declaration (`NumEntries` > 0) stores it in its record: a `'G'` desc appends it after
`Flags`, a stream writes it as subobject `0x10007` (`PsoDb.SoDecl`): u32 n, n × (u32 stream, u32 semantic length
(0xFFFFFFFF = a gap) + ASCII, u32 index, start component, component count, output slot), u32 stride count + strides,
u32 rasterized stream. Without entries the record is unchanged. `SCSKILLER_WARM_ROUNDTRIP=1` makes the warm
re-serialize every decoded record and compare it with the original.

## scskiller_creates.csv

Written by the recorder next to the game's exe. Rows are `t_ms,kind,known,tuple_known,ms,key,proxy_ms,tid,presents`:

- `t_ms`: when the create returned, in ms since the recorder loaded; `ms`: the driver call alone;
- `key`: the record key in hex (SHA-1 of tag + canonical payload, as `PsoDb.Rec.Key`); a key the db or the keys file
  held when the game started has `known` 1; the db gets the record of every other key, until it reaches `max_db_bytes`;
- `proxy_ms`: the recorder's own time outside `ms`;
- `tid`: the creating thread's id; `presents` 1 when that thread had presented a frame before the create (a create there
  holds up the frame), 0 otherwise, including a render thread's creates before its first frame and every create with
  `frames=0`.

Readers ignore lines starting with `#` and accept extra fields. Markers give real play time:

- `#session,<unix_ms_utc>,<exe file name>` at the first device. The name is `GetModuleFileNameW(NULL)`'s, exactly as
  launched: the case AMD's cache key uses. The frames file's launch record carries the same stamp.
- `#clock,<t_ms>` right after it: that instant on the `t_ms` clock, which starts when the recorder loads.
- `#end,<unix_ms_utc>,<t_ms>` at process detach, best effort.

Older recorders wrote neither `#clock` nor `#end`'s `t_ms`; readers then take the `#session` stamp as `t_ms` 0 and
pair a frames file with the only launch stamped within 10 s of it, or with none.

A staged warm writes neither marker. Readers split launches one way (`SessionLog.Launches`): at each `#session`, and
rows after an `#end` or after `t_ms` restarted open a launch without a stamp (another process's, or an older proxy's).

The game page's last session (`SessionLog`) leaves the compiles of the launch's startup out of its compile count and
its worst compile: startup as the frame report defines it, from the creates alone. The first launch after a compile
(`LaunchCheck`) counts every create.

## Plan file

`plan.bin` holds templates (canonical pipeline payloads in the recorder's database encoding, recorded or synthesized),
root signature blobs (generated, not game content), and plan items (template key, root signature hash, stage → shader
SHA-1, optional input layout), plus the plan header. It never contains game shader bytes: those are read from the
install at materialize time.

## Clear cache

`ClearGameCache` makes a game's next run cold with one all-or-nothing delete of:

- its driver cache files, by the keys learned for it (see [Driver caches](#driver-caches));
- the runtime's cache files (`<GUID>[_VEN_...].dxcache`, its `-shm` and `-wal`, the older `.idx`, `.val`, `.lock`) in
  its Windows shader cache folders under `%LOCALAPPDATA%\D3DSCache`, identified by reading the exe paths in their
  SQLite databases (read-only, with Windows' own `winsqlite3.dll`); anything else in a folder is left, and the folder
  with it;
- only when asked (CLI `cache clear <game> --game-precache`, the app's "Also delete the game's own shader cache"), the
  caches the game writes itself: Unreal's `<Project>_<ShaderPlatform>.upipelinecache` in its Saved folder and
  `*.ushaderprecache` files. The game rebuilds them at its next start, which then takes longer. The caches shipped in
  the install are never touched.

It's refused while the game runs (by process name; no process is opened), and when another game shares one of its
cache keys: on AMD an app profile or the same name hash, on NVIDIA the same exe name. Anti-cheat games get their
driver cache cleared only. Clearing also resets the first-launch judgement on AMD.
