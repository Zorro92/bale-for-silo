using Bale.Core.Connection;
using Bale.Xbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>
/// Settings › Accounts (TvAccountsScreen.kt / TvAccountsViewModel.kt, multi-account spec §4.5), where the server list
/// was: each saved server with its accounts, Add an account on it, Rename Server and Remove Server, then Add a server. An account's
/// page offers Sign in again and Sign Out. Switching accounts happens on Change Account, not here. Merely opening this
/// page never signs out.
/// </summary>
public sealed partial class ServerListPage : Page, IBackHandler, IRestoresFocus
{
    private readonly List<(Button Button, string Key)> _buttons = [];
    private ConnectionPreview? _preview;
    private SavedAccount? _pageAccount;
    private SavedServer? _removing;
    private SavedServer? _renaming;
    private string? _lastKey;
    private bool _active;
    private bool _busy;
    private SavedServerRegistry Servers => _preview?.Servers ?? AppSettings.Servers;
    internal bool IsAuthPreview => _preview is not null;

    public ServerListPage()
    {
        InitializeComponent();
        RemovePrompt.Confirmed += OnPromptConfirmed;
        RemovePrompt.Alternate += OnAccountSignOut;
        RemovePrompt.Dismissed += () => { _removing = null; _pageAccount = null; RestoreFocus(); };
        SignOutPrompt.SignOutRequested += OnSignOutRequested;
        SignOutPrompt.Dismissed += () => { _pageAccount = null; RestoreFocus(); };
        RenamePrompt.Confirmed += OnRenameConfirmed;
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        _active = true;
        _busy = false;
        if (args.NavigationMode != NavigationMode.Back)
        {
            _preview = args.Parameter as ConnectionPreview;
            _lastKey = null;
        }
        ErrorText.Visibility = Visibility.Collapsed;
        Populate();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        _active = false;
        RemovePrompt.Hide();
        SignOutPrompt.Hide();
        RenamePrompt.Hide(restoreFocus: false);
        _renaming = null;
        base.OnNavigatedFrom(args);
    }

    private void Populate()
    {
        Rows.Children.Clear();
        _buttons.Clear();
        var openId = Servers.ActiveAccount?.Id;
        foreach (var group in Servers.Groups)
        {
            var server = group.Server;
            Rows.Children.Add(new TextBlock
            {
                Text = server.DisplayName.ToUpperInvariant(), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis,
                Style = (Style)Application.Current.Resources["SkylineSettingsGroupHeaderStyle"],
            });
            foreach (var account in group.Accounts)
            {
                // "2 profiles" or "Sign in again", led by "Current" on the open account (TvAccountsScreen.kt accountValue).
                var profiles = account.ProfileCount is int count and > 0 ? $"{count} {(count == 1 ? "profile" : "profiles")}" : null;
                var value = account.NeedsSignIn ? "Sign in again"
                    : string.Join(" · ", new[] { account.Id == openId ? "Current" : null, profiles }.Where(part => part is not null));
                Add(Row(account.DisplayName, value), $"account/{account.Id}", () => OpenAccount(account));
            }
            Add(Row($"Add an account on {server.DisplayName}", null), $"add-account/{server.Id}", () => AddAccount(server));
            // TvAccountsScreen.kt AccountsRow.Rename: the address without its scheme as the value.
            Add(Row("Rename Server", server.HostLabel), $"rename/{server.Id}", () => ConfirmRename(server));
            Add(Row("Remove Server", null, destructive: true), $"remove/{server.Id}", () => ConfirmRemove(server));
        }
        var addServer = Row("Add a server", null);
        addServer.Margin = new Thickness(0, 10, 0, 0);
        Add(addServer, "add-server", OnAddServer);
        Rows.Children.Add(new TextBlock
        {
            Text = "Signing out of an account removes it and its settings from this TV. Its server stays saved.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
            Style = (Style)Application.Current.Resources["SkylineSettingsFooterStyle"],
        });
        // Focus starts on the first account, else the first "Add an account", else Add a server.
        if (_lastKey is null || !_buttons.Any(b => b.Key == _lastKey))
            _lastKey = (_buttons.FirstOrDefault(b => b.Key.StartsWith("account/", StringComparison.Ordinal)).Key
                ?? _buttons.FirstOrDefault(b => b.Key.StartsWith("add-account/", StringComparison.Ordinal)).Key) ?? "add-server";
        RestoreFocus();
    }

