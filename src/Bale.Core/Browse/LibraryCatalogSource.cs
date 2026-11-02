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
    private LibraryBrowseQuery _query = query;
    private LibraryCatalogSource? _personalControls;
    private LibraryBrowseQuery? _resultQuery;
    private bool? _libraryHasItems;
    private long? _total;

    public string Title { get; } = title;
    public LibraryBrowseQuery Query => _personalControls is { } controls ? _query with { Filter = controls.Query.Filter } : _query;
    public bool? LibraryHasItems => Query == _resultQuery ? _libraryHasItems : null;
    public long? Total => Query == _resultQuery ? _total : null;
    public bool IsPersonal => Query.PersonalList is not null;
    public bool IsCollection => Query.CollectionId is not null;
    public string? ControlsMediaType { get; private init; } = query.MediaType;
    // screens/library/TvLibraryCollectionDetailScreen.kt has Sort/Filter, but no A–Z rail.
    public bool ShowsAlphabet => !IsPersonal && !IsCollection && Query.Filter.ShowsAlphabet;
    // TvPersonalScreens.kt: only inline Favorites narrows its empty copy by Movies / TV Shows.
    public string EmptyMessage => (Query.PersonalList, Query.MediaType) switch
    {
        ("watchlist", _) => "Your watchlist is empty",
        ("favorites", "movie") => "No favorite movies yet",
        ("favorites", "series") => "No favorite TV shows yet",
        _ => "No favorites yet",
    };

    /// <summary>screens/library/TvLibraryCollectionDetailViewModel.kt: source kind and stored collection order.</summary>
    public static LibraryCatalogSource Collection(string libraryId, string? mediaScope, LibraryCollection collection, LibraryBrowseFeed library) =>
        // navigation/TvAppNavigation.kt accepts only movie/series route scopes. Audiobook collections
        // keep their native facet/sort vocabulary and TV-visible covers without adding a wire type scope.
        new(collection.Title, new(mediaScope is "movie" or "series" ? libraryId : null,
            mediaScope is "movie" or "series" ? mediaScope : null, new() { Sort = LibrarySortOption.CollectionOrder },
            collection.Id, collection.UserCollection), library) { ControlsMediaType = mediaScope };

    /// <summary>PersonalListViewModels.kt: cross-library, forty cards, server's stored list order.</summary>
    public static LibraryCatalogSource Personal(string kind, LibraryBrowseFeed library) => kind switch
    {
        "watchlist" => new("Watchlist", new(null, null, new() { Sort = LibrarySortOption.ListOrder }, PersonalList: kind), library),
        "favorites" => new("Favorites", new(null, null, new() { Sort = LibrarySortOption.ListOrder }, PersonalList: kind), library),
        _ => throw new ArgumentException("Unknown personal list.", nameof(kind)),
    };

    /// <summary>
    /// TvPersonalScreens.kt TvFavoritesInline keeps its media kind local, while
    /// rememberPersonalListControls shares sort/facets with the standalone Favorites screen.
    /// </summary>
    public LibraryCatalogSource ForInlineFavorites()
    {
        if (Query.PersonalList != "favorites") throw new InvalidOperationException("Only Favorites has inline media kinds.");
        return new LibraryCatalogSource(Title, Query with { MediaType = "movie" }, library)
        {
            _personalControls = _personalControls ?? this,
        };
    }

    public void SetFavoriteMediaType(string mediaType)
    {
        if (_personalControls is null) throw new InvalidOperationException("Media kinds belong to inline Favorites only.");
        if (mediaType is not ("movie" or "series")) throw new ArgumentException("Unknown favorite media kind.", nameof(mediaType));
        _query = _query with { MediaType = mediaType };
        _resultQuery = null;
    }

    public void SetFilter(LibraryBrowseFilter filter)
    {
        if (_personalControls is { } controls) controls.SetFilter(filter);
        else _query = _query with { Filter = filter };
        _resultQuery = null;
    }

    public Task<CatalogFilters?> FiltersAsync(CancellationToken cancellationToken = default) =>
        library.FiltersAsync(IsCollection ? null : Query.LibraryId, Query.CollectionId, Query.UserCollection, cancellationToken, Query.PersonalList);

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
            _resultQuery = requestedQuery;
            if (continuation is null) _libraryHasItems = page.LibraryHasItems;
            _total = page.Total;
        }
        return new SearchResultPage(page.Cards, page.Total ?? page.Cards.Count, page.Total is not null, next);
    }
}
