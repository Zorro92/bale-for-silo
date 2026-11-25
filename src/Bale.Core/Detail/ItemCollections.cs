using Bale.Core.Api.Models;
using Bale.Core.Browse;

namespace Bale.Core.Detail;

/// <summary>
/// The collections holding a movie or series, as listCatalogItemCollections (server faa406d) answers, in the two forms a
/// title page shows (Android TV cb2c32d8a: shared viewmodel/ItemCollectionsRow.kt, TvMediaTypeFilters.kt).
/// </summary>
public abstract record ItemCollections
{
    /// <summary>ITEM_COLLECTION_PREVIEW_LIMIT: the titles one collection's preview shows, as the web does.</summary>
    public const int PreviewLimit = 6;

    /// <summary>One collection holds the title: some of its other titles under its name, with Explore all opening it.</summary>
    public sealed record Preview(SectionCollectionCard Collection, IReadOnlyList<CatalogItem> Items) : ItemCollections;

    /// <summary>An "Also in" row with a card for every collection holding the title, never capped.</summary>
    public sealed record AlsoIn(IReadOnlyList<SectionCollectionCard> Cards) : ItemCollections;

    /// <summary>loadItemCollectionsRow: only a movie or series page asks, so other pages never send the read.</summary>
    public static bool Asks(string? type) => type?.Trim().ToLowerInvariant() is "movie" or "series";

    /// <summary>
    /// decodeItemCollectionsV2 and itemCollectionsRow: both members are required; a card this client can't open is left
    /// out and a repeat dropped, as on Home, and a preview title needs a content id. Null when no collection holds the
    /// title. One collection with other titles previews up to <see cref="PreviewLimit"/> of them; otherwise every card shows.
    /// </summary>
    public static ItemCollections? From(CatalogItemCollections body)
    {
        var cards = (body.Items ?? throw new InvalidOperationException("The collections read has no items."))
            .Where(CollectionRows.IsOpenable).DistinctBy(CollectionRows.RowKey).ToList();
        var preview = (body.PreviewItems ?? throw new InvalidOperationException("The collections read has no preview items."))
            .Where(item => !string.IsNullOrWhiteSpace(item.ContentId)).ToList();
        if (cards.Count == 0) return null;
        return cards.Count == 1 && preview.Count > 0
            ? new Preview(cards[0], preview.Take(PreviewLimit).ToList())
            : new AlsoIn(cards);
    }

    /// <summary>The cards this row can open: the preview's collection, or every "Also in" card.</summary>
    public IReadOnlyList<SectionCollectionCard> Collections => this switch
    {
        Preview preview => [preview.Collection],
        AlsoIn alsoIn => alsoIn.Cards,
        _ => [],
    };

    /// <summary>Whether a server collection is among them, so TV needs the libraries it shows before showing the row.</summary>
    public bool HasServerCollection => Collections.Any(card => card.Source != SectionCollectionCard_source.User);

    /// <summary>
    /// withTvShownCollections: only the server collections whose library TV shows (never an ebook library), as Home keeps
    /// them; personal collections always stay. Null when none is left.
    /// </summary>
    public ItemCollections? WithTvShownCollections(IReadOnlySet<string> shownLibraryIds)
    {
        bool Shown(SectionCollectionCard card) => card.Source == SectionCollectionCard_source.User
            || (CollectionRows.LibraryId(card) is { } library && shownLibraryIds.Contains(library));
        return this switch
        {
            Preview preview => Shown(preview.Collection) ? preview : null,
            AlsoIn alsoIn => alsoIn.Cards.Where(Shown).ToList() is { Count: > 0 } shown ? new AlsoIn(shown) : null,
            _ => null,
        };
    }

    /// <summary>
    /// visibleOnTv: a preview without reading titles, as in every other TV row. A preview left with nothing shows its
    /// collection's card under "Also in", so the collection can still be opened.
    /// </summary>
    public ItemCollections VisibleOnTv() => this switch
    {
        Preview preview => preview.Items.Where(item => BrowseFeed.IsVisibleTvType(item.Type, showAudiobooks: true)).ToList() is { Count: > 0 } shown
            ? preview with { Items = shown }
            : new AlsoIn([preview.Collection]),
        _ => this,
    };
}

