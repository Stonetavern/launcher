namespace WowLauncher.Services.Platform;

/// <summary>
/// Outcome of a client-start attempt. <see cref="Started"/> is the only thing callers act on;
/// <see cref="ProcessId"/>/<see cref="Error"/> are diagnostics (the launcher exits right after a
/// successful start, so the handle does not outlive it).
/// </summary>
public sealed record GameLaunchResult(bool Started, int? ProcessId = null, string? Error = null)
{
    public static GameLaunchResult Ok(int pid) => new(true, pid);
    public static GameLaunchResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// Starts the WoW client executable. The platform difference (native ProcessStart on Windows,
/// Wine on Linux via <see cref="WineGameLauncher"/>) lives entirely behind this seam;
/// <see cref="ClientService"/> stays platform-neutral and just resolves the exe + working directory.
/// </summary>
public interface IGameLauncher
{
    /// <summary>Start the client at <paramref name="exePath"/> with <paramref name="workingDirectory"/>
    /// as its cwd. Returns a <see cref="GameLaunchResult"/>; never throws.</summary>
    Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory);

    /// <summary>
    /// Ob ein Start hier ueberhaupt gelingen kann - null heisst ja, sonst steht hier der Satz, den
    /// ein Spieler zu lesen bekaeme.
    ///
    /// <para><b>Warum getrennt vom Start.</b> Auf Linux entscheidet sich das an Dingen, die der
    /// Launcher nicht mitliefert: ob Wine da ist, ob es 32-Bit kann, ob der Grafiktreiber Vulkan in
    /// der noetigen Fassung meldet. Bis hierher erfuhr man das erst NACH dem Druck auf Spielen, und
    /// nur dann. Es war damit auch von aussen nicht messbar: der erste Lauf in einer nackten VM am
    /// 2026-08-05 konnte genau diese Meldung nicht pruefen, weil sie ohne Klick nicht entsteht - und
    /// der Klick war in der VM nicht ausloesbar.</para>
    ///
    /// <para>Die Vorgabe meldet „bereit". Windows und macOS bringen ihre Laufzeit mit; eine Pruefung,
    /// die dort etwas behauptet, waere eine Meldung ueber einen Zustand, den es nicht gibt.</para>
    /// </summary>
    /// <param name="exeName">Der Dateiname des Clients, der gestartet wuerde. Notwendig, weil unter
    /// Linux zwei sehr verschiedene Wege dahinterstehen: 1.12.1 laeuft auf System-Wine, 1.14.2
    /// braucht eine Wine mit D3D12 und einen Proxy. Ohne diese Angabe muesste die Weiche raten - und
    /// eine Bereitschaftspruefung, die den falschen Zweig prueft, ist schlimmer als keine.</param>
    Task<string?> CheckReadyAsync(string exeName) => Task.FromResult<string?>(null);
}

/// <summary>
/// Windows launcher — byte-for-byte the behaviour that shipped in ClientService.LaunchAsync:
/// ProcessStartInfo(exe) with UseShellExecute=false, WorkingDirectory set, no arguments.
/// </summary>
public sealed class WindowsGameLauncher : IGameLauncher
{
    private readonly Serilog.ILogger _logger;

    public WindowsGameLauncher(Serilog.ILogger logger) => _logger = logger;

    public Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exePath)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
            };

            var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                _logger.Fatal("Failed to start WoW.exe — Process.Start returned null");
                return Task.FromResult(GameLaunchResult.Failed("Process.Start returned null"));
            }

            _logger.Information("WoW launched (PID={Pid})", process.Id);
            return Task.FromResult(GameLaunchResult.Ok(process.Id));
        }
        catch (Exception ex)
        {
            _logger.Fatal(ex, "Failed to launch WoW.exe");
            return Task.FromResult(GameLaunchResult.Failed(ex.Message));
        }
    }
}

/// <summary>
/// Neutral launcher for platforms without a dedicated implementation yet (macOS and any other host,
/// Codex F6b). Carries a platform-neutral message — no "Linux"/"Wine" wording — so a macOS user
/// isn't told this is a Linux problem. Linux itself is covered by <see cref="WineGameLauncher"/>,
/// registered in DI; macOS gets its own IGameLauncher (CrossOver) per AGENTS.md.
/// </summary>
public sealed class UnsupportedGameLauncher : IGameLauncher
{
    private const string Message =
        "Client launch is not supported on this operating system yet.";

    private readonly Serilog.ILogger _logger;

    public UnsupportedGameLauncher(Serilog.ILogger logger) => _logger = logger;

    public Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory)
    {
        _logger.Error("Client launch not supported on this operating system (WP1 stub): {Path}", exePath);
        return Task.FromResult(GameLaunchResult.Failed(Message));
    }
}
