# Changelog

All notable changes to the SCSKiller app and command line. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed

- In a narrow window the Library's buttons wrap onto another line under the summary instead of being cut off.

## [1.2.3] - 2026-10-06

### Added

- "Closing the window quits SCSKiller" in Settings, off by default. On, the close button quits the way Quit in the
  notification area does: a running compile finishes saving first, and a downloaded update installs if it is set to.
- "Scan games when SCSKiller starts" in Settings, on by default. Off, SCSKiller shows the last list when it starts and
  reads a game again only when you refresh, when the game exits, or when Steam or the Xbox app installs or updates one.
- On NVIDIA, Unreal Engine 4.25 games (Returnal) compile their ray tracing shaders without a recording, with the root
  signatures and collection layout 4.25 builds. Before, every ray tracing shader needed a recording, and those compiled in
  play were most of Returnal's stutters after a compile.
- "Install updates automatically" in About, on by default: a downloaded update installs the next time SCSKiller starts,
  including when it starts with Windows, or when you quit it from the notification area. It waits while a compile, an
  offline session or a game runs, and never installs during Windows' shutdown. "Restart to update" still installs it at
  once, with the setting off, only that button does.

### Fixed

- Unreal games from the Xbox app (Dead Island 2) were compiled under a launcher stub, not the game's own exe: compile them again.
- Dead Island 2's compile matched none of the pipelines the game creates: its engine was taken for Unreal 4.27, whose
  root signatures differ from those of the 4.25 build it runs on. Compile it again.
- Games from the Xbox app were never recorded: the recorder passed every launch through as "not armed", because Windows
  names a packaged game's exe by its WindowsApps path, not the folder the Xbox app installed it to.
- An Unreal game's crash reporter writing its symbol files into the game folder turned the recorder off until the next check.
- Xbox app games with ReShade and RenoDX in the game's package folder, next to MicrosoftGame.config (Beast of
  Reincarnation), weren't compiled: SCSKiller said it couldn't tell whether the game loads ReShade from there. Windows
  loads it from that folder for an Xbox app game, so these games now compile through ReShade and RenoDX. A d3d12.dll
  in that folder loads instead of the recorder, so the recorder stays out of such a game and the game page says why.
- On Windows 10, a compile of a game that ships its own D3D12 runtime (the Agility SDK, a `D3D12` folder beside the
  exe) could fail its pipelines with `replay of 'S' (pipeline stream) hr=0x80070057`: it ran on Windows' older D3D12,
  which lacks shader model 6.6 and the newer pipeline settings the game uses. The compile now runs on the game's runtime.
- Maximum mode's description said it saves well under 1 ms per pairing. It now says it seems to help most on AMD, and what it costs.
- Monster Hunter Wilds and other RE Engine games could sit at "Building plan" for hours and then crash SCSKiller: with a
  recording, the plan tried every pairing of a material's vertex and pixel shaders, though only pairs a recording has
  seen can be compiled. It now pairs only those: PRAGMATA's plan takes 2-3 seconds and under 0.5 GB instead of 46 seconds and
  5.7 GB, with the same pipelines.
- A game page no longer shows a warning when the driver skips pipelines in a compile: the compile line says how many.
- Every start read through every game's folders and DLLs again, even with nothing installed or updated: with many games
  on a hard disk it ground for minutes after Windows started, and the list took up to 20 seconds to show. A start now
  reads a game again only where something changed (its build, its exe, the files beside the exe), at low disk priority:
  on a PC with 64 games it reads 50 MB instead of 830 MB and lists them in about a second. After an SCSKiller update the
  list shows at once and the games are detected again in the background. The recorder's anti-cheat check no longer
  walks every recorded game's folders every 2 minutes: it walks one when its folders change.
- The Witcher 3 crashed at start with the recorder installed and frame generation on (FSR 3, with the Steam overlay):
  the recorder's frame-time hook and the overlay's called each other until the game ran out of stack. Once FSR 3 or XeSS
  frame generation is on, or OptiScaler's frame generation is set up, the recorder measures no frame times for that
  launch, it still records.
- A game with RenoDX and ReShade loaded by OptiScaler (ReShade64.dll with `LoadReshade=true` in OptiScaler.ini) compiles
  through a copy of OptiScaler, ReShade and the HDR mod, and records. Before, it wasn't compiled. A game whose HDR mod
  still blocks it names the case and its fix in one line: ReShade not next to the exe, ReShade under a name the game
  doesn't load, LoadReshade off, or Luma.
- With REFramework installed, RE Engine games (Monster Hunter Wilds) recorded nothing: REFramework makes the recorder
  report its path as a copy in a `_storage_` folder, where it found no settings. It now records beside the game's exe.
- A file a mod or the game writes beside the exe as it runs (a crash dump, a numbered log, a shader cache) no longer
  stops the recorder until SCSKiller checks the folder again. A launch that still isn't recorded for a change says to
  start the game again.
