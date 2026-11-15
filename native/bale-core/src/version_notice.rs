//! Ported from `PlaybackVersionNotice.Describe`: the passive notice for an explicit version pick the
//! server replaced with another file. The notice's timing state stays in C#.

use serde::Deserialize;

use crate::dotnet::{eq_ignore_case, is_blank, lower};
use crate::models::{FileVersion, PlaybackPlan};
use crate::software_decode::normalize_codec;

#[derive(Deserialize)]
pub struct DescribeArgs {
    plan: PlaybackPlan,
    /// The Detail selection's file id: only that proves an explicit pick.
    picked_file_id: Option<String>,
    versions: Vec<FileVersion>,
    /// `PlaybackFormatting.ResolutionRank(version.Resolution)` for each version (C# until the Detail slice).
    resolution_ranks: Vec<i32>,
    /// `PlaybackFormatting.Decodes(asked, videoDecode)`; `None` without decode claims or a picked version.
    asked_decodes: Option<bool>,
}

pub fn describe(args: &DescribeArgs) -> Option<String> {
    let plan = &args.plan;
    let picked = args.picked_file_id.as_deref();
    let effective = effective_file_id(plan);
    // Auto resolves a file id for Start too; only the Detail selection proves an explicit pick.
    if is_blank(picked) || is_blank(effective) || picked == effective {
        return None;
    }
    let find = |id: Option<&str>| {
        args.versions
            .iter()
            .position(|v| v.file_id.as_deref() == id)
    };
    let (asked, played) = (find(picked), find(effective));
    let requested_label = resolution(args, asked, None);
    let source = plan.source.as_ref();
    let output_height = if eq_ignore_case(plan.delivery.as_deref(), "original_http") {
        source.and_then(|s| s.height)
    } else {
        plan.effective_recipe
            .as_ref()
            .and_then(|r| r.height)
            .or(source.and_then(|s| s.height))
    };
    let played_label = resolution(args, played, output_height);
    let prefix = match &played_label {
        None => "Playing another version".to_owned(),
        Some(label) if played_label == requested_label => {
            format!("Playing another {label} version")
        }
        Some(label) => format!("Playing {label}"),
    };
    let selected = match &requested_label {
        None => "the selected version".to_owned(),
        Some(label) => format!("the selected {label} version"),
    };

    // These are the successful plan's quality constraints, not the discarded file's refusal. Do not
    // echo server messages: they are free-form and can contain private request data.
    let reason = plan.decision_reason.as_deref().map(lower);
    let capped = plan
        .degradation_warnings
        .iter()
        .flatten()
        .any(|warning| warning.code.as_deref() == Some("bandwidth_cap_applied"));
    let suffix = match reason.as_deref() {
        _ if reason.as_deref() == Some("quality_bandwidth_cap") || capped => {
            " — limited by the playback bandwidth cap.".to_owned()
        }
        Some("quality_bandwidth_limit") => " — adjusted for the connection speed.".to_owned(),
        Some("quality_fixed_rung") => " — using the selected playback quality.".to_owned(),
        Some("quality_device_limit") => " — adjusted for this console's video limits.".to_owned(),
        _ if asked.is_some_and(|i| has_video_codec(&args.versions[i]))
            && args.asked_decodes == Some(false) =>
        {
            format!(" — this console can't play {selected} directly.")
        }
        // planAlternateVersionV3 returns the alternate's reason (often validated_original_playback),
        // not the original 4K-policy/codec/subtitle refusal. Stay neutral when that cause is absent.
        _ if played_label == requested_label => " instead of the one selected.".to_owned(),
        _ => format!(" instead of {selected}."),
    };
    Some(prefix + &suffix)
}

fn effective_file_id(plan: &PlaybackPlan) -> Option<&str> {
    plan.effective_media_file_id.as_deref().or(plan
        .source
        .as_ref()
        .and_then(|s| s.media_file_id.as_deref()))
}

fn has_video_codec(version: &FileVersion) -> bool {
    let first = version
        .video_tracks
        .as_ref()
        .and_then(|tracks| tracks.first());
    normalize_codec(
        first
            .and_then(|t| t.codec.as_deref())
            .or(version.codec_video.as_deref()),
    )
    .is_some()
}

/// Only bounded numeric facts and fixed labels, never a file name or arbitrary label.
fn resolution(
    args: &DescribeArgs,
    version: Option<usize>,
    output_height: Option<i64>,
) -> Option<String> {
    let rank = version
        .and_then(|i| args.resolution_ranks.get(i).copied())
        .unwrap_or(0);
    let track_height = version
        .and_then(|i| args.versions[i].video_tracks.as_ref()?.first()?.height)
        .unwrap_or(0);
    let height = match output_height {
        Some(h) if h > 0 => h,
        _ if rank > 0 => i64::from(rank),
        _ => track_height,
    };
    match height {
        2160 => Some("4K".to_owned()),
        4320 => Some("8K".to_owned()),
        1..=9999 => Some(format!("{height}p")),
        _ => None,
    }
}
