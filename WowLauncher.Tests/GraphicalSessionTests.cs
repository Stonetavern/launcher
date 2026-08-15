using System;
using System.Collections.Generic;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Was ein Mensch liest, wenn der Launcher kein Fenster oeffnen kann.
///
/// <para><b>Gemessen, nicht ausgedacht</b> (erster Lauf in der nackten Ubuntu-VM, 2026-08-05): der
/// Launcher endete mit <c>System.Exception: XOpenDisplay failed</c> und acht Zeilen Spurabzug. Das ist
/// ein Text fuer Entwickler, und er widerspricht der eigenen Regel des Projekts - kein nackter
/// Spurabzug erreicht je einen Spieler. Abgestuerzt war dabei gar nichts: es fehlte ein Bildschirm,
/// und das ist die einzige Auskunft, die weiterhilft.</para>
/// </summary>
public sealed class GraphicalSessionTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) map[k] = v;
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>Ohne jede Anzeige gibt es etwas zu sagen - und mit einer nicht. Die beiden Faelle
    /// muessen unterscheidbar sein, sonst redet der Launcher entweder immer oder nie ueber
    /// Bildschirme.</summary>
    [Fact]
    public void OhneAnzeige_GibtEsEtwasZuSagen_MitAnzeigeNicht()
    {
        Assert.NotNull(GraphicalSession.Missing(Env(), isLinux: true));
        Assert.Null(GraphicalSession.Missing(Env(("DISPLAY", ":0")), isLinux: true));
        Assert.Null(GraphicalSession.Missing(Env(("WAYLAND_DISPLAY", "wayland-0")), isLinux: true));
    }

    /// <summary>Windows und macOS haben immer eine Anzeige. Eine Pruefung, die dort etwas behauptet,
    /// waere eine Meldung ueber einen Zustand, den es nicht gibt.</summary>
    [Fact]
    public void AusserhalbVonLinux_WirdNichtsBehauptet() =>
        Assert.Null(GraphicalSession.Missing(Env(), isLinux: false));

    /// <summary>
    /// Der Text nennt die Werte, um die es geht. Wer ihn liest, hat gerade kein Fenster, in dem er
    /// etwas nachschlagen koennte - und „nicht gesetzt" ist hier die halbe Diagnose.
    /// </summary>
    [Fact]
    public void DerText_NenntDieWerteUmDieEsGeht()
    {
        var text = GraphicalSession.Missing(Env(("XDG_SESSION_TYPE", "tty")), isLinux: true)!;

        Assert.Contains("DISPLAY: not set", text, StringComparison.Ordinal);
        Assert.Contains("WAYLAND_DISPLAY: not set", text, StringComparison.Ordinal);
        Assert.Contains("XDG_SESSION_TYPE: tty", text, StringComparison.Ordinal);
        Assert.Contains("SSH", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Der zweite Fall, und der haeufigere: eine Anzeige ist angegeben, laesst sich aber nicht
    /// oeffnen. Genau so lag es in der VM (<c>DISPLAY=:0</c> stand da, die Berechtigung fehlte). Der
    /// urspruengliche Fehler gehoert dazu, sonst ist der Text zwar freundlich und fuer eine Diagnose
    /// wertlos.
    /// </summary>
    [Fact]
    public void WirdDieAnzeigeVerweigert_StehtDerGrundDabei()
    {
        var text = GraphicalSession.Refused(Env(("DISPLAY", ":0")), "XOpenDisplay failed");

        Assert.Contains("DISPLAY: :0", text, StringComparison.Ordinal);
        Assert.Contains("XOpenDisplay failed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", text, StringComparison.Ordinal);
    }

    /// <summary>Nach den Regeln fuer Spielertexte: keine Gedankenstriche, keine Apostrophe. Dieselbe
    /// Pruefung, die der Sprachkatalog bekommt - dieser Text steht nur nicht darin, weil er
    /// erscheint, bevor irgendetwas geladen ist.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("refused")]
    public void DerText_HaeltSichAnDieRegelnFuerSpielertexte(string which)
    {
        var text = which == "missing"
            ? GraphicalSession.Missing(Env(), isLinux: true)!
            : GraphicalSession.Refused(Env(), "irgendwas");

        Assert.DoesNotContain("—", text, StringComparison.Ordinal);
        Assert.DoesNotContain("–", text, StringComparison.Ordinal);
        Assert.DoesNotContain("'", text, StringComparison.Ordinal);
    }
}
