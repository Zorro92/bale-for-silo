//! Ported from `SoftwareDecodePolicy` (src/Bale.Core/Playback/SoftwareDecodePolicy.cs): the
//! bundled decoder policy, separate from the console's measured system decoders.

use serde::{Deserialize, Serialize};

use crate::dotnet;

// Every video codec the bundled FFmpeg build decodes in software that Bale declares (owner,
// 2026-11-17: as AetherEngine does, declare everything FFmpeg decodes; Apple's aetherOriginalHTTP
// list plus MPEG-1, H.263, Theora, MJPEG, ProRes and DNxHD). The build disables no decoders. Hardware
// codecs (SYSTEM_VIDEO_CODECS) fall back to FFmpeg software when the console's decoder cannot take
// the file (route::hardware_admits). Names are ffprobe's, as silo-server compares them.
pub const FFMPEG_VIDEO_CODECS: &[&str] = &[
    "h264",
    "hevc",
    "av1",
    "vp9",
    "vp8",
    "mpeg4",
    "mpeg2video",
    "mpeg1video",
    "vc1",
    "msmpeg4v1",
    "msmpeg4v2",
    "msmpeg4v3",
    "wmv1",
    "wmv2",
    "wmv3",
    "h263",
    "theora",
    "qtrle",
    "mjpeg",
    "prores",
    "dnxhd",
];

// Software-only video codecs: FFMPEG_VIDEO_CODECS that no console hardware decoder takes. Filters
// that withdraw "software video" drop these from the flat lists (hardware codecs keep their
// hardware entries and lose only their software ones).
pub const VIDEO_CODECS: &[&str] = &[
    "av1",
    "mpeg4",
    "vp8",
    "mpeg1video",
    "msmpeg4v1",
    "msmpeg4v2",
    "msmpeg4v3",
    "wmv1",
    "wmv2",
    "wmv3",
    "h263",
    "theora",
    "qtrle",
    "mjpeg",
    "prores",
    "dnxhd",
];

// What silo-server may package (progressive fMP4, HLS) for Bale: the codecs verified in packages
// before 2026-11-17. The newly declared software codecs stay on the original file, as Apple keeps
// packaged routes on their native vocabulary.
pub const PACKAGED_VIDEO_CODECS: &[&str] =
    &["h264", "hevc", "av1", "vp9", "vc1", "mpeg2video", "mpeg4"];

// Containers only FFmpeg's demuxer opens (everything but MP4/MKV, which Windows also reads). Spellings
// follow silo-server's scanner (Apple's aetherVideoContainers): MPEG program streams are "mpeg".
pub const FFMPEG_ONLY_CONTAINERS: &[&str] = &[
    "avi", "mov", "m4v", "mpegts", "ts", "m2ts", "mts", "3gp", "3g2", "mpeg", "vob", "ogg", "webm",
    "flv", "asf",
];

// Codecs the system (hardware) decoders may take. A claim for one still needs the fixed
// baseline or the console's decoder probe (ConsoleEvidence.ForHardware).
pub const SYSTEM_VIDEO_CODECS: &[&str] = &["h264", "hevc", "vp9", "vc1", "mpeg2video"];

// Hardware codecs that play only through FFmpeg's demuxer feeding the system decoder.
// Windows' own WebM source opened VP9 but never left Paused at 0 s, and its MP4 source and
// AdaptiveMediaSource are untested for VP9 (docs/player-plan.md). So every VP9 file or package
// takes the FFmpeg demuxer, Windows' HLS never gets it, and "ffmpeg off" withdraws the claim.
// VC-1/MPEG-2 follow the same rule: only their compressed FFmpeg providers feeding system
// decoders are trusted, never a Windows container source. Since the owner's 2026-11-16 call,
// packaged deliveries read by FFmpeg (progressive, HLS through FFmpeg) claim them too.
pub const DEMUXED_VIDEO_CODECS: &[&str] = &["vp9", "vc1", "mpeg2video"];

// Hardware codecs FFmpeg decodes in software where the console's decoder can't take the file
// (route::hardware_admits), only inside FALLBACK_ENVELOPES (owner, 2026-11-24: docs/software-decode.md
// phase A). VC-1 and MPEG-2 have no files past their 1080p hardware decoders worth it.
pub const SOFTWARE_FALLBACK_VIDEO_CODECS: &[&str] = &["h264", "hevc", "vp9"];

