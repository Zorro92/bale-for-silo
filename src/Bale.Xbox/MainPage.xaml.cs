using Bale.Core;
using Bale.Core.Api.Models;
using Bale.Core.Auth;
using Bale.Core.Connection;
using Bale.Xbox.Services;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>Server entry, phone sign-in, then the local profile launch policy.</summary>
public sealed partial class MainPage : Page, IBackHandler
{
    public const string SessionExpired = "session-expired";
    public const string ServerEntry = "server-entry";

    private CancellationTokenSource? _pairing;
    private Uri? _loginServer;
    private bool _active;
    private bool _connecting;
    private bool _expiredNotice;
    private InputPane? _inputPane;
    private bool _keyboardVisible;

#if DEBUG_SERVER
    private DebugServer.AuthPreview? _preview;

    internal bool IsAuthPreview => _preview is not null;

    // Debug-only navigation must never restore, replace or persist the live session.
    internal void ShowPreview(DebugServer.AuthPreview preview)
    {
        _preview = preview;
        _expiredNotice = preview == DebugServer.AuthPreview.Expired;
        _pairing?.Cancel();
        SetConnecting(false);
        if (Session.Client is not { } client) return;
        _loginServer = client.Server;
        if (preview == DebugServer.AuthPreview.Server)
        {
            ShowServerEntry(ServerAddress.ToBaseUrl(client.Server));
        }
        else
        {
            _ = StartPairingAsync(client.Server);
        }
    }
#endif

    public MainPage()
    {
        InitializeComponent();
        // TvServerSetupScreen.kt rememberTvName(): the model/friendly name of this TV, not the server.
        TvNameText.Text = DeviceIdentity.Current().DeviceName;
        Rail.Attach(ServerActions).Pin = false;
        var codeActions = Rail.Attach(CodeActions);
        codeActions.Pin = false;
        // TvLoginScreen.kt focuses only actions offered by the current status, never a hidden retry.
        codeActions.EntryItem = () => NewCodeButton.Visibility == Visibility.Visible ? NewCodeButton : ChangeServerButton;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _active = true;
        _inputPane = InputPane.GetForCurrentView();
        _inputPane.Showing += OnInputPaneShowing;
        _inputPane.Hiding += OnInputPaneHiding;
#if DEBUG_SERVER
        if (e.Parameter is DebugServer.AuthPreview preview)
        {
            ShowPreview(preview);
            return;
        }
#endif
        _expiredNotice = e.Parameter as string == SessionExpired;
        var saved = AppSettings.ServerUrl;
        if (!ServerAddress.TryParse(saved ?? string.Empty, out var server))
        {
            ShowServerEntry(saved);
            return;
        }

        _loginServer = server;
        if (e.Parameter as string == ServerEntry)
        {
            ShowServerEntry(saved);
            return;
        }

        if (!_expiredNotice && AppSettings.LoadRefreshToken(ServerAddress.ToBaseUrl(server)) is { } refreshToken)
        {
            // MainTvActivity.kt routes from local tokens, not a blocking network refresh. An offline
            // TV stays in the picker/shell with Retry; only a real refresh 401 returns to sign-in.
            ServerPanel.Visibility = Visibility.Collapsed;
            Session.Restore(server, refreshToken);
            await Task.Yield();
            if (_active)
            {
                Session.ContinueToHome(Frame);
            }
            return;
        }

        await StartPairingAsync(server);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _active = false;
        _pairing?.Cancel();
        HideKeyboard();
        if (_inputPane is { } pane)
        {
            pane.Showing -= OnInputPaneShowing;
            pane.Hiding -= OnInputPaneHiding;
            _inputPane = null;
        }
    }

    public bool OnBack()
    {
        // TvServerSetupScreen.kt: Back first dismisses the stock IME, not the address screen.
        if (_keyboardVisible)
        {
            HideKeyboard();
            return true;
        }
#if DEBUG_SERVER
        if (_preview is not null)
        {
            return DebugServer.CloseAuthPreview(Frame);
        }
#endif
        if (CodePanel.Visibility == Visibility.Visible)
        {
            // TvAppNavigation.kt replaces ServerSetup with Login. TvLoginScreen.kt intercepts Back
            // only for password/nearby panels (not present here); the code screen is the app root.
            _pairing?.Cancel();
            Application.Current.Exit();
            return true;
        }

        if (_loginServer is { } server)
        {
            _ = StartPairingAsync(server);
        }
        else
        {
            // TvServerSetupScreen.kt returns from manual entry to phone setup. Until that separate
            // flow exists, Back from first-run manual entry leaves Bale rather than becoming inert.
            Application.Current.Exit();
        }

        return true;
    }

