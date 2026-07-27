namespace WowLauncher.Services.Platform;

using System.Diagnostics;
using System.Text;

/// <summary>
/// macOS <see cref="IWineHost"/> — runs the modern 1.14.2 client under the <b>free, redistributable</b>
/// Gcenx "Game Porting Toolkit" Wine (wine-7.7 + Apple's D3DMetal), NOT CrossOver.
///
/// <para><b>Why GPTK and not CrossOver (revises the 2026-07-17 macOS dossier).</b> CrossOver is a
/// per-user paid licence and cannot ship inside the .app — that was the blocker behind the dossier's
/// "steer a user-installed CrossOver" design. The Gcenx GPTK build is a plain tarball
/// (<c>game-porting-toolkit-3.0-2.tar.xz</c>, ~250 MB, ad-hoc signed) that carries wine64 + wineserver +
/// <c>D3DMetal.framework</c> and is redistributable, so the launcher can bundle it and ship ONE
/// notarised DMG for every Mac player. Proven 2026-07-23: this exact host started
/// <c>WowClassic_ForCustomServers.exe</c> (build 42597) and reached the in-world state on Stonetavern.</para>
///
/// <para><b>Rosetta.</b> GPTK's wine64 is an x86_64 binary, so every invocation goes through
/// <c>arch -x86_64</c> (Rosetta 2). WoW uses AVX, which Rosetta only advertises when
/// <c>ROSETTA_ADVERTISE_AVX=1</c> is set — without it the client faults early. This is the single
/// long-term risk: Apple has announced Rosetta will narrow to "older games" only. No action needed now.</para>
///
/// <para><b>Graphics.</b> <c>WINEDLLOVERRIDES=d3d12=b</c> forces Wine's builtin d3d12, which routes to
/// GPTK's D3DMetal (D3D12 → Metal). The client's own <c>gxApi D3D12</c> (set by <see cref="WtfConfigWriter"/>)
/// must stay — DXVK/D3D11 renders the world but drops the entire UI layer.</para>
///
/// Mirror of <see cref="WineGameLauncher"/>'s IWineHost surface (readiness probe, RunAsync,
/// ToWindowsPathAsync) so <see cref="ModernClientLauncher"/> can drive it unchanged. See the macOS
/// handoff for the one Arctium caveat (Arctium's in-memory patch hangs under GPTK-Wine → prefer the
/// pre-patched <c>WowClassic_ForCustomServers.exe</c> started directly).
/// </summary>
public sealed class MacWineHost : IWineHost
{
    // GPTK's wine64 is x86_64; run everything through Rosetta.
    private const string Rosetta = "arch";
    private static readonly string[] RosettaPrefix = { "-x86_64" };

    private readonly Serilog.ILogger _logger;
    private readonly WineOptions _options;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private bool _prefixReady;

    public MacWineHost(Serilog.ILogger logger, WineOptions options)
    {
        _logger = logger;
        _options = options;
    }

    /// <summary>Resolve GPTK's wine64. Prefers the copy bundled in the .app
    /// (<c>&lt;bundle&gt;/Contents/Resources/wine/bin/wine64</c>), then a user-installed
    /// "Game Porting Toolkit.app" under /Applications, then the launcher's data dir. Returns null when
    /// none is found — the caller surfaces an install prompt rather than a Wine error.</summary>
    public static string? ResolveWine64(string shareDir, string? bundleResourcesDir = null)
    {
        var candidates = new List<string>();
        if (bundleResourcesDir is not null)
            candidates.Add(Path.Combine(bundleResourcesDir, "wine", "bin", "wine64"));
        candidates.Add(Path.Combine(shareDir, "gptk", "Game Porting Toolkit.app",
            "Contents", "Resources", "wine", "bin", "wine64"));
        candidates.Add("/Applications/Game Porting Toolkit.app/Contents/Resources/wine/bin/wine64");
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<GameLaunchResult> RunAsync(
        string exePath, string workingDirectory, IReadOnlyList<string> args)
    {
        if (!await EnsurePrefixAsync().ConfigureAwait(false))
            return GameLaunchResult.Failed("Wine-Prefix (GPTK) konnte nicht initialisiert werden.");
        if (!File.Exists(exePath))
            return GameLaunchResult.Failed($"Programm nicht gefunden: {exePath}");

        try
        {
            var psi = NewWineProcess(exePath, args);
            psi.WorkingDirectory = workingDirectory;

            using var process = Process.Start(psi);
            if (process is null)
                return GameLaunchResult.Failed("Process.Start returned null");

            _logger.Information("Client via GPTK-Wine gestartet (PID={Pid}, prefix={Prefix})",
                process.Id, _options.PrefixPath);
            return GameLaunchResult.Ok(process.Id);
        }
        catch (Exception ex)
        {
            _logger.Fatal(ex, "GPTK-Wine-Start fehlgeschlagen: {Exe}", exePath);
            return GameLaunchResult.Failed($"GPTK-Wine-Start fehlgeschlagen: {ex.Message}");
        }
    }

    public async Task<string?> ToWindowsPathAsync(string unixPath)
    {
        if (!await EnsurePrefixAsync().ConfigureAwait(false))
            return null;
        try
        {
            var psi = NewWineProcess(_options.WineBinary, new[] { "winepath", "-w", unixPath },
                overrideExeWithWine: false, captureStdout: true);
            using var process = Process.Start(psi);
            if (process is null) return null;
            // Bound the wait like the prefix init: a wedged winepath must not block the caller forever
            // (Codex review). On timeout, kill it and return null so the caller reports a resolvable
            // error rather than hanging.
            using var cts = new CancellationTokenSource(_options.EffectiveProbeTimeout);
            var output = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var win = output.Trim();
            return string.IsNullOrEmpty(win) ? null : win;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "winepath -w fehlgeschlagen für {Path}", unixPath);
            return null;
        }
    }

