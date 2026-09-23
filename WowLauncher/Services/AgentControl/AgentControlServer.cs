using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WowLauncher.Services.AgentControl;

/// <summary>
/// A loopback HTTP endpoint that lets a test harness read the launcher's state and press its buttons.
///
/// <para><b>Endpoints</b> (all require <c>X-Agent-Token</c>):</para>
/// <list type="bullet">
///   <item><c>GET  /state</c> — <see cref="AgentState"/> as JSON.</item>
///   <item><c>POST /command/{name}</c> — runs one command, answers <see cref="AgentCommandResult"/>.</item>
/// </list>
///
/// <para><b>🔴 Why this is safe to ship disabled and dangerous to ship enabled.</b> The endpoint can
/// start downloads and launch the game, so it is guarded three times over, and each guard is
/// independently sufficient:</para>
/// <list type="number">
///   <item><b>It does not exist without the switch.</b> No <c>--agent-control</c>, no listener.</item>
///   <item><b>Loopback only.</b> The prefix is <c>http://127.0.0.1:{port}/</c>, so the OS itself
///     refuses connections from the network. A remote request cannot reach the socket at all.
///     Requests that somehow arrive non-local are rejected a second time in code.</item>
///   <item><b>A per-run token.</b> 32 random bytes from <see cref="RandomNumberGenerator"/>, compared
///     in fixed time. It is never logged and never sent to the client; a harness learns it by reading
///     the handshake file, which requires being the same user on the same machine.</item>
/// </list>
///
/// <para>The token comparison uses <see cref="CryptographicOperations.FixedTimeEquals"/> rather than
/// string equality on purpose. It costs nothing and removes the timing side channel that makes
/// "compare and return early" a guessing oracle.</para>
/// </summary>
public sealed class AgentControlServer : IAsyncDisposable
{
    public const string TokenHeader = "X-Agent-Token";

    private readonly IAgentControlSurface _surface;
    private readonly Serilog.ILogger _log;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly byte[] _tokenBytes;
    private Task? _loop;

    /// <summary>The token a client must present. Handed to the handshake file, never to a response.</summary>
    public string Token { get; }

    /// <summary>The port actually bound. Only meaningful after <see cref="Start"/>.</summary>
    public int Port { get; private set; }

    public AgentControlServer(IAgentControlSurface surface, Serilog.ILogger log)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<AgentControlServer>();

        _tokenBytes = RandomNumberGenerator.GetBytes(32);
        Token = Convert.ToHexString(_tokenBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Binds and begins serving. With <paramref name="port"/> 0 the OS picks a free one, which is the
    /// normal case; <see cref="Port"/> then reports what it picked.
    /// </summary>
    public void Start(int port = 0)
    {
        // HttpListener cannot bind port 0 and report back, so the free port is found with a throwaway
        // socket first. The gap between releasing it and binding it here is a theoretical race; on
        // loopback, for a test-only endpoint, that is an acceptable trade against a fixed port that
        // collides whenever two launchers run at once.
        Port = port != 0 ? port : FindFreeLoopbackPort();

        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(() => AcceptLoopAsync(_stopping.Token));

        _log.Information("Agent control listening on 127.0.0.1:{Port} (token in the handshake file)", Port);
    }

    private static int FindFreeLoopbackPort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var chosen = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return chosen;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return; // Shutting down — the listener was disposed out from under us on purpose.
            }
            catch (HttpListenerException ex)
            {
                _log.Warning(ex, "Agent control: listener stopped accepting");
                return;
            }

            // One slow command must not stall the next request, so each is handled detached. Failures
            // are contained per request; nothing here may take the launcher down.
            _ = Task.Run(() => HandleSafelyAsync(context, ct), CancellationToken.None);
        }
    }

    private async Task HandleSafelyAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            await HandleAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Agent control: request failed");
            TryWrite(context, 500, new { error = "internal error" });
        }
        finally
        {
            try { context.Response.Close(); } catch (Exception) { /* client already gone */ }
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;

        // Guard 2, in code as well as in the binding: never serve anything that did not come from
        // this machine, whatever the prefix ends up allowing.
        if (!request.IsLocal)
        {
            Write(context, 403, new { error = "loopback only" });
            return;
        }

        // Guard 3: the token, before anything is read or done.
        if (!TokenMatches(request.Headers[TokenHeader]))
        {
            Write(context, 401, new { error = "bad or missing " + TokenHeader });
            return;
        }

        var path = request.Url?.AbsolutePath.TrimEnd('/') ?? "";

        if (request.HttpMethod == "GET" && path is "" or "/state")
        {
            Write(context, 200, await _surface.GetStateAsync(ct).ConfigureAwait(false));
            return;
        }

        if (request.HttpMethod == "POST" && path.StartsWith("/command/", StringComparison.Ordinal))
        {
            var name = path["/command/".Length..];
            if (string.IsNullOrWhiteSpace(name))
            {
                Write(context, 400, new { error = "no command named" });
                return;
            }

            var result = await _surface.InvokeAsync(name, ct).ConfigureAwait(false);
            // A refused command is a valid answer about the launcher's state, not a protocol error —
            // hence 200 with accepted=false. A harness asserts on the field, not on the status code.
            Write(context, 200, result);
            return;
        }

        Write(context, 404, new { error = "unknown route", hint = "GET /state, POST /command/{name}" });
    }

    private bool TokenMatches(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;

        byte[] presentedBytes;
        try
        {
            presentedBytes = Convert.FromHexString(presented);
        }
        catch (FormatException)
        {
            return false;
        }

        // FixedTimeEquals is length-sensitive but not early-exiting: a wrong length is rejected
        // without leaking where the first differing byte sits.
        return presentedBytes.Length == _tokenBytes.Length
            && CryptographicOperations.FixedTimeEquals(presentedBytes, _tokenBytes);
    }

    private static void Write(HttpListenerContext context, int status, object payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, AgentControlJson.Options);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = json.Length;
        context.Response.OutputStream.Write(json, 0, json.Length);
    }

    private static void TryWrite(HttpListenerContext context, int status, object payload)
    {
        try { Write(context, status, payload); } catch (Exception) { /* response already begun */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try { _listener.Stop(); } catch (Exception) { /* never started */ }

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (Exception) { /* torn down */ }
        }

        _listener.Close();
        _stopping.Dispose();
        CryptographicOperations.ZeroMemory(_tokenBytes);
    }
}

/// <summary>One shared serializer setup so responses look the same everywhere.</summary>
internal static class AgentControlJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>UTF-8 without a BOM, for the handshake file.</summary>
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
}
