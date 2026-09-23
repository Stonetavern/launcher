using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Patching;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests.Patching;

/// <summary>
/// AP5, Linux: the real patch engine against the ONE unified 1.14.2 tree built on 2026-09-20. The
/// install under test is a hardlink copy of the live Linux v1.4.3 ZIP — the state a player has
/// tomorrow, without <c>client-state.json</c> — and the server root is the real
/// <c>client-release.py build</c> output (files.json with per-file os tags, tree/, deltas).
///
/// <para>Opt-in via <c>STONETAVERN_E2E=1</c> and only on this machine (the tree lives under
/// <c>/mnt/data/wow/s4-2026-09-20-ein-baum</c>). Never points at the production manifest.</para>
/// </summary>
public sealed class PatchEngineUnifiedE2ETests
{
    private const string DistRoot = "/mnt/data/wow/s4-2026-09-20-ein-baum/dist/modern-1.14.2/all/1.5.0";
    private const string WorkRoot = "/mnt/data/wow/s5-2026-09-20";
    private static readonly string ButlerBinary = Path.Combine(AppContext.BaseDirectory,
        "butler-fixture", "tools", "butler", "linux-x64", "butler");

    /// <summary>The extracted live Linux v1.4.3 ZIP, whichever top-level folder the ZIP uses.</summary>
    private static string? FindLinuxSource()
    {
        foreach (var candidate in new[]
                 {
                     "/mnt/data/wow/s4-2026-09-20-ein-baum/src-linux/Stonetavern",
                     "/mnt/data/wow/s4-2026-09-20-ein-baum/src-linux",
                 })
        {
            if (Directory.Exists(Path.Combine(candidate, "World of Warcraft"))) return candidate;
        }
        return null;
    }

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("STONETAVERN_E2E") == "1"
        && OperatingSystem.IsLinux()
        && File.Exists(Path.Combine(DistRoot, "files.json"))
        && FindLinuxSource() is not null
        && File.Exists(ButlerBinary);

    private static void SkipUnlessEnabled() =>
        Skip.IfNot(Enabled, "set STONETAVERN_E2E=1 on the machine that built /mnt/data/wow/s4-2026-09-20-ein-baum");

    private static string FreshInstall(string scenario)
    {
        var work = Path.Combine(WorkRoot, scenario);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        HardlinkCopy(FindLinuxSource()!, work);
        return work;
    }

    /// <summary>Hardlink every file, like <c>cp -al</c>: the copy costs directory entries, not 8 GB.</summary>
    private static void HardlinkCopy(string source, string target)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (link(file, dst) != 0) File.Copy(file, dst);
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int link(string oldpath, string newpath);

