using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The tail of a play session: hide-to-tray leaves the launcher alive, so it now watches the game end
/// and reaps the 1.14.2 realm proxy itself. The one rule that must never break: the proxy is stopped
/// ONLY once the client process is proven gone — never while someone is still playing.
/// </summary>
public sealed class GameSessionTests
{
    private static Serilog.ILogger Log() => new Serilog.LoggerConfiguration().CreateLogger();

    /// <summary>A detector scripted with a sequence of answers; the last value repeats once exhausted.
    /// Models the real timeline: the client appears, stays up for a while, then is gone.</summary>
    private sealed class ScriptedDetector(params bool[] answers) : IGameProcessDetector
    {
        private readonly IReadOnlyList<bool> _answers = answers;
        private int _i;
        public int Calls { get; private set; }

        public bool IsGameRunning(string? expectedExePath)
        {
            Calls++;
            var v = _answers[Math.Min(_i, _answers.Count - 1)];
            if (_i < _answers.Count - 1) _i++;
            return v;
        }
    }

    private sealed class FakeProxy : IGameProxy
    {
        public int StopCount;
        public Task<GameProxyResult> StartAndWaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult(GameProxyResult.Ok(1));
        public Task StopAsync() { Interlocked.Increment(ref StopCount); return Task.CompletedTask; }
    }

    private static GameSession NewSession(IGameProcessDetector detector) =>
        // Tiny windows + a real short delay: the polling logic runs against wall-clock deadlines (as in
        // production) but the test does not sit for whole seconds.
        new(detector, Log(),
            appearTimeout: TimeSpan.FromMilliseconds(200),
            pollInterval: TimeSpan.FromMilliseconds(5),
            delay: (d, ct) => Task.Delay(d, ct));

    // ── The invariant: never stop the proxy while a client is running ──────────────────────────────

    [Fact]
    public async Task StopProxyIfNoGame_LeavesTheProxyRunning_WhileAClientIsStillUp()
    {
        var proxy = new FakeProxy();
        var session = NewSession(new ScriptedDetector(true)); // a client is running
        session.AttachProxy(proxy);

        var stopped = await session.StopProxyIfNoGameAsync("/opt/wow/WowClassic.exe");

        Assert.False(stopped);
        Assert.Equal(0, proxy.StopCount);   // the playing user's connection is not cut
        Assert.True(session.HasProxy);      // still owned, still reapable later
    }

    [Fact]
    public async Task StopProxyIfNoGame_StopsTheProxy_WhenNoClientIsRunning()
    {
        var proxy = new FakeProxy();
        var session = NewSession(new ScriptedDetector(false)); // nothing running
        session.AttachProxy(proxy);

        var stopped = await session.StopProxyIfNoGameAsync();

        Assert.True(stopped);
        Assert.Equal(1, proxy.StopCount);
        Assert.False(session.HasProxy);
    }

    [Fact]
    public async Task StopProxyIfNoGame_IsANoOp_WithNoProxyAttached_1121()
    {
        // 1.12.1 speaks straight to the realm — nothing was ever attached, so a quit has nothing to reap.
        var session = NewSession(new ScriptedDetector(false));

        var stopped = await session.StopProxyIfNoGameAsync();

        Assert.False(stopped);
    }

    // ── The exit watchdog ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MonitorUntilExit_ReapsTheProxy_OnceTheGameIsGone()
    {
        var proxy = new FakeProxy();
        // appears, stays up one more poll, then gone.
        var detector = new ScriptedDetector(true, true, false);
        var session = NewSession(detector);
        session.AttachProxy(proxy);

        await session.MonitorUntilExitAsync("/opt/wow/WowClassic.exe");

        Assert.Equal(1, proxy.StopCount);
        Assert.False(session.HasProxy);
    }

    [Fact]
    public async Task MonitorUntilExit_NeverReapsWhileTheGameIsStillRunning()
    {
        var proxy = new FakeProxy();
        // Running for several polls, then gone — the reap must not happen before that last answer.
        var detector = new ScriptedDetector(true, true, true, true, false);
        var session = NewSession(detector);
        session.AttachProxy(proxy);

        await session.MonitorUntilExitAsync("/opt/wow/WowClassic.exe");

        Assert.Equal(1, proxy.StopCount);
        // The detector was polled more than once → the loop genuinely waited for "gone" rather than
        // reaping on the first look.
        Assert.True(detector.Calls >= 3, "the watchdog must keep polling until the game is actually gone");
    }

