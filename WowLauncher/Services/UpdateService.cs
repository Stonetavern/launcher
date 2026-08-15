namespace WowLauncher.Services;

using System.Diagnostics;
using System.Reflection;
using WowLauncher.Models;
using WowLauncher.Services.Platform;

/// <summary>
/// Launcher self-update: if the manifest advertises a newer launcher build, download +
/// verify it and swap the running .exe via a detached batch script, then exit so the new
/// binary relaunches. The running .exe can't overwrite itself, hence the batch indirection.
/// </summary>
public interface IUpdateService
{
    /// <summary>Raised once this launcher is committed to replacing its own binary, and BEFORE the
    /// download starts — the download is the long part, and by then everything else is decided.
    /// <para>The start screen listens to this so it can say "installing an update" for the whole wait
    /// instead of timing out midway and dropping into a shell that vanishes moments later.</para></summary>
    event EventHandler? LauncherUpdateStarting;

    /// <summary>
    /// Returns true if an update was downloaded, hash-verified, and the swap was launched —
    /// the caller MUST then Environment.Exit(0) so the running binary releases its file lock.
    /// Returns false (and does nothing) when no update applies or verification fails.
    /// <para>Auto-apply runs ONLY on the Windows channel (reads <c>manifest.launcher</c>, byte-for-byte
    /// the shipped behaviour). On every other channel this never downloads or swaps a launcher — Linux
    /// never fetches the Windows build, and its update surfaces through <see cref="CheckForNotice"/>
    /// instead (PLAN §1.5 / WP7: no auto-apply before artefact signing).</para>
    /// <para><b>Signature gate (all channels).</b> Before anything else this obtains a
    /// signature-verified manifest from <see cref="IManifestSignatureGate"/> and every later decision
    /// reads THAT copy. Without a valid signature nothing happens at all: no download, no swap, and
    /// <see cref="CheckForNotice"/> stays silent too. That costs the non-Windows channels one small
    /// HTTP round trip they did not make before — deliberately, because a notify-only channel that
    /// skipped the check would advertise unauthenticated versions to the player.</para>
    /// </summary>
    Task<bool> CheckAndApplyAsync(ServerManifest? manifest, CancellationToken ct = default);

    /// <summary>
    /// Notify-only launcher-update check for platforms that do NOT auto-apply (Linux today). Pure —
    /// no download, no swap, no side effects. Reads THIS platform's launcher field (Linux →
    /// <c>launcher_linux</c>) and returns a notice when it advertises a strictly newer version than the
    /// running assembly; otherwise null. The Windows channel always returns null here (it auto-applies
    /// via <see cref="CheckAndApplyAsync"/> and must not also raise a passive hint). A missing field is
    /// not an error — it simply yields null (no hint).
    /// <para><b>Reads only signature-verified data.</b> The hint is derived from the manifest copy
    /// <see cref="CheckAndApplyAsync"/> proved authentic in this same round; the
    /// <paramref name="manifest"/> argument is kept for API compatibility and for the "no manifest at
    /// all" case, but its launcher fields are never trusted. Called without a preceding successful
    /// verification, this returns null — a player must not be told a new version exists on the word of
    /// an unsigned document.</para>
    /// </summary>
    LauncherUpdateNotice? CheckForNotice(ServerManifest? manifest);
}

/// <summary>Which platform's launcher build this process should look at, and whether it may auto-apply.</summary>
public enum LauncherUpdateChannel
{
    /// <summary>Reads <c>manifest.launcher</c> and auto-applies via the Windows swap strategy (shipped behaviour).</summary>
    Windows,
    /// <summary>Reads <c>manifest.launcher_linux</c>; Check + Notify only (no auto-apply until WP7 signing).</summary>
    Linux,
    /// <summary>Reads <c>manifest.launcher_macos</c> and auto-applies via the macOS bundle swap.</summary>
    MacOs,

    /// <summary>No launcher field wired for this host — never notifies, never applies.</summary>
    None,
}

/// <summary>A passive "a newer launcher exists" hint for the notify-only platforms (no download attached).</summary>
public sealed record LauncherUpdateNotice(string Version, string DownloadPage);

public sealed class UpdateService : IUpdateService
{
    /// <summary>Where a notify-only hint points the player. Not a download URL — the passive path
    /// intentionally sends the user to the signed release page (no unattended fetch, PLAN §1.5).</summary>
    public const string DownloadPage = "downloads.stonetavern.app";

