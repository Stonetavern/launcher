using System.Collections.Generic;
using System.Threading.Tasks;
using WowLauncher.Services;

namespace WowLauncher.Tests;

/// <summary>Shared folder-picker test doubles for PlayViewModel/SettingsViewModel construction across
/// the suite, so every test that does not care about the picker gets an explicit, honest double instead
/// of a shared mutable singleton.</summary>

/// <summary>Always answers "cancelled" (null) — the safe default for tests that are not about the
/// picker: nothing they do should ever depend on a folder having been chosen.</summary>
internal sealed class NullFolderPicker : IFolderPickerService
{
    public int CallCount { get; private set; }
    public Task<string?> PickFolderAsync(string title, string? startAt = null)
    {
        CallCount++;
        return Task.FromResult<string?>(null);
    }
}

/// <summary>Answers with a fixed path every time it is asked, and records every call (title + startAt)
/// so a test can assert the dialog fired exactly once, or never.</summary>
internal sealed class FixedFolderPicker(string? answer) : IFolderPickerService
{
    public List<(string Title, string? StartAt)> Calls { get; } = [];

    public Task<string?> PickFolderAsync(string title, string? startAt = null)
    {
        Calls.Add((title, startAt));
        return Task.FromResult(answer);
    }
}
