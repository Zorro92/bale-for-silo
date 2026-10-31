using System.Globalization;
using Bale.Core.Api.Models;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Bale.Core.Browse;

/// <summary>TvLibrarySortOption: display order, wire names and natural directions.</summary>
public sealed record LibrarySortOption(string Label, string Field, bool DescendingByDefault = true)
{
    public static LibrarySortOption CollectionOrder { get; } = new("Collection Order", string.Empty, false);
    public static LibrarySortOption Title { get; } = new("Title", "title", false);
    public static LibrarySortOption DateAdded { get; } = new("Date Added", "added_at");
    public static IReadOnlyList<LibrarySortOption> Video { get; } =
        [Title, DateAdded, new("Release Date", "release_date"), new("Year", "year"), new("Rating", "rating_imdb"), new("Runtime", "runtime"), new("Resolution", "resolution")];
    public static IReadOnlyList<LibrarySortOption> Audiobooks { get; } =
        [Title, new("Author", "author", false), new("Narrator", "narrator", false), new("Series", "series", false), DateAdded, new("Runtime", "runtime")];

    public static IReadOnlyList<LibrarySortOption> For(string? mediaType, bool collection = false)
    {
        var options = mediaType == "audiobook" ? Audiobooks : Video;
        return collection ? new[] { CollectionOrder }.Concat(options).ToList() : options;
    }

    public string DirectionLabel(bool descending) => Field switch
    {
        "" => "Default",
        "title" or "author" or "narrator" or "series" => descending ? "Z–A" : "A–Z",
        "added_at" or "release_date" or "year" => descending ? "Newest" : "Oldest",
        "runtime" => descending ? "Longest" : "Shortest",
        _ => descending ? "Highest" : "Lowest",
    };
}

public enum LibraryFacet
{
    Genre, Decade, WatchStatus, ContentRating, Resolution, DynamicRange, Studio, Network, Country,
    AudioLanguage, SubtitleLanguage, OriginalLanguage, Author, Narrator, Series,
}

public sealed record LibraryFacetOption(string Value, string Label);

/// <summary>TvLibraryBrowseFacets.kt: vocabulary and dimension order, not arbitrary server fields.</summary>
public static class LibraryFacets
{
    public static IReadOnlyList<LibraryFacet> For(string? mediaType) => mediaType == "audiobook"
        ? [LibraryFacet.Genre, LibraryFacet.Author, LibraryFacet.Narrator, LibraryFacet.Series, LibraryFacet.Decade, LibraryFacet.WatchStatus]
        : [LibraryFacet.Genre, LibraryFacet.Decade, LibraryFacet.WatchStatus, LibraryFacet.ContentRating, LibraryFacet.Resolution,
            LibraryFacet.DynamicRange, LibraryFacet.Studio, LibraryFacet.Network, LibraryFacet.Country,
            LibraryFacet.AudioLanguage, LibraryFacet.SubtitleLanguage, LibraryFacet.OriginalLanguage];

    public static string Title(this LibraryFacet facet) => facet switch
    {
        LibraryFacet.WatchStatus => "Watch Status",
        LibraryFacet.ContentRating => "Content Rating",
        LibraryFacet.DynamicRange => "Dynamic Range",
        LibraryFacet.AudioLanguage => "Audio Language",
        LibraryFacet.SubtitleLanguage => "Subtitle Language",
        LibraryFacet.OriginalLanguage => "Original Language",
        _ => facet.ToString(),
    };

