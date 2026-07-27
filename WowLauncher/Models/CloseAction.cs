namespace WowLauncher.Models;

/// <summary>
/// What the launcher does when the player presses the window close button (the titlebar cross or a
/// window-manager close). Persisted in <see cref="LauncherConfig.CloseAction"/> so the "remember my
/// choice" tick survives a restart.
///
/// <para>Default is <see cref="Ask"/>: the launcher shows the keep-running prompt every time until the
/// player says "remember". Hiding into the tray with no memory of consent would be a surprise, and
/// quitting a launcher a player wanted parked behind their game is the same surprise the other way.</para>
/// </summary>
public enum CloseAction
{
    /// <summary>No saved choice yet: show the keep-running prompt.</summary>
    Ask = 0,

    /// <summary>Hide to the system tray and keep running.</summary>
    Background = 1,

    /// <summary>Quit the launcher.</summary>
    Quit = 2,
}
