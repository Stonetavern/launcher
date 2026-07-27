using System;
using System.Threading.Tasks;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Guards the WP2 grace gate (PLAN §4): a successful <c>wine</c> start is not proof the client runs.
/// The Linux policy proves <em>stability</em> — the client must still be alive at the END of the grace
/// window — so it returns <c>false</c> when no client ever appears AND when one appears then dies inside
/// the window (→ launcher shows a failure instead of vanishing), and <c>true</c> only when the client is
/// still up at the window's end. "Never running" and "appears then dies" are the placebo targets — with
/// the guard flipped to early-return on the first sighting, "appears then dies" must go red.
/// </summary>
public sealed class GraceExitPolicyTests
{
    private static Serilog.ILogger Log => new Serilog.LoggerConfiguration().CreateLogger();

    /// <summary>Detector that reports "running" only from the Nth probe onward (then stays running).</summary>
    private sealed class CountingDetector(int runningFrom) : IGameProcessDetector
    {
        private readonly int _runningFrom = runningFrom;
        public int Calls { get; private set; }

        public bool IsGameRunning(string? expectedExePath)
        {
            Calls++;
            return Calls >= _runningFrom;
        }
    }

    /// <summary>Detector that reports "running" for the first N probes, then "dead" forever — models a
    /// wine start that appears and then dies inside the grace window (crash on load / missing DLL).</summary>
    private sealed class DiesAfterDetector(int aliveForFirst) : IGameProcessDetector
    {
        private readonly int _aliveForFirst = aliveForFirst;
        public int Calls { get; private set; }

        public bool IsGameRunning(string? expectedExePath)
        {
            Calls++;
            return Calls <= _aliveForFirst;
        }
    }

    [Fact]
    public async Task Windows_ImmediatePolicy_ExitsWithoutInspectingProcesses()
    {
        // ImmediateLaunchExitPolicy must confirm synchronously and unconditionally — the shipped
        // Windows behaviour (exit right after start, no process scan).
        var ok = await new ImmediateLaunchExitPolicy().ConfirmClientRunningAsync("/irrelevant/WoW.exe");
        Assert.True(ok);
    }

    [Fact]
    public async Task Grace_ClientNeverAppears_ReturnsFalse()
    {
        // A wine start that produces no live client → false, so the launcher reports a failure.
        var detector = new CountingDetector(runningFrom: int.MaxValue); // never running
        var policy = new GraceWindowLaunchExitPolicy(
            detector, Log, window: TimeSpan.FromMilliseconds(150), interval: TimeSpan.FromMilliseconds(20));

        var ok = await policy.ConfirmClientRunningAsync("/opt/wow/WoW.exe");

        Assert.False(ok);
        Assert.True(detector.Calls >= 2, "the policy must keep polling across the grace window, not check once");
    }

    [Fact]
    public async Task Grace_ClientAppearsThenDiesInWindow_ReturnsFalse()
    {
        // The core WP2 fix: a client that is seen early but dies before the window ends must NOT let the
        // launcher exit. Alive for the first 2 probes, then dead → the final authoritative scan is
        // negative → false. (Placebo: with an early-return-on-first-sighting guard this returns true.)
        var detector = new DiesAfterDetector(aliveForFirst: 2);
        var policy = new GraceWindowLaunchExitPolicy(
            detector, Log, window: TimeSpan.FromMilliseconds(200), interval: TimeSpan.FromMilliseconds(20));

        var ok = await policy.ConfirmClientRunningAsync("/opt/wow/WoW.exe");

        Assert.False(ok);
        Assert.True(detector.Calls >= 3,
            "the policy must keep scanning past the first sighting so it can observe the client die");
    }

    [Fact]
    public async Task Grace_ClientStaysAlive_ReturnsTrue()
    {
        // Client is up from the first probe and stays up → alive at the end of the window → true.
        var detector = new CountingDetector(runningFrom: 1); // running on every probe
        var policy = new GraceWindowLaunchExitPolicy(
            detector, Log, window: TimeSpan.FromMilliseconds(120), interval: TimeSpan.FromMilliseconds(20));

        var ok = await policy.ConfirmClientRunningAsync("/opt/wow/WoW.exe");

        Assert.True(ok);
    }

    [Fact]
    public async Task Grace_ClientAppearsAfterDelayAndStays_ReturnsTrue()
    {
        // Models wine's fork lag: the client shows up on the 3rd probe and then stays up → still alive
        // at the window's end → true.
        var detector = new CountingDetector(runningFrom: 3);
        var policy = new GraceWindowLaunchExitPolicy(
            detector, Log, window: TimeSpan.FromMilliseconds(200), interval: TimeSpan.FromMilliseconds(20));

        var ok = await policy.ConfirmClientRunningAsync("/opt/wow/WoW.exe");

        Assert.True(ok);
    }
}
