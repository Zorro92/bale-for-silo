//! The debug AV1 decoder switch (owner, 2026-11-18): software AV1 routes may decode with rav1g (AV1 in
//! D3D12 compute shaders) through the FFmpegInteropX fork's `GpuDecoderSampleProvider`
//! (`bale_av1_decoder=1`, wrapper 2.1.0-bale.44; the fork's `bale/design-rav1g-provider.md`) instead of
//! dav1d zero-copy. Debug builds only; production never asks for rav1g.
//!
//! The decisions here: which mounts take rav1g and what they set ([`route`]), which output the live
//! guard accepts ([`output_verdict`]), whether a failed rav1g mount remounts once on dav1d
//! ([`remount_on_dav1d`]), and how the fork's fixed log lines read ([`parse_log`], [`describe`]).

use serde::{Deserialize, Serialize};

use crate::dotnet::trim_lower;
use crate::models::{FileVersion, PlaybackPlan, VideoTrack};
use crate::software_decode::normalize_codec;

/// MSS BufferTime on the rav1g route. The fork's image pool is 8 images and rav1g itself holds up to
/// slots + 2 of them (5 at 4K), so Media Foundation must not wait for more than a few decoded frames
/// before it starts: today's 500-1000 ms (12-24 frames at 24 fps) could block the start forever.
pub const BUFFER_MS: i32 = 200;
/// The most decoded frames the BufferTime may ask Media Foundation to hold (8 images minus rav1g's 3+).
pub const BUFFER_FRAMES: f64 = 5.0;
/// A floor so a very high frame rate still buffers something.
pub const MIN_BUFFER_MS: i32 = 40;
/// `bale_decode_ahead_frames` on the rav1g route (the fork advises 2-4 there; software video gets 16, or 8
/// above 1080p). Each decoded-ahead frame pins one of the 8 pool images until Media Foundation takes it. At
/// 4K rav1g holds up to 5 (slots + 2), leaving 3: more ahead could never fill and would only hold images the
/// renderer's BufferTime (at most 5 frames) waits for; 2 leaves less slack for a slow frame at 1080p, where
/// rav1g holds fewer. `ffmpeg-tune ahead=` still overrides it.
pub const DECODE_AHEAD_FRAMES: i32 = 3;

// Media Foundation's enum values, as the fork's VideoColorAttributes::Apply writes them.
const MF_PRIMARIES_BT709: u32 = 2;
const MF_PRIMARIES_BT470BG: u32 = 4;
const MF_PRIMARIES_SMPTE170M: u32 = 5;
const MF_PRIMARIES_BT2020: u32 = 9;
const MF_TRANSFER_709: u32 = 5;
const MF_TRANSFER_2020_CONST: u32 = 12;
const MF_TRANSFER_2020: u32 = 13;
const MF_TRANSFER_PQ: u32 = 15;
const MF_TRANSFER_HLG: u32 = 16;
const MF_MATRIX_BT709: u32 = 1;
const MF_MATRIX_BT601: u32 = 2;
const MF_MATRIX_BT2020_10: u32 = 4;
const MF_MATRIX_BT2020_12: u32 = 5;
const MF_RANGE_16_235: u32 = 2;

/// The source facts the switch needs, from the plan and the delivered file's catalog track. The colour
/// values are Media Foundation's for the container's description (what the fork puts on direct output);
/// `None` where the catalog has no value or one this module does not map.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct SourceFacts {
    pub bit_depth: Option<i64>,
    pub primaries: Option<u32>,
    pub transfer: Option<u32>,
    pub matrix: Option<u32>,
    pub frame_rate: Option<f64>,
}

#[derive(Deserialize)]
pub struct RouteArgs {
    /// The attempt's captured switch: "dav1d" or "rav1g".
    pub selected: String,
    pub software_video: bool,
    pub video_codec: Option<String>,
    pub video_width: Option<i64>,
    pub video_height: Option<i64>,
    pub display_width: i64,
    pub display_height: i64,
    /// The display is known SDR (the tone-map stage maps PQ/HLG for it).
    pub sdr_display: bool,
    /// A rav1g mount of this attempt already failed: the attempt stays on dav1d.
    pub withdrawn: bool,
    pub plan: PlaybackPlan,
    pub versions: Option<Vec<FileVersion>>,
}

