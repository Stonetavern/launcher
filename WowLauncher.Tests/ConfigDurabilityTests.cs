using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The config file is the launcher's only memory: which realms exist, where each client build is
/// installed, which version is installed there. Losing it costs the player a multi-GB re-download.
///
/// <para>The failure this file exists for: a damaged launcher_config.json was caught, silently
/// replaced by defaults, and then OVERWRITTEN by the next save (which happens on almost any
/// interaction). The damaged file was the only copy of the data, so it was gone for good. The second
/// half is the same story one step earlier: File.WriteAllText truncates before it writes, so a crash
/// or a power cut mid-save produced exactly such a damaged file.</para>
/// </summary>
public sealed class ConfigDurabilityTests
{
    private sealed class TempPaths : IAppPaths, IDisposable
    {
        public readonly string Root = Path.Combine(
            Path.GetTempPath(), "st-config-test-" + Guid.NewGuid().ToString("N"));
        public TempPaths() => Directory.CreateDirectory(Root);
        public string ConfigDir => Root;
        public string StateDir => Root;
        public string CacheDir => Root;
        public string LogDir => Root;
        public string ShareDir => Root;
        public string ConfigFilePath => Path.Combine(Root, "launcher_config.json");
        public string NewsCacheFilePath => Path.Combine(Root, "news-cache.json");
        public string ClientInstallDir(int gameBuild) => Path.Combine(Root, $"WoW-Client-{gameBuild}");
        public string ClientDownloadZip(int gameBuild) => Path.Combine(Root, $"WoW-Client-{gameBuild}.zip");
        public void EnsureDirectories() => Directory.CreateDirectory(Root);
        public void Dispose() { try { Directory.Delete(Root, true); } catch { /* best-effort */ } }
    }

    /// <summary>A marker only present in the player's own (damaged) file, so "did it survive" is a
    /// yes/no question about bytes on disk rather than about a parsed object.</summary>
    private const string Marker = "my-own-realm-that-must-not-vanish";

    [Fact]
    public void ADamagedConfigIsKept_AndTheNextSaveCannotDestroyIt()
    {
        using var paths = new TempPaths();
        // Truncated JSON: exactly what a save interrupted halfway leaves behind.
        File.WriteAllText(paths.ConfigFilePath,
            "{ \"Realms\": [ { \"Id\": \"" + Marker + "\", \"RealmlistAddress\": \"play.example.inv");

        var svc = new ConfigService(paths);
        var cfg = svc.Load();               // must degrade to defaults, not crash
        Assert.NotNull(cfg);

        svc.Save(cfg);                      // the save that used to wipe the evidence

        var survivors = Directory.EnumerateFiles(paths.Root)
            .Where(f => File.ReadAllText(f).Contains(Marker, StringComparison.Ordinal))
            .ToList();

        Assert.True(survivors.Count > 0,
            "The damaged config was replaced by defaults with no copy kept. The player's realms and " +
            "registered client installs are unrecoverable.");
    }

