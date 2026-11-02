using Bale.Core.Browse;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

public sealed partial class BrowsePage
{
    private CancellationTokenSource? _libraryRecommendedLoad;
    private bool _libraryRecommendedResume;

    private async Task LoadLibraryRecommendedAsync(bool refresh)
    {
        if (_libraryId is not { } libraryId || Session.Client is not { } client) return;
        var scope = _libraryMediaScope;
        var profileId = client.ProfileId;
        var previous = _libraryRecommendedLoad;
        using var load = new CancellationTokenSource();
        _libraryRecommendedLoad = load;
        _libraryRecommendedResume = false;
        _loading = true;
        // TvLibraryDetailViewModel.kt.loadRecommended cancels a superseded load and its refill cursors.
        previous?.Cancel();
        LibraryRefillWarning.Visibility = Visibility.Collapsed;
        if (_rows.Count == 0) VisualStateManager.GoToState(this, "Loading", false);

        bool IsCurrent() => ReferenceEquals(_libraryRecommendedLoad, load) && !load.IsCancellationRequested
            && ReferenceEquals(client, Session.Client) && client.ProfileId == profileId
            && _libraryId == libraryId && _libraryMediaScope == scope;
        try
        {
            var result = await client.Browse.LoadLibraryRecommendedAsync(libraryId, ArtworkWidths.Tv1080, scope, load.Token);
            if (!IsCurrent() || (refresh && !_isLoaded)) return;

            // TvSkylineSectionFeed.kt keeps row/card identities across a retry, just like the shared feed loader.
            var restoreFocus = UpdateRows(result.Rows, refresh);
            if (_rows.Count == 0)
            {
                // TvLibraryDetailScreen.kt.RecommendedTab: an incomplete empty scope is an error, not an empty library.
                if (result.Error is { } error) ShowError(error, !refresh || restoreFocus);
                else ShowEmpty(!refresh || restoreFocus);
                return;
            }

            VisualStateManager.GoToState(this, "Content", false);
            ShowLibraryRefillWarning(result.Error);
            ShowMarquee((_lastCard?.Card as CardView) ?? _shownMarquee ?? _rows[0].Cards.OfType<CardView>().First(), refresh);
            if (refresh)
            {
                if (restoreFocus) RestoreCardFocus(refreshHome: false);
            }
            else
            {
                // TvLibraryDetailScreen.kt uses the same first-card entry claim as the shared section feed.
                _focusFirstRow = true;
                Rows.LayoutUpdated += OnRowsLayoutUpdated;
            }
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            // The old library/scope must neither publish nor continue paging after it leaves the screen.
        }
        catch (Exception ex)
        {
            if (!IsCurrent()) return;
            Log.Error($"Library {libraryId} load", ex);
            var message = $"Couldn't load this page. {ex.Message}";
            if (_rows.Count == 0)
            {
                ShowError(message, !refresh || IsInPage(FocusManager.GetFocusedElement() as DependencyObject));
            }
            else
            {
                // TvLibraryDetailScreen.kt keeps the feed usable and exposes Retry when only some shelves failed.
                ShowLibraryRefillWarning(message);
            }
        }
        finally
        {
            if (ReferenceEquals(_libraryRecommendedLoad, load))
            {
                _libraryRecommendedLoad = null;
                _loading = false;
            }
        }
    }

    private void ShowLibraryRefillWarning(string? message)
    {
        LibraryRefillText.Text = message ?? string.Empty;
        LibraryRefillWarning.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SuspendLibraryRecommendedLoad()
    {
        if (_libraryRecommendedLoad is not { } load) return;
        _libraryRecommendedLoad = null;
        _libraryRecommendedResume = true;
        _loading = false;
        load.Cancel();
    }

    private void ResumeLibraryRecommendedLoad()
    {
        // The cached shell is also unloaded under Detail. Resume an interrupted load, not an already populated feed.
        // TvLibraryDetailViewModel.kt retains Recommended's loaded state while its screen is away.
        if (_libraryRecommendedResume && !_loading && _libraryId is not null)
        {
            _libraryRecommendedResume = false;
            _ = LoadLibraryRecommendedAsync(refresh: _rows.Count > 0);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // PageFrame keeps the outgoing page loaded for its fade: stop scope refills at navigation, not after the fade.
        SuspendLibraryRecommendedLoad();
        base.OnNavigatedFrom(e);
    }

    private async void OnLibraryRefillRetryClick(object sender, RoutedEventArgs e)
    {
        // TvLibraryDetailScreen.kt's warning disappears on retry; relinquish its button before removing it.
        RestoreCardFocus(refreshHome: false);
        await LoadLibraryRecommendedAsync(refresh: true);
    }

    private void OnLibraryRefillKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Up:
            case VirtualKey.GamepadDPadUp:
            case VirtualKey.GamepadLeftThumbstickUp:
                e.Handled = true;
                RestoreCardFocus(refreshHome: false);
                break;
            case VirtualKey.Down:
            case VirtualKey.Left:
            case VirtualKey.Right:
            case VirtualKey.GamepadDPadDown:
            case VirtualKey.GamepadDPadLeft:
            case VirtualKey.GamepadDPadRight:
            case VirtualKey.GamepadLeftThumbstickDown:
            case VirtualKey.GamepadLeftThumbstickLeft:
            case VirtualKey.GamepadLeftThumbstickRight:
                e.Handled = true;
                break;
        }
    }
}
