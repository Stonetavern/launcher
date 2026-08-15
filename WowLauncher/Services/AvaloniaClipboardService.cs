namespace WowLauncher.Services;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
// SetTextAsync ist in Avalonia 12 eine Erweiterungsmethode auf IClipboard, keine Schnittstellen-
// Methode mehr. Ohne dieses using kompiliert es nicht - und mit dem falschen Fund landet man beim
// Datenobjekt-Weg, der fuer eine Zeile Text zu viel ist.
using Avalonia.Input.Platform;
using Serilog;

/// <summary>
/// Puts text on the system clipboard.
///
/// <para>An interface for one method, because the alternative is a view model that reaches into
/// <see cref="Avalonia.Input.Platform.IClipboard"/> and can therefore only be tested by opening a
/// window. The whole point of the copy button is what it copies, and that has to be assertable.</para>
/// </summary>
public interface IClipboardService
{
    /// <summary>True when the text is actually on the clipboard. False when there is no window, no
    /// clipboard backend, or the platform refused — never an exception, and never a silent true: a
    /// button that says "Copied" over an empty clipboard sends a player to paste nothing.</summary>
    Task<bool> SetTextAsync(string text);
}

/// <inheritdoc cref="IClipboardService"/>
public sealed class AvaloniaClipboardService(ILogger log) : IClipboardService
{
    public async Task<bool> SetTextAsync(string text)
    {
        // Same guard as the folder picker, same reason: the screenshot harness renders one frame with
        // nobody at the keyboard, and no platform clipboard behind it.
        if (Ui.Headless)
        {
            log.Debug("Clipboard skipped: running headless (--screenshot)");
            return false;
        }

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } window)
        {
            log.Debug("Clipboard skipped: no desktop window available");
            return false;
        }

        var clipboard = TopLevel.GetTopLevel(window)?.Clipboard;
        if (clipboard is null)
        {
            log.Debug("Clipboard skipped: this platform exposes none");
            return false;
        }

        try
        {
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception ex)
        {
            // A refused clipboard (Wayland focus rules, a compositor without the protocol) is a
            // disappointment, not a crash.
            log.Warning(ex, "Could not put the start report on the clipboard");
            return false;
        }
    }
}
