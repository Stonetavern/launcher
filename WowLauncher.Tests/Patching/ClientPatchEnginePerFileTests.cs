using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Patching;
using Xunit;

namespace WowLauncher.Tests.Patching;

/// <summary>PER_FILE (ARCHITEKTUR-v2-patcher.md §4/§3): hash-scan against files.json, fetch only what
/// differs, never touch a protected path, report (never delete) a foreign file.</summary>
public sealed class ClientPatchEnginePerFileTests
{
    private static ClientPatchEngine NewEngine(
        FakeManifestLoader loader, FakeDownloadService download, string? currentOs = null) =>
        new(loader, download, new ClientVerifyService(new Serilog.LoggerConfiguration().CreateLogger()),
            new FakeButlerSidecar(), new FakeGameProcessDetector(),
            new Serilog.LoggerConfiguration().CreateLogger(), delay: (_, _) => Task.CompletedTask,
            currentOs: currentOs is null ? null : () => currentOs);

    private static ClientFileEntry WithOs(ClientFileEntry entry, params string[] os)
    {
        entry.Os = [.. os];
        return entry;
    }

    private static ManifestFile NewClient() => new()
    {
        Build = 5875, Os = "windows", Version = "1.4",
        FilesUrl = "http://fake.invalid/files.json", FilesSha256 = "irrelevant",
        FilesBase = "http://fake.invalid/tree/",
        Protected = ["WTF/"],
    };

    [Fact]
    public async Task PerFile_downloads_only_the_one_broken_file_out_of_five()
    {
        var root = PatchingFakes.NewTempDir();
        var files = new (string Path, byte[] Content)[]
        {
            ("WoW.exe", "exe"u8.ToArray()),
            ("Data/a.MPQ", "aaa"u8.ToArray()),
            ("Data/b.MPQ", "bbb"u8.ToArray()),
            ("Data/c.MPQ", "ccc"u8.ToArray()),
            ("Data/d.MPQ", "ddd"u8.ToArray()),
        };
        var manifest = new ClientFileManifest
        {
            Build = 5875, Version = "1.4",
            Files = files.Select(f => PatchingFakes.Entry(f.Path, f.Content)).ToList(),
        };

        // Write all five to disk correctly, then damage exactly one.
        foreach (var f in files) PatchingFakes.WriteFile(root, f.Path, f.Content);
        PatchingFakes.WriteFile(root, "Data/c.MPQ", "CORRUPT"u8.ToArray());

        var client = NewClient();
        var download = new FakeDownloadService();
        // The server has the CORRECT bytes for the broken file, addressable via files_base + path.
        download.Content[client.FilesBase + "Data/c.MPQ"] =
            files.First(f => f.Path == "Data/c.MPQ").Content;

        var outcome = await NewEngine(new FakeManifestLoader(manifest), download)
            .RunAsync(client, root, forcePerFile: true);

        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.Equal(PatchRoute.PerFile, outcome.Route);
        Assert.Single(download.Requested);
        Assert.Equal(client.FilesBase + "Data/c.MPQ", download.Requested[0]);
        Assert.Equal("ccc", File.ReadAllText(Path.Combine(root, "Data", "c.MPQ")));
    }

    [Fact]
    public async Task Protected_path_is_never_flagged_or_fetched_even_when_it_differs()
    {
        var root = PatchingFakes.NewTempDir();
        var wowExe = "exe"u8.ToArray();
        var manifest = new ClientFileManifest
        {
            Build = 5875, Version = "1.4",
            Files = [PatchingFakes.Entry("WoW.exe", wowExe), PatchingFakes.Entry("WTF/Config.wtf", "shipped-default"u8.ToArray())],
            Protected = ["WTF/"],
        };
        PatchingFakes.WriteFile(root, "WoW.exe", wowExe);
        // The player's own config — deliberately does NOT match the manifest' shipped default.
        PatchingFakes.WriteFile(root, "WTF/Config.wtf", "player-edited-config"u8.ToArray());

        var client = NewClient();
        var download = new FakeDownloadService();
        download.Content[client.FilesBase + "WTF/Config.wtf"] = "shipped-default"u8.ToArray();

        var outcome = await NewEngine(new FakeManifestLoader(manifest), download)
            .RunAsync(client, root, forcePerFile: true);

        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.Empty(download.Requested); // never fetched
        Assert.Equal("player-edited-config", File.ReadAllText(Path.Combine(root, "WTF", "Config.wtf"))); // never overwritten
    }

