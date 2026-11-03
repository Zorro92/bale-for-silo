using System.Numerics;
using Bale.Core.Auth;
using Bale.Core.Profiles;
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
public sealed partial class ProfilesPage : Page, IBackHandler, IRestoresFocus
{
    private readonly Rail _actions;
    private CancellationTokenSource? _loading;
    private CancellationTokenSource? _pinVerification;
    private ProfileView? _pinProfile;
    private ProfileView? _chosen;
    private int _lastProfileIndex;
    private bool _active;
    private bool _verifying;
    private bool _leaving;

    public ProfilesPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        _reverify = new(PinPrompt, ViewFor);
        DeletePrompt.Confirmed += OnDeleteConfirmed;
        DeletePrompt.Dismissed += () => { _deleteCandidate = null; RestoreFocus(); };
        _actions = Rail.Attach(PickerActions);
        _actions.Pin = false;
        Rail.Attach(RememberActions).Pin = false;
        RetryButton.PreviewKeyDown += OnRetryPreviewKeyDown;
        PinPrompt.PinEntered += pin => _ = VerifyPinAsync(pin);
        PinPrompt.Dismissed += () => { if (_pinProfile is not null) ClosePin(); };
        SignOutPrompt.SignOutRequested += OnSignOutRequested;
        SignOutPrompt.Dismissed += RestoreSignOutFocus;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _active = true;
        _leaving = false;
        var household = e.Parameter as ProfileNavigation;
        if (household is null && Session.Client is { } client) household = new(client.Profiles, client.Browse.Resolve);
        if (household is null) return;
        var returning = e.NavigationMode == NavigationMode.Back && ReferenceEquals(_household?.Repository, household.Repository);
        _pinProfile = _chosen = null;
        _pinForManagement = _verifying = _deleting = false;
        _mutation = _pinVerification = null;
        _deleteCandidate = null;
        RememberPanel.Visibility = Visibility.Collapsed;
        if (!returning)
        {
            _lastProfileIndex = 0;
            _lastDelete = _focusInUtilities = false;
            _lastAction = null;
        }
        if (!ReferenceEquals(_household?.Repository, household.Repository))
        {
            _profiles = [];
            Profiles.ItemsSource = null;
            _gate = new(household.Repository.Management);
            _lastProfileIndex = 0;
            _lastDelete = _focusInUtilities = false;
            _lastAction = null;
        }
        _household = household;
        _reverify.Activate(household.Repository.Management);
        // TvAppNavigation.kt makes the real picker a root; pushed editors and disposable previews retain Back.
        if (!household.IsPreview && e.NavigationMode != NavigationMode.Back) Frame.BackStack.Clear();
        await LoadAsync(restoreFocus: returning);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _active = false;
        _loading?.Cancel();
        _pinVerification?.Cancel();
        _mutation?.Cancel();
        _reverify.Deactivate();
        if (e.SourcePageType != typeof(ProfileEditorPage)) _gate?.Done();
        PinPrompt.Hide(restoreFocus: false);
        SignOutPrompt.Hide();
        DeletePrompt.Hide();
    }

    public bool OnBack()
    {
        if (DeletePrompt.Dismiss() || _reverify.Dismiss() || SignOutPrompt.Dismiss()) return true;
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
#if DEBUG_SERVER
            if (IsProfilePreview) return DebugServer.CloseAuthPreview(Frame);
#endif
            // TvProfileSelectionScreen.kt / StatusViews.kt install no error-screen Back handler;
            // TvAppNavigation.kt makes ProfileSelection a root, so Back leaves the app, not sign-in.
            Application.Current.Exit();
        }
        return true;
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync(bool restoreFocus = false)
    {
        if (_household is not { } household || !IsCurrent) return;
        var client = household.IsPreview ? null : Session.Client;
        _loading?.Cancel();
        using var loading = new CancellationTokenSource();
        _loading = loading;
        SetLoadState(isLoading: true);
        SetAccountName(household.IsPreview ? "Preview household" : client?.Account?.Username ?? AppSettings.AccountName);
        try
        {
#if DEBUG_SERVER
            if (!household.IsPreview) DebugServer.ThrowIfProfilesFaulted();
#endif
            var profiles = await household.Repository.ListAsync(loading.Token);
            if (!IsCurrent || loading.IsCancellationRequested || !ReferenceEquals(_household, household)) return;
            _profiles = profiles;
            RebuildProfiles(restoreFocus);
            SetLoadState(isLoading: false);
            RestoreFocus();
            if (client is { Account: null })
            {
                try
                {
                    var account = await client.GetAccountAsync(loading.Token);
                    if (IsCurrent && !loading.IsCancellationRequested && ReferenceEquals(Session.Client, client))
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
        catch (Exception ex)
        {
            Log.Error("Profiles load", ex);
            if (IsCurrent && !loading.IsCancellationRequested && ReferenceEquals(_household, household))
            {
                // TvProfileSelectionViewModel.kt: network failures use this copy; API errors keep their message.
                ShowError(ex is HttpRequestException ? "Network error. Please try again."
                    : string.IsNullOrWhiteSpace(ex.Message) ? "Failed to load profiles" : ex.Message);
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

    private void SetLoadState(bool isLoading, string? error = null)
    {
        var state = new ProfilePickerState(isLoading, Profiles.Items.Count, error);
        PickerPanel.Visibility = state.ShowPicker ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.IsActive = state.ShowLoading;
        LoadingRing.Visibility = state.ShowLoading ? Visibility.Visible : Visibility.Collapsed;
        FullScreenErrorText.Text = InlineErrorText.Text = error ?? string.Empty;
        FullScreenErrorPanel.Visibility = state.ShowFullScreenError ? Visibility.Visible : Visibility.Collapsed;
        InlineErrorText.Visibility = state.ShowInlineError ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string error)
    {
        SetLoadState(isLoading: false, error);
        if (FullScreenErrorPanel.Visibility == Visibility.Visible)
        {
            // StatusViews.kt: Retry is the only focusable control on the full-screen error.
            FocusRetry.Until(FullScreenErrorPanel, () => !_active || FullScreenErrorPanel.Visibility != Visibility.Visible || RetryButton.Focus(FocusState.Programmatic));
        }
    }

    private void OnProfileFocus(object sender, RoutedEventArgs e)
    {
        var focused = FocusManager.GetFocusedElement() as DependencyObject;
        if (ProfileContainer(focused) is { Content: ProfileView view } item)
        {
            _lastProfileIndex = Math.Max(Profiles.IndexFromContainer(item), 0);
            _lastDelete = focused is Button;
            _focusInUtilities = false;
            Windows.UI.Xaml.Automation.AutomationProperties.SetName(item, view.AccessibleName);
            ApplyProfileFocus(item, focused: ReferenceEquals(focused, item));
        }
    }

    private void OnProfileBlur(object sender, RoutedEventArgs e)
    {
        if (ProfileContainer(e.OriginalSource as DependencyObject) is { } item)
            ApplyProfileFocus(item, focused: false);
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
        if (root.FindName("AddFill") is UIElement addFill) addFill.Opacity = focused ? 1 : 0;
        if (root.FindName("AddOutline") is Windows.UI.Xaml.Shapes.Ellipse outline)
            outline.Stroke = new SolidColorBrush(Color.FromArgb(focused ? (byte)179 : (byte)64, 255, 255, 255));
        if (root.FindName("AddIcon") is Windows.UI.Xaml.Shapes.Path addIcon)
            addIcon.Fill = new SolidColorBrush(Color.FromArgb(focused ? (byte)255 : (byte)102, 255, 255, 255));
        if (root.FindName("DeleteButton") is Button deleteButton)
        {
            deleteButton.Translation = new Vector3(0, focused ? 9 : 0, 0);
            if (root.FindName("DeleteIcon") is Windows.UI.Xaml.Shapes.Path deleteIcon)
                deleteIcon.Fill = new SolidColorBrush(ReferenceEquals(FocusManager.GetFocusedElement(), deleteButton) ? Colors.Black : Color.FromArgb(191, 237, 237, 237));
        }
        if (root.FindName("ProfileCaption") is TextBlock caption)
        {
            caption.Foreground = new SolidColorBrush(focused ? Colors.White : Color.FromArgb(191, 237, 237, 237));
            // Move the name 13 → 22 below the avatar without changing layout: a taller focused tile
            // overflows its ItemsWrapGrid cell (sized from the first tile) and gets clipped.
            caption.Translation = new Vector3(0, focused ? 9 : 0, 0);
        }
    }

    private bool FocusProfile(int index, bool delete = false)
    {
        if (index < 0 || index >= Profiles.Items.Count || Profiles.ItemsPanelRoot is not { } panel)
        {
            return false;
        }

        // Native AOT reports ContentControl, and ContainerFromIndex can return null (Rail.cs).
        var container = panel.Children.OfType<ContentControl>().FirstOrDefault(item => ReferenceEquals(item.Content, Profiles.Items[index]));
        if (!delete) return container?.Focus(FocusState.Programmatic) == true;
        return container?.ContentTemplateRoot is FrameworkElement root && root.FindName("DeleteButton") is Button { Visibility: Visibility.Visible } button
            && button.Focus(FocusState.Programmatic);
    }

    private void RestoreProfileFocus() => FocusRetry.Until(Profiles,
        () => !_active || PickerPanel.Visibility != Visibility.Visible || PinPrompt.IsOpen || DeletePrompt.IsOpen
            || FocusProfile(_lastProfileIndex, _lastDelete));

    private void OnProfilesPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var (x, y) = Direction(e.Key);
        if (x == 0 && y == 0)
        {
            return;
        }

        e.Handled = true;
        var focused = FocusManager.GetFocusedElement() as DependencyObject;
        var item = ProfileContainer(focused);
        if (item?.Content is not ProfileView view) return;
        var onDelete = focused is Button;

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
                FocusProfile(target, onDelete);
            }
        }
        else if (y < 0 && onDelete) FocusProfile(index);
        else if (y < 0 && index >= columns)
        {
            var previous = (ProfileView)Profiles.Items[index - columns];
            FocusProfile(index - columns, previous.DeleteVisibility == Visibility.Visible);
        }
        else if (y > 0)
        {
            if (!onDelete && view.DeleteVisibility == Visibility.Visible) FocusProfile(index, delete: true);
            else if (index + columns < Profiles.Items.Count)
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

    private void OnPickerActionFocus(object sender, RoutedEventArgs e)
    {
        _focusInUtilities = true;
        _lastAction = sender as Control;
        ColorPickerAction(sender, focused: true);
    }

    private void OnPickerActionBlur(object sender, RoutedEventArgs e) => ColorPickerAction(sender, focused: false);

    private void ColorPickerAction(object sender, bool focused)
    {
        // TvMarquee.kt uses the same Ink for its label and icon; a Path does not inherit Foreground.
        var icon = ReferenceEquals(sender, ManageButton) ? ManageIcon : ReferenceEquals(sender, ChangeServerButton) ? ChangeServerIcon : SignOutIcon;
        icon.Fill = new SolidColorBrush(focused ? Colors.Black : Color.FromArgb(255, 237, 237, 237));
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
                    FocusProfile(_lastProfileIndex, _lastDelete);
                }
            }
        }
    }

    private void OnRetryPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var (x, y) = Direction(e.Key);
        if (x != 0 || y != 0)
        {
            // StatusViews.kt has no picker actions behind Retry, so every directional edge stays put.
            e.Handled = true;
        }
    }

    private void OnProfileClick(object sender, ItemClickEventArgs e)
    {
        if (_leaving || _deleting || !IsCurrent || e.ClickedItem is not ProfileView chosen || FocusManager.GetFocusedElement() is Button) return;
        if (chosen.IsAdd) { ApplyManagementAction(_gate!.Request(_profiles, add: true)); return; }
        if (string.IsNullOrEmpty(chosen.Profile.Id)) return;
        if (_gate?.IsManaging == true) { OpenEditor(chosen.Profile); return; }
        if (chosen.Profile.HasPin == true) OpenPin(chosen);
        else
        {
            if (!IsProfilePreview) Session.Client!.UseProfile(chosen.Profile.Id);
            CompleteSelection(chosen);
        }
    }

    private void OpenPin(ProfileView chosen, bool management = false)
    {
        _pinProfile = chosen;
        _pinForManagement = management;
        _verifying = false;
        PinPrompt.Show(chosen, management ? "Enter this PIN to manage profiles" : "Enter your PIN");
        PickerPanel.Visibility = Visibility.Collapsed;
    }

    private void ClosePin()
    {
        _pinVerification?.Cancel();
        if (!_pinForManagement && !IsProfilePreview) Session.Client?.UseProfile(null);
        _gate?.CancelPin();
        _pinForManagement = false;
        _pinProfile = null;
        _verifying = false;
        PinPrompt.Hide(restoreFocus: false);
        PickerPanel.Visibility = Visibility.Visible;
        RestoreFocus();
    }

    private async Task VerifyPinAsync(string pin)
    {
        if (_pinProfile is not { } chosen || _household is not { } household || !IsCurrent || _verifying)
        {
            return;
        }

        using var verification = new CancellationTokenSource();
        _pinVerification = verification;
        _verifying = true;
        PinPrompt.SetVerifying(true);
        try
        {
            var management = _pinForManagement;
            var proof = management || household.IsPreview ? await household.Repository.VerifyPinAsync(chosen.Profile.Id!, pin, verification.Token) : null;
            var valid = management || household.IsPreview ? !string.IsNullOrWhiteSpace(proof)
                : await Session.Client!.VerifyPinAsync(chosen.Profile.Id!, pin, verification.Token);
            if (!IsCurrent || verification.IsCancellationRequested || !ReferenceEquals(_pinProfile, chosen) || !ReferenceEquals(_household, household))
            {
                return;
            }

            if (valid)
            {
                _pinProfile = null;
                _pinForManagement = false;
                PinPrompt.Hide(restoreFocus: false);
                if (management)
                {
                    PickerPanel.Visibility = Visibility.Visible;
                    ApplyManagementAction(_gate!.CompletePin(proof));
                }
                else CompleteSelection(chosen);
            }
            else
            {
                PinPrompt.ShowError("Wrong PIN. Try again.");
            }
        }
        catch (OperationCanceledException) when (verification.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Profile PIN", ex);
            if (IsCurrent && !verification.IsCancellationRequested && ReferenceEquals(_pinProfile, chosen))
            {
                PinPrompt.ShowError("Network error. Please try again.");
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

    private void CompleteSelection(ProfileView chosen)
    {
        if (IsProfilePreview)
        {
            PickerPanel.Visibility = Visibility.Visible;
            ShowError($"Preview selected {chosen.Name}; signed-in profile unchanged.");
            RestoreFocus();
            return;
        }
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
        if (IsProfilePreview) return;
        _leaving = true;
        Session.Persist();
        Frame.Navigate(typeof(ShellPage));
        Frame.BackStack.Clear();
    }

    private void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        if (IsProfilePreview) { ShowError("Profile preview; signed-in session unchanged."); return; }
        if (!_leaving && !_deleting) SignOutPrompt.Show();
    }

    private async void OnSignOutRequested(bool removeServer) => await LeaveServerAsync(removeServer);

    private void RestoreSignOutFocus()
    {
        // TvProfileSelectionScreen.kt keeps the utility row mounted behind TvSignOutDialog.kt.
        FocusRetry.Until(SignOutButton, () => !_active || _leaving || SignOutPrompt.IsOpen
            || PickerPanel.Visibility != Visibility.Visible || SignOutButton.Focus(FocusState.Programmatic));
    }

    private void OnChangeServerClick(object sender, RoutedEventArgs e)
    {
        if (IsProfilePreview) { ShowError("Profile preview; signed-in server unchanged."); return; }
        if (!_leaving && !_deleting) Frame.Navigate(typeof(ServerListPage));
    }

    private async Task LeaveServerAsync(bool removeServer = false)
    {
        if (_leaving || IsProfilePreview)
        {
            return;
        }
        _leaving = true;
        _loading?.Cancel();
        await Session.SignOutAsync(removeServer);
        if (_active && Session.Client is null)
        {
            // TvAppNavigation.kt: keep → Login, remove → resolve remaining server or ServerSetup.
            Frame.Navigate(typeof(MainPage));
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
