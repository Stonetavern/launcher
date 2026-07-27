using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// End-to-end proofs of the 1.14.2 start path against a REAL extracted client package, a REAL
/// HermesProxy and a REAL wine-ge runner. Everything else in this suite proves the launcher's logic
/// with doubles; this file is the part that answers "does it actually work", which no amount of green
/// unit tests establishes.
///
/// <para><b>Opt-in.</b> They start real processes and need a real client package, so they run only
/// when <c>MECHAGON_MODERN_IT</c> points at an extracted bundle:</para>
/// <code>
/// MECHAGON_MODERN_IT=/mnt/data/wow/beta-test-linux \
///   dotnet test --filter ModernClientIntegrationTests
/// </code>
/// <para>Without it they SKIP rather than pass - a green run on a machine with no client would be a
/// lie of exactly the kind this project has been bitten by before.</para>
///
/// <para><b>What they touch.</b> A throwaway wine prefix under TEMP, never the player's or the
/// launcher's real one. The proxy binds 127.0.0.1:1119 and is killed again. The client, if started, is
/// killed as soon as its process is observed - the proof is that the sequence brings the game process
/// up, not that anyone plays.</para>
/// </summary>
public sealed class ModernClientIntegrationTests
{
    private static string? Bundle => Environment.GetEnvironmentVariable("MECHAGON_MODERN_IT");
    private static bool Enabled => !string.IsNullOrWhiteSpace(Bundle) && Directory.Exists(Bundle);

    private static Serilog.ILogger Log =>
        new Serilog.LoggerConfiguration().MinimumLevel.Debug().CreateLogger();

    private static string ClientExe =>
        Path.Combine(Bundle!, "World of Warcraft", "_classic_era_", "WowClassic.exe");

    private static void KillClient()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pkill", "-f WowClassic\\.exe")
            { UseShellExecute = false });
            p?.WaitForExit(5000);
        }
        catch { /* best effort */ }
    }

    private static void KillProxy()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pkill", "-x HermesProxy")
            { UseShellExecute = false });
            p?.WaitForExit(5000);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// The proxy alone: it must start from its own directory and actually bind the port. This is where
    /// the CSV incident of 2026-07-21 lived - the proxy died at startup with a DirectoryNotFoundException
    /// because it could not find the game data beside it, and every layer above reported nothing.
    /// </summary>
    [SkippableFact]
    public async Task RealProxy_StartsAndActuallyBindsPort1119()
    {
        Skip.IfNot(Enabled, "MECHAGON_MODERN_IT is not set to an extracted client package");

        var proxyExe = Path.Combine(Bundle!, "Hermes", "linux", "HermesProxy");
        Assert.True(File.Exists(proxyExe), $"proxy binary missing: {proxyExe}");

        var pidFile = Path.Combine(Path.GetTempPath(), "modern-it-" + Guid.NewGuid().ToString("N") + ".pid");
        var runner = new HermesProxyRunner(Log, proxyExe, [], pidFile);
        try
        {
            var result = await runner.StartAndWaitForPortAsync(
                ModernClientLauncher.ProxyPort, TimeSpan.FromSeconds(45));

            Assert.True(result.Ready, $"proxy never bound the port: {result.Error}");
            Assert.NotNull(result.ProcessId);
        }
        finally
        {
            await runner.StopAsync();
            try { if (File.Exists(pidFile)) File.Delete(pidFile); } catch { }
        }
    }

    /// <summary>
    /// The full sequence through the real code path: real wine-ge, real prefix boot, real winepath,
    /// real Arctium, real proxy - and the assertion is the game process itself, not an exit code.
    /// </summary>
    [SkippableFact]
    public async Task RealLaunch_BringsUpTheClientProcess()
    {
        Skip.IfNot(Enabled, "MECHAGON_MODERN_IT is not set to an extracted client package");

        var wineGe = WineGeLocator.FindLatestForCurrentUser();
        Skip.If(wineGe is null, "no Lutris wine-ge runner installed on this machine");
        Assert.True(File.Exists(ClientExe), $"client missing: {ClientExe}");

        var shareDir = Path.Combine(Path.GetTempPath(), "modern-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(shareDir);

        var detector = new LinuxGameProcessDetector(Log);
        var wine = new WineGameLauncher(Log, WineOptions.ModernForShareDir(shareDir, wineGe!));
        var launcher = new ModernClientLauncher(
            Log, wine, detector, new XrandrDisplayResolution(Log),
            layout => new HermesProxyRunner(
                Log, layout.ProxyExe, [], Path.Combine(shareDir, "hermes-proxy.pid")),
            proxyTimeout: TimeSpan.FromSeconds(45),
            clientAppearTimeout: TimeSpan.FromSeconds(120));

        try
        {
            var result = await launcher.LaunchAsync(ClientExe, Path.GetDirectoryName(ClientExe)!);

            Assert.True(result.Started, $"launch failed: {result.Error}");
            Assert.True(detector.IsGameRunning(ClientExe), "launch reported success but no client process is running");

            // Optional survival window. "The process appeared" is not "the client works": a missing
            // D3D12 layer shows up as a client that starts and then dies, or sits there drawing
            // nothing, and both look identical to a one-second check. Set MECHAGON_MODERN_HOLD to a
            // number of seconds to hold the client and confirm it is STILL alive at the end - the same
            // reasoning GraceWindowLaunchExitPolicy uses, applied to the real thing.
            var holdSeconds = Environment.GetEnvironmentVariable("MECHAGON_MODERN_HOLD");
            if (int.TryParse(holdSeconds, out var seconds) && seconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                Assert.True(
                    detector.IsGameRunning(ClientExe),
                    $"the client came up but was gone again within {seconds}s - it started and died, " +
                    "which is what a missing or broken D3D12 layer looks like");
            }
        }
        finally
        {
            KillClient();
            // The launcher deliberately leaves the proxy running (it is reaped from the pidfile on the
            // next start), but a test uses a throwaway share dir and therefore a throwaway pidfile, so
            // nothing would ever reap this one. Left behind it holds port 1119 and makes the NEXT run
            // fail for a reason that has nothing to do with the code - which is exactly how the first
            // end-to-end run produced a green result it had not earned.
            KillProxy();
            try { Directory.Delete(shareDir, recursive: true); } catch { }
        }
    }
}
