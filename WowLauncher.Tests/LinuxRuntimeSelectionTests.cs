using System.Threading.Tasks;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Which Wine the game starts with, and — the part a player actually needs — why.
///
/// <para>The whole decision table is provable here because the two lookups and the executable test are
/// seams. Nothing in this file depends on what happens to be installed on the machine running it,
/// which is the point: a test that passes because the dev box has Lutris proves nothing about the
/// player who does not.</para>
/// </summary>
public sealed class LinuxRuntimeSelectionTests
{
    private const string Ge = "/home/p/.local/share/lutris/runners/wine/wine-ge-8-26/bin/wine";
    private const string Sys = "/usr/bin/wine";

    private static LinuxRuntimeDecision Resolve(
        LinuxRuntimeKind requested, string? custom = null,
        string? ge = null, string? sys = null,
        bool executable = true, bool autoPrefersWineGe = false) =>
        LinuxRuntimeSelection.Resolve(
            requested, custom, () => ge, () => sys, _ => executable, autoPrefersWineGe);

    // ── Auto means what it always meant, and it is NOT the same for both clients ─────────────────

    /// <summary>🔴 The regression this parameter exists for, caught on 2026-08-05 by the status line
    /// this feature adds. With one shared "automatic", the 1.12.1 client — a 32-bit binary — was
    /// silently moved onto a wine-ge runner that is frequently an x86_64-only build. Every launcher
    /// before started it on plain wine. A setting whose default changes how the game starts is worse
    /// than no setting at all.</summary>
    [Fact]
    public void Automatisch_Nimmt_Fuer_1_12_1_Das_System_Wine_Auch_Wenn_WineGe_Da_Ist()
    {
        var d = Resolve(LinuxRuntimeKind.Auto, ge: Ge, sys: Sys, autoPrefersWineGe: false);

        Assert.Equal(LinuxRuntimeKind.SystemWine, d.Kind);
        Assert.Equal(Sys, d.Path);
        Assert.Equal(LinuxRuntimeReason.AutoPickedSystemWine, d.Reason);
    }

    [Fact]
    public void Automatisch_Nimmt_Fuer_1_14_2_WineGe_Wenn_Vorhanden()
    {
        var d = Resolve(LinuxRuntimeKind.Auto, ge: Ge, sys: Sys, autoPrefersWineGe: true);

        Assert.Equal(LinuxRuntimeKind.WineGe, d.Kind);
        Assert.Equal(Ge, d.Path);
        Assert.Equal(LinuxRuntimeReason.AutoPickedWineGe, d.Reason);
    }

    /// <summary>Both directions fall through to the other one rather than refusing: a game that starts
    /// beats a refusal, and the reason records which way it went.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Automatisch_Nimmt_Was_Da_Ist_Wenn_Die_Erste_Wahl_Fehlt(bool prefersWineGe)
    {
        var only = prefersWineGe ? Sys : Ge;
        var d = Resolve(LinuxRuntimeKind.Auto,
            ge: prefersWineGe ? null : Ge, sys: prefersWineGe ? Sys : null,
            autoPrefersWineGe: prefersWineGe);

        Assert.Equal(only, d.Path);
        Assert.True(d.Found);
    }

    // ── An explicit choice is honoured, and a fallback is never silent ───────────────────────────

    [Fact]
    public void Wer_WineGe_Waehlt_Bekommt_WineGe()
    {
        var d = Resolve(LinuxRuntimeKind.WineGe, ge: Ge, sys: Sys);

        Assert.Equal(LinuxRuntimeKind.WineGe, d.Kind);
        Assert.Equal(LinuxRuntimeReason.AsRequested, d.Reason);
        Assert.False(d.FellBack);
    }

    [Fact]
    public void Wer_WineGe_Waehlt_Und_Keines_Hat_Bekommt_System_Wine_Und_Erfaehrt_Es()
    {
        var d = Resolve(LinuxRuntimeKind.WineGe, ge: null, sys: Sys);

        Assert.Equal(LinuxRuntimeKind.SystemWine, d.Kind);
        Assert.Equal(Sys, d.Path);
        Assert.Equal(LinuxRuntimeReason.WineGeMissing, d.Reason);
        Assert.True(d.FellBack, "a fallback the player is not told about is the defect, not the fallback");
    }

