//! Ported from `PlaybackRoutePolicy`: pure per-mount policy. HLS never leaves AdaptiveMediaSource.

use serde::{Deserialize, Serialize};

use crate::dolby_vision7;
use crate::dotnet::{eq_ignore_case, lower, trim_lower};
use crate::models::{FileVersion, PlaybackPlan};
use crate::software_decode::{
    AUDIO_CODECS, DEMUXED_VIDEO_CODECS, FFMPEG_ONLY_CONTAINERS, FFMPEG_VIDEO_CODECS,
    SOFTWARE_FALLBACK_VIDEO_CODECS, SYSTEM_AUDIO_CODECS, SYSTEM_VIDEO_CODECS, contains,
    fallback_admits, normalize_codec,
};
use crate::tone_map::{self, DOLBY_VISION8_BASE_LAYER_REASON};

/// `PlaybackProgressive.Delivery`.
const PROGRESSIVE_DELIVERY: &str = "server_remux_progressive";

#[derive(Deserialize)]
pub struct DecideArgs {
    plan: PlaybackPlan,
    is_hls: bool,
    ffmpeg_enabled: bool,
    /// Debug A/B only: demux every direct mount with FFmpeg, keeping system video decode.
    force_ffmpeg: bool,
    sdr_output: bool,
    versions: Option<Vec<FileVersion>>,
    /// HLS plays through the Rust reader and FFmpeg's forward-only input instead of Windows' source.
    #[serde(default)]
    hls_through_ffmpeg: bool,
    /// The console's hardware decode entries (the decoder probe). Empty: every hardware codec is
    /// taken as decodable in hardware, as before the software fallback existed.
    #[serde(default)]
    hardware_decode: Vec<crate::models::DecodeClaim>,
    /// The current display mode's raw size (0 = unknown), for the SDR downscale route.
    #[serde(default)]
    display_width: i64,
    #[serde(default)]
    display_height: i64,
    /// The wrapper and bale_gpu take the SDR scale-only stage, and this attempt has not declined it.
    #[serde(default)]
    sdr_downscale: bool,
    /// The software entries the attempt claims now (its start request, as any replan left them): a
    /// hardware codec's software route plays only when one admits the file (`fallback_admits`).
    #[serde(default)]
    software_decode: Vec<crate::models::DecodeClaim>,
    /// The wrapper (bale.80+) and bale_gpu run the stage on FFmpeg software frames too: a software
    /// route may take the tone map, and with `sdr_downscale` the downscale (phase B,
    /// docs/software-decode.md).
    #[serde(default)]
    software_stage: bool,
}

/// `PlaybackRouteDecision`.
#[derive(Serialize, Debug, PartialEq)]
pub struct RouteDecision {
    pub use_ffmpeg: bool,
    pub software_video: bool,
    pub video_codec: Option<String>,
    pub audio_codec: Option<String>,
    pub audio_ordinal: Option<i64>,
    pub video_width: Option<i64>,
    pub video_height: Option<i64>,
    pub dolby_vision_base_layer: bool,
    pub console_tone_map: bool,
    pub dolby_vision7_base_layer: bool,
    /// SDR HEVC/VP9/H.264 larger than the display: bale_gpu's scale-only stage to display size, on
    /// d3d11va frames or (phase B, wrapper bale.80+) FFmpeg software frames.
    pub console_downscale: bool,
    /// A hardware codec (H.264/HEVC/VP9) the console's decoder can't take, inside a claimed software
    /// envelope: FFmpeg decodes it in software, through the console stage where one applies
    /// (docs/software-decode.md phases A and B). A software route of a hardware codec without it is
    /// refused (FfmpegSource).
    pub software_fallback: bool,
}

