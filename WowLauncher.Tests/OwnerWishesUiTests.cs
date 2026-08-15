using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using WowLauncher.ViewModels;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Three small owner requests from 2026-08-04, each of which is a dead end for a player if it is
/// built only halfway. They are grouped in one file because they share one failure shape: the
/// FUNCTION already existed in every case, and what was missing was the way to it.
///
/// <list type="number">
/// <item><b>The cog beside the profile name.</b> Settings were reachable through a tab, and the tab
/// is the road for someone looking for them. The cog is the handle for someone who already knows.</item>
/// <item><b>The armory says what it is.</b> For a visitor without an account this page can only ever
/// be empty, and the only way to an account was a small text link under a sign-in field on a
/// different page. It also never said that a Stonetavern account is the SAME account the game
/// itself uses, which anyone arriving from another private server has no reason to assume.</item>
/// <item><b>"I already have the game".</b> The locate flow existed, hash-verified against the file
/// manifest, and sat as one dimmed line under the play button that only appears while no client is
/// registered. Overlooked once, it costs a player gigabytes of re-download.</item>
/// </list>
///
/// <para>What is provable here and what is not: the behaviour tests below execute real view models.
/// The layout assertions read the AXAML as text, because this test project deliberately carries no
/// Avalonia.Headless reference. A source assertion cannot prove a control is VISIBLE on screen, only
/// that the markup says what it should. The owner still has to look at it once. That limit is stated
/// rather than papered over.</para>
/// </summary>
public sealed partial class OwnerWishesUiTests
{
    // ── 1 · The way into an account, from the one page that proves you need one ─────────────────

    /// <summary>The armory's register button lands the player on the SIGN-UP form, not on a page
    /// where they have to find it again.</summary>
    [Fact]
    public void DerWegZumKonto_LandetImRegistrierformular_NichtNurAufDerKontoseite()
    {
        var shell = NewShell();

        shell.GoRegisterCommand.Execute(null);

        Assert.True(shell.IsAccount);
        Assert.True(shell.Login.IsRegisterMode);
    }

