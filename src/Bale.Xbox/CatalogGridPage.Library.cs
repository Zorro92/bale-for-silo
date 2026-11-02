using Bale.Core.Browse;
using Bale.Xbox.Controls;
using Bale.Xbox.Services;
using Microsoft.Kiota.Abstractions;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox;

/// <summary>TvLibraryDetailScreen.kt's LibraryTab, hosted on the shared section catalog renderer.</summary>
public sealed partial class CatalogGridPage : IBackHandler
{
    private LibraryCatalogSource? _librarySource;
    private LibraryBrowseToolbar? _libraryToolbar;
    private LibraryBrowseOverlay? _libraryOverlay;
    private Control? _libraryReloadFocus;
    private int? _libraryAlphabetReturnIndex;
    private bool _libraryInitialFocus;
    private bool _libraryFilterOpening;

    partial void ConfigureSource()
    {
        ConfigurePersonalSource();
        if (_entry.Source is not LibraryCatalogSource source) return;
        _librarySource = source;
        _libraryInitialFocus = !_entry.Results.HasLoaded;
        if (source.IsCollection)
        {
            // screens/library/TvLibraryCollectionDetailScreen.kt: title and controls scroll INSIDE
            // the grid header; the collection opens on its first card rather than the Sort pill.
            VisualStateManager.GoToState(this, "Collection", false);
        }
        else
        {
            PageTitle.Visibility = Visibility.Collapsed;
            if (!source.IsPersonal) VisualStateManager.GoToState(this, "LibraryBrowse", false);
        }
        _libraryToolbar = new LibraryBrowseToolbar();
        if (source.IsCollection) _libraryToolbar.UseCollectionHeader();
        _libraryOverlay = new LibraryBrowseOverlay();
        ToolbarSlot.Child = _libraryToolbar;
        SourceOverlay.Child = _libraryOverlay;
        SourceOverlay.Visibility = Visibility.Visible;
        _libraryOverlay.Configure(source, !source.IsPersonal && !source.IsCollection && LibraryBrowseSettings.Preserve(_entry.Client, source.Query));
        _libraryToolbar.Update(source.Query.Filter);
        _libraryToolbar.SortRequested += () => _libraryOverlay.OpenSort();
        _libraryToolbar.FilterRequested += () => _ = OpenLibraryFiltersAsync();
        _libraryToolbar.ClearRequested += () =>
        {
            _libraryToolbar.FocusFilter();
            _ = ChangeLibraryFilterAsync(source.Query.Filter.ApplyFacets(new()));
        };
        _libraryToolbar.TopRequested += FocusAboveLibraryControls;
        _libraryToolbar.ContentRequested += () => { if (_cards.Count > 0) FocusAt(Math.Clamp(_lastFocusedIndex, 0, _cards.Count - 1)); };
        _libraryOverlay.FilterChanged += filter => _ = ChangeLibraryFilterAsync(filter);
        _libraryOverlay.PreserveChanged += preserve =>
        {
            if (!source.IsPersonal && !source.IsCollection) LibraryBrowseSettings.SetPreserve(_entry.Client, source.Query, preserve);
        };
        _libraryOverlay.PanelClosed += filter => { if (filter) _libraryToolbar.FocusFilter(); else _libraryToolbar.FocusSort(); };
        _libraryOverlay.OpenChanged += open =>
        {
            Cards.IsEnabled = !open;
            _libraryToolbar.IsEnabled = !open;
        };
        _libraryOverlay.GridRequested += RestoreLibraryGridFocus;
        _libraryOverlay.AlphabetWidthChanged += UpdateLibraryGridWidth;
        PreviewKeyDown += OnLibraryPreviewKeyDown;
        UpdateLibraryGridWidth();
    }

    private void UpdateLibraryGridWidth()
    {
        if (_librarySource is null) return;
        // TvAlphabetRail.kt: yield the rail's measured width throughout its 180ms tween in the SAME six-column grid.
        var reserved = _librarySource.ShowsAlphabet ? _libraryOverlay?.AlphabetWidth ?? 0 : 0;
        Cards.Margin = new Thickness(0, 0, reserved, 0);
        SyncCards();
    }

    private async Task OpenLibraryFiltersAsync()
    {
        if (_librarySource is null || _libraryOverlay is null || _libraryFilterOpening) return;
        _libraryFilterOpening = true;
        try
        {
            var options = await _librarySource.FiltersAsync(_lifetime.Token);
            if (_active && _entry.IsCurrent(Session.Client)) _libraryOverlay.OpenFilter(options);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ApiException ex) when (ex.ResponseStatusCode == 401) { Session.Expire(); }
        catch (Exception ex)
        {
            Log.Error("Library facets", ex);
            // The fixed Decade / Watch Status / Dynamic Range facets remain available offline.
            if (_active && _entry.IsCurrent(Session.Client)) _libraryOverlay.OpenFilter(null);
        }
        finally { _libraryFilterOpening = false; }
    }

