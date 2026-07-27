using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WowLauncher.Models;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The additive per-build client schema (<c>phases[].clients[]</c>), which lets one phase publish
/// download coordinates for more than one client build - the change that turns 1.14.2 from
/// "bring your own" into something the launcher can fetch.
///
/// <para>Two properties matter and both are pinned here: an OLD manifest (no <c>clients</c> array)
/// must behave exactly as it did before, and the phase's singular <c>client</c> must never be handed
/// to a build it was not published for. The second one is the whole reason the earlier hard guard
/// existed; loosening it wrongly would download and extract a 1.12.1 client over a 1.14.2 install.</para>
/// </summary>
public sealed class PerBuildClientManifestTests
{
    private const int Vanilla = 5875;
    private const int ClassicEra = 42597;

    private static PhaseManifest Deserialize(string json) =>
        JsonSerializer.Deserialize<ServerManifest>(json)!.Phases.Single();

    /// <summary>Exactly the shape live today (fetched 2026-07-22): one phase, one client, no
    /// <c>clients</c> array anywhere.</summary>
    private const string OldManifest = """
    {
      "product": "stonetavern-classic",
      "current_version": "1.12.1",
      "active_phase": "vanilla",
      "base": {
        "version": "1.12.1",
        "url": "https://downloads.example.invalid/base.zip",
        "size": 5316714041,
        "sha256": "9cd0a7b80441266a79bc0ab7f6306fa1910446571d0d496f88aceda3b266eb0b"
      },
      "phases": [
        {
          "phase": "vanilla",
          "realmlist": "play.stonetavern.app",
          "client": {
            "version": "1.12.1",
            "url": "https://downloads.example.invalid/client-1.12.1.zip",
            "size": 5316714041,
            "sha256": "9cd0a7b80441266a79bc0ab7f6306fa1910446571d0d496f88aceda3b266eb0b"
          }
        }
      ]
    }
    """;

    /// <summary>The same manifest extended additively: the singular <c>client</c> is untouched, a
    /// <c>clients</c> array is added beside it.</summary>
    private const string NewManifest = """
    {
      "product": "stonetavern-classic",
      "current_version": "1.12.1",
      "active_phase": "vanilla",
      "phases": [
        {
          "phase": "vanilla",
          "realmlist": "play.stonetavern.app",
          "client": {
            "version": "1.12.1",
            "url": "https://downloads.example.invalid/client-1.12.1.zip",
            "size": 5316714041,
            "sha256": "9cd0a7b80441266a79bc0ab7f6306fa1910446571d0d496f88aceda3b266eb0b"
          },
          "clients": [
            {
              "build": 42597,
              "version": "1.3.1",
              "url": "https://downloads.example.invalid/modern-1.14.2-v1.3.1.zip",
              "size": 9876543210,
              "sha256": "aaaabbbbccccddddeeeeffff00001111222233334444555566667777888899990"
            }
          ]
        }
      ]
    }
    """;

    // ── Backward compatibility ────────────────────────────────────────────────────────────────

    [Fact]
    public void AnOldManifest_StillDeserializes_WithAnEmptyClientsList()
    {
        var phase = Deserialize(OldManifest);

        Assert.Empty(phase.Clients);
        Assert.NotNull(phase.Client);
    }

    [Fact]
    public void AnOldManifest_StillServesTheCanonicalBuild()
    {
        var phase = Deserialize(OldManifest);

        var coords = phase.ClientForBuild(Vanilla, canonicalBuild: Vanilla);

        Assert.NotNull(coords);
        Assert.Equal("https://downloads.example.invalid/client-1.12.1.zip", coords!.Url);
    }

    /// <summary>The rule the old hard guard enforced, now enforced by the lookup itself: an old
    /// manifest offers NOTHING for a build it never published, rather than the wrong zip.</summary>
    [Fact]
    public void AnOldManifest_OffersNothingForASecondBuild()
    {
        var phase = Deserialize(OldManifest);

        Assert.Null(phase.ClientForBuild(ClassicEra, canonicalBuild: Vanilla));
    }

    // ── The new capability ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ANewManifest_ServesTheSecondBuildFromItsOwnEntry()
    {
        var phase = Deserialize(NewManifest);

        var coords = phase.ClientForBuild(ClassicEra, canonicalBuild: Vanilla);

        Assert.NotNull(coords);
        Assert.Equal("https://downloads.example.invalid/modern-1.14.2-v1.3.1.zip", coords!.Url);
        Assert.Equal("1.3.1", coords.Version);
        Assert.Equal(42597, coords.Build);
    }

    [Fact]
    public void ANewManifest_StillServesTheCanonicalBuildFromTheSingularClient()
    {
        var phase = Deserialize(NewManifest);

        var coords = phase.ClientForBuild(Vanilla, canonicalBuild: Vanilla);

        Assert.Equal("https://downloads.example.invalid/client-1.12.1.zip", coords!.Url);
    }

