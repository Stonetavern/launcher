namespace WowLauncher.Services;

using WowLauncher.Models;

/// <summary>
/// Fetch and parse manifest.json from the distribution server.
/// </summary>
public interface IManifestService
{
    Task<ServerManifest?> FetchAsync(CancellationToken ct = default);

    /// <summary>
    /// Das Manifest, gegen das sich der LAUNCHER selbst prüft — immer das von Stonetavern, nie das
    /// des gewählten Realms.
    ///
    /// <para>🔴 <b>Warum das getrennt ist</b> (Befund 2026-08-05, gefunden beim Reparieren eines
    /// eigenen Realms). Bis hierher las das Selbst-Update dasselbe Manifest wie der Client, und das
    /// hängt am gewählten Realm. Zwei Folgen, beide still:</para>
    /// <list type="number">
    /// <item>Wer einen eigenen Realm anlegt und ihn auswählt, bekommt <b>gar keine
    /// Launcher-Updates mehr</b> — kein Fehler, keine Meldung, der Launcher altert einfach. Genau
    /// die Spieler mit einem eigenen Realm sind die, die Fehlerbehebungen am dringendsten
    /// brauchen.</item>
    /// <item>Ein fremder Realm konnte mitreden, welches Binary der Launcher für sich selbst
    /// herunterlädt. Das Signatur-Gate hat das aufgefangen (fremde Signatur ⇒ abgelehnt), also war
    /// es fail-closed — aber eine Tür, die nur zufällt, weil jemand anders davorsteht, sollte gar
    /// nicht erst offen sein. Dieselbe Klasse wie der Token-Leak, der <c>ApiEndpoints</c> auf
    /// Stonetavern gepinnt hat.</item>
    /// </list>
    /// <para>Der CLIENT bleibt realm-gebunden: welches Spielpaket ein Realm ausliefert, ist seine
    /// Sache. Was der Launcher mit sich selbst tut, ist es nicht.</para>
    /// </summary>
    Task<ServerManifest?> FetchLauncherManifestAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetch the OPTIONAL per-file manifest a <see cref="ManifestFile.FilesUrl"/> points at
    /// (<c>deploy/MANIFEST-SCHEMA.md</c> §files_url). Offline-first, same as <see cref="FetchAsync"/>:
    /// any network/parse failure returns null so the caller falls back to the whole-ZIP repair path
    /// rather than surfacing a hard error for what is still an optional feature.
    /// </summary>
    Task<ClientFileManifest?> FetchFileManifestAsync(string url, CancellationToken ct = default);
}

public sealed class ManifestService : IManifestService
{
    private readonly HttpClient _httpClient;
    private readonly IConfigService _config;
    private readonly Serilog.ILogger _logger;

    private readonly IUpdateCheckLog? _checkLog;

