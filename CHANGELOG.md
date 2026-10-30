# Changelog

All notable changes to the SCSKiller app and command line. Versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

**New**

- Linux: lists and compiles the Windows games you added to Faugus Launcher, on the Proton build Faugus runs them with.

**Fixed**

- Linux: a game whose prefix a launcher set up without a Steam folder (an empty STEAM_COMPAT_CLIENT_INSTALL_PATH, as Faugus
  runs umu) is no longer refused as needing a first run from its launcher.

## [1.4.3] - 2026-10-30

**Improved**

- Problem reports include how well SCSKiller rebuilds the game's root signatures from a recording.

**Fixed**

- Turning Record while I play on takes over a recorder another SCSKiller copy or an older version put in the game's
  folder, instead of switching back off.

## [1.4.2] - 2026-10-30

**This is a very big update**, the first stable release of 1.4, with everything from the 1.4.0 alphas and betas.

### New in 1.4.2

**New**

- **SCSKiller on Windows uses the same app as on Linux.** Your settings, games and shortcuts stay as they are.
- About lists the problem reports you sent from this version on, with their Report IDs.
- The mouse's back and forward buttons move between pages.
- In DirectX 12 Ubisoft Connect games on Windows, the Before and after countdown shows in the game too, not only in
  SCSKiller.
- The Before and after countdown shows in the game with frame generation on, with DLSS Frame Generation and FSR 3, with
  HDR, and beside the Steam, Ubisoft Connect and RivaTuner overlays.
- On Linux, DirectX 11 games under Proton compile on NVIDIA.
- On Linux with NVIDIA, DirectX games of the Flatpak Steam compile, inside that Steam's sandbox.
- **Warhammer 40,000: Space Marine 2** is supported. It compiles from a recording. The game contains anti-cheat, so
  enabling the recorder there is at your own risk, and SCSKiller isn't responsible for bans.
- Godot 4 games on DirectX 12 are detected and compile from their recording, and Godot 4 games on Vulkan compile as
  other Vulkan games do. Godot 3 and Godot 4's Compatibility renderer run on OpenGL, which waits for OpenGL support in
  SCSKiller.
- The compile shows when it started and says when it's on its last pipelines, the game page says when the cache is
  complete, and a notification tells you when a compile you started finishes.
- A store game's page lets you choose its exe when SCSKiller picks the wrong one, and Auto goes back to SCSKiller's
  pick.
- A wrong exe pick for a game can be fixed from SCSKiller's side, without an app update.
- Unreal Engine 4 games that write their own pipeline cache compile without a recording on AMD, anti-cheat games
  included.
- SCSKiller detects Starfield Luma and asks for a new compile after it's installed, changed or removed.
- Luma add-ons that change a DirectX 11 game's shaders ask for a new compile, and that compile runs through Luma.
- The game page says when Windows' automatic disk cleanup is set to clear the DirectX shader cache, if a compiled game's
  shader cache shrank or is gone.
- About shows the update channel it actually checks, and why, when your Patreon membership doesn't include the build's
  channel.

**Improved**

- Fortnite and other Unreal Engine 5 games that keep shader libraries without file names compile far more of their
  shaders (Fortnite: about 7 times the pipelines).
- Fortnite's DirectX 11 and Performance modes compile far more of their shaders (about 23 times the DirectX 11
  pipelines).
- Handing a compile to the game's shader cache is several times faster on big games.
- The "Handing the compile to the game's cache" bar shows "Finishing: writing into the game's cache" while a big merge
  runs past its estimate, instead of sitting at 100%.
- Handing a compile to the game's shader cache stops when you pause, remove or quit, waits for enough free disk space
  instead of filling the drive, and Clear cache works again after a hand-over was cut short.
- A compile whose process keeps growing restarts that process sooner and goes on where it was, also in DirectX 11 games
  and right away when the PC is nearly out of memory, so it doesn't slow to a crawl after hours.
- Reading the shaders inside an Unreal game's packages takes far less memory and time.
- Unreal games with patch paks or a Saved\Paks download plan only the shaders the game loads, not every older copy too
  (Marvel Rivals, Echoes of Aincrad).
- The compile keeps to its thread limit and finishes every pipeline when the game uses a ReShade build that creates
  pipelines asynchronously.
- DirectX 12 games whose files hold only a few raw shaders compile from their recording.
- Pipelines from another GPU's recording that blend on a format this GPU can't blend count as skipped (this GPU doesn't
  support what they need) instead of failed.
- A game whose plan a newer SCSKiller rebuilt mostly anew says "The plan changed, compile again" instead of offering
  that many more pipelines, and the last compile's failures count against the pipelines it tried.
- The Library's cache total includes the Windows shader cache, matching the Cache page.
- A game's cache clear shows in app.log, so problem reports show it.
- DirectX 11 compiles under Linux no longer fill up memory on games with many shaders.
- The traversal stutter fix also works with upscaler and frame-generation mods such as OptiScaler and HelixSR.
- Problem reports include the traversal stutter fix's status, its worst hitches and what it placed.
- The Windows app takes about 18 MB less disk space, as it no longer includes Windows App SDK files it never loads.
- DXVK games launched through Steam compile fewer shaders while playing, on NVIDIA and AMD GPUs.
- Marvel Rivals compiles its full plan on PCs with 16 GB.
- What's new shows the release notes with their sections and headings.
- What's new opens larger, with space between the sections.

**Fixed**

- FINAL FANTASY XIV and other DirectX 11 games with a DirectX 12 mod beside the exe are no longer read as DirectX 11 or
  12.
- Stopping a huge compile no longer throws away what it created when the plan changes before the next compile (a
  community recording refresh), even if handing its pipelines to the game's cache was cut short.
- Fortnite and other Unreal games with readable shader libraries no longer ask for the AES key when the encrypted files
  hold no shaders.
- An Unreal game whose encrypted files hold no package or shader library (Sea of Thieves) accepts its correct AES key,
  and a game whose global shader cache can't be read shows as unsupported instead of Ready.
- Unreal games that keep tools in their Binaries folder, such as Phantom Blade Zero, are listed with the game's own exe
  instead of a tool like 8za_v2.exe.
- Steam games whose exe sits four folders down, such as Dying Light: The Beast, are found without adding them by hand. A
  DLSS game's recorder says which file blocks it instead of "can't load", and tries again after a moment's antivirus
  lock without a restart.
- DLSS games on NVIDIA whose winmm.dll and version.dll belong to a mod loader, such as Cyberpunk 2077 with RED4ext, are
  recorded.
- On some AMD drivers, the Cache page lists a DirectX 11 game's driver cache under the game instead of "Files without an
  app key", and SCSKiller warns when AMD's shader cache is set to Always on.
- DirectX 11 games on AMD such as Counter-Strike 2: a shader that resets the GPU driver during a compile no longer fails
  the 15 shaders compiled with it. It's skipped, and later compiles on the same driver skip it at once.
- On AMD, compiles use the same cache as the game, also with OptiScaler beside it, so games like STAR WARS Galactic
  Racer and S.T.A.L.K.E.R. 2 are no longer marked Not compatible.
- On AMD, some Unreal Engine games compile once more after the update, this time into the cache the game reads.
- Games like PRAGMATA no longer log a crash event at startup with the recorder installed.
- On Linux, a Steam game first seen before Steam's app info could be read is checked again once it can, instead of
  staying at Needs a recording.
- SCSKiller checks for updates once when it starts, instead of twice 30 seconds apart.
- The About page on Linux shows the right .NET version and only the parts Linux uses, and screen readers read the
  licence list and sliders by name.
- Building plan finishes faster for games with a large community recording on Intel GPUs.
- Building plan shows its progress in percent, like Compiling.
- A second SCSKiller copy (portable or test) no longer takes over, updates, re-signs or removes another copy's recorder
  in a game folder, nor moves its session files.
- A game you added by hand whose graphics API SCSKiller can't tell shows the API menu, so you can pick Vulkan and record
  it.
- A game that can't be read is left out of the Library on its own, with a line in the log, instead of hiding every game
  of its store.
- Steam games show in the Library again when one of them has an exe over 2 GB.
- A launch that isn't recorded because of a broken Steam manifest names that file and says to move it out of its folder.
- Unreal games whose shader files sit in two places keep one build id between reads and find the community recordings
  shared under either of their old ids.
- Always record no longer keeps the compile from reaching the game's cache or blocks Clear cache.
- SCSKiller no longer compiles or records a game that loads its own copy of the Intel graphics driver.
- Before and after runs in Star Wars Outlaws and other Ubisoft Connect games no longer stop with a frame generation note
  when frame generation is off.
- A careful compile of a DirectX 11 game with a recording no longer fails at once with "scskiller_pass.bin has N items,
  the dbs M".
- On AMD, ReShade or OptiScaler installed as d3d12.dll loads again in games like Cyberpunk 2077 with the recorder on.
  The recorder stays out of those games, and their page says to rename the mod to dxgi.dll to record.

### From 1.4.0-alpha.1

**New**

- **SCSKiller on Linux**, as an AppImage. It installs for your user only, without root, adds itself
  to the application menu and updates itself.
- **Vulkan games from Steam** are recorded while you play, and compiled on NVIDIA and AMD on Windows. The recorder adds only one
  small settings file to the game's folder, plus one registry value per game on Windows or one file per game in your
  Vulkan layer folder on Linux. Turning the recorder off removes them.
- On Windows with NVIDIA, Vulkan games from Steam such as No Man's Sky compile from Steam's shader cache without a recording.
- On Windows with AMD, a Vulkan game from Steam compiles after you start it once with the recorder on and Steam's Shader
  Pre-Caching on.
- Clear cache on a Vulkan game from Steam with NVIDIA or AMD also deletes Steam's shader cache
  of the game, so it frees the size it shows.
- **Crimson Desert** compiles from a recording, or without one once you paste the game's key on its page, as published.
- **Crimson Desert** also compiles its shadow shaders and the shaders its render passes share.
- The game page and the queue warn when this PC's DirectX 12 can't compile a game's shaders, for example Shader Model
  6.6 shaders on Windows 10 without the game's own DirectX 12 runtime.
- On Linux, Steam games under Proton, non-Steam games added to Steam included, read SCSKiller's compiled cache with no
  launch options to set.
- On Linux, Vulkan games from Steam read SCSKiller's compiled cache with no launch options to set, on NVIDIA, AMD and Intel.
- **Before and after recording** works for Vulkan games from Steam.
- On Linux, Before and after works on NVIDIA, AMD and Intel. The game's Steam shader cache is set aside for the before
  run and put back after it.
- On Linux, Settings has Remove recorders, to use before you delete the AppImage.
- On Linux, Start when you sign in works, and the driver-update choice When idle works too, including in Game Mode.

**Faster and steadier**

- An update download that was cut off continues where it stopped instead of starting over.
- The window stays responsive while SCSKiller reads a large game's shaders in the background after an update.
- A game's page stays responsive while a scan or a compile runs (thanks @misutesu-desu).
- **A stopped compile no longer starts over at 0%** when SCSKiller checks its plan again after a restart and nothing
  in it changed.
- **A game set to DirectX 11** no longer compiles the DirectX 12 pipelines of its community recording, which DirectX 11
  never uses.

**Games**

- **Star Wars Outlaws** is supported. It compiles without a recording.
- **Unsupported games** no longer show a Community data line on their page, which made them look supported.

**Recorder and anti-cheat**

- **Ubisoft Connect games** no longer close at start with the recorder on, such as Assassin's Creed Valhalla. In these
  games and in DLSS games the recorder records shaders but doesn't time frames, so Before and after isn't offered.
- **Forza Horizon 5** contains anti-cheat. It counts as an anti-cheat game from Steam, the Xbox app or added by hand,
  so the recorder stays out unless you enable it at your own risk. SCSKiller isn't responsible for bans.

**Fixed**

- **Crimson Desert on AMD**: no more failed shaders in a compile, and every render pass gets its full shader set.
- **Crimson Desert** gets the same community recording on every PC with the same game version.
- Unity games started with -force-d3d11, -force-d3d12 or -force-vulkan in their Steam launch options are compiled for
  that API.
