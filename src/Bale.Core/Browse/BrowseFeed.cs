using System.Globalization;
using Bale.Core.Api;
using Bale.Core.Api.Models;

namespace Bale.Core.Browse;

/// <summary>
/// Rows for Home and library landings, and search results. Row order and contents are the
/// server's (and the profile's customizations); Bale never invents its own rows.
/// </summary>
public sealed class BrowseFeed
{
    /// <summary>Cards per row the TV asks for; rows can show "see all" past this later.</summary>
    public const int CardsPerRow = 24;

    private readonly SiloApiClient _api;
    private readonly Uri _server;
    private HomeCapabilities? _capabilities;

    public BrowseFeed(SiloApiClient api, Uri server)
    {
        _api = api;
        _server = server;
    }

    /// <summary>Home's rows.</summary>
    public async Task<IReadOnlyList<MediaRow>> LoadHomeAsync(ArtworkWidths widths, CancellationToken cancellationToken = default)
    {
        // item_limit is only safe to send when the server advertises it.
        _capabilities ??= await _api.Api.V2.Home.Capabilities.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var limitMax = _capabilities?.SectionItemLimitMax;

        var response = await _api.Api.V2.Home.Sections.GetAsync(config =>
        {
            config.QueryParameters.PosterWidth = widths.Poster;
            config.QueryParameters.BackdropWidth = widths.Backdrop;
            config.QueryParameters.LogoWidth = widths.Logo;
            config.QueryParameters.StillWidth = widths.Still;
            if (limitMax is > 0)
            {
                config.QueryParameters.ItemLimit = Math.Min(CardsPerRow, limitMax.Value);
            }
        }, cancellationToken).ConfigureAwait(false);

        return ToRows(response?.Sections);
    }

    /// <summary>A library's Browse landing: its server-defined rows.</summary>
    public async Task<IReadOnlyList<MediaRow>> LoadLibraryAsync(string libraryId, ArtworkWidths widths, CancellationToken cancellationToken = default)
    {
        var response = await _api.Api.V2.Library[libraryId].Sections.GetAsync(config =>
        {
            config.QueryParameters.PosterWidth = widths.Poster;
            config.QueryParameters.BackdropWidth = widths.Backdrop;
            config.QueryParameters.LogoWidth = widths.Logo;
            config.QueryParameters.StillWidth = widths.Still;
        }, cancellationToken).ConfigureAwait(false);

        return ToRows(response?.Sections);
    }

    /// <summary>For You: the server's recommendation rows (TvRecommendationsScreen, /recommendations/discover).</summary>
    public async Task<IReadOnlyList<MediaRow>> LoadForYouAsync(ArtworkWidths widths, CancellationToken cancellationToken = default)
    {
        var response = await _api.Api.V2.Recommendations.Discover.GetAsync(config =>
        {
            config.QueryParameters.PosterWidth = widths.Poster;
            config.QueryParameters.BackdropWidth = widths.Backdrop;
            config.QueryParameters.LogoWidth = widths.Logo;
            config.QueryParameters.StillWidth = widths.Still;
        }, cancellationToken).ConfigureAwait(false);

        return (response?.Items ?? [])
            .Where(row => row.Items is { Count: > 0 })
            .Select(row => new MediaRow(
                row.Key ?? string.Empty,
                row.Title ?? string.Empty,
                row.Kind ?? string.Empty,
                false,
                CardShape.Poster,
                row.Items!.Take(CardsPerRow).Select(item => ToCard(item, CardShape.Poster)).ToList()))
            .ToList();
    }

