namespace WowLauncher.Models;

/// <summary>
/// A realm the player can select: a name, an address to write into <c>realmlist.wtf</c>, and the
/// client build it needs. This is the launcher's single notion of "a place to play" - it replaced the
/// earlier split between a settings-side <c>ServerProfile</c> and a rail-side <c>RealmChoice</c>, which
/// were two names for the same thing and guaranteed they would drift apart.
///
/// <para><b>Presets are data, not wiring</b> (owner decision 2026-07-21). Stonetavern ships as two
/// preset entries so the launcher works out of the box, but nothing else in the app assumes them: a
/// player can add their own realm, point it at any address, and bind it to whichever client build they
/// have installed. The launcher stays a client configuration tool - it ships no directory of other
/// people's servers, which is the line <c>CLAUDE.md</c> §10 draws.</para>
///
/// <para>Mutable class rather than a record: entries are edited in place and round-tripped through
/// <c>launcher_config.json</c> by <c>System.Text.Json</c>, which wants settable properties.</para>
/// </summary>
public sealed class RealmEntry
{
    /// <summary>When the player added this realm, unix milliseconds. 0 = unknown (shipped preset, or
    /// written before the field existed). Used only by the profile sync to decide whether an add or a
    /// delete happened later.</summary>
    public long AddedAt { get; set; }

    /// <summary>Stable id, also the key for per-realm art (<c>hero-&lt;id&gt;.webp</c>).</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>What lands in <c>realmlist.wtf</c>: a hostname or a raw IP, no port, no "set realmlist".</summary>
    public string RealmlistAddress { get; set; } = "";

    /// <summary>Which client build this realm speaks (<see cref="ClientVersion.Key"/>). The launcher
    /// resolves the matching install and configures exactly that one.</summary>
    public string ClientKey { get; set; } = "1.12.1";

    /// <summary>Manifest endpoint for managed mode (client downloads, updates, era transitions).
    /// Empty = simple mode: the launcher only points an existing install at this realm.</summary>
    public string? ManifestUrl { get; set; }

    /// <summary>The full set of client builds this realm offers, in display order. Optional - a realm
    /// usually needs only one, which is why this can be null. When set with more than one entry the
    /// hero shows a small toggle instead of the single badge (owner decision 2026-07-21: a realm may
    /// speak more than one client, e.g. Elwynn offers both 1.12.1 and the modern 1.14.2 client).
    ///
    /// <para>Absent in every launcher_config.json written before this field existed - System.Text.Json
    /// leaves it null on load rather than failing, and <see cref="AvailableClients"/> below falls back
    /// to the single <see cref="ClientKey"/>, so an existing save keeps loading exactly as before with
    /// no migration step.</para></summary>
    public List<string>? ClientKeys { get; set; }

    /// <summary>Resolved client builds this realm actually offers: <see cref="ClientKeys"/> when set,
    /// else a single-entry list built from <see cref="ClientKey"/>. This is what the hero toggle
    /// enumerates; <see cref="ClientKey"/> alone still says which ONE is active right now.</summary>
    public IReadOnlyList<ClientVersion> AvailableClients =>
        (ClientKeys is { Count: > 0 } keys ? keys : new List<string> { ClientKey })
            .Select(ClientVersion.ByKey)
            .Where(c => !c.IsExcludedOnCurrentOs)   // macOS: drop the 32-bit legacy builds (owner scope 2026-07-23)
            .Distinct()
            .ToList();

    /// <summary>True when the player gets an actual choice. Drives whether the hero shows the client
    /// toggle at all - one client stays a single quiet badge, never a control with one option in it.</summary>
    public bool HasMultipleClients => AvailableClients.Count > 1;

    /// <summary>Shipped with the launcher. Presets can be selected and edited but not deleted, so a
    /// player cannot end up with an empty rail and no way back.</summary>
    public bool IsPreset { get; set; }

