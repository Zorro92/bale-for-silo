using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

public sealed partial class ShellPage
{
    private CancellationTokenSource? _requestsProbe;

    /// <summary>TvMainShell.kt / RequestsFeatureStore.kt: profile-menu entry, hidden until positively enabled.</summary>
    private async Task RefreshRequestsCapabilityAsync()
    {
        _requestsProbe?.Cancel();
        _requestsProbe?.Dispose();
        if (Session.Client is not { } client)
        {
            RequestsRow.Visibility = Visibility.Collapsed;
            return;
        }
        var profile = client.ProfileId;
        var cancellation = new CancellationTokenSource();
        _requestsProbe = cancellation;
        RequestsRow.Visibility = client.Requests.Capability.Enabled ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            var capability = await client.Requests.RefreshCapabilityAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested || Current != this || !ReferenceEquals(Session.Client, client) || client.ProfileId != profile) return;
            var hadFocus = RequestsRow.FocusState != FocusState.Unfocused;
            RequestsRow.Visibility = capability.Enabled ? Visibility.Visible : Visibility.Collapsed;
            if (!capability.Enabled && hadFocus) HistoryRow.Focus(FocusState.Programmatic);
            // Never eject on an unresolved probe; transient errors preserve the previous answer in Core.
            if (capability.Resolved && !capability.Enabled && ContentFrame.Content is RequestsPage && _tabs.Count > 0)
            {
                Select(_tabs[0]);
                FocusSelectedTab();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { Log.Error("Requests capability", ex); }
    }

    private void OnRequestsClick(object sender, RoutedEventArgs args)
    {
        if (Session.Client?.Requests.Capability.Enabled != true) return;
        ClosePanels();
        _personalReturnToProfile = false;
        if (ContentFrame.Content is RequestsPage requests) requests.EnterFromMenu();
        else
        {
            // Requests is a secondary route, not another root tab: Back pops the previous root.
            ContentFrame.Navigate(typeof(RequestsPage));
        }
    }

    internal void FocusRequestsAvatar()
    {
        ClosePanels();
        _profileDwellSuppressed = true;
        _restoringProfileFocus = true;
        try { AvatarButton.Focus(FocusState.Programmatic); }
        finally { _restoringProfileFocus = false; }
    }

    internal void OpenRequestDetail(RequestDetailEntry entry)
    {
        ClosePanels();
        Frame.Navigate(typeof(RequestDetailPage), entry);
    }
}
