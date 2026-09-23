namespace WowLauncher.Services.Platform;

using System.Diagnostics;

/// <summary>
/// Runs the modern 1.14.2 client's Wine-side steps (Arctium, then the client itself) under a
/// Steam-installed <b>GE-Proton</b> instead of plain Wine, exactly the way the owner's measured beta
/// script does it (<c>/mnt/data/wow/beta-test-linux/Play Stonetavern (JimsProxy-Beta).sh</c>, Ledger run
/// U — in the world). KONZEPT §13 puts this FIRST in the Linux runner order, ahead of wine-ge/system
/// Wine: JimsProxy v5.2.1-beta.4 no longer crashes on world entry (see
/// <see cref="ProxyBinaryResolver"/>'s superseded <c>WineCrashNote</c>), and that fix was measured
/// under GE-Proton specifically.
///
/// <para><b>Same shape as <see cref="UmuGameLauncher"/>, with two real differences.</b> Both are
/// Proton-family runners with a readiness check before the real launch and a prefix directory created
/// up front (<see cref="UmuGameLauncher"/>-style, <c>UmuGameLauncher.cs</c> line 71). What differs:
/// (1) the invocation shape is <c>&lt;GE-Proton dir&gt;/proton run &lt;exe&gt;</c>, not a separate
/// <c>umu-run</c> binary with <c>GAMEID</c>/<c>WINEPREFIX</c> env vars - GE-Proton needs BOTH
/// <c>STEAM_COMPAT_DATA_PATH</c> and <c>STEAM_COMPAT_CLIENT_INSTALL_PATH</c>
/// (<see cref="GeProtonEnvironment"/>); (2) this class also implements <see cref="IWineHost"/>, because
/// unlike umu it DOES sit inside <see cref="ModernClientLauncher"/>'s proxy-then-Arctium-then-client
/// sequence — the proxy keeps starting exactly as it does today (<c>HermesProxyRunner</c>, native
/// JimsProxy from S13's Linux runner order); only the WINE STEP underneath Arctium changes.</para>
///
/// <para><b>D3D12, checked against the actual script text (2026-09-20), not assumed.</b> The
/// task brief this class was written from expected D3D12/vkd3d environment variables around line 104
/// of the reference script. The script that exists on disk (156 lines total) sets none: D3D12 selection
/// happens entirely through <c>Config.wtf</c>'s <c>gxApi</c> key
/// (<see cref="WtfConfigWriter"/> already writes it for every Linux/macOS modern-client path), which is
/// why no environment variable for it appears here either — inventing one the measured evidence does
/// not contain would be exactly the kind of unmeasured claim CORE §3 warns against.</para>
/// </summary>
public sealed class ProtonGameLauncher : IGameLauncher, IWineHost
{
    private readonly Serilog.ILogger _logger;
    private readonly string _protonExePath;
    private readonly string _steamCompatClientInstallPath;

    public ProtonGameLauncher(Serilog.ILogger logger, string protonExePath, string steamCompatClientInstallPath)
    {
        _logger = logger;
        _protonExePath = protonExePath;
        _steamCompatClientInstallPath = steamCompatClientInstallPath;
    }

