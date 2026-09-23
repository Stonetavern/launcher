namespace WowLauncher.Tests;

using System;
using System.Diagnostics;
using System.IO;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

/// <summary>
/// Der Rückweg unter macOS — bis 2026-09-15 gab es ihn nicht.
///
/// <para><b>Wie es aufgefallen ist.</b> Nicht durch Lesen, sondern durch einen echten Durchlauf auf
/// einem echten Mac: das ausgelieferte Bundle 1.8.0 aktualisierte sich sauber auf 1.8.5, und danach
/// lag <c>Stonetavern.app.old/Contents/MacOS/update-health.txt</c> unberührt da.
/// <c>UpdateService</c> schrieb den Sentinel also brav — nur las ihn niemand. Das macOS-Tauschskript
/// kannte weder <c>SENTINEL</c> noch <c>QUARANTINE</c> noch ein Protokoll (gemessen gegen den
/// Linux-Zweig als Positivkontrolle: dort 5 / 2 / 12 Treffer, auf macOS je 0).</para>
///
/// <para><b>Warum der Sentinel NEBEN dem Bundle liegen muss.</b> Auf Linux wird eine Datei getauscht
/// und ihr Verzeichnis bleibt stehen. Auf macOS wird das ganze Bundle umbenannt — alles darin wandert
/// mit dem alten Bundle weg. Ein Sentinel in <c>Contents/MacOS</c> ist nach dem Tausch genau dort, wo
/// niemand mehr nachsieht. Deshalb prüft <see cref="SentinelUndSkriptMeinenDieselbeStelle"/> beide
/// Seiten gegeneinander: die Stelle, an der <c>UpdateHealth</c> schreibt, und die, an der das Skript
/// sucht, dürfen nie auseinanderlaufen.</para>
///
/// <para>Die Tests <b>führen das erzeugte Skript aus</b>, gegen echte Verzeichnisse. Nur den
/// Skripttext zu prüfen würde beweisen, dass wir aufgeschrieben haben, was wir meinten — nicht, dass
/// am Ende der richtige Build dasteht. 🔴 Was Linux nicht beantworten kann: ob <c>open</c> das Bundle
/// wirklich zurückbringt. Auf Linux gibt es kein <c>open</c>; die Renames sind dieselben Syscalls,
/// der Start des Bundles ist es nicht. Ein Lauf auf einem echten Mac bleibt dafür geschuldet.</para>
/// </summary>
public sealed class MacUpdateHealthScriptTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "machealth-" + Guid.NewGuid().ToString("N"));

    public MacUpdateHealthScriptTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* Aufräumen */ }
    }

    private string Sentinel => Path.Combine(_root, UpdateHealth.SentinelName);
    private string Quarantine => Path.Combine(_root, UpdateHealth.QuarantineName);

    [Fact]
    public void SentinelUndSkriptMeinenDieselbeStelle()
    {
        // Der Vertrag zwischen den zwei Seiten, als Test festgehalten. Laufen sie auseinander, ist der
        // Vertrag wieder vorhanden und wirkungslos — und das sieht man an keiner Ampel.
        var bundle = Path.Combine(_root, "Stonetavern.app");
        var script = MacUpdateSwapStrategy.BuildScript(
            Path.Combine(_root, "neu", "Stonetavern.app"), bundle, Path.Combine(_root, "neu"),
            1234, "WowLauncher", target: new Version(2, 0, 0));

        Assert.Contains($"SENTINEL='{Path.Combine(_root, UpdateHealth.SentinelName)}'", script,
            StringComparison.Ordinal);
        Assert.Contains($"QUARANTINE='{Path.Combine(_root, UpdateHealth.QuarantineName)}'", script,
            StringComparison.Ordinal);

        // …und ausdrücklich NICHT im Bundle, denn das wird umbenannt.
        Assert.DoesNotContain(Path.Combine(bundle, "Contents"), script, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplySwap_ReichtDieZielversionBisInsSkript()
    {
        // 🔴 Diese Prüfung fehlte zuerst, und die Gegenprobe hat es gemerkt: alle anderen Tests hier
        // rufen BuildScript DIREKT und übergeben die Zielversion selbst. Baut man die Weitergabe in
        // ApplySwap wieder aus, bleiben sie deshalb grün — und genau dieser Fehler war auf Linux
        // schon einmal da ("die Zielversion wurde bis in ApplySwap durchgereicht und dort
        // fallengelassen"). Ein Test, der die Stelle nicht berührt, schützt sie nicht.
        var bundle = Path.Combine(_root, "Stonetavern.app");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
        var laufend = Path.Combine(bundle, "Contents", "MacOS", "WowLauncher");
        File.WriteAllText(laufend, "#!/bin/sh\n");
        var paket = Path.Combine(_root, "update.zip");
        File.WriteAllText(paket, "egal — das Entpacken ist hier eine Naht");

        string? staged = null;
        var strategie = new MacUpdateSwapStrategy(
            Serilog.Core.Logger.None,
            spawn: (_, args) => { staged = args[0]; return true; },
            unpack: (_, ziel) =>
            {
                var contents = Path.Combine(ziel, "Stonetavern.app", "Contents");
                var neu = Path.Combine(contents, "MacOS");
                Directory.CreateDirectory(neu);
                // Ohne Info.plist ist das kein Bundle: ExecutableNameOf weigert sich dann — zu Recht,
                // denn ein Launcher, der nicht starten kann, darf nicht installiert werden.
                File.WriteAllText(Path.Combine(contents, "Info.plist"),
                    "<plist><dict><key>CFBundleExecutable</key><string>WowLauncher</string></dict></plist>");
                var exe = Path.Combine(neu, "WowLauncher");
                File.WriteAllText(exe, "#!/bin/sh\n");
                File.SetUnixFileMode(exe,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            });

        Assert.True(strategie.ApplySwap(paket, laufend, _root, new Version(2, 0, 0)));
        Assert.NotNull(staged);

        var skript = File.ReadAllText(staged!);
        Assert.Contains("TARGET='2.0.0'", skript, StringComparison.Ordinal);
        Assert.Contains($"SENTINEL='{Path.Combine(_root, UpdateHealth.SentinelName)}'", skript,
            StringComparison.Ordinal);

        // 🔴 NUR die Datei. Die macOS-Strategie legt ihr Skript direkt in Path.GetTempPath() ab (anders
        // als Linux, das ein eigenes 0700-Verzeichnis anlegt) — ein "lösche das Elternverzeichnis"
        // hätte hier /tmp rekursiv gelöscht und dabei die Arbeitsverzeichnisse der anderen Tests
        // mitgenommen. Genau das ist im Gesamtlauf am 2026-09-15 passiert, während der Test allein
        // grün blieb.
        try { File.Delete(staged!); } catch { /* das Skript löscht sich sonst selbst */ }
        try { Directory.Delete(Path.Combine(_root, ".stonetavern-update-" + Environment.ProcessId), true); }
        catch { /* das Staging räumt das Skript selbst weg */ }
    }

    [Fact]
    public void UpdateHealth_SchreibtNebenDasBundle_NichtHinein()
    {
        // Die SCHREIBSEITE des Vertrags. Ohne diesen Test war sie von nichts berührt: die Gegenprobe
        // baute die Auflösung aus, und kein einziger Test wurde rot — der Fehler, der M1 überhaupt
        // erst ermöglicht hat, wäre also ungehindert zurückgekommen.
        var bundle = Path.Combine(_root, "Stonetavern.app");
        var laufend = Path.Combine(bundle, "Contents", "MacOS", "WowLauncher");

        Assert.Equal(_root, UpdateHealth.ResolveDirectory(laufend));

        // Und die Gegenrichtung, damit das hier nicht einfach "gib immer den Großvater zurück" ist:
        // außerhalb eines Bundles bleibt es das Verzeichnis der laufenden Datei.
        var flach = Path.Combine(_root, "stonetavern-launcher");
        Assert.Equal(_root, UpdateHealth.ResolveDirectory(flach));
        var tiefer = Path.Combine(_root, "unterordner", "stonetavern-launcher");
        Assert.Equal(Path.Combine(_root, "unterordner"), UpdateHealth.ResolveDirectory(tiefer));
    }

    [Fact]
    public void OhneZielversion_StehtKeineWacheImSkript()
    {
        // Kein halber Vertrag: gibt es keine Zielversion, soll auch nichts dastehen, was nach einer
        // Wache aussieht. Genau diese Zeile trennt "abgeschaltet" von "vorhanden und wirkungslos".
        var script = MacUpdateSwapStrategy.BuildScript(
            "/tmp/neu/S.app", Path.Combine(_root, "S.app"), "/tmp/neu", 1234, "S");

        Assert.DoesNotContain("SENTINEL", script, StringComparison.Ordinal);
        Assert.DoesNotContain("QUARANTINE", script, StringComparison.Ordinal);
    }

    [Fact]
    public void MeldetSichDerNeueBuild_BleibtErStehen()
    {
        var (bundle, neu) = Bundles();
        File.WriteAllText(Sentinel, "2.0.0\n");
        // Der „neue Build" meldet sich: er löscht den Sentinel und endet. Das ist genau das, was
        // UpdateHealth.ReportHealthy nach dem ersten Fenster tut.
        SetExecutable(bundle, neu, $"rm -f '{Sentinel}'");

        RunScript(MacUpdateSwapStrategy.BuildScript(neu, bundle, Path.GetDirectoryName(neu)!,
            DeadPid(), "WowLauncher", waitTicks: 5, target: new Version(2, 0, 0), healthTicks: 8,
            launchLine: Start));

        Assert.Equal("NEU", Marker(bundle));
        Assert.False(File.Exists(Quarantine), "es wurde zurückgerollt, obwohl der Build sich gemeldet hat");
        Assert.False(Directory.Exists(bundle + MacUpdateSwapStrategy.BrokenSuffix));
        Assert.Contains("health OK", SwapLog(), StringComparison.Ordinal);
    }

    [Fact]
    public void StirbtDerNeueBuild_KommtDerVorherigeZurueck()
    {
        // Der Fall, für den es den Vertrag gibt: Tausch in Ordnung, Programm stirbt vor dem ersten
        // Fenster. Auf macOS passierte bis hierher gar nichts — der Spieler behielt den kaputten Build.
        var (bundle, neu) = Bundles();
        File.WriteAllText(Sentinel, "2.0.0\n");
        SetExecutable(bundle, neu, "exit 0");   // startet und ist sofort wieder weg, ohne Meldung

        RunScript(MacUpdateSwapStrategy.BuildScript(neu, bundle, Path.GetDirectoryName(neu)!,
            DeadPid(), "WowLauncher", waitTicks: 5, target: new Version(2, 0, 0), healthTicks: 4));

        Assert.Equal("ALT", Marker(bundle));
        Assert.True(Directory.Exists(bundle + MacUpdateSwapStrategy.BrokenSuffix),
            "der kaputte Build wurde gelöscht statt aufgehoben — dann kann ihn niemand mehr ansehen");
        Assert.True(File.Exists(Quarantine), "ohne Quarantäne-Notiz läuft der zurückgeholte Build in denselben Absturz");
        Assert.Equal("2.0.0", File.ReadAllText(Quarantine).Trim());
        Assert.False(File.Exists(Sentinel));
        Assert.Contains("zurueckgerollt", SwapLog(), StringComparison.Ordinal);
    }

    [Fact]
    public void LaeuftDerNeueBuildNoch_WirdNichtZurueckgerollt()
    {
        // Langsam ist nicht kaputt. Einen möglicherweise nur beschäftigten Launcher abzuschießen wäre
        // ein schlimmerer Fehler als der, der hier verhindert wird.
        var (bundle, neu) = Bundles();
        File.WriteAllText(Sentinel, "2.0.0\n");
        SetExecutable(bundle, neu, "sleep 30");

        RunScript(MacUpdateSwapStrategy.BuildScript(neu, bundle, Path.GetDirectoryName(neu)!,
            DeadPid(), "WowLauncher", waitTicks: 5, target: new Version(2, 0, 0), healthTicks: 3,
            launchLine: Start));

        Assert.Equal("NEU", Marker(bundle));
        Assert.False(File.Exists(Quarantine));
        Assert.Contains("health UNKLAR", SwapLog(), StringComparison.Ordinal);

        foreach (var p in Process.GetProcesses())
        {
            try { if (p.ProcessName == "sleep") { /* nicht unsere Sache, nur nicht hängen bleiben */ } }
            catch { /* Prozesse verschwinden während der Aufzählung */ }
        }
    }

    [Fact]
    public void DasSkript_SchreibtEinTauschprotokoll()
    {
        // Auf der einen Plattform, wo ein Mensch die Ausgabe des abgekoppelten Skripts ohnehin nie
        // sieht, gab es bis 2026-09-15 auch keine Datei, in der etwas stand.
        var (bundle, neu) = Bundles();
        SetExecutable(bundle, neu, "exit 0");

        RunScript(MacUpdateSwapStrategy.BuildScript(neu, bundle, Path.GetDirectoryName(neu)!,
            DeadPid(), "WowLauncher", waitTicks: 5));

        Assert.True(File.Exists(Path.Combine(_root, MacUpdateSwapStrategy.SwapLogName)),
            "kein update-swap.log — nach einem fehlgeschlagenen Tausch bliebe keinerlei Spur");
        Assert.Contains("swap OK", SwapLog(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Schreibt das ERZEUGTE Skript heraus, damit es auf einem echten Mac laufen kann.
    ///
    /// <para>🔴 Kein Test im üblichen Sinn, sondern die Brücke über eine Lücke, die sonst offen
    /// bliebe: der Rückfall hängt auf macOS an <c>open</c>, und <c>open</c> gibt es auf dieser
    /// Maschine nicht. Alle Tests hier ersetzen es deshalb durch einen direkten Start — was die
    /// Renames beweist, aber nicht, dass <c>open</c> das Bundle wirklich zurückbringt. Der Mac hat
    /// kein .NET, also wandert der Skripttext dorthin statt die Testsuite.</para>
    ///
    /// <para>Läuft nur mit <c>MECHAGON_MAC_SCRIPT_OUT</c> und <c>MECHAGON_MAC_ROOT</c>, sonst
    /// übersprungen — und übersprungen heißt hier sichtbar übersprungen, nicht still bestanden.</para>
    /// </summary>
    [SkippableFact]
    public void SchreibtDasSkriptFuerEinenEchtenMacHeraus()
    {
        var ziel = Environment.GetEnvironmentVariable("MECHAGON_MAC_SCRIPT_OUT");
        var wurzel = Environment.GetEnvironmentVariable("MECHAGON_MAC_ROOT");
        Skip.If(string.IsNullOrWhiteSpace(ziel) || string.IsNullOrWhiteSpace(wurzel),
            "MECHAGON_MAC_SCRIPT_OUT + MECHAGON_MAC_ROOT nicht gesetzt — nur für den Lauf auf einem echten Mac");

        var bundle = Path.Combine(wurzel!, "Stonetavern.app");
        var neu = Path.Combine(wurzel!, "stage", "Stonetavern.app");
        File.WriteAllText(ziel!, MacUpdateSwapStrategy.BuildScript(
            neu, bundle, Path.Combine(wurzel!, "stage"), DeadPid(), "WowLauncher",
            waitTicks: 5, target: new Version(2, 0, 0), healthTicks: 6));
    }

    // ─── Gerüst ──────────────────────────────────────────────────────────

    /// <summary>Was in Produktion <c>open</c> tut, auf einer Maschine ohne <c>open</c>: das Programm
    /// starten, das das Bundle nennt. Nur damit lassen sich die zwei Zweige pruefen, die davon
    /// abhaengen, dass der neue Build wirklich laeuft.</summary>
    private const string Start = "\"$CUR/Contents/MacOS/$EXE\" &";

    private string SwapLog()
    {
        var p = Path.Combine(_root, MacUpdateSwapStrategy.SwapLogName);
        return File.Exists(p) ? File.ReadAllText(p) : "";
    }

    /// <summary>Zwei Bundles nebeneinander: das installierte und das frisch entpackte.</summary>
    private (string Bundle, string Neu) Bundles()
    {
        var bundle = Path.Combine(_root, "Stonetavern.app");
        var stage = Path.Combine(_root, "stage");
        var neu = Path.Combine(stage, "Stonetavern.app");
        foreach (var b in new[] { bundle, neu })
            Directory.CreateDirectory(Path.Combine(b, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(bundle, "marker"), "ALT");
        File.WriteAllText(Path.Combine(neu, "marker"), "NEU");
        return (bundle, neu);
    }

    /// <summary>Ein „Build" ist ein Shell-Skript: so bestimmt der Test, ob er sich meldet, abstürzt
    /// oder nur langsam ist.</summary>
    private static void SetExecutable(string bundle, string neu, string body)
    {
        foreach (var b in new[] { bundle, neu })
        {
            var exe = Path.Combine(b, "Contents", "MacOS", "WowLauncher");
            File.WriteAllText(exe, "#!/bin/sh\n" + body + "\n");
            File.SetUnixFileMode(exe,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string Marker(string bundle)
    {
        var p = Path.Combine(bundle, "marker");
        return File.Exists(p) ? File.ReadAllText(p).Trim() : "<kein Bundle>";
    }

    private static void RunScript(string script, int timeoutMs = 60_000)
    {
        var path = Path.Combine(Path.GetTempPath(), "macswap-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path, script);
        try
        {
            using var sh = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { path } })!;
            if (!sh.WaitForExit(timeoutMs)) sh.Kill(entireProcessTree: true);
        }
        finally
        {
            try { File.Delete(path); } catch { /* das Skript löscht sich selbst */ }
        }
    }

    /// <summary>Ein Prozess, der sicher weg ist: unser eigenes Kind, abgeholt.</summary>
    private static int DeadPid()
    {
        using var corpse = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "exit 0" } })!;
        corpse.WaitForExit();
        return corpse.Id;
    }
}
