namespace WowLauncher.Services.Platform;

/// <summary>
/// Ob dieser Prozess ueberhaupt ein Fenster oeffnen kann, und was ein Mensch dagegen tun kann.
///
/// <para><b>Der Befund, der das ausgeloest hat</b> (erster Lauf in der nackten Ubuntu-VM,
/// 2026-08-05). Der Launcher wurde ohne erreichbare Anzeige gestartet und beendete sich mit:</para>
///
/// <code>
/// Unhandled exception. System.Exception: XOpenDisplay failed
///    at Avalonia.X11.AvaloniaX11Platform.Initialize(X11PlatformOptions options)
///    ...
/// </code>
///
/// <para>Das ist ein Spurabzug fuer Entwickler, kein Satz fuer einen Menschen. Und es widerspricht
/// der eigenen Regel des Projekts: <em>kein nackter Spurabzug erreicht je einen Spieler</em>. Der
/// Schutzschild in <c>Program.Main</c> faengt zwar alles ab und schreibt ein Absturzprotokoll - aber
/// hier ist gar nichts abgestuerzt. Es fehlte schlicht ein Bildschirm, und das ist die einzige
/// Auskunft, die weiterhilft.</para>
///
/// <para>Rein: liest nur Umgebungsvariablen, die hereingereicht werden. Kein Fenster, kein X11, kein
/// Wayland - damit die Entscheidung geprueft werden kann, ohne eine Anzeige zu haben oder zu
/// fehlen.</para>
/// </summary>
public static class GraphicalSession
{
    /// <summary>
    /// Was einem Menschen zu sagen ist, wenn hier kein Fenster aufgehen kann - oder null, wenn nichts
    /// dagegen spricht.
    ///
    /// <para>Nur unter Linux eine Frage: Windows und macOS haben immer eine Anzeige, und eine Pruefung,
    /// die dort etwas behauptet, waere eine Meldung ueber einen Zustand, den es nicht gibt.</para>
    /// </summary>
    /// <param name="env">Zugriff auf die Umgebung, hereingereicht statt gelesen.</param>
    /// <param name="isLinux">Ob diese Pruefung ueberhaupt zutrifft.</param>
    public static string? Missing(Func<string, string?> env, bool isLinux)
    {
        if (!isLinux) return null;

        var display = env("DISPLAY");
        var wayland = env("WAYLAND_DISPLAY");
        if (!string.IsNullOrWhiteSpace(display) || !string.IsNullOrWhiteSpace(wayland)) return null;

        return Explain(env, "No graphical session was found.");
    }

    /// <summary>
    /// Derselbe Satz fuer den anderen Fall: eine Anzeige ist ANGEGEBEN, laesst sich aber nicht
    /// oeffnen. Genau das ist in der VM passiert (<c>DISPLAY=:0</c> stand da, die Berechtigung fehlte)
    /// und ist der haeufigere Fall von beiden, weil er ueber SSH und in Diensten auftritt.
    /// </summary>
    public static string Refused(Func<string, string?> env, string? detail) =>
        Explain(env, "The graphical session could not be opened.")
        + (string.IsNullOrWhiteSpace(detail) ? "" : $"\nDetails: {detail}");

    private static string Explain(Func<string, string?> env, string headline)
    {
        static string Show(string? value) => string.IsNullOrWhiteSpace(value) ? "not set" : value!;

        // Die Werte gehoeren dazu und nicht in ein Protokoll: sie sind die halbe Diagnose, und wer
        // diesen Text liest, hat gerade kein Fenster, in dem er etwas nachschlagen koennte.
        return headline
             + $"\nDISPLAY: {Show(env("DISPLAY"))}"
             + $"\nWAYLAND_DISPLAY: {Show(env("WAYLAND_DISPLAY"))}"
             + $"\nXDG_SESSION_TYPE: {Show(env("XDG_SESSION_TYPE"))}"
             + "\n\nThe launcher is a window and needs a desktop to open it in."
             + "\nIf you are connected over SSH, start it on the machine itself."
             + "\nIf you are on a server without a desktop, there is nothing to open.";
    }
}
