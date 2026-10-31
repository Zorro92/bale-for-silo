using Bale.Core.Browse;
using Bale.Xbox.Services;
using Windows.UI.Xaml;

namespace Bale.Xbox;

public sealed partial class CatalogGridPage
{
    private bool _personalRefreshing;
    private int _personalRestoreVersion;
    private bool IsPersonalGrid => _entry?.Source is LibraryCatalogSource { IsPersonal: true } or HistoryCatalogSource;

    private void ConfigurePersonalSource()
    {
        if (!IsPersonalGrid) return;
        PersonalTitle.Text = _entry.Source.Title;
        VisualStateManager.GoToState(this, "Personal", false);
        var kind = _entry.Source switch
        {
            LibraryCatalogSource { Query.PersonalList: "watchlist" } => "PersonalWatchlist",
            LibraryCatalogSource => "PersonalFavorites",
            _ => "PersonalHistory",
        };
        VisualStateManager.GoToState(this, kind, false);

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
