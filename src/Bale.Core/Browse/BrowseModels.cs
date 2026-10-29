namespace Bale.Core.Browse;

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
/// <param name="RemainingMinutes">Minutes left on a resumable card.</param>
/// <param name="PlayContentId">What Play starts; the card itself when null.</param>
/// <param name="Badges">Quality badges for the marquee, e.g. "4K", "DOLBY VISION".</param>
/// <param name="EpisodeCode">"S2 E7" on episode cards.</param>
/// <param name="EpisodeTitle">The episode's own title on episode cards.</param>
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
    int? RemainingMinutes,
    string? PlayContentId,
    string? SeriesId,
    IReadOnlyList<string> Badges,
    string? EpisodeCode,
    string? EpisodeTitle)
{
    /// <summary>
    /// The marquee meta line from the Skyline guide: badges, then "S2 E7 · Title · 23m left" for
    /// episodes or year · rating · runtime · genres otherwise.
    /// </summary>
    public string MarqueeMeta
    {
        get
        {
            var parts = new List<string>(Badges);
            if (EpisodeCode is not null)
            {
                parts.Add(EpisodeCode);
                if (!string.IsNullOrEmpty(EpisodeTitle))
                {
                    parts.Add(EpisodeTitle!);
                }
            }
            else if (Meta is not null)
            {
                parts.Add(Meta);
            }

            if (RemainingMinutes is > 0)
            {
                parts.Add($"{RemainingMinutes}m left");
            }

            return string.Join(" · ", parts);
        }
    }
}

public sealed record MediaRow(string Id, string Title, string SectionType, bool Featured, CardShape Shape, IReadOnlyList<MediaCard> Cards);

/// <summary>Device-pixel widths the TV draws each kind of art at, so the server sends the right size.</summary>
public sealed record ArtworkWidths(int Poster, int Backdrop, int Logo, int Still)
{
    /// <summary>
    /// A 1080p TV (2 device pixels per layout pixel), using Silo's TV sizes: 88-wide Home posters,
    /// 186-wide Continue Watching thumbs, a 440-wide marquee logo and a full-width backdrop.
    /// </summary>
    public static ArtworkWidths Tv1080 { get; } = new(Poster: 208, Backdrop: 1920, Logo: 880, Still: 372);
}
