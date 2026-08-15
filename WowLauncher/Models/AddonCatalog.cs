namespace WowLauncher.Models;

using System.Text.Json.Serialization;

/// <summary>
/// One addon as the realm offers it. The catalog lives on the distribution host
/// (<c>addons.json</c>) beside the client manifest, and the ZIPs it points at are mirrored there too
/// (owner decision 2026-07-27: we host, so a player never depends on a third-party host being up and
/// the launcher only ever talks to one origin).
///
/// <para><b>What the launcher needs beyond a URL.</b> <see cref="Sha256"/> because nothing is ever
/// extracted unverified (hard invariant), and <see cref="Folders"/> because an addon is not a file but
/// a set of directories under <c>Interface/AddOns/</c>: without knowing which ones it owns, the
/// launcher could neither remove nor update it without guessing — and guessing here deletes a player's
/// own addon.</para>
///
/// <para><b>Legal.</b> Mirroring someone else's code means redistributing it, so
/// <see cref="License"/> and <see cref="Homepage"/> are not decoration: they are what makes the mirror
/// defensible and are shown in the UI. No Blizzard assets, ever — an addon is community Lua/XML.</para>
/// </summary>
public sealed class AddonEntry
{
    /// <summary>Stable key (lower case, no spaces). Also the key of the installed-state file.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>One plain sentence: what it does for the player. No marketing.</summary>
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";

    [JsonPropertyName("author")] public string Author { get; set; } = "";

    /// <summary>SPDX-ish license name of the mirrored package (shown, never guessed).</summary>
    [JsonPropertyName("license")] public string License { get; set; } = "";

    /// <summary>Where the addon really comes from, so a player can check it themselves.</summary>
    [JsonPropertyName("homepage")] public string Homepage { get; set; } = "";

    /// <summary>The version, as the catalogue names it. For a mirrored upstream package that is its
    /// release (<c>7.5.2</c>); for a community upload it is the content itself (a hash prefix), because
    /// uploads carry no release number and "the file changed" is the only true statement available.</summary>
    [JsonPropertyName("version")] public string Version { get; set; } = "";

    /// <summary>When this version was published, ISO date. Provenance the hash cannot give: it answers
    /// "which one is newer" and "what did I have last week" for a player and for support, where a hash
    /// prefix only ever answers "different" (Codex review 2026-07-27). Empty when unknown.</summary>
    [JsonPropertyName("released")] public string Released { get; set; } = "";

    /// <summary>Client builds this package is for (5875 = 1.12.1, 42597 = 1.14.2). An addon offered
    /// for the wrong build is worse than none: it loads and misbehaves.</summary>
    [JsonPropertyName("builds")] public List<int> Builds { get; set; } = [];

    [JsonPropertyName("url")] public string Url { get; set; } = "";

    [JsonPropertyName("size")] public long Size { get; set; }

    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";

    /// <summary>The folders under <c>Interface/AddOns/</c> this package owns. Everything the ZIP
    /// carries outside them is refused rather than written (a package that wants to put files
    /// somewhere else is not an addon install, it is something else).
    ///
    /// <para>MAY be empty, and is for every community upload: the website's pack catalogue records a
    /// name, a size and a hash, not a folder layout. The launcher then derives the folders from the
    /// VERIFIED package itself (its top-level directories) and holds the same rule against it — the
    /// package still cannot write anywhere else, the launcher just learned the boundary from the zip
    /// instead of being told it. A catalogue that DOES declare folders is authoritative.</para></summary>
    [JsonPropertyName("folders")] public List<string> Folders { get; set; } = [];

    /// <summary>Usable at all: an entry missing an id, a URL or a hash cannot be installed safely, so
    /// it is dropped from the catalog rather than shown as a dead button. Folders may be absent (they
    /// are then derived from the package), but a folder that IS declared must be a plain folder name —
    /// a catalogue entry naming <c>../WTF</c> is not a mistake to work around, it is one to drop.</summary>
    [JsonIgnore]
    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(Id)
        && !string.IsNullOrWhiteSpace(Url)
        && !string.IsNullOrWhiteSpace(Sha256)
        && Folders.All(IsSafeFolderName);

    /// <summary>A folder name that can only ever name a direct child of <c>Interface/AddOns/</c>.
    /// Anything with a separator, a drive or a <c>..</c> is refused — the catalog is data from the
    /// network, and this is the first place a bad entry could escape the addons directory.</summary>
    public static bool IsSafeFolderName(string? folder) =>
        !string.IsNullOrWhiteSpace(folder)
        && folder.Trim() == folder
        && folder.IndexOfAny(['/', '\\', ':']) < 0
        && folder != "." && folder != ".."
        && folder.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>True when this package targets <paramref name="build"/>.
    ///
    /// <para>🔴 Eine LEERE Build-Liste gilt als "passt zu keinem Build", nicht als "passt zu allen".
    /// Vorher war es umgekehrt, und das war fail-open: ein Katalog-Eintrag ohne Build-Angabe wurde
    /// jedem Client angeboten, also auch ein 1.14.2-Addon einem 1.12-Client, wo es auf API trifft, die
    /// es dort nicht gibt. Ein Eintrag ohne Build-Angabe ist ein Fehler in den Katalogdaten; darauf mit
    /// "gilt ueberall" zu antworten macht aus einem Datenfehler ein Spielerproblem. Gemessen
    /// 2026-08-12: alle 584 Eintraege des Live-Katalogs tragen eine Build-Angabe (548 fuer 5875,
    /// 36 fuer 42597), es geht also kein real existierendes Paket verloren.</para></summary>
    public bool SupportsBuild(int build) => Builds.Contains(build);
}

/// <summary>The addon catalog document as it is served.</summary>
public sealed class AddonCatalog
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;

    [JsonPropertyName("updated")] public string Updated { get; set; } = "";

    [JsonPropertyName("addons")] public List<AddonEntry> Addons { get; set; } = [];

    /// <summary>The usable entries for one client build, in catalog order. Unusable entries are
    /// dropped here, once, so no consumer has to remember to check.</summary>
    public IReadOnlyList<AddonEntry> ForBuild(int build) =>
        Addons.Where(a => a.IsUsable && a.SupportsBuild(build)).ToList();

    public static AddonCatalog Empty => new();
}

/// <summary>
/// What the launcher installed into one client, written beside the addons it owns
/// (<c>Interface/AddOns/.stonetavern-addons.json</c>). It lives WITH the install rather than in the
/// launcher config on purpose: addons belong to a client directory, which can be moved, reinstalled or
/// shared between realms, and a launcher-side list would drift from what is actually on disk. The
/// client updater already preserves <c>Interface/AddOns/</c>, so it survives client updates.
/// </summary>
public sealed class InstalledAddonState
{
    [JsonPropertyName("addons")] public List<InstalledAddonRecord> Addons { get; set; } = [];
}

/// <summary>One installed addon: what it is, which version, and exactly which folders the launcher
/// created — the last part is what makes a removal safe (never a folder we did not write).</summary>
public sealed class InstalledAddonRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("released")] public string Released { get; set; } = "";
    [JsonPropertyName("folders")] public List<string> Folders { get; set; } = [];
}
