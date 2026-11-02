namespace Bale.Core.Browse;

public sealed record BrowseReturnTarget(string SectionId, string ItemId, int SectionIndex, int ItemIndex);
public readonly record struct BrowseReturnPosition(int SectionIndex, int ItemIndex);

/// <summary>TvSkylineSectionFeed.kt: resolve against rendered rows, by identity before index.</summary>
public static class BrowseFocus
{
    // TvMediaRow.kt includes Explore all as a focusable slot after the deduplicated media cards.
    public const string ExploreAllKey = "\0explore-all";

    public static BrowseReturnPosition? Resolve(IReadOnlyList<MediaRow> rows, BrowseReturnTarget? target)
    {
        var visible = rows.Where(row => row.Cards.Count > 0).ToList();
        if (visible.Count == 0) return null;
        if (target is null) return new(0, 0);

        var sectionIndex = visible.FindIndex(row => row.Id == target.SectionId);
        var sameSection = sectionIndex >= 0;
        if (!sameSection) sectionIndex = Math.Clamp(target.SectionIndex, 0, visible.Count - 1);
        var section = visible[sectionIndex];
        var keys = section.Cards.Select(card => card.ContentId).Distinct(StringComparer.Ordinal).ToList();
        if (section.OffersExploreAll) keys.Add(ExploreAllKey);

        // Home rows overlap. Never follow a removed section's title into a copy in another row.
        var itemIndex = sameSection ? keys.IndexOf(target.ItemId) : -1;
        if (itemIndex < 0) itemIndex = Math.Clamp(target.ItemIndex, 0, keys.Count - 1);
        return new(sectionIndex, itemIndex);
    }
}