- The log no longer says a recorder another SCSKiller copy placed records nothing. It may still record, as its game page
  says.
- On NVIDIA, the first Before and after run of a game you played before SCSKiller now counts instead of being played
  again.
- On Intel under Windows, a Vulkan game's page says it runs on Vulkan, which isn't supported on this GPU yet.
- On AMD and Intel, a game's page no longer says its DLSS compiles, the DLSS tag's tip says DLSS runs only on NVIDIA,
  and a game that can't compile no longer says its upscalers compile.
- The recorder files of a game you uninstall while SCSKiller runs are removed without restarting SCSKiller.
- A Steam Vulkan game that was uninstalled, or reinstalled with anti-cheat, no longer keeps the Vulkan recorder
  registered for it.
- A game is no longer compiled without its middleware shaders when their DLL couldn't be read. The compile fails and
  the next one compiles it in full.
- SCSKiller no longer writes through a link someone placed in a game's folder.
- On Linux, a compile started from the terminal now holds back an update of the app until it ends.
- On Linux with NVIDIA, a big game reads all of its compiled cache, so its shaders no longer compile again while you
  play.
- Uninstalling SCSKiller also removes its notification registration from Windows.
- The recorder's log names the pipelines a game failed to create, with the error, so a report shows them.
- Reading the shaders of a big Unreal Engine game like Days Gone uses less memory. Maximum memory for compiling, set
  above half your RAM, now also raises the memory limit for reading shaders and building the plan.
- Wuthering Waves and other Unreal games no longer show as not supported when their files are busy while you play.
- A game with new pipelines says when compiling again redoes the whole game, or all its DirectX 11 shaders.
- **Send report** no longer fails with AMD graphics drivers.
- On AMD, when Clear cache can't delete a driver cache file that no app holds, it says that Reset Shader Cache in AMD
  Software removes it.

### From 1.4.0-alpha.3

**New**

- News from SCSKiller also shows as a desktop notification, one per new item with its title and first line, at most
  three at a time. Clicking it opens the bell.
- The PC stays awake while a compile runs, the screen can still turn off.
- Games whose shaders can't be read, like 007 First Light, compile from your own recording of them. Turn on Record on
  the game's page and play, "Record in all compatible games" leaves them out.
- Recordings of Vulkan games from Steam, and of games without a store version such as Ubisoft, Battle.net or games you
  added, are shared with the community database when sharing is on, and downloaded from it like other games. A Vulkan
  recording is shared without its shader code.
- The Cache page on Linux shows each game's shader cache and the driver's own cache, and on Windows it also shows and clears
  the files of Vulkan games in Steam's shader cache, as the game pages do.
- Report a problem lists the game's first launch after the last compile, as cache hits and compiles. The log notes
  when a compile's shader cache was merged into the game's own folder, with the keys added.
- **Compile new games automatically** in Settings, off by default: games you never compiled compile while the PC is
  idle, the same games Add all ready takes.
- On Linux, Windows games from Heroic (Epic, GOG and Amazon) and Lutris are listed and compiled in their own Wine or
  Proton prefix, and Steam installed as a Flatpak or Snap is found.
- **Vulkan games from every store** are recorded while you play and compiled on Windows, as Steam ones are: Epic, GOG,
  EA, Ubisoft, games added by hand, and the Xbox app. The recorder turns on for the game's own exe only, through
  one Vulkan loader settings file in SCSKiller's data folder and one registry value.
- For these games, Clear cache, the cache size shown and Before and after cover the driver's own Vulkan cache of the
  game, once SCSKiller has seen the game or its compile use it.
- A Vulkan game's page also says when the game ran as administrator outside a recording session, which stops the
  recorder from loading.
- A game's page has a **Compile size limit** for DirectX 12 games: a compile adds about that much at most to the game's
  shader cache. Recorded pipelines and ray tracing don't count toward it, and the game still adds its own shaders as
  you play.
- **HDD mode per drive**: Settings can turn HDD mode on only for the games on chosen drives, so games on an SSD
  are still read by the scan.
- Arknights: Endfield installed with GRYPHLINK, its own launcher, is found in the launcher's games folder. A copy
  installed elsewhere is still added with Add game.
- **Leave out a folder** or a whole drive from the Library's Folders menu: games installed there are not listed or
  read, and their recorders are taken out first. Picking the folder again in the menu lists its games again.
- **DCS World** compiles the shaders it built the last time you played. Before the first play its page says to play it once.
- **Before and after** can compile the after run with Max compile, where Max compile is offered. The report says so.
- Star Wars Outlaws uses your max anisotropy setting when it compiles without a recording.

**Added**

- The app log (app.log) records each start with its process id and each exit with its reason, and the next start says
  when the previous run ended without one.
- On Linux, games with anti-cheat that run on Vulkan can be recorded in a session you start from their page, as on
  Windows, once you allow it in Settings. Enabling the recorder there is at your own risk.
- On Linux, games with anti-cheat that Steam runs on DirectX 12 with Proton can be recorded in a session you start from
  their page too. Enabling the recorder there is at your own risk.
- On Linux, Vulkan games you added to Steam as non-Steam games can be recorded and compiled, like Steam's own Vulkan
  games.
- On Linux, DirectX 12 and DirectX 11 games that Steam runs with Proton also compile Steam's shader download for the game.
  After one start from Steam they compile without a recording. The game's page shows how many pipelines come from it.

**Fixed**

- Star Wars Outlaws with its DLC installed reads its shaders instead of failing.
- On Linux, games run with Proton-CachyOS compile instead of asking you to start them from Steam.
- Screen readers name the compile, refresh and page buttons.
- The command line shows VAC for games whose only anti-cheat is VAC, in the list and in status.
- A game whose folder was moved or deleted says it couldn't read the game files, not that its engine isn't supported
  yet.
- Compiles on AMD no longer pick up pipelines known to crash the driver from the shared packs.
- The library check no longer stops at one game whose files never finish reading. After a few minutes that game shows
  that it couldn't be read in time and is retried at the next scan, and the other games are checked.
- When a game's packages are too big to read in the memory SCSKiller has, the message now says it ran out of its read
  budget while reading the packages, not that the game holds more shaders than a compile can read.
- Infinity Nikki reads the patch files in its Saved folder too.
- A game with anti-cheat that has a RenoDX add-on next to it shows the RenoDX tag, found by the add-on
  file's name only, without reading any of the game's files.
- A DirectX 12 game's page and the queue warn when the GPU doesn't support DirectX 12, instead of compiling shaders
  it can't build.
- SCSKiller itself uses far less memory while a large game compiles: the memory it used to read and plan the
  game goes back to Windows when the compile starts, not only after the queue ends.
- On Linux, games of the Flatpak Steam say why they can't be compiled or recorded yet, also in the Flatpak version of
  SCSKiller, instead of asking for a recording.
- On Linux, the recorder works in Windows Vulkan games under Proton, also in non-Steam games added to Steam. A Vulkan
  game with anti-cheat is detected as Vulkan and can be recorded in a session at your own risk, and the session leaves no
  Vulkan layer entry behind. A compile stopped while it runs Steam's pipelines shows the whole plan's count, and pipelines
  skipped for failing Vulkan validation stay in the final count. A game folder holding a Wine prefix is no longer searched
  through the prefix's drive links, and Wine's own DLLs in it no longer make a Vulkan game read as DirectX 12. A game in a
  Steam game's Proton prefix, or whose steam_appid.txt names a Steam game, counts as VAC when that Steam game has VAC.
  With NVIDIA's NVK driver, the note that games of a Flatpak Heroic or Lutris can't be compiled yet names Mesa's drivers,
  not AMD and Intel.
- **A game whose steam_appid.txt names a VAC game** counts as VAC, so the recorder stays out.
- On Windows, games whose Steam files can't be read aren't recorded, so a VAC game is never recorded by mistake.
- **DOOM Eternal** from the Xbox app is found by its game exe, not the larger mod sandbox beside it.
- Borderlands 3 is compiled for DirectX 12, its default, also before the game saved its settings.
- On Linux with NVIDIA, a Steam game with Shader Pre-caching turned off reads the compile too. SCSKiller puts it in the
  game's own cache file once it has seen the game play, or says to turn Shader Pre-caching on when it doesn't fit.
- On Linux with NVIDIA and Shader Pre-caching off, the compile reaches games under Proton such as Monster Hunter Wilds.
  SCSKiller found the game's own cache file under only some of the names the driver gives it, and couldn't write the
  file format the driver uses outside Steam's folder.
- With an Intel GPU, Clear cache, the Cache page and before and after recordings include the Intel driver's shader
  cache, and the Library and Settings show its 512 MB limit per game instead of an unknown limit.
- Numbers like "3 109 of 131 120" no longer show a wide gap between digit groups in locales such as Polish.
- On Windows, a Vulkan compile runs on all its threads instead of one. A compile during which the graphics driver
  is updated or reset stops and asks you to compile again, instead of counting the rest as rejected by the driver.
  Its progress also moves past pipelines the driver rejects.
- A recording session for Wuthering Waves added by hand waits for you to start the game from its launcher, instead
  of starting the game's exe, which asked for admin rights or closed without its launcher.
- Recording stays off in FINAL FANTASY VII REBIRTH while Shader Injector is installed, because both hook the same
  functions. Compiling works as before.
- HELLDIVERS 2 uses helldivers2.exe when the HD2Arsenal mod manager is in its folder.
- A scan no longer fails when a recording is being written at the same moment, for example by a second SCSKiller.
- The before and after panel shows in Vulkan games that run in HDR or with 10-bit colour, as it does in DirectX
  games.
- Call of Duty: Modern Warfare (2019) on Steam is recognised as a game with anti-cheat, and the recorder is taken out
  of it.
- Wuthering Waves from WeGame opens with its AES key, like the launcher's copy.
- Split Fiction on AMD compiles for the driver's profile of the game from the first compile.
- On AMD, a game the driver gives a cache of its own, such as S.T.A.L.K.E.R. 2, shows Not compatible after its first
  compile misses it, instead of asking for a second compile of several GB that misses too. A driver update lets it
  compile again.
- **Before and after works in DLSS games and Ubisoft Connect games**, such as Star Wars Outlaws. The recorder times
  their frames again. In DirectX 12 Ubisoft Connect games on Windows the countdown shows in SCSKiller, not in the game.
- **Assassin's Creed Valhalla** can be recorded again.
- A session at your own risk in a DLSS game with anti-cheat on NVIDIA says before it starts that DLSS loads DirectX 12
  before the recorder can, instead of starting a session that records nothing.
- The game page shows Max compile only where it compiles more, not for Vulkan or DirectX 11 games or on GPUs that
  cache whole pipelines.
- Unreal games whose launcher downloads game files into the game's Saved folder, such as Infinity Nikki, compile the
  shaders in those files too. When the launcher downloads new ones, the game asks to compile again.
- An Unreal game where only some game files are encrypted shows **Set AES key** on its page when SCSKiller can't find
  the key. The rest of its shaders compile until you add it.
- The recorder no longer stops recording with "the scskiller.ini in its folder isn't SCSKiller's" after a game update,
  and a game where that already happened records again.
- On Linux, a compile run by another SCSKiller (another data folder) no longer counts as its game running.
- On Linux, a compile whose own process is closing no longer takes it for the game starting, which showed the compile
  stopped for the game and then compiled again.
- On Linux with NVIDIA, a compiled game whose cache gets more room shows Needs rebuilding with how many more pipelines
  fit, instead of Warmed until the next compile.
- On Linux with NVIDIA, games whose DLSS loads DirectX 12 before the game can be recorded as on Windows, instead of
  showing that DLSS blocks the recorder.
- A Vulkan game from Steam that hasn't run from Steam on this PC yet asks you to start it once from Steam, instead of
  asking for a 5-minute recording.
- A Vulkan game's page says how many pipelines come from Steam's shader download and how many from your play, and why
  some are skipped, instead of showing DirectX rows.
- **Vulkan recording sessions** say when the game ran on DirectX or as administrator, which stops the recorder from
  loading. Picking Vulkan for a game that last ran on DirectX shows a warning before the session starts.