    [Fact]
    public async Task MonitorUntilExit_WithNoProxy_JustWaitsOutTheGame_1121()
    {
        // 1.12.1: no proxy attached. The watchdog still runs (so the window can be restored), but has
        // nothing to stop and must not throw.
        var detector = new ScriptedDetector(true, false);
        var session = NewSession(detector);

        var ex = await Record.ExceptionAsync(() => session.MonitorUntilExitAsync("/opt/wow/WoW.exe"));

        Assert.Null(ex);
        Assert.False(session.HasProxy);
    }

    [Fact]
    public async Task MonitorUntilExit_IsCancellable_AndDoesNotReapOnCancel()
    {
        var proxy = new FakeProxy();
        var detector = new ScriptedDetector(true); // stays running forever
        var session = NewSession(detector);
        session.AttachProxy(proxy);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
        await session.MonitorUntilExitAsync("/opt/wow/WowClassic.exe", cts.Token);

        // Cancelled mid-wait → the proxy is left exactly as it was (a torn-down wait must not reap).
        Assert.Equal(0, proxy.StopCount);
        Assert.True(session.HasProxy);
    }

    // ── Liveness ≠ ownership (Codex review): a bound client PID is authoritative over the name-scan ──

    /// <summary>A scripted bound-PID liveness: a sequence of alive/dead answers, last value repeats.</summary>
    private static Func<int, bool> ScriptedPid(params bool[] answers)
    {
        var i = 0;
        return _ =>
        {
            var v = answers[Math.Min(i, answers.Length - 1)];
            if (i < answers.Length - 1) i++;
            return v;
        };
    }

    private static GameSession NewSessionWithPid(IGameProcessDetector detector, Func<int, bool> isPidAlive) =>
        new(detector, Log(),
            appearTimeout: TimeSpan.FromMilliseconds(200),
            pollInterval: TimeSpan.FromMilliseconds(5),
            delay: (d, ct) => Task.Delay(d, ct),
            isPidAlive: isPidAlive);

    /// <summary>With a client PID bound, the reap follows THAT process, not the name-scan. A foreign
    /// same-named process (the detector says "running" forever) must NOT hold the proxy open once our own
    /// bound PID is gone — the exact "holds it too long" failure the ownership binding closes.</summary>
    [Fact]
    public async Task MonitorUntilExit_WithBoundPid_ReapsWhenOurPidIsGone_EvenIfANameScanStillSeesAClient()
    {
        var proxy = new FakeProxy();
        var detector = new ScriptedDetector(true); // a same-named foreign client appears "running" forever
        var session = NewSessionWithPid(detector, ScriptedPid(true, true, false)); // our PID dies
        session.BindClientProcess(4242);
        session.AttachProxy(proxy);

        await session.MonitorUntilExitAsync("/opt/wow/WowClassic.exe");

        Assert.Equal(1, proxy.StopCount);   // reaped when OUR process ended, despite the name-scan
        Assert.False(session.HasProxy);
    }

    /// <summary>The other direction: a bound PID that is still alive keeps the proxy even when the
    /// name-scan says nothing is running — the "reaps too early" failure the binding closes.</summary>
    [Fact]
    public async Task StopProxyIfNoGame_WithBoundPid_KeepsTheProxy_WhileOurPidIsAlive_EvenIfNameScanSaysGone()
    {
        var proxy = new FakeProxy();
        var session = NewSessionWithPid(new ScriptedDetector(false), ScriptedPid(true)); // name-scan gone, PID alive
        session.BindClientProcess(4242);
        session.AttachProxy(proxy);

        var stopped = await session.StopProxyIfNoGameAsync("/opt/wow/WowClassic.exe");

        Assert.False(stopped);
        Assert.Equal(0, proxy.StopCount);
        Assert.True(session.HasProxy);
    }

    /// <summary>And a bound PID that is dead stops the proxy even if a foreign same-named process makes
    /// the name-scan say "running".</summary>
    [Fact]
    public async Task StopProxyIfNoGame_WithBoundPid_StopsTheProxy_WhenOurPidIsDead_EvenIfNameScanSaysRunning()
    {
        var proxy = new FakeProxy();
        var session = NewSessionWithPid(new ScriptedDetector(true), ScriptedPid(false)); // name-scan running, PID dead
        session.BindClientProcess(4242);
        session.AttachProxy(proxy);

        var stopped = await session.StopProxyIfNoGameAsync("/opt/wow/WowClassic.exe");

        Assert.True(stopped);
        Assert.Equal(1, proxy.StopCount);
        Assert.False(session.HasProxy);
    }
}

