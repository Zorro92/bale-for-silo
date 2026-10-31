using Bale.Core.Detail;

namespace Bale.Core.Browse;

public enum CardAction
{
    Play,
    Watched,
    Favorite,
    Watchlist,
    RemoveFromHome,
}

public sealed record CardMenuEntry(CardAction Action, string Label);

/// <summary>Optional callbacks and row order from TvMediaCardActions.kt, not Detail's More Actions dialog.</summary>
public static class CardMenu
{
    public static IReadOnlyList<CardMenuEntry> ForCard(MediaCard card, bool home = false, string sectionType = "")
    {
        var entries = new List<CardMenuEntry>();
        // TvHomeScreen.kt only offers Play/Resume on Continue Watching, never on Next Up or catalog grids.
        if (home && IsContinueWatching(sectionType))
        {
            entries.Add(new(CardAction.Play, card.PositionSeconds is not null ? "Resume" : "Play"));
        }

        entries.AddRange(ForFlags(card.Flags));
        if (home && IsProgressRow(sectionType) && HomeCardDismissal.For(card) is { } dismissal)
        {
            entries.Add(new(CardAction.RemoveFromHome, dismissal.Label));
        }

        return entries;
    }

    /// <summary>TvDetailEpisodeRail.kt exposes watched and favorite only, without Play or watchlist.</summary>
    public static IReadOnlyList<CardMenuEntry> ForEpisode(bool played, bool favorite) =>
        ForFlags(new ItemFlags(false, favorite, played), watchlist: false);

    private static IReadOnlyList<CardMenuEntry> ForFlags(ItemFlags flags, bool watchlist = true)
    {
        var entries = new List<CardMenuEntry>
        {
            new(CardAction.Watched, flags.Played ? "Mark as Unwatched" : "Mark as Watched"),
            new(CardAction.Favorite, flags.IsFavorite ? "Remove from Favorites" : "Add to Favorites"),
        };
        if (watchlist)
        {
            entries.Add(new(CardAction.Watchlist, flags.InWatchlist ? "Remove from Watchlist" : "Add to Watchlist"));
        }

        return entries;
    }

    public static bool IsContinueWatching(string sectionType) =>
        sectionType.Contains("continue", StringComparison.OrdinalIgnoreCase)
        || sectionType.Contains("in_progress", StringComparison.OrdinalIgnoreCase);

    public static bool IsProgressRow(string sectionType) => IsContinueWatching(sectionType)
        || sectionType.Contains("next_up", StringComparison.OrdinalIgnoreCase)
        || sectionType.Contains("up_next", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// HomeDismissal.kt: progress wins over series; an episode/series dismissal drops the whole show
/// from both progress rows, but never from ordinary shelves. Movies/books leave one card at a time.
/// </summary>
public sealed record HomeCardDismissal(string Surface, string Label)
{
    public static HomeCardDismissal? For(MediaCard card) => card.ProgressUpdatedAt is not null
        ? new("continue_watching", "Remove from Continue Watching")
        : !string.IsNullOrWhiteSpace(card.SeriesId) ? new("next_up", "Remove from Next Up") : null;

    public static IReadOnlyList<MediaRow> Remove(IReadOnlyList<MediaRow> rows, MediaCard dismissed)
    {
        var series = dismissed.Type.Trim().ToLowerInvariant() switch
        {
            "series" => dismissed.ContentId,
            "episode" or "episodes" => dismissed.SeriesId,
            _ => null,
        };
        return rows.Select(row => !CardMenu.IsProgressRow(row.SectionType) ? row : row with
        {
            Cards = row.Cards.Where(card => !Matches(card)).ToList(),
        }).Where(row => row.Cards.Count > 0).ToList();

        bool Matches(MediaCard card) =>
            (card.ContentId == dismissed.ContentId && (dismissed.ProgressUpdatedAt is not null
                ? card.ProgressUpdatedAt == dismissed.ProgressUpdatedAt : card.SeriesId == dismissed.SeriesId))
            || (!string.IsNullOrWhiteSpace(series) && (card.ContentId == series || card.SeriesId == series));
    }
}
