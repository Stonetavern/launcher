using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowLauncher.Localization;
using WowLauncher.Models;
using WowLauncher.Services;

namespace WowLauncher.ViewModels;

/// <summary>
/// One addon on screen. A row, not a record, for the same reason the friends list uses rows: the
/// instance has to survive an install (busy → installed) or the list rebuilds and the player loses
/// their scroll position mid-action.
/// </summary>
public sealed partial class AddonRow : ObservableObject
{
    public AddonRow(AddonEntry entry, AddonStatus status)
    {
        Entry = entry;
        Apply(status);
    }

    /// <summary>The catalog entry behind this row — what an install actually acts on.</summary>
    public AddonEntry Entry { get; }

    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string Summary => Entry.Summary;

    /// <summary>Author and license on one line. The license is shown because the launcher mirrors
    /// someone else's work; hiding who wrote it would be the wrong kind of quiet.</summary>
    public string Credit =>
        string.Join("  ·  ", new[] { Entry.Author, Entry.License }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string Homepage => Entry.Homepage;
    public bool HasHomepage => !string.IsNullOrWhiteSpace(Entry.Homepage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionLine), nameof(CanInstall), nameof(CanRemove), nameof(ActionLabel))]
    private bool _installed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionLine), nameof(CanInstall), nameof(CanRemove), nameof(ActionLabel))]
    private bool _managed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionLine), nameof(ActionLabel))]
    private bool _hasUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall), nameof(CanRemove))]
    private bool _busy;

    /// <summary>Ob das Paket zum laufenden Client gehoert. Die Liste zeigt bewusst auch fremde Pakete
    /// (Owner-Entscheid 2026-08-12), aber installierbar sind sie nicht — der Knopf bleibt aus, und
    /// selbst wenn er es nicht taete, lehnt AddonService.InstallAsync ab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall), nameof(FitsNote))]
    private bool _fitsClient = true;

    /// <summary>Kurzer Grund neben der Zeile, wenn das Paket nicht zu diesem Client gehoert.</summary>
    public string FitsNote => FitsClient ? "" : Loc.T("Addons_OtherGameVersion");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionLine))]
    private string _installedVersion = "";

    public string AvailableVersion => Entry.Version;

    /// <summary>The publication date, when the catalogue carries one. Shown beside the version because
    /// "abc123def456" answers "different" and nothing else — a date answers "newer".</summary>
    public string Released => Entry.Released;
    public bool HasReleased => !string.IsNullOrWhiteSpace(Entry.Released);

    /// <summary>The one line under the name that says where this addon stands. Never a guess: a
    /// hand-installed copy is named as such rather than shown as installed by us.</summary>
    public string VersionLine
    {
        get
        {
            if (Installed && !Managed) return Loc.T("Addons_State_Foreign");
            if (Installed && HasUpdate)
                return Loc.F("Addons_State_UpdateAvailable", InstalledVersion, AvailableVersion);
            if (Installed) return Loc.F("Addons_State_Installed", InstalledVersion);
            return Loc.F("Addons_State_Available", AvailableVersion);
        }
    }

    public string ActionLabel =>
        HasUpdate ? Loc.T("Addons_Update") : Loc.T("Addons_Install");

    /// <summary>Install and update are the same button; a hand-installed copy offers neither, because
    /// the launcher will not overwrite something it did not put there.</summary>
    public bool CanInstall => FitsClient && !Busy && (!Installed || HasUpdate) && !(Installed && !Managed);

    public bool CanRemove => !Busy && Installed && Managed;

    public void Apply(AddonStatus status)
    {
        Installed = status.IsInstalled;
        Managed = status.IsManaged;
        HasUpdate = status.HasUpdate;
        InstalledVersion = status.InstalledVersion ?? "";
        FitsClient = status.FitsClient;
    }
}

