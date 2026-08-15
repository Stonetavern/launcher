namespace WowLauncher.Services.Platform;

using System.Diagnostics;

/// <summary>
/// Where every part of a modern (1.14.2 Classic Era) client bundle sits, derived from the ONE path the
/// launcher already resolves: the client executable. The bundle has a fixed shape, laid down by the
/// packaging step and mirrored by the working start script:
///
/// <code>
/// &lt;root&gt;/Hermes/linux/HermesProxy          native ELF, CSV game data BESIDE it
/// &lt;root&gt;/Launcher/Arctium WoW Launcher.exe  in-memory patcher, starts the client
/// &lt;root&gt;/World of Warcraft/_classic_era_/WowClassic.exe
/// &lt;root&gt;/World of Warcraft/_classic_era_/WTF/Config.wtf
/// </code>
/// </summary>
public sealed record ModernClientLayout(
    string BundleRoot,
    string ClientExe,
    string ClientDir,
    string ArctiumExe,
    string ArctiumDir,
    string ProxyExe,
    string ProxyDir,
    string ConfigWtf)
{
    public const string ArctiumExeName = "Arctium WoW Launcher.exe";
    public const string ProxyExeName = "HermesProxy";

    /// <summary>Derive the layout from the resolved client exe, or null when the exe is not sitting in
    /// a bundle of this shape (a hand-placed client, a different packaging). Null is a legitimate
    /// answer, not a failure: the caller turns it into a sentence naming what is missing, rather than
    /// guessing at paths that are not there.</summary>
    public static ModernClientLayout? Resolve(string clientExePath)
    {
        try
        {
            var clientDir = Path.GetDirectoryName(Path.GetFullPath(clientExePath));   // .../_classic_era_
            if (clientDir is null) return null;
            var wowDir = Path.GetDirectoryName(clientDir);                            // .../World of Warcraft
            if (wowDir is null) return null;
            var root = Path.GetDirectoryName(wowDir);                                 // bundle root
            if (root is null) return null;

            var proxyDir = Path.Combine(root, "Hermes", "linux");
            var arctiumDir = Path.Combine(root, "Launcher");

            return new ModernClientLayout(
                BundleRoot: root,
                ClientExe: Path.GetFullPath(clientExePath),
                ClientDir: clientDir,
                ArctiumExe: Path.Combine(arctiumDir, ArctiumExeName),
                ArctiumDir: arctiumDir,
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

/// <summary>The monitor resolution the client should go fullscreen at.</summary>
public interface IDisplayResolution
{
    /// <summary>The active mode as WoW writes it (<c>2560x1440</c>), or null if it cannot be read.</summary>
    string? Current();
}

/// <summary>
/// Reads the active mode from <c>xrandr</c>, the same source the working start script uses. Returns
/// null rather than a guess when xrandr is absent (a pure-Wayland session without XWayland tooling):
/// a WRONG resolution is worse than none here, because the client would then map its window off-screen
/// and the player sees nothing at all.
/// </summary>
public sealed class XrandrDisplayResolution : IDisplayResolution
{
    private readonly Serilog.ILogger _logger;

    public XrandrDisplayResolution(Serilog.ILogger logger) => _logger = logger;

    public string? Current()
    {
        try
        {
            var psi = new ProcessStartInfo("xrandr")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000)) { try { process.Kill(true); } catch { } return null; }
            if (process.ExitCode != 0) return null;

            return ParseActiveMode(output);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "xrandr not available; leaving the fullscreen resolution untouched");
            return null;
        }
    }

    /// <summary>The first mode line marked current with <c>*</c>, e.g. <c>2560x1440 59.95*+</c>.</summary>
    internal static string? ParseActiveMode(string xrandrOutput)
    {
        foreach (var raw in xrandrOutput.Split('\n'))
        {
            if (!raw.Contains('*')) continue;
            var mode = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (mode is null) continue;
            var parts = mode.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _))
                return mode;
        }
        return null;
    }
}