    private void Add(Button button, string key, Action action)
    {
        button.Click += (_, _) =>
        {
            if (_active && !_busy && !RemovePrompt.IsOpen && !SignOutPrompt.IsOpen && !RenamePrompt.IsOpen) action();
        };
        button.GotFocus += (_, _) =>
        {
            _lastKey = key;
            button.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        };
        _buttons.Add((button, key));
        Rows.Children.Add(button);
    }

    private static Button Row(string label, string? value, bool destructive = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock { Text = label, Style = (Style)Application.Current.Resources["SkylineSettingsRowTextStyle"] };
        if (destructive) text.Foreground = (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["SkylineSettingsDestructive"];
        grid.Children.Add(text);
        if (!string.IsNullOrEmpty(value))
        {
            var detail = new TextBlock { Text = value, Style = (Style)Application.Current.Resources["SkylineSettingsValueTextStyle"] };
            Grid.SetColumn(detail, 1);
            grid.Children.Add(detail);
        }
        var row = new Button { Style = (Style)Application.Current.Resources["SkylineSettingsValueRowStyle"], Content = grid };
        AutomationProperties.SetName(row, string.IsNullOrEmpty(value) ? label : $"{label}, {value}");
        return row;
    }

    public void RestoreFocus() => FocusRetry.Until(Rows, () => !_active || RemovePrompt.IsOpen || SignOutPrompt.IsOpen || RenamePrompt.IsOpen
        || (_buttons.FirstOrDefault(b => b.Key == _lastKey).Button ?? _buttons.LastOrDefault().Button) is not { } button
        || button.Focus(FocusState.Programmatic));

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (RemovePrompt.IsOpen || SignOutPrompt.IsOpen || RenamePrompt.IsOpen) return;
        var vertical = SettingsInput.VerticalStep(args.Key);
        if (vertical == 0 && SettingsInput.HorizontalStep(args.Key) == 0) return;
        args.Handled = true;
        if (vertical == 0) return; // One column: Left/Right never move focus sideways.
        var focused = FocusManager.GetFocusedElement() as Button;
        var index = _buttons.FindIndex(b => ReferenceEquals(b.Button, focused));
        if (index < 0) { RestoreFocus(); return; }
        _buttons[Math.Clamp(index + vertical, 0, _buttons.Count - 1)].Button.Focus(FocusState.Keyboard);
    }

    /// <summary>An account's page: its server and profiles, with Sign in again and Sign Out.</summary>
    private void OpenAccount(SavedAccount account)
    {
        _pageAccount = account;
        var server = Servers.Snapshot.Entries.FirstOrDefault(s => s.Id == account.ServerId)?.DisplayName ?? string.Empty;
        var lines = new List<string> { server };
        if (account.ProfileCount is int count and > 0) lines.Add($"{count} {(count == 1 ? "profile" : "profiles")}");
        if (account.NeedsSignIn) lines.Add("This account needs to sign in again.");
        RemovePrompt.ShowAccount(account.DisplayName, string.Join("\n", lines));
    }

    private void OnPromptConfirmed()
    {
        if (_removing is { } server)
        {
            _removing = null;
            RemoveServer(server);
        }
        else if (_pageAccount is { } account)
        {
            _pageAccount = null;
            SignInAgain(account);
        }
    }

    private void OnAccountSignOut()
    {
        if (_pageAccount is { } account) SignOutPrompt.Show(account);
    }

    /// <summary>Sign in again: the account becomes the open one and its sign-in shows with its username.</summary>
    private void SignInAgain(SavedAccount account)
    {
        if (_preview is not null)
        {
            Frame.Navigate(typeof(MainPage), new ConnectionNavigation(_preview, ConnectionScreen.Login));
            return;
        }
        if (Switch(account.ServerId, account.Id)) OpenSignIn();
    }

    /// <summary>"Add an account on {server}": a sign-in there with no account selected.</summary>
    private void AddAccount(SavedServer server)
    {
        if (_preview is not null)
        {
            Frame.Navigate(typeof(MainPage), new ConnectionNavigation(_preview, ConnectionScreen.Login));
            return;
        }
        if (Switch(server.Id, null)) OpenSignIn();
    }

