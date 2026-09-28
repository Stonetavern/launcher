using WowLauncher.Startup;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>A second start asked a launcher whose main window was closed to come forward, and
/// <c>Show()</c> on a closed window crashed it (E2E 2026-09-28).</summary>
public sealed class BringForwardTests
{
    private sealed class W { }

    [Fact]
    public void AnOpenMainWindow_IsTheOneShown()
    {
        var main = new W();
        Assert.Same(main, BringForward.Pick(main, new[] { new W(), main }));
    }

    [Fact]
    public void AClosedMainWindow_IsNeverShown_TheLatestOpenOneIs()
    {
        var closed = new W();
        var latest = new W();
        Assert.Same(latest, BringForward.Pick(closed, new[] { new W(), latest }));
    }

    [Fact]
    public void NothingOpen_MeansNothingToShow()
    {
        Assert.Null(BringForward.Pick(new W(), System.Array.Empty<W>()));
        Assert.Null(BringForward.Pick<W>(null, System.Array.Empty<W>()));
    }
}
