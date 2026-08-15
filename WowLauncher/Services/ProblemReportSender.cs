namespace WowLauncher.Services;

using System.Net.Http.Json;
using System.Text.Json.Serialization;

/// <summary>Outcome of sending a problem report, in the terms the player is shown.</summary>
public sealed record ProblemReportResult(bool Ok, string? Reference, string? Error)
{
    public static ProblemReportResult Sent(string? reference) => new(true, reference, null);
    public static ProblemReportResult Failed(string error) => new(false, null, error);
}

public interface IProblemReportSender
{
    Task<ProblemReportResult> SendAsync(ProblemReportPayload payload, CancellationToken ct = default);
}

/// <summary>
/// Posts a problem report to the website, which mails it on.
///
/// <para>The launcher deliberately has no idea where the report ends up. Today it becomes a mail;
/// the plan is an operator portal later. Both are the same POST from here, so the launcher does not
/// need a new release when the destination changes.</para>
/// </summary>
public sealed class ProblemReportSender : IProblemReportSender
{
    internal const string Route = "/api/launcher/report";

    private readonly HttpClient _http;
    private readonly IConfigService _config;
    private readonly Serilog.ILogger _log;

    public ProblemReportSender(HttpClient http, IConfigService config, Serilog.ILogger log)
    {
        _http = http;
        _config = config;
        _log = log;
    }

    public async Task<ProblemReportResult> SendAsync(ProblemReportPayload payload, CancellationToken ct = default)
    {
        var site = _config.Load().SiteBaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(site))
            return ProblemReportResult.Failed("This launcher has no site configured to send reports to.");

        try
        {
            using var response = await _http
                .PostAsJsonAsync(site.TrimEnd('/') + Route, payload, ct)
                .ConfigureAwait(false);

            // Read the body either way: the server explains a refusal in it, and repeating that
            // explanation is the difference between a player retrying sensibly and giving up.
            var body = await response.Content
                .ReadFromJsonAsync<Response>(cancellationToken: ct)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode && body?.Ok == true)
            {
                _log.Information("Problem report sent, reference {Reference}", body.Reference);
                return ProblemReportResult.Sent(body.Reference);
            }

            var reason = !string.IsNullOrWhiteSpace(body?.Error)
                ? body!.Error!
                : $"The server refused the report (HTTP {(int)response.StatusCode}).";
            _log.Warning("Problem report refused: {Reason}", reason);
            return ProblemReportResult.Failed(reason);
        }
        catch (OperationCanceledException)
        {
            // The player closed the dialog or the app is shutting down. Not a failure to report.
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Problem report could not be sent");
            return ProblemReportResult.Failed(
                "The report could not be sent. Check your connection and try again.");
        }
    }

    private sealed record Response(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("reference")] string? Reference,
        [property: JsonPropertyName("error")] string? Error);
}
