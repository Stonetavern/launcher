using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.ViewModels;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Covers the "create account in the launcher" feature end to end at the unit level:
/// <list type="bullet">
/// <item><see cref="RegisterViewModel"/> client-side validation (nothing empty, rules accepted, the
/// two passwords match) and the auto-sign-in bridge (<c>Registered</c> fires on success).</item>
/// <item><see cref="LauncherAuthService.RegisterAsync"/> hits the right URL with the right JSON body,
/// stores the returned token (so the player ends signed in), and never logs the secret.</item>
/// </list>
/// Zero real accounts, zero real backend, zero real crypto - a capturing HttpMessageHandler and an
/// in-memory token store stand in for the wire and disk. The token/password literals below are
/// obviously fake test fixtures, never credentials.
/// </summary>
public sealed class RegisterTests
{
    // ── Test doubles ────────────────────────────────────────────────────────────────────────────

    private sealed class InMemoryTokenStore : ITokenStore
    {
        public LauncherSession? Saved;
        public LauncherSession? Load() => Saved;
        public void Save(LauncherSession session) => Saved = session;
        public void Clear() => Saved = null;
    }

    private sealed class StubConfig : IConfigService
    {
        public LauncherConfig Load() => new() { RealmlistAddress = "play.stonetavern.app" };
        public void Save(LauncherConfig config) { }
        public bool LastSaveSucceeded => true;
    }

