using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The realm probe. It was rebuilt twice and had no test either time.
///
/// <para>First: the 3s budget used to be a <c>Task.WhenAny</c> race against a delay. The connect task
/// kept running after the timeout, <c>TcpClient.Dispose</c> faulted it, nobody awaited it, and the fault
/// surfaced through <c>TaskScheduler.UnobservedTaskException</c> - one launcher_crash.log entry per
/// check against an unreachable realm. A linked token cancels the connect instead, so there is no
/// orphan left to fault.</para>
///
/// <para>Second: the realmlist address the launcher carries may legally include a port
/// ("play.stonetavern.app:8085" is accepted by the settings form and written verbatim into
/// realmlist.wtf). Handing that whole string to <c>TcpClient.ConnectAsync</c> as a HOST resolves to
/// nothing, so such a realm was shown as offline forever.</para>
///
/// <para>Third, the rule the rest of the app already follows: a TIMEOUT means offline, a CALLER cancel
/// means the answer is not wanted any more and must propagate. Swallowing the second one leaves a wrong
/// "offline" dot on screen for a realm nobody asked about any more.</para>
/// </summary>
public sealed class ServerStatusServiceTests
{
    /// <summary>Answers immediately, so a test never depends on a name resolving or a host answering.</summary>
    private sealed class NoPlayersHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>Hangs until the CALLER's token is signalled, which is what a slow API looks like from
    /// here.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }
    }

    private static ServerStatusService New(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new Serilog.LoggerConfiguration().CreateLogger());

    /// <summary>A port on the loopback that is guaranteed to refuse: bound, read, released.</summary>
    private static int ClosedLoopbackPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ── Host/port split ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("play.stonetavern.app", 3724, "play.stonetavern.app", 3724)]
    [InlineData("play.stonetavern.app:8085", 3724, "play.stonetavern.app", 8085)]
    [InlineData("127.0.0.1:8085", 3724, "127.0.0.1", 8085)]
    [InlineData("[::1]", 3724, "::1", 3724)]
    [InlineData("[::1]:8085", 3724, "::1", 8085)]
    [InlineData("::1", 3724, "::1", 3724)]                       // bare IPv6: the colons are the address
    [InlineData("play.stonetavern.app:notaport", 3724, "play.stonetavern.app:notaport", 3724)]
    [InlineData("play.stonetavern.app:70000", 3724, "play.stonetavern.app:70000", 3724)]
    [InlineData("", 3724, "", 3724)]
    public void SplitHostPort_SeparatesThePortTheRealmlistAddressMayCarry(
        string address, int fallback, string expectedHost, int expectedPort)
    {
        var (host, port) = ServerStatusService.SplitHostPort(address, fallback);
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
    }

    /// <summary>
    /// The property the split exists for, measured through the public entry point: a realm whose address
    /// carries its port must be probed on THAT port. Same listener, two spellings of the same target -
    /// one of them used to be unresolvable and therefore permanently offline.
    /// </summary>
    [Fact]
    public async Task ARealmAddressCarryingItsPort_IsProbedOnThatPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var svc = New(new NoPlayersHandler());

            var withPort = await svc.CheckAsync($"127.0.0.1:{port}");
            Assert.True(withPort.Online,
                "A realmlist address of the form host:port must be probed on that port. Passing the " +
                "whole string as a host name makes every such realm offline forever.");

            var closed = await svc.CheckAsync("127.0.0.1", ClosedLoopbackPort());
            Assert.False(closed.Online);
        }
        finally { listener.Stop(); }
    }

    // ── Timeout vs caller cancel ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnreachableRealmIsOffline_NotAnException()
    {
        var handler = new NoPlayersHandler();
        var result = await New(handler).CheckAsync($"127.0.0.1:{ClosedLoopbackPort()}", ["elwynn"]);

        Assert.False(result.Online);
        Assert.Null(result.PlayerCount);  // the API said 404: nothing counted, and null is not zero
        Assert.Equal(1, handler.Calls);   // the probe failing must not stop the player count call
    }

    /// <summary>
    /// The caller gave up (a newer expansion or realm pick superseded this check). That must come back
    /// as a cancellation, not as "the realm is offline" - the newer check owns the dot, and a stale
    /// false answer overwrites it.
    /// </summary>
    [Fact]
    public async Task ACallerCancelWhileTheApiIsSlow_Propagates()
    {
        var handler = new HangingHandler();
        var svc = New(handler);
        using var cts = new CancellationTokenSource();

        var check = svc.CheckAsync($"127.0.0.1:{ClosedLoopbackPort()}", ["elwynn"], cts.Token);
        await handler.Reached.Task;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
    }

    /// <summary>A token that is already dead must not put a socket on the wire or a request in flight.</summary>
    [Fact]
    public async Task AnAlreadyCancelledCheck_DoesNothingAtAll()
    {
        var handler = new NoPlayersHandler();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => New(handler).CheckAsync("127.0.0.1", ClosedLoopbackPort(), cts.Token));
        Assert.Equal(0, handler.Calls);
    }

    // ── Player count: summed over our realms, none for a foreign server ─────────────────────────

    /// <summary>Answers /api/realm?realm=id from a table, and records every URL it was asked for.</summary>
    private sealed class RealmApiHandler(System.Collections.Generic.Dictionary<string, string> bodies) : HttpMessageHandler
    {
        public readonly System.Collections.Concurrent.ConcurrentBag<string> Urls = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            var realm = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["realm"] ?? "";
            return Task.FromResult(bodies.TryGetValue(realm, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private const string Elwynn45 =
        """{"state":"up","online":45,"checkedAt":1,"presence":{"online":45,"source":"database"}}""";
    private const string Barrens16 =
        """{"state":"up","online":16,"checkedAt":1,"presence":{"online":16,"source":"database"}}""";

    /// <summary>
    /// Stonetavern is one login address with two game realms behind it; the login shell shows ONE number
    /// for it, both realms together. Before, the count came from the default realm alone (46 while
    /// Elwynn 45 + Barrens 16 were on, owner screenshot 2026-09-28).
    /// </summary>
    [Fact]
    public async Task OurRealm_CountsEveryGameRealmBehindTheAddress_AsOneSum()
    {
        var handler = new RealmApiHandler(new() { ["elwynn"] = Elwynn45, ["barrens"] = Barrens16 });

        var result = await New(handler).CheckAsync($"127.0.0.1:{ClosedLoopbackPort()}", ["elwynn", "barrens"]);

        Assert.Equal(61, result.PlayerCount);
        Assert.Equal(2, handler.Urls.Count);
        Assert.Contains(handler.Urls, u => u.EndsWith("/api/realm?realm=elwynn", StringComparison.Ordinal));
        Assert.Contains(handler.Urls, u => u.EndsWith("/api/realm?realm=barrens", StringComparison.Ordinal));
    }

    /// <summary>
    /// A server that is not ours gets up/down and nothing else. It used to be sent Stonetavern's count
    /// (the API host came from the config's global realmlist, whatever realm was being probed), so
    /// "kronos" showed "46 online".
    /// </summary>
    [Fact]
    public async Task AForeignServer_GetsNoCount_AndNoRequestIsMadeForOne()
    {
        var handler = new RealmApiHandler(new() { ["elwynn"] = Elwynn45 });

        var result = await New(handler).CheckAsync($"127.0.0.1:{ClosedLoopbackPort()}", []);
        var plain = await New(handler).CheckAsync("127.0.0.1", ClosedLoopbackPort());

        Assert.Null(result.PlayerCount);
        Assert.Null(plain.PlayerCount);
        Assert.Empty(handler.Urls);
    }

    /// <summary>A sum with a hole in it is a smaller number presented as the total: if one realm cannot
    /// be counted, there is no number at all.</summary>
    [Fact]
    public async Task OneRealmUncountable_MeansNoNumber_NotAPartialSum()
    {
        var failing = new RealmApiHandler(new() { ["elwynn"] = Elwynn45 });   // barrens answers 500
        var nullPresence = new RealmApiHandler(new()
        {
            ["elwynn"] = Elwynn45,
            ["barrens"] = """{"state":"up","online":null,"checkedAt":1,"presence":{"online":null,"source":"database"}}""",
        });

        Assert.Null((await New(failing).CheckAsync("127.0.0.1:1", ["elwynn", "barrens"])).PlayerCount);
        Assert.Null((await New(nullPresence).CheckAsync("127.0.0.1:1", ["elwynn", "barrens"])).PlayerCount);
    }

    /// <summary>The database presence is the number for Barrens (the web tier cannot see its world
    /// port); it wins over the top-level field when both are sent.</summary>
    [Fact]
    public async Task ThePresenceCount_WinsOverTheTopLevelNumber()
    {
        var handler = new RealmApiHandler(new()
        {
            ["barrens"] = """{"state":"unknown","online":0,"checkedAt":1,"presence":{"online":16,"source":"database"}}""",
        });

        Assert.Equal(16, (await New(handler).CheckAsync("127.0.0.1:1", ["barrens"])).PlayerCount);
    }

    [Theory]
    [InlineData("play.stonetavern.app", "stonetavern.app")]
    [InlineData("PLAY.stonetavern.app", "stonetavern.app")]
    [InlineData("stonetavern.app", "stonetavern.app")]
    [InlineData("replay.example.org", "replay.example.org")]   // only the prefix goes, not every "play."
    [InlineData("", "")]
    public void TheApiHost_IsTheLoginHostWithoutItsPlayPrefix(string login, string expected)
        => Assert.Equal(expected, ServerStatusService.ApiHostFor(login));
}
