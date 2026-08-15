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

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly int _attempts;

    public DownloadService(HttpClient http, Serilog.ILogger log)
        : this(http, log, null, NetworkAttempts) { }

    /// <summary>Test seam for the recovery schedule. A test must be able to prove that the launcher
    /// retries and how often WITHOUT waiting out the real backoff — sixty-seven seconds of sleeping
    /// per case would push the suite into the territory where people stop running it.</summary>
    internal DownloadService(HttpClient http, Serilog.ILogger log,
        Func<TimeSpan, CancellationToken, Task>? delay, int attempts)
    {
        _http = http;
        _log = log;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
        _attempts = attempts;
    }

    /// <summary>
    /// Sidecar next to a <c>.part</c> naming where the partial bytes came from. Without it a resume
    /// is a guess: a <c>.part</c> left over from a different build (same destination file name) would be
    /// spliced onto the new download and only surface as a checksum failure after several GB.
    /// </summary>
    internal static string PartOwnerPath(string destPath) => destPath + ".part.src";

    /// <summary>
    /// What the sidecar remembers about a partial download. The URL alone is not enough, and that is
    /// the whole reason this type exists (owner question, 2026-08-05):
    ///
    /// <para><b>Ein Spieler pausiert einen 5-GB-Download und setzt ihn am naechsten Tag fort. In der
    /// Zwischenzeit haben wir das Paket unter DERSELBEN Adresse ersetzt.</b> Bis hierher pruefte der
    /// Wiederaufnahme-Weg nur die Adresse, fand sie gleich, haengte neue Bytes an alte und lieferte
    /// ein Archiv, das es so nie gegeben hat. Auffallen konnte das erst am Ende, nach mehreren
    /// Gigabyte, als Pruefsummenfehler - und der sieht aus wie ein kaputter Download, nicht wie ein
    /// ersetztes Paket. Der Spieler laedt dann alles noch einmal und landet beim selben Ergebnis.</para>
    ///
    /// <para><b>Und der umgekehrte Fall:</b> das Paket ist in der Zwischenzeit VERSCHWUNDEN. Dann
    /// antwortet der Server 404, und ohne eigene Behandlung bleibt eine <c>.part</c> liegen, die nie
    /// wieder zu etwas fuehrt.</para>
    ///
    /// <para>Drei Dinge werden deshalb festgehalten und beim Fortsetzen geprueft: die Adresse (wie
    /// bisher), die HTTP-Identitaet der Datei (<c>ETag</c>, sonst <c>Last-Modified</c>) und die
    /// Gesamtgroesse. Die Identitaet geht als <c>If-Range</c> mit - der Server antwortet dann nur mit
    /// 206, wenn sich nichts geaendert hat, und sonst mit 200 und der ganzen Datei. Das ist genau der
    /// Fall, fuer den dieser Header erfunden wurde.</para>
    /// </summary>
    internal sealed record PartClaim(string Url, string? ETag, string? LastModified, long Total)
    {
        /// <summary>Etwas, womit sich <c>If-Range</c> stellen laesst. Null heisst: der Server hat uns
        /// keine Identitaet gegeben, und die Groesse muss allein tragen.</summary>
        public string? Validator => !string.IsNullOrWhiteSpace(ETag) ? ETag
            : !string.IsNullOrWhiteSpace(LastModified) ? LastModified : null;

        public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this);

        /// <summary>
        /// Liest den Merkzettel. Vertraegt ausdruecklich auch die ALTE Form - dort stand nur die nackte
        /// Adresse -, weil sonst jeder pausierte Download beim naechsten Launcher-Update stillschweigend
        /// von vorn begonnen haette: mehrere Gigabyte, weil sich ein Dateiformat geaendert hat.
        /// </summary>
        public static PartClaim? FromText(string? text)
        {
            var raw = (text ?? "").Trim();
            if (raw.Length == 0) return null;
            if (!raw.StartsWith('{')) return new PartClaim(raw, null, null, -1);
            try { return System.Text.Json.JsonSerializer.Deserialize<PartClaim>(raw); }
            catch (System.Text.Json.JsonException) { return null; }
        }
    }

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
    private long ResumableBytes(string url, string partPath, string ownerPath, out PartClaim? claim)
    {
        claim = null;
        if (!File.Exists(partPath)) return 0;

        string? text = null;
        try { if (File.Exists(ownerPath)) text = File.ReadAllText(ownerPath); }
        catch (Exception ex) { _log.Warning(ex, "Could not read partial-download marker {Path}", ownerPath); }

        var read = PartClaim.FromText(text);
        if (read is null || !string.Equals(read.Url, url, StringComparison.Ordinal))
        {
            DiscardPart(partPath, ownerPath,
                read is null ? "no marker, source unknown" : "marker names a different source");
            return 0;
        }

        try
        {
            var length = new FileInfo(partPath).Length;

            // Die gemerkte Gesamtgroesse gegen das halten, was schon auf der Platte liegt. Mehr Bytes
            // als die Datei gross war heisst: die Datei dort drueben ist eine andere geworden, und
            // zwar eine kleinere. Weiterschreiben ergaebe ein Archiv, das es nie gab.
            if (read.Total > 0 && length > read.Total)
            {
                DiscardPart(partPath, ownerPath,
                    $"the partial ({length} bytes) is larger than the file it came from ({read.Total})");
                return 0;
            }

            claim = read;
            return length;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not measure partial download {Path}", partPath);
            DiscardPart(partPath, ownerPath, "length unreadable");
            return 0;
        }
    }

    private void WritePartOwner(string ownerPath, PartClaim claim)
    {
        try { File.WriteAllText(ownerPath, claim.ToJson()); }
        catch (Exception ex) { _log.Warning(ex, "Could not write partial-download marker {Path}", ownerPath); }
    }

    /// <summary>Was der Server ueber diese Datei sagt: Identitaet und Gesamtgroesse. Die Gesamtgroesse
    /// kommt bei einer Teilantwort aus <c>Content-Range</c> (dort steht sie hinter dem Schraegstrich),
    /// sonst aus <c>Content-Length</c>.</summary>
    private static PartClaim ClaimFrom(string url, HttpResponseMessage response, long existing)
    {
        var etag = response.Headers.ETag?.ToString();
        var modified = response.Content.Headers.LastModified?.ToString("R");

        long total = -1;
        var range = response.Content.Headers.ContentRange;
        if (range?.Length is long l && l > 0) total = l;
        else if (response.Content.Headers.ContentLength is long cl && cl > 0) total = existing + cl;

        return new PartClaim(url, etag, modified, total);
    }

    /// <summary>How often a lost connection is picked back up before the player is asked to do
    /// anything. Six attempts across the schedule below cover roughly two and a half minutes of
    /// outage — a reconnecting router, a train tunnel, a WLAN that changes access point.</summary>
    internal const int NetworkAttempts = 6;

    /// <summary>Rising, then flat. Doubling forever would make the launcher look dead on a long
    /// outage; thirty seconds is short enough that a player who is watching sees it retry, and long
    /// enough not to hammer a server that is having a bad minute.</summary>
    internal static TimeSpan BackoffFor(int attempt) => attempt switch
    {
        0 => TimeSpan.FromSeconds(2),
        1 => TimeSpan.FromSeconds(5),
        2 => TimeSpan.FromSeconds(10),
        3 => TimeSpan.FromSeconds(20),
        _ => TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// The download, including picking itself back up after the connection drops.
    ///
    /// <para><b>Why this exists.</b> Every piece was already here — the <c>.part</c> survives, the
    /// Range header resumes, the failure is classified as a network fault. What was missing is that
    /// nobody acted on it: a dropped connection put the launcher into an error state and waited for
    /// the player to press the button again. On a multi-gigabyte client over a home line that is not
    /// an edge case, it is the normal case, and the player who walked away comes back to a stopped
    /// download rather than a finished one.</para>
    ///
    /// <para><b>Only network faults are retried.</b> A cancel is the player, a disk error will not fix
    /// itself by waiting, a hash mismatch means the bytes are wrong and repeating gets the same wrong
    /// bytes. Those return immediately; retrying them would only hide them.</para>
    /// </summary>
    public async Task<DownloadResult> DownloadFileAsync(string url, string destPath,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await TransferAsync(url, destPath, progress, ct).ConfigureAwait(false);

            if (result.Ok || result.Failure != DownloadFailure.Network || ct.IsCancellationRequested)
                return result;

            if (attempt + 1 >= _attempts)
            {
                _log.Warning("Connection lost {Attempts} times in a row — handing back to the player: {Url}",
                    _attempts, url);
                return result;
            }

            var wait = BackoffFor(attempt);
            _log.Information("Connection lost ({Reason}) — retrying in {Wait}s, attempt {Next} of {Of}",
                result.Detail, wait.TotalSeconds, attempt + 2, _attempts);

            // Sagen, dass gewartet wird. Ein stehender Byte-Zaehler sieht aus wie ein Haenger, und wer
            // einen Haenger vermutet, schliesst den Launcher -- also genau dann, wenn die Erholung
            // gegriffen haette.
            progress?.Report(new DownloadProgress
            {
                Waiting = new RetryWait(attempt + 2, _attempts, wait, result.Detail ?? ""),
            });

            try { await _delay(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return DownloadResult.Fail(DownloadFailure.Cancelled); }
        }
    }

    /// <summary>One transfer attempt. Resumes from the <c>.part</c> if there is one.</summary>
    private async Task<DownloadResult> TransferAsync(string url, string destPath,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var partPath = destPath + ".part";
        var ownerPath = PartOwnerPath(destPath);
        try
        {
            // Resume only from bytes we can prove came from THIS url (see ResumableBytes).
            long existing = ResumableBytes(url, partPath, ownerPath, out var claim);
            _log.Information("Download: {Url} → {Path} (resume from {Existing} bytes)", url, destPath, existing);

            // At most two passes: the second one only ever runs after a resume was rejected outright
            // (HTTP 416), and it starts from zero — so this cannot loop.
            for (var attempt = 0; ; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (existing > 0)
                {
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

                    // 🔴 If-Range: der Server liefert die Teilantwort NUR, wenn die Datei noch dieselbe
                    // ist. Hat sie sich geaendert, kommt 200 mit der ganzen Datei statt 206 - und
                    // darauf faellt der Zweig unten von selbst richtig zurueck. Ohne diesen Header
                    // haengt eine Wiederaufnahme neue Bytes an alte, und das faellt erst nach mehreren
                    // Gigabyte als Pruefsummenfehler auf, der wie ein kaputter Download aussieht.
                    //
                    // Direkt in die Kopfzeilen geschrieben statt ueber IfRange: die typisierte
                    // Eigenschaft nimmt entweder ein ETag oder ein Datum, und was der Server uns
                    // gegeben hat, wissen wir hier nur als Zeichenkette.
                    if (claim?.Validator is { Length: > 0 } validator)
                        req.Headers.TryAddWithoutValidation("If-Range", validator);
                }

                using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                // Das Paket ist verschwunden. Ohne eigene Behandlung bliebe eine .part liegen, die nie
                // wieder zu etwas fuehrt, und jeder weitere Versuch faende dieselbe Sackgasse.
                if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
                {
                    DiscardPart(partPath, ownerPath, $"the server no longer offers this file (HTTP {(int)response.StatusCode})");
                    return DownloadResult.Fail(DownloadFailure.ServerError, $"HTTP {(int)response.StatusCode}");
                }

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
                    // Zwei sehr verschiedene Gruende fuer dieselbe Antwort, und der Unterschied gehoert
                    // ins Protokoll: hatten wir eine Identitaet mitgeschickt, dann heisst 200 "die Datei
                    // hat sich geaendert"; hatten wir keine, heisst es nur "dieser Server kann kein
                    // Fortsetzen". Beide Male wird von vorn geladen, aber wer spaeter das Protokoll
                    // liest, sucht sonst am falschen Ende.
                    if (claim?.Validator is { Length: > 0 })
                        _log.Information(
                            "The file changed on the server since the download was paused - starting over (HTTP {Status})",
                            (int)response.StatusCode);
                    else
                        _log.Information("Server does not support resume (HTTP {Status}) — restarting from zero", (int)response.StatusCode);
                    existing = 0;
                }

                // Eine Teilantwort, die aus einer anderen Datei stammt als der, aus der die vorhandenen
                // Bytes kamen. Ein Server ohne If-Range-Unterstuetzung liefert genau das - 206 aus der
                // NEUEN Datei -, und dann waere das Ergebnis ein Archiv aus zwei Fassungen. Die
                // Gesamtgroesse ist das, was hier zu vergleichen bleibt.
                if (resuming && claim!.Total > 0)
                {
                    var now = ClaimFrom(url, response, existing);
                    if (now.Total > 0 && now.Total != claim.Total)
                    {
                        DiscardPart(partPath, ownerPath,
                            $"the file is now {now.Total} bytes where the partial came from {claim.Total}");
                        existing = 0;
                        resuming = false;
                        if (attempt == 0) continue;
                        return DownloadResult.Fail(DownloadFailure.ServerError, "the file changed while paused");
                    }
                }

                if (!response.IsSuccessStatusCode)
                {
                    _log.Warning("Download HTTP {Status}: {Url}", (int)response.StatusCode, url);
                    return DownloadResult.Fail(DownloadFailure.ServerError, $"HTTP {(int)response.StatusCode}");
                }

                // Claim the .part for this url BEFORE the first byte lands, so an interrupted download
                // can prove its own provenance on the next attempt.
                if (!resuming) WritePartOwner(ownerPath, ClaimFrom(url, response, existing));

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

    // Player data the launcher must never overwrite on update/repair (case-insensitive directory
    // names). Matched as a path SEGMENT, not only as a leading prefix: the modern client ships its
    // tree under "World of Warcraft/_classic_era_/", so a leading-prefix test let
    // "World of Warcraft/_classic_era_/WTF/Config.wtf" through — clobbering the player's settings on
    // extract, and making Repair report a permanent defect that forced a full re-download every time.
    // 🔴 "interface/addons/" steht hier bewusst NICHT.
    // Spieler-Addons brauchen den Schutz nicht: Extract fasst ausschliesslich Zip-Eintraege an und
    // Verify prueft ausschliesslich Manifest-Eintraege — was der Spieler selbst installiert hat,
    // kommt in beiden Mengen nicht vor und wird nie angefasst. Wer AddOns/ pauschal schuetzt, sperrt
    // damit nur den EINEN Fall aus, der wirklich drinsteht: das mitgelieferte Server-Addon
    // (JimsPlus). Das wird ausschliesslich ueber das Client-Paket verteilt — es steht in keinem
    // Addon-Katalog —, ein Schutz haette also jedes Addon-Update fuer bestehende Installationen
    // stillgelegt (2026-08-12).
    //
    // Der Segmentvergleich ist bewusst positionsunabhaengig und damit theoretisch breiter als noetig:
    // ein Paket, das irgendwo tief im Datenbaum einen Ordner "screenshots" mitbringt, wuerde
    // mitgeschuetzt und damit ungeprueft bleiben (Einwand der Codex-Zweitinstanz, 2026-08-12).
    // Nachgemessen an allen drei ausgelieferten Datei-Manifesten (macOS/Linux/Windows): die Regel
    // trifft dort ausschliesslich echte Spielerdaten unter _classic_era_/WTF/, kein Fehltreffer.
    // Die Alternative waere, die vollen Pfade hart zu verdrahten - das griffe bei der naechsten
    // Layout-Aenderung nicht mehr, und dann werden Spielerdaten ueberschrieben. Der teurere Fehler
    // ist ungeschuetzte Spielerdaten, nicht eine ungeprueft gebliebene Paketdatei.
    private static readonly string[] PreservePrefixes =
        ["wtf/", "screenshots/"];
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
        return PreservePrefixes.Any(pre => lower.StartsWith(pre, StringComparison.Ordinal)
                                           || lower.Contains("/" + pre, StringComparison.Ordinal))
            || PreserveExactFiles.Any(f => lower.Equals(f, StringComparison.Ordinal)
                                           || lower.EndsWith("/" + f, StringComparison.Ordinal));
    }

    /// <summary>
    /// Dateien, die der Client selbst fortschreibt. Sie stehen im Auslieferungspaket und damit im
    /// Repair-Manifest, koennen aber nach dem Spielen nicht mehr dazu passen. Als Defekt gezaehlt
    /// wuerde jeder Repair in einen Voll-Download laufen.
    ///
    /// <para>Die Liste ist absichtlich KURZ und haelt sich an Gemessenes. Ein erster Entwurf schloss
    /// auch die 32 CASC-Indexdateien unter Data/data/*.idx und .build.info aus, mit der Begruendung,
    /// die Indexgenerationen zaehlten im Dateinamen hoch. Die Gegenprobe an einer nachweislich
    /// bespielten Installation (2026-08-12, Account-Ordner und SavedVariables vorhanden) hat das
    /// widerlegt: 34 der 35 Kandidaten waren byteidentisch zum Paket, allein lru_status wich ab. Aus
    /// einem Dateinamen auf Fluechtigkeit zu schliessen war eine Annahme, keine Beobachtung, und
    /// haette 34 pruefbare Dateien ungeprueft gelassen. Sollte ein Spieler echte Defekte an
    /// .idx-Dateien melden, gehoert das gemessen und dann ergaenzt, nicht vorsorglich geraten.</para>
    ///
    /// <para>Absichtlich getrennt von <see cref="IsPreserved"/>: das ist kein Spieler-Eigentum. Beim
    /// Entpacken DUERFEN diese Dateien ueberschrieben werden; sie taugen nur nicht als
    /// Integritaets-Beweis. Deshalb greift die Regel ausschliesslich beim Verifizieren.</para>
    /// </summary>
    internal static bool IsVolatileRuntimeState(string relPath)
    {
        var lower = relPath.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        var name = lower[(lower.LastIndexOf('/') + 1)..];
        // Der LRU-Cache des CASC-Datenspeichers besteht aus mehreren Dateien: lru_status und
        // lru_shard_N. Ein erster Anlauf listete nur lru_status; der echte Repair-Lauf auf dem Mac
        // des Owners fand danach genau eine beschaedigte Datei, lru_shard_0 - gleiche Groesse wie im
        // Paket, abweichender Inhalt, also nachweislich vom Client fortgeschrieben. Diese eine Datei
        // hat einen 8-GB-Download ausgeloest (2026-08-12). Deshalb das Praefix statt einer Aufzaehlung:
        // weitere Shards kommen mit der Cache-Groesse dazu.
        return name.StartsWith("lru_", StringComparison.Ordinal) || name == "shmem";
    }

    public async Task<bool> ExtractClientAsync(string zipPath, string destDir,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            progress?.Report("Entpacke…");
            Directory.CreateDirectory(destDir);

            // Repair/Update hand us ClientInstalls[build] — the EXE folder, which for the modern
            // client sits two levels below the package root the zip paths are relative to. Extracting
            // there would nest a second "World of Warcraft/<flavor>/" inside the existing one and
            // leave the install doubled. Resolve against what is actually on disk instead; a fresh
            // install has no files to match and correctly keeps destDir (ContentRoot).
            using (var probe = System.IO.Compression.ZipFile.OpenRead(zipPath))
            {
                var resolved = ContentRoot.Resolve(
                    destDir,
                    probe.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).Select(e => e.FullName));
                if (!string.Equals(resolved, destDir, StringComparison.Ordinal))
                {
                    _log.Information("ExtractClient: zip paths resolve against {Root}, not {Dest}", resolved, destDir);
                    destDir = resolved;
                }
            }

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
