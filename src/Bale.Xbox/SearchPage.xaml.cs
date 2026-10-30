using System.Collections.ObjectModel;
using Bale.Core.Browse;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox;

/// <summary>Search, reached from the top bar's search icon (guide §6.7). Results update as you type.</summary>
public sealed partial class SearchPage : Page, IRestoresFocus
{
    // TvSearchViewModel.kt searches any non-blank query after a 300ms pause.
    private static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(300);

    /// <summary>Android TV's search grid cell (TvArtworkDimens.GridPosterWidth): 138 dp wide, 2:3.</summary>
    private const double PosterWidth = 138;
    private const double PosterHeight = 207;
    private static readonly ArtworkWidths SearchArtwork = ArtworkWidths.Tv1080 with { Poster = 276 };

    private readonly DispatcherTimer _typing = new() { Interval = TypingPause };
    private readonly SearchResults _state = new();
    private readonly ObservableCollection<CardView> _cards = [];
    private readonly Rail _filterRail;
    private readonly Rail _peopleRail;
    private IReadOnlyList<SearchMediaFilter> _filters = [SearchMediaFilter.All, SearchMediaFilter.Movies, SearchMediaFilter.Series];
    private List<SearchFilterView> _filterViews = [];
    private List<SearchPersonView> _peopleViews = [];
    private SearchMediaFilter _filter = SearchMediaFilter.All;
    private bool _ready;
    private bool _focusedOnce;
    private bool _filtersLoaded;
    private bool _loadingFilters;
    private bool _resultBringIntoViewHooked;
    private bool _pagingCheckQueued;
    private ScrollViewer? _resultScroller;
    private CardView? _lastResult;
    private CancellationTokenSource? _search;

    private bool ShowAudiobooks => _filters.Contains(SearchMediaFilter.Audiobooks);

    public SearchPage()
    {
        InitializeComponent();
        _filterRail = Rail.Attach(Filters);
        _filterRail.Pin = false;
        _filterRail.EntryItem = () => _filterViews.FirstOrDefault(view => view.Selected);
        _filterRail.Entered += ScrollHeaderIntoView;
        _peopleRail = Rail.Attach(People);
        _peopleRail.Entered += ScrollHeaderIntoView;
        SetFilterViews();
        Results.ItemsSource = _cards;
        _ready = true;

        // Focus alone never opens the keyboard (TvSearchScreen.kt's read-only-until-Select field).
        // Keep PreventKeyboardDisplayOnProgrammaticFocus in XAML for both this move and chips → field.
        Loaded += async (_, _) =>
        {
            if (!_focusedOnce)
            {
                _focusedOnce = true;
                QueryBox.Focus(FocusState.Programmatic);
            }

            await LoadFiltersAsync();
        };
        _typing.Tick += async (_, _) =>
        {
            _typing.Stop();
            await SearchAsync(QueryBox.Text.Trim());
        };
        Results.PreviewKeyDown += OnResultsPreviewKeyDown;
        Filters.PreviewKeyDown += OnFiltersPreviewKeyDown;
        People.PreviewKeyDown += OnPeoplePreviewKeyDown;
        QueryBox.PreviewKeyDown += OnQueryPreviewKeyDown;
        Results.Loaded += (_, _) => HookResults();
        Results.ContainerContentChanging += (_, _) => QueuePagingCheck();
        Results.SizeChanged += (_, _) => QueuePagingCheck();
    }

