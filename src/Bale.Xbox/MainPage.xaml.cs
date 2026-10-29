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

/// <summary>Sign-in: pick a server, pair with a code, then on to profiles and Home.</summary>
public sealed partial class MainPage : Page, IBackHandler
{
    private CancellationTokenSource? _pairing;

    public MainPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var saved = AppSettings.ServerUrl;
        if (Session.Client is null && saved is not null && ServerAddress.TryParse(saved, out var server)
            && AppSettings.LoadRefreshToken(saved) is { } refreshToken)
        {
            ShowBusy();
            try
            {
                var client = Session.Connect(server);
                if (await client.ResumeAsync(refreshToken))
                {
                    Session.Persist();
                    await Session.ContinueToHomeAsync(Frame);
                    return;
                }

                AppSettings.ForgetRefreshToken(saved);
            }
            catch (Exception ex)
            {
                // Offline or server down: fall through to the server screen with the address filled in.
                Log.Error("Resume", ex);
            }
        }

        ShowServerEntry(saved);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _pairing?.Cancel();
    }

    /// <summary>B on the controller backs out of pairing instead of closing the app.</summary>
    public bool OnBack()
    {
        if (CodePanel.Visibility != Visibility.Visible)
        {
            return false;
        }

        OnCancelPairingClick(this, new RoutedEventArgs());
        return true;
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
            ShowServerEntry(ServerBox.Text, "That doesn't look like a server address.");
            return;
        }

        ShowBusy();
        var client = Session.Connect(server);
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
            Log.Error("Connect", ex);
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
                client.SignIn(result.Tokens!, result.ProfileId, result.ProfileToken);
                Session.Persist();
                ShowBusy();
                await Session.ContinueToHomeAsync(Frame);
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

    private void ShowServerEntry(string? server, string? error = null)
    {
        ShowOnly(ServerPanel);
        ServerBox.Text = server ?? string.Empty;
        ServerError.Text = error ?? string.Empty;
        ServerError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        (string.IsNullOrEmpty(ServerBox.Text) ? (Control)ServerBox : ConnectButton).Focus(FocusState.Programmatic);
    }

    private void ShowCode(DeviceLoginTicket ticket)
    {
        ShowOnly(CodePanel);
        ActivateText.Text = ticket.ActivateText;
        CodeText.Text = ticket.DisplayCode;
        PairingStatus.Text = "Waiting for approval";
        CancelPairingButton.Focus(FocusState.Programmatic);
    }

    private void ShowBusy() => ShowOnly(BusyRing);

    private void ShowOnly(FrameworkElement visible)
    {
        foreach (var panel in new FrameworkElement[] { ServerPanel, CodePanel, BusyRing })
        {
            panel.Visibility = panel == visible ? Visibility.Visible : Visibility.Collapsed;
        }

        BusyRing.IsActive = visible == BusyRing;
    }
}
