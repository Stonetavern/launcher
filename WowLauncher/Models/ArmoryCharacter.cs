namespace WowLauncher.Models;

using System.Collections.Generic;
using Avalonia.Media;
using WowLauncher.Localization;

/// <summary>One raid lockout row, already formatted for display.</summary>
public sealed record ArmoryLockout(string Instance, string Resets)
{
    /// <summary>Build a lockout row from a raw reset stamp. Both feeds (the wire mapper and the demo
    /// mock) go through here, so the countdown wording exists exactly once and lives in the catalog.
    /// A mock that spelled the same sentence out as a literal would drift silently the moment the
    /// catalog is reworded, and the demo path is the one screenshots are taken from.</summary>
    public static ArmoryLockout FromReset(string instance, long unixSeconds) =>
        new(instance, FormatReset(unixSeconds));

    /// <summary>Unix SECONDS -> "Resets in 4 days" / "Resets today". A reset already in the past is
    /// reported as expired rather than as a negative number.</summary>
    public static string FormatReset(long unixSeconds)
    {
        if (unixSeconds <= 0) return "";
        var when = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var days = (int)Math.Floor((when - DateTimeOffset.UtcNow).TotalDays);
        if (days < 0) return Loc.T("Armory_Reset_Expired");
        if (days == 0) return Loc.T("Armory_Reset_Today");
        if (days == 1) return Loc.T("Armory_Reset_Tomorrow");
        return Loc.F("Armory_Reset_Days", days);
    }
}

/// <summary>
/// A character as the armory surface shows it. This is the UI-facing shape:
/// <see cref="Services.MockArmoryService"/> builds it from invented demo data and
/// <see cref="Services.HttpArmoryService"/> maps the wire DTO onto it, so the view never changes when
/// the backend arrives.
///
/// <para>Display names (class, race, zone, rank, score tier) arrive as text FROM the server. The
/// launcher deliberately carries no class/zone/faction name table: that would be game data in the
/// binary, and the web app already owns those tables. The one thing resolved locally is the class
/// ACCENT COLOUR, from the numeric class id - a colour is not game data, and the alternative (a hex
/// string on the wire) would let the server restyle the launcher.</para>
/// </summary>
public sealed record ArmoryCharacter(
    long Guid,
    string Name,
    int ClassId,
    string ClassName,
    string RaceName,
    int Level,
    bool Online,
    string? Guild,
    string? GuildRank,
    string? Zone,
    long MoneyCopper,
    long PlayedSeconds,
    int QuestCount,
    string? HonorRankName,
    int Score,
    string? ScoreTier,
    IReadOnlyList<ArmoryLockout> Lockouts)
{
    /// <summary>"60 Human Rogue" - the identity line under the name.</summary>
    public string Subtitle => Loc.F("Armory_Fmt_Subtitle", Level, RaceName, ClassName);

    /// <summary>"Lantern Watch (Officer)" or an empty string when the character is unguilded.</summary>
    public string GuildLine => string.IsNullOrEmpty(Guild) ? ""
        : string.IsNullOrEmpty(GuildRank) ? Guild
        : Loc.F("Armory_Fmt_Guild", Guild, GuildRank);

    public bool HasGuild => GuildLine.Length > 0;
    public bool HasZone => !string.IsNullOrEmpty(Zone);
    public bool HasLockouts => Lockouts.Count > 0;
    public bool HasHonorRank => !string.IsNullOrEmpty(HonorRankName);

    /// <summary>Copper -> "123g 45s 67c". The DB stores one integer; splitting it here keeps the
    /// number honest instead of rounding to gold.</summary>
    public string Gold => Loc.F("Armory_Fmt_Gold",
        MoneyCopper / 10000, MoneyCopper % 10000 / 100, MoneyCopper % 100);

    /// <summary>Seconds -> "124h 30m". Days would need a second unit word for no extra clarity.</summary>
    public string Played => Loc.F("Armory_Fmt_Played", PlayedSeconds / 3600, PlayedSeconds % 3600 / 60);

    public string LevelText => Level.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string ScoreText => Score.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string QuestText => QuestCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Rail initial, same idiom as the friends avatar.</summary>
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();

    /// <summary>The class accent used for the row edge and the name. Compiled bindings cannot call a
    /// lookup, so it is a property.</summary>
    public IBrush Accent => ClassColors.For(ClassId);
}

/// <summary>
/// Class id -> accent colour. Game DOMAIN data (which id is which class), not a style token, so it
/// lives here rather than in Styles.v3.axaml: a style resource would need one key per class plus a
/// converter to pick between them, and the table would still be here in spirit.
///
/// <para>Ids are the class ids used by the character table. Class 6 (Death Knight) exists only from
/// Wrath onward, and the launcher ships a 3.3.5a (12340) client profile in
/// <see cref="ClientVersion"/>, so leaving it out would paint a real WotLK main in the unknown-class
/// fallback tone. An unknown id (a custom class on somebody else's realm) still falls back to the
/// parchment colour rather than throwing - the launcher must render whatever a realm reports.</para>
/// </summary>
public static class ClassColors
{
    private static readonly Dictionary<int, IBrush> Table = new()
    {
        [1] = New("#C79C6E"),   // Warrior
        [2] = New("#F58CBA"),   // Paladin
        [3] = New("#ABD473"),   // Hunter
        [4] = New("#FFF569"),   // Rogue
        [5] = New("#FFFFFF"),   // Priest
        [6] = New("#C41F3B"),   // Death Knight (WotLK only, see the remarks)
        [7] = New("#0070DE"),   // Shaman
        [8] = New("#69CCF0"),   // Mage
        [9] = New("#9482C9"),   // Warlock
        [11] = New("#FF7D0A"),  // Druid
    };

    private static readonly IBrush Fallback = New("#B6A88C"); // ParchmentSecondary

    // One shared brush per class, created once and never mutated (a SolidColorBrush handed to many
    // rows is fine as long as nobody assigns to its Color - nobody does).
    private static IBrush New(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static IBrush For(int classId) => Table.TryGetValue(classId, out var b) ? b : Fallback;
}
