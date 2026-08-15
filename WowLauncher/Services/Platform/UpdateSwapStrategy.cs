namespace WowLauncher.Services.Platform;

using System.Diagnostics;

/// <summary>
/// Performs the platform-specific swap of the running launcher binary with a freshly downloaded,
/// hash-verified one, then relaunches. A running process cannot overwrite its own image, hence the
/// detached indirection. Only reached after the hard SHA256 gate in <see cref="UpdateService"/>.
/// </summary>
public interface IUpdateSwapStrategy
{
    /// <summary>False when this platform has no self-update swap yet (Linux until WP7) — the caller
    /// then skips the swap instead of pretending to apply one.</summary>
    bool IsSupported { get; }

    /// <summary>Stage + launch a detached process that swaps <paramref name="currentExePath"/> with
    /// <paramref name="newExePath"/> and relaunches it. The caller MUST exit right after so the
    /// running binary releases its lock.
    /// <para>Codex F4b — two distinct failure modes, matching pre-WP1 semantics: the STAGING step
    /// (writing the swap script) THROWS on failure (a swap that can't even be staged is a hard error
    /// that propagates); the LAUNCH step returns <c>false</c> so the caller cleans up and falls back
    /// to a normal start. Returns <c>true</c> when the detached swap was launched.</para></summary>
    /// <param name="target">The version that is supposed to come up, for the health contract
    /// (<see cref="WowLauncher.Services.IUpdateHealth"/>). Optional so a caller that does not care
    /// keeps the behaviour that shipped: no target means no watch and no rollback.</param>
    bool ApplySwap(string newExePath, string currentExePath, string appDir, Version? target = null);
}

/// <summary>
/// Windows swap — byte-for-byte the detached cmd.exe batch that shipped in UpdateService:
/// wait 2 s, move the new exe over the (now-released) current exe, relaunch, delete self.
/// </summary>
public sealed class WindowsUpdateSwapStrategy : IUpdateSwapStrategy
{
    private readonly Serilog.ILogger _log;

    public WindowsUpdateSwapStrategy(Serilog.ILogger log) => _log = log;

    public bool IsSupported => true;

    /// <summary>Suffix of the copy kept behind, same contract as Linux: one generation, so a player
    /// whose new build refuses to start has something to go back to.</summary>
    public const string PreviousSuffix = ".old";

    /// <summary>Name of the swap's own record, written NEXT TO the launcher. The batch runs detached and
    /// windowless after the launcher is gone, so anything it echoes goes nowhere; without this file a
    /// failed swap leaves no trace on the one platform where swaps actually fail.</summary>
    public const string SwapLogName = "update-swap.log";

    /// <summary>Where a build goes that was rolled back: kept, not deleted, so the crash can still be
    /// looked at afterwards. One generation, same as <see cref="PreviousSuffix"/>.</summary>
    public const string BrokenSuffix = ".broken";

