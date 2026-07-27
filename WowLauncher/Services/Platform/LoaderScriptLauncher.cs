using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace WowLauncher.Services.Platform;

/// <summary>
/// Legacy (1.12.1) launch through the client package's own loader script when it ships one.
///
/// <para><b>Why this exists.</b> The tuned 1.12.1 client (Stonetavern-Classic) does not run by starting
/// <c>WoW.exe</c> directly. Its documented, tested entry point is <c>launch.sh</c> (Linux), which runs
/// the chain <c>wine VanillaFixes.exe WoW_tweaked.exe</c> — VanillaFixes is the mandatory RDTSC timing
/// loader (without it the 5875 client stutters on modern hardware) — and additionally places the DXVK
/// <c>d3d9.dll</c> next to the exe with a <c>d3d9=n,b</c> override (Direct3D 9 on Vulkan; without it the
/// client falls back to slow wined3d), writes the resolution from the real monitor (a too-small value
/// crops the client UI), and handles the first-run TOS. Starting <c>wine WoW.exe</c> ourselves silently
/// skips all of that: the client comes up looking fine but stutters, renders on wined3d and may show a
/// cropped UI. So when the install carries <c>launch.sh</c>, we run IT, not the bare exe.</para>
///
/// <para><b>Fallback.</b> A client WITHOUT a loader script (an older package, a player's own install)
/// keeps exactly today's behaviour: delegate to the inner launcher (<c>wine WoW.exe</c>). The decision
/// is per-launch off what is actually on disk, so nothing regresses for installs that never had a loader.</para>
///
/// <para>The script self-locates (<c>CLIENT_DIR</c> from <c>BASH_SOURCE</c>) and <c>cd</c>s into its own
/// directory, so we only need to hand <c>bash</c> the script path. It manages its own WINEPREFIX default,
/// matching the environment the package is proven against.</para>
/// </summary>
public sealed class LoaderScriptLauncher : IGameLauncher
{
    /// <summary>The loader script name the tuned Linux client ships (see class remarks).</summary>
    internal const string LoaderName = "launch.sh";

    private readonly IGameLauncher _inner;
    private readonly Serilog.ILogger _log;
    private readonly Func<string, GameLaunchResult>? _runScript;

    /// <param name="inner">The launcher used when there is no loader script (the existing wine start).</param>
    /// <param name="runScript">Test seam: given the resolved script path, start it and return the result.
    /// Null = start <c>bash &lt;script&gt;</c> for real.</param>
    public LoaderScriptLauncher(IGameLauncher inner, Serilog.ILogger log, Func<string, GameLaunchResult>? runScript = null)
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

        _log.Information("Launching the tuned client through its loader script {Script}", script);
        try
        {
            return _runScript is not null ? _runScript(script) : StartBash(script);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Loader script {Script} failed to start", script);
            return GameLaunchResult.Failed($"Could not start the client loader: {ex.Message}");
        }
    }

    /// <summary>
    /// The loader script for this install, or null when there is none. Looks in the working directory and
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

    private GameLaunchResult StartBash(string script)
    {
        var psi = new ProcessStartInfo("bash")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(script) ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add(script);
        var proc = Process.Start(psi);
        if (proc is null)
            return GameLaunchResult.Failed("The client loader did not start.");
        _log.Information("Client loader started (pid {Pid})", proc.Id);
        return GameLaunchResult.Ok(proc.Id);
    }
}
