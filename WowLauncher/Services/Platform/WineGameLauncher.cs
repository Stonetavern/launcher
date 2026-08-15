namespace WowLauncher.Services.Platform;

using System.ComponentModel;
using System.Diagnostics;

/// <summary>
/// Which runtime shape a client needs, and therefore what "this prefix is ready" has to mean. The
/// two builds fail in opposite directions, so one shared check would be wrong for one of them:
/// 1.12 is a 32-bit D3D9 PE that dies without a wow64 layer and does not care about Vulkan, while
/// 1.14.2 is 64-bit and reaches the world only through D3D12/vkd3d, for which syswow64 says nothing.
/// </summary>
public enum ClientRuntime
{
    /// <summary>1.12.1 and the other 32-bit builds: system Wine, wow64 required, D3D9 only.</summary>
    Legacy32,

    /// <summary>1.14.2 Classic Era: 64-bit, needs a Wine build carrying D3D12 (wine-ge) and a host
    /// Vulkan driver.</summary>
    Modern64,
}

/// <summary>
/// Immutable configuration for the launcher's managed Wine environment. The prefix path is
/// injectable so tests can point at a throwaway TEMP prefix and never touch the real one
/// (WP2 proof requirement); the wine binary defaults to "wine" (resolved via PATH).
/// </summary>
public sealed record WineOptions(
    string PrefixPath,
    string WineBinary = "wine",
    TimeSpan? ProbeTimeout = null,
    ClientRuntime Runtime = ClientRuntime.Legacy32)
{
    /// <summary>The managed prefix under the launcher's data directory: <c>&lt;ShareDir&gt;/prefix</c>
    /// (Linux: <c>~/.local/share/stonetavern-launcher/prefix</c>). Sourced from <see cref="IAppPaths"/>
    /// (WP3) so it honours the XDG overrides consistently with every other launcher path. Never
    /// <c>~/.wine</c> — the launcher manages its own prefix and never hijacks the user's default or a
    /// Lutris/Steam one (PLAN §1.3).</summary>
    public static string DefaultPrefixPath() => Path.Combine(AppPaths.ForCurrentOs().ShareDir, "prefix");

    /// <summary>Build options for a given launcher data directory (WP3: DI passes the injected
    /// <see cref="IAppPaths.ShareDir"/>).</summary>
    public static WineOptions ForShareDir(string shareDir) => new(Path.Combine(shareDir, "prefix"));

    /// <summary>Options for the modern 1.14.2 client: its OWN prefix, booted by the given wine-ge
    /// binary. A separate prefix rather than the legacy one because the two are initialised by
    /// different Wine builds - booting one prefix with two Wine versions is how a prefix ends up in a
    /// half-migrated state that no probe reports and that shows up as a client which starts and
    /// renders nothing.</summary>
    public static WineOptions ModernForShareDir(string shareDir, string wineBinary) =>
        new(Path.Combine(shareDir, "prefix-modern"), wineBinary, Runtime: ClientRuntime.Modern64);

    public static WineOptions Default() => ForShareDir(AppPaths.ForCurrentOs().ShareDir);

    /// <summary>Wall-clock ceiling for the one-time <c>wineboot -u</c> prefix init. A fresh prefix
    /// on a slow disk can take a while; a hung wineboot must not wedge the launcher forever.</summary>
    public TimeSpan EffectiveProbeTimeout => ProbeTimeout ?? TimeSpan.FromSeconds(120);
}

