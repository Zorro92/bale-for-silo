using Bale.Core.Detail;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Microsoft.Kiota.Abstractions;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>A title's detail page (TvItemDetailScreen). Opened with the content id as the navigation parameter.</summary>
public sealed partial class DetailPage : Page
{
    private const double LogoHeight = 80;

    /// <summary>TvDetailHero's 0.42 sampled-tint strength over black.</summary>
    private const double TintStrength = 0.42;

    private string _contentId = string.Empty;
    private ItemDetail? _detail;
    private EpisodeCard? _nextUp;
    private MaskedArt? _art;
    private List<SeasonView> _seasons = new();
    private long? _shownSeason;
    private bool _busy;

    public DetailPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            try
            {
                _art = new MaskedArt(ArtBox, "ms-appx:///Assets/ArtMaskDetail.png");
                if (_detail?.BackdropUrl is { } pending)
                {
                    _art.Show(pending);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Detail art", ex);
            }
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _contentId = e.Parameter as string ?? string.Empty;
        Log.Info("Detail: navigated to " + _contentId);
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
            var detail = _detail = await client.Detail.LoadAsync(_contentId);
            ShowHero(detail);
            PlayButton.Focus(FocusState.Programmatic);

            if (detail.IsSeries)
            {
                await LoadSeasonsAsync(client.Detail, detail);
            }

            ShowCast(detail);
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            ShowError("This title isn't available anymore.");
        }
        catch (Exception ex)
        {
            Log.Error($"Detail {_contentId} load", ex);
            ShowError($"Couldn't load this title. {ex.Message}");
        }
        finally
        {
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowHero(ItemDetail detail)
    {
        TitleText.Text = detail.Title.ToUpperInvariant();
        TitleText.Opacity = 1;
        TitleLogo.Opacity = 0;
        if (detail.LogoUrl is { } logo)
        {
            // The logo replaces the text title once decoded; a broken logo never blanks the title.
            TitleSlot.Height = LogoHeight;
            var image = new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelHeight = (int)LogoHeight, UriSource = logo };
            image.ImageOpened += (_, _) =>
            {
                TitleLogo.Opacity = 1;
                TitleText.Opacity = 0;
            };
            TitleLogo.Source = image;
        }

        SubtitleText.Text = detail.Subtitle ?? string.Empty;
        SubtitleText.Visibility = string.IsNullOrEmpty(detail.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        ShowMetadata(detail);
        Synopsis.Text = detail.Overview ?? string.Empty;
        CreditLine.Text = detail.CreditLine ?? string.Empty;
        CreditLine.Visibility = detail.CreditLine is null ? Visibility.Collapsed : Visibility.Visible;

        PlayLabel.Text = Core.Detail.PlayLabel.For(detail, _nextUp);
        StartOverButton.Visibility = detail.ResumeSeconds is > 0 ? Visibility.Visible : Visibility.Collapsed;
        SeriesItem.Visibility = detail.Type == "episode" && detail.SeriesId is not null ? Visibility.Visible : Visibility.Collapsed;
        ShowFlags(detail.Flags);

        if (detail.BackdropUrl is { } backdrop)
        {
            _art?.Show(backdrop);
            _ = TintFromAsync(backdrop);
        }
    }

    /// <summary>
    /// TvDetailHero.MetadataRow: the outlined rating chip leads, then the facts and quality chips, then
    /// type and genres, separated by dots. Built in code: resources can't be read from code under AOT.
    /// </summary>
    private void ShowMetadata(ItemDetail detail)
    {
        Metadata.Children.Clear();
        if (detail.ContentRating is { } rating)
        {
            Metadata.Children.Add(Chip(rating, Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF), FontWeightsBlack()));
        }

        // Facts, then their quality chips, then type and genres; dots between text tokens.
        var tokens = 0;
        void AddToken(string token)
        {
            if (tokens++ > 0)
            {
                Metadata.Children.Add(Text("·", Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF)));
            }

            Metadata.Children.Add(Text(token, Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)));
        }

        foreach (var fact in detail.Facts)
        {
            AddToken(fact);
        }

        foreach (var badge in detail.QualityBadges)
        {
            Metadata.Children.Add(Chip(badge, Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF), FontWeightsBlack()));
        }

        foreach (var source in detail.SourceTokens)
        {
            AddToken(source);
        }
    }

    private static Windows.UI.Text.FontWeight FontWeightsBlack() => Windows.UI.Text.FontWeights.Black;

    private static TextBlock Text(string text, Color color) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = Windows.UI.Text.FontWeights.Medium,
        Foreground = new SolidColorBrush(color),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>TvDetailHero.RatingChip: 0.75 px white @ 70% outline, 2.5 px corners.</summary>
    private static Border Chip(string text, Color border, Windows.UI.Text.FontWeight weight) => new()
    {
        BorderBrush = new SolidColorBrush(border),
        BorderThickness = new Thickness(0.75),
        CornerRadius = new CornerRadius(2.5),
        Padding = new Thickness(6, 1.5, 6, 1.5),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 9, FontWeight = weight, CharacterSpacing = 30, Foreground = new SolidColorBrush(Colors.White) },
    };

    private void ShowFlags(ItemFlags flags)
    {
        WatchlistButton.Content = flags.InWatchlist ? "" : "";
        AutomationProperties.SetName(WatchlistButton, flags.InWatchlist ? "Remove from Watchlist" : "Add to Watchlist");
        FavoriteItem.Text = flags.IsFavorite ? "Remove from Favorites" : "Add to Favorites";
        WatchedItem.Text = flags.Played ? "Mark as Unwatched" : "Mark as Watched";
    }

    private async Task TintFromAsync(Uri backdrop)
    {
        if (await AmbientTint.SampleAsync(backdrop) is not { } tint)
        {
            return;
        }

        // The sampled tint composited over black at 0.42, fading out down the page.
        var top = Color.FromArgb(255, (byte)(tint.R * TintStrength), (byte)(tint.G * TintStrength), (byte)(tint.B * TintStrength));
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0.5, 0), EndPoint = new Windows.Foundation.Point(0.5, 1) };
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = top });
        brush.GradientStops.Add(new GradientStop { Offset = 0.7, Color = Color.FromArgb(255, (byte)(top.R / 3), (byte)(top.G / 3), (byte)(top.B / 3)) });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = Colors.Black });
        Wash.Fill = brush;
    }

    private async Task LoadSeasonsAsync(DetailFeed feed, ItemDetail detail)
    {
        var seasons = await feed.SeasonsAsync(detail.ContentId);
        if (seasons.Count == 0)
        {
            return;
        }

        _seasons = seasons.Select(season => new SeasonView(season)).ToList();
        Seasons.ItemsSource = _seasons;
        Seasons.Visibility = _seasons.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        EpisodesSection.Visibility = Visibility.Visible;

        // Open on the next-up episode's season, as the official apps do.
        var start = seasons.FirstOrDefault(season => season.Number == detail.PlaySeasonNumber) ?? seasons[0];
        await ShowSeasonAsync(feed, detail, start);
    }

    private async Task ShowSeasonAsync(DetailFeed feed, ItemDetail detail, SeasonChoice season)
    {
        _shownSeason = season.Number;
        SeasonEyebrow.Text = season.Title.ToUpperInvariant();
        EpisodeCount.Text = $"{season.EpisodeCount} episode{(season.EpisodeCount == 1 ? string.Empty : "s")}";
        UpdateSeasonChips();

        var episodes = await feed.EpisodesAsync(detail.ContentId, season.Number);
        if (_shownSeason != season.Number)
        {
            return;
        }

        Episodes.ItemsSource = episodes.Select(episode => new EpisodeView(episode)).ToList();
        if (episodes.FirstOrDefault(episode => episode.ContentId == detail.PlayTargetId) is { } nextUp)
        {
            _nextUp = nextUp;
            PlayLabel.Text = Core.Detail.PlayLabel.For(detail, nextUp);
        }
    }

    private void UpdateSeasonChips()
    {
        Seasons.UpdateLayout();
        foreach (var view in _seasons)
        {
            if (Seasons.ContainerFromItem(view) is DependencyObject container && FindChild<ToggleButton>(container) is { } chip)
            {
                chip.IsChecked = view.Season.Number == _shownSeason;
            }
        }
    }

    private void ShowCast(ItemDetail detail)
    {
        if (detail.Cast.Count == 0)
        {
            return;
        }

        Cast.ItemsSource = detail.Cast.Take(30).Select(member => new CastView(member)).ToList();
        CastSection.Visibility = Visibility.Visible;
    }

    private async void OnSeasonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SeasonView view } && _detail is { } detail && Session.Client is { } client)
        {
            try
            {
                await ShowSeasonAsync(client.Detail, detail, view.Season);
            }
            catch (Exception ex)
            {
                Log.Error("Season load", ex);
            }
        }
    }

    private void OnCardFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: EpisodeView or CastView } item)
        {
            CardFocus.Apply(item, focused: true);
        }
    }

    private void OnCardBlur(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: EpisodeView or CastView } item)
        {
            CardFocus.Apply(item, focused: false);
        }
    }

    private async void OnPlayClick(object sender, RoutedEventArgs e) => await PlaybackComingAsync();

    private async void OnStartOverClick(object sender, RoutedEventArgs e) => await PlaybackComingAsync();

    private async void OnEpisodeClick(object sender, ItemClickEventArgs e) => await PlaybackComingAsync();

    private static async Task PlaybackComingAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Playback is next",
            Content = new TextBlock { Text = "Playing titles arrives in the next milestone.", TextWrapping = TextWrapping.Wrap, FontSize = 12 },
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    private async void OnWatchlistClick(object sender, RoutedEventArgs e) =>
        await ToggleAsync(flags => flags with { InWatchlist = !flags.InWatchlist }, (feed, id, flags) => feed.SetWatchlistAsync(id, flags.InWatchlist));

    private async void OnFavoriteClick(object sender, RoutedEventArgs e) =>
        await ToggleAsync(flags => flags with { IsFavorite = !flags.IsFavorite }, (feed, id, flags) => feed.SetFavoriteAsync(id, flags.IsFavorite));

    private async void OnWatchedClick(object sender, RoutedEventArgs e) =>
        await ToggleAsync(flags => flags with { Played = !flags.Played }, (feed, id, flags) => feed.SetPlayedAsync(id, flags.Played));

    /// <summary>Flips a flag at once and tells the server; puts it back if the server refuses.</summary>
    private async Task ToggleAsync(Func<ItemFlags, ItemFlags> change, Func<DetailFeed, string, ItemFlags, Task> send)
    {
        if (_busy || _detail is not { } detail || Session.Client is not { } client)
        {
            return;
        }

        _busy = true;
        var before = detail.Flags;
        var after = change(before);
        _detail = detail with { Flags = after };
        ShowFlags(after);
        ActionStatus.Text = string.Empty;
        try
        {
            await send(client.Detail, detail.ContentId, after);
        }
        catch (Exception ex)
        {
            Log.Error("Detail toggle", ex);
            _detail = detail with { Flags = before };
            ShowFlags(before);
            ActionStatus.Text = "Couldn't update that. Try again.";
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnSeriesClick(object sender, RoutedEventArgs e)
    {
        if (_detail?.SeriesId is { } seriesId)
        {
            Frame.Navigate(typeof(DetailPage), seriesId);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        RetryButton.Focus(FocusState.Programmatic);
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e) => await LoadAsync();

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T self)
        {
            return self;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (FindChild<T>(VisualTreeHelper.GetChild(parent, i)) is { } match)
            {
                return match;
            }
        }

        return null;
    }
}
