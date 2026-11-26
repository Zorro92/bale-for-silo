//! The AV1 decoder (owner, 2026-11-26: rav1g is the default in every build; tested in debug builds
//! since 2026-11-18): software AV1 routes decode with rav1g (AV1 in D3D12 compute shaders) through the
//! FFmpegInteropX fork's `GpuDecoderSampleProvider` (`bale_av1_decoder=1`, wrapper 2.1.0-bale.44; the
//! fork's `bale/design-rav1g-provider.md`) wherever rav1g.dll is usable, and with dav1d CPU decode/grain
//! on D3D11 otherwise: rav1g, then the fork's in-open dav1d fallback (at most 1080 lines), then Bale's
//! one dav1d remount, then the ordinary replan or server transcode.
//!
//! The decisions here: whether the packaged library is usable ([`library_status`]), which mounts take
//! rav1g and what they set ([`route`]), which output the live guard accepts ([`output_verdict`]),
//! whether a failed rav1g mount remounts once on dav1d ([`remount_on_dav1d`]), and how the fork's
//! fixed log lines read ([`parse_log`], [`describe`]).

use serde::{Deserialize, Serialize};

use crate::dotnet::trim_lower;
use crate::models::{FileVersion, PlaybackPlan, VideoTrack};
use crate::software_decode::normalize_codec;

/// MSS BufferTime on the rav1g route, at every resolution (owner, 2026-11-18). Since fork bale.46 the provider
/// returns each rav1g image at once and its feeder thread keeps 4 units in flight (bale.54), so a short buffer no
/// longer starves the renderer; the console measured 4K AV1 at 250 ms smoothest (23.92-23.99 fps) and leanest. Every
/// output frame is display-sized after the stage (~5.9 MiB at 1080p), so the saving is the same at any resolution,
/// and the Xbox's practical app ceiling is ~1.2 GB.
pub const BUFFER_MS: i32 = 250;
/// Units the fork's feeder keeps in flight (its bale.54 default), part of the output texture cap below.
pub const INFLIGHT_UNITS: i32 = 4;
/// Spare textures above buffered frames + in flight in the cap below.
pub const OUTPUT_TEXTURE_MARGIN: i32 = 2;
/// `bale_decode_ahead_frames` on the rav1g route: 8, what the fork confirmed for 1080p and what dav1d gets
/// above 1080p. `ffmpeg-tune ahead=` still overrides it.
pub const DECODE_AHEAD_FRAMES: i32 = 8;
/// Wrapper 2.1.0-bale.48 exports `bale_rav1g_prewarm` from FFmpegInteropX.dll.
pub const PREWARM_WRAPPER_REVISION: i32 = 48;

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

/// The interface version the pinned wrapper's `BaleRav1g.h` is written against (its static_assert;
/// scripts/rav1g.json `abi_version`, scripts/rav1g-common.ps1 `$Rav1gWrapperAbiVersion`). The fork
/// refuses a library of any other version and decodes with dav1d.
pub const LIBRARY_ABI_VERSION: u32 = 3;

/// The exports the fork resolves from rav1g.dll (`BaleRav1gExports`; scripts/rav1g-common.ps1
/// `$Rav1gExports`, which CI checks in the package). A library without one of them is refused.
pub const LIBRARY_EXPORTS: &[&str] = &[
    "rav1g_abi_version",
    "rav1g_version",
    "rav1g_peek_stream_info",
    "rav1g_device_create",
    "rav1g_device_d3d12_device",
    "rav1g_settings_default",
    "rav1g_decoder_create",
    "rav1g_decoder_decode",
    "rav1g_decoder_drain",
    "rav1g_decoder_configuration",
    "rav1g_decoder_output_fence",
    "rav1g_decoder_destroy",
];

