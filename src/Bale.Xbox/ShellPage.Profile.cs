using Bale.Xbox.Services;
using Windows.System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace Bale.Xbox;

public sealed partial class ShellPage
{
    // TvTopMenuBar.kt: the avatar always waits 180ms, even when switching from another panel.
    private readonly DispatcherTimer _avatarDwell = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private bool _profileMenuEntered;
    private bool _profileDwellSuppressed;
    private bool _restoringProfileFocus;

    private void InitializeProfileMenu()
    {
        AvatarButton.GotFocus += OnAvatarFocus;
        AvatarButton.LostFocus += OnAvatarBlur;
        ProfilePanel.PreviewKeyDown += OnProfilePanelPreviewKeyDown;
        _avatarDwell.Tick += (_, _) =>
        {
            _avatarDwell.Stop();
            if (!_profileDwellSuppressed && AvatarButton.FocusState != FocusState.Unfocused) OpenProfilePanel(enter: false);
        };
    }

    private void OnAvatarFocus(object sender, RoutedEventArgs e)
    {
        ClosePanels();
        if (!_profileDwellSuppressed) _avatarDwell.Start();
    }

    private void OnAvatarBlur(object sender, RoutedEventArgs e)
    {
        _avatarDwell.Stop();
        _profileDwellSuppressed = false;
        // TvShellFocusState.closeProfilePreview: blur dismisses a preview, never a menu being entered.
        if (!_profileMenuEntered)
        {
            ProfilePanel.Visibility = Visibility.Collapsed;
            AvatarButton.XYFocusDown = null;
        }
    }

    private void OnAvatarClick(object sender, RoutedEventArgs e) => OpenProfilePanel();

    private void OpenProfilePanel(bool enter = true)
    {
        ClosePanels();
        _profileMenuEntered = enter;
        ProfilePanel.Visibility = Visibility.Visible;
        AvatarButton.XYFocusDown = SwitchProfileRow;
        if (enter)
        {
            FocusRetry.Until(ProfilePanel, () => ProfilePanel.Visibility != Visibility.Visible || SwitchProfileRow.Focus(FocusState.Programmatic));
        }
    }

    private void DismissProfileMenu()
    {
        ClosePanels();
        // TvTopMenuBar.profileFocusRequest suppresses dwell until the viewer leaves the avatar.
        _profileDwellSuppressed = true;
        FocusRetry.Until(AvatarButton, () =>
        {
            _restoringProfileFocus = true;
            try { return AvatarButton.Focus(FocusState.Programmatic); }
            finally { _restoringProfileFocus = false; }
        });
    }

    private void OnProfilePanelPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            var step = e.Key switch
            {
                VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp => -1,
                VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown => 1,
                _ => 0,
            };
            if (step != 0)
            {
                e.Handled = true;
                Control[] rows = [SwitchProfileRow, WatchlistRow, FavoritesRow, HistoryRow, SwitchServerRow, SignOutRow];
                var focused = FocusManager.GetFocusedElement() as DependencyObject;
                var index = Array.FindIndex(rows, row => IsWithin(focused, row));
                rows[Math.Clamp(index + step, 0, rows.Length - 1)].Focus(FocusState.Keyboard);
            }
            else if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Tab
                or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadDPadRight
                or VirtualKey.GamepadLeftThumbstickLeft or VirtualKey.GamepadLeftThumbstickRight)
            {
                // TvProfileDropdown cancels every directional exit; Left/Up do NOT close it.
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            e.Handled = true;
            Log.Error("Profile menu navigation", ex);
        }
    }

    private void OnProfileRowFocus(object sender, RoutedEventArgs e) => ColorProfileIcon(sender, true);
    private void OnProfileRowBlur(object sender, RoutedEventArgs e) => ColorProfileIcon(sender, false);

    private static void ColorProfileIcon(object sender, bool focused)
    {
        // A Path doesn't inherit ContentPresenter.Foreground. Recolor the content icon explicitly,
        // as CardActionMenu does, without casting a ContentPresenter under Native AOT.
        if (sender is ContentControl { Content: DependencyObject content } && FindChild<Shape>(content) is { } icon)
        {
            icon.Fill = new SolidColorBrush(focused ? Colors.Black : Color.FromArgb(230, 237, 237, 237));
        }
    }

    private void OnPersonalListClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string kind }) OpenPersonalList(kind);
    }

    private async void OnSwitchServerClick(object sender, RoutedEventArgs e)
    {
        // ProfilesPage.LeaveServerAsync already defines Bale's single-server change-server flow.
        ClosePanels();
        await SignOutAsync();
    }
}
