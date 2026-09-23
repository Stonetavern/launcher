using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Localization;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using WowLauncher.ViewModels;
using Xunit;

namespace WowLauncher.Tests.E2E;

/// <summary>
/// <b>Ebene B.3</b> of <c>(internal design notes, not published)</c> — the
/// repair path as one chain: server manifest over real HTTP → per-file manifest over real HTTP →
/// hashing the tree on disk → the decision whether anything is fetched at all.
///
/// <para>🔴 <b>The plan's premise for this level is wrong, and these tests say so out loud.</b> §3 B.3
/// asks to measure that exactly ONE broken file is re-fetched and calls a silent fallback to the full
/// download the most expensive defect of this launcher. There is no per-file repair to measure:
/// <c>PlayViewModel.Repair()</c> verifies per file and then, on ANY defect, downloads the whole ZIP —
/// see <c>PlayViewModel.cs:1991</c> ("downloading a fresh client") and the method header at 1900,
/// which names this "Phase 1" explicitly. The full download is the designed behaviour, not a silent
/// fallback. So these tests measure what actually exists, and
/// <see cref="OneDamagedFile_CostsTheWholePackage_WhichIsThePhase1Design"/> pins the cost of it
/// instead of leaving it as an assumption.</para>
///
/// <para>What is genuinely worth a through-stitch here is the saving that DOES exist, and it is the
/// one nobody would notice breaking: an <b>intact</b> install must download NOTHING. If verification
/// silently stops finding the files — a path-rooting bug did exactly that on 2026-08-12, reporting
/// all 1123 files of a healthy install as missing — every Repair costs a player gigabytes and no log
/// turns red.</para>
/// </summary>
public sealed class ClientRepairChainTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "stonetavern-b3-" + Guid.NewGuid().ToString("N"));

    private static Serilog.ILogger Log => Serilog.Core.Logger.None;

    /// <summary>Enough entries for <see cref="VerifyReport.LooksLikeADifferentPackage"/> to be able to
    /// reach a verdict at all — below its threshold of 100 "everything missing" proves nothing.</summary>
    private const int FileCount = 120;

    public ClientRepairChainTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task AnIntactInstall_DownloadsNothing()
    {
        var (origin, vm, installDir) = await ArrangeAsync(Damage.None);
        using (origin)
        {
            await vm.RepairCommand.ExecuteAsync(null);

            // The measurement: the package was never asked for. Not "the state says Ready" — a state
            // can be right while gigabytes went over the wire.
            Assert.DoesNotContain(origin.Requests, r => r.Path == "/client/base.zip");

            // And the chain really ran, rather than falling out early somewhere: the per-file manifest
            // was fetched. Without this the assertion above would also hold for a Repair that did nothing.
            Assert.Contains(origin.Requests, r => r.Path == "/client/files.json" && r.Status == 200);
            Assert.Equal(LauncherState.Ready, vm.State);
            Assert.Equal(FileCount, Directory.GetFiles(installDir, "*", SearchOption.AllDirectories).Length);

            // 🔴 The assertion the first version of this test was missing, and the reason it was a
            // placebo: "nothing was downloaded" is ALSO what happens when verification looks in the
            // wrong place. Everything missing plus nothing corrupt is exactly the foreign-client
            // verdict, so the guard swallows the failure and the test stays green. Measured on
            // 2026-09-15 by mutating ContentRoot.Resolve away — the test did not notice.
            // The install must therefore be recognised as INTACT, not as a stranger's.
            Assert.NotEqual(Loc.T("Play_ForeignClient_NotOurs"), vm.ReadyDetail);
        }
    }

    [Fact]
    public async Task OneDamagedFile_CostsTheWholePackage_WhichIsThePhase1Design()
    {
        // 🔴 This test does NOT assert an improvement — it pins the price of the shipped design so the
        // number is measured rather than assumed, and so the day a per-file repair lands, this test is
        // the one that goes red and has to be rewritten. That is the intended shape.
        var (origin, vm, _) = await ArrangeAsync(Damage.OneFileCorrupt);
        using (origin)
        {
            await vm.RepairCommand.ExecuteAsync(null);

            var zip = origin.Requests.Where(r => r.Path == "/client/base.zip").ToList();
            Assert.NotEmpty(zip);

            // One changed byte, and the whole package crosses the wire.
            Assert.Equal(PackageBytes.Length, zip.Sum(r => r.BytesWritten));

            // Not one per-file address was ever requested — the evidence that no per-file repair exists.
            Assert.DoesNotContain(origin.Requests, r => r.Path.StartsWith("/client/files/", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task AClientThePlayerBroughtHimself_IsLeftAlone()
    {
        // Report ST-KQYG-ARA3: a player with his own Blizzard client was told over and over that his
        // client needed an 8 GB update, because every single manifest path missed. "Nothing of this
        // package is here" and "this package is damaged" look identical from the outside and have
        // opposite right answers.
        var (origin, vm, _) = await ArrangeAsync(Damage.NotOurPackageAtAll);
        using (origin)
        {
            await vm.RepairCommand.ExecuteAsync(null);

            Assert.DoesNotContain(origin.Requests, r => r.Path == "/client/base.zip");
            Assert.Contains(origin.Requests, r => r.Path == "/client/files.json" && r.Status == 200);
            Assert.Equal(LauncherState.Ready, vm.State);

            // The launcher also SAYS it, rather than just staying quiet. 🔴 Note what this line is
            // not: it is no second safeguard against a vacuous pass, because the same message is
            // already set during InitAsync by the locate path (PlayViewModel.cs:1805) and would stand
            // here even if Repair itself had no guard at all. The assertion that discriminates is the
            // one above — with the Repair guard removed, the whole package is fetched (measured
            // 2026-09-15: 64026 bytes of /client/base.zip).
            Assert.Equal(Loc.T("Play_ForeignClient_NotOurs"), vm.ReadyDetail);
        }
    }

    // ─── fixture ──────────────────────────────────────────────────────────

    private enum Damage { None, OneFileCorrupt, NotOurPackageAtAll }

    /// <summary>The bytes the "client package" consists of. Small, but its LENGTH is the number the
    /// second test reports as the cost of a one-byte defect, so it is derived, never typed.</summary>
    private static readonly byte[] PackageBytes = Encoding.UTF8.GetBytes(
        new string('P', 64_000) + "stonetavern-client-package");

    private async Task<(TestOrigin Origin, PlayViewModel Vm, string InstallDir)> ArrangeAsync(Damage damage)
    {
        var origin = TestOrigin.Start();
        var paths = new TempPaths(_root);
        paths.EnsureDirectories();

        var config = new MemoryConfig
        {
            Current = new LauncherConfig
            {
                ManifestUrl = origin.BaseUrl + "/manifest.json",
                RealmlistAddress = "play.stonetavern.app",
            },
        };

        var build = RealmRegistry.Resolve(config.Current).Client.Build;
        var installDir = paths.ClientInstallDir(build);
        Directory.CreateDirectory(installDir);

        // The per-file manifest, and a tree on disk that matches it — or does not, depending.
        var entries = new List<ClientFileEntry>();
        for (var i = 0; i < FileCount; i++)
        {
            var relative = $"Data/part{i:D3}.bin";
            var content = Encoding.UTF8.GetBytes($"file {i} of the stonetavern client package");
            entries.Add(new ClientFileEntry
            {
                Path = relative,
                Size = content.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            });

            if (damage == Damage.NotOurPackageAtAll) continue;   // nothing of ours on disk

            var onDisk = Path.Combine(installDir, "Data", $"part{i:D3}.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(onDisk)!);
            // Same LENGTH, different content: a size check alone would call this intact, so the defect
            // can only be found by hashing — which is the step the chain is here to prove still happens.
            await File.WriteAllBytesAsync(onDisk,
                damage == Damage.OneFileCorrupt && i == 42
                    ? Encoding.UTF8.GetBytes($"FILE {i} OF THE STONETAVERN CLIENT PACKAGE")
                    : content);
        }

        if (damage == Damage.NotOurPackageAtAll)
        {
            // A real, working install — just not ours. The launcher must recognise it as a stranger's
            // client, not as a damaged copy of the package.
            await File.WriteAllTextAsync(Path.Combine(installDir, "WoW.exe"), "a client the player brought");
        }

        origin.Publish("/client/files.json",
            JsonSerializer.Serialize(new ClientFileManifest { Build = build, Version = "1.0.0", Files = entries },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        origin.Publish("/client/base.zip", PackageBytes);

        var serverManifest = new ServerManifest
        {
            CurrentVersion = "1.0.0",
            ActivePhase = "vanilla",
            Phases =
            [
                new PhaseManifest
                {
                    Phase = "vanilla",
                    Realmlist = "play.stonetavern.app",
                    Client = new ManifestFile
                    {
                        Version = "1.0.0",
                        Url = origin.BaseUrl + "/client/base.zip",
                        Sha256 = Convert.ToHexString(SHA256.HashData(PackageBytes)).ToLowerInvariant(),
                        FilesUrl = origin.BaseUrl + "/client/files.json",
                    },
                },
            ],
        };
        origin.Publish("/manifest.json", JsonSerializer.Serialize(serverManifest));

        config.Current.ClientInstalls[build] = installDir;

        var http = origin.HttpClientForPlain();
        var vm = new PlayViewModel(
            config,
            // The REAL manifest service: the per-file manifest is fetched over the wire, parsed from
            // what the server actually sent. A double here would skip the leg most likely to drift.
            new ManifestService(http, config, Log),
            new FoundClient(installDir),
            new OfflineStatus(),
            new DownloadService(http, Log, (_, _) => Task.CompletedTask, 1),
            new ClientVerifyService(Log),
            new NoUpdate(), new NoNews(), new ExitNow(), paths,
            new FixedFolderPicker(installDir), Log);

        await vm.InitAsync();
        origin.ClearRequests();   // everything from here on is the Repair under test
        return (origin, vm, installDir);
    }

    // ─── doubles for the parts that are not under test ────────────────────

    private sealed class MemoryConfig : IConfigService
    {
        public LauncherConfig Current = new();
        public LauncherConfig Load() => Current;
        public void Save(LauncherConfig config) => Current = config;
        public bool LastSaveSucceeded => true;
    }

    private sealed class FoundClient(string installDir) : IClientService
    {
        public string? FindWowExe(string? configuredPath = null) => Path.Combine(installDir, "WoW.exe");
        public string? FindWowExeForBuild(int gameBuild, IReadOnlyDictionary<int, string> installs) =>
            Path.Combine(installDir, "WoW.exe");
        public IReadOnlyDictionary<int, string> DetectInstalls(IReadOnlyDictionary<int, string> known) => known;
        public int? DetectBuild(string wowDirectory) => null;
        public bool IsGameRunning() => false;
        public void SetRealmlist(string wowDirectory, string realmlistAddress) { }
        public void ConfigureClient(string wowDirectory, string locale, string realmlistAddress) { }
        public Task<GameLaunchResult> LaunchAsync(string wowExePath) =>
            Task.FromResult(new GameLaunchResult(false, null, "test"));
    }

    private sealed class OfflineStatus : IServerStatusService
    {
        public Task<ServerStatusResult> CheckAsync(string host, int port = 3724, CancellationToken ct = default) =>
            Task.FromResult(new ServerStatusResult { Online = false, PlayerCount = 0 });
    }

    private sealed class NoUpdate : IUpdateService
    {
        public event EventHandler? LauncherUpdateStarting { add { } remove { } }

        public Task<bool> CheckAndApplyAsync(ServerManifest? manifest, CancellationToken ct = default) =>
            Task.FromResult(false);

        public LauncherUpdateNotice? CheckForNotice(ServerManifest? manifest) => null;
    }

    private sealed class NoNews : INewsService
    {
        public Task<IReadOnlyList<NewsItem>> GetNewsAsync(bool forceRefresh = false, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NewsItem>>([]);
    }

    private sealed class ExitNow : ILaunchExitPolicy
    {
        public Task<bool> ConfirmClientRunningAsync(string exePath) => Task.FromResult(false);
    }

    private sealed class FixedFolderPicker(string folder) : IFolderPickerService
    {
        public Task<string?> PickFolderAsync(string title, string? startAt = null) =>
            Task.FromResult<string?>(folder);
    }

    private sealed class TempPaths(string root) : IAppPaths
    {
        public string Root => root;
        public string StateDir => root;
        public string ConfigDir => root;
        public string CacheDir => root;
        public string LogDir => root;
        public string ShareDir => root;
        public string ConfigFilePath => Path.Combine(root, "launcher_config.json");
        public string NewsCacheFilePath => Path.Combine(root, "news-cache.json");
        public string ClientInstallDir(int gameBuild) => Path.Combine(root, $"WoW-Client-{gameBuild}");
        public string ClientDownloadZip(int gameBuild) => Path.Combine(root, $"WoW-Client-{gameBuild}.zip");
        public void EnsureDirectories() => Directory.CreateDirectory(root);
    }
}
