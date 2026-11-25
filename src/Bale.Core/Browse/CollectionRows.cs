using System.Globalization;
using Bale.Core.Api.Models;

namespace Bale.Core.Browse;

/// <summary>
/// What a card of a row of collections opens (shared SectionModels.kt SectionCollectionCard): a server
/// collection in its library, or a personal collection made by <see cref="CreatorProfileId"/>.
/// </summary>
public sealed record CollectionTarget(string Id, bool UserCollection, string? LibraryId, string? CreatorProfileId)
{
    /// <summary>The collection as the library Collections tab holds it, for the shared collection grid.</summary>
    public LibraryCollection ToLibraryCollection(string title, Uri? poster) => new(Id, title, poster, UserCollection);
}

/// <summary>
/// The profiles a personal collection card can name as its maker (TvCollectionRow.kt TvCollectionOwners):
/// the active profile, whose own collections get no byline, and the household's names by id.
/// </summary>
public sealed record CollectionOwners(string? ActiveProfileId, IReadOnlyDictionary<string, string> NamesById)
{
    public static CollectionOwners None { get; } = new(null, new Dictionary<string, string>());

    /// <summary>TvCollectionOwners.ownerName: another profile's personal collection whose maker has a known name.</summary>
    public string? OwnerName(CollectionTarget target)
    {
        if (!target.UserCollection || string.IsNullOrWhiteSpace(target.CreatorProfileId)) return null;
        if (ActiveProfileId is null || target.CreatorProfileId == ActiveProfileId) return null;
        return NamesById.TryGetValue(target.CreatorProfileId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : null;
    }
}

/// <summary>
/// Rows of collections (section type <c>collection_posters</c>, server 4a0ab33b/217ff25a): their cards come in
/// <c>collections</c>, not <c>items</c>. Ported from Android TV's first version (db87f835f): SectionModels.kt,
/// HomeSectionsV2Api.decodeSectionV2, TvHomeSections.withTvShownCollections and TvCollectionRow.kt.
/// </summary>
public static class CollectionRows
{
    public const string SectionType = "collection_posters";

    /// <summary>A collection card's id prefix: content ids never contain it, so no title request can name a collection.</summary>
    public const string CardIdPrefix = "collection:";

    public static bool IsCollectionRow(Section section) => section.SectionType == SectionType;

    /// <summary>
    /// decodeSectionV2: the cards this client can open (a known source, and a library for a server
    /// collection), in the row's order, one per source and id.
    /// </summary>
    public static IReadOnlyList<SectionCollectionCard> OpenableCards(Section section) => (section.Collections ?? [])
        .Where(IsOpenable)
        .DistinctBy(RowKey)
        .ToList();

    /// <summary>SectionCollectionCard.isOpenable.</summary>
    public static bool IsOpenable(SectionCollectionCard card) => !string.IsNullOrWhiteSpace(card.Id) && card.Source switch
    {
        SectionCollectionCard_source.Library => LibraryId(card) is not null,
        SectionCollectionCard_source.User => true,
        _ => false,
    };

    /// <summary>SectionCollectionCard.rowKey: a server and a personal collection may share an id.</summary>
    public static string RowKey(SectionCollectionCard card) =>
        (card.Source == SectionCollectionCard_source.User ? "user" : "library") + ":" + card.Id;

    /// <summary>libraryIdInt: the client's numeric library id, null for a personal collection or a malformed one.</summary>
    public static string? LibraryId(SectionCollectionCard card) =>
        card.Source == SectionCollectionCard_source.Library && long.TryParse(card.LibraryId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// TvHomeSections.withTvShownCollections: Home keeps a server collection only when its library is one TV
    /// shows the profile (never an ebook library); null means not known yet, which holds every server card
    /// back. Personal collections always stay. A row left with no cards is dropped.
    /// </summary>
    public static IReadOnlyList<SectionCollectionCard> ShownOnHome(IReadOnlyList<SectionCollectionCard> cards, IReadOnlySet<string>? shownLibraryIds) =>
        cards.Where(card => card.Source == SectionCollectionCard_source.User
            || (shownLibraryIds is not null && LibraryId(card) is { } library && shownLibraryIds.Contains(library))).ToList();

    /// <summary>
    /// TvCollectionRow.kt: the card's one caption line, "by Name" for another profile's shared collection,
    /// else "3 items". <paramref name="showCounts"/> is off on a Movies- or Series-scoped library page, where
    /// the whole collection's count would be wrong.
    /// </summary>
    public static string? Caption(CollectionTarget target, long? itemCount, bool showCounts, CollectionOwners owners)
    {
        if (owners.OwnerName(target) is { } owner) return "by " + owner;
        if (!showCounts) return null;
        var count = Math.Max(0, itemCount ?? 0);
        // collections_itemCount: "%2$s item" / "%2$s items" with the grouped count.
        return count.ToString("N0", CultureInfo.InvariantCulture) + (count == 1 ? " item" : " items");
    }

    /// <summary>
    /// A collection as a poster card. The marquee previews its title and its backdrop as the hero, the poster
    /// when it has none (SectionCollectionCard.tvMarqueeItem); nothing else.
    /// </summary>
    public static MediaCard ToCard(SectionCollectionCard card, Func<string?, Uri?> resolve, bool showCounts, CollectionOwners owners)
    {
        var target = new CollectionTarget(card.Id!, card.Source == SectionCollectionCard_source.User, LibraryId(card),
            string.IsNullOrWhiteSpace(card.CreatorProfileId) ? null : card.CreatorProfileId);
        var poster = resolve(card.PosterUrl);
        return new MediaCard(CardIdPrefix + RowKey(card), "collection", card.Title ?? string.Empty,
            Caption(target, card.ItemCount, showCounts, owners), poster, resolve(card.BackdropUrl) ?? poster,
            null, null, null, null, null, null, null, [], null, null, null)
        {
            Collection = target,
        };
    }

    /// <summary>
    /// A row of collections as a Skyline poster rail: every card (never capped to the 20-card row limit), no
    /// Explore all, and never the hero, so a featured flag is ignored (TvLibraryDetailViewModel.kt).
    /// </summary>
    public static MediaRow ToRow(Section section, IReadOnlyList<SectionCollectionCard> cards, Func<string?, Uri?> resolve,
        bool showCounts, CollectionOwners owners) =>
        new(section.Id ?? string.Empty, section.Title ?? string.Empty, SectionType, false, CardShape.Poster,
            cards.Select(card => ToCard(card, resolve, showCounts, owners)).ToList());

    /// <summary>Whether any card is another profile's collection, so naming makers needs the profile list.</summary>
    public static bool NeedsOwners(IEnumerable<Section> sections, string? activeProfileId) =>
        NeedsOwners(sections.Where(IsCollectionRow).SelectMany(OpenableCards), activeProfileId);

    /// <inheritdoc cref="NeedsOwners(IEnumerable{Section}, string?)"/>
    public static bool NeedsOwners(IEnumerable<SectionCollectionCard> cards, string? activeProfileId) => activeProfileId is not null
        && cards.Any(card => card.Source == SectionCollectionCard_source.User
            && !string.IsNullOrWhiteSpace(card.CreatorProfileId) && card.CreatorProfileId != activeProfileId);
}
