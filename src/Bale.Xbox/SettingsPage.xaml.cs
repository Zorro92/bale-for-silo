using System.Globalization;
using Bale.Core;
using Bale.Core.Settings;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.System;
using Windows.UI.Core;
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
    // Navigated to anew (not Back): the pane opens at its top once loaded.
    private bool _newVisit;
    private bool _backPressed;
    private bool _signingOut;
    private bool _autoPlayBusy;
    private CancellationTokenSource? _autoPlayRead;
    // Show Next Up / Skip Intros / Skip Credits (TvSettingsScreen.kt:1395, :1403, :1408): one write at a time, as for Auto-Play.
    private bool _episodeBusy;
    // Settings › Subtitles (TvSettingsScreen.kt TvSubtitleSettingsPane): one write at a time; the open sheet's picker and row.
    private bool _subtitleBusy;
    private CancellationTokenSource? _subtitleRead;
    private SubtitlePicker? _subtitlePicker;
    private Control? _subtitlePickerRow;
    // Settings → Playback → Streaming / Connection (TvSettingsScreen.kt, TvConnectionSettings.kt).
    private readonly DispatcherTimer _connectionClock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _testing;
    private bool _resetBusy;
    private bool _resetFailed;
    private bool _modeBusy;
    private ConnectionSpeedNotice? _notice;

    public SettingsPage()
    {
        InitializeComponent();
        BufferingQualityLabel.Text = BufferingPickerTitle.Text = BufferingQualityModes.Title;
        BufferingQualityDescription.Text = BufferingQualityModes.Description;
        LearnedLimitValue.Text = ConnectionSettings.Reset;
        LearnedLimitError.Text = ConnectionSettings.ResetFailed;
        ConnectionSpeedLabel.Text = ConnectionSpeedInfoLabel.Text = ConnectionSettings.SpeedLabel;
        ConnectionSkipped.Text = ConnectionSettings.SkippedMetered;
        BufferingProfileOptionText.Text = ConnectionSettings.UseProfileSetting;
        BufferingNeverOptionText.Text = BufferingQualityModes.Label(BufferingQualityMode.Never);
        BufferingAskOptionText.Text = BufferingQualityModes.Label(BufferingQualityMode.Ask);
        BufferingAlwaysOptionText.Text = BufferingQualityModes.Label(BufferingQualityMode.Always);
        NextUpLabel.Text = NextUpPickerTitle.Text = NextUpPromptChoices.Title;
        NextUpProfileOptionText.Text = ConnectionSettings.UseProfileSetting;
        // One label for the row's value and each option (NextUpPromptChoices.Label); an option's Tag is its id.
        foreach (var (option, label) in new (Button, TextBlock)[]
        {
            (NextUpAtEndOption, NextUpAtEndOptionText), (NextUp10Option, NextUp10OptionText), (NextUp30Option, NextUp30OptionText),
            (NextUp60Option, NextUp60OptionText), (NextUp120Option, NextUp120OptionText),
        })
            label.Text = NextUpPromptChoices.FromId(option.Tag as string) is { } seconds ? NextUpPromptChoices.Label(seconds) : string.Empty;
        IntroSkipLabel.Text = IntroSkipPickerTitle.Text = IntroSkipModes.Title;
        IntroSkipProfileOptionText.Text = ConnectionSettings.UseProfileSetting;
        IntroSkipNeverOptionText.Text = IntroSkipModes.Label(IntroSkipMode.Never);
        IntroSkipAskOptionText.Text = IntroSkipModes.Label(IntroSkipMode.Ask);
        IntroSkipAlwaysOptionText.Text = IntroSkipModes.Label(IntroSkipMode.Always);
        // One label for the row's value and each option (SdrBrightness.Label); an option's Tag is its nits.
        foreach (var (option, label) in new (Button, TextBlock)[]
        {
            (SdrBrightnessDefaultOption, SdrBrightnessDefaultOptionText), (SdrBrightness80Option, SdrBrightness80OptionText),
            (SdrBrightness120Option, SdrBrightness120OptionText), (SdrBrightness150Option, SdrBrightness150OptionText),
            (SdrBrightness200Option, SdrBrightness200OptionText), (SdrBrightness250Option, SdrBrightness250OptionText),
            (SdrBrightness300Option, SdrBrightness300OptionText),
        })
            label.Text = SdrBrightness.Label(SdrBrightnessNits(option));
        // A minute clock for the speed line's age and a second clock for a 429 countdown, in one.
        _connectionClock.Tick += (_, _) =>
        {
            if (OwnsSettings && _category == SettingsCategory.Playback) UpdateConnection();
        };
        Loaded += OnLoaded;
        Unloaded += (_, _) => DetachFocusBoundary();
        SignOutPrompt.SignOutRequested += OnSignOutRequested;
        SignOutPrompt.Dismissed += () => RestoreFocus();
        OptionSheet.Chosen += OnSubtitleOptionChosen;
        ResetPrompt.Confirmed += OnResetAppearanceConfirmed;
        ResetPrompt.Dismissed += () => FocusTarget(ResetAppearanceButton);
        SettingsScroll.OwnBringIntoView(DetailContent);
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
            _newVisit = true;
        }
        var visible = SettingsCategories.Visible(SettingsSupport.Bale);
        GeneralCategory.Visibility = visible.Contains(SettingsCategory.General) ? Visibility.Visible : Visibility.Collapsed;
        PlaybackCategory.Visibility = visible.Contains(SettingsCategory.Playback) ? Visibility.Visible : Visibility.Collapsed;
        SubtitlesCategory.Visibility = visible.Contains(SettingsCategory.Subtitles) ? Visibility.Visible : Visibility.Collapsed;
        ServerCategory.Visibility = visible.Contains(SettingsCategory.Server) ? Visibility.Visible : Visibility.Collapsed;
        SelectCategory(SettingsCategories.Resolve(_category, SettingsSupport.Bale));
        PlaybackError.Visibility = Visibility.Collapsed;
        SubtitlesError.Visibility = Visibility.Collapsed;
        _notice = null;
        _resetFailed = false;
        ConnectionSpeedService.Changed -= OnConnectionChanged;
        ConnectionSpeedService.Changed += OnConnectionChanged;
        _connectionClock.Start();
        UpdateAutoPlay();
        UpdateEpisodeSettings();
        UpdateSdrBrightness();
        UpdateConnection();
        UpdateSubtitles();
        UpdateIdentity();
        UpdatePolicy();
        UpdateShowAudiobooks();
        var version = DeviceIdentity.Current().ClientVersion;
        RailVersion.Text = "Bale " + version;
        AppVersion.Text = version;
        if (_client is { } client)
        {
            ServerName.Text = AppSettings.GetServerBranding(client.Server).ServerName ?? client.Server.Host;
            ServerAddress.Text = client.Server.GetLeftPart(UriPartial.Path);
            _ = ReadAccountAsync(client, _profileId);
            _ = ReadAutoPlayAsync(client, _profileId);
            _ = ReadSubtitlesAsync(client, _profileId);
            _ = ReadConnectionAsync(client, _profileId);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _active = false;
        _accountRead?.Cancel();
        _autoPlayRead?.Cancel();
        _subtitleRead?.Cancel();
        ConnectionSpeedService.Changed -= OnConnectionChanged;
        _connectionClock.Stop();
        DetachFocusBoundary();
        OptionSheet.Hide();
        _subtitlePicker = null;
        ResetPrompt.Hide();
        ProfilePicker.Visibility = Visibility.Collapsed;
        BufferingPicker.Visibility = Visibility.Collapsed;
        NextUpPicker.Visibility = Visibility.Collapsed;
        IntroSkipPicker.Visibility = Visibility.Collapsed;
        SdrBrightnessPicker.Visibility = Visibility.Collapsed;
        SignOutPrompt.Hide();
        base.OnNavigatedFrom(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_active) return;
        // A new visit composes the pane afresh in TvSettingsScreen.kt, at its top; Back finds it where it was. Done once the
        // cached page is back in the tree, where its scroller takes the change.
        if (_newVisit) DetailScroller.ChangeView(null, 0, null, disableAnimation: true);
        _newVisit = false;
        ServerCategory.ApplyTemplate();
        VisualStateManager.GoToState(ServerCategory, "ServerIcon", false);
        PlaybackCategory.ApplyTemplate();
        VisualStateManager.GoToState(PlaybackCategory, "PlaybackIcon", false);
        SubtitlesCategory.ApplyTemplate();
        VisualStateManager.GoToState(SubtitlesCategory, "SubtitlesIcon", false);
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

    /// <summary>
    /// The rail's categories (TvSettingsCategory) and their rows. Selecting a category checks its row and unchecks every
    /// other one, and Sign Out unchecks them all, both through this list: a row left out would keep its selected platter.
    /// </summary>
    private (SettingsCategory Category, ToggleButton Row)[] Categories =>
    [
        (SettingsCategory.General, GeneralCategory), (SettingsCategory.Playback, PlaybackCategory),
        (SettingsCategory.Subtitles, SubtitlesCategory), (SettingsCategory.Server, ServerCategory),
    ];

    private void SelectCategory(SettingsCategory category)
    {
        var changed = category != _category;
        _category = category;
        var general = category == SettingsCategory.General;
        var playback = category == SettingsCategory.Playback;
        var subtitles = category == SettingsCategory.Subtitles;
        var server = category == SettingsCategory.Server;
        // TvSettingsRailCategoryRow: only the selected category keeps the selected platter.
        foreach (var (each, row) in Categories) row.IsChecked = each == category;
        GeneralPane.Visibility = GeneralHeaderIcon.Visibility = general ? Visibility.Visible : Visibility.Collapsed;
        PlaybackPane.Visibility = PlaybackHeaderIcon.Visibility = playback ? Visibility.Visible : Visibility.Collapsed;
        SubtitlesPane.Visibility = SubtitlesHeaderIcon.Visibility = subtitles ? Visibility.Visible : Visibility.Collapsed;
        ServerPane.Visibility = ServerHeaderIcon.Visibility = server ? Visibility.Visible : Visibility.Collapsed;
        (PaneTitle.Text, PaneBlurb.Text) = category switch
        {
            SettingsCategory.Playback => ("Playback", "Streaming quality and episode behavior for this Xbox."),
            // TvSettingsCategory.Subtitles: settings_subtitles, settings_blurb_subtitles.
            SettingsCategory.Subtitles => ("Subtitles", "Language, behavior, and on-screen appearance."),
            SettingsCategory.Server => ("Server", "The Silo server this Xbox is connected to."),
            _ => ("General", "App-level options for this Xbox."),
        };
        // TvSettingsScreen.kt composes a newly selected category's pane at its top; refocusing the selected category (Left
        // or Back from its rows) leaves the pane where it was.
        if (changed) DetailScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private ToggleButton SelectedCategory => _category switch
    {
        SettingsCategory.Playback => PlaybackCategory,
        SettingsCategory.Subtitles => SubtitlesCategory,
        SettingsCategory.Server => ServerCategory,
        _ => GeneralCategory,
    };

    private Control[] DetailActions => _category switch
    {
        SettingsCategory.Playback => new Control[]
            {
                BufferingQualityButton, LearnedLimitButton, ConnectionSpeedButton, AutoPlayButton, AutoPlayProfileButton,
                NextUpButton, IntroSkipButton, SkipCreditsButton, SkipCreditsProfileButton, SdrBrightnessButton,
            }.Where(item => item.Visibility == Visibility.Visible).ToArray(),
        SettingsCategory.Subtitles => new Control[]
            {
                SubtitleLanguageButton, SubtitleModeButton, ForcedSubtitlesButton, MatchDeviceButton, DeviceOnlyButton, FontSizeButton,
                FontFamilyButton, FontColorButton, TextOpacityButton, TextOutlineButton, OutlineColorButton, BackgroundStyleButton,
                BackgroundOpacityButton, BackgroundColorButton, PositionButton, ResetAppearanceButton,
            }.Where(item => item.Visibility == Visibility.Visible).ToArray(),
        SettingsCategory.Server => [AccountsButton],
        _ => [ProfileSelectionButton, HomeSectionsButton, ShowAudiobooksButton],
    };

    private SettingsCategory CategoryOf(object sender) =>
        ReferenceEquals(sender, ServerCategory) ? SettingsCategory.Server
        : ReferenceEquals(sender, PlaybackCategory) ? SettingsCategory.Playback
        : ReferenceEquals(sender, SubtitlesCategory) ? SettingsCategory.Subtitles
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

    /// <summary>
    /// TvSettingsScreen.kt's panes glide a row that takes focus to Compose's TV pivot (SettingsFocusScroll), every pane
    /// alike. XAML scrolls a ScrollViewer for keyboard focus only, and the page moves focus itself (FocusTarget's
    /// Focus(Programmatic)), so the pane scrolls here whichever way the row got focus; a row not laid out yet (a pane just
    /// switched in) is revealed on a later layout while it keeps focus.
    /// </summary>
    private void OnDetailFocus(object sender, RoutedEventArgs e)
    {
        // GotFocus arrives after the move: reveal what holds focus now, if it is still in the pane.
        if (FocusManager.GetFocusedElement() is not FrameworkElement row || !SettingsInput.IsWithin(row, DetailContent)) return;
        FocusRetry.Until(this, () => !OwnsSettings || !ReferenceEquals(FocusManager.GetFocusedElement(), row)
            || SettingsScroll.Reveal(DetailScroller, DetailContent, row, animate: true));
    }

    public void RestoreFocus()
    {
        if (OwnsSettings) FocusTarget(_returnTarget ?? SelectedCategory);
    }

    public bool OnBack()
    {
        if (SignOutPrompt.Dismiss()) return true;
        if (ResetPrompt.Dismiss()) return true;
        if (OptionSheet.IsOpen)
        {
            CloseSubtitlePicker();
            return true;
        }
        if (BufferingPicker.Visibility == Visibility.Visible)
        {
            CloseBufferingPicker();
            return true;
        }
        if (NextUpPicker.Visibility == Visibility.Visible)
        {
            CloseNextUpPicker();
            return true;
        }
        if (IntroSkipPicker.Visibility == Visibility.Visible)
        {
            CloseIntroSkipPicker();
            return true;
        }
        if (SdrBrightnessPicker.Visibility == Visibility.Visible)
        {
            CloseSdrBrightnessPicker();
            return true;
        }
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
        if (!OwnsSettings || SignOutPrompt.IsOpen || ResetPrompt.IsOpen) return;
        if (SettingsInput.IsBack(e.Key))
        {
            _backPressed = true;
            e.Handled = true;
            return;
        }
        var vertical = SettingsInput.VerticalStep(e.Key);
        var horizontal = SettingsInput.HorizontalStep(e.Key);
        var focused = FocusManager.GetFocusedElement() as DependencyObject;
        if (OptionSheet.IsOpen)
        {
            if (vertical != 0)
            {
                OptionSheet.Move(vertical);
                e.Handled = true;
            }
            if (horizontal != 0 || e.Key == VirtualKey.Tab) e.Handled = true;
            return;
        }
        if (BufferingPicker.Visibility == Visibility.Visible)
        {
            if (vertical != 0)
            {
                var options = BufferingOptions;
                var index = Array.FindIndex(options, option => SettingsInput.IsWithin(focused, option));
                options[Math.Clamp(index + vertical, 0, options.Length - 1)].Focus(FocusState.Keyboard);
                e.Handled = true;
            }
            if (horizontal != 0 || e.Key == VirtualKey.Tab) e.Handled = true;
            return;
        }
        if (NextUpPicker.Visibility == Visibility.Visible)
        {
            if (vertical != 0)
            {
                var options = NextUpOptions;
                var index = Array.FindIndex(options, option => SettingsInput.IsWithin(focused, option));
                options[Math.Clamp(index + vertical, 0, options.Length - 1)].Focus(FocusState.Keyboard);
                e.Handled = true;
            }
            if (horizontal != 0 || e.Key == VirtualKey.Tab) e.Handled = true;
            return;
        }
        if (IntroSkipPicker.Visibility == Visibility.Visible)
        {
            if (vertical != 0)
            {
                var options = IntroSkipOptions;
                var index = Array.FindIndex(options, option => SettingsInput.IsWithin(focused, option));
                options[Math.Clamp(index + vertical, 0, options.Length - 1)].Focus(FocusState.Keyboard);
                e.Handled = true;
            }
            if (horizontal != 0 || e.Key == VirtualKey.Tab) e.Handled = true;
            return;
        }
        if (SdrBrightnessPicker.Visibility == Visibility.Visible)
        {
            if (vertical != 0)
            {
                var options = SdrBrightnessOptions;
                var index = Array.FindIndex(options, option => SettingsInput.IsWithin(focused, option));
                options[Math.Clamp(index + vertical, 0, options.Length - 1)].Focus(FocusState.Keyboard);
                e.Handled = true;
            }
            if (horizontal != 0 || e.Key == VirtualKey.Tab) e.Handled = true;
            return;
        }
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
            Control[] rail = new Control[] { AccountButton, GeneralCategory, PlaybackCategory, SubtitlesCategory, ServerCategory, SignOutButton }
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
                    || SettingsInput.IsWithin(focused, SubtitlesCategory) || SettingsInput.IsWithin(focused, ServerCategory))) FocusTarget(DetailActions[0]);
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
        if (SignOutPrompt.IsOpen || ResetPrompt.IsOpen || !SettingsInput.IsBack(e.Key)) return;
        e.Handled = true;
        if (!_backPressed) return;
        _backPressed = false;
        OnBack();
    }

    private void OnGettingFocus(UIElement sender, GettingFocusEventArgs e)
    {
        try
        {
            if (OwnsSettings && OptionSheet.IsOpen && !SettingsInput.IsWithin(e.NewFocusedElement, OptionSheet))
            {
                if (OptionSheet.FocusTarget is not { } option || !e.TrySetNewFocusedElement(option)) e.Cancel = true;
            }
            else if (OwnsSettings && BufferingPicker.Visibility == Visibility.Visible && !SettingsInput.IsWithin(e.NewFocusedElement, BufferingPicker))
            {
                if (!e.TrySetNewFocusedElement(SelectedBufferingOption)) e.Cancel = true;
            }
            else if (OwnsSettings && NextUpPicker.Visibility == Visibility.Visible && !SettingsInput.IsWithin(e.NewFocusedElement, NextUpPicker))
            {
                if (!e.TrySetNewFocusedElement(NextUpFocusOption)) e.Cancel = true;
            }
            else if (OwnsSettings && IntroSkipPicker.Visibility == Visibility.Visible && !SettingsInput.IsWithin(e.NewFocusedElement, IntroSkipPicker))
            {
                if (!e.TrySetNewFocusedElement(SelectedIntroSkipOption)) e.Cancel = true;
            }
            else if (OwnsSettings && SdrBrightnessPicker.Visibility == Visibility.Visible && !SettingsInput.IsWithin(e.NewFocusedElement, SdrBrightnessPicker))
            {
                if (!e.TrySetNewFocusedElement(SelectedSdrBrightnessOption)) e.Cancel = true;
            }
            else if (OwnsSettings && ProfilePicker.Visibility == Visibility.Visible && !SettingsInput.IsWithin(e.NewFocusedElement, ProfilePicker))
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

    private void UpdateShowAudiobooks() => ShowAudiobooksValue.Text = TopMenuSettings.ShowAudiobooks(_client) ? "On" : "Off";

    /// <summary>
    /// TvSettingsScreen.kt:1130 Show Audiobooks (TvSettingsViewModel.kt onShowAudiobooksTabChanged): stored on this Xbox for
    /// this server and profile; the top menu takes it on the way back to Home (TvMainShell.kt re-reads it per route).
    /// </summary>
    private void OnShowAudiobooksClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings || _client is not { } client || _profileId is not { } profileId) return;
        _returnTarget = ShowAudiobooksButton;
        if (TopMenuSettings.SetShowAudiobooks(client, profileId, !TopMenuSettings.ShowAudiobooks(client)))
        {
            SettingsError.Visibility = Visibility.Collapsed;
        }
        else
        {
            SettingsError.Text = "Bale couldn’t save the top menu preference on this Xbox. Try again.";
            SettingsError.Visibility = Visibility.Visible;
        }
        UpdateShowAudiobooks();
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
            if (OwnsSettings && !read.IsCancellationRequested && client.ProfileId == profileId)
            {
                UpdateAutoPlay();
                UpdateEpisodeSettings();
                UpdateConnection();
            }
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

    private void UpdateSdrBrightness() => SdrBrightnessValue.Text = SdrBrightness.Label(AppSettings.SdrWhiteNits);

    private Button[] SdrBrightnessOptions =>
    [
        SdrBrightnessDefaultOption, SdrBrightness80Option, SdrBrightness120Option, SdrBrightness150Option, SdrBrightness200Option,
        SdrBrightness250Option, SdrBrightness300Option,
    ];

    /// <summary>An option's SDR white: its Tag in nits; anything that isn't a choice reads as Default.</summary>
    private static int SdrBrightnessNits(object? option) =>
        int.TryParse((option as FrameworkElement)?.Tag as string, NumberStyles.None, CultureInfo.InvariantCulture, out var nits)
            ? SdrBrightness.FromStored(nits) : SdrBrightness.DefaultNits;

    /// <summary>The checked option: this Xbox's stored white, Default while none (or an unknown value) is stored.</summary>
    private Button SelectedSdrBrightnessOption
    {
        get
        {
            var nits = AppSettings.SdrWhiteNits;
            return SdrBrightnessOptions.FirstOrDefault(option => SdrBrightnessNits(option) == nits) ?? SdrBrightnessDefaultOption;
        }
    }

    /// <summary>The Buffering Quality picker's pattern: the checked option takes focus, Back or a choice returns to the row.</summary>
    private void OnSdrBrightnessClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings) return;
        _returnTarget = SdrBrightnessButton;
        var selected = SelectedSdrBrightnessOption;
        foreach (var option in SdrBrightnessOptions)
        {
            option.ApplyTemplate();
            VisualStateManager.GoToState(option, ReferenceEquals(option, selected) ? "Selected" : "Unselected", false);
        }
        SdrBrightnessPicker.Visibility = Visibility.Visible;
        FocusRetry.Until(this, () => !OwnsSettings || SdrBrightnessPicker.Visibility != Visibility.Visible || selected.Focus(FocusState.Programmatic));
    }

    private void CloseSdrBrightnessPicker()
    {
        SdrBrightnessPicker.Visibility = Visibility.Collapsed;
        FocusTarget(SdrBrightnessButton);
    }

    /// <summary>A choice is stored on this Xbox only (no server value); the next playback attempt reads it.</summary>
    private void OnSdrBrightnessOptionClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings) return;
        try
        {
            AppSettings.SdrWhiteNits = SdrBrightnessNits(sender);
            PlaybackError.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            Log.Error("Settings SDR brightness write", error);
            PlaybackError.Text = "Bale couldn’t save the SDR brightness preference on this Xbox. Try again.";
            PlaybackError.Visibility = Visibility.Visible;
        }
        UpdateSdrBrightness();
        CloseSdrBrightnessPicker();
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
            if (OwnsSettings && client.ProfileId == profileId)
            {
                UpdateAutoPlay();
                UpdateEpisodeSettings();
                UpdateConnection();
            }
        }
    }

    /// <summary>
    /// TvSettingsScreen.kt:1395/:1403/:1408: Show Next Up and Skip Intros show the effective value's label, Skip Credits
    /// On/Off, and Skip Credits' "Use Profile Setting" row only while this Xbox has its own value (focus moves up to the
    /// switch when it leaves).
    /// </summary>
    private void UpdateEpisodeSettings()
    {
        NextUpValue.Text = NextUpPromptChoices.Label(_client?.Settings.NextUpPromptSeconds.Value ?? NextUpPromptChoices.Default);
        IntroSkipValue.Text = IntroSkipModes.Label(_client?.Settings.IntroSkip.Value ?? IntroSkipModes.Default);
        var credits = _client?.Settings.AutoSkipCredits ?? new BooleanSetting(false);
        SkipCreditsValue.Text = credits.Value ? "On" : "Off";
        if (!credits.DeviceOverride && SettingsInput.IsWithin(FocusManager.GetFocusedElement() as DependencyObject, SkipCreditsProfileButton))
            FocusTarget(SkipCreditsButton);
        SkipCreditsProfileButton.Visibility = credits.DeviceOverride ? Visibility.Visible : Visibility.Collapsed;
    }

    private Button[] NextUpOptions => [NextUpProfileOption, NextUpAtEndOption, NextUp10Option, NextUp30Option, NextUp60Option, NextUp120Option];

    /// <summary>The checked option (NextUpPromptChoices.SelectedId); none for a device value that is not a choice.</summary>
    private Button? SelectedNextUpOption
    {
        get
        {
            var selected = NextUpPromptChoices.SelectedId(_client?.Settings.NextUpPromptSeconds ?? new IntegerSetting(NextUpPromptChoices.Default));
            return NextUpOptions.FirstOrDefault(option => option.Tag as string == selected);
        }
    }

    /// <summary>TvSettingsPickerSheet: the checked option takes focus, else the first row.</summary>
    private Button NextUpFocusOption => SelectedNextUpOption ?? NextUpProfileOption;

    /// <summary>The Skip Intros picker's pattern: the checked option takes focus, Back or a choice returns to the row.</summary>
    private void OnNextUpClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings) return;
        _returnTarget = NextUpButton;
        var selected = SelectedNextUpOption;
        foreach (var option in NextUpOptions)
        {
            option.ApplyTemplate();
            VisualStateManager.GoToState(option, ReferenceEquals(option, selected) ? "Selected" : "Unselected", false);
        }
        var focus = NextUpFocusOption;
        NextUpPicker.Visibility = Visibility.Visible;
        FocusRetry.Until(this, () => !OwnsSettings || NextUpPicker.Visibility != Visibility.Visible || focus.Focus(FocusState.Programmatic));
    }

    private void CloseNextUpPicker()
    {
        NextUpPicker.Visibility = Visibility.Collapsed;
        FocusTarget(NextUpButton);
    }

    /// <summary>A lead stores this Xbox's own value (profile_device); "Use profile setting" deletes it and rereads.</summary>
    private async void OnNextUpOptionClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings || _client is not { } client) return;
        var choice = (sender as FrameworkElement)?.Tag as string;
        CloseNextUpPicker();
        await ChangeEpisodeSettingAsync(client, "Show Next Up", () => choice == NextUpPromptChoices.UseProfileId
            ? client.Settings.UseProfileNextUpPromptSecondsAsync()
            : NextUpPromptChoices.FromId(choice) is { } seconds ? client.Settings.SetNextUpPromptSecondsAsync(seconds) : Task.CompletedTask);
    }

    private Button[] IntroSkipOptions => [IntroSkipProfileOption, IntroSkipNeverOption, IntroSkipAskOption, IntroSkipAlwaysOption];

    /// <summary>"Use profile setting" is the checked choice while this Xbox has no value of its own (profileLayered).</summary>
    private Button SelectedIntroSkipOption
    {
        get
        {
            var setting = _client?.Settings.IntroSkip ?? new IntroSkipSetting();
            if (!setting.DeviceOverride) return IntroSkipProfileOption;
            return setting.Value switch
            {
                IntroSkipMode.Never => IntroSkipNeverOption,
                IntroSkipMode.Always => IntroSkipAlwaysOption,
                _ => IntroSkipAskOption,
            };
        }
    }

    /// <summary>The Buffering Quality picker's pattern: the checked option takes focus, Back or a choice returns to the row.</summary>
    private void OnIntroSkipClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings) return;
        _returnTarget = IntroSkipButton;
        var selected = SelectedIntroSkipOption;
        foreach (var option in IntroSkipOptions)
        {
            option.ApplyTemplate();
            VisualStateManager.GoToState(option, ReferenceEquals(option, selected) ? "Selected" : "Unselected", false);
        }
        IntroSkipPicker.Visibility = Visibility.Visible;
        FocusRetry.Until(this, () => !OwnsSettings || IntroSkipPicker.Visibility != Visibility.Visible || selected.Focus(FocusState.Programmatic));
    }

    private void CloseIntroSkipPicker()
    {
        IntroSkipPicker.Visibility = Visibility.Collapsed;
        FocusTarget(IntroSkipButton);
    }

    /// <summary>A mode stores this Xbox's own value (profile_device); "Use profile setting" deletes it and rereads.</summary>
    private async void OnIntroSkipOptionClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings || _client is not { } client) return;
        var choice = (sender as FrameworkElement)?.Tag as string;
        CloseIntroSkipPicker();
        await ChangeEpisodeSettingAsync(client, "Skip Intros", () => choice == "profile"
            ? client.Settings.UseProfileIntroSkipModeAsync()
            : IntroSkipModes.FromWire(choice) is { } mode ? client.Settings.SetIntroSkipModeAsync(mode) : Task.CompletedTask);
    }

    /// <summary>TvSettingsScreen.kt SettingsToggleRow: the switch stores a value for this device (scope profile_device).</summary>
    private async void OnSkipCreditsClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings || _client is not { } client) return;
        _returnTarget = SkipCreditsButton;
        await ChangeEpisodeSettingAsync(client, "Skip Credits", () => client.Settings.SetAutoSkipCreditsAsync(!client.Settings.AutoSkipCredits.Value));
    }

    /// <summary>UseProfileSettingRow: focus goes back to the switch above before the row leaves (skipCreditsFocus).</summary>
    private async void OnSkipCreditsProfileClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings || _client is not { } client) return;
        FocusTarget(SkipCreditsButton);
        await ChangeEpisodeSettingAsync(client, "Skip Credits", () => client.Settings.UseProfileAutoSkipCreditsAsync());
    }

    private async Task ChangeEpisodeSettingAsync(SiloClient client, string name, Func<Task> change)
    {
        if (_episodeBusy) return;
        _episodeBusy = true;
        var profileId = _profileId;
        try
        {
            await change();
            if (OwnsSettings && client.ProfileId == profileId) PlaybackError.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            Log.Error("Settings " + name + " write", error);
            if (OwnsSettings && client.ProfileId == profileId)
            {
                PlaybackError.Text = "Bale couldn’t save " + name + " on the server. Try again.";
                PlaybackError.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _episodeBusy = false;
            if (OwnsSettings && client.ProfileId == profileId)
            {
                UpdateEpisodeSettings();
                UpdateAutoPlay();
                UpdateConnection();
            }
        }
    }

    /// <summary>Settings › Subtitles reads its values when Settings opens (one effective read for every row).</summary>
    private async Task ReadSubtitlesAsync(SiloClient client, string? profileId)
    {
        _subtitleRead?.Cancel();
        using var read = new CancellationTokenSource();
        _subtitleRead = read;
        try
        {
            await client.SubtitleSettings.RefreshAsync(read.Token);
            if (OwnsSettings && !read.IsCancellationRequested && client.ProfileId == profileId) UpdateSubtitles();
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested) { }
        catch (Exception error) { Log.Error("Settings subtitles read", error); }
        finally { if (ReferenceEquals(_subtitleRead, read)) _subtitleRead = null; }
    }

    /// <summary>
    /// TvSubtitleSettingsPane: every row's value, Text Opacity only where the server's contract knows it (focus moves up
    /// when it leaves), the scope footer, and the preview in the effective appearance.
    /// </summary>
    private void UpdateSubtitles()
    {
        var settings = _client?.SubtitleSettings;
        var preferences = settings?.Preferences ?? new SubtitlePreferences();
        var setting = settings?.Appearance ?? new SubtitleAppearanceSetting(SubtitleAppearance.Default);
        var appearance = setting.Value;
        SubtitleLanguageValue.Text = SettingsLanguages.SubtitleLabel(preferences.Language);
        SubtitleModeValue.Text = SubtitleModes.Label(preferences.Mode);
        ForcedSubtitlesValue.Text = OnOff(preferences.ShowForced);
        MatchDeviceValue.Text = OnOff(settings?.MatchDevice == true);
        DeviceOnlyValue.Text = OnOff(setting.DeviceOverride);
        FontSizeValue.Text = SubtitleAppearanceOptions.FontSizeLabel(appearance.FontSize);
        FontFamilyValue.Text = SubtitleAppearanceOptions.FontFamilyLabel(appearance.FontFamily);
        FontColorValue.Text = SubtitleAppearanceOptions.FontColorLabel(appearance.FontColor);
        TextOpacityValue.Text = SubtitleAppearanceOptions.Percent(appearance.TextOpacity);
        if (!setting.TextOpacitySupported && SettingsInput.IsWithin(FocusManager.GetFocusedElement() as DependencyObject, TextOpacityButton))
            FocusTarget(FontColorButton);
        TextOpacityButton.Visibility = Shown(setting.TextOpacitySupported);
        TextOutlineValue.Text = OnOff(appearance.TextOutline);
        OutlineColorValue.Text = SubtitleAppearanceOptions.OutlineColorLabel(appearance.TextOutlineColor);
        BackgroundStyleValue.Text = SubtitleAppearanceOptions.BackgroundStyleLabel(appearance.BackgroundStyle);
        BackgroundOpacityValue.Text = SubtitleAppearanceOptions.Percent(appearance.BackgroundOpacity);
        BackgroundColorValue.Text = SubtitleAppearanceOptions.BackgroundColorLabel(appearance.BackgroundColor);
        PositionValue.Text = SubtitleAppearanceOptions.PositionLabel(appearance.Position);
        // settings_subtitles_scopeDevice / settings_subtitles_scopeProfile.
        SubtitleScopeFooter.Text = setting.DeviceOverride ? "Only on this device." : "Saved for this profile on all devices.";
        UpdateSubtitlePreview(settings?.EffectiveAppearance(settings.MatchDevice ? SystemCaptions.Read() : null) ?? appearance);
    }

    private static string OnOff(bool on) => on ? "On" : "Off";

    /// <summary>
    /// TvSettingsSubtitlePreview: the sample in the effective appearance's font (the faces the player draws with), size,
    /// colour and text opacity; a four-way 1px outline for Text Outline or the Outline style; the box only for Box; at the
    /// top, or 7 (18 for Lower Third) above the bottom.
    /// </summary>
    private void UpdateSubtitlePreview(SubtitleAppearance appearance)
    {
        var safe = appearance.Sanitized();
        var family = new Windows.UI.Xaml.Media.FontFamily(SubtitleTextStyle.FontNameOf(safe.FontFamily));
        var size = SubtitleAppearanceOptions.PreviewFontSize(safe.FontSize);
        var outline = safe.TextOutline || safe.BackgroundStyle == SubtitleBackgroundStyle.Outline;
        var outlineBrush = PreviewBrush(safe.TextOutlineColor, 100);
        foreach (var text in new[] { SubtitlePreviewOutline1, SubtitlePreviewOutline2, SubtitlePreviewOutline3, SubtitlePreviewOutline4 })
        {
            text.FontFamily = family;
            text.FontSize = size;
            text.Foreground = outlineBrush;
            text.Visibility = Shown(outline);
        }
        SubtitlePreviewText.FontFamily = family;
        SubtitlePreviewText.FontSize = size;
        SubtitlePreviewText.Foreground = PreviewBrush(safe.FontColor, safe.TextOpacity);
        var box = safe.BackgroundStyle == SubtitleBackgroundStyle.Box;
        SubtitlePreviewBox.Background = box ? PreviewBrush(safe.BackgroundColor, safe.BackgroundOpacity) : null;
        SubtitlePreviewBox.Padding = box ? new Thickness(7, 2, 7, 2) : new Thickness(0);
        SubtitlePreviewBox.VerticalAlignment = safe.Position == SubtitlePosition.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        SubtitlePreviewBox.Margin = new Thickness(0, 0, 0, safe.Position == SubtitlePosition.LowerThird ? 18 : 7);
    }

    /// <summary>A #rrggbb colour at a percentage opacity (a literal: code never reads XAML resources).</summary>
    private static SolidColorBrush PreviewBrush(string hex, int opacityPercent)
    {
        var (red, green, blue) = SubtitleAppearance.Rgb(hex);
        return new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(Math.Clamp(opacityPercent, 0, 100) * 255 / 100), red, green, blue));
    }

    /// <summary>A picker row (its Tag names the SubtitlePicker): its sheet, the checked row focused (TvSettingsPickerSheet).</summary>
    private void OnSubtitlePickerClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings || _client is not { } client || sender is not Control row
            || !Enum.TryParse<SubtitlePicker>(row.Tag as string, out var picker)) return;
        _returnTarget = row;
        _subtitlePicker = picker;
        _subtitlePickerRow = row;
        var settings = client.SubtitleSettings;
        OptionSheet.Show(SubtitleSettingsPickers.Build(picker, settings.Preferences, settings.LanguageSuggestions, settings.Appearance.Value));
    }

    private void CloseSubtitlePicker()
    {
        OptionSheet.Hide();
        _subtitlePicker = null;
        if (_subtitlePickerRow is { } row) FocusTarget(row);
    }

    /// <summary>
    /// A chosen row: Language and Behavior write the profile's value (onSubtitleLanguageChanged / onSubtitleModeChanged);
    /// an appearance row saves the whole appearance, the profile's or this Xbox's own (editAppearance).
    /// </summary>
    private async void OnSubtitleOptionChosen(string id)
    {
        if (!OwnsSettings || _client is not { } client || _subtitlePicker is not { } picker) return;
        CloseSubtitlePicker();
        var settings = client.SubtitleSettings;
        if (picker == SubtitlePicker.Language)
            await ChangeSubtitleSettingAsync(client, "the subtitle language", () => settings.SetLanguageAsync(id));
        else if (picker == SubtitlePicker.Behavior)
        {
            if (SubtitleModes.FromWire(id) is { } mode) await ChangeSubtitleSettingAsync(client, "the subtitle behavior", () => settings.SetModeAsync(mode));
        }
        else if (SubtitleSettingsPickers.Apply(picker, id, settings.Appearance.Value) is { } next)
            await ChangeSubtitleSettingAsync(client, "the subtitle appearance", () => settings.SetAppearanceAsync(next));
    }

    /// <summary>TvSettingsScreen.kt SettingsToggleRow: Show Forced Subtitles is the profile's (onShowForcedSubtitlesChanged).</summary>
    private async void OnForcedSubtitlesClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings || _client is not { } client) return;
        _returnTarget = ForcedSubtitlesButton;
        var settings = client.SubtitleSettings;
        await ChangeSubtitleSettingAsync(client, "Show Forced Subtitles", () => settings.SetShowForcedAsync(!settings.Preferences.ShowForced));
    }

    /// <summary>Use Device Settings: this Xbox only (setSubtitleMatchesDevice is a local write), for this server and profile.</summary>
    private void OnMatchDeviceClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings || _client is not { } client) return;
        _returnTarget = MatchDeviceButton;
        var settings = client.SubtitleSettings;
        if (settings.SetMatchDevice(!settings.MatchDevice)) SubtitlesError.Visibility = Visibility.Collapsed;
        else
        {
            SubtitlesError.Text = "Bale couldn’t save Use Device Settings on this Xbox. Try again.";
            SubtitlesError.Visibility = Visibility.Visible;
        }
        UpdateSubtitles();
    }

    /// <summary>Only on This Device: on keeps a copy for this Xbox; off deletes it and goes back to the profile's.</summary>
    private async void OnDeviceOnlyClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings || _client is not { } client) return;
        _returnTarget = DeviceOnlyButton;
        var settings = client.SubtitleSettings;
        await ChangeSubtitleSettingAsync(client, "Only on This Device",
            () => settings.SetAppearanceOnlyOnThisDeviceAsync(!settings.Appearance.DeviceOverride));
    }

    private async void OnTextOutlineClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings || _client is not { } client) return;
        _returnTarget = TextOutlineButton;
        var settings = client.SubtitleSettings;
        var appearance = settings.Appearance.Value;
        await ChangeSubtitleSettingAsync(client, "the subtitle appearance",
            () => settings.SetAppearanceAsync(appearance with { TextOutline = !appearance.TextOutline }));
    }

    /// <summary>TvSettingsConfirmDialog: settings_subtitles_resetTitle / _resetMessage / _resetConfirm.</summary>
    private void OnResetAppearanceClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings) return;
        _returnTarget = ResetAppearanceButton;
        ResetPrompt.Show("Reset subtitle appearance?", "This restores every subtitle appearance option to its default.", "Reset");
    }

    /// <summary>resetSubtitleAppearance: the contract default, saved where edits go (the profile's, or this Xbox's own).</summary>
    private async void OnResetAppearanceConfirmed()
    {
        if (!OwnsSettings || _client is not { } client) return;
        FocusTarget(ResetAppearanceButton);
        await ChangeSubtitleSettingAsync(client, "the subtitle appearance", () => client.SubtitleSettings.SetAppearanceAsync(SubtitleAppearance.Default));
    }

    private async Task ChangeSubtitleSettingAsync(SiloClient client, string name, Func<Task> change)
    {
        if (_subtitleBusy) return;
        _subtitleBusy = true;
        // A read that started before this write must not land after it with the old value.
        _subtitleRead?.Cancel();
        var profileId = _profileId;
        try
        {
            await change();
            if (OwnsSettings && client.ProfileId == profileId) SubtitlesError.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            Log.Error("Settings subtitles write", error);
            if (OwnsSettings && client.ProfileId == profileId)
            {
                SubtitlesError.Text = "Bale couldn’t save " + name + " on the server. Try again.";
                SubtitlesError.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _subtitleBusy = false;
            if (OwnsSettings && client.ProfileId == profileId) UpdateSubtitles();
        }
    }

    /// <summary>
    /// Settings reads the connection when Playback settings opens, after a network change while it is open (via
    /// <see cref="ConnectionSpeedService.Changed"/>), and after Reset and Test speed (buffering-quality design,
    /// "Settings").
    /// </summary>
    private async Task ReadConnectionAsync(SiloClient client, string? profileId)
    {
        try { await ConnectionSpeedService.ReadAsync(client); }
        catch (Exception error) { Log.Error("Settings connection read", error); }
        if (OwnsSettings && client.ProfileId == profileId) UpdateConnection();
    }

    private void OnConnectionChanged() =>
        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
        {
            if (OwnsSettings) UpdateConnection();
        });

    private void UpdateConnection()
    {
        var focused = FocusManager.GetFocusedElement() as DependencyObject;
        var speed = _client?.Playback.Speed;
        var view = ConnectionSettings.Build(_client?.Settings.BufferingQuality ?? new BufferingQualitySetting(), speed?.Connection,
            speed?.Schedule is not null, speed?.Metered == true, _testing, _notice, DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        StreamingGroup.Visibility = Shown(view.StreamingShown);
        BufferingQualityButton.Visibility = BufferingQualityDescription.Visibility = Shown(view.ModeShown);
        BufferingQualityValue.Text = view.ModeValue;
        LearnedLimitButton.Visibility = Shown(view.LearnedLimit is not null);
        LearnedLimitLabel.Text = view.LearnedLimit ?? string.Empty;
        LearnedLimitError.Visibility = Shown(_resetFailed && view.LearnedLimit is not null);
        ConnectionGroup.Visibility = Shown(view.ConnectionShown);
        ConnectionSpeedButton.Visibility = Shown(view.ConnectionShown && !view.Skipped);
        ConnectionSpeedInfoRow.Visibility = ConnectionSkipped.Visibility = Shown(view.Skipped);
        ConnectionSpeedValue.Text = view.TestValue;
        SetLine(ConnectionSpeedLine, view.SpeedLine);
        SetLine(ConnectionNotice, view.Notice);
        SetLine(ConnectionBudget, view.Budget);
        // A row that left with focus hands it to the row above it, as the official app does after Reset.
        foreach (var row in new Control[] { BufferingQualityButton, LearnedLimitButton, ConnectionSpeedButton })
            if (row.Visibility == Visibility.Collapsed && SettingsInput.IsWithin(focused, row)) FocusTarget(ResetFocusTarget);
    }

    private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private static void SetLine(TextBlock line, string? text)
    {
        if (line.Text != (text ?? string.Empty)) line.Text = text ?? string.Empty;
        line.Visibility = Shown(text is not null);
    }

    /// <summary>Where focus goes when the learned-limit row leaves: the mode row, else the next row down.</summary>
    private Control ResetFocusTarget =>
        new Control[] { BufferingQualityButton, ConnectionSpeedButton }.FirstOrDefault(row => row.Visibility == Visibility.Visible) ?? AutoPlayButton;

    /// <summary>Test speed: the accurate test (up to 10 s). The row stays focusable while it runs; a second press is ignored.</summary>
    private async void OnTestSpeedClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings || _testing || _client is not { } client) return;
        _returnTarget = ConnectionSpeedButton;
        var profileId = _profileId;
        _testing = true;
        _notice = null;
        UpdateConnection();
        ConnectionSpeedNotice? notice;
        try { notice = await ConnectionSpeedService.TestSpeedAsync(); }
        catch (Exception error)
        {
            Log.Error("Settings test speed", error);
            notice = new ConnectionSpeedNotice(ConnectionSpeedNoticeKind.Failed);
        }
        _testing = false;
        if (!OwnsSettings || client.ProfileId != profileId) return;
        _notice = notice;
        UpdateConnection();
    }

    /// <summary>Reset: DELETE the learned limit, then read the connection again. Focus moves up first.</summary>
    private async void OnLearnedLimitResetClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings || _resetBusy || _client is not { } client) return;
        _resetBusy = true;
        var profileId = _profileId;
        FocusTarget(ResetFocusTarget);
        bool reset;
        try { reset = await client.Playback.ResetLearnedLimitAsync(CancellationToken.None); }
        catch (Exception error)
        {
            Log.Error("Settings learned-limit reset", error);
            reset = false;
        }
        if (OwnsSettings && client.ProfileId == profileId)
        {
            _resetFailed = !reset;
            UpdateConnection();
        }
        if (reset) await ReadConnectionAsync(client, profileId);
        _resetBusy = false;
    }

    private Button[] BufferingOptions => [BufferingProfileOption, BufferingNeverOption, BufferingAskOption, BufferingAlwaysOption];

    /// <summary>"Use profile setting" is the checked choice while this Xbox has no value of its own.</summary>
    private Button SelectedBufferingOption
    {
        get
        {
            var setting = _client?.Settings.BufferingQuality ?? new BufferingQualitySetting();
            if (!setting.DeviceOverride) return BufferingProfileOption;
            return setting.Value switch
            {
                BufferingQualityMode.Never => BufferingNeverOption,
                BufferingQualityMode.Ask => BufferingAskOption,
                _ => BufferingAlwaysOption,
            };
        }
    }

    private void OnBufferingQualityClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings) return;
        _returnTarget = BufferingQualityButton;
        var selected = SelectedBufferingOption;
        foreach (var option in BufferingOptions)
        {
            option.ApplyTemplate();
            VisualStateManager.GoToState(option, ReferenceEquals(option, selected) ? "Selected" : "Unselected", false);
        }
        BufferingPicker.Visibility = Visibility.Visible;
        FocusRetry.Until(this, () => !OwnsSettings || BufferingPicker.Visibility != Visibility.Visible || selected.Focus(FocusState.Programmatic));
    }

    private void CloseBufferingPicker()
    {
        BufferingPicker.Visibility = Visibility.Collapsed;
        FocusTarget(BufferingQualityButton);
    }

    /// <summary>A mode stores this Xbox's own value (profile_device); "Use profile setting" deletes it.</summary>
    private async void OnBufferingOptionClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings || _client is not { } client) return;
        var choice = (sender as FrameworkElement)?.Tag as string;
        CloseBufferingPicker();
        if (_modeBusy) return;
        _modeBusy = true;
        var profileId = _profileId;
        try
        {
            if (choice == "profile") await client.Settings.UseProfileBufferingQualityModeAsync();
            else if (BufferingQualityModes.FromWire(choice) is { } mode) await client.Settings.SetBufferingQualityModeAsync(mode);
            if (OwnsSettings && client.ProfileId == profileId) PlaybackError.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            Log.Error("Settings buffering quality write", error);
            if (OwnsSettings && client.ProfileId == profileId)
            {
                PlaybackError.Text = "Bale couldn’t save Lower quality when buffering on the server. Try again.";
                PlaybackError.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _modeBusy = false;
            if (OwnsSettings && client.ProfileId == profileId) UpdateConnection();
        }
    }

    private void OnSignOutFocus(object sender, RoutedEventArgs e)
    {
        // TvSettingsScreen.kt hides the category selection while the bottom rail action owns focus.
        foreach (var (_, row) in Categories) row.IsChecked = false;
    }

    private void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings || _signingOut) return;
        _returnTarget = SignOutButton;
        SignOutPrompt.Show();
    }

    private void OnAccountsClick(object sender, RoutedEventArgs e)
    {
        if (!OwnsSettings || _signingOut) return;
        _returnTarget = AccountsButton;
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