- Vulkan recording sessions in games with anti-cheat place the recorder in the game's folder for the session, which
  some games require. It is removed when the session ends.
- Games that load Vulkan at runtime, such as Enshrouded, are detected as Vulkan, and upscaler and middleware shaders no
  longer decide a game's graphics API.
- On Linux with NVIDIA, a large game's compile that fills its shader cache counts as compiled and is placed for the
  game, instead of failing. Steam games get a 17 GB cache instead of 10 GB, and the plan fits what the cache holds.
  With Mesa, the Library and Settings no longer show a 10 GB limit, because Mesa's cache for a Steam game has none.
- On Linux, When idle compiles wait until you've been away from the PC for two minutes on GNOME, KDE on X11 and
  desktops that report idle time to the system, instead of starting at once.
- On Linux, Ask me first shows the driver-update notification after you sign in, also when SCSKiller isn't running.
- On Linux with AMD and Intel, Clear cache deletes the game's shader cache, Steam's Shader Pre-Caching files for it
  included, which Steam processes again.
- On Linux, Clear cache also deletes the game's vkd3d-proton cache that the compile writes, which Settings already
  counted in the cache's size.
- On Linux with NVIDIA, when a game's shaders don't fit, the queue and the game's page name SCSKiller's compile folder,
  17 GB for a game whose cache goes in Steam's 4 GB files, instead of a cache the driver keeps.
- On Linux, Settings shows the free space where the shader cache is, named by its mount point, instead of the root
  file system's.
- On Linux with Mesa, compiles no longer pass a 10 GB shader cache size, which Mesa's cache for a Steam game never used.
- A community recording that downloads while its game compiles is put in place after the compile, so the compile's
  result is shown for the recordings it compiled.
- On NVIDIA, game pages and the Cache page show how much of the driver cache is in use next to its size on disk,
  and the cache-limit warning uses the space in use.
- Marvel Rivals asks you to start it once from Steam after an install or update before it compiles, because its
  launcher clears the shader cache at the first start of a new install or season.
- On AMD, Cyberpunk 2077 and other games that create their device through AMD AGS keep recording after a game update
  changes AGS. An AGS the recorder can't work with is named in the app log.
- A compile with nothing to compile, such as a game whose shaders SCSKiller can't read yet, ends as Failed and says
  so, instead of showing Warmed.
- app.log no longer repeats "could not import ...scskiller.db" while a recorded game runs. The recording is imported
  when the game exits, as before.
- A before and after recording is no longer lost when the anti-cheat check of a large game runs out of time during
  its compile.
- On Linux, a Linux program that stays open after the game closes, such as an overlay, no longer keeps SCSKiller
  waiting for the game to close. A Windows program that does is named in the log.
- With NVIDIA, the large-compile warning says when the driver would keep more of the cache in memory in game than this
  PC's RAM leaves free.
- Compiling a game again when only pipelines the driver refused are left says so, and that the ones compiled earlier
  are kept, instead of showing all of them as failed.
- Zenless Zone Zero no longer offers a recording session, because the game closes itself with the recorder in its
  folder. Compiling still works.
- The Witcher 3's page says when MFG Unlocker is next to the game, because its Path Traced Hair turns off while the
  recorder is in the game folder.
- On Linux, a plan bigger than the compile's cache keeps the pipelines whose shaders recordings saw, instead of the
  first ones found in the game files. Shader files that pair nearly every vertex shader with every pixel shader get each
  shader planned once, instead of every pair.
- On Windows with NVIDIA, shader files that pair nearly every vertex shader with every pixel shader get each shader planned once,
  instead of every pair, so planning such a game no longer runs out of memory.
- On Linux, pipelines whose shaders fail Vulkan validation, or that need a feature the GPU lacks, are shown as such,
  instead of as the GPU driver crashing on them.
- On AMD, Clear cache deletes driver cache files it isn't allowed to read, instead of saying they are in use.
- On Linux, Intel GPUs on Mesa's ANV driver and NVIDIA GPUs on Mesa's NVK driver compile through the same Mesa path as
  AMD, without the experimental switch.

### From 1.4.0-beta.1

**New**

- The Cache page shows each installed game's icon on its row, the other rows have a plain icon.
- A game with an AES key has "Change AES key" and "Remove AES key" on its page.
- The game page has an "Open folder" button that opens the detected exe's folder with the exe selected.
- At 100% coverage the game page says why "Found in the game files" counts more than "Shaders in the game".
- The Before and after image shows the SCSKiller version and the GPU driver version in its corner.
- The compile queue has a "Move to bottom" button next to "Move to top".
- Games that load ReShade and RenoDX through Special K compile through that ReShade and its add-ons, as games with
  ReShade as dxgi.dll do. When it can't be sure which ReShade Special K loads, the game's page says what to change.
- The Cache page summary shows the driver cache with its limit, and on NVIDIA 621.01 and later the Windows shader cache and the total.
- On NVIDIA, DirectX 11 games can run the recorder for the frame-time graph alone during regular play, not only during
  a Before and after recording. "Record in all compatible games" turns it on for them, as for DirectX 12 games.
- Automatic compiles can start right away instead of waiting for the PC to be idle: after a driver update with
  "Automatically, right away", and when a game exits with new shaders with "Compile new shaders right away".
- A game's page can turn off "Use shared shader hashes" for that game alone. Its compiles then leave out what the
  community shared, and nothing more is downloaded for it.

**Fixed**

- A game added by hand from a Steam game's folder counts as a game with anti-cheat when that Steam game does, such
  as Call of Duty: Modern Warfare (2019), and the recorder is taken out of it.
- A game recorded at several texture filtering settings no longer compiles every pipeline again for each of them: a
  compile takes the one the game uses and the last other one (Maximum mode: three), and the game page counts those
  pipelines too.
- With Download shared shader hashes on, a game whose shared recording SCSKiller only partly matches compiles the
  shaders nobody recorded yet, as it does without the download.
- Compiling again after a compile stopped on GPU driver crashes no longer stops after a few pipelines when it runs on
  one thread.
- The library's Cache column says it counts each game's driver and Windows shader cache, so a game can be bigger than
  the Driver shader cache total, which counts the driver's folder only.
- A compile on a GPU driver that crashes on a large share of the pipelines stops with that reason after about a thousand
  of them instead of restarting for days. Compiling again continues past them.
- A DirectX 11 game's last session no longer says it had no stutters from shader compiles. It counts its stutters
  and says SCSKiller can't tell shader compilation stutters from other ones on DirectX 11.
- On NVIDIA, the pipeline pairings a game ships or a recording holds compile first, before the ones SCSKiller pairs
  up itself, and a compile size limit keeps them first. A size-limited compile of Gears of War: E-Day no longer leaves
  them out.
- A game that switches between DirectX 11 and 12 keeps both compiles. Switching back no longer shows 0% and asks to
  compile everything again.
- A game whose files hold no shaders SCSKiller can compile shows why on its page after its first compile, instead of Ready
  to compile, and Add all ready leaves it out.
- On NVIDIA, the Compile size limit on a game's page is no longer ignored. The compile leaves out the pipelines least
  likely to be used to stay under it.
- A compile that waits shows why instead of a plain Compiling: paused until the PC is idle, or waiting for an earlier
  compile to go into its game's shader cache.
- Compiles are refused while the NVIDIA shader cache is turned off, and the game page says how to turn it back on.
  The Library's cache card says when it is off.
- Marvel Rivals' first compile estimates its cache from what it measured on other PCs, about twice the old
  figure, and the game page calls the size an estimate.
- A Vulkan recording session in a game with anti-cheat no longer says the game ran as administrator when SCSKiller
  couldn't tell.
- DirectX 9 games such as Half-Life 2 show that DirectX 9 games aren't supported yet, instead of asking for a
  recording.
- Open folder on the Cache page opens the game's own cache folder, or lets you pick when its cache is in two
  folders, and every row has it.
- A Vulkan game whose launcher starts its renderer from a subfolder is detected as Vulkan. Where the graphics card has
  a Vulkan compile, the API picker always offers Vulkan.
- The Vulkan recorder reaches a game's renderer when its launcher starts it from a subfolder, from the launch after
  SCSKiller first sees it run.
- A recording near its size limit keeps the pipelines with shaders it hasn't seen yet, instead of stopping at the
  first ones a game creates, so the rest of the session's new shaders are still recorded.
- On NVIDIA, a game's Disk space counts only the driver cache the game and its last compile use, not what an earlier
  compile left. The Cache page lists those under Earlier SCSKiller compiles.
- The game page and `status` line for games with Advanced Shader Delivery say Windows already delivered part of the
  game's compiled shaders and SCSKiller compiles the rest.
- Record in all compatible games also installs the recorder in games that need a recording, and games on OpenGL say
  they aren't supported yet.

### From 1.4.0-beta.2

**New**

- DirectX 9 games say that compiling them waits for DirectX 9 support (on AMD, for SCSKiller's DirectX 9 compile), and
  DirectX 10 games show as DirectX 11 (runs on DirectX 10), instead of both showing as DirectX 11 or 12.
- A game whose steam_appid.txt names an app Steam doesn't know offers a recording session at your own risk.

**Improved**

- Crimson Desert compiles most shader combinations before the game's key is added (about 98% of the combinations found
  instead of 87%).
- Plans on NVIDIA no longer add a pipeline per shader where one pipeline covers two (ELDEN RING 39,814 to 31,773
  pipelines, same coverage).

**Fixed**

- Vulkan games compiled into Steam's shader cache on NVIDIA compile again after Steam clears that cache, instead of
  looking covered.
- Vulkan games on AMD compiled into Steam's shader cache compile again after Steam clears that cache, instead of looking
  covered, and a compile no longer replays every pipeline while that cache is intact.
- BeamNG.drive is no longer marked as a 32-bit game.
- Games that can run on DirectX or Vulkan, such as BeamNG.drive, are recorded on whichever one they run.
- The game page shows the latest session of a game played on both DirectX and Vulkan.
- The log no longer says games with AMD AGS 6 aren't recorded on AMD.
- The recorder no longer refuses games that ship an unusual steam_appid.txt file.
- The "Use shared shader hashes" switch stays on the game page after you turn it off.
- Games whose exe is a small launcher next to the DirectX 9 one (e.g. Fahrenheit: Indigo Prophecy Remastered) show as
  DirectX 9 instead of DirectX 11 or 12.

### From 1.4.1-alpha.1

**New**

- An experimental traversal stutter fix for DirectX 12 games on Windows, recommended for GPUs with more than 8 GB of
  VRAM. It stays off until you turn it on, from a one-time question, in Settings, or on each game's page.

## [1.4.1-alpha.2] - 2026-10-27

**New**

- For games with Advanced Shader Delivery, the compile will skip the shaders Windows already delivered and compile only
  the rest.
- The Library will show what SCSKiller checks by itself after an update, those checks will use at most a quarter of the
  RAM up to 8 GB, a compile you start will no longer wait for them, and a check that can't finish will keep the game's
  plan and no longer run again at every start.
- DirectX 9 games will say that compiling them waits for DirectX 9 support (on AMD, for SCSKiller's DirectX 9 compile),
  and DirectX 10 games will show as DirectX 11 (runs on DirectX 10), instead of both showing as DirectX 11 or 12.
- Games with anti-cheat will get an "Always record" switch that records every launch after you confirm it once, at your
  own risk.
- A game whose steam_appid.txt names an app Steam doesn't know will offer a recording session at your own risk.
- Settings will offer the same shader cache sizes as the NVIDIA app, from 512 MB to Unlimited.

**Improved**

- A Plague Tale: Requiem will compile every shader combination found in its files instead of about 97%.
- Crimson Desert will compile most shader combinations before the game's key is added (about 98% of the combinations
  found instead of 87%).
- The RE4 Chainsaw Demo will compile from its own root signatures on AMD.
- While SCSKiller hands a finished compile to the game's cache, the Library and the game's page will show its progress
  ("Handing the compile to the game's cache: N%"). Clear cache will stop it first instead of failing with files in use.
