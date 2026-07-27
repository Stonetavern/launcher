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
    public const string ElwynnId = "elwynn";
    public const string BarrensId = "barrens";

    private const string StonetavernManifest = "https://downloads.stonetavern.app/manifest.json";

    /// <summary>The shipped presets. Fresh instances each call - never hand out the canon for editing.</summary>
    public static List<RealmEntry> Presets() =>
    [
        new()
        {
            Id = ElwynnId, Name = "Elwynn", RealmlistAddress = "play.stonetavern.app",
            ClientKey = "1.12.1", ClientKeys = ["1.12.1", "1.14.2"],
            ManifestUrl = StonetavernManifest, IsPreset = true, IsLive = true,
        },
        new()
        {
            Id = BarrensId, Name = "Barrens", RealmlistAddress = "play.stonetavern.app",
            // Both Vanilla realms speak the same two clients, so Barrens offers 1.14.2 too (Owner
            // 2026-07-23). The client is installed once per BUILD (ClientInstalls is keyed by gamebuild,
            // not by realm), so a 1.14.2 install done on Elwynn is found here with no second download.
            ClientKey = "1.12.1", ClientKeys = ["1.12.1", "1.14.2"],
            ManifestUrl = StonetavernManifest, IsPreset = true, IsLive = false,
        },
    ];

    /// <summary>Presets plus the player's own realms, presets first, ids deduped against the presets.</summary>
    public static List<RealmEntry> All(LauncherConfig cfg)
    {
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
