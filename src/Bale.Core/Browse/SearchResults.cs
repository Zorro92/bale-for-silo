using Bale.Core.Api.Models;

namespace Bale.Core.Browse;

/// <summary>A search chip, in TvSearchViewModel.kt's fixed media-type order.</summary>
public sealed record SearchMediaFilter(string Label, string? Type)
{
    public static SearchMediaFilter All { get; } = new("All", null);
    public static SearchMediaFilter Movies { get; } = new("Movies", "movie");
    public static SearchMediaFilter Series { get; } = new("Series", "series");
    public static SearchMediaFilter Audiobooks { get; } = new("Audiobooks", "audiobook");

    public static IReadOnlyList<SearchMediaFilter> FromTabs(IReadOnlyList<LibraryTab> tabs)
    {
        // TvSearchViewModel.kt gates both video chips on hasVideo, not on separate movie/TV libraries.
        var filters = new List<SearchMediaFilter> { All };
        if (tabs.Any(tab => tab.Type is LibraryTabType.Movies or LibraryTabType.Series))
        {
            filters.Add(Movies);
            filters.Add(Series);
        }

        // A music/audio library alone must not advertise an audiobook-only query.
        if (tabs.Any(tab => tab.Type == LibraryTabType.Audiobooks))
        {
            filters.Add(Audiobooks);
        }

        return filters;
    }
}

/// <summary>An opaque catalog cursor plus its history, so a looping server cannot spin forever.</summary>
public sealed class SearchContinuation
{
    internal SearchContinuation(string cursor, IEnumerable<string> seen)
    {
        Cursor = cursor;
        Seen = new HashSet<string>(seen, StringComparer.Ordinal);
    }

    public string Cursor { get; }

    internal IReadOnlyCollection<string> Seen { get; }
}

public sealed record SearchResultPage(IReadOnlyList<MediaCard> Cards, long Total, bool TotalExact, SearchContinuation? Continuation)
{
    public bool HasMore => Continuation is not null;
}

public sealed record SearchPerson(string Id, string Name, Uri? PhotoUrl);

/// <summary>A request belongs to one query/filter generation, including load-more requests.</summary>
public sealed record SearchRequest(int Generation, string Query, string? Type, SearchContinuation? Continuation, bool Append);

/// <summary>
/// Search's platform-independent state. TvSearchViewModel.kt keeps old titles during a reset, but
/// rejects pages and people from superseded queries, even if their network calls ignore cancellation.
/// </summary>
public sealed class SearchResults
{
    private readonly List<MediaCard> _items = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private int _resultsGeneration = -1;

    public int Generation { get; private set; }
    public string Query { get; private set; } = string.Empty;
    public SearchMediaFilter Filter { get; private set; } = SearchMediaFilter.All;
    public IReadOnlyList<MediaCard> Items => _items;
    public IReadOnlyList<SearchPerson> People { get; private set; } = [];
    public SearchContinuation? Continuation { get; private set; }
    public bool HasMore => Continuation is not null;
    public long Total { get; private set; }
    public bool TotalExact { get; private set; }
    public bool IsSearching { get; private set; }
    public bool IsLoadingMore { get; private set; }
    public string? Error { get; private set; }

    public string? Status => SearchText.Status(Query, Total, IsSearching, Error,
        Filter.Type is null && HasMore, !TotalExact, People.Count > 0);

    /// <summary>Personal card actions preserve result order, generation and paging.</summary>
    public void UpdateCard(MediaCard card)
    {
        var index = _items.FindIndex(item => item.ContentId == card.ContentId);
        if (index >= 0) _items[index] = card;
    }

    /// <summary>Invalidate immediately, before the debounce, so an old page cannot land mid-keystroke.</summary>
    public void ChangeQuery(string query, SearchMediaFilter filter)
    {
        Generation++;
        Query = query.Trim();
        Filter = filter;
        _resultsGeneration = -1;
        Continuation = null;
        IsSearching = false;
        IsLoadingMore = false;
        Error = null;
        People = [];
        if (Query.Length == 0)
        {
            _items.Clear();
            _ids.Clear();
            Total = 0;
            TotalExact = false;
        }
    }

