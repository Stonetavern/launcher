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
/// The realm rail is the second half of the re-entrancy story. <see cref="ConcurrentSwitchTests"/>
/// covers the expansion picker inside the play surface; this file covers the shell, which owns the
/// realm and had no test at all.
///
/// <para>What a realm switch does: it repoints realmlist AND manifest, then makes the play surface
/// re-resolve everything from config. Doing that on top of a running download hides a live transfer
/// behind an Initializing surface and points the launcher at a different server than the one the
/// bytes are coming from. The rail is disabled in the v3 shell while busy; these tests cover every
/// other caller, which is what the guard in the setter exists for.</para>
/// </summary>
public sealed class ShellRealmRailTests
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

    private sealed class FixedManifest : IManifestService
    {
        public Task<ServerManifest?> FetchAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(new ServerManifest
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
            });
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

    private sealed class GatedDownload : IDownloadService
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Release.Task;
            return DownloadResult.Fail(DownloadFailure.Network);
        }

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
        private readonly string _root = Path.Combine(Path.GetTempPath(), "st-rail-" + Guid.NewGuid().ToString("N"));
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

    private sealed class NoFriends : IFriendsPresenceService
    {
        public Task<IReadOnlyList<FriendPresence>> GetFriendsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FriendPresence>>([]);
        public Task<AddFriendResult> AddFriendAsync(string account, CancellationToken ct = default) =>
            Task.FromResult(AddFriendResult.Rejected("test"));
    }

    private sealed class SignedOutAuth : ILauncherAuthService
    {
        public bool IsLoggedIn => false;
        public string? CurrentToken => null;
        public string? CurrentAccount => null;
        public Task<LoginOutcome> LoginAsync(string u, string p, CancellationToken ct = default) =>
            Task.FromResult(LoginOutcome.Failure("test"));
        public Task<LoginOutcome> RegisterAsync(RegisterRequest r, CancellationToken ct = default) =>
            Task.FromResult(LoginOutcome.Failure("test"));
        public void Logout() { }
    }

    private sealed class NoArmory : IArmoryService
    {
        public Task<ArmoryRoster> GetCharactersAsync(string realmId, CancellationToken ct = default) =>
            Task.FromResult(ArmoryRoster.Empty(ArmoryStatus.SignedOut));
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    /// <param name="withSecondRealm">Seed a player's OWN realm beside the shipped preset. Since the
    /// rail collapsed to a single Stonetavern entry (owner 2026-07-27), a second entry only exists when
    /// someone added one — which is exactly the case the switch guard has to hold for.</param>
    private static (ShellViewModel shell, MemoryConfig cfg, GatedDownload dl) NewShell(bool withSecondRealm = true)
    {
        var cfg = new MemoryConfig();
        if (withSecondRealm)
            cfg.Current.Realms.Add(new RealmEntry
            {
                Id = "my-realm",
                Name = "My realm",
                RealmlistAddress = "play.example.invalid",
                ClientKey = "1.12.1",
            });
        var dl = new GatedDownload();
        var log = new Serilog.LoggerConfiguration().CreateLogger();
        var paths = new TempPaths();
        var play = new PlayViewModel(cfg, new FixedManifest(), new NoClient(), new OfflineStatus(), dl,
            new ClientVerifyService(log), new NoUpdate(), new NoNews(), new ExitNow(), paths,
            new FixedFolderPicker(paths.Root), log);
        var auth = new SignedOutAuth();
        var shell = new ShellViewModel(play, new PatchNotesViewModel(new NoNews(), cfg, log),
            new SettingsViewModel(cfg, new NullFolderPicker()), new NoFriends(), auth, new LoginViewModel(auth), cfg,
            new ArmoryViewModel(new NoArmory(), log));
        return (shell, cfg, dl);
    }

    /// <summary>Park a download so the shell is busy, exactly as a real multi-GB transfer would.</summary>
    private static async Task<Task> StartParkedDownloadAsync(ShellViewModel shell, GatedDownload dl)
    {
        await shell.InitAsync();
        Assert.Equal(LauncherState.NoClient, shell.Play.State);
        var play = shell.Play.PlayCommand.ExecuteAsync(null);
        await dl.Started.Task;
        Assert.False(shell.Play.CanSwitchContext);
        return play;
    }

    // ── Tests ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARealmClickDuringADownload_IsRefused_AndTheRailSnapsBack()
    {
        var (shell, cfg, dl) = NewShell();
        var startedOn = shell.SelectedRealm;
        var other = shell.Realms.First(r => r.Id != startedOn.Id);

        var play = await StartParkedDownloadAsync(shell, dl);

        shell.SelectedRealm = other;      // what the rail does when it is not disabled

        Assert.Same(startedOn, shell.SelectedRealm);
        Assert.NotEqual(other.Id, cfg.Current.SelectedRealmId);
        Assert.Equal(LauncherState.Downloading, shell.Play.State);

        dl.Release.TrySetResult();
        await play;
    }

    /// <summary>
    /// The realm list is rebuilt from config whenever the settings surface changes it, and the rebuild
    /// hands out FRESH RealmEntry instances. If the frozen realm is not re-resolved in that rebuild, the
    /// guard above puts an object back into the rail that is no longer part of it: the selection then
    /// points at an entry the list does not contain, and the rail shows nothing selected.
    /// </summary>
    [Fact]
    public async Task RebuildingTheRailDuringADownload_KeepsTheSelectionInsideTheRail()
    {
        var (shell, cfg, dl) = NewShell();
        var play = await StartParkedDownloadAsync(shell, dl);
        var lockedId = shell.SelectedRealm.Id;

        // What Settings.RealmsChanged triggers: the player edits their realm list mid-download.
        shell.ReloadRealms();

        Assert.Equal(lockedId, shell.SelectedRealm.Id);
        Assert.Contains(shell.SelectedRealm, shell.Realms);

        // And the guard still works against the rebuilt list.
        var other = shell.Realms.First(r => r.Id != lockedId);
        shell.SelectedRealm = other;
        Assert.Equal(lockedId, shell.SelectedRealm.Id);
        Assert.Contains(shell.SelectedRealm, shell.Realms);

        dl.Release.TrySetResult();
        await play;
    }

    /// <summary>The counter-proof: the guard must not simply refuse everything. With nothing running a
    /// realm click has to reach the config, or the rail is decorative.</summary>
    [Fact]
    public async Task ARealmClickWhileIdle_SwitchesTheRealm()
    {
        var (shell, cfg, dl) = NewShell();
        await shell.InitAsync();

        var other = shell.Realms.First(r => r.Id != shell.SelectedRealm.Id);
        shell.SelectedRealm = other;

        Assert.Same(other, shell.SelectedRealm);
        Assert.Equal(other.Id, cfg.Current.SelectedRealmId);
    }
}
