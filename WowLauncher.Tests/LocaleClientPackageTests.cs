using System;
using System.Linq;
using System.Text.Json;
using WowLauncher.Models;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// A language that is a whole CLIENT rather than an add-on pack.
///
/// <para>Russian 1.12.1 is one. Measured 2026-08-04: the Russian localisation was built against a
/// different client - its <c>Interface\GlueXML\AccountLogin.xml</c> is 54721 bytes against our 25651
/// - and our executable refuses it before a window appears, with a Russian dialog saying the login
/// interface files are damaged. Replacing the interface XML with ours did not help. So Russian ships
/// as its own download.</para>
///
/// <para>What these tests guard is the failure that would be invisible: handing a player the wrong
/// package. It downloads, it verifies against its hash, it extracts cleanly - and only then does the
/// player find out their client is in a language they cannot read, several gigabytes later.</para>
/// </summary>
public sealed class LocaleClientPackageTests
{
    private const int Vanilla = 5875;
    private const int ClassicEra = 42597;

    private static PhaseManifest Phase(string json) =>
        JsonSerializer.Deserialize<PhaseManifest>(json)!;

    private static string ThisOs =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    private static ServerManifest WithRussian() => JsonSerializer.Deserialize<ServerManifest>("""
        {"phases":[{"phase":"vanilla",
          "client":{"url":"https://x.invalid/neutral.zip","sha256":"a"},
          "clients":[{"build":5875,"url":"https://x.invalid/english.zip","sha256":"b"}]}],
         "language_clients":[
           {"build":5875,"locale":"ruRU","url":"https://x.invalid/russian.zip","sha256":"c"}]}
        """)!;

    [Fact]
    public void PickingRussian_ResolvesToTheRussianPackage()
    {
        var chosen = WithRussian().ClientForLocale(Vanilla, "ruRU");

        Assert.Equal("https://x.invalid/russian.zip", chosen!.Url);
    }

    /// <summary>Every other language keeps getting the neutral package - it is the English client plus
    /// an add-on pack, and that path must not change because Russian appeared.</summary>
    [Theory]
    [InlineData("enUS")]
    [InlineData("deDE")]
    [InlineData("frFR")]
    [InlineData(null)]
    public void EveryOtherLanguage_KeepsTheNeutralPackage(string? locale)
    {
        var manifest = WithRussian();

        Assert.Null(manifest.ClientForLocale(Vanilla, locale));
        Assert.Equal("https://x.invalid/english.zip",
            manifest.Phases[0].ClientForBuild(Vanilla, Vanilla)!.Url);
    }

    /// <summary>
    /// The defect this whole field exists to prevent, held down by a test.
    ///
    /// <para>The launcher in players hands resolves a client by build and OS and knows nothing about
    /// a language. A Russian entry sitting in <c>phases[].clients</c> for build 5875 is therefore the
    /// only entry matching that build, and every 1.12.1 player gets it - 6.2 GB that downloads,
    /// verifies against its hash and extracts cleanly before anyone sees a Cyrillic letter. Simulated
    /// against the shipped resolver on 2026-08-04, which is how it was caught.</para>
    /// </summary>
    [Fact]
    public void ALocaleClient_IsInvisibleToAResolverThatKnowsNoLanguages()
    {
        var manifest = WithRussian();

        // Exactly what the shipped launcher computes: build plus OS, no language anywhere.
        var blind = manifest.Phases[0].ClientForBuild(Vanilla, Vanilla);

        Assert.Equal("https://x.invalid/english.zip", blind!.Url);
        Assert.DoesNotContain(manifest.Phases.SelectMany(p => p.Clients),
            c => !string.IsNullOrWhiteSpace(c.Locale));
    }

    /// <summary>A manifest that predates this field resolves exactly as it did before. The whole point
    /// of the field being optional is that nothing already published changes meaning.</summary>
    [Fact]
    public void AManifestWithoutLocales_ResolvesAsItAlwaysDid()
    {
        var old = Phase("""
            {"phase":"vanilla",
             "client":{"url":"https://x.invalid/canonical.zip","sha256":"a"},
             "clients":[{"build":42597,"url":"https://x.invalid/modern.zip","sha256":"b"}]}
            """);

        Assert.Equal("https://x.invalid/canonical.zip", old.ClientForBuild(Vanilla, Vanilla)!.Url);
        Assert.Equal("https://x.invalid/modern.zip", old.ClientForBuild(ClassicEra, Vanilla)!.Url);
    }

