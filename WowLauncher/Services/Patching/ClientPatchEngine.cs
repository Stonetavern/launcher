namespace WowLauncher.Services.Patching;

using System.Text.Json;
using WowLauncher.Models;
using WowLauncher.Services.Platform;

/// <summary>
/// Every state <see cref="ClientPatchEngine"/> itself owns (ARCHITEKTUR-v2-patcher.md §4).
///
/// <para><b>Deliberately narrower than the full §4 diagram.</b> <c>FETCH_MANIFEST</c>,
/// <c>VERIFY_SIGNATURE</c>, <c>ADMIT</c> and <c>PREFLIGHT</c> already run upstream of this engine
/// today — <c>ManifestService</c>/<c>ManifestSignatureGate</c>/<c>ManifestReleasePolicy</c> fetch and
/// authenticate the manifest, <c>InstallEnvironmentPreflight</c> runs in <c>PlayViewModel</c> before
/// any bytes move (release 1.8.11). By the time a caller builds a <see cref="ClientPatchEngine"/> it
/// already holds an admitted <see cref="ManifestFile"/> for a preflighted install root — duplicating
/// those stages here would be a second copy of trust logic that already exists once. This engine
/// starts exactly where the diagram's "LOAD_FILES_JSON" box begins.</para>
/// </summary>
public enum PatchState
{
    /// <summary>Nothing has run yet.</summary>
    Idle,
    /// <summary>Fetching + trust-binding <c>files.json</c> via <see cref="IClientFileManifestLoader"/>.</summary>
    LoadFilesJson,
    /// <summary>Deciding the route: <see cref="PatchRoute.FullZip"/>, <see cref="PatchRoute.Delta"/>,
    /// <see cref="PatchRoute.PerFile"/> or <see cref="PatchRoute.UpToDate"/>.</summary>
    Plan,
    /// <summary>No install tree exists yet — today's whole-ZIP download+extract.</summary>
    FullZip,
    /// <summary>Applying a butler <c>.pwr</c> delta from the recorded <c>client-state.json</c> version.</summary>
    Delta,
    /// <summary>Hash-scanning the tree against <c>files.json</c> and re-downloading only what differs.</summary>
    PerFile,
    /// <summary>Full hash-scan confirming the tree now matches <c>files.json</c>.</summary>
    Verify,
    /// <summary>Atomically writing the new <c>client-state.json</c>.</summary>
    WriteState,
    /// <summary>Already at the target version and hash-bound files.json — nothing to do.</summary>
    UpToDate,
    /// <summary>Terminal success.</summary>
    Ready,
    /// <summary>Terminal failure — see <see cref="PatchOutcome.Findings"/>/<see cref="PatchOutcome.ErrorMessage"/>.</summary>
    Error,
    /// <summary>Terminal: PLAN found the client running and refused to touch its files (Hermes Q1).</summary>
    GameRunning,
}

/// <summary>Which path actually moved (or didn't move) bytes — ARCHITEKTUR-v2-patcher.md §4/§8.</summary>
public enum PatchRoute
{
    /// <summary>PLAN hasn't run, or a route was never chosen (engine ended in <see cref="PatchState.GameRunning"/>
    /// before deciding one).</summary>
    None,
    FullZip,
    Delta,
    PerFile,
    UpToDate,
}

/// <summary>One thing the engine noticed and could not — or should not — silently fix on its own,
/// reported instead of acted on (ARCHITEKTUR-v2-patcher.md §3: a foreign file is reported, never
/// deleted).</summary>
public sealed record PatchFinding(string Code, string Message)
{
    public const string GameRunning = "GAME_RUNNING";
    public const string FileLocked = "FILE_LOCKED";
    public const string ForeignFile = "FOREIGN_FILE";
    public const string DeltaFellBack = "DELTA_FELL_BACK";
    public const string FilesJsonUnavailable = "FILES_JSON_UNAVAILABLE";
    public const string FilesJsonUntrusted = "FILES_JSON_UNTRUSTED";
    public const string DiskSpace = "DISK_SPACE";
    public const string VerifyFailed = "VERIFY_FAILED";
    public const string ExtractFailed = "EXTRACT_FAILED";
}

/// <summary>Fired as the engine moves through its states, for the same kind of progress surface
/// <see cref="Models.DownloadProgress"/>/<see cref="VerifyProgress"/> already give the ZIP path.</summary>
public sealed record PatchProgress(
    PatchState State, PatchRoute Route, string? CurrentFile = null,
    long BytesDownloaded = 0, long TotalBytes = 0, string? Detail = null);

/// <summary>What one <see cref="ClientPatchEngine.RunAsync"/> call did.</summary>
public sealed class PatchOutcome
{
    public required PatchState State { get; init; }
    public required PatchRoute Route { get; init; }
    public int FilesChanged { get; init; }
    public long BytesDownloaded { get; init; }
    public List<PatchFinding> Findings { get; init; } = [];
    public string? ErrorMessage { get; init; }

    /// <summary><see cref="State"/> reached a terminal, successful node.</summary>
    public bool Ok => State is PatchState.Ready or PatchState.UpToDate;

    public static PatchOutcome Failed(PatchRoute route, string message, List<PatchFinding>? findings = null) =>
        new() { State = PatchState.Error, Route = route, ErrorMessage = message, Findings = findings ?? [] };
}

