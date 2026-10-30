using System.Numerics;
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
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>A title's detail page (TvItemDetailScreen). Opened with the content id as the navigation parameter.</summary>
public sealed partial class DetailPage : Page, IBackHandler, IRestoresFocus
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
    private EpisodeCard? _playEpisode;
    private SeriesDetailEntry? _routeEntry;
    private string _synopsisOverview = string.Empty;
    private string? _synopsisTagline;
    private string _synopsisTitle = string.Empty;
    private Control? _lastFocused;
    private int _episodeLoadVersion;
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
                AttachTitleShadow(TitleText, TitleShadow);
                AttachTitleShadow(SubtitleText, SubtitleShadow);
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

        SynopsisButton.XYFocusDown = PlayButton;
        SynopsisButton.GotFocus += (_, _) => ScrollTo(0);
        PageContent.GotFocus += (_, args) => _lastFocused = args.OriginalSource as Control ?? _lastFocused;

        // The page moves only by the anchors above, never by a focused element's own bring-into-view.
        PageContent.BringIntoViewRequested += (_, args) => args.Handled = true;
        PageScroller.PreviewKeyDown += OnPagePreviewKeyDown;
        PreviewKeyDown += OnDialogPreviewKeyDown;
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
                if (edge is not null && IsWithin(e.OriginalSource as DependencyObject, edge))
                {
                    e.Handled = true;
                    return;
                }
            }

            var direction = VerticalDirection(e.Key);
            if (direction == FocusNavigationDirection.None)
            {
                return;
            }

            // TvExpandableSynopsis.kt is the only stop above actions. Up at it stays put.
            if (direction != FocusNavigationDirection.Up || !IsWithin(e.OriginalSource as DependencyObject, SynopsisButton))
            {
                FocusManager.TryMoveFocus(direction);
            }

            // Handled even when nothing lies that way: a vertical key never scrolls or moves focus sideways.
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
        _routeEntry = e.Parameter as SeriesDetailEntry;
        _contentId = _routeEntry?.SeriesId ?? e.Parameter as string ?? string.Empty;
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
        PageScroller.Visibility = Visibility.Visible;
        _playEpisode = null;
        _shownSeason = null;
        _episodeLoadVersion++;
        try
        {
            // Preloaded from Home when the card had focus, so this is usually instant.
            var detail = await client.Detail.GetAsync(_contentId);
            var entry = _routeEntry;
            if (detail.SeriesEntry is { } redirect)
            {
                try
                {
                    // TvItemDetailScreen.kt replaces episode/season routes in place. Never push a
                    // second detail entry: Back must return to the card, not redirect in a loop.
                    var parent = await client.Detail.GetAsync(redirect.SeriesId);
                    if (parent.IsSeries)
                    {
                        detail = parent;
                        entry = redirect;
                    }
                }
                catch (Exception ex)
                {
                    // Like tvSeriesDetailRedirect's defensive parent check, retain a usable leaf.
                    Log.Error("Detail series redirect", ex);
                }
            }

            _detail = detail;
            ShowHero(detail);
            ShowCast(detail);
            FocusRetry.Until(PlayButton, () => PlayButton.Focus(FocusState.Programmatic));

            if (detail.PlaysEpisodes)
            {
                await LoadSeasonsAsync(client.Detail, detail, entry);
            }
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
        var (title, subtitle) = DetailTitle.Split(detail.Title);
        TitleText.Text = title.ToUpperInvariant();
        SubtitleText.Text = (detail.Subtitle ?? subtitle)?.ToUpperInvariant() ?? string.Empty;
        SubtitleSlot.Visibility = SubtitleText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        TitleLogo.Opacity = 0;
        // As on Home: a title with logo art waits briefly for it before showing the text title.
        TextTitle.Opacity = detail.LogoUrl is null ? 1 : 0;
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
                    TextTitle.Opacity = 0;
                };
                image.ImageFailed += (_, _) => TextTitle.Opacity = 1;
                var grace = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                grace.Tick += (_, _) =>
                {
                    grace.Stop();
                    if (TitleLogo.Opacity == 0)
                    {
                        TextTitle.Opacity = 1;
                    }
                };
                grace.Start();
            }
        }

        // Only a series fixes the slots; a movie's stack follows its synopsis (TvDetailHero.kt).
        SynopsisButton.Height = detail.IsSeries ? 56 : double.NaN;
        CreditLine.Margin = new Thickness(0, detail.IsSeries ? 13 : 6, 0, 0);
        SelectorSlot.Margin = new Thickness(0, detail.IsSeries ? 2 : 6, 0, 0);
        Actions.Margin = new Thickness(0, detail.IsSeries ? 13 : 19, 0, 0);

        ShowEditorial(detail, null);
        CreditLine.Text = detail.CreditLine ?? string.Empty;

        ShowPlayback();
        // TvItemDetailScreen.kt reserves 170dp so episode labels never shift the round actions.
        PlayButton.Width = detail.IsSeries ? 170 : double.NaN;
        ShowFlags(detail.Flags);
        SetPageTint(null);

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
            Synopsis.Text = string.IsNullOrWhiteSpace(episode.Overview) ? string.Empty : episode.HeroSynopsis;
        }

        // TvExpandableSynopsis.kt previews the episode name, but the dialog gets its clean full
        // overview. The series tagline belongs only to Show mode (TvDetailHero.kt).
        _synopsisOverview = (episode is null ? detail.Overview : episode.Overview) ?? string.Empty;
        _synopsisTagline = episode is null ? detail.Tagline : null;
        _synopsisTitle = episode?.Title ?? detail.Subtitle ?? detail.Title;
        SynopsisButton.IsTabStop = !string.IsNullOrWhiteSpace(_synopsisOverview);
        foreach (var action in Actions.Children.OfType<Button>())
        {
            action.XYFocusUp = SynopsisButton.IsTabStop ? SynopsisButton : null;
        }
    }

    private string? PlayTargetId => _detail is { } detail
        ? detail.PlaysEpisodes ? _playEpisode?.ContentId : detail.PlayTargetId
        : null;

    private void ShowPlayback()
    {
        if (_detail is not { } detail)
        {
            return;
        }

        PlayLabel.Text = Core.Detail.PlayLabel.For(detail, _playEpisode);
        StartOverButton.Visibility = Core.Detail.PlayLabel.ResumeFor(detail, _playEpisode) is > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        FavoriteLabel.Text = flags.IsFavorite ? "Remove from Favorites" : "Add to Favorites";
        FavoriteOutline.Visibility = flags.IsFavorite ? Visibility.Collapsed : Visibility.Visible;
        FavoriteBroken.Visibility = flags.IsFavorite ? Visibility.Visible : Visibility.Collapsed;
        WatchedLabel.Text = DetailActions.WatchedLabel(_detail?.Type ?? string.Empty, flags.Played);
        WatchedMarkIcon.Visibility = flags.Played ? Visibility.Collapsed : Visibility.Visible;
        WatchedUnmarkIcon.Visibility = flags.Played ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task TintFromAsync(Uri backdrop)
    {
        var tint = await AmbientTint.SampleAsync(backdrop);
        if (_detail?.BackdropUrl == backdrop)
        {
            SetPageTint(tint);
        }
    }

    private void SetPageTint(Color? sampled)
    {
        // TvItemDetailScreen.tvDetailPageSurfaceColor: one opaque tint * 0.42, never a gradient.
        byte Channel(double value) => (byte)Math.Round(value * TintStrength * 255);
        var color = sampled is { } tint
            ? Color.FromArgb(255, Channel(tint.R / 255d), Channel(tint.G / 255d), Channel(tint.B / 255d))
            : Color.FromArgb(255, Channel(0.04), Channel(0.12), Channel(0.14));
        Wash.Fill = new SolidColorBrush(color);
    }

    private static void AttachTitleShadow(TextBlock text, FrameworkElement host)
    {
        // TvDetailHero.heroDisplayHero: black @ 0.55, blur 16, y 4. A text alpha mask keeps
        // the shadow on the glyphs, not a rectangular ThemeShadow behind the whole title slot.
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        var shadow = compositor.CreateDropShadow();
        shadow.Color = Colors.Black;
        shadow.Opacity = 0.55f;
        shadow.BlurRadius = 16;
        shadow.Offset = new Vector3(0, 4, 0);
        shadow.Mask = text.GetAlphaMask();
        var visual = compositor.CreateSpriteVisual();
        visual.Shadow = shadow;
        void Resize() => visual.Size = new Vector2((float)text.ActualWidth, (float)text.ActualHeight);
        text.SizeChanged += (_, _) => Resize();
        Resize();
        ElementCompositionPreview.SetElementChildVisual(host, visual);
    }

    private async Task LoadSeasonsAsync(DetailFeed feed, ItemDetail detail, SeriesDetailEntry? entry)
    {
        // TvItemDetailScreen.kt keeps the series viewport (including Show) mounted even when
        // the season request fails. Only the title request owns the full-page error panel.
        _tabs = detail.IsSeries ? [new SeasonView(null)] : [];
        Seasons.ItemsSource = _tabs;
        EpisodesSection.Visibility = Visibility.Visible;
        SetEpisodesLoading(true);
        var seriesId = detail.IsSeries ? detail.ContentId : detail.SeriesId;
        try
        {
            if (seriesId is not null)
            {
                var seasons = await feed.SeasonsAsync(seriesId);
                _tabs.AddRange(seasons.Select(season => new SeasonView(season)));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Detail seasons", ex);
        }

        Seasons.ItemsSource = _tabs.ToList();
        var entrySeason = entry?.SeasonNumber ?? detail.SeasonNumber;
        var selected = _tabs.FirstOrDefault(tab => tab.Season?.Number == entrySeason) ?? _tabs.FirstOrDefault();
        if (selected is not null)
        {
            await SelectTabAsync(selected, preferredEpisodeId: entry?.EpisodeId, focusEntry: entry?.EpisodeId is not null);
        }
        else
        {
            Episodes.ItemsSource = new List<EpisodeView>();
            SetEpisodesLoading(false);
        }
    }

    /// <summary>The initial next-up season; Show subsequently keeps the current season (TvItemDetailScreen.kt).</summary>
    private SeasonChoice? PlaySeason() =>
        _tabs.Select(tab => tab.Season).FirstOrDefault(season => season?.Number == _detail?.PlaySeasonNumber)
        ?? _tabs.Select(tab => tab.Season).FirstOrDefault(season => season is not null);

    /// <summary>Show / Season modes follow focus, not a press (TvSeasonPicker.kt).</summary>
    private async Task SelectTabAsync(SeasonView tab, bool focusEdge = false, int edge = 0,
        string? preferredEpisodeId = null, bool focusEntry = false, bool reload = false)
    {
        if (_detail is not { } detail || Session.Client is not { } client)
        {
            return;
        }

        _selectedTab = tab;
        UpdateTabs();
        var season = tab.Season ?? _tabs.Select(view => view.Season).FirstOrDefault(choice => choice?.Number == _shownSeason) ?? PlaySeason();
        if (season is null)
        {
            _episodeLoadVersion++;
            _shownSeason = null;
            Episodes.ItemsSource = new List<EpisodeView>();
            _episodesRail.Remembered = null;
            SetPlayEpisode(null);
            ShowEditorial(detail, null);
            SetEpisodesLoading(false);
            return;
        }

        if (!reload && season.Number == _shownSeason && Episodes.Items.Count > 0)
        {
            if (tab.Season is null && Episodes.ItemsSource is List<EpisodeView> existing)
            {
                SetPlayEpisode(DetailEpisodes.NextUp(existing.Select(view => view.Episode).ToList()));
            }

            ShowEditorial(detail, tab.Season is null ? null : _playEpisode);
            return;
        }

        var version = ++_episodeLoadVersion;
        _shownSeason = season.Number;
        _playEpisode = null;
        ShowPlayback();
        _swapping = focusEdge;
        Episodes.ItemsSource = new List<EpisodeView>();
        _episodesRail.Remembered = null;
        SetEpisodesLoading(true);
        IReadOnlyList<EpisodeCard> episodes;
        try
        {
            var seriesId = detail.IsSeries ? detail.ContentId : detail.SeriesId;
            episodes = seriesId is null ? [] : await client.Detail.EpisodesAsync(seriesId, season.Number);
        }
        catch (Exception ex)
        {
            Log.Error("Detail episodes", ex);
            episodes = [];
        }

        // A -> B -> A can complete out of order. Season number alone isn't a request identity.
        if (version != _episodeLoadVersion)
        {
            return;
        }

        var suggested = episodes.FirstOrDefault(episode => episode.ContentId == preferredEpisodeId) ?? DetailEpisodes.NextUp(episodes);
        var views = episodes.Select(episode => new EpisodeView(episode, episode.ContentId == suggested?.ContentId)).ToList();
        var target = focusEdge && views.Count > 0
            ? edge > 0 ? views[0] : views[^1]
            : views.FirstOrDefault(view => view.Episode.ContentId == suggested?.ContentId);
        Episodes.ItemsSource = views;
        _episodesRail.Remembered = target;
        SetPlayEpisode(target?.Episode);
        ShowEditorial(_detail ?? detail, _selectedTab?.Season is null ? null : _playEpisode);
        SetEpisodesLoading(false);
        _swapping = false;
        if (target is not null)
        {
            // TvDetailEpisodeRail.kt centers the route/suggested episode once per season, not
            // after every Left/Right. Rail retains its normal pin-on-focus behavior afterwards.
            RevealEpisode(target, focusEdge || (focusEntry && target.Episode.ContentId == preferredEpisodeId), center: !focusEdge);
        }
        else
        {
            _swapping = false;
            if (focusEdge)
            {
                FocusRetry.Until(Seasons, () => _seasonsRail.Focus());
            }
        }
    }

    private void SetEpisodesLoading(bool loading)
    {
        EpisodesLoading.IsActive = loading;
        EpisodesLoading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        NoEpisodes.Visibility = !loading && Episodes.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetPlayEpisode(EpisodeCard? episode)
    {
        _playEpisode = episode;
        if (Episodes.ItemsSource is List<EpisodeView> views)
        {
            foreach (var view in views)
            {
                view.SetCurrent(view.Episode.ContentId == episode?.ContentId);
            }
        }

        ShowPlayback();
    }

    private void RevealEpisode(EpisodeView target, bool focus, bool center)
    {
        var version = _episodeLoadVersion;
        Episodes.ScrollIntoView(target);
        FocusRetry.Until(Episodes, () =>
        {
            if (version != _episodeLoadVersion)
            {
                return true;
            }

            var container = Episodes.ItemsPanelRoot?.Children.OfType<ContentControl>().FirstOrDefault(item => ReferenceEquals(item.Content, target));
            var scroller = FindChild<ScrollViewer>(Episodes);
            if (container is null || container.ActualWidth == 0 || scroller is null || scroller.ViewportWidth == 0)
            {
                return false;
            }

            if (focus)
            {
                _episodesRail.Pin = !center;
                try
                {
                    if (!_episodesRail.FocusItem(target, FocusState.Keyboard))
                    {
                        return false;
                    }
                }
                finally
                {
                    _episodesRail.Pin = true;
                }
            }

            if (center)
            {
                var x = container.TransformToVisual(scroller).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
                scroller.ChangeView(scroller.HorizontalOffset + x + container.ActualWidth / 2 - scroller.ViewportWidth / 2, null, null, true);
            }

            _swapping = false;
            return true;
        });
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

        // TvItemDetailScreen.kt activates the focused episode for Play/Resume AND Start Over,
        // not just for the facts and synopsis. Show-level mutations still target the series.
        SetPlayEpisode(view.Episode);
        ShowEditorial(detail, view.Episode);
    }

    private void ShowCast(ItemDetail detail)
    {
        CastSection.Visibility = detail.Cast.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // TvCastCrewSection.kt caps the rail at 24 people, preserving the server's cast order.
        Cast.ItemsSource = detail.Cast.Take(24).Select(member => new CastView(member)).ToList();
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
        // A press re-checks the selected toggle; it can also retry an empty/failed season.
        UpdateTabs();
        if (sender is FrameworkElement { DataContext: SeasonView view }
            && (!ReferenceEquals(view, _selectedTab) || Episodes.Items.Count == 0))
        {
            try
            {
                await SelectTabAsync(view);
            }
            catch (Exception ex)
            {
                Log.Error("Season click", ex);
            }
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

    private void OnCardFocus(object sender, RoutedEventArgs e) => ApplyCardFocus(e.OriginalSource as ContentControl, true);

    private void OnCardBlur(object sender, RoutedEventArgs e) => ApplyCardFocus(e.OriginalSource as ContentControl, false);

    private static void ApplyCardFocus(ContentControl? item, bool focused)
    {
        if (item?.ContentTemplateRoot is not FrameworkElement root)
        {
            return;
        }

        if (item.Content is EpisodeView)
        {
            // TvDetailEpisodeRail.kt deliberately omits the shared card halo: 3px inside
            // white@0.9 border and a 1.04 still-only lift, with a 180ms animation.
            if (root.FindName("EpisodeFocusRing") is UIElement ring)
            {
                ring.Opacity = focused ? 1 : 0;
            }

            if (root.FindName("Art") is FrameworkElement art)
            {
                art.ScaleTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) };
                art.CenterPoint = new Vector3((float)art.ActualWidth / 2, (float)art.ActualHeight / 2, 0);
                var scale = focused ? 1.04f : 1;
                art.Scale = new Vector3(scale, scale, 1);
            }
        }
        else if (item.Content is CastView)
        {
            CardFocus.Apply(item, focused);
            if (root.FindName("CastName") is TextBlock name)
            {
                name.Foreground = new SolidColorBrush(Color.FromArgb(focused ? (byte)255 : (byte)0xE0, 255, 255, 255));
            }
        }
    }

    private async void OnPlayClick(object sender, RoutedEventArgs e)
    {
        // TvItemDetailScreen.kt never plays an empty series container while its target loads.
        if (PlayTargetId is not null)
        {
            await PlaybackComingAsync();
        }
    }

    private async void OnStartOverClick(object sender, RoutedEventArgs e)
    {
        if (PlayTargetId is not null)
        {
            await PlaybackComingAsync();
        }
    }

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

    private static FocusNavigationDirection VerticalDirection(VirtualKey key) => key switch
    {
        VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp => FocusNavigationDirection.Up,
        VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown => FocusNavigationDirection.Down,
        _ => FocusNavigationDirection.None,
    };

    private void OnSynopsisClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_synopsisOverview))
        {
            return;
        }

        FullSynopsisTitle.Text = _synopsisTitle;
        FullSynopsisTagline.Text = _synopsisTagline ?? string.Empty;
        FullSynopsisTagline.Visibility = string.IsNullOrWhiteSpace(_synopsisTagline) ? Visibility.Collapsed : Visibility.Visible;
        FullSynopsis.Text = _synopsisOverview;
        MorePanel.Visibility = Visibility.Collapsed;
        SynopsisPanel.Visibility = Visibility.Visible;
        ResizeSynopsisDialog();
        OpenDialog();
        SynopsisScroller.ChangeView(null, 0, null, true);
        FocusRetry.Until(SynopsisScroller, () => SynopsisScroller.Focus(FocusState.Programmatic));
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (_detail is not { } detail)
        {
            return;
        }

        ShowFlags(detail.Flags);
        var season = _selectedTab?.Season;
        SeasonWatchedItem.Visibility = detail.IsSeries && season is { EpisodeCount: > 0, ContentId.Length: > 0 }
            ? Visibility.Visible : Visibility.Collapsed;
        if (season is not null)
        {
            SeasonWatchedLabel.Text = $"Mark {season.Title} {(season.Played ? "Unwatched" : "Watched")}";
            SeasonMarkIcon.Visibility = season.Played ? Visibility.Collapsed : Visibility.Visible;
            SeasonUnmarkIcon.Visibility = season.Played ? Visibility.Visible : Visibility.Collapsed;
        }

        var hasSeries = detail.Type is "episode" or "season" && !string.IsNullOrWhiteSpace(detail.SeriesId);
        SeriesItem.Visibility = hasSeries ? Visibility.Visible : Visibility.Collapsed;
        SeasonItem.Visibility = hasSeries && detail.Type == "episode" && detail.SeasonNumber is > 0 ? Visibility.Visible : Visibility.Collapsed;
        GoToSeasonLabel.Text = $"Go to Season {detail.SeasonNumber}";
        MorePanel.Visibility = Visibility.Visible;
        SynopsisPanel.Visibility = Visibility.Collapsed;
        OpenDialog();
        FocusRetry.Until(FavoriteItem, () => FavoriteItem.Focus(FocusState.Programmatic));
    }

    private void OpenDialog()
    {
        // TvOptionDialog.kt / TvExpandableSynopsis.kt own a modal focus boundary. Disabling
        // only the underlying viewport keeps vertical focus searches out of the page rails.
        _lastFocused = FocusManager.GetFocusedElement() as Control ?? _lastFocused;
        DialogLayer.Visibility = Visibility.Visible;
        PageScroller.IsEnabled = false;
    }

    private void CloseDialog()
    {
        DialogLayer.Visibility = Visibility.Collapsed;
        PageScroller.IsEnabled = true;
        RestoreFocus();
    }

    public bool OnBack()
    {
        if (DialogLayer.Visibility != Visibility.Visible)
        {
            return false;
        }

        CloseDialog();
        return true;
    }

    public void RestoreFocus() => FocusRetry.Until(PageContent, () =>
        (_lastFocused is { IsEnabled: true, Visibility: Visibility.Visible } control && control.Focus(FocusState.Programmatic))
        || PlayButton.Focus(FocusState.Programmatic));

    private void OnDialogBackgroundTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, DialogLayer))
        {
            CloseDialog();
            e.Handled = true;
        }
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) => ResizeSynopsisDialog();

    private void ResizeSynopsisDialog()
    {
        // TvExpandableSynopsis.kt uses 62% of the window width and 72% of its height.
        if (SynopsisPanel is not null)
        {
            SynopsisPanel.Width = Root.ActualWidth * 0.62;
            SynopsisPanel.Height = Root.ActualHeight * 0.72;
        }
    }

    private void OnDialogPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DialogLayer.Visibility != Visibility.Visible)
        {
            return;
        }

        try
        {
            if (e.Key is VirtualKey.Escape or VirtualKey.Back or VirtualKey.GamepadB)
            {
                CloseDialog();
                e.Handled = true;
                return;
            }

            var synopsis = SynopsisPanel.Visibility == Visibility.Visible;
            if (synopsis && e.Key is VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA)
            {
                CloseDialog();
                e.Handled = true;
                return;
            }

            var direction = VerticalDirection(e.Key);
            if (direction != FocusNavigationDirection.None)
            {
                if (synopsis)
                {
                    var step = direction == FocusNavigationDirection.Up ? -180 : 180;
                    SynopsisScroller.ChangeView(null, Math.Clamp(SynopsisScroller.VerticalOffset + step, 0, SynopsisScroller.ScrollableHeight), null);
                }
                else
                {
                    // First/last rows stop; Up/Down never wrap into another column or the page.
                    var rows = MoreOptions.Children.OfType<Button>().Where(row => row.Visibility == Visibility.Visible).ToList();
                    var focused = FocusManager.GetFocusedElement();
                    var edge = direction == FocusNavigationDirection.Up ? rows.FirstOrDefault() : rows.LastOrDefault();
                    if (!ReferenceEquals(focused, edge))
                    {
                        FocusManager.TryMoveFocus(direction);
                    }
                }

                e.Handled = true;
            }
            else if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadDPadRight
                or VirtualKey.GamepadLeftThumbstickLeft or VirtualKey.GamepadLeftThumbstickRight)
            {
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Detail dialog keys", ex);
        }
    }

    private void OnMoreRowFocus(object sender, RoutedEventArgs e) => RecolorMenuIcons(sender as DependencyObject, true);

    private void OnMoreRowBlur(object sender, RoutedEventArgs e) => RecolorMenuIcons(sender as DependencyObject, false);

    private static void RecolorMenuIcons(DependencyObject? node, bool focused)
    {
        if (node is null)
        {
            return;
        }

        if (node is Windows.UI.Xaml.Shapes.Shape { Stroke: not null } shape)
        {
            shape.Stroke = new SolidColorBrush(focused ? Color.FromArgb(255, 11, 11, 12) : Color.FromArgb(255, 237, 237, 237));
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
        {
            RecolorMenuIcons(VisualTreeHelper.GetChild(node, index), focused);
        }
    }

    private async void OnWatchlistClick(object sender, RoutedEventArgs e) =>
        await ToggleAsync(flags => flags with { InWatchlist = !flags.InWatchlist }, (feed, id, flags) => feed.SetWatchlistAsync(id, flags.InWatchlist));

    private async void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        CloseDialog();
        await ToggleAsync(flags => flags with { IsFavorite = !flags.IsFavorite }, (feed, id, flags) => feed.SetFavoriteAsync(id, flags.IsFavorite));
    }

    private async void OnWatchedClick(object sender, RoutedEventArgs e)
    {
        CloseDialog();
        await ToggleAsync(flags => flags with { Played = !flags.Played }, (feed, id, flags) => feed.SetPlayedAsync(id, flags.Played), refreshWatched: true);
    }

    private async void OnSeasonWatchedClick(object sender, RoutedEventArgs e)
    {
        var season = _selectedTab?.Season;
        CloseDialog();
        if (_busy || season is null || season.ContentId.Length == 0 || Session.Client is not { } client)
        {
            return;
        }

        _busy = true;
        ActionStatus.Text = string.Empty;
        try
        {
            // The existing Kiota watched/{id} endpoint accepts a season's content ID and
            // expands it server-side. Never fan out per-episode writes (TvItemDetailViewModel.kt).
            await client.Detail.SetPlayedAsync(season.ContentId, !season.Played);
            client.Detail.Forget(season.ContentId);
            await RefreshWatchedAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Detail season watched", ex);
            ActionStatus.Text = "Couldn't update that. Try again.";
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Flips a flag at once and tells the server; puts it back if the server refuses.</summary>
    private async Task ToggleAsync(Func<ItemFlags, ItemFlags> change, Func<DetailFeed, string, ItemFlags, Task> send, bool refreshWatched = false)
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
            if (refreshWatched)
            {
                await RefreshWatchedAsync();
            }
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

    private async Task RefreshWatchedAsync()
    {
        if (_detail is not { } detail || Session.Client is not { } client)
        {
            return;
        }

        try
        {
            // Aggregate flags and per-episode resume state come from the server after a
            // season/series mutation, including rewatches the container write leaves intact.
            client.Detail.Forget(detail.ContentId);
            var refreshed = await client.Detail.GetAsync(detail.ContentId);
            if (_detail?.ContentId != detail.ContentId)
            {
                return;
            }

            _detail = refreshed;
            ShowFlags(refreshed.Flags);
            ShowPlayback();
            if (!detail.PlaysEpisodes)
            {
                return;
            }

            var seriesId = detail.IsSeries ? detail.ContentId : detail.SeriesId;
            if (seriesId is null)
            {
                return;
            }

            var seasons = await client.Detail.SeasonsAsync(seriesId);
            var selectedNumber = _selectedTab?.Season?.Number;
            var activeEpisode = _playEpisode?.ContentId;
            _tabs = detail.IsSeries ? [new SeasonView(null)] : [];
            _tabs.AddRange(seasons.Select(season => new SeasonView(season)));
            Seasons.ItemsSource = _tabs;
            var selected = _tabs.FirstOrDefault(tab => tab.Season?.Number == selectedNumber) ?? _tabs.FirstOrDefault();
            if (selected is not null)
            {
                await SelectTabAsync(selected, preferredEpisodeId: activeEpisode, reload: true);
            }
        }
        catch (Exception ex)
        {
            // A successful write must not be rolled back just because its subsequent read failed.
            Log.Error("Detail watched refresh", ex);
            ActionStatus.Text = "Updated, but couldn't refresh episodes. Try again.";
        }
    }

    private void OnSeriesClick(object sender, RoutedEventArgs e)
    {
        CloseDialog();
        if (_detail?.SeriesId is { } seriesId)
        {
            Frame.Navigate(typeof(DetailPage), seriesId);
        }
    }

    private void OnGoToSeasonClick(object sender, RoutedEventArgs e)
    {
        CloseDialog();
        if (_detail is { SeriesId: { } seriesId, SeasonNumber: { } season })
        {
            Frame.Navigate(typeof(DetailPage), new SeriesDetailEntry(seriesId, season, null));
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        PageScroller.Visibility = Visibility.Collapsed;
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
