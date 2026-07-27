namespace WowLauncher.Models;

public sealed class LauncherConfig
{
    // ─── Active connection (projected from the selected realm) ────────────────
    // These stay as the launcher's flat "effective" values so services read them
    // unchanged; RealmRegistry.ApplyActiveRealm keeps them in sync on load.
    public string RealmlistAddress { get; set; } = "play.stonetavern.app";
    public string WowExecutablePath { get; set; } = "WoW.exe";
    public string ManifestUrl { get; set; } = "https://downloads.stonetavern.app/manifest.json";
    public string PatchServerBaseUrl { get; set; } = "https://downloads.stonetavern.app";

    /// <summary>
    /// Where the Stonetavern ACCOUNT API lives (login, friends, armory). Empty = the built-in default
    /// (<see cref="Services.ApiEndpoints.DefaultBase"/>). Only meant for a test server.
    ///
    /// <para>🔴 This must never be derived from the selected realm. See
    /// <see cref="Services.ApiEndpoints"/> for the token-leak this prevents.</para>
    /// </summary>
    public string AccountApiBaseUrl { get; set; } = "";

    /// <summary>
    /// Where the Stonetavern WEBSITE lives, used for the changelog link behind a patch note. Empty =
    /// the built-in default.
    /// </summary>
    public string SiteBaseUrl { get; set; } = "https://stonetavern.app";

    /// <summary>
    /// Carry the player's OWN realms with their Stonetavern account, so a second machine gets them
    /// back after signing in. Off by default and deliberately so: it sends the names and addresses of
    /// realms - including servers that are not ours - to us, and nobody should have that happen
    /// without saying yes first.
    /// </summary>
    public bool ProfileSyncEnabled { get; set; }

    /// <summary>
    /// Realms the player DELETED, kept as ids so the deletion survives a sync.
    ///
    /// <para>🔴 Without this the union could never delete anything: a realm removed here comes back
    /// from the server on the very next sync, forever, and the player watches it reappear. A grave
    /// marker is the smallest thing that makes "remove" mean remove. Re-adding a realm with the same
    /// id clears its marker, so a deletion is not a life sentence.</para>
    /// </summary>
    public List<ProfileGrave> DeletedRealms { get; set; } = [];

    /// <summary>Version of the profile last seen on the server. Only the server ever increments it;
    /// the launcher just echoes it back so a concurrent write can be detected instead of silently
    /// overwritten.</summary>
    public long ProfileVersion { get; set; }

    // ─── Realms (shipped presets + whatever the player added) ─────────────────
    /// <summary>Every realm the player sees, presets included once they have been edited or reordered.
    /// Empty on a fresh install: <see cref="RealmRegistry"/> supplies the presets.</summary>
    public List<RealmEntry> Realms { get; set; } = [];

    /// <summary>Id of the realm currently selected in the rail.</summary>
    public string SelectedRealmId { get; set; } = RealmRegistry.ElwynnId;

    // ─── Legacy (pre-2026-07-21 "server profiles") ────────────────────────────
    // Read only so an existing launcher_config.json can be migrated into Realms once; never written
    // back. Dropping the properties outright would make a player's own servers vanish silently on
    // first start, which is exactly the kind of quiet data loss the doctrine warns about.
    /// <summary>Obsolete. Migrated into <see cref="Realms"/> by ConfigService, then left empty.</summary>
    public List<LegacyServerProfile> CustomServers { get; set; } = [];

    /// <summary>Obsolete. Seeds <see cref="SelectedRealmId"/> during migration.</summary>
    public string SelectedServerId { get; set; } = "";

