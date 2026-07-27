namespace WowLauncher.Services;

/// <summary>
/// Lets a ViewModel ask the player to pick a folder without knowing anything about
/// <c>Avalonia.Controls</c> — the View/platform seam the MVVM rule in the avalonia-desktop skill
/// requires (no <c>TopLevel</c>/<c>StorageProvider</c> reference in a ViewModel).
/// </summary>
public interface IFolderPickerService
{
    /// <summary>
    /// Shows a native folder picker. Returns the chosen local path, or null when the player cancelled,
    /// no window/storage provider is available (headless/screenshot/e2e), or the chosen location has no
    /// local filesystem path (e.g. an MTP device) — never throws.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="startAt">Folder the dialog should open in, if it still exists. Optional.</param>
    Task<string?> PickFolderAsync(string title, string? startAt = null);
}
