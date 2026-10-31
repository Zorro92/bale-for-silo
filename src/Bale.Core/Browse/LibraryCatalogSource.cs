using System.Runtime.CompilerServices;
using Bale.Core.Api.Models;

namespace Bale.Core.Browse;

/// <summary>
/// TvLibraryDetailScreen.LibraryTab: the same catalog grid as section browsing, with a mutable
/// sort/facet query. The adapter owns its cursor wrappers so unrelated sources cannot reuse them.
/// </summary>
public sealed class LibraryCatalogSource(string title, LibraryBrowseQuery query, LibraryBrowseFeed library) : ICatalogGridSource
{
    private readonly ConditionalWeakTable<SearchContinuation, LibraryBrowseContinuation> _continuations = new();

    public string Title { get; } = title;
    public LibraryBrowseQuery Query { get; private set; } = query;
    public bool? LibraryHasItems { get; private set; }
    public long? Total { get; private set; }
    public bool IsPersonal => Query.PersonalList is not null;
    public bool ShowsAlphabet => !IsPersonal && Query.Filter.ShowsAlphabet;
    public string EmptyMessage => Query.PersonalList == "watchlist" ? "Your watchlist is empty" : "No favorites yet";

    /// <summary>PersonalListViewModels.kt: cross-library, forty cards, server's stored list order.</summary>
    public static LibraryCatalogSource Personal(string kind, LibraryBrowseFeed library) => kind switch
    {
        "watchlist" => new("Watchlist", new(null, null, new() { Sort = LibrarySortOption.ListOrder }, PersonalList: kind), library),
        "favorites" => new("Favorites", new(null, null, new() { Sort = LibrarySortOption.ListOrder }, PersonalList: kind), library),
        _ => throw new ArgumentException("Unknown personal list.", nameof(kind)),
    };

    public void SetFilter(LibraryBrowseFilter filter)
    {
        Query = Query with { Filter = filter };
        LibraryHasItems = null;
        Total = null;
    }

    public Task<CatalogFilters?> FiltersAsync(CancellationToken cancellationToken = default) =>
        library.FiltersAsync(Query.LibraryId, Query.CollectionId, Query.UserCollection, cancellationToken, Query.PersonalList);

    public async Task<SearchResultPage> LoadPageAsync(BrowseFeed feed, ArtworkWidths widths, SearchContinuation? continuation,
        CancellationToken cancellationToken = default)
    {
        LibraryBrowseContinuation? cursor = null;
        if (continuation is not null && !_continuations.TryGetValue(continuation, out cursor))
        {
            throw new InvalidOperationException("This continuation belongs to another catalog source.");
        }

        var requestedQuery = Query;
        var page = await library.LoadAsync(requestedQuery, widths, cursor, cancellationToken).ConfigureAwait(false);
        SearchContinuation? next = null;
        if (page.Continuation is { } more)
        {
            next = new SearchContinuation(more.Cursor, more.Seen);
            _continuations.Add(next, more);
        }

        if (Query == requestedQuery)
        {
            if (continuation is null) LibraryHasItems = page.LibraryHasItems;
            Total = page.Total;
        }
        return new SearchResultPage(page.Cards, page.Total ?? page.Cards.Count, page.Total is not null, next);
    }
}
