using System.Collections.ObjectModel;
using Bale.Core.Browse;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Microsoft.Kiota.Abstractions;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.System;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>
/// A root screen: Home, or a library tab's Browse landing, drawn like Silo's Android TV app
/// (TvSkylineSectionFeed): a passive marquee previews the focused card above the server's rows, over
/// a wash tinted from that card's art.
/// </summary>
public sealed partial class BrowsePage : Page, IRestoresFocus
{
    public const string HomeParameter = "home";

    /// <summary>The For You tab: recommendation rows, laid out like Home (TvRecommendationsScreen).</summary>
    public const string ForYouParameter = "foryou";

    private bool _forYou;

    /// <summary>Marquee updates wait for focus to rest this long, so scrubbing a row doesn't thrash backdrops (guide §4.2).</summary>
    private static readonly TimeSpan MarqueeSettle = TimeSpan.FromMilliseconds(150);

    /// <summary>The logo slot's fixed height, so the block doesn't jump as art loads (TvFocusMarquee).</summary>
    private const double LogoSlotHeight = 84;

    private readonly DispatcherTimer _marqueeTimer = new() { Interval = MarqueeSettle };

    /// <summary>How long a marquee waits for its logo before showing the text title instead.</summary>
    private readonly DispatcherTimer _logoGrace = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private CardView? _pendingMarquee;
    private CardView? _shownMarquee;
    private string? _libraryId;
    private string? _libraryMediaScope;
    private string? _libraryTitle;
    private bool _focusFirstRow;
    private bool _loading;
    private bool _isLoaded;
    private bool _updatingRows;
    private bool _returning;
    private int _focusVersion;
    private readonly ObservableCollection<RowView> _rows = new();
    private readonly CardMenuHost _cardMenu;
    private MaskedArt? _art;
    private AmbientTint.WashAnimation? _wash;
    private Color? _tint;

    /// <summary>The last card focused anywhere on the page: where Down from the top bar returns.</summary>
    private (int Row, BrowseTileView Card)? _lastCard;

