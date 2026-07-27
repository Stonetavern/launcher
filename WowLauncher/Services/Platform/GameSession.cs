namespace WowLauncher.Services.Platform;

/// <summary>
/// The one place that owns a launched game's out-of-process tail: the realm proxy the modern
/// (1.14.2) launcher started, and the wait for the client to quit so that proxy can be reaped.
///
/// <para><b>Why this exists (proxy-lifetime reversal, supersedes the 2026-07-22 detach decision).</b>
/// The modern launcher used to leave HermesProxy running and reap it at the START of the NEXT session
/// from its pidfile, because the launcher exited right after a confirmed launch and a watchdog would
/// have died with it. Now the launcher stays alive in the system tray after Play, so it CAN watch the
/// game end and stop the proxy itself — the same thing <c>Play Stonetavern.sh</c> does at its end
/// (<c>pkill -x HermesProxy</c>). The pidfile self-heal in <see cref="HermesProxyRunner"/> stays as the
/// crash-only safety net (a launcher killed mid-session cannot run any cleanup); it never competes with
/// this path, because a clean stop deletes the pidfile.</para>
///
/// <para><b>The invariant this type enforces:</b> the proxy is NEVER stopped while a client is still
/// running — that would cut the connection of a player mid-game. Every stop path here first proves the
/// game process is gone (<see cref="MonitorUntilExitAsync"/> only reaps after the poll sees it gone;
/// <see cref="StopProxyIfNoGameAsync"/> refuses while a client is detected).</para>
///
/// <para>The 1.12.1 client speaks straight to the realm and has no proxy, so nothing is ever attached
/// for it: <see cref="MonitorUntilExitAsync"/> then just waits and returns, and the caller brings the
/// launcher back from the tray all the same.</para>
/// </summary>
public interface IGameSession
{
    /// <summary>Hand the session the proxy the current launch started, so it can be stopped when the
    /// game ends. Only the modern (1.14.2) launcher calls this; 1.12.1 never does.</summary>
    void AttachProxy(IGameProxy proxy);

    /// <summary>True while a proxy is attached (a modern client was launched and has not been reaped).</summary>
    bool HasProxy { get; }

    /// <summary>Stop and detach the proxy, but ONLY if no client is currently running — used by the
    /// tray Quit so a deliberate shutdown never kills a playing user's connection. Returns true iff a
    /// proxy was actually stopped. No-op (false) when a game is still up or nothing is attached.
    /// <paramref name="clientExePath"/> is the client exe when known; null asks "is ANY client running".</summary>
    Task<bool> StopProxyIfNoGameAsync(string? clientExePath = null);

    /// <summary>Poll until the client at <paramref name="clientExePath"/> is gone, then stop the attached
    /// proxy (if any). The proxy is never stopped before the poll confirms the process is gone. Safe with
    /// no proxy attached (1.12.1): it simply waits out the game and returns.</summary>
    Task MonitorUntilExitAsync(string clientExePath, CancellationToken ct = default);

    /// <summary>
    /// Bind the session's liveness to a CONCRETE client process (its PID), so "is the game still up" is
    /// answered by that exact process rather than a process-name scan a same-named foreign process could
    /// satisfy (Codex review: liveness ≠ ownership). Once bound, both the reap watchdog
    /// (<see cref="MonitorUntilExitAsync"/>) and the tray-quit guard (<see cref="StopProxyIfNoGameAsync"/>)
    /// judge the game by this PID; the name-scan stays only as the fallback for the no-PID case.
    ///
    /// <para>Optional by design: only the native-Windows modern launcher gets the client PID (it starts
    /// the client itself, the way <c>Play Stonetavern.cmd</c> <c>start /wait</c>s on it). The Linux modern
    /// launcher starts the client through Arctium and never sees its PID, so it never binds and keeps the
    /// name-scan. A default no-op keeps every existing implementer valid.</para>
    /// </summary>
    void BindClientProcess(int pid) { }
}

/// <inheritdoc cref="IGameSession"/>
public sealed class GameSession : IGameSession
{
    private readonly IGameProcessDetector _detector;
    private readonly Serilog.ILogger _logger;
    private readonly TimeSpan _appearTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<int, bool> _isPidAlive;
    private readonly object _gate = new();
    private IGameProxy? _proxy;
    private int? _clientPid;

