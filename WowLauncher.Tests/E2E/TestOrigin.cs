using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WowLauncher.Tests.E2E;

/// <summary>
/// A real HTTP/1.1 server inside the test process — the piece Ebene B of
/// <c>(internal design notes, not published)</c> is built on.
///
/// <para><b>Why a real socket and not another <see cref="HttpMessageHandler"/> stub.</b> The suite
/// already has stubs (see <c>ManifestSignatureTests.StubServer</c>), and they prove the decisions a
/// method makes. What they cannot prove is that the launcher's HTTP layer behaves: a handler stub
/// never sees a <c>Range</c> request line, never answers 206, never terminates a body early, and
/// never lets <see cref="HttpClient"/>'s own parsing run. Every silent failure Ebene B exists to
/// catch lives in exactly that layer.</para>
///
/// <para><b>The measurement point is this class, not a return value</b> (PLAN §3). Everything the
/// server was asked is written to <see cref="Requests"/> — method, path, <c>Range</c>, <c>If-Range</c>,
/// the status served and the number of body bytes actually written. A return value can be right while
/// the wrong path was taken; a request log cannot.</para>
///
/// <para><b>TLS.</b> <c>ManifestSignatureGate</c> takes its address from
/// <c>LauncherChannel.ManifestUrl</c>, which is <c>https://downloads.stonetavern.app/manifest.json</c>
/// and deliberately not configurable at run time. So this origin also terminates TLS under exactly
/// that name and <see cref="HttpClientForSignedOrigin"/> hands out a client that dials loopback for
/// it and trusts precisely this one throwaway certificate. 🔴 That relaxes TRANSPORT trust for one
/// test client — it does not touch the manifest trust chain, which stays fully intact: the manifest
/// served here is signed with a real P-256 key and verified against its real public half. The rule
/// from PLAN §3 ("no switch that bypasses the chain of trust") is about that chain, and it holds.</para>
/// </summary>
internal sealed class TestOrigin : IDisposable
{
    private readonly TcpListener _plain;
    private readonly TcpListener _tls;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<string, Resource> _resources = new(StringComparer.Ordinal);
    private readonly List<RecordedRequest> _requests = [];
    // The origin hands out clients, so the origin closes them. Leaving them to the GC would leak a
    // socket handler per test — harmless in ten tests, not harmless in a suite that grows.
    private readonly List<HttpClient> _clients = [];
    private readonly Lock _gate = new();

    /// <summary>The hostname this origin terminates TLS for.</summary>
    public string TlsHost { get; }

    private TestOrigin(string tlsHost)
    {
        TlsHost = tlsHost;
        _certificate = SelfSigned(tlsHost);

        _plain = new TcpListener(IPAddress.Loopback, 0);
        _plain.Start();
        _tls = new TcpListener(IPAddress.Loopback, 0);
        _tls.Start();

        _ = AcceptLoopAsync(_plain, tls: false);
        _ = AcceptLoopAsync(_tls, tls: true);
    }

    /// <param name="tlsHost">The name the signed-manifest origin is reached under. Defaults to the
    /// host in <c>LauncherChannel.StableBaseUrl</c> so a rename there breaks this loudly instead of
    /// leaving the tests dialling a name nobody serves.</param>
    public static TestOrigin Start(string? tlsHost = null) =>
        new(tlsHost ?? new Uri(WowLauncher.Services.LauncherChannel.StableBaseUrl).Host);

