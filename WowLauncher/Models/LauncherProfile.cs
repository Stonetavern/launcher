namespace WowLauncher.Models;

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

/// <summary>
/// The part of a player's launcher setup that is worth carrying to another machine: which realms they
/// added and which one was selected. Nothing else.
///
/// <para>🔴 <b>No paths.</b> <see cref="LauncherConfig.ClientInstalls"/>,
/// <see cref="LauncherConfig.PreferredInstallRoot"/> and <see cref="LauncherConfig.WowExecutablePath"/>
/// describe one machine and are meaningless on another. Syncing them would make a Linux launcher
/// believe in <c>C:\Games\WoW</c>: the client would count as installed, PLAY would point at nothing,
/// and no error would ever be raised. A whitelist is used here rather than "everything except", so a
/// field added later cannot travel by accident.</para>
/// </summary>
public sealed class LauncherProfile
{
    [JsonPropertyName("realms")]
    public List<ProfileRealm> Realms { get; set; } = [];

    [JsonPropertyName("selectedRealmId")]
    public string SelectedRealmId { get; set; } = "";

    /// <summary>Ids of realms the player deleted, each with WHEN. See
    /// <see cref="LauncherConfig.DeletedRealmIds"/> for why a union needs grave markers, and
    /// <see cref="ProfileMerge"/> for why they need a time on them.</summary>
    // Hiess in der ersten Fassung "deletedRealmIds" (nur Ids, ohne Zeit). Eine Migration braucht es
    // NICHT und das ist geprueft, nicht angenommen: der Sync war nie in einem veroeffentlichten Build
    // (das ausgelieferte AppImage 1.1.0 ist aelter als der erste Sync-Commit), keine Config auf einer
    // Maschine traegt das alte Feld, und die Server-Tabelle ist bis heute nicht angelegt. Es gibt
    // schlicht keinen Bestand. Sobald das hier einmal ausgeliefert ist, waere eine Umbenennung ohne
    // Migration dagegen ein stiller Datenverlust - Grabsteine faenden sich nicht wieder und geloeschte
    // Realms wuerden auferstehen.
    [JsonPropertyName("deletedRealms")]
    public List<ProfileGrave> DeletedRealms { get; set; } = [];

    [JsonIgnore]
    public List<string> DeletedRealmIds => DeletedRealms.Select(g => g.Id).ToList();

    /// <summary>Read the portable bits out of a config. Presets are skipped: they ship with the
    /// launcher, every install already has them, and a stored copy would only pin an old address.</summary>
    public static LauncherProfile FromConfig(LauncherConfig cfg)
    {
        var live = (cfg.Realms ?? [])
            .Where(r => !r.IsPreset && !string.IsNullOrWhiteSpace(r.Id))
            .Select(ProfileRealm.From)
            .ToList();
        return new LauncherProfile
        {
            SelectedRealmId = cfg.SelectedRealmId ?? "",
            Realms = live,
            // Graves are taken verbatim. Filtering out "the realm exists again anyway" here would be a
            // second, quieter copy of the revive rule - and the same trap: on a machine that has not
            // seen the deletion yet the realm always still exists. Exactly one place clears a grave,
            // and it is the add button.
            DeletedRealms = (cfg.DeletedRealms ?? [])
                .Where(g => !string.IsNullOrWhiteSpace(g.Id))
                .GroupBy(g => g.Id, System.StringComparer.OrdinalIgnoreCase)
                .Select(grp => new ProfileGrave { Id = grp.Key, DeletedAt = grp.Max(g => g.DeletedAt) })
                .ToList(),
        };
    }
}

