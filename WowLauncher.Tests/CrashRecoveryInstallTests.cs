namespace WowLauncher.Tests;

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

/// <summary>
/// Nach einem Absturz wird nachgesehen, was schon da ist — nicht alles neu geholt.
///
/// <para><b>Der Fall.</b> Der Rechner stirbt zwischen „Archiv fertig geladen" und „Archiv entpackt".
/// Beim nächsten Start lag das vollständige, bereits geprüfte Archiv im Zwischenspeicher — und der
/// Launcher lud es trotzdem noch einmal, weil niemand gefragt hat. Bei einem Client dieser Größe ist
/// das der Unterschied zwischen einer Minute und einem Abend, auf einer Leitung, die gerade eben
/// bewiesen hat, dass sie abbrechen kann.</para>
///
/// <para><b>Was die Prüfung ist und was nicht.</b> Beweis ist der Hash, nicht die Anwesenheit der
/// Datei: eine halb geschriebene Datei liegt auf der Platte genauso da wie eine ganze. Passt sie
/// nicht zum Manifest, wird sie weggeworfen und normal geladen — entpackt wird sie nie.</para>
/// </summary>
public sealed class CrashRecoveryInstallTests : IDisposable
{
    private const string VanillaUrl = "https://downloads.example.invalid/client-5875.zip";
    private const string VanillaSha = "1111111111111111111111111111111111111111111111111111111111111111";
    private const int VanillaBuild = 5875;

    private readonly TempPaths _paths = new();
    public void Dispose() => _paths.Dispose();

    // ── Attrappen ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Zählt Downloads und lässt den Test bestimmen, ob ein Archiv als „passt zum Manifest"
    /// durchgeht. Beides muss getrennt steuerbar sein: die Frage ist ja gerade, ob eine gültige Datei
    /// den Download überflüssig macht.</summary>
    private sealed class CountingDownload : IDownloadService
    {
        public int DownloadCalls;
        public int HashChecks;
        public bool HashMatches = true;
        public string? ExtractedFrom;

        public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref DownloadCalls);
            // Ein echter Download hinterlässt eine Datei. Ohne sie könnte der Test nicht unterscheiden,
            // ob danach die geladene oder die vorgefundene Datei entpackt wurde.
            File.WriteAllText(destPath, "FRISCH GELADEN");
            return Task.FromResult(DownloadResult.Success);
        }

        public Task<bool> VerifyHashAsync(string path, string expectedSha256, CancellationToken ct = default)
        {
            Interlocked.Increment(ref HashChecks);
            return Task.FromResult(HashMatches);
        }

        public Task<bool> ExtractZipAsync(string z, string d, IProgress<string>? p = null, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> ExtractClientAsync(string zipPath, string destDir,
            IProgress<string>? p = null, CancellationToken ct = default)
        {
            ExtractedFrom = File.Exists(zipPath) ? File.ReadAllText(zipPath) : null;
            Directory.CreateDirectory(destDir);
            return Task.FromResult(true);
        }
    }

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
        /// <summary>Kein Launcher-Manifest in diesem Double: der Selbst-Update-Pfad ist hier
        /// nicht der Prüfgegenstand, und "keins" heißt "kein Update", nie "irgendeins".</summary>
        public Task<ServerManifest?> FetchLauncherManifestAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(null);

        public Task<ClientFileManifest?> FetchFileManifestAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<ClientFileManifest?>(null);
    }

    /// <summary>Findet vor dem Entpacken nichts und danach die frisch entpackte exe.</summary>
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

    private sealed class FixedFolderPicker(string dir) : IFolderPickerService
    {
        public Task<string?> PickFolderAsync(string title, string? startIn = null) =>
            Task.FromResult<string?>(dir);
    }

    private sealed class TempPaths : IAppPaths, IDisposable
    {
        public string Root => _root;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "st-crash-" + Guid.NewGuid().ToString("N"));
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
        public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* Aufräumen */ } }
    }

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

    private (PlayViewModel vm, CountingDownload dl) NewPlay()
    {
        var dl = new CountingDownload();
        var log = Serilog.Core.Logger.None;
        _paths.EnsureDirectories();
        var vm = new PlayViewModel(new MemoryConfig(), new FixedManifest(Manifest()), new InstallingClient(),
            new OfflineStatus(), dl, new ClientVerifyService(log), new NoUpdate(), new NoNews(),
            new ExitNow(), _paths, new FixedFolderPicker(_paths.Root), log);
        return (vm, dl);
    }

    // ── Tests ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>🔴 Der Fall aus dem Leben: das geprüfte Archiv liegt noch da, der Rechner ist beim
    /// Entpacken gestorben. Es wird nichts erneut geladen.</summary>
    [Fact]
    public async Task EinFertigesArchivImZwischenspeicher_WirdNichtNochEinmalGeladen()
    {
        var (vm, dl) = NewPlay();
        var zip = _paths.ClientDownloadZip(VanillaBuild);
        File.WriteAllText(zip, "SCHON DA");

        await vm.InitAsync();
        await vm.PlayCommand.ExecuteAsync(null);

        Assert.Equal(0, dl.DownloadCalls);
        Assert.Equal("SCHON DA", dl.ExtractedFrom);   // entpackt wurde das Vorgefundene
    }

    /// <summary>
    /// Und die Gegenprobe, ohne die der Test oben nur beweisen würde, dass irgendetwas übersprungen
    /// wird: passt das Vorgefundene nicht zum Manifest, wird es weggeworfen und normal geladen. Eine
    /// halb geschriebene Datei liegt auf der Platte genauso da wie eine ganze.
    /// </summary>
    [Fact]
    public async Task EinAngefangenesArchiv_WirdVerworfenUndNeuGeladen()
    {
        var (vm, dl) = NewPlay();
        dl.HashMatches = false;
        var zip = _paths.ClientDownloadZip(VanillaBuild);
        File.WriteAllText(zip, "HALB GESCHRIEBEN");

        await vm.InitAsync();
        await vm.PlayCommand.ExecuteAsync(null);

        Assert.Equal(1, dl.DownloadCalls);
        // Nicht entpackt wurde jedenfalls das kaputte Stück.
        Assert.NotEqual("HALB GESCHRIEBEN", dl.ExtractedFrom);
    }

    /// <summary>Ohne vorhandenes Archiv ändert sich nichts am gewohnten Weg — sonst hätte die
    /// Bestandsaufnahme den Normalfall mitverändert.</summary>
    [Fact]
    public async Task OhneVorhandenesArchiv_LaeuftAllesWieBisher()
    {
        var (vm, dl) = NewPlay();

        await vm.InitAsync();
        await vm.PlayCommand.ExecuteAsync(null);

        Assert.Equal(1, dl.DownloadCalls);
        Assert.Equal("FRISCH GELADEN", dl.ExtractedFrom);
    }

    /// <summary>
    /// Der teure Teil wird nicht doppelt gemacht: ein Archiv, das gerade gegen genau diesen Hash
    /// geprüft wurde, wird nicht noch einmal über mehrere Gigabyte gehasht. Das ist kein Feinschliff —
    /// es sind Minuten, in denen der Fortschrittsbalken stillsteht und der Spieler glaubt, es hängt.
    /// </summary>
    [Fact]
    public async Task EinBereitsGeprueftesArchiv_WirdNichtZweimalGehasht()
    {
        var (vm, dl) = NewPlay();
        File.WriteAllText(_paths.ClientDownloadZip(VanillaBuild), "SCHON DA");

        await vm.InitAsync();
        await vm.PlayCommand.ExecuteAsync(null);

        Assert.Equal(1, dl.HashChecks);
    }
}
