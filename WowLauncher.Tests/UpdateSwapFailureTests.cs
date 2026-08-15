namespace WowLauncher.Tests;

using System;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

/// <summary>
/// A failed update must cost the player the UPDATE, never the LAUNCHER.
///
/// <para>Staging the swap helper used to throw on purpose. But this path is reached from startup
/// through a fire-and-forget call, so the exception surfaced nowhere: it abandoned initialisation and
/// left the player looking at a launcher that never finished starting — no error, no hint, no way to
/// update by hand. Found in the pre-release review of 1.6.1, before it reached anyone.</para>
/// </summary>
public class UpdateSwapFailureTests
{
    private static readonly Serilog.ILogger Log = Serilog.Core.Logger.None;

    private static ServerManifest Tempting() => new()
    {
        Launcher = new ManifestFile
        {
            Version = "9.9.9",
            Url = "https://downloads.stonetavern.app/launcher/x.exe",
            Sha256 = "0000000000000000000000000000000000000000000000000000000000000000",
            Size = 1,
        },
    };

    private static UpdateService Service(IUpdateSwapStrategy swap) =>
        new(new OkDownload(), Log, swap, new SignedGate(Tempting()),
            LauncherUpdateChannel.Windows, new Version(1, 0, 0));

    /// <summary>THE regression: a throwing swap must be answered, not propagated.</summary>
    [Fact]
    public async Task AThrowingSwap_DoesNotEscapeIntoStartup()
    {
        var svc = Service(new ThrowingSwap());

        var applied = await svc.CheckAndApplyAsync(Tempting());   // must not throw

        Assert.False(applied);
    }

    /// <summary>And it must not fail silently either: the player gets the manual-download hint, which
    /// is the only remaining way for them to get a working launcher.</summary>
    [Fact]
    public async Task AThrowingSwap_HandsOverToTheManualDownloadHint()
    {
        var manifest = Tempting();
        var svc = Service(new ThrowingSwap());

        await svc.CheckAndApplyAsync(manifest);

        var notice = svc.CheckForNotice(manifest);
        Assert.NotNull(notice);
        Assert.Equal("9.9.9", notice!.Version);
    }

