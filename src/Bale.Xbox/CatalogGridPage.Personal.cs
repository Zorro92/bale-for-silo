using Bale.Core.Browse;
using Bale.Xbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox;

public sealed partial class CatalogGridPage
{
    private bool _personalRefreshing;
    private int _personalRestoreVersion;
    private Rail? _personalMediaRail;
    private bool IsPersonalGrid => _entry?.Source is LibraryCatalogSource { IsPersonal: true } or HistoryCatalogSource;
    private bool IsInlinePersonalGrid => _entry is { InlinePersonal: true, Source: LibraryCatalogSource { IsPersonal: true } };

    private void ConfigurePersonalSource()
    {
        if (!IsPersonalGrid) return;
        PersonalTitle.Text = _entry.Source.Title;
        // TvPersonalScreens.kt PersonalInlineGrid omits YOUR LIBRARY, the title and its icon.
        VisualStateManager.GoToState(this, IsInlinePersonalGrid ? "PersonalInline" : "Personal", false);
        VisualStateManager.GoToState(this, IsInlinePersonalGrid && _entry.SavedListFallback ? "SavedListFallback" : "NoSavedListCaption", false);
        var kind = _entry.Source switch
        {
            LibraryCatalogSource { Query.PersonalList: "watchlist" } => "PersonalWatchlist",
            LibraryCatalogSource => "PersonalFavorites",
            _ => "PersonalHistory",
        };
        VisualStateManager.GoToState(this, kind, false);

        var showMediaKinds = IsInlinePersonalGrid && _entry.Source is LibraryCatalogSource { Query.PersonalList: "favorites" };
        VisualStateManager.GoToState(this, showMediaKinds ? "InlineFavoriteMediaKinds" : "NoPersonalMediaKinds", false);
        if (showMediaKinds)
        {
            _personalMediaRail = Rail.Attach(PersonalMediaKinds);
            _personalMediaRail.Pin = false;
        }
        if (_entry.Source is not LibraryCatalogSource) return;
        VisualStateManager.GoToState(this, "PersonalWithControls", false);
        // TvPersonalScreens.kt keeps controls mounted on loading/error/empty queries. Put states
        // INSIDE the scrolling grid, rather than covering the pills with a whole-surface state.
        foreach (var state in new FrameworkElement[] { PersonalEmptyPanel, LoadingRing, ErrorPanel })
        {
            ContentArea.Children.Remove(state);
            GridStates.Children.Add(state);
        }
    }

    private void UpdatePersonalState()
    {
        if (!IsPersonalGrid) return;
        if (_personalMediaRail is not null)
        {
            PersonalMovies.IsChecked = _librarySource?.Query.MediaType == "movie";
            PersonalTvShows.IsChecked = _librarySource?.Query.MediaType == "series";
        }
        var state = _entry.Results;
        EmptyMessage.Visibility = Visibility.Collapsed;
        PersonalEmptyPanel.Visibility = state.HasLoaded && !state.IsLoading && state.Error is null && _cards.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        PersonalEmptyMessage.Text = _entry.Source switch
        {
            LibraryCatalogSource { Query.Filter.Facets.ActiveCount: > 0 } => "No titles match the current filters.",
            LibraryCatalogSource saved => saved.EmptyMessage,
            HistoryCatalogSource history => history.EmptyMessage,
            _ => string.Empty,
        };
        ErrorMessage.Text = _entry.Source switch
        {
            LibraryCatalogSource { Query.PersonalList: "watchlist" } => "Couldn't load your watchlist.",
            LibraryCatalogSource => "Couldn't load your favorites.",
            _ => "Couldn't load watch history.",
        };
        _libraryToolbar?.UpdateCount(state.HasLoaded && state.Error is null ? _librarySource?.Total : null);
    }

    internal bool EnterInlinePersonal(string kind, bool fallback)
    {
        if (!IsInlinePersonalGrid || !_entry.IsCurrent(Session.Client) || _librarySource?.Query.PersonalList != kind) return false;
        // TvForYouEntryRequest.kt / TvRecommendationsScreen.kt: another explicit pick resets the
        // entry focus and fallback caption, not TvFavoritesInline's remembered Movies / TV Shows.
        // Detail return never takes this path; it still restores the launch-card identity.
        _entry.SavedListFallback = fallback;
        VisualStateManager.GoToState(this, fallback ? "SavedListFallback" : "NoSavedListCaption", false);
        _entry.ReturnItemId = null;
        _entry.ReturnItemIndex = _lastFocusedIndex = 0;
        _libraryReloadFocus = null;
        _focusGeneration++;
        if (!_entry.Results.IsLoading || _cards.Count > 0) RestoreFocus();
        return true;
    }

    private void FocusAboveLibraryControls()
    {
        // TvPersonalScreens.kt PersonalInlineGrid places Favorites' media row above Sort/Filter.
        // Explicit vertical handoffs avoid XAML choosing a sideways neighbour at either row edge.
        if (_personalMediaRail is { } rail)
        {
            rail.Remembered = _librarySource?.Query.MediaType == "series" ? PersonalTvShows : PersonalMovies;
            _scroller?.ChangeView(null, 0, null, true);
            rail.Focus();
        }
        else ShellPage.Current?.FocusSelectedTab();
    }

    private void OnPersonalMediaKindsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var direction = Direction(e.Key);
        if (direction is not (GridDirection.Up or GridDirection.Down)) return;
        e.Handled = true;
        if (direction == GridDirection.Up) ShellPage.Current?.FocusSelectedTab();
        else _libraryToolbar?.FocusControls();
    }

    private async void OnPersonalMediaKindClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string mediaType } button || _librarySource is null || !_active || !_entry.IsCurrent(Session.Client)) return;
        // TvPersonalScreens.kt TvFavoritesInline: Movies by default, TV Shows selects type=series.
        // Keep focus on the clicked chip through the reload, not on the new first card.
        PersonalMovies.IsChecked = mediaType == "movie";
        PersonalTvShows.IsChecked = mediaType == "series";
        if (_librarySource.Query.MediaType == mediaType) return;
        _libraryReloadFocus = button;
        _librarySource.SetFavoriteMediaType(mediaType);
        _scroller?.ChangeView(null, 0, null, true);
        await ReloadAsync();
    }

    private async Task RefreshPersonalAsync()
    {
        // TvPersonalScreens.PersonalListResumeRefresh: returning from Detail must remove ghost
        // entries after card actions. Resolve the remembered ID only against the REPLACED pages.
        var version = ++_personalRestoreVersion;
        _personalRefreshing = true;
        _focusGeneration++;
        _cards.Clear();
        try
        {
            await LoadAsync(_entry.Results.Begin());
            while (_active && version == _personalRestoreVersion && _entry.IsCurrent(Session.Client)
                && _entry.ReturnItemId is { } target && !_entry.Results.Items.Any(card => card.ContentId == target)
                && _entry.Results.BeginMore() is { } next)
            {
                await LoadAsync(next);
            }
        }
        finally
        {
            if (version == _personalRestoreVersion)
            {
                _personalRefreshing = false;
                RestoreFocus();
                QueuePagingCheck();
            }
        }
    }
}
