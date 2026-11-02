using System.Collections.ObjectModel;
using Bale.Core.Browse;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Microsoft.Kiota.Abstractions;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>screens/library/TvLibraryDetailScreen.kt CollectionsTab, including saved card entry and grouped headers.</summary>
public sealed partial class LibraryCollectionsPage : Page, IRestoresFocus
{
    private readonly ObservableCollection<LibraryCollectionRowView> _rows = [];
    private readonly List<LibraryCollectionCardView> _cards = [];
    private readonly CancellationTokenSource _lifetime = new();
    private LibraryCollectionsEntry _entry = null!;
    private LibraryCollectionsGrid _grid = new([]);
    private bool _active;
    private bool _loading;
    private string? _error;
    private int _focusGeneration;

    public LibraryCollectionsPage()
    {
        InitializeComponent();
        Rows.ItemsSource = _rows;
        Rows.GettingFocus += OnGridGettingFocus;
        // theme/Layout.kt rememberTvGridBringIntoViewSpec owns scrolling, not UWP's geometric reveal.
        Rows.BringIntoViewRequested += (_, args) => args.Handled = true;
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            if (IsLoaded && Parent is not null) return;
            _active = false;
            _focusGeneration++;
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not LibraryCollectionsEntry entry || !entry.IsCurrent) return;
        _entry = entry;
        _active = true;
        if (entry.Sections is null) await LoadAsync();
        else
        {
            BuildRows();
            RestoreFocus();
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The cached shell can unload/reparent its nested tab during an outer collection visit.
        if (_active || !IsLoaded || Parent is null || _lifetime.IsCancellationRequested || _entry?.IsCurrent != true) return;
        _active = true;
        if (_entry.Sections is null && !_loading && _error is null) await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading || !_active || !_entry.IsCurrent) return;
        _loading = true;
        _error = null;
        UpdateState();
        try
        {
            var sections = await _entry.Client.LibraryBrowse.CollectionsAsync(_entry.LibraryId, _lifetime.Token);
            if (_lifetime.IsCancellationRequested || !_entry.IsCurrent) return;
            _entry.Sections = sections;
            BuildRows();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ApiException ex) when (ex.ResponseStatusCode == 401) { if (_entry.IsCurrent) Session.Expire(); }
        catch (Exception ex)
        {
            Log.Error("Library collections", ex);
            if (!_lifetime.IsCancellationRequested && _entry.IsCurrent)
                _error = string.IsNullOrWhiteSpace(ex.Message) ? "Failed to load collections" : ex.Message;
        }
        finally
        {
            _loading = false;
            if (!_lifetime.IsCancellationRequested && _entry.IsCurrent)
            {
                UpdateState();
                RestoreFocus();
            }
        }
    }

    private double CardWidth() => CatalogGridLayout.Measure(
        (Root.ActualWidth > 0 ? Root.ActualWidth : Window.Current.Bounds.Width) - Scroller.Padding.Left - Scroller.Padding.Right,
        LibraryCollectionsGrid.Columns, Scroller.Padding.Right).CardWidth;

    private void BuildRows()
    {
        _focusGeneration++;
        _grid = new LibraryCollectionsGrid(_entry.Sections ?? []);
        _cards.Clear();
        _cards.AddRange(_grid.Cells.Select(cell => new LibraryCollectionCardView(cell, CardWidth())));
        _rows.Clear();
        foreach (var row in _grid.Rows)
            _rows.Add(new LibraryCollectionRowView(row.Header, row.Cells.Select(cell => _cards[cell.Index]).ToList()));
        UpdateState();
    }

    private void UpdateState()
    {
        LoadingRing.IsActive = _loading && _cards.Count == 0;
        LoadingRing.Visibility = LoadingRing.IsActive ? Visibility.Visible : Visibility.Collapsed;
        EmptyMessage.Visibility = !_loading && _error is null && _entry.Sections is not null && _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = _error is not null && _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorMessage.Text = _error ?? string.Empty;
    }

    internal bool ShowsLibrary(string libraryId, string? mediaScope) => _entry?.IsCurrent == true
        && _entry.LibraryId == libraryId && _entry.MediaScope == mediaScope;

    internal void FocusContent() => RestoreFocus();

    public void RestoreFocus()
    {
        if (!_active || _entry?.IsCurrent != true) return;
        if (_grid.Entry(_entry.LastFocusedId) is { } entry) FocusAt(entry.Index);
        else if (_error is not null)
            FocusRetry.Until(ErrorPanel, () => !_active || RetryButton.Focus(FocusState.Programmatic));
    }

    private void FocusAt(int index)
    {
        if (index < 0 || index >= _cards.Count) return;
        var view = _cards[index];
        var generation = ++_focusGeneration;
        FocusRetry.Until(Rows, () =>
        {
            if (!_active || generation != _focusGeneration || !_entry.IsCurrent) return true;
            var container = Containers(Rows).FirstOrDefault(item => ReferenceEquals(item.Content, view));
            if (container?.Focus(FocusState.Programmatic) != true) return false;
            Reveal(container);
            return true;
        });
    }

    private void Reveal(ContentControl container)
    {
        if (container.ContentTemplateRoot is not FrameworkElement root || root.FindName("Art") is not FrameworkElement art) return;
        var offset = art.TransformToVisual(Scroller).TransformPoint(new Point(0, 0)).Y;
        var distance = LibraryCollectionsGrid.ScrollDistance(offset, art.ActualHeight, Scroller.ViewportHeight, Scroller.Padding.Top);
        if (Math.Abs(distance) > 0.5) Scroller.ChangeView(null, Scroller.VerticalOffset + distance, null, true);
    }

    private void OnGridGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        try
        {
            // CollectionsTab's enter requester applies to every entry, not only the initial load.
            if (Within(args.OldFocusedElement, Rows) || args.NewFocusedElement is not ContentControl { Content: LibraryCollectionCardView }
                || _grid.Entry(_entry.LastFocusedId) is not { } entry) return;
            var target = Containers(Rows).FirstOrDefault(item => ReferenceEquals(item.Content, _cards[entry.Index]));
            if (target is not null && !ReferenceEquals(target, args.NewFocusedElement)) args.TrySetNewFocusedElement(target);
        }
        catch (Exception ex) { Log.Error("Collection grid entry", ex); }
    }

    private void OnGridPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || e.OriginalSource is not ContentControl { Content: LibraryCollectionCardView view } || Direction(e.Key) is not { } direction) return;
        e.Handled = true;
        if (direction == GridDirection.Up && view.Cell.Row == _grid.Cells[0].Row)
        {
            _focusGeneration++;
            ShellPage.Current?.FocusSelectedTab();
            return;
        }
        var target = _grid.Move(view.Cell.Index, direction);
        if (target != view.Cell.Index) FocusAt(target);
    }

    private void OnCollectionFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not ContentControl { Content: LibraryCollectionCardView view } container) return;
        CardFocus.Apply(container, true, artScale: 1.10f);
        _entry.LastFocusedId = view.Cell.Collection.Id;
        Reveal(container);
    }

    private void OnCollectionBlur(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: LibraryCollectionCardView } container) CardFocus.Apply(container, false, artScale: 1.10f);
    }

    private void OnCollectionClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LibraryCollectionCardView view || !_entry.IsCurrent) return;
        _entry.LastFocusedId = view.Cell.Collection.Id;
        OpenCollection(view);
    }

    partial void OpenCollection(LibraryCollectionCardView view);

    private async void OnRetryClick(object sender, RoutedEventArgs e) => await LoadAsync();

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_entry is null) return;
        var width = CardWidth();
        foreach (var view in _cards)
            if (view.Poster.Width != width) view.Poster.Update(view.Poster.Card, string.Empty, width, width * 1.5, false);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _active = false;
        _focusGeneration++;
        _lifetime.Cancel();
        base.OnNavigatedFrom(e);
    }

    private static IEnumerable<ContentControl> Containers(DependencyObject root)
    {
        if (root is ContentControl { Content: LibraryCollectionCardView } container) yield return container;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Containers(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static bool Within(DependencyObject? element, DependencyObject ancestor)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }

    private static GridDirection? Direction(VirtualKey key) => key switch
    {
        VirtualKey.Left or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadLeftThumbstickLeft => GridDirection.Left,
        VirtualKey.Right or VirtualKey.GamepadDPadRight or VirtualKey.GamepadLeftThumbstickRight => GridDirection.Right,
        VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp => GridDirection.Up,
        VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown => GridDirection.Down,
        _ => null,
    };
}
