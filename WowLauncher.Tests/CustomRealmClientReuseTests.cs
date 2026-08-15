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
/// A realm the player added themselves (owner report 2026-08-05: a "localhost" realm bound to 1.14.2).
/// Such a realm has no manifest, so it can never download or repair a client. Two things follow, and
/// both are the point of this file:
///
/// <list type="bullet">
/// <item><b>An installed client is shared.</b> <see cref="LauncherConfig.ClientInstalls"/> is keyed by
/// gamebuild alone - there is no per-realm install anywhere in the launcher - so a 1.14.2 client
/// downloaded for Stonetavern is the SAME client a self-added realm plays on, with only the realmlist
/// repointed. The owner's second question ("why can a client I already have not be used for any
/// realm") has to answer itself in a test, not in a comment.</item>
/// <item><b>A realm that cannot download must still offer a way forward.</b> Not a grey button reading
/// UNAVAILABLE, which is what it did.</item>
/// </list>
/// </summary>
public sealed class CustomRealmClientReuseTests
{
    private const int ClassicEra = 42597;

    // ── Test doubles ───────────────────────────────────────────────────────────────────────────

    /// <summary>Mirrors what the real <see cref="ConfigService"/> does on every load: project the
    /// selected realm into the flat fields the services read. Without it a test would silently keep a
    /// Stonetavern manifest URL while selecting a manifest-less realm.</summary>
    private sealed class MemoryConfig(LauncherConfig cfg) : IConfigService
    {
        public LauncherConfig Current = cfg;
        public LauncherConfig Load()
        {
            RealmRegistry.ApplyActiveRealm(Current);
            return Current;
        }
        public void Save(LauncherConfig config) => Current = config;
        public bool LastSaveSucceeded => true;
    }

    /// <summary>The install registry is the only thing this knows: hand back the registered directory's
    /// exe for the build that is registered, nothing for any other. No generic search, so a pass can
    /// only come from the shared per-build registry.</summary>
    private sealed class RegisteredInstallClient(int build, string dir) : IClientService
    {
        public string? LaunchedExe;

        public string? FindWowExe(string? configuredPath = null) => null;
        public string? FindWowExeForBuild(int gameBuild, IReadOnlyDictionary<int, string> installs) =>
            gameBuild == build && installs.TryGetValue(gameBuild, out var d)
                ? Path.Combine(d, ClientVersion.ByBuild(gameBuild)!.ExeName)
                : null;
        public IReadOnlyDictionary<int, string> DetectInstalls(IReadOnlyDictionary<int, string> known) =>
            new Dictionary<int, string>();
        public int? DetectBuild(string wowDirectory) =>
            string.Equals(wowDirectory, dir, StringComparison.OrdinalIgnoreCase) ? build : null;
        public bool IsGameRunning() => false;
        public void SetRealmlist(string wowDirectory, string realmlistAddress) { }
        public void ConfigureClient(string wowDirectory, string locale, string realmlistAddress) { }
        public Task<GameLaunchResult> LaunchAsync(string wowExePath)
        {
            LaunchedExe = wowExePath;
            // Deliberately "did not start": a CONFIRMED launch with no tray host makes the view model
            // call Environment.Exit(0), which takes the test host down with it. What this file has to
            // prove is WHICH executable the launch path picks for a manifest-less realm, and that is
            // recorded above before any of that.
            return Task.FromResult(new GameLaunchResult(false, null, "test double does not start processes"));
        }
    }

    /// <summary>Counts every fetch. A manifest-less realm must never be handed one, and "check for
    /// updates" must not pretend it asked a server that does not exist.</summary>
    private sealed class CountingManifest : IManifestService
    {
        public int Fetches;
        public Task<ServerManifest?> FetchAsync(CancellationToken ct = default)
        {
            Fetches++;
            return Task.FromResult<ServerManifest?>(null);
        }
        /// <summary>Kein Launcher-Manifest in diesem Double: der Selbst-Update-Pfad ist hier
        /// nicht der Prüfgegenstand, und "keins" heißt "kein Update", nie "irgendeins".</summary>
        public Task<ServerManifest?> FetchLauncherManifestAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(null);

