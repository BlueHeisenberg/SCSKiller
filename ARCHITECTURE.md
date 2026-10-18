# SCSKiller architecture

SCSKiller removes shader-compilation stutter by filling the GPU driver's shader cache before a game runs. It reads the
game's shaders from its installed files, plans the pipelines the game will create, and replays them in a separate
process that the driver takes for the game. Where the files don't say enough (root signatures built at run time, AMD's
state-dependent cache, ray tracing), an optional recorder captures what the game creates while it's played, and the
next plan replays that too.

Everything vendor- or engine-specific sits behind an interface in `src/SCSKiller.Core/Contracts.cs`: a store is an
`IGameSource`, an engine an `IEngineReader`, a GPU vendor an `IGpuVendorBackend` with its `VendorCaps`. The planner and
the warmer read only the caps.

## Components

**App** (`src/SCSKiller.App/`). WinUI 3, unpackaged: library, game pages, queue, cache, tray, settings. View models
(`ViewModels.cs`) and the Velopack integration (`Updater.cs`) only. All logic is in Core, behind `IScsKiller`.

**Core** (`src/SCSKiller.Core/`). The `ScsKiller` facade (`App/ScsKiller.cs`) owns the per-game state (`AppStore`),
the compile queue and the recorder's lifecycle.

- *Scan* (`Games/`): an `IGameSource` per store, and `ManualSource` for games the user adds by their exe or that it
  finds in the user's folders of games (each folder in one is a game). `GameFiles` picks the real
  exe and is the only anti-cheat detector. `Vac` decides Valve Anti-Cheat. `ReShade` classifies shader mods.
- *Engine readers* (`Unreal/`, `Unity/`, `FromSoft/`, `ReEngine/`, `RedEngine/`, `Northlight/`, `Dagor/`, `Rage/`,
  `Source2/`, `NaughtyDog/`, `SantaMonica/`, `Nixxes/`, `Tank/`, `Decima/`, `Luminous/`, `ForzaTech/`, `BlackSpace/`, and `Carved/` for any
  game shipping raw DXBC/DXIL): each lists the build's shaders as a `ShaderIndex` grouped in shader maps (shaders that can be drawn
  together) and reads their bytes on demand. A shader is the SHA-1 of its container (`ShaderContainer.Parse`).
- *Planner* (`Planning/`): `Planner` and `PlanBuilder` turn index, recording and caps into a plan. `RootSig` rebuilds
  the root signatures engines create at run time, from `plan-rules.json` (`RulesFile`). `UnitCover` picks the fewest
  pipelines that cover every per-stage cache unit, `RtCollections` synthesizes ray tracing collections, and
  `Middleware` covers pipelines embedded in DLLs such as FidelityFX and XeSS. Static samplers follow the texture
  filtering setting (`SamplerVariants`): the rule is checked with each record's own samplers, records differing only
  there count once, and the community's records at other settings than this PC's recording's are left out. Without
  that recording the community's setting at the player's anisotropy from the game's config is kept
  (`UnrealRhi.MaxAnisotropy`), else its most common, or every one in Maximum mode (`ScsKiller.ForeignVariants`).
- *Warm* (`Warming/`): `Warmer` runs `scskiller_warm.exe`, parses its output, retries and isolates driver crashes.
- *Vendors* (`Vendors/`): `NvidiaBackend`, `AmdBackend`, their caps, and `NvidiaAppCache` / `AmdAppCache`, which find
  and learn a game's driver cache files.
- *Recorder management* (`App/ScsKiller.cs`, `App/Recordings.cs`): install, its stamp, import, the keys file.
- *Community* (`App/Community.cs`, `App/Sharing.cs`, `Planning/HashOnly.cs`, `Planning/Rehydrate.cs`).
- *Updates and content* (`App/Updates.cs`, `App/ContentTrust.cs`, `App/Account.cs`, `App/RouteFailover.cs`).

