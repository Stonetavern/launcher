namespace WowLauncher.Services;

using WowLauncher.Services.Platform;

/// <summary>
/// The health contract of a self-update: the new build has to prove it can actually start, and if it
/// cannot, the previous one comes back by itself.
///
/// <para><b>Why this exists, and why the attempt ledger does not cover it.</b>
/// <see cref="UpdateAttemptLedger"/> answers "the update never arrived" — the swap was blocked, the
/// old binary came back up, and without a brake it would try forever. This class answers the opposite
/// failure: <b>the update arrived and is broken</b>. The swap succeeded, the file on disk is the new
/// version, and it dies before it can do anything — so it never reads the ledger, never clears the
/// note, and every start from now on is the same crash. The player is left with a launcher that no
/// longer starts and a <c>.old</c> file beside it that nobody will ever put back.</para>
///
/// <para>That is not hypothetical. On 2026-08-04 a launcher whose native libraries were missing died
/// with <c>TypeInitializationException</c> in SkiaSharp roughly three seconds after start, before a
/// window appeared, three times in a row. A running process cannot repair that: whatever rolls back
/// must not depend on the broken application.</para>
///
/// <para><b>The contract, in three files, all next to the executable</b> (not in the state directory —
/// the swap script has to see them, and after a rollback the OLD build has to find them):</para>
/// <list type="number">
/// <item><b>Sentinel</b> (<see cref="SentinelName"/>) — written by the OLD build just before it hands
/// over. It names the version that is supposed to come up.</item>
/// <item>The new build <b>deletes the sentinel once it is really up</b>
/// (<see cref="ReportHealthy"/>) — after the shell window exists, not merely after Main was entered.
/// Deleting it earlier would prove nothing.</item>
/// <item>The swap script waits. If the sentinel is still there and the process is <b>gone</b>, it puts
/// the previous build back and writes a <b>quarantine note</b> (<see cref="QuarantineName"/>).</item>
/// </list>
///
/// <para>The quarantine note is what stops the loop from starting over: the restored build reads it
/// (<see cref="IsQuarantined"/>) and refuses to auto-apply exactly that version again. Without it the
/// old build would come up, see the same newer version in the manifest, and walk back into the same
/// crash — the rollback would have bought nothing.</para>
///
/// <para><b>Deliberately narrow:</b> a rollback happens only when the new process is <b>dead</b> while
/// the sentinel still stands. A process that is merely slow keeps its update. Killing a launcher that
/// might just be busy would turn a working start into a forced downgrade, which is a worse failure
/// than the one being prevented.</para>
/// </summary>
public interface IUpdateHealth
{
    /// <summary>Announce the version that is about to take over. Called before the swap hands off.</summary>
    void ExpectVersion(Version target);

    /// <summary>The new build is genuinely up — clear the sentinel. Safe to call when there is none.</summary>
    void ReportHealthy();

    /// <summary>True when this exact version already rolled back once and must not be auto-applied
    /// again.</summary>
    bool IsQuarantined(Version target);

    /// <summary>The version that was rolled back, or null. Only for telling the player why they are
    /// not on the newest build.</summary>
    Version? QuarantinedVersion { get; }
}

/// <inheritdoc/>
public sealed class UpdateHealth : IUpdateHealth
{
    /// <summary>Written by the outgoing build, deleted by the incoming one. Its presence after the new
    /// process is gone is the whole signal.</summary>
    public const string SentinelName = "update-health.txt";

    /// <summary>Left behind by the swap script when it rolls back. Names the version that failed.</summary>
    public const string QuarantineName = "update-quarantine.txt";

    private readonly string _sentinel;
    private readonly string _quarantine;
    private readonly Serilog.ILogger _log;
    private readonly Version _running;

    public UpdateHealth(IAppPaths paths, Serilog.ILogger log)
    {
        // Next to the executable, NOT in StateDir: the swap script is a batch/shell file that knows
        // the exe's directory and nothing else, and after a rollback the restored build must find the
        // quarantine note without depending on where a config resolver would have put it.
        //
        // 🔴 "Das Programm" heisst hier: die Datei, die der Spieler gestartet hat -- dieselbe, die der
        // Tausch ersetzt. Unter einem AppImage ist das NICHT Environment.ProcessPath: das zeigt in die
        // schreibgeschuetzte squashfs-Einhaengung, die es nach dem Beenden nicht mehr gibt. Der
        // Sentinel waere dort nicht schreibbar (still, nur eine Warnung) und die Quarantaene-Notiz nie
        // auffindbar -- ein Vertrag, den es gibt und der nichts tut.
        var dir = Path.GetDirectoryName(UpdateService.CurrentBinaryPath(AppContext.BaseDirectory))
                  ?? AppContext.BaseDirectory;
        _sentinel = Path.Combine(dir, SentinelName);
        _quarantine = Path.Combine(dir, QuarantineName);
        _log = log;
        _running = UpdateService.RunningVersion(System.Reflection.Assembly.GetExecutingAssembly());
        _ = paths;
    }

