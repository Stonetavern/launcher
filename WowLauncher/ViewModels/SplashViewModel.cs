using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using WowLauncher.Localization;

namespace WowLauncher.ViewModels;

/// <summary>How the start screen ended. Every one of these closes the splash — that is the point.</summary>
public enum StartupOutcome
{
    /// <summary>The startup work ran to completion. The shell takes over.</summary>
    Completed,

    /// <summary>The startup work threw. The shell takes over anyway: it renders its own error state,
    /// and a splash is the worst possible place to strand a player.</summary>
    Failed,

    /// <summary>The startup work was still running when the budget ran out. It keeps running in the
    /// background and the shell takes over — which is exactly what the launcher did before this screen
    /// existed, so the fallback is the previous behaviour, not a new failure mode.</summary>
    TimedOut,

    /// <summary>The token was cancelled (the player dismissed the splash).</summary>
    Cancelled,

    /// <summary>A launcher self-update was announced and the process did not exit within the swap
    /// window. Only reachable if the swap stalled; the normal path never returns at all.</summary>
    LauncherUpdate,
}

/// <summary>
/// The startup work the splash waits for, behind an interface so the state machine can be tested
/// without a network, a manifest, or a window.
/// </summary>
public interface IStartupWork
{
    /// <summary>Raised once the startup decided to replace the launcher binary. After this the process
    /// is expected to end so a helper can swap the file, so <see cref="RunAsync"/> normally never
    /// returns.</summary>
    event EventHandler? LauncherUpdateStarted;

    Task RunAsync(CancellationToken ct);
}

/// <summary>
/// The start screen: the lantern, a quiet wick, and one line that says what is happening.
///
/// <para><b>The one rule.</b> The splash must never outlive its work. <see cref="RunAsync"/> has no
/// path that leaves it standing: success, failure, cancellation and "still running after the budget"
/// all return an outcome, and the caller closes the window in a <c>finally</c>. The launcher had an
/// endless update loop on 2026-08-01; a start screen that can hang is the same wound in a new place.</para>
///
/// <para><b>A minimum display time of five seconds</b> (owner directive, 2026-08-05). This file argued
/// the opposite until then: a floor spends the player's time to make the developer comfortable. The
/// counter-argument that decided it is that a window which appears and vanishes inside a second reads
/// as a crash, and the launcher has already been mistaken for one — measured 2026-08-02, the shell
/// flashed up two seconds before the process exited during a swap. The floor is a floor, never an
/// addition: work that takes longer than five seconds waits for nothing extra.</para>
///
/// <para>It applies only where the splash hands over to a working shell — completion and failure.
/// A player who dismisses the screen gets it closed at once (holding a window someone just clicked
/// away is worse than a flash), and the budget and update paths are already past five seconds.</para>
///
/// <para><b>Nothing here blocks the start.</b> News, realm status and the addon catalogue are already
/// fire and forget inside the startup work. The budget below covers everything else: whatever is still
/// pending when it expires keeps running while the shell comes up.</para>
/// </summary>
public sealed partial class SplashViewModel : ViewModelBase
{
    /// <summary>How long the splash waits before handing over to the shell regardless. Twelve seconds
    /// is roughly four times a healthy cold start over a slow connection; past that the honest thing is
    /// a working launcher with a stale realm dot, not a stone frame with a lantern on it.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(12);

    /// <summary>Extra grace once a self-update was announced. The swap downloads, verifies, hands off
    /// to the helper and ends the process; that is allowed to outlast the normal budget, because
    /// dropping into a shell whose binary is being exchanged underneath it is worse than waiting.</summary>
    public static readonly TimeSpan DefaultUpdateGrace = TimeSpan.FromSeconds(90);

    /// <summary>How long the start screen stays up at minimum, even when there was nothing left to wait
    /// for. Owner directive 2026-08-05. Below the twelve-second budget by construction: a floor above
    /// the ceiling would hold every player for the full budget on every start.</summary>
    public static readonly TimeSpan DefaultMinimumDisplay = TimeSpan.FromSeconds(5);

    private readonly IStartupWork _work;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<TimeSpan> _elapsed;
    private readonly Action<Action> _post;
    private readonly TimeSpan _budget;
    private readonly TimeSpan _updateGrace;
    private readonly TimeSpan _minimumDisplay;

    private bool _updateAnnounced;

    public SplashViewModel(
        IStartupWork work,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<Action>? post = null,
        TimeSpan? budget = null,
        TimeSpan? updateGrace = null,
        TimeSpan? minimumDisplay = null,
        Func<TimeSpan>? elapsed = null)
    {
        _work = work;
        _delay = delay ?? QuietDelayAsync;
        // Monotonic by default: a wall clock that jumps backwards mid-start would make the floor
        // unbounded, and a start screen that can hang is the one thing this class exists to prevent.
        var started = System.Diagnostics.Stopwatch.StartNew();
        _elapsed = elapsed ?? (() => started.Elapsed);
        // The update announcement can in principle arrive from whatever thread raised it, and it lands
        // on a bound property. Default: marshal. Tests pass a straight-through post.
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        _budget = budget ?? DefaultBudget;
        _updateGrace = updateGrace ?? DefaultUpdateGrace;
        _minimumDisplay = minimumDisplay ?? DefaultMinimumDisplay;
    }