**Recorder** (`proxy/proxy.cpp`, built as `d3d12.dll`). Forwards every export to the system DLL. Only in an admitted
launch (see [Rules](#rules)) does it hook the device and record what the game creates. As `d3d11.dll`, in a DirectX 11
game on AMD, it records input layouts. Otherwise it is a pure pass-through that writes nothing in the game folder.

**scskiller_warm.exe** (`proxy/warm.cpp`, `proxy/warm11.cpp`). Stages a copy of itself named like the game's exe, plus
the recorder DLL and the work folder's databases, in a new folder, and runs it. In that copy the DLL runs in warm mode
(only in a process exporting `SCSKiller_WarmHost`) and creates every item on a D3D12 or D3D11 device. The parent
relays progress and handles pause, stop and the heartbeat.

**Selftest** (`proxy/selftest.cpp`, `probe11.cpp`, `layout11.cpp`). Checks the recorder end to end on the real GPU and
probes driver cache behaviour. Every fact under [Driver caches](#driver-caches) has a probe. The header lists them.

**Server.** The hosted service behind the API. It holds accounts and Patreon sign-in, signs access tokens, takes
uploads into the manifest, and serves the manifest, recordings, packs, content files and gated update feeds, with rate
limits and quotas. The client side of every exchange is in Core's *Community* and *Updates and content* parts.

**CLI** (`src/SCSKiller.Cli/`). Core from a command line (`scan`, `status`, `compile`, `record`, `cache`, `task`).
`scskillerw` is its windowless copy, which the driver-update task runs as `rewarm-stale --if-driver-changed`
(`ScheduledTask`). The app runs it elevated for admin-only actions (`Elevated`).

## Main flows

### Compile

1. **Scan.** Stores list games. `GameFiles.GameExe` picks the real exe (for Unreal, the `-Shipping.exe`, never a
   launcher, and `<name>.exe` over a Ubisoft+ `<name>_Plus.exe`). An EasyAntiCheat bootstrap gives way to the
   `executable` its `EasyAntiCheat\Settings.json` names, read only, and the Unreal rule then applies to that exe. An app
   that installs several games in one folder (`Games/collections.json`) is listed as one game each, with its own exes
   and data folder. A game in a subfolder of its own (Mass Effect Legendary Edition) has that folder as its install and
   its own recorder. Engine and anti-cheat verdicts are cached in `scan.json` while exe, store build and SCSKiller build
   hold; an anti-cheat verdict also only while the marker it found is there and the install root's and exe folder's
   entries (`ScsKiller.FolderStamp`) are as they were, else the game is checked in full again. A verdict found outside a
   scan (an install, a reconcile, a pass) knows no marker path: the next scan checks the game in full. A check that ran
   out of time or failed keeps the recorder out but is never held: the 2-minute pass walks the game again while no compile
   runs, and a full check begun after it that finds none clears it.
2. **Read.** The engine reader builds the `ShaderIndex`. Archive keys come from the user or a pinned download and stay
   local.
3. **Check.** `Planner.Check` gives `Unsupported`, `NeedsRecording` or `Ready`. Without a recording, `Ready` needs a
   state-independent cache (NVIDIA) and a root-signature rule for the engine version (for a Nixxes port, for the game build:
   `RuleMatch.Games`), or records that name their root signatures (`Carved/DawnStores`). Wuthering Waves (Kuro's 4.26) has
   rules of its own (`ue-kuro`, `ue-kuro-rt`), taken only by the fork's exact names (`UnrealReader.DetectFork`).
4. **Prepare.** `recording.db`, merged with any community download, gets the shaders it names by hash read back from
   the install (`Rehydrate.Run`).
5. **Plan.** `Planner.Build` writes `plan.bin`, stamped with the planner version and the matching rules' hash.
6. **Materialize.** `Planner.Materialize` copies the plan's shader bytes from the install into `work\`. Items naming a
   shader the install lacks are skipped.
7. **Warm.** `scskiller_warm.exe` replays `work\` under the game's exe name, then `work\` is deleted. A complete warm
   writes `scskiller.warmed`. One the driver rejected more than `PartlyCompiledShare` (10%) of is partly compiled
   (`ScsKiller.IsPartlyCompiled`). The ring is one measure in every state (`ScsKiller.ShownPercent`): stage-set coverage
   times the plan's items in the cache, over the plan's items plus pipelines recorded since. A compile
   that throws keeps its stage and exception in `compile-error.log`. The last warm's logs stay in `warm-last.log`. A
   complete warm adds what it created to `compiled.keys`, and a later one leaves those items out of `work\` unless
   `ScsKiller.FullReplayReason` says the cache may not hold them (another driver, a changed game or exe, a launch that
   still compiled, cache files gone, made again or shorter).
8. **Track.** The warm's inputs go into `warm-<hash>.keys`. `ScsKiller.PendingOf` counts what's new since. A driver or
   game update, a new planner or new rules make the game `Stale`, and the idle re-warm picks it up. Plans a new planner
   or new rules change are rebuilt as plan checks (`QueueItem.PlanCheck`), one at a time at below-normal priority: a new
   planner's right after the scan, not while a game is played, new rules' while the PC is idle.

### Recorder

1. **Install** (`ReconcileRecorders`) into compatible games only: supported, no anti-cheat, not VAC, writable without
   elevation, not running, and on NVIDIA not a game whose first `d3d12.dll` comes with Streamline
   (`GameFiles.StreamlineFirst`). A foreign `d3d12.dll` blocks it unless the user chains it (renamed to
   `d3d12.scskiller-next.dll`, loaded via `next=` in `scskiller.ini`). A proxy deleted while chained (the mod's own
   uninstall) leaves the renamed file unused and turns recording off for the game: never reinstalled around, never put back.
   On AMD, where the exe has AGS make its device (`GameFiles.AgsMakesTheDevice`: it imports
   `agsDriverExtensionsDX12_CreateDevice` and its `amd_ags_x64.dll` is a build in `GameFiles.WrappableAgs`), the recorder
   goes in as `amd_ags_x64.dll` instead (`scskiller_ags.dll`, `SCSK_AGS_WRAP`), in front of the game's own AGS renamed
   `amd_ags_real.dll`, which every removal renames back.
2. **Stamp** after a clean full anti-cheat check (`ScsKiller.Attest`). A build without an index is indexed
   first in the background (`ScsKiller.IndexForRecorder`), and a launch before that ends records every shader's bytes.
   The index runs below normal priority, never while a compile runs: a compile starting stops it, and it reads again
   once the queue is done.
   A collection's folder has one recorder, the first game's, stamped for the exe of whichever of its games ran last
   (`ScsKiller.ArmTarget`); its imports are split into each game's recording by the shaders its build ships.
3. **Admit** once, at the game's first device.
4. **Capture** pipelines, root signatures, ray tracing state objects and additions, the NVAPI state of each create,
   and under a mod layer both what the game asked for and what the driver got. Shipped shaders are recorded by hash
   (the keys file lists them), so only run-time and mod shaders carry bytes.
5. **Import** (`Recordings`): `scskiller.db` beside the exe is an inbox, merged by record key into `recording.db`,
   then emptied.
6. **Upload** (`Sharing`, opt-in): the game's own recording in hash-only form (`HashOnly.Canonical`: no shader bytes,
   no mod-layer records), under an anonymous upload device's token.
7. **Judge** (AMD): the first launch after a complete warm decides whether the game is fully or partly warmed.

### Before and after

`ScsKiller.BeforeAfter`, state in `GameRecord.BeforeAfter`. On a recorded D3D12 or D3D11 game without anti-cheat:
clear the game's caches (the pipeline caches it writes itself too: Unreal's, and Dawn's and RE Engine's in its folder,
`OwnPipelineCaches`), the user plays, clear again, compile with that run's recording, the user plays again. A run is
the first launch stamped after the click, kept as its pinned session snapshot cut at the pair's 5 or 10 minutes of play
counted from the launch's first present, loading and pauses included (`FrameLog.Read`'s play cap: frames,
hitches and creates after that point are left out), and counts with 3 minutes of play. A
short or lost run is redone after a clear (and a compile, for the after run). A launch outside the flow after the
compile makes Continue compile again. A driver or game update in between voids the pair. No other compile runs while
the before run is still to come. Each run's launch first installs or checks the game's recorder, in and armed, and is
refused with why otherwise (`BeforeAfterRecorderReady`). `BeforeAfter.Compare` reads only the two snapshots.

- **D3D11** has no create rows, so a run whose csv launch has `#api,d3d11` compares every hitch of 50 ms or more, the
  time frozen, the worst hitch and the 1% low, with no shader split and no ready share. Stutters on
  loading screens count too.
- **NVIDIA, D3D11** (`ScsKiller.Frames11`): the `d3d11.dll` goes in only while a pair is in progress and records
  frame times only (SCSKiller's `scskiller.ini` has `frames_only=1`: no `scskiller.db`, no keys file, no input layout
  hook). It is stamped only with a pair in progress and that line, else taken out as for VAC. The game's switch Off
  keeps it out, and a `scskiller.ini` that isn't SCSKiller's refuses it. Every other recorder rule applies.
- **In-game panel** (`proxy/overlay.h`): while a run is armed or playing and the pair has it on, SCSKiller's
  `scskiller.ini` has `overlay=1`, `ab_play_s` and `ab_tag`. In the outermost Present the recorder copies a panel to the
  top right of the back buffer: the time left, a progress bar, the last 5 s of frame times with a 50 ms line, and on
  D3D12 "SHADER COMPILATION STUTTER!" for 2 s after each shader compilation stutter within the play time, by
  `FrameLog`'s rule on the creates that end in the frame (the recorder's last creates in a ring the present reads
  without a lock), so the panel and the report count the same. The text is Inter, pre-rasterized into
  `proxy/overlay_font.h` by `proxy/overlay_font.py`. Its pixels are painted on the CPU only when they change, at most
  ten times a second. A frame costs one copy: on D3D12 a command list recorded once per back buffer, one
  `ExecuteCommandLists` and one `Signal` on the swap chain's queue, never a wait; on D3D11 `CopySubresourceRegion` on
  the immediate context. No shader or pipeline, so nothing reaches the db, the csv or the driver cache. No back buffer is
  held between presents. Drawn in SDR (8-bit, 10-bit `G22_NONE_P709`), HDR10 (`G2084_NONE_P2020`, SDR white at 203
  nits) and scRGB FP16, by the colour space a `SetColorSpace1` hook tracks. With frame generation it counts the game's
  frames and draws on the displayed swap chain (FSR 3's, on its direct present queue). Under a layer (ReShade as
  `dxgi.dll`, a chained `d3d12.dll`) it draws on the system's swap chain after the layer's own drawing. Off for the
  launch after any failure (logged as `overlay:`). Play is counted live by
  `FrameLog`'s rules, the last 10 s left out as the report's quit window would be, and the app reads the frame log at
  every poll, so the two countdowns agree within a poll. The app's own countdown and sound stay as the fallback.
- **Coldness:** the before run's clear deletes the driver-cache keys SCSKiller knows for the game and records which keys
  still have files (`BeforeAfterRecord.Survivors`). Both drivers keep a running app's cache files open, so the keys the
  game holds during the run are learned as it plays. If one of them survived the clear, the game started with old files:
  the page says so at once and the before run is played again after a clear that now knows the key. A key the game never
  held wasn't read. AMD's D3D11 driver doesn't always hold its DxCache file open, so for a DirectX 11 game whose DxCache
  key isn't known the clear sets aside every DxCache file of no known game (`DxQuarantine`: renamed beside DxCache, a
  journal first) until the run ends. A key made during the run is then learned as the game's and its set-aside files
  deleted when the game's process was seen holding it, or when it is the only one made since the game started, no
  process holds it, no other game has the exe name and the game didn't run D3D12. The rest goes back (also after a
  cancel or a crash, at the next poll). Otherwise none is learned and the report says the after run may have used the
  before run's cache. Another app's key is still taken as the game's (only a regenerable driver cache) when the app
  remakes its file and lets go of it during the run while the game makes none, when the clock jumps back over 30 s
  during the run, or when the game ran D3D12 under a driver profile the hint doesn't know and sampling missed it. So a
  before run that counts is cold, and its ready pipelines are the driver's reuse of stages it compiled earlier in the
  launch.

### Community download

1. Every install fetches the manifest (append-only 80-byte records, by byte range) with no token and nothing about
   its games, and matches its games locally (`CommunityManifest`).
2. A supporter's token (`db` entitlement) downloads the build's merged recording to `community.db`. Official recordings
   (`official.db`, some open to every install) and D3D11 draws (`draws.db`) come the same way.
3. Shared middleware packs are free for every install, keyed by DLL version and GPU vendor.
4. Everything downloaded is hash-only and rehydrated from the local install. A community recording that names none of
   the installed build's shaders is kept but not used for that build (`CommunityDownload.MatchesNothingIn`).
   An index without shaders hashes to SHA-1 of nothing for every game, so nothing is found or shared by that hash
   (`ShaderIndex.EmptyContentHash`).

Compatibility reports (`CompatReport`, `Sharing.SendCompatAsync`) are sent only from the game page's Rate this compile, under
the upload device. Their public aggregate (`CompatFeed`, `compat.json`) is signed with its own feed keys, which sign
nothing else, and sets only the game page's similar-systems line.

## Rules

**Outside a session the recorder never runs inside an anti-cheat or VAC game.** Enforced in layers:

- *Detection.* `GameFiles.DetectAntiCheat` matches markers by name anywhere in the install. Links count by name and
  aren't followed, and an install it can't list whole counts as anti-cheat. An anti-cheat that leaves no file in the
  install and isn't VAC is invisible to the markers, so its games are named by exe (a marker) or store id
  (`GameFiles.AntiCheatIds`). A Steam game whose launch options, in any branch, start a marker (appinfo.vdf,
  `Vac.LaunchExes`) counts too. `Games/Vac.cs` treats a Steam game as VAC whenever Steam's data can't vouch otherwise.
  Under Wine (Proton) Steam's data is the Linux Steam's, through Wine's drives, in the folder Steam starts the game
  with, never the game's library (`SteamSource.AppInfoPath`, `appinfo_path` in the proxy).
- *One place acts.* Every verdict goes through `ScsKiller.AntiCheatFound`, which takes the recorder out, at once or
  when the game exits; its `scskiller.ini` goes at once, beside a running game too (`StripIni`). Verdicts come from the
  scan, a refresh, a game's exit, an exe or build change, and a full check of every recorder game every 2 minutes
  (`CheckRecorderGames`), the only one that sees anti-cheat an update puts deep in an install while the app runs.
- *Install* checks before the copy, after it and as its last step. Engine readers read an anti-cheat game's exe file
  only for its archive key (`UnrealKeys`, `SoulsKeys`, `DecimaKeys`), never its running process.
- *Admission* (`admitted()` in `proxy/proxy.cpp`) records only when `scskiller.ini` carries a valid stamp for the exe
  as it is now, no marker is in the exe's folder or any folder above it up to the install root (`root=`), the path has
  no `Riot Games` folder, no anti-cheat module is loaded, `vac_now` clears VAC from Steam's files, the dll loaded from
  the exe's folder, the folder isn't on the revocation list, and the ini is unchanged when it decides. The reason for a
  pass-through goes to `refused\<exe key>` in the recorder folder. A session at the user's risk is the one way past the
  marker, Riot, module and VAC checks: `risk=1` after `pid=` and `pid_time=`, inside the signed stamp, with the binding
  to this process. Then the proxy admits past what those checks find and logs it. `risk=` without a pid or with another
  value is refused (`risk= malformed`), and what a check couldn't inspect still refuses. Only the two session starts
  sign it, and a bound session without it ignores only EasyAntiCheat's own marker names. The stamp also signs `vendor=amd`
  (the AGS hook) and the in-game panel's lines, so a hand edit can't turn either on. The AGS hook goes in when AGS loads,
  on a check of the stamp without its key, and admission takes it out unless `vendor=amd` is in the ini it verified. `frames_only=1` stays unsigned: it
  only narrows what is recorded.
- *Sessions (app only):* the recorder goes into an anti-cheat game only in a session the user allowed for that game and
  confirmed for that launch. EasyAntiCheat games of `Games/offline-eac.json` run it without the anti-cheat
  (`ScsKiller.StartOfflineSession`): their exe starts directly with `steam_appid.txt` beside it. With the setting
  "Allow the recorder in games with anti-cheat (at your own risk)" on, any other detected anti-cheat or VAC game on D3D12
  whose store SCSKiller can start, its engine supported or not and no server verdict against it, runs it
  (`ScsKiller.StartRiskSession`) through its normal launch, with `risk=1` in the stamp.
  SCSKiller starts the exe suspended, then places the recorder with its stamp bound to that pid and creation time,
  recording each file's hash first (`GameRecord.OfflineSession`, `RecorderFiles`). Its cleanup deletes only files that
  still hold those bytes, when that process has exited or at the next logon after a crash. A risk session of a store
  game gets an unsigned ini first and is signed for the exe's first process once the store started it (a first device
  before that is a pass-through), and waits on every process of the install tree. One session per confirmation: a
  crashed session doesn't relaunch. No pass takes out or re-signs a live session's files. `ReconcileRecorders`,
  `RecorderEffective` and "Record in all compatible games" never place the recorder in an anti-cheat game, and turning
  the setting off clears each such game's switch and removes a leftover session's files. Any other launch is a
  pass-through.

