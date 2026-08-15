namespace WowLauncher.Services;

using WowLauncher.Services.Platform;

/// <summary>
/// Remembers which launcher version this installation last tried to update to, and how often that
/// attempt was made without the new version ever showing up.
///
/// <para><b>Why this exists.</b> The self-update ends with a detached helper that replaces the running
/// binary and starts it again. The launcher cannot observe whether that actually worked — it has
/// already exited. If the replacement fails (Defender holding the file, a locked or read-only install
/// directory, the helper killed mid-way, an ACL on Program Files), the OLD binary comes back up, reads
/// the same manifest, finds the same newer version, and downloads it again. Forever. That is a second,
/// entirely separate endless loop from the version-comparison bug of 2026-08-01 (see
/// <see cref="UpdateService.RunningVersion"/>) — fixing the comparison does not touch it, and it was
/// found by an external review the same day.</para>
///
/// <para><b>What it does.</b> The launcher writes down what it is about to attempt. When it comes back
/// up it compares: if the running version reached the target, the note is cleared. If not, the attempt
/// is counted. After <see cref="MaxAttempts"/> failed tries at the SAME version, auto-apply steps aside
/// and the passive hint takes over — the player is told to download it manually instead of watching the
/// launcher restart itself all evening. A newer target resets the count, so a genuinely broken release
/// never permanently disables updating.</para>
/// </summary>
public interface IUpdateAttemptLedger
{
    /// <summary>True when this version has already failed to install <see cref="MaxAttempts"/> times and
    /// must not be auto-applied again.</summary>
    bool IsExhausted(Version target, Version running);

    /// <summary>Record that an auto-apply for <paramref name="target"/> is about to be attempted.</summary>
    void RecordAttempt(Version target);
}

/// <summary>File-backed ledger: one small text file in the launcher's own state directory.</summary>
public sealed class UpdateAttemptLedger : IUpdateAttemptLedger
{
    /// <summary>Three tries, then hands over to the passive hint. Not one: a swap can fail for a reason
    /// that genuinely passes (a virus scanner busy with the file, a reboot pending). Not ten: every
    /// failed try costs the player a full launcher download and a restart they did not ask for.</summary>
    public const int MaxAttempts = 3;

    private const string FileName = "update-attempts.txt";

    private readonly string _path;
    private readonly Serilog.ILogger _log;

    public UpdateAttemptLedger(IAppPaths paths, Serilog.ILogger log)
    {
        _path = Path.Combine(paths.StateDir, FileName);
        _log = log;
    }

    public bool IsExhausted(Version target, Version running)
    {
        var (noted, count) = Read();
        if (noted is null)
            return false;

        // The update landed: we are running at or past what we last tried to install. Clear the note,
        // otherwise an old count would later be charged against an unrelated version.
        if (UpdateService.CompareVersions(running, noted) >= 0)
        {
            Clear();
            return false;
        }

        // A different target than the one that kept failing — the server published something new, which
        // is exactly the case where the player deserves a fresh set of attempts.
        if (UpdateService.CompareVersions(target, noted) != 0)
            return false;

        if (count < MaxAttempts)
            return false;

        _log.Warning(
            "Launcher-Update auf {Target} ist {Count}x nicht angekommen — Auto-Update ausgesetzt, " +
            "der Hinweis auf den manuellen Download übernimmt. Häufigste Ursache: der Dateitausch wird " +
            "blockiert (Virenscanner, schreibgeschütztes Installationsverzeichnis)",
            target, count);
        return true;
    }

    public void RecordAttempt(Version target)
    {
        var (noted, count) = Read();
        var next = noted is not null && UpdateService.CompareVersions(noted, target) == 0 ? count + 1 : 1;

        // Never write a number Read() would reject as implausible. Today IsExhausted stops the caller
        // before the count could exceed the ceiling, so this cannot trigger — but if that ordering ever
        // changes, an out-of-range value would be discarded on the next start and hand out a FRESH
        // budget, quietly turning the brake off. Clamping here means the brake fails stuck, not open.
        next = Math.Min(next, MaxAttempts);
        try
        {
            File.WriteAllText(_path, $"{target}\n{next}\n");
        }
        catch (Exception ex)
        {
            // Deliberately not fatal. On Windows the state directory sits next to the executable, so a
            // directory we cannot write to is a directory the update download cannot land in either —
            // the update fails on its own a moment later. Refusing to update because the BOOKKEEPING
            // failed would turn a note-taking problem into a stuck launcher.
            _log.Warning(ex, "Update-Versuch konnte nicht notiert werden ({Path})", _path);
        }
    }

    private (Version? Target, int Count) Read()
    {
        try
        {
            if (!File.Exists(_path))
                return (null, 0);

            var lines = File.ReadAllLines(_path);
            if (lines.Length < 2 || !Version.TryParse(lines[0].Trim(), out var target)
                                 || !int.TryParse(lines[1].Trim(), out var count)
                                 // Parseable is not the same as plausible. A file containing
                                 // "-2147483648" parses fine and would then be incremented for two
                                 // billion launches before it ever reached the ceiling — the endless
                                 // loop restored through the very mechanism meant to stop it. Only a
                                 // count this class could itself have written is accepted. (Found by
                                 // Codex in the ship review of 1.6.1, before release.)
                                 || count < 1 || count > MaxAttempts)
            {
                // Unreadable OR implausible note = no note. Never let a corrupt file decide that
                // updating is over, and never let it decide that the ceiling is unreachable either.
                Clear();
                return (null, 0);
            }
            return (target, count);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Update-Versuchsnotiz nicht lesbar ({Path})", _path);
            return (null, 0);
        }
    }

    private void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) { _log.Debug(ex, "Update-Versuchsnotiz nicht löschbar ({Path})", _path); }
    }
}
