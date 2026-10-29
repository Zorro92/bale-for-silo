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
public sealed partial class BrowsePage : Page
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

    /// <summary>The last card focused in each row, by row index (TvMediaRow's focusRestorer).</summary>
    private readonly Dictionary<int, CardView> _lastFocused = new();

    /// <summary>The last card focused anywhere on the page: where Down from the top bar returns.</summary>
    private (int Row, CardView Card)? _lastCard;

    public BrowsePage()
    {
        InitializeComponent();

        // handledEventsToo: a horizontal ListView may claim Up/Down itself before it would bubble.
        RowsScroller.AddHandler(KeyDownEvent, new KeyEventHandler(OnRowsKeyDown), handledEventsToo: true);
        RowsScroller.PreviewKeyDown += OnRowsPreviewKeyDown;
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
                PinCard(row, item);
                if (RowIndexOf(row) is { } index)
                {
                    _lastFocused[index] = view;
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

        // Find the remembered card among the realized containers (ContainerFromIndex returns nothing
        // for these rails under AOT); a first visit lands on the first card.
        Control? card = null;
        if (_lastFocused.TryGetValue(targetIndex, out var remembered) && target.ItemsPanelRoot is { } panel)
        {
            card = panel.Children.OfType<ContentControl>().FirstOrDefault(container => ReferenceEquals(container.Content, remembered));
        }

        e.Handled = (card ?? target).Focus(FocusState.Keyboard);
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
    private void OnRowsGettingFocus(UIElement sender, GettingFocusRoutedEventArgs args)
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
    /// Left/Right never leave the rail. At either end there is nothing to move to, and the ListView
    /// itself would hand focus to the nearest card in some other row, so this runs on the tunnelling
    /// pass, before the ListView sees the key.
    /// </summary>
    private void OnRowsPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: CardView edge }
            && ((edge.IsLast && e.Key is VirtualKey.Right or VirtualKey.GamepadDPadRight or VirtualKey.GamepadLeftThumbstickRight)
                || (edge.IsFirst && e.Key is VirtualKey.Left or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadLeftThumbstickLeft)))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// A rail's own bring-into-view request stops at the rail: left to bubble, it also nudged the page
    /// vertically on every Left/Right, which PinRow then undid, so the page jumped.
    /// </summary>
    private void OnRailBringIntoView(UIElement sender, BringIntoViewRequestedEventArgs args) => args.Handled = true;

    /// <summary>The focused row moves up to sit right under the marquee.</summary>
    private void PinRow(ListView row)
    {
        if (VisualTreeHelper.GetParent(row) is FrameworkElement rowPanel)
        {
            var top = rowPanel.TransformToVisual(Rows).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            RowsScroller.ChangeView(null, Math.Max(0, top), null);
        }
    }

    /// <summary>
    /// The focused card slides to the row's start padding (the tvOS / Android "pinned" rail), so every
    /// Left/Right is one card-sized glide and focus always sits in the same place. Row ends clamp.
    /// </summary>
    private static void PinCard(ListView row, ContentControl card)
    {
        if (FindChild<ScrollViewer>(row) is not { } scroller)
        {
            return;
        }

        var x = card.TransformToVisual(scroller).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
        scroller.ChangeView(scroller.HorizontalOffset + x - row.Padding.Left, null, null);
    }

    private void OnCardBlur(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: CardView } item)
        {
            CardFocus.Apply(item, focused: false);
        }
    }

    private async void OnCardClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CardView { Card: var card })
        {
            await CardInfo.ShowAsync(card);
        }
    }

    private void ShowMarquee(CardView? view)
    {
        if (view is null || ReferenceEquals(view, _shownMarquee))
        {
            return;
        }

        _shownMarquee = view;
        var card = view.Card;
        MarqueeTitle.Text = card.Title;
        MarqueeMeta.Text = card.MarqueeMeta;
        MarqueeSynopsis.Text = card.Overview ?? string.Empty;
        ShowBadges(card.Badges);

        // Logo art fades over the text title once it has decoded; a slow or broken logo never blanks the slot.
        MarqueeLogo.Opacity = 0;
        MarqueeTitle.Opacity = 1;
        MarqueeLogo.Source = null;
        TitleSlot.Height = card.LogoUrl is null ? double.NaN : LogoSlotHeight;
        if (card.LogoUrl is { } logo)
        {
            MarqueeLogo.Height = LogoSlotHeight;
            var image = new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelHeight = (int)LogoSlotHeight, UriSource = logo };
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