/// <summary>
/// The ADDONS section: what the realm offers for the client the player has, what is already in
/// <c>Interface/AddOns</c>, and one button per row.
///
/// <para><b>Every row is bound to a concrete client install.</b> Addons are not a launcher-wide
/// setting — they live inside one client folder, and the 1.12.1 and 1.14.2 installs have their own.
/// So the section refuses to act at all when no install is known for the selected build, and says so,
/// instead of installing into a folder it invented.</para>
/// </summary>
public sealed partial class AddonsViewModel : ViewModelBase
{
    private readonly IAddonService _addons;
    private readonly IConfigService _config;
    private readonly AddonProfileService _profiles;
    /// <summary>Only to answer "is the game up right now". Optional so the tests that build this VM
    /// directly do not have to invent one; null then means "cannot tell", and the switch goes ahead.</summary>
    private readonly WowLauncher.Services.Platform.IGameProcessDetector? _detector;
    private readonly Serilog.ILogger _log;

    private string _clientDir = "";
    private int _build;

    public AddonsViewModel(
        IAddonService addons, IConfigService config, AddonProfileService profiles, Serilog.ILogger log,
        WowLauncher.Services.Platform.IGameProcessDetector? detector = null)
    {
        _addons = addons;
        _config = config;
        _profiles = profiles;
        _detector = detector;
        _log = log.ForContext<AddonsViewModel>();
    }

    // ── Profiles: one addon set per realm ─────────────────────────────────────────────────────

    /// <summary>The realms this account plays on, each with its own addon set. Both realms share one
    /// client directory, so before profiles existed they shared one Interface/AddOns and the last
    /// install won for both. Shown only when there is more than one realm to keep apart.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProfiles), nameof(CanCreateProfile), nameof(CreateProfileHint))]
    private IReadOnlyList<string> _profileNames = [];

    [ObservableProperty] private string? _activeProfile;

    /// <summary>True while this VM is writing <see cref="ActiveProfile"/> itself. Without it the
    /// property change raised by a successful switch would ask for the same switch again.</summary>
    private bool _applyingProfile;

    /// <summary>The picker binds straight to the property, so the change IS the request. Doing it here
    /// rather than through a behaviour keeps the view free of an interactivity package for one event.</summary>
    partial void OnActiveProfileChanged(string? value)
    {
        if (_applyingProfile || string.IsNullOrWhiteSpace(value)) return;
        _ = SelectProfileAsync(value);
    }

    /// <summary>Der Waehler erscheint, sobald es mehr als einen Satz gibt - egal ob die Saetze von
    /// den Realms kommen oder selbst angelegt sind. Vorher hing er allein an der Realm-Zahl, was
    /// bedeutet haette: wer einen eigenen zweiten Satz anlegt und nur EINEN Realm hat, kann ihn danach
    /// nicht mehr erreichen.</summary>
    public bool ShowProfiles => ProfileNames.Count > 1;

    /// <summary>Anlegen geht immer, sobald ein Client da ist - auch beim ersten eigenen Satz, wo es
    /// noch keinen Waehler gibt.</summary>
    public bool ShowCreateProfile => !NeedsClient && !SignedOut;

