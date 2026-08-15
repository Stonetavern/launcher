using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowLauncher.Localization;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;

namespace WowLauncher.ViewModels;

/// <summary>
/// Settings: the realm list (shipped presets plus whatever the player added), the client path and the
/// tweak toggles. Wired to the live config.
///
/// <para>A realm carries the address <em>and</em> the client build it needs, so adding one is the whole
/// job: name it, point it at an address, say which client it speaks. The launcher then writes
/// <c>realmlist.wtf</c> into exactly that client install when the realm is played. Presets can be edited
/// but not removed, so the rail can never end up empty.</para>
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IConfigService _config;
    private readonly IFolderPickerService _folderPicker;
    private readonly IDesktopIntegrationService _desktop;

    /// <summary>Raised after a realm was added, removed or repointed, so the v3 rail can rebuild.</summary>
    public event Action? RealmsChanged;

    /// <param name="desktopIntegration">Opt-in menu/taskbar integration. Optional so the existing test
    /// constructions keep compiling; when null it falls back to the per-OS default (an inert stub off
    /// Linux), and DI injects the real registration in the app.</param>
    public SettingsViewModel(IConfigService config, IFolderPickerService folderPicker,
        IDesktopIntegrationService? desktopIntegration = null,
        StartReport? startReport = null, IClipboardService? clipboard = null,
        IUpdateCheckLog? checkLog = null, Func<string>? runningVersion = null,
        Func<DateTimeOffset>? now = null)
    {
        _checkLog = checkLog;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _runningVersion = runningVersion ?? (() =>
        {
            var v = UpdateService.RunningVersion(System.Reflection.Assembly.GetExecutingAssembly());
            return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        });
        _config = config;
        _folderPicker = folderPicker;
        _desktop = desktopIntegration ?? DesktopIntegration.ForCurrentOs();
        _startReport = startReport;
        _clipboard = clipboard;
        var c = _config.Load();
        _wowExecutablePath = c.WowExecutablePath;
        _profileSyncEnabled = c.ProfileSyncEnabled;
        _preferredInstallRoot = c.PreferredInstallRoot ?? "";
        _linuxRuntime = LinuxRuntimeSelection.Parse(c.LinuxRuntime);
        _linuxRuntimeCustomPath = c.LinuxRuntimeCustomPath ?? "";

        Realms = new ObservableCollection<RealmEntry>(RealmRegistry.All(c));
        _selectedRealm = Realms.FirstOrDefault(r => r.Id == c.SelectedRealmId) ?? Realms[0];
        _newRealmClient = ClientVersion.Default;

        // Reflect a prior install: if the entry already points at this launch target, start in the
        // "Added" state so the button reads as done rather than inviting a pointless re-run.
        _menuIntegrationDone = _desktop.IsSupported && _desktop.IsInstalled();
    }

    // ─── Desktop integration (opt-in, Linux only) ─────────────────────────
    /// <summary>Whether to show the "Add to menu" control at all. False off Linux, so the whole block
    /// is hidden rather than showing a button that would no-op.</summary>
    public bool ShowDesktopIntegration => _desktop.IsSupported;

    /// <summary>True once the menu entry is in place (either found at startup or written this session).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddToMenuLabel))]
    [NotifyCanExecuteChangedFor(nameof(AddToMenuCommand))]
    private bool _menuIntegrationDone;

    /// <summary>Button label flips to the done state after a successful install.</summary>
    public string AddToMenuLabel =>
        MenuIntegrationDone ? Loc.T("Settings_AddToMenu_Done") : Loc.T("Settings_AddToMenu");

    /// <summary>Quiet one-liner shown only when an install attempt failed. No modal, no crash.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDesktopIntegrationError))]
    private string? _desktopIntegrationError;

    public bool HasDesktopIntegrationError => !string.IsNullOrEmpty(DesktopIntegrationError);

    private bool CanAddToMenu => _desktop.IsSupported && !MenuIntegrationDone;

    /// <summary>Install the menu entry + taskbar icon. Async so the best-effort cache refresh never
    /// blocks the UI thread; the generated AsyncRelayCommand also serializes re-entry.</summary>
    [RelayCommand(CanExecute = nameof(CanAddToMenu))]
    private async Task AddToMenu()
    {
        DesktopIntegrationError = null;
        var result = await _desktop.InstallAsync();
        if (result.Ok)
            MenuIntegrationDone = true;
        else
            DesktopIntegrationError = Loc.T("Settings_AddToMenu_Failed");
    }

    // ─── Sprache der Oberfläche ───────────────────────────────────────────
    /// <summary>Die Auswahl: „der Spielsprache folgen" plus jede ausgelieferte Übersetzung.
    /// Die Namen stehen in ihrer eigenen Sprache, weil die Spieler, die das brauchen, die aktuelle
    /// nicht lesen können.</summary>
    public IReadOnlyList<LauncherLanguageChoice> LauncherLanguages { get; } =
        LauncherLanguageChoice.All;

    /// <summary>Leer = der Spielsprache folgen. Das bleibt die Voreinstellung: wer das Spiel auf
    /// Deutsch stellt, will den Launcher selten auf Englisch. Eine ausdrückliche Wahl überschreibt
    /// das dann dauerhaft, auch wenn die Client-Sprache später wechselt.</summary>
    public LauncherLanguageChoice SelectedLauncherLanguage
    {
        get
        {
            var saved = _config.Load().LauncherLanguage ?? "";
            return LauncherLanguages.FirstOrDefault(l => l.Code == saved) ?? LauncherLanguages[0];
        }
        set
        {
            if (value is null) return;
            var cfg = _config.Load();
            if ((cfg.LauncherLanguage ?? "") == value.Code) return;
            cfg.LauncherLanguage = value.Code;
            _config.Save(cfg);
            RefreshSaveWarning();

            // Sofort anwenden. Eine Sprache, die erst nach einem Neustart greift, sieht aus wie eine
            // Einstellung, die nicht funktioniert.
            Loc.Use(string.IsNullOrWhiteSpace(value.Code)
                ? Loc.ForClientLocale.GetValueOrDefault(cfg.Locale, "en")
                : value.Code);
            OnPropertyChanged();
        }
    }

    public string Title => Loc.T("Settings_Title");

    // ─── Realms ───────────────────────────────────────────────────────────
    public ObservableCollection<RealmEntry> Realms { get; }

    /// <summary>Client builds a realm can be bound to. Grouped by era in the UI, but always labelled
    /// with the exact version and build: "Vanilla" alone is ambiguous now that it means 1.12.1 or 1.14.2.</summary>
    public IReadOnlyList<ClientVersion> ClientVersions => ClientVersion.All;

    /// <summary>Builds a NEW realm can actually be bound to. TBC/Wrath are real vocabulary
    /// (<see cref="ClientVersion.IsAvailable"/> exists so the rest of the app can already talk about
    /// them) but there is no shippable client for either yet - offering them here would let a player
    /// create a realm that can never start (owner decision 2026-07-21, the v3 add-realm dialog).</summary>
    public IReadOnlyList<ClientVersion> AddableClientVersions =>
        ClientVersion.All.Where(c => c.IsAvailable && !c.IsExcludedOnCurrentOs).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveSelected), nameof(SelectedRealmAddress),
        nameof(SelectedRealmClient), nameof(SelectedRealmClientLine),
        nameof(ShowRealmBothClients), nameof(RealmOffersBothClients))]
    private RealmEntry _selectedRealm;

    /// <summary>Presets stay; only realms the player added can go.</summary>
    public bool CanRemoveSelected => SelectedRealm is { IsPreset: false };

    public string SelectedRealmAddress => SelectedRealm?.RealmlistAddress ?? "";
    public string SelectedRealmClientLine => SelectedRealm?.ClientLine ?? "";

    /// <summary>Which client the selected realm is bound to. Setting it re-binds the realm, which is
    /// how a downloaded client gets assigned to a realm.</summary>
    public ClientVersion SelectedRealmClient
    {
        get => SelectedRealm?.Client ?? ClientVersion.Default;
        set
        {
            if (SelectedRealm is null || value is null || SelectedRealm.ClientKey == value.Key) return;
            SelectedRealm.ClientKey = value.Key;
            PersistRealms();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedRealmClientLine));
            RealmsChanged?.Invoke();
        }
    }

    // ── Ein eigener Realm mit BEIDEN Clients (Owner-Befund 2026-08-05) ─────────────────────────
    //
    // Der Owner hat `localhost` angelegt, ihm 1.12.1 gegeben - und danach denselben Realm ein
    // ZWEITES Mal angelegt, nur um 1.14.2 zu bekommen. Zwei Eintraege in der Leiste fuer eine
    // Adresse, und keiner der beiden weiss vom anderen.
    //
    // Der Grund war eine Datenentscheidung, keine Absicht: `ClientKeys` (welche Builds ein Realm
    // anbietet) galt als Stammdaten der ausgelieferten Presets und wurde beim Anlegen nie gesetzt.
    // Stonetavern trug beide, ein selbst angelegter Realm konnte es nicht - und die Umschaltleiste
    // ueber dem Spielen-Knopf erschien deshalb dort nie.
    //
    // Warum EIN Schalter und nicht eine Liste von Haken: es gibt heute zwei auslieferbare Clients.
    // Eine Mehrfachauswahl fuer zwei Dinge ist ein Menue fuer eine Ja-Nein-Frage. Sollte je ein
    // dritter dazukommen, greift derselbe Schalter weiter - er schreibt ALLE auslieferbaren Keys,
    // nicht zwei fest verdrahtete.

    /// <summary>Nur dort sichtbar, wo die Frage sich stellt: ein selbst angelegter Realm, und mehr
    /// als ein auslieferbarer Client. Presets tragen ihre Builds als Stammdaten und werden hier
    /// nicht angefasst.</summary>
    public bool ShowRealmBothClients =>
        SelectedRealm is { IsPreset: false } && AddableClientVersions.Count > 1;

    /// <summary>Ob dieser Realm alle auslieferbaren Clients anbietet. Aus, wenn er nur den einen
    /// trägt, auf den er gebunden ist.</summary>
    public bool RealmOffersBothClients
    {
        get => SelectedRealm?.ClientKeys is { Count: > 1 };
        set
        {
            if (SelectedRealm is null || SelectedRealm.IsPreset) return;
            if (value == RealmOffersBothClients) return;

            SelectedRealm.ClientKeys = value
                ? AddableClientVersions.Select(c => c.Key).ToList()
                : null;

            // Der gebundene Client bleibt, was er war - er bestimmt weiterhin, womit gestartet wird.
            // Ihn hier mitzuaendern hiesse, dass ein Haken die Spielflaeche umstellt, ohne dass
            // jemand das verlangt hat.
            PersistRealms();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedRealmClientLine));
            RealmsChanged?.Invoke();
        }
    }

    partial void OnSelectedRealmChanged(RealmEntry value)
    {
        if (value is null) return;
        var c = _config.Load();
        c.SelectedRealmId = value.Id;
        _config.Save(c);
        RefreshSaveWarning();
        RealmsChanged?.Invoke();
    }

    // ─── Add-realm form ───────────────────────────────────────────────────
    [ObservableProperty] private string _newRealmName = "";
    [ObservableProperty] private string _newRealmAddress = "";
    [ObservableProperty] private string _newRealmManifestUrl = "";
    [ObservableProperty] private ClientVersion _newRealmClient;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddRealmError))]
    private string? _addRealmError;

    public bool HasAddRealmError => !string.IsNullOrEmpty(AddRealmError);

    /// <summary>Add a realm. Name + address are required; the manifest URL is optional (without one the
    /// launcher only points an existing install at the realm instead of managing downloads).
    ///
    /// <para>The address is validated HERE, where it is typed. It ends up verbatim in realmlist.wtf and
    /// Config.wtf, so a value carrying a line break would append arbitrary directives to files the
    /// client reads as configuration. ClientService validates again at the write, because the same
    /// field also arrives from the server manifest and from a hand-edited config.</para></summary>
    /// <summary>Unix milliseconds. One place, so add and delete are stamped the same way.</summary>
    private static long Now() => System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [RelayCommand]
    private void AddRealm()
    {
        AddRealmError = null;
        if (string.IsNullOrWhiteSpace(NewRealmName) || string.IsNullOrWhiteSpace(NewRealmAddress))
            return;

        if (!ClientService.IsValidRealmlistAddress(NewRealmAddress.Trim()))
        {
            AddRealmError = Loc.T("Settings_Error_BadAddress");
            return;
        }

        // The manifest URL is optional, but an unusable one is worse than none: it is handed straight to
        // HttpClient, a value without a scheme throws there, and the launcher then falls back to simple
        // mode without saying so — the player sees a realm that can never manage a client and no reason
        // why. Reject it here, where the player can still fix the typo.
        if (!string.IsNullOrWhiteSpace(NewRealmManifestUrl)
            && !ManifestService.IsValidManifestUrl(NewRealmManifestUrl))
        {
            AddRealmError = Loc.T("Settings_Error_BadManifestUrl");
            return;
        }

        var c = _config.Load();
        var entry = new RealmEntry
        {
            Id = RealmRegistry.NewId(NewRealmName, RealmRegistry.All(c)),
            Name = NewRealmName.Trim(),
            RealmlistAddress = NewRealmAddress.Trim(),
            ClientKey = (NewRealmClient ?? ClientVersion.Default).Key,
            ManifestUrl = string.IsNullOrWhiteSpace(NewRealmManifestUrl) ? null : NewRealmManifestUrl.Trim(),
            IsPreset = false,
            IsLive = true,
            AddedAt = Now(),
        };

        c.Realms.Add(entry);
        // Der lokale Grabstein faellt weg; gegen einen aelteren Grabstein auf dem Server gewinnt der
        // Realm ueber AddedAt. Ohne den Zeitpunkt waere ein einmal geloeschter Name fuer immer
        // verbrannt (Codex-Befund, dritte Runde).
        c.DeletedRealms.RemoveAll(g => string.Equals(g.Id, entry.Id, System.StringComparison.OrdinalIgnoreCase));
        c.SelectedRealmId = entry.Id;
        _config.Save(c);
        RefreshSaveWarning();

        Realms.Add(entry);
        SelectedRealm = entry;

        NewRealmName = "";
        NewRealmAddress = "";
        NewRealmManifestUrl = "";
        NewRealmClient = ClientVersion.Default;

        RealmsChanged?.Invoke();
    }

    [RelayCommand]
    private void RemoveSelectedRealm()
    {
        var target = SelectedRealm;
        if (target is null || target.IsPreset) return;

        var c = _config.Load();
        c.Realms.RemoveAll(r => r.Id == target.Id);
        // Grabstein MIT Zeitpunkt setzen, sonst holt der naechste Sync den Realm zurueck - und ohne
        // die Zeit koennte ein spaeteres Neu-Anlegen ihn nie wieder gewinnen.
        c.DeletedRealms.RemoveAll(g => string.Equals(g.Id, target.Id, System.StringComparison.OrdinalIgnoreCase));
        c.DeletedRealms.Add(new ProfileGrave { Id = target.Id, DeletedAt = Now() });
        if (c.SelectedRealmId == target.Id) c.SelectedRealmId = RealmRegistry.ElwynnId;
        _config.Save(c);

        Realms.Remove(target);
        SelectedRealm = Realms.FirstOrDefault(r => r.Id == c.SelectedRealmId) ?? Realms[0];
        RealmsChanged?.Invoke();
    }

    /// <summary>Write the in-memory realm list back. Presets are only persisted once edited, so a fresh
    /// install keeps an empty list and picks up preset changes from future launcher versions.</summary>
    private void PersistRealms()
    {
        var c = _config.Load();
        foreach (var realm in Realms)
        {
            var idx = c.Realms.FindIndex(r => r.Id == realm.Id);
            if (idx >= 0) c.Realms[idx] = realm;
            else if (!IsUnchangedPreset(realm)) c.Realms.Add(realm);
        }
        _config.Save(c);
        RefreshSaveWarning();
    }

    private static bool IsUnchangedPreset(RealmEntry realm)
    {
        var shipped = RealmRegistry.Presets().FirstOrDefault(p => p.Id == realm.Id);
        return shipped is not null
            && shipped.RealmlistAddress == realm.RealmlistAddress
            && shipped.ClientKey == realm.ClientKey
            && shipped.ManifestUrl == realm.ManifestUrl;
    }

    // ─── Client + tweaks ──────────────────────────────────────────────────
    [ObservableProperty] private string _wowExecutablePath;

    partial void OnWowExecutablePathChanged(string value) => Persist(c => c.WowExecutablePath = value);

    /// <summary>
    /// Carry the player's own realms with their account. OFF unless they say yes: the realm names and
    /// addresses they added leave their machine, and some of those addresses belong to servers that are
    /// not ours. That is a decision, not a default.
    /// </summary>
    [ObservableProperty] private bool _profileSyncEnabled;

    partial void OnProfileSyncEnabledChanged(bool value) => Persist(c => c.ProfileSyncEnabled = value);

    /// <summary>Empty = the launcher decides (<see cref="LauncherConfig.PreferredInstallRoot"/>). Also
    /// settable by hand, e.g. to point at an existing install the player wants reused going forward.</summary>
    [ObservableProperty] private string _preferredInstallRoot;

    partial void OnPreferredInstallRootChanged(string value) =>
        Persist(c => c.PreferredInstallRoot = string.IsNullOrWhiteSpace(value) ? null : value.Trim());

    // ─── Linux: womit gestartet wird (Owner-Wunsch 2026-08-04, wiederholt 2026-08-05) ────
    //
    // Die Maschinerie gab es laengst - wine-ge finden, auf System-Wine zurueckfallen -, nur traf sie
    // die Entscheidung still. Wessen Spiel falsch startete, konnte weder sehen, womit gestartet wurde,
    // noch etwas daran aendern. Die Einstellung ist die eine Haelfte; die andere ist die Zeile
    // darunter, die sagt, was TATSAECHLICH benutzt wird - auch und gerade dann, wenn es nicht das
    // Gewuenschte ist.

    /// <summary>Nur unter Linux sichtbar. Auf Windows startet der Client nativ, auf macOS loest GPTK
    /// seine eigene Wine auf - eine Einstellung, die dort zu wirken scheint, waere genau die Sorte
    /// stiller Luege, gegen die sie gebaut ist.</summary>
    public bool ShowLinuxRuntime => OperatingSystem.IsLinux();

    /// <summary>One menu entry. The enum alone would render as "SystemWine" in the list - the name a
    /// developer reads, not one a player does.</summary>
    public sealed record RuntimeOption(LinuxRuntimeKind Kind, string DisplayName);

    public IReadOnlyList<RuntimeOption> LinuxRuntimeOptions { get; } =
    [
        new(LinuxRuntimeKind.Auto, Loc.T("Settings_Runtime_Auto")),
        new(LinuxRuntimeKind.SystemWine, Loc.T("Settings_Runtime_System")),
        new(LinuxRuntimeKind.WineGe, Loc.T("Settings_Runtime_WineGe")),
        new(LinuxRuntimeKind.Custom, Loc.T("Settings_Runtime_Custom")),
    ];

    /// <summary>Bound to the menu. Setting it writes the config through <see cref="LinuxRuntime"/>, so
    /// there is one place that persists and one place that decides.</summary>
    public RuntimeOption? SelectedLinuxRuntimeOption
    {
        get => LinuxRuntimeOptions.FirstOrDefault(o => o.Kind == LinuxRuntime);
        set { if (value is not null) LinuxRuntime = value.Kind; }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLinuxRuntimeCustomPath), nameof(LinuxRuntimeStatus),
        nameof(SelectedLinuxRuntimeOption))]
    private LinuxRuntimeKind _linuxRuntime;

    partial void OnLinuxRuntimeChanged(LinuxRuntimeKind value) =>
        Persist(c => c.LinuxRuntime = LinuxRuntimeSelection.ToConfigValue(value));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinuxRuntimeStatus))]
    private string _linuxRuntimeCustomPath;

    partial void OnLinuxRuntimeCustomPathChanged(string value) =>
        Persist(c => c.LinuxRuntimeCustomPath = (value ?? "").Trim());

    public bool ShowLinuxRuntimeCustomPath => LinuxRuntime == LinuxRuntimeKind.Custom;

    /// <summary>
    /// Was gerade wirklich benutzt wird — für BEIDE Clients, weil „Automatisch" für sie nicht
    /// dasselbe heißt.
    ///
    /// <para>🔴 Bis 2026-08-05 beschrieb diese Zeile immer nur 1.12.1. Wer 1.14.2 spielen wollte, las
    /// eine Aussage über einen anderen Client und hielt sie für seine — plausibel und falsch. Genau
    /// die Fehlerform, gegen die diese Zeile gebaut wurde (Zweitinstanz-Befund).</para>
    ///
    /// <para>Wird bei jeder Änderung neu aufgelöst, damit sie nicht eine Einstellung liest und ein
    /// anderes Ergebnis anzeigt.</para>
    /// </summary>
    public string LinuxRuntimeStatus
    {
        get
        {
            if (!ShowLinuxRuntime) return "";
            var stored = LinuxRuntimeSelection.ToConfigValue(LinuxRuntime);
            var legacy = LinuxRuntimeSelection.ForCurrentUser(stored, LinuxRuntimeCustomPath,
                autoPrefersWineGe: false);
            var modern = LinuxRuntimeSelection.ForCurrentUser(stored, LinuxRuntimeCustomPath,
                autoPrefersWineGe: true);

            // Beide Clients auf derselben Binärdatei: ein Satz genügt, zwei identische Zeilen wären
            // Lärm.
            if (legacy.Path == modern.Path && legacy.Reason == modern.Reason)
                return Describe(legacy);

            return Loc.F("Settings_Runtime_PerClient",
                ClientVersion.ByKey("1.12.1").ShortLabel, Describe(legacy),
                ClientVersion.ByKey("1.14.2").ShortLabel, Describe(modern));
        }
    }

    private static string Describe(LinuxRuntimeDecision d) => d.Reason switch
    {
        LinuxRuntimeReason.NothingFound => Loc.T("Settings_Runtime_None"),
        LinuxRuntimeReason.WineGeMissing => Loc.F("Settings_Runtime_WineGeMissing", d.Path),
        LinuxRuntimeReason.CustomMissing => Loc.F("Settings_Runtime_CustomMissing", d.Path),
        LinuxRuntimeReason.AutoPickedWineGe => Loc.F("Settings_Runtime_AutoWineGe", d.Path),
        LinuxRuntimeReason.AutoPickedSystemWine => Loc.F("Settings_Runtime_AutoSystem", d.Path),
        _ => Loc.F("Settings_Runtime_InUse", d.Path),
    };

    // ─── "Ich habe das Spiel schon" (Owner-Wunsch 2026-08-04, Punkt 6) ────
    // Die Funktion gab es laengst, aber nur als gedaempfte Zeile unter dem Spielen-Knopf, und die
    // steht dort nur, solange KEIN Client eingetragen ist. Wer sie einmal uebersehen hat, laedt
    // 5 GB neu, die schon auf der Platte liegen. Deshalb steht sie zusaetzlich hier, wo ein Spieler
    // nach Ordnern sucht.
    //
    // Warum durchgereicht statt nachgebaut: die Erkennung ist NICHT "such eine WoW.exe". Sie prueft
    // erst den Namen gegen den aktiven Build (WowClassic.exe kann nie 5875 sein), dann die Hashes
    // gegen das Datei-Manifest, und traegt das Ergebnis mit Version ein. Das ein zweites Mal zu
    // schreiben hiesse, zwei Wahrheiten ueber denselben Ordner zu haben. Die Shell haengt nach dem
    // Bauen den echten Befehl aus dem Play-Modell ein.
    /// <summary>The play surface's locate command, injected by the shell. Null in tests and in any
    /// host that has no play surface, which is why the whole block hides itself.</summary>
    public System.Windows.Input.ICommand? LocateClientCommand
    {
        get => _locateClientCommand;
        set
        {
            if (ReferenceEquals(_locateClientCommand, value)) return;
            if (_locateClientCommand is not null)
                _locateClientCommand.CanExecuteChanged -= OnLocateCanExecuteChanged;
            _locateClientCommand = value;
            if (_locateClientCommand is not null)
                _locateClientCommand.CanExecuteChanged += OnLocateCanExecuteChanged;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowLocateClient));
            OnPropertyChanged(nameof(CanLocateClient));
        }
    }

    private System.Windows.Input.ICommand? _locateClientCommand;

    private void OnLocateCanExecuteChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CanLocateClient));
        OnPropertyChanged(nameof(ShowLocateClient));
    }

    /// <summary>Whether the block appears at all. Hidden when no command was injected, because a
    /// button that cannot do anything is worse than no button.</summary>
    public bool ShowLocateClient => LocateClientCommand is not null;

    /// <summary>False once a client is registered for the active realm. The button then greys out and
    /// the line underneath says why, instead of opening a picker whose result would be discarded.</summary>
    public bool CanLocateClient => LocateClientCommand?.CanExecute(null) ?? false;

    /// <summary>Opens the same native folder picker the first-install download flow uses.
    /// A cancel leaves the field exactly as it was.</summary>
    [RelayCommand]
    private async Task BrowseInstallFolder()
    {
        var picked = await _folderPicker.PickFolderAsync(Loc.T("Play_Picker_Title"), PreferredInstallRoot);
        if (picked is not null)
            PreferredInstallRoot = picked;
    }

    // ─── Startbericht zum Kopieren ────────────────────────────────────────
    // Der haeufigste Bericht ist "ich bin eingeloggt, komme aber nicht in die Welt", und er kommt als
    // Bildschirmfoto eines Fensters, auf dem keine der sechs Angaben steht, die ihn beantworten:
    // Clientbuild, Realm-Adresse, Addon-Satz, benutzter Runner, ob der Proxy im Spiel ist, wo die
    // Konfiguration liegt. Der Launcher kennt alles davon und sonst niemand.
    //
    // Bewusst NICHT der Problembericht daneben: der schickt ein Protokoll an uns und braucht Netz,
    // Postfach und Konto. Dieser verlaesst die Maschine nie - der Spieler fuegt ihn dort ein, wo er
    // ohnehin schon fragt.

    private readonly StartReport? _startReport;
    private readonly IClipboardService? _clipboard;

    // ─── Welche Fassung, und wann das zuletzt jemand erfahren hat ──────────
    // "Warum bekomme ich den Fix nicht" hat drei moegliche Antworten, und der Launcher zeigte keine
    // davon: der Spieler ist alt, der Server bietet Altes an, oder seit Wochen hat niemand fragen
    // koennen. Sie verlangen drei verschiedene Handlungen (herunterladen, auf uns warten, ins Netz
    // schauen) und sehen von aussen gleich aus.
    //
    // Am 2026-08-05 war live 1.6.4, gebaut 1.7.4 und die Download-Seite bot 1.5.0 an - drei
    // Antworten auf eine Frage, und kein Spieler konnte eine davon sehen.

    private readonly IUpdateCheckLog? _checkLog;
    private readonly Func<string> _runningVersion;
    private readonly Func<DateTimeOffset> _now;

    public bool ShowReleaseStatus => _checkLog is not null;

    /// <summary>
    /// Die Angaben neu ablesen, die sich waehrend der Sitzung aendern koennen: Fassungsstand,
    /// Pruefalter, Startbericht. Die Ansicht ist ein Singleton, und ohne diesen Anstoss zeigt sie den
    /// Stand vom Programmstart - waehrend eine Update-Pruefung, ein Realmwechsel oder ein neu
    /// eingetragener Client laengst etwas anderes wahr gemacht haben. Wird beim Oeffnen der
    /// Einstellungen gerufen (siehe <see cref="ShellViewModel"/>).
    /// </summary>
    public void RefreshLiveFacts()
    {
        OnPropertyChanged(nameof(ReleaseStatusLine));
        OnPropertyChanged(nameof(ReleaseCheckLine));
        OnPropertyChanged(nameof(StartReportText));
    }

    private ReleaseStatusFacts Release() => ReleaseStatus.Evaluate(
        _runningVersion(), _checkLog?.LastOffered, _checkLog?.LastSuccess, _now());

    /// <summary>Die Hauptaussage: habe ich, was angeboten wird.</summary>
    public string ReleaseStatusLine
    {
        get
        {
            var f = Release();
            if (f.NeverChecked) return Loc.F("Settings_Release_Never", f.Running);
            if (f.OfferUnknown) return Loc.F("Settings_Release_NoOffer", f.Running);
            if (f.Behind) return Loc.F("Settings_Release_Behind", f.Running, f.Offered, UpdateService.DownloadPage);
            if (f.Ahead) return Loc.F("Settings_Release_Ahead", f.Running, f.Offered);
            return Loc.F("Settings_Release_UpToDate", f.Running);
        }
    }

    /// <summary>Wie alt die Auskunft darueber ist. Getrennt von der Aussage, weil eine drei Wochen
    /// alte Pruefung die Zahlen darueber nicht falsch macht, aber unglaubwuerdig.</summary>
    public string ReleaseCheckLine
    {
        get
        {
            var f = Release();
            if (f.AgeDays is not int days) return Loc.T("Settings_Release_CheckedNever");
            if (ReleaseStatus.IsStale(f)) return Loc.F("Settings_Release_CheckedStale", days);
            return days == 0
                ? Loc.T("Settings_Release_CheckedToday")
                : Loc.F("Settings_Release_CheckedDays", days);
        }
    }

    /// <summary>Der Block erscheint nur, wenn er auch etwas kann. Ein Knopf ohne Dienst dahinter waere
    /// genau die Sorte Anzeige, die etwas verspricht und nichts tut.</summary>
    public bool ShowStartReport => _startReport is not null;

    /// <summary>Was kopiert wird, sichtbar bevor es kopiert wird. Wer etwas weitergeben soll, darf
    /// vorher hineinsehen.</summary>
    public string StartReportText =>
        _startReport is null ? "" : StartReport.Format(_startReport.Build());

    /// <summary>Leer, bis kopiert wurde; danach die Bestaetigung oder der Grund, warum nicht. Eine
    /// Zwischenablage kann sich weigern (Wayland, ein Compositor ohne das Protokoll), und ein Knopf,
    /// der dann trotzdem "Kopiert" sagt, schickt jemanden dazu, nichts einzufuegen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartReportNote))]
    private string _startReportNote = "";

    public bool HasStartReportNote => StartReportNote.Length > 0;

    [RelayCommand]
    private async Task CopyStartReport()
    {
        if (_startReport is null) return;

        // Frisch gebaut statt aus der Anzeige genommen: zwischen Oeffnen der Einstellungen und dem
        // Druck auf den Knopf kann der Spieler den Realm gewechselt oder einen Client eingetragen
        // haben, und kopiert wird der Stand von jetzt.
        var text = StartReport.Format(_startReport.Build());
        OnPropertyChanged(nameof(StartReportText));

        var ok = _clipboard is not null && await _clipboard.SetTextAsync(text);
        StartReportNote = ok ? Loc.T("Settings_StartReport_Copied") : Loc.T("Settings_StartReport_Failed");
    }

    private void Persist(Action<LauncherConfig> mutate)
    {
        var c = _config.Load();
        mutate(c);
        _config.Save(c);
        RefreshSaveWarning();
    }

    /// <summary>
    /// Shown when the launcher could not write its config. Without this the UI reports every setting as
    /// applied while nothing reaches disk (an install under Program Files, restrictive ACLs, a full
    /// disk), and the loss only becomes visible after a restart.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveWarning))]
    private string? _saveWarning;

    public bool HasSaveWarning => !string.IsNullOrEmpty(SaveWarning);

    private void RefreshSaveWarning() =>
        SaveWarning = _config.LastSaveSucceeded ? null : Loc.T("Settings_Error_SaveFailed");
}
