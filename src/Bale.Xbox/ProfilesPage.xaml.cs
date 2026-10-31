using System.Numerics;
using Bale.Core.Auth;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Microsoft.Kiota.Abstractions;
using Windows.System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>Who's watching, including the remote-first PIN and first-pick remember offer.</summary>
public sealed partial class ProfilesPage : Page, IBackHandler
{
    private readonly Rail _actions;
    private readonly ProfilePin _pin = new();
    private CancellationTokenSource? _loading;
    private CancellationTokenSource? _pinVerification;
    private ProfileView? _pinProfile;
    private ProfileView? _chosen;
    private int _lastProfileIndex;
    private bool _active;
    private bool _verifying;
    private bool _pinError;
    private bool _leaving;

    public ProfilesPage()
    {
        InitializeComponent();
        _actions = Rail.Attach(PickerActions);
        _actions.Pin = false;
        Rail.Attach(RememberActions).Pin = false;
        RetryButton.PreviewKeyDown += OnRetryPreviewKeyDown;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _active = true;
        // TvAppNavigation.kt onSwitchProfile replaces Main: Back from the picker leaves the app.
        Frame.BackStack.Clear();
        await LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _active = false;
        _loading?.Cancel();
        _pinVerification?.Cancel();
    }

    public bool OnBack()
    {
        if (_pinProfile is not null)
        {
            ClosePin();
        }
        else if (RememberPanel.Visibility == Visibility.Visible)
        {
            AnswerRememberOffer(remember: false);
        }
        else
        {
            Application.Current.Exit();
        }
        return true;
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (Session.Client is not { } client)
        {
            return;
        }

        _loading?.Cancel();
        using var loading = new CancellationTokenSource();
        _loading = loading;
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        SetAccountName(client.Account?.Username ?? AppSettings.AccountName);
        try
        {
            var profiles = await client.GetProfilesAsync(loading.Token);
            if (!_active || loading.IsCancellationRequested || !ReferenceEquals(Session.Client, client))
            {
                return;
            }

            var views = profiles.Select(p => new ProfileView(p, ProfileAvatars.ImageUrl(p.AvatarUrl, client.Browse.Resolve))).ToList();
            Profiles.ItemsSource = views;
            Profiles.Width = Math.Min(6, Math.Max(1, views.Count)) * 142;
            if (views.Count == 0)
            {
                ShowError("No profiles are available on this account.");
                return;
            }

            var current = views.FindIndex(p => p.Profile.Id == (client.ProfileId ?? AppSettings.ProfileId));
            _lastProfileIndex = Math.Max(current, 0);
            RestoreProfileFocus();
            if (client.Account is null)
            {
                try
                {
                    var account = await client.GetAccountAsync(loading.Token);
                    if (_active && !loading.IsCancellationRequested && ReferenceEquals(Session.Client, client))
                    {
                        SetAccountName(account?.Username);
                        AppSettings.AccountName = account?.Username;
                    }
                }
                catch (Exception ex) when (ex is not ApiException { ResponseStatusCode: 401 } && ex is not OperationCanceledException)
                {
                    // TvProfileSelectionScreen.kt reserves the account line while the account loads.
                    // A missing account label must not hide already-loaded profiles when offline.
                    Log.Error("Profile account name", ex);
                }
            }
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested)
        {
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 401)
        {
            if (_active && ReferenceEquals(Session.Client, client))
            {
                Session.Expire();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Profiles load", ex);
            if (_active && !loading.IsCancellationRequested)
            {
                ShowError($"Couldn't load profiles. {ex.Message}");
            }
        }
        finally
        {
            if (ReferenceEquals(_loading, loading))
            {
                _loading = null;
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void SetAccountName(string? name) => AccountText.Text = string.IsNullOrEmpty(name) ? " " : $"Signed in as {name}";

    private void ShowError(string error)
    {
        ErrorText.Text = error;
        ErrorPanel.Visibility = Visibility.Visible;
        FocusRetry.Until(ErrorPanel, () => !_active || ErrorPanel.Visibility != Visibility.Visible || RetryButton.Focus(FocusState.Programmatic));
    }

    private void OnProfileFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: ProfileView } item)
        {
            _lastProfileIndex = Math.Max(Profiles.IndexFromContainer(item), 0);
            ApplyProfileFocus(item, focused: true);
        }
    }

    private void OnProfileBlur(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: ProfileView } item)
        {
            ApplyProfileFocus(item, focused: false);
        }
    }

    private static void ApplyProfileFocus(ContentControl item, bool focused)
    {
        if (item.ContentTemplateRoot is not FrameworkElement root)
        {
            return;
        }

        // TvProfileSelectionScreen.kt: lift only the avatar 1.12, with a white ring and no glow.
        if (root.FindName("Art") is FrameworkElement art)
        {
            art.ScaleTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(120) };
            art.CenterPoint = new Vector3((float)art.ActualWidth / 2, (float)art.ActualHeight / 2, 0);
            var scale = focused ? 1.12f : 1f;
            art.Scale = new Vector3(scale, scale, 1);
        }
        if (root.FindName("FocusRing") is UIElement ring)
        {
            ring.Opacity = focused ? 1 : 0;
        }
        if (root.FindName("ProfileCaption") is TextBlock caption)
        {
            caption.Foreground = new SolidColorBrush(focused ? Colors.White : Color.FromArgb(191, 237, 237, 237));
            caption.Margin = new Thickness(0, focused ? 22 : 13, 0, 0);
        }
    }

    private bool FocusProfile(int index)
    {
        if (index < 0 || index >= Profiles.Items.Count || Profiles.ItemsPanelRoot is not { } panel)
        {
            return false;
        }

        // Native AOT reports ContentControl, and ContainerFromIndex can return null (Rail.cs).
        return panel.Children.OfType<ContentControl>().FirstOrDefault(item => ReferenceEquals(item.Content, Profiles.Items[index]))
            ?.Focus(FocusState.Programmatic) == true;
    }

    private void RestoreProfileFocus() => FocusRetry.Until(Profiles,
        () => !_active || PickerPanel.Visibility != Visibility.Visible || FocusProfile(_lastProfileIndex));

    private void OnProfilesPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var (x, y) = Direction(e.Key);
        if (x == 0 && y == 0)
        {
            return;
        }

        e.Handled = true;
        var item = FocusManager.GetFocusedElement() as ContentControl;
        if (item?.Content is not ProfileView)
        {
            return;
        }

        var index = Profiles.IndexFromContainer(item);
        if (index < 0)
        {
            return;
        }

        var columns = Math.Min(6, Profiles.Items.Count);
        if (x != 0)
        {
            var target = index + x;
            if (target >= 0 && target < Profiles.Items.Count && target / columns == index / columns)
            {
                FocusProfile(target);
            }
        }
        else if (y < 0 && index >= columns)
        {
            FocusProfile(index - columns);
        }
        else if (y > 0)
        {
            if (index + columns < Profiles.Items.Count)
            {
                FocusProfile(index + columns);
            }
            else if (index / columns == (Profiles.Items.Count - 1) / columns)
            {
                _actions.Focus(FocusState.Keyboard);
            }
            // No tile directly below in an incomplete row: stay, never move diagonally.
        }
    }

    private void OnPickerActionsPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var (_, y) = Direction(e.Key);
        if (y != 0)
        {
            e.Handled = true;
            if (y < 0)
            {
                if (Profiles.Items.Count > 0)
                {
                    FocusProfile(_lastProfileIndex);
                }
                else if (ErrorPanel.Visibility == Visibility.Visible)
                {
                    RetryButton.Focus(FocusState.Keyboard);
                }
            }
        }
    }

    private void OnRetryPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var (x, y) = Direction(e.Key);
        if (x != 0 || y != 0)
        {
            e.Handled = true;
            if (y > 0)
            {
                _actions.Focus(FocusState.Keyboard);
            }
            else if (y < 0 && Profiles.Items.Count > 0)
            {
                FocusProfile(_lastProfileIndex);
            }
        }
    }

    private void OnProfileClick(object sender, ItemClickEventArgs e)
    {
        if (_leaving || e.ClickedItem is not ProfileView chosen || Session.Client is not { } client || string.IsNullOrEmpty(chosen.Profile.Id))
        {
            return;
        }

        if (chosen.Profile.HasPin == true)
        {
            OpenPin(chosen);
        }
        else
        {
            client.UseProfile(chosen.Profile.Id);
            CompleteSelection(chosen);
        }
    }

    private void OpenPin(ProfileView chosen)
    {
        _pinProfile = chosen;
        _pin.Clear();
        _pinError = false;
        _verifying = false;
        PinName.Text = chosen.Name;
        PinInitial.Text = chosen.Initial;
        PinAvatarBrush.ImageSource = chosen.Avatar;
        PinStatus.Text = "Enter your PIN";
        PinStatus.Foreground = new SolidColorBrush(Color.FromArgb(191, 237, 237, 237));
        PaintDots();
        PickerPanel.Visibility = Visibility.Collapsed;
        PinPanel.Visibility = Visibility.Visible;
        FocusRetry.Until(PinPanel, () => !_active || !ReferenceEquals(_pinProfile, chosen) || PinFiveButton.Focus(FocusState.Programmatic));
    }

    private void ClosePin()
    {
        _pinVerification?.Cancel();
        Session.Client?.UseProfile(null);
        _pinProfile = null;
        _pin.Clear();
        _verifying = false;
        PinPanel.Visibility = Visibility.Collapsed;
        PickerPanel.Visibility = Visibility.Visible;
        RestoreProfileFocus();
    }

    private void OnPinCancelClick(object sender, RoutedEventArgs e) => ClosePin();

    private void OnPinKeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            if (tag == "delete")
            {
                DeleteDigit();
            }
            else if (tag.Length == 1)
            {
                EnterDigit(tag[0]);
            }
        }
    }

    private void OnPinPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Back)
        {
            // App.OnBackKey handles Backspace outside text boxes. Consume it on the tunnelling pass
            // before it bubbles there, so it deletes a dot instead of closing this prompt.
            e.Handled = true;
            DeleteDigit();
            return;
        }

        var key = (int)e.Key;
        if (key >= (int)VirtualKey.Number0 && key <= (int)VirtualKey.Number9)
        {
            e.Handled = true;
            EnterDigit((char)('0' + key - (int)VirtualKey.Number0));
            return;
        }
        if (key >= (int)VirtualKey.NumberPad0 && key <= (int)VirtualKey.NumberPad9)
        {
            e.Handled = true;
            EnterDigit((char)('0' + key - (int)VirtualKey.NumberPad0));
            return;
        }

        var (x, y) = Direction(e.Key);
        if (x == 0 && y == 0)
        {
            return;
        }

        e.Handled = true;
        if (FocusManager.GetFocusedElement() is not Button focused)
        {
            return;
        }
        if (focused == PinCancelButton)
        {
            if (y < 0)
            {
                PinZeroButton.Focus(FocusState.Keyboard);
            }
            return;
        }
        if (!PinKeypad.Children.Contains(focused))
        {
            return;
        }

        var row = Grid.GetRow(focused);
        var column = Grid.GetColumn(focused);
        var target = PinKeypad.Children.OfType<Button>().FirstOrDefault(button =>
            Grid.GetRow(button) == row + y * 2 && Grid.GetColumn(button) == column + x * 2);
        if (target is not null)
        {
            target.Focus(FocusState.Keyboard);
        }
        else if (y > 0 && focused == PinZeroButton)
        {
            PinCancelButton.Focus(FocusState.Keyboard);
        }
        // The empty cell below 7 and every other keypad edge stay put, never shift sideways.
    }

    private void EnterDigit(char digit)
    {
        if (_verifying || _pinProfile is null || !_pin.Append(digit))
        {
            return;
        }

        ClearPinError();
        PaintDots();
        if (_pin.IsComplete)
        {
            _ = VerifyPinAsync();
        }
    }

    private void DeleteDigit()
    {
        if (_verifying)
        {
            return;
        }
        _pin.Delete();
        ClearPinError();
        PaintDots();
    }

    private void ClearPinError()
    {
        _pinError = false;
        PinStatus.Text = "Enter your PIN";
        PinStatus.Foreground = new SolidColorBrush(Color.FromArgb(191, 237, 237, 237));
    }

    private async Task VerifyPinAsync()
    {
        if (_pinProfile is not { } chosen || Session.Client is not { } client || _verifying)
        {
            return;
        }

        using var verification = new CancellationTokenSource();
        _pinVerification = verification;
        _verifying = true;
        PinStatus.Text = "Checking…";
        try
        {
            var valid = await client.VerifyPinAsync(chosen.Profile.Id!, _pin.Digits, verification.Token);
            if (!_active || verification.IsCancellationRequested || !ReferenceEquals(_pinProfile, chosen) || !ReferenceEquals(Session.Client, client))
            {
                return;
            }

            if (valid)
            {
                _pinProfile = null;
                PinPanel.Visibility = Visibility.Collapsed;
                CompleteSelection(chosen);
            }
            else
            {
                ShowPinError("Wrong PIN. Try again.");
            }
        }
        catch (OperationCanceledException) when (verification.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Profile PIN", ex);
            if (_active && !verification.IsCancellationRequested && ReferenceEquals(_pinProfile, chosen))
            {
                ShowPinError("Network error. Please try again.");
            }
        }
        finally
        {
            if (ReferenceEquals(_pinVerification, verification))
            {
                _pinVerification = null;
                _verifying = false;
            }
        }
    }

    private void ShowPinError(string message)
    {
        _pin.Clear();
        _pinError = true;
        PinStatus.Text = message;
        PinStatus.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 138, 128));
        PaintDots();
        // TvPinEntryDialog.kt keeps the keypad focusable while checking; no retry focus grab is needed.
    }

    private void PaintDots()
    {
        var index = 0;
        foreach (var dot in PinDots.Children.OfType<Border>())
        {
            var filled = _pinError || index++ < _pin.Digits.Length;
            var ink = _pinError ? Color.FromArgb(255, 255, 138, 128) : Colors.White;
            dot.BorderBrush = new SolidColorBrush(ink);
            dot.Background = new SolidColorBrush(filled ? ink : Colors.Transparent);
        }
    }

    private void CompleteSelection(ProfileView chosen)
    {
        _chosen = chosen;
        Session.ProfileName = chosen.Name;
        Session.ProfileAvatar = chosen.AvatarUrl;
        if (!AppSettings.ProfileRememberOfferAnswered)
        {
            // TvProfileSelectionScreen.kt offers the first pick once; dismissal means keep asking.
            PickerPanel.Visibility = Visibility.Collapsed;
            RememberTitle.Text = $"Open {chosen.Name} automatically?";
            RememberPanel.Visibility = Visibility.Visible;
            FocusRetry.Until(RememberPanel, () => !_active || RememberPanel.Visibility != Visibility.Visible || RememberYesButton.Focus(FocusState.Programmatic));
            return;
        }

        OpenHome();
    }

    private void OnRememberYesClick(object sender, RoutedEventArgs e) => AnswerRememberOffer(remember: true);

    private void OnRememberNotNowClick(object sender, RoutedEventArgs e) => AnswerRememberOffer(remember: false);

    private void AnswerRememberOffer(bool remember)
    {
        if (_chosen is null || _leaving)
        {
            return;
        }
        AppSettings.OpenProfileAutomatically = remember;
        AppSettings.ProfileRememberOfferAnswered = true;
        OpenHome();
    }

    private void OnRememberPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Direction(e.Key).Y != 0)
        {
            e.Handled = true;
        }
    }

    private void OpenHome()
    {
        _leaving = true;
        Session.Persist();
        Frame.Navigate(typeof(ShellPage));
        Frame.BackStack.Clear();
    }

    private async void OnSignOutClick(object sender, RoutedEventArgs e) => await LeaveServerAsync();

    private async void OnChangeServerClick(object sender, RoutedEventArgs e) => await LeaveServerAsync();

    private async Task LeaveServerAsync()
    {
        if (_leaving)
        {
            return;
        }
        _leaving = true;
        _loading?.Cancel();
        await Session.SignOutAsync();
        if (_active)
        {
            // Bale currently holds one server. Both utilities return to its editable address form;
            // the official TvAppNavigation.kt Sign out also clears the stack to ServerSetup.
            Frame.Navigate(typeof(MainPage), MainPage.ServerEntry);
            Frame.BackStack.Clear();
        }
    }

    private static (int X, int Y) Direction(VirtualKey key) => key switch
    {
        VirtualKey.Left or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadLeftThumbstickLeft => (-1, 0),
        VirtualKey.Right or VirtualKey.GamepadDPadRight or VirtualKey.GamepadLeftThumbstickRight => (1, 0),
        VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp => (0, -1),
        VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown => (0, 1),
        _ => (0, 0),
    };
}
