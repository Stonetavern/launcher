namespace WowLauncher.Services;

using System.Text.Json;
using WowLauncher.Models;
using WowLauncher.Services.Platform;

/// <summary>
/// Fetches the news feed (news.json) from the distribution server, with an offline-first cache.
/// Resolution order: fresh in-memory cache → network fetch → on-disk cache → bundled defaults.
/// The rail is therefore NEVER empty and NEVER blocks the UI (DESIGN-UI-ENTERPRISE §5).
/// </summary>
public interface INewsService
{
    /// <summary>All news items, newest-first. force=true bypasses the in-memory freshness window.</summary>
    Task<IReadOnlyList<NewsItem>> GetNewsAsync(bool force = false, CancellationToken ct = default);
}

public sealed class NewsService : INewsService
{
    private readonly HttpClient _http;
    private readonly IConfigService _config;
    private readonly Serilog.ILogger _log;
    private readonly bool _demo;

    private IReadOnlyList<NewsItem>? _memCache;
    private DateTime _fetchedUtc = DateTime.MinValue;
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);

    // Single-flight gate: the news rail (PlayViewModel) and the PatchNotes section both ask for news
    // at startup, concurrently. Without coalescing they each see the empty cache and each hit the
    // network — two "Fetching news" round-trips per launch (WP0 finding). This shares ONE in-flight
    // fetch: the second concurrent caller awaits the first's task instead of starting its own.
    //
    // F3: the shared task is reset by CHECKING it under the lock, never by a finally/continuation.
    // The old "finally { _inFlight = null }" raced with the "??=" assignment: when FetchCoreAsync
    // completed synchronously (no news URL, disk/default fallback) the reset ran BEFORE the task was
    // stored, so a completed task got stranded in _inFlight forever — force=true and every later
    // caller were then pinned to that one stale result and could never re-fetch. Here we instead
    // start a fresh flight whenever the stored one is null OR already completed (and always on force),
    // which is immune to completion ordering.
    private readonly object _gate = new();
    private Task<IReadOnlyList<NewsItem>>? _inFlight;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly string _diskCachePath;

    public NewsService(HttpClient http, IConfigService config, IAppPaths paths, Serilog.ILogger log,
        bool demo = false)
    {
        _http = http; _config = config; _log = log; _demo = demo;
        // F2: the disk-cache location comes from the injected path resolver, not a hard-coded
        // LocalApplicationData constant. Windows maps back to the historical %LocalAppData%\Stonetavern
        // path (byte-gleich); Linux uses the XDG cache dir (~/.cache/stonetavern-launcher).
        _diskCachePath = paths.NewsCacheFilePath;
    }

    public Task<IReadOnlyList<NewsItem>> GetNewsAsync(bool force = false, CancellationToken ct = default)
    {
        // 1) Fresh in-memory cache → instant (covers both VMs hitting the singleton).
        if (!force && _memCache is not null && DateTime.UtcNow - _fetchedUtc < Freshness)
            return Task.FromResult(_memCache);

        Task<IReadOnlyList<NewsItem>> flight;
        lock (_gate)
        {
            // Re-check under the lock: a racing caller may have just populated the cache.
            if (!force && _memCache is not null && DateTime.UtcNow - _fetchedUtc < Freshness)
                return Task.FromResult(_memCache);

            // Single-flight: join an in-progress fetch, but ONLY if one is genuinely still running.
            // A completed task (incl. one that finished synchronously) must never be handed out again
            // (F3). force=true always starts a fresh flight instead of returning the old result. The
            // shared flight ignores any single caller's CancellationToken (it is shared).
            if (force || _inFlight is null || _inFlight.IsCompleted)
                _inFlight = FetchCoreAsync(CancellationToken.None);
            flight = _inFlight;
        }

        // Honour the caller's token for its OWN wait only: WaitAsync surfaces cancellation to THIS
        // caller without cancelling the shared flight the other (uncancelled) callers still await.
        return ct.CanBeCanceled ? flight.WaitAsync(ct) : flight;
    }

    private async Task<IReadOnlyList<NewsItem>> FetchCoreAsync(CancellationToken ct)
    {
        var cfg = _config.Load();
        var baseUrl = (cfg.PatchServerBaseUrl ?? "").TrimEnd('/');
        var url = string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl}/news.json";

        // 2) Network fetch (offline-safe; never throws out).
        if (url is not null)
        {
            try
            {
                _log.Information("Fetching news from {Url}", url);
                var json = await _http.GetStringAsync(url, ct);
                var items = Parse(json);
                if (items.Count > 0)
                {
                    _memCache = items;
                    _fetchedUtc = DateTime.UtcNow;
                    await TryWriteDiskCacheAsync(json, ct);
                    return items;
                }
                _log.Warning("News feed empty — falling back to cache/defaults");
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "News fetch failed — falling back to cache/defaults");
            }
        }

        // 3) On-disk cache (last good feed survives offline restarts).
        var disk = TryReadDiskCache();
        if (disk is { Count: > 0 })
        {
            _memCache ??= disk;
            return disk;
        }

        // 4) Nothing to show.
        //
        // This used to hand out four bundled "news" items so the rail would never look empty. They
        // were invented — a balance patch, a ban wave with a count, a phase milestone with a number —
        // and every offline player was shown them as fact. The launcher does not lie (BRAND_BIBLE §3),
        // so an empty rail is the correct offline state: it says nothing rather than something false.
        // Demo content stays available for QA under --demo, where nobody mistakes it for the truth.
        var fallback = _demo ? DemoItems() : [];
        _memCache ??= fallback;
        return fallback;
    }

    private static List<NewsItem> Parse(string json)
    {
        // Accept either { "items": [...] } or a bare [...] array.
        var trimmed = json.TrimStart();
        if (trimmed.StartsWith('['))
            return JsonSerializer.Deserialize<List<NewsItem>>(json, JsonOpts) ?? [];
        return JsonSerializer.Deserialize<NewsFeed>(json, JsonOpts)?.Items ?? [];
    }

    private async Task TryWriteDiskCacheAsync(string json, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_diskCachePath)!);
            await File.WriteAllTextAsync(_diskCachePath, json, ct);
        }
        catch (Exception ex) { _log.Debug(ex, "News disk-cache write failed"); }
    }

    private List<NewsItem>? TryReadDiskCache()
    {
        try
        {
            if (!File.Exists(_diskCachePath)) return null;
            return Parse(File.ReadAllText(_diskCachePath));
        }
        catch (Exception ex) { _log.Debug(ex, "News disk-cache read failed"); return null; }
    }

    /// <summary>QA-only sample feed for <c>--demo</c>, so a machine with no realm connection can still
    /// be used to judge the layout. Never returned outside demo mode: these are not real events, and
    /// showing invented news to a player is the one thing the launcher must not do.</summary>
    private static List<NewsItem> DemoItems() =>
    [
        new() { Date = "2026-06-26", Title = "Sample news entry", Category = "news",
                Summary = "Demo content. This is what a news item looks like." },
        new() { Date = "2026-06-25", Title = "Sample patch note", Category = "patch",
                Summary = "Demo content. This is what a patch note looks like." },
        new() { Date = "2026-06-25", Title = "Sample event", Category = "event" },
    ];
}
