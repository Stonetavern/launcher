namespace WowLauncher.Models;

/// <summary>
/// Which emulator core a phase runs on. Drives nothing client-side directly,
/// but documents the server topology the realm belongs to (ADR-001).
/// </summary>
public enum CoreKind
{
    Vmangos,
    CmangosTbc,
    CmangosWotlk,
}

/// <summary>
/// One step of Stonetavern's shared progression. The server announces exactly one
/// <em>active</em> phase at a time; the whole realm advances together
/// (Vanilla → TBC-Prepatch → BC → WotLK-Prepatch → WotLK).
///
/// The crucial client fact: there are 5 phases but only 3 client builds
/// (5875 / 8606 / 12340). A phase transition only forces a client switch when
/// the <see cref="GameBuild"/> changes — i.e. Vanilla→TBC-Prepatch (5875→8606)
/// and BC→WotLK-Prepatch (8606→12340). The prepatch phases share a client with
/// the expansion that follows; the server gates the new continent, not the client.
/// </summary>
public sealed record ProgressionPhase(
    string Slug,
    string DisplayName,
    string Era,
    string GameVersion,
    int GameBuild,
    CoreKind Core,
    bool IsPrepatch)
{
    /// <summary>Short client label for the ActionBar, e.g. "2.4.3 (8606)".</summary>
    public string ClientLabel => $"{GameVersion} ({GameBuild})";
}

/// <summary>
/// The canonical, ordered Stonetavern progression. This is the launcher-side source
/// of truth for which client a phase needs; the server manifest only names the
/// active phase by <see cref="ProgressionPhase.Slug"/> and supplies realm + download
/// coordinates for it (see <see cref="PhaseManifest"/>).
/// </summary>
public static class Progression
{
    public static readonly IReadOnlyList<ProgressionPhase> Phases =
    [
        new("vanilla",        "Vanilla",        "Azeroth",   "1.12.1", 5875,  CoreKind.Vmangos,      IsPrepatch: false),
        new("tbc-prepatch",   "TBC Prepatch",   "Azeroth",   "2.4.3",  8606,  CoreKind.CmangosTbc,   IsPrepatch: true),
        new("bc",             "Burning Crusade","Outland",   "2.4.3",  8606,  CoreKind.CmangosTbc,   IsPrepatch: false),
        new("wotlk-prepatch", "WotLK Prepatch", "Azeroth",   "3.3.5a", 12340, CoreKind.CmangosWotlk, IsPrepatch: true),
        new("wotlk",          "Wrath",          "Northrend", "3.3.5a", 12340, CoreKind.CmangosWotlk, IsPrepatch: false),
    ];

    /// <summary>Fallback when the server hasn't announced a phase yet (offline-first).</summary>
    public static ProgressionPhase Default => Phases[0];

    public static ProgressionPhase? BySlug(string? slug) =>
        slug is null ? null : Phases.FirstOrDefault(p => p.Slug == slug);

    /// <summary>
    /// True when moving from <paramref name="current"/> to <paramref name="next"/>
    /// requires the player to swap to a different WoW client build. Same-build
    /// transitions (prepatch→expansion) are seamless — same install, just a new realm gate.
    /// </summary>
    public static bool RequiresClientSwitch(ProgressionPhase? current, ProgressionPhase next) =>
        current is not null && current.GameBuild != next.GameBuild;
}