    public BrowsePage()
    {
        InitializeComponent();
        _cardMenu = new CardMenuHost(this, RowsScroller, OpenCard,
            view => _libraryId is null && !_forYou ? _rows.FirstOrDefault(row => row.Cards.Contains(view))?.Row : null);
        _cardMenu.Changed += OnCardChanged;
        _cardMenu.DismissedFromHome += OnHomeCardDismissed;

        Rows.ItemsSource = _rows;
        // The tunnelling pass runs before the ListView can make its own geometric (sideways) move.
        RowsScroller.PreviewKeyDown += OnRowsKeyDown;
        RowsScroller.GettingFocus += OnRowsGettingFocus;
        RowsScroller.LostFocus += (_, _) =>
        {
            if (!_updatingRows && !_returning && !IsInRows(FocusManager.GetFocusedElement() as DependencyObject))
            {
                _focusVersion++;
            }
        };
        EmptyPanel.PreviewKeyDown += OnStateKeyDown;
        ErrorPanel.PreviewKeyDown += OnStateKeyDown;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _logoGrace.Tick += (_, _) =>
        {
            _logoGrace.Stop();
            if (MarqueeLogo.Opacity == 0)
            {
                MarqueeTitle.Opacity = 1;
            }
        };
        _marqueeTimer.Tick += (_, _) =>
        {
            _marqueeTimer.Stop();
            ShowMarquee(_pendingMarquee);
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        try
        {
            _wash = new AmbientTint.WashAnimation(Wash);
            _wash.Show(_tint);
            _art = new MaskedArt(ArtBox);
            if (_shownMarquee?.Card.BackdropUrl is { } pending)
            {
                _art.Show(pending);
            }
        }
        catch (Exception ex)
        {
            // The marquee remains usable even if the optional Composition backdrop cannot load.
            Log.Error("Marquee backdrop", ex);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _returning = _lastCard is not null;
        _focusVersion++;
        _marqueeTimer.Stop();
        _logoGrace.Stop();
        _art?.Dispose();
        _art = null;
        _wash?.Dispose();
        _wash = null;
    }

    public static string LibraryParameter(string libraryId, string libraryName) => $"library:{libraryId}:{libraryName}";

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Log.Info("Browse: navigated to " + (e.Parameter as string ?? ""));
        var parameter = e.Parameter as string ?? HomeParameter;
        _forYou = parameter == ForYouParameter;
        _libraryId = null;
        _libraryMediaScope = null;
        _libraryTitle = null;
        if (e.Parameter is LibraryRecommendedEntry library)
        {
            _libraryId = library.LibraryId;
            _libraryMediaScope = library.MediaScope;
            _libraryTitle = library.Title;
        }
        else if (parameter.StartsWith("library:", StringComparison.Ordinal))
        {
            var parts = parameter.Split(new[] { ':' }, 3);
            _libraryId = parts[1];
            _libraryTitle = parts.Length > 2 ? parts[2] : "This library";
        }

        VisualStateManager.GoToState(this, _forYou ? "ForYou" : _libraryId is null ? "Home" : "Library", false);
        await LoadAsync();
    }

    private async Task LoadAsync(bool refresh = false)
    {
        if (_loading || Session.Client is not { } client)
        {
            return;
        }

        _loading = true;
        if (_rows.Count == 0)
        {
            VisualStateManager.GoToState(this, "Loading", false);
        }

        try
        {
            var rows = _forYou ? await client.Browse.LoadForYouAsync(ArtworkWidths.Tv1080)
                : _libraryId is null ? await client.Browse.LoadHomeAsync(ArtworkWidths.Tv1080)
                : await client.Browse.LoadLibraryAsync(_libraryId, ArtworkWidths.Tv1080);
            if (!ReferenceEquals(client, Session.Client) || (refresh && !_isLoaded))
            {
                return;
            }

            if (_libraryId is not null) rows = LibraryRecommendedRows.For(rows, _libraryMediaScope);
            var restoreFocus = UpdateRows(rows, refresh);
            if (_rows.Count == 0)
            {
                ShowEmpty(!refresh || restoreFocus);
                return;
            }

            VisualStateManager.GoToState(this, "Content", false);
            ShowMarquee((_lastCard?.Card as CardView) ?? _shownMarquee ?? _rows[0].Cards.OfType<CardView>().First(), refresh);
            if (refresh)
            {
                if (restoreFocus)
                {
                    RestoreCardFocus(refreshHome: false);
                }
            }
            else
            {
                // Entry focus: the first card of the first row, never replayed by a quiet refresh.
                _focusFirstRow = true;
                Rows.LayoutUpdated += OnRowsLayoutUpdated;
            }
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 401)
        {
            // TvAppNavigation.kt sessionExpired keeps the server and explains why sign-in is needed.
            Session.Expire();
        }
        catch (Exception ex)
        {
            Log.Error(_libraryId is null ? "Home load" : $"Library {_libraryId} load", ex);
            // TvHomeScreen.kt keeps the existing feed usable when a quiet refresh fails.
            if (_rows.Count == 0)
            {
                ShowError($"Couldn't load this page. {ex.Message}", focus: !refresh || IsInPage(FocusManager.GetFocusedElement() as DependencyObject));
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// TvHomeScreen.kt refreshes on resume, after return focus lands. TvSkylineSectionFeed.kt resolves
    /// by section/card identity first, then the nearest surviving index, not the first card on Home.
    /// </summary>
    private bool UpdateRows(IReadOnlyList<MediaRow> rows, bool preserveFocus)
    {
        var focused = FocusManager.GetFocusedElement() as DependencyObject;
        var restoreFocus = preserveFocus && IsInPage(focused);
        var last = _lastCard;
        var rememberedRow = last is { } previous && previous.Row < _rows.Count ? _rows[previous.Row] : null;
        var rememberedIndex = last is { } remembered ? rememberedRow?.Cards.IndexOf(remembered.Card) ?? 0 : 0;
        _updatingRows = true;
        try
        {
            var index = 0;
            foreach (var row in rows.Where(row => row.Cards.Count > 0))
            {
                var existing = _rows.Skip(index).FirstOrDefault(view => view.Row.Id == row.Id);
                if (existing is null)
                {
                    _rows.Insert(index, new RowView(row));
                }
                else
                {
                    var oldIndex = _rows.IndexOf(existing);
                    if (oldIndex != index)
                    {
                        _rows.Move(oldIndex, index);
                    }

                    existing.Update(row);
                }

                index++;
            }

            while (_rows.Count > index)
            {
                _rows.RemoveAt(_rows.Count - 1);
            }

            if (_rows.Count == 0)
            {
                _lastCard = null;
            }
            else if (last is not null || restoreFocus)
            {
                var row = _rows.FirstOrDefault(view => ReferenceEquals(view, rememberedRow))
                    ?? _rows[Math.Clamp(last?.Row ?? 0, 0, _rows.Count - 1)];
                var target = row.Cards.FirstOrDefault(card => card.Key == last?.Card.Key)
                    ?? row.Cards[Math.Clamp(rememberedIndex, 0, row.Cards.Count - 1)];
                row.LastFocused = target;
                _lastCard = (_rows.IndexOf(row), target);
            }

            // Existing rails outlive a refresh too; don't let their entry targets retain deleted cards.
            for (var i = 0; i < _rows.Count; i++)
            {
                if (Rows.ContainerFromIndex(i) is DependencyObject container && FindChild<ListView>(container)?.Tag is Rail rail)
                {
                    rail.Remembered = _rows[i].LastFocused;
                }
            }
        }
        finally
        {
            _updatingRows = false;
        }

        return restoreFocus;
    }

    /// <summary>
    /// Entry focus: the first card of the first row (Skyline §6.1). Windows hands focus to the first
    /// focusable control (the top bar) before the rows exist, so keep trying on each layout pass until
    /// the card is in the tree and accepts focus.
    /// </summary>
    private void OnRowsLayoutUpdated(object? sender, object e)
    {
        if (!_focusFirstRow)
        {
            Rows.LayoutUpdated -= OnRowsLayoutUpdated;
            return;
        }

        if (!_isLoaded)
        {
            return;
        }

        // Focusing the row's ListView hands focus to its first card; its containers aren't reachable
        // through ContainerFromIndex here, so don't look for them.
        var row = Rows.ContainerFromIndex(0) is DependencyObject rowContainer ? FindChild<ListView>(rowContainer) : null;
        var focused = row?.Focus(FocusState.Programmatic) == true;
        if (focused)
        {
            _focusFirstRow = false;
            Rows.LayoutUpdated -= OnRowsLayoutUpdated;
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnCardFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: BrowseTileView view } item)
        {
            ApplyTileFocus(item, view, focused: true);
            if (_updatingRows || _returning)
            {
                return;
            }

            _focusVersion++;
            if (sender is ListView row)
            {
                PinRow(row);
                if (RowIndexOf(row) is { } index)
                {
                    _lastCard = (index, view);
                }
            }

            // TvSkylineSectionFeed.kt: the end card moves return focus, but keeps the last media preview.
            if (view is CardView media)
            {
                _pendingMarquee = media;
                _marqueeTimer.Stop();
                _marqueeTimer.Start();
            }
        }
    }

    /// <summary>
    /// Up/Down move between rows the way Silo's TV rails do (TvMediaRow's focusRestorer): into the
    /// neighbouring row's last-focused card, or its first card on a first visit, instead of whichever
    /// card happens to sit nearest on screen. A focused ListView restores its own last-focused item.
    /// Up from the first row falls through to the top bar.
    /// </summary>
    private void OnRowsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Focus/layout callbacks must not let an exception escape through the native input path.
        try
        {
            MoveBetweenRows(e);
        }
        catch (Exception ex)
        {
            Log.Error("Row navigation", ex);
        }
    }

    private void MoveBetweenRows(KeyRoutedEventArgs e)
    {
        var step = e.Key switch
        {
            VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown => 1,
            VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp => -1,
            _ => 0,
        };
        var header = SeeAllButtonOf(e.OriginalSource as DependencyObject);
        if (header is not null && e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadDPadRight
            or VirtualKey.GamepadLeftThumbstickLeft or VirtualKey.GamepadLeftThumbstickRight)
        {
            e.Handled = true;
            return;
        }

        if (step == 0 || RowIndexOf(e.OriginalSource as DependencyObject) is not { } index)
        {
            return;
        }

        // TvSectionHeader is outside the LazyRow's focus group: Up reaches its action, Down re-enters
        // the remembered rail. Don't let the platform turn either handoff into a sideways card move.
        if (header is not null && step > 0)
        {
            e.Handled = true;
            FocusRow(index);
            return;
        }

        if (header is null && step < 0 && FocusSeeAll(index))
        {
            e.Handled = true;
            return;
        }

        // TvSkylineSectionFeed.kt owns vertical handoffs. Consume even failed/edge moves so the
        // focus engine cannot turn Up/Down into a sideways move within this row (keyboard or pad).
        e.Handled = true;
        var targetIndex = index + step;
        if (targetIndex < 0)
        {
            ShellPage.Current?.FocusSelectedTab();
            return;
        }

        if (step > 0 && FocusSeeAll(targetIndex)) return;
        FocusRow(targetIndex);
    }

    private void FocusRow(int index)
    {
        if (index < 0 || index >= _rows.Count || Rows.ContainerFromIndex(index) is not DependencyObject container
            || FindChild<ListView>(container) is not { } target) return;
        if (target.Tag is Rail rail) rail.Focus(FocusState.Keyboard);
        else target.Focus(FocusState.Keyboard);
    }

    private bool FocusSeeAll(int index) => index >= 0 && index < _rows.Count && _rows[index].Row.OffersSeeAll
        && Rows.ContainerFromIndex(index) is DependencyObject container
        && FindChild<Button>(container)?.Focus(FocusState.Keyboard) == true;

    private static Button? SeeAllButtonOf(DependencyObject? element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Button { Name: "SeeAllButton" } button) return button;
        }

        return null;
    }

