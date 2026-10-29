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

    /// <summary>Marquee updates wait for focus to rest this long, so scrubbing a row doesn't thrash backdrops (guide §4.2).</summary>
    private static readonly TimeSpan MarqueeSettle = TimeSpan.FromMilliseconds(150);

    /// <summary>The logo slot's fixed height, so the block doesn't jump as art loads (TvFocusMarquee).</summary>
    private const double LogoSlotHeight = 84;

    private readonly DispatcherTimer _marqueeTimer = new() { Interval = MarqueeSettle };
    private CardView? _pendingMarquee;
    private CardView? _shownMarquee;
    private string? _libraryId;
    private bool _focusFirstRow;
    private MaskedArt? _art;

    /// <summary>The last card focused anywhere on the page: where Down from the top bar returns.</summary>
    private (int Row, CardView Card)? _lastCard;

    public BrowsePage()
    {
        InitializeComponent();

        // handledEventsToo: a horizontal ListView may claim Up/Down itself before it would bubble.
        RowsScroller.AddHandler(KeyDownEvent, new KeyEventHandler(OnRowsKeyDown), handledEventsToo: true);
        RowsScroller.GettingFocus += OnRowsGettingFocus;
        Loaded += (_, _) =>
        {
            try
            {
                _art = new MaskedArt(ArtBox);
            }
            catch (Exception ex)
            {
                // Without the art the marquee still works on the tint alone.
                Log.Error("Marquee art", ex);
                return;
            }

            if (_shownMarquee?.Card.BackdropUrl is { } pending)
            {
                _art.Show(pending);
            }
        };
        _marqueeTimer.Tick += (_, _) =>
        {
            _marqueeTimer.Stop();
            ShowMarquee(_pendingMarquee);
        };
    }

    public static string LibraryParameter(string libraryId, string libraryName) => $"library:{libraryId}:{libraryName}";

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Log.Info("Browse: navigated to " + (e.Parameter as string ?? ""));
        var parameter = e.Parameter as string ?? HomeParameter;
        if (parameter.StartsWith("library:", StringComparison.Ordinal))
        {
            var parts = parameter.Split(new[] { ':' }, 3);
            _libraryId = parts[1];
            ScopeCaption.Text = parts.Length > 2 ? parts[2] : string.Empty;

            VisualStateManager.GoToState(this, "Library", false);
        }

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (Session.Client is not { } client)
        {
            return;
        }

        LoadingRing.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        try
        {
            var rows = _libraryId is null
                ? await client.Browse.LoadHomeAsync(ArtworkWidths.Tv1080)
                : await client.Browse.LoadLibraryAsync(_libraryId, ArtworkWidths.Tv1080);
            var views = rows.Select(row => new RowView(row)).ToList();
            Rows.ItemsSource = views;
            if (views.Count == 0)
            {
                ShowError(_libraryId is null ? "Nothing on Home yet. Add a library on your Silo server and check back." : "This library is empty.");
                return;
            }

            // Entry focus: the first card of the first row, previewed at once (guide §6.1).
            ShowMarquee(views[0].Cards[0]);
            _focusFirstRow = true;
            Rows.LayoutUpdated += OnRowsLayoutUpdated;
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 401)
        {
            if (ShellPage.Current is { } shell)
            {
                await shell.SignOutAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(_libraryId is null ? "Home load" : $"Library {_libraryId} load", ex);
            ShowError($"Couldn't load this page. {ex.Message}");
        }
        finally
        {
            LoadingRing.Visibility = Visibility.Collapsed;
        }
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
        if (e.OriginalSource is ContentControl { Content: CardView view } item)
        {
            CardFocus.Apply(item, focused: true);
            if (sender is ListView row)
            {
                PinRow(row);
                if (RowIndexOf(row) is { } index)
                {
                    _lastCard = (index, view);
                }
            }

            _pendingMarquee = view;
            _marqueeTimer.Stop();
            _marqueeTimer.Start();
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
        // An exception thrown from a handler attached with AddHandler ends the app without reaching
        // UnhandledException, so nothing here may throw.
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
        if (step == 0 || RowIndexOf(e.OriginalSource as DependencyObject) is not { } index)
        {
            return;
        }

        // Up from the first row (and Down from the last) belongs to the focus engine: the top bar.
        var targetIndex = index + step;
        if (targetIndex < 0 || targetIndex >= Rows.Items.Count
            || Rows.ContainerFromIndex(targetIndex) is not DependencyObject next || FindChild<ListView>(next) is not { } target)
        {
            return;
        }

        // The row's rail lands on its remembered card, or the first card on a first visit.
        e.Handled = target.Tag is Rail rail ? rail.Focus(FocusState.Keyboard) : target.Focus(FocusState.Keyboard);
    }

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
            if (_lastCard is not { } last || IsInRows(args.OldFocusedElement) || !IsInRows(args.NewFocusedElement)
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
        rail.ItemFocused += (_, item) => row.LastFocused = item as CardView;
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
        if (e.OriginalSource is ContentControl { Content: CardView } item)
        {
            CardFocus.Apply(item, focused: false);
        }
    }

    /// <summary>A card opens its detail page; an episode opens its series' page (TvHomeScreen: "the combined Series page").</summary>
    private void OnCardClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CardView { Card: var card })
        {
            ShellPage.Current?.OpenDetail(card.Type == "episode" && card.SeriesId is { } series ? series : card.ContentId);
        }
    }

    /// <summary>Back from a detail page: focus returns to the card that opened it.</summary>
    public void RestoreFocus()
    {
        if (_lastCard is not { } last)
        {
            return;
        }

        FocusRetry.Until(Rows, () =>
            Rows.ContainerFromIndex(last.Row) is DependencyObject rowContainer
            && FindChild<ListView>(rowContainer)?.ItemsPanelRoot is { } panel
            && panel.Children.OfType<ContentControl>().FirstOrDefault(c => ReferenceEquals(c.Content, last.Card)) is { } card
            && card.Focus(FocusState.Programmatic));
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
            var card = row.Cards[i].Card;
            detail?.Preload(card.ContentId);
            if (card.Type == "episode" && card.SeriesId is { } series)
            {
                detail?.Preload(series);
            }

            if (i != index)
            {
                Artwork.Warm(row.Cards[i].Card.BackdropUrl);
                Artwork.Warm(row.Cards[i].Card.LogoUrl);
            }
        }
    }

    private void ShowMarquee(CardView? view)
    {
        if (view is null || ReferenceEquals(view, _shownMarquee))
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

        // Logo art fades over the text title once it has decoded; a slow or broken logo never blanks the slot.
        MarqueeLogo.Opacity = 0;
        MarqueeTitle.Opacity = 1;
        MarqueeLogo.Source = null;
        TitleSlot.Height = card.LogoUrl is null ? double.NaN : LogoSlotHeight;
        if (card.LogoUrl is { } logo)
        {
            MarqueeLogo.Height = LogoSlotHeight;
            var image = Artwork.Load(logo, height: (int)LogoSlotHeight);
            image.ImageOpened += (_, _) =>
            {
                if (ReferenceEquals(_shownMarquee, view))
                {
                    MarqueeLogo.Opacity = 1;
                    MarqueeTitle.Opacity = 0;
                }
            };
            MarqueeLogo.Source = image;
        }

        _art?.Hide();
        if (card.BackdropUrl is { } backdrop)
        {
            _art?.Show(backdrop);
            _ = TintFromAsync(view, backdrop);
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
        if (await AmbientTint.SampleAsync(backdrop) is not { } tint || !ReferenceEquals(_shownMarquee, view))
        {
            return;
        }

        Wash.Fill = AmbientTint.Wash(tint);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        RetryButton.Focus(FocusState.Programmatic);
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        _shownMarquee = null;
        await LoadAsync();
    }
}
