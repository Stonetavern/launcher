namespace WowLauncher.Services.Platform;

using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

/// <summary>
/// Outcome of starting the realm proxy and waiting for it to actually accept connections. Never
/// reports "ready" on a guess: a proxy process that launched but never bound its listener is exactly
/// what let the client run "into the void" under the hand-run shell script this replaces
/// (HANDOFF-LINUX-FERTIGBAUEN-2026-07-21.md §3 WP1 — "nicht blind weiterlaufen").
/// </summary>
public sealed record GameProxyResult(bool Ready, int? ProcessId = null, string? Error = null)
{
    public static GameProxyResult Ok(int pid) => new(true, pid);
    public static GameProxyResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// A realm-facing proxy the client connects through instead of the real server (HermesProxy, for the
/// 1.14.2/Classic Era client, listening on a local TCP port). The launcher owns its whole lifecycle:
/// start it, PROVE it is actually listening before handing control to the client, and make sure it
/// never outlives the session — including a launcher that crashed instead of exiting cleanly.
/// </summary>
public interface IGameProxy
{
    /// <summary>Start the proxy and block until <paramref name="port"/> actually accepts a TCP
    /// connection, or <paramref name="timeout"/> elapses. A started-but-not-yet-listening process is
    /// NOT success.</summary>
    Task<GameProxyResult> StartAndWaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>Stop the proxy this instance started. Safe to call when nothing is running.</summary>
    Task StopAsync();

    /// <summary>Re-verify, right before the client is started, that THIS proxy still owns the listener on
    /// <paramref name="port"/> (our process alive, port open, owner is our PID) — narrows the
    /// check-to-use gap between "proxy ready" and "start the client" (Codex Finding 1).
    ///
    /// <para>Both shipped runners implement this for real — <see cref="JimsProxyRunner"/> on Windows,
    /// <see cref="HermesProxyRunner"/> on Linux and macOS. The <c>true</c> here is what a test double or
    /// a future runner inherits, and it is a claim, not a check: anything that keeps it is saying "I do
    /// not verify ownership". Do not leave it in place on something a player launches through.</para></summary>
    Task<bool> VerifyStillListeningAsync(int port, CancellationToken ct = default) => Task.FromResult(true);