    private void OnSeeAllFocus(object sender, RoutedEventArgs e)
    {
        _focusVersion++;
        if (RowIndexOf(sender as DependencyObject) is { } index && Rows.ContainerFromIndex(index) is DependencyObject container
            && FindChild<ListView>(container) is { } row) PinRow(row);
    }

    private void OnSeeAllClick(object sender, RoutedEventArgs e) => ShellPage.Current?.OpenForYou();

    /// <summary>The index of the row holding <paramref name="element"/>.</summary>
    private int? RowIndexOf(DependencyObject? element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (VisualTreeHelper.GetParent(node) is Panel panel && panel == Rows.ItemsPanelRoot)
            {
                var index = Rows.IndexFromContainer(node);
                return index >= 0 ? index : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Focus entering the rows from outside (Down from the top bar, Back to content) returns to the
    /// user's last-focused card, as Silo's Android TV shell does with its content focusRestorer
    /// ("card 7 of row 3"), instead of whichever card happens to sit under the tab.
    /// </summary>
    private void OnRowsGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        try
        {
            if (_updatingRows || _lastCard is not { } last || IsInRows(args.OldFocusedElement) || !IsInRows(args.NewFocusedElement)
                || Rows.ContainerFromIndex(last.Row) is not DependencyObject rowContainer
                || FindChild<ListView>(rowContainer)?.ItemsPanelRoot is not { } panel)
            {
                return;
            }

            if (panel.Children.OfType<ContentControl>().FirstOrDefault(c => ReferenceEquals(c.Content, last.Card)) is { } card
                && !ReferenceEquals(card, args.NewFocusedElement))
            {
                args.TrySetNewFocusedElement(card);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Restoring focus", ex);
        }
    }

    private bool IsInRows(DependencyObject? element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node == Rows)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsInPage(DependencyObject? element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, this))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Each row is a <see cref="Rail"/>: Left/Right stop at its ends, the focused card pins to the
    /// start padding, and the row remembers its last card (kept on the RowView, which outlives its
    /// recycled ListView).
    /// </summary>
    private void OnRailLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListView { Tag: null, DataContext: RowView row } list)
        {
            return;
        }

