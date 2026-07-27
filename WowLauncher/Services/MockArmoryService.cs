namespace WowLauncher.Services;

using WowLauncher.Models;

/// <summary>
/// In-memory armory feed for the v3 shell under <c>--demo</c>. No IO, no backend, no HTTP client -
/// a fixed roster per realm returned via <see cref="Task.FromResult{T}"/>. This is what the armory
/// surface is judged against until <c>GET /api/launcher/characters</c> exists
/// (see /AI/projects/wow/launcher/HANDOFF-armory.md).
///
/// <para><b>Every name here is invented.</b> No real player, account or character from any live realm
/// appears in this file, and none ever may: demo data ships in the binary.</para>
///
/// <para>The roster is deliberately uneven - a geared main, a half-levelled alt, a level 12 bank
/// character - so the score plate, the empty guild line and the lockout block all show in a single
/// screenshot instead of looking uniformly full.</para>
/// </summary>
public sealed class MockArmoryService : IArmoryService
{
    /// <summary>A reset that is <paramref name="days"/> whole days away right now. The demo roster
    /// carries stamps rather than sentences so its countdown text is produced by the same catalog
    /// lookup the real feed uses; an hour of slack keeps the floor division off the boundary.</summary>
    private static long ResetInDays(int days) =>
        DateTimeOffset.UtcNow.AddDays(days).AddHours(1).ToUnixTimeSeconds();

    // Built per call rather than cached in a static field: the rows format catalog text, and a static
    // initialiser would freeze both that text and the reset countdown at first touch. Three records
    // per call cost nothing next to the HTTP call they stand in for.
    private static ArmoryCharacter[] Elwynn =>
    [
        // Deliberately NOT reusing any name from MockFriendsPresenceService: a character sharing a
        // friend account name would show exactly the account-to-character join the owner directive of
        // 2026-07-20 refuses, and a demo screenshot is how a wrong idea gets agreed to.
        new(Guid: 4101, Name: "Torvin", ClassId: 4, ClassName: "Rogue", RaceName: "Human",
            Level: 60, Online: true,
            Guild: "Lantern Watch", GuildRank: "Officer", Zone: "Blackrock Depths",
            MoneyCopper: 8_642_317, PlayedSeconds: 1_612_800, QuestCount: 486,
            HonorRankName: "Knight-Captain", Score: 431, ScoreTier: "Raid Ready",
            Lockouts:
            [
                ArmoryLockout.FromReset("Molten Core", ResetInDays(4)),
                ArmoryLockout.FromReset("Onyxias Lair", ResetInDays(2)),
            ]),
        new(Guid: 4118, Name: "Belworth", ClassId: 5, ClassName: "Priest", RaceName: "Dwarf",
            Level: 47, Online: false,
            Guild: "Lantern Watch", GuildRank: "Member", Zone: "Tanaris",
            MoneyCopper: 412_880, PlayedSeconds: 302_400, QuestCount: 214,
            HonorRankName: null, Score: 118, ScoreTier: "Leveling",
            Lockouts: []),
        new(Guid: 4133, Name: "Corrin", ClassId: 1, ClassName: "Warrior", RaceName: "Night Elf",
            Level: 12, Online: false,
            Guild: null, GuildRank: null, Zone: "Darnassus",
            MoneyCopper: 21_450, PlayedSeconds: 27_900, QuestCount: 38,
            HonorRankName: null, Score: 24, ScoreTier: "Fresh",
            Lockouts: []),
    ];

    private static ArmoryCharacter[] Barrens =>
    [
        new(Guid: 7201, Name: "Draveth", ClassId: 9, ClassName: "Warlock", RaceName: "Orc",
            Level: 33, Online: false,
            Guild: null, GuildRank: null, Zone: "Thousand Needles",
            MoneyCopper: 96_120, PlayedSeconds: 154_800, QuestCount: 121,
            HonorRankName: null, Score: 71, ScoreTier: "Leveling",
            Lockouts: []),
    ];

    public Task<ArmoryRoster> GetCharactersAsync(string realmId, CancellationToken ct = default)
    {
        // Unknown realm ids answer with an empty OK roster, not a failure: a player who added their
        // own realm has no characters here, and that is a normal state, not a broken one.
        var list = realmId switch
        {
            "elwynn" => Elwynn,
            "barrens" => Barrens,
            _ => Array.Empty<ArmoryCharacter>(),
        };
        return Task.FromResult(new ArmoryRoster(list, ArmoryStatus.Ok));
    }
}
