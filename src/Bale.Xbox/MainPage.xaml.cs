using Bale.Core;
using Bale.Core.Api.Models;
using Bale.Core.Auth;
using Bale.Core.Connection;
using Bale.Xbox.Services;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
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

    public MainPage()
    {
        InitializeComponent();
        Rail.Attach(ServerActions).Pin = false;
        Rail.Attach(CodeActions).Pin = false;
        PreviewKeyDown += OnPagePreviewKeyDown;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _active = true;
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
    }

    public bool OnBack()
    {
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
            return true;
        }

        return false;
    }

    private void OnPagePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = OnBack();
        }
    }

    private void OnServerBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnConnectClick(sender, e);
        }
        else if (IsDown(e.Key))
        {
            e.Handled = true;
            ConnectButton.Focus(FocusState.Keyboard);
        }
        else if (IsUp(e.Key))
        {
            e.Handled = true;
        }
    }

    private void OnServerActionsPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsUp(e.Key))
        {
            e.Handled = true;
            ServerBox.Focus(FocusState.Keyboard);
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

        if (!ServerAddress.TryParse(ServerBox.Text, out var server))
        {
            ShowServerEntry(ServerBox.Text, "That doesn't look like a server address.");
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
        _connecting = false;
        _expiredNotice = false;
        ConnectButton.Content = "Connect";
        ShowServerEntry(_loginServer is { } server ? ServerAddress.ToBaseUrl(server) : AppSettings.ServerUrl);
    }

    private async void OnServerBackClick(object sender, RoutedEventArgs e)
    {
        if (_loginServer is { } server)
        {
            await StartPairingAsync(server);
        }
    }

    private async Task StartPairingAsync(Uri server, bool fromServerEntry = false)
    {
        _pairing?.Cancel();
        using var pairing = new CancellationTokenSource();
        _pairing = pairing;
        var client = Session.Client is { } existing && existing.Server == server ? existing : Session.Connect(server);
        DeviceLoginTicket? ticket = null;
        var showingCode = !fromServerEntry;
        var approved = false;
        if (fromServerEntry)
        {
            _connecting = true;
            ConnectButton.Content = "Connecting…";
        }
        if (showingCode)
        {
            ShowCodeScreen(server);
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
            AppSettings.ServerUrl = ServerAddress.ToBaseUrl(server);
            _connecting = false;
            ConnectButton.Content = "Connect";
            if (!showingCode)
            {
                showingCode = true;
                ShowCodeScreen(server);
            }

            var identity = DeviceIdentity.Current();
            while (IsCurrent(pairing))
            {
                ticket = await client.DeviceLogin.StartAsync(identity.DeviceName, identity.Platform, pairing.Token);
                if (!IsCurrent(pairing))
                {
                    return;
                }

                await ShowCodeAsync(ticket, pairing);
                if (!IsCurrent(pairing))
                {
                    return;
                }

                var result = await client.DeviceLogin.WaitForApprovalAsync(ticket,
                    onOpened: () => _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        if (IsCurrent(pairing))
                        {
                            PairingStatus.Text = "Approve the request on your phone.";
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
                        client.SignIn(result.Tokens!, result.ProfileId, result.ProfileToken);
                        Session.Persist();
                        Session.ContinueToHome(Frame);
                        return;
                    case DeviceLoginOutcome.Expired:
                        // TvLoginScreen.kt renews in place without a countdown or another focus grab.
                        PairingStatus.Text = "Getting a new code…";
                        break;
                    case DeviceLoginOutcome.Denied:
                        ShowCodeFailure("Sign-in was declined on your phone.", "Show a new code");
                        return;
                    default:
                        ShowCodeFailure("Sign-in was canceled. Show a new code to try again.", "Show a new code");
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
                    ShowCodeFailure($"Couldn't finish signing in. {ex.Message}", "Try again");
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
                _connecting = false;
                ConnectButton.Content = "Connect";
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
        ServerPanel.Visibility = Visibility.Visible;
        CodePanel.Visibility = Visibility.Collapsed;
        ServerBox.Text = server ?? string.Empty;
        ServerError.Text = error ?? string.Empty;
        ServerError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        ServerBackButton.Visibility = _loginServer is null ? Visibility.Collapsed : Visibility.Visible;
        FocusRetry.Until(ServerPanel, () => !_active || ServerPanel.Visibility != Visibility.Visible
            || (string.IsNullOrEmpty(ServerBox.Text) ? (Control)ServerBox : ConnectButton).Focus(FocusState.Programmatic));
    }

    private void ShowCodeScreen(Uri server)
    {
        var entering = CodePanel.Visibility != Visibility.Visible;
        ServerPanel.Visibility = Visibility.Collapsed;
        CodePanel.Visibility = Visibility.Visible;
        SignInTitle.Text = $"Sign in to {server.Host}";
        SignInInstructions.Text = "Scan the code with your phone's camera, or go to your server's /activate page and enter it. "
            + "Approve on your phone and this TV signs in by itself.";
        SessionNotice.Text = $"You were signed out of {server.Host}. Sign in again.";
        SessionNotice.Visibility = _expiredNotice ? Visibility.Visible : Visibility.Collapsed;
        QrImage.Source = null;
        QrFallback.Visibility = Visibility.Collapsed;
        CodeText.Text = string.Empty;
        ActivateText.Text = string.Empty;
        CodeLoadingRing.Visibility = Visibility.Visible;
        CodeLoadingRing.IsActive = true;
        PairingRing.Visibility = Visibility.Visible;
        PairingRing.IsActive = true;
        PairingStatus.Text = "Getting a code…";
        NewCodeButton.Content = "Show a new code";
        if (entering)
        {
            FocusRetry.Until(CodePanel, () => !_active || CodePanel.Visibility != Visibility.Visible || NewCodeButton.Focus(FocusState.Programmatic));
        }
    }

    private async Task ShowCodeAsync(DeviceLoginTicket ticket, CancellationTokenSource pairing)
    {
        ActivateText.Text = ticket.ActivateText;
        CodeText.Text = ticket.DisplayCode;
        SignInInstructions.Text = $"Scan the code with your phone's camera, or go to {ticket.ActivateText} and enter it. "
            + "Approve on your phone and this TV signs in by itself.";
        PairingStatus.Text = "Waiting for approval";
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

    private void ShowCodeFailure(string message, string action)
    {
        PairingRing.IsActive = false;
        PairingRing.Visibility = Visibility.Collapsed;
        CodeLoadingRing.IsActive = false;
        CodeLoadingRing.Visibility = Visibility.Collapsed;
        PairingStatus.Text = message;
        NewCodeButton.Content = action;
        FocusRetry.Until(CodePanel, () => !_active || CodePanel.Visibility != Visibility.Visible || NewCodeButton.Focus(FocusState.Programmatic));
    }

    private static bool IsUp(VirtualKey key) => key is VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp;

    private static bool IsDown(VirtualKey key) => key is VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown;
}
