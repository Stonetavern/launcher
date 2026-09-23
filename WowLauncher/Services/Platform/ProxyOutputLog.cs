namespace WowLauncher.Services.Platform;

using System.Diagnostics;
using System.Globalization;
using System.Text;

/// <summary>
/// Writes the realm proxy's stdout/stderr to a file beside the launcher logs.
///
/// <para><b>Why this exists.</b> Both proxy runners used to drain the child's streams into nothing
/// (<c>_ = process.StandardOutput.ReadToEndAsync()</c>) — necessary so a chatty child never blocks on a
/// full pipe, but it threw away the only place the proxy ever explains itself. On 2026-08-24 a macOS
/// player reached the realm list and was bounced back to the login screen; the launcher log showed a
/// flawless run (<c>Proxy ready: PID=…, port 1119</c>) because everything the proxy had to say about the
/// failing session went to a discarded stream. A failure that leaves no trace cannot be diagnosed by
/// anyone who is not sitting in front of the machine.</para>
///
/// <para><b>Contract.</b> Reading never blocks the caller and never throws into it: a write failure
/// degrades to "keep draining, stop writing" rather than killing the launch. The previous run is kept as
/// <c>&lt;name&gt;.1</c>, because the interesting failure is usually the one BEFORE the player restarted
/// the launcher to try again. Output is capped (<see cref="DefaultMaxBytes"/>) so a proxy stuck in a log
/// loop cannot fill the disk — the cap is announced in the file itself, so a truncated log is never
/// mistaken for a quiet one.</para>
/// </summary>
public sealed class ProxyOutputLog : IDisposable
{
    /// <summary>8 MiB — comfortably more than a full session of proxy chatter, far less than a runaway loop.</summary>
    public const long DefaultMaxBytes = 8 * 1024 * 1024;

    private readonly TextWriter _writer;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private long _written;
    private bool _capped;
    private bool _disposed;

    private ProxyOutputLog(TextWriter writer, long maxBytes)
    {
        _writer = writer;
        _maxBytes = maxBytes;
    }

    /// <summary>
    /// The one call both proxy runners make after starting their child: record its output when a path is
    /// configured, and otherwise fall back to the plain drain that keeps a chatty child from blocking on a
    /// full pipe. Draining is not optional — recording is. Returns the log when one was opened.
    /// </summary>
    public static ProxyOutputLog? AttachOrDrain(Process process, string? path, Serilog.ILogger logger,
        long maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!string.IsNullOrEmpty(path))
        {
            var log = Attach(process, path, logger, maxBytes);
            if (log is not null) return log;
        }

        // No path configured, or the file could not be opened: drain both streams into nothing, exactly as
        // before. A full pipe would stall the proxy mid-session, which is far worse than a missing log.
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return null;
    }

    /// <summary>
    /// Start pumping <paramref name="process"/>' redirected stdout and stderr into
    /// <paramref name="path"/>. Returns null when the file could not be opened — the caller then falls
    /// back to plain draining, so a read-only log directory costs the player a diagnostic, never a launch.
    /// The caller MUST have started the process with both streams redirected.
    /// </summary>
    public static ProxyOutputLog? Attach(Process process, string path, Serilog.ILogger logger,
        long maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(process);

        var log = Open(path, logger, maxBytes);
        if (log is null) return null;

        log.WriteLine("launcher", FormattableString.Invariant(
            $"proxy output log opened {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}, PID={process.Id}"));

        // Fire-and-forget by design: the pumps must outlive this call and must never make the caller wait.
        // PumpAsync itself is total (it swallows its own I/O faults), so there is no unobserved exception.
        _ = log.PumpAsync(process.StandardOutput, "out");
        _ = log.PumpAsync(process.StandardError, "err");
        return log;
    }

    /// <summary>Open (and rotate) the log file, without touching any process. Split out from
    /// <see cref="Attach"/> so the file, rotation and capping behaviour is provable on its own — a test
    /// for "does a full log say it is full" should not need a child process to exist.</summary>
    internal static ProxyOutputLog? Open(string path, Serilog.ILogger logger, long maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(logger);

        TextWriter writer;
        try
        {
            RotatePrevious(path);
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            { AutoFlush = true }; // a crash must not cost the last lines — that is exactly when they matter
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "Could not open the proxy output log {Path} — draining without recording", path);
            return null;
        }

        return new ProxyOutputLog(writer, maxBytes);
    }

    /// <summary>Copy one stream line by line into the log until it ends. Total by contract: any read or
    /// write fault ends the pump quietly instead of surfacing as an unobserved task exception. Internal so
    /// the pumping/tagging/capping logic is provable against a <see cref="StringReader"/>, with no child
    /// process and no proxy binary.</summary>
    internal async Task PumpAsync(TextReader reader, string tag)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                WriteLine(tag, line);
        }
        catch
        {
            // Stream closed, process gone, disk gone — draining is over, and none of it is worth
            // failing a launch for.
        }
    }

    /// <summary>Append one tagged line, honouring the size cap. Announces the cap once, in the file, so a
    /// truncated log cannot be misread as a proxy that fell silent.</summary>
    internal void WriteLine(string tag, string line)
    {
        lock (_gate)
        {
            if (_disposed || _capped) return;

            var text = string.Concat("[", tag, "] ", line);
            if (_written + text.Length > _maxBytes)
            {
                _capped = true;
                TryWrite(FormattableString.Invariant(
                    $"[launcher] output cap of {_maxBytes} bytes reached — the proxy is still running, this log is not."));
                return;
            }

            _written += text.Length;
            TryWrite(text);
        }
    }

    /// <summary>Whether the size cap has been hit (the log is truncated, the proxy is not).</summary>
    internal bool IsCapped { get { lock (_gate) return _capped; } }

    private void TryWrite(string text)
    {
        try { _writer.WriteLine(text); }
        catch { _disposed = true; } // disk full/removed: stop writing, keep draining
    }

    /// <summary>Keep the previous run as <c>&lt;name&gt;.1</c>. Best effort: losing the older log is never
    /// a reason to fail the new one.</summary>
    private static void RotatePrevious(string path)
    {
        try
        {
            if (File.Exists(path)) File.Move(path, path + ".1", overwrite: true);
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _writer.Dispose(); } catch { /* already gone */ }
        }
    }

    /// <summary>The conventional file name for a runner's proxy output, beside the launcher logs.</summary>
    public static string FileNameFor(string runnerName) =>
        string.Create(CultureInfo.InvariantCulture, $"proxy-{runnerName}.log");
}
