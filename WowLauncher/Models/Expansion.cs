namespace WowLauncher.Models;

/// <summary>
/// A user-facing expansion choice (Vanilla / TBC / WotLK). This is the <em>picker</em>
/// abstraction sitting on top of the finer-grained <see cref="ProgressionPhase"/> list:
/// the server progression has 5 phases (incl. prepatches) but only 3 client builds, and
/// players think in expansions, not phases. Each expansion maps to its canonical phase
/// (the non-prepatch one) and carries the background art shown when it's selected.
///
/// Selecting an expansion drives: the client build to resolve, the realm coordinates
/// (via the manifest phase entry), and the HeroStage background.
/// </summary>
public sealed record Expansion(
    string Id,
    string DisplayName,
    string ShortLabel,
    int GameBuild,
    string PhaseSlug,
    string BackgroundBase)
{
    /// <summary>
    /// The three shipped expansions, in progression order. <see cref="GameBuild"/> /
    /// <see cref="PhaseSlug"/> stay in lockstep with <see cref="Progression.Phases"/>.
    /// <see cref="BackgroundBase"/> is the avares path WITHOUT extension — the loader tries
    /// .webp first (shipped art, ~500 KB) then .png (fallback), and degrades gracefully to the
    /// flat Stone backdrop when neither is present (art delivered separately).
    /// </summary>
    public static readonly IReadOnlyList<Expansion> All =
    [
        new("vanilla", "Vanilla",          "1.12", 5875,  "vanilla", "avares://WowLauncher/Assets/Backgrounds/bg-vanilla"),
        new("tbc",     "Burning Crusade",  "2.4.3", 8606, "bc",      "avares://WowLauncher/Assets/Backgrounds/bg-tbc"),
        new("wotlk",   "Wrath of the Lich King", "3.3.5a", 12340, "wotlk", "avares://WowLauncher/Assets/Backgrounds/bg-wotlk"),
    ];

    /// <summary>Candidate background asset URIs in load priority (WebP primary, PNG fallback).</summary>
    public IEnumerable<string> BackgroundCandidates()
    {
        yield return BackgroundBase + ".webp";
        yield return BackgroundBase + ".png";
    }

    /// <summary>
    /// Corner emblem asset base (top-right of the hero). This is a BRAND-SAFE stylized expansion
    /// emblem in the Stonetavern style — never an embedded Blizzard logo (clean-room/legal). Drops
    /// in like the backgrounds; absent → nothing shows. PNG first (transparency for the emblem).
    /// </summary>
    public string LogoBase => $"avares://WowLauncher/Assets/Logos/logo-{Id}";

    public IEnumerable<string> LogoCandidates()
    {
        yield return LogoBase + ".png";
        yield return LogoBase + ".webp";
    }

    /// <summary>True when at least one client build of this era exists to ship. Derived straight from
    /// <see cref="ClientVersion"/> - the one place that tracks availability - so this can never drift
    /// out of sync with what the picker actually offers.</summary>
    public bool IsAvailable => ClientVersion.EraIsAvailable(Id);

    public static Expansion Default => All[0];

    public static Expansion ById(string? id) =>
        All.FirstOrDefault(e => e.Id == id) ?? Default;

    /// <summary>Resolve the expansion that owns a given progression phase slug (prepatch → its expansion).</summary>
    public static Expansion ForPhase(string? phaseSlug) => phaseSlug switch
    {
        "vanilla" => All[0],
        "tbc-prepatch" or "bc" => All[1],
        "wotlk-prepatch" or "wotlk" => All[2],
        _ => Default,
    };

    /// <summary>The canonical progression phase this expansion launches into.</summary>
    public ProgressionPhase Phase => Progression.BySlug(PhaseSlug) ?? Progression.Default;
}
