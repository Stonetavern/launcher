using System.Net.Sockets;
using System.Text.Json;
using WowLauncher.Models;

namespace WowLauncher.Services;

public interface IServerStatusService
{
    Task<ServerStatusResult> CheckAsync(string host, int port = 3724, CancellationToken ct = default);
}

public sealed class ServerStatusResult
{
    public bool Online { get; init; }
    public int PlayerCount { get; init; }
}

public sealed class ServerStatusService : IServerStatusService
{
    private readonly HttpClient _http;
    private readonly IConfigService _config;
    private readonly Serilog.ILogger _log;

    public ServerStatusService(HttpClient http, IConfigService config, Serilog.ILogger log)
    {
        _http = http;
        _config = config;
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

    public async Task<ServerStatusResult> CheckAsync(string host, int port = 3724, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var (probeHost, probePort) = SplitHostPort(host, port);
        bool tcpOk = false;
        int players = 0;

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

        // Player count via API
        try
        {
            var config = _config.Load();
            // The realmlist address may carry a port; the API lives on the plain host name.
            var (realmHost, _) = SplitHostPort(config.RealmlistAddress, port);
            // If realmlist is "play.stonetavern.app", API is "stonetavern.app"
            var apiHost = realmHost.Replace("play.", "");
            var url = $"https://{apiHost}/api/realm";

            var resp = await _http.GetAsync(url, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                var status = JsonSerializer.Deserialize<RealmStatusResponse>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (status is not null)
                {
                    players = status.Online;
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.Warning(ex, "Failed to fetch player count from API");
        }

        return new ServerStatusResult { Online = tcpOk, PlayerCount = players };
    }
}