- With a recording, an Unreal Engine 4.25 game (Returnal) compiled none of the ray tracing shaders it hadn't recorded:
  its ray tracing collections have no state object config, which SCSKiller always added, so the recorded ones never
  rebuilt and no rule was learned. They rebuild now, and Returnal's recording compiles its other 13,335 ray tracing
  libraries.
- More anti-cheat is recognised, so the recorder stays out of these games and comes out at the next check where it was
  installed: Anti-Cheat Expert's and Tencent's other files (Delta Force), every HoYoverse game by its own exe and the
  HoYoPlay launcher a Steam install carries (Zenless Zone Zero, whatever its anti-cheat driver is called in a version),
  and EA Javelin's game service launcher. These games still compile from recordings and the community
  database.
- A Riot Games title added by hand (VALORANT) could get the recorder: Riot Vanguard installs outside the game's folder,
  so SCSKiller found no anti-cheat there. Every Riot Games title (VALORANT, League of Legends and Teamfight Tactics,
  Legends of Runeterra, 2XKO) is now anti-cheat by its exe and by its "Riot Games" folder, the recorder refuses to
  record in them, and a recorder already in one comes out at the next check.
- Turning the recorder on sometimes failed with "couldn't install: scskiller.ini is being used by another process", or
  was turned off again at the next scan, when it came while SCSKiller converted a recording stored by SCSKiller 1.0.
  The recorder now waits for the conversion.
- An update downloaded before a restart now installs at the next start even when the PC has no network yet at sign-in.
  Before, it waited for the update server to confirm it, and on a pre-release channel for the Patreon sign-in too, so
  it often only showed "Restart to update" later.
- When one update server asked SCSKiller to wait, every update check waited, on every channel, and "Check for updates"
  only said so: GitHub limiting the stable feed also held back the internal and beta feeds, and the package server's
  daily download limit held back the feeds. Only requests to that server wait now, the other feeds are still read, and a
  newer update they offer still downloads. An update taken back from a pre-release feed is no longer kept for installing
  just because the beta or alpha channel has no feed.
- Adding a game by hand whose Unreal Engine is split into several DLLs (Returnal) picked a 32-bit Launcher.exe beside the
  game and refused it as 32-bit. The game's own exe is added now, and neither a 32-bit program nor an installer beside a
  pick is taken for the game.
