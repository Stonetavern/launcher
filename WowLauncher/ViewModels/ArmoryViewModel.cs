using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using WowLauncher.Localization;
using WowLauncher.Models;
using WowLauncher.Services;

namespace WowLauncher.ViewModels;

/// <summary>
/// The armory section: the characters of the signed-in account on the selected realm, one selected
/// character shown as a ledger.
///
/// <para>Offline-first by construction. The service never throws, so this VM has exactly three
/// display states and no error state: a roster, or one calm sentence (signed out / nothing here /
/// not available right now). A player who opens ARMORY with no session sees an invitation to sign in,
/// not a red box.</para>
///
/// <para>Threading: <see cref="LoadAsync"/> is only ever awaited from the UI thread (the shell calls
/// it on tab switch, realm switch and sign-in) and does NOT ConfigureAwait(false), so the bound
/// collection is only ever mutated on the UI thread even though the HTTP service hops off it.</para>
/// </summary>
public sealed partial class ArmoryViewModel : ViewModelBase
{
    private readonly IArmoryService _armory;
    private readonly Serilog.ILogger _log;

    public ArmoryViewModel(IArmoryService armory, Serilog.ILogger log)
    {
        _armory = armory;
        _log = log.ForContext<ArmoryViewModel>();
    }

    public ObservableCollection<ArmoryCharacter> Characters { get; } = new();