/// What a mount sets for rav1g. `rav1g` false means today's dav1d zero-copy route, unchanged.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct RouteDecision {
    pub rav1g: bool,
    /// MSS BufferTime for this mount, or `None` for the software-video default.
    pub buffer_ms: Option<i32>,
    /// `bale_decode_ahead_frames` for this mount, or `None` for the software-video default.
    pub decode_ahead_frames: Option<i32>,
    /// `bale_tonemap_output_width/height`: SDR larger than the display goes through the fork's stage
    /// (bale_gpu's area downscale), never Media Foundation's scaler, which leaks per open on Xbox.
    pub output_width: Option<i32>,
    pub output_height: Option<i32>,
    /// `bale_tonemap=1`: PQ/HLG on a known SDR display is tone-mapped by the stage.
    pub tone_map: bool,
    pub source: SourceFacts,
}

pub fn route(args: &RouteArgs) -> RouteDecision {
    let rav1g = args.selected == "rav1g"
        && !args.withdrawn
        && args.software_video
        && normalize_codec(args.video_codec.as_deref()).as_deref() == Some("av1");
    let source = source_facts(&args.plan, args.versions.as_deref());
    if !rav1g {
        return RouteDecision {
            rav1g,
            buffer_ms: None,
            decode_ahead_frames: None,
            output_width: None,
            output_height: None,
            tone_map: false,
            source,
        };
    }
    let (width, height) = (
        args.video_width.unwrap_or(0),
        args.video_height.unwrap_or(0),
    );
    let downscale = crate::tone_map::downscale_to_display(
        width,
        height,
        args.display_width,
        args.display_height,
    );
    RouteDecision {
        rav1g,
        buffer_ms: Some(buffer_ms(source.frame_rate)),
        decode_ahead_frames: Some(DECODE_AHEAD_FRAMES),
        output_width: downscale.then_some(args.display_width as i32),
        output_height: downscale.then_some(args.display_height as i32),
        tone_map: args.sdr_display,
        source,
    }
}

/// [`BUFFER_MS`], or [`BUFFER_FRAMES`] frames when that is shorter at the source's frame rate.
pub fn buffer_ms(frame_rate: Option<f64>) -> i32 {
    match frame_rate {
        Some(fps) if fps.is_finite() && fps > 0.0 => {
            let frames = (BUFFER_FRAMES * 1000.0 / fps).floor();
            (frames.min(f64::from(BUFFER_MS)) as i32).max(MIN_BUFFER_MS)
        }
        _ => BUFFER_MS,
    }
}

/// The delivered file's facts: the plan's source, joined to its catalog version (the file actually
/// delivered, as tone_map.rs joins it) when that has exactly one video track.
pub fn source_facts(plan: &PlaybackPlan, versions: Option<&[FileVersion]>) -> SourceFacts {
    let source = plan.source.as_ref();
    let file_id = plan
        .effective_media_file_id
        .as_deref()
        .or(source.and_then(|s| s.media_file_id.as_deref()));
    let track: Option<&VideoTrack> = file_id
        .and_then(|id| versions?.iter().find(|v| v.file_id.as_deref() == Some(id)))
        .and_then(|v| match v.video_tracks.as_deref() {
            Some([track]) => Some(track),
            _ => None,
        });
    let bit_depth = source
        .and_then(|s| s.bit_depth)
        .or(track.and_then(|t| t.bit_depth));
    let Some(track) = track else {
        return SourceFacts {
            bit_depth,
            ..SourceFacts::default()
        };
    };
    let transfer_name = trim_lower(track.color_transfer.as_deref());
    let twelve_bit_transfer = matches!(transfer_name.as_deref(), Some("bt2020-12" | "15"));
    SourceFacts {
        bit_depth,
        primaries: primaries(track.color_primaries.as_deref()),
        transfer: transfer(track.color_transfer.as_deref()),
        matrix: matrix(track.color_space.as_deref(), twelve_bit_transfer),
        frame_rate: frame_rate(track.frame_rate.as_deref()),
    }
}