/// <summary>The on-disk record at <c>&lt;root&gt;/.stonetavern/client-state.json</c>
/// (ARCHITEKTUR-v2-patcher.md §3).</summary>
public sealed class ClientPatchState
{
    public int Build { get; set; }
    public string? Os { get; set; }
    public string Version { get; set; } = "";
    public string? FilesSha256 { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }
}

/// <summary>
/// Drives one client install tree through the v2 patch state machine (ARCHITEKTUR-v2-patcher.md §4):
/// decide whether the existing install can be brought current with a butler delta, a per-file
/// hash-and-fetch pass, or needs the original whole-ZIP path, then prove the result with a full
/// manifest hash-scan before recording the new state. Never invents a fifth path — every route below
/// calls services this codebase already ships (<see cref="IDownloadService"/>,
/// <see cref="IClientVerifyService"/>, the ZIP extractor) rather than re-implementing them.
/// </summary>
public sealed class ClientPatchEngine
{
    /// <summary>The one legal transition table (Owner-Auftrag: "explizite Übergangstabelle"; a
    /// transition this table does not list throws, closing the class of dead-button bugs the Lumenia
    /// reparieren-Knopf incident came from). <see cref="PatchState.Verify"/> → <see
    /// cref="PatchState.PerFile"/> is allowed by the table for up to <see cref="MaxVerifyRetryRounds"/>
    /// rounds; the round limit is a business rule enforced in <see cref="RunAsync"/>, not a second
    /// graph — the table only says "structurally possible", not "how many times".</summary>
    internal static readonly IReadOnlyDictionary<PatchState, PatchState[]> Transitions =
        new Dictionary<PatchState, PatchState[]>
        {
            [PatchState.Idle] = [PatchState.LoadFilesJson],
            [PatchState.LoadFilesJson] = [PatchState.Plan, PatchState.Error],
            [PatchState.Plan] =
                [PatchState.FullZip, PatchState.Delta, PatchState.PerFile, PatchState.UpToDate,
                 PatchState.GameRunning, PatchState.Error],
            [PatchState.FullZip] = [PatchState.Verify, PatchState.Error],
            [PatchState.Delta] = [PatchState.Verify, PatchState.PerFile, PatchState.Error],
            [PatchState.PerFile] = [PatchState.Verify, PatchState.Error],
            [PatchState.Verify] = [PatchState.WriteState, PatchState.PerFile, PatchState.Error],
            [PatchState.WriteState] = [PatchState.Ready, PatchState.Error],
            [PatchState.UpToDate] = [PatchState.Ready],
            [PatchState.Ready] = [],
            [PatchState.Error] = [],
            [PatchState.GameRunning] = [],
        };

    /// <summary>ARCHITEKTUR-v2-patcher.md §4: "VERIFY ... Abweichung → noch eine PER_FILE-Runde (max.
    /// 2), dann ERROR". Counts rounds AFTER the first PerFile attempt, so at most three PerFile passes
    /// run in total for one <see cref="RunAsync"/> call.</summary>
    internal const int MaxVerifyRetryRounds = 2;

    /// <summary>ARCHITEKTUR-v2-patcher.md §5: three attempts at 2/5/10s for a file a scanner or the
    /// game briefly holds, before it counts as genuinely <see cref="PatchFinding.FileLocked"/>.</summary>
    internal static readonly int[] FileLockRetryDelaysMs = [2000, 5000, 10000];

    /// <summary>ARCHITEKTUR-v2-patcher.md §5: how many PER_FILE downloads run at once.</summary>
    internal const int MaxParallelDownloads = 3;

    private readonly IClientFileManifestLoader _manifestLoader;
    private readonly IDownloadService _download;
    private readonly IClientVerifyService _verify;
    private readonly IButlerSidecar _butler;
    private readonly IGameProcessDetector _gameDetector;
    private readonly Serilog.ILogger _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<string> _currentOs;

    public ClientPatchEngine(
        IClientFileManifestLoader manifestLoader, IDownloadService download, IClientVerifyService verify,
        IButlerSidecar butler, IGameProcessDetector gameDetector, Serilog.ILogger log)
        : this(manifestLoader, download, verify, butler, gameDetector, log, null, null)
    {
    }

    /// <summary>Test seam for the file-lock backoff — a test must prove three retries happen without
    /// sleeping 17 real seconds per case (same reasoning as <c>DownloadService</c>'s delay seam).
    /// <paramref name="currentOs"/> is the second seam this constructor carries: PER_FILE/VERIFY need
    /// to know which OS they are running under to filter <see cref="ClientFileEntry.Os"/> (KONZEPT §13,
    /// the shared one-tree-per-build layout), and a test proving that filter must be able to claim
    /// "linux" without actually running on Linux — the same reasoning that keeps
    /// <see cref="OperatingSystem.IsLinux"/> out of this class entirely.</summary>
    internal ClientPatchEngine(
        IClientFileManifestLoader manifestLoader, IDownloadService download, IClientVerifyService verify,
        IButlerSidecar butler, IGameProcessDetector gameDetector, Serilog.ILogger log,
        Func<TimeSpan, CancellationToken, Task>? delay, Func<string>? currentOs = null)
    {
        _manifestLoader = manifestLoader;
        _download = download;
        _verify = verify;
        _butler = butler;
        _gameDetector = gameDetector;
        _log = log.ForContext<ClientPatchEngine>();
        _currentOs = currentOs ?? DefaultCurrentOs;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
    }