    /// <summary>The single line under the wick. Starts as "starting", becomes the update wording the
    /// moment the launcher decides to replace itself.</summary>
    [ObservableProperty]
    private string _status = Loc.T("Splash_Status_Starting");

    /// <summary>True once a self-update was announced. Drives the emphasis in the markup: the update
    /// sentence is the one message on this screen a player must not mistake for a crash.</summary>
    [ObservableProperty]
    private bool _isUpdatingLauncher;

    /// <summary>The running product version, same number the self-update compares. It is on the splash
    /// because "which version am I actually on" was unanswerable during the update loop.</summary>
    public string VersionText
    {
        get
        {
            var v = Services.UpdateService.RunningVersion(System.Reflection.Assembly.GetExecutingAssembly());
            return $"v{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }

    /// <summary>The outcome of the last <see cref="RunAsync"/>, for callers that cannot hold the result.</summary>
    public StartupOutcome Outcome { get; private set; } = StartupOutcome.Completed;

    /// <summary>
    /// Run the startup work and report how it ended. Never throws: the caller closes the splash on the
    /// returned outcome, and an exception escaping here would be the one way to leave it standing.
    /// </summary>
    public async Task<StartupOutcome> RunAsync(CancellationToken ct)
    {
        _work.LauncherUpdateStarted += OnLauncherUpdateStarted;
        try
        {
            Task work;
            try
            {
                work = _work.RunAsync(ct);
            }
            catch (Exception ex)
            {
                // A synchronous throw before the first await — still an outcome, never a hang.
                Serilog.Log.Error(ex, "Startup work failed before it started");
                return Outcome = StartupOutcome.Failed;
            }

            var finished = await Task.WhenAny(work, _delay(_budget, ct)).ConfigureAwait(true);

            if (!ReferenceEquals(finished, work) && _updateAnnounced)
            {
                // The binary is being replaced. Give the swap its own window instead of racing it.
                finished = await Task.WhenAny(work, _delay(_updateGrace, ct)).ConfigureAwait(true);
            }

            if (!ReferenceEquals(finished, work))
            {
                // Hand over, but do not lose the work: if it faults later nobody is awaiting it.
                Observe(work);
                if (ct.IsCancellationRequested) return Outcome = StartupOutcome.Cancelled;
                return Outcome = _updateAnnounced ? StartupOutcome.LauncherUpdate : StartupOutcome.TimedOut;
            }

            StartupOutcome outcome;
            try
            {
                await work.ConfigureAwait(true);
                outcome = StartupOutcome.Completed;
            }
            catch (OperationCanceledException)
            {
                // Dismissed, not finished: close now. The floor is for starts, not for aborts.
                return Outcome = StartupOutcome.Cancelled;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Startup work failed - continuing into the shell");
                outcome = StartupOutcome.Failed;
            }

            await HoldForMinimumDisplayAsync(ct).ConfigureAwait(true);
            // The hold is interruptible: a player who dismissed the screen during it gets that answer.
            if (ct.IsCancellationRequested) return Outcome = StartupOutcome.Cancelled;
            return Outcome = outcome;
        }
        finally
        {
            _work.LauncherUpdateStarted -= OnLauncherUpdateStarted;
        }
    }

    /// <summary>Wait out whatever is left of the minimum display time. Returns at once when the screen
    /// has already been up that long, which is the normal case on a cold start over a real connection.
    /// Never waits longer than the floor itself: the remaining time is computed, not assumed.</summary>
    private async Task HoldForMinimumDisplayAsync(CancellationToken ct)
    {
        var remaining = _minimumDisplay - _elapsed();
        if (remaining <= TimeSpan.Zero) return;
        await _delay(remaining, ct).ConfigureAwait(true);
    }

    private void OnLauncherUpdateStarted(object? sender, EventArgs e)
    {
        _updateAnnounced = true;
        _post(() =>
        {
            IsUpdatingLauncher = true;
            Status = Loc.T("Splash_Status_UpdatingLauncher");
        });
    }

    /// <summary>Cancellation is how this race ends, not an error — never let it surface as a fault.</summary>
    private static async Task QuietDelayAsync(TimeSpan duration, CancellationToken ct)
    {
        try { await Task.Delay(duration, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* the splash is going away anyway */ }
    }

    /// <summary>Keep an abandoned startup task from surfacing as an unobserved exception.</summary>
    private static void Observe(Task work) =>
        _ = work.ContinueWith(
            t => Serilog.Log.Warning(t.Exception, "Startup work failed after the splash handed over"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