// ffprobe names (or FFmpeg's AVCOL_* numbers) → Media Foundation, as VideoColorAttributes::Apply maps
// them. Only the values AV1 files carry in practice; anything else is not compared.
fn primaries(value: Option<&str>) -> Option<u32> {
    match trim_lower(value)?.as_str() {
        "bt709" | "1" => Some(MF_PRIMARIES_BT709),
        "bt470bg" | "5" => Some(MF_PRIMARIES_BT470BG),
        "smpte170m" | "6" => Some(MF_PRIMARIES_SMPTE170M),
        "bt2020" | "9" => Some(MF_PRIMARIES_BT2020),
        _ => None,
    }
}

fn transfer(value: Option<&str>) -> Option<u32> {
    match trim_lower(value)?.as_str() {
        "bt709" | "1" | "smpte170m" | "6" => Some(MF_TRANSFER_709),
        "bt2020-10" | "14" | "bt2020-12" | "15" => Some(MF_TRANSFER_2020),
        "smpte2084" | "16" => Some(MF_TRANSFER_PQ),
        "arib-std-b67" | "18" => Some(MF_TRANSFER_HLG),
        _ => None,
    }
}

fn matrix(value: Option<&str>, twelve_bit_transfer: bool) -> Option<u32> {
    match trim_lower(value)?.as_str() {
        "bt709" | "1" => Some(MF_MATRIX_BT709),
        "bt470bg" | "5" | "smpte170m" | "6" => Some(MF_MATRIX_BT601),
        "bt2020nc" | "9" if twelve_bit_transfer => Some(MF_MATRIX_BT2020_12),
        "bt2020nc" | "9" => Some(MF_MATRIX_BT2020_10),
        _ => None,
    }
}

/// "23.976", "24000/1001" or "25/1".
fn frame_rate(value: Option<&str>) -> Option<f64> {
    let text = value?.trim();
    let fps = match text.split_once('/') {
        Some((num, den)) => {
            let num: f64 = num.trim().parse().ok()?;
            let den: f64 = den.trim().parse().ok()?;
            if den == 0.0 {
                return None;
            }
            num / den
        }
        None => text.parse().ok()?,
    };
    (fps.is_finite() && fps > 0.0).then_some(fps)
}

/// The media type the guard read off a video sample's stream descriptor.
#[derive(Debug, Clone, Default, Deserialize)]
pub struct OutputFacts {
    /// "NV12", "P010" or anything else (a GUID string).
    pub subtype: Option<String>,
    /// `{6d553633-9287-4856-bc9f-0c23af00e184}` = 1: the fork's direct rav1g output.
    pub rav1g_mark: bool,
    /// `{b41e6a52-3c9d-4f0b-8e7a-5d2c1f9a0b37}` = 1: the fork's tone-map/downscale stage output.
    pub tone_map_mark: bool,
    pub primaries: Option<u32>,
    pub transfer: Option<u32>,
    pub matrix: Option<u32>,
    pub range: Option<u32>,
}

#[derive(Deserialize)]
pub struct VerdictArgs {
    pub source: SourceFacts,
    pub output: OutputFacts,
    /// The display is known SDR: direct PQ/HLG output there means the stage declined.
    pub sdr_display: bool,
    /// bale_gpu cannot write P010, so the stage writes NV12 (as FfmpegVideoOutputGuard allows).
    pub allow_nv12_stage: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct Verdict {
    /// "direct" (rav1g's own texture), "stage" (the fork's stage read rav1g's texture), "dav1d" (no
    /// mark: the open fell back to dav1d; accepted, this is a debug switch) or "reject".
    pub kind: String,
    /// A fixed reason for "reject", else `None`.
    pub reason: Option<String>,
}

impl Verdict {
    fn new(kind: &str) -> Self {
        Self {
            kind: kind.to_owned(),
            reason: None,
        }
    }

