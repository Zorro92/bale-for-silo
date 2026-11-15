//! Ported from `DisplayHdrPolicy` and `DisplayMatchPolicy`: HDMI mode facts in, an index into the
//! supported modes out (never a synthesized HDMI mode).

use std::cmp::Ordering;

use serde::{Deserialize, Serialize};

use crate::dolby_vision7;
use crate::dotnet::{eq_ignore_case, is_blank, lower};
use crate::models::{DisplayMode, HdrCapabilities, PlaybackPlan};

#[derive(Serialize, Debug, PartialEq)]
pub struct DisplayHdr {
    hdr: HdrCapabilities,
    evidence: &'static str,
}

pub fn from_modes(read_succeeded: bool, modes: &[DisplayMode]) -> DisplayHdr {
    // HDR10+, HLG and Dolby Vision are unverified. Xbox Dolby Vision is low-latency DV (profile 5)
    // only; revisit later rather than claiming support from the HDMI flag.
    let hdr10 = read_succeeded && modes.iter().any(|mode| !mode.stereo && mode.smpte2084);
    DisplayHdr {
        hdr: HdrCapabilities {
            hdr10,
            ..HdrCapabilities::default()
        },
        evidence: if read_succeeded { "exact" } else { "unknown" },
    }
}

/// Microsoft's Xbox media guidance excludes 120 Hz; media modes top out at 60 Hz.
fn media_refresh(rate: f64) -> bool {
    rate.is_finite() && rate > 0.0 && rate <= 60.0
}

fn area(mode: &DisplayMode) -> u64 {
    u64::from(mode.width) * u64::from(mode.height)
}

/// LINQ's double ordering for the finite values that reach these sorts.
fn by(a: f64, b: f64) -> Ordering {
    a.partial_cmp(&b).unwrap_or(Ordering::Equal)
}

pub fn pick_hdr10_mode(current: &DisplayMode, modes: &[DisplayMode]) -> Option<usize> {
    if current.width == 0
        || current.height == 0
        || !current.refresh_rate.is_finite()
        || current.refresh_rate <= 0.0
    {
        return None;
    }
    // Keep the current resolution/refresh when possible. Otherwise prefer the smallest resolution
    // that holds it, then the closest refresh; never shrink or enter stereo.
    // HdrDisplayController.kt also prioritizes matching/holding resolution over refresh.
    let mut eligible: Vec<(usize, &DisplayMode)> = modes
        .iter()
        .enumerate()
        .filter(|(_, m)| {
            !m.stereo
                && m.smpte2084
                && m.width >= current.width
                && m.height >= current.height
                && media_refresh(m.refresh_rate)
        })
        .collect();
    // Stable, like OrderBy/ThenBy.
    eligible.sort_by(|(_, a), (_, b)| {
        area(a)
            .cmp(&area(b))
            .then(by(
                (a.refresh_rate - current.refresh_rate).abs(),
                (b.refresh_rate - current.refresh_rate).abs(),
            ))
            .then(b.metadata2086.cmp(&a.metadata2086))
    });
    eligible.first().map(|(index, _)| *index)
}

pub fn requires_hdr10(plan: &PlaybackPlan) -> bool {
    // Direct and remux deliveries preserve the picture; a tone-mapped HLS plan must not inherit the
    // original source's HDR range. Follow the effective output when provided.
    if !matches!(
        plan.delivery.as_deref().map(lower).as_deref(),
        Some("original_http" | "progressive" | "server_remux_progressive")
    ) || eq_ignore_case(
        plan.stream.as_ref().and_then(|s| s.protocol.as_deref()),
        "hls",
    ) {
        return false;
    }
    let recipe_range = plan
        .effective_recipe
        .as_ref()
        .and_then(|r| r.dynamic_range.as_deref());
    let range = if is_blank(recipe_range) {
        plan.source
            .as_ref()
            .and_then(|s| s.dynamic_range.as_deref())
    } else {
        recipe_range
    };
    // The server can also validate HDR10 base-layer playback of HDR10+ / legacy hdr_unknown.
    eq_ignore_case(range.map(str::trim), "hdr10")
        || plan
            .claims
            .as_ref()
            .and_then(|c| c.video.as_ref())
            .and_then(|v| v.hdr10)
            == Some(true)
        || dolby_vision7::requires_base_layer(plan)
}

