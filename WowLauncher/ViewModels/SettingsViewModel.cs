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
        IDesktopIntegrationService? desktopIntegration = null)
    {
        _config = config;
        _folderPicker = folderPicker;
        _desktop = desktopIntegration ?? DesktopIntegration.ForCurrentOs();
        var c = _config.Load();
        _wowExecutablePath = c.WowExecutablePath;
        _profileSyncEnabled = c.ProfileSyncEnabled;
        _preferredInstallRoot = c.PreferredInstallRoot ?? "";

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
        nameof(SelectedRealmClient), nameof(SelectedRealmClientLine))]
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

    /// <summary>Opens the same native folder picker the first-install download flow uses.
    /// A cancel leaves the field exactly as it was.</summary>
    [RelayCommand]
    private async Task BrowseInstallFolder()
    {
        var picked = await _folderPicker.PickFolderAsync(Loc.T("Play_Picker_Title"), PreferredInstallRoot);
        if (picked is not null)
            PreferredInstallRoot = picked;
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
