namespace WowLauncher.Services;

/// <summary>Result of a sign-in attempt. On success carries the account name to greet; on failure a
/// short, VOICE-compliant English message (no em dashes, no apostrophes) safe to show the player.</summary>
public sealed record LoginOutcome(bool Ok, string? AccountName, string? Error)
{
    public static LoginOutcome Success(string accountName) => new(true, accountName, null);
    public static LoginOutcome Failure(string error) => new(false, null, error);
}

/// <summary>The fields a new-account request carries. The launcher does light client-side checks
/// (nothing empty, the two passwords match, the rules are accepted) before sending; the web server is
/// the authoritative gate for name/email format, password strength, duplicates and rate limiting.</summary>
public sealed record RegisterRequest(
    string Username,
    string Email,
    string Password,
    string Confirm,
    bool AcceptRules,
    bool Newsletter);

/// <summary>
/// The launcher sign-in: exchanges the player username + password for a short-lived bearer token via
/// <c>POST /api/launcher/login</c> (SRP6 verified server-side), persists that token through
/// <see cref="ITokenStore"/>, and hands the bearer to the friends service. The password is used only
/// for the request body and is never stored, never logged.
/// </summary>
public interface ILauncherAuthService
{
    /// <summary>True while a non-expired session is held.</summary>
    bool IsLoggedIn { get; }

    /// <summary>The bearer token for API calls, or null when signed out. Consumed by
    /// <see cref="HttpFriendsPresenceService"/> — never rendered, never logged.</summary>
    string? CurrentToken { get; }

    /// <summary>The signed-in account username, or null when signed out.</summary>
    string? CurrentAccount { get; }

    Task<LoginOutcome> LoginAsync(string username, string password, CancellationToken ct = default);

    /// <summary>Create a new account via <c>POST /api/launcher/register</c> and, on success, sign the
    /// player straight in (the returned bearer token is stored exactly like a login, so the caller ends
    /// in the same signed-in state). Confirming the email is not required to play. Offline-first and
    /// honest about failure, same contract as <see cref="LoginAsync"/>: every distinct server signal
    /// becomes a calm English line and nothing throws to the UI.</summary>
    Task<LoginOutcome> RegisterAsync(RegisterRequest request, CancellationToken ct = default);

    void Logout();
}
