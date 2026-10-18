# Changelog

All notable changes to the SCSKiller app and command line. Versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.3.0] - 2026-10-17

**This is a very big update**, with everything from the 1.3.0 alphas and betas.

- **After this update**, SCSKiller checks every compiled game again, which can take several minutes with many games. The app is
  slow until that ends. Later starts are fast.

### From 1.3.0-alpha.1

**New**

- **Official recordings**: SCSKiller's own recordings of a game version, marked "Official recording" on the game page.
  Each is for backers, for every Patreon supporter, or for everyone without signing in.
- **DirectX 11 games compile on AMD**. Their vertex shaders need a recording, so on AMD the recorder now records DirectX
  11 games too, and the community database shares what it learns.
- **Engine support updates from the server**: the rules that decide what a game compiles can be fixed or extended
  between releases. Only rules SCSKiller signed are used, and a change rebuilds only the games it covers, while the PC
  is idle.
- **Riot Client**: its games are found (VALORANT, League of Legends, Legends of Runeterra, 2XKO). Riot Client
  games contain anti-cheat, and SCSKiller can't start them, so they have no recording session.
- **Naughty Dog games** (The Last of Us Part I, UNCHARTED 4: A Thief's End, UNCHARTED: The Lost Legacy): SCSKiller
  reads their shaders. They compile after a recording. The two UNCHARTED games of the Legacy of Thieves Collection show
  as two games, each compiled for its own exe. Listed as one, the collection compiled for an exe that never runs, so
  the driver cache it filled was one neither game reads. One recorder, on UNCHARTED 4's page, records both.
- **God of War Ragnarök** compiles without a recording on NVIDIA. **God of War (2018)** compiles its DirectX 11 shaders
  without a recording (on AMD, all but the vertex shaders).
- **Ghost of Tsushima DIRECTOR'S CUT** and **Ratchet & Clank: Rift Apart** compile without a recording on NVIDIA, and
  after a recording on AMD. **HELLDIVERS 2**'s shaders are read too, but GameGuard keeps the recorder out, so it can't
  compile yet.
- **Horizon Zero Dawn Complete Edition** and **DEATH STRANDING DIRECTOR'S CUT**: SCSKiller reads their shaders. They
  compile after a recording.
- **Overwatch (Steam)** compiles its DirectX 11 shaders without a recording (on AMD, the pixel and compute shaders).
  Its Steam install counts as an anti-cheat game, so the recorder stays out (thanks @JohnsonRan).
- **Source 2 games** like Counter-Strike 2 compile their DirectX 11 shaders without a recording (on AMD, all but the
  vertex shaders). Based on the notes in issue #13.
- **Notifications** under a bell at the top: news, notes on new rules and links, only ones SCSKiller signed.
- **"What's new"** lists a version's changes once after an update. About shows it again.
- **Max compile** on a game's page compiles every combination of that game's shaders, once. It takes longer and uses
  more disk space, but potentially fewer shaders compile while you play, which seems to help most on AMD. Nothing is
  kept for the game: its next Compile is a standard one. It replaces Compile everything.
- **Maximum mode suggested per game**: a game whose Maximum mode is known to help on your GPU says so on its page, with
  "Turn on for this game". It turns Maximum on for that game only. On AMD, Maximum adds at most as many pairings as the
  game's plan has, and at most about 2 GB of the shared cache. Maximum stays off by default.
- **AMD cache use**: Settings and the Cache page show the DirectX 12 cache against the driver's fixed 16 GB, and say when
  it's nearly full. A game's Maximum suggestion says how much it would add at most.
- **Cache page**: every shader cache on the PC with its size, by installed game, uninstalled game, old driver, earlier
  SCSKiller compiles and other GPU vendor, and the folder the driver reads and writes it in. Delete an item, a group or
  everything there. After a driver update it offers to free what the new driver no longer reads.
- A game marked not supported after it was compiled offers to clear the cache compiled for it.
- A driver cache folder moved to another drive with a junction is followed: Settings shows that drive's free space, and
  a link whose drive is missing holds compiles instead of marking games as cleared by the driver.
- **Library sorting and filters**: the Game, Pipelines and Cache headers sort each section, and a status filter shows
  only games that aren't supported, are ready to compile, are compiled, or were compiled by SCSKiller.
- **Game folders**: add a folder of games no store lists, and SCSKiller finds each game in it at every scan. Games added
  by hand or found in a folder can be renamed.
- **Scan progress** shows per store and per game, and a big library shows its last list at once while the scan runs.
- **New shaders compile on their own** if you choose: when a compiled game gains pipelines from its recording, the
  community database or an update, SCSKiller compiles them while the PC is idle, never while you play. Set it in
  Settings, or per game on its page. The default still only notifies you.
- **Theme**: Light, Dark or the Windows setting, in Settings.
- **The tray icon's tooltip** shows SCSKiller's status and the compile's progress.
- **"Share diagnostics to help fix problems"** in Settings (off by default): when a compile fails or the driver rejects
  pipelines, SCSKiller sends error codes and pipeline hashes, nothing else. "What's shared" in Settings lists it all.
  SCSKiller asks once, in the welcome or at the first start after the update.
- **Shader hash sharing** also sends one report after a compile: which pipelines still compiled at the next launch, with
  your GPU model and driver.
- **Compile progress on the game page**: while a game compiles, its page shows the queue's progress and the time left.
  While it waits, it shows its place in the queue.
- **Edit the compile queue in bulk**: select several games with Ctrl+click or Shift+click, then Remove selected. Clear
  queue removes every game but the one compiling.
- **Pick the DirectX** of a game when SCSKiller only guessed it: Auto, DirectX 11 or DirectX 12 next to its engine on the
  game page. Its plan is rebuilt for the pick. Your recording of the game wins over a guess, and over the pick. Unity games
  built for both count as a guess now, and a change of DirectX always rebuilds the plan.
- **GPU picker** in Settings, on a PC with more than one NVIDIA or AMD GPU: SCSKiller compiles for the one you pick
  instead of the one with the most video memory. It switches after a restart.
- **Earlier sessions** on a game's page: the last 20 launches with frame times, each with its date, play length, 1%
  low and stutters.
- **Send report**: once a game was compiled and played, or couldn't be compiled, its page can send an anonymous
  report of how it ran. Players with a similar system then see what others reported on the game's page.

**Faster and steadier**

- **A new community recording** for a game you compiled rebuilds its plan while the PC is idle, and the game shows how
  many more pipelines it can compile.
- A game whose engine SCSKiller reads better says "SCSKiller can compile more" instead of "Game shaders changed".
- When new rules more than double a game's plan, the compile after a driver update leaves it for you to start.
- **A recompile compiles only what isn't compiled yet**, so adding a recording or turning on Maximum mode is quicker. It
  compiles everything again after a driver update, when the driver's cache lost files, or when the game still compiled
  after the last run.
- On NVIDIA, a game no longer asks to compile again after each session for pipelines whose shaders its last compile
  already compiled.
- On NVIDIA, compiled ray tracing pipelines that a game creates on other threads (The Witcher 3) are found in the cache
  at launch instead of compiling again.
- The Library holds still: a game that changes is updated in its row, the list keeps its place when a game moves to
  another section, and the scan's status line no longer pushes the list down.
- **Less memory with big community recordings**: a download is checked as it decodes and kept compressed, and a
  compile no longer holds every recorded shader in memory. With a 250,000-record recording, the download takes about
  45 MB instead of 1.1 GB and the compile's peak about half.
- **Bigger community recordings on NVIDIA**: up to 500,000 pipelines per game, once the community database serves
  them. AMD keeps reading up to 250,000.
- **Building a plan can no longer run forever or take all the memory.** A plan that outgrows half the PC's RAM or 30
  minutes stops with a clear error, and the next compile plans again. Building the plan of a game with many shader
  layouts is much faster.
- With "Closes to System Tray" on, closing the window no longer sometimes quits SCSKiller.
- **Updates**: after a failed download, SCSKiller waits longer before the next try (1 hour, then up to 12). "Check for
  updates" still tries at once.
- A **partly compiled** game's "Last compile" card says what the refused pipelines mean and what helps, and the
  coverage ring of a compiled game no longer counts the pipelines the driver refused.
- While a compile runs, the queue and the game page say "memory limit: 4 of 32 threads" when the compile's memory limit
  runs fewer threads than set.
- **Pipelines that crash the GPU driver** are all found and skipped in one compile (The Witcher 3 on AMD). Before, a
  compile stopped after 20 driver crashes, and a game with many of them needed several clicks on Compile. A compile
  that still stops says how many it skipped, and compiling again continues where it stopped.
- **Pipelines that make the GPU driver fault** (PRAGMATA on AMD) are found and skipped the same way. Before, a compile
  skipped them only for that run and stopped after three, so every later compile met them again.
- The "Last compile" card says "Nothing failed." also when it skipped pipelines whose shaders aren't in this version
  of the game.
- **Start with Windows**: a portable or test copy of SCSKiller no longer removes the installed copy's autostart, or
  moves it to itself, when it starts. A copy changes another copy's entry only when you turn the setting on in it.
- **NVIDIA Auto Shader Compilation** in Settings: Apply changes only what differs. With the feature already on it says
  so, with no administrator prompt, instead of failing to set up NVIDIA's task again. An error says which step failed
  and whether the setting was saved.
- **app.log** in SCSKiller's data folder keeps what the app did (scans, community sync, compiles and the recorder) for
  a bug report. Your user name and profile folder are masked, and it takes at most 2 MB.

**Games**

- **GTA V Enhanced** compiles on NVIDIA without a recording. BattlEye still keeps the recorder out.
- **Resident Evil Village** and **PRAGMATA** compile on NVIDIA without a recording.
- **Dragon's Dogma 2**: SCSKiller reads its game files, which it refused before.
- **Games that build all their shaders at the main menu** (Horizon Forbidden West, Detroit: Become Human) show as not
  supported, with that reason.
- **Encrypted Unreal games with anti-cheat** (VALORANT and others) get their key from the exe file on disk, so
  SCSKiller can read their files. Reading the key never touches the running game.
- **Encrypted Unreal games** take their correct AES key where it was refused or opened only some files: games with
  their own decryption (Dead by Daylight and other forks), files under a named key, and readable file lists whose files
  are encrypted. The key is also found in exes built for AVX. When the exe is protected (SteamStub, Themida, VMProtect,
  Enigma, or Denuvo), the game page says so instead of only asking for the key.
- **Cyberpunk 2077** shows its engine as REDengine 4, and Alan Wake 2 and CONTROL Resonant as Northlight 2.
- **Cyberpunk 2077 on Steam** is found with its own exe, the one its launcher configuration names, not REDprelauncher.exe.
  The recorder goes beside the game, and the game no longer counts as closed once the launcher exits.
- **Returnal** no longer compiles tessellated pipelines the driver rejects.
- Games whose shaders carry their own root signatures (Naughty Dog, RAGE, FromSoftware and others SCSKiller reads
  directly) no longer compile pipelines the driver rejects, where a shader uses resources the root signature lacks.
- **The Last of Us Part I**: a compute shader the game ships with several root signatures compiles once for each of
  them, instead of only the first.
- **War Thunder** is on the known shader-stutter list, and no longer stays on "Needs rebuilding" when nothing new comes
  of it. On DirectX 12 it also compiles its shadow and depth passes.
- **ELDEN RING NIGHTREIGN** with its Digital Artbook & Mini Soundtrack installed no longer shows as a DirectX 11 Unity
  game. SCSKiller took the artbook's app for the game. It now follows the exe the game's EasyAntiCheat settings start.
- Unreal Engine 4.25 games no longer show "not tested on this engine version yet".
- **Unreal versions**: 5.7 and 5.8 games no longer show as 5.6, and a game on a newer Unreal than SCSKiller knows says
  "Unreal 5.9 isn't supported yet" instead of compiling as 5.8. A version SCSKiller can only guess shows "(guessed)", and
  a game whose Unreal version changes rebuilds its plan.
- DirectX 11 shaders that read a multisampled texture with more samples than the GPU supports compile.
- **Games started through EasyAntiCheat's bootstrap** (Gears of War: E-Day and Sea of Thieves from the Xbox app) compile
  for the game's own exe, the one the anti-cheat's settings name, instead of the bootstrap. An earlier compile shows
  "Needs rebuilding".
- A game's page says the game may still run its own shader step when it starts.
- A not supported game no longer shows its shader coverage after Clear cache.

**Mods**

- **ReShade and HDR mods** like RenoDX show as a tag in the Library and on the game page, and "Compiles through RenoDX"
  under the game's status.
- Luma mods no longer make the game page say some pipelines aren't precompiled (they change no DirectX 12 pipeline).
- When a mod loads a copy of the recorder from another folder, where it can't record, the game page says so.
- **ReShade chaining another mod** through ReShade.ini's `[PROXY] ProxyLibrary` (OptiScaler or a DLSS mod under another
  name) no longer compiles through a copy of ReShade that lacks that mod's DLL. With a RenoDX mod that changes every
  pipeline, the game page says how to keep that mod under another name and turn the proxy off. One that replaces some
  shaders compiles without ReShade.

**Recorder and anti-cheat**

- **Recording in games with anti-cheat, at your own risk.** Until now the recorder always stayed out of these games, so
  they never got a recording of their own. Settings has "Allow the recorder in games with anti-cheat (at your own
  risk)", off by default. A game that contains anti-cheat then offers "Record a session (at your own risk)" on its page,
  and each session is confirmed before it starts. Games with anti-cheat can treat the recorder as cheating and ban your
  account, and no game is known to be safe. SCSKiller removes every file it places when the game exits, or after a
  crash at your next logon.
- **Frame times with frame generation on** (FSR 3): the graph shows the frames the game rendered. Before, the recorder
  measured no frame times while frame generation was on.
- **Frame times in games with NVIDIA Streamline** (Ghost of Tsushima, Ratchet & Clank) on every GPU. Before, the
  recorder measured none in them unless DLSS frame generation was on.
- On NVIDIA, the recorder no longer makes a game crash as it closes.
- **The recorder no longer crashes a game now and then as it sets up its hooks.** It moved a few of a function's first
  instructions to make room for its jump, so a game thread paused among them could resume mid-jump and crash. It now
  replaces only the first instruction.