pub fn decide(args: DecideArgs) -> RouteDecision {
    let plan = &args.plan;
    let source = plan.source.as_ref();
    let recipe = plan.effective_recipe.as_ref();
    let delivery = plan.delivery.as_deref().map(lower);
    let delivery = delivery.as_deref();
    let original = delivery == Some("original_http");
    // SourceDescriptorFromFileV3 describes the server-selected audio track, not the file's default.
    // A remux's recipe describes its actual output, which has only the selected audio stream.
    let pick = |from_source: Option<&str>, from_recipe: Option<&str>| {
        normalize_codec(if original {
            from_source
        } else {
            from_recipe.or(from_source)
        })
    };
    let video = pick(
        source.and_then(|s| s.video_codec.as_deref()),
        recipe.and_then(|r| r.video_codec.as_deref()),
    );
    let audio = pick(
        source.and_then(|s| s.audio_codec.as_deref()),
        recipe.and_then(|r| r.audio_codec.as_deref()),
    );
    let size = |from_source: Option<i64>, from_recipe: Option<i64>| {
        if original {
            from_source
        } else {
            from_recipe.or(from_source)
        }
    };
    let width = size(source.and_then(|s| s.width), recipe.and_then(|r| r.width));
    let height = size(source.and_then(|s| s.height), recipe.and_then(|r| r.height));
    let hls = args.is_hls
        || eq_ignore_case(
            plan.stream.as_ref().and_then(|s| s.protocol.as_deref()),
            "hls",
        )
        || delivery == Some("hls")
        || delivery.is_some_and(|d| d.ends_with("_hls"));
    // HLS through FFmpeg: Silo's one rendition, as a forward-only stream like the progressive remux.
    let hls_ffmpeg = hls && args.hls_through_ffmpeg;
    // Both forward-only outputs carry only the selected audio stream.
    let ordinal = if delivery == Some(PROGRESSIVE_DELIVERY) || hls_ffmpeg {
        Some(0)
    } else {
        plan.selected_tracks
            .as_ref()
            .and_then(|t| t.audio.as_ref())
            .and_then(|a| a.index)
    };
    let direct = matches!(
        delivery,
        Some("original_http" | "progressive" | PROGRESSIVE_DELIVERY)
    ) || hls_ffmpeg;
    let audio_ref = audio.as_deref();
    // Packages FFmpeg reads take the same software decoders as originals (owner, 2026-11-16). Since
    // 2026-11-17 (owner: declare everything FFmpeg decodes) a hardware codec the console's decoder
    // cannot take (bit depth, size, a 4:2:2/4:4:4 or other non-hardware profile) decodes in FFmpeg too.
    // The source's attributes describe the video only when the delivery keeps the source codec.
    let source_video = normalize_codec(source.and_then(|s| s.video_codec.as_deref()));
    let attributes = source.filter(|_| source_video.as_deref() == video.as_deref());
    let video = video.as_deref();
    let software_video = contains(FFMPEG_VIDEO_CODECS, video)
        && !hardware_admits(
            video,
            attributes.and_then(|s| s.bit_depth),
            attributes.and_then(|s| s.video_profile.as_deref()),
            width,
            height,
            &args.hardware_decode,
        );
    let software_audio =
        !contains(SYSTEM_AUDIO_CODECS, audio_ref) && contains(AUDIO_CODECS, audio_ref);
    let container = plan
        .stream
        .as_ref()
        .and_then(|s| s.container.as_deref())
        .or(source.and_then(|s| s.container.as_deref()));
    // Windows' MKV source over-reads (~2.2x the bitrate) and blocks on the network; FFmpeg's demuxer does not.
    let matroska = is_matroska(container);
    let ffmpeg_container = contains(FFMPEG_ONLY_CONTAINERS, trim_lower(container).as_deref());
    // VP9/VC-1/MPEG-2 use verified compressed providers feeding the system decoder, regardless of
    // the container. Their claims do not imply Windows' own container source works.
    let demuxed_video = contains(DEMUXED_VIDEO_CODECS, video);
    // A Dolby Vision profile 8 base-layer plan keeps the file's DV sample entries (dvh1/dvhe in MP4),
    // which Windows' own MP4 source is not trusted with; the FFmpeg demuxer hands plain HEVC on.
    let dv7 = dolby_vision7::requires_base_layer(plan);
    let dolby_vision_base_layer = dv7
        || original
            && eq_ignore_case(
                plan.decision_reason.as_deref(),
                DOLBY_VISION8_BASE_LAYER_REASON,
            );
    let tone_map =
        tone_map::requires_tone_map(plan, args.sdr_output, args.versions.as_deref(), hls_ffmpeg);
    // A forward-only progressive remux has no length or ranges for Windows' own sources: it always
    // takes FFmpeg's non-seekable input (PlaybackProgressive), with the system decoder.
    let progressive = delivery == Some(PROGRESSIVE_DELIVERY);
    // Every original goes through FFmpeg (owner, 2026-11-16): MP4 too, so all direct play shares the
    // Rust transport, exact seeks and the console tone map. "ffmpeg off" keeps Windows' sources. A video
    // codec FFmpeg cannot hand to a decoder Bale allows (FfmpegSource) stays with Windows' source.
    let original_ffmpeg =
        original && (video.is_none() || contains(SYSTEM_VIDEO_CODECS, video) || software_video);
    let ffmpeg = args.ffmpeg_enabled
        && direct
        && (!hls || hls_ffmpeg)
        && (original_ffmpeg
            || progressive
            || hls_ffmpeg
            || software_video
            || software_audio
            || matroska
            || ffmpeg_container
            || demuxed_video
            || dolby_vision_base_layer
            || args.force_ffmpeg
            || tone_map);
    // On Xbox, MF's video processor leaks driver state per open when it scales 4K SDR down itself
    // (2026-11-17: Titanic 4K VP9 SDR died at open 24). Hand it display-sized SDR from bale_gpu's
    // scale-only stage instead: plain SDR (no HDR, DV or HDR10+) hardware HEVC/VP9/H.264 originals only
    // (H.264 joined 2026-11-24: d3d11va H.264 verified on the console, the stage reads its NV12).
    let sdr_source = matches!(
        trim_lower(source.and_then(|s| s.dynamic_range.as_deref())).as_deref(),
        None | Some("" | "sdr")
    ) && source
        .and_then(|s| s.dolby_vision_profile)
        .is_none_or(|p| p <= 0)
        && source.and_then(|s| s.hdr10_plus) != Some(true);
    // Phase B (owner, 2026-11-24): software frames take the same stage when the wrapper runs it on
    // them (bale.80+); AV1 and the other software codecs stay off it (the codec list below).
    let console_downscale = args.sdr_downscale
        && ffmpeg
        && original
        && !hls
        && (!software_video || args.software_stage)
        && !tone_map
        && !dolby_vision_base_layer
        && matches!(video, Some("hevc" | "vp9" | "h264"))
        && sdr_source
        && tone_map::downscale_to_display(
            width.unwrap_or(0),
            height.unwrap_or(0),
            args.display_width,
            args.display_height,
        );
    // Phase A (owner, 2026-11-24): a hardware codec's software route plays only inside a software
    // envelope the attempt still claims, judged on the delivered file's catalog facts (pixel format,
    // frame rate and bitrate are only there). Without the file's version the route stays refused.
    // Phase B: the stage takes software frames too (wrapper bale.80+), so a tone map (HDR on an SDR
    // display) needs that stage, and a frame above 1080p needs the stage's downscale unless the
    // display is known to fit it: Media Foundation must never scale it (its scaler leaks per open
    // with 4K SDR). At 1080p and below a declined downscale leaves the scaling to Media Foundation,
    // as on hardware.
    let above_1080p = width.is_some_and(|w| w > 1920) || height.is_some_and(|h| h > 1080);
    let display_fits = args.display_width > 0
        && args.display_height > 0
        && !tone_map::downscale_to_display(
            width.unwrap_or(0),
            height.unwrap_or(0),
            args.display_width,
            args.display_height,
        );
    let software_fallback = ffmpeg
        && software_video
        && contains(SOFTWARE_FALLBACK_VIDEO_CODECS, video)
        && (!tone_map || args.software_stage)
        && !dolby_vision_base_layer
        && (!above_1080p || display_fits || console_downscale || tone_map)
        && delivered_version(plan, args.versions.as_deref()).is_some_and(|version| {
            normalize_codec(version_codec(version)).as_deref() == video
                && fallback_admits(version, &args.software_decode)
        });
    RouteDecision {
        console_downscale,
        software_fallback,
        use_ffmpeg: ffmpeg,
        software_video: ffmpeg && software_video,
        video_codec: video.map(str::to_owned),
        audio_codec: audio,
        audio_ordinal: ordinal,
        video_width: width,
        video_height: height,
        dolby_vision_base_layer: ffmpeg && dolby_vision_base_layer,
        console_tone_map: tone_map,
        dolby_vision7_base_layer: dv7,
    }
}

