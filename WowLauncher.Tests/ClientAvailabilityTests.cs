using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using WowLauncher.ViewModels;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Only two client builds actually exist (Vanilla 1.12.1 and 1.14.2 Classic Era). Burning Crusade and
/// Wrath were sitting in the picker as normal, clickable tiles that led nowhere. This pins the model
/// (<see cref="ClientChoice"/>/<see cref="ClientVersion.IsAvailable"/>), the view-model click guard
/// (<see cref="PlayViewModel.SelectedClientChoice"/>), and the realm-side quiet-failure path
/// (<see cref="RealmEntry.ClientAvailable"/>).
/// </summary>
public sealed class ClientAvailabilityTests
{
    // ── Model-level: no PlayViewModel needed ────────────────────────────────────────────────────

    [Fact]
    public void Vanilla_Offers_Exactly_Two_Versions_Both_Selectable()
    {
        var vanilla = ClientChoice.All.Where(c => c.Client.EraId == "vanilla").ToList();

        Assert.Equal(2, vanilla.Count);
        Assert.Contains(vanilla, c => c.Client.Key == "1.12.1");
        Assert.Contains(vanilla, c => c.Client.Key == "1.14.2");
        Assert.All(vanilla, c => Assert.True(c.IsAvailable));
    }

    [Fact]
    public void BurningCrusade_And_Wrath_Are_Visible_But_Not_Available()
    {
        var choices = ClientChoice.All;

        var tbc = Assert.Single(choices, c => c.Client.EraId == "tbc");
        var wotlk = Assert.Single(choices, c => c.Client.EraId == "wotlk");

        // Visible: they are in the same list the picker binds to, not filtered out.
        Assert.Contains(tbc, choices);
        Assert.Contains(wotlk, choices);
        // Not selectable: the one flag the UI, the setter guard and the disabled style all read.
        Assert.False(tbc.IsAvailable);
        Assert.False(wotlk.IsAvailable);
    }

    // ── macOS is 1.14.2 only (owner scope 2026-07-23): the 32-bit legacy builds are hard-excluded ──

    [Fact]
    public void OnMacOs_Only_The_Modern_Build_Is_Not_Excluded()
    {
        // The rule takes the OS as a parameter so it is testable off the current (Linux) host.
        Assert.True(ClientVersion.ByKey("1.12.1").IsExcludedOn(isMacOs: true));   // no path under GPTK-Wine
        Assert.False(ClientVersion.ByKey("1.14.2").IsExcludedOn(isMacOs: true));  // the one Mac build
        Assert.True(ClientVersion.ByKey("2.4.3").IsExcludedOn(isMacOs: true));    // legacy → excluded too
        Assert.True(ClientVersion.ByKey("3.3.5a").IsExcludedOn(isMacOs: true));
    }

    [Fact]
    public void OffMacOs_Nothing_Is_Excluded()
    {
        Assert.All(ClientVersion.All, cv => Assert.False(cv.IsExcludedOn(isMacOs: false)));
    }

    [Fact]
    public void OnCurrentOs_NotMac_The_Picker_And_ForKey_Are_Unchanged()
    {
        // This test host is Linux, so the OS filter is a no-op here — pins that Windows/Linux keep 1.12.1
        // (no regression). The macOS filtering is proven at the rule level by IsExcludedOn above.
        Assert.Equal(ClientChoice.All.Count, ClientChoice.AllForCurrentOs.Count);
        Assert.Equal("1.12.1", ClientChoice.ForKey("1.12.1").Client.Key);
        Assert.Contains(ClientChoice.AllForCurrentOs, c => c.Client.Key == "1.12.1");
    }

    [Fact]
    public void Expansion_IsAvailable_Matches_Its_ClientVersions()
    {
        Assert.True(Expansion.ById("vanilla").IsAvailable);
        Assert.False(Expansion.ById("tbc").IsAvailable);
        Assert.False(Expansion.ById("wotlk").IsAvailable);
    }

