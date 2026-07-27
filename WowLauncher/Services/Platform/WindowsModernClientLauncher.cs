namespace WowLauncher.Services.Platform;

/// <summary>
/// Where every part of a <b>native Windows</b> 1.14.2 (Classic Era) client bundle sits, derived from the
/// ONE path the launcher already resolves: <c>WowClassic.exe</c>. The Windows package has a fixed shape,
/// laid down by the packaging step and mirrored by the working <c>Play Stonetavern.cmd</c>:
///
/// <code>
/// &lt;root&gt;/Hermes/JimsProxy.exe                      native Windows proxy (5.1.8), config beside it
/// &lt;root&gt;/World of Warcraft/_classic_era_/WowClassic.exe                 the retail-named client
/// &lt;root&gt;/World of Warcraft/_classic_era_/WowClassic_ForCustomServers.exe the exe actually launched
/// &lt;root&gt;/World of Warcraft/_classic_era_/WTF/Config.wtf
/// </code>
///
/// <para><b>Two things differ from the Linux <see cref="ModernClientLayout"/> and are the whole reason
/// this is a separate type:</b> the proxy is <c>Hermes/JimsProxy.exe</c> (no <c>linux</c> subfolder, no
/// ELF), and there is no Arctium launcher at all. Native Windows runs
/// <c>WowClassic_ForCustomServers.exe</c> directly — the static custom-server build ACCESS_VIOLATEs only
/// under Wine, which is why the Linux path needs Arctium to patch it in memory; on real Windows it just
/// runs.</para>
/// </summary>
public sealed record WindowsModernClientLayout(
    string BundleRoot,
    string ClientExe,          // WowClassic.exe — the resolved/detected client
    string CustomServerExe,    // WowClassic_ForCustomServers.exe — the exe actually started
    string ClientDir,
    string ProxyExe,
    string ProxyDir,
    string ConfigWtf)
{
    public const string ProxyExeName = "JimsProxy.exe";
    public const string CustomServerExeName = "WowClassic_ForCustomServers.exe";

    /// <summary>Derive the Windows layout from the resolved client exe, or null when the exe is not
    /// sitting in a bundle of this shape. Null is a legitimate answer, not a failure: the caller turns it
    /// into a sentence naming what is missing rather than guessing at paths that are not there.</summary>
    public static WindowsModernClientLayout? Resolve(string clientExePath)
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

            return new WindowsModernClientLayout(
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
/// Starts the modern 1.14.2 client on <b>native Windows</b>, the way <c>Play Stonetavern.cmd</c> proves
/// it starts — which is NOT the Linux way. There is no Wine, no winepath, no Arctium: JimsProxy first,
/// then <c>WowClassic_ForCustomServers.exe</c> directly. Every step exists because leaving it out
/// produces a specific silent failure, the same discipline the Linux <see cref="ModernClientLauncher"/>
/// follows:
///
/// <list type="number">
/// <item><b>Config.wtf first (defensive).</b> The shipped Windows bundle already writes
/// <c>portal 127.0.0.1:1119</c>, but a fresh or repaired install may not — so the launcher pins it
/// rather than trusting the package. It does NOT force <c>gxApi</c> or a fullscreen resolution: the Wine
/// D3D12/Wayland workarounds the Linux path needs do not apply to a native client, which renders and
/// sizes itself correctly on its own.</item>
/// <item><b>Proxy second, and PROVEN listening.</b> The client cannot reach the realm without JimsProxy,
/// and a proxy that started but never bound its port produces a client that sits at the login screen with
/// no error. <see cref="JimsProxyRunner"/> waits for the port rather than assuming (the shell/batch
/// script only slept two seconds and hoped).</item>
/// <item><b>The client third, natively.</b> <c>WowClassic_ForCustomServers.exe</c> is started through the
/// plain native launch path (<see cref="WindowsGameLauncher"/>) with the client directory as its working
/// directory.</item>
/// <item><b>Then wait for the CLIENT to actually appear</b> before reporting success and before handing
/// the proxy to the session — a start call returning is not proof the game came up.</item>
/// </list>
///
/// <para><b>Proxy lifetime.</b> On a confirmed launch the started proxy is handed to
/// <see cref="IGameSession"/>, which reaps it once the client process is gone (never before — cutting a
/// playing user's connection is the one thing that must not happen). Every FAILURE path below stops the
/// proxy inline, because it was never handed off. This is the identical contract the Linux modern
/// launcher upholds; only the mechanics under it are native.</para>
/// </summary>
public sealed class WindowsModernClientLauncher : IGameLauncher
{
    /// <summary>The BNet auth port JimsProxy listens on (BNetPort in HermesProxy.config). Fixed on both
    /// sides — the proxy binds it, Config.wtf's portal points at it — so it is one constant here.</summary>
    public const int ProxyPort = 1119;

    /// <summary>The bitness the 1.14.2 Classic Era client (and the win-x64 JimsProxy) must be. A mismatch
    /// is refused before any process starts (fail-closed).</summary>
    private const PeArch RequiredArch = PeArch.X64;

    /// <summary>The realm the bundled proxy config must point its upstream at (there is exactly ONE
    /// bundled server — Stonetavern). A proxy config with a missing or different endpoint is refused,
    /// so the client can never be started against the wrong server.</summary>
    private const string ExpectedRealmHost = "play.stonetavern.app";

    /// <summary>The JimsProxy config file (read by the proxy from beside its binary).</summary>
    private const string ProxyConfigName = "HermesProxy.config";

    private readonly Serilog.ILogger _logger;
    private readonly IGameLauncher _nativeStarter;
    private readonly IGameProcessDetector _detector;
    private readonly Func<WindowsModernClientLayout, IGameProxy> _proxyFactory;
    private readonly IGameSession _session;
    private readonly string _expectedRealmHost;
    private readonly Func<string, PeArch> _peArch;
    private readonly Func<int, bool> _isPidAlive;
    private readonly TimeSpan _proxyTimeout;
    private readonly TimeSpan _clientAppearTimeout;
    private readonly Func<TimeSpan, Task> _delay;

    /// <param name="nativeStarter">The plain native launch path (<see cref="WindowsGameLauncher"/>) used
    /// to start the custom-server exe — reused so the "how to start a Windows exe" logic lives in one
    /// place and cannot drift.</param>
    /// <param name="session">The reap owner. REQUIRED (Codex review P1): without an owner the proxy would
    /// never be reaped, so there is no such thing as a "successful" launch without one — a null session is
    /// a programming error, not a supported mode.</param>
    /// <param name="peArch">Seam: reads the PE architecture of an exe (default <see cref="PeArchitecture.Read"/>),
    /// so the fail-closed bitness check is testable without shipping real PE binaries as fixtures.</param>
    /// <param name="isPidAlive">Seam: is a started process still alive (default: a real process lookup),
    /// so the PID-based appear/confirm can be proven on Fedora.</param>
    /// <param name="expectedRealmHost">The upstream realm the proxy config must name (default the bundled
    /// Stonetavern realm); overridable so the fail-closed endpoint check is testable.</param>
    public WindowsModernClientLauncher(
        Serilog.ILogger logger,
        IGameLauncher nativeStarter,
        IGameProcessDetector detector,
        Func<WindowsModernClientLayout, IGameProxy> proxyFactory,
        IGameSession session,
        Func<string, PeArch>? peArch = null,
        Func<int, bool>? isPidAlive = null,
        string? expectedRealmHost = null,
        TimeSpan? proxyTimeout = null,
        TimeSpan? clientAppearTimeout = null,
        Func<TimeSpan, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _logger = logger;
        _nativeStarter = nativeStarter;
        _detector = detector;
        _proxyFactory = proxyFactory;
        _session = session;
        _expectedRealmHost = expectedRealmHost ?? ExpectedRealmHost;
        _peArch = peArch ?? PeArchitecture.Read;
        _isPidAlive = isPidAlive ?? DefaultIsPidAlive;
        _proxyTimeout = proxyTimeout ?? TimeSpan.FromSeconds(20);
        _clientAppearTimeout = clientAppearTimeout ?? TimeSpan.FromSeconds(60);
        _delay = delay ?? Task.Delay;
    }

    /// <summary>The atomic stages of a launch. Each transition has a defined rollback (see LaunchAsync's
    /// finally): the proxy is guaranteed to be stopped if it was started but the session never took
    /// ownership of it, so a failure or an exception mid-flight never leaves an orphan on port 1119.</summary>
    private enum Stage { Validating, ProxyOwned, ClientStarted, HandedOff }

    public async Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory)
    {
        // ── Fail-closed validation, BEFORE any process starts ──────────────────────────────────────
        if (!File.Exists(exePath))
            return GameLaunchResult.Failed($"Client executable not found: {exePath}");

        var layout = WindowsModernClientLayout.Resolve(exePath);
        if (layout is null)
            return GameLaunchResult.Failed(IncompleteBundleMessage(exePath));

        // (c) required files present.
        if (!File.Exists(layout.ProxyExe))
            return GameLaunchResult.Failed(MissingPartMessage("Hermes/" + WindowsModernClientLayout.ProxyExeName, layout));
        if (!File.Exists(layout.CustomServerExe))
            return GameLaunchResult.Failed(
                MissingPartMessage("World of Warcraft/_classic_era_/" + WindowsModernClientLayout.CustomServerExeName, layout));

        // (a) PE architecture matches the build: the 1.14.2 client and the win-x64 proxy are 64-bit.
        // A 32-bit exe here is the wrong client entirely and would fail in a way that looks like something
        // else, so refuse it now rather than after a "successful" start. (b) The manifest `os` match is
        // enforced upstream at download time by ManifestFile.MatchesCurrentOs — a Linux package never
        // reaches this Windows launcher; the bitness check is the concrete launch-time guard.
        if (!ArchOk(layout.CustomServerExe, out var clientErr))
            return GameLaunchResult.Failed(clientErr);
        if (!ArchOk(layout.ProxyExe, out var proxyErr))
            return GameLaunchResult.Failed(proxyErr);

        // (d) Write the portal and READ IT BACK before starting anything. Pointing the client at the wrong
        // endpoint is the most dangerous silent failure (a "successful" launch against the wrong server),
        // so a portal we cannot confirm on disk aborts the launch instead of trusting the write.
        if (!WriteAndConfirmPortal(layout))
            return GameLaunchResult.Failed(PortalConfigMessage(layout));

        // (e) Validate the PROXY's own upstream endpoint config before starting it. The portal points the
        // client at our proxy; this checks the proxy is pointed at the expected Stonetavern realm. A
        // missing or wrong ServerAddress means the proxy would relay to the wrong (or no) server — the same
        // silent "successful start against the wrong endpoint" the portal readback guards on the client
        // side, closed on the proxy side too (fail-closed).
        if (!ProxyEndpointOk(layout, out var endpointErr))
            return GameLaunchResult.Failed(endpointErr);

        // ── Explicit session lifecycle with guaranteed rollback ────────────────────────────────────
        var proxy = _proxyFactory(layout);
        var stage = Stage.Validating;
        try
        {
            // Proxy before client. Its working directory is the proxy's OWN directory (handled inside
            // JimsProxyRunner): JimsProxy reads HermesProxy.config from beside the binary.
            var proxyResult = await proxy.StartAndWaitForPortAsync(ProxyPort, _proxyTimeout).ConfigureAwait(false);
            if (!proxyResult.Ready)
            {
                // The runner already stopped its own process on a failed start, so there is nothing to
                // roll back here (stage is still Validating).
                _logger.Error("Modern client launch aborted: proxy not ready ({Error})", proxyResult.Error);
                return GameLaunchResult.Failed(ProxyFailedMessage(proxyResult.Error));
            }
            stage = Stage.ProxyOwned;

            // Re-verify, right before starting the client, that OUR proxy still owns the listener — closes
            // the check-to-use gap between "proxy ready" and this start (the proxy could have died or a
            // foreign listener taken 1119 in between). False → roll back rather than start a client against
            // a listener we no longer control.
            if (!await proxy.VerifyStillListeningAsync(ProxyPort).ConfigureAwait(false))
            {
                _logger.Error("The proxy no longer owns port {Port} at client-start time — aborting", ProxyPort);
                return GameLaunchResult.Failed(ProxyFailedMessage(null));
            }

            // The custom-server exe, started natively (ForCustomServers runs straight on Windows — the
            // ACCESS_VIOLATE that forces Arctium is a Wine-only problem). Working directory is the client
            // dir. Play Stonetavern.cmd `start /wait`s on exactly this exe, so it is the process that
            // stays alive for the whole session and whose PID the reap binds to.
            _logger.Information("Starting the native Windows client ({Exe})", layout.CustomServerExe);
            var start = await _nativeStarter.LaunchAsync(layout.CustomServerExe, layout.ClientDir).ConfigureAwait(false);
            if (!start.Started)
                return GameLaunchResult.Failed(start.Error ?? "The client could not be started.");
            stage = Stage.ClientStarted;

            // Confirm the client actually came up, by ITS pid, so a start that returns then instantly dies
            // is caught. When a PID is bound it is AUTHORITATIVE — a same-named foreign process must not
            // stand in for our dead client (Codex review). The name-scan is used only when no PID is known.
            var clientPid = start.ProcessId;
            var appeared = await WaitForClientAsync(layout.ClientExe, clientPid).ConfigureAwait(false);
            if (!appeared)
            {
                _logger.Error(
                    "The client never appeared within {Timeout:F0}s after the native start", _clientAppearTimeout.TotalSeconds);
                return GameLaunchResult.Failed(ClientNeverAppearedMessage());
            }

            // ClientOwned → hand off. Bind the reap to the CONCRETE client PID (ownership, not a name-scan
            // a foreign process could satisfy — Codex review), then give the proxy to the session so the
            // launcher (alive in the tray) reaps it when THAT process ends, never before. The session is
            // required (constructor invariant), so a handed-off launch always has a reap owner.
            if (clientPid is int pid) _session.BindClientProcess(pid);
            _session.AttachProxy(proxy);
            stage = Stage.HandedOff;

            _logger.Information(
                "Native modern client is running (client PID={ClientPid}, proxy PID={Pid}, port {Port})",
                clientPid, proxyResult.ProcessId, ProxyPort);
            return GameLaunchResult.Ok(clientPid ?? 0);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Native modern launch failed unexpectedly");
            return GameLaunchResult.Failed("The client could not be started. The launcher log has the details.");
        }
        finally
        {
            // Guaranteed rollback: if we started (own) the proxy but never handed it to the session — a
            // failed client start, a client that never appeared, or an exception anywhere above — stop it
            // so no orphan is left holding port 1119. When the session took ownership (HandedOff) the
            // reap watchdog owns it and must NOT be stopped here (the client is running). StopAsync is
            // idempotent, so a double-stop after the runner's own cleanup is safe.
            if (stage is Stage.ProxyOwned or Stage.ClientStarted)
            {
                _logger.Information("Rolling back: stopping the proxy the session never took ownership of");
                try { await proxy.StopAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.Debug(ex, "Rollback proxy stop failed (best effort)"); }
            }
        }
    }

    /// <summary>Confirm the exe at <paramref name="path"/> is the required architecture; on a mismatch
    /// hands back a user-facing reason and false. <see cref="PeArch.Unknown"/> is also a refusal: a file
    /// we cannot read as a PE is not something we start.</summary>
    private bool ArchOk(string path, out string error)
    {
        var arch = _peArch(path);
        if (arch == RequiredArch) { error = ""; return true; }
        _logger.Error("Refusing to start {Path}: PE architecture is {Arch}, expected {Expected}", path, arch, RequiredArch);
        error = ArchMismatchMessage(path, arch);
        return false;
    }

    /// <summary>Point the client at the local proxy AND confirm the write landed by reading it back.
    /// Unlike the Linux path this does not touch gxApi or the fullscreen resolution — those are
    /// Wine/Wayland workarounds a native client does not need. Returns false when the portal cannot be
    /// confirmed on disk, which aborts the launch (fail-closed): a client pointed at the wrong endpoint
    /// is the silent failure this guards.</summary>
    private bool WriteAndConfirmPortal(WindowsModernClientLayout layout)
    {
        var expected = $"127.0.0.1:{ProxyPort}";
        var settings = new Dictionary<string, string> { ["portal"] = expected };

        if (!WtfConfigWriter.Apply(layout.ConfigWtf, settings))
        {
            _logger.Error("Could not write {Config}", layout.ConfigWtf);
            return false;
        }

        // Read back: prove the exact portal line is now on disk before we start proxy or client.
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
            _logger.Information("Client config confirmed on disk: portal={Expected}", expected);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Portal readback could not read {Config}", layout.ConfigWtf);
            return false;
        }
    }

    /// <summary>Confirm the client came up. When the started process's PID is known it is the
    /// AUTHORITATIVE signal — a same-named foreign process (name-scan) must NOT stand in for our specific
    /// client, so the name-scan is used ONLY when no PID is available (Codex review). This is the same
    /// PID-over-name rule the reap watchdog (<see cref="GameSession"/>) applies.</summary>
    private async Task<bool> WaitForClientAsync(string clientExe, int? clientPid)
    {
        bool Up() => clientPid is int pid ? _isPidAlive(pid) : _detector.IsGameRunning(clientExe);

        var deadline = DateTime.UtcNow + _clientAppearTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Up()) return true;
            await _delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        return Up();
    }

    /// <summary>Validate the proxy's own upstream endpoint config (fail-closed). Reads the JimsProxy
    /// config beside the proxy binary and confirms its <c>ServerAddress</c> is present and equals the
    /// expected Stonetavern realm. Missing file, missing/blank key, or a different host → refuse.</summary>
    private bool ProxyEndpointOk(WindowsModernClientLayout layout, out string error)
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

    /// <summary>Default bound-PID liveness: the process exists and has not exited. Best-effort, never throws.</summary>
    private static bool DefaultIsPidAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch { return false; }
    }

    private static string IncompleteBundleMessage(string exePath) =>
        "This 1.14.2 client cannot be started from where it is. The launcher expects the full Windows " +
        "client package, with the Hermes folder next to the World of Warcraft folder.\n" +
        $"Found the client at: {exePath}";

    private static string MissingPartMessage(string missing, WindowsModernClientLayout layout) =>
        $"The 1.14.2 Windows client package is incomplete: {missing} is missing.\n" +
        $"Package folder: {layout.BundleRoot}\n" +
        "Download the client package again and extract all of it.";

    private static string ProxyFailedMessage(string? detail) =>
        "The realm proxy did not start, so the client would not be able to reach the realm.\n" +
        (string.IsNullOrWhiteSpace(detail) ? "" : detail + "\n") +
        "The launcher log has the full output.";

    private static string ClientNeverAppearedMessage() =>
        "The client did not start. The proxy came up, but no game process appeared.\n" +
        "The launcher log has the details.";

    private static string ArchMismatchMessage(string path, PeArch arch) =>
        "This file is not the right kind of program for the 1.14.2 client.\n" +
        $"Expected a 64-bit Windows program, found: {(arch == PeArch.Unknown ? "not a readable Windows program" : arch.ToString())}.\n" +
        $"File: {path}\n" +
        "Download the Windows client package again and extract all of it.";

    private static string PortalConfigMessage(WindowsModernClientLayout layout) =>
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