    private async Task ChangeLibraryFilterAsync(LibraryBrowseFilter filter, bool restoreFilterFocus = false)
    {
        if (_librarySource is null || !_active || !_entry.IsCurrent(Session.Client)) return;
        var previous = _librarySource.Query.Filter;
        if (previous.NamePrefix == filter.NamePrefix && LibraryBrowsePreferences.Serialize(previous) == LibraryBrowsePreferences.Serialize(filter))
        {
            _libraryOverlay?.Update();
            return;
        }
        _libraryReloadFocus = restoreFilterFocus ? _libraryToolbar?.FilterFocusTarget : FocusManager.GetFocusedElement() as Control;
        _libraryAlphabetReturnIndex = null; // A new query no longer contains the card the rail was entered from.
        _librarySource.SetFilter(filter);
        if (!_librarySource.IsPersonal) LibraryBrowseSettings.Save(_entry.Client, _librarySource.Query);
        _libraryToolbar?.Update(filter);
        _libraryOverlay?.Update();
        UpdateLibraryGridWidth();
        _scroller?.ChangeView(null, 0, null, true);
        var reload = ReloadAsync();
        // Resetting the GridView's items moves focus out of its header; put it straight back on the
        // pill the change came from (TvLibraryBrowseControls.kt claims Filter before clearing).
        if (_libraryReloadFocus is { } target) FocusReloadTarget(target);
        await reload;
    }

    private bool FocusReloadTarget(Control target) => _libraryToolbar?.FocusPill(target) ?? target.Focus(FocusState.Programmatic);

    private async void OnErrorClearFiltersClick(object sender, RoutedEventArgs e)
    {
        if (_entry.Results.Error is null || _librarySource?.CanClearErrorFilters != true) return;
        // screens/library/TvLibraryDetailScreen.kt LibraryTab applies the empty facet selection,
        // including resetting Match Any and the per-visit prefix, then overwrites preserved filters.
        // The error action disappears on reload, so claim Filter rather than retaining a hidden button.
        await ChangeLibraryFilterAsync(_librarySource.Query.Filter.ApplyFacets(new()), restoreFilterFocus: true);
    }

