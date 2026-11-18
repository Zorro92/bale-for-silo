namespace Bale.Core.Browse;

/// <summary>
/// catalog/sort/CatalogSort.kt (silo-android shared): the media a sort list is for. A field applies
/// to a scope when the server can order that media by it (Latest Episode Added only orders series).
/// </summary>
public enum CatalogSortScope { Movie, Series, Episode, Audiobook, Ebook, Manga }

public enum CatalogSortRelevanceKind { Any, AllVideo, Only }

/// <summary>
/// CatalogSortRelevance: which fields a sort list offers, as the web client's relevance scope.
/// Any lists every field (cross-library lists such as Favorites/Watchlist), AllVideo only fields
/// that order movies, series and episodes alike (a mixed library with no side picked), Only the
/// fields for one scope (a movie or series library, or one side of a mixed library).
/// </summary>
public readonly record struct CatalogSortRelevance(CatalogSortRelevanceKind Kind, CatalogSortScope Scope = default)
{
    public static CatalogSortRelevance Any { get; } = new(CatalogSortRelevanceKind.Any);
    public static CatalogSortRelevance AllVideo { get; } = new(CatalogSortRelevanceKind.AllVideo);
    public static CatalogSortRelevance Only(CatalogSortScope scope) => new(CatalogSortRelevanceKind.Only, scope);

    /// <summary>Ebook and manga lists name Date Viewed and Plays "Date Read" and "Reads".</summary>
    public bool IsReading => Kind == CatalogSortRelevanceKind.Only && Scope is CatalogSortScope.Ebook or CatalogSortScope.Manga;

    /// <summary>
    /// CatalogSortRelevance.forLibrary: <paramref name="libraryType"/> is the library's type and
    /// <paramref name="mediaScope"/> the movie/series side a mixed library is browsed on, if any.
    /// Bale passes its tab scope ("movie", "series", "audiobook") as the type; a mixed library's
    /// tab side gives the same answer, and no scope (Music) is all-video, as upstream.
    /// </summary>
    public static CatalogSortRelevance ForLibrary(string? libraryType, string? mediaScope = null) =>
        (libraryType?.Trim().ToLowerInvariant() ?? string.Empty) switch
        {
            "movie" or "movies" or "film" => Only(CatalogSortScope.Movie),
            "series" or "show" or "shows" or "tv" => Only(CatalogSortScope.Series),
            "audiobook" or "audiobooks" => Only(CatalogSortScope.Audiobook),
            "manga" => Only(CatalogSortScope.Manga),
            "ebook" or "ebooks" or "book" or "books" or "comic" or "comics" or "reading" => Only(CatalogSortScope.Ebook),
            _ => mediaScope?.Trim().ToLowerInvariant() switch
            {
                "movie" => Only(CatalogSortScope.Movie),
                "series" => Only(CatalogSortScope.Series),
                _ => AllVideo,
            },
        };
}

/// <summary>CatalogSortDirectionStyle: how a field's two directions are named.</summary>
public enum CatalogSortDirectionStyle { Alphabetical, Chronological, Length, Magnitude, Count }

/// <summary>
/// CatalogSortField: the catalog sort fields in the web client's order and with its default
/// directions (silo-server web/src/lib/querySortOptions.ts). <see cref="Wire"/> is the server's
/// sort field; labels are the Android strings_catalog.xml English names.
/// </summary>
public sealed class CatalogSortField
{
    private static readonly CatalogSortScope[] Video = [CatalogSortScope.Movie, CatalogSortScope.Series, CatalogSortScope.Episode];
    private static readonly CatalogSortScope[] Media = [.. Video, CatalogSortScope.Audiobook, CatalogSortScope.Ebook];
    private static readonly CatalogSortScope[] MediaAndManga = [.. Media, CatalogSortScope.Manga];

    private readonly HashSet<CatalogSortScope> _scopes;

    private CatalogSortField(string label, string wire, bool descendingByDefault, bool personalized, CatalogSortScope[] scopes,
        CatalogSortDirectionStyle style, string? ratingSource = null)
    {
        Label = label;
        Wire = wire;
        DescendingByDefault = descendingByDefault;
        Personalized = personalized;
        _scopes = [.. scopes];
        DirectionStyle = style;
        RatingSource = ratingSource;
    }

    public string Label { get; }
    public string Wire { get; }
    public bool DescendingByDefault { get; }
    /// <summary>Ordered by this profile's own watching, so only offered where the server sorts per viewer.</summary>
    public bool Personalized { get; }
    public CatalogSortDirectionStyle DirectionStyle { get; }
    /// <summary>Offered only while the server shows this rating source.</summary>
    public string? RatingSource { get; }