    /// <summary>Whether the proxy process this instance started is still running — <c>true</c> alive,
    /// <c>false</c> gone, <c>null</c> "this implementation does not measure it".
    ///
    /// <para>The three-valued shape is the point. A plain <c>bool</c> forces a default to CLAIM
    /// something: <c>true</c> would report every unmeasuring implementation as healthy and hide a dead
    /// proxy, <c>false</c> would raise an alarm on every test double. <c>null</c> says "not measured",
    /// which is the only honest answer for a type that does not own a process — and the watchdog in
    /// <see cref="GameSession"/> treats it as silence, never as a verdict.</para>
    ///
    /// <para>Unlike <see cref="VerifyStillListeningAsync"/> this is deliberately cheap and synchronous:
    /// it is polled every couple of seconds for the whole length of a play session, so it may only look
    /// at the process handle the runner already holds — no port probe, no <c>lsof</c>.</para></summary>
    bool? IsProcessAlive => null;
}

/// <summary>
/// Runs a proxy as a child process and proves it is listening before returning control.
///
/// <para><b>Surviving a launcher crash.</b> A clean shutdown (<see cref="StopAsync"/>, or the normal
/// Play → launch → exit flow) always reaches the kill below. A HARD crash (SIGKILL, power loss) skips
/// every finally block, so nothing in THIS process can guarantee cleanup for that case — .NET's
/// <see cref="Process"/> has no cross-platform equivalent of Linux's <c>PR_SET_PDEATHSIG</c>. The
/// realistic guarantee instead: record the started PID in a durable pidfile, and self-heal on the
/// NEXT start — before starting a new proxy, kill whatever the pidfile still points at (name-checked,
/// so a PID the OS recycled for something unrelated after a reboot is never touched). The same shape
/// <c>ClientService.IsGameRunning()</c> already uses for "is a stale client still around" — detect and
/// correct on the next run, not a kernel-level guarantee this process cannot make.</para>
///
/// <para><b>Not wired into DI yet.</b> The exe path this runs is a constructor parameter, not resolved
/// here — HOW the launcher obtains the proxy binary (bundled vs. hash-verified download, matching
/// WP1's "umu beschaffen" half) is a separate, security-sensitive decision (a downloader that fetches
/// and pins the wrong thing is worse than no downloader) and is deliberately NOT decided in this pass.
/// This class is the lifecycle half of WP1: start, prove-listening, self-healing stop — ready for
/// whichever launcher (<c>UmuGameLauncher</c> or otherwise) supplies a resolved binary path.</para>
/// </summary>
public sealed class HermesProxyRunner : IGameProxy
{
    private readonly Serilog.ILogger _logger;
    private readonly string _exePath;
    private readonly IReadOnlyList<string> _args;
    private readonly string _pidFilePath;
    /// <summary>Where the proxy is STARTED from. Not derived from the binary any more: the macOS package
    /// puts the binary in <c>Hermes/bin/</c> while its config and CSV data stay in <c>Hermes/</c>, so the
    /// exe directory is the wrong answer there (Codex review 2026-08-24). Null keeps the old behaviour.</summary>
    private readonly string? _workingDirectory;
    private readonly Func<int, CancellationToken, Task<bool>> _portProbe;
    private readonly Func<int, CancellationToken, Task<int?>> _portOwnerProbe;
    private readonly IReadOnlyDictionary<string, string>? _environmentOverrides;
    private readonly Func<int, bool> _sigterm;   // graceful stop; true when the signal was delivered
    private readonly Action<Process> _hardKill;  // SIGKILL fallback (own seam so a test can observe it)
    private readonly TimeSpan _stopGrace;        // how long to wait for a clean exit before escalating
    /// <summary>Where the proxy's own stdout/stderr is recorded. Null keeps the old behaviour (drain and
    /// discard) — the launch never depends on it. See <see cref="ProxyOutputLog"/> for why it exists.</summary>
    private readonly string? _outputLogPath;

    /// <summary>Move the proxy's REST/realm/instance ports off anything that already holds them and
    /// wait for ALL of them before reporting ready (see <see cref="HermesPortPlan"/>). Off by default so
    /// a caller that passes its own port arguments keeps full control.</summary>
    private readonly bool _relocateAuxiliaryPorts;
    private readonly Func<int, CancellationToken, Task<bool>> _auxPortProbe;
    private readonly Func<ISet<int>, int> _freePortPicker;

    private Process? _process;
    private ProxyOutputLog? _outputLog;
    /// <summary>The first line in which the proxy said it failed to come up (<see cref="HermesPortPlan.IsFatal"/>).
    /// Set from the output pump; once set, a still-running process is NOT a working proxy.</summary>
    private volatile string? _fatalLine;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _fatalLines = new();

    /// <summary>The auxiliary ports the proxy was started with (after relocation), for logs and tests.</summary>
    public IReadOnlyDictionary<string, int>? AuxiliaryPorts { get; private set; }

    public HermesProxyRunner(Serilog.ILogger logger, string exePath, IReadOnlyList<string> args, string pidFilePath,
        IReadOnlyDictionary<string, string>? environmentOverrides = null, string? workingDirectory = null,
        string? outputLogPath = null, bool relocateAuxiliaryPorts = false)
        : this(logger, exePath, args, pidFilePath, TcpPortProbeAsync, environmentOverrides, workingDirectory,
            outputLogPath: outputLogPath, relocateAuxiliaryPorts: relocateAuxiliaryPorts)
    {
    }