    /// <summary>Movies, series and episodes matching <paramref name="query"/>, best match first.</summary>
    /// <summary>
    /// Search as TvSearchViewModel runs it: the catalog's query source, with no type for All and
    /// "movie" or "series" for the filter chips. An episode hit is captioned with its own title and
    /// year, as the official search grid shows it.
    /// </summary>
    public async Task<IReadOnlyList<MediaCard>> SearchAsync(string query, ArtworkWidths widths, string? type = null, int limit = 48, CancellationToken cancellationToken = default)
    {
        var response = await _api.Api.V2.Catalog.GetAsync(config =>
        {
            config.QueryParameters.Q = query;
            config.QueryParameters.Type = type;
            config.QueryParameters.Limit = limit;
            config.QueryParameters.PosterWidth = widths.Poster;
            config.QueryParameters.BackdropWidth = widths.Backdrop;
            config.QueryParameters.LogoWidth = widths.Logo;
        }, cancellationToken).ConfigureAwait(false);

        return (response?.Items ?? [])
            .Select(item => ToCard(item, CardShape.Poster) is var card && item.Type == "episode" && !string.IsNullOrWhiteSpace(item.Title)
                ? card with { Title = item.Title!, Subtitle = item.Year is > 0 ? item.Year.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null }
                : card)
            .ToList();
    }

