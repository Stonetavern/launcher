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

/// <summary>Delta failures ALWAYS fall back to PER_FILE (ARCHITEKTUR-v2-patcher.md §4: "Fehler
/// irgendwo → PER_FILE") and the run still ends correct — the three ways a Delta can go wrong: a
/// tampered/mismatched package hash, a butler that reports apply-failed outright, and the S0 case
/// that matters most, butler apply exiting 0 against the wrong base while verify catches it.</summary>
public sealed class ClientPatchEngineDeltaFallbackTests
{
    private static ManifestFile NewClient() => new()
    {
        Build = 5875, Os = "windows", Version = "1.4",
        FilesUrl = "http://fake.invalid/files.json", FilesSha256 = "irrelevant",
        FilesBase = "http://fake.invalid/tree/",
    };

    private static (ClientFileManifest manifest, byte[] wowExe) NewManifestWithLeadFile()
    {
        var wowExe = "wow-exe-target-bytes"u8.ToArray();
        return (new ClientFileManifest { Build = 5875, Version = "1.4", Files = [PatchingFakes.Entry("WoW.exe", wowExe)] }, wowExe);
    }

    private static ClientPatchEngine NewEngine(FakeManifestLoader loader, FakeDownloadService download, FakeButlerSidecar butler) =>
        new(loader, download, new ClientVerifyService(new Serilog.LoggerConfiguration().CreateLogger()),
            butler, new FakeGameProcessDetector(), new Serilog.LoggerConfiguration().CreateLogger(),
            delay: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task Wrong_delta_package_hash_falls_back_to_PerFile_and_still_ends_correct()
    {
        var root = PatchingFakes.NewTempDir();
        var (manifest, wowExe) = NewManifestWithLeadFile();
        PatchingFakes.WriteFile(root, "WoW.exe", "wow-exe-OLD-bytes"u8.ToArray()); // wrong content on purpose
        ClientPatchEngine.WriteState(root, new ClientPatchState { Build = 5875, Os = "windows", Version = "1.0" });

        var client = NewClient();
        client.Deltas = [new ManifestDelta
        {
            From = "1.0", Url = "http://x/d.pwr", Sha256 = "0000000000000000000000000000000000000000000000000000000000000000"[..64],
            SigUrl = "http://x/d.pwr.sig", SigSha256 = "1111111111111111111111111111111111111111111111111111111111111111"[..64],
        }];
        var download = new FakeDownloadService();
        download.Content[client.Deltas[0].Url] = "pwr-bytes"u8.ToArray(); // hash will NOT match the pinned Sha256 above
        download.Content[client.Deltas[0].SigUrl] = "sig-bytes"u8.ToArray();
        // PER_FILE fallback needs to be able to fetch the correct WoW.exe.
        download.Content[client.FilesBase + "WoW.exe"] = wowExe;
        var butler = new FakeButlerSidecar { IsAvailable = true };

        var outcome = await NewEngine(new FakeManifestLoader(manifest), download, butler).RunAsync(client, root, forcePerFile: false);

        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.Equal(PatchRoute.PerFile, outcome.Route); // fell back
        Assert.Equal(0, butler.ApplyCalls); // never got far enough to call apply
        Assert.Contains(outcome.Findings, f => f.Code == PatchFinding.DeltaFellBack);
        Assert.Equal(wowExe, File.ReadAllBytes(Path.Combine(root, "WoW.exe")));
    }

    [Fact]
    public async Task Butler_apply_failure_falls_back_to_PerFile()
    {
        var root = PatchingFakes.NewTempDir();
        var (manifest, wowExe) = NewManifestWithLeadFile();
        PatchingFakes.WriteFile(root, "WoW.exe", "stale"u8.ToArray());
        ClientPatchEngine.WriteState(root, new ClientPatchState { Build = 5875, Os = "windows", Version = "1.0" });

        var client = NewClient();
        client.Deltas = [new ManifestDelta { From = "1.0", Url = "http://x/d.pwr", SigUrl = "http://x/d.pwr.sig" }];
        var download = new FakeDownloadService();
        download.Content[client.Deltas[0].Url] = "pwr"u8.ToArray();
        download.Content[client.Deltas[0].SigUrl] = "sig"u8.ToArray();
        download.Content[client.FilesBase + "WoW.exe"] = wowExe;
        // Hashes must match what was "downloaded" for apply to even be attempted.
        client.Deltas[0].Sha256 = Sha256Of("pwr"u8.ToArray());
        client.Deltas[0].SigSha256 = Sha256Of("sig"u8.ToArray());

        var butler = new FakeButlerSidecar { IsAvailable = true, ApplyResult = ButlerResult.Fail(1, "boom") };

        var outcome = await NewEngine(new FakeManifestLoader(manifest), download, butler).RunAsync(client, root, forcePerFile: false);

        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.Equal(PatchRoute.PerFile, outcome.Route);
        Assert.Equal(1, butler.ApplyCalls);
        Assert.Equal(0, butler.VerifyCalls); // apply already failed — no point verifying
        Assert.Equal(wowExe, File.ReadAllBytes(Path.Combine(root, "WoW.exe")));
    }

    /// <summary>The S0 lesson (ARCHITEKTUR-v2-patcher.md §5): "butler apply Exit 0 ist KEINE Aussage".
    /// Apply reports success against the wrong base; verify — which the engine ALWAYS runs after apply
    /// — catches it, and the engine still ends at the correct file, not the silently wrong one.</summary>
    [Fact]
    public async Task Apply_exit_0_against_wrong_base_is_caught_by_verify_and_falls_back_to_PerFile()
    {
        var root = PatchingFakes.NewTempDir();
        var (manifest, wowExe) = NewManifestWithLeadFile();
        PatchingFakes.WriteFile(root, "WoW.exe", "wrong-base"u8.ToArray());
        ClientPatchEngine.WriteState(root, new ClientPatchState { Build = 5875, Os = "windows", Version = "1.0" });

        var client = NewClient();
        client.Deltas = [new ManifestDelta
        {
            From = "1.0", Url = "http://x/d.pwr", Sha256 = Sha256Of("pwr"u8.ToArray()),
            SigUrl = "http://x/d.pwr.sig", SigSha256 = Sha256Of("sig"u8.ToArray()),
        }];
        var download = new FakeDownloadService();
        download.Content[client.Deltas[0].Url] = "pwr"u8.ToArray();
        download.Content[client.Deltas[0].SigUrl] = "sig"u8.ToArray();
        download.Content[client.FilesBase + "WoW.exe"] = wowExe;

        // Apply "succeeds" (writes a WRONG WoW.exe — exactly the silent-corruption shape S0 measured)
        // but VerifyAsync (butler verify) reports it corrupted, same as the real butler did in
        // A1/verify-A1-wrongbase2.log.
        var butler = new FakeButlerSidecar
        {
            IsAvailable = true,
            ApplyResult = ButlerResult.Success(0),
            VerifyResult = ButlerResult.Fail(1, "64.00 KiB corrupted data found"),
            FilesToWriteOnApply = new() { ["WoW.exe"] = "silently-wrong-bytes"u8.ToArray() },
        };

        var outcome = await NewEngine(new FakeManifestLoader(manifest), download, butler).RunAsync(client, root, forcePerFile: false);

        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.Equal(PatchRoute.PerFile, outcome.Route);
        Assert.Equal(1, butler.ApplyCalls);
        Assert.Equal(1, butler.VerifyCalls); // verify WAS called, and it is what caught the problem
        Assert.Contains(outcome.Findings, f => f.Code == PatchFinding.DeltaFellBack);
        // End state is the CORRECT bytes, fetched by the PER_FILE fallback, not the corrupted ones apply wrote.
        Assert.Equal(wowExe, File.ReadAllBytes(Path.Combine(root, "WoW.exe")));
    }

    /// <summary>R2 (KONZEPT §12): after a successful delta, butler verify already proved the whole
    /// applied tree. The engine must not hash the same 8 GB a second time — measured on this counter:
    /// one call is the DELTA disk-space pre-check, two would mean the redundant full scan is back.</summary>
    [Fact]
    public async Task After_a_successful_delta_the_full_hash_scan_is_skipped()
    {
        var root = PatchingFakes.NewTempDir();
        var (manifest, wowExe) = NewManifestWithLeadFile();
        PatchingFakes.WriteFile(root, "WoW.exe", "old"u8.ToArray());
        ClientPatchEngine.WriteState(root, new ClientPatchState { Build = 5875, Os = "windows", Version = "1.0" });

        var client = NewClient();
        client.Deltas = [new ManifestDelta
        {
            From = "1.0", Url = "http://x/d.pwr", Sha256 = Sha256Of("pwr"u8.ToArray()),
            SigUrl = "http://x/d.pwr.sig", SigSha256 = Sha256Of("sig"u8.ToArray()),
        }];
        var download = new FakeDownloadService();
        download.Content[client.Deltas[0].Url] = "pwr"u8.ToArray();
        download.Content[client.Deltas[0].SigUrl] = "sig"u8.ToArray();
        var butler = new FakeButlerSidecar
        {
            IsAvailable = true,
            ApplyResult = ButlerResult.Success(0),
            VerifyResult = ButlerResult.Success(0),
            FilesToWriteOnApply = new() { ["WoW.exe"] = wowExe },
        };
        var verify = new CountingVerifyService(
            new ClientVerifyService(new Serilog.LoggerConfiguration().CreateLogger()));

        var engine = new ClientPatchEngine(new FakeManifestLoader(manifest), download, verify, butler,
            new FakeGameProcessDetector(), new Serilog.LoggerConfiguration().CreateLogger(),
            delay: (_, _) => Task.CompletedTask);
        var outcome = await engine.RunAsync(client, root, forcePerFile: false);

        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.Equal(PatchRoute.Delta, outcome.Route);
        Assert.Equal(1, butler.VerifyCalls);
        Assert.Equal(1, verify.Calls); // pre-check only — no second 8 GB hash pass
    }

    /// <summary>Counts calls so the R2 test can tell "one scan" from "two scans" without faking the
    /// verifier's answers.</summary>
    private sealed class CountingVerifyService(IClientVerifyService inner) : IClientVerifyService
    {
        public int Calls;
        public Task<VerifyReport> VerifyAsync(string installDir, ClientFileManifest manifest,
            IProgress<VerifyProgress>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            return inner.VerifyAsync(installDir, manifest, progress, ct);
        }
    }

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
}
