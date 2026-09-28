namespace WowLauncher.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Localization;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using WowLauncher.ViewModels;
using Xunit;

/// <summary>
/// Die Schleife, die der Spieler sieht: laden, scheitern, wieder laden.
///
/// <para><b>Belegt, nicht vermutet.</b> Vier unabhängige Meldungen zwischen dem 28.08. und dem
/// 04.09.2026 beschreiben dasselbe: der Client lädt bis 100 %, danach steht „Download Failed" ohne
/// Grund da, und beim nächsten Versuch geht alles von vorn los. Im Protokoll zu ST-KQYG-ARA3
/// (macOS, Launcher 1.8.0) steht dieselbe Mechanik aus der anderen Richtung: derselbe Vorgang
/// viermal in vier Minuten, jedes Mal ohne dass sich irgendetwas geändert hätte.</para>
///
/// <para><b>Drei Ursachen, alle hier festgenagelt.</b> (1) Das geprüfte Archiv wurde gelöscht, BEVOR
/// jemand nachsah, ob das Entpacken geklappt hat — der nächste Versuch musste also mehrere Gigabyte
/// erneut holen, um an derselben Stelle zu scheitern. (2) Die beiden Ausgänge nach dem Entpacken
/// setzten <c>State = DownloadError</c>, ohne irgendeinen Satz dazu zu setzen, und
/// <c>DownloadErrorDetail</c> wurde nirgends geleert — der Spieler las entweder gar nichts oder den
/// Satz des VORIGEN Fehlversuchs. (3) Für den Client-Download gab es überhaupt keine Obergrenze;
/// der <see cref="UpdateAttemptLedger"/> zählt ausschließlich den Selbst-Update des Launchers.</para>
///
/// <para>Alle Doubles sind erfunden; es kommen keine echten Spielerdaten vor.</para>
/// </summary>
public sealed class UpdateLoopExtractFailureTests : IDisposable
{
    private const string Url = "https://downloads.example.invalid/client-5875.zip";
    private const string Sha = "2222222222222222222222222222222222222222222222222222222222222222";
    private const int Build = 5875;

    /// <summary>Groesse des Testarchivs. Steht im Manifest UND ist das, was die Attrappe schreibt,
    /// damit die Zwischenspeicher-Erkennung (Laenge == Manifest-Groesse) ueberhaupt anspringt.</summary>
    private const int PayloadBytes = 4096;

    private readonly TempPaths _paths = new();
    public void Dispose() => _paths.Dispose();

    // ── Attrappen ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Zählt Downloads und Entpack-Versuche und lässt den Test bestimmen, ob das Entpacken
    /// gelingt. Beides getrennt, weil die Frage genau lautet: wie oft wird geladen, wenn das Entpacken
    /// scheitert.</summary>
    private sealed class ScriptedDownload : IDownloadService
    {
        public int DownloadCalls;
        public int ExtractCalls;

        /// <summary>Was das Entpacken meldet. Voreinstellung Erfolg; die Tests setzen hier den
        /// GRUND, weil genau er entscheidet, ob ein Fehlschlag das Budget verbraucht.</summary>
        public ExtractOutcome ExtractResult = ExtractOutcome.Success;
        public bool HashMatches = true;

