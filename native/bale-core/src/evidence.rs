//! Ported from `ConsoleEvidence` (src/Bale.Core/Playback/PlaybackCapabilities.cs): the measured
//! baseline in docs/player-plan.md. Installed converters are not playback proof.

use serde::Serialize;

use crate::hardware::{DecodeSize, Limits};
use crate::models::HdrCapabilities;
use crate::software_decode::{AUDIO_CODECS, SYSTEM_AUDIO_CODECS};

/// Image subtitle codecs (silo-server's names) the overlay draws from the container.
pub const EMBEDDED_BITMAP_SUBTITLE_CODECS: &[&str] =
    &["hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle"];

/// `VideoDecodeCapability`, in the server's wire shape (PlaybackModels.kt).
#[derive(Serialize, Debug, PartialEq, Clone)]
pub struct VideoDecodeClaim {
    codec: &'static str,
    decoder_name: Option<&'static str>,
    profiles: Vec<&'static str>,
    levels: Vec<i32>,
    bit_depths: Vec<i32>,
    max_width: Option<i32>,
    max_height: Option<i32>,
    max_frame_rate: Option<f64>,
    max_bitrate_kbps: Option<i32>,
    hardware: bool,
}

#[derive(Serialize)]
pub struct NativeEmbeddedSubtitles {
    container: &'static str,
    codecs: &'static [&'static str],
    track_identity: &'static str,
    ass_styling: bool,
    font_attachments: bool,
}

#[derive(Serialize)]
pub struct Subtitles {
    native_embedded: Vec<NativeEmbeddedSubtitles>,
    embedded_text: bool,
    sidecar_text: bool,
    ass_styling: bool,
    embedded_bitmap: bool,
    sidecar_bitmap: bool,
    font_attachments: bool,
}

