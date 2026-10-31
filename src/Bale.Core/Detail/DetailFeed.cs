using System.Globalization;
using Bale.Core.Api;
using Bale.Core.Api.Models;
using Bale.Core.Browse;

namespace Bale.Core.Detail;

/// <summary>
/// Loads a title's detail page and its seasons and episodes, and changes the viewer's flags on it.
/// Wording and ordering follow Silo's Android TV app (TvDetailMetadata, TvItemDetailScreen).
/// </summary>
public sealed partial class DetailFeed
{
    /// <summary>Device-pixel widths for a 1080p TV: the detail backdrop box, the 325-wide logo, 100 dp cast photos, 180 dp stills.</summary>
    private const int BackdropWidth = 1920;
    private const int LogoWidth = 650;
    private const int PhotoWidth = 200;
    private const int StillWidth = 360;

    private readonly SiloApiClient _api;
    private readonly BrowseFeed _browse;

    public DetailFeed(SiloApiClient api, BrowseFeed browse)
    {
        _api = api;
        _browse = browse;
    }

    /// <summary>How long a preloaded or opened detail is reused before it's fetched again.</summary>
    private static readonly TimeSpan CacheLife = TimeSpan.FromMinutes(5);
    private const int CacheSize = 40;

    private readonly Dictionary<string, (DateTime At, Task<ItemDetail> Detail)> _cache = new();
    private readonly object _cacheLock = new();