    /// <summary>
    /// The detached batch that replaces the launcher after it exits. Extracted from
    /// <see cref="ApplySwap"/> on 2026-08-01 so it can be inspected by a test at all — an external
    /// review pointed out that the Windows swap protocol, on the very platform that broke, had no test
    /// of any kind while the Linux helper was executed by three.
    ///
    /// <para>🔴 <b>Rewritten 2026-08-02 after the first real Windows end-to-end run, which the previous
    /// version had never had.</b> That run reproduced the update loop in full — three downloads, three
    /// relaunches into 1.6.1 — and measurement in the win11 VM found two independent defects, both in
    /// the two lines this script used to consist of:</para>
    /// <list type="number">
    /// <item><b><c>move</c> cannot do this job.</b> After the launcher exits, Defender still holds an
    /// open handle on the exe it just scanned: opening the target for writing is denied for a good
    /// while afterwards. <c>move</c> onto an existing target opens that target, so it fails —
    /// "Access is denied", 0 files moved. A RENAME does not open either file, it edits the directory
    /// entry, and it succeeds in exactly the same state. That is why Linux does three renames and why
    /// Windows now does the same: <c>cur</c> → <c>.old</c>, <c>new</c> → <c>cur</c>.</item>
    /// <item><b><c>if errorlevel 1</c> never fired.</b> Measured: <c>move</c> prints "Access is denied."
    /// and still exits with code <b>0</b>. So the guard added the day before — the whole point of which
    /// was to stop a blocked swap from passing for a successful one — was dead code on real cmd.exe.
    /// The check is now made against reality instead of an exit code (CORE §3): the swap counts as
    /// done only if the new file is gone from its download path AND the target path exists. This is
    /// the same lesson as the backup that was green while it copied nothing.</item>
    /// </list>
    ///
    /// <para>Two smaller repairs ride along. The script waited a flat <c>timeout /t 2</c> and hoped the
    /// launcher had exited; it now WAITS FOR THE PID, bounded, exactly as the Linux helper does — a
    /// swap under a running process is the failure this script exists to avoid. And it waits with
    /// <c>ping</c> rather than <c>timeout</c>, because <c>timeout</c> refuses to run when stdin is
    /// redirected (which it is, in a detached windowless child) and would silently not wait at all.</para>
    ///
    /// <para>The relaunch stays unconditional on purpose: a failed update must cost the player their
    /// update, never their launcher. The ceiling that ends a loop still lives in
    /// <see cref="Services.UpdateAttemptLedger"/> and still does not depend on this script running at
    /// all — the ledger is what actually stopped the loop in the run described above.</para>
    /// </summary>
    /// <param name="pid">The launcher's process id. The swap must not touch the file until this is gone.</param>
    /// <param name="waitTicks">Bounded wait, one tick ≈ 1 s. If the launcher never exits we do nothing
    /// rather than swap under it.</param>
    /// <param name="target">The version that is supposed to come up. Ends up in the quarantine note
    /// if it does not, which is what keeps the restored build from walking into the same crash again.
    /// Null disables the health watch entirely — the script then behaves exactly as before.</param>
    /// <param name="healthTicks">How long to wait for the new build to report in, one tick ≈ 2 s.
    /// Generous on purpose: a first start on a cold disk with a virus scanner in the way is slow, and
    /// a rollback of a healthy build is a worse outcome than a late one.</param>
    internal static string BuildScript(
        string newExePath, string currentExePath, int pid, int waitTicks = 100,
        Version? target = null, int healthTicks = 45)
    {
        var previous = currentExePath + PreviousSuffix;
        // ren takes a NAME, never a path, as its second argument — both files already live in the
        // same directory (UpdateService downloads next to the binary it replaces).
        var currentName = Path.GetFileName(currentExePath);
        var previousName = Path.GetFileName(previous);
        var dir = Path.GetDirectoryName(currentExePath) ?? ".";
        var log = Path.Combine(dir, SwapLogName);
        var broken = currentExePath + BrokenSuffix;

        return string.Join("\r\n",
            "@echo off",
            "setlocal",
            $"set \"NEW={newExePath}\"",
            $"set \"CUR={currentExePath}\"",
            $"set \"OLD={previous}\"",
            $"set \"CURNAME={currentName}\"",
            $"set \"OLDNAME={previousName}\"",
            $"set \"BROKEN={broken}\"",
            $"set \"BROKENNAME={Path.GetFileName(broken)}\"",
            $"set \"SENTINEL={Path.Combine(dir, Services.UpdateHealth.SentinelName)}\"",
            $"set \"QUARANTINE={Path.Combine(dir, Services.UpdateHealth.QuarantineName)}\"",
            $"set \"TARGET={target?.ToString() ?? string.Empty}\"",
            $"set \"LOG={log}\"",
            $"set \"PID={pid}\"",
            "set \"TRIES=0\"",
            "",
            "rem Wait for the launcher to be gone. ping, not timeout: timeout aborts under redirected",
            "rem stdin, which is what a detached windowless child has, and would not wait at all.",
            "rem CSV output, and a line must START with a quote to count as a process: the plain format",
            "rem prints an INFO sentence when nothing matches, and a failing tasklist would otherwise",
            "rem read exactly like 'the launcher has exited' — the one wrong answer here.",
            ":wait",
            "tasklist /FI \"PID eq %PID%\" /FO CSV /NH 2>nul | findstr /B /C:\"\\\"\" >nul",
            "if errorlevel 1 goto swap",
            "set /a TRIES+=1",
            $"if %TRIES% GEQ {waitTicks} goto giveup",
            "ping -n 2 127.0.0.1 >nul 2>&1",
            "goto wait",
            "",
            ":swap",
            "rem Free the backup name, but never let that decide the update: ren refuses an existing",
            "rem target, so if the old backup cannot be deleted (held, read-only) we step aside to a",
            "rem unique name instead of failing an update over a leftover file.",
            "if exist \"%OLD%\" del /F /Q \"%OLD%\" >nul 2>&1",
            "if exist \"%OLD%\" set \"OLD=%OLD%.%PID%\"",
            "if exist \"%OLD%\" set \"OLDNAME=%OLDNAME%.%PID%\"",
            "ren \"%CUR%\" \"%OLDNAME%\" >nul 2>&1",
            "ren \"%NEW%\" \"%CURNAME%\" >nul 2>&1",
            "",
            "rem Truth check, not an exit code: move reports success while doing nothing.",
            "if exist \"%NEW%\" goto failed",
            "if not exist \"%CUR%\" goto failed",
            "echo [%DATE% %TIME%] swap OK: %CUR% replaced, previous build kept at %OLD%>>\"%LOG%\"",
            "goto relaunch",
            "",
            ":failed",
            "rem Put the player's launcher back if the first rename went through and the second did not.",
            "if not exist \"%CUR%\" if exist \"%OLD%\" ren \"%OLD%\" \"%CURNAME%\" >nul 2>&1",
            "echo [%DATE% %TIME%] swap FAILED: could not replace %CUR% (target held or read-only)>>\"%LOG%\"",
            "goto relaunch",
            "",
            ":giveup",
            "echo [%DATE% %TIME%] swap SKIPPED: launcher pid %PID% never exited>>\"%LOG%\"",
            "",
            "rem Start what EXISTS, never a path blindly. If both renames failed and the restore failed",
            "rem too, the player's launcher is sitting under the backup name — starting it is the whole",
            "rem promise of this script. A lost update is acceptable; a lost launcher is not.",
            ":relaunch",
            "if exist \"%CUR%\" (",
            "  start \"\" \"%CUR%\"",
            "  goto watch",
            ") else if exist \"%OLD%\" (",
            "  echo [%DATE% %TIME%] relaunching the previous build from %OLD%>>\"%LOG%\"",
            "  start \"\" \"%OLD%\"",
            ") else (",
            "  echo [%DATE% %TIME%] NOTHING TO START: neither %CUR% nor %OLD% exists>>\"%LOG%\"",
            ")",
            "goto done",
            "",
            "rem ── Gesundheitsvertrag ────────────────────────────────────────────────────────────────",
            "rem Der Tausch ist durch. Damit ist NICHT bewiesen, dass der neue Build startet — genau",
            "rem dieser Fall (Tausch ok, Programm stirbt vor dem ersten Fenster) hinterliess bisher",
            "rem einen Spieler mit einem toten Launcher und einer .old daneben, die niemand zurueck-",
            "rem legt. Der neue Build loescht die Sentinel-Datei, sobald er wirklich oben ist.",
            "rem",
            "rem Zurueckgerollt wird NUR, wenn die Sentinel noch steht UND kein Prozess mehr laeuft.",
            "rem Ein langsamer Start behaelt sein Update: einen womoeglich nur beschaeftigten Launcher",
            "rem abzuschiessen waere ein schlimmerer Fehler als der, der hier verhindert wird.",
            ":watch",
            "if not exist \"%SENTINEL%\" goto done",
            "set \"WTRIES=0\"",
            ":watchloop",
            "ping -n 2 127.0.0.1 >nul 2>&1",
            "if not exist \"%SENTINEL%\" (",
            "  echo [%DATE% %TIME%] health OK: der neue Build hat sich gemeldet>>\"%LOG%\"",
            "  goto done",
            ")",
            "set /a WTRIES+=1",
            $"if %WTRIES% LSS {healthTicks} goto watchloop",
            "",
            "rem Zeit abgelaufen. Laeuft er noch? Dann ist er langsam, nicht kaputt - Finger weg.",
            "tasklist /FI \"IMAGENAME eq %CURNAME%\" /FO CSV /NH 2>nul | findstr /B /C:\"\\\"\" >nul",
            "if not errorlevel 1 (",
            "  echo [%DATE% %TIME%] health UNKLAR: %CURNAME% laeuft noch, hat sich aber nicht" +
                " gemeldet - kein Rueckfall>>\"%LOG%\"",
            "  goto done",
            ")",
            "",
            ":rollback",
            "echo [%DATE% %TIME%] health FEHLGESCHLAGEN: der neue Build ist ohne Meldung beendet" +
                " - zurueck auf den vorherigen>>\"%LOG%\"",
            // 🔴 KEINE Klammern in einem echo-Text innerhalb eines Klammerblocks. cmd.exe beendet den
            // Block an der ERSTEN schliessenden Klammer, egal ob sie in Anfuehrungszeichen steht oder
            // mitten in einem Satz. Genau hier stand einmal "(%OLD% fehlt)": der Block endete dort,
            // der Rest der Zeile wurde ein eigener, kaputter Befehl - und das darauf folgende
            // `goto done` lief damit UNBEDINGT. Ergebnis: der Rueckfall entschied sich richtig, sagte
            // es sogar ins Protokoll und tat dann nichts. Am 2026-08-04 in der win11-VM gemessen; die
            // Textbaustein-Tests waren dabei alle gruen, weil sie cmd-Semantik nicht kennen.
            "if not exist \"%OLD%\" (",
            "  echo [%DATE% %TIME%] kein Rueckweg vorhanden - der neue Build bleibt stehen>>\"%LOG%\"",
            "  goto done",
            ")",
            "if exist \"%BROKEN%\" del /F /Q \"%BROKEN%\" >nul 2>&1",
            "ren \"%CUR%\" \"%BROKENNAME%\" >nul 2>&1",
            "ren \"%OLD%\" \"%CURNAME%\" >nul 2>&1",
            "rem Wieder gegen die Wirklichkeit pruefen, nicht gegen einen Exit-Code.",
            "if not exist \"%CUR%\" (",
            "  echo [%DATE% %TIME%] RUECKFALL FEHLGESCHLAGEN: %CUR% fehlt>>\"%LOG%\"",
            "  goto done",
            ")",
            "rem Die Quarantaene-Notiz ist der Teil, der die Schleife beendet: ohne sie faende der",
            "rem wiederhergestellte Build dieselbe neuere Version im Manifest und liefe erneut in",
            "rem denselben Absturz - der Rueckfall haette nichts gebracht.",
            "echo %TARGET%>\"%QUARANTINE%\"",
            "del /F /Q \"%SENTINEL%\" >nul 2>&1",
            "echo [%DATE% %TIME%] zurueckgerollt auf den vorherigen Build, %TARGET% in" +
                " Quarantaene>>\"%LOG%\"",
            "start \"\" \"%CUR%\"",
            "",
            ":done",
            "del \"%~f0\"") + "\r\n";
    }

