namespace WowLauncher.Services.Platform;

using System.ComponentModel;
using System.Diagnostics;

/// <summary>
/// Immutable configuration for umu/Proton launches. The prefix path is injectable so tests can point
/// at a throwaway TEMP prefix and never touch a real one (same reasoning as <see cref="WineOptions"/>).
/// </summary>
public sealed record UmuOptions(string PrefixPath, string GameId = "umu-default", string UmuBinary = "umu-run")
{
    /// <summary>The managed Proton prefix under the launcher's data dir, parallel to
    /// <see cref="WineOptions.DefaultPrefixPath"/> — never a Steam/Lutris prefix the player already
    /// has, so the launcher never touches state it does not own.</summary>
    public static string DefaultPrefixPath() => Path.Combine(AppPaths.ForCurrentOs().ShareDir, "proton-prefix");

    public static UmuOptions ForShareDir(string shareDir) => new(Path.Combine(shareDir, "proton-prefix"));

    public static UmuOptions Default() => ForShareDir(AppPaths.ForCurrentOs().ShareDir);
}

/// <summary>
/// <b>Fallback</b> launcher for the modern 1.14.2/Classic Era client: runs it under Proton via
/// <c>umu-run</c> (Open-Wine-Components/umu-launcher). Used only when no Lutris wine-ge runner is
/// installed - see <see cref="LinuxGameLauncherRouter"/> for why wine-ge is the first choice
/// (owner decision 2026-07-21; an earlier note claimed D3D12 was Proton-only, which the working
/// start script disproves). 1.12.1 stays on <see cref="WineGameLauncher"/>: it is a proven, 32-bit,
/// D3D9-only client with nothing to gain from a container it does not need.
///
/// <para><b>Env contract</b> (verified against the project's own docs before writing this — GitHub
/// wiki FAQ + man page, not invented, per CORE §3 "externe Claims sind Hypothesen"): <c>GAMEID</c>
/// (arbitrary or a umu-database id; <c>"umu-default"</c> applies no game-specific fixes),
/// <c>PROTONPATH</c> (optional, defaults to UMU-Proton — left unset here, so umu manages its own
/// Proton build), <c>WINEPREFIX</c> (optional, defaults to <c>$HOME/Games/umu/$GAMEID</c> — set
/// explicitly to the launcher's own managed prefix, same reasoning as
/// <see cref="WineGameLauncher"/> never using <c>~/.wine</c>).</para>
///
/// <para><b>Not attempted here</b> (both still open, tracked in the handoff): automated,
/// hash-verified, version-pinned acquisition of <c>umu-launcher</c> itself, and a real
/// download-progress UI for umu's own ~2.9 GB first-run fetch of steamrt/UMU-Proton. This class
/// assumes <c>umu-run</c> is already resolvable (player-installed, or bundled later by whichever
/// package decides that question) and reports an actionable message when it is not — the same shape
/// <see cref="WineGameLauncher"/> uses for a missing Wine.</para>
/// </summary>
public sealed class UmuGameLauncher : IGameLauncher
{
    private readonly Serilog.ILogger _logger;
    private readonly UmuOptions _options;

    public UmuGameLauncher(Serilog.ILogger logger, UmuOptions options)
    {
        _logger = logger;
        _options = options;
    }

    public async Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory)
    {
        if (!File.Exists(exePath))
            return GameLaunchResult.Failed($"Client executable not found: {exePath}");

        var probe = await ProbeAsync().ConfigureAwait(false);
        if (!probe.Ok)
            return GameLaunchResult.Failed(probe.Error!);

        try
        {
            Directory.CreateDirectory(_options.PrefixPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not create the Proton prefix directory: {Prefix}", _options.PrefixPath);
            return GameLaunchResult.Failed(
                $"Could not create the Proton prefix ({_options.PrefixPath}): {ex.Message}");
        }

        try
        {
            var psi = new ProcessStartInfo(probe.UmuBinary!)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(exePath);
            psi.Environment["GAMEID"] = _options.GameId;
            psi.Environment["WINEPREFIX"] = _options.PrefixPath;

            using var process = Process.Start(psi);
            if (process is null)
            {
                _logger.Fatal("Umu start failed: Process.Start returned null");
                return GameLaunchResult.Failed("Process.Start returned null");
            }

            var pid = process.Id;
            _logger.Information(
                "WoW started via umu/Proton (PID={Pid}, prefix={Prefix})", pid, _options.PrefixPath);
            return GameLaunchResult.Ok(pid);
        }
        catch (Exception ex)
        {
            _logger.Fatal(ex, "Umu start failed: {Exe}", exePath);
            return GameLaunchResult.Failed($"Umu start failed: {ex.Message}");
        }
    }

    /// <summary>Confirm <c>umu-run</c> actually resolves and answers before attempting the real
    /// launch — a bare PATH check is not proof (the same "functional, not cosmetic" reasoning
    /// <see cref="WineGameLauncher"/> uses for wine itself).</summary>
    private async Task<UmuReadiness> ProbeAsync()
    {
        try
        {
            var psi = new ProcessStartInfo(_options.UmuBinary)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--help");

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return UmuReadiness.NotReady("umu-run did not respond in time.");
            }

            await stdoutTask.ConfigureAwait(false);
            // umu-run --help exits 0 when it can actually run; anything else means it is present but
            // broken (missing a dependency it needs at startup), which is still "not usable" for us.
            return process.ExitCode == 0
                ? UmuReadiness.Ready(_options.UmuBinary)
                : UmuReadiness.NotReady(UmuBrokenMessage());
        }
        catch (Win32Exception)
        {
            return UmuReadiness.NotReady(UmuMissingMessage());
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "umu-run readiness check failed");
            return UmuReadiness.NotReady(UmuMissingMessage());
        }
    }

    private static string UmuMissingMessage() =>
        "The 1.14.2 client needs a Wine build that provides D3D12, and none was found.\n" +
        "Either option works:\n" +
        "1. Install a wine-ge runner through Lutris. This is the setup the client is tested on:\n" +
        "   https://lutris.net/downloads\n" +
        "2. Install umu-launcher to run the client under Proton. It downloads about 3 GB on first run:\n" +
        "   https://github.com/Open-Wine-Components/umu-launcher\n" +
        "The 1.12.1 client is not affected and still works.";

    private static string UmuBrokenMessage() =>
        "umu-run is installed but did not start correctly. Check the launcher log for details.";

    private readonly record struct UmuReadiness(bool Ok, string? UmuBinary, string? Error)
    {
        public static UmuReadiness Ready(string umuBinary) => new(true, umuBinary, null);
        public static UmuReadiness NotReady(string error) => new(false, null, error);
    }
}