    /// <summary>Switch the addon set. The rename is instant and copies nothing, but it cannot happen
    /// while the game holds the folder open, so a running client refuses the switch instead of
    /// half-doing it.</summary>
    public async Task SelectProfileAsync(string profile)
    {
        if (NeedsClient) return;

        var onDisk = _profiles.ActiveProfile(_clientDir);
        if (string.Equals(profile, onDisk, System.StringComparison.OrdinalIgnoreCase)) return;

        // Renaming the folder a live client has open is the one way this feature could cost a player
        // something real: WoW writes its addon settings back on exit, into whichever folder now
        // carries the name.
        var result = _profiles.Switch(_clientDir, profile, IsGameRunning());
        if (!result.Ok)
        {
            Status = result.Error ?? Loc.T("Addons_Profile_Failed");
            // Put the picker back on what is actually on disk: a failed switch that leaves the menu
            // showing the other realm would be a lie about which addons are loaded.
            //
            // 🔴 Über den EINTRAG der Liste, nicht über den rohen Plattenwert. Auf der Platte steht
            // der Name kleingeschrieben, in der Liste großgeschrieben - der rohe Wert findet dort
            // keine Entsprechung, und das Menü stand danach LEER. Ein leeres Menü über einem Ordner
            // voller Addons ist die dritte Variante derselben Lüge: es behauptet nichts, wo es etwas
            // behaupten müsste. Gefunden 2026-08-05 durch einen Test, der genau diesen Fehlerpfad
            // erst möglich machte.
            _applyingProfile = true;
            ActiveProfile = ProfileNames.FirstOrDefault(
                n => string.Equals(n, onDisk, System.StringComparison.OrdinalIgnoreCase)) ?? onDisk;
            _applyingProfile = false;
            return;
        }

        var cfg = _config.Load();
        cfg.AddonProfile = result.Profile;
        _config.Save(cfg);

        _applyingProfile = true;
        ActiveProfile = result.Profile;
        _applyingProfile = false;

        Status = Loc.F("Addons_Profile_Switched", result.Profile);
        // The list on screen describes the OLD set until it is asked again.
        await LoadAsync(_build).ConfigureAwait(true);
    }

    // ── Eigene Saetze, die der Spieler selbst anlegt (Owner-Wunsch 2 vom 2026-08-04) ───────────
    //
    // Bis hierher gab es genau einen Satz je Realm, vom Launcher benannt. Wer einen zweiten wollte -
    // eine schlanke Fassung fuer Raids, eine zum Ausprobieren -, musste Ordner von Hand verschieben.
    //
    // Warum das kein neuer Mechanismus ist: AddonProfileService kennt seit dem ersten Tag beliebige
    // Namen. Ein angelegter Satz ist ein Verzeichnis `Interface/AddOns.<name>` neben dem aktiven, und
    // KnownProfiles findet ihn beim naechsten Blick auf die Platte wieder. Es braucht also keine
    // zusaetzliche Liste irgendwo - und damit auch keine zweite Wahrheit, die mit der Platte
    // auseinanderlaufen kann.
    //
    // 🔴 Nicht gesynct, ausdruecklich (Owner). Der Profil-Abgleich traegt die Realms des Spielers,
    // nicht seine Ordner.