/// <summary>
/// Reads and writes WoW's <c>Config.wtf</c>, whose whole grammar is <c>SET key "value"</c> per line.
/// Keys are matched case-insensitively and rewritten in place; a key that is not there is appended.
/// Existing settings the launcher has no opinion about are never touched, because this file also holds
/// the player's own graphics and sound choices.
/// </summary>
public static class WtfConfigWriter
{
    /// <summary>Apply <paramref name="settings"/> to the file at <paramref name="path"/>, creating it
    /// (and its directory) when absent. Returns false when the file could not be written; the caller
    /// decides whether that is fatal.</summary>
    public static bool Apply(string path, IReadOnlyDictionary<string, string> settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var lines = File.Exists(path)
                ? File.ReadAllLines(path).ToList()
                : [];

            // Write atomically: a full truncate-in-place write (File.WriteAllLines straight onto `path`)
            // means a crash or a concurrent writer mid-write leaves the player's Config.wtf truncated —
            // it holds their own graphics/sound choices, not just our two keys, so a partial write loses
            // real user data (Codex review). Write a sibling temp file, flush it, then File.Move-replace:
            // the rename is atomic on the same filesystem, so a reader ever sees either the old or the new
            // complete file, never a half-written one.
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllLines(tmp, ApplyToLines(lines, settings));
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best effort */ } }
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The pure transformation, so the line handling is testable without a filesystem.</summary>
    internal static IReadOnlyList<string> ApplyToLines(
        IReadOnlyList<string> lines, IReadOnlyDictionary<string, string> settings)
    {
        var result = lines.ToList();
        foreach (var (key, value) in settings)
        {
            var line = $"SET {key} \"{value}\"";
            var idx = result.FindIndex(l => IsSettingFor(l, key));
            if (idx >= 0) result[idx] = line; else result.Add(line);
        }
        return result;
    }

    /// <summary>Is this a <c>SET portal …</c> line? Exposed so the launch path can find EVERY portal
    /// line (not just the first) with the same matching rule this writer uses — the rewrite only
    /// replaces the first match, so a duplicate can otherwise survive unnoticed.</summary>
    internal static bool IsPortalLine(string line) => IsSettingFor(line, "portal");

    private static bool IsSettingFor(string line, string key)
    {
        var trimmed = line.TrimStart();
        // ANY whitespace after SET, not just a literal space (Codex review round 2, 2026-07-27).
        // The old check required "SET " exactly, so `SET\tportal "…"` was neither rewritten by the
        // writer NOR seen by the launch-path readback — a line the client may well honour could sit
        // beside the canonical one and the launch would proceed. WoW's own grammar for this file is
        // not documented anywhere we can rely on, so the matcher errs on the side of recognising MORE
        // lines: a line we recognise is one we either fix or refuse to start against.
        if (!trimmed.StartsWith("SET", StringComparison.OrdinalIgnoreCase)) return false;
        if (trimmed.Length <= 3 || !char.IsWhiteSpace(trimmed[3])) return false;
        var rest = trimmed[3..].TrimStart();
        return rest.StartsWith(key, StringComparison.OrdinalIgnoreCase)
               && rest.Length > key.Length
               && char.IsWhiteSpace(rest[key.Length]);
    }
}

