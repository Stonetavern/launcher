using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Resume robustness for the multi-GB client download. Every case here is a state a real player can
/// reach by doing nothing wrong, and every one of them used to be permanent:
///
/// <list type="bullet">
/// <item>A <c>.part</c> longer than the remote file makes the server answer HTTP 416 to the resume
/// Range. The old code returned a generic server error and LEFT the .part in place, so every future
/// attempt sent the same impossible Range. Only deleting the scratch file by hand fixed it.</item>
/// <item>A <c>.part</c> left over from a different build (same destination file name) was appended to
/// blindly, splicing two archives together. That only surfaced as a checksum failure after several GB.</item>
/// <item>An HttpClient timeout arrives as TaskCanceledException, which the old catch reported as
/// "cancelled by the user" - a state the UI treats as harmless rather than as a network failure.</item>
/// </list>
/// </summary>
public sealed class DownloadResumeTests
{
    private const string Body = "STONETAVERN-CLIENT-PAYLOAD";
    private const string Url = "https://downloads.example.invalid/client-5875.zip";
    private const string OtherUrl = "https://downloads.example.invalid/client-12340.zip";

    // ── Test doubles ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Serves <see cref="Body"/> in full for an unranged GET, and answers any ranged GET with 416 -
    /// exactly what a server does when the requested offset is past the end of the current file.
    /// Records every request so the test can prove which shape actually went out.
    /// </summary>
    private sealed class RangeRejectingHandler : HttpMessageHandler
    {
        public readonly List<string?> Ranges = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var range = request.Headers.Range?.ToString();
            Ranges.Add(range);

            if (range is not null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body, Encoding.UTF8),
            });
        }
    }

    /// <summary>Throws the exception an HttpClient timeout produces: a TaskCanceledException whose
    /// token was never signalled by the caller.</summary>
    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
    }

    private static DownloadService NewService(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new Serilog.LoggerConfiguration().CreateLogger());

    private static string NewScratchDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"st-dl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── Tests ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StalePartTooLarge_Http416_IsDiscardedAndTheDownloadRestarts()
    {
        var dir = NewScratchDir();
        var dest = Path.Combine(dir, "client.zip");
        // A leftover partial that claims to belong to this url but is LONGER than the remote file.
        await File.WriteAllTextAsync(dest + ".part", new string('x', Body.Length * 4));
        await File.WriteAllTextAsync(DownloadService.PartOwnerPath(dest), Url);

        var handler = new RangeRejectingHandler();
        var result = await NewService(handler).DownloadFileAsync(Url, dest);

        Assert.True(result.Ok,
            "A .part past the end of the remote file must be discarded and the download restarted, " +
            "not turned into a permanent HTTP 416 loop.");
        Assert.Equal(Body, await File.ReadAllTextAsync(dest));
        Assert.False(File.Exists(dest + ".part"), "The stale partial must be gone.");
        Assert.False(File.Exists(DownloadService.PartOwnerPath(dest)), "The owner marker must be cleaned up.");
        // First attempt ranged (and rejected), second attempt unranged.
        Assert.Equal(2, handler.Ranges.Count);
        Assert.NotNull(handler.Ranges[0]);
        Assert.Null(handler.Ranges[1]);
    }

    [Fact]
    public async Task Http416_LeavesNoPartBehind_SoTheNextAttemptStartsClean()
    {
        var dir = NewScratchDir();
        var dest = Path.Combine(dir, "client.zip");
        await File.WriteAllTextAsync(dest + ".part", new string('x', Body.Length * 4));
        await File.WriteAllTextAsync(DownloadService.PartOwnerPath(dest), Url);

        var handler = new RangeRejectingHandler();
        await NewService(handler).DownloadFileAsync(Url, dest);

        // The whole point: a SECOND run finds nothing to resume from and never sends a Range at all.
        File.Delete(dest);
        var second = new RangeRejectingHandler();
        var result = await NewService(second).DownloadFileAsync(Url, dest);

        Assert.True(result.Ok);
        Assert.Single(second.Ranges);
        Assert.Null(second.Ranges[0]);
    }

    [Fact]
    public async Task PartFromADifferentUrl_IsNotResumed()
    {
        var dir = NewScratchDir();
        var dest = Path.Combine(dir, "client.zip");
        // Bytes from another build under the same destination name.
        await File.WriteAllTextAsync(dest + ".part", "BYTES-FROM-A-DIFFERENT-BUILD");
        await File.WriteAllTextAsync(DownloadService.PartOwnerPath(dest), OtherUrl);

        var handler = new RangeRejectingHandler();
        var result = await NewService(handler).DownloadFileAsync(Url, dest);

        Assert.True(result.Ok);
        Assert.Equal(Body, await File.ReadAllTextAsync(dest));
        Assert.Single(handler.Ranges);
        Assert.Null(handler.Ranges[0]);
    }

    [Fact]
    public async Task UnmarkedPart_IsNotResumed_BecauseItsSourceCannotBeProven()
    {
        var dir = NewScratchDir();
        var dest = Path.Combine(dir, "client.zip");
        await File.WriteAllTextAsync(dest + ".part", "LEFTOVER-FROM-AN-OLDER-LAUNCHER");
        // No owner marker on purpose.

        var handler = new RangeRejectingHandler();
        var result = await NewService(handler).DownloadFileAsync(Url, dest);

        Assert.True(result.Ok);
        Assert.Equal(Body, await File.ReadAllTextAsync(dest));
        Assert.Single(handler.Ranges);
        Assert.Null(handler.Ranges[0]);
    }

    [Fact]
    public async Task Timeout_IsReportedAsNetwork_NotAsAUserCancel()
    {
        var dir = NewScratchDir();
        var dest = Path.Combine(dir, "client.zip");

        var result = await NewService(new TimeoutHandler()).DownloadFileAsync(Url, dest);

        Assert.False(result.Ok);
        Assert.Equal(DownloadFailure.Network, result.Failure);
    }

    [Fact]
    public async Task ACallerCancel_IsStillReportedAsCancelled()
    {
        var dir = NewScratchDir();
        var dest = Path.Combine(dir, "client.zip");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await NewService(new TimeoutHandler()).DownloadFileAsync(Url, dest, null, cts.Token);

        Assert.False(result.Ok);
        Assert.Equal(DownloadFailure.Cancelled, result.Failure);
    }
}
