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
/// The native-Windows proxy lifecycle (start, prove-listening, self-healing stop, Windows-shaped
/// graceful-then-hard stop) that the 1.14.2 native launch depends on. Same discipline as
/// <see cref="GameProxyTests"/> for HermesProxyRunner: no JimsProxy binary and no Windows host are
/// needed — <c>/usr/bin/sleep</c> and <c>/usr/bin/true</c> stand in as real child processes and the TCP
/// "is it listening" probe is injected. The Windows-only bit (closing the proxy's window) is exercised
/// through the injected graceful/hard seams so the escalation ORDER is proven on any OS; the real
/// window-close is E2E-only.
/// </summary>
public sealed class JimsProxyRunnerTests
{
    private static string TempPidPath() =>
        Path.Combine(Path.GetTempPath(), $"st-jims-test-{Guid.NewGuid():N}.pid");

    private static Serilog.ILogger Log() => new Serilog.LoggerConfiguration().CreateLogger();

    /// <summary>Closed for the first <paramref name="closedCount"/> calls, open forever after. Call 1 is
    /// the pre-start "is the port already taken" check, so a normal start needs the port closed for at
    /// least that call; <c>OpensAfter(0)</c> = already open before we started = a port conflict.</summary>
    private static Func<int, CancellationToken, Task<bool>> OpensAfter(int closedCount)
    {
        var calls = 0;
        return (_, _) => Task.FromResult(Interlocked.Increment(ref calls) > closedCount);
    }

    private static Func<int, CancellationToken, Task<bool>> NeverOpens() =>
        (_, _) => Task.FromResult(false);

    /// <summary>An owner probe that reports the PID the runner recorded in its pidfile — i.e. the process
    /// WE started owns the listener. The runner writes the pidfile before it probes the owner, so this
    /// reads back the started PID and stands in for what GetExtendedTcpTable/netstat report on real
    /// Windows. Needed on every "ready" test now that an undeterminable owner is fail-closed.</summary>
    private static Func<int, CancellationToken, Task<int?>> OwnerFromPidFile(string pidPath) =>
        (_, _) =>
        {
            try
            {
                if (File.Exists(pidPath) && int.TryParse(File.ReadAllText(pidPath).Trim(), out var p))
                    return Task.FromResult<int?>(p);
            }
            catch { /* ignore */ }
            return Task.FromResult<int?>(null);
        };

