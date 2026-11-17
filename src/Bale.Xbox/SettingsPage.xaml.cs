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
    private bool _autoPlayBusy;
    private CancellationTokenSource? _autoPlayRead;

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
        var visible = SettingsCategories.Visible(SettingsSupport.Bale);
        GeneralCategory.Visibility = visible.Contains(SettingsCategory.General) ? Visibility.Visible : Visibility.Collapsed;
        PlaybackCategory.Visibility = visible.Contains(SettingsCategory.Playback) ? Visibility.Visible : Visibility.Collapsed;
        ServerCategory.Visibility = visible.Contains(SettingsCategory.Server) ? Visibility.Visible : Visibility.Collapsed;
        SelectCategory(SettingsCategories.Resolve(_category, SettingsSupport.Bale));
        PlaybackError.Visibility = Visibility.Collapsed;
        UpdateAutoPlay();
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
            _ = ReadAutoPlayAsync(client, _profileId);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _active = false;
        _accountRead?.Cancel();
        _autoPlayRead?.Cancel();
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
        PlaybackCategory.ApplyTemplate();
        VisualStateManager.GoToState(PlaybackCategory, "PlaybackIcon", false);
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
        var playback = category == SettingsCategory.Playback;
        var server = category == SettingsCategory.Server;
        GeneralCategory.IsChecked = general;
        PlaybackCategory.IsChecked = playback;
        ServerCategory.IsChecked = server;
        GeneralPane.Visibility = GeneralHeaderIcon.Visibility = general ? Visibility.Visible : Visibility.Collapsed;
        PlaybackPane.Visibility = PlaybackHeaderIcon.Visibility = playback ? Visibility.Visible : Visibility.Collapsed;
        ServerPane.Visibility = ServerHeaderIcon.Visibility = server ? Visibility.Visible : Visibility.Collapsed;
        (PaneTitle.Text, PaneBlurb.Text) = category switch
        {
            SettingsCategory.Playback => ("Playback", "Episode behavior for this Xbox."),
            SettingsCategory.Server => ("Server", "The Silo server this Xbox is connected to."),
            _ => ("General", "App-level options for this Xbox."),
        };
        DetailScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private ToggleButton SelectedCategory => _category switch
    {
        SettingsCategory.Playback => PlaybackCategory,
        SettingsCategory.Server => ServerCategory,
        _ => GeneralCategory,
    };

    private Control[] DetailActions => _category switch
    {
        SettingsCategory.Playback => AutoPlayProfileButton.Visibility == Visibility.Visible ? [AutoPlayButton, AutoPlayProfileButton] : [AutoPlayButton],
        SettingsCategory.Server => [ChangeServerButton],
        _ => [ProfileSelectionButton, HomeSectionsButton],
    };

    private SettingsCategory CategoryOf(object sender) =>
        ReferenceEquals(sender, ServerCategory) ? SettingsCategory.Server
        : ReferenceEquals(sender, PlaybackCategory) ? SettingsCategory.Playback
        : SettingsCategory.General;

    private void OnCategoryFocus(object sender, RoutedEventArgs e)
    {
        SelectCategory(CategoryOf(sender));
        _returnTarget = SelectedCategory;
    }

    private void OnCategoryClick(object sender, RoutedEventArgs e)
    {
        SelectCategory(CategoryOf(sender));
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
            Control[] rail = new Control[] { AccountButton, GeneralCategory, PlaybackCategory, ServerCategory, SignOutButton }
                .Where(item => item.Visibility == Visibility.Visible).ToArray();
            if (vertical != 0)
            {
                var index = Array.FindIndex(rail, item => SettingsInput.IsWithin(focused, item));
                FocusTarget(rail[Math.Clamp(index + vertical, 0, rail.Length - 1)]);
                e.Handled = true;
            }
            else if (horizontal != 0)
            {
                if (horizontal > 0 && (SettingsInput.IsWithin(focused, GeneralCategory) || SettingsInput.IsWithin(focused, PlaybackCategory)
                    || SettingsInput.IsWithin(focused, ServerCategory))) FocusTarget(DetailActions[0]);
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

    private async Task ReadAutoPlayAsync(SiloClient client, string? profileId)
    {
        _autoPlayRead?.Cancel();
        using var read = new CancellationTokenSource();
        _autoPlayRead = read;
        try
        {
            await client.Settings.RefreshAsync(read.Token);
            if (OwnsSettings && !read.IsCancellationRequested && client.ProfileId == profileId) UpdateAutoPlay();
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested) { }
        catch (Exception error) { Log.Error("Settings auto-play read", error); }
        finally { if (ReferenceEquals(_autoPlayRead, read)) _autoPlayRead = null; }
    }

    private void UpdateAutoPlay()
    {
        var setting = _client?.Settings.AutoPlayNext ?? new BooleanSetting(true);
        AutoPlayValue.Text = setting.Value ? "On" : "Off";
        AutoPlayDescription.Text = (setting.Value
            ? "When an episode ends, the next one plays after a short countdown."
            : "When an episode ends, Up Next waits for you to choose the next episode.")
            + (setting.DeviceOverride ? " This Xbox uses its own setting." : string.Empty);
        var profileShown = setting.DeviceOverride;
        if (!profileShown && SettingsInput.IsWithin(FocusManager.GetFocusedElement() as DependencyObject, AutoPlayProfileButton))
            FocusTarget(AutoPlayButton);
        AutoPlayProfileButton.Visibility = profileShown ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>TvSettingsScreen.kt: the switch stores a value for this device (scope profile_device).</summary>
    private async void OnAutoPlayClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings || _autoPlayBusy || _client is not { } client) return;
        _returnTarget = AutoPlayButton;
        await ChangeAutoPlayAsync(client, () => client.Settings.SetAutoPlayNextAsync(!client.Settings.AutoPlayNext.Value));
    }

    /// <summary>"Use profile setting": delete this device's value so the profile's applies again.</summary>
    private async void OnAutoPlayProfileClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings || _autoPlayBusy || _client is not { } client) return;
        FocusTarget(AutoPlayButton);
        await ChangeAutoPlayAsync(client, () => client.Settings.UseProfileSettingAsync());
    }

    private async Task ChangeAutoPlayAsync(SiloClient client, Func<Task<BooleanSetting>> change)
    {
        _autoPlayBusy = true;
        _autoPlayRead?.Cancel();
        var profileId = _profileId;
        try
        {
            await change();
            if (OwnsSettings && client.ProfileId == profileId) PlaybackError.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            Log.Error("Settings auto-play write", error);
            if (OwnsSettings)
            {
                PlaybackError.Text = "Bale couldn’t save Auto-Play on the server. Try again.";
                PlaybackError.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _autoPlayBusy = false;
            if (OwnsSettings && client.ProfileId == profileId) UpdateAutoPlay();
        }
    }

    private void OnSignOutFocus(object sender, RoutedEventArgs e)
    {
        // TvSettingsScreen.kt hides the category selection while the bottom rail action owns focus.
        GeneralCategory.IsChecked = PlaybackCategory.IsChecked = ServerCategory.IsChecked = false;
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
