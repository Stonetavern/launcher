using System;
using System.IO;
using System.Linq;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// One addon set per realm. The active set is always the real <c>Interface/AddOns</c> (the game knows
/// no other path); the others sit beside it as <c>AddOns.&lt;profile&gt;</c> and are swapped in by
/// renaming, which copies nothing and needs no elevation.
///
/// <para>What these tests are really guarding is a player's addons. The dangerous states are: a
/// half-finished switch (one folder parked, the other never arrived), a folder the launcher did not
/// create being thrown away, and a rename that failed but reported success.</para>
/// </summary>
public sealed class AddonProfileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "st-profiles-" + Guid.NewGuid().ToString("N"));

    private static Serilog.ILogger Log() => new Serilog.LoggerConfiguration().CreateLogger();
    private AddonProfileService NewService() => new(Log());

    private string Addons => Path.Combine(_root, "Interface", "AddOns");
    private string Parked(string profile) => Addons + "." + profile;

    private void MakeAddons(params string[] folders)
    {
        Directory.CreateDirectory(Addons);
        foreach (var f in folders) Directory.CreateDirectory(Path.Combine(Addons, f));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void AFreshSwitch_CreatesTheProfileAndMarksIt()
    {
        var svc = NewService();

        var result = svc.Switch(_root, "elwynn");

        Assert.True(result.Ok);
        Assert.True(Directory.Exists(Addons));
        Assert.Equal("elwynn", svc.ActiveProfile(_root));
    }

    /// <summary>The case that must never lose anything: an AddOns folder that was there before
    /// profiles existed belongs to the player, so it becomes their current profile rather than being
    /// parked under a name they never chose - or replaced by an empty one.</summary>
    [Fact]
    public void AnExistingUnmarkedFolder_IsAdopted_NotDiscarded()
    {
        MakeAddons("Questie", "Details");
        var svc = NewService();

        var result = svc.Switch(_root, "elwynn");

        Assert.True(result.Ok);
        Assert.Equal("elwynn", svc.ActiveProfile(_root));
        Assert.True(Directory.Exists(Path.Combine(Addons, "Questie")));
        Assert.True(Directory.Exists(Path.Combine(Addons, "Details")));
    }

    [Fact]
    public void SwitchingAway_ParksTheOldSet_AndBringsTheNewOneBackLater()
    {
        var svc = NewService();
        MakeAddons("Questie");
        svc.Switch(_root, "elwynn");                       // adopts Questie as elwynn

        var toBarrens = svc.Switch(_root, "barrens");
        Directory.CreateDirectory(Path.Combine(Addons, "PallyPower"));   // installed on barrens

        var backToElwynn = svc.Switch(_root, "elwynn");

        Assert.True(toBarrens.Ok);
        Assert.True(backToElwynn.Ok);
        Assert.Equal("elwynn", svc.ActiveProfile(_root));
        // Elwynn has its own addon back, and barrens' addon is parked, not merged in.
        Assert.True(Directory.Exists(Path.Combine(Addons, "Questie")));
        Assert.False(Directory.Exists(Path.Combine(Addons, "PallyPower")));
        Assert.True(Directory.Exists(Path.Combine(Parked("barrens"), "PallyPower")));
    }

    [Fact]
    public void SwitchingToTheActiveProfile_DoesNothing()
    {
        var svc = NewService();
        MakeAddons("Questie");
        svc.Switch(_root, "elwynn");

        var again = svc.Switch(_root, "elwynn");

        Assert.True(again.Ok);
        Assert.True(Directory.Exists(Path.Combine(Addons, "Questie")));
        Assert.False(Directory.Exists(Parked("elwynn")));   // nothing was parked
    }

    /// <summary>Renaming the folder a live client has open either fails on Windows or, worse, succeeds
    /// on Linux and leaves the running game reading a directory nobody can see.</summary>
    [Fact]
    public void WhileTheGameIsRunning_NothingIsTouched()
    {
        var svc = NewService();
        MakeAddons("Questie");
        svc.Switch(_root, "elwynn");

        var result = svc.Switch(_root, "barrens", gameRunning: true);

        Assert.False(result.Ok);
        Assert.Contains("running", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("elwynn", svc.ActiveProfile(_root));
        Assert.True(Directory.Exists(Path.Combine(Addons, "Questie")));
    }

    /// <summary>A parking spot that is already taken means an earlier switch left something behind.
    /// Refusing keeps both sets; overwriting would delete one of them.</summary>
    [Fact]
    public void AnOccupiedParkingSpot_StopsTheSwitch_WithBothSetsIntact()
    {
        var svc = NewService();
        MakeAddons("Questie");
        svc.Switch(_root, "elwynn");
        Directory.CreateDirectory(Path.Combine(Parked("elwynn"), "leftover"));

        var result = svc.Switch(_root, "barrens");

        Assert.False(result.Ok);
        Assert.Equal("elwynn", svc.ActiveProfile(_root));
        Assert.True(Directory.Exists(Path.Combine(Addons, "Questie")));
        Assert.True(Directory.Exists(Path.Combine(Parked("elwynn"), "leftover")));
    }

    [Fact]
    public void KnownProfiles_ListsTheActiveOneAndEveryParkedOne()
    {
        var svc = NewService();
        MakeAddons();
        svc.Switch(_root, "elwynn");
        svc.Switch(_root, "barrens");

        var profiles = svc.KnownProfiles(_root);

        Assert.Equal(new[] { "barrens", "elwynn" }, profiles.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("elwynn", "elwynn")]
    [InlineData("Barrens", "barrens")]
    [InlineData("../../etc", "etc")]
    [InlineData("a/b\\c", "abc")]
    [InlineData("", "default")]
    [InlineData("...", "default")]
    public void ProfileNamesNeverBecomePaths(string input, string expected) =>
        Assert.Equal(expected, AddonProfileService.Sanitise(input));
}