    public static CatalogSortField Title { get; } = new("Title", "title", false, false, MediaAndManga, CatalogSortDirectionStyle.Alphabetical);
    public static CatalogSortField DateAdded { get; } = new("Date Added", "added_at", true, false, MediaAndManga, CatalogSortDirectionStyle.Chronological);
    public static CatalogSortField ReleaseDate { get; } = new("Release Date", "release_date", true, false, MediaAndManga, CatalogSortDirectionStyle.Chronological);
    public static CatalogSortField LatestEpisodeAirDate { get; } = new("Latest Episode Air Date", "last_air_date", true, false,
        [CatalogSortScope.Series, CatalogSortScope.Episode], CatalogSortDirectionStyle.Chronological);
    public static CatalogSortField LatestEpisodeAdded { get; } = new("Latest Episode Added", "latest_episode_added", true, false,
        [CatalogSortScope.Series], CatalogSortDirectionStyle.Chronological);
    public static CatalogSortField Year { get; } = new("Year", "year", true, false, MediaAndManga, CatalogSortDirectionStyle.Chronological);
    public static CatalogSortField ContentRating { get; } = new("Content Rating", "content_rating", false, false, Video, CatalogSortDirectionStyle.Alphabetical);
    public static CatalogSortField Duration { get; } = new("Duration", "runtime", true, false, Media, CatalogSortDirectionStyle.Length);
    public static CatalogSortField ImdbRating { get; } = new("IMDb Rating", "rating_imdb", true, false, Video, CatalogSortDirectionStyle.Magnitude);
    public static CatalogSortField TmdbRating { get; } = new("TMDB Rating", "rating_tmdb", true, false, Video, CatalogSortDirectionStyle.Magnitude);
    public static CatalogSortField RtCriticRating { get; } = new("RT Critic Rating", "rating_rt_critic", true, false, Video,
        CatalogSortDirectionStyle.Magnitude, "rt_critic");
    public static CatalogSortField RtAudienceRating { get; } = new("RT Audience Rating", "rating_rt_audience", true, false, Video,
        CatalogSortDirectionStyle.Magnitude, "rt_audience");
    public static CatalogSortField Resolution { get; } = new("Resolution", "resolution", true, false, Video, CatalogSortDirectionStyle.Magnitude);
    public static CatalogSortField Bitrate { get; } = new("Bitrate", "bitrate", true, false, Media, CatalogSortDirectionStyle.Magnitude);
    public static CatalogSortField Progress { get; } = new("Progress", "progress", true, true, MediaAndManga, CatalogSortDirectionStyle.Magnitude);
    public static CatalogSortField DateViewed { get; } = new("Date Viewed", "date_viewed", true, true, MediaAndManga, CatalogSortDirectionStyle.Chronological);
    public static CatalogSortField Plays { get; } = new("Plays", "plays", true, true, MediaAndManga, CatalogSortDirectionStyle.Count);
    public static CatalogSortField Author { get; } = new("Author", "author", false, false,
        [CatalogSortScope.Audiobook, CatalogSortScope.Ebook, CatalogSortScope.Manga], CatalogSortDirectionStyle.Alphabetical);
    public static CatalogSortField Narrator { get; } = new("Narrator", "narrator", false, false, [CatalogSortScope.Audiobook], CatalogSortDirectionStyle.Alphabetical);
    public static CatalogSortField Series { get; } = new("Series", "series", false, false,
        [CatalogSortScope.Audiobook, CatalogSortScope.Ebook], CatalogSortDirectionStyle.Alphabetical);

    /// <summary>Every field, in display order.</summary>
    public static IReadOnlyList<CatalogSortField> All { get; } =
    [
        Title, DateAdded, ReleaseDate, LatestEpisodeAirDate, LatestEpisodeAdded, Year, ContentRating, Duration,
        ImdbRating, TmdbRating, RtCriticRating, RtAudienceRating, Resolution, Bitrate, Progress, DateViewed, Plays,
        Author, Narrator, Series,
    ];

    /// <summary>CatalogSortText.kt labelRes: ebook and manga lists say "Date Read" and "Reads".</summary>
    public string LabelFor(CatalogSortRelevance relevance) => relevance.IsReading && ReferenceEquals(this, DateViewed) ? "Date Read"
        : relevance.IsReading && ReferenceEquals(this, Plays) ? "Reads"
        : Label;

    /// <summary>CatalogSortText.kt directionLabelRes.</summary>
    public string DirectionLabel(bool descending) => DirectionStyle switch
    {
        CatalogSortDirectionStyle.Alphabetical => descending ? "Z–A" : "A–Z",
        CatalogSortDirectionStyle.Chronological => descending ? "Newest" : "Oldest",
        CatalogSortDirectionStyle.Length => descending ? "Longest" : "Shortest",
        CatalogSortDirectionStyle.Count => descending ? "Most" : "Fewest",
        _ => descending ? "Highest" : "Lowest",
    };

    public bool AppliesTo(CatalogSortRelevance relevance) => relevance.Kind switch
    {
        CatalogSortRelevanceKind.Any => true,
        CatalogSortRelevanceKind.AllVideo => _scopes.IsSupersetOf(Video),
        _ => _scopes.Contains(relevance.Scope),
    };

    /// <summary>The field for a server sort value. Legacy aliases map as the web client maps them.</summary>
    public static CatalogSortField? FromWire(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        var canonical = normalized switch
        {
            "sort_title" => "title",
            "recently_added" => "added_at",
            "rating" => "rating_imdb",
            _ => normalized,
        };
        return All.FirstOrDefault(field => field.Wire == canonical);
    }

    /// <summary>
    /// CatalogSortField.options: the fields one list offers, in display order. A rating sort whose
    /// source the server doesn't show is left out. <paramref name="keep"/>, the field the list is
    /// sorted by now, is always listed, so a restored sort the list wouldn't offer stays visible.
    /// </summary>
    public static IReadOnlyList<CatalogSortField> Options(CatalogSortRelevance relevance, bool includePersonalized,
        IReadOnlySet<string> shownRatingSources, CatalogSortField? keep = null) =>
        All.Where(field => ReferenceEquals(field, keep) || ((includePersonalized || !field.Personalized) && field.AppliesTo(relevance)
            && (field.RatingSource is null || shownRatingSources.Contains(field.RatingSource)))).ToList();
}