    [Fact]
    public async Task StartAndWaitForPortAsync_ReportsReady_OnceTheProbeSaysOpen()
    {
        var pidPath = TempPidPath();
        var runner = new JimsProxyRunner(
            Log(), "/usr/bin/sleep", ["5"], pidPath, OpensAfter(3), portOwnerProbe: OwnerFromPidFile(pidPath));

        var result = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));

        Assert.True(result.Ready);
        Assert.NotNull(result.ProcessId);

        await runner.StopAsync();
    }

    [Fact]
    public async Task StartAndWaitForPortAsync_NeverReportsReady_WhenThePortNeverOpens()
    {
        var runner = new JimsProxyRunner(Log(), "/usr/bin/sleep", ["5"], TempPidPath(), NeverOpens());

        var result = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromMilliseconds(300));

        Assert.False(result.Ready);
        Assert.NotNull(result.Error);
    }

    /// <summary>"It ran for a while" is not "it worked": a proxy that started and exited without ever
    /// opening the port must be noticed at once, not after the whole timeout.</summary>
    [Fact]
    public async Task StartAndWaitForPortAsync_FailsFast_WhenTheProcessExitsBeforeOpeningThePort()
    {
        var runner = new JimsProxyRunner(Log(), "/usr/bin/true", [], TempPidPath(), NeverOpens());

        var sw = Stopwatch.StartNew();
        var result = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(10));
        sw.Stop();

        Assert.False(result.Ready);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"Took {sw.Elapsed} to notice an already-exited process - it waited out the timeout instead.");
    }

    [Fact]
    public async Task StartAndWaitForPortAsync_RefusesWhenThePortIsAlreadyHeldBySomethingElse()
    {
        var runner = new JimsProxyRunner(Log(), "/usr/bin/sleep", ["30"], TempPidPath(), OpensAfter(0));

        var result = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));

        Assert.False(result.Ready);
        Assert.NotNull(result.Error);
        Assert.Contains("1119", result.Error);
        // The message names the Windows way to clear it, not the Linux pkill.
        Assert.Contains("JimsProxy.exe", result.Error);

        await runner.StopAsync();
    }

    [Fact]
    public async Task StartAndWaitForPortAsync_FailsHonestly_WhenTheExeDoesNotExist()
    {
        var runner = new JimsProxyRunner(Log(), "/does/not/exist/JimsProxy.exe", [], TempPidPath(), OpensAfter(0));

        var result = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(1));

        Assert.False(result.Ready);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StopAsync_OnANeverStartedRunner_DoesNotThrow()
    {
        var runner = new JimsProxyRunner(Log(), "/usr/bin/sleep", ["5"], TempPidPath(), OpensAfter(0));
        var ex = Record.Exception(() => runner.StopAsync().GetAwaiter().GetResult());
        Assert.Null(ex);
    }

    /// <summary>The default stop path: the graceful window-close is a no-op off Windows (there is no
    /// window and the seam returns false), so the runner escalates to the real hard kill — and the
    /// process is actually gone afterwards, not merely "no longer tracked".</summary>
    [Fact]
    public async Task StopAsync_ActuallyKillsTheProcess()
    {
        var pidPath = TempPidPath();
        var runner = new JimsProxyRunner(
            Log(), "/usr/bin/sleep", ["30"], pidPath, OpensAfter(1), portOwnerProbe: OwnerFromPidFile(pidPath));

        var started = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));
        Assert.True(started.Ready);
        var pid = started.ProcessId!.Value;

        await runner.StopAsync();

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

    /// <summary>The Windows-shaped graceful stop: when the window-close IS delivered, the runner waits
    /// the grace window and escalates to the hard kill only after it, and it tries the graceful step
    /// BEFORE the hard kill. Proven with injected seams (a real /usr/bin/sleep that ignores the close),
    /// so the escalation order holds without a Windows console window.</summary>
    [Fact]
    public async Task GracefulStop_IsTriedBeforeTheHardKill_WhenTheCloseIsDelivered()
    {
        var order = new List<string>();
        var gracefulCalled = false;
        // Graceful "succeeds" (delivered) but the process ignores it, forcing the escalation.
        bool Graceful(Process _) { gracefulCalled = true; order.Add("graceful"); return true; }
        void HardKill(Process p) { order.Add("hard"); p.Kill(entireProcessTree: true); }

        var pidPath = TempPidPath();
        var runner = new JimsProxyRunner(
            Log(), "/usr/bin/sleep", ["30"], pidPath, OpensAfter(1),
            gracefulStop: Graceful, hardKill: HardKill, stopGrace: TimeSpan.FromMilliseconds(200),
            portOwnerProbe: OwnerFromPidFile(pidPath));

        var started = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));
        Assert.True(started.Ready);

        await runner.StopAsync();

        Assert.True(gracefulCalled, "The graceful close was never attempted.");
        Assert.Equal(["graceful", "hard"], order); // graceful first, hard only after the grace window
    }

    /// <summary>When the graceful close is NOT delivered (no window — the normal redirected-output case),
    /// the runner goes straight to the hard kill without waiting out the grace window.</summary>
    [Fact]
    public async Task GracefulStop_NotDelivered_GoesStraightToTheHardKill()
    {
        var order = new List<string>();
        bool Graceful(Process _) { order.Add("graceful"); return false; } // not delivered
        void HardKill(Process p) { order.Add("hard"); p.Kill(entireProcessTree: true); }

        var pidPath = TempPidPath();
        var runner = new JimsProxyRunner(
            Log(), "/usr/bin/sleep", ["30"], pidPath, OpensAfter(1),
            gracefulStop: Graceful, hardKill: HardKill, stopGrace: TimeSpan.FromSeconds(30),
            portOwnerProbe: OwnerFromPidFile(pidPath));

        var started = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));
        Assert.True(started.Ready);

        var sw = Stopwatch.StartNew();
        await runner.StopAsync();
        sw.Stop();

        Assert.Equal(["graceful", "hard"], order);
        // No 30s grace wait when the close was never delivered.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Waited {sw.Elapsed} instead of killing at once.");
    }

    /// <summary>Crash-survival: a PID left behind by a previous session (the launcher never reached its
    /// own StopAsync) is cleaned up the NEXT time a proxy starts.</summary>
    [Fact]
    public async Task ANewRunner_KillsAStaleProxyLeftOverFromAPreviousSession()
    {
        var pidPath = TempPidPath();
        var leftover = Process.Start(new ProcessStartInfo("/usr/bin/sleep", "30") { UseShellExecute = false })!;
        File.WriteAllText(pidPath, leftover.Id.ToString());

        try
        {
            var runner = new JimsProxyRunner(Log(), "/usr/bin/sleep", ["5"], pidPath, OpensAfter(0));
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

    // ── Port OWNERSHIP, not just liveness (Codex review P0) ─────────────────────────────────────

    /// <summary>"The port is open and our process is alive" is still not "our process is the one
    /// listening". When the listener's owning PID is a DIFFERENT process, the launch is refused — a
    /// foreign or stale listener holds 1119 while our proxy is alive but did not bind it.</summary>
    [Fact]
    public async Task StartAndWaitForPortAsync_Refuses_WhenTheListenerIsOwnedByAForeignPid()
    {
        // Port closed on the pre-check, open after; owner is a PID that is not ours (a huge sentinel).
        var runner = new JimsProxyRunner(
            Log(), "/usr/bin/sleep", ["30"], TempPidPath(), OpensAfter(1),
            portOwnerProbe: (_, _) => Task.FromResult<int?>(999_999));

        var result = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));

        Assert.False(result.Ready);
        Assert.NotNull(result.Error);
        Assert.Contains("1119", result.Error);

        await runner.StopAsync();
    }

    /// <summary>When the listener owner is our own started process, ownership is proven and the launch is
    /// ready. The runner records its started PID in the pidfile BEFORE it probes the owner, so the probe
    /// reads that PID back and returns it — standing in for what netstat reports on real Windows.</summary>
    [Fact]
    public async Task StartAndWaitForPortAsync_ReportsReady_WhenTheListenerIsOwnedByOurProxy()
    {
        var pidPath = TempPidPath();
        Task<int?> OwnerIsOurStartedProcess(int _, CancellationToken __)
        {
            try
            {
                if (File.Exists(pidPath) && int.TryParse(File.ReadAllText(pidPath).Trim(), out var p))
                    return Task.FromResult<int?>(p);
            }
            catch { /* ignore */ }
            return Task.FromResult<int?>(null);
        }

        var runner = new JimsProxyRunner(
            Log(), "/usr/bin/sleep", ["30"], pidPath, OpensAfter(1),
            portOwnerProbe: OwnerIsOurStartedProcess);

        var result = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));

        Assert.True(result.Ready);
        await runner.StopAsync();
    }

    /// <summary>Fail-closed (Codex Finding 1): when the owner cannot be determined at all (probe returns
    /// null), the launch is REFUSED rather than trusting the listener — a start against a possibly-foreign
    /// listener is worse than a retry. (Previously this fell back to liveness and reported ready.)</summary>
    [Fact]
    public async Task StartAndWaitForPortAsync_Refuses_WhenTheOwnerCannotBeDetermined()
    {
        var runner = new JimsProxyRunner(
            Log(), "/usr/bin/sleep", ["30"], TempPidPath(), OpensAfter(1),
            portOwnerProbe: (_, _) => Task.FromResult<int?>(null));

        var result = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));

        Assert.False(result.Ready);
        Assert.NotNull(result.Error);

        await runner.StopAsync();
    }

    // ── P0: references survive a failed kill so an orphan stays retryable ────────────────────────

    /// <summary>If the hard kill does not take, StopAsync must KEEP the process handle and the pidfile so
    /// a later reap (or the next-start self-heal) can retry — clearing them before a confirmed exit (the
    /// earlier bug) left an orphan on the port with nothing pointing at it. Modelled with a hardKill seam
    /// that does nothing (the process stays alive), so the "kill failed" branch is exercised.</summary>
    [Fact]
    public async Task StopAsync_KeepsTheHandleAndPidfile_WhenTheKillDoesNotTake()
    {
        var pidPath = TempPidPath();
        var neverExits = new List<string>();
        void NoOpKill(Process p) { neverExits.Add("tried"); /* deliberately does NOT kill */ }

        var runner = new JimsProxyRunner(
            Log(), "/usr/bin/sleep", ["30"], pidPath, OpensAfter(1),
            gracefulStop: _ => false, hardKill: NoOpKill, stopGrace: TimeSpan.FromMilliseconds(50),
            portOwnerProbe: OwnerFromPidFile(pidPath));

        var started = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));
        Assert.True(started.Ready);
        var realPid = started.ProcessId!.Value;

        await runner.StopAsync();

        // The kill was attempted, but since it did nothing the reference and pidfile are KEPT.
        Assert.Single(neverExits);
        Assert.True(File.Exists(pidPath), "The pidfile was deleted even though the kill did not take.");

        // Clean up the real leftover process for the test host.
        try { using var p = Process.GetProcessById(realPid); p.Kill(entireProcessTree: true); } catch { }
    }

    // ── Finding 1: the pre-client re-check of continued ownership ────────────────────────────────

    [Fact]
    public async Task VerifyStillListeningAsync_IsTrue_WhenOurProxyStillOwnsThePort()
    {
        var pidPath = TempPidPath();
        var runner = new JimsProxyRunner(
            Log(), "/usr/bin/sleep", ["30"], pidPath, OpensAfter(1), portOwnerProbe: OwnerFromPidFile(pidPath));

        var started = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));
        Assert.True(started.Ready);

        Assert.True(await runner.VerifyStillListeningAsync(1119));

        await runner.StopAsync();
    }

    [Fact]
    public async Task VerifyStillListeningAsync_IsFalse_WhenAForeignPidNowOwnsThePort()
    {
        var pidPath = TempPidPath();
        // Ownership matches at start, but the re-check sees a foreign owner (models a takeover).
        var reCheck = false;
        Task<int?> Owner(int _, CancellationToken __)
        {
            if (reCheck) return Task.FromResult<int?>(999_999);
            try { if (File.Exists(pidPath) && int.TryParse(File.ReadAllText(pidPath).Trim(), out var p)) return Task.FromResult<int?>(p); }
            catch { }
            return Task.FromResult<int?>(null);
        }
        var runner = new JimsProxyRunner(
            Log(), "/usr/bin/sleep", ["30"], pidPath, OpensAfter(1), portOwnerProbe: Owner);

        var started = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));
        Assert.True(started.Ready);

        reCheck = true;
        Assert.False(await runner.VerifyStillListeningAsync(1119));

        await runner.StopAsync();
    }

    [Theory]
    [InlineData("  TCP    127.0.0.1:1119     0.0.0.0:0     LISTENING     1234", 1119, 1234)]
    [InlineData("  TCP    0.0.0.0:1119       0.0.0.0:0     LISTENING     42", 1119, 42)]
    [InlineData("  TCP    [::]:1119          [::]:0        LISTENING     7", 1119, 7)]
    public void ParseNetstatOwner_ReturnsTheListenerPid(string line, int port, int expected)
    {
        Assert.Equal(expected, JimsProxyRunner.ParseNetstatOwner(line, port));
    }

    [Fact]
    public void ParseNetstatOwner_ReturnsNull_WhenNoListenerOnThatPort()
    {
        // An established connection to :1119 is not a LISTENER, and a listener on another port is not it.
        var output =
            "  TCP    127.0.0.1:52000   127.0.0.1:1119    ESTABLISHED   555\n" +
            "  TCP    0.0.0.0:8085       0.0.0.0:0        LISTENING     99\n";
        Assert.Null(JimsProxyRunner.ParseNetstatOwner(output, 1119));
    }

    /// <summary>The name check keeps the self-heal from becoming "kill anything at this PID": a stale
    /// pidfile pointing at a process that does not look like OUR proxy is left alone.</summary>
    [Fact]
    public async Task ANewRunner_NeverKillsAProcessThatDoesNotLookLikeTheProxy()
    {
        var pidPath = TempPidPath();
        var unrelated = Process.Start(new ProcessStartInfo("/usr/bin/sleep", "30") { UseShellExecute = false })!;
        File.WriteAllText(pidPath, unrelated.Id.ToString());

        try
        {
            // This runner's own exe is "true", not "sleep" - the stale entry must not match it.
            var runner = new JimsProxyRunner(Log(), "/usr/bin/true", [], pidPath, OpensAfter(0));
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
