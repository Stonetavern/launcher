namespace WowLauncher.Models;

/// <summary>
/// The PlayViewModel state machine (DESIGN-UI-ENTERPRISE §7).
/// Every state has a designed UI — no default dialog, no stack trace.
/// </summary>
public enum LauncherState
{
    /// <summary>App start: config load, client search, realm/news/manifest in parallel.</summary>
    Initializing,
    /// <summary>A newer launcher build was fetched + verified; the swap-and-relaunch is running.</summary>
    UpdatingLauncher,
    /// <summary>WoW.exe not found → First-Run wizard / download path.</summary>
    NoClient,
    /// <summary>
    /// The active phase needs a different client build than the one last played
    /// (e.g. Vanilla→TBC-Prepatch, 5875→8606). A client for the new era must be
    /// fetched/selected before SPIELEN — distinct from <see cref="NoClient"/> so the
    /// UI can frame it as "a new era has begun", not "you have nothing installed".
    /// </summary>
    EraTransition,
    /// <summary>Client ok and up to date → SPIELEN.</summary>
    Ready,
    /// <summary>Manifest build &gt; local build → AKTUALISIEREN.</summary>
    UpdateAvailable,
    /// <summary>Update running, bytes flowing.</summary>
    Downloading,
    /// <summary>
    /// The player paused an in-flight download. The partial <c>.part</c> file is kept and a resume
    /// picks up from those bytes via HTTP Range (see DownloadService). Distinct from
    /// <see cref="DownloadError"/> so the UI frames it as a deliberate, resumable stop, not a failure.
    /// </summary>
    Paused,
    /// <summary>SHA256 verify / extract phase (indeterminate).</summary>
    Verifying,
    /// <summary>Hash or net error after retries — nothing overwritten.</summary>
    DownloadError,
    /// <summary>SPIELEN clicked: realmlist written, WoW starting, launcher exits.</summary>
    Launching,
    /// <summary>WoW.exe start threw.</summary>
    LaunchFailed,
}

/// <summary>Realm reachability — orthogonal to <see cref="LauncherState"/> (drives the HeroStage PulseDot).</summary>
public enum RealmState
{
    Checking,
    Online,
    Offline,
}