    /// <summary>
    /// The GAME realms behind this entry, for the account-scoped web APIs (armory character lists).
    /// Usually the entry's own id — one rail entry, one realm — but not always: Stonetavern is ONE place
    /// to connect (a single auth address, which is all <see cref="RealmlistAddress"/> can express) while
    /// the account's characters live on two named realms behind it. Splitting the rail into one entry per
    /// game realm was the older shape and was wrong: both entries carried the same address, so the choice
    /// changed nothing about where the client connected (owner 2026-07-27).
    ///
    /// <para>Shipped data, not player-editable — null on a config written before this field existed, and
    /// <see cref="AccountRealms"/> then falls back to the id, which is what a custom realm wants anyway.</para>
    /// </summary>
    public List<string>? ArmoryRealms { get; set; }

    /// <summary>Resolved game realms for the account APIs: <see cref="ArmoryRealms"/> when set, else the
    /// entry's own id.</summary>
    public IReadOnlyList<string> AccountRealms =>
        ArmoryRealms is { Count: > 0 } realms ? realms : [Id];

    /// <summary>False marks a realm that is announced but not playable yet (rail shows it dimmed).</summary>
    public bool IsLive { get; set; } = true;

    public bool HasManifest => !string.IsNullOrWhiteSpace(ManifestUrl);

    /// <summary>The client this realm needs, resolved from <see cref="ClientKey"/>. Falls back to the
    /// default build for an unknown key rather than throwing, so a stale/hand-edited config still
    /// renders a realm - see <see cref="ClientAvailable"/> for whether that build actually ships.</summary>
    public ClientVersion Client
    {
        get
        {
            var byKey = ClientVersion.ByKey(ClientKey);
            if (!byKey.IsExcludedOnCurrentOs) return byKey;
            // The active key names a build this OS cannot run (e.g. 1.12.1 on macOS — owner scope): resolve
            // to the first build this realm offers that the OS DOES support, so the Mac never shows a 1.12
            // line. A realm that offers ONLY the excluded build keeps it (ClientAvailable then reports false).
            return AvailableClients.FirstOrDefault() ?? byKey;
        }
    }

    /// <summary>False when the client this realm needs is announced but not shippable yet (Burning
    /// Crusade, Wrath) OR has no path on this OS (a 1.12-only realm on macOS). The realm still shows - it
    /// does not vanish or silently switch to another build - the UI just says the client is not ready.</summary>
    public bool ClientAvailable => Client.IsAvailable && !Client.IsExcludedOnCurrentOs;

    /// <summary>Rail glyph: first letter of the name. Brand-safe, no Blizzard mark.</summary>
    public string ShortTag => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();

    /// <summary>True for the one shipped realm, which wears the lantern instead of a letter.
    /// <para>An initial is what you show when you have nothing better. Stonetavern HAS something
    /// better — the lantern is its mark, and a mark that identifies a place at a glance is exactly
    /// what a 52px tile is for. Realms a player adds themselves keep the letter: inventing a crest
    /// for someone else's server would claim an identity that is not ours to give.</para></summary>
    public bool WearsBrandMark =>
        string.Equals(Id, RealmRegistry.StonetavernId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Inverse of <see cref="WearsBrandMark"/> — compiled bindings cannot negate.</summary>
    public bool WearsLetterMark => !WearsBrandMark;

    /// <summary>Upper-cased name for the hero headline (compiled bindings cannot call ToUpper).</summary>
    public string NameUpper => Name.ToUpperInvariant();

    /// <summary>The line under the hero headline: which client this realm actually needs. Always
    /// precise - "Vanilla" alone stops being true once an era has two client builds.</summary>
    public string ClientLine => $"{Client.Era}  ·  {Client.PreciseLabel}";
}

/// <summary>
/// Resolves the realm list: shipped presets first, then whatever the player added, and keeps the flat
/// "active connection" fields of <see cref="LauncherConfig"/> in sync with the selection so the rest of
/// the launcher (manifest, download, client services) keeps reading them unchanged.
/// </summary>
public static class RealmRegistry
{
    /// <summary>The one shipped realm. Stonetavern is a single place to connect.</summary>
    public const string StonetavernId = "stonetavern";

