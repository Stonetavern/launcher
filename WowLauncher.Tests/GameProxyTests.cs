using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The proxy lifecycle (start, prove-listening, self-healing stop) that WP1
/// (HANDOFF-LINUX-FERTIGBAUEN-2026-07-21.md §3) needs before a Windows client under Wine/Proton can
/// reach a realm through HermesProxy. No embedded binaries and no HermesProxy binary required for
/// these tests: <c>/usr/bin/sleep</c> and <c>/usr/bin/true</c> stand in as real, universally-present
/// child processes (proving the actual start/track/kill machinery), and the TCP "is it listening"
/// check is injected as a fake probe (proving the polling/timeout/early-exit LOGIC without needing a
/// real socket listener in the test process).
/// </summary>
public sealed class GameProxyTests
{
    private static string TempPidPath() =>
        Path.Combine(Path.GetTempPath(), $"st-proxy-test-{Guid.NewGuid():N}.pid");

    private static Serilog.ILogger Log() => new Serilog.LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task StartAndWaitForPortAsync_PassesExplicitEnvironmentOverridesToChild()
    {
        if (!OperatingSystem.IsLinux()) return;

        var output = Path.Combine(Path.GetTempPath(), $"st-proxy-env-{Guid.NewGuid():N}.txt");
        var script = Path.Combine(Path.GetTempPath(), $"st-proxy-env-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(script, $"#!/bin/sh\nprintf %s \"$DYLD_LIBRARY_PATH\" > {output}\nsleep 5\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var runner = new HermesProxyRunner(Log(), script, [], TempPidPath(), OpensAfter(1),
                new Dictionary<string, string> { ["DYLD_LIBRARY_PATH"] = "/runtime/openssl" });
            var result = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));
            Assert.True(result.Ready);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!File.Exists(output) && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.Equal("/runtime/openssl", await File.ReadAllTextAsync(output));
            await runner.StopAsync();
        }
        finally
        {
            File.Delete(script);
            File.Delete(output);
        }
    }

    /// <summary>A probe that reports "closed" for the first <paramref name="closedCount"/> calls, then
    /// "open" forever after - simulates a proxy that takes a moment to bind its listener.
    ///
    /// <para>Call 1 is the pre-start check ("is this port already taken by someone else"), so any
    /// scenario that wants a NORMAL start must leave the port closed for at least that one call.
    /// <c>OpensAfter(0)</c> therefore means "the port was already open before we started anything",
    /// which is a port conflict and is refused - see the test that pins exactly that.</para></summary>
    private static Func<int, CancellationToken, Task<bool>> OpensAfter(int closedCount)
    {
        var calls = 0;
        return (_, _) => Task.FromResult(Interlocked.Increment(ref calls) > closedCount);
    }

    private static Func<int, CancellationToken, Task<bool>> NeverOpens() =>
        (_, _) => Task.FromResult(false);

    [Fact]
    public async Task StartAndWaitForPortAsync_ReportsReady_OnceTheProbeSaysOpen()
    {
        var runner = new HermesProxyRunner(Log(), "/usr/bin/sleep", ["5"], TempPidPath(), OpensAfter(3));

        var result = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));

        Assert.True(result.Ready);
        Assert.NotNull(result.ProcessId);

        await runner.StopAsync();
    }

    [Fact]
    public async Task StartAndWaitForPortAsync_NeverReportsReady_WhenThePortNeverOpens()
    {
        var runner = new HermesProxyRunner(Log(), "/usr/bin/sleep", ["5"], TempPidPath(), NeverOpens());

        var result = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromMilliseconds(300));

        Assert.False(result.Ready);
        Assert.NotNull(result.Error);
    }

    /// <summary>The exact case the launcher must NOT silently accept: a proxy that started, exited
    /// again almost immediately, and never opened anything. "It ran for a while" is not "it worked".</summary>
    [Fact]
    public async Task StartAndWaitForPortAsync_FailsFast_WhenTheProcessExitsBeforeOpeningThePort()
    {
        var runner = new HermesProxyRunner(Log(), "/usr/bin/true", [], TempPidPath(), NeverOpens());

        var sw = Stopwatch.StartNew();
        var result = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(10));
        sw.Stop();

        Assert.False(result.Ready);
        // /usr/bin/true exits in milliseconds - a correct implementation notices immediately and does
        // not sit through the whole 10s timeout waiting for a process that is already gone.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"Took {sw.Elapsed} to notice an already-exited process - it waited out the timeout instead.");
    }

    /// <summary>
    /// A port already held by something the launcher does not own must be refused BEFORE a proxy is
    /// started, not discovered afterwards. Observed for real on 2026-07-22 during the first end-to-end
    /// run: a proxy left over from an earlier session still held 1119, the readiness probe went green
    /// on its first poll (it can only see that SOMETHING listens), and the proxy that had just been
    /// started died of the address conflict a few seconds later. The launch reported success and the
    /// client would have talked to a stale process nobody owned. Checking after the fact is a race -
    /// whether the dead proxy is noticed depends on how fast it dies - so the check has to come first.
    /// </summary>
    [Fact]
    public async Task StartAndWaitForPortAsync_RefusesWhenThePortIsAlreadyHeldBySomethingElse()
    {
        // Open from the very first probe = open before anything of ours was started.
        var runner = new HermesProxyRunner(Log(), "/usr/bin/sleep", ["30"], TempPidPath(), OpensAfter(0));

        var result = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));

        Assert.False(result.Ready);
        Assert.NotNull(result.Error);
        Assert.Contains("1119", result.Error);

        await runner.StopAsync();
    }

    [Fact]
    public async Task StopAsync_ActuallyKillsTheProcess()
    {
        // Closed on the pre-start check, open from then on: a normal, healthy start.
        var runner = new HermesProxyRunner(Log(), "/usr/bin/sleep", ["30"], TempPidPath(), OpensAfter(1));

        var started = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));
        Assert.True(started.Ready);
        var pid = started.ProcessId!.Value;

        await runner.StopAsync();

        // Give the OS a moment to reap it, then confirm it is really gone - not "we stopped tracking
        // it" but "the process no longer exists".
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        var stillRunning = true;
        while (DateTime.UtcNow < deadline)
        {
            try { using var p = Process.GetProcessById(pid); stillRunning = !p.HasExited; }
            catch (ArgumentException) { stillRunning = false; }
            if (!stillRunning) break;
            await Task.Delay(100);
        }
        Assert.False(stillRunning, $"PID {pid} was still running after StopAsync.");
    }

    /// <summary>A stop that did not work must not report success by cleaning up. Until 2026-08-03 this
    /// method cleared its handle and deleted the PID file BEFORE killing and then threw the kill's result
    /// away, so a failed kill left a live proxy on 1119 that nothing pointed at any more — not this
    /// instance, and not the next start's stale-PID sweep, which reads exactly that file. The player then
    /// hit "port already in use" on the next Play, from a proxy the launcher believed it had stopped.
    ///
    /// <para>Both seams are neutered here (no signal, no kill) while the child really keeps running, so
    /// what is measured is the world, not the exit code.</para></summary>
    [Fact]
    public async Task AStopThatFails_KeepsThePidFile_SoTheNextStartCanReapIt()
    {
        if (!OperatingSystem.IsLinux()) return;

        var pidFile = TempPidPath();
        var runner = new HermesProxyRunner(
            Log(), "/usr/bin/sleep", ["30"], pidFile, OpensAfter(1),
            sigterm: _ => false,            // signal never delivered
            hardKill: static _ => { },      // and the hard kill does nothing either
            stopGrace: TimeSpan.FromMilliseconds(50));

        var started = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));
        Assert.True(started.Ready);
        var pid = started.ProcessId!.Value;
        try
        {
            await runner.StopAsync();

            Assert.True(File.Exists(pidFile), "a failed stop deleted the PID file, orphaning a live proxy");
            Assert.Equal(pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (await File.ReadAllTextAsync(pidFile)).Trim());
            using var alive = Process.GetProcessById(pid);
            Assert.False(alive.HasExited);   // the premise of the test: it really did survive
        }
        finally
        {
            try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
            File.Delete(pidFile);
        }
    }

    /// <summary>The other direction, so the test above cannot pass by never cleaning up at all: a stop
    /// that worked does remove the PID file.</summary>
    [Fact]
    public async Task AStopThatWorks_RemovesThePidFile()
    {
        if (!OperatingSystem.IsLinux()) return;

        var pidFile = TempPidPath();
        var runner = new HermesProxyRunner(Log(), "/usr/bin/sleep", ["30"], pidFile, OpensAfter(1));

        var started = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));
        Assert.True(started.Ready);
        Assert.True(File.Exists(pidFile));

        await runner.StopAsync();

        Assert.False(File.Exists(pidFile));
    }

    [Fact]
    public void StopAsync_OnANeverStartedRunner_DoesNotThrow()
    {
        var runner = new HermesProxyRunner(Log(), "/usr/bin/sleep", ["5"], TempPidPath(), OpensAfter(0));
        // Never started - StopAsync must be safe to call anyway (e.g. an aborted Play attempt).
        var ex = Record.Exception(() => runner.StopAsync().GetAwaiter().GetResult());
        Assert.Null(ex);
    }

    [Fact]
    public async Task StartAndWaitForPortAsync_FailsHonestly_WhenTheExeDoesNotExist()
    {
        var runner = new HermesProxyRunner(Log(), "/does/not/exist/HermesProxy", [], TempPidPath(), OpensAfter(0));

        var result = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(1));

        Assert.False(result.Ready);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── The re-check right before the client starts ───────────────────────────────────────────────
    //
    // Between "the proxy is ready" and "the client is running" sit Wine startup, a winepath call and a
    // process spawn — seconds in which the listener can change hands. This used to be a hardcoded
    // `true` on every platform but Windows: a security check that answered "all fine" without ever
    // looking, which is the failure shape that costs the most, because nothing about it looks broken.

    [Fact]
    public async Task TheRecheck_PassesWhenOurOwnProxyStillOwnsThePort()
    {
        var owner = new Owner();
        var runner = new HermesProxyRunner(Log(), "/usr/bin/sleep", ["30"], TempPidPath(),
            OpensAfter(1), portOwnerProbe: owner.Probe);
        try
        {
            var started = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));
            Assert.True(started.Ready);
            owner.Pid = started.ProcessId;   // the listener belongs to the proxy we started

            Assert.True(await runner.VerifyStillListeningAsync(1119));
        }
        finally { await runner.StopAsync(); }
    }

    [Fact]
    public async Task TheRecheck_RefusesWhenTheListenerChangedHands()
    {
        // The 2026-07-22 shape: the port is open, so every "is it listening" probe stays green — but it
        // is somebody else's listener now, and starting the client would send the session to it.
        var runner = new HermesProxyRunner(Log(), "/usr/bin/sleep", ["30"], TempPidPath(),
            OpensAfter(1), portOwnerProbe: (_, _) => Task.FromResult<int?>(999_999));
        try
        {
            Assert.True((await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5))).Ready);

            Assert.False(await runner.VerifyStillListeningAsync(1119));
        }
        finally { await runner.StopAsync(); }
    }

    [Fact]
    public async Task TheRecheck_RefusesWhenOurProxyDied_EvenThoughThePortIsStillOpen()
    {
        // A proxy that comes up, is proven listening, and then dies while Wine is still starting. The
        // port probe says open the whole way through, so only the liveness question can catch it — and
        // it is exactly the case where something else is holding 1119 by the time the client connects.
        var owner = new Owner();
        var runner = new HermesProxyRunner(Log(), "/usr/bin/sleep", ["0.3"], TempPidPath(),
            OpensAfter(1), portOwnerProbe: owner.Probe);
        var started = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));
        Assert.True(started.Ready);
        owner.Pid = started.ProcessId;

        await Task.Delay(900);   // the proxy dies in here, the port stays "open" to the probe

        Assert.False(await runner.VerifyStillListeningAsync(1119));
    }

    [Fact]
    public async Task TheRecheck_StillPlays_WhenTheOwnerCannotBeDeterminedAtAll()
    {
        // Deliberate difference from the Windows runner: an unreadable owner means "I do not know", and
        // taking the game away from a player whose setup is fine is the wrong answer to not knowing.
        var runner = new HermesProxyRunner(Log(), "/usr/bin/sleep", ["30"], TempPidPath(),
            OpensAfter(1), portOwnerProbe: (_, _) => Task.FromResult<int?>(null));
        try
        {
            Assert.True((await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5))).Ready);

            Assert.True(await runner.VerifyStillListeningAsync(1119));
        }
        finally { await runner.StopAsync(); }
    }

    /// <summary>An owner probe whose answer the test sets AFTER the proxy has started — the PID only
    /// exists then, and the runner reports it. No guessing from process tables.</summary>
    private sealed class Owner
    {
        public int? Pid;
        public Func<int, CancellationToken, Task<int?>> Probe => (_, _) => Task.FromResult(Pid);
    }

    /// <summary>
    /// The crash-survival half: a PID left behind by a previous session (the launcher never reached
    /// its own StopAsync, e.g. it was killed) must be cleaned up the NEXT time a proxy is started -
    /// not left running forever, silently competing for the same port.
    /// </summary>
    [Fact]
    public async Task ANewRunner_KillsAStaleProxyLeftOverFromAPreviousSession()
    {
        var pidPath = TempPidPath();

        // Simulate the previous (crashed) session: a real sleep process, its PID recorded exactly the
        // way HermesProxyRunner records its own.
        var leftover = Process.Start(new ProcessStartInfo("/usr/bin/sleep", "30") { UseShellExecute = false })!;
        File.WriteAllText(pidPath, leftover.Id.ToString());

        try
        {
            var runner = new HermesProxyRunner(Log(), "/usr/bin/sleep", ["5"], pidPath, OpensAfter(0));
            await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (!leftover.HasExited && DateTime.UtcNow < deadline)
                await Task.Delay(100);

            Assert.True(leftover.HasExited, "The stale sleep process from the previous session was not killed.");
        }
        finally
        {
            if (!leftover.HasExited) leftover.Kill(entireProcessTree: true);
            leftover.Dispose();
        }
    }

    /// <summary>The name check that keeps the self-heal from becoming a "kill anything at this PID"
    /// footgun: a stale pidfile pointing at a process that no longer looks like OUR proxy (a totally
    /// different program the OS has since reused the PID for) must be left alone.</summary>
    [Fact]
    public async Task ANewRunner_NeverKillsAProcessThatDoesNotLookLikeTheProxy()
    {
        var pidPath = TempPidPath();

        // A real, unrelated process (a different binary name) recorded at the pidfile path - stands
        // in for "the OS recycled this PID for something else since the crash".
        var unrelated = Process.Start(new ProcessStartInfo("/usr/bin/sleep", "30") { UseShellExecute = false })!;
        File.WriteAllText(pidPath, unrelated.Id.ToString());

        try
        {
            // This runner's own exe is "true", not "sleep" - the stale entry must not match it.
            var runner = new HermesProxyRunner(Log(), "/usr/bin/true", [], pidPath, OpensAfter(0));
            await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(1));

            await Task.Delay(300);
            Assert.False(unrelated.HasExited, "An unrelated process was killed by the stale-PID cleanup.");
        }
        finally
        {
            if (!unrelated.HasExited) unrelated.Kill(entireProcessTree: true);
            unrelated.Dispose();
        }
    }
}
