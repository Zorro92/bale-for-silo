using System.Collections.ObjectModel;
using Bale.Core.Browse;
using Bale.Core.People;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>TvPersonDetailScreen.kt: portrait, identity/bio, and a filtered six-column filmography.</summary>
public sealed partial class PersonPage : Page, IBackHandler, IRestoresFocus
{
    private static readonly ArtworkWidths WorksArtwork = ArtworkWidths.Tv1080 with { Poster = 276 };
    private readonly ObservableCollection<CardView> _cards = [];
    private readonly Rail _filterRail;
    private readonly CancellationTokenSource _lifetime = new();
    private PersonPageEntry _entry = null!;
    private List<PersonFilterView> _filters = [];
    private ScrollViewer? _scroller;
    private bool _active;
    private bool _pagingCheckQueued;
    private int _focusGeneration;

    public PersonPage()
    {
        InitializeComponent();
        Works.ItemsSource = _cards;
        _filterRail = Rail.Attach(Filters);
        _filterRail.Pin = false;
        _filterRail.EntryItem = () => _filters.FirstOrDefault(filter => filter.Selected);
        Works.PreviewKeyDown += OnWorksPreviewKeyDown;
        Filters.PreviewKeyDown += OnFiltersPreviewKeyDown;
        BioButton.PreviewKeyDown += OnBioPreviewKeyDown;
        WorksRetryButton.PreviewKeyDown += OnRetryPreviewKeyDown;
        PreviewKeyDown += OnDialogPreviewKeyDown;
        PreviewKeyUp += OnDialogPreviewKeyUp;
        Works.Loaded += (_, _) => HookScroller();
        Works.ContainerContentChanging += (_, _) => QueuePagingCheck();
        Works.SizeChanged += (_, _) => QueuePagingCheck();
        // Header focus is anchored explicitly; horizontal chip navigation cannot scroll the page.
        Header.BringIntoViewRequested += (_, args) => args.Handled = true;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _active = true;
        _entry = e.Parameter as PersonPageEntry ?? new PersonPageEntry(e.Parameter as string ?? string.Empty);
        SetFilters();
        if (!_entry.FiltersLoaded) _ = LoadFiltersAsync();
        if (_entry.Profile is null)
        {
            await LoadProfileAsync();
        }
        else
        {
            ShowProfile();
            SyncCards();
            UpdateWorks();
            var generation = _focusGeneration;
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                if (_active && generation == _focusGeneration) RestoreFocus();
            });
            if (!_entry.Works.HasLoaded) await LoadWorksAsync(_entry.Works.Begin(_entry.Works.Filter));
        }
    }

    private async Task LoadProfileAsync()
    {
        if (Session.Client is not { } client) return;
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        try
        {
            var person = await client.People.GetAsync(_entry.PersonId, _lifetime.Token);
            if (!_active || _lifetime.IsCancellationRequested) return;
            _entry.Profile = person;
            ShowProfile();
            RestoreFilterFocus();
            await LoadWorksAsync(_entry.Works.Begin(_entry.Works.Filter));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Person load", ex);
            if (_active)
            {
                ErrorPanel.Visibility = Visibility.Visible;
                FocusRetry.Until(ErrorPanel, () => !_active || RetryButton.Focus(FocusState.Programmatic));
            }
        }
        finally
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowProfile()
    {
        if (_entry.Profile is not { } person) return;
        PersonName.Text = person.Name;
        Initials.Text = person.Initials;
        Initials.Visibility = person.PhotoUrl is null ? Visibility.Visible : Visibility.Collapsed;
        Portrait.Source = person.PhotoUrl is { } photo ? Artwork.Load(photo, width: 150) : null;
        var badges = person.Metadata(DateOnly.FromDateTime(DateTime.Today));
        Metadata.ItemsSource = badges.Select(text => new PersonBadgeView(text)).ToList();
        Metadata.Visibility = badges.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Bio.Text = FullBio.Text = person.Bio ?? string.Empty;
        BioButton.Visibility = person.Bio is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyBio.Visibility = person.Bio is null ? Visibility.Visible : Visibility.Collapsed;
        Works.Visibility = Visibility.Visible;
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = ErrorPanel.Visibility = Visibility.Collapsed;
        ResizeDialog();
    }

    private async Task LoadFiltersAsync()
    {
        if (Session.Client is not { } client) return;
        try
        {
            var filters = await client.People.FiltersAsync(_entry.PersonId, _lifetime.Token);
            if (!_active || _lifetime.IsCancellationRequested) return;
            var headerFocused = FilterButtons().Any(button => button.FocusState != FocusState.Unfocused);
            _entry.Filters = filters;
            _entry.FiltersLoaded = true;
            _entry.ShowAudiobooks = filters.Contains(PersonMediaFilter.Audiobooks);
            var changedFilter = !filters.Contains(_entry.Works.Filter);
            if (changedFilter)
            {
                await ApplyFilterAsync(PersonMediaFilter.All);
            }
            else
            {
                SetFilters();
                if (headerFocused) RestoreFilterFocus();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Person filters", ex);
        }
    }

    private void SetFilters()
    {
        _filters = _entry.Filters.Select(filter => new PersonFilterView(filter, filter == _entry.Works.Filter)).ToList();
        _filterRail.Remembered = null;
        Filters.ItemsSource = _filters;
    }

    private async void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PersonFilterView view })
        {
            await ApplyFilterAsync(view.Filter);
        }
    }

    private async Task ApplyFilterAsync(PersonMediaFilter filter)
    {
        if (filter == _entry.Works.Filter && _entry.Works.Error is null)
        {
            SetFilters();
            RestoreFilterFocus();
            return;
        }

        _focusGeneration++;
        _entry.ReturnItemId = null;
        var request = _entry.Works.Begin(filter);
        _cards.Clear();
        SetFilters();
        RestoreFilterFocus();
        await LoadWorksAsync(request);
    }

    private async Task LoadWorksAsync(PersonWorksRequest request)
    {
        if (Session.Client is not { } client) return;
        UpdateWorks();
        try
        {
            var page = await client.People.WorksAsync(_entry.PersonId, request.Filter, WorksArtwork, request.Continuation,
                _entry.ShowAudiobooks, _lifetime.Token);
            if (_active && !_lifetime.IsCancellationRequested && _entry.Works.Apply(request, page))
            {
                SyncCards();
                UpdateWorks();
                QueuePagingCheck();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Person works", ex);
            if (_active && _entry.Works.Fail(request)) UpdateWorks();
        }
    }

    private void SyncCards()
    {
        // TvPersonDetailScreen.kt uses six equal columns with 20dp gaps and 50dp page insets.
        var width = Math.Max(1, ((Root.ActualWidth > 0 ? Root.ActualWidth : Window.Current.Bounds.Width) - 100 - 5 * 20) / 6);
        foreach (var card in _entry.Works.Items.Skip(_cards.Count))
        {
            _cards.Add(new CardView(card, "Filmography", width, card.Type is "audiobook" or "audiobooks" ? width : width * 1.5, landscape: false));
        }

        foreach (var view in _cards)
        {
            var height = view.Card.Type is "audiobook" or "audiobooks" ? width : width * 1.5;
            if (view.Width != width) view.Update(view.Card, "Filmography", width, height, landscape: false);
        }

        HookScroller();
    }

    private void UpdateWorks()
    {
        var state = _entry.Works;
        Count.Text = state.CountLabel ?? string.Empty;
        WorksLoading.IsActive = state.IsLoading;
        WorksLoading.Visibility = state.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        EmptyWorks.Visibility = state.HasLoaded && !state.IsLoading && state.Error is null && _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WorksError.Text = state.Error ?? string.Empty;
        WorksError.Visibility = WorksRetryButton.Visibility = state.Error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void HookScroller()
    {
        if (_scroller is null && Rail.FindChild<ScrollViewer>(Works) is { } scroller)
        {
            _scroller = scroller;
            scroller.ViewChanged += (_, _) => QueuePagingCheck();
        }
    }

    private void QueuePagingCheck()
    {
        if (!_active || _entry is null || _pagingCheckQueued || !_entry.Works.HasMore || _entry.Works.IsLoading || _entry.Works.Error is not null) return;
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
            }).Select(container => Works.IndexFromContainer(container)).DefaultIfEmpty(-1).Max();
            if (SearchResults.IsNearEnd(lastVisible, _cards.Count) && _entry.Works.BeginMore() is { } request)
            {
                await LoadWorksAsync(request);
            }
        });
    }

    private async void OnWorksRetryClick(object sender, RoutedEventArgs e)
    {
        var request = _entry.Works.HasMore ? _entry.Works.BeginMore(retry: true) : _entry.Works.Begin(_entry.Works.Filter);
        if (request is null) return;
        if (!request.Append) _cards.Clear();
        // Hiding the footer retry cannot leave focus on a disabled/removed control.
        FocusSelectedFilter();
        await LoadWorksAsync(request);
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e) => await LoadProfileAsync();

    private IEnumerable<ContentControl> Containers() => Works.ItemsPanelRoot?.Children.OfType<ContentControl>().Where(item => item.Content is CardView) ?? [];
    private IEnumerable<ToggleButton> FilterButtons() =>
        Filters.ItemsPanelRoot?.Children.OfType<DependencyObject>()
            .Select(container => FocusManager.FindFirstFocusableElement(container)).OfType<ToggleButton>() ?? [];

    private bool FocusSelectedFilter() => _filterRail.Focus();

    private void RestoreFilterFocus()
    {
        var generation = ++_focusGeneration;
        FocusRetry.Until(Filters, () => !_active || generation != _focusGeneration || FocusSelectedFilter());
    }

    private void OnHeaderFocus(object sender, RoutedEventArgs e) => _scroller?.ChangeView(null, 0, null);

    private void FocusWorkAt(int index)
    {
        if (index < 0 || index >= _cards.Count) return;
        var target = _cards[index];
        var generation = ++_focusGeneration;
        Works.ScrollIntoView(target);
        var attempts = 60;
        FocusRetry.Until(Works, () =>
        {
            if (!_active || generation != _focusGeneration) return true;
            var container = Containers().FirstOrDefault(item => ReferenceEquals(item.Content, target));
            if (container is not null && container.Focus(FocusState.Programmatic))
            {
                container.StartBringIntoView();
                return true;
            }

            return --attempts <= 0 && FocusSelectedFilter();
        });
    }

    private static bool IsUp(VirtualKey key) => key is VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp;
    private static bool IsDown(VirtualKey key) => key is VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown;

    private void OnWorksPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is not ContentControl { Content: CardView } item) return;
        var index = Works.IndexFromContainer(item);
        var up = IsUp(e.Key);
        var down = IsDown(e.Key);
        var step = e.Key switch
        {
            VirtualKey.Left or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadLeftThumbstickLeft => -1,
            VirtualKey.Right or VirtualKey.GamepadDPadRight or VirtualKey.GamepadLeftThumbstickRight => 1,
            _ => 0,
        };
        if (index < 0 || (!up && !down && step == 0)) return;
        _focusGeneration++;
        if (up || down)
        {
            e.Handled = true;
            // Exact column steps: Down above an incomplete final row stays put, never slides sideways.
            var target = index + (up ? -6 : 6);
            if (up && index < 6) FocusSelectedFilter();
            else if (target >= 0 && target < _cards.Count) FocusWorkAt(target);
            else if (down && WorksRetryButton.Visibility == Visibility.Visible) WorksRetryButton.Focus(FocusState.Keyboard);
        }
        else if ((step < 0 && index % 6 == 0) || (step > 0 && (index % 6 == 5 || index == _cards.Count - 1)))
        {
            e.Handled = true;
        }
    }

    private void OnFiltersPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsUp(e.Key))
        {
            _focusGeneration++;
            e.Handled = true;
            if (BioButton.Visibility == Visibility.Visible) BioButton.Focus(FocusState.Keyboard);
            else _scroller?.ChangeView(null, 0, null);
        }
        else if (IsDown(e.Key))
        {
            e.Handled = true;
            if (_cards.Count > 0) FocusWorkAt(0);
            else if (WorksRetryButton.Visibility == Visibility.Visible) WorksRetryButton.Focus(FocusState.Keyboard);
        }
    }

    private void OnBioPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsUp(e.Key) || IsDown(e.Key))
        {
            e.Handled = true;
            if (IsDown(e.Key)) FocusSelectedFilter();
        }
    }

    private void OnRetryPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsUp(e.Key) || IsDown(e.Key))
        {
            e.Handled = true;
            if (IsUp(e.Key))
            {
                if (_cards.Count > 0) FocusWorkAt(_cards.Count - 1);
                else FocusSelectedFilter();
            }
        }
    }

    private void OnWorkFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: CardView } item) CardFocus.Apply(item, true);
    }

    private void OnWorkBlur(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: CardView } item) CardFocus.Apply(item, false);
    }

    private void OnWorkClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CardView view) return;
        _entry.ReturnItemId = view.Card.ContentId;
        var frame = Frame;
        if (frame.Navigate(typeof(DetailPage), view.Card.ContentId) && frame.BackStack.Count > 0)
        {
            frame.BackStack[frame.BackStack.Count - 1] = new PageStackEntry(typeof(PersonPage), _entry, null);
        }
    }

    public void RestoreFocus()
    {
        var index = _cards.ToList().FindIndex(view => view.Card.ContentId == _entry.ReturnItemId);
        if (index >= 0) FocusWorkAt(index);
        else RestoreFilterFocus();
    }

    private void OnBioClick(object sender, RoutedEventArgs e)
    {
        Works.IsEnabled = false;
        BioDialog.Visibility = Visibility.Visible;
        BioScroller.ChangeView(null, 0, null, disableAnimation: true);
        ResizeDialog();
        FocusRetry.Until(BioDialog, () => !_active || BioDialog.Visibility != Visibility.Visible || BioScroller.Focus(FocusState.Programmatic));
    }

    private void CloseBio()
    {
        BioDialog.Visibility = Visibility.Collapsed;
        Works.IsEnabled = true;
        FocusRetry.Until(Header, () => !_active || BioButton.Focus(FocusState.Programmatic));
    }

    public bool OnBack()
    {
        if (BioDialog.Visibility != Visibility.Visible) return false;
        CloseBio();
        return true;
    }

    private void OnDialogPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (BioDialog.Visibility != Visibility.Visible)
        {
            if (IsUp(e.Key) || IsDown(e.Key) || e.Key is VirtualKey.Left or VirtualKey.Right
                or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadDPadRight
                or VirtualKey.GamepadLeftThumbstickLeft or VirtualKey.GamepadLeftThumbstickRight)
            {
                // A direction belongs to the viewer, not an earlier deferred return-focus request.
                _focusGeneration++;
            }

            return;
        }

        if (IsUp(e.Key) || IsDown(e.Key))
        {
            BioScroller.ChangeView(null, Math.Clamp(BioScroller.VerticalOffset + (IsDown(e.Key) ? 180 : -180), 0, BioScroller.ScrollableHeight), null);
            e.Handled = true;
        }
        else if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadDPadRight
            or VirtualKey.GamepadLeftThumbstickLeft or VirtualKey.GamepadLeftThumbstickRight or VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA)
        {
            e.Handled = true;
        }
    }

    private void OnDialogPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (BioDialog.Visibility == Visibility.Visible && e.Key is VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA)
        {
            CloseBio();
            e.Handled = true;
        }
    }

    private void OnBioBackgroundTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, BioDialog))
        {
            CloseBio();
            e.Handled = true;
        }
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_entry is not null) SyncCards();
        ResizeDialog();
    }

    private void ResizeDialog()
    {
        BioDialogPanel.Width = Root.ActualWidth * 0.56;
        BioDialogPanel.Height = Root.ActualHeight * 0.76;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _active = false;
        _lifetime.Cancel();
        _entry.Works.StopLoading();
        base.OnNavigatedFrom(e);
    }
}