/// <summary>
/// Linux only: picks the runtime a launch goes through, off the resolved exe's OWN name - not the OS,
/// not a config flag. <c>RealmEntry</c>/<c>ClientVersion</c> already carry which build a launch is
/// for; the exe name restates that same fact where this class can see it without new plumbing
/// (<see cref="ClientService"/> already resolves build-specific exe names via
/// <c>ClientVersion.ExeName</c>), and the modern/legacy split itself is read from
/// <c>ClientVersion.NeedsModernRuntime</c> rather than a hardcoded build number.
///
/// <para><b>Three targets, in this order of preference (owner decision 2026-07-21):</b></para>
/// <list type="number">
/// <item>legacy client (1.12.1) -> system Wine. Proven, 32-bit, D3D9, gains nothing from a container.</item>
/// <item>modern client (1.14.2) -> <b>wine-ge</b>, when a Lutris runner is installed. This is the only
/// path proven end to end on this project: <c>Play Stonetavern.sh</c> starts the client this way and a
/// character reached the world with it. wine-ge carries the D3D12 layer in the runner itself.</item>
/// <item>modern client with no wine-ge -> umu/Proton as the fallback. It is the second choice, not the
/// first: it downloads roughly 2.9 GB on first run and has never been proven with this client. An
/// earlier note had this the other way round, claiming D3D12 was Proton-only, which the working script
/// disproves.</item>
/// </list>
///
/// <para>The modern launcher is resolved ONCE at construction, not per launch, so the choice is a
/// property of the session that the log records at startup rather than something that can silently
/// differ between two clicks of the same button.</para>
/// </summary>
public sealed class LinuxGameLauncherRouter : IGameLauncher
{
    private readonly IGameLauncher _legacyWine;
    private readonly IGameLauncher? _modernWine;
    private readonly IGameLauncher _modernFallback;
    private readonly Serilog.ILogger _logger;

    /// <param name="legacyWine">System Wine, for the 32-bit builds.</param>
    /// <param name="modernWine">wine-ge for the modern client, or null when no wine-ge is installed.</param>
    /// <param name="modernFallback">umu/Proton, used for the modern client only when
    /// <paramref name="modernWine"/> is null.</param>
    public LinuxGameLauncherRouter(
        IGameLauncher legacyWine,
        IGameLauncher? modernWine,
        IGameLauncher modernFallback,
        Serilog.ILogger logger)
    {
        _legacyWine = legacyWine;
        _modernWine = modernWine;
        _modernFallback = modernFallback;
        _logger = logger;
    }

    public Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory)
    {
        var exeName = Path.GetFileName(exePath);
        var target = NeedsModernRuntime(exeName)
            ? _modernWine ?? _modernFallback
            : _legacyWine;
        _logger.Debug("Routing launch of {Exe} to {Launcher}", exeName, target.GetType().Name);
        return target.LaunchAsync(exePath, workingDirectory);
    }

    /// <summary>Whether this executable belongs to a build needing the modern (64-bit, D3D12) runtime.
    /// Delegates to <c>ClientVersion</c> so adding a second modern build is a one-line change there and
    /// not a forgotten edit here.</summary>
    internal static bool NeedsModernRuntime(string exeName) =>
        WowLauncher.Models.ClientVersion.ExeNameNeedsModernRuntime(exeName);
}
