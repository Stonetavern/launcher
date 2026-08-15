namespace WowLauncher.Tests;

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

/// <summary>
/// The report is the last thing a player with a broken launcher can do. What the server says about a
/// refusal has to reach them — the difference between retrying sensibly and giving up.
///
/// <para>Measured against the live endpoint on 2026-08-04: it answers JSON on every path, including a
/// refusal (<c>{"ok":false,"error":"Too many reports from here. Please try again later."}</c>, HTTP
/// 429). That is only worth anything if the launcher passes it on rather than replacing it with a
/// guess — and until these tests there was nothing holding it to that.</para>
/// </summary>
public sealed class ProblemReportSenderTests
{
    private static readonly Serilog.ILogger Log = Serilog.Core.Logger.None;

    private static ProblemReportPayload Payload() =>
        new("1.6.4", "Linux", "stonetavern", 5875, true, "es startet nicht", "", "");

    private static ProblemReportSender Sender(HttpStatusCode status, string body,
        string contentType = "application/json") =>
        new(new HttpClient(new CannedHandler(status, body, contentType)),
            new FixedConfig("https://stonetavern.app"), Log);

    [Fact]
    public async Task AnAcceptedReport_HandsBackTheReference()
    {
        var result = await Sender(HttpStatusCode.OK, """{"ok":true,"reference":"ST-4711"}""")
            .SendAsync(Payload());

        Assert.True(result.Ok);
        Assert.Equal("ST-4711", result.Reference);
    }

    /// <summary>
    /// 🔴 The refusal the live server actually produces. A player who sends twice in a row gets this,
    /// and "Too many reports from here" tells them to wait — while a generic "check your connection"
    /// would send them hunting a network fault that does not exist.
    /// </summary>
    [Fact]
    public async Task AThrottledReport_RepeatsWhatTheServerSaid()
    {
        var result = await Sender(HttpStatusCode.TooManyRequests,
                """{"ok":false,"error":"Too many reports from here. Please try again later."}""")
            .SendAsync(Payload());

        Assert.False(result.Ok);
        Assert.Equal("Too many reports from here. Please try again later.", result.Error);
    }

    [Fact]
    public async Task ARejectedReport_RepeatsTheServersReason()
    {
        var result = await Sender(HttpStatusCode.BadRequest, """{"ok":false,"error":"message is empty"}""")
            .SendAsync(Payload());

        Assert.False(result.Ok);
        Assert.Equal("message is empty", result.Error);
    }

    /// <summary>A server that refuses without saying why still has to produce something a player can
    /// act on — and the status code is the only fact left.</summary>
    [Fact]
    public async Task ARefusalWithoutAReason_StillNamesTheStatus()
    {
        var result = await Sender(HttpStatusCode.ServiceUnavailable, """{"ok":false}""").SendAsync(Payload());

        Assert.False(result.Ok);
        Assert.Contains("503", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case that is easy to get wrong: an HTML error page instead of JSON — a proxy, a captive
    /// portal, a CDN block. Parsing it throws, and the player must get a sentence rather than a
    /// crash or a silent nothing.
    /// </summary>
    [Fact]
    public async Task AnHtmlErrorPage_DoesNotThrow_AndStillExplains()
    {
        var result = await Sender(HttpStatusCode.Forbidden, "<html><body>Blocked</body></html>", "text/html")
            .SendAsync(Payload());

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    /// <summary>A launcher without a site configured says so instead of posting into the void.</summary>
    [Fact]
    public async Task WithoutASiteConfigured_NothingIsSent()
    {
        var sender = new ProblemReportSender(
            new HttpClient(new CannedHandler(HttpStatusCode.OK, """{"ok":true}""")),
            new FixedConfig(""), Log);

        var result = await sender.SendAsync(Payload());

        Assert.False(result.Ok);
    }

    /// <summary>A cancel is a player closing the dialog, not a failed report — it must travel up
    /// rather than be dressed as a server problem.</summary>
    [Fact]
    public async Task ACancel_TravelsUp()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Sender(HttpStatusCode.OK, """{"ok":true}""").SendAsync(Payload(), cts.Token));
    }

    /// <summary>
    /// The header the operator sees in their server log when they chase a report. It used to say
    /// "WowLauncher/1.0" on every build ever shipped — answering the first question of any diagnosis
    /// ("which build?") with an untruth. It now carries the product version, the same source the
    /// update comparison uses.
    /// </summary>
    [Fact]
    public void TheUserAgent_NamesTheRealVersion()
    {
        var ua = Infrastructure.DependencyInjection.LauncherUserAgent;

        Assert.StartsWith("StonetavernLauncher/", ua, StringComparison.Ordinal);
        // Eine echte, lesbare Version — und nicht mehr die feste Zeichenkette, die jeder Build trug.
        Assert.True(Version.TryParse(ua["StonetavernLauncher/".Length..], out _),
            $"'{ua}' trägt keine lesbare Version");
        Assert.NotEqual("WowLauncher/1.0", ua);
    }

    // ── Attrappen ────────────────────────────────────────────────────────────────────────────────

    private sealed class CannedHandler(HttpStatusCode status, string body, string contentType = "application/json")
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, contentType),
            });
        }
    }

    private sealed class FixedConfig(string site) : IConfigService
    {
        private LauncherConfig _c = new() { SiteBaseUrl = site };
        public LauncherConfig Load() => _c;
        public void Save(LauncherConfig config) => _c = config;
        public bool LastSaveSucceeded => true;
    }
}
