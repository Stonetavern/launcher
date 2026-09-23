using System.Windows.Input;
using Avalonia.Threading;
using WowLauncher.ViewModels;

namespace WowLauncher.Services.AgentControl;

/// <summary>
/// Connects the control surface to the live <see cref="PlayViewModel"/>.
///
/// <para><b>Everything here runs on the UI thread.</b> The HTTP listener answers on a thread-pool
/// thread, and both reading a bound property and invoking a command from there is a data race that
/// shows up as a wrong reading or a torn update rather than as a crash — the worst kind. Every access
/// goes through <see cref="Dispatcher.UIThread"/>, so a harness sees precisely what the window would
/// show at that instant.</para>
///
/// <para><b>Commands are named, not discovered.</b> The map below is the whole reachable surface.
/// Reflecting over the ViewModel would have exposed whatever happens to be public today and would
/// change silently whenever someone adds a command — a test harness would gain powers nobody granted
/// it. Adding one here is a deliberate line of code.</para>
/// </summary>
public sealed class PlayViewModelAgentSurface(PlayViewModel play) : IAgentControlSurface
{
    private readonly PlayViewModel _play = play ?? throw new ArgumentNullException(nameof(play));

    /// <summary>Command name (as used in the URL) → the command on the ViewModel.</summary>
    private IReadOnlyDictionary<string, ICommand> Commands => new Dictionary<string, ICommand>(
        StringComparer.OrdinalIgnoreCase)
    {
        ["play"] = _play.PlayCommand,
        ["repair"] = _play.RepairCommand,
        ["checkForUpdates"] = _play.CheckForUpdatesCommand,
        ["pauseDownload"] = _play.PauseDownloadCommand,
        ["locateExistingClient"] = _play.LocateExistingClientCommand,
        ["openLogs"] = _play.OpenLogsCommand,
    };

    public Task<AgentState> GetStateAsync(CancellationToken ct = default) =>
        Dispatcher.UIThread.InvokeAsync(() => new AgentState
        {
            State = _play.State.ToString(),
            Headline = _play.ActionPrimaryText,
            Detail = _play.StatusLine,
            Progress = _play.DownloadProgress,
            Busy = _play.IsBusy,
            ClientBuild = _play.SelectedClientChoice?.Client.Build,
            SubLine = string.IsNullOrWhiteSpace(_play.SubLine) ? null : _play.SubLine,
            Commands = Commands.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.CanExecute(null),
                StringComparer.OrdinalIgnoreCase),
        }).GetTask();

    public Task<AgentCommandResult> InvokeAsync(string command, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!Commands.TryGetValue(command, out var target))
            {
                return new AgentCommandResult(false,
                    $"unknown command '{command}'; known: {string.Join(", ", Commands.Keys)}");
            }

            // The guard is asked, not bypassed. A harness that presses a disabled button must get the
            // same "no" a player gets — otherwise it proves the app can do something it visibly cannot.
            if (!target.CanExecute(null))
                return new AgentCommandResult(false, $"'{command}' is disabled in state {_play.State}");

            target.Execute(null);
            return new AgentCommandResult(true, $"'{command}' started in state {_play.State}");
        }).GetTask();
    }
}