/// <summary>The graceful-shutdown contract the owner requires: stop the proxy with SIGTERM first and
/// only escalate to SIGKILL if it does not exit in time — the same signal the working start script sends
/// (<c>pkill -x HermesProxy</c>), so HermesProxy can close its listener and sockets cleanly.</summary>
public sealed class ProxyGracefulStopTests
{
    private static Serilog.ILogger Log() => new Serilog.LoggerConfiguration().CreateLogger();
    private static string TempPidPath() =>
        Path.Combine(Path.GetTempPath(), $"st-proxy-grace-{Guid.NewGuid():N}.pid");

    // Closed on the pre-start check, open from then on: a normal healthy start.
    private static Func<int, CancellationToken, Task<bool>> OpensAfter(int closedCount)
    {
        var calls = 0;
        return (_, _) => Task.FromResult(Interlocked.Increment(ref calls) > closedCount);
    }

    [Fact]
    public async Task Stop_TriesSigtermFirst_AndDoesNotHardKill_WhenTheProxyExitsCleanly()
    {
        var order = new List<string>();
        // SIGTERM stand-in that actually makes the process exit (the graceful signal worked).
        bool Sigterm(int pid)
        {
            order.Add("term");
            try { using var p = Process.GetProcessById(pid); p.Kill(); } catch { /* already gone */ }
            return true;
        }
        void HardKill(Process p) { order.Add("kill"); p.Kill(entireProcessTree: true); }

        var runner = new HermesProxyRunner(
            Log(), "/usr/bin/sleep", ["30"], TempPidPath(), OpensAfter(1),
            sigterm: Sigterm, hardKill: HardKill, stopGrace: TimeSpan.FromSeconds(2));

        var started = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));
        Assert.True(started.Ready);

        await runner.StopAsync();

        // Graceful was attempted, and because the process exited within the grace, SIGKILL never ran.
        Assert.Equal(["term"], order);
    }

    [Fact]
    public async Task Stop_EscalatesToSigkill_OnlyAfterTheGracePeriodElapses_WhenSigtermIsIgnored()
    {
        var order = new List<string>();
        // SIGTERM "delivered" but ignored — the process keeps running (models a proxy that traps it).
        bool Sigterm(int pid) { order.Add("term"); return true; }
        void HardKill(Process p) { order.Add("kill"); p.Kill(entireProcessTree: true); }

        var grace = TimeSpan.FromMilliseconds(300);
        var runner = new HermesProxyRunner(
            Log(), "/usr/bin/sleep", ["30"], TempPidPath(), OpensAfter(1),
            sigterm: Sigterm, hardKill: HardKill, stopGrace: grace);

        var started = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));
        Assert.True(started.Ready);
        var pid = started.ProcessId!.Value;

        var sw = Stopwatch.StartNew();
        await runner.StopAsync();
        sw.Stop();

        // Order proves it: graceful FIRST, hard kill SECOND — and only after the grace window elapsed.
        Assert.Equal(["term", "kill"], order);
        Assert.True(sw.Elapsed >= grace - TimeSpan.FromMilliseconds(50),
            $"SIGKILL fired after only {sw.Elapsed} — it must wait out the {grace} grace before escalating.");

        // And the process really is gone (the fallback did its job).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        var gone = false;
        while (DateTime.UtcNow < deadline)
        {
            try { using var p = Process.GetProcessById(pid); gone = p.HasExited; }
            catch (ArgumentException) { gone = true; }
            if (gone) break;
            await Task.Delay(50);
        }
        Assert.True(gone, $"PID {pid} survived the SIGKILL fallback.");
    }
}

/// <summary>The hide/restore seam: unconfigured (headless/screenshot/no-tray) it is a safe no-op so a
/// ViewModel can call it unconditionally; configured, it forwards to the wired window actions.</summary>
public sealed class ShellWindowControllerTests
{
    [Fact]
    public void IsUnconfigured_UntilWired_AndCallsAreNoOps()
    {
        var c = new ShellWindowController();
        Assert.False(c.IsConfigured);
        // Must not throw even though nothing is wired.
        c.HideToTray();
        c.RestoreFromTray();
    }

    [Fact]
    public void Configured_ForwardsToTheWiredActions()
    {
        var c = new ShellWindowController();
        int hides = 0, restores = 0;
        c.Configure(hideToTray: () => hides++, restoreFromTray: () => restores++);

        Assert.True(c.IsConfigured);
        c.HideToTray();
        c.RestoreFromTray();

        Assert.Equal(1, hides);
        Assert.Equal(1, restores);
    }
}
