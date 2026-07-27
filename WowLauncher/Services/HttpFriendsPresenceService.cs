namespace WowLauncher.Services;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WowLauncher.Models;
using WowLauncher.Localization;

/// <summary>
/// The real friends/presence source: maps the web-app contract (HANDOFF-friends-api.md) onto the
/// UI-facing <see cref="FriendPresence"/>. The displayed identity is ALWAYS <c>account.username</c> —
/// never a character name (owner directive 2026-07-20). Presence collapses to exactly three live
/// states plus offline (see <see cref="Map"/>).
///
/// <para><b>Offline-first.</b> Not signed in, HTTP error, timeout or malformed body all yield an empty
/// or degraded list — never a throw and never a crash, matching the rest of the launcher. The bearer
/// is pulled from <see cref="ILauncherAuthService"/> per request (the client is long-lived and shared,
/// the token changes on sign-in/out), and is never logged.</para>
/// </summary>
public sealed class HttpFriendsPresenceService : IFriendsPresenceService
{
    private readonly HttpClient _http;
    private readonly IConfigService _config;
    private readonly ILauncherAuthService _auth;
    private readonly Serilog.ILogger _log;

    public HttpFriendsPresenceService(HttpClient http, IConfigService config, ILauncherAuthService auth, Serilog.ILogger log)
    {
        _http = http;
        _config = config;
        _auth = auth;
        _log = log;
    }

    public async Task<IReadOnlyList<FriendPresence>> GetFriendsAsync(CancellationToken ct = default)
    {
        var token = _auth.CurrentToken;
        if (!_auth.IsLoggedIn || string.IsNullOrEmpty(token))
            return Array.Empty<FriendPresence>(); // signed out -> nothing to show, no request

        var url = $"{ApiEndpoints.Base(_config)}/friends";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.Warning("Friends fetch returned {Status} — showing a degraded list", (int)resp.StatusCode);
                return Array.Empty<FriendPresence>();
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dtos = JsonSerializer.Deserialize(json, FriendsApiJsonContext.Default.FriendDtoArray);
            if (dtos is null) return Array.Empty<FriendPresence>();

            var list = new List<FriendPresence>(dtos.Length);
            foreach (var dto in dtos)
            {
                var friend = Map(dto);
                if (friend is not null) list.Add(friend);
            }
            return list;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the CALLER (poller) cancelled — not an error state
        }
        catch (OperationCanceledException ex)
        {
            // Timeout, not a cancel: rethrowing it escaped the fire-and-forget poll as an unhandled
            // exception. A server that does not answer degrades the list, like any other fetch failure.
            _log.Warning(ex, "Friends fetch timed out — showing a degraded list");
            return Array.Empty<FriendPresence>();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Friends fetch failed — showing a degraded list");
            return Array.Empty<FriendPresence>();
        }
    }

    public async Task<AddFriendResult> AddFriendAsync(string account, CancellationToken ct = default)
    {
        var name = account?.Trim();
        if (string.IsNullOrEmpty(name))
            return AddFriendResult.Rejected(Loc.T("Friends_Error_EmptyName"));

        var token = _auth.CurrentToken;
        if (!_auth.IsLoggedIn || string.IsNullOrEmpty(token))
            return AddFriendResult.Rejected(Loc.T("Friends_Error_SignIn"));

        var url = $"{ApiEndpoints.Base(_config)}/friends";
        try
        {
            var body = JsonSerializer.Serialize(
                new AddFriendRequestDto { TargetUsername = name },
                FriendsApiJsonContext.Default.AddFriendRequestDto);

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return AddFriendResult.Rejected(Loc.T("Friends_Error_NoAccount"));
            if (resp.StatusCode == HttpStatusCode.Conflict)
                return AddFriendResult.Rejected(Loc.T("Friends_Error_Duplicate"));
            if (!resp.IsSuccessStatusCode)
                return AddFriendResult.Rejected(Loc.T("Friends_Error_AddFailed"));

            // 200/201 -> a pending request now exists. Show it optimistically (offline, "Pending")
            // until the next poll returns the confirmed row from the server.
            return AddFriendResult.Added(new FriendPresence(name, PresenceStatus.Offline, "Pending", null));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the CALLER cancelled
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient timeout. The request may or may not have reached the server, so the line says
            // exactly that instead of claiming the account was not added.
            _log.Warning(ex, "Add friend request timed out");
            return AddFriendResult.Rejected(Loc.T("Friends_Error_Timeout"));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Add friend request failed");
            return AddFriendResult.Rejected(Loc.T("Friends_Error_Network"));
        }
    }

    /// <summary>
    /// Contract -> <see cref="FriendPresence"/>. The only three states the server can actually produce:
    /// <list type="bullet">
    /// <item><c>presence.online == false</c> (incl. every <c>pending</c> row) -> <see cref="PresenceStatus.Offline"/>.</item>
    /// <item><c>online</c> and a non-empty <c>realm</c> -> <see cref="PresenceStatus.InGame"/>.</item>
    /// <item><c>online</c> with no <c>realm</c> (in the launcher, not in a world) -> <see cref="PresenceStatus.Online"/>.</item>
    /// </list>
    /// <see cref="PresenceStatus.Away"/> / <see cref="PresenceStatus.Busy"/> are NOT invented here — the
    /// server has no such flag yet (they remain reserved for a future status field, and are still used
    /// by the Mock for demo variety). A row with no username is dropped (no identity to show).
    /// </summary>
    internal static FriendPresence? Map(FriendDto dto)
    {
        var account = dto.Account?.Username;
        if (string.IsNullOrWhiteSpace(account)) return null;

        var presence = dto.Presence;
        var online = presence?.Online ?? false;
        var realm = presence?.Realm;

        var status = !online ? PresenceStatus.Offline
            : string.IsNullOrEmpty(realm) ? PresenceStatus.Online
            : PresenceStatus.InGame;

        var activity = status == PresenceStatus.Offline ? "" : presence?.Activity ?? "";
        return new FriendPresence(account, status, activity, realm);
    }
}
