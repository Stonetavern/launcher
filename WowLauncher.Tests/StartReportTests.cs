namespace WowLauncher.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using WowLauncher.ViewModels;
using Xunit;

/// <summary>
/// Der Startbericht zum Kopieren.
///
/// <para>Er ist ein Diagnose-Werkzeug, und ein Diagnose-Werkzeug, das etwas anderes berichtet als der
/// Launcher tut, ist schlimmer als keins: es schickt jemanden dazu, das Falsche zu suchen. Jeder Test
/// hier bewacht genau diese Fehlerform - eine Zeile, die plausibel aussieht und nicht stimmt.</para>
///
/// <para>Die zwei, auf die es ankommt: die Adresse muss die sein, die wirklich in <c>realmlist.wtf</c>
/// landet (bei einem mitgelieferten Realm steht in der Konfiguration nichts), und der Addon-Satz muss
/// von der PLATTE kommen, nicht aus der Konfiguration - das Spiel laedt den Ordner, nicht die
/// Einstellung.</para>
/// </summary>
public sealed class StartReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "st-startreport-" + Guid.NewGuid().ToString("N"));

    public StartReportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    // ── Der Realm und seine Adresse ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Ein mitgelieferter Realm traegt seine Adresse im Build, nicht in der Konfiguration. Wer die
    /// gespeicherte Zeichenkette berichtet, schreibt bei JEDEM Stonetavern-Spieler eine leere Adresse
    /// hin - und der Leser sucht danach einen Fehler, den es nicht gibt.
    /// </summary>
    [Fact]
    public void EinMitgelieferterRealm_BerichtetDieAdresseDieWirklichBenutztWird()
    {
        var cfg = new LauncherConfig { SelectedRealmId = RealmRegistry.StonetavernId };
        var text = StartReport.Format(Subject(cfg).Build());

        Assert.Contains(RealmRegistry.StonetavernAddress, text, StringComparison.Ordinal);
        Assert.Contains("shipped", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ein gespeicherter Realm-Eintrag OHNE Adresse ist ein echter Zustand - eine gespeicherte Kopie
    /// eines Presets gewinnt ueber die ausgelieferte Fassung, samt ihrer Luecken - und er ist die
    /// Diagnose selbst: so ein Realm startet ins Nichts. Der Bericht muss es aussprechen. Eine leere
    /// Zeile hinter "Address:" liest sich wie ein Formatierungsfehler und wird ueberblaettert, und
    /// zwar von genau der Person, die es haette sehen koennen.
    /// </summary>
    [Fact]
    public void EinRealmOhneAdresse_SagtDasStattEinerLeerenZeile()
    {
        var cfg = new LauncherConfig { SelectedRealmId = RealmRegistry.StonetavernId };
        cfg.Realms.Add(new RealmEntry
        {
            Id = RealmRegistry.StonetavernId, Name = "Stonetavern",
            RealmlistAddress = "", IsPreset = true, ClientKey = "1.12.1",
        });

        var text = StartReport.Format(Subject(cfg).Build());

        Assert.Contains("Address: " + StartReport.Unknown, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Address: \n", text, StringComparison.Ordinal);
    }

    /// <summary>Ein selbst angelegter Realm wird als solcher ausgewiesen. Das entscheidet, wen man
    /// ueberhaupt fragen kann: an einem fremden Server koennen wir nichts richten.</summary>
    [Fact]
    public void EinEigenerRealm_StehtMitAdresseUndHerkunftDrin()
    {
        var cfg = new LauncherConfig { SelectedRealmId = "own" };
        cfg.Realms.Add(new RealmEntry
        {
            Id = "own", Name = "Localhost", RealmlistAddress = "127.0.0.1:8085", ClientKey = "1.12.1",
        });

        var text = StartReport.Format(Subject(cfg).Build());

        Assert.Contains("Localhost", text, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:8085", text, StringComparison.Ordinal);
        Assert.Contains("added by hand", text, StringComparison.Ordinal);
    }

    // ── Der Addon-Satz ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Die Konfiguration sagt das eine, die Platte das andere. Berichtet werden muss, was das Spiel
    /// laedt. Ein Test, in dem beide dasselbe sagen, wuerde diesen Fehler nie sehen, deshalb sagen sie
    /// hier ausdruecklich Verschiedenes.
    /// </summary>
    [Fact]
    public void DerAddonSatz_KommtVonDerPlatteNichtAusDerKonfiguration()
    {
        var clientDir = Path.Combine(_dir, "client");
        var addons = Path.Combine(clientDir, "Interface", "AddOns");
        Directory.CreateDirectory(addons);
        File.WriteAllText(Path.Combine(addons, AddonProfileService.MarkerName), "barrens");

        var cfg = new LauncherConfig
        {
            SelectedRealmId = RealmRegistry.StonetavernId,
            AddonProfile = "elwynn",                       // die Konfiguration irrt sich
            ClientInstalls = new Dictionary<int, string> { [5875] = clientDir },
        };

        var report = new StartReport(new MemoryConfig(cfg), new Paths(_dir),
            addonSet: () => new AddonProfileService(Serilog.Log.Logger).ActiveProfile(clientDir),
            version: () => "9.9.9");

        var text = StartReport.Format(report.Build());

        Assert.Contains("barrens", text, StringComparison.Ordinal);
        Assert.DoesNotContain("elwynn", text, StringComparison.Ordinal);
    }

    /// <summary>Ohne eingetragenen Client steht dort ein Wort und keine Leere. Eine leere Zeile liest
    /// sich als "nichts zu berichten", gemeint ist "noch nicht eingerichtet" - und genau der
    /// Unterschied ist bei "ich komme nicht ins Spiel" die Antwort.</summary>
    [Fact]
    public void OhneClient_StehtNichtEingerichtetDaKeineLeereZeile()
    {
        var text = StartReport.Format(Subject(new LauncherConfig()).Build());

        Assert.Contains("Client folder: " + StartReport.Unknown, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Client folder: \n", text, StringComparison.Ordinal);
    }

    // ── Das System ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ohne Distributionsnamen liest sich die Zeile als <c>Unix 7.1.4.204 x64</c> - wahr und nutzlos.
    /// Fast jedes Startproblem unter Linux ist eine Paketfrage (welches Wine, welcher Vulkan-Treiber,
    /// welche glibc), und die beantwortet die Distribution, nicht die Kernel-Nummer. Getestet wird die
    /// Ableseregel, nicht die Maschine, auf der der Test gerade laeuft.
    /// </summary>
    [Theory]
    [InlineData("NAME=\"Fedora Linux\"\nPRETTY_NAME=\"Fedora Linux 44 (KDE Plasma)\"\nID=fedora\n",
        "Fedora Linux 44 (KDE Plasma)")]
    [InlineData("PRETTY_NAME=Arch Linux\nID=arch\n", "Arch Linux")]
    [InlineData("NAME=\"Ubuntu\"\nVERSION_ID=\"26.04\"\n", "")]   // ohne PRETTY_NAME: nichts erfinden
    [InlineData("", "")]
    public void DerSystemname_KommtAusOsRelease(string osRelease, string expected) =>
        Assert.Equal(expected, StartReport.PrettyName(osRelease));

    // ── Verbindung ──────────────────────────────────────────────────────────────────────────────

    /// <summary>1.12.1 spricht direkt mit dem Realm, 1.14.2 laeuft ueber den Proxy. Das ist die
    /// Zeile, die "Login geht, Welt nicht" haeufig erklaert - und die beiden Faelle muessen
    /// unterscheidbar sein, sonst beweist der Test nichts.</summary>
    [Theory]
    [InlineData("1.12.1", "direct")]
    [InlineData("1.14.2", "through the realm proxy")]
    public void DieVerbindungsart_FolgtDemClientDesRealms(string clientKey, string expected)
    {
        var cfg = new LauncherConfig { SelectedRealmId = "own" };
        cfg.Realms.Add(new RealmEntry
        {
            Id = "own", Name = "Own", RealmlistAddress = "realm.invalid", ClientKey = clientKey,
        });

        var text = StartReport.Format(Subject(cfg).Build());

        Assert.Contains("Connection: " + expected, text, StringComparison.Ordinal);
    }

    // ── Was nicht drin sein darf ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Der Bericht wird in einen Discord-Kanal eingefuegt, den Fremde lesen. Er traegt deshalb weder
    /// Kontonamen noch Protokoll: der Problembericht daneben tut das, aber der geht an uns und wird
    /// vorher angezeigt. Hier stuende es fuer immer oeffentlich.
    /// </summary>
    [Fact]
    public void DerBericht_TraegtWederKontonamenNochProtokoll()
    {
        File.WriteAllText(Path.Combine(_dir, "launcher.log"), "a line nobody asked to publish");
        var cfg = new LauncherConfig
        {
            SelectedRealmId = RealmRegistry.StonetavernId,
            LastAccountName = "SomePlayerName",
        };

        var text = StartReport.Format(Subject(cfg).Build());

        Assert.DoesNotContain("SomePlayerName", text, StringComparison.Ordinal);
        Assert.DoesNotContain("nobody asked to publish", text, StringComparison.Ordinal);
    }

    // ── Der Knopf ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Was der Knopf ablegt, ist was daneben steht. Zwei Wege zu demselben Text sind zwei
    /// Gelegenheiten, auseinanderzulaufen.</summary>
    [Fact]
    public async Task DerKnopf_LegtGenauDenAngezeigtenTextAb()
    {
        var vm = Vm(new LauncherConfig { SelectedRealmId = RealmRegistry.StonetavernId },
            out var clipboard, out _);

        await vm.CopyStartReportCommand.ExecuteAsync(null);

        Assert.Equal(vm.StartReportText, clipboard.Text);
        Assert.True(vm.HasStartReportNote);
    }

    /// <summary>
    /// Kopiert wird der Stand von JETZT, nicht der vom Oeffnen der Einstellungen. Zwischen beiden
    /// kann der Spieler den Realm gewechselt haben, und ein Bericht ueber den vorherigen Realm ist
    /// genau die plausible Falschaussage, gegen die das Ganze gebaut ist.
    /// </summary>
    [Fact]
    public async Task DerKnopf_BerichtetDenStandVonJetzt()
    {
        var cfg = new LauncherConfig { SelectedRealmId = RealmRegistry.StonetavernId };
        var vm = Vm(cfg, out var clipboard, out var config);

        // Erst einmal kopieren, DANN wechseln, dann noch einmal. Ohne den ersten Druck bewiese der
        // Test nichts: ein Bericht, der beim ersten Bauen einfriert, waere hier noch richtig, und
        // die Gegenprobe hat genau das 2026-08-05 gezeigt.
        await vm.CopyStartReportCommand.ExecuteAsync(null);
        var before = clipboard.Text!;

        config.Current.Realms.Add(new RealmEntry
        {
            Id = "own", Name = "Localhost", RealmlistAddress = "127.0.0.1:8085", ClientKey = "1.12.1",
        });
        config.Current.SelectedRealmId = "own";

        await vm.CopyStartReportCommand.ExecuteAsync(null);

        Assert.Contains(RealmRegistry.StonetavernAddress, before, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:8085", clipboard.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Eine Zwischenablage kann sich weigern (Wayland-Fokusregeln, ein Compositor ohne das Protokoll).
    /// Der Knopf muss das sagen. Sagt er trotzdem "Kopiert", fuegt der Spieler nichts ein und wundert
    /// sich am anderen Ende jemand ueber eine leere Nachricht.
    /// </summary>
    [Fact]
    public async Task WennDieZwischenablageAblehnt_SagtDerHinweisEtwasAnderes()
    {
        var ok = Vm(new LauncherConfig(), out var okBoard, out _);
        okBoard.Accept = true;
        await ok.CopyStartReportCommand.ExecuteAsync(null);

        var refused = Vm(new LauncherConfig(), out var badBoard, out _);
        badBoard.Accept = false;
        await refused.CopyStartReportCommand.ExecuteAsync(null);

        Assert.NotEqual(ok.StartReportNote, refused.StartReportNote);
        Assert.True(refused.HasStartReportNote);
        Assert.Null(badBoard.Text);
    }

    /// <summary>Ohne Dienst kein Block. Ein Knopf, der nichts kann, ist schlimmer als keiner.</summary>
    [Fact]
    public void OhneBerichtsdienst_BleibtDerBlockUnsichtbar()
    {
        var vm = new SettingsViewModel(new MemoryConfig(new LauncherConfig()), new NoPicker());

        Assert.False(vm.ShowStartReport);
        Assert.Equal("", vm.StartReportText);
    }

    // ── Hilfsmittel ─────────────────────────────────────────────────────────────────────────────

    private StartReport Subject(LauncherConfig cfg) =>
        new(new MemoryConfig(cfg), new Paths(_dir), version: () => "9.9.9");

    private SettingsViewModel Vm(LauncherConfig cfg, out FakeClipboard clipboard, out MemoryConfig config)
    {
        clipboard = new FakeClipboard();
        config = new MemoryConfig(cfg);
        return new SettingsViewModel(config, new NoPicker(), new NoDesktop(),
            new StartReport(config, new Paths(_dir), version: () => "9.9.9"), clipboard);
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public bool Accept = true;
        public string? Text;

        public Task<bool> SetTextAsync(string text)
        {
            // Bei Ablehnung wird NICHTS gemerkt: ein Double, das den Text trotzdem behaelt, koennte
            // den Unterschied zwischen "kopiert" und "abgelehnt" nicht mehr zeigen.
            if (!Accept) return Task.FromResult(false);
            Text = text;
            return Task.FromResult(true);
        }
    }

    private sealed class NoPicker : IFolderPickerService
    {
        public Task<string?> PickFolderAsync(string title, string? startAt = null) =>
            Task.FromResult<string?>(null);
    }

    private sealed class NoDesktop : IDesktopIntegrationService
    {
        public bool IsSupported => false;
        public bool IsInstalled() => false;
        public Task<DesktopIntegrationResult> InstallAsync(System.Threading.CancellationToken ct = default) =>
            Task.FromResult(new DesktopIntegrationResult(DesktopIntegrationOutcome.Unsupported, "not here"));
    }

    private sealed class MemoryConfig(LauncherConfig cfg) : IConfigService
    {
        public LauncherConfig Current = cfg;
        public LauncherConfig Load()
        {
            RealmRegistry.ApplyActiveRealm(Current);
            return Current;
        }
        public void Save(LauncherConfig config) => Current = config;
        public bool LastSaveSucceeded => true;
    }

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
}
