namespace WowLauncher.Services.Platform;

using System.Diagnostics;

/// <summary>
/// Performs the platform-specific swap of the running launcher binary with a freshly downloaded,
/// hash-verified one, then relaunches. A running process cannot overwrite its own image, hence the
/// detached indirection. Only reached after the hard SHA256 gate in <see cref="UpdateService"/>.
/// </summary>
public interface IUpdateSwapStrategy
{
    /// <summary>False when this platform has no self-update swap yet (Linux until WP7) — the caller
    /// then skips the swap instead of pretending to apply one.</summary>
    bool IsSupported { get; }

    /// <summary>Stage + launch a detached process that swaps <paramref name="currentExePath"/> with
    /// <paramref name="newExePath"/> and relaunches it. The caller MUST exit right after so the
    /// running binary releases its lock.
    /// <para>Codex F4b — two distinct failure modes, matching pre-WP1 semantics: the STAGING step
    /// (writing the swap script) THROWS on failure (a swap that can't even be staged is a hard error
    /// that propagates); the LAUNCH step returns <c>false</c> so the caller cleans up and falls back
    /// to a normal start. Returns <c>true</c> when the detached swap was launched.</para></summary>
    bool ApplySwap(string newExePath, string currentExePath, string appDir);
}

/// <summary>
/// Windows swap — byte-for-byte the detached cmd.exe batch that shipped in UpdateService:
/// wait 2 s, move the new exe over the (now-released) current exe, relaunch, delete self.
/// </summary>
public sealed class WindowsUpdateSwapStrategy : IUpdateSwapStrategy
{
    private readonly Serilog.ILogger _log;

    public WindowsUpdateSwapStrategy(Serilog.ILogger log) => _log = log;

    public bool IsSupported => true;

    public bool ApplySwap(string newExePath, string currentExePath, string appDir)
    {
        var bat = Path.Combine(Path.GetTempPath(), "stonetavern-launcher-update.bat");

        // STAGING — OUTSIDE the try, so a write failure propagates exactly as pre-WP1 (Codex F4b).
        File.WriteAllText(bat, string.Join("\r\n",
            "@echo off",
            "timeout /t 2 /nobreak > nul",
            $"move /Y \"{newExePath}\" \"{currentExePath}\"",
            $"start \"\" \"{currentExePath}\"",
            "del \"%~f0\"") + "\r\n");

        // LAUNCH — caught, returns false so the caller falls back to a normal start (pre-WP1 semantics).
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = appDir,
            });
            _log.Information("Launcher update applied, restarting via {Bat}", bat);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Update swap start failed, starting normally");
            return false;
        }
    }
}

/// <summary>
/// Linux swap — not implemented in V1. Auto-apply self-update requires artefact signing (WP7);
/// until then the Linux path is Check + Notify only (WP4). Reports unsupported so UpdateService
/// refuses the swap cleanly rather than running an unverified strategy.
/// </summary>
public sealed class LinuxUpdateSwapStrategy : IUpdateSwapStrategy
{
    public bool IsSupported => false;

    public bool ApplySwap(string newExePath, string currentExePath, string appDir) =>
        throw new PlatformNotSupportedException(
            "Auto-Apply-Self-Update ist unter Linux noch nicht verfügbar (kommt in WP7 nach Artefakt-Signierung). " +
            "Bis dahin nur Update-Prüfung + Hinweis (WP4).");
}

/// <summary>
/// Neutral swap for platforms without a dedicated implementation yet (macOS and any other host,
/// Codex F6b). Reports unsupported so UpdateService refuses the swap cleanly; the message carries no
/// "Linux"/"WP7" wording.
/// </summary>
public sealed class UnsupportedUpdateSwapStrategy : IUpdateSwapStrategy
{
    public bool IsSupported => false;

    public bool ApplySwap(string newExePath, string currentExePath, string appDir) =>
        throw new PlatformNotSupportedException(
            "Der automatische Launcher-Selbst-Update wird auf diesem Betriebssystem noch nicht unterstützt.");
}
