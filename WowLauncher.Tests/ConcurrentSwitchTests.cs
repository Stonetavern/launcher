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
/// What happens when the player clicks a different expansion tile in the middle of a multi-GB
/// download. The v3 shell binds the picker straight to the property setter, so the click did NOT go
/// through the one command that carried a busy guard.
///
/// <para>The damage the old code did, in order: <c>ApplyExpansionAsync</c> overwrote
/// <c>_activePhase</c>, <c>_downloadUrl</c> and <c>_downloadSha256</c> while the download was parked
/// on its await. The finished archive was then verified against the NEW hash, so several GB were
/// deleted with a "checksum wrong" line. If the hash happened to match, the archive was extracted into
/// the directory of a build it was never downloaded for, and that path was registered in the config as
/// that build's install.</para>
///
/// <para>Two guarantees are pinned here: the era cannot move while an operation owns it, and the
/// operation reads the values it started with even if something else moves them.</para>
/// </summary>
public sealed class ConcurrentSwitchTests
{
    private const string VanillaUrl = "https://downloads.example.invalid/client-5875.zip";
    private const string VanillaSha = "1111111111111111111111111111111111111111111111111111111111111111";

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
        private readonly ServerManifest _m = m;

        public Task<ServerManifest?> FetchAsync(CancellationToken ct = default) => Task.FromResult<ServerManifest?>(_m);
        /// <summary>Kein Launcher-Manifest in diesem Double: der Selbst-Update-Pfad ist hier
        /// nicht der Prüfgegenstand, und "keins" heißt "kein Update", nie "irgendeins".</summary>
        public Task<ServerManifest?> FetchLauncherManifestAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(null);

