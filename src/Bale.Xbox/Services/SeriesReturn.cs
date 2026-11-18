using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>
/// The player's answer to "which episode should the series page land on" (tvOS SeriesPlaybackReturn, Android
/// e727c9af5), handed across the Up Next player replacements to the Detail entry underneath. One-shot: the series
/// page takes it on return; a later page for another series finds nothing. UI thread.
/// </summary>
internal static class SeriesReturn
{
    private static SeriesPlaybackReturn? _pending;

    public static void Record(SeriesPlaybackReturn? value) => _pending = value;

    public static SeriesPlaybackReturn? Take(string? seriesId)
    {
        var pending = _pending;
        _pending = null;
        return pending is not null && pending.SeriesId == seriesId ? pending : null;
    }
}