**The stamp.** `scskiller.ini`'s `exe_size=` and `exe_time=` are taken before a full anti-cheat check and written,
with `sig=`, only when it is clean and no finding or removal came since (`ScsKiller.RemovalGen`). `sig=` is an
HMAC-SHA1 under `recorder.key` of the exe's file key (its file identity, the same through links and the WindowsApps
path; `proxy/recorder_key.h`), the stamp, `root=` and the identity of the folder it names from the exe folder's
final path, so an ini copied, edited or left beside another exe is refused, and a launch through a link checks the real
folders. Every removal takes SCSKiller's ini out first; one it can't take out or can't read yet, or a user's that carries a
`sig=`, puts its folder's key on `recorder.revoked`. The installed copy's recorder folder
is `%LOCALAPPDATA%\SCSKiller`, another copy's its data folder (**Copies**). The key is readable by the user's own programs, which
are out of the threat model. Every start deletes 1.2's ledger (`armed\`) first, so 1.2 proxies are pass-throughs.
The reconcile then updates each 1.2 proxy and stamps its ini. Whose a folder's ini is lives in `recorder-inis.json`
(`IniOwner`, by the folder's full path, not in any game's record): it is SCSKiller's while that entry tracks its
bytes by hash (changed since, or over 64 KB, it is the user's until SCSKiller writes its own there), or, untracked (1.2
tracked only the dll when it found one there; the first start moves a record's ini hash into its folder's entry), when
its bytes are exactly what the one writer (`ScsKiller.IniOf`) gives for the values in them (`OurIniBytes`). Any other
ini, or one that can't be read, is never written or deleted, and that recorder records nothing. Every write or delete
reads the ini again right before it. An ini still to be taken out keeps its folder on its game's record, and the next
removal through that record takes it out (an ini store that can't be read or saved leaves the removal pending on the
record too). A d3d12.dll or d3d11.dll proven ours by its export goes from every folder a record names and the game's
own, as do files with their recorded hash and SCSKiller's ini by ownership; a folder that can't be cleaned never keeps
the others in.

