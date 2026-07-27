using WowLauncher.Models;
using WowLauncher.Services;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The close-button decision, pinned as pure logic. The window either asks, hides to the tray, or
/// quits, and "remember my choice" is the only thing that turns a one-off answer into a saved
/// preference. None of this touches Avalonia, so it is provable without a display (the test project
/// carries no headless render harness by design).
/// </summary>
public sealed class CloseBehaviorTests
{
    // ── Resolve: saved preference → immediate action ────────────────────────────────────────────

    [Fact]
    public void Ask_ShowsThePrompt()
        => Assert.Equal(CloseBehavior.Resolution.Prompt, CloseBehavior.Resolve(CloseAction.Ask));

    [Fact]
    public void Background_HidesWithoutAsking()
        => Assert.Equal(CloseBehavior.Resolution.Background, CloseBehavior.Resolve(CloseAction.Background));

    [Fact]
    public void Quit_QuitsWithoutAsking()
        => Assert.Equal(CloseBehavior.Resolution.Quit, CloseBehavior.Resolve(CloseAction.Quit));

    [Fact]
    public void UnknownValue_FallsBackToAsking()
        => Assert.Equal(CloseBehavior.Resolution.Prompt, CloseBehavior.Resolve((CloseAction)99));

    [Fact]
    public void FreshConfig_DefaultsToAsking()
    {
        // A brand-new launcher (or an older config with no such field) must ask, never silently hide.
        Assert.Equal(CloseAction.Ask, new LauncherConfig().CloseAction);
        Assert.Equal(CloseBehavior.Resolution.Prompt, CloseBehavior.Resolve(new LauncherConfig().CloseAction));
    }

    // ── FromPrompt: the player's answer → action now + what to remember ─────────────────────────

    [Fact]
    public void Yes_WithoutRemember_HidesButSavesNothing()
    {
        var (act, persist) = CloseBehavior.FromPrompt(keepRunning: true, remember: false);
        Assert.Equal(CloseBehavior.Resolution.Background, act);
        Assert.Null(persist); // next close asks again
    }

    [Fact]
    public void No_WithoutRemember_QuitsButSavesNothing()
    {
        var (act, persist) = CloseBehavior.FromPrompt(keepRunning: false, remember: false);
        Assert.Equal(CloseBehavior.Resolution.Quit, act);
        Assert.Null(persist);
    }

    [Fact]
    public void Yes_WithRemember_HidesAndSavesBackground()
    {
        var (act, persist) = CloseBehavior.FromPrompt(keepRunning: true, remember: true);
        Assert.Equal(CloseBehavior.Resolution.Background, act);
        Assert.Equal(CloseAction.Background, persist);
    }

    [Fact]
    public void No_WithRemember_QuitsAndSavesQuit()
    {
        var (act, persist) = CloseBehavior.FromPrompt(keepRunning: false, remember: true);
        Assert.Equal(CloseBehavior.Resolution.Quit, act);
        Assert.Equal(CloseAction.Quit, persist);
    }

    // ── Persistence: the remembered choice survives, and drives the next resolve ────────────────

    private sealed class FakeConfig(LauncherConfig cfg) : IConfigService
    {
        private LauncherConfig _cfg = cfg;
        public int Saves { get; private set; }

        // Return the same instance so a save is observable on the next load, like the real file store.
        public LauncherConfig Load() => _cfg;
        public void Save(LauncherConfig config) { _cfg = config; Saves++; }
        public bool LastSaveSucceeded => true;
    }

    [Fact]
    public void Remembering_Background_MakesTheNextCloseHideSilently()
    {
        var store = new FakeConfig(new LauncherConfig());
        var pref = new ClosePreferenceService(store);
        Assert.Equal(CloseAction.Ask, pref.Load()); // starts by asking

        pref.Save(CloseAction.Background);

        Assert.Equal(CloseAction.Background, pref.Load());
        Assert.Equal(CloseBehavior.Resolution.Background, CloseBehavior.Resolve(pref.Load()));
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void Remembering_Quit_MakesTheNextCloseQuitSilently()
    {
        var store = new FakeConfig(new LauncherConfig());
        var pref = new ClosePreferenceService(store);

        pref.Save(CloseAction.Quit);

        Assert.Equal(CloseAction.Quit, pref.Load());
        Assert.Equal(CloseBehavior.Resolution.Quit, CloseBehavior.Resolve(pref.Load()));
    }

    [Fact]
    public void Saving_PreservesTheRestOfTheConfig()
    {
        // The preference write is load-modify-save on the whole document: it must not clobber a
        // player's other settings (here, a realm they added).
        var store = new FakeConfig(new LauncherConfig { SelectedRealmId = "my-own-realm" });
        var pref = new ClosePreferenceService(store);

        pref.Save(CloseAction.Background);

        Assert.Equal("my-own-realm", store.Load().SelectedRealmId);
        Assert.Equal(CloseAction.Background, store.Load().CloseAction);
    }
}
