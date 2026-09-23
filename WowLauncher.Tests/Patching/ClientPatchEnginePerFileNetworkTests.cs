using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Patching;
using Xunit;

namespace WowLauncher.Tests.Patching;

/// <summary>
/// The one PER_FILE case that genuinely needs real HTTP semantics rather than
/// <see cref="FakeDownloadService"/>: a cancel mid-transfer must leave a resumable <c>.part</c>, and
/// restarting must continue from it via Range rather than re-downloading from zero
/// (ARCHITEKTUR-v2-patcher.md §5, same resume contract <c>DownloadService</c> already gives the ZIP
/// path). Drives the REAL <see cref="DownloadService"/> through the REAL <see cref="ClientPatchEngine"/>
/// against an in-process Range-aware handler — no network, no temp HTTP server needed for this proof.
/// </summary>
public sealed class ClientPatchEnginePerFileNetworkTests
{
    /// <summary>Serves one fixed byte array with real Range/If-Range support — the same contract a
    /// static file server (nginx, `python3 -m http.server`) gives <c>DownloadService</c>. Delivers the
    /// body as a slow, chunked stream so a test can cancel mid-transfer deterministically instead of
    /// racing a real clock.</summary>
    private sealed class RangeableSlowHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly int _stallAfterBytes;
        public readonly List<string?> Ranges = [];
        private readonly TaskCompletionSource _stalled = new();
        public Task Stalled => _stalled.Task;

        public RangeableSlowHandler(byte[] body, int stallAfterBytes)
        {
            _body = body;
            _stallAfterBytes = stallAfterBytes;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var range = request.Headers.Range?.ToString();
            Ranges.Add(range);

            long start = 0;
            if (request.Headers.Range?.Ranges.FirstOrDefault() is { } r && r.From is { } from)
                start = from;

            var slice = _body[(int)start..];
            var stream = new SlowMemoryStream(slice, _stallAfterBytes - (int)start, _stalled);
            var response = new HttpResponseMessage(start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            };
            response.Content.Headers.ContentLength = slice.Length;
            if (start > 0)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, _body.Length - 1, _body.Length);
            response.Headers.ETag = new EntityTagHeaderValue("\"fixed-etag\"");
            return await Task.FromResult(response);
        }
    }

    /// <summary>A read stream that delivers small chunks with a short delay between them (so a test
    /// can cancel mid-stream after a known number of bytes) and resolves <paramref name="stalled"/>
    /// once that many bytes have actually been handed out.</summary>
    private sealed class SlowMemoryStream(byte[] data, int signalAfterBytes, TaskCompletionSource stalled) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (_pos >= data.Length) return 0;
            var n = Math.Min(Math.Min(count, data.Length - _pos), 2048);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            if (_pos >= signalAfterBytes && !stalled.Task.IsCompleted) stalled.TrySetResult();
            await Task.Delay(15, CancellationToken.None); // deliberately NOT ct: let the caller observe+react to cancel via its own token on the NEXT await, exactly like a real socket read would
            ct.ThrowIfCancellationRequested();
            return n;
        }
    }

    private static ManifestFile NewClient(string filesBase) => new()
    {
        Build = 5875, Os = "windows", Version = "1.4",
        FilesUrl = "http://fake.invalid/files.json", FilesSha256 = "irrelevant",
        FilesBase = filesBase,
    };

    [Fact]
    public async Task Cancel_mid_download_keeps_the_part_file_and_resume_does_not_redownload_from_zero()
    {
        var root = PatchingFakes.NewTempDir();
        // A lead file must already be present and correct on disk — PLAN's "kein Baum vorhanden"
        // check (ARCHITEKTUR-v2-patcher.md §4) looks for it before anything else, and without one the
        // route would be FullZip, not PerFile, regardless of forcePerFile.
        var wowExe = "wow-exe"u8.ToArray();
        PatchingFakes.WriteFile(root, "WoW.exe", wowExe);

        var payload = new byte[200_000];
        new Random(42).NextBytes(payload);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();

        var manifest = new ClientFileManifest
        {
            Build = 5875, Version = "1.4",
            Files =
            [
                PatchingFakes.Entry("WoW.exe", wowExe),
                new ClientFileEntry { Path = "Data/big.bin", Size = payload.Length, Sha256 = hash },
            ],
        };
        var client = NewClient("http://fake.invalid/tree/");

        var handler = new RangeableSlowHandler(payload, stallAfterBytes: 40_000);
        var download = new DownloadService(new HttpClient(handler), new Serilog.LoggerConfiguration().CreateLogger());
        var engine = new ClientPatchEngine(new FakeManifestLoader(manifest), download,
            new ClientVerifyService(new Serilog.LoggerConfiguration().CreateLogger()),
            new FakeButlerSidecar(), new FakeGameProcessDetector(),
            new Serilog.LoggerConfiguration().CreateLogger());

        using var cts = new CancellationTokenSource();
        var runTask = engine.RunAsync(client, root, forcePerFile: true, progress: null, cts.Token);

        // Bounded waits rather than a bare `await` on either side: a genuine hang here must fail the
        // test with a clear message, not wedge the whole suite (measured 2026-09-19 — the FIRST cut of
        // this test, missing the WoW.exe lead file below, silently took the FullZip route instead of
        // PerFile and hung the entire "Patching" filter run because nothing ever signalled `Stalled`).
        var stalledOrTimeout = await Task.WhenAny(handler.Stalled, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(stalledOrTimeout == handler.Stalled, "the fake server never received the expected request — PLAN likely chose a different route than PerFile");
        cts.Cancel();

        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(completed == runTask, "engine.RunAsync did not observe the cancellation in time");
        await Assert.ThrowsAsync<OperationCanceledException>(() => runTask);

        var finalPath = Path.Combine(root, "Data", "big.bin");
        var partPath = finalPath + ".part";
        Assert.False(File.Exists(finalPath));
        Assert.True(File.Exists(partPath), "a cancelled per-file download must leave a resumable .part");
        var partialLength = new FileInfo(partPath).Length;
        Assert.InRange(partialLength, 1, payload.Length - 1);

        // ── Restart: same engine/handler, a fresh (non-cancelled) token ────────────────────────────
        var outcome = await engine.RunAsync(client, root, forcePerFile: true, progress: null, CancellationToken.None);

        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.True(File.Exists(finalPath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(finalPath));
        // The resume must have gone out as a RANGED request — proof it continued instead of restarting.
        Assert.Contains(handler.Ranges, r => r is not null && r.Contains("bytes="));
    }
}
