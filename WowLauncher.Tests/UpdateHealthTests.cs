using System;
using System.IO;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The health contract of a self-update: does the new build come up, and if not, does the previous
/// one come back.
///
/// <para>The failure being guarded is one the attempt ledger cannot see. The ledger answers "the
/// update never arrived"; this answers "it arrived and crashed". The swap succeeded, the file on disk
/// IS the new version, and it dies before it can do anything — so it never reads a ledger, never
/// clears a note, and every start from then on is the same crash. Measured on 2026-08-04: a launcher
/// missing its native libraries died in SkiaSharp about three seconds in, before a window appeared,
/// three times in a row.</para>
/// </summary>
public sealed class UpdateHealthTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "health-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _sentinel;
    private readonly string _quarantine;

    public UpdateHealthTests()
    {
        Directory.CreateDirectory(_root);
        _sentinel = Path.Combine(_root, UpdateHealth.SentinelName);
        _quarantine = Path.Combine(_root, UpdateHealth.QuarantineName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* scratch */ }
    }

    /// <param name="running">Was dieser Build von sich behauptet. Muss angegeben werden: die
    /// laufende Assembly ist im Testprozess die TEST-Assembly, kein Launcher.</param>
    private UpdateHealth Health(Version? running = null) =>
        new(_sentinel, _quarantine, Logger(), running ?? new Version(1, 6, 5));

    private static Serilog.ILogger Logger() =>
        new Serilog.LoggerConfiguration().CreateLogger();

    // ── Der Sentinel ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnnouncingAVersion_LeavesAMarkTheSwapScriptCanSee()
    {
        Health().ExpectVersion(new Version(1, 6, 5));

        Assert.True(File.Exists(_sentinel));
        Assert.Contains("1.6.5", File.ReadAllText(_sentinel), StringComparison.Ordinal);
    }

    [Fact]
    public void ABuildThatCameUp_ClearsTheMark()
    {
        var health = Health();
        health.ExpectVersion(new Version(1, 6, 5));

        health.ReportHealthy();

        Assert.False(File.Exists(_sentinel));
    }

    /// <summary>Reporting in without anything pending is the normal case — every ordinary start does
    /// it. It must not throw and must not create anything.</summary>
    [Fact]
    public void ReportingWithoutAnUpdate_DoesNothingAtAll()
    {
        Health().ReportHealthy();

        Assert.False(File.Exists(_sentinel));
    }

    /// <summary>The whole signal, in one assertion: a build that never reported in leaves the mark
    /// standing. That is what the swap script reads.</summary>
    [Fact]
    public void ABuildThatNeverCameUp_LeavesTheMarkStanding()
    {
        Health().ExpectVersion(new Version(1, 6, 5));

        // Kein ReportHealthy — genau das ist der Absturz vor dem ersten Fenster.

        Assert.True(File.Exists(_sentinel));
    }

    // ── Die Quarantäne ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AVersionThatWasRolledBack_IsNotInstalledAgain()
    {
        File.WriteAllText(_quarantine, "1.6.5\n");

        Assert.True(Health().IsQuarantined(new Version(1, 6, 5)));
    }

    /// <summary>
    /// The point of the quarantine, stated as a test: without it the restored build finds the same
    /// newer version in the manifest and walks straight back into the same crash — the rollback
    /// would have bought nothing.
    /// </summary>
    [Fact]
    public void WithoutTheNote_TheRestoredBuildWouldTryTheSameVersionAgain()
    {
        Assert.False(Health().IsQuarantined(new Version(1, 6, 5)));
    }

    /// <summary>A newer release is a different build and deserves its own chance. Otherwise one broken
    /// version would end updating for good and the FIX could never reach anybody.</summary>
    [Fact]
    public void ANewerVersion_GetsItsOwnChance_AndClearsTheNote()
    {
        File.WriteAllText(_quarantine, "1.6.5\n");
        var health = Health();

        Assert.False(health.IsQuarantined(new Version(1, 6, 6)));
        Assert.False(File.Exists(_quarantine));
    }

    [Fact]
    public void AnUnreadableNote_QuarantinesNothing()
    {
        File.WriteAllText(_quarantine, "not a version\n");

        Assert.False(Health().IsQuarantined(new Version(1, 6, 5)));
        Assert.Null(Health().QuarantinedVersion);
    }

    [Fact]
    public void TheRolledBackVersion_CanBeNamed()
    {
        File.WriteAllText(_quarantine, "1.6.5\n");

        Assert.Equal(new Version(1, 6, 5), Health().QuarantinedVersion);
    }

    // ── Das Windows-Skript ───────────────────────────────────────────────────────────────────────

    private static string Script(Version? target) =>
        WindowsUpdateSwapStrategy.BuildScript(
            @"C:\app\WowLauncher.exe.download", @"C:\app\WowLauncher.exe", pid: 4242, target: target);

    /// <summary>
    /// 🔴 The condition for the rollback, held down by a test: the previous build comes back only when
    /// the mark still stands AND the new process is gone. A launcher that is merely slow keeps its
    /// update — killing one that might just be busy would turn a working start into a forced
    /// downgrade, which is the worse failure.
    /// </summary>
    [Fact]
    public void TheScript_RollsBackOnlyWhenTheNewBuildIsBothSilentAndGone()
    {
        var s = Script(new Version(1, 6, 5));

        // Es wartet auf den Sentinel …
        Assert.Contains("if not exist \"%SENTINEL%\"", s, StringComparison.Ordinal);
        // … und fragt danach, ob der Prozess ueberhaupt noch laeuft …
        Assert.Contains("tasklist /FI \"IMAGENAME eq %CURNAME%\"", s, StringComparison.Ordinal);
        // … und laesst einen laufenden Prozess in Ruhe.
        Assert.Contains("kein Rueckfall", s, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScript_PutsThePreviousBuildBackAndStartsIt()
    {
        var s = Script(new Version(1, 6, 5));

        Assert.Contains("ren \"%CUR%\" \"%BROKENNAME%\"", s, StringComparison.Ordinal);
        Assert.Contains("ren \"%OLD%\" \"%CURNAME%\"", s, StringComparison.Ordinal);
        Assert.Contains("start \"\" \"%CUR%\"", s, StringComparison.Ordinal);
    }

    /// <summary>The rollback writes the note that ends the loop. Without this line the whole rollback
    /// is a detour back into the same crash.</summary>
    [Fact]
    public void TheRollback_WritesTheQuarantineNote()
    {
        var s = Script(new Version(1, 6, 5));

        Assert.Contains("echo %TARGET%>\"%QUARANTINE%\"", s, StringComparison.Ordinal);
        Assert.Contains("set \"TARGET=1.6.5\"", s, StringComparison.Ordinal);
    }

    /// <summary>Never roll back onto nothing. If the previous build is not there, the new one stays —
    /// a launcher that starts badly beats no launcher at all.</summary>
    [Fact]
    public void WithoutAPreviousBuild_NothingIsRolledBack()
    {
        var s = Script(new Version(1, 6, 5));

        Assert.Contains("if not exist \"%OLD%\" (", s, StringComparison.Ordinal);
        Assert.Contains("kein Rueckweg vorhanden", s, StringComparison.Ordinal);
    }

    /// <summary>And the truth check that this project keeps relearning: after the two renames, ask the
    /// filesystem whether the launcher is actually there — never an exit code.</summary>
    [Fact]
    public void TheRollback_ChecksRealityRatherThanAnExitCode()
    {
        var s = Script(new Version(1, 6, 5));

        Assert.Contains("if not exist \"%CUR%\" (", s, StringComparison.Ordinal);
        Assert.Contains("RUECKFALL FEHLGESCHLAGEN", s, StringComparison.Ordinal);
    }

    /// <summary>A caller that passes no target gets exactly the script that shipped: no watch, no
    /// rollback. Optional means optional.</summary>
    [Fact]
    public void WithoutATarget_TheScriptBehavesAsItAlwaysDid()
    {
        var s = Script(null);

        Assert.Contains("set \"TARGET=\"", s, StringComparison.Ordinal);
        // Der Wachzweig steht zwar im Skript, kann aber ohne Sentinel nichts ausloesen: ohne Ziel
        // schreibt UpdateService keinen, und der erste Test des Zweigs springt sofort heraus.
        Assert.Contains("if not exist \"%SENTINEL%\" goto done", s, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔴 No parentheses inside an echo that sits in a parenthesised block. cmd.exe ends the block at
    /// the FIRST closing parenthesis, quoted or not, mid-sentence or not.
    ///
    /// <para>This is not a style rule, it is a measured defect. The line
    /// <c>echo ... kein Rueckweg vorhanden (%OLD% fehlt) ...</c> ended its own <c>if</c> block right
    /// there; the rest became a separate broken command, and the <c>goto done</c> meant to be
    /// conditional ran UNCONDITIONALLY. The rollback decided correctly, wrote its reason to the log,
    /// and then did nothing at all — the launcher stayed on the crashing build. Found on 2026-08-04
    /// in the win11 VM while every string-matching test in this file was green, because none of them
    /// know what cmd.exe does with a bracket.</para>
    /// </summary>
    [Fact]
    public void NoEchoInsideABlock_CarriesAParenthesis()
    {
        var lines = Script(new Version(1, 6, 5)).Split("\r\n");
        var depth = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (depth > 0 && trimmed.StartsWith("echo ", StringComparison.Ordinal))
            {
                // Alles vor der Umleitung ist der Meldungstext.
                var text = trimmed.Split(">>", 2)[0];
                Assert.DoesNotContain('(', text);
                Assert.DoesNotContain(')', text);
            }

            // Eine `) else if ... (`-Zeile schliesst UND oeffnet — beides zaehlen, sonst driftet die
            // Tiefe und der Test meldet einen Fehler im Skript, wo keiner ist.
            if (trimmed.StartsWith(')')) depth--;
            if (trimmed.EndsWith('(')) depth++;
        }

        Assert.Equal(0, depth);   // und die Bloecke gehen sauber auf
    }

    /// <summary>
    /// 🔴 Only the build the contract is FOR may sign it off.
    ///
    /// <para>Found by the push gate on 2026-08-04, before this shipped: a second instance of the OLD
    /// build, started by hand while the update was running, would have cleared the new build's
    /// sentinel. The swap script would then have seen "healthy", not rolled back, and left the
    /// crashed new build in place — the way back gone at exactly the moment it was needed.</para>
    /// </summary>
    [Fact]
    public void AnOlderBuild_CannotSignOffTheContractOfANewerOne()
    {
        Health(new Version(1, 6, 5)).ExpectVersion(new Version(1, 6, 5));

        // Der ALTE Build meldet sich - z.B. eine zweite, von Hand gestartete Instanz.
        Health(new Version(1, 6, 4)).ReportHealthy();

        Assert.True(File.Exists(_sentinel));
    }

    /// <summary>And a build from the future does not get to sign it off either — the contract names
    /// one version, not "anything at least this new".</summary>
    [Fact]
    public void ADifferentBuildEntirely_CannotSignOffEither()
    {
        Health(new Version(1, 6, 5)).ExpectVersion(new Version(1, 6, 5));

        Health(new Version(1, 7, 0)).ReportHealthy();

        Assert.True(File.Exists(_sentinel));
    }

    /// <summary>An unreadable sentinel must not block the build it was meant for: a note nobody can
    /// parse is treated as no claim, and the running build clears it.</summary>
    [Fact]
    public void AnUnreadableSentinel_IsClearedRatherThanHonoured()
    {
        File.WriteAllText(_sentinel, "kein Versionstext\n");

        Health(new Version(1, 6, 4)).ReportHealthy();

        Assert.False(File.Exists(_sentinel));
    }

    /// <summary>The script deletes itself on every path. A leftover batch in %TEMP% would be run again
    /// by a later attempt with stale instructions.</summary>
    [Fact]
    public void EveryPath_EndsWithTheScriptDeletingItself()
    {
        var s = Script(new Version(1, 6, 5));

        Assert.Contains(":done", s, StringComparison.Ordinal);
        Assert.EndsWith("del \"%~f0\"\r\n", s, StringComparison.Ordinal);
    }
}