        /// <summary>Ein Archiv in der Groesse, die im Manifest steht — sonst laeuft die
        /// Zwischenspeicher-Erkennung nie an und die Platzpruefung misst am Ziel vorbei.</summary>
        public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref DownloadCalls);
            File.WriteAllBytes(destPath, new byte[PayloadBytes]);
            return Task.FromResult(DownloadResult.Success);
        }

        public Task<bool> VerifyHashAsync(string path, string expectedSha256, CancellationToken ct = default) =>
            Task.FromResult(HashMatches);

        public Task<bool> ExtractZipAsync(string z, string d, IProgress<string>? p = null, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> ExtractClientAsync(string zipPath, string destDir,
            IProgress<string>? p = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref ExtractCalls);
            if (!ExtractResult.Ok) return Task.FromResult(false);
            Directory.CreateDirectory(destDir);
            File.WriteAllText(Path.Combine(destDir, "WoW.exe"), "MZ");
            return Task.FromResult(true);
        }

        public Task<bool> ExtractFreshClientAsync(string zipPath, string destDir,
            IProgress<string>? p = null, CancellationToken ct = default)
            => ExtractClientAsync(zipPath, destDir, p, ct);

        public async Task<ExtractOutcome> ExtractClientWithReasonAsync(string zipPath, string destDir,
            bool freshInstall, IProgress<string>? p = null, CancellationToken ct = default)
        {
            await ExtractClientAsync(zipPath, destDir, p, ct).ConfigureAwait(false);
            return ExtractResult;
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

    /// <summary>Das Manifest kann sich zwischen zwei Laeufen aendern — genau das ist der Fall
    /// "korrigiertes Paket unter derselben Versionsnummer".</summary>
    private sealed class MutableManifest(ServerManifest m) : IManifestService
    {
        public ServerManifest Current = m;
        public Task<ServerManifest?> FetchAsync(CancellationToken ct = default) => Task.FromResult<ServerManifest?>(Current);
        public Task<ServerManifest?> FetchLauncherManifestAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(null);
        public Task<ClientFileManifest?> FetchFileManifestAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<ClientFileManifest?>(null);
    }

    /// <summary>Findet eine Exe nur dort, wo wirklich eine liegt. <paramref name="blind"/> stellt den
    /// zweiten Ausgang nach: entpackt, aber im Ergebnis steht kein Spielprogramm.</summary>
    private sealed class DiskClient(bool blind = false) : IClientService
    {
        public string? FindWowExe(string? configuredPath = null)
        {
            if (blind || string.IsNullOrEmpty(configuredPath)) return null;
            var exe = Path.Combine(configuredPath, "WoW.exe");
            return File.Exists(exe) ? exe : null;
        }

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
        private readonly string _root = Path.Combine(Path.GetTempPath(), "st-loop-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Mit echter <c>Size</c>. Ohne sie ueberspringt <c>DownloadAsync</c> die Platzpruefung
    /// vollstaendig — und genau das hat die erste Fassung dieser Tests blind gemacht fuer die Frage,
    /// wieviel Platz ein behaltenes Archiv beim naechsten Versuch noch verlangt (Codex-Review
    /// 2026-09-14).</summary>
    private static ServerManifest Manifest(string sha = Sha) => new()
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
                    Version = "1.0.0", Url = Url, Sha256 = sha, Size = PayloadBytes,
                },
            },
        ],
    };

    private (PlayViewModel vm, ScriptedDownload dl, MutableManifest mf) NewPlayWithManifest(bool blindClient = false)
    {
        var dl = new ScriptedDownload();
        var mf = new MutableManifest(Manifest());
        var log = Serilog.Core.Logger.None;
        _paths.EnsureDirectories();
        var vm = new PlayViewModel(new MemoryConfig(), mf, new DiskClient(blindClient),
            new OfflineStatus(), dl, new ClientVerifyService(log), new NoUpdate(), new NoNews(),
            new ExitNow(), _paths, new FixedFolderPicker(_paths.Root), log);
        return (vm, dl, mf);
    }

    private (PlayViewModel vm, ScriptedDownload dl) NewPlay(bool blindClient = false)
    {
        var (vm, dl, _) = NewPlayWithManifest(blindClient);
        return (vm, dl);
    }

    // ── Die Schleife selbst ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 Der Kern: ein gescheitertes Entpacken wirft das geprüfte Archiv NICHT weg, und der zweite
    /// Versuch lädt deshalb nichts erneut.
    ///
    /// <para>Ohne den Fix stand <c>File.Delete(zipPath)</c> vor der Erfolgsprüfung. Dann ist die
    /// Datei weg, der nächste Anlauf lädt wieder mehrere Gigabyte — und scheitert an derselben
    /// Stelle. Das ist die Schleife aus „repeatedly downloads and fails to extract", Wort für Wort.</para>
    /// </summary>
    [Fact]
    public async Task EinGescheitertesEntpacken_BehaeltDasGeprueftArchiv_UndLaedtNichtNochEinmal()
    {
        var (vm, dl) = NewPlay();
        dl.ExtractResult = ExtractOutcome.Fail(ExtractFailure.BadArchive);
        var zip = _paths.ClientDownloadZip(Build);

        await vm.InitAsync();
        await vm.PlayCommand.ExecuteAsync(null);

        Assert.Equal(1, dl.DownloadCalls);
        Assert.True(File.Exists(zip), "Das geprüfte Archiv muss nach einem gescheiterten Entpacken liegen bleiben.");

        await vm.PlayCommand.ExecuteAsync(null);

        Assert.Equal(1, dl.DownloadCalls);   // der zweite Versuch nimmt das vorhandene Archiv
        Assert.Equal(2, dl.ExtractCalls);    // versucht wurde es trotzdem noch einmal
    }

    /// <summary>Die Gegenprobe, ohne die der Test oben auch bei einem Launcher grün wäre, der gar
    /// nichts mehr tut: gelingt das Entpacken, wird das Archiv wie bisher weggeräumt.</summary>
    [Fact]
    public async Task EinGelungenesEntpacken_RaeumtDasArchivWieBisherWeg()
    {
        var (vm, dl) = NewPlay();
        var zip = _paths.ClientDownloadZip(Build);

        await vm.InitAsync();
        await vm.PlayCommand.ExecuteAsync(null);

        Assert.Equal(1, dl.ExtractCalls);
        Assert.False(File.Exists(zip), "Nach einem erfolgreichen Entpacken hat das Archiv keinen Zweck mehr.");
        Assert.Equal(LauncherState.Ready, vm.State);
    }

    // ── Der Satz, den der Spieler liest ──────────────────────────────────────────────────────────

    /// <summary>🔴 Ein gescheitertes Entpacken sagt, dass es das Entpacken war. Vorher setzte dieser
    /// Ausgang <c>State = DownloadError</c> und sonst nichts — die Anzeige fiel auf „Download
    /// fehlgeschlagen" zurück, und genau mit diesem Satz fangen die Meldungen an.</summary>
    [Fact]
    public async Task EinGescheitertesEntpacken_SagtWarum()
    {
        var (vm, dl) = NewPlay();
        dl.ExtractResult = ExtractOutcome.Fail(ExtractFailure.BadArchive);

        await vm.InitAsync();
        await vm.PlayCommand.ExecuteAsync(null);

        Assert.Equal(LauncherState.DownloadError, vm.State);
        Assert.Equal(Loc.T("Play_Error_ExtractBadArchive"), vm.DownloadErrorDetail);
        Assert.NotEqual("", vm.DownloadErrorDetail);
    }

    /// <summary>Kennt der Extraktor den Grund nicht - genau das melden Attrappen und aeltere
    /// Doubles ueber die Standard-Implementierung der Schnittstelle -, bleibt der allgemeine Satz.
    /// Ohne diese Zeile koennte die Zuordnung Grund-zu-Satz still ins Leere laufen.</summary>
    [Fact]
    public async Task EinUnbekannterGrund_BekommtDenAllgemeinenSatz()
    {
        var (vm, dl) = NewPlay();
        dl.ExtractResult = ExtractOutcome.Fail(ExtractFailure.Unknown);

        await vm.InitAsync();
        await vm.PlayCommand.ExecuteAsync(null);

        Assert.Equal(Loc.T("Play_Error_ExtractFailed"), vm.DownloadErrorDetail);
    }

    /// <summary>🔴 Produktionslauf 2026-09-24: warf die Patch-Engine (Fremddatei-Bericht, Wine-Symlink
    /// nach /), las der Spieler „unpacking failed" - entpackt wurde gar nichts. Eine Ausnahme der Engine
    /// bekommt einen eigenen Satz, in jeder Sprache vorhanden und verschieden vom Entpack-Satz.</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void EineAusnahmeDerPatchEngine_SagtNichtEntpacken(string lang)
    {
        var root = FindRepoLauncherDir();
        var vm = File.ReadAllText(Path.Combine(root, "WowLauncher", "ViewModels", "PlayViewModel.cs"));
        var at = vm.IndexOf("patch: engine threw for build", StringComparison.Ordinal);
        Assert.True(at > 0, "catch-Block der Patch-Engine nicht gefunden");
        var block = vm.Substring(at, 200);
        Assert.Contains("Loc.T(\"Play_Error_PatchFailed\")", block);
        Assert.DoesNotContain("Play_Error_ExtractFailed", block);

        var file = Path.Combine(root, "WowLauncher", "Localization", "lang", lang + ".json");
        var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file)).RootElement;
        var text = doc.GetProperty("Play_Error_PatchFailed").GetString();
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.NotEqual(doc.GetProperty("Play_Error_ExtractFailed").GetString(), text);
    }

    private static string FindRepoLauncherDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "WowLauncher", "Localization")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("launcher repo root");
    }

    /// <summary>Der zweite wortlose Ausgang: entpackt, aber im Ergebnis liegt kein Spielprogramm.</summary>
    [Fact]
    public async Task EntpacktOhneSpielprogramm_SagtEbenfallsWarum()
    {
        var (vm, _) = NewPlay(blindClient: true);

        await vm.InitAsync();
        await vm.PlayCommand.ExecuteAsync(null);

        Assert.Equal(LauncherState.DownloadError, vm.State);
        Assert.Equal(Loc.T("Play_Error_NoExeAfterExtract"), vm.DownloadErrorDetail);
    }

    /// <summary>
    /// 🔴 Ein Fehlersatz überlebt seinen Anlass nicht.
    ///
    /// <para><c>DownloadErrorDetail</c> wurde nirgends geleert — als einziges der drei Detailfelder.
    /// Wer einmal zu wenig Plattenplatz hatte, las diesen Satz danach bei JEDEM weiteren Fehler, so
    /// lange der Launcher offen blieb. So wird aus einem verschluckten Entpack-Fehler die Meldung
    /// „sagt kein Speicherplatz, obwohl Platz frei ist".</para>
    /// </summary>
    [Fact]
    public async Task EinAlterFehlersatz_UeberlebtDenNaechstenVersuchNicht()
    {
        var (vm, dl) = NewPlay();
        dl.HashMatches = false;

        await vm.InitAsync();
        await vm.PlayCommand.ExecuteAsync(null);
        Assert.Equal(LauncherState.DownloadError, vm.State);
        var ersterSatz = vm.DownloadErrorDetail;
        Assert.NotEqual("", ersterSatz);

        dl.HashMatches = true;
        await vm.PlayCommand.ExecuteAsync(null);

        Assert.Equal(LauncherState.Ready, vm.State);
        Assert.Equal("", vm.DownloadErrorDetail);
    }

    // ── Die Obergrenze ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 Nach drei vergeblichen Anläufen hört der Launcher von selbst auf und sagt, woran es lag.
    ///
    /// <para>Für den Client-Download gab es bis dahin keine Obergrenze. Der
    /// <see cref="UpdateAttemptLedger"/> deckt diesen Weg nicht ab — er zählt den Selbst-Update des
    /// Launchers und schreibt dafür auf Platte, weil der Launcher zwischen zwei Versuchen beendet
    /// ist. Hier läuft er weiter, die Schleife passiert in einer Sitzung, also zählt die Sitzung.</para>
    /// </summary>
    [Fact]
    public async Task NachDreiVergeblichenAnlaeufen_WirdNichtWiederEntpackt()
    {
        var (vm, dl) = NewPlay();
        dl.ExtractResult = ExtractOutcome.Fail(ExtractFailure.BadArchive);

        await vm.InitAsync();
        for (var i = 0; i < 6; i++)
            await vm.PlayCommand.ExecuteAsync(null);

        Assert.Equal(PlayViewModel.MaxInstallAttempts, dl.ExtractCalls);
        Assert.Equal(LauncherState.DownloadError, vm.State);
        Assert.Contains(Loc.T("Play_Error_ExtractBadArchive"), vm.DownloadErrorDetail, StringComparison.Ordinal);

        // Aufgeben raeumt auf: das behaltene Archiv nuetzt keinem Versuch mehr und darf nicht als
        // mehrere Gigabyte Muell liegenbleiben - erst recht nicht auf einer knappen Platte.
        Assert.False(File.Exists(_paths.ClientDownloadZip(Build)),
            "Beim endgueltigen Aufgeben muss der Zwischenspeicher freigegeben werden.");
        Assert.Contains(Loc.T("Play_Error_CacheFreed"), vm.DownloadErrorDetail, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔴 Eine volle Platte verbraucht KEINEN Versuch.
    ///
    /// <para>Der Extraktor meldete Abbruch, Plattenmangel, gehaltene Datei, Rechtefehler und kaputtes
    /// Archiv alle als dasselbe <c>false</c>. Mit einem Zaehler darauf waere ein Spieler, der nach dem
    /// dritten Fehlschlag Platz schafft, ausgesperrt gewesen - der Schutz haette den Falschen
    /// bestraft (Codex-Review 2026-09-14).</para>
    /// </summary>
    [Theory]
    [InlineData(ExtractFailure.DiskFull)]
    [InlineData(ExtractFailure.FileLocked)]
    [InlineData(ExtractFailure.AccessDenied)]
    public async Task EinBehebbarerUmgebungsfehler_VerbrauchtKeinenVersuch(ExtractFailure failure)
    {
        var (vm, dl) = NewPlay();
        dl.ExtractResult = ExtractOutcome.Fail(failure);

        await vm.InitAsync();
        for (var i = 0; i < 6; i++)
            await vm.PlayCommand.ExecuteAsync(null);

        Assert.Equal(6, dl.ExtractCalls);   // nie gesperrt
        Assert.Equal(LauncherState.DownloadError, vm.State);
        Assert.DoesNotContain(Loc.T("Play_Error_CacheFreed"), vm.DownloadErrorDetail, StringComparison.Ordinal);
        Assert.True(File.Exists(_paths.ClientDownloadZip(Build)),
            "Ein behebbarer Umgebungsfehler darf das gepruefte Archiv nicht wegwerfen.");
    }

    /// <summary>Und die Gegenprobe zur Gegenprobe: ein wirklich kaputtes Paket wird sehr wohl
    /// gezaehlt. Ohne sie wuerde der Test darueber auch bei einem abgeschalteten Zaehler gruen.</summary>
    [Fact]
    public async Task EinKaputtesArchiv_VerbrauchtSehrWohlVersuche()
    {
        var (vm, dl) = NewPlay();
        dl.ExtractResult = ExtractOutcome.Fail(ExtractFailure.BadArchive);

        await vm.InitAsync();
        for (var i = 0; i < 6; i++)
            await vm.PlayCommand.ExecuteAsync(null);

        Assert.Equal(PlayViewModel.MaxInstallAttempts, dl.ExtractCalls);
    }

    /// <summary>
    /// 🔴 Ein korrigiertes Paket unter DERSELBEN Versionsnummer bekommt ein frisches Budget.
    ///
    /// <para>Der Zaehler haengt an den Koordinaten des Artefakts (Build + SHA256), nicht am
    /// Versionsstring. Sonst bliebe die Sperre ueber genau der Auslieferung stehen, die den Fehler
    /// behebt - und der Spieler muesste den Launcher neu starten, um einen Fix zu bekommen, von dem
    /// er nichts weiss.</para>
    /// </summary>
    [Fact]
    public async Task EinKorrigiertesPaketUnterGleicherVersion_BekommtEinFrischesBudget()
    {
        var (vm, dl, mf) = NewPlayWithManifest();
        dl.ExtractResult = ExtractOutcome.Fail(ExtractFailure.BadArchive);

        await vm.InitAsync();
        for (var i = 0; i < 5; i++)
            await vm.PlayCommand.ExecuteAsync(null);
        Assert.Equal(PlayViewModel.MaxInstallAttempts, dl.ExtractCalls);

        // Der Betreiber legt ein repariertes Paket unter derselben Version nach.
        mf.Current = Manifest(sha: "3333333333333333333333333333333333333333333333333333333333333333");
        dl.ExtractResult = ExtractOutcome.Success;
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        await vm.PlayCommand.ExecuteAsync(null);

        Assert.Equal(PlayViewModel.MaxInstallAttempts + 1, dl.ExtractCalls);
        Assert.Equal(LauncherState.Ready, vm.State);
    }

    // ── Platz: die Rechnung, die das behaltene Archiv nicht gegen sich selbst wendet ─────────────

    /// <summary>
    /// 🔴 Liegt das Archiv schon da, darf sein eigener Platz nicht noch einmal verlangt werden.
    ///
    /// <para>Die Sperre verlangte <c>size * 2,2 + 500 MB</c>, auch wenn das Archiv bereits auf der
    /// Platte lag und selbst <c>size</c> davon belegte. Beim macOS-Paket sind das 18,4 GB gegen 8,3 GB,
    /// die schon liegen: der billige zweite Versuch konnte an genau der Sperre scheitern, die er
    /// umgehen sollte.</para>
    /// </summary>
    [Fact]
    public void EinVorhandenesArchiv_SenktDenVerlangtenPlatzUmSeineEigeneGroesse()
    {
        const long size = 8_300_000_000;

        var fresh = PlayViewModel.RequiredFreeBytes(size, archiveAlreadyCached: false);
        var cached = PlayViewModel.RequiredFreeBytes(size, archiveAlreadyCached: true);

        Assert.True(cached < fresh, $"cached={cached} fresh={fresh}");
        // Und zwar um genau eine Archivgroesse: wird das Archiv doch verworfen, wird exakt so viel
        // frei, dass die volle Rechnung wieder aufgeht. Die gesenkte Pruefung kann den Launcher also
        // nie in einen Download lassen, fuer den der Platz nicht reicht.
        Assert.Equal(fresh, cached + size);
    }

    /// <summary>Gegenprobe: ohne Archiv bleibt die Anforderung, was sie war - Download plus
    /// entpackter Baum plus Reserve. Ohne diese Zeile wuerde der Test darueber auch bei einer
    /// Rechnung gruen, die gar keinen Platz mehr verlangt.</summary>
    [Fact]
    public void OhneArchiv_BleibtDieAnforderungDieAlte()
    {
        const long size = 1_000_000_000;
        Assert.Equal((long)(size * 2.2) + 500L * 1024 * 1024,
            PlayViewModel.RequiredFreeBytes(size, archiveAlreadyCached: false));
    }

    /// <summary>
    /// 🔴 Download und Entpacken landen nicht zwangslaeufig auf demselben Datentraeger — dann darf
    /// nicht beides vom selben Laufwerk verlangt werden.
    ///
    /// <para>Das Archiv geht immer in den Zwischenspeicher, der entpackte Baum dorthin, wo der Spieler
    /// den Client haben will; bei 18 GB ist das typischerweise eine zweite Platte. Bis 2026-09-14
    /// forderte die Sperre beide Betraege auf dem Laufwerk des Installationsordners — dort also rund
    /// eine Archivgroesse zu viel, was Spieler mit reichlich Platz abwies.</para>
    /// </summary>
    [Fact]
    public void ZweiLaufwerke_VerlangenNichtBeideDenDownload()
    {
        const long size = 8_300_000_000;

        var together  = PlayViewModel.RequiredFreeBytes(size, archiveAlreadyCached: false, cacheOnSameVolume: true);
        var installOnly = PlayViewModel.RequiredFreeBytes(size, archiveAlreadyCached: false, cacheOnSameVolume: false);

        // Getrennt gerechnet bleibt auf dem Installationslaufwerk genau der entpackte Baum uebrig …
        Assert.Equal(PlayViewModel.UnpackBudget(size), installOnly);
        // … und die Differenz ist exakt der Download, der dort gar nicht stattfindet.
        Assert.Equal(together, installOnly + size);
    }

    /// <summary>Gegenprobe: auf EINEM Laufwerk aendert sich nichts an der alten Rechnung — sonst
    /// wuerde der Test darueber auch bei einer Formel gruen, die generell weniger verlangt.</summary>
    [Fact]
    public void EinLaufwerk_BleibtDieAlteSumme()
    {
        const long size = 1_000_000_000;
        Assert.Equal(PlayViewModel.RequiredFreeBytes(size, archiveAlreadyCached: false),
            PlayViewModel.RequiredFreeBytes(size, archiveAlreadyCached: false, cacheOnSameVolume: true));
        Assert.Equal((long)(size * 2.2) + 500L * 1024 * 1024,
            PlayViewModel.RequiredFreeBytes(size, archiveAlreadyCached: false, cacheOnSameVolume: true));
    }

    /// <summary>Auf getrennten Laufwerken ist der Bedarf des Installationsordners vom Archiv
    /// unabhaengig: dort liegt es nicht, und sein Loeschen macht dort nichts frei. Genau deshalb
    /// loescht <see cref="PlayViewModel.PlanShortfall"/> ueber die Grenze hinweg auch nicht.</summary>
    [Fact]
    public void AufGetrenntenLaufwerken_AendertDasArchivDenInstallationsbedarfNicht()
    {
        const long size = 8_300_000_000;

        Assert.Equal(
            PlayViewModel.RequiredFreeBytes(size, archiveAlreadyCached: false, cacheOnSameVolume: false),
            PlayViewModel.RequiredFreeBytes(size, archiveAlreadyCached: true,  cacheOnSameVolume: false));

        // Und die genannte Zahl gilt auch beim naechsten Klick, weil nichts geloescht wurde.
        Assert.Equal(
            PlayViewModel.RequiredFreeBytes(size, archiveAlreadyCached: true, cacheOnSameVolume: false),
            PlayViewModel.RequirementAfterCleanup(size, archiveCached: true, cacheFreed: false,
                cacheOnSameVolume: false));
    }

    /// <summary>Der Betrag, den das Laufwerk des Zwischenspeichers tragen muss: die Bytes, die noch
    /// fliessen, plus dieselbe feste Reserve. Liegt das Archiv schon da, ist er null — darum wird
    /// dieser Zweig dann gar nicht erst gemessen.</summary>
    [Fact]
    public void DerZwischenspeicher_TraegtNurWasNochFliesst()
    {
        const long size = 8_300_000_000;

        Assert.Equal(size, PlayViewModel.DownloadBudget(size, archiveAlreadyCached: false));
        Assert.Equal(0,    PlayViewModel.DownloadBudget(size, archiveAlreadyCached: true));

        // Die beiden Budgets zusammen sind die alte Gesamtrechnung — keine Luecke, keine Doppelung.
        Assert.Equal(PlayViewModel.RequiredFreeBytes(size, archiveAlreadyCached: false),
            PlayViewModel.DownloadBudget(size, false) + PlayViewModel.UnpackBudget(size));
    }

    /// <summary>
    /// 🔴 Das Laufwerk, auf dem das Archiv wirklich landet, wurde bis 2026-09-14 nie gemessen.
    ///
    /// <para>Liegt der Client auf einer geraeumigen zweiten Platte, waehrend die Systemplatte mit dem
    /// Zwischenspeicher voll ist, lief der Transfer minutenlang und endete an einer Wand, vor der die
    /// Vorpruefung haette stehen sollen. Die Abweisung nennt den Bedarf, damit der Spieler weiss, was
    /// er dort frei machen muss.</para>
    /// </summary>
    [Fact]
    public void DasLaufwerkDesZwischenspeichers_WirdGemessen_WennEsEinAnderesIst()
    {
        const long size = 8_300_000_000;
        var need = size + PlayViewModel.DiskHeadroom;

        // Zu wenig -> Abweisung, und zwar mit genau dem Betrag, der dort gebraucht wird.
        Assert.Equal(need, PlayViewModel.CacheShortfall(size, archiveCached: false,
            separateVolumes: true, availableOnCacheVolume: need - 1));

        // Genug -> kein Grund zur Abweisung.
        Assert.Null(PlayViewModel.CacheShortfall(size, archiveCached: false,
            separateVolumes: true, availableOnCacheVolume: need));
    }

    /// <summary>Gegenproben, die den Zweig genau dort schweigen lassen, wo er sonst doppelt
    /// verlangen wuerde: auf EINEM Laufwerk hat die Pruefung des Installationsordners den Download
    /// schon mitgefordert, und mit vollstaendigem Archiv fliesst ueberhaupt nichts mehr.</summary>
    [Theory]
    [InlineData(false, false)]  // ein Laufwerk, kein Archiv    -> die andere Pruefung deckt es ab
    [InlineData(true,  true)]   // getrennt, Archiv liegt da    -> nichts fliesst mehr
    [InlineData(false, true)]
    public void DerZwischenspeicherZweig_SchweigtWoErNichtsZuSagenHat(bool separateVolumes, bool archiveCached)
    {
        Assert.Null(PlayViewModel.CacheShortfall(8_300_000_000, archiveCached, separateVolumes,
            availableOnCacheVolume: 0));
    }

    // ── Und dass das Aufraeumen nicht ueber Datentraegergrenzen hinweg loescht ───────────────────

    /// <summary>
    /// 🔴 Loeschen hilft nur auf dem Laufwerk, das gemessen wurde.
    ///
    /// <para>Der Zwischenspeicher liegt in <c>CacheDir</c>, der Installationsordner dort, wo der
    /// Spieler ihn hingelegt hat - bei einem 18-GB-Client auf einer zweiten Platte sind das
    /// verschiedene Mounts. Ein Loeschen darueber hinweg gibt auf dem gemessenen Laufwerk NULL Bytes
    /// frei und vernichtet das geprüfte Mehr-Gigabyte-Archiv: die teure Schleife waere wiederhergestellt,
    /// und zwar genau bei den Spielern mit knapper Platte (Review 2026-09-14).</para>
    /// </summary>
    [Theory]
    [InlineData(true,  true,  true,  false)]   // Archiv da, gleiches Laufwerk  -> loeschen
    [InlineData(true,  false, false, true)]    // Archiv da, anderes Laufwerk   -> behalten, hinweisen
    [InlineData(false, true,  false, false)]   // kein Archiv                   -> nichts zu tun
    [InlineData(false, false, false, false)]
    public void DerAufraeumplan_LoeschtNurWoEsEtwasBringt(
        bool archiveCached, bool sameVolume, bool expectFree, bool expectPointElsewhere)
    {
        var plan = PlayViewModel.PlanShortfall(archiveCached, sameVolume);

        Assert.Equal(expectFree, plan.FreeTheCache);
        Assert.Equal(expectPointElsewhere, plan.PointAtOtherDrive);
    }

    /// <summary>
    /// 🔴 Die genannte Zahl muss die sein, gegen die der NAECHSTE Klick rechnet.
    ///
    /// <para>Vorher wurde die mit Archiv gerechnete Menge genannt und danach das Archiv geloescht:
    /// der Spieler schaffte exakt das Genannte frei und wurde mit einer groesseren Zahl erneut
    /// abgewiesen. Dieselbe Fehlerklasse wie der stehengebliebene Fehlersatz.</para>
    /// </summary>
    [Fact]
    public void DieGenannteMenge_GiltAuchNochBeimNaechstenKlick()
    {
        const long size = 8_300_000_000;

        // Geloescht -> beim naechsten Mal gibt es keinen Zwischenspeicher mehr.
        Assert.Equal(PlayViewModel.RequiredFreeBytes(size, archiveAlreadyCached: false),
            PlayViewModel.RequirementAfterCleanup(size, archiveCached: true, cacheFreed: true));

        // Behalten (anderes Laufwerk) -> beim naechsten Mal gibt es ihn noch.
        Assert.Equal(PlayViewModel.RequiredFreeBytes(size, archiveAlreadyCached: true),
            PlayViewModel.RequirementAfterCleanup(size, archiveCached: true, cacheFreed: false));

        // Und ohne Archiv aendert das Aufraeumen ueberhaupt nichts.
        Assert.Equal(PlayViewModel.RequiredFreeBytes(size, archiveAlreadyCached: false),
            PlayViewModel.RequirementAfterCleanup(size, archiveCached: false, cacheFreed: false));
    }

    /// <summary>Ein unbrauchbares Ziel (fehlendes Verzeichnis, unmoeglicher Name) ist KEIN behebbarer
    /// Umgebungsfehler: es heilt nicht von selbst, also muss es einen Versuch verbrauchen - sonst
    /// wiederholt der Launcher es unbegrenzt und raet dabei falsch, das Spiel zu schliessen.</summary>
    [Fact]
    public async Task EinUnbrauchbaresZiel_VerbrauchtVersuche_UndSagtEtwasAnderes()
    {
        var (vm, dl) = NewPlay();
        dl.ExtractResult = ExtractOutcome.Fail(ExtractFailure.TargetUnusable);

        await vm.InitAsync();
        for (var i = 0; i < 6; i++)
            await vm.PlayCommand.ExecuteAsync(null);

        Assert.Equal(PlayViewModel.MaxInstallAttempts, dl.ExtractCalls);
        Assert.Contains(Loc.T("Play_Error_ExtractTargetUnusable"), vm.DownloadErrorDetail, StringComparison.Ordinal);
    }

    /// <summary>Gegenprobe zur Obergrenze: sie greift nur nach echten Fehlversuchen. Ein Lauf, der
    /// durchgeht, verbraucht nichts — sonst hätte ein Zähler den Normalfall mitbeschädigt.</summary>
    [Fact]
    public async Task EinGelungenerLauf_VerbrauchtKeinenVersuch()
    {
        var (vm, dl) = NewPlay();

        await vm.InitAsync();
        await vm.PlayCommand.ExecuteAsync(null);
        Assert.Equal(LauncherState.Ready, vm.State);

        // Ein zweiter Anlauf auf denselben Build muss weiterhin arbeiten dürfen.
        vm.State = LauncherState.UpdateAvailable;
        await vm.PlayCommand.ExecuteAsync(null);

        Assert.Equal(2, dl.ExtractCalls);
        Assert.Equal(LauncherState.Ready, vm.State);
    }
}
