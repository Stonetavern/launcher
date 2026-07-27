namespace WowLauncher.Services;

using WowLauncher.Models;

/// <summary>Outcome of an add-by-account request. Carries the optimistic new entry on success, or a
/// short VOICE-compliant English reason on rejection (unknown account, already added). The reason is
/// surfaced to the player, never swallowed.</summary>
public sealed record AddFriendResult(FriendPresence? Friend, string? Error)
{
    public bool Ok => Friend is not null;
    public static AddFriendResult Added(FriendPresence friend) => new(friend, null);
    public static AddFriendResult Rejected(string error) => new(null, error);
}

/// <summary>Friends + presence, keyed by account. <see cref="MockFriendsPresenceService"/> drives the
/// v3 UI under <c>--demo</c>; <see cref="HttpFriendsPresenceService"/> hits the web app
/// (<c>/api/friends</c>) with the same <see cref="FriendPresence"/> shape, so the UI never changes.
/// All methods are async and offline-first — a network failure yields a degraded list, never a
/// throw.</summary>
public interface IFriendsPresenceService
{
    Task<IReadOnlyList<FriendPresence>> GetFriendsAsync(CancellationToken ct = default);

    /// <summary>Add by account name. Returns the optimistic new entry, or a rejection reason.</summary>
    Task<AddFriendResult> AddFriendAsync(string account, CancellationToken ct = default);
}
