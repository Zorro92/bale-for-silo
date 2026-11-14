using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

/// <summary>PlaybackSessionManager.kt failure recovery and same-route seek re-anchoring.</summary>
public static class PlaybackReplan
{
    public static PlaybackReplanRequestV3 ForFailure(PlaybackStartRequestV3 start, PlaybackPlan plan, string planAttemptId,
        IReadOnlyList<string> priorAttemptedKeys, int attemptCount, double sourcePositionSeconds, PlaybackFailureV3 failure,
        PlaybackRouteDecision? failedRoute = null)
    {
        // A tone-map create/open/MediaFailed failure is not evidence that HEVC/VP9 or the
        // selected audio codec failed. Withdraw the range promise, keeping decode capabilities.
        if (failedRoute?.ConsoleToneMap == true && failure.Classification != PlaybackAudioSelectionException.Classification)
            return ForToneMapFailure(start, plan, planAttemptId, priorAttemptedKeys, attemptCount, sourcePositionSeconds);
        if (failure.Classification == PlaybackAudioSelectionException.Classification)
        {
            if (failure.AudioDecoderUnsupported)
            {
                // The mounted track exists but its decoder cannot take it. Packaging alone cannot
                // repair that: withdraw this audio decode codec so the server converts it (AAC).
                var codec = SoftwareDecodePolicy.NormalizeCodec(failedRoute?.AudioCodec ?? plan.Source?.AudioCodec);
                start = start with
                {
                    ClientCapabilities = start.ClientCapabilities with
                    {
                        CodecsAudio = start.ClientCapabilities.CodecsAudio.Where(value => SoftwareDecodePolicy.NormalizeCodec(value) != codec).ToArray(),
                    },
                    ClientPlaybackContext = start.ClientPlaybackContext with
                    {
                        Deliveries = start.ClientPlaybackContext.Deliveries.ToDictionary(pair => pair.Key, pair => pair.Value with
                        {
                            AudioDecodeCodecs = pair.Value.AudioDecodeCodecs.Where(value => SoftwareDecodePolicy.NormalizeCodec(value) != codec).ToArray(),
                        }),
                    },
                };
            }
            // A source-inventory/selection failure is not proof that the video decoder failed.
            // Ask the server to package the SAME chosen audio instead of retrying an original
            // we could not map. Mapping failures keep decode evidence; decoder failures above
            // narrow only audio. Both keep passthrough evidence and the selected track identity.
            var context = start.ClientPlaybackContext;
            start = start with
            {
                ClientPlaybackContext = context with
                {
                    Deliveries = context.Deliveries.ToDictionary(pair => pair.Key, pair => pair.Key == "original_http"
                        ? pair.Value with
                        {
                            Enabled = false,
                            SupportedOnDevice = false,
                            FailureReason = PlaybackAudioSelectionException.Classification,
                            ValidatedClaims = pair.Value.ValidatedClaims.Where(claim => claim != PlaybackProtocol.ClientSelectedAudioTrackClaim).ToArray(),
                        } : pair.Value),
                },
            };
        }
        else if (failedRoute is { UseFfmpeg: true })
        {
            // Do not offer the failing local decoder again: make the server adapt the affected streams.
            start = SoftwareDecodePolicy.WithoutFailedFallbacks(start, failedRoute);
            failure = new("decoder_failure", "FFmpegInteropX failed; server transcode required. " + failure.Message, "FFmpegInteropX");
        }
        return Build(start, plan, planAttemptId, priorAttemptedKeys.Append(plan.PlanAttemptKey!).ToArray(),
            attemptCount, sourcePositionSeconds, "failure_recovery", failure);
    }

    public static PlaybackReplanRequestV3 ForSeek(PlaybackStartRequestV3 start, PlaybackPlan plan, string planAttemptId,
        IReadOnlyList<string> priorAttemptedKeys, int attemptCount, double sourcePositionSeconds)
    {
        // apiv2.PlaybackReplanBody and ReplanRequestV3.Validate: this counts mounted plans,
        // starting at one (PlaybackSessionManager.kt newActiveAttempt), not failed recoveries.
        if (attemptCount is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(attemptCount));
        if (!double.IsFinite(sourcePositionSeconds) || sourcePositionSeconds < 0 || sourcePositionSeconds > 31_536_000)
            throw new ArgumentOutOfRangeException(nameof(sourcePositionSeconds));
        // reanchorActiveVideoSession: a seek must not spend failure-recovery budget or blacklist
        // the working route. Retain its identity, tracks, capabilities and quality preferences.
        return Build(start, plan, planAttemptId, priorAttemptedKeys, attemptCount, sourcePositionSeconds, "seek_reanchor", null);
    }

    private static PlaybackReplanRequestV3 Build(PlaybackStartRequestV3 start, PlaybackPlan plan, string planAttemptId,
        IReadOnlyList<string> priorAttemptedKeys, int attemptCount, double sourcePositionSeconds, string operation, PlaybackFailureV3? failure)
    {
        if (string.IsNullOrWhiteSpace(plan.PlanId) || string.IsNullOrWhiteSpace(plan.PlanAttemptKey))
            throw new InvalidDataException("The playback plan has no recovery identity.");
        var attemptedKeys = priorAttemptedKeys.Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).ToArray();
        var tracks = new SelectedPlaybackTracksV3(
            Track(plan.SelectedTracks?.Audio, start.AudioTrackId, start.AudioTrackIndex),
            Track(plan.SelectedTracks?.Subtitle, start.SubtitleTrackId, start.SubtitleTrackIndex, subtitle: true));
        return new(start.PlaybackAttemptId, Guid.NewGuid().ToString(), plan.PlanId, planAttemptId, plan.PlanAttemptKey,
            attemptedKeys, attemptCount, double.IsFinite(sourcePositionSeconds) ? Math.Max(0, sourcePositionSeconds) : 0,
            tracks, start.ClientFeatures, start.ClientCapabilities, start.ClientPlaybackContext)
        {
            Operation = operation,
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
        start = WithoutDynamicRangeClaim(start);
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

    private static PlaybackStartRequestV3 WithoutDynamicRangeClaim(PlaybackStartRequestV3 start) => start with
    {
        ClientPlaybackContext = start.ClientPlaybackContext with
        {
            Deliveries = start.ClientPlaybackContext.Deliveries.ToDictionary(pair => pair.Key, pair => pair.Value with
            {
                ValidatedClaims = pair.Value.ValidatedClaims.Where(claim => claim != PlaybackProtocol.ClientManagedDynamicRangeClaim).ToArray(),
            }),
        },
    };

    public static PlaybackReplanRequestV3 ForToneMapFailure(PlaybackStartRequestV3 start, PlaybackPlan plan, string planAttemptId,
        IReadOnlyList<string> priorAttemptedKeys, int attemptCount, double sourcePositionSeconds) =>
        Build(WithoutDynamicRangeClaim(start), plan, planAttemptId, priorAttemptedKeys.Append(plan.PlanAttemptKey!).ToArray(),
            attemptCount, sourcePositionSeconds, "failure_recovery",
            new("player_failure", "Console tone mapping failed; server tone mapping required."));

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
