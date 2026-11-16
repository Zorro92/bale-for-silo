//! Ported from `PlaybackRoutePolicy`: pure per-mount policy. HLS never leaves AdaptiveMediaSource.

use serde::{Deserialize, Serialize};

use crate::dolby_vision7;
use crate::dotnet::{eq_ignore_case, lower, trim_lower};
use crate::models::{FileVersion, PlaybackPlan};
use crate::software_decode::{
    AUDIO_CODECS, DEMUXED_VIDEO_CODECS, FFMPEG_ONLY_CONTAINERS, ORIGINAL_ONLY_VIDEO_CODECS,
    SYSTEM_AUDIO_CODECS, VIDEO_CODECS, contains, normalize_codec,
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
    let video = video.as_deref();
    let audio_ref = audio.as_deref();
    let software_video =
        contains(VIDEO_CODECS, video) && (original || !contains(ORIGINAL_ONLY_VIDEO_CODECS, video));
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
    let ffmpeg = args.ffmpeg_enabled
        && direct
        && (!hls || hls_ffmpeg)
        && (progressive
            || hls_ffmpeg
            || software_video
            || software_audio
            || matroska
            || ffmpeg_container
            || demuxed_video
            || dolby_vision_base_layer
            || args.force_ffmpeg
            || tone_map);
    RouteDecision {
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

/// MKV and WebM (Matroska-based); MP4 stays on Windows' own source.
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
