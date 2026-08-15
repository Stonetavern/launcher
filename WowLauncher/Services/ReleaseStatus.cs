namespace WowLauncher.Services;

/// <summary>
/// What a player is running, what was last offered, and how long ago anyone found out.
///
/// <para><b>The failure this answers.</b> "Why do I not have the fix" has three possible answers and
/// the launcher used to show none of them: the player is on an old build, or the server offers an old
/// build, or nobody has managed to ask in weeks. They call for completely different actions - download,
/// wait for us, check the network - and from the outside they look identical.</para>
///
/// <para>Pure: it compares two version strings and a timestamp. No files, no network, no UI, so the
/// wording sits in the view model and the DECISION can be asserted on its own.</para>
/// </summary>
public sealed record ReleaseStatusFacts(
    string Running,
    string Offered,
    DateTimeOffset? LastSuccess,
    int? AgeDays,
    bool Behind,
    bool Ahead)
{
    /// <summary>Nobody has ever reached the update server on this machine. Distinct from "up to
    /// date": one is knowledge, the other is the absence of it, and only the second is a reason to
    /// look at the network.</summary>
    public bool NeverChecked => LastSuccess is null;

    /// <summary>The check worked but named no version. Rare and worth saying out loud rather than
    /// rendering as an empty gap.</summary>
    public bool OfferUnknown => !NeverChecked && Offered.Length == 0;

    /// <summary>Known, checked, and the same on both sides.</summary>
    public bool UpToDate => !NeverChecked && !OfferUnknown && !Behind && !Ahead;
}

public static class ReleaseStatus
{
    /// <summary>
    /// How old a successful check may be before it stops counting as an answer. Seven days is not a
    /// rule about servers, it is a rule about players: past a week, "I checked" is no longer a reason
    /// to believe the version on screen.
    /// </summary>
    public const int StaleAfterDays = 7;

    public static ReleaseStatusFacts Evaluate(string running, string? offered,
        DateTimeOffset? lastSuccess, DateTimeOffset now)
    {
        var offeredText = (offered ?? "").Trim();

        var behind = false;
        var ahead = false;
        if (Version.TryParse(running, out var r) && Version.TryParse(offeredText, out var o))
        {
            // Normalised, because 1.7 and 1.7.0 are the same release and Version does not think so.
            var rn = new Version(r.Major, r.Minor, Math.Max(r.Build, 0));
            var on = new Version(o.Major, o.Minor, Math.Max(o.Build, 0));
            behind = rn < on;
            // "Ahead" is not a curiosity, it is the state of every build this workstation makes: a
            // launcher newer than the one on the download page. Reporting it as up to date would hide
            // exactly the gap between what exists and what anybody has.
            ahead = rn > on;
        }

        int? age = lastSuccess is { } ls ? Math.Max(0, (int)(now - ls).TotalDays) : null;

        return new ReleaseStatusFacts(running, offeredText, lastSuccess, age, behind, ahead);
    }

    /// <summary>True when a check is old enough that the numbers beside it should not be trusted.</summary>
    public static bool IsStale(ReleaseStatusFacts f) => f.AgeDays is int d && d >= StaleAfterDays;
}