/// The catalog version of the file the plan delivers (its effective file, else the source's).
fn delivered_version<'a>(
    plan: &PlaybackPlan,
    versions: Option<&'a [FileVersion]>,
) -> Option<&'a FileVersion> {
    let id = plan.effective_media_file_id.as_deref().or(plan
        .source
        .as_ref()
        .and_then(|source| source.media_file_id.as_deref()))?;
    versions?
        .iter()
        .find(|version| version.file_id.as_deref() == Some(id))
}

/// A version's video codec: its first track's, else the file's.
fn version_codec(version: &FileVersion) -> Option<&str> {
    crate::dotnet::non_empty(
        version
            .video_tracks
            .as_ref()
            .and_then(|tracks| tracks.first())
            .and_then(|track| track.codec.as_deref()),
    )
    .or(version.codec_video.as_deref())
}

/// Whether the console's hardware decoder takes this video: a hardware codec with a profile the
/// hardware decodes, and (when the probe's entries are known) an entry admitting its bit depth and
/// size. Unknown attributes are admitted, as silo-server does.
pub fn hardware_admits(
    codec: Option<&str>,
    bit_depth: Option<i64>,
    profile: Option<&str>,
    width: Option<i64>,
    height: Option<i64>,
    entries: &[crate::models::DecodeClaim],
) -> bool {
    let Some(codec) = codec else { return true };
    if !SYSTEM_VIDEO_CODECS.contains(&codec) || !hardware_profile(codec, profile) {
        return false;
    }
    if entries.is_empty() {
        return true;
    }
    entries.iter().any(|entry| {
        entry.hardware
            && eq_ignore_case(Some(entry.codec.as_str()), codec)
            && bit_depth.is_none_or(|depth| {
                entry.bit_depths.is_empty()
                    || entry.bit_depths.iter().any(|&d| i64::from(d) == depth)
            })
            && width.is_none_or(|w| entry.max_width.is_none_or(|max| w <= i64::from(max)))
            && height.is_none_or(|h| entry.max_height.is_none_or(|max| h <= i64::from(max)))
    })
}

