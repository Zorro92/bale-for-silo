using Bale.Core.Api.Api.V2.Catalog;
using Bale.Core.Api.Models;
using Microsoft.Kiota.Abstractions;

namespace Bale.Core.Browse;

public sealed partial class BrowseFeed
{
    /// <summary>TvLibraryDetailViewModel.kt: refill scoped Recommended shelves, three at a time in server order.</summary>
    public async Task<LibraryRecommendedResult> LoadLibraryRecommendedAsync(string libraryId, ArtworkWidths widths,
        string? mediaScope = null, CancellationToken cancellationToken = default)
    {
        var sections = await LibrarySectionsAsync(libraryId, widths, cancellationToken).ConfigureAwait(false);
        using var permits = new SemaphoreSlim(3);
        var scoped = await Task.WhenAll((sections ?? []).Where(section => section.Featured != true).Select(async section =>
        {
            await permits.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ScopeLibrarySectionAsync(section, libraryId, mediaScope, widths, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                permits.Release();
            }
        })).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return new LibraryRecommendedResult(LibraryRecommendedRows.For(scoped.Where(result => result.Section.Items is { Count: > 0 })
            .Select(result => ToRow(result.Section, libraryId)).ToList(), mediaScope), scoped.Any(result => result.Incomplete));
    }

    private async Task<(Section Section, bool Incomplete)> ScopeLibrarySectionAsync(Section section, string libraryId,
        string? mediaScope, ArtworkWidths widths, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mediaScope is null) return (section, false);
        var source = new SectionBrowseSource(section.Id ?? string.Empty, section.Title ?? string.Empty, libraryId, mediaScope);
        var items = section.Items ?? [];
        var initial = items.Where(item => source.MatchesMediaScope(item.Type)).ToList();
        // shared/catalog/LibraryMediaScope.kt: a full inline window cannot prove the source is exhausted.
        var target = (int)Math.Min(section.ItemLimit is > 0 ? section.ItemLimit.Value : Math.Max(items.Count, 20), 100);
        (Section, bool) Result(IEnumerable<CatalogItem> cards, bool incomplete = false) => (new Section
        {
            Id = section.Id, Title = section.Title, SectionType = section.SectionType, Featured = section.Featured,
            Items = cards.Take(target).ToList(),
        }, incomplete);
        if (initial.Count >= target) return Result(initial);
        if ((section.TotalCount ?? 0) <= items.Count && section.ItemLimit is > 0 && items.Count < section.ItemLimit)
        {
            return Result(initial);
        }
        // LibraryMediaScope.kt: catalog sections use the admin definition, not profile overrides.
        if (section.Customized == true || section.IsCustom == true) return Result(initial, true);

        var merged = initial.DistinctBy(item => item.ContentId).ToList();
        var ids = merged.Select(item => item.ContentId).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        try
        {
            // LibraryMediaScope.kt bounds each refill to eight sequential catalog pages (SectionApi.kt: 50 items).
            for (var pageIndex = 0; pageIndex < 8; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await _api.Api.V2.Catalog.GetAsync(config =>
                {
                    config.QueryParameters.Source = GetSourceQueryParameterType.Section;
                    config.QueryParameters.Scope = GetScopeQueryParameterType.Library;
                    config.QueryParameters.LibraryId = libraryId;
                    config.QueryParameters.SectionId = source.SectionId;
                    config.QueryParameters.Cursor = cursor;
                    config.QueryParameters.Limit = 50;
                    config.QueryParameters.PosterWidth = widths.Poster;
                    config.QueryParameters.BackdropWidth = widths.Backdrop;
                    config.QueryParameters.LogoWidth = widths.Logo;
                    config.QueryParameters.StillWidth = widths.Still;
                }, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (page is null) return Result(merged, true);
                var pageItems = page.Items ?? [];
                foreach (var item in pageItems.Where(item => source.MatchesMediaScope(item.Type)))
                {
                    // LibraryMediaScope.kt: inline items lead, deduplicate by ID, and keep their richer progress payload.
                    if (ids.Add(item.ContentId)) merged.Add(item);
                }
                if (merged.Count >= target || page.Page?.HasMore != true) return Result(merged);
                var next = page.Page.NextCursor;
                if (pageItems.Count == 0 || string.IsNullOrWhiteSpace(next) || !seen.Add(next)) return Result(merged, true);
                cursor = next;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 401)
        {
            throw;
        }
        catch
        {
            // TvLibraryDetailViewModel.kt keeps partial shelves usable and offers Retry / Browse on refill failure.
            return Result(merged, true);
        }
        return Result(merged, true);
    }
}

public sealed record LibraryRecommendedResult(IReadOnlyList<MediaRow> Rows, bool Incomplete)
{
    // TvLibraryDetailViewModel.kt.loadRecommended: the same warning for a partial or wholly missing shelf.
    public string? Error => Incomplete ? "Some shelves could not be fully loaded for this media type. Retry or open Browse." : null;
}
