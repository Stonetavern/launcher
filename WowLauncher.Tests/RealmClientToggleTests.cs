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
/// A realm may now offer more than one client build (owner decision 2026-07-21: Elwynn speaks both
/// 1.12.1 and 1.14.2). Three things had to hold for that to be safe rather than decorative:
///
/// <list type="bullet">
/// <item>A saved <c>launcher_config.json</c> from before <see cref="RealmEntry.ClientKeys"/> existed
/// must keep loading (backward compatibility: the field is simply absent, not migrated).</item>
/// <item>The badge (<c>SelectedRealm.Client</c>) and the footer (<c>Play.ClientVersionText</c>) must
/// agree after the toggle is used - this is the exact contradiction a screenshot exposed against
/// Elwynn (badge said 1.12.1, the removed picker's selected tile said 1.14.2). The root cause,
/// confirmed by reading the code: <c>[ObservableProperty]</c> skips the setter body when the new
/// value equals the old one, and <c>Expansion</c> is a record, so picking the OTHER Vanilla build
/// handed <c>SelectedExpansion</c> an equal value and silently never re-applied.</item>
/// <item>A preset can never be removed, through any command path, even if a caller bypasses the
/// button's own <c>CanExecute</c>.</item>
/// </list>
/// </summary>
public sealed class RealmClientToggleTests
{
    // ── Model-level: no VM needed ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Unit-level only: proves <see cref="RealmEntry.AvailableClients"/>'s own fallback logic, not
    /// what an old saved config actually does end to end (that is a merge question, answered by
    /// <see cref="AnOldSavedElwynn_WithoutClientKeys_StillOffersBothClientsAfterMerging"/> below via a
    /// real file through the real <see cref="ConfigService"/>). Still a real case: a player's OWN
    /// custom realm (never a shipped preset) keeps whatever ClientKeys it was given - nothing merges
    /// stammdaten back into it.
    /// </summary>
    [Fact]
    public void ACustomRealmWithNoClientKeysSet_FallsBackToItsSingleClientKey()
    {
        var realm = new RealmEntry { Id = "someones-realm", Name = "Someones realm", ClientKey = "1.12.1" };

        Assert.Null(realm.ClientKeys);
        Assert.False(realm.HasMultipleClients);
        var only = Assert.Single(realm.AvailableClients);
        Assert.Equal("1.12.1", only.Key);
    }

    /// <summary>
    /// The real proof for Codex Finding 3 (2026-07-22): a pre-2026-07-21 saved Elwynn (hand-authored
    /// JSON, no ClientKeys property at all, exactly what an old launcher wrote) must still end up
    /// offering both clients after RealmRegistry.All() merges it - and the player's own edit (a
    /// repointed address) must survive the merge. Goes through the REAL file + the REAL
    /// ConfigService.Load(), not a hand-built RealmEntry: a prior version of this test only
    /// constructed an object in memory and proved nothing about deserialization or the merge.
    /// </summary>
    [Fact]
    public void AnOldSavedElwynn_WithoutClientKeys_StillOffersBothClientsAfterMerging()
    {
        var paths = new TempPaths();
        paths.EnsureDirectories();
        var json = """
        {
          "Realms": [
            {
              "Id": "elwynn", "Name": "Elwynn", "RealmlistAddress": "old.stonetavern.app",
              "ClientKey": "1.12.1", "ManifestUrl": "https://downloads.stonetavern.app/manifest.json",
              "IsPreset": true, "IsLive": true
            }
          ],
          "SelectedRealmId": "elwynn"
        }
        """;
        File.WriteAllText(paths.ConfigFilePath, json);

        var svc = new ConfigService(paths);
        var cfg = svc.Load();

        // Load() itself already calls RealmRegistry.ApplyActiveRealm -> All(), which mutates the
        // deserialized RealmEntry in place - by the time Load() returns, ClientKeys has ALREADY been
        // patched onto the stored instance. That merge running on every load (not just when the rail
        // is rebuilt) is exactly why an old save recovers immediately, with no separate migration step.
        var storedElwynn = cfg.Realms.Single(r => r.Id == RealmRegistry.ElwynnId);
        Assert.Equal(["1.12.1", "1.14.2"], storedElwynn.ClientKeys);

        var elwynn = RealmRegistry.All(cfg).Single(r => r.Id == RealmRegistry.ElwynnId);
        Assert.True(elwynn.HasMultipleClients);
        Assert.Equal(["1.12.1", "1.14.2"], elwynn.AvailableClients.Select(c => c.Key));
        // The deliberate edit (repointed address) must not be lost by fixing the Stammdaten merge.
        Assert.Equal("old.stonetavern.app", elwynn.RealmlistAddress);
    }

