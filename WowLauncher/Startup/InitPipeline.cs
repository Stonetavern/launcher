using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WowLauncher.Startup;

/// <summary>Where one step of the init pipeline stands. Every step ends in Done or Failed: a step
/// that throws is Failed, never "still running", so the login gate can never hang on it.</summary>
public enum InitStepState
{
    Pending,
    Running,
    Done,
    Failed,
}

/// <summary>What the pipeline tells its listener each time a step changes state.</summary>
/// <param name="Key">Stable machine key of the step (config, update, realm, cdn, installs).</param>
/// <param name="State">The new state.</param>
/// <param name="StatusText">The player-facing line for the status row while the step runs. Already
/// localized (English first, VOICE.md), so the ViewModel shows it as is.</param>
/// <param name="Detail">Optional result the step wants the surface to know (e.g. the realm answered),
/// or the failure reason for the log. Never shown raw to the player.</param>
public sealed record InitStepReport(string Key, InitStepState State, string StatusText, string? Detail);

/// <summary>
/// One step of the startup pipeline (Spec 2026-09-20 §5.2). Pure description: what it is called,
/// what the status row says while it runs, whether the login button waits for it, and the work.
/// </summary>
public sealed class InitStep
{
    public InitStep(string key, string statusText, bool gatesLogin, Func<CancellationToken, Task<string?>> run,
                    TimeSpan? budget = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(run);
        Key = key;
        StatusText = statusText;
        GatesLogin = gatesLogin;
        Run = run;
        Budget = budget;
    }

    /// <summary>How long the step may take before it counts as Failed ("budget"). Null: no limit.
    /// Exists because the HTTP layer retries four times with 2/4/8 s backoff: with the download
    /// origin down, an unbudgeted update check would hold the login gate closed for ~14 s
    /// (measured 2026-09-20 against a refused port). The splash had a 12 s budget for the same work.</summary>
    public TimeSpan? Budget { get; }

    public string Key { get; }

    /// <summary>The status-row sentence while this step runs ("Connecting to the realm").</summary>
    public string StatusText { get; }

    /// <summary>§5.2: the login button is active only once steps 1 to 3 have COMPLETED. Completed
    /// means Done or Failed: an unreachable realm is reported, it does not lock the player out.</summary>
    public bool GatesLogin { get; }

    /// <summary>The work. Returns an optional detail string; throws to mark the step Failed.</summary>
    public Func<CancellationToken, Task<string?>> Run { get; }
}

/// <summary>
/// The startup pipeline behind the login screen (Spec §5.2): steps run one after another, each one
/// announces itself on the status row, none of them blocks the input fields, and the login gate
/// opens the moment the gating steps are through.
///
/// <para><b>Never blocks, never throws.</b> A failing step is recorded as Failed with its reason and
/// the pipeline moves on. The only way out of <see cref="RunAsync"/> before the last step is the
/// token, and even that is reported (Cancelled) rather than thrown, because the caller is the UI and
/// an exception there is a crash log with a player behind it.</para>
///
/// <para><b>Halt.</b> A step may decide the launcher must stop (the self-update swap is the one case:
/// the binary is about to be replaced). It signals that through <see cref="RequestHalt"/>; the
/// pipeline then reports the halt and runs nothing further. Whoever owns the process ends it.</para>
///
/// <para>Thread-neutral: <see cref="StepChanged"/> fires on whatever thread the step completed on.
/// The ViewModel marshals to the UI thread; this class knows no dispatcher.</para>
/// </summary>
public sealed class InitPipeline
{
    private readonly IReadOnlyList<InitStep> _steps;
    private readonly Dictionary<string, InitStepState> _states;
    private readonly object _gate = new();
    private bool _halted;
    private string? _haltText;

    public InitPipeline(IReadOnlyList<InitStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0) throw new ArgumentException("A pipeline needs at least one step.", nameof(steps));
        if (steps.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() != steps.Count)
            throw new ArgumentException("Step keys must be unique.", nameof(steps));

        _steps = steps;
        _states = steps.ToDictionary(s => s.Key, _ => InitStepState.Pending, StringComparer.Ordinal);
    }

    public IReadOnlyList<InitStep> Steps => _steps;

    /// <summary>Raised for every state change of every step, in order. See the class remarks for the
    /// threading contract.</summary>
    public event Action<InitStepReport>? StepChanged;

    /// <summary>Raised once, when a step asked the launcher to stop (self-update swap). Carries the
    /// sentence the surface should leave on screen.</summary>
    public event Action<string>? HaltRequested;

    /// <summary>True once every step marked <see cref="InitStep.GatesLogin"/> is Done or Failed.</summary>
    public bool IsLoginGateOpen
    {
        get
        {
            lock (_gate)
                return _steps.Where(s => s.GatesLogin)
                             .All(s => _states[s.Key] is InitStepState.Done or InitStepState.Failed);
        }
    }

    /// <summary>True once every step is Done or Failed (or the pipeline halted).</summary>
    public bool IsComplete
    {
        get
        {
            lock (_gate)
                return _halted || _states.Values.All(s => s is InitStepState.Done or InitStepState.Failed);
        }
    }

    public bool IsHalted { get { lock (_gate) return _halted; } }

    public InitStepState StateOf(string key)
    {
        lock (_gate) return _states[key];
    }

    /// <summary>For a step's work: stop the pipeline after this step. Idempotent.</summary>
    public void RequestHalt(string statusText)
    {
        lock (_gate)
        {
            if (_halted) return;
            _halted = true;
            _haltText = statusText;
        }
    }

    /// <summary>
    /// Run every step in order. Completes when the last step completed, the token was cancelled, or a
    /// step requested a halt. Never throws.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        foreach (var step in _steps)
        {
            if (ct.IsCancellationRequested) return;
            if (IsHalted) break;

            Set(step, InitStepState.Running, null);

            string? detail;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (step.Budget is { } limit) budget.CancelAfter(limit);
            try
            {
                detail = await step.Run(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller went away (window closed, player aborted). Not a failure of the step.
                return;
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                // Over budget: a Failed step, so the gate opens and the surface reports it.
                Serilog.Log.Warning("Init step {Step} exceeded its budget of {Budget}", step.Key, step.Budget);
                Set(step, InitStepState.Failed, "budget");
                continue;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Init step {Step} failed", step.Key);
                Set(step, InitStepState.Failed, ex.Message);
                continue;
            }

            Set(step, InitStepState.Done, detail);
        }

        string? halt;
        lock (_gate) halt = _halted ? _haltText : null;
        if (halt is not null) HaltRequested?.Invoke(halt);
    }

    private void Set(InitStep step, InitStepState state, string? detail)
    {
        lock (_gate) _states[step.Key] = state;
        // One line per state change: the support answer to "what was the launcher doing when it
        // sat there" is this trail.
        Serilog.Log.Information("Init step {Step}: {State}{Detail}", step.Key, state,
            detail is null ? "" : $" ({detail})");
        StepChanged?.Invoke(new InitStepReport(step.Key, state, step.StatusText, detail));
    }
}