/// <summary>
/// A Wine environment something else can run a Windows program in: start an arbitrary executable with
/// arguments, and translate a Unix path into the Windows path that program will understand.
///
/// <para>Exists because the modern client is NOT started by running its own exe. It is started through
/// the Arctium launcher, which takes arguments and a Windows path, so the step that orchestrates that
/// (<see cref="ModernClientLauncher"/>) needs more than <see cref="IGameLauncher.LaunchAsync"/>
/// offers - and it needs it against the same prefix, with the same readiness probe already done, which
/// is exactly what <see cref="WineGameLauncher"/> owns.</para>
/// </summary>
public interface IWineHost
{
    /// <summary>Start <paramref name="exePath"/> under Wine with <paramref name="args"/>. Runs the
    /// readiness probe first, like a normal launch. Never throws.</summary>
    Task<GameLaunchResult> RunAsync(string exePath, string workingDirectory, IReadOnlyList<string> args);

    /// <summary>The Windows path (<c>Z:\...</c>) this prefix maps <paramref name="unixPath"/> to, via
    /// <c>wine winepath -w</c>, or null when it cannot be resolved. Used instead of mapping a drive
    /// letter into the prefix: no prefix symlink to create, and spaces survive.</summary>
    Task<string?> ToWindowsPathAsync(string unixPath);
}

/// <summary>Outcome of the one-time Wine readiness probe. On success carries the resolved wine
/// binary so the launch step reuses exactly what the probe validated.</summary>
internal sealed record WineReadiness(bool Ok, string? WineBinary = null, string? Error = null)
{
    public static WineReadiness Ready(string wineBinary) => new(true, wineBinary);
    public static WineReadiness NotReady(string error) => new(false, null, error);
}

/// <summary>
/// Linux client launcher: WoW 1.12 is a 32-bit Windows PE, so it runs under <b>system Wine</b> in a
/// launcher-managed prefix (PLAN §1.2/§1.3). Readiness is a <b>functional probe</b> — not a bare
/// PATH check: it resolves the wine binary, confirms <c>wine --version</c> answers, then initialises
/// the managed prefix once via <c>wineboot -u</c> and verifies <c>system.reg</c> exists. The result
/// is cached per prefix. The start itself is <c>wine &lt;absolute WoW.exe&gt;</c> with the exe's
/// directory as cwd, <c>UseShellExecute=false</c>, no shell, minimal env (WINEPREFIX + WINEDEBUG=-all;
/// WINEDLLOVERRIDES left at default for vanilla 1.12). Never throws — every failure is a
/// <see cref="GameLaunchResult"/> with an actionable message.
/// </summary>
public sealed class WineGameLauncher : IGameLauncher, IWineHost
{
    private readonly Serilog.ILogger _logger;
    private readonly WineOptions _options;

    // The functional probe is expensive (wineboot spins up a wineserver) — run it at most once per
    // prefix. The gate serialises concurrent first launches; the cached success is reused thereafter.
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private WineReadiness? _cachedReady;

    /// <summary>
    /// Welche Wine JETZT gilt, bei jedem Start neu gefragt.
    ///
    /// <para>🔴 Warum das eine Funktion ist und kein Wert (Befund einer Zweitinstanz, 2026-08-05).
    /// Dieser Launcher ist ein Singleton, und die Laufzeit wurde beim Aufbau der Anwendung
    /// <b>einmal</b> aus der Konfiguration gelesen. Die Einstellungsseite speichert dagegen sofort
    /// und rechnete ihre Statuszeile sofort neu aus. Ergebnis: ein Spieler stellt abends „eigener
    /// Pfad" ein, liest darunter „In Benutzung: /opt/proton/…/wine", drückt SPIELEN — und startet mit
    /// dem Runner vom Programmstart. Kein Fehler, keine Logzeile, kein roter Test.</para>
    ///
    /// <para>Das ist exakt die Fehlerform, gegen die diese Einstellung überhaupt gebaut wurde: ein
    /// plausibler Zustand, der falsch ist. Deshalb wird die Wahl jetzt beim Start aufgelöst, und die
    /// Bereitschaftsprüfung merkt sich, FÜR WELCHE Binärdatei sie galt.</para>
    /// </summary>
    private readonly Func<string>? _binaryNow;

