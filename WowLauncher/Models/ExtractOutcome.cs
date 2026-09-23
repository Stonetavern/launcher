namespace WowLauncher.Models;

/// <summary>
/// Why an extraction did not finish.
///
/// <para><b>Why this exists.</b> <c>DownloadService.ExtractClientAsync</c> reduced a cancelled run, a
/// full disk, a file the running game holds, a permission problem and a corrupt archive to the same
/// <c>false</c>. That is enough to show an error, and not enough to decide anything: the install
/// attempt budget must charge a broken package but must NOT charge a player who simply needs to close
/// WoW or free some space, because he would then be locked out at the exact moment he fixed the
/// cause. Codex review 2026-09-14.</para>
/// </summary>
public enum ExtractFailure
{
    None = 0,
    /// <summary>The player pressed pause, or the run was abandoned. Nothing is wrong.</summary>
    Cancelled,
    /// <summary>The target filesystem ran out of room mid-write.</summary>
    DiskFull,
    /// <summary>A file could not be replaced because something holds it — the game, a scanner.</summary>
    FileLocked,
    /// <summary>The target is write-protected or not ours to write.</summary>
    AccessDenied,
    /// <summary>The archive itself does not read as a ZIP.</summary>
    BadArchive,
    /// <summary>The target path cannot be used at all: a directory in it does not exist, the name is
    /// longer or stranger than the filesystem accepts. Deterministic — it will not heal by itself.</summary>
    TargetUnusable,
    /// <summary>Anything else, including a double that only reports true/false.</summary>
    Unknown,
}

/// <summary>The result of one extraction, with the reason when it failed.</summary>
public readonly record struct ExtractOutcome(bool Ok, ExtractFailure Failure)
{
    public static ExtractOutcome Success => new(true, ExtractFailure.None);
    public static ExtractOutcome Fail(ExtractFailure failure) => new(false, failure);

    /// <summary>
    /// True when repeating the very same attempt could plausibly succeed once the player changes
    /// something about his machine — space freed, game closed, scanner done, folder unlocked.
    ///
    /// <para>These must never consume the attempt budget. A budget exists to stop the launcher from
    /// fetching a package that is broken no matter how often it is fetched; charging it for a full
    /// disk would turn "free some space and try again" into "the launcher refuses now".</para>
    /// </summary>
    public bool IsEnvironmental => Failure is ExtractFailure.Cancelled
        or ExtractFailure.DiskFull or ExtractFailure.FileLocked or ExtractFailure.AccessDenied;
}
