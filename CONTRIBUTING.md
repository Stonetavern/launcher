# Contributing

## Branches

```
main            protected, no direct pushes
feature/<name>  new work
fix/<name>      bug fixes
v<x.y.z>        release tags
```

Open a pull request against `main`. All status checks have to pass before merge.

## Commits

Conventional Commits: `feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`.
Add a scope where it helps, for example `fix(download): resume after 416`.

## Before you open a pull request

```
dotnet build WowLauncher/WowLauncher.csproj -c Release
dotnet test WowLauncher.Tests/WowLauncher.Tests.csproj
```

## Testing rules

After fixing a bug, disable the fix temporarily and confirm the test goes red.
A test that would pass without its fix proves nothing.

Do not assert on a download by checking that it completed. Assert on the
checksum.

## Continuous integration

Build and test jobs run on GitHub hosted standard runners. They are free and
unlimited for public repositories. The better reason is security: this
repository is public, so anyone can open a pull request. On a self hosted
runner that code would execute on hardware owned by the operator, inside the
network of the operator. On a hosted runner it executes on a throwaway VM.

Deployment jobs must never run on a hosted runner. They need access to the
internal network, and opening that path would defeat the isolation it exists to
protect. Deployment happens from self hosted runners inside the target network.
The secret scan workflow enforces exactly that distinction.

Workflows start with `permissions: contents: read`. Add rights only to the job
that needs them. Third party actions are pinned to a full commit SHA, never to
a tag.

## What not to commit

- Game clients, patches, or any archive over 10 MB
- Manifests describing live infrastructure
- Internal hostnames, addresses, or access paths
- Anything that identifies a player
