namespace WowLauncher.Tests;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WowLauncher.Services.Platform;
using Xunit;

/// <summary>
/// The macOS self-update. Until 2026-08-02 there was none: the platform was wired to
/// <c>UnsupportedUpdateSwapStrategy</c>, so a Mac player stayed on whatever build they installed while
/// the realm moved on.
///
/// <para><b>What these tests can and cannot prove.</b> The helper is POSIX sh and the moves are
/// renames, so running it on Linux exercises the real script against real directories — that is the
/// half that used to be wrong on the other two platforms (a script whose TEXT looked right and whose
/// EFFECT was nothing). What Linux cannot answer is whether <c>open</c> brings the bundle back with
/// its Dock icon and how Gatekeeper treats a bundle replaced underneath it. Those are named here as
/// open, not quietly assumed: one run on a real Mac is still owed.</para>
/// </summary>
public sealed class MacUpdateSwapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mac-swap-" + Guid.NewGuid().ToString("N"));

    public MacUpdateSwapTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static Serilog.ILogger Log() => new Serilog.LoggerConfiguration().CreateLogger();

    /// <summary>A minimal but real .app: the layout Apple fixes and the strategy relies on.</summary>
    private string MakeBundle(string name, string marker, bool executable = true)
    {
        var bundle = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
        var exe = Path.Combine(bundle, "Contents", "MacOS", "WowLauncher");
        File.WriteAllText(exe, marker);
        if (executable && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"),
            "<plist><dict><key>CFBundleExecutable</key><string>WowLauncher</string></dict></plist>");
        return bundle;
    }

    // ── Finding the bundle ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheBundleIsFound_FromTheExecutableInsideIt()
    {
        var bundle = MakeBundle("Stonetavern.app", "v1");

        var found = MacUpdateSwapStrategy.BundleRootOf(
            Path.Combine(bundle, "Contents", "MacOS", "WowLauncher"));

        Assert.Equal(bundle, found);
    }

    /// <summary>The check is the exact Apple layout, not "is there a .app somewhere in this path".
    /// A player whose game folder happens to be called something.app must never have it swapped.</summary>
    [Theory]
    [InlineData("Games/WoW.app/WowLauncher")]              // .app, but not the bundle layout
    [InlineData("Stonetavern.app/Contents/Helpers/x")]     // inside a bundle, wrong directory
    [InlineData("opt/stonetavern/WowLauncher")]            // a plain install, no bundle at all
    public void AnythingThatIsNotTheAppleLayout_IsNotTreatedAsABundle(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");

        Assert.Null(MacUpdateSwapStrategy.BundleRootOf(path));
    }

    [Fact]
    public void AnInstallThatIsNotABundle_FailsLoudlyInsteadOfSwappingSomethingElse()
    {
        var zip = Path.Combine(_root, "update.zip");
        File.WriteAllText(zip, "not really a zip, we never get that far");
        var loose = Path.Combine(_root, "loose", "WowLauncher");
        Directory.CreateDirectory(Path.GetDirectoryName(loose)!);
        File.WriteAllText(loose, "v1");

        var strategy = new MacUpdateSwapStrategy(Log(), (_, _) => true, (_, _) => { });

        // Staging failures throw, same contract as Windows and Linux.
        Assert.Throws<InvalidOperationException>(() => strategy.ApplySwap(zip, loose, _root));
    }

    [Fact]
    public void AnUpdateWithNoBundleInside_IsRefused()
    {
        var bundle = MakeBundle("Stonetavern.app", "v1");
        var zip = Path.Combine(_root, "update.zip");
        File.WriteAllText(zip, "x");

        // Unpacks "successfully" but produces no .app — a mis-built or mis-published artifact.
        var strategy = new MacUpdateSwapStrategy(Log(), (_, _) => true,
            (_, dest) => File.WriteAllText(Path.Combine(dest, "readme.txt"), "wrong artifact"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            strategy.ApplySwap(zip, Path.Combine(bundle, "Contents", "MacOS", "WowLauncher"), _root));
        Assert.Contains(".app", ex.Message, StringComparison.Ordinal);
    }

    // ── The helper, run for real ────────────────────────────────────────────────────────────────

    /// <summary>Run the generated script against real directories and assert what is on disk
    /// afterwards. Reading the script text would prove nothing: on Windows a script that reported
    /// success while doing nothing shipped twice.</summary>
    private static async Task<int> RunScript(string script)
    {
        var p = Process.Start(new ProcessStartInfo("/bin/sh", script) { UseShellExecute = false })!;
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    [Fact]
    public async Task TheNewBundleTakesTheOldOnesPlace_AndTheOldOneStaysBeside()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var current = MakeBundle("Stonetavern.app", "v1");
        var staging = Path.Combine(_root, ".staging");
        Directory.CreateDirectory(staging);
        var incoming = MakeBundle(Path.Combine(".staging", "Stonetavern.app"), "v2");

        var script = Path.Combine(_root, "swap.sh");
        // pid 1 never exits, so the wait has to be given a pid that is already gone: our own child.
        File.WriteAllText(script,
            MacUpdateSwapStrategy.BuildScript(incoming, current, staging, DeadPid(), "WowLauncher", waitTicks: 5));

        var exit = await RunScript(script);

        Assert.Equal(0, exit);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(current, "Contents", "MacOS", "WowLauncher")));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(
            current + MacUpdateSwapStrategy.PreviousSuffix, "Contents", "MacOS", "WowLauncher")));
        Assert.False(Directory.Exists(staging), "the staging directory was left behind");
    }

    [Fact]
    public async Task NothingIsTouched_WhileTheLauncherIsStillRunning()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var current = MakeBundle("Stonetavern.app", "v1");
        var staging = Path.Combine(_root, ".staging");
        Directory.CreateDirectory(staging);
        var incoming = MakeBundle(Path.Combine(".staging", "Stonetavern.app"), "v2");

        using var alive = Process.Start(new ProcessStartInfo("/usr/bin/sleep", "30") { UseShellExecute = false })!;
        var script = Path.Combine(_root, "swap.sh");
        File.WriteAllText(script,
            MacUpdateSwapStrategy.BuildScript(incoming, current, staging, alive.Id, "WowLauncher", waitTicks: 3));

        var exit = await RunScript(script);
        alive.Kill(entireProcessTree: true);

        Assert.NotEqual(0, exit);
        // The player's launcher is untouched: swapping under a live process is the failure this waits for.
        Assert.Equal("v1", File.ReadAllText(Path.Combine(current, "Contents", "MacOS", "WowLauncher")));
        Assert.False(Directory.Exists(current + MacUpdateSwapStrategy.PreviousSuffix));
    }

    /// <summary>A second update over an install that already has an <c>.old</c> beside it. One
    /// generation is kept on purpose; the previous <c>.old</c> must not block the swap.</summary>
    [Fact]
    public async Task ASecondUpdate_ReplacesThePreviousBackupInsteadOfFailing()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var current = MakeBundle("Stonetavern.app", "v2");
        MakeBundle("Stonetavern.app" + MacUpdateSwapStrategy.PreviousSuffix, "v1");
        var staging = Path.Combine(_root, ".staging");
        Directory.CreateDirectory(staging);
        var incoming = MakeBundle(Path.Combine(".staging", "Stonetavern.app"), "v3");

        var script = Path.Combine(_root, "swap.sh");
        File.WriteAllText(script,
            MacUpdateSwapStrategy.BuildScript(incoming, current, staging, DeadPid(), "WowLauncher", waitTicks: 5));

        Assert.Equal(0, await RunScript(script));
        Assert.Equal("v3", File.ReadAllText(Path.Combine(current, "Contents", "MacOS", "WowLauncher")));
        Assert.Equal("v2", File.ReadAllText(Path.Combine(
            current + MacUpdateSwapStrategy.PreviousSuffix, "Contents", "MacOS", "WowLauncher")));
    }

    /// <summary>The truth check, not the exit code: if what landed is not a usable bundle, the
    /// player's previous launcher comes back rather than being left with a broken one.</summary>
    [Fact]
    public async Task IfWhatLandedIsNotAUsableBundle_ThePreviousOneComesBack()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var current = MakeBundle("Stonetavern.app", "v1");
        var staging = Path.Combine(_root, ".staging");
        Directory.CreateDirectory(staging);
        // A directory named .app with nothing inside it — the shape a truncated or wrongly built
        // artifact has. It moves fine; it just is not a launcher.
        var incoming = Path.Combine(staging, "Stonetavern.app");
        Directory.CreateDirectory(incoming);

        var script = Path.Combine(_root, "swap.sh");
        File.WriteAllText(script,
            MacUpdateSwapStrategy.BuildScript(incoming, current, staging, DeadPid(), "WowLauncher", waitTicks: 5));

        var exit = await RunScript(script);

        Assert.NotEqual(0, exit);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(current, "Contents", "MacOS", "WowLauncher")));
    }

    // ── The three things a Codex review found on 2026-08-02 ─────────────────────────────────────
    //
    // The first version of this swap called a bundle good when Contents/MacOS was a directory. That
    // is not a launcher. All three findings below are the same shape: a swap that "succeeds" and
    // leaves the player with something that cannot start, while the build that worked has been
    // renamed to .old and nothing points at it any more.

    /// <summary>The case that was not written down: a formally valid bundle whose program cannot
    /// run. Not the same as an empty .app, and not the same as no .app at all.</summary>
    [Fact]
    public void ABundleWhoseProgramIsNotExecutable_IsRefusedBeforeAnythingMoves()
    {
        if (OperatingSystem.IsWindows()) return;   // no modes to strip

        var bundle = MakeBundle("Stonetavern.app", "v1");
        var broken = MakeBundle(Path.Combine("incoming", "Stonetavern.app"), "v2", executable: false);
        var zip = Path.Combine(_root, "update.zip");
        File.WriteAllText(zip, "x");

        Assert.Null(MacUpdateSwapStrategy.ExecutableNameOf(broken));

        var strategy = new MacUpdateSwapStrategy(Log(), (_, _) => true,
            (_, dest) => CopyDir(broken, Path.Combine(dest, "Stonetavern.app")));

        Assert.Throws<InvalidOperationException>(() =>
            strategy.ApplySwap(zip, Path.Combine(bundle, "Contents", "MacOS", "WowLauncher"), _root));
        // Nothing moved: the player still has the launcher they had.
        Assert.Equal("v1", File.ReadAllText(Path.Combine(bundle, "Contents", "MacOS", "WowLauncher")));
    }

    [Fact]
    public void ABundleWithNoInfoPlist_HasNoProgramToRun()
    {
        var bundle = MakeBundle("NoPlist.app", "v1");
        File.Delete(Path.Combine(bundle, "Contents", "Info.plist"));

        Assert.Null(MacUpdateSwapStrategy.ExecutableNameOf(bundle));
    }

    /// <summary>A plist may name the program. It may not name a path — that would let a published
    /// artifact point the launch at something outside its own bundle.</summary>
    [Theory]
    [InlineData("<key>CFBundleExecutable</key><string>../../../bin/sh</string>")]
    [InlineData("<key>CFBundleExecutable</key><string>/bin/sh</string>")]
    public void APlistThatNamesAPath_IsNotFollowed(string plistBody)
    {
        var bundle = MakeBundle("Escape.app", "v1");
        File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"), $"<plist><dict>{plistBody}</dict></plist>");

        // Falls back to "the one runnable file in MacOS/", which is the launcher itself — never the path.
        Assert.Equal("WowLauncher", MacUpdateSwapStrategy.ExecutableNameOf(bundle));
    }

    /// <summary>The blocker that costs the most and shows the least: the swap fails, the script
    /// exits, and the player is left staring at a closed launcher with a perfectly good bundle on
    /// disk that nobody starts. Every failure path after the process is gone must bring one back.</summary>
    [Fact]
    public void EveryFailureAfterTheLauncherIsGone_BringsALauncherBack()
    {
        var script = MacUpdateSwapStrategy.BuildScript("/tmp/new/S.app", "/A/S.app", "/tmp/new", 1, "S");

        // Past the wait, no bail-out exits on its own — they all go through give_up, which opens
        // whatever bundle is on disk before it leaves.
        var afterWait = script[script.IndexOf("done", StringComparison.Ordinal)..];
        Assert.DoesNotContain("exit 1", afterWait, StringComparison.Ordinal);

        // Inside give_up, not merely somewhere in the file. The first version of this assertion
        // looked for the line anywhere and stayed green when the one in give_up was deleted — a
        // placebo, caught by disabling the fix and watching nothing turn red.
        var body = script[script.IndexOf("give_up() {", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\n}", StringComparison.Ordinal)];
        Assert.Contains("open \"$CUR\"", body, StringComparison.Ordinal);

        // The one exception, and it has to stay one: the timeout. There the launcher is still
        // running, so opening anything would put a second window in front of the player.
        var timeout = script.Split('\n').Single(l => l.Contains("-gt", StringComparison.Ordinal));
        Assert.Contains("exit 1", timeout, StringComparison.Ordinal);
        Assert.DoesNotContain("open", timeout, StringComparison.Ordinal);
        Assert.DoesNotContain("give_up", timeout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IfTheProgramIsMissingAfterTheSwap_ThePreviousLauncherComesBackAndIsStarted()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var current = MakeBundle("Stonetavern.app", "v1");
        var staging = Path.Combine(_root, ".staging");
        Directory.CreateDirectory(staging);
        // Right shape, wrong contents: Contents/MacOS is there, the program is not. This passed the
        // first version of the truth check.
        var incoming = Path.Combine(staging, "Stonetavern.app");
        Directory.CreateDirectory(Path.Combine(incoming, "Contents", "MacOS"));

        var script = Path.Combine(_root, "swap.sh");
        File.WriteAllText(script,
            MacUpdateSwapStrategy.BuildScript(incoming, current, staging, DeadPid(), "WowLauncher", waitTicks: 5));

        Assert.NotEqual(0, await RunScript(script));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(current, "Contents", "MacOS", "WowLauncher")));
        Assert.False(Directory.Exists(current + ".updating"), "the update lock was left behind");
    }

    /// <summary>Two launchers, two helpers, one bundle. The second must not rename a directory the
    /// first is halfway through moving.</summary>
    [Fact]
    public async Task ASecondUpdaterStandsDown_WhileOneIsAlreadySwapping()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var current = MakeBundle("Stonetavern.app", "v1");
        var staging = Path.Combine(_root, ".staging");
        Directory.CreateDirectory(staging);
        var incoming = MakeBundle(Path.Combine(".staging", "Stonetavern.app"), "v2");

        // Somebody else holds the lock.
        Directory.CreateDirectory(current + ".updating");

        var script = Path.Combine(_root, "swap.sh");
        File.WriteAllText(script,
            MacUpdateSwapStrategy.BuildScript(incoming, current, staging, DeadPid(), "WowLauncher", waitTicks: 5));

        var exit = await RunScript(script);

        Assert.Equal(0, exit);   // standing down is not a failure
        Assert.Equal("v1", File.ReadAllText(Path.Combine(current, "Contents", "MacOS", "WowLauncher")));
        Assert.True(Directory.Exists(current + ".updating"), "it removed a lock it did not take");
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(d.Replace(from, to, StringComparison.Ordinal));
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = f.Replace(from, to, StringComparison.Ordinal);
            File.Copy(f, target, overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(f));
        }
    }

    [Fact]
    public void TheScriptStripsQuarantine_AndRelaunchesTheBundleRatherThanTheBinary()
    {
        var script = MacUpdateSwapStrategy.BuildScript("/tmp/new/S.app", "/A/S.app", "/tmp/new", 1234, "S");

        // Both are Mac-only effects that no Linux run can demonstrate, so they are pinned as text —
        // and only these two, because everything else here is proven by running the thing.
        Assert.Contains("xattr -dr com.apple.quarantine", script, StringComparison.Ordinal);
        Assert.Contains("open \"$CUR\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void APathWithASingleQuote_CannotBreakOutOfTheScript()
    {
        var script = MacUpdateSwapStrategy.BuildScript(
            "/tmp/new/S.app", "/Users/o'brien/Applications/S.app", "/tmp/new", 1, "S");

        Assert.DoesNotContain("o'brien", script, StringComparison.Ordinal);
        Assert.Contains(@"o'\''brien", script, StringComparison.Ordinal);
    }

    /// <summary>A pid that is certainly gone: start something and wait for it to exit.</summary>
    private static int DeadPid()
    {
        using var p = Process.Start(new ProcessStartInfo("/usr/bin/true") { UseShellExecute = false })!;
        p.WaitForExit();
        return p.Id;
    }
}
