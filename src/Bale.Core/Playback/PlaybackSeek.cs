using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

/// <summary>Absolute source/movie seconds, shared by player skips and the debug seek endpoint.</summary>
public sealed record PlaybackSeek(double Seconds, bool Pause = false)
{
    // silo-android TvPlayerScreen.kt LEGACY_TV_VIDEO_SEEK_INTERVALS and model/settings/SeekIntervals.kt.
    public const int SkipBackSeconds = 10;
    public const int SkipForwardSeconds = 30;

    public double? PlayerPosition(PlaybackTimeline timeline, double? duration) =>
        !double.IsFinite(Seconds) || Seconds < 0 || duration is > 0 && Seconds > duration ? null
        : timeline.PlayerForSource(Seconds) is { } player && player < TimeSpan.MaxValue.TotalSeconds ? player : null;

    public static double SkipTarget(double origin, double? duration, bool forward)
    {
        var position = double.IsFinite(origin) ? Math.Max(0, origin) : 0;
        var target = Math.Max(0, position + (forward ? SkipForwardSeconds : -SkipBackSeconds));
        return duration is > 0 and { } end && double.IsFinite(end) ? Math.Min(target, end) : target;
    }

    /// <summary>
    /// PlaybackTimelineSeekPolicy.kt: use a native seek only where the mounted transport can serve
    /// it. Native HLS seekability/ranges prove the produced window, not the whole movie duration.
    /// </summary>
    public bool CanSeekInMount(PlaybackPlan plan, bool isHls, bool canSeek,
        IReadOnlyList<(double Start, double End)> playerRanges)
    {
        if (!canSeek || PlayerPosition(PlaybackTimeline.From(plan.Timeline), plan.Source?.DurationSeconds) is not { } player)
            return false;
        var start = plan.Timeline?.SeekWindowStartSeconds;
        var end = plan.Timeline?.SeekWindowEndSeconds;
        if (start is { } from && (!double.IsFinite(from) || from < 0 || Seconds < from)
            || end is { } to && (!double.IsFinite(to) || to < 0 || Seconds > to)
            || start is { } a && end is { } b && a > b) return false;
        if (!isHls) return true;
        if (start is not null && end is not null || plan.Timeline?.CanSeekAnywhere == true) return true;
        return playerRanges.Any(range => double.IsFinite(range.Start) && double.IsFinite(range.End)
            && range.Start >= 0 && range.Start <= player && player <= range.End);
    }
}
