using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// F3 single-flight proofs for <see cref="NewsService"/>. The bug (fixed here): the old
/// "finally { _inFlight = null }" reset raced the "??=" store — a fetch that completed synchronously
/// (no news URL / disk-or-default fallback) stranded a COMPLETED task in <c>_inFlight</c> forever, so
/// <c>force=true</c> and every later caller were pinned to that one stale result and could never
/// re-fetch, and a caller's <see cref="CancellationToken"/> was ignored. These tests drive an
/// in-process fake handler (no real network) and assert:
/// (a) two concurrent callers share exactly ONE fetch;
/// (b) after a synchronous empty-URL flight the next call starts a NEW fetch — the F3 regression /
///     placebo test (goes red against the old "??=" pattern);
/// (c) force=true after a completed flight starts a NEW fetch (bypasses the freshness window);
/// (d) cancelling one caller leaves the other callers' shared flight unaffected.
/// </summary>
public sealed class NewsServiceTests
{
    // A valid feed with >0 items → the network branch caches it and sets the freshness timestamp.
    private const string NewsJson =
        """[{"date":"2026-07-17","title":"WP3 news","summary":"live","category":"news"}]""";

    // ── Test doubles ──────────────────────────────────────────────────────────────────────────

    /// <summary>Counts requests and lets the test gate/shape each response body.</summary>
    private sealed class FakeHandler(Func<CancellationToken, Task<string>> body) : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<string>> _body = body;
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public FakeHandler(string body) : this(_ => Task.FromResult(body)) { }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var json = await _body(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    /// <summary>Returns a configurable base URL; each Load() advances to the next queued value
    /// (the last value sticks), so a test can go from "no URL" to "real URL" between fetches.</summary>
    private sealed class QueueConfig(params string[] urls) : IConfigService
    {
        private readonly Queue<string> _urls = new Queue<string>(urls);
        private string _current = "";

        public LauncherConfig Load()
        {
            if (_urls.Count > 0) _current = _urls.Dequeue();
            return new LauncherConfig { PatchServerBaseUrl = _current };
        }
        public void Save(LauncherConfig config) { }
        public bool LastSaveSucceeded => true;
    }

    /// <summary>Pins every write path to a throwaway temp dir — the disk cache never touches $HOME.</summary>
    private sealed class TempPaths : IAppPaths, IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "mechagon-news-test-" + Guid.NewGuid().ToString("N"));
        public string ConfigDir => _root;
        public string StateDir => _root;
        public string CacheDir => _root;
        public string LogDir => _root;
        public string ShareDir => _root;
        public string ConfigFilePath => Path.Combine(_root, "launcher_config.json");
        public string NewsCacheFilePath => Path.Combine(_root, "news-cache.json");
        public string ClientInstallDir(int gameBuild) => Path.Combine(_root, $"WoW-Client-{gameBuild}");
        public string ClientDownloadZip(int gameBuild) => Path.Combine(_root, $"WoW-Client-{gameBuild}.zip");
        public void EnsureDirectories() => Directory.CreateDirectory(_root);
        public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best-effort */ } }
    }

    private static NewsService NewService(HttpMessageHandler handler, IConfigService config, IAppPaths paths,
        bool demo = false) =>
        new(new HttpClient(handler), config, paths, new Serilog.LoggerConfiguration().CreateLogger(), demo);

    // ── The offline fallback must not invent news ───────────────────────────────────────────────

    /// <summary>
    /// With no feed URL and no disk cache the launcher used to hand out four bundled "news" items -
    /// a balance patch, a ban wave with a count, a phase milestone with a number - and every offline
    /// player saw them as fact. The launcher does not lie (BRAND_BIBLE §3): the honest offline state
    /// is an empty rail.
    ///
    /// <para>Goes red the moment the fallback returns anything at all, which is exactly the mutation
    /// that reintroduces invented content.</para>
    /// </summary>
    [Fact]
    public async Task WithNoFeedAndNoCache_TheRailStaysEmpty_RatherThanInventingNews()
    {
        var handler = new FakeHandler(NewsJson);   // never reached: no URL configured
        using var paths = new TempPaths();
        var svc = NewService(handler, new QueueConfig(""), paths, demo: false);

        var items = await svc.GetNewsAsync();

        Assert.Empty(items);
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>The QA switch keeps sample entries, so the layout can still be judged on a machine
    /// with no realm connection. Guards the other direction: --demo must not silently go empty too.</summary>
    [Fact]
    public async Task InDemoMode_SampleEntriesAreStillProvided()
    {
        var handler = new FakeHandler(NewsJson);
        using var paths = new TempPaths();
        var svc = NewService(handler, new QueueConfig(""), paths, demo: true);

        var items = await svc.GetNewsAsync();

        Assert.NotEmpty(items);
        // Sample content says it is a sample. Nothing here may read as a real announcement.
        Assert.All(items, i => Assert.Contains("Sample", i.Title, System.StringComparison.Ordinal));
    }

    // ── (a) two concurrent callers → exactly ONE fetch ──────────────────────────────────────────

    [Fact]
    public async Task ConcurrentCallers_ShareASingleFetch()
    {
        // Gate the response so the first fetch is still in flight when the second caller arrives.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(async _ => { await release.Task.ConfigureAwait(false); return NewsJson; });
        using var paths = new TempPaths();
        var svc = NewService(handler, new QueueConfig("https://example.test"), paths);

        var t1 = svc.GetNewsAsync();
        var t2 = svc.GetNewsAsync(); // must JOIN the in-flight fetch, not start a second one

        Assert.Equal(1, handler.Calls);
        release.SetResult();
        var r1 = await t1;
        var r2 = await t2;

        Assert.Equal(1, handler.Calls);
        Assert.Same(r1, r2);          // both callers got the same coalesced result instance
        Assert.NotEmpty(r1);
    }

    // ── (b) synchronous empty-URL flight → next call starts a NEW fetch (F3 regression / placebo) ─

    [Fact]
    public async Task AfterSynchronousEmptyUrlFlight_NextCall_StartsAFreshFetch()
    {
        // First Load() yields NO base URL → FetchCoreAsync completes SYNCHRONOUSLY via the bundled
        // defaults (zero HTTP calls). The old "finally { _inFlight = null }" reset ran before the
        // "??=" store, stranding that completed task in _inFlight — so the second call returned the
        // stale defaults task and never hit the network (handler.Calls stays 0). With the fix the
        // completed flight is not reused: the second call starts a real fetch.
        //
        // PLACEBO: revert GetNewsAsync to the old "_inFlight ??= FetchCoalescedAsync()" (with the
        // finally-reset) and this assertion goes RED — handler.Calls == 0 and the result is still the
        // bundled defaults instead of "WP3 news".
        var handler = new FakeHandler(NewsJson);
        using var paths = new TempPaths();
        var svc = NewService(handler, new QueueConfig("", "https://example.test"), paths);

        var first = await svc.GetNewsAsync();   // empty URL → defaults, 0 HTTP calls
        Assert.Equal(0, handler.Calls);

        var second = await svc.GetNewsAsync();   // MUST start a new fetch (now the URL is real)
        Assert.Equal(1, handler.Calls);
        Assert.Contains(second, i => i.Title == "WP3 news");
        Assert.DoesNotContain(first, i => i.Title == "WP3 news"); // the first was the stale defaults
    }

    // ── (c) force=true after a completed flight → a NEW fetch (bypasses freshness) ──────────────

    [Fact]
    public async Task ForceTrue_AfterCompletedFlight_StartsAFreshFetch()
    {
        var handler = new FakeHandler(NewsJson);
        using var paths = new TempPaths();
        var svc = NewService(handler, new QueueConfig("https://example.test"), paths);

        await svc.GetNewsAsync();               // successful fetch → cached + fresh
        Assert.Equal(1, handler.Calls);

        await svc.GetNewsAsync();               // within freshness → served from cache, no new fetch
        Assert.Equal(1, handler.Calls);

        await svc.GetNewsAsync(force: true);    // force must bypass freshness AND start a new flight
        Assert.Equal(2, handler.Calls);
    }

    // ── (d) cancelling one caller leaves the shared flight (other callers) unaffected ───────────

    [Fact]
    public async Task CancellingOneCaller_DoesNotAffectOtherCallers()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(async _ => { await release.Task.ConfigureAwait(false); return NewsJson; });
        using var paths = new TempPaths();
        var svc = NewService(handler, new QueueConfig("https://example.test"), paths);

        using var cts = new CancellationTokenSource();
        var cancellable = svc.GetNewsAsync(force: false, cts.Token); // caller A (cancellable)
        var bystander = svc.GetNewsAsync();                          // caller B joins the same flight

        Assert.Equal(1, handler.Calls);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellable); // A's wait is cancelled

        // The shared flight keeps running; B is unaffected and still completes with the real feed.
        release.SetResult();
        var result = await bystander;
        Assert.NotEmpty(result);
        Assert.Equal(1, handler.Calls); // still exactly one fetch — cancellation did not spawn another
    }
}
