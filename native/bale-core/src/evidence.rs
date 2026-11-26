//! Ported from `ConsoleEvidence` (src/Bale.Core/Playback/PlaybackCapabilities.cs): the measured
//! baseline in docs/player-plan.md. Installed converters are not playback proof.

use serde::Serialize;

use crate::hardware::{DecodeSize, Limits};
use crate::models::HdrCapabilities;
use crate::software_decode::{
    AUDIO_CODECS, FALLBACK_ENVELOPES, FFMPEG_VIDEO_CODECS, SYSTEM_AUDIO_CODECS, VIDEO_CODECS,
};

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

/// MP4 and MKV (Windows reads both) plus every container only FFmpeg's demuxer opens.
const CONTAINERS: &[&str] = &[
    "mp4", "mkv", "matroska", "avi", "mov", "m4v", "mpegts", "ts", "m2ts", "mts", "3gp", "3g2",
    "mpeg", "vob", "ogg", "webm", "flv", "asf",
];

pub fn baseline() -> Baseline {
    Baseline {
        // AVI needs FFmpeg; see docs/player-plan.md.
        containers: CONTAINERS,
        // VP9 joins only when the console's decoder probe reports it (for_hardware). A .webm file
        // needs no container claim: silo-server's detectContainer (internal/scanner/probe.go)
        // names Matroska and WebM both "mkv".
        video_codecs: FFMPEG_VIDEO_CODECS,
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
        video_decode: video_decode(None, false),
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

/// The production claims for what the console's hardware decoders report, with rav1g's AV1 entry
/// where its library is usable. `None` for missing or empty limits (the probe failed or found
/// nothing): the caller keeps the fixed 1080p baseline.
pub fn for_hardware(hardware: Option<&Limits>, rav1g: bool) -> Option<HardwareClaims> {
    let hardware = hardware.filter(|limits| !limits.is_empty())?;
    let decode = video_decode(Some(hardware), rav1g);
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

/// Hardware claims from the decoder probe plus the AV1 and MPEG-4 software claims. Missing or empty
/// limits keep the fixed H.264/HEVC 1080p baseline with no VP9. `rav1g`: rav1g.dll is usable
/// (Rav1gLibrary), so its 2160p60 AV1 entry joins dav1d's 1080p60 one.
pub fn video_decode(hardware: Option<&Limits>, rav1g: bool) -> Vec<VideoDecodeClaim> {
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
    // such streams transcode too. It stays beside rav1g's entry: at 1080p and below a stream rav1g
    // declines or fails on decodes on dav1d (the fork's in-open fallback, Bale's one dav1d remount).
    claims.push(av1("dav1d", 1920, 1080, 60.0, None));
    // rav1g, AV1 in D3D12 compute shaders (owner, 2026-11-26: the default AV1 decoder in every build),
    // where its library is usable: Main 8/10-bit up to 2160p60 at the 40 Mbps soft ceiling (constants
    // below). Platform-attested rates are absolute (the server trades size for rate only on exact
    // evidence), and it takes a source any entry admits.
    if rav1g {
        claims.push(av1(
            "rav1g",
            RAV1G_MAX_WIDTH,
            RAV1G_MAX_HEIGHT,
            RAV1G_MAX_FRAME_RATE,
            Some(RAV1G_MAX_BITRATE_KBPS),
        ));
    }
    // The software-only codecs FFmpeg decodes (owner, 2026-11-17, as AetherEngine declares its build):
    // one software entry per codec, capped at 60 fps (owner, 2026-11-24: the console's app-mode output
    // is at most 60 Hz, so faster files transcode). AV1 keeps its own entries above.
    // The hardware codecs (H.264, HEVC, VP9, VC-1, MPEG-2) get no such entry: outside the bounded
    // fallback below the player refuses their software route (FfmpegSource; docs/software-decode.md),
    // so the server transcodes what the hardware entries above don't admit instead of planning a direct
    // play that fails to open. silo-server compares software profiles literally to ffprobe's names even
    // under platform_attested evidence, so these name none.
    // Measured before: MPEG-4 Part 2 at 512x384 (0.06 cores), 720x480 (0.08), 1080p (0.30).
    for &codec in FFMPEG_VIDEO_CODECS {
        if codec == "av1" || !VIDEO_CODECS.contains(&codec) {
            continue;
        }
        claims.push(VideoDecodeClaim {
            codec,
            decoder_name: Some("FFmpeg"),
            profiles: vec![],
            levels: vec![],
            bit_depths: vec![],
            max_width: None,
            max_height: None,
            max_frame_rate: Some(60.0),
            max_bitrate_kbps: None,
            hardware: false,
        });
    }
    // The hardware codecs' software fallback (owner, 2026-11-24, docs/software-decode.md phases A
    // and B): only the console-measured envelopes (1080p, and 8-bit H.264 up to 4K30 at 50 Mbps),
    // with profile names and bit depths so silo-server keeps them off files the hardware takes and
    // off unknown profiles. The server takes a source any entry admits; what it can't check (chroma,
    // and any bound when the profile-blind hardware entry admitted the file) the player does
    // (software_decode::fallback_admits). Claimed with or without a probed hardware decoder: the
    // software decoder doesn't depend on it. The 4K entry needs the stage on software frames; a
    // device without it drops that entry (`DeviceFilter::ForSoftwareStage`).
    for envelope in FALLBACK_ENVELOPES {
        claims.push(VideoDecodeClaim {
            codec: envelope.codec,
            decoder_name: Some("FFmpeg"),
            profiles: envelope.profiles.to_vec(),
            levels: vec![],
            bit_depths: envelope.bit_depths.to_vec(),
            max_width: Some(envelope.max_width),
            max_height: Some(envelope.max_height),
            max_frame_rate: Some(envelope.max_frame_rate),
            max_bitrate_kbps: envelope.max_bitrate_kbps,
            hardware: false,
        });
    }
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

// rav1g's AV1 claim (owner, 2026-11-26): tested on the console up to 4K60 and reliable to about
// 40-60 Mbps; real content stays below 40 Mbps, so 40 Mbps is the soft ceiling. silo-server's
// videoDecodeTakesSourceV3 (capabilities_v3.go) holds the source's bitrate to `max_bitrate_kbps`
// (a file of unknown bitrate passes), so a faster 4K AV1 file transcodes or takes a lower version.
pub const RAV1G_MAX_WIDTH: i32 = 3840;
pub const RAV1G_MAX_HEIGHT: i32 = 2160;
pub const RAV1G_MAX_FRAME_RATE: f64 = 60.0;
pub const RAV1G_MAX_BITRATE_KBPS: i32 = 40_000;

// Software-class entries (hardware: false, so the server needs software_video_decode_v1 and devices
// without measured software video drop them): rav1g runs in compute shaders, not a fixed-function
// decoder, and its route is FFmpeg's software AV1 route (route::hardware_admits never takes AV1).
fn av1(
    decoder: &'static str,
    width: i32,
    height: i32,
    fps: f64,
    bitrate: Option<i32>,
) -> VideoDecodeClaim {
    VideoDecodeClaim {
        codec: "av1",
        decoder_name: Some(decoder),
        profiles: vec!["main"],
        levels: vec![],
        bit_depths: vec![8, 10],
        max_width: Some(width),
        max_height: Some(height),
        max_frame_rate: Some(fps),
        max_bitrate_kbps: bitrate,
        hardware: false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::hardware::DecodeSize;

    fn av1_entries(claims: &[VideoDecodeClaim]) -> Vec<&VideoDecodeClaim> {
        claims.iter().filter(|claim| claim.codec == "av1").collect()
    }

    #[test]
    fn dav1d_alone_claims_1080p60_without_a_bitrate_bound() {
        for claims in [video_decode(None, false), baseline().video_decode] {
            let av1 = av1_entries(&claims);
            assert_eq!(av1.len(), 1);
            let dav1d = av1[0];
            assert_eq!(dav1d.decoder_name, Some("dav1d"));
            assert_eq!(
                (dav1d.max_width, dav1d.max_height),
                (Some(1920), Some(1080))
            );
            assert_eq!(dav1d.max_frame_rate, Some(60.0));
            assert_eq!(dav1d.max_bitrate_kbps, None);
            assert!(!dav1d.hardware);
        }
    }

    #[test]
    fn a_usable_rav1g_adds_2160p60_at_the_40_mbps_soft_ceiling() {
        let claims = video_decode(None, true);
        let entries = av1_entries(&claims);
        assert_eq!(entries.len(), 2);
        // dav1d's measured entry stays for what rav1g declines at 1080p and below.
        assert_eq!(entries[0], &av1("dav1d", 1920, 1080, 60.0, None));
        let rav1g = entries[1];
        assert_eq!(rav1g.decoder_name, Some("rav1g"));
        assert_eq!(rav1g.profiles, vec!["main"]);
        assert_eq!(rav1g.bit_depths, vec![8, 10]);
        assert_eq!(
            (rav1g.max_width, rav1g.max_height),
            (Some(3840), Some(2160))
        );
        assert_eq!(rav1g.max_frame_rate, Some(60.0));
        assert_eq!(rav1g.max_bitrate_kbps, Some(40_000));
        assert!(!rav1g.hardware);
        // Everything else is the same claim set.
        let without: Vec<_> = claims
            .iter()
            .filter(|claim| claim.decoder_name != Some("rav1g"))
            .collect();
        assert_eq!(
            without,
            video_decode(None, false).iter().collect::<Vec<_>>()
        );
    }

    #[test]
    fn the_probed_hardware_claims_carry_rav1g_too() {
        let hd = DecodeSize {
            width: 1920,
            height: 1080,
        };
        let limits = Limits {
            h264: Some(hd),
            hevc_main: Some(hd),
            hevc_main10: Some(hd),
            ..Limits::default()
        };
        let with = for_hardware(Some(&limits), true).unwrap();
        let without = for_hardware(Some(&limits), false).unwrap();
        assert_eq!(with.video_codecs, without.video_codecs);
        assert_eq!(av1_entries(&with.video_decode).len(), 2);
        assert_eq!(av1_entries(&without.video_decode).len(), 1);
        assert!(for_hardware(None, true).is_none());
    }
}
