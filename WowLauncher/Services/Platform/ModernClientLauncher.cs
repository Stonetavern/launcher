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

    private static bool IsSettingFor(string line, string key)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith("SET ", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = trimmed[4..].TrimStart();
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

    private readonly Serilog.ILogger _logger;
    private readonly IWineHost _wine;
    private readonly IGameProcessDetector _detector;
    private readonly IDisplayResolution _display;
    private readonly Func<ModernClientLayout, IGameProxy> _proxyFactory;
    private readonly IGameSession? _session;
    private readonly TimeSpan _proxyTimeout;
    private readonly TimeSpan _clientAppearTimeout;
    private readonly Func<TimeSpan, Task> _delay;

    public ModernClientLauncher(
        Serilog.ILogger logger,
        IWineHost wine,
        IGameProcessDetector detector,
        IDisplayResolution display,
        Func<ModernClientLayout, IGameProxy> proxyFactory,
        IGameSession? session = null,
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

        WriteClientConfig(layout);

        // Proxy before client. Its working directory is the proxy's OWN directory, not the bundle root:
        // HermesProxy reads its game data (flight paths, spell tables, hotfixes) from a CSV folder
        // beside the binary, and starting it anywhere else makes it die at startup with a
        // DirectoryNotFoundException - the exact failure a mis-scoped packaging exclude caused on
        // 2026-07-21, so this is a known-real failure mode and not a hypothetical.
        var proxy = _proxyFactory(layout);
        var proxyResult = await proxy.StartAndWaitForPortAsync(ProxyPort, _proxyTimeout).ConfigureAwait(false);
        if (!proxyResult.Ready)
        {
            _logger.Error("Modern client launch aborted: proxy not ready ({Error})", proxyResult.Error);
            return GameLaunchResult.Failed(ProxyFailedMessage(proxyResult.Error));
        }

        // Arctium wants a WINDOWS path. Resolving it through winepath (rather than mapping a drive
        // letter into the prefix) means no prefix symlink to create and no quoting trouble with the
        // spaces in "World of Warcraft".
        var windowsPath = await _wine.ToWindowsPathAsync(layout.ClientDir).ConfigureAwait(false);
        if (windowsPath is null)
        {
            await proxy.StopAsync().ConfigureAwait(false);
            return GameLaunchResult.Failed(
                "Wine could not resolve the client path. The launcher log has the details.");
        }

        _logger.Information("Starting the client through Arctium ({Path})", windowsPath);
        var arctium = await _wine.RunAsync(
            layout.ArctiumExe, layout.ArctiumDir, ["--version=ClassicEra", "--path", windowsPath])
            .ConfigureAwait(false);
        if (!arctium.Started)
        {
            await proxy.StopAsync().ConfigureAwait(false);
            return GameLaunchResult.Failed(arctium.Error ?? "The client patcher could not be started.");
        }

        // Wait for the CLIENT. Arctium exiting is normal and means nothing about whether the game came
        // up, so its process is not what gets watched here.
        var appeared = await WaitForClientAsync(layout.ClientExe).ConfigureAwait(false);
        if (!appeared)
        {
            await proxy.StopAsync().ConfigureAwait(false);
            _logger.Error(
                "The client never appeared within {Timeout:F0}s after Arctium ran", _clientAppearTimeout.TotalSeconds);
            return GameLaunchResult.Failed(ClientNeverAppearedMessage());
        }

        _logger.Information("Modern client is running (proxy PID={Pid}, port {Port})", proxyResult.ProcessId, ProxyPort);
        // Hand the live proxy to the session so the launcher (now alive in the tray) can stop it when the
        // game ends, instead of leaving it detached to be reaped next start. When no session is wired
        // (unit tests), the proxy is simply left running as before — production always wires one.
        _session?.AttachProxy(proxy);
        // The PID reported is Arctium's: the client itself was started by Arctium, not by this process,
        // so there is no handle to it here. Callers use the result as a started/failed signal and
        // confirm the real client through IGameProcessDetector, which is what just happened above.
        return GameLaunchResult.Ok(arctium.ProcessId ?? 0);
    }

    /// <summary>Point the client at the local proxy and pin the renderer and resolution. A failure to
    /// write is logged and NOT fatal: an existing config from a previous run may already be correct,
    /// and refusing to launch over it would turn a recoverable state into a dead button.</summary>
    private void WriteClientConfig(ModernClientLayout layout)
    {
        var settings = new Dictionary<string, string>
        {
            ["portal"] = $"127.0.0.1:{ProxyPort}",
            ["gxApi"] = "D3D12",
        };

        var resolution = _display.Current();
        if (resolution is not null)
            settings["gxFullscreenResolution"] = resolution;
        else
            _logger.Warning(
                "Could not read the display resolution; leaving gxFullscreenResolution as it is. " +
                "If the game starts with no visible window, set it by hand in {Config}", layout.ConfigWtf);

        if (WtfConfigWriter.Apply(layout.ConfigWtf, settings))
            _logger.Information(
                "Client config written: portal=127.0.0.1:{Port}, gxApi=D3D12, resolution={Res}",
                ProxyPort, resolution ?? "unchanged");
        else
            _logger.Error("Could not write {Config}; starting anyway with whatever it already holds",
                layout.ConfigWtf);
    }

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
