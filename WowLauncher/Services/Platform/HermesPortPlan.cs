namespace WowLauncher.Services.Platform;

using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <summary>
/// The ports HermesProxy binds BESIDES the BNet/portal port, and how to move them when something else
/// already holds one.
///
/// <para><b>Why this exists (2026-09-27, Linux).</b> The proxy bound 127.0.0.1:1119, then died on
/// 127.0.0.1:8081 ("SocketException (98): Address already in use", "Failed to start BnetRestApiSession
/// service") because a local <c>llama-server</c> held 8081. The process did not exit: on Linux
/// <c>Program.cs</c> always ends in "Press enter to close" and waited on stdin, still holding 1119. The
/// launcher's readiness check only looked at 1119, reported the proxy ready, and the client sat on
/// "Connecting" forever. Any player with any program on 8081, 8084 or 8086 hits the same wall.</para>
///
/// <para><b>Why moving them is safe — measured in the proxy source</b> (JimsProxy build 521b4, the one we
/// ship, <c>/mnt/data/wow/build-2026-09-19-521b4/src</c>):</para>
/// <list type="bullet">
/// <item><c>Program.cs</c>: option <c>--set Key=Value</c> "Overwrites a specific config value";
/// <c>Server.cs:89</c> passes those overrides into <c>ConfigurationParser.ParseFromFile</c>.</item>
/// <item>REST: <c>LoginServiceManager.Initialize</c> builds its endpoint from <c>Settings.RestPort</c>, and
/// <c>Authentication.cs</c> hands the client <c>https://{address}:{port}/bnetserver/login/…</c> as the
/// <c>web_auth_url</c> — the client learns the REST port FROM the proxy, it is not in Config.wtf.</item>
/// <item>Realm: <c>RealmManager.cs:309</c> / <c>Realm.cs:41</c> send <c>Settings.RealmPort</c> to the
/// client. Instance: <c>WorldSocket.cs:980,991</c> send <c>Settings.InstancePort</c> in the connect-to.</item>
/// </list>
/// <para>The BNet port (1119) is the one that cannot move this way: the client reads it from
/// <c>SET portal "127.0.0.1:1119"</c> in its own Config.wtf. It stays a hard preflight with a message that
/// names the process holding it.</para>
/// </summary>
public static class HermesPortPlan
{
    /// <summary>A config key of the proxy and the port it defaults to (Settings.cs in the proxy).</summary>
    public sealed record AuxPort(string Key, int DefaultPort);

    /// <summary>The ports the proxy tells the client about itself, so they may be moved.</summary>
    public static readonly IReadOnlyList<AuxPort> Relocatable =
    [
        new("RestPort", 8081),
        new("RealmPort", 8084),
        new("InstancePort", 8086),
    ];

    /// <summary>The outcome of planning: the ports the proxy will bind, the extra arguments that move
    /// the ones that had to move, and a line per move for the log.</summary>
    public sealed record Plan(
        IReadOnlyDictionary<string, int> Ports,
        IReadOnlyList<string> ExtraArgs,
        IReadOnlyList<string> Moves);

    /// <summary>Reads <c>&lt;add key="…" value="…"/&gt;</c> from the proxy's own config file, so the plan
    /// starts from what the proxy would really use. A missing or unreadable file means the defaults.</summary>
    public static Dictionary<string, int> ReadConfigured(string? configPath)
    {
        var result = Relocatable.ToDictionary(p => p.Key, p => p.DefaultPort, StringComparer.Ordinal);
        if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath)) return result;
        try
        {
            var doc = XDocument.Load(configPath);
            foreach (var add in doc.Descendants("add"))
            {
                var key = (string?)add.Attribute("key");
                var value = (string?)add.Attribute("value");
                if (key is null || !result.ContainsKey(key)) continue;
                if (int.TryParse(value, out var port) && port is > 0 and <= 65535) result[key] = port;
            }
        }
        catch
        {
            // A config the launcher cannot parse is the proxy's problem to report; plan from defaults.
        }
        return result;
    }

    /// <summary>
    /// Keep every configured port that is free; move every taken one to a port the OS says is free.
    /// <paramref name="isTaken"/> answers "does something already listen here"; <paramref name="pickFree"/>
    /// returns a free port that is not in the given set (so two moved ports never collide, and none lands
    /// on the BNet port).
    /// </summary>
    public static async Task<Plan> BuildAsync(
        IReadOnlyDictionary<string, int> configured, int bnetPort,
        Func<int, CancellationToken, Task<bool>> isTaken, Func<ISet<int>, int> pickFree,
        CancellationToken ct = default)
    {
        var ports = new Dictionary<string, int>(StringComparer.Ordinal);
        var args = new List<string>();
        var moves = new List<string>();
        var used = new HashSet<int> { bnetPort };

        foreach (var aux in Relocatable)
        {
            var wanted = configured.TryGetValue(aux.Key, out var p) ? p : aux.DefaultPort;
            var port = wanted;
            if (used.Contains(port) || await isTaken(port, ct).ConfigureAwait(false))
            {
                port = pickFree(used);
                args.Add("--set");
                args.Add($"{aux.Key}={port}");
                moves.Add($"{aux.Key} {wanted} is in use, the proxy uses {port} instead");
            }
            used.Add(port);
            ports[aux.Key] = port;
        }
        return new Plan(ports, args, moves);
    }

    /// <summary>A port the OS hands out as free on loopback right now, not in <paramref name="avoid"/>.</summary>
    public static int PickFreeLoopbackPort(ISet<int> avoid)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (!avoid.Contains(port)) return port;
        }
        throw new InvalidOperationException("The OS did not hand out a free loopback port.");
    }

    // ── Output that means the proxy is dead, even while its process lives on ──────────────────────────

    /// <summary>
    /// Lines the proxy prints when a listener could not start. After them it may still be alive, still
    /// hold 1119, and sit on "Press enter to close" — which is why a live process is not proof of a
    /// working proxy. Taken verbatim from proxy-hermes.log of 2026-09-27 23:12.
    /// </summary>
    private static readonly Regex FatalLine = new(
        @"Failed to start \w+ service|Unhandled exception|StartNetwork failed|Address already in use|Press enter to close",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>True when <paramref name="line"/> says the proxy failed to come up.</summary>
    public static bool IsFatal(string line) => FatalLine.IsMatch(line);

    /// <summary>The line a player (and the log) should see first: which service failed, then the
    /// socket error, then anything else. stdout and stderr arrive in no fixed order, so "first seen"
    /// is not "most telling".</summary>
    public static string MostTelling(IReadOnlyCollection<string> fatalLines)
    {
        static int Rank(string l) =>
            l.Contains("Failed to start", StringComparison.Ordinal) ? 0
            : l.Contains("Address already in use", StringComparison.Ordinal) ? 1
            : l.Contains("Press enter", StringComparison.Ordinal) ? 3 : 2;
        return fatalLines.OrderBy(Rank).First();
    }
}
