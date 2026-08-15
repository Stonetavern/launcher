using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using WowLauncher.Localization;
using WowLauncher.Models;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The translated catalogs against the English source.
///
/// <para>Everything here guards a failure that ships GREEN. A translated catalog is a pile of strings
/// nobody compiles: a dropped key shows English in one corner of an otherwise German interface, a
/// mistyped <c>{0}</c> throws <see cref="FormatException"/> at the moment the string is shown (which
/// is usually the moment something has already gone wrong), and a swapped <c>{0}</c>/<c>{1}</c>
/// prints the file count where the file name belongs.</para>
/// </summary>
public sealed class LocalizationParityTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)(?::[^}]*)?\}", RegexOptions.Compiled);

    /// <summary>Every UI language embedded in this build except the source language.</summary>
    public static TheoryData<string> Translations()
    {
        var data = new TheoryData<string>();
        foreach (var code in Loc.Shipped.Where(c => c != Loc.Code)) data.Add(code);
        return data;
    }

    private static Dictionary<string, string> Catalog(string code)
    {
        var catalog = Loc.Load(code);
        Assert.True(catalog is not null, $"{code}.json is not embedded in the assembly.");
        return catalog!;
    }

    private static IEnumerable<KeyValuePair<string, string>> Content(Dictionary<string, string> c) =>
        c.Where(kv => !kv.Key.StartsWith('_'));

    /// <summary>Every language the realm can answer in has a catalog. Without this, adding a locale to
    /// the game menu and forgetting the launcher half is invisible until a Russian player opens an
    /// English launcher.</summary>
    [Fact]
    public void EveryRealmLanguage_HasACatalog()
    {
        var missing = ClientLocales.RealmSupported
            .Select(locale => Loc.ForClientLocale.TryGetValue(locale, out var ui) ? ui : null)
            .Where(ui => ui is null || !Loc.Shipped.Contains(ui))
            .ToList();

        Assert.True(missing.Count == 0,
            "These realm languages have no launcher catalog: " + string.Join(", ", missing));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void ATranslation_CarriesExactlyTheEnglishKeys(string code)
    {
        var english = Content(Catalog(Loc.Code)).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var other = Content(Catalog(code)).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);

        var missing = english.Except(other).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var extra = other.Except(english).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            $"{code}.json is missing these keys, so they show up in English:\n{string.Join("\n", missing)}");
        Assert.True(extra.Count == 0,
            $"{code}.json has keys English does not, so they are dead weight:\n{string.Join("\n", extra)}");
    }

    /// <summary>Same placeholders, same numbers. A translation that drops <c>{1}</c> loses a value the
    /// sentence was built around; one that invents <c>{2}</c> throws when it is formatted.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    public void ATranslation_KeepsEveryPlaceholder(string code)
    {
        var english = Catalog(Loc.Code);
        var other = Catalog(code);

        var wrong = new List<string>();
        foreach (var (key, value) in Content(english))
        {
            if (!other.TryGetValue(key, out var translated)) continue;
            var want = Placeholder.Matches(value).Select(m => m.Groups[1].Value).OrderBy(x => x).ToList();
            var have = Placeholder.Matches(translated).Select(m => m.Groups[1].Value).OrderBy(x => x).ToList();
            if (!want.SequenceEqual(have, StringComparer.Ordinal))
                wrong.Add($"{key}: English has {{{string.Join(",", want)}}}, {code} has {{{string.Join(",", have)}}}");
        }

        Assert.True(wrong.Count == 0,
            $"Placeholder mismatch in {code}.json - these throw or print the wrong value at runtime:\n" +
            string.Join("\n", wrong));
    }

    /// <summary>Nothing was left in English by accident. Not every identical string is a mistake
    /// (Discord is Discord), so this only fails when a catalog is mostly untranslated.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    public void ATranslation_IsActuallyTranslated(string code)
    {
        var english = Catalog(Loc.Code);
        var other = Catalog(code);

        var shared = Content(english)
            .Where(kv => other.TryGetValue(kv.Key, out var t) && t == kv.Value)
            .Select(kv => kv.Key)
            .ToList();

        var ratio = (double)shared.Count / Content(english).Count();
        Assert.True(ratio < 0.25,
            $"{code}.json is {ratio:P0} identical to English. Untranslated keys:\n" +
            string.Join("\n", shared.Take(30)));
    }

    /// <summary>Typographic dashes are out in every language (VOICE.md). Apostrophes are NOT checked
    /// here: French cannot be written without them, and enforcing an English rule on French would
    /// produce broken French to satisfy a style guide.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    public void ATranslation_UsesNoTypographicDashes(string code)
    {
        var offenders = Content(Catalog(code))
            .Where(kv => kv.Value.Contains('—') || kv.Value.Contains('–'))
            .Select(kv => $"{kv.Key}: {kv.Value}")
            .ToList();

        Assert.True(offenders.Count == 0,
            $"VOICE.md forbids em and en dashes:\n{string.Join("\n", offenders)}");
    }

    // ── Switching ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The launcher has no language picker of its own: it follows the language the player
    /// picked for the GAME. Both shapes of identifier therefore have to resolve.</summary>
    [Theory]
    [InlineData("deDE", "de")]
    [InlineData("de", "de")]
    [InlineData("esMX", "es")]
    [InlineData("enUS", "en")]
    [InlineData("itIT", "en")]     // a locale with no catalog stays English
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void ALanguageIdentifier_ResolvesToAShippedCatalog(string? input, string expected) =>
        Assert.Equal(expected, Loc.Normalise(input));

    [Fact]
    public void SwitchingLanguage_ChangesWhatTheUiShows()
    {
        var english = Loc.T("Play_Cta_Play");
        try
        {
            Assert.Equal("de", Loc.Use("deDE"));
            var german = Loc.T("Play_Cta_Play");
            Assert.NotEqual(english, german);
        }
        finally
        {
            Loc.Use(Loc.Code);
        }
        Assert.Equal(english, Loc.T("Play_Cta_Play"));
    }

    /// <summary>A language we do not ship leaves the interface as it was, rather than emptying it.
    /// This is the difference between "we have no Italian" and "the launcher went blank".</summary>
    [Fact]
    public void AnUnshippedLanguage_LeavesTheInterfaceAlone()
    {
        var before = Loc.T("Play_Cta_Play");

        Assert.Equal(Loc.Current, Loc.Use("itIT"));

        Assert.Equal(before, Loc.T("Play_Cta_Play"));
    }

    /// <summary>A key a translation is missing falls back to English, not to the raw key. A catalog a
    /// few entries behind stays usable instead of showing Play_Cta_Play to a player.</summary>
    [Fact]
    public void AMissingKey_FallsBackToEnglish_NotToTheKeyName()
    {
        try
        {
            Loc.Use("de");
            // Every key exists in every catalog (the parity test above enforces that), so this proves
            // the fallback with a key no catalog has.
            Assert.Equal("Nope_Not_A_Key", Loc.T("Nope_Not_A_Key"));

            using (Loc.OverrideForTests("Play_Cta_Play", ""))
                Assert.Equal(Loc.Load(Loc.Code)!["Play_Cta_Play"], Loc.T("Play_Cta_Play"));
        }
        finally
        {
            Loc.Use(Loc.Code);
        }
    }
}
