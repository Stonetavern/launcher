using System.IO.Compression;
using WowLauncher.Models;

namespace WowLauncher.Services;

public interface IDownloadService
{
    /// <summary>
    /// Download to destPath, resuming from a partial <c>.part</c> file via HTTP Range when present.
    /// Returns a classified <see cref="DownloadResult"/> (network / server / disk / cancelled) so the
    /// UI can show a self-diagnosable message. A partial <c>.part</c> survives cancel/network failure
    /// so the next attempt resumes instead of restarting the (multi-GB) download.
    /// </summary>
    Task<DownloadResult> DownloadFileAsync(string url, string destPath,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
    Task<bool> ExtractZipAsync(string zipPath, string destDir,
        IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Extract a client ZIP over <paramref name="destDir"/> while NEVER clobbering player data:
    /// any entry under WTF/, Interface/AddOns/, Screenshots/ or a realmlist.wtf that already exists
    /// on disk is skipped (the user's config/addons/saved realm survive an update or repair). Every
    /// other entry is overwritten with the canonical version. A fresh install (empty destDir) extracts
    /// everything — the preserve rule only ever fires on an existing install (Hermes Q1 preserve-list).
    ///
    /// NOTE (Hermes H2): extraction writes in place, so a crash mid-extract leaves a mixed old/new
    /// tree. This is recoverable — the Repair button re-extracts the full canonical client over it.
    /// A future atomic temp-dir swap is tracked once the file-level manifest lands (STATUS).
    /// </summary>
    Task<bool> ExtractClientAsync(string zipPath, string destDir,
        IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Verify a downloaded file's SHA256 against the expected hex digest from the manifest.
    /// Empty/missing expected → HARD failure (returns false): an unverifiable artefact must never
    /// be extracted or executed (Invariante §2 / Codex A2). Returns false on mismatch or read error
    /// too — the caller must NOT extract/use the file.
    /// </summary>
    Task<bool> VerifyHashAsync(string path, string expectedSha256, CancellationToken ct = default);
}

public sealed class DownloadService : IDownloadService
{
    private readonly HttpClient _http;
    private readonly Serilog.ILogger _log;

    public DownloadService(HttpClient http, Serilog.ILogger log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Sidecar next to a <c>.part</c> naming the URL the partial bytes came from. Without it a resume
    /// is a guess: a <c>.part</c> left over from a different build (same destination file name) would be
    /// spliced onto the new download and only surface as a checksum failure after several GB.
    /// </summary>
    internal static string PartOwnerPath(string destPath) => destPath + ".part.src";

    /// <summary>
    /// Delete a partial download and its owner sidecar. Both are scratch files that the next attempt
    /// regenerates, so failures here are logged and swallowed rather than failing the download.
    /// </summary>
    private void DiscardPart(string partPath, string ownerPath, string why)
    {
        try { if (File.Exists(partPath)) File.Delete(partPath); }
        catch (Exception ex) { _log.Warning(ex, "Could not delete stale partial download {Path}", partPath); }
        try { if (File.Exists(ownerPath)) File.Delete(ownerPath); }
        catch (Exception ex) { _log.Warning(ex, "Could not delete partial-download marker {Path}", ownerPath); }
        _log.Information("Discarded partial download {Path}: {Why}", partPath, why);
    }

    /// <summary>
    /// How many bytes of <paramref name="partPath"/> may be resumed for <paramref name="url"/>.
    /// Zero unless the sidecar proves the partial belongs to exactly this URL; anything else is
    /// discarded, because resuming foreign bytes produces a corrupt archive.
    /// </summary>
    private long ResumableBytes(string url, string partPath, string ownerPath)
    {
        if (!File.Exists(partPath)) return 0;

        string? owner = null;
        try { if (File.Exists(ownerPath)) owner = File.ReadAllText(ownerPath).Trim(); }
        catch (Exception ex) { _log.Warning(ex, "Could not read partial-download marker {Path}", ownerPath); }

        if (!string.Equals(owner, url, StringComparison.Ordinal))
        {
            DiscardPart(partPath, ownerPath,
                owner is null ? "no marker, source unknown" : "marker names a different source");
            return 0;
        }

        try { return new FileInfo(partPath).Length; }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not measure partial download {Path}", partPath);
            DiscardPart(partPath, ownerPath, "length unreadable");
            return 0;
        }
    }

    private void WritePartOwner(string ownerPath, string url)
    {
        try { File.WriteAllText(ownerPath, url); }
        catch (Exception ex) { _log.Warning(ex, "Could not write partial-download marker {Path}", ownerPath); }
    }

    public async Task<DownloadResult> DownloadFileAsync(string url, string destPath,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var partPath = destPath + ".part";
        var ownerPath = PartOwnerPath(destPath);
        try
        {
            // Resume only from bytes we can prove came from THIS url (see ResumableBytes).
            long existing = ResumableBytes(url, partPath, ownerPath);
            _log.Information("Download: {Url} → {Path} (resume from {Existing} bytes)", url, destPath, existing);

            // At most two passes: the second one only ever runs after a resume was rejected outright
            // (HTTP 416), and it starts from zero — so this cannot loop.
            for (var attempt = 0; ; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (existing > 0)
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

                using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                // 416 = the offset we asked to resume from is past the end of the current remote file
                // (the server republished a smaller build, or the .part is junk). Without discarding the
                // .part here EVERY future attempt would send the same impossible Range and fail forever,
                // and only deleting the file by hand would fix it.
                if (existing > 0 && response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    DiscardPart(partPath, ownerPath, "server rejected the resume offset (HTTP 416)");
                    existing = 0;
                    if (attempt == 0) continue;
                    return DownloadResult.Fail(DownloadFailure.ServerError, "HTTP 416");
                }

                // 206 = server honored Range → append. Anything else with existing>0 → server can't resume, restart.
                bool resuming = existing > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (existing > 0 && !resuming)
                {
                    _log.Information("Server does not support resume (HTTP {Status}) — restarting from zero", (int)response.StatusCode);
                    existing = 0;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _log.Warning("Download HTTP {Status}: {Url}", (int)response.StatusCode, url);
                    return DownloadResult.Fail(DownloadFailure.ServerError, $"HTTP {(int)response.StatusCode}");
                }

                // Claim the .part for this url BEFORE the first byte lands, so an interrupted download
                // can prove its own provenance on the next attempt.
                if (!resuming) WritePartOwner(ownerPath, url);

                var contentLen = response.Content.Headers.ContentLength ?? -1;
                var total = contentLen > 0 ? existing + contentLen : -1;

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using (var file = new FileStream(partPath, resuming ? FileMode.Append : FileMode.Create,
                           FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    long downloaded = existing;
                    // Zaehler und Nenner muessen dasselbe Fenster messen. Vorher lief der Zaehler ab
                    // Download-START (downloaded - existing), waehrend die Stopwatch bei jedem Report
                    // neu startete: bei 12 % stand dann "632 MB in 0,2 s" = 3070 MB/s auf dem Schirm.
                    // Jetzt merkt sich lastReported den Stand des letzten Ticks, also echtes Delta.
                    long lastReported = existing;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    int read;
                    while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), ct);
                        downloaded += read;
                        if (progress is not null && total > 0 && sw.ElapsedMilliseconds > 200)
                        {
                            var speed = (downloaded - lastReported) / (sw.Elapsed.TotalSeconds + 0.001);
                            progress.Report(new DownloadProgress
                            {
                                BytesDownloaded = downloaded,
                                TotalBytes = total,
                                SpeedBytesPerSecond = speed,
                                Status = $"{downloaded / 1_048_576.0:F1} / {total / 1_048_576.0:F1} MB"
                            });
                            lastReported = downloaded;
                            sw.Restart();
                        }
                    }
                    await file.FlushAsync(ct);
                }

                // Atomic promote .part → final only on full success.
                File.Move(partPath, destPath, overwrite: true);
                try { if (File.Exists(ownerPath)) File.Delete(ownerPath); } catch { /* scratch file */ }
                _log.Information("Download complete: {Path}", destPath);
                return DownloadResult.Success;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.Information("Download cancelled — .part kept for resume: {Url}", url);
            return DownloadResult.Fail(DownloadFailure.Cancelled);
        }
        catch (OperationCanceledException ex)
        {
            // Nobody cancelled us: this is the HttpClient timeout surfacing as TaskCanceledException.
            // Reporting it as "cancelled by the user" hides a real connectivity fault behind a state
            // the UI treats as harmless. It is a network failure and must read like one.
            _log.Warning(ex, "Download timed out — .part kept for resume: {Url}", url);
            return DownloadResult.Fail(DownloadFailure.Network, "timeout");
        }
        catch (HttpRequestException ex)
        {
            _log.Warning(ex, "Download network error, .part kept for resume: {Url}", url);
            return DownloadResult.Fail(DownloadFailure.Network, ex.Message);
        }
        catch (IOException ex)
        {
            _log.Error(ex, "Download disk IO error: {Path}", destPath);
            return DownloadResult.Fail(DownloadFailure.DiskIo, ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Download failed: {Url}", url);
            return DownloadResult.Fail(DownloadFailure.Network, ex.Message);
        }
    }

    public async Task<bool> VerifyHashAsync(string path, string expectedSha256, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            // HARD failure (Codex A2 / Invariante §2): a missing hash means we cannot prove the
            // artefact is authentic, so we refuse it rather than extract an unverifiable file.
            _log.Error("No expected SHA256 present, integrity check failed for {Path} " +
                       "(an empty hash is a hard failure, nothing is extracted)", path);
            return false;
        }
        try
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            await using var fs = File.OpenRead(path);
            var digest = await sha.ComputeHashAsync(fs, ct);
            var actual = Convert.ToHexString(digest).ToLowerInvariant();
            var ok = string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
            if (ok)
                _log.Information("SHA256 verified: {Path}", path);
            else
                _log.Error("SHA256 mismatch for {Path}. Expected {Exp}, got {Act}", path, expectedSha256, actual);
            return ok;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "SHA256 computation failed for {Path}", path);
            return false;
        }
    }