/// <summary>
/// Starts the modern 1.14.2 client the way it is actually proven to start, which is not "run the exe".
/// Rebuilt from <c>Play Stonetavern.sh</c>, the one path a character has reached the world through, and
/// every step below is there because leaving it out produces a specific silent failure:
///
/// <list type="number">
/// <item><b>Config.wtf first.</b> <c>portal</c> points the client at the local proxy;
/// <c>gxApi D3D12</c> because DXVK/D3D11 renders the world but loses the ENTIRE UI layer (no action
/// bars, no chat) - a bug that looks like a broken client, not like a wrong setting; and
/// <c>gxFullscreenResolution</c> pinned to the real display, because a mismatched one maps the window
/// off-screen under Wayland and the player sees no window at all.</item>
/// <item><b>Proxy second, and PROVEN listening.</b> The client cannot reach the realm without it, and
/// a proxy that started but never bound its port produces a client that sits at the login screen with
/// no error. <see cref="HermesProxyRunner"/> waits for the port rather than assuming.</item>
/// <item><b>Arctium third, not the client.</b> The static <c>WowClassic_ForCustomServers.exe</c>
/// ACCESS_VIOLATEs (ERROR #132) under Wine; the identical client patched in memory by Arctium does
/// not. So the launcher runs Arctium with <c>--version=ClassicEra --path &lt;windows path&gt;</c>.</item>
/// <item><b>Then wait for the CLIENT, not for Arctium.</b> Arctium patches, launches, and exits within
/// seconds. Treating its exit as the launch result reports success before the game exists, and reports
/// failure when Arctium exits normally.</item>
/// </list>
///
/// <para><b>Proxy lifetime (reversed, supersedes the 2026-07-22 detach decision).</b> The launcher no
/// longer exits after a confirmed launch — it hides into the system tray and lives on — so it CAN watch
/// for the game to quit the way the shell script does and stop the proxy itself. On a successful launch
/// the started proxy is handed to <see cref="IGameSession"/>, which reaps it once the client process is
/// gone (never before — cutting a playing user's connection is the one thing that must not happen). The
/// pidfile self-heal in <see cref="HermesProxyRunner"/> stays as the crash-only safety net (a launcher
/// killed mid-session runs no cleanup) and never competes with the clean path, because a clean stop
/// deletes the pidfile. Every FAILURE path below still stops the proxy inline — it was never handed off,
/// so there is nothing for the session to reap.</para>
/// </summary>
public sealed class ModernClientLauncher : IGameLauncher
{
    /// <summary>The BNet auth port HermesProxy listens on. Fixed on both sides (the proxy binds it,
    /// Config.wtf points at it), so it is one constant here rather than two settings that can disagree.</summary>
    public const int ProxyPort = 1119;

    /// <summary>The architecture the shipped bundle is built for. The client and the Arctium patcher are
    /// 64-bit Windows programs; anything else in their place is a different package, not this one.</summary>
    private const PeArch RequiredArch = PeArch.X64;

    private readonly Serilog.ILogger _logger;
    private readonly IWineHost _wine;
    private readonly IGameProcessDetector _detector;
    private readonly IDisplayResolution _display;
    /// <summary>The proxy's own configuration file, read by HermesProxy from beside its binary.</summary>
    private const string ProxyConfigName = "HermesProxy.config";

    private readonly Func<ModernClientLayout, IGameProxy> _proxyFactory;
    private readonly IGameSession? _session;
    private readonly Func<string?>? _realmAddress;
    /// <summary>The language the player picked, read at launch time. Null (unit tests, and any caller
    /// that does not care) leaves <c>textLocale</c> in Config.wtf exactly as it was.</summary>
    private readonly Func<string?>? _locale;
    private readonly Func<string, PeArch> _peArch;
    private readonly TimeSpan _proxyTimeout;
    private readonly TimeSpan _clientAppearTimeout;
    private readonly Func<TimeSpan, Task> _delay;

    /// <param name="realmAddress">The realm this launch goes to. The modern client never speaks to the
    /// realm directly — it talks to the proxy, and the PROXY's <c>ServerAddress</c> decides which realm
    /// that is. Without this the shipped (Stonetavern) address was used for every realm, so a player's
    /// own realm was ignored outright. Null = leave the proxy config alone (unit tests; production wires
    /// it). See <see cref="WowLauncher.Services.RealmBinding"/>.</param>
    public ModernClientLauncher(
        Serilog.ILogger logger,
        IWineHost wine,
        IGameProcessDetector detector,
        IDisplayResolution display,
        Func<ModernClientLayout, IGameProxy> proxyFactory,
        IGameSession? session = null,
        Func<string?>? realmAddress = null,
        Func<string?>? locale = null,
        Func<string, PeArch>? peArch = null,
        TimeSpan? proxyTimeout = null,
        TimeSpan? clientAppearTimeout = null,
        Func<TimeSpan, Task>? delay = null)
    {
        _logger = logger;
        _wine = wine;
        _detector = detector;
        _display = display;
        _proxyFactory = proxyFactory;
        _session = session;
        _realmAddress = realmAddress;
        _locale = locale;
        _peArch = peArch ?? PeArchitecture.Read;
        _proxyTimeout = proxyTimeout ?? TimeSpan.FromSeconds(20);
        _clientAppearTimeout = clientAppearTimeout ?? TimeSpan.FromSeconds(60);
        _delay = delay ?? Task.Delay;
    }

