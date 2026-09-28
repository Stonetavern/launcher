using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace WowLauncher.Controls;

/// <summary>
/// The ring around the lantern while the launcher gets ready: a hairline arc that grows clockwise
/// from twelve o'clock as the start checks finish, with a small ember at its leading end. It replaces
/// the dashed ring that turned forever (a placeholder, owner 2026-09-28: "the loading animation is not
/// that cool"). A ring that fills says how far along the start is; a ring that spins says nothing.
///
/// <para><see cref="Value"/> is 0 to 1 and is animated by a style transition (Motion tokens), so this
/// control only draws. Pure render: no timers, no layout work per frame.</para>
/// </summary>
public sealed class KindleRing : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<KindleRing, double>(nameof(Value));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<KindleRing, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<KindleRing, double>(nameof(StrokeThickness), 1.5);

    /// <summary>The full circle under the arc (the "track"). Drawn by this control so track and arc
    /// share one radius by construction; two separate shapes drift apart by half a stroke.</summary>
    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<KindleRing, IBrush?>(nameof(Track));

    public static readonly StyledProperty<double> TrackOpacityProperty =
        AvaloniaProperty.Register<KindleRing, double>(nameof(TrackOpacity), 1.0);

    /// <summary>The ember at the arc's head. Null draws no ember.</summary>
    public static readonly StyledProperty<IBrush?> SparkProperty =
        AvaloniaProperty.Register<KindleRing, IBrush?>(nameof(Spark));

    static KindleRing()
    {
        AffectsRender<KindleRing>(ValueProperty, StrokeProperty, StrokeThicknessProperty, SparkProperty,
                                  TrackProperty, TrackOpacityProperty);
    }

    /// <summary>How much of the ring is lit, 0 to 1. Values outside are clamped.</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public double TrackOpacity
    {
        get => GetValue(TrackOpacityProperty);
        set => SetValue(TrackOpacityProperty, value);
    }

    public IBrush? Spark
    {
        get => GetValue(SparkProperty);
        set => SetValue(SparkProperty, value);
    }

    /// <summary>The point on a circle of <paramref name="radius"/> around <paramref name="centre"/> at
    /// <paramref name="fraction"/> of a full turn, clockwise from twelve o'clock (screen y points
    /// down). Internal for the geometry test.</summary>
    internal static Point PointAt(Point centre, double radius, double fraction)
    {
        var angle = (fraction * 2 * Math.PI) - (Math.PI / 2);
        return new Point(centre.X + (radius * Math.Cos(angle)), centre.Y + (radius * Math.Sin(angle)));
    }

    /// <summary>The ring's radius inside a box of the given size: the ember is wider than the line,
    /// and both stay inside the bounds.</summary>
    internal static double RadiusFor(Size size, double thickness) =>
        (Math.Min(size.Width, size.Height) / 2) - SparkRadius(thickness);

    /// <summary>The ember's radius: a soft glow a little over three line widths across.</summary>
    internal static double SparkRadius(double thickness) => thickness * 3.4;

    public override void Render(DrawingContext context)
    {
        var thickness = StrokeThickness;
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = RadiusFor(Bounds.Size, thickness);
        if (radius <= 0) return;

        if (Track is { } track)
        {
            using (context.PushOpacity(TrackOpacity))
                context.DrawEllipse(null, new Pen(track, 1), centre, radius, radius);
        }

        var value = Math.Clamp(Value, 0, 1);
        if (value <= 0 || Stroke is null) return;

        var pen = new Pen(Stroke, thickness, lineCap: PenLineCap.Round);

        if (value >= 0.9995)
        {
            context.DrawEllipse(null, pen, centre, radius, radius);
            return;
        }

        var start = PointAt(centre, radius, 0);
        var end = PointAt(centre, radius, value);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(start, isFilled: false);
            g.ArcTo(end, new Size(radius, radius), rotationAngle: 0,
                    isLargeArc: value > 0.5, sweepDirection: SweepDirection.Clockwise);
            g.EndFigure(isClosed: false);
        }
        context.DrawGeometry(null, pen, geometry);

        if (Spark is { } spark)
        {
            var r = SparkRadius(thickness);
            context.DrawEllipse(spark, null, end, r, r);
        }
    }
}
