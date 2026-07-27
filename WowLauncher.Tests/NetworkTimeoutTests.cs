using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.ViewModels;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// A server that is reachable but does not answer must be a reported failure, never an escaping
/// exception.
///
/// <para>Both HTTP services used to do <c>catch (OperationCanceledException) { throw; }</c> with the
/// reasoning that a cancelled request is not a failure. No caller passes a token, so the only producer
/// of that exception was the HttpClient timeout - and TaskCanceledException derives from
/// OperationCanceledException. The rethrow escaped an <c>[RelayCommand]</c> async method, which the
/// generated AsyncRelayCommand surfaces as an unhandled exception on the UI thread. The player saw a
/// cleared password field and no explanation.</para>
///
/// <para>The distinction that has to hold: the CALLER cancelling still propagates, a timeout does not.</para>
/// </summary>
public sealed class NetworkTimeoutTests
{
    // ── Test doubles ────────────────────────────────────────────────────────────────────────────

    /// <summary>Throws exactly what HttpClient throws when its Timeout elapses: a
    /// TaskCanceledException whose token was never signalled by the caller.</summary>
    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
    }

    /// <summary>Honours the caller's token, so "a real cancel still propagates" is provable.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }
    }

    private sealed class StubConfig : IConfigService
    {
        public LauncherConfig Load() => new() { RealmlistAddress = "play.stonetavern.app" };
        public void Save(LauncherConfig config) { }
        public bool LastSaveSucceeded => true;
    }

    private sealed class MemoryTokenStore : ITokenStore
    {
        private LauncherSession? _s;
        public LauncherSession? Load() => _s;
        public void Save(LauncherSession session) => _s = session;
        public void Clear() => _s = null;
    }

    private sealed class SignedInAuth : ILauncherAuthService
    {
        public bool IsLoggedIn => true;
        public string? CurrentToken => "test-token";
        public string? CurrentAccount => "tester";
        public Task<LoginOutcome> LoginAsync(string u, string p, CancellationToken ct = default) =>
            Task.FromResult(LoginOutcome.Success("tester"));
        public Task<LoginOutcome> RegisterAsync(RegisterRequest r, CancellationToken ct = default) =>
            Task.FromResult(LoginOutcome.Failure("unused"));
        public void Logout() { }
    }

    private static Serilog.ILogger Silent() => new Serilog.LoggerConfiguration().CreateLogger();

    // ── Sign-in ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SignInAgainstAHangingServer_ReturnsAFailure_InsteadOfThrowing()
    {
        var svc = new LauncherAuthService(new HttpClient(new TimeoutHandler()),
            new StubConfig(), new MemoryTokenStore(), Silent());

        var outcome = await svc.LoginAsync("player", "secret");

        Assert.False(outcome.Ok);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Error),
            "A timeout must produce a line the player can read, not an unhandled exception.");
        Assert.False(svc.IsLoggedIn);
    }

    [Fact]
    public async Task SignInTimeout_ReadsAsATimeout_NotAsBadCredentials()
    {
        var svc = new LauncherAuthService(new HttpClient(new TimeoutHandler()),
            new StubConfig(), new MemoryTokenStore(), Silent());

        var outcome = await svc.LoginAsync("player", "secret");

        // Cause separation: a server that does not answer is not a wrong password and not a
        // malformed response body. Those three have cost real debugging time before.
        Assert.NotEqual(WowLauncher.Localization.Loc.T("Login_Error_Invalid"), outcome.Error);
        Assert.NotEqual(WowLauncher.Localization.Loc.T("Login_Error_BadResponse"), outcome.Error);
        Assert.Equal(WowLauncher.Localization.Loc.T("Login_Error_Timeout"), outcome.Error);
    }

    [Fact]
    public async Task ACallerCancelIsStillPropagated()
    {
        var svc = new LauncherAuthService(new HttpClient(new HangingHandler()),
            new StubConfig(), new MemoryTokenStore(), Silent());

        using var cts = new CancellationTokenSource();
        var task = svc.LoginAsync("player", "secret", cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task TheSignInCommandSurfacesTheTimeoutAsAnErrorLine()
    {
        var svc = new LauncherAuthService(new HttpClient(new TimeoutHandler()),
            new StubConfig(), new MemoryTokenStore(), Silent());
        var vm = new LoginViewModel(svc) { Username = "player", Password = "secret" };

        // The command itself must complete. Before the fix this faulted and the AsyncRelayCommand
        // rethrew onto the UI synchronization context.
        await vm.SignInCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Equal("", vm.Password);
    }

    // ── Friends / presence ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFriendsPollAgainstAHangingServer_DegradesInsteadOfThrowing()
    {
        var svc = new HttpFriendsPresenceService(new HttpClient(new TimeoutHandler()),
            new StubConfig(), new SignedInAuth(), Silent());

        var friends = await svc.GetFriendsAsync();

        Assert.Empty(friends);
    }

    [Fact]
    public async Task AddFriendAgainstAHangingServer_IsRejectedWithATimeoutLine()
    {
        var svc = new HttpFriendsPresenceService(new HttpClient(new TimeoutHandler()),
            new StubConfig(), new SignedInAuth(), Silent());

        var result = await svc.AddFriendAsync("Ashwarden");

        Assert.False(result.Ok);
        Assert.Equal(WowLauncher.Localization.Loc.T("Friends_Error_Timeout"), result.Error);
    }

    [Fact]
    public async Task AFriendsPollCancelledByItsCaller_StillPropagates()
    {
        var svc = new HttpFriendsPresenceService(new HttpClient(new HangingHandler()),
            new StubConfig(), new SignedInAuth(), Silent());

        using var cts = new CancellationTokenSource();
        var task = svc.GetFriendsAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}