    public static IReadOnlyList<LibraryFacetOption> Options(this LibraryFacet facet, CatalogFilters? options) => facet switch
    {
        LibraryFacet.Decade => new[] { 2020, 2010, 2000, 1990, 1980, 1970, 1960 }
            .Select(year => new LibraryFacetOption(year.ToString(CultureInfo.InvariantCulture), $"{year}s")).ToList(),
        LibraryFacet.DynamicRange => [new("hdr", "HDR"), new("dolby_vision", "Dolby Vision")],
        LibraryFacet.WatchStatus => [new("unwatched", "Unwatched"), new("in_progress", "In Progress"), new("watched", "Watched"), new("favorited", "Favorited"), new("watchlist", "Watchlist")],
        _ => (facet switch
        {
            LibraryFacet.Genre => options?.Genres,
            LibraryFacet.ContentRating => options?.ContentRatings,
            LibraryFacet.Resolution => options?.Technical?.Resolutions,
            LibraryFacet.Studio => options?.Studios,
            LibraryFacet.Network => options?.Networks,
            LibraryFacet.Country => options?.Countries,
            LibraryFacet.AudioLanguage => options?.Technical?.AudioLanguages,
            LibraryFacet.SubtitleLanguage => options?.Technical?.SubtitleLanguages,
            LibraryFacet.OriginalLanguage => options?.OriginalLanguages,
            LibraryFacet.Author => options?.Authors,
            LibraryFacet.Narrator => options?.Narrators,
            LibraryFacet.Series => options?.Series,
            _ => null,
        } ?? []).Select(value => new LibraryFacetOption(value, value)).ToList(),
    };
}

/// <summary>Immutable applied/draft selections. Watch status is single-select; every other facet is OR within its dimension.</summary>
public sealed record LibraryFacetSelection
{
    public bool MatchAll { get; init; } = true;
    public IReadOnlyDictionary<LibraryFacet, IReadOnlyList<string>> Values { get; init; } = new Dictionary<LibraryFacet, IReadOnlyList<string>>();
    public IReadOnlyList<string> Selected(LibraryFacet facet) => Values.TryGetValue(facet, out var values) ? values : [];
    public int ActiveCount => Values.Count(pair => pair.Value.Count > 0);
    public bool CanReset => ActiveCount > 0 || !MatchAll;

    public LibraryFacetSelection Toggle(LibraryFacet facet, string value)
    {
        var values = Selected(facet).ToList();
        if (!values.Remove(value))
        {
            if (facet == LibraryFacet.WatchStatus) values.Clear();
            values.Add(value);
        }

        var next = new Dictionary<LibraryFacet, IReadOnlyList<string>>(Values);
        if (values.Count == 0) next.Remove(facet);
        else next[facet] = values;
        return this with { Values = next };
    }

    public LibraryFacetSelection Clear(LibraryFacet facet) => this with
    {
        Values = Values.Where(pair => pair.Key != facet).ToDictionary(pair => pair.Key, pair => pair.Value),
    };

    /// <summary>Canonical v2 groups use typed scalar/array values (CatalogQueryBuilder), never legacy comma lists.</summary>
    public List<CatalogQueryGroup> ToQueryGroups()
    {
        var groups = new List<CatalogQueryGroup>();
        foreach (var (facet, field, op) in new (LibraryFacet, string, string)[]
        {
            (LibraryFacet.Genre, "genre", "contains"), (LibraryFacet.Studio, "studio", "is"),
            (LibraryFacet.Network, "network", "is"), (LibraryFacet.Country, "country", "is"),
            (LibraryFacet.ContentRating, "content_rating", "is"), (LibraryFacet.Resolution, "resolution", "is"),
            (LibraryFacet.AudioLanguage, "audio_language", "is"), (LibraryFacet.SubtitleLanguage, "subtitle_language", "is"),
            (LibraryFacet.OriginalLanguage, "original_language", "is"), (LibraryFacet.Author, "author", "is"),
            (LibraryFacet.Narrator, "narrator", "is"), (LibraryFacet.Series, "series", "is"),
        })
        {
            Add(Selected(facet).Order(StringComparer.Ordinal).Select(value => Rule(field, op, new UntypedString(value))).ToList());
        }

        Add(Selected(LibraryFacet.Decade).Order(StringComparer.Ordinal).Select(value => int.TryParse(value, CultureInfo.InvariantCulture, out var year)
            ? Rule("year", "between", new UntypedArray([new UntypedString(value), new UntypedString((year + 9).ToString(CultureInfo.InvariantCulture))]))
            : null).OfType<CatalogQueryRule>().ToList());
        Add(Selected(LibraryFacet.DynamicRange).Where(value => value is "hdr" or "dolby_vision")
            .Order(StringComparer.Ordinal).Select(value => Rule(value, "is", new UntypedBoolean(true))).ToList());
        if (Selected(LibraryFacet.WatchStatus).FirstOrDefault() is { } status)
        {
            var field = status switch { "in_progress" => "in_progress", "favorited" => "favorited", "watchlist" => "in_watchlist", _ => "watched" };
            groups.Add(new CatalogQueryGroup { Match = CatalogQueryGroup_match.All, Rules = [Rule(field, "is", new UntypedBoolean(status != "unwatched"))] });
        }

        return groups;

        void Add(List<CatalogQueryRule> rules)
        {
            if (rules.Count > 0) groups.Add(new CatalogQueryGroup { Match = CatalogQueryGroup_match.Any, Rules = rules });
        }
    }

