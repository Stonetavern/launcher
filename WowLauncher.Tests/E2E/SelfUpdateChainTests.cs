using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests.E2E;

/// <summary>
/// <b>Ebene B.1</b> of <c>(internal design notes, not published)</c>: the
/// self-update cycle as ONE chain, not as parts.
///
/// <para>Every link here already has unit tests — the signature
/// (<c>ManifestSignatureTests</c>), the release policy (<c>ManifestReleasePolicyTests</c>), the swap
/// script (<c>LinuxUpdateSwapTests</c>), the health contract (<c>UpdateHealthTests</c>). What none of
/// them can show is the chain: manifest over real HTTP → signature → policy → download → SHA-256 →
/// staged helper → the running file actually replaced → the NEW build comes up and says so. The last
/// two steps happen after the launcher process is gone, which is precisely why no log of the launcher
/// ever contained them.</para>
///
/// <para><b>Two deliberate substitutions, both named rather than hidden</b> (PLAN §9 asks for this):
/// <list type="number">
/// <item>The launcher binary is a shell script. A .NET single-file build cannot be produced inside a
/// test in reasonable time, and the property under test is the swap and the health handshake, not
/// what the new process computes. The surrogate does the two things the contract requires of a new
/// build: it clears the sentinel and it records which version came up.</item>
/// <item>The helper script's <c>PID</c> is rewritten to a process that has already exited.
/// <c>ApplySwap</c> bakes in <see cref="Environment.ProcessId"/>, which inside a test is the test host
/// — and that never exits, so the real script would (correctly) refuse to swap under it. Exactly one
/// number is changed; every rename, the health watch and the self-deletion run as generated.</item>
/// </list></para>
/// </summary>
public sealed class SelfUpdateChainTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "stonetavern-b1-" + Guid.NewGuid().ToString("N"));

    /// <summary>The launcher's own log, captured into the test directory. Without it a refusal in the
    /// middle of a six-link chain is a boolean and nothing else — and the link that said no is the one
    /// piece of information the test exists to produce.</summary>
    private readonly List<string> _lines = [];

    private Serilog.ILogger Log => new Serilog.LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.Sink(new ListSink(_lines))
        .CreateLogger();

    private readonly string? _appImageBefore = Environment.GetEnvironmentVariable("APPIMAGE");

    public SelfUpdateChainTests() => Directory.CreateDirectory(_root);

    /// <summary>
    /// Makes the launcher treat <paramref name="path"/> as the file it runs from.
    ///
    /// <para>🔴 This is not a test switch — it is the Linux production path. <c>UpdateService</c>
    /// derives BOTH the download destination and the swap target from the running process
    /// (<c>CurrentBinaryPath</c>), and under a test host that process is <c>/usr/lib64/dotnet/dotnet</c>:
    /// the first run of this test spent six retries and 67 seconds failing to write
    /// <c>/usr/lib64/dotnet/dotnet.download</c>, and reported it as a network fault. Under an AppImage
    /// the same resolution is equally wrong for a different reason — the process image lives on a
    /// read-only squashfs mount — which is why the shipped code consults <c>$APPIMAGE</c> FIRST. So the
    /// test sets exactly what a real Linux launcher has set for it, and every later decision is made by
    /// production code with no knowledge that a test is running.</para>
    ///
    /// <para>Process-wide state, restored in <see cref="Dispose"/>. Safe because this suite runs
    /// serialised on purpose (see <c>AssemblyInfo.cs</c>).</para>
    /// </summary>
    private static void RunAs(string path) => Environment.SetEnvironmentVariable("APPIMAGE", path);

    private sealed class ListSink(List<string> lines) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent)
        {
            lock (lines) lines.Add($"[{logEvent.Level}] {logEvent.RenderMessage()}" +
                (logEvent.Exception is null ? "" : " !! " + logEvent.Exception.GetType().Name + ": " + logEvent.Exception.Message));
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("APPIMAGE", _appImageBefore);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task TheWholeCycle_RunsFromManifestToARestartedNewBuild()
    {
        using var origin = TestOrigin.Start();
        using var release = new TestRelease();

        var installed = new Version(1, 6, 4);
        var offered = new Version(1, 7, 0);

        var currentExe = Path.Combine(_root, "stonetavern-launcher");
        WriteSurrogateLauncher(currentExe, installed);
        RunAs(currentExe);

        var newBuild = SurrogateLauncherText(offered);
        var newBytes = Encoding.UTF8.GetBytes(newBuild);
        origin.Publish("/launcher/stonetavern-launcher-1.7.0", newBytes);

        var manifest = TestRelease.AdmissibleManifest();
        manifest.LauncherLinux = new ManifestFile
        {
            Version = offered.ToString(),
            Url = origin.BaseUrl + "/launcher/stonetavern-launcher-1.7.0",
            Sha256 = Convert.ToHexString(SHA256.HashData(newBytes)).ToLowerInvariant(),
        };
        release.Publish(origin, manifest);

        var helperRan = new TaskCompletionSource<int>();
        var service = NewUpdateService(origin, release, installed, currentExe, helperRan);

        var applied = await service.CheckAndApplyAsync(manifest);

        Assert.True(applied,
            "the chain refused the update. What the origin was asked: " + Describe(origin) +
            "\nWhat the launcher logged:\n" + LauncherLog());

        // Step 1: what the ORIGIN saw, in order. A return value can be true while the wrong route was
        // taken; the request log cannot (PLAN §3).
        var paths = origin.Requests.Select(r => r.Path).ToList();
        Assert.Equal(
            [TestRelease.ManifestPath, TestRelease.SignaturePath, "/launcher/stonetavern-launcher-1.7.0"],
            paths);
        Assert.All(origin.Requests, r => Assert.Equal(200, r.Status));

        // Step 2: the helper actually did its work — this is the part that happens after the launcher
        // process is gone and therefore appears in no launcher log.
        Assert.Equal(0, await helperRan.Task.WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.True(File.Exists(currentExe), "the launcher path is empty after the swap");
        Assert.Contains($"VERSION={offered}", File.ReadAllText(currentExe), StringComparison.Ordinal);

        var previous = currentExe + LinuxUpdateSwapStrategy.PreviousSuffix;
        Assert.True(File.Exists(previous), "no way back — the previous build was not kept");
        Assert.Contains($"VERSION={installed}", File.ReadAllText(previous), StringComparison.Ordinal);

        // Step 3: the RESTARTED process is the new build. "Launcher reports the old version after the
        // swap" is the shape the 2026-08-01 update loop had, and it is invisible in every other test.
        var started = Path.Combine(_root, "which-build-came-up.txt");
        Assert.True(File.Exists(started), "the swap script never started the new build");
        Assert.Equal(offered.ToString(), File.ReadAllText(started).Trim());

        // Step 4: no rollback happened. 🔴 What this does NOT prove is the health WATCH — the surrogate
        // clears the sentinel either way, so removing the watch from the swap script leaves these three
        // assertions green. The rollback branch is proven in LinuxUpdateHealthScriptTests, which drives
        // the generated script with a short health budget; repeating it here would cost the ninety
        // seconds ApplySwap hard-codes. Named rather than implied, so nobody reads more into a green
        // run than it carries (PLAN §7).
        Assert.False(File.Exists(Path.Combine(_root, UpdateHealth.SentinelName)),
            "the sentinel still stands, so the new build never reported in");
        Assert.False(File.Exists(Path.Combine(_root, UpdateHealth.QuarantineName)),
            "a quarantine note means this update was rolled back");
        Assert.False(File.Exists(currentExe + ".broken"));

        // Step 5: no debris. A leftover .download is a half-finished update the next start would trip on.
        Assert.False(File.Exists(currentExe + ".download"));
        Assert.True(File.Exists(Path.Combine(_root, LinuxUpdateSwapStrategy.SwapLogName)),
            "the swap left no account of itself");
    }

    /// <summary>
    /// The positive control the four negative cases of B.2 need, and the reason it lives here: four
    /// refusals prove nothing if this environment never swaps anything at all. The test above IS that
    /// control — this one only states the dependency so a future edit cannot quietly drop it.
    /// </summary>
    [Fact]
    public async Task AnUnsignedManifest_StopsTheChainBeforeAnythingIsDownloaded()
    {
        using var origin = TestOrigin.Start();
        using var release = new TestRelease();

        var installed = new Version(1, 6, 4);
        var currentExe = Path.Combine(_root, "stonetavern-launcher");
        WriteSurrogateLauncher(currentExe, installed);
        RunAs(currentExe);

        var newBytes = Encoding.UTF8.GetBytes(SurrogateLauncherText(new Version(1, 7, 0)));
        origin.Publish("/launcher/stonetavern-launcher-1.7.0", newBytes);

        var manifest = TestRelease.AdmissibleManifest();
        manifest.LauncherLinux = new ManifestFile
        {
            Version = "1.7.0",
            Url = origin.BaseUrl + "/launcher/stonetavern-launcher-1.7.0",
            Sha256 = Convert.ToHexString(SHA256.HashData(newBytes)).ToLowerInvariant(),
        };
        release.PublishWithoutSignature(origin, manifest);

        var service = NewUpdateService(origin, release, installed, currentExe, new TaskCompletionSource<int>());

        Assert.False(await service.CheckAndApplyAsync(manifest));

        // The measurement that matters: the binary was never even asked for. A launcher that downloads
        // first and checks afterwards has already spent the player's bandwidth on an unauthenticated
        // artefact.
        Assert.DoesNotContain(origin.Requests, r => r.Path.StartsWith("/launcher/", StringComparison.Ordinal));
        Assert.Contains(origin.Requests, r => r.Path == TestRelease.SignaturePath && r.Status == 404);
        Assert.Contains($"VERSION={installed}", File.ReadAllText(currentExe), StringComparison.Ordinal);
        Assert.False(File.Exists(currentExe + LinuxUpdateSwapStrategy.PreviousSuffix));
    }

    private string LauncherLog()
    {
        lock (_lines) return string.Join('\n', _lines);
    }

    /// <summary>Everything the origin saw, as one line — so a refusal says WHICH link stopped, instead
    /// of leaving the reader to guess between six of them.</summary>
    private static string Describe(TestOrigin origin) =>
        origin.Requests.Count == 0
            ? "(nothing at all — the launcher never reached the server)"
            : string.Join(" | ", origin.Requests.Select(r =>
                $"{r.Method} {r.Path} -> {r.Status} ({r.BytesWritten} B{(r.Range is null ? "" : ", Range " + r.Range)})"));

    // ─── wiring ───────────────────────────────────────────────────────────

    /// <summary>The shipped services, wired to the test origin. Nothing here is a mock of the
    /// launcher's own logic — only the origin and the surrogate binary are ours.</summary>
    private UpdateService NewUpdateService(TestOrigin origin, TestRelease release, Version installed,
        string currentExe, TaskCompletionSource<int> helperRan)
    {
        var health = new UpdateHealth(
            Path.Combine(_root, UpdateHealth.SentinelName),
            Path.Combine(_root, UpdateHealth.QuarantineName),
            Log, installed);

        var swap = new LinuxUpdateSwapStrategy(Log, spawn: (shell, args) =>
        {
            _ = RunHelperAsync(shell, args[0], helperRan);
            return true;
        });

        return new UpdateService(
            new DownloadService(origin.HttpClientForPlain(), Log),
            Log, swap, release.Gate(origin, Log), LauncherUpdateChannel.Linux,
            currentVersion: installed, attempts: null, health: health);
    }

    /// <summary>
    /// Runs the REAL generated helper, with the one value a test process cannot supply honestly
    /// replaced: the pid to wait for. See the class comment.
    /// </summary>
    private static async Task RunHelperAsync(string shell, string script, TaskCompletionSource<int> done)
    {
        try
        {
            var text = await File.ReadAllTextAsync(script);
            var deadPid = AlreadyExitedPid();
            var rewritten = text.Replace($"PID={Environment.ProcessId}", $"PID={deadPid}",
                StringComparison.Ordinal);
            if (rewritten == text)
            {
                done.TrySetException(new InvalidOperationException(
                    $"the helper does not contain 'PID={Environment.ProcessId}' — the swap script " +
                    "changed shape and this substitution is silently doing nothing"));
                return;
            }
            await File.WriteAllTextAsync(script, rewritten);

            using var process = Process.Start(new ProcessStartInfo(shell) { ArgumentList = { script } })!;
            await process.WaitForExitAsync();
            done.TrySetResult(process.ExitCode);
        }
        catch (Exception ex)
        {
            done.TrySetException(ex);
        }
    }

    /// <summary>A pid that is definitely gone: our own child, reaped. The alternative — inventing a
    /// number — could name a live process and would make the helper wait out its whole budget.</summary>
    private static int AlreadyExitedPid()
    {
        using var corpse = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "exit 0" } })!;
        corpse.WaitForExit();
        return corpse.Id;
    }

    private void WriteSurrogateLauncher(string path, Version version)
    {
        File.WriteAllText(path, SurrogateLauncherText(version));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// What a launcher build has to do for the update contract to be satisfiable: say which version it
    /// is, record that it came up, and clear the health sentinel once it is really running. A real
    /// build does the last one in <c>UpdateHealth.ReportHealthy</c> after the window exists.
    /// </summary>
    private static string SurrogateLauncherText(Version version) =>
        string.Join('\n',
            "#!/bin/sh",
            $"VERSION={version}",
            "DIR=$(dirname \"$0\")",
            "printf '%s\\n' \"$VERSION\" > \"$DIR/which-build-came-up.txt\"",
            $"rm -f \"$DIR/{UpdateHealth.SentinelName}\"",
            "sleep 2",
            "") + "\n";
}
