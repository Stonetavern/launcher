namespace WowLauncher.Tests;

using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using Xunit;

/// <summary>
/// Die Einstufung an einem ECHTEN kaputten Archiv, nicht an einer selbstgebauten Ausnahme.
///
/// <para><b>Warum das der wichtigste Test dieser Reihe ist.</b>
/// <see cref="ExtractFailureClassificationTests"/> prüft <c>Classify</c> gegen Ausnahmen, die der Test
/// selbst wirft — das beweist die Zuordnung, aber nicht, dass .NET für ein beschädigtes ZIP wirklich
/// genau diese Ausnahme liefert. Läge dort in Wahrheit eine <see cref="IOException"/>, würde ein
/// kaputtes Paket als „behebbar" gelten, keinen Versuch verbrauchen, und der Abbruchzähler wäre für
/// den HAUPTFALL wirkungslos: grün und trotzdem nutzlos. Das ist die Placebo-Falle eine Ebene höher
/// (Review 2026-09-14).</para>
///
/// <para>Deshalb wird hier ein gültiges ZIP gebaut, dann kaputtgemacht, und durch den echten
/// <see cref="DownloadService"/> geschickt.</para>
/// </summary>
public sealed class CorruptArchiveClassificationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "corrupt-" + Guid.NewGuid().ToString("N"));

    public CorruptArchiveClassificationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* Aufräumen */ } }

    private static DownloadService Service() =>
        new(new HttpClient(), Serilog.Core.Logger.None);

    /// <summary>Ein gültiges Client-ZIP in der Form, die der Launcher ausliefert.</summary>
    private string MakeValidZip(string name = "client.zip")
    {
        var path = Path.Combine(_dir, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("World of Warcraft/_classic_era_/WowClassic.exe");
            using var w = new StreamWriter(entry.Open());
            w.Write(new string('M', 4096));
        }
        return path;
    }

    /// <summary>🔴 Die Positivkontrolle. Ohne sie wäre „kaputt" nicht von „dieser Test entpackt
    /// grundsätzlich nichts" zu unterscheiden.</summary>
    [Fact]
    public async Task EinGueltigesArchiv_WirdEntpackt()
    {
        var zip = MakeValidZip();
        var dest = Path.Combine(_dir, "ok");

        var outcome = await Service().ExtractClientWithReasonAsync(zip, dest, freshInstall: true);

        Assert.True(outcome.Ok, $"failure={outcome.Failure}");
        Assert.True(File.Exists(Path.Combine(dest, "World of Warcraft", "_classic_era_", "WowClassic.exe")));
    }

    /// <summary>
    /// 🔴 Ein abgeschnittenes Archiv — der häufigste echte Schaden — muss als kaputtes Paket gelten
    /// und damit einen Versuch verbrauchen. Gälte es als behebbar, liefe der Launcher unbegrenzt
    /// weiter gegen dieselbe Datei.
    /// </summary>
    [Fact]
    public async Task EinAbgeschnittenesArchiv_GiltAlsKaputtesPaket_UndVerbrauchtEinenVersuch()
    {
        var zip = MakeValidZip("truncated.zip");
        var full = new FileInfo(zip).Length;
        using (var fs = new FileStream(zip, FileMode.Open, FileAccess.Write))
            fs.SetLength(full / 2);   // das zentrale Verzeichnis am Ende ist damit weg

        var outcome = await Service().ExtractClientWithReasonAsync(
            zip, Path.Combine(_dir, "trunc"), freshInstall: true);

        Assert.False(outcome.Ok);
        Assert.Equal(ExtractFailure.BadArchive, outcome.Failure);
        Assert.False(outcome.IsEnvironmental,
            "Ein kaputtes Archiv muss das Install-Budget verbrauchen, sonst laeuft der Launcher endlos.");
    }

    /// <summary>
    /// 🔴 Und der zweite echte Schaden: Kopf und Verzeichnis sind heil, die Nutzdaten nicht. Hier
    /// scheitert nicht das Oeffnen, sondern das Lesen eines Eintrags — ein anderer Weg durch den
    /// Code, der dieselbe Einstufung ergeben muss.
    /// </summary>
    [Fact]
    public async Task EinArchivMitBeschaedigtenNutzdaten_GiltEbenfallsAlsKaputtesPaket()
    {
        var zip = MakeValidZip("badcrc.zip");
        var bytes = await File.ReadAllBytesAsync(zip);
        // Mitten in den komprimierten Datenstrom schreiben: die lokale Kopfzeile bleibt heil, die
        // CRC-Pruefung beim Lesen schlaegt fehl.
        for (var i = 64; i < Math.Min(160, bytes.Length); i++) bytes[i] ^= 0xFF;
        await File.WriteAllBytesAsync(zip, bytes);

        var outcome = await Service().ExtractClientWithReasonAsync(
            zip, Path.Combine(_dir, "crc"), freshInstall: true);

        Assert.False(outcome.Ok);
        Assert.Equal(ExtractFailure.BadArchive, outcome.Failure);
        Assert.False(outcome.IsEnvironmental);
    }
}
