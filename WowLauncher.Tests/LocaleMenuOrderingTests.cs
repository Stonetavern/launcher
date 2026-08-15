namespace WowLauncher.Tests;

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

/// <summary>
/// Der Sprachwähler war weg, obwohl der Server drei Pakete anbot.
///
/// <para><b>Was passiert war</b> (Owner-Befund 2026-08-04, von Codex als Reihenfolgeproblem
/// bestätigt). Welche Sprachen wählbar sind, hat <b>zwei</b> Eingaben: was auf der Platte liegt und
/// was der Server anbietet. Nachgerechnet wurde nur, wenn sich die erste änderte. Nach einer frischen
/// 1.12.1-Installation lief die Rechnung also genau dann, wenn der Client-Pfad gesetzt wurde — war
/// das Manifest da noch nicht eingetroffen, blieb „nur Englisch" stehen. Das Manifest kam später,
/// niemand rechnete nach, der Wähler blieb verschwunden.</para>
///
/// <para>Kein Fehler, keine Meldung, kein roter Test: nur ein Bedienelement, das nicht da war. Und
/// drei verschiedene Ursachen erzeugen dasselbe Bild — der Client hat nur eine Sprache, das Manifest
/// fehlte, oder die Rechnung lief nie.</para>
///
/// <para>🔴 <b>Was dieser Test NICHT beweist.</b> Er bleibt grün, wenn man die Neuberechnung im
/// Manifest-Setter wieder abschaltet: „Nach Updates suchen" landet über
/// <c>ApplyExpansionAsync</c> ohnehin wieder beim Client-Pfad und rechnet dort nach. Der Test hält
/// also das <b>Verhalten</b> fest (ein spät eintreffendes Manifest bringt den Wähler zurück), nicht
/// den Fix. Warum der Wähler beim Owner wirklich fehlte, ist am 2026-08-04 <b>ungeklärt</b> — die
/// Protokollzeile in <c>RefreshAvailableLocales</c> beantwortet es beim nächsten Start. Wer hier
/// später einen roten Fall findet, gehört genau hierhin.</para>
/// </summary>
public sealed class LocaleMenuOrderingTests : IDisposable
{
    private const int VanillaBuild = 5875;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "locale-order-" + Guid.NewGuid().ToString("N"));

    public LocaleMenuOrderingTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* Aufräumen */ } }

    /// <summary>Ein 1.12.1-Verzeichnis, wie es nach der Installation wirklich aussieht: MPQ-Archive,
    /// kein einziger Sprachordner. Also genau eine installierte Sprache — Englisch.</summary>
    private string EnglishOnlyClient()
    {
        var dir = Path.Combine(_root, "Stonetavern-Enhanced-1.12.1");
        Directory.CreateDirectory(Path.Combine(dir, "Data"));
        foreach (var f in new[] { "dbc.MPQ", "interface.MPQ", "patch.MPQ", "terrain.MPQ" })
            File.WriteAllText(Path.Combine(dir, "Data", f), "x");
        File.WriteAllText(Path.Combine(dir, "WoW.exe"), "x");
        return dir;
    }

    // ── Attrappen ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Liefert erst gar kein Manifest und später eines mit drei Sprachpaketen — die
    /// Reihenfolge, in der es beim Owner schiefging.</summary>
    private sealed class LateManifest : IManifestService
    {
        public ServerManifest? Current;
        public Task<ServerManifest?> FetchAsync(CancellationToken ct = default) => Task.FromResult(Current);
        /// <summary>Kein Launcher-Manifest in diesem Double: der Selbst-Update-Pfad ist hier
        /// nicht der Prüfgegenstand, und "keins" heißt "kein Update", nie "irgendeins".</summary>
        public Task<ServerManifest?> FetchLauncherManifestAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(null);

        public Task<ClientFileManifest?> FetchFileManifestAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<ClientFileManifest?>(null);
    }

    private sealed class MemoryConfig(string clientDir) : IConfigService
    {
        public LauncherConfig Current = new()
        {
            ManifestUrl = "https://downloads.example.invalid/manifest.json",
            RealmlistAddress = "play.stonetavern.app",
            Locale = "enUS",
            ClientInstalls = new Dictionary<int, string> { [VanillaBuild] = clientDir },
        };
        public LauncherConfig Load() => Current;
        public void Save(LauncherConfig config) => Current = config;
        public bool LastSaveSucceeded => true;
    }

    private sealed class InstalledClient(string dir) : IClientService
    {
        public string? FindWowExe(string? configuredPath = null) => Path.Combine(dir, "WoW.exe");
        public string? FindWowExeForBuild(int gameBuild, IReadOnlyDictionary<int, string> installs) =>
            gameBuild == VanillaBuild ? Path.Combine(dir, "WoW.exe") : null;
        public IReadOnlyDictionary<int, string> DetectInstalls(IReadOnlyDictionary<int, string> known) => known;
        public int? DetectBuild(string wowDirectory) => VanillaBuild;
        public bool IsGameRunning() => false;
        public void SetRealmlist(string wowDirectory, string realmlistAddress) { }
        public void ConfigureClient(string wowDirectory, string locale, string realmlistAddress) { }
        public Task<GameLaunchResult> LaunchAsync(string wowExePath) =>
            Task.FromResult(new GameLaunchResult(false, null, "test"));
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

    private sealed class OfflineStatus : IServerStatusService
    {
        public Task<ServerStatusResult> CheckAsync(string host, int port = 3724, CancellationToken ct = default) =>
            Task.FromResult(new ServerStatusResult { Online = false, PlayerCount = 0 });
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

    private sealed class ExitNow : ILaunchExitPolicy
    {
        public Task<bool> ConfirmClientRunningAsync(string exePath) => Task.FromResult(false);
    }

    private sealed class NoPicker : IFolderPickerService
    {
        public Task<string?> PickFolderAsync(string title, string? startIn = null) => Task.FromResult<string?>(null);
    }

    private sealed class TempPaths(string root) : IAppPaths
    {
        public string ConfigDir => root;
        public string StateDir => root;
        public string CacheDir => root;
        public string LogDir => root;
        public string ShareDir => root;
        public string ConfigFilePath => Path.Combine(root, "launcher_config.json");
        public string NewsCacheFilePath => Path.Combine(root, "news-cache.json");
        public string ClientInstallDir(int gameBuild) => Path.Combine(root, $"WoW-Client-{gameBuild}");
        public string ClientDownloadZip(int gameBuild) => Path.Combine(root, $"WoW-Client-{gameBuild}.zip");
        public void EnsureDirectories() => Directory.CreateDirectory(root);
    }

    /// <summary>Das Manifest des Servers: drei Sprachpakete für 1.12.1, genau wie live.</summary>
    private static ServerManifest WithThreePacks() => new()
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
        LanguagePacks =
        [
            new ManifestLanguagePack { Locale = "deDE", Build = VanillaBuild, Url = "https://x.invalid/de.zip" },
            new ManifestLanguagePack { Locale = "esES", Build = VanillaBuild, Url = "https://x.invalid/es.zip" },
            new ManifestLanguagePack { Locale = "frFR", Build = VanillaBuild, Url = "https://x.invalid/fr.zip" },
        ],
    };

    private (PlayViewModel vm, LateManifest manifest) NewPlay(string clientDir)
    {
        var log = Serilog.Core.Logger.None;
        var manifest = new LateManifest();
        var paths = new TempPaths(_root);
        var vm = new PlayViewModel(new MemoryConfig(clientDir), manifest, new InstalledClient(clientDir),
            new OfflineStatus(), new NoDownload(), new ClientVerifyService(log), new NoUpdate(),
            new NoNews(), new ExitNow(), paths, new NoPicker(), log,
            languagePacks: new LanguagePackService(new NoDownload(), new VanillaLocalePacks(log), log));
        return (vm, manifest);
    }

    // ── Tests ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 Der Fall des Owners: der Client steht, das Manifest kommt später. Sobald es da ist, muss der
    /// Wähler erscheinen — ohne dass sich am Client noch etwas ändert.
    /// </summary>
    [Fact]
    public async Task EinSpaetErreichtesManifest_BringtDenSprachwaehlerZurueck()
    {
        var (vm, manifest) = NewPlay(EnglishOnlyClient());

        // Erster Durchlauf ohne Manifest: nur Englisch installiert, nichts angeboten.
        await vm.InitAsync();
        Assert.False(vm.HasLanguageChoice);

        // Das Manifest trifft ein. Am Client hat sich NICHTS geändert -- genau darum ging es.
        manifest.Current = WithThreePacks();
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.True(vm.HasLanguageChoice,
            "der Server bietet drei Pakete an, also gibt es etwas zu wählen");
        Assert.Equal(["enUS", "deDE", "frFR", "esES"], vm.AvailableLocales.Select(l => l.Code));
    }

    /// <summary>Die Gegenprobe: ohne angebotene Pakete bleibt der Wähler weg. Ein Auswahlfeld mit
    /// einem einzigen Eintrag behauptet eine Wahl, die es nicht gibt.</summary>
    [Fact]
    public async Task OhneAngeboteneSprachen_BleibtDerWaehlerWeg()
    {
        var (vm, manifest) = NewPlay(EnglishOnlyClient());
        manifest.Current = new ServerManifest { CurrentVersion = "1.0.0", ActivePhase = "vanilla" };

        await vm.InitAsync();

        Assert.False(vm.HasLanguageChoice);
        Assert.Single(vm.AvailableLocales);
    }
}
