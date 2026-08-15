using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The Linux self-update swap. It is done by a generated shell helper rather than in-process, and the
/// reason is a real failure from an end-to-end run on 2026-07-27: a single-file .NET application loads
/// parts of ITSELF lazily from its own path, so the moment that path pointed at the new build, the
/// running process could no longer load the assembly <c>Process.Start</c> needs. The update had in fact
/// been applied and the launcher reported failure — a log that lies.
///
/// <para>So these tests RUN the generated script against real files. Asserting its text would only
/// prove we wrote what we meant to write, not that the renames end where a player needs them.</para>
/// </summary>
public sealed class LinuxUpdateSwapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "swap-" + Guid.NewGuid().ToString("N"));

    public LinuxUpdateSwapTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static Serilog.ILogger Logger() => new Serilog.LoggerConfiguration().CreateLogger();

    private string WriteFile(string name, string marker)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n# " + marker + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    /// <summary>Run the generated helper the way the system would, but for a pid that is already gone,
    /// so the wait loop falls through immediately and the test does not depend on timing.</summary>
    private static int RunScript(string script)
    {
        using var sh = Process.Start(new ProcessStartInfo("/bin/sh", script) { UseShellExecute = false })!;
        sh.WaitForExit(10_000);
        return sh.ExitCode;
    }

    private string GenerateScript(string newExe, string current, int pid = 999_999, int waitTicks = 100)
    {
        var path = Path.Combine(_root, "update.sh");
        File.WriteAllText(path, LinuxUpdateSwapStrategy.BuildScript(newExe, current, _root, pid, waitTicks));
        return path;
    }

    [Fact]
    public void TheHelper_PutsTheNewBuildInPlace_AndKeepsTheOldOneBeside()
    {
        var current = WriteFile("stonetavern-launcher.AppImage", "OLD");
        var fresh = WriteFile("downloaded.new", "NEW");

        Assert.Equal(0, RunScript(GenerateScript(fresh, current)));

        Assert.Contains("NEW", File.ReadAllText(current), StringComparison.Ordinal);
        var previous = current + LinuxUpdateSwapStrategy.PreviousSuffix;
        Assert.True(File.Exists(previous));                                        // a way back exists…
        Assert.Contains("OLD", File.ReadAllText(previous), StringComparison.Ordinal);
        Assert.False(File.Exists(fresh));                                          // …and nothing staged is left
        Assert.True(File.GetUnixFileMode(current).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void TheHelper_DoesNothingWhileTheLauncherIsStillRunning()
    {
        // The whole point of the helper: it must not touch the file of a live process. Given a pid that
        // IS alive (our own), it waits and then gives up rather than swapping underneath it.
        var current = WriteFile("stonetavern-launcher.AppImage", "OLD");
        var fresh = WriteFile("downloaded.new", "NEW");

        // A short wait budget: the behaviour under test is "gives up rather than swapping under a live
        // process", not how long it is willing to wait.
        var exit = RunScript(GenerateScript(fresh, current, Environment.ProcessId, waitTicks: 3));

        Assert.NotEqual(0, exit);
        Assert.Contains("OLD", File.ReadAllText(current), StringComparison.Ordinal);
    }

    [Fact]
    public void TheHelper_PutsTheOldBuildBack_WhenTheNewOneCannotLand()
    {
        // The dangerous window: the old launcher is already renamed away and the new one has not
        // arrived. Provoked by deleting the staged file before the second rename can use it.
        var current = WriteFile("stonetavern-launcher.AppImage", "OLD");
        var missing = Path.Combine(_root, "never-downloaded");

        var exit = RunScript(GenerateScript(missing, current));

        Assert.NotEqual(0, exit);
        Assert.True(File.Exists(current));
        Assert.Contains("OLD", File.ReadAllText(current), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderBackup_IsReplaced_NotStacked()
    {
        var current = WriteFile("stonetavern-launcher.AppImage", "OLD");
        File.WriteAllText(current + LinuxUpdateSwapStrategy.PreviousSuffix, "ANCIENT");
        var fresh = WriteFile("downloaded.new", "NEW");

        Assert.Equal(0, RunScript(GenerateScript(fresh, current)));

        // One generation, not an archive that grows with every update.
        Assert.Contains("OLD", File.ReadAllText(current + LinuxUpdateSwapStrategy.PreviousSuffix),
            StringComparison.Ordinal);
    }

    [Fact]
    public void APathWithAQuoteInIt_IsNotAWayToRunSomethingElse()
    {
        // The paths are pasted into a shell script. A player's folder can contain anything.
        var dir = Path.Combine(_root, "it's mine");
        Directory.CreateDirectory(dir);
        var current = Path.Combine(dir, "stonetavern-launcher.AppImage");
        File.WriteAllText(current, "# OLD\n");
        var fresh = Path.Combine(dir, "downloaded.new");
        File.WriteAllText(fresh, "# NEW\n");
        var marker = Path.Combine(_root, "pwned");

        var script = Path.Combine(_root, "update.sh");
        File.WriteAllText(script, LinuxUpdateSwapStrategy.BuildScript(
            fresh, current + "'; touch '" + marker + "'; :'", dir, 999_999));
        RunScript(script);

        Assert.False(File.Exists(marker));   // the quote stayed inside the string it belongs to
    }

    [Fact]
    public void AMissingDownload_Throws_RatherThanPretendingToSwap()
    {
        // Staging failures are hard errors by contract (same as Windows).
        var current = WriteFile("stonetavern-launcher.AppImage", "OLD");

        Assert.Throws<FileNotFoundException>(() =>
            new LinuxUpdateSwapStrategy(Logger()).ApplySwap(Path.Combine(_root, "not-there"), current, _root));
    }

    [Fact]
    public void TheStrategy_ReportsFailure_WhenTheHelperCannotBeStarted()
    {
        // Then the caller starts normally instead of exiting into nothing.
        var current = WriteFile("stonetavern-launcher.AppImage", "OLD");
        var fresh = WriteFile("downloaded.new", "NEW");
        var strategy = new LinuxUpdateSwapStrategy(Logger(), spawn: (_, _) => false);

        Assert.False(strategy.ApplySwap(fresh, current, _root));
        Assert.Contains("OLD", File.ReadAllText(current), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStrategy_NeverTouchesTheLauncherItself()
    {
        // The invariant the whole redesign rests on: while the launcher is alive, its own file is not
        // modified by the launcher. Only a helper is written, and only elsewhere.
        var current = WriteFile("stonetavern-launcher.AppImage", "OLD");
        var fresh = WriteFile("downloaded.new", "NEW");
        var spawned = 0;
        var strategy = new LinuxUpdateSwapStrategy(Logger(), spawn: (_, _) => { spawned++; return true; });

        Assert.True(strategy.ApplySwap(fresh, current, _root));

        Assert.Equal(1, spawned);
        Assert.Contains("OLD", File.ReadAllText(current), StringComparison.Ordinal);  // untouched…
        Assert.True(File.Exists(fresh));                                             // …and so is the download
    }

    [Fact]
    public void TheStrategy_StagesTheHelperInAPrivateRandomDirectory()
    {
        var current = WriteFile("stonetavern-launcher.AppImage", "OLD");
        var fresh = WriteFile("downloaded.new", "NEW");
        string? script = null;
        var strategy = new LinuxUpdateSwapStrategy(Logger(), spawn: (_, args) =>
        {
            script = args[0];
            return true;
        });

        Assert.True(strategy.ApplySwap(fresh, current, _root));
        Assert.NotNull(script);
        Assert.Equal("update.sh", Path.GetFileName(script));

        var helperDir = Path.GetDirectoryName(script)!;
        Assert.StartsWith(Path.GetTempPath(), helperDir, StringComparison.Ordinal);
        var mode = File.GetUnixFileMode(helperDir);
        const UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                                   UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                                   UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        Assert.Equal(UnixFileMode.None, mode & others);
        Assert.Contains("Stonetavern launcher self-update", File.ReadAllText(script!), StringComparison.Ordinal);

        Directory.Delete(helperDir, recursive: true);
    }
}
