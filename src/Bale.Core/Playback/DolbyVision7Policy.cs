using Bale.Core.Api.Models;
using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <summary>
/// silo-server candidate_routes_v3.go: DV7 originals are a client-executed base-layer promise.
/// Implemented in Rust (native/bale-core/src/dolby_vision7.rs).
/// </summary>
public static class DolbyVision7Policy
{
    // The recipe Bale offers; dolby_vision7.rs validates plans against the same name, version and claims.
    public static PlaybackTransformation Transformation { get; } = new(PlaybackProtocol.ClientDolbyVision7ToHdr10, "client", "1")
    {
        ValidatedClaims = Array.AsReadOnly(new[]
        {
            "dolby_vision_metadata_removed", "hdr10_base_layer_preserved", "enhancement_layer_discarded",
        }),
    };

    /// <summary>
    /// Includes unexpected DV7 originals too: the native container/system-decoder path must never
    /// take one just because a server omitted its reason or changed its range wording.
    /// </summary>
    public static bool RequiresBaseLayer(PlaybackPlan plan) =>
        BaleCoreNative.Call("dolby_vision7.requires_base_layer", writer => writer.Server("plan", plan), BaleCoreJson.Default.Boolean);

    public static void ValidatePlan(PlaybackPlan plan) =>
        BaleCoreNative.Checked(BaleCoreNative.Call("dolby_vision7.validate_plan", writer => writer.Server("plan", plan),
            BaleCoreJson.Default.BaleCoreCheckedBoolean));

    /// <summary>
    /// Silo describes the first playable video in demux order (capabilities_v3.go), not FFmpeg's
    /// preferred/default stream. Check the complete inventory so a missing BL cannot select an EL.
    /// </summary>
    public static int BaseLayerTrackIndex(IReadOnlyList<int> streamIndices, int? expectedVideoCount) =>
        BaleCoreNative.Checked(BaleCoreNative.Call("dolby_vision7.base_layer_track_index", writer =>
        {
            writer.Numbers("stream_indices", streamIndices);
            writer.Number("expected_video_count", expectedVideoCount);
        }, BaleCoreJson.Default.BaleCoreCheckedInt32));
}
