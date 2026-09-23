using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The proxy's own words, recorded instead of discarded.
///
/// <para>Until 2026-08-24 both runners drained the child's streams into nothing. The launcher log then
/// described a perfect run while the proxy was failing a login — the failure left no trace anywhere.
/// These tests prove the recording end to end: the pumping/tagging/capping logic against a plain
/// <see cref="StringReader"/>, and the wiring against a REAL child process that really talks, so a
/// regression to "drain and discard" cannot pass.</para>
/// </summary>
public sealed class ProxyOutputLogTests
{
    private static Serilog.ILogger Log() => new Serilog.LoggerConfiguration().CreateLogger();

    private static string TempPath(string kind) =>
        Path.Combine(Path.GetTempPath(), $"st-proxy-{kind}-{Guid.NewGuid():N}.log");

    private static string TempPidPath() =>
        Path.Combine(Path.GetTempPath(), $"st-proxy-test-{Guid.NewGuid():N}.pid");

    private static Func<int, System.Threading.CancellationToken, Task<bool>> OpensAfter(int polls)
    {
        var seen = 0;
        return (_, _) => Task.FromResult(++seen > polls);
    }

    // ---- the logic, provable without a process -------------------------------------------------

    [Fact]
    public async Task PumpAsync_WritesEveryLineWithItsStreamTag()
    {
        var path = TempPath("pump");
        try
        {
            var log = ProxyOutputLog.Open(path, Log());
            Assert.NotNull(log);
            await log!.PumpAsync(new StringReader("Starting Hermes Proxy...\nConfig loading failed\n"), "out");
            log.Dispose();

            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("[out] Starting Hermes Proxy...", text, StringComparison.Ordinal);
            Assert.Contains("[out] Config loading failed", text, StringComparison.Ordinal);
        }
        finally { File.Delete(path); File.Delete(path + ".1"); }
    }

    [Fact]
    public async Task WriteLine_StopsAtTheCapAndSaysSoInTheFile()
    {
        // A truncated log that goes quiet without explanation is exactly the kind of "silent green" this
        // whole file exists to prevent: the reader would take the last line for the proxy's last word.
        var path = TempPath("cap");
        try
        {
            var log = ProxyOutputLog.Open(path, Log(), maxBytes: 64);
            Assert.NotNull(log);
            for (var i = 0; i < 50; i++) log!.WriteLine("out", new string('x', 40));
            Assert.True(log!.IsCapped);
            log.Dispose();

            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("output cap of 64 bytes reached", text, StringComparison.Ordinal);
            Assert.True(text.Length < 1000, $"capped log grew to {text.Length} bytes");
        }
        finally { File.Delete(path); File.Delete(path + ".1"); }
    }

    [Fact]
    public async Task Attach_KeepsThePreviousRunAsDotOne()
    {
        // The interesting failure is usually the run BEFORE the player restarted the launcher to retry.
        var path = TempPath("rotate");
        try
        {
            await File.WriteAllTextAsync(path, "[out] the run that actually failed\n");
            ProxyOutputLog.Open(path, Log())?.Dispose();

            Assert.True(File.Exists(path + ".1"), "the previous run was not kept");
            Assert.Contains("the run that actually failed", await File.ReadAllTextAsync(path + ".1"),
                StringComparison.Ordinal);
        }
        finally { File.Delete(path); File.Delete(path + ".1"); }
    }

    [Fact]
    public void AttachOrDrain_WithoutAPath_DrainsAndRecordsNothing()
    {
        if (!OperatingSystem.IsLinux()) return;

        // No path configured must still drain, or a chatty proxy stalls on a full pipe mid-session.
        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("echo hello; echo boom >&2");
        using var process = Process.Start(psi)!;

        Assert.Null(ProxyOutputLog.AttachOrDrain(process, null, Log()));
        Assert.True(process.WaitForExit(5000), "the child never exited — its output was not drained");
    }

    // ---- the wiring: a real child, really recorded ----------------------------------------------

    [Fact]
    public async Task HermesProxyRunner_RecordsWhatTheProxyActuallyPrints()
    {
        if (!OperatingSystem.IsLinux()) return;

        // THE regression test for 2026-08-24. A child that prints on both streams, exactly as JimsProxy
        // does while a login fails. Without the recording (the old `_ = ReadToEndAsync()`) the file below
        // does not exist at all, so this test cannot pass by accident.
        var path = TempPath("runner");
        var script = Path.Combine(Path.GetTempPath(), $"st-proxy-talker-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(script,
            "#!/bin/sh\necho 'Starting Hermes Proxy...'\necho 'AUTH_LOGON_CHALLENGE failed' >&2\nsleep 5\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var runner = new HermesProxyRunner(Log(), script, [], TempPidPath(), OpensAfter(1),
            outputLogPath: path);
        try
        {
            var result = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(5));
            Assert.True(result.Ready);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            string text;
            do
            {
                await Task.Delay(50);
                text = File.Exists(path) ? await ReadSharedAsync(path) : "";
            }
            while (!text.Contains("AUTH_LOGON_CHALLENGE", StringComparison.Ordinal) && DateTime.UtcNow < deadline);

            Assert.Contains("[out] Starting Hermes Proxy...", text, StringComparison.Ordinal);
            Assert.Contains("[err] AUTH_LOGON_CHALLENGE failed", text, StringComparison.Ordinal);
        }
        finally
        {
            await runner.StopAsync();
            File.Delete(script);
            File.Delete(path);
            File.Delete(path + ".1");
        }
    }

    /// <summary>Read a file the log is still writing to (the writer holds it with
    /// <see cref="FileShare.Read"/>, so a plain ReadAllText would fight it on some filesystems).</summary>
    private static async Task<string> ReadSharedAsync(string path)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return await reader.ReadToEndAsync();
        }
        catch (IOException) { return ""; }
    }
}