    [Fact]
    public void Wer_System_Wine_Waehlt_Und_Keines_Hat_Bekommt_WineGe()
    {
        var d = Resolve(LinuxRuntimeKind.SystemWine, ge: Ge, sys: null);

        Assert.Equal(LinuxRuntimeKind.WineGe, d.Kind);
        Assert.True(d.FellBack);
    }

    // ── The custom path, which is also the way to a Proton build ─────────────────────────────────

    [Fact]
    public void Ein_Eigener_Pfad_Wird_Genommen_Wenn_Er_Ausfuehrbar_Ist()
    {
        var d = Resolve(LinuxRuntimeKind.Custom, custom: "/opt/proton/files/bin/wine",
            ge: Ge, sys: Sys, executable: true);

        Assert.Equal(LinuxRuntimeKind.Custom, d.Kind);
        Assert.Equal("/opt/proton/files/bin/wine", d.Path);
        Assert.Equal(LinuxRuntimeReason.AsRequested, d.Reason);
    }

    /// <summary>Exists but cannot be run counts as NOT found. Handing an unexecutable file to a process
    /// start turns a wrong setting into an unexplained launch failure — the player would see the game
    /// fail and the setting still showing their choice.</summary>
    [Fact]
    public void Ein_Eigener_Pfad_Der_Nicht_Ausfuehrbar_Ist_Zaehlt_Nicht_Als_Gefunden()
    {
        var d = Resolve(LinuxRuntimeKind.Custom, custom: "/opt/proton/files/bin/wine",
            ge: Ge, sys: Sys, executable: false);

        Assert.NotEqual(LinuxRuntimeKind.Custom, d.Kind);
        Assert.Equal(LinuxRuntimeReason.CustomMissing, d.Reason);
        Assert.True(d.FellBack);
        Assert.True(d.Found, "falling back to nothing would strand a player over one wrong path");
    }

    [Fact]
    public void Ein_Leerer_Eigener_Pfad_Ist_Kein_Pfad()
    {
        var d = Resolve(LinuxRuntimeKind.Custom, custom: "   ", ge: null, sys: Sys);

        Assert.Equal(LinuxRuntimeKind.SystemWine, d.Kind);
        Assert.Equal(LinuxRuntimeReason.CustomMissing, d.Reason);
    }

    // ── Nothing at all ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(LinuxRuntimeKind.Auto)]
    [InlineData(LinuxRuntimeKind.SystemWine)]
    [InlineData(LinuxRuntimeKind.WineGe)]
    [InlineData(LinuxRuntimeKind.Custom)]
    public void Ohne_Jedes_Wine_Wird_Nichts_Erfunden(LinuxRuntimeKind requested)
    {
        var d = Resolve(requested, custom: "/nope", ge: null, sys: null, executable: false);

        Assert.False(d.Found);
        Assert.Equal(LinuxRuntimeReason.NothingFound, d.Reason);
        Assert.Equal("", d.Path);
    }

    // ── Die Einstellung wirkt SOFORT, nicht erst nach einem Neustart ────────────────────────────

