using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Patching;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests.Patching;

/// <summary>
/// Teil A (ARCHITEKTUR-v2-patcher.md §5, 2026-09-19): a Bestandsinstallation that self-updated
/// through an exe-only (Windows) or AppImage-only (Linux) channel never got <c>tools/butler/</c>
/// next to its launcher at all — <see cref="IButlerSidecar.EnsureAvailableAsync"/> is the one-time
/// fetch that reopens the Delta route for it. These tests run the REAL SHA-256 gate both ways: the
/// download fake writes genuine bytes (either the real vendored butler files, so the hash genuinely
/// matches the pin, or genuine garbage, so it genuinely does not) rather than scripting the
/// verdict — the same discipline as <see cref="ButlerSidecarRealBinaryTests"/>. Linux-only, like
/// that file, because that is the RID this dev/CI machine can vendor a fixture for; every test is a
/// real [SkippableFact] skip (not a silent pass) when the fixture is absent.
/// </summary>
public sealed class ButlerSidecarEnsureAvailableTests
{
    /// <summary>Real vendored linux-x64 butler + 7z sidecars, copied next to the test assembly by
    /// WowLauncher.Tests.csproj — see <see cref="ButlerSidecarRealBinaryTests"/> for the same fixture.
    /// Serving THESE bytes back through the fake download is what lets the "correct bytes" tests
    /// prove a real pinned-hash match instead of a scripted one.</summary>
    private static string RealFilesDir =>
        Path.Combine(AppContext.BaseDirectory, "butler-fixture", "tools", "butler", "linux-x64");

    private static bool FixturePresent =>
        OperatingSystem.IsLinux() && File.Exists(Path.Combine(RealFilesDir, "butler"));

    private static readonly string[] LinuxFiles = ["butler", "7z.so", "libc7zip.so"];

