using Bale.Core.Profiles;
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
    // TvTopMenuBar.kt: the avatar always waits the first 120ms, even when switching from another panel.
    private readonly DispatcherTimer _avatarDwell = new() { Interval = TopMenuPreviewTiming.Delay(hasOpenPanel: false, isProfile: true) };
    private bool _profileMenuEntered;
    private bool _profileDwellSuppressed;
    private bool _restoringProfileFocus;
    private bool _signingOut;

    private void InitializeProfileMenu()
    {
        AvatarButton.GotFocus += OnAvatarFocus;
        AvatarButton.LostFocus += OnAvatarBlur;
        ProfilePanel.PreviewKeyDown += OnProfilePanelPreviewKeyDown;
        ProfilePanel.PreviewKeyUp += OnProfilePanelPreviewKeyUp;
        SignOutPrompt.SignOutRequested += OnSignOutRequested;
        SignOutPrompt.Dismissed += OnSignOutDismissed;
        _avatarDwell.Tick += (_, _) =>
        {
            _avatarDwell.Stop();
            if (!_profileDwellSuppressed && AvatarButton.FocusState != FocusState.Unfocused) OpenProfilePanel(enter: false);
        };
    }

    private void OnAvatarFocus(object sender, RoutedEventArgs e)
    {
        // TvTopMenuBar.kt leaves the old cascade visible until the 120ms profile dwell finishes;
        // TvShellFocusState.kt previewProfileMenu closes it only when that preview actually opens.
        _dwell.Stop();
        _dwellTab = null;
        _avatarDwell.Stop();
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
        _ = RefreshRequestsCapabilityAsync();
        // TvProfileDropdown focuses its first row, History.
        AvatarButton.XYFocusDown = HistoryRow;
        if (enter)
        {
            FocusRetry.Until(ProfilePanel, () => ProfilePanel.Visibility != Visibility.Visible || HistoryRow.Focus(FocusState.Programmatic));
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

    private void OnProfilePanelPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Escape or VirtualKey.Back)) return;
        e.Handled = true;
        DismissProfileMenu();
    }

    private void OnProfilePanelPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            // TvProfileDropdown dismisses on KeyUp. Consume KeyDown so the app-wide Back policy
            // cannot close this menu early or handle a held Escape again on the underlying page.
            if (e.Key is VirtualKey.Escape or VirtualKey.Back)
            {
                e.Handled = true;
                return;
            }
            var step = e.Key switch
            {
                VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp => -1,
                VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown => 1,
                _ => 0,
            };
            if (step != 0)
            {
                e.Handled = true;
                Control[] candidates = [HistoryRow, RequestsRow, SettingsRow, SwitchProfileRow, AddProfileRow, ChangeAccountRow, SignOutRow];
                var rows = candidates.Where(row => row.Visibility == Visibility.Visible && row.IsEnabled).ToArray();
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

    private void ColorProfileIcon(object sender, bool focused)
    {
        // A Path doesn't inherit ContentPresenter.Foreground. Recolor the content icon explicitly,
        // as CardActionMenu does, without casting a ContentPresenter under Native AOT. Sign Out
        // rests in the destructive red (TvPlayerChrome.StopText) and inverts like the others.
        var destructive = ReferenceEquals(sender, SignOutRow);
        var rest = destructive ? Color.FromArgb(255, 255, 105, 97) : Color.FromArgb(230, 237, 237, 237);
        if (sender is ContentControl { Content: DependencyObject content })
        {
            if (FindChild<Shape>(content) is { } icon) icon.Fill = new SolidColorBrush(focused ? Colors.Black : rest);
            if (destructive && FindChild<TextBlock>(content) is { } label) label.Foreground = new SolidColorBrush(focused ? Colors.Black : rest);
        }
    }

    /// <summary>ProfileMenuRow.kt: Switch Profile, Add Profile or no row, by the account's profile count and limit.</summary>
    private void ShowProfileRow(ProfileMenuRow row)
    {
        SwitchProfileRow.Visibility = row == ProfileMenuRow.SwitchProfile ? Visibility.Visible : Visibility.Collapsed;
        AddProfileRow.Visibility = row == ProfileMenuRow.AddProfile ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAddProfileClick(object sender, RoutedEventArgs e)
    {
        if (_signingOut) return;
        // TvMainShell.kt closes the menu, then opens the picker's new-profile form (primary PIN first).
        ClosePanels();
        Session.ShowProfilePicker(Frame, add: true);
    }

    private void OnPersonalListClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string kind }) OpenPersonalList(kind);
    }

    private void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        if (_signingOut) return;
        // TvMainShell.kt hides the profile dropdown before opening TvSignOutDialog.kt.
        ClosePanels();
        SignOutPrompt.Show();
    }

    private async void OnSignOutRequested(bool removeServer) => await SignOutAsync(removeServer);

    private void OnSignOutDismissed()
    {
        // TvMainShell.kt's dismissProfileMenu restores the avatar, not the old Sign Out row.
        if (Current == this && !_signingOut) DismissProfileMenu();
    }

    private void OnChangeAccountClick(object sender, RoutedEventArgs e)
    {
        if (_signingOut) return;
        // TvAppNavigation.kt pushes Change Account over the shell without destroying the signed-in session.
        ClosePanels();
        _personalReturnToProfile = true;
        Frame.Navigate(typeof(ChangeAccountPage));
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();
}
