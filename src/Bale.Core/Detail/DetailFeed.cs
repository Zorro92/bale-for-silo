using System.Globalization;
using Bale.Core.Api;
using Bale.Core.Api.Models;
using Bale.Core.Browse;

namespace Bale.Core.Detail;

/// <summary>
/// Loads a title's detail page and its seasons and episodes, and changes the viewer's flags on it.
/// Wording and ordering follow Silo's Android TV app (TvDetailMetadata, TvItemDetailScreen).
/// </summary>
public sealed class DetailFeed
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
                season.EpisodeCount ?? 0))
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

    public Task SetWatchlistAsync(string contentId, bool on, CancellationToken cancellationToken = default) =>
        on ? _api.Api.V2.Watchlist[contentId].PutAsync(cancellationToken: cancellationToken)
           : _api.Api.V2.Watchlist[contentId].DeleteAsync(cancellationToken: cancellationToken);

    public Task SetFavoriteAsync(string contentId, bool on, CancellationToken cancellationToken = default) =>
        on ? _api.Api.V2.Favorites[contentId].PutAsync(cancellationToken: cancellationToken)
           : _api.Api.V2.Favorites[contentId].DeleteAsync(cancellationToken: cancellationToken);

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
                .Select(credit => new CastMember(credit.Name!.Trim(), FirstRole(credit.Character), _browse.Resolve(credit.PhotoUrl)))
                .ToList(),
            isSeries ? item.ContentId : item.SeriesId,
            item.PlayContentId ?? item.ContentId ?? string.Empty,
            item.PlaySeasonNumber,
            // The detail payload keeps the resume point in user_data; position_seconds is a card field.
            (item.UserData?.PositionSeconds ?? item.PositionSeconds) is > 0 and var resume ? resume : null,
            new ItemFlags(
                item.UserState?.InWatchlist == true,
                item.UserState?.IsFavorite == true,
                item.UserState?.Played == true));
    }

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
        var label = string.Join(" · ", new[]
        {
            number > 0 ? $"EPISODE {number}" : null,
            episode.Runtime is > 0 ? $"{episode.Runtime}m" : null,
        }.OfType<string>());
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
            label,
            progress,
            data?.Played == true);
    }
}
