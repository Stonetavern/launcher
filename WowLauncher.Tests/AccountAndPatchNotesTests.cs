using System.Linq;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.ViewModels;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The batch of owner findings from 2026-07-22, each pinned by the behaviour it demands rather than
/// by the code that happens to implement it.
/// </summary>
public sealed class AccountAndPatchNotesTests
{
    private sealed class Cfg(LauncherConfig c) : IConfigService
    {
        public LauncherConfig Load() => c;
        public void Save(LauncherConfig x) { }
        public bool LastSaveSucceeded => true;
    }

    // ── The token leak: account APIs must never follow the selected realm ───────────────────────

    [Fact]
    public void AccountApi_DoesNotFollowTheSelectedRealm()
    {
        // A player adds their own realm through the "+" in the rail. Before the fix, ApiEndpoints
        // derived the API host from exactly this address, so the friends service then sent the
        // STONETAVERN bearer token to a stranger's server, with nothing visibly wrong on screen.
        var cfg = new Cfg(new LauncherConfig { RealmlistAddress = "play.someone-elses-server.example" });

        var url = ApiEndpoints.Base(cfg);

        Assert.DoesNotContain("someone-elses-server.example", url);
        Assert.Equal(ApiEndpoints.DefaultBase, url);
    }

    [Fact]
    public void AccountApi_HonoursAnExplicitOverride()
    {
        // The override exists for a test server. It comes from the player's own config file, never
        // from anything a realm can supply.
        var cfg = new Cfg(new LauncherConfig { AccountApiBaseUrl = "https://staging.example/api/" });

        Assert.Equal("https://staging.example/api", ApiEndpoints.Base(cfg));
    }

    // ── Patch notes point at the changelog, not at pages that 404 ──────────────────────────────

    [Fact]
    public void PatchNote_OpensTheChangelog()
    {
        Assert.Equal("https://stonetavern.app/changelog",
            PlayViewModel.ChangelogUrl("https://stonetavern.app"));
    }

    [Fact]
    public void PatchNote_ChangelogUrl_SurvivesAnEmptyOrSloppyBase()
    {
        // Empty config must not produce "/changelog" (a relative string handed to the browser opens
        // nothing), and a trailing slash must not produce a double slash.
        Assert.Equal("https://stonetavern.app/changelog", PlayViewModel.ChangelogUrl(null));
        Assert.Equal("https://stonetavern.app/changelog", PlayViewModel.ChangelogUrl("  "));
        Assert.Equal("https://example.test/changelog", PlayViewModel.ChangelogUrl("https://example.test/"));
    }

    [Fact]
    public void NewsCount_IsTen()
    {
        // The owner's number. Pinned because both the rail and the patch-notes page read it, and a
        // silent drift between them is exactly how the two lists stopped agreeing before.
        Assert.Equal(10, PlayViewModel.NewsCount);
    }

    // ── The dead patch switches are gone for good ──────────────────────────────────────────────

    [Fact]
    public void LauncherConfig_HasNoVanillaPatchSwitches()
    {
        // They were two checkboxes in Settings that NOTHING read: the launcher never patched the
        // client. A control that shows a promise and does nothing is worse than no control, and it
        // cannot come back by accident.
        var props = typeof(LauncherConfig).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("UseVanillaFixes", props);
        Assert.DoesNotContain("UseVanillaTweaks", props);
    }

    [Fact]
    public void SettingsViewModel_ExposesNoVanillaPatchSwitches()
    {
        var props = typeof(SettingsViewModel).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("UseVanillaFixes", props);
        Assert.DoesNotContain("UseVanillaTweaks", props);
    }

    // ── Account is its own place ───────────────────────────────────────────────────────────────

    [Fact]
    public void Account_IsItsOwnSection_NotSettings()
    {
        // The Account button used to open SETTINGS, so "am I signed in, and as whom" had no answer
        // anywhere in the launcher.
        Assert.Contains(ShellViewModel.Section.Account,
            System.Enum.GetValues<ShellViewModel.Section>());
        Assert.NotEqual(ShellViewModel.Section.Settings, ShellViewModel.Section.Account);
    }
}