    /// <summary>Test seam: a fake "is the port open" probe stands in for a real TCP connect attempt,
    /// so the polling/timeout/early-exit LOGIC can be proven without a real listening process. The
    /// graceful-stop seams (<paramref name="sigterm"/>, <paramref name="hardKill"/>,
    /// <paramref name="stopGrace"/>) default to the real POSIX behaviour and are only overridden in tests
    /// that prove SIGTERM is tried before SIGKILL.</summary>
    internal HermesProxyRunner(Serilog.ILogger logger, string exePath, IReadOnlyList<string> args,
        string pidFilePath, Func<int, CancellationToken, Task<bool>> portProbe,
        IReadOnlyDictionary<string, string>? environmentOverrides = null, string? workingDirectory = null,
        Func<int, bool>? sigterm = null,
        Action<Process>? hardKill = null, TimeSpan? stopGrace = null,
        Func<int, CancellationToken, Task<int?>>? portOwnerProbe = null,
        string? outputLogPath = null,
        bool relocateAuxiliaryPorts = false,
        Func<int, CancellationToken, Task<bool>>? auxPortProbe = null,
        Func<ISet<int>, int>? freePortPicker = null)
    {
        _outputLogPath = outputLogPath;
        _relocateAuxiliaryPorts = relocateAuxiliaryPorts;
        _auxPortProbe = auxPortProbe ?? TcpPortProbeAsync;
        _freePortPicker = freePortPicker ?? HermesPortPlan.PickFreeLoopbackPort;
        _logger = logger;
        _exePath = exePath;
        _args = args;
        _pidFilePath = pidFilePath;
        _workingDirectory = workingDirectory;
        _portProbe = portProbe;
        _portOwnerProbe = portOwnerProbe ?? ListenerOwnerAsync;
        _environmentOverrides = environmentOverrides;
        _sigterm = sigterm ?? PosixSigterm;
        _hardKill = hardKill ?? (static p => p.Kill(entireProcessTree: true));
        _stopGrace = stopGrace ?? TimeSpan.FromSeconds(5);
    }