    private readonly IDownloadService _download;
    private readonly Serilog.ILogger _log;
    private readonly IUpdateSwapStrategy _swap;
    private readonly IManifestSignatureGate _signatureGate;
    private readonly LauncherUpdateChannel _channel;
    private readonly IUpdateAttemptLedger? _attempts;
    private readonly IUpdateHealth? _health;

    /// <inheritdoc />
    public event EventHandler? LauncherUpdateStarting;

    /// <summary>Set when an auto-apply was attempted this round and did not go through, so the passive
    /// hint can step in instead of the update failing silently.</summary>
    private bool _autoApplyFailed;
    private readonly Version _current;

    /// <summary>The manifest whose signature was proven in the most recent <see cref="CheckAndApplyAsync"/>,
    /// or null when that check did not happen or did not pass. Every update decision — auto-apply AND
    /// the passive hint — reads this and nothing else, so "unsigned" can only ever mean "silent".</summary>
    private ServerManifest? _verified;

    public UpdateService(IDownloadService download, Serilog.ILogger log, IUpdateSwapStrategy swap,
        IManifestSignatureGate signatureGate, LauncherUpdateChannel channel, Version? currentVersion = null,
        IUpdateAttemptLedger? attempts = null, IUpdateHealth? health = null,
        IUpdateCheckLog? checkLog = null)
    {
        _download = download;
        _log = log;
        _swap = swap;
        // Required, not optional-with-a-default: a defaulted gate would be a fail-open switch that one
        // forgotten constructor call could flip back on without anyone noticing.
        _signatureGate = signatureGate;
        _channel = channel;
        // Optional, and that IS the safe direction here: no ledger means "do not brake", which is the
        // behaviour that shipped. A test that does not care about the brake gets the old semantics; the
        // real app always passes one (see DependencyInjection).
        _attempts = attempts;
        // Same reasoning as the ledger, same safe direction: no health contract means the swap runs
        // without a safety net, which is exactly the behaviour that shipped before this existed.
        _health = health;
        _current = currentVersion ?? RunningVersion(Assembly.GetExecutingAssembly());
        // Optional wie die beiden darueber: ohne Protokoll verhaelt sich das Update exakt wie bisher,
        // es wird nur nichts aufgeschrieben.
        _checkLog = checkLog;
    }

    private readonly IUpdateCheckLog? _checkLog;

    /// <summary>
    /// The version this running build should be compared against the manifest with — the PRODUCT
    /// version (<c>&lt;Version&gt;</c>, surfaced as <see cref="AssemblyInformationalVersionAttribute"/>),
    /// not <see cref="AssemblyName.Version"/>.
    /// <para>🔴 This distinction caused a self-update LOOP in the field (2026-08-01). The csproj pins
    /// <c>AssemblyVersion</c> at 1.1.0.0 and only bumps <c>Version</c>, so every shipped build reports
    /// itself as 1.1.0.0 through <see cref="AssemblyName.Version"/> while the manifest advertises 1.5.1.
    /// The published 1.5.1 AppImage was verified to carry exactly that pair. Result: update found →
    /// downloaded → swapped → the new binary reports 1.1.0.0 again → forever. Comparing the product
    /// version makes "installed" and "advertised" the same scale, which is the only way the comparison
    /// can ever terminate.</para>
    /// <para>InformationalVersion carries a SourceLink suffix (<c>1.5.1+d6c40de…</c>); everything from
    /// the first <c>+</c> or <c>-</c> is build metadata / prerelease tag and is cut before parsing. If it
    /// is missing or unparsable we fall back to <see cref="AssemblyName.Version"/> — a wrong-but-old
    /// number is still better than 0.0, which would make every manifest look like an update.</para>
    /// </summary>
    internal static Version RunningVersion(Assembly asm)
    {
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var core = informational.Split('+', '-')[0].Trim();
            if (Version.TryParse(core, out var product))
                return product;
        }