- An Unreal Engine game with a larger program beside its exe was found from a store as that program: a 32-bit launcher
  (Returnal's Launcher.exe) or the Epic Online Services installer (Returnal on Steam). Its compiles went to the wrong
  driver cache and the recorder was set up for that program, and adding it by hand from the exe in its main folder was
  refused as 32-bit. The game's own -Win64-Shipping exe is found now, and installers and engine helpers beside it are
  never taken for the game. Compile such a game again: the earlier compile went to the other program's cache.
- Neverness to Everness showed as "Unreal Engine 5.5 · custom fork" and was read as its closed beta's engine. An
  anti-cheat game's engine version comes from its game files, which can't tell 5.5 from 5.7, and of the two engine forks
  named like the game SCSKiller took the beta's (5.5) over the release's (5.6). A fork named exactly like the game now
  wins, from any version its files allow, the game shows as Unreal Engine 5.6.
- FINAL FANTASY VII REMAKE INTERGRADE was taken for a DirectX 11 game, so it showed "not supported" on AMD and
  compiled only DirectX 11 shaders on NVIDIA. Its engine starts DirectX 12 unless it's launched with -dx11. It's a
  DirectX 12 game now: it asks for a recording, and compiles from it.
- Dead Island 2 was taken for a DirectX 11 game. Its engine sets DirectX 12 in its own base config, which SCSKiller
  didn't read, it reads it now for every Unreal Engine game, so a game whose engine defaults to DirectX 12 there is
  recognised as DirectX 12 too.
- Dead Island 2 couldn't be compiled: "none of the game's shader code decompresses". Its engine stores its shaders
  compressed with Zstandard, and names that format in the shader library, which SCSKiller misread. All of its 53,648
  shaders are read now.
- Unreal Engine 4.25 games' shipped pipeline cache was skipped, so a few of the pipelines these games create themselves
  weren't compiled (Returnal: 38 shader and root-signature pairs of 3,939). It's read now, and warmed games get their plans
  rebuilt when the PC is idle.
- On AMD, The Witcher 3 with ray tracing showed "Needs rebuilding: N new pipelines recorded" after every play session,
  however often it was compiled. The game names its ray tracing pipelines anew each launch, and AMD's driver reuses one
  only exactly as it was built, so no compile can prepare the next launch's. They're still compiled, but no longer count
  as new.
- On NVIDIA, a recorded pipeline whose shaders were all compiled before, in another pipeline, no longer counts as a new
  pipeline: NVIDIA's driver compiles each shader once with its root signature, whatever the rest of the pipeline. In The
  Witcher 3 these were about half of the new pipelines after each play session (cull modes, depth biases and new pairings
  of known shaders), which kept the game at "Needs rebuilding" though a compile added nothing.
- A game compiled without its ray tracing showed "Needs a 5-min recording" in orange. It shows as warmed now, with
  "Ray tracing needs a 5-min recording" under it, and its page still offers the recording.
- A game's page no longer stutters as it opens, and Clear cache asks at once: sizing Windows' shader cache read every
  app's cache files. A second click on Clear cache while it waited closed SCSKiller.
- An Unreal Engine game with a malformed shader stopped at "Reading shaders" with an error. That shader is skipped now
  and counted as unparseable, as other malformed shaders already were.
- Reading the shaders of an Unreal Engine game that keeps them in its material packages (Codename CURE II) could fill the
  memory, and Windows then showed SCSKiller as not responding: it read as many large packages at once as the PC has
  threads, each with all its shaders decoded. It reads packages within a share of the free memory now (2 GB at most)
  and handles each shader as it's found.
- A compile paused for over a minute failed when it resumed: every pipeline still compiling when it was paused looked
  stuck to SCSKiller, which gave up after repeated faults. Likewise, a pause of over 10 minutes while a finished compile
  was writing the driver cache cut that write short. The time a compile is paused no longer counts.
- DRAGON BALL: Sparking! ZERO and other encrypted Unreal games whose key the scan missed now open without entering it.
- Unreal games with ray tracing turned off no longer ask for a ray-tracing recording.
- A compile could stop itself over and over with "stopped while <game> is running" when the game wasn't running:
  SCSKiller took its own compile, which runs under the game's exe name, for the game whenever that process wasn't a
  direct child of its compile helper. It recognises its own compile by the folder it runs from now.
- On a CPU without AVX2, SCSKiller closed at "Reading shaders": the Oodle library it downloads to read Unreal Engine
  and FromSoftware games' files needs AVX2 and BMI2. On such a CPU it reads Oodle-compressed files without that library
  now, and Oodle data that still can't be read says that the CPU lacks AVX2/BMI2.
- One game with a file dated past what .NET can read failed the whole scan with "Not a valid Win32 FileTime", so no
  game was listed. That game now shows the error, its recorder stays as it was until it can be read again, and every
  other game scans.

## [1.2.2] - 2026-10-04

### Fixed

- An offline session without EasyAntiCheat (ELDEN RING) didn't start on a PC where SCSKiller had never armed a recorder:
  it stopped with "Could not find a part of the path" for a file in %LOCALAPPDATA%\SCSKiller\armed. It starts now, and every other
  step that reads or clears the recorder's ledger works when its folder isn't there yet.
- A game you added by hand and recorded, which a store or launcher now lists (such as Zenless Zone Zero, found through
  HoYoPlay), compiles from that recording again. The recording moves to the store's entry of the game, which before
  started without it, so an anti-cheat game showed as not supported. An anti-cheat game is compiled whenever its game
  files, a recording made on this PC or a community recording give a plan; only the recorder stays out of it.
- An anti-cheat game with no recording that the community database has says so, and where to get it.
- When a store entry takes over a game you added by hand, the recorder installed for it comes out of its own folder, every
  file SCSKiller installed and recorded there included, for each copy you added; what it recorded joins the store entry.
- A different build of the same version (a pre-release and the release) checks every game again instead of reusing the
  last build's results.
- A game compiled before 1.2.1 with a RenoDX HDR mod showed "an HDR mod was installed since the compile" after the update,
  although the mod had been there all along. It now says the last compile didn't run through the game's HDR mod; compiling
  again runs through it, as 1.2.1 does for every such game.
- The recorder stays armed when a mod beside the game writes a log, an ini, a screenshot or a save as the game starts
  (OptiScaler's log, a DLSS frame generation enabler's log), so those launches are recorded. Before, any new file
  anywhere in the game folder disarmed it until the next check, and the launch was silently not recorded. Any other new
  file (a program file under any name included), anything named like anti-cheat, or a changed game exe still disarms
  it, and each disarm is logged with the file.
- A launch the recorder doesn't record now says why on the game's page ("The last launch wasn't recorded: ...").
- A mod's d3d12.dll that can't be renamed for "Record alongside" (OptiScaler, Special K) is named on the game's page.
  For OptiScaler, which stops working renamed, the page says to rename it to dxgi.dll (or winmm.dll or version.dll),
  names OptiScaler supports and the recorder records beside.
- A recorder moved next to the exe a game really runs (Stellar Blade's PatchData copy) is armed as soon as it moves, so
  the next launch is recorded instead of only one after the next check.

## [1.2.1] - 2026-10-04

### Added

- Finds installed games from NCSOFT's PURPLE launcher, in a PURPLE section of the Library. AION 2 is listed as not
  supported: its game files are encrypted, and its anti-cheat (NCGuard) keeps it from being recorded. Play starts a PURPLE
  game through PURPLE, as its desktop shortcut does (not for anti-cheat games, which SCSKiller never starts).
- Finds games installed with HoYoPlay (Genshin Impact, Honkai: Star Rail, Zenless Zone Zero, Honkai Impact 3rd), in a
  HoYoPlay section of the Library. Every HoYoverse game ships a kernel anti-cheat, so these games are never recorded;
  more of their anti-cheat files are recognised when the same games come from Steam or Epic Games.
- War Thunder installed with Gaijin's launcher shows up in the Library, and on NVIDIA SCSKiller compiles all of its
  DirectX 11 shaders without a recording. On DirectX 12 it compiles the pipelines the game's shader definitions
  name, with the root signatures the game builds from them, also without a recording.
- Control (Remedy's Northlight engine, DirectX 12) compiles without a recording: SCSKiller reads the game's shader files
  and the pipelines they pair, with the root signatures the game builds. Ray tracing shaders are compiled too, without
  a guarantee that the game reuses them yet. On NVIDIA its DirectX 11 shaders are compiled as well, for playing on
  DirectX 11.

### Changed

- Before it writes into a game folder, SCSKiller reads the list of running programs once to tell whether the game runs,
  instead of twice. What counts as running is the same.
- A compile that completes with pipelines the driver rejected keeps its log as `warm-rejects.log` in the game's data
  folder, so the reasons can be looked at afterwards. Only the latest is kept; a later compile with no rejections
  removes it.
- ELDEN RING's known-stutter note is "moderate" instead of "severe": it says most of the game's big hitches aren't shader
  compiles, citing Digital Foundry and SCSKiller's own measurement.
- SCSKiller checks for updates every hour (it was 6 hours), and About has a Check for updates button. Refreshing the
  Library also downloads a new version, so "Restart to update" appears without waiting.
- Games with a RenoDX HDR mod compile through a copy of ReShade and the mod, so the compile matches what the modded game
  creates. Games whose mod adds to every root signature (most RenoDX mods) are no longer left uncompiled and
  unrecorded, and games whose mod replaces some shaders get those pipelines too. The recorder records under ReShade what
  reaches the driver, which covers the pipelines a mod creates while the game draws; those stay on this PC and are
  never shared. Installing, updating or removing the mod marks the game to compile again, and a compile stopped before
  that starts over instead of resuming. With ReShade installed as d3d12.dll, the game page says to turn on Record
  alongside ReShade for those pipelines. A game is still not compiled or recorded when its mod adds to every root
  signature and SCSKiller can't reproduce how the game loads ReShade: ReShade in the install folder rather than beside
  the game's exe, or under a name the game doesn't load by itself (an .asi, ReShade64.dll). The game page says so. Luma
  mods need their shader files, which the copy leaves out: those games compile without the mod, as before.
- On NVIDIA, a game's new pipelines no longer count a ray tracing pipeline it creates again under another launch's
  export names, or a material it adds again in another order (The Witcher 3 does both every launch): the driver compiles
  nothing new for them. A ray tracing pipeline the game creates with other NVIDIA shader extension settings counts as
  new.

### Fixed

- Stellar Blade (and any game whose folder holds a patcher's copy of its exe): the recorder went next to the copy,
  which never runs, so the game showed Playing without a frame graph. SCSKiller now picks the real exe, and when a game
  runs another exe of its folder than the one SCSKiller found, it follows that exe and moves the recorder next to it
  once the game exits.
- On NVIDIA, the last session's report counts a ray-traced (RayQuery) pipeline the compile took from the game's files
  as one the driver partly recompiles every launch, not as a compile, also when no recording has it.
- Games with BattlEye whose launcher sits at the top of the game folder (such as War Thunder) are found with the
  program BattlEye's launcher starts, also when you add one by picking its launcher, instead of the launcher itself.
- Warframe counts as an anti-cheat game, so it is never recorded: Digital Extremes detects third-party software
  itself, with no driver or folder SCSKiller could see.
- Elden Ring and Elden Ring Nightreign: a compile without a recording now also covers the few pixel shaders the game
  draws behind another bundle's vertex shaders. Against an Elden Ring recording, every pipeline made of shaders in the game's
  files is now planned.
- Elden Ring with ray tracing on NVIDIA: the compile builds each material's ray tracing collection the way the game
  does, with the game's own root signatures, instead of a guess that matched nothing the game creates. All 30
  collections of a recorded session match. The game's ray tracing pipelines, built from whichever materials are loaded,
  still compile the first time.
- An Unreal game whose shader code partly doesn't decompress (Dead Island 2) no longer fails the whole compile with a
  long list of "cannot decompress" errors. SCSKiller skips those shaders and compiles the rest. Only when none of the
  game's shader code decompresses does the compile fail, with one message.
- Unreal Engine 5.8 games (Fortnite, The Sinking City 2) no longer fail to compile with "Arithmetic operation resulted
  in an overflow". When SCSKiller can't read a game's exe (anti-cheat, Xbox app), it now recognizes 5.8 from the game's
  containers instead of taking it for 5.6. 5.8 shader libraries store shorter shader hashes, and SCSKiller reads either
  layout whichever version it detected. A shader library that still can't be read fails with its file name. Unreal
  Engine 5.7 and later compile without a recording, marked "not tested on this engine version yet": 5.7 with the rule a
  5.6 game's recording confirmed, 5.8 and later with a 5.8 rule built from Epic's 5.8 source. It gives mesh and
  amplification shaders their UAVs, and shaders that use an NVIDIA shader extension the extra slot 5.8 adds for it.
  Pipelines whose shaders bind something the rule doesn't cover are left out, as for any untested engine. Fortnite:
  46,468 pipelines planned, none left out.
- Recording did nothing since 1.2.0: the recorder was installed but never armed, so games stayed on "Needs a recording"
  however long they were played. On a PC where SCSKiller had never armed a recorder before, arming failed every time;
  and an install or update that wrote the keys file disarmed the recorder it had just installed. A recorder is now armed
  as soon as it is installed, updated or turned on, every check re-arms one that isn't, and a failure to arm is written
  to recorders.log.
- A game showed "Playing now" all the time, and its cache couldn't be cleared, while another program ran under the name
  of an exe in the game's folder (a tool or launcher helper that ships with the game but runs from elsewhere, like
  Gaijin's agent in War Thunder's folder). Such a process counts as the game only when it runs from the game's folder.
- SCSKiller's memory use while it idles in the notification area: the key sets a scan reads are dropped after 5 minutes
  without use (about 280 MB on a library with 66 games), the memory is handed back to Windows while the window is
  closed to the notification area or minimized, and a compile no longer
  keeps the game's whole shader index in memory afterwards (about 250 MB for a game with 130,000 shaders).

## [1.2.0] - 2026-10-04

### Added

- On an Intel GPU (or another that isn't NVIDIA or AMD), the Library says SCSKiller can't compile there yet and why,
  instead of only marking every game "Not supported on this GPU yet". Close it and it stays closed for that GPU.
- Add a game no store lists, such as one from another launcher or in a folder of its own: "Add a game…" in the Library
  asks for its .exe. A launcher picked by mistake is followed to the game it starts, and an exe of a game already listed
  opens that game. Before adding, SCSKiller shows the program the recorder goes next to and the game folder it
  suggests; change the folder if it isn't the game's own ("Game folder…" on the game page changes it later). SCSKiller
  checks that whole folder for anti-cheat before it records, as for any game, and refuses a drive or a folder that
  holds other games. Added games get their own Library section, compile, record and show up on their game page like
  any other, and Play starts the exe. Their recordings aren't shared with the community database. "Remove from
  library" on the game page takes the recorder out of the game folder and forgets the game; its files stay.
- Record an offline session without EasyAntiCheat, at your own risk, in ELDEN RING and ARMORED CORE VI: games that run
  offline without it when their exe is started directly. Allow it in "Record while I play" on the game's page, then
  "Record offline session" asks you to confirm every time. SCSKiller puts the recorder in, starts the game itself
  offline, and only that process is recorded: the game started any other way, through Steam with EasyAntiCheat too,
  isn't. Steam must be running (offline mode is fine). The moment the game exits, the files come out, the recording and
  the session's frame times are kept, and the game folder is checked to be as it was, also when SCSKiller was closed
  meanwhile; after a crash or power loss, at the next logon or start. The recording is shared like any other while
  sharing is on. Never offered for other anti-cheats or other games.

### Changed

- Games whose ReShade HDR mod adds to every root signature they create (most RenoDX mods) aren't compiled or recorded:
  every pipeline changes, so a compile wouldn't match. The Library and the game page say so, the recorder comes out of
  the game folder, and its recordings aren't shared. Mods that only replace some shaders (some RenoDX mods, Luma) are
  noted on the game page and still compile. Plain ReShade and add-ons that leave the game's shaders alone
  (renodx-dlss5) change nothing. Removing the mod lifts the block at the next scan.
- The recorder records only in games SCSKiller has fully checked for anti-cheat. Any change in the game folder switches
  it off until the next clean check, and a game updated while SCSKiller was closed isn't recorded until SCSKiller has
  checked it again.
- Anti-cheat detection knows more anti-cheats: NCGuard (AION 2), Tencent's Anti-Cheat Expert, HoYoverse's, NetEase's,
  Nexon's BlackCipher, AhnLab HackShield, PunkBuster, EQU8, Denuvo Anti-Cheat and more of XIGNCODE3 and nProtect
  GameGuard. Their games are never recorded, including a game added by hand.
- For a game added by hand, the anti-cheat check also looks at what lies directly in the folders above the game's
  folder, up to the drive or a folder of many games. A game whose confirmed folder is a subfolder, such as War Thunder's
  `win64`, is no longer recorded when BattlEye sits in the folder above. BattlEye's client DLL beside the exe is
  recognised too.
- In the frame-time graph, a ray-tracing state object counts as a shader stutter when it takes 25 ms or more to create,
  not 60 ms.
- On NVIDIA, Unreal Engine 5.0 to 5.4 games compile their ray-tracing shaders without a recording, as 5.1 games already
  did, when their shaders are laid out like 5.1's (Darwin's Paradox). Games laid out differently, such as Unreal 5.5 and
  5.6's bindless ray tracing, still need a recording.

### Fixed

- Elden Ring and Elden Ring Nightreign: a compile without a recording now uses the root signatures the game itself
  creates. Before, every pipeline it compiled used a slightly different form, which the driver treats as another
  pipeline, so the compile didn't save the game any work.
- A compile could pause, or close when SCSKiller exited, an unrelated program: Windows reuses process IDs, and a program
  whose parent had already exited could be taken for part of the compile.
- The game page's button compiles right away, as "Compile queue" does: "Compile" (it said "Add to queue"), "Compile
  carefully" and "Compile without ray tracing (partial)" only added the game to the queue, so nothing compiled until the
  queue was started. The Library's "Add to queue" still only adds.
- Unreal games that choose DirectX 12 from Steam's launch menu, such as Deep Rock Galactic, were detected as DirectX 11,
  so the recorder wasn't offered. Detection reads the game's Steam launch menu and its last log.
- Unreal Engine 4 games that start on DirectX 11 but ship ray tracing, such as Ghostrunner, were detected as DirectX 11
  only. Ray tracing needs DirectX 12, so they now count as DirectX 11 or 12: SCSKiller compiles for both and offers the
  recorder. Adding an encrypted game's AES key also re-runs the detection at the next scan.
- The Witcher 3's compile no longer tries the ray-tracing materials and the one pipeline NVIDIA's driver rejects (92
  failures per compile); the game page counts them as not covered.
- A game with ray tracing that SCSKiller can't compile from its files (Unreal 5 games such as SILENT HILL: Townfall)
  kept asking for a 5-minute recording after one was made. The recording's ray tracing was only looked at on the next
  compile, so the status never changed. A recorded session is checked as soon as the game closes (while the PC is
  idle): hardware Lumen's ray tracing is then covered by the recording, and a session of 5 minutes or more with no ray
  tracing (software Lumen, or ray tracing off) makes the game ready to compile in full, with a note that playing with
  ray tracing on covers it too. Once the recorder has seen 5 minutes of play, the status says what is missing instead
  of asking for 5 minutes again.
- Unreal 5 games whose shaders trace rays inline (hardware Lumen) no longer need a recording for their ray tracing:
  it compiles from the game files. A game whose Windows device profile turns ray tracing pipelines off
  (r.RayTracing.AllowPipeline=0, as SILENT HILL: Townfall does) never uses its ray tracing libraries, and the game page
  says so. Otherwise the page notes that ray tracing using separate pipelines (such as path tracing) is compiled only
  from a recording, and still counts those libraries as not compiled.
- Xbox app games installed in a folder other than `<drive>:\XboxGames`, such as D:\Games\XboxGames, weren't found.

## [1.1.2] - 2026-10-03

### Added

- The Witcher 3: Wild Hunt's ray tracing on NVIDIA: once a recording has a session with ray tracing on, SCSKiller also
  compiles the hit groups of the game's materials, so the materials the game adds while you play stutter far less. The
  game page counts the ray-tracing shaders a compile can't cover (it showed 0 whenever a recording had any ray
  tracing).

### Changed

- A downloaded update also installs when SCSKiller starts, for example after the PC was shut down with the app still
  open: the app restarts into the new version within seconds, unless a compile is running.

### Fixed

- With the internal update channel chosen, SCSKiller kept offering the version already installed.
- About links to the website, the source code and the Patreon page; it still said they were coming.
- A game's last session could show no frame-time graph and count only its last few minutes of shader compiles, when
  two pipelines finished creating at the same moment (seen in The Witcher 3).
- On NVIDIA, games that create their pipelines with NVIDIA's shader extensions (The Witcher 3) now find the pipelines
  SCSKiller plans in the cache; before, only the recorded ones hit. These games ask for one more compile.

## [1.1.1] - 2026-10-03

### Added

- The Witcher 3: Wild Hunt's DirectX 12 build compiles without a recording on NVIDIA: SCSKiller reads its shader caches
  and the pipelines its materials use.

### Changed

- Clicking the Library's refresh button also fetches everything from the server, whatever its age (at most every 10
  minutes): the known-stutter and tested-engine lists, community recordings and shared packs, your supporter status and
  the update check.
- Every game's compile plan is rebuilt once after this update, while the PC is idle; a compiled game whose new plan adds
  pipelines says how many more it can compile. Plans for games with tessellation and geometry shaders, or with ray
  tracing, cover more pipelines.

### Fixed

- A game started through its launcher, such as The Witcher 3's REDprelauncher, is found by the exe the launcher starts,
  so its recorder goes where the game loads it. A recorder already next to the launcher moves there once neither runs,
  with the last session's report and frame times.
- A GPU driver updated while SCSKiller runs is noticed within minutes: the driver shown, the games that need compiling
  again and the driver-update notification follow it without a restart. A GPU of another vendor asks for a restart.
- The app, the command line and the scheduled task no longer compile the same game at once: the second one waits.
- A compile that was stopped and then continued reports the pipelines that failed before the stop, and starts over
  after a driver update.
- A game updated since the last scan is compiled with the engine and checks of the installed build.
- NVIDIA: a compiled game whose shader cache was removed (a shader cache reset) shows as needing a compile again.
- A failed compile's message points to a log that is still there.
- A recorder chained to a mod's d3d12.dll whose install was cut off gets its settings file back, so the mod loads.
- A launch played while a recompile ran no longer judges the compile that ends after it.
- A recording imported just before SCSKiller was closed or crashed is always taken into the next compile.
- Turning "Share anonymous shader hashes" off stops a sharing pass already under way: nothing more is uploaded.
- Signing out and in again while the app renews its sign-in in the background no longer signs the new sign-in out.
- A damaged record in a game's scskiller.db fails only that game's import, not the whole library scan.
- A known-stutter or tested-engines list from the server with an empty entry is ignored instead of stopping the other
  list from updating.
- While the community database refuses or can't be reached, the app waits before asking again instead of asking once
  per game.
- Clear cache also finds a game's D3D shader cache when its path has non-ASCII characters (an accented user name), and
  deletes read-only cache files too.
- Stable updates are found while the SCSKiller server is down, also with an expired sign-in. Beta and alpha also offer a
  newer stable release.
- A game update that changes only its shipped pipeline list or its inline shaders gets a new plan.
- Games are found more reliably: an Epic game whose manifest names itself as the main game, Xbox games on any drive,
  and games installed under a folder named like Setup or Redist.
- A RE Engine game with a patch file that can't be read shows as unsupported instead of compiling outdated shaders.
- Frame times: failed presents no longer count as frames, and a frame file that can't be written no longer shifts the
  times after it. Games that create their device through `ID3D12DeviceFactory` are recorded.
- A compile that fails while it is being watched no longer leaves its process running.

## [1.1.0] - 2026-10-02

### Added

- Shared packs for the FidelityFX and XeSS upscalers: a game that ships an upscaler DLL version someone has recorded on
  the same GPU vendor gets those shaders compiled, free and without an account or a recording of its own. Packs are
  uploaded only with "Share anonymous shader hashes" on.

### Changed

- The installer is `SCSKiller-Setup.exe`, without the version, so its download link always gets the latest release.
- Every game's compile plan is rebuilt once after this update, while the PC is idle, and games compiled before ask for
  one more compile; it mostly finds the shaders already in the driver cache.
- Unreal games: plans also use the pipeline list the game ships, so shader pairs its files alone can't match are
  compiled too. Unreal Engine 5.4 is on the list of tested engine versions.

### Fixed

- The recorder no longer crashes a game when an overlay hooks its presents after the recorder and the game makes another
  swap chain, when the game unloads d3d12.dll after creating a device, when NVAPI is called while the recorder hooks it,
  or when `next=` in scskiller.ini names the recorder itself.
- A damaged scskiller.db no longer makes the game allocate gigabytes when it starts; a full disk, or a second process
  recording in the same folder, no longer leaves a recording that can't be read past that point.
- The recorder no longer goes through the C runtime when it ends a launch's timings at exit, which could stop a game's
  exit if another thread had died holding its lock.
- Stopping a compile after a GPU driver fault or hang no longer makes the next compile skip the pipelines that were
  still unfinished.
- A compile whose process hangs at exit is always ended after the exit limit, also when its last pipelines finished
  right after it started.
- DirectX 11 hull shaders that take all 32 input registers compile instead of counting as failed.
- "Last time you played" no longer counts the shaders a game compiles while it starts up as compiles during play.
- Turning the recorder off removes all its files from the game folder, the last session's frame times included (an
  `scskiller.ini` you edited stays), and files an earlier version left there go at the next scan.
