using System.Net.Sockets;
using System.Text.Json;
using WowLauncher.Models;

namespace WowLauncher.Services;

public interface IServerStatusService
{
    /// <summary>Reachability only: is the realm's login port answering. No player count.</summary>
    Task<ServerStatusResult> CheckAsync(string host, int port = 3724, CancellationToken ct = default);

    /// <summary>
    /// Reachability plus the number of players online, summed over <paramref name="countRealms"/>: the
    /// game realms behind this address (<see cref="Models.RealmEntry.ArmoryRealms"/>; Stonetavern is one
    /// login address with Elwynn and Barrens behind it). An empty list means "not ours, no count": a
    /// foreign server gets its up/down state and nothing else.
    ///
    /// <para>The default body keeps every test double that only knows the first overload compiling;
    /// it answers without a count.</para>
    /// </summary>
    Task<ServerStatusResult> CheckAsync(string host, IReadOnlyList<string> countRealms, CancellationToken ct = default)
        => CheckAsync(host, ct: ct);
}

public sealed class ServerStatusResult
{
    public bool Online { get; init; }

    /// <summary>Players online across the counted realms. Null when nothing was counted: a foreign
    /// server, or a Stonetavern realm whose count could not be read. Null is not zero and must not be
    /// shown as zero.</summary>
    public int? PlayerCount { get; init; }
}

public sealed class ServerStatusService : IServerStatusService
{
    private readonly HttpClient _http;
    private readonly Serilog.ILogger _log;

    // No config any more: the count follows the realm being probed, never the global realmlist.
    public ServerStatusService(HttpClient http, Serilog.ILogger log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Split a realmlist address into the host and port to probe. The address the launcher carries is
    /// the one the client writes into realmlist.wtf, and that form legally includes a port
    /// ("play.stonetavern.app:8085" — ClientService.IsValidRealmlistAddress accepts it). Handing the
    /// whole string to TcpClient.ConnectAsync as a HOST made every such realm resolve to nothing, so it
    /// was reported offline forever. Bracketed IPv6 ("[::1]", "[::1]:8085") is handled; a bare IPv6
    /// literal keeps the fallback port, because its colons are part of the address.
    /// </summary>
    internal static (string Host, int Port) SplitHostPort(string? address, int fallbackPort)
    {
        var s = (address ?? "").Trim();
        if (s.Length == 0) return (s, fallbackPort);

        if (s[0] == '[')
        {
            var end = s.IndexOf(']');
            if (end <= 0) return (s, fallbackPort);
            var v6 = s[1..end];
            if (end + 2 < s.Length && s[end + 1] == ':'
                && int.TryParse(s[(end + 2)..], out var p6) && p6 is > 0 and <= 65535)
                return (v6, p6);
            return (v6, fallbackPort);
        }

        var idx = s.LastIndexOf(':');
        if (idx > 0 && s.IndexOf(':') == idx
            && int.TryParse(s[(idx + 1)..], out var p) && p is > 0 and <= 65535)
            return (s[..idx], p);

        return (s, fallbackPort);
    }

    public Task<ServerStatusResult> CheckAsync(string host, int port = 3724, CancellationToken ct = default)
        => CheckCoreAsync(host, port, [], ct);

    public Task<ServerStatusResult> CheckAsync(string host, IReadOnlyList<string> countRealms, CancellationToken ct = default)
        => CheckCoreAsync(host, 3724, countRealms, ct);

    private async Task<ServerStatusResult> CheckCoreAsync(string host, int port, IReadOnlyList<string> countRealms,
                                                          CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var (probeHost, probePort) = SplitHostPort(host, port);
        bool tcpOk = false;
        int? players = null;

        // TCP probe. The 3s budget is enforced through a linked token rather than by racing a
        // Task.Delay: the old shape left the connect task running after the timeout, TcpClient.Dispose
        // then faulted it, and because nobody awaited it the fault surfaced via
        // TaskScheduler.UnobservedTaskException — one launcher_crash.log entry for every check against
        // an unreachable realm. Cancelling the connect means there is no orphaned task to fault.
        //
        // Every catch below is filtered on "the CALLER did not cancel". A timeout means offline; a
        // caller cancel means the answer is no longer wanted and must propagate, exactly as it does in
        // LauncherAuthService / HttpFriendsPresenceService / HttpArmoryService. Swallowing it reported a
        // superseded realm as offline and left that wrong dot on screen.
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var client = new TcpClient();
            await client.ConnectAsync(probeHost, probePort, timeout.Token);
            tcpOk = true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { tcpOk = false; } // our 3s budget
        catch (SocketException) { tcpOk = false; }               // refused / unreachable / DNS
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Anything else is worth seeing: an invalid host string, a disposed socket, a platform fault.
            _log.Warning(ex, "Realm TCP probe failed for {Host}:{Port}", probeHost, probePort);
            tcpOk = false;
        }

        // Player count: only for realms we can count (see the interface). It used to be fetched for
        // EVERY address from the config's global realmlist, so a foreign server ("kronos") showed
        // Stonetavern's number, and Stonetavern itself showed Elwynn alone (owner screenshot
        // 2026-09-28: "46 online" twice, while Elwynn 45 + Barrens 16 were on).
        if (countRealms.Count > 0)
            players = await CountPlayersAsync(probeHost, countRealms, ct).ConfigureAwait(false);

        return new ServerStatusResult { Online = tcpOk, PlayerCount = players };
    }

    private static readonly JsonSerializerOptions StatusJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The web API that reports a realm: the login host without its "play." prefix
    /// ("play.stonetavern.app" -> "stonetavern.app"). Only the prefix is cut, not every "play." in the
    /// name.</summary>
    internal static string ApiHostFor(string loginHost)
    {
        var h = loginHost.Trim();
        return h.StartsWith("play.", StringComparison.OrdinalIgnoreCase) ? h["play.".Length..] : h;
    }

    /// <summary>
    /// One GET per game realm (<c>/api/realm?realm=id</c>), in parallel, summed. All or nothing: if any
    /// realm could not be counted the sum would be a smaller number presented as the total, so the
    /// answer is then null and the row says "online" without a number.
    /// </summary>
    private async Task<int?> CountPlayersAsync(string loginHost, IReadOnlyList<string> realms, CancellationToken ct)
    {
        var apiHost = ApiHostFor(loginHost);
        if (apiHost.Length == 0) return null;

        async Task<int?> One(string realm)
        {
            try
            {
                var url = $"https://{apiHost}/api/realm?realm={Uri.EscapeDataString(realm)}";
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return JsonSerializer.Deserialize<RealmStatusResponse>(json, StatusJson)?.Players;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.Warning(ex, "Player count for realm {Realm} could not be read", realm);
                return null;
            }
        }

        var counts = await Task.WhenAll(realms.Select(One)).ConfigureAwait(false);
        return counts.All(c => c.HasValue) ? counts.Sum(c => c!.Value) : null;
    }
}
