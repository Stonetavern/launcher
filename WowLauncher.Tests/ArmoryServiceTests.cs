using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Localization;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.ViewModels;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Armory: the mock that drives <c>--demo</c>, the wire mapping, and the three quiet empty states.
///
/// <para>🔴 <b>Read this before trusting the mapping tests.</b> The JSON below is NOT a captured
/// response. <c>GET /api/launcher/characters</c> did not exist when these tests were written
/// (2026-07-21) - the contract lives in (internal design notes, not published) and this fixture
/// is that contract written twice. That is exactly the shape of the failure this project already paid
/// for once: the login fixture invented <c>expiresAt</c> as a string, the API sent a number, every
/// test was green and every sign-in was broken. So these tests prove that the mapper does what the
/// contract says, and NOTHING about what the server sends. When the endpoint ships, replace the
/// constant below with a real captured body before treating any of it as evidence.</para>
/// </summary>
public sealed class ArmoryServiceTests
{
    // Contract shape from HANDOFF-armory.md (NOT a capture - see the class remarks).
    private const string CharactersJson =
        """
        {
          "realm": "elwynn",
          "characters": [
            {"guid":4101,"name":"Torvin","race":1,"raceName":"Human","class":4,"className":"Rogue",
             "level":60,"online":true,"guild":"Lantern Watch","guildRank":"Officer",
             "zoneName":"Blackrock Depths","money":8642317,"playedTimeTotal":1612800,
             "questCount":486,"honorRank":8,"honorRankName":"Knight-Captain",
             "score":431,"scoreTier":"Raid Ready",
             "lockouts":[{"map":409,"mapName":"Molten Core","resetTime":4102444800}]},
            {"guid":4133,"name":"Corrin","race":4,"raceName":"Night Elf","class":1,"className":"Warrior",
             "level":12,"online":false,"guild":null,"guildRank":null,"zoneName":"Darnassus",
             "money":21450,"playedTimeTotal":27900,"questCount":38,"honorRank":0,"honorRankName":null,
             "score":24,"scoreTier":"Fresh","lockouts":[]}
          ]
        }
        """;

    // ── Test doubles ────────────────────────────────────────────────────────────────────────────

    private sealed class StubAuth(bool loggedIn, string? token) : ILauncherAuthService
    {
        public bool IsLoggedIn { get; } = loggedIn; public string? CurrentToken { get; } = token; public string? CurrentAccount => "tester";
        public Task<LoginOutcome> LoginAsync(string u, string p, CancellationToken ct = default) =>
            Task.FromResult(LoginOutcome.Success("tester"));
        public Task<LoginOutcome> RegisterAsync(RegisterRequest r, CancellationToken ct = default) =>
            Task.FromResult(LoginOutcome.Failure("unused"));
        public void Logout() { }
    }

    private sealed class StubConfig : IConfigService
    {
        public LauncherConfig Load() => new() { RealmlistAddress = "play.stonetavern.app" };
        public void Save(LauncherConfig config) { }
        public bool LastSaveSucceeded => true;
    }

    private sealed class CaptureHandler(HttpStatusCode status, string body = "", bool @throw = false) : HttpMessageHandler
    {
        private readonly HttpStatusCode _status = status;
        private readonly string _body = body;
        private readonly bool _throw = @throw;
        public int Calls { get; private set; }
        public string? LastUrl { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUrl = request.RequestUri?.ToString();
            LastAuthorization = request.Headers.Authorization?.ToString();
            if (_throw) throw new HttpRequestException("simulated network failure");
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
        }
    }

    private static Serilog.ILogger Log => new Serilog.LoggerConfiguration().CreateLogger();

    private static HttpArmoryService Service(CaptureHandler handler, bool loggedIn = true, string? token = "test-token") =>
        new(new HttpClient(handler), new StubConfig(), new StubAuth(loggedIn, token), Log);

    // ── Wire mapping (against the CONTRACT, not against the server) ──────────────────────────────

