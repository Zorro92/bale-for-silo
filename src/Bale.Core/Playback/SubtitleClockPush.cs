namespace Bale.Core.Playback;

/// <summary>
/// Decides when the player hands the subtitle overlay a new media clock. The overlay's render thread
/// extrapolates position + elapsed × rate by itself, so it only needs a push when that extrapolation
/// changes (play/pause/buffering, rate, a seek) and about once per second while playing to correct drift.
/// Never per frame. The platform supplies a monotonic millisecond clock.
/// </summary>
public sealed class SubtitleClockPush
{
    public const long DriftIntervalMilliseconds = 1000;

    private long? _pushedAt;
    private bool _playing;
    private double _rate;

    /// <summary>True when the clock should be pushed now; the push is then recorded.</summary>
    public bool ShouldPush(bool playing, double rate, long now, bool force = false)
    {
        var due = force || _pushedAt is not { } last || playing != _playing || rate != _rate
            // A paused clock does not drift; only a seek moves it, and seeks force a push.
            || playing && now - last >= DriftIntervalMilliseconds;
        if (!due) return false;
        _pushedAt = now;
        _playing = playing;
        _rate = rate;
        return true;
    }

    /// <summary>A new overlay knows no clock yet: its first push always goes through.</summary>
    public void Reset() => _pushedAt = null;

    /// <summary>Source seconds as the overlay's whole milliseconds; unknown or negative positions are 0.</summary>
    public static long Milliseconds(double sourceSeconds) =>
        double.IsFinite(sourceSeconds) && sourceSeconds > 0
            ? (long)Math.Min(Math.Round(sourceSeconds * 1000), long.MaxValue / 2) : 0;

    /// <summary>MediaPlaybackSession.PlaybackRate, or 1 when it is not a usable forward rate.</summary>
    public static double Rate(double rate) => double.IsFinite(rate) && rate > 0 ? rate : 1;
}
