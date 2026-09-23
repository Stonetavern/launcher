namespace WowLauncher.Services.Platform;

using System.Diagnostics;
using WowLauncher.Models;

/// <summary>
/// The <c>START.sh</c> the 1.12.1 client package ships for Linux (since 2026-09-22): it downloads a
/// PINNED GE-Proton (+ umu and the Valve runtime container where the kernel allows it), checks every
/// file by SHA-256 and runs the package's loader chain through it. No Steam, no system Wine, no sudo.
///
/// <para><b>Why the launcher hands this to the script instead of doing it itself.</b> A player who
/// downloads the client from the website runs the very same script. One code path means the launcher
/// and a manual start can never disagree about which runtime, which prefix or which mode is used, and
/// a runtime bump is a new package version, patched like any other file. Measured across Ubuntu
/// 22.04/24.04/26.04, Mint 22, Debian 12/13, Fedora 41/44, Arch, openSUSE, Alma 9/10, with the real
/// GPU (BEFUND-2026-09-22-ist-stand-clients-und-proton.md §3.6c/§3.6d).</para>
///
/// <para>Contract with the script: <c>--check</c> (fast, exit 0 = this machine can run it, reasons on
/// stderr as lines starting with <c>x </c>), <c>--prepare</c> (download/verify, prints
/// <c>STPROGRESS &lt;step&gt; &lt;percent&gt; &lt;text&gt;</c> on stdout), no argument = play.</para>
/// </summary>
public static class LinuxStartScript
{
    public const string Name = "START.sh";

    /// <summary>The script in <paramref name="clientDir"/>, or null. Linux only, legacy client only —
    /// the modern client has its own proxy/Arctium sequence and never goes through this.</summary>
    public static string? Find(string? clientDir, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(clientDir)) return null;
        var path = Path.Combine(clientDir, Name);
        return (fileExists ?? File.Exists)(path) ? path : null;
    }

    /// <summary>One <c>STPROGRESS</c> line, or null for anything else. Pure, so the parser is provable.</summary>
    public static (string Step, int Percent, string Text)? ParseProgress(string? line)
    {
        if (string.IsNullOrEmpty(line) || !line.StartsWith("STPROGRESS ", StringComparison.Ordinal)) return null;
        var parts = line.Split(' ', 4, StringSplitOptions.None);
        if (parts.Length < 3 || !int.TryParse(parts[2], out var pct)) return null;
        return (parts[1], Math.Clamp(pct, 0, 100), parts.Length > 3 ? parts[3] : "");
    }

    /// <summary>What a player should read when the script refused: its own "x ..." reason lines and the
    /// final "Stonetavern: ..." line, in order. Falls back to the last non-empty stderr lines so a
    /// failure never arrives as an empty message.</summary>
    public static string PlayerMessage(IReadOnlyList<string> stderrLines)
    {
        var reasons = stderrLines
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("x ", StringComparison.Ordinal) || l.StartsWith("Stonetavern:", StringComparison.Ordinal))
            .ToList();
        if (reasons.Count > 0) return string.Join("\n", reasons);
        var tail = stderrLines.Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3).ToList();
        return tail.Count > 0 ? string.Join("\n", tail) : "The Linux start script failed without a message.";
    }

    /// <summary>Runs <c>bash START.sh &lt;arg&gt;</c>, streaming stdout lines to <paramref name="onStdout"/>.
    /// Returns the exit code and all stderr lines.</summary>
    public static async Task<(int Exit, IReadOnlyList<string> Stderr)> RunAsync(
        string script, string arg, Action<string>? onStdout, TimeSpan? timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("bash")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(script) ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("bash did not start");
        var stderr = new List<string>();
        var errTask = Task.Run(async () =>
        {
            while (await p.StandardError.ReadLineAsync(ct).ConfigureAwait(false) is { } l)
                lock (stderr) stderr.Add(l);
        }, ct);
        var outTask = Task.Run(async () =>
        {
            while (await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } l)
                onStdout?.Invoke(l);
        }, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Process.Kill is SIGKILL. Acceptable ONLY because --check/--prepare never start a graphics
            // process (the GPU rule forbids hard-killing those); a download cut here resumes next time
            // (curl -C -, wget -c, the Python fallback sends a Range header).
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }
        await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        lock (stderr) return (p.ExitCode, stderr.ToList());
    }
}

/// <summary>
/// Linux: before the 1.12.1 client starts, let its <c>START.sh --prepare</c> download and verify the
/// pinned runtime, with the step text in the launcher instead of a silent minute-long wait. Clients
/// without a <c>START.sh</c> (older packages, a player's own install) need nothing here and start
/// exactly as before.
/// </summary>
public sealed class LinuxStartScriptProvisioner : IGameRuntimeProvisioner
{
    private readonly Serilog.ILogger _log;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string, Action<string>?, CancellationToken, Task<(int, IReadOnlyList<string>)>> _run;

    public LinuxStartScriptProvisioner(Serilog.ILogger log) : this(log, null, null) { }

    /// <summary>Test seam: file probe and script runner injectable, so the decision and the message
    /// handling are provable without bash, a network or 1.2 GB.</summary>
    internal LinuxStartScriptProvisioner(
        Serilog.ILogger log, Func<string, bool>? fileExists,
        Func<string, string, Action<string>?, CancellationToken, Task<(int, IReadOnlyList<string>)>>? run)
    {
        _log = log;
        _fileExists = fileExists ?? File.Exists;
        _run = run ?? (async (script, arg, onOut, ct) =>
            await LinuxStartScript.RunAsync(script, arg, onOut, timeout: null, ct).ConfigureAwait(false));
    }

    /// <summary>Name-only question: without the install path there is no script to find.</summary>
    public bool NeedsRuntime(string clientExeName) => false;

    public Task<RuntimeReadiness> EnsureAsync(
        IProgress<DownloadProgress>? progress = null, IProgress<string>? step = null, CancellationToken ct = default)
        => Task.FromResult(RuntimeReadiness.Ready);

    public bool NeedsRuntimeForExe(string exePath) =>
        !ClientVersion.ExeNameNeedsModernRuntime(Path.GetFileName(exePath))
        && LinuxStartScript.Find(Path.GetDirectoryName(exePath), _fileExists) is not null;

    public async Task<RuntimeReadiness> EnsureForExeAsync(
        string exePath, IProgress<DownloadProgress>? progress = null, IProgress<string>? step = null,
        CancellationToken ct = default)
    {
        var script = LinuxStartScript.Find(Path.GetDirectoryName(exePath), _fileExists);
        if (script is null) return RuntimeReadiness.Ready;

        _log.Information("Preparing the Linux runtime through {Script} --prepare", script);
        try
        {
            var (exit, stderr) = await _run(script, "--prepare", line =>
            {
                if (LinuxStartScript.ParseProgress(line) is { } pr)
                {
                    step?.Report(pr.Text);
                    _log.Information("START.sh {Step} {Pct}% {Text}", pr.Step, pr.Percent, pr.Text);
                }
            }, ct).ConfigureAwait(false);

            if (exit == 0) return RuntimeReadiness.Ready;
            var message = LinuxStartScript.PlayerMessage(stderr);
            _log.Error("START.sh --prepare exited {Exit}: {Message}", exit, message);
            return RuntimeReadiness.Failed(message);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Error(ex, "START.sh --prepare could not run");
            return RuntimeReadiness.Failed($"The Linux start script could not run: {ex.Message}");
        }
    }
}