    private static ManifestFile NewClient(RangeFileServer server)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(DistRoot, "manifest-block.json")));
        var block = doc.RootElement;
        var deltas = block.GetProperty("deltas").EnumerateArray().Select(d => new ManifestDelta
        {
            From = d.GetProperty("from").GetString()!,
            // The block carries the production URLs; this run never touches the production manifest or
            // CDN, so every delta is fetched from the local server root (the files sit next to
            // files.json — `publish` has not happened).
            Url = server.BaseUrl + Path.GetFileName(new Uri(d.GetProperty("url").GetString()!).LocalPath),
            Size = d.GetProperty("size").GetInt64(),
            Sha256 = d.GetProperty("sha256").GetString()!,
            SigUrl = server.BaseUrl + Path.GetFileName(new Uri(d.GetProperty("sig_url").GetString()!).LocalPath),
            SigSha256 = d.GetProperty("sig_sha256").GetString()!,
        }).ToList();

        return new ManifestFile
        {
            Build = block.GetProperty("build").GetInt32(),
            Version = block.GetProperty("version").GetString()!,
            Url = server.BaseUrl + "missing-fullclient.zip",  // must never be fetched on a patched install
            Sha256 = "",
            FilesUrl = server.BaseUrl + "files.json",
            FilesSha256 = block.GetProperty("files_sha256").GetString()!,
            FilesBase = server.BaseUrl + "tree/",
            Deltas = deltas,
            Protected = block.GetProperty("protected").EnumerateArray().Select(p => p.GetString()!).ToList(),
        };
    }

    private static ClientPatchEngine NewEngine() => new(
        new ClientFileManifestLoader(new HttpClient(), new Serilog.LoggerConfiguration().CreateLogger()),
        new DownloadService(new HttpClient(), new Serilog.LoggerConfiguration().CreateLogger()),
        new ClientVerifyService(new Serilog.LoggerConfiguration().CreateLogger()),
        new ButlerSidecar(Path.Combine(AppContext.BaseDirectory, "butler-fixture"),
            new Serilog.LoggerConfiguration().CreateLogger()),
        new StubGameProcessDetector(new Serilog.LoggerConfiguration().CreateLogger()),
        new Serilog.LoggerConfiguration().CreateLogger());

    private static async Task<VerifyReport> FinalScan(string root)
    {
        var manifest = JsonSerializer.Deserialize(
            await File.ReadAllBytesAsync(Path.Combine(DistRoot, "files.json")),
            ClientFileManifestJsonContext.Default.ClientFileManifest)!;
        // Only the files THIS OS runs: a Linux install legitimately lacks the Windows/macOS proxies
        // (KONZEPT §13), and an unfiltered scan would report them as missing on a healthy tree.
        var linux = new ClientFileManifest
        {
            Build = manifest.Build,
            Version = manifest.Version,
            Protected = manifest.Protected,
            Files = manifest.Files.Where(f => f.Os is not { Count: > 0 } || f.AppliesTo("linux")).ToList(),
        };
        return await new ClientVerifyService(new Serilog.LoggerConfiguration().CreateLogger())
            .VerifyAsync(root, linux);
    }

    // ── (1) first run: PER_FILE, only the changed files, old layout reported, second run UpToDate ──

    [SkippableFact]
    public async Task FirstRun_Patches_PerFile_Reports_The_Old_Layout_And_The_Second_Run_Is_UpToDate()
    {
        SkipUnlessEnabled();
        var root = FreshInstall("v_perfile");
        using var server = new RangeFileServer(DistRoot);
        var engine = NewEngine();
        var client = NewClient(server);

        var first = await engine.RunAsync(client, root, forcePerFile: false);
        var report = await FinalScan(root);

        Console.WriteLine($"(1) first route={first.Route} state={first.State} changed={first.FilesChanged} " +
                          $"bytes={first.BytesDownloaded} requests={server.AccessLog.Count}");
        foreach (var line in server.AccessLog) Console.WriteLine("  " + line);

        Assert.Equal(PatchRoute.PerFile, first.Route);
        Assert.Equal(PatchState.Ready, first.State);
        Assert.True(report.IsIntact, $"missing={string.Join(",", report.Missing)} corrupt={string.Join(",", report.Corrupt)}");

        // The Linux half of the unified tree arrived, the foreign OS parts did not. The trailing space
        // keeps "HermesProxy.config" (a shared file, legitimately fetched) out of the HermesProxy check.
        Assert.Contains(server.AccessLog, l => l.Contains("/tree/Hermes/bin/JimsProxy-linux-x64 "));
        Assert.Contains(server.AccessLog, l => l.Contains("/tree/Hermes/CSV/"));
        Assert.DoesNotContain(server.AccessLog, l => l.Contains("/tree/Hermes/JimsProxy.exe "));
        Assert.DoesNotContain(server.AccessLog, l => l.Contains("/tree/Hermes/HermesProxy "));

        // The old Hermes/linux/ layout stays (reported, never deleted).
        Assert.Contains(first.Findings, f => f.Code == PatchFinding.ForeignFile && f.Message.Contains("Hermes/linux/"));
        Assert.True(File.Exists(Path.Combine(root, "Hermes", "linux", "JimsProxy")),
            "the v1.4.3 old-layout proxy was deleted");
        Assert.InRange(first.BytesDownloaded, 1, 400L * 1024 * 1024); // ~110 MB expected, not the 8 GB tree

        var statePath = Path.Combine(root, ".stonetavern", "client-state.json");
        Assert.True(File.Exists(statePath), "client-state.json was not written");

        server.AccessLog.Clear();
        var second = await engine.RunAsync(client, root, forcePerFile: false);
        Console.WriteLine($"(1) second route={second.Route} state={second.State} requests={server.AccessLog.Count}");
        Assert.Equal(PatchRoute.UpToDate, second.Route);
        Assert.Equal(PatchState.Ready, second.State);
        // files.json is always fetched (that IS how the state is compared); not a single tree file.
        Assert.Empty(server.AccessLog.Where(l => l.Contains("/tree/")));
    }

    // ── (2) sabotage one file → repair fetches exactly that one ────────────────────────────────────

    [SkippableFact]
    public async Task Sabotaged_File_Is_Repaired_With_Exactly_One_Request()
    {
        SkipUnlessEnabled();
        var root = FreshInstall("v_sabotage");
        using var server = new RangeFileServer(DistRoot);
        var engine = NewEngine();
        var client = NewClient(server);

        var first = await engine.RunAsync(client, root, forcePerFile: false);
        Assert.Equal(PatchState.Ready, first.State);

        var victim = Path.Combine(root, "Hermes", "bin", "JimsProxy-linux-x64");
        var original = File.ReadAllBytes(victim);
        original[0] ^= 0xFF;
        File.WriteAllBytes(victim, original);

        server.AccessLog.Clear();
        var repair = await engine.RunAsync(client, root, forcePerFile: true);
        var report = await FinalScan(root);

        Console.WriteLine($"(2) repair route={repair.Route} state={repair.State} changed={repair.FilesChanged} " +
                          $"requests={server.AccessLog.Count}");
        foreach (var line in server.AccessLog) Console.WriteLine("  " + line);

        Assert.Equal(PatchRoute.PerFile, repair.Route);
        Assert.Equal(PatchState.Ready, repair.State);
        Assert.Equal(1, repair.FilesChanged);
        var treeRequests = server.AccessLog.Where(l => l.Contains("tree/")).ToList();
        Assert.Single(treeRequests);
        Assert.Contains("JimsProxy-linux-x64", treeRequests[0]);
        Assert.True(report.IsIntact);
    }

    // ── (3) cancel mid-download → clean restart, the partial file is resumed, not re-fetched ───────

    [SkippableFact]
    public async Task Cancel_Mid_Download_Then_Restart_Resumes_The_Partial_File()
    {
        SkipUnlessEnabled();
        var root = FreshInstall("v_cancel");
        using var server = new RangeFileServer(DistRoot);
        server.ThrottleMsPerChunk["tree/Hermes/bin/JimsProxy-linux-x64"] = 4; // ~80MB: a few seconds
        var engine = NewEngine();
        var client = NewClient(server);

        using var cts = new CancellationTokenSource();
        var reachedHalfway = new TaskCompletionSource();
        var progress = new Progress<PatchProgress>(p =>
        {
            if (p.State == PatchState.PerFile && p.CurrentFile == "Hermes/bin/JimsProxy-linux-x64"
                && p.TotalBytes > 0 && p.BytesDownloaded >= p.TotalBytes / 2)
                reachedHalfway.TrySetResult();
        });

        var runTask = engine.RunAsync(client, root, forcePerFile: false, progress, cts.Token);
        var first = await Task.WhenAny(reachedHalfway.Task, Task.Delay(TimeSpan.FromMinutes(4)));
        Assert.True(first == reachedHalfway.Task,
            "the throttled proxy never reported half its bytes within 4 minutes — no unbounded wait");
        cts.Cancel();
        var cancelled = await Record.ExceptionAsync(() => runTask);
        Assert.IsType<OperationCanceledException>(cancelled);

        var partPath = Path.Combine(root, "Hermes", "bin", "JimsProxy-linux-x64.part");
        Assert.True(File.Exists(partPath), $"no .part file at {partPath}");
        var partialLen = new FileInfo(partPath).Length;
        Console.WriteLine($"(3) cancelled at {partialLen} bytes of the proxy");
        Assert.InRange(partialLen, 1, 79_894_911 - 1);

        server.AccessLog.Clear();
        var resumed = await engine.RunAsync(client, root, forcePerFile: true, progress: null, CancellationToken.None);
        var report = await FinalScan(root);

        Console.WriteLine($"(3) resume route={resumed.Route} state={resumed.State}");
        foreach (var line in server.AccessLog) Console.WriteLine("  " + line);

        Assert.Equal(PatchState.Ready, resumed.State);
        Assert.True(report.IsIntact);
        Assert.Contains(server.AccessLog, l => l.Contains("JimsProxy-linux-x64") && l.Contains("range="));
    }

    // ── (4) a seeded 1.4.3-linux state takes the DELTA (real butler), butler verify is the gate ────

    [SkippableFact]
    public async Task Seeded_Linux_State_Takes_The_Delta()
    {
        SkipUnlessEnabled();
        var root = FreshInstall("v_delta");
        // The delta was built from the extracted live Linux tree; the engine picks it by the recorded
        // version string (KONZEPT: Deltas heissen 1.4.0-windows / 1.4.3-linux / 1.4.3-macos).
        Directory.CreateDirectory(Path.Combine(root, ".stonetavern"));
        await File.WriteAllTextAsync(Path.Combine(root, ".stonetavern", "client-state.json"),
            JsonSerializer.Serialize(new ClientPatchState
            {
                Build = 42597, Os = "linux", Version = "1.4.3-linux", FilesSha256 = "seeded",
                VerifiedAt = DateTimeOffset.UtcNow,
            }));

        using var server = new RangeFileServer(DistRoot);
        var engine = NewEngine();
        var client = NewClient(server);

        var outcome = await engine.RunAsync(client, root, forcePerFile: false);
        var report = await FinalScan(root);

        var fellBack = outcome.Findings.FirstOrDefault(f => f.Code == PatchFinding.DeltaFellBack);
        Console.WriteLine($"(4) route={outcome.Route} state={outcome.State} bytes={outcome.BytesDownloaded} " +
                          $"requests={server.AccessLog.Count} fallback={fellBack?.Message}");
        foreach (var line in server.AccessLog) Console.WriteLine("  " + line);

        Assert.Equal(PatchRoute.Delta, outcome.Route);
        Assert.Equal(PatchState.Ready, outcome.State);
        Assert.True(report.IsIntact, $"missing={string.Join(",", report.Missing)} corrupt={string.Join(",", report.Corrupt)}");
    }
}