- A game's page will say why pipelines couldn't be compiled, and a problem report will list the top reasons.
- A pipeline that crashed or hung the GPU driver will stay skipped on later compiles until the driver changes, and a
  pipeline that hangs before the compile's workers start will no longer stall the compile.
- Compile progress will keep its total after a driver crash or hang, clear the retry note once the compile is past it,
  and estimate the time left from a stopped compile until one completes.
- Plans on NVIDIA will no longer add a pipeline per shader where one pipeline covers two (ELDEN RING 39,814 to 31,773
  pipelines, same coverage).
- On NVIDIA, the pipelines a stopped or failed compile created will no longer be compiled again after the game's plan
  changes or SCSKiller updates.
- Unreal Engine 5.8 games such as Fortnite will compile their compute shaders that use NVIDIA's extensions the way the
  game creates them, so those will no longer compile during play on NVIDIA.
- The cache hand-off after a game session will read the game's whole cache only once a week instead of every time.

**Fixed**

- Unreal Engine games with a recording will compile all their pipelines again, instead of leaving out a large part of
  them on NVIDIA. The affected games will compile once more after the update.
- Vulkan games compiled into Steam's shader cache on NVIDIA will compile again after Steam clears that cache, instead
  of looking covered.
- A game's coverage will show what its last compile covers, and pipelines found since then will only be counted in the
  line that offers to compile them, instead of lowering the percentage.
- The Shader layouts row will no longer say DirectX 11 for a DirectX 12 game that compiles from its recordings.
- Resident Evil Requiem (demo) will compile the pipelines its game creates on NVIDIA, also from a shared recording made
  on another GPU.
- Pipelines whose pixel shader sets the stencil reference will no longer be compiled on NVIDIA, where they always fail.
- Resident Evil Requiem (demo) and Dragon's Dogma 2 Character Creator will compile most of their pipelines instead of
  about a third.
- DLSS games on NVIDIA whose exe loads none of winmm, version or dinput8, such as Dying Light: The Beast, will be
  recorded, and no game will show Not supported because of DLSS.
- An Unreal game whose files are only partly readable without its AES key will say it needs the key for full coverage,
  and will still compile what it can read.
- Vulkan games on AMD compiled into Steam's shader cache will compile again after Steam clears that cache, instead of
  looking covered, and a compile will no longer replay every pipeline while that cache is intact.
- BeamNG.drive will no longer be marked as a 32-bit game.
- Games that can run on DirectX or Vulkan, such as BeamNG.drive, will be recorded on whichever one they run.
- The game page will show the latest session of a game played on both DirectX and Vulkan.
- The log will no longer say games with AMD AGS 6 aren't recorded on AMD.
- "Delete all" will no longer say a compile is in progress after a compile has finished.
- The recorder will stop refusing games that ship an unusual steam_appid.txt file.
- The "Use shared shader hashes" switch will stay on the game page after you turn it off.
- NVIDIA players without a recording will no longer compile Unreal games with a root-signature layout the game doesn't
  use (an extra, larger compile). The first compile after the update redoes those games once.
- Games whose exe is a small launcher next to the DirectX 9 one (e.g. Fahrenheit: Indigo Prophecy Remastered) will show
  as DirectX 9 instead of DirectX 11 or 12.
- Microsoft Flight Simulator 2024 and other games whose shader delivery has no engine name will take the compiled cache
  again instead of waiting for it with "Not reached yet".
- SCSKiller will hand a compile to a game whose shader cache folder you moved to another drive with a junction or link,
  without rereading it at every start, and the game's page will say why when it can't.
- A compile's shader cache will no longer go missing when one of its files is damaged or can't be read, or when a driver
  update comes before the game's next start.

## [1.4.1-alpha.1] - 2026-10-26

Everything in 1.4.0-beta.1, plus:

**New**

- An experimental traversal stutter fix for DirectX 12 games on Windows, recommended for GPUs with more than 8 GB of
  VRAM. It stays off until you turn it on, from a one-time question, in Settings, or on each game's page.

## [1.4.0-beta.2] - 2026-10-27

**New**

- For games with Advanced Shader Delivery, the compile will skip the shaders Windows already delivered and compile only
  the rest.
- The Library will show what SCSKiller checks by itself after an update, those checks will use at most a quarter of the
  RAM up to 8 GB, a compile you start will no longer wait for them, and a check that can't finish will keep the game's
  plan and no longer run again at every start.
- DirectX 9 games will say that compiling them waits for DirectX 9 support (on AMD, for SCSKiller's DirectX 9 compile),
  and DirectX 10 games will show as DirectX 11 (runs on DirectX 10), instead of both showing as DirectX 11 or 12.
- Games with anti-cheat will get an "Always record" switch that records every launch after you confirm it once, at your
  own risk.
- A game whose steam_appid.txt names an app Steam doesn't know will offer a recording session at your own risk.
- Settings will offer the same shader cache sizes as the NVIDIA app, from 512 MB to Unlimited.

**Improved**

- A Plague Tale: Requiem will compile every shader combination found in its files instead of about 97%.
- Crimson Desert will compile most shader combinations before the game's key is added (about 98% of the combinations
  found instead of 87%).
- The RE4 Chainsaw Demo will compile from its own root signatures on AMD.
- While SCSKiller hands a finished compile to the game's cache, the Library and the game's page will show its progress
  ("Handing the compile to the game's cache: N%"). Clear cache will stop it first instead of failing with files in use.
- A game's page will say why pipelines couldn't be compiled, and a problem report will list the top reasons.
- A pipeline that crashed or hung the GPU driver will stay skipped on later compiles until the driver changes, and a
  pipeline that hangs before the compile's workers start will no longer stall the compile.
- Compile progress will keep its total after a driver crash or hang, clear the retry note once the compile is past it,
  and estimate the time left from a stopped compile until one completes.
- Plans on NVIDIA will no longer add a pipeline per shader where one pipeline covers two (ELDEN RING 39,814 to 31,773
  pipelines, same coverage).
- On NVIDIA, the pipelines a stopped or failed compile created will no longer be compiled again after the game's plan
  changes or SCSKiller updates.
- Unreal Engine 5.8 games such as Fortnite will compile their compute shaders that use NVIDIA's extensions the way the
  game creates them, so those will no longer compile during play on NVIDIA.
- The cache hand-off after a game session will read the game's whole cache only once a week instead of every time.

**Fixed**

- Unreal Engine games with a recording will compile all their pipelines again, instead of leaving out a large part of
  them on NVIDIA. The affected games will compile once more after the update.
- Vulkan games compiled into Steam's shader cache on NVIDIA will compile again after Steam clears that cache, instead
  of looking covered.
- A game's coverage will show what its last compile covers, and pipelines found since then will only be counted in the
  line that offers to compile them, instead of lowering the percentage.
- The Shader layouts row will no longer say DirectX 11 for a DirectX 12 game that compiles from its recordings.
- Resident Evil Requiem (demo) will compile the pipelines its game creates on NVIDIA, also from a shared recording made
  on another GPU.
- Pipelines whose pixel shader sets the stencil reference will no longer be compiled on NVIDIA, where they always fail.
- Resident Evil Requiem (demo) and Dragon's Dogma 2 Character Creator will compile most of their pipelines instead of
  about a third.
- DLSS games on NVIDIA whose exe loads none of winmm, version or dinput8, such as Dying Light: The Beast, will be
  recorded, and no game will show Not supported because of DLSS.
- An Unreal game whose files are only partly readable without its AES key will say it needs the key for full coverage,
  and will still compile what it can read.
- Vulkan games on AMD compiled into Steam's shader cache will compile again after Steam clears that cache, instead of
  looking covered, and a compile will no longer replay every pipeline while that cache is intact.
- BeamNG.drive will no longer be marked as a 32-bit game.
- Games that can run on DirectX or Vulkan, such as BeamNG.drive, will be recorded on whichever one they run.
- The game page will show the latest session of a game played on both DirectX and Vulkan.
- The log will no longer say games with AMD AGS 6 aren't recorded on AMD.
- "Delete all" will no longer say a compile is in progress after a compile has finished.
- The recorder will stop refusing games that ship an unusual steam_appid.txt file.
- The "Use shared shader hashes" switch will stay on the game page after you turn it off.
- NVIDIA players without a recording will no longer compile Unreal games with a root-signature layout the game doesn't
  use (an extra, larger compile). The first compile after the update redoes those games once.
- Games whose exe is a small launcher next to the DirectX 9 one (e.g. Fahrenheit: Indigo Prophecy Remastered) will show
  as DirectX 9 instead of DirectX 11 or 12.
- Microsoft Flight Simulator 2024 and other games whose shader delivery has no engine name will take the compiled cache
  again instead of waiting for it with "Not reached yet".
- SCSKiller will hand a compile to a game whose shader cache folder you moved to another drive with a junction or link,
  without rereading it at every start, and the game's page will say why when it can't.
- A compile's shader cache will no longer go missing when one of its files is damaged or can't be read, or when a driver
  update comes before the game's next start.

## [1.4.0-beta.1] - 2026-10-26

**New**

- The Cache page shows each installed game's icon on its row, the other rows have a plain icon.
- A game with an AES key has "Change AES key" and "Remove AES key" on its page.
- The game page has an "Open folder" button that opens the detected exe's folder with the exe selected.
- At 100% coverage the game page says why "Found in the game files" counts more than "Shaders in the game".
- The Before and after image shows the SCSKiller version and the GPU driver version in its corner.
- The compile queue has a "Move to bottom" button next to "Move to top".
- Games that load ReShade and RenoDX through Special K compile through that ReShade and its add-ons, as games with
  ReShade as dxgi.dll do. When it can't be sure which ReShade Special K loads, the game's page says what to change.
- Games with anti-cheat compile through their ReShade and RenoDX too, as other games do.
- The Cache page summary shows the driver cache with its limit, and on NVIDIA 621.01 and later the Windows shader cache and the total.
- On NVIDIA, DirectX 11 games can run the recorder for the frame-time graph alone during regular play, not only during
  a Before and after recording. "Record in all compatible games" turns it on for them, as for DirectX 12 games.
- Automatic compiles can start right away instead of waiting for the PC to be idle: after a driver update with
  "Automatically, right away", and when a game exits with new shaders with "Compile new shaders right away".
- A game's page can turn off "Use shared shader hashes" for that game alone. Its compiles then leave out what the
  community shared, and nothing more is downloaded for it.

**Fixed**

- On NVIDIA, a rebuild after a launch that still compiled some new pipelines compiles only those, instead of every
  pipeline again, such as in Borderlands 4.
- After a compile, saving the driver's cache, finishing up and copying the cache to the game's folder show as their own
  steps with their progress and time, instead of sitting at 100%.
- Frame times are measured with OptiScaler or another mod installed as dxgi.dll in the game's folder.
- Frame times are measured with FSR mods that replace or rename the game's FSR file, such as HelixSR.
- The recorder works in Dragon's Dogma 2 Character Creator again, instead of turning itself off as "not signed by
  SCSKiller".
- A game added by hand from a Steam game's folder counts as a game with anti-cheat when that Steam game does, such
  as Call of Duty: Modern Warfare (2019), and the recorder is taken out of it.
- The recorder steps aside from frame generation's swap chain (AMD FSR, DLSS through Streamline) and stops overlays
  from looping through it.
- With "Download shared shader hashes" on, NVIDIA pipelines this PC recorded compile with the game's own NVIDIA settings,
  not another player's, so a rebuild after a download no longer compiles them all again.
- Shared shader hashes pool across PCs: every install of the same game build adds to one shared set and downloads it,
  instead of most PCs getting a set of their own. A recording shared before this version is shared again once, within
  7 days.
- When the recorder doesn't run in a game, for example because of anti-cheat or a changed game, nothing of it stays
  active in that game.
- A recorder from an older SCSKiller version found in a game with anti-cheat no longer turns that game's recorder on.
- A game recorded at several texture filtering settings no longer compiles every pipeline again for each of them: a
  compile takes the one the game uses and the last other one (Maximum mode: three), and the game page counts those
  pipelines too.
