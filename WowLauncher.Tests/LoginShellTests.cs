using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Localization;
using WowLauncher.Models;
using WowLauncher.Startup;
using WowLauncher.ViewModels;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The 1.9 login/loading shell (design/SPEC-2026-09-20), everything below the window: the init
/// pipeline (§5.2), the stub gateway, the ViewModel's phases (§2, §6) and the config flag.
///
/// <para>No Avalonia here: the ViewModel takes a straight-through <c>post</c>, so the assertions
/// run on the test thread and a hung pipeline would hang the test instead of passing it.</para>
/// </summary>
public sealed class LoginShellTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static InitStep Step(string key, bool gates, Func<CancellationToken, Task<string?>>? run = null) =>
        new(key, $"status:{key}", gates, run ?? (_ => Task.FromResult<string?>(null)));

    private static InitStep Blocked(string key, bool gates, TaskCompletionSource<string?> tcs) =>
        new(key, $"status:{key}", gates, _ => tcs.Task);

    private static (InitPipeline, InitFacts) Ready(InitFacts? facts = null)
    {
        var p = new InitPipeline(
        [
            Step(LauncherInitSteps.ConfigKey, true),
            Step(LauncherInitSteps.UpdateKey, true),
            Step(LauncherInitSteps.RealmKey, true),
            Step(LauncherInitSteps.CdnKey, false),
            Step(LauncherInitSteps.InstallsKey, false),
        ]);
        return (p, facts ?? new InitFacts { Realms = [new RealmProbe("stonetavern", "Stonetavern", "play.stonetavern.app", true, 7)] });
    }

    private static LoginShellViewModel Vm(InitPipeline p, InitFacts f, IAuthGateway auth, bool allowSkip = false) =>
        new(p, f, auth, allowSkip, post: a => a());

    private sealed class ScriptedGateway(Func<CancellationToken, Task<AuthResult>> answer) : IAuthGateway
    {
        public int Calls;
        public Task<AuthResult> SignInAsync(string u, string p, CancellationToken ct) { Calls++; return answer(ct); }
    }

    // ── InitPipeline ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StepsRunInOrder_AndEveryStateChangeIsReported()
    {
        var reports = new List<InitStepReport>();
        var p = new InitPipeline([Step("a", true), Step("b", false)]);
        p.StepChanged += reports.Add;

        await p.RunAsync(CancellationToken.None);

        Assert.Equal(["a:Running", "a:Done", "b:Running", "b:Done"], reports.Select(r => $"{r.Key}:{r.State}"));
        Assert.True(p.IsComplete);
    }

    [Fact]
    public async Task AFailingStepIsRecorded_AndTheRestStillRuns()
    {
        var reports = new List<InitStepReport>();
        var p = new InitPipeline(
        [
            Step("a", true, _ => throw new InvalidOperationException("boom")),
            Step("b", false),
        ]);
        p.StepChanged += reports.Add;

        await p.RunAsync(CancellationToken.None);   // must not throw

        Assert.Equal(InitStepState.Failed, p.StateOf("a"));
        Assert.Equal(InitStepState.Done, p.StateOf("b"));
        Assert.Contains(reports, r => r.Key == "a" && r.State == InitStepState.Failed && r.Detail == "boom");
    }

    [Fact]
    public async Task TheLoginGateOpensWhenTheGatingStepsCompleted_EvenWhenOneFailed()
    {
        var cdn = new TaskCompletionSource<string?>();
        var p = new InitPipeline(
        [
            Step("config", true),
            Step("update", true, _ => throw new Exception("no manifest")),
            Step("realm", true),
            Blocked("cdn", false, cdn),
            Step("installs", false),
        ]);

        var run = p.RunAsync(CancellationToken.None);
        // cdn is blocked, so the pipeline is parked there: gate must already be open.
        await WaitUntil(() => p.StateOf("cdn") == InitStepState.Running);

        Assert.True(p.IsLoginGateOpen);
        Assert.False(p.IsComplete);

        cdn.SetResult(null);
        await run;
        Assert.True(p.IsComplete);
    }

    [Fact]
    public async Task TheGateStaysClosedWhileAGatingStepRuns()
    {
        var realm = new TaskCompletionSource<string?>();
        var p = new InitPipeline([Step("config", true), Blocked("realm", true, realm), Step("cdn", false)]);

        var run = p.RunAsync(CancellationToken.None);
        await WaitUntil(() => p.StateOf("realm") == InitStepState.Running);

        Assert.False(p.IsLoginGateOpen);
        realm.SetResult("online");
        await run;
        Assert.True(p.IsLoginGateOpen);
    }

    [Fact]
    public async Task AHaltStopsThePipelineAfterTheCurrentStep()
    {
        InitPipeline? self = null;
        var p = new InitPipeline(
        [
            Step("config", true),
            Step("update", true, _ => { self!.RequestHalt("updating"); return Task.FromResult<string?>("swap"); }),
            Step("realm", true),
        ]);
        self = p;
        string? halted = null;
        p.HaltRequested += t => halted = t;

        await p.RunAsync(CancellationToken.None);

        Assert.Equal("updating", halted);
        Assert.Equal(InitStepState.Pending, p.StateOf("realm"));
        Assert.True(p.IsHalted);
        Assert.True(p.IsComplete);
    }

    [Fact]
    public async Task CancellationEndsTheRunQuietly()
    {
        var cts = new CancellationTokenSource();
        var p = new InitPipeline(
        [
            Step("a", true, async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return null; }),
            Step("b", true),
        ]);
        var run = p.RunAsync(cts.Token);
        cts.Cancel();
        await run;   // no throw
        Assert.Equal(InitStepState.Pending, p.StateOf("b"));
    }

    [Fact]
    public async Task AStepOverItsBudget_FailsAndTheGateStillOpens()
    {
        var p = new InitPipeline(
        [
            new InitStep("update", "s", true,
                async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return null; },
                budget: TimeSpan.FromMilliseconds(40)),
            Step("realm", true),
        ]);
        await p.RunAsync(CancellationToken.None);

        Assert.Equal(InitStepState.Failed, p.StateOf("update"));
        Assert.Equal(InitStepState.Done, p.StateOf("realm"));
        Assert.True(p.IsLoginGateOpen);
    }

    [Fact]
    public void TheNetworkStepsCarryTheBudget_TheLocalOnesDoNot()
    {
        // The real step list, built on stubs: the budget is a property of the shipped wiring, not
        // of the test double, so a step that loses it goes red here.
        var (p, _) = LauncherInitSteps.Build(
            new StubConfig(), new StubManifest(), new StubUpdate(), new StubStatus(), new StubClient());
        var byKey = p.Steps.ToDictionary(s => s.Key);
        Assert.Null(byKey[LauncherInitSteps.ConfigKey].Budget);
        Assert.Equal(LauncherInitSteps.NetworkBudget, byKey[LauncherInitSteps.UpdateKey].Budget);
        Assert.Equal(LauncherInitSteps.NetworkBudget, byKey[LauncherInitSteps.RealmKey].Budget);
        Assert.Equal(LauncherInitSteps.NetworkBudget, byKey[LauncherInitSteps.CdnKey].Budget);
        Assert.Null(byKey[LauncherInitSteps.InstallsKey].Budget);
        Assert.Equal([true, true, true, false, false], p.Steps.Select(s => s.GatesLogin));
    }

    [Fact]
    public void DuplicateKeysAreRefused()
    {
        Assert.Throws<ArgumentException>(() => new InitPipeline([Step("a", true), Step("a", false)]));
    }

    // ── FakeAuthGateway ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("success", AuthResultKind.Success)]
    [InlineData("REJECT", AuthResultKind.Rejected)]
    [InlineData("unavailable", AuthResultKind.Unavailable)]
    [InlineData("garbage", AuthResultKind.Success)]
    [InlineData(null, AuthResultKind.Success)]
    public async Task TheStubAnswersWhatTheConfigSays(string? mode, AuthResultKind expected)
    {
        var g = new FakeAuthGateway(mode, TimeSpan.Zero, k => k);
        var r = await g.SignInAsync("player", "pw", CancellationToken.None);
        Assert.Equal(expected, r.Kind);
    }

    [Fact]
    public async Task TheTimeoutModeNeverAnswers_OnlyTheTokenEndsIt()
    {
        var g = new FakeAuthGateway("timeout", TimeSpan.Zero, k => k);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => g.SignInAsync("p", "w", cts.Token));
    }

    // ── LoginShellViewModel: phases ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ColdStart_ThenPhase1_GateOpens_StatusEndsReady_RealmRowResolves()
    {
        var (p, f) = Ready();
        var vm = Vm(p, f, new FakeAuthGateway("success", TimeSpan.Zero, k => k));

        Assert.Equal(LoginPhase.ColdStart, vm.Phase);
        Assert.True(vm.IsColdStart);
        Assert.False(vm.IsGateOpen);
        Assert.Single(vm.Realms);
        Assert.False(vm.Realms[0].IsResolved);

        vm.BeginPhase1();
        await WaitUntil(() => p.IsComplete);

        Assert.Equal(LoginPhase.Login, vm.Phase);
        Assert.True(vm.IsGateOpen);
        Assert.True(vm.SignInCommand.CanExecute(null));
        Assert.True(vm.Realms[0].IsResolved);
        Assert.True(vm.Realms[0].IsOnline);
        Assert.Equal(Localization.Loc.T("Init_Status_Ready"), vm.StatusText);
    }

    [Fact]
    public async Task AnUnreachableRealm_IsReportedOnTheStatusRow_AndDoesNotLockTheGate()
    {
        var (p, f) = Ready(new InitFacts { Realms = [new RealmProbe("stonetavern", "Stonetavern", "x", false, 0)] });
        var vm = Vm(p, f, new FakeAuthGateway("success", TimeSpan.Zero, k => k));
        vm.BeginPhase1();
        await WaitUntil(() => p.IsComplete);

        Assert.True(vm.IsRealmUnreachable);
        Assert.True(vm.IsGateOpen);
        Assert.Equal(Localization.Loc.T("Init_Status_RealmDown"), vm.StatusText);
        Assert.False(vm.Realms[0].IsOnline);
    }

    [Fact]
    public async Task SignInIsGatedUntilSteps1To3Completed()
    {
        var realm = new TaskCompletionSource<string?>();
        var p = new InitPipeline(
        [
            Step(LauncherInitSteps.ConfigKey, true),
            Step(LauncherInitSteps.UpdateKey, true),
            Blocked(LauncherInitSteps.RealmKey, true, realm),
        ]);
        var vm = Vm(p, new InitFacts(), new FakeAuthGateway("success", TimeSpan.Zero, k => k));
        vm.Username = "a"; vm.Password = "b";
        vm.BeginPhase1();
        await WaitUntil(() => p.StateOf(LauncherInitSteps.RealmKey) == InitStepState.Running);

        Assert.False(vm.SignInCommand.CanExecute(null));
        Assert.True(vm.IsButtonBusy);
        Assert.Equal("status:realm", vm.StatusText);

        realm.SetResult(null);
        await WaitUntil(() => vm.IsGateOpen);
        Assert.True(vm.SignInCommand.CanExecute(null));
    }

    [Fact]
    public async Task EmptyFields_AreAnInlineError_NotARequest()
    {
        var (p, f) = Ready();
        var gw = new ScriptedGateway(_ => Task.FromResult(AuthResult.Success("x")));
        var vm = Vm(p, f, gw);
        vm.BeginPhase1();
        await WaitUntil(() => vm.IsGateOpen);

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Equal(0, gw.Calls);
        Assert.Equal(Localization.Loc.T("Login_Error_Empty"), vm.Error);
        Assert.Equal(LoginPhase.Login, vm.Phase);
    }

    [Fact]
    public async Task Success_PulsesThenTransitions_ThenCompletes()
    {
        var (p, f) = Ready();
        var vm = Vm(p, f, new FakeAuthGateway("success", TimeSpan.Zero, k => k));
        var completed = 0;
        vm.TransitionCompleted += () => completed++;
        vm.BeginPhase1();
        await WaitUntil(() => vm.IsGateOpen);
        vm.Username = "player"; vm.Password = "pw";

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Equal(LoginPhase.Succeeded, vm.Phase);
        Assert.Equal("", vm.Password);                  // never kept
        Assert.True(vm.FieldsReadOnly);
        Assert.True(vm.IsButtonBusy);

        vm.BeginTransition();                           // the View, after the pulse
        Assert.Equal(LoginPhase.Transition, vm.Phase);
        Assert.True(vm.IsTransitioning);

        vm.CompleteTransition();                        // the View, after Motion.Transition
        Assert.Equal(LoginPhase.Done, vm.Phase);
        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task Rejected_ShakesOnce_ShowsTheError_FocusesThePassword_AndStaysInPhase1()
    {
        var (p, f) = Ready();
        var vm = Vm(p, f, new FakeAuthGateway("reject", TimeSpan.Zero, k => k));
        vm.BeginPhase1();
        await WaitUntil(() => vm.IsGateOpen);
        vm.Username = "player"; vm.Password = "wrong";

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Equal(LoginPhase.Login, vm.Phase);
        Assert.Equal("Login_Error_Invalid", vm.Error);
        Assert.True(vm.HasError);
        Assert.Equal(1, vm.ShakeTick);
        Assert.True(vm.WantsPasswordFocus);
        Assert.False(vm.OffersOfflinePath);
        Assert.Equal("", vm.Password);
        Assert.True(vm.SignInCommand.CanExecute(null));  // can try again

        vm.Password = "wrong again";
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.ShakeTick);                  // a counter, so the second shake plays too
    }

    [Fact]
    public async Task Timeout_UsesTheOwnSentence_AndOffersTheOfflinePath()
    {
        var (p, f) = Ready();
        // Never answers: the ViewModel's own cut-off must end it. The policy is 10 s in production;
        // here the gateway honours the token and the test shortens nothing, so use a token-aware
        // gateway that throws on cancel immediately to prove the catch path without waiting 10 s.
        var gw = new ScriptedGateway(_ => throw new OperationCanceledException());
        var vm = Vm(p, f, gw);
        vm.BeginPhase1();
        await WaitUntil(() => vm.IsGateOpen);
        vm.Username = "player"; vm.Password = "pw";

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Equal(LoginPhase.Login, vm.Phase);
        Assert.Equal(Localization.Loc.T("Login_Error_Timeout"), vm.Error);
        Assert.True(vm.OffersOfflinePath);
        Assert.True(vm.ShowsOfflinePath);
        Assert.Equal(0, vm.ShakeTick);                  // a timeout is not a wrong password
        Assert.True(vm.ContinueOfflineCommand.CanExecute(null));

        vm.ContinueOfflineCommand.Execute(null);
        Assert.Equal(LoginPhase.Transition, vm.Phase);
    }

    [Fact]
    public async Task Unavailable_ReadsLikeATimeout_NotLikeAWrongPassword()
    {
        var (p, f) = Ready();
        var vm = Vm(p, f, new FakeAuthGateway("unavailable", TimeSpan.Zero, k => k));
        vm.BeginPhase1();
        await WaitUntil(() => vm.IsGateOpen);
        vm.Username = "player"; vm.Password = "pw";

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Equal("Login_Error_Unavailable", vm.Error);   // the fake's own localizer (k => k)
        Assert.True(vm.OffersOfflinePath);
        Assert.Equal(0, vm.ShakeTick);
    }

    [Fact]
    public async Task TheOfflineLinkIsHiddenUnlessTimedOut_OrTheOwnerFlippedSection12_3()
    {
        var (p, f) = Ready();
        var vm = Vm(p, f, new FakeAuthGateway("success", TimeSpan.Zero, k => k));
        vm.BeginPhase1();
        await WaitUntil(() => vm.IsGateOpen);
        Assert.False(vm.ShowsOfflinePath);
        Assert.False(vm.ContinueOfflineCommand.CanExecute(null));

        var (p2, f2) = Ready();
        var vm2 = Vm(p2, f2, new FakeAuthGateway("success", TimeSpan.Zero, k => k), allowSkip: true);
        vm2.BeginPhase1();
        await WaitUntil(() => vm2.IsGateOpen);
        Assert.True(vm2.ShowsOfflinePath);
        Assert.True(vm2.ContinueOfflineCommand.CanExecute(null));
    }

    [Fact]
    public async Task ALauncherUpdateNotice_BecomesTheBanner()
    {
        var facts = new InitFacts
        {
            Realms = [new RealmProbe("stonetavern", "Stonetavern", "x", true, 1)],
            UpdateNotice = new Services.LauncherUpdateNotice("1.9.0", "https://stonetavern.app"),
        };
        var (p, f) = Ready(facts);
        var vm = Vm(p, f, new FakeAuthGateway("success", TimeSpan.Zero, k => k));
        vm.BeginPhase1();
        await WaitUntil(() => p.IsComplete);
        Assert.True(vm.HasUpdateBanner);
        Assert.Contains("1.9.0", vm.UpdateBanner);
    }

    [Fact]
    public async Task ASelfUpdateSwap_HaltsTheScreenWithTheUpdateSentence()
    {
        InitPipeline? self = null;
        var p = new InitPipeline(
        [
            Step(LauncherInitSteps.ConfigKey, true),
            Step(LauncherInitSteps.UpdateKey, true, _ => { self!.RequestHalt("Splash_Status_UpdatingLauncher"); return Task.FromResult<string?>("swap"); }),
            Step(LauncherInitSteps.RealmKey, true),
        ]);
        self = p;
        var vm = Vm(p, new InitFacts(), new FakeAuthGateway("success", TimeSpan.Zero, k => k));
        var halted = 0;
        vm.HaltRequested += () => halted++;
        vm.BeginPhase1();
        await WaitUntil(() => vm.Phase == LoginPhase.Halted);

        Assert.Equal(1, halted);
        Assert.Equal("Splash_Status_UpdatingLauncher", vm.StatusText);   // the halt text is passed through verbatim
        Assert.False(vm.SignInCommand.CanExecute(null));
    }

    // ── The flag ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("login", true)]
    [InlineData(" LOGIN ", true)]
    [InlineData("v2", true)]          // pre-1.9 value, kept as alias
    [InlineData("", true)]            // 1.9.1: the login shell is the default
    [InlineData("v3", true)]          // unknown -> the known default surface, not the legacy one
    [InlineData("v1", false)]         // the one escape hatch back to the old start
    [InlineData(" V1 ", false)]
    public void OnlyV1OptsOutOfTheLoginShell(string value, bool expected)
    {
        Assert.Equal(expected, LoginShellFlag.IsEnabled(new LauncherConfig { LauncherShell = value }));
    }

    [Fact]
    public void TheDefaultConfigOpensTheLoginShell()
    {
        var cfg = new LauncherConfig();
        Assert.True(LoginShellFlag.IsEnabled(cfg));
        Assert.Equal("success", FakeAuthGateway.Normalize(cfg.LauncherShellFakeAuth));
        Assert.False(cfg.LauncherShellAllowSkipSignIn);
    }

    // ── Spec §12.1 policy ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheAuthTimeoutIsTenSeconds() => Assert.Equal(TimeSpan.FromSeconds(10), AuthPolicy.Timeout);

    // ── Spec §12.1 A: the adapter over the launcher service ─────────────────────────────────────

    [Theory]
    [InlineData("Login_Error_Invalid", AuthResultKind.Rejected)]
    [InlineData("Login_Error_Timeout", AuthResultKind.Timeout)]
    [InlineData("Login_Error_Network", AuthResultKind.Unavailable)]
    [InlineData("Login_Error_RateLimit", AuthResultKind.Unavailable)]
    public async Task TheRealGatewayKeepsTheServicesOwnLine(string key, AuthResultKind expected)
    {
        var service = new ScriptedLauncherAuth { Next = Services.LoginOutcome.Failure(Loc.T(key)) };

        var result = await new LauncherAuthGateway(service).SignInAsync("tester", "secret", default);

        Assert.Equal(expected, result.Kind);
        Assert.Equal(Loc.T(key), result.Message);   // the service's line, never re-worded
    }

    [Fact]
    public async Task TheRealGatewayMapsSuccessToTheAccountName()
    {
        var service = new ScriptedLauncherAuth { Next = Services.LoginOutcome.Success("player-one") };

        var result = await new LauncherAuthGateway(service).SignInAsync("player-one", "secret", default);

        Assert.Equal(AuthResultKind.Success, result.Kind);
        Assert.Equal("player-one", result.AccountName);
        Assert.Null(result.Message);
    }

    // ── The REAL service against a dead and a hanging origin (Spec §6) ───────────────────────────

    /// <summary>A port nothing listens on: the request fails at once and the card must offer the
    /// offline path without waiting for the 10 s cut-off.</summary>
    [Fact]
    public async Task RealService_DeadOrigin_OffersTheOfflinePath()
    {
        var (log, _) = CapturingLogger();
        using var paths = new TempPaths();
        paths.EnsureDirectories();
        var (p, f) = Ready();
        var auth = RealAuth(log, paths, DeadPort());
        var vm = Vm(p, f, new LauncherAuthGateway(auth));
        vm.BeginPhase1();
        await WaitUntil(() => vm.IsGateOpen);
        vm.Username = "tester";
        vm.Password = "secret";

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Equal(LoginPhase.Login, vm.Phase);
        Assert.True(vm.OffersOfflinePath);
        Assert.True(vm.ShowsOfflinePath);
        Assert.Equal(Loc.T("Login_Error_Network"), vm.Error);
        Assert.Equal("", vm.Password);       // cleared after the attempt, whatever the result
        Assert.False(auth.IsLoggedIn);
    }

    /// <summary>An origin that accepts and never answers: the only way out is our 10 s policy, and
    /// the log must name the timeout (AP2, Spec §6). The HttpClient's own 30 s timeout is the safety
    /// net that turns a broken cut-off into a red assertion instead of a hanging test.</summary>
    [Fact]
    public async Task RealService_HangingOrigin_TimesOutAfterTenSeconds_WithOfflinePath()
    {
        using var blackhole = new TcpListener(IPAddress.Loopback, 0);
        blackhole.Start();
        var port = ((IPEndPoint)blackhole.LocalEndpoint).Port;

        var (log, sink) = CapturingLogger();
        var previous = Serilog.Log.Logger;
        Serilog.Log.Logger = log;   // the timeout line is the ViewModel's, the service is injected
        try
        {
            using var paths = new TempPaths();
            paths.EnsureDirectories();
            var (p, f) = Ready();
            var auth = RealAuth(log, paths, port);
            var vm = Vm(p, f, new LauncherAuthGateway(auth));
            vm.BeginPhase1();
            await WaitUntil(() => vm.IsGateOpen);
            vm.Username = "tester";
            vm.Password = "secret";

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await vm.SignInCommand.ExecuteAsync(null);
            sw.Stop();

            Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(9),
                $"the 10 s policy must be the cut-off, answered after {sw.Elapsed.TotalSeconds:F1} s");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(18),
                $"and not much later: {sw.Elapsed.TotalSeconds:F1} s");
            Assert.Equal(LoginPhase.Login, vm.Phase);
            Assert.True(vm.OffersOfflinePath);
            Assert.Equal(Loc.T("Login_Error_Timeout"), vm.Error);
            Assert.Contains("did not answer within", sink.All());
            Assert.False(auth.IsLoggedIn);
        }
        finally
        {
            Serilog.Log.Logger = previous;
            blackhole.Stop();
        }
    }

    private static int DeadPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();   // nothing listens from here on
        return port;
    }

    private static Services.LauncherAuthService RealAuth(Serilog.ILogger log, Services.Platform.IAppPaths paths, int port) =>
        new(new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            new StubConfig($"http://127.0.0.1:{port}"), new Services.FileTokenStore(paths, log), log);

    // ── Service doubles for the real step list ───────────────────────────────────────────────────

    /// <summary>Serilog sink that keeps every rendered line (message + exception) for inspection.</summary>
    private sealed class ListSink : Serilog.Core.ILogEventSink
    {
        public readonly List<string> Lines = [];
        public void Emit(Serilog.Events.LogEvent e)
        {
            lock (Lines) Lines.Add(e.RenderMessage() + " " + (e.Exception?.ToString() ?? ""));
        }
        public string All() { lock (Lines) return string.Join("\n", Lines); }
    }

    private static (Serilog.ILogger log, ListSink sink) CapturingLogger()
    {
        var sink = new ListSink();
        var log = new Serilog.LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (log, sink);
    }

    private sealed class TempPaths : Services.Platform.IAppPaths, IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "mechagon-login-test-" + Guid.NewGuid().ToString("N"));
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
        public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }
    }

    private sealed class ScriptedLauncherAuth : Services.ILauncherAuthService
    {
        public Services.LoginOutcome Next { get; set; } = Services.LoginOutcome.Failure("test");
        public bool IsLoggedIn => false;
        public string? CurrentToken => null;
        public string? CurrentAccount => null;
        public Task<Services.LoginOutcome> LoginAsync(string u, string p, CancellationToken ct = default) =>
            Task.FromResult(Next);
        public Task<Services.LoginOutcome> RegisterAsync(Services.RegisterRequest r, CancellationToken ct = default) =>
            Task.FromResult(Services.LoginOutcome.Failure("not used"));
        public void Logout() { }
    }

    private sealed class StubConfig(string? apiBase = null) : Services.IConfigService
    {
        public LauncherConfig Load() => new() { AccountApiBaseUrl = apiBase ?? "" };
        public void Save(LauncherConfig config) { }
        public bool LastSaveSucceeded => true;
    }

    private sealed class StubManifest : Services.IManifestService
    {
        public Task<ServerManifest?> FetchAsync(CancellationToken ct = default) => Task.FromResult<ServerManifest?>(null);
        public Task<ServerManifest?> FetchLauncherManifestAsync(CancellationToken ct = default) => Task.FromResult<ServerManifest?>(null);
        public Task<ClientFileManifest?> FetchFileManifestAsync(string url, CancellationToken ct = default) => Task.FromResult<ClientFileManifest?>(null);
    }

    private sealed class StubUpdate : Services.IUpdateService
    {
        public event EventHandler? LauncherUpdateStarting { add { } remove { } }
        public Task<bool> CheckAndApplyAsync(ServerManifest? m, CancellationToken ct = default) => Task.FromResult(false);
        public Services.LauncherUpdateNotice? CheckForNotice(ServerManifest? m) => null;
    }

    private sealed class StubStatus : Services.IServerStatusService
    {
        public Task<Services.ServerStatusResult> CheckAsync(string host, int port = 3724, CancellationToken ct = default) =>
            Task.FromResult(new Services.ServerStatusResult { Online = false, PlayerCount = 0 });
    }

    private sealed class StubClient : Services.IClientService
    {
        public string? FindWowExe(string? configuredPath = null) => null;
        public string? FindWowExeForBuild(int gameBuild, IReadOnlyDictionary<int, string> installs) => null;
        public IReadOnlyDictionary<int, string> DetectInstalls(IReadOnlyDictionary<int, string> known) => new Dictionary<int, string>();
        public int? DetectBuild(string wowDirectory) => null;
        public bool IsGameRunning() => false;
        public void SetRealmlist(string wowDirectory, string realmlistAddress) { }
        public void ConfigureClient(string wowDirectory, string locale, string realmlistAddress) { }
        public Task<Services.Platform.GameLaunchResult> LaunchAsync(string wowExePath) => Task.FromResult(new Services.Platform.GameLaunchResult(false, null, "test"));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met within 5 s");
            await Task.Delay(5);
        }
    }
}