    public WineGameLauncher(Serilog.ILogger logger, WineOptions options, Func<string>? binaryNow = null)
    {
        _logger = logger;
        _options = options;
        _binaryNow = binaryNow;
    }

    /// <summary>Die Binärdatei für diesen Start. Ein leeres Ergebnis des Auflösers zählt als „nichts
    /// gefunden" und fällt auf die gebaute Option zurück, statt einen leeren Pfad zu starten.</summary>
    private string CurrentBinary
    {
        get
        {
            var now = _binaryNow?.Invoke();
            return string.IsNullOrWhiteSpace(now) ? _options.WineBinary : now;
        }
    }

    public Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory) =>
        RunAsync(exePath, workingDirectory, []);

    /// <summary><see cref="IWineHost.RunAsync"/>. The plain launch above is this with no arguments -
    /// one code path, so the readiness probe, the env and the error handling cannot drift apart
    /// between "start the client" and "start the thing that starts the client".</summary>
    public async Task<GameLaunchResult> RunAsync(
        string exePath, string workingDirectory, IReadOnlyList<string> args)
    {
        var ready = await EnsureReadyAsync().ConfigureAwait(false);
        if (!ready.Ok)
            return GameLaunchResult.Failed(ready.Error!);

        if (!File.Exists(exePath))
            // Name the file that is actually missing: for 1.14.2 this is WowClassic.exe, and a message
            // saying "WoW.exe not found" sends the player looking for a file that never existed.
            return GameLaunchResult.Failed($"Client executable not found: {exePath}");

        try
        {
            var psi = new ProcessStartInfo(ready.WineBinary!)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(exePath);
            foreach (var a in args) psi.ArgumentList.Add(a);
            ApplyWineEnv(psi);

            // Dispose the local Process handle (does NOT terminate the spawned game — it only frees our
            // handle); we hand the PID onward. Windows behaviour is unchanged: the OS process lives on.
            using var process = Process.Start(psi);
            if (process is null)
            {
                _logger.Fatal("Wine start failed: Process.Start returned null");
                return GameLaunchResult.Failed("Process.Start returned null");
            }

            var pid = process.Id;
            _logger.Information(
                "WoW started via Wine (PID={Pid}, prefix={Prefix})", pid, _options.PrefixPath);
            return GameLaunchResult.Ok(pid);
        }
        catch (Exception ex)
        {
            _logger.Fatal(ex, "Wine start failed: {Exe}", exePath);
            return GameLaunchResult.Failed($"Wine start failed: {ex.Message}");
        }
    }

    /// <summary><see cref="IWineHost.ToWindowsPathAsync"/>. Runs the readiness probe first: winepath
    /// itself needs an initialised prefix, and calling it on an uninitialised one both fails and
    /// silently triggers a prefix creation nobody asked for at that moment.</summary>
    public async Task<string?> ToWindowsPathAsync(string unixPath)
    {
        var ready = await EnsureReadyAsync().ConfigureAwait(false);
        if (!ready.Ok) return null;

        try
        {
            var (exit, stdout) = await RunCapturedAsync(
                ready.WineBinary!, ["winepath", "-w", unixPath], WineEnv(), TimeSpan.FromSeconds(30))
                .ConfigureAwait(false);
            if (exit != 0) return null;

            // winepath prints the path plus a newline; anything else (an empty answer, a wine warning
            // on its own line) is not a path and must not be handed on as one.
            var line = stdout.Trim();
            return line.Length == 0 || line.Contains('\n') ? null : line;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "winepath failed for {Path}", unixPath);
            return null;
        }
    }

    /// <summary>Run (or reuse) the one-time functional readiness probe for this prefix.</summary>
    /// <inheritdoc/>
    public async Task<string?> CheckReadyAsync(string exeName)
    {
        var ready = await EnsureReadyAsync().ConfigureAwait(false);
        return ready.Ok ? null : (ready.Error ?? "The game cannot be started on this machine.");
    }

    private async Task<WineReadiness> EnsureReadyAsync()
    {
        // Der Cache gilt nur fuer die Binaerdatei, mit der er entstanden ist. Ohne diesen Vergleich
        // haette die neue Einstellung erst nach einem Neustart gewirkt - und die Statuszeile haette
        // in der Zwischenzeit das Gegenteil behauptet.
        var wanted = CurrentBinary;
        if (_cachedReady is { Ok: true } cached && cached.WineBinary == wanted)
            return cached;

        await _probeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cachedReady is { Ok: true } again && again.WineBinary == wanted)
                return again;

            var probed = await ProbeAsync().ConfigureAwait(false);
            // Cache only success: a failure may be transient (wine just installed, disk freed) — a
            // later launch should re-probe rather than stay poisoned.
            if (probed.Ok)
                _cachedReady = probed;
            return probed;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    private async Task<WineReadiness> ProbeAsync()
    {
        // (1) Detection: resolve wine + confirm it actually answers. `wine --version` failing with a
        // Win32Exception (ENOENT) means wine isn't installed → actionable distro hint.
        string wineBinary = CurrentBinary;
        try
        {
            var (exit, stdout) = await RunCapturedAsync(
                wineBinary, ["--version"], env: null, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (exit != 0)
                return WineReadiness.NotReady(WineMissingMessage());
            _logger.Information("Wine detected: {Version}", stdout.Trim());
        }
        catch (Win32Exception)
        {
            return WineReadiness.NotReady(WineMissingMessage());
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Wine version check failed");
            return WineReadiness.NotReady(WineMissingMessage());
        }

        // (2) Functional prefix probe: create + initialise the managed prefix with `wineboot -u`,
        // then verify system.reg exists (a bare PATH check would not catch a broken/read-only prefix).
        try
        {
            Directory.CreateDirectory(_options.PrefixPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not create Wine prefix directory: {Prefix}", _options.PrefixPath);
            return WineReadiness.NotReady(
                $"Could not create the Wine prefix ({_options.PrefixPath}): {ex.Message}");
        }

        var env = WineEnv();
        try
        {
            var (exit, _) = await RunCapturedAsync(
                wineBinary, ["wineboot", "-u"], env, _options.EffectiveProbeTimeout).ConfigureAwait(false);
            if (exit != 0)
                return WineReadiness.NotReady(
                    "Wine prefix initialization failed (wineboot returned an error). " +
                    "Details are in the launcher log.");
        }
        catch (TimeoutException)
        {
            // A wineboot that overran leaves a detached wineserver for THIS managed prefix. Tear it down
            // — scoped strictly to the prefix via WINEPREFIX in env, never a global `wineserver -k`.
            await KillPrefixServerAsync(wineBinary, env).ConfigureAwait(false);
            return WineReadiness.NotReady(
                "Wine prefix initialization timed out. Please try again.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "wineboot -u failed");
            return WineReadiness.NotReady($"Wine prefix initialization failed: {ex.Message}");
        }

        // "wineboot exit 0" is NOT proof the prefix is done: wineserver persists the registry
        // (system.reg) only when it flushes on idle — checking system.reg right after wineboot races
        // and finds nothing ("grün ist kein Beweis", CORE §3). `wineserver -w` blocks until the boot
        // server terminates and has flushed; we then still poll briefly as a belt-and-braces guard.
        await WaitForRegistryFlushAsync(wineBinary, env).ConfigureAwait(false);

        var systemReg = Path.Combine(_options.PrefixPath, "system.reg");
        if (!await PollForFileAsync(systemReg, TimeSpan.FromSeconds(15)).ConfigureAwait(false))
            return WineReadiness.NotReady(
                "Wine prefix initialization is incomplete (system.reg is missing). Please try again.");

        // (3) Capability check — which one depends on the CLIENT, not on the OS. Running the 32-bit
        // check for 1.14.2 would pass on a Wine build with no D3D12 at all and hand the player a client
        // that starts and never draws; running the D3D12 check for 1.12 would reject a perfectly good
        // plain-Wine setup. Same reason the check may not simply be dropped for the modern client
        // (HANDOFF 2026-07-22 §5 WP2): it has to become the RIGHT check, not no check.
        var capability = _options.Runtime switch
        {
            ClientRuntime.Modern64 => await VerifyModernRuntimeAsync(wineBinary).ConfigureAwait(false),
            _ => await Verify32BitSupportAsync(wineBinary, env).ConfigureAwait(false),
        };
        if (!capability.Ok)
            return capability;

        _logger.Information("Wine prefix ready: {Prefix}", _options.PrefixPath);
        return WineReadiness.Ready(wineBinary);
    }

    /// <summary>Prove the managed prefix can actually run a 32-bit PE (WoW 1.12 is 32-bit). Minimal
    /// check: the prefix has a <c>syswow64</c> directory (absent ⇒ no wow64/32-bit layer). Full proof:
    /// start a real 32-bit system binary from that directory — <c>syswow64/cmd.exe</c>, present in every
    /// initialised prefix — with <c>/c exit 0</c> and confirm it exits 0. We never fabricate a test
    /// binary from bytes (AGENTS invariant: no embedded binaries); if no suitable 32-bit system binary
    /// exists, the syswow64 directory existence stands as the minimal check.</summary>
    private async Task<WineReadiness> Verify32BitSupportAsync(
        string wineBinary, IReadOnlyDictionary<string, string> env)
    {
        var syswow64 = Path.Combine(_options.PrefixPath, "drive_c", "windows", "syswow64");
        bool syswow64Exists;
        try { syswow64Exists = Directory.Exists(syswow64); }
        catch { syswow64Exists = false; }
        if (!syswow64Exists)
        {
            _logger.Error("Wine prefix has no syswow64: no 32-bit support: {Prefix}", _options.PrefixPath);
            return WineReadiness.NotReady(WineNo32BitMessage());
        }

        var cmd32 = Path.Combine(syswow64, "cmd.exe");
        if (!File.Exists(cmd32))
        {
            // wineboot creates syswow64/cmd.exe in every working wow64 prefix. If it is missing,
            // the 32-bit layer is incomplete and a WoW 1.12 start is unproven. Without full proof
            // the prefix counts as NotReady (Codex gate: Ready without the probe would be a silent failure path).
            _logger.Error(
                "syswow64 present but without cmd.exe: 32-bit layer incomplete, prefix not ready: {Prefix}",
                _options.PrefixPath);
            return WineReadiness.NotReady(WineNo32BitMessage());
        }

        try
        {
            var (exit, _) = await RunCapturedAsync(
                wineBinary, [cmd32, "/c", "exit", "0"], env, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            if (exit != 0)
            {
                _logger.Error(
                    "32-bit probe failed: syswow64/cmd.exe exited with {Exit}: Wine has no 32-bit support", exit);
                return WineReadiness.NotReady(WineNo32BitMessage());
            }
            _logger.Information("Wine 32-bit support confirmed (syswow64/cmd.exe started, exit 0)");
            return WineReadiness.Ready(wineBinary);
        }
        catch (TimeoutException)
        {
            _logger.Error("32-bit probe timed out: Wine has no working 32-bit support");
            return WineReadiness.NotReady(WineNo32BitMessage());
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "32-bit probe failed");
            return WineReadiness.NotReady(WineNo32BitMessage());
        }
    }

    /// <summary>Prove the managed prefix can actually run the MODERN client (1.14.2): the Wine build
    /// must carry a D3D12 layer, and the host must have a Vulkan driver new enough for vkd3d to map
    /// D3D12 onto. Both are checked before the client starts, because both fail the same silent way -
    /// the client launches, shows no window or drops out on world entry, and nothing is logged that
    /// names the cause (HANDOFF 2026-07-22 §5 WP3).
    ///
    /// <para>The D3D12 check reads the booted prefix rather than the runner directory: what matters is
    /// what this prefix will load, and a prefix booted by plain system Wine has no D3D12 even when a
    /// wine-ge runner exists elsewhere on the machine.</para></summary>
    private Task<WineReadiness> VerifyModernRuntimeAsync(string wineBinary)
    {
        var system32 = Path.Combine(_options.PrefixPath, "drive_c", "windows", "system32");
        var d3d12 = Path.Combine(system32, "d3d12.dll");
        bool hasD3D12;
        try { hasD3D12 = File.Exists(d3d12); }
        catch { hasD3D12 = false; }

        if (!hasD3D12)
        {
            _logger.Error(
                "Modern client prefix has no d3d12.dll: this Wine build cannot render the 1.14.2 client ({Wine}, prefix={Prefix})",
                wineBinary, _options.PrefixPath);
            return Task.FromResult(WineReadiness.NotReady(NoD3D12Message()));
        }

        var vulkan = VulkanProbe.ForCurrentMachine();
        _logger.Information(
            "Vulkan check: driverFound={Found}, apiVersion={Version}, source={Source}",
            vulkan.DriverFound, vulkan.ApiVersion?.ToString() ?? "unknown", vulkan.Source);

        if (!vulkan.MeetsModernClientRequirement)
            return Task.FromResult(WineReadiness.NotReady(VulkanMessage(vulkan)));

        _logger.Information(
            "Modern client prefix ready: d3d12.dll present, Vulkan {Version} ({Prefix})",
            vulkan.ApiVersion, _options.PrefixPath);
        return Task.FromResult(WineReadiness.Ready(wineBinary));
    }

    /// <summary>Minimal Wine env for the launch: managed prefix + quiet debug channel. WINEDLLOVERRIDES
    /// is intentionally left at Wine's default — vanilla 1.12 needs no overrides.</summary>
    private void ApplyWineEnv(ProcessStartInfo psi)
    {
        foreach (var (k, v) in WineEnv())
            psi.Environment[k] = v;
    }

    private IReadOnlyDictionary<string, string> WineEnv() => new Dictionary<string, string>
    {
        ["WINEPREFIX"] = _options.PrefixPath,
        ["WINEDEBUG"] = "-all",
    };

    /// <summary>Run a process to completion, capturing stdout, with a hard timeout (kills the tree on
    /// overrun). Inherits the parent env (so DISPLAY/HOME survive) and layers <paramref name="env"/>
    /// on top. Throws <see cref="TimeoutException"/> on overrun; a missing binary throws
    /// <see cref="Win32Exception"/> from Process.Start (caller distinguishes it).</summary>
    private static async Task<(int ExitCode, string Stdout)> RunCapturedAsync(
        string fileName, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Process.Start returned null for {fileName}");

        // Drain stdout/stderr concurrently so a chatty child can't dead-lock on a full pipe buffer.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                // Reap the killed tree so we don't leak a zombie / a still-detaching child. Bounded wait
                // (fresh token — the timeout token is already cancelled) so cleanup can't itself hang.
                using var reapCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(reapCts.Token).ConfigureAwait(false);
            }
            catch { /* already gone, or reap timed out — best effort */ }
            throw new TimeoutException($"{fileName} exceeded the timeout of {timeout.TotalSeconds:F0}s.");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        return (process.ExitCode, stdout);
    }

    /// <summary>Wait for the boot wineserver to flush the registry to disk. Best-effort: <c>wineserver
    /// -w</c> blocks until that server terminates (persisting system.reg on the way out). Any failure
    /// is swallowed — the subsequent <see cref="PollForFileAsync"/> is the actual gate.</summary>
    private async Task WaitForRegistryFlushAsync(string wineBinary, IReadOnlyDictionary<string, string> env)
    {
        var wineserver = ResolveSibling(wineBinary, "wineserver");
        try
        {
            await RunCapturedAsync(wineserver, ["-w"], env, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "wineserver -w failed (best-effort), falling back to file poll");
        }
    }

    /// <summary>Tear down a possibly-detached wineserver for THIS managed prefix ONLY after an init
    /// overrun. Scope is the prefix in <paramref name="env"/> (WINEPREFIX) — this is never a global
    /// <c>wineserver -k</c> (AGENTS gate: only the launcher's own / test prefix may be killed).</summary>
    private async Task KillPrefixServerAsync(string wineBinary, IReadOnlyDictionary<string, string> env)
    {
        var wineserver = ResolveSibling(wineBinary, "wineserver");
        try
        {
            await RunCapturedAsync(wineserver, ["-k"], env, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            _logger.Information("Ran wineserver -k against prefix {Prefix} after init timeout", _options.PrefixPath);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "wineserver -k (prefix cleanup after timeout) failed, best effort");
        }
    }

    /// <summary>A sibling executable next to the resolved wine binary (e.g. wineserver alongside an
    /// absolute wine path); falls back to the bare name so PATH resolution still applies.</summary>
    private static string ResolveSibling(string wineBinary, string sibling)
    {
        try
        {
            var dir = Path.GetDirectoryName(wineBinary);
            if (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, sibling);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { /* fall through to bare name */ }
        return sibling;
    }

    private static async Task<bool> PollForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (File.Exists(path)) return true;
            if (DateTime.UtcNow >= deadline) return File.Exists(path);
            await Task.Delay(250).ConfigureAwait(false);
        }
    }

    private static string WineMissingMessage() =>
        "Wine is not installed or not in PATH. WoW 1.12 is a Windows application and needs Wine.\n" +
        "Fedora:        sudo dnf install wine\n" +
        "Ubuntu/Debian: sudo apt install wine\n" +
        "Arch:          sudo pacman -S wine\n" +
        "openSUSE:      sudo zypper install wine";

    private static string NoD3D12Message() =>
        "This Wine build cannot run the 1.14.2 client. It renders through D3D12, which plain system " +
        "Wine does not provide.\n" +
        "Install a wine-ge runner through Lutris, or install umu-launcher so the launcher can fall " +
        "back to Proton:\n" +
        "Lutris:  https://lutris.net/downloads\n" +
        "umu:     https://github.com/Open-Wine-Components/umu-launcher";

    private static string VulkanMessage(VulkanCapability vulkan) =>
        vulkan.DriverFound
            ? $"The graphics driver on this machine reports Vulkan {vulkan.ApiVersion}. The 1.14.2 " +
              "client needs Vulkan 1.3 or newer, because it renders through D3D12 and Wine maps that " +
              "onto Vulkan.\nUpdating the graphics driver is the usual fix. The 1.12.1 client does " +
              "not need Vulkan and still works."
            : "No Vulkan driver was found on this machine. The 1.14.2 client renders through D3D12, " +
              "which needs Vulkan 1.3 or newer.\nInstall the Vulkan driver for your graphics card " +
              "(Mesa for AMD and Intel, the NVIDIA driver for NVIDIA). The 1.12.1 client does not " +
              "need Vulkan and still works.";

    private static string WineNo32BitMessage() =>
        "Wine has no 32-bit support. Please install wine with wow64.\n" +
        "WoW 1.12 is a 32-bit Windows application, this Wine prefix cannot run 32-bit programs.\n" +
        "Fedora:        sudo dnf install wine   (includes wow64/32-bit support)\n" +
        "Ubuntu/Debian: sudo dpkg --add-architecture i386 && sudo apt install wine32:i386\n" +
        "Arch:          enable the multilib repo, then sudo pacman -S wine\n" +
        "openSUSE:      sudo zypper install wine (32-bit package)";
}