    /// <summary>One-time <c>wineboot -u</c> for the modern prefix (prefix-modern), gated so concurrent
    /// first launches don't race. Verifies <c>system.reg</c> appears.</summary>
    private async Task<bool> EnsurePrefixAsync()
    {
        if (_prefixReady) return true;
        await _probeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_prefixReady) return true;
            var systemReg = Path.Combine(_options.PrefixPath, "system.reg");
            if (!File.Exists(systemReg))
            {
                Directory.CreateDirectory(_options.PrefixPath);
                var psi = NewWineProcess(_options.WineBinary, new[] { "wineboot", "-u" },
                    overrideExeWithWine: false);
                using var boot = Process.Start(psi);
                if (boot is not null)
                {
                    // Bound the wait: a hung wineboot (a bad prefix, a wedged wineserver) must not wedge
                    // every launch forever. WineOptions already carries a probe timeout for exactly this;
                    // on timeout, kill the process tree and fall through to the system.reg check, which
                    // will report the prefix as not ready rather than block indefinitely (Codex review).
                    using var cts = new CancellationTokenSource(_options.EffectiveProbeTimeout);
                    try
                    {
                        await boot.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.Error("wineboot -u timed out after {Timeout}s ({Prefix}); killing it",
                            _options.EffectiveProbeTimeout.TotalSeconds, _options.PrefixPath);
                        try { boot.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    }
                }
            }
            _prefixReady = File.Exists(systemReg);
            if (!_prefixReady)
                _logger.Error("system.reg fehlt nach wineboot -u ({Prefix})", _options.PrefixPath);
            return _prefixReady;
        }
        finally { _probeGate.Release(); }
    }

    /// <summary>Build the Rosetta→wine64 ProcessStartInfo with the proven env. When
    /// <paramref name="overrideExeWithWine"/> is true, <paramref name="exeOrTarget"/> is the Windows
    /// program to run under wine (wine64 is inserted); otherwise it IS the wine64 binary and
    /// <paramref name="args"/> are wine's own args (e.g. winepath / wineboot).</summary>
    private ProcessStartInfo NewWineProcess(
        string exeOrTarget, IReadOnlyList<string> args,
        bool overrideExeWithWine = true, bool captureStdout = false)
    {
        var psi = new ProcessStartInfo(Rosetta)
        {
            UseShellExecute = false,
            RedirectStandardOutput = captureStdout,
        };
        foreach (var a in RosettaPrefix) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(_options.WineBinary);          // GPTK wine64
        if (overrideExeWithWine) psi.ArgumentList.Add(exeOrTarget);   // the Windows .exe
        foreach (var a in args) psi.ArgumentList.Add(a);

        psi.Environment["WINEPREFIX"] = _options.PrefixPath;
        psi.Environment["WINEDEBUG"] = "-all";
        psi.Environment["WINEESYNC"] = "1";
        psi.Environment["WINEDLLOVERRIDES"] = "d3d12=b";     // builtin d3d12 → GPTK D3DMetal
        psi.Environment["ROSETTA_ADVERTISE_AVX"] = "1";      // WoW needs AVX under Rosetta
        psi.Environment["MTL_HUD_ENABLED"] = "0";
        psi.Environment["MVK_CONFIG_SYNCHRONOUS_QUEUE_SUBMITS"] = "1";
        return psi;
    }
}
