//! Ported from `DolbyVision7Policy`: silo-server candidate_routes_v3.go, DV7 originals are a
//! client-executed base-layer promise.

use crate::dotnet::eq_ignore_case;
use crate::models::PlaybackPlan;
use crate::software_decode::normalize_codec;

/// `PlaybackProtocol.ClientDolbyVision7ToHdr10`, the recipe name and decision reason.
pub const RECIPE: &str = "client_dv7_to_hdr10";
const RECIPE_VERSION: &str = "1";
const VALIDATED_CLAIMS: [&str; 3] = [
    "dolby_vision_metadata_removed",
    "hdr10_base_layer_preserved",
    "enhancement_layer_discarded",
];

/// Includes unexpected DV7 originals too: the native container/system-decoder path must never take
/// one just because a server omitted its reason or changed its range wording.
pub fn requires_base_layer(plan: &PlaybackPlan) -> bool {
    eq_ignore_case(plan.delivery.as_deref(), "original_http")
        && (plan.source.as_ref().and_then(|s| s.dolby_vision_profile) == Some(7)
            || eq_ignore_case(plan.decision_reason.as_deref(), RECIPE)
            || plan.transformations.as_ref().is_some_and(|all| {
                all.iter().any(|t| {
                    t.executor.as_deref() == Some("client") && t.name.as_deref() == Some(RECIPE)
                })
            }))
}

pub fn validate_plan(plan: &PlaybackPlan) -> Result<bool, &'static str> {
    let source = plan.source.as_ref();
    if source.and_then(|s| s.dolby_vision_profile) != Some(7)
        || normalize_codec(source.and_then(|s| s.video_codec.as_deref())).as_deref() != Some("hevc")
    {
        return Err("The DV7 base-layer source is unproven.");
    }
    let recipes: Vec<_> = plan
        .transformations
        .iter()
        .flatten()
        .filter(|t| t.executor.as_deref() == Some("client"))
        .collect();
    // C# compares the claims as sorted sequences; sorted ordinal multisets agree for real names.
    let expected = sorted(VALIDATED_CLAIMS.iter().map(|c| c.to_string()).collect());
    let unsupported = recipes.iter().any(|t| {
        t.name.as_deref() != Some(RECIPE)
            || t.recipe_version.as_deref() != Some(RECIPE_VERSION)
            || t.validated_claims
                .as_ref()
                .is_none_or(|claims| sorted(claims.clone()) != expected)
    });
    if recipes.len() > 1
        || unsupported
        || eq_ignore_case(plan.decision_reason.as_deref(), RECIPE) && recipes.len() != 1
    {
        return Err("The DV7 client recipe is unsupported.");
    }
    Ok(true)
}

/// Silo describes the first playable video in demux order (capabilities_v3.go), not FFmpeg's
/// preferred/default stream. Check the complete inventory so a missing BL cannot select an EL.
pub fn base_layer_track_index(
    stream_indices: &[i32],
    expected_video_count: Option<i32>,
) -> Result<usize, &'static str> {
    let complete = expected_video_count
        .is_some_and(|count| count > 0 && stream_indices.len() == count as usize);
    if !complete || stream_indices.iter().any(|&i| i < 0) || has_duplicates(stream_indices) {
        return Err("The DV7 base-layer inventory is incomplete.");
    }
    // MinBy keeps the first of equal keys; indices are distinct here anyway.
    Ok(stream_indices
        .iter()
        .enumerate()
        .min_by_key(|(_, stream)| **stream)
        .map(|(i, _)| i)
        .unwrap_or(0))
}

pub fn has_duplicates(values: &[i32]) -> bool {
    let mut seen = std::collections::HashSet::new();
    values.iter().any(|value| !seen.insert(value))
}

fn sorted(mut values: Vec<String>) -> Vec<String> {
    values.sort();
    values
}
