namespace WowLauncher.Tests;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using WowLauncher.ViewModels;
using Xunit;

/// <summary>
/// Welche Fassung ein Spieler hat, welche angeboten wird, und wann das zuletzt jemand erfahren hat.
///
/// <para><b>Wogegen das gebaut ist.</b> Ein Launcher, der den Update-Server nicht erreicht, sieht
/// nicht kaputt aus - er sieht fertig aus. Kein Fehler, kein Banner, nichts Rotes, und er startet das
/// Spiel monatelang tadellos, waehrend jede Fehlerbehebung an ihm vorbeigeht. Am 2026-08-05 war live
/// 1.6.4, gebaut 1.7.4 und die Download-Seite bot 1.5.0 an: drei Antworten auf eine Frage, und kein
/// Spieler konnte eine davon sehen.</para>
///
/// <para>Der wichtigste Test in dieser Datei ist <see cref="EineAntwortOhneInhalt_GiltNichtAlsPruefung"/>.
/// Ein Server, der 200 und Unbrauchbares liefert (die Anmeldeseite eines Hotel-WLANs, eine
/// abgeschnittene Datei), darf keine frische Pruefung hinterlassen - sonst behauptet die Anzeige
/// gerade dann Aktualitaet, wenn gar nichts geprueft wurde.</para>
/// </summary>
public sealed class ReleaseStatusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "st-release-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    public ReleaseStatusTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    // ── Das Urteil ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1.5.0", "1.7.4", true, false)]   // der Spieler haengt hinterher
    [InlineData("1.7.4", "1.5.0", false, true)]   // der Server haengt hinterher: jeder eigene Bau
    [InlineData("1.7.4", "1.7.4", false, false)]
    [InlineData("1.7", "1.7.0", false, false)]    // dieselbe Fassung, anders geschrieben
    public void DieBeidenSeiten_WerdenUnterschieden(string running, string offered, bool behind, bool ahead)
    {
        var f = ReleaseStatus.Evaluate(running, offered, Now, Now);

        Assert.Equal(behind, f.Behind);
        Assert.Equal(ahead, f.Ahead);
        Assert.Equal(!behind && !ahead, f.UpToDate);
    }

    /// <summary>
    /// „Noch nie geprueft" ist NICHT „aktuell". Das eine ist Wissen, das andere dessen Abwesenheit,
    /// und nur das zweite ist ein Grund, ins Netz zu schauen. Ohne die Unterscheidung liest ein
    /// Spieler ohne Netzverbindung, er sei auf dem neuesten Stand.
    /// </summary>
    [Fact]
    public void NochNieGeprueft_IstNichtDasselbeWieAktuell()
    {
        var never = ReleaseStatus.Evaluate("1.5.0", null, null, Now);
        var current = ReleaseStatus.Evaluate("1.5.0", "1.5.0", Now, Now);

        Assert.True(never.NeverChecked);
        Assert.False(never.UpToDate);
        Assert.True(current.UpToDate);
        Assert.False(current.NeverChecked);
    }

    /// <summary>Eine Pruefung, die glueckte, aber keine Fassung nannte, ist ein eigener Fall - und
    /// keine Luecke in der Anzeige.</summary>
    [Fact]
    public void EinePruefungOhneFassung_HatIhrenEigenenFall()
    {
        var f = ReleaseStatus.Evaluate("1.7.4", "", Now, Now);

        Assert.True(f.OfferUnknown);
        Assert.False(f.NeverChecked);
        Assert.False(f.UpToDate);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(6, false)]
    [InlineData(7, true)]
    [InlineData(60, true)]
    public void EineAlteAuskunft_GiltAlsAlt(int daysAgo, bool stale)
    {
        var f = ReleaseStatus.Evaluate("1.7.4", "1.7.4", Now.AddDays(-daysAgo), Now);

        Assert.Equal(daysAgo, f.AgeDays);
        Assert.Equal(stale, ReleaseStatus.IsStale(f));
    }

    // ── Das Protokoll ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Die Erreichbarkeit und die angebotene Fassung kommen von zwei verschiedenen Stellen. Ein
    /// Eintrag der einen darf die andere nicht loeschen - die erste Fassung dieser Klasse ersetzte
    /// bei jedem Schreiben den ganzen Eintrag, und ein Abruf ohne Angebot haette das letzte bekannte
    /// Angebot stillschweigend geloescht.
    /// </summary>
    [Fact]
    public void EinNeuerAbruf_LoeschtDasLetzteAngebotNicht()
    {
        var log = new UpdateCheckLog(new Paths(_dir), Serilog.Log.Logger, () => Now);
        log.RecordOffered("1.7.4");
        log.RecordReached();

        Assert.Equal("1.7.4", log.LastOffered);
        Assert.Equal(Now, log.LastSuccess);
    }

    /// <summary>Was aufgeschrieben wurde, ueberlebt den Neustart - sonst stuende nach jedem Start
    /// „noch nie geprueft", und die Anzeige waere eine Anzeige ueber diese Sitzung statt ueber
    /// diesen Rechner.</summary>
    [Fact]
    public void DasProtokoll_UeberlebtEinenNeustart()
    {
        new UpdateCheckLog(new Paths(_dir), Serilog.Log.Logger, () => Now).RecordOffered("1.7.4");

        var again = new UpdateCheckLog(new Paths(_dir), Serilog.Log.Logger, () => Now.AddDays(1));

        Assert.Equal("1.7.4", again.LastOffered);
    }

    // ── Was als Pruefung zaehlt ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 Der Kern. Ein Server, der 200 und Unbrauchbares zurueckgibt, hat nichts geprueft. Wuerde
    /// das als Erfolg gelten, behauptete die Anzeige eine frische Pruefung ausgerechnet dort, wo
    /// keine stattgefunden hat - und der Spieler sucht den Fehler ueberall, nur nicht beim Update.
    /// Die beiden Faelle stehen nebeneinander, sonst bewiese der Test nichts.
    /// </summary>
    /// <remarks>
    /// Zwei Sorten von Unbrauchbarem, und sie nehmen VERSCHIEDENE Wege durch den Code. Eine
    /// HTML-Seite wirft beim Auswerten und faellt in den Fangzweig; ein gueltiges JSON <c>null</c>
    /// wirft nichts und kommt bis zur Erfolgszeile durch. Nur der zweite Fall wird von der Bedingung
    /// gedeckt, um die es hier geht - der erste stand zuerst allein hier, und die Gegenprobe hat den
    /// Test 2026-08-05 prompt als Placebo entlarvt.
    /// </remarks>
    [Theory]
    [InlineData("<html>hotel wifi login</html>")]
    [InlineData("null")]
    public async Task EineAntwortOhneInhalt_GiltNichtAlsPruefung(string body)
    {
        var good = new SpyLog();
        await Service(good, "{\"current_version\":\"1.2.3\"}").FetchLauncherManifestAsync();

        var bad = new SpyLog();
        await Service(bad, body).FetchLauncherManifestAsync();

        Assert.True(good.Reached);
        Assert.False(bad.Reached);
    }

    /// <summary>Das Realm-Manifest sagt nichts darueber, ob der LAUNCHER seinen Server erreicht hat.
    /// Ein eigener Realm mit eigenem Manifest wuerde sonst jede Pruefung frisch aussehen lassen,
    /// waehrend der Launcher seit Wochen nichts von Stonetavern gehoert hat.</summary>
    [Fact]
    public async Task DasRealmManifest_ZaehltNichtAlsLauncherPruefung()
    {
        var spy = new SpyLog();
        await Service(spy, "{\"current_version\":\"1.2.3\"}",
            manifestUrl: "https://realm.example.invalid/manifest.json").FetchAsync();

        Assert.False(spy.Reached);
    }

    /// <summary>
    /// 🔴 Die angebotene Fassung wird erst NACH der Signaturpruefung festgehalten. Eine Zahl aus einem
    /// unbeglaubigten Dokument gehoert nicht vor einen Spieler - dieselbe fail-closed-Regel, der auch
    /// der Update-Hinweis folgt. Ohne die beiden Faelle nebeneinander bewiese der Test nichts.
    /// </summary>
    [Fact]
    public async Task DieAngeboteneFassung_WirdErstNachDerSignaturFestgehalten()
    {
        var manifest = new ServerManifest
        {
            LauncherLinux = new ManifestFile
            {
                Version = "9.9.9",
                Url = "https://example.invalid/WowLauncher.tar.xz",
                Sha256 = "9999999999999999999999999999999999999999999999999999999999999999",
            },
        };

        var refused = new SpyLog();
        await NewUpdateService(refused, new Gate(null)).CheckAndApplyAsync(manifest);

        var verified = new SpyLog();
        await NewUpdateService(verified, new Gate(manifest)).CheckAndApplyAsync(manifest);

        Assert.Null(refused.Offered);
        Assert.Equal("9.9.9", verified.Offered);
    }

    // ── Die Anzeige ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Wer hinterherhaengt, bekommt gesagt wo er es herbekommt. Ohne die Adresse ist die
    /// Zeile eine Feststellung statt einer Handlung.</summary>
    [Fact]
    public void WerHinterherhaengt_BekommtDieAdresseGenannt()
    {
        var vm = Vm(running: "1.5.0", offered: "1.7.4", checkedAt: Now);

        Assert.Contains("1.7.4", vm.ReleaseStatusLine, StringComparison.Ordinal);
        Assert.Contains(UpdateService.DownloadPage, vm.ReleaseStatusLine, StringComparison.Ordinal);
    }

    /// <summary>Eine wochenalte Auskunft macht die Zahlen darueber nicht falsch, aber
    /// unglaubwuerdig - und das muss dastehen, sonst liest sich eine veraltete Angabe wie eine
    /// frische.</summary>
    [Fact]
    public void EineWochenalteAuskunft_SagtDassSieAltIst()
    {
        var fresh = Vm(running: "1.7.4", offered: "1.7.4", checkedAt: Now);
        var old = Vm(running: "1.7.4", offered: "1.7.4", checkedAt: Now.AddDays(-30));

        Assert.NotEqual(fresh.ReleaseCheckLine, old.ReleaseCheckLine);
        Assert.Contains("30", old.ReleaseCheckLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// Die Ansicht ist ein Singleton und liest ihre Angaben aus lebendem Zustand. Ohne einen Anstoss
    /// beim Oeffnen zeigt sie den Stand vom Programmstart weiter - eine Anzeige, die stillsteht,
    /// waehrend sie Aktualitaet behauptet.
    /// </summary>
    [Fact]
    public void EinAnstoss_LaesstAlleLebendenAngabenNeuLesen()
    {
        var vm = Vm(running: "1.7.4", offered: "1.7.4", checkedAt: Now);
        var seen = new System.Collections.Generic.List<string>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");

        vm.RefreshLiveFacts();

        Assert.Contains(nameof(SettingsViewModel.ReleaseStatusLine), seen);
        Assert.Contains(nameof(SettingsViewModel.ReleaseCheckLine), seen);
        Assert.Contains(nameof(SettingsViewModel.StartReportText), seen);
    }

    /// <summary>Ohne Protokoll kein Block - dieselbe Regel wie beim Startbericht.</summary>
    [Fact]
    public void OhnePruefprotokoll_BleibtDerBlockUnsichtbar()
    {
        var vm = new SettingsViewModel(new MemoryConfig(), new NoPicker());

        Assert.False(vm.ShowReleaseStatus);
    }

    // ── Hilfsmittel ─────────────────────────────────────────────────────────────────────────────

    private SettingsViewModel Vm(string running, string offered, DateTimeOffset checkedAt) =>
        new(new MemoryConfig(), new NoPicker(), new NoDesktop(), startReport: null, clipboard: null,
            checkLog: new FixedLog(checkedAt, offered), runningVersion: () => running, now: () => Now);

    private ManifestService Service(IUpdateCheckLog log, string body, string? manifestUrl = null) =>
        new(new HttpClient(new CannedHandler(body)) { BaseAddress = null },
            new MemoryConfig(manifestUrl), Serilog.Log.Logger, log);

    /// <summary>Nichts wird heruntergeladen und nichts getauscht: geprueft wird, was AUFGESCHRIEBEN
    /// wird, nicht was installiert.</summary>
    private static UpdateService NewUpdateService(IUpdateCheckLog log, Gate gate) =>
        new(new NoDownload(), Serilog.Log.Logger, new NoSwap(), gate,
            LauncherUpdateChannel.Linux, new Version(1, 0, 0), checkLog: log);

    private sealed class Gate(ServerManifest? verified) : IManifestSignatureGate
    {
        public Task<ServerManifest?> AcquireVerifiedAsync(CancellationToken ct = default) =>
            Task.FromResult(verified);
    }

    private sealed class NoSwap : IUpdateSwapStrategy
    {
        public bool IsSupported => false;
        public bool ApplySwap(string newExePath, string currentExePath, string appDir, Version? target = null) => false;
    }

    private sealed class NoDownload : IDownloadService
    {
        public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(DownloadResult.Success);
        public Task<bool> VerifyHashAsync(string path, string expectedSha256, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> ExtractZipAsync(string zipPath, string destDir,
            IProgress<string>? progress = null, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> ExtractClientAsync(string zipPath, string destDir,
            IProgress<string>? progress = null, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private sealed class SpyLog : IUpdateCheckLog
    {
        public bool Reached;
        public string? Offered;
        public void RecordReached() => Reached = true;
        public void RecordOffered(string? version) => Offered = version;
        public DateTimeOffset? LastSuccess => Reached ? Now : null;
        public string LastOffered => Offered ?? "";
    }

    private sealed class FixedLog(DateTimeOffset when, string offered) : IUpdateCheckLog
    {
        public void RecordReached() { }
        public void RecordOffered(string? version) { }
        public DateTimeOffset? LastSuccess => when;
        public string LastOffered => offered;
    }

    private sealed class NoPicker : IFolderPickerService
    {
        public Task<string?> PickFolderAsync(string title, string? startAt = null) =>
            Task.FromResult<string?>(null);
    }

    private sealed class NoDesktop : IDesktopIntegrationService
    {
        public bool IsSupported => false;
        public bool IsInstalled() => false;
        public Task<DesktopIntegrationResult> InstallAsync(CancellationToken ct = default) =>
            Task.FromResult(new DesktopIntegrationResult(DesktopIntegrationOutcome.Unsupported, "not here"));
    }

    private sealed class MemoryConfig(string? manifestUrl = null) : IConfigService
    {
        private LauncherConfig _c = new()
        {
            SelectedRealmId = RealmRegistry.StonetavernId,
            ManifestUrl = manifestUrl ?? "",
        };
        public LauncherConfig Load() => _c;
        public void Save(LauncherConfig config) => _c = config;
        public bool LastSaveSucceeded => true;
    }

    private sealed class Paths(string dir) : IAppPaths
    {
        public string ConfigDir => dir;
        public string StateDir => dir;
        public string CacheDir => dir;
        public string LogDir => dir;
        public string ShareDir => dir;
        public string ConfigFilePath => Path.Combine(dir, "launcher_config.json");
        public string NewsCacheFilePath => Path.Combine(dir, "news-cache.json");
        public string ClientInstallDir(int gameBuild) => Path.Combine(dir, $"WoW-Client-{gameBuild}");
        public string ClientDownloadZip(int gameBuild) => Path.Combine(dir, $"WoW-Client-{gameBuild}.zip");
        public void EnsureDirectories() => Directory.CreateDirectory(dir);
    }
}
