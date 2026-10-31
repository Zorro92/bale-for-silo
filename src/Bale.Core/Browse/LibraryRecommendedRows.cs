namespace Bale.Core.Browse;

/// <summary>TvLibraryDetailScreen.RecommendedTab and LibraryMediaScope.kt: no featured rows, scoped cards.</summary>
public static class LibraryRecommendedRows
{
    public static IReadOnlyList<MediaRow> For(IReadOnlyList<MediaRow> rows, string? mediaScope) => rows
        .Where(row => !row.Featured)
        .Select(row => row with
        {
            Cards = row.Cards.Where(card => Matches(card.Type, mediaScope)).ToList(),
            ExploreSource = row.ExploreSource is { } source ? source with { MediaScope = mediaScope } : null,
        })
        .Where(row => row.Cards.Count > 0)
        .ToList();

    private static bool Matches(string type, string? scope) => scope switch
    {
        "movie" => type.Trim().ToLowerInvariant() is "movie" or "movies" or "film",
        "series" => type.Trim().ToLowerInvariant() is "series" or "show" or "shows" or "tv" or "episode",
        _ => BrowseFeed.IsVisibleTvType(type, showAudiobooks: true),
    };
}