/// <summary>One realm, reduced to what is true on every machine.</summary>
public sealed class ProfileRealm
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("realmlistAddress")]
    public string RealmlistAddress { get; set; } = "";

    [JsonPropertyName("clientKey")]
    public string ClientKey { get; set; } = "";

    [JsonPropertyName("manifestUrl")]
    public string ManifestUrl { get; set; } = "";

    /// <summary>When the player added this realm, unix milliseconds. Zero for a realm written before
    /// this field existed; the merge then treats it as "older than anything", which is the safe
    /// reading for a realm nobody has touched since.</summary>
    [JsonPropertyName("addedAt")]
    public long AddedAt { get; set; }

    public static ProfileRealm From(RealmEntry r) => new()
    {
        AddedAt = r.AddedAt,
        Id = r.Id ?? "",
        Name = r.Name ?? "",
        RealmlistAddress = r.RealmlistAddress ?? "",
        ClientKey = r.ClientKey ?? "",
        ManifestUrl = r.ManifestUrl ?? "",
    };

    public RealmEntry ToEntry() => new()
    {
        Id = Id,
        Name = Name,
        RealmlistAddress = RealmlistAddress,
        ClientKey = ClientKey,
        ManifestUrl = ManifestUrl,
        IsPreset = false,
    };

    /// <summary>Only a realm that can actually be used is worth storing or accepting. A manifest URL
    /// that is neither empty nor http(s) is dropped rather than the whole realm: the realm still works
    /// without one, and refusing a foreign scheme is the point.</summary>
    public bool IsUsable()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name)) return false;
        if (string.IsNullOrWhiteSpace(RealmlistAddress)) return false;
        if (!string.IsNullOrWhiteSpace(ManifestUrl)
            && !ManifestUrl.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase)
            && !ManifestUrl.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase))
            ManifestUrl = "";
        return true;
    }
}

/// <summary>A deleted realm and when it was deleted.</summary>
public sealed class ProfileGrave
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Unix milliseconds. 0 = a grave from before this field existed.</summary>
    [JsonPropertyName("deletedAt")]
    public long DeletedAt { get; set; }
}

/// <summary>What the server hands back: the stored profile and the version it is at.</summary>
public sealed class LauncherProfileEnvelope
{
    [JsonPropertyName("version")]
    public long Version { get; set; }

    [JsonPropertyName("profile")]
    public LauncherProfile? Profile { get; set; }
}

/// <summary>What the launcher sends up: the profile plus the version it believes the server is at.</summary>
public sealed class LauncherProfilePutRequest
{
    [JsonPropertyName("baseVersion")]
    public long BaseVersion { get; set; }

    [JsonPropertyName("profile")]
    public LauncherProfile Profile { get; set; } = new();
}

/// <summary>
/// Bring a server profile and a local config together.
///
/// <para><b>Union by id, never "last write wins".</b> Overwriting the whole list with whichever side
/// wrote last silently DELETES a realm added on the other machine, and the player has no way to notice
/// until they go looking for it. A union can only ever produce one realm too many, which costs a click
/// - the other direction costs data. Deleting therefore becomes weaker on purpose: a realm removed on
/// one machine can come back from the other until both have seen the removal.</para>
/// </summary>
public static class ProfileMerge
{
    /// <summary>How many grave markers travel. High enough that no real player hits it, low enough
    /// that the stored blob cannot grow without bound.</summary>
    public const int MaxTombstones = 200;

    public static LauncherProfile Union(LauncherProfile? server, LauncherProfile local)
    {
        var merged = new LauncherProfile();

        // Deletions from BOTH sides, keeping the LATEST time per id.
        //
        // 🔴 A grave needs a time on it, and this is the third attempt at that lesson. "Union of ids"
        // alone cannot express delete-then-re-add: with the grave always winning, re-adding a realm is
        // impossible forever; with local presence beating the grave, deleting is impossible forever.
        // Both were shipped and both were wrong. What actually decides is WHICH ACT HAPPENED LATER,
        // and that is a fact only a timestamp carries. Clocks between two machines are not perfectly
        // aligned, which for a per-realm tug of war is a far smaller price than either dead end.
        var graves = new Dictionary<string, long>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var g in (server?.DeletedRealms ?? []).Concat(local.DeletedRealms))
        {
            if (string.IsNullOrWhiteSpace(g.Id)) continue;
            graves[g.Id] = graves.TryGetValue(g.Id, out var had) ? System.Math.Max(had, g.DeletedAt) : g.DeletedAt;
        }

