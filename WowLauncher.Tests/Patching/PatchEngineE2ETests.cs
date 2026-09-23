using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Patching;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests.Patching;

/// <summary>
/// A minimal, real, Range-aware static file server for the E2E proof below. Deliberately NOT
/// <c>python3 -m http.server</c> — measured 2026-09-19 that Python's stdlib server ignores the
/// <c>Range</c> header entirely (always answers 200 with the full body), which would make the
/// resume/cancel scenario pass for the wrong reason (a full re-download that happens to still work,
/// not a genuine resume). This one implements Range/If-Range/206 for real, the same contract
/// <c>DownloadService</c> already assumes a production static file host gives it.
/// </summary>
internal sealed class RangeFileServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _root;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}/";
    public readonly List<string> AccessLog = [];

    /// <summary>Relative path (server-root relative) → per-64KiB-chunk delay in ms. Lets a test slow
    /// down ONE large file (the delta .pwr) enough to cancel mid-transfer deterministically, without
    /// throttling everything and making the whole E2E run slow.</summary>
    public readonly Dictionary<string, int> ThrottleMsPerChunk = new(StringComparer.Ordinal);

    public RangeFileServer(string root)
    {
        _root = root;
        Port = GetFreePort();
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var rel = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath.TrimStart('/'));
            var path = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            var info = new FileInfo(path);
            long start = 0, end = info.Length - 1;
            var rangeHeader = ctx.Request.Headers["Range"];
            var ranged = !string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.Ordinal);
            if (ranged)
            {
                var spec = rangeHeader!["bytes=".Length..].Split('-');
                start = long.Parse(spec[0]);
                if (spec.Length > 1 && spec[1].Length > 0) end = long.Parse(spec[1]);
            }
            lock (AccessLog)
                AccessLog.Add($"{ctx.Request.HttpMethod} /{rel} {(ranged ? $"range={start}-{end}" : "full")}");

            ctx.Response.Headers["Accept-Ranges"] = "bytes";
            ctx.Response.Headers["ETag"] = "\"e2e-fixed\"";
            ctx.Response.StatusCode = ranged ? 206 : 200;
            if (ranged) ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{info.Length}";
            var len = end - start + 1;
            ctx.Response.ContentLength64 = len;

            ThrottleMsPerChunk.TryGetValue(rel, out var throttle);
            using var fs = File.OpenRead(path);
            fs.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[65536];
            long remaining = len;
            while (remaining > 0)
            {
                var n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (n <= 0) break;
                ctx.Response.OutputStream.Write(buffer, 0, n);
                remaining -= n;
                if (throttle > 0) Thread.Sleep(throttle);
            }
            ctx.Response.OutputStream.Close();
        }
        catch (Exception)
        {
            try { ctx.Response.Abort(); } catch { /* client gone */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        _listener.Close();
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

/// <summary>
/// E2E proof on real client data (ARCHITEKTUR-v2-patcher.md §8, S2 row): the real
/// classic-1.12.1/windows/s1-test tree from <c>/mnt/data/wow/s0-2026-09-19/S1-out/…</c>, the real
/// butler binary, working copies under <c>/mnt/data/wow/s2-2026-09-19/</c>. Opt-in via
/// <c>STONETAVERN_E2E=1</c> (real Skip, never a silent pass, when unset or the fixture tree is
/// missing — e.g. on a machine other than this one).
/// </summary>
public sealed class PatchEngineE2ETests
{
    private const string SourceRoot = "/mnt/data/wow/s0-2026-09-19/S1-out/classic-1.12.1/windows/s1-test";
    private const string WorkRoot = "/mnt/data/wow/s2-2026-09-19";
    private static readonly string ButlerBinary = Path.Combine(AppContext.BaseDirectory,
        "butler-fixture", "tools", "butler", "linux-x64", "butler");

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("STONETAVERN_E2E") == "1"
        && Directory.Exists(SourceRoot) && File.Exists(ButlerBinary) && OperatingSystem.IsLinux();

    private static void SkipUnlessEnabled() =>
        Skip.IfNot(Enabled, "set STONETAVERN_E2E=1 on a machine with /mnt/data/wow/s0-2026-09-19 and the linux butler fixture to run this");

    private ManifestFile NewClient(RangeFileServer server) => new()
    {
        Build = 5875,
        Os = "windows",
        Version = "s1-test",
        Url = server.BaseUrl + "does-not-exist-fullclient.zip", // (f): must never actually be fetched in full
        Sha256 = "",
        FilesUrl = server.BaseUrl + "files.json",
        FilesSha256 = "ed9e8cdda52942144313fd6079af7120d06260810d3bc2131727221fb13b16da",
        FilesBase = server.BaseUrl + "tree/",
        Deltas =
        [
            new ManifestDelta
            {
                From = "1.3", Url = server.BaseUrl + "1.3-to-s1-test.pwr", Size = 157292637,
                Sha256 = "4c970fc41bae7babf11d3de73ee07a43d93aa471115a493e429a3e9995224e9b",
                SigUrl = server.BaseUrl + "1.3-to-s1-test.pwr.sig",
                SigSha256 = "793b4ebd7ccc1b6f00ea84b296aa03350a17372ad6af433e6f0bf8999af2aa46",
            },
        ],
    };

    private static ClientPatchEngine NewEngine() => new(
        new ClientFileManifestLoader(new HttpClient(), new Serilog.LoggerConfiguration().CreateLogger()),
        new DownloadService(new HttpClient(), new Serilog.LoggerConfiguration().CreateLogger()),
        new ClientVerifyService(new Serilog.LoggerConfiguration().CreateLogger()),
        new ButlerSidecar(Path.Combine(AppContext.BaseDirectory, "butler-fixture"),
            new Serilog.LoggerConfiguration().CreateLogger()),
        new StubGameProcessDetector(new Serilog.LoggerConfiguration().CreateLogger()),
        new Serilog.LoggerConfiguration().CreateLogger());

    private static async Task<VerifyReport> FinalHashScan(string root)
    {
        var loader = new ClientFileManifestLoader(new HttpClient(), new Serilog.LoggerConfiguration().CreateLogger());
        var manifest = System.Text.Json.JsonSerializer.Deserialize(
            await File.ReadAllBytesAsync(Path.Combine(SourceRoot, "files.json")),
            ClientFileManifestJsonContext.Default.ClientFileManifest)!;
        return await new ClientVerifyService(new Serilog.LoggerConfiguration().CreateLogger())
            .VerifyAsync(root, manifest);
    }

    // ── (a) — no state.json, only Data/patch-3.MPQ is missing relative to files.json ──────────────
    [SkippableFact]
    public async Task Scenario_a_no_state_json_uses_PerFile_and_fetches_only_the_one_missing_file()
    {
        SkipUnlessEnabled();
        var root = Path.Combine(WorkRoot, "v_a");
        using var server = new RangeFileServer(SourceRoot);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var outcome = await NewEngine().RunAsync(NewClient(server), root, forcePerFile: false);

        sw.Stop();
        var report = await FinalHashScan(root);
        Console.WriteLine($"(a) route={outcome.Route} state={outcome.State} bytes={outcome.BytesDownloaded} " +
                           $"time={sw.Elapsed} hashScan={report.Ok}/{report.Total} requests={server.AccessLog.Count}");
        foreach (var line in server.AccessLog) Console.WriteLine("  " + line);

        Assert.Equal(PatchRoute.PerFile, outcome.Route);
        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.True(report.IsIntact, $"missing={string.Join(",", report.Missing)} corrupt={string.Join(",", report.Corrupt)}");
    }

    // ── (b) — state.json version=1.3 present → Delta ──────────────────────────────────────────────
    [SkippableFact]
    public async Task Scenario_b_state_json_1_3_uses_Delta()
    {
        SkipUnlessEnabled();
        var root = Path.Combine(WorkRoot, "v_b");
        using var server = new RangeFileServer(SourceRoot);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var outcome = await NewEngine().RunAsync(NewClient(server), root, forcePerFile: false);

        sw.Stop();
        var report = await FinalHashScan(root);
        Console.WriteLine($"(b) route={outcome.Route} state={outcome.State} bytes={outcome.BytesDownloaded} " +
                           $"time={sw.Elapsed} hashScan={report.Ok}/{report.Total} requests={server.AccessLog.Count}");

        Assert.Equal(PatchRoute.Delta, outcome.Route);
        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.True(report.IsIntact, $"missing={string.Join(",", report.Missing)} corrupt={string.Join(",", report.Corrupt)}");
    }

    // ── (c) — 1-byte sabotage in an already-patched tree → Repair fetches exactly that file ────────
    [SkippableFact]
    public async Task Scenario_c_repair_after_sabotage_fetches_exactly_one_file()
    {
        SkipUnlessEnabled();
        var root = Path.Combine(WorkRoot, "v_c");
        using var server = new RangeFileServer(SourceRoot);

        var outcome = await NewEngine().RunAsync(NewClient(server), root, forcePerFile: true); // Repair()

        var report = await FinalHashScan(root);
        Console.WriteLine($"(c) route={outcome.Route} state={outcome.State} filesChanged={outcome.FilesChanged} " +
                           $"requests={server.AccessLog.Count} hashScan={report.Ok}/{report.Total}");
        foreach (var line in server.AccessLog) Console.WriteLine("  " + line);

        Assert.Equal(PatchRoute.PerFile, outcome.Route);
        Assert.Equal(1, outcome.FilesChanged);
        Assert.True(report.IsIntact);
    }

    // ── (d) — cancel mid-delta-download at ~50%, restart, no double download of the first half ────
    [SkippableFact]
    public async Task Scenario_d_cancel_mid_delta_then_resume_does_not_redownload_the_first_half()
    {
        SkipUnlessEnabled();
        var root = Path.Combine(WorkRoot, "v_d");
        using var server = new RangeFileServer(SourceRoot);
        server.ThrottleMsPerChunk["1.3-to-s1-test.pwr"] = 3; // ~157MB / 64KiB * 3ms ≈ 11s — enough to cancel mid-way

        using var cts = new CancellationTokenSource();
        var reachedHalfway = new TaskCompletionSource();
        var progress = new Progress<PatchProgress>(p =>
        {
            if (p.State == PatchState.Delta && p.TotalBytes > 0 && p.BytesDownloaded >= p.TotalBytes / 2)
                reachedHalfway.TrySetResult();
        });

        var engine = NewEngine();
        var client = NewClient(server);
        var runTask = engine.RunAsync(client, root, forcePerFile: false, progress, cts.Token);
        await reachedHalfway.Task;
        cts.Cancel();

        var cancelled = await Record.ExceptionAsync(() => runTask);
        Assert.IsType<OperationCanceledException>(cancelled);

        var pwrPart = Directory.GetFiles(Path.Combine(root, ".stonetavern", "cache"), "*.pwr.part").FirstOrDefault();
        Assert.NotNull(pwrPart);
        var partialLen = new FileInfo(pwrPart!).Length;
        Console.WriteLine($"(d) cancelled at {partialLen} / {client.Deltas[0].Size} bytes of the .pwr");
        Assert.InRange(partialLen, 1, client.Deltas[0].Size - 1);

        server.AccessLog.Clear();
        var outcome = await engine.RunAsync(client, root, forcePerFile: false, progress: null, CancellationToken.None);
        var report = await FinalHashScan(root);

        Console.WriteLine($"(d) resume route={outcome.Route} state={outcome.State} hashScan={report.Ok}/{report.Total}");
        foreach (var line in server.AccessLog) Console.WriteLine("  " + line);

        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.True(report.IsIntact);
        // Proof of resume, not restart: the retry's request(s) for the .pwr must be RANGED.
        Assert.Contains(server.AccessLog, l => l.Contains("1.3-to-s1-test.pwr") && l.Contains("range="));
    }

    // ── (e) — delta on a sabotaged base: apply/verify (or hash pre-check) catches it → PerFile ─────
    [SkippableFact]
    public async Task Scenario_e_delta_on_wrong_base_falls_back_to_PerFile_and_still_ends_correct()
    {
        SkipUnlessEnabled();
        var root = Path.Combine(WorkRoot, "v_e");
        using var server = new RangeFileServer(SourceRoot);

        var outcome = await NewEngine().RunAsync(NewClient(server), root, forcePerFile: false);
        var report = await FinalHashScan(root);
        Console.WriteLine($"(e) route={outcome.Route} state={outcome.State} findings=[{string.Join(",", outcome.Findings.Select(f => f.Code))}] " +
                           $"hashScan={report.Ok}/{report.Total}");

        Assert.Equal(PatchRoute.PerFile, outcome.Route);
        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.True(report.IsIntact, $"missing={string.Join(",", report.Missing)} corrupt={string.Join(",", report.Corrupt)}");
    }

    // ── (f) — empty folder → FullZip is CHOSEN, the 5 GB archive is never actually fetched ─────────
    [SkippableFact]
    public async Task Scenario_f_empty_folder_chooses_FullZip_route_without_loading_the_archive()
    {
        SkipUnlessEnabled();
        var root = Path.Combine(WorkRoot, "v_f_empty");
        Directory.CreateDirectory(root);
        using var server = new RangeFileServer(SourceRoot); // deliberately has no fullclient.zip → fails fast, proving the point

        var outcome = await NewEngine().RunAsync(NewClient(server), root, forcePerFile: false);
        Console.WriteLine($"(f) route={outcome.Route} state={outcome.State} error={outcome.ErrorMessage}");

        Assert.Equal(PatchRoute.FullZip, outcome.Route); // the decision under test
        // The zip 404s immediately — proof no multi-GB transfer was attempted, not a real install.
        Assert.Equal(PatchState.Error, outcome.State);
    }
}
