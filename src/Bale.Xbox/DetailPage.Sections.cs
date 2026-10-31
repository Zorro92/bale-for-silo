using System.Numerics;
using Bale.Core.Browse;
using Bale.Core.Detail;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

public sealed partial class DetailPage
{
    private readonly Rail _trailersRail;
    private readonly Rail _similarRail;
    private IReadOnlyList<MediaCard> _similarCards = [];
    private CancellationTokenSource? _sectionLoad;
    private bool _similarLoaded;
    private bool _active;
    private int? _returnCastIndex;
    private string? _returnSimilarId;
    private int _focusGeneration;
    private bool _openingPerson;

    private void ShowSupportingSections(ItemDetail detail)
    {
        Trailers.ItemsSource = detail.Trailers.Select(card => new TrailerView(card)).ToList();
        TrailersSection.Visibility = detail.ShowsSupportingSections && detail.Trailers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailFacts.ItemsSource = detail.Details.Select((fact, index) => new DetailFactView(fact, index)).ToList();
        DetailsSection.Visibility = detail.ShowsSupportingSections && detail.Details.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SimilarHeader.Text = detail.SimilarTitle;
    }

    private async Task LoadSimilarAsync(ItemDetail detail)
    {
        _sectionLoad?.Cancel();
        _sectionLoad?.Dispose();
        var load = _sectionLoad = new CancellationTokenSource();
        _similarLoaded = false;
        ShowSimilar([]);
        if (!detail.ShowsSupportingSections || detail.Type == "episode" || Session.Client is not { } client)
        {
            _similarLoaded = true;
            return;
        }

        try
        {
            var cards = await client.Detail.SimilarAsync(detail.ContentId, load.Token);
            if (!load.IsCancellationRequested && _active && _detail?.ContentId == detail.ContentId)
            {
                _similarLoaded = true;
                ShowSimilar(cards);
            }
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // TvItemDetailScreen.kt omits an empty/failed recommendations section, not the hero.
            Log.Error("Detail similar", ex);
            if (!load.IsCancellationRequested && _active)
            {
                _similarLoaded = true;
                ShowSimilar([]);
            }
        }
    }