    /// <summary>Validates <paramref name="to"/> against <see cref="Transitions"/> for the state the
    /// engine is currently in, and throws if it is not listed — the one place every state change in
    /// this class passes through.</summary>
    internal static void Transition(PatchState from, PatchState to)
    {
        if (!Transitions.TryGetValue(from, out var allowed) || !allowed.Contains(to))
            throw new InvalidOperationException(
                $"ClientPatchEngine: illegal state transition {from} → {to} (not in the transition table)");
    }

    /// <summary>
    /// Runs one client install tree through PLAN → (route) → VERIFY → WRITE_STATE.
    /// </summary>
    /// <param name="client">The admitted, signature-verified manifest entry for this build/os.</param>
    /// <param name="installRoot">The folder the game executable lives in (today's
    /// <c>ClientInstalls[build]</c> convention) — package-relative paths are resolved from here via
    /// <see cref="ContentRoot"/>, exactly like <see cref="ClientVerifyService"/> already does.</param>
    /// <param name="forcePerFile">Repair: skip Delta even when a matching one exists
    /// (ARCHITEKTUR-v2-patcher.md §4, "REPAIR = PLAN mit erzwungenem PER_FILE").</param>
    public async Task<PatchOutcome> RunAsync(ManifestFile client, string installRoot, bool forcePerFile,
        IProgress<PatchProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        var findings = new List<PatchFinding>();
        var state = PatchState.Idle;
        long bytesDownloaded = 0;
        var filesChanged = 0;

        void Report(PatchState s, PatchRoute route, string? file = null, long b = 0, long t = 0, string? detail = null) =>
            progress?.Report(new PatchProgress(s, route, file, b, t, detail));

        // ── LOAD_FILES_JSON ─────────────────────────────────────────────────────────────────────
        Transition(state, PatchState.LoadFilesJson);
        state = PatchState.LoadFilesJson;
        Report(state, PatchRoute.None, detail: "Loading files.json");

        ClientFileManifest manifest;
        try
        {
            var loaded = await _manifestLoader.LoadAsync(client, ct).ConfigureAwait(false);
            if (loaded is null || loaded.Files.Count == 0)
            {
                findings.Add(new PatchFinding(PatchFinding.FilesJsonUnavailable,
                    $"files.json unavailable or empty for build {client.Build}"));
                Transition(state, PatchState.Error);
                return PatchOutcome.Failed(PatchRoute.None, "files.json unavailable", findings);
            }
            manifest = WithoutProtected(loaded);
        }
        catch (ManifestTrustException ex)
        {
            findings.Add(new PatchFinding(PatchFinding.FilesJsonUntrusted, ex.Message));
            Transition(state, PatchState.Error);
            return PatchOutcome.Failed(PatchRoute.None, ex.Message, findings);
        }

        // ── PLAN ────────────────────────────────────────────────────────────────────────────────
        Transition(state, PatchState.Plan);
        state = PatchState.Plan;
        Report(state, PatchRoute.None, detail: "Planning");

        var root = ContentRoot.Resolve(installRoot, manifest.Files.Select(f => f.Path));

        if (_gameDetector.IsGameRunning(FindLeadFilePath(root, manifest, manifest.Build)))
        {
            findings.Add(new PatchFinding(PatchFinding.GameRunning, "the client is running — refusing to touch its files"));
            Transition(state, PatchState.GameRunning);
            return new PatchOutcome { State = PatchState.GameRunning, Route = PatchRoute.None, Findings = findings };
        }

        var existingState = ReadState(root);
        var hasLeadFile = HasLeadFile(root, manifest, manifest.Build);
        // R2 (KONZEPT §12): RunDeltaAsync only returns ok AFTER butler verify proved the applied tree
        // against the signature. Remember that so VERIFY does not hash the same 8 GB a second time.
        var verifiedByButler = false;

        // Teil A (ARCHITEKTUR-v2-patcher.md §5): a Bestandsinstallation that self-updated through an
        // exe-only (Windows) or AppImage-only (Linux) channel never got tools/butler/ next to the
        // launcher at all. Only worth the network round-trip when everything else about a Delta
        // already lines up — FullZip/PerFile/UpToDate installs never pay for it.
        var deltaCandidate = !forcePerFile && hasLeadFile && existingState is not null
            && client.Deltas.Any(d => string.Equals(d.From, existingState.Version, StringComparison.Ordinal));
        if (deltaCandidate)
        {
            await _butler.EnsureAvailableAsync(ct).ConfigureAwait(false);
        }

        PatchRoute route;
        if (!forcePerFile && hasLeadFile && existingState is not null
            && existingState.Build == client.Build
            && string.Equals(existingState.Version, client.Version, StringComparison.Ordinal)
            && string.Equals(existingState.FilesSha256 ?? "", client.FilesSha256 ?? "", StringComparison.Ordinal))
        {
            route = PatchRoute.UpToDate;
        }
        else if (!hasLeadFile)
        {
            route = PatchRoute.FullZip;
        }
        else if (deltaCandidate && _butler.IsAvailable)
        {
            route = PatchRoute.Delta;
        }
        else
        {
            route = PatchRoute.PerFile;
        }

        _log.Information("patch: PLAN for build {Build} → {Route} (leadFile={Lead}, state={State}, butler={Butler})",
            client.Build, route, hasLeadFile, existingState?.Version ?? "(none)", _butler.IsAvailable);

        if (route == PatchRoute.UpToDate)
        {
            Transition(state, PatchState.UpToDate);
            state = PatchState.UpToDate;
            Transition(state, PatchState.Ready);
            return new PatchOutcome { State = PatchState.Ready, Route = route, Findings = findings };
        }

        // ── ROUTE EXECUTION ─────────────────────────────────────────────────────────────────────
        if (route == PatchRoute.FullZip)
        {
            Transition(state, PatchState.FullZip);
            state = PatchState.FullZip;
            Report(state, route, detail: "Full client download");

            var zipOutcome = await RunFullZipAsync(client, root, manifest.Files.Select(f => f.Path), progress, ct)
                .ConfigureAwait(false);
            if (!zipOutcome.ok)
            {
                findings.Add(new PatchFinding(PatchFinding.ExtractFailed, zipOutcome.detail));
                Transition(state, PatchState.Error);
                return PatchOutcome.Failed(route, zipOutcome.detail, findings);
            }
            bytesDownloaded += zipOutcome.bytes;
            filesChanged = manifest.Files.Count;
        }
        else if (route == PatchRoute.Delta)
        {
            Transition(state, PatchState.Delta);
            state = PatchState.Delta;
            Report(state, route, detail: "Applying delta");

            var deltaResult = await RunDeltaAsync(client, existingState!, root, progress, ct).ConfigureAwait(false);
            bytesDownloaded += deltaResult.bytes;

            if (!deltaResult.ok)
            {
                findings.Add(new PatchFinding(PatchFinding.DeltaFellBack, deltaResult.detail));
                _log.Warning("patch: delta failed ({Reason}) — falling back to PER_FILE", deltaResult.detail);
                Transition(state, PatchState.PerFile);
                state = PatchState.PerFile;
                route = PatchRoute.PerFile;
                Report(state, route, detail: "Per-file fallback after delta failure");
                var fallback = await RunPerFileAsync(client, manifest, root, findings, progress, ct).ConfigureAwait(false);
                bytesDownloaded += fallback.bytes;
                filesChanged += fallback.filesChanged;
            }
            else
            {
                filesChanged = deltaResult.changed; // -1 when the pre-check could not measure it
                verifiedByButler = true;
            }
        }
        else // PerFile
        {
            Transition(state, PatchState.PerFile);
            state = PatchState.PerFile;
            Report(state, route, detail: "Checking files");

            var perFile = await RunPerFileAsync(client, manifest, root, findings, progress, ct).ConfigureAwait(false);
            bytesDownloaded += perFile.bytes;
            filesChanged = perFile.filesChanged;
        }

        // ── VERIFY (with up to MaxVerifyRetryRounds extra PER_FILE rounds) ─────────────────────
        for (var round = 0; ; round++)
        {
            Transition(state, PatchState.Verify);
            state = PatchState.Verify;
            Report(state, route, detail: "Verifying");

            if (verifiedByButler)
            {
                // R2: butler verify already covered EVERY file of the applied tree (it is what made
                // RunDeltaAsync return ok); the S0 lesson is satisfied by that verify. A second hash
                // scan over the same 8 GB buys nothing and costs minutes.
                _log.Information("patch: VERIFY skipped — butler verify covered the whole applied tree (R2)");
                break;
            }

            var verifyProgress = new Progress<VerifyProgress>(p =>
                Report(state, route, p.CurrentPath, p.Checked, p.Total, "Verifying"));
            var report = await _verify.VerifyAsync(root, FilterForCurrentOs(manifest), verifyProgress, ct)
                .ConfigureAwait(false);

            if (report.IsIntact)
                break;

            if (round >= MaxVerifyRetryRounds)
            {
                findings.Add(new PatchFinding(PatchFinding.VerifyFailed,
                    $"{report.Missing.Count} missing, {report.Corrupt.Count} corrupt after {round + 1} PER_FILE rounds"));
                Transition(state, PatchState.Error);
                return PatchOutcome.Failed(route, "verify failed after retries", findings);
            }

            _log.Warning("patch: VERIFY found {Missing} missing / {Corrupt} corrupt — retry round {Round}/{Max}",
                report.Missing.Count, report.Corrupt.Count, round + 1, MaxVerifyRetryRounds);
            Transition(state, PatchState.PerFile);
            state = PatchState.PerFile;
            route = PatchRoute.PerFile;
            var retry = await RunPerFileAsync(client, manifest, root, findings, progress, ct).ConfigureAwait(false);
            bytesDownloaded += retry.bytes;
            filesChanged = filesChanged < 0 ? retry.filesChanged : filesChanged + retry.filesChanged;
        }

        // ── WRITE_STATE ─────────────────────────────────────────────────────────────────────────
        Transition(state, PatchState.WriteState);
        state = PatchState.WriteState;
        Report(state, route, detail: "Writing state");

        try
        {
            WriteState(root, new ClientPatchState
            {
                Build = client.Build ?? 0,
                Os = client.Os,
                Version = client.Version,
                FilesSha256 = client.FilesSha256,
                VerifiedAt = DateTimeOffset.UtcNow,
            });
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "patch: could not write client-state.json under {Root}", root);
            // Not fatal: the tree is correct on disk, only the fast-path bookkeeping is missing. The
            // next run simply falls back to PER_FILE instead of DELTA/UP_TO_DATE.
        }

