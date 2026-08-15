using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WowLauncher.Views;

/// <summary>
/// The start screen. View-only mechanics: per-OS decorations and the motion gate. All state lives in
/// <see cref="ViewModels.SplashViewModel"/>, and the window is opened and closed by <see cref="App"/> —
/// it never decides its own lifetime, which is what keeps "the splash outlived its work" impossible
/// to reach from here.
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Same reason as the shells: KWin draws a bright active frame at BorderOnly, and the declared
        // None is what Windows already gets. macOS keeps its native frame off a splash entirely.
        if (OperatingSystem.IsLinux())
            WindowDecorations = WindowDecorations.None;

        UpdateMotion();
    }

    // The wick and the lantern flicker are selectored under "Border.live" (see SplashWindow.axaml).
    // A splash is closed on every startup path so it cannot be the window that animates behind a game,
    // but a minimised start screen should still paint nothing — same gate, same wiring as the v3 shell.
    private void UpdateMotion()
    {
        var live = IsVisible && WindowState != WindowState.Minimized;
        if (this.FindControl<Border>("Splash") is { } splash)
            splash.Classes.Set("live", live);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == IsVisibleProperty)
            UpdateMotion();
    }
}
