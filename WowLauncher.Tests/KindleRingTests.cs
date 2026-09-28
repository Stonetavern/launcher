using Avalonia;
using WowLauncher.Controls;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The ring around the lantern fills clockwise from twelve o'clock (owner 2026-09-28: the spinning
/// ring said nothing). The geometry is pure math, so it is checked here without a render: where the
/// arc starts and runs, and that the ember at its head never leaves the control's bounds.
/// </summary>
public sealed class KindleRingTests
{
    private static readonly Point Centre = new(52, 52);
    private const double Radius = 40;

    [Theory]
    [InlineData(0.00, 52, 12)]   // twelve o'clock: straight up (screen y grows downwards)
    [InlineData(0.25, 92, 52)]   // three o'clock: right, so the ring runs clockwise
    [InlineData(0.50, 52, 92)]   // six o'clock
    [InlineData(0.75, 12, 52)]   // nine o'clock
    [InlineData(1.00, 52, 12)]   // a full turn is back at the top
    public void PointAt_StartsAtTwelve_AndRunsClockwise(double fraction, double x, double y)
    {
        var p = KindleRing.PointAt(Centre, Radius, fraction);

        Assert.Equal(x, p.X, 6);
        Assert.Equal(y, p.Y, 6);
    }

    [Theory]
    [InlineData(104, 104, 1.5)]   // the shipped size in both the boot frame and the login shell
    [InlineData(104, 80, 1.5)]    // a non-square box takes the smaller side
    [InlineData(40, 40, 3.0)]
    public void TheEmber_StaysInsideTheBounds_AtEveryAngle(double w, double h, double thickness)
    {
        var size = new Size(w, h);
        var centre = new Point(w / 2, h / 2);
        var radius = KindleRing.RadiusFor(size, thickness);
        var spark = KindleRing.SparkRadius(thickness);

        Assert.True(radius > 0);
        for (var f = 0.0; f <= 1.0; f += 1.0 / 64)
        {
            var p = KindleRing.PointAt(centre, radius, f);
            Assert.InRange(p.X - spark, -1e-9, w + 1e-9);
            Assert.InRange(p.X + spark, -1e-9, w + 1e-9);
            Assert.InRange(p.Y - spark, -1e-9, h + 1e-9);
            Assert.InRange(p.Y + spark, -1e-9, h + 1e-9);
        }
    }

    [Fact]
    public void TheShippedRing_HasItsRadiusAt46Point9()
    {
        // 104 / 2 - 1.5 * 3.4: the track and the arc share this one radius by construction.
        Assert.Equal(46.9, KindleRing.RadiusFor(new Size(104, 104), 1.5), 9);
    }
}
