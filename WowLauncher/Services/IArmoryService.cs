namespace WowLauncher.Services;

using WowLauncher.Models;

/// <summary>Why a roster is empty. The armory surface must never show an error in the players face:
/// each state maps to one calm sentence in the catalog, and every one of them is a legitimate resting
/// state of the app rather than a failure.</summary>
public enum ArmoryStatus
{
    /// <summary>The server answered. The list may still be empty (a fresh account).</summary>
    Ok,

    /// <summary>No session. The armory is account scoped, so there is nothing to ask for.</summary>
    SignedOut,

    /// <summary>Offline, timeout, HTTP error, or an endpoint that does not exist yet.</summary>
    Unavailable,
}

/// <summary>The roster for one realm, plus why it looks the way it does.</summary>
public sealed record ArmoryRoster(IReadOnlyList<ArmoryCharacter> Characters, ArmoryStatus Status)
{
    public static ArmoryRoster Empty(ArmoryStatus status) => new(Array.Empty<ArmoryCharacter>(), status);
}

/// <summary>
/// The characters of the SIGNED-IN account on one realm. <see cref="MockArmoryService"/> drives the
/// v3 armory under <c>--demo</c>; <see cref="HttpArmoryService"/> will hit the web app with the same
/// <see cref="ArmoryCharacter"/> shape, so the UI never changes.
///
/// <para><b>Own characters only.</b> The interface takes no account parameter on purpose: the bearer
/// token IS the scope. Showing a FRIENDS characters would join an account username to a character
/// name, which is exactly the link the owner directive of 2026-07-20 refuses for the presence
/// surface. That is an owner question, not an implementation detail, so the seam does not exist yet.</para>
///
/// <para>Offline-first: no method throws. A failure returns an empty roster with a status, never an
/// exception, matching <see cref="IFriendsPresenceService"/>.</para>
/// </summary>
public interface IArmoryService
{
    Task<ArmoryRoster> GetCharactersAsync(string realmId, CancellationToken ct = default);
}
