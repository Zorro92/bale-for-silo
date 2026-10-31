using Bale.Core.Connection;
using Bale.Xbox.Services;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox;

/// <summary>TvMainShell.kt / TvServerOfflinePill: a shell overlay, not a feed error or an offline navigation gate.</summary>
public sealed partial class ShellPage
{
    private ServerReachabilityMonitor? _reachability;

    private void InitializeOfflinePill()
    {
        Loaded += (_, _) =>
        {
            _reachability = Session.Reachability;
            if (_reachability is not null) _reachability.StateChanged += OnReachabilityChanged;
            UpdateOfflinePill();
        };
        Unloaded += (_, _) =>
        {
            if (_reachability is not null) _reachability.StateChanged -= OnReachabilityChanged;
            _reachability = null;
        };
    }

    private async void OnReachabilityChanged(object? sender, EventArgs e)
    {
        try
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (ReferenceEquals(sender, _reachability) && ReferenceEquals(_reachability, Session.Reachability)) UpdateOfflinePill();
            });
        }
        catch (Exception ex)
        {
            Log.Error("Offline indicator", ex);
        }
    }

    private void UpdateOfflinePill()
    {
        var offline = _reachability?.State.Status == ServerReachabilityStatus.Unreachable;
        if (!offline && IsWithin(FocusManager.GetFocusedElement() as DependencyObject, OfflinePill)) FocusSelectedTab();
        OfflinePill.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnOfflineRetryClick(object sender, RoutedEventArgs e)
    {
        if (_reachability is not { } monitor) return;
        try
        {
            // TvMainShell.kt retries discovery only: no sign-out, navigation, or replacement of cached content.
            await monitor.RetryNowAsync();
        }
        catch (OperationCanceledException)
        {
            // App suspension/server replacement invalidated this retry.
        }
        catch (ObjectDisposedException)
        {
            // The configured server was replaced before the click could dispatch.
        }
        catch (Exception ex)
        {
            Log.Error("Offline retry", ex);
        }
    }
}