    private void ShowSimilar(IReadOnlyList<MediaCard> cards)
    {
        _similarCards = cards;
        _similarRail.Remembered = null;
        // Match SkylineDetailRelatedWidth/Height (TvMediaRow's default 130x195); CardView needs the size for artwork/badge scaling.
        Similar.ItemsSource = cards.Select(card => new CardView(card, SimilarHeader.Text, 130, 195, landscape: false)).ToList();
        SimilarSection.Visibility = cards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDetailsFocus(object sender, RoutedEventArgs e) => AnchorSection(DetailsSection);

    private void OnTrailerClick(object sender, ItemClickEventArgs e)
    {
        // Playback is not implemented yet. Keep the official-shaped trailer/extra cards selectable without launching a player.
    }

    private void OnSimilarClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CardView { Card: var card })
        {
            _returnCastIndex = null;
            _returnSimilarId = card.ContentId;
            NavigateWithReturn(typeof(DetailPage), card.ContentId);
        }
    }

    private async void OnCastClick(object sender, ItemClickEventArgs e)
    {
        if (_openingPerson || e.ClickedItem is not CastView view || Session.Client is not { } client)
        {
            return;
        }

        _openingPerson = true;
        try
        {
            var personId = await client.People.ResolveAsync(view.Member);
            if (_active && ReferenceEquals(client, Session.Client) && !string.IsNullOrWhiteSpace(personId))
            {
                _returnSimilarId = null;
                _returnCastIndex = Cast.Items.IndexOf(view);
                NavigateWithReturn(typeof(PersonPage), new PersonPageEntry(personId));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Detail open person", ex);
        }
        finally
        {
            _openingPerson = false;
        }
    }

    /// <summary>Explicit section edges, rather than geometric fallback, enforce the TV's vertical-never-horizontal rule.</summary>
    private void MoveSectionFocus(DependencyObject? source, FocusNavigationDirection direction)
    {
        _focusGeneration++;
        var stops = new List<(DependencyObject Region, Func<bool> Focus)>();
        if (SynopsisButton.IsTabStop)
        {
            stops.Add((SynopsisButton, () => SynopsisButton.Focus(FocusState.Keyboard)));
        }

        stops.Add((Actions, () => PlayButton.Focus(FocusState.Keyboard)));
        if (EpisodesSection.Visibility == Visibility.Visible)
        {
            if (Seasons.Items.Count > 0)
            {
                stops.Add((Seasons, () => _seasonsRail.Focus(FocusState.Keyboard)));
            }

            if (Episodes.Items.Count > 0)
            {
                stops.Add((Episodes, () => _episodesRail.Focus(FocusState.Keyboard)));
            }
        }

        if (CastSection.Visibility == Visibility.Visible) stops.Add((Cast, () => _castRail.Focus(FocusState.Keyboard)));
        if (TrailersSection.Visibility == Visibility.Visible) stops.Add((Trailers, () => _trailersRail.Focus(FocusState.Keyboard)));
        if (SimilarSection.Visibility == Visibility.Visible) stops.Add((Similar, () => _similarRail.Focus(FocusState.Keyboard)));
        if (DetailsSection.Visibility == Visibility.Visible) stops.Add((DetailsSection, () => DetailsSection.Focus(FocusState.Keyboard)));
        var index = stops.FindIndex(stop => IsWithin(source, stop.Region));
        var next = index + (direction == FocusNavigationDirection.Up ? -1 : 1);
        if (index >= 0 && next >= 0 && next < stops.Count)
        {
            stops[next].Focus();
        }
    }

    private static void ApplyTrailerFocus(FrameworkElement root, bool focused)
    {
        if (root.FindName("TrailerFocusRing") is UIElement ring) ring.Opacity = focused ? 1 : 0;
        if (root.FindName("TrailerTitle") is TextBlock title) title.Opacity = focused ? 1 : 0.92;
        if (root.FindName("PlayScrim") is Border scrim) scrim.Opacity = focused ? 0.72 : 0.55;
        // TvDetailTrailersSection.kt scales the entire card, unlike the shared art-only poster lift.
        root.ScaleTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) };
        root.CenterPoint = new Vector3((float)root.ActualWidth / 2, (float)root.ActualHeight / 2, 0);
        var scale = focused ? 1.05f : 1;
        root.Scale = new Vector3(scale, scale, 1);
    }

    /// <summary>A snapshot belongs to this exact stack entry; a per-type Frame cache cannot preserve nested Detail routes.</summary>
    private sealed record DetailReturnEntry(
        ItemDetail Detail,
        IReadOnlyList<SeasonChoice> Seasons,
        long? SelectedSeason,
        long? ShownSeason,
        IReadOnlyList<EpisodeCard> Episodes,
        bool EpisodesLoading,
        string? PlayEpisodeId,
        IReadOnlyList<MediaCard> Similar,
        bool SimilarLoaded,
        int? CastIndex,
        string? SimilarId);

    private void NavigateWithReturn(Type page, object parameter)
    {
        if (_detail is not { } detail)
        {
            return;
        }

        var saved = new DetailReturnEntry(detail, _tabs.Select(tab => tab.Season).OfType<SeasonChoice>().ToList(),
            _selectedTab?.Season?.Number, _shownSeason, Episodes.Items.OfType<EpisodeView>().Select(view => view.Episode).ToList(),
            EpisodesLoading.IsActive, _playEpisode?.ContentId, _similarCards, _similarLoaded, _returnCastIndex, _returnSimilarId);
        var frame = Frame;
        if (frame.Navigate(page, parameter) && frame.BackStack.Count > 0)
        {
            frame.BackStack[frame.BackStack.Count - 1] = new PageStackEntry(typeof(DetailPage), saved, null);
        }
    }

    private void RestoreEntry(DetailReturnEntry saved)
    {
        _detail = saved.Detail;
        _contentId = saved.Detail.ContentId;
        _returnCastIndex = saved.CastIndex;
        _returnSimilarId = saved.SimilarId;
        ShowHero(saved.Detail);
        ShowCast(saved.Detail);
        ShowSupportingSections(saved.Detail);
        _similarLoaded = saved.SimilarLoaded;
        ShowSimilar(saved.Similar);
        if (saved.Detail.PlaysEpisodes)
        {
            _tabs = saved.Detail.IsSeries ? [new SeasonView(null)] : [];
            _tabs.AddRange(saved.Seasons.Select(season => new SeasonView(season)));
            Seasons.ItemsSource = _tabs;
            _selectedTab = _tabs.FirstOrDefault(tab => tab.Season?.Number == saved.SelectedSeason) ?? _tabs.FirstOrDefault();
            _shownSeason = saved.ShownSeason;
            EpisodesSection.Visibility = Visibility.Visible;
            var episodes = saved.Episodes.Select(episode => new EpisodeView(episode, episode.ContentId == saved.PlayEpisodeId)).ToList();
            Episodes.ItemsSource = episodes;
            var current = episodes.FirstOrDefault(view => view.Episode.ContentId == saved.PlayEpisodeId);
            _episodesRail.Remembered = current;
            SetPlayEpisode(current?.Episode);
            UpdateTabs();
            ShowEditorial(saved.Detail, _selectedTab?.Season is null ? null : _playEpisode);
            SetEpisodesLoading(false);
            // Cast/related navigation is available while seasons load. Resume an unfinished rail, not an empty snapshot.
            if (saved.EpisodesLoading && Session.Client is { } client)
            {
                var entry = saved.SelectedSeason is { } number
                    ? new SeriesDetailEntry(saved.Detail.SeriesId ?? saved.Detail.ContentId, number, saved.PlayEpisodeId)
                    : null;
                _ = LoadSeasonsAsync(client.Detail, saved.Detail, entry);
            }
        }

        LoadingRing.Visibility = Visibility.Collapsed;
        if (!_similarLoaded) _ = LoadSimilarAsync(saved.Detail);
        // Wait until Windows has made its first-focus pick; deliberate input can revoke this return meanwhile.
        var generation = _focusGeneration;
        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
        {
            if (_active && generation == _focusGeneration) RestoreFocus();
        });
    }

    private void RestoreSectionFocus()
    {
        var generation = ++_focusGeneration;
        ListView? list = null;
        Rail? rail = null;
        object? target = null;
        if (_returnCastIndex is { } index && Cast.Items.Count > 0)
        {
            list = Cast;
            rail = _castRail;
            target = Cast.Items[Math.Clamp(index, 0, Cast.Items.Count - 1)];
        }
        else if (_returnSimilarId is { } id)
        {
            list = Similar;
            rail = _similarRail;
            target = Similar.Items.OfType<CardView>().FirstOrDefault(view => view.Card.ContentId == id);
        }

        if (list is not null && rail is not null && target is not null)
        {
            rail.Remembered = target;
            list.ScrollIntoView(target);
            var attempts = 60;
            FocusRetry.Until(PageContent, () =>
            {
                if (!_active || generation != _focusGeneration) return true;
                if (rail.FocusItem(target)) return true;
                return --attempts <= 0 && PlayButton.Focus(FocusState.Programmatic);
            });
        }
        else
        {
            FocusRetry.Until(PageContent, () => !_active || generation != _focusGeneration
                || (_lastFocused is { IsEnabled: true, Visibility: Visibility.Visible } control && control.Focus(FocusState.Programmatic))
                || PlayButton.Focus(FocusState.Programmatic));
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _active = false;
        _episodeLoadVersion++;
        _sectionLoad?.Cancel();
        base.OnNavigatedFrom(e);
    }
}