    private sealed class TempPaths : IAppPaths, IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "st-butler-ensure-" + Guid.NewGuid().ToString("N"));
        public TempPaths() => Directory.CreateDirectory(Root);
        public string ConfigDir => Root;
        public string StateDir => Root;
        public string CacheDir => Path.Combine(Root, "cache");
        public string LogDir => Root;
        public string ShareDir => Path.Combine(Root, "share");
        public string ConfigFilePath => Path.Combine(Root, "launcher_config.json");
        public string NewsCacheFilePath => Path.Combine(Root, "news-cache.json");
        public string ClientInstallDir(int build) => Path.Combine(Root, $"c{build}");
        public string ClientDownloadZip(int build) => Path.Combine(Root, $"c{build}.zip");
        public void EnsureDirectories() { Directory.CreateDirectory(CacheDir); Directory.CreateDirectory(ShareDir); }
        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ } }
    }

    /// <summary>A fake CDN that serves real bytes read off disk for named files, and genuinely wrong
    /// bytes for files listed in <see cref="CorruptFiles"/> — no scripted pass/fail, every hash check
    /// downstream (in <see cref="ButlerSidecar"/> AND in this fake's own <see cref="VerifyHashAsync"/>)
    /// runs against real bytes on disk.</summary>
    private sealed class FakeCdn : IDownloadService
    {
        public required Dictionary<string, string> ServeFromDisk { get; init; }
        public HashSet<string> CorruptFiles { get; } = [];
        public HashSet<string> FailFiles { get; } = [];
        public List<string> RequestedUrls { get; } = [];

        public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            RequestedUrls.Add(url);
            var name = Path.GetFileName(destPath);
            if (FailFiles.Contains(name))
                return Task.FromResult(DownloadResult.Fail(DownloadFailure.ServerError, "simulated CDN failure"));

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            if (CorruptFiles.Contains(name))
                File.WriteAllBytes(destPath, "these are definitely not the right bytes for this file"u8.ToArray());
            else
                File.Copy(ServeFromDisk[name], destPath, overwrite: true);

            return Task.FromResult(DownloadResult.Success);
        }

        public Task<bool> VerifyHashAsync(string path, string expectedSha256, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(expectedSha256) || !File.Exists(path)) return Task.FromResult(false);
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            return Task.FromResult(string.Equals(actual, expectedSha256, StringComparison.Ordinal));
        }

        public Task<bool> ExtractZipAsync(string zipPath, string destDir, IProgress<string>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(true);
        public Task<bool> ExtractClientAsync(string zipPath, string destDir, IProgress<string>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(true);
        public Task<ExtractOutcome> ExtractClientWithReasonAsync(string zipPath, string destDir, bool freshInstall,
            IProgress<string>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(ExtractOutcome.Fail(ExtractFailure.Unknown));
    }

    private static FakeCdn NewCdnServingRealBytes() => new()
    {
        ServeFromDisk = new Dictionary<string, string>
        {
            ["butler"] = Path.Combine(RealFilesDir, "butler"),
            ["7z.so"] = Path.Combine(RealFilesDir, "7z.so"),
            ["libc7zip.so"] = Path.Combine(RealFilesDir, "libc7zip.so"),
        },
    };

    [SkippableFact]
    public async Task Correct_bytes_land_at_location_2_and_become_available()
    {
        Skip.IfNot(FixturePresent, "butler binary fixture not present for this OS/arch — see WowLauncher.Tests.csproj");

        using var paths = new TempPaths();
        var emptyBundledDir = PatchingFakes.NewTempDir(); // no tools/butler/ here at all — location 1 absent
        var cdn = NewCdnServingRealBytes();
        var sidecar = new ButlerSidecar(emptyBundledDir, cdn, paths, new Serilog.LoggerConfiguration().CreateLogger());

        Assert.False(sidecar.IsAvailable);

        var ok = await sidecar.EnsureAvailableAsync(CancellationToken.None);

        Assert.True(ok);
        Assert.True(sidecar.IsAvailable);
        Assert.Equal(3, cdn.RequestedUrls.Count);

        var landedDir = Path.Combine(paths.ShareDir, "tools", "butler", ButlerSidecar.Version, "linux-x64");
        foreach (var name in LinuxFiles)
            Assert.True(File.Exists(Path.Combine(landedDir, name)), $"{name} did not land at {landedDir}");

        // Fetched executable must actually be runnable, not just present — the whole point of the
        // chmod step (ARCHITEKTUR-v2-patcher.md §5, Teil A step 2).
        var mode = File.GetUnixFileMode(Path.Combine(landedDir, "butler"));
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute), "fetched butler binary is not marked executable");
    }

    [SkippableFact]
    public async Task Wrong_bytes_leave_it_unavailable_and_nothing_lands_at_the_target()
    {
        Skip.IfNot(FixturePresent, "butler binary fixture not present for this OS/arch");

        using var paths = new TempPaths();
        var emptyBundledDir = PatchingFakes.NewTempDir();
        var cdn = NewCdnServingRealBytes();
        cdn.CorruptFiles.Add("butler"); // first file in the pin list — the loop must stop right here
        var sidecar = new ButlerSidecar(emptyBundledDir, cdn, paths, new Serilog.LoggerConfiguration().CreateLogger());

        var ok = await sidecar.EnsureAvailableAsync(CancellationToken.None);

        Assert.False(ok);
        Assert.False(sidecar.IsAvailable);
        var landedDir = Path.Combine(paths.ShareDir, "tools", "butler", ButlerSidecar.Version, "linux-x64");
        Assert.False(Directory.Exists(landedDir), "a failed fetch must leave nothing at the target location");
    }

    [SkippableFact]
    public async Task A_failing_download_leaves_it_unavailable_and_nothing_lands_at_the_target()
    {
        Skip.IfNot(FixturePresent, "butler binary fixture not present for this OS/arch");

        using var paths = new TempPaths();
        var emptyBundledDir = PatchingFakes.NewTempDir();
        var cdn = NewCdnServingRealBytes();
        cdn.FailFiles.Add("7z.so"); // second file — proves a mid-sequence network failure is caught too
        var sidecar = new ButlerSidecar(emptyBundledDir, cdn, paths, new Serilog.LoggerConfiguration().CreateLogger());

        var ok = await sidecar.EnsureAvailableAsync(CancellationToken.None);

        Assert.False(ok);
        Assert.False(sidecar.IsAvailable);
        var landedDir = Path.Combine(paths.ShareDir, "tools", "butler", ButlerSidecar.Version, "linux-x64");
        Assert.False(Directory.Exists(landedDir));
    }

    [SkippableFact]
    public async Task Location_1_wins_and_EnsureAvailableAsync_never_touches_the_network_when_it_already_verifies()
    {
        Skip.IfNot(FixturePresent, "butler binary fixture not present for this OS/arch");

        var bundledBase = PatchingFakes.NewTempDir();
        var bundledDir = Path.Combine(bundledBase, "tools", "butler", "linux-x64");
        Directory.CreateDirectory(bundledDir);
        foreach (var name in LinuxFiles)
            File.Copy(Path.Combine(RealFilesDir, name), Path.Combine(bundledDir, name));

        using var paths = new TempPaths();
        // A CDN that would fail loudly if ever asked — proves ResolveValidDir checks (1) BEFORE (2)
        // and EnsureAvailableAsync short-circuits on IsAvailable without ever calling it.
        var cdn = NewCdnServingRealBytes();
        cdn.CorruptFiles.UnionWith(LinuxFiles);
        var sidecar = new ButlerSidecar(bundledBase, cdn, paths, new Serilog.LoggerConfiguration().CreateLogger());

        Assert.True(sidecar.IsAvailable);

        var ok = await sidecar.EnsureAvailableAsync(CancellationToken.None);

        Assert.True(ok);
        Assert.Empty(cdn.RequestedUrls);
    }

    [SkippableFact]
    public async Task A_previously_fetched_location_2_also_short_circuits_without_a_new_fetch()
    {
        Skip.IfNot(FixturePresent, "butler binary fixture not present for this OS/arch");

        using var paths = new TempPaths();
        var landedDir = Path.Combine(paths.ShareDir, "tools", "butler", ButlerSidecar.Version, "linux-x64");
        Directory.CreateDirectory(landedDir);
        foreach (var name in LinuxFiles)
            File.Copy(Path.Combine(RealFilesDir, name), Path.Combine(landedDir, name));

        var emptyBundledDir = PatchingFakes.NewTempDir();
        var cdn = NewCdnServingRealBytes();
        cdn.CorruptFiles.UnionWith(LinuxFiles);
        var sidecar = new ButlerSidecar(emptyBundledDir, cdn, paths, new Serilog.LoggerConfiguration().CreateLogger());

        Assert.True(sidecar.IsAvailable);

        var ok = await sidecar.EnsureAvailableAsync(CancellationToken.None);

        Assert.True(ok);
        Assert.Empty(cdn.RequestedUrls);
    }

    [SkippableFact]
    public async Task The_fetch_goes_to_the_launchers_own_origin_not_a_fixed_host()
    {
        // Until 2026-09-23 the fetch URL was a constant on the stable CDN, so a beta build or a
        // STONETAVERN_DOWNLOAD_BASE test run fetched from production and the path was never testable
        // before release (the Windows E2E found every file 404 there).
        Skip.IfNot(FixturePresent, "butler binary fixture not present for this OS/arch");

        using var paths = new TempPaths();
        var cdn = NewCdnServingRealBytes();
        var sidecar = new ButlerSidecar(PatchingFakes.NewTempDir(), cdn, paths,
            new Serilog.LoggerConfiguration().CreateLogger(), origin: "http://127.0.0.1:18677/");

        Assert.True(await sidecar.EnsureAvailableAsync(CancellationToken.None));
        Assert.Equal(3, cdn.RequestedUrls.Count);
        Assert.All(cdn.RequestedUrls, url =>
            Assert.StartsWith($"http://127.0.0.1:18677/tools/butler/{ButlerSidecar.Version}/linux-x64/", url));
    }

    [SkippableFact]
    public async Task Without_an_origin_the_fetch_stays_on_the_stable_cdn()
    {
        Skip.IfNot(FixturePresent, "butler binary fixture not present for this OS/arch");

        using var paths = new TempPaths();
        var cdn = NewCdnServingRealBytes();
        var sidecar = new ButlerSidecar(PatchingFakes.NewTempDir(), cdn, paths,
            new Serilog.LoggerConfiguration().CreateLogger());

        Assert.True(await sidecar.EnsureAvailableAsync(CancellationToken.None));
        Assert.All(cdn.RequestedUrls, url =>
            Assert.StartsWith($"https://downloads.stonetavern.app/tools/butler/{ButlerSidecar.Version}/", url));
    }
}