    private bool Switch(string serverId, string? accountId)
    {
        try
        {
            Session.SwitchAccount(serverId, accountId);
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"Account change failed type={ex.GetType().Name}");
            ShowError("Couldn't change to that account. Try again.");
            return false;
        }
    }

    /// <summary>The screens behind belonged to the old account; Back from the sign-in returns here.</summary>
    private void OpenSignIn()
    {
        _busy = true;
        Frame.BackStack.Clear();
        Frame.Navigate(typeof(MainPage), MainPage.AccountSignIn);
    }

    private async void OnSignOutRequested(bool removeServer)
    {
        if (!_active || _pageAccount is not { } account) return;
        _pageAccount = null;
        _busy = true;
        try
        {
            if (_preview is not null)
            {
                Servers.RemoveAccount(account.Id, removeServer);
                _busy = false;
                Populate();
                return;
            }
            var wasOpen = Servers.ActiveAccount?.Id == account.Id;
            await Session.SignOutAccountAsync(account, removeServer);
            if (!_active) return;
            _busy = false;
            // Signing out of the open account leaves no profile behind: resolve what opens next.
            if (wasOpen) ResolveActive();
            else Populate();
        }
        catch (Exception ex)
        {
            _busy = false;
            Log.Info($"Account sign-out failed type={ex.GetType().Name}");
            ShowError("Silo couldn't sign out of that account. Please try again.");
            RestoreFocus();
        }
    }

    /// <summary>
    /// Rename Server (TvAccountsScreen.kt renameServer): TvTextInputDialog with the current name filled in. Blank is
    /// allowed and goes back to the name the server gives itself.
    /// </summary>
    private void ConfirmRename(SavedServer server)
    {
        _renaming = server;
        RenamePrompt.ShowText("Rename Server", "Name", "Rename", server.DisplayName, allowBlank: true);
    }

    private void OnRenameConfirmed(string name)
    {
        if (_renaming is not { } server || !_active) return;
        _renaming = null;
        try
        {
            // The fixture registry in previews, the saved list otherwise; only the local name changes.
            Servers.Rename(server.Id, name);
        }
        catch (Exception ex)
        {
            // TvAccountsViewModel.kt onRename ignores a failed save; the list keeps the old name.
            Log.Info($"Server rename failed type={ex.GetType().Name}");
        }
        Populate();
    }

    private void ConfirmRemove(SavedServer server)
    {
        _removing = server;
        RemovePrompt.ShowRemove(server.DisplayName);
    }

    /// <summary>Remove Server signs out of every account on it, then forgets the server.</summary>
    private void RemoveServer(SavedServer server)
    {
        try
        {
            if (_preview is not null) Servers.Remove(server.Id);
            else if (Session.RemoveServer(server.Id))
            {
                ResolveActive();
                return;
            }
            Populate(); // Removing a server not in use is a list edit; the live session stays.
        }
        catch (Exception ex)
        {
            Log.Info($"Saved server removal failed type={ex.GetType().Name}");
            ShowError("Signing out of one of its accounts didn't finish, so the server is still saved. Try again.");
            RestoreFocus();
        }
    }

    private void OnAddServer()
    {
        // Keep the signed-in page/session until a successful probe and (for HTTP) explicit consent.
        Frame.Navigate(typeof(MainPage), _preview is null ? MainPage.ServerEntry : new ConnectionNavigation(_preview));
    }

    private void ResolveActive()
    {
        Frame.Navigate(typeof(MainPage));
        Frame.BackStack.Clear(); // No journal entry may reopen the old account's UI/profile.
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    public bool OnBack()
    {
        if (RemovePrompt.Dismiss() || SignOutPrompt.Dismiss() || RenamePrompt.Dismiss()) return true;
#if DEBUG_SERVER
        if (IsAuthPreview && DebugServer.CloseAuthPreview(Frame)) return true;
#endif
        // TvAppNavigation.kt: a change here that left no profile open goes to what opens next, not the old pages.
        if (Frame.CanGoBack && (_preview is not null || Session.Client is not null)) Frame.GoBack();
        else ResolveActive();
        return true;
    }
}
