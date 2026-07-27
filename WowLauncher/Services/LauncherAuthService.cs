namespace WowLauncher.Services;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using WowLauncher.Models;
using WowLauncher.Localization;

/// <summary>
/// Real launcher auth against the web app. <c>POST {api}/launcher/login {username,password}</c> ->
/// <c>{ token, account:{id,username}, expiresAt }</c>. The base URL is derived from the active realm
/// exactly like <see cref="ServerStatusService"/> (see <see cref="ApiEndpoints"/>).
///
/// <para>Offline-first and honest about failure: every distinct server signal (401/429/503) becomes a
/// specific, calm English line; anything else (network down, malformed body) degrades to a generic
/// retry line and NEVER throws to the UI. The password reaches only the request body; the token is
/// held in memory and persisted via <see cref="ITokenStore"/>. Neither is ever logged.</para>
/// </summary>
public sealed class LauncherAuthService : ILauncherAuthService
{
    private readonly HttpClient _http;
    private readonly IConfigService _config;
    private readonly ITokenStore _store;
    private readonly Serilog.ILogger _log;

    private LauncherSession? _session;

    public LauncherAuthService(HttpClient http, IConfigService config, ITokenStore store, Serilog.ILogger log)
    {
        _http = http;
        _config = config;
        _store = store;
        _log = log;

        // Restore a previous session so a returning player is already signed in. Drop it if expired.
        var restored = _store.Load();
        if (restored is not null && !IsExpired(restored)) _session = restored;
        else if (restored is not null) _store.Clear();
    }

    public bool IsLoggedIn => _session is not null && !IsExpired(_session);
    public string? CurrentToken => IsLoggedIn ? _session!.Token : null;
    public string? CurrentAccount => IsLoggedIn ? _session!.Username : null;

