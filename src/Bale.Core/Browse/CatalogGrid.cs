namespace Bale.Core.Browse;

/// <summary>
/// A paged grid's item source, separate from its presentation (no sort/filter UI assumptions).
/// The page retains one source per navigation entry. A source may own mutable query state; changing
/// it must restart the grid, never reuse a continuation from the previous query.
/// </summary>
public interface ICatalogGridSource
{
    string Title { get; }

    /// <summary>
    /// Null continuation starts a query; later calls receive only this source's opaque continuation.
    /// Return poster cards in server order and drain filtered-only pages until a card or the end.
    /// Honor cancellation; the page also fences late responses, so an old query cannot replace a new one.
    /// </summary>
    Task<SearchResultPage> LoadPageAsync(BrowseFeed feed, ArtworkWidths widths, SearchContinuation? continuation,
        CancellationToken cancellationToken = default);
}

/// <summary>SectionBrowseArgs / SectionApi.kt: a Home row, or a row on a particular library page.</summary>
public sealed record SectionBrowseSource(string SectionId, string Title, string? LibraryId = null, string? MediaScope = null) : ICatalogGridSource
{
    public Task<SearchResultPage> LoadPageAsync(BrowseFeed feed, ArtworkWidths widths, SearchContinuation? continuation,
        CancellationToken cancellationToken = default) => feed.SectionPageAsync(this, widths, continuation, cancellationToken);

    // LibraryMediaScope.kt: section sources forbid type overlays; scope is applied after each page.
    internal bool MatchesMediaScope(string? type) => MediaScope switch
    {
        "movie" => type?.Trim().ToLowerInvariant() is "movie" or "movies" or "film",
        "series" => type?.Trim().ToLowerInvariant() is "series" or "show" or "shows" or "tv" or "episode",
        _ => true,
    };
}

public sealed record CatalogGridRequest(int Generation, SearchContinuation? Continuation, bool Append);

/// <summary>SectionBrowseViewModel.kt's retained, deduplicated pages, retry and removed-row state.</summary>
public sealed class CatalogGridResults
{
    private readonly List<MediaCard> _items = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private int _generation;

    public IReadOnlyList<MediaCard> Items => _items;
    public SearchContinuation? Continuation { get; private set; }
    public bool HasMore => Continuation is not null && !Unavailable;
    public bool HasLoaded { get; private set; }
    public bool IsLoading { get; private set; }
    public bool Unavailable { get; private set; }
    public string? Error { get; private set; }
    public string? LoadMoreError { get; private set; }

    public CatalogGridRequest Begin()
    {
        _generation++;
        _items.Clear();
        _ids.Clear();
        Continuation = null;
        HasLoaded = Unavailable = false;
        Error = LoadMoreError = null;
        IsLoading = true;
        return new CatalogGridRequest(_generation, null, false);
    }

    public CatalogGridRequest? BeginMore(bool retry = false)
    {
        if (IsLoading || !HasMore || (LoadMoreError is not null && !retry)) return null;
        IsLoading = true;
        LoadMoreError = null;
        return new CatalogGridRequest(_generation, Continuation, true);
    }

    public bool Apply(CatalogGridRequest request, SearchResultPage page)
    {
        if (request.Generation != _generation) return false;
        var before = _items.Count;
        _items.AddRange(page.Cards.Where(card => _ids.Add(card.ContentId)));
        Continuation = page.Continuation;
        IsLoading = false;
        HasLoaded = true;
        Error = null;
        // TvCatalogGrid.kt pauses automatic paging if a successful page adds nothing; Retry re-arms it.
        LoadMoreError = request.Append && page.HasMore && before == _items.Count ? "Couldn't load more" : null;
        return true;
    }

    public bool Fail(CatalogGridRequest request, bool notFound = false)
    {
        if (request.Generation != _generation) return false;
        IsLoading = false;
        Unavailable = notFound;
        if (notFound) Continuation = null;
        else if (request.Append) LoadMoreError = "Couldn't load more";
        else Error = "Couldn't load this row.";
        return true;
    }

    public void UpdateCard(MediaCard card)
    {
        var index = _items.FindIndex(item => item.ContentId == card.ContentId);
        if (index >= 0) _items[index] = card;
    }

    /// <summary>Navigation cancels transport and fences late responses, but retains pages for Back.</summary>
    public void StopLoading()
    {
        _generation++;
        IsLoading = false;
    }
}

public enum GridDirection { Left, Right, Up, Down }

/// <summary>Exact column steps; an absent cell is never replaced by a sideways neighbour.</summary>
public static class CatalogGridNavigation
{
    public static int Move(int index, int count, int columns, GridDirection direction)
    {
        if (columns <= 0 || index < 0 || index >= count) return index;
        var target = direction switch
        {
            GridDirection.Left when index % columns > 0 => index - 1,
            GridDirection.Right when index % columns < columns - 1 => index + 1,
            GridDirection.Up => index - columns,
            GridDirection.Down => index + columns,
            _ => index,
        };
        return target >= 0 && target < count ? target : index;
    }
}
