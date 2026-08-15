using System.Linq;
using System.Threading.Tasks;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The routing rule the Linux split depends on. Wrong either way is a silent failure: a 64-bit client
/// started on plain system Wine has no D3D12 and never reaches the world, and a 32-bit client shoved
/// through Proton is needless risk on a path already proven working.
///
/// <para>The preference order itself is the owner decision of 2026-07-21: the modern client goes to
/// wine-ge when one is installed, and only falls back to umu/Proton when there is none. These tests
/// pin that order, because the code shipped the opposite order first and looked entirely correct.</para>
/// </summary>
public sealed class LinuxGameLauncherRouterTests
{
    private sealed class RecordingLauncher : IGameLauncher
    {
        public int CallCount;
        public Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory)
        {
            CallCount++;
            return Task.FromResult(GameLaunchResult.Ok(1));
        }
    }

    private static Serilog.ILogger Logger() => new Serilog.LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task ModernClient_PrefersWineGe_OverProton()
    {
        var legacy = new RecordingLauncher();
        var wineGe = new RecordingLauncher();
        var proton = new RecordingLauncher();
        var router = new LinuxGameLauncherRouter(legacy, wineGe, Logger());

        await router.LaunchAsync("/some/dir/WowClassic.exe", "/some/dir");

        Assert.Equal(1, wineGe.CallCount);
        Assert.Equal(0, proton.CallCount);
        Assert.Equal(0, legacy.CallCount);
    }

    /// <summary>
    /// This test used to pin the opposite: without wine-ge, fall back to umu/Proton. That fallback
    /// starts the client exe on its own, and the modern client only reaches this realm THROUGH the
    /// local proxy the wine-ge path owns. So the player got a window, a login screen, and a realm that
    /// was never contacted — a launch that succeeds and arrives nowhere, with nothing red anywhere.
    /// A real player hit exactly this (Discord 2026-07-26): the launcher "failed" for them while the
    /// shipped shell script started the same client fine, because the script brought the proxy.
    ///
    /// <para><b>Narrowed 2026-08-03.</b> The refusal used to cover "no wine-ge", which was too wide:
    /// measured on Fedora with the plain system wine 11.0, the whole chain works (proxy on 1119, Arctium
    /// patches, the client comes up) — and the bundle's own script had been falling back to it all along.
    /// The refusal now means what it says: no Wine at all. What is refused is a launch with no proxy,
    /// never a launch without Lutris.</para>
    /// </summary>
    [Fact]
    public async Task ModernClient_WithNoWineAtAll_IsRefused_NotStartedWithoutItsProxy()
    {
        var legacy = new RecordingLauncher();
        var router = new LinuxGameLauncherRouter(legacy, modernWine: null, Logger());

        var result = await router.LaunchAsync("/some/dir/WowClassic.exe", "/some/dir");

        Assert.False(result.Started);
        Assert.Equal(0, legacy.CallCount);
        // The message has to name the way out, or "not supported" leaves the player with nothing to do.
        // Both ways out, now that either one works.
        Assert.Contains("wine", result.Error, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Lutris", result.Error, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The legacy 1.12 client is untouched by all of this: it needs no proxy, it is the
    /// proven path, and it must keep starting on a machine with no wine-ge at all.</summary>
    [Fact]
    public async Task LegacyClient_StillStarts_WhenThereIsNoWineGeAtAll()
    {
        var legacy = new RecordingLauncher();
        var router = new LinuxGameLauncherRouter(legacy, modernWine: null, Logger());

        var result = await router.LaunchAsync("/some/dir/WoW.exe", "/some/dir");

        Assert.True(result.Started);
        Assert.Equal(1, legacy.CallCount);
    }

    [Fact]
    public async Task LegacyClient_RoutesToSystemWine_EvenWhenWineGeExists()
    {
        var legacy = new RecordingLauncher();
        var wineGe = new RecordingLauncher();
        var proton = new RecordingLauncher();
        var router = new LinuxGameLauncherRouter(legacy, wineGe, Logger());

        await router.LaunchAsync("/some/dir/WoW.exe", "/some/dir");

        Assert.Equal(1, legacy.CallCount);
        Assert.Equal(0, wineGe.CallCount);
        Assert.Equal(0, proton.CallCount);
    }

    [Theory]
    [InlineData("WowClassic.exe", true)]
    [InlineData("WOWCLASSIC.EXE", true)]
    [InlineData("WoW.exe", false)]
    [InlineData("SomethingElse.exe", false)]
    public void NeedsModernRuntime_MatchesOnlyTheModernBuildsExe(string exeName, bool expected) =>
        Assert.Equal(expected, LinuxGameLauncherRouter.NeedsModernRuntime(exeName));

    /// <summary>The routing question must be answered by the ClientVersion flag, not by a build number
    /// repeated in the router. If someone flips NeedsModernRuntime on a build, routing has to follow
    /// without a second edit.</summary>
    [Fact]
    public void ModernRuntimeFlag_IsSetOnExactlyTheClassicEraBuild()
    {
        var modern = WowLauncher.Models.ClientVersion.All.Where(c => c.NeedsModernRuntime).ToList();
        Assert.Single(modern);
        Assert.Equal(42597, modern[0].Build);
        Assert.Equal("WowClassic.exe", modern[0].ExeName);
    }
}
