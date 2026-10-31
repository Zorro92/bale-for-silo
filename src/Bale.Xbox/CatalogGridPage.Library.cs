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
    private bool _libraryInitialFocus;
    private bool _libraryFilterOpening;

    partial void ConfigureSource()
    {
        if (_entry.Source is not LibraryCatalogSource source) return;
        _librarySource = source;
        _libraryInitialFocus = !_entry.Results.HasLoaded;
        PageTitle.Visibility = Visibility.Collapsed;
        // TvLibraryDetailScreen.kt: taller 108dp Browse inset, full-span controls then 30dp row gap.
        Cards.Padding = new Thickness(40, 108, 20, 40);
        ToolbarSlot.Margin = new Thickness(0, 0, 20, 30);
        _libraryToolbar = new LibraryBrowseToolbar();
        _libraryOverlay = new LibraryBrowseOverlay();
        ToolbarSlot.Child = _libraryToolbar;
        SourceOverlay.Child = _libraryOverlay;
        SourceOverlay.Visibility = Visibility.Visible;
        _libraryOverlay.Configure(source, LibraryBrowseSettings.Preserve(_entry.Client, source.Query));
        _libraryToolbar.Update(source.Query.Filter);
        _libraryToolbar.SortRequested += () => _libraryOverlay.OpenSort();
        _libraryToolbar.FilterRequested += () => _ = OpenLibraryFiltersAsync();
        _libraryToolbar.ClearRequested += () =>
        {
            _libraryToolbar.FocusFilter();
            _ = ChangeLibraryFilterAsync(source.Query.Filter.ApplyFacets(new()));
        };
        _libraryToolbar.TopRequested += () => ShellPage.Current?.FocusSelectedTab();
        _libraryToolbar.ContentRequested += () => { if (_cards.Count > 0) FocusAt(Math.Clamp(_lastFocusedIndex, 0, _cards.Count - 1)); };
        _libraryOverlay.FilterChanged += filter => _ = ChangeLibraryFilterAsync(filter);
        _libraryOverlay.PreserveChanged += preserve => LibraryBrowseSettings.SetPreserve(_entry.Client, source.Query, preserve);
        _libraryOverlay.PanelClosed += filter => { if (filter) _libraryToolbar.FocusFilter(); else _libraryToolbar.FocusSort(); };
        _libraryOverlay.OpenChanged += open =>
        {
            Cards.IsEnabled = !open;
            _libraryToolbar.IsEnabled = !open;
        };
        _libraryOverlay.GridRequested += () =>
        {
            if (_cards.Count > 0) FocusAt(Math.Clamp(_lastFocusedIndex, 0, _cards.Count - 1));
            else _libraryToolbar.FocusControls();
        };
        _libraryOverlay.AlphabetExpanded += _ => UpdateLibraryGridWidth();
        PreviewKeyDown += OnLibraryPreviewKeyDown;
        UpdateLibraryGridWidth();
    }

    private void UpdateLibraryGridWidth()
    {
        if (_librarySource is null) return;
        // TvAlphabetRail.kt: the edge peek expands in the SAME six-column layout; no lost column.
        var reserved = _librarySource.Query.Filter.ShowsAlphabet ? (_libraryOverlay?.AlphabetHasFocus == true ? 96 : 18) + 16 : 0;
        Cards.Margin = new Thickness(0, 0, reserved, 0);
        SyncCards();
    }

    partial void AdjustSourceCardWidth(ref double width)
    {
        if (_librarySource is null) return;
        var reserved = _librarySource.Query.Filter.ShowsAlphabet ? (_libraryOverlay?.AlphabetHasFocus == true ? 96 : 18) + 16 : 0;
        var viewport = Root.ActualWidth > 0 ? Root.ActualWidth : Window.Current.Bounds.Width;
        width = Math.Max(1, (viewport - reserved - 40 - 16 - 5 * 20) / 6);
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

    private async Task ChangeLibraryFilterAsync(LibraryBrowseFilter filter)
    {
        if (_librarySource is null || !_active || !_entry.IsCurrent(Session.Client)) return;
        var previous = _librarySource.Query.Filter;
        if (previous.NamePrefix == filter.NamePrefix && LibraryBrowsePreferences.Serialize(previous) == LibraryBrowsePreferences.Serialize(filter))
        {
            _libraryOverlay?.Update();
            return;
        }
        _libraryReloadFocus = FocusManager.GetFocusedElement() as Control;
        _librarySource.SetFilter(filter);
        LibraryBrowseSettings.Save(_entry.Client, _librarySource.Query);
        _libraryToolbar?.Update(filter);
        _libraryOverlay?.Update();
        UpdateLibraryGridWidth();
        _scroller?.ChangeView(null, 0, null, true);
        await ReloadAsync();
    }

    partial void OnSourceLoaded()
    {
        if (_librarySource is null) return;
        EmptyMessage.Text = _librarySource.LibraryHasItems == false ? "This library is empty." : "No titles match the current filters.";
    }

    partial void UpdateSourceState()
    {
        if (_librarySource is null) return;
        // Controls remain available during loading and empty results, but an initial error replaces them.
        var available = !_entry.Results.Unavailable && _entry.Results.Error is null;
        if (available) Cards.Visibility = Visibility.Visible;
        _libraryOverlay?.Update(available);
        if (ErrorPanel.Children.OfType<TextBlock>().FirstOrDefault() is { } error) error.Text = "Couldn't load this library.";
    }

    partial void OnGridTopUp(ref bool handled)
    {
        if (_libraryToolbar is null) return;
        _libraryToolbar.FocusControls();
        _scroller?.ChangeView(null, 0, null, true);
        handled = true;
    }

    partial void OnGridRightEdge(ref bool handled)
    {
        // TvAlphabetRail.kt's enter target is always All. This is a library-only grid-edge exception:
        // horizontal card rails still stop at both ends, and every Up/Down remains column-preserving.
        handled = _libraryOverlay?.FocusAlphabet() == true;
    }

    partial void RestoreSourceFocus(ref bool handled)
    {
        if (_librarySource is null || _libraryToolbar is null) return;
        if (_libraryReloadFocus is { } target)
        {
            _libraryReloadFocus = null;
            FocusRetry.Until(this, () => !_active || target.Focus(FocusState.Programmatic));
            handled = true;
        }
        else if (_libraryInitialFocus || _cards.Count == 0)
        {
            _libraryInitialFocus = false;
            FocusRetry.Until(this, () => !_active || _entry.Results.Error is not null || _entry.Results.Unavailable || FocusLibraryToolbar());
            handled = true;
        }
    }

    private bool FocusLibraryToolbar() => _libraryToolbar?.FocusControls() == true;

    internal bool ShowsLibrary(string libraryId, string? mediaScope) => _entry.IsCurrent(Session.Client)
        && _librarySource?.Query is { CollectionId: null } query && query.LibraryId == libraryId && query.MediaType == mediaScope;

    internal void FocusLibraryContent()
    {
        if (_entry.Results.Error is not null) RetryButton.Focus(FocusState.Programmatic);
        else if (_cards.Count > 0) FocusAt(Math.Clamp(_lastFocusedIndex, 0, _cards.Count - 1));
        else _libraryToolbar?.FocusControls();
    }

    private void OnLibraryPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || _libraryOverlay?.IsOpen == true) return;
        if (e.Key == VirtualKey.Tab && _librarySource?.Query.Filter.ShowsAlphabet == true)
        {
            e.Handled = _libraryOverlay?.FocusAlphabet() == true;
        }
    }

    public bool OnBack() => _libraryOverlay?.OnBack() == true;

    partial void CleanupSource()
    {
        _libraryOverlay?.Close(restore: false);
        PreviewKeyDown -= OnLibraryPreviewKeyDown;
    }
}
