using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

/// <summary>
/// The plan's two clocks, from android-shared/.../common/player/seek/PlaybackTimelineSeekPolicy.kt.
/// The engine plays "player" time; progress, resume and duration are always "source" (movie) time.
/// A reanchored HLS stream may start its player clock at 0 for a non-zero source position.
/// </summary>
public sealed record PlaybackTimeline(double SourceStartSeconds, double PlayerStartSeconds, double TimelineOffsetSeconds)
{
    public static PlaybackTimeline From(TimelineV3? timeline) => new(
        Finite(timeline?.SourceStartSeconds), Finite(timeline?.PlayerStartSeconds), Finite(timeline?.TimelineOffsetSeconds, allowNegative: true));

    /// <summary>sourcePositionForPlayer.</summary>
    public double? SourceForPlayer(double playerSeconds) => Map(playerSeconds, TimelineOffsetSeconds);

    /// <summary>playerPositionForSource.</summary>
    public double? PlayerForSource(double sourceSeconds) => Map(sourceSeconds, -TimelineOffsetSeconds);

    /// <summary>
    /// TvVideoPlaybackStarter.kt: the player mounts at the server's player_start_seconds; only Start
    /// Over's explicit zero overrides that server-defined coordinate.
    /// </summary>
    public double PlayerStart(double? overridePosition) => PlaybackStartPosition.Resolve(
        PlaybackStartPosition.IsExplicitStartOver(overridePosition) ? overridePosition : null, PlayerStartSeconds, null);

    /// <summary>resolveTvSourceStartPosition: what the reporter attaches as the session's start.</summary>
    public double SourceStart(double? startRequestPosition, double? overridePosition) =>
        PlaybackStartPosition.Resolve(startRequestPosition, SourceStartSeconds, PlayerStart(overridePosition));

    private static double? Map(double position, double offset)
    {
        if (!double.IsFinite(position) || position < 0 || !double.IsFinite(offset)) return null;
        var mapped = position + offset;
        return double.IsFinite(mapped) && mapped >= 0 ? mapped : null;
    }

    private static double Finite(double? value, bool allowNegative = false) =>
        value is { } v && double.IsFinite(v) && (allowNegative || v >= 0) ? v : 0;
}
