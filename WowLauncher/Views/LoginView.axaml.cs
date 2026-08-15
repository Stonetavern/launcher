using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WowLauncher.Views;

/// <summary>v3 sign-in card, hosted in the friends sidebar when signed out. Pure renderer — all state
/// and the sign-in command live in <see cref="WowLauncher.ViewModels.LoginViewModel"/>.</summary>
public partial class LoginView : UserControl
{
    /// <summary>
    /// Whether the card wears the FRIENDS caption above it. True in the sidebar, where the card IS the
    /// friends gate and the caption keeps the column's rhythm. False on the account page, which already
    /// carries its own heading and its own lead sentence.
    ///
    /// <para>Why this exists: the same card is hosted in two places, but the caption only made sense in
    /// one. On the account page it read as a second card stacked on the first, and once the armory
    /// started sending people straight to the sign-up form (2026-08-04) it also said "sign in to see
    /// friends" directly above a form for creating an account. The property belongs on the CONTROL and
    /// not on the view model, because the view model is a process-lifetime singleton shared by both
    /// hosts: a flag on it would flip in one place and change the other.</para>
    /// </summary>
    public static readonly StyledProperty<bool> ShowFriendsHeaderProperty =
        AvaloniaProperty.Register<LoginView, bool>(nameof(ShowFriendsHeader), defaultValue: true);

    public bool ShowFriendsHeader
    {
        get => GetValue(ShowFriendsHeaderProperty);
        set => SetValue(ShowFriendsHeaderProperty, value);
    }

    public LoginView() => AvaloniaXamlLoader.Load(this);
}
