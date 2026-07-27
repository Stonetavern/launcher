namespace WowLauncher.Services;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Serilog;

/// <summary>
/// Avalonia implementation of <see cref="IFolderPickerService"/>: resolves the active window's
/// <see cref="TopLevel"/> and asks its <see cref="IStorageProvider"/> for a folder.
///
/// <para>Never opens a dialog and never throws when a dialog cannot meaningfully be shown — the
/// screenshot harness (<see cref="Ui.Headless"/>) and any run with no <see cref="Window"/> yet
/// (headless test host, a lifetime that never got a <c>MainWindow</c>) both fall through to null
/// instead of touching <see cref="IStorageProvider"/> at all.</para>
/// </summary>
public sealed class AvaloniaFolderPickerService(ILogger log) : IFolderPickerService
{
    public async Task<string?> PickFolderAsync(string title, string? startAt = null)
    {
        // Checked BEFORE any TopLevel/StorageProvider touch: on some backends showing a native dialog
        // with nobody at the keyboard does not throw, it blocks — which would hang the QA screenshot
        // harness (it renders one frame and exits after a fixed timer) instead of failing loudly.
        if (Ui.Headless)
        {
            log.Debug("Folder picker skipped: running headless (--screenshot)");
            return null;
        }

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } window)
        {
            log.Debug("Folder picker skipped: no desktop window available");
            return null;
        }

        var topLevel = TopLevel.GetTopLevel(window);
        var storage = topLevel?.StorageProvider;
        if (storage is null || !storage.CanPickFolder)
        {
            log.Debug("Folder picker skipped: no StorageProvider (or folder picking unsupported) on this platform");
            return null;
        }

        try
        {
            IStorageFolder? startFolder = null;
            if (!string.IsNullOrWhiteSpace(startAt) && Directory.Exists(startAt))
            {
                try { startFolder = await storage.TryGetFolderFromPathAsync(startAt); }
                catch (Exception ex) { log.Debug(ex, "Could not resolve start folder {Path} for the picker", startAt); }
            }

            var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = startFolder,
            });

            var folder = result.Count > 0 ? result[0] : null;
            if (folder is null) return null; // player cancelled

            var localPath = folder.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(localPath))
            {
                // e.g. an MTP/cloud location with no real filesystem path — we cannot write a client
                // install there, so treat it exactly like a cancel rather than handing back garbage.
                log.Warning("Folder picker returned a non-local location — treating as cancelled");
                return null;
            }

            return localPath;
        }
        catch (Exception ex)
        {
            // A picker failure is never worse than "the player did not pick a folder" — surface it as
            // a cancel, not a crash.
            log.Warning(ex, "Folder picker failed");
            return null;
        }
    }
}
