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

/// <summary>The Material icons in TvMediaCardActions.kt, independent of the UI toolkit.</summary>
public enum CardMenuIcon
{
    Play,
    Check,
    VisibilityOff,
    Favorite,
    FavoriteBorder,
    BookmarkAdd,
    BookmarkRemove,
    Close,
}

public sealed record CardMenuEntry(CardAction Action, string Label, CardMenuIcon Icon);

/// <summary>Optional callbacks and row order from TvMediaCardActions.kt, not Detail's More Actions dialog.</summary>
public static class CardMenu
{
    public static IReadOnlyList<CardMenuEntry> ForCard(MediaCard card, bool home = false, string sectionType = "")
    {
        var entries = new List<CardMenuEntry>();
        // TvHomeScreen.kt only offers Play/Resume on Continue Watching, never on Next Up or catalog grids.
        if (home && IsContinueWatching(sectionType))
        {
            entries.Add(new(CardAction.Play, card.PositionSeconds is not null ? "Resume" : "Play", CardMenuIcon.Play));
        }

        entries.AddRange(ForFlags(card.Flags));
        if (home && IsProgressRow(sectionType) && HomeCardDismissal.For(card) is { } dismissal)
        {
            entries.Add(new(CardAction.RemoveFromHome, dismissal.Label, CardMenuIcon.Close));
        }

        return entries;
    }

    /// <summary>TvDetailEpisodeRail.kt exposes watched and favorite only, without Play or watchlist.</summary>
    public static IReadOnlyList<CardMenuEntry> ForEpisode(bool played, bool favorite) =>
        ForFlags(new ItemFlags(false, favorite, played), watchlist: false);

    public static IReadOnlyList<CardMenuEntry> ForFlags(ItemFlags flags, bool watchlist = true)
    {
        var entries = new List<CardMenuEntry>
        {
            new(CardAction.Watched, flags.Played ? "Mark as Unwatched" : "Mark as Watched",
                flags.Played ? CardMenuIcon.VisibilityOff : CardMenuIcon.Check),
            new(CardAction.Favorite, flags.IsFavorite ? "Remove from Favorites" : "Add to Favorites",
                flags.IsFavorite ? CardMenuIcon.Favorite : CardMenuIcon.FavoriteBorder),
        };
        if (watchlist)
        {
            entries.Add(new(CardAction.Watchlist, flags.InWatchlist ? "Remove from Watchlist" : "Add to Watchlist",
                flags.InWatchlist ? CardMenuIcon.BookmarkRemove : CardMenuIcon.BookmarkAdd));
        }

        return entries;
    }

    public static ItemFlags Toggle(ItemFlags flags, CardAction action) => action switch
    {
        CardAction.Watched => flags with { Played = !flags.Played },
        CardAction.Favorite => flags with { IsFavorite = !flags.IsFavorite },
        CardAction.Watchlist => flags with { InWatchlist = !flags.InWatchlist },
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Not a card toggle."),
    };

    /// <summary>Use Detail's existing writes; callers can optimistically apply Toggle and roll back on failure.</summary>
    public static async Task<ItemFlags> ToggleAsync(DetailFeed feed, string contentId, ItemFlags before, CardAction action,
        CancellationToken cancellationToken = default)
    {
        var after = Toggle(before, action);
        await (action switch
        {
            CardAction.Watched => feed.SetPlayedAsync(contentId, after.Played, cancellationToken),
            CardAction.Favorite => feed.SetFavoriteAsync(contentId, after.IsFavorite, cancellationToken),
            CardAction.Watchlist => feed.SetWatchlistAsync(contentId, after.InWatchlist, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        }).ConfigureAwait(false);
        feed.Forget(contentId);
        return after;
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
        // HomeViewModel only publishes a dismissal while the observed target/anchor is still current.
        if (For(dismissed) is null || !rows.Where(row => CardMenu.IsProgressRow(row.SectionType)).SelectMany(row => row.Cards).Any(MatchesAnchor))
        {
            return rows;
        }

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

        bool MatchesAnchor(MediaCard card) => card.ContentId == dismissed.ContentId && (dismissed.ProgressUpdatedAt is not null
            ? card.ProgressUpdatedAt == dismissed.ProgressUpdatedAt : card.SeriesId == dismissed.SeriesId);

        bool Matches(MediaCard card) => MatchesAnchor(card)
            || (!string.IsNullOrWhiteSpace(series) && (card.ContentId == series || card.SeriesId == series));
    }
}
