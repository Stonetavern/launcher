using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Zwei Dinge, die vor dem Ausliefern bewiesen sein muessen und vorher nur behauptet waren:
/// welche Dateien wirklich Laufzeitzustand sind, und dass Entpacken in der richtigen Ebene landet.
/// </summary>
public class VolatileStateTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wl-vol-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    // Laufzeitzustand seiner Natur nach: LRU-Zaehler des Datencaches und Shared-Memory-Datei.
    [InlineData("World of Warcraft/Data/data/wow_classic_era-eu/lru_status")]
    [InlineData("World of Warcraft/Data/data/shmem")]
    // Gemessen am echten Client: gleiche Groesse wie im Paket, abweichender Hash. Diese eine Datei
    // liess Repair 8 GB laden, obwohl der Client intakt war.
    [InlineData("World of Warcraft/Data/data/wow_classic_era-eu/lru_shard_0")]
    [InlineData("World of Warcraft/Data/data/wow_classic_era-eu/lru_shard_11")]
    public void RuntimeState_IsRecognised(string path)
    {
        Assert.True(DownloadService.IsVolatileRuntimeState(path), path);
    }

    [Theory]
    // 🔴 Diese Faelle standen in einem ersten Entwurf als "fluechtig" drin. Die Gegenprobe an einer
    // bespielten Installation (2026-08-12) zeigte: byteidentisch zum Paket. Sie gehoeren geprueft,
    // sonst bleiben 34 pruefbare Dateien ungeprueft, weil ein Dateiname nach Cache aussah.
    [InlineData("World of Warcraft/Data/data/000000000c.idx")]
    [InlineData("World of Warcraft/.build.info")]
    // Statische Archiv-Indizes: der Grossteil des Manifests.
    [InlineData("World of Warcraft/Data/indices/0e5b73a8dcbc2a35.index")]
    [InlineData("World of Warcraft/Data/data/data.000")]
    [InlineData("World of Warcraft/_classic_era_/WowClassic.exe")]
    [InlineData("Hermes/CSV/AreaNames.csv")]
    public void ShippedContent_IsNotMistakenForRuntimeState(string path)
    {
        Assert.False(DownloadService.IsVolatileRuntimeState(path), path);
    }

    [Fact]
    public async Task Verify_IgnoresRuntimeState_ButStillFlagsRealDamage()
    {
        var dir = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "Data", "data"));
            File.WriteAllText(Path.Combine(dir, "Data", "data", "lru_status"), "counter-moved-on");
            File.WriteAllText(Path.Combine(dir, "Data", "data", "data.000"), "damaged");

            var manifest = new ClientFileManifest
            {
                Build = 42597,
                Files =
                [
                    new ClientFileEntry { Path = "Data/data/lru_status", Size = 42, Sha256 = new string('b', 64) },
                    new ClientFileEntry { Path = "Data/data/data.000", Size = 999, Sha256 = new string('c', 64) },
                ],
            };

            var report = await new ClientVerifyService(Serilog.Log.Logger).VerifyAsync(dir, manifest);

            Assert.DoesNotContain("Data/data/lru_status", report.Corrupt);
            Assert.Contains("Data/data/data.000", report.Corrupt);
            Assert.False(report.IsIntact, "echter Schaden muss weiterhin gemeldet werden");
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Der Test, den die Codex-Zweitinstanz zu Recht verlangt hat: dass Repair/Update NICHT eine
    /// Ebene zu tief entpacken, war vorher nur aus dem Code gelesen, nicht ausgeloest. Ein 2-KB-Zip
    /// mit denselben relativen Pfaden wie das echte 8,5-GB-Paket beweist es, ohne etwas zu laden.
    /// Ohne die Wurzel-Auflösung entstuende
    /// _classic_era_/World of Warcraft/_classic_era_/WowClassic.exe — eine doppelt verschachtelte
    /// Installation, die sich bei jedem weiteren Durchlauf vertieft.
    /// </summary>
    [Fact]
    public async Task Extract_LandsInThePackageRoot_NotOneLevelTooDeep()
    {
        var packageRoot = NewTempDir();
        try
        {
            var exeDir = Path.Combine(packageRoot, "World of Warcraft", "_classic_era_");
            Directory.CreateDirectory(exeDir);
            // Bestehende Installation, wie sie nach dem Erstinstallieren aussieht.
            File.WriteAllText(Path.Combine(exeDir, "WowClassic.exe"), "old-exe");
            Directory.CreateDirectory(Path.Combine(packageRoot, "Hermes", "CSV"));
            File.WriteAllText(Path.Combine(packageRoot, "Hermes", "CSV", "AreaNames.csv"), "old-areas");

            var zipPath = Path.Combine(Path.GetTempPath(), "wl-zip-" + Path.GetRandomFileName() + ".zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                AddEntry(zip, "Hermes/CSV/AreaNames.csv", "new-areas");
                AddEntry(zip, "World of Warcraft/_classic_era_/WowClassic.exe", "new-exe");
            }

            try
            {
                // Genau das, was Repair/Update uebergeben: das EXE-Verzeichnis.
                var ok = await new DownloadService(new System.Net.Http.HttpClient(), Serilog.Log.Logger)
                    .ExtractClientAsync(zipPath, exeDir);

                Assert.True(ok);
                // Im Paket-Root aktualisiert:
                Assert.Equal("new-areas", File.ReadAllText(Path.Combine(packageRoot, "Hermes", "CSV", "AreaNames.csv")));
                Assert.Equal("new-exe", File.ReadAllText(Path.Combine(exeDir, "WowClassic.exe")));
                // Und NICHT ein zweites Mal verschachtelt:
                Assert.False(Directory.Exists(Path.Combine(exeDir, "World of Warcraft")),
                    "Entpacken hat eine zweite 'World of Warcraft/'-Ebene im Exe-Verzeichnis erzeugt");
                Assert.False(Directory.Exists(Path.Combine(exeDir, "Hermes")),
                    "Entpacken hat Hermes/ eine Ebene zu tief angelegt");
            }
            finally { File.Delete(zipPath); }
        }
        finally { Directory.Delete(packageRoot, true); }
    }

    private static void AddEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