    private static CatalogQueryRule Rule(string field, string op, UntypedNode value) => new() { Field = field, Op = op, Value = value };
}

public sealed record LibraryBrowseFilter
{
    public LibrarySortOption Sort { get; init; } = LibrarySortOption.DateAdded;
    public bool Descending { get; init; } = true;
    public string? NamePrefix { get; init; }
    public LibraryFacetSelection Facets { get; init; } = new();
    public bool ShowsAlphabet => Sort.Field == "title";
    public bool IsNarrowed => NamePrefix is not null || Facets.ActiveCount > 0;

    // Re-selecting the active sort flips direction, without losing an A–Z prefix.
    public LibraryBrowseFilter SelectSort(LibrarySortOption sort) => this with
    {
        Sort = sort,
        Descending = sort.Field.Length > 0 && (Sort == sort ? !Descending : sort.DescendingByDefault),
    };

    public LibraryBrowseFilter SelectPrefix(string? prefix) => this with
    {
        NamePrefix = prefix is null or "All" or "" ? null : prefix == "#" ? "0-9" : prefix.ToUpperInvariant(),
    };

    // Applying the facet panel clears the per-visit prefix, as on Android TV.
    public LibraryBrowseFilter ApplyFacets(LibraryFacetSelection facets) => this with { Facets = facets, NamePrefix = null };
}

public sealed record LibraryBrowseQuery(string LibraryId, string? MediaType, LibraryBrowseFilter Filter, string? CollectionId = null, bool UserCollection = false);

/// <summary>A library cursor belongs to exactly the query and artwork widths that produced it.</summary>
public sealed class LibraryBrowseContinuation
{
    internal LibraryBrowseContinuation(string cursor, string key, IEnumerable<string> seen)
    {
        Cursor = cursor;
        Key = key;
        Seen = new HashSet<string>(seen, StringComparer.Ordinal);
    }

    public string Cursor { get; }
    internal string Key { get; }
    internal IReadOnlyCollection<string> Seen { get; }
}

public sealed record LibraryBrowseResult(IReadOnlyList<MediaCard> Cards, LibraryBrowseContinuation? Continuation, bool? LibraryHasItems = null)
{
    public bool HasMore => Continuation is not null;
}

/// <summary>Grid moves are column-preserving and never wrap a row, including a short final row.</summary>
public static class LibraryGridNavigation
{
    public const int Columns = 6;
    public const int LoadMoreRows = 8;
    public static bool IsNearEnd(int lastVisible, int count) => count > 0 && lastVisible >= Math.Max(0, count - Columns * LoadMoreRows);

    public static int Move(int index, int count, int horizontal, int vertical)
    {
        if (index < 0 || index >= count) return index;
        var target = index + horizontal + vertical * Columns;
        if (target < 0 || target >= count || (horizontal != 0 && target / Columns != index / Columns)) return index;
        return target;
    }
}
