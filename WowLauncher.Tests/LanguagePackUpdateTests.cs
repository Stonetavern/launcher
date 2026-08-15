using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Ein Sprachpaket, das nachgebessert wurde, muss die erreichen, die es schon haben.
///
/// <para><b>Die Luecke, gegen die diese Datei gebaut ist.</b> Ein Paket wird einmal installiert und
/// danach nie wieder angesehen: der Wechsel fragt „liegt die Datei da" und laedt dann nicht mehr. Fuer
/// einen Wechsel ist das richtig, fuer eine Fehlerbehebung falsch. Wer deDE einmal hat, behaelt es fuer
/// immer - kein Fehler, keine Meldung, die Sprache gilt als installiert, und das ist sie ja auch, nur
/// nicht die veroeffentlichte. Genau die Form, die gruen ausgeliefert wird.</para>
///
/// <para><b>Und was hier ausdruecklich NICHT gebaut wird:</b> eine Zustellung „an die deutschen
/// Clients". Das Manifest ist fuer alle gleich; jeder Launcher vergleicht nur die Sprachen, die er
/// wirklich auf der Platte hat. Ein englischer Spieler laedt nichts, und der Server erfaehrt nie, wer
/// welche Sprache spricht.</para>
/// </summary>
public sealed class LanguagePackUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "st-langupd-" + Guid.NewGuid().ToString("N"));

    private static Serilog.ILogger Log() => new Serilog.LoggerConfiguration().CreateLogger();

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string AltePruefsumme = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string NeuePruefsumme = "2222222222222222222222222222222222222222222222222222222222222222";

    private static ManifestLanguagePack Pack(string locale, string sha) => new()
    {
        Locale = locale, Build = 5875, Url = $"https://example.invalid/lang-{locale}.zip",
        Sha256 = sha, Size = 1234,
    };

    // ── Der Zustand ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Die vier Zustaende muessen unterscheidbar sein, und drei davon sind es nur, weil die Herkunft
    /// mitgeschrieben wird. Ohne sie gaebe es genau zwei: „liegt da" und „liegt nicht da".
    /// </summary>
    [Fact]
    public void DerZustand_UnterscheidetAktuellVeraltetUndUnbekannt()
    {
        var packs = new VanillaLocalePacks(Log());
        var svc = new LanguagePackService(new FakeDownloads(), packs, Log());
        var angeboten = Pack("deDE", NeuePruefsumme);

        Assert.Equal(LanguagePackState.NotInstalled, svc.State(_root, "deDE", angeboten));

        InstalliereRohes("deDE", "alt");
        Assert.Equal(LanguagePackState.OriginUnknown, svc.State(_root, "deDE", angeboten));

        packs.WritePackOrigin(_root, "deDE", AltePruefsumme);
        Assert.Equal(LanguagePackState.Outdated, svc.State(_root, "deDE", angeboten));

        packs.WritePackOrigin(_root, "deDE", NeuePruefsumme);
        Assert.Equal(LanguagePackState.UpToDate, svc.State(_root, "deDE", angeboten));
    }

    /// <summary>
    /// Kein Angebot heisst NICHT „aktuell". Es heisst, dass es nichts gibt, wogegen sich vergleichen
    /// liesse - der Fall jedes selbst angelegten Realms, der gar kein Manifest hat. Ein Vergleich
    /// gegen nichts, der „aktuell" meldet, ist die beruhigende Falschaussage, die dieses Projekt
    /// ueberall sonst verbietet.
    /// </summary>
    [Fact]
    public void OhneAngebot_WirdNichtsBehauptet()
    {
        var svc = new LanguagePackService(new FakeDownloads(), new VanillaLocalePacks(Log()), Log());
        InstalliereRohes("deDE", "alt");
        new VanillaLocalePacks(Log()).WritePackOrigin(_root, "deDE", AltePruefsumme);

        Assert.Equal(LanguagePackState.NothingOffered, svc.State(_root, "deDE", pack: null));
    }

    /// <summary>Englisch steckt in den Basisdateien und hat nie ein Paket. Es als „nicht installiert"
    /// zu melden waere formal wahr und praktisch irrefuehrend.</summary>
    [Fact]
    public void Englisch_BrauchtNieEinPaket()
    {
        var svc = new LanguagePackService(new FakeDownloads(), new VanillaLocalePacks(Log()), Log());

        Assert.Equal(LanguagePackState.NotInstalled, svc.State(_root, "enUS", Pack("enUS", NeuePruefsumme)));
    }

    // ── Der Unterschied zwischen Wechseln und Aktualisieren ─────────────────────────────────────

    /// <summary>
    /// 🔴 Der Kern. Beide Wege bekommen dieselbe Ausgangslage - eine installierte alte Fassung und ein
    /// neues Angebot - und muessen sich UNTERSCHIEDLICH verhalten: der Wechsel laedt nicht (das ist
    /// richtig, er soll nur umschalten), das Aktualisieren schon. Ein Test, der nur einen der beiden
    /// Wege prueft, bewiese nichts ueber den Unterschied.
    /// </summary>
    [Fact]
    public async Task DerWechselLaedtNicht_DasAktualisierenSchon()
    {
        var angeboten = Pack("deDE", NeuePruefsumme);

        InstalliereRohes("deDE", "alt");
        var wechsel = new FakeDownloads { Zip = ("deDE", "neu") };
        var a = await new LanguagePackService(wechsel, new VanillaLocalePacks(Log()), Log())
            .EnsureAsync(_root, "deDE", angeboten);

        var aktualisieren = new FakeDownloads { Zip = ("deDE", "neu") };
        var b = await new LanguagePackService(aktualisieren, new VanillaLocalePacks(Log()), Log())
            .UpdateAsync(_root, "deDE", angeboten);

        Assert.True(a.Ok);
        Assert.Equal(0, wechsel.Calls);
        Assert.True(b.Ok);
        Assert.Equal(1, aktualisieren.Calls);
        // Gelesen wird dort, wo die Sprache nach dem Wechsel WIRKLICH liegt: der Wechsel hat deDE
        // aktiviert, also im Patch-Platz. Ein Test, der stur den Ordner liest, misst hinterher eine
        // Datei, die es an dieser Stelle gar nicht mehr gibt.
        Assert.Equal("neu", AktiverInhalt("deDE"));
    }

    /// <summary>Nach dem Installieren steht daneben, woher es kam. Ohne diese Zeile weiss der naechste
    /// Start nur, DASS eine Sprache installiert ist, nicht welche Fassung - und ein korrigiertes Paket
    /// erreichte nie jemanden, der die Sprache schon hat.</summary>
    [Fact]
    public async Task NachDemInstallieren_StehtDieHerkunftDaneben()
    {
        var packs = new VanillaLocalePacks(Log());
        var svc = new LanguagePackService(new FakeDownloads { Zip = ("deDE", "inhalt") }, packs, Log());

        await svc.EnsureAsync(_root, "deDE", Pack("deDE", NeuePruefsumme));

        Assert.Equal(NeuePruefsumme, packs.PackOrigin(_root, "deDE"));
        Assert.Equal(LanguagePackState.UpToDate, svc.State(_root, "deDE", Pack("deDE", NeuePruefsumme)));
    }

    // ── Die aktive Sprache ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Die aktive Sprache liegt nicht in ihrem Ordner, sondern im Patch-Platz. Wer nur den Ordner
    /// ersetzt, meldet Erfolg, und der Spieler startet weiter mit der alten Fassung: das Spiel liest
    /// den Platz. Genau die Sorte plausibler Zustand, der falsch ist.
    /// </summary>
    [Fact]
    public async Task WirdDieAktiveSpracheAktualisiert_LiegtDasNeueImPatchPlatz()
    {
        var packs = new VanillaLocalePacks(Log());
        var svc = new LanguagePackService(new FakeDownloads { Zip = ("deDE", "neu") }, packs, Log());

        InstalliereRohes("deDE", "alt");
        Assert.True(packs.Apply(_root, "deDE").Ok);
        Assert.Equal("alt", File.ReadAllText(Path.Combine(_root, "Data", VanillaLocalePacks.SlotFileName)));

        var result = await svc.UpdateAsync(_root, "deDE", Pack("deDE", NeuePruefsumme));

        Assert.True(result.Ok);
        Assert.Equal("deDE", packs.Active(_root));
        Assert.Equal("neu", File.ReadAllText(Path.Combine(_root, "Data", VanillaLocalePacks.SlotFileName)));
    }

    /// <summary>
    /// Waehrend das Spiel laeuft, wird die aktive Sprache nicht angefasst. Eine MPQ unter einem
    /// laufenden Client zu ersetzen scheitert auf Windows und GELINGT auf Linux - wo das Spiel danach
    /// aus einer Datei liest, die niemand mehr findet.
    ///
    /// <para>Und die Absage kommt VOR dem Herunterladen: wer erst 90 MB laedt und dann absagt, hat die
    /// Bandbreite trotzdem verbraucht.</para>
    /// </summary>
    [Fact]
    public async Task LaeuftDasSpiel_WirdDieAktiveSpracheNichtAngefasst()
    {
        var packs = new VanillaLocalePacks(Log());
        var downloads = new FakeDownloads { Zip = ("deDE", "neu") };
        var svc = new LanguagePackService(downloads, packs, Log());

        InstalliereRohes("deDE", "alt");
        packs.Apply(_root, "deDE");

        var result = await svc.UpdateAsync(_root, "deDE", Pack("deDE", NeuePruefsumme), gameRunning: true);

        Assert.False(result.Ok);
        Assert.Equal(0, downloads.Calls);
        Assert.Equal("alt", File.ReadAllText(Path.Combine(_root, "Data", VanillaLocalePacks.SlotFileName)));
        // Und die Absage redet vom AKTUALISIEREN, nicht vom Wechseln. Ohne die eigene fruehe Pruefung
        // faengt zwar auch die Umschaltung den Fall - der Spieler liest dann aber "schliesse das Spiel,
        // bevor du die Sprache wechselst", waehrend er gar nicht wechseln wollte. Die Gegenprobe hat
        // diesen Test 2026-08-05 ohne diese Zeile als Placebo entlarvt: das Verhalten war identisch,
        // nur die Auskunft nicht.
        Assert.Contains("updating", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scheitert das Herunterladen mitten im Vorgang, bekommt der Spieler seine funktionierende
    /// Sprache zurueck. Ohne das stuende er nach einem Netzabbruch auf Englisch, ohne dass ihn jemand
    /// gefragt haette - eine Aktualisierung, die eine Verschlechterung ist.
    /// </summary>
    [Fact]
    public async Task ScheitertDasLaden_IstDieAlteSpracheWiederAktiv()
    {
        var packs = new VanillaLocalePacks(Log());
        var svc = new LanguagePackService(new FakeDownloads { HashOk = false, Zip = ("deDE", "neu") },
            packs, Log());

        InstalliereRohes("deDE", "alt");
        packs.Apply(_root, "deDE");

        var result = await svc.UpdateAsync(_root, "deDE", Pack("deDE", NeuePruefsumme));

        Assert.False(result.Ok);
        Assert.Equal("deDE", packs.Active(_root));
        Assert.Equal("alt", File.ReadAllText(Path.Combine(_root, "Data", VanillaLocalePacks.SlotFileName)));
    }

    // ── Hilfsmittel ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Der Inhalt des Pakets an der Stelle, an der es gerade liegt: im Patch-Platz, wenn die
    /// Sprache aktiv ist, sonst in ihrem Ordner.</summary>
    private string AktiverInhalt(string locale)
    {
        var slot = Path.Combine(_root, "Data", VanillaLocalePacks.SlotFileName);
        return new VanillaLocalePacks(Log()).Active(_root) == locale && File.Exists(slot)
            ? File.ReadAllText(slot)
            : File.ReadAllText(VanillaLocalePacks.PackPath(_root, locale));
    }

    /// <summary>Legt ein Paket hin, wie es eine Installation von vor dem Herkunftsmarker
    /// hinterlassen haette: die Datei ist da, es steht nichts daneben.</summary>
    private void InstalliereRohes(string locale, string inhalt)
    {
        var pfad = VanillaLocalePacks.PackPath(_root, locale);
        Directory.CreateDirectory(Path.GetDirectoryName(pfad)!);
        File.WriteAllText(pfad, inhalt);
    }

    private sealed class FakeDownloads : IDownloadService
    {
        public int Calls { get; private set; }
        public bool HashOk { get; init; } = true;

        /// <summary>Sprache und Inhalt der einen Datei, die das Archiv tragen darf.</summary>
        public (string Locale, string Content)? Zip { get; init; }

        public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            if (Zip is not { } z) return Task.FromResult(DownloadResult.Fail(DownloadFailure.Network));

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using var zip = ZipFile.Open(destPath, ZipArchiveMode.Create);
            using var writer = new StreamWriter(
                zip.CreateEntry($"Data/{z.Locale}/locale-{z.Locale}.MPQ").Open());
            writer.Write(z.Content);
            return Task.FromResult(DownloadResult.Success);
        }

        public Task<bool> VerifyHashAsync(string path, string expectedSha256, CancellationToken ct = default) =>
            Task.FromResult(HashOk);

        public Task<bool> ExtractZipAsync(string zipPath, string destDir,
            IProgress<string>? progress = null, CancellationToken ct = default) =>
            throw new InvalidOperationException(
                "Ein Sprachpaket geht nie durch den allgemeinen Entpacker - der schriebe jeden Eintrag, " +
                "den das Archiv zufaellig traegt.");

        public Task<bool> ExtractClientAsync(string zipPath, string destDir,
            IProgress<string>? progress = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("Ein Sprachpaket ist kein Clientpaket.");
    }
}