/// One console-measured software envelope of a hardware codec. The claims
/// (`evidence::video_decode`) and the player's gate (`fallback_admits`) both come from these.
pub struct FallbackEnvelope {
    pub codec: &'static str,
    /// ffprobe's profile names, lower-cased: silo-server compares a software entry's profiles
    /// literally (capabilities_v3.go videoProfileSupportedV3), so a file the hardware takes ("high",
    /// "main 10", "profile 0") or one with no recorded profile never matches.
    pub profiles: &'static [&'static str],
    pub bit_depths: &'static [i32],
    pub max_frame_rate: f64,
}

/// Every envelope's size. Media Foundation must never scale software 4K SDR down to a smaller
/// display (its video processor leaks per open, docs/player-plan.md), so larger files wait for
/// software frames through Bale's stage (phase B); the 4K30 CPU fits already.
pub const FALLBACK_MAX_WIDTH: i32 = 1920;
pub const FALLBACK_MAX_HEIGHT: i32 = 1080;

/// 4:2:2, 4:4:4 and 4:4:0 decode in software up to this rate (owner, 2026-11-24). The claims can't
/// say chroma (silo-server has no field for it, and HEVC RExt is one profile for 4:2:0 12-bit and
/// 4:2:2), so the player applies it (`fallback_admits`).
pub const FALLBACK_NON_420_MAX_FRAME_RATE: f64 = 30.0;

/// Measured on the Series X (2026-11-24, docs/software-decode.md "Measured on the console"), 0 missed
/// frames each: H.264 High 10 1080p24 8 Mbps 0.78 cores and 1080p60 20 Mbps 1.90; H.264 High 4:2:2
/// 10-bit 1080p30 50 Mbps 1.80; HEVC RExt 4:2:2 10-bit 1080p30 30 Mbps 2.08 and 12-bit 4:2:0 1080p24
/// 1.06; VP9 profile 1 4:4:4 1080p30 0.90. No bitrate bound was approved; those clips' rates (20-50
/// Mbps) are the measured ones.
pub const FALLBACK_ENVELOPES: &[FallbackEnvelope] = &[
    // 4:2:0 above 8 bits: up to 60 fps, the console's app-mode output (owner).
    FallbackEnvelope {
        codec: "h264",
        profiles: &["high 10"],
        bit_depths: &[8, 10],
        max_frame_rate: 60.0,
    },
    FallbackEnvelope {
        codec: "hevc",
        profiles: &["rext"],
        bit_depths: &[12],
        max_frame_rate: 60.0,
    },
    // 4:2:2 and 4:4:4: up to 30 fps.
    FallbackEnvelope {
        codec: "h264",
        profiles: &["high 4:2:2", "high 4:4:4 predictive"],
        bit_depths: &[8, 10],
        max_frame_rate: 30.0,
    },
    FallbackEnvelope {
        codec: "hevc",
        profiles: &["rext"],
        bit_depths: &[8, 10, 12],
        max_frame_rate: 30.0,
    },
    FallbackEnvelope {
        codec: "vp9",
        profiles: &["profile 1", "profile 3"],
        bit_depths: &[8, 10, 12],
        max_frame_rate: 30.0,
    },
];

// Measured Media Foundation playback, not merely installed decoder registrations.
pub const SYSTEM_AUDIO_CODECS: &[&str] = &["aac", "ac3", "eac3", "mp3"];

// silo-server capabilities_v3.go normalizeCodecV3 / protocol_v3.go spellings. Decode, not passthrough.
// The server compares audio claims to the stored ffprobe codec name exactly (after lowercasing),
// so LPCM needs each pcm_* spelling. PCM and MPEG-1 Layer II decode in FFmpeg: Windows' own MP4
// source was never verified with LPCM or MP2, so those files take the FFmpeg route like DTS.
// "pcm" is the route's normalized name for every pcm_* (normalize_codec).
pub const AUDIO_CODECS: &[&str] = &[
    "dts",
    "dts_hd",
    "truehd",
    "flac",
    "opus",
    "vorbis",
    "alac",
    "mp2",
    "pcm",
    "pcm_s16le",
    "pcm_s16be",
    "pcm_s24le",
    "pcm_s24be",
    "pcm_s32le",
    "pcm_s32be",
    "pcm_f32le",
    "pcm_f32be",
    "pcm_f64le",
    "pcm_f64be",
    "pcm_u8",
    "pcm_bluray",
    "pcm_dvd",
    // Scanner aliases (Apple's aetherOriginalHTTPAudioCodecs) and Windows Media audio, all FFmpeg decodes.
    "mlp",
    "dca",
    "dts-hd",
    "dtshd",
    "wmav1",
    "wmav2",
    "wmapro",
];

