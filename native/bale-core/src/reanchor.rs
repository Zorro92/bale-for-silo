//! Ported from `PlaybackSeekReanchor.Matches`: PlaybackSessionManager.kt seekReanchorMismatch /
//! hasSameSeekReanchorBaseRoute. Whether a seek's new plan keeps the same route, so the player can
//! re-anchor in place. Sub-objects are compared as the exact JSON text Kiota wrote, as the C#
//! original compared Kiota-serialized strings. Plans carry stream grants: never log them.

use std::collections::HashSet;

use serde::Deserialize;
use serde_json::value::RawValue;

pub const FEATURE: &str = "seek_reanchor_v1";

#[derive(Deserialize, Default)]
#[serde(default)]
pub struct Plan {
    plan_id: Option<String>,
    plan_attempt_key: Option<String>,
    session_id: Option<String>,
    requested_media_file_id: Option<String>,
    effective_media_file_id: Option<String>,
    delivery: Option<String>,
    stream: Option<Stream>,
    selected_tracks: Option<Box<RawValue>>,
    effective_recipe: Option<Box<RawValue>>,
    claims: Option<Box<RawValue>>,
    subtitle: Option<Subtitle>,
    subtitle_fidelity_policy: Option<String>,
    transformations: Option<Vec<Box<RawValue>>>,
    applied_quirks: Option<Vec<Box<RawValue>>>,
    runtime_corrections: Option<Vec<String>>,
}

#[derive(Deserialize, Default)]
#[serde(default)]
struct Stream {
    protocol: Option<String>,
    mime_type: Option<String>,
    container: Option<String>,
    header_refresh: Option<String>,
}

#[derive(Deserialize, Default)]
#[serde(default)]
struct Subtitle {
    mode: Option<String>,
    track_id: Option<String>,
    artifact: Option<Artifact>,
    hls_rendition: Option<Box<RawValue>>,
}

#[derive(Deserialize, Default)]
#[serde(default)]
struct Artifact {
    mime_type: Option<String>,
    format: Option<String>,
}

#[derive(Deserialize)]
pub struct MatchesArgs {
    current: Plan,
    /// The decision's plan; `None` when the decision carried none.
    next: Option<Plan>,
    /// `PlaybackMessages.ValidatePlayable(decision).IsPlayable` (C#: it reads the whole decision).
    playable: bool,
    server_features: Option<Vec<String>>,
    decision_session_id: Option<String>,
    session_id: String,
    file_id: String,
}

pub fn matches(args: &MatchesArgs) -> bool {
    let Some(next) = args.next.as_ref() else {
        return false;
    };
    let session = args.session_id.as_str();
    if !args.playable
        || !args
            .server_features
            .as_ref()
            .is_some_and(|f| f.iter().any(|f| f == FEATURE))
        || args.decision_session_id.as_deref() != Some(session)
        || next.session_id.as_deref().is_some_and(|s| s != session)
    {
        return false;
    }
    let current = &args.current;
    let requested = current
        .requested_media_file_id
        .as_deref()
        .unwrap_or(&args.file_id);
    let effective = current
        .effective_media_file_id
        .as_deref()
        .unwrap_or(requested);
    let next_requested = next.requested_media_file_id.as_deref().unwrap_or(requested);
    let stream = |plan: &Plan, field: fn(&Stream) -> &Option<String>| {
        plan.stream.as_ref().and_then(|s| field(s).clone())
    };
    let subtitle = |plan: &Plan, field: fn(&Subtitle) -> Option<String>| {
        plan.subtitle.as_ref().and_then(field)
    };
    next.plan_id == current.plan_id
        && next.plan_attempt_key == current.plan_attempt_key
        && next_requested == requested
        && next
            .effective_media_file_id
            .as_deref()
            .unwrap_or(next_requested)
            == effective
        && next.delivery == current.delivery
        && stream(next, |s| &s.protocol) == stream(current, |s| &s.protocol)
        && stream(next, |s| &s.mime_type) == stream(current, |s| &s.mime_type)
        && stream(next, |s| &s.container) == stream(current, |s| &s.container)
        && stream(next, |s| &s.header_refresh) == stream(current, |s| &s.header_refresh)
        && same(&next.selected_tracks, &current.selected_tracks)
        && same(&next.effective_recipe, &current.effective_recipe)
        && same(&next.claims, &current.claims)
        && subtitle(next, |s| s.mode.clone()) == subtitle(current, |s| s.mode.clone())
        && subtitle(next, |s| s.track_id.clone()) == subtitle(current, |s| s.track_id.clone())
        && subtitle(next, |s| s.artifact.as_ref()?.mime_type.clone())
            == subtitle(current, |s| s.artifact.as_ref()?.mime_type.clone())
        && subtitle(next, |s| s.artifact.as_ref()?.format.clone())
            == subtitle(current, |s| s.artifact.as_ref()?.format.clone())
        && same(
            &next.subtitle.as_ref().and_then(|s| s.hls_rendition.clone()),
            &current
                .subtitle
                .as_ref()
                .and_then(|s| s.hls_rendition.clone()),
        )
        && next.subtitle_fidelity_policy == current.subtitle_fidelity_policy
        && same_set(&next.transformations, &current.transformations)
        && same_set(&next.applied_quirks, &current.applied_quirks)
        && string_set(&next.runtime_corrections) == string_set(&current.runtime_corrections)
}

/// Both absent, or both present with the same serialized text. A JSON `null` is absent: Kiota never
/// writes null members.
fn same(left: &Option<Box<RawValue>>, right: &Option<Box<RawValue>>) -> bool {
    match (left, right) {
        (None, None) => true,
        (Some(l), Some(r)) => l.get() == r.get(),
        _ => false,
    }
}

fn same_set(left: &Option<Vec<Box<RawValue>>>, right: &Option<Vec<Box<RawValue>>>) -> bool {
    let set = |values: &Option<Vec<Box<RawValue>>>| -> HashSet<String> {
        values
            .iter()
            .flatten()
            .map(|v| v.get().to_owned())
            .collect()
    };
    set(left) == set(right)
}

fn string_set(values: &Option<Vec<String>>) -> HashSet<&str> {
    values.iter().flatten().map(String::as_str).collect()
}