    /// <summary>The libraries this profile can see, grouped into the top bar's content-type tabs.</summary>
    public async Task<IReadOnlyList<LibraryTab>> LoadLibraryTabsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _api.Api.V2.User.Libraries.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return LibraryTabs.From(response?.Items ?? []);
    }

    private IReadOnlyList<MediaRow> ToRows(IEnumerable<Section>? sections) =>
        (sections ?? [])
            .Where(section => section.Items is { Count: > 0 })
            .Select(ToRow)
            .ToList();

    internal MediaRow ToRow(Section section)
    {
        // Servers without section_item_limit_max send whole sections; long rows cost memory on a console.
        var items = section.Items!.Take(CardsPerRow).ToList();
        var shape = IsProgressRow(section, items) ? CardShape.Landscape : CardShape.Poster;
        return new MediaRow(
            section.Id ?? string.Empty,
            section.Title ?? string.Empty,
            section.SectionType ?? string.Empty,
            section.Featured == true,
            shape,
            items.Select(item => ToCard(item, shape)).ToList());
    }

    /// <summary>Continue Watching / Next Up: the server marks their cards with item_source.</summary>
    internal static bool IsProgressRow(Section section, IReadOnlyCollection<CatalogItem> items) =>
        items.Any(item => !string.IsNullOrEmpty(item.ItemSource))
        || (section.SectionType ?? string.Empty).IndexOf("continue", StringComparison.OrdinalIgnoreCase) >= 0
        || (section.SectionType ?? string.Empty).IndexOf("next_up", StringComparison.OrdinalIgnoreCase) >= 0;

    internal MediaCard ToCard(CatalogItem item, CardShape shape)
    {
        var isEpisode = item.Type == "episode";
        var title = isEpisode && !string.IsNullOrEmpty(item.SeriesTitle) ? item.SeriesTitle! : item.Title ?? string.Empty;

        // Landscape cards prefer the episode still; the banner prefers series art over a still.
        var image = shape == CardShape.Landscape
            ? Resolve(item.BackdropUrl) ?? Resolve(item.SeriesBackdropUrl) ?? Resolve(item.PosterUrl)
            : Resolve(isEpisode ? item.SeriesPosterUrl ?? item.PosterUrl : item.PosterUrl);
        var backdrop = Resolve(isEpisode ? item.SeriesBackdropUrl ?? item.BackdropUrl : item.BackdropUrl);

        return new MediaCard(
            item.ContentId ?? string.Empty,
            item.Type ?? string.Empty,
            title,
            Subtitle(item, isEpisode),
            image,
            backdrop,
            Resolve(item.LogoUrl),
            item.Overview,
            Meta(item),
            Progress(item),
            RemainingMinutes(item),
            item.PlayContentId,
            item.SeriesId,
            Badges(item.OverlaySummary, item.ContentRating),
            item.Type == "episode" && item.SeasonNumber is { } season && item.EpisodeNumber is { } episode ? $"S{season} E{episode}" : null,
            item.Type == "episode" ? item.Title : null,
            Runtime(item.Runtime))
        {
            ContentRating = string.IsNullOrWhiteSpace(item.ContentRating) ? null : item.ContentRating!.Trim().ToUpperInvariant(),
            MarqueeFacts = MarqueeFacts(item, isEpisode),
            Rating = isEpisode ? null : Detail.DetailFeed.OutOfTen("imdb", "IMDb", item.RatingImdb) ?? Detail.DetailFeed.OutOfTen("tmdb", "TMDB", item.RatingTmdb),
            Genre = isEpisode ? null : (item.Genres ?? []).FirstOrDefault(genre => !string.IsNullOrWhiteSpace(genre)),
            SpecBadges = SpecBadges(item.OverlaySummary),
            CornerTop = new[] { PrettyResolution(item.OverlaySummary?.Resolution), Upper(item.OverlaySummary?.Hdr), Upper(item.OverlaySummary?.Audio) }.OfType<string>().ToList(),
            CornerBottom = new[] { Upper(item.OverlaySummary?.ReleaseType) }.OfType<string>().ToList(),
        };
    }

    private static IReadOnlyList<string> MarqueeFacts(CatalogItem item, bool isEpisode)
    {
        var facts = new List<string>();
        if (isEpisode)
        {
            if (EpisodeTag(item.SeasonNumber, item.EpisodeNumber) is { } tag)
            {
                facts.Add(tag);
            }

            if (!string.IsNullOrWhiteSpace(item.Title))
            {
                facts.Add(item.Title!);
            }
        }
        else
        {
            if (item.Year is > 0)
            {
                facts.Add(item.Year.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (Length(item.Runtime, item.DurationSeconds) is { } length)
            {
                facts.Add(length);
            }
        }

        return facts;
    }

    /// <summary>"S01E02" (tvEpisodeTag).</summary>
    internal static string? EpisodeTag(long? season, long? episode) =>
        season is null && episode is null ? null : $"{(season is { } s ? $"S{s:00}" : "")}{(episode is { } e ? $"E{e:00}" : "")}";

    /// <summary>"1h 36m", "2h", "52 min": the runtime, else the file's duration (lengthText).</summary>
    internal static string? Length(long? runtimeMinutes, double? durationSeconds)
    {
        var minutes = runtimeMinutes is > 0 ? runtimeMinutes.Value
            : durationSeconds is > 0 && double.IsFinite(durationSeconds.Value) ? (long)Math.Round(durationSeconds.Value / 60) : 0;
        return minutes switch
        {
            <= 0 => null,
            < 60 => $"{minutes} min",
            _ when minutes % 60 == 0 => $"{minutes / 60}h",
            _ => $"{minutes / 60}h {minutes % 60}m",
        };
    }

    /// <summary>"4K · DOLBY VISION · EAC3 5.1" as separate, uppercased badges (TvMarqueeContent.specLine).</summary>
    internal static IReadOnlyList<string> SpecBadges(CatalogItemOverlay? overlay)
    {
        var badges = new List<string>();
        if (overlay is null)
        {
            return badges;
        }

        if (PrettyResolution(overlay.Resolution) is { } resolution)
        {
            badges.Add(resolution);
        }

        if (overlay.Hdr?.Trim() is { Length: > 0 } hdr)
        {
            badges.Add(hdr.Contains("dv", StringComparison.OrdinalIgnoreCase) || hdr.Contains("dolby", StringComparison.OrdinalIgnoreCase) ? "DOLBY VISION" : hdr.ToUpperInvariant());
        }

        if (overlay.Audio?.Trim() is { Length: > 0 } audio)
        {
            var codec = audio.Contains("atmos", StringComparison.OrdinalIgnoreCase) ? "ATMOS" : audio.ToUpperInvariant();
            badges.Add(overlay.AudioChannels?.Trim() is { Length: > 0 } channels ? $"{codec} {channels.ToUpperInvariant()}" : codec);
        }

        return badges;
    }

    /// <summary>"4K" for 2160p, "8K" for 4320p, "1080P" otherwise (OverlayRegistry.prettyResolution, uppercased by the preset).</summary>
    internal static string? PrettyResolution(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "2160p" or "4k" or "uhd" => "4K",
        "4320p" or "8k" => "8K",
        var other => other.ToUpperInvariant(),
    };

    private static string? Upper(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim().ToUpperInvariant();

    /// <summary>Second caption line, as Silo's Android TV cards show it: the episode's own title, else the year.</summary>
    private static string? Subtitle(CatalogItem item, bool isEpisode)
    {
        if (isEpisode)
        {
            // "S01E01 • Pilot", as the official cards caption an episode.
            var tag = EpisodeTag(item.SeasonNumber, item.EpisodeNumber);
            var title = string.IsNullOrWhiteSpace(item.Title) ? null : item.Title;
            return tag is null ? title : title is null ? tag : $"{tag} • {title}";
        }

        return item.Year is > 0 ? item.Year.Value.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Marquee facts for a movie or series: "2025 · 1h 58m · Drama · Thriller". Rating is a badge instead.</summary>
    internal static string? Meta(CatalogItem item)
    {
        var parts = new List<string>();
        if (item.Year is > 0)
        {
            parts.Add(item.Year.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (Runtime(item.Runtime) is { } runtime)
        {
            parts.Add(runtime);
        }

        parts.AddRange((item.Genres ?? []).Take(2));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>"52 min" under an hour, "1h 58m" over.</summary>
    internal static string? Runtime(long? minutes) => minutes switch
    {
        null or <= 0 => null,
        < 60 => $"{minutes} min",
        _ => $"{minutes / 60}h {minutes % 60}m",
    };

    /// <summary>
    /// The marquee's outlined badges, as Silo's Android TV app shows them: resolution, dynamic range,
    /// audio codec, then the content rating, e.g. "4K · HDR · EAC3 · TV-MA".
    /// </summary>
    internal static IReadOnlyList<string> Badges(CatalogItemOverlay? overlay, string? contentRating = null)
    {
        var badges = new List<string>();
        if (overlay?.Resolution == "2160p")
        {
            badges.Add("4K");
        }

        var hdr = overlay?.Hdr ?? string.Empty;
        if (hdr.Contains("DV", StringComparison.OrdinalIgnoreCase) || hdr.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase))
        {
            badges.Add("DOLBY VISION");
        }
        else if (hdr.Length > 0)
        {
            badges.Add("HDR");
        }

        var audio = overlay?.Audio ?? string.Empty;
        if (audio.Contains("atmos", StringComparison.OrdinalIgnoreCase))
        {
            badges.Add("ATMOS");
        }
        else if (audio.Length > 0)
        {
            badges.Add(audio.ToUpperInvariant());
        }

        if (!string.IsNullOrEmpty(contentRating))
        {
            badges.Add(contentRating!.ToUpperInvariant());
        }

        return badges;
    }

    private static int? RemainingMinutes(CatalogItem item) =>
        item.PositionSeconds is double position and > 0 && item.DurationSeconds is double duration && duration > position
            ? (int)Math.Ceiling((duration - position) / 60)
            : null;

    private static double? Progress(CatalogItem item) =>
        item.PositionSeconds is > 0 && item.DurationSeconds is > 0
            ? Math.Min(1, item.PositionSeconds.Value / item.DurationSeconds.Value)
            : null;

    /// <summary>Turns an artwork URL from the API (presigned, possibly relative) into one an image control can load.</summary>
    public Uri? Resolve(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme.StartsWith("http", StringComparison.Ordinal))
        {
            return absolute;
        }

        var root = new Uri(_server.ToString().TrimEnd('/') + "/");
        return new Uri(root, url!.TrimStart('/'));
    }
}