**Copies.** The installed, a portable and a test copy (`SCSKILLER_DATA_DIR`) each have their own data folder, which holds
their own `recorder.key`, `recorder.revoked` and `refused\`. A copy that isn't the installed one writes `scskiller.ledger` (its data folder's `armed\`
path, UTF-8, the marker earlier builds with a ledger write too) beside each recorder it places, first; the proxy reads its
recorder folder from it (none: the installed copy's, unreadable: it refuses). A recorder
belongs to the copy it names, an unmarked one to the installed copy; any other copy never adopts, stamps, updates,
removes or imports it or its files (`ScsKiller.Foreign`), except to take it out of an anti-cheat game. Its game's switch
shows off there, even when the copy's record (a data folder copied from another's) says it placed it. The one exception is
a 1.2.5 portable's unmarked recorder: its record takes it back while every file there is the bytes the record names and no
record in the installed copy's data folder names that folder or fails to read (`ScsKiller.NamedByInstalled`). The user turning
it on in another copy takes it over: the marker is rewritten (by the installed copy, deleted), and its ini tracked as this
copy's. The marker goes last, with the recorder's data files, and stays while an ini removal or a rollback is pending
there. A collection's folder (`Games/collections.json`) has one recorder, stamped for the exe
of whichever of its games ran last (`ScsKiller.CollectionArmExe`); a launch of its other exe is refused, and its page
says the stamp moved to it.

**Game folders.** Nothing is written there but the recorder's files (and an offline session's `steam_appid.txt`), and
only while the game isn't running (`GameFolderWrite`). Clear cache with the game's own caches also deletes the
pipeline caches the game writes there and rebuilds (`OwnPipelineCaches`), never a file it ships. Running is judged
from the system's process list: no game process is ever opened. Removal deletes exactly what was installed, checked by
hash, and renames a chained mod back. A Steam game uninstalled with the recorder in its folder gets the same removal
(`ScsKiller.CleanUninstalled`) once two scans at least an hour apart found its appmanifest gone, nothing of it
downloading and its exe missing, on a fixed drive and with no link on the way; then the folders that are left empty
go, up to the game's own install folder. Readers open game files read-only.

**Game content stays on the PC.** Game shader bytes are kept only in `work\` (deleted after each warm) and, for
shaders no game file holds, in the recording. User-given keys stay local. Everything shared is hash-only.

**A warm and its game never run together.** NVIDIA gives a second process of the same name its own cache files, so
the queue holds a warm while the game runs and stops a running one gracefully. A warm is never killed: the driver might
not write its cache.

**Updates and signing.**

- Updates are Velopack packages. `releases.<channel>.json` carries their SHA-256 and is Ed25519-signed (`.sig`) with an
  offline release key. The app pins the public keys and refuses a feed that doesn't verify or is older than one it
  accepted (`FeedTrust`). A downloaded update is checked again before it applies (`StagedUpdate`), and never while a
  compile holds `Local\SCSKiller.Busy`.
- `plan-rules.json` and `news.json` from the server are signed with separate keys (`ContentTrust`). Server rules that
  shrink a plan below `Planner.MinRulesShare` lose to the embedded ones.
- Stable releases are Authenticode-signed. Codecs, the AMD AGS DLL and downloaded archive keys are pinned by hash or
  commit.
- Access tokens are PASETO `v4.public`, signed by the server with a key only it holds and verified with public keys.

## Data locations

| Where | What |
|---|---|
| `%LOCALAPPDATA%\SCSKiller\` | `settings.json`, scan caches (`discovered.json`, `scan.json`, `games.json`, `middleware.json`, `reshade.json`, `manual-games.json`), the user's game folders, removed and renamed games (`added-library.json`), the Library's sort (`library-view.json`), `recorders.log`, whose each game folder's `scskiller.ini` is (`recorder-inis.json`), the app's log (`app.log`, up to 1 MB, then `app.log.1`), `auth.dat`, `upload.dat`, `update-staged.json`, `codecs\` |
| `...\games\<id>\` | `state.json` (`GameRecord`), `plan.bin`, `plan-*.keys`, `warm-*.keys`, `index.*`, `recording.db`, `community.db`, `official.db`, `draws.db`, `work\`, warm logs, `compile-error.log`, `scskiller.warmed`, `compiled.keys`, `stopped-scskiller_failed.keys`, keys for the game (`aes.key`, `archive.keys`, `pak.modulus`) |
| `...\games\<id>\sessions\` | `<launch unix ms>.json`: a launch's frame report with its driver, build and swap chain (`Snapshots`, `"v": 1`). The newest 20 are kept, plus a before and after pair's two pinned ones. Removing an added game deletes them, Clear recording doesn't |
| `...\packs\`, `...\community\` | Middleware packs, the community manifest and shared packs |
| `...\recorder.key`, `recorder.revoked`, `refused\` | The recorder's signing key, revocation list and last refusals (each copy's in its own data folder) |
| Beside the game's exe | `d3d12.dll` or `d3d11.dll`, `scskiller.ini`, `scskiller.keys`, `scskiller.warmed`, `scskiller.db`, `scskiller.log`, `scskiller_creates.csv`, `scskiller_frames.bin`, `scskiller.ledger` (a portable or test copy's), a chained mod |
| `%LOCALAPPDATA%\SCSKiller.App\` | The Velopack install (`current\`, `native\`, `cli\`). Uninstall deletes it whole, so no data lives there |

## Driver caches

`VendorCaps` holds these per vendor. Intel is experimental, used only with `Settings.ExperimentalIntel`, and other
vendors are unsupported. Vulkan is unsupported: NVIDIA keys its
Vulkan cache on the exe name, AMD on the full path. `%LOCALAPPDATA%\D3DSCache` is the Windows runtime's cache, one
folder per exe path, not a driver cache.

The Cache page (`ScsKiller.CacheInventory`) puts each driver cache file in one item, reading names, sizes and write
times only. A learned key goes to its game, an installed one before an uninstalled game's record. On AMD the exe name's
hash counts as a learned key. An NVIDIA file last written before the active driver's DriverStore folder was created, or
before the earliest compile under that driver, is an older driver's. A key only an earlier compile opened, and the D3DSCache folders of staged compile exes, are earlier
compiles'. The other vendor's folders are listed whole. Keys nobody learned can't be named. Links below a cache folder
aren't followed.

A delete from the page (`ScsKiller.DeleteCaches`) takes only the files as the page read them. Nothing goes while a
compile runs in any SCSKiller process, and no compile starts while a delete runs (`ScsKiller.DeletingName`). An item
whose game runs is skipped, and the game's compile lock is held while its files go. Owners are worked out again at
delete time: on NVIDIA a game whose exe has the same file name shares the key. Files go in units, one app key's or one
folder's, whole or not at all: a unit with a file that changed, is in use, sits outside the cache folders, is reached
through a link below one, has a link among its files, or gained a file since the read is skipped and reported. A cache
folder that is itself a link (moved to another drive) is followed. Deleting a game's item resets its compile, as Clear
cache does. A cache folder that is a link whose target is missing reads as empty, marks nothing trimmed and holds
compiles.

### NVIDIA, D3D12

- **Key:** the exe file name, case-insensitive (`CacheKeyedByExeName`). A packaged (Xbox) game is keyed on its package
  identity (`PackageKeyed`), so its warm runs with it (`--package`). The NVAPI shader-extension slot is part of the key
  and is recorded (`'N'`) and replayed. So are NVAPI's pipeline creation flags, which are global and used only by ray
  tracing state objects: each state object takes the flags last set by any thread, and the warm never creates two at once
  under different flags.
- **Granularity:** per stage, on shader and whole root signature only (`StateIndependentCache`, `PerStageCache`).
  Fixed-function state, formats and input layout aren't in the key, so synthesized state hits. View instancing isn't
  synthesized.
- **Files:** `NVIDIA\DXCache\*.nvph`, named by an underivable hash of the exe name and a type that changes with the
  driver, matched on the hash alone, learned from the files the driver holds open (`NvidiaAppCache`). A driver update
  deletes older D3D12 files. Files are pre-sized, not sparse. SCSKiller reads only their names, sizes and which
  processes hold them, never their contents. A game whose 4 GiB file was 94-97% full lost hits on its own entries.
- **Unreached:** NVIDIA Aftermath with shader debug info makes the driver compile every pipeline under another key
  (GenerateShaderDebugInfo with crash dumps on, not its other flags or DRED's device flags: `selftest amwarm`). The warm
  starts Aftermath through the game's own DLL as the game does (`--aftermath`, `NvAftermath.Of`): as the recorder logged
  it, else the plan rules' entry for the game (`nv.aftermath.v1`), else Unreal's call for `EngineInfo.AftermathShaderDebug`.
  A warm process that fails or dies starting it, before its first item, makes the warm start over once without it
  (`WarmResult.AftermathDropped`), and a stopped compile resumes only in the same mode (`GameRecord.ResumeAftermath`).
  A warm that didn't while the game does is Stale (`GameRecord.WarmedAftermath`), and an unreached verdict on it doesn't
  apply. The first launch after a warm is judged, and enough of the warm's pipelines still compiling cold mark the game
  (`GameRecord.UnreachedWarm`, "the compile didn't help this game on NVIDIA") until a later warm completes. The scan's
  `EngineInfo.AftermathShaderDebug` alone doesn't change the status. `Unsupported` and partly compiled come first.
- The D3D12 runtime version isn't in the key. The warm runs on the game's Agility SDK when it ships one (`--d3d12`).
- RayQuery compute PSOs are never full hits (`SessionLog.RayQueryFloorMs`).

### AMD, D3D12

- **Key:** FNV-1a-32 of the exe name's UTF-16LE bytes, case-sensitive (`AmdAppCache.DxcAppHash`), files
  `AMD\DxcCache\<app>.*.parc`. The launched case is learned (`GameRecord.LaunchedExeName`).
- **Overrides:** driver app profiles replace the key, some only for a matching install path (`--stage-path`). A device
  created through AGS with an app name is keyed on that name, an Xbox app game's too (`AmdAppCache.AgsKey`, `--ags`
  where `ScsKiller.AgsFor` has proof: AGS detected, not assumed from an unreadable exe). Unreal's app name is its
  project's (`AmdAgs.Of`); another engine's is the string in its exe whose hash is the key the game's own process held
  (`AmdAgs.Recovered`), so its warms register it only after the game was seen. The recorder also logs the names a game
  passes to a loaded `amd_ags_x64.dll`'s `agsDriverExtensionsDX12_CreateDevice` (the csv's `#ags` line); one whose key
  the game's process held comes first (`AmdAgs.Learned`). AGS linked into the exe isn't hooked. Unreal's project name is
  also registered once a plain warm missed a game none of whose keys is its plain key (an app-name profile not listed
  yet). App-name profiles and proven app names come from the plan rules (`amd.ags.v1`), so the server can add them. Real
  keys are learned from the files a process of that name holds open (`GameRecord.CacheKeys`). A game none of whose keys
  the last warm filled is Not compatible while the next warm would run the same way, until a launch shows it holding one
  (`ScsKiller.CantReach`).