    /// <summary>
    /// Order matters, and this is the assertion that would catch it being swapped back. The register
    /// mode is set BEFORE the section flips, so the panel is already showing the sign-up form when it
    /// fades in. The other way round the player watches a sign-in card morph into a sign-up card,
    /// which reads like a misclick and invites a second, wrong click.
    ///
    /// <para>Proven by listening to the section change and asserting what the login VM says AT THAT
    /// MOMENT, not afterwards. Asserting after the fact would pass either way, which is precisely the
    /// placebo shape this codebase keeps running into.</para>
    /// </summary>
    [Fact]
    public void DasFormularStehtSchon_BevorDieSeiteEinblendet()
    {
        var shell = NewShell();
        bool? modeWhenSectionFlipped = null;

        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Selected) && modeWhenSectionFlipped is null)
                modeWhenSectionFlipped = shell.Login.IsRegisterMode;
        };

        shell.GoRegisterCommand.Execute(null);

        Assert.True(modeWhenSectionFlipped.HasValue, "the section never changed");
        Assert.True(modeWhenSectionFlipped!.Value,
            "the account page appeared before the sign-up form was switched on");
    }

    /// <summary>The two sentences a visitor needs are in every catalog, in the catalog's own language:
    /// what this page shows, and that one account covers launcher and game. A key that exists only in
    /// English falls back silently and the player reads a language they did not choose.</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("ru")]
    public void JederKatalog_KenntBeideSaetzeDesArsenals(string code)
    {
        var catalog = WowLauncher.Localization.Loc.Load(code);
        Assert.NotNull(catalog);

        foreach (var key in new[] { "Armory_ScopeNote", "Armory_Join_Lead", "Armory_Join_Button" })
        {
            Assert.True(catalog!.ContainsKey(key), $"{code}.json is missing {key}");
            Assert.False(string.IsNullOrWhiteSpace(catalog[key]), $"{code}.json has an empty {key}");
        }
    }

    // ── 2 · "I already have the game" ──────────────────────────────────────────────────────────

    /// <summary>
    /// The settings page offers the SAME locate command the play surface does, not a second
    /// implementation of it. That matters beyond tidiness: recognition is not "find a WoW.exe". It
    /// checks the executable NAME against the active build first (WowClassic.exe can never be 5875),
    /// then the hashes against the file manifest, then records the result with its version. A second
    /// copy of that would eventually disagree with the first about the same folder.
    /// </summary>
    [Fact]
    public void DieEinstellungenBenutzenDenselbenBefehl_WieDieSpielseite()
    {
        var shell = NewShell();

        Assert.NotNull(shell.Settings.LocateClientCommand);
        Assert.Same(shell.Play.LocateExistingClientCommand, shell.Settings.LocateClientCommand);
        Assert.True(shell.Settings.ShowLocateClient);
    }

    /// <summary>A settings VM built without a shell (tests, or any host with no play surface) hides
    /// the whole block instead of showing a button that would do nothing.</summary>
    [Fact]
    public void OhneSpielseite_VerstecktSichDerKnopfGanz()
    {
        var settings = new SettingsViewModel(new MemoryConfig(), new NullFolderPicker());

        Assert.Null(settings.LocateClientCommand);
        Assert.False(settings.ShowLocateClient);
        Assert.False(settings.CanLocateClient);
    }

    /// <summary>
    /// The button greys out with the command it mirrors. Locate is only executable while there is no
    /// usable client (NoClient / EraTransition / DownloadError / Paused); once one is registered the
    /// picker would open and its answer be discarded. The settings surface has to follow that state
    /// live, which is why it subscribes to CanExecuteChanged rather than reading the flag once.
    /// </summary>
    [Fact]
    public async Task DerKnopfFolgtDemZustand_UndMeldetDieAenderung()
    {
        var shell = NewShell();
        await shell.InitAsync();

        Assert.Equal(LauncherState.NoClient, shell.Play.State);
        Assert.True(shell.Settings.CanLocateClient);

        var announced = false;
        shell.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.CanLocateClient)) announced = true;
        };

        // A client is now registered: the play surface leaves the locate-able states.
        shell.Play.State = LauncherState.Ready;

        Assert.False(shell.Settings.CanLocateClient);
        Assert.True(announced, "the settings page was never told the button had to grey out");
    }

    // ── 3 · Layout (source assertions, see the class summary for their limit) ───────────────────

    /// <summary>The title bar carries NO cog. It carried one for a day: asked for on 2026-08-04,
    /// removed on 2026-08-05 with the owner looking at the running window ("das ist auch redundant").
    /// The argument for keeping both was that the tab is for finding and the cog for reaching - which
    /// holds when they are in different places, and does not when they sit in the same bar at the same
    /// time. This test is inverted rather than deleted so nobody rebuilds it from the same reasoning:
    /// the removal is a decision, not an omission.</summary>
    [Fact]
    public void DieTitelleiste_TraegtKeinZahnrad()
    {
        var shell = StripComments(ReadSource("Views/ShellV3Window.axaml"));
        var styles = StripComments(ReadSource("Styles.v3.axaml"));

        Assert.DoesNotContain("v3cog", shell);
        Assert.DoesNotContain("CogGeometry", shell);
        // The style and the geometry go too - dead markup in a shared stylesheet is how a removed
        // control comes back by accident.
        Assert.DoesNotContain("v3cog", styles);
        Assert.DoesNotContain("CogGeometry", styles);

        // The one way in stays, and is the reason the cog can go.
        Assert.Contains("loc:Tr Nav_Settings", shell);
    }

    /// <summary>
    /// The locate button carries its explanation. What was missing was never size: "Locate installed
    /// WoW" says what the button DOES, not what it is for, and a player who has the files already does
    /// not read it as being about them.
    /// </summary>
    [Fact]
    public void DerFinden_Knopf_TraegtSeineErklaerung()
    {
        var shell = StripComments(ReadSource("Views/ShellV3Window.axaml"));

        var locate = Regex.Match(shell,
            "<StackPanel[^>]*Play\\.ShowLocate.*?</StackPanel>", RegexOptions.Singleline);
        Assert.True(locate.Success, "the locate block on the play surface is gone");
        Assert.Contains("Play_Locate_Hint", locate.Value);
        Assert.Contains("LocateExistingClientCommand", locate.Value);

        var settings = StripComments(ReadSource("Views/SettingsView.axaml"));
        Assert.Contains("LocateClientCommand", settings);
        Assert.Contains("Settings_LocateClient_Hint", settings);
        // Grey without a reason is a dead end, so the reason is on the page.
        Assert.Contains("Settings_LocateClient_Done", settings);
    }

    /// <summary>The armory's empty state carries the explanation and the way out, and the way out is
    /// only offered to someone who is actually signed out.</summary>
    [Fact]
    public void DasLeereArsenal_ErklaertSichUndBietetDenWegAn()
    {
        var shell = StripComments(ReadSource("Views/ShellV3Window.axaml"));

        var empty = Regex.Match(shell,
            "<StackPanel IsVisible=\"\\{Binding Armory.ShowEmpty\\}\".*?\n                    </StackPanel>",
            RegexOptions.Singleline);
        Assert.True(empty.Success, "the armory empty state is gone");
        Assert.Contains("Armory_ScopeNote", empty.Value);
        Assert.Contains("Armory_Join_Lead", empty.Value);
        Assert.Contains("GoRegisterCommand", empty.Value);
        Assert.Contains("IsVisible=\"{Binding !IsLoggedIn}\"", empty.Value);
    }

    // ── 4 · What looking at the rendered thing turned up (2026-08-05) ──────────────────────────

    /// <summary>
    /// The third tab is set in capitals like the other two. It was not: it used Settings_Title
    /// ("Settings"), and lowercase between ARMORY and ADDONS read as a different KIND of control
    /// rather than as the third tab. Avalonia has no text-transform, so the capitals are the
    /// catalog's job, and they have to be the capitals of that language: a Russian label in Latin
    /// capitals would be just as wrong as no capitals at all.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("ru")]
    public void DerDritteReiter_StehtInVersalien_WieDieAnderenBeiden(string code)
    {
        var catalog = WowLauncher.Localization.Loc.Load(code);
        Assert.NotNull(catalog);
        Assert.True(catalog!.ContainsKey("Nav_Settings"), $"{code}.json is missing Nav_Settings");

        var label = catalog["Nav_Settings"];
        Assert.Equal(label.ToUpperInvariant(), label);
        // ...and it is a translation, not a copy of the English one, except where the word matches.
        Assert.False(string.IsNullOrWhiteSpace(label));
    }

    /// <summary>
    /// The FRIENDS caption belongs to the sidebar, not to the account page. On the account page it
    /// read as a second card stacked on the first, and above the sign-up form it said "sign in to see
    /// friends" next to a Create account button. The flag sits on the CONTROL because the login view
    /// model is a process-lifetime singleton shared by both hosts, so a flag on it would flip in one
    /// place and change the other.
    /// </summary>
    [Fact]
    public void DieKontoseite_TraegtKeineFreundesUeberschrift()
    {
        var shell = StripComments(ReadSource("Views/ShellV3Window.axaml"));
        var login = StripComments(ReadSource("Views/LoginView.axaml"));

        Assert.Contains("ShowFriendsHeader=\"False\"", shell);
        Assert.Contains("IsVisible=\"{Binding ShowFriendsHeader, ElementName=Card}\"", login);
        // Default true, so the sidebar keeps its caption without saying anything.
        Assert.Contains("defaultValue: true", ReadSource("Views/LoginView.axaml.cs"));
    }

    /// <summary>The locate button stays quiet next to the primary action. Making it a filled button
    /// gave the play surface two things that looked equally important; what makes it findable is the
    /// sentence under it, not the colour.</summary>
    [Fact]
    public void DerFinden_Knopf_NimmtDerHauptsacheNichtDasGewicht()
    {
        var shell = StripComments(ReadSource("Views/ShellV3Window.axaml"));

        var locate = Regex.Match(shell,
            "<StackPanel[^>]*Play\\.ShowLocate.*?</StackPanel>", RegexOptions.Singleline);
        Assert.True(locate.Success);
        Assert.Contains("Classes=\"ghost\"", locate.Value);
    }

    /// <summary>The realm picker on the addons page says what it does, in a sentence, wherever it is
    /// shown. Owner, 2026-08-05, looking at the running launcher: "ich check das immer noch nicht".
    /// He is right - the control is labelled "Addons for" and offers two realm names, which reads as a
    /// filter over the list below it. It is not a filter. It swaps the folder the game loads. This is
    /// the same class of defect as the three found on 2026-08-04: every test was green and the screen
    /// was still unreadable, because no test can assert that a person understood something.</summary>
    [Fact]
    public void DieRealmAuswahl_ImAddonTab_ErklaertSichSelbst()
    {
        var shell = StripComments(ReadSource("Views/ShellV3Window.axaml"));

        // In ONE block with the picker, under the same visibility condition. Both halves matter and
        // both were wrong on the first attempt: the sentence sat at the left margin, roughly 800px
        // from the control it explains, and got read as a continuation of the paragraph above it.
        // MaxWidth 330 identifies the block in the addons tab; the play surface carries its own picker
        // (see DerAddonSatz_StehtAnDerSpielflaeche) and would otherwise match this pattern first.
        var block = Regex.Match(shell,
            "<StackPanel[^>]*MaxWidth=\"330\"[^>]*Addons\\.ShowProfiles.*?</StackPanel>\\s*</StackPanel>",
            RegexOptions.Singleline);
        Assert.True(block.Success, "the picker and its explanation are no longer one block");
        Assert.Contains("Addons_Profile_Hint", block.Value);
        Assert.Contains("<ComboBox", block.Value);
    }

    /// <summary>The sentence has to name the consequence, not the mechanism. "Switches the addon
    /// profile" would be true and useless; what a player needs to know is that the other set survives.
    /// Asserted on the catalogue text so a rewrite that drops the reassurance goes red.</summary>
    [Fact]
    public void DerErklaersatz_SagtDassNichtsVerlorenGeht()
    {
        var en = ReadSource("Localization/lang/en.json");
        using var doc = System.Text.Json.JsonDocument.Parse(en);
        var text = doc.RootElement.GetProperty("Addons_Profile_Hint").GetString() ?? "";

        Assert.Contains("share one game folder", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing is deleted", text, StringComparison.OrdinalIgnoreCase);
        // VOICE.md: no em dashes, no apostrophes in the English voice.
        Assert.DoesNotContain('—', text);
        Assert.DoesNotContain('\'', text);
    }

    /// <summary>
    /// The addon set is choosable where the player presses PLAY, not only inside a tab.
    ///
    /// <para>🔴 The reason it cannot simply follow the realm, which is what I proposed before reading
    /// the model: the rail carries ONE entry (Stonetavern, one address) and both game realms sit
    /// behind it — the player picks Elwynn or Barrens at the realm screen INSIDE the game, long after
    /// the launcher is gone. So there is no realm for the launcher to hook to, and this picker is the
    /// only moment the decision can be expressed at all. Then it belongs next to the action, not two
    /// tabs away.</para>
    /// </summary>
    [Fact]
    public void DerAddonSatz_StehtAnDerSpielflaeche()
    {
        var shell = StripComments(ReadSource("Views/ShellV3Window.axaml"));

        var block = Regex.Match(shell,
            "<StackPanel[^>]*Addons\\.ShowProfiles[^>]*MaxWidth=\"420\".*?</StackPanel>\\s*</StackPanel>",
            RegexOptions.Singleline);
        Assert.True(block.Success, "the addon set picker is gone from the play surface");
        Assert.Contains("<ComboBox", block.Value);
        Assert.Contains("Play_AddonSet_Hint", block.Value);
    }

    /// <summary>The play surface knows the active set WITHOUT anyone opening the addons tab. Before
    /// this, profiles were resolved only in <c>LoadAsync</c>, which also fetches the catalogue — so on
    /// a fresh start the picker beside PLAY would have been empty until the player visited a tab they
    /// have no reason to visit.</summary>
    [Fact]
    public void DerAktiveSatz_StehtOhneNetzUndOhneReiterFest()
    {
        var cfg = new MemoryConfig();
        var paths = new TempPaths();
        paths.EnsureDirectories();

        // A client folder with an AddOns directory in it, the way an installed client looks.
        var clientDir = Path.Combine(paths.Root, "client");
        Directory.CreateDirectory(Path.Combine(clientDir, "Interface", "AddOns"));
        var c = cfg.Load();
        c.ClientInstalls[5875] = clientDir;
        cfg.Save(c);

        var log = new Serilog.LoggerConfiguration().CreateLogger();
        var vm = new AddonsViewModel(new NullAddonService(), cfg, new AddonProfileService(log), log);

        vm.RefreshProfilesOnly(5875);

        // 🔴 Seit 2026-08-12 gibt es EINEN Satz (Owner-Entscheid). Der Kern dieses Tests bleibt: der
        // aktive Satz steht ohne Netz und ohne Reiterbesuch fest. Was sich dreht, ist die Erwartung an
        // die Auswahl - vorher wurden die Account-Realms zu zwei Saetzen (Elwynn, Barrens) und
        // erzeugten einen Waehler, den niemand bestellt hatte.
        Assert.Single(vm.ProfileNames);
        Assert.False(vm.ShowProfiles, "bei einem einzigen Satz gehoert kein Waehler auf die Spielflaeche");
        Assert.DoesNotContain("Elwynn", vm.ProfileNames);
        Assert.DoesNotContain("Barrens", vm.ProfileNames);
        Assert.False(string.IsNullOrEmpty(vm.ActiveProfile));
    }

    /// <summary>
    /// Signed out means no catalogue, and the gate is not the screen.
    ///
    /// <para>Owner, 2026-08-05: addons behind the login. The screen half is here; the half that
    /// actually restricts anything is the endpoint (apps/web api/launcher/addons, which now answers
    /// 401) and the service, which does not ask at all while signed out - and does not hand out
    /// yesterday's disk cache either, because a gate that only holds until the first time it was
    /// passed is not a gate.</para>
    /// </summary>
    [Fact]
    public void OhneAnmeldung_KeinKatalog_UndKeinPlattencache()
    {
        var log = new Serilog.LoggerConfiguration().CreateLogger();
        var cfg = new MemoryConfig();
        var paths = new TempPaths();
        paths.EnsureDirectories();

        // A cache file on disk, the way a previously signed-in session leaves one behind.
        File.WriteAllText(Path.Combine(paths.CacheDir, "addons-catalog.json"),
            "{\"version\":1,\"addons\":[{\"id\":\"leftover\",\"name\":\"Leftover\"}]}");

        var svc = new AddonService(new HttpClient(new RefusingHandler()), cfg, new NoDownload(),
            paths, log, new SignedOutAuth());

        var catalog = svc.GetCatalogAsync().GetAwaiter().GetResult();

        Assert.Empty(catalog.Addons);
    }

    /// <summary>A handler that fails the test rather than the request: while signed out the service
    /// must not reach the network at all, so any call here is the defect.</summary>
    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "the catalogue was fetched while signed out: " + request.RequestUri);
    }

    /// <summary>The addons page tells its three empty states apart. Showing "no addons are offered for
    /// this client yet" to a signed-out player would be the right screen with the wrong reason, and a
    /// wrong reason sends somebody looking for a problem that is not there.</summary>
    [Fact]
    public void DieAddonSeite_UnterscheidetDreiLeereZustaende()
    {
        var log = new Serilog.LoggerConfiguration().CreateLogger();
        var vm = new AddonsViewModel(new NullAddonService(), new MemoryConfig(),
            new AddonProfileService(log), log)
        {
            NeedsClient = true,
            SignedOut = true,
        };

        Assert.False(vm.ShowNeedsClient, "signed out is the reason, not the missing client");
        Assert.False(vm.ShowEmpty);

        vm.SignedOut = false;
        Assert.True(vm.ShowNeedsClient);
    }

    // ── Source access ───────────────────────────────────────────────────────────────────────────

    private static string AppDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "WowLauncher");
            if (File.Exists(Path.Combine(candidate, "Styles.v3.axaml"))) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"WowLauncher source tree not found above {AppContext.BaseDirectory}");
    }

    private static string ReadSource(string relative)
    {
        var path = Path.Combine(AppDir(), relative);
        Assert.True(File.Exists(path), $"expected source file missing: {path}");
        return File.ReadAllText(path);
    }

    private static string StripComments(string xaml) => CommentRegex().Replace(xaml, " ");

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    /// <summary>
    /// Beim Oeffnen der Einstellungen werden die lebenden Angaben neu abgelesen (Fassungsstand,
    /// Pruefalter, Startbericht).
    ///
    /// <para>Die Ansicht ist ein Singleton: ohne diesen Anstoss zeigt sie den Stand vom
    /// Programmstart, waehrend eine Update-Pruefung, ein Realmwechsel oder ein neu eingetragener
    /// Client laengst etwas anderes wahr gemacht haben. Der erste Renderlauf am 2026-08-05 zeigte
    /// genau das: "noch nie den Update-Server erreicht", obwohl die Pruefung Sekunden vorher geglueckt
    /// war und in der Zustandsdatei stand.</para>
    /// </summary>
    [Fact]
    public void OpeningSettings_RereadsTheLiveFacts()
    {
        var shell = NewShell();
        var seen = new List<string>();
        shell.Settings.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");

        shell.GoSettingsCommand.Execute(null);

        Assert.Contains(nameof(SettingsViewModel.StartReportText), seen);
    }

    // ── Doubles ─────────────────────────────────────────────────────────────────────────────────

    private static ShellViewModel NewShell()
    {
        var cfg = new MemoryConfig();
        var log = new Serilog.LoggerConfiguration().CreateLogger();
        var paths = new TempPaths();
        var play = new PlayViewModel(cfg, new NoManifest(), new NoClient(), new OfflineStatus(),
            new NoDownload(), new ClientVerifyService(log), new NoUpdate(), new NoNews(), new ExitNow(),
            paths, new FixedFolderPicker(paths.Root), log);
        var auth = new SignedOutAuth();
        return new ShellViewModel(play, new PatchNotesViewModel(new NoNews(), cfg, log),
            new SettingsViewModel(cfg, new NullFolderPicker()), new NoFriends(), auth,
            new LoginViewModel(auth), cfg, new ArmoryViewModel(new NoArmory(), log));
    }

    private sealed class MemoryConfig : IConfigService
    {
        private LauncherConfig _current = new()
        {
            ManifestUrl = "https://downloads.example.invalid/manifest.json",
            RealmlistAddress = "play.stonetavern.app",
        };
        public LauncherConfig Load() => _current;
        public void Save(LauncherConfig config) => _current = config;
        public bool LastSaveSucceeded => true;
    }

    private sealed class NoManifest : IManifestService
    {
        public Task<ServerManifest?> FetchAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(null);
        /// <summary>Kein Launcher-Manifest in diesem Double: der Selbst-Update-Pfad ist hier
        /// nicht der Prüfgegenstand, und "keins" heißt "kein Update", nie "irgendeins".</summary>
        public Task<ServerManifest?> FetchLauncherManifestAsync(CancellationToken ct = default) =>
            Task.FromResult<ServerManifest?>(null);

        public Task<ClientFileManifest?> FetchFileManifestAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<ClientFileManifest?>(null);
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

    private sealed class OfflineStatus : IServerStatusService
    {
        public Task<ServerStatusResult> CheckAsync(string host, int port = 3724, CancellationToken ct = default) =>
            Task.FromResult(new ServerStatusResult { Online = false, PlayerCount = 0 });
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

    private sealed class NoFriends : IFriendsPresenceService
    {
        public Task<IReadOnlyList<FriendPresence>> GetFriendsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FriendPresence>>([]);
        public Task<AddFriendResult> AddFriendAsync(string account, CancellationToken ct = default) =>
            Task.FromResult(AddFriendResult.Rejected("test"));
    }

    private sealed class SignedOutAuth : ILauncherAuthService
    {
        public bool IsLoggedIn => false;
        public string? CurrentToken => null;
        public string? CurrentAccount => null;
        public Task<LoginOutcome> LoginAsync(string u, string p, CancellationToken ct = default) =>
            Task.FromResult(LoginOutcome.Failure("test"));
        public Task<LoginOutcome> RegisterAsync(RegisterRequest r, CancellationToken ct = default) =>
            Task.FromResult(LoginOutcome.Failure("test"));
        public void Logout() { }
    }

    private sealed class NoArmory : IArmoryService
    {
        public Task<ArmoryRoster> GetCharactersAsync(string realmId, CancellationToken ct = default) =>
            Task.FromResult(ArmoryRoster.Empty(ArmoryStatus.SignedOut));
    }

    private sealed class TempPaths : IAppPaths
    {
        public string Root => _root;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "st-wishes-" + Guid.NewGuid().ToString("N"));
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
}