    public bool ApplySwap(string newExePath, string currentExePath, string appDir, Version? target = null)
    {
        // Per-process name, like the Linux helper has always used. A fixed name means two launcher
        // instances — or one leftover script from a previous attempt — overwrite each other's swap
        // instructions, and the file is predictable and executable in a world-writable directory.
        var bat = Path.Combine(Path.GetTempPath(),
            $"stonetavern-launcher-update-{Environment.ProcessId}.bat");

        // STAGING — OUTSIDE the try, so a write failure propagates exactly as pre-WP1 (Codex F4b).
        File.WriteAllText(bat,
            BuildScript(newExePath, currentExePath, Environment.ProcessId, target: target));

        // LAUNCH — caught, returns false so the caller falls back to a normal start (pre-WP1 semantics).
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = appDir,
            });
            _log.Information("Launcher update applied, restarting via {Bat}", bat);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Update swap start failed, starting normally");
            return false;
        }
    }
}

/// <summary>
/// Linux swap. Unlocked once the manifest carries a signature the launcher verifies against an
/// embedded key (<see cref="WowLauncher.Services.ManifestSignature"/>) — that was the condition WP7
/// named, and it is met: without a valid signature <see cref="UpdateService"/> never reaches this
/// class at all.
///
/// <para><b>Why this is a rename and not an overwrite.</b> A running AppImage cannot be written over
/// safely: the file may sit on a read-only mount, behind a FUSE boundary, or be open as the process
/// image. On Linux a RENAME within one directory is atomic and leaves the running process untouched —
/// it keeps its old inode until it exits, while the path already points at the new build. So the swap
/// is three renames in the target's OWN directory (never across filesystems, where rename is not
/// atomic and would degrade into a copy that can be interrupted halfway):
/// <list type="number">
/// <item>the new binary, already downloaded and hash-verified, is moved NEXT TO the current one;</item>
/// <item>the current one is renamed to <c>&lt;name&gt;.old</c> — kept, not deleted, so a player whose
/// new build refuses to start has something to go back to;</item>
/// <item>the new one takes the original path and is made executable.</item>
/// </list>
/// Only then is the replacement process started. If any step fails the previous state is put back and
/// the method returns false, which makes the caller start normally instead of leaving the player with
/// no launcher at all.</para>
///
/// <para><b>What it deliberately does not do:</b> no zsync/AppImageUpdate delta path (the project
/// still calls itself beta, and a delta cannot prove provenance — the whole file is verified against
/// the signed manifest instead), and no writing outside the directory the launcher already runs from.</para>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class LinuxUpdateSwapStrategy : IUpdateSwapStrategy
{
    private readonly Serilog.ILogger _log;
    private readonly Func<string, string[], bool> _spawn;

    public LinuxUpdateSwapStrategy(Serilog.ILogger log) : this(log, null) { }

    /// <param name="spawn">Test seam for launching the detached helper, so a test can run the generated
    /// script itself and assert what it did to real files instead of trusting its text.</param>
    internal LinuxUpdateSwapStrategy(Serilog.ILogger log, Func<string, string[], bool>? spawn)
    {
        _log = log;
        _spawn = spawn ?? DefaultSpawn;
    }

    public bool IsSupported => true;

    /// <summary>Suffix of the copy kept behind. One generation only: the point is a way back from the
    /// update that just happened, not an archive.</summary>
    public const string PreviousSuffix = ".old";

    /// <summary>
    /// Hand the swap to a detached shell script and return; the caller then exits, the script waits for
    /// that exit and only THEN touches the file.
    ///
    /// <para><b>Why a helper and not the renames in-process</b> (found by an end-to-end run on
    /// 2026-07-27, not by reasoning). Doing it here looked correct and even left the right files on
    /// disk — but the relaunch died with <c>FileNotFoundException: System.IO.Pipes</c>. A single-file
    /// .NET application loads parts of ITSELF lazily from its own path, so the moment that path points
    /// at the new build, the running process can no longer load the very assembly
    /// <see cref="Process.Start"/> needs. The same class of failure produced a SIGBUS in a second run.
    /// The launcher then reported a failed update while the new build was in fact already installed —
    /// the worst outcome of all, because it is a lie the log tells you.</para>
    ///
    /// <para>So the process must not modify its own file while it lives. The script is spawned FIRST
    /// (while the binary is still whole and every assembly still loadable), waits for the pid to go
    /// away, then renames inside one directory — atomic, same filesystem — keeps the old build beside
    /// the new one, and restores it if anything fails. This is the same shape the Windows strategy has
    /// used since day one; Linux only looked like it could get away without it.</para>
    /// </summary>
    public bool ApplySwap(string newExePath, string currentExePath, string appDir, Version? target = null)
    {
        // STAGING failures throw (contract, same as Windows): a swap that cannot even be prepared is a
        // hard error, not a "start normally" case.
        if (!File.Exists(newExePath))
            throw new FileNotFoundException("The downloaded launcher is not where it should be", newExePath);

        var script = WritePrivateHelper(
            BuildScript(newExePath, currentExePath, appDir, Environment.ProcessId, target: target));

        // LAUNCH failures are signalled, so the caller falls back to a normal start.
        try
        {
            if (!_spawn("/bin/sh", [script]))
            {
                DeletePrivateHelper(script);
                return false;
            }
            _log.Information(
                "Launcher update handed to {Script}; it applies after this process exits and keeps the " +
                "previous build at {Previous}", script, currentExePath + PreviousSuffix);
            return true;
        }
        catch (Exception ex)
        {
            DeletePrivateHelper(script);
            _log.Error(ex, "Could not start the update helper — starting normally");
            return false;
        }
    }

    /// <summary>
    /// Stages a helper in a per-user private directory. A predictable file directly under shared
    /// <c>/tmp</c> can be replaced or symlinked by another local user between writing and execution;
    /// the updater would then run their shell program. The random 0700 directory makes the path
    /// inaccessible to other users, and CreateNew prevents accidental reuse even within this process.
    /// </summary>
    private static string WritePrivateHelper(string contents)
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"stonetavern-launcher-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var script = Path.Combine(directory, "update.sh");
        using (var stream = new FileStream(script, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
            writer.Write(contents);
        File.SetUnixFileMode(script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private static void DeletePrivateHelper(string script)
    {
        try
        {
            File.Delete(script);
            Directory.Delete(Path.GetDirectoryName(script)!);
        }
        catch { /* launch failure must not turn cleanup into a second failure */ }
    }

    /// <summary>Where the swap writes what it did. Same name as on Windows, and for the same reason:
    /// when a player says "it did not update", this file is the only account of what happened after
    /// the launcher was already gone.</summary>
    public const string SwapLogName = "update-swap.log";

    /// <summary>Name the crashing build is parked under when the health contract rolls back. Kept
    /// rather than deleted, so the failure can still be looked at.</summary>
    public const string BrokenSuffix = ".broken";

    /// <summary>
    /// The helper, generated so the paths are literal and nothing is parsed at run time. Written as
    /// plain POSIX sh because that exists on every system this launcher runs on.
    ///
    /// <para><b>Der Gesundheitsvertrag (zweiter Teil, ab <c>target</c>).</b> Bis 2026-08-04 endete
    /// dieses Skript nach dem Neustart. Der Tausch war damit "erfolgreich" -- und ob der neue Build
    /// ueberhaupt hochkommt, hat nie jemand nachgesehen. Genau dieser Fall (Tausch in Ordnung,
    /// Programm stirbt vor dem ersten Fenster) hinterliess auf Windows einen Spieler mit einem toten
    /// Launcher und einer <c>.old</c>-Datei daneben, die niemand zurueckschiebt; auf Linux gab es
    /// dagegen bis hier gar nichts. Die Zielversion wurde bis in <c>ApplySwap</c> durchgereicht und
    /// dort fallengelassen.</para>
    ///
    /// <para>Zurueckgerollt wird nur, wenn die Sentinel-Datei noch steht <b>und</b> der neue Prozess
    /// weg ist. Ein langsamer Start behaelt sein Update: einen womoeglich nur beschaeftigten Launcher
    /// abzuschiessen waere ein schlimmerer Fehler als der, der hier verhindert wird.</para>
    /// </summary>
    /// <param name="target">Die Version, die hochkommen soll. Landet in der Quarantaene-Notiz, wenn
    /// sie es nicht tut -- das ist der Teil, der die Absturzschleife beendet. Null schaltet die Wache
    /// ab, dann verhaelt sich das Skript wie vorher.</param>
    /// <param name="healthTicks">Wie lange auf die Meldung gewartet wird, ein Tick = 1 s.</param>
    internal static string BuildScript(
        string newExePath, string currentExePath, string appDir, int pid, int waitTicks = 100,
        Version? target = null, int healthTicks = 90)
    {
        var previous = currentExePath + PreviousSuffix;
        var dir = Path.GetDirectoryName(currentExePath) ?? ".";
        var lines = new List<string>
        {
            "#!/bin/sh",
            "# Stonetavern launcher self-update. Generated; safe to delete if the launcher is running.",
            "set -u",
            $"NEW='{Escape(newExePath)}'",
            $"CUR='{Escape(currentExePath)}'",
            $"OLD='{Escape(previous)}'",
            $"BROKEN='{Escape(currentExePath + BrokenSuffix)}'",
            $"DIR='{Escape(appDir)}'",
            $"LOG='{Escape(Path.Combine(dir, SwapLogName))}'",
            $"PID={pid}",
            "",
            "say() { echo \"[$(date '+%Y-%m-%d %H:%M:%S')] $1\" >> \"$LOG\" 2>/dev/null; }",
            "",
            "# Wait for the launcher to let go of its own file. Bounded: if it never exits we do nothing",
            "# rather than swapping under a running process, which is the failure this script exists for.",
            "i=0",
            "while kill -0 \"$PID\" 2>/dev/null; do",
            "  i=$((i+1))",
            $"  if [ \"$i\" -gt {waitTicks} ]; then say \"swap SKIPPED: pid $PID never exited\"; exit 1; fi",
            "  sleep 0.1",
            "done",
            "",
            "# Renames inside one directory: atomic, and the old build stays until the new one is in place.",
            "rm -f \"$OLD\" 2>/dev/null",
            "if [ -e \"$CUR\" ] && ! mv \"$CUR\" \"$OLD\"; then say 'swap FAILED: could not step aside'; exit 1; fi",
            "if ! mv \"$NEW\" \"$CUR\"; then",
            "  [ -e \"$OLD\" ] && mv \"$OLD\" \"$CUR\"   # put back what the player had",
            "  say 'swap FAILED: new build could not take the path, previous restored'",
            "  exit 1",
            "fi",
            "chmod +x \"$CUR\"",
            "say \"swap OK: $CUR replaced, previous build kept at $OLD\"",
            "",
            "cd \"$DIR\" 2>/dev/null",
            "\"$CUR\" &",
            "NEWPID=$!",
        };

        if (target is not null)
        {
            lines.AddRange(new[]
            {
                "",
                "# ── Gesundheitsvertrag ───────────────────────────────────────────────────────────────",
                "# Die drei Namen stehen bewusst NUR hier: ohne Zielversion gibt es keine Wache, und",
                "# dann soll im Skript auch nichts stehen, was so aussieht, als gaebe es eine.",
                $"SENTINEL='{Escape(Path.Combine(dir, Services.UpdateHealth.SentinelName))}'",
                $"QUARANTINE='{Escape(Path.Combine(dir, Services.UpdateHealth.QuarantineName))}'",
                $"TARGET='{Escape(target.ToString())}'",
                "# Der neue Build loescht die Sentinel-Datei, sobald er wirklich oben ist (nach dem",
                "# Fenster, nicht nach Main). Steht sie danach immer noch UND ist der Prozess weg, ist er",
                "# gestorben, bevor er etwas anzeigen konnte -- dann kommt der vorherige zurueck.",
                "if [ -f \"$SENTINEL\" ]; then",
                "  w=0",
                $"  while [ \"$w\" -lt {healthTicks} ]; do",
                "    sleep 1",
                "    if [ ! -f \"$SENTINEL\" ]; then say 'health OK: der neue Build hat sich gemeldet'; break; fi",
                "    w=$((w+1))",
                "  done",
                "  if [ -f \"$SENTINEL\" ]; then",
                "    if kill -0 \"$NEWPID\" 2>/dev/null; then",
                "      # Laeuft noch, hat sich nur nicht gemeldet: langsam, nicht kaputt. Finger weg.",
                "      say 'health UNKLAR: der neue Build laeuft noch ohne Meldung - kein Rueckfall'",
                "    elif [ ! -e \"$OLD\" ]; then",
                "      say 'kein Rueckweg vorhanden - der neue Build bleibt stehen'",
                "    else",
                "      say 'health FEHLGESCHLAGEN: der neue Build ist ohne Meldung beendet - zurueck auf den vorherigen'",
                "      rm -f \"$BROKEN\" 2>/dev/null",
                "      mv \"$CUR\" \"$BROKEN\" 2>/dev/null",
                "      if mv \"$OLD\" \"$CUR\" 2>/dev/null && [ -e \"$CUR\" ]; then",
                "        # Die Quarantaene-Notiz beendet die Schleife: ohne sie faende der wiederher-",
                "        # gestellte Build dieselbe neuere Version im Manifest und liefe erneut in",
                "        # denselben Absturz - der Rueckfall haette nichts gebracht.",
                "        printf '%s\\n' \"$TARGET\" > \"$QUARANTINE\"",
                "        rm -f \"$SENTINEL\"",
                "        chmod +x \"$CUR\" 2>/dev/null",
                "        say \"zurueckgerollt auf den vorherigen Build, $TARGET in Quarantaene\"",
                "        \"$CUR\" &",
                "      else",
                "        say 'RUECKFALL FEHLGESCHLAGEN: der vorherige Build kam nicht zurueck'",
                "      fi",
                "    fi",
                "  fi",
                "fi",
            });
        }

        lines.Add("");
        lines.Add("SELF_DIR=$(dirname \"$0\")");
        lines.Add("rm -f \"$0\"");
        lines.Add("rmdir \"$SELF_DIR\" 2>/dev/null || true");
        lines.Add("");
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>Single quotes are the only thing that can end a single-quoted sh string.</summary>
    private static string Escape(string path) => path.Replace("'", "'\\''", StringComparison.Ordinal);

    private static bool DefaultSpawn(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi) is not null;
    }
}

/// <summary>
/// macOS swap. Same choreography as Linux — spawn a detached helper FIRST, let it wait for this
/// process to die, then rename inside one directory — but it moves a <b>directory</b>, because what
/// gets updated on a Mac is an <c>.app</c> bundle and not a file.
///
/// <para><b>Why the artifact is a zip and not a binary.</b> A self-contained .NET app on macOS is an
/// apphost plus sixteen dylibs plus an <c>Info.plist</c> plus an icon. Swapping only the apphost — the
/// literal reading of "swap the exe" that Windows and Linux get away with — leaves the new executable
/// beside the old libraries, which is either a crash or, worse, a launcher that starts and behaves
/// like neither version. So the published macOS update artifact is a zip OF THE BUNDLE, it is unpacked
/// while this process is still whole, and the helper then swaps the two directories.</para>
///
/// <para><b>Why unpacking happens here and the move happens there.</b> Unpacking touches only a temp
/// directory, so it is safe in-process. Moving the bundle is not: a single-file .NET application loads
/// parts of itself lazily from its own path, and the moment that path changes the running process can
/// no longer load the assembly <see cref="Process.Start"/> needs. That was measured on Linux on
/// 2026-07-27 and there is no reason a Mac would be kinder.</para>
///
/// <para><b>Not verifiable from here.</b> This is written on Fedora and cross-checked by tests that
/// run the generated script against real directories on Linux — the script is POSIX sh and the moves
/// are the same syscalls. What Linux cannot prove is the Mac-only half: whether
/// <c>open</c> brings the relaunched bundle back with its Dock icon, and how Gatekeeper reacts to a
/// bundle that was replaced underneath it. That needs one run on a real Mac before this is called
/// done (PLAN-baseline-2026-08-02.md — a human runs the critical path once per platform).</para>
/// </summary>
public sealed class MacUpdateSwapStrategy : IUpdateSwapStrategy
{
    private readonly Serilog.ILogger _log;
    private readonly Func<string, string[], bool> _spawn;
    private readonly Action<string, string> _unpack;

    public MacUpdateSwapStrategy(Serilog.ILogger log) : this(log, null, null) { }

    /// <param name="spawn">Test seam for launching the detached helper, so a test can run the script
    /// itself and assert what it did to real directories instead of trusting its text.</param>
    /// <param name="unpack">Test seam for the zip extraction.</param>
    internal MacUpdateSwapStrategy(Serilog.ILogger log, Func<string, string[], bool>? spawn,
        Action<string, string>? unpack)
    {
        _log = log;
        _spawn = spawn ?? DefaultSpawn;
        _unpack = unpack ?? DefaultUnpack;
    }

    public bool IsSupported => true;

    /// <summary>Suffix of the bundle kept behind. One generation, same as Linux: a way back from the
    /// update that just happened, not an archive.</summary>
    public const string PreviousSuffix = ".old";

    public bool ApplySwap(string newExePath, string currentExePath, string appDir, Version? target = null)
    {
        // STAGING failures throw (contract, same as the other two platforms).
        if (!File.Exists(newExePath))
            throw new FileNotFoundException("The downloaded launcher is not where it should be", newExePath);

        var bundle = BundleRootOf(currentExePath)
            ?? throw new InvalidOperationException(
                $"The running launcher is not inside an .app bundle ({currentExePath}), so there is " +
                "nothing to swap. This build was not installed the way the updater expects.");

        // Unpack while this process is still whole. A temp directory beside the bundle, not in /tmp:
        // the move below must be a rename within one filesystem to stay atomic.
        var staging = Path.Combine(Path.GetDirectoryName(bundle)!,
            $".stonetavern-update-{Environment.ProcessId}");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        _unpack(newExePath, staging);

        var bundles = Directory.GetDirectories(staging, "*.app");
        if (bundles.Length != 1)
            throw new InvalidOperationException(
                $"The downloaded update holds {bundles.Length} .app bundles, expected exactly one " +
                $"({newExePath}). Refusing to guess which one is the launcher.");

        var newBundle = bundles[0];
        var executable = ExecutableNameOf(newBundle)
            ?? throw new InvalidOperationException(
                $"The downloaded bundle has no runnable executable in Contents/MacOS ({newBundle}). " +
                "Refusing to install a launcher that cannot start.");

        var script = Path.Combine(Path.GetTempPath(),
            $"stonetavern-launcher-update-{Environment.ProcessId}.sh");
        File.WriteAllText(script,
            BuildScript(newBundle, bundle, staging, Environment.ProcessId, executable));
        if (OperatingSystem.IsMacOS())
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // LAUNCH failures are signalled, so the caller falls back to a normal start.
        try
        {
            if (!_spawn("/bin/sh", [script])) return false;
            _log.Information(
                "Launcher update handed to {Script}; it applies after this process exits and keeps the " +
                "previous bundle at {Previous}", script, bundle + PreviousSuffix);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not start the update helper — starting normally");
            return false;
        }
    }

    /// <summary>
    /// The <c>.app</c> directory a running executable lives in, or null when it does not live in one.
    /// The layout is fixed by Apple: <c>Something.app/Contents/MacOS/executable</c>, so this walks up
    /// exactly three levels and checks the name rather than searching for a ".app" anywhere in the
    /// path — a player whose game folder is called <c>WoW.app</c> would otherwise hand this method a
    /// directory it must never touch.
    /// </summary>
    internal static string? BundleRootOf(string exePath)
    {
        var macOsDir = Path.GetDirectoryName(Path.GetFullPath(exePath));
        if (macOsDir is null || !Path.GetFileName(macOsDir).Equals("MacOS", StringComparison.Ordinal))
            return null;

        var contents = Path.GetDirectoryName(macOsDir);
        if (contents is null || !Path.GetFileName(contents).Equals("Contents", StringComparison.Ordinal))
            return null;

        var bundle = Path.GetDirectoryName(contents);
        if (bundle is null || !bundle.EndsWith(".app", StringComparison.Ordinal)) return null;
        return bundle;
    }

    /// <summary>
    /// The name of the program this bundle actually runs, or null when it has none that could run.
    ///
    /// <para>"Contents/MacOS exists" is not a launcher. A zip carrying an empty <c>Contents/MacOS</c>,
    /// or one whose executable lost its <c>+x</c> bit, passes that check and installs a bundle that
    /// cannot start — while the working one has been renamed to <c>.old</c> and nothing points at it
    /// any more. So: <c>Info.plist</c> must be there, and there must be a real, executable, regular
    /// file to run. <c>CFBundleExecutable</c> decides which one when the plist is readable XML;
    /// otherwise the single executable file in <c>Contents/MacOS</c> is taken, and an ambiguous
    /// directory is a refusal rather than a guess.</para>
    /// </summary>
    internal static string? ExecutableNameOf(string bundle)
    {
        var contents = Path.Combine(bundle, "Contents");
        var macOs = Path.Combine(contents, "MacOS");
        if (!File.Exists(Path.Combine(contents, "Info.plist")) || !Directory.Exists(macOs)) return null;

        static bool Runnable(string path)
        {
            if (!File.Exists(path)) return false;
            if (new FileInfo(path).LinkTarget is not null) return false;   // a link is not the program
            if (OperatingSystem.IsWindows()) return true;                  // no modes to read
            return File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute);
        }

        if (DeclaredExecutable(Path.Combine(contents, "Info.plist")) is string declared)
            return Runnable(Path.Combine(macOs, declared)) ? declared : null;

        var runnable = Directory.GetFiles(macOs).Where(Runnable).ToList();
        return runnable.Count == 1 ? Path.GetFileName(runnable[0]) : null;
    }

    /// <summary>The <c>CFBundleExecutable</c> of an XML plist, or null when the plist is binary or
    /// does not name one. Deliberately a small regex and not a plist parser: the only question is
    /// which file to check, and a plist this cannot read falls back to the check above rather than
    /// to a guess.</summary>
    private static string? DeclaredExecutable(string infoPlist)
    {
        try
        {
            var text = File.ReadAllText(infoPlist);
            var m = System.Text.RegularExpressions.Regex.Match(text,
                @"<key>\s*CFBundleExecutable\s*</key>\s*<string>([^<]+)</string>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var name = m.Groups[1].Value.Trim();
            // A declared name that is a path is not a name — never let a plist point outside MacOS/.
            return name.Length == 0 || name.Contains('/') || name.Contains("..", StringComparison.Ordinal)
                ? null : name;
        }
        catch { return null; }
    }

    /// <summary>
    /// The helper. Same shape as the Linux one: literal paths, no parsing at run time, bounded wait,
    /// and the old bundle stays until the new one is in place.
    /// </summary>
    internal static string BuildScript(
        string newBundle, string currentBundle, string staging, int pid, string executable,
        int waitTicks = 100)
    {
        var previous = currentBundle + PreviousSuffix;
        return string.Join('\n',
            "#!/bin/sh",
            "# Stonetavern launcher self-update. Generated; safe to delete if the launcher is running.",
            "set -u",
            $"NEW='{Escape(newBundle)}'",
            $"CUR='{Escape(currentBundle)}'",
            $"OLD='{Escape(previous)}'",
            $"STAGE='{Escape(staging)}'",
            $"EXE='{Escape(executable)}'",
            $"LOCK='{Escape(currentBundle)}.updating'",
            $"PID={pid}",
            "",
            "# Every way out of this script that is not 'the launcher is still running' ends with a",
            "# launcher on screen. Getting this wrong is not a crash, it is worse: the player pressed",
            "# update, the window closed, and nothing ever came back — with a perfectly intact bundle",
            "# sitting on disk that nobody starts.",
            "give_up() {",
            "  rm -rf \"$STAGE\" 2>/dev/null",
            "  rmdir \"$LOCK\" 2>/dev/null",
            "  [ -d \"$CUR\" ] && open \"$CUR\"",
            "  rm -f \"$0\"",
            "  exit 1",
            "}",
            "",
            "# Wait for the launcher to let go of its own bundle. Bounded: if it never exits we do",
            "# nothing rather than swapping under a running process. This is the ONE exit that must",
            "# not open anything — that process is still alive and on screen.",
            "i=0",
            "while kill -0 \"$PID\" 2>/dev/null; do",
            "  i=$((i+1))",
            $"  [ \"$i\" -gt {waitTicks} ] && {{ rm -rf \"$STAGE\" 2>/dev/null; rm -f \"$0\"; exit 1; }}",
            "  sleep 0.1",
            "done",
            "",
            "# One updater at a time. Two launchers, two helpers, and the second one renames a bundle",
            "# the first is halfway through moving — mkdir is the atomic test-and-set every sh has.",
            "if ! mkdir \"$LOCK\" 2>/dev/null; then",
            "  rm -rf \"$STAGE\" 2>/dev/null",
            "  [ -d \"$CUR\" ] && open \"$CUR\"",
            "  rm -f \"$0\"",
            "  exit 0",
            "fi",
            "",
            "# Nothing downloaded ever carries the quarantine flag into a bundle a player already",
            "# trusted: a replaced bundle that suddenly asks Gatekeeper for permission looks to the",
            "# player exactly like the malware warning this project spends so much effort avoiding.",
            "xattr -dr com.apple.quarantine \"$NEW\" 2>/dev/null",
            "",
            "# Renames inside one directory: atomic, and the old bundle stays until the new one landed.",
            "rm -rf \"$OLD\" 2>/dev/null",
            "if [ -e \"$CUR\" ] && ! mv \"$CUR\" \"$OLD\"; then give_up; fi",
            "if ! mv \"$NEW\" \"$CUR\"; then",
            "  [ -e \"$OLD\" ] && mv \"$OLD\" \"$CUR\"   # put back what the player had",
            "  give_up",
            "fi",
            "",
            "# Truth check, not an exit code — and not 'is there a folder' either. What has to be true",
            "# is that there is a program to run: the file Info.plist names, executable, really there.",
            "if [ ! -f \"$CUR/Contents/MacOS/$EXE\" ] || [ ! -x \"$CUR/Contents/MacOS/$EXE\" ]; then",
            "  rm -rf \"$CUR\" 2>/dev/null",
            "  [ -e \"$OLD\" ] && mv \"$OLD\" \"$CUR\"",
            "  give_up",
            "fi",
            "",
            "rm -rf \"$STAGE\" 2>/dev/null",
            "rmdir \"$LOCK\" 2>/dev/null",
            "# open, not the executable directly: that is what gives the relaunched launcher its Dock",
            "# icon and its own LaunchServices session instead of a headless child of this script.",
            "open \"$CUR\"",
            "rm -f \"$0\"",
            "") + "\n";
    }

    /// <summary>Single quotes are the only thing that can end a single-quoted sh string.</summary>
    private static string Escape(string path) => path.Replace("'", "'\\''", StringComparison.Ordinal);

    /// <summary>
    /// Unpack the bundle with <c>ditto</c> on macOS, and only fall back to the managed unzip off it.
    ///
    /// <para><b>Measured, not assumed</b> (2026-08-02, .NET 10 on Linux): <c>ZipFile.ExtractToDirectory</c>
    /// DOES restore the executable bit — and does NOT restore symlinks. A link comes back as an
    /// ordinary file whose content is the target path. An <c>.app</c> with any framework in it is
    /// built out of exactly such links (<c>Versions/Current</c>, the dylib aliases beside it), so the
    /// result is a bundle that unpacks without an error, passes a "does the folder exist" check, and
    /// cannot start. <c>ditto -x -k</c> is Apple's own tool for this and keeps links, modes and
    /// extended attributes.</para>
    ///
    /// <para>The managed path stays for the non-macOS case, which in practice means tests and a
    /// developer machine — never a player.</para>
    /// </summary>
    private static void DefaultUnpack(string zipPath, string destDir)
    {
        if (!OperatingSystem.IsMacOS())
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, destDir, overwriteFiles: true);
            return;
        }

        var psi = new ProcessStartInfo("/usr/bin/ditto")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-x", "-k", zipPath, destDir }) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not run ditto to unpack the update");
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"ditto could not unpack the update (exit {p.ExitCode}): {stderr.Trim()}");
    }

    private static bool DefaultSpawn(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi) is not null;
    }
}

/// <summary>
/// Neutral swap for platforms without a dedicated implementation yet (any host that is not Windows,
/// Linux or macOS). Reports unsupported so UpdateService refuses the swap cleanly; the message names
/// no platform.
/// </summary>
public sealed class UnsupportedUpdateSwapStrategy : IUpdateSwapStrategy
{
    public bool IsSupported => false;

    public bool ApplySwap(string newExePath, string currentExePath, string appDir, Version? target = null) =>
        throw new PlatformNotSupportedException(
            "Der automatische Launcher-Selbst-Update wird auf diesem Betriebssystem noch nicht unterstützt.");
}
