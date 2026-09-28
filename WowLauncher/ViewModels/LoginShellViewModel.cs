using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowLauncher.Localization;
using WowLauncher.Services;
using WowLauncher.Startup;

namespace WowLauncher.ViewModels;

/// <summary>The phases of Spec 2026-09-20 §2, as far as the login shell owns them.</summary>
public enum LoginPhase
{
    /// <summary>Phase 0: first frame. Gradient, sigil at rest, nothing else.</summary>
    ColdStart,

    /// <summary>Phase 1: the card is up, the pipeline runs behind it.</summary>
    Login,

    /// <summary>Phase 2: a sign-in is in flight.</summary>
    Authenticating,

    /// <summary>Phase 2, accepted: the sigil pulses once (Motion.Base); the View then begins Phase 3.</summary>
    Succeeded,

    /// <summary>Phase 3: the 450 ms storyboard towards the shell.</summary>
    Transition,

    /// <summary>The shell owns the desktop; this window is done.</summary>
    Done,

    /// <summary>The self-update swap started; the process ends.</summary>
    Halted,
}

/// <summary>One row of the realm status inside the card: a skeleton until the probe resolved.</summary>
public sealed partial class RealmStatusRow : ViewModelBase
{
    public RealmStatusRow(string name) { Name = name; }

    public string Name { get; }

    [ObservableProperty] private bool _isResolved;
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private string _detail = "";

    /// <summary>Online with a count ("61 online") only when the realm is ours and the count was read;
    /// a foreign server, or a count that could not be read, says just "online".</summary>
    public void Resolve(bool online, int? players)
    {
        IsOnline = online;
        Detail = !online ? Loc.T("Login_Shell_Realm_Offline")
            : players is { } n ? Loc.F("Login_Shell_Realm_Online", n)
            : Loc.T("Login_Shell_Realm_OnlineNoCount");
        IsResolved = true;
    }
}

/// <summary>
/// The login screen that is also the loading screen (Spec §1). Owns the phase, the two fields, the
/// status row, the realm rows and the outcome of a sign-in. Knows no window: the Phase-3 storyboard
/// is the View's (it reads the motion tokens), the View calls <see cref="CompleteTransition"/> when
/// the storyboard has run.
///
/// <para><b>Threading.</b> Pipeline reports arrive on worker threads and are posted to the UI thread
/// through <c>_post</c>; every bound property is touched there and only there. Tests pass a
/// straight-through post.</para>
///
/// <para><b>The password</b> lives in <see cref="Password"/> while an attempt is in flight and is
/// cleared after every attempt, success or not. Never logged, never persisted. The player path talks
/// to the launcher sign-in through <see cref="LauncherAuthGateway"/> (Spec §12.1 A); the QA render
/// harness seeds <see cref="FakeAuthGateway"/> instead.</para>
/// </summary>
public sealed partial class LoginShellViewModel : ViewModelBase
{
    private readonly InitPipeline _pipeline;
    private readonly InitFacts _facts;
    private readonly IAuthGateway _auth;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _pipelineCts = new();
    private CancellationTokenSource? _authCts;

    public LoginShellViewModel(InitPipeline pipeline, InitFacts facts, IAuthGateway auth,
                               bool allowSkipSignIn = false, Action<Action>? post = null,
                               IUsernameMemory? remembered = null)
    {
        _pipeline = pipeline;
        _facts = facts;
        _auth = auth;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        AllowSkipSignIn = allowSkipSignIn;
        _remembered = remembered;
        var rememberedName = remembered?.Load();
        Username = rememberedName ?? "";
        RememberUsername = rememberedName is not null;

        // Rows exist as skeletons from the first frame; step 3 fills them in place (no layout jump).
        foreach (var name in InitialRealmNames())
            Realms.Add(new RealmStatusRow(name));

        _pipeline.StepChanged += OnStepChanged;
        _pipeline.HaltRequested += OnHaltRequested;
    }

    /// <summary>Realm names for the skeleton rows before step 3 answered. The registry's shipped
    /// entries are known without I/O, so the row count is right from the first frame.</summary>
    private static string[] InitialRealmNames() =>
        Models.RealmRegistry.Presets().Select(r => r.Name).ToArray();

