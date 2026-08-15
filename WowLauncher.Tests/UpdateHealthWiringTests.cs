namespace WowLauncher.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

/// <summary>
/// The health contract as the update path actually uses it.
///
/// <para><see cref="UpdateHealthTests"/> proves the pieces in isolation — that a note can be written,
/// read and cleared. That is not the same as proving they are wired in. Without the tests here, both
/// lines in <see cref="UpdateService"/> could be deleted and the whole suite would stay green while
/// the rollback quietly stopped working: no sentinel would ever be written, so the swap script would
/// never watch anything, and a quarantined version would be reinstalled on the next start. A test
/// that cannot go red for the change it guards is a placebo (CORE §3).</para>
/// </summary>
public sealed class UpdateHealthWiringTests
{
    private static readonly Serilog.ILogger Log = Serilog.Core.Logger.None;

    private static ServerManifest Offering(string version) => new()
    {
        Launcher = new ManifestFile
        {
            Version = version,
            Url = "https://downloads.stonetavern.app/launcher/x.exe",
            Sha256 = "0000000000000000000000000000000000000000000000000000000000000000",
            Size = 1,
        },
    };

    private static UpdateService Service(IUpdateHealth health, IUpdateSwapStrategy? swap = null) =>
        new(new OkDownload(), Log, swap ?? new RecordingSwap(), new SignedGate(Offering("9.9.9")),
            LauncherUpdateChannel.Windows, new Version(1, 0, 0), attempts: null, health: health);

    /// <summary>
    /// 🔴 The sentinel has to be announced BEFORE the handover, for the same reason the attempt is
    /// noted there: everything after that line happens in a process on its way out. A sentinel written
    /// later would never be written at all in exactly the case it exists for.
    /// </summary>
    [Fact]
    public async Task AnUpdate_AnnouncesWhatHasToComeUp()
    {
        var health = new RecordingHealth();

        await Service(health).CheckAndApplyAsync(Offering("9.9.9"));

        Assert.Equal(new Version(9, 9, 9), health.Expected);
    }

    /// <summary>The swap needs to know the version too — it is what lands in the quarantine note, and
    /// without it the restored build has nothing to refuse.</summary>
    [Fact]
    public async Task TheSwap_IsToldWhichVersionMustComeUp()
    {
        var swap = new RecordingSwap();

        await Service(new RecordingHealth(), swap).CheckAndApplyAsync(Offering("9.9.9"));

        Assert.Equal(new Version(9, 9, 9), swap.Target);
    }

    /// <summary>
    /// The defect this whole mechanism exists to prevent, as one test: a version that crashed on
    /// start and was rolled back must not be installed again. Without this check the restored build
    /// finds the same newer version in the manifest and walks straight back into the same crash — the
    /// rollback would have bought nothing.
    /// </summary>
    [Fact]
    public async Task AQuarantinedVersion_IsNeverInstalledAgain()
    {
        var swap = new RecordingSwap();
        var health = new RecordingHealth { Quarantined = new Version(9, 9, 9) };

        var applied = await Service(health, swap).CheckAndApplyAsync(Offering("9.9.9"));

        Assert.False(applied);
        Assert.Null(swap.Target);           // gar nicht erst getauscht
        Assert.Null(health.Expected);       // und gar nicht erst angekündigt
    }

    /// <summary>A quarantine is for one build, not for updating as such. The FIX has to be able to
    /// reach the player, otherwise one broken release ends updates forever.</summary>
    [Fact]
    public async Task AFixedVersion_GetsThroughDespiteTheQuarantine()
    {
        var swap = new RecordingSwap();
        var health = new RecordingHealth { Quarantined = new Version(9, 9, 9) };
        var svc = new UpdateService(new OkDownload(), Log, swap, new SignedGate(Offering("9.9.10")),
            LauncherUpdateChannel.Windows, new Version(1, 0, 0), attempts: null, health: health);

        await svc.CheckAndApplyAsync(Offering("9.9.10"));

        Assert.Equal(new Version(9, 9, 10), swap.Target);
    }

    /// <summary>And the player is told why they are not on the newest build, instead of silently
    /// staying behind.</summary>
    [Fact]
    public async Task AQuarantinedVersion_HandsOverToTheManualDownloadHint()
    {
        var manifest = Offering("9.9.9");
        var svc = Service(new RecordingHealth { Quarantined = new Version(9, 9, 9) });

        await svc.CheckAndApplyAsync(manifest);

        Assert.NotNull(svc.CheckForNotice(manifest));
    }

    /// <summary>No health contract at all is the behaviour that shipped before this existed: the swap
    /// runs without a net rather than not at all.</summary>
    [Fact]
    public async Task WithoutAHealthContract_TheUpdateStillApplies()
    {
        var swap = new RecordingSwap();
        var svc = new UpdateService(new OkDownload(), Log, swap, new SignedGate(Offering("9.9.9")),
            LauncherUpdateChannel.Windows, new Version(1, 0, 0));

        await svc.CheckAndApplyAsync(Offering("9.9.9"));

        Assert.Equal(new Version(9, 9, 9), swap.Target);
    }

    // ── Attrappen ────────────────────────────────────────────────────────────────────────────────

    private sealed class RecordingHealth : IUpdateHealth
    {
        public Version? Expected { get; private set; }
        public Version? Quarantined { get; init; }
        public List<Version> QuarantineChecks { get; } = [];

        public void ExpectVersion(Version target) => Expected = target;
        public void ReportHealthy() { }
        public Version? QuarantinedVersion => Quarantined;

        public bool IsQuarantined(Version target)
        {
            QuarantineChecks.Add(target);
            return Quarantined is not null && Quarantined == target;
        }
    }

    private sealed class RecordingSwap : IUpdateSwapStrategy
    {
        public Version? Target { get; private set; }
        public bool IsSupported => true;

        public bool ApplySwap(string newExePath, string currentExePath, string appDir,
            Version? target = null)
        {
            Target = target;
            return true;
        }
    }

    private sealed class SignedGate(ServerManifest signed) : IManifestSignatureGate
    {
        public Task<ServerManifest?> AcquireVerifiedAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(signed);
    }

    private sealed class OkDownload : IDownloadService
    {
        public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(DownloadResult.Success);

        public Task<bool> VerifyHashAsync(string path, string expectedSha256, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> ExtractZipAsync(string zipPath, string destDir,
            IProgress<string>? progress = null, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> ExtractClientAsync(string zipPath, string destDir,
            IProgress<string>? progress = null, CancellationToken ct = default) => Task.FromResult(true);
    }
}
