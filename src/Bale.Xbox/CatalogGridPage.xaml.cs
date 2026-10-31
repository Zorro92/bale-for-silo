using System.Collections.ObjectModel;
using Bale.Core.Browse;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Microsoft.Kiota.Abstractions;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>TvSectionBrowseScreen.kt: a reusable paged catalog surface, initially backed by Home/library sections.</summary>
public sealed partial class CatalogGridPage : Page, IRestoresFocus
{
    private const int Columns = 6;
    private static readonly ArtworkWidths GridArtwork = ArtworkWidths.Tv1080 with { Poster = 276 };
    private readonly ObservableCollection<CardView> _cards = [];
    private readonly CardMenuHost _cardMenu;
    private readonly CancellationTokenSource _lifetime = new();
    private CatalogGridEntry _entry = null!;
    private ScrollViewer? _scroller;
    private bool _active;
    private bool _pagingCheckQueued;
    private int _focusGeneration;
    private int _lastFocusedIndex;

    public CatalogGridPage()
    {
        InitializeComponent();
        _cardMenu = new CardMenuHost(this, Cards, OpenCard);
        _cardMenu.Changed += item => { if (item is CardView view) _entry.Results.UpdateCard(view.Card); };
        Cards.ItemsSource = _cards;
        Cards.PreviewKeyDown += OnGridPreviewKeyDown;
        MoreRetryButton.PreviewKeyDown += OnRetryPreviewKeyDown;
        Cards.Loaded += (_, _) => HookScroller();
        Cards.ContainerContentChanging += (_, _) => QueuePagingCheck();
        Cards.SizeChanged += (_, _) => QueuePagingCheck();
    }

