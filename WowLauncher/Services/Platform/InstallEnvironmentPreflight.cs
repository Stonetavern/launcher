namespace WowLauncher.Services.Platform;

using WowLauncher.Localization;

/// <summary>Which host this process runs on, as the preflight rules need to branch on it. A small,
/// self-contained enum rather than <c>OSPlatform</c>/<c>OperatingSystem.Is*()</c> directly, so
/// <see cref="InstallEnvironmentPreflight.Evaluate"/> can be driven by a constructed
/// <see cref="InstallEnvironmentFacts"/> in a unit test without touching the real OS at all.</summary>
public enum InstallHostOs
{
    Windows,
    MacOs,
    Linux,

    /// <summary>Anything else. No rule below fires for it beyond the OS-agnostic ones
    /// (<see cref="InstallEnvironmentPreflight"/> PATH_TOO_LONG, PATH_NOT_WRITABLE, GAME_RUNNING).</summary>
    Other,
}

/// <summary>How serious a <see cref="PreflightFinding"/> is. <c>Block</c> stops the operation the
/// preflight guards (no bytes move); <c>Warn</c> lets it proceed with a note the player can act on.</summary>
public enum PreflightSeverity
{
    Warn,
    Block,
}

/// <summary>One preflight result. <see cref="MessageKey"/> is a <see cref="Loc"/> catalog key —
/// resolved through <see cref="InstallEnvironmentPreflight.DisplayText"/>, never assembled ad hoc at
/// the call site, so every surface that shows a finding shows the exact same, already-translated
/// sentence. <see cref="Args"/> exists for a future rule that needs to interpolate a number or path
/// into its sentence; none of the rules below use it today.</summary>
public sealed record PreflightFinding(
    PreflightSeverity Severity, string Code, string MessageKey, IReadOnlyList<object?> Args)
{
    public PreflightFinding(PreflightSeverity severity, string code, string messageKey)
        : this(severity, code, messageKey, Array.Empty<object?>())
    {
    }
}

/// <summary>
/// Everything <see cref="InstallEnvironmentPreflight.Evaluate"/> needs to decide whether an install,
/// download, repair or self-update swap is about to walk into one of the traps release 1.8.11 exists
/// to catch. A plain data object on purpose — the OS reads that fill it in
/// (<see cref="InstallEnvironmentProbes"/>) are a separate, thin, untested-by-design layer, so the
/// decision logic here can be tested with nothing but constructed facts.
/// </summary>
/// <param name="Os">The host this process runs on.</param>
/// <param name="LauncherBaseDir">Where the running launcher binary/bundle lives
/// (<c>AppContext.BaseDirectory</c> or the macOS bundle's <c>Contents/MacOS</c>).</param>
/// <param name="TargetInstallDir">The folder about to be written to — the client install directory
/// for a download/repair, or the launcher's own directory for a self-update swap.</param>
/// <param name="IsElevated">True when this process runs with administrator rights (Windows only —
/// always false elsewhere, elevation is not a concept the other rules read).</param>
/// <param name="EnvironmentVars">The handful of environment variables the rules below consult
/// (<c>ProgramFiles</c>, <c>ProgramFiles(x86)</c>, <c>windir</c>, <c>OneDrive</c>,
/// <c>OneDriveConsumer</c>, <c>OneDriveCommercial</c>, <c>USERPROFILE</c>, <c>APPIMAGE</c>), looked up
/// case-insensitively — callers should hand in a dictionary built with
/// <see cref="StringComparer.OrdinalIgnoreCase"/> (<see cref="InstallEnvironmentProbes.CollectEnvironmentVars"/>
/// already does).</param>
/// <param name="HasQuarantineAttr">macOS: true when the launcher bundle still carries
/// <c>com.apple.quarantine</c>.</param>
/// <param name="WriteProbeSucceeded">True when a real create-plus-delete probe against
/// <paramref name="TargetInstallDir"/> succeeded.</param>
/// <param name="LongestRelativePathInManifest">The longest relative file path (in characters) the
/// client tree will contain, from the loaded file manifest, or a conservative reserve when none was
/// loaded yet (<see cref="InstallEnvironmentPreflight.DefaultLongestRelativePathReserve"/>).</param>
/// <param name="GameProcessRunning">True when a client process is currently running.</param>
public sealed record InstallEnvironmentFacts(
    InstallHostOs Os,
    string LauncherBaseDir,
    string TargetInstallDir,
    bool IsElevated,
    IReadOnlyDictionary<string, string> EnvironmentVars,
    bool HasQuarantineAttr,
    bool WriteProbeSucceeded,
    int LongestRelativePathInManifest,
    bool GameProcessRunning);

