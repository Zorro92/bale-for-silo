using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

/// <summary>
/// A native embedded subtitle selection the player could not mount (silo-server
/// docs/architecture/playback-protocol-v3.md, "A confirmed native selection failure"; silo-apple
/// PlayerViewModel.swift protocolV3LoadFailureRecovery). The recovery is one <c>failure_recovery</c>
/// replan with this classification: the server then disables native selection for the rest of the
/// attempt and answers with the same video route and an executable fallback (extracted sidecar or
/// burn-in). Only deterministic mount checks count, never a sidecar fetch or a network error.
/// </summary>
public static class PlaybackEmbeddedSubtitleFailure
{
    // silo-server internal/playback/protocol_v3.go FailureSubtitleEmbeddedFailedV3.
    public const string Classification = "subtitle_embedded_failed";

    /// <summary>A fixed message; never carries a native exception's text, a URL or a track title.</summary>
    public static PlaybackFailureV3 Failure { get; } = new(Classification, "The selected embedded subtitle could not be mounted.");

    public static bool Is(PlaybackFailureV3? failure) => failure?.Classification == Classification;

    /// <summary>
    /// The plan asks the player to draw a subtitle from the played container itself (subtitle.embedded
    /// with mode render). Sidecars, packaged artifacts, burn-in and Off are never native.
    /// </summary>
    public static bool IsNative(PlaybackPlan? plan) => plan?.Subtitle is { Embedded: not null, Mode: "render" };

    /// <summary>
    /// The fixed stage name when a native plan cannot be mounted as given, else null. Mirrors silo-apple
    /// ApplePlaybackV3PlanAdapter.swift's invalidEmbeddedSubtitle: native selection exists only on
    /// original_http, and the stream index must be one the wrapper accepts (ResolveEmbedded).
    /// </summary>
    public static string? PlanFailure(PlaybackPlan? plan)
    {
        if (!IsNative(plan)) return null;
        if (!string.Equals(plan!.Delivery, "original_http", StringComparison.Ordinal)) return "delivery";
        return PlaybackSubtitleResolver.ResolveEmbedded(plan) is null ? "stream_index" : null;
    }

    /// <summary>
    /// After the FFmpeg source opened: the demuxer created a subtitle provider for the bound stream.
    /// silo-apple AetherPlaybackController.validateEmbeddedSubtitleSelection checks the same inventory;
    /// FFmpegInteropX lists only subtitle streams whose decoder opened.
    /// </summary>
    public static bool Mounted(int streamIndex, IEnumerable<int> demuxedSubtitleStreams) =>
        demuxedSubtitleStreams.Contains(streamIndex);

    /// <summary>
    /// Whether a mount failure of this plan's subtitle sends the recovery replan. It shares the page's
    /// single failure-recovery budget with audio_track_selection_failed and codec failures; once that is
    /// spent the video keeps playing without subtitles (the behaviour before this replan existed), so a
    /// repeated failure in the same attempt can neither loop nor turn into a playback error.
    /// </summary>
    public static bool Replans(PlaybackPlan? plan, bool recoveryUsed) => IsNative(plan) && !recoveryUsed;
}
