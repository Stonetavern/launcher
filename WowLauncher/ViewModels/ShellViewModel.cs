using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Localization;

namespace WowLauncher.ViewModels;

/// <summary>
/// The shell: TitleBar + NavRail (3 sections) + the active section view, with the global
/// ActionBar bound to <see cref="Play"/> (DESIGN-UI-ENTERPRISE §2/§4). SelectedSection-driven
/// navigation, no tab strip — the left rail <em>is</em> the navigation.
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    public PlayViewModel Play { get; }
    public PatchNotesViewModel PatchNotes { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>v3 armory section: the characters of the signed-in account on the selected realm.</summary>
    public ArmoryViewModel Armory { get; }

    /// <summary>Addons section: what the realm offers for the selected client build, and what of it is
    /// already in that client's <c>Interface/AddOns</c>.</summary>
    public AddonsViewModel Addons { get; }

    /// <summary>Feeds the v2 skin's telemetry column. Unused by v1 — additive, nothing to break.</summary>
    public TelemetryViewModel Telemetry { get; }

    // v3-only: friends/presence source (DI supplies the real HTTP impl, or the Mock under --demo) plus
    // the auth service that gates it and the sign-in form. v1/v2 never touch the members below, so
    // wiring it in is additive.
    private readonly IFriendsPresenceService _friends;
    private readonly ILauncherAuthService _auth;
    private readonly IConfigService _config;

    /// <summary>The sign-in form shown in the friends sidebar while signed out.</summary>
    public LoginViewModel Login { get; }

    private readonly IProfileSyncService? _profileSync;

    /// <summary>
    /// Reconcile the player's own realms with their account. Never awaited by a caller, so nothing may
    /// escape into TaskScheduler.UnobservedTaskException - same contract as the friends loader.
    ///
    /// <para>A sign-out is the one outcome that must be visible: the token lives 24 hours and is not
    /// renewed, and a sync that quietly stops working is worse than one that never ran.</para>
    /// </summary>
    internal async Task SafeSyncProfileAsync()
    {
        if (_profileSync is null) return;
        try
        {
            var outcome = await _profileSync.SyncAsync().ConfigureAwait(true);
            if (outcome == ProfileSyncOutcome.SignedOut && IsLoggedIn) SignOut();
            if (outcome == ProfileSyncOutcome.Synced) ReloadRealms();
        }
        catch (Exception) { /* the local config is untouched; nothing to tell the player */ }
    }

    /// <summary>Diagnostics for the report dialog. Optional for the same reason addons are: the rail
    /// and armory tests build this view model directly and have no business wiring a report sender.
    /// The dialog is only reachable from the shell window, which production always builds fully.</summary>
    public Services.ProblemReport Report { get; }

    /// <summary>Where a report goes. See <see cref="Report"/> for why it is optional.</summary>
    public Services.IProblemReportSender ReportSender { get; }

    public ShellViewModel(PlayViewModel play, PatchNotesViewModel patchNotes, SettingsViewModel settings,
        IFriendsPresenceService friends, ILauncherAuthService auth, LoginViewModel login,
        IConfigService config, ArmoryViewModel armory, AddonsViewModel? addons = null,
        IProfileSyncService? profileSync = null,
        Services.ProblemReport? report = null, Services.IProblemReportSender? reportSender = null)
    {
        Report = report ?? new Services.ProblemReport(Services.Platform.AppPaths.ForCurrentOs(), config);
        ReportSender = reportSender ?? new NullProblemReportSender();
        Play = play;
        PatchNotes = patchNotes;
        Settings = settings;
        Armory = armory;
        // Optional for the same reason profileSync is: the rail/armory tests build this VM directly and
        // have no business wiring an addon catalog. Production always passes one.
        Addons = addons ?? new AddonsViewModel(
            new NullAddonService(), config, new AddonProfileService(Serilog.Log.Logger), Serilog.Log.Logger);
        _friends = friends;
        _auth = auth;
        // Optional on purpose: the tests build this VM directly and a realm-rail test has no business
        // caring about profile sync. Null simply means "no sync in this instance".
        _profileSync = profileSync;
        _config = config;
        Login = login;
        Telemetry = new TelemetryViewModel(play, Ui.Demo);
        _current = play;

        // Seed the rail straight from config. Assigning through the generated property would fire
        // OnSelectedRealmChanged and kick off a realm switch before the app is even up, so the backing
        // field is set directly and the art loaded by hand.
        var cfg = _config.Load();
        foreach (var r in RealmRegistry.All(cfg)) Realms.Add(r);
        _selectedRealm = Realms.FirstOrDefault(r => r.Id == cfg.SelectedRealmId) ?? Realms[0];
        _lockedRealm = _selectedRealm;   // the realm the play surface is pointed at right now
        _realmHeroArt = LoadRealmArt(_selectedRealm);

        // The settings surface owns adding and removing realms; the rail mirrors whatever it did.
        Settings.RealmsChanged += ReloadRealms;
        // Push after the player edits their realms. Fire and forget with its own guard: a failing sync
        // must never make adding a realm feel broken - the realm is already saved locally either way.
        Settings.RealmsChanged += () => _ = SafeSyncProfileAsync();
        // Any successful add/remove closes the v3 overlay, whichever surface it came from.
        Settings.RealmsChanged += () => IsAddRealmOpen = false;

        // Reflect the restored session (a returning player is already signed in) and react to a fresh
        // sign-in. Both this VM and the login VM are process-lifetime singletons, so the subscription
        // needs no teardown.
        _isLoggedIn = _auth.IsLoggedIn;
        Login.SignedIn += OnSignedIn;

        // "Ich habe das Spiel schon" gehoert auch in die Einstellungen, nicht nur unter den
        // Spielen-Knopf. Der Befehl wird durchgereicht statt nachgebaut, damit es EINE Erkennung
        // gibt (Name gegen Build, dann Hashes gegen das Datei-Manifest) und nicht zwei, die
        // irgendwann verschieden urteilen. Nur die Shell kennt beide Seiten, also verdrahtet sie es.
        Settings.LocateClientCommand = Play.LocateExistingClientCommand;

        // Welcher Addon-Satz aktiv ist, muss die Spielflaeche wissen, bevor jemand den Addon-Reiter
        // oeffnet - dort steht seit 2026-08-05 die Auswahl, weil dort gespielt wird. Kein Netz, nur
        // Config und zwei Verzeichnisse, also unbedenklich auf dem Startweg.
        Addons.RefreshProfilesOnly(Play.SelectedClientChoice.Client.Build);
        // Der Addon-Katalog haengt seit 2026-08-05 am Login (Owner). Die Seite muss die DREI leeren
        // Zustaende auseinanderhalten koennen - kein Client, nichts angeboten, nicht angemeldet -,
        // also bekommt sie den einen mitgeteilt, den sie selbst nicht kennt.
        Addons.SignedOut = !_auth.IsLoggedIn;
        // Und erneut, wenn der Spieler den Client wechselt: 1.12.1 und 1.14.2 sind getrennte
        // Installationen mit getrennten Addon-Ordnern, der Satz des einen sagt nichts ueber den anderen.
        Play.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayViewModel.SelectedClientChoice))
                Addons.RefreshProfilesOnly(Play.SelectedClientChoice.Client.Build);
        };
    }

    public string Wordmark => "STONETAVERN";

    /// <summary>Die Version, die der Spieler unten in der Hülle liest — dieselbe Zahl, die der
    /// Self-Update vergleicht (<see cref="Services.UpdateService.RunningVersion"/>), also die
    /// PRODUKT-Version. Vorher stand hier <c>GetName().Version</c>, die im Auslieferungsbuild bei
    /// 1.1.0.0 eingefroren war: der Launcher zeigte allen Spielern „v1.1.0", während 1.5.1 lief. Das
    /// hat die Update-Schleife vom 2026-08-01 nicht verursacht, aber sie unsichtbar gemacht — wer die
    /// Version abliest, um zu prüfen ob das Update ankam, bekommt eine Zahl, die sich nie ändert.</summary>
    public string VersionText
    {
        get
        {
            // Komponentenweise, nicht ToString(3): eine zweigliedrige Version ("1.6") hat Build = -1,
            // und ToString(3) wirft darauf eine ArgumentException — in einem Getter, den die Hülle beim
            // Zeichnen aufruft. Eine Versionsanzeige darf das Fenster nicht mitreissen.
            var v = Services.UpdateService.RunningVersion(System.Reflection.Assembly.GetExecutingAssembly());
            return $"v{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }

    public enum Section { Play, PatchNotes, Armory, Addons, Settings, Account }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(IsPlay), nameof(IsPatchNotes), nameof(IsArmory),
                              nameof(IsAddons), nameof(IsSettings), nameof(IsAccount))]
    private Section _selected = Section.Play;

    [ObservableProperty]
    private ViewModelBase _current;

    public bool IsPlay => Selected == Section.Play;
    public bool IsPatchNotes => Selected == Section.PatchNotes;
    public bool IsArmory => Selected == Section.Armory;
    public bool IsAddons => Selected == Section.Addons;
    public bool IsSettings => Selected == Section.Settings;
    public bool IsAccount => Selected == Section.Account;

    partial void OnSelectedChanged(Section value)
    {
        Current = value switch
        {
            Section.PatchNotes => PatchNotes,
            Section.Armory => Armory,
            Section.Addons => Addons,
            Section.Settings => Settings,
            // Account has no view model of its own yet: the panel is shell-bound (sign-in card or
            // identity), so Current stays on Play and the panel visibility does the work.
            _ => Play,
        };

        // The armory is fetched when it is looked at, not on a timer: it is a page a player opens
        // occasionally, and polling it would put a request on the wire every 18s for nothing.
        if (value == Section.Armory) _ = SafeLoadArmoryAsync();
        // Same rule for the addons: read the catalog and the disk when the page is opened, not on a
        // timer. Which build matters is whatever the play surface is pointed at right now.
        if (value == Section.Addons) _ = SafeLoadAddonsAsync();
        // Und dieselbe Regel fuer die Einstellungen: Fassungsstand und Startbericht beschreiben einen
        // Zustand, der sich waehrend der Sitzung aendert (eine Update-Pruefung, ein gewechselter
        // Realm, ein neu eingetragener Client). Die Ansichten sind Singletons und wuerden sonst den
        // Stand vom Programmstart zeigen - eine Anzeige, die stillsteht, waehrend sie Aktualitaet
        // behauptet, ist genau die Fehlerform, gegen die beide gebaut sind.
        if (value == Section.Settings) Settings.RefreshLiveFacts();
    }

    [RelayCommand] private void GoPlay() => Selected = Section.Play;
    [RelayCommand] private void GoPatchNotes() => Selected = Section.PatchNotes;
    [RelayCommand] private void GoArmory() => Selected = Section.Armory;
    [RelayCommand] private void GoAddons() => Selected = Section.Addons;
    [RelayCommand] private void GoSettings() => Selected = Section.Settings;

    /// <summary>The Account button in the titlebar. It used to open SETTINGS, which is where a player
    /// looking for "am I signed in, and as whom" would never think to look and would not find an
    /// answer either (owner finding 2026-07-22). Identity now has its own place.</summary>
    [RelayCommand] private void GoAccount() => Selected = Section.Account;

    /// <summary>
    /// Straight to the sign-up form, not merely to the account page. Used by the armory, which is
    /// the one place a visitor without an account is guaranteed to stand: it can only ever be empty
    /// for them (owner request 2026-08-04, point 5).
    ///
    /// <para>Order matters. The register mode is set BEFORE the section flips, so the panel is
    /// already showing the form when it fades in. The other way round the player watches a sign-in
    /// card turn into a sign-up card, which reads like a misclick.</para>
    /// </summary>
    [RelayCommand]
    private void GoRegister()
    {
        Login.ShowRegisterCommand.Execute(null);
        Selected = Section.Account;
    }

    // ─── Addons ───────────────────────────────────────────────────────────
    // Bound to the client build the play surface is on, because addons live inside ONE client folder
    // (1.12.1 and 1.14.2 have separate installs and separate Interface/AddOns).

    /// <summary>Fire-and-forget entry point; nothing may escape into UnobservedTaskException.</summary>
    internal async Task SafeLoadAddonsAsync()
    {
        try { await Addons.LoadAsync(Play.SelectedClientChoice.Client.Build); }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Addon list refresh failed - keeping the previous list");
        }
    }

    // ─── Armory (v3) ──────────────────────────────────────────────────────
    // Account scoped and realm scoped: the bearer decides WHOSE characters, the rail decides WHICH
    // realm. Loaded on tab open, on realm switch (while open) and on sign-in; cleared on sign-out.

    /// <summary>Fire-and-forget entry point. Same contract as the friends loader: nothing may escape
    /// into TaskScheduler.UnobservedTaskException, because no caller awaits it.</summary>
    internal async Task SafeLoadArmoryAsync()
    {
        // The GAME realms behind the selected entry, not the entry id: one rail entry can stand for more
        // than one realm the account has characters on (Stonetavern = Elwynn + Barrens), and the web API
        // answers 404 unknown_realm for anything that is not a real realm slug.
        try { await Armory.LoadAsync(SelectedRealm?.AccountRealms ?? []); }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Armory refresh failed - keeping the previous list");
        }
    }

    // ─── Community-Links (öffnen im Standard-Browser, nicht in-app — Hermes §5.2) ───
    [RelayCommand] private void OpenForum() => OpenUrl("https://forum.stonetavern.app");
    [RelayCommand] private void OpenDiscord() => OpenUrl("https://discord.gg/aAfsM8Q5Sp");

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* kein Default-Browser / headless — bewusst still */ }
    }

    // ─── Realm rail (v3) ──────────────────────────────────────────────────
    // Realms are DATA, not wiring (owner decision 2026-07-21): the shipped Stonetavern entries are
    // presets in the same list a player adds to, so the launcher works for any server. Selecting one
    // now really switches the connection - realmlist + manifest - not just the artwork.
    public ObservableCollection<RealmEntry> Realms { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveSelectedRealm))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedRealmCommand))]
    private RealmEntry _selectedRealm;

    /// <summary>The "+" overlay in the rail: name, address, client, optional update source. Own bool
    /// here (not code-behind) - the View stays a thin renderer, this is real UI state.</summary>
    [ObservableProperty] private bool _isAddRealmOpen;

    [RelayCommand] private void OpenAddRealm() => IsAddRealmOpen = true;
    [RelayCommand] private void CloseAddRealm() => IsAddRealmOpen = false;

    /// <summary>Presets can never be removed - hard check here, not just a hidden button, so no
    /// command path (this one or Settings') can ever drop Elwynn/Barrens off the rail.</summary>
    public bool CanRemoveSelectedRealm => SelectedRealm is { IsPreset: false };

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedRealm))]
    private void RemoveSelectedRealm()
    {
        var target = SelectedRealm;
        if (target is null || target.IsPreset) return; // never trust CanExecute alone for this

        var cfg = _config.Load();
        cfg.Realms.RemoveAll(r => r.Id == target.Id);
        if (cfg.SelectedRealmId == target.Id) cfg.SelectedRealmId = RealmRegistry.ElwynnId;
        _config.Save(cfg);

        ReloadRealms();
        IsAddRealmOpen = false;
    }

    /// <summary>The hero's small client toggle (only shown when <see cref="RealmEntry.HasMultipleClients"/>
    /// is true): switch which of the realm's offered builds is active right now. Persists the pick onto
    /// the realm entry itself and re-runs the same re-resolution a realm switch does, since a different
    /// client build needs a fresh realm ping and action-state evaluation exactly like a different realm.</summary>
    [RelayCommand]
    private async Task SelectRealmClient(ClientVersion version)
    {
        if (version is null || SelectedRealm is null) return;
        if (!Play.CanSwitchContext) return; // a running download/verify/launch owns the realm
        if (SelectedRealm.ClientKey == version.Key) return;
        if (!SelectedRealm.AvailableClients.Any(c => c.Key == version.Key)) return;

        // A FRESH instance, not an in-place mutation of the old one. RealmEntry has no
        // INotifyPropertyChanged, and a real screenshot (not just a ViewModel-level test) proved that
        // mutating SelectedRealm.ClientKey in place and then manually raising
        // OnPropertyChanged(nameof(SelectedRealm)) left the badge's nested bindings
        // (SelectedRealm.Client.Era / .PreciseLabel) showing the OLD client - only bindings that
        // separately re-fire their OWN OnPropertyChanged (Play.ClientVersionText) actually updated.
        // Handing the setter a genuinely different reference is what makes Avalonia's compiled
        // bindings re-walk the whole path, and it is the same shape ReloadRealms already uses.
        var updated = new RealmEntry
        {
            Id = SelectedRealm.Id,
            Name = SelectedRealm.Name,
            RealmlistAddress = SelectedRealm.RealmlistAddress,
            ClientKey = version.Key,
            ClientKeys = SelectedRealm.ClientKeys,
            ManifestUrl = SelectedRealm.ManifestUrl,
            IsPreset = SelectedRealm.IsPreset,
            IsLive = SelectedRealm.IsLive,
        };

        var cfg = _config.Load();
        var idx = cfg.Realms.FindIndex(r => r.Id == updated.Id);
        if (idx >= 0) cfg.Realms[idx] = updated; else cfg.Realms.Add(updated);
        _config.Save(cfg);

        var railIdx = Realms.IndexOf(SelectedRealm);
        if (railIdx >= 0) Realms[railIdx] = updated;

        // Suppress the automatic realm-switch side effect the normal SelectedRealm setter triggers
        // (OnSelectedRealmChanged fires Play.SwitchRealmAsync fire-and-forget) - this command awaits
        // the switch explicitly below, so callers (and tests) observe it actually finished.
        _suppressRealmChange = true;
        SelectedRealm = updated;
        _suppressRealmChange = false;
        OnPropertyChanged(nameof(ActiveRealmClient));

        await Play.SwitchRealmAsync();
    }

    /// <summary>Two-way proxy for the hero's small client toggle (ListBox.SelectedItem needs a settable
    /// property; routing the setter through <see cref="SelectRealmClient"/> keeps the busy guard,
    /// persistence and re-resolution in the one place instead of duplicating them in the setter).</summary>
    public ClientVersion? ActiveRealmClient
    {
        get => SelectedRealm?.Client;
        set { if (value is not null) _ = SelectRealmClient(value); }
    }

    /// <summary>Rebuild the rail from config, keeping the current selection if it survived.</summary>
    public void ReloadRealms()
    {
        var cfg = _config.Load();
        var all = RealmRegistry.All(cfg);
        var keepId = SelectedRealm?.Id ?? cfg.SelectedRealmId;

        Realms.Clear();
        foreach (var r in all) Realms.Add(r);

        var pick = Realms.FirstOrDefault(r => r.Id == keepId) ?? Realms[0];
        // The rebuild handed out fresh instances, so the frozen realm must be re-resolved by id or the
        // busy guard would try to restore an entry that is no longer in the rail.
        _lockedRealm = Realms.FirstOrDefault(r => r.Id == _lockedRealm?.Id) ?? pick;
        if (!ReferenceEquals(pick, SelectedRealm)) SelectedRealm = pick;
        else RealmHeroArt = LoadRealmArt(pick);
        OnPropertyChanged(nameof(ActiveRealmClient)); // fresh instance may carry a changed ClientKey
    }

    // ─── Realm hero art (v3) ──────────────────────────────────────────────
    // The v3 hero is realm-centric, so it loads its OWN atmospheric key-art keyed by realm
    // (Assets/Backgrounds/hero-<id>.{webp,png}) — deliberately NOT PlayViewModel.Background, which
    // is the per-ERA art that today is only a text placeholder ("BURNING CRUSADE"). Binding the v3
    // hero to that placeholder produced a double-exposure ghost behind the realm headline. Until
    // real realm art is dropped in, this stays null → the carved stone stage is the backdrop. v1/v2
    // keep using Play.Background unchanged.
    private readonly Dictionary<string, Bitmap?> _realmArtCache = new();

    [ObservableProperty] private Bitmap? _realmHeroArt;

    /// <summary>The realm a running client operation belongs to. Non-null exactly while the rail is
    /// frozen, so a rejected click can be put back where it was.</summary>
    private RealmEntry? _lockedRealm;

    partial void OnSelectedRealmChanged(RealmEntry value)
    {
        if (value is null) return;

        // A download/verify/extract/launch owns the realm: switching would repoint the realmlist and
        // the manifest under the running operation and reset the play surface to Initializing, which
        // hides a live transfer that keeps running. The rail is disabled in the v3 shell while busy;
        // this is the guard for every other caller.
        if (!Play.CanSwitchContext && _lockedRealm is not null)
        {
            if (!ReferenceEquals(SelectedRealm, _lockedRealm))
            {
                _suppressRealmChange = true;
                SelectedRealm = _lockedRealm;
                _suppressRealmChange = false;
            }
            return;
        }
        if (_suppressRealmChange) return;

        RealmHeroArt = LoadRealmArt(value);
        OnPropertyChanged(nameof(ActiveRealmClient)); // a different realm may show a different toggle

        // Persist first, then let the play surface re-resolve from config: RealmRegistry projects the
        // realm's address + manifest into the flat fields every service already reads, so nothing below
        // this line needs to know what a realm is.
        var cfg = _config.Load();
        cfg.SelectedRealmId = value.Id;
        _config.Save(cfg);

        _lockedRealm = value;
        _ = Play.SwitchRealmAsync();

        // The armory is realm scoped, so a switch invalidates it. Only refetch when the section is
        // actually on screen; otherwise the tab will load it when it is opened.
        if (IsArmory) _ = SafeLoadArmoryAsync();
    }

    private bool _suppressRealmChange;

    private Bitmap? LoadRealmArt(RealmEntry? realm)
    {
        if (realm is null) return null;
        if (_realmArtCache.TryGetValue(realm.Id, out var hit)) return hit;

        Bitmap? bmp = null;
        foreach (var ext in new[] { ".webp", ".png" })
        {
            try
            {
                var uri = new System.Uri($"avares://WowLauncher/Assets/Backgrounds/hero-{realm.Id}{ext}");
                if (!AssetLoader.Exists(uri)) continue;
                using var s = AssetLoader.Open(uri);
                bmp = new Bitmap(s);
                break;
            }
            catch (System.Exception) { /* missing/corrupt art → stone stage fallback */ }
        }
        _realmArtCache[realm.Id] = bmp;
        return bmp;
    }

    // ─── Friends sidebar (v3) ─────────────────────────────────────────────
    // Rows, not records: the row instance must survive a presence change, or the ItemsControl
    // rebuilds the container and replays the arrival animation on every poll (see FriendRow).
    public ObservableCollection<FriendRow> Friends { get; } = new();

    // Starts CLOSED. It used to start open, and for a signed-out player that meant a permanent
    // sign-in form taking a quarter of the window with no way to dismiss it: the collapse button
    // lived inside the signed-in branch, so exactly the people who did not want to sign in could not
    // close it (owner finding 2026-07-22, "absolut kontraproduktiv"). Now it is a panel you pull in
    // from the right when you want it, and signing in opens it for you.
    [ObservableProperty] private bool _isFriendsOpen;
    [ObservableProperty] private string _addFriendInput = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddFriendError))]
    private string? _addFriendError;

    public bool HasAddFriendError => !string.IsNullOrEmpty(AddFriendError);

    // Signed-in gate: the sidebar shows the friends list when true, the sign-in card when false.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountName), nameof(AccountInitial), nameof(AccountButtonText))]
    private bool _isLoggedIn;

    /// <summary>The signed-in account, or empty. Read through the auth service rather than cached, so
    /// it cannot drift from the session that the HTTP calls actually use.</summary>
    public string AccountName => _auth.CurrentAccount ?? "";

    /// <summary>Single letter for the avatar disc. It was a hardcoded "S" - the same letter for every
    /// player, signed in or not, which told a player nothing and quietly implied a session.</summary>
    public string AccountInitial =>
        AccountName.Length > 0 ? AccountName[..1].ToUpperInvariant() : "?";

    /// <summary>Titlebar label: the account name when there is one, otherwise the invitation.</summary>
    public string AccountButtonText =>
        AccountName.Length > 0 ? AccountName : Loc.T("V3_SignIn");

    /// <summary>Header count. Recomputed whenever the collection is (re)loaded or grown.</summary>
    public int OnlineFriendCount => Friends.Count(f => f.IsOnline);

    /// <summary>The header line itself ("5 online"). Localized here rather than via a StringFormat
    /// in the AXAML, because word order differs per language.</summary>
    public string OnlineFriendCountText => Loc.F("V3_OnlineCount", OnlineFriendCount);

    private void NotifyOnlineCount()
    {
        OnPropertyChanged(nameof(OnlineFriendCount));
        OnPropertyChanged(nameof(OnlineFriendCountText));
    }

    // Presence poll: refresh the roster every 18s while signed in (WebSocket push stays v2). The timer
    // ticks on the UI thread, so the ObservableCollection is only ever mutated there.
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(18) };
    private bool _pollWired;

    [RelayCommand] private void ToggleFriends() => IsFriendsOpen = !IsFriendsOpen;

    [RelayCommand]
    private async Task AddFriend()
    {
        AddFriendError = null;
        var name = AddFriendInput?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var result = await _friends.AddFriendAsync(name);
        if (result.Ok)
        {
            InsertFriendSorted(result.Friend!);
            NotifyOnlineCount();
            AddFriendInput = "";
        }
        else
        {
            AddFriendError = result.Error; // surfaced, not swallowed
        }
    }

    /// <summary>Sign out: drop the token, clear the roster, stop polling, show the sign-in card again.</summary>
    [RelayCommand]
    private void SignOut()
    {
        _auth.Logout();
        StopPolling();
        Friends.Clear();
        NotifyOnlineCount();
        AddFriendError = null;
        AddFriendInput = "";
        IsLoggedIn = false;
        IsFriendsOpen = false;  // do not leave the sign-in form standing open after a sign-out
        Armory.Clear(); // account scoped: signing out must not leave the previous roster on screen
    }

    /// <summary>Login VM raised success (UI thread): flip to signed-in, load the roster, start polling.</summary>
    private void OnSignedIn()
    {
        IsLoggedIn = true;
        IsFriendsOpen = true;   // signing in IS the request to see the roster
        _ = SafeSyncProfileAsync();   // pull the realms this account carries
        _ = SafeLoadFriendsAsync();
        StartPolling();
        if (IsArmory) _ = SafeLoadArmoryAsync(); // only if the player is looking at it
    }

    private void StartPolling()
    {
        if (!_pollWired)
        {
            // Fire-and-forget the async refresh from the (UI-thread) tick without async void: the tick
            // handler stays synchronous and the guarded PollOnceAsync owns all error handling.
            _pollTimer.Tick += (_, _) => _ = PollOnceAsync();
            _pollWired = true;
        }
        if (!_pollTimer.IsEnabled) _pollTimer.Start();
    }

    private void StopPolling()
    {
        if (_pollTimer.IsEnabled) _pollTimer.Stop();
    }

    private async Task PollOnceAsync()
    {
        if (!IsLoggedIn) { StopPolling(); return; }
        await SafeLoadFriendsAsync();
    }

    /// <summary>
    /// The fire-and-forget entry point for loading the roster. Every caller here is
    /// <c>_ = ...</c> with no continuation, so an exception escaping it would land in
    /// <c>TaskScheduler.UnobservedTaskException</c> and be written to launcher_crash.log with no
    /// visible cause. Offline-first: a failed load leaves the list exactly as it was.
    /// </summary>
    internal async Task SafeLoadFriendsAsync()
    {
        try { await LoadFriendsAsync(); }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Friends refresh failed — keeping the previous list");
        }
    }

    /// <summary>Load the roster, online first then alphabetical. Mutates the bound collection on the
    /// UI thread only: this method is only ever awaited from the UI thread (InitAsync / the poll tick /
    /// OnSignedIn), and it does NOT ConfigureAwait(false), so the continuation stays on the UI thread
    /// even though the HTTP service hops off it internally.</summary>
    public async Task LoadFriendsAsync()
    {
        if (!IsLoggedIn) return; // signed out -> nothing to fetch (and the HTTP impl would no-op anyway)

        var list = await _friends.GetFriendsAsync();
        Reconcile(list.OrderByDescending(x => x.IsOnline).ThenBy(x => x.Account).ToList());
        NotifyOnlineCount();
    }

    /// <summary>
    /// Bring the bound collection to <paramref name="target"/> by touching only what actually
    /// differs, instead of Clear + re-Add.
    ///
    /// The poll runs every 18s and the roster is usually identical, so a full rebuild destroyed and
    /// recreated every row eight times a minute: the list flickered, any scroll position was lost,
    /// and a row-arrival animation would have fired for everyone on every tick (motion as noise,
    /// which is the thing this shell is explicitly not supposed to do). FriendPresence is a record,
    /// so value equality decides "unchanged" for free. UI thread only — see the caller's contract.
    ///
    /// <para>A changed presence is written INTO the existing row (FriendRow.Update) instead of
    /// replacing the collection element. Replacing the element is a CollectionChanged.Replace, the
    /// ItemsControl discards the container, and the new container replays Grid.v3enterrow: the row
    /// would lift in again every time a friend changed zone. Only Add / Insert / Move touch the
    /// collection now, which is exactly the set of events that means "the roster changed".</para>
    /// </summary>
    private void Reconcile(IList<FriendPresence> target)
    {
        for (var i = Friends.Count - 1; i >= 0; i--)
            if (!target.Any(t => t.Account == Friends[i].Account))
                Friends.RemoveAt(i);

        for (var i = 0; i < target.Count; i++)
        {
            var t = target[i];
            if (i >= Friends.Count) { Friends.Add(new FriendRow(t)); continue; }
            if (Friends[i].Account == t.Account)
            {
                Friends[i].Update(t);   // same account, changed presence — in place, no new container
                continue;
            }

            var at = -1;
            for (var j = i + 1; j < Friends.Count; j++)
                if (Friends[j].Account == t.Account) { at = j; break; }

            if (at >= 0)
            {
                Friends.Move(at, i);
                Friends[i].Update(t);
            }
            else
            {
                Friends.Insert(i, new FriendRow(t));
            }
        }

        while (Friends.Count > target.Count) Friends.RemoveAt(Friends.Count - 1);
    }

    /// <summary>Insert into the same order LoadFriendsAsync builds (online first, then alphabetical),
    /// so an added account lands in its sorted slot instead of being appended out of order.</summary>
    private void InsertFriendSorted(FriendPresence f)
    {
        var i = 0;
        while (i < Friends.Count)
        {
            var cur = Friends[i];
            // f sorts before cur when f is online and cur is not, or (same online state) f.Account < cur.Account.
            var before = (f.IsOnline && !cur.IsOnline)
                || (f.IsOnline == cur.IsOnline
                    && string.Compare(f.Account, cur.Account, System.StringComparison.Ordinal) < 0);
            if (before) break;
            i++;
        }
        Friends.Insert(i, new FriendRow(f));
    }

    public Task InitAsync()
    {
        // 🔴 Die ZWEITE Abrufquelle. Der Abruf im PlayViewModel allein stillzulegen hat nichts
        // bewirkt: dieser hier lief weiter, und im Log stand nach dem Umbau unveraendert
        // "Fetching news from .../news.json" (gemessen 2026-08-12 am laufenden 1.7.8). Genau der
        // Fall, in dem ein halber Umbau wie ein ganzer aussieht — wer eine Seite eines Mechanismus
        // abschaltet, prueft die andere.
        // _ = PatchNotes.LoadAsync();
        if (IsLoggedIn)             // v3 friends rail — only when a session is already restored
        {
            _ = SafeLoadFriendsAsync(); // non-blocking, like News
            StartPolling();
        }
        return Play.InitAsync();
    }
}