    private void OnServerTextChanged(object sender, TextChangedEventArgs e)
    {
        if (ServerError is null || ServerSecurityHint is null) return;
        // TvServerSetupViewModel.kt clears the validation error as soon as the address changes.
        ServerError.Visibility = Visibility.Collapsed;
        // TvServerSetupScreen.kt AddressEntryScreen: this is information, not the later HTTP consent flow.
        ServerSecurityHint.Text = ServerBox.Text.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "This address uses unencrypted HTTP. Fine on a trusted home network; avoid it on public Wi-Fi."
            : "Secure HTTPS is tried automatically.";
    }

    private void OnInputPaneShowing(InputPane sender, InputPaneVisibilityEventArgs args)
    {
        if (!_active || ServerPanel.Visibility != Visibility.Visible) return;
        _keyboardVisible = true;
        // TvServerSetupScreen.kt imePadding + TvMarqueeScreen's copyScroll: shrink the deck,
        // then reveal the editing field rather than letting the IME cover it or pan the whole page.
        AuthLayout.Padding = new Thickness(0, 0, 0, args.OccludedRect.Height);
        args.EnsuredFocusedElementInView = true;
        AuthLayout.UpdateLayout();
        ServerBox.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
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

    private void OnServerBoxPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_keyboardVisible)
        {
            if (e.Key == VirtualKey.Enter)
            {
                // TvMarqueeField.kt ImeAction.Go submits. Other keys belong to the stock IME.
                e.Handled = true;
                OnConnectClick(sender, e);
            }
            return;
        }
        if (e.Key is VirtualKey.Enter or VirtualKey.GamepadA)
        {
            e.Handled = true;
            // TvMarqueeField.kt tvShowImeOnSelect: a closed field opens IME, never Connect.
            if (!_connecting) _inputPane?.TryShow();
        }
        else if (IsDown(e.Key))
        {
            e.Handled = true;
            ConnectButton.Focus(FocusState.Keyboard);
        }
        else if (IsUp(e.Key) || e.Key is VirtualKey.Left or VirtualKey.Right
            or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadDPadRight
            or VirtualKey.GamepadLeftThumbstickLeft or VirtualKey.GamepadLeftThumbstickRight)
        {
            // TvMarqueeField.kt doesn't move an invisible caret with the keyboard closed.
            e.Handled = true;
        }
    }

    private void OnServerActionsPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsUp(e.Key))
        {
            e.Handled = true;
            ServerBox.Focus(FocusState.Programmatic);
        }
        else if (IsDown(e.Key))
        {
            e.Handled = true;
        }
    }

    private void OnCodeActionsPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsUp(e.Key) || IsDown(e.Key))
        {
            e.Handled = true; // The code card isn't focusable; a vertical edge never shifts sideways.
        }
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_connecting)
        {
            return;
        }

        HideKeyboard();
        if (!ServerAddress.TryParse(ServerBox.Text, out var server))
        {
            // TvServerSetupScreen.kt AddressEntryScreen submits blank input to validation too.
            ShowServerEntry(ServerBox.Text, string.IsNullOrWhiteSpace(ServerBox.Text)
                ? "Please enter a server host." : "That doesn't look like a server address.");
            return;
        }

        await StartPairingAsync(server, fromServerEntry: true);
    }

    private async void OnNewCodeClick(object sender, RoutedEventArgs e)
    {
        if (_loginServer is { } server)
        {
            await StartPairingAsync(server);
        }
    }

    private void OnChangeServerClick(object sender, RoutedEventArgs e)
    {
        _pairing?.Cancel();
        SetConnecting(false);
        _expiredNotice = false;
        ShowServerEntry(_loginServer is { } server ? ServerAddress.ToBaseUrl(server) : AppSettings.ServerUrl);
    }

    private void OnServerBackClick(object sender, RoutedEventArgs e) => OnBack();

    private void SetConnecting(bool connecting)
    {
        // TvServerSetupScreen.kt fixes the address during its probe but leaves the action focusable.
        _connecting = connecting;
        ServerBox.IsReadOnly = connecting;
        ConnectButton.Content = connecting ? "Connecting…" : "Connect";
    }

    private async Task StartPairingAsync(Uri server, bool fromServerEntry = false)
    {
        _pairing?.Cancel();
        using var pairing = new CancellationTokenSource();
        _pairing = pairing;
#if DEBUG_SERVER
        // A preview can request and poll a real code, but owns a separate, unauthenticated client.
        // Neither a changed address nor a late approval can dispose/overwrite the signed-in client.
        using var previewClient = _preview is not null ? new SiloClient(server, DeviceIdentity.Current()) : null;
        var client = previewClient ?? (Session.Client is { } existing && existing.Server == server ? existing : Session.Connect(server));
#else
        var client = Session.Client is { } existing && existing.Server == server ? existing : Session.Connect(server);
#endif
        DeviceLoginTicket? ticket = null;
        var showingCode = !fromServerEntry;
        var approved = false;
        if (fromServerEntry)
        {
            SetConnecting(true);
        }
        if (showingCode)
        {
            ShowCodeScreen(server);
            _ = RefreshServerBrandingAsync(server, pairing);
        }

        try
        {
            var capability = await client.DeviceLogin.GetCapabilityAsync(pairing.Token);
            if (!IsCurrent(pairing))
            {
                return;
            }

            if (capability?.State != DeviceLoginCapability_state.Available)
            {
                throw new InvalidOperationException("This server doesn't have sign-in with a code turned on.");
            }

            _loginServer = server;
#if DEBUG_SERVER
            if (_preview is null)
#endif
            AppSettings.ServerUrl = ServerAddress.ToBaseUrl(server);
            SetConnecting(false);
            if (!showingCode)
            {
                showingCode = true;
                ShowCodeScreen(server);
                _ = RefreshServerBrandingAsync(server, pairing);
            }

            var identity = DeviceIdentity.Current();
            var renewed = false;
            while (IsCurrent(pairing))
            {
                ticket = await client.DeviceLogin.StartAsync(identity.DeviceName, identity.Platform, pairing.Token);
                if (!IsCurrent(pairing))
                {
                    return;
                }

                await ShowCodeAsync(ticket, pairing, renewed);
                if (!IsCurrent(pairing))
                {
                    return;
                }

                var result = await client.DeviceLogin.WaitForApprovalAsync(ticket,
                    onOpened: () => _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        if (IsCurrent(pairing))
                        {
                            // TvLoginScreen.kt statusText / tv_signin_status_opened in strings.xml.
                            PairingStatus.Text = "Continue on your phone";
                        }
                    }), cancellationToken: pairing.Token);
                if (!IsCurrent(pairing))
                {
                    return;
                }

                switch (result.Outcome)
                {
                    case DeviceLoginOutcome.Approved:
                        approved = true;
#if DEBUG_SERVER
                        // Preview approvals are deliberately ignored: no token adoption, vault writes or routing.
                        if (previewClient is not null)
                        {
                            ShowCodeApproved(result.Tokens?.User?.Username);
                            return;
                        }
#endif
                        client.SignIn(result.Tokens!, result.ProfileId, result.ProfileToken);
                        Session.Persist();
                        ShowCodeApproved(result.Tokens?.User?.Username);
                        Session.ContinueToHome(Frame);
                        return;
                    case DeviceLoginOutcome.Expired:
                        // TvLoginViewModel.kt publishes NewCode after expiry; no countdown or focus grab.
                        renewed = true;
                        QrImage.Source = null;
                        CodeText.Text = string.Empty;
                        ActivateText.Text = string.Empty;
                        CodeLoadingRing.Visibility = Visibility.Visible;
                        CodeLoadingRing.IsActive = true;
                        PairingStatus.Text = "Getting a sign-in code…";
                        break;
                    case DeviceLoginOutcome.Denied:
                        // TvLoginScreen.kt withdraws a declined QR and stays with the retry action focused.
                        SignInInstructions.Text = "Show a new code to try again.";
                        ShowCodeFailure("Sign-in was declined.", "Show a new code");
                        return;
                    default:
                        ShowCodeFailure("Sign-in paused.", "Show a new code");
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (pairing.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Phone sign-in", ex);
            if (IsCurrent(pairing))
            {
                if (showingCode)
                {
                    // TvLoginScreen.kt statusText: Failed before a code, CouldntFinish afterwards.
                    ShowCodeFailure(ticket is null ? "Couldn't get a sign-in code." : "Couldn't finish signing in on this TV.", "Try again");
                }
                else
                {
                    ShowServerEntry(ServerBox.Text, $"Couldn't connect. {ex.Message}");
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_pairing, pairing))
            {
                _pairing = null;
                SetConnecting(false);
            }

            if (ticket is not null && !approved && pairing.IsCancellationRequested)
            {
                try
                {
                    await client.DeviceLogin.CancelAsync(ticket);
                }
                catch (Exception ex)
                {
                    // Navigation may already have disposed the old server connection.
                    Log.Error("Cancel phone sign-in", ex);
                }
            }
        }
    }

    private bool IsCurrent(CancellationTokenSource pairing) =>
        _active && ReferenceEquals(_pairing, pairing) && !pairing.IsCancellationRequested;

    private void ShowServerEntry(string? server, string? error = null)
    {
        HideKeyboard();
        TvNameChip.Visibility = Visibility.Visible;
        ServerPanel.Visibility = Visibility.Visible;
        CodePanel.Visibility = Visibility.Collapsed;
        ServerBox.Text = server ?? string.Empty;
        ServerError.Text = error ?? string.Empty;
        ServerError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        // TvServerSetupScreen.kt AddressEntryScreen claims the address field, even when prefilled.
        FocusRetry.Until(ServerPanel, () => !_active || ServerPanel.Visibility != Visibility.Visible
            || ServerBox.Focus(FocusState.Programmatic));
    }

    private void ShowCodeScreen(Uri server)
    {
        var entering = CodePanel.Visibility != Visibility.Visible;
        HideKeyboard();
        TvNameChip.Visibility = Visibility.Collapsed;
        ServerPanel.Visibility = Visibility.Collapsed;
        CodePanel.Visibility = Visibility.Visible;
        ShowServerContext(new SignInServer(server, AppSettings.GetServerName(ServerAddress.ToBaseUrl(server)), AppSettings.GetServerBranding(server)));
        // TvLoginScreen.kt: use the server's activate URL even before the first code arrives.
        SetSignInInstructions(DeviceCodeFormat.ActivateText(ServerAddress.ToBaseUrl(server) + "/activate", string.Empty));
        SessionNotice.Visibility = _expiredNotice ? Visibility.Visible : Visibility.Collapsed;
        CodeDetails.Visibility = Visibility.Visible;
        CodeProblemIcon.Visibility = Visibility.Collapsed;
        CodeApprovedIcon.Visibility = Visibility.Collapsed;
        PairingStatus.Visibility = Visibility.Visible;
        PairingError.Visibility = Visibility.Collapsed;
        QrImage.Source = null;
        QrFallback.Visibility = Visibility.Collapsed;
        CodeText.Text = string.Empty;
        ActivateText.Text = string.Empty;
        CodeLoadingRing.Visibility = Visibility.Visible;
        CodeLoadingRing.IsActive = true;
        PairingRing.Visibility = Visibility.Visible;
        PairingRing.IsActive = true;
        PairingStatus.Text = "Getting a sign-in code…";
        var retryFocused = NewCodeButton.FocusState != FocusState.Unfocused;
        NewCodeButton.Visibility = Visibility.Collapsed;
        if (entering || retryFocused)
        {
            // TvLoginScreen.kt CodeActions: only a problem offers a new-code action. With password
            // sign-in deferred, Change server is the first available action on a live code screen.
            FocusRetry.Until(CodePanel, () => !_active || CodePanel.Visibility != Visibility.Visible || ChangeServerButton.Focus(FocusState.Programmatic));
        }
    }

    private void ShowServerContext(SignInServer server)
    {
        // TvMarquee.kt TvMarqueeServerCard + SignInServer.kt: branded/saved name, host and TLS badge.
        ServerNameText.Text = server.Name;
        ServerAddressText.Text = server.Address;
        ServerInitialText.Text = server.Initial;
        ServerInitialMark.Visibility = server.Initial.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // MarqueeServerMark.kt keeps its initial underneath the optional image, including on failure.
        // An ImageBrush clips to the Border's radius; a child Image would keep square corners in UWP.
        if (ServerMarkBrush.ImageSource is not BitmapImage current || current.UriSource != server.MarkUrl)
        {
            ServerImageMark.Visibility = Visibility.Collapsed;
            ServerMarkBrush.ImageSource = null;
            if (server.MarkUrl is { } mark)
            {
                var image = new BitmapImage();
                image.ImageOpened += (_, _) =>
                {
                    if (ReferenceEquals(ServerMarkBrush.ImageSource, image)) ServerImageMark.Visibility = Visibility.Visible;
                };
                image.ImageFailed += (_, _) =>
                {
                    if (ReferenceEquals(ServerMarkBrush.ImageSource, image)) ServerImageMark.Visibility = Visibility.Collapsed;
                };
                ServerMarkBrush.ImageSource = image;
                image.UriSource = mark;
            }
        }
        SecureBadge.Visibility = server.IsSecure ? Visibility.Visible : Visibility.Collapsed;
        HttpBadge.Visibility = server.IsSecure ? Visibility.Collapsed : Visibility.Visible;
        SignInTitle.Text = $"Sign in to {server.Name}";
        // TvLoginScreen.kt notices / tv_signin_session_expired in strings.xml, using the same display name.
        SessionNoticeText.Text = $"You were signed out of {server.Name}. Sign in again.";
    }

    private async Task RefreshServerBrandingAsync(Uri server, CancellationTokenSource pairing)
    {
        try
        {
            var branding = await ServerBranding.FetchAsync(server, pairing.Token);
            if (branding is null || !IsCurrent(pairing)) return;
#if DEBUG_SERVER
            if (_preview is null)
#endif
            AppSettings.SetServerBranding(ServerAddress.ToBaseUrl(server), branding);
            if (_loginServer == server && CodePanel.Visibility == Visibility.Visible)
            {
                ShowServerContext(new SignInServer(server, AppSettings.GetServerName(ServerAddress.ToBaseUrl(server)), branding));
            }
        }
        catch (OperationCanceledException) when (pairing.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // SignInServer.kt treats optional branding as a fallback, never a sign-in failure.
            Log.Error("Sign-in server branding", ex);
        }
    }

    private void SetSignInInstructions(string activate) =>
        SignInInstructions.Text = $"Scan the code with your phone's camera, or go to {activate} and enter it. "
            + "Approve on your phone and this TV signs in by itself.";

    private async Task ShowCodeAsync(DeviceLoginTicket ticket, CancellationTokenSource pairing, bool renewed)
    {
        ActivateText.Text = ticket.ActivateText;
        // TvMarqueeCodeTiles widens the grouping space to an en space; the underlying code is unchanged.
        CodeText.Text = ticket.DisplayCode.Replace(' ', ' ');
        SetSignInInstructions(ticket.ActivateText);
        PairingStatus.Text = renewed ? "New code. Waiting for approval." : "Waiting for approval";
        QrImage.Source = null;
        try
        {
            // PngByteQRCode is managed byte-only rendering; BitmapImage decodes through UWP, not GDI+.
            var bytes = DeviceLoginQrCode.Png(ticket);
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            if (IsCurrent(pairing))
            {
                QrImage.Source = image;
                QrFallback.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Sign-in QR", ex);
            if (IsCurrent(pairing))
            {
                QrFallback.Visibility = Visibility.Visible;
            }
        }

        if (IsCurrent(pairing))
        {
            CodeLoadingRing.IsActive = false;
            CodeLoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowCodeApproved(string? accountName)
    {
        // TvLoginScreen.kt CodeCard / strings.xml: withdraw the QR and confirm the saved account.
        CodeDetails.Visibility = Visibility.Collapsed;
        CodeApprovedIcon.Visibility = Visibility.Visible;
        PairingRing.IsActive = false;
        PairingRing.Visibility = Visibility.Collapsed;
        PairingStatus.Text = string.IsNullOrWhiteSpace(accountName) ? "Signed in" : $"Signed in as {accountName}";
    }

    private void ShowCodeFailure(string message, string action)
    {
        // TvLoginScreen.kt CodeCard: denied/failed codes are no longer live and must not remain scannable.
        QrImage.Source = null;
        CodeDetails.Visibility = Visibility.Collapsed;
        CodeProblemIcon.Visibility = Visibility.Visible;
        PairingRing.IsActive = false;
        PairingRing.Visibility = Visibility.Collapsed;
        CodeLoadingRing.IsActive = false;
        CodeLoadingRing.Visibility = Visibility.Collapsed;
        PairingStatus.Visibility = Visibility.Collapsed;
        PairingError.Text = message;
        PairingError.Visibility = Visibility.Visible;
        NewCodeButton.Content = action;
        NewCodeButton.Visibility = Visibility.Visible;
        FocusRetry.Until(CodePanel, () => !_active || CodePanel.Visibility != Visibility.Visible || NewCodeButton.Focus(FocusState.Programmatic));
    }

    private static bool IsUp(VirtualKey key) => key is VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp;

    private static bool IsDown(VirtualKey key) => key is VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown;
}