- **Granularity:** per stage, not state-independent. A VS's input elements, topology type, root signature layout or a
  PS export shape recompile. A new VS-PS pairing, a same-shape format, MSAA or blend relinks. Rasterizer and
  depth-stencil state are free. A VS also compiles for how its PS consumes it. Field lists: `UnitPolicy`,
  `ExactLayouts`. A create under 3 ms is a hit.
- **Process-dependent:** what's kept varies with concurrency and compile order. The first launch after a warm is judged
  (`ScsKiller.PartlyWarmedShare`), and a partly warmed game offers the careful compile (`WarmPasses`).
- **Files** are written through a memory map: write times never change, sizes do.
- **Cap:** the whole DxcCache folder holds 16 GiB (`AmdAppCache.DxcCacheCap`), least recently accessed files evicted
  at device creation. Reading contents refreshes a file, so the app only lists names and sizes.
- **Kept aside:** a compiled game's own files are hard-linked into its data folder (`AmdKept`). The driver evicts only
  its name, and the link goes back before the game runs, keeping the file id, so the compile stays on record.
- **Budget:** one game's plan stays under the whole cache (`PlanBuilder.CacheBudget`, 16 GiB at 28 KiB a PSO). Past
  it, recorded PSOs are always kept, then the cover PSOs that compile a shader the first time, then the rest by
  provenance and by how many stage sets their new units serve. Maximum's pairings fill only what is left under it.