- Reinstalling the same ReShade or RenoDX files no longer asks for a new compile of a game that compiles through them.
- With Download shared shader hashes on, a game whose shared recording SCSKiller only partly matches compiles the
  shaders nobody recorded yet, as it does without the download.
- Compiling again after a compile stopped on GPU driver crashes no longer stops after a few pipelines when it runs on
  one thread.
- Pipelines skipped after a spot where the GPU driver kept failing are no longer said to have crashed it, and a partly
  compiled game's status counts the pipelines the driver crashed on apart from the ones it refused.
- The library's Cache column says it counts each game's driver and Windows shader cache, so a game can be bigger than
  the Driver shader cache total, which counts the driver's folder only.
- A compile on a GPU driver that crashes on a large share of the pipelines stops with that reason after about a thousand
  of them instead of restarting for days. Compiling again continues past them.
- A DirectX 11 game's last session no longer says it had no stutters from shader compiles. It counts its stutters
  and says SCSKiller can't tell shader compilation stutters from other ones on DirectX 11.
- A compile no longer stays at "Compiling" forever when a process the GPU driver or a ReShade add-on left running holds
  its output. It ends about 30 seconds after the compile process exits, with an error when the compile didn't finish.
  A compile that stops making progress (a ReShade add-on or the GPU driver hanging) ends after 10 minutes with an error
  instead of showing Compiling forever.
- On NVIDIA, part of Gears of War: E-Day's compiled pipelines now match the ones the game uses: they are built with
  NVIDIA's shader extension and NVIDIA Aftermath set up as the game sets them.
- Clear cache on a game's page deletes its Windows shader cache on every driver and whatever its size, also for games
  with anti-cheat, as the Cache page says.
- On NVIDIA, the pipeline pairings a game ships or a recording holds compile first, before the ones SCSKiller pairs
  up itself, and a compile size limit keeps them first. A size-limited compile of Gears of War: E-Day no longer leaves
  them out.
- A game that switches between DirectX 11 and 12 keeps both compiles. Switching back no longer shows 0% and asks to
  compile everything again.
- On NVIDIA, compiles with "Download shared shader hashes" on no longer stop on GPU driver crashes in the community's
  ray tracing pipelines. Where SCSKiller builds a game's ray tracing collections from its files those pipelines are left
  out, and the others compile last.
- A compile of a very big plan, such as Marvel Rivals with all its shader combinations, no longer fails after about
  3 minutes with nothing created.
- On AMD, God of War Ragnarök's compile reaches the game's own driver cache instead of showing Not compatible.
- A compile stopped by a game starting or by GPU driver crashes and hangs keeps its progress. Compiling again goes on
  past the spot where the driver kept failing, and what it skips there counts as skipped, not failed.
- A game whose files hold no shaders SCSKiller can compile shows why on its page after its first compile, instead of Ready
  to compile, and Add all ready leaves it out.
- After a compile that was stopped and continued, the next compile on the same driver leaves out what its last part
  compiled instead of compiling everything again. Putting a compile into the game's Windows shader cache shows as its own
  step with its progress, and takes seconds when that cache already holds it all.
- SCSKiller no longer crashes at start on NVIDIA PCs whose driver lists its files without their folder.
- On NVIDIA, the Compile size limit on a game's page is no longer ignored. The compile leaves out the pipelines least
  likely to be used to stay under it.
- A compile that waits shows why instead of a plain Compiling: paused until the PC is idle, or waiting for an earlier
  compile to go into its game's shader cache.
- On NVIDIA, "The compile didn't help" clears once a later launch shows the compile in use. Until then the game
  also missed driver-update recompiles and new-shader prompts.
- On NVIDIA, putting a compile into a game's Windows shader cache no longer checks the whole cache again while the game
  hasn't changed it. With a cache of about 100 GB that check took up to an hour at start, and compiles waited for it.
- When SCSKiller can't read a cache size or another detail in the background, it carries on without it instead of
  closing.
- On AMD, compiling a DirectX 11 game no longer leaves an unused DirectX 12 shader cache file under the game's name.
- Compiles are refused while the NVIDIA shader cache is turned off, and the game page says how to turn it back on.
  The Library's cache card says when it is off.
- On AMD, the recorder installs in Cyberpunk 2077 again. The game's latest update added a file with the name the
  recorder used.
- When the recorder can't go into a game, for example because Windows Security removed one of its files, the game's page
  says why instead of showing nothing.
- Marvel Rivals' first compile estimates its cache from what it measured on other PCs, about twice the old
  figure, and the game page calls the size an estimate.
- A Vulkan recording session in a game with anti-cheat no longer says the game ran as administrator when SCSKiller
  couldn't tell.
- DirectX 9 games such as Half-Life 2 show that DirectX 9 games aren't supported yet, instead of asking for a
  recording.
- 32-bit games show that they aren't supported yet, instead of Ready to compile and a compile that doesn't reach them,
  and the recorder no longer goes into their folder.
- Open folder on the Cache page opens the game's own cache folder, or lets you pick when its cache is in two
  folders, and every row has it.
- A Vulkan game whose launcher starts its renderer from a subfolder is detected as Vulkan. Where the graphics card has
  a Vulkan compile, the API picker always offers Vulkan.
- The Vulkan recorder reaches a game's renderer when its launcher starts it from a subfolder, from the launch after
  SCSKiller first sees it run.
- On NVIDIA from driver 621.01, a game moved to another folder or drive keeps its compiled shaders: they go into the
  Windows shader cache of its new location when the game exits or SCSKiller starts.
- A recording near its size limit keeps the pipelines with shaders it hasn't seen yet, instead of stopping at the
  first ones a game creates, so the rest of the session's new shaders are still recorded.
- On NVIDIA, a game's Disk space counts only the driver cache the game and its last compile use, not what an earlier
  compile left. The Cache page lists those under Earlier SCSKiller compiles.
- On AMD, a game that registers its own app name with AMD's driver compiles for it again once a launch shows it
  using that cache, instead of staying on a cache the game doesn't read.
- The game page and `status` line for games with Advanced Shader Delivery say Windows already delivered part of the
  game's compiled shaders and SCSKiller compiles the rest.
- A damaged shared shader download no longer makes a game compile fewer pipelines than its own recording holds.
- A folder you add no longer lists programs that aren't games (like Telegram Desktop), which could pause the compile while they ran.
- Before and after measures the game's rendered frames with frame generation on: DLSS frame generation (such as
  Resident Evil Requiem), FSR frame generation and OptiScaler's own frame generation, also in Ubisoft Connect games.
- RE Engine games such as Resident Evil Village leave out the pipelines the game lists whose two shaders don't fit
  together, which AMD's driver refused (18 in Resident Evil Village).
- After updating from 1.3.0, app.log says why the first compile runs every pipeline again.
- On NVIDIA, a compile from SCSKiller 1.3.1 whose result never reached the game's own shader cache, because SCSKiller
  closed while copying it, is copied there when SCSKiller starts. Clear cache also removes a copy still waiting to go
  in, and removing earlier compiles on the Cache page no longer says "Nothing needs to compile again".
- A compile you start no longer waits while SCSKiller checks other games after an update, and the queue shows what it
  waits for.
- Frame times keep going after a game re-creates its swap chain under Streamline or OptiScaler, such as when frame
  generation is turned on or off.
- Record in all compatible games also installs the recorder in games that need a recording, and games on OpenGL say
  they aren't supported yet.
- Beta builds ask for a Patreon supporter sign-in at the first start, and run offline after that.

## [1.4.0-alpha.3] - 2026-10-22

**New**

- News from SCSKiller also shows as a desktop notification, one per new item with its title and first line, at most
  three at a time. Clicking it opens the bell.
- The PC stays awake while a compile runs, the screen can still turn off.
- On NVIDIA 621 and later, a game whose Windows shader cache expects another identity than its Advanced Shader Delivery
  registration says can be fixed from the server, without an app update.
- Games that get compiled shaders from Windows through Advanced Shader Delivery show an Advanced Shader Delivery tag with its size.
  When the delivered shaders fit your driver, the game page and `status` say a compile doesn't add much.
- On NVIDIA with sharing on, the first launch after a compile reports how many pipelines Windows' shader cache still had
  to add, also with the recorder off, so games the compile misses show up on the server. Only counts and the game
  build's hash are sent.
- Games whose shaders can't be read, like 007 First Light, compile from your own recording of them. Turn on Record on
  the game's page and play, "Record in all compatible games" leaves them out.
- Recordings of Vulkan games from Steam, and of games without a store version such as Ubisoft, Battle.net or games you
  added, are shared with the community database when sharing is on, and downloaded from it like other games. A Vulkan
  recording is shared without its shader code.

- The Cache page on Linux shows each game's shader cache and the driver's own cache, and on Windows it also shows and clears
  the files of Vulkan games in Steam's shader cache, as the game pages do.
- Report a problem lists the game's first launch after the last compile, as cache hits and compiles. The log notes
  when a compile's shader cache was merged into the game's own folder, with the keys added.
**New**

- **Compile new games automatically** in Settings, off by default: games you never compiled compile while the PC is
  idle, the same games Add all ready takes.
- On Linux, Windows games from Heroic (Epic, GOG and Amazon) and Lutris are listed and compiled in their own Wine or
  Proton prefix, and Steam installed as a Flatpak or Snap is found.
- **Vulkan games from every store** are recorded while you play and compiled on Windows, as Steam ones are: Epic, GOG,
  EA, Ubisoft, games added by hand, and the Xbox app. The recorder turns on for the game's own exe only, through
  one Vulkan loader settings file in SCSKiller's data folder and one registry value.
- For these games, Clear cache, the cache size shown and Before and after cover the driver's own Vulkan cache of the
  game, once SCSKiller has seen the game or its compile use it.
- A Vulkan game's page also says when the game ran as administrator outside a recording session, which stops the
  recorder from loading.
- A game's page has a **Compile size limit** for DirectX 12 games: a compile adds about that much at most to the game's
  shader cache. Recorded pipelines and ray tracing don't count toward it, and the game still adds its own shaders as
  you play.
- **HDD mode per drive**: Settings can turn HDD mode on only for the games on chosen drives, so games on an SSD
  are still read by the scan.
- Arknights: Endfield installed with GRYPHLINK, its own launcher, is found in the launcher's games folder. A copy
  installed elsewhere is still added with Add game.
- **Leave out a folder** or a whole drive from the Library's Folders menu: games installed there are not listed or
  read, and their recorders are taken out first. Picking the folder again in the menu lists its games again.
- **DCS World** compiles the shaders it built the last time you played. Before the first play its page says to play it once.
- **Before and after** can compile the after run with Max compile, where Max compile is offered. The report says so.
- Star Wars Outlaws uses your max anisotropy setting when it compiles without a recording.
**Added**
- The app log (app.log) records each start with its process id and each exit with its reason, and the next start says
  when the previous run ended without one.
- On Linux, games with anti-cheat that run on Vulkan can be recorded in a session you start from their page, as on
  Windows, once you allow it in Settings. Enabling the recorder there is at your own risk.
- On Linux, games with anti-cheat that Steam runs on DirectX 12 with Proton can be recorded in a session you start from
  their page too. Enabling the recorder there is at your own risk.
- On Linux, Vulkan games you added to Steam as non-Steam games can be recorded and compiled, like Steam's own Vulkan
  games.
- On Linux, DirectX 12 and DirectX 11 games that Steam runs with Proton also compile Steam's shader download for the game.
  After one start from Steam they compile without a recording. The game's page shows how many pipelines come from it.
**Fixed**

- On NVIDIA, a DirectX 12 game that creates again a pipeline it made and let go of this session gets it back at once
  while the recorder is on, instead of stalling on Windows' shader cache.
- On older NVIDIA drivers that keep their shader cache in AppData\LocalLow\NVIDIA\PerDriverVersion\DXCache, the game
  pages, the Cache page and Clear cache find the compiled cache there instead of showing none.
- On NVIDIA 621 and later, games that get shaders through Advanced Shader Delivery in the Xbox app keep the compiled
  pipelines at their next start instead of compiling again. The game page shows the size of the delivered shaders.
