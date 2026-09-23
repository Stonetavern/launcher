using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using WowLauncher.ViewModels;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Was der Spieler zwischen dem Doppelklick auf den Launcher und dem druckbaren Play-Knopf abwartet.
///
/// <para>Anlass sind echte Spielerprotokolle: 75 Sekunden vom Start bis zum Spielstart, fast
/// vollstaendig Wartezeit auf Netzabrufe, vor einem Client, der komplett lokal vorlag (Bericht
/// ST-PB2B-QZ17, 2026-08-31). Zwei Ursachen stecken darin: die beiden unabhaengigen Manifeste wurden
/// nacheinander geholt, und der Spielzustand wartete auf einen Realm-Ping, dessen Antwort er nie
/// liest.</para>
///
/// <para>Diese Tests messen die REIHENFOLGE, nicht die Wanduhr: "es ist schneller" waere auf einer
/// belasteten Maschine ein Zufallsergebnis. Stattdessen halten die Doubles die Abrufe an und
/// beobachten, was der Launcher in der Zwischenzeit bereits getan hat.</para>
/// </summary>
public sealed class StartPathLatencyTests
{
    /// <summary>Beide Manifest-Abrufe haengen, bis der Test sie freigibt, und melden ihren Beginn.
    /// Damit ist "laufen sie gleichzeitig?" eine Beobachtung statt einer Zeitmessung: stehen beide
    /// Startsignale, bevor eines freigegeben wurde, koennen sie nicht nacheinander gelaufen sein.</summary>
    private sealed class GatedManifest(ServerManifest m) : IManifestService
    {
        private readonly ServerManifest _m = m;
        public readonly TaskCompletionSource RealmStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource LauncherStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ServerManifest?> FetchAsync(CancellationToken ct = default)
        {
            RealmStarted.TrySetResult();
            await Release.Task;
            return _m;
        }

        public async Task<ServerManifest?> FetchLauncherManifestAsync(CancellationToken ct = default)
        {
            LauncherStarted.TrySetResult();
            await Release.Task;
            return null; // "kein Launcher-Manifest" heisst "kein Update", nie "irgendeins"
        }

        public Task<ClientFileManifest?> FetchFileManifestAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<ClientFileManifest?>(null);
    }

    /// <summary>Ein Realm-Ping, der haengt und beim Eintritt festhaelt, welchen Zustand der Launcher
    /// zu diesem Zeitpunkt bereits erreicht hatte.</summary>
    private sealed class GatedStatus : IServerStatusService
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<LauncherState>? ReadState;
        public LauncherState StateWhenPingBegan = LauncherState.Initializing;

