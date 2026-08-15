using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Addon management. Two things can go badly wrong here and both are silent, so both have their own
/// test: the launcher unpacking something it did not verify (a package off the network writing into a
/// client folder), and the launcher deleting a folder it did not create (a player's own addon, gone
/// because the catalog happened to know the name).
/// </summary>
public sealed class AddonServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "addons-" + Guid.NewGuid().ToString("N"));

    public AddonServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static Serilog.ILogger Logger() => new Serilog.LoggerConfiguration().CreateLogger();

    private string ClientDir()
    {
        var dir = Path.Combine(_root, "client");
        Directory.CreateDirectory(Path.Combine(dir, "Interface", "AddOns"));
        return dir;
    }

    private static string AddonsOf(string clientDir) => Path.Combine(clientDir, "Interface", "AddOns");

    /// <summary>Build a zip whose entries are exactly <paramref name="entries"/> (path → contents).</summary>
    private string MakeZip(string name, params (string Path, string Content)[] entries)
    {
        var zipPath = Path.Combine(_root, name);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var (path, content) in entries)
            {
                var entry = zip.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }
        return zipPath;
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static AddonEntry Entry(string id, string version, string zipPath, params string[] folders) =>
        new()
        {
            Id = id,
            Name = id,
            Version = version,
            Url = "https://downloads.stonetavern.app/addons/" + id + ".zip",
            Sha256 = Sha256Of(zipPath),
            Folders = [.. folders],
            Builds = [5875],
        };

    /// <summary>Stands in for the real downloader: copies a local file to the destination (the network
    /// half is not what these tests are about) and computes a REAL sha256 for the verify, so the
    /// hash-mismatch test proves the actual check rather than a stubbed answer.</summary>
    private sealed class FakeDownloads : IDownloadService
    {
        private readonly string _source;
        public int Downloads;
        public FakeDownloads(string source) => _source = source;

        public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Downloads++;
            File.Copy(_source, destPath, overwrite: true);
            return Task.FromResult(DownloadResult.Success);
        }

        public Task<bool> ExtractZipAsync(string zipPath, string destDir,
            IProgress<string>? progress = null, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> ExtractClientAsync(string zipPath, string destDir,
            IProgress<string>? progress = null, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> VerifyHashAsync(string path, string expectedSha256, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(expectedSha256)) return Task.FromResult(false);
            return Task.FromResult(string.Equals(Sha256Of(path), expectedSha256.Trim(),
                StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// A service whose catalogue really is fetched over HTTP (a stub handler serving the given entries)
    /// — because an install now confirms, against a freshly fetched catalogue, that the addon is still
    /// offered. A test that stubbed that check away would be testing a launcher nobody ships.
    /// </summary>
    private AddonService NewService(string zipToServe, params AddonEntry[] offered)
    {
        var catalog = new AddonCatalog { Addons = [.. offered] };
        var json = System.Text.Json.JsonSerializer.Serialize(catalog);
        var http = new HttpClient(new StubCatalog(json)) { BaseAddress = null };
        var config = new MemoryConfig { Current = new LauncherConfig { SiteBaseUrl = "https://test.invalid" } };
        return new AddonService(http, config, new FakeDownloads(zipToServe), new TempPaths(_root), Logger());
    }

    private sealed class StubCatalog : HttpMessageHandler
    {
        private readonly string _json;
        public StubCatalog(string json) => _json = json;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>A config with no distribution host: these tests drive the installer, not the catalog
    /// fetch, and a service that quietly tried to reach the network would make them flaky.</summary>
    private sealed class MemoryConfig : IConfigService
    {
        public LauncherConfig Current = new() { PatchServerBaseUrl = "" };
        public LauncherConfig Load() => Current;
        public void Save(LauncherConfig config) => Current = config;
        public bool LastSaveSucceeded => true;
    }

    private sealed class TempPaths : WowLauncher.Services.Platform.IAppPaths
    {
        private readonly string _root;
        public TempPaths(string root) { _root = Path.Combine(root, "paths"); Directory.CreateDirectory(_root); }
        public string ConfigDir => _root;
        public string StateDir => _root;
        public string CacheDir => _root;
        public string LogDir => _root;
        public string ShareDir => _root;
        public string ConfigFilePath => Path.Combine(_root, "launcher_config.json");
        public string NewsCacheFilePath => Path.Combine(_root, "news-cache.json");
        public string ClientInstallDir(int gameBuild) => Path.Combine(_root, $"WoW-Client-{gameBuild}");
        public string ClientDownloadZip(int gameBuild) => Path.Combine(_root, $"WoW-Client-{gameBuild}.zip");
        public void EnsureDirectories() => Directory.CreateDirectory(_root);
    }

    // ── The catalog ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ACatalogEntryMissingItsHashOrNamingABadFolder_IsNotOffered()
    {
        var catalog = new AddonCatalog
        {
            Addons =
            [
                new AddonEntry { Id = "ok", Url = "https://x/y.zip", Sha256 = "abc", Folders = ["Ok"], Builds = [5875] },
                new AddonEntry { Id = "nohash", Url = "https://x/y.zip", Folders = ["NoHash"], Builds = [5875] },
                // No folders is legitimate now (a community upload names them itself, from the package).
                new AddonEntry { Id = "nofolders", Url = "https://x/y.zip", Sha256 = "abc", Builds = [5875] },
                // A folder name that would escape the addons directory is not a folder name at all.
                new AddonEntry { Id = "escape", Url = "https://x/y.zip", Sha256 = "abc", Folders = ["../../WTF"], Builds = [5875] },
            ],
        };

        var offered = catalog.ForBuild(5875);

        Assert.Equal(["ok", "nofolders"], offered.Select(a => a.Id));
    }

    [Fact]
    public void ForBuild_YieldsOnlyThePackagesDeclaredForThatBuild()
    {
        // 🔴 Zwei Aenderungen gegenueber dem urspruenglichen Verhalten, beide 2026-08-12 auf
        // Owner-Entscheid:
        //   1. Ein Eintrag OHNE Build-Angabe galt als "passt zu allen" und erschien in beiden Listen.
        //      Das war fail-open: ein Katalog-Eintrag ohne Angabe ist ein Datenfehler, und ihn jedem
        //      Client anzubieten macht daraus ein Spielerproblem. Jetzt erscheint er in keiner.
        //   2. ForBuild ist nicht mehr die Schranke der Addon-Liste — die zeigt bewusst alles, siehe
        //      AddonBuildGateTests. ForBuild beantwortet nur noch "was ist fuer diesen Build erklaert".
        var catalog = new AddonCatalog
        {
            Addons =
            [
                new AddonEntry { Id = "vanilla", Url = "u", Sha256 = "h", Folders = ["V"], Builds = [5875] },
                new AddonEntry { Id = "modern", Url = "u", Sha256 = "h", Folders = ["M"], Builds = [42597] },
                new AddonEntry { Id = "nobuild", Url = "u", Sha256 = "h", Folders = ["B"] },
            ],
        };

        Assert.Equal(["vanilla"], catalog.ForBuild(5875).Select(a => a.Id));
        Assert.Equal(["modern"], catalog.ForBuild(42597).Select(a => a.Id));
    }

    // ── Install ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnInstall_WritesTheFolder_AndRecordsWhatItOwns()
    {
        var client = ClientDir();
        var zip = MakeZip("questie.zip", ("Questie/Questie.toc", "## Title: Questie"), ("Questie/Init.lua", "-- x"));
        var entry = Entry("questie", "7.5.2", zip, "Questie");
        var service = NewService(zip, entry);

        var result = await service.InstallAsync(client, entry);

        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(AddonsOf(client), "Questie", "Questie.toc")));

        var status = AddonService.Join([entry], AddonsOf(client));
        Assert.True(status[0].IsInstalled);
        Assert.True(status[0].IsManaged);
        Assert.Equal("7.5.2", status[0].InstalledVersion);
    }

    [Fact]
    public async Task APackageWhoseHashDoesNotMatch_IsNeverUnpacked()
    {
        // The hard invariant, at the level it matters: not "the error is reported" but "nothing was
        // written". A verify that runs after extraction would pass a test that only checked Ok == false.
        var client = ClientDir();
        var zip = MakeZip("bad.zip", ("Questie/Questie.toc", "## Title: Questie"));
        var entry = Entry("questie", "1.0", zip, "Questie");
        entry.Sha256 = new string('0', 64);   // what the catalog claims, and it is wrong
        var service = NewService(zip, entry);

        var result = await service.InstallAsync(client, entry);

        Assert.False(result.Ok);
        Assert.False(Directory.Exists(Path.Combine(AddonsOf(client), "Questie")));
    }

    [Fact]
    public async Task APackageThatWritesOutsideItsOwnFolder_IsRefusedWhole()
    {
        // Two escapes in one package: a classic zip slip (../../) and an entry that stays inside the
        // addons directory but belongs to something else. Either one discards the package; nothing of
        // it may remain, or the player is left with half an addon and a modified client config.
        var client = ClientDir();
        var zip = MakeZip("evil.zip",
            ("Questie/Questie.toc", "## Title: Questie"),
            ("../../WTF/Config.wtf", "SET portal \"evil\""),
            ("OtherAddon/Other.lua", "-- not ours"));
        var entry = Entry("questie", "1.0", zip, "Questie");
        var service = NewService(zip, entry);

        var result = await service.InstallAsync(client, entry);

        Assert.False(result.Ok);
        Assert.False(Directory.Exists(Path.Combine(AddonsOf(client), "Questie")));
        Assert.False(Directory.Exists(Path.Combine(AddonsOf(client), "OtherAddon")));
        Assert.False(File.Exists(Path.Combine(client, "WTF", "Config.wtf")));
    }

    [Fact]
    public async Task AnAddonTheLauncherDidNotInstall_IsNeverOverwritten()
    {
        // The player put Questie there by hand, possibly edited. Installing over it would destroy that
        // silently, so the launcher refuses and says the folder is theirs.
        var client = ClientDir();
        var byHand = Path.Combine(AddonsOf(client), "Questie");
        Directory.CreateDirectory(byHand);
        File.WriteAllText(Path.Combine(byHand, "Questie.toc"), "## Title: my own copy");

        var zip = MakeZip("questie.zip", ("Questie/Questie.toc", "## Title: catalog copy"));
        var entry = Entry("questie", "7.5.2", zip, "Questie");
        var service = NewService(zip, entry);

        var result = await service.InstallAsync(client, entry);

        Assert.False(result.Ok);
        Assert.Equal("## Title: my own copy", File.ReadAllText(Path.Combine(byHand, "Questie.toc")));
    }

    [Fact]
    public async Task AnUpdate_RemovesTheOldFilesFirst()
    {
        // A file upstream deleted must not survive into the new version and load beside it.
        var client = ClientDir();
        var oldZip = MakeZip("old.zip",
            ("Questie/Questie.toc", "## Version: 1.0"), ("Questie/Legacy.lua", "-- dropped upstream"));
        var oldEntry = Entry("questie", "1.0", oldZip, "Questie");
        Assert.True((await NewService(oldZip, oldEntry).InstallAsync(client, oldEntry)).Ok);
        Assert.True(File.Exists(Path.Combine(AddonsOf(client), "Questie", "Legacy.lua")));

        var newZip = MakeZip("new.zip", ("Questie/Questie.toc", "## Version: 2.0"));
        var newEntry = Entry("questie", "2.0", newZip, "Questie");

        var result = await NewService(newZip, newEntry).InstallAsync(client, newEntry);

        Assert.True(result.Ok);
        Assert.False(File.Exists(Path.Combine(AddonsOf(client), "Questie", "Legacy.lua")));
        Assert.Equal("## Version: 2.0", File.ReadAllText(Path.Combine(AddonsOf(client), "Questie", "Questie.toc")));
        Assert.Equal("2.0", AddonService.Join([newEntry], AddonsOf(client))[0].InstalledVersion);
    }

    // ── Status ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFolderDeletedByHand_CountsAsUninstalled_WhateverTheStateFileSays()
    {
        // Measure the disk, not the bookkeeping: the player deleted the folder in a file manager.
        var client = ClientDir();
        var zip = MakeZip("questie.zip", ("Questie/Questie.toc", "## Title: Questie"));
        var entry = Entry("questie", "7.5.2", zip, "Questie");
        Assert.True((await NewService(zip, entry).InstallAsync(client, entry)).Ok);

        Directory.Delete(Path.Combine(AddonsOf(client), "Questie"), recursive: true);

        var status = AddonService.Join([entry], AddonsOf(client))[0];
        Assert.False(status.IsInstalled);
        Assert.False(status.IsManaged);
    }

    [Fact]
    public void AHandInstalledAddon_IsShownButNotClaimed()
    {
        var client = ClientDir();
        Directory.CreateDirectory(Path.Combine(AddonsOf(client), "Questie"));
        var entry = new AddonEntry
        {
            Id = "questie",
            Name = "Questie",
            Version = "7.5.2",
            Url = "u",
            Sha256 = "h",
            Folders = ["Questie"],
            Builds = [5875],
        };

        var status = AddonService.Join([entry], AddonsOf(client))[0];

        Assert.True(status.IsInstalled);
        Assert.False(status.IsManaged);
        Assert.True(status.IsForeign);
        Assert.False(status.HasUpdate);   // we do not know its version, so we never claim one is due
    }

    [Fact]
    public async Task ADifferentCatalogVersion_ShowsAsAnUpdate()
    {
        var client = ClientDir();
        var zip = MakeZip("questie.zip", ("Questie/Questie.toc", "## Title: Questie"));
        var installed = Entry("questie", "7.5.2", zip, "Questie");
        Assert.True((await NewService(zip, installed).InstallAsync(client, installed)).Ok);

        var newer = Entry("questie", "7.6.0", zip, "Questie");
        var status = AddonService.Join([newer], AddonsOf(client))[0];

        Assert.True(status.IsInstalled);
        Assert.True(status.HasUpdate);
    }

    // ── Remove ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveTakesOnlyWhatTheLauncherInstalled()
    {
        var client = ClientDir();
        var zip = MakeZip("questie.zip", ("Questie/Questie.toc", "## Title: Questie"));
        var entry = Entry("questie", "7.5.2", zip, "Questie");
        var service = NewService(zip, entry);
        Assert.True((await service.InstallAsync(client, entry)).Ok);

        // A neighbour the player installed themselves, with the same shape.
        var mine = Path.Combine(AddonsOf(client), "MyOwnAddon");
        Directory.CreateDirectory(mine);
        File.WriteAllText(Path.Combine(mine, "MyOwnAddon.toc"), "## Title: mine");

        var result = await service.RemoveAsync(client, "questie");

        Assert.True(result.Ok);
        Assert.False(Directory.Exists(Path.Combine(AddonsOf(client), "Questie")));
        Assert.True(File.Exists(Path.Combine(mine, "MyOwnAddon.toc")));   // untouched
    }

    [Fact]
    public async Task RemoveRefusesAnAddonTheLauncherDidNotInstall()
    {
        var client = ClientDir();
        var byHand = Path.Combine(AddonsOf(client), "Questie");
        Directory.CreateDirectory(byHand);
        File.WriteAllText(Path.Combine(byHand, "Questie.toc"), "## Title: my own copy");
        var zip = MakeZip("questie.zip", ("Questie/Questie.toc", "x"));

        var result = await NewService(zip).RemoveAsync(client, "questie");

        Assert.False(result.Ok);
        Assert.True(File.Exists(Path.Combine(byHand, "Questie.toc")));
    }

    [Fact]
    public async Task AHandEditedStateFile_CannotTalkTheLauncherIntoDeletingElsewhere()
    {
        // The state file is on disk and therefore editable. A folder name in it that is not a plain
        // folder name is refused rather than resolved — otherwise it is a delete-anything primitive.
        var client = ClientDir();
        var addons = AddonsOf(client);
        var victim = Path.Combine(client, "WTF");
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "Config.wtf"), "SET portal \"x\"");

        File.WriteAllText(Path.Combine(addons, AddonService.StateFileName),
            """{"addons":[{"id":"evil","name":"evil","version":"1","folders":["../WTF"]}]}""");

        var zip = MakeZip("x.zip", ("A/A.toc", "x"));
        var result = await NewService(zip).RemoveAsync(client, "evil");

        Assert.True(result.Ok);   // the record is dropped…
        Assert.True(File.Exists(Path.Combine(victim, "Config.wtf")));   // …but nothing outside was deleted
    }

    // ── Community uploads: the package names its own folders ──────────────────────────────────

    [Fact]
    public async Task AnEntryWithoutFolders_LearnsThemFromTheVerifiedPackage()
    {
        // The website's pack catalogue records a name, a size and a hash — not a folder layout. So the
        // launcher reads the layout off the package it just verified, and then owns exactly that.
        var client = ClientDir();
        var zip = MakeZip("pack.zip",
            ("MyUI/MyUI.toc", "## Title: MyUI"), ("MyUI/core.lua", "-- x"), ("MyUI_Options/Opt.toc", "## Title: Opt"));
        var entry = Entry("myui", "abc123", zip);   // no folders declared
        Assert.Empty(entry.Folders);

        var result = await NewService(zip, entry).InstallAsync(client, entry);

        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(AddonsOf(client), "MyUI", "MyUI.toc")));
        Assert.True(File.Exists(Path.Combine(AddonsOf(client), "MyUI_Options", "Opt.toc")));

        // …and it can be removed again, which is only possible because the folders were recorded.
        Assert.True((await NewService(zip).RemoveAsync(client, "myui")).Ok);
        Assert.False(Directory.Exists(Path.Combine(AddonsOf(client), "MyUI")));
        Assert.False(Directory.Exists(Path.Combine(AddonsOf(client), "MyUI_Options")));
    }

    [Fact]
    public async Task APackageWithLooseFilesAtItsRoot_IsRefused()
    {
        // Without a declared layout, a root-level file would land straight in Interface/AddOns and
        // belong to nobody: unremovable, and indistinguishable from a file the player put there.
        var client = ClientDir();
        var zip = MakeZip("loose.zip", ("readme.txt", "hello"), ("MyUI/MyUI.toc", "## Title: MyUI"));
        var entry = Entry("loose", "abc123", zip);

        var result = await NewService(zip, entry).InstallAsync(client, entry);

        Assert.False(result.Ok);
        Assert.False(Directory.Exists(Path.Combine(AddonsOf(client), "MyUI")));
        Assert.False(File.Exists(Path.Combine(AddonsOf(client), "readme.txt")));
    }

    [Fact]
    public async Task ADerivedLayoutStillWillNotOverwriteAPlayersOwnFolder()
    {
        var client = ClientDir();
        var mine = Path.Combine(AddonsOf(client), "MyUI");
        Directory.CreateDirectory(mine);
        File.WriteAllText(Path.Combine(mine, "MyUI.toc"), "## Title: mine");

        var zip = MakeZip("pack2.zip", ("MyUI/MyUI.toc", "## Title: from the catalogue"));
        var entry = Entry("myui", "abc123", zip);

        var result = await NewService(zip, entry).InstallAsync(client, entry);

        Assert.False(result.Ok);
        Assert.Equal("## Title: mine", File.ReadAllText(Path.Combine(mine, "MyUI.toc")));
    }

    // ── The four Codex findings (2026-07-27), each with the sequence it described ──────────────

    [Fact]
    public async Task AnUpdateThatRenamesIntoAnotherAddonsFolder_IsRefused()
    {
        // Codex finding: A owns FolderA, B owns FolderB. A ships an update that now contains FolderB.
        // The old code only checked for clashes on FIRST install, so the "update" would have deleted
        // B's files and taken its folder — one addon eating another, reported as success.
        var client = ClientDir();
        var zipA = MakeZip("a.zip", ("AddonA/A.toc", "## A"));
        var entryA = Entry("a", "1", zipA, "AddonA");
        Assert.True((await NewService(zipA, entryA).InstallAsync(client, entryA)).Ok);

        var zipB = MakeZip("b.zip", ("AddonB/B.toc", "## B"));
        var entryB = Entry("b", "1", zipB, "AddonB");
        Assert.True((await NewService(zipB, entryB).InstallAsync(client, entryB)).Ok);

        var zipA2 = MakeZip("a2.zip", ("AddonB/B.toc", "## A pretending to be B"));
        var entryA2 = Entry("a", "2", zipA2, "AddonB");

        var result = await NewService(zipA2, entryA2).InstallAsync(client, entryA2);

        Assert.False(result.Ok);
        Assert.Equal("## B", File.ReadAllText(Path.Combine(AddonsOf(client), "AddonB", "B.toc")));
        Assert.True(File.Exists(Path.Combine(AddonsOf(client), "AddonA", "A.toc")));
    }

    [Fact]
    public async Task AFolderThatIsALink_IsNeverWrittenThrough()
    {
        // Codex finding: every path check in the extractor is lexical, so a symlink inside the addons
        // directory is exactly the way out of it. The link is refused instead of followed.
        var client = ClientDir();
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "not the launcher's business");

        var linkPath = Path.Combine(AddonsOf(client), "MyUI");
        Directory.CreateSymbolicLink(linkPath, outside);

        var zip = MakeZip("link.zip", ("MyUI/MyUI.toc", "## Title: MyUI"));
        var entry = Entry("myui", "1", zip, "MyUI");

        var result = await NewService(zip, entry).InstallAsync(client, entry);

        Assert.False(result.Ok);
        Assert.False(File.Exists(Path.Combine(outside, "MyUI.toc")));
        Assert.Equal("not the launcher's business", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public async Task AFailedUpdate_LeavesThePreviousVersionInPlace()
    {
        // Codex finding: delete-then-extract means a failure halfway leaves the player with NO addon.
        // Here the new package is refused during extraction (it writes outside its folder), which is
        // the same moment a full disk or a killed process would hit: the old version must still be there.
        var client = ClientDir();
        var good = MakeZip("v1.zip", ("MyUI/MyUI.toc", "## Version: 1"));
        var v1 = Entry("myui", "1", good, "MyUI");
        Assert.True((await NewService(good, v1).InstallAsync(client, v1)).Ok);

        var bad = MakeZip("v2.zip", ("MyUI/MyUI.toc", "## Version: 2"), ("../../WTF/Config.wtf", "evil"));
        var v2 = Entry("myui", "2", bad, "MyUI");

        var result = await NewService(bad, v2).InstallAsync(client, v2);

        Assert.False(result.Ok);
        Assert.Equal("## Version: 1", File.ReadAllText(Path.Combine(AddonsOf(client), "MyUI", "MyUI.toc")));
        Assert.Equal("1", AddonService.Join([v1], AddonsOf(client))[0].InstalledVersion);
        // …and no working directories left behind.
        Assert.DoesNotContain(Directory.GetDirectories(AddonsOf(client))
            .Select(Path.GetFileName)
, n => n!.StartsWith(AddonService.StagingPrefix, StringComparison.Ordinal)
                        || n!.StartsWith(AddonService.BackupPrefix, StringComparison.Ordinal));
    }

    // ── The backup is the player's only copy ──────────────────────────────────────────────────────
    //
    // An update moves the installed version into a hidden backup folder, puts the new one in place,
    // and throws the backup away. Every failure path in between used to end in the same cleanup, which
    // deleted that backup unconditionally — including the paths where the old version had NOT been put
    // back. The player did not lose an update, they lost the addon they already had, silently, on the
    // cleanup path.

    [Fact]
    public async Task WhenTheStateFileCannotBeWritten_ThePlayerKeepsTheVersionTheyAlreadyHad()
    {
        // The new version installs fine and then ownership cannot be recorded, so the launcher takes it
        // back out again — correctly, an addon it cannot remove later is worse than none. What must not
        // happen is that the OLD version goes with it.
        var client = ClientDir();
        var addons = AddonsOf(client);
        var v1zip = MakeZip("keep-v1.zip", ("MyUI/MyUI.toc", "## Version: 1"));
        var v1 = Entry("myui", "1", v1zip, "MyUI");
        Assert.True((await NewService(v1zip, v1).InstallAsync(client, v1)).Ok);

        // A directory where the state writer wants its temp file: every write fails from here on, on
        // Windows and Linux alike, without touching permissions.
        Directory.CreateDirectory(Path.Combine(addons, AddonService.StateFileName + ".tmp"));

        var v2zip = MakeZip("keep-v2.zip", ("MyUI/MyUI.toc", "## Version: 2"));
        var v2 = Entry("myui", "2", v2zip, "MyUI");
        var result = await NewService(v2zip, v2).InstallAsync(client, v2);

        Assert.False(result.Ok);
        Assert.True(Directory.Exists(Path.Combine(addons, "MyUI")), "the addon the player had is gone");
        Assert.Equal("## Version: 1", File.ReadAllText(Path.Combine(addons, "MyUI", "MyUI.toc")));
    }

    [Fact]
    public void WhenAFolderCannotBeMovedBack_TheOnlyRemainingCopyIsKept()
    {
        // The rollback itself fails here: the live folder cannot be removed, so the old one cannot take
        // its place. That is the one case where the copy inside the backup is the only one left in the
        // world, and the caller is told to keep it rather than clean it up.
        var addons = AddonsOf(ClientDir());
        var backup = Path.Combine(addons, AddonService.BackupPrefix + "test");
        Directory.CreateDirectory(Path.Combine(backup, "Blocked"));
        File.WriteAllText(Path.Combine(backup, "Blocked", "old.lua"), "the only copy");

        // Something in its place that cannot be removed to make room.
        var live = Path.Combine(addons, "Blocked");
        Directory.CreateDirectory(live);
        File.WriteAllText(Path.Combine(live, "new.lua"), "the half-installed one");
        var blocker = Block(live);

        try
        {
            var service = NewService(MakeZip("unused.zip", ("X/x.toc", "x")));

            Assert.False(service.RestoreFromBackup(addons, backup));
            Assert.True(File.Exists(Path.Combine(backup, "Blocked", "old.lua")),
                "the only remaining copy was reported as restored");
        }
        finally { blocker.Dispose(); }
    }

    /// <summary>Make <paramref name="dir"/> undeletable for as long as the returned handle lives —
    /// a held file on Windows, a mode with no write bit on Unix. Both are what a real read-only client
    /// folder or a file the game still has open does to a rollback.</summary>
    private static IDisposable Block(string dir)
    {
        if (!OperatingSystem.IsWindows())
        {
            var before = File.GetUnixFileMode(dir);
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            return new Restore(() => File.SetUnixFileMode(dir, before));
        }

        return new FileStream(Path.Combine(dir, "held"), FileMode.Create, FileAccess.Write, FileShare.None);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    [Fact]
    public async Task WhenTheRollbackSucceeds_NothingIsLeftLyingAround()
    {
        // The counterpart: a rollback that worked must still clean up after itself, or the addons folder
        // fills with hidden backups nobody ever looks at.
        var client = ClientDir();
        var addons = AddonsOf(client);
        var v1zip = MakeZip("clean-v1.zip", ("MyUI/MyUI.toc", "## Version: 1"));
        var v1 = Entry("myui", "1", v1zip, "MyUI");
        Assert.True((await NewService(v1zip, v1).InstallAsync(client, v1)).Ok);

        // The catalogue declares two folders, the package ships one — the swap fails halfway through.
        var v2zip = MakeZip("clean-v2.zip", ("MyUI/MyUI.toc", "## Version: 2"));
        var v2 = Entry("myui", "2", v2zip, "MyUI", "MyUI_Extra");

        var result = await NewService(v2zip, v2).InstallAsync(client, v2);

        Assert.False(result.Ok);
        Assert.Equal("## Version: 1", File.ReadAllText(Path.Combine(addons, "MyUI", "MyUI.toc")));
        Assert.DoesNotContain(Directory.GetDirectories(addons).Select(Path.GetFileName),
            n => n!.StartsWith(AddonService.BackupPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAddonThatIsNoLongerOffered_CannotBeInstalledFromACachedList()
    {
        // Codex finding: a pack a GM removed is gone from the live list but still in yesterday's cached
        // copy. Installing from that copy would defeat the moderation the single-source design exists
        // for, so the install confirms against a freshly fetched catalogue.
        var client = ClientDir();
        var zip = MakeZip("gone.zip", ("MyUI/MyUI.toc", "## Title: MyUI"));
        var entry = Entry("myui", "1", zip, "MyUI");

        // The catalogue no longer carries it — the row a player still sees from an earlier fetch.
        var result = await NewService(zip).InstallAsync(client, entry);

        Assert.False(result.Ok);
        Assert.False(Directory.Exists(Path.Combine(AddonsOf(client), "MyUI")));
    }

    [Fact]
    public async Task APackageThatUnpacksFarTooLarge_IsRefused()
    {
        // A matching hash proves the file is the one the catalogue names. It says nothing about what
        // unpacking costs, so the cost is measured rather than trusted.
        var client = ClientDir();
        var zipPath = Path.Combine(_root, "bomb.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("MyUI/big.lua");
            using var w = new StreamWriter(e.Open());
            // Highly compressible, so the archive stays small while the declared size is over the cap.
            var chunk = new string('a', 1024 * 1024);
            for (var i = 0; i < 520; i++) w.Write(chunk);
        }
        var entry = Entry("myui", "1", zipPath, "MyUI");

        var result = await NewService(zipPath, entry).InstallAsync(client, entry);

        Assert.False(result.Ok);
        Assert.False(Directory.Exists(Path.Combine(AddonsOf(client), "MyUI")));
    }

    // ── Where the catalogue comes from ────────────────────────────────────────────────────────

    [Fact]
    public void TheWebsiteIsAskedFirst_TheDownloadHostIsTheFallback()
    {
        // One list, two ways to reach it: the site owns uploads and moderation, so a pack a GM removes
        // must vanish from the launcher too. The static file on the download host only stands in when
        // the site cannot be reached.
        var config = new MemoryConfig
        {
            Current = new LauncherConfig
            {
                SiteBaseUrl = "https://stonetavern.app",
                PatchServerBaseUrl = "https://downloads.stonetavern.app",
            },
        };
        var service = new AddonService(new HttpClient(), config, new FakeDownloads(""), new TempPaths(_root), Logger());

        Assert.Equal(
            ["https://stonetavern.app/api/launcher/addons", "https://downloads.stonetavern.app/addons.json"],
            service.CatalogUrls());
    }

    // ── Versions-Sperre beim Installieren ────────────────────────────────────────────────────

    /// <summary>
    /// Seit die Liste absichtlich auch die Pakete des anderen Spielstands zeigt, ist der frueher
    /// alleinige Listenfilter keine Schranke mehr. Diese Sperre ist die einzige, die dann noch
    /// zaehlt — und sie muss zuschlagen, BEVOR irgendetwas geladen wird.
    /// </summary>
    [Fact]
    public async Task Install_RefusesAPackageMadeForAnotherGameVersion()
    {
        var config = new MemoryConfig { Current = new LauncherConfig { SiteBaseUrl = "https://test.invalid" } };
        var service = new AddonService(new HttpClient(), config, new FakeDownloads(""), new TempPaths(_root), Logger());

        var entry = new AddonEntry
        {
            Id = "modern-only", Name = "Modern Only", Version = "1",
            Url = "https://addons.stonetavern.app/modern-only.zip",
            Sha256 = new string('a', 64), Size = 1024,
            Folders = ["ModernOnly"], Builds = [42597],
        };

        var result = await service.InstallAsync(_root, entry, 5875);

        Assert.False(result.Ok);
        Assert.Contains("1.14.2", result.Error!);
        Assert.Contains("1.12", result.Error!);
        Assert.False(Directory.Exists(Path.Combine(_root, "Interface", "AddOns", "ModernOnly")));
    }

    [Fact]
    public async Task Install_RefusesAnEntryThatDeclaresNoGameVersionAtAll()
    {
        var config = new MemoryConfig { Current = new LauncherConfig { SiteBaseUrl = "https://test.invalid" } };
        var service = new AddonService(new HttpClient(), config, new FakeDownloads(""), new TempPaths(_root), Logger());

        var entry = new AddonEntry
        {
            Id = "nobuild", Name = "No Build", Version = "1",
            Url = "https://addons.stonetavern.app/nobuild.zip",
            Sha256 = new string('a', 64), Size = 1024,
            Folders = ["NoBuild"], Builds = [],
        };

        var result = await service.InstallAsync(_root, entry, 42597);

        Assert.False(result.Ok);
        Assert.Contains("no game version", result.Error!);
    }

    // ── Where the addons folder is ────────────────────────────────────────────────────────────

    [Fact]
    public void TheAddonsFolderIsFoundInBothClientShapes()
    {
        var legacy = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(Path.Combine(legacy, "Interface", "AddOns"));
        Assert.Equal(Path.Combine(legacy, "Interface", "AddOns"), AddonService.AddonsDir(legacy));

        var modern = Path.Combine(_root, "modern");
        var inner = Path.Combine(modern, "World of Warcraft", "_classic_era_", "Interface", "AddOns");
        Directory.CreateDirectory(inner);
        Assert.Equal(inner, AddonService.AddonsDir(modern));
    }
}
