using Bale.Core;
using Bale.Core.Connection;
using Bale.Xbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>
/// TvChangeAccountScreen.kt / TvChangeAccountViewModel.kt (multi-account spec §4.1): the saved servers on a rail, the
/// shown server's accounts beside it. Choosing another account makes it the open one and opens its picker, which waits
/// for a choice; one that must sign in again opens sign-in with its username; "Add an account" signs in on that server.
/// It is the root screen while no account is open but another has a session, and at a launch that asks who's watching
/// with several accounts saved. While it is open every saved account's profile count is refreshed once in the
/// background (AccountProfilesRefresher.kt), and an arrow under each column says when rows wait above or below.
/// </summary>
public sealed partial class ChangeAccountPage : Page, IBackHandler, IRestoresFocus
{
    private static readonly SolidColorBrush ShownFill = new(Windows.UI.Color.FromArgb(36, 255, 255, 255));
    private static readonly SolidColorBrush Transparent = new(Windows.UI.Colors.Transparent);
    private readonly List<(Button Row, AccountServerGroup Group)> _rail = [];
    private readonly List<Button> _pane = [];
    private readonly Dictionary<string, (Button Card, TextBlock Detail, string? Badge)> _cards = [];
    private CancellationTokenSource? _refresh;
    private IReadOnlyList<AccountServerGroup> _groups = [];
    private ConnectionPreview? _preview;
    private string? _shownServerId;
    private Button? _lastFocused;
    private bool _active;
    private bool _changing;
    private SavedServerRegistry Servers => _preview?.Servers ?? AppSettings.Servers;
    internal bool IsAuthPreview => _preview is not null;

    public ChangeAccountPage() => InitializeComponent();

