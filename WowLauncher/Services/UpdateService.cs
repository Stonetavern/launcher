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
    /// <summary>
    /// Returns true if an update was downloaded, hash-verified, and the swap was launched —
    /// the caller MUST then Environment.Exit(0) so the running binary releases its file lock.
    /// Returns false (and does nothing) when no update applies or verification fails.
    /// <para>Auto-apply runs ONLY on the Windows channel (reads <c>manifest.launcher</c>, byte-for-byte
    /// the shipped behaviour). On every other channel this returns false immediately WITHOUT any
    /// network I/O — Linux never downloads the Windows launcher, and its update surfaces through
    /// <see cref="CheckForNotice"/> instead (PLAN §1.5 / WP7: no auto-apply before artefact signing).</para>
    /// </summary>
    Task<bool> CheckAndApplyAsync(ServerManifest? manifest, CancellationToken ct = default);

    /// <summary>
    /// Notify-only launcher-update check for platforms that do NOT auto-apply (Linux today). Pure —
    /// no download, no swap, no side effects. Reads THIS platform's launcher field (Linux →
    /// <c>launcher_linux</c>) and returns a notice when it advertises a strictly newer version than the
    /// running assembly; otherwise null. The Windows channel always returns null here (it auto-applies
    /// via <see cref="CheckAndApplyAsync"/> and must not also raise a passive hint). A missing field is
    /// not an error — it simply yields null (no hint).
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
    /// <summary>No launcher field wired yet (macOS / other host) — never notifies, never applies.</summary>
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
    private readonly LauncherUpdateChannel _channel;
    private readonly Version _current;

    public UpdateService(IDownloadService download, Serilog.ILogger log, IUpdateSwapStrategy swap,
        LauncherUpdateChannel channel, Version? currentVersion = null)
    {
        _download = download;
        _log = log;
        _swap = swap;
        _channel = channel;
        _current = currentVersion ?? Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
    }

    public LauncherUpdateNotice? CheckForNotice(ServerManifest? manifest)
    {
        // Only the notify-only platform (Linux) surfaces a passive hint. Windows auto-applies in
        // CheckAndApplyAsync and must not double-report; None (macOS/other) has no field wired.
        var info = _channel switch
        {
            LauncherUpdateChannel.Linux => manifest?.LauncherLinux,
            _ => null,
        };
        var available = NewerBuild(info);
        return available is null ? null : new LauncherUpdateNotice(available.ToString(), DownloadPage);
    }

    public async Task<bool> CheckAndApplyAsync(ServerManifest? manifest, CancellationToken ct = default)
    {
        // Auto-apply is Windows-only. Any other channel does nothing here (no download, no swap) —
        // Linux update is Check + Notify (CheckForNotice); auto-apply arrives with signing in WP7.
        if (_channel != LauncherUpdateChannel.Windows)
            return false;

        var info = manifest?.Launcher;
        if (info is null || string.IsNullOrWhiteSpace(info.Url) || string.IsNullOrWhiteSpace(info.Version))
            return false;

        var current = _current;
        if (!Version.TryParse(info.Version, out var available) || available <= current)
            return false;

        // HARD security gate: a launcher update executes code. An unverifiable binary is
        // refused outright — a compromised CDN must not be able to push arbitrary code.
        if (string.IsNullOrWhiteSpace(info.Sha256))
        {
            _log.Error("Launcher update {Ver} has no SHA256, rejected (self-update strictly requires a hash)", available);
            return false;
        }

        _log.Information("Launcher update available: {Cur} -> {New}", current, available);

        var appDir = AppContext.BaseDirectory;
        var tmpExe = Path.Combine(appDir, "WowLauncher_new.exe.tmp");
        if (!(await _download.DownloadFileAsync(info.Url, tmpExe, null, ct)).Ok)
        {
            _log.Warning("Launcher update download failed, skipping, starting normally");
            return false;
        }
        if (!await _download.VerifyHashAsync(tmpExe, info.Sha256, ct))
        {
            _log.Error("Launcher update SHA256 mismatch, discarded (never run an unverified binary)");
            TryDelete(tmpExe);
            return false;
        }

        // Auto-apply only where a swap strategy exists (Windows today; Linux stays Check+Notify
        // until WP7 delivers signed artefacts). Never run an unsupported/unverified swap.
        if (!_swap.IsSupported)
        {
            _log.Error("Launcher self-update not supported on this platform, skipped (auto-apply follows in WP7)");
            TryDelete(tmpExe);
            return false;
        }

        // Detached swap of the locked .exe after we exit, then relaunch (behind IUpdateSwapStrategy;
        // Windows batch is byte-for-byte the shipped one).
        //
        // Codex F4b: NOT wrapped in a catch. Pre-WP1 the batch WRITE sat outside the try and its
        // failure propagated — that behaviour is restored: a staging failure throws out of ApplySwap
        // and out of this method. Only a LAUNCH failure is signalled (return false) → clean up the
        // downloaded binary and fall back to a normal start, exactly as before.
        var currentExe = Process.GetCurrentProcess().MainModule?.FileName
                         ?? Path.Combine(appDir, "WowLauncher.exe");
        if (!_swap.ApplySwap(tmpExe, currentExe, appDir))
        {
            TryDelete(tmpExe);
            return false;
        }
        return true;
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
        return available > _current ? available : null;
    }
}