    /// <summary>R3: the manifest's OWN protected list is the stronger lock. The test above passes even
    /// without it (WTF is in DownloadService's preserved list); this one uses a path nothing else
    /// protects, so it fails the moment the engine stops honouring <c>protected</c>.</summary>
    [Fact]
    public async Task Entry_under_a_manifest_protected_path_is_never_fetched()
    {
        var root = PatchingFakes.NewTempDir();
        var wowExe = "exe"u8.ToArray();
        var manifest = new ClientFileManifest
        {
            Build = 5875, Version = "1.4",
            Files = [PatchingFakes.Entry("WoW.exe", wowExe), PatchingFakes.Entry("MyStuff/keepsake.bin", "shipped"u8.ToArray())],
            Protected = ["MyStuff/"],
        };
        PatchingFakes.WriteFile(root, "WoW.exe", wowExe);
        PatchingFakes.WriteFile(root, "MyStuff/keepsake.bin", "player-version"u8.ToArray());

        var client = NewClient();
        var download = new FakeDownloadService();
        download.Content[client.FilesBase + "MyStuff/keepsake.bin"] = "shipped"u8.ToArray();

        var outcome = await NewEngine(new FakeManifestLoader(manifest), download)
            .RunAsync(client, root, forcePerFile: true);

        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.Empty(download.Requested); // filtered out before VERIFY could flag it as corrupt
        Assert.Equal("player-version", File.ReadAllText(Path.Combine(root, "MyStuff", "keepsake.bin")));
    }

    /// <summary>R1: the path string is clean; the filesystem is not. A symlink already on disk must not
    /// turn a PER_FILE download into a write outside the install root.</summary>
    [Fact]
    public async Task PerFile_refuses_to_write_through_a_symlink_that_leaves_the_root()
    {
        if (OperatingSystem.IsWindows()) return; // symlink creation needs privileges there
        var root = PatchingFakes.NewTempDir();
        var outside = Path.Combine(root + "-outside");
        Directory.CreateDirectory(outside);
        try
        {
            var wowExe = "exe"u8.ToArray();
            var manifest = new ClientFileManifest
            {
                Build = 5875, Version = "1.4",
                Files = [PatchingFakes.Entry("WoW.exe", wowExe), PatchingFakes.Entry("Data/evil.bin", "shipped"u8.ToArray())],
            };
            PatchingFakes.WriteFile(root, "WoW.exe", wowExe);
            Directory.CreateSymbolicLink(Path.Combine(root, "Data"), outside);

            var client = NewClient();
            var download = new FakeDownloadService();
            download.Content[client.FilesBase + "Data/evil.bin"] = "shipped"u8.ToArray();

            var outcome = await NewEngine(new FakeManifestLoader(manifest), download)
                .RunAsync(client, root, forcePerFile: true);

            Assert.DoesNotContain(client.FilesBase + "Data/evil.bin", download.Requested);
            Assert.False(File.Exists(Path.Combine(outside, "evil.bin")));
            Assert.Contains(outcome.Findings, f => f.Message.Contains("refusing to write"));
        }
        finally
        {
            try { Directory.Delete(outside, true); } catch { /* temp dir */ }
        }
    }

    [Fact]
    public async Task Foreign_file_is_reported_but_never_deleted()    {
        var root = PatchingFakes.NewTempDir();
        var wowExe = "exe"u8.ToArray();
        var manifest = new ClientFileManifest { Build = 5875, Version = "1.4", Files = [PatchingFakes.Entry("WoW.exe", wowExe)] };
        PatchingFakes.WriteFile(root, "WoW.exe", wowExe);
        PatchingFakes.WriteFile(root, "Interface/AddOns/SomeMod/left-behind.lua", "-- leftover"u8.ToArray());

        var client = NewClient();
        var outcome = await NewEngine(new FakeManifestLoader(manifest), new FakeDownloadService())
            .RunAsync(client, root, forcePerFile: true);

        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.Contains(outcome.Findings,
            f => f.Code == PatchFinding.ForeignFile && f.Message.Contains("left-behind.lua"));
        Assert.True(File.Exists(Path.Combine(root, "Interface", "AddOns", "SomeMod", "left-behind.lua")));
    }

