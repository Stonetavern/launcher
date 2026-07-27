using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using WowLauncher.ViewModels;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The first-install folder picker: DownloadAsync must ask the player where a NEW client build goes,
/// exactly once per player (not once per build), and must NEVER ask for Repair or Update. A dialog that
/// fires during Repair, Update, or the QA screenshot harness is a regression, not a feature.
/// </summary>
public sealed class InstallFolderPickerTests
{
    private const string Build5875Url = "https://downloads.example.invalid/client-5875.zip";
    private const string Build5875Sha = "3333333333333333333333333333333333333333333333333333333333333333";

    // ── Test doubles ────────────────────────────────────────────────────────────────────────────

    private sealed class MemoryConfig : IConfigService
    {
        public LauncherConfig Current = new()
        {
            ManifestUrl = "https://downloads.example.invalid/manifest.json",
            RealmlistAddress = "play.stonetavern.app",
        };
        public LauncherConfig Load() => Current;
        public void Save(LauncherConfig config) => Current = config;
        public bool LastSaveSucceeded => true;
    }

    private sealed class FixedManifest(ServerManifest m) : IManifestService
    {
        public Task<ServerManifest?> FetchAsync(CancellationToken ct = default) => Task.FromResult<ServerManifest?>(m);
        public Task<ClientFileManifest?> FetchFileManifestAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<ClientFileManifest?>(null);
    }

    /// <summary>Finds nothing before install, the freshly extracted exe after — enough to reach
    /// "install registered" without a real archive on disk.</summary>
    private sealed class FakeClient : IClientService
    {
        public string? FindWowExe(string? configuredPath = null) =>
            string.IsNullOrEmpty(configuredPath) ? null : Path.Combine(configuredPath, "WoW.exe");
        public string? FindWowExeForBuild(int gameBuild, IReadOnlyDictionary<int, string> installs) => null;
        public IReadOnlyDictionary<int, string> DetectInstalls(IReadOnlyDictionary<int, string> known) => new Dictionary<int, string>();
        public int? DetectBuild(string wowDirectory) => null;
        public bool IsGameRunning() => false;
        public void SetRealmlist(string wowDirectory, string realmlistAddress) { }
        public void ConfigureClient(string wowDirectory, string locale, string realmlistAddress) { }
        public Task<GameLaunchResult> LaunchAsync(string wowExePath) => Task.FromResult(new GameLaunchResult(false, null, "test"));
    }

    private sealed class OfflineStatus : IServerStatusService
    {
        public Task<ServerStatusResult> CheckAsync(string host, int port = 3724, CancellationToken ct = default) =>
            Task.FromResult(new ServerStatusResult { Online = false, PlayerCount = 0 });
    }

    /// <summary>Always "succeeds" instantly, no real bytes on disk — records what it was called with and
    /// how many times, so a test can assert a download never started at all.</summary>
    private sealed class FakeDownload : IDownloadService
    {
        public int DownloadCalls;
        public string? ExtractedTo;