    [Fact]
    public void BothVanillaPresets_OfferBothClients()
    {
        // Owner 2026-07-23: both Vanilla realms speak the same two clients, so Barrens offers 1.14.2
        // as well as 1.12.1 (the install is keyed by gamebuild, shared across realms, no re-download).
        var elwynn = RealmRegistry.Presets().Single(r => r.Id == RealmRegistry.ElwynnId);
        var barrens = RealmRegistry.Presets().Single(r => r.Id == RealmRegistry.BarrensId);

        Assert.True(elwynn.HasMultipleClients);
        Assert.Equal(["1.12.1", "1.14.2"], elwynn.AvailableClients.Select(c => c.Key));

        Assert.True(barrens.HasMultipleClients);
        Assert.Equal(["1.12.1", "1.14.2"], barrens.AvailableClients.Select(c => c.Key));
    }

    // ── Test doubles (same shape as ShellRealmRailTests) ───────────────────────────────────────

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
                        Client = new ManifestFile
                        {
                            Version = "1.0.0",
                            Url = "https://downloads.example.invalid/client-5875.zip",
                            Sha256 = "1111111111111111111111111111111111111111111111111111111111111111",
                        },
                    },
                ],
            });
        public Task<ClientFileManifest?> FetchFileManifestAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<ClientFileManifest?>(null);
    }

    /// <summary>Two managed phases (vanilla + wotlk), each with its own real download coordinates -
    /// what a v1 era switch (Vanilla -> Wrath) needs to prove the URL survives the switch.</summary>
    private sealed class TwoPhaseManifest : IManifestService
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
                        Phase = "vanilla", Realmlist = "play.stonetavern.app",
                        Client = new ManifestFile
                        {
                            Version = "1.0.0", Url = "https://downloads.example.invalid/vanilla.zip",
                            Sha256 = "1111111111111111111111111111111111111111111111111111111111111111",
                        },
                    },
                    new PhaseManifest
                    {
                        Phase = "wotlk", Realmlist = "play.stonetavern.app",
                        Client = new ManifestFile
                        {
                            Version = "1.0.0", Url = "https://downloads.example.invalid/wotlk.zip",
                            Sha256 = "3333333333333333333333333333333333333333333333333333333333333333",
                        },
                    },
                ],
            });
        public Task<ClientFileManifest?> FetchFileManifestAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<ClientFileManifest?>(null);
    }

    /// <summary>A phase that publishes BOTH Vanilla client builds: the canonical 5875 as the singular
    /// client, and 42597 in the additive per-build array. The shape the prod manifest takes once the
    /// modern client is published.</summary>
    private sealed class PerBuildManifest : IManifestService
    {
        public const string ModernUrl = "https://downloads.example.invalid/modern-1.14.2.zip";

        public Task<ServerManifest?> FetchAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(new ServerManifest
            {
                CurrentVersion = "1.0.0",
                ActivePhase = "vanilla",
                Phases =
                [
                    new PhaseManifest
                    {
                        Phase = "vanilla", Realmlist = "play.stonetavern.app",
                        Client = new ManifestFile
                        {
                            Version = "1.0.0", Url = "https://downloads.example.invalid/client-5875.zip",
                            Sha256 = "1111111111111111111111111111111111111111111111111111111111111111",
                        },
                        Clients =
                        [
                            new ManifestFile
                            {
                                Build = 42597, Version = "1.3.1", Url = ModernUrl,
                                Sha256 = "2222222222222222222222222222222222222222222222222222222222222222",
                            },
                        ],
                    },
                ],
            });

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
        /// <summary>Whether a transfer was ever actually started. The guard that keeps the manifest's
        /// 5875 coordinates off a 1.14.2 pick must refuse BEFORE this is ever called - "it failed" is
        /// not proof of "it never tried the wrong file".</summary>
        public int CallCount;

        public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(DownloadResult.Fail(DownloadFailure.Network));
        }
        public Task<bool> ExtractZipAsync(string z, string d, IProgress<string>? p = null, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> ExtractClientAsync(string z, string d, IProgress<string>? p = null, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> VerifyHashAsync(string path, string expected, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    /// <summary>Parks inside DownloadFileAsync until the test releases it - the same shape as
    /// ConcurrentSwitchTests' double, needed here to prove a transfer actually STARTS (Downloading)
    /// rather than just checking the final failure state.</summary>
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

    /// <summary>Like <see cref="GatedDownload"/>, but also records the URL it was handed - "a download
    /// started" is not the assertion that matters here, "it started with the 1.14.2 package" is.</summary>
    private sealed class UrlRecordingDownload : IDownloadService
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? LastUrl;

        public async Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            LastUrl = url;
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

    /// <summary>Records which build every lookup asked for, so a test can prove the toggle queries the
    /// EXACT client build (5875 vs 42597) and not the progression phase's fixed GameBuild.</summary>
    private sealed class BuildRecordingClient : IClientService
    {
        public readonly List<int> RequestedBuilds = [];
        public string? FindWowExe(string? configuredPath = null) => null;
        public string? FindWowExeForBuild(int gameBuild, IReadOnlyDictionary<int, string> installs)
        {
            RequestedBuilds.Add(gameBuild);
            return null;
        }
        public IReadOnlyDictionary<int, string> DetectInstalls(IReadOnlyDictionary<int, string> known) =>
            new Dictionary<int, string>();
        public int? DetectBuild(string wowDirectory) => null;
        public bool IsGameRunning() => false;
        public void SetRealmlist(string wowDirectory, string realmlistAddress) { }
        public void ConfigureClient(string wowDirectory, string locale, string realmlistAddress) { }
        public Task<GameLaunchResult> LaunchAsync(string wowExePath) =>
            Task.FromResult(new GameLaunchResult(false, null, "test"));
    }

    /// <summary>
    /// Nothing is ever registered for a specific build, but the untargeted, generic search always
    /// finds the SAME 5875 install regardless of what build was actually asked for - exactly what a
    /// real machine looks like when only an old 1.12.1 client sits in the default search path.
    /// <see cref="DetectBuild"/> tells the truth about that directory (5875), which is the fact
    /// <c>ResolveInstalledExe</c> must check before accepting the generic find (Codex Finding 1,
    /// 2026-07-22).
    /// </summary>
    private sealed class WrongBuildFoundClient : IClientService
    {
        /// <summary>Set the moment a launch is actually attempted. "It failed" is not proof of "it
        /// never tried the wrong client" - only this is.</summary>
        public bool Launched;

        public string? FindWowExeForBuild(int gameBuild, IReadOnlyDictionary<int, string> installs) => null;
        public string? FindWowExe(string? configuredPath = null) => "/tmp/some-old-install/WoW.exe";
        public int? DetectBuild(string wowDirectory) => 5875;
        public IReadOnlyDictionary<int, string> DetectInstalls(IReadOnlyDictionary<int, string> known) =>
            new Dictionary<int, string>();
        public bool IsGameRunning() => false;
        public void SetRealmlist(string wowDirectory, string realmlistAddress) { }
        public void ConfigureClient(string wowDirectory, string locale, string realmlistAddress) { }
        public Task<GameLaunchResult> LaunchAsync(string wowExePath)
        {
            Launched = true;
            return Task.FromResult(new GameLaunchResult(false, null, "test"));
        }
    }

    private sealed class NoUpdate : IUpdateService
    {
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
        private readonly string _root = Path.Combine(Path.GetTempPath(), "st-toggle-" + Guid.NewGuid().ToString("N"));
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

    private static (ShellViewModel shell, MemoryConfig cfg, NoDownload dl) NewShell(IClientService? client = null)
    {
        var cfg = new MemoryConfig();
        var dl = new NoDownload();
        var log = new Serilog.LoggerConfiguration().CreateLogger();
        var paths = new TempPaths();
        var play = new PlayViewModel(cfg, new FixedManifest(), client ?? new NoClient(), new OfflineStatus(), dl,
            new ClientVerifyService(log), new NoUpdate(), new NoNews(), new ExitNow(), paths,
            new NullFolderPicker(), log);
        var auth = new SignedOutAuth();
        var shell = new ShellViewModel(play, new PatchNotesViewModel(new NoNews(), cfg, log),
            new SettingsViewModel(cfg, new NullFolderPicker()), new NoFriends(), auth, new LoginViewModel(auth), cfg,
            new ArmoryViewModel(new NoArmory(), log));
        return (shell, cfg, dl);
    }

    // ── The badge/footer contradiction, closed ─────────────────────────────────────────────────

    [Fact]
    public async Task SwitchingElwynnToItsSecondClient_MakesTheBadgeAndTheFooterAgree()
    {
        var (shell, cfg, dl) = NewShell();
        await shell.InitAsync();

        Assert.Equal(RealmRegistry.ElwynnId, shell.SelectedRealm.Id);
        Assert.True(shell.SelectedRealm.HasMultipleClients);
        Assert.Equal("1.12.1", shell.SelectedRealm.ClientKey);
        Assert.Contains("1.12.1", shell.Play.ClientVersionText);

        var modernClient = shell.SelectedRealm.AvailableClients.Single(c => c.Key == "1.14.2");
        await shell.SelectRealmClientCommand.ExecuteAsync(modernClient);

        // The badge: RealmEntry.Client is read straight off ClientKey.
        Assert.Equal("1.14.2", shell.SelectedRealm.ClientKey);
        Assert.Equal("1.14.2", shell.SelectedRealm.Client.Key);

        // The footer: Play.ClientVersionText. Before this package's fix this stayed on "1.12.1
        // (5875)" - a stuck value, not a fresh read - because the picker's own setter silently no-op'd
        // (see class doc). Now both name the SAME build.
        Assert.Contains("1.14.2", shell.Play.ClientVersionText);
        Assert.Contains("42597", shell.Play.ClientVersionText);
        Assert.Equal(shell.SelectedRealm.Client.ShortLabel,
            shell.Play.SelectedClientChoice.Client.ShortLabel);
    }

    [Fact]
    public async Task ASecondClientRealmWithoutAnInstall_NeverGetsTheWrongDownloadUrl()
    {
        // Elwynn is managed (has a ManifestUrl), and the manifest describes the 5875 build only - it
        // has no notion of a 1.14.2 download. Picking 1.14.2 must not hand that 5875 zip's URL to a
        // build that is not the one it was published for (owner decision 2026-07-21: alt builds are
        // bring-your-own until the manifest schema grows a second per-build entry).
        var (shell, cfg, dl) = NewShell();
        await shell.InitAsync();

        var modernClient = shell.SelectedRealm.AvailableClients.Single(c => c.Key == "1.14.2");
        await shell.SelectRealmClientCommand.ExecuteAsync(modernClient);

        Assert.Equal(LauncherState.NoClient, shell.Play.State);
        // PlayCommand in NoClient state tries to download - it must refuse on "no URL" BEFORE ever
        // calling the download service, never hand it the 5875 zip's URL for a 42597 pick.
        await shell.Play.PlayCommand.ExecuteAsync(null);
        Assert.Equal(LauncherState.DownloadError, shell.Play.State);
        Assert.Equal(0, dl.CallCount);
    }

    [Fact]
    public async Task SwitchingToTheSecondClient_QueriesTheInstallForTheExactBuild_NotThePhasesBuild()
    {
        // The progression phase "vanilla" always reports GameBuild 5875 - it has no notion of the
        // 42597 (1.14.2) build at all. Before this package's fix the install lookup used
        // _activePhase.GameBuild, so switching to 1.14.2 would still search for a 5875 install
        // (WoW.exe) instead of a 42597 one (WowClassic.exe).
        var client = new BuildRecordingClient();
        var (shell, cfg, dl) = NewShell(client);
        await shell.InitAsync();
        Assert.Contains(5875, client.RequestedBuilds);

        var modernClient = shell.SelectedRealm.AvailableClients.Single(c => c.Key == "1.14.2");
        await shell.SelectRealmClientCommand.ExecuteAsync(modernClient);

        Assert.Contains(42597, client.RequestedBuilds);
    }

    /// <summary>
    /// Codex Finding 1 (2026-07-22, HIGH): the wrong client could still be launched. The per-build
    /// registered lookup was already build-safe, but BOTH the managed and the simple-mode path fell
    /// back to the untargeted <c>FindWowExe()</c> whenever nothing was registered - and that fallback
    /// accepts ANY known exe name, so a machine with only an old 5875 <c>WoW.exe</c> in the default
    /// search path silently resolved to Ready for a realm that names 42597 (1.14.2), and Play would
    /// have launched 5875 against a 1.14.2 realm. The directory must be exactly what it says: only a
    /// 5875 WoW.exe, no 42597 install anywhere.
    /// </summary>
    [Fact]
    public async Task ARealmNeeding1142_NeverAcceptsAGeneric5875Fallback_AndNeverBecomesReady()
    {
        var client = new WrongBuildFoundClient();
        var (shell, cfg, dl) = NewShell(client);
        await shell.InitAsync();

        // 1.12.1 legitimately matches the found 5875 exe - the generic fallback is not wrong here.
        Assert.Equal(LauncherState.Ready, shell.Play.State);

        var modernClient = shell.SelectedRealm.AvailableClients.Single(c => c.Key == "1.14.2");
        await shell.SelectRealmClientCommand.ExecuteAsync(modernClient);

        // The SAME 5875 exe is still all FindWowExe() ever finds - it must now be REJECTED, not reused.
        Assert.Equal(LauncherState.NoClient, shell.Play.State);
        Assert.NotEqual(LauncherState.Ready, shell.Play.State);
    }

    /// <summary>
    /// Codex Finding 2 (2026-07-22, HIGH): v1 (PlayView.axaml, tabu - not touched) binds
    /// SelectedExpansion directly and has no client-build picker at all, that concept postdates it.
    /// Without OnSelectedExpansionChanged bringing SelectedClientChoice along, a v1 era switch left it
    /// on the OLD build, and the download-coordinate guard (ApplyExpansionAsync) then saw a build that
    /// does not match the new phase and emptied the URL - v1 could not download anything after
    /// switching era. Exercises the exact v1 path: setting SelectedExpansion directly, nothing else.
    /// </summary>
    [Fact]
    public async Task SwitchingEraViaSelectedExpansion_TheV1Path_KeepsClientChoiceAndDownloadCoordinatesInSync()
    {
        var cfg = new MemoryConfig();
        var dl = new GatedDownload();
        var log = new Serilog.LoggerConfiguration().CreateLogger();
        var paths = new TempPaths();
        var vm = new PlayViewModel(cfg, new TwoPhaseManifest(), new NoClient(), new OfflineStatus(), dl,
            new ClientVerifyService(log), new NoUpdate(), new NoNews(), new ExitNow(), paths,
            new FixedFolderPicker(paths.Root), log);

        await vm.InitAsync();
        Assert.Equal("1.12.1", vm.SelectedClientChoice.Client.Key);

        // The v1 path: no client picker there, just the era picker.
        vm.SelectedExpansion = Expansion.ById("wotlk");
        await Task.Delay(50);

        Assert.Equal("3.3.5a", vm.SelectedClientChoice.Client.Key);
        Assert.Contains("12340", vm.ClientVersionText);

        // And the download coordinates actually survived: Play() must reach a real transfer attempt,
        // not DownloadError from a guard that saw a stale, mismatched build and emptied the URL.
        var play = vm.PlayCommand.ExecuteAsync(null);
        await dl.Started.Task;
        Assert.Equal(LauncherState.Downloading, vm.State);
        dl.Release.TrySetResult();
        await play;
    }

    /// <summary>
    /// Codex Finding 4 (2026-07-22, MEDIUM): the build-mismatch guard (introduced earlier in this
    /// package) correctly empties the download URL, but left NoClient looking exactly like a normal
    /// "click DOWNLOAD" state - the click was guaranteed to fail (empty URL -> DownloadError) rather
    /// than the launcher being honest up front. The action must be disabled and the text must say the
    /// player brings their own client, not invite a doomed click.
    /// </summary>
    [Fact]
    public async Task ASecondClientRealmWithoutAnInstall_DisablesTheActionAndSaysBringYourOwn()
    {
        var (shell, cfg, dl) = NewShell();
        await shell.InitAsync();

        var modernClient = shell.SelectedRealm.AvailableClients.Single(c => c.Key == "1.14.2");
        await shell.SelectRealmClientCommand.ExecuteAsync(modernClient);

        Assert.Equal(LauncherState.NoClient, shell.Play.State);
        Assert.True(shell.Play.NeedsOwnClient);
        Assert.False(shell.Play.ActionEnabled);
    }

    /// <summary>
    /// The half of Finding 4 the first fix left open, found by re-reading the code on 2026-07-22 and
    /// then proved by a mutation probe (the first attempt at this test was itself a placebo - it went
    /// through the v3 toggle, and SwitchRealmAsync sets State to Initializing on the way, which raises
    /// everything for unrelated reasons and hides the defect entirely).
    ///
    /// <para>Disabling the action is only half the job: the ActionBar has to be TOLD. On the DIRECT
    /// picker path - PlayViewModel.SelectedClientChoice, which v1/v2 and any future direct binding
    /// use - State goes NoClient -> NoClient across the pick, LauncherState is an enum, so
    /// [ObservableProperty] sees an unchanged value, raises nothing, and skips its entire
    /// NotifyPropertyChangedFor list. Every property is correct on a fresh read, which is exactly why
    /// asserting values proves nothing here, but the UI never re-reads them and keeps showing an
    /// enabled DOWNLOAD button for a build that has no download. Same failure shape as the two defects
    /// this package already fixed: right value, no notification, visible only in a screenshot. So this
    /// test watches PropertyChanged.</para>
    /// </summary>
    [Fact]
    public async Task PickingABuildWithNoDownload_NotifiesTheActionBar_NotJustTheProperties()
    {
        var (shell, cfg, dl) = NewShell();
        await shell.InitAsync();

        // Precondition: the case where State does NOT move, so nothing else raises on our behalf.
        Assert.Equal(LauncherState.NoClient, shell.Play.State);
        Assert.True(shell.Play.ActionEnabled);

        var raised = new List<string>();
        shell.Play.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        // The direct path, not the v3 toggle: no realm switch, no State reset, nothing but the pick.
        shell.Play.SelectedClientChoice = ClientChoice.ForKey("1.14.2");
        await Task.Delay(50); // OnSelectedClientChoiceChanged re-applies fire-and-forget

        Assert.Equal(LauncherState.NoClient, shell.Play.State);   // State really did not move
        Assert.DoesNotContain(nameof(PlayViewModel.State), raised);
        Assert.False(shell.Play.ActionEnabled);
        Assert.Contains(nameof(PlayViewModel.ActionEnabled), raised);
        Assert.Contains(nameof(PlayViewModel.ActionPrimaryText), raised);
        Assert.Contains(nameof(PlayViewModel.ActionGlyph), raised);
        Assert.Contains(nameof(PlayViewModel.StatusLine), raised);
        Assert.Contains(nameof(PlayViewModel.SubLine), raised);
        Assert.Contains(nameof(PlayViewModel.NeedsOwnClient), raised);
    }

    /// <summary>
    /// The cached-path last resort in LaunchCoreAsync is verified rather than trusted: _wowPath is set
    /// by whichever build was resolved last, and a stale one would start 1.12.1 against a 1.14.2 realm
    /// - the defect Finding 1 closed one layer up, at the layer below it.
    ///
    /// <para><b>Honest scope:</b> no end-to-end path is known that reaches LaunchCoreAsync with a
    /// _wowPath belonging to another build - a build change re-resolves State, and Play() in NoClient
    /// downloads rather than launches. So this is a unit test of the rule itself, not a reproduction of
    /// a live defect, and it is labelled that way instead of being dressed up as one. The end-to-end
    /// attempt was written first, failed its mutation probe (it passed with the check removed, because
    /// it never reached the launch path at all), and was deleted rather than kept as decoration.</para>
    /// </summary>
    /// <summary>
    /// The other side of Finding 4, once the manifest actually publishes the second build: with
    /// per-build coordinates present, picking 1.14.2 must offer a REAL download rather than
    /// bring-your-own - and it must be the 1.14.2 package, never the 1.12.1 one. Uses the gated
    /// download double so the assertion is that a transfer actually STARTS, with the URL it was
    /// handed, instead of inspecting a final failure state.
    /// </summary>
    [Fact]
    public async Task WhenTheManifestPublishesTheSecondBuild_ThatBuildBecomesDownloadable()
    {
        var cfg = new MemoryConfig();
        var dl = new UrlRecordingDownload();
        var log = new Serilog.LoggerConfiguration().CreateLogger();
        var paths = new TempPaths();
        var play = new PlayViewModel(cfg, new PerBuildManifest(), new NoClient(), new OfflineStatus(), dl,
            new ClientVerifyService(log), new NoUpdate(), new NoNews(), new ExitNow(), paths,
            new FixedFolderPicker(paths.Root), log);
        var auth = new SignedOutAuth();
        var shell = new ShellViewModel(play, new PatchNotesViewModel(new NoNews(), cfg, log),
            new SettingsViewModel(cfg, new NullFolderPicker()), new NoFriends(), auth, new LoginViewModel(auth), cfg,
            new ArmoryViewModel(new NoArmory(), log));

        await shell.InitAsync();
        var modernClient = shell.SelectedRealm.AvailableClients.Single(c => c.Key == "1.14.2");
        await shell.SelectRealmClientCommand.ExecuteAsync(modernClient);

        // No install anywhere, but now there IS a source: an honest DOWNLOAD, not bring-your-own.
        Assert.Equal(LauncherState.NoClient, shell.Play.State);
        Assert.False(shell.Play.NeedsOwnClient);
        Assert.True(shell.Play.ActionEnabled);

        var play2 = shell.Play.PlayCommand.ExecuteAsync(null);
        await dl.Started.Task;
        Assert.Equal(PerBuildManifest.ModernUrl, dl.LastUrl);
        dl.Release.TrySetResult();
        await play2;
    }

    [Theory]
    [InlineData("WoW.exe", 5875, true)]
    [InlineData("WoW.exe", 42597, false)]          // WoW.exe is never the Classic Era build
    [InlineData("WowClassic.exe", 42597, true)]
    [InlineData("WowClassic.exe", 5875, false)]    // WowClassic.exe is never 1.12.1
    [InlineData("WoW.exe", 12340, true)]           // one name, several legacy builds: still consistent
    public void AnExeNameIsOnlyAcceptedForABuildItCanActuallyBe(string exeName, int build, bool accepted) =>
        Assert.Equal(accepted, ClientVersion.ExeNameCanBeBuild(exeName, build));

    /// <summary>
    /// The defect at the level it actually lives, independent of the v3 toggle (which bypasses it by
    /// going through SwitchRealmAsync). Directly exercises PlayViewModel.SelectedClientChoice, which
    /// v1/v2 or any future direct picker still binds to: [ObservableProperty] skips the setter body
    /// when the new value equals the old one, and Expansion is a record, so picking the OTHER Vanilla
    /// build hands SelectedExpansion an equal value and the setter never fires ApplyExpansionAsync.
    /// </summary>
    [Fact]
    public async Task PickingTheOtherVanillaBuild_ActuallyReapplies_NotJustHighlightsTheOtherTile()
    {
        // ClientVersionText tracks SelectedClientChoice directly, so it would look right even if
        // ApplyExpansionAsync never re-ran - that is not proof the fix works. What actually proves
        // ApplyExpansionAsync fired is the SIDE EFFECT only it produces: a fresh install lookup for
        // the new build. BuildRecordingClient observes exactly that.
        var client = new BuildRecordingClient();
        var (shell, _, _) = NewShell(client);
        await shell.InitAsync();
        Assert.Equal("1.12.1", shell.Play.SelectedClientChoice.Client.Key);
        Assert.Contains(5875, client.RequestedBuilds);
        Assert.DoesNotContain(42597, client.RequestedBuilds);

        shell.Play.SelectedClientChoice = ClientChoice.ForKey("1.14.2");
        await Task.Delay(50); // OnSelectedClientChoiceChanged re-applies fire-and-forget

        Assert.Equal("1.14.2", shell.Play.SelectedClientChoice.Client.Key);
        Assert.Contains(42597, client.RequestedBuilds);
    }

    // ── Presets can never be removed ───────────────────────────────────────────────────────────

    // NOTE (Codex review, 2026-07-22): a prior version of this file also had a
    // "RemoveSelectedRealm_RefusesAPreset_EvenBypassingCanExecute" test that asserted Elwynn survives
    // a bypassed remove. It passed for the WRONG reason: Elwynn is never actually IN cfg.Realms on a
    // fresh config (it is a virtual preset RealmRegistry.All() re-adds every time regardless of what
    // RemoveSelectedRealm's body does), so the assertion held even with the guard deleted - a
    // placebo. Removed; the real proof of "a preset survives a forced remove" is the test right below,
    // which uses a STORED preset override (the only case where the guard's absence is observable).

    /// <summary>
    /// RealmRegistry.All() always re-adds the shipped presets from Presets() regardless of what
    /// cfg.Realms holds, so an unguarded remove can never make Elwynn vanish from the rail - it would
    /// only strip a PLAYER'S persisted override (an edited address/client) back to the shipped
    /// default, silently. That is the actual thing the hard guard protects, and it is invisible if you
    /// only look at "is the realm still on the rail".
    /// </summary>
    [Fact]
    public async Task RemoveSelectedRealm_KeepsAPersistedPresetEditIntact_EvenBypassingCanExecute()
    {
        var (shell, cfg, dl) = NewShell();
        await shell.InitAsync();

        // A persisted override of Elwynn, the shape Settings/the client toggle writes when a preset
        // is edited: same id, IsPreset still true, but a build the shipped default does not carry.
        var edited = new RealmEntry
        {
            Id = RealmRegistry.ElwynnId,
            Name = "Elwynn",
            RealmlistAddress = "play.stonetavern.app",
            ClientKey = "1.14.2",
            ClientKeys = ["1.12.1", "1.14.2"],
            IsPreset = true,
        };
        cfg.Current.Realms.Add(edited);
        shell.ReloadRealms();
        Assert.Equal("1.14.2", shell.SelectedRealm.ClientKey);

        shell.RemoveSelectedRealmCommand.Execute(null);

        var stillStored = Assert.Single(cfg.Current.Realms, r => r.Id == RealmRegistry.ElwynnId);
        Assert.Equal("1.14.2", stillStored.ClientKey);
    }

    [Fact]
    public async Task RemoveSelectedRealm_RemovesAPlayersOwnRealm()
    {
        var (shell, cfg, dl) = NewShell();
        await shell.InitAsync();

        var own = new RealmEntry { Id = "my-realm", Name = "My realm", RealmlistAddress = "play.example.invalid", ClientKey = "1.12.1" };
        cfg.Current.Realms.Add(own);
        shell.ReloadRealms();
        shell.SelectedRealm = shell.Realms.First(r => r.Id == own.Id); // what clicking the rail does
        Assert.Equal(own.Id, shell.SelectedRealm.Id);
        Assert.False(shell.SelectedRealm.IsPreset);

        shell.RemoveSelectedRealmCommand.Execute(null);

        Assert.DoesNotContain(shell.Realms, r => r.Id == own.Id);
        Assert.DoesNotContain(own.Id, cfg.Current.Realms.Select(r => r.Id));
    }
}