    public async Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory)
    {
        if (!File.Exists(exePath))
            return GameLaunchResult.Failed($"Client executable not found: {exePath}");

        var layout = ModernClientLayout.Resolve(exePath);
        if (layout is null)
            return GameLaunchResult.Failed(IncompleteBundleMessage(exePath));
        if (!File.Exists(layout.ArctiumExe))
            return GameLaunchResult.Failed(MissingPartMessage("Launcher/" + ModernClientLayout.ArctiumExeName, layout));
        if (!File.Exists(layout.ProxyExe))
            return GameLaunchResult.Failed(MissingPartMessage("Hermes/linux/" + ModernClientLayout.ProxyExeName, layout));

        // Bitness, before any process starts — the same fail-closed gate Windows has had since the Codex
        // review, brought over because a wrong-architecture client fails LATER and in a way that looks
        // like something else (Wine reporting a missing DLL, a start that returns and then dies), which
        // sends the player and the support thread after the wrong cause. Only the two PE files are
        // checked: the client and the Arctium patcher run under Wine and must be 64-bit. HermesProxy is
        // a native ELF here, not a PE, so reading a COFF header off it would be meaningless.
        if (!ArchOk(layout.ClientExe, out var clientArchError))
            return GameLaunchResult.Failed(clientArchError);
        if (!ArchOk(layout.ArctiumExe, out var arctiumArchError))
            return GameLaunchResult.Failed(arctiumArchError);

        // Point the PROXY at the realm the player picked, before it starts. The client only ever talks
        // to 127.0.0.1, so this file — not realmlist.wtf — decides which realm this launch reaches.
        // Fail-closed: a realm we cannot honour aborts the launch instead of quietly connecting the
        // player to the shipped one.
        if (!PointProxyAtSelectedRealm(layout, out var realmError))
            return GameLaunchResult.Failed(realmError);

        // Same fail-closed rule one level down: the client must be pointed at OUR proxy, proven on disk.
        // An unconfirmable portal means the game would come up healthy and talk to the wrong server.
        if (!WriteClientConfig(layout))
            return GameLaunchResult.Failed(PortalUnconfirmedMessage(layout.ConfigWtf));

        // Proxy before client. Its working directory is the proxy's OWN directory, not the bundle root:
        // HermesProxy reads its game data (flight paths, spell tables, hotfixes) from a CSV folder
        // beside the binary, and starting it anywhere else makes it die at startup with a
        // DirectoryNotFoundException - the exact failure a mis-scoped packaging exclude caused on
        // 2026-07-21, so this is a known-real failure mode and not a hypothetical.
        var proxy = _proxyFactory(layout);
        var stage = Stage.Validating;
        try
        {
            var proxyResult = await proxy.StartAndWaitForPortAsync(ProxyPort, _proxyTimeout).ConfigureAwait(false);
            if (!proxyResult.Ready)
            {
                // The runner already stopped its own process on a failed start, so there is nothing to
                // roll back here (stage is still Validating).
                _logger.Error("Modern client launch aborted: proxy not ready ({Error})", proxyResult.Error);
                return GameLaunchResult.Failed(ProxyFailedMessage(proxyResult.Error));
            }
            stage = Stage.ProxyOwned;

            // The same re-check Windows and macOS do, and for the same reason: between "the proxy is ready"
            // and "the client is running" sits Wine startup, a winepath call and a process spawn. A listener
            // that changed hands in that window would send the player's session somewhere else, and the game
            // would come up looking perfectly healthy.
            if (!await proxy.VerifyStillListeningAsync(ProxyPort).ConfigureAwait(false))
            {
                _logger.Error("The proxy no longer owns port {Port} at client-start time — aborting", ProxyPort);
                return GameLaunchResult.Failed(ProxyFailedMessage(null));
            }

            // Arctium wants a WINDOWS path. Resolving it through winepath (rather than mapping a drive
            // letter into the prefix) means no prefix symlink to create and no quoting trouble with the
            // spaces in "World of Warcraft".
            var windowsPath = await _wine.ToWindowsPathAsync(layout.ClientDir).ConfigureAwait(false);
            if (windowsPath is null)
            {
                return GameLaunchResult.Failed(
                    "Wine could not resolve the client path. The launcher log has the details.");
            }

            _logger.Information("Starting the client through Arctium ({Path})", windowsPath);
            var arctium = await _wine.RunAsync(
                layout.ArctiumExe, layout.ArctiumDir, ["--version=ClassicEra", "--path", windowsPath])
                .ConfigureAwait(false);
            if (!arctium.Started)
                return GameLaunchResult.Failed(arctium.Error ?? "The client patcher could not be started.");
            stage = Stage.ClientStarted;

            // Wait for the CLIENT. Arctium exiting is normal and means nothing about whether the game came
            // up, so its process is not what gets watched here.
            var appeared = await WaitForClientAsync(layout.ClientExe).ConfigureAwait(false);
            if (!appeared)
            {
                _logger.Error(
                    "The client never appeared within {Timeout:F0}s after Arctium ran", _clientAppearTimeout.TotalSeconds);
                return GameLaunchResult.Failed(ClientNeverAppearedMessage());
            }

            _logger.Information("Modern client is running (proxy PID={Pid}, port {Port})", proxyResult.ProcessId, ProxyPort);
            // Hand the live proxy to the session so the launcher (now alive in the tray) can stop it when the
            // game ends, instead of leaving it detached to be reaped next start. When no session is wired
            // (unit tests), the proxy is simply left running as before — production always wires one.
            _session?.AttachProxy(proxy);
            stage = Stage.HandedOff;
            // The PID reported is Arctium's: the client itself was started by Arctium, not by this process,
            // so there is no handle to it here. Callers use the result as a started/failed signal and
            // confirm the real client through IGameProcessDetector, which is what just happened above.
            return GameLaunchResult.Ok(arctium.ProcessId ?? 0);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Modern client launch failed unexpectedly");
            return GameLaunchResult.Failed("The client could not be started. The launcher log has the details.");
        }
        finally
        {
            // Guaranteed rollback, the piece Linux was missing while Windows and macOS had it: every
            // early return above used to stop the proxy by hand, so an EXCEPTION between "proxy started"
            // and "session took ownership" — winepath throwing, the Wine host throwing, the detector
            // throwing — left HermesProxy running on 1119 with nothing pointing at it. The next Play then
            // failed on a busy port for a reason the player could not see.
            if (stage is Stage.ProxyOwned or Stage.ClientStarted)
            {
                _logger.Information("Rolling back: stopping the proxy the session never took ownership of");
                try { await proxy.StopAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.Debug(ex, "Rollback proxy stop failed (best effort)"); }
            }
        }
    }

    /// <summary>The atomic stages of a launch, mirroring <see cref="WindowsModernClientLauncher"/>. Each
    /// transition has a defined rollback in LaunchAsync's finally: a proxy that was started but never
    /// handed to the session is always stopped, so no failure path leaves an orphan on the port.</summary>
    private enum Stage { Validating, ProxyOwned, ClientStarted, HandedOff }

    /// <summary>Confirm the PE at <paramref name="path"/> is the architecture this build starts; on a
    /// mismatch hands back a player-facing reason and false. <see cref="PeArch.Unknown"/> is a refusal
    /// too — a file we cannot read as a PE is not something we hand to Wine.</summary>
    private bool ArchOk(string path, out string error)
    {
        var arch = _peArch(path);
        if (arch == RequiredArch) { error = ""; return true; }
        _logger.Error(
            "Refusing to start {Path}: PE architecture is {Arch}, expected {Expected}", path, arch, RequiredArch);
        error = ArchMismatchMessage(path, arch);
        return false;
    }

    private static string ArchMismatchMessage(string path, PeArch arch)
    {
        var what = arch == PeArch.Unknown
            ? "could not be read as a Windows program"
            : $"is {(arch == PeArch.X86 ? "32-bit" : arch.ToString())}, and this build needs 64-bit";
        return $"This client cannot be started: {Path.GetFileName(path)} {what}.\n" +
               "The package is either incomplete or not the one this launcher installs. " +
               "Reinstall the client from the launcher.";
    }

    /// <summary>
    /// Write the selected realm into the proxy's own config and prove it landed. Returns false with a
    /// player-facing reason when the realm cannot be honoured (no address, an address that is not safe to
    /// write, an unwritable or unconfirmable proxy config) — the launch is then refused rather than sent
    /// to whatever address the package happens to ship with.
    ///
    /// <para>When no realm accessor is wired at all (unit tests) the proxy config is left untouched and
    /// the launch proceeds exactly as before this existed.</para>
    /// </summary>
    private bool PointProxyAtSelectedRealm(ModernClientLayout layout, out string error)
    {
        error = "";
        if (_realmAddress is null) return true;

        var raw = _realmAddress();
        var address = WowLauncher.Services.RealmAddress.Parse(raw);
        if (address is null)
        {
            _logger.Error("Refusing the modern launch: {Address} is not a usable realm address", raw);
            error = UnusableRealmMessage(raw);
            return false;
        }

        var configPath = Path.Combine(layout.ProxyDir, ProxyConfigName);
        if (!RealmBinding.PointProxyAtRealm(configPath, address, out error)) return false;

        _logger.Information("Proxy pointed at the selected realm: {Address}", address.Value);
        return true;
    }

    private static string UnusableRealmMessage(string? raw) =>
        "The launcher cannot start this realm because its address is not usable.\n" +
        $"Address: {(string.IsNullOrWhiteSpace(raw) ? "(empty)" : raw)}\n" +
        "Open Settings and correct the realm address (a host name or IP, optionally with :port).";

    /// <summary>Point the client at the local proxy and pin the renderer and resolution, then PROVE the
    /// portal line is on disk. Returns false when it is not — the caller must not start anything.
    ///
    /// <para><b>Why this is fail-closed (2026-07-27).</b> Until today a failed write was logged and the
    /// launch continued "with whatever it already holds". The reasoning was that a config from a previous
    /// run may already be correct and refusing to launch would turn a recoverable state into a dead
    /// button. That reasoning is sound but it guarded the wrong thing: it treated the WRITE as the
    /// condition when only the RESULT matters. If the stale config points somewhere else, the client
    /// comes up looking perfectly healthy and talks to the wrong server — the same shape of silent
    /// failure as the 1.12 farclip bug, where a client started fine and rendered a broken world.
    /// Windows and macOS have always read the portal back and aborted
    /// (<c>WindowsModernClientLauncher.WriteAndConfirmPortal</c>); Linux was the odd one out.</para>
    ///
    /// <para>The dead-button worry is answered by checking the file rather than the write: a config that
    /// already carries the right portal passes even when <c>Apply</c> fails, so the recoverable case
    /// still launches. Only an unconfirmable portal stops the launch.</para></summary>
    private bool WriteClientConfig(ModernClientLayout layout)
    {
        var expectedPortal = $"127.0.0.1:{ProxyPort}";
        var settings = new Dictionary<string, string>
        {
            ["portal"] = expectedPortal,
            ["gxApi"] = "D3D12",
        };

        var resolution = _display.Current();
        if (resolution is not null)
            settings["gxFullscreenResolution"] = resolution;
        else
            _logger.Warning(
                "Could not read the display resolution; leaving gxFullscreenResolution as it is. " +
                "If the game starts with no visible window, set it by hand in {Config}", layout.ConfigWtf);

        // The language, and the one rule that governs it: only ever write a locale this installation
        // demonstrably carries. A locale it does not carry is not a fallback to English — the client
        // dies on ERROR #134 ("Can't find file in build manifest") before any window appears, with a
        // Blizzard error box that says nothing about languages (measured 2026-08-03 with itIT against
        // an install that has ten other locales). So the wanted locale is checked against the install
        // and silently downgraded to English if it is not there, rather than handed to a client that
        // would crash on it.
        //
        // Text only. `audioLocale` is deliberately left alone: whether spoken audio is present per
        // locale was not measured, and an unmeasured audioLocale is the same #134 crash with a
        // different file id. Voice-over stays English until someone proves otherwise.
        var wantedLocale = _locale?.Invoke();
        if (!string.IsNullOrWhiteSpace(wantedLocale))
        {
            var installed = InstalledClientLocales.ForClientDir(layout.ClientDir);
            var carriedByClient = installed.Text.Contains(wantedLocale, StringComparer.Ordinal);
            // Two conditions, not one. The client carrying a language only makes the CLIENT speak it;
            // quests, items and NPC text come from the realm, and outside vMaNGOS's four slots they
            // come back English. A saved pick from before that rule (or a hand-edited config) must not
            // survive into a half-translated game.
            var servedByRealm = WowLauncher.Models.ClientLocales.RealmSupported.Contains(wantedLocale);
            if (carriedByClient && servedByRealm)
            {
                settings["textLocale"] = wantedLocale;
            }
            else
            {
                _logger.Warning(
                    "Not starting in {Locale} (client carries it: {Carried}, realm serves it: {Served}; " +
                    "client has: {Available}) — using {Fallback}",
                    wantedLocale, carriedByClient, servedByRealm,
                    string.Join(", ", installed.Text), InstalledClientLocales.Fallback);
                settings["textLocale"] = InstalledClientLocales.Fallback;
            }
        }

        if (WtfConfigWriter.Apply(layout.ConfigWtf, settings))
            _logger.Information(
                "Client config written: portal=127.0.0.1:{Port}, gxApi=D3D12, resolution={Res}",
                ProxyPort, resolution ?? "unchanged");
        else
            _logger.Warning("Could not write {Config}; checking whether it already holds the right portal",
                layout.ConfigWtf);

        // Read back: the portal line must be on disk before proxy or client start. This is the only
        // proof that the client will talk to OUR proxy and not to whatever the file said before.
        //
        // EVERY portal line has to match, not just one (Codex review 2026-07-27). WtfConfigWriter
        // rewrites the FIRST match and leaves any later duplicate untouched, so a file carrying two
        // `SET portal` lines would pass an "at least one matches" check while the client may well take
        // the last one — and then the launcher reports success for a game pointed at the old address.
        // Which line the 1.14.2 client actually honours is not documented anywhere we can rely on, so
        // this refuses to depend on it: if any portal line disagrees, the launch stops.
        try
        {
            var wanted = $"SET portal \"{expectedPortal}\"";
            var portalLines = File.ReadAllLines(layout.ConfigWtf)
                .Select(l => l.Trim())
                .Where(l => WtfConfigWriter.IsPortalLine(l))
                .ToList();

            if (portalLines.Count == 0)
            {
                _logger.Error("Portal readback failed: {Wanted} not found in {Config}", wanted, layout.ConfigWtf);
                return false;
            }

            var disagreeing = portalLines.Where(l => !string.Equals(l, wanted, StringComparison.Ordinal)).ToList();
            if (disagreeing.Count > 0)
            {
                _logger.Error(
                    "Portal readback failed: {Count} of {Total} portal lines in {Config} do not say {Wanted} " +
                    "(first offender: {Offender}). Refusing to start against an ambiguous endpoint",
                    disagreeing.Count, portalLines.Count, layout.ConfigWtf, wanted, disagreeing[0]);
                return false;
            }

            _logger.Information("Client config confirmed on disk: portal={Expected} ({Count} line(s), all agreeing)",
                expectedPortal, portalLines.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Portal readback could not read {Config}", layout.ConfigWtf);
            return false;
        }
    }

    private static string PortalUnconfirmedMessage(string configPath) =>
        "The launcher could not confirm that the game is pointed at the local proxy.\n" +
        $"File: {configPath}\n" +
        "Starting anyway could connect the game to the wrong server, so the launch was stopped.\n" +
        "Check that the file exists, is writable, and holds exactly one 'SET portal' line " +
        "(a leftover second one from an older setup is the usual cause), then try again.";

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

    private static string IncompleteBundleMessage(string exePath) =>
        "This 1.14.2 client cannot be started from where it is. The launcher expects the full client " +
        "package, with the Hermes and Launcher folders next to the World of Warcraft folder.\n" +
        $"Found the client at: {exePath}";

    private static string MissingPartMessage(string missing, ModernClientLayout layout) =>
        $"The 1.14.2 client package is incomplete: {missing} is missing.\n" +
        $"Package folder: {layout.BundleRoot}\n" +
        "Download the client package again and extract all of it.";

    private static string ProxyFailedMessage(string? detail) =>
        "The realm proxy did not start, so the client would not be able to reach the realm.\n" +
        (string.IsNullOrWhiteSpace(detail) ? "" : detail + "\n") +
        "The launcher log has the full output.";

    private static string ClientNeverAppearedMessage() =>
        "The client did not start. The patcher ran, but no game process appeared.\n" +
        "This is usually a Wine problem. A wine-ge runner installed through Lutris is the setup this " +
        "client is tested on. The launcher log has the details.";
}
