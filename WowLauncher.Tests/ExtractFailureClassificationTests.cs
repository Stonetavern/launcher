namespace WowLauncher.Tests;

using System;
using System.IO;
using WowLauncher.Models;
using WowLauncher.Services;
using Xunit;

/// <summary>
/// Welcher Grund hinter einem gescheiterten Entpacken steckt.
///
/// <para><b>Warum das zählt.</b> Bis 2026-09-14 meldete der Extraktor jeden Fehlschlag als dasselbe
/// <c>false</c>. Damit verbrauchte eine volle Platte oder ein noch laufendes WoW einen der drei
/// Install-Versuche — ein Spieler, der nach dem dritten Fehlschlag genau das behob, war gesperrt
/// (Codex-Review 2026-09-14). Die Einstufung ist also keine Kosmetik im Fehlertext, sondern die
/// Eingabe einer Entscheidung.</para>
///
/// <para><b>Die Falle, die diese Tests bewachen:</b> <c>ExtractEntryAsync</c> verpackt den echten
/// Grund nach seinen Wiederholungen in eine eigene <see cref="IOException"/>. Eine Einstufung, die
/// den äußersten Typ nimmt, sieht deshalb immer nur die Schale — eine volle Platte läse sich als
/// "etwas hält die Datei". Deshalb wird die ganze Kette befragt, und zwar nach Aussagekraft.</para>
/// </summary>
public sealed class ExtractFailureClassificationTests
{
    /// <summary>Die Fehlernummer, an der eine volle Platte auf DIESER Plattform zu erkennen ist —
    /// ENOSPC auf Unix, ERROR_DISK_FULL auf Windows. Ein fest verdrahteter Wert wäre auf der jeweils
    /// anderen Plattform stillschweigend falsch.</summary>
    private static IOException DiskFull() =>
        new("no space left on device", OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28);

    [Fact]
    public void EinAbbruch_IstEinAbbruch() =>
        Assert.Equal(ExtractFailure.Cancelled, DownloadService.Classify(new OperationCanceledException()));

    [Fact]
    public void EinKaputtesArchiv_IstKeinPlatzproblem() =>
        Assert.Equal(ExtractFailure.BadArchive, DownloadService.Classify(new InvalidDataException()));

    [Fact]
    public void EinRechteproblem_HeisstRechteproblem() =>
        Assert.Equal(ExtractFailure.AccessDenied, DownloadService.Classify(new UnauthorizedAccessException()));

    [Fact]
    public void EineVollePlatte_WirdAnDerFehlernummerErkannt() =>
        Assert.Equal(ExtractFailure.DiskFull, DownloadService.Classify(DiskFull()));

    /// <summary>Die Restmenge: eine IOException ohne spezifisches Signal ist die gehaltene Datei —
    /// der Virenscanner- und Spiel-läuft-noch-Fall, den die Wiederholung pro Datei abzufangen
    /// versucht.</summary>
    [Fact]
    public void EineGewoehnlicheIoAusnahme_IstDieGehalteneDatei() =>
        Assert.Equal(ExtractFailure.FileLocked, DownloadService.Classify(new IOException("in use")));

    /// <summary>🔴 Der Fall, an dem die naive Einstufung scheitert: der echte Grund steckt INNEN,
    /// weil der Extraktor ihn selbst verpackt. Ohne Blick in die Kette läse sich das als gehaltene
    /// Datei — und eine volle Platte würde als kaputtes Paket gezählt.</summary>
    [Fact]
    public void EineVollePlatteInDerVerpackungDesExtraktors_BleibtEineVollePlatte()
    {
        var wrapped = new IOException("Could not write 'x' after 4 attempts", DiskFull());

        Assert.Equal(ExtractFailure.DiskFull, DownloadService.Classify(wrapped));
    }

