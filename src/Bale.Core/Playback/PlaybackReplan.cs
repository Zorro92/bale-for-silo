using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

/// <summary>PlaybackSessionManager.kt failure recovery and same-route seek re-anchoring.</summary>
public static class PlaybackReplan
{
    public static PlaybackReplanRequestV3 ForFailure(PlaybackStartRequestV3 start, PlaybackPlan plan, string planAttemptId,
        IReadOnlyList<string> priorAttemptedKeys, int attemptCount, double sourcePositionSeconds, PlaybackFailureV3 failure,
        PlaybackRouteDecision? failedRoute = null)
    {
        // playback-protocol-v3.md "A confirmed native selection failure": only the subtitle failed, so the
        // video and audio route stays offered as is (no codec, delivery, DV7 or range withdrawal). The server
        // itself drops native selection for the rest of the attempt; capabilities and features are resent
        // unchanged, as silo-apple's protocolV3LoadFailureRecovery does.
        if (PlaybackEmbeddedSubtitleFailure.Is(failure))
            return Build(start, plan, planAttemptId, priorAttemptedKeys.Append(plan.PlanAttemptKey!).ToArray(),
                attemptCount, sourcePositionSeconds, "failure_recovery", failure);
        // A progressive mount that failed is not offered again: the next plan uses HLS (or the
        // original) as before progressive existed. Seeks and reconnects never come through here.
        if (PlaybackProgressive.IsProgressive(plan)) start = PlaybackProgressive.WithoutProgressive(start);
        // silo-apple b9446d5d: a mount that ran out of its start budget says nothing about a decoder, a console
        // stage or an audio track (a slow or hung source fails the same way), so every claim stays and the
        // failure keeps its classification. The server excludes the failed route by its attempted key and
        // offers the next one, as for Apple's noPlayableTrackWithinBudget. A seek that never landed, even after the
        // same plan was remounted at its target, fails the same way (Aether's softwarePipelineFailed /
        // nativeItemFailed, both "server can adapt" in silo-apple, which resends its capabilities unchanged): a
        // pipeline that stopped repositioning says nothing about one codec, stage or track.
        if (PlaybackStartBudget.Is(failure) || PlaybackSeekWatchdog.Is(failure))
            return Build(start, plan, planAttemptId, priorAttemptedKeys.Append(plan.PlanAttemptKey!).ToArray(),
                attemptCount, sourcePositionSeconds, "failure_recovery", failure);
        if (DolbyVision7Policy.RequiresBaseLayer(plan))
        {
            start = WithoutDolbyVision7(start);
            if (failure.Classification != PlaybackAudioSelectionException.Classification)
                return Build(start, plan, planAttemptId, priorAttemptedKeys.Append(plan.PlanAttemptKey!).ToArray(),
                    attemptCount, sourcePositionSeconds, "failure_recovery",
                    new("player_failure", "DV7 base-layer playback failed; server conversion required."));
        }
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

    /// <summary>
    /// A mid-playback audio or subtitle pick (TvPlayerViewModel.kt selectAudioOption / selectSubtitleOption):
    /// the same session at the current position with the new track. <paramref name="audioIndex"/> null keeps
    /// the audio; <paramref name="subtitleIndex"/> null keeps the subtitle, -1 turns it off. Indexes are the
    /// catalog's (audio ordinal, combined external-first subtitle index), as in the start request.
    /// </summary>
    public static PlaybackReplanRequestV3 ForTrackChange(PlaybackStartRequestV3 start, PlaybackPlan plan, string planAttemptId,
        IReadOnlyList<string> priorAttemptedKeys, int attemptCount, double sourcePositionSeconds, int? audioIndex, int? subtitleIndex)
    {
        if (attemptCount is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(attemptCount));
        if (audioIndex is < 0 || subtitleIndex is < -1) throw new ArgumentOutOfRangeException(audioIndex is < 0 ? nameof(audioIndex) : nameof(subtitleIndex));
        var audio = audioIndex is { } a ? new PlaybackTrackIdentityV3($"file:{start.FileId}:audio:{a}", a)
            : Track(plan.SelectedTracks?.Audio, start.AudioTrackId, start.AudioTrackIndex);
        var subtitle = subtitleIndex switch
        {
            null => Track(plan.SelectedTracks?.Subtitle, start.SubtitleTrackId, start.SubtitleTrackIndex, subtitle: true),
            < 0 => null,
            var s => new PlaybackTrackIdentityV3($"file:{start.FileId}:subtitle:{s}", s),
        };
        return Build(start, plan, planAttemptId, priorAttemptedKeys, attemptCount, sourcePositionSeconds, "track_change", null,
            new SelectedPlaybackTracksV3(audio, subtitle));
    }

    /// <summary>
    /// The buffering-quality controller's <c>quality_change</c> (silo-server buffering-quality design, "Lower
    /// automatically" / "Ask first" / Restore quality): the same session, tracks and position at the quality and
    /// estimate the controller chose. Nothing failed, so no attempted key is added and no failure budget is spent.
    /// </summary>
    public static PlaybackReplanRequestV3 ForQualityChange(PlaybackStartRequestV3 start, PlaybackPlan plan, string planAttemptId,
        IReadOnlyList<string> priorAttemptedKeys, int attemptCount, double sourcePositionSeconds, BufferingQualityReplan change)
    {
        if (attemptCount is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(attemptCount));
        ArgumentNullException.ThrowIfNull(change);
        return Build(start, plan, planAttemptId, priorAttemptedKeys, attemptCount, sourcePositionSeconds, change.Operation, null) with
        {
            QualityPreference = change.QualityPreference,
            BandwidthEstimateKbps = change.BandwidthEstimateKbps,
            BandwidthEstimateSource = change.BandwidthEstimateSource,
            ObservedThroughputKbps = change.ObservedThroughputKbps,
            LearnedLimit = change.LearnedLimit,
        };
    }

    /// <summary>
    /// A replan the player sends for its own reasons (track change, seek reanchor, failure recovery) after a lowering:
    /// the controller's quality and, on Auto, the kept step-down estimate without a source, so the lowering holds where
    /// no learned limit applies ("Later replans after a lowering"). Null leaves the request unchanged.
    /// </summary>
    public static PlaybackReplanRequestV3 WithClientReplan(PlaybackReplanRequestV3 request, BufferingQualityReplan? client) =>
        client is null ? request : request with
        {
            QualityPreference = client.QualityPreference,
            BandwidthEstimateKbps = client.BandwidthEstimateKbps,
            BandwidthEstimateSource = null,
            ObservedThroughputKbps = null,
        };

    /// <summary>
    /// A subtitle pick's replan answer that leaves the mounted stream alone (InPlacePlanAdoption.kt
    /// mediaMountKey; silo-apple's report replan that "keeps the stream for the answer"): same delivery, stream,
    /// file, audio track, recipe and timeline origin, and no burn-in on either side (burn-in changes the video).
    /// Bale draws every rendered subtitle itself, so such an answer swaps only the overlay: no remount, no pause.
    /// </summary>
    public static bool KeepsTransport(PlaybackPlan playing, PlaybackPlan answer) => TransportChange(playing, answer) is null;

    /// <summary>The first thing the answer changes about the mounted stream (a fixed field name for logs), or null.</summary>
    public static string? TransportChange(PlaybackPlan playing, PlaybackPlan answer)
    {
        if (playing.Subtitle?.Mode == "burn_in" || answer.Subtitle?.Mode == "burn_in") return "burn_in";
        if (!Same(playing.Delivery, answer.Delivery) || answer.Delivery is null) return "delivery";
        if (playing.Stream is not { } a || answer.Stream is not { } b || string.IsNullOrEmpty(b.Url)) return "stream";
        // The server signs a fresh stream token on every plan (?st=...); the path names the stream.
        if (!Same(UrlPath(a.Url), UrlPath(b.Url))) return "stream.url";
        if (!Same(a.Protocol, b.Protocol)) return "stream.protocol";
        if (!Same(a.Container, b.Container)) return "stream.container";
        if (!Same(a.MimeType, b.MimeType)) return "stream.mime_type";
        if (!Same(playing.Source?.MediaFileId, answer.Source?.MediaFileId)) return "source.media_file_id";
        if (playing.SelectedTracks?.Audio?.Index != answer.SelectedTracks?.Audio?.Index) return "selected_tracks.audio";
        var (x, y) = (playing.EffectiveRecipe, answer.EffectiveRecipe);
        if ((x is null) != (y is null)) return "effective_recipe";
        if (x is not null && y is not null)
        {
            if (!Same(x.VideoCodec, y.VideoCodec) || x.Width != y.Width || x.Height != y.Height || !Same(x.DynamicRange, y.DynamicRange))
                return "effective_recipe.video";
            if (!Same(x.AudioCodec, y.AudioCodec) || x.AudioChannels != y.AudioChannels || !Same(x.AudioLayout, y.AudioLayout))
                return "effective_recipe.audio";
            if (x.BitrateKbps != y.BitrateKbps) return "effective_recipe.bitrate";
        }
        if (playing.Timeline?.StreamOriginSeconds != answer.Timeline?.StreamOriginSeconds) return "timeline.stream_origin";
        if (playing.Timeline?.TimelineOffsetSeconds != answer.Timeline?.TimelineOffsetSeconds) return "timeline.offset";
        return null;

        static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);
        static string? UrlPath(string? url) => url?.Split('?', '#')[0];
    }

