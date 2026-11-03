using Bale.Core.Auth;
using Bale.Core.Connection;
using Bale.Xbox.Services;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace Bale.Xbox;

public sealed partial class MainPage
{
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
        if (PasswordPanel.Visibility != Visibility.Visible) SignInTitle.Text = $"Sign in to {server.Name}";
        // TvLoginScreen.kt notices / tv_signin_session_expired in strings.xml, using the same display name.
        SessionNoticeText.Text = $"You were signed out of {server.Name}. Sign in again.";
    }

    private async Task RefreshServerBrandingAsync(Uri server, CancellationTokenSource pairing)
    {
        try
        {
            var branding = await ServerBranding.FetchAsync(server, pairing.Token);
            if (branding is null || !IsLoginCurrent(pairing)) return;
            if (_preview is null) AppSettings.SetServerBranding(ServerAddress.ToBaseUrl(server), branding);
            if (_loginServer == server && CodePanel.Visibility == Visibility.Visible)
            {
                ShowServerContext(new SignInServer(server, branding.ServerName, branding));
                UpdatePasswordCopy();
            }
        }
        catch (OperationCanceledException) when (pairing.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // SignInServer.kt treats optional branding as a fallback, never a sign-in failure.
            Log.Info($"Sign-in branding unavailable type={ex.GetType().Name}");
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
        if (PasswordPanel.Visibility != Visibility.Visible) SetSignInInstructions(ticket.ActivateText);
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
        if (PasswordPanel.Visibility != Visibility.Visible)
            FocusRetry.Until(CodePanel, () => !_active || CodePanel.Visibility != Visibility.Visible || PasswordPanel.Visibility == Visibility.Visible || NewCodeButton.Focus(FocusState.Programmatic));
    }

}
