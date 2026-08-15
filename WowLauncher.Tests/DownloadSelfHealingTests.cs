namespace WowLauncher.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using Xunit;

/// <summary>
/// Der Launcher holt sich eine verlorene Verbindung selbst zurück.
///
/// <para><b>Warum das gefehlt hat, obwohl alles dafür da war.</b> Die <c>.part</c>-Datei überlebt,
/// der Range-Kopf nimmt wieder auf, der Fehler wird sauber als Netzproblem eingestuft — nur gehandelt
/// hat niemand darauf. Ein Abbruch führte in den Fehlerzustand und wartete darauf, dass der Spieler
/// noch einmal drückt. Bei einem Mehr-Gigabyte-Client über eine Hausleitung ist das kein Sonderfall,
/// sondern der Normalfall: wer nebenher etwas anderes tut, kommt zu einem stehengebliebenen Download
/// zurück statt zu einem fertigen. Am 2026-08-04 genau so passiert.</para>
///
/// <para>Die Tests messen an echten Dateien und einem echten <see cref="HttpClient"/> mit
/// eingesetztem Handler — die Wartezeit ist über eine Naht abgekürzt, das Verhalten nicht.</para>
/// </summary>
public sealed class DownloadSelfHealingTests : IDisposable
{
    private const string Body = "STONETAVERN-CLIENT-PAYLOAD-0123456789";
    private const string Url = "https://downloads.example.invalid/client-5875.zip";

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "heal-" + Guid.NewGuid().ToString("N"));

    public DownloadSelfHealingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* Aufräumen */ } }

    private string Dest => Path.Combine(_dir, "client.zip");

    /// <summary>Die aufgezeichneten Wartezeiten. Ein Test, der nur „es hat geklappt" prüft, würde eine
    /// enge Schleife ohne Pause nicht von einer geordneten Wiederaufnahme unterscheiden.</summary>
    private readonly List<TimeSpan> _waits = [];

    private DownloadService Service(HttpMessageHandler handler, int attempts = DownloadService.NetworkAttempts) =>
        new(new HttpClient(handler), Serilog.Core.Logger.None,
            (wait, _) => { _waits.Add(wait); return Task.CompletedTask; }, attempts);

    // ── Attrappen ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Bricht die ersten <c>failures</c> Anfragen wie ein Netzausfall ab und liefert danach
    /// den Rest der Nutzlast — mit Range-Unterstützung, damit die Fortsetzung echt ist.</summary>
    private sealed class FlakyHandler(int failures) : HttpMessageHandler
    {
        public int Requests;
        public readonly List<long> ResumeOffsets = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var from = request.Headers.Range?.Ranges is { Count: > 0 } r
                ? System.Linq.Enumerable.First(r).From ?? 0
                : 0;
            ResumeOffsets.Add(from);

            if (Requests <= failures)
                throw new HttpRequestException("Name or service not known");

            var rest = Body[(int)from..];
            var response = new HttpResponseMessage(
                from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StringContent(rest, Encoding.UTF8),
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>Fällt immer aus. Für die Frage, ob der Launcher irgendwann aufhört.</summary>
    private sealed class DeadHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            throw new HttpRequestException("Network is unreachable");
        }
    }

    /// <summary>Antwortet mit einem Serverfehler. Der darf NICHT wiederholt werden.</summary>
    private sealed class ServerErrorHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    // ── Der Fall, für den das Ganze existiert ────────────────────────────────────────────────────

    /// <summary>🔴 Verbindung bricht zweimal weg, der Download läuft trotzdem zu Ende — ohne dass
    /// jemand etwas drückt, und ohne von vorn anzufangen.</summary>
    [Fact]
    public async Task EinAbbruchUnterwegs_HeiltSichSelbst()
    {
        var handler = new FlakyHandler(failures: 2);

        var result = await Service(handler).DownloadFileAsync(Url, Dest);

        Assert.True(result.Ok);
        Assert.Equal(Body, File.ReadAllText(Dest));
        Assert.Equal(3, handler.Requests);                    // zwei Ausfälle, ein Erfolg
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)], _waits);
    }

    /// <summary>
    /// Der eigentliche Sinn: nach dem Abbruch wird ab den bereits geschriebenen Bytes fortgesetzt,
    /// nicht von vorn. Ohne diese Prüfung wäre eine Wiederholung, die jedes Mal bei null anfängt,
    /// genauso grün — und bei einem Mehr-Gigabyte-Client eine Endlosschleife.
    /// </summary>
    [Fact]
    public async Task NachDemAbbruch_WirdFortgesetztStattNeuGeladen()
    {
        // Ein halb geladenes Archiv mit gültigem Herkunftsnachweis liegt schon da.
        var half = Body[..10];
        File.WriteAllText(Dest + ".part", half);
        File.WriteAllText(DownloadService.PartOwnerPath(Dest), Url);

        var handler = new FlakyHandler(failures: 1);
        var result = await Service(handler).DownloadFileAsync(Url, Dest);

        Assert.True(result.Ok);
        Assert.Equal(Body, File.ReadAllText(Dest));
        // Beide Anläufe fragen ab Byte 10 -- der zweite fängt NICHT bei null an.
        Assert.Equal([10L, 10L], handler.ResumeOffsets);
    }

    /// <summary>Irgendwann ist Schluss. Ein Launcher, der ewig weiterprobiert, ist für den Spieler
    /// dasselbe wie einer, der hängt — nur dass er dabei die Leitung belegt.</summary>
    [Fact]
    public async Task EineDauerhafteStoerung_EndetBeimSpieler()
    {
        var handler = new DeadHandler();

        var result = await Service(handler, attempts: 4).DownloadFileAsync(Url, Dest);

        Assert.False(result.Ok);
        Assert.Equal(DownloadFailure.Network, result.Failure);
        Assert.Equal(4, handler.Requests);
        Assert.Equal(3, _waits.Count);                        // vier Versuche, drei Pausen
    }

    /// <summary>
    /// 🔴 Nur Netzfehler werden wiederholt. Eine 404 ist eine Aussage des Servers und wird durch
    /// Warten nicht wahrer; sie zu wiederholen würde den Fehler nur verstecken und den Spieler
    /// zweieinhalb Minuten auf eine Antwort warten lassen, die schon da war.
    /// </summary>
    [Fact]
    public async Task EinServerfehler_WirdNichtWiederholt()
    {
        var handler = new ServerErrorHandler();

        var result = await Service(handler).DownloadFileAsync(Url, Dest);

        Assert.False(result.Ok);
        Assert.Equal(DownloadFailure.ServerError, result.Failure);
        Assert.Equal(1, handler.Requests);
        Assert.Empty(_waits);
    }

    /// <summary>Ein Abbruch durch den Spieler ist keine Störung. Nach einem Druck auf Pause darf der
    /// Launcher nicht von allein weiterladen — sonst nimmt die Selbstheilung dem Spieler die
    /// Kontrolle über seine eigene Leitung.</summary>
    [Fact]
    public async Task EinAbbruchDurchDenSpieler_WirdNichtWiederholt()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new FlakyHandler(failures: 0);

        var result = await Service(handler).DownloadFileAsync(Url, Dest, null, cts.Token);

        Assert.False(result.Ok);
        Assert.Equal(DownloadFailure.Cancelled, result.Failure);
        Assert.Empty(_waits);
    }

    /// <summary>Die Wartezeit wird gemeldet, bevor sie beginnt. Ein stehender Byte-Zähler sieht aus
    /// wie ein Hänger, und wer einen Hänger vermutet, schließt den Launcher — also genau in dem
    /// Moment, in dem die Erholung gegriffen hätte.</summary>
    [Fact]
    public async Task WaehrendDerPause_ErfaehrtDerSpielerWasLosIst()
    {
        var seen = new List<RetryWait>();

        await Service(new FlakyHandler(failures: 2))
            .DownloadFileAsync(Url, Dest, new Direkt(p => { if (p.Waiting is { } w) seen.Add(w); }));

        Assert.Equal(2, seen.Count);
        Assert.Equal(2, seen[0].Attempt);
        Assert.Equal(DownloadService.NetworkAttempts, seen[0].Of);
        Assert.Equal(TimeSpan.FromSeconds(2), seen[0].In);
        Assert.False(string.IsNullOrWhiteSpace(seen[0].Reason));
    }

    /// <summary>Nimmt die Meldung sofort entgegen. <see cref="Progress{T}"/> stellt über den
    /// SynchronizationContext zu, also irgendwann — im vollen Testlauf ist „irgendwann" mal länger
    /// als jede Frist, die man hinschreibt. Ein Test, der unter Last umkippt, wird abgeschaltet
    /// statt gelesen, und dann bewacht er nichts mehr.</summary>
    private sealed class Direkt(Action<DownloadProgress> onReport) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => onReport(value);
    }

    /// <summary>Die Erholung wird länger, aber nicht endlos: eine Verdopplung ohne Deckel ließe den
    /// Launcher auf einer langen Störung tot aussehen.</summary>
    [Fact]
    public void DieWartezeit_WaechstUndDeckeltBeiEinerHalbenMinute()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), DownloadService.BackoffFor(0));
        Assert.True(DownloadService.BackoffFor(1) > DownloadService.BackoffFor(0));
        Assert.True(DownloadService.BackoffFor(3) > DownloadService.BackoffFor(2));
        Assert.Equal(TimeSpan.FromSeconds(30), DownloadService.BackoffFor(4));
        Assert.Equal(TimeSpan.FromSeconds(30), DownloadService.BackoffFor(99));
    }
}