    public async Task<bool> ExtractZipAsync(string zipPath, string destDir,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            progress?.Report("Extracting...");
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, destDir, true);
            progress?.Report("Extraction complete");
            _log.Information("Extracted: {Zip} → {Dir}", zipPath, destDir);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Extraction failed: {Zip}", zipPath);
            return false;
        }
    }

    // Player data the launcher must never overwrite on update/repair (case-insensitive prefixes).
    private static readonly string[] PreservePrefixes =
        ["wtf/", "interface/addons/", "screenshots/"];
    private static readonly string[] PreserveExactFiles =
        ["realmlist.wtf"];

    /// <summary>
    /// Exposed internally so <see cref="ClientVerifyService"/> can apply the exact same preserve rule
    /// when deciding what to check — a file the extractor would never touch must never be reported as
    /// a repair defect either (one rule, one place, not two copies that can drift apart).
    /// </summary>
    internal static bool IsPreserved(string relPath)
    {
        var p = relPath.Replace('\\', '/').TrimStart('/');
        var lower = p.ToLowerInvariant();
        return PreservePrefixes.Any(pre => lower.StartsWith(pre, StringComparison.Ordinal))
            || PreserveExactFiles.Any(f => lower.Equals(f, StringComparison.Ordinal)
                                           || lower.EndsWith("/" + f, StringComparison.Ordinal));
    }

    public async Task<bool> ExtractClientAsync(string zipPath, string destDir,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            progress?.Report("Entpacke…");
            Directory.CreateDirectory(destDir);
            var fullDest = Path.GetFullPath(destDir);
            // Trailing separator so the zip-slip prefix check can't match a sibling dir
            // (e.g. dest "C:\Games\WoW" must NOT accept "..\WoW2\x" → "C:\Games\WoW2\x").
            var destRoot = fullDest.EndsWith(Path.DirectorySeparatorChar)
                ? fullDest : fullDest + Path.DirectorySeparatorChar;
            int extracted = 0, preserved = 0;

            await Task.Run(() =>
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry

                    // Zip-slip guard: resolve target and ensure it stays under destDir.
                    var target = Path.GetFullPath(Path.Combine(fullDest, entry.FullName));
                    if (!target.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Warning("Skipping zip entry outside dest (zip-slip): {Entry}", entry.FullName);
                        continue;
                    }

                    // Preserve existing player data — never clobber config/addons/screenshots/realmlist.
                    if (IsPreserved(entry.FullName) && File.Exists(target))
                    {
                        preserved++;
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                    extracted++;
                }
            }, ct);

            progress?.Report("Entpacken fertig");
            _log.Information("ExtractClient: {Zip} → {Dir} ({Ex} files, {Pr} preserved)",
                zipPath, destDir, extracted, preserved);
            return true;
        }
        catch (OperationCanceledException)
        {
            _log.Information("ExtractClient abgebrochen: {Zip}", zipPath);
            return false;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "ExtractClient failed: {Zip}", zipPath);
            return false;
        }
    }
}
