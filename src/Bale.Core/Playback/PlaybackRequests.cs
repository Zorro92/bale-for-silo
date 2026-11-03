using System.Text.Json.Serialization;

namespace Bale.Core.Playback;

/// <summary>shared/src/commonMain/kotlin/org/siloserver/silo/model/playback/PlaybackProtocolV3.kt.</summary>
public static class PlaybackProtocol
{
    public const int Version = 3;
    public const string SequencedProgressFeature = "sequenced_progress_v1";

    public static IReadOnlyList<string> SoloClientFeatures(ClientPlaybackContext context)
    {
        // playbackClientFeaturesV3: these describe the protocol, not Android-only engine abilities.
        var features = new List<string>
        {
            "playback_plan_v3", "client_video_transformations_v1", "device_quirks_v1",
            "seek_reanchor_v1", "direct_stream_resume_v1",
        };
        if (context.Deliveries.TryGetValue("original_http", out var original) && original.Subtitles.NativeEmbedded.Count > 0)
            features.Add("embedded_subtitles_v1");
        if (context.Output.AudioPassthrough.Entries.Count > 0) features.Add("layout_aware_passthrough");
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
    public bool? AllowAlternateVersions { get; init; }
}

public sealed record PlaybackTrackIdentityV3(string Id, int? Index = null);
public sealed record SelectedPlaybackTracksV3(PlaybackTrackIdentityV3? Audio = null, PlaybackTrackIdentityV3? Subtitle = null);
public sealed record PlaybackFailureV3(string Classification, string? Message = null, string? DecoderName = null);

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
    public PlaybackFailureV3? Failure { get; init; }
}

// Explicit source generation keeps the fallback safe for the Xbox Native AOT consumer.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PlaybackStartRequestV3))]
[JsonSerializable(typeof(PlaybackReplanRequestV3))]
internal partial class PlaybackJson : JsonSerializerContext;