/// `PlaybackProtocol.SoftwareVideoDecodeFeature`.
pub const SOFTWARE_VIDEO_DECODE_FEATURE: &str = "software_video_decode_v1";
/// `PlaybackProtocol.ClientDolbyVision8BaseLayerClaim`.
pub const DOLBY_VISION8_BASE_LAYER_CLAIM: &str = "client_dv8_base_layer_fallback_v1";

/// `list.Contains(value)` where a null value is never contained.
pub fn contains(list: &[&str], value: Option<&str>) -> bool {
    value.is_some_and(|value| list.contains(&value))
}

/// Every list above, for the C# properties that expose them.
#[derive(Serialize)]
pub struct Lists {
    video_codecs: &'static [&'static str],
    ffmpeg_video_codecs: &'static [&'static str],
    packaged_video_codecs: &'static [&'static str],
    ffmpeg_only_containers: &'static [&'static str],
    system_video_codecs: &'static [&'static str],
    demuxed_video_codecs: &'static [&'static str],
    system_audio_codecs: &'static [&'static str],
    audio_codecs: &'static [&'static str],
    software_fallback_video_codecs: &'static [&'static str],
}

pub fn lists() -> Lists {
    Lists {
        video_codecs: VIDEO_CODECS,
        ffmpeg_video_codecs: FFMPEG_VIDEO_CODECS,
        packaged_video_codecs: PACKAGED_VIDEO_CODECS,
        ffmpeg_only_containers: FFMPEG_ONLY_CONTAINERS,
        system_video_codecs: SYSTEM_VIDEO_CODECS,
        demuxed_video_codecs: DEMUXED_VIDEO_CODECS,
        system_audio_codecs: SYSTEM_AUDIO_CODECS,
        audio_codecs: AUDIO_CODECS,
        software_fallback_video_codecs: SOFTWARE_FALLBACK_VIDEO_CODECS,
    }
}

/// Whether the player decodes this file in FFmpeg software as a hardware codec's fallback (phase A
/// of docs/software-decode.md): a software entry the attempt still claims admits it
/// (`formatting::decodes`, Bale's mirror of the server's matching: codec, profile, bit depth, size,
/// frame rate, bitrate), and a file not known to be 4:2:0 runs at most
/// `FALLBACK_NON_420_MAX_FRAME_RATE` (a known rate). The server can't apply the chroma rule, nor any
/// software bound when the profile-blind hardware entry admitted the file (HEVC RExt, VP9 profile
/// 1/3, H.264 4:2:2), so the player refuses everything else and the failure replan withdraws the
/// codec.
pub fn fallback_admits(
    version: &crate::models::FileVersion,
    claims: &[crate::models::DecodeClaim],
) -> bool {
    let track = version
        .video_tracks
        .as_ref()
        .and_then(|tracks| tracks.first());
    let Some(codec) = normalize_codec(
        dotnet::non_empty(track.and_then(|t| t.codec.as_deref()))
            .or(version.codec_video.as_deref()),
    ) else {
        return false;
    };
    if !SOFTWARE_FALLBACK_VIDEO_CODECS.contains(&codec.as_str()) {
        return false;
    }
    let software: Vec<crate::models::DecodeClaim> = claims
        .iter()
        .filter(|claim| !claim.hardware && claim.codec == codec)
        .cloned()
        .collect();
    if !crate::formatting::decodes(version, &software) {
        return false;
    }
    let profile = dotnet::non_empty(track.and_then(|t| t.profile.as_deref())).map(dotnet::lower);
    let pixel_format = track.and_then(|t| t.pixel_format.as_deref());
    chroma_420(&codec, profile.as_deref(), pixel_format) == Some(true)
        || crate::formatting::frame_rate(track.and_then(|t| t.frame_rate.as_deref()))
            .is_some_and(|fps| fps <= FALLBACK_NON_420_MAX_FRAME_RATE + 0.01)
}

/// Whether a file is 4:2:0 (or monochrome): from its pixel format (ffprobe's names), else from a
/// profile that allows no other chroma. `None` when neither says.
fn chroma_420(codec: &str, profile: Option<&str>, pixel_format: Option<&str>) -> Option<bool> {
    if let Some(format) = dotnet::trim_lower(pixel_format).filter(|format| !format.is_empty()) {
        return Some(
            format.contains("420")
                || ["nv12", "nv21", "p010", "p016", "gray"]
                    .iter()
                    .any(|prefix| format.starts_with(prefix)),
        );
    }
    let four_two_zero = match codec {
        "h264" => matches!(
            profile,
            Some(
                "constrained baseline"
                    | "baseline"
                    | "main"
                    | "extended"
                    | "high"
                    | "high 10"
                    | "high 10 intra"
            )
        ),
        "hevc" => matches!(profile, Some("main" | "main 10" | "main still picture")),
        "vp9" => matches!(profile, Some("profile 0" | "profile 2")),
        _ => false,
    };
    four_two_zero.then_some(true)
}

