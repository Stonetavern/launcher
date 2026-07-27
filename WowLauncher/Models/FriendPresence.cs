namespace WowLauncher.Models;

/// <summary>Presence states. The real backend (HttpFriendsPresenceService) only ever produces
/// <see cref="Offline"/>, <see cref="Online"/> and <see cref="InGame"/> — the server has no away/busy
/// flag yet. <see cref="Away"/> / <see cref="Busy"/> are reserved for a future server status field and
/// are currently produced only by the Mock (demo variety, so every presence colour shows in one shot).</summary>
public enum PresenceStatus { Offline, Online, Away, Busy, InGame }

/// <summary>A friend, keyed by ACCOUNT (username), NOT by character. The activity line says what
/// the account is playing (realm/era), never the character name (owner directive 2026-07-20).</summary>
public sealed record FriendPresence(
    string Account,        // the username shown in the list — this is the identity
    PresenceStatus Status,
    string Activity,       // "Playing Elwynn" | "In Barrens - TBC" | "" when offline
    string? Realm)         // "elwynn" | "barrens" | null
{
    public bool IsOnline => Status != PresenceStatus.Offline;
    /// <summary>Round-avatar fallback: first letter of the account, upper.</summary>
    public string Initial => string.IsNullOrEmpty(Account) ? "?" : Account[..1].ToUpperInvariant();

    // ─── Per-status view flags (additive) ─────────────────────────────────
    // Compiled bindings cannot compare an enum to a literal without a converter, so the presence
    // dot picks its colour class from these bools (`Classes.online="{Binding IsPresenceOnline}"`
    // etc.). This keeps every colour a StaticResource token in Styles.v3.axaml — no hardcoded hex,
    // no converter. Does not change the record's positional signature.
    public bool IsPresenceOnline => Status == PresenceStatus.Online;
    public bool IsAway => Status == PresenceStatus.Away;
    public bool IsBusy => Status == PresenceStatus.Busy;
    public bool IsInGame => Status == PresenceStatus.InGame;
    public bool IsPresenceOffline => Status == PresenceStatus.Offline;
}
