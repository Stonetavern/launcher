using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WowLauncher.Views;

/// <summary>
/// The report dialog. A thin renderer: everything it shows comes from
/// <see cref="ViewModels.ProblemReportViewModel"/>, and the only code here is closing the window,
/// which is view-only mechanics.
/// </summary>
public partial class ProblemReportWindow : Window
{
    // The generated InitializeComponent, not AvaloniaXamlLoader directly: only the generated method
    // wires up x:Name fields. Same trap that crashed the close prompt on 2026-07-23.
    public ProblemReportWindow() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
