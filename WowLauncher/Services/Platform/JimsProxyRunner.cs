namespace WowLauncher.Services.Platform;

using System.Diagnostics;
using System.Net.Sockets;

/// <summary>
/// Native-Windows realm proxy runner: starts <c>JimsProxy.exe</c> (the Windows HermesProxy fork,
/// version 5.1.8) directly, proves it is actually listening before control passes to the client, and
/// reaps it without ever outliving the session. It is the Windows counterpart of
/// <see cref="HermesProxyRunner"/> — same lifecycle contract (<see cref="IGameProxy"/>), same
/// prove-listening and pidfile self-heal machinery — but with two deliberate differences that make it a
/// separate class rather than a reuse of the Linux runner:
///
/// <list type="number">
/// <item><b>No Wine, no ELF, no <c>Hermes/linux</c>.</b> Windows runs the native <c>JimsProxy.exe</c>
/// straight, exactly the way <c>Play Stonetavern.cmd</c> does (the one path a Windows character has
/// reached the world through).</item>
/// <item><b>A Windows-shaped "graceful" stop.</b> Windows has no SIGTERM, so the Linux
/// <c>pkill</c>/SIGTERM path does not apply. JimsProxy is a console process; a clean stop first tries to
/// close its window (<see cref="Process.CloseMainWindow"/> delivers <c>WM_CLOSE</c> so the proxy can
/// drain its sockets), and only escalates to <see cref="Process.Kill(bool)"/> when it does not exit
/// within the grace window. When the proxy has no window to close (started with redirected output), the
/// graceful attempt reports "not delivered" and the hard kill runs immediately — which is exactly the
/// shipped <c>taskkill /F</c> behaviour of the batch file, so nothing regresses. The invariant this
/// upholds (never stop the proxy while the client still runs) is enforced one level up by
/// <see cref="IGameSession"/>, so by the time a stop reaches here the player is already gone and the
/// cost of the grace window is nil.</item>
/// </list>
///
/// <para><b>Testability.</b> The TCP readiness probe and the two stop steps (graceful, hard) are
/// injectable seams, so the polling/timeout/early-exit/escalation LOGIC is proven on any OS with a real
/// child process (a plain <c>sleep</c>) and a fake probe — no JimsProxy binary and no Windows host
/// required for the unit tests. The real graceful step (window close) only does anything on Windows with
/// a real console window, which is E2E-only.</para>
/// </summary>
public sealed class JimsProxyRunner : IGameProxy
{
    private readonly Serilog.ILogger _logger;
    private readonly string _exePath;
    private readonly IReadOnlyList<string> _args;
    private readonly string _pidFilePath;
    private readonly Func<int, CancellationToken, Task<bool>> _portProbe;
    private readonly Func<int, CancellationToken, Task<int?>> _portOwnerProbe; // owning PID of the listener, or null if unknown
    private readonly Func<Process, bool> _gracefulStop; // WM_CLOSE; true when the close was delivered
    private readonly Action<Process> _hardKill;         // Kill fallback (own seam so a test can observe it)
    private readonly TimeSpan _stopGrace;               // how long to wait for a clean exit before escalating

    private Process? _process;

    public JimsProxyRunner(Serilog.ILogger logger, string exePath, IReadOnlyList<string> args, string pidFilePath)
        : this(logger, exePath, args, pidFilePath, TcpPortProbeAsync)
    {
    }

    /// <summary>Test seam: a fake "is the port open" probe stands in for a real TCP connect attempt, so
    /// the polling/timeout/early-exit logic can be proven without a real listening process.
    /// <paramref name="portOwnerProbe"/> reports which PID owns the listener on the port (default: real
    /// <c>netstat -ano</c> parsing on Windows, null everywhere else) so the port-OWNERSHIP proof is
    /// testable on Fedora. The graceful-stop seams default to the real Windows behaviour and are only
    /// overridden in tests that prove the graceful close is tried before the hard kill.</summary>
    internal JimsProxyRunner(Serilog.ILogger logger, string exePath, IReadOnlyList<string> args,
        string pidFilePath, Func<int, CancellationToken, Task<bool>> portProbe,
        Func<Process, bool>? gracefulStop = null, Action<Process>? hardKill = null, TimeSpan? stopGrace = null,
        Func<int, CancellationToken, Task<int?>>? portOwnerProbe = null)
    {
        _logger = logger;
        _exePath = exePath;
        _args = args;
        _pidFilePath = pidFilePath;
        _portProbe = portProbe;
        _portOwnerProbe = portOwnerProbe ?? WindowsListenerOwnerAsync;
        _gracefulStop = gracefulStop ?? WindowsCloseWindow;
        _hardKill = hardKill ?? (static p => p.Kill(entireProcessTree: true));
        _stopGrace = stopGrace ?? TimeSpan.FromSeconds(5);
    }