    /// <param name="checkLog">Notified ONLY when the launcher manifest was fetched AND parsed, so the
    /// release status can say when the launcher last really reached the update server. Optional: the
    /// existing test constructions pass none, and without it the fetch behaves exactly as before.</param>
    public ManifestService(HttpClient httpClient, IConfigService config, Serilog.ILogger logger,
        IUpdateCheckLog? checkLog = null)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
        _checkLog = checkLog;
    }

    /// <summary>
    /// Is this a URL <see cref="HttpClient"/> can actually fetch? Only an ABSOLUTE https URI is
    /// (plus plain-http loopback, see below).
    /// A player who types "downloads.example.com/manifest.json" (no scheme) produced a relative Uri,
    /// HttpClient.GetAsync threw InvalidOperationException, the catch-all below returned null, and the
    /// launcher dropped into simple mode — no client management, no download, no update, and not a word
    /// on screen. The form validates with this before it stores anything; this class checks again,
    /// because the same field also arrives from a hand-edited launcher_config.json.
    ///
    /// <para><b>Why plain http is refused (2026-07-27).</b> The manifest carries the SHA-256 the
    /// launcher then verifies its downloads against — client packages of several GB and its own
    /// update. Expected hash and payload therefore travel the SAME channel: anyone able to replace
    /// the file over that channel also replaces the hash, and the check passes on the attacker's
    /// bytes. Over https that takes a compromised host; over plain http it takes anyone on the
    /// network path. Until the manifest itself is signed, transport security is the only thing
    /// standing between a coffee-shop network and code execution, so http is refused outright
    /// rather than warned about.</para>
    ///
    /// <para>Loopback stays allowed on purpose: a manifest served from 127.0.0.1 or localhost has no
    /// network path to intercept, and local test servers should not need a certificate.</para>
    /// </summary>
    public static bool IsValidManifestUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }

    public Task<ServerManifest?> FetchAsync(CancellationToken ct = default) =>
        FetchFromAsync(_config.Load().ManifestUrl, "realm", ct);

    /// <summary><see cref="IManifestService.FetchLauncherManifestAsync"/>. Die Adresse ist eine
    /// Konstante im Programm, kein Konfigurationswert: eine handeditierte Datei darf nicht bestimmen,
    /// woher sich der Launcher seine eigene nächste Fassung holt.</summary>
    public Task<ServerManifest?> FetchLauncherManifestAsync(CancellationToken ct = default) =>
        FetchFromAsync(RealmRegistry.StonetavernManifest, "launcher", ct);

    private async Task<ServerManifest?> FetchFromAsync(string? url, string which, CancellationToken ct)
    {

        // Simple-mode profile (custom server, no manifest) → nothing to fetch.
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.Information("No {Which} manifest URL (simple-mode server) — skipping fetch", which);
            return null;
        }

        if (!IsValidManifestUrl(url))
        {
            _logger.Error("Manifest URL {Url} is not an absolute http(s) address — nothing to fetch", url);
            return null;
        }

        try
        {
            _logger.Information("Fetching {Which} manifest from {Url}", which, url);
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var manifest = System.Text.Json.JsonSerializer.Deserialize<ServerManifest>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            _logger.Information("Manifest: v{Version} ({Date})",
                manifest?.CurrentVersion, manifest?.BuildDate);

            // 🔴 Nur hier, und nur wenn wirklich ETWAS ankam. Eine 200-Antwort mit unbrauchbarem
            // Inhalt (eine Anmeldeseite eines Hotel-WLANs, eine abgeschnittene Datei) laesst
            // `manifest` null - und wuerde ohne diese Bedingung als "erfolgreich geprueft"
            // festgehalten. Der Launcher berichtete dann eine frische Pruefung, bei der nie etwas
            // geprueft wurde: genau die plausible Falschaussage, gegen die die Anzeige gebaut ist.
            if (manifest is not null && which == "launcher")
                _checkLog?.RecordReached();

            return manifest;
        }
        catch (HttpRequestException ex)
        {
            _logger.Warning(ex, "Manifest download failed — server may be offline");
            return null; // Offline-first: null = no update available
        }
        // Caller cancel and timeout arrive as the same exception type but mean opposite things: one says
        // "nobody wants this answer any more" and must propagate, the other says "the server is slow"
        // and is offline-first null. Same split as the download and auth paths.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            _logger.Warning("Manifest fetch timed out");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error fetching manifest");
            return null;
        }
    }

    public async Task<ClientFileManifest?> FetchFileManifestAsync(string url, CancellationToken ct = default)
    {
        if (!IsValidManifestUrl(url))
        {
            _logger.Warning("File manifest URL {Url} is not an absolute http(s) address — skipping", url);
            return null;
        }

        try
        {
            _logger.Information("Fetching file manifest from {Url}", url);
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var manifest = await System.Text.Json.JsonSerializer.DeserializeAsync(
                stream, ClientFileManifestJsonContext.Default.ClientFileManifest, ct);

            _logger.Information("File manifest: build {Build}, {Count} files",
                manifest?.Build, manifest?.Files.Count ?? 0);
            return manifest;
        }
        catch (HttpRequestException ex)
        {
            _logger.Warning(ex, "File manifest download failed — falling back to whole-ZIP repair");
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            _logger.Warning("File manifest fetch timed out — falling back to whole-ZIP repair");
            return null;
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.Warning(ex, "File manifest was not valid JSON — falling back to whole-ZIP repair");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error fetching file manifest");
            return null;
        }
    }
}
