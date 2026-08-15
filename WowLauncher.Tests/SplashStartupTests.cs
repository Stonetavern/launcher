using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using WowLauncher.Localization;
using WowLauncher.ViewModels;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The start screen's one rule: it must never outlive its work.
///
/// <para>These tests exist because a splash is the easiest place in a desktop app to strand a player.
/// Every failure mode gets its own red test — the work finishing, the work throwing, the work hanging,
/// the player dismissing it, and the self-update that ends the process instead of returning. If any of
/// them could leave <see cref="SplashViewModel.RunAsync"/> pending, the test hangs and the suite goes
/// red; there is no assertion that can pass while the screen stands forever.</para>
///
/// <para>Time is injected, so nothing here sleeps: the "budget" and the "grace" are tasks the test
/// completes by hand.</para>
/// </summary>
public sealed partial class SplashStartupTests
{
    // ── Doubles ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Startup work the test drives: it finishes, throws or hangs exactly when told to.</summary>
    private sealed class ScriptedWork : IStartupWork
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler? LauncherUpdateStarted;

        public int Runs { get; private set; }
        public CancellationToken SeenToken { get; private set; }

        public Task RunAsync(CancellationToken ct)
        {
            Runs++;
            SeenToken = ct;
            return _tcs.Task;
        }

        public void Finish() => _tcs.TrySetResult();
        public void Fail(Exception ex) => _tcs.TrySetException(ex);
        public void Cancel() => _tcs.TrySetCanceled();
        public void AnnounceLauncherUpdate() => LauncherUpdateStarted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A clock the test moves: each requested wait is handed back as a task to complete.</summary>
    private sealed class ManualClock
    {
        private readonly System.Collections.Generic.List<TaskCompletionSource> _pending = [];

        public int Waits { get { lock (_pending) return _pending.Count; } }

        /// <summary>The duration of the most recently requested wait — the minimum display time is a
        /// promise about a duration, so the duration is what gets asserted.</summary>
        public TimeSpan LastRequested { get; private set; }

        public Task Delay(TimeSpan duration, CancellationToken ct)
        {
            LastRequested = duration;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pending) _pending.Add(tcs);
            ct.Register(() => tcs.TrySetResult());
            return tcs.Task;
        }