    /// <summary>Nothing behind it: no profile is open, so Back steps from the accounts to the rail, then leaves.</summary>
    private bool IsRoot => !Frame.CanGoBack;

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        _active = true;
        _changing = false;
        var navigation = args.Parameter as AccountsNavigation;
        _preview = navigation?.Preview; // Back navigation restores the same parameter.
        ErrorText.Visibility = Visibility.Collapsed;
        Populate(args.NavigationMode == NavigationMode.Back ? null : navigation?.AddAccountOn);
        // Once per opening, as the ViewModel's init: coming Back to this screen keeps the counts already read.
        if (args.NavigationMode != NavigationMode.Back) RefreshProfileCounts();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        _active = false;
        _refresh?.Cancel();
        _refresh = null;
        base.OnNavigatedFrom(args);
    }

    /// <summary>
    /// TvChangeAccountViewModel.kt init: the cards show each account's last known count at once, then every saved
    /// account's profile list is read once in the background, each with its own saved session. An unreachable server
    /// or a refused session keeps its count; nothing signs out, marks "Sign in again" or prompts. Previews use the
    /// fixture's counts, with no network.
    /// </summary>
    private async void RefreshProfileCounts()
    {
        _refresh?.Cancel();
        var cancel = _refresh = new CancellationTokenSource();
        try
        {
            var servers = Servers;
            AccountProfileCountSource source;
            if (_preview is not null) source = _preview.ProfileCounts;
            else
            {
                var identity = DeviceIdentity.Current();
                source = AccountProfilesRefresher.FromSavedSessions(servers, VaultAccountCredentials.Instance, url => new SiloClient(url, identity));
            }
            var changed = await Task.Run(() => new AccountProfilesRefresher(servers, source).RefreshAllAsync(cancel.Token));
            if (!_active || cancel.IsCancellationRequested || !ReferenceEquals(servers, Servers)) return;
            Log.Info($"Account profile counts refreshed changed={changed.Count}");
            if (changed.Count > 0) ShowCounts();
        }
        catch (Exception ex)
        {
            Log.Info($"Account profile refresh failed type={ex.GetType().Name}");
        }
    }

    /// <summary>New counts land on the shown cards in place, so focus and scrolling stay where they are.</summary>
    private void ShowCounts()
    {
        _groups = Servers.Groups;
        foreach (var account in _groups.SelectMany(g => g.Accounts))
        {
            if (!_cards.TryGetValue(account.Id, out var card)) continue;
            var profiles = ProfilesLine(account);
            card.Detail.Text = profiles ?? string.Empty;
            card.Detail.Visibility = profiles is null ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(card.Card, string.Join(", ", new[] { account.DisplayName, profiles, card.Badge }.Where(part => part is not null)));
        }
    }

    private static string? ProfilesLine(SavedAccount account) =>
        account.ProfileCount is int count and > 0 ? $"{count} {(count == 1 ? "profile" : "profiles")}" : null;

    private void OnScrollerViewChanged(object sender, ScrollViewerViewChangedEventArgs args) => ShowScrollArrows();
    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs args) => ShowScrollArrows();

    /// <summary>ScrollingColumn's arrows: up while rows are above (canScrollBackward), down while rows are below.</summary>
    private void ShowScrollArrows()
    {
        Arrows(RailScroller, RailMoreAbove, RailMoreBelow);
        Arrows(PaneScroller, PaneMoreAbove, PaneMoreBelow);

        static void Arrows(ScrollViewer scroller, UIElement above, UIElement below)
        {
            above.Visibility = scroller.VerticalOffset > 0.5 ? Visibility.Visible : Visibility.Collapsed;
            below.Visibility = scroller.ScrollableHeight - scroller.VerticalOffset > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Populate(string? addAccountOn)
    {
        _groups = Servers.Groups;
        RailRows.Children.Clear();
        _rail.Clear();
        foreach (var group in _groups)
        {
            var row = new Button { Style = (Style)Application.Current.Resources["SkylineChangeAccountRailRowStyle"], Content = RailContent(group.Server) };
            AutomationProperties.SetName(row, group.Server.DisplayName);
            row.GotFocus += OnRowGotFocus;
            row.Click += (_, _) => FocusPaneEntry();
            _rail.Add((row, group));
            RailRows.Children.Add(row);
        }
        // Focus starts on the open account, or with addAccountOn on that server's "Add an account".
        var shown = _groups.FirstOrDefault(g => g.Server.Id == addAccountOn) ?? _groups.FirstOrDefault();
        ShowServer(shown?.Server.Id);
        _lastFocused = shown is null ? AddServerButton : addAccountOn is not null && shown.Server.Id == addAccountOn ? _pane[^1] : PaneEntry();
        RestoreFocus();
    }

    private void ShowServer(string? serverId)
    {
        _shownServerId = serverId;
        foreach (var (row, group) in _rail) row.Background = group.Server.Id == serverId ? ShownFill : Transparent;
        PaneRows.Children.Clear();
        _pane.Clear();
        _cards.Clear();
        // ScrollingColumn's pane scroll state is keyed by the server: another server's accounts start at the top.
        PaneScroller.ChangeView(null, 0, null, disableAnimation: true);
        if (_groups.FirstOrDefault(g => g.Server.Id == serverId) is not { } shown) return;
        var openId = Servers.ActiveAccount?.Id;
        foreach (var account in shown.Accounts)
        {
            var current = account.Id == openId;
            var badge = account.NeedsSignIn ? "Sign in again" : current ? "Current" : null;
            var profiles = ProfilesLine(account);
            var card = Card(account.DisplayName, profiles, account.DisplayName.Trim()[..1].ToUpperInvariant(), badge, account.NeedsSignIn, out var detail);
            AutomationProperties.SetName(card, string.Join(", ", new[] { account.DisplayName, profiles, badge }.Where(part => part is not null)));
            _cards[account.Id] = (card, detail, badge);
            card.Click += (_, _) => OnAccountChosen(account);
            _pane.Add(card);
        }
        var add = Card("Add an account", $"Sign in on {shown.Server.DisplayName}", "+", null, false, out _);
        AutomationProperties.SetName(add, "Add an account");
        add.Click += (_, _) => OnAddAccount(shown.Server);
        _pane.Add(add);
        foreach (var card in _pane)
        {
            card.GotFocus += OnRowGotFocus;
            PaneRows.Children.Add(card);
        }
    }

    /// <summary>Where Right from the rail lands: the open account, else the first card, else "Add an account".</summary>
    private Button PaneEntry()
    {
        var accounts = _groups.FirstOrDefault(g => g.Server.Id == _shownServerId)?.Accounts ?? [];
        var openId = Servers.ActiveAccount?.Id;
        for (var index = 0; index < accounts.Count; index++)
        {
            if (accounts[index].Id == openId) return _pane[index];
        }
        return _pane[0];
    }

    private void FocusPaneEntry()
    {
        if (_pane.Count > 0) PaneEntry().Focus(FocusState.Keyboard);
    }

    private Button? ShownRailRow() => _rail.FirstOrDefault(pair => pair.Group.Server.Id == _shownServerId).Row;

    public void RestoreFocus() => FocusRetry.Until(Root, () => !_active || (_lastFocused ?? AddServerButton).Focus(FocusState.Programmatic));

    private void OnRowGotFocus(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button) return;
        _lastFocused = button;
        button.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        // Focusing a server on the rail shows its accounts.
        if (_rail.FirstOrDefault(pair => ReferenceEquals(pair.Row, button)).Group is { } group && group.Server.Id != _shownServerId)
            ShowServer(group.Server.Id);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var vertical = SettingsInput.VerticalStep(args.Key);
        var horizontal = SettingsInput.HorizontalStep(args.Key);
        if (vertical == 0 && horizontal == 0) return;
        args.Handled = true;
        var focused = FocusManager.GetFocusedElement() as Button;
        List<Button> rail = [.. _rail.Select(pair => pair.Row), AddServerButton];
        if (focused is not null && rail.IndexOf(focused) is var r and >= 0)
        {
            // Up/Down stay in the rail and stop at its ends; Right enters the shown server's accounts.
            if (vertical != 0) rail[Math.Clamp(r + vertical, 0, rail.Count - 1)].Focus(FocusState.Keyboard);
            else if (horizontal > 0) FocusPaneEntry();
        }
        else if (focused is not null && _pane.IndexOf(focused) is var p and >= 0)
        {
            if (vertical != 0) _pane[Math.Clamp(p + vertical, 0, _pane.Count - 1)].Focus(FocusState.Keyboard);
            else if (horizontal < 0) (ShownRailRow() ?? AddServerButton).Focus(FocusState.Keyboard);
        }
        else
        {
            RestoreFocus();
        }
    }

    private void OnAccountChosen(SavedAccount account)
    {
        if (_changing || !_active) return;
        if (_preview is not null)
        {
            Servers.ActivateAccount(account.ServerId, account.Id);
            Populate(null);
            return;
        }
        var isOpen = Servers.ActiveAccount?.Id == account.Id;
        if (isOpen && !account.NeedsSignIn && Session.Client is not null)
        {
            // The open account from the profile menu: its picker, as Switch Profile opens it.
            Session.ShowProfilePicker(Frame);
            return;
        }
        _changing = true;
        try
        {
            if (!isOpen) Session.SwitchAccount(account.ServerId, account.Id);
        }
        catch (Exception ex)
        {
            _changing = false;
            Log.Info($"Account change failed type={ex.GetType().Name}");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (account.NeedsSignIn)
        {
            OpenSignIn();
            return;
        }
        // Everything behind belonged to the old account; its picker waits for a choice.
        Frame.Navigate(typeof(MainPage), MainPage.AccountChosen);
        Frame.BackStack.Clear();
    }

    /// <summary>"Add an account": the server becomes active with no account open, then sign-in starts there.</summary>
    private void OnAddAccount(SavedServer server)
    {
        if (_changing || !_active) return;
        if (_preview is not null)
        {
            Frame.Navigate(typeof(MainPage), new ConnectionNavigation(_preview, ConnectionScreen.Login));
            return;
        }
        _changing = true;
        try
        {
            Session.SwitchAccount(server.Id, null);
        }
        catch (Exception ex)
        {
            _changing = false;
            Log.Info($"Add account failed type={ex.GetType().Name}");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        OpenSignIn();
    }

    /// <summary>Sign-in over this screen: Back from it returns here, never to the old account's pages.</summary>
    private void OpenSignIn()
    {
        Frame.BackStack.Clear();
        Frame.Navigate(typeof(MainPage), MainPage.AccountSignIn);
    }

    private void OnAddServerClick(object sender, RoutedEventArgs args)
    {
        if (_changing) return;
        // The existing server setup flow keeps the open session until a server is committed.
        Frame.Navigate(typeof(MainPage), _preview is null ? MainPage.ServerEntry : new ConnectionNavigation(_preview));
    }

    public bool OnBack()
    {
#if DEBUG_SERVER
        if (IsAuthPreview && DebugServer.CloseAuthPreview(Frame)) return true;
#endif
        if (!IsRoot)
        {
            Frame.GoBack();
            return true;
        }
        // Root: Back steps from the accounts to the rail first (Roku), and leaves Silo from the rail.
        var focused = FocusManager.GetFocusedElement() as Button;
        if (focused is not null && _pane.Contains(focused) && ShownRailRow() is { } rail)
        {
            rail.Focus(FocusState.Keyboard);
            return true;
        }
        Application.Current.Exit();
        return true;
    }

    private static Button Card(string title, string? detail, string initial, string? badge, bool warning, out TextBlock detailText)
    {
        var grid = new Grid { ColumnSpacing = 11 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = new Border { Style = (Style)Application.Current.Resources["SkylineChangeAccountTileStyle"] };
        tile.Child = new TextBlock { Text = initial, Style = (Style)Application.Current.Resources["SkylineChangeAccountTileTextStyle"] };
        grid.Children.Add(tile);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SkylineChangeAccountNameStyle"] });
        // The second line is always there, collapsed without a count, so a refreshed count can fill it in place.
        detailText = new TextBlock
        {
            Text = detail ?? string.Empty, Style = (Style)Application.Current.Resources["SkylineChangeAccountDetailStyle"],
            Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible,
        };
        text.Children.Add(detailText);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var card = new Button { Style = (Style)Application.Current.Resources["SkylineChangeAccountCardStyle"], Content = grid };
        if (badge is not null)
        {
            var ink = warning ? (SolidColorBrush)Application.Current.Resources["SkylineAuthWarning"]
                : (SolidColorBrush)Application.Current.Resources["SkylinePrimary"];
            var restFill = new SolidColorBrush(warning ? Windows.UI.Color.FromArgb(20, ink.Color.R, ink.Color.G, ink.Color.B)
                : Windows.UI.Color.FromArgb(115, 0, 0, 0));
            var pill = new Border
            {
                Style = (Style)Application.Current.Resources["SkylineChangeAccountBadgeStyle"],
                Background = restFill,
                BorderBrush = warning ? new SolidColorBrush(Windows.UI.Color.FromArgb(89, ink.Color.R, ink.Color.G, ink.Color.B))
                    : (SolidColorBrush)Application.Current.Resources["SkylineAuthHairline"],
                Child = new TextBlock { Text = badge.ToUpperInvariant(), Foreground = ink, Style = (Style)Application.Current.Resources["SkylineChangeAccountBadgeTextStyle"] },
            };
            Grid.SetColumn(pill, 2);
            grid.Children.Add(pill);
            // On the focused card the badge turns near-opaque black so its text keeps contrast.
            var focusFill = new SolidColorBrush(Windows.UI.Color.FromArgb(217, 0, 0, 0));
            card.GotFocus += (_, _) => pill.Background = focusFill;
            card.LostFocus += (_, _) => pill.Background = restFill;
        }
        return card;
    }

    /// <summary>MarqueeServerMark.kt: the server's branded mark over its initial.</summary>
    private static Grid RailContent(SavedServer server)
    {
        var grid = new Grid { ColumnSpacing = 9 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var mark = new Grid();
        mark.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SkylineChangeAccountMarkStyle"],
            Child = new TextBlock
            {
                Text = server.DisplayName.Trim()[..1].ToUpperInvariant(), FontSize = 12.04, FontWeight = Windows.UI.Text.FontWeights.Bold,
                Foreground = (SolidColorBrush)Application.Current.Resources["SkylinePrimary"],
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        });
        if (Uri.TryCreate(server.Url, UriKind.Absolute, out var url) && AppSettings.GetServerBranding(url).MarkUrl is { } markUrl)
        {
            // An ImageBrush clips to the Border's radius; the initial stays underneath if the image fails.
            var image = new Border { Style = (Style)Application.Current.Resources["SkylineChangeAccountMarkStyle"], Visibility = Visibility.Collapsed };
            var bitmap = new BitmapImage();
            bitmap.ImageOpened += (_, _) => image.Visibility = Visibility.Visible;
            image.Background = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
            bitmap.UriSource = markUrl;
            mark.Children.Add(image);
        }
        grid.Children.Add(mark);
        var name = new TextBlock { Text = server.DisplayName, VerticalAlignment = VerticalAlignment.Center, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        return grid;
    }
}