    // Source-specific chrome lives in a partial, not in the section/grid renderer. ToolbarSlot and
    // SourceOverlay are Borders so Native AOT callers can fill Child without casting a presenter.
    partial void ConfigureSource();
    partial void OnSourceLoaded();
    partial void UpdateSourceState();
    partial void OnGridTopUp(ref bool handled);
    partial void OnGridRightEdge(ref bool handled);
    partial void RestoreSourceFocus(ref bool handled);
    partial void CleanupSource();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not CatalogGridEntry entry) return;
        _entry = entry;
        _active = true;
        ConfigureSource();
        PageTitle.Text = UnavailableTitle.Text = entry.Source.Title;
        if (!entry.IsCurrent(Session.Client))
        {
            UnavailablePanel.Visibility = Visibility.Visible;
            return;
        }

        if (!entry.Results.HasLoaded)
        {
            await LoadAsync(entry.Results.Begin());
        }
        else
        {
            SyncCards();
            UpdateState();
            OnSourceLoaded();
            // PageFrame calls IRestoresFocus after layout, before revealing the incoming page.
            QueuePagingCheck();
        }
    }

    private async Task LoadAsync(CatalogGridRequest request)
    {
        if (!_entry.IsCurrent(Session.Client)) return;
        UpdateState();
        try
        {
            var page = await _entry.Source.LoadPageAsync(_entry.Client.Browse, GridArtwork, request.Continuation, _lifetime.Token);
            if (!_active || _lifetime.IsCancellationRequested || !_entry.IsCurrent(Session.Client) || !_entry.Results.Apply(request, page)) return;
            SyncCards();
            UpdateState();
            OnSourceLoaded();
            // TvSectionBrowseScreen: only first entry/retry restores; later pages never take focus.
            if (!request.Append) RestoreFocus();
            QueuePagingCheck();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 401)
        {
            Session.Expire();
        }
        catch (Exception ex)
        {
            Log.Error("Section grid load", ex);
            if (_active && _entry.IsCurrent(Session.Client) && _entry.Results.Fail(request, ex is ApiException { ResponseStatusCode: 404 }))
            {
                UpdateState();
                if (!request.Append && !_entry.Results.Unavailable)
                {
                    FocusRetry.Until(ErrorPanel, () => !_active || RetryButton.Focus(FocusState.Programmatic));
                }
            }
        }
    }

    private void SyncCards()
    {
        // TvSectionBrowseScreen: 40dp left/right, five 20dp gaps, six equal artwork columns.
        var width = Math.Max(1, ((Root.ActualWidth > 0 ? Root.ActualWidth : Window.Current.Bounds.Width) - 80 - 5 * 20) / Columns);
        foreach (var card in _entry.Results.Items.Skip(_cards.Count))
        {
            _cards.Add(new CardView(card, _entry.Source.Title, width, CardHeight(card, width), landscape: false)
            {
                GridFocusVisual = RailFocusVisual.ExplorePoster,
            });
        }

        foreach (var view in _cards)
        {
            if (view.Width != width) view.Update(view.Card, _entry.Source.Title, width, CardHeight(view.Card, width), landscape: false);
        }

        HookScroller();
    }

    private static double CardHeight(MediaCard card, double width) => card.Type is "audiobook" or "audiobooks" ? width : width * 1.5;

    private void UpdateState()
    {
        var state = _entry.Results;
        var initialLoading = state.IsLoading && !state.HasLoaded;
        LoadingRing.IsActive = initialLoading;
        LoadingRing.Visibility = initialLoading ? Visibility.Visible : Visibility.Collapsed;
        UnavailablePanel.Visibility = state.Unavailable ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = state.Error is not null && !state.Unavailable ? Visibility.Visible : Visibility.Collapsed;
        Cards.Visibility = !initialLoading && !state.Unavailable && state.Error is null ? Visibility.Visible : Visibility.Collapsed;
        EmptyMessage.Visibility = state.HasLoaded && _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreErrorPanel.Visibility = state.LoadMoreError is not null ? Visibility.Visible : Visibility.Collapsed;
        MoreLoading.IsActive = state.IsLoading && state.HasLoaded;
        MoreLoading.Visibility = MoreLoading.IsActive ? Visibility.Visible : Visibility.Collapsed;
        UpdateSourceState();
    }

    private void HookScroller()
    {
        if (_scroller is null && Rail.FindChild<ScrollViewer>(Cards) is { } scroller)
        {
            _scroller = scroller;
            scroller.ViewChanged += (_, _) => QueuePagingCheck();
        }
    }

    private void QueuePagingCheck()
    {
        if (!_active || _entry is null || _pagingCheckQueued || !_entry.IsCurrent(Session.Client)
            || !_entry.Results.HasMore || _entry.Results.IsLoading || _entry.Results.LoadMoreError is not null) return;
        _pagingCheckQueued = true;
        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () =>
        {
            _pagingCheckQueued = false;
            HookScroller();
            if (!_active || _scroller is not { } scroller) return;
            var lastVisible = Containers().Where(container =>
            {
                var top = container.TransformToVisual(scroller).TransformPoint(new Point(0, 0)).Y;
                return top < scroller.ViewportHeight && top + container.ActualHeight > 0;
            }).Select(container => _cards.IndexOf((CardView)container.Content)).DefaultIfEmpty(-1).Max();
            if (SearchResults.IsNearEnd(lastVisible, _cards.Count) && _entry.Results.BeginMore() is { } request)
            {
                await LoadAsync(request);
            }
        });
    }

    private IEnumerable<ContentControl> Containers() => Cards.ItemsPanelRoot?.Children.OfType<ContentControl>().Where(item => item.Content is CardView) ?? [];

    private void FocusAt(int index)
    {
        if (index < 0 || index >= _cards.Count) return;
        var target = _cards[index];
        var generation = ++_focusGeneration;
        Cards.ScrollIntoView(target);
        FocusRetry.Until(Cards, () =>
        {
            if (!_active || generation != _focusGeneration) return true;
            var container = Containers().FirstOrDefault(item => ReferenceEquals(item.Content, target));
            if (container?.Focus(FocusState.Programmatic) != true) return false;
            container.StartBringIntoView();
            return true;
        });
    }

    private void OnGridPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            if (e.Handled || _cardMenu.IsOpen || e.OriginalSource is not ContentControl { Content: CardView view }) return;
            var direction = Direction(e.Key);
            if (direction is null) return;
            var index = _cards.IndexOf(view);
            if (index < 0) return;
            e.Handled = true;
            _focusGeneration++;
            if (direction == GridDirection.Up && index < Columns)
            {
                var handled = false;
                OnGridTopUp(ref handled);
                if (handled) return;
            }

            var target = CatalogGridNavigation.Move(index, _cards.Count, Columns, direction.Value);
            if (target == index && direction == GridDirection.Right)
            {
                var handled = false;
                OnGridRightEdge(ref handled);
                if (handled) return;
            }

            if (target != index) FocusAt(target);
            else if (direction == GridDirection.Down && MoreErrorPanel.Visibility == Visibility.Visible)
            {
                MoreRetryButton.Focus(FocusState.Keyboard);
            }
        }
        catch (Exception ex)
        {
            e.Handled = true;
            Log.Error("Section grid navigation", ex);
        }
    }

    private static GridDirection? Direction(VirtualKey key) => key switch
    {
        VirtualKey.Left or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadLeftThumbstickLeft => GridDirection.Left,
        VirtualKey.Right or VirtualKey.GamepadDPadRight or VirtualKey.GamepadLeftThumbstickRight => GridDirection.Right,
        VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp => GridDirection.Up,
        VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown => GridDirection.Down,
        _ => null,
    };

    private void OnRetryPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Direction(e.Key) is not { } direction) return;
        e.Handled = true;
        if (direction == GridDirection.Up) FocusAt(_lastFocusedIndex);
    }

    private void OnCardFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: CardView view } item)
        {
            CardFocus.Apply(item, true, artScale: 1.10f);
            _lastFocusedIndex = _cards.IndexOf(view);
        }
    }

    private void OnCardBlur(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: CardView } item) CardFocus.Apply(item, false, artScale: 1.10f);
    }

    private void OnCardClick(object sender, ItemClickEventArgs e) => OpenCard(e.ClickedItem);

    private void OpenCard(object item)
    {
        if (item is not CardView view || !_entry.IsCurrent(Session.Client)) return;
        _entry.ReturnItemId = view.Card.ContentId;
        // The normal journal already holds this entry (including its pages and return identity).
        Frame.Navigate(typeof(DetailPage), view.Card.ContentId);
    }

    public void RestoreFocus()
    {
        if (!_active || _entry is null || !_entry.IsCurrent(Session.Client)) return;
        var handled = false;
        RestoreSourceFocus(ref handled);
        if (handled) return;
        var index = _cards.ToList().FindIndex(view => view.Card.ContentId == _entry.ReturnItemId);
        if (_cards.Count > 0) FocusAt(index >= 0 ? index : 0);
    }

    /// <summary>
    /// Restarts the current source after its query changes. The source owns sort/filter state;
    /// the page owns clearing cards, fencing older responses and returning to the first result.
    /// Call on the UI thread, after updating the source, not from ConfigureSource.
    /// </summary>
    public Task ReloadAsync()
    {
        if (!_active || _entry is null || !_entry.IsCurrent(Session.Client)) return Task.CompletedTask;
        _focusGeneration++;
        _entry.ReturnItemId = null;
        _lastFocusedIndex = 0;
        _cards.Clear();
        return LoadAsync(_entry.Results.Begin());
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void OnMoreRetryClick(object sender, RoutedEventArgs e)
    {
        if (_entry.Results.BeginMore(retry: true) is not { } request) return;
        // The footer disappears while loading. Hand focus back before removing its focused button.
        FocusAt(_lastFocusedIndex);
        await LoadAsync(request);
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_entry is not null && _entry.IsCurrent(Session.Client)) SyncCards();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _active = false;
        _focusGeneration++;
        _lifetime.Cancel();
        _entry?.Results.StopLoading();
        CleanupSource();
        base.OnNavigatedFrom(e);
    }
}