    private async Task LoadFiltersAsync()
    {
        if (_filtersLoaded || _loadingFilters || Session.Client is not { } client)
        {
            return;
        }

        _loadingFilters = true;
        try
        {
            var filters = SearchMediaFilter.FromTabs(await client.Browse.LoadLibraryTabsAsync());
            _filtersLoaded = true;
            if (_filters.SequenceEqual(filters))
            {
                return;
            }

            var focusedChip = FilterButtons().FirstOrDefault(chip => chip.FocusState != FocusState.Unfocused);
            var showAudiobooks = ShowAudiobooks;
            _filters = filters;
            var changedFilter = !_filters.Contains(_filter);
            if (changedFilter)
            {
                _filter = SearchMediaFilter.All;
            }

            SetFilterViews();
            if (focusedChip is not null)
            {
                FocusRetry.Until(Filters, FocusSelectedFilter);
            }

            // TvSearchViewModel.kt re-runs a live query when the available media scope changes.
            if ((changedFilter || showAudiobooks != ShowAudiobooks) && !string.IsNullOrWhiteSpace(QueryBox.Text))
            {
                await SearchAsync(QueryBox.Text.Trim());
            }
            else
            {
                UpdatePresentation();
            }
        }
        catch (Exception ex)
        {
            // TvSearchViewModel.kt keeps its video-only defaults if libraries cannot be fetched.
            Log.Error("Search libraries", ex);
        }
        finally
        {
            _loadingFilters = false;
        }
    }

    private void SetFilterViews()
    {
        _filterViews = _filters.Select(filter => new SearchFilterView(filter) { Selected = filter == _filter }).ToList();
        _filterRail.Remembered = null;
        Filters.ItemsSource = _filterViews;
        QueryBox.PlaceholderText = SearchText.Prompt(_filters);
        EmptyBody.Text = SearchText.Description(_filters);
    }

    private IEnumerable<ToggleButton> FilterButtons() =>
        Filters.ItemsPanelRoot?.Children.OfType<DependencyObject>()
            .Select(container => FocusManager.FindFirstFocusableElement(container)).OfType<ToggleButton>() ?? [];

    private bool FocusSelectedFilter() => _filterRail.Focus();

    private void HookResults()
    {
        if (!_resultBringIntoViewHooked && Results.ItemsPanelRoot is { } panel)
        {
            // Handle below the inner ScrollViewer so it sees the adjusted rectangle.
            panel.BringIntoViewRequested += OnResultBringIntoViewRequested;
            _resultBringIntoViewHooked = true;
        }

        if (_resultScroller is null && Rail.FindChild<ScrollViewer>(Results) is { } scroller)
        {
            _resultScroller = scroller;
            scroller.ViewChanged += (_, _) => QueuePagingCheck();
        }
    }

    private void ScrollHeaderIntoView()
    {
        HookResults();
        // TvSearchScreen.kt brings header item zero back on entry to people/chips, not on Left/Right.
        _resultScroller?.ChangeView(null, 0, null);
    }

