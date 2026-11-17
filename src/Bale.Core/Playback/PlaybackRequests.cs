using System.Text.Json.Serialization;

namespace Bale.Core.Playback;

/// <summary>shared/src/commonMain/kotlin/org/siloserver/silo/model/playback/PlaybackProtocolV3.kt.</summary>
public static class PlaybackProtocol
{
    public const int Version = 3;
    public const string SequencedProgressFeature = "sequenced_progress_v1";

    public const string SoftwareVideoDecodeFeature = "software_video_decode_v1";
    public const string ClientVideoTransformationsFeature = "client_video_transformations_v1";
    public const string ClientDolbyVision7ToHdr10 = "client_dv7_to_hdr10";
    /// <summary>silo-server ClaimClientManagedDynamicRangeV3: only original_http maps source range onto the active output.</summary>
    public const string ClientManagedDynamicRangeClaim = "client_managed_dynamic_range_v1";
    public const string ClientManagedDynamicRangeReason = "client_managed_dynamic_range";
    public const string ClientSelectedAudioTrackClaim = "client_selected_audio_track_v1";
    /// <summary>
    /// silo-server protocol_v3.go ClaimClientDV8BaseLayerFallbackV3, original_http only: a single-layer
    /// Dolby Vision profile 8 file plays unchanged through an ordinary HEVC decoder as its base layer.
    /// The server keeps every other gate (capabilities_v3.go clientDV8BaseLayerFallbackV3): profile 8,
    /// no enhancement layer, a proven base layer whose dv_bl_compat_id maps to HDR10 (1), SDR (2) or
    /// HLG (4), that range on the active output, and an HEVC decode entry that fits.
    /// </summary>
    public const string ClientDolbyVision8BaseLayerClaim = "client_dv8_base_layer_fallback_v1";
    /// <summary>The decision reason (plan_v3.go decisionReasonClientDV8BaseLayerV3) of such a plan.</summary>
    public const string ClientDolbyVision8BaseLayerReason = "client_dv8_base_layer";
    /// <summary>External SubRip arrives as the stored .srt instead of the server's WebVTT conversion.</summary>
    public const string SubripSidecarFeature = "subrip_sidecar_v1";
    /// <summary>
    /// silo-server protocol_v3.go FeatureHeaderAuthenticatedMediaV3 (attempt-sticky): plan media, HLS, subtitle and
    /// font URLs carry no signed credential, and the client sends its own Authorization on every media request.
    /// Bale already does: PlaybackMedia fetches every byte through the authenticated client (the Rust transports
    /// with NativeHeaderRefresh), and PlaybackMediaUrl accepts only API-relative media routes. Without it the server
    /// may answer with signed absolute proxy URLs, which PlaybackMediaUrl refuses. ApplePlaybackV3Capabilities.features.
    /// </summary>
    public const string HeaderAuthenticatedMediaFeature = "header_authenticated_media_v1";
    /// <summary><c>learned_limit</c>: plan without the device's learned limit and leave it stored.</summary>
    public const string LearnedLimitIgnore = "ignore";
    /// <summary><c>learned_limit</c>: delete the learned limit, then plan without it (Restore quality).</summary>
    public const string LearnedLimitClear = "clear";

    /// <summary>
    /// The <c>learned_limit</c> a start or replan carries. An explicit choice wins (Restore quality sends
    /// <c>clear</c>); otherwise <c>never</c> mode sends <c>ignore</c> on every request, so a limit learned on this
    /// device does not reach a profile that turned lowering off, and the other modes send nothing (the limit
    /// applies). silo-server refuses the field unless client_features names buffering_quality_v1, so without it
    /// nothing is sent (silo-apple PlaybackV3BufferingQualityRequest, silo-android PlaybackSessionManager
    /// modeLearnedLimit / BufferingQualityRequestGating).
    /// </summary>
    public static string? LearnedLimit(IReadOnlyList<string> clientFeatures, Settings.BufferingQualityMode mode, string? explicitChoice = null)
    {
        if (!clientFeatures.Contains(ConnectionSpeed.BufferingQualityFeature)) return null;
        return explicitChoice ?? (mode == Settings.BufferingQualityMode.Never ? LearnedLimitIgnore : null);
    }