    /// <summary>Platform still applies inside a language: a Russian package for another OS is not a
    /// Russian package for this one.</summary>
    [Fact]
    public void WithinALanguage_ThePlatformStillDecides()
    {
        var manifest = JsonSerializer.Deserialize<ServerManifest>($$"""
            {"language_clients":[
              {"build":5875,"locale":"ruRU","os":"{{ThisOs}}","url":"https://x.invalid/ru-here.zip","sha256":"a"}]}
            """)!;

        Assert.Equal("https://x.invalid/ru-here.zip", manifest.ClientForLocale(Vanilla, "ruRU")!.Url);

        var elsewhere = JsonSerializer.Deserialize<ServerManifest>("""
            {"language_clients":[
              {"build":5875,"locale":"ruRU","os":"haiku","url":"https://x.invalid/ru-there.zip","sha256":"a"}]}
            """)!;

        Assert.Null(elsewhere.ClientForLocale(Vanilla, "ruRU"));
    }

    /// <summary>The launcher needs to know WHICH languages are whole clients, because those must skip
    /// the pack machinery entirely - there is no MPQ to move into a patch slot.</summary>
    [Fact]
    public void TheLauncherCanTell_WhichLanguagesAreWholeClients()
    {
        var manifest = WithRussian();

        Assert.Equal(new[] { "ruRU" }, manifest.ClientLocalesForBuild(Vanilla));
        Assert.Empty(manifest.ClientLocalesForBuild(ClassicEra));
    }

    [Fact]
    public void AnEmptyManifest_NamesNoWholeClientLanguages() =>
        Assert.Empty(new ServerManifest().ClientLocalesForBuild(Vanilla));

    // ── The actual file that gets uploaded ───────────────────────────────────────────────────────

    private static ServerManifest Candidate()
    {
        var path = System.IO.Path.Combine(
            AppContext.BaseDirectory, "fixtures", "manifest-release-candidate.json");
        return JsonSerializer.Deserialize<ServerManifest>(System.IO.File.ReadAllText(path))!;
    }

    /// <summary>Read from the file that will be uploaded, not from a literal: a schema that parses in
    /// a hand-written fixture and then fails on the real manifest is a defect nobody sees until
    /// players are on it.</summary>
    [Fact]
    public void ThePublishedManifest_ServesRussianAsItsOwnClient()
    {
        var manifest = Candidate();

        var russian = manifest.ClientForLocale(Vanilla, "ruRU");
        Assert.NotNull(russian);
        Assert.Equal("ruRU", russian!.Locale);
        Assert.Equal(64, russian.Sha256.Length);
        Assert.True(russian.Size > 1_000_000_000, "a whole client is gigabytes, not megabytes");
        Assert.StartsWith("https://", russian.Url, StringComparison.Ordinal);

        Assert.Equal(new[] { "ruRU" }, manifest.ClientLocalesForBuild(Vanilla));
    }

    /// <summary>And German still gets the English package plus its pack - the Russian entry must not
    /// have moved anyone else onto a different download.</summary>
    [Fact]
    public void ThePublishedManifest_LeavesEveryOtherLanguageOnTheNeutralPackage()
    {
        var phase = Candidate().Phases.Single(p => p.Phase == "vanilla");

        foreach (var locale in new[] { "enUS", "deDE", "frFR", "esES" })
            Assert.Null(Candidate().ClientForLocale(Vanilla, locale));

        var neutral = phase.ClientForBuild(Vanilla, Vanilla);
        Assert.NotNull(neutral);
        Assert.Null(neutral!.Locale);
    }

    /// <summary>A language that is a whole client must NOT also be published as a pack: the launcher
    /// would then have two ways to become Russian, and the pack one does not work.</summary>
    [Fact]
    public void ALanguageIsEitherAPackOrAClient_NeverBoth()
    {
        var manifest = Candidate();
        var asClient = manifest.ClientLocalesForBuild(Vanilla).ToHashSet(StringComparer.Ordinal);
        var asPack = manifest.LanguagePacks.Select(p => p.Locale).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(asClient.Intersect(asPack, StringComparer.Ordinal));
    }
}