    /// <summary>The two GAME realms behind that one entry — ids the web APIs know, not rail entries.</summary>
    public const string ElwynnId = "elwynn";
    public const string BarrensId = "barrens";

    /// <summary>The shipped address of the one preset, so the manifest can move it but a player's edit
    /// is never overwritten (see <c>RealmBinding.Effective</c>).</summary>
    public const string StonetavernAddress = "play.stonetavern.app";

    /// <summary>Wo das Manifest von Stonetavern liegt.
    ///
    /// <para>Öffentlich seit 2026-08-05, weil das **Selbst-Update des Launchers** es braucht und
    /// eben NICHT das Manifest des gewählten Realms nehmen darf — siehe
    /// <c>IManifestService.FetchLauncherManifestAsync</c>.</para></summary>
    public const string StonetavernManifest = "https://downloads.stonetavern.app/manifest.json";

    /// <summary>
    /// The shipped presets — ONE entry (owner 2026-07-27). There used to be two, Elwynn and Barrens,
    /// carrying the identical <c>play.stonetavern.app</c> address: the rail offered a choice that changed
    /// nothing about where the client connected, while the thing it really selects — the realm address —
    /// was the same either way. The account's characters on both game realms are still reachable through
    /// <see cref="RealmEntry.AccountRealms"/>.
    ///
    /// <para>Fresh instances each call - never hand out the canon for editing.</para>
    /// </summary>
    public static List<RealmEntry> Presets() =>
    [
        new()
        {
            Id = StonetavernId, Name = "Stonetavern", RealmlistAddress = StonetavernAddress,
            ClientKey = "1.12.1", ClientKeys = ["1.12.1", "1.14.2"],
            ArmoryRealms = [ElwynnId, BarrensId],
            ManifestUrl = StonetavernManifest, IsPreset = true, IsLive = true,
        },
    ];