    /// <summary>True while the name field is open. The field is not always there: an empty text box
    /// beside a picker invites somebody to type in the wrong one.</summary>
    [ObservableProperty] private bool _isCreatingProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateProfile), nameof(CreateProfileHint))]
    private string _newProfileName = "";

    /// <summary>Empty, or a name that already exists, cannot be created. Compared through the same
    /// sanitiser the folder name goes through, so "My Raid" and "myraid" are not two sets that would
    /// both want the same directory.</summary>
    /// <summary>Immer false: es gibt genau einen Satz, und der ist nicht loeschbar und nicht
    /// vermehrbar (Owner-Entscheid 2026-08-12). Die Eigenschaft bleibt bestehen, damit die Ansicht
    /// und ihre Tests unverändert weiterlaufen, statt an einer entfernten Bindung zu zerbrechen.</summary>
    public bool CanCreateProfileNoLongerOffered => false;

    public bool CanCreateProfile
    {
        get
        {
            var clean = AddonProfileService.Sanitise(NewProfileName);
            if (string.IsNullOrWhiteSpace(NewProfileName) || clean == "default") return false;
            return !ProfileNames.Any(n =>
                string.Equals(AddonProfileService.Sanitise(n), clean, System.StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Why the button is grey, when it is. A disabled control without a reason is a dead end.</summary>
    public string CreateProfileHint
    {
        get
        {
            if (string.IsNullOrWhiteSpace(NewProfileName)) return "";
            var clean = AddonProfileService.Sanitise(NewProfileName);
            if (clean == "default") return Loc.T("Addons_NewSet_BadName");
            return CanCreateProfile ? "" : Loc.T("Addons_NewSet_Exists");
        }
    }

    [RelayCommand]
    private void StartCreateProfile()
    {
        NewProfileName = "";
        IsCreatingProfile = true;
    }

    [RelayCommand]
    private void CancelCreateProfile()
    {
        IsCreatingProfile = false;
        NewProfileName = "";
    }

    /// <summary>Create the set and switch to it. It starts EMPTY, which is the whole point of a second
    /// set - and it is the reason nothing is copied: copying somebody's addons would double the disk
    /// use and, worse, duplicate their saved settings into a set they wanted clean.</summary>
    [RelayCommand]
    private async Task CreateProfileAsync()
    {
        if (!CanCreateProfile) return;

        var wanted = NewProfileName.Trim();
        if (IsGameRunning())
        {
            // Same rule as a switch: creating one means parking the active folder, and the game holds
            // it open. Refuse rather than half-do it.
            Status = Loc.T("Addons_Profile_Failed");
            return;
        }

        var result = _profiles.Switch(_clientDir, wanted, gameRunning: false);
        if (!result.Ok)
        {
            Status = result.Error ?? Loc.T("Addons_Profile_Failed");
            return;
        }

        var cfg = _config.Load();
        cfg.AddonProfile = result.Profile;
        _config.Save(cfg);

        IsCreatingProfile = false;
        NewProfileName = "";
        Status = Loc.F("Addons_NewSet_Created", result.Profile);

        // Re-read from disk rather than appending the name here: the folder is the truth, and a list
        // built from what we just asked for would look right even if the rename had not happened.
        await LoadAsync(_build).ConfigureAwait(true);
    }

    private bool IsGameRunning()
    {
        try { return _detector?.IsGameRunning(null) ?? false; }
        catch { return false; }
    }

    /// <summary>Realm ids are lowercase on disk and in the config; a menu reads better with the name
    /// the realm actually goes by.</summary>
    private static string Capitalise(string id) =>
        string.IsNullOrEmpty(id) ? id : char.ToUpperInvariant(id[0]) + id[1..];

    private void RefreshProfiles(LauncherConfig cfg)
    {
        try
        {
            if (NeedsClient) { ProfileNames = []; ActiveProfile = null; return; }

            // 🔴 Ein Satz, nicht einer pro Realm (Owner-Entscheid 2026-08-12). Vorher wurden die
            // Account-Realms zu Addon-Saetzen (Elwynn, Barrens) — das erzeugte eine Auswahl, die
            // niemand getroffen hatte, und zwei Saetze, die auseinanderlaufen konnten.
            //
            // Die Migration schreibt nur die Marker-Datei um. Bereits geparkte Saetze
            // (Interface/AddOns.barrens) bleiben unberuehrt auf der Platte liegen: das sind die Addons
            // des Spielers. Sie erscheinen nur nicht mehr in der Auswahl.
            //
            // Weil ShowProfiles an "mehr als ein Eintrag" haengt, verschwindet die Auswahl damit von
            // selbst — es braucht keine Aenderung an der Ansicht, die spaeter jemand uebersieht.
            _profiles.MigrateToDefault(_clientDir);
            ProfileNames = [Capitalise(AddonProfileService.DefaultProfile)];

            // What is on disk wins over what the config remembers: a folder can be moved by hand.
            var onDisk = _profiles.ActiveProfile(_clientDir);
            if (onDisk is null && ProfileNames.Count > 0)
            {
                // Nothing claims the AddOns folder yet (first run with profiles, or an install from
                // before they existed). Adopt it for the first realm rather than showing an empty
                // picker over a folder that belongs to nobody: adopting writes a marker and moves
                // nothing, so whatever is installed stays installed.
                var adopted = _profiles.Switch(_clientDir, ProfileNames[0]);
                if (adopted.Ok) onDisk = adopted.Profile;
            }

            _applyingProfile = true;
            // Match case-insensitively so the picker lands on the entry it is showing, not beside it.
            ActiveProfile = ProfileNames.FirstOrDefault(
                n => string.Equals(n, onDisk, System.StringComparison.OrdinalIgnoreCase))
                ?? ProfileNames.FirstOrDefault();
            _applyingProfile = false;
        }
        catch (System.Exception ex)
        {
            _log.Debug(ex, "Could not resolve addon profiles");
            ProfileNames = [];
        }
    }

    public string Title => Loc.T("Addons_Title");

    public ObservableCollection<AddonRow> Addons { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(ShowEmpty), nameof(ShowNeedsClient), nameof(ShowCreateProfile))]
    private bool _isLoading;

    /// <summary>What just happened, in one line. Empty when there is nothing to say.</summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>Set when no client install is known for the selected build: the section then explains
    /// that instead of offering buttons that could only fail.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(ShowEmpty), nameof(ShowNeedsClient), nameof(ShowCreateProfile))]
    private bool _needsClient;

    public bool IsEmpty => !IsLoading && !NeedsClient && Addons.Count == 0;
    public string Placeholder => Loc.T("Addons_Empty");
    /// <summary>True while nobody is signed in. Set by the shell, because the sign-in state lives
    /// there. It exists so the page can tell the THREE empty states apart: no client, nothing offered,
    /// and not signed in. Showing "no addons are offered for this client yet" to a signed-out player
    /// would be the right screen with the wrong reason - and the wrong reason is what sends somebody
    /// looking for a problem that is not there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNeedsClient), nameof(ShowEmpty), nameof(ShowCreateProfile))]
    private bool _signedOut;

    public bool ShowNeedsClient => !SignedOut && NeedsClient;
    public bool ShowEmpty => !SignedOut && IsEmpty;

    public string NeedsClientText => Loc.T("Addons_NeedsClient");

    /// <summary>
    /// Load the list for one client build. Resolves the install from the config the rest of the
    /// launcher uses (<c>ClientInstalls[build]</c>), so it always points at the same folder the Play
    /// button starts from.
    /// </summary>
    /// <summary>
    /// Resolve which addon sets exist and which one is active, WITHOUT touching the network.
    ///
    /// <para>Exists because the play surface has to show the active set before anyone opens the addons
    /// tab — and it is the play surface, not the tab, where the choice matters: the rail carries one
    /// entry (Stonetavern, one address) and the player picks the realm inside the GAME, after the
    /// launcher is gone. So the launcher can never switch the set on its own, and a picker that is only
    /// reachable through a tab is a decision most players will never make.</para>
    ///
    /// <para>Config plus two directory reads, no catalogue fetch. Safe to call on startup and on every
    /// client switch; <see cref="LoadAsync"/> still does this itself, so the tab is unaffected.</para>
    /// </summary>
    public void RefreshProfilesOnly(int build)
    {
        _build = build;
        try
        {
            var cfg = _config.Load();
            _clientDir = cfg.ClientInstalls.TryGetValue(build, out var dir) ? dir ?? "" : "";
            NeedsClient = string.IsNullOrWhiteSpace(_clientDir);
            RefreshProfiles(cfg);
        }
        catch (System.Exception ex)
        {
            // Never a crash on a start path: no profiles simply means the play surface shows no picker.
            _log.Debug(ex, "Could not resolve addon profiles without the catalogue");
        }
    }

    public async Task LoadAsync(int build, CancellationToken ct = default)
    {
        _build = build;
        IsLoading = true;
        try
        {
            var cfg = _config.Load();
            _clientDir = cfg.ClientInstalls.TryGetValue(build, out var dir) ? dir ?? "" : "";
            NeedsClient = string.IsNullOrWhiteSpace(_clientDir);
            RefreshProfiles(cfg);

            var catalog = await _addons.GetCatalogAsync(ct: ct).ConfigureAwait(true);

            // Bei JEDEM Laden nachfassen, nicht nur beim Aufbau der Oberflaeche. Die Shell setzt
            // dieses Flag einmal beim Start; laeuft die Sitzung danach ab, bleibt es auf "angemeldet"
            // stehen, und die Seite nennt den falschen Grund fuer eine leere Liste (Befund
            // 2026-08-13: 36 angebotene Addons, "es werden noch keine angeboten" auf dem Schirm).
            if (_addons.CatalogNeedsSignIn) SignedOut = true;

            var offered = catalog.ForBuild(build);

            IReadOnlyList<AddonStatus> statuses = NeedsClient
                ? offered.Select(e => new AddonStatus(e.Id, e.Name, e.Summary, e.Author, e.License,
                        e.Homepage, e.Version, null, false, e.Size)).ToList()
                : await _addons.GetStatusAsync(_clientDir, build, ct).ConfigureAwait(true);

            Addons.Clear();
            foreach (var entry in offered)
            {
                var status = statuses.FirstOrDefault(s => s.Id == entry.Id);
                if (status is null) continue;
                Addons.Add(new AddonRow(entry, status));
            }
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (OperationCanceledException) { /* superseded — leave the list as it was */ }
        catch (Exception ex)
        {
            _log.Warning(ex, "Loading the addon list failed");
            Status = Loc.T("Addons_LoadFailed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task InstallAsync(AddonRow? row)
    {
        if (row is null || row.Busy || NeedsClient) return;

        row.Busy = true;
        Status = "";
        try
        {
            var progress = new Progress<string>(line => Status = line);
            var result = await _addons.InstallAsync(_clientDir, row.Entry, _build, progress).ConfigureAwait(true);
            Status = result.Ok ? Loc.F("Addons_Installed", row.Name) : result.Error ?? "";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Installing {Addon} failed", row.Id);
            Status = Loc.F("Addons_InstallFailed", row.Name);
        }
        finally
        {
            row.Busy = false;
            await RefreshRowsAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(AddonRow? row)
    {
        if (row is null || row.Busy || NeedsClient) return;

        row.Busy = true;
        Status = "";
        try
        {
            var result = await _addons.RemoveAsync(_clientDir, row.Id).ConfigureAwait(true);
            Status = result.Ok ? Loc.F("Addons_Removed", row.Name) : result.Error ?? "";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Removing {Addon} failed", row.Id);
            Status = Loc.F("Addons_RemoveFailed", row.Name);
        }
        finally
        {
            row.Busy = false;
            await RefreshRowsAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void OpenHomepage(AddonRow? row)
    {
        if (row is null || !row.HasHomepage) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(row.Homepage) { UseShellExecute = true });
        }
        catch (Exception ex) { _log.Warning(ex, "Could not open {Url}", row.Homepage); }
    }

    /// <summary>Re-read what is on disk and update the existing rows in place — the list keeps its
    /// order and its scroll position, and only the state that actually changed moves.</summary>
    private async Task RefreshRowsAsync()
    {
        if (NeedsClient || Addons.Count == 0) return;
        try
        {
            var statuses = await _addons.GetStatusAsync(_clientDir, _build).ConfigureAwait(true);
            foreach (var row in Addons)
            {
                var status = statuses.FirstOrDefault(s => s.Id == row.Id);
                if (status is not null) row.Apply(status);
            }
        }
        catch (Exception ex) { _log.Debug(ex, "Refreshing the addon rows failed"); }
    }
}

/// <summary>
/// An addon service that offers nothing. Used where a shell is built without a catalog (unit tests,
/// and any host that has no distribution server): the section then renders its empty state instead of
/// the code having to check for null on every call.
/// </summary>
internal sealed class NullAddonService : IAddonService
{
    public Task<AddonCatalog> GetCatalogAsync(bool force = false, CancellationToken ct = default) =>
        Task.FromResult(AddonCatalog.Empty);

    public Task<IReadOnlyList<AddonStatus>> GetStatusAsync(string clientDir, int build, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AddonStatus>>([]);

    public Task<AddonActionResult> InstallAsync(string clientDir, AddonEntry entry, int build = 0,
        IProgress<string>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(AddonActionResult.Failed("No addon source is configured."));

    public Task<AddonActionResult> RemoveAsync(string clientDir, string addonId, CancellationToken ct = default) =>
        Task.FromResult(AddonActionResult.Failed("No addon source is configured."));
}