    public GameSession(IGameProcessDetector detector, Serilog.ILogger logger)
        : this(detector, logger, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2),
            static (d, ct) => Task.Delay(d, ct))
    {
    }

    /// <summary>Test seam: an injected delay + short windows so the appear/exit polling LOGIC can be
    /// proven without real processes or real wall-clock waits. <paramref name="isPidAlive"/> is the
    /// bound-PID liveness check (defaults to a real process lookup) so the PID-authoritative reap logic
    /// can be proven without a real Windows process.</summary>
    internal GameSession(IGameProcessDetector detector, Serilog.ILogger logger,
        TimeSpan appearTimeout, TimeSpan pollInterval, Func<TimeSpan, CancellationToken, Task> delay,
        Func<int, bool>? isPidAlive = null)
    {
        _detector = detector;
        _logger = logger;
        _appearTimeout = appearTimeout;
        _pollInterval = pollInterval;
        _delay = delay;
        _isPidAlive = isPidAlive ?? DefaultIsPidAlive;
    }

    public bool HasProxy { get { lock (_gate) { return _proxy is not null; } } }

    public void AttachProxy(IGameProxy proxy)
    {
        lock (_gate) { _proxy = proxy; }
        _logger.Information("Game session: proxy attached — the launcher now owns its shutdown");
    }

    public void BindClientProcess(int pid)
    {
        lock (_gate) { _clientPid = pid; }
        _logger.Information("Game session: liveness bound to client PID {Pid} (ownership, not a name-scan)", pid);
    }

    /// <summary>Detach and return the current proxy in one atomic step, so two callers (the exit
    /// watchdog and a tray Quit) can never both stop the same instance. Also clears the bound client PID:
    /// the session that owned it is ending.</summary>
    private IGameProxy? TakeProxy()
    {
        lock (_gate) { var p = _proxy; _proxy = null; _clientPid = null; return p; }
    }

    /// <summary>Is the game still up? When a concrete client PID is bound, THAT process is the
    /// authoritative answer (a name-scan a same-named foreign process could satisfy would either reap too
    /// early or hold the proxy too long — Codex review). The name-scan is the fallback only when no PID is
    /// bound (1.12.1, or the Linux Arctium path that never sees the client PID).</summary>
    private bool ClientIsAlive(string? clientExePath)
    {
        int? pid;
        lock (_gate) { pid = _clientPid; }
        return pid is int p ? _isPidAlive(p) : _detector.IsGameRunning(clientExePath);
    }

    /// <summary>Default bound-PID liveness: the process exists and has not exited. A recycled/absent PID
    /// reads as dead. Best-effort — never throws.</summary>
    private static bool DefaultIsPidAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no process with that PID — gone
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> StopProxyIfNoGameAsync(string? clientExePath = null)
    {
        // The invariant, at the deliberate-shutdown entry point: a client still up keeps its proxy.
        if (ClientIsAlive(clientExePath))
        {
            _logger.Information("Quit: a client is still running — leaving the realm proxy alone");
            return false;
        }

        var proxy = TakeProxy();
        if (proxy is null) return false;

        _logger.Information("Quit: no client running — stopping the realm proxy");
        await proxy.StopAsync().ConfigureAwait(false);
        return true;
    }

    public async Task MonitorUntilExitAsync(string clientExePath, CancellationToken ct = default)
    {
        try
        {
            // Phase 1 — wait (bounded) for the client to actually be visible as a process. The launcher
            // may already have hidden itself before the OS shows the process (Windows' immediate exit
            // policy confirms without scanning), and without this the "has it ended?" loop below would
            // conclude "already gone" on its very first poll and reap a proxy the game still needs.
            var appeared = await WaitUntilAsync(() => ClientIsAlive(clientExePath), _appearTimeout, ct)
                .ConfigureAwait(false);
            if (!appeared)
                _logger.Warning(
                    "Game process never became visible within {T:F0}s — treating the session as already ended",
                    _appearTimeout.TotalSeconds);
            else
                // Phase 2 — wait until it is gone. Only here does a reap become allowed.
                while (!ct.IsCancellationRequested && ClientIsAlive(clientExePath))
                    await _delay(_pollInterval, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // cancelled: leave the proxy exactly as it is, do not reap on a torn-down wait
        }

        if (ct.IsCancellationRequested) return;

        var proxy = TakeProxy();
        if (proxy is null)
        {
            _logger.Information("Game ended — no proxy to stop (1.12.1 or a build without one)");
            return;
        }

        _logger.Information("Game ended — stopping the realm proxy");
        await proxy.StopAsync().ConfigureAwait(false);
    }

    /// <summary>Poll <paramref name="predicate"/> until it is true or <paramref name="timeout"/> elapses;
    /// returns whether it became true. A true on the first check returns immediately.</summary>
    private async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (predicate()) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await _delay(_pollInterval, ct).ConfigureAwait(false);
        }
    }
}