/// <summary>
/// Release 1.8.11 — "Stolperfallen-Preflight". Catches the install/update traps that used to surface
/// as a failed download, a silent self-update loop, or a repair that could never succeed, and turns
/// each one into a plain-English message BEFORE any byte moves. Pure and OS-free by construction: give
/// it an <see cref="InstallEnvironmentFacts"/>, get back every applicable <see cref="PreflightFinding"/>
/// — the OS is read once, by <see cref="InstallEnvironmentProbes"/>, and never again in here.
/// </summary>
public static class InstallEnvironmentPreflight
{
    /// <summary>WoW 1.12 is a 32-bit legacy client with no long-path support. 200 leaves headroom below
    /// the Windows 260-character MAX_PATH so the reserve (see below) does not itself eat the margin.</summary>
    public const int MaxEffectiveInstallPath = 200;

    /// <summary>Stand-in for <see cref="InstallEnvironmentFacts.LongestRelativePathInManifest"/> when no
    /// file manifest has been loaded yet — a plain client download never runs the per-file manifest
    /// fetch (that is Repair's optional <c>files_url</c>), so this is the number PLAY's own preflight
    /// call reaches for. Picked as a round, comfortably-conservative figure above the longest path
    /// segments actually seen in a 1.12.1/1.14.2 client tree (Interface\AddOns\... entries included).</summary>
    public const int DefaultLongestRelativePathReserve = 120;

    /// <summary>Evaluate every rule against <paramref name="facts"/>. Order is the order the table in
    /// the release ticket lists them in; callers that show only the first Block/Warn get a stable,
    /// predictable pick.</summary>
    public static IReadOnlyList<PreflightFinding> Evaluate(InstallEnvironmentFacts facts)
    {
        var findings = new List<PreflightFinding>();

        if (facts.Os == InstallHostOs.Windows && facts.IsElevated)
            findings.Add(new PreflightFinding(PreflightSeverity.Warn, "ELEVATED", "Preflight_Elevated"));

        if (IsUnderSystemRoot(facts))
            findings.Add(new PreflightFinding(PreflightSeverity.Block, "PATH_SYSTEM", "Preflight_PathSystem"));

        if (IsSyncedPath(facts.TargetInstallDir, facts.EnvironmentVars))
            findings.Add(new PreflightFinding(PreflightSeverity.Warn, "PATH_SYNCED", "Preflight_PathSynced"));

        if (IsProtectedFolder(facts))
            findings.Add(new PreflightFinding(PreflightSeverity.Warn, "PATH_PROTECTED", "Preflight_PathProtected"));

        if (IsPathTooLong(facts))
            findings.Add(new PreflightFinding(PreflightSeverity.Block, "PATH_TOO_LONG", "Preflight_PathTooLong"));

        if (!facts.WriteProbeSucceeded)
            findings.Add(new PreflightFinding(PreflightSeverity.Block, "PATH_NOT_WRITABLE", "Preflight_PathNotWritable"));

        if (facts.GameProcessRunning)
            findings.Add(new PreflightFinding(PreflightSeverity.Block, "GAME_RUNNING", "Preflight_GameRunning"));

        // TRANSLOCATED and QUARANTINED are mutually exclusive by construction (IsQuarantinedOnly
        // excludes the translocated case) — a translocated launcher's quarantine flag is irrelevant,
        // the fix is the same "move it into Applications" step either way, and showing both would just
        // repeat the same instruction twice.
        if (IsTranslocated(facts))
            findings.Add(new PreflightFinding(PreflightSeverity.Block, "TRANSLOCATED", "Preflight_Translocated"));
        else if (IsQuarantinedOnly(facts))
            findings.Add(new PreflightFinding(PreflightSeverity.Warn, "QUARANTINED", "Preflight_Quarantined"));

        if (IsAppImagePrivileged(facts))
            findings.Add(new PreflightFinding(PreflightSeverity.Warn, "APPIMAGE_PRIVILEGED", "Preflight_AppImagePrivileged"));

        return findings;
    }

