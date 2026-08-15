using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowLauncher.Services;
using WowLauncher.Localization;

namespace WowLauncher.ViewModels;

/// <summary>
/// The launcher sign-in form (username + password). Thin: it forwards the two fields to
/// <see cref="ILauncherAuthService"/> and reflects the outcome. On success it raises
/// <see cref="SignedIn"/> so the shell can flip the sidebar to the friends list and start polling.
///
/// <para>The password lives only in <see cref="Password"/> while the request is in flight and is
/// cleared after every attempt; it is never logged and never persisted (only the returned token is,
/// via the auth service). Busy state comes from the generated <c>AsyncRelayCommand</c>
/// (<c>SignInCommand.IsRunning</c>), so the button cannot be double-fired.</para>
/// </summary>
public sealed partial class LoginViewModel : ViewModelBase
{
    private readonly ILauncherAuthService _auth;

    /// <summary>Raised once, on the UI thread, after a successful sign-in OR a successful account
    /// creation (which auto-signs in through the same token path). The shell subscribes once here and
    /// gets both, so registration ends in the exact signed-in state a login does.</summary>
    public event Action? SignedIn;

    /// <summary>The "create account" form, shown in place of the sign-in fields when
    /// <see cref="IsRegisterMode"/> is true. Constructed here (not via DI) so both places that host
    /// <c>LoginView</c> reach it as <c>Login.Register</c> with no extra wiring. A successful
    /// registration is bridged onto <see cref="SignedIn"/> so the shell needs to watch only one event.</summary>
    public RegisterViewModel Register { get; }

    /// <summary>Woher die Adresse der Website kommt. Optional, weil beide Testhosts diese View
    /// ohne Konfiguration bauen; ohne sie führt der Link auf die ausgelieferte Standardadresse.</summary>
    private readonly IConfigService? _config;

    public LoginViewModel(ILauncherAuthService auth, IConfigService? config = null)
    {
        _auth = auth;
        _config = config;
        Register = new RegisterViewModel(auth);
        // Both VMs are process-lifetime singletons (the login VM is a DI singleton, Register lives as
        // long as it), so the subscription needs no teardown.
        Register.Registered += () => SignedIn?.Invoke();
    }

    /// <summary>Which face the card shows: false = sign in (default), true = create account.</summary>
    [ObservableProperty] private bool _isRegisterMode;

    /// <summary>Flip to the create-account form. Clears any stale sign-in error so the two faces do not
    /// carry each other's messages.</summary>
    [RelayCommand] private void ShowRegister() { Error = null; IsRegisterMode = true; }

    // ── "Passwort vergessen" ───────────────────────────────────────────────────────────────────
    //
    // 🔴 Was hier NICHT gebaut wird, und warum das die richtige Entscheidung ist.
    //
    // Am 2026-08-05 stand in unserer eigenen Feature-Liste, es gebe kein "Passwort vergessen", und
    // das galt als der eine Punkt, der einen Spieler komplett aussperrt. Eine kontextfreie
    // Zweitinstanz hatte es gefunden, ich hatte es uebernommen, und beide hatten dieselbe Quelle
    // angesehen: ILauncherAuthService kennt Login, Register, Logout und sonst nichts.
    //
    // Nachgesehen, bevor gebaut wurde: die Website hat den Reset LAENGST, vollstaendig -
    // /forgot-password, /reset-password, Token-Fluss, Ratenbegrenzung auf IP UND Konto
    // (apps/web/app/actions/password-reset.ts). Niemand war je ausgesperrt. Es fehlte der WEG
    // dorthin aus dem Launcher.
    //
    // Deshalb ein Link und keine zweite Implementierung. Ein eigener Reset-Pfad im Launcher waere
    // ein zweiter Weg an dieselben Konto-Daten, mit eigener Ratenbegrenzung, eigenem Token-Umgang
    // und eigenen Fehlern - also doppelt so viele Stellen, an denen ein Konto-Uebernahmefehler
    // entstehen kann, fuer null zusaetzliche Faehigkeit. Der Browser hat ausserdem etwas, das der
    // Launcher nicht hat: eine Adresszeile, an der ein Spieler sieht, wem er sein neues Passwort
    // gibt.

    /// <summary>Die Seite, auf der ein Spieler sein Passwort zuruecksetzt. Aus der Konfiguration, weil
    /// ein eigener Realm eine eigene Website haben kann - mit der ausgelieferten Adresse als
    /// Rueckfall, damit der Link nie ins Leere zeigt.</summary>
    public string ForgotPasswordUrl
    {
        get
        {
            var site = _config?.Load().SiteBaseUrl?.Trim();
            if (string.IsNullOrWhiteSpace(site)) site = "https://stonetavern.app";
            return site.TrimEnd('/') + "/forgot-password";
        }
    }

    [RelayCommand]
    private void ForgotPassword()
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(ForgotPasswordUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Kein Standardbrowser, oder headless. Der Spieler bekommt die Adresse zu lesen, statt
            // auf einen Knopf zu druecken, der nichts tut.
            Serilog.Log.Warning(ex, "Could not open the password reset page");
            Error = Loc.F("Login_Forgot_OpenFailed", ForgotPasswordUrl);
        }
    }

    /// <summary>Flip back to the sign-in form, clearing any create-account error.</summary>
    [RelayCommand] private void ShowSignIn() { Register.Error = null; IsRegisterMode = false; }

    [ObservableProperty] private string _username = "";

    // Bound to the password field. Transient-only: cleared after each attempt (below). Never logged.
    [ObservableProperty] private string _password = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    [RelayCommand]
    private async Task SignInAsync()
    {
        Error = null;

        var user = Username?.Trim() ?? "";
        var pass = Password ?? "";
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            Error = Loc.T("Login_Error_Empty");
            return;
        }

        // No ConfigureAwait(false): the continuation assigns bound properties, so it must stay on the
        // UI thread (avalonia-desktop skill). The service itself hops off the UI thread internally.
        var outcome = await _auth.LoginAsync(user, pass);

        Password = ""; // never keep the password around, whatever the result
        if (outcome.Ok)
        {
            Username = "";
            Error = null;
            SignedIn?.Invoke();
        }
        else
        {
            Error = outcome.Error;
        }
    }
}
