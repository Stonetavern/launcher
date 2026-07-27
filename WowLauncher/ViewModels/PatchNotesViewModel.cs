using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Localization;

namespace WowLauncher.ViewModels;

/// <summary>
/// Patch notes: the newest entries of the feed the rail also shows.
///
/// <para><b>No category filter.</b> This used to keep only <c>category == "patch"</c>, which meant the
/// page silently hid entries the server had published and the player had already seen on the home
/// rail. Patch notes and changelog are the same thing here - the owner does not maintain two lists -
/// so the page shows the same feed, newest first, capped at
/// <see cref="PlayViewModel.NewsCount"/>.</para>
/// </summary>
public sealed partial class PatchNotesViewModel : ViewModelBase
{
    private readonly INewsService _news;
    private readonly IConfigService _config;
    private readonly Serilog.ILogger _log;

    public PatchNotesViewModel(INewsService news, IConfigService config, Serilog.ILogger log)
    {
        _news = news;
        _config = config;
        _log = log.ForContext<PatchNotesViewModel>();
    }

    public string Title => Loc.T("Patch_Title");
    public ObservableCollection<NewsItem> Patches { get; } = new();

    public bool IsEmpty => Patches.Count == 0;
    public string Placeholder => Loc.T("Patch_Empty");

    public async Task LoadAsync()
    {
        try
        {
            var items = await _news.GetNewsAsync();
            Patches.Clear();
            foreach (var p in items.Take(PlayViewModel.NewsCount)) Patches.Add(p);
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (System.Exception ex) { _log.Warning(ex, "Loading patch notes failed"); }
    }

    /// <summary>Every entry opens the changelog, for the reason spelled out in
    /// <see cref="PlayViewModel.ChangelogUrl"/>: the per-entry links pointed at pages that 404.</summary>
    [RelayCommand]
    private void OpenPatch(NewsItem? item)
    {
        var url = PlayViewModel.ChangelogUrl(_config.Load().SiteBaseUrl);
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.Exception ex) { _log.Warning(ex, "Opening the changelog failed: {Url}", url); }
    }
}
