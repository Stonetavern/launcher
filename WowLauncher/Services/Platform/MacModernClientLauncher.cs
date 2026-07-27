namespace WowLauncher.Services.Platform;

/// <summary>
/// Where every part of a <b>macOS</b> 1.14.2 (Classic Era) client bundle sits, derived from the ONE path
/// the launcher already resolves: <c>WowClassic.exe</c>. The macOS package mirrors the Windows shape,
/// laid down by the packaging step and proven by <c>Play-Stonetavern-FreeWine.command</c>:
///
/// <code>
/// &lt;root&gt;/Hermes/HermesProxy                        native macOS proxy (universal binary), config beside it
/// &lt;root&gt;/World of Warcraft/_classic_era_/WowClassic.exe                 the retail-named client
/// &lt;root&gt;/World of Warcraft/_classic_era_/WowClassic_ForCustomServers.exe the exe actually launched
/// &lt;root&gt;/World of Warcraft/_classic_era_/WTF/Config.wtf
/// </code>
///
/// <para><b>What differs from the Windows <see cref="WindowsModernClientLayout"/> (the two are otherwise
/// the same shape):</b> the proxy is <c>Hermes/HermesProxy</c> — a native Mach-O universal binary, not
/// <c>JimsProxy.exe</c>. Like Windows and unlike Linux there is no Arctium: the pre-patched
/// <c>WowClassic_ForCustomServers.exe</c> is started directly; under the free Gcenx Game Porting Toolkit
/// Wine it runs, whereas the retail-named <c>WowClassic.exe</c> ACCESS_VIOLATEs (Arctium's in-memory patch
/// is what the Linux path needs, and it hangs under GPTK-Wine — proven 2026-07-23, so macOS uses the
/// pre-patched exe directly).</para>
/// </summary>
public sealed record MacModernClientLayout(
    string BundleRoot,
    string ClientExe,          // WowClassic.exe — the resolved/detected client
    string CustomServerExe,    // WowClassic_ForCustomServers.exe — the exe actually started
    string ClientDir,
    string ProxyExe,
    string ProxyDir,
    string ConfigWtf)
{
    /// <summary>The native macOS HermesProxy binary (no <c>.exe</c>; a Mach-O universal binary).</summary>
    public const string ProxyExeName = "HermesProxy";
    public const string CustomServerExeName = "WowClassic_ForCustomServers.exe";

    /// <summary>Derive the macOS layout from the resolved client exe, or null when the exe is not sitting
    /// in a bundle of this shape. Null is a legitimate answer, not a failure: the caller turns it into a
    /// sentence naming what is missing rather than guessing at paths that are not there.</summary>
    public static MacModernClientLayout? Resolve(string clientExePath)
    {
        try
        {
            var clientDir = Path.GetDirectoryName(Path.GetFullPath(clientExePath));   // .../_classic_era_
            if (clientDir is null) return null;
            var wowDir = Path.GetDirectoryName(clientDir);                            // .../World of Warcraft
            if (wowDir is null) return null;
            var root = Path.GetDirectoryName(wowDir);                                 // bundle root
            if (root is null) return null;

            var proxyDir = Path.Combine(root, "Hermes");

            return new MacModernClientLayout(
                BundleRoot: root,
                ClientExe: Path.GetFullPath(clientExePath),
                CustomServerExe: Path.Combine(clientDir, CustomServerExeName),
                ClientDir: clientDir,
                ProxyExe: Path.Combine(proxyDir, ProxyExeName),
                ProxyDir: proxyDir,
                ConfigWtf: Path.Combine(clientDir, "WTF", "Config.wtf"));
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Starts the modern 1.14.2 client on <b>macOS</b>, the way <c>Play-Stonetavern-FreeWine.command</c> proves
/// it starts: native HermesProxy first, then <c>WowClassic_ForCustomServers.exe</c> directly under the
/// free Gcenx Game Porting Toolkit Wine (via <see cref="IWineHost"/>) — no CrossOver, no Arctium. It is a
/// hybrid of the two other modern launchers, and each choice is there because the alternative fails:
///
/// <list type="number">
/// <item><b>Config.wtf first: portal AND <c>gxApi D3D12</c>.</b> Like the Linux path (and unlike native
/// Windows), the Wine renderer needs <c>gxApi D3D12</c> so GPTK's builtin d3d12 routes to D3DMetal;
/// DXVK/D3D11 renders the world but loses the entire UI layer. The portal is written and READ BACK before
/// anything starts — a client pointed at the wrong endpoint is the most dangerous silent failure.</item>
/// <item><b>Proxy second, and PROVEN listening.</b> The client cannot reach the realm without HermesProxy,
/// and a proxy that started but never bound its port produces a client that sits at the login screen with
/// no error. <see cref="HermesProxyRunner"/> waits for the port rather than assuming.</item>
/// <item><b>The client third, under GPTK-Wine.</b> The pre-patched custom-server exe is started through
/// <see cref="IWineHost.RunAsync"/> with the client directory as its working directory. No Arctium: the
/// static exe is already patched, and Arctium hangs under GPTK-Wine.</item>
/// <item><b>Then wait for the CLIENT by NAME, not by PID.</b> Under Rosetta + Wine the started process is
/// the <c>arch</c>/<c>wine64</c> loader, not the game — its PID is not the client's, so appear/reap go
/// through the process-name scan (<see cref="IGameProcessDetector"/>), exactly as the Linux modern path
/// does and as the proven <c>.command</c> does (<c>pgrep -f WowClassic</c>).</item>
/// </list>
///
/// <para><b>Proxy lifetime.</b> On a confirmed launch the started proxy is handed to
/// <see cref="IGameSession"/> WITHOUT binding a client PID, so the session reaps it via the detector once
/// the client process is gone (never before). Every FAILURE path stops the proxy inline, because it was
/// never handed off — the same rollback contract the other modern launchers uphold.</para>
/// </summary>
public sealed class MacModernClientLauncher : IGameLauncher
{
    /// <summary>The BNet auth port HermesProxy listens on. Fixed on both sides (the proxy binds it,
    /// Config.wtf's portal points at it), so it is one constant here.</summary>
    public const int ProxyPort = 1119;

    /// <summary>The realm the bundled proxy config must point its upstream at (exactly ONE bundled server
    /// — Stonetavern). A proxy config with a missing or different endpoint is refused, so the client can
    /// never be started against the wrong server (fail-closed, same as the Windows path).</summary>
    private const string ExpectedRealmHost = "play.stonetavern.app";

    /// <summary>The HermesProxy config file (read by the proxy from beside its binary).</summary>
    private const string ProxyConfigName = "HermesProxy.config";

    private readonly Serilog.ILogger _logger;
    private readonly IWineHost _wine;
    private readonly IGameProcessDetector _detector;
    private readonly Func<MacModernClientLayout, IGameProxy> _proxyFactory;
    private readonly IGameSession _session;
    private readonly string _expectedRealmHost;
    private readonly TimeSpan _proxyTimeout;
    private readonly TimeSpan _clientAppearTimeout;
    private readonly Func<TimeSpan, Task> _delay;

    /// <param name="wine">The GPTK-Wine host the custom-server exe is run in (<see cref="MacWineHost"/>).</param>
    /// <param name="session">The reap owner. REQUIRED: without an owner the proxy would never be reaped, so
    /// there is no such thing as a "successful" launch without one — a null session is a programming error.</param>
    public MacModernClientLauncher(
        Serilog.ILogger logger,
        IWineHost wine,
        IGameProcessDetector detector,
        Func<MacModernClientLayout, IGameProxy> proxyFactory,
        IGameSession session,
        string? expectedRealmHost = null,
        TimeSpan? proxyTimeout = null,
        TimeSpan? clientAppearTimeout = null,
        Func<TimeSpan, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _logger = logger;
        _wine = wine;
        _detector = detector;
        _proxyFactory = proxyFactory;
        _session = session;
        _expectedRealmHost = expectedRealmHost ?? ExpectedRealmHost;
        _proxyTimeout = proxyTimeout ?? TimeSpan.FromSeconds(20);
        _clientAppearTimeout = clientAppearTimeout ?? TimeSpan.FromSeconds(60);
        _delay = delay ?? Task.Delay;
    }

    /// <summary>The atomic stages of a launch, so the finally block knows what to roll back.</summary>
    private enum Stage { Validating, ProxyOwned, ClientStarted, HandedOff }

    public async Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory)
    {
        // ── Fail-closed validation, BEFORE any process starts ──────────────────────────────────────
        if (!File.Exists(exePath))
            return GameLaunchResult.Failed($"Client executable not found: {exePath}");

        var layout = MacModernClientLayout.Resolve(exePath);
        if (layout is null)
            return GameLaunchResult.Failed(IncompleteBundleMessage(exePath));

        if (!File.Exists(layout.ProxyExe))
            return GameLaunchResult.Failed(MissingPartMessage("Hermes/" + MacModernClientLayout.ProxyExeName, layout));
        if (!File.Exists(layout.CustomServerExe))
            return GameLaunchResult.Failed(
                MissingPartMessage("World of Warcraft/_classic_era_/" + MacModernClientLayout.CustomServerExeName, layout));

        // Write portal + gxApi and READ THE PORTAL BACK before starting anything. Pointing the client at
        // the wrong endpoint is the most dangerous silent failure, so a portal we cannot confirm on disk
        // aborts the launch instead of trusting the write.
        if (!WriteAndConfirmConfig(layout))
            return GameLaunchResult.Failed(PortalConfigMessage(layout));

        // Validate the PROXY's own upstream endpoint (fail-closed): HermesProxy.config beside the binary
        // must name the expected Stonetavern realm, or the proxy would relay to the wrong (or no) server.
        if (!ProxyEndpointOk(layout, out var endpointErr))
            return GameLaunchResult.Failed(endpointErr);

        // ── Explicit session lifecycle with guaranteed rollback ────────────────────────────────────
        var proxy = _proxyFactory(layout);
        var stage = Stage.Validating;
        try
        {
            var proxyResult = await proxy.StartAndWaitForPortAsync(ProxyPort, _proxyTimeout).ConfigureAwait(false);
            if (!proxyResult.Ready)
            {
                _logger.Error("Modern client launch aborted: proxy not ready ({Error})", proxyResult.Error);
                return GameLaunchResult.Failed(ProxyFailedMessage(proxyResult.Error));
            }
            stage = Stage.ProxyOwned;

            if (!await proxy.VerifyStillListeningAsync(ProxyPort).ConfigureAwait(false))
            {
                _logger.Error("The proxy no longer owns port {Port} at client-start time — aborting", ProxyPort);
                return GameLaunchResult.Failed(ProxyFailedMessage(null));
            }

            // The pre-patched custom-server exe under GPTK-Wine, with the client dir as its working
            // directory. No Arctium (the exe is already patched, and Arctium hangs under GPTK-Wine).
            _logger.Information("Starting the macOS client under GPTK-Wine ({Exe})", layout.CustomServerExe);
            var start = await _wine.RunAsync(layout.CustomServerExe, layout.ClientDir, []).ConfigureAwait(false);
            if (!start.Started)
                return GameLaunchResult.Failed(start.Error ?? "The client could not be started.");
            stage = Stage.ClientStarted;

            // Wait for the CLIENT by name — the Wine/Rosetta indirection means start.ProcessId is the wine
            // loader, not the game (so PID liveness would be wrong here). The detector's process-name scan
            // is the same signal the proven .command uses (pgrep -f WowClassic).
            var appeared = await WaitForClientAsync(layout.ClientExe).ConfigureAwait(false);
            if (!appeared)
            {
                _logger.Error(
                    "The client never appeared within {Timeout:F0}s after the GPTK-Wine start", _clientAppearTimeout.TotalSeconds);
                return GameLaunchResult.Failed(ClientNeverAppearedMessage());
            }

            // Hand the live proxy to the session WITHOUT a bound client PID, so it reaps via the detector
            // once the client process is gone (never before). Binding a PID would be wrong here: the wine
            // loader PID is not the client's, so the detector name-scan is the authoritative liveness.
            _session.AttachProxy(proxy);
            stage = Stage.HandedOff;

            _logger.Information(
                "macOS modern client is running (proxy PID={Pid}, port {Port})", proxyResult.ProcessId, ProxyPort);
            return GameLaunchResult.Ok(proxyResult.ProcessId ?? 0);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "macOS modern launch failed unexpectedly");
            return GameLaunchResult.Failed("The client could not be started. The launcher log has the details.");
        }
        finally
        {
            // Guaranteed rollback: a proxy we started but the session never took ownership of is stopped
            // so no orphan is left on port 1119. When the session took ownership (HandedOff) the reap
            // watchdog owns it and must NOT be stopped here. StopAsync is idempotent.
            if (stage is Stage.ProxyOwned or Stage.ClientStarted)
            {
                _logger.Information("Rolling back: stopping the proxy the session never took ownership of");
                try { await proxy.StopAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.Debug(ex, "Rollback proxy stop failed (best effort)"); }
            }
        }
    }

    /// <summary>Point the client at the local proxy (portal) and pin the Wine renderer (gxApi D3D12), then
    /// confirm the PORTAL landed by reading it back. gxApi is a render hint and not read back (a wrong
    /// renderer is recoverable in-game; a wrong portal silently connects to the wrong place). Returns false
    /// only when the portal cannot be confirmed on disk, which aborts the launch (fail-closed).</summary>
    private bool WriteAndConfirmConfig(MacModernClientLayout layout)
    {
        var expected = $"127.0.0.1:{ProxyPort}";
        var settings = new Dictionary<string, string>
        {
            ["portal"] = expected,
            ["gxApi"] = "D3D12",
        };

        if (!WtfConfigWriter.Apply(layout.ConfigWtf, settings))
        {
            _logger.Error("Could not write {Config}", layout.ConfigWtf);
            return false;
        }

        try
        {
            var wanted = $"SET portal \"{expected}\"";
            var present = File.ReadAllLines(layout.ConfigWtf)
                .Any(l => string.Equals(l.Trim(), wanted, StringComparison.Ordinal));
            if (!present)
            {
                _logger.Error("Portal readback failed: {Wanted} not found in {Config}", wanted, layout.ConfigWtf);
                return false;
            }
            _logger.Information("Client config confirmed on disk: portal={Expected}, gxApi=D3D12", expected);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Portal readback could not read {Config}", layout.ConfigWtf);
            return false;
        }
    }

    /// <summary>Wait for the client to appear via the process-name scan. KNOWN LIMITATION (Codex review),
    /// inherent to the Wine-indirection approach and shared verbatim with the Linux
    /// <see cref="ModernClientLauncher"/>: the scan cannot tell OUR freshly started client from an unrelated
    /// WowClassic already running, so a second instance launched while one is open reports success off the
    /// existing process, and the session reap keeps the proxy alive until the LAST WowClassic exits. It is
    /// not fixable with a PID here — under Rosetta+Wine the process we start is the loader, not the game. A
    /// pre-launch "already running" guard would be the mitigation; deliberately not added now to keep the
    /// macOS path identical to the proven Linux one rather than diverging.</summary>
    private async Task<bool> WaitForClientAsync(string clientExe)
    {
        var deadline = DateTime.UtcNow + _clientAppearTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_detector.IsGameRunning(clientExe)) return true;
            await _delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        return _detector.IsGameRunning(clientExe);
    }

    /// <summary>Validate the proxy's own upstream endpoint config (fail-closed). Reads the HermesProxy
    /// config beside the proxy binary and confirms its <c>ServerAddress</c> is present and equals the
    /// expected Stonetavern realm. Missing file, missing/blank key, or a different host → refuse. Reuses
    /// <see cref="ProxyEndpointConfig"/>, the same reader the Windows path uses.</summary>
    private bool ProxyEndpointOk(MacModernClientLayout layout, out string error)
    {
        var configPath = Path.Combine(layout.ProxyDir, ProxyConfigName);
        var address = ProxyEndpointConfig.ReadServerAddress(configPath);
        if (string.IsNullOrWhiteSpace(address))
        {
            _logger.Error("Proxy config {Config} has no ServerAddress — refusing to start", configPath);
            error = ProxyEndpointMessage(configPath, null);
            return false;
        }
        if (!string.Equals(address.Trim(), _expectedRealmHost, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Error(
                "Proxy config {Config} points at {Actual}, expected {Expected} — refusing to start",
                configPath, address, _expectedRealmHost);
            error = ProxyEndpointMessage(configPath, address);
            return false;
        }
        _logger.Information("Proxy endpoint confirmed: ServerAddress={Address}", address);
        error = "";
        return true;
    }

    private static string IncompleteBundleMessage(string exePath) =>
        "This 1.14.2 client cannot be started from where it is. The launcher expects the full macOS " +
        "client package, with the Hermes folder next to the World of Warcraft folder.\n" +
        $"Found the client at: {exePath}";

    private static string MissingPartMessage(string missing, MacModernClientLayout layout) =>
        $"The 1.14.2 macOS client package is incomplete: {missing} is missing.\n" +
        $"Package folder: {layout.BundleRoot}\n" +
        "Download the client package again and extract all of it.";

    private static string ProxyFailedMessage(string? detail) =>
        "The realm proxy did not start, so the client would not be able to reach the realm.\n" +
        (string.IsNullOrWhiteSpace(detail) ? "" : detail + "\n") +
        "The launcher log has the full output.";

    private static string ClientNeverAppearedMessage() =>
        "The client did not start. The proxy came up, but no game process appeared.\n" +
        "This usually means the Game Porting Toolkit Wine is missing or the prefix failed to initialise. " +
        "The launcher log has the details.";

    private static string PortalConfigMessage(MacModernClientLayout layout) =>
        "The launcher could not set the realm address in the client config, so it did not start the " +
        "client (it would otherwise connect to the wrong place).\n" +
        $"Config file: {layout.ConfigWtf}\n" +
        "Check that the client folder is not read-only, then try again.";

    private static string ProxyEndpointMessage(string configPath, string? actual) =>
        "The realm proxy is not pointed at the expected server, so the launcher did not start it (it " +
        "would otherwise relay to the wrong place).\n" +
        (actual is null ? "The proxy config has no server address.\n" : $"Proxy server address: {actual}\n") +
        $"Config file: {configPath}\n" +
        "Download the client package again and extract all of it.";
}
