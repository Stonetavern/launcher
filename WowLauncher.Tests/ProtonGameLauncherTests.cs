using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// GE-Proton as the modern client's Wine-side runner (KONZEPT §13, owner measurement 2026-09-19,
/// Ledger run U). Only the PURE pieces are unit-tested — the two path-derivation functions and the
/// Z: drive fallback — the same reasoning <see cref="UmuGameLauncher"/>'s own tests use for not
/// spawning a real umu-run: a real GE-Proton launch is an E2E concern, not a unit one.
/// </summary>
public sealed class ProtonGameLauncherTests
{
    [Fact]
    public void BundleRootFromWorkingDir_IsOneLevelAboveTheArctiumDirectory()
    {
        // ModernClientLauncher always calls RunAsync with layout.ArctiumDir (<root>/Launcher) as the
        // working directory - the bundle root is its parent.
        var arctiumDir = Path.Combine("/home/player/Stonetavern", "Launcher");

        var root = ProtonGameLauncher.BundleRootFromWorkingDir(arctiumDir);

        Assert.Equal(Path.GetFullPath("/home/player/Stonetavern"), root);
    }

    [Fact]
    public void BundleRootFromClientDir_IsTwoLevelsAboveTheClassicEraDirectory()
    {
        // ModernClientLauncher always calls ToWindowsPathAsync with layout.ClientDir
        // (<root>/World of Warcraft/_classic_era_) - the bundle root is two levels up, the same
        // relationship ModernClientLayout.Resolve itself uses.
        var clientDir = Path.Combine("/home/player/Stonetavern", "World of Warcraft", "_classic_era_");

        var root = ProtonGameLauncher.BundleRootFromClientDir(clientDir);

        Assert.Equal(Path.GetFullPath("/home/player/Stonetavern"), root);
    }

    [Theory]
    [InlineData("/home/player/Stonetavern/World of Warcraft", @"Z:\home\player\Stonetavern\World of Warcraft")]
    [InlineData("/a/b", @"Z:\a\b")]
    public void ZDriveFallback_MirrorsTheMeasuredScriptsSedMapping(string unixPath, string expected)
    {
        // Play Stonetavern (JimsProxy-Beta).sh line 121:
        // WOWPATH="Z:$(printf '%s' "$WOWDIR" | sed 's|/|\\|g')"
        Assert.Equal(expected, ProtonGameLauncher.ZDriveFallback(unixPath));
    }

    [Fact]
    public async Task RunAsync_RefusesWithAnActionableMessage_WhenTheProtonScriptDoesNotExist()
    {
        var launcher = new ProtonGameLauncher(
            new Serilog.LoggerConfiguration().CreateLogger(),
            protonExePath: "/does/not/exist/proton",
            steamCompatClientInstallPath: "/home/player/.local/share/Steam");
        var exe = Path.Combine(Path.GetTempPath(), "fake-arctium-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(exe, "MZ");
        try
        {
            var result = await launcher.RunAsync(exe, Path.GetDirectoryName(exe)!, []);

            Assert.False(result.Started);
            Assert.Contains("GE-Proton", result.Error);
            Assert.Contains("~/.local/share/Steam/compatibilitytools.d/", result.Error);
            // VOICE.md: no em dashes, no apostrophes in player-facing text.
            Assert.DoesNotContain("—", result.Error);
            Assert.DoesNotContain("'", result.Error);
        }
        finally
        {
            try { File.Delete(exe); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task RunAsync_RefusesWhenTheClientExeItselfIsMissing()
    {
        var launcher = new ProtonGameLauncher(
            new Serilog.LoggerConfiguration().CreateLogger(),
            protonExePath: "/does/not/exist/proton",
            steamCompatClientInstallPath: "/home/player/.local/share/Steam");

        var result = await launcher.RunAsync("/does/not/exist/Arctium.exe", "/does/not/exist", []);

        Assert.False(result.Started);
        Assert.Contains("Client executable not found", result.Error);
    }

    [Fact]
    public async Task LaunchAsync_DelegatesToRunAsyncWithNoArguments_SameShapeAsWineGameLauncher()
    {
        var launcher = new ProtonGameLauncher(
            new Serilog.LoggerConfiguration().CreateLogger(),
            protonExePath: "/does/not/exist/proton",
            steamCompatClientInstallPath: "/home/player/.local/share/Steam");

        var viaLaunch = await launcher.LaunchAsync("/does/not/exist/Arctium.exe", "/does/not/exist");
        var viaRun = await launcher.RunAsync("/does/not/exist/Arctium.exe", "/does/not/exist", []);

        Assert.Equal(viaRun.Started, viaLaunch.Started);
        Assert.Equal(viaRun.Error, viaLaunch.Error);
    }
}
