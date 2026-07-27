using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace WowLauncher.Controls;

/// <summary>
/// The signature of the v2 skin: the player count is never printed as a badge — it is drawn as light.
/// One tick per player online, uneven heights, each breathing on its own phase. Free capacity is drawn
/// as dark notches on the same line, so occupancy reads at a glance.
///
/// When the realm goes down every tick goes dark. That *is* the outage notification; no dialog needed.
/// </summary>
public sealed class PulseRail : Control
{
    public static readonly StyledProperty<int> OnlineProperty =
        AvaloniaProperty.Register<PulseRail, int>(nameof(Online));

    public static readonly StyledProperty<int> CapacityProperty =
        AvaloniaProperty.Register<PulseRail, int>(nameof(Capacity), 120);

    /// <summary>Players currently in the world. Ticks are lit up to this count.</summary>
    public int Online
    {
        get => GetValue(OnlineProperty);
        set => SetValue(OnlineProperty, value);
    }

    /// <summary>Realm slots. The rest of the rail stays as dark notches.</summary>
    public int Capacity
    {
        get => GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    private static readonly SolidColorBrush Cyan = new(Color.FromRgb(0x78, 0xD2, 0xE1));
    private static readonly SolidColorBrush Notch = new(Color.FromRgb(0x3A, 0x35, 0x32));
    private static readonly SolidColorBrush Hair = new(Color.FromRgb(0x30, 0x2D, 0x2A));

    private readonly record struct Tick(double X, double H, double Phase, double Speed);

    private readonly List<Tick> _ticks = new();
    private Size _built;
    private DispatcherTimer? _timer;
    private double _clock;

    static PulseRail()
    {
        AffectsRender<PulseRail>(OnlineProperty, CapacityProperty);
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) =>
        {
            _clock += 0.06;
            InvalidateVisual();
        });
        _timer.Start();
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        base.OnUnloaded(e);
    }

    private static Func<double> Rng(int seed)
    {
        var s = seed;
        return () =>
        {
            s = unchecked(s * 1103515245 + 12345) & 0x7fffffff;
            return s / (double)0x7fffffff;
        };
    }

    private void Build(Size size)
    {
        _ticks.Clear();
        var cap = Math.Max(1, Capacity);
        var rnd = Rng(97);
        var w = size.Width;

        for (var i = 0; i < cap; i++)
        {
            var baseX = i / (double)cap * (w - 2);
            var jitter = (rnd() - 0.5) * (w / cap) * 0.9;
            var x = Math.Clamp(baseX + jitter, 0, w - 2);
            _ticks.Add(new Tick(x, 6 + rnd() * 9, rnd() * 6.28, 0.6 + rnd() * 0.8));
        }

        _built = size;
    }

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        if (size.Width < 4 || size.Height < 4) return;
        if (size != _built || _ticks.Count != Math.Max(1, Capacity)) Build(size);

        var baseline = size.Height - 7;
        ctx.FillRectangle(Hair, new Rect(0, baseline, size.Width, 1));

        var online = Math.Clamp(Online, 0, _ticks.Count);

        for (var i = 0; i < _ticks.Count; i++)
        {
            var t = _ticks[i];

            if (i >= online)
            {
                // a free slot: a dark notch, so the rail always shows how full the world is
                ctx.FillRectangle(Notch, new Rect(t.X, baseline - 3, 2, 3));
                continue;
            }

            var breath = 0.42 + 0.58 * (0.5 + 0.5 * Math.Sin(_clock * t.Speed + t.Phase));
            var brush = new SolidColorBrush(Cyan.Color, breath);
            ctx.FillRectangle(brush, new Rect(t.X, baseline - t.H, 2, t.H));
        }
    }
}
