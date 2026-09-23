using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Pins the fixes from the Windows A-to-Z reading of 2026-09-05
/// (<c>BEFUND-windows-a-bis-z-2026-09-05.md</c>): W1 a foreign client capturing the extraction,
/// W2 one held file killing a multi-gigabyte install, W3 the crash log displacing the real log in a
/// report, W7 the swap script starting a second launcher on top of one that never exited.
/// Each test is red with its fix reverted — that was checked, not assumed.
/// </summary>
public sealed class WindowsAToZFixTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wl-atoz-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDir(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static void Write(string root, string rel, string content)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void AddEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path);
        using var w = new StreamWriter(entry.Open());
        w.Write(content);
    }

    private static readonly string[] ModernPackage =
    {
        "Hermes/CSV/AreaNames.csv",
        "Hermes/CSV/AuraSpells1.csv",
        "World of Warcraft/_classic_era_/WowClassic.exe",
        "World of Warcraft/_classic_era_/Data/patch.mpq",
    };

    // ── W1: a foreign "World of Warcraft" folder must never win the root vote ───────────────────

    /// <summary>The exact shape of the three 2026-09-04 reports: the player's install lives at
    /// <c>Games\Stonetavern\…</c>, a Blizzard client at <c>Games\World of Warcraft\…</c>, and the
    /// Stonetavern exe folder has been emptied (a failed first attempt, a manual clean-up). The
    /// retail client answers 2 of 4 samples from <c>Games</c>; the emptied install answers none.
    /// Red without the fix: <c>Games</c> wins, and the package unpacks into the Blizzard client.</summary>
    [Fact]
    public void Resolve_NeverPicksAParent_ThePackageDoesNotDescribe()
    {
        var root = NewTempDir();
        try
        {
            var games = Path.Combine(root, "Games");
            var packageRoot = Path.Combine(games, "Stonetavern");
            var exeDir = Path.Combine(packageRoot, "World of Warcraft", "_classic_era_");
            Directory.CreateDirectory(exeDir);
            Write(games, "World of Warcraft/_classic_era_/WowClassic.exe", "retail");
            Write(games, "World of Warcraft/_classic_era_/Data/patch.mpq", "retail");

            var resolved = ContentRoot.Resolve(exeDir, ModernPackage);

            Assert.Equal(packageRoot, resolved);
        }
        finally { DeleteDir(root); }
    }

    /// <summary>The player's install sits directly on the drive: <c>E:\World of Warcraft\_classic_era_</c>
    /// with <c>E:\Hermes</c> beside it. That parent IS described by the package (two levels, exactly
    /// the inner path), so it stays reachable — the fix must not throw the baby out.</summary>
    [Fact]
    public void Resolve_StillReachesAParent_ThePackageDoesDescribe()
    {
        var root = NewTempDir();
        try
        {
            var exeDir = Path.Combine(root, "World of Warcraft", "_classic_era_");
            Directory.CreateDirectory(exeDir);
            Write(root, "Hermes/CSV/AreaNames.csv", "areas");

            Assert.Equal(root, ContentRoot.Resolve(exeDir, ModernPackage));
        }
        finally { DeleteDir(root); }
    }

    /// <summary>Nothing on disk at all, but the exe folder is named like the package's inner path:
    /// the package root is two levels up by construction, and extracting into the exe folder would
    /// nest a second tree. Red without the fix: the start directory comes back.</summary>
    [Fact]
    public void Resolve_AnEmptiedModernInstall_ResolvesToThePackageRoot_WithoutEvidence()
    {
        var root = NewTempDir();
        try
        {
            var packageRoot = Path.Combine(root, "Stonetavern");
            var exeDir = Path.Combine(packageRoot, "World of Warcraft", "_classic_era_");
            Directory.CreateDirectory(exeDir);

            Assert.Equal(packageRoot, ContentRoot.Resolve(exeDir, ModernPackage));
        }
        finally { DeleteDir(root); }
    }

    /// <summary>The flat Vanilla layout has no inner path, so an empty folder is its own root.</summary>
    [Fact]
    public void Resolve_AnEmptiedFlatInstall_StaysWhereItIs()
    {
        var root = NewTempDir();
        try
        {
            var clientDir = Path.Combine(root, "WoW-Client-42597");
            Directory.CreateDirectory(clientDir);
            Write(root, "WoW.exe", "some other client one level up");

            Assert.Equal(clientDir, ContentRoot.Resolve(clientDir, new[] { "WoW.exe", "Data/patch.MPQ" }));
        }
        finally { DeleteDir(root); }
    }

    [Theory]
    [InlineData(0, true)]   // the start directory itself
    [InlineData(1, false)]  // "_classic_era_" alone is not a top-level package directory
    [InlineData(2, true)]   // "World of Warcraft/_classic_era_" is exactly the package's inner path
    [InlineData(3, false)]  // "Stonetavern/World of Warcraft/_classic_era_" — the package knows no such dir
    public void IsAdmissible_MatchesTheTailOfTheStartDirectory_AgainstThePackage(int level, bool expected)
    {
        var startParts = new[] { "E:", "Games", "Stonetavern", "World of Warcraft", "_classic_era_" };
        var packageDirs = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "World of Warcraft",
            Path.Combine("World of Warcraft", "_classic_era_"),
            Path.Combine("World of Warcraft", "_classic_era_", "Data"),
            "Hermes",
        };
        Assert.Equal(expected, ContentRoot.IsAdmissible(level, startParts, packageDirs));
    }

    /// <summary>End to end through the real extractor, as Repair calls it: the decoy client one level
    /// above the package root is left untouched and the package lands in the player's tree.</summary>
    [Fact]
    public async Task Repair_WithADecoyClientNearby_ExtractsIntoThePlayersTree()
    {
        var root = NewTempDir();
        try
        {
            var games = Path.Combine(root, "Games");
            var packageRoot = Path.Combine(games, "Stonetavern");
            var exeDir = Path.Combine(packageRoot, "World of Warcraft", "_classic_era_");
            Directory.CreateDirectory(exeDir);
            Write(games, "World of Warcraft/_classic_era_/WowClassic.exe", "retail");
            Write(games, "World of Warcraft/_classic_era_/Data/patch.mpq", "retail");

            var zipPath = Path.Combine(root, "repair.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var p in ModernPackage) AddEntry(zip, p, "stonetavern");
            }

            var svc = new DownloadService(new System.Net.Http.HttpClient(), Serilog.Core.Logger.None);
            var ok = await svc.ExtractClientAsync(zipPath, exeDir);

            Assert.True(ok);
            Assert.Equal("stonetavern", File.ReadAllText(Path.Combine(exeDir, "WowClassic.exe")));
            Assert.Equal("stonetavern", File.ReadAllText(Path.Combine(packageRoot, "Hermes", "CSV", "AreaNames.csv")));
            Assert.Equal("retail", File.ReadAllText(Path.Combine(games, "World of Warcraft", "_classic_era_", "WowClassic.exe")));
            Assert.False(Directory.Exists(Path.Combine(games, "Hermes")), "the decoy client's parent must stay untouched");
        }
        finally { DeleteDir(root); }
    }

    // ── W2: one held or protected file must not sink the whole install ─────────────────────────

    /// <summary>A read-only file in the target — Blizzard's installer sets that on some, backup
    /// tools on others — is what an update overwrites. Red without the fix:
    /// <c>UnauthorizedAccessException</c> on the first entry and a false return.</summary>
    [Fact]
    public async Task Extract_OverwritesAReadOnlyFile_InsteadOfDying()
    {
        var root = NewTempDir();
        try
        {
            var dest = Path.Combine(root, "client");
            Write(dest, "chrome_elf.dll", "old");
            var held = Path.Combine(dest, "chrome_elf.dll");
            File.SetAttributes(held, File.GetAttributes(held) | FileAttributes.ReadOnly);

            var zipPath = Path.Combine(root, "client.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                AddEntry(zip, "chrome_elf.dll", "new");
                AddEntry(zip, "WoW.exe", "new");
            }

            var svc = new DownloadService(new System.Net.Http.HttpClient(), Serilog.Core.Logger.None);
            var ok = await svc.ExtractFreshClientAsync(zipPath, dest);

            Assert.True(ok);
            Assert.Equal("new", File.ReadAllText(held));
            Assert.Equal("new", File.ReadAllText(Path.Combine(dest, "WoW.exe")));
        }
        finally { DeleteDir(root); }
    }

    /// <summary>When a file really cannot be written, the log must name THAT file — the reports so
    /// far said "ExtractClient failed: …zip" and left the player to guess. A directory standing where
    /// the file should go is the portable way to make a write fail for good.</summary>
    [Fact]
    public async Task Extract_NamesTheFileItCouldNotWrite_AfterRetrying()
    {
        var root = NewTempDir();
        try
        {
            var dest = Path.Combine(root, "client");
            Directory.CreateDirectory(Path.Combine(dest, "chrome_elf.dll"));   // a directory, not a file

            var zipPath = Path.Combine(root, "client.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                AddEntry(zip, "chrome_elf.dll", "new");
            }

            var sink = new ListSink();
            var log = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
            var svc = new DownloadService(new System.Net.Http.HttpClient(), log);

            var ok = await svc.ExtractFreshClientAsync(zipPath, dest);

            Assert.False(ok);
            var text = sink.Text;
            Assert.Contains("chrome_elf.dll", text, StringComparison.Ordinal);
            Assert.Contains("retry 3/3", text, StringComparison.Ordinal);
            Assert.Contains("Could not write", text, StringComparison.Ordinal);
        }
        finally { DeleteDir(root); }
    }

    private sealed class ListSink : Serilog.Core.ILogEventSink
    {
        private readonly System.Text.StringBuilder _sb = new();
        public string Text => _sb.ToString();
        public void Emit(Serilog.Events.LogEvent e)
        {
            _sb.AppendLine(e.RenderMessage());
            if (e.Exception is not null) _sb.AppendLine(e.Exception.ToString());
        }
    }

    // ── W3: the crash log rides along, it does not replace the launcher log ────────────────────

    private sealed class Paths(string dir) : IAppPaths
    {
        public string ConfigDir => dir;
        public string StateDir => dir;
        public string CacheDir => dir;
        public string LogDir => dir;
        public string ShareDir => dir;
        public string ConfigFilePath => Path.Combine(dir, "launcher_config.json");
        public string NewsCacheFilePath => Path.Combine(dir, "news-cache.json");
        public string ClientInstallDir(int gameBuild) => Path.Combine(dir, $"WoW-Client-{gameBuild}");
        public string ClientDownloadZip(int gameBuild) => Path.Combine(dir, $"WoW-Client-{gameBuild}.zip");
        public void EnsureDirectories() => Directory.CreateDirectory(dir);
    }

    private sealed class MemoryConfig : IConfigService
    {
        private LauncherConfig _c = new() { SelectedRealmId = "stonetavern" };
        public LauncherConfig Load() => _c;
        public void Save(LauncherConfig config) => _c = config;
        public bool LastSaveSucceeded => true;
    }

    /// <summary>Red without the fix: the crash file is the newest <c>launcher*.log</c>, so the
    /// report carried the crash and nothing of what the launcher had logged before it.</summary>
    [Fact]
    public void ANewerCrashLog_DoesNotDisplaceTheLauncherLog()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllLines(Path.Combine(dir, "launcher20260904.log"), new[] { "download started", "extract failed: chrome_elf.dll" });
            File.WriteAllText(Path.Combine(dir, ProblemReport.CrashLogName), "[2026-09-04T20:00:00+02:00] unhandled\nSystem.NullReferenceException\n");
            File.SetLastWriteTimeUtc(Path.Combine(dir, "launcher20260904.log"), DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(Path.Combine(dir, ProblemReport.CrashLogName), DateTime.UtcNow);

            var log = new ProblemReport(new Paths(dir), new MemoryConfig(), () => "1.8.8").RecentLog();

            Assert.Contains("extract failed: chrome_elf.dll", log, StringComparison.Ordinal);
            Assert.Contains("System.NullReferenceException", log, StringComparison.Ordinal);
            Assert.True(log.IndexOf("extract failed", StringComparison.Ordinal) < log.IndexOf(ProblemReport.CrashLogName, StringComparison.Ordinal),
                "the launcher log comes first, the crash tail is appended");
        }
        finally { DeleteDir(dir); }
    }

    /// <summary>The crash file is append-only and never rotated. A crash from months ago is not
    /// what the player is reporting today and only buries the relevant lines.</summary>
    [Fact]
    public void AnOldCrashLog_IsLeftOut()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllLines(Path.Combine(dir, "launcher20260904.log"), new[] { "all fine" });
            var crash = Path.Combine(dir, ProblemReport.CrashLogName);
            File.WriteAllText(crash, "[2026-03-01T10:00:00+01:00] ancient\n");
            File.SetLastWriteTimeUtc(crash, DateTime.UtcNow.AddDays(-30));

            var log = new ProblemReport(new Paths(dir), new MemoryConfig(), () => "1.8.8").RecentLog();

            Assert.Contains("all fine", log, StringComparison.Ordinal);
            Assert.DoesNotContain("ancient", log, StringComparison.Ordinal);
        }
        finally { DeleteDir(dir); }
    }

    /// <summary>Review finding (2026-09-05, mittel): regular logs rotate after seven days, the crash
    /// file never does. When it is the only file left, an old crash is still better than an empty
    /// report — the code before W3 returned it, and a fix must not carry less.</summary>
    [Fact]
    public void AnOldCrashLog_IsStillReported_WhenItIsAllThereIs()
    {
        var dir = NewTempDir();
        try
        {
            var crash = Path.Combine(dir, ProblemReport.CrashLogName);
            File.WriteAllText(crash, "[2026-03-01T10:00:00+01:00] ancient but the only witness\n");
            File.SetLastWriteTimeUtc(crash, DateTime.UtcNow.AddDays(-30));

            var log = new ProblemReport(new Paths(dir), new MemoryConfig(), () => "1.8.8").RecentLog();

            Assert.Contains("the only witness", log, StringComparison.Ordinal);
        }
        finally { DeleteDir(dir); }
    }

    // ── W7: a launcher that never exited is not started a second time ──────────────────────────

    /// <summary>The script waited, the launcher pid is still there. Until now <c>:giveup</c> fell
    /// through into <c>:relaunch</c> and started a second launcher beside the living one — two
    /// windows, one proxy pidfile, one config. Red without the fix: no <c>goto done</c> between the
    /// two labels.</summary>
    [Fact]
    public void AStillRunningLauncher_IsNotStartedASecondTime()
    {
        var s = WindowsUpdateSwapStrategy.BuildScript(
            @"C:\Games\Stonetavern\WowLauncher.exe.new", @"C:\Games\Stonetavern\WowLauncher.exe", 4242, 30);

        var giveup = s.IndexOf(":giveup", StringComparison.Ordinal);
        var relaunch = s.IndexOf(":relaunch", StringComparison.Ordinal);
        Assert.True(giveup >= 0 && relaunch > giveup, "the script keeps both labels, giveup first");

        var exit = s.IndexOf("goto done", giveup, StringComparison.Ordinal);
        Assert.True(exit >= 0 && exit < relaunch,
            "after giving up, the script must leave — not fall through into the relaunch");
        Assert.DoesNotContain("start \"\"", s[giveup..exit], StringComparison.Ordinal);
    }

    // ── W13: the wait loop never waited ─────────────────────────────────────────────────────────

    /// <summary>Found while proving W7 on real Windows: with the launcher's pid alive, the script
    /// went to <c>:swap</c> within a second. The probe <c>findstr /B /C:"\"" &gt;nul</c> leaves
    /// cmd's quote count odd, so <c>&gt;nul</c> becomes a file argument, findstr prints
    /// <c>Cannot open &gt;nul</c> and exits 1 — which the script read as "exited". Every Windows
    /// self-update since this batch exists renamed the exe under the running launcher and then
    /// started a second one. Red without the fix: findstr is still in the script.</summary>
    [Fact]
    public void TheWait_ReadsTheProcessRowItself_NeverThroughFindstr()
    {
        var s = WindowsUpdateSwapStrategy.BuildScript(
            @"C:\Games\Stonetavern\WowLauncher.exe.new", @"C:\Games\Stonetavern\WowLauncher.exe", 4242, 30,
            new Version(1, 8, 8));

        Assert.DoesNotContain("findstr", s, StringComparison.OrdinalIgnoreCase);
        // Both liveness questions — "is the old pid gone?" and "is the new build still running?" —
        // are answered by a for /f over tasklist's CSV, comparing the field itself.
        Assert.Contains("do if \"%%~A\"==\"%PID%\" set \"ALIVE=1\"", s, StringComparison.Ordinal);
        Assert.Contains("do if /I \"%%~A\"==\"%CURNAME%\" set \"RUNNING=1\"", s, StringComparison.Ordinal);
        // And a redirect never sits inside an open quote: every line has an even number of quotes.
        foreach (var line in s.Split("\r\n"))
            Assert.True(line.Count(c => c == '"') % 2 == 0, $"odd quote count: {line}");
    }
}
