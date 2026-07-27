namespace WowLauncher.Services;

/// <summary>
/// A narrow seam that lets a ViewModel ask the shell window to hide into the tray and come back, without
/// referencing any Avalonia window type (strict MVVM — the View is a thin renderer). <see cref="App"/>
/// owns the actual window, the tray icon and the UI-thread marshalling; it wires the two callbacks here
/// once it has built the window and confirmed a tray exists. Until then — and in headless/screenshot/e2e
/// runs, which never build the interactive shell — the controller is unconfigured and every call is a
/// safe no-op, so a ViewModel can invoke it unconditionally.
/// </summary>
public interface IShellWindowController
{
    /// <summary>True once <see cref="App"/> has wired real hide/restore actions (interactive app with a
    /// usable tray). False in headless/screenshot/e2e and when no tray host is available — the caller
    /// then keeps the shipped behaviour (a hard exit) rather than hiding into nothing.</summary>
    bool IsConfigured { get; }

    /// <summary>Hide the shell window into the tray. No-op until configured.</summary>
    void HideToTray();

    /// <summary>Bring the shell window back from the tray (visible, un-minimised, focused). No-op until
    /// configured. Safe to call from any thread — the wired action marshals to the UI thread itself.</summary>
    void RestoreFromTray();
}

/// <inheritdoc cref="IShellWindowController"/>
public sealed class ShellWindowController : IShellWindowController
{
    private Action? _hideToTray;
    private Action? _restoreFromTray;

    public bool IsConfigured => _hideToTray is not null && _restoreFromTray is not null;

    /// <summary>Wire the real window actions. <see cref="App"/> passes callbacks that already marshal to
    /// the UI thread, so the controller itself stays free of any Avalonia dependency.</summary>
    public void Configure(Action hideToTray, Action restoreFromTray)
    {
        _hideToTray = hideToTray;
        _restoreFromTray = restoreFromTray;
    }

    public void HideToTray() => _hideToTray?.Invoke();

    public void RestoreFromTray() => _restoreFromTray?.Invoke();
}
