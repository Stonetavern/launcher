using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Ein pausierter Download wird fortgesetzt, und in der Zwischenzeit hat sich auf dem Server etwas
/// geaendert.
///
/// <para><b>Der Fall, um den es geht</b> (Owner-Frage, 2026-08-05): jemand pausiert einen 5-GB-Download
/// und macht am naechsten Tag weiter. Inzwischen liegt unter DERSELBEN Adresse ein neues Paket. Bis
/// hierher pruefte die Wiederaufnahme nur die Adresse, fand sie gleich und haengte neue Bytes an alte.
/// Auffallen konnte das erst am Ende, nach mehreren Gigabyte, als Pruefsummenfehler - und der sieht aus
/// wie ein kaputter Download, nicht wie ein ersetztes Paket. Der Spieler laedt alles noch einmal und
/// landet beim selben Ergebnis.</para>
///
/// <para>Drei Schranken, und jede faengt einen anderen Server: <c>If-Range</c> (der Server prueft
/// selbst), der Vergleich der Gesamtgroesse (falls der Server <c>If-Range</c> ignoriert) und die
/// eigene Behandlung von 404 (die Datei ist ganz weg).</para>
/// </summary>
public sealed class DownloadPausedFileChangedTests : IDisposable
{
    private const string Url = "https://downloads.example.invalid/client-5875.zip";
    private const string Alt = "ALTES-PAKET-ALTES-PAKET-ALT";
    private const string Neu = "NEUES-PAKET-NEUES-PAKET-NEU-LAENGER";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "st-pause-" + Guid.NewGuid().ToString("N"));

    public DownloadPausedFileChangedTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string Ziel => Path.Combine(_dir, "client.zip");

    private static DownloadService NewService(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new Serilog.LoggerConfiguration().CreateLogger());

    /// <summary>Legt den Zustand „pausiert" hin: ein paar Bytes des ALTEN Pakets plus der Merkzettel,
    /// den der Launcher beim ersten Anlauf geschrieben haette.</summary>
    private void Pausiert(string? etag, long total, string bytes = "ALTES-")
    {
        File.WriteAllText(Ziel + ".part", bytes);
        var claim = new DownloadService.PartClaim(Url, etag, null, total);
        File.WriteAllText(DownloadService.PartOwnerPath(Ziel), claim.ToJson());
    }

    private string Ergebnis => File.ReadAllText(Ziel);

    // ── Die drei Schranken ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Beim Fortsetzen geht die Identitaet der Datei mit. Ohne sie kann der Server gar nicht
    /// entscheiden, ob er noch dieselbe Datei ausliefert - jede weitere Schranke waere Raten.
    /// </summary>
    [Fact]
    public async Task BeimFortsetzen_GehtDieIdentitaetMit()
    {
        var handler = new SpyHandler(Neu, etag: "\"neu\"");
        Pausiert(etag: "\"alt\"", total: Alt.Length);

        await NewService(handler).DownloadFileAsync(Url, Ziel);

        Assert.Contains("\"alt\"", handler.IfRanges[0] ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔴 Der Kern. Der Server hat die Datei ersetzt und antwortet deshalb mit 200 und der GANZEN
    /// neuen Datei statt mit 206. Was danach auf der Platte liegt, muss das neue Paket sein - und
    /// nichts davor. Ein angehaengtes Ergebnis waere ein Archiv aus zwei Fassungen, das es nie gab.
    /// </summary>
    [Fact]
    public async Task HatSichDieDateiGeaendert_WirdVonVornGeladen()
    {
        var handler = new SpyHandler(Neu, etag: "\"neu\"", honourIfRange: true);
        Pausiert(etag: "\"alt\"", total: Alt.Length);

        var result = await NewService(handler).DownloadFileAsync(Url, Ziel);

        Assert.True(result.Ok);
        Assert.Equal(Neu, Ergebnis);
        Assert.DoesNotContain("ALTES", Ergebnis, StringComparison.Ordinal);
    }

    /// <summary>
    /// Und der Gegenfall, ohne den der obige nichts beweist: hat sich NICHTS geaendert, wird
    /// fortgesetzt statt neu geladen. Ein Launcher, der bei jedem Fortsetzen von vorn beginnt, ist
    /// genauso falsch - nur teurer.
    /// </summary>
    [Fact]
    public async Task HatSichNichtsGeaendert_WirdFortgesetzt()
    {
        var handler = new SpyHandler(Alt, etag: "\"alt\"", honourIfRange: true);
        Pausiert(etag: "\"alt\"", total: Alt.Length);

        var result = await NewService(handler).DownloadFileAsync(Url, Ziel);

        Assert.True(result.Ok);
        Assert.Equal(Alt, Ergebnis);
        Assert.Equal(1, handler.PartialAnswers);
    }

    /// <summary>
    /// Die zweite Schranke, fuer Server, die <c>If-Range</c> ignorieren und stur 206 aus der NEUEN
    /// Datei liefern. Dann bleibt die Gesamtgroesse als Vergleich - und sie muss reichen, sonst
    /// entsteht genau das Archiv aus zwei Fassungen.
    /// </summary>
    [Fact]
    public async Task IgnoriertDerServerDieIdentitaet_FaengtDieGroesseEsAb()
    {
        var handler = new SpyHandler(Neu, etag: "\"neu\"", honourIfRange: false);
        Pausiert(etag: "\"alt\"", total: Alt.Length);

        var result = await NewService(handler).DownloadFileAsync(Url, Ziel);

        Assert.True(result.Ok);
        Assert.Equal(Neu, Ergebnis);
    }

    /// <summary>Das Paket ist verschwunden. Die angefangene Datei muss mit weg, sonst faende jeder
    /// weitere Versuch dieselbe Sackgasse und niemand wuerde je erfahren, warum.</summary>
    [Fact]
    public async Task IstDieDateiWeg_BleibtKeineRuineLiegen()
    {
        var handler = new SpyHandler(Neu, etag: "\"neu\"", status: HttpStatusCode.NotFound);
        Pausiert(etag: "\"alt\"", total: Alt.Length);

        var result = await NewService(handler).DownloadFileAsync(Url, Ziel);

        Assert.False(result.Ok);
        Assert.False(File.Exists(Ziel + ".part"));
        Assert.False(File.Exists(DownloadService.PartOwnerPath(Ziel)));
    }

    /// <summary>Mehr Bytes auf der Platte, als die Datei drueben gross ist. Das ist ohne Netz
    /// erkennbar und muss deshalb ohne Netz erkannt werden - eine Anfrage, deren Antwort schon
    /// feststeht, ist verschwendete Zeit des Spielers.</summary>
    [Fact]
    public async Task IstDasAngefangeneGroesserAlsDieQuelle_WirdEsVerworfen()
    {
        var handler = new SpyHandler(Neu, etag: "\"neu\"");
        Pausiert(etag: "\"alt\"", total: 3, bytes: "VIEL-ZU-VIELE-BYTES");

        await NewService(handler).DownloadFileAsync(Url, Ziel);

        Assert.Null(handler.IfRanges.Count > 0 ? handler.Ranges[0] : null);
        Assert.Equal(Neu, Ergebnis);
    }

    // ── Rueckwaertsvertraeglichkeit ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Ein Merkzettel der ALTEN Form - dort stand nur die nackte Adresse - muss weiter gelten. Sonst
    /// haette jeder pausierte Download beim naechsten Launcher-Update stillschweigend von vorn
    /// begonnen: mehrere Gigabyte, weil sich ein Dateiformat geaendert hat.
    /// </summary>
    [Fact]
    public async Task EinAlterMerkzettel_LaesstWeiterFortsetzen()
    {
        var handler = new SpyHandler(Alt, etag: "\"alt\"", honourIfRange: true);
        File.WriteAllText(Ziel + ".part", "ALTES-");
        File.WriteAllText(DownloadService.PartOwnerPath(Ziel), Url);

        var result = await NewService(handler).DownloadFileAsync(Url, Ziel);

        Assert.True(result.Ok);
        Assert.Equal(1, handler.PartialAnswers);
        Assert.Equal(Alt, Ergebnis);
    }

    // ── Doppelgaenger ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ein Server, der Teilantworten kann und dessen Verhalten bei <c>If-Range</c> sich einstellen
    /// laesst - das ist der Unterschied zwischen den beiden Schranken, die hier geprueft werden.
    /// </summary>
    private sealed class SpyHandler(string body, string etag,
        bool honourIfRange = false, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public readonly List<string?> Ranges = [];
        public readonly List<string?> IfRanges = [];
        public int PartialAnswers;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var range = request.Headers.Range;
            Ranges.Add(range?.ToString());
            IfRanges.Add(request.Headers.TryGetValues("If-Range", out var v) ? string.Join(",", v) : null);

            if (status != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(status));

            var from = (int?)range?.Ranges.FirstOrDefault()?.From ?? 0;

            // Der Kern des Doppelgaengers: eine Teilantwort gibt es nur, wenn entweder If-Range fehlt
            // oder die mitgeschickte Identitaet zur eigenen passt. Genau so verhaelt sich ein Server,
            // der den Header versteht - und mit honourIfRange=false einer, der ihn ignoriert.
            var mitgeschickt = IfRanges[^1];
            var passt = mitgeschickt is null || mitgeschickt == etag;

            if (range is not null && (!honourIfRange || passt) && from <= body.Length)
            {
                PartialAnswers++;
                var rest = body[Math.Min(from, body.Length)..];
                var teil = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new StringContent(rest, Encoding.UTF8),
                };
                teil.Headers.TryAddWithoutValidation("ETag", etag);
                teil.Content.Headers.ContentRange =
                    new System.Net.Http.Headers.ContentRangeHeaderValue(from, body.Length - 1, body.Length);
                return Task.FromResult(teil);
            }

            var ganz = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8),
            };
            ganz.Headers.TryAddWithoutValidation("ETag", etag);
            return Task.FromResult(ganz);
        }
    }
}