        public Task<ClientFileManifest?> FetchFileManifestAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<ClientFileManifest?>(null);
    }

    private sealed class OfflineStatus : IServerStatusService
    {
        public int Checks;
        public Task<ServerStatusResult> CheckAsync(string host, int port = 3724, CancellationToken ct = default)
        {
            Checks++;
            return Task.FromResult(new ServerStatusResult { Online = true, PlayerCount = 0 });
        }
    }

    private sealed class NoDownload : IDownloadService
    {
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

    private sealed class NoUpdate : IUpdateService
    {
        public event EventHandler? LauncherUpdateStarting { add { } remove { } }
        public Task<bool> CheckAndApplyAsync(ServerManifest? m, CancellationToken ct = default) => Task.FromResult(false);
        public LauncherUpdateNotice? CheckForNotice(ServerManifest? m) => null;
    }

    private sealed class NoNews : INewsService
    {
        public Task<IReadOnlyList<NewsItem>> GetNewsAsync(bool forceRefresh = false, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NewsItem>>([]);
    }

    private sealed class NeverConfirms : ILaunchExitPolicy
    {
        public Task<bool> ConfirmClientRunningAsync(string exePath) => Task.FromResult(false);
    }

    /// <summary>An install that carries exactly English and German on disk, and never downloads.</summary>
    private sealed class TwoInstalledLanguages : ILanguagePackService
    {
        public IReadOnlyList<string> Installed(string clientDir) => ["enUS", "deDE"];
        public string Active(string clientDir) => "enUS";
        public Task<VanillaLocaleResult> EnsureAsync(string clientDir, string locale, ManifestLanguagePack? pack,
            bool gameRunning = false, IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(new VanillaLocaleResult(true, "enUS"));

        /// <summary>Nichts ist veraltet, und nichts laesst sich aktualisieren: dieser Doppelgaenger
        /// steht fuer eine Installation, die einfach zwei Sprachen hat. Ein "veraltet" hier waere ein
        /// Nebenschauplatz in Tests, die von Realms handeln.</summary>
        public LanguagePackState State(string clientDir, string locale, ManifestLanguagePack? pack) =>
            LanguagePackState.NothingOffered;

        public Task<VanillaLocaleResult> UpdateAsync(string clientDir, string locale, ManifestLanguagePack? pack,
            bool gameRunning = false, IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(VanillaLocaleResult.Failed("enUS", "Not offered in this test."));
    }

    private sealed class TempPaths : IAppPaths
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "st-custom-" + Guid.NewGuid().ToString("N"));
        public string Root => _root;
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

    // ── Fixture ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A config with exactly the owner's shape: the shipped preset plus a self-added realm
    /// bound to 1.14.2, the self-added one selected, and no manifest on it.</summary>
    private static LauncherConfig CustomRealmSelected(string? installDir)
    {
        var cfg = new LauncherConfig
        {
            SelectedRealmId = "localhost",
            Realms =
            [
                new RealmEntry
                {
                    Id = "localhost", Name = "localhost", RealmlistAddress = "127.0.0.1",
                    ClientKey = "1.14.2", ManifestUrl = null, IsPreset = false, IsLive = true,
                },
            ],
        };
        if (installDir is not null) cfg.ClientInstalls[ClassicEra] = installDir;
        return cfg;
    }

    private static (PlayViewModel vm, MemoryConfig cfg, RegisteredInstallClient client,
        CountingManifest manifest, OfflineStatus status, NoDownload dl, NullFolderPicker picker)
        NewPlay(string? installDir)
    {
        var cfg = new MemoryConfig(CustomRealmSelected(installDir));
        var client = new RegisteredInstallClient(ClassicEra, installDir ?? "/nowhere");
        var manifest = new CountingManifest();
        var status = new OfflineStatus();
        var dl = new NoDownload();
        var picker = new NullFolderPicker();
        var log = new Serilog.LoggerConfiguration().CreateLogger();
        var paths = new TempPaths();
        paths.EnsureDirectories();
        var vm = new PlayViewModel(cfg, manifest, client, status, dl,
            new ClientVerifyService(log), new NoUpdate(), new NoNews(), new NeverConfirms(), paths,
            picker, log);
        return (vm, cfg, client, manifest, status, dl, picker);
    }