/// The planner's spelling of a codec name: trimmed, lower-cased, with common aliases folded
/// (silo-server capabilities_v3.go normalizeCodecV3 / protocol_v3.go spellings). Every `pcm_*`
/// becomes "pcm", the route's name for LPCM. `None` for a missing or blank name.
pub fn normalize_codec(codec: Option<&str>) -> Option<String> {
    let value = dotnet::lower(codec?.trim());
    let normalized = match value.as_str() {
        "" => return None,
        "h265" | "h.265" | "x265" => "hevc",
        "h.264" | "avc" | "x264" => "h264",
        "vp09" => "vp9",
        "e-ac-3" | "ec-3" => "eac3",
        "mlp fba" => "truehd",
        value if value.starts_with("pcm_") => "pcm",
        _ => return Some(value),
    };
    Some(normalized.to_owned())
}

/// Software video (dav1d AV1 and FFmpeg MPEG-4 Part 2) is measured only on Xbox Series (Zen 2 with
/// AVX2). Xbox One, One S and One X have 1.75–2.3 GHz Jaguar cores without AVX2, where dav1d drops
/// to its SSSE3 paths, so they claim no software video and the server transcodes it. A missing AVX2
/// alone also excludes it; an unknown device form keeps the claim.
pub fn software_video_supported(avx2: bool, device_form: Option<&str>) -> bool {
    avx2 && !device_form
        .is_some_and(|form| dotnet::starts_with_ignore_case(form.trim_start(), "Xbox One"))
}

/// The capability lists a device filter rewrites.
#[derive(Deserialize)]
pub struct DeviceLists {
    video_codecs: Vec<String>,
    video_decode: Vec<crate::models::VideoDecode>,
    audio_decode_codecs: Vec<String>,
    containers: Vec<String>,
}

/// What survives a filter. `video_decode` holds the indices of the kept entries, in order.
/// `drop_ffmpeg_executors` clears the executor flags that need FFmpeg (base layers, dynamic range
/// mapping, progressive).
#[derive(Serialize, Debug, PartialEq)]
pub struct FilteredDevice {
    video_codecs: Vec<String>,
    video_decode: Vec<usize>,
    audio_decode_codecs: Vec<String>,
    containers: Vec<String>,
    drop_ffmpeg_executors: bool,
}

#[derive(Deserialize)]
#[serde(tag = "filter", rename_all = "snake_case")]
pub enum DeviceFilter {
    /// Drops only the bundled software video claims; audio fallbacks stay.
    WithoutSoftwareVideo,
    /// dav1d uses D3D11 CPU buffers and grain; no D3D12 or zero-copy gate.
    ForSoftwareVideo { supported: bool },
    /// Drops every claim that needs FFmpeg: software video, demuxer-only video and fallback audio,
    /// plus the base-layer, dynamic-range and progressive executors (FFmpeg's demuxer, tone map and
    /// non-seekable input; Windows' sources need a length).
    WithoutFallbacks,
}

#[derive(Deserialize)]
pub struct FilterDeviceArgs {
    device: DeviceLists,
    #[serde(flatten)]
    filter: DeviceFilter,
}

