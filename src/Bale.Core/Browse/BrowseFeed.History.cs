using Bale.Core.Api.Models;

namespace Bale.Core.Browse;

public sealed partial class BrowseFeed
{
    /// <summary>HistoryV2Api.kt: forty entries, most-recent watch first, including empty cursor pages.</summary>
    internal async Task<SearchResultPage> HistoryPageAsync(ArtworkWidths widths, SearchContinuation? continuation,
        HashSet<string> contentIds, CancellationToken cancellationToken)
    {
        var cursor = continuation?.Cursor;
        var seen = new HashSet<string>(continuation?.Seen ?? [], StringComparer.Ordinal);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await _api.Api.V2.History.GetAsync(config =>
            {
                config.QueryParameters.Limit = 40;
                config.QueryParameters.Cursor = cursor;
                config.QueryParameters.PosterWidth = widths.Poster;
                config.QueryParameters.BackdropWidth = widths.Backdrop;
                config.QueryParameters.LogoWidth = widths.Logo;
                config.QueryParameters.StillWidth = widths.Still;
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            SearchContinuation? next = null;
            var nextCursor = response?.Page?.NextCursor;
            if (response?.Page?.HasMore == true)
            {
                if (string.IsNullOrWhiteSpace(nextCursor) || !seen.Add(nextCursor))
                {
                    throw new InvalidDataException("Invalid history continuation.");
                }
                next = new SearchContinuation(nextCursor, seen);
            }
            else if (!string.IsNullOrWhiteSpace(nextCursor))
            {
                throw new InvalidDataException("Invalid history continuation.");
            }

            var cards = (response?.Items ?? []).Where(item => IsVisibleTvType(item.Type, false)
                    && !string.IsNullOrWhiteSpace(item.ContentId) && contentIds.Add(item.ContentId))
                .Select(item => ToCard(HistoryItem(item), CardShape.Poster)).ToList();
            if (cards.Count == 0 && next is not null)
            {
                // History pages may be genuinely empty or contain only already-seen/hidden cards.
                // Like TvHistoryScreen, keep loading until there is a focusable card or the log ends.
                cursor = next.Cursor;
                continue;
            }
            return new SearchResultPage(cards, cards.Count, false, next);
        }
    }

    // HistoryV2Models.kt flattens the ordinary catalog card beside its watch witness. Kiota emits
    // a separate type; map the card fields explicitly instead of reflection/JSON roundtrips on AOT.
    private static CatalogItem HistoryItem(HistoryCard item) => new()
    {
        ContentId = item.ContentId,
        Type = item.Type,
        Title = item.Title,
        Year = item.Year,
        Overview = item.Overview,
        PosterUrl = item.PosterUrl,
        BackdropUrl = item.BackdropUrl,
        LogoUrl = item.LogoUrl,
        SeriesPosterUrl = item.SeriesPosterUrl,
        SeriesBackdropUrl = item.SeriesBackdropUrl,
        SeriesTitle = item.SeriesTitle,
        SeriesId = item.SeriesId,
        SeasonNumber = item.SeasonNumber,
        EpisodeNumber = item.EpisodeNumber,
        PlayContentId = item.PlayContentId,
        Runtime = item.Runtime,
        DurationSeconds = item.DurationSeconds,
        PositionSeconds = item.PositionSeconds,
        ProgressUpdatedAt = item.ProgressUpdatedAt,
        UserState = item.UserState,
        ContentRating = item.ContentRating,
        Genres = item.Genres,
        RatingImdb = item.RatingImdb,
        RatingTmdb = item.RatingTmdb,
        OverlaySummary = item.OverlaySummary,
    };
}
