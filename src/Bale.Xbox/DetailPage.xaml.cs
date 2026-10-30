using Bale.Core.Detail;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Microsoft.Kiota.Abstractions;
using Windows.System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
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

    /// <summary>Where a body section's top lands when focus enters it (DetailSectionAnchorFraction).</summary>
    private const double SectionAnchor = 0.18;

    private readonly Rail _seasonsRail;
    private readonly Rail _episodesRail;
    private readonly Rail _castRail;
    private string _contentId = string.Empty;
    private ItemDetail? _detail;
    private EpisodeCard? _nextUp;
    private MaskedArt? _art;
    private List<SeasonView> _tabs = new();
    private SeasonView? _selectedTab;
    private long? _shownSeason;
    private bool _busy;

    /// <summary>Set while the rail swaps seasons: focus briefly falls elsewhere and must not move the page.</summary>
    private bool _swapping;

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

        // Show / Season tabs reveal the focused tab without pinning; entering lands on the selected one.
        _seasonsRail = Rail.Attach(Seasons);
        _seasonsRail.Pin = false;
        _seasonsRail.Center = true;
        _seasonsRail.EntryItem = () => _selectedTab;
        _seasonsRail.Entered += () => ScrollTo(0);

        _episodesRail = Rail.Attach(Episodes);
        _episodesRail.Entered += () => ScrollTo(0);
        _episodesRail.ItemFocused += (_, item) => OnEpisodeFocused(item as EpisodeView);
        _episodesRail.EdgeReached += step => _ = StepSeasonAsync(step);

        _castRail = Rail.Attach(Cast);
        _castRail.Entered += () =>
        {
            if (!_swapping)
            {
                AnchorSection(CastSection);
            }
        };

        ActionButton.Attach(StartOverButton);
        ActionButton.Attach(WatchlistButton);
        ActionButton.Attach(MoreButton);

        // Entering the action row from outside always lands on Play (HeroActionRow's focusProperties enter).
        Actions.GettingFocus += OnActionsGettingFocus;
        Actions.GotFocus += (_, _) =>
        {
            ScrollTo(0);
            if (_detail is { } detail && _selectedTab?.Season is null)
            {
                ShowEditorial(detail, null);
            }
        };

        // The page moves only by the anchors above, never by a focused element's own bring-into-view.
        PageContent.BringIntoViewRequested += (_, args) => args.Handled = true;
        PageScroller.PreviewKeyDown += OnPagePreviewKeyDown;
    }

    /// <summary>
    /// Keyboard Up/Down move focus like the d-pad. The page and season ScrollViewers would otherwise
    /// take the arrows to scroll, so focus lagged behind or stuck. Gamepad keys already go to the focus engine.
    /// </summary>
    private void OnPagePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            var right = e.Key is VirtualKey.Right or VirtualKey.GamepadDPadRight or VirtualKey.GamepadLeftThumbstickRight;
            var left = e.Key is VirtualKey.Left or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadLeftThumbstickLeft;
            if (right || left)
            {
                // Match TvItemDetailScreen.kt's action row focusGroup: Left/Right stop at the ends.
                var actions = Actions.Children.Where(child => child is Control c && c.Visibility == Visibility.Visible && c.IsEnabled && c.IsTabStop);
                var edge = right ? actions.LastOrDefault() : actions.FirstOrDefault();
                if (edge is not null && ReferenceEquals(e.OriginalSource, edge))
                {
                    e.Handled = true;
                    return;
                }
            }

            var direction = e.Key switch
            {
                VirtualKey.Up => FocusNavigationDirection.Up,
                VirtualKey.Down => FocusNavigationDirection.Down,
                _ => FocusNavigationDirection.None,
            };
            if (direction == FocusNavigationDirection.None)
            {
                return;
            }

            // Handled even when nothing lies that way: a vertical key never scrolls or moves focus sideways.
            FocusManager.TryMoveFocus(direction);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Log.Error("Detail keys", ex);
        }
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
            // Preloaded from Home when the card had focus, so this is usually instant.
            var detail = _detail = await client.Detail.GetAsync(_contentId);
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
        TitleText.Text = detail.Title;
        TitleLogo.Opacity = 0;
        // As on Home: a title with logo art waits briefly for it before showing the text title.
        TitleText.Opacity = detail.LogoUrl is null ? 1 : 0;
        if (detail.LogoUrl is { } logo)
        {
            var image = Artwork.LoadKept(logo, (int)LogoHeight, out var ready);
            TitleLogo.Source = image;
            if (ready)
            {
                TitleLogo.Opacity = 1;
            }
            else
            {
                image.ImageOpened += (_, _) =>
                {
                    TitleLogo.Opacity = 1;
                    TitleText.Opacity = 0;
                };
                image.ImageFailed += (_, _) => TitleText.Opacity = 1;
                var grace = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                grace.Tick += (_, _) =>
                {
                    grace.Stop();
                    if (TitleLogo.Opacity == 0)
                    {
                        TitleText.Opacity = 1;
                    }
                };
                grace.Start();
            }
        }

        SubtitleText.Text = detail.Subtitle ?? string.Empty;
        SubtitleText.Visibility = string.IsNullOrEmpty(detail.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        if (!detail.IsSeries)
        {
            // Only a series fixes the slots (focusing an episode swaps the text); a movie's stack follows its synopsis.
            Synopsis.Height = double.NaN;
            CreditLine.Margin = new Thickness(0, 6, 0, 0);
            SelectorSlot.Margin = new Thickness(0, 6, 0, 0);
            Actions.Margin = new Thickness(0, 19, 0, 0);
        }

        ShowEditorial(detail, null);
        CreditLine.Text = detail.CreditLine ?? string.Empty;

        PlayLabel.Text = Core.Detail.PlayLabel.For(detail, _nextUp);
        // tvOS reserves a fixed width for the series Play label so the round actions never shift.
        PlayButton.Width = detail.IsSeries ? 170 : double.NaN;
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
    /// The facts line and synopsis: the title's own, or the focused episode's on a series (air date and
    /// runtime replace the show's facts; the content rating and external ratings stay;
    /// TvDetailMetadata.seriesEpisodeFactsLine).
    /// </summary>
    private void ShowEditorial(ItemDetail detail, EpisodeCard? episode)
    {
        if (episode is null)
        {
            ShowMetadata(detail.ContentRating, detail.Facts, detail.Ratings, detail.SourceTokens);
            Synopsis.Text = detail.Overview ?? string.Empty;
        }
        else
        {
            ShowMetadata(detail.ContentRating, episode.HeroFacts, detail.Ratings, episode.HeroTokens);
            Synopsis.Text = episode.HeroSynopsis;
        }
    }

    /// <summary>
    /// TvDetailHero.MetadataRow / TvFactsRow: the outlined content-rating chip leads, then facts,
    /// external ratings and the type and genres, with dots between them. Built in code: resources
    /// can't be read from code under AOT.
    /// </summary>
    private void ShowMetadata(string? contentRating, IReadOnlyList<string> facts, IReadOnlyList<DisplayRating> ratings, IReadOnlyList<string> trailing)
    {
        Metadata.Children.Clear();
        if (contentRating is not null)
        {
            Metadata.Children.Add(Chip(contentRating));
        }

        var tokens = 0;
        void Add(UIElement token)
        {
            if (tokens++ > 0)
            {
                Metadata.Children.Add(Text("·", Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF)));
            }

            Metadata.Children.Add(token);
        }

        foreach (var fact in facts)
        {
            Add(Text(fact, Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)));
        }

        foreach (var rating in ratings)
        {
            Add(RatingMark.Build(rating, 12));
        }

        foreach (var token in trailing)
        {
            Add(Text(token, Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)));
        }
    }

    private static TextBlock Text(string text, Color color) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = Windows.UI.Text.FontWeights.Medium,
        Foreground = new SolidColorBrush(color),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>TvDetailHero.RatingChip: 0.75 px white @ 70% outline, 2.5 px corners.</summary>
    private static Border Chip(string text) => new()
    {
        BorderBrush = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(0.75),
        CornerRadius = new CornerRadius(2.5),
        Padding = new Thickness(6, 1.5, 6, 1.5),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 9, FontWeight = Windows.UI.Text.FontWeights.Black, CharacterSpacing = 30, Foreground = new SolidColorBrush(Colors.White) },
    };

    private void ShowFlags(ItemFlags flags)
    {
        // Bookmark outline, filled once it's on the watchlist.
        WatchlistIcon.Fill = flags.InWatchlist ? new SolidColorBrush(WatchlistButton.FocusState == FocusState.Unfocused ? Colors.White : Colors.Black) : null;
        AutomationProperties.SetName(WatchlistButton, flags.InWatchlist ? "Remove from watchlist" : "Add to watchlist");
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

        // "Show" first, then every season (TvSeriesModePicker).
        _tabs = [new SeasonView(null), .. seasons.Select(season => new SeasonView(season))];
        Seasons.ItemsSource = _tabs;
        EpisodesSection.Visibility = Visibility.Visible;
        CastSection.Margin = new Thickness(0, 36, 0, 0);

        // The page opens on the show, with the next-up episode's season in the rail.
        await SelectTabAsync(_tabs[0]);
    }

    /// <summary>The next-up episode's season, which the "Show" tab lists.</summary>
    private SeasonChoice? PlaySeason() =>
        _tabs.Select(tab => tab.Season).FirstOrDefault(season => season?.Number == _detail?.PlaySeasonNumber)
        ?? _tabs.Select(tab => tab.Season).FirstOrDefault(season => season is not null);

    /// <summary>
    /// Show and each season are focus-driven modes: landing on a tab switches to it at once, without
    /// a press (TvSeriesModeTab). Show puts the series back in the hero.
    /// </summary>
    private async Task SelectTabAsync(SeasonView tab, bool focusEdge = false, int edge = 0)
    {
        if (_detail is not { } detail || Session.Client is not { } client)
        {
            return;
        }

        _selectedTab = tab;
        UpdateTabs();
        if (tab.Season is null)
        {
            ShowEditorial(detail, null);
        }

        if ((tab.Season ?? PlaySeason()) is not { } season || season.Number == _shownSeason)
        {
            return;
        }

        _shownSeason = season.Number;
        var episodes = await client.Detail.EpisodesAsync(detail.ContentId, season.Number);
        if (_shownSeason != season.Number)
        {
            return;
        }

        var views = episodes.Select(episode => new EpisodeView(episode, episode.ContentId == detail.PlayTargetId)).ToList();
        _swapping = focusEdge;
        Episodes.ItemsSource = views;
        if (views.FirstOrDefault(view => view.Episode.ContentId == detail.PlayTargetId) is { } nextUp)
        {
            _nextUp = nextUp.Episode;
            PlayLabel.Text = Core.Detail.PlayLabel.For(detail, nextUp.Episode);
        }

        // Entering the rail lands on the next-up episode in its season, else the first.
        _episodesRail.Remembered = views.FirstOrDefault(view => view.Episode.ContentId == detail.PlayTargetId) ?? views.FirstOrDefault();
        if (focusEdge && views.Count > 0)
        {
            // The far end isn't realized until the rail scrolls there.
            var target = edge > 0 ? views[0] : views[^1];
            _episodesRail.Remembered = target;
            Episodes.ScrollIntoView(target);
            FocusRetry.Until(Episodes, () =>
            {
                if (!_episodesRail.FocusItem(target, FocusState.Keyboard))
                {
                    return false;
                }

                _swapping = false;
                return true;
            });
        }
    }

    private void UpdateTabs()
    {
        if (Seasons.ItemsPanelRoot is null)
        {
            Seasons.UpdateLayout();
        }

        // Under AOT the containers don't come back as ContentPresenter; go by each tab's DataContext.
        foreach (var container in (IEnumerable<UIElement>?)Seasons.ItemsPanelRoot?.Children ?? Array.Empty<UIElement>())
        {
            if (FindChild<ToggleButton>(container) is { DataContext: SeasonView view } tab)
            {
                tab.IsChecked = ReferenceEquals(view, _selectedTab);
            }
        }
    }

    /// <summary>A tab realized after the selection changed picks up its checked state here.</summary>
    private void OnTabLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { DataContext: SeasonView view } tab)
        {
            tab.IsChecked = ReferenceEquals(view, _selectedTab);
        }
    }

    /// <summary>Right past the last episode continues into the next season, Left past the first into the previous one.</summary>
    private async Task StepSeasonAsync(int step)
    {
        try
        {
            var seasons = _tabs.Where(tab => tab.Season is not null).ToList();
            var index = seasons.FindIndex(tab => tab.Season!.Number == _shownSeason);
            if (index < 0 || index + step < 0 || index + step >= seasons.Count)
            {
                return;
            }

            await SelectTabAsync(seasons[index + step], focusEdge: true, edge: step);
        }
        catch (Exception ex)
        {
            Log.Error("Season step", ex);
        }
    }

    private void OnEpisodeFocused(EpisodeView? view)
    {
        if (view is null || _detail is not { } detail)
        {
            return;
        }

        // Focusing an episode selects its season's tab and puts the episode in the hero.
        if (_tabs.FirstOrDefault(tab => tab.Season?.Number == view.Episode.SeasonNumber) is { } tab && !ReferenceEquals(tab, _selectedTab))
        {
            _selectedTab = tab;
            UpdateTabs();
        }

        ShowEditorial(detail, view.Episode);
    }

    private void ShowCast(ItemDetail detail)
    {
        if (detail.Cast.Count == 0)
        {
            return;
        }

        Cast.ItemsSource = detail.Cast.Take(30).Select(member => new CastView(member)).ToList();
        CastSection.Visibility = Visibility.Visible;
        if (!detail.IsSeries)
        {
            CastSection.Margin = new Thickness(0, 10, 0, 0);
        }
    }

    private async void OnSeasonFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SeasonView view } && !ReferenceEquals(view, _selectedTab))
        {
            try
            {
                await SelectTabAsync(view);
            }
            catch (Exception ex)
            {
                Log.Error("Season load", ex);
            }
        }
    }

    private async void OnSeasonClick(object sender, RoutedEventArgs e)
    {
        // A press re-checks the tab the toggle just unchecked; selection itself follows focus.
        UpdateTabs();
        if (sender is FrameworkElement { DataContext: SeasonView view } && !ReferenceEquals(view, _selectedTab))
        {
            await SelectTabAsync(view);
        }
    }

    private void OnActionsGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        try
        {
            if (!IsWithin(args.OldFocusedElement, Actions) && IsWithin(args.NewFocusedElement, Actions)
                && !ReferenceEquals(args.NewFocusedElement, PlayButton))
            {
                args.TrySetNewFocusedElement(PlayButton);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Action row focus", ex);
        }
    }

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

    private void ScrollTo(double offset) => PageScroller.ChangeView(null, Math.Max(0, offset), null);

    /// <summary>A body section's top settles 18% down the screen when focus enters it (detailBodySectionAnchor).</summary>
    private void AnchorSection(FrameworkElement section)
    {
        var top = section.TransformToVisual(PageContent).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        ScrollTo(top - PageScroller.ViewportHeight * SectionAnchor);
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
            client.Detail.Forget(detail.ContentId);
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