    // ── Phase ────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsColdStart))]
    [NotifyPropertyChangedFor(nameof(IsAuthenticating))]
    [NotifyPropertyChangedFor(nameof(IsSucceeded))]
    [NotifyPropertyChangedFor(nameof(IsTransitioning))]
    [NotifyPropertyChangedFor(nameof(FieldsReadOnly))]
    [NotifyPropertyChangedFor(nameof(IsButtonBusy))]
    [NotifyPropertyChangedFor(nameof(ButtonBusyText))]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    [NotifyCanExecuteChangedFor(nameof(ContinueOfflineCommand))]
    private LoginPhase _phase = LoginPhase.ColdStart;

    public bool IsColdStart => Phase == LoginPhase.ColdStart;
    public bool IsAuthenticating => Phase == LoginPhase.Authenticating;
    public bool IsSucceeded => Phase == LoginPhase.Succeeded;
    public bool IsTransitioning => Phase is LoginPhase.Transition or LoginPhase.Done;

    /// <summary>§6: during the attempt the fields go read-only, not disabled (colour stays legible).</summary>
    public bool FieldsReadOnly => Phase != LoginPhase.Login;

    /// <summary>Raised on the UI thread once the Phase-3 storyboard has run: the shell may be shown.</summary>
    public event Action? TransitionCompleted;

    /// <summary>Raised on the UI thread when the self-update swap started and the process must end.</summary>
    public event Action? HaltRequested;

    /// <summary>The View calls this after the first frame was rendered: the sigil warms up, the
    /// pipeline starts. Nothing before that touches the network (§4).</summary>
    public void BeginPhase1()
    {
        if (Phase != LoginPhase.ColdStart) return;
        Phase = LoginPhase.Login;
        _ = _pipeline.RunAsync(_pipelineCts.Token);
    }

    // ── Status row (§5.2) ────────────────────────────────────────────────────────────────────────

    /// <summary>The one sentence under the card. Every change is a crossfade in the View.</summary>
    [ObservableProperty] private string _statusText = Loc.T("Init_Status_Config");

    /// <summary>True once step 3 answered "offline" for a shipped realm (§5.3 degraded state).</summary>
    [ObservableProperty] private bool _isRealmUnreachable;

    /// <summary>§5.3: a notify-only launcher update. Banner above the card, fades in over Motion.Slow.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateBanner))]
    private string? _updateBanner;

    public bool HasUpdateBanner => !string.IsNullOrEmpty(UpdateBanner);

    public ObservableCollection<RealmStatusRow> Realms { get; } = [];

    /// <summary>§5.2: the login button is active only once steps 1 to 3 completed. Until then it shows
    /// the micro spinner instead of a bare disabled state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGateClosed))]
    [NotifyPropertyChangedFor(nameof(IsButtonBusy))]
    [NotifyPropertyChangedFor(nameof(ButtonBusyText))]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    [NotifyCanExecuteChangedFor(nameof(ContinueOfflineCommand))]
    private bool _isGateOpen;

    public bool IsGateClosed => !IsGateOpen;

    /// <summary>True while a wait of UNKNOWN duration is on: the init pipeline (Phase 1) or the
    /// sign-in (Phase 2). Drives the area loader (Spec §1, §8.2: no spinner where progress is
    /// known, and here nothing has a percentage).</summary>
    [ObservableProperty] private bool _isWorking;

    private void UpdateIsWorking() =>
        IsWorking = (Phase == LoginPhase.Login && !_pipeline.IsComplete)
                    || Phase is LoginPhase.Authenticating or LoginPhase.Succeeded;

    /// <summary>The button shows the micro spinner while the gate is closed (§5.2) and while an
    /// attempt is in flight (§6). Same slot, same width, different sentence next to the spinner.</summary>
    public bool IsButtonBusy => IsGateClosed || IsAuthenticating || IsSucceeded;

    public string ButtonBusyText =>
        IsAuthenticating || IsSucceeded ? Loc.T("Login_SigningIn") : Loc.T("Login_Shell_GettingReady");

    /// <summary>How far the start checks are, 0 to 1: finished steps (done or failed) over all steps.
    /// Drives the ring around the lantern, which fills instead of spinning. Honest by construction:
    /// it only moves when a step really ends, never on a timer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInitComplete))]
    private double _initProgress;

