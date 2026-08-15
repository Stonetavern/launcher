using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using WowLauncher.Models;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The language-pack half of the manifest, read from the file that will actually be uploaded rather
/// than from a literal in a test. A schema that parses in a hand-written fixture and then fails on the
/// real file is a defect that only shows up once players are already on it.
/// </summary>
public sealed class LanguagePackManifestTests
{
    private const int Vanilla = 5875;
    private const int ClassicEra = 42597;

    private static ServerManifest Candidate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "manifest-release-candidate.json");
        return JsonSerializer.Deserialize<ServerManifest>(File.ReadAllText(path))!;
    }

    /// <summary>Only what vMaNGOS can answer in. Owner decision 2026-08-03: a language the realm cannot
    /// serve produces a translated client with English quests, which is worse than not offering it —
    /// and the three packs that exist for it (koKR, zhCN, zhTW) were deliberately not uploaded.</summary>
    [Fact]
    public void ThePublishedPacks_AreExactlyTheOnesTheRealmCanServe()
    {
        var packs = Candidate().LanguagePacks;

        Assert.Equal(new[] { "deDE", "esES", "frFR" },
            packs.Select(p => p.Locale).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(packs, p => Assert.Contains(p.Locale, ClientLocales.RealmSupported));
    }

    /// <summary>Every entry has to be downloadable AND verifiable. A pack without a hash is one the
    /// launcher must refuse, so publishing it would be publishing a dead menu entry.</summary>
    [Fact]
    public void EveryPack_IsFullySpecified()
    {
        foreach (var pack in Candidate().LanguagePacks)
        {
            Assert.StartsWith("https://", pack.Url, StringComparison.Ordinal);
            Assert.EndsWith($"-{pack.Locale}.zip", pack.Url, StringComparison.Ordinal);
            Assert.Equal(64, pack.Sha256.Length);
            Assert.True(pack.Size > 0, $"{pack.Locale} has no size");
            Assert.Equal(Vanilla, pack.Build);
        }
    }

    /// <summary>A pack is an MPQ. The 1.14.2 client is CASC-based and reads no MPQ at all, so handing
    /// it one would install ~90 MB that changes nothing and report success.</summary>
    [Fact]
    public void TheModernClient_IsOfferedNoPacks()
    {
        var manifest = Candidate();

        Assert.Null(manifest.LanguagePackFor("deDE", ClassicEra));
        Assert.NotNull(manifest.LanguagePackFor("deDE", Vanilla));
    }

    /// <summary>A locale nobody published resolves to nothing — never to another locale's pack, which
    /// would download, verify and extract cleanly on its way to installing the wrong language.</summary>
    [Fact]
    public void AnUnpublishedLocale_ResolvesToNothing()
    {
        var manifest = Candidate();

        Assert.Null(manifest.LanguagePackFor("ruRU", Vanilla));
        Assert.Null(manifest.LanguagePackFor("koKR", Vanilla));
    }

    /// <summary>A manifest from before this field existed still deserialises, and simply offers no
    /// packs — the launcher then shows whatever is installed on disk.</summary>
    [Fact]
    public void AManifestWithoutTheField_IsNotAnError()
    {
        var manifest = JsonSerializer.Deserialize<ServerManifest>(
            """{"product":"stonetavern-classic","current_version":"1.0.0"}""")!;

        Assert.Empty(manifest.LanguagePacks);
        Assert.Null(manifest.LanguagePackFor("deDE", Vanilla));
    }
}
