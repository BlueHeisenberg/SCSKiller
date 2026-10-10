<p align="center">
  <img width="128" src=".github/assets/logo.png" alt="SCSKiller logo">
</p>
<h1 align="center">SCSKiller</h1>
<p align="center">
  <strong>Shader Compilation Stutter Killer.</strong> SCSKiller compiles your games' shaders into your GPU driver's
  cache before you play, so the game doesn't stop to compile them mid-game. Free and open source, for Windows.
</p>

<p align="center">
  <a href="https://github.com/BlueHeisenberg/SCSKiller/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/BlueHeisenberg/SCSKiller"></a>
  <a href="https://github.com/BlueHeisenberg/SCSKiller/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/BlueHeisenberg/SCSKiller/total"></a>
  <a href="LICENSE"><img alt="Licence: GPL-3.0-or-later" src="https://img.shields.io/badge/licence-GPL--3.0--or--later-blue"></a>
  <a href="https://www.patreon.com/SCSKiller"><img alt="Patreon" src="https://img.shields.io/badge/Patreon-support-f96854?logo=patreon&logoColor=white"></a>
  <a href="https://discord.gg/st7C4yCTcN"><img alt="Discord" src="https://img.shields.io/badge/Discord-join-5865f2?logo=discord&logoColor=white"></a>
</p>

<p align="center">
  <a href="./README.md">English</a>
  ·
  <a href="./docs/README_zh-CN.md">简体中文</a>
</p>

<p align="center">
  <a href="https://scskiller.com">Website</a>
  ·
  <a href="https://github.com/BlueHeisenberg/SCSKiller/releases/latest">Download</a>
  ·
  <a href="https://www.patreon.com/SCSKiller">Patreon</a>
  ·
  <a href="https://discord.gg/st7C4yCTcN">Discord</a>
  ·
  <a href="https://x.com/SCSKiller">X</a>
  ·
  <a href="https://github.com/BlueHeisenberg/SCSKiller/issues/new?template=bug.yml">Report a bug</a>
  ·
  <a href="https://github.com/BlueHeisenberg/SCSKiller/issues/new?template=game-request.yml">Request a game</a>
</p>