    /// <summary>Every start check has ended; the ring rests.</summary>
    public bool IsInitComplete => InitProgress >= 1;

    private void OnStepChanged(InitStepReport report) => _post(() => ApplyStep(report));

    private void ApplyStep(InitStepReport report)
    {
        if (report.State == InitStepState.Running)
            StatusText = report.StatusText;

        var steps = _pipeline.Steps;
        if (steps.Count > 0)
        {
            var settled = steps.Count(st => _pipeline.StateOf(st.Key) is InitStepState.Done or InitStepState.Failed);
            InitProgress = (double)settled / steps.Count;
        }

        if (report.Key == LauncherInitSteps.RealmKey && report.State is InitStepState.Done or InitStepState.Failed)
            ApplyRealmFacts();

        if (report.Key == LauncherInitSteps.UpdateKey && report.State == InitStepState.Done
            && _facts.UpdateNotice is { } notice)
            UpdateBanner = Loc.F("Login_Shell_UpdateBanner", notice.Version);

        if (_pipeline.IsLoginGateOpen && !IsGateOpen)
            IsGateOpen = true;

        if (_pipeline.IsComplete && !_pipeline.IsHalted)
            StatusText = IsRealmUnreachable ? Loc.T("Init_Status_RealmDown") : Loc.T("Init_Status_Ready");
        UpdateIsWorking();
    }

    private void ApplyRealmFacts()
    {
        var probes = _facts.Realms;
        if (probes.Count == 0)
        {
            // The step failed before any probe: every row resolves to offline rather than staying a
            // skeleton forever (a skeleton that never fills is the loading-state lie the spec bans).
            foreach (var row in Realms) row.Resolve(false, null);
            IsRealmUnreachable = true;
            return;
        }

        foreach (var probe in probes)
        {
            var row = Realms.FirstOrDefault(r => r.Name == probe.Name);
            if (row is null) { row = new RealmStatusRow(probe.Name); Realms.Add(row); }
            row.Resolve(probe.Online, probe.PlayerCount);
        }
        // "The realm did not answer" is a sentence about THE realm list as a whole. It used to fire when
        // ANY row was offline, so an install with one live realm (Stonetavern, "79 online") and three
        // private test realms that were switched off showed the list saying "online" under a status
        // line saying nobody answered (owner screenshot 2026-09-27). The rows already say which realm
        // is down; the status line only speaks up when not a single realm answered.
        IsRealmUnreachable = !probes.Any(p => p.Online);
    }

    private void OnHaltRequested(string statusText) => _post(() =>
    {
        StatusText = statusText;
        Phase = LoginPhase.Halted;
        HaltRequested?.Invoke();
    });

    // ── The card (§6) ────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _username = "";

    /// <summary>The "Remember username" box. Unticked unless a name is already remembered. The QA
    /// harness passes no memory, so screenshots never carry a real account name.</summary>
    [ObservableProperty] private bool _rememberUsername;

    private readonly IUsernameMemory? _remembered;

    [ObservableProperty] private string _password = "";