pub fn filter_device(args: FilterDeviceArgs) -> FilteredDevice {
    let device = args.device;
    let keep_all = |device: DeviceLists| FilteredDevice {
        video_decode: (0..device.video_decode.len()).collect(),
        video_codecs: device.video_codecs,
        audio_decode_codecs: device.audio_decode_codecs,
        containers: device.containers,
        drop_ffmpeg_executors: false,
    };
    let filter =
        |device: DeviceLists,
         codec: &dyn Fn(&str) -> bool,
         decode: &dyn Fn(&crate::models::VideoDecode) -> bool| FilteredDevice {
            video_codecs: device
                .video_codecs
                .into_iter()
                .filter(|c| codec(c))
                .collect(),
            video_decode: device
                .video_decode
                .iter()
                .enumerate()
                .filter(|(_, d)| decode(d))
                .map(|(i, _)| i)
                .collect(),
            audio_decode_codecs: device.audio_decode_codecs,
            containers: device.containers,
            drop_ffmpeg_executors: false,
        };
    // Without software video a flat codec stays only while a hardware entry still names it: the
    // software fallbacks (VP9/VC-1/MPEG-2 on a failed probe, every FFmpeg-only codec) go with it.
    let without_software = |device: DeviceLists| {
        let hardware: Vec<String> = device
            .video_decode
            .iter()
            .filter(|d| d.hardware)
            .map(|d| d.codec.clone())
            .collect();
        filter(
            device,
            &|c| !VIDEO_CODECS.contains(&c) && hardware.iter().any(|h| h == c),
            &|d: &crate::models::VideoDecode| d.hardware,
        )
    };
    match args.filter {
        DeviceFilter::WithoutSoftwareVideo => without_software(device),
        DeviceFilter::ForSoftwareVideo { supported: false } => without_software(device),
        DeviceFilter::ForSoftwareVideo { supported: true } => keep_all(device),
        DeviceFilter::WithoutFallbacks => FilteredDevice {
            audio_decode_codecs: device
                .audio_decode_codecs
                .iter()
                .filter(|c| !AUDIO_CODECS.contains(&c.as_str()))
                .cloned()
                .collect(),
            containers: device
                .containers
                .iter()
                .filter(|c| !FFMPEG_ONLY_CONTAINERS.contains(&c.as_str()))
                .cloned()
                .collect(),
            drop_ffmpeg_executors: true,
            ..filter(
                device,
                &|c| !VIDEO_CODECS.contains(&c) && !DEMUXED_VIDEO_CODECS.contains(&c),
                &|d| d.hardware && !DEMUXED_VIDEO_CODECS.contains(&d.codec.as_str()),
            )
        },
    }
}

#[derive(Deserialize)]
pub struct OpenedVideoStream {
    codec: Option<String>,
    software_decoder: bool,
    system_decoder: bool,
}

#[derive(Deserialize)]
pub struct VideoDecodersArgs {
    software_video: bool,
    streams: Vec<OpenedVideoStream>,
    software_codec: Option<String>,
}

/// Checks the opened source's video streams against the route. Every stream must use the route's
/// decoder (system decoder, or FFmpeg software for an allowlisted codec), and a software-video route
/// must have at least one video stream: the server planned a software decode, so a source without
/// video is a failed open that must replan to a server transcode, never silent audio-only playback.
/// When supplied, `software_codec` also keeps AV1 and ordinary MPEG-4 output separate.
pub fn video_decoders_allowed(args: VideoDecodersArgs) -> bool {
    if args.software_video && args.streams.is_empty() {
        return false;
    }
    args.streams.iter().all(|stream| {
        if !args.software_video {
            return stream.system_decoder;
        }
        normalize_codec(stream.codec.as_deref()).is_some_and(|codec| {
            FFMPEG_VIDEO_CODECS.contains(&codec.as_str())
                && stream.software_decoder
                && args
                    .software_codec
                    .as_ref()
                    .is_none_or(|software| *software == codec)
        })
    })
}

/// The route facts a failed-fallback replan needs.
#[derive(Deserialize)]
pub struct FailedRoute {
    software_video: bool,
    software_audio: bool,
    video_codec: Option<String>,
    audio_codec: Option<String>,
    dolby_vision_base_layer: bool,
}

#[derive(Deserialize, Serialize, Debug, PartialEq)]
pub struct DeliveryLists {
    key: String,
    video_codecs: Vec<String>,
    audio_decode_codecs: Vec<String>,
    containers: Vec<String>,
    validated_claims: Vec<String>,
}

#[derive(Deserialize)]
pub struct StartLists {
    codecs_video: Vec<String>,
    codecs_video_hardware: Vec<String>,
    video_decode: Vec<crate::models::VideoDecode>,
    codecs_audio: Vec<String>,
    containers: Vec<String>,
    deliveries: Vec<DeliveryLists>,
    client_features: Vec<String>,
}

#[derive(Deserialize)]
pub struct FailedFallbacksArgs {
    start: StartLists,
    route: FailedRoute,
}

/// What a replan keeps after a failed FFmpeg route. `video_decode` holds kept indices.
#[derive(Serialize, Debug, PartialEq)]
pub struct FilteredStart {
    codecs_video: Vec<String>,
    codecs_video_hardware: Vec<String>,
    video_decode: Vec<usize>,
    codecs_audio: Vec<String>,
    containers: Vec<String>,
    deliveries: Vec<DeliveryLists>,
    client_features: Vec<String>,
}