    /// <summary>Base address for everything whose URL the TEST authors — client packages, launcher
    /// binaries, per-file repair downloads. Those addresses travel inside the manifest, so they may be
    /// plain loopback and no certificate is involved.</summary>
    public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_plain.LocalEndpoint).Port}";

    private int TlsPort => ((IPEndPoint)_tls.LocalEndpoint).Port;

    /// <summary>Everything this server was asked, in order. Read under the same lock it is written
    /// under: connections are handled on the thread pool, the assertions run on the test thread.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_gate) return _requests.ToList(); }
    }

    public void ClearRequests()
    {
        lock (_gate) _requests.Clear();
    }

    /// <summary>Publish <paramref name="body"/> at <paramref name="path"/> (leading slash included).
    /// Re-publishing the same path replaces it, which is how "the package was swapped under a paused
    /// download" is staged.</summary>
    public void Publish(string path, byte[] body, string contentType = "application/octet-stream",
        string? entityTag = null)
    {
        lock (_gate)
            _resources[path] = new Resource(body, contentType, entityTag ?? WeakTagFor(body), null, HttpStatusCode.OK);
    }

    public void Publish(string path, string body, string contentType = "application/json") =>
        Publish(path, Encoding.UTF8.GetBytes(body), contentType);

    /// <summary>Remove a resource, so requests for it answer 404 — "the server never published this".</summary>
    public void Unpublish(string path)
    {
        lock (_gate) _resources.Remove(path);
    }

    /// <summary>Answer <paramref name="path"/> with an error status and no body.</summary>
    public void PublishFailure(string path, HttpStatusCode status)
    {
        lock (_gate)
            _resources[path] = new Resource([], "text/plain", "\"err\"", null, status);
    }

    /// <summary>
    /// Serve at most <paramref name="bytes"/> of this resource's body and then kill the connection
    /// mid-transfer, while still announcing the full length. That is the wire shape of a power cut
    /// (PLAN §3 B.4) — the client has a partial file and no clean end of message.
    /// <para>Pass null to stop truncating, which is what the "second attempt" of a resume test does.</para>
    /// </summary>
    public void TruncateAfter(string path, int? bytes)
    {
        lock (_gate)
        {
            if (_resources.TryGetValue(path, out var existing))
                _resources[path] = existing with { DropAfterBytes = bytes };
        }
    }

    /// <summary>
    /// An <see cref="HttpClient"/> that reaches THIS origin for the production download hostname over
    /// real TLS, trusting only the throwaway certificate above. Used for the manifest + signature
    /// fetch, whose URL the launcher pins and does not take from configuration.
    /// </summary>
    public HttpClient HttpClientForSignedOrigin()
    {
        var expected = _certificate.Thumbprint;
        var handler = new SocketsHttpHandler
        {
            // Dial loopback whatever the name says — the name is what we are testing, the route is not.
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, TlsPort, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            SslOptions = new SslClientAuthenticationOptions
            {
                // Pinned to one certificate, not "accept anything": a test that trusts every
                // certificate would still pass if the launcher talked to a stranger.
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is X509Certificate2 c && string.Equals(c.Thumbprint, expected, StringComparison.OrdinalIgnoreCase),
            },
        };
        return Track(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) });
    }

    /// <summary>A plain client for the addresses the manifest carries. Real sockets, real parsing.</summary>
    public HttpClient HttpClientForPlain() =>
        Track(new HttpClient(new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(30) });

    private HttpClient Track(HttpClient client)
    {
        lock (_gate) _clients.Add(client);
        return client;
    }

    // ─── the server itself ────────────────────────────────────────────────

    private async Task AcceptLoopAsync(TcpListener listener, bool tls)
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_stopping.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }

            _ = Task.Run(() => HandleAsync(client, tls));
        }
    }

    private async Task HandleAsync(TcpClient client, bool tls)
    {
        // One request per connection, answered with "Connection: close". Keep-alive would mean
        // parsing pipelined requests, and a hand-written parser is exactly the kind of place a silent
        // test-infrastructure bug hides. HttpClient opens a fresh connection and is none the wiser.
        try
        {
            using (client)
            {
                Stream stream = client.GetStream();
                SslStream? secure = null;
                if (tls)
                {
                    secure = new SslStream(stream, leaveInnerStreamOpen: false);
                    await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        ClientCertificateRequired = false,
                    }, _stopping.Token);
                    stream = secure;
                }

                var head = await ReadHeadAsync(stream);
                if (head is null) return;

                await RespondAsync(stream, client, head);
                secure?.Dispose();
            }
        }
        catch (Exception)
        {
            // A client that walks away mid-response is normal here (the truncation cases do it from
            // the other side too). Never let it take the accept loop down.
        }
    }

    /// <summary>Reads request line + headers up to the blank line. Null when the peer left first.</summary>
    private static async Task<RequestHead?> ReadHeadAsync(Stream stream)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length < 16 * 1024)
        {
            var read = await stream.ReadAsync(one);
            if (read == 0) return null;
            buffer.WriteByte(one[0]);
            var bytes = buffer.GetBuffer();
            var length = (int)buffer.Length;
            if (length >= 4 && bytes[length - 4] == '\r' && bytes[length - 3] == '\n' &&
                bytes[length - 2] == '\r' && bytes[length - 1] == '\n')
                break;
        }

        var text = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;

        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return new RequestHead(parts[0], parts[1], headers);
    }

    private async Task RespondAsync(Stream stream, TcpClient client, RequestHead head)
    {
        var path = head.Target.Split('?')[0];
        Resource? resource;
        lock (_gate) _resources.TryGetValue(path, out resource);

        if (resource is null)
        {
            await WriteHeadersAsync(stream, HttpStatusCode.NotFound, "text/plain", 0, null, null);
            Record(head, 404, 0);
            return;
        }

        if (resource.Status != HttpStatusCode.OK)
        {
            await WriteHeadersAsync(stream, resource.Status, resource.ContentType, 0, null, null);
            Record(head, (int)resource.Status, 0);
            return;
        }

        var body = resource.Body;
        var from = 0;
        var to = body.Length - 1;
        var partial = false;

        if (head.Headers.TryGetValue("Range", out var range) && TryParseRange(range, body.Length, out var f, out var t))
        {
            // If-Range is the whole point of the resume contract: the client offers what it believes
            // it is continuing, and a server whose copy has changed must answer with the WHOLE file
            // instead of splicing new bytes onto old ones.
            var stale = head.Headers.TryGetValue("If-Range", out var validator) &&
                        !string.Equals(validator.Trim(), resource.ETag, StringComparison.Ordinal);
            if (!stale)
            {
                from = f;
                to = t;
                partial = true;
            }
        }

        var length = to - from + 1;
        var status = partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK;
        var contentRange = partial
            ? $"bytes {from}-{to}/{body.Length}"
            : null;

        await WriteHeadersAsync(stream, status, resource.ContentType, length, resource.ETag, contentRange);

        if (string.Equals(head.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            Record(head, (int)status, 0);
            return;
        }

        var allowed = resource.DropAfterBytes is { } cap ? Math.Min(cap, length) : length;
        await stream.WriteAsync(body.AsMemory(from, allowed));
        await stream.FlushAsync();
        Record(head, (int)status, allowed);

        if (allowed < length)
        {
            // Abortive close: the announced Content-Length is never met, and the client sees the
            // connection die under it. That is a power cut, not a polite end of message.
            client.Client.LingerState = new LingerOption(true, 0);
            client.Client.Close();
        }
    }

    private static async Task WriteHeadersAsync(Stream stream, HttpStatusCode status, string contentType,
        int contentLength, string? etag, string? contentRange)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {(int)status} {status}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"Content-Type: {contentType}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"Content-Length: {contentLength}\r\n");
        sb.Append("Accept-Ranges: bytes\r\n");
        if (etag is not null) sb.Append(CultureInfo.InvariantCulture, $"ETag: {etag}\r\n");
        if (contentRange is not null) sb.Append(CultureInfo.InvariantCulture, $"Content-Range: {contentRange}\r\n");
        sb.Append("Connection: close\r\n\r\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(bytes);
    }

    /// <summary>Only the forms the launcher actually sends: <c>bytes=N-</c> and <c>bytes=N-M</c>.</summary>
    private static bool TryParseRange(string header, int total, out int from, out int to)
    {
        from = 0;
        to = total - 1;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        var spec = header["bytes=".Length..].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return false;

        if (!int.TryParse(spec[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out from))
            return false;
        var tail = spec[(dash + 1)..];
        if (tail.Length > 0 &&
            int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var explicitTo))
            to = Math.Min(explicitTo, total - 1);

        return from >= 0 && from < total && to >= from;
    }

    private void Record(RequestHead head, int status, int bytesWritten)
    {
        head.Headers.TryGetValue("Range", out var range);
        head.Headers.TryGetValue("If-Range", out var ifRange);
        lock (_gate)
            _requests.Add(new RecordedRequest(head.Method, head.Target.Split('?')[0], range, ifRange,
                status, bytesWritten));
    }

    private static string WeakTagFor(byte[] body) =>
        "\"" + Convert.ToHexString(SHA256.HashData(body))[..16] + "\"";

    private static X509Certificate2 SelfSigned(string host)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        using var created = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        // Round-tripping through PKCS#12 is what makes the private key usable by SslStream on Linux;
        // a certificate straight out of CreateSelfSigned hands the server an unusable key there.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), password: null);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        try { _plain.Stop(); } catch { /* already down */ }
        try { _tls.Stop(); } catch { /* already down */ }
        lock (_gate)
        {
            foreach (var client in _clients) client.Dispose();
            _clients.Clear();
        }
        _certificate.Dispose();
        _stopping.Dispose();
    }

    private sealed record Resource(byte[] Body, string ContentType, string ETag, int? DropAfterBytes,
        HttpStatusCode Status);

    private sealed record RequestHead(string Method, string Target, Dictionary<string, string> Headers);
}

/// <summary>One thing the origin was asked, and what it answered. The evidence Ebene B reasons about.</summary>
internal sealed record RecordedRequest(string Method, string Path, string? Range, string? IfRange,
    int Status, int BytesWritten);
