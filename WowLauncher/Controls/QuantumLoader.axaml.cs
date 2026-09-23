using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace WowLauncher.Controls;

/// <summary>
/// The area loader for every wait of unknown duration (see the markup header). Two knobs:
/// <see cref="Scale"/> shrinks the 176x96 figure for tight places (the status line uses 0.5), and
/// <see cref="ReducedMotion"/> freezes it into four resting dots.
///
/// <para><b>Budget (Spec §9).</b> The orbits run only while the owning window is active and not
/// minimised. The control watches its own window, so every host gets the gate for free: dropping
/// the "running" class unmatches the four endless animations in one move, exactly like the
/// "Border.live" gate of the shells.</para>
/// </summary>
public partial class QuantumLoader : UserControl
{
    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<QuantumLoader, double>(nameof(Scale), 1.0);

    public static readonly StyledProperty<bool> ReducedMotionProperty =
        AvaloniaProperty.Register<QuantumLoader, bool>(nameof(ReducedMotion));

    /// <summary>Native stage size at Scale 1.</summary>
    public const double StageWidth = 176;
    public const double StageHeight = 96;

    private Window? _window;

    public QuantumLoader()
    {
        AvaloniaXamlLoader.Load(this);
        ApplyScale();
        UpdateRunning();
    }

    /// <summary>Size factor of the whole figure. 1.0 = 176x96 (Phase 2 area variant), 0.5 = 88x48
    /// (status line). The control's layout size follows, so neighbours never overlap it.</summary>
    public double Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    /// <summary>True: no orbits, four resting dots (Avalonia has no prefers-reduced-motion; this is
    /// the settings switch). The figure still reads as "waiting", it just does not move.</summary>
    public bool ReducedMotion
    {
        get => GetValue(ReducedMotionProperty);
        set => SetValue(ReducedMotionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ScaleProperty) ApplyScale();
        else if (change.Property == ReducedMotionProperty || change.Property == IsVisibleProperty) UpdateRunning();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
        {
            _window.Activated += OnWindowChanged;
            _window.Deactivated += OnWindowChanged;
            _window.PropertyChanged += OnWindowPropertyChanged;
        }
        UpdateRunning();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null)
        {
            _window.Activated -= OnWindowChanged;
            _window.Deactivated -= OnWindowChanged;
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowChanged(object? sender, EventArgs e) => UpdateRunning();

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == Visual.IsVisibleProperty)
            UpdateRunning();
    }

    /// <summary>Running = visible, not reduced, window active and not minimised. No window (a
    /// headless host) counts as active, so a QA still shows the figure at its resting positions
    /// only when the harness has not activated the window; either way it is never blank.</summary>
    private void UpdateRunning()
    {
        var windowOk = _window is null
            || (_window.IsActive && _window.IsVisible && _window.WindowState != WindowState.Minimized);
        var running = IsVisible && !ReducedMotion && windowOk;
        Classes.Set("running", running);
    }

    private void ApplyScale()
    {
        var s = Math.Clamp(Scale, 0.1, 4.0);
        var stagePanel = this.FindControl<Panel>("Stage");
        if (stagePanel?.RenderTransform is ScaleTransform st) { st.ScaleX = s; st.ScaleY = s; }
        // The stage keeps its 176x96 layout box and is scaled around its centre; the control's own
        // box shrinks with it so the layout around it stays honest.
        Width = StageWidth * s;
        Height = StageHeight * s;
        if (this.FindControl<Panel>("Stage") is { } stage)
            stage.Margin = new Thickness((Width - StageWidth) / 2, (Height - StageHeight) / 2);
    }
}