> [!WARNING]
> The only official downloads are this repository's [Releases](https://github.com/BlueHeisenberg/SCSKiller/releases).
> See [official links](#official-links) for the accounts and sites that belong to SCSKiller.

<p align="center">
  <img src=".github/assets/library.webp" alt="SCSKiller's library: games grouped by store, with shader and pipeline counts, cache size, compile time, a status such as Needs rebuilding or Warmed, and Play buttons.">
</p>

## Why

A game that meets a new effect has the GPU driver compile its shader on the spot, and the frame waits: that's shader
compilation stutter. SCSKiller reads the shaders a game ships with, works out the pipelines it will build, and compiles
them into the driver's cache ahead of time, in its own process with the game closed.

## Results

About 5 minutes of play per game, from a cold driver cache and after compiling with SCSKiller. A stutter is a shader
compile the game waited 20 ms or more for.

<table>
  <tr><th>Game</th><th>GPU</th><th>Measurement</th><th>Cold</th><th>With SCSKiller</th></tr>
  <tr><td rowspan="4">Final Fantasy VII Rebirth</td><td rowspan="2">NVIDIA<br>RTX 5090</td><td>Stutters (≥ 20 ms)</td><td>19</td><td><b>0</b></td></tr>
  <tr><td>Worst stall</td><td>76 ms</td><td><b>5 ms</b></td></tr>
  <tr><td rowspan="2">AMD<br>Ryzen AI Max+ 395 (Strix Halo)</td><td>Stutters (≥ 20 ms)</td><td>162</td><td><b>0</b></td></tr>
  <tr><td>Worst stall</td><td>291 ms</td><td><b>7 ms</b></td></tr>
  <tr><td rowspan="2">Silent Hill: Townfall</td><td rowspan="2">NVIDIA<br>RTX 5090</td><td>Stutters (≥ 20 ms)</td><td>24</td><td><b>0</b></td></tr>
  <tr><td>Worst stall</td><td>347 ms</td><td><b>8 ms</b></td></tr>
  <tr><td rowspan="4">Hogwarts Legacy</td><td rowspan="2">NVIDIA<br>RTX 5090</td><td>Stutters (≥ 20 ms)</td><td>1,014</td><td><b>87</b></td></tr>
  <tr><td>Worst stall</td><td>2,400 ms</td><td><b>100 ms</b></td></tr>
  <tr><td rowspan="2">AMD<br>Ryzen AI Max+ 395 (Strix Halo)</td><td>Stutters (≥ 20 ms)</td><td>27,743</td><td><b>206</b></td></tr>
  <tr><td>Worst stall</td><td>2,146 ms</td><td><b>104 ms</b></td></tr>
  <tr><td rowspan="2">Tiny Tina's Wonderlands</td><td rowspan="2">NVIDIA<br>RTX 5090</td><td>Stutters (≥ 20 ms)</td><td>583</td><td><b>0</b></td></tr>
  <tr><td>Worst stall</td><td>315 ms</td><td><b>13 ms</b></td></tr>
  <tr><td>Star Wars Jedi: Survivor</td><td>NVIDIA<br>RTX 5090</td><td>Stutters (≥ 20 ms)</td><td>9,038</td><td><b>11</b></td></tr>
</table>

Measured with SCSKiller's recorder. Your numbers depend on the game, GPU and driver. SCSKiller fixes shader compilation
stutter only, not traversal or streaming stutter, and a shader it couldn't find or record can still compile in game.

## Features

- **NVIDIA and AMD**, DirectX 12 games, and DirectX 11 games on NVIDIA.
- **Finds your games** in Steam, Epic Games, EA app, GOG, Ubisoft Connect, Xbox (PC), Battle.net, PURPLE, HoYoPlay and
  Gaijin, plus games you add yourself.
- **Puts games known to stutter on top**, with the reason, and marks games whose engine prepares its shaders itself.
- **Compiles again after a driver update**, which clears the shader cache, on its own if you let it.
- **Optional recorder** for games whose pipelines its files don't give: play a few minutes with it on, then compile. It
  also records alongside a mod that replaces shaders.
- **Frame times per session**: with the recorder on, a game's page graphs your last session and marks shader stutters
  apart from other hitches. They stay on your PC.
- **Play button** that starts the game through its store.
- **Community shader hash database** for Patreon supporters: other players' recordings, so your games are covered
  without recording them first. Sharing yours is opt-in, anonymous and hash-only.
- **Leaves anti-cheat games alone** beyond reading their files: no launching, no injecting, no recorder. The one
  exception is opt-in per game and confirmed every time: an offline session without EasyAntiCheat for ELDEN RING and
  ARMORED CORE VI, at your own risk.

## Install

| Requirement | |
|---|---|
| OS | Windows 10 (version 2004) or 11, 64-bit |
| GPU | NVIDIA or AMD |
| Games | DirectX 12, DirectX 11 on NVIDIA |

Intel GPUs aren't supported yet: I don't have one to test on. If you'd like to sponsor an Intel Arc GPU, get in touch at
[contact@scskiller.com](mailto:contact@scskiller.com).

Download `SCSKiller-Setup.exe` from [Releases](https://github.com/BlueHeisenberg/SCSKiller/releases/latest) and run it.
It installs for your user only, needs no admin rights and updates itself.

Or unzip `SCSKiller-<version>-Portable.zip` to any folder you can write to and run `SCSKiller.exe`. It updates itself
too, and keeps its settings and data in a `data` folder beside the exe. Turn recording off for your games before you
delete the folder, so the recorder leaves the game folders.

<!-- UNSIGNED NOTICE: delete this block once releases are signed. -->
> [!IMPORTANT]
> **Releases aren't code-signed yet**, so the first run will probably show SmartScreen's "Windows protected your PC".
> Click **More info**, then **Run anyway**. Updates never show it. Signing through the SignPath Foundation is in
> progress. To check a download, compare its SHA-256 with the one in the release notes (PowerShell:
> `Get-FileHash <file>`). Smart App Control (Windows 11) blocks unsigned apps outright: if it's on, wait for a signed
> release.
<!-- END UNSIGNED NOTICE -->

## How to use it

1. **Open SCSKiller.** It finds your games and the shaders they ship with.
2. **Queue games and press Compile.** "Add all recommended" is a good start. Keep the game closed while it compiles.
3. **Play**, from your store or with the Play button. SCSKiller can be closed. Left open, it checks that the compile
   reached the cache the game really uses, and with the recorder on, the game's page shows your session's frame times
   and anything that still compiled.

If a game needs a recording, turn on **Record**, play it for a few minutes, then compile it.

<p align="center">
  <img src=".github/assets/queue.webp" width="49%" alt="The compile queue: one game compiling with pipelines per second, games waiting, one finished.">
  <img src=".github/assets/detail.webp" width="49%" alt="A game's page: the frame times of the last session, with shader compile stutters marked apart from other hitches and loading, and the list of slow frames.">
</p>

## FAQ

**Can I get banned?** SCSKiller only reads the files of games with anti-cheat and never adds its recorder to them. Its
anti-cheat check can still miss a game, so you use SCSKiller at your own risk, and its authors are not responsible for
bans, crashes or lost data.

**Is it safe with anti-cheat games?** SCSKiller never launches them, injects into them or gives them the recorder. It
only reads their files. The exception is the offline session for ELDEN RING and ARMORED CORE VI, which run offline
without EasyAntiCheat when their exe is started directly. You allow it per game and confirm every launch, at your own
risk. Steam must be running (offline mode is fine). SCSKiller adds `d3d12.dll`, `scskiller.ini`, `scskiller.armed` and
`steam_appid.txt` to the game's folder and starts the exe itself, and the recorder records only that process. When the
game exits, SCSKiller takes those files out again, keeps the recording and the session's frame times, and checks the
folder is as it was, at the next logon or start if SCSKiller was closed or the PC crashed. If those files are still there
when you start the game online, you could be banned.

**What does it write, and where?** Settings and per-game plans go in `%LOCALAPPDATA%\SCSKiller\` (the `data` folder of
a portable copy), and the compiled pipelines go in your driver's own shader cache. Game files are never modified. The
optional recorder adds `d3d12.dll`, `scskiller.ini` and `scskiller.armed` to the folder of a game you turn it on for, and
turning it off removes exactly those files. `scskiller.armed` says the install was checked for anti-cheat, and goes as
soon as anything in the install changes. A game records only if that file is there when it starts. Uninstalling
SCSKiller removes these files and the recordings from every game folder.

**Does it touch my drivers or their settings?** No. It compiles through DirectX, as the game would.

**Do I need an account?** No. Everything the app does on your PC is free. Signing in with Patreon adds the supporter
features.

**My game isn't detected, or something broke.** Open an [issue](https://github.com/BlueHeisenberg/SCSKiller/issues/new/choose)
with the bug or game request template.

## Support the project

The app is free and stays free. [Patreon](https://www.patreon.com/SCSKiller) pays for the servers behind the community
database. Sign in with Patreon in Settings to unlock your tier.

| Tier | You get |
|---|---|
| **Patreon supporter** | The community shader hash database, and beta builds |
| **Patreon backer** | All of that, plus alpha builds and priority on game requests |

## Build from source

Visual Studio 2022 (C++ desktop workload with CMake) and the .NET 10 SDK:

```
cmake -S proxy -B proxy/build -A x64
cmake --build proxy/build --config Release
dotnet build SCSKiller.slnx -c Release
```

`build/publish.ps1` builds the distribution into `dist\`. Design: [ARCHITECTURE.md](ARCHITECTURE.md). Contributing:
[CONTRIBUTING.md](CONTRIBUTING.md). Changes: [CHANGELOG.md](CHANGELOG.md). Security issues go through
[SECURITY.md](SECURITY.md), never a public issue.

## Official links

- GitHub: https://github.com/BlueHeisenberg/SCSKiller
- Website: https://scskiller.com
- Patreon: https://www.patreon.com/SCSKiller
- Discord: https://discord.gg/st7C4yCTcN
- X: https://x.com/SCSKiller

Anything else claiming to be SCSKiller isn't. Please [report it](https://github.com/BlueHeisenberg/SCSKiller/issues/new/choose).

## Licence

GPL-3.0-or-later ([LICENSE](LICENSE)), with an additional permission for Oodle, the Windows App SDK and graphics driver
libraries ([LICENSE-EXCEPTION.txt](LICENSE-EXCEPTION.txt)). Third-party components:
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Code signing: [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md).

Not affiliated with any GPU maker, engine maker or game publisher. All product names are trademarks of their owners.

### Forks

Forks are welcome under the licence. Please give yours its own name, say it's unofficial and based on SCSKiller, and
keep the SCSKiller copyright notices. The measurements and screenshots in this README are SCSKiller's own, so please
don't present them as a fork's.

Official builds come only from this repository's [Releases](https://github.com/BlueHeisenberg/SCSKiller/releases) page.
A fork's builds, bugs and anti-cheat behaviour are its author's: SCSKiller keeps its recorder out of anti-cheat games,
and can't vouch for a fork that changes that.

Improvements are always welcome back here as pull requests. Fixes, games and ideas from forks make SCSKiller better
for everyone.
