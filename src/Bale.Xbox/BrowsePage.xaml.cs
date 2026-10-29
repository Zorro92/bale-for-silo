using Bale.Core.Browse;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Microsoft.Kiota.Abstractions;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
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

    public BrowsePage()
    {
        InitializeComponent();
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

        if (Rows.ContainerFromIndex(0) is DependencyObject rowContainer
            && FindChild<ListView>(rowContainer) is { } row
            && row.ContainerFromIndex(0) is ListViewItem first
            && first.Focus(FocusState.Programmatic))
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
        if (e.OriginalSource is ListViewItem { Content: CardView view } item)
        {
            CardFocus.Apply(item, focused: true);
            _pendingMarquee = view;
            _marqueeTimer.Stop();
            _marqueeTimer.Start();
        }
    }

    private void OnCardBlur(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ListViewItem item)
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
