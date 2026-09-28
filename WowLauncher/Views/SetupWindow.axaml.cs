using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace WowLauncher.Views;

/// <summary>The first-start setup page (see the AXAML header). View-only: the frame, the drag strip,
/// the motion gate, and "first frame on screen" so the boot frame can close under it.</summary>
public partial class SetupWindow : Window, ISigilFrame
{
    private bool _firstFrameSeen;

    public SetupWindow()
    {
        AvaloniaXamlLoader.Load(this);
        if (OperatingSystem.IsLinux())
            WindowDecorations = WindowDecorations.None;

        Opened += (_, _) => RequestAnimationFrame(_ =>
        {
            if (_firstFrameSeen) return;
            _firstFrameSeen = true;
            FirstFrameRendered?.Invoke();
        });
    }

    public Avalonia.Point? SigilCentre()
    {
        if (this.FindControl<Panel>("Sigil") is not { } sigil || sigil.Bounds.Width <= 0) return null;
        return sigil.TranslatePoint(new Avalonia.Point(sigil.Bounds.Width / 2, sigil.Bounds.Height / 2), this);
    }

    /// <summary>Raised once, when the page is really on screen.</summary>
    public event Action? FirstFrameRendered;

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
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
