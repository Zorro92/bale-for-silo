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
/// <param name="Subtitle">Second caption line: the episode's title, or the year.</param>
/// <param name="ImageUrl">Art for the card in its row's shape.</param>
/// <param name="BackdropUrl">Wide art for the banner above the rows.</param>
/// <param name="Meta">Marquee facts for movies and series, e.g. "2021 · 2h 35m · Sci-Fi".</param>
/// <param name="Progress">0..1 watched, for resumable cards.</param>
/// <param name="RemainingMinutes">Minutes left on a resumable card.</param>
/// <param name="PlayContentId">What Play starts; the card itself when null.</param>
/// <param name="Badges">Outlined marquee badges, e.g. "4K", "HDR", "EAC3", "TV-MA".</param>
/// <param name="EpisodeCode">"S2 E7" on episode cards.</param>
/// <param name="EpisodeTitle">The episode's own title on episode cards.</param>
/// <param name="RuntimeText">"52 min" or "1h 58m".</param>
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
    string? EpisodeTitle,
    string? RuntimeText)
{
    public Detail.ItemFlags Flags { get; init; } = new(false, false, false);

    /// <summary>A card of a row of collections: what it opens. Its content id names no title; never send it to the server.</summary>
    public CollectionTarget? Collection { get; init; }

    public bool IsCollection => Collection is not null;

    public double? PositionSeconds { get; init; }

    /// <summary>
    /// CardWatchStatus.kt: the episodes a started, unfinished series or season has left (user_state
    /// watched_count &gt; 0 and unplayed_count &gt; 0); null otherwise, so new shows are not covered in numbers.
    /// </summary>
    public int? EpisodesLeft { get; init; }

    /// <summary>TvMediaTypeFilters.kt: audiobook covers are square; other TV posters are 2:3.</summary>
    public double PosterAspectRatio => Type.Trim().ToLowerInvariant() is "audiobook" or "audiobooks" ? 1 : 2d / 3;

    /// <summary>The observed server stamp, required to dismiss a Continue Watching card safely.</summary>
    public DateTimeOffset? ProgressUpdatedAt { get; init; }

    /// <summary>Both watched and unwatched clear resume progress (CatalogModels.withWatched).</summary>
    public MediaCard WithPlayed(bool played) => this with
    {
        Flags = Flags with { Played = played },
        PositionSeconds = 0,
        Progress = null,
        RemainingMinutes = null,
    };

    /// <summary>The outlined chip that leads the marquee's facts line: "PG", "TV-MA".</summary>
    public string? ContentRating { get; init; }

    /// <summary>
    /// The marquee facts line after the chip (TvMarqueeContent.from): "S01E02", episode title for an
    /// episode; else year and length. <see cref="Rating"/> and <see cref="Genre"/> follow a non-episode.
    /// </summary>
    public IReadOnlyList<string> MarqueeFacts { get; init; } = [];

    /// <summary>The one rating a marquee shows: IMDb, else TMDB (ExternalRatings.primary).</summary>
    public Detail.DisplayRating? Rating { get; init; }

    /// <summary>The first genre.</summary>
    public string? Genre { get; init; }

    /// <summary>The marquee's format badges under the cast line: "4K", "DOLBY VISION", "EAC3 5.1" (specLine).</summary>
    public IReadOnlyList<string> SpecBadges { get; init; } = [];

    /// <summary>Card overlay badges, top-left stack: resolution, HDR, audio codec (OverlayRegistry defaults).</summary>
    public IReadOnlyList<string> CornerTop { get; init; } = [];

    /// <summary>Card overlay badges, bottom-left: the release type ("BLURAY").</summary>
    public IReadOnlyList<string> CornerBottom { get; init; } = [];

    /// <summary>
    /// The marquee meta line next to the badges: "S2 E8 · Exodus · 52 min · 18m left" for episodes,
    /// "2025 · 1h 58m · Drama" otherwise, with time left on anything in progress.
    /// </summary>
    public string MarqueeMeta
    {
        get
        {
            var parts = new List<string>();
            if (EpisodeCode is not null)
            {
                parts.Add(EpisodeCode);
                if (!string.IsNullOrEmpty(EpisodeTitle))
                {
                    parts.Add(EpisodeTitle!);
                }

                if (RuntimeText is not null)
                {
                    parts.Add(RuntimeText);
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

public sealed record MediaRow(string Id, string Title, string SectionType, bool Featured, CardShape Shape, IReadOnlyList<MediaCard> Cards)
{
    /// <summary>Only server-defined Home/library sections have a full section page; discovery rows do not.</summary>
    public SectionBrowseSource? ExploreSource { get; init; }

    /// <summary>The section's <c>total_count</c>; 0 when absent, which proves nothing.</summary>
    public long TotalCount { get; init; }

    /// <summary>The section's <c>item_limit</c>; 0 when absent.</summary>
    public long ItemLimit { get; init; }

    /// <summary>
    /// SectionRowLimits.kt showsWholeList (Android d1c36e7be, Apple 8655b06b): only a playback row (Continue
    /// Watching, In Progress, Next Up) counts its own list; the server counts other rows after Home's cross-row
    /// deduplication. It must report a total no larger than its cards and hold fewer cards than its limit
    /// (item_limit when positive, capped at 20), because a row filled to its limit was cut there.
    /// </summary>
    public bool ShowsWholeList
    {
        get
        {
            if (SectionType.Trim().ToLowerInvariant() is not ("continue_watching" or "in_progress" or "next_up")) return false;
            var limit = ItemLimit > 0 ? Math.Min(BrowseFeed.CardsPerRow, ItemLimit) : BrowseFeed.CardsPerRow;
            return Cards.Count < limit && TotalCount >= 1 && TotalCount <= Cards.Count;
        }
    }

    // SectionRowLimits.kt offersExploreAll: even a short row can have titles hidden by Home's cross-row
    // deduplication, unless it is a playback row known to hold its whole list.
    // TvSkylineSectionFeed.kt: a row of collections shows every card and has no Explore all.
    public bool OffersExploreAll => ExploreSource is not null && !Featured && !IsCollectionRow && Cards.Count > 0 && !ShowsWholeList;

    /// <summary>A row whose cards are collections, not titles (<c>collection_posters</c>).</summary>
    public bool IsCollectionRow => SectionType == CollectionRows.SectionType;

    /// <summary>TvHomeSectionsEditor.kt: "3 items", or "3 collections" for a row of collections.</summary>
    public string CountText => IsCollectionRow
        ? Cards.Count == 1 ? "1 collection" : $"{Cards.Count} collections"
        : Cards.Count == 1 ? "1 item" : $"{Cards.Count} items";

    /// <summary>TvHomeScreen.kt: only Home's For You row has the header shortcut to the For You tab.</summary>
    public bool OffersSeeAll => ExploreSource is { LibraryId: null } &&
        (SectionType.ToLowerInvariant() is "for_you" or "foryou" or "recommendations"
         || Title.Equals("for you", StringComparison.OrdinalIgnoreCase)
         || Title.Equals("recommended for you", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Device-pixel widths the TV draws each kind of art at, so the server sends the right size.</summary>
public sealed record ArtworkWidths(int Poster, int Backdrop, int Logo, int Still)
{
    /// <summary>
    /// A 1080p TV (2 device pixels per layout pixel), using Silo's TV sizes: 88-wide Home posters,
    /// 186-wide Continue Watching thumbs, a 440-wide marquee logo and a full-width backdrop.
    /// </summary>
    public static ArtworkWidths Tv1080 { get; } = new(Poster: 208, Backdrop: 1920, Logo: 880, Still: 372);
}
