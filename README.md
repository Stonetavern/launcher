# Stonetavern Launcher

Desktop launcher for the Stonetavern World of Warcraft realms. It downloads and
verifies the game client, keeps it patched, and starts the game with the right
configuration on Windows, Linux and macOS.

Built with Avalonia on .NET 10.

## What it does

- Downloads the game client (resumable, verified against a published SHA256)
- Repairs a damaged install per file instead of re-downloading the whole archive
- Applies the realm configuration and launches the game
- Updates itself when a newer build is published
- Shows realm status, patch notes and the friends list

## Building

Requires the .NET 10 SDK.

```
dotnet restore WowLauncher/WowLauncher.csproj
dotnet build   WowLauncher/WowLauncher.csproj -c Release
```

Self-contained single-file builds, per platform:

```
dotnet publish WowLauncher/WowLauncher.csproj -c Release -r win-x64   --self-contained -p:PublishSingleFile=true
dotnet publish WowLauncher/WowLauncher.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
dotnet publish WowLauncher/WowLauncher.csproj -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true
```

## Testing

```
dotnet test
```

## Distribution

The launcher reads a manifest that lists the current client builds and launcher
versions. The wire format is documented in `deploy/MANIFEST-SCHEMA.md`.

Game clients are several gigabytes and are served from a dedicated download
host. They are never stored in this repository and never attached to releases.
Only the launcher binaries, checksums and the signed manifest are published here.

Releases are signed. The public key is in
`deploy/assets/stonetavern-release-signing.pub.asc` so anyone can verify a
download independently.

## License

See `LICENSE`.