    [Fact]
    public void Realm_With_Unavailable_ClientKey_DoesNotCrash_AndReportsUnavailable()
    {
        var realm = new RealmEntry { Id = "future", Name = "Future Realm", ClientKey = "3.3.5a" };

        var ex = Record.Exception(() =>
        {
            _ = realm.Client;
            _ = realm.ClientLine;
            _ = realm.ClientAvailable;
        });

        Assert.Null(ex);
        Assert.Equal("3.3.5a", realm.Client.Key);          // still resolves the named build, not a fallback
        Assert.False(realm.ClientAvailable);               // ...but says calmly that it is not ready
    }

    // ── View-model level: selecting an unavailable client must not move any state ──────────────────

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

    private sealed class NoManifest : IManifestService
    {
        public Task<ServerManifest?> FetchAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(null);
        /// <summary>Kein Launcher-Manifest in diesem Double: der Selbst-Update-Pfad ist hier
        /// nicht der Prüfgegenstand, und "keins" heißt "kein Update", nie "irgendeins".</summary>
        public Task<ServerManifest?> FetchLauncherManifestAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(null);

        public Task<ClientFileManifest?> FetchFileManifestAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<ClientFileManifest?>(null);
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

    private sealed class NoDownload : IDownloadService
    {
        public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(DownloadResult.Fail(DownloadFailure.Network));
        public Task<bool> ExtractZipAsync(string z, string d, IProgress<string>? p = null, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> ExtractClientAsync(string z, string d, IProgress<string>? p = null, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> VerifyHashAsync(string path, string expected, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed class NoUpdate : IUpdateService
    {
        // Never raised here: these doubles model "there is no update", so nothing announces one.
        public event System.EventHandler? LauncherUpdateStarting { add { } remove { } }

        public Task<bool> CheckAndApplyAsync(ServerManifest? m, CancellationToken ct = default) => Task.FromResult(false);
        public LauncherUpdateNotice? CheckForNotice(ServerManifest? m) => null;
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
        private readonly string _root = Path.Combine(Path.GetTempPath(), "st-avail-" + Guid.NewGuid().ToString("N"));
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

    private static PlayViewModel NewPlay()
    {
        var log = new Serilog.LoggerConfiguration().CreateLogger();
        var paths = new TempPaths();
        return new PlayViewModel(new MemoryConfig(), new NoManifest(), new NoClient(), new OfflineStatus(),
            new NoDownload(), new ClientVerifyService(log), new NoUpdate(), new NoNews(), new ExitNow(), paths,
            new FixedFolderPicker(paths.Root), log);
    }

    [Fact]
    public void Selecting_An_Unavailable_Client_Does_Not_Change_The_Selection()
    {
        var play = NewPlay();
        var before = play.SelectedClientChoice;
        Assert.True(before.IsAvailable);   // sanity: we start on a real build (1.12.1, the realm default)

        var wotlk = play.ClientChoices.Single(c => c.Client.EraId == "wotlk");
        play.SelectedClientChoice = wotlk;

        Assert.Same(before, play.SelectedClientChoice);
        Assert.Equal(before.Expansion.Id, play.SelectedExpansion.Id);
    }

    [Fact]
    public void Selecting_An_Available_Client_Does_Change_The_Selection()
    {
        // Counter-proof: the guard above must not simply refuse every pick.
        var play = NewPlay();
        var era1142 = play.ClientChoices.Single(c => c.Client.Key == "1.14.2");

        play.SelectedClientChoice = era1142;

        Assert.Same(era1142, play.SelectedClientChoice);
    }

    // ── Pause / Resume state mapping ─────────────────────────────────────────

    [Fact]
    public void Paused_State_ReadsAsResumable_NotAsError()
    {
        var play = NewPlay();
        play.State = LauncherState.Paused;

        Assert.True(play.IsPaused);
        Assert.False(play.IsError);                 // a pause is not a failure
        Assert.True(play.ActionEnabled);            // the big button is clickable…
        Assert.Equal("RESUME", play.ActionPrimaryText); // …and reads RESUME
        Assert.True(play.ShowProgress);             // the bar stays, frozen at its percent
        Assert.False(play.IsPausable);              // cannot pause an already-paused download
        Assert.True(play.ShowLocate);               // "I already have WoW" still offered
    }

    [Fact]
    public void PauseButton_IsEnabled_OnlyWhileBytesAreFlowing()
    {
        var play = NewPlay();

        play.State = LauncherState.Downloading;
        Assert.True(play.IsPausable);
        Assert.True(play.PauseDownloadCommand.CanExecute(null));

        // Verify is fast + indeterminate → not pausable; and a paused download cannot be paused again.
        play.State = LauncherState.Verifying;
        Assert.False(play.PauseDownloadCommand.CanExecute(null));
        play.State = LauncherState.Paused;
        Assert.False(play.PauseDownloadCommand.CanExecute(null));
    }

    // ── Locate an existing installation ──────────────────────────────────────

    [Fact]
    public async Task Locate_RegistersExistingClient_AndBecomesReady()
    {
        var cfg = new MemoryConfig();
        var play = NewPlayWith(cfg, new LocatableClient(), new FixedFolderPicker("/games/wow"));
        play.State = LauncherState.NoClient;

        await play.LocateExistingClientCommand.ExecuteAsync(null);

        Assert.Equal(LauncherState.Ready, play.State);
        Assert.NotEmpty(cfg.Current.ClientInstalls);          // the install dir was recorded for the build
        Assert.Contains("/games/wow", cfg.Current.ClientInstalls.Values.Single());
    }

    [Fact]
    public async Task Locate_Cancelled_LeavesStateUntouched()
    {
        var cfg = new MemoryConfig();
        var play = NewPlayWith(cfg, new LocatableClient(), new NullFolderPicker()); // picker returns null
        play.State = LauncherState.NoClient;

        await play.LocateExistingClientCommand.ExecuteAsync(null);

        Assert.Equal(LauncherState.NoClient, play.State);      // cancel is not an error
        Assert.Empty(cfg.Current.ClientInstalls);
    }

    [Fact]
    public async Task Locate_RejectsAClientForTheWrongBuild()
    {
        // Active build is 1.12.1 (5875, the realm default). Pointing Locate at a folder whose client is
        // WowClassic.exe (only ever 42597) must be rejected BEFORE registration — never adopt a 1.14
        // client for a 1.12 realm (Codex P0: identity before integrity).
        var cfg = new MemoryConfig();
        var play = NewPlayWith(cfg, new WrongBuildClient(), new FixedFolderPicker("/games/classic-era"));
        play.State = LauncherState.NoClient;

        await play.LocateExistingClientCommand.ExecuteAsync(null);

        Assert.Equal(LauncherState.DownloadError, play.State);
        Assert.Empty(cfg.Current.ClientInstalls);   // nothing registered
    }

    [Fact]
    public async Task Locate_FolderWithoutClient_ReportsNotFound()
    {
        var cfg = new MemoryConfig();
        var play = NewPlayWith(cfg, new NoClient(), new FixedFolderPicker("/empty")); // FindWowExe → null
        play.State = LauncherState.NoClient;

        await play.LocateExistingClientCommand.ExecuteAsync(null);

        Assert.Equal(LauncherState.DownloadError, play.State);
        Assert.Empty(cfg.Current.ClientInstalls);              // nothing registered
    }

    private static PlayViewModel NewPlayWith(MemoryConfig cfg, IClientService client, IFolderPickerService picker)
    {
        var log = new Serilog.LoggerConfiguration().CreateLogger();
        var paths = new TempPaths();
        return new PlayViewModel(cfg, new NoManifest(), client, new OfflineStatus(),
            new NoDownload(), new ClientVerifyService(log), new NoUpdate(), new NoNews(), new ExitNow(), paths,
            picker, log);
    }

    /// <summary>A client that "finds" a <c>WowClassic.exe</c> (build 42597 only) in the handed folder —
    /// the wrong-era pick a 1.12 realm must reject.</summary>
    private sealed class WrongBuildClient : IClientService
    {
        public string? FindWowExe(string? configuredPath = null) =>
            string.IsNullOrEmpty(configuredPath) ? null : Path.Combine(configuredPath, "WowClassic.exe");
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

    /// <summary>A client that "finds" an exe inside whatever folder it is handed — the existing-install
    /// the player points Locate at. Everything else mirrors <see cref="NoClient"/>.</summary>
    private sealed class LocatableClient : IClientService
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
}