    /// <summary>The address a preset SHIPS with, or null for an id the launcher does not ship. Used to
    /// tell "the player edited this realm" from "this is still the shipped default", which decides
    /// whether a manifest may move it (<c>RealmBinding.Effective</c>).</summary>
    public static string? ShippedAddress(string? id) =>
        Presets().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))?.RealmlistAddress;

    /// <summary>
    /// Fold a config written by an older launcher onto the single preset: the rail entries
    /// <c>elwynn</c>/<c>barrens</c> no longer exist, so a saved copy of either would otherwise reappear
    /// as a stray custom realm (undeletable, since it was persisted as a preset) and a
    /// <c>SelectedRealmId</c> pointing at one would select nothing.
    ///
    /// <para>An address the player EDITED on the old Elwynn entry is carried over to Stonetavern — it is
    /// the only field of theirs that could have been deliberately changed and still means the same thing.
    /// Barrens is dropped: it never had an address of its own that differed.</para>
    /// </summary>
    internal static void MigrateLegacyRealms(LauncherConfig cfg)
    {
        var legacy = cfg.Realms
            .Where(r => string.Equals(r.Id, ElwynnId, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(r.Id, BarrensId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (legacy.Count > 0)
        {
            var elwynn = legacy.FirstOrDefault(r => string.Equals(r.Id, ElwynnId, StringComparison.OrdinalIgnoreCase));
            var editedAddress = elwynn is not null
                                && !string.IsNullOrWhiteSpace(elwynn.RealmlistAddress)
                                && !string.Equals(elwynn.RealmlistAddress.Trim(), StonetavernAddress, StringComparison.OrdinalIgnoreCase)
                ? elwynn.RealmlistAddress.Trim()
                : null;

            foreach (var old in legacy) cfg.Realms.Remove(old);

            if (editedAddress is not null
                && !cfg.Realms.Any(r => string.Equals(r.Id, StonetavernId, StringComparison.OrdinalIgnoreCase)))
            {
                var shipped = Presets()[0];
                shipped.RealmlistAddress = editedAddress;
                if (elwynn is not null) shipped.ClientKey = elwynn.ClientKey;
                cfg.Realms.Add(shipped);
            }
        }

        if (string.Equals(cfg.SelectedRealmId, ElwynnId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(cfg.SelectedRealmId, BarrensId, StringComparison.OrdinalIgnoreCase))
            cfg.SelectedRealmId = StonetavernId;
    }

    /// <summary>Presets plus the player's own realms, presets first, ids deduped against the presets.</summary>
    public static List<RealmEntry> All(LauncherConfig cfg)
    {
        // Older configs still carry the two rail entries this launcher no longer ships. Fold them first,
        // so nothing downstream ever sees a realm that does not exist any more.
        MigrateLegacyRealms(cfg);

        var list = Presets();

        // A stored copy of a preset (edited address, say) wins over the shipped default, so an owner
        // who repoints Elwynn keeps that across restarts.
        foreach (var stored in cfg.Realms.Where(r => !string.IsNullOrWhiteSpace(r.Id)))
        {
            var idx = list.FindIndex(p => string.Equals(p.Id, stored.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                var shipped = list[idx];
                stored.IsPreset = true;      // a preset stays undeletable however it was persisted

                // ClientKeys is shipped STAMMDATEN - which builds a preset offers - not something a
                // player has any UI to edit directly (they can only pick the currently ACTIVE one via
                // ClientKey, or repoint the address/manifest). A save written before this field existed,
                // or by an older launcher that shipped fewer client options, carries ClientKeys == null
                // and would otherwise freeze a preset at one client forever even after a launcher update
                // adds a second (Codex Finding 3, 2026-07-22: Elwynn gaining 1.14.2 never reached a
                // player who had ever edited it). The shipped list always wins here. Everything the
                // player COULD have deliberately changed - RealmlistAddress, the active ClientKey,
                // ManifestUrl, IsLive - stays exactly as saved.
                stored.ClientKeys = shipped.ClientKeys;
                // Same rule, same reason: which GAME realms sit behind a preset is shipped data the
                // player has no UI for, so the shipped list always wins over whatever was persisted.
                stored.ArmoryRealms = shipped.ArmoryRealms;
                list[idx] = stored;
            }
            else
            {
                list.Add(stored);
            }
        }

        return list;
    }

    /// <summary>The selected realm, falling back to the first entry.</summary>
    public static RealmEntry Resolve(LauncherConfig cfg)
    {
        var all = All(cfg);
        return all.FirstOrDefault(r => r.Id == cfg.SelectedRealmId) ?? all[0];
    }

    /// <summary>
    /// Project the selected realm's coordinates into the flat config fields the services read
    /// (<see cref="LauncherConfig.RealmlistAddress"/> / <see cref="LauncherConfig.ManifestUrl"/>).
    /// Empty manifest = simple mode, and <c>ManifestService</c> short-circuits to null.
    /// </summary>
    public static void ApplyActiveRealm(LauncherConfig cfg)
    {
        var realm = Resolve(cfg);
        cfg.RealmlistAddress = realm.RealmlistAddress;
        cfg.ManifestUrl = realm.HasManifest ? realm.ManifestUrl! : "";
    }

    /// <summary>Turn a display name into a usable id: lower case, non-alphanumerics to dashes, deduped
    /// against what already exists. Ids are stable keys (art, config), so they must not collide.</summary>
    public static string NewId(string name, IEnumerable<RealmEntry> existing)
    {
        var slug = new string((name ?? "").ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray())
            .Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-");
        if (slug.Length == 0) slug = "realm";

        var taken = existing.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(slug)) return slug;

        for (var n = 2; ; n++)
        {
            var candidate = $"{slug}-{n}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}