    /// <summary>Cancellation is not a failure and must still propagate — swallowing it here would make
    /// a shutting-down launcher look like a broken update.</summary>
    [Fact]
    public async Task ACancelledSwap_StillPropagates()
    {
        var svc = Service(new CancellingSwap());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.CheckAndApplyAsync(Tempting()));
    }

    /// <summary>A download that cannot land is the real-world case behind this: a launcher installed
    /// under C:\Program Files may not write next to itself, so the update file never arrives. Measured
    /// in the win11 VM on 2026-08-02 — four consecutive starts, no loop, but also no sign to the player
    /// that a newer launcher existed at all. Losing auto-apply is acceptable; losing the information
    /// is not.</summary>
    [Fact]
    public async Task ADownloadThatCannotLand_HandsOverToTheManualDownloadHint()
    {
        var manifest = Tempting();
        var svc = new UpdateService(new FailingDownload(), Log, new WorkingSwap(), new SignedGate(manifest),
            LauncherUpdateChannel.Windows, new Version(1, 0, 0));

        var applied = await svc.CheckAndApplyAsync(manifest);

        Assert.False(applied);
        Assert.Equal("9.9.9", svc.CheckForNotice(manifest)?.Version);
    }

    /// <summary>A hash mismatch is discarded — correctly, an unverified binary is never run — but the
    /// player still needs a way to the working launcher, so the hint takes over here too.</summary>
    [Fact]
    public async Task ARejectedHash_HandsOverToTheManualDownloadHint()
    {
        var manifest = Tempting();
        var svc = new UpdateService(new BadHashDownload(), Log, new WorkingSwap(), new SignedGate(manifest),
            LauncherUpdateChannel.Windows, new Version(1, 0, 0));

        var applied = await svc.CheckAndApplyAsync(manifest);

        Assert.False(applied);
        Assert.Equal("9.9.9", svc.CheckForNotice(manifest)?.Version);
    }

    /// <summary>A cancelled download is a launcher shutting down, not a broken update. Hinting there
    /// would dress a normal exit as a failure — the same distinction the swap path makes for
    /// <see cref="OperationCanceledException"/>.</summary>
    [Fact]
    public async Task ACancelledDownload_SaysNothing()
    {
        var manifest = Tempting();
        var svc = new UpdateService(new CancelledDownload(), Log, new WorkingSwap(), new SignedGate(manifest),
            LauncherUpdateChannel.Windows, new Version(1, 0, 0));

        await svc.CheckAndApplyAsync(manifest);

        Assert.Null(svc.CheckForNotice(manifest));
    }

    /// <summary>A swap that reports success must stay silent — hinting then would tell players to
    /// download by hand what the launcher just installed for them.</summary>
    [Fact]
    public async Task ASuccessfulSwap_SaysNothing()
    {
        var manifest = Tempting();
        var svc = Service(new WorkingSwap());

        await svc.CheckAndApplyAsync(manifest);

        Assert.Null(svc.CheckForNotice(manifest));
    }

    /// <summary>The announcement must come BEFORE the download, because the download is the long part.
    /// Measured 2026-08-02 in the win11 VM: 61 MB took 14 s, and the start screen — which only learned
    /// of the update afterwards — timed out at 12 s and dropped into a shell that vanished two seconds
    /// later. Ordering is the whole fix, so ordering is what this asserts.</summary>
    [Fact]
    public async Task TheUpdateIsAnnounced_BeforeTheDownloadStarts()
    {
        var manifest = Tempting();
        var announced = 0;
        var download = new RecordingDownload(() => announced);
        var svc = new UpdateService(download, Log, new WorkingSwap(), new SignedGate(manifest),
            LauncherUpdateChannel.Windows, new Version(1, 0, 0));
        svc.LauncherUpdateStarting += (_, _) => announced++;

        await svc.CheckAndApplyAsync(manifest);

        Assert.True(download.Started, "the download never ran, so the ordering claim proves nothing");
        Assert.Equal(1, announced);
        Assert.Equal(1, download.AnnouncedWhenDownloadStarted);   // already announced when it began
    }

    /// <summary>No update, no announcement — a launcher that is already current must stay silent.</summary>
    [Fact]
    public async Task NothingIsAnnounced_WhenNoUpdateApplies()
    {
        var manifest = Tempting();
        var svc = new UpdateService(new OkDownload(), Log, new WorkingSwap(), new SignedGate(manifest),
            LauncherUpdateChannel.Windows, new Version(9, 9, 9));   // already at the offered version
        var announced = 0;
        svc.LauncherUpdateStarting += (_, _) => announced++;

        await svc.CheckAndApplyAsync(manifest);

        Assert.Equal(0, announced);
    }

    /// <summary>Records whether the announcement had already been made at the moment the download
    /// began — the ordering cannot be read off a log line, so it is captured where it happens.</summary>
    private sealed class RecordingDownload : OkDownload
    {
        private readonly Func<int> _readAnnounced;

        public RecordingDownload(Func<int> readAnnounced) => _readAnnounced = readAnnounced;

        public bool Started { get; private set; }
        public int AnnouncedWhenDownloadStarted { get; private set; } = -1;

        public override Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Started = true;
            AnnouncedWhenDownloadStarted = _readAnnounced();
            return base.DownloadFileAsync(url, destPath, progress, ct);
        }
    }

    private sealed class WorkingSwap : IUpdateSwapStrategy
    {
        public bool IsSupported => true;
        public bool ApplySwap(string newExePath, string currentExePath, string appDir, Version? target = null) => true;
    }

    private sealed class FailingDownload : OkDownload
    {
        public override Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(DownloadResult.Fail(DownloadFailure.DiskIo, "install directory not writable"));
    }

    private sealed class CancelledDownload : OkDownload
    {
        public override Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(DownloadResult.Fail(DownloadFailure.Cancelled));
    }

    private sealed class BadHashDownload : OkDownload
    {
        public override Task<bool> VerifyHashAsync(string path, string expectedSha256,
            CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class ThrowingSwap : IUpdateSwapStrategy
    {
        public bool IsSupported => true;
        public bool ApplySwap(string newExePath, string currentExePath, string appDir, Version? target = null) =>
            throw new UnauthorizedAccessException("staging the helper script was refused");
    }

    private sealed class CancellingSwap : IUpdateSwapStrategy
    {
        public bool IsSupported => true;
        public bool ApplySwap(string newExePath, string currentExePath, string appDir, Version? target = null) =>
            throw new OperationCanceledException();
    }

    private sealed class SignedGate(ServerManifest signed) : IManifestSignatureGate
    {
        public Task<ServerManifest?> AcquireVerifiedAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(signed);
    }

    private class OkDownload : IDownloadService
    {
        public virtual Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(DownloadResult.Success);

        public virtual Task<bool> VerifyHashAsync(string path, string expectedSha256, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> ExtractZipAsync(string zipPath, string destDir,
            IProgress<string>? progress = null, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> ExtractClientAsync(string zipPath, string destDir,
            IProgress<string>? progress = null, CancellationToken ct = default) => Task.FromResult(true);
    }
}