        return asm.GetName().Version ?? new Version(0, 0);
    }

    /// <summary>
    /// Compares two versions with missing components read as 0, so "1.6.0" and "1.6.0.0" are the SAME
    /// version. <see cref="Version"/> does not do this: it stores an absent component as -1, which makes
    /// <c>1.6.0 &lt; 1.6.0.0</c> — and a manifest written with one more component than the build reports
    /// would look permanently newer, which is the 2026-08-01 update loop with different digits. Returns
    /// &gt;0 when <paramref name="a"/> is newer.
    /// </summary>
    internal static int CompareVersions(Version a, Version b)
    {
        static Version Pad(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
        return Pad(a).CompareTo(Pad(b));
    }

    public LauncherUpdateNotice? CheckForNotice(ServerManifest? manifest)
    {
        // No manifest at all = simple mode; nothing to say. Nothing was verified in that case either,
        // so the guard below would catch it too — this only keeps the two cases distinguishable.
        if (manifest is null)
            return null;

        // Fail-closed: no proven-authentic manifest from this round, no hint. A version number the
        // launcher cannot attribute to the release key is not worth putting in front of a player.
        if (_verified is null)
            return null;

        // The passive hint exists for the case auto-apply cannot cover: no swap strategy on this
        // platform, or a swap that was attempted and failed (read-only directory, a launcher started
        // from a folder it may not write). Where auto-apply worked there is nothing left to hint at,
        // and hinting anyway would tell a player to download what they already have.
        if (_swap.IsSupported && !_autoApplyFailed) return null;

        var info = _channel switch
        {
            LauncherUpdateChannel.Linux => _verified.LauncherLinux,
            LauncherUpdateChannel.Windows => _verified.Launcher,
            LauncherUpdateChannel.MacOs => _verified.LauncherMacos,
            _ => null,
        };
        var available = NewerBuild(info);
        return available is null ? null : new LauncherUpdateNotice(available.ToString(), DownloadPage);
    }

    public async Task<bool> CheckAndApplyAsync(ServerManifest? manifest, CancellationToken ct = default)
    {
        // Drop any previous verdict FIRST. If this round fails anywhere below, the passive hint must
        // fall silent with it rather than keep answering from an older, no-longer-proven manifest.
        _verified = null;
        _autoApplyFailed = false;

        // Simple mode (custom server, no manifest URL): there is no manifest to authenticate and no
        // update to offer. Not a refusal, so no error line.
        if (manifest is null)
            return false;

        // 🔴 The gate that makes the rest of this method safe. Until 2026-07-27 the launcher trusted
        // whatever manifest.json said and checked only the SHA-256 named in that same file — which
        // protects against a corrupted download and against nothing else: whoever can write the
        // manifest writes the hash too. Now the manifest must carry a signature made with a key that
        // exists only offline, and every field read below comes from the bytes that signature covered.
        var signed = await _signatureGate.AcquireVerifiedAsync(ct);
        if (signed is null)
        {
            _log.Error(
                "Manifest ohne gültige Signatur — kein Launcher-Update (kein Download, kein Swap, kein Hinweis). " +
                "Grund steht in der Zeile davor; erwartet wird ein signiertes manifest.json samt manifest.json.sig");
            return false;
        }
        _verified = signed;

        // Auto-apply runs on every channel that has BOTH a manifest entry and a swap strategy —
        // Windows since day one, Linux since the signature gate above exists (that was the condition
        // WP7 named: no unattended install of an artefact whose origin cannot be proven), and macOS
        // since the bundle swap exists. A channel with no manifest entry falls out below and stays
        // silent; a channel with no swap falls back to the passive hint.
        var info = _channel switch
        {
            LauncherUpdateChannel.Windows => signed.Launcher,
            LauncherUpdateChannel.Linux => signed.LauncherLinux,
            LauncherUpdateChannel.MacOs => signed.LauncherMacos,
            _ => null,
        };
        // Was fuer diese Plattform ueberhaupt angeboten wird, festhalten - VOR jeder Entscheidung
        // darueber, ob es neuer ist. Genau der Fall "angeboten wird 1.5.0, ich habe 1.7.4" ist der,
        // den ein Spieler sehen koennen muss, und er endet drei Zeilen weiter unten still mit false.
        // Erst hier, nie im Abrufpfad: bis zum Signatur-Gate oben ist eine Versionsnummer nur eine
        // Behauptung, und eine unbeglaubigte Zahl gehoert nicht vor einen Spieler.
        _checkLog?.RecordOffered(info?.Version);

        if (info is null || string.IsNullOrWhiteSpace(info.Url) || string.IsNullOrWhiteSpace(info.Version))
            return false;

        var current = _current;
        if (!Version.TryParse(info.Version, out var available) || CompareVersions(available, current) <= 0)
            return false;

        // HARD security gate: a launcher update executes code. An unverifiable binary is
        // refused outright — a compromised CDN must not be able to push arbitrary code.
        if (string.IsNullOrWhiteSpace(info.Sha256))
        {
            _log.Error("Launcher update {Ver} has no SHA256, rejected (self-update strictly requires a hash)", available);
            return false;
        }

        // 🔴 Loop brake. The swap happens in a detached helper AFTER this process exits, so nothing here
        // can observe whether it worked. When it does not (Defender holding the file, a read-only
        // install directory, the helper killed), the old binary comes back, reads the same manifest and
        // starts over — download, exit, fail, repeat, with no ceiling. Found by external review on
        // 2026-08-01, the same day the version-comparison loop was fixed; the two are independent, and
        // fixing the comparison does nothing for this one.
        if (_attempts is not null && _attempts.IsExhausted(available, current))
        {
            _autoApplyFailed = true;   // hand over to the passive hint: manual download, not another lap
            return false;
        }

        // 🔴 The other loop brake, and it stops the opposite failure. The ledger above answers "the
        // update never arrived". This one answers "it arrived and crashed": the swap worked, the new
        // build died before it could do anything, and the swap script put the previous one back
        // (IUpdateHealth). Without this check the restored build would find the same newer version
        // here and walk straight back into the same crash — the rollback would have bought nothing.
        if (_health is not null && _health.IsQuarantined(available))
        {
            _autoApplyFailed = true;
            return false;
        }

        _log.Information("Launcher update available: {Cur} -> {New}", current, available);

        // Ask whether this platform can swap at all BEFORE spending a download on it. Fetching first
        // and discovering afterwards that nothing can be applied wastes a player's bandwidth and, on a
        // metered connection, their money — and it writes a file next to their launcher for nothing.
        if (!_swap.IsSupported)
        {
            _log.Information(
                "No self-update swap on this platform — leaving it to the passive hint ({Cur} -> {New})",
                current, available);
            _autoApplyFailed = true;
            return false;
        }

        var appDir = AppContext.BaseDirectory;

        // The path that will be REPLACED. On Linux the running image is an AppImage mounted read-only,
        // so AppContext.BaseDirectory points into the mount, not at the file a player launched —
        // $APPIMAGE is the only honest answer there (the runtime sets it for exactly this purpose).
        var currentExe = CurrentBinaryPath(appDir);

        // Download NEXT TO the file it replaces, never into a temp directory: the swap that follows is
        // a rename, and a rename is only atomic within one filesystem. A /tmp on its own mount would
        // silently turn the swap into a copy that can be interrupted halfway.
        var targetDir = Path.GetDirectoryName(currentExe) ?? appDir;
        var tmpExe = Path.Combine(targetDir, Path.GetFileName(currentExe) + ".download");

        // Announce BEFORE the download, not after. Everything from here on is committed to replacing
        // the binary, and the download is the long part — 14 s for 61 MB in the win11 VM on 2026-08-02.
        // The start screen was told only once the swap had already been handed off, so its twelve-second
        // budget expired first: the shell came up two seconds before the process exited, so the player
        // saw a window appear and vanish, which reads as a crash. That is precisely the impression the
        // start screen exists to prevent, and its ninety-second update grace never got a chance to apply.
        LauncherUpdateStarting?.Invoke(this, EventArgs.Empty);
        // Both failures below hand over to the passive hint, for the same reason a failed SWAP does:
        // auto-apply cannot deliver this update, and a player who is never told is a player who stays
        // on an old build forever. Measured on 2026-08-02 in the win11 VM: a launcher installed under
        // C:\Program Files cannot write next to itself, so the download never lands — it used to skip
        // in silence, with no hint and nothing in any log the player could reach.
        var download = await _download.DownloadFileAsync(info.Url, tmpExe, null, ct);
        if (!download.Ok)
        {
            // A cancel is a launcher shutting down, not a broken update — same distinction the swap
            // path makes below. Everything else means auto-apply cannot deliver, so the hint takes over.
            if (download.Failure != DownloadFailure.Cancelled)
            {
                _log.Warning("Launcher update download failed ({Failure}), the manual download hint " +
                             "takes over", download.Failure);
                _autoApplyFailed = true;
            }
            return false;
        }
        if (!await _download.VerifyHashAsync(tmpExe, info.Sha256, ct))
        {
            _log.Error("Launcher update SHA256 mismatch, discarded (never run an unverified binary)");
            TryDelete(tmpExe);
            _autoApplyFailed = true;
            return false;
        }

        // Detached swap of the locked .exe after we exit, then relaunch (behind IUpdateSwapStrategy;
        // Windows batch is byte-for-byte the shipped one).
        //
        // Codex F4b: NOT wrapped in a catch. Pre-WP1 the batch WRITE sat outside the try and its
        // failure propagated — that behaviour is restored: a staging failure throws out of ApplySwap
        // and out of this method. Only a LAUNCH failure is signalled (return false) → clean up the
        // downloaded binary and fall back to a normal start, exactly as before.
        // Note the attempt BEFORE handing over. Everything after this line happens in a process that is
        // about to exit; a note written afterwards would never be written at all in exactly the case it
        // exists for. Counting one attempt too many (swap succeeds, note stays) is harmless — the next
        // start sees the target version reached and clears it.
        _attempts?.RecordAttempt(available);

        // Announce what has to come up, for the same reason and at the same moment: after this line
        // this process is on its way out, and a sentinel written later would never be written at all
        // in exactly the case it exists for. The new build clears it once it is genuinely up; if it
        // never does and its process is gone, the swap script puts the previous build back.
        _health?.ExpectVersion(available);

        // 🔴 Reverses an earlier decision (the "Codex F4b" note on IUpdateSwapStrategy): staging the
        // helper script used to throw on purpose, to preserve pre-WP1 semantics. That was weighed
        // against the wrong thing. This method is reached from startup through a fire-and-forget call
        // (App.axaml.cs → PlayViewModel), so an exception here does not surface as an error — it
        // abandons initialisation and leaves the player looking at a launcher that never finishes
        // starting, with no hint and no way to update manually. A failed update must cost the update,
        // never the launcher. Same verdict as a failed launch below: clean up, fall back to the passive
        // hint, start normally. (Found by Codex in the ship review of 1.6.1, before release.)
        bool swapStarted;
        try
        {
            swapStarted = _swap.ApplySwap(tmpExe, currentExe, appDir, available);
        }
        // A cancel is not a failed update — it is a launcher being shut down, and it must keep
        // travelling up. Swallowing it here would dress a normal exit as a broken update.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "Launcher-Update konnte nicht vorbereitet werden — Start läuft normal weiter, " +
                           "der Hinweis auf den manuellen Download übernimmt");
            TryDelete(tmpExe);
            _autoApplyFailed = true;
            return false;
        }

        if (!swapStarted)
        {
            TryDelete(tmpExe);
            _autoApplyFailed = true;   // the passive hint takes over: a failed swap must not be silent
            return false;
        }
        return true;
    }

    /// <summary>The file this process actually runs from, as a path that can be replaced. Under an
    /// AppImage the process image lives on a read-only mount and only <c>$APPIMAGE</c> names the file
    /// the player double-clicked; everywhere else the process' own main module is the answer.
    ///
    /// <para>Shared with <see cref="UpdateHealth"/> rather than duplicated: the health contract has to
    /// put its sentinel in the SAME directory the swap script works in. Resolving it a second way was
    /// exactly the bug — <c>Environment.ProcessPath</c> under an AppImage points into the read-only
    /// squashfs mount, so the sentinel could not be written at all and the quarantine note could never
    /// be found. Both failures are silent by design (a missing sentinel only loses the safety net), so
    /// the contract would have looked present and done nothing.</para></summary>
    internal static string CurrentBinaryPath(string appDir)
    {
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrWhiteSpace(appImage) && File.Exists(appImage)) return appImage;

        return Process.GetCurrentProcess().MainModule?.FileName
               ?? Path.Combine(appDir, OperatingSystem.IsWindows() ? "WowLauncher.exe" : "WowLauncher");
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) { _log.Debug(ex, "Cleanup {Path} failed", path); }
    }

    /// <summary>The parsed version of <paramref name="info"/> when it advertises a real build (url+version
    /// present) that is strictly newer than the running assembly; otherwise null. A missing/empty entry
    /// is not an error — it is simply "no newer build".</summary>
    private Version? NewerBuild(ManifestFile? info)
    {
        if (info is null || string.IsNullOrWhiteSpace(info.Url) || string.IsNullOrWhiteSpace(info.Version))
            return null;

        // Contract (deploy/MANIFEST-SCHEMA.md): strictly numeric System.Version ("1.2.3" / "1.2.3.4").
        // A leading "v" is tolerated defensively; anything else is a manifest authoring error and must
        // be LOUD (warning), never silently treated as "no update" — silent drops hid real updates.
        var raw = info.Version.Trim();
        if (raw.StartsWith('v') || raw.StartsWith('V'))
            raw = raw[1..];
        if (!Version.TryParse(raw, out var available))
        {
            _log.Warning(
                "Launcher-Versionsfeld im Manifest nicht parsebar: '{Raw}' — erwartet numerisches System.Version-Format (MANIFEST-SCHEMA.md)",
                info.Version);
            return null;
        }
        return CompareVersions(available, _current) > 0 ? available : null;
    }
}
