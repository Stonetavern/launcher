using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using WowLauncher.Localization;
using WowLauncher.Models;
using WowLauncher.Services;

namespace WowLauncher.ViewModels;

/// <summary>
/// The play surface: HeroStage (expansion picker + per-era background, realm status, language)
/// + the global ActionBar state machine (DESIGN-UI-ENTERPRISE §7). Strict MVVM, all services
/// injected, no code-behind logic.
/// </summary>
public sealed partial class PlayViewModel : ViewModelBase
{
    private readonly IConfigService _config;
    private readonly IManifestService _manifest;
    private readonly IClientService _client;
    private readonly IServerStatusService _srv;
    private readonly IDownloadService _download;
    private readonly IClientVerifyService _verify;
    private readonly IUpdateService _update;
    private readonly INewsService _news;
    private readonly WowLauncher.Services.Platform.ILaunchExitPolicy _launchExit;
    private readonly WowLauncher.Services.Platform.IAppPaths _paths;
    private readonly IFolderPickerService _folderPicker;
    private readonly IShellWindowController? _windowController;
    private readonly WowLauncher.Services.Platform.IGameSession? _session;
    /// <summary>The 1.12.1 language switch. Optional so every existing construction of this view model
    /// keeps compiling; null simply means the 1.12.1 language menu stays English-only, which is what
    /// shipped before it existed.</summary>
    private readonly ILanguagePackService? _languagePacks;
    /// <summary>Used only to refuse a language switch while a client is up. Optional for the same
    /// reason as <see cref="_languagePacks"/>.</summary>
    private readonly WowLauncher.Services.Platform.IGameProcessDetector? _gameDetector;
    private readonly ILogger _log;

    private string _wowPathBacking = "";

    /// <summary>The resolved client executable for the current pick. A property rather than a plain
    /// field so the LANGUAGE MENU follows it: which languages exist is a property of the installation
    /// on disk, not of the launcher, and it changes whenever this path does (era switch, a client
    /// installed, a different build picked). Every existing assignment keeps working unchanged.</summary>
    private string _wowPath
    {
        get => _wowPathBacking;
        set
        {
            if (_wowPathBacking == value) return;
            _wowPathBacking = value;
            RefreshAvailableLocales();
        }
    }

    private string _downloadUrl = "";
    private string _downloadSha256 = "";
    private long _downloadSize;                    // manifest-declared size, for the pre-download disk-space check
    private string _filesManifestUrl = "";        // optional per-file manifest for Repair (MANIFEST-SCHEMA.md §files_url)
    private string _clientVersion = "";          // manifest's current client version for the active build (§6.3)
    // False whenever the ACTIVE build (SelectedClientChoice) is not the phase's own canonical build -
    // the manifest only ever describes that one build per phase, so an alternate build (1.14.2/42597
    // on the "vanilla" phase) never gets download coordinates (see ApplyExpansionAsync). Drives
    // NeedsOwnClient, which in turn keeps the ActionBar from offering a DOWNLOAD that can only fail
    // (owner/Codex Finding 4, 2026-07-22).
    //
    // Written through BuildHasManagedSource, never directly: SIX projections read this field, and it
    // can change while State does NOT (NoClient -> NoClient when a realm's second build is picked and
    // there is no install for either). LauncherState is an enum, so an unchanged State means
    // [ObservableProperty] raises nothing at all and its whole NotifyPropertyChangedFor list is
    // skipped - the ActionBar then keeps rendering an enabled DOWNLOAD button for a build that has no
    // download, which is the very state this flag exists to prevent. Same failure shape as the two
    // defects this package already fixed: correct values, no notification, only visible in a
    // screenshot. A test that reads the properties directly cannot see it, which is why the test for
    // this one watches PropertyChanged instead.
    private bool _buildHasManagedSource = true;

    private bool BuildHasManagedSource
    {
        get => _buildHasManagedSource;
        set
        {
            if (_buildHasManagedSource == value) return;
            _buildHasManagedSource = value;
            OnPropertyChanged(nameof(NeedsOwnClient));
            OnPropertyChanged(nameof(ActionEnabled));
            OnPropertyChanged(nameof(ActionPrimaryText));
            OnPropertyChanged(nameof(ActionGlyph));
            OnPropertyChanged(nameof(StatusLine));
            OnPropertyChanged(nameof(SubLine));
            // The hero's second locate block disappears when the main button became that action.
            OnPropertyChanged(nameof(ShowLocate));
            // ActionEnabled changed above, so the command behind the button must be told too. The V3
            // shell binds IsEnabled to the property AND the Command to PlayCommand; without this the
            // command's own CanExecute stays on the previous answer and a keyboard/automation invoke
            // disagrees with what the button shows.
            PlayCommand.NotifyCanExecuteChanged();
        }
    }
    private ProgressionPhase _activePhase = Progression.Default;
    private ServerManifest? _lastManifestBacking;   // cached for re-eval / check-for-updates / repair

    /// <summary>
    /// Das zuletzt geholte Manifest. Eine Eigenschaft und kein blankes Feld aus demselben Grund wie
    /// bei <see cref="_wowPath"/>: das <b>Sprachmenü hängt daran</b>, und zwar an beiden Eingaben.
    ///
    /// <para><b>Der Fehler, den das schließt</b> (Owner-Befund 2026-08-04, von Codex bestätigt).
    /// Welche Sprachen wählbar sind, ergibt sich aus zwei Quellen: was auf der Platte liegt und was
    /// der Server an Paketen anbietet. Nachgerechnet wurde bisher nur, wenn sich die <b>erste</b>
    /// änderte. Nach einer frischen 1.12.1-Installation lief die Rechnung also in dem Moment, in dem
    /// der Client-Pfad gesetzt wurde — und wenn das Manifest da noch nicht angekommen war, blieb
    /// „nur Englisch" stehen. Das Manifest traf später ein, niemand rechnete nach, und der Wähler
    /// blieb verschwunden, obwohl der Server drei Pakete anbot. Kein Fehler, keine Meldung, nur ein
    /// Bedienelement, das nicht da war.</para>
    ///
    /// <para>Auch <c>null</c> löst die Neuberechnung aus: ein fehlgeschlagener Abruf setzt das
    /// Manifest zurück, und dann muss das Menü ebenfalls stimmen statt Pakete anzubieten, von denen
    /// wir gerade nichts mehr wissen.</para>
    /// </summary>
    private ServerManifest? _lastManifest
    {
        get => _lastManifestBacking;
        set
        {
            _lastManifestBacking = value;
            RefreshAvailableLocales();
        }
    }
    private bool _suppressExpansionChange;        // guard programmatic SelectedExpansion sets
    private CancellationTokenSource? _applyCts;   // cancels an in-flight expansion switch when a new one starts
    private CancellationTokenSource? _downloadCts; // cancels an in-flight client download when the player pauses

    public PlayViewModel(IConfigService cfg, IManifestService mf, IClientService cl,
        IServerStatusService st, IDownloadService dl, IClientVerifyService verify, IUpdateService up,
        INewsService news, WowLauncher.Services.Platform.ILaunchExitPolicy launchExit,
        WowLauncher.Services.Platform.IAppPaths paths, IFolderPickerService folderPicker, ILogger log,
        IShellWindowController? windowController = null,
        WowLauncher.Services.Platform.IGameSession? session = null,
        ILanguagePackService? languagePacks = null,
        WowLauncher.Services.Platform.IGameProcessDetector? gameDetector = null)
    {
        _config = cfg; _manifest = mf; _client = cl; _srv = st; _download = dl; _verify = verify;
        _update = up; _news = news;
        _launchExit = launchExit; _paths = paths; _folderPicker = folderPicker;
        _windowController = windowController; _session = session; _languagePacks = languagePacks;
        _gameDetector = gameDetector;
        _log = log.ForContext<PlayViewModel>();
        var c = _config.Load();
        _selectedLocale = ClientLocales.FromCode(c.Locale);
        _realmAddress = c.RealmlistAddress;
        // Seed the era from the SELECTED REALM, not from the last manual pick. The realm names the
        // client build it speaks, so deriving the era from it is what keeps the first frame honest -
        // otherwise a saved "wotlk" pick sat under an Elwynn headline that needs 1.12.1. A manual pick
        // still wins for the rest of the session; it is the realm that owns the answer across restarts.
        _selectedExpansion = Expansion.ById(RealmRegistry.Resolve(c).Client.EraId);
        // The picker row is one notch more precise than the era: it names the exact build, which
        // matters the moment an era has more than one (Vanilla: 1.12.1 vs 1.14.2). Seeded from the
        // same realm the era above came from, so the two never start out contradicting each other.
        _selectedClientChoice = ClientChoice.ForKey(RealmRegistry.Resolve(c).ClientKey);
        _currentClientChoice = _selectedClientChoice;
        // Seed the active phase from the persisted expansion so ClientVersionText / labels are
        // consistent from the very first frame (before InitAsync's ApplyExpansionAsync runs) —
        // otherwise the ActionBar briefly shows Vanilla while the picker+background show the saved era.
        _activePhase = _selectedExpansion.Phase;
        _phaseSlug = _activePhase.Slug;
        _background = LoadBackground(_selectedExpansion);
        _logo = LoadLogo(_selectedExpansion);
    }