### Intel, D3D12

Measured on Intel Arc only (`selftest fields`, `fields dxil`, `dxr`, `bindless`, probe11). The caps apply to every Intel
GPU (`IntelBackend`).

- **Key:** the exe file name, path-independent (`CacheKeyedByExeName`). Case sensitivity is unmeasured: warms use the
  launched case. Files: `%USERPROFILE%\AppData\LocalLow\Intel\ShaderCache\<64 hex>`, one per exe name, not derivable
  from the name.
- **Granularity:** per stage, not state-independent (`UnitPolicy.Intel`). Free: input layout, topology, depth-stencil
  state, DSV format, sample count, and the rasterizer fields but `MultisampleEnable`. The PS recompiles for any
  render-target format or count, any blend field, `MultisampleEnable` and `SampleMask` 0, so a PS unit keys on the exact
  formats and blend desc (`ExactLayouts.ExactShape`). `MultisampleEnable` and `SampleMask` aren't in it. The VS compiles
  for what follows it and how its PS reads it, as on AMD. A root signature layout change recompiles both, DENY flags included.
  Heap-indexed and RayQuery compute PSOs are full hits across processes.
- **Cap:** 512 MiB per exe name (`IntelBackend.PerGameCap`), past which the driver evicts that game's own entries. Not
  configurable. It is the plan budget (`PlanBuilder.CacheBudgetOf`), with AMD's 28 KiB a PSO.

