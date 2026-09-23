using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests.E2E;

/// <summary>
/// <b>Ebene B.2 und B.4</b> of
/// <c>(internal design notes, not published)</c>.
///
/// <para><b>B.2 — the chain of trust, negatively.</b> Four manifests that must each stop the update
/// dead, over the real transport and through the real gate. 🔴 Four refusals prove nothing on their
/// own: an environment that never updates anything would produce the same four. The positive control
/// is <c>SelfUpdateChainTests.TheWholeCycle_RunsFromManifestToARestartedNewBuild</c>, which uses this
/// exact wiring and DOES swap — plus <see cref="AValidManifest_InThisVeryFixture_StillApplies"/>
/// here, so the control cannot be lost by editing another file. An empty measurement is not a
/// finding.</para>
///
/// <para><b>B.4 — resume.</b> The interesting case is not "a download continues" (that is
/// <c>DownloadResumeTests</c>) but the one where the process is gone and the only thing left is what
/// is on disk. What is measured is the SECOND request as the server saw it: its <c>Range</c>, its
/// <c>If-Range</c>, and how many bytes actually crossed the wire.</para>
/// </summary>
public sealed class TrustChainAndResumeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "stonetavern-b24-" + Guid.NewGuid().ToString("N"));
    private readonly string? _appImageBefore = Environment.GetEnvironmentVariable("APPIMAGE");

    private static Serilog.ILogger Log => new Serilog.LoggerConfiguration().CreateLogger();

    public TrustChainAndResumeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("APPIMAGE", _appImageBefore);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ─── B.2: four ways the chain must refuse ─────────────────────────────

    [Fact]
    public async Task ATamperedManifest_IsRefused_AndNothingIsFetched()
    {
        // One byte changed AFTER signing: the shape a CDN compromise has, and the only one a SHA-256
        // written in the same document can never catch.
        await AssertRefusedAsync(m => m, corruptOneByte: true);
    }

    [Fact]
    public async Task AnExpiredManifest_IsRefused()
    {
        // Replay of a genuinely signed but old document. The grace window is 24 h, so this is two days
        // back — deliberately outside it rather than just barely.
        await AssertRefusedAsync(m =>
        {
            m.Expires = DateTimeOffset.UtcNow.AddDays(-2).ToString("O");
            return m;
        });
    }

    [Fact]
    public async Task AReplayedOlderSerial_IsRefused()
    {
        var store = new MemoryTrustStore().WithFloor(2000);
        await AssertRefusedAsync(m =>
        {
            m.Serial = 1999;   // one below what this launcher has already accepted
            return m;
        }, store: store);
    }

    [Fact]
    public async Task AManifestForAnotherChannel_IsRefused()
    {
        await AssertRefusedAsync(m =>
        {
            m.Channel = "beta";
            return m;
        }, channel: "stable");
    }

    [Fact]
    public async Task AValidManifest_InThisVeryFixture_StillApplies()
    {
        // The positive control for the four above, in the same file and the same wiring. Without it a
        // later edit that broke the fixture would turn all four green for the wrong reason.
        var (origin, release, currentExe, service) = Arrange(m => m, out var installed);
        using (origin)
        using (release)
        {
            Assert.True(await service.CheckAndApplyAsync(TestRelease.AdmissibleManifest()),
                "the fixture itself cannot apply an update — the four refusals above prove nothing");
            Assert.Contains(origin.Requests, r => r.Path.StartsWith("/launcher/", StringComparison.Ordinal));
            Assert.Contains($"VERSION={installed}", File.ReadAllText(currentExe), StringComparison.Ordinal);
            // The swap itself is not run here (no helper): the file is still the old one, and the
            // measurement that matters is that the launcher got as far as fetching the new binary.
        }
    }

    // ─── B.4: resume after the process is gone ────────────────────────────

    [Fact]
    public async Task AfterAnInterruptedTransfer_TheNextAttemptResumes_AndCarriesTheRestOnly()
    {
        using var origin = TestOrigin.Start();
        var payload = Payload(400_000);
        origin.Publish("/client/base.zip", payload);
        origin.TruncateAfter("/client/base.zip", 120_000);

        var dest = Path.Combine(_root, "base.zip");

        // Attempt one: the connection dies mid-body. No retry schedule — the point is the state left
        // behind, not the recovery loop (DownloadSelfHealingTests owns that one).
        var first = await NewDownloadService(origin).DownloadFileAsync(origin.BaseUrl + "/client/base.zip", dest);
        Assert.False(first.Ok);
        Assert.Equal(DownloadFailure.Network, first.Failure);

        // What survived the "power cut": the partial bytes and the note saying where they came from.
        var part = dest + ".part";
        Assert.True(File.Exists(part), "no partial file survived — the next attempt would start from zero");
        var carried = new FileInfo(part).Length;
        Assert.InRange(carried, 1, payload.Length - 1);
        Assert.True(File.Exists(DownloadService.PartOwnerPath(dest)),
            "no provenance note — a resume would then be a guess about which file these bytes are from");

        // Attempt two, from a FRESH service and a fresh connection: everything it knows it reads off
        // the disk. The server stops truncating, as a server does once the network is back.
        origin.TruncateAfter("/client/base.zip", null);
        origin.ClearRequests();

        var second = await NewDownloadService(origin).DownloadFileAsync(origin.BaseUrl + "/client/base.zip", dest);

        Assert.True(second.Ok, $"the resumed download failed: {second.Failure} {second.Detail}");
        Assert.Equal(payload, await File.ReadAllBytesAsync(dest));

        // The measurement (PLAN §3 B.4): what the SERVER saw. Fewer bytes than the first time, offered
        // with the offset the partial file ends at, and with an identity so the server can refuse to
        // splice two different packages together.
        var resumed = Assert.Single(origin.Requests);
        Assert.Equal(206, resumed.Status);
        Assert.Equal($"bytes={carried}-", resumed.Range);
        Assert.False(string.IsNullOrEmpty(resumed.IfRange),
            "resumed without an identity — the server cannot tell whether its copy still matches");
        Assert.Equal(payload.Length - carried, resumed.BytesWritten);
        Assert.True(resumed.BytesWritten < payload.Length, "the resume carried the whole file again");

        // Nothing left behind once it completed.
        Assert.False(File.Exists(part));
        Assert.False(File.Exists(DownloadService.PartOwnerPath(dest)));
    }

    [Fact]
    public async Task IfThePackageChangedWhilePaused_TheResumeStartsOver_InsteadOfSplicing()
    {
        // The expensive silent failure of the resume path: new bytes appended to old ones produce an
        // archive that never existed, and it only surfaces as a checksum error gigabytes later — which
        // reads like a broken download, so the player downloads it all again and lands in the same place.
        using var origin = TestOrigin.Start();
        origin.Publish("/client/base.zip", Payload(400_000));
        origin.TruncateAfter("/client/base.zip", 120_000);

        var dest = Path.Combine(_root, "base.zip");
        Assert.False((await NewDownloadService(origin).DownloadFileAsync(origin.BaseUrl + "/client/base.zip", dest)).Ok);

        // Same address, different package — the case the sidecar and If-Range exist for.
        var replacement = Payload(400_000, seed: 7);
        origin.Publish("/client/base.zip", replacement);
        origin.ClearRequests();

        var second = await NewDownloadService(origin).DownloadFileAsync(origin.BaseUrl + "/client/base.zip", dest);

        Assert.True(second.Ok, $"{second.Failure} {second.Detail}");
        Assert.Equal(replacement, await File.ReadAllBytesAsync(dest));

        // The server answered 200, not 206 — it recognised the offered identity as stale and sent
        // everything. The full length crossing the wire is the proof that nothing was spliced.
        var request = origin.Requests.Last();
        Assert.Equal(200, request.Status);
        Assert.Equal(replacement.Length, request.BytesWritten);
    }

    // ─── wiring ───────────────────────────────────────────────────────────

    /// <summary>The real transfer path with the recovery schedule turned off. The internal seam exists
    /// for exactly this: sixty-seven seconds of backoff per case would push the suite into the range
    /// where people stop running it, and the retry loop has its own tests.</summary>
    private static DownloadService NewDownloadService(TestOrigin origin) =>
        new(origin.HttpClientForPlain(), Log, delay: (_, _) => Task.CompletedTask, attempts: 1);

    private async Task AssertRefusedAsync(Func<ServerManifest, ServerManifest> spoil,
        bool corruptOneByte = false, IManifestTrustStore? store = null, string? channel = null)
    {
        var (origin, release, currentExe, service) =
            Arrange(spoil, out var installed, corruptOneByte, store, channel);
        using (origin)
        using (release)
        {
            Assert.False(await service.CheckAndApplyAsync(TestRelease.AdmissibleManifest()),
                "the launcher accepted a manifest it must refuse");

            // The measurement, not the return value: the binary was never asked for. A launcher that
            // downloads first and judges afterwards has already spent a player's bandwidth on an
            // artefact nobody vouched for.
            Assert.DoesNotContain(origin.Requests, r => r.Path.StartsWith("/launcher/", StringComparison.Ordinal));
            Assert.Contains($"VERSION={installed}", File.ReadAllText(currentExe), StringComparison.Ordinal);
            Assert.False(File.Exists(currentExe + LinuxUpdateSwapStrategy.PreviousSuffix));
            Assert.False(File.Exists(currentExe + ".download"));
        }
    }

    /// <summary>One fixture for all of B.2: a launcher at 1.6.4, an origin offering 1.7.0, and a
    /// manifest the caller may spoil in exactly one way.</summary>
    private (TestOrigin Origin, TestRelease Release, string CurrentExe, UpdateService Service) Arrange(
        Func<ServerManifest, ServerManifest> spoil, out Version installed, bool corruptOneByte = false,
        IManifestTrustStore? store = null, string? channel = null)
    {
        installed = new Version(1, 6, 4);
        var origin = TestOrigin.Start();
        var release = new TestRelease();

        var currentExe = Path.Combine(_root, "stonetavern-launcher");
        File.WriteAllText(currentExe, $"#!/bin/sh\nVERSION={installed}\n");
        File.SetUnixFileMode(currentExe,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("APPIMAGE", currentExe);

        var newBytes = Encoding.UTF8.GetBytes("#!/bin/sh\nVERSION=1.7.0\n");
        origin.Publish("/launcher/stonetavern-launcher-1.7.0", newBytes);

        var manifest = spoil(TestRelease.AdmissibleManifest());
        manifest.LauncherLinux = new ManifestFile
        {
            Version = "1.7.0",
            Url = origin.BaseUrl + "/launcher/stonetavern-launcher-1.7.0",
            Sha256 = Convert.ToHexString(SHA256.HashData(newBytes)).ToLowerInvariant(),
        };
        release.Publish(origin, manifest, corruptOneByte);

        var service = new UpdateService(
            new DownloadService(origin.HttpClientForPlain(), Log, (_, _) => Task.CompletedTask, 1),
            Log,
            // No swap is performed in these cases — the chain must stop long before it. A strategy that
            // reports "supported" and does nothing makes a wrongly-accepted manifest show up as a
            // fetched binary rather than as a crash.
            new NoOpSwap(),
            release.Gate(origin, Log, store, channel),
            LauncherUpdateChannel.Linux, currentVersion: installed);

        return (origin, release, currentExe, service);
    }

    private sealed class NoOpSwap : IUpdateSwapStrategy
    {
        public bool IsSupported => true;

        public bool ApplySwap(string newExePath, string currentExePath, string appDir, Version? target = null)
            => true;
    }

    private static byte[] Payload(int size, int seed = 0)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)((i + seed * 97) % 251);
        return bytes;
    }
}
