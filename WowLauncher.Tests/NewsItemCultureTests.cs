using System;
using System.Globalization;
using System.Threading;
using WowLauncher.Models;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The news rail renders in English, whatever the machine thinks its language is.
///
/// THE BUG THIS PINS DOWN
///
/// <see cref="NewsItem.DateShort"/> hardcoded <c>CultureInfo.GetCultureInfo("de-DE")</c>, so the rail
/// showed "27 JULI" for 2026-07-27. German does not abbreviate July, so the full month name appeared
/// on a surface that is English by decision ((internal design notes, not published); Localization/Loc.cs ships one
/// catalogue, "en"). Because the culture was NAMED rather than inherited, the German text reached every
/// player, including those on an English Windows, which is why nobody could reproduce it as a
/// locale problem.
///
/// WHY THE TESTS SWAP THE THREAD CULTURE
///
/// A test that only asserts "27 JUL" on the build machine would have passed against the broken code on
/// a German workstation and failed on an English one, i.e. it would have measured the machine rather
/// than the code. Pinning the thread to a culture that formats differently proves the property that
/// actually matters: the output does not depend on the culture at all.
/// </summary>
public sealed class NewsItemCultureTests
{
    /// <summary>Runs an assertion with the thread pinned to a culture, then puts it back.</summary>
    private static void UnderCulture(string name, Action body)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            var culture = CultureInfo.GetCultureInfo(name);
            Thread.CurrentThread.CurrentCulture = culture;
            body();
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("2026-07-27", "27 JUL")]
    [InlineData("2026-06-26", "26 JUN")]
    [InlineData("2026-01-01", "01 JAN")]
    [InlineData("2026-12-31", "31 DEC")]
    public void DateShort_uses_the_English_month_abbreviation(string iso, string expected)
    {
        Assert.Equal(expected, new NewsItem { Date = iso }.DateShort);
    }

    [Theory]
    [InlineData("de-DE")] // the culture that was hardcoded; abbreviates July as "Juli"
    [InlineData("fr-FR")] // "juil."
    [InlineData("ru-RU")] // Cyrillic month names
    [InlineData("en-US")]
    public void DateShort_ignores_the_culture_of_the_machine(string culture)
    {
        UnderCulture(culture, () =>
            Assert.Equal("27 JUL", new NewsItem { Date = "2026-07-27" }.DateShort));
    }

    [Fact]
    public void DateShort_never_renders_a_German_month_name()
    {
        /* The specific symptom that was reported, named so a regression is recognisable in the
         * failure output rather than being read out of a diff. */
        Assert.DoesNotContain("JULI", new NewsItem { Date = "2026-07-27" }.DateShort, StringComparison.Ordinal);
        Assert.DoesNotContain("MAERZ", new NewsItem { Date = "2026-03-15" }.DateShort, StringComparison.Ordinal);
        Assert.DoesNotContain("DEZ", new NewsItem { Date = "2026-12-31" }.DateShort, StringComparison.Ordinal);
    }

    [Fact]
    public void DateShort_reads_the_ISO_date_the_feed_actually_sends()
    {
        /* Parsing was culture-dependent too. Under a culture that reads dates day-first, an ambiguous
         * value can silently become a different date, which is worse than a wrong month NAME because
         * nothing about it looks wrong. */
        UnderCulture("de-DE", () =>
            Assert.Equal("03 FEB", new NewsItem { Date = "2026-02-03" }.DateShort));
    }

    [Fact]
    public void DateShort_falls_back_to_the_raw_value_when_it_is_not_a_date()
    {
        /* The feed is a file on a host somebody uploads by hand, so it can carry anything. Showing the
         * raw string beats showing a fabricated date. */
        Assert.Equal("SOON", new NewsItem { Date = "soon" }.DateShort);
    }

    [Theory]
    [InlineData("ban-wave")]
    [InlineData("BAN-WAVE")]
    [InlineData("ban-welle")] // the old German spelling, still accepted as INPUT
    public void CategoryTag_shows_the_ban_wave_chip_in_English(string category)
    {
        Assert.Equal("BAN-WAVE", new NewsItem { Category = category }.CategoryTag);
    }
}