    [Fact]
    public void WriteState_is_atomic_and_ReadState_round_trips()
    {
        var root = PatchingFakes.NewTempDir();
        var state = new ClientPatchState { Build = 5875, Os = "windows", Version = "1.4", FilesSha256 = "abc123" };

        ClientPatchEngine.WriteState(root, state);

        // No leftover .tmp file after a normal write.
        Assert.Empty(Directory.GetFiles(Path.Combine(root, ".stonetavern"), "*.tmp"));
        var read = ClientPatchEngine.ReadState(root);
        Assert.NotNull(read);
        Assert.Equal(state.Build, read!.Build);
        Assert.Equal(state.Version, read.Version);
        Assert.Equal(state.FilesSha256, read.FilesSha256);
    }

    [Fact]
    public void ReadState_on_a_corrupt_file_returns_null_instead_of_throwing()
    {
        var root = PatchingFakes.NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, ".stonetavern"));
        File.WriteAllText(ClientPatchEngine.StatePath(root), "{ not valid json");

        Assert.Null(ClientPatchEngine.ReadState(root));
    }

    // ── KONZEPT §13: one shared tree, per-file "os" ──────────────────────────────────────────────

    [Fact]
    public async Task Linux_run_loads_only_the_Linux_proxy_and_VERIFY_is_green_without_the_others()
    {
        var root = PatchingFakes.NewTempDir();
        var wowExe = "exe"u8.ToArray();
        var winProxy = "windows-proxy"u8.ToArray();
        var linuxProxy = "linux-proxy"u8.ToArray();
        var macProxy = "mac-proxy"u8.ToArray();
        var manifest = new ClientFileManifest
        {
            Build = 42597, Version = "1.4",
            Files =
            [
                PatchingFakes.Entry("WowClassic.exe", wowExe),
                WithOs(PatchingFakes.Entry("Hermes/JimsProxy.exe", winProxy), "windows"),
                WithOs(PatchingFakes.Entry("Hermes/bin/JimsProxy-linux-x64", linuxProxy), "linux"),
                WithOs(PatchingFakes.Entry("Hermes/bin/JimsProxy-arm64", macProxy), "macos"),
            ],
        };
        // Only the client exe and the Linux proxy are actually on disk - a real Linux install never
        // has the Windows/macOS binaries at all (they only exist inside the shared ZIP's OTHER
        // platform copies, not on a Linux player's disk).
        PatchingFakes.WriteFile(root, "WowClassic.exe", wowExe);
        PatchingFakes.WriteFile(root, "Hermes/bin/JimsProxy-linux-x64", linuxProxy);

        var client = NewClient();
        var download = new FakeDownloadService();
        var outcome = await NewEngine(new FakeManifestLoader(manifest), download, currentOs: "linux")
            .RunAsync(client, root, forcePerFile: true);

        Assert.Equal(PatchState.Ready, outcome.State);
        // Nothing was missing/corrupt for Linux, and the Windows/macOS proxies were never even looked
        // for - a Linux run must not download or verify files tagged for another OS.
        Assert.Empty(download.Requested);
        Assert.DoesNotContain(outcome.Findings, f => f.Code == PatchFinding.VerifyFailed);
    }

    [Fact]
    public async Task Linux_run_never_flags_the_Windows_proxy_on_disk_as_a_foreign_file()
    {
        var root = PatchingFakes.NewTempDir();
        var wowExe = "exe"u8.ToArray();
        var winProxy = "windows-proxy"u8.ToArray();
        var manifest = new ClientFileManifest
        {
            Build = 42597, Version = "1.4",
            Files =
            [
                PatchingFakes.Entry("WowClassic.exe", wowExe),
                WithOs(PatchingFakes.Entry("Hermes/JimsProxy.exe", winProxy), "windows"),
            ],
        };
        PatchingFakes.WriteFile(root, "WowClassic.exe", wowExe);
        // The shared ZIP extracted the Windows proxy too, even though this run is Linux - exactly the
        // 97%-identical-tree situation KONZEPT §13 describes.
        PatchingFakes.WriteFile(root, "Hermes/JimsProxy.exe", winProxy);

        var client = NewClient();
        var outcome = await NewEngine(new FakeManifestLoader(manifest), new FakeDownloadService(), currentOs: "linux")
            .RunAsync(client, root, forcePerFile: true);

        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.DoesNotContain(outcome.Findings, f => f.Code == PatchFinding.ForeignFile);
    }

    [Fact]
    public async Task A_file_without_an_os_tag_is_checked_on_every_OS()
    {
        var root = PatchingFakes.NewTempDir();
        var wowExe = "exe"u8.ToArray();
        var shared = "shared-data"u8.ToArray();
        var manifest = new ClientFileManifest
        {
            Build = 42597, Version = "1.4",
            Files =
            [
                PatchingFakes.Entry("WowClassic.exe", wowExe),
                PatchingFakes.Entry("Data/common.MPQ", shared), // no Os tag - applies everywhere
            ],
        };
        PatchingFakes.WriteFile(root, "WowClassic.exe", wowExe);
        // Missing on disk: a shared file must still be caught and fetched, whatever OS this is.

        var client = NewClient();
        var download = new FakeDownloadService();
        download.Content[client.FilesBase + "Data/common.MPQ"] = shared;

        var outcome = await NewEngine(new FakeManifestLoader(manifest), download, currentOs: "linux")
            .RunAsync(client, root, forcePerFile: true);

        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.Single(download.Requested);
        Assert.Equal(client.FilesBase + "Data/common.MPQ", download.Requested[0]);
    }

    // ── Item 4: the classic-era nested WTF must never be re-fetched or reported ──────────────────

    [Fact]
    public async Task File_under_classic_era_WTF_is_never_flagged_or_fetched_even_when_it_differs()
    {
        var root = PatchingFakes.NewTempDir();
        var wowExe = "exe"u8.ToArray();
        var nestedWtfPath = "World of Warcraft/_classic_era_/WTF/Config.wtf";
        var manifest = new ClientFileManifest
        {
            Build = 42597, Version = "1.4",
            Files = [PatchingFakes.Entry("WowClassic.exe", wowExe), PatchingFakes.Entry(nestedWtfPath, "shipped-default"u8.ToArray())],
            // The new shared-tree default-protected list (deploy/client-release.py DEFAULT_PROTECTED)
            // adds this nested path - "WTF/" alone (the OLD default) does not match it, because the
            // one-tree layout roots the scan at the bundle, not at "World of Warcraft/".
            Protected = ["World of Warcraft/_classic_era_/WTF/"],
        };
        PatchingFakes.WriteFile(root, "WowClassic.exe", wowExe);
        PatchingFakes.WriteFile(root, nestedWtfPath, "player-edited-config"u8.ToArray());

        var client = NewClient();
        var download = new FakeDownloadService();
        download.Content[client.FilesBase + nestedWtfPath] = "shipped-default"u8.ToArray();

        var outcome = await NewEngine(new FakeManifestLoader(manifest), download)
            .RunAsync(client, root, forcePerFile: true);

        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.Empty(download.Requested);
        Assert.Equal("player-edited-config",
            File.ReadAllText(Path.Combine(root, "World of Warcraft", "_classic_era_", "WTF", "Config.wtf")));
    }

    [Fact]
    public async Task A_savedvariables_file_under_classic_era_WTF_is_reported_neither_deleted_nor_downloaded()
    {
        // This is the ACTUAL "delete/overwrite plan" test: a file that is NOT in files.json at all
        // (the player's own SavedVariables, never shipped by the package) sitting under the classic-era
        // WTF prefix. Without "World of Warcraft/_classic_era_/WTF/" in the manifest's Protected list
        // this would show up as PatchFinding.ForeignFile - reported, which is already "never deleted"
        // per ARCHITEKTUR-v2-patcher.md §3, but the point of Protected is that it is not even
        // MENTIONED, exactly like WTF/ already is for the legacy 1.12.1 layout.
        var root = PatchingFakes.NewTempDir();
        var wowExe = "exe"u8.ToArray();
        var savedVariables = "World of Warcraft/_classic_era_/WTF/Account/DEMO/SavedVariables/Addon.lua";
        var manifest = new ClientFileManifest
        {
            Build = 42597, Version = "1.4",
            Files = [PatchingFakes.Entry("WowClassic.exe", wowExe)],
            Protected = ["World of Warcraft/_classic_era_/WTF/"],
        };
        PatchingFakes.WriteFile(root, "WowClassic.exe", wowExe);
        PatchingFakes.WriteFile(root, savedVariables, "-- player data"u8.ToArray());

        var client = NewClient();
        var download = new FakeDownloadService();
        var outcome = await NewEngine(new FakeManifestLoader(manifest), download)
            .RunAsync(client, root, forcePerFile: true);

        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.Empty(download.Requested);
        Assert.DoesNotContain(outcome.Findings, f => f.Code == PatchFinding.ForeignFile);
        Assert.True(File.Exists(Path.Combine(
            root, "World of Warcraft", "_classic_era_", "WTF", "Account", "DEMO", "SavedVariables", "Addon.lua")));
    }
}
