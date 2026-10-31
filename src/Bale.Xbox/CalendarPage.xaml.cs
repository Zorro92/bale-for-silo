using System.Collections.ObjectModel;
using Bale.Core;
using Bale.Core.Browse;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Microsoft.Kiota.Abstractions;
using Windows.System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>TvCalendarScreen: persistent week controls above day-snapped horizontal poster shelves.</summary>
public sealed partial class CalendarPage : Page, IRestoresFocus
{
    private readonly List<CalendarFilterView> _filters =
        [new(CalendarFilter.Following), new(CalendarFilter.Trending), new(CalendarFilter.All)];
    private readonly ObservableCollection<CalendarDayView> _days = new();
    private readonly Rail _filterRail;
    private readonly Rail _dateRail;
    private readonly Rail _actionRail;
    private CalendarFilter _filter = CalendarFilters.FromPreference(AppSettings.CalendarFilter);
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Now);
    private DateOnly _weekStart;
    private DateOnly _selectedDay;
    private CalendarWeek _week;
    private CalendarFeed? _feed;
    private SiloClient? _client;
    private string? _profileId;
    private CancellationTokenSource? _loadCancellation;
    private int _loadVersion;
    private int _focusVersion;
    private int _tintVersion;
    private bool _active;
    private bool _loading;
    private bool _needsLoad = true;
    private bool _entryPending = true;
    private bool _returning;
    private bool _updating;
    // A root-tab re-selection resets to the filter. Only a click creates a detail-return target.
    private CalendarReturnTarget? _returnTarget;
    private AmbientTint.WashAnimation? _wash;
    private Color? _tint;

    public CalendarPage()
    {
        InitializeComponent();
        _week = CalendarWeek.Empty(_today);
        _weekStart = _week.Start;
        _selectedDay = _today;
        for (var i = 0; i < 7; i++)
        {
            _days.Add(new CalendarDayView());
        }

        Filters.ItemsSource = _filters;
        WeekDates.ItemsSource = DayShelves.ItemsSource = _days;
        _filterRail = Rail.Attach(Filters);
        _filterRail.Pin = false;
        _filterRail.EntryItem = () => _filters.First(view => view.Filter == _filter);
        _dateRail = Rail.Attach(WeekDates);
        _dateRail.Pin = false;
        _dateRail.EntryItem = () => _days.FirstOrDefault(day => day.Date == _selectedDay);
        _dateRail.EdgeReached += step => (step < 0 ? PreviousWeekButton : NextWeekButton).Focus(FocusState.Keyboard);
        _actionRail = Rail.Attach(StateActions);
        _actionRail.Pin = false;
        PreviewKeyDown += OnPreviewKeyDown;
        GettingFocus += OnGettingFocus;
        // The calendar's day snap is the only vertical scrolling authority, including for controls.
        CalendarScroller.BringIntoViewRequested += (_, args) => args.Handled = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Render(_week, "Loading");
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Log.Info("Calendar: navigated to");
        await LoadAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _active = true;
        if (_tint is { } tint)
        {
            ShowTint(tint);
        }

        if (_returnTarget is not null && !_needsLoad && !_loading)
        {
            RestoreReturnTarget();
        }
        else if (_entryPending && !_loading)
        {
            FocusContent();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _active = false;
        _needsLoad |= _loading;
        _loadVersion++;
        _focusVersion++;
        _tintVersion++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _loading = false;
        _wash?.Dispose();
        _wash = null;
    }

    private async Task LoadAsync(bool refresh = false)
    {
        if (Session.Client is not { } client)
        {
            ShowMessage("Error", "Sign in to load calendar.");
            _entryPending = true;
            return;
        }

        // A CalendarFeed cache belongs to this screen AND profile, never to another viewer.
        if (!ReferenceEquals(_client, client) || _profileId != client.ProfileId)
        {
            _client = client;
            _profileId = client.ProfileId;
            _feed = new CalendarFeed(client.Api, client.Browse);
        }

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var version = ++_loadVersion;
        var profile = client.ProfileId;
        var feed = _feed!;
        _today = DateOnly.FromDateTime(DateTime.Now);
        var timezone = TimeZoneInfo.Local;
        var cached = refresh && _week.Start == _weekStart && !_needsLoad ? _week : feed.CachedWeek(_weekStart, _filter, timezone);
        _needsLoad = true;
        _loading = true;
        Render(cached ?? CalendarWeek.Empty(_weekStart), cached is null ? "Loading" : cached.HasItems ? "Content" : "Empty");
        try
        {
            var week = await feed.LoadWeekAsync(_weekStart, _filter, timezone, cancellation.Token);
            if (!IsCurrentLoad())
            {
                return;
            }

            _needsLoad = false;
            Render(week, week.HasItems ? "Content" : "Empty");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer filter/week or leaving the page owns the result now.
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 401)
        {
            if (IsCurrentLoad())
            {
                Session.Expire();
            }
        }
        catch (Exception ex)
        {
            if (IsCurrentLoad())
            {
                Log.Error("Calendar load", ex);
                // CalendarViewModel keeps usable cached shelves when revalidation fails.
                if (cached?.HasItems != true)
                {
                    ShowMessage("Error", CalendarFeed.ErrorMessage(ex));
                }
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                _loading = false;
                if (_active)
                {
                    if (_returnTarget is not null)
                    {
                        RestoreReturnTarget();
                    }
                    else if (_entryPending)
                    {
                        FocusContent();
                    }
                }
            }
        }

        bool IsCurrentLoad() => version == _loadVersion && !cancellation.IsCancellationRequested
            && ReferenceEquals(client, Session.Client) && client.ProfileId == profile;
    }

    private void Render(CalendarWeek week, string state)
    {
        _updating = true;
        try
        {
            _week = week;
            for (var i = 0; i < _days.Count; i++)
            {
                _days[i].Update(week.Days[i], _today, _selectedDay);
            }

            foreach (var filter in _filters)
            {
                filter.Selected = filter.Filter == _filter;
            }

            MonthYear.Text = week.MonthYear();
            TodayButton.Visibility = week.IsCurrentWeek(_today) ? Visibility.Collapsed : Visibility.Visible;
            if (state == "Empty")
            {
                ShowMessage(state);
            }
            else
            {
                VisualStateManager.GoToState(this, state, false);
            }
        }
        finally
        {
            _updating = false;
        }

        _ = UpdateTintAsync(week.Days.SelectMany(day => day.Items).FirstOrDefault(item => item.PosterUrl is not null)?.PosterUrl);
    }

    private void ShowMessage(string state, string? error = null)
    {
        MessageTitle.Text = error ?? CalendarFilters.EmptyTitle(_filter);
        MessageCopy.Text = state == "Error" ? "Press the week arrows to try another week." : CalendarFilters.EmptyCopy(_filter);
        _actionRail.Remembered = null;
        StateActions.ItemsSource = state == "Error"
            ? new List<CalendarActionView> { new("Refresh") }
            : CalendarFilters.EmptyLinks(_filter).Select(filter => new CalendarActionView(filter.ToString(), filter)).ToList();
        VisualStateManager.GoToState(this, state, false);
    }

    private async void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CalendarFilterView view })
        {
            return;
        }

        var changed = _filter != view.Filter;
        _filter = view.Filter;
        foreach (var filter in _filters)
        {
            filter.Selected = filter.Filter == _filter;
        }

        if (changed)
        {
            AppSettings.CalendarFilter = CalendarFilters.Preference(_filter);
            await LoadAsync();
        }
    }

    private async void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CalendarActionView action })
        {
            if (action.Filter is { } filter)
            {
                _filter = filter;
                AppSettings.CalendarFilter = CalendarFilters.Preference(filter);
            }

            // The pressed action leaves with the message: keep focus in the persistent controls.
            FocusContent();
            await LoadAsync(refresh: action.Filter is null);
        }
    }

    private async void OnPreviousWeekClick(object sender, RoutedEventArgs e) => await MoveWeekAsync(-7);
    private async void OnNextWeekClick(object sender, RoutedEventArgs e) => await MoveWeekAsync(7);

    private async Task MoveWeekAsync(int days)
    {
        _weekStart = _weekStart.AddDays(days);
        _today = DateOnly.FromDateTime(DateTime.Now);
        _selectedDay = CalendarWeek.Empty(_weekStart).InitialSelectedDay(_today);
        await LoadAsync();
    }

    private async void OnTodayClick(object sender, RoutedEventArgs e)
    {
        _today = DateOnly.FromDateTime(DateTime.Now);
        _weekStart = CalendarWeek.StartOfWeek(_today);
        _selectedDay = _today;
        // Today disappears on arrival; the highlighted date, not the shell, receives its focus.
        var load = LoadAsync();
        FocusSelectedDay();
        await load;
    }

    private void OnDayClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CalendarDayView day })
        {
            _selectedDay = day.Date;
            foreach (var date in _days)
            {
                date.Select(_selectedDay);
            }

            if (_week.HasItems && DayShelves.Visibility == Visibility.Visible)
            {
                PinShelf(day);
                if (day.Cards.Count > 0)
                {
                    FocusDay(day, day.Cards[0]);
                }
            }
        }
    }

    private void OnDayFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CalendarDayView day })
        {
            day.SetFocused(true);
        }
    }

    private void OnDayBlur(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CalendarDayView day })
        {
            day.SetFocused(false);
        }
    }

    private void OnControlsFocus(object sender, RoutedEventArgs e) => ScrollToControls();

    /// <summary>TvMainShell hands Calendar entry to the active filter, not a geometric descendant.</summary>
    internal void FocusContent()
    {
        _entryPending = false;
        _returning = false;
        var version = ++_focusVersion;
        ScrollToControls();
        FocusRetry.Until(Filters, () => !_active || version != _focusVersion || _filterRail.Focus());
    }

    private void FocusSelectedDay()
    {
        var version = ++_focusVersion;
        ScrollToControls();
        FocusRetry.Until(WeekDates, () => !_active || version != _focusVersion || _dateRail.Focus());
    }

    private void FocusDay(CalendarDayView day, CalendarCardView? card = null)
    {
        var version = ++_focusVersion;
        FocusRetry.Until(DayShelves, () =>
        {
            if (!_active || version != _focusVersion)
            {
                return true;
            }

            PinShelf(day);
            if (ShelfList(day) is not { Tag: Rail rail })
            {
                return false;
            }

            rail.Remembered = card ?? day.LastFocused;
            return rail.Focus(FocusState.Keyboard);
        });
    }

    /// <summary>Every vertical move is explicit. Edges consume Up/Down even when focus cannot move.</summary>
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            var step = e.Key switch
            {
                VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp => -1,
                VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown => 1,
                _ => 0,
            };
            var source = e.OriginalSource as DependencyObject;
            if (step == 0)
            {
                MoveBesideDates(source, e);
                return;
            }

            e.Handled = true;
            // calendarUpFallbackAction holds repeated Up at a shelf/control handoff until release.
            if (step < 0 && e.KeyStatus.WasKeyDown && IsWithin(source, Controls))
            {
                return;
            }

            if (IsWithin(source, Filters))
            {
                if (step < 0)
                {
                    _focusVersion++;
                    ShellPage.Current?.FocusSelectedTab();
                }
                else
                {
                    FocusSelectedDay();
                }
            }
            else if (IsWithin(source, WeekStrip))
            {
                if (step < 0)
                {
                    FocusContent();
                }
                else if (DayShelves.Visibility == Visibility.Visible && _days.FirstOrDefault(day => day.Cards.Count > 0) is { } first)
                {
                    FocusDay(first);
                }
                else if (StatePanel.Visibility == Visibility.Visible && !_loading)
                {
                    _focusVersion++;
                    _actionRail.Focus(FocusState.Keyboard);
                }
            }
            else if (FocusedDay(source) is { } day)
            {
                var index = _days.IndexOf(day) + step;
                while (index >= 0 && index < _days.Count && _days[index].Cards.Count == 0)
                {
                    index += step;
                }

                if (index < 0)
                {
                    if (!e.KeyStatus.WasKeyDown)
                    {
                        FocusSelectedDay();
                    }
                }
                else if (index < _days.Count)
                {
                    FocusDay(_days[index]);
                }
            }
            else if (IsWithin(source, StateActions) && step < 0)
            {
                FocusSelectedDay();
            }
        }
        catch (Exception ex)
        {
            e.Handled = true;
            Log.Error("Calendar navigation", ex);
        }
    }

    /// <summary>The dates are a Rail; its two edges connect only to their own week's arrows.</summary>
    private void MoveBesideDates(DependencyObject? source, KeyRoutedEventArgs e)
    {
        var step = e.Key switch
        {
            VirtualKey.Left or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadLeftThumbstickLeft => -1,
            VirtualKey.Right or VirtualKey.GamepadDPadRight or VirtualKey.GamepadLeftThumbstickRight => 1,
            _ => 0,
        };
        if (step == 0 || !IsWithin(source, WeekStrip) || IsWithin(source, WeekDates))
        {
            return;
        }

        e.Handled = true;
        if (IsWithin(source, PreviousWeekButton) && step > 0)
        {
            _dateRail.Remembered = _days[0];
            FocusDateEdge(_days[0]);
        }
        else if (IsWithin(source, NextWeekButton))
        {
            if (step < 0)
            {
                FocusDateEdge(_days[^1]);
            }
            else if (TodayButton.Visibility == Visibility.Visible)
            {
                TodayButton.Focus(FocusState.Keyboard);
            }
        }
        else if (IsWithin(source, TodayButton) && step < 0)
        {
            NextWeekButton.Focus(FocusState.Keyboard);
        }
    }

    private void FocusDateEdge(CalendarDayView day)
    {
        // Rail entry normally targets the selected day; crossing an arrow explicitly names an edge.
        var entry = _dateRail.EntryItem;
        _dateRail.EntryItem = () => day;
        try
        {
            _dateRail.Focus(FocusState.Keyboard);
        }
        finally
        {
            _dateRail.EntryItem = entry;
        }
    }

    private void OnGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        try
        {
            if (!_returning && !IsWithin(args.OldFocusedElement, this) && IsWithin(args.NewFocusedElement, this)
                && ItemControl(Filters, _filters.First(view => view.Filter == _filter)) is { } filter)
            {
                args.TrySetNewFocusedElement(filter);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Calendar entry focus", ex);
        }
    }

    private void OnRailLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListView { Tag: null, DataContext: CalendarDayView day } list)
        {
            var rail = Rail.Attach(list);
            rail.Remembered = day.LastFocused;
            rail.Entered += () => PinShelf(day);
            rail.ItemFocused += (_, item) =>
            {
                if (!_updating && !_returning)
                {
                    day.LastFocused = item as CalendarCardView;
                }
            };
            list.Tag = rail;
        }
    }

    private void OnCardFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: CalendarCardView view } item)
        {
            CardFocus.Apply(item, focused: true, artScale: 1.06f);
            _ = UpdateTintAsync(view.Item.PosterUrl);
        }
    }

    private void OnCardBlur(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: CalendarCardView } item)
        {
            CardFocus.Apply(item, focused: false, artScale: 1.06f);
        }
    }

    private void OnCardClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CalendarCardView view && sender is FrameworkElement { DataContext: CalendarDayView day }
            && ShellPage.Current is { } shell)
        {
            _returnTarget = new CalendarReturnTarget(day.Date, view.Item.ContentId, _days.IndexOf(day), day.Cards.IndexOf(view));
            day.LastFocused = view;
            _returning = true;
            _focusVersion++;
            if (view.Item.SeriesEntry is { } series)
            {
                shell.OpenDetail(series);
            }
            else
            {
                shell.OpenDetail(view.Item.ContentId);
            }
        }
    }

    /// <summary>Back belongs to the shell's normal content → tab → Home chain.</summary>
    public void RestoreFocus()
    {
        _entryPending = false;
        _returning = _returnTarget is not null;
        if (_needsLoad)
        {
            _ = LoadAsync(refresh: true);
        }
        else
        {
            RestoreReturnTarget();
        }
    }

    private void RestoreReturnTarget()
    {
        if (_returnTarget is not { } target || DayShelves.Visibility != Visibility.Visible || _week.Locate(target) is not { } location)
        {
            _returnTarget = null;
            FocusContent();
            return;
        }

        // Core resolves the event identity, following corrected air dates across day shelves.
        var day = _days[location.DayIndex];
        var card = day.Cards[location.ItemIndex];
        var version = ++_focusVersion;
        var attempts = 0;
        FocusRetry.Until(DayShelves, () =>
        {
            if (!_active || version != _focusVersion)
            {
                return true;
            }

            PinShelf(day);
            if (ShelfList(day) is { Tag: Rail rail })
            {
                rail.Remembered = card;
                if (rail.Focus() && ItemControl(rail.List, card) is { FocusState: not FocusState.Unfocused })
                {
                    day.LastFocused = card;
                    _returnTarget = null;
                    _returning = false;
                    return true;
                }
            }

            if (++attempts >= 50)
            {
                _returnTarget = null;
                FocusContent();
                return true;
            }

            return false;
        });
    }

    private void PinShelf(CalendarDayView day)
    {
        if (ItemContainer(DayShelves, day) is FrameworkElement shelf)
        {
            var top = shelf.TransformToVisual(CalendarContent).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            CalendarScroller.ChangeView(null, Math.Max(0, top), null);
        }
    }

    private void ScrollToControls() => CalendarScroller.ChangeView(null, 0, null);

    private ListView? ShelfList(CalendarDayView day) =>
        ItemContainer(DayShelves, day) is { } container ? Rail.FindChild<ListView>(container) : null;

    private CalendarDayView? FocusedDay(DependencyObject? source) =>
        _days.FirstOrDefault(day => ShelfList(day) is { } list && IsWithin(source, list));

    private static DependencyObject? ItemContainer(ItemsControl list, object item) =>
        list.ItemsPanelRoot?.Children.FirstOrDefault(container => container switch
        {
            ContentControl control => ReferenceEquals(control.Content, item),
            ContentPresenter presenter => ReferenceEquals(presenter.Content, item),
            FrameworkElement element => ReferenceEquals(element.DataContext, item),
            _ => false,
        });

    private static Control? ItemControl(ItemsControl list, object item) => ItemContainer(list, item) switch
    {
        Control { IsTabStop: true } control => control,
        { } container => FocusManager.FindFirstFocusableElement(container) as Control,
        _ => null,
    };

    private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private void OnPosterSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Image { Source: BitmapImage image } && e.NewSize.Width > 0)
        {
            // Take the decode width from the themed layout, never a second copy of its poster size.
            image.DecodePixelWidth = (int)Math.Ceiling(e.NewSize.Width);
        }
    }

    private async Task UpdateTintAsync(Uri? poster)
    {
        var version = ++_tintVersion;
        var tint = poster is null ? null : await AmbientTint.SampleAsync(poster);
        if (version != _tintVersion)
        {
            return;
        }

        _tint = tint;
        if (!_active)
        {
            return;
        }

        if (tint is { } color)
        {
            ShowTint(color);
        }
        else
        {
            _wash?.Dispose();
            _wash = null;
        }
    }

    private void ShowTint(Color tint)
    {
        try
        {
            _wash ??= new AmbientTint.WashAnimation(Wash);
            _wash.Show(tint);
        }
        catch (Exception ex)
        {
            Log.Error("Calendar backdrop", ex);
        }
    }
}