    /// <summary>
    /// Start-time features that depend on what the server offers. silo-server protocol_v3.go makes
    /// subrip_sidecar_v1 attempt-sticky and playback_service.go keeps it across replans from the
    /// attempt record, so it is added to the start only, and only for a server that advertises it and
    /// a client that claimed text sidecars.
    /// </summary>
    public static IReadOnlyList<string> NegotiatedStartFeatures(PlaybackStartRequestV3 request, IReadOnlyList<string>? serverFeatures)
    {
        if (serverFeatures?.Contains(SubripSidecarFeature) != true || request.ClientFeatures.Contains(SubripSidecarFeature)
            || !request.ClientPlaybackContext.Deliveries.Values.Any(delivery => delivery.Subtitles.SidecarText))
            return request.ClientFeatures;
        return request.ClientFeatures.Append(SubripSidecarFeature).ToArray();
    }

    /// <summary>
    /// A replan's client_features: the request's own list plus what its attempt's start negotiated with the server
    /// (buffering_quality_v1, subrip_sidecar_v1). silo-server's non-seek replans replace the durable request's
    /// features with the replan's list (playback_v3.go), and applyConnectionReading / applyLearnedLimit act only
    /// when it names buffering_quality_v1, so a track change or failure recovery that dropped it lost the
    /// connection's Auto budget the start was planned under. Attempt-sticky features are pinned server-side
    /// whatever is sent (PinAttemptStickyFeaturesV3). silo-apple sends buffering_quality_v1 on replans too
    /// (PlaybackSessionBridge replanProtocolV3).
    /// </summary>
    internal static IReadOnlyList<string> ReplanFeatures(PlaybackReplanRequestV3 request, NegotiatedStartFeatures? negotiated)
    {
        if (negotiated is null || negotiated.PlaybackAttemptId != request.PlaybackAttemptId) return request.ClientFeatures;
        var missing = negotiated.Added.Where(feature => !request.ClientFeatures.Contains(feature)).ToArray();
        return missing.Length == 0 ? request.ClientFeatures : request.ClientFeatures.Concat(missing).ToArray();
    }

    public static IReadOnlyList<string> SoloClientFeatures(ClientPlaybackContext context,
        IReadOnlyList<VideoDecodeCapability>? videoDecode = null)
    {
        // playbackClientFeaturesV3: these describe the protocol, not Android-only engine abilities.
        var features = new List<string>
        {
            "playback_plan_v3", ClientVideoTransformationsFeature, "device_quirks_v1",
            "seek_reanchor_v1", "direct_stream_resume_v1", HeaderAuthenticatedMediaFeature,
        };
        if (context.Deliveries.TryGetValue("original_http", out var original) && original.Subtitles.NativeEmbedded.Count > 0)
            features.Add("embedded_subtitles_v1");
        if (context.Output.AudioPassthrough.Entries.Count > 0) features.Add("layout_aware_passthrough");
        // silo-server capabilities_v3.go videoEligibleV3 ignores non-hardware decode entries unless the
        // client opts in. The measured dav1d AV1 entry supplies this in production.
        if (videoDecode?.Any(decoder => !decoder.Hardware) == true) features.Add(SoftwareVideoDecodeFeature);
        // Native Media3 HLS recipes/renditions are not evidence for the Windows player.
        return features.AsReadOnly();
    }
}

