using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Real Wine integration proofs for WP2 (PLAN §2 "beweisen, nicht behaupten"). These touch the real
/// filesystem, spawn wine, and need an X display, so they are <b>opt-in</b>: they run only when
/// <c>MECHAGON_WINE_IT=1</c> and <c>wine</c> is on PATH. Plain <c>dotnet test</c> (incl. CI without
/// wine/display) reports them as genuinely <b>SKIPPED</b> — via <c>[SkippableFact]</c> +
/// <c>Skip.IfNot</c>, never as silent passes (WP2 fixrunde F5). Every prefix/process they create lives
/// under a throwaway TEMP dir and is torn down with <c>wineserver -k</c> against THAT prefix only —
/// never the user's real ~/.local/share prefix, never a global wineserver (AGENTS.md gate).
/// Run the real proofs with:
/// <c>TMPDIR=$HOME/.cache/mechagon-wine-it MECHAGON_WINE_IT=1 DISPLAY=:0 dotnet test --filter WineLauncherIntegrationTests</c>
/// (TMPDIR must be a directory you own — wine refuses a prefix under a root-owned /tmp).
/// </summary>
public sealed class WineLauncherIntegrationTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("MECHAGON_WINE_IT") == "1" && WineOnPath();

    private static Serilog.ILogger Log =>
        new Serilog.LoggerConfiguration().MinimumLevel.Debug().CreateLogger();

    private static bool WineOnPath()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
            if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "wine")))
                return true;
        return false;
    }

    private static string NewTempDir(string tag)
    {
        var p = Path.Combine(Path.GetTempPath(), $"mechagon-wine-it-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(p);
        return p;
    }

    private static void KillPrefix(string prefix)
    {
        try
        {
            var psi = new ProcessStartInfo("wineserver") { UseShellExecute = false };
            psi.ArgumentList.Add("-k");
            psi.Environment["WINEPREFIX"] = prefix;
            psi.Environment["WINEDEBUG"] = "-all";
            using var p = Process.Start(psi);
            p?.WaitForExit(10_000);
        }
        catch { /* best effort teardown */ }
    }

    private static void Nuke(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Proof 3: the functional probe creates + initialises a fresh managed prefix under a TEMP
    /// path — <c>system.reg</c> appears — without ever touching the real ~/.local/share prefix.</summary>
    [SkippableFact]
    public async Task Probe_InitialisesFreshPrefix_SystemRegAppears()
    {
        Skip.IfNot(Enabled, "opt-in Wine integration test — set MECHAGON_WINE_IT=1 with wine on PATH.");

        var prefix = NewTempDir("prefix");
        try
        {
            var launcher = new WineGameLauncher(Log, new WineOptions(prefix, ProbeTimeout: TimeSpan.FromSeconds(90)));

            // Launch with a non-existent exe: the functional probe still runs (creating the prefix),
            // then the launch fails at the exe check — proving the probe path in isolation.
            var result = await launcher.LaunchAsync(Path.Combine(prefix, "does-not-exist-WoW.exe"), prefix);

            Assert.False(result.Started);
            // The launch reaching the exe-not-found check proves readiness passed end-to-end — INCLUDING
            // the F1 32-bit probe. Had 32-bit support been missing, LaunchAsync would have returned the
            // "ohne 32-bit-Unterstützung" readiness error instead of ever reaching the exe check.
            Assert.Contains("nicht gefunden", result.Error);
            Assert.DoesNotContain("32-bit", result.Error ?? "");
            Assert.True(File.Exists(Path.Combine(prefix, "system.reg")),
                "wineboot -u must have produced system.reg in the managed prefix");
            // F1: the managed prefix must carry a 32-bit (wow64) layer — the launcher's 32-bit probe
            // verified it functionally (started syswow64/cmd.exe); assert the directory it relies on.
            Assert.True(Directory.Exists(Path.Combine(prefix, "drive_c", "windows", "syswow64")),
                "the initialised prefix must have a syswow64 directory (32-bit/wow64 support)");
        }
        finally
        {
            KillPrefix(prefix);
            Nuke(prefix);
        }
    }

    /// <summary>Proof 4: the real WineGameLauncher starts a Windows PE named WoW.exe under the TEMP
    /// prefix, and the real LinuxGameProcessDetector finds it via /proc — the running-game guard works
    /// end-to-end. A harmless wine GUI builtin (winemine/notepad) stands in for the client so nothing
    /// full-screen or realm-touching runs; it is killed via wineserver -k against the TEST prefix only.
    /// </summary>
    [SkippableFact]
    public async Task LaunchAndDetect_WineProcessFoundViaProc_ThenKilled()
    {
        Skip.IfNot(Enabled, "opt-in Wine integration test — set MECHAGON_WINE_IT=1 with wine on PATH.");

        var prefix = NewTempDir("prefix");
        var clientDir = NewTempDir("client");
        try
        {
            var launcher = new WineGameLauncher(Log, new WineOptions(prefix, ProbeTimeout: TimeSpan.FromSeconds(90)));

            // Initialise the prefix first (bogus-exe launch runs the probe), then stand up a fake client.
            var init = await launcher.LaunchAsync(Path.Combine(prefix, "nope-WoW.exe"), prefix);
            Assert.False(init.Started);
            Assert.True(File.Exists(Path.Combine(prefix, "system.reg")), "prefix must be initialised");

            // A long-running GUI builtin PE copied to <clientDir>/WoW.exe — stays open until killed.
            var guiExe = FindGuiBuiltin(prefix)
                ?? throw new InvalidOperationException("No wine GUI builtin (winemine/notepad) found in prefix");
            var fakeWow = Path.Combine(clientDir, "WoW.exe");
            File.Copy(guiExe, fakeWow, overwrite: true);

            var result = await launcher.LaunchAsync(fakeWow, clientDir);
            Assert.True(result.Started, $"launch must succeed (error: {result.Error})");
            Assert.NotNull(result.ProcessId);

            // The detector must find the running WoW.exe within a short window (wine forks — poll).
            var detector = new LinuxGameProcessDetector(Log);
            var found = await PollAsync(() => detector.IsGameRunning(fakeWow), TimeSpan.FromSeconds(12));
            Assert.True(found, "LinuxGameProcessDetector must find the running WoW.exe via /proc");

            // Teardown: kill ONLY this prefix's processes, then confirm the detector goes quiet.
            KillPrefix(prefix);
            var gone = await PollAsync(() => !detector.IsGameRunning(fakeWow), TimeSpan.FromSeconds(10));
            Assert.True(gone, "after wineserver -k the detector must no longer report the client running");
        }
        finally
        {
            KillPrefix(prefix);
            Nuke(clientDir);
            Nuke(prefix);
        }
    }

    /// <summary>Find a harmless, long-running wine GUI builtin PE inside the initialised prefix.</summary>
    private static string? FindGuiBuiltin(string prefix)
    {
        string[] candidates =
        [
            Path.Combine(prefix, "drive_c", "windows", "winemine.exe"),
            Path.Combine(prefix, "drive_c", "windows", "system32", "winemine.exe"),
            Path.Combine(prefix, "drive_c", "windows", "notepad.exe"),
            Path.Combine(prefix, "drive_c", "windows", "system32", "notepad.exe"),
        ];
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    private static async Task<bool> PollAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(300);
        }
        return condition();
    }
}