    /// <summary>
    /// TvPlayerViewModel.kt selectAudioOption / AudioReconcile.kt mountedAudioFor: a direct play of the original
    /// file mounts every audio track, so another one is selected on the player instead of replanning. A server
    /// remux or transcode maps only its chosen stream (remux.go / transcode.go -map 0:a:N?) and still replans.
    /// </summary>
    public static bool SwitchesAudioLocally(PlaybackPlan? plan, int audioIndex) =>
        audioIndex >= 0 && string.Equals(plan?.Delivery, "original_http", StringComparison.Ordinal)
        && plan?.SelectedTracks?.Audio?.Index != audioIndex;

    /// <summary>
    /// Records a local audio switch on the plan and start request, as a committed replan would: the pills, Info and
    /// later replans (seek reanchor, failure fallback) then name the track that plays. Subtitles are untouched.
    /// </summary>
    public static PlaybackStartRequestV3 WithLocalAudio(PlaybackStartRequestV3 start, PlaybackPlan plan, int audioIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(audioIndex);
        var id = $"file:{start.FileId}:audio:{audioIndex}";
        plan.SelectedTracks ??= new SelectedTracksV3();
        plan.SelectedTracks.Audio = new TrackIdentityV3 { Id = id, Index = audioIndex };
        return start with { AudioTrackId = id, AudioTrackIndex = audioIndex };
    }