    /// <summary>
    /// 🔴 Der Befund einer Zweitinstanz vom 2026-08-05, und der schwerste des Tages: die Einstellung
    /// wirkte erst nach einem Neustart, während die Statuszeile daneben sofort das Gegenteil
    /// behauptete.
    ///
    /// <para>Der Grund war ein Singleton. Die Wine-Binärdatei wurde beim Aufbau der Anwendung
    /// <b>einmal</b> aus der Konfiguration gelesen; die Einstellungsseite speichert dagegen sofort.
    /// Ein Spieler stellt abends „eigener Pfad" ein, liest darunter „In Benutzung: /opt/proton/…",
    /// drückt SPIELEN — und startet mit dem Runner vom Programmstart. Kein Fehler, keine Logzeile,
    /// kein roter Test.</para>
    ///
    /// <para>Das ist genau die Fehlerform, gegen die diese ganze Einstellung gebaut wurde: ein
    /// plausibler Zustand, der falsch ist. Dass sie in ihrer eigenen Umsetzung wieder auftauchte, ist
    /// der Grund, warum dieser Test hier steht und nicht nur ein Kommentar.</para>
    /// </summary>
    [SkippableFact]
    public async Task EineGeaenderteEinstellung_WirktBeimNaechstenStart_OhneNeustart()
    {
        Skip.IfNot(System.OperatingSystem.IsLinux(), "der Wine-Pfad existiert nur unter Linux");

        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "st-runtime-" + System.Guid.NewGuid().ToString("N"));
        var prefix = System.IO.Path.Combine(root, "prefix");
        // Ein Prefix, das die Bereitschaftspruefung besteht: system.reg da, syswow64 da.
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(prefix, "drive_c", "windows", "syswow64"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(prefix, "system.reg"), "WINE REGISTRY\n");
        // Die 32-Bit-Pruefung verlangt syswow64/cmd.exe und startet es wirklich. Eine leere Datei
        // genuegt: gestartet wird sie von der gefaelschten "wine", die auf alles mit 0 antwortet.
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(prefix, "drive_c", "windows", "syswow64", "cmd.exe"), "");

        // Eine "wine", die auf alles mit 0 antwortet. Sie startet nichts - geprueft wird, WELCHE
        // Datei der Launcher fragt, nicht was sie tut.
        var fake = System.IO.Path.Combine(root, "wine");
        System.IO.File.WriteAllText(fake, "#!/bin/sh\necho 'wine-9.0 (fake)'\nexit 0\n");
        System.IO.File.SetUnixFileMode(fake,
            System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite | System.IO.UnixFileMode.UserExecute);

        var binary = fake;
        var launcher = new WineGameLauncher(
            new Serilog.LoggerConfiguration().CreateLogger(),
            new WineOptions(prefix), () => binary);

        // Erster Start: die Bereitschaft gelingt und wird zwischengespeichert. Er scheitert erst an
        // der fehlenden Client-Datei - und genau daran ist das erkennbar.
        var first = await launcher.LaunchAsync(
            System.IO.Path.Combine(root, "WoW.exe"), root);
        Assert.False(first.Started);
        Assert.True((first.Error ?? "").Contains("Client executable not found"),
            "die Bereitschaftspruefung kam nicht durch, der Test misst also nicht den Cache: "
            + first.Error);

        // Jetzt zeigt die Einstellung auf etwas, das es nicht gibt - so wie ein Spieler, der abends
        // umstellt. Ohne den Fix bliebe die gespeicherte Bereitschaft gueltig und der Launcher
        // liefe weiter in dieselbe Client-Meldung: die neue Einstellung waere wirkungslos, und
        // nichts auf dem Bildschirm haette es gesagt.
        binary = System.IO.Path.Combine(root, "gibt-es-nicht");

        var second = await launcher.LaunchAsync(
            System.IO.Path.Combine(root, "WoW.exe"), root);

        Assert.False(second.Started);
        Assert.DoesNotContain("Client executable not found", second.Error ?? "");

        try { System.IO.Directory.Delete(root, recursive: true); } catch { }
    }

    // ── The stored spelling is part of the contract: it lands in a file players keep ─────────────

    [Theory]
    [InlineData(LinuxRuntimeKind.Auto, "auto")]
    [InlineData(LinuxRuntimeKind.SystemWine, "system")]
    [InlineData(LinuxRuntimeKind.WineGe, "wine-ge")]
    [InlineData(LinuxRuntimeKind.Custom, "custom")]
    public void Die_Gespeicherte_Schreibweise_Ueberlebt_Einen_Rundlauf(LinuxRuntimeKind kind, string stored)
    {
        Assert.Equal(stored, LinuxRuntimeSelection.ToConfigValue(kind));
        Assert.Equal(kind, LinuxRuntimeSelection.Parse(stored));
    }

    /// <summary>A config written by a NEWER launcher must not change how an older one starts the game.
    /// Anything unknown reads as automatic, which is the behaviour that shipped for a year.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("proton-experimental-9")]
    public void Unbekanntes_Liest_Sich_Als_Automatisch(string? stored)
    {
        Assert.Equal(LinuxRuntimeKind.Auto, LinuxRuntimeSelection.Parse(stored));
    }
}
