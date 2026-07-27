using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace WowLauncher.Services.Platform;

/// <summary>
/// Windows counterpart to <see cref="LoaderScriptLauncher"/>: legacy (1.12.1) launch through the
/// client package's own loader batch file when it ships one.
///
/// <para><b>Why this exists.</b> The tuned 1.12.1 client (Stonetavern-Classic) does not run by starting
/// <c>WoW.exe</c> directly — on Windows just as on Linux. Its documented entry point is <c>launch.bat</c>
/// (the Windows pendant to <c>launch.sh</c>), which runs the chain <c>VanillaFixes.exe WoW_tweaked.exe</c>
/// — VanillaFixes is the mandatory RDTSC timing loader (without it the 5875 client stutters on modern
/// hardware) — and additionally places the DXVK <c>d3d9.dll</c> next to the exe (Direct3D 9 on Vulkan;
/// without it the client falls back to slow native D3D9), writes the resolution from the real monitor,
/// and handles the first-run TOS. Starting <c>WoW.exe</c> ourselves silently skips all of that: the
/// client comes up looking fine but stutters, renders without DXVK and may show a cropped UI. So when the
/// install carries <c>launch.bat</c>, we run IT, not the bare exe. This closes the Windows half of the
/// same silent-degradation the Linux <see cref="LoaderScriptLauncher"/> closed.</para>
///
/// <para><b>Fallback.</b> A client WITHOUT a loader batch (an older package, a player's own install)
/// keeps exactly today's Windows behaviour: delegate to the inner launcher
/// (<see cref="WindowsGameLauncher"/>, the plain native <c>Process.Start(WoW.exe)</c>). The decision is
/// per-launch off what is actually on disk, so nothing regresses for installs that never had a loader.</para>
///
/// <para>A <c>.bat</c> cannot be started with <c>UseShellExecute=false</c> directly, so we invoke it
/// through <c>cmd.exe /c &lt;script&gt;</c> with the client directory as the working dir — the batch
/// self-locates its own folder either way, matching how the package is proven.</para>
/// </summary>
public sealed class WindowsLoaderScriptLauncher : IGameLauncher
{
    /// <summary>The loader batch name the tuned Windows client ships (see class remarks).</summary>
    internal const string LoaderName = "launch.bat";

    private readonly IGameLauncher _inner;
    private readonly Serilog.ILogger _log;
    private readonly Func<string, GameLaunchResult>? _runScript;

    /// <param name="inner">The launcher used when there is no loader batch (the existing native start).</param>
    /// <param name="runScript">Test seam: given the resolved script path, start it and return the result.
    /// Null = start <c>cmd.exe /c &lt;script&gt;</c> for real.</param>
    public WindowsLoaderScriptLauncher(IGameLauncher inner, Serilog.ILogger log, Func<string, GameLaunchResult>? runScript = null)
    {
        _inner = inner;
        _log = log;
        _runScript = runScript;
    }

    public async Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory)
    {
        var script = FindLoaderScript(exePath, workingDirectory, File.Exists);
        if (script is null)
            return await _inner.LaunchAsync(exePath, workingDirectory).ConfigureAwait(false);

        _log.Information("Launching the tuned client through its loader batch {Script}", script);
        try
        {
            return _runScript is not null ? _runScript(script) : StartCmd(script);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Loader batch {Script} failed to start", script);
            return GameLaunchResult.Failed($"Could not start the client loader: {ex.Message}");
        }
    }

    /// <summary>
    /// The loader batch for this install, or null when there is none. Looks in the working directory and
    /// next to the resolved exe (the client dir either way). Pure so a test can drive it without disk.
    /// </summary>
    internal static string? FindLoaderScript(string? exePath, string? workingDirectory, Func<string, bool> fileExists)
    {
        foreach (var dir in CandidateDirs(exePath, workingDirectory))
        {
            var candidate = Path.Combine(dir, LoaderName);
            if (fileExists(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirs(string? exePath, string? workingDirectory)
    {
        if (!string.IsNullOrEmpty(workingDirectory)) yield return workingDirectory;
        var exeDir = string.IsNullOrEmpty(exePath) ? null : Path.GetDirectoryName(exePath);
        if (!string.IsNullOrEmpty(exeDir) && !string.Equals(exeDir, workingDirectory, StringComparison.Ordinal))
            yield return exeDir;
    }

    private GameLaunchResult StartCmd(string script)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(script) ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(script);
        var proc = Process.Start(psi);
        if (proc is null)
            return GameLaunchResult.Failed("The client loader did not start.");
        _log.Information("Client loader started (pid {Pid})", proc.Id);
        return GameLaunchResult.Ok(proc.Id);
    }
}
