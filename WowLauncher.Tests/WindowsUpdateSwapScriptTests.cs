namespace WowLauncher.Tests;

using System;
using System.IO;
using WowLauncher.Services.Platform;
using Xunit;

/// <summary>
/// The Windows swap protocol — the platform that actually broke on 2026-08-01.
///
/// <para><b>Honest about what this proves.</b> These assertions read the generated batch; they do not
/// run <c>cmd.exe</c>, because this suite builds on Linux. That makes them a check on the protocol, not
/// on Windows behaviour. Stated here rather than left implicit, because a test that quietly claims more
/// than it checks is exactly the kind of green that hid the original loop — and this file is the proof
/// of that: its previous version was fully green while asserting, in detail, the correctness of a
/// mechanism that does nothing at all on real Windows.</para>
///
/// <para>What the previous version asserted, and what measurement in the win11 VM found on 2026-08-02:
/// it required <c>if errorlevel 1</c> after <c>move /Y</c> and called that THE regression guard. On
/// real cmd.exe, <c>move</c> onto a target Defender still holds prints "Access is denied.", moves
/// nothing, and exits <b>0</b> — so the guard could never fire, and the loop it was written to stop was
/// reproduced end to end. The evidence for the current protocol is therefore not in this file; it is a
/// real 1.6.1 → 1.6.2 swap in the VM, recorded in
/// <c>(internal design notes, not published)</c>.</para>
/// </summary>
public class WindowsUpdateSwapScriptTests
{
    private const string NewExe = @"C:\Games\Stonetavern\WowLauncher.exe.download";
    private const string CurrentExe = @"C:\Games\Stonetavern\WowLauncher.exe";
    private const int Pid = 4242;

    private static string Script(int waitTicks = 100) =>
        WindowsUpdateSwapStrategy.BuildScript(NewExe, CurrentExe, Pid, waitTicks);

