using CommunityToolkit.Mvvm.ComponentModel;
using WowLauncher.Models;

namespace WowLauncher.ViewModels;

/// <summary>
/// One row in the v3 friends sidebar. The list is bound to these, NOT to the
/// <see cref="FriendPresence"/> record itself, and the reason is motion, not style.
///
/// <para>A record is immutable, so "this account changed its activity" could only be expressed as
/// <c>Friends[i] = newRecord</c>. An ObservableCollection replace makes the ItemsControl throw the
/// old container away and realise a new one, and a freshly realised container replays every
/// <c>Style.Animations</c> that matches it — so the row arrival lift (Grid.v3enterrow) fired again
/// for a friend who had simply walked into another zone. With the poll running every 18s that is a
/// row that jumps forever. The row object now stays the same instance for the lifetime of the
/// account and only its properties change, so the container survives and the arrival animation
/// plays exactly once: on a real arrival.</para>
///
/// <para>Identity is the ACCOUNT. It never changes for a given row; everything else can.</para>
/// </summary>
public sealed partial class FriendRow : ObservableObject
{
    public FriendRow(FriendPresence presence) => _presence = presence;

    /// <summary>The presence snapshot behind this row. Assigning a new one is the only mutation,
    /// and it refreshes every derived view flag below in one notification pass.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Activity))]
    [NotifyPropertyChangedFor(nameof(IsOnline))]
    [NotifyPropertyChangedFor(nameof(IsPresenceOnline))]
    [NotifyPropertyChangedFor(nameof(IsAway))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsInGame))]
    [NotifyPropertyChangedFor(nameof(IsPresenceOffline))]
    [NotifyPropertyChangedFor(nameof(Realm))]
    private FriendPresence _presence;

    /// <summary>Update in place. Returns true when something actually changed, so callers can keep
    /// their own derived counters honest without diffing twice.</summary>
    public bool Update(FriendPresence next)
    {
        if (Presence == next) return false;
        Presence = next;
        return true;
    }

    public string Account => Presence.Account;
    public string Initial => Presence.Initial;
    public string Activity => Presence.Activity;
    public string? Realm => Presence.Realm;
    public bool IsOnline => Presence.IsOnline;
    public bool IsPresenceOnline => Presence.IsPresenceOnline;
    public bool IsAway => Presence.IsAway;
    public bool IsBusy => Presence.IsBusy;
    public bool IsInGame => Presence.IsInGame;
    public bool IsPresenceOffline => Presence.IsPresenceOffline;
}
