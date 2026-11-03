using Bale.Core;
using Bale.Core.Settings;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>TvSettingsScreen.kt: focus-selected category rail, detail pane, then Back to Home.</summary>
public sealed partial class SettingsPage : Page, IBackHandler, IRestoresFocus
{
    private SettingsCategory _category = SettingsCategory.General;
    private Control? _returnTarget;
    private SiloClient? _client;
    private string? _profileId;
    private CancellationTokenSource? _accountRead;
    private UIElement? _focusRoot;
    private bool _active;
    private bool _backPressed;
    private bool _signingOut;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => DetachFocusBoundary();
        SignOutPrompt.SignOutRequested += OnSignOutRequested;
        SignOutPrompt.Dismissed += () => RestoreFocus();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _active = true;
        _backPressed = _signingOut = false;
        _client = Session.Client;
        _profileId = _client?.ProfileId;
        if (e.NavigationMode != NavigationMode.Back)
        {
            _category = SettingsCategory.General;
            _returnTarget = GeneralCategory;
        }
        var visible = SettingsCategories.Visible(new SettingsSupport());
        GeneralCategory.Visibility = visible.Contains(SettingsCategory.General) ? Visibility.Visible : Visibility.Collapsed;
        ServerCategory.Visibility = visible.Contains(SettingsCategory.Server) ? Visibility.Visible : Visibility.Collapsed;
        SelectCategory(SettingsCategories.Resolve(_category, new SettingsSupport()));
        UpdateIdentity();
        UpdatePolicy();
        var version = DeviceIdentity.Current().ClientVersion;
        RailVersion.Text = "Bale " + version;
        AppVersion.Text = version;
        if (_client is { } client)
        {
            ServerName.Text = AppSettings.GetServerBranding(client.Server).ServerName ?? client.Server.Host;
            ServerAddress.Text = client.Server.GetLeftPart(UriPartial.Path);
            _ = ReadAccountAsync(client, _profileId);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _active = false;
        _accountRead?.Cancel();
        DetachFocusBoundary();
        ProfilePicker.Visibility = Visibility.Collapsed;
        SignOutPrompt.Hide();
        base.OnNavigatedFrom(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_active) return;
        ServerCategory.ApplyTemplate();
        VisualStateManager.GoToState(ServerCategory, "ServerIcon", false);
        DetachFocusBoundary();
        _focusRoot = Window.Current.Content;
        if (_focusRoot is not null) _focusRoot.GettingFocus += OnGettingFocus;
        RestoreFocus();
    }

    private void DetachFocusBoundary()
    {
        if (_focusRoot is not null) _focusRoot.GettingFocus -= OnGettingFocus;
        _focusRoot = null;
    }

    private bool OwnsSettings => _active && ReferenceEquals(Frame?.Content, this)
        && ReferenceEquals(_client, Session.Client) && _client?.ProfileId == _profileId;

    private async Task ReadAccountAsync(SiloClient client, string? profileId)
    {
        using var read = new CancellationTokenSource();
        _accountRead = read;
        try
        {
            await client.GetAccountAsync(read.Token);
            if (OwnsSettings && !read.IsCancellationRequested && client.ProfileId == profileId) UpdateIdentity();
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested) { }
        catch (Exception error) { Log.Error("Settings account", error); }
        finally { if (ReferenceEquals(_accountRead, read)) _accountRead = null; }
    }

    private void UpdateIdentity()
    {
        var identity = SettingsAccount.From(_client?.Account, Session.ProfileName, AppSettings.AccountName);
        AccountName.Text = identity.Name;
        AccountSubtitle.Text = identity.Subtitle;
        AvatarInitial.Text = identity.Name[..1].ToUpperInvariant();
        AvatarPicture.Visibility = Visibility.Collapsed;
        if (Session.ProfileAvatar is { } avatar)
        {
            AvatarPicture.Fill = new ImageBrush { ImageSource = ProfileView.AvatarImage(avatar, 34), Stretch = Stretch.UniformToFill };
            AvatarPicture.Visibility = Visibility.Visible;
        }
    }