- Frame times: a slow load from the game's own pipeline library is no longer a shader stutter, a shader freeze of 5 s
  or more counts in the 1% low, and a launch never takes another launch's frames.
- Quit waits for a compile you just removed from the queue to save its cache, like any other running compile.
- Choosing another update channel no longer installs an update downloaded from the one before.
- The driver-update notification's buttons work after SCSKiller was closed.
- "Compile queue" also starts games queued to compile when the PC is idle.
- On AMD, the library's cache bar compares the DirectX 12 cache with its limit, not both caches.
- `scskiller compile` refuses an option it doesn't know or one missing its value, and Ctrl+C before a compile
  starts is no success.
- Anti-cheat files and folders are found anywhere in a game's install, hidden or system ones too, and an install with a
  folder SCSKiller can't list counts as anti-cheat, so the recorder is never offered for those games. An update that
  adds anti-cheat while the recorder is being installed takes it out again.
- Clear cache deletes only the Windows shader cache's own files in a game's D3DSCache folder, and on NVIDIA refuses
  while another installed game has the same exe name (the driver gives both one cache).
- A recording imported by the app while the command line compacts it, or the other way round, keeps every record.
- SCSKiller never writes into a game's folder while the game runs, also when a launcher started it under another exe
  name, and a game whose launcher id isn't a valid folder name gets a data folder of its own.
