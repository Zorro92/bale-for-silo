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

    public void SetFilter(LibraryBrowseFilter filter)
    {
        Query = Query with { Filter = filter };
        LibraryHasItems = null;
    }

    public Task<CatalogFilters?> FiltersAsync(CancellationToken cancellationToken = default) =>
        library.FiltersAsync(Query.LibraryId, Query.CollectionId, Query.UserCollection, cancellationToken);

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

        if (Query == requestedQuery && continuation is null) LibraryHasItems = page.LibraryHasItems;
        return new SearchResultPage(page.Cards, page.Cards.Count, false, next);
    }
}
