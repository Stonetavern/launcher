using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WowLauncher.Views;

/// <summary>The player's answer to the keep-running prompt. Null result (window dismissed by the WM,
/// Escape, or Alt+F4) means "do nothing" - the launcher neither hides nor quits.</summary>
public sealed record ClosePromptResult(bool KeepRunning, bool Remember);

/// <summary>
/// The keep-running prompt. A thin renderer: the two buttons close the dialog with a
/// <see cref="ClosePromptResult"/>; all of the decision and persistence logic lives in
/// <see cref="Services.CloseBehavior"/> and the close flow in <see cref="App"/>. Code-behind here is
/// view-only mechanics (reading the checkbox, returning the result), which the avalonia-desktop skill
/// permits.
/// </summary>
public partial class ClosePromptWindow : Window
{
    // Must call the generated InitializeComponent (not AvaloniaXamlLoader.Load directly): only the
    // generated method wires up the x:Name fields, so RememberCheck would otherwise be null and
    // reading it in the button handlers threw a NullReferenceException the moment "Keep running" was
    // clicked (crashed the whole app on close, 2026-07-23).
    public ClosePromptWindow() => InitializeComponent();

    private bool Remember => RememberCheck.IsChecked == true;

    private void OnKeepRunningClick(object? sender, RoutedEventArgs e)
        => Close(new ClosePromptResult(KeepRunning: true, Remember));

    private void OnQuitClick(object? sender, RoutedEventArgs e)
        => Close(new ClosePromptResult(KeepRunning: false, Remember));
}