/// <summary>
/// Reads a single <c>appSettings</c> value out of the JimsProxy config (an XML file of the shape
/// <c>&lt;configuration&gt;&lt;appSettings&gt;&lt;add key="ServerAddress" value="play.stonetavern.app" /&gt;…</c>).
/// Pure and side-effect-free, so the fail-closed endpoint check is unit-tested with crafted config files.
/// </summary>
public static class ProxyEndpointConfig
{
    /// <summary>The <c>ServerAddress</c> value from the proxy config at <paramref name="configPath"/>, or
    /// null when the file is missing, unreadable, or has no such key. Never throws.</summary>
    public static string? ReadServerAddress(string configPath) => ReadKey(configPath, "ServerAddress");

    /// <summary>Read an <c>&lt;add key="..." value="..."/&gt;</c> value from the proxy config, or null.</summary>
    internal static string? ReadKey(string configPath, string key)
    {
        try
        {
            if (!File.Exists(configPath)) return null;
            var doc = System.Xml.Linq.XDocument.Load(configPath);
            foreach (var add in doc.Descendants("add"))
            {
                var k = (string?)add.Attribute("key");
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    return (string?)add.Attribute("value");
            }
            return null;
        }
        catch
        {
            return null; // malformed/unreadable config → treated as "no value" → fail-closed by the caller
        }
    }
}

