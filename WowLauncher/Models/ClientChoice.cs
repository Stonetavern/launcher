using System.Collections.Generic;
using System.Linq;

namespace WowLauncher.Models;

/// <summary>
/// One row in the play-surface picker: a specific <see cref="ClientVersion"/>, with the
/// <see cref="Expansion"/> it belongs to for the existing era-level machinery (background art,
/// progression phase, persisted "SelectedExpansion" config field) to key off unchanged.
///
/// <para>Why this exists instead of binding the picker straight to <see cref="Expansion.All"/>: an
/// era and a client build used to be the same thing in the UI, but Vanilla stopped being one client
/// the moment 1.14.2 (Classic Era) joined 1.12.1 - two real, separately playable builds under one
/// era name. A picker that only lists eras cannot say which Vanilla it means; a picker of client
/// builds can, and it can also mark Burning Crusade / Wrath as unavailable at the level where that
/// is actually decided (<see cref="ClientVersion.IsAvailable"/>), not with a second flag bolted onto
/// the UI.</para>
/// </summary>
public sealed record ClientChoice(Expansion Expansion, ClientVersion Client)
{
    /// <summary>Single source of truth for whether this row can be picked: the client build itself.</summary>
    public bool IsAvailable => Client.IsAvailable;

    /// <summary>Era name, shown big: "Vanilla" / "Burning Crusade" / "Wrath of the Lich King".</summary>
    public string DisplayName => Client.Era;

    /// <summary>Which build, shown small under the era name. For an era with only one build this is
    /// still shown - consistency beats saving one line for the common case. Version + qualifier only
    /// (no build number): the picker tile is narrow and "1.14.2 (42597) · Classic Era" does not fit
    /// on one line - the build number is still available in <see cref="ClientVersion.PreciseLabel"/>
    /// wherever there is room for it (the realm line above the picker).</summary>
    public string VersionLabel => Client.Note.Length == 0 ? Client.Version : $"{Client.Version} · {Client.Note}";

    /// <summary>One row per known client build, in <see cref="ClientVersion.All"/> order - Vanilla
    /// contributes two rows (1.12.1, 1.14.2), Burning Crusade and Wrath one each.</summary>
    public static readonly IReadOnlyList<ClientChoice> All =
        ClientVersion.All.Select(cv => new ClientChoice(Expansion.ById(cv.EraId), cv)).ToList();

    /// <summary>The rows to actually offer on the current OS: <see cref="All"/> minus the builds this OS
    /// has no path for (on macOS the 32-bit legacy builds — owner scope, see
    /// <see cref="ClientVersion.IsExcludedOnCurrentOs"/>). Windows/Linux get the full list unchanged, so
    /// the "coming soon" BC/Wrath tiles still show there; macOS shows only the 1.14.2 row.</summary>
    public static IReadOnlyList<ClientChoice> AllForCurrentOs =>
        All.Where(c => !c.Client.IsExcludedOnCurrentOs).ToList();

    /// <summary>Resolve the row for a realm's precise client key. A key that names a build this OS cannot
    /// run (e.g. <c>1.12.1</c> on macOS), an unknown key, or a blank key all fall back to the first build
    /// this OS DOES support — never to a build the platform has no path for. A realm pointing at a client
    /// the launcher does not (yet) know, or that this OS excludes, must still render honestly.</summary>
    public static ClientChoice ForKey(string? key)
    {
        var match = All.FirstOrDefault(c => c.Client.Key == key);
        if (match is not null && !match.Client.IsExcludedOnCurrentOs) return match;
        return AllForCurrentOs.FirstOrDefault() ?? All.First(c => c.Client == ClientVersion.Default);
    }
}
