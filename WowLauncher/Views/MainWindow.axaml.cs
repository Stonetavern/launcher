using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace WowLauncher.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // FIX A (v1.0.1): unter Linux zeichnet der Fenstermanager bei BorderOnly einen hellen
        // server-side Aktiv-Rahmen (KWin) um das Fenster. None entfernt die WM-Dekoration ganz;
        // die eigene dunkle App-Border (StoneBorder, 1px) wird zur Fensterkante. Drag/Min/Close
        // laufen bereits client-seitig (OnTitleBarPressed → BeginMoveDrag, Chrome-Buttons).
        // Windows-Pfad bleibt UNBERÜHRT bei BorderOnly (dort ist der Rahmen dunkel/ok).
        if (OperatingSystem.IsLinux())
            WindowDecorations = WindowDecorations.None;
    }

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
