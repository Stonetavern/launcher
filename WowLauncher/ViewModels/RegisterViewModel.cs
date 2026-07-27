using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowLauncher.Services;
using WowLauncher.Localization;

namespace WowLauncher.ViewModels;

/// <summary>
/// The launcher "create account" form: username, email, password, confirm, a required "accept the
/// server rules" checkbox and an optional newsletter opt-in. Thin, like <see cref="LoginViewModel"/> -
/// it does light client-side validation (nothing empty, the two passwords match, the rules accepted),
/// forwards the fields to <see cref="ILauncherAuthService.RegisterAsync"/>, and reflects the outcome.
///
/// <para>On success the auth service has already stored the returned bearer token, so the account is
/// signed in exactly as after a login; this VM raises <see cref="Registered"/> so the shell can flip
/// to the signed-in state through the very same path a fresh login uses. The password and confirm
/// values live only while the request is in flight and are cleared after every attempt; they are never
/// logged and never persisted. Busy state comes from the generated <c>AsyncRelayCommand</c>
/// (<c>CreateAccountCommand.IsRunning</c>), so the button cannot be double-fired.</para>
/// </summary>
public sealed partial class RegisterViewModel : ViewModelBase
{
    private readonly ILauncherAuthService _auth;

    /// <summary>Raised once, on the UI thread, after a successful account creation + auto sign-in.</summary>
    public event Action? Registered;

    public RegisterViewModel(ILauncherAuthService auth) => _auth = auth;

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _email = "";

    // Bound to the password fields. Transient-only: cleared after each attempt (below). Never logged.
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _confirm = "";

    // Required: the request is rejected client-side until this is ticked (the server enforces it too).
    [ObservableProperty] private bool _acceptRules;

    // Optional marketing opt-in; off by default.
    [ObservableProperty] private bool _newsletter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    [RelayCommand]
    private async Task CreateAccountAsync()
    {
        Error = null;

        var user = Username?.Trim() ?? "";
        var mail = Email?.Trim() ?? "";
        var pass = Password ?? "";
        var confirm = Confirm ?? "";

        // Client-side sofort-validation: cheap checks that spare a round trip and give an immediate,
        // precise line. The server still does the authoritative validation (format, strength, dupes).
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(mail)
            || string.IsNullOrEmpty(pass) || string.IsNullOrEmpty(confirm))
        {
            Error = Loc.T("Register_Error_Empty");
            return;
        }
        if (!AcceptRules)
        {
            Error = Loc.T("Register_Error_Rules");
            return;
        }
        if (pass != confirm)
        {
            Error = Loc.T("Register_Error_Mismatch");
            return;
        }

        // No ConfigureAwait(false): the continuation assigns bound properties, so it must stay on the
        // UI thread (avalonia-desktop skill). The service itself hops off the UI thread internally.
        var outcome = await _auth.RegisterAsync(
            new RegisterRequest(user, mail, pass, confirm, AcceptRules, Newsletter));

        // Never keep the secrets around, whatever the result.
        Password = "";
        Confirm = "";
        if (outcome.Ok)
        {
            Username = "";
            Email = "";
            AcceptRules = false;
            Newsletter = false;
            Error = null;
            Registered?.Invoke();
        }
        else
        {
            Error = outcome.Error;
        }
    }
}