    private static int At(string s, string needle) =>
        s.IndexOf(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>THE regression, restated after measurement: <c>move</c> cannot perform this swap at all.
    /// It opens the target, and the target is held by Defender for a while after the launcher exits.
    /// A script that reaches for <c>move</c> again is the bug coming back.</summary>
    [Fact]
    public void TheScript_DoesNotUseMove() =>
        Assert.DoesNotContain("move /Y", Script(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The swap is two renames — renames edit the directory entry instead of opening the file,
    /// which is why they succeed in the exact state where move fails.</summary>
    [Fact]
    public void TheSwap_IsTwoRenames_CurrentOutOfTheWayFirst()
    {
        var s = Script();
        var toOld = At(s, "ren \"%CUR%\" \"%OLDNAME%\"");
        var toCur = At(s, "ren \"%NEW%\" \"%CURNAME%\"");
        Assert.True(toOld > 0, "current is never renamed out of the way");
        Assert.True(toCur > 0, "the download never takes the launcher's path");
        Assert.True(toOld < toCur, "the target must be vacated before the new build takes its place");
    }

    /// <summary><c>ren</c> takes a NAME as its second argument, never a path — passing a path is a
    /// classic silent failure ("The syntax of the command is incorrect") that would leave the launcher
    /// unchanged while everything else looked fine.</summary>
    [Fact]
    public void TheRenameTargets_AreBareNames_NotPaths()
    {
        var s = Script();
        Assert.Contains($"set \"CURNAME={Path.GetFileName(CurrentExe)}\"", s, StringComparison.Ordinal);
        Assert.Contains($"set \"OLDNAME={Path.GetFileName(CurrentExe)}{WindowsUpdateSwapStrategy.PreviousSuffix}\"",
            s, StringComparison.Ordinal);
        Assert.DoesNotContain("ren \"%CUR%\" \"%OLD%\"", s, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Success is decided against the filesystem, never against an exit code. Measured reason:
    /// <c>move</c> reported success (exit 0) while moving nothing, so every exit-code-based guard here
    /// was dead code. Same lesson as a backup that is green while it copies nothing (CORE §3).</summary>
    [Fact]
    public void SuccessIsCheckedAgainstReality_NotAnExitCode()
    {
        var s = Script();
        Assert.Contains("if exist \"%NEW%\" goto failed", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if not exist \"%CUR%\" goto failed", s, StringComparison.OrdinalIgnoreCase);
        // The only errorlevel left is tasklist's, which reports whether the pid is still alive.
        Assert.Contains("if errorlevel 1 goto swap", s, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The truth check has to come after the renames; placed before, it would describe the
    /// state the script was about to change and always look fine.</summary>
    [Fact]
    public void TheTruthCheck_ComesAfterTheRenames() =>
        Assert.True(At(Script(), "ren \"%NEW%\" \"%CURNAME%\"") < At(Script(), "if exist \"%NEW%\" goto failed"));

    /// <summary>A swap under a running process is the failure this script exists to prevent, so it waits
    /// for the pid instead of guessing a duration — the flat two-second wait it used to do was a hope,
    /// and the Linux helper had always done it properly.</summary>
    [Fact]
    public void TheScript_WaitsForTheLauncherToExit_BeforeTouchingAnything()
    {
        var s = Script();
        Assert.Contains($"set \"PID={Pid}\"", s, StringComparison.Ordinal);
        Assert.Contains("tasklist /FI \"PID eq %PID%\"", s, StringComparison.OrdinalIgnoreCase);
        Assert.True(At(s, "tasklist") < At(s, ":swap"), "the wait must precede the swap");
    }

    /// <summary><c>timeout</c> refuses to run with redirected stdin — which is exactly the condition in
    /// a detached, windowless child — and would then not wait at all while looking like it did.</summary>
    [Fact]
    public void TheScript_DoesNotWaitWithTimeout()
    {
        var s = Script();
        Assert.DoesNotContain("timeout /t", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ping -n 2 127.0.0.1", s, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The wait is bounded: if the launcher never exits, the script gives up rather than
    /// swapping the file underneath a live process.</summary>
    [Fact]
    public void TheWait_IsBounded()
    {
        Assert.Contains("if %TRIES% GEQ 7 goto giveup", Script(waitTicks: 7), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(":giveup", Script(), StringComparison.Ordinal);
    }

    /// <summary>If the target was vacated but the new build failed to take its place, the player's own
    /// launcher goes back. Losing an update is acceptable; losing the launcher is not.</summary>
    [Fact]
    public void AHalfDoneSwap_PutsThePlayersLauncherBack() =>
        Assert.Contains("if not exist \"%CUR%\" if exist \"%OLD%\" ren \"%OLD%\" \"%CURNAME%\"",
            Script(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The previous build is kept, not deleted — one generation, so a new build that refuses to
    /// start is recoverable. Same contract as Linux.</summary>
    [Fact]
    public void ThePreviousBuild_IsKeptBeside_TheNewOne() =>
        Assert.Contains($"set \"OLD={CurrentExe}{WindowsUpdateSwapStrategy.PreviousSuffix}\"",
            Script(), StringComparison.Ordinal);

    /// <summary>Whatever happens, the player gets their launcher back — every path ends in the single
    /// relaunch, and it always starts the INSTALLED path.</summary>
    [Fact]
    public void EveryPath_EndsInTheRelaunch_OfTheInstalledBinary()
    {
        var s = Script();
        foreach (var path in new[] { "goto relaunch", ":giveup", ":failed" })
            Assert.Contains(path, s, StringComparison.Ordinal);

        // Zwei Startzeilen, nicht mehr eine — und die zweite ist der Grund, warum diese Zusage
        // praeziser formuliert werden musste statt aufgeweicht: seit dem Gesundheitsvertrag
        // (2026-08-04) startet auch der Rueckfallzweig den Launcher. Beide koennen einander nie
        // begegnen: der Rueckfall wird erst erreicht, nachdem der erste Start ohne Meldung geendet
        // hat und sein Prozess nachweislich weg ist. Was der Test wirklich zusichert, ist also
        // nicht "genau ein Vorkommen", sondern: **kein Doppelstart in einem Durchlauf** — jeder
        // Startbefehl liegt hinter einer Marke, die den jeweils anderen ausschliesst.
        var starts = s.Split("start \"\" \"%CUR%\"").Length - 1;
        Assert.Equal(2, starts);
        Assert.True(At(s, ":relaunch") < At(s, "start \"\" \"%CUR%\""));
        // Der zweite Start gehoert zum Rueckfall und zu nichts sonst.
        Assert.True(At(s, ":rollback") < s.LastIndexOf("start \"\" \"%CUR%\"", StringComparison.Ordinal));
        // Und der Rueckfall ist von der normalen Bahn durch das Ende getrennt: wer den ersten Start
        // erreicht, laeuft in :watch und von dort entweder nach :done oder erst nach :rollback.
        Assert.True(At(s, "goto done") < At(s, ":rollback"));
    }

    /// <summary>Starting the downloaded copy would leave the player running an executable the next
    /// update overwrites underneath them.</summary>
    [Fact]
    public void NoPath_StartsTheDownloadedFileDirectly()
    {
        var s = Script();
        Assert.DoesNotContain("start \"\" \"%NEW%\"", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"start \"\" \"{NewExe}\"", s, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The batch runs detached and windowless after the launcher is gone, so anything it echoes
    /// to a console goes nowhere. Its outcome is written next to the launcher instead — without this a
    /// failed swap leaves no trace on the one platform where swaps actually fail.</summary>
    [Fact]
    public void TheOutcome_IsRecordedNextToTheLauncher()
    {
        var s = Script();
        var log = Path.Combine(Path.GetDirectoryName(CurrentExe)!, WindowsUpdateSwapStrategy.SwapLogName);
        Assert.Contains($"set \"LOG={log}\"", s, StringComparison.Ordinal);
        foreach (var outcome in new[] { "swap OK", "swap FAILED", "swap SKIPPED" })
            Assert.Contains(outcome, s, StringComparison.Ordinal);
    }

    // ── Hardening from the external review of this fix (Codex verdict, 2026-08-02) ───────────────

    /// <summary>THE remaining way to lose a player's launcher entirely: both renames fail AND the
    /// restore fails, and the script then starts a path that no longer exists. It must start whatever
    /// is actually there — the previous build under the backup name is still a working launcher.</summary>
    [Fact]
    public void TheRelaunch_StartsWhateverExists_NeverAPathBlindly()
    {
        var s = Script();
        Assert.Contains("if exist \"%CUR%\" (", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("else if exist \"%OLD%\" (", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start \"\" \"%OLD%\"", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOTHING TO START", s, StringComparison.Ordinal);
    }

    /// <summary>A backup that cannot be deleted must not cost the update. <c>ren</c> refuses an
    /// existing target, so a held or read-only leftover <c>.old</c> would fail both renames and burn
    /// one of the three ledger attempts for nothing. The script steps aside to a unique name.</summary>
    [Fact]
    public void ALeftoverBackup_DoesNotBlockTheUpdate()
    {
        var s = Script();
        Assert.Contains("if exist \"%OLD%\" set \"OLD=%OLD%.%PID%\"", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if exist \"%OLD%\" set \"OLDNAME=%OLDNAME%.%PID%\"", s, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The liveness probe must not read "tasklist itself failed" as "the launcher has exited"
    /// — that is the one wrong answer, because it swaps the file under a running process. CSV plus a
    /// leading-quote match distinguishes a real process row from the INFO sentence tasklist prints
    /// when nothing matches.</summary>
    [Fact]
    public void TheLivenessProbe_CannotMistakeItsOwnFailureForAnExit()
    {
        var s = Script();
        Assert.Contains("/FO CSV /NH", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("findstr /B", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("| find \"%PID%\"", s, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>cmd.exe requires CRLF; a batch written with bare LF misparses labels and blocks.</summary>
    [Fact]
    public void TheScript_UsesWindowsLineEndings()
    {
        var s = Script();
        Assert.Contains("\r\n", s);
        Assert.DoesNotContain(s.Replace("\r\n", ""), "\n");
    }

    [Fact]
    public void TheScript_RemovesItself() =>
        Assert.Contains("del \"%~f0\"", Script(), StringComparison.OrdinalIgnoreCase);
}