/// Profiles the console's Media Foundation decoders take: 4:2:0 H.264 up to High, HEVC Main/Main 10,
/// VP9 profiles 0 and 2. Anything chroma 4:2:2/4:4:4, H.264 High 10 and HEVC range extensions decode
/// in FFmpeg. An unknown profile is admitted.
pub(crate) fn hardware_profile(codec: &str, profile: Option<&str>) -> bool {
    let Some(profile) = profile.map(|p| p.trim().to_ascii_lowercase()) else {
        return true;
    };
    if profile.is_empty() {
        return true;
    }
    if profile.contains("4:2:2") || profile.contains("4:4:4") || profile.contains("rext") {
        return false;
    }
    match codec {
        "h264" => !profile.contains("high 10") && !profile.contains("high10"),
        "hevc" => !profile.contains("12") && !profile.contains("range"),
        "vp9" => !(profile.contains("profile 1") || profile.contains("profile 3")),
        _ => true,
    }
}

/// MKV and WebM (Matroska-based).
pub fn is_matroska(container: Option<&str>) -> bool {
    trim_lower(container).is_some_and(|name| {
        matches!(name.as_str(), "mkv" | "mka" | "webm") || name.contains("matroska")
    })
}

/// FFmpegInteropX moves its best stream first; Silo indexes audio in demux order.
pub fn audio_track_index(
    selected_ordinal: Option<i64>,
    stream_indices: &[i32],
    expected_audio_count: Option<i32>,
) -> Result<i32, &'static str> {
    if expected_audio_count.is_some_and(|count| i64::from(count) != stream_indices.len() as i64) {
        return Err("The planned audio inventory is incomplete.");
    }
    if selected_ordinal.is_none() && stream_indices.is_empty() {
        return Ok(-1);
    }
    let Some(selected) = selected_ordinal
        .filter(|&s| s >= 0 && (s as u64) < stream_indices.len() as u64)
        .filter(|_| {
            stream_indices.iter().all(|&i| i >= 0) && !dolby_vision7::has_duplicates(stream_indices)
        })
    else {
        return Err("The planned audio track is unavailable.");
    };
    let mut order: Vec<(i32, usize)> = stream_indices.iter().copied().zip(0..).collect();
    order.sort_by_key(|(stream, _)| *stream);
    Ok(order[selected as usize].1 as i32)
}
