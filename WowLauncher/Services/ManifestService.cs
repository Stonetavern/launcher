namespace WowLauncher.Services;

using WowLauncher.Models;

/// <summary>
/// Fetch and parse manifest.json from the distribution server.
/// </summary>
public interface IManifestService
{
    Task<ServerManifest?> FetchAsync(CancellationToken ct = default);

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

    public ManifestService(HttpClient httpClient, IConfigService config, Serilog.ILogger logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Is this a URL <see cref="HttpClient"/> can actually fetch? Only an ABSOLUTE http/https URI is.
    /// A player who types "downloads.example.com/manifest.json" (no scheme) produced a relative Uri,
    /// HttpClient.GetAsync threw InvalidOperationException, the catch-all below returned null, and the
    /// launcher dropped into simple mode — no client management, no download, no update, and not a word
    /// on screen. The form validates with this before it stores anything; this class checks again,
    /// because the same field also arrives from a hand-edited launcher_config.json.
    /// </summary>
    public static bool IsValidManifestUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;
    }

    public async Task<ServerManifest?> FetchAsync(CancellationToken ct = default)
    {
        var config = _config.Load();
        var url = config.ManifestUrl;

        // Simple-mode profile (custom server, no manifest) → nothing to fetch.
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.Information("No manifest URL (simple-mode server) — skipping fetch");
            return null;
        }

        if (!IsValidManifestUrl(url))
        {
            _logger.Error("Manifest URL {Url} is not an absolute http(s) address — nothing to fetch", url);
            return null;
        }

        try
        {
            _logger.Information("Fetching manifest from {Url}", url);
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var manifest = System.Text.Json.JsonSerializer.Deserialize<ServerManifest>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            _logger.Information("Manifest: v{Version} ({Date})",
                manifest?.CurrentVersion, manifest?.BuildDate);
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