### D3D11

Keyed on the exe name on every vendor, cached per shader, compiled at first use. `Planner.D3D11Stages` picks the
stages a warm covers.

- **NVIDIA, Intel:** state doesn't matter, every stage is warmed. On Intel a state change costs under 1 ms.
- **AMD:** a VS recompiles per read input element and topology class, so one with vertex inputs is warmed only from
  recorded layouts (`'D'`, `Draws11`). GS and HS compile with their VS and aren't warmed. PS and CS are warmed behind a
  generated VS.
- **DXVK:** on NVIDIA the warm runs through the game's DXVK d3d11.dll (`--d3d11`): its Vulkan cache is keyed on the exe
  name too, and a warmed VS+PS draws first in 3 ms against 30 ms cold. AMD keys it on the full path: not warmed.

### Ray tracing

Every vendor caches state objects per exe name and hits on an exact repeat (`VendorCaps.RtCacheGranularity`).

- **NVIDIA (`Collection`):** collections are cached on their own and link cheaply, so the planner synthesizes one per
  DXIL library. Export names aren't in the key.
- **AMD (`WholeObject`):** the whole linked object is the key. Only recorded objects can be warmed.
- **Intel (`WholeObject`):** collections are cached on their own, but flat state objects share nothing with them and
  `AddToStateObject` doesn't use them, so only recorded objects are warmed.

### NVIDIA under Wine (Proton, Linux)

A D3D12 game under Proton runs on vkd3d-proton, which builds a Vulkan pipeline per PSO; NVIDIA's Linux driver caches it.

- **Key:** the process image's file name, Wine's `wine64-preloader` for every Windows process of every game, and a number
  per process holding the cache: a process gets the next file. `__GL_SHADER_DISK_CACHE_APP_NAME` replaces the name,
  `..._READ_ONLY_APP_NAME` (`;`-separated file names) adds files the process only reads, `..._PATH` moves the folder
  (which must exist) and `..._SIZE` caps it: past it the driver deletes whole files. The driver reads them from the Linux
  environment Wine starts with; a variable a Windows process sets doesn't reach a process it starts.