- On NVIDIA 621 and later, a compile that ran while files of an earlier one were still in use puts its compiled
  pipelines into the game's Windows shader cache instead of leaving them out. When they have to wait, the log says why.
- A compile shows Done only once its shaders are in the game's Windows shader cache, and never writes that cache while
  the game runs. A game started right after a compile no longer plays that session without them.
- On NVIDIA from driver 621, DirectX 12 compiles no longer wait for Windows' shader cache to flush each write to disk:
  about 1.7 times as fast. The time estimate uses the rate measured on the current driver only.
- Compiled shaders that couldn't go into the game's Windows shader cache yet, also after a compile was cut short, go in
  when the game exits or SCSKiller starts. Until then the game shows Not reached yet. A compile whose shader cache was
  damaged shows Not reached yet and compiles again.
- On NVIDIA 621 and later, compiled shaders also reach the game's Windows shader cache when a folder in its path has
  ".exe" in its name, or when moving them there was cut short. SCSKiller never writes that cache while another program
  has it open, and the time estimate stays right on PCs where the faster compile can't be used.
- On NVIDIA 621 and later, a DirectX 12 game's compiled pipelines count as present while Windows' shader cache still
  holds them, and as gone when that cache is emptied. Ray tracing, and DirectX 11 shaders of a game not known to run
  DirectX 12, still count by NVIDIA's own cache files. A compile no longer redoes a game's DirectX 12 pipelines when only
  NVIDIA files they don't use went.
- Star Wars Outlaws with its DLC installed reads its shaders instead of failing.
- A compile no longer makes the PC unresponsive: all of its work, the graphics driver's too, runs below other programs,
  and reading a game's files follows the Threads setting.
- Unreal games whose store starts another exe beside the Shipping exe, such as STAR WARS Jedi: Fallen Order, are
  compiled and recorded for that exe after its next launch, instead of showing Not compatible.
- On Linux, games run with Proton-CachyOS compile instead of asking you to start them from Steam.
- With Scan at start off, a game compiled before a restart no longer shows Ready and never warmed until the next
  scan.
- On NVIDIA 621 and later, a game's Disk space also counts the part of its compiled shaders Windows keeps in its
  D3DSCache folder, so it no longer looks much smaller than on earlier drivers.
- After a driver update, SCSKiller retries the pipelines that crashed the old driver one at a time before the rest,
  and keeps them all skipped once one still crashes, so a compile no longer resets the driver once per such pipeline.
- On NVIDIA 621 and later, the Cache page's Old driver group no longer lists cache files the new driver still uses,
  so deleting it no longer makes compiled games need rebuilding.
- Screen readers name the compile, refresh and page buttons.
- On NVIDIA driver 621, DirectX 11 games compiled with 1.3.0 no longer compile again after updating.
- On NVIDIA 621 and later, a game started through a junction, a subst drive or a mounted folder gets its compile,
  and Clear cache also takes its Windows shader cache from there.
- On NVIDIA 621 and later, a DirectX 12 game whose compiled shaders were removed from Windows' DirectX shader cache
  (Disk Cleanup) asks to be compiled again.
- The command line shows VAC for games whose only anti-cheat is VAC, in the list and in status.
- A game whose compile was stopped before it finished no longer shows Not compatible, and neither does a DirectX 11
  game on AMD whose compile finished.
- A game whose folder was moved or deleted says it couldn't read the game files, not that its engine isn't supported
  yet.
- Compiles on AMD no longer pick up pipelines known to crash the driver from the shared packs.
- The library check no longer stops at one game whose files never finish reading. After a few minutes that game shows
  that it couldn't be read in time and is retried at the next scan, and the other games are checked.
- When a game's packages are too big to read in the memory SCSKiller has, the message now says it ran out of its read
  budget while reading the packages, not that the game holds more shaders than a compile can read.
- Infinity Nikki reads the patch files in its Saved folder too.
- A game with anti-cheat that has a RenoDX add-on next to it shows the RenoDX tag, found by the add-on
  file's name only, without reading any of the game's files.
- A DirectX 12 game's page and the queue warn when the GPU doesn't support DirectX 12, instead of compiling shaders
  it can't build.
- SCSKiller itself uses far less memory while a large game compiles: the memory it used to read and plan the
  game goes back to Windows when the compile starts, not only after the queue ends.
- On Linux, games of the Flatpak Steam say why they can't be compiled or recorded yet, also in the Flatpak version of
  SCSKiller, instead of asking for a recording.
- On Linux, the recorder works in Windows Vulkan games under Proton, also in non-Steam games added to Steam. A Vulkan
  game with anti-cheat is detected as Vulkan and can be recorded in a session at your own risk, and the session leaves no
  Vulkan layer entry behind. A compile stopped while it runs Steam's pipelines shows the whole plan's count, and pipelines
  skipped for failing Vulkan validation stay in the final count. A game folder holding a Wine prefix is no longer searched
  through the prefix's drive links, and Wine's own DLLs in it no longer make a Vulkan game read as DirectX 12. A game in a
  Steam game's Proton prefix, or whose steam_appid.txt names a Steam game, counts as VAC when that Steam game has VAC.
  With NVIDIA's NVK driver, the note that games of a Flatpak Heroic or Lutris can't be compiled yet names Mesa's drivers,
  not AMD and Intel.
- **A game whose steam_appid.txt names a VAC game** counts as VAC, so the recorder stays out.
- On Windows, games whose Steam files can't be read aren't recorded, so a VAC game is never recorded by mistake.
- **DOOM Eternal** from the Xbox app is found by its game exe, not the larger mod sandbox beside it.
- Borderlands 3 is compiled for DirectX 12, its default, also before the game saved its settings.
- On Linux with NVIDIA, a Steam game with Shader Pre-caching turned off reads the compile too. SCSKiller puts it in the
  game's own cache file once it has seen the game play, or says to turn Shader Pre-caching on when it doesn't fit.
- On Linux with NVIDIA and Shader Pre-caching off, the compile reaches games under Proton such as Monster Hunter Wilds.
  SCSKiller found the game's own cache file under only some of the names the driver gives it, and couldn't write the
  file format the driver uses outside Steam's folder.
- With an Intel GPU, Clear cache, the Cache page and before and after recordings include the Intel driver's shader
  cache, and the Library and Settings show its 512 MB limit per game instead of an unknown limit.
- Numbers like "3 109 of 131 120" no longer show a wide gap between digit groups in locales such as Polish.
- On Windows, a Vulkan compile runs on all its threads instead of one. A compile during which the graphics driver
  is updated or reset stops and asks you to compile again, instead of counting the rest as rejected by the driver.
  Its progress also moves past pipelines the driver rejects.
- A recording session for Wuthering Waves added by hand waits for you to start the game from its launcher, instead
  of starting the game's exe, which asked for admin rights or closed without its launcher.
- Recording stays off in FINAL FANTASY VII REBIRTH while Shader Injector is installed, because both hook the same
  functions. Compiling works as before.
- HELLDIVERS 2 uses helldivers2.exe when the HD2Arsenal mod manager is in its folder.
- A scan no longer fails when a recording is being written at the same moment, for example by a second SCSKiller.
- The before and after panel shows in Vulkan games that run in HDR or with 10-bit colour, as it does in DirectX
  games.
- Call of Duty: Modern Warfare (2019) on Steam is recognised as a game with anti-cheat, and the recorder is taken out
  of it.
- Roblox is recognised as a game with anti-cheat, also when added by hand or installed with Bloxstrap or Fishstrap,
  and the recorder is taken out of it.
- Wuthering Waves from WeGame opens with its AES key, like the launcher's copy.
- Split Fiction on AMD compiles for the driver's profile of the game from the first compile.
- On AMD, a game the driver gives a cache of its own, such as S.T.A.L.K.E.R. 2, shows Not compatible after its first
  compile misses it, instead of asking for a second compile of several GB that misses too. A driver update lets it
  compile again.
- **Before and after works in DLSS games and Ubisoft Connect games**, such as Star Wars Outlaws. The recorder times
  their frames again. In DirectX 12 Ubisoft Connect games on Windows the countdown shows in SCSKiller, not in the game.
- **Assassin's Creed Valhalla** can be recorded again.
- A session at your own risk in a DLSS game with anti-cheat on NVIDIA says before it starts that DLSS loads DirectX 12
  before the recorder can, instead of starting a session that records nothing.
- The game page shows Max compile only where it compiles more, not for Vulkan or DirectX 11 games or on GPUs that
  cache whole pipelines.
- Unreal games whose launcher downloads game files into the game's Saved folder, such as Infinity Nikki, compile the
  shaders in those files too. When the launcher downloads new ones, the game asks to compile again.
- An Unreal game where only some game files are encrypted shows **Set AES key** on its page when SCSKiller can't find
  the key. The rest of its shaders compile until you add it.
- The recorder no longer stops recording with "the scskiller.ini in its folder isn't SCSKiller's" after a game update,
  and a game where that already happened records again.
- On Linux, a compile run by another SCSKiller (another data folder) no longer counts as its game running.
- On Linux, a compile whose own process is closing no longer takes it for the game starting, which showed the compile
  stopped for the game and then compiled again.
- On Linux with NVIDIA, a compiled game whose cache gets more room shows Needs rebuilding with how many more pipelines
  fit, instead of Warmed until the next compile.
- On Linux with NVIDIA, games whose DLSS loads DirectX 12 before the game can be recorded as on Windows, instead of
  showing that DLSS blocks the recorder.
- A Vulkan game from Steam that hasn't run from Steam on this PC yet asks you to start it once from Steam, instead of
  asking for a 5-minute recording.
- A Vulkan game's page says how many pipelines come from Steam's shader download and how many from your play, and why
  some are skipped, instead of showing DirectX rows.
- **Vulkan recording sessions** say when the game ran on DirectX or as administrator, which stops the recorder from
  loading. Picking Vulkan for a game that last ran on DirectX shows a warning before the session starts.
- Vulkan recording sessions in games with anti-cheat place the recorder in the game's folder for the session, which
  some games require. It is removed when the session ends.
- Games that load Vulkan at runtime, such as Enshrouded, are detected as Vulkan, and upscaler and middleware shaders no
  longer decide a game's graphics API.
- On Linux with NVIDIA, a large game's compile that fills its shader cache counts as compiled and is placed for the
  game, instead of failing. Steam games get a 17 GB cache instead of 10 GB, and the plan fits what the cache holds.
  With Mesa, the Library and Settings no longer show a 10 GB limit, because Mesa's cache for a Steam game has none.
- On Linux, When idle compiles wait until you've been away from the PC for two minutes on GNOME, KDE on X11 and
  desktops that report idle time to the system, instead of starting at once.
- On Linux, Ask me first shows the driver-update notification after you sign in, also when SCSKiller isn't running.
- On Linux with AMD and Intel, Clear cache deletes the game's shader cache, Steam's Shader Pre-Caching files for it
  included, which Steam processes again. In 1.4.0-alpha.1 it deleted nothing there, though the size and the note said
  it would.
- On Linux, Clear cache also deletes the game's vkd3d-proton cache that the compile writes, which Settings already
  counted in the cache's size.
- On Linux with NVIDIA, when a game's shaders don't fit, the queue and the game's page name SCSKiller's compile folder,
  17 GB for a game whose cache goes in Steam's 4 GB files, instead of a cache the driver keeps.
- On Linux, Settings shows the free space where the shader cache is, named by its mount point, instead of the root
  file system's.
- On Linux with Mesa, compiles no longer pass a 10 GB shader cache size, which Mesa's cache for a Steam game never used.
- A community recording that downloads while its game compiles is put in place after the compile, so the compile's
  result is shown for the recordings it compiled.
- On NVIDIA, game pages and the Cache page show how much of the driver cache is in use next to its size on disk,
  and the cache-limit warning uses the space in use.
- Marvel Rivals asks you to start it once from Steam after an install or update before it compiles, because its
  launcher clears the shader cache at the first start of a new install or season.
- On AMD, Cyberpunk 2077 and other games that create their device through AMD AGS keep recording after a game update
  changes AGS. An AGS the recorder can't work with is named in the app log.