#[derive(Deserialize)]
pub struct PickModeArgs {
    current: DisplayMode,
    modes: Vec<DisplayMode>,
    video_width: i64,
    video_height: i64,
    #[serde(deserialize_with = "crate::dotnet::double")]
    frame_rate: f64,
    requires_hdr10: bool,
}

/// Content-resolution/refresh selection.
pub fn pick_mode(args: &PickModeArgs) -> Option<usize> {
    let (width, height, rate) = (args.video_width, args.video_height, args.frame_rate);
    if width <= 0 || height <= 0 || !rate.is_finite() || rate <= 0.0 {
        return None;
    }
    // HdrDisplayController.kt: exact resolution, otherwise holding resolutions, otherwise largest.
    // Xbox media modes are at most 60 Hz (Microsoft hevc-xbox); never enter stereo or lose required PQ.
    let eligible: Vec<(usize, &DisplayMode)> = args
        .modes
        .iter()
        .enumerate()
        .filter(|(_, m)| {
            !m.stereo
                && (!args.requires_hdr10 || m.smpte2084)
                && m.width > 0
                && m.height > 0
                && media_refresh(m.refresh_rate)
        })
        .collect();
    let largest = eligible.iter().map(|(_, m)| area(m)).max()?;
    let exact: Vec<_> = eligible
        .iter()
        .filter(|(_, m)| i64::from(m.width) == width && i64::from(m.height) == height)
        .copied()
        .collect();
    let holding: Vec<_> = eligible
        .iter()
        .filter(|(_, m)| i64::from(m.width) >= width && i64::from(m.height) >= height)
        .copied()
        .collect();
    let candidates = if !exact.is_empty() {
        exact
    } else if !holding.is_empty() {
        holding
    } else {
        eligible
            .iter()
            .filter(|(_, m)| area(m) == largest)
            .copied()
            .collect()
    };
    // RefreshRateSelector.kt: integer multiples always beat non-integer cadence, then minimize delta.
    // Compare class/delta separately so the 10000 penalty cannot obscure the 0.005 Hz tie tolerance.
    let scored: Vec<(usize, &DisplayMode, bool, f64)> = candidates
        .iter()
        .map(|&(index, mode)| {
            (
                index,
                mode,
                integer_multiple(mode.refresh_rate, rate),
                (mode.refresh_rate - rate).abs(),
            )
        })
        .collect();
    let best = scored
        .iter()
        .min_by(|a, b| b.2.cmp(&a.2).then(by(a.3, b.3)))?;
    let mut tied: Vec<_> = scored
        .iter()
        .filter(|s| s.2 == best.2 && s.3 - best.3 <= 0.005)
        .collect();
    let sixteen_nine = |m: &DisplayMode| u64::from(m.width) * 9 == u64::from(m.height) * 16;
    // HDMI can enumerate several encodings of the same transport; avoid a redundant handshake.
    tied.sort_by(|a, b| {
        sixteen_nine(b.1)
            .cmp(&sixteen_nine(a.1))
            .then(area(b.1).cmp(&area(a.1)))
            .then((*b.1 == args.current).cmp(&(*a.1 == args.current)))
    });
    tied.first().map(|s| s.0)
}

fn integer_multiple(refresh_rate: f64, frame_rate: f64) -> bool {
    let ratio = refresh_rate / frame_rate;
    // Math.Round rounds half to even.
    ratio >= 0.99 && (ratio - ratio.round_ties_even()).abs() < 0.05
}
