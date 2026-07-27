using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace WowLauncher.Views;

/// <summary>
/// v3 shell ("Battle.net" layout in the warm stone identity). Same ViewModels as v1/v2 — only the
/// skin differs. Started with <c>WowLauncher.exe --ui v3</c>. Code-behind is view-only chrome
/// mechanics (drag / min / max / close + per-OS decoration handling); all state lives in the
/// ViewModel (the avalonia-desktop skill permits exactly this).
/// </summary>
public partial class ShellV3Window : Window
{
    public ShellV3Window()
    {
        AvaloniaXamlLoader.Load(this);

        if (OperatingSystem.IsMacOS())
        {
            // macOS: use the native traffic lights (extend the client area under them) and reserve
            // the left padding so the wordmark clears them. Hide our own chrome buttons.
            WindowDecorations = WindowDecorations.Full;
            if (this.FindControl<StackPanel>("ChromeButtons") is { } chrome) chrome.IsVisible = false;
            if (this.FindControl<Border>("MacSpacer") is { } spacer) spacer.IsVisible = true;
        }
        else if (OperatingSystem.IsLinux())
        {
            // FIX A (v1.0.1, see MainWindow): Linux/KWin draws a bright active frame at BorderOnly.
            // None removes the WM decoration; our own dark 1px app border becomes the window edge.
            // Drag/min/max/close are already client-side (below). Windows keeps the declared None.
            WindowDecorations = WindowDecorations.None;
        }

        _kenBurns.Tick += (_, _) => StepKenBurns();
        UpdateMotion();
    }

    // ─── Motion gate ──────────────────────────────────────────────────────────
    // Every endless animation in the v3 skin is selectored under "Border.live" — the six of them are
    // enumerated in the motion header of Styles.v3.axaml, and MotionGateTests fails the build if a
    // seventh appears ungated. The Ken Burns is a 34s RenderTransform TRANSITION flipped by the timer
    // below; its Transitions setter is gated too, so dropping "live" tears down the leg that is
    // currently running instead of only skipping the next one. All of it goes off the moment the
    // window is minimised or hidden: a launcher that keeps a core warm behind your game is a defect,
    // and the player is in the game far longer than in here.
    //
    // Why the Ken Burns is a transition and not an Animation: keyframe-animating RenderTransform
    // throws "No animator registered for the property RenderTransform" on Avalonia 12 (documented
    // in Styles.axaml next to the pulse-dot crash fix). A transition uses the composited path that
    // Button:pressed already proves works, and it costs two timer ticks per minute.
    private readonly DispatcherTimer _kenBurns = new() { Interval = TimeSpan.FromSeconds(34) };
    private bool _kenBurnsFar;

    private void StepKenBurns()
    {
        _kenBurnsFar = !_kenBurnsFar;
        if (this.FindControl<TransitioningContentControl>("HeroArt") is { } art)
            art.Classes.Set("far", _kenBurnsFar);
    }

    private void UpdateMotion()
    {
        var live = IsVisible && WindowState != WindowState.Minimized;

        if (this.FindControl<Border>("Shell") is { } shell)
            shell.Classes.Set("live", live);

        if (live)
        {
            if (!_kenBurns.IsEnabled)
            {
                _kenBurns.Start();
                StepKenBurns();   // start moving now, not in 34 seconds
            }
        }
        else if (_kenBurns.IsEnabled)
        {
            _kenBurns.Stop();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == IsVisibleProperty)
            UpdateMotion();
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