pub fn without_failed_fallbacks(args: FailedFallbacksArgs) -> FilteredStart {
    let route = args.route;
    let start = args.start;
    // A software route withdraws the software video codecs, and its own codec when that is a hardware
    // codec (H.264, HEVC, VP9 ...), whose software route the player refuses: the hardware entries can't
    // exclude HEVC RExt, VP9 profile 1/3 or H.264 4:2:2 by profile, so a remux of the same stream would
    // fail the same way; without the codec the server transcodes (docs/software-decode.md, fix 2).
    // A system-decoder route withdraws its own codec only when that codec exists here solely through
    // this FFmpeg route (VP9).
    let keep_video = |codec: &str| {
        if route.software_video {
            !VIDEO_CODECS.contains(&codec) && route.video_codec.as_deref() != Some(codec)
        } else {
            route.video_codec.as_deref() != Some(codec) || !DEMUXED_VIDEO_CODECS.contains(&codec)
        }
    };
    // Claims carry exact spellings (pcm_s24le); the route carries the normalized one (pcm).
    let keep_audio =
        |codec: &str| !route.software_audio || normalize_codec(Some(codec)) != route.audio_codec;
    let keep_container = |container: &str| !FFMPEG_ONLY_CONTAINERS.contains(&container);
    let strings = |list: Vec<String>, keep: &dyn Fn(&str) -> bool| {
        list.into_iter().filter(|v| keep(v)).collect::<Vec<_>>()
    };
    let video_decode: Vec<usize> = start
        .video_decode
        .iter()
        .enumerate()
        // A failed software route withdraws every software entry, also a hardware codec's software
        // fallback (10-bit H.264, HEVC 4:2:2): its hardware entry alone cannot admit the file again.
        .filter(|(_, d)| keep_video(&d.codec) && !(route.software_video && !d.hardware))
        .map(|(i, _)| i)
        .collect();
    let software_decode_left = video_decode
        .iter()
        .any(|&i| !start.video_decode[i].hardware);
    FilteredStart {
        codecs_video: strings(start.codecs_video, &keep_video),
        codecs_video_hardware: strings(start.codecs_video_hardware, &keep_video),
        codecs_audio: strings(start.codecs_audio, &keep_audio),
        containers: strings(start.containers, &keep_container),
        deliveries: start
            .deliveries
            .into_iter()
            .map(|delivery| DeliveryLists {
                key: delivery.key,
                video_codecs: strings(delivery.video_codecs, &keep_video),
                audio_decode_codecs: strings(delivery.audio_decode_codecs, &keep_audio),
                containers: strings(delivery.containers, &keep_container),
                // A failed base-layer mount must not be offered again: the server then remuxes or transcodes.
                validated_claims: if route.dolby_vision_base_layer {
                    strings(delivery.validated_claims, &|claim| {
                        claim != DOLBY_VISION8_BASE_LAYER_CLAIM
                    })
                } else {
                    delivery.validated_claims
                },
            })
            .collect(),
        client_features: strings(start.client_features, &|feature| {
            feature != SOFTWARE_VIDEO_DECODE_FEATURE || software_decode_left
        }),
        video_decode,
    }
}

#[cfg(test)]
mod tests {
    use super::normalize_codec;

    #[test]
    fn software_av1_claim_needs_no_zero_copy_capability() {
        let args = serde_json::from_value(serde_json::json!({
            "device": {
                "video_codecs": ["av1", "h264"],
                "video_decode": [{ "codec": "av1", "hardware": false, "max_height": 1080 }],
                "audio_decode_codecs": [],
                "containers": []
            },
            "filter": "for_software_video",
            "supported": true
        }))
        .unwrap();
        let result = super::filter_device(args);
        assert!(result.video_codecs.iter().any(|codec| codec == "av1"));
        assert_eq!(result.video_decode, vec![0]);
    }

    fn n(codec: &str) -> Option<String> {
        normalize_codec(Some(codec))
    }

    #[test]
    fn folds_aliases() {
        for (from, to) in [
            ("h265", "hevc"),
            ("H.265", "hevc"),
            ("x265", "hevc"),
            ("HEVC", "hevc"),
            ("h.264", "h264"),
            ("AVC", "h264"),
            ("x264", "h264"),
            ("VP09", "vp9"),
            (" vp9 ", "vp9"),
            ("E-AC-3", "eac3"),
            ("ec-3", "eac3"),
            ("MLP FBA", "truehd"),
            ("pcm_s24le", "pcm"),
            ("PCM_BLURAY", "pcm"),
            ("pcm", "pcm"),
            ("mpeg4", "mpeg4"),
            ("dts_hd", "dts_hd"),
        ] {
            assert_eq!(n(from).as_deref(), Some(to), "{from}");
        }
    }

    #[test]
    fn blank_is_none() {
        assert_eq!(normalize_codec(None), None);
        assert_eq!(n(""), None);
        assert_eq!(n(" \t\u{a0}"), None);
    }