    private void OnErrorActionsPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || ErrorClearFiltersButton.Visibility != Visibility.Visible || Direction(e.Key) is not { } direction) return;
        e.Handled = true;
        // components/StatusViews.kt lays Retry and its secondary action out vertically, not as a rail.
        if (direction == GridDirection.Down && ReferenceEquals(FocusManager.GetFocusedElement(), RetryButton))
            ErrorClearFiltersButton.Focus(FocusState.Keyboard);
        else if (direction == GridDirection.Up)
        {
            if (ReferenceEquals(FocusManager.GetFocusedElement(), ErrorClearFiltersButton)) RetryButton.Focus(FocusState.Keyboard);
            else ShellPage.Current?.FocusSelectedTab();
        }
    }

    partial void OnSourceLoaded()
    {
        if (_librarySource is null || _librarySource.IsPersonal) return;
        EmptyMessage.Text = _librarySource.IsCollection
            ? _librarySource.Query.Filter.Facets.ActiveCount > 0 ? "No titles match the current filters." : "This collection is empty."
            : _librarySource.LibraryHasItems == false ? "This library is empty." : "No titles match the current filters.";
    }

    partial void UpdateSourceState()
    {
        UpdatePersonalState();
        ErrorClearFiltersButton.Visibility = _entry.Results.Error is not null && _librarySource?.CanClearErrorFilters == true
            ? Visibility.Visible : Visibility.Collapsed;
        if (_librarySource is null) return;
        // TvPersonalScreens.kt keeps saved-list controls reachable even when a query fails, so the
        // viewer can undo a filter rather than being stranded with Retry for the same failed query.
        var collectionError = _librarySource.IsCollection && _entry.Results.LoadMoreError is not null;
        var available = !_entry.Results.Unavailable && !collectionError && (_librarySource.IsPersonal || _entry.Results.Error is null);
        if (available) Cards.Visibility = Visibility.Visible;
        if (collectionError)
        {
            // screens/library/TvLibraryCollectionDetailScreen.kt replaces the collection surface
            // with Retry for paging failures too, rather than leaving an unreachable footer error.
            Cards.Visibility = MoreErrorPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
        }
        _libraryOverlay?.Update(available);
        if (!_librarySource.IsPersonal) ErrorMessage.Text = _librarySource.IsCollection ? "Failed to load collection" : "Couldn't load this library.";
        if (_librarySource.IsCollection)
        {
            // screens/library/TvLibraryCollectionDetailScreen.kt itemCountLabel: the server total
            // includes TV-hidden books. Only report the visible count once all pages are exhausted.
            var state = _entry.Results;
            _libraryToolbar?.UpdateCount(state.HasLoaded && !state.IsLoading && !state.HasMore && state.Error is null
                && state.LoadMoreError is null ? _cards.Count : null);
        }
    }

    partial void OnGridTopUp(ref bool handled)
    {
        if (_libraryToolbar is null) return;
        _libraryToolbar.FocusControls();
        _scroller?.ChangeView(null, 0, null, true);
        handled = true;
    }

    partial void OnGridRightEdge(int index, ref bool handled)
    {
        // TvAlphabetRail.kt's enter target is always All. This is a library-only grid-edge exception:
        // horizontal card rails still stop at both ends, and every Up/Down remains column-preserving.
        handled = FocusLibraryAlphabet(index);
    }

    private bool FocusLibraryAlphabet(int index)
    {
        // Latch the departure before expansion reflows the grid. Unlike _lastFocusedIndex, this
        // target cannot be overwritten by incidental card focus during layout or rail traversal.
        var previous = _libraryAlphabetReturnIndex;
        _libraryAlphabetReturnIndex = index;
        _focusGeneration++;
        if (_libraryOverlay?.FocusAlphabet() == true) return true;
        _libraryAlphabetReturnIndex = previous;
        return false;
    }

    private void RestoreLibraryGridFocus()
    {
        if (_cards.Count > 0) FocusAt(Math.Clamp(_libraryAlphabetReturnIndex ?? _lastFocusedIndex, 0, _cards.Count - 1));
        else _libraryToolbar?.FocusControls();
    }

    partial void RestoreSourceFocus(ref bool handled)
    {
        if (_libraryOverlay?.IsOpen == true)
        {
            handled = true;
            return;
        }
        if (IsPersonalGrid && !IsInlinePersonalGrid && _entry.Results.Error is not null)
        {
            FocusRetry.Until(ErrorPanel, () => !_active || RetryButton.Focus(FocusState.Programmatic));
            handled = true;
            return;
        }
        if (_librarySource is null || _libraryToolbar is null) return;
        if (_librarySource.IsCollection && (_entry.Results.Error is not null || _entry.Results.LoadMoreError is not null))
        {
            FocusRetry.Until(ErrorPanel, () => !_active || RetryButton.Focus(FocusState.Programmatic));
            handled = true;
            return;
        }
        if (_libraryReloadFocus is { } target)
        {
            _libraryReloadFocus = null;
            FocusRetry.Until(this, () => !_active || FocusReloadTarget(target));
            handled = true;
        }
        else if ((!_librarySource.IsPersonal && !_librarySource.IsCollection && _libraryInitialFocus) || _cards.Count == 0)
        {
            _libraryInitialFocus = false;
            FocusRetry.Until(this, () => !_active || (!IsInlinePersonalGrid && _entry.Results.Error is not null)
                || _entry.Results.Unavailable || FocusLibraryToolbar());
            handled = true;
        }
        else _libraryInitialFocus = false; // TvPersonalScreens.kt / TvLibraryCollectionDetailScreen.kt enter on the first card.
    }

    private bool FocusLibraryToolbar() => _librarySource?.IsPersonal == true
        ? _libraryToolbar?.FocusSort() == true : _libraryToolbar?.FocusControls() == true;

    internal bool ShowsLibrary(string libraryId, string? mediaScope) => _entry.IsCurrent(Session.Client)
        && _librarySource?.Query is { CollectionId: null } query && query.LibraryId == libraryId && query.MediaType == mediaScope;

    internal void FocusLibraryContent()
    {
        // TvRecommendationsScreen.kt claimSavedListFocus waits for cards, then falls back to Sort
        // only after the initial request has finished (TvPersonalScreens.kt PersonalInlineGrid).
        if (IsInlinePersonalGrid && _cards.Count == 0)
        {
            if (!_entry.Results.IsLoading) _libraryToolbar?.FocusSort();
        }
        else if (_entry.Results.Error is not null) RetryButton.Focus(FocusState.Programmatic);
        else if (_cards.Count > 0) FocusAt(Math.Clamp(_lastFocusedIndex, 0, _cards.Count - 1));
        else _libraryToolbar?.FocusControls();
    }

    private void OnLibraryPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || _libraryOverlay?.IsOpen == true) return;
        if (e.Key == VirtualKey.Tab && _librarySource?.ShowsAlphabet == true)
        {
            e.Handled = _libraryOverlay?.AlphabetHasFocus == true
                ? _libraryOverlay.FocusAlphabet()
                : FocusLibraryAlphabet(_lastFocusedIndex);
        }
    }

    public bool OnBack() => _libraryOverlay?.OnBack() == true;

    partial void CleanupSource()
    {
        _libraryOverlay?.Close(restore: false);
        PreviewKeyDown -= OnLibraryPreviewKeyDown;
    }
}