    private void SelectCategory(SettingsCategory category)
    {
        _category = category;
        var general = category == SettingsCategory.General;
        GeneralCategory.IsChecked = general;
        ServerCategory.IsChecked = !general;
        GeneralPane.Visibility = GeneralHeaderIcon.Visibility = general ? Visibility.Visible : Visibility.Collapsed;
        ServerPane.Visibility = ServerHeaderIcon.Visibility = general ? Visibility.Collapsed : Visibility.Visible;
        PaneTitle.Text = general ? "General" : "Server";
        PaneBlurb.Text = general ? "App-level options for this Xbox." : "The Silo server this Xbox is connected to.";
        DetailScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private ToggleButton SelectedCategory => _category == SettingsCategory.General ? GeneralCategory : ServerCategory;
    private Control[] DetailActions => _category == SettingsCategory.General ? [ProfileSelectionButton, HomeSectionsButton] : [ChangeServerButton];

    private void OnCategoryFocus(object sender, RoutedEventArgs e)
    {
        SelectCategory(ReferenceEquals(sender, ServerCategory) ? SettingsCategory.Server : SettingsCategory.General);
        _returnTarget = SelectedCategory;
    }

    private void OnCategoryClick(object sender, RoutedEventArgs e)
    {
        SelectCategory(ReferenceEquals(sender, ServerCategory) ? SettingsCategory.Server : SettingsCategory.General);
        FocusTarget(DetailActions[0]);
    }

    private void FocusTarget(Control target)
    {
        _returnTarget = target;
        FocusRetry.Until(this, () => !OwnsSettings || (target.Focus(FocusState.Programmatic) && target.FocusState != FocusState.Unfocused));
    }

    public void RestoreFocus()
    {
        if (OwnsSettings) FocusTarget(_returnTarget ?? SelectedCategory);
    }

    public bool OnBack()
    {
        if (SignOutPrompt.Dismiss()) return true;
        if (ProfilePicker.Visibility == Visibility.Visible)
        {
            ClosePicker();
            return true;
        }
        // TvSettingsScreen.kt's BackHandler: detail -> category; category/account/sign-out -> Home.
        if (SettingsInput.IsWithin(FocusManager.GetFocusedElement() as DependencyObject, DetailPane)) FocusTarget(SelectedCategory);
        else if (Frame.CanGoBack) Frame.GoBack();
        return true;
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!OwnsSettings || SignOutPrompt.IsOpen) return;
        if (SettingsInput.IsBack(e.Key))
        {
            _backPressed = true;
            e.Handled = true;
            return;
        }
        var vertical = SettingsInput.VerticalStep(e.Key);
        var horizontal = SettingsInput.HorizontalStep(e.Key);
        var focused = FocusManager.GetFocusedElement() as DependencyObject;
        if (ProfilePicker.Visibility == Visibility.Visible)
        {
            if (vertical != 0)
            {
                (vertical < 0 ? AutomaticButton : EveryTimeButton).Focus(FocusState.Keyboard);
                e.Handled = true;
            }
            if (horizontal != 0 || e.Key == VirtualKey.Tab) e.Handled = true;
            return;
        }
        if (SettingsInput.IsWithin(focused, CategoryRail))
        {
            Control[] rail = [AccountButton, GeneralCategory, ServerCategory, SignOutButton];
            if (vertical != 0)
            {
                var index = Array.FindIndex(rail, item => SettingsInput.IsWithin(focused, item));
                FocusTarget(rail[Math.Clamp(index + vertical, 0, rail.Length - 1)]);
                e.Handled = true;
            }
            else if (horizontal != 0)
            {
                if (horizontal > 0 && (SettingsInput.IsWithin(focused, GeneralCategory) || SettingsInput.IsWithin(focused, ServerCategory))) FocusTarget(DetailActions[0]);
                e.Handled = true;
            }
        }
        else if (SettingsInput.IsWithin(focused, DetailPane))
        {
            if (horizontal < 0) FocusTarget(SelectedCategory);
            if (vertical != 0)
            {
                var actions = DetailActions;
                var index = Array.FindIndex(actions, item => SettingsInput.IsWithin(focused, item));
                FocusTarget(actions[Math.Clamp(index + vertical, 0, actions.Length - 1)]);
            }
            if (horizontal != 0 || vertical != 0) e.Handled = true;
        }
        if (e.Key == VirtualKey.Tab) e.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (SignOutPrompt.IsOpen || !SettingsInput.IsBack(e.Key)) return;
        e.Handled = true;
        if (!_backPressed) return;
        _backPressed = false;
        OnBack();
    }