    /// <summary>An explicit entry wins over the singular client even for the canonical build, so a
    /// server can move a build onto a new package without rewriting the legacy field.</summary>
    [Fact]
    public void AnExplicitEntryForTheCanonicalBuild_WinsOverTheSingularClient()
    {
        var phase = Deserialize("""
        {
          "phases": [{
            "phase": "vanilla",
            "client": { "version": "1.12.1", "url": "https://old.invalid/legacy.zip", "sha256": "x" },
            "clients": [{ "build": 5875, "version": "1.12.1", "url": "https://new.invalid/current.zip", "sha256": "y" }]
          }]
        }
        """);

        Assert.Equal("https://new.invalid/current.zip", phase.ClientForBuild(Vanilla, Vanilla)!.Url);
    }

    [Fact]
    public void AnUnpublishedBuild_StillGetsNothing_EvenWithAClientsArrayPresent()
    {
        var phase = Deserialize(NewManifest);

        Assert.Null(phase.ClientForBuild(12340, canonicalBuild: Vanilla));   // Wrath, not published
    }

    /// <summary>An entry that forgot to say which build it is for must never be handed to whichever
    /// client happens to ask. Null is not a wildcard.</summary>
    [Fact]
    public void AnEntryWithoutABuild_NeverMatchesAnything()
    {
        var phase = Deserialize("""
        {
          "phases": [{
            "phase": "vanilla",
            "clients": [{ "version": "1.3.1", "url": "https://x.invalid/nameless.zip", "sha256": "z" }]
          }]
        }
        """);

        Assert.Null(phase.ClientForBuild(ClassicEra, Vanilla));
        Assert.Null(phase.ClientForBuild(Vanilla, Vanilla));   // no singular client either
    }

    // ── Per-OS packages ───────────────────────────────────────────────────────────────────────

    /// <summary>The shape the modern client actually needs: two packages for ONE build, because the
    /// Windows and Linux packages carry different proxies and are not interchangeable.</summary>
    private const string PerOsManifest = """
    {
      "phases": [{
        "phase": "vanilla",
        "client": { "version": "1.12.1", "url": "https://x.invalid/client-1.12.1.zip", "sha256": "a" },
        "clients": [
          { "build": 42597, "os": "windows", "version": "1.3.1", "url": "https://x.invalid/modern-win.zip", "sha256": "b" },
          { "build": 42597, "os": "linux",   "version": "1.3.1", "url": "https://x.invalid/modern-linux.zip", "sha256": "c" }
        ]
      }]
    }
    """;

    [Fact]
    public void APerOsBuild_ResolvesToThePackageForThisMachine()
    {
        var coords = Deserialize(PerOsManifest).ClientForBuild(ClassicEra, Vanilla);

        Assert.NotNull(coords);
        var expected = OperatingSystem.IsWindows()
            ? "https://x.invalid/modern-win.zip"
            : "https://x.invalid/modern-linux.zip";
        Assert.Equal(expected, coords!.Url);
    }

    [Fact]
    public void AnOsNeutralEntry_MatchesEveryMachine()
    {
        var phase = Deserialize("""
        {"phases":[{"phase":"vanilla","clients":[
          {"build":42597,"url":"https://x.invalid/anywhere.zip","sha256":"a"}]}]}
        """);

        Assert.Equal("https://x.invalid/anywhere.zip", phase.ClientForBuild(ClassicEra, Vanilla)!.Url);
    }

    /// <summary>A build published ONLY for other platforms must resolve to nothing - never to the
    /// phase's canonical 1.12.1 package as a consolation prize, which would download and extract the
    /// wrong client over a working install.</summary>
    [Fact]
    public void ABuildPublishedOnlyForAnotherOs_ResolvesToNothing_NotToTheCanonicalPackage()
    {
        var otherOs = OperatingSystem.IsWindows() ? "linux" : "windows";
        var phase = Deserialize($$"""
        {"phases":[{"phase":"vanilla",
          "client":{"version":"1.12.1","url":"https://x.invalid/client-1.12.1.zip","sha256":"a"},
          "clients":[{"build":42597,"os":"{{otherOs}}","url":"https://x.invalid/wrong-os.zip","sha256":"b"}]}]}
        """);

        Assert.Null(phase.ClientForBuild(ClassicEra, Vanilla));
    }

    /// <summary>
    /// The case the previous test does NOT reach, discovered by a mutation probe: when the build in
    /// question IS the canonical one, deleting the "published, but not for this OS" guard changes
    /// nothing above (42597 never equals 5875, so the canonical fallback declines anyway) and the
    /// probe stayed green. Here the server has re-published the CANONICAL build per OS - the moment a
    /// legacy singular <c>client</c> still sits beside it, dropping the guard silently serves that old
    /// package to a platform the server just said it is not for.
    /// </summary>
    [Fact]
    public void TheCanonicalBuild_PublishedOnlyForAnotherOs_DoesNotFallBackToTheLegacyPackage()
    {
        var otherOs = OperatingSystem.IsWindows() ? "linux" : "windows";
        var phase = Deserialize($$"""
        {"phases":[{"phase":"vanilla",
          "client":{"version":"1.12.1","url":"https://x.invalid/legacy-1.12.1.zip","sha256":"a"},
          "clients":[{"build":5875,"os":"{{otherOs}}","url":"https://x.invalid/wrong-os.zip","sha256":"b"}]}]}
        """);

        Assert.Null(phase.ClientForBuild(Vanilla, canonicalBuild: Vanilla));
    }