- A compile with nothing to compile, such as a game whose shaders SCSKiller can't read yet, ends as Failed and says
  so, instead of showing Warmed.
- app.log no longer repeats "could not import ...scskiller.db" while a recorded game runs. The recording is imported
  when the game exits, as before.
- A before and after recording is no longer lost when the anti-cheat check of a large game runs out of time during
  its compile.
- A compile keeps going past pipelines that crash the GPU driver instead of stopping after a few hundred of them, as on
  older AMD cards with shared upscaler data. The progress shows how many were skipped so far.
- On Linux, a Linux program that stays open after the game closes, such as an overlay, no longer keeps SCSKiller
  waiting for the game to close. A Windows program that does is named in the log.
- With NVIDIA, the large-compile warning says when the driver would keep more of the cache in memory in game than this
  PC's RAM leaves free.
- Compiling a game again when only pipelines the driver refused are left says so, and that the ones compiled earlier
  are kept, instead of showing all of them as failed.
- Zenless Zone Zero no longer offers a recording session, because the game closes itself with the recorder in its
  folder. Compiling still works.
- The Witcher 3's page says when MFG Unlocker is next to the game, because its Path Traced Hair turns off while the
  recorder is in the game folder.
- On Linux, a plan bigger than the compile's cache keeps the pipelines whose shaders recordings saw, instead of the
  first ones found in the game files. Shader files that pair nearly every vertex shader with every pixel shader get each
  shader planned once, instead of every pair.
- On Windows with NVIDIA, shader files that pair nearly every vertex shader with every pixel shader get each shader planned once,
  instead of every pair, so planning such a game no longer runs out of memory.
- On Linux, pipelines whose shaders fail Vulkan validation, or that need a feature the GPU lacks, are shown as such,
  instead of as the GPU driver crashing on them.
- On NVIDIA, a compile starts NVIDIA Aftermath only when the game's recording or SCSKiller's rules for the game show
  the game starting it, and no longer because of the game's config files, such as for Gears of War: E-Day. When the
  recording doesn't show all its settings, such as for Borderlands 4, the next compile runs without Aftermath again if
  the game's next launch shows it didn't help.
- On AMD, Clear cache deletes driver cache files it isn't allowed to read, instead of saying they are in use.
- On Linux, Intel GPUs on Mesa's ANV driver and NVIDIA GPUs on Mesa's NVK driver compile through the same Mesa path as
  AMD, without the experimental switch.

## [1.4.0-alpha.2]

**Fixed**

- Compatible with NVIDIA driver 621.01.
- Game caches are much smaller, as a result of how NVIDIA now handles the game shader cache.

## [1.4.0-alpha.1]

Everything since 1.3.0.

- **After this update**, SCSKiller checks every compiled game again, which can take several minutes with many games. The app is
  slow until that ends. Later starts are fast.

**New**

- **SCSKiller on Linux**, as an AppImage for Patreon backers. It installs for your user only, without root, adds itself
  to the application menu and updates itself.
- **Vulkan games from Steam** are recorded while you play, and compiled on NVIDIA and AMD on Windows. The recorder adds only one
  small settings file to the game's folder, plus one registry value per game on Windows or one file per game in your
  Vulkan layer folder on Linux. Turning the recorder off removes them.
- On Windows with NVIDIA, Vulkan games from Steam such as No Man's Sky compile from Steam's shader cache without a recording.
- On Windows with AMD, a Vulkan game from Steam compiles after you start it once with the recorder on and Steam's Shader
  Pre-Caching on.
- Clear cache on a Vulkan game from Steam with NVIDIA or AMD also deletes Steam's shader cache
  of the game, so it frees the size it shows.
- **Alpha builds start after one sign-in with Patreon on the backer tier**, then run offline.
- **Crimson Desert** compiles from a recording, or without one once you paste the game's key on its page, as published.
- **Crimson Desert** also compiles its shadow shaders and the shaders its render passes share.
- The game page and the queue warn when this PC's DirectX 12 can't compile a game's shaders, for example Shader Model
  6.6 shaders on Windows 10 without the game's own DirectX 12 runtime.
- On Linux, Steam games under Proton, non-Steam games added to Steam included, read SCSKiller's compiled cache with no
  launch options to set.
- On Linux, Vulkan games from Steam read SCSKiller's compiled cache with no launch options to set, on NVIDIA, AMD and Intel.
- **Before and after recording** works for Vulkan games from Steam.
- On Linux, Before and after works on NVIDIA, AMD and Intel. The game's Steam shader cache is set aside for the before
  run and put back after it.
- On Linux, Settings has Remove recorders, to use before you delete the AppImage.
- On Linux, Start when you sign in works, and the driver-update choice When idle works too, including in Game Mode.

**Faster and steadier**

- An update download that was cut off continues where it stopped instead of starting over.
- The window stays responsive while SCSKiller reads a large game's shaders in the background after an update.
- A game's page stays responsive while a scan or a compile runs (thanks @misutesu-desu).
- **A stopped compile no longer starts over at 0%** when SCSKiller checks its plan again after a restart and nothing
  in it changed.
- **A game set to DirectX 11** no longer compiles the DirectX 12 pipelines of its community recording, which DirectX 11
  never uses.

**Games**

- **Star Wars Outlaws** is supported. It compiles without a recording.
- **Unsupported games** no longer show a Community data line on their page, which made them look supported.

**Recorder and anti-cheat**

- **DLSS games on NVIDIA can be recorded**, such as Star Wars Outlaws. Next to the recorder, SCSKiller adds a small
  winmm.dll, version.dll or dinput8.dll that loads it before DLSS does, never in place of a mod's file.
- **Ubisoft Connect games** no longer close at start with the recorder on, such as Assassin's Creed Valhalla. In these
  games and in DLSS games the recorder records shaders but doesn't time frames, so Before and after isn't offered.
- **Forza Horizon 5** contains anti-cheat. It counts as an anti-cheat game from Steam, the Xbox app or added by hand,
  so the recorder stays out unless you enable it at your own risk. SCSKiller isn't responsible for bans.

**Fixed**

- **Crimson Desert on AMD**: no more failed shaders in a compile, and every render pass gets its full shader set.
- **Crimson Desert** gets the same community recording on every PC with the same game version.
- Unity games started with -force-d3d11, -force-d3d12 or -force-vulkan in their Steam launch options are compiled for
  that API.
- The log no longer says a recorder another SCSKiller copy placed records nothing. It may still record, as its game page
  says.
- On NVIDIA, the first Before and after run of a game you played before SCSKiller now counts instead of being played
  again.
- On Intel under Windows, a Vulkan game's page says it runs on Vulkan, which isn't supported on this GPU yet.
- On AMD and Intel, a game's page no longer says its DLSS compiles, the DLSS tag's tip says DLSS runs only on NVIDIA,
  and a game that can't compile no longer says its upscalers compile.
- On Linux, a screen reader no longer reads an empty line on the alpha sign-in screen.
- The recorder files of a game you uninstall while SCSKiller runs are removed without restarting SCSKiller.
- A Steam Vulkan game that was uninstalled, or reinstalled with anti-cheat, no longer keeps the Vulkan recorder
  registered for it.
- A game is no longer compiled without its middleware shaders when their DLL couldn't be read. The compile fails and
  the next one compiles it in full.
- SCSKiller no longer writes through a link someone placed in a game's folder.
- On Linux, a compile started from the terminal now holds back an update of the app until it ends.
- On Linux with NVIDIA, a big game reads all of its compiled cache, so its shaders no longer compile again while you
  play.
- Uninstalling SCSKiller also removes its notification registration from Windows.
- The recorder's log names the pipelines a game failed to create, with the error, so a report shows them.
- Reading the shaders of a big Unreal Engine game like Days Gone uses less memory. Maximum memory for compiling, set
  above half your RAM, now also raises the memory limit for reading shaders and building the plan.
- Wuthering Waves and other Unreal games no longer show as not supported when their files are busy while you play.
- A game with new pipelines says when compiling again redoes the whole game, or all its DirectX 11 shaders.
- **Send report** no longer fails with AMD graphics drivers.
- On AMD, when Clear cache can't delete a driver cache file that no app holds, it says that Reset Shader Cache in AMD
  Software removes it.

**Known issues**

- Vulkan games aren't supported on Intel under Windows yet.
- Recording games with anti-cheat isn't on Linux yet.
- Alpha builds need a Patreon backer sign-in once.
- On Linux, a compile started from the terminal and one started in the app don't wait for each other yet, and neither
  does clearing a cache, so run one at a time.

**Not tested yet**

- Other Vulkan games
- Crimson Desert's view-instancing hits
- AMD Crimson Desert
- Wukong in game


## [1.3.4] - 2026-10-27

**New**

- For games with Advanced Shader Delivery, the compile will skip the shaders Windows already delivered and compile only
  the rest.
- The Library will show what SCSKiller checks by itself after an update, those checks will use at most a quarter of the
  RAM up to 8 GB, a compile you start will no longer wait for them, and a check that can't finish will keep the game's
  plan and no longer run again at every start.
- Games with anti-cheat will get an "Always record" switch that records every launch after you confirm it once, at your
  own risk.
- The recorder will check a game's steam_appid.txt for VAC, and a game whose steam_appid.txt names an app Steam doesn't
  know will offer a recording session at your own risk.
- Settings will offer the same shader cache sizes as the NVIDIA app, from 512 MB to Unlimited.

**Improved**

- A Plague Tale: Requiem will compile every shader combination found in its files instead of about 97%.
- The RE4 Chainsaw Demo will compile from its own root signatures on AMD.
- While SCSKiller hands a finished compile to the game's cache, the Library and the game's page will show its progress
  ("Handing the compile to the game's cache: N%"). Clear cache will stop it first instead of failing with files in use.
- A game's page will say why pipelines couldn't be compiled, and a problem report will list the top reasons.
- A pipeline that crashed or hung the GPU driver will stay skipped on later compiles until the driver changes, and a
  pipeline that hangs before the compile's workers start will no longer stall the compile.
- Compile progress will keep its total after a driver crash or hang, clear the retry note once the compile is past it,
  and estimate the time left from a stopped compile until one completes.
- Unreal Engine 5.8 games such as Fortnite will compile their compute shaders that use NVIDIA's extensions the way the
  game creates them, so those will no longer compile during play on NVIDIA.
- On NVIDIA, the pipelines a stopped or failed compile created will no longer be compiled again after the game's plan
  changes or SCSKiller updates.
- The cache hand-off after a game session will read the game's whole cache only once a week instead of every time.

**Fixed**

- Unreal Engine games with a recording will compile all their pipelines again, instead of leaving out a large part of
  them on NVIDIA. The affected games will compile once more after the update.
- A game's coverage will show what its last compile covers, and pipelines found since then will only be counted in the
  line that offers to compile them, instead of lowering the percentage.
- The Shader layouts row will no longer say DirectX 11 for a DirectX 12 game that compiles from its recordings.
- Resident Evil Requiem (demo) will compile the pipelines its game creates on NVIDIA, also from a shared recording made
  on another GPU.
- Pipelines whose pixel shader sets the stencil reference will no longer be compiled on NVIDIA, where they always fail.
- Resident Evil Requiem (demo) and Dragon's Dogma 2 Character Creator will compile most of their pipelines instead of
  about a third.
- DLSS games on NVIDIA whose exe loads none of winmm, version or dinput8, such as Dying Light: The Beast, will be
  recorded, and no game will show Not supported because of DLSS.
- An Unreal game whose files are only partly readable without its AES key will say it needs the key for full coverage,
  and will still compile what it can read.
- "Delete all" will no longer say a compile is in progress after a compile has finished.
- NVIDIA players without a recording will no longer compile Unreal games with a root-signature layout the game doesn't
  use (an extra, larger compile). The first compile after the update redoes those games once.
- Microsoft Flight Simulator 2024 and other games whose shader delivery has no engine name will take the compiled cache
  again instead of waiting for it with "Not reached yet".
