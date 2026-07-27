# AGENTS: Stonetavern Launcher

**What this is:** the desktop launcher for the Stonetavern World of Warcraft
realms. It downloads and verifies the game client, patches it, and starts the
game on Windows, Linux and macOS. C# and Avalonia on .NET 10.

**Who it serves:** real players on a live realm. A bad launcher release does not
fail a build, it takes away the ability of people to play.

---

## Invariants

- **A download is not trusted because it finished.** It is trusted because its
  SHA256 matches a value that was published separately. Do not skip
  verification to save time, and do not verify only after extraction.
- **An absent optional manifest field means "not published yet", never an
  error.** The operator publishes partial manifests routinely. A launcher that
  throws on a missing `launcher_linux` or `filesUrl` breaks every player at once.
- **Never put a game client in this repository or in a GitHub release.** Clients
  are 5 to 8 GB each. They live on the download host.
- **No internal infrastructure in this repository.** No addresses, no hostnames
  with a role attached, no access paths. This repository is public.
- **Nothing that identifies a player.** No names, no addresses, no session
  material, not in a test fixture and not in a screenshot.
- **Build on hosted runners, deploy on self hosted.** This repository is public,
  so anyone can open a pull request. Building on hosted runners keeps foreign
  code off hardware owned by the operator. Deployment jobs need the internal
  network and must never be hosted.

## Layout

```
WowLauncher/          the application
  Models/             wire formats, start here to understand the data
  Services/           download, verify, update, launch
  ViewModels/ Views/  MVVM, Avalonia AXAML
WowLauncher.Tests/    xUnit
deploy/               packaging and publishing
  MANIFEST-SCHEMA.md  the wire format both sides agree on
```

| I want to | Read |
|---|---|
| understand the design | `ARCHITECTURE.md` |
| understand what gets downloaded | `deploy/MANIFEST-SCHEMA.md` |
| change how a release is built | `deploy/*.sh` and `.github/workflows/` |
| report a vulnerability | `SECURITY.md` |

## Build

```
dotnet build WowLauncher/WowLauncher.csproj -c Release
```

## Test

```
dotnet test WowLauncher.Tests/WowLauncher.Tests.csproj
```

There is no solution file at the root, so the project has to be named
explicitly.

After fixing a bug, disable the fix temporarily and confirm the test goes red.
A test that would pass without its fix is a placebo.

## Release

Tag `v<x.y.z>`. CI builds all three platforms, generates checksums in the build
job, signs the manifest and publishes a GitHub release.

Upload budget matters. The publishing host has roughly 1 MB per second
upstream. The release workflow refuses anything over 200 MB. Launcher binaries
are around 50 MB and fine. Anything larger belongs on the download host,
transferred with a delta sync, never through the release API.

## Do not change without being asked

- `.github/workflows/`, because deployment rights hang off these
- `deploy/assets/stonetavern-release-signing.*`, the public signing key and its
  fingerprint. That is how players verify a build is genuine

## Traps that have already cost time

- **Two manifest shapes.** `ServerManifest` carries one SHA256 for the whole
  client archive. `ClientFileManifest` lists every file inside it. Reaching for
  the wrong one gives you code that looks right and repairs nothing.
- **Path separators.** Client file manifests always use `/`, no matter which
  operating system generated them. Comparing against a Windows path with
  backslashes matches nothing and reports a healthy install.
- **`launcher_linux` is a separate top level field**, deliberately not nested
  under `launcher`. Nesting it would break Windows deserialisation.
- **Windows and macOS cannot self update today.** The live manifest has no
  `launcher` field and the download host returns 404 for it. There is no
  `launcher_macos` field in the model at all. Measured 2026-07-26. Do not assume
  the update path works on those platforms just because the code exists.
- **The test suite had never run on Windows.** The first CI run there failed 22
  tests, clustered in the proxy and process lifecycle classes. See the open
  issues before you trust a green build on one platform to mean anything about
  another.
