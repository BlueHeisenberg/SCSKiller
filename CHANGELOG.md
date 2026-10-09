# Changelog

All notable changes to the SCSKiller app and command line. Versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### New

- **The Last of Us Part I** and **The Last of Us Part II Remastered** compile without a recording on NVIDIA: SCSKiller
  reads the shaders in Naughty Dog's archives. Part I's main-menu shader build then finishes in about a minute. On AMD
  they need a recording, like other games.

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
  encrypted files, not only the first (found by dkflint723).
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