    // ── The owner's second question: is an installed client shared across realms? ───────────────

    /// <summary>
    /// The 1.14.2 client was installed for Stonetavern. A self-added realm naming the same build must
    /// be READY straight away - no second download, no per-realm install - and PLAY must actually start
    /// that very executable. If this ever goes red, someone has made the install per realm.
    /// </summary>
    [Fact]
    public async Task AnInstalledClient_IsUsableFromARealmThatCouldNeverHaveDownloadedIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "st-classic-" + Guid.NewGuid().ToString("N"));
        var (vm, cfg, client, manifest, _, dl, _) = NewPlay(dir);

        await vm.InitAsync();

        Assert.Equal(LauncherState.Ready, vm.State);
        Assert.Equal(WowLauncher.Localization.Loc.T("Play_Cta_Play"), vm.ActionPrimaryText);
        Assert.True(vm.ActionEnabled);

        await vm.PlayCommand.ExecuteAsync(null);

        // The exact install registered under build 42597, launched from a realm that has no manifest.
        Assert.Equal(Path.Combine(dir, "WowClassic.exe"), client.LaunchedExe);
        Assert.Equal(0, dl.CallCount);
        // And the realm the client was pointed at is the player's own address, not Stonetavern.
        Assert.Equal("127.0.0.1", cfg.Current.RealmlistAddress);
    }

    // ── The dead end: no manifest, no client ───────────────────────────────────────────────────

    /// <summary>
    /// Nothing installed for 1.14.2 and no manifest behind the realm. The launcher cannot download,
    /// and that is honest - but the action must be a way forward ("find my client"), pressable, with a
    /// sentence next to it naming the build. It used to be a grey UNAVAILABLE with no reason on screen.
    /// </summary>
    [Fact]
    public async Task ARealmWithoutManifestAndWithoutClient_PointsAtTheFolderInsteadOfGoingGrey()
    {
        var (vm, _, _, _, _, dl, picker) = NewPlay(installDir: null);

        await vm.InitAsync();

        Assert.Equal(LauncherState.NoClient, vm.State);
        Assert.True(vm.NeedsOwnClient);
        Assert.True(vm.ActionEnabled);
        Assert.Equal(WowLauncher.Localization.Loc.T("Play_Cta_Locate"), vm.ActionPrimaryText);
        Assert.Contains("1.14.2", vm.StatusLine, StringComparison.Ordinal);

        await vm.PlayCommand.ExecuteAsync(null);

        Assert.Equal(1, picker.CallCount);   // the folder dialog, the only thing that can work here
        Assert.Equal(0, dl.CallCount);       // never a download that has no URL
    }

    // ── "Check for updates" must not be a silent no-op ─────────────────────────────────────────

    // ── The language menu on a realm that cannot deliver a language pack ───────────────────────

    /// <summary>A manifest that publishes German and French packs for 1.12.1 - what Stonetavern does.</summary>
    private sealed class PackPublishingManifest : IManifestService
    {
        public Task<ServerManifest?> FetchAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(new ServerManifest
            {
                CurrentVersion = "1.0.0",
                ActivePhase = "vanilla",
                LanguagePacks =
                [
                    new ManifestLanguagePack { Locale = "deDE", Build = 5875 },
                    new ManifestLanguagePack { Locale = "frFR", Build = 5875 },
                ],
                Phases = [new PhaseManifest { Phase = "vanilla", Realmlist = "play.stonetavern.app" }],
            });
        /// <summary>Kein Launcher-Manifest in diesem Double: der Selbst-Update-Pfad ist hier
        /// nicht der Prüfgegenstand, und "keins" heißt "kein Update", nie "irgendeins".</summary>
        public Task<ServerManifest?> FetchLauncherManifestAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(null);

        public Task<ClientFileManifest?> FetchFileManifestAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<ClientFileManifest?>(null);
    }