    private void OnResultBringIntoViewRequested(UIElement sender, BringIntoViewRequestedEventArgs args)
    {
        try
        {
            if (args.TargetElement is ContentControl { Content: CardView })
            {
                // TvSearchScreen-style minimal scrolling, with 44px above and 80px below each result.
                var r = args.TargetRect;
                args.TargetRect = new Rect(r.X, r.Y - 44, r.Width, r.Height + 44 + 80);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Search bring into view", ex);
        }
    }

    private async void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: SearchFilterView view })
        {
            return;
        }

        _filter = view.Filter;
        foreach (var other in _filterViews)
        {
            other.Selected = ReferenceEquals(other, view);
        }

        await SearchAsync(QueryBox.Text.Trim());
    }

    private void OnQueryPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsDown(e.Key))
        {
            // TvSearchScreen.kt pins field → selected chip; a failed move must never go sideways.
            e.Handled = true;
            FocusSelectedFilter();
        }
    }

    private void OnFiltersPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is not ToggleButton)
        {
            return;
        }

        if (IsUp(e.Key))
        {
            e.Handled = true;
            QueryBox.Focus(FocusState.Programmatic);
        }
        else if (IsDown(e.Key))
        {
            e.Handled = true;
            // TvSearchScreen.kt's first-content precedence: people, titles, then the error action.
            if (_peopleViews.Count > 0)
            {
                _peopleRail.Focus();
            }
            else if (_cards.Count > 0)
            {
                FocusResultAt(0);
            }
            else if (RetryButton.Visibility == Visibility.Visible)
            {
                RetryButton.Focus(FocusState.Programmatic);
            }
        }
    }

    private void OnPeoplePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsUp(e.Key))
        {
            e.Handled = true;
            FocusSelectedFilter();
        }
        else if (IsDown(e.Key))
        {
            e.Handled = true;
            if (_cards.Count > 0)
            {
                // TvSearchScreen.kt leaves people → grid spatial, beneath the focused portrait.
                // Choose only the first grid row, never a sideways fallback in the people rail.
                var focused = FocusManager.GetFocusedElement() as FrameworkElement;
                var x = focused?.TransformToVisual(Results).TransformPoint(new Point(focused.ActualWidth / 2, 0)).X ?? 0;
                var firstRow = ResultContainers().Where(container => Results.IndexFromContainer(container) < GridColumns());
                var target = firstRow.OrderBy(container => Math.Abs(container.TransformToVisual(Results)
                    .TransformPoint(new Point(container.ActualWidth / 2, 0)).X - x)).FirstOrDefault();
                FocusResultAt(target is null ? 0 : Results.IndexFromContainer(target));
            }
            else if (RetryButton.Visibility == Visibility.Visible)
            {
                RetryButton.Focus(FocusState.Programmatic);
            }
        }
    }

    private static bool IsUp(VirtualKey key) => key is VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp;
    private static bool IsDown(VirtualKey key) => key is VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown;

    private IEnumerable<ContentControl> ResultContainers() =>
        Results.ItemsPanelRoot?.Children.OfType<ContentControl>().Where(container => container.Content is CardView) ?? [];

    private int GridColumns()
    {
        var containers = ResultContainers().ToList();
        var top = containers.FirstOrDefault()?.ActualOffset.Y;
        return Math.Max(1, containers.Count(container => container.ActualOffset.Y == top));
    }

    /// <summary>
    /// TvCatalogGrid.kt never wraps at row ends. Explicit column steps also keep Down above an
    /// incomplete last row from taking a neighbouring poster: a vertical key cannot move sideways.
    /// </summary>
    private void OnResultsPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var step = e.Key switch
        {
            VirtualKey.Left or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadLeftThumbstickLeft => -1,
            VirtualKey.Right or VirtualKey.GamepadDPadRight or VirtualKey.GamepadLeftThumbstickRight => 1,
            _ => 0,
        };
        var up = IsUp(e.Key);
        var down = IsDown(e.Key);
        if ((step == 0 && !up && !down) || e.OriginalSource is not ContentControl { Content: CardView } item)
        {
            return;
        }

        var index = Results.IndexFromContainer(item);
        var columns = GridColumns();
        if (down)
        {
            e.Handled = true;
            if (index >= 0 && index + columns < _cards.Count)
            {
                FocusResultAt(index + columns);
            }
            return;
        }

        if (up)
        {
            e.Handled = true;
            if (index >= columns)
            {
                FocusResultAt(index - columns);
            }
            else if (_peopleViews.Count > 0)
            {
                _peopleRail.Focus();
            }
            else
            {
                FocusSelectedFilter();
            }
            return;
        }

        if (index < 0 || (step < 0 && index % columns == 0) || (step > 0 && (index % columns == columns - 1 || index == _cards.Count - 1)))
        {
            e.Handled = true;
        }
    }

    private void FocusResultAt(int index)
    {
        if (index < 0 || index >= _cards.Count)
        {
            return;
        }

        var card = _cards[index];
        bool TryFocus() => ResultContainers().FirstOrDefault(container => ReferenceEquals(container.Content, card))?.Focus(FocusState.Programmatic) == true;
        if (TryFocus())
        {
            return;
        }

        var generation = _state.Generation;
        var origin = FocusManager.GetFocusedElement();
        Results.ScrollIntoView(card);
        // Do not let deferred realization steal focus after the viewer moves or changes the query.
        FocusRetry.Until(Results, () => generation != _state.Generation
            || !ReferenceEquals(origin, FocusManager.GetFocusedElement()) || TryFocus());
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _typing.Stop();
        _search?.Cancel();
        _state.ChangeQuery(QueryBox.Text, _filter);
        ClearPeople();
        if (_state.Query.Length == 0)
        {
            SyncCards(reset: true);
        }
        else
        {
            _typing.Start();
        }

        UpdatePresentation();
    }

    private async Task SearchAsync(string query)
    {
        _typing.Stop();
        _search?.Cancel();
        _search?.Dispose();
        _search = null;
        var request = _state.BeginSearch(query, _filter);
        ClearPeople();
        if (request is null || Session.Client is not { } client)
        {
            SyncCards(reset: true);
            UpdatePresentation();
            return;
        }

        var search = _search = new CancellationTokenSource();
        var showAudiobooks = ShowAudiobooks;
        UpdatePresentation();
        // TvSearchViewModel.kt runs people independently: they cannot delay or fail the title grid.
        _ = SearchPeopleAsync(client.Browse, request, showAudiobooks, search.Token);
        try
        {
            var page = await client.Browse.SearchPageAsync(request.Query, SearchArtwork, request.Type,
                showAudiobooks: showAudiobooks, cancellationToken: search.Token);
            if (!search.IsCancellationRequested && _state.ApplyPage(request, page))
            {
                SyncCards(reset: true);
                UpdatePresentation();
                QueuePagingCheck();
            }
        }
        catch (OperationCanceledException) when (search.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Search", ex);
            if (_state.Fail(request, ex))
            {
                UpdatePresentation();
            }
        }
    }

    private async Task SearchPeopleAsync(BrowseFeed feed, SearchRequest request, bool showAudiobooks, CancellationToken cancellationToken)
    {
        try
        {
            var people = await feed.SearchPeopleAsync(request.Query, request.Type, showAudiobooks, cancellationToken);
            if (!cancellationToken.IsCancellationRequested && _state.ApplyPeople(request.Generation, people))
            {
                _peopleViews = people.Select(person => new SearchPersonView(person)).ToList();
                _peopleRail.Remembered = null;
                People.ItemsSource = _peopleViews;
                PeopleSection.Visibility = people.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                UpdatePresentation();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Search people", ex);
        }
    }

    private void ClearPeople()
    {
        if (FocusManager.GetFocusedElement() is ContentControl { Content: SearchPersonView })
        {
            FocusSelectedFilter();
        }

        _peopleViews = [];
        _peopleRail.Remembered = null;
        People.ItemsSource = null;
        PeopleSection.Visibility = Visibility.Collapsed;
    }

    private void SyncCards(bool reset)
    {
        // Preserve the console-verified chip focus after a filter reload. Capture at mutation time,
        // not before the await: restoring a chip the viewer already left would steal their focus.
        var focused = FocusManager.GetFocusedElement() as Control;
        var headerFocus = focused is ToggleButton or TextBox or ContentControl { Content: SearchPersonView }
            || ReferenceEquals(focused, RetryButton) ? focused : null;
        if (reset)
        {
            _cards.Clear();
            _lastResult = null;
        }

        foreach (var card in _state.Items.Skip(_cards.Count))
        {
            _cards.Add(new CardView(card, "Search", PosterWidth, card.Type is "audiobook" or "audiobooks" ? PosterWidth : PosterHeight, landscape: false));
        }

        HookResults();
        if (headerFocus?.FocusState == FocusState.Unfocused)
        {
            headerFocus.Focus(FocusState.Programmatic);
        }
    }

    private void QueuePagingCheck()
    {
        if (_pagingCheckQueued || !_state.HasMore || _state.IsSearching || _state.IsLoadingMore || _state.Error is not null)
        {
            return;
        }

        _pagingCheckQueued = true;
        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () =>
        {
            _pagingCheckQueued = false;
            await LoadMoreIfNeededAsync();
        });
    }

    private async Task LoadMoreIfNeededAsync()
    {
        HookResults();
        if (_resultScroller is not { } scroller || Session.Client is not { } client || _search is not { } search)
        {
            return;
        }

        var lastVisible = -1;
        foreach (var container in ResultContainers())
        {
            var top = container.TransformToVisual(scroller).TransformPoint(new Point(0, 0)).Y;
            if (top < scroller.ViewportHeight && top + container.ActualHeight > 0)
            {
                lastVisible = Math.Max(lastVisible, Results.IndexFromContainer(container));
            }
        }

        if (!SearchResults.IsNearEnd(lastVisible, _cards.Count) || _state.BeginMore() is not { } request)
        {
            return;
        }

        UpdatePresentation();
        try
        {
            var page = await client.Browse.SearchPageAsync(request.Query, SearchArtwork, request.Type,
                request.Continuation, ShowAudiobooks, cancellationToken: search.Token);
            if (!search.IsCancellationRequested && _state.ApplyPage(request, page))
            {
                // Never replace ItemsSource, clear the collection, or focus a card while appending.
                SyncCards(reset: false);
                UpdatePresentation();
                QueuePagingCheck();
            }
        }
        catch (OperationCanceledException) when (search.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Search page", ex);
            if (_state.Fail(request, ex))
            {
                UpdatePresentation();
            }
        }
    }

    private void UpdatePresentation()
    {
        Status.Text = _state.Status ?? string.Empty;
        LoadingMoreRing.IsActive = _state.IsLoadingMore;
        LoadingMoreRing.Visibility = _state.IsLoadingMore ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        if (_state.Query.Length == 0)
        {
            ShowEmpty("Search your library", SearchText.Description(_filters));
        }
        else if (_state.IsSearching || _cards.Count > 0)
        {
            EmptyState.Visibility = Visibility.Collapsed;
        }
        else if (_state.Error is { } error)
        {
            ShowEmpty("Search is unavailable", error);
            RetryButton.Visibility = Visibility.Visible;
        }
        else if (_peopleViews.Count > 0)
        {
            // TvSearchScreen.kt: the people row is the answer; the status already says no titles.
            EmptyState.Visibility = Visibility.Collapsed;
        }
        else
        {
            ShowEmpty($"No matches for “{_state.Query}”", "Try a shorter title or a different filter.");
        }
    }

    private void ShowEmpty(string title, string body)
    {
        EmptyTitle.Text = title;
        EmptyBody.Text = body;
        EmptyState.Visibility = Visibility.Visible;
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        // The feedback action disappears during a reset; leave focus on a stable chip, not the shell.
        FocusSelectedFilter();
        await SearchAsync(QueryBox.Text.Trim());
    }

    private void OnFeedbackPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsUp(e.Key))
        {
            e.Handled = true;
            if (_peopleViews.Count > 0)
            {
                _peopleRail.Focus();
            }
            else
            {
                FocusSelectedFilter();
            }
        }
        else if (IsDown(e.Key))
        {
            e.Handled = true;
        }
    }

    private void OnResultFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: CardView card } item)
        {
            HookResults();
            _lastResult = card;
            CardFocus.Apply(item, focused: true);
            QueuePagingCheck();
        }
    }

    private void OnResultBlur(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: CardView } item)
        {
            CardFocus.Apply(item, focused: false);
        }
    }

    private void OnPersonFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: SearchPersonView } item)
        {
            CardFocus.Apply(item, focused: true);
            if (item.ContentTemplateRoot is FrameworkElement root && root.FindName("PersonName") is TextBlock name)
            {
                name.Opacity = 1;
            }
        }
    }

    private void OnPersonBlur(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: SearchPersonView } item)
        {
            CardFocus.Apply(item, focused: false);
            if (item.ContentTemplateRoot is FrameworkElement root && root.FindName("PersonName") is TextBlock name)
            {
                name.Opacity = 0.88;
            }
        }
    }

    private void OnPersonClick(object sender, ItemClickEventArgs e)
    {
        // TODO: person page, wave 2. Keep A harmless while the official-shaped row is focusable.
    }

    /// <summary>Back from a detail page lands on the result that opened it.</summary>
    public void RestoreFocus()
    {
        if (_lastResult is null)
        {
            return;
        }

        FocusRetry.Until(Results, () =>
            Results.ItemsPanelRoot?.Children.OfType<ContentControl>().FirstOrDefault(c => ReferenceEquals(c.Content, _lastResult)) is { } result
            && result.Focus(FocusState.Programmatic));
    }

    private void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CardView { Card: var card })
        {
            ShellPage.Current?.OpenDetail(card.Type == "episode" && card.SeriesId is { } series ? series : card.ContentId);
        }
    }
}
