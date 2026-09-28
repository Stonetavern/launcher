using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowLauncher.Localization;
using WowLauncher.Services;
using WowLauncher.Services.Platform;

namespace WowLauncher.ViewModels;

/// <summary>One line under the folder: a warning (amber) or a reason it cannot be used (red).</summary>
public sealed record SetupNote(string Text, bool Blocks);

/// <summary>
/// The first-start page of a new player: where the one Stonetavern folder should be
/// (<see cref="LibraryNames"/>). Suggests <c>~/Games/Stonetavern</c>, checks every choice before a byte
/// moves (writable, runnable, space, system and synced folders, path length), then runs the setup
/// transaction (<see cref="LibrarySetup"/>). Knows no window: the App closes this launcher when the copy
/// in the folder has taken over, or continues here when nothing had to move.
/// </summary>
public sealed partial class SetupViewModel : ViewModelBase
{
    private readonly LibrarySetup _setup;
    private readonly IFolderPickerService _picker;
    private readonly Action<Action> _post;

    public SetupViewModel(LibrarySetup setup, IFolderPickerService picker, string suggestedRoot,
                          Action<Action>? post = null)
    {
        _setup = setup;
        _picker = picker;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        _root = suggestedRoot;
        Recheck();
    }

    /// <summary>Raised once with the result that ends this page (handed over, set up here, or already
    /// set up). A failure keeps the page and shows <see cref="Error"/> instead.</summary>
    public event Action<LibrarySetupResult>? Finished;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetUpCommand))]
    private string _root;

    partial void OnRootChanged(string value) => Recheck();

    public ObservableCollection<SetupNote> Notes { get; } = [];

    [ObservableProperty] private string _freeText = "";

    /// <summary>The folder already holds a finished Stonetavern folder: setting up opens it.</summary>
    [ObservableProperty] private bool _isExistingLibrary;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChangeFolderCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _busyText = "";

    /// <summary>While the copy starts: on Windows the first start of the copied exe can bring up
    /// SmartScreen, and the player must know that is expected.</summary>
    [ObservableProperty] private bool _showsRunAnywayHint;

    [ObservableProperty] private string? _error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetUpCommand))]
    private bool _isBlocked;

    public bool HasError => !string.IsNullOrEmpty(Error);
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    private void Recheck()
    {
        var check = _setup.Check(Root);
        Notes.Clear();
        foreach (var f in check.Findings)
            Notes.Add(new SetupNote(LibrarySetup.DisplayText(f), f.Severity == PreflightSeverity.Block));
        FreeText = check.FreeBytes is { } free ? Loc.F("Setup_Free", FormatGb(free)) : "";
        IsExistingLibrary = check.ExistingLauncher is not null;
        IsBlocked = check.IsBlocked;
        Error = null;
    }

    internal static string FormatGb(long bytes) =>
        (bytes / (1024.0 * 1024 * 1024)).ToString(bytes >= 100L * 1024 * 1024 * 1024 ? "0" : "0.0",
                                                  System.Globalization.CultureInfo.InvariantCulture) + " GB";

    private bool CanChangeFolder() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanChangeFolder))]
    private async Task ChangeFolderAsync()
    {
        var startAt = Directory.Exists(Root) ? Root : Path.GetDirectoryName(Root);
        var picked = await _picker.PickFolderAsync(Loc.T("Setup_PickerTitle"), startAt);
        if (picked is null) return;
        // A folder the player points at is the parent they meant ("put it in D:\Games"), unless it is
        // already a Stonetavern folder: then it is the one.
        Root = LibraryMarker.TryRead(picked) is not null
               || string.Equals(Path.GetFileName(picked.TrimEnd(Path.DirectorySeparatorChar)), "Stonetavern", StringComparison.OrdinalIgnoreCase)
            ? picked
            : Path.Combine(picked, "Stonetavern");
    }

    private bool CanSetUp() => !IsBusy && !IsBlocked && !string.IsNullOrWhiteSpace(Root);

    [RelayCommand(CanExecute = nameof(CanSetUp))]
    private async Task SetUpAsync()
    {
        IsBusy = true;
        Error = null;
        var progress = new Progress<LibrarySetupStage>(stage => _post(() => ShowStage(stage)));
        try
        {
            var result = await Task.Run(() => _setup.RunAsync(Root, progress));
            if (result.Outcome == LibrarySetupOutcome.Failed)
            {
                Error = result.Message ?? Loc.T("Setup_Failed");
                IsBusy = false;
                ShowsRunAnywayHint = false;
                return;
            }
            Finished?.Invoke(result);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Library setup page: setup threw");
            Error = Loc.T("Setup_Failed");
            IsBusy = false;
            ShowsRunAnywayHint = false;
        }
    }

    private void ShowStage(LibrarySetupStage stage)
    {
        BusyText = stage switch
        {
            LibrarySetupStage.Checking => Loc.T("Setup_Stage_Checking"),
            LibrarySetupStage.Copying => Loc.T("Setup_Stage_Copying"),
            LibrarySetupStage.Verifying => Loc.T("Setup_Stage_Verifying"),
            LibrarySetupStage.Starting => Loc.T("Setup_Stage_Starting"),
            _ => Loc.T("Setup_Stage_Waiting"),
        };
        ShowsRunAnywayHint = stage == LibrarySetupStage.WaitingForHandoff && OperatingSystem.IsWindows();
    }
}
