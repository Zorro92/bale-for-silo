using System.Globalization;
using Bale.Core.Api;
using Bale.Core.Api.Models;

namespace Bale.Core.Home;

/// <summary>
/// The home screen's rows, in the order and with the sections the server (and the profile's
/// customizations) decide. Bale never invents its own rows.
/// </summary>
public sealed class HomeFeed
{
    /// <summary>Cards per row the TV asks for; rows can show "see all" past this later.</summary>
    public const int CardsPerRow = 24;

    private readonly SiloApiClient _api;
    private readonly Uri _server;
    private HomeCapabilities? _capabilities;

    public HomeFeed(SiloApiClient api, Uri server)
    {
        _api = api;
        _server = server;
    }

    public async Task<IReadOnlyList<HomeRow>> LoadAsync(ArtworkWidths widths, CancellationToken cancellationToken = default)
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

        return (response?.Sections ?? [])
            .Where(section => section.Items is { Count: > 0 })
            .Select(ToRow)
            .ToList();
    }

    internal HomeRow ToRow(Section section)
    {
        // Servers without section_item_limit_max send whole sections; long rows cost memory on a console.
        var items = section.Items!.Take(CardsPerRow).ToList();
        var shape = IsProgressRow(section, items) ? CardShape.Landscape : CardShape.Poster;
        return new HomeRow(
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
            item.PlayContentId,
            item.SeriesId);
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