        Transition(state, PatchState.Ready);
        return new PatchOutcome
        {
            State = PatchState.Ready,
            Route = route,
            FilesChanged = Math.Max(filesChanged, 0),
            BytesDownloaded = bytesDownloaded,
            Findings = findings,
        };
    }

    // ── FULL_ZIP ────────────────────────────────────────────────────────────────────────────────

    private async Task<(bool ok, long bytes, string detail)> RunFullZipAsync(
        ManifestFile client, string root, IEnumerable<string> manifestPaths,
        IProgress<PatchProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(client.Sha256) || string.IsNullOrWhiteSpace(client.Url))
            return (false, 0, "manifest entry has no url/sha256 for the whole-client ZIP");

        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, ".stonetavern", "fullclient.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

        var dlProgress = new Progress<DownloadProgress>(p =>
            progress?.Report(new PatchProgress(PatchState.FullZip, PatchRoute.FullZip,
                zipPath, p.BytesDownloaded, p.TotalBytes, p.Status)));

        var dl = await _download.DownloadFileAsync(client.Url, zipPath, dlProgress, ct).ConfigureAwait(false);
        if (dl.Failure == DownloadFailure.Cancelled) throw new OperationCanceledException(ct);
        if (!dl.Ok)
            return (false, 0, dl.UserMessage);

        if (!await _download.VerifyHashAsync(zipPath, client.Sha256, ct).ConfigureAwait(false))
        {
            TryDelete(zipPath);
            return (false, 0, "downloaded ZIP does not match the manifest hash");
        }

        var wrapper = WrapperToStrip(ZipFileEntries(zipPath), manifestPaths);
        if (wrapper is not null)
            _log.Information("patch: the ZIP wraps the client in {Wrapper} — extracting without it", wrapper);
        var outcome = await _download.ExtractClientWithReasonAsync(zipPath, root, freshInstall: true,
            stripTopFolder: wrapper, ct: ct).ConfigureAwait(false);
        if (!outcome.Ok)
            return (false, client.Size, $"extraction failed ({outcome.Failure})");

        TryDelete(zipPath);
        return (true, client.Size, "ok");
    }

    /// <summary>
    /// The one leading folder every file in the ZIP sits under, when files.json does NOT expect it —
    /// the 1.12.1 packages (one wrapper folder for the website download, root-relative files.json).
    /// Null when there is nothing to strip, including the 1.14.2 trees whose files.json itself starts
    /// with <c>World of Warcraft/</c>: a folder files.json names is content, never a wrapper.
    /// </summary>
    internal static string? WrapperToStrip(IReadOnlyList<string> zipFileEntries, IEnumerable<string> manifestPaths)
    {
        if (zipFileEntries.Count == 0) return null;
        var first = zipFileEntries[0].Replace('\\', '/');
        var slash = first.IndexOf('/');
        if (slash <= 0) return null;
        var top = first[..(slash + 1)];
        if (!zipFileEntries.All(e => e.Replace('\\', '/').StartsWith(top, StringComparison.Ordinal))) return null;
        if (manifestPaths.Any(p => p.Replace('\\', '/').StartsWith(top, StringComparison.Ordinal))) return null;
        return top;
    }

    /// <summary>File entries of the ZIP (directory entries left out). Empty when it cannot be read —
    /// then nothing is stripped and the extract itself reports the broken archive.</summary>
    private static IReadOnlyList<string> ZipFileEntries(string zipPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
            return zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).Select(e => e.FullName).ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // ── DELTA ───────────────────────────────────────────────────────────────────────────────────

    private async Task<(bool ok, long bytes, string detail, int changed)> RunDeltaAsync(
        ManifestFile client, ClientPatchState from, string root,
        IProgress<PatchProgress>? progress, CancellationToken ct)
    {
        var delta = client.Deltas.FirstOrDefault(d => string.Equals(d.From, from.Version, StringComparison.Ordinal));
        if (delta is null)
            return (false, 0, "no delta from the recorded version", -1);

        var cacheDir = Path.Combine(root, ".stonetavern", "cache");
        var stagingDir = Path.Combine(root, ".stonetavern", "staging");
        Directory.CreateDirectory(cacheDir);

        long bytes = 0;
        // Files the delta rewrites = files that differ from the target manifest right before it runs.
        // butler itself reports no trustworthy count, but this pre-check already measures exactly
        // that set; until 2026-09-23 it was thrown away and every Delta patch logged "0 file(s)
        // changed" (E2E Classic 1.5 -> 1.6: 6 files, reported 0). -1 = not measured (no files.json).
        var changed = -1;
        try
        {
            // ── Disk space: sum of manifest files that currently differ, doubled (butler writes the
            // new version alongside the old one before swapping) — ARCHITEKTUR-v2-patcher.md §4.
            var manifest = await _manifestLoader.LoadAsync(client, ct).ConfigureAwait(false);
            if (manifest is not null)
            {
                var preCheck = await _verify.VerifyAsync(root, manifest, null, ct).ConfigureAwait(false);
                var changing = new HashSet<string>(preCheck.Missing, StringComparer.Ordinal);
                foreach (var c in preCheck.Corrupt) changing.Add(c);
                changed = changing.Count;
                var required = manifest.Files.Where(f => changing.Contains(f.Path)).Sum(f => f.Size) * 2;
                var drive = DiskSpace.ForPath(root);
                if (drive is not null && required > 0 && drive.AvailableFreeSpace < required)
                    return (false, 0, $"not enough disk space for the delta (need ~{required / 1_048_576} MB)", -1);
            }

            var pwrPath = Path.Combine(cacheDir, Path.GetFileName(new Uri(delta.Url).LocalPath));
            var sigPath = Path.Combine(cacheDir, Path.GetFileName(new Uri(delta.SigUrl).LocalPath));

            var pwrProgress = new Progress<DownloadProgress>(p =>
                progress?.Report(new PatchProgress(PatchState.Delta, PatchRoute.Delta, pwrPath,
                    p.BytesDownloaded, p.TotalBytes, p.Status)));
            var pwrDl = await _download.DownloadFileAsync(delta.Url, pwrPath, pwrProgress, ct).ConfigureAwait(false);
            if (pwrDl.Failure == DownloadFailure.Cancelled) throw new OperationCanceledException(ct);
            if (!pwrDl.Ok) return (false, bytes, $"delta download failed: {pwrDl.UserMessage}", -1);
            bytes += delta.Size;
            if (!await _download.VerifyHashAsync(pwrPath, delta.Sha256, ct).ConfigureAwait(false))
                return (false, bytes, "delta package hash mismatch", -1);

            var sigDl = await _download.DownloadFileAsync(delta.SigUrl, sigPath, null, ct).ConfigureAwait(false);
            if (sigDl.Failure == DownloadFailure.Cancelled) throw new OperationCanceledException(ct);
            if (!sigDl.Ok) return (false, bytes, $"delta signature download failed: {sigDl.UserMessage}", -1);
            if (!await _download.VerifyHashAsync(sigPath, delta.SigSha256, ct).ConfigureAwait(false))
                return (false, bytes, "delta signature hash mismatch", -1);

            Directory.CreateDirectory(stagingDir);
            var applyProgress = new Progress<string>(msg =>
                progress?.Report(new PatchProgress(PatchState.Delta, PatchRoute.Delta, Detail: msg)));

            var apply = await _butler.ApplyAsync(pwrPath, stagingDir, root, applyProgress, ct).ConfigureAwait(false);
            if (!apply.Ok)
                return (false, bytes, $"butler apply failed: {apply.Detail}", -1);

            // 🔴 S0 lesson, non-negotiable: apply Exit 0 is not proof. Always verify.
            var verify = await _butler.VerifyAsync(sigPath, root, ct).ConfigureAwait(false);
            if (!verify.Ok)
                return (false, bytes, $"butler verify failed after apply: {verify.Detail}", -1);

            return (true, bytes, "ok", changed);
        }
        finally
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); }
            catch (Exception ex) { _log.Debug(ex, "patch: could not clean staging dir {Dir}", stagingDir); }
        }
    }

    /// <summary>The host OS in the same three spellings <see cref="ClientFileEntry.Os"/> and
    /// <see cref="Models.ManifestFile.Os"/> use ("windows"/"linux"/"macos"), or "" for anything else.
    /// Kept OUT of the OS-filtering call sites directly (see the internal constructor's
    /// <c>currentOs</c> seam) so PER_FILE/VERIFY's actual behaviour — which files it looks at — is a
    /// pure function of an injected string, not of which machine happens to run the test.</summary>
    internal static string DefaultCurrentOs() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsLinux() ? "linux" :
        OperatingSystem.IsMacOS() ? "macos" : "";

    /// <summary>KONZEPT §13: one shared tree for all OS, with per-file <see cref="ClientFileEntry.Os"/>
    /// as the only place a build still distinguishes them. PER_FILE and VERIFY must only ever look at,
    /// download, or complain about files that apply to THIS OS — a Windows install must never fetch or
    /// report on <c>Hermes/bin/JimsProxy-linux-x64</c>. Files without an <c>os</c> tag (the ~97% that
    /// are byte-identical everywhere) always pass through unfiltered.
    ///
    /// <para>Returns the SAME instance, not a copy, when the host OS is unrecognised or nothing in the
    /// manifest declares <c>os</c> at all — an old files.json without the field must behave exactly as
    /// it did before this existed, and an unrecognised host must not narrow a still-unauthenticated
    /// notion of "its own" files out of the check.</para></summary>
    private ClientFileManifest FilterForCurrentOs(ClientFileManifest manifest)
    {
        var os = _currentOs();
        if (string.IsNullOrEmpty(os)) return manifest;
        if (!manifest.Files.Any(f => f.Os is { Count: > 0 })) return manifest;

        return new ClientFileManifest
        {
            Build = manifest.Build,
            Version = manifest.Version,
            Protected = manifest.Protected,
            Files = manifest.Files.Where(f => f.AppliesTo(os)).ToList(),
        };
    }

    /// <summary>
    /// R3 (KONZEPT §12): entries the manifest itself declares protected are removed before the engine
    /// looks at the tree at all. The build tool already keeps them out of files.json; this is the
    /// second lock, so a files.json that DOES list one (generator bug, hand-edited document) can never
    /// make PER_FILE fetch it or VERIFY overwrite the player's own file. Returns the same instance when
    /// nothing is filtered, so an old manifest without protected paths costs no allocation.
    /// </summary>
    private static ClientFileManifest WithoutProtected(ClientFileManifest manifest)
    {
        if (manifest.Protected.Count == 0) return manifest;
        var files = manifest.Files.Where(f => !IsProtectedPath(f.Path, manifest.Protected)).ToList();
        if (files.Count == manifest.Files.Count) return manifest;

        return new ClientFileManifest
        {
            Build = manifest.Build,
            Version = manifest.Version,
            Protected = manifest.Protected,
            Files = files,
        };
    }

    /// <summary>Whether a wire path is covered by the manifest's protected list: a bare prefix covers
    /// itself and everything under it; an entry with a wildcard is a glob over the whole path (the lru
    /// cache entries of KONZEPT §13 — the CASC storage id in the middle differs per install). The same
    /// rule the release tool applies (deploy/client-release.py is_protected), so "protected" means one
    /// thing on both sides of the wire.</summary>
    internal static bool IsProtectedPath(string wirePath, IReadOnlyList<string> protectedEntries)
    {
        foreach (var entry in protectedEntries)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var normalized = entry.Replace('\\', '/').TrimEnd('/');
            if (normalized.Length == 0) continue;

            if (normalized.Contains('*') || normalized.Contains('?'))
            {
                if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(normalized, wirePath, ignoreCase: true))
                    return true;
                continue;
            }
            if (wirePath.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                || wirePath.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ── PER_FILE ────────────────────────────────────────────────────────────────────────────────

    private async Task<(long bytes, int filesChanged)> RunPerFileAsync(
        ManifestFile client, ClientFileManifest manifest, string root, List<PatchFinding> findings,
        IProgress<PatchProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(client.FilesBase))
        {
            findings.Add(new PatchFinding(PatchFinding.VerifyFailed, "no files_base published — cannot fetch single files"));
            return (0, 0);
        }

        var verifyProgress = new Progress<VerifyProgress>(p =>
            progress?.Report(new PatchProgress(PatchState.PerFile, PatchRoute.PerFile, p.CurrentPath, p.Checked, p.Total)));
        var report = await _verify.VerifyAsync(root, FilterForCurrentOs(manifest), verifyProgress, ct)
            .ConfigureAwait(false);

        // The FULL (unfiltered) manifest here, not the OS-filtered one: a Windows proxy sitting on a
        // Linux install is not a foreign file (KONZEPT §13, "Fremde Dateien anderer OS ... sind kein
        // Befund") - it is a legitimate part of the shared tree that this OS simply does not run.
        ReportForeignFiles(root, manifest, findings);

        var toFetch = report.Missing.Concat(report.Corrupt).Distinct(StringComparer.Ordinal).ToList();
        if (toFetch.Count == 0)
            return (0, 0);

        var byPath = manifest.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var baseUri = new Uri(client.FilesBase, UriKind.Absolute);

        long totalBytes = 0;
        var changed = 0;
        var gate = new SemaphoreSlim(MaxParallelDownloads);
        var tasks = toFetch.Select(async wirePath =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!byPath.TryGetValue(wirePath, out var entry)) return;
                var url = BuildFileUrl(baseUri, wirePath);
                var localPath = Path.Combine(root, ManifestPath.ToLocal(wirePath));
                // R1: the wire path policy ran at load; this is the filesystem half of it — a symlink
                // already on disk must not redirect the write outside the install root.
                if (!ClientFilePathPolicy.IsInsideRoot(root, localPath, out var escape))
                {
                    lock (findings)
                        findings.Add(new PatchFinding(PatchFinding.VerifyFailed,
                            $"refusing to write {wirePath}: {escape}"));
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

                var fileProgress = new Progress<DownloadProgress>(p =>
                    progress?.Report(new PatchProgress(PatchState.PerFile, PatchRoute.PerFile, wirePath,
                        p.BytesDownloaded, p.TotalBytes, p.Status)));

                // The replacement is a fresh 0644 file; without this JimsProxy lost its execute bit and
                // Play failed with "Permission denied" (E2E 2026-09-24, Modern Linux 1.4.4).
                var previousMode = WowLauncher.Services.Platform.UnixExecBit.ModeOf(localPath);
                var ok = await DownloadOneWithLockRetryAsync(url, localPath, entry.Sha256, fileProgress, ct)
                    .ConfigureAwait(false);
                if (ok)
                {
                    if (WowLauncher.Services.Platform.UnixExecBit.Restore(localPath, previousMode))
                        _log.Information("patch: {Path} is executable again", wirePath);
                    Interlocked.Add(ref totalBytes, entry.Size);
                    Interlocked.Increment(ref changed);
                }
                else
                {
                    lock (findings)
                        findings.Add(new PatchFinding(PatchFinding.FileLocked, wirePath));
                }
            }
            finally { gate.Release(); }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return (totalBytes, changed);
    }

    /// <summary>One PER_FILE download, retried on a disk-write failure the way §5 asks (2/5/10s) —
    /// the shape a Windows sharing violation from an antivirus or the game itself takes. Network
    /// failures are already retried inside <see cref="IDownloadService.DownloadFileAsync"/>; this
    /// layer only covers the write-side failure that service does not distinguish from a genuine
    /// disk error (<see cref="DownloadFailure.DiskIo"/> covers both — <c>DownloadResult</c> does not
    /// carry the Win32 error code needed to tell them apart, so every DiskIo failure here gets the
    /// same short retry rather than none at all).</summary>
    private async Task<bool> DownloadOneWithLockRetryAsync(string url, string localPath, string expectedSha256,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await _download.DownloadFileAsync(url, localPath, progress, ct).ConfigureAwait(false);
            if (result.Failure == DownloadFailure.Cancelled) throw new OperationCanceledException(ct);
            if (result.Ok && await _download.VerifyHashAsync(localPath, expectedSha256, ct).ConfigureAwait(false))
                return true;

            if (result.Failure != DownloadFailure.DiskIo || attempt >= FileLockRetryDelaysMs.Length)
                return false;

            _log.Warning("patch: {Path} held or write-protected — retry {Attempt}/{Max}",
                localPath, attempt + 1, FileLockRetryDelaysMs.Length);
            await _delay(TimeSpan.FromMilliseconds(FileLockRetryDelaysMs[attempt]), ct).ConfigureAwait(false);
        }
    }

    private static string BuildFileUrl(Uri baseUri, string wirePath)
    {
        var encoded = string.Join('/', wirePath.Split('/').Select(Uri.EscapeDataString));
        return new Uri(baseUri, encoded).ToString();
    }

    /// <summary>ARCHITEKTUR-v2-patcher.md §3: a file on disk that is neither in <c>files.json</c> nor
    /// under a protected prefix is reported, never deleted (Enhanced leftovers, misplaced addons).
    /// Bounded to files actually under <paramref name="root"/> — walking the whole tree once per
    /// PER_FILE pass, same cost class as the hash-scan <see cref="IClientVerifyService"/> already
    /// does over the same tree.
    ///
    /// <para>🔴 <b>Production run 2026-09-24 (serial 34).</b> The GE-Proton prefix lives INSIDE the
    /// install (<c>proton-compat/&lt;GE&gt;/pfx</c>, <see cref="GeProtonEnvironment.Build"/>), and Wine
    /// puts <c>dosdevices/z:</c> there as a symlink to <c>/</c>. The old walk followed it into the
    /// whole file system, hit <c>/root</c> with "Permission denied", and the exception (thrown lazily
    /// inside the loop, outside the old try) failed the entire patch: every Linux player who had
    /// started the modern client through GE-Proton once got "unpacking failed" instead of the 1.4.4
    /// patch and could not play. Now: symlinks are never followed, unreadable folders are skipped,
    /// the launcher's own prefix folder is not part of the client, and a report can never fail a
    /// patch - it is information, not a precondition.</para></summary>
    private void ReportForeignFiles(string root, ClientFileManifest manifest, List<PatchFinding> findings)
    {
        if (!Directory.Exists(root)) return;
        var known = new HashSet<string>(
            manifest.Files.Select(f => ManifestPath.ToLocal(f.Path)),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        try
        {
            foreach (var full in Directory.EnumerateFiles(root, "*", ForeignScanOptions))
            {
                var rel = Path.GetRelativePath(root, full);
                if (IsLauncherOwned(rel)) continue;
                if (known.Contains(rel)) continue;
                if (IsProtectedPath(rel.Replace(Path.DirectorySeparatorChar, '/'), manifest.Protected)) continue;

                findings.Add(new PatchFinding(PatchFinding.ForeignFile, rel));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.Warning(ex, "patch: could not finish the foreign file report under {Root} - the patch itself is not affected", root);
        }
    }

    /// <summary>Never follow a symlink (Wine's <c>dosdevices/z:</c> points at <c>/</c>), skip what
    /// cannot be read instead of throwing.</summary>
    private static readonly EnumerationOptions ForeignScanOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Folders the launcher itself writes into the install: its patch state and the
    /// GE-Proton prefix (<see cref="GeProtonEnvironment.Build"/>). Neither is part of the client.</summary>
    internal static bool IsLauncherOwned(string relativePath)
    {
        var rel = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        return rel.StartsWith(".stonetavern", StringComparison.Ordinal)
            || rel.StartsWith(GeProtonEnvironment.CompatFolderName + "/", StringComparison.Ordinal);
    }

    // ── PLAN helpers ────────────────────────────────────────────────────────────────────────────

    /// <summary>The client executable's manifest-declared path, matched against the exe name THE
    /// ENTRY'S BUILD ships (<see cref="ClientVersion.All"/>): 1.12.1 is <c>WoW.exe</c>, 1.14.2 is
    /// <c>WowClassic.exe</c>. The build is the fact that decides the name, and the manifest already
    /// carries it.
    ///
    /// <para>🔴 The name used to be hardcoded to "WoW.exe"/"Wow.exe" on the theory that the manifest
    /// IS the "Bestand" description. But the 1.14.2 client has no `WoW.exe` at all — measured
    /// 2026-09-20 on the unified tree, a complete Linux v1.4.3 install reported `leadFile=False`,
    /// PLAN chose FULL_ZIP, and the whole patch engine was bypassed for every modern player.</para>
    /// </summary>
    private static string? FindLeadFilePath(string root, ClientFileManifest manifest, int? build)
    {
        var names = build is int b && ClientVersion.ByBuild(b) is { } version
            ? new[] { version.ExeName }
            : ClientVersion.ExeNames;
        foreach (var entry in manifest.Files)
        {
            var leaf = entry.Path[(entry.Path.LastIndexOf('/') + 1)..];
            if (!names.Any(n => leaf.Equals(n, StringComparison.OrdinalIgnoreCase))) continue;

            var local = Path.Combine(root, ManifestPath.ToLocal(entry.Path));
            if (File.Exists(local)) return local;
        }
        return null;
    }

    private static bool HasLeadFile(string root, ClientFileManifest manifest, int? build) =>
        FindLeadFilePath(root, manifest, build) is not null;

    // ── client-state.json ──────────────────────────────────────────────────────────────────────

    internal static string StatePath(string root) => Path.Combine(root, ".stonetavern", "client-state.json");

    internal static ClientPatchState? ReadState(string root)
    {
        var path = StatePath(root);
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ClientPatchState>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception)
        {
            // An unreadable/corrupt state file is treated exactly like "no state" — the engine falls
            // back to PER_FILE, which is always correct, just not the fastest path.
            return null;
        }
    }

    internal static void WriteState(string root, ClientPatchState state)
    {
        var path = StatePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state));
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* regenerable */ }
    }
}
