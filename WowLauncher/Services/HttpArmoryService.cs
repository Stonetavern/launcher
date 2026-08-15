namespace WowLauncher.Services;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WowLauncher.Models;

/// <summary>
/// The real armory source: <c>GET {base}/launcher/characters?realm={id}</c> with the launcher bearer,
/// mapped onto the UI-facing <see cref="ArmoryCharacter"/>.
///
/// <para>🔴 <b>The endpoint does not exist yet</b> (checked 2026-07-21: the web app serves
/// login + friends + account/online and nothing that returns character JSON). Until it ships, this
/// class answers <see cref="ArmoryStatus.Unavailable"/> on every call, which is exactly what it would
/// do against a 404 anyway - so wiring it in cannot break the app, and the demo mock keeps driving
/// the UI. The contract it expects is written down in
/// (internal design notes, not published); the DTOs carry the same warning.</para>
///
/// <para><b>Own characters only.</b> The bearer is the scope - no account id is ever sent, and the
/// launcher has no way to ask for somebody elses characters.</para>
///
/// <para><b>Offline-first.</b> Signed out, HTTP error, timeout or a malformed body all yield an empty
/// roster with a status, never a throw. The bearer is read from
/// <see cref="ILauncherAuthService"/> per request (the client is long-lived and shared, the token
/// changes on sign-in/out) and is never logged.</para>
/// </summary>
public sealed class HttpArmoryService : IArmoryService
{
    private readonly HttpClient _http;
    private readonly IConfigService _config;
    private readonly ILauncherAuthService _auth;
    private readonly Serilog.ILogger _log;

    public HttpArmoryService(HttpClient http, IConfigService config, ILauncherAuthService auth, Serilog.ILogger log)
    {
        _http = http;
        _config = config;
        _auth = auth;
        _log = log;
    }

    public async Task<ArmoryRoster> GetCharactersAsync(string realmId, CancellationToken ct = default)
    {
        var token = _auth.CurrentToken;
        var id = realmId ?? "";
        if (!_auth.IsLoggedIn || string.IsNullOrEmpty(token))
            return ArmoryRoster.Empty(ArmoryStatus.SignedOut, id); // no session -> no request

        var realm = Uri.EscapeDataString(realmId ?? "");
        var url = $"{ApiEndpoints.Base(_config)}/launcher/characters?realm={realm}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode is HttpStatusCode.Unauthorized)
                return ArmoryRoster.Empty(ArmoryStatus.SignedOut, id);
            if (!resp.IsSuccessStatusCode)
            {
                // The code goes to the PLAYER, not only to the log. "Not available" alone made a
                // realm that does not exist, a server that is down and an expired session look
                // identical on screen, and left the one person who could act on it nothing to act on.
                _log.Warning(
                    "Armory fetch for {Realm} returned {Status} - showing the quiet empty state",
                    id, (int)resp.StatusCode);
                return ArmoryRoster.Empty(ArmoryStatus.Unavailable, id, $"HTTP {(int)resp.StatusCode}");
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize(json, ArmoryApiJsonContext.Default.ArmoryResponseDto);
            if (dto?.Characters is null)
                return ArmoryRoster.Empty(ArmoryStatus.Unavailable, id, "unreadable answer");

            var list = new List<ArmoryCharacter>(dto.Characters.Length);
            foreach (var c in dto.Characters)
            {
                var mapped = Map(c);
                if (mapped is not null) list.Add(mapped with { RealmId = id });
            }
            return new ArmoryRoster(list, ArmoryStatus.Ok, id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the CALLER cancelled - not an error state
        }
        catch (OperationCanceledException ex)
        {
            _log.Warning(ex, "Armory fetch for {Realm} timed out - showing the quiet empty state", id);
            return ArmoryRoster.Empty(ArmoryStatus.Unavailable, id, "no answer in time");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Armory fetch for {Realm} failed - showing the quiet empty state", id);
            return ArmoryRoster.Empty(ArmoryStatus.Unavailable, id, "could not be reached");
        }
    }

    /// <summary>
    /// Wire DTO -> <see cref="ArmoryCharacter"/>. A row with no name is dropped (nothing to show).
    /// Missing display names degrade to an empty string rather than to a locally invented one: the
    /// launcher owns no class/race/zone tables, and printing "Unknown" everywhere would be worse than
    /// printing nothing.
    ///
    /// <para>🔴 <b>Named arguments are mandatory here, not a style preference.</b> The target record
    /// has seventeen members and several runs of neighbours with the same type: two <c>long</c>
    /// (money, played), four <c>string?</c> (guild, rank, zone, honor rank), three <c>int</c> (level,
    /// quests, score). Written positionally, swapping any two of them compiles, runs, renders a
    /// plausible number and colours no test red - which is precisely the failure this project already
    /// paid a day for. With the names written out, the same mistake is a visible lie on the line
    /// (<c>MoneyCopper: dto.PlayedTimeTotal</c>) instead of an invisible ordering slip. Do not
    /// "simplify" this back to positional form.</para>
    /// </summary>
    internal static ArmoryCharacter? Map(ArmoryCharacterDto dto)
    {
        if (dto is null || string.IsNullOrWhiteSpace(dto.Name)) return null;

        var lockouts = new List<ArmoryLockout>(dto.Lockouts?.Length ?? 0);
        foreach (var l in dto.Lockouts ?? [])
            lockouts.Add(ArmoryLockout.FromReset(l.MapName ?? "", l.ResetTime));

        return new ArmoryCharacter(
            Guid: dto.Guid,
            Name: dto.Name!,
            ClassId: dto.Class,
            ClassName: dto.ClassName ?? "",
            RaceName: dto.RaceName ?? "",
            Level: dto.Level,
            Online: dto.Online,
            Guild: dto.Guild,
            GuildRank: dto.GuildRank,
            Zone: dto.ZoneName,
            MoneyCopper: dto.Money,
            PlayedSeconds: dto.PlayedTimeTotal,
            QuestCount: dto.QuestCount,
            HonorRankName: dto.HonorRankName,
            Score: dto.Score,
            ScoreTier: dto.ScoreTier,
            Lockouts: lockouts);
    }
}
