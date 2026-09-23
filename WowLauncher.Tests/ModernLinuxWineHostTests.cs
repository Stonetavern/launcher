using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The Linux runner order for the modern (1.14.2) client's Wine-side steps: GE-Proton first (KONZEPT
/// §13), otherwise EXACTLY today's wine-ge/system-Wine choice (untouched). These tests never construct
/// a real <see cref="ProtonGameLauncher"/> against a real GE-Proton install - only the ROUTING is
/// tested here, the same split <see cref="ProtonGameLauncherTests"/> uses for the pure pieces
/// underneath it.
/// </summary>
public sealed class ModernLinuxWineHostTests
{
    private sealed class FakeWineHost : IWineHost
    {
        public int RunCount;
        public int WinePathCount;

        public Task<GameLaunchResult> RunAsync(string exePath, string workingDirectory, IReadOnlyList<string> args)
        {
            RunCount++;
            return Task.FromResult(GameLaunchResult.Ok(42));
        }

        public Task<string?> ToWindowsPathAsync(string unixPath)
        {
            WinePathCount++;
            return Task.FromResult<string?>(@"Z:\fake");
        }
    }

    private static Serilog.ILogger Logger() => new Serilog.LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task NoGeProtonInstalled_UsesTheFallbackExactlyLikeBeforeThisChange()
    {
        var fallback = new FakeWineHost();
        var fallbackConstructions = 0;
        var host = new ModernLinuxWineHost(
            Logger(),
            geProton: () => null,
            steamCompatClientInstallPath: () => "/home/player/.local/share/Steam",
            fallback: () => { fallbackConstructions++; return fallback; });

        var result = await host.RunAsync("/bundle/Launcher/Arctium.exe", "/bundle/Launcher", []);
        var path = await host.ToWindowsPathAsync("/bundle/World of Warcraft/_classic_era_");

        Assert.True(result.Started);
        Assert.Equal(42, result.ProcessId);
        Assert.Equal(@"Z:\fake", path);
        Assert.Equal(1, fallback.RunCount);
        Assert.Equal(1, fallback.WinePathCount);
        // The fallback factory ran, meaning the composite genuinely delegated rather than
        // short-circuiting some other way - this is the "same launcher as before" proof.
        Assert.True(fallbackConstructions >= 1);
    }

    [Fact]
    public async Task GeProtonInstalled_RoutesIntoProtonInsteadOfTheFallback()
    {
        var fallback = new FakeWineHost();
        var host = new ModernLinuxWineHost(
            Logger(),
            // A path that does not exist on disk - ProtonGameLauncher.IsProtonReady() will refuse it,
            // which is fine here: the point of this test is that the FALLBACK was never touched, not
            // that a fabricated Proton binary actually starts a game.
            geProton: () => "/does/not/exist/proton",
            steamCompatClientInstallPath: () => "/home/player/.local/share/Steam",
            fallback: () => fallback);

        var exe = Path.Combine(Path.GetTempPath(), "fake-arctium-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(exe, "MZ");
        try
        {
            var result = await host.RunAsync(exe, Path.GetDirectoryName(exe)!, []);

            Assert.False(result.Started);
            Assert.Contains("GE-Proton", result.Error);
            Assert.Equal(0, fallback.RunCount); // never delegated to the fallback
        }
        finally
        {
            try { File.Delete(exe); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task GeProtonInstalled_AlsoRoutesToWindowsPathAsync_AwayFromTheFallback()
    {
        var fallback = new FakeWineHost();
        var host = new ModernLinuxWineHost(
            Logger(),
            geProton: () => "/does/not/exist/proton",
            steamCompatClientInstallPath: () => "/home/player/.local/share/Steam",
            fallback: () => fallback);

        // ProtonGameLauncher.ToWindowsPathAsync falls back to its own Z: drive mapping when the
        // (fabricated, non-executable) proton path is not usable - it still must not touch the
        // wine-ge/system-Wine fallback.
        var path = await host.ToWindowsPathAsync("/bundle/World of Warcraft/_classic_era_");

        Assert.Equal(@"Z:\bundle\World of Warcraft\_classic_era_", path);
        Assert.Equal(0, fallback.WinePathCount);
    }
}
