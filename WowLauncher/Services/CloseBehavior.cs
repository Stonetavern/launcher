namespace WowLauncher.Services;

using WowLauncher.Models;

/// <summary>
/// The pure decision logic behind the window close button, kept free of any Avalonia type so it can be
/// tested without a display (the test project carries no headless render harness by design). It answers
/// two questions and nothing else: given the saved preference, what should happen now; and given the
/// player's answer to the prompt, what should happen now plus what (if anything) to remember.
/// </summary>
public static class CloseBehavior
{
    /// <summary>What the shell should actually do about a close request.</summary>
    public enum Resolution
    {
        /// <summary>Ask the player (show the keep-running prompt).</summary>
        Prompt,

        /// <summary>Hide to the tray and keep running.</summary>
        Background,

        /// <summary>Quit the launcher.</summary>
        Quit,
    }

    /// <summary>Resolve a saved preference into the immediate action. Ask (the default, or an unknown
    /// value) means the prompt is shown; a remembered choice acts straight away.</summary>
    public static Resolution Resolve(CloseAction saved) => saved switch
    {
        CloseAction.Background => Resolution.Background,
        CloseAction.Quit => Resolution.Quit,
        _ => Resolution.Prompt,
    };

    /// <summary>
    /// Translate the prompt's answer into the action to take now and the choice to persist. "Yes, keep
    /// running" is <see cref="Resolution.Background"/>; "No" is <see cref="Resolution.Quit"/>. The choice
    /// is only persisted when the player ticked "remember my choice"; otherwise <paramref name="persist"/>
    /// is null and the next close asks again.
    /// </summary>
    public static (Resolution act, CloseAction? persist) FromPrompt(bool keepRunning, bool remember)
    {
        var act = keepRunning ? Resolution.Background : Resolution.Quit;
        CloseAction? persist = remember
            ? (keepRunning ? CloseAction.Background : CloseAction.Quit)
            : null;
        return (act, persist);
    }
}

/// <summary>
/// Reads and persists the player's close-button preference. A thin seam over
/// <see cref="IConfigService"/> so the close flow depends on this narrow surface rather than the whole
/// config, and so tests can drive it with a fake config service.
/// </summary>
public interface IClosePreferenceService
{
    /// <summary>The saved preference (defaults to <see cref="CloseAction.Ask"/>).</summary>
    CloseAction Load();

    /// <summary>Persist a remembered choice. Only ever <see cref="CloseAction.Background"/> or
    /// <see cref="CloseAction.Quit"/> in practice; writing <see cref="CloseAction.Ask"/> would clear it.</summary>
    void Save(CloseAction action);
}

/// <inheritdoc cref="IClosePreferenceService"/>
public sealed class ClosePreferenceService(IConfigService config) : IClosePreferenceService
{
    private readonly IConfigService _config = config;

    public CloseAction Load() => _config.Load().CloseAction;

    public void Save(CloseAction action)
    {
        // Load-modify-save the whole document: ConfigService owns the atomic write and the "keep a
        // damaged file" durability rules, so we never hand-write the file here.
        var cfg = _config.Load();
        cfg.CloseAction = action;
        _config.Save(cfg);
    }
}