    /// <summary>The two checks the release ticket wires into the self-update SWAP specifically —
    /// nothing else from <see cref="Evaluate"/> applies there. A self-update swap replaces exactly one
    /// file next to the running exe, not a client tree: PATH_SYSTEM/PATH_SYNCED/PATH_PROTECTED/
    /// PATH_TOO_LONG are about where a multi-gigabyte client install lives, and an existing player may
    /// well already run their launcher from a folder one of those rules would now flag — turning that
    /// into a newly-blocked self-update for installs that shipped fine every day until now is outside
    /// this ticket's scope, so only the two rules the ticket names are evaluated here.</summary>
    public static IReadOnlyList<PreflightFinding> EvaluateSelfUpdateSwap(InstallEnvironmentFacts facts)
    {
        var findings = new List<PreflightFinding>();

        if (IsTranslocated(facts))
            findings.Add(new PreflightFinding(PreflightSeverity.Block, "TRANSLOCATED", "Preflight_Translocated"));

        if (!facts.WriteProbeSucceeded)
            findings.Add(new PreflightFinding(PreflightSeverity.Block, "PATH_NOT_WRITABLE", "Preflight_PathNotWritable"));

        return findings;
    }

    /// <summary>
    /// CLOCK_SKEW is not one of the rules above: it does not read an <see cref="InstallEnvironmentFacts"/>
    /// at all, and it never blocks anything on its own — it is an EXTRA note alongside a rejection
    /// <see cref="ManifestReleasePolicy"/> already made. When a signed manifest was refused because it
    /// looked expired, and that expiry lies more than a week in the past, that combination is exactly
    /// what a badly wrong local clock produces (a dead CMOS battery, a resumed VM, a UTC/local mixup) —
    /// worth telling the player, instead of leaving "no update, ever" unexplained.
    ///
    /// <para><b>What this deliberately does not implement.</b> The rule as specified also has an
    /// alternative trigger, "or the manifest serial date lies in the future". <see cref="ServerManifest"/>
    /// carries an integer <c>Serial</c> and an <c>Expires</c> timestamp — there is no separate
    /// "serial date" field to read that condition from, and inventing one would be a manifest schema
    /// change, which this release explicitly does not make (Grenzen). Left out, not guessed at.</para>
    /// </summary>
    /// <param name="release">The verdict <c>ManifestReleasePolicy.Admit</c> already returned.</param>
    /// <param name="manifestExpires">The raw <c>manifest.Expires</c> string from the same manifest.</param>
    /// <param name="now">Wall-clock time to compare against — the caller's own clock, the very thing
    /// under suspicion, passed in rather than read here so this stays a pure, testable function.</param>
    public static PreflightFinding? CheckClockSkew(
        WowLauncher.Services.ReleaseVerdict release, string? manifestExpires, DateTimeOffset now)
    {
        if (release.Ok) return null;
        // Matches the exact prefix ManifestReleasePolicy.Admit formats its expiry refusal with
        // ("manifest expired at {expires:O} (now {now:O})") — any other refusal reason (bad channel,
        // rollback, missing field) is not a clock symptom and must not trigger this.
        if (!release.Reason.StartsWith("manifest expired at", StringComparison.Ordinal)) return null;
        if (string.IsNullOrWhiteSpace(manifestExpires)) return null;
        if (!DateTimeOffset.TryParse(manifestExpires, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var expires))
            return null;

        return now - expires > TimeSpan.FromDays(7)
            ? new PreflightFinding(PreflightSeverity.Warn, "CLOCK_SKEW", "Preflight_ClockSkew")
            : null;
    }

    /// <summary>The already-translated sentence for a finding. The switch's arms are literal catalog
    /// keys on purpose — <c>LocalizationCatalogTests</c> greps source for exactly this shape
    /// (<c>Loc.T("...")</c>) to prove every catalog entry is reachable from code; a lookup built from
    /// <c>finding.MessageKey</c> alone would be invisible to that scan.</summary>
    public static string DisplayText(PreflightFinding finding) => finding.Code switch
    {
        "ELEVATED" => Loc.T("Preflight_Elevated"),
        "PATH_SYSTEM" => Loc.T("Preflight_PathSystem"),
        "PATH_SYNCED" => Loc.T("Preflight_PathSynced"),
        "PATH_PROTECTED" => Loc.T("Preflight_PathProtected"),
        "PATH_TOO_LONG" => Loc.T("Preflight_PathTooLong"),
        "PATH_NOT_WRITABLE" => Loc.T("Preflight_PathNotWritable"),
        "GAME_RUNNING" => Loc.T("Preflight_GameRunning"),
        "TRANSLOCATED" => Loc.T("Preflight_Translocated"),
        "QUARANTINED" => Loc.T("Preflight_Quarantined"),
        "APPIMAGE_PRIVILEGED" => Loc.T("Preflight_AppImagePrivileged"),
        "CLOCK_SKEW" => Loc.T("Preflight_ClockSkew"),
        _ => finding.MessageKey,
    };