        // 🔴 A grave ALWAYS wins over a realm that is merely still present locally. The tempting
        // rule - "it exists here, so the player must have re-added it" - cannot tell that apart from
        // "this machine has not seen the deletion yet", and the second is the normal case: the other
        // machine always still has the realm until it syncs. With that rule every device revived every
        // deletion and the two wrote it back and forth forever. Taking a deletion back is an explicit
        // act, and it clears the grave where it happens: SettingsViewModel.AddRealm.
        //
        // The price, stated rather than hidden: re-adding a realm on machine B BEFORE B has seen the
        // grave from machine A loses that realm once. It comes back on the next attempt, and no data
        // that existed on two machines is lost - which is the trade we want in this direction.

        // A realm survives when it was added AFTER it was buried. Equal times mean the realm stays:
        // between "lose the realm" and "keep one too many", keeping is the harmless direction.
        bool Buried(ProfileRealm r) => graves.TryGetValue(r.Id, out var at) && at > r.AddedAt;

        var byId = new Dictionary<string, ProfileRealm>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var r in server?.Realms ?? [])
            if (r.IsUsable() && !Buried(r)) byId[r.Id] = r;
        // Local wins on a collision: the player is sitting in front of THIS machine, and its copy is
        // the one they can see and correct.
        foreach (var r in local.Realms)
            if (r.IsUsable() && !Buried(r)) byId[r.Id] = r;

        // A grave whose realm is alive again has been answered and can go.
        foreach (var id in byId.Keys.ToList()) graves.Remove(id);

        merged.Realms = byId.Values.OrderBy(r => r.Id, System.StringComparer.Ordinal).ToList();
        merged.DeletedRealms = graves
            .OrderByDescending(kv => kv.Value)          // keep the freshest when the cap bites
            .Take(MaxTombstones)
            .Select(kv => new ProfileGrave { Id = kv.Key, DeletedAt = kv.Value })
            .OrderBy(g => g.Id, System.StringComparer.Ordinal)
            .ToList();

        // The selected realm: the local choice wins as long as it still points at something this
        // machine has. On a fresh install it points at a preset that every install has, so without
        // the second half the realm the account was last on would never be restored - the field
        // would be uploaded and then silently ignored, which is worse than not syncing it at all.
        var localPicksSomethingReal =
            !string.IsNullOrWhiteSpace(local.SelectedRealmId)
            && (local.Realms.Any(r => string.Equals(r.Id, local.SelectedRealmId, System.StringComparison.OrdinalIgnoreCase))
                || Models.RealmRegistry.Presets().Any(p => string.Equals(p.Id, local.SelectedRealmId, System.StringComparison.OrdinalIgnoreCase)));
        merged.SelectedRealmId = localPicksSomethingReal || server is null
            ? local.SelectedRealmId
            : server.SelectedRealmId;
        return merged;
    }

    /// <summary>True when the two hold the same realms with the same contents. Used to skip a pointless
    /// upload: a sync that pushes on every start burns a request and bumps the version for nothing.</summary>
    public static bool SameContent(LauncherProfile a, LauncherProfile b)
    {
        if (a.Realms.Count != b.Realms.Count) return false;
        if (!string.Equals(a.SelectedRealmId, b.SelectedRealmId, System.StringComparison.Ordinal)) return false;
        if (a.DeletedRealms.Count != b.DeletedRealms.Count) return false;
        var bg = b.DeletedRealms.ToDictionary(g => g.Id, g => g.DeletedAt, System.StringComparer.OrdinalIgnoreCase);
        foreach (var g in a.DeletedRealms)
            if (!bg.TryGetValue(g.Id, out var at) || at != g.DeletedAt) return false;
        var bs = b.Realms.ToDictionary(r => r.Id, System.StringComparer.OrdinalIgnoreCase);
        foreach (var x in a.Realms)
        {
            if (!bs.TryGetValue(x.Id, out var y)) return false;
            if (x.Name != y.Name || x.RealmlistAddress != y.RealmlistAddress
                || x.ClientKey != y.ClientKey || x.ManifestUrl != y.ManifestUrl) return false;
        }
        return true;
    }
}