    // Await the connect with a token instead of racing it against a delay — same shape as
    // ServerStatusService.CheckAsync. The Task.WhenAny form left the connect running when the 300 ms
    // delay won: the using-block disposed the TcpClient underneath it, the pending I/O faulted with
    // SocketException 995 ("aborted by thread exit or application request"), and nobody ever observed
    // that task. Every poll against a not-yet-open port produced one, and they surfaced together at the
    // next GC as the TaskScheduler.UnobservedTaskException series players kept sending in — which then
    // pushed the real launcher.log out of the problem report, because that picks the newest
    // "launcher*.log" and launcher_crash.log grows without bound.
    internal static async Task<bool> TcpPortProbeAsync(int port, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(300));
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false; // connection refused/reset/timed out - not open yet
        }
    }

    public async Task<GameProxyResult> StartAndWaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct = default)
    {
        KillStalePidFileEntry();

        if (!File.Exists(_exePath))
            return GameProxyResult.Failed($"Proxy executable not found: {_exePath}");

        // A per-file update by launcher 1.9.1 or older left the proxy without its execute bit
        // (E2E 2026-09-24). Repair it here, so such an install plays again without a download.
        if (UnixExecBit.Ensure(_exePath))
            _logger.Warning("Proxy {Exe} was not executable; execute bit restored", _exePath);

        // Is the port free BEFORE we start? This is the deterministic half of the "someone else holds
        // the port" problem observed on 2026-07-22: with the port already taken, the readiness probe
        // goes green on its first poll - it can only see that SOMETHING listens - while the proxy we
        // just started dies of the address conflict a few seconds later. Timing decides which of the
        // two the check below notices, so the reliable answer is to ask before starting anything.
        // Anything of ours is already gone at this point (KillStalePidFileEntry above), so a port still
        // in use here belongs to something the launcher does not own.
        if (await PortIsTakenAsync(port, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
        {
            var holder = await DescribePortOwnerAsync(port, ct).ConfigureAwait(false);
            _logger.Error("Port {Port} is already in use by a process the launcher does not own ({Holder})",
                port, holder ?? "owner unknown");
            return GameProxyResult.Failed(PortTakenMessage(port, holder));
        }

        // The ports the proxy binds after 1119 (REST 8081, realm 8084, instance 8086). A taken one used to
        // kill the proxy AFTER 1119 was already open, which the check above cannot see (2026-09-27: a
        // local llama-server on 8081). The proxy tells the client these ports itself, so a taken one is
        // moved to a free port instead of refused — see HermesPortPlan for the source evidence.
        var args = new List<string>(_args);
        if (_relocateAuxiliaryPorts)
        {
            var workDir = _workingDirectory ?? Path.GetDirectoryName(Path.GetFullPath(_exePath)) ?? "";
            var plan = await HermesPortPlan.BuildAsync(
                HermesPortPlan.ReadConfigured(Path.Combine(workDir, "HermesProxy.config")), port,
                _auxPortProbe, _freePortPicker, ct).ConfigureAwait(false);
            foreach (var move in plan.Moves) _logger.Warning("Proxy port moved: {Move}", move);
            args.AddRange(plan.ExtraArgs);
            AuxiliaryPorts = plan.Ports;
        }

        Process process;
        try
        {
            var psi = new ProcessStartInfo(_exePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // stdin is OURS and closed right after the start. On Linux and macOS the proxy's
                // Program.cs always ends with "Press enter to close" + Console.ReadLine(); with an
                // inherited stdin that read never returns, and a proxy that failed to start sat there
                // holding 1119 (2026-09-27). A closed pipe makes the read return at once, so a failed
                // proxy actually exits and frees its ports.
                RedirectStandardInput = true,
                // The proxy's OWN directory, never the launcher's. HermesProxy loads its game data
                // (flight paths, spell tables, hotfixes) from a CSV folder BESIDE the binary using a
                // relative path, so an inherited working directory makes it die at startup with a
                // DirectoryNotFoundException on Hermes/CSV/Hotfix/... - the exact crash a mis-scoped
                // packaging exclude produced on 2026-07-21, reproduced and fixed there. Setting it
                // here means the launcher cannot recreate that failure by accident.
                // Explicit directory when the caller knows it (macOS: Hermes/, while the binary sits in
                // Hermes/bin/), otherwise the binary's own directory as before.
                WorkingDirectory = _workingDirectory ?? Path.GetDirectoryName(Path.GetFullPath(_exePath)) ?? "",
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (_environmentOverrides is not null)
                foreach (var (key, value) in _environmentOverrides) psi.Environment[key] = value;
            // Without libicu the .NET proxy aborts before it opens its port (matrix 2026-09-24).
            if (OperatingSystem.IsLinux()
                && IcuProbe.ProxyEnvironmentFor(IcuProbe.DefaultDirs, Environment.GetEnvironmentVariable) is { } icu
                && !psi.Environment.ContainsKey(icu.Key))
            {
                _logger.Warning("No libicu found; starting the proxy with {Var}=1", icu.Key);
                psi.Environment[icu.Key] = icu.Value;
            }

            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to start proxy: {Exe}", _exePath);
            return GameProxyResult.Failed($"Could not start the proxy: {ex.Message}");
        }

        _process = process;
        _fatalLine = null;
        _fatalLines.Clear();
        try { process.StandardInput.Close(); } catch { /* already gone: nothing to close */ }
        WritePidFile(process.Id);
        _outputLog = ProxyOutputLog.AttachOrDrain(process, _outputLogPath, _logger,
            onLine: (_, line) =>
            {
                if (!HermesPortPlan.IsFatal(line)) return;
                if (_fatalLines.Count < 8) _fatalLines.Enqueue(line.Trim());
                _fatalLine ??= line.Trim();
            });

        var deadline = DateTime.UtcNow + timeout;
        var opened = await WaitForPortAsync(port, timeout, process, ct).ConfigureAwait(false);
        if (opened && _relocateAuxiliaryPorts && AuxiliaryPorts is not null)
        {
            // 1119 alone is not "ready": the proxy starts its listeners one after another and may die on
            // the second. Wait for every one of them, or for the proxy to say it failed.
            foreach (var (key, auxPort) in AuxiliaryPorts)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero
                    || !await WaitForPortAsync(auxPort, left, process, ct, _auxPortProbe).ConfigureAwait(false))
                {
                    _logger.Error("Proxy (PID={Pid}) opened {Port} but never its {Key} on {AuxPort}",
                        process.Id, port, key, auxPort);
                    opened = false;
                    break;
                }
            }
        }
        if (_fatalLine is not null)
        {
            // stdout and stderr arrive in no fixed order: give the other stream a moment, then name the
            // line that says WHAT failed, not merely the first one that said something did.
            await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
            var fatal = HermesPortPlan.MostTelling(_fatalLines.ToArray());
            _logger.Error("Proxy (PID={Pid}) reported a failed start: {Lines}", process.Id,
                string.Join(" | ", _fatalLines));
            await StopAsync().ConfigureAwait(false);
            return GameProxyResult.Failed(FailedStartMessage(fatal));
        }
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

        // "The port is open" is NOT "our proxy is listening". Observed for real on 2026-07-22: a proxy
        // left over from an earlier session still held 1119, the probe went green on the FIRST poll,
        // and the proxy we had just started died a second later because the port was taken - reported
        // as ready, with the client then talking to a stale process nobody owned. So the success path
        // has to confirm the thing we started is the thing that is alive.
        if (process.HasExited)
        {
            _logger.Error(
                "Port {Port} is open but the proxy we started (PID={Pid}) has already exited: " +
                "another process is holding the port", port, process.Id);
            await StopAsync().ConfigureAwait(false);
            return GameProxyResult.Failed(PortTakenMessage(port));
        }

        _logger.Information("Proxy ready: PID={Pid}, port {Port}", process.Id, port);
        return GameProxyResult.Ok(process.Id);
    }

    /// <summary>
    /// Prove, right before the client is started, that the proxy this instance started is still the one
    /// on <paramref name="port"/>. Three questions, cheapest first: is our process still alive, is the
    /// port still open, and does the listener belong to our PID.
    ///
    /// <para><b>What this refuses and what it lets through.</b> A listener that demonstrably belongs to
    /// someone else is a refusal — that is the whole failure this closes, and it happened for real on
    /// 2026-07-22. An owner that cannot be determined at all (no <c>/proc</c>, no <c>lsof</c>) is NOT a
    /// refusal here: it logs and falls back to alive-and-listening. That is a deliberate difference from
    /// <see cref="JimsProxyRunner"/>, which is fail-closed because its Windows probe is reliable. On the
    /// platforms this runner serves, turning an unreadable probe into "you cannot play" would take the
    /// game away from players whose setup is fine.</para>
    /// </summary>
    /// <inheritdoc />
    /// <remarks>Reads only the process handle this runner already holds. <c>null</c> before a start and
    /// after a clean stop — in both cases there is no proxy of ours to be dead.</remarks>
    public bool? IsProcessAlive
    {
        get
        {
            var process = _process;
            if (process is null) return null;
            // A disposed or otherwise unreadable handle is NOT proof of death: reporting false here
            // would tell a playing user their connection dropped when nothing happened.
            // A proxy that said it failed to start is dead to the player even while its process sits
            // on "Press enter to close" holding 1119.
            if (_fatalLine is not null) return false;
            try { return !process.HasExited; }
            catch (System.InvalidOperationException) { return null; }
        }
    }

    public async Task<bool> VerifyStillListeningAsync(int port, CancellationToken ct = default)
    {
        var process = _process;
        if (process is null || process.HasExited || _fatalLine is not null)
        {
            _logger.Error("The proxy is no longer running when the client was about to start");
            return false;
        }
        if (!await _portProbe(port, ct).ConfigureAwait(false))
        {
            _logger.Error("Port {Port} is no longer open when the client was about to start", port);
            return false;
        }

        var owner = await _portOwnerProbe(port, ct).ConfigureAwait(false);
        if (owner is null)
        {
            _logger.Warning(
                "Could not determine which process owns port {Port}; proceeding on process-alive and " +
                "port-open alone", port);
            return true;
        }
        if (owner != process.Id)
        {
            _logger.Error(
                "Port {Port} is owned by PID {Owner}, not by our proxy (PID={Pid}) — aborting the launch",
                port, owner, process.Id);
            return false;
        }
        return true;
    }

    /// <summary>Which PID holds the listener on <paramref name="port"/>, or null when it cannot be
    /// told. Linux reads <c>/proc</c> directly (the socket inode of the listener, then the file
    /// descriptors of our own child — no external command, no parsing of localised output); macOS
    /// shells out to <c>lsof</c>, which is present in the base system. Anything else returns null,
    /// which the caller treats as "unknown", not as "foreign".</summary>
    private static async Task<int?> ListenerOwnerAsync(int port, CancellationToken ct)
    {
        if (OperatingSystem.IsLinux()) return LinuxListenerOwner(port);
        if (OperatingSystem.IsMacOS()) return await MacListenerOwnerAsync(port, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>The listening socket's inode from <c>/proc/net/tcp</c>, then whichever process has that
    /// inode open. Only processes we may read are searched, which for a child of ours is enough.</summary>
    private static int? LinuxListenerOwner(int port)
    {
        try
        {
            var inodes = ListeningInodes("/proc/net/tcp", port);
            foreach (var i in ListeningInodes("/proc/net/tcp6", port)) inodes.Add(i);
            if (inodes.Count == 0) return null;

            foreach (var procDir in Directory.EnumerateDirectories("/proc"))
            {
                var name = Path.GetFileName(procDir);
                if (!int.TryParse(name, out var pid)) continue;
                string[] fds;
                try { fds = Directory.GetFiles(Path.Combine(procDir, "fd")); }
                catch { continue; }   // not ours to read — skip, do not conclude

                foreach (var fd in fds)
                {
                    string? target;
                    try { target = new FileInfo(fd).LinkTarget; }
                    catch { continue; }
                    if (target is null || !target.StartsWith("socket:[", StringComparison.Ordinal)) continue;
                    var inode = target[8..].TrimEnd(']');
                    if (inodes.Contains(inode)) return pid;
                }
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>The socket inodes of every LISTEN entry on <paramref name="port"/> in a
    /// <c>/proc/net/tcp</c>-shaped table. The local address column is <c>HEX_IP:HEX_PORT</c> and state
    /// <c>0A</c> is LISTEN.</summary>
    private static HashSet<string> ListeningInodes(string table, int port)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        string[] lines;
        try { lines = File.ReadAllLines(table); }
        catch { return found; }

        var wanted = ":" + port.ToString("X4");
        foreach (var line in lines.Skip(1))
        {
            var cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 10) continue;
            if (!cols[1].EndsWith(wanted, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(cols[3], "0A", StringComparison.OrdinalIgnoreCase)) continue;
            found.Add(cols[9]);
        }
        return found;
    }

    private static async Task<int?> MacListenerOwnerAsync(int port, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/sbin/lsof")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-nP", $"-iTCP:{port}", "-sTCP:LISTEN", "-t" }) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);

            var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return int.TryParse(first?.Trim(), out var pid) ? pid : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Stop the proxy and only then forget it. The order matters: until 2026-08-03 this method cleared
    /// <c>_process</c> and deleted the PID file FIRST and threw the kill's result away, so a kill that
    /// did not work left a live proxy on <see cref="ProxyPort"/> that nothing pointed at any more —
    /// neither this instance nor the next start's stale-PID sweep, which reads exactly that file. The
    /// launcher reported a clean stop and the player got "port already in use" on the next Play.
    ///
    /// <para>So: measure against the world (did the process actually exit), not against the fact that a
    /// kill was issued. A failed stop keeps both the handle and the PID file, which is what makes the
    /// corpse findable. Idempotent either way — a second call on an already-stopped runner is a no-op.</para>
    /// </summary>
    public async Task StopAsync()
    {
        var process = _process;
        if (process is null)
        {
            DeletePidFile();
            return;
        }

        var stopped = false;
        try
        {
            stopped = process.HasExited || await GracefulThenHardStopAsync(process).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Stopping the proxy failed (best effort)");
        }

        if (!stopped)
        {
            _logger.Error(
                "The proxy (PID={Pid}) is STILL RUNNING after the stop attempt; keeping {PidFile} and the "
                + "handle so the next start can reap it instead of hitting a busy port",
                SafePid(process), _pidFilePath);
            return;
        }

        _process = null;
        DeletePidFile();
        // Close the output log only once the proxy is confirmed gone — its last lines are written as it
        // dies, and those are the ones worth reading.
        try { _outputLog?.Dispose(); } catch { /* best effort */ }
        _outputLog = null;
        process.Dispose();
    }

    /// <summary>The PID for a log line, or -1 when the handle can no longer tell us. Never throws:
    /// a failed stop must still produce a readable log line.</summary>
    private static int SafePid(Process process)
    {
        try { return process.Id; }
        catch { return -1; }
    }

    /// <summary>Has this process really exited? Any handle that can no longer answer counts as "no",
    /// because the safe direction here is to keep the corpse findable.</summary>
    private static bool HasExitedSafe(Process process)
    {
        try { return process.HasExited; }
        catch { return false; }
    }

    /// <summary>
    /// Stop the proxy the way the working start script does: SIGTERM first so HermesProxy can close its
    /// listener and drain its sockets cleanly, and only escalate to SIGKILL if it does not exit within
    /// the grace window. A hard kill mid-play would leave a half-open realm connection behind; the
    /// player has already been proven gone before we get here (see <see cref="IGameSession"/>), so the
    /// cost of the grace is nil and the upside is a clean teardown. Windows has no SIGTERM —
    /// <see cref="_sigterm"/> returns false there and we go straight to the hard kill, which is the
    /// shipped Windows behaviour unchanged.
    /// </summary>
    private async Task<bool> GracefulThenHardStopAsync(Process process)
    {
        var pid = process.Id;
        var termed = false;
        try { termed = _sigterm(pid); }
        catch (Exception ex) { _logger.Debug(ex, "SIGTERM to proxy PID {Pid} failed", pid); }

        if (termed)
        {
            _logger.Information(
                "Sent SIGTERM to the proxy (PID={Pid}); waiting up to {Grace:F0}s for a clean exit",
                pid, _stopGrace.TotalSeconds);
            if (await WaitForExitAsync(process, _stopGrace).ConfigureAwait(false))
            {
                _logger.Information("Proxy (PID={Pid}) exited cleanly after SIGTERM", pid);
                return true;
            }
            _logger.Warning(
                "Proxy (PID={Pid}) did not exit within {Grace:F0}s of SIGTERM — escalating to SIGKILL",
                pid, _stopGrace.TotalSeconds);
        }

        _hardKill(process);
        try { process.WaitForExit(5000); } catch { /* best effort */ }
        // The answer this returns is read from the process, not from the fact that a kill was issued.
        return HasExitedSafe(process);
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

    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int PosixKill(int pid, int sig);

    /// <summary>Send SIGTERM to <paramref name="pid"/> on Linux (the signal <c>pkill -x HermesProxy</c>
    /// sends). Returns true when the syscall reported success. No-op returning false off Linux — the
    /// caller then falls back to the hard kill.</summary>
    private static bool PosixSigterm(int pid)
    {
        if (!OperatingSystem.IsLinux()) return false;
        const int SIGTERM = 15;
        try { return PosixKill(pid, SIGTERM) == 0; }
        catch { return false; }
    }

    /// <summary>True if <paramref name="port"/> is still accepting connections after
    /// <paramref name="grace"/>. The grace exists because a proxy of ours that was just killed needs a
    /// moment to release the socket - reporting "in use" immediately would turn our own cleanup into a
    /// refusal to start.</summary>
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

    internal static string PortTakenMessage(int port, string? holder = null) =>
        holder is null
            ? $"Port {port} is already in use. The realm proxy needs it, and something else is holding it.\n" +
              "This is usually a proxy left running from a client started outside the launcher. Close it, " +
              "or run: pkill -x HermesProxy"
            : $"Port {port} is already in use by {holder}. The realm proxy needs this port.\n" +
              "Close that program, then press Play again.";

    internal static string FailedStartMessage(string fatalLine) =>
        "The realm proxy stopped while starting. It said: " + fatalLine + "\n" +
        (fatalLine.Contains("Press enter", StringComparison.Ordinal)
            ? "One of its network ports could not be opened. "
            : "") +
        "The proxy has been closed and its ports are free again. Press Play to try once more.";

    /// <summary>"llama-server (PID 1234)" for the process listening on <paramref name="port"/>, or null
    /// when it cannot be told (another user's process, no /proc, no lsof).</summary>
    private async Task<string?> DescribePortOwnerAsync(int port, CancellationToken ct)
    {
        try
        {
            var pid = await _portOwnerProbe(port, ct).ConfigureAwait(false);
            if (pid is null) return null;
            string name;
            try { using var p = Process.GetProcessById(pid.Value); name = p.ProcessName; }
            catch { return $"PID {pid.Value}"; }
            return $"{name} (PID {pid.Value})";
        }
        catch { return null; }
    }

    private async Task<bool> WaitForPortAsync(int port, TimeSpan timeout, Process process, CancellationToken ct,
        Func<int, CancellationToken, Task<bool>>? probe = null)
    {
        probe ??= _portProbe;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) return false; // died before ever opening the port - stop polling
            if (_fatalLine is not null) return false; // said it failed - its process may linger, it is dead
            if (await probe(port, ct).ConfigureAwait(false)) return true;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        return false;
    }

    private void KillStalePidFileEntry()
    {
        var entry = ProxyPidFile.Read(_pidFilePath);
        if (entry is null) { ProxyPidFile.Delete(_pidFilePath); return; }

        // Another launcher window is holding this proxy for a session that is running right now.
        // Killing it here disconnects a player mid-game — see ProxyPidFile for how that happened.
        if (ProxyPidFile.OwnerStillRunning(entry.Value))
        {
            _logger.Information(
                "Leaving the proxy (PID={Pid}) alone: another launcher instance (PID={Owner}) is using it",
                entry.Value.ProxyPid, entry.Value.OwnerPid);
            return;
        }

        var pid = entry.Value.ProxyPid;
        try
        {
            using var stale = Process.GetProcessById(pid);
            // Name-checked: GetProcessById alone cannot tell "our old proxy" from "whatever the OS
            // handed that PID to since" (a reboot recycles PIDs). Only kill a plausible match.
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

    // Records the proxy PID AND ours, so a second launcher window can tell "orphaned by a crash"
    // from "in use by a session that is running" (ProxyPidFile).
    private void WritePidFile(int pid) => ProxyPidFile.Write(_pidFilePath, pid, _logger);

    private void DeletePidFile()
    {
        try { if (File.Exists(_pidFilePath)) File.Delete(_pidFilePath); }
        catch { /* best effort */ }
    }
}
