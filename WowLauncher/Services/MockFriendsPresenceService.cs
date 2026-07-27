namespace WowLauncher.Services;

using WowLauncher.Models;
using WowLauncher.Localization;

/// <summary>In-memory friends/presence feed for the v3 shell. No IO — a fixed roster returned via
/// <see cref="Task.FromResult{T}"/>, with an optimistic "Pending" offline entry appended on add.
/// The HTTP impl (later) returns the SAME <see cref="FriendPresence"/> shape, so the UI never
/// changes. Account names are the identity shown in the list; the activity line says what the
/// account is playing, never a character name (owner directive 2026-07-20). All English text
/// (VOICE.md): no em dashes, no apostrophes.</summary>
public sealed class MockFriendsPresenceService : IFriendsPresenceService
{
    // ~7 accounts: a mix of in-game / online / away / busy / offline so every presence colour and
    // the online-count all show in a single screenshot.
    private readonly List<FriendPresence> _roster =
    [
        new("Ashwarden",  PresenceStatus.InGame,  "In Elwynn - Vanilla", "elwynn"),
        new("Kaelthar",   PresenceStatus.InGame,  "In Barrens - TBC",    "barrens"),
        new("Emberfell",  PresenceStatus.Online,  "In launcher",         null),
        new("Grimtide",   PresenceStatus.Away,    "Away",                null),
        new("Lightbane",  PresenceStatus.Busy,    "Do not disturb",      null),
        new("Oakenreach", PresenceStatus.Offline, "",                    null),
        new("Stormquill", PresenceStatus.Offline, "",                    null),
    ];

    public Task<IReadOnlyList<FriendPresence>> GetFriendsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FriendPresence>>(_roster.ToList());

    public Task<AddFriendResult> AddFriendAsync(string account, CancellationToken ct = default)
    {
        var name = account?.Trim();
        if (string.IsNullOrEmpty(name))
            return Task.FromResult(AddFriendResult.Rejected(Loc.T("Friends_Error_EmptyName")));

        // Optimistic: a freshly added account sits offline as "Pending" until the real backend
        // confirms it (no request is sent yet — this is the mock).
        var entry = new FriendPresence(name, PresenceStatus.Offline, "Pending", null);
        _roster.Add(entry);
        return Task.FromResult(AddFriendResult.Added(entry));
    }
}
