using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

/// <summary>
/// Silo's <c>server_remux_progressive</c> delivery: the server copies the source into one fragmented
/// MP4 response that starts at the plan's resolved keyframe origin and only moves forward
/// (silo-server docs/playback-api.md "Delivery", docs/architecture/playback-protocol-v3.md §4, §6.2).
/// It has no byte ranges, so every seek and every reconnect is a <c>seek_reanchor</c> replan whose
/// new URL starts at the target time. Bale mounts it through FFmpeg's non-seekable input.
/// </summary>
public static class PlaybackProgressive
{
    public const string Delivery = "server_remux_progressive";

    /// <summary>
    /// protocol_v3.go FeatureProgressiveRangeRefusalV3, listed in deliveries.progressive.features:
    /// the URL then carries remux_range=refuse, and a GET whose Range starts past byte 0 answers 409
    /// before the remux starts instead of a 200 from the origin. Bale never sends such a Range; a
    /// 409 means "reopen at the current position".
    /// </summary>
    public const string RangeRefusalFeature = "progressive_range_refusal_v1";

    /// <summary>
    /// silo-server web/src/player/components/VideoPlayer.tsx PROGRESSIVE_REMUX_IDLE_REOPEN_MS: a paused
    /// reader stops the response, and the server reaps it after its stall window (180 s by default,
    /// docs/architecture/streaming-write-deadline.md). Playing again after this long reopens it.
    /// </summary>
    public const long IdleReopenMilliseconds = 60_000;

    /// <summary>
    /// MP4-muxable audio the FFmpeg route decodes. The server copies any claimed codec into MP4 with
    /// no muxability check of its own (candidate_video_v3.go remuxAudioFacts), and FFmpeg's MP4 muxer
    /// takes TrueHD/DTS/PCM only as experimental or not at all, so those convert to AAC instead.
    /// </summary>
    public static IReadOnlyList<string> AudioCodecs { get; } = Array.AsReadOnly(new[] { "aac", "ac3", "eac3", "mp3", "flac", "opus", "alac" });

    public static bool IsProgressive(PlaybackPlan? plan) =>
        string.Equals(plan?.Delivery, Delivery, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The player clock a mount opens at. The forward-only response starts at its keyframe origin
    /// (player 0), and FFmpeg cannot seek a non-seekable input to player_start_seconds, so the mount
    /// plays from the origin, at most one GOP before the request. Source time stays exact through the
    /// plan's timeline offset. Every other delivery keeps the requested player start.
    /// </summary>
    public static double MountPlayerStart(PlaybackPlan plan, double playerStart) => IsProgressive(plan) ? 0 : playerStart;

    /// <summary>The source position a forward-only mount opens at: its origin (timeline offset).</summary>
    public static double MountSourceStart(PlaybackPlan plan, double sourceStart) =>
        IsProgressive(plan) ? PlaybackTimeline.From(plan.Timeline).TimelineOffsetSeconds : sourceStart;

    /// <summary>Playing again after a pause this long reopens the stream at the paused position.</summary>
    public static bool ShouldReopenAfterIdle(PlaybackPlan? plan, long? pausedAtMilliseconds, long nowMilliseconds) =>
        IsProgressive(plan) && pausedAtMilliseconds is { } paused && nowMilliseconds - paused >= IdleReopenMilliseconds;

    /// <summary>
    /// A failure replan after a progressive mount failed: never offer the forward-only executor again
    /// in this attempt. HLS (or the original) carries the replacement, as before progressive existed.
    /// </summary>
    public static PlaybackStartRequestV3 WithoutProgressive(PlaybackStartRequestV3 start, string reason = "progressive_executor_failed") => start with
    {
        ClientPlaybackContext = start.ClientPlaybackContext with
        {
            Deliveries = start.ClientPlaybackContext.Deliveries.ToDictionary(pair => pair.Key, pair => pair.Key == "progressive"
                ? pair.Value with { Enabled = false, SupportedOnDevice = false, FailureReason = reason }
                : pair.Value),
        },
    };
}
