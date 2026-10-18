# Code signing policy

## Status

SCSKiller has applied for free code signing from the [SignPath Foundation](https://signpath.org). Until it's granted,
releases are **unsigned** and Windows shows "Unknown publisher". Each release lists the SHA-256 of its downloads.

## What is signed

Only binaries built from this repository's source by its GitHub Actions release workflow, from a stable release tag
(`vX.Y.Z`):

- `SCSKiller.exe` (the app)
- `cli\scskiller.exe` and `cli\scskillerw.exe` (the command line)
- `native\scskiller_warm.exe` and `native\segheap\scskiller_warm.exe` (the compiler that runs outside the game)
- `native\d3d12.dll` (the optional recorder)
- `SCSKiller-Setup.exe` and `Update.exe` (Velopack)

Upstream binaries and runtimes in the package (the .NET runtime, the Windows App SDK, Velopack's own components, AMD's
`native\amd_ags_x64.dll`) aren't signed by this project. Alpha and beta builds aren't built here and aren't signed under
this policy. Every signed file carries the product name `SCSKiller` and the release's version.

## Roles

| Role | Who |
|---|---|
| Author (commits to the source repository) | [the maintainer](https://github.com/BlueHeisenberg) |
| Reviewer (reviews every external contribution before it's merged) | the maintainer |
| Approver (approves each signing request) | the maintainer |

Contributions are reviewed in public pull requests before they're merged ([CONTRIBUTING.md](CONTRIBUTING.md)). Every
signing request is approved by hand. All members use multi-factor authentication on GitHub and SignPath.

## Privacy

SCSKiller sends nothing about you, your PC or your games unless you ask it to. It connects to:

- the SCSKiller service (`api.scskiller.com`, fallback `api.scskiller.io`):
  - for the welcome text, the list of games known to stutter and the community database's index (nothing about you is
    sent)
  - to sign in with Patreon
  - for signed-in supporters, to download the community recordings of your games' builds, and on the alpha or beta
    channel to check for those updates
  - only with "Share my shader hashes" on, to upload your recordings' hash-only form (no shader code) under an
    anonymous device not linked to your account
  - only when you press Send in a game's "Report a problem", to send the report it shows you (kept 90 days)
- `dl.scskiller.io`, for alpha and beta updates
- GitHub Releases, for stable updates
- public GitHub-hosted sources, for a decompression library (Oodle) or an archive key a game needs, the first time it's
  needed.

Shaders and recordings stay on your PC unless you turn sharing on. Frame times always stay on your PC.

## Reporting

Report a signed binary you believe is malicious or misused through [SECURITY.md](SECURITY.md).
