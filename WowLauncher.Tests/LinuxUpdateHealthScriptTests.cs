namespace WowLauncher.Tests;

using System;
using System.Diagnostics;
using System.IO;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

/// <summary>
/// Der Rückweg unter Linux: ein Update, das ankommt und dann nicht startet, muss den vorherigen
/// Build von allein zurückholen.
///
/// <para><b>Warum es diese Tests erst seit 2026-08-04 gibt.</b> Der Gesundheitsvertrag galt als
/// „plattformweit gebaut". Er war es nicht: nur die Windows-Strategie schrieb Sentinel-Wache,
/// Rückrollung und Quarantäne-Notiz. <c>LinuxUpdateSwapStrategy.ApplySwap</c> nahm die Zielversion
/// entgegen und reichte sie nicht an <c>BuildScript</c> weiter — das Skript tauschte, startete neu
/// und sah nie wieder nach. Ein Spieler mit einem kaputten Update hatte einen Launcher, der nicht
/// mehr startet, und eine <c>.old</c>-Datei daneben, die niemand zurückschiebt.</para>
///
/// <para>Diese Tests <b>führen das erzeugte Skript aus</b>, gegen echte Dateien, mit einem echten
/// Kindprozess als „neuer Build". Den Skripttext zu prüfen würde nur beweisen, dass wir
/// aufgeschrieben haben, was wir meinten — nicht, dass am Ende der richtige Build dasteht. Genau
/// diese Lücke hat auf Windows einen Rückfall grün aussehen lassen, der nichts tat.</para>
/// </summary>
public sealed class LinuxUpdateHealthScriptTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "health-" + Guid.NewGuid().ToString("N"));

    public LinuxUpdateHealthScriptTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* Aufräumen */ } }

    private string Sentinel => Path.Combine(_root, UpdateHealth.SentinelName);
    private string Quarantine => Path.Combine(_root, UpdateHealth.QuarantineName);

    /// <summary>Ein „Build" ist hier ein Shell-Skript: so kann der Test bestimmen, ob er sich meldet
    /// (Sentinel löschen), abstürzt (sofort enden) oder nur langsam ist (laufen bleiben).</summary>
    private string WriteBuild(string name, string body)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private string GenerateScript(string newExe, string current, Version? target, int healthTicks)
    {
        var path = Path.Combine(_root, "update.sh");
        File.WriteAllText(path, LinuxUpdateSwapStrategy.BuildScript(
            newExe, current, _root, pid: 999_999, waitTicks: 5, target: target, healthTicks: healthTicks));
        return path;
    }

    private static int RunScript(string script, int timeoutMs = 30_000)
    {
        using var sh = Process.Start(
            new ProcessStartInfo("/bin/sh", script) { UseShellExecute = false })!;
        sh.WaitForExit(timeoutMs);
        return sh.ExitCode;
    }

    // ── Der Fall, für den das Ganze existiert ────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 Der neue Build stirbt, bevor er sich melden kann. Danach muss dastehen: der vorherige Build
    /// unter dem Originalnamen, der kaputte unter <c>.broken</c>, eine Quarantäne-Notiz mit der
    /// Version — und keine Sentinel-Datei mehr.
    /// </summary>
    [Fact]
    public void EinNeuerBuildDerNichtHochkommt_HoltDenVorherigenZurueck()
    {
        var current = WriteBuild("launcher.AppImage", "echo ALT; exit 0");
        var fresh = WriteBuild("launcher.AppImage.download", "echo NEU; exit 1");   // stirbt sofort
        File.WriteAllText(Sentinel, "1.7.0\n");

        Assert.Equal(0, RunScript(GenerateScript(fresh, current, new Version(1, 7, 0), healthTicks: 3)));

        Assert.Contains("ALT", File.ReadAllText(current), StringComparison.Ordinal);
        Assert.True(File.Exists(current + LinuxUpdateSwapStrategy.BrokenSuffix),
            "der kaputte Build gehört aufgehoben, nicht gelöscht");
        Assert.Contains("NEU",
            File.ReadAllText(current + LinuxUpdateSwapStrategy.BrokenSuffix), StringComparison.Ordinal);

        // Die Quarantäne-Notiz ist der Teil, der die Absturzschleife beendet: ohne sie fände der
        // wiederhergestellte Build dieselbe Version im Manifest und liefe erneut hinein.
        Assert.True(File.Exists(Quarantine));
        Assert.Equal("1.7.0", File.ReadAllText(Quarantine).Trim());
        Assert.False(File.Exists(Sentinel));
    }

    /// <summary>Der Normalfall: der neue Build meldet sich. Nichts wird zurückgerollt, und es entsteht
    /// keine Quarantäne, die spätere Updates blockieren würde.</summary>
    [Fact]
    public void EinNeuerBuildDerSichMeldet_BehaeltSeinUpdate()
    {
        var current = WriteBuild("launcher.AppImage", "echo ALT; exit 0");
        var fresh = WriteBuild("launcher.AppImage.download",
            $"rm -f '{Sentinel}'; echo NEU; exit 0");
        File.WriteAllText(Sentinel, "1.7.0\n");

        Assert.Equal(0, RunScript(GenerateScript(fresh, current, new Version(1, 7, 0), healthTicks: 5)));

        Assert.Contains("NEU", File.ReadAllText(current), StringComparison.Ordinal);
        Assert.False(File.Exists(current + LinuxUpdateSwapStrategy.BrokenSuffix));
        Assert.False(File.Exists(Quarantine));
    }

    /// <summary>
    /// Ein Build, der nur langsam ist, behält sein Update. Einen womöglich bloß beschäftigten Launcher
    /// abzuschießen wäre ein schlimmerer Fehler als der, den die Wache verhindert — deshalb ist das
    /// Kriterium „Sentinel steht noch UND der Prozess ist weg", nicht „Sentinel steht noch".
    /// </summary>
    [Fact]
    public void EinLangsamerBuild_WirdNichtZurueckgerollt()
    {
        var current = WriteBuild("launcher.AppImage", "echo ALT; exit 0");
        var fresh = WriteBuild("launcher.AppImage.download", "sleep 20; exit 0");   // lebt, meldet nicht
        File.WriteAllText(Sentinel, "1.7.0\n");

        Assert.Equal(0, RunScript(GenerateScript(fresh, current, new Version(1, 7, 0), healthTicks: 2)));

        Assert.Contains("sleep 20", File.ReadAllText(current), StringComparison.Ordinal);
        Assert.False(File.Exists(current + LinuxUpdateSwapStrategy.BrokenSuffix),
            "ein laufender Build darf nicht zurückgerollt werden");
        Assert.False(File.Exists(Quarantine));
    }

    /// <summary>Ohne Zielversion gibt es keine Wache — das Verhalten, das vor dem Vertrag ausgeliefert
    /// wurde. Der Test hält fest, dass der Schalter wirklich abschaltet und nicht nur nichts findet.</summary>
    [Fact]
    public void OhneZielversion_GibtEsKeineWache()
    {
        var current = WriteBuild("launcher.AppImage", "echo ALT; exit 0");
        var fresh = WriteBuild("launcher.AppImage.download", "exit 1");
        File.WriteAllText(Sentinel, "1.7.0\n");

        Assert.Equal(0, RunScript(GenerateScript(fresh, current, target: null, healthTicks: 2)));

        Assert.Contains("exit 1", File.ReadAllText(current), StringComparison.Ordinal);
        Assert.False(File.Exists(current + LinuxUpdateSwapStrategy.BrokenSuffix));
        Assert.True(File.Exists(Sentinel), "ohne Wache rührt das Skript die Sentinel-Datei nicht an");
    }

    /// <summary>
    /// Kein Rückweg vorhanden: die <c>.old</c>-Datei ist weg, während der neue Build stirbt. Dann bleibt
    /// der neue Build stehen — ein verlorenes Update ist hinnehmbar, ein verlorener Launcher nicht.
    /// </summary>
    [Fact]
    public void OhneRueckweg_BleibtDerNeueBuildStehen()
    {
        var current = WriteBuild("launcher.AppImage", "echo ALT; exit 0");
        var old = current + LinuxUpdateSwapStrategy.PreviousSuffix;
        // Der neue Build stirbt UND räumt den Rückweg weg, bevor die Wache zuschlägt.
        var fresh = WriteBuild("launcher.AppImage.download", $"rm -f '{old}'; exit 1");
        File.WriteAllText(Sentinel, "1.7.0\n");

        Assert.Equal(0, RunScript(GenerateScript(fresh, current, new Version(1, 7, 0), healthTicks: 3)));

        Assert.True(File.Exists(current), "der Launcher muss auf jeden Fall dastehen");
        Assert.False(File.Exists(Quarantine),
            "ohne vollzogenen Rückfall gibt es nichts zu quarantänen");
    }

    /// <summary>Das Protokoll ist der einzige Bericht darüber, was nach dem Ende des Launchers passiert
    /// ist. Ein Rückfall, der nicht darin steht, ist für den Betreiber nicht passiert.</summary>
    [Fact]
    public void EinRueckfall_StehtImProtokoll()
    {
        var current = WriteBuild("launcher.AppImage", "exit 0");
        var fresh = WriteBuild("launcher.AppImage.download", "exit 1");
        File.WriteAllText(Sentinel, "1.7.0\n");

        RunScript(GenerateScript(fresh, current, new Version(1, 7, 0), healthTicks: 3));

        var log = Path.Combine(_root, LinuxUpdateSwapStrategy.SwapLogName);
        Assert.True(File.Exists(log));
        var text = File.ReadAllText(log);
        Assert.Contains("swap OK", text, StringComparison.Ordinal);
        Assert.Contains("zurueckgerollt", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔴 Der Weg, auf dem der Vertrag unter Linux still gar nicht existierte: <c>ApplySwap</c> nahm die
    /// Zielversion entgegen und gab sie nicht weiter. Der Test geht durch die öffentliche Schnittstelle
    /// und liest, was wirklich auf die Platte geschrieben wurde.
    /// </summary>
    [Fact]
    public void ApplySwap_ReichtDieZielversionAnDasSkriptWeiter()
    {
        var current = WriteBuild("launcher.AppImage", "exit 0");
        var fresh = WriteBuild("launcher.AppImage.download", "exit 0");
        string? captured = null;

        var strategy = new LinuxUpdateSwapStrategy(
            Serilog.Core.Logger.None,
            (_, args) => { captured = File.ReadAllText(args[0]); return true; });

        Assert.True(strategy.ApplySwap(fresh, current, _root, new Version(9, 9, 9)));

        Assert.NotNull(captured);
        Assert.Contains("TARGET='9.9.9'", captured, StringComparison.Ordinal);
        Assert.Contains(UpdateHealth.QuarantineName, captured, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔴 Der zweite Weg, auf dem der Vertrag unter Linux still nichts getan hätte: die Sentinel-Datei
    /// landete neben <c>Environment.ProcessPath</c>. Unter einem AppImage zeigt das in die
    /// schreibgeschützte squashfs-Einhängung, die es nach dem Beenden nicht mehr gibt — der Sentinel
    /// wäre dort nicht schreibbar (nur eine Warnung im Protokoll) und das Tauschskript hätte in einem
    /// ganz anderen Verzeichnis danach gesehen. Beides fällt nicht auf: ein fehlender Sentinel
    /// verliert nur das Sicherheitsnetz, laut wird nichts.
    ///
    /// <para>Maßgeblich ist deshalb die Datei, die der Spieler gestartet hat — dieselbe, die der Tausch
    /// ersetzt. <c>$APPIMAGE</c> ist dort die einzige ehrliche Antwort.</para>
    /// </summary>
    [Fact]
    public void UnterEinemAppImage_LiegtDerVertragNebenDerGestartetenDatei()
    {
        var appImage = WriteBuild("stonetavern-launcher.AppImage", "exit 0");
        var before = Environment.GetEnvironmentVariable("APPIMAGE");
        try
        {
            Environment.SetEnvironmentVariable("APPIMAGE", appImage);
            var health = new UpdateHealth(new NirgendwoPaths(), Serilog.Core.Logger.None);

            health.ExpectVersion(new Version(1, 7, 0));

            Assert.True(File.Exists(Sentinel),
                "der Vertrag gehört neben das AppImage, nicht in die Einhängung, aus der es läuft");
            Assert.Equal("1.7.0", File.ReadAllText(Sentinel).Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPIMAGE", before);
        }
    }

    /// <summary>Ohne Zielversion darf im Skript auch keine Wache stehen — sonst wäre der Schalter aus
    /// dem vorigen Test nur zufällig wirkungslos.</summary>
    [Fact]
    public void ApplySwap_OhneZielversion_ErzeugtKeineWache()
    {
        var current = WriteBuild("launcher.AppImage", "exit 0");
        var fresh = WriteBuild("launcher.AppImage.download", "exit 0");
        string? captured = null;

        var strategy = new LinuxUpdateSwapStrategy(
            Serilog.Core.Logger.None,
            (_, args) => { captured = File.ReadAllText(args[0]); return true; });

        Assert.True(strategy.ApplySwap(fresh, current, _root));

        Assert.NotNull(captured);
        Assert.DoesNotContain(UpdateHealth.QuarantineName, captured, StringComparison.Ordinal);
    }

    /// <summary>Verzeichnisse, die absichtlich woanders liegen: greift <see cref="UpdateHealth"/>
    /// versehentlich doch auf sie zurück, fällt es im Test auf statt beim Spieler.</summary>
    private sealed class NirgendwoPaths : WowLauncher.Services.Platform.IAppPaths
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "nirgendwo-" + Guid.NewGuid().ToString("N"));
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
    }
}