    // UseVanillaFixes / UseVanillaTweaks sind 2026-07-22 ersatzlos entfallen. Sie waren zwei
    // Haken in den Einstellungen, die NICHTS ausloesten: kein Code hat sie je gelesen. Der
    // Launcher patcht den Client nicht mehr live - wer einen gepatchten Client will, laedt ihn
    // fertig herunter. Ein Schalter, der ein Versprechen anzeigt und nichts tut, ist schlimmer
    // als kein Schalter. Alte Configs behalten die Felder als unbekannte JSON-Eigenschaften;
    // System.Text.Json ignoriert sie beim Laden, es braucht also keine Migration.
    public string LastAccountName { get; set; } = "";

    /// <summary>
    /// What pressing the window close button does (see <see cref="Models.CloseAction"/>). Default
    /// <see cref="Models.CloseAction.Ask"/> means the keep-running prompt is shown every time; once the
    /// player ticks "remember my choice" this holds <see cref="Models.CloseAction.Background"/> or
    /// <see cref="Models.CloseAction.Quit"/> and the prompt is skipped. Serialised as its numeric value;
    /// an older config without the field simply reads back as Ask.
    /// </summary>
    public CloseAction CloseAction { get; set; } = CloseAction.Ask;

    /// <summary>
    /// WoW <em>client</em> locale (realmlist/MPQ side, see <see cref="ClientLocales"/>). The launcher
    /// interface itself is English only and has no setting (see <see cref="Localization.Loc"/>).
    /// </summary>
    public string Locale { get; set; } = "enUS";

    /// <summary>
    /// True once the one-time AppImage first-run setup has run (moved the launcher into
    /// <c>~/Applications</c>, dropped a trusted desktop shortcut and registered the menu entry). Set by
    /// <see cref="Services.Platform.IFirstRunSetup"/> on the first start FROM an <c>.AppImage</c> so the
    /// player is never asked again. An older config, or one from a tarball/dev run, reads back false —
    /// harmless, because the setup only acts when it is actually running from an AppImage and every step
    /// it does is idempotent. Windows/macOS never set this.
    /// </summary>
    public bool DesktopIntegrationDone { get; set; }

    /// <summary>
    /// Known WoW client installations keyed by gamebuild (5875 / 8606 / 12340).
    /// Each progression phase maps to exactly one build (see <see cref="Models.Progression"/>),
    /// so the launcher can keep all three eras installed side-by-side and pick the right one.
    /// </summary>
    public Dictionary<int, string> ClientInstalls { get; set; } = [];

    /// <summary>
    /// Slug of the phase the player last launched into. Compared against the server's
    /// active phase to detect when a new era needs a different client build
    /// (e.g. Vanilla→TBC-Prepatch). Empty on first run.
    /// </summary>
    public string LastPhase { get; set; } = "";

    /// <summary>
    /// Id of the expansion the player picked in the launcher (<see cref="Models.Expansion"/>).
    /// Drives which client build is resolved + which background is shown. Empty = follow the
    /// server's announced active phase (default behaviour).
    /// </summary>
    public string SelectedExpansion { get; set; } = "";

    /// <summary>
    /// Installed client version per gamebuild (build → manifest version string at install time).
    /// Lets the launcher compare a found install against the manifest's current client version
    /// to drive <see cref="Models.LauncherState.UpdateAvailable"/> (§6.3 build comparison).
    /// </summary>
    public Dictionary<int, string> InstalledClientVersions { get; set; } = [];

    /// <summary>
    /// Folder the player chose for a first client install, asked once via a native folder picker
    /// before the very first download of any build. Null/empty = "the launcher decides" — the
    /// original, unprompted default (<see cref="Services.Platform.IAppPaths.ClientInstallDir"/>: next
    /// to the exe on Windows, the XDG data dir on Linux). Once set, every later first-install for a
    /// NEW build reuses this root (each build still gets its own sub-folder, see
    /// <see cref="Services.Platform.IAppPaths"/> naming) instead of asking again — the dialog is a
    /// one-time preference, not a per-download interruption. Repair and Update never read or write
    /// this field: they always target the path already recorded in <see cref="ClientInstalls"/>.
    /// </summary>
    public string? PreferredInstallRoot { get; set; }
}