/// <summary>
/// The collections row as a title page shows it (TvItemCollectionsRow.kt): a preview's titles under its collection's
/// name, ended by an Explore all card that opens <see cref="Collection"/>; or "Also in" with a card per collection.
/// <see cref="LibraryTypes"/> holds the type of each library a server collection card opens in.
/// </summary>
public sealed record ItemCollectionsRow(string Title, IReadOnlyList<MediaCard> Cards, MediaCard? Collection,
    IReadOnlyDictionary<string, string> LibraryTypes)
{
    /// <summary>detail.alsoIn.</summary>
    public const string AlsoInTitle = "Also in";

    /// <summary>TvCollectionsExploreAllReturnKey: NUL never appears in a content id or a collection card's key.</summary>
    public const string ExploreAllKey = "\0collections-explore-all";

    /// <summary>A preview of one collection's titles, which Explore all follows.</summary>
    public bool IsPreview => Collection is not null;

    /// <summary>
    /// TvItemCollectionsRow.returnIndex: where a return to the row lands, the card that opened a page, by its key (a
    /// title's content id or a collection card's id); <see cref="ExploreAllKey"/> sits after a preview's titles.
    /// -1 when that card is gone.
    /// </summary>
    public int ReturnIndex(string key)
    {
        if (key == ExploreAllKey) return IsPreview ? Cards.Count : -1;
        for (var index = 0; index < Cards.Count; index++)
        {
            if (Cards[index].ContentId == key) return index;
        }

        return -1;
    }

    /// <summary>
    /// What a collection card opens (TvAppNavigation.kt onOpenCollection): a personal collection on its own, a server
    /// collection in its library, with that library's type picking the page's Sort and Filter choices as a Home
    /// collection card does. Never scoped to a media type: a collection's titles can live in other libraries.
    /// </summary>
    public LibraryCatalogSource Open(MediaCard card, LibraryBrowseFeed library)
    {
        var target = card.Collection ?? throw new ArgumentException("Not a collection card.", nameof(card));
        var type = target.LibraryId is { } id && LibraryTypes.TryGetValue(id, out var libraryType) ? libraryType : null;
        return LibraryCatalogSource.Collection(target.LibraryId ?? string.Empty, null, target.ToLibraryCollection(card.Title, card.ImageUrl),
            library, ControlsMediaType(type));
    }

    /// <summary>
    /// The collection page's Sort and Filter vocabulary for its library's type, as CatalogSort.forLibrary and
    /// TvLibraryBrowseFacets.availableFor read that type; null keeps the all-video set (a mixed library).
    /// </summary>
    public static string? ControlsMediaType(string? libraryType) => libraryType?.Trim().ToLowerInvariant() switch
    {
        "movie" or "movies" or "film" => "movie",
        "series" or "show" or "shows" or "tv" => "series",
        "audiobook" or "audiobooks" => "audiobook",
        _ => null,
    };

    /// <summary>
    /// The row's cards: a preview's titles as TvMediaRow posters (one per content id), or the collection posters of
    /// TvCollectionRow with their "N items" / "by Name" line.
    /// </summary>
    internal static ItemCollectionsRow Create(ItemCollections collections, BrowseFeed browse, CollectionOwners owners,
        IReadOnlyDictionary<string, string> shownLibraries)
    {
        MediaCard Card(SectionCollectionCard card) => CollectionRows.ToCard(card, browse.Resolve, showCounts: true, owners);
        var types = collections.Collections.Select(CollectionRows.LibraryId).OfType<string>().Distinct(StringComparer.Ordinal)
            .Where(shownLibraries.ContainsKey).ToDictionary(id => id, id => shownLibraries[id], StringComparer.Ordinal);
        return collections switch
        {
            ItemCollections.Preview preview => new(preview.Collection.Title ?? string.Empty,
                preview.Items.Select(item => browse.ToCard(item, CardShape.Poster)).DistinctBy(card => card.ContentId).ToList(),
                Card(preview.Collection), types),
            ItemCollections.AlsoIn alsoIn => new(AlsoInTitle, alsoIn.Cards.Select(Card).ToList(), null, types),
            _ => throw new ArgumentOutOfRangeException(nameof(collections)),
        };
    }
}

public sealed partial class DetailFeed
{
    /// <summary>
    /// The collections row of a movie or series page, or null for none (loadItemCollectionsRow and
    /// TvItemDetailViewModel.loadCollections, Android cb2c32d8a). Only a movie or series asks, and only a server whose
    /// search capabilities advertise item_collections, read on each load since they belong to the current server and
    /// profile. Server collections show only from libraries TV shows, as on Home, and book titles stay off TV.
    /// Throws when a read fails, the library list a server collection needs included, so the page keeps the row it
    /// shows (none on a first load). No library id is sent: Bale's title page has none, and no box set row.
    /// </summary>
    public async Task<ItemCollectionsRow?> CollectionsAsync(ItemDetail detail, CancellationToken cancellationToken = default)
    {
        if (!ItemCollections.Asks(detail.Type) || string.IsNullOrWhiteSpace(detail.ContentId)) return null;
        var capabilities = await _api.Api.V2.Catalog.Search.Capabilities.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (capabilities?.ItemCollections != true) return null;
        var body = await _api.Api.V2.Catalog.Items[detail.ContentId].Collections.GetAsync(config =>
        {
            // CatalogRepository.getItemCollections sizes the cards as a poster rail (ArtworkSurface.SIMILAR).
            config.QueryParameters.PosterWidth = RailPosterWidth;
        }, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The server returned no collections for {detail.ContentId}.");
        if (ItemCollections.From(body) is not { } loaded) return null;

        // Unverified cards could be an ebook library's: a failed library read keeps the row already shown.
        var libraries = loaded.HasServerCollection
            ? await _browse.ShownLibrariesAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The libraries TV shows could not be read.")
            : new Dictionary<string, string>();
        if (loaded.WithTvShownCollections(libraries.Keys.ToHashSet(StringComparer.Ordinal))?.VisibleOnTv() is not { } row) return null;
        // rememberTvCollectionOwners names the makers of "Also in" cards; a preview shows its collection only as a heading.
        var owners = row is ItemCollections.AlsoIn alsoIn
            ? await _browse.CollectionOwnersAsync(alsoIn.Cards, cancellationToken).ConfigureAwait(false)
            : CollectionOwners.None;
        return ItemCollectionsRow.Create(row, _browse, owners, libraries);
    }
}