    /// <summary>
    /// A title's detail, from the cache when it was preloaded or opened in the last few minutes, as
    /// the official TV feed does for the cards around focus (TvSkylineSectionFeed's fetchDetail), so
    /// the marquee's cast line and the detail page appear at once.
    /// </summary>
    public Task<ItemDetail> GetAsync(string contentId)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(contentId, out var cached) && DateTime.UtcNow - cached.At < CacheLife && !cached.Detail.IsFaulted && !cached.Detail.IsCanceled)
            {
                return cached.Detail;
            }

            if (_cache.Count >= CacheSize)
            {
                var oldest = _cache.OrderBy(entry => entry.Value.At).First().Key;
                _cache.Remove(oldest);
            }

            var load = LoadAsync(contentId);
            _cache[contentId] = (DateTime.UtcNow, load);
            return load;
        }
    }

    /// <summary>Start loading a detail the user is likely to open; failures are left for the real open to retry.</summary>
    public void Preload(string contentId) => _ = GetAsync(contentId).ContinueWith(task => _ = task.Exception, TaskScheduler.Default);

    /// <summary>Drop a cached detail after the viewer changed it (watchlist, favorite, watched).</summary>
    public void Forget(string contentId)
    {
        lock (_cacheLock)
        {
            _cache.Remove(contentId);
        }
    }

    public async Task<ItemDetail> LoadAsync(string contentId, CancellationToken cancellationToken = default)
    {
        var item = await _api.Api.V2.Catalog.Items[contentId].GetAsync(config =>
        {
            config.QueryParameters.BackdropWidth = BackdropWidth;
            config.QueryParameters.LogoWidth = LogoWidth;
            config.QueryParameters.PhotoWidth = PhotoWidth;
            config.QueryParameters.StillWidth = StillWidth;
        }, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The server returned no detail for {contentId}.");
        return ToDetail(item);
    }

    /// <summary>A series' seasons, specials last like the official apps' season picker.</summary>
    public async Task<IReadOnlyList<SeasonChoice>> SeasonsAsync(string seriesId, CancellationToken cancellationToken = default)
    {
        var seasons = await _api.Api.V2.Catalog.Series[seriesId].Seasons.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return (seasons?.Items ?? [])
            .Where(season => season.SeasonNumber is not null)
            .Select(season => new SeasonChoice(
                season.SeasonNumber!.Value,
                season.SeasonNumber == 0 ? "Specials" : $"Season {season.SeasonNumber}",
                season.EpisodeCount ?? 0)
            {
                ContentId = season.ContentId ?? string.Empty,
                Played = season.UserData?.Played == true,
            })
            .OrderBy(season => season.Number == 0 ? long.MaxValue : season.Number)
            .ToList();
    }

    public async Task<IReadOnlyList<EpisodeCard>> EpisodesAsync(string seriesId, long seasonNumber, CancellationToken cancellationToken = default)
    {
        var episodes = await _api.Api.V2.Catalog.Series[seriesId].Seasons[seasonNumber].Episodes.GetAsync(config =>
        {
            config.QueryParameters.StillWidth = StillWidth;
        }, cancellationToken).ConfigureAwait(false);
        return (episodes?.Items ?? []).Select(episode => ToEpisode(episode, seasonNumber)).ToList();
    }

    /// <summary>TvItemDetailViewModel loads twelve ranked cards independently of the hero, excluding the source and TV-hidden types.</summary>
    public async Task<IReadOnlyList<MediaCard>> SimilarAsync(string contentId, CancellationToken cancellationToken = default)
    {
        var response = await _api.Api.V2.Recommendations.Similar[contentId].GetAsync(config =>
        {
            config.QueryParameters.Limit = 12;
            // TvArtworkLayout's SIMILAR surface: 130dp posters at 1080p's 2x density.
            config.QueryParameters.PosterWidth = 260;
        }, cancellationToken).ConfigureAwait(false);
        return (response?.Items ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.ContentId) && item.ContentId != contentId && BrowseFeed.IsVisibleTvType(item.Type, showAudiobooks: true))
            .DistinctBy(item => item.ContentId)
            .Select(item => _browse.ToCard(item, CardShape.Poster)).ToList();
    }

    public Task SetWatchlistAsync(string contentId, bool on, CancellationToken cancellationToken = default) =>
        on ? _api.Api.V2.Watchlist[contentId].PutAsync(cancellationToken: cancellationToken)
           : _api.Api.V2.Watchlist[contentId].DeleteAsync(cancellationToken: cancellationToken);

    public Task SetFavoriteAsync(string contentId, bool on, CancellationToken cancellationToken = default) =>
        on ? _api.Api.V2.Favorites[contentId].PutAsync(cancellationToken: cancellationToken)
           : _api.Api.V2.Favorites[contentId].DeleteAsync(cancellationToken: cancellationToken);

    /// <summary>The generated watched/{id} endpoint expands season and series IDs to their episodes.</summary>
    public Task SetPlayedAsync(string contentId, bool on, CancellationToken cancellationToken = default) =>
        on ? _api.Api.V2.Watched[contentId].PostAsync(cancellationToken: cancellationToken)
           : _api.Api.V2.Watched[contentId].DeleteAsync(cancellationToken: cancellationToken);

    internal ItemDetail ToDetail(CatalogItemDetail item)
    {
        var type = item.Type ?? string.Empty;
        var isEpisode = type == "episode";
        var isSeries = type == "series";

        return new ItemDetail(
            item.ContentId ?? string.Empty,
            type,
            isEpisode && !string.IsNullOrEmpty(item.SeriesTitle) ? item.SeriesTitle! : item.Title ?? string.Empty,
            isEpisode ? item.Title : null,
            _browse.Resolve(item.LogoUrl),
            _browse.Resolve(isEpisode ? item.SeriesBackdropUrl ?? item.BackdropUrl : item.BackdropUrl),
            string.IsNullOrWhiteSpace(item.Tagline) ? null : item.Tagline,
            item.Overview,
            SourceTokens(item),
            string.IsNullOrWhiteSpace(item.ContentRating) ? null : item.ContentRating!.Trim(),
            Facts(item),
            QualityBadges(item.OverlaySummary),
            isSeries ? StarringCredit(item) : DirectorCredit(item),
            (item.Cast ?? [])
                .Where(credit => !string.IsNullOrWhiteSpace(credit.Name))
                .Select(credit => new CastMember(credit.Name!.Trim(), FirstRole(credit.Character), _browse.Resolve(credit.PhotoUrl)) { PersonId = credit.PersonId?.Trim() })
                .ToList(),
            isSeries ? item.ContentId : item.SeriesId,
            item.PlayContentId ?? item.ContentId ?? string.Empty,
            item.PlaySeasonNumber,
            // TvItemDetailScreen.HeroActionRow takes a container's resume only for leaf items.
            isSeries || type == "season" ? null : PlayLabel.ResumePosition(item.UserData?.PositionSeconds, item.UserData?.DurationSeconds),
            new ItemFlags(
                item.UserState?.InWatchlist == true,
                item.UserState?.IsFavorite == true,
                item.UserState?.Played == true),
            Ratings(item))
        {
            MarqueeLine = MarqueeLine(item),
            SeasonNumber = item.SeasonNumber,
            Trailers = Trailers(item),
            Details = DetailFacts(item),
        };
    }

    /// <summary>"Aired Mar 12, 2026 · A, B, C": the air date and the first three cast by billing order.</summary>
    internal static string? MarqueeLine(CatalogItemDetail item)
    {
        var parts = new List<string>();
        if (item.AirDate is { } aired)
        {
            parts.Add("Aired " + new DateOnly(aired.Year, aired.Month, aired.Day).ToString("MMM d, yyyy", CultureInfo.GetCultureInfo("en-US")));
        }

        var cast = (item.Cast ?? [])
            .OrderBy(credit => credit.Order ?? long.MaxValue)
            .Select(credit => credit.Name?.Trim())
            .Where(name => !string.IsNullOrEmpty(name))
            .Take(3)
            .ToList();
        if (cast.Count > 0)
        {
            parts.Add(string.Join(", ", cast));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// The server's chosen, formatted ratings when it sends them; from an older server, IMDb then
    /// TMDB from the raw scores, never Rotten Tomatoes (ExternalRatings.forTitle).
    /// </summary>
    internal static IReadOnlyList<DisplayRating> Ratings(CatalogItemDetail item)
    {
        if (item.Ratings is { } ratings)
        {
            return ratings
                .Where(rating => !string.IsNullOrWhiteSpace(rating.Name) && !string.IsNullOrWhiteSpace(rating.Display))
                .Select(rating => new DisplayRating(rating.Source ?? string.Empty, rating.Name!, rating.Display!))
                .ToList();
        }

        return new[] { OutOfTen("imdb", "IMDb", item.RatingImdb), OutOfTen("tmdb", "TMDB", item.RatingTmdb) }.OfType<DisplayRating>().ToList();
    }

    internal static DisplayRating? OutOfTen(string source, string name, double? value) =>
        value is > 0 and <= 10 && double.IsFinite(value.Value)
            ? new DisplayRating(source, name, (Math.Round(value.Value * 10, MidpointRounding.AwayFromZero) / 10).ToString("0.0", CultureInfo.InvariantCulture))
            : null;

    /// <summary>"TV Show · Comedy · Animation" (TvDetailMetadata.sourceTokens).</summary>
    internal static IReadOnlyList<string> SourceTokens(CatalogItemDetail item)
    {
        var genres = (item.Genres ?? []).Where(genre => !string.IsNullOrWhiteSpace(genre)).Take(2);
        return item.Type switch
        {
            "series" => ["TV Show", .. genres],
            "episode" => new[]
            {
                item.SeasonNumber is { } season ? season == 0 ? "Specials" : $"Season {season}" : null,
                item.EpisodeNumber is > 0 ? $"Episode {item.EpisodeNumber}" : null,
                (item.Genres ?? []).FirstOrDefault(genre => !string.IsNullOrWhiteSpace(genre)),
            }.OfType<string>().ToList(),
            "movie" => ["Movie", .. genres],
            _ => [CultureInfo.InvariantCulture.TextInfo.ToTitleCase(item.Type ?? string.Empty), .. genres],
        };
    }

    /// <summary>"1997 · 29 Seasons" for a series, year and runtime otherwise (TvDetailMetadata.factsLine).</summary>
    internal static IReadOnlyList<string> Facts(CatalogItemDetail item)
    {
        var facts = new List<string>();
        if (item.Year is > 0)
        {
            facts.Add(item.Year.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (item.Type == "series")
        {
            if (item.SeasonCount is > 0 and var count)
            {
                facts.Add($"{count} Season{(count == 1 ? string.Empty : "s")}");
            }
        }
        else if (BrowseFeed.Runtime(item.Runtime) is { } runtime)
        {
            facts.Add(runtime);
        }

        return facts;
    }

    /// <summary>The quality chips after the facts: "4K"/"HD", "HDR"/"DOLBY VISION", "5.1"/"7.1".</summary>
    internal static IReadOnlyList<string> QualityBadges(CatalogItemOverlay? overlay)
    {
        var badges = new List<string>();
        switch (overlay?.Resolution)
        {
            case "2160p":
                badges.Add("4K");
                break;
            case "1080p" or "720p":
                badges.Add("HD");
                break;
        }

        var hdr = overlay?.Hdr ?? string.Empty;
        if (hdr.Contains("DV", StringComparison.OrdinalIgnoreCase))
        {
            badges.Add("DOLBY VISION");
        }
        else if (hdr.Length > 0)
        {
            badges.Add("HDR");
        }

        if (overlay?.AudioChannels is "5.1" or "7.1")
        {
            badges.Add(overlay.AudioChannels);
        }

        return badges;
    }

    /// <summary>Up to three distinct cast names (TvItemDetailScreen.seriesStarringCredit).</summary>
    internal static string? StarringCredit(CatalogItemDetail item)
    {
        var names = (item.Cast ?? [])
            .OrderBy(credit => credit.Order ?? long.MaxValue)
            .Select(credit => credit.Name?.Trim())
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct()
            .Take(3)
            .ToList();
        return names.Count == 0 ? null : "Starring " + string.Join(", ", names);
    }

    internal static string? DirectorCredit(CatalogItemDetail item)
    {
        var directors = (item.Crew ?? [])
            .Where(credit => string.Equals(credit.Job, "Director", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(credit.Name))
            .Select(credit => credit.Name!.Trim())
            .Distinct()
            .Take(2)
            .ToList();
        return directors.Count == 0 ? null : "Directed by " + string.Join(", ", directors);
    }

    /// <summary>Servers list every voice a person plays as "Stan Marsh|Eric Cartman"; show the first.</summary>
    internal static string? FirstRole(string? character) =>
        character?.Split('|').Select(role => role.Trim()).FirstOrDefault(role => role.Length > 0);

    internal EpisodeCard ToEpisode(Episode episode, long seasonNumber)
    {
        var number = episode.EpisodeNumber ?? 0;
        var data = episode.UserData;
        var progress = data?.PositionSeconds is > 0 && data.DurationSeconds is > 0
            ? Math.Min(1, data.PositionSeconds.Value / data.DurationSeconds.Value)
            : (double?)null;

        return new EpisodeCard(
            episode.ContentId ?? string.Empty,
            seasonNumber,
            number,
            episode.Title ?? string.Empty,
            episode.Overview,
            _browse.Resolve(episode.StillUrl),
            episode.AirDate is { } aired ? new DateOnly(aired.Year, aired.Month, aired.Day) : null,
            episode.Runtime is > 0 ? episode.Runtime : null,
            progress,
            data?.Played == true)
        {
            ResumeSeconds = PlayLabel.ResumePosition(data?.PositionSeconds, data?.DurationSeconds),
            IsInProgress = data?.IsInProgress == true,
        };
    }
}
