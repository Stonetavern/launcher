using System;
using System.IO;
using System.Linq;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The 1.12.1 language switch. The client loads <c>patch-?.MPQ</c> out of <c>Data/</c> and nothing
/// else, so a language is active exactly when its pack sits in the slot — a config key alone changes
/// nothing, which is the failure these tests exist to keep out.
///
/// <para>What is really being guarded is a ~90 MB file the player downloaded: the dangerous states are
/// a half-finished switch (slot emptied, new pack never arrived), a pack that was moved somewhere it
/// cannot be found again, and a file the launcher did not place being moved aside.</para>
/// </summary>
public sealed class VanillaLocalePacksTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "st-locale-" + Guid.NewGuid().ToString("N"));

    private static Serilog.ILogger Log() => new Serilog.LoggerConfiguration().CreateLogger();
    private VanillaLocalePacks NewService() => new(Log());

    private string Data => Path.Combine(_root, "Data");
    private string Slot => Path.Combine(Data, VanillaLocalePacks.SlotFileName);
    private string Marker => Path.Combine(Data, VanillaLocalePacks.MarkerName);
    private string Config => Path.Combine(_root, "WTF", "Config.wtf");

    /// <summary>An installed pack, exactly where the ZIP extracts it.</summary>
    private string MakePack(string locale, string content = "MPQ")
    {
        var path = VanillaLocalePacks.PackPath(_root, locale);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string ConfigValue(string key) =>
        File.Exists(Config)
            ? File.ReadAllLines(Config).FirstOrDefault(l => l.StartsWith($"SET {key} ", StringComparison.Ordinal))
              ?.Split('"')[1] ?? ""
            : "";

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void AFreshInstall_IsEnglish_AndOffersOnlyEnglish()
    {
        var svc = NewService();
        Directory.CreateDirectory(Data);

        Assert.Equal("enUS", svc.Active(_root));
        Assert.Equal(new[] { "enUS" }, svc.Installed(_root));
    }

    /// <summary>The whole point: after a switch the pack must be IN the slot the client reads. A test
    /// that only checked Config.wtf would pass against a launcher that changes nothing.</summary>
    [Fact]
    public void ActivatingALanguage_MovesThePackIntoThePatchSlot()
    {
        var svc = NewService();
        MakePack("deDE", "german-pack");

        var result = svc.Apply(_root, "deDE");

        Assert.True(result.Ok, result.Error);
        Assert.Equal("german-pack", File.ReadAllText(Slot));
        Assert.False(File.Exists(VanillaLocalePacks.PackPath(_root, "deDE")));
        Assert.Equal("deDE", svc.Active(_root));
    }

    /// <summary>All three keys, because the client takes interface text, voice-over and its data path
    /// from three separate ones and a half-set config reads like a broken pack.</summary>
    [Fact]
    public void ActivatingALanguage_WritesAllThreeConfigKeys()
    {
        var svc = NewService();
        MakePack("frFR");

        svc.Apply(_root, "frFR");

        Assert.Equal("frFR", ConfigValue("locale"));
        Assert.Equal("frFR", ConfigValue("textLocale"));
        Assert.Equal("frFR", ConfigValue("audioLocale"));
    }

    [Fact]
    public void SwitchingLanguages_PutsTheOldPackBack_AndKeepsBothUsable()
    {
        var svc = NewService();
        MakePack("deDE", "german-pack");
        MakePack("esES", "spanish-pack");

        svc.Apply(_root, "deDE");
        var toSpanish = svc.Apply(_root, "esES");

        Assert.True(toSpanish.Ok, toSpanish.Error);
        Assert.Equal("spanish-pack", File.ReadAllText(Slot));
        Assert.Equal("german-pack", File.ReadAllText(VanillaLocalePacks.PackPath(_root, "deDE")));
        Assert.Equal(new[] { "deDE", "enUS", "esES" }, svc.Installed(_root).OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>Back to English is not "install the English pack" — it is emptying the slot, because
    /// English lives in the base MPQs.</summary>
    [Fact]
    public void BackToEnglish_EmptiesTheSlot_AndKeepsThePack()
    {
        var svc = NewService();
        MakePack("deDE", "german-pack");
        svc.Apply(_root, "deDE");

        var back = svc.Apply(_root, "enUS");

        Assert.True(back.Ok, back.Error);
        Assert.False(File.Exists(Slot));
        Assert.False(File.Exists(Marker));
        Assert.Equal("german-pack", File.ReadAllText(VanillaLocalePacks.PackPath(_root, "deDE")));
        Assert.Equal("enUS", ConfigValue("textLocale"));
    }

    /// <summary>A patch the launcher did not place is a mod the player installed. Refusing keeps it;
    /// moving it aside would silently disable something they chose, with no way back.
    ///
    /// <para>The message is asserted, not just the survival of the file, and that is deliberate: the
    /// counter-check (placebo-check.sh case M) showed that with the up-front guard removed the file
    /// still survives — a second guard further down refuses too, for a different and much less useful
    /// reason ("cannot tell which language it is", to a player who never installed a language). So a
    /// test that only checked the bytes was green either way and proved nothing about the guard it was
    /// named after.</para></summary>
    [Fact]
    public void AForeignPatchInTheSlot_IsRefused_NotMovedAside()
    {
        var svc = NewService();
        Directory.CreateDirectory(Data);
        File.WriteAllText(Slot, "someone-elses-mod");
        MakePack("deDE");

        var result = svc.Apply(_root, "deDE");

        Assert.False(result.Ok);
        Assert.Contains("did not install", result.Error, StringComparison.Ordinal);
        Assert.Equal("someone-elses-mod", File.ReadAllText(Slot));
        Assert.True(File.Exists(VanillaLocalePacks.PackPath(_root, "deDE")));
    }

    /// <summary>A language that was never downloaded must fail BEFORE anything moves, or the player
    /// ends up with no language at all instead of the one they had.</summary>
    [Fact]
    public void AMissingPack_FailsWithoutDisturbingTheActiveLanguage()
    {
        var svc = NewService();
        MakePack("deDE", "german-pack");
        svc.Apply(_root, "deDE");

        var result = svc.Apply(_root, "ruRU");

        Assert.False(result.Ok);
        Assert.Equal("deDE", result.Locale);
        Assert.Equal("german-pack", File.ReadAllText(Slot));
        Assert.Equal("deDE", svc.Active(_root));
    }

    [Fact]
    public void WhileTheGameIsRunning_NothingIsTouched()
    {
        var svc = NewService();
        MakePack("deDE", "german-pack");
        svc.Apply(_root, "deDE");

        var result = svc.Apply(_root, "esES", gameRunning: true);

        Assert.False(result.Ok);
        Assert.Contains("running", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("german-pack", File.ReadAllText(Slot));
        Assert.Equal("deDE", svc.Active(_root));
    }

    /// <summary>Server-supplied text is cached per row with no language stamp, so a switch that leaves
    /// WDB in place shows the old language for everything the player has already seen.</summary>
    [Fact]
    public void SwitchingLanguage_ThrowsAwayTheServerTextCache()
    {
        var svc = NewService();
        MakePack("deDE");
        var wdb = Path.Combine(_root, "WDB");
        Directory.CreateDirectory(wdb);
        File.WriteAllText(Path.Combine(wdb, "creaturecache.wdb"), "stale");
        var keep = Path.Combine(wdb, "notes.txt");
        File.WriteAllText(keep, "not a cache");

        svc.Apply(_root, "deDE");

        Assert.False(File.Exists(Path.Combine(wdb, "creaturecache.wdb")));
        Assert.True(File.Exists(keep));   // only the cache goes, not the folder or anything else
    }

    /// <summary>Re-applying the language that is already on repairs the config rather than moving
    /// files: an update that replaced Config.wtf must not leave the client English while the pack says
    /// German.</summary>
    [Fact]
    public void ReapplyingTheActiveLanguage_RewritesTheConfig_AndMovesNothing()
    {
        var svc = NewService();
        MakePack("deDE", "german-pack");
        svc.Apply(_root, "deDE");
        File.Delete(Config);

        var again = svc.Apply(_root, "deDE");

        Assert.True(again.Ok, again.Error);
        Assert.Equal("german-pack", File.ReadAllText(Slot));
        Assert.Equal("deDE", ConfigValue("textLocale"));
    }

    /// <summary>A marker left behind by a pack that is gone (a player deleted it, an update wiped
    /// Data/) must not block the switch — it is bookkeeping, not state.</summary>
    [Fact]
    public void AStaleMarker_IsCleanedUp_AndTheSwitchProceeds()
    {
        var svc = NewService();
        Directory.CreateDirectory(Data);
        File.WriteAllText(Marker, "deDE\n");
        MakePack("frFR", "french-pack");

        var result = svc.Apply(_root, "frFR");

        Assert.True(result.Ok, result.Error);
        Assert.Equal("french-pack", File.ReadAllText(Slot));
        Assert.Equal("frFR", svc.Active(_root));
    }

    [Theory]
    [InlineData("deDE", true)]
    [InlineData("enUS", true)]
    [InlineData("de", false)]
    [InlineData("dede", false)]
    [InlineData("../etc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyRealLocaleCodesAreAccepted(string? code, bool valid) =>
        Assert.Equal(valid, VanillaLocalePacks.IsLocaleCode(code));

    [Fact]
    public void AnInvalidLocale_IsRefused_NeverRepaired()
    {
        var svc = NewService();
        MakePack("deDE");

        var result = svc.Apply(_root, "../../Windows");

        Assert.False(result.Ok);
        Assert.False(File.Exists(Slot));
    }
}