        public async Task<ServerStatusResult> CheckAsync(string host, int port = 3724, CancellationToken ct = default)
        {
            if (ReadState is not null) StateWhenPingBegan = ReadState();
            Entered.TrySetResult();
            await Release.Task;
            return new ServerStatusResult { Online = false, PlayerCount = 0 };
        }
    }

    /// <summary>Nichts wird geladen: dieser Test faehrt nur den Startweg.</summary>
    private sealed class NoDownload : IDownloadService
    {
        public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("Der Startweg laedt nichts herunter.");
        public Task<bool> ExtractZipAsync(string zipPath, string destDir,
            IProgress<string>? progress = null, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> ExtractClientAsync(string zipPath, string destDir,
            IProgress<string>? progress = null, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> VerifyHashAsync(string path, string expectedSha256, CancellationToken ct = default) =>
            Task.FromResult(true);
    
        /// <summary>Pflichtteil der Schnittstelle: ohne Grund gilt der Fehlschlag als nicht behebbar,
        /// also als kaputtes Paket. Das ist die sichere Richtung fuer eine Attrappe.</summary>
        public async System.Threading.Tasks.Task<WowLauncher.Models.ExtractOutcome> ExtractClientWithReasonAsync(
            string zipPath, string destDir, bool freshInstall,
            System.IProgress<string>? progress = null,
            System.Threading.CancellationToken ct = default) =>
            await ExtractClientAsync(zipPath, destDir, progress, ct).ConfigureAwait(false)
                ? WowLauncher.Models.ExtractOutcome.Success
                : WowLauncher.Models.ExtractOutcome.Fail(WowLauncher.Models.ExtractFailure.Unknown);
    }

    private sealed class MemoryConfig : IConfigService
    {
        public LauncherConfig Current = new()
        {
            ManifestUrl = "https://downloads.example.invalid/manifest.json",
            RealmlistAddress = "play.stonetavern.app",
        };
        public LauncherConfig Load() => Current;
        public void Save(LauncherConfig config) => Current = config;
        public bool LastSaveSucceeded => true;
    }

    private sealed class NoClient : IClientService
    {
        public string? FindWowExe(string? configuredPath = null) => null;
        public string? FindWowExeForBuild(int gameBuild, IReadOnlyDictionary<int, string> installs) => null;
        public IReadOnlyDictionary<int, string> DetectInstalls(IReadOnlyDictionary<int, string> known) =>
            new Dictionary<int, string>();
        public int? DetectBuild(string wowDirectory) => null;
        public bool IsGameRunning() => false;
        public void SetRealmlist(string wowDirectory, string realmlistAddress) { }
        public void ConfigureClient(string wowDirectory, string locale, string realmlistAddress) { }
        public Task<GameLaunchResult> LaunchAsync(string wowExePath) =>
            Task.FromResult(new GameLaunchResult(false, null, "test"));
    }

    private sealed class NoUpdate : IUpdateService
    {
        // Never raised here: these doubles model "there is no update", so nothing announces one.
        public event System.EventHandler? LauncherUpdateStarting { add { } remove { } }

        public Task<bool> CheckAndApplyAsync(ServerManifest? manifest, CancellationToken ct = default) => Task.FromResult(false);
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

    private sealed class TempPaths : IAppPaths
    {
        public string Root => _root;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "st-switch-" + Guid.NewGuid().ToString("N"));
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

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    private static ServerManifest Manifest() => new()
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
                    Url = "https://downloads.example.invalid/vanilla.zip",
                    Sha256 = new string('a', 64),
                },
            },
        ],
    };

    private static PlayViewModel NewPlay(IManifestService manifest, IServerStatusService status, TempPaths paths)
    {
        var log = new Serilog.LoggerConfiguration().CreateLogger();
        return new PlayViewModel(new MemoryConfig(), manifest, new NoClient(), status, new NoDownload(),
            new ClientVerifyService(log), new NoUpdate(), new NoNews(), new ExitNow(), paths,
            new FixedFolderPicker(paths.Root), log);
    }

    // ── Tests ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Realm- und Launcher-Manifest sind zwei unabhaengige Dokumente auf zwei Adressen. Werden
    /// sie nacheinander geholt, zahlt ein Spieler auf einer haengenden Leitung beide Zeitgrenzen
    /// hintereinander (30 s + 30 s, DependencyInjection.cs:362). Der Beweis fuer "gleichzeitig" ist,
    /// dass BEIDE Abrufe begonnen haben, obwohl noch keiner geantwortet hat — sequentiell unmoeglich.</summary>
    [Fact]
    public async Task BothManifests_AreFetchedAtTheSameTime_NotOneAfterTheOther()
    {
        var paths = new TempPaths();
        paths.EnsureDirectories();
        var manifest = new GatedManifest(Manifest());
        var status = new GatedStatus();
        status.Release.TrySetResult();
        var vm = NewPlay(manifest, status, paths);

        var init = vm.InitAsync();
        var beide = Task.WhenAll(manifest.RealmStarted.Task, manifest.LauncherStarted.Task);
        var gewonnen = await Task.WhenAny(beide, Task.Delay(TimeSpan.FromSeconds(10)));
        manifest.Release.TrySetResult();
        await Task.WhenAny(init, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(manifest.RealmStarted.Task.IsCompletedSuccessfully, "Der Realm-Manifest-Abruf hat nie begonnen.");
        Assert.True(ReferenceEquals(gewonnen, beide),
            "Der zweite Manifest-Abruf begann erst, nachdem der erste geantwortet hatte — sie laufen nacheinander.");
    }

    /// <summary>Der Spielzustand liest die Antwort des Realm-Pings nicht (ResolveBuildState kennt nur
    /// Konfiguration und Installation). Stand er trotzdem dahinter, wartete der Spieler bis zu 13
    /// Sekunden vor einem startbereiten Client: 3 s TCP-Probe plus 10 s HTTP fuer eine Spielerzahl, die
    /// der Launcher bewusst nie anzeigt.</summary>
    [Fact]
    public async Task TheActionState_IsDecidedBefore_TheRealmPingAnswers()
    {
        var paths = new TempPaths();
        paths.EnsureDirectories();
        var manifest = new GatedManifest(Manifest());
        manifest.Release.TrySetResult();
        var status = new GatedStatus();
        PlayViewModel? vm = null;
        status.ReadState = () => vm!.State;
        vm = NewPlay(manifest, status, paths);

        var init = vm.InitAsync();
        await Task.WhenAny(status.Entered.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(status.Entered.Task.IsCompletedSuccessfully, "Der Realm-Ping wurde nie erreicht.");

        // Der Zustand steht, bevor der Ping ueberhaupt antworten konnte.
        Assert.NotEqual(LauncherState.Initializing, status.StateWhenPingBegan);

        status.Release.TrySetResult();
        await Task.WhenAny(init, Task.Delay(TimeSpan.FromSeconds(10)));
    }
}