- A malformed root signature in a recording or a community download no longer breaks a game's whole plan: only the
  pipelines that use it are left out.
- An update download stops at the size the signed feed gives, and gives up when no data arrives for two minutes (the
  next check tries again).

## [1.0.0] - 2026-10-01

The first public release.

### Added

- Compile a game's shaders into the NVIDIA or AMD driver cache before you play, so the game doesn't stutter while it
  compiles them. Works from the app or the `scskiller` command line.
- An installer (`SCSKiller-<version>-Setup.exe`) and a portable version (`SCSKiller-<version>-Portable.zip`); both
  update themselves.
- DirectX 12 games on NVIDIA and AMD, and DirectX 11 games on NVIDIA.
- Finds installed games from Steam, Epic Games, EA app, GOG, Ubisoft Connect, Xbox (PC) and Battle.net.
- Reads shaders straight from the game files for Unreal Engine, Unity, FromSoftware and RE Engine games, and scans
  other games' files for shader containers.
- An optional recorder for games whose files aren't enough: it captures the pipelines a game builds while you play, so
  the next compile covers them. It can record alongside a `d3d12.dll` mod such as ReShade when you turn that on for the
  game. It is never offered for games with anti-cheat.
- The recorder also times each frame. A game's page shows the last session's frame-time graph, with the stutters from
  shader compiles told apart from other hitches, the 1% low and a list of slow frames. Frame times stay on your PC.