        var rail = Rail.Attach(list);
        rail.Remembered = row.LastFocused;
        rail.ItemFocused += (_, item) =>
        {
            if (!_updatingRows && !_returning)
            {
                row.LastFocused = item as BrowseTileView;
            }
        };
        list.Tag = rail;
    }

    /// <summary>The focused row moves up to sit right under the marquee.</summary>
    private void PinRow(ListView row)
    {
        if (VisualTreeHelper.GetParent(row) is FrameworkElement rowPanel)
        {
            var top = rowPanel.TransformToVisual(Rows).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            RowsScroller.ChangeView(null, Math.Max(0, top), null);
        }
    }

    private void OnCardBlur(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: BrowseTileView view } item)
        {
            ApplyTileFocus(item, view, focused: false);
        }
    }

    private static void ApplyTileFocus(ContentControl item, BrowseTileView view, bool focused)
    {
        CardFocus.Apply(item, focused, view is ExploreAllView explore ? explore.FocusScale : CardFocus.ArtScale);
        if (item.ContentTemplateRoot is FrameworkElement root && root.FindName("ExploreAll") is Control surface)
        {
            VisualStateManager.GoToState(surface, focused ? "Focused" : "Resting", false);
        }
    }

    /// <summary>TvHomeScreen.kt preserves episode selection; DetailPage resolves the episode to its series.</summary>
    private void OnCardClick(object sender, ItemClickEventArgs e) => OpenCard(e.ClickedItem);

    private void OpenCard(object item)
    {
        if (item is ExploreAllView explore && Session.Client is { } client)
        {
            ShellPage.Current?.Frame.Navigate(typeof(CatalogGridPage), new CatalogGridEntry(explore.Source, client));
        }
        else if (item is CardView { Card: var card })
        {
            ShellPage.Current?.OpenDetail(card.ContentId);
        }
    }

    private void OnCardChanged(object item)
    {
        if (item is not CardView view) return;
        // Keep duplicate appearances and the row snapshots in sync without replacing card containers.
        foreach (var row in _rows.Where(row => row.Row.Cards.Any(card => card.ContentId == view.Card.ContentId)))
        {
            row.Update(row.Row with { Cards = row.Row.Cards.Select(card => card.ContentId == view.Card.ContentId ? view.Card : card).ToList() });
        }

        if (_shownMarquee?.Card.ContentId == view.Card.ContentId) ShowFacts(view.Card);
    }

    private void OnHomeCardDismissed(MediaCard card)
    {
        var restore = IsInPage(FocusManager.GetFocusedElement() as DependencyObject);
        UpdateRows(HomeCardDismissal.Remove(_rows.Select(row => row.Row).ToList(), card), preserveFocus: true);
        if (_rows.Count == 0)
        {
            ShowEmpty(restore);
            return;
        }

        ShowMarquee((_lastCard?.Card as CardView) ?? _shownMarquee ?? _rows[0].Cards.OfType<CardView>().First(), refresh: true);
        if (restore) RestoreCardFocus(refreshHome: false);
    }

    /// <summary>TvHomeScreen.kt defers its resume refresh until the shell has restored return focus.</summary>
    public void RestoreFocus()
    {
        // The shell has finished its initial focus pass; explicit restoration now owns the target.
        _returning = false;
        if (_lastCard is null)
        {
            if (_libraryId is null && !_forYou)
            {
                _ = LoadAsync(refresh: true);
            }

            return;
        }

        RestoreCardFocus(refreshHome: _libraryId is null && !_forYou);
    }

    /// <summary>TvTopMenuBar.kt hands Down to the remembered content, never a geometric neighbour.</summary>
    internal void FocusContent()
    {
        _returning = false;
        if (EmptyPanel.Visibility == Visibility.Visible)
        {
            if (_libraryId is null) FocusStateButton(RefreshButton, EmptyPanel);
            else ShellPage.Current?.FocusSelectedTab();
        }
        else if (ErrorPanel.Visibility == Visibility.Visible)
        {
            FocusStateButton(RetryButton, ErrorPanel);
        }
        else if (_rows.Count > 0)
        {
            _lastCard ??= (0, _rows[0].Cards[0]);
            RestoreCardFocus(refreshHome: false);
        }
    }

    private void RestoreCardFocus(bool refreshHome)
    {
        var version = _focusVersion;
        FocusRetry.Until(Rows, () =>
        {
            // Don't steal focus if the user has moved again while containers were being laid out.
            if (version != _focusVersion || _lastCard is not { } last)
            {
                return true;
            }

            if (!_isLoaded || Rows.ContainerFromIndex(last.Row) is not DependencyObject rowContainer
                || FindChild<ListView>(rowContainer) is not { } row || row.ItemsPanelRoot is not { } panel)
            {
                return false;
            }

            if (row.Tag is Rail rail)
            {
                rail.Remembered = last.Card;
            }

            if (panel.Children.OfType<ContentControl>().FirstOrDefault(c => ReferenceEquals(c.Content, last.Card)) is not { } card
                || !card.Focus(FocusState.Programmatic))
            {
                return false;
            }

            PinRow(row);
            if (last.Card is CardView media) ShowMarquee(media);
            if (refreshHome)
            {
                _ = LoadAsync(refresh: true);
            }

            return true;
        });
    }

    /// <summary>Focus has settled on <paramref name="view"/>: fetch the marquee art and details of the two cards either side (HeroFocusPrefetchRadius).</summary>
    private void PrefetchAround(CardView view)
    {
        if (Rows.ItemsSource is not IEnumerable<RowView> rows || rows.FirstOrDefault(row => row.Cards.Contains(view)) is not { } row)
        {
            return;
        }

        var index = row.Cards.ToList().IndexOf(view);
        var detail = Session.Client?.Detail;
        for (var i = Math.Max(0, index - 2); i <= Math.Min(row.Cards.Count - 1, index + 2); i++)
        {
            // Details too: the marquee's cast line, and the page a press opens (an episode opens its series).
            if (row.Cards[i] is not CardView neighbour) continue;
            var card = neighbour.Card;
            detail?.Preload(card.ContentId);
            if (card.Type == "episode" && card.SeriesId is { } series)
            {
                detail?.Preload(series);
            }

            if (i != index)
            {
                Artwork.Warm(card.BackdropUrl);
                Artwork.Warm(card.LogoUrl);
            }
        }
    }

    private void ShowMarquee(CardView? view, bool refresh = false)
    {
        if (view is null || (!refresh && ReferenceEquals(view, _shownMarquee)))
        {
            return;
        }

        _shownMarquee = view;
        PrefetchAround(view);
        var card = view.Card;
        MarqueeTitle.Text = card.Title;
        ShowFacts(card);
        MarqueeSynopsis.Text = card.Overview ?? string.Empty;
        MarqueeDetailLine.Text = string.Empty;
        _ = ShowDetailLineAsync(view);
        ShowBadges(card.SpecBadges);

        // A title with logo art waits briefly for it (decoded logos are kept in memory, and the
        // neighbours' are prefetched) before falling back to the text title, so moving along a row
        // doesn't flash text for a frame or two before every logo. A slow or broken logo still never
        // leaves the slot blank: the text appears after the grace period and the logo replaces it
        // whenever it lands.
        _logoGrace.Stop();
        MarqueeLogo.Opacity = 0;
        MarqueeLogo.Source = null;
        MarqueeTitle.Opacity = card.LogoUrl is null ? 1 : 0;
        TitleSlot.Height = card.LogoUrl is null ? double.NaN : LogoSlotHeight;
        if (card.LogoUrl is { } logo)
        {
            MarqueeLogo.Height = LogoSlotHeight;
            var image = Artwork.LoadKept(logo, (int)LogoSlotHeight, out var ready);
            MarqueeLogo.Source = image;
            if (ready)
            {
                MarqueeLogo.Opacity = 1;
            }
            else
            {
                image.ImageOpened += (_, _) =>
                {
                    if (ReferenceEquals(_shownMarquee, view) && view.Card.LogoUrl == logo)
                    {
                        _logoGrace.Stop();
                        MarqueeLogo.Opacity = 1;
                        MarqueeTitle.Opacity = 0;
                    }
                };
                image.ImageFailed += (_, _) =>
                {
                    if (ReferenceEquals(_shownMarquee, view) && view.Card.LogoUrl == logo)
                    {
                        MarqueeTitle.Opacity = 1;
                    }
                };
                _logoGrace.Start();
            }
        }

        if (card.BackdropUrl is { } backdrop)
        {
            // TvRootHeroBackdrop.kt crossfades the old art into its replacement, never via black.
            _art?.Show(backdrop);
            _ = TintFromAsync(view, backdrop);
        }
        else
        {
            _art?.Hide();
            _tint = null;
            _wash?.Show(null);
        }
    }

    /// <summary>
    /// The facts line (TvFactsRow on the marquee): the content-rating chip, then the facts, rating and
    /// genre with dots between. Built in code: resources can't be read from code under AOT.
    /// </summary>
    private void ShowFacts(MediaCard card)
    {
        MarqueeFacts.Children.Clear();
        if (card.ContentRating is { } rating)
        {
            MarqueeFacts.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0.75),
                CornerRadius = new CornerRadius(2.5),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = rating, FontSize = 9.5, FontWeight = Windows.UI.Text.FontWeights.SemiBold, CharacterSpacing = 30, Foreground = new SolidColorBrush(Colors.White) },
            });
        }

        var tokens = 0;
        void Add(UIElement token)
        {
            if (tokens++ > 0)
            {
                MarqueeFacts.Children.Add(FactText("·"));
            }

            MarqueeFacts.Children.Add(token);
        }

        foreach (var fact in card.MarqueeFacts)
        {
            Add(FactText(fact));
        }

        if (card.Rating is { } score)
        {
            Add(RatingMark.Build(score, 12));
        }

        if (card.Genre is { } genre)
        {
            Add(FactText(genre));
        }
    }

    private static TextBlock FactText(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = Windows.UI.Text.FontWeights.Medium,
        Foreground = new SolidColorBrush(Color.FromArgb(0xBF, 0xED, 0xED, 0xED)),
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary>The cast line arrives with the title's detail, which the card's neighbours already preloaded.</summary>
    private async Task ShowDetailLineAsync(CardView view)
    {
        if (Session.Client is not { } client)
        {
            return;
        }

        try
        {
            var detail = await client.Detail.GetAsync(view.Card.ContentId);
            if (ReferenceEquals(_shownMarquee, view))
            {
                MarqueeDetailLine.Text = detail.MarqueeLine ?? string.Empty;
            }
        }
        catch (Exception)
        {
            // The line stays empty, as when a title has no cast.
        }
    }

    /// <summary>Outlined badges built in code: resources can't be looked up from code under AOT, so colours are literal here.</summary>
    private void ShowBadges(IReadOnlyList<string> badges)
    {
        Badges.Children.Clear();
        foreach (var badge in badges)
        {
            Badges.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0.5),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2.5, 6, 2.5),
                Child = new TextBlock
                {
                    Text = badge,
                    FontSize = 9,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    CharacterSpacing = 60,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF)),
                },
            });
        }

        Badges.Visibility = badges.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task TintFromAsync(CardView view, Uri backdrop)
    {
        var tint = await AmbientTint.SampleAsync(backdrop);
        if (!ReferenceEquals(_shownMarquee, view) || view.Card.BackdropUrl != backdrop)
        {
            return;
        }

        _tint = tint;
        _wash?.Show(tint);
    }

    private void ClearMarquee()
    {
        _focusFirstRow = false;
        Rows.LayoutUpdated -= OnRowsLayoutUpdated;
        _marqueeTimer.Stop();
        _logoGrace.Stop();
        _shownMarquee = _pendingMarquee = null;
        _art?.Hide();
        _tint = null;
        _wash?.Show(null);
    }

    private void ShowEmpty(bool focus)
    {
        ClearMarquee();
        // TvHomeScreen.kt: an empty feed is not an error, and has its own Refresh action.
        EmptyTitle.Text = _forYou ? "No recommendations yet" : _libraryId is null ? "Nothing to watch yet" : $"{_libraryTitle} is empty.";
        EmptyMessage.Text = _forYou ? "Watch a few titles and check back."
            : _libraryId is null ? "Add media to your libraries or start watching to see it here." : string.Empty;
        EmptyMessage.Visibility = RefreshButton.Visibility = _libraryId is null ? Visibility.Visible : Visibility.Collapsed;
        VisualStateManager.GoToState(this, "Empty", false);
        if (focus && _libraryId is null)
        {
            FocusStateButton(RefreshButton, EmptyPanel);
        }
    }

    private void ShowError(string message, bool focus)
    {
        ClearMarquee();
        ErrorText.Text = message;
        VisualStateManager.GoToState(this, "Error", false);
        if (focus)
        {
            FocusStateButton(RetryButton, ErrorPanel);
        }
    }

    private void FocusStateButton(Control button, FrameworkElement panel)
    {
        var version = _focusVersion;
        FocusRetry.Until(this, () => version != _focusVersion || panel.Visibility != Visibility.Visible
            || (_isLoaded && button.Focus(FocusState.Programmatic)));
    }

    private void OnStateKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Up:
            case VirtualKey.GamepadDPadUp:
            case VirtualKey.GamepadLeftThumbstickUp:
                ShellPage.Current?.FocusSelectedTab();
                e.Handled = true;
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

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }
}
