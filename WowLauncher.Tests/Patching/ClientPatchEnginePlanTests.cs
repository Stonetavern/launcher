using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text.Json;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Patching;
using Xunit;

namespace WowLauncher.Tests.Patching;

/// <summary>PLAN routing (ARCHITEKTUR-v2-patcher.md §4) — the three ordinary routes plus the
/// UP_TO_DATE short-circuit, each driven through a full <see cref="ClientPatchEngine.RunAsync"/> call
/// with in-memory fakes so the whole PLAN → route → VERIFY → WRITE_STATE path is proven, not just the
/// decision.</summary>
public sealed class ClientPatchEnginePlanTests
{
    private static ManifestFile NewClient(string version = "1.4") => new()
    {
        Build = 5875,
        Os = "windows",
        Version = version,
        Url = "http://fake.invalid/full.zip",
        Sha256 = "",
        FilesUrl = "http://fake.invalid/files.json",
        FilesSha256 = "irrelevant-in-these-tests", // FakeManifestLoader bypasses the real trust check
        FilesBase = "http://fake.invalid/tree/",
    };

    private static ClientPatchEngine NewEngine(FakeManifestLoader loader, FakeDownloadService download,
        FakeButlerSidecar butler, FakeGameProcessDetector detector) =>
        new(loader, download, new ClientVerifyService(new Serilog.LoggerConfiguration().CreateLogger()),
            butler, detector, new Serilog.LoggerConfiguration().CreateLogger(),
            delay: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task No_lead_file_on_disk_routes_to_FullZip()
    {
        var root = PatchingFakes.NewTempDir();
        var wowExe = "wow-exe-bytes"u8.ToArray();
        var manifest = new ClientFileManifest { Build = 5875, Version = "1.4", Files = [PatchingFakes.Entry("WoW.exe", wowExe)] };
        var client = NewClient();
        client.Sha256 = Convert.ToHexString(SHA256.HashData("zip-bytes"u8.ToArray())).ToLowerInvariant();

        var download = new FakeDownloadService();
        download.Content[client.Url] = "zip-bytes"u8.ToArray();
        var zipPath = Path.Combine(root, ".stonetavern", "fullclient.zip");
        download.ZipContents[zipPath] = new() { ["WoW.exe"] = wowExe };

        var engine = NewEngine(new FakeManifestLoader(manifest), download, new FakeButlerSidecar(), new FakeGameProcessDetector());
        var outcome = await engine.RunAsync(client, root, forcePerFile: false);

        Assert.Equal(PatchRoute.FullZip, outcome.Route);
        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.True(File.Exists(Path.Combine(root, "WoW.exe")));
    }

    /// <summary>Regression, found in the AP5 E2E on 2026-09-20: the 1.14.2 client ships
    /// <c>WowClassic.exe</c>, never <c>WoW.exe</c>. With the old hardcoded name a complete modern
    /// install read as "nothing here" and PLAN chose FULL_ZIP — the patch engine bypassed for every
    /// modern player. The lead name now comes from the entry's build.</summary>
    [Fact]
    public async Task Modern_install_with_WowClassic_routes_to_PerFile_not_FullZip()
    {
        var root = PatchingFakes.NewTempDir();
        var exe = "wowclassic-bytes"u8.ToArray();
        var manifest = new ClientFileManifest
        {
            Build = 42597, Version = "1.5.0",
            Files = [PatchingFakes.Entry("World of Warcraft/_classic_era_/WowClassic.exe", exe)],
        };
        var client = NewClient("1.5.0");
        client.Build = 42597;
        PatchingFakes.WriteFile(root, "World of Warcraft/_classic_era_/WowClassic.exe", exe);

        var engine = NewEngine(new FakeManifestLoader(manifest), new FakeDownloadService(),
            new FakeButlerSidecar(), new FakeGameProcessDetector());
        var outcome = await engine.RunAsync(client, root, forcePerFile: false);

        Assert.Equal(PatchRoute.PerFile, outcome.Route);
        Assert.Equal(PatchState.Ready, outcome.State);
    }

    /// <summary>The counter-proof: a modern entry whose tree really has no executable stays FULL_ZIP.
    /// Matching by build must not turn "empty folder" into "installed".</summary>
    [Fact]
    public async Task Modern_install_without_the_exe_still_routes_to_FullZip()
    {
        var root = PatchingFakes.NewTempDir();
        var manifest = new ClientFileManifest
        {
            Build = 42597, Version = "1.5.0",
            Files = [PatchingFakes.Entry("World of Warcraft/_classic_era_/WowClassic.exe", "x"u8.ToArray())],
        };
        var client = NewClient("1.5.0");
        client.Build = 42597;

        var engine = NewEngine(new FakeManifestLoader(manifest), new FakeDownloadService(),
            new FakeButlerSidecar(), new FakeGameProcessDetector());
        var outcome = await engine.RunAsync(client, root, forcePerFile: false);

        Assert.Equal(PatchRoute.FullZip, outcome.Route);
    }

    [Fact]
    public async Task Lead_file_present_with_matching_state_and_available_butler_routes_to_Delta()
    {
        var root = PatchingFakes.NewTempDir();
        var wowExe = "wow-exe-v1"u8.ToArray();
        PatchingFakes.WriteFile(root, "WoW.exe", wowExe);
        ClientPatchEngine.WriteState(root, new ClientPatchState { Build = 5875, Os = "windows", Version = "1.0", FilesSha256 = "old" });

        var manifest = new ClientFileManifest { Build = 5875, Version = "1.4", Files = [PatchingFakes.Entry("WoW.exe", wowExe)] };
        var client = NewClient();
        client.Deltas = [new ManifestDelta
        {
            From = "1.0", Url = "http://fake.invalid/1.0-to-1.4.pwr", Size = 3,
            Sha256 = Convert.ToHexString(SHA256.HashData("pwr"u8.ToArray())).ToLowerInvariant(),
            SigUrl = "http://fake.invalid/1.0-to-1.4.pwr.sig",
            SigSha256 = Convert.ToHexString(SHA256.HashData("sig"u8.ToArray())).ToLowerInvariant(),
        }];

        var download = new FakeDownloadService();
        download.Content[client.Deltas[0].Url] = "pwr"u8.ToArray();
        download.Content[client.Deltas[0].SigUrl] = "sig"u8.ToArray();
        var butler = new FakeButlerSidecar { IsAvailable = true };

        var engine = NewEngine(new FakeManifestLoader(manifest), download, butler, new FakeGameProcessDetector());
        var outcome = await engine.RunAsync(client, root, forcePerFile: false);

        Assert.Equal(PatchRoute.Delta, outcome.Route);
        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.Equal(1, butler.ApplyCalls);
        Assert.Equal(1, butler.VerifyCalls);
    }

    /// <summary>E2E 2026-09-23 (Classic 1.5 -> 1.6, Linux and Windows): the Delta route logged
    /// "0 file(s) changed" for a patch that rewrote 6 files. The count is the set that differs from
    /// the target manifest right before butler runs.</summary>
    [Fact]
    public async Task Delta_reports_the_files_that_differed_before_the_patch()
    {
        var root = PatchingFakes.NewTempDir();
        var wowExe = "wow-exe-v2"u8.ToArray();
        PatchingFakes.WriteFile(root, "WoW.exe", "wow-exe-v1"u8.ToArray());          // differs
        PatchingFakes.WriteFile(root, "Data/patch.MPQ", "patch-v1"u8.ToArray());     // differs
        PatchingFakes.WriteFile(root, "Data/common.MPQ", "common"u8.ToArray());      // unchanged
        ClientPatchEngine.WriteState(root, new ClientPatchState { Build = 5875, Os = "windows", Version = "1.0", FilesSha256 = "old" });

        var manifest = new ClientFileManifest
        {
            Build = 5875, Version = "1.4",
            Files =
            [
                PatchingFakes.Entry("WoW.exe", wowExe),
                PatchingFakes.Entry("Data/patch.MPQ", "patch-v2"u8.ToArray()),
                PatchingFakes.Entry("Data/common.MPQ", "common"u8.ToArray()),
            ],
        };
        var client = NewClient();
        client.Deltas = [new ManifestDelta
        {
            From = "1.0", Url = "http://fake.invalid/1.0-to-1.4.pwr", Size = 3,
            Sha256 = Convert.ToHexString(SHA256.HashData("pwr"u8.ToArray())).ToLowerInvariant(),
            SigUrl = "http://fake.invalid/1.0-to-1.4.pwr.sig",
            SigSha256 = Convert.ToHexString(SHA256.HashData("sig"u8.ToArray())).ToLowerInvariant(),
        }];

        var download = new FakeDownloadService();
        download.Content[client.Deltas[0].Url] = "pwr"u8.ToArray();
        download.Content[client.Deltas[0].SigUrl] = "sig"u8.ToArray();
        var butler = new FakeButlerSidecar { IsAvailable = true };

        var engine = NewEngine(new FakeManifestLoader(manifest), download, butler, new FakeGameProcessDetector());
        var outcome = await engine.RunAsync(client, root, forcePerFile: false);

        Assert.Equal(PatchRoute.Delta, outcome.Route);
        Assert.Equal(2, outcome.FilesChanged);
    }

    /// <summary>Teil A (ARCHITEKTUR-v2-patcher.md §5): PLAN must try to fetch a missing butler sidecar
    /// BEFORE deciding the route, not after — otherwise a Bestandsinstallation that self-updated
    /// through an exe-only channel would never get a second chance at Delta.</summary>
    [Fact]
    public async Task EnsureAvailableAsync_success_during_PLAN_unlocks_the_Delta_route()
    {
        var root = PatchingFakes.NewTempDir();
        var wowExe = "wow-exe-v1"u8.ToArray();
        PatchingFakes.WriteFile(root, "WoW.exe", wowExe);
        ClientPatchEngine.WriteState(root, new ClientPatchState { Build = 5875, Os = "windows", Version = "1.0", FilesSha256 = "old" });

        var manifest = new ClientFileManifest { Build = 5875, Version = "1.4", Files = [PatchingFakes.Entry("WoW.exe", wowExe)] };
        var client = NewClient();
        client.Deltas = [new ManifestDelta
        {
            From = "1.0", Url = "http://fake.invalid/1.0-to-1.4.pwr", Size = 3,
            Sha256 = Convert.ToHexString(SHA256.HashData("pwr"u8.ToArray())).ToLowerInvariant(),
            SigUrl = "http://fake.invalid/1.0-to-1.4.pwr.sig",
            SigSha256 = Convert.ToHexString(SHA256.HashData("sig"u8.ToArray())).ToLowerInvariant(),
        }];

        var download = new FakeDownloadService();
        download.Content[client.Deltas[0].Url] = "pwr"u8.ToArray();
        download.Content[client.Deltas[0].SigUrl] = "sig"u8.ToArray();
        // Starts UNAVAILABLE — a fresh Bestandsinstallation with no tools/butler/ next to it. Only
        // EnsureAvailableAsync's own success (scripted here) flips it.
        var butler = new FakeButlerSidecar { IsAvailable = false, EnsureAvailableResult = true };

        var engine = NewEngine(new FakeManifestLoader(manifest), download, butler, new FakeGameProcessDetector());
        var outcome = await engine.RunAsync(client, root, forcePerFile: false);

        Assert.Equal(1, butler.EnsureAvailableCalls);
        Assert.Equal(PatchRoute.Delta, outcome.Route);
        Assert.Equal(1, butler.ApplyCalls);
    }

    /// <summary>The mirror case: the fetch itself fails (bad network, wrong hash — proven separately
    /// in <c>ButlerSidecarEnsureAvailableTests</c>), so PLAN must fall through to PER_FILE rather than
    /// erroring out or retrying forever.</summary>
    [Fact]
    public async Task EnsureAvailableAsync_failure_during_PLAN_falls_through_to_PerFile()
    {
        var root = PatchingFakes.NewTempDir();
        var wowExe = "wow-exe-v1"u8.ToArray();
        PatchingFakes.WriteFile(root, "WoW.exe", wowExe);
        ClientPatchEngine.WriteState(root, new ClientPatchState { Build = 5875, Os = "windows", Version = "1.0", FilesSha256 = "old" });

        var manifest = new ClientFileManifest { Build = 5875, Version = "1.4", Files = [PatchingFakes.Entry("WoW.exe", wowExe)] };
        var client = NewClient();
        client.Deltas = [new ManifestDelta { From = "1.0", Url = "http://x/d.pwr", SigUrl = "http://x/d.pwr.sig" }];
        var butler = new FakeButlerSidecar { IsAvailable = false, EnsureAvailableResult = false };

        var engine = NewEngine(new FakeManifestLoader(manifest), new FakeDownloadService(), butler, new FakeGameProcessDetector());
        var outcome = await engine.RunAsync(client, root, forcePerFile: false);

        Assert.Equal(1, butler.EnsureAvailableCalls);
        Assert.Equal(PatchRoute.PerFile, outcome.Route);
        Assert.Equal(0, butler.ApplyCalls);
    }

    /// <summary>FullZip/PerFile-only installs must never pay for the fetch attempt — there is no
    /// Delta to unlock, so EnsureAvailableAsync would just be a wasted network round-trip.</summary>
    [Fact]
    public async Task No_delta_candidate_never_calls_EnsureAvailableAsync()
    {
        var root = PatchingFakes.NewTempDir();
        var wowExe = "wow-exe-v1"u8.ToArray();
        var manifest = new ClientFileManifest { Build = 5875, Version = "1.4", Files = [PatchingFakes.Entry("WoW.exe", wowExe)] };

        // Case 1: no lead file at all -> FullZip.
        var client = NewClient();
        client.Sha256 = Convert.ToHexString(SHA256.HashData("zip-bytes"u8.ToArray())).ToLowerInvariant();
        var download = new FakeDownloadService();
        download.Content[client.Url] = "zip-bytes"u8.ToArray();
        var zipPath = Path.Combine(root, ".stonetavern", "fullclient.zip");
        download.ZipContents[zipPath] = new() { ["WoW.exe"] = wowExe };
        var fullZipButler = new FakeButlerSidecar();
        var fullZipOutcome = await NewEngine(new FakeManifestLoader(manifest), download, fullZipButler, new FakeGameProcessDetector())
            .RunAsync(client, root, forcePerFile: false);
        Assert.Equal(PatchRoute.FullZip, fullZipOutcome.Route);
        Assert.Equal(0, fullZipButler.EnsureAvailableCalls);

        // Case 2: lead file present but no state.json (never patched before) -> PerFile, still no Deltas offered.
        var root2 = PatchingFakes.NewTempDir();
        PatchingFakes.WriteFile(root2, "WoW.exe", wowExe);
        var perFileButler = new FakeButlerSidecar();
        var perFileOutcome = await NewEngine(new FakeManifestLoader(manifest), new FakeDownloadService(), perFileButler, new FakeGameProcessDetector())
            .RunAsync(NewClient(), root2, forcePerFile: false);
        Assert.Equal(PatchRoute.PerFile, perFileOutcome.Route);
        Assert.Equal(0, perFileButler.EnsureAvailableCalls);
    }

    [Fact]
    public async Task Lead_file_present_without_usable_state_routes_to_PerFile()
    {
        var root = PatchingFakes.NewTempDir();
        var wowExe = "wow-exe-v1"u8.ToArray();
        PatchingFakes.WriteFile(root, "WoW.exe", wowExe); // no client-state.json at all

        var manifest = new ClientFileManifest { Build = 5875, Version = "1.4", Files = [PatchingFakes.Entry("WoW.exe", wowExe)] };
        var client = NewClient();

        var engine = NewEngine(new FakeManifestLoader(manifest), new FakeDownloadService(),
            new FakeButlerSidecar(), new FakeGameProcessDetector());
        var outcome = await engine.RunAsync(client, root, forcePerFile: false);

        Assert.Equal(PatchRoute.PerFile, outcome.Route);
        Assert.Equal(PatchState.Ready, outcome.State);
    }

    [Fact]
    public async Task Force_per_file_skips_delta_even_when_one_would_apply()
    {
        var root = PatchingFakes.NewTempDir();
        var wowExe = "wow-exe-v1"u8.ToArray();
        PatchingFakes.WriteFile(root, "WoW.exe", wowExe);
        ClientPatchEngine.WriteState(root, new ClientPatchState { Build = 5875, Os = "windows", Version = "1.0" });

        var manifest = new ClientFileManifest { Build = 5875, Version = "1.4", Files = [PatchingFakes.Entry("WoW.exe", wowExe)] };
        var client = NewClient();
        client.Deltas = [new ManifestDelta { From = "1.0", Url = "http://x/d.pwr", SigUrl = "http://x/d.pwr.sig" }];
        var butler = new FakeButlerSidecar { IsAvailable = true };

        var engine = NewEngine(new FakeManifestLoader(manifest), new FakeDownloadService(), butler, new FakeGameProcessDetector());
        var outcome = await engine.RunAsync(client, root, forcePerFile: true);

        Assert.Equal(PatchRoute.PerFile, outcome.Route);
        Assert.Equal(0, butler.ApplyCalls);
    }

    [Fact]
    public async Task Matching_state_and_files_sha256_routes_to_UpToDate_without_any_download()
    {
        var root = PatchingFakes.NewTempDir();
        var wowExe = "wow-exe-v1"u8.ToArray();
        PatchingFakes.WriteFile(root, "WoW.exe", wowExe);
        var client = NewClient();
        ClientPatchEngine.WriteState(root, new ClientPatchState
        {
            Build = client.Build!.Value, Os = client.Os, Version = client.Version, FilesSha256 = client.FilesSha256,
        });

        var manifest = new ClientFileManifest { Build = 5875, Version = "1.4", Files = [PatchingFakes.Entry("WoW.exe", wowExe)] };
        var download = new FakeDownloadService();
        var engine = NewEngine(new FakeManifestLoader(manifest), download, new FakeButlerSidecar(), new FakeGameProcessDetector());

        var outcome = await engine.RunAsync(client, root, forcePerFile: false);

        Assert.Equal(PatchRoute.UpToDate, outcome.Route);
        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.Empty(download.Requested);
    }

    [Fact]
    public async Task Game_running_blocks_plan_with_a_finding_and_touches_nothing()
    {
        var root = PatchingFakes.NewTempDir();
        var wowExe = "wow-exe-v1"u8.ToArray();
        PatchingFakes.WriteFile(root, "WoW.exe", wowExe);
        var manifest = new ClientFileManifest { Build = 5875, Version = "1.4", Files = [PatchingFakes.Entry("WoW.exe", wowExe)] };
        var download = new FakeDownloadService();
        var detector = new FakeGameProcessDetector { Running = true };

        var engine = NewEngine(new FakeManifestLoader(manifest), download, new FakeButlerSidecar(), detector);
        var outcome = await engine.RunAsync(NewClient(), root, forcePerFile: false);

        Assert.Equal(PatchState.GameRunning, outcome.State);
        Assert.Contains(outcome.Findings, f => f.Code == PatchFinding.GameRunning);
        Assert.Empty(download.Requested);
        Assert.False(File.Exists(ClientPatchEngine.StatePath(root)));
    }

    [Fact]
    public async Task Missing_files_json_ends_in_error_with_a_finding()
    {
        var root = PatchingFakes.NewTempDir();
        var engine = NewEngine(new FakeManifestLoader(null), new FakeDownloadService(),
            new FakeButlerSidecar(), new FakeGameProcessDetector());

        var outcome = await engine.RunAsync(NewClient(), root, forcePerFile: false);

        Assert.Equal(PatchState.Error, outcome.State);
        Assert.Contains(outcome.Findings, f => f.Code == PatchFinding.FilesJsonUnavailable);
    }
}
