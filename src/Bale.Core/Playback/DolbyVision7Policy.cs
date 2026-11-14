using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

/// <summary>silo-server candidate_routes_v3.go: DV7 originals are a client-executed base-layer promise.</summary>
public static class DolbyVision7Policy
{
    public static PlaybackTransformation Transformation { get; } = new(PlaybackProtocol.ClientDolbyVision7ToHdr10, "client", "1")
    {
        ValidatedClaims = Array.AsReadOnly(new[]
        {
            "dolby_vision_metadata_removed", "hdr10_base_layer_preserved", "enhancement_layer_discarded",
        }),
    };

    // Include unexpected DV7 originals too: the native container/system-decoder path must never
    // take one just because a server omitted its reason or changed its range wording.
    public static bool RequiresBaseLayer(PlaybackPlan plan) =>
        string.Equals(plan.Delivery, "original_http", StringComparison.OrdinalIgnoreCase)
        && (plan.Source?.DolbyVisionProfile == 7
            || string.Equals(plan.DecisionReason, PlaybackProtocol.ClientDolbyVision7ToHdr10, StringComparison.OrdinalIgnoreCase)
            || plan.Transformations?.Any(transform => transform.Executor == "client"
                && transform.Name == PlaybackProtocol.ClientDolbyVision7ToHdr10) == true);

    public static void ValidatePlan(PlaybackPlan plan)
    {
        if (plan.Source?.DolbyVisionProfile != 7 || SoftwareDecodePolicy.NormalizeCodec(plan.Source.VideoCodec) != "hevc")
            throw new InvalidDataException("The DV7 base-layer source is unproven.");
        var recipes = plan.Transformations?.Where(transform => transform.Executor == "client").ToArray() ?? [];
        if (recipes.Length > 1 || recipes.Any(transform => transform.Name != Transformation.Name || transform.RecipeVersion != Transformation.RecipeVersion
                || transform.ValidatedClaims is not { } claims || claims.Count != Transformation.ValidatedClaims.Count
                || !claims.Order().SequenceEqual(Transformation.ValidatedClaims.Order()))
            || string.Equals(plan.DecisionReason, Transformation.Name, StringComparison.OrdinalIgnoreCase) && recipes.Length != 1)
            throw new InvalidDataException("The DV7 client recipe is unsupported.");
    }

    /// <summary>
    /// Silo describes the first playable video in demux order (capabilities_v3.go), not FFmpeg's
    /// preferred/default stream. Check the complete inventory so a missing BL cannot select an EL.
    /// </summary>
    public static int BaseLayerTrackIndex(IReadOnlyList<int> streamIndices, int? expectedVideoCount)
    {
        if (expectedVideoCount is not > 0 || streamIndices.Count != expectedVideoCount
            || streamIndices.Any(index => index < 0) || streamIndices.Distinct().Count() != streamIndices.Count)
            throw new InvalidDataException("The DV7 base-layer inventory is incomplete.");
        return streamIndices.Select((stream, index) => (stream, index)).MinBy(pair => pair.stream).index;
    }
}