- **Granularity:** per stage, not state-independent: blend enable recompiles a fragment shader; formats and depth format
  mostly don't. Compute pipelines from the files are hits.
- **vkd3d-proton's own cache** holds the DXIL->SPIR-V translation: `vkd3d-proton.<exe name>.cache` under
  `VKD3D_SHADER_CACHE_PATH`. Its SPIR-V depends on its build, so a warm runs on the game's Proton.
- **DXGI is DXVK's**, its driver version DXVK's own: under Wine `GpuBackends` takes the host driver's from Vulkan.
- The Proton wrapper script runs the CLI in the game's prefix on its Proton and starts compile's Wine session
  with `proton-env --warm`'s variables: the game's `proton-cache\` folder beside `work\`, an app name of the warm's own.
  The game's launch takes `proton-env`'s: the same folders, its own app name, and every file of the warm's read only
  (`Wine.WarmFiles`: the warm's number depends on what else of Wine holds the cache). For a Steam game it takes the
  prefix and Proton from Steam's files, and the launch options' variables replace the NVIDIA cache ones Steam sets.
  The CLI gets the active Steam client's folder for Steam's data (`steam-client`: the running client's, else the one
  client folder present of `~/.steam/root`, Flatpak's and Snap's), never a library folder, and none when that isn't certain.

## File formats

**Recorder database** (`scskiller.db`, `recording.db`, `scskiller_gen.db`, packs). Records of `u8 tag, u32 length,
payload`, keyed by SHA-1 of tag and payload. Written by the recorder and the planner, replayed by the warm. Tags: the
header of `proxy/proxy.cpp`, state objects at `write_so`. Reader: `PsoDb`.

**Plan file** (`plan.bin`). Templates, generated root signatures and items naming shaders by SHA-1, never shader bytes.
Written by `Planner.Build`, read at materialize and by the new-pipeline count. Type: `PlanFile` (`Planning/Planner.cs`).

**Keys file** (`scskiller.keys`). Written by `Recordings` beside the recorder: hashes already recorded and shaders the
build ships, so the recorder skips the first and records the second by hash.

**Warmed file** (`scskiller.warmed`). Written by `Recordings.Warmed` after a complete compile, kept in the game's data
folder and copied beside the keys file: the sorted keys of the pipelines and state objects the compile created, then
the sorted (stages + root signature) tuple hashes, less what the warm listed in `scskiller_failed.keys`.

### scskiller_warm.exe protocol

Options: the header of `proxy/warm.cpp`. Stdout is JSON lines only (`stage`, `start`, `progress`, `done`, `retry`,
`error`), parsed by `WarmEvent` in `Warming/Warmer.cs`. The items it didn't create (failed, abandoned or skipped as a
crash) go by record key to `scskiller_failed.keys`. Exit 0 is completed or stopped, 1 failure, 3 retry. Replay
follows file order, so a stopped run resumes with `--start <done>`. Stop is a named event, never a kill. Memory budget:
`SCSKiller_WarmMemory` in `proxy/proxy.cpp`.

### scskiller_creates.csv

Written by the recorder beside the exe, one row per create
(`t_ms,kind,known,tuple_known,ms,key,proxy_ms,tid,presents`), with `#session`, `#clock` and `#end` markers per launch,
`#api,d3d11` from the recorder as `d3d11.dll`, `#swapchain,<w>,<h>,<DXGI_FORMAT>` per swap chain, `#overlay,1` once
the in-game panel drew and `#framegen,1` once a frame generation module (DLSS-G's `nvngx_dlssg.dll`, XeSS's) loaded
or frame generation made its swap chain, then `#frames,stopped` when no hook times the game's frames after it.
`#session` has the exe name as launched, the case AMD keys on. With a `scskiller.warmed`, `known` and `tuple_known`
mean the last compile created that key or tuple; without one, `known` is a key the db or keys file held at start. Read
by `SessionLog`.

**Frames file** (`scskiller_frames.bin`). The last launch's frame times as `u32` records on the csv's clock. Format at
the frame writer in `proxy/proxy.cpp`. Read by `FrameLog`, which tells shader stutter from loading and other hitches.
**Tagged records** sit in the gap values the writer never uses: top 4 bits 15 with the low 28 bits under 268,435
(`(1 << 28) / 1000`, `FrameLog.TagEnd`), since a gap is written only for 2^28 µs or more. A tag is one `u32`; a reader
without tags takes it for a gap of that many milliseconds. Tags 0-14 (`0xF0000000`-`0xF000000E`) mark that swap
chain as frame generation's, its presents the frames the game renders; every other swap chain's are the displayed
ones. Tags 15-268,434 (`0xF000000F`-`0xF0041892`) are free for new record types, lowest first (a small tag keeps an
old reader's shift small); readers skip tags they don't know, and a record that needs data encodes it in further tags.

## Where details live

- Code first: the names above lead to the rules, and their comments hold the reasons and thresholds. `Contracts.cs`
  is the map of the interfaces.
- The client side of each server exchange: `App/Community.cs`, `App/Sharing.cs`,
  `App/Account.cs`, `App/Updates.cs` and `App/ContentTrust.cs`.
- Signed feeds and content files: `tools/release-sign` (`verify`, `verify-content`). Code signing:
  [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md).
- Building and testing: [CONTRIBUTING.md](CONTRIBUTING.md). Reporting a vulnerability: [SECURITY.md](SECURITY.md).
