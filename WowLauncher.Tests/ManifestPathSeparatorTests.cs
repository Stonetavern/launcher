namespace WowLauncher.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using Xunit;

/// <summary>
/// Beide Hälften eines Mechanismus müssen dasselbe für einen Pfad halten — und zwar das Richtige.
///
/// <para><b>Der Befund.</b> <see cref="ContentRoot.Resolve"/> bildete vor der Wurzelwahl <c>\</c> UND
/// <c>/</c> auf den Plattform-Trenner ab, <see cref="ClientVerifyService"/> danach nur <c>/</c>. Auf
/// Windows unsichtbar; auf macOS und Linux zählte die Wurzelwahl einen Eintrag als vorhanden, den der
/// Prüfer als fehlend meldete. Zwei Hälften, zwei Meinungen.</para>
///
/// <para><b>Die erste Reparatur war die falsche.</b> Sie hat den Prüfer an die Wurzelwahl angeglichen,
/// also auf Unix ebenfalls umgedeutet — und damit eine Prüfung gebaut, die eine Datei als vorhanden
/// melden kann, die es nicht gibt: auf Unix ist <c>\</c> ein legales Zeichen in einem Dateinamen, und
/// <c>dir\file</c> ist dort eine ANDERE Datei als <c>dir/file</c>. Der zugehörige Test bewies nur die
/// Umdeutung, nicht ihre Korrektheit. Codex-Review 2026-09-14; die Regel liegt jetzt in
/// <see cref="ManifestPath"/> und ist plattformabhängig, weil die Wirklichkeit es ist.</para>
///
/// <para><c>deploy/MANIFEST-SCHEMA.md</c> §files[].path verlangt <c>/</c>, immer. Ein Backslash ist
/// also ein Generatorfehler; er wird protokolliert, nicht zum harten Abbruch gemacht — ein Launcher,
/// der ein Manifest verwirft, sperrt im Zweifel alle Spieler aus. Nachgemessen 2026-09-14: keines der
/// ausgelieferten Manifeste enthält heute einen Backslash.</para>
///
/// <para>🔴 Unicode (NFC/NFD) wird bewusst NICHT normalisiert: auf macOS wäre es folgenlos, auf Linux
/// wären zwei Normalformen zwei verschiedene Dateien. Gemessen: keines der ausgelieferten Manifeste
/// enthält ein Zeichen über ASCII.</para>
/// </summary>
public sealed class ManifestPathSeparatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sep-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static Serilog.ILogger Log() => Serilog.Core.Logger.None;

    private static string Sha(string content) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();

    /// <summary>Legt die ausgelieferte Paketform auf die Platte: Proxy-Baum plus Client-Baum unter
    /// einer gemeinsamen Wurzel, Exe zwei Ebenen tief — genau die Form, die
    /// <c>modern-1.14.2-macos-files.json</c> beschreibt.</summary>
    private (string packageRoot, string exeDir, List<(string rel, string body)> files) LayOutPackage()
    {
        var packageRoot = Path.Combine(_root, "WoW-Client-42597");
        var exeDir = Path.Combine(packageRoot, "World of Warcraft", "_classic_era_");
        Directory.CreateDirectory(Path.Combine(packageRoot, "Hermes", "CSV"));
        Directory.CreateDirectory(exeDir);

        var files = new List<(string rel, string body)>();
        for (var i = 0; i < 120; i++)
            files.Add(($"Hermes/CSV/Table{i:D3}.csv", $"row-{i}"));
        files.Add(("World of Warcraft/_classic_era_/WowClassic.exe", "MZ"));

        foreach (var (rel, body) in files)
        {
            var full = Path.Combine(packageRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, body);
        }
        return (packageRoot, exeDir, files);
    }

    private static ClientFileManifest ManifestWith(IEnumerable<(string rel, string body)> files, char separator)
    {
        var manifest = new ClientFileManifest { Build = 42597 };
        foreach (var (rel, body) in files)
            manifest.Files.Add(new ClientFileEntry
            {
                Path = rel.Replace('/', separator),
                Size = System.Text.Encoding.UTF8.GetByteCount(body),
                Sha256 = Sha(body),
            });
        return manifest;
    }

    // ── Der Normalfall ───────────────────────────────────────────────────────────────────────────

    /// <summary>Die Positivkontrolle, ohne die keiner der Tests darunter etwas beweist: das echte
    /// Paketlayout mit der Wire-Schreibweise ist unversehrt, und die Wurzel wird zwei Ebenen über dem
    /// Exe-Ordner gefunden.</summary>
    [Fact]
    public async Task DasEchtePaketMitNormalenPfaden_IstUnversehrt()
    {
        var (_, exeDir, files) = LayOutPackage();

        var report = await new ClientVerifyService(Log()).VerifyAsync(exeDir, ManifestWith(files, '/'));

        Assert.True(report.IsIntact,
            $"missing={report.Missing.Count} corrupt={report.Corrupt.Count} ok={report.Ok}");
        Assert.Equal(files.Count, report.FoundAtManifestPath);
    }

    // ── Die Trennzeichenregel, in beide Richtungen ───────────────────────────────────────────────

    /// <summary>
    /// 🔴 Auf Unix darf ein Manifest-Eintrag mit Backslash NICHT von einer anderen Datei erfüllt
    /// werden. Genau das hätte die erste Reparatur getan: sie hätte <c>Hermes\CSV\Table000.csv</c> als
    /// <c>Hermes/CSV/Table000.csv</c> gelesen und eine Datei als vorhanden gemeldet, nach der niemand
    /// gefragt hat. Ein Integritätsprüfer darf in genau diese Richtung nie irren.
    /// </summary>
    [Fact]
    public async Task EinEintragMitBackslash_TrifftNurDortZu_WoDerBackslashEinTrennerIst()
    {
        var (_, exeDir, files) = LayOutPackage();

        var report = await new ClientVerifyService(Log()).VerifyAsync(exeDir, ManifestWith(files, '\\'));

        // Kein stiller Ausstieg per return: der Test misst auf JEDER Plattform und sagt, was dort
        // gilt. Ein gruener Linux-Lauf, der die Windows-Regel nur uebersprungen haette, saehe wie ein
        // Beweis aus und waere keiner (Review 2026-09-14). Die Windows-Zusicherung ist hier formuliert,
        // aber auf dieser Maschine NICHT ausgefuehrt - das steht so auch im Bericht.
        Assert.Equal(OperatingSystem.IsWindows(), report.IsIntact);
        Assert.Equal(OperatingSystem.IsWindows() ? files.Count : 0, report.FoundAtManifestPath);
        Assert.Equal(OperatingSystem.IsWindows() ? 0 : files.Count, report.Missing.Count);
    }

    /// <summary>
    /// 🔴 Und die Gegenrichtung, die zeigt, dass der Prüfer den Pfad wörtlich nimmt statt ihn nur
    /// abzulehnen: heißt die Datei auf Unix wirklich <c>CSV\Table.csv</c>, dann erfüllt sie den
    /// Eintrag <c>Hermes\CSV\Table.csv</c> — und der Eintrag mit Schrägstrich erfüllt sie nicht.
    /// </summary>
    [Fact]
    public async Task EinBackslashImNamen_IstAufUnixEineDatei_UndAufWindowsEinPfad()
    {
        var packageRoot = Path.Combine(_root, "Literal");
        Directory.CreateDirectory(packageRoot);
        // Auf Unix ist das EINE Datei mit Backslashes im Namen, direkt in der Wurzel. Auf Windows
        // erzeugt derselbe Aufruf den verschachtelten Pfad - deshalb die Verzeichnisse vorher anlegen,
        // damit der Test auf beiden Plattformen wirklich laeuft statt sich davonzustehlen.
        var target = Path.Combine(packageRoot, "Hermes\\CSV\\Table.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "row");

        var entry = new ClientFileEntry
        {
            Path = "Hermes\\CSV\\Table.csv", Size = 3, Sha256 = Sha("row"),
        };
        var literal = new ClientFileManifest { Build = 42597, Files = [entry] };
        var slashed = new ClientFileManifest
        {
            Build = 42597,
            Files = [new ClientFileEntry { Path = "Hermes/CSV/Table.csv", Size = 3, Sha256 = Sha("row") }],
        };

        var service = new ClientVerifyService(Log());
        Assert.True((await service.VerifyAsync(packageRoot, literal)).IsIntact,
            "Der Eintrag beschreibt genau diese Datei — sie muss gefunden werden.");
        // Auf Windows beschreibt der Schraegstrich-Eintrag DIESELBE Datei (beide Trenner sind dort
        // Trenner), auf Unix eine andere, die es nicht gibt.
        Assert.Equal(OperatingSystem.IsWindows(),
            (await service.VerifyAsync(packageRoot, slashed)).IsIntact);
    }

    /// <summary>Der plattformabhängige Vertrag selbst, ohne Dateisystem: auf Windows ist ein
    /// Backslash ein Trenner (er kann in einem Windows-Dateinamen gar nicht vorkommen), auf Unix
    /// bleibt er stehen.</summary>
    [Fact]
    public void ToLocal_DeutetDenBackslashNurDortUm_WoErKeinDateinameSeinKann()
    {
        var local = ManifestPath.ToLocal("Hermes\\CSV\\Table.csv");
        var segments = local.Split(Path.DirectorySeparatorChar).Length;

        // Die Zeichenkette sieht auf beiden Plattformen gleich aus — was sie BEDEUTET, nicht: auf
        // Windows sind das drei Pfadteile, auf Unix ist es EIN Dateiname, der Backslashes enthält.
        // Eine Prüfung auf die Zeichen allein wäre hier ein Placebo.
        Assert.Equal(OperatingSystem.IsWindows() ? 3 : 1, segments);

        // Und der Schrägstrich ist überall ein Trenner, das ist die Wire-Konvention.
        Assert.Equal(Path.Combine("Hermes", "CSV", "Table.csv"), ManifestPath.ToLocal("Hermes/CSV/Table.csv"));
    }

    /// <summary>Der Schemaverstoß wird erkannt, damit er protokolliert werden kann. Ohne diese Zeile
    /// wäre der stille Teil des Verhaltens — „liest sich als fehlend" — nicht erklärbar.</summary>
    [Theory]
    [InlineData("Hermes/CSV/Table.csv", false)]
    [InlineData("Hermes\\CSV\\Table.csv", true)]
    [InlineData("", false)]
    public void EinBackslashImManifest_GiltAlsSchemaverstoss(string path, bool violates) =>
        Assert.Equal(violates, ManifestPath.ViolatesSeparatorRule(path));

    // ── Und dass beide Hälften jetzt dieselbe Regel benutzen ─────────────────────────────────────

    /// <summary>
    /// 🔴 Die Wurzelwahl darf einen Backslash-Eintrag auf Unix nicht als Treffer zählen — sonst
    /// widersprechen sich die beiden Hälften wieder, nur mit vertauschten Rollen.
    /// </summary>
    [Fact]
    public void DieWurzelwahl_ZaehltEinenBackslashEintragNurDortAlsTreffer_WoErEinPfadIst()
    {
        var (packageRoot, exeDir, files) = LayOutPackage();
        var wirePaths = new List<string>();
        foreach (var (rel, _) in files) wirePaths.Add(rel.Replace('/', '\\'));

        // Auf Unix trifft kein einziger Pfad und keine Elternebene ist beschreibbar: die Wurzel bleibt,
        // wo sie angefangen hat, statt einen Treffer zu behaupten, den der Pruefer gleich darauf
        // bestreitet. Auf Windows ist der Backslash ein Trenner, also findet dieselbe Anlage die
        // Paketwurzel - beide Haelften sagen dann wieder dasselbe.
        Assert.Equal(OperatingSystem.IsWindows() ? packageRoot : exeDir,
            ContentRoot.Resolve(exeDir, wirePaths));

        // Positivkontrolle mit derselben Anlage: mit der Wire-Schreibweise findet sie die Paketwurzel.
        var ok = new List<string>();
        foreach (var (rel, _) in files) ok.Add(rel);
        Assert.Equal(packageRoot, ContentRoot.Resolve(exeDir, ok));
    }

    /// <summary>
    /// Die Unterschrift aus ST-KQYG-ARA3, nachgestellt: ein fremder Client in Blizzard-Form
    /// (<c>&lt;Ordner&gt;/_classic_era_/</c>, kein <c>Hermes</c>, keine Zwischenebene „World of
    /// Warcraft") gegen unser Paket-Manifest. Die wenigen „ok" sind Ausnahmen, die nie auf der Platte
    /// gesucht werden — deshalb darf der Bericht das nicht als Beschädigung lesen.
    /// </summary>
    [Fact]
    public async Task EinFremdesPaketlayout_LiestSichAlsFremdesPaket_NichtAlsSchaden()
    {
        var blizzardDir = Path.Combine(_root, "WoW Classic 1.14.2", "_classic_era_");
        Directory.CreateDirectory(blizzardDir);
        await File.WriteAllTextAsync(Path.Combine(blizzardDir, "WowClassic.exe"), "MZ");

        var files = new List<(string rel, string body)>();
        for (var i = 0; i < 120; i++)
            files.Add(($"Hermes/CSV/Table{i:D3}.csv", $"row-{i}"));
        files.Add(("World of Warcraft/_classic_era_/WTF/Config.wtf", "SET locale \"enUS\""));

        var report = await new ClientVerifyService(Log()).VerifyAsync(blizzardDir, ManifestWith(files, '/'));

        Assert.True(report.LooksLikeADifferentPackage,
            $"checkable={report.Checkable} found={report.FoundAtManifestPath} ok={report.Ok}");
        Assert.Equal(0, report.FoundAtManifestPath);
        Assert.Equal(1, report.Ok);            // die WTF-Ausnahme, die nie auf der Platte gesucht wird
        Assert.Equal(120, report.Missing.Count);
    }
}