    public async Task<LoginOutcome> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var url = $"{ApiEndpoints.Base(_config)}/launcher/login";
        try
        {
            var body = JsonSerializer.Serialize(
                new LoginRequestDto { Username = username, Password = password },
                FriendsApiJsonContext.Default.LoginRequestDto);

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            // Specific server signals -> specific, calm lines (VOICE.md: no em dashes, no apostrophes).
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return LoginOutcome.Failure(Loc.T("Login_Error_Invalid"));
            if ((int)resp.StatusCode == 429)
                return LoginOutcome.Failure(Loc.T("Login_Error_RateLimit"));
            if (resp.StatusCode == HttpStatusCode.ServiceUnavailable)
                return LoginOutcome.Failure(Loc.T("Login_Error_Unavailable"));
            if (!resp.IsSuccessStatusCode)
                return LoginOutcome.Failure(Loc.T("Login_Error_Generic"));

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize(json, FriendsApiJsonContext.Default.LoginResponseDto);
            if (dto is null || string.IsNullOrEmpty(dto.Token) || string.IsNullOrEmpty(dto.Account?.Username))
                return LoginOutcome.Failure(Loc.T("Login_Error_BadResponse"));

            var session = new LauncherSession(dto.Token, dto.Account.Id, dto.Account.Username, dto.ExpiresAt);
            _session = session;
            _store.Save(session);
            _log.Information("Launcher sign-in succeeded"); // no token, no username, no password
            return LoginOutcome.Success(session.Username);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the CALLER cancelled — not a failure to report to the player
        }
        catch (OperationCanceledException ex)
        {
            // Nobody cancelled: the HttpClient timeout fired (TaskCanceledException derives from
            // OperationCanceledException). Rethrowing here escaped the AsyncRelayCommand as an
            // unhandled exception and the player saw a cleared password field with no explanation.
            // A server that does not answer is its own failure mode, distinct from "cannot connect".
            _log.Warning(ex, "Launcher sign-in timed out waiting for the server");
            return LoginOutcome.Failure(Loc.T("Login_Error_Timeout"));
        }
        // A malformed/unexpected response body is NOT a connectivity problem — saying "could not
        // reach the server" here sent us hunting a network fault while the real cause was a contract
        // mismatch (expiresAt arrived as a number, the DTO wanted a string). Keep the two apart.
        catch (JsonException ex)
        {
            _log.Warning(ex, "Launcher sign-in: server response could not be parsed");
            return LoginOutcome.Failure(Loc.T("Login_Error_BadResponse"));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Launcher sign-in request failed (network or server error)");
            return LoginOutcome.Failure(Loc.T("Login_Error_Network"));
        }
    }

    public async Task<LoginOutcome> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var url = $"{ApiEndpoints.Base(_config)}/launcher/register";
        try
        {
            var body = JsonSerializer.Serialize(
                new RegisterRequestDto
                {
                    Username = request.Username,
                    Email = request.Email,
                    Password = request.Password,
                    Confirm = request.Confirm,
                    Terms = request.AcceptRules,
                    Newsletter = request.Newsletter,
                },
                FriendsApiJsonContext.Default.RegisterRequestDto);

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            // Map by status to a local VOICE-compliant line, mirroring LoginAsync. The server also sends
            // a { error, message } body, but its validation copy carries en dashes (8-20 characters), so
            // the raw message is deliberately NOT shown - the status is enough to pick a clean line.
            if ((int)resp.StatusCode == 409)
                return LoginOutcome.Failure(Loc.T("Register_Error_Conflict"));
            if ((int)resp.StatusCode == 429)
                return LoginOutcome.Failure(Loc.T("Register_Error_RateLimit"));
            if (resp.StatusCode == HttpStatusCode.ServiceUnavailable)
                return LoginOutcome.Failure(Loc.T("Register_Error_Unavailable"));
            if (resp.StatusCode == HttpStatusCode.BadRequest)
                return LoginOutcome.Failure(Loc.T("Register_Error_Invalid"));
            if (!resp.IsSuccessStatusCode)
                return LoginOutcome.Failure(Loc.T("Register_Error_Generic"));

            // Success shape is identical to login (token/account/expiresAt) - so is the auto-sign-in.
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize(json, FriendsApiJsonContext.Default.LoginResponseDto);
            if (dto is null || string.IsNullOrEmpty(dto.Token) || string.IsNullOrEmpty(dto.Account?.Username))
                return LoginOutcome.Failure(Loc.T("Register_Error_BadResponse"));

            var session = new LauncherSession(dto.Token, dto.Account.Id, dto.Account.Username, dto.ExpiresAt);
            _session = session;
            _store.Save(session);
            _log.Information("Launcher account created and signed in"); // no token, no username, no password
            return LoginOutcome.Success(session.Username);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the CALLER cancelled - not a failure to report to the player
        }
        catch (OperationCanceledException ex)
        {
            _log.Warning(ex, "Launcher account creation timed out waiting for the server");
            return LoginOutcome.Failure(Loc.T("Register_Error_Timeout"));
        }
        catch (JsonException ex)
        {
            _log.Warning(ex, "Launcher account creation: server response could not be parsed");
            return LoginOutcome.Failure(Loc.T("Register_Error_BadResponse"));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Launcher account creation request failed (network or server error)");
            return LoginOutcome.Failure(Loc.T("Register_Error_Network"));
        }
    }

    public void Logout()
    {
        _session = null;
        _store.Clear();
        _log.Information("Launcher signed out");
    }

    /// <summary>Expired when <c>expiresAt</c> parses to a past instant. Unset or unparseable is treated
    /// as not-expired (lenient): the server is the real gate and will 401 an actually-dead token.</summary>
    private static bool IsExpired(LauncherSession session)
    {
        if (session.ExpiresAt <= 0) return false; // unknown -> lenient, the server is the real gate
        return DateTimeOffset.FromUnixTimeMilliseconds(session.ExpiresAt) <= DateTimeOffset.UtcNow;
    }
}

/// <summary>Auth stand-in for <c>--demo</c>: always signed in with no real token, so the v3 friends
/// sidebar renders (against the in-memory Mock friends service) on a machine with no credentials and
/// no backend. Never used outside demo mode.</summary>
public sealed class DemoLauncherAuthService : ILauncherAuthService
{
    public bool IsLoggedIn => true;
    public string? CurrentToken => null;
    public string? CurrentAccount => "Stonetavern";
    public Task<LoginOutcome> LoginAsync(string username, string password, CancellationToken ct = default) =>
        Task.FromResult(LoginOutcome.Success("Stonetavern"));
    public Task<LoginOutcome> RegisterAsync(RegisterRequest request, CancellationToken ct = default) =>
        Task.FromResult(LoginOutcome.Success("Stonetavern"));
    public void Logout() { /* demo stays signed in */ }
}
