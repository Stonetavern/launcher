using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using WowLauncher.Startup;

namespace WowLauncher.Views;

/// <summary>A window that draws the lantern where the login shell will, so the shell can take over
/// from it without a jump (the boot frame, the setup page).</summary>
public interface ISigilFrame
{
    /// <summary>The lantern centre in window coordinates, or null before layout.</summary>
    Point? SigilCentre();
}

/// <summary>
/// The boot frame: on screen before anything expensive exists (see the AXAML header). View-only:
/// the motion gate, the first-frame signal <see cref="App"/> continues the start from, and the
/// position of the lantern the login shell glides from.
/// </summary>
public partial class BootWindow : Window, ISigilFrame
{
    private bool _firstFrameSeen;

    public BootWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Same as every shell: KWin draws a bright active frame at BorderOnly; macOS keeps its frame.
        if (OperatingSystem.IsLinux())
            WindowDecorations = WindowDecorations.None;

        Opened += (_, _) => RequestAnimationFrame(_ =>
        {
            if (_firstFrameSeen) return;
            _firstFrameSeen = true;
            StartupClock.Mark("boot-frame");
            Serilog.Log.Information("Boot frame on screen at {Ms:F0} ms after process start",
                StartupClock.Elapsed.TotalMilliseconds);
            FirstFrameRendered?.Invoke();
        });
    }

    /// <summary>Raised once, when the boot frame is really on screen. The start continues from here,
    /// so the expensive work never delays the first picture.</summary>
    public event Action? FirstFrameRendered;

    /// <summary>True once the first frame was drawn.</summary>
    public bool HasRendered => _firstFrameSeen;

    /// <summary>The lantern's centre in window coordinates, or null before layout. The login shell
    /// starts its own lantern here and glides it into place.</summary>
    public Point? SigilCentre()
    {
        if (this.FindControl<Panel>("Sigil") is not { } sigil || sigil.Bounds.Width <= 0) return null;
        return sigil.TranslatePoint(new Point(sigil.Bounds.Width / 2, sigil.Bounds.Height / 2), this);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == IsVisibleProperty)
        {
            var live = IsVisible && WindowState != WindowState.Minimized;
            if (this.FindControl<Border>("Root") is { } root) root.Classes.Set("live", live);
        }
    }

    private void OnDragStripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}