    /// <summary>Inline error under the password field. Null when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(IsFieldError))]
    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>True after a Timeout or Unavailable answer: the card offers the offline path (§6).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsOfflinePath))]
    [NotifyPropertyChangedFor(nameof(IsFieldError))]
    [NotifyCanExecuteChangedFor(nameof(ContinueOfflineCommand))]
    private bool _offersOfflinePath;

    /// <summary>Whether the offline link is on the card at all.</summary>
    public bool ShowsOfflinePath => OffersOfflinePath || AllowSkipSignIn;

    /// <summary>The password field wears the failure hairline only when the FIELD is at fault (empty
    /// or rejected). A timeout or an unreachable service is not the password's fault.</summary>
    public bool IsFieldError => HasError && !OffersOfflinePath;

    /// <summary>Spec §12.3 is open; this is the plug. When true the card always offers
    /// "Continue without signing in". Default false: the login is the way in.</summary>
    public bool AllowSkipSignIn { get; }

    /// <summary>Incremented on every rejected attempt. The View plays the shake on each tick;
    /// a counter instead of a bool so two rejections in a row both shake.</summary>
    [ObservableProperty] private int _shakeTick;

    /// <summary>Set once, on a rejected attempt, to move the focus back into the password field.
    /// The View watches it and resets it.</summary>
    [ObservableProperty] private bool _wantsPasswordFocus;

    private bool CanSignIn() => Phase == LoginPhase.Login && IsGateOpen;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        Error = null;
        OffersOfflinePath = false;

        var user = Username?.Trim() ?? "";
        var pass = Password ?? "";
        if (user.Length == 0 || pass.Length == 0)
        {
            Error = Loc.T("Login_Error_Empty");
            return;
        }

        Phase = LoginPhase.Authenticating;
        _authCts?.Dispose();
        _authCts = new CancellationTokenSource(AuthPolicy.Timeout);

        AuthResult result;
        try
        {
            // Stays on the UI thread after the await: bound properties are assigned below.
            result = await _auth.SignInAsync(user, pass, _authCts.Token);
        }
        catch (OperationCanceledException)
        {
            // The cut-off is ours (AuthPolicy.Timeout), not the server saying no. Logged without any
            // credential so a support report can tell this apart from a wrong password.
            Serilog.Log.Information("Sign-in did not answer within {Seconds:F0} s; offering the offline path",
                AuthPolicy.Timeout.TotalSeconds);
            result = AuthResult.Timeout(Loc.T("Login_Error_Timeout"));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Sign-in failed unexpectedly");
            result = AuthResult.Unavailable(Loc.T("Login_Error_Unavailable"));
        }
        finally
        {
            Password = "";   // never keep the password around, whatever the result
        }

        switch (result.Kind)
        {
            case AuthResultKind.Success:
                // Only a name the server accepted is remembered, never a typo or a failed attempt.
                if (RememberUsername) _remembered?.Save(user);
                else _remembered?.Clear();
                Error = null;
                Phase = LoginPhase.Succeeded;   // the View plays the pulse, then calls BeginTransition
                break;

            case AuthResultKind.Rejected:
                Phase = LoginPhase.Login;
                Error = result.Message;
                ShakeTick++;
                WantsPasswordFocus = true;
                break;

            default:
                // Timeout / Unavailable: own sentence, offline path offered, no modal (§6).
                Phase = LoginPhase.Login;
                Error = result.Message;
                OffersOfflinePath = true;
                break;
        }
    }

    private bool CanContinueOffline() =>
        Phase == LoginPhase.Login && IsGateOpen && (OffersOfflinePath || AllowSkipSignIn);

    /// <summary>The offline path: into the shell without a session, which is exactly what 1.8 does
    /// on every start. Shown only after a timeout/unavailable answer, or always when §12.3 says so.</summary>
    [RelayCommand(CanExecute = nameof(CanContinueOffline))]
    private void ContinueOffline()
    {
        Error = null;
        BeginTransition();
    }

    /// <summary>The View reports that the success pulse has played (or that it has none): Phase 3
    /// begins. Also the direct path for the offline link, which has no pulse.</summary>
    public void BeginTransition()
    {
        if (Phase is not (LoginPhase.Succeeded or LoginPhase.Login)) return;
        Phase = LoginPhase.Transition;
    }

    /// <summary>The View reports that the Phase-3 storyboard finished.</summary>
    public void CompleteTransition()
    {
        if (Phase != LoginPhase.Transition) return;
        Phase = LoginPhase.Done;
        TransitionCompleted?.Invoke();
    }

    /// <summary>The window is going away: stop the pipeline and any sign-in in flight.</summary>
    public void Shutdown()
    {
        _pipeline.StepChanged -= OnStepChanged;
        _pipeline.HaltRequested -= OnHaltRequested;
        _pipelineCts.Cancel();
        _authCts?.Cancel();
    }

    /// <summary>Generated hook of the Phase property: keep the loader flag in step.</summary>
    partial void OnPhaseChanged(LoginPhase value) => UpdateIsWorking();

    /// <summary>The running product version, same number the self-update compares.</summary>
    public string VersionText
    {
        get
        {
            var v = Services.UpdateService.RunningVersion(System.Reflection.Assembly.GetExecutingAssembly());
            return $"v{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }
}