    public SearchRequest? BeginSearch(string query, SearchMediaFilter filter)
    {
        ChangeQuery(query, filter);
        if (Query.Length == 0)
        {
            return null;
        }

        IsSearching = true;
        return new SearchRequest(Generation, Query, filter.Type, null, false);
    }

    public SearchRequest? BeginMore(bool retry = false)
    {
        if (IsSearching || IsLoadingMore || !HasMore || _resultsGeneration != Generation || (Error is not null && !retry))
        {
            return null;
        }

        IsLoadingMore = true;
        Error = null;
        return new SearchRequest(Generation, Query, Filter.Type, Continuation, true);
    }

    public bool ApplyPage(SearchRequest request, SearchResultPage page)
    {
        if (request.Generation != Generation || (request.Append && _resultsGeneration != Generation))
        {
            return false;
        }

        if (!request.Append)
        {
            _items.Clear();
            _ids.Clear();
        }

        var previousCount = _items.Count;
        _items.AddRange(page.Cards.Where(card => _ids.Add(card.ContentId)));
        _resultsGeneration = Generation;
        Continuation = page.Continuation;
        IsSearching = false;
        IsLoadingMore = false;
        // All can include reading titles hidden on TV: TvSearchViewModel.kt counts rendered titles.
        Total = Filter.Type is null ? _items.Count : page.Total;
        TotalExact = Filter.Type is null ? !page.HasMore : page.TotalExact;
        // TvCatalogGrid.kt latches a page that did not grow the list until an explicit retry.
        Error = request.Append && page.HasMore && _items.Count == previousCount ? "Couldn't load more" : null;
        return true;
    }

    public bool ApplyPeople(int generation, IReadOnlyList<SearchPerson> people)
    {
        if (generation != Generation)
        {
            return false;
        }

        People = people;
        return true;
    }

    public bool Fail(SearchRequest request, Exception error)
    {
        if (request.Generation != Generation)
        {
            return false;
        }

        IsSearching = false;
        IsLoadingMore = false;
        Error = SearchText.ErrorMessage(error);
        return true;
    }

    /// <summary>TvCatalogGrid.kt prefetches within six visible items of the end.</summary>
    public static bool IsNearEnd(int lastVisibleIndex, int count) =>
        count > 0 && lastVisibleIndex >= 0 && lastVisibleIndex >= count - 6;
}

/// <summary>Exact copy and precedence from TvSearchScreen.kt, independent of XAML.</summary>
public static class SearchText
{
    public static string MediaNames(IReadOnlyList<SearchMediaFilter> filters)
    {
        var names = filters.Where(filter => filter.Type is not null).Select(filter => filter.Label.ToLowerInvariant()).ToList();
        return names.Count switch
        {
            0 => "your library",
            1 => names[0],
            2 => string.Join(" and ", names),
            _ => $"{string.Join(", ", names.Take(names.Count - 1))}, and {names[^1]}",
        };
    }

    public static string Prompt(IReadOnlyList<SearchMediaFilter> filters) => $"Search {MediaNames(filters)}";
    public static string Description(IReadOnlyList<SearchMediaFilter> filters) => $"Find {MediaNames(filters)} in one place.";

    public static string? Status(string query, long total, bool isSearching, string? error,
        bool isPartialCount, bool isEstimatedCount = false, bool hasPeople = false) => query.Trim().Length == 0 ? null
        : isSearching ? "Searching…"
        : error is not null ? "Couldn't update results"
        : total == 0 && hasPeople ? "No matching titles"
        : total == 0 ? "No results"
        : isEstimatedCount && !isPartialCount ? $"About {total} results"
        : total == 1 ? "1 result"
        : isPartialCount ? $"{total}+ results"
        : $"{total} results";

    // TvSearchViewModel.kt never displays a raw network exception (it can contain URLs/tokens).
    public static string ErrorMessage(Exception error) => error switch
    {
        Problem { Detail: { Length: > 0 } detail } => detail,
        Problem { Title: { Length: > 0 } title } => title,
        HttpRequestException => "Unable to connect to the server.",
        OperationCanceledException or TimeoutException => "The request timed out. Try again.",
        InvalidDataException => "The server returned invalid pagination. Try searching again.",
        _ => "Search failed",
    };
}