    private static async Task<bool> TcpPortProbeAsync(int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync("127.0.0.1", port);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var delay = Task.Delay(Timeout.Infinite, linked.Token);
            var completed = await Task.WhenAny(connectTask, delay).ConfigureAwait(false);
            return completed == connectTask && client.Connected;
        }
        catch
        {
            return false; // connection refused/reset - not open yet
        }
    }

    public async Task<GameProxyResult> StartAndWaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct = default)
    {
        KillStalePidFileEntry();

        if (!File.Exists(_exePath))
            return GameProxyResult.Failed($"Proxy executable not found: {_exePath}");

        // Is the port free BEFORE we start? A proxy left over from a client started outside the launcher
        // still holds 1119, and a readiness probe cannot tell "our proxy" from "any listener" — so ask
        // first, the same deterministic guard HermesProxyRunner uses on Linux. Anything of ours is
        // already gone (KillStalePidFileEntry above), so a port still in use here is not ours.
        if (await PortIsTakenAsync(port, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
        {
            _logger.Error("Port {Port} is already in use by a process the launcher does not own", port);
            return GameProxyResult.Failed(PortTakenMessage(port));
        }

        Process process;
        try
        {
            var psi = new ProcessStartInfo(_exePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // The proxy's OWN directory, never the launcher's. JimsProxy reads its config
                // (HermesProxy.config with ServerAddress/ports) from beside the binary, so an inherited
                // working directory breaks it at startup — the same relative-path trap the Linux runner
                // guards against.
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(_exePath)) ?? "",
            };
            foreach (var a in _args) psi.ArgumentList.Add(a);

            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to start proxy: {Exe}", _exePath);
            return GameProxyResult.Failed($"Could not start the proxy: {ex.Message}");
        }

        _process = process;
        WritePidFile(process.Id);
        _ = process.StandardOutput.ReadToEndAsync(); // drain, never block a chatty child
        _ = process.StandardError.ReadToEndAsync();

        var opened = await WaitForPortAsync(port, timeout, process, ct).ConfigureAwait(false);
        if (!opened)
        {
            var diedEarly = process.HasExited;
            _logger.Error(
                "Proxy (PID={Pid}) never opened port {Port} within {Timeout}s (exited early: {DiedEarly})",
                process.Id, port, timeout.TotalSeconds, diedEarly);
            await StopAsync().ConfigureAwait(false);
            return GameProxyResult.Failed(diedEarly
                ? "The proxy exited before it started listening. Check the launcher log."
                : $"The proxy did not open port {port} in time. Check the launcher log.");
        }

        // "The port is open" is NOT "our proxy is listening": a stale proxy could hold the port while
        // the one we started dies of the address conflict. Confirm the thing we started is still alive.
        if (process.HasExited)
        {
            _logger.Error(
                "Port {Port} is open but the proxy we started (PID={Pid}) has already exited: " +
                "another process is holding the port", port, process.Id);
            await StopAsync().ConfigureAwait(false);
            return GameProxyResult.Failed(PortTakenMessage(port));
        }

        // OWNERSHIP, not just liveness (Codex review, now fail-closed): "the port is open and our process
        // is alive" is still not "our process is the one LISTENING on it". A foreign/stale listener could
        // hold 1119 while our proxy is alive but failed to bind. Prove the listener's owning PID is ours —
        // on real Windows via GetExtendedTcpTable (netstat as fallback). If the owner cannot be determined
        // at all, that is now a REFUSAL, not a fall-through: starting the client against an unverified
        // (possibly foreign) listener is worse than a retry. (Off Windows there is no real JimsProxy to
        // run; the unit tests inject the owner probe.)
        var owner = await _portOwnerProbe(port, ct).ConfigureAwait(false);
        if (owner is not int ownerPid)
        {
            _logger.Error(
                "Could not determine the owner of port {Port} — refusing to start against an unverified " +
                "listener (fail-closed)", port);
            await StopAsync().ConfigureAwait(false);
            return GameProxyResult.Failed(PortOwnerUnknownMessage(port));
        }
        if (ownerPid != process.Id)
        {
            _logger.Error(
                "Port {Port} is listened on by PID {Owner}, not our proxy (PID={Pid}) — a foreign or stale " +
                "listener holds it", port, ownerPid, process.Id);
            await StopAsync().ConfigureAwait(false);
            return GameProxyResult.Failed(PortTakenMessage(port));
        }

        _logger.Information("Proxy ready: PID={Pid}, port {Port} (owner confirmed)", process.Id, port);
        return GameProxyResult.Ok(process.Id);
    }

    /// <summary>Re-verify, right before the client is started, that OUR proxy still owns the listener on
    /// <paramref name="port"/> — our process is alive, the port is open, and its owner is our PID. Narrows
    /// the check-to-use gap between "proxy ready" and "start the client" (Codex Finding 1). Returns false
    /// when the proxy died, the port dropped, or a different process now owns it — the caller then rolls
    /// the launch back rather than starting a client against a listener it no longer controls.</summary>
    public async Task<bool> VerifyStillListeningAsync(int port, CancellationToken ct = default)
    {
        var process = _process;
        if (process is null || process.HasExited) return false;
        if (!await _portProbe(port, ct).ConfigureAwait(false)) return false;
        var owner = await _portOwnerProbe(port, ct).ConfigureAwait(false);
        return owner == process.Id; // null (undeterminable) or a foreign PID both fail this
    }

    private static string PortOwnerUnknownMessage(int port) =>
        $"The launcher could not confirm that its own proxy owns port {port}, so it did not start the " +
        "client (starting against an unverified listener could connect to the wrong place).\n" +
        "Close any other program using that port and try again.";

    /// <summary>Which PID owns the TCP listener on <paramref name="port"/>, or null when it cannot be
    /// determined. Windows-only: primary path is <c>GetExtendedTcpTable</c> (the IP Helper API, table
    /// class TCP_TABLE_OWNER_PID_LISTENER — race-free and does not spawn a process); the
    /// <c>netstat -ano</c> parse is kept as a fallback. Off Windows there is no real JimsProxy to run, so
    /// it returns null and the caller (now fail-closed) refuses.
    ///
    /// <para><b>Only verifiable on real Windows.</b> The netstat parse is unit-tested via
    /// <see cref="ParseNetstatOwner"/>; the GetExtendedTcpTable path is E2E-only, as is whether JimsProxy
    /// binds on its own PID (a single-process console proxy, as <c>Play Stonetavern.cmd</c> starts it)
    /// versus a child.</para></summary>
    private static async Task<int?> WindowsListenerOwnerAsync(int port, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var viaApi = TcpTableOwner.ListenerOwningPid(port);
        if (viaApi is int pid) return pid;

        // Fallback: parse netstat, in case the API path was unavailable for any reason.
        try
        {
            var psi = new ProcessStartInfo("netstat")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-ano");

            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            _ = proc.StandardError.ReadToEndAsync(ct);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try { await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch { } return null; }

            return ParseNetstatOwner(await stdoutTask.ConfigureAwait(false), port);
        }
        catch
        {
            return null; // cannot determine — caller (fail-closed) refuses
        }
    }

    /// <summary>
    /// The IP Helper API owner lookup: <c>GetExtendedTcpTable</c> with <c>TCP_TABLE_OWNER_PID_LISTENER</c>
    /// returns the listening TCP sockets with their owning PID, without spawning a process or parsing
    /// text. IPv4 only here (the JimsProxy config binds <c>127.0.0.1</c>); an IPv6-only listener would
    /// fall through to the netstat fallback which also reads <c>[::]</c>. Windows-only; E2E-verified.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static class TcpTableOwner
    {
        private const int AfInet = 2;                       // AF_INET
        private const int TcpTableOwnerPidListener = 3;     // TCP_TABLE_OWNER_PID_LISTENER
        private const int ErrorInsufficientBuffer = 122;
        private const int RowSize = 24;                     // MIB_TCPROW_OWNER_PID: 6 x DWORD

        [System.Runtime.InteropServices.DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tableClass, int reserved);

        public static int? ListenerOwningPid(int port)
        {
            try
            {
                int bufLen = 0;
                var ret = GetExtendedTcpTable(IntPtr.Zero, ref bufLen, false, AfInet, TcpTableOwnerPidListener, 0);
                if (ret != ErrorInsufficientBuffer || bufLen <= 0) return null;

                var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(bufLen);
                try
                {
                    ret = GetExtendedTcpTable(buf, ref bufLen, false, AfInet, TcpTableOwnerPidListener, 0);
                    if (ret != 0) return null;

                    int n = System.Runtime.InteropServices.Marshal.ReadInt32(buf);        // dwNumEntries
                    var rowPtr = IntPtr.Add(buf, 4);
                    for (int i = 0; i < n; i++)
                    {
                        // MIB_TCPROW_OWNER_PID: state(0), localAddr(4), localPort(8), remoteAddr(12),
                        // remotePort(16), owningPid(20). localPort is network byte order in the low word.
                        int rawPort = System.Runtime.InteropServices.Marshal.ReadInt32(rowPtr, 8);
                        int rowPort = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF);
                        if (rowPort == port)
                            return System.Runtime.InteropServices.Marshal.ReadInt32(rowPtr, 20);
                        rowPtr = IntPtr.Add(rowPtr, RowSize);
                    }
                    return null;
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(buf);
                }
            }
            catch
            {
                return null; // API unavailable/failed — caller falls back to netstat
            }
        }
    }

    /// <summary>Parse the owning PID of the LISTENING socket on <paramref name="port"/> out of
    /// <c>netstat -ano</c> output, or null when no such listener is found. A line looks like:
    /// <c>  TCP    127.0.0.1:1119    0.0.0.0:0    LISTENING    1234</c> (also <c>0.0.0.0:1119</c> or
    /// <c>[::]:1119</c>). Matches on the LOCAL address column ending in <c>:port</c> and the LISTENING
    /// state, and returns the last column as the PID.</summary>
    internal static int? ParseNetstatOwner(string netstatOutput, int port)
    {
        var suffix = ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var raw in netstatOutput.Split('\n'))
        {
            var cols = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 4) continue;
            if (!cols[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)) continue;
            if (!cols.Any(c => c.Equals("LISTENING", StringComparison.OrdinalIgnoreCase))) continue;
            // Local address is column 1; it must be the port we asked about (not a remote endpoint).
            if (!cols[1].EndsWith(suffix, StringComparison.Ordinal)) continue;
            if (int.TryParse(cols[^1], out var pid)) return pid;
        }
        return null;
    }

    public async Task StopAsync()
    {
        var process = _process;
        if (process is null)
        {
            DeletePidFile(); // nothing of ours running; clear any leftover pidfile
            return;
        }

        bool exited;
        try
        {
            exited = process.HasExited || await GracefulThenHardStopAsync(process).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Stopping the proxy threw (best effort)");
            exited = process.HasExited;
        }

        if (exited)
        {
            // Drop the references ONLY now that the exit is confirmed. Clearing them before a confirmed
            // exit (the earlier bug) made a failed kill un-retryable: the handle and the pidfile a later
            // reap or the next-start self-heal needs were already gone, and an orphan on 1119 had nothing
            // pointing at it.
            _process = null;
            DeletePidFile();
            try { process.Dispose(); } catch { /* already gone */ }
        }
        else
        {
            // The kill did not take. KEEP the handle AND the pidfile so a later StopAsync, the tray-quit
            // reap, or the next-start self-heal can retry against this exact PID — never leave an orphan
            // holding the port with nothing that can find it again.
            _logger.Error(
                "Proxy (PID={Pid}) did not exit after a stop attempt — keeping its handle and pidfile so a " +
                "later reap can retry; the realm port may still be held", process.Id);
        }
    }

    /// <summary>
    /// Stop JimsProxy the Windows way: ask its window to close first so it can drain its sockets, and
    /// escalate to a hard kill only when it does not exit within the grace window. When there is no
    /// window to close (redirected output — our normal case), the graceful step reports "not delivered"
    /// and the hard kill runs immediately.
    ///
    /// <para><b>Is a clean socket drain necessary? No — a hard kill is acceptable here, and here is the
    /// reasoning (Codex review P2).</b> A stop only ever reaches this method AFTER the client process is
    /// gone (the <see cref="IGameSession"/> reap watchdog waits for the client PID to exit, and the tray
    /// Quit refuses while a client is alive). So the player is already disconnected before the proxy is
    /// touched; killing the proxy at that point causes at most a dirty close of the proxy↔realm socket,
    /// which the realm sees as an ordinary player disconnect (a session that is already ending). There is
    /// no in-flight player packet to lose. This is exactly what the proven <c>Play Stonetavern.cmd</c>
    /// does (<c>taskkill /IM JimsProxy.exe /F</c>) after the client closes. The <see cref="Process.CloseMainWindow"/>
    /// attempt is kept as the cheaper, cleaner path FOR THE CASE where JimsProxy is started with a visible
    /// console window (a future non-redirected mode) — then it can close its listener itself — but it is
    /// not required for correctness.</para>
    /// </summary>
    /// <returns>Whether the process actually exited. The caller (<see cref="StopAsync"/>) keeps the
    /// handle and pidfile when this is false, so a failed kill stays retryable rather than orphaning the
    /// port.</returns>
    private async Task<bool> GracefulThenHardStopAsync(Process process)
    {
        var pid = process.Id;
        var closed = false;
        try { closed = _gracefulStop(process); }
        catch (Exception ex) { _logger.Debug(ex, "Graceful close of proxy PID {Pid} failed", pid); }

        if (closed)
        {
            _logger.Information(
                "Asked the proxy window to close (PID={Pid}); waiting up to {Grace:F0}s for a clean exit",
                pid, _stopGrace.TotalSeconds);
            if (await WaitForExitAsync(process, _stopGrace).ConfigureAwait(false))
            {
                _logger.Information("Proxy (PID={Pid}) exited cleanly after the close request", pid);
                return true;
            }
            _logger.Warning(
                "Proxy (PID={Pid}) did not exit within {Grace:F0}s of the close request — escalating to a hard kill",
                pid, _stopGrace.TotalSeconds);
        }

        try { _hardKill(process); }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Hard kill of proxy PID {Pid} threw", pid);
            return process.HasExited;
        }
        // Confirm the hard kill actually took, and report it — a kill that did not land must not be
        // reported as a clean stop.
        return await WaitForExitAsync(process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    /// <summary>Ask a process' main window to close (delivers <c>WM_CLOSE</c>). Returns whether a window
    /// was there to receive it — false means the process has no window (e.g. output was redirected), and
    /// the caller then goes straight to the hard kill. Non-Windows: false (no console-window semantics),
    /// so the seam is inert off Windows and unit tests drive it explicitly.</summary>
    private static bool WindowsCloseWindow(Process process)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return process.CloseMainWindow(); }
        catch { return false; }
    }

    /// <summary>Wait up to <paramref name="grace"/> for the process to exit; returns whether it did.</summary>
    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan grace)
    {
        if (process.HasExited) return true;
        using var cts = new CancellationTokenSource(grace);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }

    /// <summary>True if <paramref name="port"/> is still accepting connections after
    /// <paramref name="grace"/> — a proxy of ours that was just killed needs a moment to release the
    /// socket, so reporting "in use" immediately would turn our own cleanup into a refusal to start.</summary>
    private async Task<bool> PortIsTakenAsync(int port, TimeSpan grace, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + grace;
        while (true)
        {
            if (!await _portProbe(port, ct).ConfigureAwait(false)) return false;
            if (DateTime.UtcNow >= deadline) return true;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    private static string PortTakenMessage(int port) =>
        $"Port {port} is already in use. The realm proxy needs it, and something else is holding it.\n" +
        "This is usually a proxy left running from a client started outside the launcher. Close it, " +
        "or end JimsProxy.exe in Task Manager (or run: taskkill /IM JimsProxy.exe /F).";

    private async Task<bool> WaitForPortAsync(int port, TimeSpan timeout, Process process, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) return false; // died before ever opening the port - stop polling
            if (await _portProbe(port, ct).ConfigureAwait(false)) return true;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        return false;
    }

    private void KillStalePidFileEntry()
    {
        int pid;
        try
        {
            if (!File.Exists(_pidFilePath)) return;
            var text = File.ReadAllText(_pidFilePath).Trim();
            if (!int.TryParse(text, out pid)) { DeletePidFile(); return; }
        }
        catch { return; }

        try
        {
            using var stale = Process.GetProcessById(pid);
            // Name-checked: GetProcessById alone cannot tell "our old proxy" from "whatever the OS handed
            // that PID to since" (a reboot recycles PIDs). Only kill a plausible match.
            if (LooksLikeOurProxy(stale))
            {
                _logger.Warning("Killing a stale proxy from a previous session (PID={Pid})", pid);
                stale.Kill(entireProcessTree: true);
                stale.WaitForExit(5000);
            }
        }
        catch (ArgumentException)
        {
            // No process with that PID - already gone, nothing to do.
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Could not check/kill stale proxy PID {Pid}", pid);
        }
        finally
        {
            DeletePidFile();
        }
    }

    private bool LooksLikeOurProxy(Process candidate)
    {
        try
        {
            var expected = Path.GetFileNameWithoutExtension(_exePath);
            return string.Equals(candidate.ProcessName, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void WritePidFile(int pid)
    {
        try
        {
            var dir = Path.GetDirectoryName(_pidFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_pidFilePath, pid.ToString());
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Could not write proxy pidfile {Path}", _pidFilePath);
        }
    }

    private void DeletePidFile()
    {
        try { if (File.Exists(_pidFilePath)) File.Delete(_pidFilePath); }
        catch { /* best effort */ }
    }
}
