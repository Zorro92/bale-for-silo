namespace Bale.Core.Home;

public enum CardShape
{
    /// <summary>2:3 poster art: movies and series.</summary>
    Poster,

    /// <summary>16:9 art with a progress bar: Continue Watching and Next Up.</summary>
    Landscape,
}

/// <summary>One card on a home row, already resolved to what the TV draws.</summary>
/// <param name="Title">Main line: the movie or series title.</param>
/// <param name="Subtitle">Second line, e.g. "S2 · E5 · The Rains" or the year.</param>
/// <param name="ImageUrl">Art for the card in its row's shape.</param>
/// <param name="BackdropUrl">Wide art for the banner above the rows.</param>
/// <param name="Meta">Banner detail line, e.g. "2021 · PG-13 · 2h 35m · Sci-Fi".</param>
/// <param name="Progress">0..1 watched, for resumable cards.</param>
/// <param name="PlayContentId">What Play starts; the card itself when null.</param>
public sealed record MediaCard(
    string ContentId,
    string Type,
    string Title,
    string? Subtitle,
    Uri? ImageUrl,
    Uri? BackdropUrl,
    Uri? LogoUrl,
    string? Overview,
    string? Meta,
    double? Progress,
    string? PlayContentId,
    string? SeriesId);

public sealed record HomeRow(string Id, string Title, string SectionType, bool Featured, CardShape Shape, IReadOnlyList<MediaCard> Cards);

/// <summary>Device-pixel widths the TV draws each kind of art at, so the server sends the right size.</summary>
public sealed record ArtworkWidths(int Poster, int Backdrop, int Logo, int Still)
{
    /// <summary>Sizes for a 1080p TV: ~220 px posters, full-width backdrops.</summary>
    public static ArtworkWidths Tv1080 { get; } = new(Poster: 300, Backdrop: 1920, Logo: 600, Still: 480);
}