    private void OnGettingFocus(UIElement sender, GettingFocusEventArgs e)
    {
        try
        {
            if (OwnsSettings && ProfilePicker.Visibility == Visibility.Visible && !SettingsInput.IsWithin(e.NewFocusedElement, ProfilePicker))
            {
                var selected = AppSettings.OpenProfileAutomatically ? AutomaticButton : EveryTimeButton;
                if (!e.TrySetNewFocusedElement(selected)) e.Cancel = true;
            }
        }
        catch (Exception error) { Log.Error("Settings picker focus", error); }
    }

    private void OnAccountClick(object sender, RoutedEventArgs e)
    {
        if (OwnsSettings) Session.ShowProfilePicker(Frame);
    }

    private void OnHomeSectionsClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings) return;
        _returnTarget = HomeSectionsButton;
        Frame.Navigate(typeof(HomeSectionsPage));
    }

    private void UpdatePolicy()
    {
        ProfileSelectionValue.Text = AppSettings.OpenProfileAutomatically ? "Automatic" : "Every Time";
        ProfileSelectionDescription.Text = AppSettings.OpenProfileAutomatically
            ? "Use the last selected profile when Bale launches. A protected profile can reopen without its PIN."
            : "Show Who's Watching each time Bale launches.";
        AutomaticButton.ApplyTemplate();
        EveryTimeButton.ApplyTemplate();
        VisualStateManager.GoToState(AutomaticButton, AppSettings.OpenProfileAutomatically ? "Selected" : "Unselected", false);
        VisualStateManager.GoToState(EveryTimeButton, AppSettings.OpenProfileAutomatically ? "Unselected" : "Selected", false);
    }

    private void OnProfileSelectionClick(object sender, RoutedEventArgs e)
    {
        UpdatePolicy();
        _returnTarget = ProfileSelectionButton;
        ProfilePicker.Visibility = Visibility.Visible;
        var selected = AppSettings.OpenProfileAutomatically ? AutomaticButton : EveryTimeButton;
        FocusRetry.Until(this, () => !OwnsSettings || ProfilePicker.Visibility != Visibility.Visible || selected.Focus(FocusState.Programmatic));
    }

    private void ClosePicker()
    {
        ProfilePicker.Visibility = Visibility.Collapsed;
        FocusTarget(ProfileSelectionButton);
    }

    private void OnPolicyClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings) return;
        try
        {
            // ProfileLaunchPreferences.kt: only automatic/askEveryLaunch have an honored Bale policy.
            AppSettings.OpenProfileAutomatically = ReferenceEquals(sender, AutomaticButton);
            AppSettings.ProfileRememberOfferAnswered = true;
            SettingsError.Visibility = Visibility.Collapsed;
            UpdatePolicy();
        }
        catch (Exception error)
        {
            Log.Error("Save profile launch policy", error);
            SettingsError.Text = "Bale couldn’t save the profile preference on this Xbox. Try again.";
            SettingsError.Visibility = Visibility.Visible;
        }
        ClosePicker();
    }

    private void OnSignOutFocus(object sender, RoutedEventArgs e)
    {
        // TvSettingsScreen.kt hides the category selection while the bottom rail action owns focus.
        GeneralCategory.IsChecked = ServerCategory.IsChecked = false;
    }

    private void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings || _signingOut) return;
        _returnTarget = SignOutButton;
        SignOutPrompt.Show();
    }

    private void OnChangeServerClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings || _signingOut) return;
        _returnTarget = ChangeServerButton;
        Frame.Navigate(typeof(ServerListPage));
    }

    private async void OnSignOutRequested(bool removeServer)
    {
        if (!OwnsSettings || _signingOut) return;
        _signingOut = true;
        await Session.SignOutAsync(removeServer);
        if (_active && ReferenceEquals(Frame.Content, this) && Session.Client is null)
        {
            Frame.Navigate(typeof(MainPage));
            Frame.BackStack.Clear();
        }
    }
}
