using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>
/// Which of a series' or movie's track preferences a player saved (silo-roku 3ae62aa nav.notePlayed tracksSaved), handed
/// across Up Next's player replacements to the Detail entry underneath, which puts its own picks of those kinds back on
/// Auto and reads its choices again. One-shot, like SeriesReturn: the page takes it on return; a page for another title
/// finds nothing. UI thread.
/// </summary>
internal static class TrackChoiceReturn
{
    private static PlayerTrackChoiceReturn? _pending;

    public static void Record(PlayerTrackPreferenceWrite write) => _pending = PlayerTrackChoiceReturn.With(_pending, write);

    public static PlayerTrackChoiceReturn? Take(string? ownerId)
    {
        var pending = _pending;
        _pending = null;
        return pending is not null && pending.OwnerId == ownerId ? pending : null;
    }
}