    // ─── State machine (§7) ───────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(IsUpdateAvailable), nameof(IsEraTransition),
        nameof(IsDownloading), nameof(IsError), nameof(IsBusy), nameof(ActionPrimaryText), nameof(ActionGlyph),
        nameof(ActionEnabled), nameof(ShowProgress), nameof(StatusLine), nameof(SubLine),
        nameof(CanRepair), nameof(CanCheckUpdates), nameof(CanSwitchContext), nameof(NeedsOwnClient),
        nameof(IsPaused), nameof(IsPausable), nameof(ShowLocate), nameof(CanLocate))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand), nameof(UpdateCommand),
        nameof(RepairCommand), nameof(CheckForUpdatesCommand),
        nameof(PauseDownloadCommand), nameof(LocateExistingClientCommand))]
    private LauncherState _state = LauncherState.Initializing;

    public bool IsReady => State == LauncherState.Ready;
    public bool IsUpdateAvailable => State == LauncherState.UpdateAvailable;
    public bool IsEraTransition => State == LauncherState.EraTransition;
    public bool IsDownloading => State is LauncherState.Downloading or LauncherState.Verifying;
    public bool IsError => State == LauncherState.DownloadError;
    public bool IsBusy => State is LauncherState.Downloading or LauncherState.Verifying or LauncherState.Launching;

    /// <summary>The download is paused: bytes on disk, resumable via the Play/Resume button.</summary>
    public bool IsPaused => State == LauncherState.Paused;

    /// <summary>Pause makes sense only while bytes are actually flowing — not during the (fast,
    /// indeterminate) verify/extract step, where a "pause" could not honour the request cleanly.</summary>
    public bool IsPausable => State == LauncherState.Downloading;

    /// <summary>
    /// Whether locating an existing install is a sensible thing to ask for at all: no usable local
    /// client yet (nothing to play, or a failed/paused download the player would rather satisfy from an
    /// existing copy). Drives the COMMAND, including the copy of it in the settings panel, so it stays
    /// true even when the hero shows no second button.
    /// </summary>
    public bool CanLocate => State is LauncherState.NoClient or LauncherState.EraTransition
        or LauncherState.DownloadError or LauncherState.Paused;

    /// <summary>
    /// Whether the hero shows the quiet second "I already have WoW" block under the action button.
    ///
    /// <para>Not shown when the main button IS that action (<see cref="NeedsOwnClient"/>): the render on
    /// 2026-08-05 had "FIND MY CLIENT" in ember and "Locate installed WoW" in ghost grey directly
    /// underneath, two controls for one thing. The tests were all green - only the picture showed it.</para>
    /// </summary>
    public bool ShowLocate => CanLocate && !NeedsOwnClient;

    // ─── Active progression phase (drives client identity + era-transition UI) ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClientVersionText), nameof(PhaseName), nameof(EraName))]
    private string _phaseSlug = Progression.Default.Slug;

    public string PhaseName => _activePhase.DisplayName;
    public string EraName => _activePhase.Era;

    // ─── Expansion picker (user-facing era selection) ─────────────────────
    public IReadOnlyList<Expansion> Expansions => Expansion.All;

    [ObservableProperty] private Expansion _selectedExpansion;

    // ─── Client picker (what the play surface actually binds to) ──────────
    // One row per real client build the CURRENT OS can run. On Windows/Linux Vanilla contributes two
    // (1.12.1 and 1.14.2) plus the COMING-SOON Burning Crusade / Wrath tiles; on macOS the 32-bit legacy
    // builds have no path (owner scope 2026-07-23: Mac = 1.14.2 only), so the picker shows only 1.14.2
    // rather than offering a 1.12.1 the Mac can never start.
    public IReadOnlyList<ClientChoice> ClientChoices => ClientChoice.AllForCurrentOs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClientVersionText))]
    private ClientChoice _selectedClientChoice;

    /// <summary>Last successfully-applied row. Reverts land here, not on whatever the user just
    /// clicked - <see cref="SelectedClientChoice"/> already holds the rejected value by the time the
    /// partial method below runs.</summary>
    private ClientChoice _currentClientChoice;

    partial void OnSelectedClientChoiceChanged(ClientChoice value)
    {
        if (_suppressExpansionChange || value is null) return;

        if (!value.IsAvailable)
        {
            _log.Information("Client pick {Key} ignored: not available yet", value.Client.Key);
            RevertClientChoiceTo(_currentClientChoice);
            return;
        }

        // Same busy guard as the era picker (OnSelectedExpansionChanged): a running download owns
        // the era/build it started for, and switching underneath it produces a client verified
        // against the wrong hash or registered under a build it was never downloaded for.
        if (IsBusy && _lockedExpansion is not null)
        {
            _log.Information("Client switch to {Key} ignored: a client operation is running", value.Client.Key);
            RevertClientChoiceTo(_currentClientChoice);
            return;
        }

        _currentClientChoice = value;

        // Confirmed defect (owner investigation 2026-07-21): [ObservableProperty] skips the setter body
        // (and so OnSelectedExpansionChanged, and so ApplyExpansionAsync) when the new value equals the
        // old one - and Expansion is a record, so picking the OTHER Vanilla build (1.12.1 <-> 1.14.2)
        // hands it the SAME Expansion value. The picker moved (SelectedClientChoice really changed) but
        // nothing downstream re-resolved: _activePhase, ClientVersionText and the installed-client
        // lookup all kept pointing at whichever build was active before. That is exactly the
        // screenshot contradiction reported against Elwynn (badge said 1.12.1, the picker tile said
        // 1.14.2) - not a rendering artefact, a real stuck value. Re-apply explicitly whenever the
        // expansion itself did not change, since SelectedExpansion's own setter cannot be trusted to
        // notice a same-era, different-build pick.
        if (SelectedExpansion == value.Expansion)
        {
            _ = ApplyExpansionAsync(value.Expansion, persist: true, NewApplyToken());
        }
        else
        {
            SelectedExpansion = value.Expansion;
        }
    }

    /// <summary>Put the picker back on <paramref name="keep"/> without re-entering the switch path.</summary>
    private void RevertClientChoiceTo(ClientChoice keep)
    {
        if (ReferenceEquals(SelectedClientChoice, keep)) return;
        _suppressExpansionChange = true;
        SelectedClientChoice = keep;
        _suppressExpansionChange = false;
    }

    /// <summary>Per-era background (null = art not delivered yet → flat Stone backdrop shows through).</summary>
    [ObservableProperty] private Bitmap? _background;

    /// <summary>Per-era corner emblem (null = art not delivered yet → corner stays empty).</summary>
    [ObservableProperty] private Bitmap? _logo;
    private readonly Dictionary<string, Bitmap?> _logoCache = new();

    // ─── News rail (§5, INewsService) ─────────────────────────────────────
    public ObservableCollection<NewsItem> News { get; } = new();

    private async Task LoadNewsAsync()
    {
        try
        {
            var items = await _news.GetNewsAsync();
            News.Clear();
            foreach (var n in items.Take(NewsCount)) News.Add(n);
        }
        catch (System.Exception ex) { _log.Warning(ex, "Loading news failed"); }
    }

    /// <summary>How many entries the rail and the patch-notes page show. Ten is the owner's number:
    /// enough that "what changed lately" is answered without turning the page into an archive, and the
    /// archive is one click away on the website anyway.</summary>
    internal const int NewsCount = 10;

    /// <summary>
    /// Where a patch note points. Deliberately ONE destination for every entry.
    ///
    /// <para>Each entry in <c>news.json</c> used to carry its own <c>url</c>, and most of them pointed
    /// at <c>/news/&lt;slug&gt;</c> pages that do not exist: every click was a 404 in the player's
    /// browser (owner finding 2026-07-22, "bringt mich auf komische Seiten"). The changelog is the one
    /// page that is actually maintained, and it lists <em>all</em> realms with a picker, so a player
    /// lands somewhere true no matter which realm they play.</para>
    /// </summary>
    internal static string ChangelogUrl(string? siteBaseUrl) =>
        (string.IsNullOrWhiteSpace(siteBaseUrl) ? "https://stonetavern.app" : siteBaseUrl.TrimEnd('/'))
        + "/changelog";

    [RelayCommand]
    private void OpenNews(NewsItem? item)
    {
        var url = ChangelogUrl(_config.Load().SiteBaseUrl);
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.Exception ex) { _log.Warning(ex, "Opening the changelog failed: {Url}", url); }
    }

    // Only three expansions → cache each decoded Bitmap once and reuse the instance. Avoids both
    // re-decoding on every switch AND the "dispose during cross-fade" hazard (the outgoing layer
    // keeps rendering the old instance for 240ms). An Avalonia Bitmap holds unmanaged GPU memory,
    // but three stable instances for the app's lifetime is ~30 MB worst case — fine (Hermes Q2).
    private readonly Dictionary<string, Bitmap?> _bgCache = new();

    /// <summary>
    /// The expansion the running long operation (download/verify/extract/launch) was started for.
    /// Non-null exactly while such an operation owns the era, and the picker is frozen to it.
    /// </summary>
    private Expansion? _lockedExpansion;

    partial void OnSelectedExpansionChanged(Expansion value)
    {
        // Programmatic sync from server phase must not re-trigger the user-driven path.
        if (_suppressExpansionChange || value is null) return;

        // A running download OWNS the era: its url, hash and target build were captured from
        // _activePhase, and ApplyExpansionAsync would overwrite all three mid-flight. That produced a
        // finished multi-GB download verified against the NEW hash (mismatch, 5 GB deleted) or, worse,
        // an extract registered under a build that was never downloaded. The picker is disabled in the
        // v3 shell while busy; this is the second line of defence for every other entry point.
        if (IsBusy && _lockedExpansion is not null)
        {
            _log.Information("Expansion switch to {Exp} ignored: a client operation is running", value.Id);
            RevertExpansionTo(_lockedExpansion);
            return;
        }

        // v1 (PlayView.axaml) binds SelectedExpansion directly - it has no client-build picker of its
        // own, that whole concept postdates it. Without this, switching era there left
        // SelectedClientChoice on whatever build was active before (e.g. still 5875/Vanilla after
        // picking TBC), and every read this package now keys off SelectedClientChoice - the
        // download-coordinate guard in ApplyExpansionAsync would then see a build that does not match
        // the new phase and empty the URL, so v1 could no longer download anything after an era switch
        // (Codex Finding 2, 2026-07-22). Bring it along to the era's own first listed build whenever
        // the current pick no longer belongs to the new era; a pick already in this era (the v3 toggle,
        // or v1 re-selecting the same era) is left exactly as it is.
        if (SelectedClientChoice is null || SelectedClientChoice.Expansion != value)
        {
            var firstOfEra = ClientChoice.AllForCurrentOs.FirstOrDefault(c => c.Expansion == value);
            if (firstOfEra is not null)
            {
                _suppressExpansionChange = true;   // do not re-enter OnSelectedClientChoiceChanged
                SelectedClientChoice = firstOfEra;
                _currentClientChoice = firstOfEra;
                _suppressExpansionChange = false;
            }
        }

        _ = ApplyExpansionAsync(value, persist: true, NewApplyToken());
    }

    /// <summary>Put the picker back on <paramref name="keep"/> without re-entering the switch path.</summary>
    private void RevertExpansionTo(Expansion keep)
    {
        if (ReferenceEquals(SelectedExpansion, keep)) return;
        _suppressExpansionChange = true;
        SelectedExpansion = keep;
        _suppressExpansionChange = false;
    }

    /// <summary>
    /// True while a long client operation owns the era and the realm. The v3 shell binds the expansion
    /// picker and the realm rail to <c>!CanSwitchContext</c> so the switch cannot even be attempted;
    /// the guards in the setters cover every other caller.
    /// </summary>
    public bool CanSwitchContext => !IsBusy;

    /// <summary>Cancel any in-flight expansion switch and hand out a fresh token for the new one
    /// (C2/H1: the fire-and-forget path has no IsBusy gate; the token makes the latest pick win).</summary>
    private CancellationToken NewApplyToken()
    {
        _applyCts?.Cancel();
        _applyCts = new CancellationTokenSource();
        return _applyCts.Token;
    }

    // ─── Realm status (HeroStage PulseDot) ────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RealmStatusText), nameof(RealmOnline),
        nameof(RealmOffline), nameof(RealmChecking))]
    private RealmState _realm = RealmState.Checking;

    // FIX 3 (v1.0.1): PlayerCount stays as live realm state (still fetched from the status endpoint)
    // but is NEVER shown — the momentary count is dishonest UX (a small number scares players off,
    // owner directive, consistent with the website /play). The ShowPlayerCount / PlayerCountText
    // display members and the "N Spieler" TextBlock were removed; only the up/down status remains.
    [ObservableProperty]
    private int _playerCount;

    [ObservableProperty] private string _realmAddress;

    public bool RealmOnline => Realm == RealmState.Online;
    public bool RealmOffline => Realm == RealmState.Offline;
    public bool RealmChecking => Realm == RealmState.Checking;

    public string RealmStatusText => Realm switch
    {
        RealmState.Online => Loc.T("Play_Realm_Online"),
        RealmState.Offline => Loc.T("Play_Realm_Offline"),
        _ => Loc.T("Play_Realm_Checking") + "…",
    };

    // ─── ActionBar projection (§6) ────────────────────────────────────────
    // Reads the exact client build (SelectedClientChoice), not the server progression phase
    // (_activePhase): the phase only distinguishes 5875/8606/12340, so it cannot tell 1.12.1 apart
    // from 1.14.2 (both "vanilla") - the mismatch that caused the badge/footer contradiction this
    // package fixed (owner investigation 2026-07-21).
    public string ClientVersionText => Loc.F("Play_ClientVersion", SelectedClientChoice.Client.ShortLabel);

    // Glyph prefixes (✓ / ⚠ / …) stay in code, not in the catalogs: they are typography, not language,
    // and a translator cannot lose them by accident.
    public string StatusLine => State switch
    {
        LauncherState.Initializing => Loc.T("Play_Status_Connecting") + "…",
        LauncherState.UpdatingLauncher => Loc.T("Play_Status_UpdatingLauncher") + "…",
        // Without a managed source the launcher cannot fetch this build - say that in a sentence the
        // player can act on, and name the build. The old line ("No managed download for this client")
        // described the launcher's plumbing, not the player's situation, and it was the ONLY text on
        // screen for a self-added realm: the shell renders StatusLine, never SubLine (owner finding
        // 2026-08-05, a grey button with no reason next to it).
        LauncherState.NoClient => _buildHasManagedSource
            ? Loc.T("Play_Status_NoClient")
            // ShortLabel, not PreciseLabel: the precise one carries its own "  ·  Classic Era" separator,
            // which read as a broken sentence in the middle of one (image check 2026-08-05).
            : Loc.F("Play_Status_BringYourOwn", SelectedClientChoice.Client.ShortLabel),
        LauncherState.EraTransition => Loc.F("Play_Status_EraTransition", _activePhase.DisplayName),
        LauncherState.Ready => "✓ " + Loc.T("Play_Status_Ready"),
        LauncherState.UpdateAvailable => Loc.T("Play_Status_UpdateAvailable"),
        LauncherState.Downloading => Loc.T("Play_Status_Downloading") + "…",
        LauncherState.Paused => Loc.T("Play_Status_Paused"),
        LauncherState.Verifying => Loc.T("Play_Status_Verifying") + "…",
        LauncherState.DownloadError => "⚠ " + (string.IsNullOrEmpty(DownloadErrorDetail)
            ? Loc.T("Play_Status_DownloadFailed")
            : DownloadErrorDetail),
        LauncherState.Launching => Loc.T("Play_Status_Launching") + "…",
        // Codex F6a: show the concrete launcher/stub failure text (e.g. the Linux/Wine or
        // "not supported on this OS" message) instead of a generic line, when we have one.
        LauncherState.LaunchFailed => "⚠ " + (string.IsNullOrEmpty(LaunchFailedDetail)
            ? Loc.T("Play_Status_LaunchFailed")
            : LaunchFailedDetail),
        _ => "",
    };

    public string SubLine => State switch
    {
        LauncherState.NoClient => _buildHasManagedSource
            ? Loc.F("Play_Sub_NoClient", _activePhase.ClientLabel)
            : Loc.F("Play_Sub_BringYourOwn", SelectedClientChoice.Client.PreciseLabel),
        LauncherState.EraTransition => Loc.F("Play_Sub_EraTransition", _activePhase.Era, _activePhase.ClientLabel),
        LauncherState.UpdateAvailable => Loc.T("Play_Sub_UpdateAvailable"),
        LauncherState.LaunchFailed => _wowPath,
        _ => "",
    };

    public string ActionPrimaryText => State switch
    {
        // No managed source: the button becomes the way OUT of the dead end instead of naming it.
        // "UNAVAILABLE" on a disabled button read as "this realm/client is unavailable" and offered
        // nothing to press - while the one thing that does work here (point the launcher at a client
        // that is already on the machine) sat further down in ghost grey.
        LauncherState.NoClient => _buildHasManagedSource ? Loc.T("Play_Cta_Download") : Loc.T("Play_Cta_Locate"),
        LauncherState.EraTransition => Loc.T("Play_Cta_NewEra"),
        LauncherState.UpdateAvailable => Loc.T("Play_Cta_Update"),
        LauncherState.DownloadError => Loc.T("Play_Cta_Retry"),
        LauncherState.Paused => Loc.T("Play_Cta_Resume"),
        LauncherState.Launching => Loc.T("Play_Cta_Starting") + "…",
        _ => Loc.T("Play_Cta_Play"),
    };

    public string ActionGlyph => State switch
    {
        // No glyph for the locate case. "⌕" rendered as a cross in the shipped serif face and read like
        // a cancel button (image check 2026-08-05) - a wrong picture is worse than none.
        LauncherState.NoClient => _buildHasManagedSource ? "⬇" : "",
        LauncherState.EraTransition => "⬇",
        LauncherState.UpdateAvailable => "⬇",
        LauncherState.DownloadError => "↻",
        LauncherState.Paused => "▶",
        _ => "▶",
    };

    /// <summary>True exactly when the active build has no manifest-backed download - a DOWNLOAD button
    /// here would refuse on an empty URL every time (Codex/owner Finding 4, 2026-07-22: a guaranteed
    /// dead click is not an honest state). Drives the action LABEL and the status text above.
    ///
    /// <para>It no longer disables the button (owner finding 2026-08-05). A realm someone added
    /// themselves has no manifest, so it can never download - but pointing the launcher at a client
    /// that is already installed works perfectly well, and that is what the button now does. A grey
    /// button reading UNAVAILABLE was a dead end with no reason and no way forward.</para></summary>
    public bool NeedsOwnClient => State == LauncherState.NoClient && !_buildHasManagedSource;

    public bool ActionEnabled =>
        State is LauncherState.Ready or LauncherState.EraTransition
            or LauncherState.UpdateAvailable or LauncherState.DownloadError or LauncherState.Paused
            or LauncherState.NoClient;

    // The progress bar stays on while paused (frozen at its last percent) so the player sees exactly
    // where a resume will pick up, not a blank slate.
    public bool ShowProgress => IsDownloading || IsPaused;

    // Repair makes sense once a client for this build exists; check-for-updates whenever idle.
    public bool CanRepair => (State is LauncherState.Ready or LauncherState.UpdateAvailable)
        && !string.IsNullOrEmpty(_downloadUrl);
    public bool CanCheckUpdates => State is LauncherState.Ready or LauncherState.UpdateAvailable
        or LauncherState.NoClient or LauncherState.EraTransition or LauncherState.DownloadError;

    // ─── Download progress ────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadPercentText), nameof(FillWidth))]
    private double _downloadProgress;
    [ObservableProperty] private string _downloadDetail = "";

    /// <summary>Prominent percentage for the download surface (the 20-minute moment).</summary>
    public string DownloadPercentText => $"{DownloadProgress:F0}%";

    /// <summary>
    /// v2 skin: the action slab IS the progress bar, so the fill is measured in pixels of the
    /// 300px slab rather than drawn as a separate control (Styles.v2.axaml).
    /// </summary>
    public double FillWidth => 300.0 * System.Math.Clamp(DownloadProgress, 0, 100) / 100.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    private string _downloadErrorDetail = "";

    /// <summary>Concrete failure text from the platform launcher (stub/Wine/Process.Start) — shown
    /// in the LaunchFailed status line instead of a generic message (Codex F6a).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    private string _launchFailedDetail = "";

    /// <summary>WP4: passive "a newer launcher exists" hint (Linux Check + Notify — no auto-apply until
    /// WP7). Empty on Windows (Windows auto-applies) and whenever the manifest advertises no newer Linux
    /// build. Orthogonal to <see cref="LauncherState"/> — it does not gate any action, it only informs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLauncherUpdateHint))]
    private string _launcherUpdateHint = "";

    public bool HasLauncherUpdateHint => !string.IsNullOrEmpty(LauncherUpdateHint);

    // ─── Language ─────────────────────────────────────────────────────────

    /// <summary>The languages the INSTALLED client can actually be started in. Not a fixed list: the
    /// 1.14.2 package carries ten of them, and which ones is written in the installation itself. A
    /// language the client does not carry must never reach the menu — picking one kills the client on
    /// ERROR #134 before a window appears, which reads to a player like a broken install.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLanguageChoice))]
    private IReadOnlyList<LocaleInfo> _availableLocales = ClientLocales.SupportedLocales;

    /// <summary>Whether there is anything to choose. A picker with one entry is a control that lies
    /// about being a choice, so the surface hides it entirely for a client that ships one language.</summary>
    public bool HasLanguageChoice => AvailableLocales.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedLocaleCode))]
    private LocaleInfo _selectedLocale;

    /// <summary>
    /// The picked language as its CODE, which is what the picker binds to.
    ///
    /// <para>Binding the object itself does not survive the list being replaced: the control drops a
    /// selection whose instance is no longer in its ItemsSource and writes the null back, and the box
    /// then renders empty while this view model still holds the right language. A code is matched by
    /// value, so a rebuilt list keeps the selection (Linux end-to-end run, 2026-08-04 - the value was
    /// correct in every log line and the box was blank on screen).</para>
    /// </summary>
    public string SelectedLocaleCode
    {
        get => SelectedLocale?.Code ?? ClientLocales.Default.Code;
        set
        {
            // The control clears its selection while re-templating; that is not the player choosing
            // "no language".
            if (string.IsNullOrEmpty(value) || value == SelectedLocale?.Code) return;
            SelectedLocale = ClientLocales.FromCode(value);
        }
    }

    /// <summary>The saved language, or English when the config cannot be read. Never throws: this runs
    /// while a property is changing, and a language menu is not worth a crashed view model.</summary>
    /// <summary>Die eigene Sprache der Oberfläche, oder leer für „der Spielsprache folgen". Fehler
    /// beim Lesen bedeuten „keine eigene" — dann verhält sich der Launcher wie vor der Trennung.</summary>
    private string SafeLauncherLanguage()
    {
        try { return _config.Load().LauncherLanguage ?? ""; }
        catch (Exception ex)
        {
            _log.Debug(ex, "Launcher-Sprache nicht lesbar — folgt der Spielsprache");
            return "";
        }
    }

    private string SafeConfigLocale()
    {
        try { return _config.Load().Locale; }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not read the saved language; falling back to English");
            return ClientLocales.Default.Code;
        }
    }

    /// <summary>True while a language pack is being fetched or moved into place. Guards re-entry: the
    /// switch reverts <see cref="SelectedLocale"/> when it fails, and that assignment comes straight
    /// back through this handler.</summary>
    private bool _applyingLocale;

    /// <summary>What the language switch is doing right now, or why it did not. Empty when nothing is
    /// happening — a permanently visible status line about a language nobody is changing is noise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanguageStatus))]
    private string _languageActivity = "";

    /// <summary>
    /// Why the language menu is as short as it is. Unlike <see cref="LanguageActivity"/> this is not an
    /// event but a standing fact about the selected realm, so it stays on screen.
    ///
    /// <para><b>The finding it closes</b> (owner, 2026-08-05, same root as the grey UNAVAILABLE button).
    /// For 1.12.1 a language is a pack the SERVER publishes; a realm the player added has no manifest,
    /// so there are no packs, and the menu shrank to whatever happened to be installed - two entries
    /// next to Stonetavern's five, with nothing saying why. Shortening the menu is correct: offering a
    /// language that cannot be delivered would fail at the moment of picking it. Shortening it in
    /// silence is not.</para>
    /// </summary>
    private string _languageNote = "";

    /// <summary>What the surface shows next to the language picker: whatever is happening right now,
    /// otherwise the standing reason the menu is short. Bound as <c>Play.LanguageStatus</c>.</summary>
    public string LanguageStatus =>
        LanguageActivity.Length > 0 ? LanguageActivity : _languageNote;

    // ─── Ein Sprachpaket, das nachgebessert wurde ─────────────────────────
    // Ein Paket wird einmal installiert und danach nie wieder angesehen: der Wechsel fragt "liegt die
    // Datei da" und laedt dann nicht. Fuer einen Wechsel ist das richtig, fuer eine FEHLERBEHEBUNG
    // falsch. Wer deDE einmal hat, behaelt es fuer immer - kein Fehler, keine Meldung, die Sprache
    // gilt als installiert, und das ist sie ja auch, nur eben nicht die veroeffentlichte.
    //
    // Nichts daran ist eine Zustellung "an die deutschen Clients". Das Manifest ist fuer alle gleich;
    // jeder Launcher vergleicht nur die Sprachen, die er WIRKLICH auf der Platte hat. Ein englischer
    // Spieler laedt nichts, und der Server erfaehrt nicht, wer welche Sprache spricht - er muss es
    // auch nicht wissen.

    /// <summary>Die Sprache, fuer die gerade eine neuere Fassung angeboten wird, oder leer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLanguagePackUpdate), nameof(LanguagePackUpdateLabel))]
    [NotifyCanExecuteChangedFor(nameof(UpdateLanguagePackCommand))]
    private string _languagePackUpdateLocale = "";

    public bool HasLanguagePackUpdate => LanguagePackUpdateLocale.Length > 0;

    /// <summary>Die Sprache steht IM Knopf, nicht nur daneben: der Knopf sitzt in einer Leiste voller
    /// anderer Knoepfe, und "Aktualisieren" allein waere dort eine Frage statt einer Ansage.</summary>
    public string LanguagePackUpdateLabel => HasLanguagePackUpdate
        ? Loc.F("Language_Update_Button", ClientLocales.FromCode(LanguagePackUpdateLocale).DisplayName)
        : "";

    private bool CanUpdateLanguagePack => HasLanguagePackUpdate && !_applyingLocale && !IsBusy;

    /// <summary>
    /// Das installierte Paket durch das veroeffentlichte ersetzen. Nur auf Knopfdruck, nie von allein:
    /// es sind 90 MB, und eine Datei unter einem laufenden Spiel auszutauschen ist genau der Griff,
    /// den der Spieler bestimmen soll.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpdateLanguagePack))]
    private async Task UpdateLanguagePack()
    {
        var code = LanguagePackUpdateLocale;
        if (_languagePacks is null || code.Length == 0) return;
        var dir = Path.GetDirectoryName(_wowPathBacking);
        if (string.IsNullOrEmpty(dir)) return;

        _applyingLocale = true;
        try
        {
            LanguageActivity = Loc.T("Language_Downloading");
            var pack = _lastManifest?.LanguagePackFor(code, ClientVersion.Default.Build);
            var progress = new System.Progress<DownloadProgress>(p =>
            {
                DownloadProgress = p.Percentage;
                LanguageActivity = Loc.F("Language_Downloading_Percent", p.Percentage);
            });

            var result = await _languagePacks
                .UpdateAsync(dir, code, pack, GameIsRunning(), progress)
                .ConfigureAwait(true);

            LanguageActivity = result.Ok ? "" : (result.Error ?? Loc.T("Language_Failed"));
            if (!result.Ok)
                _log.Warning("The {Locale} language pack could not be updated: {Error}", code, result.Error);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Updating the {Locale} language pack failed", code);
            LanguageActivity = Loc.T("Language_Failed");
        }
        finally
        {
            _applyingLocale = false;
            RefreshAvailableLocales();
        }
    }

    /// <summary>
    /// Nachsehen, ob eine der WIRKLICH installierten Sprachen inzwischen nachgebessert wurde. Reine
    /// Auskunft aus dem zuletzt geholten Manifest: kein Netz, kein Download.
    ///
    /// <para>Unbekannte Herkunft zaehlt ausdruecklich NICHT als veraltet. Ein Paket von vor dem
    /// Herkunftsmarker ist wahrscheinlich in Ordnung, und jemanden auf Verdacht 90 MB laden zu lassen,
    /// kostet echte Bandbreite fuer ein Vielleicht.</para>
    /// </summary>
    private void RefreshLanguagePackUpdate(string? clientDir)
    {
        if (_languagePacks is null || string.IsNullOrEmpty(clientDir))
        {
            LanguagePackUpdateLocale = "";
            return;
        }

        try
        {
            foreach (var code in _languagePacks.Installed(clientDir))
            {
                var pack = _lastManifest?.LanguagePackFor(code, ClientVersion.Default.Build);
                if (_languagePacks.State(clientDir, code, pack) == LanguagePackState.Outdated)
                {
                    LanguagePackUpdateLocale = code;
                    return;
                }
            }
            LanguagePackUpdateLocale = "";
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not check the installed language packs against the manifest");
            LanguagePackUpdateLocale = "";
        }
    }

    partial void OnSelectedLocaleChanged(LocaleInfo value)
    {
        if (_applyingLocale) return;

        var c = _config.Load();
        c.Locale = value.Code;
        _config.Save(c);
        // The launcher has no language of its own: it follows the game. A player who set the game to
        // German did not mean "but keep the launcher English", and a second picker for the same
        // intent is one more control than anybody wants.
        // Nur mitziehen, wenn der Spieler der Oberflaeche keine eigene Sprache gegeben hat.
        // Sonst wuerde eine Client-Umstellung eine bewusste Entscheidung stillschweigend ueberschreiben.
        if (string.IsNullOrWhiteSpace(SafeLauncherLanguage()))
            Loc.Use(value.Code);
        _log.Information("Locale → {Locale}", value.Code);

        // 1.12.1 carries its language in an MPQ that has to be downloaded and moved into the patch
        // slot; 1.14.2 carries all ten inside the package and needs nothing but the config key the
        // launch path writes.
        if (WholeClientLocales.Contains(value.Code, StringComparer.Ordinal))
        {
            // This language is a client of its own. Nothing to move into a patch slot; the surface
            // has to re-resolve so it offers the download for THAT package instead.
            LanguageActivity = "";
            _ = ApplyExpansionAsync(SelectedExpansion, persist: false, NewApplyToken());
            return;
        }

        if (_languagePacks is not null && !CurrentClientIsModern)
            _ = ApplyVanillaLocaleAsync(value.Code);
    }

    /// <summary>The languages that arrive as a whole client of their own for 1.12.1, not as a pack.
    /// Russian is one: its localisation was built against a different client, and its interface files
    /// make our executable refuse to start. Picking such a language is a DOWNLOAD, not a switch.</summary>
    private IReadOnlyList<string> WholeClientLocales =>
        _lastManifestBacking?.ClientLocalesForBuild(ClientVersion.Default.Build) ?? [];

    /// <summary>Whether the client behind the current pick is the CASC-based 1.14.2 build. Read off the
    /// executable name rather than the picker, because the picker can move while the path has not.</summary>
    private bool CurrentClientIsModern =>
        !string.IsNullOrEmpty(_wowPathBacking)
        && ClientVersion.ExeNameNeedsModernRuntime(Path.GetFileName(_wowPathBacking));

    /// <summary>
    /// Put the picked language onto the 1.12.1 install, downloading its pack when it is not there yet.
    /// A failure reverts the picker to the language that IS active — leaving the menu showing German
    /// while the client starts in English is the plausible-but-wrong state this whole path exists to
    /// avoid.
    /// </summary>
    private async Task ApplyVanillaLocaleAsync(string code)
    {
        if (_languagePacks is null) return;
        var dir = Path.GetDirectoryName(_wowPathBacking);
        if (string.IsNullOrEmpty(dir)) return;

        _applyingLocale = true;
        try
        {
            var installed = _languagePacks.Installed(dir).Contains(code, StringComparer.Ordinal);
            LanguageActivity = installed
                ? Loc.T("Language_Switching")
                : Loc.T("Language_Downloading");

            var pack = _lastManifest?.LanguagePackFor(code, ClientVersion.Default.Build);
            var progress = new System.Progress<DownloadProgress>(p =>
            {
                DownloadProgress = p.Percentage;
                LanguageActivity = Loc.F("Language_Downloading_Percent", p.Percentage);
            });

            var result = await _languagePacks
                .EnsureAsync(dir, code, pack, GameIsRunning(), progress)
                .ConfigureAwait(true);

            if (result.Ok)
            {
                LanguageActivity = "";
                var cfg = _config.Load();
                cfg.Locale = result.Locale;
                _config.Save(cfg);
            }
            else
            {
                _log.Warning("The language could not be switched to {Locale}: {Error}", code, result.Error);
                LanguageActivity = result.Error ?? Loc.T("Language_Failed");
                SelectedLocale = ClientLocales.FromCode(result.Locale);
                var cfg = _config.Load();
                cfg.Locale = result.Locale;
                _config.Save(cfg);
            }

            RefreshAvailableLocales();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Switching the client language to {Locale} failed", code);
            LanguageActivity = Loc.T("Language_Failed");
        }
        finally
        {
            _applyingLocale = false;
        }
    }

    /// <summary>Whether a client of ours is up right now. Renaming a language MPQ under a running
    /// client either fails or, worse, succeeds and leaves the game reading a file nobody can find.</summary>
    private bool GameIsRunning()
    {
        try { return _gameDetector?.IsGameRunning(_wowPathBacking) == true; }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not tell whether the game is running; assuming it is not");
            return false;
        }
    }

    /// <summary>Re-read what the client on disk offers. Both builds are covered, by two different
    /// truths: the modern (1.14.2, CASC) build carries its languages inside the installation, while
    /// 1.12.1 takes its language from separately installed MPQ packs — so for that one the menu is what
    /// is installed plus what the server publishes a pack for.
    ///
    /// <para>If the current pick is not in the new list — the player switches from a client with ten
    /// languages to one with one — the selection falls back to English rather than staying on a
    /// language this client cannot deliver.</para></summary>
    private void RefreshAvailableLocales()
    {
        try
        {
            var path = _wowPathBacking;
            var dir = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
            IReadOnlyList<LocaleInfo> menu;
            var localOnly = false;   // the menu is limited to the installation, and nothing can be added
            if (string.IsNullOrEmpty(path) || dir is null)
            {
                menu = ClientLocales.SupportedLocales;
            }
            else if (ClientVersion.ExeNameNeedsModernRuntime(Path.GetFileName(path)))
            {
                menu = ClientLocales.ForInstalled(
                    WowLauncher.Services.Platform.InstalledClientLocales.ForClientDir(dir).Text);
            }
            else if (_languagePacks is not null)
            {
                // 1.12.1: what is on disk, plus what the server has a pack for. The second half is the
                // point — a language nobody has downloaded yet still belongs in the menu, because
                // picking it is how it gets downloaded.
                var offered = _lastManifestBacking?.LanguagePacks
                    .Where(p => p.Build is null || p.Build == ClientVersion.Default.Build)
                    .Select(p => p.Locale).ToList() ?? [];
                var whole = WholeClientLocales;
                // Nothing on offer at all = a realm the player added themselves (no manifest, so no
                // language_packs). The menu is then exactly what is installed, which is correct - and
                // has to be SAID, or it reads as a launcher that lost three languages (owner 2026-08-05).
                localOnly = offered.Count == 0 && whole.Count == 0;
                menu = ClientLocales.ForInstalled(
                    _languagePacks.Installed(dir).Concat(offered).Concat(whole));
            }
            else
            {
                menu = ClientLocales.SupportedLocales;
            }

            var note = localOnly ? Loc.T("Play_Language_LocalOnly") : "";
            if (!string.Equals(note, _languageNote, StringComparison.Ordinal))
            {
                _languageNote = note;
                OnPropertyChanged(nameof(LanguageStatus));
            }

            // Only when it ACTUALLY differs. Replacing the list makes the picker drop its own
            // selection, and this method runs more than once per start (client path, then manifest).
            // The second, identical rebuild was what left the box empty: same languages, new list
            // object, selection gone - and a view model that was right about every value.
            // 🔴 Was dabei herauskam, gehoert ins Protokoll. Am 2026-08-04 fehlte der Sprachwaehler
            // beim Owner, und von aussen war nicht zu unterscheiden, ob das Menue leer blieb, weil
            // nichts installiert ist, weil das Manifest noch nicht da war, oder weil dieser Aufruf
            // gar nicht lief. Drei verschiedene Fehler, ein identisches Bild: kein Auswahlfeld.
            // Der Zwischenstand kostet eine Zeile und beantwortet die Frage beim naechsten Start.
            _log.Information(
                "Sprachmenü: {Count} Einträge [{Codes}] · Client {Path} · Manifest {Manifest} · Pakete {Packs}",
                menu.Count, string.Join(",", menu.Select(l => l.Code)),
                string.IsNullOrEmpty(path) ? "(keiner)" : path,
                _lastManifestBacking is null ? "fehlt" : "da",
                _lastManifestBacking?.LanguagePacks.Count ?? 0);

            // 🔴 Ohne aufgeloesten Client ist `menu` der Platzhalter aus SupportedLocales — Englisch
            // und sonst nichts. Ihn in die LISTE zu schreiben ist derselbe Fehler wie die gespeicherte
            // Wahl daran zu reparieren, nur eine Stufe frueher und ueber die Oberflaeche statt ueber
            // den Code: der Waehler laesst eine Auswahl fallen, die in seiner neuen Liste nicht mehr
            // vorkommt, und schreibt Englisch zurueck durch die Bindung. Das ist ein echter Wechsel,
            // also wird er gespeichert — und die Sprache des Spielers ist weg.
            //
            // Der Riegel weiter unten deckte nur die Reparatur ab. Eine Seite des Mechanismus geaendert,
            // die andere stehen gelassen: gruen, unauffaellig, falsch.
            //
            // Gemessen am 2026-08-13 auf dem Mac des Owners, 1.14.2, beim Wechsel zwischen zwei Realms:
            //   11:06:22.624  Locale → deDE                                  (der Spieler waehlt)
            //   11:06:24.196  Sprachmenü: 1 Einträge [enUS] · Client (keiner) (ApplyExpansionAsync
            //                 setzt _wowPath = "" als Reset, dieser Aufruf haengt am Setter)
            //   11:06:24.328  Found build 42597 client · Sprachmenü: 5 Einträge
            //   11:06:25.902  Locale → enUS                                  (die Wahl ist weg)
            // Der Reset selbst ist richtig (H3/L2: eine geworfene Anwendung darf den Pfad der alten Ära
            // nicht stehen lassen). Falsch ist nur, daraus eine Aussage ueber die Sprachen zu machen.
            if (!string.IsNullOrEmpty(path)
                && !menu.Select(l => l.Code).SequenceEqual(AvailableLocales.Select(l => l.Code), StringComparer.Ordinal))
                AvailableLocales = menu;

            // Called here rather than from an OnAvailableLocalesChanged hook: the picker clears a
            // selection that is not in its list and writes the null back, and that null has to be
            // repaired from the SAVED language, not merely replaced by English. Doing it inline keeps
            // it on a path that provably runs.
            // Bei derselben Gelegenheit nachsehen, ob eine installierte Sprache nachgebessert wurde.
            // Hier und nicht anderswo: dies ist die Stelle, die nach jedem Wechsel, jedem
            // Manifest-Abruf und jedem Clientwechsel ohnehin laeuft.
            RefreshLanguagePackUpdate(dir);

            // 🔴 Ohne aufgeloesten Client ist `menu` KEINE Aussage darueber, was der Spieler haben
            // kann: es ist der Platzhalter aus SupportedLocales, und der kennt nur enUS. Die
            // gespeicherte Wahl daran zu "reparieren" loescht sie. Diese Methode laeuft beim Start
            // zweimal — einmal bevor der Client gefunden ist, 700 ms spaeter mit ihm (live
            // beobachtet 2026-08-12: "Sprachmenü: 1 Einträge [enUS] · Client (keiner)" gefolgt von
            // "Locale → enUS", danach das volle Menue mit fuenf Sprachen). Ergebnis war ein Spieler,
            // der bei JEDEM Start still von Deutsch auf Englisch gesetzt wurde. Solange kein Client
            // dasteht, wird nichts korrigiert und nichts gespeichert.
            if (string.IsNullOrEmpty(path)) return;

            var repaired = LocaleSelection.Repair(menu, SelectedLocale?.Code, SafeConfigLocale());
            if (repaired is null) return;

            if (SelectedLocale?.Code != repaired.Code)
            {
                if (SelectedLocale is not null)
                    _log.Information("This client does not offer {Locale}; falling back to {Fallback}",
                        SelectedLocale.Code, repaired.Code);
                SelectedLocale = repaired;      // a real change: save it, switch the interface language
            }
            else
            {
                // The value did not change, so the generated setter would raise NOTHING - and the
                // picker has meanwhile dropped its own selection because the list underneath it was
                // replaced. Without an unconditional notification the box sits there empty while this
                // view model is entirely correct about which language is picked. Deliberately do not
                // use the setter: nothing changed, so nothing should be saved or re-applied, only
                // re-shown.
                OnPropertyChanged(nameof(SelectedLocale));
            }
        }
        catch (Exception ex)
        {
            // A language menu is not worth a crashed view model. English is always right.
            _log.Debug(ex, "Could not read the installed client languages; offering English only");
            AvailableLocales = ClientLocales.SupportedLocales;
        }
    }

    // ─── Art loading (cached, WebP/PNG fallback, graceful-missing) ─────────
    private Bitmap? LoadBackground(Expansion exp) =>
        LoadCached(_bgCache, exp.Id, exp.BackgroundCandidates(), "background");
    private Bitmap? LoadLogo(Expansion exp) =>
        LoadCached(_logoCache, exp.Id, exp.LogoCandidates(), "logo");

    private Bitmap? LoadCached(Dictionary<string, Bitmap?> cache, string key,
        IEnumerable<string> candidates, string what)
    {
        if (cache.TryGetValue(key, out var hit)) return hit;

        Bitmap? bmp = null;
        foreach (var asset in candidates)
        {
            try
            {
                var uri = new System.Uri(asset);
                if (!AssetLoader.Exists(uri)) continue;
                using var s = AssetLoader.Open(uri);
                bmp = new Bitmap(s);
                break;
            }
            catch (System.Exception ex)
            {
                _log.Debug(ex, "{What} load failed: {Asset}", what, asset);
            }
        }
        if (bmp is null) _log.Debug("No {What} art for {Key} yet", what, key);
        cache[key] = bmp;
        return bmp;
    }

    // ─── Init: detect installs → resolve active expansion → realm + client → state ──
    public async Task InitAsync()
    {
        State = LauncherState.Initializing;
        Realm = RealmState.Checking;

        // News in parallel — non-critical, never blocks the play path (offline → cache/defaults).
        // 🔴 Patch Notes und die News-Leiste fliegen aus dem Launcher (Owner-Entscheid 2026-08-12).
        // Hier wird der DATENPFAD stillgelegt, nicht nur die Anzeige: ohne diesen Aufruf gibt es
        // keinen news.json-Abruf mehr, keinen Cache und keine Netzabhaengigkeit fuer etwas, das der
        // Spieler nicht mehr zu sehen bekommt. Die News-Sammlung bleibt leer, die Leiste rendert
        // dadurch nichts. Die dann toten Klassen (NewsService, PatchNotesViewModel) und die leere
        // Spalte in der Ansicht werden in einem eigenen Durchgang ausgebaut — eine stillgelegte
        // Quelle ist harmlos, ein halb ausgerissener Datenpfad nicht.
        // _ = LoadNewsAsync();

        var cfg = _config.Load();
        var managed = !string.IsNullOrWhiteSpace(cfg.ManifestUrl);

        // One-time discovery: find clients the player already has so a found older install can be
        // brought current instead of re-downloaded from scratch. New finds are merged + persisted.
        if (managed)
        {
            // Off the UI thread: discovery walks drive roots and reads file versions. On a machine with
            // a mapped network drive or a sleeping disk that blocks for seconds, and it ran BEFORE the
            // first await in this method, so the window painted and then froze. Task.Run returns to the
            // UI thread afterwards (no ConfigureAwait(false) here), so the config writes below stay
            // single threaded.
            var installs = new Dictionary<int, string>(cfg.ClientInstalls);
            var detected = await Task.Run(() => _client.DetectInstalls(installs));
            var changed = false;
            foreach (var (build, dir) in detected)
            {
                if (!cfg.ClientInstalls.TryGetValue(build, out var known) ||
                    !string.Equals(known, dir, System.StringComparison.OrdinalIgnoreCase))
                {
                    cfg.ClientInstalls[build] = dir;
                    changed = true;
                }
            }
            if (changed) { _config.Save(cfg); _log.Information("Persisted {N} detected install(s)", detected.Count); }
        }

        // Offline-first: start from the last known phase so client identity is sane pre-network.
        _activePhase = Progression.BySlug(cfg.LastPhase) ?? Progression.Default;

        // The manifest is the source of truth for the globally active phase + coordinates.
        var manifest = await _manifest.FetchAsync();
        _lastManifest = manifest;

        // Launcher self-update first: newer, hash-verified build? → swap + relaunch.
        //
        // 🔴 Gegen das STONETAVERN-Manifest, nicht gegen das des gewaehlten Realms (2026-08-05). Bis
        // dahin bekam ein Spieler, der einen eigenen Realm ausgewaehlt hatte, gar keine
        // Launcher-Updates mehr - lautlos, ohne Fehler, ohne Meldung. Begruendung ausfuehrlich an
        // IManifestService.FetchLauncherManifestAsync.
        var launcherManifest = await _manifest.FetchLauncherManifestAsync();
        if (await _update.CheckAndApplyAsync(launcherManifest))
        {
            State = LauncherState.UpdatingLauncher;
            await Task.Delay(500);
            System.Environment.Exit(0);
            return;
        }

        // Notify-only platforms (Linux) surface a passive launcher-update hint here — the auto-apply
        // above did nothing for them by design. No-op on Windows (CheckForNotice returns null).
        ApplyLauncherNotice(launcherManifest);

        // Decide the active expansion: explicit player pick wins; else the server's announced phase.
        Expansion exp;
        // The REALM decides first: it names the client build it speaks, and a saved era from an earlier
        // session must not survive a realm switch. Without this the hero contradicted itself on every
        // start - badge "Vanilla 1.12.1 (5875)" from the realm, active tile "Wrath of the Lich King",
        // action bar "Client 3.3.5a (12340)". One screen, three answers.
        var realmEra = Expansion.ById(RealmRegistry.Resolve(cfg).Client.EraId);
        if (realmEra is not null)
            exp = realmEra;
        else if (!string.IsNullOrWhiteSpace(cfg.SelectedExpansion))
            exp = Expansion.ById(cfg.SelectedExpansion);
        else if (manifest is not null && Progression.BySlug(manifest.ActivePhase) is { } resolved)
            exp = Expansion.ForPhase(resolved.Slug);
        else
            exp = Expansion.ForPhase(_activePhase.Slug);

        // Reflect the choice in the picker WITHOUT re-triggering the user-driven path.
        _suppressExpansionChange = true;
        SelectedExpansion = exp;
        SelectedClientChoice = ClientChoice.ForKey(RealmRegistry.Resolve(cfg).ClientKey);
        _currentClientChoice = SelectedClientChoice;
        _suppressExpansionChange = false;

        await ApplyExpansionAsync(exp, persist: false, NewApplyToken());
    }

    /// <summary>
    /// Re-resolve everything for the realm the player just picked in the rail: new realmlist, new
    /// manifest (or none, in simple mode), fresh realm ping and action state.
    ///
    /// <para>Deliberately NOT <see cref="InitAsync"/>: that one also runs the launcher self-update,
    /// which may exit the process. Clicking a realm must never restart the app.</para>
    /// </summary>
    public async Task SwitchRealmAsync()
    {
        // A realm switch resets State to Initializing, which would tear the progress UI off a live
        // download while the transfer kept running in the background. The rail is disabled while busy
        // (CanSwitchContext); this guard covers programmatic callers.
        if (IsBusy)
        {
            _log.Information("Realm switch ignored: a client operation is running ({State})", State);
            return;
        }

        var cfg = _config.Load();          // RealmRegistry has already projected the new realm into it
        RealmAddress = cfg.RealmlistAddress;
        State = LauncherState.Initializing;
        Realm = RealmState.Checking;

        // Follow the realm into its era. The realm names the client build it speaks
        // (RealmEntry.ClientKey), so leaving the picker where it was made the hero contradict itself:
        // the badge read "Vanilla 1.12.1 (5875)" from the realm while the active tile said "Wrath of
        // the Lich King" and the action bar reported a third build. One screen, three answers.
        // The player can still change the era afterwards; this only stops the switch from lying.
        var realmEra = Expansion.ById(RealmRegistry.Resolve(cfg).Client.EraId);
        _suppressExpansionChange = true;   // no second apply: the one below already covers it
        if (realmEra.Id != SelectedExpansion?.Id)
            SelectedExpansion = realmEra;
        SelectedClientChoice = ClientChoice.ForKey(RealmRegistry.Resolve(cfg).ClientKey);
        _currentClientChoice = SelectedClientChoice;
        _suppressExpansionChange = false;

        _lastManifest = await _manifest.FetchAsync();   // null in simple mode → no managed downloads
        // The 1.12.1 language menu is partly the manifest's answer (which packs the server publishes),
        // so it has to be rebuilt whenever a manifest lands — not only when the client path changes.
        RefreshAvailableLocales();
        await ApplyExpansionAsync(SelectedExpansion, persist: false, NewApplyToken());
    }

    /// <summary>
    /// Switch the launcher to an expansion: load its background, resolve realm + client-download
    /// coordinates from the manifest, ping the realm, and re-evaluate the action state for that
    /// build. Called both from init and from the player picking an expansion.
    /// </summary>
    private async Task ApplyExpansionAsync(Expansion exp, bool persist, CancellationToken ct = default)
    {
        try
        {
            // Reset transient per-era fields so a thrown apply can't leak the previous era's
            // client path / version into the UI or the next state resolution (H3/L2).
            _wowPath = "";
            _clientVersion = "";
            _filesManifestUrl = "";

            _activePhase = exp.Phase;
            PhaseSlug = _activePhase.Slug;
            OnPropertyChanged(nameof(ClientVersionText));
            OnPropertyChanged(nameof(PhaseName));
            OnPropertyChanged(nameof(EraName));
            Background = LoadBackground(exp);
            Logo = LoadLogo(exp);

            if (persist)
            {
                var c = _config.Load();
                c.SelectedExpansion = exp.Id;
                _config.Save(c);
                _log.Information("Expansion → {Exp}", exp.Id);
            }

            var cfg = _config.Load();
            var managed = !string.IsNullOrWhiteSpace(cfg.ManifestUrl);

            // Manifest phase entry for THIS expansion's phase (realm + client coords).
            var phaseEntry = _lastManifest?.Phases.FirstOrDefault(p => p.Phase == _activePhase.Slug);
            // The realm the PLAYER picked wins. The manifest may only move a preset that still carries
            // its shipped address (that is how the operator relocates Stonetavern without a new
            // launcher) — a realm someone added or edited themselves is never overridden, which is
            // exactly what used to happen: you typed an address, the manifest replaced it, and the
            // client connected somewhere else without a word. See Services/RealmBinding.cs.
            var selectedRealm = RealmRegistry.Resolve(cfg);
            var realmlist = RealmBinding.Effective(
                selectedRealm, RealmRegistry.ShippedAddress(selectedRealm.Id), phaseEntry?.Realmlist);
            RealmAddress = realmlist;

            // Download coordinates for the ACTIVE build, not for the era. A phase publishes its own
            // canonical build (5875 for vanilla) and may publish further builds beside it
            // (PhaseManifest.clients), which is how a realm that speaks both 1.12.1 and 1.14.2 can hand
            // out both. ClientForBuild keeps the narrow rule that matters: the phase's singular `client`
            // and the top-level `base` describe the CANONICAL build only and are never handed to a
            // different one - fetching the 1.12.1 zip for a 42597 pick would extract the wrong client
            // over a working install.
            //
            // No entry for this build = no managed source: the launcher says so plainly instead of
            // offering a download that can only fail. Simple mode (no ManifestUrl at all) has no managed
            // source for ANY build - a realm without a manifest cannot download or repair whatever
            // client it names.
            var activeBuild = SelectedClientChoice.Client.Build;
            // The language can pick a different PACKAGE, not just a different config key. Russian
            // 1.12.1 is its own client (its interface files make our executable refuse to start), so
            // for that pick the manifest hands out a whole other download. Every other language is an
            // add-on pack on top of the neutral package and resolves exactly as before.
            var coords = _lastManifest?.ClientForLocale(activeBuild, cfg.Locale)
                         ?? phaseEntry?.ClientForBuild(activeBuild, _activePhase.GameBuild)
                         ?? (activeBuild == _activePhase.GameBuild ? _lastManifest?.Base : null);

            BuildHasManagedSource = managed && !string.IsNullOrWhiteSpace(coords?.Url);
            if (BuildHasManagedSource)
            {
                _downloadUrl = coords!.Url;
                _downloadSha256 = coords.Sha256;
                _downloadSize = coords.Size;
                _filesManifestUrl = coords.FilesUrl ?? "";
                _clientVersion = !string.IsNullOrWhiteSpace(coords.Version)
                    ? coords.Version
                    // Only the canonical build may borrow the manifest's global current_version: that
                    // field describes the phase's own client, and lending it to a second build would
                    // compare one client's version against another's and report a phantom update.
                    : (activeBuild == _activePhase.GameBuild ? _lastManifest?.CurrentVersion ?? "" : "");
            }
            else
            {
                _downloadUrl = ""; _downloadSha256 = ""; _downloadSize = 0;
                _filesManifestUrl = ""; _clientVersion = "";
            }

            // Realm reachability ping (non-fatal).
            Realm = RealmState.Checking;
            try
            {
                // Hand the apply token down: an overtaken expansion pick used to keep its 3s TCP probe
                // and the HTTP call alive for nothing, and the stale answer then had to be discarded by
                // hand below. Cancelling it is both cheaper and the same rule the other services follow.
                var status = await _srv.CheckAsync(realmlist, ct: ct);
                if (ct.IsCancellationRequested) return; // a newer expansion pick superseded us
                Realm = status.Online ? RealmState.Online : RealmState.Offline;
                PlayerCount = status.PlayerCount;
            }
            catch (System.OperationCanceledException)
            {
                return; // superseded: the newer pick owns the realm dot, do not touch it
            }
            catch (System.Exception ex)
            {
                _log.Warning(ex, "Realm ping failed");
                Realm = RealmState.Offline;
            }

            // Only the latest pick commits the action state — a stale call must not clobber it (C2).
            if (ct.IsCancellationRequested) return;
            State = ResolveBuildState(cfg, managed);
            // Which languages exist depends on the build that is now active and on the install behind
            // it. The path setter refreshes the menu when the path CHANGES; this covers the case where
            // it did not (same client, different manifest or a pack installed since).
            RefreshAvailableLocales();
        }
        catch (System.Exception ex)
        {
            _log.Error(ex, "ApplyExpansion failed: {Exp}", exp.Id);
            if (!ct.IsCancellationRequested) Realm = RealmState.Offline; // M3: never leave PulseDot stuck on "Checking"
        }
    }

    /// <summary>
    /// Resolve the action state for the active build: do we have a client, is it current
    /// (§6.3 version compare), or does a new era need a different build?
    /// </summary>
    /// <summary>
    /// Resolve an installed exe for EXACTLY <paramref name="build"/>, or null. Never hands back a
    /// generic find that has not been verified to be that build.
    ///
    /// <para>Codex Finding 1 (2026-07-22): <see cref="IClientService.FindWowExeForBuild"/> is already
    /// build-safe (it rejects an exe name that can never be <paramref name="build"/>), but the caller
    /// used to fall back to the untargeted <see cref="IClientService.FindWowExe"/> whenever nothing was
    /// registered for that build - that fallback accepts ANY known exe name, so a realm needing 42597
    /// (1.14.2) with only a registered/found 5875 <c>WoW.exe</c> silently resolved to Ready and would
    /// have launched the wrong client against the wrong realm. The fallback here is the SAME generic
    /// search, but its result is only accepted once <see cref="IClientService.DetectBuild"/> on its
    /// directory PROVES it really is <paramref name="build"/> - simple mode (no per-build registration
    /// at all) goes through the identical check, not a separate "anything goes" path.</para>
    /// </summary>
    private string? ResolveInstalledExe(int build, LauncherConfig cfg)
    {
        var registered = _client.FindWowExeForBuild(build, cfg.ClientInstalls);
        if (registered is not null) return registered;

        var generic = _client.FindWowExe();
        if (generic is null) return null;

        var detected = _client.DetectBuild(Path.GetDirectoryName(generic) ?? "");
        return detected == build ? generic : null;
    }

    /// <summary>The cached <c>_wowPath</c>, but only when its file name is one this build ever ships
    /// as. <c>WoW.exe</c> can be 5875/8606/12340 but never 42597, and <c>WowClassic.exe</c> can only be
    /// 42597, so the name alone rules out a cross-build mixup - the case that actually matters here.
    /// Null when there is no cached path or it belongs to a different build.</summary>
    private string? VerifiedCachedPath(int build)
    {
        if (string.IsNullOrEmpty(_wowPath)) return null;
        var name = Path.GetFileName(_wowPath);
        return ClientVersion.ExeNameCanBeBuild(name, build) ? _wowPath : null;
    }

    private LauncherState ResolveBuildState(LauncherConfig cfg, bool managed)
    {
        // The exact build to look for: SelectedClientChoice (1.12.1/1.14.2/...), never
        // _activePhase.GameBuild - the progression phase only knows 5875/8606/12340, which conflates
        // Vanilla's two client builds into one number and would resolve/launch the wrong exe the
        // moment a realm's second client is actually in play (owner investigation 2026-07-21).
        var build = SelectedClientChoice.Client.Build;

        if (!managed)
        {
            _wowPath = ResolveInstalledExe(build, cfg) ?? "";
            return string.IsNullOrEmpty(_wowPath) ? LauncherState.NoClient : LauncherState.Ready;
        }

        _wowPath = ResolveInstalledExe(build, cfg) ?? "";

        if (string.IsNullOrEmpty(_wowPath))
        {
            // No client for this build. A *different* last PHASE means a real server-progression era
            // began (Vanilla -> TBC-Prepatch etc.) - still Progression-level, unaffected by which of a
            // single phase's client builds is active, so this stays reachable exactly as before.
            var lastPhase = Progression.BySlug(cfg.LastPhase);
            return Progression.RequiresClientSwitch(lastPhase, _activePhase)
                ? LauncherState.EraTransition
                : LauncherState.NoClient;
        }

        // §6.3 build/content comparison. We "know it's current" only if we recorded the version
        // at install time and it matches the manifest. A detected install we didn't create has no
        // recorded version → if the server offers a managed download, surface UpdateAvailable so a
        // found *older* client can be brought current cleanly (player data preserved on extract).
        var canUpdate = !string.IsNullOrEmpty(_downloadUrl);
        var known = cfg.InstalledClientVersions.TryGetValue(build, out var iv)
                    ? iv : "";
        var isCurrent = !string.IsNullOrEmpty(known)
                        && !string.IsNullOrEmpty(_clientVersion)
                        && string.Equals(known, _clientVersion, System.StringComparison.OrdinalIgnoreCase);

        // C3: a client we didn't install has no recorded version. WITHOUT a per-file content
        // manifest we can't prove its content is stale — so if its exe build matches the era's
        // expected build, treat it as current (Ready). Otherwise we'd nag EVERY player who brought
        // their own client to re-download ~5 GB on every launch. True content-staleness detection
        // needs the file-level manifest (Hermes Q1, tracked in STATUS). Repair stays available to
        // force a resync. Only a genuine recorded-version mismatch surfaces UpdateAvailable.
        if (!isCurrent && canUpdate && !string.IsNullOrEmpty(known))
            return LauncherState.UpdateAvailable; // recorded version differs from manifest → real update

        if (!isCurrent && canUpdate && _client.DetectBuild(Path.GetDirectoryName(_wowPath) ?? "") is int b
            && b == build)
            return LauncherState.Ready; // unknown provenance but the build matches → assume current

        return (isCurrent || !canUpdate) ? LauncherState.Ready : LauncherState.UpdateAvailable;
    }

    // ─── Commands ─────────────────────────────────────────────────────────
    [RelayCommand]
    private async Task SelectExpansion(Expansion exp)
    {
        if (exp is null || IsBusy) return;
        var ct = NewApplyToken();
        _suppressExpansionChange = true;
        SelectedExpansion = exp;
        _suppressExpansionChange = false;
        await ApplyExpansionAsync(exp, persist: true, ct);
    }

    [RelayCommand(CanExecute = nameof(ActionEnabled))]
    private async Task Play()
    {
        switch (State)
        {
            // No manifest behind this realm (or no entry for this build): a download can only fail,
            // but locating an existing install is exactly the right move - and it is what the button
            // now says. Same one action, whichever way the player reaches it.
            case LauncherState.NoClient when !_buildHasManagedSource:
                await LocateExistingClient();
                return;
            case LauncherState.NoClient:
            case LauncherState.EraTransition:
            case LauncherState.UpdateAvailable:
            case LauncherState.DownloadError:
            case LauncherState.Paused:   // Resume: DownloadAsync picks the .part bytes back up (HTTP Range)
                await DownloadAsync();
                return;
            default:
                await LaunchAsync();
                return;
        }
    }

    [RelayCommand(CanExecute = nameof(IsUpdateAvailable))]
    private Task Update() => DownloadAsync();

    /// <summary>Pause an in-flight download. The partial <c>.part</c> file stays on disk; the big action
    /// button then reads Resume and continues from those bytes via HTTP Range (DownloadService resume).
    /// Only pausable while bytes are actually flowing (<see cref="IsPausable"/>), never mid-verify.</summary>
    [RelayCommand(CanExecute = nameof(IsPausable))]
    private void PauseDownload()
    {
        _log.Information("Download pause requested at {Pct:F0}%", DownloadProgress);
        _downloadCts?.Cancel();
    }

    /// <summary>
    /// "I already have WoW": let the player point at an existing installation instead of downloading
    /// gigabytes again. Recognition is two-tier — first a client executable must be present in the
    /// folder, then (when the server publishes a per-file manifest for this build) the files are
    /// hash-verified against it. That confirms it is genuinely our client at the current version, not
    /// merely a file named WoW.exe, and in the SAME pass tells us whether it is complete and up to date:
    /// intact → Ready; incomplete or outdated → UpdateAvailable so the player can top it up. No extra
    /// download for recognition — the file manifest is the tiny one Repair already uses.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLocate))]
    private async Task LocateExistingClient()
    {
        if (IsBusy)
        {
            _log.Warning("Locate ignored: an operation is already running ({State})", State);
            return;
        }

        var build = SelectedClientChoice.Client.Build;
        var picked = await _folderPicker.PickFolderAsync(Loc.T("Play_Locate_Title"));
        if (picked is null)
        {
            _log.Information("Locate cancelled for build {Build}", build);
            return; // cancel is not an error — leave the state exactly as it was
        }

        // Tier 1: is there a client executable in there at all (name-level recognition)?
        var exe = _client.FindWowExe(picked);
        if (string.IsNullOrEmpty(exe))
        {
            _log.Information("No WoW client found under {Dir}", picked);
            DownloadErrorDetail = Loc.T("Play_Locate_NotFound");
            State = LauncherState.DownloadError;
            return;
        }
        var installDir = Path.GetDirectoryName(exe) ?? picked;

        // Identity BEFORE integrity (Codex P0, 2026-07-23): the located exe's NAME must be able to be the
        // ACTIVE build. WowClassic.exe can never be 5875 and WoW.exe can never be 42597, so this rejects
        // pointing a 1.12 realm at a 1.14 client (or the reverse) BEFORE anything is registered. The hash
        // verify further down only proves version/integrity — it never proves which realm the client
        // belongs to, so without this gate a foreign but "intact" tree could be adopted as Ready.
        var exeName = Path.GetFileName(exe);
        if (!ClientVersion.ExeNameCanBeBuild(exeName, build))
        {
            _log.Information("Located {Exe} cannot be build {Build} for this realm — rejected", exeName, build);
            DownloadErrorDetail = Loc.T("Play_Locate_WrongClient");
            State = LauncherState.DownloadError;
            return;
        }

        // Tier 2: hash-verify against the per-file manifest when the server publishes one — this both
        // proves it is our client and tells us if it is current, in one pass.
        if (!string.IsNullOrWhiteSpace(_filesManifestUrl))
        {
            State = LauncherState.Verifying;
            DownloadProgress = 0;
            DownloadDetail = Loc.T("Play_Detail_CheckingFiles") + "…";

            var fileManifest = await _manifest.FetchFileManifestAsync(_filesManifestUrl);
            if (fileManifest is not null && fileManifest.Files.Count > 0)
            {
                var progress = new System.Progress<VerifyProgress>(p =>
                {
                    DownloadProgress = p.Total > 0 ? 100.0 * p.Checked / p.Total : 0;
                    DownloadDetail = Loc.F("Play_Detail_CheckingFilesProgress", p.Checked, p.Total);
                });

                try
                {
                    var report = await _verify.VerifyAsync(installDir, fileManifest, progress);
                    RegisterLocated(build, installDir, exe, report.IsIntact ? _clientVersion : null);
                    if (report.IsIntact)
                    {
                        _log.Information("Located client verified intact for build {Build} at {Dir}", build, installDir);
                        State = LauncherState.Ready;
                        return;
                    }
                    _log.Information(
                        "Located client for build {Build} is not current ({Missing} missing, {Corrupt} corrupt) — update available",
                        build, report.Missing.Count, report.Corrupt.Count);
                    State = LauncherState.UpdateAvailable;
                    return;
                }
                catch (System.Exception ex)
                {
                    // An unwalkable tree is not proof it is wrong — register it by name and let the normal
                    // path drive updates, rather than rejecting a client the player really has.
                    _log.Warning(ex, "Verify of located client threw — registering by executable only");
                }
            }
        }

        // No per-file manifest (or it was unreachable): recognise by executable, register, and be honest
        // that we could not hash-verify the version here — the normal build comparison still applies.
        RegisterLocated(build, installDir, exe, version: null);
        State = LauncherState.Ready;
    }

    /// <summary>Record a client the player pointed us at: register its install dir for the build (so
    /// future starts find it), optionally its version (only when hash-verified as current), and adopt
    /// its exe as the live launch path.</summary>
    private void RegisterLocated(int build, string installDir, string exe, string? version)
    {
        var cfg = _config.Load();
        cfg.ClientInstalls[build] = installDir;
        // A recorded version is a claim of a KNOWN-current install. Set it only when the files hash-
        // verified as current; otherwise clear any stale entry (Codex P0, 2026-07-23) so a newly pointed
        // directory never inherits a previous install's version identity and looks up to date when it is
        // not — the normal manifest build/version comparison then re-evaluates it honestly.
        if (!string.IsNullOrEmpty(version)) cfg.InstalledClientVersions[build] = version;
        else cfg.InstalledClientVersions.Remove(build);
        _config.Save(cfg);
        _wowPath = exe;
        _log.Information("Registered located client for build {Build} at {Dir}", build, installDir);
    }

    /// <summary>Re-fetch the manifest and re-evaluate — explicit "Check for updates" affordance.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    private async Task CheckForUpdates()
    {
        _log.Information("Manual update check");
        var prev = State;
        // "No manifest configured" and "the manifest could not be reached" both come back as null, and
        // treating them the same made this button do NOTHING on a self-added realm (owner finding
        // 2026-08-05): it bailed out below before re-resolving anything. Simple mode has no server to
        // ask, but there is still something real to check - whether a client for this build turned up
        // on disk, and whether the realm answers - so it must fall through to the re-resolve instead
        // of returning. Only a CONFIGURED manifest that stays silent means offline.
        var simpleMode = string.IsNullOrWhiteSpace(_config.Load().ManifestUrl);
        State = LauncherState.Initializing;
        _lastManifest = await _manifest.FetchAsync();
        // Self-update may have appeared since launch. Wieder gegen Stonetavern, nicht gegen den
        // Realm: sonst tut dieser Knopf auf einem eigenen Realm fuer den Launcher gar nichts.
        var launcherManifest = await _manifest.FetchLauncherManifestAsync();
        if (await _update.CheckAndApplyAsync(launcherManifest))
        {
            State = LauncherState.UpdatingLauncher;
            await Task.Delay(500);
            System.Environment.Exit(0);
            return;
        }
        if (_lastManifest is null && !simpleMode) { State = prev; return; } // offline → keep prior state
        ApplyLauncherNotice(launcherManifest);
        await ApplyExpansionAsync(SelectedExpansion, persist: false, NewApplyToken());
    }

    /// <summary>Refresh the passive launcher-update hint from the manifest (Linux Check + Notify).
    /// On Windows the service returns null, so the hint clears — the visual state is unchanged.</summary>
    private void ApplyLauncherNotice(ServerManifest? manifest)
    {
        var notice = _update.CheckForNotice(manifest);
        if (notice is null)
        {
            LauncherUpdateHint = "";
            return;
        }
        LauncherUpdateHint =
            Loc.F("Play_LauncherUpdateHint", notice.Version, notice.DownloadPage);
        _log.Information("Launcher-Update-Hinweis (nur Notify, kein Auto-Apply): {Ver} — {Page}",
            notice.Version, notice.DownloadPage);
    }

    /// <summary>
    /// Repair the active build's client. Phase 1 (MANIFEST-SCHEMA.md §files_url): when the server
    /// publishes a per-file manifest, check the existing install against it FIRST — cheapest checks
    /// first (exists? → size matches? → only then hash) — and only fall back to re-downloading +
    /// re-extracting the whole multi-GB ZIP when a file actually turns out missing or damaged. No
    /// files_url published, or nothing installed yet to check → exactly today's behaviour: full
    /// re-download straight away. Extraction (whichever path gets there) overwrites client files but
    /// leaves user data (WTF/, Interface/AddOns, Screenshots, realmlist) untouched — they aren't in
    /// the base zip.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRepair))]
    private async Task Repair()
    {
        _log.Information("Repair requested for build {Build}", SelectedClientChoice.Client.Build);

        if (IsBusy)
        {
            _log.Warning("Repair request ignored: an operation is already running ({State})", State);
            return;
        }

        // The exact client build, not the progression phase (see ResolveBuildState) - the phase alone
        // cannot tell 1.12.1 apart from 1.14.2.
        var build = SelectedClientChoice.Client.Build;
        var cfg = _config.Load();
        var hasInstall = cfg.ClientInstalls.TryGetValue(build, out var installDir)
            && !string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir);

        // No per-file manifest for this build, or nothing on disk to check against → unchanged
        // behaviour, no new code path in play.
        if (string.IsNullOrWhiteSpace(_filesManifestUrl) || !hasInstall)
        {
            await DownloadAsync(repair: true);
            return;
        }

        var fileManifest = await _manifest.FetchFileManifestAsync(_filesManifestUrl);
        if (fileManifest is null || fileManifest.Files.Count == 0)
        {
            _log.Warning("File manifest unavailable or empty for build {Build} — falling back to full repair download", build);
            await DownloadAsync(repair: true);
            return;
        }

        State = LauncherState.Verifying;
        DownloadProgress = 0;
        DownloadDetail = Loc.T("Play_Detail_CheckingFiles") + "…";

        var progress = new System.Progress<VerifyProgress>(p =>
        {
            DownloadProgress = p.Total > 0 ? 100.0 * p.Checked / p.Total : 0;
            DownloadDetail = Loc.F("Play_Detail_CheckingFilesProgress", p.Checked, p.Total);
        });

        VerifyReport report;
        try
        {
            report = await _verify.VerifyAsync(installDir!, fileManifest, progress);
        }
        catch (System.Exception ex)
        {
            // An unreadable/unwalkable install tree is not proof of nothing being wrong with it —
            // fall back to the one path guaranteed to fix it rather than leaving the player stuck.
            _log.Error(ex, "Verify threw unexpectedly for build {Build} — falling back to full repair download", build);
            await DownloadAsync(repair: true, skipBusyGuard: true);
            return;
        }

        if (report.IsIntact)
        {
            _log.Information("Repair: {Ok} of {Total} files already intact for build {Build} — nothing to download",
                report.Ok, report.Total, build);
            State = LauncherState.Ready;
            return;
        }

        var brokenCount = report.Missing.Count + report.Corrupt.Count;
        _log.Information(
            "Repair: {Broken} of {Total} files need repair for build {Build} (missing {Missing}, corrupt {Corrupt}) — downloading a fresh client",
            brokenCount, report.Total, build, report.Missing.Count, report.Corrupt.Count);
        DownloadDetail = Loc.F("Play_Detail_RepairFoundDefects", brokenCount);
        await DownloadAsync(repair: true, skipBusyGuard: true);
    }

    /// <param name="skipBusyGuard">
    /// Repair sets <see cref="State"/> to <see cref="LauncherState.Verifying"/> itself while it checks
    /// the existing install file-by-file (also an <see cref="IsBusy"/> state), then falls through into
    /// this method on the SAME call stack once a real defect turns up. The busy guard below exists to
    /// stop a SECOND user click from starting a second run over a live one, not to stop the verify
    /// step that already owns the run from continuing it — so that one caller opts out.
    /// </param>
    private async Task DownloadAsync(bool repair = false, bool skipBusyGuard = false)
    {
        // Refuse to start a second run over a live one. IsBusy covers Downloading/Verifying/Launching,
        // which is exactly the window in which the fields below must not move.
        if (IsBusy && !skipBusyGuard)
        {
            _log.Warning("Download request ignored: an operation is already running ({State})", State);
            return;
        }

        // Captured before any state change: a Resume enters here with State == Paused and must keep the
        // progress bar where it was; a fresh start resets it (below, after the pre-flight guards).
        var resuming = State == LauncherState.Paused;

        if (string.IsNullOrEmpty(_downloadUrl))
        {
            _log.Warning("Download requested without a URL (no manifest reachable)");
            DownloadErrorDetail = DownloadResult.Fail(DownloadFailure.ServerError).UserMessage;
            State = LauncherState.DownloadError;
            return;
        }

        // Codex F1: a manifest entry without a SHA256 can NEVER verify — refuse BEFORE the (multi-GB)
        // download instead of failing afterwards with a misleading "Prüfsumme stimmt nicht — erneut
        // versuchen" (a retry would download 5 GB again and fail identically). The post-download
        // VerifyHashAsync below stays as the second line of defence.
        if (string.IsNullOrWhiteSpace(_downloadSha256))
        {
            _log.Error("Download refused: server manifest has no SHA256 for build {Build}", SelectedClientChoice.Client.Build);
            DownloadErrorDetail = Loc.T("Play_Error_NoSha");
            State = LauncherState.DownloadError;
            return;
        }

        // Never write client files while WoW holds locks on them (Hermes Q1).
        if (_client.IsGameRunning())
        {
            _log.Warning("Download/repair refused: WoW is running");
            DownloadErrorDetail = Loc.T("Play_Error_GameRunning");
            State = LauncherState.DownloadError;
            return;
        }

        // ── Capture every input ONCE. Everything below runs for minutes and must not read a field the
        // UI can still move. _activePhase / _downloadUrl / _downloadSha256 are exactly those fields.
        var phase = _activePhase;
        var activeClient = SelectedClientChoice;   // the exact build - see ResolveBuildState
        var build = activeClient.Client.Build;
        var url = _downloadUrl;
        var sha = _downloadSha256;
        var size = _downloadSize;
        var version = _clientVersion;

        var cfgPre = _config.Load();
        var isFreshInstall = !repair && !(cfgPre.ClientInstalls.TryGetValue(build, out var alreadyInstalled)
                                           && !string.IsNullOrWhiteSpace(alreadyInstalled));

        // First install of THIS build, never Repair, never Update (Update targets a build that already
        // has a ClientInstalls entry): ask the player where to put it. Exactly once per player, not once
        // per build — a PreferredInstallRoot already on record is reused silently below instead of
        // asking again.
        if (isFreshInstall && string.IsNullOrWhiteSpace(cfgPre.PreferredInstallRoot))
        {
            var picked = await _folderPicker.PickFolderAsync(Loc.T("Play_Picker_Title"));
            if (picked is null)
            {
                // Cancel is not an error: nothing started, nothing to undo, State is untouched.
                _log.Information("Install folder picker cancelled for build {Build} — download not started", build);
                return;
            }

            cfgPre.PreferredInstallRoot = picked;
            _config.Save(cfgPre);
        }

        // Where THIS download will land. Repair/Update reuse the recorded install dir once the download
        // has succeeded (computed again below from the just-reloaded config, in case the picker step
        // above changed it); a genuine first install goes under the player's chosen root — one
        // sub-folder per build (AppPathNames.ClientDirName) so two builds never collide — or the
        // launcher's own default when no root was ever chosen (headless/cancelled-then-retried-later).
        var installRoot = isFreshInstall
            ? (!string.IsNullOrWhiteSpace(cfgPre.PreferredInstallRoot)
                ? Path.Combine(cfgPre.PreferredInstallRoot!, WowLauncher.Services.Platform.AppPathNames.ClientDirName(build))
                : _paths.ClientInstallDir(build))
            : (cfgPre.ClientInstalls.TryGetValue(build, out var recorded) && !string.IsNullOrWhiteSpace(recorded)
                ? recorded
                : _paths.ClientInstallDir(build));

        // Writability + free-space check BEFORE the (multi-GB) transfer starts — the same "refuse
        // early, not after minutes of transfer" reasoning as the missing-SHA256 guard above.
        try
        {
            Directory.CreateDirectory(installRoot);
            var probe = Path.Combine(installRoot, $".wl-write-check-{System.Guid.NewGuid():N}");
            File.WriteAllBytes(probe, [0]);
            File.Delete(probe);
        }
        catch (System.Exception ex)
        {
            _log.Error(ex, "Install folder not writable: {Dir}", installRoot);
            DownloadErrorDetail = Loc.T("Play_Error_InstallDirNotWritable");
            State = LauncherState.DownloadError;
            return;
        }

        if (size > 0)
        {
            try
            {
                var driveRoot = Path.GetPathRoot(Path.GetFullPath(installRoot));
                if (!string.IsNullOrEmpty(driveRoot))
                {
                    var drive = new DriveInfo(driveRoot);
                    // The extracted client is materially larger than the compressed download, and we
                    // have no per-file manifest size here (that is Repair's optional files_url, not the
                    // base client entry) — a flat multiplier plus fixed headroom is a conservative,
                    // config-free stand-in: good enough to refuse BEFORE a multi-GB transfer that could
                    // never finish, rather than failing partway through with a half-written client.
                    var required = (long)(size * 2.2) + 500L * 1024 * 1024;
                    if (drive.AvailableFreeSpace < required)
                    {
                        _log.Warning(
                            "Not enough disk space for build {Build}: need ~{Need} MB, have {Have} MB on {Drive}",
                            build, required / 1_048_576, drive.AvailableFreeSpace / 1_048_576, driveRoot);
                        DownloadErrorDetail = Loc.T("Play_Error_NoDiskSpace");
                        State = LauncherState.DownloadError;
                        return;
                    }
                }
            }
            catch (System.Exception ex)
            {
                // A failed space probe is not proof of a failed download — log it and let the real
                // download surface any actual DiskIo failure instead of blocking on a guess.
                _log.Debug(ex, "Disk space check failed for {Dir} — proceeding without it", installRoot);
            }
        }

        _lockedExpansion = SelectedExpansion;   // freeze the picker for the duration (see OnSelectedExpansionChanged)

        // A fresh cancellation source for this run — the Pause button cancels it, which the download
        // service turns into DownloadFailure.Cancelled while keeping the .part bytes for a resume.
        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();
        var downloadToken = _downloadCts.Token;

        State = LauncherState.Downloading;
        // On a fresh start reset the bar to 0; on a Resume keep the last percent so it does not flash
        // back to 0 before the first (already-offset) progress callback lands.
        if (!resuming) DownloadProgress = 0;
        // WP3: download scratch → CacheDir, client install → ShareDir (Windows: both next to the exe,
        // byte-gleich). Ensure the cache dir exists before the download writes into it.
        var zipPath = _paths.ClientDownloadZip(build);
        try { Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!); } catch { /* CreateDirectory below on extract too */ }

        var progress = new System.Progress<DownloadProgress>(p =>
        {
            // Der Launcher hat die Verbindung verloren und holt sie sich selbst zurueck. Der
            // Fortschrittsbalken bleibt stehen, WO er steht -- die Prozentzahl auf 0 zu setzen waere
            // die Unwahrheit, die Bytes liegen ja noch da. Nur die Zeile darunter wechselt.
            if (p.Waiting is { } wait)
            {
                DownloadDetail = Loc.F("Play_Detail_Reconnecting",
                    (int)System.Math.Round(wait.In.TotalSeconds), wait.Attempt, wait.Of);
                return;
            }

            DownloadProgress = p.Percentage;
            // Groesse und Tempo, sonst nichts. Eine Restzeit stand hier bis 2026-07-21 mit dabei,
            // war aber nur so gut wie die Momentangeschwindigkeit: sie sprang bei jedem Ausreisser
            // und behauptete "0:01 left" mitten im Transfer. Eine Zahl, die der Spieler als
            // Versprechen liest, wird nicht geschaetzt (Owner-Entscheidung 2026-07-21).
            DownloadDetail = Loc.F("Play_Detail_Progress",
                $"{p.BytesDownloaded / 1_048_576.0:F0}",
                $"{p.TotalBytes / 1_048_576.0:F0}",
                $"{p.SpeedBytesPerSecond / 1_048_576.0:F1}");
        });

        try
        {
            // 🔴 Erst nachsehen, was schon da ist. Ein Rechner, der waehrend des Entpackens abstuerzt,
            // hat das vollstaendige, geprueffte Archiv noch im Zwischenspeicher liegen -- bis hier
            // wurde es trotzdem noch einmal geholt, weil niemand gefragt hat. Bei einem
            // Mehr-Gigabyte-Client ist das der Unterschied zwischen einer Minute und einem Abend.
            //
            // Die Pruefung ist der Beweis, nicht die Existenz der Datei: eine halb geschriebene Datei
            // liegt genauso da wie eine ganze. Stimmt der Hash nicht, wird sie weggeworfen und normal
            // geladen -- nie entpackt.
            var haveZip = false;
            if (File.Exists(zipPath) && !string.IsNullOrWhiteSpace(sha))
            {
                DownloadDetail = Loc.T("Play_Detail_TakingStock");
                if (await _download.VerifyHashAsync(zipPath, sha, downloadToken))
                {
                    _log.Information(
                        "Das Archiv fuer Build {Build} liegt schon vollstaendig und geprueft im Zwischenspeicher — kein Download",
                        build);
                    haveZip = true;
                    DownloadProgress = 100;
                }
                else
                {
                    _log.Information("Angefangenes Archiv fuer Build {Build} passt nicht zum Manifest — wird neu geladen", build);
                    try { File.Delete(zipPath); } catch { /* regenerierbar */ }
                }
            }

            var dl = haveZip ? DownloadResult.Success
                             : await _download.DownloadFileAsync(url, zipPath, progress, downloadToken);
            if (!dl.Ok)
            {
                if (dl.Failure == DownloadFailure.Cancelled)
                {
                    // The player pressed Pause: the .part bytes survive on disk, so this is a resumable
                    // stop, not a failure. The big action button now reads Resume (see ActionPrimaryText).
                    _log.Information("Download paused by the player at {Pct:F0}%", DownloadProgress);
                    State = LauncherState.Paused;
                    return;
                }
                DownloadErrorDetail = dl.UserMessage;
                State = LauncherState.DownloadError;
                return;
            }

            State = LauncherState.Verifying;
            DownloadDetail = (repair ? Loc.T("Play_Detail_VerifyRepair") : Loc.T("Play_Detail_Verify")) + "…";
            // haveZip heisst: GENAU diese Datei wurde eben gegen GENAU diesen Hash geprueft. Ein
            // zweiter Durchlauf ueber mehrere Gigabyte kostet Minuten und beweist nichts Neues.
            // Ohne haveZip bleibt die Pruefung das harte Tor -- unverifiziert wird nie entpackt.
            if (!haveZip && !await _download.VerifyHashAsync(zipPath, sha))
            {
                _log.Error("SHA256 check failed — download discarded, nothing was overwritten");
                try { File.Delete(zipPath); } catch { /* regenerable */ }
                DownloadErrorDetail = DownloadResult.Fail(DownloadFailure.HashMismatch).UserMessage;
                State = LauncherState.DownloadError;
                return;
            }

            DownloadDetail = Loc.T("Play_Detail_Extracting") + "…";
            // Repair and Update extract over the existing install dir; a fresh install uses installRoot
            // resolved above (the player's chosen root, or the launcher default). Re-checked against the
            // freshest config rather than trusting installRoot blindly, in case something else wrote
            // ClientInstalls for this build while the download was in flight.
            var cfgNow = _config.Load();
            var extractDir = !repair && isFreshInstall
                ? installRoot
                : cfgNow.ClientInstalls.TryGetValue(build, out var existing) && !string.IsNullOrWhiteSpace(existing)
                    ? existing
                    : installRoot;
            // ExtractClientAsync preserves WTF/, Interface/AddOns/, Screenshots/, realmlist.wtf —
            // a clean update/repair never clobbers player data (Hermes Q1 preserve-list).
            var extracted = await _download.ExtractClientAsync(zipPath, extractDir);
            try { File.Delete(zipPath); } catch { /* regenerable */ }

            if (!extracted) { State = LauncherState.DownloadError; return; }

            var installedExe = _client.FindWowExe(extractDir) ?? "";
            if (string.IsNullOrEmpty(installedExe)) { State = LauncherState.DownloadError; return; }

            // Register the install + its version so future starts know it's current (§6.3). Keyed off
            // the CAPTURED build, never the live field: registering a path under an era the player
            // switched to mid-download would point that era at a client it does not own.
            var cfg = _config.Load();
            cfg.ClientInstalls[build] = Path.GetDirectoryName(installedExe) ?? extractDir;
            if (!string.IsNullOrEmpty(version))
                cfg.InstalledClientVersions[build] = version;
            _config.Save(cfg);

            // Only adopt the resolved exe as the live path if the era/build did not move underneath us.
            if (ReferenceEquals(_activePhase, phase) || SelectedClientChoice.Client.Build == build)
                _wowPath = installedExe;

            State = LauncherState.Ready;
        }
        finally
        {
            _lockedExpansion = null;
        }
    }

    private async Task LaunchAsync()
    {
        if (IsBusy)
        {
            _log.Warning("Launch request ignored: an operation is already running ({State})", State);
            return;
        }

        _lockedExpansion = SelectedExpansion;   // the realmlist we are about to write belongs to THIS era
        try
        {
            await LaunchCoreAsync();
        }
        finally
        {
            _lockedExpansion = null;
        }
    }

    private async Task LaunchCoreAsync()
    {
        State = LauncherState.Launching;
        LaunchFailedDetail = "";
        var cfg = _config.Load();

        var build = SelectedClientChoice.Client.Build;
        // ResolveInstalledExe (never the raw, unverified FindWowExe fallback) - see its doc comment
        // for the exact silent failure this closes.
        //
        // The _wowPath last resort is verified too, not trusted. It is set by whichever
        // ResolveBuildState ran last, and "last" is not necessarily "for THIS build": the pick can move
        // between an apply and this launch, and it is exactly one stale field away from starting 1.12.1
        // against a 1.14.2 realm - the same defect Finding 1 closed one layer up. Re-checking here is a
        // file-name comparison, so the cost of being sure is nil.
        var wow = ResolveInstalledExe(build, cfg) ?? VerifiedCachedPath(build);
        if (string.IsNullOrEmpty(wow))
        {
            State = LauncherState.LaunchFailed;
            return;
        }

        var wowDir = Path.GetDirectoryName(wow) ?? ".";
        cfg.LastPhase = _activePhase.Slug;
        cfg.ClientInstalls[build] = wowDir;
        // Freeze the address THIS launch uses into the config, because the launch path has three more
        // consumers that each have to agree about it and none of them can see this view model: the
        // client's own realmlist.wtf (written just below), the loader script's REALMLIST, and the 1.14.2
        // proxy's ServerAddress. One field, one truth — see Services/RealmBinding.cs.
        cfg.RealmlistAddress = RealmAddress;
        _config.Save(cfg);

        // For 1.12.1 the language is a file in a patch slot, not a config key, and the slot is the only
        // thing the client actually reads. Applied here as well as on selection because the two can
        // drift apart without anyone touching the picker: a client update rewrites Config.wtf, a repair
        // re-extracts Data/. No download happens here — a pack that is not installed simply leaves the
        // launch on the language that IS active, rather than holding up the game behind 90 MB.
        var launchLocale = cfg.Locale;
        if (_languagePacks is not null
            && !ClientVersion.ExeNameNeedsModernRuntime(Path.GetFileName(wow))
            // A language that IS its own client carries its language inside the install. Running it
            // through the pack machinery would find no pack, report a failure, and start the game in
            // English while the player picked Russian.
            && !WholeClientLocales.Contains(cfg.Locale, StringComparer.Ordinal))
        {
            var applied = await _languagePacks.EnsureAsync(wowDir, cfg.Locale, pack: null);
            launchLocale = applied.Locale;
            if (!applied.Ok)
            {
                _log.Warning("Starting in {Locale} instead of {Wanted}: {Error}",
                    applied.Locale, cfg.Locale, applied.Error);
                // Auch SAGEN, nicht nur loggen. Der Verzicht auf den Download oben ist gewollt, das
                // stille Sprachwechseln nicht: sonst startet das Spiel unerklaerlich auf Englisch,
                // obwohl im Launcher weiter Deutsch steht, und der Spieler haelt es fuer einen Fehler
                // im Spiel statt fuer ein fehlendes Sprachpaket, das ein Klick nachlaedt (2026-08-12).
                LanguageActivity = Loc.F("Language_FellBackTo", applied.Locale);
            }
        }

        _client.ConfigureClient(wowDir, launchLocale, RealmAddress);
        var result = await _client.LaunchAsync(wow);

        // Grace gate (PLAN §4): on Windows the policy confirms immediately. On Linux/Wine a successful
        // start is NOT proof the client runs (wine often starts and dies at once), so the policy holds a
        // short grace window and confirms a live client process; otherwise we surface a launch failure
        // instead of hiding a game that never came up.
        if (result.Started && await _launchExit.ConfirmClientRunningAsync(wow))
        {
            // The launcher used to hard-exit here. Now it hides into the tray and stays alive so it can
            // watch the game end and reap the 1.14.2 proxy itself (proxy-lifetime reversal, see
            // IGameSession). Both builds hide; only 1.14.2 has a proxy to stop. The client is confirmed
            // up, so the surface is Ready again for the next play once the window comes back.
            State = LauncherState.Ready;
            if (_windowController is { IsConfigured: true })
            {
                _windowController.HideToTray();
                _ = MonitorGameExitAsync(wow);
            }
            else
            {
                // No tray to hide into (headless/e2e, or no tray host) → keep the shipped hard exit
                // rather than orphaning an invisible process.
                System.Environment.Exit(0);
            }
            return;
        }

        // Codex F6a: keep the launcher's concrete failure text so the UI can show it (the stub/Wine
        // launcher says WHY it can't start instead of a generic error). If the start itself succeeded
        // but the grace window saw no live client, explain that specific case.
        _log.Error("WoW.exe start failed: {Path}: {Error}", wow, result.Error);
        LaunchFailedDetail = result.Started
            ? Loc.T("Play_Error_WineNotPersistent")
            : result.Error ?? "";
        State = LauncherState.LaunchFailed;
    }

    /// <summary>
    /// After the launcher has hidden into the tray, wait for the client to quit, let the session reap
    /// the 1.14.2 proxy (it never stops the proxy while the game is still running), then bring the
    /// launcher back so the player can play again. Fire-and-forget: it must not block the Play command,
    /// and any failure here is a log line, never a crash. For 1.12.1 there is no proxy — the session
    /// simply waits out the game and returns, and the window is restored all the same.
    /// </summary>
    private async Task MonitorGameExitAsync(string clientExePath)
    {
        try
        {
            if (_session is not null)
                await _session.MonitorUntilExitAsync(clientExePath);
            // RestoreFromTray is thread-safe (App marshals to the UI thread), so it is fine to call it
            // from this background continuation.
            _windowController?.RestoreFromTray();
        }
        catch (System.Exception ex)
        {
            _log.Warning(ex, "Game-exit watchdog failed");
        }
    }

    [RelayCommand]
    private void OpenLogs()
    {
        try
        {
            var log = Path.Combine(_paths.LogDir, "launcher.log");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(log) { UseShellExecute = true });
        }
        catch (System.Exception ex) { _log.Warning(ex, "Opening logs failed"); }
    }
}
