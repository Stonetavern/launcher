namespace WowLauncher.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using WowLauncher.ViewModels;
using Xunit;

/// <summary>
/// Trockenlauf für die Frage: <b>wenn wir einen neuen 1.14.2-Client ausliefern (etwa mit einem
/// getauschten Übersetzer-Proxy) — bekommt ein installierter Launcher dieses Update überhaupt?</b>
///
/// <para>Geprüft wird die ganze Kette an ihrem eigenen Produktionscode, nicht an einer Nachbildung:
/// Signatur des ausgelieferten Manifests → Zulassungsregeln (serial/Kanal/Ablauf) → die
/// Zustandsentscheidung im <see cref="PlayViewModel"/>, die den Knopf auf „Aktualisieren" stellt.
/// Jede Feststellung hat ihre Gegenprobe: ein Test, der ohne den geprüften Mechanismus grün bliebe,
/// beweist nichts (CORE §3).</para>
///
/// <para><b>Die zwei Tests mit Netzzugriff laufen nur auf Ansage</b> (<c>ST_DRYRUN_NETWORK=1</c>).
/// Sie messen den echten Auslieferungsstand auf <c>downloads.stonetavern.app</c> — wertvoll für einen
/// Trockenlauf vor dem Ausliefern, aber nichts, was eine Testsuite bei jedem Lauf über das Netz
/// erzwingen darf.</para>
/// </summary>
public sealed class JimsProxyRolloutDryRunTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private const int ModernBuild = 42597;
    private const string ManifestUrl = "https://downloads.stonetavern.app/manifest.json";

    private static bool NetworkAllowed =>
        Environment.GetEnvironmentVariable("ST_DRYRUN_NETWORK") == "1";

    private readonly TempPaths _paths = new();
    public void Dispose() => _paths.Dispose();

    // ── 1) Die ausgelieferte Kette: Signatur ────────────────────────────────────────────────────

    /// <summary>
    /// Das Manifest, das heute ausgeliefert wird, trägt eine Signatur, die der eingebaute
    /// Release-Schlüssel des Launchers bestätigt. Ohne diesen Nachweis ist jede Aussage über ein
    /// künftiges Update wertlos: der Launcher lehnt ein unsigniertes Manifest ab, und ein neu
    /// veröffentlichtes muss neu signiert werden.
    ///
    /// <para>Die Gegenprobe steht im selben Test: ein einziges gekipptes Byte im Manifest muss die
    /// Prüfung rot machen. Bliebe sie grün, würde der Test nur beweisen, dass irgendetwas „ok" sagt.</para>
    /// </summary>
    [Fact]
    public async Task AusgeliefertesManifest_TraegtEineGueltigeSignatur()
    {
        if (!NetworkAllowed) { NotMeasured(); return; }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var manifestBytes = await http.GetByteArrayAsync(ManifestUrl);
        var signature = await http.GetStringAsync(ManifestUrl + ".sig");

        var gate = new ManifestSignature(ManifestSignature.EmbeddedPublicKeysBase64);

        var verdict = gate.Verify(manifestBytes, signature);
        Assert.True(verdict.Ok, $"Signatur des ausgelieferten Manifests: {verdict.Reason}");

        // Gegenprobe: ein verändertes Byte darf nicht durchgehen.
        var tampered = (byte[])manifestBytes.Clone();
        tampered[^2] ^= 0x01;
        Assert.False(gate.Verify(tampered, signature).Ok,
            "Ein verändertes Manifest wurde akzeptiert — die Prüfung misst nichts.");
    }

    /// <summary>
    /// Dieselbe Kette eine Stufe weiter: eine gültige Signatur beweist Herkunft, nicht Aktualität.
    /// Der Launcher lässt ein Manifest nur zu, wenn Kanal, Ablaufdatum und die laufende Nummer
    /// stimmen. Für den Trockenlauf ist die laufende Nummer der interessante Teil — sie muss beim
    /// Veröffentlichen mitwachsen.
    /// </summary>
    [Fact]
    public async Task AusgeliefertesManifest_WirdVonDerFreigaberegelZugelassen()
    {
        if (!NetworkAllowed) { NotMeasured(); return; }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var manifest = await http.GetFromJsonSafeAsync(ManifestUrl);
        Assert.NotNull(manifest);

        var store = new MemoryTrustStore();
        var policy = new ManifestReleasePolicy(store, Serilog.Core.Logger.None,
            channel: manifest!.Channel);

        var verdict = policy.Admit(manifest);
        Assert.True(verdict.Ok, $"Freigaberegel: {verdict.Reason}");
        Assert.Equal(manifest.Serial, store.Highest);

        // Gegenprobe: eine kleinere laufende Nummer wird nach dieser Annahme abgewiesen — genau der
        // Schutz, der ein altes, korrekt signiertes Manifest nicht noch einmal wirksam werden lässt.
        var replay = await http.GetFromJsonSafeAsync(ManifestUrl);
        replay!.Serial = (manifest.Serial ?? 0) - 1;
        Assert.False(policy.Admit(replay).Ok,
            "Ein zurückgedrehtes Manifest wurde zugelassen — der Rückspiel-Schutz misst nichts.");
    }

    /// <summary>
    /// Sagt im Testprotokoll laut, dass hier NICHT gemessen wurde. xunit 2.9.2 kennt kein
    /// Überspringen zur Laufzeit, und ein stilles Grün wäre genau die Sorte Befund, die keiner ist:
    /// „nicht gemessen" darf nie wie „geprüft" aussehen (CORE §3).
    /// </summary>
    private void NotMeasured([System.Runtime.CompilerServices.CallerMemberName] string test = "") =>
        output.WriteLine($"NICHT GEMESSEN: {test} braucht Netzzugriff — Lauf mit ST_DRYRUN_NETWORK=1 wiederholen.");

    // ── 2) Die Entscheidung im Launcher ─────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 Der eigentliche Trockenlauf: ein Spieler hat 1.3.1 über den Launcher installiert, das
    /// Manifest nennt 1.3.2 — der Launcher muss in den Zustand „Aktualisieren" gehen.
    /// </summary>
    [Fact]
    public async Task NeueClientVersionImManifest_WirdAlsUpdateErkannt()
    {
        var (vm, _) = NewPlay(manifestVersion: "1.3.2", installedVersion: "1.3.1");
        await vm.InitAsync();

        Assert.Equal(LauncherState.UpdateAvailable, vm.State);
    }

    /// <summary>
    /// Gegenprobe. Ohne sie würde der Test oben auch dann grün bleiben, wenn der Launcher schlicht
    /// immer „Aktualisieren" anzeigt: gleiche Version im Manifest wie installiert ⇒ spielbereit,
    /// kein Update-Angebot.
    /// </summary>
    [Fact]
    public async Task GleicheVersion_BleibtSpielbereit()
    {
        var (vm, _) = NewPlay(manifestVersion: "1.3.1", installedVersion: "1.3.1");
        await vm.InitAsync();

        Assert.Equal(LauncherState.Ready, vm.State);
    }

    /// <summary>
    /// 🟡 Die Lücke, die dieser Trockenlauf sichtbar machen soll — kein Fehler, eine bewusste
    /// Entscheidung (§6.3/C3 im <see cref="PlayViewModel"/>), aber eine mit Folgen für einen
    /// Proxy-Tausch: bei einem Client, den der Launcher NICHT selbst installiert hat, gibt es keine
    /// aufgezeichnete Version. Stimmt die Build-Nummer, gilt er als aktuell — ein neuer Client im
    /// Manifest erreicht diesen Spieler also nie von selbst.
    /// </summary>
    [Fact]
    public async Task SelbstMitgebrachterClient_BekommtKeinUpdateAngebot()
    {
        var (vm, _) = NewPlay(manifestVersion: "1.3.2", installedVersion: null);
        await vm.InitAsync();

        Assert.Equal(LauncherState.Ready, vm.State);
    }

    // ── Aufbau ──────────────────────────────────────────────────────────────────────────────────

    private (PlayViewModel vm, MemoryConfig cfg) NewPlay(string manifestVersion, string? installedVersion)
    {
        _paths.EnsureDirectories();
        var installDir = Path.Combine(_paths.Root, "modern-1.14.2");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "WowClassic.exe"), "exe");

        // Welcher Client-Build aktiv ist, entscheidet der gewählte Realm (RealmEntry.ClientKey) —
        // deshalb wird hier der Stonetavern-Realm auf 1.14.2 gestellt, statt einen Zustand zu setzen,
        // den es im Launcher gar nicht gibt.
        var realm = RealmRegistry.Presets()[0];
        realm.ClientKey = ClientVersion.ByBuild(ModernBuild)?.Key ?? "1.14.2";
        realm.ManifestUrl = ManifestUrl;

        var cfg = new MemoryConfig
        {
            Current = new LauncherConfig
            {
                ManifestUrl = ManifestUrl,
                RealmlistAddress = realm.RealmlistAddress,
                SelectedRealmId = realm.Id,
                Realms = [realm],
                ClientInstalls = new Dictionary<int, string> { [ModernBuild] = installDir },
                InstalledClientVersions = installedVersion is null
                    ? []
                    : new Dictionary<int, string> { [ModernBuild] = installedVersion },
            },
        };

        var log = Serilog.Core.Logger.None;
        var vm = new PlayViewModel(cfg, new FixedManifest(ManifestWithModern(manifestVersion)),
            new InstalledClient(installDir), new OfflineStatus(), new NoDownload(),
            new ClientVerifyService(log), new NoUpdate(), new NoNews(), new ExitNow(), _paths,
            new FixedFolderPicker(_paths.Root), log);
        return (vm, cfg);
    }

    /// <summary>Ein Manifest im Zuschnitt des echten: eine Phase, darin der 1.14.2-Client als eigener
    /// Eintrag. Ohne <c>os</c>, damit der Trockenlauf auf jeder Maschine dieselbe Frage stellt — die
    /// Plattform-Auswahl ist hier nicht der Prüfgegenstand.</summary>
    private static ServerManifest ManifestWithModern(string version) => new()
    {
        CurrentVersion = "1.12.1-enhanced",
        ActivePhase = "vanilla",
        Serial = 13,
        Channel = "stable",
        Expires = DateTimeOffset.UtcNow.AddDays(30).ToString("O"),
        Phases =
        [
            new PhaseManifest
            {
                Phase = "vanilla",
                Realmlist = "play.stonetavern.app",
                Client = new ManifestFile
                {
                    Version = "1.12.1-enhanced",
                    Url = "https://downloads.example.invalid/vanilla.zip",
                    Sha256 = new string('1', 64),
                },
                Clients =
                [
                    new ManifestFile
                    {
                        Build = ModernBuild,
                        Version = version,
                        Url = "https://downloads.example.invalid/modern-" + version + ".zip",
                        Size = 8_240_519_812,
                        Sha256 = new string('2', 64),
                    },
                ],
            },
        ],
    };

    // ── Attrappen ───────────────────────────────────────────────────────────────────────────────

    private sealed class MemoryTrustStore : IManifestTrustStore
    {
        public long Highest;
        public long? ReadHighestSerial() => Highest;
        public void Remember(long serial) { if (serial > Highest) Highest = serial; }
    }

    private sealed class MemoryConfig : IConfigService
    {
        public LauncherConfig Current = new();
        public LauncherConfig Load() => Current;
        public void Save(LauncherConfig config) => Current = config;
        public bool LastSaveSucceeded => true;
    }

    private sealed class FixedManifest(ServerManifest m) : IManifestService
    {
        public Task<ServerManifest?> FetchAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(m);
        public Task<ServerManifest?> FetchLauncherManifestAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(null);
        public Task<ClientFileManifest?> FetchFileManifestAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<ClientFileManifest?>(null);
    }

    /// <summary>Ein vorhandener 1.14.2-Client an einem bekannten Ort, mit passender Build-Nummer —
    /// derselbe Zustand, in dem ein Spieler nach einer Installation über den Launcher ist.</summary>
    private sealed class InstalledClient(string dir) : IClientService
    {
        private string Exe => Path.Combine(dir, "WowClassic.exe");
        public string? FindWowExe(string? configuredPath = null) => Exe;
        public string? FindWowExeForBuild(int gameBuild, IReadOnlyDictionary<int, string> installs) =>
            gameBuild == ModernBuild ? Exe : null;
        public IReadOnlyDictionary<int, string> DetectInstalls(IReadOnlyDictionary<int, string> known) => known;
        public int? DetectBuild(string wowDirectory) => ModernBuild;
        public bool IsGameRunning() => false;
        public void SetRealmlist(string wowDirectory, string realmlistAddress) { }
        public void ConfigureClient(string wowDirectory, string locale, string realmlistAddress) { }
        public Task<GameLaunchResult> LaunchAsync(string wowExePath) =>
            Task.FromResult(new GameLaunchResult(false, null, "dry run"));
    }

    private sealed class OfflineStatus : IServerStatusService
    {
        public Task<ServerStatusResult> CheckAsync(string host, int port = 3724, CancellationToken ct = default) =>
            Task.FromResult(new ServerStatusResult { Online = false, PlayerCount = 0 });
    }

    /// <summary>Lädt nichts: ein Trockenlauf, der acht Gigabyte zöge, wäre keiner.</summary>
    private sealed class NoDownload : IDownloadService
    {
        public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(DownloadResult.Fail(DownloadFailure.Network));
        public Task<bool> VerifyHashAsync(string path, string expectedSha256, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> ExtractZipAsync(string z, string d, IProgress<string>? p = null, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> ExtractClientAsync(string zipPath, string destDir,
            IProgress<string>? p = null, CancellationToken ct = default) => Task.FromResult(false);
    
        /// <summary>Pflichtteil der Schnittstelle: ohne Grund gilt der Fehlschlag als nicht behebbar,
        /// also als kaputtes Paket. Das ist die sichere Richtung fuer eine Attrappe.</summary>
        public async System.Threading.Tasks.Task<WowLauncher.Models.ExtractOutcome> ExtractClientWithReasonAsync(
            string zipPath, string destDir, bool freshInstall,
            System.IProgress<string>? progress = null,
            System.Threading.CancellationToken ct = default) =>
            await ExtractClientAsync(zipPath, destDir, progress, ct).ConfigureAwait(false)
                ? WowLauncher.Models.ExtractOutcome.Success
                : WowLauncher.Models.ExtractOutcome.Fail(WowLauncher.Models.ExtractFailure.Unknown);
    }

    private sealed class NoUpdate : IUpdateService
    {
        public event EventHandler? LauncherUpdateStarting { add { } remove { } }
        public Task<bool> CheckAndApplyAsync(ServerManifest? m, CancellationToken ct = default) =>
            Task.FromResult(false);
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
        private readonly string _root = Path.Combine(Path.GetTempPath(), "st-dryrun-" + Guid.NewGuid().ToString("N"));
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
        public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* Aufräumen */ } }
    }
}

/// <summary>Kleine Lesehilfe für die beiden Netz-Tests: dasselbe Deserialisieren, das der Launcher
/// selbst benutzt, damit der Trockenlauf nicht an einem zweiten JSON-Verständnis vorbeimisst.</summary>
internal static class DryRunHttpExtensions
{
    public static async Task<ServerManifest?> GetFromJsonSafeAsync(this HttpClient http, string url)
    {
        var json = await http.GetStringAsync(url);
        return System.Text.Json.JsonSerializer.Deserialize(json, ServerManifestContext.Default.ServerManifest);
    }
}
