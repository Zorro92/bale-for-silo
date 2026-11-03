using Bale.Core;
using Bale.Core.Api.Models;
using Bale.Core.Auth;
using Bale.Core.Connection;
using Bale.Xbox.Services;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>TvServerSetupScreen.kt / TvLoginScreen.kt: public setup, origin consent, then phone or password sign-in.</summary>
public sealed partial class MainPage : Page, IBackHandler, IRestoresFocus
{
    public const string SessionExpired = "session-expired";
    public const string ServerEntry = "server-entry";
    private CancellationTokenSource? _pairing;
    private CancellationTokenSource? _loginLife;
    private Uri? _loginServer;
    private bool _active;
    private bool _connecting;
    private bool _expiredNotice;
    private bool _sessionAdopted;
    private InputPane? _inputPane;
    private bool _keyboardVisible;
    private ConnectionPreview? _preview;
    private Button? _httpPromptOpener;
    private SavedServerRegistry Servers => _preview?.Servers ?? AppSettings.Servers;
    internal bool IsAuthPreview => _preview is not null;

    public MainPage()
    {
        InitializeComponent();
        TvNameText.Text = DeviceIdentity.Current().DeviceName;
        var serverActions = Rail.Attach(ServerActions);
        serverActions.Pin = false;
        serverActions.EntryItem = () => ConnectButton;
        var schemes = Rail.Attach(SchemeActions);
        schemes.Pin = false;
        schemes.EntryItem = () => AutoButton;
        var passwordFields = Rail.Attach(PasswordRow);
        passwordFields.Pin = false;
        passwordFields.EntryItem = () => PasswordInput;
        var passwordActions = Rail.Attach(PasswordActions);
        passwordActions.Pin = false;
        passwordActions.EntryItem = () => SignInButton.IsEnabled ? SignInButton : _passwordOnly ? PasswordChangeServerButton : UsePhoneButton;
        var codeActions = Rail.Attach(CodeActions);
        codeActions.Pin = false;
        codeActions.EntryItem = () => NewCodeButton.Visibility == Visibility.Visible ? NewCodeButton
            : PasswordButton.Visibility == Visibility.Visible ? PasswordButton : ChangeServerButton;
        HttpPrompt.Confirmed += OnHttpConfirmed;
        HttpPrompt.Dismissed += OnHttpDismissed;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        _active = true;
        _inputPane = InputPane.GetForCurrentView();
        _inputPane.Showing += OnInputPaneShowing;
        _inputPane.Hiding += OnInputPaneHiding;
        try
        {
#if DEBUG_SERVER
            if (args.Parameter is ConnectionNavigation preview)
            {
                ShowPreview(preview);
                return;
            }
#endif
            _expiredNotice = args.Parameter as string == SessionExpired;
            if (args.Parameter as string == ServerEntry)
            {
                ShowServerEntry(null);
                return;
            }
            if (!ServerAddress.TryParse(AppSettings.ServerUrl ?? string.Empty, out var server))
            {
                ShowServerEntry(null);
                return;
            }
            await ResolveServerAsync(server);
        }
        catch (Exception ex)
        {
            // A corrupt/unwritable registry must not be replaced with empty state or destroy vault entries.
            Log.Info($"Connection startup blocked type={ex.GetType().Name}");
            StopConnectionWork();
            ShowServerEntry(null, "Unable to load the saved server list. Your saved session has not been removed.", discover: false);
            ConnectButton.IsEnabled = false;
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        _active = false;
        StopConnectionWork();
        HideKeyboard();
        if (_inputPane is { } pane)
        {
            pane.Showing -= OnInputPaneShowing;
            pane.Hiding -= OnInputPaneHiding;
            _inputPane = null;
        }
        base.OnNavigatedFrom(args);
    }

    private void StopConnectionWork()
    {
        _pairing?.Cancel();
        _loginLife?.Cancel();
        _probeLife?.Cancel();
        StopDiscovery();
        HttpPrompt.Hide();
        _httpPromptOpener = null;
        _pendingServer = null;
        ClearPassword();
        _nearby = [];
        NearbyPanel.Visibility = Visibility.Collapsed;
        SetPasswordBusy(false);
    }

    private async Task ResolveServerAsync(Uri server)
    {
        _loginServer = server;
        if (Servers.RequiresHttpApproval(server))
        {
            // Consent is deliberately not inferred from legacy tokens: keep them, never send them yet.
            ShowServerEntry(ServerAddress.ToBaseUrl(server));
            _pendingServer = new ServerSetupResult(server);
            _pendingActivation = false;
            Log.Info("HTTP credential gate awaiting exact-origin consent");
            ShowHttpPrompt(server);
            return;
        }
        if (!_expiredNotice && _preview is null && AppSettings.LoadRefreshToken(ServerAddress.ToBaseUrl(server)) is { } refresh)
        {
            StopDiscovery();
            Session.Restore(server, refresh); // Local adoption only, including the migrated access/profile proof.
            await Task.Yield();
            if (_active) Session.ContinueToHome(Frame);
            return;
        }
        await StartLoginAsync(server);
    }

    private async Task StartLoginAsync(Uri server)
    {
        _pairing?.Cancel();
        _loginLife?.Cancel();
        _loginLife?.Dispose();
        var lifetime = _loginLife = new CancellationTokenSource();
        _sessionAdopted = false;
        _loginServer = server;
        _passwordOnly = false;
        _options = new SignInOptions();
        SetPasswordBusy(false);
        PasswordButton.Visibility = Visibility.Visible;
        StopDiscovery();
        ShowCodeScreen(server);
        if (_preview is not null)
        {
            ShowPreviewCode();
            return;
        }
        if (Servers.RequiresHttpApproval(server)) throw new InvalidOperationException("HTTP approval is required.");
        var client = Session.Client is { } current && current.Server == server ? current : Session.Connect(server);
        _ = RefreshServerBrandingAsync(server, lifetime);
        try
        {
            using var options = new PasswordSignIn(server, DeviceIdentity.Current());
            var discoveredOptions = await options.GetOptionsAsync(lifetime.Token);
            if (!IsLoginCurrent(lifetime)) return;
            _options = discoveredOptions;
            PasswordButton.Visibility = _options.PasswordAvailable ? Visibility.Visible : Visibility.Collapsed;
            Log.Info($"Sign-in providers password={_options.PasswordAvailable}");
            var capability = await client.DeviceLogin.GetCapabilityAsync(lifetime.Token);
            if (!IsLoginCurrent(lifetime)) return;
            _passwordOnly = capability?.State != DeviceLoginCapability_state.Available;
            Log.Info($"Sign-in device capability available={!_passwordOnly}");
            if (_passwordOnly && _options.PasswordAvailable) ShowPasswordScreen();
            else if (_passwordOnly) ShowCodeFailure("This server doesn't have sign-in with a code turned on.", "Try again");
            else await StartPairingAsync(server);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Info($"Sign-in capability unavailable type={ex.GetType().Name}");
            if (IsLoginCurrent(lifetime)) ShowCodeFailure("Couldn't get a sign-in code.", "Try again");
        }
    }

    private async Task StartPairingAsync(Uri server)
    {
        if (_preview is not null)
        {
            ShowCodeScreen(server);
            ShowPreviewCode();
            return;
        }
        _pairing?.Cancel();
        using var pairing = CancellationTokenSource.CreateLinkedTokenSource(_loginLife!.Token);
        _pairing = pairing;
        var client = Session.Client!;
        DeviceLoginTicket? ticket = null;
        var approved = false;
        ShowCodeScreen(server);
        try
        {
            var identity = DeviceIdentity.Current();
            var renewed = false;
            while (IsCurrent(pairing))
            {
                ticket = await client.DeviceLogin.StartAsync(identity.DeviceName, identity.Platform, pairing.Token);
                if (!IsCurrent(pairing)) return;
                await ShowCodeAsync(ticket, pairing, renewed);
                if (!IsCurrent(pairing)) return;
                var result = await client.DeviceLogin.WaitForApprovalAsync(ticket,
                    onOpened: () => _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        if (IsCurrent(pairing)) PairingStatus.Text = "Continue on your phone";
                    }), cancellationToken: pairing.Token);
                if (!IsCurrent(pairing)) return;
                switch (result.Outcome)
                {
                    case DeviceLoginOutcome.Approved:
                        approved = true;
                        CompleteSignIn(client, result.Tokens!, result.ProfileId, result.ProfileToken);
                        return;
                    case DeviceLoginOutcome.Expired:
                        renewed = true;
                        QrImage.Source = null;
                        CodeText.Text = ActivateText.Text = string.Empty;
                        CodeLoadingRing.Visibility = Visibility.Visible;
                        CodeLoadingRing.IsActive = true;
                        PairingStatus.Text = "Getting a sign-in code…";
                        break;
                    case DeviceLoginOutcome.Denied:
                        ShowCodeFailure("Sign-in was declined.", "Show a new code");
                        return;
                    default:
                        ShowCodeFailure("Sign-in paused.", "Show a new code");
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (pairing.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Info($"Phone sign-in failed type={ex.GetType().Name}");
            if (IsCurrent(pairing)) ShowCodeFailure(ticket is null ? "Couldn't get a sign-in code." : "Couldn't finish signing in on this TV.", "Try again");
        }
        finally
        {
            if (ReferenceEquals(_pairing, pairing)) _pairing = null;
            if (ticket is not null && !approved && pairing.IsCancellationRequested)
            {
                try { await client.DeviceLogin.CancelAsync(ticket); }
                catch (Exception ex) { Log.Info($"Cancel phone sign-in unavailable type={ex.GetType().Name}"); }
            }
        }
    }

    private void CompleteSignIn(SiloClient client, TokenPair tokens, string? profileId = null, string? profileToken = null)
    {
        // Both completions run on the UI dispatcher. Only the first current-server success may save/route.
        if (!_active || _sessionAdopted || _preview is not null || !ReferenceEquals(Session.Client, client)
            || _loginServer != client.Server || Servers.Active?.Url != ServerAddress.ToBaseUrl(client.Server)
            || Servers.RequiresHttpApproval(client.Server)) return;
        _sessionAdopted = true;
        try
        {
            client.SignIn(tokens, profileId, profileToken);
            AppSettings.RenewPlaybackLogin();
            Session.Persist();
        }
        catch (Exception ex)
        {
            _sessionAdopted = false;
            Log.Info($"Sign-in save failed type={ex.GetType().Name}");
            if (PasswordPanel.Visibility == Visibility.Visible) ShowPasswordError("Unable to save this session. Try again.");
            else ShowCodeFailure("Unable to save this session. Try again.", "Try again");
            return;
        }
        Log.Info("Sign-in session saved; applying profile launch policy");
        _loginLife?.Cancel();
        ShowCodeApproved(tokens.User?.Username);
        Session.ContinueToHome(Frame);
    }

    private bool IsCurrent(CancellationTokenSource pairing) => _active && ReferenceEquals(_pairing, pairing) && !pairing.IsCancellationRequested && !_sessionAdopted;
    private bool IsLoginCurrent(CancellationTokenSource lifetime) => _active && ReferenceEquals(_loginLife, lifetime) && !lifetime.IsCancellationRequested && !_sessionAdopted;

    private void ShowCodeScreen(Uri server)
    {
        HideKeyboard();
        TvNameChip.Visibility = Visibility.Collapsed;
        ServerPanel.Visibility = Visibility.Collapsed;
        CodePanel.Visibility = Visibility.Visible;
        PasswordPanel.Visibility = Visibility.Collapsed;
        CodeActions.Visibility = PhoneHint.Visibility = CodeCard.Visibility = Visibility.Visible;
        PhoneHelpCard.Visibility = Visibility.Collapsed;
        ShowServerContext(new SignInServer(server, _preview is null ? AppSettings.GetServerName(ServerAddress.ToBaseUrl(server)) : "Example server",
            _preview is null ? AppSettings.GetServerBranding(server) : null));
        SetSignInInstructions(DeviceCodeFormat.ActivateText(ServerAddress.ToBaseUrl(server) + "/activate", string.Empty));
        SessionNotice.Visibility = _expiredNotice ? Visibility.Visible : Visibility.Collapsed;
        CodeDetails.Visibility = Visibility.Visible;
        CodeProblemIcon.Visibility = CodeApprovedIcon.Visibility = PairingError.Visibility = Visibility.Collapsed;
        PairingStatus.Visibility = Visibility.Visible;
        QrImage.Source = null;
        QrFallback.Visibility = Visibility.Collapsed;
        CodeText.Text = ActivateText.Text = string.Empty;
        CodeLoadingRing.Visibility = PairingRing.Visibility = Visibility.Visible;
        CodeLoadingRing.IsActive = PairingRing.IsActive = true;
        PairingStatus.Text = "Getting a sign-in code…";
        NewCodeButton.Visibility = Visibility.Collapsed;
        RestoreFocus();
    }

    private void ShowPreviewCode()
    {
        CodeText.Text = "000 000";
        ActivateText.Text = "example.com/activate";
        PairingStatus.Text = "Preview — no live sign-in request";
        CodeLoadingRing.IsActive = PairingRing.IsActive = false;
        CodeLoadingRing.Visibility = PairingRing.Visibility = Visibility.Collapsed;
        QrFallback.Visibility = Visibility.Visible;
        QrFallback.Text = "Preview\nNo live QR code";
    }

#if DEBUG_SERVER
    internal void ShowPreview(ConnectionNavigation request)
    {
        StopConnectionWork();
        _preview = request.Preview;
        _expiredNotice = request.Screen == ConnectionScreen.Expired;
        _loginServer = new Uri(_preview.Servers.Active?.Url ?? "https://example.com");
        _options = new SignInOptions();
        _passwordOnly = false;
        SetPasswordBusy(false);
        PasswordButton.Visibility = Visibility.Visible;
        SetConnecting(false);
        if (request.Screen is ConnectionScreen.Server or ConnectionScreen.HttpWarning or ConnectionScreen.Discovery)
        {
            ShowServerEntry(request.Screen == ConnectionScreen.HttpWarning ? "http://192.0.2.10:8090" : null);
            if (request.Screen == ConnectionScreen.HttpWarning)
            {
                _pendingServer = new ServerSetupResult(new Uri("http://192.0.2.10:8090"));
                _pendingActivation = true;
                ShowHttpPrompt(_pendingServer.Server!);
            }
            if (request.Screen == ConnectionScreen.Discovery) ShowNearbyPreview();
        }
        else
        {
            ShowCodeScreen(_loginServer);
            ShowPreviewCode();
            if (request.Screen == ConnectionScreen.Password) ShowPasswordScreen();
        }
    }
#endif

    private void ShowHttpPrompt(Uri server, Button? opener = null)
    {
        _httpPromptOpener = opener;
        HttpPrompt.ShowHttp(server.Authority);
    }

    private void OnHttpDismissed()
    {
        _pendingServer = null;
        var opener = _httpPromptOpener;
        _httpPromptOpener = null;
        Log.Info("HTTP consent cancelled; credentials untouched");
        if (HttpPrompt.IsOpen || !_active) return;
        // TvServerSetupScreen.kt: Compose AlertDialog restores its opener. Never focus a field
        // on dismissal: the activating Enter/A KeyUp can otherwise open the Xbox keyboard.
        FrameworkElement panel = ServerPanel.Visibility == Visibility.Visible ? ServerPanel
            : PasswordPanel.Visibility == Visibility.Visible ? PasswordPanel : CodePanel;
        FocusRetry.Until(panel, () =>
        {
            if (!_active || HttpPrompt.IsOpen || panel.Visibility != Visibility.Visible
                || (ReferenceEquals(panel, CodePanel) && PasswordPanel.Visibility == Visibility.Visible)) return true;
            if (opener is not null && SettingsInput.IsWithin(opener, panel) && opener.Focus(FocusState.Programmatic)) return true;
            Button primary;
            if (ReferenceEquals(panel, ServerPanel)) primary = ConnectButton;
            else if (ReferenceEquals(panel, PasswordPanel))
                primary = SignInButton.IsEnabled ? SignInButton : _passwordOnly ? PasswordChangeServerButton : UsePhoneButton;
            else primary = NewCodeButton.Visibility == Visibility.Visible ? NewCodeButton
                : PasswordButton.Visibility == Visibility.Visible ? PasswordButton : ChangeServerButton;
            return primary.Focus(FocusState.Programmatic);
        });
    }

    public void RestoreFocus()
    {
        if (HttpPrompt.IsOpen || !_active) return;
        if (ServerPanel.Visibility == Visibility.Visible)
            FocusRetry.Until(ServerPanel, () => !_active || HttpPrompt.IsOpen || ServerPanel.Visibility != Visibility.Visible || ServerBox.Focus(FocusState.Programmatic));
        else if (PasswordPanel.Visibility == Visibility.Visible)
            FocusRetry.Until(PasswordPanel, () => !_active || HttpPrompt.IsOpen || PasswordPanel.Visibility != Visibility.Visible || UsernameBox.Focus(FocusState.Programmatic));
        else
            FocusRetry.Until(CodePanel, () => !_active || CodePanel.Visibility != Visibility.Visible || PasswordPanel.Visibility == Visibility.Visible
                || (PasswordButton.Visibility == Visibility.Visible ? PasswordButton : ChangeServerButton).Focus(FocusState.Programmatic));
    }

    public bool OnBack()
    {
        if (_keyboardVisible) { HideKeyboard(); return true; }
        if (HttpPrompt.Dismiss()) return true;
        if (PasswordPanel.Visibility == Visibility.Visible && !_passwordOnly)
        {
            OnUsePhoneClick(this, new RoutedEventArgs());
            return true;
        }
#if DEBUG_SERVER
        if (IsAuthPreview) return DebugServer.CloseAuthPreview(Frame);
#endif
        if (ServerPanel.Visibility == Visibility.Visible && Frame.CanGoBack) Frame.GoBack();
        else Application.Current.Exit();
        return true;
    }

    private async void OnNewCodeClick(object sender, RoutedEventArgs args)
    {
        if (_loginServer is { } server) await StartLoginAsync(server);
    }

    private void OnChangeServerClick(object sender, RoutedEventArgs args)
    {
        StopConnectionWork();
        _expiredNotice = false;
        SetConnecting(false);
        ShowServerEntry(null);
    }

    private void OnServerBackClick(object sender, RoutedEventArgs args) => OnBack();
    private void OnCodeActionsPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (SettingsInput.VerticalStep(args.Key) != 0) args.Handled = true;
    }

    private void OnInputPaneShowing(InputPane sender, InputPaneVisibilityEventArgs args)
    {
        if (!_active) return;
        _keyboardVisible = true;
        AuthLayout.Padding = new Thickness(0, 0, 0, args.OccludedRect.Height);
        args.EnsuredFocusedElementInView = true;
        AuthLayout.UpdateLayout();
        if (FocusManager.GetFocusedElement() is FrameworkElement field)
            field.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    private void OnInputPaneHiding(InputPane sender, InputPaneVisibilityEventArgs args)
    {
        _keyboardVisible = false;
        AuthLayout.Padding = new Thickness(0);
    }

    private void HideKeyboard()
    {
        _inputPane?.TryHide();
        _keyboardVisible = false;
        AuthLayout.Padding = new Thickness(0);
    }

    private static bool IsUp(VirtualKey key) => SettingsInput.VerticalStep(key) < 0;
    private static bool IsDown(VirtualKey key) => SettingsInput.VerticalStep(key) > 0;
}
