using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace WowLauncher.Views;

/// <summary>
/// v2 shell ("Obsidian Instrument"). Same ViewModels as v1 — only the skin differs.
/// Started with <c>WowLauncher.exe --ui v2</c>.
/// </summary>
public partial class ShellV2Window : Window
{
    public ShellV2Window()
    {
        AvaloniaXamlLoader.Load(this);

        // FIX A (v1.0.1): siehe MainWindow — Linux/KWin zeichnet bei BorderOnly einen hellen
        // Aktiv-Rahmen. None entfernt ihn; die eigene OHair-App-Border (1px) wird zur Kante.
        // Drag/Min/Close sind bereits client-seitig. Windows bleibt UNBERÜHRT bei BorderOnly.
        if (OperatingSystem.IsLinux())
            WindowDecorations = WindowDecorations.None;
    }

    // The window has no OS titlebar (WindowDecorations=BorderOnly), so it drags by its own.
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