- A recording keeps only the hash of a shader the game ships, and reads the shader back from the game files. Recording
  limit per game: 32 MB, 128 MB, 256 MB (the default), 512 MB, 1 GB or Unlimited, in Settings. The app shows the space
  each game's recording uses and has a Clear recording button. A game that recorded new pipelines since its last
  compile says so.
- Uninstalling SCSKiller removes the recorder and its files from every game folder, and puts back a mod it was
  recording alongside.
- On AMD, the driver's shader cache is fixed at 16 GB. The queue warns when the games in it won't fit, and a game
  whose cache the driver trimmed to make room shows as needing a rebuild.
- On AMD, games whose first launch still compiled many pipelines show "Partly warmed" and offer a careful compile,
  which is slower but reaches more of them.
- On NVIDIA, compiles run about three times faster, and ray-traced pipelines that the driver partly recompiles at
  every launch are listed on their own, not counted as stutters.
- Recompiles after a GPU driver update, on a schedule or by hand.
- Notices a game update, also one installed while SCSKiller runs: the game shows as needing a rebuild, and the next
  compile covers the installed version.
- A Play button in the Library and on a game's page for Steam, Epic Games, Xbox, GOG (with GOG Galaxy) and Ubisoft
  Connect games. It starts the game through its store, never on anti-cheat games, and waits while the game compiles.
- A game's page shows how long the last session lasted, also for games that close without a clean exit, as Unreal
  games do.
- A community database of shader hashes for Patreon supporters: games compile from other players' recordings without
  recording them yourself. Without a membership, a game that needs a recording still says when the database has one
  for its version, and how many pipelines it holds.
- "Share my shader hashes" (opt-in): uploads the hash-only form of your recordings (no shader code) under an anonymous
  device, never linked to your account.
- A notification when compiled games have new pipelines to compile (recorded while playing, or from the shared shader
  hashes), with Compile now and Show. It comes once per change and never while the game runs; Settings can turn it off.
- An anonymous daily check that counts active installs. Once a day the app tells the server that an install is active,
  with the app's version and the GPU vendor (NVIDIA, AMD or other) and no identifier: nothing about the PC or its
  games. "Send an anonymous daily check" in Settings turns it off.
- Status texts in plain words, such as "no recording needed" or "turn on recording and play for about 5 minutes". The
  list of engine versions SCSKiller has tested updates from the server, like the list of games known to stutter.
- A minimum window size at which every page still fits.
- Clear cache: removes a game's driver cache, its Windows shader cache and Unreal's own pipeline cache, so the next run
  starts cold.