    /// <summary>The start request the session now plays: a committed track change replaces its tracks.</summary>
    public static PlaybackStartRequestV3 WithTracks(PlaybackStartRequestV3 start, SelectedPlaybackTracksV3 tracks) => start with
    {
        AudioTrackId = tracks.Audio?.Id,
        AudioTrackIndex = tracks.Audio?.Index,
        SubtitleTrackId = tracks.Subtitle?.Id,
        SubtitleTrackIndex = tracks.Subtitle?.Index,
    };

    private static PlaybackReplanRequestV3 Build(PlaybackStartRequestV3 start, PlaybackPlan plan, string planAttemptId,
        IReadOnlyList<string> priorAttemptedKeys, int attemptCount, double sourcePositionSeconds, string operation, PlaybackFailureV3? failure,
        SelectedPlaybackTracksV3? tracksOverride = null)
    {
        if (string.IsNullOrWhiteSpace(plan.PlanId) || string.IsNullOrWhiteSpace(plan.PlanAttemptKey))
            throw new InvalidDataException("The playback plan has no recovery identity.");
        var attemptedKeys = priorAttemptedKeys.Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).ToArray();
        var tracks = tracksOverride ?? new SelectedPlaybackTracksV3(
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
        start = WithoutDolbyVision7(start);
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
        return Build(start, plan, planAttemptId, priorAttemptedKeys.Append(plan.PlanAttemptKey!).ToArray(),
            attemptCount, sourcePositionSeconds, "failure_recovery",
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

    private static PlaybackStartRequestV3 WithoutDolbyVision7(PlaybackStartRequestV3 start)
    {
        start = WithoutDynamicRangeClaim(start);
        return start with
        {
            ClientPlaybackContext = start.ClientPlaybackContext with
            {
                Deliveries = start.ClientPlaybackContext.Deliveries.ToDictionary(pair => pair.Key, pair => pair.Value with
                {
                    Transformations = pair.Value.Transformations.Where(transform => transform.Name != PlaybackProtocol.ClientDolbyVision7ToHdr10).ToArray(),
                }),
            },
        };
    }

    public static PlaybackReplanRequestV3 ForToneMapFailure(PlaybackStartRequestV3 start, PlaybackPlan plan, string planAttemptId,
        IReadOnlyList<string> priorAttemptedKeys, int attemptCount, double sourcePositionSeconds) =>
        Build(WithoutDolbyVision7(start), plan, planAttemptId, priorAttemptedKeys.Append(plan.PlanAttemptKey!).ToArray(),
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