    /// <summary>An OS string this launcher does not know is not a match. "Probably fine" would hand a
    /// player a package built for something else.</summary>
    [Fact]
    public void AnUnknownOsString_IsNotAMatch()
    {
        var phase = Deserialize("""
        {"phases":[{"phase":"vanilla","clients":[
          {"build":42597,"os":"haiku","url":"https://x.invalid/haiku.zip","sha256":"a"}]}]}
        """);

        Assert.Null(phase.ClientForBuild(ClassicEra, Vanilla));
    }

    [Fact]
    public void AnOsSpecificEntry_WinsOverAnOsNeutralOne()
    {
        var thisOs = OperatingSystem.IsWindows() ? "windows" : "linux";
        var phase = Deserialize($$"""
        {"phases":[{"phase":"vanilla","clients":[
          {"build":42597,"url":"https://x.invalid/generic.zip","sha256":"a"},
          {"build":42597,"os":"{{thisOs}}","url":"https://x.invalid/specific.zip","sha256":"b"}]}]}
        """);

        Assert.Equal("https://x.invalid/specific.zip", phase.ClientForBuild(ClassicEra, Vanilla)!.Url);
    }

    // ── The actual file that gets uploaded ────────────────────────────────────────────────────

    /// <summary>
    /// The candidate production manifest (<c>deploy/manifest-with-1142.json</c>) read as a file, not
    /// as a string literal in a test. A schema that parses in a hand-written fixture and then fails on
    /// the real file is exactly the kind of gap that only shows up in production, so the file that will
    /// be uploaded is the file that is tested.
    /// </summary>
    [Fact]
    public void TheProductionCandidateManifest_ParsesAndServesBothBuilds()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "manifest-prod-candidate.json"));
        var manifest = JsonSerializer.Deserialize<ServerManifest>(json);

        Assert.NotNull(manifest);
        var phase = manifest!.Phases.Single(p => p.Phase == "vanilla");

        var legacy = phase.ClientForBuild(Vanilla, Vanilla);
        Assert.NotNull(legacy);
        Assert.Contains("Stonetavern-WoW-1.12.1", legacy!.Url);

        var modern = phase.ClientForBuild(ClassicEra, Vanilla);
        Assert.NotNull(modern);
        Assert.Equal("1.3.1", modern!.Version);
        // Every published package carries a hash: the launcher verifies before extracting, so an entry
        // without one is a download it would have to refuse.
        Assert.Equal(64, modern.Sha256.Length);
        Assert.True(modern.Size > 0);

        // The two packages are NOT interchangeable - the Windows one has no Arctium launcher, the
        // Linux one has a native proxy - so each platform must land on its own.
        var expectedMarker = OperatingSystem.IsWindows() ? "-v1.3.1.zip" : "-Linux-v1.3.1.zip";
        Assert.EndsWith(expectedMarker, modern.Url);
    }

    /// <summary>Both platform packages are declared, whichever platform the suite happens to run
    /// on - otherwise a manifest missing the Linux half would still pass on a Windows CI box.</summary>
    [Fact]
    public void TheProductionCandidateManifest_DeclaresBothPlatformPackages()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "manifest-prod-candidate.json"));
        var phase = JsonSerializer.Deserialize<ServerManifest>(json)!.Phases.Single(p => p.Phase == "vanilla");

        var modern = phase.Clients.Where(c => c.Build == ClassicEra).ToList();
        Assert.Equal(2, modern.Count);
        Assert.Contains(modern, c => c.Os == "windows");
        Assert.Contains(modern, c => c.Os == "linux");
        Assert.All(modern, c => Assert.Equal(64, c.Sha256.Length));
        Assert.All(modern, c => Assert.StartsWith("https://", c.Url));
    }

    /// <summary>A launcher older than this field ignores it: proven by round-tripping the new manifest
    /// through the same deserializer with the unknown member left in - System.Text.Json skips unknown
    /// properties by default, and the test exists so a future opt into strict parsing cannot break the
    /// wire contract silently.</summary>
    [Fact]
    public void UnknownFieldsInTheManifest_AreIgnoredRatherThanFatal()
    {
        var manifest = JsonSerializer.Deserialize<ServerManifest>("""
        {
          "product": "stonetavern-classic",
          "some_future_field": { "nested": [1, 2, 3] },
          "phases": [{ "phase": "vanilla", "client": { "url": "https://x.invalid/a.zip" }, "whatever": 5 }]
        }
        """);

        Assert.NotNull(manifest);
        Assert.Equal("vanilla", manifest!.Phases.Single().Phase);
    }
}
