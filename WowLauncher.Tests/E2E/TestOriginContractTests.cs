using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace WowLauncher.Tests.E2E;

/// <summary>
/// The positive control for the test infrastructure itself (PLAN §3, B.2).
///
/// <para>Every Ebene-B proof reads <see cref="TestOrigin.Requests"/> and concludes things from what is
/// or is not in it. That reasoning is only worth anything if the origin demonstrably serves, ranges,
/// records and truncates — otherwise an empty request log or a failed download would be read as a
/// finding about the launcher when it is a fact about this file. A measuring instrument that has not
/// been shown to measure produces no findings.</para>
/// </summary>
public sealed class TestOriginContractTests
{
    [Fact]
    public async Task ItServesABodyAndRecordsTheRequest()
    {
        using var origin = TestOrigin.Start();
        var payload = Payload(5000);
        origin.Publish("/pkg.bin", payload);

        using var client = origin.HttpClientForPlain();
        var body = await client.GetByteArrayAsync(origin.BaseUrl + "/pkg.bin");

        Assert.Equal(payload, body);
        var recorded = Assert.Single(origin.Requests);
        Assert.Equal("GET", recorded.Method);
        Assert.Equal("/pkg.bin", recorded.Path);
        Assert.Equal(200, recorded.Status);
        Assert.Equal(payload.Length, recorded.BytesWritten);
        Assert.Null(recorded.Range);
    }

    [Fact]
    public async Task AnUnknownPath_Is404_AndIsStillRecorded()
    {
        using var origin = TestOrigin.Start();
        using var client = origin.HttpClientForPlain();

        using var response = await client.GetAsync(origin.BaseUrl + "/nothing-here");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(404, Assert.Single(origin.Requests).Status);
    }

    [Fact]
    public async Task ARangeRequest_IsAnsweredWith206AndTheRightSlice()
    {
        using var origin = TestOrigin.Start();
        var payload = Payload(5000);
        origin.Publish("/pkg.bin", payload);

        using var client = origin.HttpClientForPlain();
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.BaseUrl + "/pkg.bin");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, null);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(payload.Skip(1000).ToArray(), body);
        Assert.Equal("bytes 1000-4999/5000", response.Content.Headers.ContentRange!.ToString());
        Assert.Equal("bytes=1000-", Assert.Single(origin.Requests).Range);
    }

    [Fact]
    public async Task AStaleIfRange_GetsTheWholeFileBack_NotASplice()
    {
        // The wire behaviour the resume contract depends on: the client offers what it thinks it is
        // continuing, the server's copy has changed, so the server must answer 200 with everything.
        using var origin = TestOrigin.Start();
        origin.Publish("/pkg.bin", Payload(5000));

        using var client = origin.HttpClientForPlain();
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.BaseUrl + "/pkg.bin");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, null);
        request.Headers.TryAddWithoutValidation("If-Range", "\"a-tag-from-yesterday\"");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5000, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task ATruncatedResponse_DiesMidTransfer_InsteadOfEndingCleanly()
    {
        using var origin = TestOrigin.Start();
        origin.Publish("/pkg.bin", Payload(200_000));
        origin.TruncateAfter("/pkg.bin", 4096);

        using var client = origin.HttpClientForPlain();

        // Not "returns fewer bytes": the announced length is never met and the read throws. A body that
        // simply ended short would let a caller believe it had the whole file.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            client.GetByteArrayAsync(origin.BaseUrl + "/pkg.bin"));

        Assert.Equal(4096, Assert.Single(origin.Requests).BytesWritten);
    }

    [Fact]
    public async Task TheSignedOriginIsReachedOverRealTlsUnderTheProductionHostname()
    {
        // This is the leg ManifestSignatureGate uses, and it is the one most likely to break silently:
        // if it did not work, every gate test would fail for a transport reason and look like a
        // refusal by the launcher.
        using var origin = TestOrigin.Start();
        origin.Publish(TestRelease.ManifestPath, "{\"product\":\"stonetavern-classic\"}");

        using var client = origin.HttpClientForSignedOrigin();
        var body = await client.GetStringAsync(WowLauncher.Services.LauncherChannel.ManifestUrl);

        Assert.Contains("stonetavern-classic", body, StringComparison.Ordinal);
        Assert.Equal(TestRelease.ManifestPath, Assert.Single(origin.Requests).Path);
    }

    private static byte[] Payload(int size)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }
}