    fn reject(reason: &str) -> Self {
        Self {
            kind: "reject".to_owned(),
            reason: Some(reason.to_owned()),
        }
    }
}

/// What the guard does with a rav1g route's video output (the fork's design §4).
pub fn output_verdict(args: &VerdictArgs) -> Verdict {
    let output = &args.output;
    let subtype = output.subtype.as_deref().map(str::to_ascii_uppercase);
    let nv12 = matches!(
        subtype.as_deref(),
        Some("NV12" | "3231564E-0000-0010-8000-00AA00389B71")
    );
    let p010 = matches!(
        subtype.as_deref(),
        Some("P010" | "30313050-0000-0010-8000-00AA00389B71")
    );
    match (output.rav1g_mark, output.tone_map_mark) {
        (true, true) => Verdict::reject("both marks"),
        (false, true) => {
            // As FfmpegVideoOutputGuard.IsSdrOutput: the stage's output is limited-range BT.709, P010
            // unless bale_gpu cannot write it.
            let bt709 = output.primaries == Some(MF_PRIMARIES_BT709)
                && output.transfer == Some(MF_TRANSFER_709)
                && output.matrix == Some(MF_MATRIX_BT709)
                && output.range == Some(MF_RANGE_16_235);
            if !(p010 || nv12 && args.allow_nv12_stage) {
                Verdict::reject("stage format")
            } else if !bt709 {
                Verdict::reject("stage colour")
            } else {
                Verdict::new("stage")
            }
        }
        (true, false) => direct_verdict(args, nv12, p010),
        (false, false) => Verdict::new("dav1d"),
    }
}

fn direct_verdict(args: &VerdictArgs, nv12: bool, p010: bool) -> Verdict {
    let output = &args.output;
    let source = &args.source;
    // The bit-depth rule (AGENTS.md): 8-bit stays NV12, more than 8 bits is P010, never NV12.
    let format_ok = match source.bit_depth {
        Some(depth) if depth <= 8 => nv12,
        Some(_) => p010,
        // The fork chose the format from the sequence header; without a depth only reject the unknown.
        None => nv12 || p010,
    };
    if !format_ok {
        return Verdict::reject("format does not match the source depth");
    }
    // Direct output carries the container's description (VideoColorAttributes::Apply). The HDR policy
    // may leave BT.2020 primaries and HDR transfers out; it never writes a different value.
    let hdr_primaries = |value: u32| value >= MF_PRIMARIES_BT2020;
    let hdr_transfer = |value: u32| value >= MF_TRANSFER_2020_CONST;
    let matches =
        |expected: Option<u32>, actual: Option<u32>, may_omit: &dyn Fn(u32) -> bool| match (
            expected, actual,
        ) {
            (None, _) => true,
            (Some(e), Some(a)) => e == a,
            (Some(e), None) => may_omit(e),
        };
    if !matches(source.primaries, output.primaries, &hdr_primaries)
        || !matches(source.transfer, output.transfer, &hdr_transfer)
        || !matches(source.matrix, output.matrix, &|_| false)
    {
        return Verdict::reject("colour does not match the source");
    }
    if args.sdr_display && matches!(source.transfer, Some(MF_TRANSFER_PQ | MF_TRANSFER_HLG)) {
        // bale_tonemap was set for this display: direct HDR output means the stage declined.
        return Verdict::reject("HDR on an SDR display");
    }
    Verdict::new("direct")
}

#[derive(Deserialize)]
pub struct RemountArgs {
    /// The failed mount was configured for rav1g.
    pub rav1g_route: bool,
    /// Its video ran on rav1g (the GPU provider opened), not the fork's open-time dav1d fallback.
    pub rav1g_active: bool,
    /// This attempt already remounted on dav1d once.
    pub remounted: bool,
    pub hdr_output_failed: bool,
    pub subtitle_failure: bool,
    pub start_budget: bool,
}

/// Whether a failed mount remounts the same plan once on dav1d zero-copy instead of spending the
/// single server replan, as the console-downscale-declined remount does. Failures that say nothing
/// about the decoder (HDMI, subtitles, a start that ran out of time) take the ordinary path; a second
/// failure always does, so this never loops.
pub fn remount_on_dav1d(args: &RemountArgs) -> bool {
    args.rav1g_route
        && args.rav1g_active
        && !args.remounted
        && !args.hdr_output_failed
        && !args.subtitle_failure
        && !args.start_budget
}

/// One of the fork's fixed `Bale rav1g:` log lines (BaleRav1g.h, GpuDecoderSampleProvider.h).
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct LogEvent {
    /// "loaded", "active", "fallback", "failed", "output", "destroyed" or "other".
    pub kind: String,
    /// The text after the prefix, one line, at most [`MAX_DETAIL`] characters.
    pub detail: String,
}

pub const LOG_PREFIX: &str = "Bale rav1g: ";
pub const MAX_DETAIL: usize = 160;

pub fn parse_log(line: &str) -> Option<LogEvent> {
    let detail = line
        .strip_prefix(LOG_PREFIX)?
        .trim_end_matches(['\r', '\n']);
    let kind = if detail.starts_with("loaded ") {
        "loaded"
    } else if detail.starts_with("active ") {
        "active"
    } else if detail.starts_with("fallback to dav1d") {
        "fallback"
    } else if detail.starts_with("stream failed") {
        "failed"
    } else if detail.starts_with("output ") {
        "output"
    } else if detail.starts_with("destroyed") {
        "destroyed"
    } else {
        "other"
    };
    let detail: String = detail
        .chars()
        .filter(|c| !c.is_control())
        .take(MAX_DETAIL)
        .collect();
    Some(LogEvent {
        kind: kind.to_owned(),
        detail,
    })
}

#[derive(Deserialize)]
pub struct DescribeArgs {
    pub selected: String,
    /// The mount was configured for rav1g (`bale_av1_decoder=1`).
    pub rav1g_route: bool,
    /// rav1g.dll is in the app package.
    pub library_present: bool,
    /// The decoder engine FFmpegInteropX reports for the video stream: rav1g's provider reports the
    /// D3D11 engine, the dav1d fallback the software one.
    pub gpu_engine: Option<bool>,
    /// The guard's last verdict kind, if a sample was seen.
    pub verdict: Option<String>,
    pub fallback: Option<String>,
    pub failed: Option<String>,
    pub output: Option<String>,
    /// This attempt remounted on dav1d after a rav1g failure.
    pub remounted: bool,
}

/// The stats/`where` row: which decoder the open actually used, and why.
pub fn describe(args: &DescribeArgs) -> String {
    let library = if args.library_present {
        ""
    } else {
        " · rav1g.dll not packaged"
    };
    if !args.rav1g_route {
        let reason = if args.remounted {
            " (remounted after a rav1g failure)"
        } else if args.selected == "rav1g" {
            " (not a software AV1 route)"
        } else {
            ""
        };
        return format!("dav1d{reason} · switch {}{library}", args.selected);
    }
    if let Some(failed) = &args.failed {
        return format!("rav1g failed: {failed}{library}");
    }
    if let Some(fallback) = &args.fallback {
        return format!("dav1d (rav1g {fallback}){library}");
    }
    let used = match (args.verdict.as_deref(), args.gpu_engine) {
        (Some("dav1d"), _) => "dav1d (no rav1g mark: open fell back)",
        (Some("direct"), _) => "rav1g direct",
        (Some("stage"), _) => "rav1g + stage",
        (Some("reject"), _) => "rav1g output rejected",
        (_, Some(true)) => "rav1g",
        (_, Some(false)) => "dav1d (software engine: open fell back)",
        _ => "rav1g requested",
    };
    match &args.output {
        Some(output) => format!("{used} · {output}{library}"),
        None => format!("{used}{library}"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::models::{PlaybackSource, VideoTrack};

    fn plan(bit_depth: Option<i64>) -> PlaybackPlan {
        PlaybackPlan {
            source: Some(PlaybackSource {
                media_file_id: Some("f".into()),
                video_codec: Some("av1".into()),
                bit_depth,
                ..PlaybackSource::default()
            }),
            ..PlaybackPlan::default()
        }
    }

    fn version(primaries: &str, transfer: &str, space: &str, rate: &str) -> FileVersion {
        FileVersion {
            file_id: Some("f".into()),
            video_tracks: Some(vec![VideoTrack {
                color_primaries: Some(primaries.into()),
                color_transfer: Some(transfer.into()),
                color_space: Some(space.into()),
                frame_rate: Some(rate.into()),
                ..VideoTrack::default()
            }]),
            ..FileVersion::default()
        }
    }

    fn route_args(selected: &str) -> RouteArgs {
        RouteArgs {
            selected: selected.into(),
            software_video: true,
            video_codec: Some("av1".into()),
            video_width: Some(3840),
            video_height: Some(2160),
            display_width: 1920,
            display_height: 1080,
            sdr_display: true,
            withdrawn: false,
            plan: plan(Some(10)),
            versions: Some(vec![version("bt709", "bt709", "bt709", "24000/1001")]),
        }
    }

    #[test]
    fn only_switched_software_av1_takes_rav1g() {
        assert!(route(&route_args("rav1g")).rav1g);
        assert!(!route(&route_args("dav1d")).rav1g);
        let mut args = route_args("rav1g");
        args.withdrawn = true;
        assert!(!route(&args).rav1g);
        let mut args = route_args("rav1g");
        args.software_video = false;
        assert!(!route(&args).rav1g);
        let mut args = route_args("rav1g");
        args.video_codec = Some("hevc".into());
        assert!(!route(&args).rav1g);
        let dav1d = route(&route_args("dav1d"));
        assert_eq!(dav1d.buffer_ms, None);
        assert_eq!(dav1d.decode_ahead_frames, None);
        assert_eq!(dav1d.output_width, None);
        assert!(!dav1d.tone_map);
    }

    #[test]
    fn rav1g_route_sets_buffer_stage_and_display_size() {
        let decision = route(&route_args("rav1g"));
        assert_eq!(decision.buffer_ms, Some(200));
        assert_eq!(decision.decode_ahead_frames, Some(3));
        assert_eq!(
            (decision.output_width, decision.output_height),
            (Some(1920), Some(1080))
        );
        assert!(decision.tone_map);
        let mut args = route_args("rav1g");
        args.video_width = Some(1920);
        args.video_height = Some(1080);
        args.sdr_display = false;
        let decision = route(&args);
        assert_eq!(decision.output_width, None);
        assert!(!decision.tone_map);
    }

    #[test]
    fn decode_ahead_stays_in_the_forks_range_beside_rav1gs_4k_hold() {
        // The fork's advice for the GPU provider: 2-4 frames.
        assert!((2..=4).contains(&DECODE_AHEAD_FRAMES));
        // 8 pool images minus rav1g's 4K hold (slots + 2 = 5).
        assert!(DECODE_AHEAD_FRAMES <= 8 - 5);
    }

    #[test]
    fn buffer_time_holds_at_most_five_frames() {
        assert_eq!(buffer_ms(None), 200);
        assert_eq!(buffer_ms(Some(23.976)), 200);
        assert_eq!(buffer_ms(Some(30.0)), 166);
        assert_eq!(buffer_ms(Some(59.94)), 83);
        assert_eq!(buffer_ms(Some(240.0)), MIN_BUFFER_MS);
        assert_eq!(buffer_ms(Some(f64::NAN)), 200);
        assert_eq!(buffer_ms(Some(0.0)), 200);
    }

    #[test]
    fn source_facts_map_catalog_colour() {
        let facts = source_facts(
            &plan(Some(10)),
            Some(&[version("bt2020", "smpte2084", "bt2020nc", "60/1")]),
        );
        assert_eq!(
            facts,
            SourceFacts {
                bit_depth: Some(10),
                primaries: Some(9),
                transfer: Some(15),
                matrix: Some(4),
                frame_rate: Some(60.0),
            }
        );
        let facts = source_facts(
            &plan(None),
            Some(&[version("bt2020", "bt2020-12", "bt2020nc", "x")]),
        );
        assert_eq!(facts.matrix, Some(5));
        assert_eq!(facts.transfer, Some(13));
        assert_eq!(facts.frame_rate, None);
        // No catalog match: depth only.
        let facts = source_facts(&plan(Some(8)), None);
        assert_eq!(
            facts,
            SourceFacts {
                bit_depth: Some(8),
                ..SourceFacts::default()
            }
        );
        let facts = source_facts(&plan(None), Some(&[version("film", "linear", "ycgco", "")]));
        assert_eq!(
            (facts.primaries, facts.transfer, facts.matrix),
            (None, None, None)
        );
    }

    fn verdict(depth: Option<i64>, output: OutputFacts) -> Verdict {
        output_verdict(&VerdictArgs {
            source: SourceFacts {
                bit_depth: depth,
                primaries: Some(2),
                transfer: Some(5),
                matrix: Some(1),
                frame_rate: None,
            },
            output,
            sdr_display: true,
            allow_nv12_stage: false,
        })
    }

    fn direct(subtype: &str) -> OutputFacts {
        OutputFacts {
            subtype: Some(subtype.into()),
            rav1g_mark: true,
            primaries: Some(2),
            transfer: Some(5),
            matrix: Some(1),
            range: Some(2),
            ..OutputFacts::default()
        }
    }

    #[test]
    fn direct_output_keeps_the_source_depth() {
        assert_eq!(verdict(Some(8), direct("NV12")).kind, "direct");
        assert_eq!(verdict(Some(10), direct("P010")).kind, "direct");
        assert_eq!(
            verdict(Some(10), direct("30313050-0000-0010-8000-00aa00389b71")).kind,
            "direct"
        );
        assert_eq!(verdict(Some(10), direct("NV12")).kind, "reject");
        assert_eq!(verdict(Some(8), direct("P010")).kind, "reject");
        assert_eq!(verdict(Some(12), direct("P010")).kind, "direct");
        assert_eq!(verdict(None, direct("NV12")).kind, "direct");
        assert_eq!(verdict(None, direct("RGB32")).kind, "reject");
    }

    #[test]
    fn direct_output_carries_the_source_colour() {
        let mut output = direct("NV12");
        output.matrix = Some(2);
        assert_eq!(verdict(Some(8), output).kind, "reject");
        let mut output = direct("NV12");
        output.primaries = None;
        assert_eq!(verdict(Some(8), output).kind, "reject");
        // Unknown source colour is not compared.
        let args = VerdictArgs {
            source: SourceFacts {
                bit_depth: Some(10),
                ..SourceFacts::default()
            },
            output: OutputFacts {
                subtype: Some("P010".into()),
                rav1g_mark: true,
                ..OutputFacts::default()
            },
            sdr_display: false,
            allow_nv12_stage: false,
        };
        assert_eq!(output_verdict(&args).kind, "direct");
    }

    #[test]
    fn hdr_direct_output_may_omit_hdr_attributes_but_not_on_sdr() {
        let hdr = SourceFacts {
            bit_depth: Some(10),
            primaries: Some(9),
            transfer: Some(15),
            matrix: Some(4),
            frame_rate: None,
        };
        let output = OutputFacts {
            subtype: Some("P010".into()),
            rav1g_mark: true,
            matrix: Some(4),
            ..OutputFacts::default()
        };
        let mut args = VerdictArgs {
            source: hdr,
            output,
            sdr_display: false,
            allow_nv12_stage: false,
        };
        assert_eq!(output_verdict(&args).kind, "direct");
        args.output.transfer = Some(5);
        assert_eq!(output_verdict(&args).kind, "reject");
        args.output.transfer = Some(15);
        args.output.primaries = Some(9);
        assert_eq!(output_verdict(&args).kind, "direct");
        args.sdr_display = true;
        assert_eq!(
            output_verdict(&args).reason.as_deref(),
            Some("HDR on an SDR display")
        );
    }

    #[test]
    fn stage_output_is_marked_bt709_p010() {
        let stage = OutputFacts {
            subtype: Some("P010".into()),
            tone_map_mark: true,
            primaries: Some(2),
            transfer: Some(5),
            matrix: Some(1),
            range: Some(2),
            ..OutputFacts::default()
        };
        assert_eq!(verdict(Some(10), stage.clone()).kind, "stage");
        assert_eq!(verdict(Some(8), stage.clone()).kind, "stage");
        let mut nv12 = stage.clone();
        nv12.subtype = Some("NV12".into());
        assert_eq!(verdict(Some(10), nv12.clone()).kind, "reject");
        let args = VerdictArgs {
            source: SourceFacts::default(),
            output: nv12,
            sdr_display: true,
            allow_nv12_stage: true,
        };
        assert_eq!(output_verdict(&args).kind, "stage");
        let mut colour = stage.clone();
        colour.range = Some(1);
        assert_eq!(verdict(Some(10), colour).kind, "reject");
        let mut both = stage;
        both.rav1g_mark = true;
        assert_eq!(
            verdict(Some(10), both).reason.as_deref(),
            Some("both marks")
        );
    }

    #[test]
    fn unmarked_output_is_the_dav1d_fallback() {
        let output = OutputFacts {
            subtype: Some("P010".into()),
            ..OutputFacts::default()
        };
        assert_eq!(verdict(Some(8), output), Verdict::new("dav1d"));
    }

    #[test]
    fn remount_once_and_only_for_rav1g_failures() {
        let base = RemountArgs {
            rav1g_route: true,
            rav1g_active: true,
            remounted: false,
            hdr_output_failed: false,
            subtitle_failure: false,
            start_budget: false,
        };
        assert!(remount_on_dav1d(&base));
        let changes: [fn(&mut RemountArgs); 6] = [
            |a: &mut RemountArgs| a.rav1g_route = false,
            |a: &mut RemountArgs| a.rav1g_active = false,
            |a: &mut RemountArgs| a.remounted = true,
            |a: &mut RemountArgs| a.hdr_output_failed = true,
            |a: &mut RemountArgs| a.subtitle_failure = true,
            |a: &mut RemountArgs| a.start_budget = true,
        ];
        for change in changes {
            let mut args = RemountArgs { ..base };
            change(&mut args);
            assert!(!remount_on_dav1d(&args));
        }
    }

    #[test]
    fn parses_the_forks_log_lines() {
        let event = |line: &str| parse_log(line).map(|e| e.kind);
        assert_eq!(
            event("Bale rav1g: loaded rav1g 0.4.0 (interface 1)\n").as_deref(),
            Some("loaded")
        );
        assert_eq!(
            event(
                "Bale rav1g: active 3840x2160 bits=10 slots=3 parse=1 threads=2 device=501.5 MiB"
            )
            .as_deref(),
            Some("active")
        );
        assert_eq!(
            event("Bale rav1g: fallback to dav1d (library missing, status=0x8007007E)").as_deref(),
            Some("fallback")
        );
        assert_eq!(
            event("Bale rav1g: stream failed (decode, hr=0x80042B23)").as_deref(),
            Some("failed")
        );
        assert_eq!(
            event("Bale rav1g: output 3840x2160 P010 direct hdr=0").as_deref(),
            Some("output")
        );
        assert_eq!(
            event("Bale rav1g: destroyed (frames=1 discarded=0)").as_deref(),
            Some("destroyed")
        );
        assert_eq!(
            event("Bale rav1g: device creation failed").as_deref(),
            Some("other")
        );
        assert_eq!(event("Bale tone map: request ignored"), None);
        let long = format!("Bale rav1g: output {}\r\n", "x\u{1}".repeat(400));
        let detail = parse_log(&long).unwrap().detail;
        assert_eq!(detail.chars().count(), MAX_DETAIL);
        assert!(!detail.chars().any(char::is_control));
    }

    fn describe_args() -> DescribeArgs {
        DescribeArgs {
            selected: "rav1g".into(),
            rav1g_route: true,
            library_present: true,
            gpu_engine: Some(true),
            verdict: None,
            fallback: None,
            failed: None,
            output: None,
            remounted: false,
        }
    }

    #[test]
    fn describes_the_decoder_the_open_used() {
        assert_eq!(describe(&describe_args()), "rav1g");
        let mut args = describe_args();
        args.verdict = Some("direct".into());
        args.output = Some("output 1920x1080 NV12 direct hdr=0".into());
        assert_eq!(
            describe(&args),
            "rav1g direct · output 1920x1080 NV12 direct hdr=0"
        );
        let mut args = describe_args();
        args.gpu_engine = Some(false);
        args.fallback = Some("fallback to dav1d (library missing, status=0x8007007E)".into());
        args.library_present = false;
        assert_eq!(
            describe(&args),
            "dav1d (rav1g fallback to dav1d (library missing, status=0x8007007E)) · rav1g.dll not packaged"
        );
        let mut args = describe_args();
        args.failed = Some("stream failed (decode, hr=0x80042B23)".into());
        assert_eq!(
            describe(&args),
            "rav1g failed: stream failed (decode, hr=0x80042B23)"
        );
        let mut args = describe_args();
        args.verdict = Some("dav1d".into());
        assert_eq!(describe(&args), "dav1d (no rav1g mark: open fell back)");
        let mut args = describe_args();
        args.rav1g_route = false;
        args.remounted = true;
        assert_eq!(
            describe(&args),
            "dav1d (remounted after a rav1g failure) · switch rav1g"
        );
        let mut args = describe_args();
        args.rav1g_route = false;
        args.selected = "dav1d".into();
        assert_eq!(describe(&args), "dav1d · switch dav1d");
        let mut args = describe_args();
        args.rav1g_route = false;
        assert_eq!(
            describe(&args),
            "dav1d (not a software AV1 route) · switch rav1g"
        );
    }
}
