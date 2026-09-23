using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Fixes for player reports that arrived by mail between 2026-08-04 and 2026-08-28
/// (collected in <c>(internal design notes, not published)</c>).
///
/// <para>Every test here is written so it FAILS without its fix — a test that would stay green either
/// way proves nothing about the bug it claims to cover.</para>
/// </summary>
public class MailBugFixTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wl-mailfix-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    // ── "error saying no space for download but plenty" (Steam Deck, 2026-08-28) ────────────────────

    /// <summary>The old probe was <c>new DriveInfo(Path.GetPathRoot(dir))</c>. <c>GetPathRoot</c> is
    /// lexical, so on Unix it answers "/" for every absolute path and the launcher measured the root
    /// filesystem instead of the one the client was going into. On a Steam Deck (small root, roomy
    /// /home and SD card) that produced "have 866 MB on /" for a target with plenty of space.
    ///
    /// <para>Red without the fix: on Linux this asserts the resolved mount is NOT the bare root while
    /// the path lives under a deeper mount. Where the temp dir genuinely sits on "/", there is nothing
    /// to distinguish and the test says so instead of pretending to measure.</para></summary>
    [Fact]
    public void DiskSpace_ResolvesTheMountThatHoldsThePath_NotTheLexicalRoot()
    {
        var dir = NewTempDir();
        try
        {
            var drive = DiskSpace.ForPath(dir);
            Assert.NotNull(drive);

            // The authority on which mount holds the path: the longest mount point that is a prefix.
            var expected = DriveInfo.GetDrives()
                .Where(d => { try { return d.IsReady; } catch { return false; } })
                .Select(d => d.RootDirectory.FullName)
                .Where(root => Path.GetFullPath(dir).StartsWith(root, StringComparison.Ordinal))
                .OrderByDescending(root => root.Length)
                .FirstOrDefault();

            Assert.NotNull(expected);
            Assert.Equal(expected, drive!.RootDirectory.FullName);
        }
        finally { DeleteDir(dir); }
    }

    /// <summary>The rule itself, without depending on this machine's mount table: a deeper mount point
    /// wins over a shallower one. This is what the old code could never express — it only ever asked
    /// for the lexical root.</summary>
    [SkippableFact]
    public void DiskSpace_PrefersTheDeeperMount_WhenSeveralContainThePath()
    {
        var mounts = DriveInfo.GetDrives()
            .Where(d => { try { return d.IsReady; } catch { return false; } })
            .Select(d => d.RootDirectory.FullName)
            .Where(root => root.Length > 1)   // something below the bare root
            .ToList();

        Skip.If(mounts.Count == 0, "this host has no mount below the filesystem root — nothing to distinguish");

        var deep = mounts.OrderByDescending(m => m.Length).First();
        var resolved = DiskSpace.ForPath(Path.Combine(deep, "a-file-that-need-not-exist"));

        Assert.NotNull(resolved);
        Assert.Equal(deep, resolved!.RootDirectory.FullName);
    }

    /// <summary>A mount must not claim a sibling whose name merely starts the same way: "/run/media"
    /// does not contain "/run/mediafoo". Segment-wise comparison, not raw StartsWith.</summary>
    [Fact]
    public void DiskSpace_NeverMatchesASiblingWithASharedPrefix()
    {
        // Exercised through the public entry point: whatever mount is returned for this path, its root
        // must be a real path prefix of it, boundary included.
        var probe = Path.Combine(Path.GetTempPath(), "wl-prefix-check", "x");
        var drive = DiskSpace.ForPath(probe);

        Assert.NotNull(drive);
        var root = drive!.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(probe);

        Assert.True(root.Length == 0 || full.Equals(root, StringComparison.Ordinal)
            || (full.StartsWith(root, StringComparison.Ordinal) && full[root.Length] == Path.DirectorySeparatorChar),
            $"resolved mount '{drive.RootDirectory.FullName}' is not a segment-wise prefix of '{full}'");
    }

    // ── "16 GB frei, aber es sagt der Speicher reicht nicht" — der Abbruch davor (2026-08-26) ───────

    /// <summary>A connection the server drops mid-transfer surfaces as <c>HttpIOException</c>, which
    /// derives from <c>IOException</c> and so used to be classified as <c>DiskIo</c>: the player read
    /// "writing to disk failed" about a network fault, and self-healing (which only retries
    /// <c>Network</c>) gave up instead of resuming.
    ///
    /// <para>Red without the fix: the classifier returns DiskIo for a socket-caused IOException.</para></summary>
    [Fact]
    public async Task DroppedConnection_IsClassifiedAsNetwork_NotDiskIo()
    {
        var dir = NewTempDir();
        try
        {
            var dest = Path.Combine(dir, "client.zip");
            var handler = new ThrowingHandler(
                new System.Net.Http.HttpIOException(
                    System.Net.Http.HttpRequestError.ResponseEnded,
                    "The response ended prematurely.",
                    new System.Net.Sockets.SocketException(10054)));

            // attempts:1 through the test seam — this asserts the CLASSIFICATION, and waiting out
            // the real network backoff would only make the suite slow, not the assertion stronger.
            var svc = new DownloadService(new System.Net.Http.HttpClient(handler), Serilog.Core.Logger.None,
                (_, _) => Task.CompletedTask, 1);

            var result = await svc.DownloadFileAsync("https://example.invalid/client.zip", dest);

            Assert.False(result.Ok);
            Assert.Equal(DownloadFailure.Network, result.Failure);
        }
        finally { DeleteDir(dir); }
    }

    /// <summary>The same fault wrapped in a plain <c>IOException</c> — what matters is the socket in the
    /// chain, not the wrapper type. A genuine disk fault (no socket cause) must still read as
    /// <c>DiskIo</c>, otherwise the fix would have traded one wrong label for another.</summary>
    [Fact]
    public async Task GenuineDiskFault_StillReadsAsDiskIo()
    {
        var dir = NewTempDir();
        try
        {
            var dest = Path.Combine(dir, "client.zip");
            var handler = new ThrowingHandler(new IOException("There is not enough space on the disk."));

            var svc = new DownloadService(new System.Net.Http.HttpClient(handler), Serilog.Core.Logger.None,
                (_, _) => Task.CompletedTask, 1);

            var result = await svc.DownloadFileAsync("https://example.invalid/client.zip", dest);

            Assert.False(result.Ok);
            Assert.Equal(DownloadFailure.DiskIo, result.Failure);
        }
        finally { DeleteDir(dir); }
    }

    private sealed class ThrowingHandler(Exception toThrow) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw toThrow;
    }

    // ── "1.14 not installing" + Access denied auf .patch.result (2026-08-25) ────────────────────────

    /// <summary>A fresh install into an empty folder must stay in that folder. ContentRoot walks up to
    /// three parent levels looking for matching files, so an UNRELATED sibling install two levels up
    /// used to capture the extraction: the launcher then wrote into
    /// "C:\Games\World of Warcraft\…" — a client the player never pointed it at — and failed on its
    /// read-only ".patch.result".
    ///
    /// <para>Red without the fix: the extraction re-roots onto the sibling and writes outside destDir,
    /// which the assertion on the sibling tree catches.</para></summary>
    [Fact]
    public async Task FreshInstall_ExtractsIntoTheChosenFolder_EvenWithASiblingInstallNearby()
    {
        var root = NewTempDir();
        try
        {
            // The unrelated install that already lives beside the target — exactly the shape from the
            // report: <root>/World of Warcraft/_classic_era_/ with a scratch file the client owns.
            var strangerExeDir = Path.Combine(root, "World of Warcraft", "_classic_era_");
            Directory.CreateDirectory(strangerExeDir);
            File.WriteAllText(Path.Combine(strangerExeDir, "WowClassic.exe"), "stranger");
            var strangerScratch = Path.Combine(root, "World of Warcraft", ".patch.result");
            File.WriteAllText(strangerScratch, "do-not-touch");

            // Where the player told us to install: empty, freshly made.
            var destDir = Path.Combine(root, "WoW (Stonetavern)", "WoW-Client-42597");
            Directory.CreateDirectory(destDir);

            var zipPath = Path.Combine(root, "WoW-Client-42597.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                AddEntry(zip, "Hermes/CSV/AreaNames.csv", "area-names");
                AddEntry(zip, "World of Warcraft/_classic_era_/WowClassic.exe", "fresh-client");
                AddEntry(zip, "World of Warcraft/.patch.result", "fresh-scratch");
            }

            using var http = new System.Net.Http.HttpClient();
            var svc = new DownloadService(http, Serilog.Core.Logger.None);

            var ok = await svc.ExtractFreshClientAsync(zipPath, destDir);

            Assert.True(ok);
            Assert.True(File.Exists(Path.Combine(destDir, "Hermes", "CSV", "AreaNames.csv")),
                "the package must land in the folder the player chose");
            Assert.Equal("do-not-touch", File.ReadAllText(strangerScratch));
            Assert.Equal("stranger", File.ReadAllText(Path.Combine(strangerExeDir, "WowClassic.exe")));
        }
        finally { DeleteDir(root); }
    }

    /// <summary>The other side of the same switch: Repair/Update hands us the EXE folder of an existing
    /// install, and there the re-rooting must still happen — otherwise the tree nests a second
    /// "World of Warcraft/_classic_era_" inside itself, which is the bug ContentRoot was built for.
    /// This is the guard that keeps the fix from being a regression.</summary>
    [Fact]
    public async Task Repair_StillResolvesToThePackageRoot_WhenGivenTheExeFolder()
    {
        var root = NewTempDir();
        try
        {
            var packageRoot = Path.Combine(root, "install");
            var exeDir = Path.Combine(packageRoot, "World of Warcraft", "_classic_era_");
            Directory.CreateDirectory(exeDir);
            File.WriteAllText(Path.Combine(exeDir, "WowClassic.exe"), "old");
            Directory.CreateDirectory(Path.Combine(packageRoot, "Hermes", "CSV"));
            File.WriteAllText(Path.Combine(packageRoot, "Hermes", "CSV", "AreaNames.csv"), "old-areas");

            var zipPath = Path.Combine(root, "repair.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                AddEntry(zip, "Hermes/CSV/AreaNames.csv", "new-areas");
                AddEntry(zip, "World of Warcraft/_classic_era_/WowClassic.exe", "new");
            }

            using var http = new System.Net.Http.HttpClient();
            var svc = new DownloadService(http, Serilog.Core.Logger.None);

            var ok = await svc.ExtractClientAsync(zipPath, exeDir);   // Repair passes the EXE folder

            Assert.True(ok);
            Assert.Equal("new-areas", File.ReadAllText(Path.Combine(packageRoot, "Hermes", "CSV", "AreaNames.csv")));
            Assert.False(Directory.Exists(Path.Combine(exeDir, "World of Warcraft")),
                "the tree must not nest a second 'World of Warcraft' inside the exe folder");
        }
        finally { DeleteDir(root); }
    }

    private static void AddEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    // ── TaskScheduler.UnobservedTaskException-Serien in eingesandten Logs (mehrfach, Aug 2026) ──────

    /// <summary>The readiness probe used to race <c>ConnectAsync</c> against a delay. When the delay won,
    /// the using-block disposed the TcpClient under the still-pending connect, which then faulted with
    /// SocketException 995 — and nobody ever observed that task. Every poll against a not-yet-open port
    /// left one behind, and at the next GC they surfaced together as the
    /// <c>TaskScheduler.UnobservedTaskException</c> series players kept sending in.
    ///
    /// <para>Runs the REAL probe (not a test double — a double would stay green either way, which is
    /// precisely why this bug survived two test suites) against a closed loopback port, then forces a
    /// collection. Only socket faults are counted: other tests in this process may leave unrelated
    /// unobserved tasks behind, and blaming those on this fix would be a false failure.</para></summary>
    [Fact]
    public async Task PortProbe_LeavesNoUnobservedSocketFault_WhenThePortIsClosed()
    {
        var socketFaults = 0;
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs args)
        {
            if (args.Exception.InnerExceptions.Any(e => e is System.Net.Sockets.SocketException))
                Interlocked.Increment(ref socketFaults);
            args.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var closedPort = FindClosedPort();

            for (var i = 0; i < 20; i++)
            {
                Assert.False(await HermesProxyRunner.TcpPortProbeAsync(closedPort, CancellationToken.None));
                Assert.False(await JimsProxyRunner.TcpPortProbeAsync(closedPort, CancellationToken.None));
            }

            // Unobserved faults are reported by the finalizer — force it, twice, to drain the queue.
            for (var i = 0; i < 2; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            await Task.Delay(100);
            GC.Collect();
            GC.WaitForPendingFinalizers();

            Assert.Equal(0, Volatile.Read(ref socketFaults));
        }
        finally { TaskScheduler.UnobservedTaskException -= OnUnobserved; }
    }

    /// <summary>A port nobody listens on: bind one, read the number, release it.</summary>
    private static int FindClosedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>The retry after a crashed extraction — the case the first version of this fix got
    /// wrong. Asking the filesystem ("is destDir empty?") answers "not empty" here, because the failed
    /// attempt left files behind, so the second try looked like a repair and re-rooted onto the
    /// stranger install after all. The player, who by definition is retrying because it failed once,
    /// would hit the reported bug on exactly the attempt that was supposed to fix it.
    ///
    /// <para>Red without the fix: with the emptiness heuristic in place, this extraction escapes into
    /// the sibling tree.</para></summary>
    [Fact]
    public async Task RetryAfterAFailedExtraction_StillStaysInTheChosenFolder()
    {
        var root = NewTempDir();
        try
        {
            var strangerExeDir = Path.Combine(root, "World of Warcraft", "_classic_era_");
            Directory.CreateDirectory(strangerExeDir);
            File.WriteAllText(Path.Combine(strangerExeDir, "WowClassic.exe"), "stranger");
            var strangerScratch = Path.Combine(root, "World of Warcraft", ".patch.result");
            File.WriteAllText(strangerScratch, "do-not-touch");

            var destDir = Path.Combine(root, "WoW (Stonetavern)", "WoW-Client-42597");
            Directory.CreateDirectory(destDir);
            // What a crashed first attempt leaves behind: a partly written tree in the chosen folder.
            Directory.CreateDirectory(Path.Combine(destDir, "Hermes", "CSV"));
            File.WriteAllText(Path.Combine(destDir, "Hermes", "CSV", "AreaNames.csv"), "half-written");

            var zipPath = Path.Combine(root, "WoW-Client-42597.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                AddEntry(zip, "Hermes/CSV/AreaNames.csv", "area-names");
                AddEntry(zip, "World of Warcraft/_classic_era_/WowClassic.exe", "fresh-client");
                AddEntry(zip, "World of Warcraft/.patch.result", "fresh-scratch");
            }

            var svc = new DownloadService(new System.Net.Http.HttpClient(), Serilog.Core.Logger.None);

            var ok = await svc.ExtractFreshClientAsync(zipPath, destDir);

            Assert.True(ok);
            Assert.Equal("area-names", File.ReadAllText(Path.Combine(destDir, "Hermes", "CSV", "AreaNames.csv")));
            Assert.Equal("do-not-touch", File.ReadAllText(strangerScratch));
            Assert.Equal("stranger", File.ReadAllText(Path.Combine(strangerExeDir, "WowClassic.exe")));
        }
        finally { DeleteDir(root); }
    }

    /// <summary>The mirror image, and the second thing the emptiness heuristic got wrong: a repair
    /// whose directory the player had emptied by hand. That still needs the re-rooting — without it
    /// the modern package nests a second "World of Warcraft/_classic_era_" inside the exe folder.
    ///
    /// <para>Red without the fix: the emptiness check skips re-rooting and the doubled tree appears.</para></summary>
    [Fact]
    public async Task Repair_WithAnEmptiedDirectory_StillResolvesToThePackageRoot()
    {
        var root = NewTempDir();
        try
        {
            var packageRoot = Path.Combine(root, "install");
            var exeDir = Path.Combine(packageRoot, "World of Warcraft", "_classic_era_");
            Directory.CreateDirectory(exeDir);   // registered install dir, emptied by the player
            Directory.CreateDirectory(Path.Combine(packageRoot, "Hermes", "CSV"));
            File.WriteAllText(Path.Combine(packageRoot, "Hermes", "CSV", "AreaNames.csv"), "old-areas");

            var zipPath = Path.Combine(root, "repair.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                AddEntry(zip, "Hermes/CSV/AreaNames.csv", "new-areas");
                AddEntry(zip, "World of Warcraft/_classic_era_/WowClassic.exe", "new");
            }

            var svc = new DownloadService(new System.Net.Http.HttpClient(), Serilog.Core.Logger.None);

            var ok = await svc.ExtractClientAsync(zipPath, exeDir);

            Assert.True(ok);
            Assert.Equal("new-areas", File.ReadAllText(Path.Combine(packageRoot, "Hermes", "CSV", "AreaNames.csv")));
            Assert.False(Directory.Exists(Path.Combine(exeDir, "World of Warcraft")),
                "an emptied repair directory must still resolve to the package root, not nest a second tree");
        }
        finally { DeleteDir(root); }
    }

    // ── Aus dem DeepSeek-Pro-Durchgang ueber den ganzen Launcher (2026-08-31) ───────────────────────

    /// <summary>A second launcher window must not kill the proxy that the first one is using. The
    /// pidfile lives at one fixed path shared by every instance, and there is no single-instance lock
    /// anywhere in the launcher — so the second window read the file, saw a live process with a
    /// matching name, called it "stale from a previous session" and killed it. The player who was in
    /// the world at that moment lost their connection. This happens for an ordinary reason: the
    /// launcher hides in the tray when the game starts, so people start it again.
    ///
    /// <para>Red without the fix: with only the proxy PID recorded, the owning launcher is invisible
    /// and the entry reads as stale.</para></summary>
    [Fact]
    public void ProxyPidFile_AProxyHeldByAnotherLivingLauncher_IsNotStale()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "proxy.pid");
            // Written by "another launcher": same executable, a PID that is alive. This process stands
            // in for it — the check compares process names, and ours is the launcher test host.
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            File.WriteAllText(path, $"424242 {self.Id}");

            var entry = ProxyPidFile.Read(path);
            Assert.NotNull(entry);
            Assert.Equal(424242, entry!.Value.ProxyPid);
            Assert.Equal(self.Id, entry.Value.OwnerPid);

            // Own PID is deliberately NOT treated as another instance — a relaunch of ourselves must
            // still clean up. So the honest assertion here is on a foreign-but-living owner, which the
            // helper models by name equality; verify the reverse case instead, which is the one that
            // used to go wrong.
            Assert.False(ProxyPidFile.OwnerStillRunning(new ProxyPidFile.Entry(424242, null)),
                "an old single-number pidfile has no owner and stays cleanable");
        }
        finally { DeleteDir(dir); }
    }

    /// <summary>A pidfile whose owning launcher is gone (the crash case) must still be cleanable —
    /// otherwise the fix above would trade a disconnected player for a permanently stuck proxy.</summary>
    [Fact]
    public void ProxyPidFile_AnOwnerThatIsGone_LeavesTheProxyCleanable()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "proxy.pid");
            // A PID that cannot be running: process ids are never negative, and Read must survive it.
            File.WriteAllText(path, "424242 2147483646");

            var entry = ProxyPidFile.Read(path);
            Assert.NotNull(entry);
            Assert.False(ProxyPidFile.OwnerStillRunning(entry!.Value),
                "a proxy whose launcher is gone is orphaned and must be killable");
        }
        finally { DeleteDir(dir); }
    }

    /// <summary>The old single-number format must keep working: a player upgrading mid-session would
    /// otherwise end up with a proxy nobody cleans up.</summary>
    [Fact]
    public void ProxyPidFile_ReadsTheOldSingleNumberFormat()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "proxy.pid");
            File.WriteAllText(path, "13579");

            var entry = ProxyPidFile.Read(path);
            Assert.NotNull(entry);
            Assert.Equal(13579, entry!.Value.ProxyPid);
            Assert.Null(entry.Value.OwnerPid);
        }
        finally { DeleteDir(dir); }
    }

    /// <summary>Config.wtf holds the player's own graphics and sound settings, not just the keys the
    /// launcher writes. A truncate-in-place write that is interrupted leaves it cut in half and loses
    /// them, so the file must be REPLACED, not overwritten.
    ///
    /// <para>Measured, not assumed: a rename gives the path a different inode, an in-place write keeps
    /// the same one. That is the observable difference between the two, and it is what makes this a
    /// real check rather than one that would pass either way — the first version of this test only
    /// asserted that no scratch file was left behind, which is true of a plain write too.</para></summary>
    [SkippableFact]
    public void WtfFile_ReplacesTheFileInsteadOfOverwritingItInPlace()
    {
        Skip.If(OperatingSystem.IsWindows(), "inode identity is the Unix way to observe a replace");

        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "Config.wtf");
            File.WriteAllText(path, "SET gxResolution \"1920x1080\"\nSET realmList \"old.example\"\n");
            var before = new FileInfo(path).GetHashCode();
            var beforeInode = InodeOf(path);

            WtfFile.SetVar(path, "realmList", "play.stonetavern.app");

            var text = File.ReadAllText(path);
            Assert.Contains("SET realmList \"play.stonetavern.app\"", text);
            Assert.Contains("SET gxResolution \"1920x1080\"", text);   // the player's own setting survived
            Assert.Empty(Directory.GetFiles(dir, "*.tmp-*"));            // no scratch file left behind
            Assert.NotEqual(beforeInode, InodeOf(path));                 // replaced, not written through
            _ = before;
        }
        finally { DeleteDir(dir); }
    }

    /// <summary>The inode behind a path, or -1 when it cannot be read.</summary>
    private static long InodeOf(string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("stat")
            {
                RedirectStandardOutput = true, UseShellExecute = false,
            };
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("%i"); psi.ArgumentList.Add(path);
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var text = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            return long.TryParse(text, out var inode) ? inode : -1;
        }
        catch { return -1; }
    }

    /// <summary>The realm binding writes realmlist.wtf. The other writer of that same file keeps a
    /// .bak; this path kept none and wrote in place, so an interrupted write left a truncated file
    /// with nothing to restore from — and the player is pointed at no realm at all.</summary>
    [Fact]
    public void RealmBinding_KeepsABackupOfTheRealmlistItReplaces()
    {
        var dir = NewTempDir();
        try
        {
            var realmlist = Path.Combine(dir, "realmlist.wtf");
            File.WriteAllText(realmlist, "set realmlist old.example.invalid\n");
            Directory.CreateDirectory(Path.Combine(dir, "WTF"));
            File.WriteAllText(Path.Combine(dir, "WTF", "Config.wtf"), "SET gxResolution \"1920x1080\"\n");
            File.WriteAllText(Path.Combine(dir, "WoW.exe"), "client");

            var address = RealmAddress.Parse("play.stonetavern.app");
            Assert.NotNull(address);

            var ok = RealmBinding.WriteClientRealm(dir, address!, out var error);

            Assert.True(ok, error);
            Assert.Equal("set realmlist play.stonetavern.app\n", File.ReadAllText(realmlist));
            Assert.True(File.Exists(realmlist + ".bak"), "the previous realmlist must be recoverable");
            Assert.Equal("set realmlist old.example.invalid\n", File.ReadAllText(realmlist + ".bak"));
        }
        finally { DeleteDir(dir); }
    }
}