    // ─── Rules ──────────────────────────────────────────────────────────────

    private static bool IsUnderSystemRoot(InstallEnvironmentFacts facts)
    {
        if (string.IsNullOrWhiteSpace(facts.TargetInstallDir)) return false;

        if (facts.Os == InstallHostOs.Windows)
        {
            foreach (var key in new[] { "ProgramFiles", "ProgramFiles(x86)", "windir" })
                if (facts.EnvironmentVars.TryGetValue(key, out var root)
                    && IsUnderPath(facts.TargetInstallDir, root))
                    return true;
            return false;
        }

        if (facts.Os is InstallHostOs.MacOs or InstallHostOs.Linux)
        {
            foreach (var root in new[] { "/usr", "/opt", "/Applications" })
                if (IsUnderPath(facts.TargetInstallDir, root))
                    return true;
        }

        return false;
    }

    internal static bool IsSyncedPath(string targetInstallDir, IReadOnlyDictionary<string, string> env)
    {
        if (string.IsNullOrWhiteSpace(targetInstallDir)) return false;
        var normalized = targetInstallDir.Replace('\\', '/');

        if (normalized.Contains("/OneDrive", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var key in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (env.TryGetValue(key, out var root) && IsUnderPath(targetInstallDir, root))
                return true;

        return normalized.Contains("/Library/Mobile Documents", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/Dropbox", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/Google Drive", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProtectedFolder(InstallEnvironmentFacts facts)
    {
        if (facts.Os != InstallHostOs.Windows) return false;
        if (!facts.EnvironmentVars.TryGetValue("USERPROFILE", out var profile) || string.IsNullOrWhiteSpace(profile))
            return false;

        foreach (var leaf in new[] { "Documents", "Desktop", "Pictures", "Videos", "Music" })
        {
            var candidate = profile.TrimEnd('\\', '/') + "/" + leaf;
            if (IsUnderPath(facts.TargetInstallDir, candidate))
                return true;
        }
        return false;
    }

    private static bool IsPathTooLong(InstallEnvironmentFacts facts) =>
        !string.IsNullOrEmpty(facts.TargetInstallDir)
        && facts.TargetInstallDir.Length + facts.LongestRelativePathInManifest + 1 > MaxEffectiveInstallPath;

    internal static bool IsTranslocated(InstallEnvironmentFacts facts) =>
        facts.Os == InstallHostOs.MacOs
        && !string.IsNullOrEmpty(facts.LauncherBaseDir)
        && facts.LauncherBaseDir.Replace('\\', '/').Contains("/AppTranslocation/", StringComparison.Ordinal);

    private static bool IsQuarantinedOnly(InstallEnvironmentFacts facts) =>
        facts.Os == InstallHostOs.MacOs && facts.HasQuarantineAttr && !IsTranslocated(facts);

    private static bool IsAppImagePrivileged(InstallEnvironmentFacts facts)
    {
        if (facts.Os != InstallHostOs.Linux) return false;
        if (!facts.EnvironmentVars.TryGetValue("APPIMAGE", out var appImage) || string.IsNullOrWhiteSpace(appImage))
            return false;

        var normalized = appImage.Replace('\\', '/');
        return normalized.StartsWith("/usr", StringComparison.Ordinal)
            || normalized.StartsWith("/opt", StringComparison.Ordinal);
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> itself or lies
    /// inside it, comparing path segments (not a raw substring — <c>/usrx</c> must never match
    /// <c>/usr</c>), case-insensitively (Windows paths, and macOS/Linux home-relative roots are equally
    /// safe to fold).</summary>
    private static bool IsUnderPath(string candidate, string? root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root)) return false;

        var c = candidate.Replace('\\', '/').TrimEnd('/');
        var r = root.Replace('\\', '/').TrimEnd('/');
        if (r.Length == 0) return false;

        return c.Equals(r, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase);
    }
}