/// <summary>
/// PlaybackProtocolV3.kt plus network/apiv2/PlaybackV2Api.kt's v2Body: file IDs stay opaque strings.
/// InstallationId is added by PlaybackClient, not inferred from X-Silo-Device-Id.
/// </summary>
public sealed record PlaybackStartRequestV3(
    string FileId,
    string ProfileId,
    string PlaybackAttemptId,
    IReadOnlyList<string> ClientFeatures,
    ClientCodecCapabilities ClientCapabilities,
    ClientPlaybackContext ClientPlaybackContext)
{
    public string? InstallationId { get; init; }
    public int ProtocolVersion { get; init; } = PlaybackProtocol.Version;
    public string QualityPreference { get; init; } = "auto";
    public string SubtitleFidelityPreference { get; init; } = "preserve";
    public string ProgressPersistence { get; init; } = "server";
    public double? StartPosition { get; init; }
    public string? AudioTrackId { get; init; }
    public int? AudioTrackIndex { get; init; }
    public string? SubtitleTrackId { get; init; }
    public int? SubtitleTrackIndex { get; init; }
    public bool Metered { get; init; }
    public int? BandwidthCapKbps { get; init; }
    /// <summary>The highest current speed reading (buffering_quality_v1); starts only, never replans.</summary>
    public int? BandwidthEstimateKbps { get; init; }
    public string? BandwidthEstimateSource { get; init; }
    /// <summary><c>ignore</c> / <c>clear</c> with buffering_quality_v1 (<see cref="PlaybackProtocol.LearnedLimit"/>); set by PlaybackClient.</summary>
    public string? LearnedLimit { get; init; }
    public bool? AllowAlternateVersions { get; init; }
}

/// <summary>The features a start added to its builder's list after reading the server's capabilities.</summary>
internal sealed record NegotiatedStartFeatures(string PlaybackAttemptId, IReadOnlyList<string> Added);

public sealed record PlaybackTrackIdentityV3(string Id, int? Index = null);
public sealed record SelectedPlaybackTracksV3(PlaybackTrackIdentityV3? Audio = null, PlaybackTrackIdentityV3? Subtitle = null);
public sealed record PlaybackFailureV3(string Classification, string? Message = null, string? DecoderName = null)
{
    // Local evidence used to narrow the replan's decode claims, not a protocol field.
    [JsonIgnore]
    public bool AudioDecoderUnsupported { get; init; }
}

/// <summary>PlaybackProtocolV3.kt: the caller retains replan IDs and server-minted attempt keys across retries.</summary>
public sealed record PlaybackReplanRequestV3(
    string PlaybackAttemptId,
    string ReplanRequestId,
    string FailedPlanId,
    string PlanAttemptId,
    string PlanAttemptKey,
    IReadOnlyList<string> AttemptedPlanKeys,
    int AttemptCount,
    double PositionSeconds,
    SelectedPlaybackTracksV3 SelectedTracks,
    IReadOnlyList<string> ClientFeatures,
    ClientCodecCapabilities ClientCapabilities,
    ClientPlaybackContext ClientPlaybackContext)
{
    public string? InstallationId { get; init; }
    public int ProtocolVersion { get; init; } = PlaybackProtocol.Version;
    public string? Operation { get; init; }
    public IReadOnlyList<string> LocalMutations { get; init; } = [];
    public string QualityPreference { get; init; } = "auto";
    public bool Metered { get; init; }
    public int? BandwidthCapKbps { get; init; }
    /// <summary>
    /// An explicit <c>clear</c> (Restore quality) wins; otherwise PlaybackClient applies the mode's <c>ignore</c>.
    /// Dropped when the replan does not name buffering_quality_v1.
    /// </summary>
    public string? LearnedLimit { get; init; }
    public PlaybackFailureV3? Failure { get; init; }
}

// Explicit source generation keeps the fallback safe for the Xbox Native AOT consumer.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PlaybackStartRequestV3))]
[JsonSerializable(typeof(PlaybackReplanRequestV3))]
[JsonSerializable(typeof(PlaybackJournalEntry[]))]
internal partial class PlaybackJson : JsonSerializerContext;