    /// <summary>Test seam: both paths given directly.</summary>
    /// <param name="runningVersion">What this build calls itself. A test needs to state it: the
    /// running assembly in a test host is the TEST assembly, not a launcher.</param>
    internal UpdateHealth(string sentinelPath, string quarantinePath, Serilog.ILogger log,
        Version? runningVersion = null)
    {
        _sentinel = sentinelPath;
        _quarantine = quarantinePath;
        _log = log;
        _running = runningVersion
                   ?? UpdateService.RunningVersion(System.Reflection.Assembly.GetExecutingAssembly());
    }

    public void ExpectVersion(Version target)
    {
        try
        {
            File.WriteAllText(_sentinel, $"{target}\n");
            _log.Information("Gesundheitsvertrag gesetzt: {Target} muss sich melden ({Path})",
                target, _sentinel);
        }
        catch (Exception ex)
        {
            // Not fatal, and the direction matters: no sentinel means the swap script will not roll
            // back. That is the safe way to fail — an update that cannot be watched still installs,
            // it just loses its safety net. Refusing to update because a note could not be written
            // would turn a bookkeeping problem into a stuck launcher, the same reasoning as in
            // UpdateAttemptLedger.RecordAttempt.
            _log.Warning(ex, "Gesundheitsvertrag konnte nicht gesetzt werden ({Path})", _sentinel);
        }
    }

    public void ReportHealthy()
    {
        try
        {
            // 🔴 ZUERST: nur den EIGENEN Vertrag quittieren, bevor irgendetwas gelöscht wird.
            //
            // Ein Sentinel nennt die Version, die kommen soll; wer eine andere ist, hat nichts zu
            // bestätigen. Ohne diesen Abgleich könnte eine zweite, parallel oder von Hand gestartete
            // Instanz des ALTEN Builds den Vertrag des NEUEN erfüllen: das Swap-Skript sähe
            // „gesund", rollte nicht zurück, und der abgestürzte neue Build bliebe aktiv — der
            // Rückweg wäre genau dann weg, wenn er gebraucht wird. (Codex im Push-Gate, 2026-08-04,
            // bevor das ausgeliefert wurde. Festgehalten von
            // UpdateHealthTests.AnOlderBuild_CannotSignOffTheContractOfANewerOne.)
            var expected = ExpectedVersion;

            if (expected is not null && UpdateService.CompareVersions(_running, expected) != 0)
            {
                _log.Information(
                    "Gesundheitsvertrag gilt {Expected}, hier läuft {Running} — nicht quittiert",
                    expected, _running);
                return;
            }

            // Kein offener Vertrag: der Normalfall bei jedem gewöhnlichen Start.
            if (!File.Exists(_sentinel))
                return;

            File.Delete(_sentinel);
            _log.Information("Gesundheitsvertrag erfüllt — der neue Build ist oben");
        }
        catch (Exception ex)
        {
            // 🔴 This one is worth a warning rather than a debug line. A sentinel that cannot be
            // deleted looks exactly like a crashed launcher to the swap script: the next start would
            // be rolled back even though nothing is wrong.
            _log.Warning(ex, "Gesundheitsvertrag nicht quittierbar ({Path}) — ein Rückfall auf den " +
                             "vorherigen Build ist möglich, obwohl dieser Build läuft", _sentinel);
        }
    }

    /// <summary>The version the pending sentinel demands, or null when there is none. Test seam and
    /// the basis for <see cref="ReportHealthy"/> only quitting its own contract.</summary>
    internal Version? ExpectedVersion
    {
        get
        {
            try
            {
                if (!File.Exists(_sentinel))
                    return null;

                var first = File.ReadAllLines(_sentinel).FirstOrDefault()?.Trim();
                return Version.TryParse(first, out var v) ? v : null;
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Gesundheitsvertrag nicht lesbar ({Path})", _sentinel);
                return null;
            }
        }
    }

    public Version? QuarantinedVersion
    {
        get
        {
            try
            {
                if (!File.Exists(_quarantine))
                    return null;

                var first = File.ReadAllLines(_quarantine).FirstOrDefault()?.Trim();
                return Version.TryParse(first, out var v) ? v : null;
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Quarantäne-Notiz nicht lesbar ({Path})", _quarantine);
                return null;
            }
        }
    }

    public bool IsQuarantined(Version target)
    {
        var failed = QuarantinedVersion;
        if (failed is null)
            return false;

        // Only THIS version is quarantined. A newer release is a different build and deserves its own
        // chance — otherwise one broken version would end updating for good, and the fix could never
        // reach anybody.
        if (UpdateService.CompareVersions(failed, target) != 0)
        {
            ClearQuarantine();
            return false;
        }

        _log.Warning("Launcher {Target} ist beim Start abgestürzt und wurde zurückgerollt — " +
                     "diese Version wird nicht erneut automatisch installiert", target);
        return true;
    }

    private void ClearQuarantine()
    {
        try { if (File.Exists(_quarantine)) File.Delete(_quarantine); }
        catch (Exception ex) { _log.Debug(ex, "Quarantäne-Notiz nicht löschbar ({Path})", _quarantine); }
    }
}