    [Fact]
    public async Task GetCharacters_MapsTheContractOntoTheUiShape()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, CharactersJson);
        var roster = await Service(handler).GetCharactersAsync("elwynn");

        Assert.Equal(ArmoryStatus.Ok, roster.Status);
        Assert.Equal(2, roster.Characters.Count);

        var torvin = roster.Characters.Single(c => c.Name == "Torvin");
        Assert.Equal(60, torvin.Level);
        Assert.True(torvin.Online);
        Assert.Equal("Rogue", torvin.ClassName);
        Assert.Equal(431, torvin.Score);
        Assert.Equal("Raid Ready", torvin.ScoreTier);
        Assert.Equal("Lantern Watch (Officer)", torvin.GuildLine);
        Assert.Single(torvin.Lockouts);
        Assert.Equal("Molten Core", torvin.Lockouts[0].Instance);

        // Unguilded characters must not print an empty "()" - the whole line is suppressed.
        var corrin = roster.Characters.Single(c => c.Name == "Corrin");
        Assert.False(corrin.HasGuild);
        Assert.Equal("", corrin.GuildLine);
        Assert.False(corrin.HasLockouts);
    }

    /// <summary>
    /// 🔴 The anti-swap test. Every single member of <see cref="ArmoryCharacter"/> is asserted against
    /// the fixture field it must come from, and the fixture gives every same-typed neighbour a
    /// DISTINCT value on purpose (money 8642317 vs played 1612800; level 60 vs quests 486 vs score
    /// 431; guild vs rank vs zone vs honor rank).
    ///
    /// <para>Why this test exists rather than "a couple of representative fields": the mapper feeds a
    /// seventeen-member record, and the earlier version fed it positionally. Swapping two arguments of
    /// the same type compiled, produced 161g instead of 864g and 2400h instead of 448h, and left the
    /// whole suite green, because no test looked at those two numbers. Partial coverage of a wide
    /// positional mapping is not coverage, it is a lottery. If a member is added to the record, add it
    /// here too - an unasserted member is an unguarded one.</para>
    /// </summary>
    [Fact]
    public async Task GetCharacters_MapsEveryFieldOntoTheFieldItBelongsTo()
    {
        var roster = await Service(new CaptureHandler(HttpStatusCode.OK, CharactersJson))
            .GetCharactersAsync("elwynn");

        var torvin = roster.Characters.Single(c => c.Name == "Torvin");
        Assert.Equal(4101, torvin.Guid);
        Assert.Equal("Torvin", torvin.Name);
        Assert.Equal(4, torvin.ClassId);
        Assert.Equal("Rogue", torvin.ClassName);
        Assert.Equal("Human", torvin.RaceName);
        Assert.Equal(60, torvin.Level);
        Assert.True(torvin.Online);
        Assert.Equal("Lantern Watch", torvin.Guild);
        Assert.Equal("Officer", torvin.GuildRank);
        Assert.Equal("Blackrock Depths", torvin.Zone);
        Assert.Equal(8_642_317, torvin.MoneyCopper);
        Assert.Equal(1_612_800, torvin.PlayedSeconds);
        Assert.Equal(486, torvin.QuestCount);
        Assert.Equal("Knight-Captain", torvin.HonorRankName);
        Assert.Equal(431, torvin.Score);
        Assert.Equal("Raid Ready", torvin.ScoreTier);

        // The two derived numbers a swap would corrupt while still looking like plausible values.
        Assert.Equal("864g 23s 17c", torvin.Gold);
        Assert.Equal("448h 0m", torvin.Played);

        var lockout = Assert.Single(torvin.Lockouts);
        Assert.Equal("Molten Core", lockout.Instance);
        Assert.Equal(ArmoryLockout.FormatReset(4102444800), lockout.Resets);

        // Second row, so nulls are proven to survive as nulls rather than as the previous rows value.
        var corrin = roster.Characters.Single(c => c.Name == "Corrin");
        Assert.Equal(4133, corrin.Guid);
        Assert.Equal(1, corrin.ClassId);
        Assert.Equal("Warrior", corrin.ClassName);
        Assert.Equal("Night Elf", corrin.RaceName);
        Assert.Equal(12, corrin.Level);
        Assert.False(corrin.Online);
        Assert.Null(corrin.Guild);
        Assert.Null(corrin.GuildRank);
        Assert.Equal("Darnassus", corrin.Zone);
        Assert.Equal(21_450, corrin.MoneyCopper);
        Assert.Equal(27_900, corrin.PlayedSeconds);
        Assert.Equal(38, corrin.QuestCount);
        Assert.Null(corrin.HonorRankName);
        Assert.Equal(24, corrin.Score);
        Assert.Equal("Fresh", corrin.ScoreTier);
        Assert.Empty(corrin.Lockouts);
    }

    /// <summary>Guards the guard: if the record grows a member, the test above stops being
    /// exhaustive silently. This counts the members and fails until both are updated together.</summary>
    [Fact]
    public void Character_shape_has_not_grown_behind_the_mapping_test()
    {
        var members = typeof(ArmoryCharacter)
            .GetConstructors()
            .Single(c => c.GetParameters().Length > 1)
            .GetParameters().Length;

        Assert.True(members == 17,
            $"ArmoryCharacter now has {members} constructor members instead of 17. Add the new one to " +
            "GetCharacters_MapsEveryFieldOntoTheFieldItBelongsTo and to HttpArmoryService.Map (named " +
            "argument), then update this count.");
    }

    [Fact]
    public async Task GetCharacters_SendsTheBearerAndTheRealm()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, CharactersJson);
        await Service(handler).GetCharactersAsync("barrens");

        Assert.Equal("Bearer test-token", handler.LastAuthorization);
        Assert.Contains("/api/launcher/characters?realm=barrens", handler.LastUrl);
        // No account id is ever sent: the token IS the scope (own characters only).
        Assert.DoesNotContain("account", handler.LastUrl!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Map_DropsRowsWithNoName()
    {
        // A nameless row has no identity to render; it must be skipped, not shown as a blank line.
        Assert.Null(HttpArmoryService.Map(new ArmoryCharacterDtoProbe().Nameless));
    }

    /// <summary>The DTO is internal to the app assembly; this probe just builds one for the test.</summary>
    private sealed class ArmoryCharacterDtoProbe
    {
        public ArmoryCharacterDto Nameless { get; } = new() { Guid = 1, Name = "  ", Level = 60 };
    }

    // ── Offline-first: every failure is a quiet state, never a throw ─────────────────────────────

    [Fact]
    public async Task SignedOut_MakesNoRequestAtAll()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, CharactersJson);
        var roster = await Service(handler, loggedIn: false, token: null).GetCharactersAsync("elwynn");

        Assert.Equal(ArmoryStatus.SignedOut, roster.Status);
        Assert.Empty(roster.Characters);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task MissingEndpoint_Degrades_ToUnavailable()
    {
        // This is TODAY's live behaviour: the route does not exist, so the server answers 404.
        var roster = await Service(new CaptureHandler(HttpStatusCode.NotFound)).GetCharactersAsync("elwynn");
        Assert.Equal(ArmoryStatus.Unavailable, roster.Status);
        Assert.Empty(roster.Characters);
    }

    [Fact]
    public async Task NetworkFailure_And_GarbageBody_Degrade_WithoutThrowing()
    {
        var offline = await Service(new CaptureHandler(HttpStatusCode.OK, "", @throw: true)).GetCharactersAsync("elwynn");
        Assert.Equal(ArmoryStatus.Unavailable, offline.Status);

        var garbage = await Service(new CaptureHandler(HttpStatusCode.OK, "{ not json")).GetCharactersAsync("elwynn");
        Assert.Equal(ArmoryStatus.Unavailable, garbage.Status);
    }

    [Fact]
    public async Task ExpiredToken_ReadsAsSignedOut_NotAsAnError()
    {
        var roster = await Service(new CaptureHandler(HttpStatusCode.Unauthorized)).GetCharactersAsync("elwynn");
        Assert.Equal(ArmoryStatus.SignedOut, roster.Status);
    }

    // ── Formatting (the launcher owns exactly these two conversions) ─────────────────────────────

    [Fact]
    public void Money_And_Played_AreSplitFromTheRawDbValues()
    {
        var c = Sample(money: 8_642_317, played: 1_612_800);
        Assert.Equal("864g 23s 17c", c.Gold);   // copper -> g/s/c, nothing rounded away
        Assert.Equal("448h 0m", c.Played);      // seconds -> hours/minutes
    }

    [Fact]
    public void ResetTime_NeverPrintsANegativeCountdown()
    {
        var past = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeSeconds();
        var soon = DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds();
        var later = DateTimeOffset.UtcNow.AddDays(4).AddHours(1).ToUnixTimeSeconds();

        Assert.Equal("Reset", ArmoryLockout.FormatReset(past));
        Assert.Equal("Resets today", ArmoryLockout.FormatReset(soon));
        Assert.Equal("Resets in 4 days", ArmoryLockout.FormatReset(later));
        Assert.Equal("", ArmoryLockout.FormatReset(0));
    }

    [Fact]
    public void ClassColour_FallsBackInsteadOfThrowing_OnAnUnknownClassId()
    {
        // A custom class on somebody elses realm must render, not crash.
        Assert.NotNull(ClassColors.For(99));
        Assert.NotEqual(ClassColors.For(4), ClassColors.For(9)); // rogue and warlock are distinct
    }

    /// <summary>Every class the SHIPPED client profiles can produce needs its own colour. The launcher
    /// carries a 3.3.5a (12340) profile, so class 6 exists in the wild; without an entry a real Death
    /// Knight main renders in the unknown-class parchment tone and reads as a broken custom class.
    /// The assertion is tied to the client table rather than hardcoded so dropping WotLK support drops
    /// the requirement with it.</summary>
    [Fact]
    public void EveryClassTheShippedClientsCanHave_HasItsOwnColour()
    {
        var unknown = ClassColors.For(99);
        int[] vanilla = [1, 2, 3, 4, 5, 7, 8, 9, 11];

        var wotlk = ClientVersion.All.Any(c => c.Build == 12340);
        int[] expected = wotlk ? [.. vanilla, 6] : vanilla;

        var missing = expected.Where(id => Equals(ClassColors.For(id), unknown)).ToList();
        Assert.True(missing.Count == 0,
            "These class ids fall back to the unknown-class colour although the launcher ships a " +
            $"client that can create them: {string.Join(", ", missing)}");
    }

    // ── Mock (the only thing that renders today) ─────────────────────────────────────────────────

    [Fact]
    public async Task Mock_HasARosterPerPresetRealm_AndAnEmptyOkRosterForAnythingElse()
    {
        var mock = new MockArmoryService();

        Assert.NotEmpty((await mock.GetCharactersAsync("elwynn")).Characters);
        Assert.NotEmpty((await mock.GetCharactersAsync("barrens")).Characters);

        var custom = await mock.GetCharactersAsync("someones-own-realm");
        Assert.Empty(custom.Characters);
        Assert.Equal(ArmoryStatus.Ok, custom.Status); // a realm with no characters is normal, not broken
    }

    [Fact]
    public async Task Mock_ShareNoNameWithTheFriendsMock()
    {
        // A demo character named like a demo friend account would picture exactly the account-to-
        // character join the owner directive of 2026-07-20 refuses. Screenshots are how a wrong idea
        // gets agreed to, so the demo data must not draw that line even by accident.
        var characters = (await new MockArmoryService().GetCharactersAsync("elwynn")).Characters
            .Concat((await new MockArmoryService().GetCharactersAsync("barrens")).Characters)
            .Select(c => c.Name)
            .ToList();
        var accounts = (await new MockFriendsPresenceService().GetFriendsAsync()).Select(f => f.Account);

        Assert.Empty(characters.Intersect(accounts));
    }

    /// <summary>
    /// The demo lockout countdown must come out of the catalog, not out of a literal in the mock.
    ///
    /// <para>Asserting the mock text against <c>Loc.F("Armory_Reset_Days", 4)</c> would prove nothing:
    /// a hardcoded "Resets in 4 days" is character-for-character equal to the catalog rendering today,
    /// so that test stays green on the bug. This one rewords the catalog entry first. A mock that
    /// formats follows the rewording; a mock that spells the sentence out does not. The demo path is
    /// the one screenshots are taken from, so a drift there is a drift in what the owner signs off.</para>
    /// </summary>
    [Fact]
    public async Task Mock_TakesItsLockoutWordingFromTheCatalog()
    {
        using (Loc.OverrideForTests("Armory_Reset_Days", "resets after {0} sleeps"))
        {
            var torvin = (await new MockArmoryService().GetCharactersAsync("elwynn")).Characters
                .Single(c => c.Name == "Torvin");

            Assert.Equal(2, torvin.Lockouts.Count);
            Assert.Equal("resets after 4 sleeps", torvin.Lockouts[0].Resets);
            Assert.Equal("resets after 2 sleeps", torvin.Lockouts[1].Resets);
        }

        // The override is scoped: the catalog is intact for every test that runs after this one.
        Assert.Equal("Resets in 4 days", Loc.F("Armory_Reset_Days", 4));
    }

    // ── ViewModel: the three quiet states ────────────────────────────────────────────────────────

    [Fact]
    public async Task ViewModel_ShowsOneCalmSentencePerEmptyReason()
    {
        var vm = new ArmoryViewModel(new MockArmoryService(), Log);

        // Before anything is loaded the section is signed out, not "empty".
        Assert.True(vm.ShowEmpty);
        Assert.Equal("Sign in to see your characters.", vm.EmptyMessage);

        await vm.LoadAsync("elwynn");
        Assert.False(vm.ShowEmpty);
        Assert.True(vm.HasCharacters);
        Assert.NotNull(vm.Selected);           // the first character is selected for you

        await vm.LoadAsync("someones-own-realm");
        Assert.True(vm.ShowEmpty);
        Assert.Equal("No characters on this realm yet.", vm.EmptyMessage);
        Assert.Null(vm.Selected);

        vm.Clear();
        Assert.Equal("Sign in to see your characters.", vm.EmptyMessage);
    }

    [Fact]
    public async Task ViewModel_KeepsTheSelectedCharacterAcrossARefresh()
    {
        var vm = new ArmoryViewModel(new MockArmoryService(), Log);
        await vm.LoadAsync("elwynn");
        vm.Selected = vm.Characters[2];
        var picked = vm.Selected.Guid;

        await vm.LoadAsync("elwynn");
        Assert.Equal(picked, vm.Selected!.Guid); // a refresh must not bounce the reader back to row 1
    }

    /// <summary>An armory feed whose answers are released by the test, one realm at a time, and which
    /// deliberately IGNORES its cancellation token. A service is allowed to do that (the mock does),
    /// so the VM must not rely on cancellation alone to keep a stale answer off the screen.</summary>
    private sealed class GatedArmory : IArmoryService
    {
        private readonly Dictionary<string, TaskCompletionSource<ArmoryRoster>> _gates = [];

        public Task<ArmoryRoster> GetCharactersAsync(string realmId, CancellationToken ct = default)
        {
            var gate = new TaskCompletionSource<ArmoryRoster>(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates[realmId] = gate;
            return gate.Task;
        }

        public void Answer(string realmId, params string[] names) =>
            _gates[realmId].SetResult(new ArmoryRoster(
                names.Select((n, i) => new ArmoryCharacter(
                    Guid: 1000 + i, Name: n, ClassId: 1, ClassName: "Warrior", RaceName: "Human",
                    Level: 60, Online: false, Guild: null, GuildRank: null, Zone: null,
                    MoneyCopper: 0, PlayedSeconds: 0, QuestCount: 0,
                    HonorRankName: null, Score: 0, ScoreTier: null,
                    Lockouts: [])).ToList(),
                ArmoryStatus.Ok));
    }

    /// <summary>
    /// A fast realm switch must not let the older answer win.
    ///
    /// <para>Both requests are in flight, then the FIRST one (elwynn) answers LAST - the ordinary case
    /// when one realm is slower or hits the 15s timeout plus retry. Without a guard the elwynn roster
    /// is written after the barrens roster and the player looks at the wrong characters under a rail
    /// that highlights barrens: no exception, no log line, nothing red.</para>
    /// </summary>
    [Fact]
    public async Task ViewModel_LateAnswerFromTheOldRealm_DoesNotOverwriteTheNewOne()
    {
        var feed = new GatedArmory();
        var vm = new ArmoryViewModel(feed, Log);

        var first = vm.LoadAsync("elwynn");
        var second = vm.LoadAsync("barrens");

        feed.Answer("barrens", "Draveth");           // the realm the player is actually looking at
        await second;
        feed.Answer("elwynn", "Torvin", "Belworth"); // the stale answer arrives afterwards
        await first;

        Assert.Equal(["Draveth"], vm.Characters.Select(c => c.Name));
        Assert.Equal("Draveth", vm.Selected?.Name);
        Assert.False(vm.IsLoading);
    }

    /// <summary>Signing out while a load is in flight must not repaint the previous accounts roster
    /// onto a signed-out screen.</summary>
    [Fact]
    public async Task ViewModel_AnswerArrivingAfterSignOut_IsDropped()
    {
        var feed = new GatedArmory();
        var vm = new ArmoryViewModel(feed, Log);

        var load = vm.LoadAsync("elwynn");
        vm.Clear();
        feed.Answer("elwynn", "Torvin");
        await load;

        Assert.Empty(vm.Characters);
        Assert.True(vm.ShowEmpty);
        Assert.Equal("Sign in to see your characters.", vm.EmptyMessage);
    }

    private static ArmoryCharacter Sample(long money, long played) =>
        new(Guid: 1, Name: "Torvin", ClassId: 4, ClassName: "Rogue", RaceName: "Human",
            Level: 60, Online: true, Guild: null, GuildRank: null, Zone: null,
            MoneyCopper: money, PlayedSeconds: played, QuestCount: 0,
            HonorRankName: null, Score: 0, ScoreTier: null, Lockouts: []);
}