    [Fact]
    public void EinRechteproblemInDerVerpackungDesExtraktors_BleibtEinRechteproblem()
    {
        var wrapped = new IOException("Could not write 'x'", new UnauthorizedAccessException());

        Assert.Equal(ExtractFailure.AccessDenied, DownloadService.Classify(wrapped));
    }

    [Fact]
    public void EinUnbekannterFehler_BleibtUnbekannt() =>
        Assert.Equal(ExtractFailure.Unknown, DownloadService.Classify(new InvalidOperationException()));

    /// <summary>
    /// 🔴 Deterministische Pfadfehler sind KEINE gehaltene Datei.
    ///
    /// <para>Sie sind alle <see cref="IOException"/>-Ableitungen und landeten damit im Sammelbecken
    /// <see cref="ExtractFailure.FileLocked"/> - also unter "behebbar". Ein fehlendes Verzeichnis oder
    /// ein Name, den das Dateisystem nicht annimmt, heilt aber nie von selbst: der Launcher haette es
    /// unbegrenzt wiederholt und dabei geraten, Spiel und Virenscanner zu schliessen, was mit der
    /// Ursache nichts zu tun hat (Review 2026-09-14).</para>
    /// </summary>
    [Fact]
    public void EinFehlendesVerzeichnis_IstKeineGehalteneDatei() =>
        Assert.Equal(ExtractFailure.TargetUnusable, DownloadService.Classify(new DirectoryNotFoundException()));

    [Fact]
    public void EineFehlendeDatei_IstKeineGehalteneDatei() =>
        Assert.Equal(ExtractFailure.TargetUnusable, DownloadService.Classify(new FileNotFoundException()));

    [Fact]
    public void EinZuLangerPfad_IstKeineGehalteneDatei() =>
        Assert.Equal(ExtractFailure.TargetUnusable, DownloadService.Classify(new PathTooLongException()));

    /// <summary>Auch durch die Verpackung des Extraktors hindurch - sonst griffe die Einstufung genau
    /// dort nicht, wo sie im Betrieb entsteht.</summary>
    [Fact]
    public void EinFehlendesVerzeichnisInDerVerpackung_BleibtEinUnbrauchbaresZiel() =>
        Assert.Equal(ExtractFailure.TargetUnusable,
            DownloadService.Classify(new IOException("Could not write 'x'", new DirectoryNotFoundException())));

    /// <summary>Und die Gegenprobe: eine gewoehnliche IOException bleibt die gehaltene Datei. Ohne
    /// sie koennte die neue Regel alles verschlucken und die Tests blieben gruen.</summary>
    [Fact]
    public void EineGewoehnlicheIoAusnahme_WirdVonDerNeuenRegelNichtEingesammelt() =>
        Assert.Equal(ExtractFailure.FileLocked, DownloadService.Classify(new IOException("sharing violation")));

    // ── Und die Folge, um die es eigentlich geht ─────────────────────────────────────────────────

    /// <summary>
    /// Die Einstufung trägt eine Entscheidung: behebbare Umgebungsfehler dürfen das Install-Budget
    /// nicht verbrauchen, ein kaputtes Paket schon. Ohne diese Zeile könnte die Zuordnung kippen,
    /// ohne dass ein Test es merkt.
    /// </summary>
    [Theory]
    [InlineData(ExtractFailure.Cancelled, true)]
    [InlineData(ExtractFailure.DiskFull, true)]
    [InlineData(ExtractFailure.FileLocked, true)]
    [InlineData(ExtractFailure.AccessDenied, true)]
    [InlineData(ExtractFailure.BadArchive, false)]
    [InlineData(ExtractFailure.TargetUnusable, false)]
    [InlineData(ExtractFailure.Unknown, false)]
    public void NurBehebbareUmgebungsfehler_ZaehlenAlsUmgebung(ExtractFailure failure, bool environmental) =>
        Assert.Equal(environmental, ExtractOutcome.Fail(failure).IsEnvironmental);
}
