namespace WowLauncher.Tests;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using WowLauncher.ViewModels;
using Xunit;

/// <summary>
/// Die gewählte Spielsprache überlebte einen Realmwechsel nicht.
///
/// <para><b>Was passiert war</b> (Owner-Befund 2026-08-13, am eigenen Mac reproduziert, 1.14.2).
/// Der Spieler stellt auf Deutsch, wechselt den Realm, und steht wieder auf Englisch — ohne Meldung,
/// ohne Fehler. Im Protokoll steht der ganze Vorgang in drei Sekunden:</para>
/// <code>
/// 11:06:22.624  Locale → deDE                                   der Spieler wählt
/// 11:06:24.196  Sprachmenü: 1 Einträge [enUS] · Client (keiner)  der Reset in ApplyExpansionAsync
/// 11:06:24.328  Found build 42597 client · Sprachmenü: 5 Einträge
/// 11:06:25.902  Locale → enUS                                    die Wahl ist weg
/// </code>
///
/// <para><b>Die Kette.</b> <c>ApplyExpansionAsync</c> setzt <c>_wowPath = ""</c>, damit eine
/// geworfene Anwendung den Client-Pfad der alten Ära nicht stehen lässt. Das ist richtig. Am
/// Property-Setter hängt aber die Neuberechnung des Sprachmenüs, und ohne Client ist deren Antwort
/// der Platzhalter: Englisch, sonst nichts. Diese Antwort wurde in die LISTE geschrieben. Ein
/// Auswahlfeld lässt eine Auswahl fallen, die in seiner Liste nicht mehr vorkommt, und schreibt die
/// verbliebene zurück — ein echter Wechsel, der gespeichert wird.</para>
///
/// <para><b>Was dieser Test nachbaut.</b> Genau dieses Rückschreiben, in
/// <see cref="SpiegleWaehlerVerhalten"/>: kommt eine neue Liste, und die aktuelle Wahl steht nicht
/// darin, dann schreibt der Wähler den ersten Eintrag zurück. Ohne diesen Nachbau wäre der Test ein
/// Placebo — die Liste allein kippt nichts, sie ist nur die Bedingung dafür. Gegenprobe gefahren: mit
/// dem Fix zurückgenommen wird der Test rot (<c>enUS</c> statt <c>deDE</c>).</para>
/// </summary>
public sealed class LocaleSurvivesEraResetTests : IDisposable
{
    private const int ModernBuild = 42597;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "locale-reset-" + Guid.NewGuid().ToString("N"));

    public LocaleSurvivesEraResetTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* Aufräumen */ } }

    /// <summary>Eine 1.14.2-Installation, wie sie auf der Platte liegt: die ausführbare Datei eine
    /// Ebene tiefer, und daneben die <c>.build.info</c>, aus der die Sprachen gelesen werden. Fünf
    /// Sprachen mit Text, genau die, die auch der Realm beantworten kann.</summary>
    private string ModernClientDir()
    {
        var root = Path.Combine(_root, "WoW-Client-42597", "World of Warcraft");
        var era = Path.Combine(root, "_classic_era_");
        Directory.CreateDirectory(era);
        File.WriteAllText(Path.Combine(era, "WowClassic.exe"), "x");
        File.WriteAllText(Path.Combine(root, ".build.info"),
            "Branch!STRING:0|Active!DEC:1|Tags!STRING:0|Version!STRING:0|Product!STRING:0\n"
            + "eu|1|"
            + "Windows x86_64 EU? enUS text?:"
            + "Windows x86_64 EU? deDE text?:"
            + "Windows x86_64 EU? frFR text?:"
            + "Windows x86_64 EU? esES text?:"
            + "Windows x86_64 EU? ruRU text?"
            + "|1.14.2.42597|wow_classic_era\n");
        return era;
    }

    private (PlayViewModel vm, MemoryConfig cfg) NewPlay(string clientDir)
    {
        var log = Serilog.Core.Logger.None;
        var cfg = new MemoryConfig(clientDir);
        var vm = new PlayViewModel(cfg, new NoManifest(), new InstalledClient(clientDir),
            new OfflineStatus(), new NoDownload(), new ClientVerifyService(log), new NoUpdate(),
            new NoNews(), new ExitNow(), new TempPaths(_root), new NoPicker(), log);
        return (vm, cfg);
    }

    /// <summary>
    /// Das Verhalten des Auswahlfelds, das der Test sonst nicht hätte: eine neue Liste, in der die
    /// aktuelle Wahl nicht vorkommt, lässt es die Wahl fallen und den ersten Eintrag zurückschreiben.
    /// Ohne diesen Nachbau bliebe der Test auch ohne Fix grün.
    /// </summary>
    private static void SpiegleWaehlerVerhalten(PlayViewModel vm)
    {
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PlayViewModel.AvailableLocales)) return;
            var liste = vm.AvailableLocales;
            if (liste.Count == 0) return;
            if (liste.Any(l => l.Code == vm.SelectedLocaleCode)) return;
            vm.SelectedLocaleCode = liste[0].Code;
        };
    }

    /// <summary>
    /// 🔴 Der Fall des Owners. Deutsch ist gewählt, dann läuft ein Wechsel durch — und Deutsch steht
    /// danach immer noch, in der Ansicht wie in der gespeicherten Einstellung.
    /// </summary>
    [Fact]
    public async Task EinEraReset_LaesstDieGewaehlteSpracheStehen()
    {
        var (vm, cfg) = NewPlay(ModernClientDir());
        await vm.InitAsync();
        SpiegleWaehlerVerhalten(vm);

        Assert.Equal(["enUS", "deDE", "frFR", "esES", "ruRU"], vm.AvailableLocales.Select(l => l.Code));
        Assert.Equal("deDE", vm.SelectedLocaleCode);

        // Der Wechsel, so wie ihn ein Klick auf den zweiten Realm auslöst. Er läuft durch
        // ApplyExpansionAsync und damit durch `_wowPath = ""` — die Stelle, an der das Sprachmenü
        // kurzzeitig zum Platzhalter wird.
        await vm.SwitchRealmAsync();

        Assert.Equal("deDE", vm.SelectedLocaleCode);
        Assert.Equal("deDE", cfg.Current.Locale);
        Assert.Equal(5, vm.AvailableLocales.Count);
    }

    /// <summary>Die Gegenprobe zur Messung selbst: der Nachbau des Wählers greift wirklich. Schrumpft
    /// die Liste tatsächlich auf Englisch, dann kippt die Wahl — sonst würde der Test oben auch dann
    /// grün bleiben, wenn er gar nichts prüft.</summary>
    [Fact]
    public void DerNachbauDesWaehlers_KipptDieWahlWirklich()
    {
        var (vm, _) = NewPlay(ModernClientDir());
        vm.AvailableLocales = ClientLocales.ForInstalled(["enUS", "deDE"]);
        vm.SelectedLocaleCode = "deDE";
        SpiegleWaehlerVerhalten(vm);

        vm.AvailableLocales = ClientLocales.SupportedLocales;   // nur noch Englisch

        Assert.Equal("enUS", vm.SelectedLocaleCode);
    }

    // ── Attrappen ────────────────────────────────────────────────────────────────────────────────

    private sealed class MemoryConfig(string clientDir) : IConfigService
    {
        public LauncherConfig Current = new()
        {
            SelectedRealmId = "testrealm",
            Realms =
            [
                new RealmEntry
                {
                    Id = "testrealm", Name = "Testrealm", RealmlistAddress = "play.stonetavern.app",
                    ClientKey = "1.14.2", ClientKeys = ["1.14.2"], IsPreset = false, IsLive = true,
                },
            ],
            RealmlistAddress = "play.stonetavern.app",
            Locale = "deDE",
            ClientInstalls = new Dictionary<int, string> { [ModernBuild] = clientDir },
        };
        public LauncherConfig Load() => Current;
        public void Save(LauncherConfig config) => Current = config;
        public bool LastSaveSucceeded => true;
    }

    private sealed class NoManifest : IManifestService
    {
        public Task<ServerManifest?> FetchAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(null);
        public Task<ServerManifest?> FetchLauncherManifestAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(null);
        public Task<ClientFileManifest?> FetchFileManifestAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<ClientFileManifest?>(null);
    }

    private sealed class InstalledClient(string dir) : IClientService
    {
        public string? FindWowExe(string? configuredPath = null) => Path.Combine(dir, "WowClassic.exe");
        public string? FindWowExeForBuild(int gameBuild, IReadOnlyDictionary<int, string> installs) =>
            gameBuild == ModernBuild ? Path.Combine(dir, "WowClassic.exe") : null;
        public IReadOnlyDictionary<int, string> DetectInstalls(IReadOnlyDictionary<int, string> known) => known;
        public int? DetectBuild(string wowDirectory) => ModernBuild;
        public bool IsGameRunning() => false;
        public void SetRealmlist(string wowDirectory, string realmlistAddress) { }
        public void ConfigureClient(string wowDirectory, string locale, string realmlistAddress) { }
        public Task<GameLaunchResult> LaunchAsync(string wowExePath) =>
            Task.FromResult(new GameLaunchResult(false, null, "test"));
    }

    private sealed class NoDownload : IDownloadService
    {
        public Task<DownloadResult> DownloadFileAsync(string url, string destPath,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(DownloadResult.Fail(DownloadFailure.Network));
        public Task<bool> ExtractZipAsync(string z, string d, IProgress<string>? p = null, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> ExtractClientAsync(string z, string d, IProgress<string>? p = null, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> VerifyHashAsync(string path, string expected, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed class OfflineStatus : IServerStatusService
    {
        public Task<ServerStatusResult> CheckAsync(string host, int port = 3724, CancellationToken ct = default) =>
            Task.FromResult(new ServerStatusResult { Online = false, PlayerCount = 0 });
    }

    private sealed class NoUpdate : IUpdateService
    {
        public event EventHandler? LauncherUpdateStarting { add { } remove { } }
        public Task<bool> CheckAndApplyAsync(ServerManifest? m, CancellationToken ct = default) => Task.FromResult(false);
        public LauncherUpdateNotice? CheckForNotice(ServerManifest? m) => null;
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

    private sealed class NoPicker : IFolderPickerService
    {
        public Task<string?> PickFolderAsync(string title, string? startIn = null) => Task.FromResult<string?>(null);
    }

    private sealed class TempPaths(string root) : IAppPaths
    {
        public string ConfigDir => root;
        public string StateDir => root;
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
