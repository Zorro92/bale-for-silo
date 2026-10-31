using System.Runtime.CompilerServices;

namespace Bale.Core.Browse;

/// <summary>HistoryViewModel.kt: a chronological cursor log, without catalog sort/filter controls.</summary>
public sealed class HistoryCatalogSource : ICatalogGridSource
{
    private sealed record Request(BrowseFeed Feed, ArtworkWidths Widths, IReadOnlyCollection<string> ContentIds);
    private readonly ConditionalWeakTable<SearchContinuation, Request> _continuations = new();

    public string Title => "Watch History";
    public string EmptyMessage => "No watch history yet";

    public async Task<SearchResultPage> LoadPageAsync(BrowseFeed feed, ArtworkWidths widths, SearchContinuation? continuation,
        CancellationToken cancellationToken = default)
    {
        Request? request = null;
        if (continuation is not null && (!_continuations.TryGetValue(continuation, out request)
            || !ReferenceEquals(request.Feed, feed) || request.Widths != widths))
        {
            throw new InvalidOperationException("This continuation belongs to another history request.");
        }

        var seen = new HashSet<string>(request?.ContentIds ?? [], StringComparer.Ordinal);
        var page = await feed.HistoryPageAsync(widths, continuation, seen, cancellationToken).ConfigureAwait(false);
        if (page.Continuation is { } next) _continuations.Add(next, new Request(feed, widths, seen));
        return page;
    }
}
