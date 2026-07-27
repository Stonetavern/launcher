using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Contract -> <see cref="FriendPresence"/> mapping for the real friends service, driven through the
/// public <see cref="IFriendsPresenceService.GetFriendsAsync"/> against an in-process fake handler (no
/// network). Locks the three producible states (Offline / Online / InGame), the account-as-identity
/// rule, the bearer header, and the offline-first degradation the whole launcher relies on.
/// </summary>
public sealed class HttpFriendsPresenceServiceTests
{
    // Exact on-wire shape from HANDOFF-friends-api.md: a bare array. Covers ingame (online+realm),
    // online-no-realm (in launcher), accepted-offline, and a pending row (offline, no presence).
    private const string FriendsJson =
        """
        [
          {"id":1,"account":{"id":10,"username":"Ashwarden"},"status":"accepted","direction":"outgoing","presence":{"online":true,"realm":"elwynn","activity":"Playing Elwynn"}},
          {"id":2,"account":{"id":11,"username":"Emberfell"},"status":"accepted","direction":"outgoing","presence":{"online":true,"realm":null,"activity":"In launcher"}},
          {"id":3,"account":{"id":12,"username":"Oakenreach"},"status":"accepted","direction":"incoming","presence":{"online":false,"realm":null,"activity":null}},
          {"id":4,"account":{"id":13,"username":"Stormquill"},"status":"pending","direction":"incoming","presence":{"online":false,"realm":null,"activity":null}}
        ]
        """;

    // ── Test doubles ────────────────────────────────────────────────────────────────────────────

    private sealed class StubAuth(bool loggedIn, string? token) : ILauncherAuthService
    {
        public bool IsLoggedIn { get; } = loggedIn; public string? CurrentToken { get; } = token; public string? CurrentAccount => "tester";
        public Task<LoginOutcome> LoginAsync(string u, string p, CancellationToken ct = default) =>
            Task.FromResult(LoginOutcome.Success("tester"));
        public Task<LoginOutcome> RegisterAsync(RegisterRequest r, CancellationToken ct = default) =>
            Task.FromResult(LoginOutcome.Failure("unused"));
        public void Logout() { }
    }

    private sealed class StubConfig : IConfigService
    {
        public LauncherConfig Load() => new() { RealmlistAddress = "play.stonetavern.app" };
        public void Save(LauncherConfig config) { }
        public bool LastSaveSucceeded => true;
    }

    /// <summary>Records the request and returns a fixed status/body, or throws to simulate a network
    /// failure. Counts calls so "no request when signed out" is provable.</summary>
    private sealed class CaptureHandler(HttpStatusCode status, string body = "", bool @throw = false) : HttpMessageHandler
    {
        private readonly HttpStatusCode _status = status;
        private readonly string _body = body;
        private readonly bool _throw = @throw;
        public int Calls { get; private set; }
        public string? LastUrl { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUrl = request.RequestUri?.ToString();
            LastAuthorization = request.Headers.Authorization?.ToString();
            if (_throw) throw new HttpRequestException("simulated network failure");
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new System.Net.Http.StringContent(_body) });
        }
    }

    private static Serilog.ILogger Log => new Serilog.LoggerConfiguration().CreateLogger();

    private static HttpFriendsPresenceService Service(CaptureHandler handler, bool loggedIn = true, string? token = "test-token") =>
        new(new HttpClient(handler), new StubConfig(), new StubAuth(loggedIn, token), Log);

    // ── Mapping ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFriends_MapsAllThreeProducibleStates_ByAccountUsername()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, FriendsJson);
        var list = (await Service(handler).GetFriendsAsync()).ToList();

        Assert.Equal(4, list.Count);

        var ashwarden = list.Single(f => f.Account == "Ashwarden");
        Assert.Equal(PresenceStatus.InGame, ashwarden.Status);     // online + realm -> InGame
        Assert.Equal("Playing Elwynn", ashwarden.Activity);
        Assert.Equal("elwynn", ashwarden.Realm);

        var emberfell = list.Single(f => f.Account == "Emberfell");
        Assert.Equal(PresenceStatus.Online, emberfell.Status);     // online + no realm -> Online
        Assert.Equal("In launcher", emberfell.Activity);
        Assert.Null(emberfell.Realm);

        var oakenreach = list.Single(f => f.Account == "Oakenreach");
        Assert.Equal(PresenceStatus.Offline, oakenreach.Status);   // offline
        Assert.Equal("", oakenreach.Activity);

        var stormquill = list.Single(f => f.Account == "Stormquill");
        Assert.Equal(PresenceStatus.Offline, stormquill.Status);   // pending -> offline
        Assert.Equal("", stormquill.Activity);

        // The displayed identity is always the account username (never a character name); none of the
        // producible states is Away or Busy (the server has no such flag).
        Assert.DoesNotContain(list, f => f.Status is PresenceStatus.Away or PresenceStatus.Busy);
    }

    [Fact]
    public async Task GetFriends_SendsBearerToken_AtFriendsEndpoint()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, FriendsJson);
        await Service(handler, token: "abc-123").GetFriendsAsync();

        Assert.Equal("Bearer abc-123", handler.LastAuthorization);
        Assert.Equal("https://stonetavern.app/api/friends", handler.LastUrl); // play. stripped, /api added
    }

    // ── Offline-first ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFriends_OnNetworkFailure_ReturnsDegradedList_NeverThrows()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, "", @throw: true);
        var list = await Service(handler).GetFriendsAsync();
        Assert.Empty(list); // degraded, not a crash
    }

    [Fact]
    public async Task GetFriends_OnServerError_ReturnsEmpty()
    {
        var handler = new CaptureHandler(HttpStatusCode.InternalServerError, "");
        var list = await Service(handler).GetFriendsAsync();
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetFriends_WhenSignedOut_MakesNoRequest()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, FriendsJson);
        var list = await Service(handler, loggedIn: false, token: null).GetFriendsAsync();

        Assert.Empty(list);
        Assert.Equal(0, handler.Calls); // no token -> no call
    }

    // ── Add friend ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddFriend_On201_ReturnsOptimisticPendingEntry()
    {
        var handler = new CaptureHandler(HttpStatusCode.Created, "");
        var result = await Service(handler).AddFriendAsync("Newcomer");

        Assert.True(result.Ok);
        Assert.NotNull(result.Friend);
        Assert.Equal("Newcomer", result.Friend!.Account);
        Assert.Equal(PresenceStatus.Offline, result.Friend.Status);
        Assert.Equal("Bearer test-token", handler.LastAuthorization);
    }

    [Fact]
    public async Task AddFriend_On404_RejectsWithUnknownAccountMessage()
    {
        var handler = new CaptureHandler(HttpStatusCode.NotFound, "");
        var result = await Service(handler).AddFriendAsync("Ghost");

        Assert.False(result.Ok);
        Assert.Equal("No account with that name.", result.Error);
    }

    [Fact]
    public async Task AddFriend_On409_RejectsWithAlreadyAddedMessage()
    {
        var handler = new CaptureHandler(HttpStatusCode.Conflict, "");
        var result = await Service(handler).AddFriendAsync("Ashwarden");

        Assert.False(result.Ok);
        Assert.Equal("That account is already on your list.", result.Error);
    }
}
