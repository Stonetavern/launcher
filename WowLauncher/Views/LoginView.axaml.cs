using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WowLauncher.Views;

/// <summary>v3 sign-in card, hosted in the friends sidebar when signed out. Pure renderer — all state
/// and the sign-in command live in <see cref="WowLauncher.ViewModels.LoginViewModel"/>.</summary>
public partial class LoginView : UserControl
{
    public LoginView() => AvaloniaXamlLoader.Load(this);
}
