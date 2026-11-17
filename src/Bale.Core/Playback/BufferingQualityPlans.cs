using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

/// <summary>What the buffering-quality controller reads from an adopted protocol-v3 plan (silo-android BufferingQualityPlanMapping.kt).</summary>
public static class BufferingQualityPlans
{
    public const string ActiveQualityFeature = "plan_active_quality_v1";
    public const string QualityReductionUnavailable = "quality_reduction_unavailable";

    /// <param name="adoptionId">Unique per adoption: the server's plan_id can repeat across replans.</param>
    /// <param name="qualityPreference">The preference the plan was requested with; blank reads as auto.</param>
    /// <param name="requestedEstimateKbps">The estimate the plan's start or replan carried, for its summary.</param>
    public static BufferingQualityPlan From(PlaybackPlan plan, string adoptionId, string? qualityPreference,
        IReadOnlyList<string>? serverFeatures, int? requestedEstimateKbps)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // The spec's delivered bitrate: effective_recipe.bitrate_kbps, else source.bitrate_kbps.
        var delivered = Kbps(plan.EffectiveRecipe?.BitrateKbps) ?? Kbps(plan.Source?.BitrateKbps);
        var rungs = (plan.AvailableQualities ?? [])
            .Where(rung => !string.IsNullOrWhiteSpace(rung.Label) && Kbps(rung.BitrateKbps) is not null)
            .Select(rung => new BufferingQualityRung(rung.Label!, Kbps(rung.BitrateKbps)!.Value)).ToArray();
        var quality = string.IsNullOrWhiteSpace(qualityPreference) || qualityPreference == "original"
            ? BufferingQualityController.AutoQuality : qualityPreference;
        return new(adoptionId, delivered, quality, rungs,
            plan.DegradationWarnings?.Any(warning => warning.Code == QualityReductionUnavailable) == true)
        {
            // Only a server that reports it means it (PlaybackExecutionPlan activeQualityReported).
            ActiveQuality = serverFeatures?.Contains(ActiveQualityFeature) == true && !string.IsNullOrWhiteSpace(plan.ActiveQuality)
                ? plan.ActiveQuality : null,
            RequestedBandwidthEstimateKbps = requestedEstimateKbps is > 0 ? requestedEstimateKbps : null,
        };
    }

    private static int? Kbps(long? value) => value is > 0 and <= int.MaxValue ? (int)value.Value : null;
}
