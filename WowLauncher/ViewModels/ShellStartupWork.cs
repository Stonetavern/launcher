using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;

namespace WowLauncher.ViewModels;

/// <summary>
/// The real startup work behind the splash: whatever <see cref="ShellViewModel.InitAsync"/> already
/// does (news, signed manifest, launcher self-update, addon catalogue, installed client, realm status).
///
/// <para>This is an adapter, not a second startup path. It adds exactly one thing the splash cannot
/// see by itself: a signal for the moment the launcher decides to replace its own binary.</para>
///
/// <para><b>Two sources for that one signal, and the order matters.</b> The primary source is
/// <see cref="IUpdateService.LauncherUpdateStarting"/>, raised BEFORE the download. The secondary is
/// <see cref="PlayViewModel.State"/> turning <see cref="LauncherState.UpdatingLauncher"/>, which the
/// update path sets only AFTER the swap has been handed off. Watching the state alone was the original
/// design and it was measurably too late: in the win11 VM on 2026-08-02 the 61 MB download took 14 s,
/// the splash's twelve-second budget expired first, and the shell appeared two seconds before the
/// process exited — a window that flashes up and disappears, which a player reads as a crash. The
/// state remains observed as a fallback for any path that reaches the swap without raising the event;
/// whichever arrives first wins, and the announcement is made exactly once either way.</para>
///
/// <para><see cref="ShellViewModel.InitAsync"/> takes no cancellation token today, and giving it one
/// would mean threading a token through the update path that was just repaired and shipped. The token
/// therefore governs the splash and its own waits; a cancelled start hands over immediately and the
/// remaining work finishes into a shell that is already on screen.</para>
/// </summary>
public sealed class ShellStartupWork : IStartupWork, IDisposable
{
    private readonly Func<Task> _run;
    private readonly INotifyPropertyChanged _source;
    private readonly string _property;
    private readonly Func<bool> _isUpdatingLauncher;
    private readonly IUpdateService? _update;
    private bool _announced;

    public ShellStartupWork(ShellViewModel shell, IUpdateService? update = null)
        : this(shell.InitAsync, shell.Play, nameof(PlayViewModel.State),
               () => shell.Play.State == LauncherState.UpdatingLauncher, update)
    {
    }

    /// <summary>Test seam: the same wiring against any observable source, so the "a self-update is
    /// announced exactly once" rule can be proven without a manifest, a network or a real update.</summary>
    internal ShellStartupWork(Func<Task> run, INotifyPropertyChanged source, string property,
                             Func<bool> isUpdatingLauncher, IUpdateService? update = null)
    {
        _run = run;
        _source = source;
        _property = property;
        _isUpdatingLauncher = isUpdatingLauncher;
        _update = update;
        _source.PropertyChanged += OnSourcePropertyChanged;
        if (_update is not null) _update.LauncherUpdateStarting += OnUpdateStarting;
    }

    public event EventHandler? LauncherUpdateStarted;

    public Task RunAsync(CancellationToken ct) => _run();

    private void OnUpdateStarting(object? sender, EventArgs e) => Announce();

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != _property) return;
        if (!_isUpdatingLauncher()) return;

        Announce();
    }

    /// <summary>Exactly once, whichever of the two sources arrives first.</summary>
    private void Announce()
    {
        if (_announced) return;
        _announced = true;
        LauncherUpdateStarted?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _source.PropertyChanged -= OnSourcePropertyChanged;
        if (_update is not null) _update.LauncherUpdateStarting -= OnUpdateStarting;
    }
}