    [Fact]
    public void RepeatedFailuresDoNotOverwriteTheFirstBackup()
    {
        using var paths = new TempPaths();

        File.WriteAllText(paths.ConfigFilePath, "{ \"Realms\": [ \"" + Marker + "-first\"");
        new ConfigService(paths).Load();

        // A second damaged file appears later (a different failure, different content).
        File.WriteAllText(paths.ConfigFilePath, "{ \"Realms\": [ \"" + Marker + "-second\"");
        new ConfigService(paths).Load();

        var all = string.Join("\n", Directory.EnumerateFiles(paths.Root).Select(File.ReadAllText));
        Assert.Contains(Marker + "-first", all, StringComparison.Ordinal);
        Assert.Contains(Marker + "-second", all, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveLeavesNoScratchFileBehind_AndTheResultParses()
    {
        using var paths = new TempPaths();
        var svc = new ConfigService(paths);

        var cfg = svc.Load();
        cfg.ClientInstalls[5875] = "/somewhere/WoW";
        svc.Save(cfg);

        Assert.True(svc.LastSaveSucceeded);
        Assert.Empty(Directory.EnumerateFiles(paths.Root, "*.tmp"));

        var reread = new ConfigService(paths).Load();
        Assert.Equal("/somewhere/WoW", reread.ClientInstalls[5875]);
    }

    /// <summary>
    /// The point of the atomic write, stated as the property a player would notice.
    ///
    /// <para>File.WriteAllText truncates the file and then streams the document into it. Anything that
    /// looks at launcher_config.json inside that window - a second launcher instance, a backup tool, or
    /// the next start after a power cut hit exactly there - reads a document that ends mid-token. The
    /// scratch-file-plus-rename shape has no such window: the config path only ever changes in one step,
    /// so an observer sees the whole old document or the whole new one.</para>
    ///
    /// <para>The observer here stands in for the interrupted start. Asserting only that no .tmp file is
    /// left behind (what this file used to do) is satisfied by the broken variant too, which is why that
    /// assertion alone proved nothing.</para>
    /// </summary>
    [Fact]
    public async Task AConcurrentReaderNeverSeesAHalfWrittenConfig()
    {
        using var paths = new TempPaths();
        var svc = new ConfigService(paths);
        var cfg = svc.Load();

        // Big enough that writing it is not instantaneous - a document that fits in one buffer flush
        // would hide the window rather than test it.
        for (var i = 0; i < 4000; i++)
            cfg.Realms.Add(new RealmEntry
            {
                Id = "realm-" + i,
                Name = "Realm number " + i,
                RealmlistAddress = "play.example.invalid",
            });
        svc.Save(cfg);
        Assert.True(svc.LastSaveSucceeded);

        var stop = false;
        var reads = 0;
        var damaged = 0;

        var observer = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                string text;
                try
                {
                    // FileShare.Delete so that watching the file cannot make the rename fail on Windows.
                    using var fs = new FileStream(paths.ConfigFilePath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs);
                    text = sr.ReadToEnd();
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                if (text.Length == 0) continue;
                Interlocked.Increment(ref reads);
                try { using var doc = JsonDocument.Parse(text); }
                catch (JsonException) { Interlocked.Increment(ref damaged); }
            }
        });

        for (var round = 0; round < 20; round++) svc.Save(cfg);

        Volatile.Write(ref stop, true);
        await observer.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(reads > 0, "the observer never managed to look at the config while it was written");
        Assert.True(svc.LastSaveSucceeded);
        Assert.Equal(0, damaged);
    }

    /// <summary>
    /// Two damaged loads inside the same second. The backup name only resolves to seconds, so without a
    /// uniqueness counter the second rename hits an existing file, throws, and blocks every save for the
    /// rest of the session - the launcher then silently forgets everything the player does.
    /// </summary>
    [Fact]
    public void TwoDamagedLoadsInTheSameSecond_KeepBothCopies_AndSavingStillWorks()
    {
        using var paths = new TempPaths();

        File.WriteAllText(paths.ConfigFilePath, "{ \"Realms\": [ \"" + Marker + "-a\"");
        new ConfigService(paths).Load();
        File.WriteAllText(paths.ConfigFilePath, "{ \"Realms\": [ \"" + Marker + "-b\"");

        var svc = new ConfigService(paths);
        var cfg = svc.Load();

        var backups = Directory.EnumerateFiles(paths.Root, "*.bak").ToList();
        Assert.Equal(2, backups.Count);
        Assert.Contains(backups, f => File.ReadAllText(f).Contains(Marker + "-a", StringComparison.Ordinal));
        Assert.Contains(backups, f => File.ReadAllText(f).Contains(Marker + "-b", StringComparison.Ordinal));

        cfg.ClientInstalls[5875] = "/somewhere/WoW";
        svc.Save(cfg);
        Assert.True(svc.LastSaveSucceeded,
            "A second damaged config must not cost the player every setting made afterwards.");
        Assert.Equal("/somewhere/WoW", new ConfigService(paths).Load().ClientInstalls[5875]);
    }

    [Fact]
    public void AnUnwritableConfigDirectoryIsReported_NotSilentlySwallowed()
    {
        using var paths = new TempPaths();
        var svc = new ConfigService(paths);
        var cfg = svc.Load();

        // Make the config path un-writable in the bluntest portable way: turn it into a directory.
        File.Delete(paths.ConfigFilePath);
        Directory.CreateDirectory(paths.ConfigFilePath);

        svc.Save(cfg);

        Assert.False(svc.LastSaveSucceeded,
            "A save that never reached disk must be visible to the UI. Reporting settings as applied " +
            "while nothing persists is the failure mode this flag exists for.");
    }
}
