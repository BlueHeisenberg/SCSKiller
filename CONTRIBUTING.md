# Contributing

Bugs and game requests go in Issues, with the templates. Security problems go through [SECURITY.md](SECURITY.md), never
a public issue.

## Anti-cheat games

SCSKiller never touches a game with anti-cheat beyond reading its files: no recorder, no injection, no opening its
processes. The one exception is the offline session (ARCHITECTURE.md, Recorder), for the EasyAntiCheat games listed in
`src/SCSKiller.Core/Games/offline-eac.json` only: opted into per game, confirmed per launch, started by SCSKiller and
recorded in that process alone. Changes that widen it or work around the rule aren't accepted.

## How pull requests are merged

Pull requests are reviewed here in public. This repository is updated once per release, so an accepted PR is applied to
the development tree with your authorship kept and ships in the next release. The release commit credits you with a
`Co-authored-by` line, the changelog thanks you by handle, and the PR is closed with a link to that commit.

## Sign-off (DCO)

Every commit needs a `Signed-off-by` line (`git commit -s`), certifying the
[Developer Certificate of Origin](https://developercertificate.org/): you wrote the change or have the right to submit
it under this project's licence. Use the commit author's name and email (a GitHub noreply address is fine). Commits
without it can't be merged. Contributions are licensed under GPL-3.0-or-later with the additional permission in
[LICENSE-EXCEPTION.txt](LICENSE-EXCEPTION.txt).

## Build

Visual Studio 2022 with the C++ desktop workload (MSVC x64, CMake), and the .NET 10 SDK:

```
git submodule update --init
cmake -S proxy -B proxy/build -A x64
cmake --build proxy/build --config Release
dotnet build SCSKiller.slnx -c Release
```

The submodule is ValveResourceFormat, which reads Source 2 shaders. The CMake configure downloads AMD's `amd_ags_x64.dll`, pinned by SHA-256. Without it, AMD warms of AGS games use a plain
device.

## Tests

```
dotnet test tests/SCSKiller.Tests -c Release --filter "Needs!=Gpu&Needs!=Game"
```

CI runs this on a clean Windows machine with no games and no GPU, and it must pass. Tests that need more carry a trait:
`[Trait("Needs", "Gpu")]` drives a real GPU and its driver cache, `[Trait("Needs", "Game")]` reads installed games,
launchers or recordings. Such a test returns early when what it needs isn't there. Run them locally
(`dotnet test tests/SCSKiller.Tests -c Release`) when your change touches their area.

- No machine paths in tests. `TestEnv` finds games through Steam's library list, the `XboxGames` folders, GOG and
  `SCSKILLER_TEST_GAMES_ROOT` (`;`-separated folders of game install folders). GPU tests take a lock in
  `SCSKILLER_DEV_DIR` (default `%TEMP%\scskiller-test`).
- Tests never write into a game folder, change the driver's cache size or settings, or register scheduled tasks.

## Style

- Match the code around your change. One topic per PR, kept small.
- Comment only what the code can't say: a non-obvious reason, an invariant, a driver or engine workaround, a file-format
  detail, a measured fact. No commented-out code.
- Add a line under `## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md) for anything a user would notice.
- Don't commit game files, shader dumps, recordings (`*.db`) or third-party binaries.
