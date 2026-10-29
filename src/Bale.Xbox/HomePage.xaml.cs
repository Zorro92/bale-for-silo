using Bale.Core.Home;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Microsoft.Kiota.Abstractions;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>Home: a banner for the focused card over the server's rows.</summary>
public sealed partial class HomePage : Page
{
    /// <summary>How long focus rests on a card before the banner follows, so holding a direction doesn't load every backdrop.</summary>
    private static readonly TimeSpan BannerSettle = TimeSpan.FromMilliseconds(150);

    private readonly DispatcherTimer _bannerTimer = new() { Interval = BannerSettle };
    private MediaCard? _pendingBanner;
    private MediaCard? _shownBanner;
    private bool _loaded;
    private bool _focusFirstRow;

    public HomePage()
    {
        InitializeComponent();
        _bannerTimer.Tick += (_, _) =>
        {
            _bannerTimer.Stop();
            ShowBanner(_pendingBanner);
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ProfileName.Text = Session.ProfileName ?? Session.Client?.Account?.Username ?? string.Empty;
        if (!_loaded)
        {
            await LoadAsync();
        }
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
            var rows = await client.Home.LoadAsync(ArtworkWidths.Tv1080);
            Rows.ItemsSource = rows.Select(row => new RowView(row)).ToList();
            _loaded = true;
            if (rows.Count == 0)
            {
                ShowError("Nothing on Home yet. Add a library on your Silo server and check back.", retry: true);
                return;
            }

            ShowBanner(rows[0].Cards[0]);
            _focusFirstRow = true;
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 401)
        {
            await Session.SignOutAsync();
            Frame.Navigate(typeof(MainPage));
            Frame.BackStack.Clear();
        }
        catch (Exception ex)
        {
            Log.Error("Home load", ex);
            ShowError($"Couldn't load Home. {ex.Message}", retry: true);
        }
        finally
        {
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Rows load top to bottom, so the first one to finish is the top row: start focus on its first card.</summary>
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
        if (e.OriginalSource is ListViewItem { Content: CardView view })
        {
            _pendingBanner = view.Card;
            _bannerTimer.Stop();
            _bannerTimer.Start();
        }
    }

    private async void OnCardClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CardView { Card: var card })
        {
            return;
        }

        // Placeholder until the details page lands in the next milestone.
        var dialog = new ContentDialog
        {
            Title = card.Title,
            Content = new TextBlock
            {
                Text = string.Join("\n\n", new[] { card.Subtitle, card.Meta, card.Overview, "Details and playback are coming in the next milestone." }
                    .Where(line => !string.IsNullOrEmpty(line))),
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = "Close",
        };
        await dialog.ShowAsync();
    }

    private void ShowBanner(MediaCard? card)
    {
        if (card is null || ReferenceEquals(card, _shownBanner))
        {
            return;
        }

        _shownBanner = card;
        BannerTitle.Text = card.Title;
        BannerMeta.Text = card.Subtitle is not null && card.Type == "episode" ? card.Subtitle : card.Meta ?? string.Empty;
        BannerOverview.Text = card.Overview ?? string.Empty;

        if (card.LogoUrl is { } logo)
        {
            BannerLogo.Source = new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = 360, UriSource = logo };
            BannerLogo.Visibility = Visibility.Visible;
            BannerTitle.Visibility = Visibility.Collapsed;
        }
        else
        {
            BannerLogo.Source = null;
            BannerLogo.Visibility = Visibility.Collapsed;
            BannerTitle.Visibility = Visibility.Visible;
        }

        if (card.BackdropUrl is { } backdrop)
        {
            var image = new BitmapImage { UriSource = backdrop };
            image.ImageOpened += (_, _) => Backdrop.Opacity = 1;
            Backdrop.Opacity = 0;
            Backdrop.Source = image;
        }
        else
        {
            Backdrop.Opacity = 0;
        }
    }

    private void ShowError(string message, bool retry)
    {
        ErrorText.Text = message;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        RetryButton.Focus(FocusState.Programmatic);
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _shownBanner = null;
        await LoadAsync();
    }

    private void OnSwitchProfileClick(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(ProfilesPage), ProfilesPage.FromHome);

    private async void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        await Session.SignOutAsync();
        Frame.Navigate(typeof(MainPage));
        Frame.BackStack.Clear();
    }
}