/// What the app's own check of rav1g.dll found (once per process, off the UI thread): the same
/// steps as the fork's loader, short of creating rav1g's device.
#[derive(Deserialize)]
pub struct LibraryArgs {
    /// rav1g.dll is in the package root (an empty pin in scripts/rav1g.json packages none).
    pub present: bool,
    pub loaded: bool,
    /// The first of [`LIBRARY_EXPORTS`] the loaded library lacks.
    pub missing_export: Option<String>,
    /// What `rav1g_abi_version()` returned.
    pub abi_version: Option<u32>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct LibraryStatus {
    /// The fork will load it: the rav1g route and its 2160p60 AV1 claim apply.
    pub usable: bool,
    /// Fixed words for the log and the debug stats, e.g. "rav1g.dll not packaged".
    pub detail: String,
}

/// Whether rav1g.dll is usable, as the fork's loader will judge it (`BaleRav1gExports::Get`). Not
/// usable keeps AV1 on dav1d at 1080p60; a device or stream rav1g can't take is still the fork's
/// open-time fallback.
pub fn library_status(args: &LibraryArgs) -> LibraryStatus {
    let refused = |detail: String| LibraryStatus {
        usable: false,
        detail,
    };
    if !args.present {
        return refused("rav1g.dll not packaged".into());
    }
    if !args.loaded {
        return refused("rav1g.dll failed to load".into());
    }
    if let Some(missing) = &args.missing_export {
        // Only a name from the list reaches the text.
        let name = LIBRARY_EXPORTS
            .iter()
            .find(|&&export| export == missing.as_str())
            .copied()
            .unwrap_or("an export");
        return refused(format!("rav1g.dll lacks {name}"));
    }
    match args.abi_version {
        Some(LIBRARY_ABI_VERSION) => LibraryStatus {
            usable: true,
            detail: format!("rav1g.dll usable (interface {LIBRARY_ABI_VERSION})"),
        },
        Some(other) => refused(format!(
            "rav1g.dll interface {other}, expected {LIBRARY_ABI_VERSION}"
        )),
        None => refused("rav1g.dll interface unknown".into()),
    }
}

#[derive(Deserialize)]
pub struct RouteArgs {
    /// The attempt's AV1 decoder: "rav1g" in release builds, the captured debug switch ("dav1d" or
    /// "rav1g", default rav1g) in debug ones.
    pub selected: String,
    /// rav1g.dll is usable ([`library_status`]); without it the mount stays on dav1d.
    pub library_usable: bool,
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

/// What a mount sets for rav1g. `rav1g` false means dav1d CPU decode/grain on D3D11.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct RouteDecision {
    pub rav1g: bool,
    /// MSS BufferTime for this mount, or `None` for the software-video default.
    pub buffer_ms: Option<i32>,
    /// `bale_decode_ahead_frames` for this mount, or `None` for the software-video default.
    pub decode_ahead_frames: Option<i32>,
    /// `bale_gpu_output_textures` (player-side texture cap), or `None` for no cap.
    pub output_textures: Option<i32>,
    /// `bale_tonemap_output_width/height`: SDR larger than the display goes through the fork's stage
    /// (bale_gpu's area downscale), never Media Foundation's scaler, which leaks per open on Xbox.
    pub output_width: Option<i32>,
    pub output_height: Option<i32>,
    /// `bale_tonemap=1`: PQ/HLG on a known SDR display is tone-mapped by the stage.
    pub tone_map: bool,
    /// rav1g's display pass (or the fork's stage on rav1g's frames) scales this SDR source down to the
    /// display: a stage for the engine output's depth (`engine_output`), as the console downscale is for
    /// HEVC/VP9/H.264. PQ/HLG and BT.2020 sources get none from the size alone (the fork's
    /// BaleToneMap::Create: no curve without the tone map); their tone map is route.rs's.
    pub downscale_stage: bool,
    pub source: SourceFacts,
}

pub fn route(args: &RouteArgs) -> RouteDecision {
    let rav1g = args.selected == "rav1g"
        && args.library_usable
        && !args.withdrawn
        && args.software_video
        && normalize_codec(args.video_codec.as_deref()).as_deref() == Some("av1");
    let source = source_facts(&args.plan, args.versions.as_deref());
    if !rav1g {
        return RouteDecision {
            rav1g,
            buffer_ms: None,
            decode_ahead_frames: None,
            output_textures: None,
            output_width: None,
            output_height: None,
            tone_map: false,
            downscale_stage: false,
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
    let hdr_or_wide = matches!(source.transfer, Some(MF_TRANSFER_PQ | MF_TRANSFER_HLG))
        || source.primaries == Some(MF_PRIMARIES_BT2020);
    RouteDecision {
        rav1g,
        buffer_ms: Some(BUFFER_MS),
        decode_ahead_frames: Some(DECODE_AHEAD_FRAMES),
        output_textures: output_textures(source.frame_rate),
        output_width: downscale.then_some(args.display_width as i32),
        output_height: downscale.then_some(args.display_height as i32),
        tone_map: args.sdr_display,
        downscale_stage: downscale && !hdr_or_wide,
        source,
    }
}

/// `bale_gpu_output_textures` (fork bale.56): frames in [`BUFFER_MS`] at the source's frame rate (rounded up) +
/// [`INFLIGHT_UNITS`] + [`OUTPUT_TEXTURE_MARGIN`]: 12 at 24 fps (the 4K console measurement, 0 overruns), 14 at 30,
/// 21 at 60. A cap below what the buffer holds makes frames wait up to 150 ms each (the fork's overrun), so an unknown
/// or implausible frame rate gets no cap (`None`, the fork's unlimited default).
pub fn output_textures(frame_rate: Option<f64>) -> Option<i32> {
    let fps = frame_rate.filter(|fps| fps.is_finite() && (1.0..=240.0).contains(fps))?;
    let buffered = (fps * f64::from(BUFFER_MS) / 1000.0).ceil() as i32;
    Some((buffered + INFLIGHT_UNITS + OUTPUT_TEXTURE_MARGIN).clamp(4, 64))
}

/// Whether the pinned wrapper exports `bale_rav1g_prewarm` (2.1.0-bale.48 or later).
pub fn prewarm_supported(version: &str) -> bool {
    version.strip_prefix("2.1.0-bale.").is_some_and(|revision| {
        !revision.is_empty()
            && revision.bytes().all(|b| b.is_ascii_digit())
            && revision
                .parse::<i32>()
                .is_ok_and(|revision| revision >= PREWARM_WRAPPER_REVISION)
    })
}

/// The log words for `bale_rav1g_prewarm`'s return (the fork's bale/README.md: 0 started, 1 already
/// started, -1 could not start its thread).
pub fn prewarm_result(code: i32) -> String {
    match code {
        0 => "started".into(),
        1 => "already started".into(),
        -1 => "failed (could not start its thread)".into(),
        other => format!("unexpected return {other}"),
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
    /// Bale asked this mount's stage or rav1g's display pass for dithered 8-bit output
    /// (`bale_tonemap_output_depth=8`, an 8-bit SDR display): marked NV12 is the request met.
    #[serde(default)]
    pub nv12_requested: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct Verdict {
    /// "direct" (rav1g's own texture), "stage" (the fork's stage or rav1g's display pass processed
    /// the frame), "dav1d" (no mark: the open fell back to dav1d; accepted, the chain's next step) or
    /// "reject".
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
            if !(p010 || nv12 && (args.allow_nv12_stage || args.nv12_requested)) {
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
    /// Delivered source height, never the scaled display output; unknown fails closed.
    pub source_height: Option<i64>,
}

/// Whether a failed mount of at most 1080 lines remounts once on dav1d/D3D11 instead of spending the
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
        && matches!(args.source_height, Some(1..=1080))
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
    /// Why rav1g.dll is not usable ([`LibraryStatus::detail`]); `None` when it is.
    pub library: Option<String>,
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
    let library = args
        .library
        .as_deref()
        .map(|why| format!(" · {why}"))
        .unwrap_or_default();
    if !args.rav1g_route {
        let reason = if args.remounted {
            " (remounted after a rav1g failure)"
        } else if args.selected == "rav1g" && args.library.is_some() {
            " (rav1g unavailable)"
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
            library_usable: true,
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
        // No usable rav1g.dll: dav1d with its own tuning, as before rav1g.
        let mut args = route_args("rav1g");
        args.library_usable = false;
        assert_eq!(route(&args), route(&route_args("dav1d")));
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
        assert!(!dav1d.downscale_stage);
    }

    #[test]
    fn only_sdr_larger_than_the_display_gets_rav1gs_downscale_stage() {
        // 4K SDR BT.709 on a 1080p display: rav1g's display pass scales it.
        assert!(route(&route_args("rav1g")).downscale_stage);
        let mut fits = route_args("rav1g");
        (fits.video_width, fits.video_height) = (Some(1920), Some(1080));
        assert!(!route(&fits).downscale_stage);
        // Unknown colour tags are SDR to the fork (BT.709 assumed at 720p and up).
        let mut untagged = route_args("rav1g");
        untagged.versions = None;
        assert!(route(&untagged).downscale_stage);
        // PQ/HLG or BT.2020 get no stage from the size alone: the tone map on SDR, passthrough on HDR.
        for (primaries, transfer, space) in [
            ("bt2020", "smpte2084", "bt2020nc"),
            ("bt2020", "arib-std-b67", "bt2020nc"),
            ("bt2020", "bt709", "bt2020nc"),
            ("bt709", "smpte2084", "bt709"),
        ] {
            let mut hdr = route_args("rav1g");
            hdr.versions = Some(vec![version(primaries, transfer, space, "24")]);
            let decision = route(&hdr);
            assert!(decision.rav1g && decision.output_width.is_some());
            assert!(!decision.downscale_stage, "{primaries} {transfer}");
        }
    }

    #[test]
    fn rav1g_route_sets_buffer_stage_and_display_size() {
        let decision = route(&route_args("rav1g"));
        // 250 ms at every resolution; the cap follows the frame rate.
        assert_eq!(decision.buffer_ms, Some(BUFFER_MS));
        assert_eq!(decision.decode_ahead_frames, Some(8));
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
        assert_eq!(decision.buffer_ms, Some(BUFFER_MS));
    }

    #[test]
    fn prewarm_needs_wrapper_bale_48() {
        assert!(prewarm_supported("2.1.0-bale.48"));
        assert!(prewarm_supported("2.1.0-bale.120"));
        assert!(!prewarm_supported("2.1.0-bale.47"));
        assert!(!prewarm_supported("2.1.0-bale."));
        assert!(!prewarm_supported("2.1.0-bale.4x"));
        assert!(!prewarm_supported("2.1.0"));
        assert!(!prewarm_supported(""));
    }

    #[test]
    fn prewarm_results_read_as_the_fork_documents_them() {
        assert_eq!(prewarm_result(0), "started");
        assert_eq!(prewarm_result(1), "already started");
        assert_eq!(prewarm_result(-1), "failed (could not start its thread)");
        assert_eq!(prewarm_result(7), "unexpected return 7");
    }

    #[test]
    fn output_texture_cap_follows_the_frame_rate() {
        assert_eq!(output_textures(Some(24000.0 / 1001.0)), Some(12));
        assert_eq!(output_textures(Some(24.0)), Some(12));
        assert_eq!(output_textures(Some(25.0)), Some(13));
        assert_eq!(output_textures(Some(30.0)), Some(14));
        assert_eq!(output_textures(Some(59.94)), Some(21));
        assert_eq!(output_textures(Some(120.0)), Some(36));
        assert_eq!(output_textures(None), None);
        assert_eq!(output_textures(Some(f64::NAN)), None);
        assert_eq!(output_textures(Some(0.0)), None);
        assert_eq!(output_textures(Some(1000.0)), None);
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
            nv12_requested: false,
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
            nv12_requested: false,
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
            nv12_requested: false,
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
            output: nv12.clone(),
            sdr_display: true,
            allow_nv12_stage: true,
            nv12_requested: false,
        };
        assert_eq!(output_verdict(&args).kind, "stage");
        // Dithered 8-bit output Bale asked for (an 8-bit SDR display) is the stage's NV12 too.
        let requested = VerdictArgs {
            source: SourceFacts {
                bit_depth: Some(10),
                ..SourceFacts::default()
            },
            output: nv12,
            sdr_display: true,
            allow_nv12_stage: false,
            nv12_requested: true,
        };
        assert_eq!(output_verdict(&requested).kind, "stage");
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
            source_height: Some(1080),
        };
        assert!(remount_on_dav1d(&base));
        assert!(remount_on_dav1d(&RemountArgs {
            source_height: Some(720),
            ..base
        }));
        let changes: [fn(&mut RemountArgs); 11] = [
            |a: &mut RemountArgs| a.rav1g_route = false,
            |a: &mut RemountArgs| a.rav1g_active = false,
            |a: &mut RemountArgs| a.remounted = true,
            |a: &mut RemountArgs| a.hdr_output_failed = true,
            |a: &mut RemountArgs| a.subtitle_failure = true,
            |a: &mut RemountArgs| a.start_budget = true,
            |a: &mut RemountArgs| a.source_height = Some(1081),
            |a: &mut RemountArgs| a.source_height = Some(2160),
            |a: &mut RemountArgs| a.source_height = None,
            |a: &mut RemountArgs| a.source_height = Some(0),
            |a: &mut RemountArgs| a.source_height = Some(-1),
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
            library: None,
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
        args.library = Some("rav1g.dll not packaged".into());
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
        // No usable library: the route never asked for rav1g.
        args.library = Some("rav1g.dll interface 2, expected 3".into());
        assert_eq!(
            describe(&args),
            "dav1d (rav1g unavailable) · switch rav1g · rav1g.dll interface 2, expected 3"
        );
    }

    fn library(
        present: bool,
        loaded: bool,
        missing: Option<&str>,
        abi: Option<u32>,
    ) -> LibraryStatus {
        library_status(&LibraryArgs {
            present,
            loaded,
            missing_export: missing.map(str::to_owned),
            abi_version: abi,
        })
    }

    #[test]
    fn a_library_is_usable_only_as_the_fork_loads_it() {
        let usable = library(true, true, None, Some(LIBRARY_ABI_VERSION));
        assert!(usable.usable);
        assert_eq!(usable.detail, "rav1g.dll usable (interface 3)");
        let refused = [
            (library(false, false, None, None), "rav1g.dll not packaged"),
            (library(true, false, None, None), "rav1g.dll failed to load"),
            (
                library(true, true, Some("rav1g_decoder_drain"), Some(3)),
                "rav1g.dll lacks rav1g_decoder_drain",
            ),
            // Only the list's names reach the text.
            (
                library(true, true, Some("evil\nname"), Some(3)),
                "rav1g.dll lacks an export",
            ),
            (
                library(true, true, None, Some(2)),
                "rav1g.dll interface 2, expected 3",
            ),
            (
                library(true, true, None, None),
                "rav1g.dll interface unknown",
            ),
        ];
        for (status, detail) in refused {
            assert!(!status.usable);
            assert_eq!(status.detail, detail);
        }
    }

    #[test]
    fn the_library_list_is_the_forks() {
        assert_eq!(LIBRARY_EXPORTS.len(), 12);
        assert_eq!(LIBRARY_EXPORTS[0], "rav1g_abi_version");
        assert!(
            LIBRARY_EXPORTS
                .iter()
                .all(|name| name.starts_with("rav1g_"))
        );
    }
}
