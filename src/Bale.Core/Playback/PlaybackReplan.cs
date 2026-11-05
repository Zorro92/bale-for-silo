using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

/// <summary>PlaybackSessionManager.kt prepareActiveVideoSessionReplan, for failure recovery only.</summary>
public static class PlaybackReplan
{
    public static PlaybackReplanRequestV3 ForFailure(PlaybackStartRequestV3 start, PlaybackPlan plan, string planAttemptId,
        IReadOnlyList<string> priorAttemptedKeys, int attemptCount, double sourcePositionSeconds, PlaybackFailureV3 failure,
        PlaybackRouteDecision? failedRoute = null)
    {
        if (failedRoute is { UseFfmpeg: true })
        {
            // Do not offer the failing local decoder again: make the server adapt the affected streams.
            start = SoftwareDecodePolicy.WithoutFailedFallbacks(start, failedRoute);
            failure = new("decoder_failure", "FFmpegInteropX failed; server transcode required. " + failure.Message, "FFmpegInteropX");
        }
        if (string.IsNullOrWhiteSpace(plan.PlanId) || string.IsNullOrWhiteSpace(plan.PlanAttemptKey))
            throw new InvalidDataException("The playback plan has no recovery identity.");
        var attemptedKeys = priorAttemptedKeys.Append(plan.PlanAttemptKey)
            .Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).ToArray();
        var tracks = new SelectedPlaybackTracksV3(
            Track(plan.SelectedTracks?.Audio, start.AudioTrackId, start.AudioTrackIndex),
            Track(plan.SelectedTracks?.Subtitle, start.SubtitleTrackId, start.SubtitleTrackIndex, subtitle: true));
        return new(start.PlaybackAttemptId, Guid.NewGuid().ToString(), plan.PlanId, planAttemptId, plan.PlanAttemptKey,
            attemptedKeys, attemptCount, double.IsFinite(sourcePositionSeconds) ? Math.Max(0, sourcePositionSeconds) : 0,
            tracks, start.ClientFeatures, start.ClientCapabilities, start.ClientPlaybackContext)
        {
            Operation = "failure_recovery",
            Failure = failure,
            QualityPreference = start.QualityPreference,
            Metered = start.Metered,
            BandwidthCapKbps = start.BandwidthCapKbps,
        };
    }

    public static PlaybackReplanRequestV3 ForHdrOutputFailure(PlaybackStartRequestV3 start, PlaybackPlan plan, string planAttemptId,
        IReadOnlyList<string> priorAttemptedKeys, int attemptCount, double sourcePositionSeconds)
    {
        // An enumerated HDR mode is not usable if the output switch fails. Withdraw every copy
        // of the claim so the server tone-maps instead of retrying HDR on an SDR HDMI signal.
        var hdr = new HdrCapabilities();
        var context = start.ClientPlaybackContext;
        start = start with
        {
            ClientCapabilities = start.ClientCapabilities with { Hdr = false, HdrDetails = hdr },
            ClientPlaybackContext = context with
            {
                Output = context.Output with { HdrDetails = hdr, Display = new("unknown", hdr) },
                Deliveries = context.Deliveries.ToDictionary(pair => pair.Key, pair => pair.Value with { HdrDetails = hdr }),
            },
        };
        return ForFailure(start, plan, planAttemptId, priorAttemptedKeys, attemptCount, sourcePositionSeconds,
            new("player_failure", "HDR output switch failed; server tone mapping required."));
    }

    public static PlaybackFailureV3 Classify(int? hresult, bool isDecoderError, bool isNetworkError) => new(
        isDecoderError ? "decoder_failure" : isNetworkError ? "http_failure" : "player_failure",
        hresult is { } code ? $"MediaFailed 0x{code:X8}" : "MediaFailed");

    private static PlaybackTrackIdentityV3? Track(TrackIdentityV3? selected, string? fallbackId, int? fallbackIndex, bool subtitle = false)
    {
        var index = selected?.Index ?? fallbackIndex;
        if (subtitle && index is < 0) return null;
        var id = selected?.Id ?? fallbackId;
        return string.IsNullOrWhiteSpace(id) ? null : new(id, index is { } value ? checked((int)value) : null);
    }
}
