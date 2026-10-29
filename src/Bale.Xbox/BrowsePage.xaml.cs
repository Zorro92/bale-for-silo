using System.Numerics;
using Bale.Core.Browse;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Microsoft.Kiota.Abstractions;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>
/// A Skyline root screen: Home, or a library tab's Browse landing. A passive marquee previews the
/// focused card over the server's rows (guide §5.4, §5.5, §6.1, §6.2).
/// </summary>
public sealed partial class BrowsePage : Page
{
    public const string HomeParameter = "home";

    /// <summary>Marquee updates wait for focus to rest this long, so scrubbing a row doesn't thrash backdrops (guide §4.2).</summary>
    private static readonly TimeSpan MarqueeSettle = TimeSpan.FromMilliseconds(150);

    private const float FocusScale = 1.05f;

    private readonly DispatcherTimer _marqueeTimer = new() { Interval = MarqueeSettle };
    private CardView? _pendingMarquee;
    private CardView? _shownMarquee;
    private string? _libraryId;
    private bool _focusFirstRow;

    public BrowsePage()
    {
        InitializeComponent();
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
        var parameter = e.Parameter as string ?? HomeParameter;
        if (parameter.StartsWith("library:", StringComparison.Ordinal))
        {
            var parts = parameter.Split(new[] { ':' }, 3);
            _libraryId = parts[1];
            ScopeCaption.Text = parts.Length > 2 ? parts[2] : string.Empty;

            // Library landing: compact marquee below the pill row (guide §5.5).
            PillRow.Visibility = Visibility.Visible;
            Marquee.Margin = new Thickness(44, 112, 0, 0);
            MarqueeTitle.FontSize = (double)Application.Current.Resources["SkylineMarqueeTitleCompactSize"];
            MarqueeTitle.LineHeight = 33;
            MarqueeLogo.MaxHeight = 80;
            RowsScroller.Margin = new Thickness(0, 255, 0, 0);
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

    /// <summary>Rows load top to bottom, so the first to finish is the top row: start focus on its first card.</summary>
    private void OnRowLoaded(object sender, RoutedEventArgs e)
    {
        if (_focusFirstRow && sender is ListView row)
        {
            _focusFirstRow = false;
            // A focused ListView hands focus to its first item if the container isn't realized yet.
            ((Control?)(row.ContainerFromIndex(0) as ListViewItem) ?? row).Focus(FocusState.Programmatic);
        }
    }

    private void OnCardFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ListViewItem { Content: CardView view } item)
        {
            Lift(item, FocusScale);
            _pendingMarquee = view;
            _marqueeTimer.Stop();
            _marqueeTimer.Start();
        }
    }

    private void OnCardBlur(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ListViewItem item)
        {
            Lift(item, 1f);
        }
    }

    /// <summary>Focus scale 1.05 around the card's centre over 120 ms (guide §4.2, §5.6).</summary>
    private static void Lift(ListViewItem item, float scale)
    {
        item.ScaleTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(120) };
        item.CenterPoint = new Vector3((float)item.ActualWidth / 2, (float)item.ActualHeight / 2, 0);
        item.Scale = new Vector3(scale, scale, 1);
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
        Eyebrow.Text = view.RowTitle.ToUpperInvariant();
        MarqueeTitle.Text = card.Title;
        MarqueeMeta.Text = card.MarqueeMeta;
        MarqueeSynopsis.Text = card.Overview ?? string.Empty;

        // Logo art when the server has it; never wait on it — the text title shows until it loads.
        MarqueeLogo.Source = null;
        MarqueeLogo.Visibility = Visibility.Collapsed;
        MarqueeTitle.Visibility = Visibility.Visible;
        if (card.LogoUrl is { } logo)
        {
            var image = new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = 440, UriSource = logo };
            image.ImageOpened += (_, _) =>
            {
                if (ReferenceEquals(_shownMarquee, view))
                {
                    MarqueeLogo.Visibility = Visibility.Visible;
                    MarqueeTitle.Visibility = Visibility.Collapsed;
                }
            };
            MarqueeLogo.Source = image;
        }

        if (card.BackdropUrl is { } backdrop)
        {
            var image = new BitmapImage { UriSource = backdrop };
            image.ImageOpened += (_, _) =>
            {
                if (ReferenceEquals(_shownMarquee, view))
                {
                    Backdrop.Opacity = 1;
                }
            };
            Backdrop.Opacity = 0;
            Backdrop.Source = image;
        }
        else
        {
            Backdrop.Opacity = 0;
        }
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
