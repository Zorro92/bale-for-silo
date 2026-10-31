using Bale.Core.Api;
using Bale.Core.Api.Api.V2.Catalog;
using Bale.Core.Api.Models;

namespace Bale.Core.Browse;

/// <summary>TvLibraryDetailViewModel's bounded catalog grid, facets and grouped collections.</summary>
public sealed class LibraryBrowseFeed(SiloApiClient api, BrowseFeed browse)
{
    public const int PageSize = 100;

    public async Task<LibraryBrowseResult> LoadAsync(LibraryBrowseQuery query, ArtworkWidths widths,
        LibraryBrowseContinuation? continuation = null, CancellationToken cancellationToken = default)
    {
        var filter = query.Filter;
        var key = string.Join('\n', query.LibraryId, query.MediaType, query.CollectionId, query.UserCollection,
            widths, LibraryBrowsePreferences.Serialize(filter), filter.NamePrefix);
        if (continuation is not null && continuation.Key != key)
        {
            throw new InvalidOperationException("The query changed. Reload from the first page.");
        }

        var cursor = continuation?.Cursor;
        var seen = new HashSet<string>(continuation?.Seen ?? [], StringComparer.Ordinal);
        var groups = filter.Facets.ToQueryGroups();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CatalogBrowseCollection? response;
            if (groups.Count > 0)
            {
                // CatalogV2Api.kt uses POST for structured facets; only artwork remains in the URL.
                response = await api.Api.V2.Catalog.Query.PostAsync(new CatalogQuery
                {
                    Source = query.CollectionId is null ? CatalogQuery_source.Query
                        : query.UserCollection ? CatalogQuery_source.User_collection :CatalogQuery_source.Library_collection,
                    LibraryId = query.LibraryId,
                    CollectionId = query.CollectionId,
                    Type = query.MediaType,
                    NamePrefix = filter.NamePrefix,
                    Groups = groups,
                    Match = filter.Facets.MatchAll ? CatalogQuery_match.All : CatalogQuery_match.Any,
                    Sort = filter.Sort.Field.Length == 0 ? null : filter.Sort.Field,
                    Order = filter.Sort.Field.Length == 0 ? null : filter.Descending ? CatalogQuery_order.Desc : CatalogQuery_order.Asc,
                    Limit = PageSize,
                    Cursor = cursor,
                }, config =>
                {
                    config.QueryParameters.PosterWidth = widths.Poster;
                    config.QueryParameters.BackdropWidth = widths.Backdrop;
                    config.QueryParameters.LogoWidth = widths.Logo;
                    config.QueryParameters.StillWidth = widths.Still;
                }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                response = await api.Api.V2.Catalog.GetAsync(config =>
                {
                    config.QueryParameters.Source = query.CollectionId is null ? GetSourceQueryParameterType.Query
                        : query.UserCollection ? GetSourceQueryParameterType.User_collection :GetSourceQueryParameterType.Library_collection;
                    config.QueryParameters.LibraryId = query.LibraryId;
                    config.QueryParameters.CollectionId = query.CollectionId;
                    config.QueryParameters.Type = query.MediaType;
                    config.QueryParameters.NamePrefix = filter.NamePrefix;
                    config.QueryParameters.Sort = filter.Sort.Field.Length == 0 ? null : (filter.Descending ? "-" : string.Empty) + filter.Sort.Field;
                    config.QueryParameters.Cursor = cursor;
                    config.QueryParameters.Limit = PageSize;
                    config.QueryParameters.PosterWidth = widths.Poster;
                    config.QueryParameters.BackdropWidth = widths.Backdrop;
                    config.QueryParameters.LogoWidth = widths.Logo;
                    config.QueryParameters.StillWidth = widths.Still;
                }, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var items = response?.Items ?? [];
            LibraryBrowseContinuation? next = null;
            if (response?.Page?.HasMore == true && items.Count > 0)
            {
                var nextCursor = response.Page.NextCursor;
                if (string.IsNullOrWhiteSpace(nextCursor) || !seen.Add(nextCursor))
                {
                    throw new InvalidDataException("Invalid catalog continuation.");
                }

                next = new LibraryBrowseContinuation(nextCursor, key, seen);
            }

            var cards = items.Where(item => BrowseFeed.IsVisibleTvType(item.Type, query.MediaType == "audiobook"))
                .Select(item => browse.ToCard(item, CardShape.Poster)).ToList();
            if (cards.Count == 0 && next is not null)
            {
                // An all-hidden page cannot trigger its own load-more. Never advance by a visible count.
                cursor = next.Cursor;
                continue;
            }

            bool? libraryHasItems = null;
            if (continuation is null && cards.Count == 0)
            {
                libraryHasItems = items.Count > 0 || (filter.IsNarrowed
                    ? await HasItemsAsync(query, cancellationToken).ConfigureAwait(false)
                    : false);
            }

            return new LibraryBrowseResult(cards, next, libraryHasItems);
        }
    }

    private async Task<bool> HasItemsAsync(LibraryBrowseQuery query, CancellationToken cancellationToken)
    {
        var page = await api.Api.V2.Catalog.GetAsync(config =>
        {
            config.QueryParameters.Source = query.CollectionId is null ? GetSourceQueryParameterType.Query
                : query.UserCollection ? GetSourceQueryParameterType.User_collection :GetSourceQueryParameterType.Library_collection;
            config.QueryParameters.LibraryId = query.LibraryId;
            config.QueryParameters.CollectionId = query.CollectionId;
            config.QueryParameters.Type = query.MediaType;
            config.QueryParameters.Limit = 1;
        }, cancellationToken).ConfigureAwait(false);
        return page?.Items is { Count: > 0 };
    }

    public Task<CatalogFilters?> FiltersAsync(string libraryId, string? collectionId = null, bool userCollection = false,
        CancellationToken cancellationToken = default) => api.Api.V2.Catalog.Filters.GetAsync(config =>
        {
            config.QueryParameters.LibraryId = libraryId;
            config.QueryParameters.CollectionId = collectionId;
            config.QueryParameters.Source = collectionId is null ? null : userCollection
                ? Api.Api.V2.Catalog.Filters.GetSourceQueryParameterType.User_collection
                : Api.Api.V2.Catalog.Filters.GetSourceQueryParameterType.Library_collection;
            config.QueryParameters.SkipTechnical = false;
        }, cancellationToken);

    public async Task<IReadOnlyList<LibraryCollectionSection>> CollectionsAsync(string libraryId, CancellationToken cancellationToken = default)
    {
        var response = await api.Api.V2.Library[libraryId].Collections.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return CollectionSections(response ?? new LibraryCollectionTab());
    }

    /// <summary>TvLibraryDetailViewModel.buildCollectionSections: weave the ungrouped bucket into sort_order.</summary>
    public IReadOnlyList<LibraryCollectionSection> CollectionSections(LibraryCollectionTab response)
    {
        if (response.Groups is not { Count: > 0 } && response.Ungrouped is null)
        {
            var flat = (response.Collections ?? []).Select(item => new LibraryCollection(item.Id ?? string.Empty,
                item.Title ?? string.Empty, browse.Resolve(item.PosterUrl), false)).ToList();
            return flat.Count == 0 ? [] : [new LibraryCollectionSection(string.Empty, flat)];
        }

        var sections = (response.Groups ?? []).Where(group => group.Collections is { Count: > 0 })
            .Select(group => (Order: group.SortOrder ?? 0, Section: new LibraryCollectionSection(group.Name ?? string.Empty,
                group.Collections!.Select(item => Collection(item, group.Kind == "user_collections")).ToList()))).ToList();
        if (response.Ungrouped is { Collections.Count: > 0 } ungrouped)
        {
            sections.Add((ungrouped.SortOrder ?? 0, new LibraryCollectionSection(string.Empty, ungrouped.Collections.Select(item => Collection(item, false)).ToList())));
        }

        return sections.OrderBy(slot => slot.Order).Select(slot => slot.Section).ToList();
    }

    private LibraryCollection Collection(LibraryCollectionCard item, bool userCollection) =>
        new(item.Id ?? string.Empty, item.Title ?? string.Empty, browse.Resolve(item.PosterUrl), userCollection);
}

public sealed record LibraryCollection(string Id, string Title, Uri? Poster, bool UserCollection);
public sealed record LibraryCollectionSection(string Name, IReadOnlyList<LibraryCollection> Collections);
