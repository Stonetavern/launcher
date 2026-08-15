using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowLauncher.Localization;
using WowLauncher.Services;

namespace WowLauncher.ViewModels;

/// <summary>
/// The report a player writes when something is wrong.
///
/// <para><b>Why the launcher does this at all</b> rather than pointing at a web form: the four facts
/// that usually decide a diagnosis are ones the player cannot give. Which build they run, whether the
/// manifest signature verified, where their client sits, what failed last. The launcher knows all of
/// it, so it attaches it and the player only has to say what they saw.</para>
///
/// <para><b>The preview is not decoration.</b> This sends the player's own log, which carries their
/// folder names. They see exactly what goes out before it goes out; a bundle nobody can inspect is one
/// they would be right to refuse.</para>
/// </summary>
public sealed partial class ProblemReportViewModel : ViewModelBase
{
    private readonly ProblemReport _report;
    private readonly IProblemReportSender _sender;

    public ProblemReportViewModel(ProblemReport report, IProblemReportSender sender)
    {
        _report = report;
        _sender = sender;
        RefreshPreview();
    }

    /// <summary>What the player saw. The one thing only they can supply, so the send button waits
    /// for it: a report saying nothing costs someone a reply asking what happened.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _message = "";

    /// <summary>Optional. Without it a report can be read but not answered, which is a fair trade to
    /// offer rather than a field to force.</summary>
    [ObservableProperty]
    private string _contact = "";

    /// <summary>Exactly what will be sent, refreshed as the player types.</summary>
    [ObservableProperty]
    private string _preview = "";

    /// <summary>Set once the report is away. Carries the reference the player can quote.</summary>
    [ObservableProperty]
    private string _sentReference = "";

    /// <summary>Why it did not go out, in words a player can act on.</summary>
    [ObservableProperty]
    private string _error = "";

    public bool IsSent => !string.IsNullOrEmpty(SentReference);
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>A report with no description is one nobody can act on.</summary>
    public bool CanSend => !string.IsNullOrWhiteSpace(Message);

    partial void OnMessageChanged(string value) => RefreshPreview();
    partial void OnContactChanged(string value) => RefreshPreview();

    partial void OnSentReferenceChanged(string value) => OnPropertyChanged(nameof(IsSent));
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    private void RefreshPreview() =>
        Preview = ProblemReport.Preview(_report.Build(Message, Contact));

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync(CancellationToken ct)
    {
        Error = "";
        var payload = _report.Build(Message, Contact);

        // No ConfigureAwait(false): what follows assigns to bound properties, so the continuation has
        // to come back to the UI thread.
        var result = await _sender.SendAsync(payload, ct);

        if (result.Ok)
            SentReference = string.IsNullOrWhiteSpace(result.Reference)
                ? Loc.T("Report_Sent_NoReference")
                : result.Reference!;
        else
            Error = result.Error ?? Loc.T("Report_Failed_Generic");
    }
}

/// <summary>
/// Stands in when a <see cref="ShellViewModel"/> is built without a sender, which only happens in
/// tests. It refuses rather than pretending to send: a double that silently reported success would
/// make a broken wiring look like a working feature.
/// </summary>
internal sealed class NullProblemReportSender : IProblemReportSender
{
    public Task<ProblemReportResult> SendAsync(ProblemReportPayload payload, CancellationToken ct = default) =>
        Task.FromResult(ProblemReportResult.Failed("Reporting is not wired up in this build."));
}
