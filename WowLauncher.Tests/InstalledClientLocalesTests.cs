using System.IO;
using System.Linq;
using WowLauncher.Models;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Which languages a 1.14.2 installation offers, read from its own <c>.build.info</c>.
///
/// <para><b>The measurement these tests encode (2026-08-03, local 1.14.2 install, build 42597).</b>
/// Ten locales are tagged in that file — enUS deDE esES esMX frFR koKR ptBR ruRU zhCN zhTW — and each
/// one started the client in that language. itIT, which is NOT tagged, produced
/// <c>ERROR #134 (0x85100086) … Can't find file in build manifest</c> and no window. So a missing
/// locale is a crash, not a fallback, and the list may never be guessed.</para>
/// </summary>
public sealed class InstalledClientLocalesTests
{
    /// <summary>A trimmed copy of the real file: same columns, same tag shape, same trailing '?'.</summary>
    private const string RealShape =
        "Branch!STRING:0|Active!DEC:1|Build Key!HEX:16|CDN Key!HEX:16|Tags!STRING:0|Version!STRING:0|Product!STRING:0\n"
        + "eu|1|a69c90c78c9f0a6c|623412ba548b99ed|"
        + "Windows x86_64 EU? acct-BGR? geoip-BG? deDE speech?:"
        + "Windows x86_64 EU? acct-BGR? geoip-BG? deDE text?:"
        + "Windows x86_64 EU? acct-BGR? geoip-BG? enUS text?:"
        + "Windows x86_64 EU? acct-BGR? geoip-BG? ruRU text?:"
        + "Windows x86_64 EU? acct-BGR? geoip-BG? zhTW text?"
        + "|1.14.2.42597|wow_classic_era\n";

    [Fact]
    public void TheTaggedTextLocalesAreTheOfferedOnes()
    {
        var available = InstalledClientLocales.Parse(RealShape);

        Assert.Equal(new[] { "deDE", "enUS", "ruRU", "zhTW" }, available.Text.OrderBy(x => x, System.StringComparer.Ordinal));
    }

    /// <summary>Text and speech are separate tags and must not be conflated: deDE has both here,
    /// ruRU only text. Writing audioLocale for a locale with no speech is the same #134 crash.</summary>
    [Fact]
    public void SpeechIsTrackedApartFromText()
    {
        var available = InstalledClientLocales.Parse(RealShape);

        Assert.Contains("deDE", available.Speech);
        Assert.DoesNotContain("ruRU", available.Speech);
    }

    /// <summary>A locale that is not tagged is not offered — this is the itIT case, the one that
    /// crashed the client for real.</summary>
    [Fact]
    public void AnUntaggedLocaleIsNeverOffered()
    {
        var available = InstalledClientLocales.Parse(RealShape);

        Assert.DoesNotContain("itIT", available.Text);
    }

    /// <summary>Only an active branch describes THIS installation.</summary>
    [Fact]
    public void AnInactiveBranchIsIgnored()
    {
        var twoBranches = RealShape.TrimEnd('\n')
            + "\nus|0|deadbeefdeadbeef|deadbeefdeadbeef|Windows x86_64 US? frFR text?|1.14.2.42597|wow_classic_era\n";

        var available = InstalledClientLocales.Parse(twoBranches);

        Assert.DoesNotContain("frFR", available.Text);
    }

    /// <summary>Every failure mode lands on English rather than on a guess. A guess here is a client
    /// that will not start.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("garbage without any pipe")]
    [InlineData("Branch!STRING:0|Active!DEC:1\neu|1\n")]   // no Tags column at all
    public void AnythingUnreadableMeansEnglishOnly(string content)
    {
        var available = InstalledClientLocales.Parse(content);

        Assert.Equal(new[] { "enUS" }, available.Text);
    }

    [Fact]
    public void AMissingFileMeansEnglishOnly()
    {
        var available = InstalledClientLocales.Read(Path.Combine(Path.GetTempPath(), "does-not-exist-" + System.Guid.NewGuid()));

        Assert.Equal(new[] { "enUS" }, available.Text);
    }

    /// <summary>English is in the menu even when the file forgot to mention it — every installation
    /// carries it, and an empty language menu is a broken screen.</summary>
    [Fact]
    public void EnglishIsAlwaysPresent()
    {
        var noEnglish =
            "Branch!STRING:0|Active!DEC:1|Tags!STRING:0\neu|1|Windows x86_64 EU? deDE text?\n";

        Assert.Contains("enUS", InstalledClientLocales.Parse(noEnglish).Text);
    }

    [Theory]
    [InlineData("deDE", true)]
    [InlineData("zhTW", true)]
    [InlineData("text", false)]
    [InlineData("EU", false)]
    [InlineData("x86_64", false)]
    public void OnlyRealLocaleCodesAreAccepted(string candidate, bool expected) =>
        Assert.Equal(expected, InstalledClientLocales.LooksLikeLocale(candidate));

    // ── The menu built from it ────────────────────────────────────────────────────────────────

    [Fact]
    public void TheMenuNamesEveryOfferedLocale_AndNothingElse()
    {
        var menu = ClientLocales.ForInstalled(["deDE", "ruRU", "enUS"]);

        Assert.Equal(new[] { "enUS", "deDE", "ruRU" }, menu.Select(l => l.Code));
        Assert.All(menu, l => Assert.False(string.IsNullOrWhiteSpace(l.DisplayName)));
    }

    /// <summary>The client carrying a language is only half the question. vMaNGOS answers in four
    /// languages beside English (frFR deDE esES ruRU); the other five the 1.14.2 package ships would
    /// give a German-looking client whose quests, items and NPCs are all English. Owner decision
    /// 2026-08-03: those do not go in the menu.</summary>
    [Fact]
    public void LanguagesTheRealmCannotAnswerIn_AreNotOffered()
    {
        var everythingTheClientHas = new[]
        { "enUS", "deDE", "esES", "esMX", "frFR", "koKR", "ptBR", "ruRU", "zhCN", "zhTW" };

        var menu = ClientLocales.ForInstalled(everythingTheClientHas).Select(l => l.Code).ToArray();

        Assert.Equal(new[] { "enUS", "deDE", "frFR", "esES", "ruRU" }, menu);
    }

    /// <summary>The four slots are the ones the localisation plan pinned (I4), so a change to this set
    /// is a decision about the realm, not a tidy-up of a list.</summary>
    [Fact]
    public void TheRealmSpeaksExactlyTheVmangosSlots() =>
        Assert.Equal(
            new[] { "deDE", "enUS", "esES", "frFR", "ruRU" },
            ClientLocales.RealmSupported.OrderBy(x => x, System.StringComparer.Ordinal));

    /// <summary>A code we cannot name is dropped rather than shown as a bare code — but only if
    /// something nameable remains, and English always does.</summary>
    [Fact]
    public void AnUnnamedCodeIsDroppedFromTheMenu()
    {
        var menu = ClientLocales.ForInstalled(["deDE", "xxYY"]);

        Assert.Equal(new[] { "enUS", "deDE" }, menu.Select(l => l.Code));
    }

    [Fact]
    public void NoInstallationInformationMeansEnglishOnly()
    {
        Assert.Equal(new[] { "enUS" }, ClientLocales.ForInstalled(null).Select(l => l.Code));
    }
}