    [ObservableProperty] private ArmoryCharacter? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyMessage), nameof(ShowEmpty))]
    private ArmoryStatus _status = ArmoryStatus.SignedOut;

    /// <summary>One line per realm behind this account: how many characters it returned, or why it
    /// returned none. Stonetavern has TWO realms behind one address, and until now their rosters were
    /// poured into one list with one shared status — so a realm that failed was invisible behind the
    /// one that worked, and "not available" could mean either of them (or both). Shown whenever more
    /// than one realm was asked, or when a single realm has something to explain.</summary>
    public ObservableCollection<ArmoryRealmSummary> RealmSummaries { get; } = new();

    public bool ShowRealmSummaries =>
        RealmSummaries.Count > 1 || RealmSummaries.Any(s => s.Status != ArmoryStatus.Ok);

    public bool HasCharacters => Characters.Count > 0;
    public bool HasSelection => Selected is not null;

    /// <summary>The empty state is shown for every non-roster case, including "loaded and empty".</summary>
    public bool ShowEmpty => !IsLoading && Characters.Count == 0;

    /// <summary>One quiet sentence, never an error. VOICE.md: no apostrophes, no em dashes.</summary>
    public string EmptyMessage => Status switch
    {
        ArmoryStatus.SignedOut => Loc.T("Armory_Empty_SignedOut"),
        ArmoryStatus.Unavailable => Loc.T("Armory_Empty_Unavailable"),
        _ => Loc.T("Armory_Empty_NoCharacters"),
    };

    partial void OnSelectedChanged(ArmoryCharacter? value) => OnPropertyChanged(nameof(HasSelection));

    /// <summary>The token source of the load that is allowed to write to this VM. Every other load is
    /// stale by definition: whoever started later asked the newer question.</summary>
    private CancellationTokenSource? _load;

    /// <summary>
    /// Load the roster for one realm. Keeps the current selection when the same character is still in
    /// the list (a realm switch replaces it, a refresh does not), so opening the tab twice does not
    /// bounce the reader back to the first row.
    ///
    /// <para><b>Only the newest load may write.</b> The shell fires this off without awaiting, on tab
    /// open, on realm switch and on sign-in, so a player clicking Elwynn and then Barrens has two
    /// requests in flight over a client that allows 15s plus a retry. Without a guard the slower
    /// answer lands last and paints the Elwynn roster under a highlighted Barrens rail: wrong
    /// characters, no error, no log line. So each call cancels the one before it and, because a
    /// service is free to ignore its token, an answer that arrives after it was superseded is dropped
    /// on the floor instead of assigned.</para>
    /// </summary>
    /// <summary>Convenience overload for a single realm (tests, callers that hold one id).</summary>
    public Task LoadAsync(string realmId, CancellationToken ct = default) =>
        LoadAsync(string.IsNullOrWhiteSpace(realmId) ? [] : new[] { realmId }, ct);

    /// <inheritdoc cref="LoadAsync(string, CancellationToken)"/>
    /// <remarks>
    /// Takes a LIST because one rail entry can stand for more than one game realm (Stonetavern is one
    /// address, two realms — see <c>RealmEntry.AccountRealms</c>). The rosters are concatenated in the
    /// given order. Status is the friendliest true answer: <c>Ok</c> when any realm answered, otherwise
    /// the first realm's status — so one realm being unreachable does not hide the characters on
    /// another, and a signed-out account still says so rather than showing a bare empty list.
    /// </remarks>
    public async Task LoadAsync(IReadOnlyList<string> realmIds, CancellationToken ct = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var previous = Interlocked.Exchange(ref _load, cts);
        if (previous is not null)
        {
            previous.Cancel();   // the older question is no longer being asked
            previous.Dispose();  // its own finally sees that it is no longer current and skips this
        }

        IsLoading = true;
        try
        {
            var rosters = new List<ArmoryRoster>(realmIds.Count);
            foreach (var realmId in realmIds)
            {
                if (string.IsNullOrWhiteSpace(realmId)) continue;
                rosters.Add(await _armory.GetCharactersAsync(realmId, cts.Token));
            }
            if (!ReferenceEquals(Volatile.Read(ref _load), cts)) return; // superseded mid-flight

            var keep = Selected?.Guid;

            Characters.Clear();
            foreach (var c in rosters.SelectMany(r => r.Characters)) Characters.Add(c);

            // Per realm, so a realm that failed says so instead of hiding behind one that worked.
            RealmSummaries.Clear();
            foreach (var r in rosters)
                RealmSummaries.Add(new ArmoryRealmSummary(r.RealmId, r.Status, r.Characters.Count, r.Detail));
            OnPropertyChanged(nameof(ShowRealmSummaries));

            Status = rosters.Count == 0
                ? ArmoryStatus.Unavailable
                : rosters.FirstOrDefault(r => r.Status == ArmoryStatus.Ok)?.Status ?? rosters[0].Status;

            Selected = Characters.FirstOrDefault(c => c.Guid == keep) ?? Characters.FirstOrDefault();
            OnPropertyChanged(nameof(HasCharacters));
        }
        catch (OperationCanceledException)
        {
            // Superseded or the caller walked away. Not a failure and not an empty state - the newer
            // load owns the screen now, so this one leaves it exactly as it found it.
        }
        catch (System.Exception ex)
        {
            // The service contract says it does not throw; this is the belt on top of the braces, so a
            // future implementation bug degrades the section instead of killing a fire-and-forget task.
            _log.Warning(ex, "Armory load failed - showing the quiet empty state");
            if (ReferenceEquals(Volatile.Read(ref _load), cts)) Status = ArmoryStatus.Unavailable;
        }
        finally
        {
            // Only the current load owns IsLoading: an older one flipping it to false would blink the
            // empty state through the middle of the newer request.
            if (Interlocked.CompareExchange(ref _load, null, cts) == cts)
            {
                cts.Dispose();
                IsLoading = false;
                OnPropertyChanged(nameof(ShowEmpty));
            }
        }
    }

    /// <summary>Signed out: drop everything and fall back to the sign-in invitation.</summary>
    public void Clear()
    {
        // Cancel first. A load started before the sign-out would otherwise land afterwards and put the
        // previous accounts characters back on a signed-out screen.
        var running = Interlocked.Exchange(ref _load, null);
        running?.Cancel();
        running?.Dispose();

        Characters.Clear();
        RealmSummaries.Clear();
        Selected = null;
        IsLoading = false; // the cancelled load will not reach its own reset, so do it here
        Status = ArmoryStatus.SignedOut;
        OnPropertyChanged(nameof(HasCharacters));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowRealmSummaries));
    }
}

/// <summary>What one realm answered, in one line a player can read: the realm by name, and either how
/// many characters it holds or why it holds none. The reason is short and non-secret ("HTTP 404", "no
/// answer in time") — enough to tell a realm that does not exist from a server that is down, which a
/// single "not available right now" could not.</summary>
public sealed record ArmoryRealmSummary(string RealmId, ArmoryStatus Status, int Count, string? Detail)
{
    /// <summary>Realm name for display. The shipped realms have proper names; anything else shows the
    /// id it was asked with, which is what a hand-added realm wants anyway.</summary>
    public string DisplayName => RealmId switch
    {
        "elwynn" => "Elwynn",
        "barrens" => "Barrens",
        "" => Loc.T("Armory_Realm_Unknown"),
        _ => RealmId,
    };

    public string Line => Status switch
    {
        ArmoryStatus.Ok => Loc.F("Armory_Realm_Count", DisplayName, Count),
        ArmoryStatus.SignedOut => Loc.F("Armory_Realm_SignedOut", DisplayName),
        _ => string.IsNullOrEmpty(Detail)
            ? Loc.F("Armory_Realm_Unavailable", DisplayName)
            : Loc.F("Armory_Realm_UnavailableWhy", DisplayName, Detail),
    };

    public bool IsOk => Status == ArmoryStatus.Ok;
}
