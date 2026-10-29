using Bale.Core;
using Bale.Core.Api.Models;
using Bale.Core.Auth;
using Bale.Core.Connection;
using Bale.Xbox.Services;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>
/// First-run flow: pick a server, pair with a code, land signed in. Kept in code-behind for this
/// first milestone; screens move to view models once there is more than one.
/// </summary>
public sealed partial class MainPage : Page
{
    private SiloClient? _client;
    private CancellationTokenSource? _pairing;

    public MainPage()
    {
        InitializeComponent();
        SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var saved = AppSettings.ServerUrl;
        if (saved is not null && ServerAddress.TryParse(saved, out var server)
            && AppSettings.LoadRefreshToken(saved) is { } refreshToken)
        {
            ShowBusy();
            try
            {
                var client = NewClient(server);
                if (await client.ResumeAsync(refreshToken))
                {
                    AppSettings.SaveRefreshToken(saved, client.RefreshToken!);
                    ShowSignedIn();
                    return;
                }

                AppSettings.ForgetRefreshToken(saved);
            }
            catch (Exception)
            {
                // Offline or server down: fall through to the server screen with the address filled in.
            }
        }

        ShowServerEntry(saved);
    }

    private void OnServerBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnConnectClick(sender, e);
        }
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (!ServerAddress.TryParse(ServerBox.Text, out var server))
        {
            ShowServerError("That doesn't look like a server address.");
            return;
        }

        ShowBusy();
        var client = NewClient(server);
        try
        {
            var capability = await client.DeviceLogin.GetCapabilityAsync();
            if (capability?.State != DeviceLoginCapability_state.Available)
            {
                ShowServerEntry(ServerBox.Text, "This server doesn't have sign-in with a code turned on.");
                return;
            }

            await PairAsync(client);
        }
        catch (Exception ex)
        {
            ShowServerEntry(ServerBox.Text, $"Couldn't reach {server.Host}. {ex.Message}");
        }
    }

    private async Task PairAsync(SiloClient client)
    {
        _pairing?.Cancel();
        var pairing = _pairing = new CancellationTokenSource();

        var identity = DeviceIdentity.Current();
        var ticket = await client.DeviceLogin.StartAsync(identity.DeviceName, identity.Platform, pairing.Token);
        ShowCode(ticket);

        DeviceLoginResult result;
        try
        {
            result = await client.DeviceLogin.WaitForApprovalAsync(
                ticket,
                onOpened: () => _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => PairingStatus.Text = "Approve the request on your other device"),
                cancellationToken: pairing.Token);
        }
        catch (OperationCanceledException)
        {
            await client.DeviceLogin.CancelAsync(ticket);
            return;
        }

        switch (result.Outcome)
        {
            case DeviceLoginOutcome.Approved:
                client.SignIn(result.Tokens!);
                AppSettings.ServerUrl = ServerAddress.ToBaseUrl(client.Server);
                AppSettings.SaveRefreshToken(AppSettings.ServerUrl, client.RefreshToken!);
                ShowSignedIn();
                break;
            case DeviceLoginOutcome.Expired:
                // Codes are short-lived; quietly issue a fresh one.
                await PairAsync(client);
                break;
            default:
                ShowServerEntry(ServerBox.Text, "The sign-in request was declined.");
                break;
        }
    }

    private void OnCancelPairingClick(object sender, RoutedEventArgs e)
    {
        _pairing?.Cancel();
        ShowServerEntry(ServerBox.Text);
    }

    private async void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        if (_client is null)
        {
            return;
        }

        var server = ServerAddress.ToBaseUrl(_client.Server);
        ShowBusy();
        await _client.SignOutAsync();
        AppSettings.ForgetRefreshToken(server);
        ShowServerEntry(server);
    }

    private void OnBackRequested(object? sender, BackRequestedEventArgs e)
    {
        // B on the controller backs out of pairing instead of closing the app.
        if (CodePanel.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            OnCancelPairingClick(this, new RoutedEventArgs());
        }
    }

    private SiloClient NewClient(Uri server)
    {
        _client?.Dispose();
        return _client = new SiloClient(server, DeviceIdentity.Current());
    }

    private void ShowServerEntry(string? server, string? error = null)
    {
        ShowOnly(ServerPanel);
        ServerBox.Text = server ?? string.Empty;
        ServerError.Text = error ?? string.Empty;
        ServerError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        (string.IsNullOrEmpty(ServerBox.Text) ? (Control)ServerBox : ConnectButton).Focus(FocusState.Programmatic);
    }

    private void ShowServerError(string error) => ShowServerEntry(ServerBox.Text, error);

    private void ShowCode(DeviceLoginTicket ticket)
    {
        ShowOnly(CodePanel);
        ActivateText.Text = ticket.ActivateText;
        CodeText.Text = ticket.DisplayCode;
        PairingStatus.Text = "Waiting for approval";
        CancelPairingButton.Focus(FocusState.Programmatic);
    }

    private void ShowSignedIn()
    {
        ShowOnly(SignedInPanel);
        WelcomeText.Text = $"Signed in as {_client?.Account?.Username}";
        ServerText.Text = _client?.Server.Host ?? string.Empty;
        SignOutButton.Focus(FocusState.Programmatic);
    }

    private void ShowBusy() => ShowOnly(BusyRing);

    private void ShowOnly(FrameworkElement visible)
    {
        foreach (var panel in new FrameworkElement[] { ServerPanel, CodePanel, SignedInPanel, BusyRing })
        {
            panel.Visibility = panel == visible ? Visibility.Visible : Visibility.Collapsed;
        }

        BusyRing.IsActive = visible == BusyRing;
    }
}
