namespace WowLauncher.Services.Platform;

using System.Diagnostics;

/// <summary>
/// Decides whether the launcher may exit after a <em>started</em> client. On Windows the answer is
/// "immediately" — that is the shipped, byte-for-byte behaviour (<c>Environment.Exit(0)</c> right
/// after <see cref="GameLaunchResult.Started"/>). On Linux a successful <c>wine</c> start is NOT proof
/// the client runs — wine frequently starts and dies instantly (PLAN §4) — so the launcher must hold a
/// short grace window and confirm a live client process before exiting; otherwise it reports a launch
/// failure instead of vanishing on a game that never came up.
/// </summary>
public interface ILaunchExitPolicy
{
    /// <summary>True ⇒ the launcher may exit now (client confirmed / no confirmation needed).
    /// False ⇒ the start did not result in a running client; the caller surfaces a launch failure.
    /// <paramref name="expectedExePath"/> is the absolute WoW.exe path just launched.</summary>
    Task<bool> ConfirmClientRunningAsync(string expectedExePath);
}

/// <summary>
/// Windows (and any platform whose start is synchronous and authoritative): exit immediately. This
/// preserves the exact shipped behaviour — the launcher process ends the moment the client is started
/// and never inspects a process list.
/// </summary>
public sealed class ImmediateLaunchExitPolicy : ILaunchExitPolicy
{
    public Task<bool> ConfirmClientRunningAsync(string expectedExePath) => Task.FromResult(true);
}

/// <summary>
/// Linux/Wine: confirm the client is <em>stably</em> running before letting the launcher exit. wine
/// forks — the real game process appears a beat after <c>Process.Start</c> returns — but a wine start
/// can also spawn a process that dies within a second (crash on load, missing DLL, bad config). A first
/// positive scan is therefore NOT proof: the guard polls across the whole grace window and only allows
/// the exit if the client is <b>still alive at the end of the window</b> (final authoritative scan
/// positive). A false negative here is safe (the launcher surfaces a launch failure); the danger it
/// closes is a false <em>exit</em> on a client that appeared and then died inside the window.
/// </summary>
public sealed class GraceWindowLaunchExitPolicy : ILaunchExitPolicy
{
    private readonly IGameProcessDetector _detector;
    private readonly Serilog.ILogger _logger;
    private readonly TimeSpan _window;
    private readonly TimeSpan _interval;

    public GraceWindowLaunchExitPolicy(
        IGameProcessDetector detector,
        Serilog.ILogger logger,
        TimeSpan? window = null,
        TimeSpan? interval = null)
    {
        _detector = detector;
        _logger = logger;
        _window = window ?? TimeSpan.FromSeconds(10);
        _interval = interval ?? TimeSpan.FromMilliseconds(500);
    }

    /// <summary>Semantics: poll until the grace window elapses, then take one final authoritative scan.
    /// Return <c>true</c> only if that last scan is positive — i.e. the client must be alive at the END
    /// of the window, not merely to have appeared once. This defeats the "appears then dies inside the
    /// window" false-exit (a start success is not a stability proof).</summary>
    public async Task<bool> ConfirmClientRunningAsync(string expectedExePath)
    {
        var sw = Stopwatch.StartNew();

        // Poll for the whole window so a short-lived process cannot short-circuit the decision. We do
        // not early-return on a first sighting: the client has to survive to the window's end.
        while (sw.Elapsed < _window)
        {
            var seen = _detector.IsGameRunning(expectedExePath);
            var remaining = _window - sw.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            _ = seen; // interim scans keep the detector warm; only the final scan is authoritative.
            await Task.Delay(remaining < _interval ? remaining : _interval).ConfigureAwait(false);
        }

        // Final authoritative scan at the end of the grace window.
        var alive = _detector.IsGameRunning(expectedExePath);
        if (alive)
            _logger.Information(
                "Client process confirmed at the end of the {Window:F0}s grace window: the launcher may exit",
                _window.TotalSeconds);
        else
            _logger.Warning(
                "Wine started but no client process is running at the end of the {Window:F0}s grace window: the launch counts as failed",
                _window.TotalSeconds);
        return alive;
    }
}