/// <summary>
/// Windows only: picks the launch path off the resolved exe's OWN name, exactly as
/// <see cref="LinuxGameLauncherRouter"/> does on Linux — legacy builds (1.12.1, <c>WoW.exe</c>) go
/// through the plain native start, the modern build (1.14.2, <c>WowClassic.exe</c>) goes through the
/// native proxy+client sequence. The modern/legacy split is read from
/// <c>ClientVersion.NeedsModernRuntime</c> so adding a second modern build is a one-line change there,
/// not a forgotten edit here.
///
/// <para>Before this router the Windows branch registered only the plain native launcher, which would
/// have started <c>WowClassic.exe</c> with NO proxy — the client would reach the login screen and stop
/// there with no error, the exact silent failure the whole modern-launch sequence exists to close.</para>
/// </summary>
public sealed class WindowsGameLauncherRouter : IGameLauncher
{
    private readonly IGameLauncher _legacyNative;
    private readonly IGameLauncher _modern;
    private readonly Serilog.ILogger _logger;

    /// <param name="legacyNative">Plain native start, for the 32-bit builds (1.12.1).</param>
    /// <param name="modern">The native proxy+client sequence, for the 1.14.2 build.</param>
    public WindowsGameLauncherRouter(IGameLauncher legacyNative, IGameLauncher modern, Serilog.ILogger logger)
    {
        _legacyNative = legacyNative;
        _modern = modern;
        _logger = logger;
    }

    public Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory)
    {
        var exeName = Path.GetFileName(exePath);
        var target = WowLauncher.Models.ClientVersion.ExeNameNeedsModernRuntime(exeName)
            ? _modern
            : _legacyNative;
        _logger.Debug("Routing launch of {Exe} to {Launcher}", exeName, target.GetType().Name);
        return target.LaunchAsync(exePath, workingDirectory);
    }
}