/// `ConsoleEvidence.Default`, every field written: System.Text.Json's generated code sets an
/// init-only property missing from the JSON to null rather than to its C# initializer.
#[derive(Serialize)]
pub struct Baseline {
    containers: &'static [&'static str],
    video_codecs: &'static [&'static str],
    audio_decode_codecs: Vec<&'static str>,
    audio_passthrough_codecs: [&'static str; 0],
    max_channels: i32,
    hdr: HdrCapabilities,
    subtitles: Subtitles,
    // Codec names alone are not the exact profile/level tuples an "exact" claim requires.
    video_evidence: &'static str,
    audio_evidence: &'static str,
    hardware_video_codecs: [&'static str; 0],
    video_decode: Vec<VideoDecodeClaim>,
    passthrough_entries: [(); 0],
    display_hdr_evidence: &'static str,
    original_http_supported: bool,
    original_audio_track_selection_supported: bool,
    original_dolby_vision8_base_layer_supported: bool,
    original_dolby_vision7_base_layer_supported: bool,
    // Set per console once the pinned wrapper and bale_gpu tone-map ABI are found (PlaybackClaims).
    original_dynamic_range_mapping_supported: bool,
    progressive_supported: bool,
    hls_supported: bool,
    /// HLS through the Rust HLS reader and FFmpeg: packages carry the original's codecs but AV1.
    hls_ffmpeg_supported: bool,
}

pub fn baseline() -> Baseline {
    Baseline {
        // AVI needs FFmpeg; see docs/player-plan.md.
        containers: &["mp4", "mkv", "avi"],
        // VP9 joins only when the console's decoder probe reports it (for_hardware). A .webm file
        // needs no container claim: silo-server's detectContainer (internal/scanner/probe.go)
        // names Matroska and WebM both "mkv".
        video_codecs: &["h264", "hevc", "av1", "mpeg4"],
        // The system baseline plus bundled FFmpeg audio decode; no encoded output/passthrough promise.
        audio_decode_codecs: SYSTEM_AUDIO_CODECS
            .iter()
            .chain(AUDIO_CODECS)
            .copied()
            .collect(),
        // TrueHD/DTS converters exist, but no receiver has verified passthrough yet.
        audio_passthrough_codecs: [],
        max_channels: 8,
        // Unknown until the player reads the connected display; installed HEVC is not HDR evidence.
        hdr: HdrCapabilities::default(),
        // Bale's own overlay (bale_gpu.dll, libass) draws fetched text sidecars: ASS/SSA with their
        // styling and the server's font bundle, SubRip and WebVTT. It also draws image tracks the
        // FFmpeg demuxer decodes from a direct MKV (wrapper patch 0022), claimed per stream index.
        // PlaybackStartBuilder keeps native_embedded on original_http and opts packaged deliveries
        // into PGS .sup windows. The server gates embedded_bitmap to embedded PGS alone.
        subtitles: Subtitles {
            native_embedded: vec![NativeEmbeddedSubtitles {
                container: "mkv",
                codecs: EMBEDDED_BITMAP_SUBTITLE_CODECS,
                track_identity: "ffmpeg_stream_index",
                ass_styling: false,
                font_attachments: false,
            }],
            embedded_text: false,
            sidecar_text: true,
            ass_styling: true,
            embedded_bitmap: true,
            sidecar_bitmap: false,
            font_attachments: true,
        },
        video_evidence: "platform_attested",
        audio_evidence: "platform_attested",
        hardware_video_codecs: [],
        video_decode: video_decode(None, 1920, 1080),
        passthrough_entries: [],
        display_hdr_evidence: "unknown",
        original_http_supported: true,
        original_audio_track_selection_supported: true,
        original_dolby_vision8_base_layer_supported: true,
        original_dolby_vision7_base_layer_supported: true,
        original_dynamic_range_mapping_supported: false,
        // Forward-only remuxes play through FFmpeg's non-seekable input; seeks reanchor (PlaybackProgressive).
        progressive_supported: true,
        hls_supported: true,
        hls_ffmpeg_supported: true,
    }
}

/// The codec list and decode claims that replace the baseline's for a probed console.
#[derive(Serialize, Debug, PartialEq)]
pub struct HardwareClaims {
    video_codecs: Vec<&'static str>,
    video_decode: Vec<VideoDecodeClaim>,
}

/// The production claims for what the console's hardware decoders report. `None` for missing or
/// empty limits (the probe failed or found nothing): the caller keeps the fixed 1080p baseline.
pub fn for_hardware(hardware: Option<&Limits>) -> Option<HardwareClaims> {
    let hardware = hardware.filter(|limits| !limits.is_empty())?;
    let decode = video_decode(Some(hardware), 1920, 1080);
    // A codec is named only while it has a decode entry, so VP9 appears only when probed.
    let mut codecs = Vec::new();
    for claim in &decode {
        if !codecs.contains(&claim.codec) {
            codecs.push(claim.codec);
        }
    }
    Some(HardwareClaims {
        video_codecs: codecs,
        video_decode: decode,
    })
}

fn baseline_entry(codec: &'static str, bit_depths: Vec<i32>) -> VideoDecodeClaim {
    VideoDecodeClaim {
        codec,
        decoder_name: Some("Media Foundation"),
        profiles: vec![],
        levels: vec![],
        bit_depths,
        max_width: Some(1920),
        max_height: Some(1080),
        max_frame_rate: Some(60.0),
        max_bitrate_kbps: None,
        hardware: true,
    }
}

/// Hardware claims from the decoder probe plus measured AV1 and MPEG-4 software claims. Missing or
/// empty limits keep the fixed H.264/HEVC 1080p baseline with no VP9. The AV1 bounds (positive;
/// the C# wrapper checks) let debug callers test unmeasured sizes.
pub fn video_decode(
    hardware: Option<&Limits>,
    av1_max_width: i32,
    av1_max_height: i32,
) -> Vec<VideoDecodeClaim> {
    // Mirror silo-apple's iosApp/iosApp/Shared/AppleDecodeCapabilities.swift bounded attestation.
    let mut claims = Vec::new();
    match hardware.filter(|limits| !limits.is_empty()) {
        None => {
            // No decoder report to go on: the sizes MP4 playback was verified at on the console
            // (docs/player-plan.md), H.264 8-bit and HEVC 8/10-bit.
            claims.push(baseline_entry("h264", vec![8]));
            claims.push(baseline_entry("hevc", vec![8, 10]));
        }
        Some(hardware) => {
            // The hardware decoder's own report is the limit (docs/player-plan.md). Display fit is
            // Auto's version choice (DisplayVersionMatch), never a smaller claim.
            add_hardware(&mut claims, "h264", hardware.h264, None);
            add_hardware(
                &mut claims,
                "hevc",
                hardware.hevc_main,
                hardware.hevc_main10,
            );
            // A partial probe keeps what it measured and gives the H.264/HEVC depths it did not
            // measure the same fixed 1080p baseline entries as a failed probe; a complete probe that
            // found no decoder is trusted as it is.
            if hardware.partial {
                let hevc_depths: Vec<i32> = [(8, hardware.hevc_main), (10, hardware.hevc_main10)]
                    .into_iter()
                    .filter(|(_, size)| size.is_none())
                    .map(|(depth, _)| depth)
                    .collect();
                if hardware.h264.is_none() {
                    claims.push(baseline_entry("h264", vec![8]));
                }
                if !hevc_depths.is_empty() {
                    claims.push(baseline_entry("hevc", hevc_depths));
                }
            }
            add_hardware(
                &mut claims,
                "vp9",
                hardware.vp9_profile0,
                hardware.vp9_profile2,
            );
            // FFmpegInteropX's compressed providers feed WVC1/MPEG2 to Media Foundation, like VP9.
            // Only the console's matching NV12 VLD profile admits these original-file paths.
            add_hardware(&mut claims, "vc1", hardware.vc1, None);
            add_hardware(&mut claims, "mpeg2video", hardware.mpeg2, None);
        }
    }
    // dav1d zero-copy, measured in real time: 0.1.230 Fight Club 1080p23.976 10-bit (~1.3 cores)
    // and 0.1.439 SmackDown 1080p59.94 10-bit (0 late frames, ~3 cores); 8-bit clips too.
    // Zero-copy takes only Main (4:2:0, 8/10-bit); High (4:4:4) and Professional (4:2:2, 12-bit)
    // transcode. The server lowercases ffprobe's "Main" and fails an empty or unknown profile, so
    // such streams transcode too.
    claims.push(av1(av1_max_width.min(1920), av1_max_height.min(1080), 60.0));
    // A larger debug bound stays at 30 fps. Platform-attested rates are absolute (the server trades
    // size for rate only on exact evidence), and it takes a source any entry admits.
    if av1_max_width > 1920 || av1_max_height > 1080 {
        claims.push(av1(av1_max_width, av1_max_height, 30.0));
    }
    // Owner's Series X software probes: 30 s each, 0 late frames at 512x384 (0.06 cores), 720x480
    // (0.08) and 1920x1080 (0.30). No 60 fps evidence: cap at 30 fps. Ordinary FFmpeg output, not
    // AV1 zero-copy. silo-server capabilities_v3.go compares software profiles literally to
    // lowercased ffprobe names, even under platform_attested evidence.
    claims.push(VideoDecodeClaim {
        codec: "mpeg4",
        decoder_name: Some("FFmpeg"),
        profiles: vec!["simple profile", "advanced simple profile"],
        levels: vec![],
        bit_depths: vec![8],
        max_width: Some(1920),
        max_height: Some(1080),
        max_frame_rate: Some(30.0),
        max_bitrate_kbps: None,
        hardware: false,
    });
    claims
}

/// One entry per codec at the probed size. If the 8-bit and 10-bit profiles report different
/// sizes, each depth gets its own entry so neither is overclaimed; the server takes a source any
/// entry admits.
fn add_hardware(
    claims: &mut Vec<VideoDecodeClaim>,
    codec: &'static str,
    eight_bit: Option<DecodeSize>,
    ten_bit: Option<DecodeSize>,
) {
    match (eight_bit, ten_bit) {
        (Some(both), Some(ten)) if both == ten => {
            claims.push(hardware_entry(codec, vec![8, 10], both))
        }
        _ => {
            if let Some(low) = eight_bit {
                claims.push(hardware_entry(codec, vec![8], low));
            }
            if let Some(high) = ten_bit {
                claims.push(hardware_entry(codec, vec![10], high));
            }
        }
    }
}

// 60 fps: Microsoft's Xbox guidance has 60 Hz media modes and no 120 Hz ones
// (learn.microsoft.com/windows/uwp/audio-video-camera/hevc-xbox). No bitrate bound: the decoder
// reports none, and silo-server's videoDecodeTakesSourceV3 (capabilities_v3.go) reads an absent
// max_bitrate_kbps as unconstrained.
// No profile list. Under Bale's platform_attested evidence silo-server's videoEligibleV3 skips
// profile and level lists on hardware entries, so "main"/"main 10" or "profile 0"/"profile 2" would
// not keep HEVC RExt (Main 4:2:2 10) or VP9 profiles 1 and 3 out. Only the exact tier reads them,
// by plain lowercase equality with ffprobe's name, and there a file with no recorded profile would
// be refused. Bale's own version matching (PlaybackFormatting.Decodes) does read lists, so adding
// one would also make it disagree with the server. Bit depth is the filter the server applies.
fn hardware_entry(codec: &'static str, bit_depths: Vec<i32>, size: DecodeSize) -> VideoDecodeClaim {
    VideoDecodeClaim {
        codec,
        decoder_name: Some("Media Foundation"),
        profiles: vec![],
        levels: vec![],
        bit_depths,
        max_width: Some(size.width),
        max_height: Some(size.height),
        max_frame_rate: Some(60.0),
        max_bitrate_kbps: None,
        hardware: true,
    }
}

fn av1(width: i32, height: i32, fps: f64) -> VideoDecodeClaim {
    VideoDecodeClaim {
        codec: "av1",
        decoder_name: Some("dav1d"),
        profiles: vec!["main"],
        levels: vec![],
        bit_depths: vec![8, 10],
        max_width: Some(width),
        max_height: Some(height),
        max_frame_rate: Some(fps),
        max_bitrate_kbps: None,
        hardware: false,
    }
}