    /// <inheritdoc cref="IGameLauncher.LaunchAsync"/>
    public Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory) =>
        RunAsync(exePath, workingDirectory, []);

    /// <summary><see cref="IWineHost.RunAsync"/>. The bundle root — needed for the per-install,
    /// per-GE-Proton-version prefix <see cref="GeProtonEnvironment.Build"/> scopes the launch to — is
    /// derived from <paramref name="workingDirectory"/> the same way <see cref="ModernClientLayout"/>
    /// itself derives it: <c>ModernClientLauncher</c> always calls this with the Arctium directory
    /// (<c>&lt;root&gt;/Launcher</c>) as the working directory, so its parent IS the bundle root. No
    /// extra plumbing through the DI graph is needed for that reason - the path already carries it.
    /// </summary>
    public async Task<GameLaunchResult> RunAsync(string exePath, string workingDirectory, IReadOnlyList<string> args)
    {
        if (!File.Exists(exePath))
            return GameLaunchResult.Failed($"Client executable not found: {exePath}");

        if (!IsProtonReady())
            return GameLaunchResult.Failed(ProtonMissingMessage());

        var installRoot = BundleRootFromWorkingDir(workingDirectory);
        var env = GeProtonEnvironment.Build(
            installRoot, GeProtonEnvironment.DirNameFromProtonExe(_protonExePath), _steamCompatClientInstallPath);

        try
        {
            Directory.CreateDirectory(env["STEAM_COMPAT_DATA_PATH"]);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not create the GE-Proton prefix directory: {Prefix}", env["STEAM_COMPAT_DATA_PATH"]);
            return GameLaunchResult.Failed(
                $"Could not create the GE-Proton prefix ({env["STEAM_COMPAT_DATA_PATH"]}): {ex.Message}");
        }

        try
        {
            var psi = new ProcessStartInfo(_protonExePath)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add(exePath);
            foreach (var a in args) psi.ArgumentList.Add(a);
            foreach (var (key, value) in env) psi.Environment[key] = value;

            using var process = Process.Start(psi);
            if (process is null)
            {
                _logger.Fatal("GE-Proton start failed: Process.Start returned null");
                return GameLaunchResult.Failed("Process.Start returned null");
            }

            var pid = process.Id;
            _logger.Information(
                "WoW started via GE-Proton (PID={Pid}, prefix={Prefix})", pid, env["STEAM_COMPAT_DATA_PATH"]);
            return GameLaunchResult.Ok(pid);
        }
        catch (Exception ex)
        {
            _logger.Fatal(ex, "GE-Proton start failed: {Exe}", exePath);
            return GameLaunchResult.Failed($"GE-Proton start failed: {ex.Message}");
        }
    }

    /// <summary><see cref="IWineHost.ToWindowsPathAsync"/>. The measured script resolves this with
    /// <c>proton run winepath -w &lt;path&gt;</c> and, when that produces nothing usable, falls back to
    /// a plain <c>Z:</c> drive mapping (script lines 119-121) rather than refusing outright - GE-Proton
    /// maps the whole Unix filesystem under <c>Z:</c> by default, so the fallback is correct even on a
    /// prefix <c>winepath</c> itself could not be run against yet.</summary>
    public async Task<string?> ToWindowsPathAsync(string unixPath)
    {
        var installRoot = BundleRootFromClientDir(unixPath);
        if (IsProtonReady())
        {
            var env = GeProtonEnvironment.Build(
                installRoot, GeProtonEnvironment.DirNameFromProtonExe(_protonExePath), _steamCompatClientInstallPath);
            try
            {
                var psi = new ProcessStartInfo(_protonExePath)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("run");
                psi.ArgumentList.Add("winepath");
                psi.ArgumentList.Add("-w");
                psi.ArgumentList.Add(unixPath);
                foreach (var (key, value) in env) psi.Environment[key] = value;

                using var process = Process.Start(psi);
                if (process is not null)
                {
                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    _ = process.StandardError.ReadToEndAsync();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try
                    {
                        await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                        var stdout = await stdoutTask.ConfigureAwait(false);
                        var line = stdout.Trim();
                        if (process.ExitCode == 0 && line.Length > 0 && !line.Contains('\n'))
                            return line;
                    }
                    catch (OperationCanceledException)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "proton run winepath failed for {Path}", unixPath);
            }
        }

        // Fall back to the plain Z: mapping the measured script uses when winepath comes up empty -
        // a pure function so this branch is provable without ever spawning proton.
        _logger.Information("winepath via GE-Proton did not resolve {Path} - using the Z: drive fallback", unixPath);
        return ZDriveFallback(unixPath);
    }

    /// <summary>The plain <c>Z:</c> drive mapping the measured script falls back to (line 121:
    /// <c>WOWPATH="Z:$(printf '%s' "$WOWDIR" | sed 's|/|\\|g')"</c>) - GE-Proton's default prefix maps
    /// the whole host filesystem under <c>Z:</c>, so this is correct without ever running winepath.
    /// A pure function, not a shell-out, exactly so it can be proven with a plain unit test rather than
    /// a real GE-Proton install.</summary>
    internal static string ZDriveFallback(string unixPath) => "Z:" + unixPath.Replace('/', '\\');

    /// <summary>The bundle root from the Arctium working directory <see cref="ModernClientLauncher"/>
    /// always passes to <see cref="RunAsync"/> (<c>&lt;root&gt;/Launcher</c>) - one level up.</summary>
    internal static string BundleRootFromWorkingDir(string arctiumWorkingDir) =>
        Path.GetDirectoryName(Path.GetFullPath(arctiumWorkingDir)) ?? arctiumWorkingDir;

    /// <summary>The bundle root from the client directory <see cref="ModernClientLauncher"/> always
    /// passes to <see cref="ToWindowsPathAsync"/> (<c>&lt;root&gt;/World of Warcraft/_classic_era_</c>)
    /// - two levels up, the same relationship <see cref="ModernClientLayout.Resolve"/> itself derives
    /// the bundle root with.</summary>
    internal static string BundleRootFromClientDir(string clientDir) =>
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(clientDir))) ?? clientDir;

    /// <summary>File-exists-and-executable, not a subprocess probe like <see cref="UmuGameLauncher"/>'s
    /// <c>umu-run --help</c>. Deliberate: <c>proton</c> is a wrapper script that expects the full
    /// STEAM_COMPAT environment before doing anything useful, and the measured script never
    /// health-checks it either - it goes straight from "the file is executable" to starting the real
    /// game. Inventing a synthetic readiness command GE-Proton was never observed answering would be an
    /// unmeasured claim, not a stronger check. <see cref="GeProtonLocator.FindLatest"/> already proves
    /// this once when it discovers the binary; this is the same check repeated defensively at the point
    /// of use, in case the caller was constructed with a since-uninstalled path.</summary>
    private bool IsProtonReady()
    {
        if (!File.Exists(_protonExePath)) return false;
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            var mode = File.GetUnixFileMode(_protonExePath);
            const UnixFileMode anyExecute = UnixFileMode.UserExecute |
                                            UnixFileMode.GroupExecute |
                                            UnixFileMode.OtherExecute;
            return (mode & anyExecute) != 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private string ProtonMissingMessage() =>
        "The 1.14.2 client needs GE-Proton, and it could not be started.\n" +
        $"Expected an executable proton script at: {_protonExePath}\n" +
        "Install Steam and place a GE-Proton build under " +
        "~/.local/share/Steam/compatibilitytools.d/, then start the game again.\n" +
        "The 1.12.1 client is not affected and still works.";
}