        public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref DownloadCalls);
            return Task.FromResult(DownloadResult.Success);
        }

        public Task<bool> ExtractZipAsync(string z, string d, IProgress<string>? p = null, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> ExtractClientAsync(string z, string d, IProgress<string>? p = null, CancellationToken ct = default)
        {
            ExtractedTo = d;
            return Task.FromResult(true);
        }
        public Task<bool> VerifyHashAsync(string path, string expectedSha256, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class NoUpdate : IUpdateService
    {
        public Task<bool> CheckAndApplyAsync(ServerManifest? manifest, CancellationToken ct = default) => Task.FromResult(false);
        public LauncherUpdateNotice? CheckForNotice(ServerManifest? manifest) => null;
    }

    private sealed class NoNews : INewsService
    {
        public Task<IReadOnlyList<NewsItem>> GetNewsAsync(bool forceRefresh = false, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NewsItem>>([]);
    }

    private sealed class ExitNow : ILaunchExitPolicy
    {
        public Task<bool> ConfirmClientRunningAsync(string exePath) => Task.FromResult(false);
    }

    private sealed class TempPaths : IAppPaths
    {
        public string Root => _root;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "st-picker-" + Guid.NewGuid().ToString("N"));
        public string ConfigDir => _root;
        public string StateDir => _root;
        public string CacheDir => _root;
        public string LogDir => _root;
        public string ShareDir => _root;
        public string ConfigFilePath => Path.Combine(_root, "launcher_config.json");
        public string NewsCacheFilePath => Path.Combine(_root, "news-cache.json");
        public string ClientInstallDir(int gameBuild) => Path.Combine(_root, $"WoW-Client-{gameBuild}");
        public string ClientDownloadZip(int gameBuild) => Path.Combine(_root, $"WoW-Client-{gameBuild}.zip");
        public void EnsureDirectories() => Directory.CreateDirectory(_root);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    private static ServerManifest Manifest(long size = 0) => new()
    {
        CurrentVersion = "1.0.0",
        ActivePhase = "vanilla",
        Phases =
        [
            new PhaseManifest
            {
                Phase = "vanilla",
                Realmlist = "play.stonetavern.app",
                Client = new ManifestFile { Version = "1.0.0", Url = Build5875Url, Sha256 = Build5875Sha, Size = size },
            },
        ],
    };

    private static (PlayViewModel vm, MemoryConfig cfg, FakeDownload dl, FixedFolderPicker picker, TempPaths paths) NewPlay(
        string? pickerAnswer, long manifestSize = 0)
    {
        var cfg = new MemoryConfig();
        var dl = new FakeDownload();
        var picker = new FixedFolderPicker(pickerAnswer);
        var paths = new TempPaths();
        var log = new Serilog.LoggerConfiguration().CreateLogger();
        var vm = new PlayViewModel(cfg, new FixedManifest(Manifest(manifestSize)), new FakeClient(),
            new OfflineStatus(), dl, new ClientVerifyService(log), new NoUpdate(), new NoNews(),
            new ExitNow(), paths, picker, log);
        return (vm, cfg, dl, picker, paths);
    }

    // ── Tests ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PickerCancelled_NoDownloadStarted_StateAndConfigUnchanged()
    {
        var (vm, cfg, dl, picker, _) = NewPlay(pickerAnswer: null);
        await vm.InitAsync();
        var stateBefore = vm.State;

        await vm.PlayCommand.ExecuteAsync(null);

        Assert.Single(picker.Calls);
        Assert.Equal(0, dl.DownloadCalls);
        Assert.Equal(stateBefore, vm.State);
        Assert.Null(cfg.Current.PreferredInstallRoot);
    }

    [Fact]
    public async Task PickerPicks_PersistsPreferredInstallRoot_AndExtractsUnderIt()
    {
        var chosen = Path.Combine(Path.GetTempPath(), "st-chosen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(chosen);
        try
        {
            var (vm, cfg, dl, picker, _) = NewPlay(pickerAnswer: chosen);
            await vm.InitAsync();

            await vm.PlayCommand.ExecuteAsync(null);

            Assert.Single(picker.Calls);
            Assert.Equal(chosen, cfg.Current.PreferredInstallRoot);
            Assert.Equal(LauncherState.Ready, vm.State);
            Assert.Equal(Path.Combine(chosen, "WoW-Client-5875"), dl.ExtractedTo);
        }
        finally { try { Directory.Delete(chosen, recursive: true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task PreferredRootAlreadySet_SkipsDialog_ReusesRoot()
    {
        var chosen = Path.Combine(Path.GetTempPath(), "st-reused-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(chosen);
        try
        {
            // NullFolderPicker would fail this test the moment it is actually asked - proving the
            // dialog really is skipped, not just "happens to answer the same thing".
            var cfg = new MemoryConfig { Current = { PreferredInstallRoot = chosen } };
            var dl = new FakeDownload();
            var picker = new NullFolderPicker();
            var paths = new TempPaths();
            var log = new Serilog.LoggerConfiguration().CreateLogger();
            var vm = new PlayViewModel(cfg, new FixedManifest(Manifest()), new FakeClient(),
                new OfflineStatus(), dl, new ClientVerifyService(log), new NoUpdate(), new NoNews(),
                new ExitNow(), paths, picker, log);
            await vm.InitAsync();

            await vm.PlayCommand.ExecuteAsync(null);

            Assert.Equal(0, picker.CallCount);
            Assert.Equal(LauncherState.Ready, vm.State);
            Assert.Equal(Path.Combine(chosen, "WoW-Client-5875"), dl.ExtractedTo);
        }
        finally { try { Directory.Delete(chosen, recursive: true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task Repair_NeverOpensDialog()
    {
        var (vm, cfg, dl, picker, paths) = NewPlay(pickerAnswer: null);
        await vm.InitAsync();

        // Get to a state where Repair is legal without going through the picker path: pre-register the
        // install directly, the way a completed download would have.
        var installDir = Path.Combine(paths.Root, "WoW-Client-5875");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "WoW.exe"), "");
        cfg.Current.ClientInstalls[5875] = installDir;
        vm.State = LauncherState.Ready;

        Assert.True(vm.CanRepair);
        await vm.RepairCommand.ExecuteAsync(null);

        Assert.Empty(picker.Calls);
        Assert.Equal(1, dl.DownloadCalls);
        Assert.Equal(installDir, dl.ExtractedTo);
    }

    [Fact]
    public async Task Update_NeverOpensDialog()
    {
        var (vm, cfg, dl, picker, paths) = NewPlay(pickerAnswer: null);
        await vm.InitAsync();

        var installDir = Path.Combine(paths.Root, "WoW-Client-5875");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "WoW.exe"), "");
        cfg.Current.ClientInstalls[5875] = installDir;
        vm.State = LauncherState.UpdateAvailable;

        Assert.True(vm.UpdateCommand.CanExecute(null));
        await vm.UpdateCommand.ExecuteAsync(null);

        Assert.Empty(picker.Calls);
        Assert.Equal(1, dl.DownloadCalls);
        Assert.Equal(installDir, dl.ExtractedTo);
    }

    [Fact]
    public async Task NotEnoughDiskSpace_NoDownloadStarted_ErrorSet()
    {
        var chosen = Path.Combine(Path.GetTempPath(), "st-nospace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(chosen);
        try
        {
            // A declared size no real test machine has free (4 TB, times the launcher's conservative
            // extracted-size multiplier): deterministic without faking DriveInfo itself.
            var (vm, cfg, dl, picker, _) = NewPlay(pickerAnswer: chosen, manifestSize: 4_000_000_000_000L);
            await vm.InitAsync();

            await vm.PlayCommand.ExecuteAsync(null);

            Assert.Single(picker.Calls);                // the folder WAS chosen
            Assert.Equal(0, dl.DownloadCalls);          // but the transfer never started
            Assert.Equal(LauncherState.DownloadError, vm.State);
            Assert.False(string.IsNullOrEmpty(vm.DownloadErrorDetail));
        }
        finally { try { Directory.Delete(chosen, recursive: true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task AvaloniaFolderPickerService_NoApplicationOrTopLevel_ReturnsNull_NoThrow()
    {
        // The xUnit host never spins up an Avalonia Application, so Application.Current is null here -
        // exactly the "no TopLevel available" case the headless/screenshot harness hits. The service
        // must fall through to null instead of throwing on the missing ApplicationLifetime.
        var log = new Serilog.LoggerConfiguration().CreateLogger();
        var svc = new AvaloniaFolderPickerService(log);

        var result = await svc.PickFolderAsync("Choose a folder");

        Assert.Null(result);
    }
}