    #[test]
    fn lower_cases_like_dotnet_invariant() {
        assert_eq!(n("ΣΑΣ").as_deref(), Some("σασ"));
        assert_eq!(n("\u{130}X").as_deref(), Some("\u{130}x"));
        assert_eq!(n("Ǆẞ").as_deref(), Some("ǆß"));
    }
}

#[cfg(test)]
mod fallback_tests {
    use serde_json::json;

    use crate::models::{DecodeClaim, FileVersion};

    /// The production claims of a Series console (hardware entries from its probe report, AV1, the
    /// software-only codecs and the phase A fallback), as the C# side would send them back.
    fn series_claims() -> Vec<DecodeClaim> {
        let limits: crate::hardware::Limits = serde_json::from_value(json!({
            "h264": { "width": 1920, "height": 1080 },
            "hevc_main": { "width": 7680, "height": 4320 }, "hevc_main10": { "width": 7680, "height": 4320 },
            "vp9_profile0": { "width": 3840, "height": 2160 }, "vp9_profile2": { "width": 3840, "height": 2160 },
            "vc1": null, "mpeg2": null, "partial": false
        }))
        .unwrap();
        let claims = crate::evidence::video_decode(Some(&limits), 1920, 1080);
        serde_json::from_value(serde_json::to_value(claims).unwrap()).unwrap()
    }

    fn file(
        codec: &str,
        profile: Option<&str>,
        depth: i64,
        size: (i64, i64),
        fps: Option<&str>,
        pixel_format: Option<&str>,
    ) -> FileVersion {
        serde_json::from_value(json!({
            "file_id": "file", "codec_video": codec,
            "video_tracks": [{ "codec": codec, "profile": profile, "bit_depth": depth, "width": size.0,
                               "height": size.1, "frame_rate": fps, "pixel_format": pixel_format }]
        }))
        .unwrap()
    }

    const HD: (i64, i64) = (1920, 1080);
    const UHD: (i64, i64) = (3840, 2160);

    #[test]
    fn the_claims_carry_each_envelope_with_ffprobe_profiles() {
        let claims = series_claims();
        let fallback: Vec<&DecodeClaim> = claims
            .iter()
            .filter(|c| {
                !c.hardware && super::SOFTWARE_FALLBACK_VIDEO_CODECS.contains(&c.codec.as_str())
            })
            .collect();
        assert_eq!(fallback.len(), super::FALLBACK_ENVELOPES.len());
        for (claim, envelope) in fallback.iter().zip(super::FALLBACK_ENVELOPES) {
            assert_eq!(claim.codec, envelope.codec);
            assert_eq!(claim.profiles, envelope.profiles);
            assert_eq!(claim.bit_depths, envelope.bit_depths);
            assert_eq!(
                (claim.max_width, claim.max_height),
                (Some(1920), Some(1080))
            );
            assert_eq!(claim.max_frame_rate, Some(envelope.max_frame_rate));
            assert_eq!(claim.max_bitrate_kbps, None);
            // ffprobe's names, lower-cased, as silo-server compares them.
            assert!(claim.profiles.iter().all(|p| *p == p.to_lowercase()));
        }
    }

    #[test]
    fn inside_the_envelopes() {
        let claims = series_claims();
        for version in [
            file(
                "h264",
                Some("High 10"),
                10,
                HD,
                Some("24000/1001"),
                Some("yuv420p10le"),
            ),
            file(
                "h264",
                Some("high 10"),
                10,
                HD,
                Some("60"),
                Some("yuv420p10le"),
            ),
            file("h264", Some("high 10"), 8, (1280, 720), Some("50"), None),
            file(
                "h264",
                Some("high 4:2:2"),
                10,
                HD,
                Some("30000/1001"),
                Some("yuv422p10le"),
            ),
            file(
                "h264",
                Some("high 4:4:4 predictive"),
                8,
                HD,
                Some("30"),
                Some("yuv444p"),
            ),
            file(
                "hevc",
                Some("Rext"),
                10,
                HD,
                Some("30000/1001"),
                Some("yuv422p10le"),
            ),
            file(
                "hevc",
                Some("rext"),
                12,
                HD,
                Some("60"),
                Some("yuv420p12le"),
            ),
            file("hevc", Some("rext"), 12, HD, Some("24"), None),
            file(
                "vp9",
                Some("Profile 1"),
                8,
                HD,
                Some("30000/1001"),
                Some("yuv444p"),
            ),
            file(
                "vp9",
                Some("profile 3"),
                10,
                HD,
                Some("25"),
                Some("yuv422p10le"),
            ),
        ] {
            assert!(
                super::fallback_admits(&version, &claims),
                "{:?}",
                version.video_tracks
            );
        }
    }

