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
/// Settings > Troubleshooting > "Save support package" (owner 2026-09-28): one zip with every log,
/// the realm proxy logs above all, and nothing that identifies the player or opens their account.
/// </summary>
public sealed class SupportPackageTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "stsp-" + Guid.NewGuid().ToString("N")[..8]);
    public SupportPackageTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (Exception) { } }

    private sealed class Paths(string root) : IAppPaths
    {
        public string ConfigDir => Path.Combine(root, "config");
        public string StateDir => Path.Combine(root, "state");
        public string CacheDir => Path.Combine(root, "cache");
        public string LogDir => StateDir;
        public string ShareDir => Path.Combine(root, "share");
        public string ConfigFilePath => Path.Combine(ConfigDir, "launcher_config.json");
        public string NewsCacheFilePath => Path.Combine(CacheDir, "news.json");
        public string ClientInstallDir(int b) => Path.Combine(ShareDir, $"WoW-Client-{b}");
        public string ClientDownloadZip(int b) => Path.Combine(CacheDir, $"c{b}.zip");
        public void EnsureDirectories() { }
    }

    private sealed class Config : IConfigService
    {
        public LauncherConfig Cfg = new();
        public LauncherConfig Load() => Cfg;
        public void Save(LauncherConfig config) => Cfg = config;
        public bool LastSaveSucceeded => true;
    }

    private (SupportPackage Package, string Home) Build()
    {
        var home = Path.Combine(_tmp, "home", "alice");
        var paths = new Paths(Path.Combine(home, ".stonetavern"));
        Directory.CreateDirectory(paths.LogDir);
        File.WriteAllText(Path.Combine(paths.LogDir, "launcher20260928.log"),
            $"[INF] signed in as Frostbolt99\n[INF] client at {home}/Games/Stonetavern/Modern-1.14.2\nAuthorization: Bearer abc.def.ghi\n"
            + $"[INF] wine sees Z:{home.Replace('/', '\\')}\\Games\n");
        File.WriteAllText(Path.Combine(paths.LogDir, "proxy-jims.log"), "JimsProxy: realm list 2 entries\n");
        File.WriteAllText(Path.Combine(paths.LogDir, "update-attempts.txt"), "1.9.3\n");

        // A 1.14.2 package in the library: the client two levels below the package, Hermes beside it.
        var package = Path.Combine(home, "Games", "Stonetavern", "Modern-1.14.2");
        var client = Directory.CreateDirectory(Path.Combine(package, "World of Warcraft", "_classic_era_")).FullName;
        Directory.CreateDirectory(Path.Combine(client, "Logs"));
        File.WriteAllText(Path.Combine(client, "Logs", "Client.log"), "client says hi\n");
        Directory.CreateDirectory(Path.Combine(client, "WTF"));
        File.WriteAllText(Path.Combine(client, "WTF", "Config.wtf"),
            "SET portal \"play.stonetavern.app\"\nSET accountName \"FROSTBOLT99\"\nSET accountList \"Secondalt|\"\nSET gxApi \"D3D11\"\n");
        Directory.CreateDirectory(Path.Combine(package, "Hermes", "Logs"));
        File.WriteAllText(Path.Combine(package, "Hermes", "Logs", "hermes-2026-09-28.log"), "proxy session\n");

        var cfg = new Config();
        cfg.Cfg.LastAccountName = "Frostbolt99";
        cfg.Cfg.ClientInstalls[42597] = client;
        return (new SupportPackage(paths, cfg, null, Serilog.Log.Logger, home, () => new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero)), home);
    }

    private static string Read(ZipArchive zip, string entry)
    {
        using var r = new StreamReader(zip.GetEntry(entry)!.Open());
        return r.ReadToEnd();
    }

    [Fact]
    public async Task ThePackage_HoldsTheLauncherTheProxyAndTheGameLogs()
    {
        var (package, _) = Build();

        var path = await package.CreateAsync(Path.Combine(_tmp, "out"));

        using var zip = ZipFile.OpenRead(path);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("launcher/launcher20260928.log", names);
        Assert.Contains("launcher/proxy-jims.log", names);
        Assert.Contains("launcher/update-attempts.txt", names);
        Assert.Contains("client-42597/Logs/Client.log", names);
        Assert.Contains("client-42597/WTF/Config.wtf", names);
        Assert.Contains("client-42597/proxy/Logs/hermes-2026-09-28.log", names);
        Assert.Contains("launcher/launcher_config.json", names);
        Assert.Contains("system.txt", names);
    }

    [Fact]
    public async Task ThePackage_CarriesNoAccountNameNoHomeFolderAndNoToken()
    {
        var (package, home) = Build();

        var path = await package.CreateAsync(Path.Combine(_tmp, "out"));

        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            var text = Read(zip, entry.FullName);
            Assert.DoesNotContain("Frostbolt99", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(home, text);
            Assert.DoesNotContain("alice", text);                // not even in the Wine form Z:\\...
            Assert.DoesNotContain("Secondalt", text);            // the other accounts Config.wtf remembers
            Assert.DoesNotContain("abc.def.ghi", text);
        }
        var wtf = Read(zip, "client-42597/WTF/Config.wtf");
        Assert.Contains("gxApi", wtf);                       // what support needs stays
        Assert.Contains("play.stonetavern.app", wtf);
        Assert.Contains("~/Games/Stonetavern/Modern-1.14.2", Read(zip, "launcher/launcher20260928.log"));
    }

    [Fact]
    public void AWindowsHome_IsRemovedWithEitherSlash()
    {
        var paths = new Paths(_tmp);
        var package = new SupportPackage(paths, new Config(), null, Serilog.Log.Logger, @"C:\Users\alice");

        var text = package.Clean(@"install C:\Users\alice\Games\Stonetavern and C:/Users/alice/AppData", null);

        Assert.DoesNotContain("alice", text);
        Assert.Contains(@"~\Games\Stonetavern", text);
    }

    [Fact]
    public async Task ALongLog_ContributesItsEnd_AndSaysSo()
    {
        var (package, _) = Build();
        var log = Directory.GetFiles(Path.Combine(_tmp, "home", "alice", ".stonetavern", "state"), "launcher*.log").Single();
        File.WriteAllText(log, new string('a', (int)SupportPackage.MaxFileBytes) + "\nTHE END\n");

        var path = await package.CreateAsync(Path.Combine(_tmp, "out"));

        using var zip = ZipFile.OpenRead(path);
        var text = Read(zip, "launcher/" + Path.GetFileName(log));
        Assert.StartsWith("[only the last", text);
        Assert.EndsWith("THE END\n", text);
    }
}