    /// <summary>Records the last request URI + body, then returns a fixed response.</summary>
    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        private readonly HttpStatusCode _status = status;
        private readonly string _body = body;
        public Uri? LastUri { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
        }
    }

    private sealed class ListSink : Serilog.Core.ILogEventSink
    {
        private readonly List<string> _lines = [];
        public void Emit(Serilog.Events.LogEvent e)
        {
            lock (_lines) _lines.Add(e.RenderMessage() + " " + (e.Exception?.ToString() ?? ""));
        }
        public string All() { lock (_lines) return string.Join("\n", _lines); }
    }

    private static (Serilog.ILogger log, ListSink sink) CapturingLogger()
    {
        var sink = new ListSink();
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        return (log, sink);
    }

    /// <summary>An auth-service fake for the ViewModel tests: it records the RegisterRequest it was
    /// handed and returns a configurable outcome, so the VM's validation and event wiring can be tested
    /// without any HTTP.</summary>
    private sealed class FakeAuth : ILauncherAuthService
    {
        public RegisterRequest? LastRequest;
        public int RegisterCalls;
        public LoginOutcome NextOutcome = LoginOutcome.Success("TESTER");

        public bool IsLoggedIn => false;
        public string? CurrentToken => null;
        public string? CurrentAccount => null;
        public Task<LoginOutcome> LoginAsync(string u, string p, CancellationToken ct = default) =>
            Task.FromResult(LoginOutcome.Failure("unused"));
        public Task<LoginOutcome> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
        {
            RegisterCalls++;
            LastRequest = request;
            return Task.FromResult(NextOutcome);
        }
        public void Logout() { }
    }

    // Obviously-fake fixtures, never credentials.
    private const string FakeToken = "xx-fake-fixture-value-xx"; // gitleaks:allow
    private static readonly string OkBody =
        $$"""{"token":"{{FakeToken}}","account":{"id":7,"username":"TESTER"},"expiresAt":32503680000000}""";
    private const string Fixture = "not-a-real-passphrase"; // gitleaks:allow

    // ── Service: URL, body, token storage ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RegisterAsync_PostsToTheRegisterEndpoint_WithTheExpectedJsonBody()
    {
        var (log, _) = CapturingLogger();
        var handler = new CapturingHandler(HttpStatusCode.OK, OkBody);
        var store = new InMemoryTokenStore();
        var auth = new LauncherAuthService(new HttpClient(handler), new StubConfig(), store, log);

        var outcome = await auth.RegisterAsync(
            new RegisterRequest("Tester", "tester@example.com", Fixture, Fixture, AcceptRules: true, Newsletter: true));

        Assert.True(outcome.Ok);
        Assert.Equal("TESTER", outcome.AccountName);

        // Right endpoint (account API base + /launcher/register), never the realm host.
        Assert.NotNull(handler.LastUri);
        Assert.Equal("https://stonetavern.app/api/launcher/register", handler.LastUri!.ToString());

        // Right body: camelCase fields, terms mapped from AcceptRules, newsletter carried through.
        var body = handler.LastBody!;
        Assert.Contains("\"username\":\"Tester\"", body);
        Assert.Contains("\"email\":\"tester@example.com\"", body);
        Assert.Contains("\"confirm\":", body);
        Assert.Contains("\"terms\":true", body);
        Assert.Contains("\"newsletter\":true", body);
    }

    [Fact]
    public async Task RegisterAsync_OnSuccess_StoresTheToken_AndSignsIn()
    {
        var (log, sink) = CapturingLogger();
        var store = new InMemoryTokenStore();
        var auth = new LauncherAuthService(
            new HttpClient(new CapturingHandler(HttpStatusCode.OK, OkBody)), new StubConfig(), store, log);

        await auth.RegisterAsync(
            new RegisterRequest("Tester", "tester@example.com", Fixture, Fixture, true, false));

        Assert.True(auth.IsLoggedIn);
        Assert.Equal("TESTER", auth.CurrentAccount);
        Assert.NotNull(store.Saved);                    // persisted, like a login
        Assert.DoesNotContain(FakeToken, sink.All());   // token never logged
        Assert.DoesNotContain(Fixture, sink.All());     // password never logged
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "That account name is taken, or this email already has too many accounts.")]
    [InlineData((HttpStatusCode)429, "Too many attempts. Please wait a moment and try again.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "The account service is temporarily unavailable. Please try again shortly.")]
    [InlineData(HttpStatusCode.BadRequest, "Please check your details. Your password must be long enough and your email must be valid.")]
    public async Task RegisterAsync_MapsServerStatusToACleanLine_WithoutSigningIn(HttpStatusCode status, string expected)
    {
        var (log, _) = CapturingLogger();
        var store = new InMemoryTokenStore();
        var auth = new LauncherAuthService(
            new HttpClient(new CapturingHandler(status, """{"error":"x","message":"raw 8-20 dashy copy"}""")),
            new StubConfig(), store, log);

        var outcome = await auth.RegisterAsync(
            new RegisterRequest("Tester", "tester@example.com", Fixture, Fixture, true, false));

        Assert.False(outcome.Ok);
        Assert.Equal(expected, outcome.Error);   // local VOICE-clean line, not the raw server message
        Assert.False(auth.IsLoggedIn);
        Assert.Null(store.Saved);
    }

    // ── ViewModel: client-side validation + auto-sign-in bridge ──────────────────────────────────

    private static RegisterViewModel MakeVm(FakeAuth auth) => new(auth);

    [Fact]
    public async Task Vm_RejectsEmptyFields_WithoutCallingTheService()
    {
        var auth = new FakeAuth();
        var vm = MakeVm(auth);
        vm.Username = "Tester"; // everything else empty
        vm.AcceptRules = true;

        await vm.CreateAccountCommand.ExecuteAsync(null);

        Assert.Equal(0, auth.RegisterCalls);
        Assert.True(vm.HasError);
        Assert.Equal("Fill in every field to create your account.", vm.Error);
    }

    [Fact]
    public async Task Vm_RequiresTheRulesCheckbox()
    {
        var auth = new FakeAuth();
        var vm = MakeVm(auth);
        vm.Username = "Tester";
        vm.Email = "tester@example.com";
        vm.Password = Fixture;
        vm.Confirm = Fixture;
        vm.AcceptRules = false;

        await vm.CreateAccountCommand.ExecuteAsync(null);

        Assert.Equal(0, auth.RegisterCalls);
        Assert.Equal("Please accept the server rules to continue.", vm.Error);
    }

    [Fact]
    public async Task Vm_RejectsMismatchedPasswords()
    {
        var auth = new FakeAuth();
        var vm = MakeVm(auth);
        vm.Username = "Tester";
        vm.Email = "tester@example.com";
        vm.Password = Fixture;
        vm.Confirm = "something-else";
        vm.AcceptRules = true;

        await vm.CreateAccountCommand.ExecuteAsync(null);

        Assert.Equal(0, auth.RegisterCalls);
        Assert.Equal("The two passwords do not match.", vm.Error);
    }

    [Fact]
    public async Task Vm_OnValidInput_CallsService_RaisesRegistered_AndClearsSecrets()
    {
        var auth = new FakeAuth { NextOutcome = LoginOutcome.Success("TESTER") };
        var vm = MakeVm(auth);
        var raised = 0;
        vm.Registered += () => raised++;

        vm.Username = "Tester";
        vm.Email = "tester@example.com";
        vm.Password = Fixture;
        vm.Confirm = Fixture;
        vm.AcceptRules = true;
        vm.Newsletter = true;

        await vm.CreateAccountCommand.ExecuteAsync(null);

        Assert.Equal(1, auth.RegisterCalls);
        Assert.NotNull(auth.LastRequest);
        Assert.Equal("tester@example.com", auth.LastRequest!.Email);
        Assert.True(auth.LastRequest.AcceptRules);
        Assert.True(auth.LastRequest.Newsletter);
        Assert.Equal(1, raised);            // auto-sign-in bridge fired
        Assert.False(vm.HasError);
        Assert.Equal("", vm.Password);      // secrets cleared whatever the result
        Assert.Equal("", vm.Confirm);
    }

    [Fact]
    public async Task Vm_OnServerFailure_SurfacesTheError_DoesNotRaiseRegistered_ClearsSecrets()
    {
        var auth = new FakeAuth { NextOutcome = LoginOutcome.Failure("That account name is taken, or this email already has too many accounts.") };
        var vm = MakeVm(auth);
        var raised = 0;
        vm.Registered += () => raised++;

        vm.Username = "Tester";
        vm.Email = "tester@example.com";
        vm.Password = Fixture;
        vm.Confirm = Fixture;
        vm.AcceptRules = true;

        await vm.CreateAccountCommand.ExecuteAsync(null);

        Assert.Equal(1, auth.RegisterCalls);
        Assert.Equal(0, raised);
        Assert.True(vm.HasError);
        Assert.Equal("", vm.Password);      // cleared even on failure
        Assert.Equal("", vm.Confirm);
    }

    // ── LoginViewModel: the mode toggle + the bridge onto SignedIn ────────────────────────────────

    [Fact]
    public async Task LoginVm_RegisterSuccess_BridgesOnto_SignedIn()
    {
        var auth = new FakeAuth { NextOutcome = LoginOutcome.Success("TESTER") };
        var login = new LoginViewModel(auth);
        var signedIn = 0;
        login.SignedIn += () => signedIn++;

        login.ShowRegisterCommand.Execute(null);
        Assert.True(login.IsRegisterMode);

        login.Register.Username = "Tester";
        login.Register.Email = "tester@example.com";
        login.Register.Password = Fixture;
        login.Register.Confirm = Fixture;
        login.Register.AcceptRules = true;

        await login.Register.CreateAccountCommand.ExecuteAsync(null);

        Assert.Equal(1, signedIn); // shell watches only Login.SignedIn and still sees the registration

        login.ShowSignInCommand.Execute(null);
        Assert.False(login.IsRegisterMode);
    }
}