    #[test]
    fn outside_the_envelopes() {
        let claims = series_claims();
        for version in [
            // 4K waits for phase B (software frames through Bale's stage).
            file("h264", Some("high"), 8, UHD, Some("30"), Some("yuv420p")),
            file(
                "h264",
                Some("high 10"),
                10,
                UHD,
                Some("24"),
                Some("yuv420p10le"),
            ),
            file(
                "hevc",
                Some("rext"),
                10,
                UHD,
                Some("30"),
                Some("yuv422p10le"),
            ),
            file(
                "vp9",
                Some("profile 1"),
                8,
                UHD,
                Some("30"),
                Some("yuv444p"),
            ),
            // 4:2:2 / 4:4:4 above 30 fps, also where only the chroma rule refuses (12-bit RExt's 60 fps entry).
            file(
                "h264",
                Some("high 4:2:2"),
                10,
                HD,
                Some("60"),
                Some("yuv422p10le"),
            ),
            file(
                "hevc",
                Some("rext"),
                10,
                HD,
                Some("60"),
                Some("yuv422p10le"),
            ),
            file(
                "hevc",
                Some("rext"),
                12,
                HD,
                Some("60"),
                Some("yuv422p12le"),
            ),
            file("hevc", Some("rext"), 12, HD, Some("60"), None),
            file("vp9", Some("profile 1"), 8, HD, Some("60"), Some("yuv444p")),
            // Not 4:2:0 and no known rate.
            file("hevc", Some("rext"), 10, HD, None, Some("yuv422p10le")),
            // Above 60 fps, and profiles the envelopes don't list (the hardware's, intra, none).
            file(
                "h264",
                Some("high 10"),
                10,
                HD,
                Some("120"),
                Some("yuv420p10le"),
            ),
            file("h264", Some("high"), 8, HD, Some("30"), Some("yuv420p")),
            file(
                "h264",
                Some("high 4:2:2 intra"),
                10,
                HD,
                Some("30"),
                Some("yuv422p10le"),
            ),
            file("vp9", Some("profile 0"), 8, HD, Some("30"), Some("yuv420p")),
            file("h264", None, 10, HD, Some("24"), Some("yuv420p10le")),
            // A software-only codec is not a hardware codec's fallback.
            file("av1", Some("main"), 10, HD, Some("24"), Some("yuv420p10le")),
            file("mpeg4", None, 8, (720, 480), Some("25"), Some("yuv420p")),
        ] {
            assert!(
                !super::fallback_admits(&version, &claims),
                "{:?}",
                version.video_tracks
            );
        }
    }

    #[test]
    fn only_claimed_software_entries_admit() {
        let version = file(
            "h264",
            Some("high 10"),
            10,
            HD,
            Some("24"),
            Some("yuv420p10le"),
        );
        let claims = series_claims();
        // Xbox One / no AVX2, or a replan that withdrew software video: hardware entries alone never admit.
        let hardware: Vec<DecodeClaim> = claims.iter().filter(|c| c.hardware).cloned().collect();
        assert!(!super::fallback_admits(&version, &hardware));
        assert!(!super::fallback_admits(&version, &[]));
        assert!(super::fallback_admits(&version, &claims));
    }

    #[test]
    fn chroma_comes_from_the_pixel_format_else_a_four_two_zero_profile() {
        use super::chroma_420;
        for format in [
            "yuv420p",
            "yuvj420p",
            "yuv420p10le",
            "yuv420p12le",
            "nv12",
            "p010le",
            "gray",
            "gray10le",
            "yuva420p",
        ] {
            assert_eq!(
                chroma_420("hevc", Some("rext"), Some(format)),
                Some(true),
                "{format}"
            );
        }
        for format in [
            "yuv422p",
            "yuv422p10le",
            "yuvj422p",
            "yuv444p",
            "yuv444p12le",
            "yuv440p",
            "gbrp",
            "nv16",
            "yuv411p",
        ] {
            assert_eq!(
                chroma_420("hevc", Some("rext"), Some(format)),
                Some(false),
                "{format}"
            );
        }
        assert_eq!(chroma_420("h264", Some("high 10"), None), Some(true));
        assert_eq!(chroma_420("h264", Some("high 10"), Some(" ")), Some(true));
        assert_eq!(chroma_420("hevc", Some("rext"), None), None);
        assert_eq!(chroma_420("h264", Some("high 4:2:2"), None), None);
        assert_eq!(chroma_420("vp9", Some("profile 1"), None), None);
    }
}
