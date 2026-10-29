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

    /// <summary>Movies, series and episodes matching <paramref name="query"/>, best match first.</summary>
    public async Task<IReadOnlyList<MediaCard>> SearchAsync(string query, ArtworkWidths widths, int limit = 48, CancellationToken cancellationToken = default)
    {
        var response = await _api.Api.V2.Catalog.GetAsync(config =>
        {
            config.QueryParameters.Q = query;
            config.QueryParameters.Type = "video_with_episodes";
            config.QueryParameters.Limit = limit;
            config.QueryParameters.PosterWidth = widths.Poster;
            config.QueryParameters.BackdropWidth = widths.Backdrop;
            config.QueryParameters.LogoWidth = widths.Logo;
        }, cancellationToken).ConfigureAwait(false);

        return (response?.Items ?? []).Select(item => ToCard(item, CardShape.Poster)).ToList();
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
            Badges(item.OverlaySummary),
            item.Type == "episode" && item.SeasonNumber is { } season && item.EpisodeNumber is { } episode ? $"S{season} E{episode}" : null,
            item.Type == "episode" ? item.Title : null);
    }

    private static string? Subtitle(CatalogItem item, bool isEpisode)
    {
        if (isEpisode)
        {
            var code = item.SeasonNumber is { } s && item.EpisodeNumber is { } e ? $"S{s} · E{e}" : null;
            return string.Join(" · ", new[] { code, item.Title }.Where(part => !string.IsNullOrEmpty(part)));
        }

        return item.Year is > 0 ? item.Year.Value.ToString(CultureInfo.InvariantCulture) : null;
    }

    internal static string? Meta(CatalogItem item)
    {
        var parts = new List<string>();
        if (item.Year is > 0)
        {
            parts.Add(item.Year.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(item.ContentRating))
        {
            parts.Add(item.ContentRating!);
        }

        if (item.Runtime is long minutes and > 0)
        {
            parts.Add(minutes >= 60 ? $"{minutes / 60}h {minutes % 60}m" : $"{minutes}m");
        }

        parts.AddRange((item.Genres ?? []).Take(2));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>The marquee's quality badges, Skyline style: "4K · DOLBY VISION · ATMOS".</summary>
    internal static IReadOnlyList<string> Badges(CatalogItemOverlay? overlay)
    {
        var badges = new List<string>();
        if (overlay is null)
        {
            return badges;
        }

        if (overlay.Resolution == "2160p")
        {
            badges.Add("4K");
        }

        var hdr = overlay.Hdr ?? string.Empty;
        if (hdr.Contains("DV", StringComparison.OrdinalIgnoreCase) || hdr.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase))
        {
            badges.Add("DOLBY VISION");
        }
        else if (hdr.Length > 0)
        {
            badges.Add(hdr.ToUpperInvariant());
        }

        if ((overlay.Audio ?? string.Empty).Contains("atmos", StringComparison.OrdinalIgnoreCase))
        {
            badges.Add("ATMOS");
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