        /// <summary>Let the most recently requested wait expire.</summary>
        public void Expire()
        {
            TaskCompletionSource last;
            lock (_pending)
            {
                Assert.NotEmpty(_pending);
                last = _pending[^1];
            }
            last.TrySetResult();
        }
    }

    private static SplashViewModel NewSplash(ScriptedWork work, ManualClock clock) =>
        // post: straight through, so the test observes the bound properties without a dispatcher.
        new(work, clock.Delay, a => a());

    /// <summary>Fails the test rather than hanging the suite: a splash that never settles is the exact
    /// defect these tests are here to catch, and a hung xunit run reports nothing useful.</summary>
    private static async Task<StartupOutcome> Settle(Task<StartupOutcome> run)
    {
        var done = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(ReferenceEquals(done, run),
            "the start screen never settled - it would still be on the player's desktop");
        return await run;
    }

    /// <summary>Wait until the state machine has asked the clock for its n-th wait, so a test never
    /// races the transition it is about to drive.</summary>
    private static async Task WaitForWaits(ManualClock clock, int expected)
    {
        for (var i = 0; i < 200 && clock.Waits < expected; i++) await Task.Delay(5);
        Assert.Equal(expected, clock.Waits);
    }

    /// <summary>Let the pending floor expire, so a test that only cares about the outcome does not have
    /// to spell out the minimum display time. Waits for the request first: expiring before the state
    /// machine has asked for the wait would hit the budget instead and prove nothing.</summary>
    private static async Task ReleaseFloor(ManualClock clock)
    {
        await WaitForWaits(clock, 2);    // the budget, then the floor
        clock.Expire();
    }

    // ── Success ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Work_that_completes_ends_the_splash()
    {
        var work = new ScriptedWork();
        var clock = new ManualClock();
        var vm = NewSplash(work, clock);

        var run = vm.RunAsync(CancellationToken.None);
        work.Finish();
        await ReleaseFloor(clock);

        Assert.Equal(StartupOutcome.Completed, await Settle(run));
        Assert.Equal(StartupOutcome.Completed, vm.Outcome);
    }

    /// <summary>Owner directive 2026-08-05, and the inverse of the test that stood here before: the
    /// screen used to be forbidden from holding the player at all. Finished work is not enough to end
    /// it — a launcher that appears and vanishes inside a second reads as a crash.</summary>
    [Fact]
    public async Task Finished_work_alone_does_not_end_the_splash_before_the_minimum_display_time()
    {
        var work = new ScriptedWork();
        var clock = new ManualClock();   // never expires anything
        var vm = NewSplash(work, clock);

        var run = vm.RunAsync(CancellationToken.None);
        work.Finish();

        await WaitForWaits(clock, 2);    // the floor was asked for, and nothing lets it pass
        Assert.False(run.IsCompleted);

        clock.Expire();
        Assert.Equal(StartupOutcome.Completed, await Settle(run));
    }

    /// <summary>The floor is a floor, not an addition. Work that already took longer than the minimum
    /// hands over immediately — otherwise every slow start would pay the five seconds twice.</summary>
    [Fact]
    public async Task Work_that_outlasted_the_minimum_hands_over_without_waiting_again()
    {
        var work = new ScriptedWork();
        var clock = new ManualClock();   // never expires anything
        var vm = new SplashViewModel(work, clock.Delay, a => a(),
            minimumDisplay: TimeSpan.FromSeconds(5), elapsed: () => TimeSpan.FromSeconds(9));

        var run = vm.RunAsync(CancellationToken.None);
        work.Finish();

        Assert.Equal(StartupOutcome.Completed, await Settle(run));
        Assert.Equal(1, clock.Waits);    // the budget only — no floor was ever requested
    }

    /// <summary>The remaining time is computed, not assumed: five seconds of floor after two seconds of
    /// work is three more, not five. Asserted on the duration the clock is asked for, because that is
    /// the number a player feels.</summary>
    [Fact]
    public async Task Only_the_remainder_of_the_minimum_is_waited_out()
    {
        var work = new ScriptedWork();
        var clock = new ManualClock();
        var vm = new SplashViewModel(work, clock.Delay, a => a(),
            minimumDisplay: TimeSpan.FromSeconds(5), elapsed: () => TimeSpan.FromSeconds(2));

        var run = vm.RunAsync(CancellationToken.None);
        work.Finish();
        await ReleaseFloor(clock);
        await Settle(run);

        Assert.Equal(TimeSpan.FromSeconds(3), clock.LastRequested);
    }

    // ── Failure ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Work_that_throws_ends_the_splash_instead_of_propagating()
    {
        var work = new ScriptedWork();
        var clock = new ManualClock();
        var vm = NewSplash(work, clock);

        var run = vm.RunAsync(CancellationToken.None);
        work.Fail(new InvalidOperationException("manifest host unreachable"));
        await ReleaseFloor(clock);

        // Not a rethrow: an exception escaping RunAsync would skip the caller's hand-over and leave
        // the splash on screen, which is the one outcome that must not exist.
        Assert.Equal(StartupOutcome.Failed, await Settle(run));
    }

    [Fact]
    public async Task Work_that_throws_before_its_first_await_ends_the_splash()
    {
        var vm = new SplashViewModel(new ThrowingWork(), (_, _) => Task.Delay(Timeout.Infinite), a => a());

        Assert.Equal(StartupOutcome.Failed, await Settle(vm.RunAsync(CancellationToken.None)));
    }

    private sealed class ThrowingWork : IStartupWork
    {
        public event EventHandler? LauncherUpdateStarted { add { } remove { } }

        public Task RunAsync(CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    // ── Hanging work: the shell wins, the splash does not stand forever ──────────────────────────

    [Fact]
    public async Task Work_that_never_finishes_hands_over_to_the_shell_when_the_budget_expires()
    {
        var work = new ScriptedWork();
        var clock = new ManualClock();
        var vm = NewSplash(work, clock);

        var run = vm.RunAsync(CancellationToken.None);
        Assert.False(run.IsCompleted);   // still waiting, as it should be

        clock.Expire();                  // the budget runs out; the work is still pending

        Assert.Equal(StartupOutcome.TimedOut, await Settle(run));
    }

    [Fact]
    public async Task A_dismissed_splash_reports_cancelled()
    {
        var work = new ScriptedWork();
        var clock = new ManualClock();
        var vm = NewSplash(work, clock);
        using var cts = new CancellationTokenSource();

        var run = vm.RunAsync(cts.Token);
        cts.Cancel();                    // the clock's wait is registered on the token

        Assert.Equal(StartupOutcome.Cancelled, await Settle(run));
        Assert.Equal(cts.Token, work.SeenToken);
    }

    [Fact]
    public async Task Cancelled_work_reports_cancelled_not_failed()
    {
        var work = new ScriptedWork();
        var vm = NewSplash(work, new ManualClock());

        var run = vm.RunAsync(CancellationToken.None);
        work.Cancel();

        Assert.Equal(StartupOutcome.Cancelled, await Settle(run));
    }

    // ── The self-update: the case this launcher cannot afford to get wrong ───────────────────────

    [Fact]
    public async Task A_self_update_is_named_on_screen_and_the_wording_comes_from_the_catalogue()
    {
        // Rewording the catalogue entry is the only assertion that goes red on a hardcoded sentence:
        // comparing against Loc.T would pass either way (Loc.OverrideForTests exists for exactly this).
        using var _ = Loc.OverrideForTests("Splash_Status_UpdatingLauncher", "SWAPPING THE BINARY");

        var work = new ScriptedWork();
        var clock = new ManualClock();
        var vm = NewSplash(work, clock);

        var run = vm.RunAsync(CancellationToken.None);
        Assert.False(vm.IsUpdatingLauncher);

        work.AnnounceLauncherUpdate();

        Assert.True(vm.IsUpdatingLauncher);
        Assert.Equal("SWAPPING THE BINARY", vm.Status);

        // Housekeeping so the run does not leak a pending task into the next test.
        work.Finish();
        await ReleaseFloor(clock);
        await Settle(run);
    }

    [Fact]
    public async Task The_budget_alone_does_not_cut_a_running_self_update_short()
    {
        var work = new ScriptedWork();
        var clock = new ManualClock();
        var vm = NewSplash(work, clock);

        var run = vm.RunAsync(CancellationToken.None);
        work.AnnounceLauncherUpdate();

        clock.Expire();                  // the normal budget expires mid-swap
        await WaitForWaits(clock, 2);    // budget, then the update grace
        Assert.False(run.IsCompleted);   // the swap keeps its own window

        clock.Expire();                  // the swap stalled past the grace too
        Assert.Equal(StartupOutcome.LauncherUpdate, await Settle(run));
    }

    [Fact]
    public async Task A_self_update_that_ends_the_process_never_returns_but_still_cannot_hang_forever()
    {
        // The real path calls Environment.Exit inside the work, so RunAsync never returns. This proves
        // the fallback: if the swap stalls, the grace expires and the caller gets an outcome, closes
        // the splash and shows the shell. A frozen frame is not one of the possible endings.
        var work = new ScriptedWork();
        var clock = new ManualClock();
        var vm = NewSplash(work, clock);

        var run = vm.RunAsync(CancellationToken.None);
        work.AnnounceLauncherUpdate();
        clock.Expire();
        await WaitForWaits(clock, 2);    // the grace has been requested
        clock.Expire();

        Assert.Equal(StartupOutcome.LauncherUpdate, await Settle(run));
        Assert.True(vm.IsUpdatingLauncher);
    }

    [Fact]
    public async Task The_starting_line_comes_from_the_catalogue_too()
    {
        using var _ = Loc.OverrideForTests("Splash_Status_Starting", "LIGHTING THE LANTERN");

        var work = new ScriptedWork();
        var clock = new ManualClock();
        var vm = NewSplash(work, clock);
        Assert.Equal("LIGHTING THE LANTERN", vm.Status);

        var run = vm.RunAsync(CancellationToken.None);
        work.Finish();
        await ReleaseFloor(clock);
        await Settle(run);
    }

    // ── The adapter that turns the launcher's own state into that signal ─────────────────────────

    private sealed partial class FakePlay : ObservableObject
    {
        [ObservableProperty]
        private string _state = "Initializing";
    }

    [Fact]
    public void The_adapter_announces_the_self_update_once_the_launcher_state_says_so()
    {
        var play = new FakePlay();
        var announced = 0;
        using var work = new ShellStartupWork(
            () => Task.CompletedTask, play, nameof(FakePlay.State),
            () => play.State == "UpdatingLauncher");
        work.LauncherUpdateStarted += (_, _) => announced++;

        play.State = "NoClient";
        Assert.Equal(0, announced);      // any other state is not an update

        play.State = "UpdatingLauncher";
        Assert.Equal(1, announced);

        // Idempotent: a re-notification must not re-announce, or the splash would flip its wording
        // back and forth while the swap runs.
        play.State = "Initializing";
        play.State = "UpdatingLauncher";
        Assert.Equal(1, announced);
    }

    [Fact]
    public void The_adapter_stops_listening_when_it_is_disposed()
    {
        var play = new FakePlay();
        var announced = 0;
        var work = new ShellStartupWork(
            () => Task.CompletedTask, play, nameof(FakePlay.State),
            () => play.State == "UpdatingLauncher");
        work.LauncherUpdateStarted += (_, _) => announced++;

        work.Dispose();
        play.State = "UpdatingLauncher";

        Assert.Equal(0, announced);
    }

    // ── Announcing BEFORE the download, which is the whole point ─────────────────────────────────

    /// <summary>THE regression this pair of tests exists for. Watching only the launcher state meant
    /// the splash heard about the update AFTER the download: measured 2026-08-02 in the win11 VM, the
    /// 61 MB download took 14 s, the twelve-second budget expired first, and the shell appeared two
    /// seconds before the process exited. A window that flashes up and vanishes reads as a crash — the
    /// exact impression the start screen exists to prevent, with its ninety-second update grace never
    /// getting a chance to apply.</summary>
    [Fact]
    public void The_adapter_announces_as_soon_as_the_update_service_commits_before_any_download()
    {
        var play = new FakePlay();
        var update = new FakeUpdateService();
        var announced = 0;
        using var work = new ShellStartupWork(
            () => Task.CompletedTask, play, nameof(FakePlay.State),
            () => play.State == "UpdatingLauncher", update);
        work.LauncherUpdateStarted += (_, _) => announced++;

        update.RaiseStarting();

        // The launcher state has not moved at all — that only happens after the swap is handed off.
        Assert.Equal("Initializing", play.State);
        Assert.Equal(1, announced);
    }

    /// <summary>Both sources feed one announcement. The state stays observed as a fallback, so a path
    /// that reaches the swap without raising the event still reaches the splash — but a player must
    /// never see the wording flip because both fired.</summary>
    [Fact]
    public void Both_sources_together_still_announce_exactly_once()
    {
        var play = new FakePlay();
        var update = new FakeUpdateService();
        var announced = 0;
        using var work = new ShellStartupWork(
            () => Task.CompletedTask, play, nameof(FakePlay.State),
            () => play.State == "UpdatingLauncher", update);
        work.LauncherUpdateStarted += (_, _) => announced++;

        update.RaiseStarting();
        play.State = "UpdatingLauncher";
        update.RaiseStarting();

        Assert.Equal(1, announced);
    }

    /// <summary>The state fallback must still work on its own, for any path that swaps without the
    /// event — otherwise this change would trade one blind spot for another.</summary>
    [Fact]
    public void The_state_fallback_still_announces_when_the_event_never_comes()
    {
        var play = new FakePlay();
        var update = new FakeUpdateService();
        var announced = 0;
        using var work = new ShellStartupWork(
            () => Task.CompletedTask, play, nameof(FakePlay.State),
            () => play.State == "UpdatingLauncher", update);
        work.LauncherUpdateStarted += (_, _) => announced++;

        play.State = "UpdatingLauncher";

        Assert.Equal(1, announced);
    }

    [Fact]
    public void Disposing_also_unhooks_the_update_service()
    {
        var update = new FakeUpdateService();
        var announced = 0;
        var work = new ShellStartupWork(
            () => Task.CompletedTask, new FakePlay(), nameof(FakePlay.State), () => false, update);
        work.LauncherUpdateStarted += (_, _) => announced++;

        work.Dispose();
        update.RaiseStarting();

        Assert.Equal(0, announced);
    }

    private sealed class FakeUpdateService : WowLauncher.Services.IUpdateService
    {
        public event EventHandler? LauncherUpdateStarting;

        public void RaiseStarting() => LauncherUpdateStarting?.Invoke(this, EventArgs.Empty);

        public Task<bool> CheckAndApplyAsync(WowLauncher.Models.ServerManifest? manifest,
            CancellationToken ct = default) => Task.FromResult(false);

        public WowLauncher.Services.LauncherUpdateNotice? CheckForNotice(
            WowLauncher.Models.ServerManifest? manifest) => null;
    }
}