        public Task<ClientFileManifest?> FetchFileManifestAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<ClientFileManifest?>(null);
    }

    /// <summary>A manifest the test can republish mid-run: the server rolling out a new client build
    /// while a transfer is parked is the exact race the capture-once rule exists for.</summary>
    private sealed class MutableManifest(ServerManifest m) : IManifestService
    {
        public ServerManifest? Current = m;

        public Task<ServerManifest?> FetchAsync(CancellationToken ct = default) => Task.FromResult(Current);
        /// <summary>Kein Launcher-Manifest in diesem Double: der Selbst-Update-Pfad ist hier
        /// nicht der Prüfgegenstand, und "keins" heißt "kein Update", nie "irgendeins".</summary>
        public Task<ServerManifest?> FetchLauncherManifestAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(null);

        public Task<ClientFileManifest?> FetchFileManifestAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<ClientFileManifest?>(null);
    }

    /// <summary>Finds nothing before the download and the freshly extracted exe afterwards, so a run
    /// can get all the way to "install registered in the config".</summary>
    private sealed class InstallingClient : IClientService
    {
        public string? FindWowExe(string? configuredPath = null) =>
            string.IsNullOrEmpty(configuredPath) ? null : Path.Combine(configuredPath, "WoW.exe");
        public string? FindWowExeForBuild(int gameBuild, IReadOnlyDictionary<int, string> installs) => null;
        public IReadOnlyDictionary<int, string> DetectInstalls(IReadOnlyDictionary<int, string> known) =>
            new Dictionary<int, string>();
        public int? DetectBuild(string wowDirectory) => null;
        public bool IsGameRunning() => false;
        public void SetRealmlist(string wowDirectory, string realmlistAddress) { }
        public void ConfigureClient(string wowDirectory, string locale, string realmlistAddress) { }
        public Task<GameLaunchResult> LaunchAsync(string wowExePath) =>
            Task.FromResult(new GameLaunchResult(false, null, "test"));
    }

    private sealed class NoClient : IClientService
    {
        public string? FindWowExe(string? configuredPath = null) => null;
        public string? FindWowExeForBuild(int gameBuild, IReadOnlyDictionary<int, string> installs) => null;
        public IReadOnlyDictionary<int, string> DetectInstalls(IReadOnlyDictionary<int, string> known) =>
            new Dictionary<int, string>();
        public int? DetectBuild(string wowDirectory) => null;
        public bool IsGameRunning() => false;
        public void SetRealmlist(string wowDirectory, string realmlistAddress) { }
        public void ConfigureClient(string wowDirectory, string locale, string realmlistAddress) { }
        public Task<GameLaunchResult> LaunchAsync(string wowExePath) =>
            Task.FromResult(new GameLaunchResult(false, null, "test"));
    }

    private sealed class OfflineStatus : IServerStatusService
    {
        public Task<ServerStatusResult> CheckAsync(string host, int port = 3724, CancellationToken ct = default) =>
            Task.FromResult(new ServerStatusResult { Online = false, PlayerCount = 0 });
    }

    /// <summary>Parks inside DownloadFileAsync until the test releases it, and records exactly which
    /// url and which expected hash the ViewModel handed over.</summary>
    private sealed class GatedDownload : IDownloadService
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? RequestedUrl;
        public string? VerifiedAgainst;
        public string? ExtractedTo;

        /// <summary>How many transfers were actually started. "The state still says Downloading" does
        /// not answer that question; only counting the calls does.</summary>
        public int DownloadCalls;

        /// <summary>Off by default (the run ends at the transfer, nothing to fake on disk). Turned on
        /// for the run that has to reach verify, extract and the config write.</summary>
        public bool RunToCompletion;

        public async Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref DownloadCalls);
            RequestedUrl ??= url;   // the FIRST transfer is the one under test
            Started.TrySetResult();
            await Release.Task;
            return RunToCompletion ? DownloadResult.Success : DownloadResult.Fail(DownloadFailure.Network);
        }

        public Task<bool> ExtractZipAsync(string z, string d, IProgress<string>? p = null, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> ExtractClientAsync(string z, string d, IProgress<string>? p = null, CancellationToken ct = default)
        {
            ExtractedTo = d;
            return Task.FromResult(RunToCompletion);
        }
        public Task<bool> VerifyHashAsync(string path, string expectedSha256, CancellationToken ct = default)
        {
            VerifiedAgainst = expectedSha256;
            return Task.FromResult(RunToCompletion);
        }
    }

    private sealed class NoUpdate : IUpdateService
    {
        // Never raised here: these doubles model "there is no update", so nothing announces one.
        public event System.EventHandler? LauncherUpdateStarting { add { } remove { } }

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
        private readonly string _root = Path.Combine(Path.GetTempPath(), "st-switch-" + Guid.NewGuid().ToString("N"));
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

    private static ServerManifest Manifest() => new()
    {
        CurrentVersion = "1.0.0",
        ActivePhase = "vanilla",
        Phases =
        [
            new PhaseManifest
            {
                Phase = "vanilla",
                Realmlist = "play.stonetavern.app",
                Client = new ManifestFile { Version = "1.0.0", Url = VanillaUrl, Sha256 = VanillaSha },
            },
        ],
    };

    // The same phase after the server published a new build: different url, different hash, different
    // version. Every one of the three is a value the download captured before it started.
    private const string RepublishedUrl = "https://downloads.example.invalid/client-5875-v2.zip";
    private const string RepublishedSha = "2222222222222222222222222222222222222222222222222222222222222222";

    private static ServerManifest Republished() => new()
    {
        CurrentVersion = "2.0.0",
        ActivePhase = "vanilla",
        Phases =
        [
            new PhaseManifest
            {
                Phase = "vanilla",
                Realmlist = "play.stonetavern.app",
                Client = new ManifestFile { Version = "2.0.0", Url = RepublishedUrl, Sha256 = RepublishedSha },
            },
        ],
    };

    private static (PlayViewModel vm, GatedDownload dl) NewPlay()
    {
        var dl = new GatedDownload();
        var log = new Serilog.LoggerConfiguration().CreateLogger();
        var paths = new TempPaths();
        var vm = new PlayViewModel(new MemoryConfig(), new FixedManifest(Manifest()), new NoClient(),
            new OfflineStatus(), dl, new ClientVerifyService(log), new NoUpdate(), new NoNews(),
            new ExitNow(), paths, new FixedFolderPicker(paths.Root), log);
        return (vm, dl);
    }

    // ── Tests ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SwitchingExpansionDuringADownload_IsRefused()
    {
        var (vm, dl) = NewPlay();
        await vm.InitAsync();
        Assert.Equal(LauncherState.NoClient, vm.State);   // no client anywhere -> PLAY means download

        var play = vm.PlayCommand.ExecuteAsync(null);
        await dl.Started.Task;                            // the download now owns the era
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanSwitchContext);

        var before = vm.SelectedExpansion;
        vm.SelectedExpansion = Expansion.ById("wotlk");    // the click the v3 picker performs

        Assert.Same(before, vm.SelectedExpansion);
        Assert.Equal(LauncherState.Downloading, vm.State);

        dl.Release.TrySetResult();
        await play;
    }

    [Fact]
    public async Task ASwitchAttemptDuringADownload_CannotRepointTheRunningTransfer()
    {
        var (vm, dl) = NewPlay();
        await vm.InitAsync();

        var play = vm.PlayCommand.ExecuteAsync(null);
        await dl.Started.Task;

        vm.SelectedExpansion = Expansion.ById("wotlk");
        dl.Release.TrySetResult();
        await play;

        // The transfer that ran was the one that was started, and nothing else.
        Assert.Equal(VanillaUrl, dl.RequestedUrl);
    }

    [Fact]
    public async Task ARealmSwitchDuringADownload_DoesNotResetThePlaySurface()
    {
        var (vm, dl) = NewPlay();
        await vm.InitAsync();

        var play = vm.PlayCommand.ExecuteAsync(null);
        await dl.Started.Task;

        // What the realm rail triggers. It used to set State = Initializing on top of a live download,
        // so the progress UI vanished while the transfer kept running.
        await vm.SwitchRealmAsync();

        Assert.Equal(LauncherState.Downloading, vm.State);

        dl.Release.TrySetResult();
        await play;
    }

    [Fact]
    public async Task AfterTheDownloadEnds_TheExpansionPickerWorksAgain()
    {
        var (vm, dl) = NewPlay();
        await vm.InitAsync();

        var play = vm.PlayCommand.ExecuteAsync(null);
        await dl.Started.Task;
        dl.Release.TrySetResult();
        await play;

        Assert.True(vm.CanSwitchContext);
        vm.SelectedExpansion = Expansion.ById("wotlk");
        Assert.Equal("wotlk", vm.SelectedExpansion.Id);
    }

    [Fact]
    public async Task ASecondPlayPressDuringADownload_DoesNotStartASecondTransfer()
    {
        var (vm, dl) = NewPlay();
        await vm.InitAsync();

        var play = vm.PlayCommand.ExecuteAsync(null);
        await dl.Started.Task;

        // Bypass the command's own re-entrancy guard the way any programmatic caller would.
        await vm.PlayCommand.ExecuteAsync(null);

        Assert.Equal(LauncherState.Downloading, vm.State);
        dl.Release.TrySetResult();
        await play;
    }

    /// <summary>
    /// The busy guard inside the download path itself, measured where it can be measured: how many
    /// transfers were started.
    ///
    /// <para>A second PLAY press lands in the launch path (the state is Downloading), so it never
    /// reached this guard. The presses that DO reach it are the ones the surface really offers while a
    /// transfer runs or has just failed: UPDATE and REPAIR. Two runs over one destination file write the
    /// same .part and the same zip, and the second File.Move lands on top of the first.</para>
    ///
    /// <para>The assertion runs while the first transfer is still parked on purpose: without the guard
    /// the extra runs would be sitting in DownloadFileAsync at that moment, so awaiting them first would
    /// deadlock instead of failing.</para>
    /// </summary>
    [Fact]
    public async Task AnUpdateOrRepairPressDuringADownload_StartsNoSecondTransfer()
    {
        var (vm, dl) = NewPlay();
        await vm.InitAsync();

        var play = vm.PlayCommand.ExecuteAsync(null);
        await dl.Started.Task;
        Assert.Equal(1, dl.DownloadCalls);

        var update = vm.UpdateCommand.ExecuteAsync(null);
        var repair = vm.RepairCommand.ExecuteAsync(null);
        await Task.Delay(100);

        Assert.Equal(1, dl.DownloadCalls);

        dl.Release.TrySetResult();
        await play;
        await update;
        await repair;
    }

    /// <summary>
    /// The capture-once rule, asserted on the values that actually decide what happens to several GB.
    ///
    /// <para>Mid-transfer the server republishes the build (new url, new hash, new version) and the
    /// player hits "check for updates". That path re-applies the expansion WITHOUT the busy guard, so it
    /// moves <c>_downloadSha256</c>, <c>_clientVersion</c> and <c>_downloadUrl</c> under the running
    /// transfer, and its state reset briefly unfreezes the picker, so the era moves too.</para>
    ///
    /// <para>What must survive all of that: the finished archive is checked against the hash it was
    /// downloaded for, and the install is registered under the build it was downloaded for, with the
    /// version that build was published as. Reading the live fields instead means several GB fail their
    /// checksum and get deleted, or land in another era's install slot.</para>
    /// </summary>
    [Fact]
    public async Task AFinishedDownload_IsVerifiedAndRegisteredAgainstWhatItStartedWith()
    {
        var manifest = new MutableManifest(Manifest());
        var cfg = new MemoryConfig();
        var dl = new GatedDownload { RunToCompletion = true };
        var log = new Serilog.LoggerConfiguration().CreateLogger();
        var paths = new TempPaths();
        var vm = new PlayViewModel(cfg, manifest, new InstallingClient(), new OfflineStatus(), dl,
            new ClientVerifyService(log), new NoUpdate(), new NoNews(), new ExitNow(), paths,
            new FixedFolderPicker(paths.Root), log);

        await vm.InitAsync();
        Assert.Equal(LauncherState.NoClient, vm.State);

        var play = vm.PlayCommand.ExecuteAsync(null);
        await dl.Started.Task;

        manifest.Current = Republished();
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        vm.SelectedExpansion = Expansion.ById("wotlk");
        await Task.Delay(50);

        dl.Release.TrySetResult();
        await play;

        Assert.Equal(VanillaUrl, dl.RequestedUrl);
        Assert.Equal(VanillaSha, dl.VerifiedAgainst);

        var vanillaBuild = Expansion.ById("vanilla").GameBuild;
        var wotlkBuild = Expansion.ById("wotlk").GameBuild;
        Assert.Contains(BuildMarker(vanillaBuild), dl.ExtractedTo ?? "", StringComparison.Ordinal);
        Assert.True(cfg.Current.ClientInstalls.ContainsKey(vanillaBuild),
            "The install was registered under an era the player moved to mid-download.");
        Assert.False(cfg.Current.ClientInstalls.ContainsKey(wotlkBuild));
        Assert.Equal("1.0.0", cfg.Current.InstalledClientVersions[vanillaBuild]);
    }

    /// <summary>The build-specific fragment every client path carries, so "which build did this land
    /// under" can be read off the extract directory without knowing the temp root.</summary>
    private static string BuildMarker(int gameBuild) => $"WoW-Client-{gameBuild}";
}