- **A game that makes its own crash dump or log folder** (Horizon Zero Dawn's coredumps) no longer turns the recorder
  off. Any new folder that wasn't empty turned it off until the install was checked again, so the first recorded
  session was lost. A new folder now keeps it on when it holds only data files, with no code or anti-cheat in it.
- **A portable or test copy** keeps its own recorders: each copy's armed list is in its own data folder, and a copy
  never arms, updates, removes or imports the recording of a recorder another copy placed. Before, a copy with a new
  data folder took every recorder for its own and moved the game's recording out of the game folder. Turning the
  recorder on in a copy takes it over. Recorders placed before this version belong to the installed copy.
- The recorder's log no longer holds your user name or full folder paths, so it's safer to share in a bug report.
- **Valve Anti-Cheat (VAC) games** contain anti-cheat, so the recorder stays out unless you enable it, at your own
  risk. SCSKiller isn't responsible for bans. They still compile from their files.
- **NetEase games whose anti-cheat leaves no file in the game folder** count as anti-cheat games, so the recorder stays
  out of them: the Steam installs of Once Human, Marvel Rivals, FragPunk, Sword of Justice and NARAKA: BLADEPOINT, and
  Once Human from any store by its exe.
- **ELDEN RING added by hand** gets the offline session too, when the EasyAntiCheat settings beside its exe are
  ELDEN RING's.
- **Euro Truck Simulator 2 and American Truck Simulator** count as anti-cheat games on a PC with TruckersMP.
- **Xbox app Unreal games that start through a small launcher** (Senua's Saga: Hellblade II, Avowed) record. The
  recorder moves beside the game's own exe once the game is closed.
- **A Steam game you uninstalled loses the recorder too.** Steam leaves the folder behind when it holds files it didn't
  install. A scan once the game is gone removes SCSKiller's own files from it, and the folders they leave empty. The
  game's recording is kept.

### From 1.3.0-alpha.2

**New**

- Initial experimental Linux support.
- **Command line**: `add` adds a game no store lists.
- **Leave out of Add all ready** on the game page: the game stays out of Add all ready, and its own Add button still works.
- **Remove from library** on every game's page. A game a store lists is hidden, also after a refresh, and nothing compiles
  it on its own. Its files, saves and compiled cache stay. **Show hidden** on the Library page brings it back.
- **Set AES key** on the game page of an encrypted Unreal game: paste your own key there, without opening Why? first.
- **DirectX choice** on the game page of every DirectX game, also where SCSKiller is sure which one it runs on.
- **Before and after recording** on the game page of a recorded DirectX 12 or DirectX 11 game: SCSKiller deletes the
  game's shader caches and starts it, you play 5 minutes, it deletes the caches again and compiles, and you play the
  same 5 minutes again. Both runs count from the game's first frame, loading and pauses included. A small panel in the
  game shows the time left, your frame times and each shader compilation stutter as it happens, also with frame
  generation and HDR. The report compares shader compilation stutters, pipelines ready from the cache and the worst
  stutter, with both frame-time graphs, and says whether frame generation was on. On DirectX 11 it compares every
  stutter, since SCSKiller can't tell shader compilation stutters apart there. Save or copy it as an image, or share it
  to X, Reddit or Discord. Frame times and the report stay on your PC unless you share the image.
- **Intel GPUs (experimental, contributed by nyannoying1337)**: Settings shows "Experimental: compile on Intel GPUs" on a
  PC with an Intel GPU, off by default. Only Intel Arc was tested.
- **Send report** lists what a report carries until you've sent one. Later reports leave that line out, and Settings
  still has What's shared.
- **Compile ray tracing shaders** in Settings, on by default, with a choice per game on its page. Off skips them, and
  the game builds them itself when you turn ray tracing on.

**Faster and steadier**

- **Also delete the game's own shader cache** also clears the pipeline libraries of GTA V Enhanced and HELLDIVERS 2.
- **Also delete the game's own shader cache** no longer deletes an Unreal game's user pipeline cache. It lists what the
  game created while you played, and only playing again refills it.
- **The game scan keeps going past a file it can't read**, on a failing drive or in a cloud folder. 1.2.3 to 1.2.5
  stopped with system error 0xc0000006. That game is skipped, and the log names the file.
- On NVIDIA, a compile where several shaders hang the driver at once skips them for that compile and keeps going.
- A compile that created nothing ends as Failed and says why, such as the driver rejecting every pipeline, instead
  of Done with a count.
- On Windows 10, a game whose exe is protected or packed, such as AION 2 and Fortnite, compiles with the game's own
  D3D12 runtime from its D3D12 folder. Its compile used the older runtime built into Windows 10, which rejected every
  pipeline.
- On NVIDIA, a game whose compiled shader cache files are gone, or smaller than after its compile, asks to compile
  again. When its shaders are bigger than the driver's shader cache size, it asks to raise that size instead.
- On AMD, a game whose files the driver removed because its shader cache is full no longer asks to compile again,
  which only removed another game's files in turn.
- **Keep compiled caches aside** (AMD, Settings): a compiled game's shader cache is put back after the driver's 16 GB
  limit removes it, so it doesn't stutter again.
- On NVIDIA, a plan built again after a new recording no longer offers to compile the shaders the last compile already
  compiled.
- **The coverage ring** on the game page is one measure in every state, so it no longer drops after each play, jumps
  when the game needs compiling again, or rises when the driver refuses more than a tenth of the pipelines.
- A compile run by another SCSKiller, such as a portable copy, no longer counts as playing the game.
- A portable copy made from your installed SCSKiller's data folder starts with Start with Windows, Record all games and
  automatic updates off.
- **A community recording that doesn't match the game's files** is no longer compiled, which skipped all its pipelines
  every time, and games whose shaders SCSKiller can't read no longer share or download recordings under one hash.
- A game compiles one texture filtering setting unless Maximum mode is on, so a community recording with several
  settings no longer makes its compile several times bigger.
- On Unreal games, the texture filtering setting compiled is the player's own when the game's settings files name it.
- After an app update, a compiled game stays compiled until its plan is checked, and says it can compile more only
  when the check finds more.
- **Unreal games** also compile what the game saved in its own pipeline cache in your user folder while you played,
  without the recorder, also when the game contains anti-cheat. The next compile picks up what a new play added.
- **Reading shaders** on Unreal games runs at below-normal priority and shows how many packages it has read, so games
  like Days Gone or Styx no longer look stuck there and the window stays responsive. A read that runs out of memory
  stops with a message that says so.
- A ray tracing shader that crashes or hangs the driver is skipped for that compile, along with the ones built on it,
  and the compile keeps all its threads. These count as skipped, not failed, and the next compile tries them again.
- A compile stopped after the driver reset too many times continues from where it stopped.
- **DirectX 11 games**: shaders SCSKiller can't compile yet count as skipped, no longer as refused by the driver, and
  Unity games such as Megabonk compile more of their shaders.
- When a compile pauses for a game, the shaders it was building are finished on resume instead of being skipped and
  blamed on the driver.
- **Updates never install a version older than the one running**, unless you pick Go back to stable.
- On AMD, a game that gives the driver its own name through AMD's AGS library gets its compile where it reads its
  shaders, once the recorder saw it play.
- The game page says Careful compile is only for games whose compile fails.
- On NVIDIA, the game page also says a compile didn't help when that shows after a Before and after run or after
  Clear cache.
- While a game runs, its page shows the last finished play under Last time you played, not part of the current one.
- A game that compiled shaders for its whole launch no longer shows "no stutters" and a 1% low of 0 fps on its page.
- On AMD, Before and after and Clear cache no longer delete the DirectX 11 shader cache of another game with the same exe name.

**Games**

- **RE Engine games** with an empty patch file in their folder (Resident Evil 2, often a mod manager's leftover) no
  longer show "Not supported yet".
- Games whose ray tracing is all inline, such as RE Engine games, count their shaders that trace rays inline in the
  game page details.
- **Hidden game folders are scanned**, such as a Unity game's Data folder hidden in Explorer. Their compile no longer
  skips every shader.
- **DCS World** and other games with two builds of one program (bin\DCS.exe and bin-mt\DCS.exe) can be added with
  their own folder, which was refused as "a folder of several games" (found by JLAF5150).
- **Xbox app Unreal games** built for WinGDK (Evil West) read their own DirectX setting, not the Windows one.
- **FINAL FANTASY XV WINDOWS EDITION** precompiles every DirectX 11 shader without a recording (on AMD, all but the
  vertex shaders).
- **Mass Effect Legendary Edition**, **FINAL FANTASY TACTICS - The Ivalice Chronicles** and **KINGDOM HEARTS HD 1.5+2.5
  ReMIX** on Steam list each of their games on its own. Mass Effect Legendary Edition showed only Mass Effect 3, and
  each of its games now has its own recorder (#126).
- **Forza Horizon 6** (Steam and Xbox app) compiles from its game files without a recording. Its ray tracing shaders
  need a recording. The game contains anti-cheat, so the recorder goes in only through "Record a session (at your own
  risk)".
- **Unreal games stay detected** when SCSKiller can't download a decompression codec, such as behind a proxy, firewall
  or antivirus. They showed "engine not supported yet", and now an unreadable game names the missing codec.
- A game that needs a recording and is in the community database says "in the community database for Patreon
  supporters" when you aren't signed in as one, since only they can compile from it.

**Recorder and anti-cheat**

- **PRAGMATA** gets no recorder for now, since it crashes the game while it loads. Compiling still works, and a
  recorder already in its folder is removed.
- **WARDOGS** counts as a game with anti-cheat (Elytra), so the recorder goes in only through "Record a session (at
  your own risk)". It showed "no anti-cheat" since the game moved from EasyAntiCheat to Elytra.
- **THE FINALS** counts as a game with anti-cheat by its Steam id, since Elytra installs outside its folder.
- **A Steam game whose launch options start an anti-cheat launcher** counts as a game with anti-cheat even when no
  anti-cheat file is in its folder yet.
- **A mod the recorder ran alongside** no longer comes back after its own uninstall removed it. Its renamed file is left
  unused and recording turns off for that game.
- A portable copy upgraded from 1.2.5 takes back the recorders it placed, so they record and update again.
- **A portable copy made from your installed SCSKiller's data folder** no longer takes over the recorders the installed
  copy placed. Its game page says another SCSKiller copy placed them, until you turn recording on there.
- **Frame times are recorded again in games where a mod such as ReShade replaces DirectX's dxgi.dll**, so the game
  page's frame-time graph and the Before and after panel show there. 1.2.5 lost them in those games.
- The recorder records while the game's exe is the one SCSKiller last checked for anti-cheat, and no anti-cheat is next
  to it or in the game's folders above it. A mod's files appearing in the game folder no longer stop it until the next
  check. In a game that contains anti-cheat, the recorder goes in only through its own session card, at your own risk.
  SCSKiller isn't responsible for bans. Outside a session, SCSKiller takes it out the moment it finds anti-cheat: when
  it scans, when the game exits or updates, and every 2 minutes while it runs. The `scskiller.armed` file is gone from
  game folders.
- **Forza Horizon 6** contains anti-cheat. It counts as an anti-cheat game from Steam, the Xbox app or added by hand,
  so the recorder stays out unless you enable it at your own risk. SCSKiller isn't responsible for bans.
- **A VAC game in a folder whose path is longer than 260 characters** counts as VAC, so the recorder stays out. Its VAC
  check could miss it before.

**Known issues**

- A portable copy made from another portable copy's data folder can take over the recorders that copy placed with
  1.2.5. 1.2.5 recorders don't record which copy placed them.

### From 1.3.0-beta.1

**New**

- **HDD mode** in Settings: SCSKiller reads a game's files only when you open or compile it, so scans finish on big or
  slow libraries.
- **Maximum memory for compiling** goes up to 2 GB below your PC's RAM.

**Faster and steadier**

- **Compile queue** and Start no longer add games you didn't pick.
- Games show in the library as soon as a scan finds them, and a game you add shows at once.
- **Compiles no longer stay slow after hitting the memory limit** on ray tracing games. While the PC has little free
  memory, a compile uses fewer threads.
- Games with many driver-crashing pipelines can start their compile again.
- **A compile no longer stops when the GPU driver crashes the compile process outright.** It goes on and skips only
  the pipeline that crashed.
- A compile downloads the community upscaler packs for that game first, so it no longer shows "Needs rebuilding" right
  after compiling.
- Games with too many shaders to plan compile their recorded pipelines instead of failing, and their status says
  "recorded pipelines only".
- **A compiled game only shows "Needs rebuilding" once at least 500 new pipelines are recorded**, or 1% of its plan if
  that is more. Fewer are compiled at its next compile, or quietly while the PC is idle if the game compiles new shaders
  automatically.
- **Unreal games keep their game exe** when another program from the game's folder runs during play, so a compile no
  longer goes to the wrong exe and the game no longer asks for a full rebuild after playing.
- One unreadable Steam game folder no longer hides every Steam game.
- A game no longer counts as running when another program with the same exe name runs, such as Office's ai.exe for
  Alien: Isolation.
- When NVIDIA Auto Shader Compilation can't be applied, the error says why NVIDIA's tool failed, and the app log keeps
  its full output.
- **Before and after** warns up front that the game's own shader cache is deleted, isn't backed up and can take minutes
  to rebuild.
- The Before and after report says that 1% low and average count from the game's first frame, menus and loading
  screens included.

**Games**

- **ELDEN RING** with its DLC installed compiles even when the DLC's key can't be found, and the DLC's shaders are added
  once the key downloads.
- **EA SPORTS WRC** from the EA app is compiled for WRC.exe instead of the leftover trial exe.
- **Godot games** show Vulkan or OpenGL instead of DirectX 11 and 12, and are marked as not supported.
- Unity games now show the DirectX of their latest run, so "Record while I play" comes back after a game switches to
  DirectX 12.
- Games whose shared recording includes players without mesh shader support, like **SILENT HILL: Townfall**, now get
  their full compile again instead of about a third of it.

**Recorder and anti-cheat**

- **Assassin's Creed Shadows, Mirage and Black Flag Resynced** get no recorder for now, since they failed to start with
  it. Compiling still works.
- **Frame times and Before and after work in games where ReShade is installed as d3d12.dll or d3d11.dll** (ReShade 6.8
  or later).
- Turning recording off keeps the last session's graph on the game page.
- Turning recording off also removes the recorder copy Onimusha keeps in its _storage_ folder.

### From 1.3.0-beta.2

**New**

- **Cyberpunk 2077 on AMD graphics cards is recorded**, contributed by ExMoha.
- **Games installed with the new Epic Games Launcher preview** are found and can be launched.
- **DXVK games on NVIDIA** are compiled through the game's own DXVK d3d11.dll.
- **Wuthering Waves compiles on NVIDIA graphics cards** from its game files once you enter its AES key, ray tracing
  included, contributed by JohnsonRan. Its shader cache is huge, up to about 80 GB. On AMD graphics cards it still needs
  a recording.
- **Allow the recorder in games with anti-cheat (at your own risk)** also offers a recording session for games whose
  engine isn't supported yet. Their recording is kept for when support arrives.
- **Report a problem** on each game page sends what went wrong to the SCSKiller team, and shows the whole report
  before you send it.

**Fixed**

- A big compile no longer drops to one thread when Windows reports little free memory.
- Games that contain anti-cheat and use a ReShade HDR mod like RenoDX no longer show Needs rebuilding after a restart.
- Neverness to Everness uses the game's exe instead of its launcher.
- Immortals of Aveum is compiled for its game exe instead of a launcher stub, so it compiles again on Windows 10.
- Games with Valve Anti-Cheat (VAC) no longer show No anti-cheat on their page.
- On a game that contains anti-cheat, the recorder turns on only through its own session card, never through the Turn
  on recording button.
- On AMD, the recorder texts for Cyberpunk 2077 name the file it adds, amd_ags_x64.dll.
- Assassin's Creed Valhalla no longer gets the recorder, which kept it at the splash screen.
- Hogwarts Legacy and games like it record again: the recorder no longer goes next to their launcher.
- A compile stopped because its game started waits until the game has been closed for 2 minutes before it continues,
  so restarting a game right after closing it no longer runs into its compile.
- A recording the game still had open when SCSKiller read it is picked up before the next compile.
- After a driver update, the game page says the last compile was for the old driver instead of showing its old coverage.
- scskiller --version on a portable copy no longer copies the installed copy's data.
- When the recorder records nothing, the game page and app.log say why.
- The Ray tracing shaders choice only shows on games with ray tracing.
- Frame times and Before and after also work with ReShade before 6.8 installed as dxgi.dll.
- A game with DXVK installed as its d3d11.dll or dxgi.dll says on its page that DXVK runs it on Vulkan, and whether the
  compile covers it.
- Recording sessions in games that contain anti-cheat also record DirectX 11 games.
- A game that contains anti-cheat and can be recorded in a session says it needs a recording instead of showing it as blocked.
- Send report is now Rate this compile, and Known to stutter is now Stutters without a compile.
- The anonymous daily check is off until you turn it on. SCSKiller asks once, and you can change it any time in Settings.
- A recorder an earlier version left as d3d11.dll is removed when recording is off, and before the recorder goes in as
  d3d12.dll.
- Games with FSR frame generation that closed at start with Record while I play on, like Train Sim World 7, start
  again.
- A before and after run whose frame times stop when frame generation starts ends with a note instead of waiting.
- Games from HoYoPlay, Battle.net, Riot Client and the EA app no longer offer a recording session that fails when
  clicked. Their page says SCSKiller can't start games from that store.
- SCP: Secret Laboratory no longer gets the recorder, which kept it from starting.
- The compile warns when one game's shaders are more than your NVIDIA shader cache limit in Settings.
- A compile no longer fails with Access denied when a folder left from an earlier compile is locked, for example by
  OneDrive.
- Turning on recording for a game where another SCSKiller copy left its recorder now puts this copy's recorder in
  place.
- On AMD, Split Fiction and other Unreal games that the driver gives a shader cache of their own are compiled into
  that cache. A game an earlier compile missed says so on its page, and the next compile reaches it.
- Before and after no longer says frame generation was on in games that only load DLSS Frame Generation, like Ready or
  Not with the option off.

### New in 1.3.0

**Fixed**

- STAR WARS: Galactic Racer compiles into the cache the game reads on AMD and is no longer marked as not supported.
- Wuthering Waves from its own launcher is read as Unreal, so its page asks for the AES key.
- On a busy PC, for example during a long compile of another game, a game with the recorder no longer loses it for
  good, and the game page no longer says the HDR mod the last compile ran through is gone. The recorder comes back once
  SCSKiller has checked the game again.
- A game with many new pipelines compiles automatically right away instead of waiting a day, one waiting for the PC to
  be idle is still compiled after SCSKiller restarts, and one held back by another compile is queued once that compile
  ends.
- The Last of Us Part II Remastered and other games with Sony's crash reporter are compiled for the game's own exe.
- Dark and Darker on Steam counts as a game with anti-cheat, so the recorder stays out.
- Mafia: The Old Country and other games that start NVIDIA's crash reporting with shader debug info use their compiled
  shaders: the compile now starts it the way the game does, and such a game compiled before is compiled again.
- On NVIDIA, the game page notices when the driver trims a game's compiled shaders, and no longer says the cache is gone
  when it isn't.
- A compile that fails no longer leaves its compile process running, and problem reports say why it failed.
- The recorder no longer slows a game's startup when it creates many shaders at once.
- A big compile that reaches its memory limit no longer drops to one thread for hours. It keeps its threads when fewer
  threads don't free any memory.
- On a PC with more than 64 GB of RAM, Auto gives compiling a quarter of the RAM instead of 16 GB.
- The compile time estimate follows the speed measured on all your compiled games, not only the first one.
- In a recording session for a game with anti-cheat, the card says the game contains anti-cheat when the recorder
  didn't load.
- Opening the queue page or Settings no longer changes the thread or memory limits you saved.
- The compile queue's cache warning no longer offers Raise the limit when the NVIDIA cache limit is already Unlimited.
- A click on the tray icon opens SCSKiller, a right-click shows its menu.
- On a game page, Left out says when pipelines weren't seen in a recording yet, and the note on what still compiles
  while you play stays when recording is already on.
- On a game page, the note on shader slots the game's engine adds no longer asks for a recording when recording is
  already on.
- Clicking a SCSKiller notification after SCSKiller was closed opens it instead of failing to start.

## [1.3.0-beta.2]

**New**

- **Cyberpunk 2077 on AMD graphics cards is recorded**, contributed by ExMoha.
- **Games installed with the new Epic Games Launcher preview** are found and can be launched.
- **DXVK games on NVIDIA** are compiled through the game's own DXVK d3d11.dll.
- **Wuthering Waves compiles on NVIDIA graphics cards** from its game files once you enter its AES key, ray tracing
  included, contributed by JohnsonRan. Its shader cache is huge, up to about 80 GB. On AMD graphics cards it still needs
  a recording.
- **Allow the recorder in games with anti-cheat (at your own risk)** also offers a recording session for games whose
  engine isn't supported yet. Their recording is kept for when support arrives.
- **Report a problem** on each game page sends what went wrong to the SCSKiller team, and shows the whole report
  before you send it.

**Fixed**

- A big compile no longer drops to one thread when Windows reports little free memory.
- Games that contain anti-cheat and use a ReShade HDR mod like RenoDX no longer show Needs rebuilding after a restart.
- Neverness to Everness uses the game's exe instead of its launcher.
- Immortals of Aveum is compiled for its game exe instead of a launcher stub, so it compiles again on Windows 10.
- Games with Valve Anti-Cheat (VAC) no longer show No anti-cheat on their page.
- On a game that contains anti-cheat, the recorder turns on only through its own session card, never through the Turn
  on recording button.
- On AMD, the recorder texts for Cyberpunk 2077 name the file it adds, amd_ags_x64.dll.
- Assassin's Creed Valhalla no longer gets the recorder, which kept it at the splash screen.
- Hogwarts Legacy and games like it record again: the recorder no longer goes next to their launcher.
- A compile stopped because its game started waits until the game has been closed for 2 minutes before it continues,
  so restarting a game right after closing it no longer runs into its compile.
- A recording the game still had open when SCSKiller read it is picked up before the next compile.
- After a driver update, the game page says the last compile was for the old driver instead of showing its old coverage.
- scskiller --version on a portable copy no longer copies the installed copy's data.
- When the recorder records nothing, the game page and app.log say why.
- The Ray tracing shaders choice only shows on games with ray tracing.
- Frame times and Before and after also work with ReShade before 6.8 installed as dxgi.dll.
- A game with DXVK installed as its d3d11.dll or dxgi.dll says on its page that DXVK runs it on Vulkan, and whether the
  compile covers it.
- Recording sessions in games that contain anti-cheat also record DirectX 11 games.
- A game that contains anti-cheat and can be recorded in a session says it needs a recording instead of showing it as blocked.
- Send report is now Rate this compile, and Known to stutter is now Stutters without a compile.
- The anonymous daily check is off until you turn it on. SCSKiller asks once, and you can change it any time in Settings.
- A recorder an earlier version left as d3d11.dll is removed when recording is off, and before the recorder goes in as
  d3d12.dll.
- Games with FSR frame generation that closed at start with Record while I play on, like Train Sim World 7, start
  again.
- A before and after run whose frame times stop when frame generation starts ends with a note instead of waiting.
- Games from HoYoPlay, Battle.net, Riot Client and the EA app no longer offer a recording session that fails when
  clicked. Their page says SCSKiller can't start games from that store.
- SCP: Secret Laboratory no longer gets the recorder, which kept it from starting.
- The compile warns when one game's shaders are more than your NVIDIA shader cache limit in Settings.
- A compile no longer fails with Access denied when a folder left from an earlier compile is locked, for example by
  OneDrive.
- Turning on recording for a game where another SCSKiller copy left its recorder now puts this copy's recorder in
  place.
- On AMD, Split Fiction and other Unreal games that the driver gives a shader cache of their own are compiled into
  that cache. A game an earlier compile missed says so on its page, and the next compile reaches it.
- Before and after no longer says frame generation was on in games that only load DLSS Frame Generation, like Ready or
  Not with the option off.

## [1.3.0-beta.1]

**This is a very big update**, with everything from the 1.3.0 alphas and many new fixes.

- **After this update**, SCSKiller checks every compiled game again, which can take several minutes with many games. The app is
  slow until that ends. Later starts are fast.

### From 1.3.0-alpha.1

**New**

- **Official recordings**: SCSKiller's own recordings of a game version, marked "Official recording" on the game page.
  Each is for backers, for every Patreon supporter, or for everyone without signing in.
- **DirectX 11 games compile on AMD**. Their vertex shaders need a recording, so on AMD the recorder now records DirectX
  11 games too, and the community database shares what it learns.
- **Engine support updates from the server**: the rules that decide what a game compiles can be fixed or extended
  between releases. Only rules SCSKiller signed are used, and a change rebuilds only the games it covers, while the PC
  is idle.
- **Riot Client**: its games are found (VALORANT, League of Legends, Legends of Runeterra, 2XKO). They're never
  recorded, as anti-cheat games.
- **Naughty Dog games** (The Last of Us Part I, UNCHARTED 4: A Thief's End, UNCHARTED: The Lost Legacy): SCSKiller
  reads their shaders. They compile after a recording. The two UNCHARTED games of the Legacy of Thieves Collection show
  as two games, each compiled for its own exe. Listed as one, the collection compiled for an exe that never runs, so
  the driver cache it filled was one neither game reads. One recorder, on UNCHARTED 4's page, records both.
- **God of War Ragnarök** compiles without a recording on NVIDIA. **God of War (2018)** compiles its DirectX 11 shaders
  without a recording (on AMD, all but the vertex shaders).
- **Ghost of Tsushima DIRECTOR'S CUT** and **Ratchet & Clank: Rift Apart** compile without a recording on NVIDIA, and
  after a recording on AMD. **HELLDIVERS 2**'s shaders are read too, but GameGuard keeps the recorder out, so it can't
  compile yet.
- **Horizon Zero Dawn Complete Edition** and **DEATH STRANDING DIRECTOR'S CUT**: SCSKiller reads their shaders. They
  compile after a recording.
- **Overwatch (Steam)** compiles its DirectX 11 shaders without a recording (on AMD, the pixel and compute shaders).
  Its Steam install counts as an anti-cheat game, so the recorder stays out (thanks @JohnsonRan).
- **Source 2 games** like Counter-Strike 2 compile their DirectX 11 shaders without a recording (on AMD, all but the
  vertex shaders). Based on the notes in issue #13.
- **Notifications** under a bell at the top: news, notes on new rules and links, only ones SCSKiller signed.
- **"What's new"** lists a version's changes once after an update. About shows it again.
- **Max compile** on a game's page compiles every combination of that game's shaders, once. It takes longer and uses
  more disk space, but potentially fewer shaders compile while you play, which seems to help most on AMD. Nothing is
  kept for the game: its next Compile is a standard one. It replaces Compile everything.
- **Maximum mode suggested per game**: a game whose Maximum mode is known to help on your GPU says so on its page, with
  "Turn on for this game". It turns Maximum on for that game only. On AMD, Maximum adds at most as many pairings as the
  game's plan has, and at most about 2 GB of the shared cache. Maximum stays off by default.
- **AMD cache use**: Settings and the Cache page show the DirectX 12 cache against the driver's fixed 16 GB, and say when
  it's nearly full. A game's Maximum suggestion says how much it would add at most.
- **Cache page**: every shader cache on the PC with its size, by installed game, uninstalled game, old driver, earlier
  SCSKiller compiles and other GPU vendor, and the folder the driver reads and writes it in. Delete an item, a group or
  everything there. After a driver update it offers to free what the new driver no longer reads.
- A game marked not supported after it was compiled offers to clear the cache compiled for it.
- A driver cache folder moved to another drive with a junction is followed: Settings shows that drive's free space, and
  a link whose drive is missing holds compiles instead of marking games as cleared by the driver.
- **Library sorting and filters**: the Game, Pipelines and Cache headers sort each section, and a status filter shows
  only games that aren't supported, are ready to compile, are compiled, or were compiled by SCSKiller.
- **Game folders**: add a folder of games no store lists, and SCSKiller finds each game in it at every scan. Games added
  by hand or found in a folder can be renamed.
- **Scan progress** shows per store and per game, and a big library shows its last list at once while the scan runs.
- **New shaders compile on their own** if you choose: when a compiled game gains pipelines from its recording, the
  community database or an update, SCSKiller compiles them while the PC is idle, never while you play. Set it in
  Settings, or per game on its page. The default still only notifies you.
- **Theme**: Light, Dark or the Windows setting, in Settings.
- **A click on the tray icon** opens a small window with SCSKiller's status and the compile's progress, and Pause, Open
  and Quit. The icon's tooltip shows the status too.
- **"Share diagnostics to help fix problems"** in Settings (off by default): when a compile fails or the driver rejects
  pipelines, SCSKiller sends error codes and pipeline hashes, nothing else. "What's shared" in Settings lists it all.
  SCSKiller asks once, in the welcome or at the first start after the update.
- **Shader hash sharing** also sends one report after a compile: which pipelines still compiled at the next launch, with
  your GPU model and driver.
- **Compile progress on the game page**: while a game compiles, its page shows the queue's progress and the time left.
  While it waits, it shows its place in the queue.
- **Edit the compile queue in bulk**: select several games with Ctrl+click or Shift+click, then Remove selected. Clear
  queue removes every game but the one compiling.
- **Pick the DirectX** of a game when SCSKiller only guessed it: Auto, DirectX 11 or DirectX 12 next to its engine on the
  game page. Its plan is rebuilt for the pick. Your recording of the game wins over a guess, and over the pick. Unity games
  built for both count as a guess now, and a change of DirectX always rebuilds the plan.
- **GPU picker** in Settings, on a PC with more than one NVIDIA or AMD GPU: SCSKiller compiles for the one you pick
  instead of the one with the most video memory. It switches after a restart.
- **Earlier sessions** on a game's page: the last 20 launches with frame times, each with its date, play length, 1%
  low and stutters.
- **Send report**: once a game was compiled and played, or couldn't be compiled, its page can send an anonymous
  report of how it ran. Players with a similar system then see what others reported on the game's page.

**Faster and steadier**

- **A new community recording** for a game you compiled rebuilds its plan while the PC is idle, and the game shows how
  many more pipelines it can compile.
- A game whose engine SCSKiller reads better says "SCSKiller can compile more" instead of "Game shaders changed".
- When new rules more than double a game's plan, the compile after a driver update leaves it for you to start.
- **A recompile compiles only what isn't compiled yet**, so adding a recording or turning on Maximum mode is quicker. It
  compiles everything again after a driver update, when the driver's cache lost files, or when the game still compiled
  after the last run.
- On NVIDIA, a game no longer asks to compile again after each session for pipelines whose shaders its last compile
  already compiled.
- On NVIDIA, compiled ray tracing pipelines that a game creates on other threads (The Witcher 3) are found in the cache
  at launch instead of compiling again.
- The Library holds still: a game that changes is updated in its row, the list keeps its place when a game moves to
  another section, and the scan's status line no longer pushes the list down.
- **Less memory with big community recordings**: a download is checked as it decodes and kept compressed, and a
  compile no longer holds every recorded shader in memory. With a 250,000-record recording, the download takes about
  45 MB instead of 1.1 GB and the compile's peak about half.
- **Bigger community recordings on NVIDIA**: up to 500,000 pipelines per game, once the community database serves
  them. AMD keeps reading up to 250,000.
- **Building a plan can no longer run forever or take all the memory.** A plan that outgrows half the PC's RAM or 30
  minutes stops with a clear error, and the next compile plans again. Building the plan of a game with many shader
  layouts is much faster.
- With "Closes to System Tray" on, closing the window no longer sometimes quits SCSKiller.
- **Updates**: after a failed download, SCSKiller waits longer before the next try (1 hour, then up to 12). "Check for
  updates" still tries at once.
- A **partly compiled** game's "Last compile" card says what the refused pipelines mean and what helps, and the
  coverage ring of a compiled game no longer counts the pipelines the driver refused.
- While a compile runs, the queue and the game page say "memory limit: 4 of 32 threads" when the compile's memory limit
  runs fewer threads than set.
- **Pipelines that crash the GPU driver** are all found and skipped in one compile (The Witcher 3 on AMD). Before, a
  compile stopped after 20 driver crashes, and a game with many of them needed several clicks on Compile. A compile
  that still stops says how many it skipped, and compiling again continues where it stopped.
- **Pipelines that make the GPU driver fault** (PRAGMATA on AMD) are found and skipped the same way. Before, a compile
  skipped them only for that run and stopped after three, so every later compile met them again.
- The "Last compile" card says "Nothing failed." also when it skipped pipelines whose shaders aren't in this version
  of the game.
- **Start with Windows**: a portable or test copy of SCSKiller no longer removes the installed copy's autostart, or
  moves it to itself, when it starts. A copy changes another copy's entry only when you turn the setting on in it.
- **NVIDIA Auto Shader Compilation** in Settings: Apply changes only what differs. With the feature already on it says
  so, with no administrator prompt, instead of failing to set up NVIDIA's task again. An error says which step failed
  and whether the setting was saved.
- **app.log** in SCSKiller's data folder keeps what the app did (scans, community sync, compiles and the recorder) for
  a bug report. Your user name and profile folder are masked, and it takes at most 2 MB.

**Games**

- **GTA V Enhanced** compiles on NVIDIA without a recording. BattlEye still keeps the recorder out.
- **Resident Evil Village** and **PRAGMATA** compile on NVIDIA without a recording.
- **Dragon's Dogma 2**: SCSKiller reads its game files, which it refused before.
- **Games that build all their shaders at the main menu** (Horizon Forbidden West, Detroit: Become Human) show as not
  supported, with that reason.
- **Encrypted Unreal games with anti-cheat** (VALORANT and others) get their key from the exe file on disk, so
  SCSKiller can read their files. Reading the key never touches the running game.
- **Encrypted Unreal games** take their correct AES key where it was refused or opened only some files: games with
  their own decryption (Dead by Daylight and other forks), files under a named key, and readable file lists whose files
  are encrypted. The key is also found in exes built for AVX. When the exe is protected (SteamStub, Themida, VMProtect,
  Enigma, or Denuvo), the game page says so instead of only asking for the key.
- **Cyberpunk 2077** shows its engine as REDengine 4, and Alan Wake 2 and CONTROL Resonant as Northlight 2.
- **Cyberpunk 2077 on Steam** is found with its own exe, the one its launcher configuration names, not REDprelauncher.exe.
  The recorder goes beside the game, and the game no longer counts as closed once the launcher exits.
- **Returnal** no longer compiles tessellated pipelines the driver rejects.
- Games whose shaders carry their own root signatures (Naughty Dog, RAGE, FromSoftware and others SCSKiller reads
  directly) no longer compile pipelines the driver rejects, where a shader uses resources the root signature lacks.
- **The Last of Us Part I**: a compute shader the game ships with several root signatures compiles once for each of
  them, instead of only the first.
- **War Thunder** is on the known shader-stutter list, and no longer stays on "Needs rebuilding" when nothing new comes
  of it. On DirectX 12 it also compiles its shadow and depth passes.
- **ELDEN RING NIGHTREIGN** with its Digital Artbook & Mini Soundtrack installed no longer shows as a DirectX 11 Unity
  game. SCSKiller took the artbook's app for the game. It now follows the exe the game's EasyAntiCheat settings start.
- Unreal Engine 4.25 games no longer show "not tested on this engine version yet".
- **Unreal versions**: 5.7 and 5.8 games no longer show as 5.6, and a game on a newer Unreal than SCSKiller knows says
  "Unreal 5.9 isn't supported yet" instead of compiling as 5.8. A version SCSKiller can only guess shows "(guessed)", and
  a game whose Unreal version changes rebuilds its plan.
- DirectX 11 shaders that read a multisampled texture with more samples than the GPU supports compile.
- **Games started through EasyAntiCheat's bootstrap** (Gears of War: E-Day and Sea of Thieves from the Xbox app) compile
  for the game's own exe, the one the anti-cheat's settings name, instead of the bootstrap. An earlier compile shows
  "Needs rebuilding".
- A game's page says the game may still run its own shader step when it starts.
- A not supported game no longer shows its shader coverage after Clear cache.

**Mods**

- **ReShade and HDR mods** like RenoDX show as a tag in the Library and on the game page, and "Compiles through RenoDX"
  under the game's status.
- Luma mods no longer make the game page say some pipelines aren't precompiled (they change no DirectX 12 pipeline).
- When a mod loads a copy of the recorder from another folder, where it can't record, the game page says so.
- **ReShade chaining another mod** through ReShade.ini's `[PROXY] ProxyLibrary` (OptiScaler or a DLSS mod under another
  name) no longer compiles through a copy of ReShade that lacks that mod's DLL. With a RenoDX mod that changes every
  pipeline, the game page says how to keep that mod under another name and turn the proxy off. One that replaces some
  shaders compiles without ReShade.

**Recorder and anti-cheat**

- **Recording in games with anti-cheat, at your own risk.** Until now the recorder always stayed out of these games, so
  they never got a recording of their own. Settings has "Allow the recorder in games with anti-cheat (at your own
  risk)", off by default. A game that contains anti-cheat then offers "Record a session (at your own risk)" on its page,
  and each session is confirmed before it starts. Games with anti-cheat can treat the recorder as cheating and ban your
  account, and no game is known to be safe. SCSKiller removes every file it places when the game exits, or after a
  crash at your next logon.
- **Frame times with frame generation on** (FSR 3): the graph shows the frames the game rendered. Before, the recorder
  measured no frame times while frame generation was on.
- **Frame times in games with NVIDIA Streamline** (Ghost of Tsushima, Ratchet & Clank) on every GPU. Before, the
  recorder measured none in them unless DLSS frame generation was on.
- On NVIDIA, the recorder no longer makes a game crash as it closes.
- **The recorder no longer crashes a game now and then as it sets up its hooks.** It moved a few of a function's first
  instructions to make room for its jump, so a game thread paused among them could resume mid-jump and crash. It now
  replaces only the first instruction.
- **A game that makes its own crash dump or log folder** (Horizon Zero Dawn's coredumps) no longer turns the recorder
  off. Any new folder that wasn't empty turned it off until the install was checked again, so the first recorded
  session was lost. A new folder now keeps it on when it holds only data files, with no code or anti-cheat in it.
- **A portable or test copy** keeps its own recorders: each copy's armed list is in its own data folder, and a copy
  never arms, updates, removes or imports the recording of a recorder another copy placed. Before, a copy with a new
  data folder took every recorder for its own and moved the game's recording out of the game folder. Turning the
  recorder on in a copy takes it over. Recorders placed before this version belong to the installed copy.
- The recorder's log no longer holds your user name or full folder paths, so it's safer to share in a bug report.
- **Valve Anti-Cheat (VAC) games** never get the recorder. They still compile from their files.
- **NetEase games whose anti-cheat leaves no file in the game folder** count as anti-cheat games, so the recorder stays
  out of them: the Steam installs of Once Human, Marvel Rivals, FragPunk, Sword of Justice and NARAKA: BLADEPOINT, and
  Once Human from any store by its exe.
- **ELDEN RING added by hand** gets the offline session too, when the EasyAntiCheat settings beside its exe are
  ELDEN RING's.
- **Euro Truck Simulator 2 and American Truck Simulator** count as anti-cheat games on a PC with TruckersMP.
- **Xbox app Unreal games that start through a small launcher** (Senua's Saga: Hellblade II, Avowed) record. The
  recorder moves beside the game's own exe once the game is closed.
- **A Steam game you uninstalled loses the recorder too.** Steam leaves the folder behind when it holds files it didn't
  install. A scan once the game is gone removes SCSKiller's own files from it, and the folders they leave empty. The
  game's recording is kept.

### From 1.3.0-alpha.2

**New**

- Initial experimental Linux support.
- **Command line**: `add` adds a game no store lists.
- **Leave out of Add all ready** on the game page: the game stays out of Add all ready, and its own Add button still works.
- **Remove from library** on every game's page. A game a store lists is hidden, also after a refresh, and nothing compiles
  it on its own. Its files, saves and compiled cache stay. **Show hidden** on the Library page brings it back.
- **Set AES key** on the game page of an encrypted Unreal game: paste your own key there, without opening Why? first.
- **DirectX choice** on the game page of every DirectX game, also where SCSKiller is sure which one it runs on.
- **Before and after recording** on the game page of a recorded DirectX 12 or DirectX 11 game: SCSKiller deletes the
  game's shader caches and starts it, you play 5 minutes, it deletes the caches again and compiles, and you play the
  same 5 minutes again. Both runs count from the game's first frame, loading and pauses included. A small panel in the
  game shows the time left, your frame times and each shader compilation stutter as it happens, also with frame
  generation and HDR. The report compares shader compilation stutters, pipelines ready from the cache and the worst
  stutter, with both frame-time graphs, and says whether frame generation was on. On DirectX 11 it compares every
  stutter, since SCSKiller can't tell shader compilation stutters apart there. Save or copy it as an image, or share it
  to X, Reddit or Discord. Frame times and the report stay on your PC unless you share the image.
- **Intel GPUs (experimental, contributed by nyannoying1337)**: Settings shows "Experimental: compile on Intel GPUs" on a
  PC with an Intel GPU, off by default. Only Intel Arc was tested.
- **Send report** lists what a report carries until you've sent one. Later reports leave that line out, and Settings
  still has What's shared.
- **Compile ray tracing shaders** in Settings, on by default, with a choice per game on its page. Off skips them, and
  the game builds them itself when you turn ray tracing on.

**Faster and steadier**

- **Also delete the game's own shader cache** also clears the pipeline libraries of GTA V Enhanced and HELLDIVERS 2.
- **Also delete the game's own shader cache** no longer deletes an Unreal game's user pipeline cache. It lists what the
  game created while you played, and only playing again refills it.
- **The game scan keeps going past a file it can't read**, on a failing drive or in a cloud folder. 1.2.3 to 1.2.5
  stopped with system error 0xc0000006. That game is skipped, and the log names the file.
- On NVIDIA, a compile where several shaders hang the driver at once skips them for that compile and keeps going.
- A compile that created nothing ends as Failed and says why, such as the driver rejecting every pipeline, instead
  of Done with a count.
- On Windows 10, a game whose exe is protected or packed, such as AION 2 and Fortnite, compiles with the game's own
  D3D12 runtime from its D3D12 folder. Its compile used the older runtime built into Windows 10, which rejected every
  pipeline.
- On NVIDIA, a game whose compiled shader cache files are gone, or smaller than after its compile, asks to compile
  again. When its shaders are bigger than the driver's shader cache size, it asks to raise that size instead.
- On AMD, a game whose files the driver removed because its shader cache is full no longer asks to compile again,
  which only removed another game's files in turn.
- **Keep compiled caches aside** (AMD, Settings): a compiled game's shader cache is put back after the driver's 16 GB
  limit removes it, so it doesn't stutter again.
- On NVIDIA, a plan built again after a new recording no longer offers to compile the shaders the last compile already
  compiled.
- **The coverage ring** on the game page is one measure in every state, so it no longer drops after each play, jumps
  when the game needs compiling again, or rises when the driver refuses more than a tenth of the pipelines.
- A compile run by another SCSKiller, such as a portable copy, no longer counts as playing the game.
- A portable copy made from your installed SCSKiller's data folder starts with Start with Windows, Record all games and
  automatic updates off.
- **A community recording that doesn't match the game's files** is no longer compiled, which skipped all its pipelines
  every time, and games whose shaders SCSKiller can't read no longer share or download recordings under one hash.
- A game compiles one texture filtering setting unless Maximum mode is on, so a community recording with several
  settings no longer makes its compile several times bigger.
- On Unreal games, the texture filtering setting compiled is the player's own when the game's settings files name it.
- After an app update, a compiled game stays compiled until its plan is checked, and says it can compile more only
  when the check finds more.
- **Unreal games** also compile what the game saved in its own pipeline cache in your user folder while you played,
  without the recorder, also when the game contains anti-cheat. The next compile picks up what a new play added.
- **Reading shaders** on Unreal games runs at below-normal priority and shows how many packages it has read, so games
  like Days Gone or Styx no longer look stuck there and the window stays responsive. A read that runs out of memory
  stops with a message that says so.
- A ray tracing shader that crashes or hangs the driver is skipped for that compile, along with the ones built on it,
  and the compile keeps all its threads. These count as skipped, not failed, and the next compile tries them again.
- A compile stopped after the driver reset too many times continues from where it stopped.
- **DirectX 11 games**: shaders SCSKiller can't compile yet count as skipped, no longer as refused by the driver, and
  Unity games such as Megabonk compile more of their shaders.
- When a compile pauses for a game, the shaders it was building are finished on resume instead of being skipped and
  blamed on the driver.
- **Updates never install a version older than the one running**, unless you pick Go back to stable.
- On AMD, a game that gives the driver its own name through AMD's AGS library gets its compile where it reads its
  shaders, once the recorder saw it play.
- The game page says Careful compile is only for games whose compile fails.
- On NVIDIA, the game page also says a compile didn't help when that shows after a Before and after run or after
  Clear cache.
- While a game runs, its page shows the last finished play under Last time you played, not part of the current one.
- A game that compiled shaders for its whole launch no longer shows "no stutters" and a 1% low of 0 fps on its page.
- On AMD, Before and after and Clear cache no longer delete the DirectX 11 shader cache of another game with the same exe name.

**Games**

- **RE Engine games** with an empty patch file in their folder (Resident Evil 2, often a mod manager's leftover) no
  longer show "Not supported yet".
- Games whose ray tracing is all inline, such as RE Engine games, count their shaders that trace rays inline in the
  game page details.
- **Hidden game folders are scanned**, such as a Unity game's Data folder hidden in Explorer. Their compile no longer
  skips every shader.
- **DCS World** and other games with two builds of one program (bin\DCS.exe and bin-mt\DCS.exe) can be added with
  their own folder, which was refused as "a folder of several games" (found by JLAF5150).
- **Xbox app Unreal games** built for WinGDK (Evil West) read their own DirectX setting, not the Windows one.
- **FINAL FANTASY XV WINDOWS EDITION** precompiles every DirectX 11 shader without a recording (on AMD, all but the
  vertex shaders).
- **Mass Effect Legendary Edition**, **FINAL FANTASY TACTICS - The Ivalice Chronicles** and **KINGDOM HEARTS HD 1.5+2.5
  ReMIX** on Steam list each of their games on its own. Mass Effect Legendary Edition showed only Mass Effect 3, and
  each of its games now has its own recorder (#126).
- **Forza Horizon 6** (Steam and Xbox app) compiles from its game files without a recording. Its ray tracing shaders
  need a recording. The game contains anti-cheat, so the recorder goes in only through "Record a session (at your own
  risk)".
- **Unreal games stay detected** when SCSKiller can't download a decompression codec, such as behind a proxy, firewall
  or antivirus. They showed "engine not supported yet", and now an unreadable game names the missing codec.
- A game that needs a recording and is in the community database says "in the community database for Patreon
  supporters" when you aren't signed in as one, since only they can compile from it.

**Recorder and anti-cheat**

- **PRAGMATA** gets no recorder for now, since it crashes the game while it loads. Compiling still works, and a
  recorder already in its folder is removed.
- **WARDOGS** counts as a game with anti-cheat (Elytra), so the recorder goes in only through "Record a session (at
  your own risk)". It showed "no anti-cheat" since the game moved from EasyAntiCheat to Elytra.
- **THE FINALS** counts as a game with anti-cheat by its Steam id, since Elytra installs outside its folder.
- **A Steam game whose launch options start an anti-cheat launcher** counts as a game with anti-cheat even when no
  anti-cheat file is in its folder yet.
- **A mod the recorder ran alongside** no longer comes back after its own uninstall removed it. Its renamed file is left
  unused and recording turns off for that game.
- A portable copy upgraded from 1.2.5 takes back the recorders it placed, so they record and update again.
- **A portable copy made from your installed SCSKiller's data folder** no longer takes over the recorders the installed
  copy placed. Its game page says another SCSKiller copy placed them, until you turn recording on there.
- **Frame times are recorded again in games where a mod such as ReShade replaces DirectX's dxgi.dll**, so the game
  page's frame-time graph and the Before and after panel show there. 1.2.5 lost them in those games.
- The recorder records while the game's exe is the one SCSKiller last checked for anti-cheat, and no anti-cheat is next
  to it or in the game's folders above it. A mod's files appearing in the game folder no longer stop it until the next
  check. SCSKiller still never puts it in an anti-cheat game, and takes it out the moment it finds anti-cheat: when it
  scans, when the game exits or updates, and every 2 minutes while it runs. The `scskiller.armed` file is gone from game
  folders.
- **Forza Horizon 6** contains anti-cheat. It counts as an anti-cheat game from Steam, the Xbox app or added by hand,
  so the recorder stays out unless you enable it at your own risk. SCSKiller isn't responsible for bans.
- **A VAC game in a folder whose path is longer than 260 characters** counts as VAC, so the recorder stays out. Its VAC
  check could miss it before.

**Known issues**

- A portable copy made from another 1.2.5 portable copy's data folder takes over that copy's recorders. 1.2.5
  recorders don't record which copy placed them.

### New in 1.3.0-beta.1

**New**

- **HDD mode** in Settings: SCSKiller reads a game's files only when you open or compile it, so scans finish on big or
  slow libraries.
- **Maximum memory for compiling** goes up to 2 GB below your PC's RAM.

**Faster and steadier**

- **Compile queue** and Start no longer add games you didn't pick.
- Games show in the library as soon as a scan finds them, and a game you add shows at once.
- **Compiles no longer stay slow after hitting the memory limit** on ray tracing games. While the PC has little free
  memory, a compile uses fewer threads.
- Games with many driver-crashing pipelines can start their compile again.
- **A compile no longer stops when the GPU driver crashes the compile process outright.** It goes on and skips only
  the pipeline that crashed.
- A compile downloads the community upscaler packs for that game first, so it no longer shows "Needs rebuilding" right
  after compiling.
- Games with too many shaders to plan compile their recorded pipelines instead of failing, and their status says
  "recorded pipelines only".
- **A compiled game only shows "Needs rebuilding" once at least 500 new pipelines are recorded**, or 1% of its plan if
  that is more. Fewer are compiled at its next compile, or quietly while the PC is idle if the game compiles new shaders
  automatically.
- **Unreal games keep their game exe** when another program from the game's folder runs during play, so a compile no
  longer goes to the wrong exe and the game no longer asks for a full rebuild after playing.
- One unreadable Steam game folder no longer hides every Steam game.
- A game no longer counts as running when another program with the same exe name runs, such as Office's ai.exe for
  Alien: Isolation.
- When NVIDIA Auto Shader Compilation can't be applied, the error says why NVIDIA's tool failed, and the app log keeps
  its full output.
- **Before and after** warns up front that the game's own shader cache is deleted, isn't backed up and can take minutes
  to rebuild.
- The Before and after report says that 1% low and average count from the game's first frame, menus and loading
  screens included.

**Games**

- **ELDEN RING** with its DLC installed compiles even when the DLC's key can't be found, and the DLC's shaders are added
  once the key downloads.
- **EA SPORTS WRC** from the EA app is compiled for WRC.exe instead of the leftover trial exe.
- **Godot games** show Vulkan or OpenGL instead of DirectX 11 and 12, and are marked as not supported.
- Unity games now show the DirectX of their latest run, so "Record while I play" comes back after a game switches to
  DirectX 12.
- Games whose shared recording includes players without mesh shader support, like **SILENT HILL: Townfall**, now get
  their full compile again instead of about a third of it.

**Recorder and anti-cheat**

- **Assassin's Creed Shadows, Mirage and Black Flag Resynced** get no recorder for now, since they failed to start with
  it. Compiling still works.
- **Frame times and Before and after work in games where ReShade is installed as d3d12.dll or d3d11.dll** (ReShade 6.8
  or later).
- Turning recording off keeps the last session's graph on the game page.
- Turning recording off also removes the recorder copy Onimusha keeps in its _storage_ folder.

## [1.3.0-alpha.2]

- **After this update**, SCSKiller checks every compiled game again, which can take several minutes with many games. The app is
  slow until that ends. Later starts are fast.

### New

- Initial experimental Linux support.
- **Command line**: `add` adds a game no store lists.
- **Leave out of Add all ready** on the game page: the game stays out of Add all ready, and its own Add button still works.
- **Remove from library** on every game's page. A game a store lists is hidden, also after a refresh, and nothing compiles
  it on its own. Its files, saves and compiled cache stay. **Show hidden** on the Library page brings it back.
- **Set AES key** on the game page of an encrypted Unreal game: paste your own key there, without opening Why? first.
- **DirectX choice** on the game page of every DirectX game, also where SCSKiller is sure which one it runs on.
- **Before and after recording** on the game page of a recorded DirectX 12 or DirectX 11 game: SCSKiller deletes the
  game's shader caches and starts it, you play 5 minutes, it deletes the caches again and compiles, and you play the
  same 5 minutes again. Both runs count from the game's first frame, loading and pauses included. A small panel in the
  game shows the time left, your frame times and each shader compilation stutter as it happens, also with frame
  generation and HDR. The report compares shader compilation stutters, pipelines ready from the cache and the worst
  stutter, with both frame-time graphs, and says whether frame generation was on. On DirectX 11 it compares every
  stutter, since SCSKiller can't tell shader compilation stutters apart there. Save or copy it as an image, or share it
  to X, Reddit or Discord. Frame times and the report stay on your PC unless you share the image.
- **Intel GPUs (experimental, contributed by nyannoying1337)**: Settings shows "Experimental: compile on Intel GPUs" on a
  PC with an Intel GPU, off by default. Only Intel Arc was tested.
- **Send report** lists what a report carries until you've sent one. Later reports leave that line out, and Settings
  still has What's shared.
- **Compile ray tracing shaders** in Settings, on by default, with a choice per game on its page. Off skips them, and
  the game builds them itself when you turn ray tracing on.

### Faster and steadier

- **Also delete the game's own shader cache** also clears the pipeline libraries of GTA V Enhanced and HELLDIVERS 2.
- **Also delete the game's own shader cache** no longer deletes an Unreal game's user pipeline cache. It lists what the
  game created while you played, and only playing again refills it.
- **The game scan keeps going past a file it can't read**, on a failing drive or in a cloud folder. 1.2.3 to 1.2.5
  stopped with system error 0xc0000006. That game is skipped, and the log names the file.
- On NVIDIA, a compile where several shaders hang the driver at once skips them for that compile and keeps going.
- A compile that created nothing ends as Failed and says why, such as the driver rejecting every pipeline, instead
  of Done with a count.
- On Windows 10, a game whose exe is protected or packed, such as AION 2 and Fortnite, compiles with the game's own
  D3D12 runtime from its D3D12 folder. Its compile used the older runtime built into Windows 10, which rejected every
  pipeline.
- On NVIDIA, a game whose compiled shader cache files are gone, or smaller than after its compile, asks to compile
  again. When its shaders are bigger than the driver's shader cache size, it asks to raise that size instead.
- On AMD, a game whose files the driver removed because its shader cache is full no longer asks to compile again,
  which only removed another game's files in turn.
- **Keep compiled caches aside** (AMD, Settings): a compiled game's shader cache is put back after the driver's 16 GB
  limit removes it, so it doesn't stutter again.
- On NVIDIA, a plan built again after a new recording no longer offers to compile the shaders the last compile already
  compiled.
- **The coverage ring** on the game page is one measure in every state, so it no longer drops after each play, jumps
  when the game needs compiling again, or rises when the driver refuses more than a tenth of the pipelines.
- A compile run by another SCSKiller, such as a portable copy, no longer counts as playing the game.
- A portable copy made from your installed SCSKiller's data folder starts with Start with Windows, Record all games and
  automatic updates off.
- **A community recording that doesn't match the game's files** is no longer compiled, which skipped all its pipelines
  every time, and games whose shaders SCSKiller can't read no longer share or download recordings under one hash.
- A game compiles one texture filtering setting unless Maximum mode is on, so a community recording with several
  settings no longer makes its compile several times bigger.
- On Unreal games, the texture filtering setting compiled is the player's own when the game's settings files name it.
- After an app update, a compiled game stays compiled until its plan is checked, and says it can compile more only
  when the check finds more.
- **Unreal games** also compile what the game saved in its own pipeline cache in your user folder while you played,
  without the recorder, also when the game contains anti-cheat. The next compile picks up what a new play added.
- **Reading shaders** on Unreal games runs at below-normal priority and shows how many packages it has read, so games
  like Days Gone or Styx no longer look stuck there and the window stays responsive. A read that runs out of memory
  stops with a message that says so.
- A ray tracing shader that crashes or hangs the driver is skipped for that compile, along with the ones built on it,
  and the compile keeps all its threads. These count as skipped, not failed, and the next compile tries them again.
- A compile stopped after the driver reset too many times continues from where it stopped.
- **DirectX 11 games**: shaders SCSKiller can't compile yet count as skipped, no longer as refused by the driver, and
  Unity games such as Megabonk compile more of their shaders.
- When a compile pauses for a game, the shaders it was building are finished on resume instead of being skipped and
  blamed on the driver.
- **Updates never install a version older than the one running**, unless you pick Go back to stable.
- On AMD, a game that gives the driver its own name through AMD's AGS library gets its compile where it reads its
  shaders, once the recorder saw it play.
- The game page says Careful compile is only for games whose compile fails.
- On NVIDIA, the game page also says a compile didn't help when that shows after a Before and after run or after
  Clear cache.
- While a game runs, its page shows the last finished play under Last time you played, not part of the current one.
- A game that compiled shaders for its whole launch no longer shows "no stutters" and a 1% low of 0 fps on its page.
- On AMD, Before and after and Clear cache no longer delete the DirectX 11 shader cache of another game with the same exe name.

### Games

- **RE Engine games** with an empty patch file in their folder (Resident Evil 2, often a mod manager's leftover) no
  longer show "Not supported yet".
- Games whose ray tracing is all inline, such as RE Engine games, count their shaders that trace rays inline in the
  game page details.
- **Hidden game folders are scanned**, such as a Unity game's Data folder hidden in Explorer. Their compile no longer
  skips every shader.
- **DCS World** and other games with two builds of one program (bin\DCS.exe and bin-mt\DCS.exe) can be added with
  their own folder, which was refused as "a folder of several games" (found by JLAF5150).
- **Xbox app Unreal games** built for WinGDK (Evil West) read their own DirectX setting, not the Windows one.
- **FINAL FANTASY XV WINDOWS EDITION** precompiles every DirectX 11 shader without a recording (on AMD, all but the
  vertex shaders).
- **Mass Effect Legendary Edition**, **FINAL FANTASY TACTICS - The Ivalice Chronicles** and **KINGDOM HEARTS HD 1.5+2.5
  ReMIX** on Steam list each of their games on its own. Mass Effect Legendary Edition showed only Mass Effect 3, and
  each of its games now has its own recorder (#126).
- **Forza Horizon 6** (Steam and Xbox app) compiles from its game files without a recording. Its ray tracing shaders
  need a recording. The game contains anti-cheat, so the recorder goes in only through "Record a session (at your own
  risk)".
- **Unreal games stay detected** when SCSKiller can't download a decompression codec, such as behind a proxy, firewall
  or antivirus. They showed "engine not supported yet", and now an unreadable game names the missing codec.
- A game that needs a recording and is in the community database says "in the community database for Patreon
  supporters" when you aren't signed in as one, since only they can compile from it.

### Recorder and anti-cheat

- **PRAGMATA** gets no recorder for now, since it crashes the game while it loads. Compiling still works, and a
  recorder already in its folder is removed.
- **WARDOGS** counts as a game with anti-cheat (Elytra), so the recorder goes in only through "Record a session (at
  your own risk)". It showed "no anti-cheat" since the game moved from EasyAntiCheat to Elytra.
- **THE FINALS** counts as a game with anti-cheat by its Steam id, since Elytra installs outside its folder.
- **A Steam game whose launch options start an anti-cheat launcher** counts as a game with anti-cheat even when no
  anti-cheat file is in its folder yet.
- **A mod the recorder ran alongside** no longer comes back after its own uninstall removed it. Its renamed file is left
  unused and recording turns off for that game.
- A portable copy upgraded from 1.2.5 takes back the recorders it placed, so they record and update again.
- **A portable copy made from your installed SCSKiller's data folder** no longer takes over the recorders the installed
  copy placed. Its game page says another SCSKiller copy placed them, until you turn recording on there.
- **Frame times are recorded again in games where a mod such as ReShade replaces DirectX's dxgi.dll**, so the game
  page's frame-time graph and the Before and after panel show there. 1.2.5 lost them in those games.
- The recorder records while the game's exe is the one SCSKiller last checked for anti-cheat, and no anti-cheat is next
  to it or in the game's folders above it. A mod's files appearing in the game folder no longer stop it until the next
  check. SCSKiller still never puts it in an anti-cheat game, and takes it out the moment it finds anti-cheat: when it
  scans, when the game exits or updates, and every 2 minutes while it runs. The `scskiller.armed` file is gone from game
  folders.
- **Forza Horizon 6** contains anti-cheat. It counts as an anti-cheat game from Steam, the Xbox app or added by hand,
  so the recorder stays out unless you enable it at your own risk. SCSKiller isn't responsible for bans.
- **A VAC game in a folder whose path is longer than 260 characters** counts as VAC, so the recorder stays out. Its VAC
  check could miss it before.

### Known issues

- A portable copy made from another 1.2.5 portable copy's data folder takes over that copy's recorders. 1.2.5
  recorders don't record which copy placed them.
## [1.3.0-alpha.1]

### New

- **Official recordings**: SCSKiller's own recordings of a game version, marked "Official recording" on the game page.
  Each is for backers, for every Patreon supporter, or for everyone without signing in.
- **DirectX 11 games compile on AMD**. Their vertex shaders need a recording, so on AMD the recorder now records DirectX
  11 games too, and the community database shares what it learns.
- **Engine support updates from the server**: the rules that decide what a game compiles can be fixed or extended
  between releases. Only rules SCSKiller signed are used, and a change rebuilds only the games it covers, while the PC
  is idle.
- **Riot Client**: its games are found (VALORANT, League of Legends, Legends of Runeterra, 2XKO). They're never
  recorded, as anti-cheat games.
- **Naughty Dog games** (The Last of Us Part I, UNCHARTED 4: A Thief's End, UNCHARTED: The Lost Legacy): SCSKiller
  reads their shaders. They compile after a recording. The two UNCHARTED games of the Legacy of Thieves Collection show
  as two games, each compiled for its own exe. Listed as one, the collection compiled for an exe that never runs, so
  the driver cache it filled was one neither game reads. One recorder, on UNCHARTED 4's page, records both.
- **God of War Ragnarök** compiles without a recording on NVIDIA. **God of War (2018)** compiles its DirectX 11 shaders
  without a recording (on AMD, all but the vertex shaders).
- **Ghost of Tsushima DIRECTOR'S CUT** and **Ratchet & Clank: Rift Apart** compile without a recording on NVIDIA, and
  after a recording on AMD. **HELLDIVERS 2**'s shaders are read too, but GameGuard keeps the recorder out, so it can't
  compile yet.
- **Horizon Zero Dawn Complete Edition** and **DEATH STRANDING DIRECTOR'S CUT**: SCSKiller reads their shaders. They
  compile after a recording.
- **Overwatch (Steam)** compiles its DirectX 11 shaders without a recording (on AMD, the pixel and compute shaders).
  Its Steam install counts as an anti-cheat game, so the recorder stays out (thanks @JohnsonRan).
- **Source 2 games** like Counter-Strike 2 compile their DirectX 11 shaders without a recording (on AMD, all but the
  vertex shaders). Based on the notes in issue #13.
- **Notifications** under a bell at the top: news, notes on new rules and links, only ones SCSKiller signed.
- **"What's new"** lists a version's changes once after an update. About shows it again.
- **Max compile** on a game's page compiles every combination of that game's shaders, once. It takes longer and uses
  more disk space, but potentially fewer shaders compile while you play, which seems to help most on AMD. Nothing is
  kept for the game: its next Compile is a standard one. It replaces Compile everything.
- **Maximum mode suggested per game**: a game whose Maximum mode is known to help on your GPU says so on its page, with
  "Turn on for this game". It turns Maximum on for that game only. On AMD, Maximum adds at most as many pairings as the
  game's plan has, and at most about 2 GB of the shared cache. Maximum stays off by default.
- **AMD cache use**: Settings and the Cache page show the DirectX 12 cache against the driver's fixed 16 GB, and say when
  it's nearly full. A game's Maximum suggestion says how much it would add at most.
- **Cache page**: every shader cache on the PC with its size, by installed game, uninstalled game, old driver, earlier
  SCSKiller compiles and other GPU vendor, and the folder the driver reads and writes it in. Delete an item, a group or
  everything there. After a driver update it offers to free what the new driver no longer reads.
- A game marked not supported after it was compiled offers to clear the cache compiled for it.
- A driver cache folder moved to another drive with a junction is followed: Settings shows that drive's free space, and
  a link whose drive is missing holds compiles instead of marking games as cleared by the driver.
- **Library sorting and filters**: the Game, Pipelines and Cache headers sort each section, and a status filter shows
  only games that aren't supported, are ready to compile, are compiled, or were compiled by SCSKiller.
- **Game folders**: add a folder of games no store lists, and SCSKiller finds each game in it at every scan. Games added
  by hand or found in a folder can be renamed.
- **Scan progress** shows per store and per game, and a big library shows its last list at once while the scan runs.
- **New shaders compile on their own** if you choose: when a compiled game gains pipelines from its recording, the
  community database or an update, SCSKiller compiles them while the PC is idle, never while you play. Set it in
  Settings, or per game on its page. The default still only notifies you.
- **Theme**: Light, Dark or the Windows setting, in Settings.
- **A click on the tray icon** opens a small window with SCSKiller's status and the compile's progress, and Pause, Open
  and Quit. The icon's tooltip shows the status too.
- **"Share diagnostics to help fix problems"** in Settings (off by default): when a compile fails or the driver rejects
  pipelines, SCSKiller sends error codes and pipeline hashes, nothing else. "What's shared" in Settings lists it all.
  SCSKiller asks once, in the welcome or at the first start after the update.
- **Shader hash sharing** also sends one report after a compile: which pipelines still compiled at the next launch, with
  your GPU model and driver.
- **Compile progress on the game page**: while a game compiles, its page shows the queue's progress and the time left.
  While it waits, it shows its place in the queue.
- **Edit the compile queue in bulk**: select several games with Ctrl+click or Shift+click, then Remove selected. Clear
  queue removes every game but the one compiling.
- **Pick the DirectX** of a game when SCSKiller only guessed it: Auto, DirectX 11 or DirectX 12 next to its engine on the
  game page. Its plan is rebuilt for the pick. Your recording of the game wins over a guess, and over the pick. Unity games
  built for both count as a guess now, and a change of DirectX always rebuilds the plan.
- **GPU picker** in Settings, on a PC with more than one NVIDIA or AMD GPU: SCSKiller compiles for the one you pick
  instead of the one with the most video memory. It switches after a restart.
- **Earlier sessions** on a game's page: the last 20 launches with frame times, each with its date, play length, 1%
  low and stutters.
- **Send report**: once a game was compiled and played, or couldn't be compiled, its page can send an anonymous
  report of how it ran. Players with a similar system then see what others reported on the game's page.

### Faster and steadier

- **A new community recording** for a game you compiled rebuilds its plan while the PC is idle, and the game shows how
  many more pipelines it can compile.
- A game whose engine SCSKiller reads better says "SCSKiller can compile more" instead of "Game shaders changed".
- When new rules more than double a game's plan, the compile after a driver update leaves it for you to start.
- **A recompile compiles only what isn't compiled yet**, so adding a recording or turning on Maximum mode is quicker. It
  compiles everything again after a driver update, when the driver's cache lost files, or when the game still compiled
  after the last run.
- On NVIDIA, a game no longer asks to compile again after each session for pipelines whose shaders its last compile
  already compiled.
- On NVIDIA, compiled ray tracing pipelines that a game creates on other threads (The Witcher 3) are found in the cache
  at launch instead of compiling again.
- The Library holds still: a game that changes is updated in its row, the list keeps its place when a game moves to
  another section, and the scan's status line no longer pushes the list down.
- **Less memory with big community recordings**: a download is checked as it decodes and kept compressed, and a
  compile no longer holds every recorded shader in memory. With a 250,000-record recording, the download takes about
  45 MB instead of 1.1 GB and the compile's peak about half.
- **Bigger community recordings on NVIDIA**: up to 500,000 pipelines per game, once the community database serves
  them. AMD keeps reading up to 250,000.
- **Building a plan can no longer run forever or take all the memory.** A plan that outgrows half the PC's RAM or 30
  minutes stops with a clear error, and the next compile plans again. Building the plan of a game with many shader
  layouts is much faster.
- With "Closes to System Tray" on, closing the window no longer sometimes quits SCSKiller.
- **Updates**: after a failed download, SCSKiller waits longer before the next try (1 hour, then up to 12). "Check for
  updates" still tries at once.
- A **partly compiled** game's "Last compile" card says what the refused pipelines mean and what helps, and the
  coverage ring of a compiled game no longer counts the pipelines the driver refused.
- While a compile runs, the queue and the game page say "memory limit: 4 of 32 threads" when the compile's memory limit
  runs fewer threads than set.
- **Pipelines that crash the GPU driver** are all found and skipped in one compile (The Witcher 3 on AMD). Before, a
  compile stopped after 20 driver crashes, and a game with many of them needed several clicks on Compile. A compile
  that still stops says how many it skipped, and compiling again continues where it stopped.
- **Pipelines that make the GPU driver fault** (PRAGMATA on AMD) are found and skipped the same way. Before, a compile
  skipped them only for that run and stopped after three, so every later compile met them again.
- The "Last compile" card says "Nothing failed." also when it skipped pipelines whose shaders aren't in this version
  of the game.
- **Start with Windows**: a portable or test copy of SCSKiller no longer removes the installed copy's autostart, or
  moves it to itself, when it starts. A copy changes another copy's entry only when you turn the setting on in it.
- **NVIDIA Auto Shader Compilation** in Settings: Apply changes only what differs. With the feature already on it says
  so, with no administrator prompt, instead of failing to set up NVIDIA's task again. An error says which step failed
  and whether the setting was saved.
- **app.log** in SCSKiller's data folder keeps what the app did (scans, community sync, compiles and the recorder) for
  a bug report. Your user name and profile folder are masked, and it takes at most 2 MB.

### Games

- **GTA V Enhanced** compiles on NVIDIA without a recording. BattlEye still keeps the recorder out.
- **Resident Evil Village** and **PRAGMATA** compile on NVIDIA without a recording.
- **Dragon's Dogma 2**: SCSKiller reads its game files, which it refused before.
- **Games that build all their shaders at the main menu** (Horizon Forbidden West, Detroit: Become Human) show as not
  supported, with that reason.
- **Encrypted Unreal games with anti-cheat** (VALORANT and others) get their key from the exe file on disk, so
  SCSKiller can read their files. Reading the key never touches the running game.
- **Encrypted Unreal games** take their correct AES key where it was refused or opened only some files: games with
  their own decryption (Dead by Daylight and other forks), files under a named key, and readable file lists whose files
  are encrypted. The key is also found in exes built for AVX. When the exe is protected (SteamStub, Themida, VMProtect,
  Enigma, or Denuvo), the game page says so instead of only asking for the key.
- **Cyberpunk 2077** shows its engine as REDengine 4, and Alan Wake 2 and CONTROL Resonant as Northlight 2.
- **Cyberpunk 2077 on Steam** is found with its own exe, the one its launcher configuration names, not REDprelauncher.exe.
  The recorder goes beside the game, and the game no longer counts as closed once the launcher exits.
- **Returnal** no longer compiles tessellated pipelines the driver rejects.
- Games whose shaders carry their own root signatures (Naughty Dog, RAGE, FromSoftware and others SCSKiller reads
  directly) no longer compile pipelines the driver rejects, where a shader uses resources the root signature lacks.
- **The Last of Us Part I**: a compute shader the game ships with several root signatures compiles once for each of
  them, instead of only the first.
- **War Thunder** is on the known shader-stutter list, and no longer stays on "Needs rebuilding" when nothing new comes
  of it. On DirectX 12 it also compiles its shadow and depth passes.
- **ELDEN RING NIGHTREIGN** with its Digital Artbook & Mini Soundtrack installed no longer shows as a DirectX 11 Unity
  game. SCSKiller took the artbook's app for the game. It now follows the exe the game's EasyAntiCheat settings start.
- Unreal Engine 4.25 games no longer show "not tested on this engine version yet".
- **Unreal versions**: 5.7 and 5.8 games no longer show as 5.6, and a game on a newer Unreal than SCSKiller knows says
  "Unreal 5.9 isn't supported yet" instead of compiling as 5.8. A version SCSKiller can only guess shows "(guessed)", and
  a game whose Unreal version changes rebuilds its plan.
- DirectX 11 shaders that read a multisampled texture with more samples than the GPU supports compile.
- **Games started through EasyAntiCheat's bootstrap** (Gears of War: E-Day and Sea of Thieves from the Xbox app) compile
  for the game's own exe, the one the anti-cheat's settings name, instead of the bootstrap. An earlier compile shows
  "Needs rebuilding".
- A game's page says the game may still run its own shader step when it starts.
- A not supported game no longer shows its shader coverage after Clear cache.

### Mods

- **ReShade and HDR mods** like RenoDX show as a tag in the Library and on the game page, and "Compiles through RenoDX"
  under the game's status.
- Luma mods no longer make the game page say some pipelines aren't precompiled (they change no DirectX 12 pipeline).
- When a mod loads a copy of the recorder from another folder, where it can't record, the game page says so.
- **ReShade chaining another mod** through ReShade.ini's `[PROXY] ProxyLibrary` (OptiScaler or a DLSS mod under another
  name) no longer compiles through a copy of ReShade that lacks that mod's DLL. With a RenoDX mod that changes every
  pipeline, the game page says how to keep that mod under another name and turn the proxy off. One that replaces some
  shaders compiles without ReShade.

### Recorder and anti-cheat

- **Recording in games with anti-cheat, at your own risk.** Until now the recorder always stayed out of these games, so
  they never got a recording of their own. Settings has "Allow the recorder in games with anti-cheat (at your own
  risk)", off by default. A game that contains anti-cheat then offers "Record a session (at your own risk)" on its page,
  and each session is confirmed before it starts. Games with anti-cheat can treat the recorder as cheating and ban your
  account, and no game is known to be safe. SCSKiller removes every file it places when the game exits, or after a
  crash at your next logon.
- **Frame times with frame generation on** (FSR 3): the graph shows the frames the game rendered. Before, the recorder
  measured no frame times while frame generation was on.
- **Frame times in games with NVIDIA Streamline** (Ghost of Tsushima, Ratchet & Clank) on every GPU. Before, the
  recorder measured none in them unless DLSS frame generation was on.
- On NVIDIA, the recorder no longer makes a game crash as it closes.
- **The recorder no longer crashes a game now and then as it sets up its hooks.** It moved a few of a function's first
  instructions to make room for its jump, so a game thread paused among them could resume mid-jump and crash. It now
  replaces only the first instruction.
- **A game that makes its own crash dump or log folder** (Horizon Zero Dawn's coredumps) no longer turns the recorder
  off. Any new folder that wasn't empty turned it off until the install was checked again, so the first recorded
  session was lost. A new folder now keeps it on when it holds only data files, with no code or anti-cheat in it.
- **A portable or test copy** keeps its own recorders: each copy's armed list is in its own data folder, and a copy
  never arms, updates, removes or imports the recording of a recorder another copy placed. Before, a copy with a new
  data folder took every recorder for its own and moved the game's recording out of the game folder. Turning the
  recorder on in a copy takes it over. Recorders placed before this version belong to the installed copy.
- The recorder's log no longer holds your user name or full folder paths, so it's safer to share in a bug report.
- **Valve Anti-Cheat (VAC) games** never get the recorder. They still compile from their files.
- **NetEase games whose anti-cheat leaves no file in the game folder** count as anti-cheat games, so the recorder stays
  out of them: the Steam installs of Once Human, Marvel Rivals, FragPunk, Sword of Justice and NARAKA: BLADEPOINT, and
  Once Human from any store by its exe.
- **ELDEN RING added by hand** gets the offline session too, when the EasyAntiCheat settings beside its exe are
  ELDEN RING's.
- **Euro Truck Simulator 2 and American Truck Simulator** count as anti-cheat games on a PC with TruckersMP.
- **Xbox app Unreal games that start through a small launcher** (Senua's Saga: Hellblade II, Avowed) record. The
  recorder moves beside the game's own exe once the game is closed.
- **A Steam game you uninstalled loses the recorder too.** Steam leaves the folder behind when it holds files it didn't
  install. A scan once the game is gone removes SCSKiller's own files from it, and the folders they leave empty. The
  game's recording is kept.

## [1.2.5] - 2026-10-10

### Faster and steadier

- **Compiles with long chains of ray tracing pipelines keep going**, as in The Callisto Protocol and Black Myth: Wukong
  with a community recording. Each pipeline in such a chain waits for the one it builds on, and 1.2.4 counted that wait
  as a driver hang. The compile then restarted the same stretch again and again with fewer threads. A wait for another
  pipeline no longer counts as a hang.
- **A compile no longer stays at about 100% without finishing.** At its end, the driver could hang while letting go of
  a ray tracing pipeline, and the compile waited for it forever. That last step now has a time limit, and the compile
  ends.
- **Compile clicked while SCSKiller checks a game's plan in the background** runs with your compile settings. 1.2.4 kept
  it as background work, at idle priority and with the background thread count, so it could run far slower than set.
- **Reading a game's shaders in the background no longer slows a compile.** 1.2.4 does that work when a game's recorder
  is set up, at a higher priority than the compile, and it slowed a running compile by up to a fifth. It now runs
  below the compile, stops when any compile starts, and waits until the queue is done.
- **The cache size shown is the size on disk again**, as in 1.2.3 (NVIDIA). 1.2.4 opened the driver's cache files to
  measure how much of them was in use, and showing a size doesn't need that. SCSKiller no longer touches the driver's
  cache files for this.
- **"Not compatible"** for a game whose shaders the driver keeps under a different cache than the one SCSKiller's
  compile fills, as seen on AMD with Black Myth: Wukong. 1.2.4 kept offering to compile it again, which could never
  help, so the same game was rebuilt over and over. The game page now says so and suggests clearing its cache to free
  the space. A driver update offers the game again, even after Clear cache, since the new driver may read the cache
  SCSKiller fills.

### Recorder and anti-cheat

- **The recorder no longer crashes games with a dxgi.dll mod in their folder**, like OptiScaler in Onimusha: Way of the
  Sword. It hooked the mod's own swap chain as if it were Windows', which could crash the game. It now measures frame
  times only through Windows' own DirectX.
- **More anti-cheat is recognised**: NetEase Protect (Aniimo) and Halo Infinite's Arbiter. The recorder must never load
  into a game with anti-cheat, so it now stays out of these.
- **Cleaning up after an offline session deletes only SCSKiller's own files.** It deleted every file by the names the
  session could create. So after a session that ended abnormally, it could delete a mod's d3d12.dll or your own
  steam_appid.txt put in the game's folder later. A file SCSKiller didn't write is now left alone.

### Mods

- **Special K** beside a game's exe is no longer taken for ReShade. SCSKiller copied it as if it were ReShade, so the
  compile of a game that loads RenoDX through Special K failed. That game now shows why it can't compile.

## [1.2.4] - 2026-10-08

### New

- **Marvel's Guardians of the Galaxy** compiles without a recording on NVIDIA.
- **The window** opens at the size and place you closed it, maximized if it was (thanks @kaeldrin-gh).
- **The queue warns** when one game's compile adds more than 16 GB: "This compile adds about 32 GB. Very large caches
  can make the game start slower and use more memory." When the queue goes past the cache limit too, it says so ("and
  takes the cache past your 16 GB limit, so older games may be evicted"), and on AMD that the driver's cache can't hold
  it all.
- **Bigger community recordings** are read, up to 250,000 pipelines per game, for games like S.T.A.L.K.E.R. 2.
- **Games without shader stutter** show "No shader stutter". They stay out of "Add all ready", driver-update rebuilds
  and the recorder by default, and can still be compiled from their page. The list starts empty. The server can fill
  it, and mark a game not supported with a reason, without an app update. STAR WARS: Galactic Racer shows "Not
  supported yet · An app update is needed for support".

### Faster and steadier

- **Less memory**: SCSKiller gives memory back after a scan or compile and when it's in the tray. It keeps at most about
  30 MB of pipeline lists instead of up to 250 MB, and once minimized or in the tray it hands its working set back to
  Windows. `SCSKiller.exe --memory`, run while the app is open, writes what it holds to memory.log in its data folder,
  for a bug report.
- **A crash leaves a report**: when SCSKiller closes on its own or can't start, it writes what went wrong to crash.log
  in its data folder, and a failed start shows where that file is.
- A compile no longer leaves about 0.5 MB behind in Windows' shader cache folder each time.
- **A failed compile** says which step failed and keeps the whole error in compile-error.log in the game's SCSKiller
  folder, for a bug report (found by dkflint723).
- The compile helper reports its error instead of crashing when it can't start an Xbox app game's compile, no longer
  retries a compile process that died before it finished, and passes on a folder ending in a backslash correctly (found
  by dkflint723).
- Reading a large Unity bundle keeps only the blocks it read last in memory, not the whole bundle (found by dkflint723).
- A recording test no longer fails at random when a file is rewritten within one clock tick (thanks @kaeldrin-gh).
- Sharing notices a recording rewritten at the same size within one clock tick, instead of using what it read before
  (thanks @Dmi-3).
- **Loading hitches** no longer count as shader stutters on the game page: a load compiling on many threads, or a
  precompile after a splash screen, is loading.
- **A game's own precompile and its loading screens** no longer count as compiles while you played, also when the
  recorder can't time frames (frame generation on): they show as compiled while the game started or loaded.
- **"The compile didn't help"** shows on NVIDIA when a game's first launch after a compile still compiles what
  SCSKiller compiled: the game needs a custom loader. It isn't compiled again unless you ask, and its page says the
  unused cache can be cleared.
- **A nearly full 4 GB NVIDIA cache file** is flagged on the game page.
- **Games compiled for DirectX 11 and DirectX 12**, like War Thunder, say so: "Compiled for DirectX 11 and DirectX 12"
  on the page, "DirectX 11 and 12" in the Library, and where each API's shaders come from.
- **"Partly compiled"** shows when the driver rejected or crashed on more than a tenth of a compile's pipelines, with
  how many, instead of "Warmed". Its ring shows the share that compiled, and "Why?" explains it. A TEKKEN 8 compile
  on Windows 10 with 1.2.2 had about 180,000 of 258,000 rejected and still looked complete. A game the driver skipped
  only a few pipelines of stays "Warmed".
- A game on the **"Not supported yet"** or **"Not supported"** list shows that status on NVIDIA too, instead of
  "The compile didn't help".
- **Fewer compile notifications**: only for games you've compiled, only when there's enough new to compile (1% of the
  game's pipelines, at least 100, counted since the last notification), and at most once a day per game. A newer
  SCSKiller that can compile more of a game tells you once per compile, not again when the count moves. Updating
  doesn't bring back a notification you already had. Pipelines whose shaders aren't in your install are no longer
  counted as new, on the game page either, and new upscaler pipelines are called that, not "recorded".
- **After an update that plans more**, compiled games are checked for what it adds right away, one at a time and
  paused while you play, instead of once the PC is idle, so their pages show the new plan within minutes. A game with
  new pipelines to compile shows how much of its plan is already compiled in its ring, and a compile that just
  finished no longer brings a notification about the pipelines it compiled.
- The "still running" notice shows only the first time you close the window, not after every start.
- **Coverage and pipeline counts** leave out what can't be compiled on your PC: pipelines whose shaders aren't in your
  install, AMD-only pipelines on NVIDIA, pipelines that crash your driver, and shader combinations DirectX refuses to
  create.
- **Your texture filtering setting**: a community recording can hold the same pipelines from players with other
  texture filtering settings. With a recording of your own, SCSKiller compiles and counts only the ones at your setting.
  Without one, it compiles every setting the community recorded, since yours isn't known, and counts each pipeline once.
- The Library's games no longer vanish and come back for a moment while SCSKiller starts or a game's status changes.
- **"Stopped while … is running"** names the process and its path, so a leftover one can be ended in Task Manager.
- The Library's buttons sit on their own row, aligned right, and wrap in a narrow window instead of being cut off
  (thanks @JohnsonRan).
- The Library's Time column and the careful compile's "instead of" show how long the last compile took, as the game page
  does. Clear cache shows the same size as Disk space. A finished game in the compile queue opens its page when clicked,
  and Library in the sidebar goes back to the list from a game's page.

### Games

- **FINAL FANTASY VII REBIRTH** is fully covered again. Its community recording mixes players with different texture
  filtering settings, and that made SCSKiller fall back to a guess that left out two thirds of its shader combinations.
  Its page also asked to compile 2,425 new pipelines that were no use. Any game whose community recording mixes
  settings like this is planned in full again, after one more compile.
- **Cyberpunk 2077** and other games where DLSS loads DirectX 12 before the recorder no longer ask for a recording that
  never comes (NVIDIA). They still compile from their files and the community database.
- **Ready or Not** compiles again: shader libraries over 2 GB are read (found by dkflint723), and it's recognised as
  Unreal Engine 5.
- **3on3 FreeStyle: Rebound** is recognised as Unreal Engine 5.1, not 4.27. When an Unreal game's files don't tell its
  version, SCSKiller takes it from the exe's version number, and the game page and Library mark it "(guessed)". The
  game page also says when a compile rests on a guessed version or DirectX.
- **Wuthering Waves** is recognised as Kuro's fork of Unreal Engine 4.26, not 4.27, and shows as not supported instead
  of asking for an AES key that wouldn't help: SCSKiller can only learn how this engine builds its pipelines from
  a recording, which its anti-cheat blocks. Its files, 5.6 GB shader library included, now read correctly (thanks
  @JohnsonRan).
- **Anti-cheat games that need a recording** show "Not supported" instead of "Not supported yet", with the same reason:
  their anti-cheat blocks the recording they need. Gears of War: Reloaded is one, and so is every anti-cheat DirectX 12
  game on AMD without a community recording. Games an offline session can record, such as ELDEN RING and ARMORED CORE
  VI, show "Needs an offline session" instead until they have a recording: "EasyAntiCheat blocks the recorder, but you
  can record at your own risk with an offline session."
- **Xbox app Unreal games** are never compiled for a developer tool or console build in their package.
- **Xbox app Unreal games on AMD** compile into the cache the game actually uses, after you play them once. Before,
  their page kept saying "Needs rebuilding" after every compile (Banishers: Ghosts of New Eden).
- **Resident Evil Village** is read as a DirectX 12 game. It was taken for one that may also run on DirectX 11, so its
  page said Ready and compiled DirectX 11 shaders it never uses, where it needs a 5-minute recording.
- **Scarlet Nexus** compiles again: a line in the game's own settings that the Unreal file reader can't parse no longer
  stops it (found by dkflint723).
- An Unreal game with **one shader library SCSKiller can't read** compiles from its other libraries, and the compile log
  names the one it skipped (found by dkflint723).
- **Encrypted Unreal games**: an AES key, found in the exe or typed in, is taken when it opens any of the game's
  encrypted files, not only the first (found by dkflint723). Anti-cheat games' exes are searched for it too: the file
  on disk, never the running game.
- **Newer Unreal games' shipped pipeline caches** are read, so the pipelines they list are compiled as the game pairs
  them. Gears of War: E-Day's lists 9,688 graphics pipelines.

### Recorder and anti-cheat

- **A first recording no longer fills up in minutes.** A game recorded before its first compile, which is every game
  that needs a recording and most DirectX 12 games on AMD, kept the bytes of every shader it built: SILENT HILL f
  reached the 256 MB limit after about two minutes. SCSKiller now reads the game's shaders in the background as soon
  as the recorder is on, and again after a game update, so the recorder keeps only their hashes.
- **The recording limit is 1 GB by default**, up from 256 MB. A limit you picked yourself stays.
- **"Played, nothing recorded"** shows in the Library when a game was played with the recorder on and nothing was
  recorded, instead of "Needs a 5-min recording". The game page says why: another exe of the game's folder ran, the
  game changed since the recorder was set up, or the recorder didn't load, which asks for a report (found by
  dkflint723).
- **Games with two exes in one folder** record and compile for the exe that actually runs: after one launch of the
  other exe, the recorder is set up for it. Ubisoft games with a Ubisoft+ exe, like Assassin's Creed Valhalla, start
  from the regular exe, and a Ubisoft+ subscriber's Plus exe is followed the same way.
- **Ubisoft Connect games** show their name: an install folder ending in "/" left it empty. Ubisoft Connect's file
  written at every launch no longer makes SCSKiller check the game's folder again.
- On AMD, **DirectX 11 games** no longer stay on "Needs a 5-min recording": the page says a recording helps only on
  DirectX 12.
- Removing the recorder always takes out its d3d12.dll and puts back the mod it was chained to.
- The recorder switches off right away when a game's folder changes, also while the command line runs.
- **Blizzard games** installed by Battle.net but listed by another store, or added by hand, keep the recorder out:
  Battle.net's files in their folder count as anti-cheat, in the app and in the recorder (found by dkflint723).
- The recorder goes into a game's folder under a temporary name and is then renamed into place: a copy cut short by a
  full disk or a closed app can't leave a broken d3d12.dll that stops the game from starting (found by dkflint723).
- A recording whose end a power loss left zero-filled is cut back to its last whole record and recording goes on, and a
  ray tracing addition is no longer recorded against a released object (found by dkflint723).
- **How much of a game's cache is in use** shows under its disk space on NVIDIA, and in the Library's tooltip: "4 GB on
  disk · 2.6 GB in use". The driver reserves its cache files in doubling steps, so a cache that grew a little can
  take twice the space. Clear cache still frees the whole size on disk.
- **About and the README** say SCSKiller is used at your own risk.

### PCs and Windows

- **SCSKiller starts on older Windows 10 builds** that lack an update newer .NET apps need for a CPU security feature.
  There it closed right away at start.
- **The portable build** keeps its settings and data in a `data` folder beside SCSKiller.exe. Its first start copies
  your existing data there.
- Requests to SCSKiller's servers now name the app version, its update channel and the Windows build, and nothing that
  identifies you or your PC.

### Updates

- A failed update leaves a log: updates.log in SCSKiller's data folder.
- An install, update or uninstall step that fails no longer skips the others, such as taking the recorder out of your
  games. The failure goes to updates.log.

## [1.2.3] - 2026-10-06

### New

- **"Closing the window quits SCSKiller"** in Settings (off by default). A running compile finishes saving first, and a
  downloaded update installs.
- **"Scan games when SCSKiller starts"** in Settings (on by default). Turn it off and SCSKiller shows your last list,
  and reads a game again only when you refresh, when the game closes, or when Steam or the Xbox app installs or updates
  one.
- **Updates install by themselves**, the next time SCSKiller starts or when you quit it, never during a compile, a game
  or Windows shutdown. You can turn this off in About.
- **Ray tracing without a recording** for Unreal Engine 4.25 games like Returnal on NVIDIA. With a recording, Returnal
  also compiles the ray tracing it hadn't recorded.

### Faster and steadier

- **Much faster start**: only the games that changed are read again, at low disk priority. The list shows in about a
  second, and no more disk grinding after Windows starts.
- Pausing a compile no longer makes it fail when you resume.
- A compile no longer stops itself saying the game is running when it isn't.
- Large Unreal games no longer run out of memory while their shaders are read.
- One game with a broken file no longer stops every game from being listed.
- One broken Unreal shader no longer stops a compile at "Reading shaders".
- Game pages open without stutter, and Clear cache responds right away.
- Skipped pipelines after a compile show as a short line instead of a warning, that's normal.

### Games

- **Xbox app games** finally record, and their Unreal games are compiled for the real game exe instead of a launcher.
- **Dead Island 2** compiles and matches the game (it runs on its own Unreal Engine 4.25 build, on DirectX 12).
- **Unreal games with a launcher next to the exe**, like Returnal, are found as the real game, also when added by hand.
- **Monster Hunter Wilds** and other RE Engine games no longer get stuck on "Building plan".
- **The Witcher 3** no longer crashes at start with frame generation on (the recorder then skips frame times for that
  launch, it still records), and no longer asks for a rebuild after every session.
- **DRAGON BALL: Sparking! ZERO** and other encrypted Unreal games open without entering a key.
- **FINAL FANTASY VII REMAKE INTERGRADE** is recognised as DirectX 12 (or DirectX 11 if you launch it with -dx11 on
  Steam).
- **Neverness to Everness** is recognised as Unreal Engine 5.6.
- **Unreal Engine 4.25 games** also compile the pipeline list they ship with.
- Games with ray tracing turned off no longer ask for a ray tracing recording, and show as compiled.
- **After updating**: if you play Dead Island 2, Returnal or other Unreal games from the Xbox app, **compile them
  again**. Their earlier compile went to the wrong place.

### Mods

- **RenoDX with ReShade loaded by OptiScaler** compiles through the whole chain, and records.
- **Xbox app games with ReShade** in their package folder compile through ReShade and RenoDX.
- RE Engine games record with **REFramework** installed.
- Logs, crash dumps and shader caches next to the exe no longer turn the recorder off.
- When a mod setup can't be compiled, the game page tells you why and how to fix it.

### Recorder and anti-cheat

- **More anti-cheat is recognised**, so the recorder stays out: every Riot Games title (VALORANT, League of Legends,
  2XKO and more), Anti-Cheat Expert games like Delta Force, every HoYoverse game and EA Javelin games.
- Turning the recorder on no longer fails with "scskiller.ini is being used by another process".

### PCs and Windows

- **Older CPUs without AVX2** no longer close SCSKiller at "Reading shaders".
- **On Windows 10**, games that ship their own DirectX 12 version compile with it.
- **Maximum mode** explains itself: it seems to help most on AMD, and it takes longer and uses more disk space.

### Updates

- A downloaded update installs at the next start even when the PC has no network yet.
- One update server asking to wait no longer holds back every update check.

## [1.2.2] - 2026-10-04

### Games

- **Games you added by hand and recorded** that a store now lists (Zenless Zone Zero through HoYoPlay) compile from that
  recording again.
- **ELDEN RING's offline session** starts on a PC where the recorder was never used.
- An anti-cheat game says when the community database has a recording for it, and where to get it.

### Mods

- **The recorder stays on** when a mod writes a log, an ini, a screenshot or a save as the game starts (OptiScaler, DLSS
  frame generation enablers), so those launches are recorded.
- A mod's d3d12.dll that can't be renamed for "Record alongside" is named on the game page. For OptiScaler, the page
  says to rename it to dxgi.dll.
- A RenoDX game compiled before 1.2.1 no longer says an HDR mod was installed since. Compile it again to run through the
  mod.

### Recorder and anti-cheat

- **The game page says why** a launch wasn't recorded.
- A recorder moved next to the exe a game really runs (Stellar Blade) records from the very next launch.
- When a store takes over a game you added by hand, the recorder comes out of your copy and its recording joins the
  store's entry.

### Updates

- Going from a pre-release to the release of the same version checks every game again.

## [1.2.1] - 2026-10-04

### New

- **NCSOFT's PURPLE launcher**: its games are found. AION 2 shows as not supported (encrypted files, NCGuard
  anti-cheat).
- **HoYoPlay**: Genshin Impact, Honkai: Star Rail, Zenless Zone Zero and Honkai Impact 3rd are found. They're never
  recorded, every HoYoverse game has a kernel anti-cheat.
- **War Thunder** from Gaijin's launcher is found, and compiles without a recording on NVIDIA, on DirectX 11 and 12.
- **Control** compiles without a recording, ray tracing included. On NVIDIA its DirectX 11 shaders too.

### Faster and steadier

- **Less memory** while SCSKiller sits in the notification area, often hundreds of MB less.

### Games

- **Stellar Blade**: the recorder goes next to the real exe, not a patcher's copy, and SCSKiller follows the exe a game
  really runs.
- **Unreal Engine 5.8 games** (Fortnite, The Sinking City 2) compile instead of failing with "Arithmetic operation
  resulted in an overflow". 5.7 and later compile without a recording, marked "not tested on this engine version yet".
- **ELDEN RING and Nightreign** compile a few more pixel shaders without a recording, and on NVIDIA their ray tracing is
  built the way the game builds it.
- **The Witcher 3** on NVIDIA no longer counts pipelines it makes again every launch as new.
- **Dead Island 2**: shaders that don't decompress are skipped instead of failing the whole compile.
- **War Thunder** and other games with BattlEye's launcher at the top of their folder are found as the real game, and no
  longer show "Playing now" all the time.
- **ELDEN RING's** stutter note is "moderate": most of its big hitches aren't shader compiles.

### Mods

- **RenoDX games compile through ReShade and the mod**, so the compile matches the modded game, and they're recorded
  again. Installing, updating or removing the mod marks the game to compile again.

### Recorder and anti-cheat

- **Recording works again**: since 1.2.0 the recorder was installed but never switched on, so games stayed on "Needs a
  recording".
- **Warframe** counts as an anti-cheat game, so it's never recorded.

### Updates

- **Check for updates** button in About. Updates are checked every hour, and refreshing the Library downloads one right
  away.

## [1.2.0] - 2026-10-04

### New

- **Add a game by hand**: "Add a game…" in the Library takes any .exe, and a launcher is followed to the game it starts.
  Added games compile, record and play like any other, and their recordings aren't shared.
- **Record an offline session without EasyAntiCheat** in ELDEN RING and ARMORED CORE VI, at your own risk. Allow it on
  the game page, confirm every time, and the recorder comes out the moment the game exits.
- **Intel and other GPUs**: the Library says SCSKiller can't compile there yet, and why.

### Faster and steadier

- **"Compile"** on the game page compiles right away, instead of only adding the game to the queue.
- A compile can no longer pause or close an unrelated program.

### Games

- **ELDEN RING and Nightreign** compile their pipelines exactly as the game makes them, so a compile without a recording
  now saves real work.
- **Unreal Engine 5 games with hardware Lumen** compile their ray tracing from the game files, no recording needed.
- **Unreal Engine 5.0 to 5.4** ray tracing compiles without a recording on NVIDIA when laid out like 5.1 (Darwin's
  Paradox).
- A recording is checked as soon as the game closes, so games like SILENT HILL: Townfall stop asking for a 5-min
  recording after one was made.
- **Deep Rock Galactic** and other Unreal games that pick DirectX 12 in Steam's launch menu are recognised as DirectX
  12.
- **Ghostrunner** and other Unreal Engine 4 games that ship ray tracing compile for DirectX 11 and 12, and can be
  recorded.
- **The Witcher 3** compiles no longer try the pipelines NVIDIA's driver rejects.
- Xbox app games installed outside `<drive>:\XboxGames` are found.

### Mods

- **Games whose HDR mod changes every pipeline** (most RenoDX mods) aren't compiled or recorded, and the game page says
  so. Mods that replace only some shaders still compile.

### Recorder and anti-cheat

- **The recorder records only in games fully checked for anti-cheat**, and any change in a game's folder switches it off
  until the next check.
- **More anti-cheat is recognised**: NCGuard (AION 2), Anti-Cheat Expert, HoYoverse's, NetEase's, Nexon's BlackCipher,
  AhnLab HackShield, PunkBuster, EQU8, Denuvo Anti-Cheat, and more of XIGNCODE3 and nProtect GameGuard.
- For a game added by hand, the anti-cheat check also looks in the folders above it (BattlEye above War Thunder's
  `win64`).
- In the frame-time graph, ray tracing that takes 25 ms or more counts as a shader stutter.

## [1.1.2] - 2026-10-03

### New

- **The Witcher 3 ray tracing** on NVIDIA: once a recording has a session with ray tracing on, the materials the game
  adds while you play stutter far less.

### Games

- **The Witcher 3** and other games that use NVIDIA's shader extensions now find SCSKiller's compiled pipelines on
  NVIDIA. Compile them once more.
- A game's last session no longer loses its frame-time graph when two pipelines finish at the same moment.

### Updates

- A downloaded update also installs when SCSKiller starts.
- About links to the website, the source code and the Patreon page.

## [1.1.1] - 2026-10-03

### New

- **The Witcher 3** on DirectX 12 compiles without a recording on NVIDIA.
- **Refresh in the Library** fetches everything from the server: stutter lists, community recordings, shared packs, your
  supporter status and updates.
- **Better plans**: every game's plan is rebuilt once while the PC is idle, and games with tessellation, geometry
  shaders or ray tracing get more pipelines.

### Faster and steadier

- **A GPU driver update** is noticed within minutes, without a restart.
- The app, the command line and the scheduled task no longer compile the same game at once.
- A stopped and continued compile reports what failed before the stop, and starts over after a driver update.
- A damaged recording fails only its own game, not the whole library scan.
- A recording imported just before SCSKiller closed or crashed always goes into the next compile.
- A failed compile's message points to a log that's still there, and a compile that fails while watched no longer keeps
  running.
- SCSKiller waits before asking again when the community database is down, and ignores a broken list from the server.
- Signing out and in again no longer signs the new sign-in out.

### Games

- **Games started through a launcher**, like The Witcher 3's REDprelauncher, are found by the exe the launcher starts,
  and the recorder moves there.
- **Games are found more reliably**: Epic games, Xbox games on any drive, and games installed under a folder named like
  Setup or Redist.
- A game updated since the last scan, or whose shipped pipeline list changed, gets a fresh plan.
- On NVIDIA, a game whose shader cache was reset shows as needing a compile again.
- A RE Engine game with a patch file that can't be read shows as not supported instead of compiling outdated shaders.
- Clear cache works with an accented user name, and deletes read-only cache files too.

### Mods

- A recorder chained to a mod whose install was cut off gets its settings back, so the mod loads.

### Recorder and anti-cheat

- **Frame times**: failed presents no longer count as frames, and more games are recorded.
- A launch played while a recompile ran no longer judges that compile.
- Turning "Share anonymous shader hashes" off stops an upload already under way.

### Updates

- Stable updates are found while the SCSKiller server is down. Beta and alpha also offer a newer stable release.

## [1.1.0] - 2026-10-02

### New

- **Shared packs for FidelityFX and XeSS**: a game that ships an upscaler version someone recorded on the same GPU
  vendor gets those shaders compiled, free, without an account or a recording.
- **Unreal games** also use the pipeline list they ship, and Unreal Engine 5.4 is a tested engine version.
- **The installer is SCSKiller-Setup.exe**, so its download link always gets the latest release.
- Every game's plan is rebuilt once while the PC is idle. Games compiled before ask for one more compile.

### Faster and steadier

- A compile stopped after a GPU driver fault no longer skips the unfinished pipelines next time, and a compile that
  hangs at exit is always ended.
- Quit waits for a compile you just removed from the queue to save its cache.
- "Compile queue" also starts games queued for when the PC is idle.
- DirectX 11 hull shaders that use all 32 inputs compile.
- A broken entry in a recording or a community download leaves out only its pipelines, not the whole plan.
- A recording stays whole when the app and the command line use it at once.
- `scskiller compile` refuses an option it doesn't know, and Ctrl+C before a compile starts isn't a success.
- The driver-update notification's buttons work after SCSKiller was closed.

### Recorder and anti-cheat

- **The recorder no longer crashes games** with overlays, mods that unload DirectX 12, or NVAPI calls, and no longer
  holds up a game's exit.
- **Anti-cheat is found anywhere in a game's install**, hidden folders included. A folder SCSKiller can't read counts as
  anti-cheat.
- **SCSKiller never writes into a game's folder while it runs**, also when a launcher started it.
- A damaged recording no longer makes a game use gigabytes of memory at start, and a full disk no longer breaks a
  recording.
- Turning the recorder off removes all its files from the game folder. An scskiller.ini you edited stays.
- Frame times: a slow load is no longer a shader stutter, and freezes of 5 s or more count in the 1% low.
- "Last time you played" no longer counts the shaders a game compiles while it starts.

### PCs and Windows

- On AMD, the Library's cache bar compares the DirectX 12 cache with its limit.
- Clear cache deletes only the Windows shader cache's own files. On NVIDIA it refuses while another game has the same
  exe name (they share one cache).

### Updates

- Choosing another update channel no longer installs an update from the one before.
- An update download gives up when no data arrives for two minutes, and stops at the size the signed feed gives.

## [1.0.0] - 2026-10-01

The first public release.

### New

- **Compile a game's shaders** into the NVIDIA or AMD driver cache before you play, so the game doesn't stutter
  compiling them. From the app or the `scskiller` command line.
- **DirectX 12 games** on NVIDIA and AMD, and **DirectX 11 games** on NVIDIA.
- **Finds your games** from Steam, Epic Games, EA app, GOG, Ubisoft Connect, Xbox and Battle.net.
- **Reads shaders from the game files** for Unreal Engine, Unity, FromSoftware and RE Engine games, and scans other
  games for shader containers.
- **Community database** for Patreon supporters: games compile from other players' recordings. Without a membership, a
  game still says when the database has one.
- **Recompiles after a GPU driver update**, on a schedule or by hand, and notices game updates.
- **Play button** for Steam, Epic Games, Xbox, GOG (with GOG Galaxy) and Ubisoft Connect games. It waits while the game
  compiles, and never starts an anti-cheat game.
- **Clear cache** removes a game's driver cache, Windows shader cache and Unreal's own pipeline cache.
- An installer and a portable version, both update themselves.
- Status texts in plain words, like "no recording needed".

### Recorder and anti-cheat

- **An optional recorder** captures the pipelines a game builds while you play, so the next compile covers them. It can
  record alongside a d3d12.dll mod like ReShade, and is never offered for anti-cheat games.
- **Frame-time graph** of the last session, with shader stutters told apart from other hitches, the 1% low and the
  slowest frames. Frame times stay on your PC.
- A recording keeps only hashes of the shaders a game ships. Set its limit per game in Settings (256 MB by default) or
  clear it on the game page.
- **"Share my shader hashes"** (opt-in) uploads the hash-only form of your recordings under an anonymous device, never
  linked to your account.
- A notification tells you when compiled games have new pipelines to compile.
- Uninstalling SCSKiller removes the recorder from every game folder, and puts back a mod it recorded alongside.

### PCs and Windows

- **On NVIDIA**, compiles run about three times faster.
- **On AMD**, the queue warns when games won't fit in the driver's 16 GB cache, and a game whose first launch still
  compiled a lot offers a careful compile.
- An anonymous daily check counts active installs, with the app version and GPU vendor only. Turn it off in Settings.