    private static PlayViewModel NewVanillaPlay(IManifestService manifest, string? realmManifestUrl, string dir)
    {
        var cfg = new LauncherConfig
        {
            SelectedRealmId = "local",
            Realms =
            [
                new RealmEntry
                {
                    Id = "local", Name = "local", RealmlistAddress = "127.0.0.1",
                    ClientKey = "1.12.1", ManifestUrl = realmManifestUrl, IsPreset = false, IsLive = true,
                },
            ],
        };
        cfg.ClientInstalls[5875] = dir;
        var log = new Serilog.LoggerConfiguration().CreateLogger();
        var paths = new TempPaths();
        paths.EnsureDirectories();
        return new PlayViewModel(new MemoryConfig(cfg), manifest, new RegisteredInstallClient(5875, dir),
            new OfflineStatus(), new NoDownload(), new ClientVerifyService(log), new NoUpdate(), new NoNews(),
            new NeverConfirms(), paths, new NullFolderPicker(), log,
            windowController: null, session: null, languagePacks: new TwoInstalledLanguages());
    }

    /// <summary>
    /// Owner finding 2026-08-05 (second half, same root cause). For 1.12.1 a language is a pack the
    /// SERVER publishes. A self-added realm has no manifest, so there are no packs and the menu is
    /// exactly what is installed: two entries where Stonetavern shows five. Shortening it is right -
    /// offering a language that cannot be delivered would fail at the moment of picking it - but the
    /// launcher has to SAY so, or it looks like it lost three languages.
    /// </summary>
    [Fact]
    public async Task OnARealmWithoutLanguagePacks_TheShortMenu_ComesWithTheReason()
    {
        var dir = Path.Combine(Path.GetTempPath(), "st-vanilla-" + Guid.NewGuid().ToString("N"));
        var vm = NewVanillaPlay(new CountingManifest(), realmManifestUrl: null, dir);

        await vm.InitAsync();

        Assert.Equal(["enUS", "deDE"], vm.AvailableLocales.Select(l => l.Code));
        Assert.Equal(WowLauncher.Localization.Loc.T("Play_Language_LocalOnly"), vm.LanguageStatus);
    }

    /// <summary>The other side: a realm whose server DOES publish packs must not carry the note - the
    /// menu is long there for a reason, and a standing explanation nobody needs is noise.</summary>
    [Fact]
    public async Task OnARealmThatPublishesLanguagePacks_ThereIsNoNote()
    {
        var dir = Path.Combine(Path.GetTempPath(), "st-vanilla-" + Guid.NewGuid().ToString("N"));
        var vm = NewVanillaPlay(new PackPublishingManifest(),
            realmManifestUrl: "https://downloads.example.invalid/manifest.json", dir);

        await vm.InitAsync();

        Assert.Contains("frFR", vm.AvailableLocales.Select(l => l.Code));
        Assert.Equal("", vm.LanguageStatus);
    }

    // ── "Check for updates" must not be a silent no-op ─────────────────────────────────────────

    /// <summary>
    /// The owner pressed "check for updates" on a self-added realm and nothing happened at all. Cause:
    /// a null manifest was read as "we are offline" and the method returned before re-resolving
    /// anything - but a realm with no manifest URL is not offline, it simply has no server to ask.
    /// There is still something real to check: whether a client turned up on disk since, and whether
    /// the realm answers. This pins that it does both.
    /// </summary>
    [Fact]
    public async Task CheckForUpdates_OnARealmWithoutManifest_StillRechecksTheClientAndTheRealm()
    {
        // Start with nothing installed, so the launcher settles on NoClient.
        var dir = Path.Combine(Path.GetTempPath(), "st-classic-" + Guid.NewGuid().ToString("N"));
        var (vm, cfg, client, _, status, _, _) = NewPlay(installDir: null);
        await vm.InitAsync();
        Assert.Equal(LauncherState.NoClient, vm.State);

        var pingsBefore = status.Checks;

        // The player installs/registers the client outside the launcher and presses the button.
        cfg.Current.ClientInstalls[ClassicEra] = dir;

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.True(status.Checks > pingsBefore, "the realm was not re-checked");
        Assert.Equal(LauncherState.Ready, vm.State);
    }
}