- SCSKiller will hand a compile to a game whose shader cache folder you moved to another drive with a junction or link,
  without rereading it at every start, and the game's page will say why when it can't.
- A compile's shader cache will no longer go missing when one of its files is damaged or can't be read, or when a driver
  update comes before the game's next start.

## [1.3.3] - 2026-10-26

**Fixed**

- On NVIDIA, a rebuild after a launch that still compiled some new pipelines compiles only those, instead of every
  pipeline again, such as in Borderlands 4.
- After a compile, saving the driver's cache, finishing up and copying the cache to the game's folder show as their own
  steps with their progress and time, instead of sitting at 100%.
- Frame times are measured with OptiScaler or another mod installed as dxgi.dll in the game's folder.
- Frame times are measured with FSR mods that replace or rename the game's FSR file, such as HelixSR.
- The recorder works in Dragon's Dogma 2 Character Creator again, instead of turning itself off as "not signed by
  SCSKiller".
- Call of Duty: Modern Warfare (2019), Call of Duty HQ and Warzone on Steam count as games with anti-cheat, also when
  added by hand, and the recorder is taken out of them.
- The recorder steps aside from frame generation's swap chain (AMD FSR, DLSS through Streamline) and stops overlays
  from looping through it.
- With "Download shared shader hashes" on, NVIDIA pipelines this PC recorded compile with the game's own NVIDIA settings,
  not another player's, so a rebuild after a download no longer compiles them all again.
- A recorder from an older SCSKiller version found in a game with anti-cheat no longer turns that game's recorder on.
- When the recorder doesn't run in a game, for example because of anti-cheat or a changed game, nothing of it stays
  active in that game.
- Games with anti-cheat compile through their ReShade and RenoDX too, as other games do.
- Shared shader hashes pool across PCs: every install of the same game build adds to one shared set and downloads it,
  instead of most PCs getting a set of their own. A recording shared before this version is shared again once, within
  7 days.
- A game recorded at several texture filtering settings no longer compiles every pipeline again for each of them: a
  compile takes the one the game uses and the last other one, and the game page counts those pipelines too.
- A damaged shared shader download no longer makes a game compile fewer pipelines than its own recording holds.
- Pipelines skipped after a spot where the GPU driver kept failing are no longer said to have crashed it, and a partly
  compiled game's status counts the pipelines the driver crashed on apart from the ones it refused.
- A compile no longer stays at "Compiling" forever when a process the GPU driver or a ReShade add-on left running holds
  its output. It ends about 30 seconds after the compile process exits, with an error when the compile didn't finish.
  A compile that stops making progress (a ReShade add-on or the GPU driver hanging) ends after 10 minutes with an error
  instead of showing Compiling forever.
- On NVIDIA, part of Gears of War: E-Day's compiled pipelines now match the ones the game uses: they are built with
  NVIDIA's shader extension and NVIDIA Aftermath set up as the game sets them.
- On NVIDIA, compiles with "Download shared shader hashes" on no longer stop on GPU driver crashes in the community's
  ray tracing pipelines. Where SCSKiller builds a game's ray tracing collections from its files those pipelines are left
  out, and the others compile last.
- A compile of a very big plan, such as Marvel Rivals with all its shader combinations, no longer fails after about
  3 minutes with nothing created.
- On AMD, God of War Ragnarök's compile reaches the game's own driver cache instead of showing Not compatible.
- A compile stopped by a game starting or by GPU driver crashes and hangs keeps its progress. Compiling again goes on
  past the spot where the driver kept failing, and what it skips there counts as skipped, not failed.
- A game whose files hold no shaders SCSKiller can compile shows why on its page after its first compile, instead of
  showing as compiled.
- After a compile that was stopped and continued, the next compile on the same driver leaves out what its last part
  compiled instead of compiling everything again. Putting a compile into the game's Windows shader cache shows as its own
  step with its progress, and takes seconds when that cache already holds it all.
- Clear cache on a game's page deletes its Windows shader cache on every driver and whatever its size, also for games
  with anti-cheat, as the Cache page says.
- SCSKiller no longer crashes at start on NVIDIA PCs whose driver lists its files without their folder.
- On NVIDIA, "The compile didn't help" clears once a later launch shows the compile in use. Until then the game
  also missed driver-update recompiles and new-shader prompts.
- On NVIDIA, putting a compile into a game's Windows shader cache no longer checks the whole cache again while the game
  hasn't changed it. With a cache of about 100 GB that check took up to an hour at start, and compiles waited for it.
- When SCSKiller can't read a cache size or another detail in the background, it carries on without it instead of
  closing.
- On AMD, compiling a DirectX 11 game no longer leaves an unused DirectX 12 shader cache file under the game's name.
- On AMD, the recorder installs in Cyberpunk 2077 again. The game's latest update added a file with the name the
  recorder used.
- When the recorder can't go into a game, for example because Windows Security removed one of its files, the game's page
  says why instead of showing nothing.
- Clicking a SCSKiller notification while SCSKiller is running no longer closes it.
- On NVIDIA from driver 621.01, a game moved to another folder or drive keeps its compiled shaders: they go into the
  Windows shader cache of its new location when the game exits or SCSKiller starts.
- On AMD, a game that registers its own app name with AMD's driver compiles for it again once a launch shows it
  using that cache, instead of staying on a cache the game doesn't read.
- Older Unreal Engine 4 games such as ABZÛ, Little Nightmares and Styx: Shards of Darkness no longer fail with
  "outgrew its memory limit" while reading their shaders.
- 32-bit games show that they aren't supported yet, instead of Ready to compile and a compile that doesn't reach them,
  and the recorder no longer goes into their folder.
- Reinstalling the same ReShade or RenoDX files no longer asks for a new compile of a game that compiles through them.
- A compile keeps going past pipelines that crash the GPU driver instead of stopping after a few hundred of them, as on
  older AMD cards. The progress shows how many were skipped so far.
- A folder you add no longer lists programs that aren't games (like Telegram Desktop), which could pause the compile while they ran.
- DLSS games on NVIDIA can be recorded, such as Resident Evil Requiem and Star Wars Outlaws. Next to the recorder,
  SCSKiller adds a small winmm.dll, version.dll or dinput8.dll that loads it before DLSS does, never in place of a
  mod's file.
- Ubisoft Connect games no longer close at start with the recorder on, and Before and after times their frames.
- Before and after measures the game's rendered frames with frame generation on: DLSS frame generation (such as
  Resident Evil Requiem), FSR frame generation and OptiScaler's own frame generation, also in Ubisoft Connect games.
- RE Engine games such as Resident Evil Village leave out the pipelines the game lists whose two shaders don't fit
  together, which AMD's driver refused (18 in Resident Evil Village).
- After updating from 1.3.0, app.log says why the first compile runs every pipeline again.
- On NVIDIA, a compile from SCSKiller 1.3.1 whose result never reached the game's own shader cache, because SCSKiller
  closed while copying it, is copied there when SCSKiller starts. Clear cache also removes a copy still waiting to go
  in, and removing earlier compiles on the Cache page no longer says "Nothing needs to compile again".
- A compile you start no longer waits while SCSKiller checks other games after an update, and the queue shows what it
  waits for.
- Frame times keep going after a game re-creates its swap chain under Streamline or OptiScaler, such as when frame
  generation is turned on or off.

## [1.3.2] - 2026-10-22

**Fixed**

- On NVIDIA from driver 621.01, DirectX 12 compiles no longer wait for Windows' shader cache to flush each write to disk:
  about 1.7 times as fast. The time estimate uses the rate measured on the current driver only.
- A compile shows Done only once its shaders are in the game's Windows shader cache, and never writes that cache while
  the game runs. A game started right after a compile no longer plays that session without them.
- Compiled shaders that couldn't go into the game's Windows shader cache yet, also after a compile was cut short, go in
  when the game exits or SCSKiller starts. Until then the game shows Not reached yet. A compile whose shader cache was
  damaged shows Not reached yet and compiles again.
- On NVIDIA 621.01 and later, compiled shaders also reach the game's Windows shader cache when a folder in its path has
  ".exe" in its name, or when moving them there was cut short. SCSKiller never writes that cache while another program
  has it open, and the time estimate stays right on PCs where the faster compile can't be used.
- On NVIDIA 621.01 and later, games that get shaders through Advanced Shader Delivery in the Xbox app keep the compiled
  pipelines at their next start instead of compiling again. The game page shows the size of the delivered shaders.
- On NVIDIA, a DirectX 12 game that creates again a pipeline it made and let go of this session gets it back at once
  while the recorder is on, instead of stalling on Windows' shader cache.
- On older NVIDIA drivers that keep their shader cache in AppData\LocalLow\NVIDIA\PerDriverVersion\DXCache, the game
  pages, the Cache page and Clear cache find the compiled cache there instead of showing none.
- On NVIDIA 621.01 and later, a compile that ran while files of an earlier one were still in use puts its compiled
  pipelines into the game's Windows shader cache instead of leaving them out. When they have to wait, the log says why.
- On NVIDIA 621.01 and later, a DirectX 12 game's compiled pipelines count as present while Windows' shader cache still
  holds them, and as gone when that cache is emptied. Ray tracing, and DirectX 11 shaders of a game not known to run
  DirectX 12, still count by NVIDIA's own cache files. A compile no longer redoes a game's DirectX 12 pipelines when only
  NVIDIA files they don't use went.
- A compile no longer makes the PC unresponsive: all of its work, the graphics driver's too, runs below other programs,
  and reading a game's files follows the Threads setting.
- With Scan at start off, a game compiled before a restart no longer shows Ready and never warmed until the next
  scan.
- On NVIDIA 621.01 and later, a game's Disk space also counts the part of its compiled shaders Windows keeps in its
  D3DSCache folder, so it no longer looks much smaller than on earlier drivers.
- After a driver update, SCSKiller retries the pipelines that crashed the old driver one at a time before the rest,
  and keeps them all skipped once one still crashes, so a compile no longer resets the driver once per such pipeline.
- Unreal games whose store starts another exe beside the Shipping exe, such as STAR WARS Jedi: Fallen Order, are
  compiled and recorded for that exe after its next launch, instead of showing Not compatible.
- On NVIDIA 621.01 and later, the Cache page's Old driver group no longer lists cache files the new driver still uses,
  so deleting it no longer makes compiled games need rebuilding.
- On NVIDIA 621.01 and later, a game started through a junction, a subst drive or a mounted folder gets its compile,
  and Clear cache also takes its Windows shader cache from there.
- On NVIDIA 621.01 and later, a DirectX 12 game whose compiled shaders were removed from Windows' DirectX shader cache
  (Disk Cleanup) asks to be compiled again.
- On NVIDIA driver 621.01, DirectX 11 games compiled with 1.3.0 no longer compile again after updating.
- A game whose compile was stopped before it finished no longer shows Not compatible, and neither does a DirectX 11
  game on AMD whose compile finished.
- Roblox is recognised as a game with anti-cheat, also when added by hand or installed with Bloxstrap or Fishstrap,
  and the recorder is taken out of it.
- On NVIDIA, a compile starts NVIDIA Aftermath only when the game's recording or SCSKiller's rules for the game show
  the game starting it, and no longer because of the game's config files, such as for Gears of War: E-Day. When the
  recording doesn't show all its settings, such as for Borderlands 4, the next compile runs without Aftermath again if
  the game's next launch shows it didn't help.
- On NVIDIA 621.01 and later, a game whose Windows shader cache expects another identity than its Advanced Shader Delivery
  registration says can be fixed from the server, without an app update.
- Games that get compiled shaders from Windows through Advanced Shader Delivery show an Advanced Shader Delivery tag with its size.
  When the delivered shaders fit your driver, the game page and `status` say a compile doesn't add much.
- On NVIDIA with sharing on, the first launch after a compile reports how many pipelines Windows' shader cache still had
  to add, also with the recorder off, so games the compile misses show up on the server. Only counts and the game
  build's hash are sent.

## [1.3.1] - 2026-10-22

**Fixed**

- Compatible with NVIDIA driver 621.01.
- Game caches are much smaller, as a result of how NVIDIA now handles the game shader cache.

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
