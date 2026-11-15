//! Ported from `PlaybackOriginalAudioSelection.ResolveIndex`: AudioReconcile.kt's catalog-to-mounted
//! audio reconciliation for untouched originals. The selection and its confirmation stay in C#
//! (they drive the player).

use serde::{Deserialize, Serialize};

use crate::dotnet::{is_blank, lower};
use crate::language::subtitle_language_key;
use crate::route::audio_track_index;
use crate::software_decode::normalize_codec;

/// A catalog `AudioTrack`, in the server's wire shape.
#[derive(Deserialize, Default, Clone)]
#[serde(default)]
pub struct CatalogTrack {
    language: Option<String>,
    codec: Option<String>,
    channels: Option<i64>,
    embedded_title: Option<String>,
}

/// `MountedPlaybackAudioTrack`, in the player's AudioTracks order.
#[derive(Deserialize, Clone)]
pub struct MountedTrack {
    stream_index: Option<i32>,
    language: Option<String>,
    codec: Option<String>,
    channels: Option<i64>,
    embedded_title: Option<String>,
    decodable: bool,
}

/// `{"index": n}` or `{"failed": {...}}` for the C# wrapper.
#[derive(Serialize, Debug, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum Outcome {
    Index(i32),
    Failed(SelectionFailed),
}

impl From<Result<i32, SelectionFailed>> for Outcome {
    fn from(result: Result<i32, SelectionFailed>) -> Self {
        result.map_or_else(Outcome::Failed, Outcome::Index)
    }
}

/// Why the selection failed; C# throws `PlaybackAudioSelectionException(decoder_unsupported)`.
#[derive(Serialize, Debug, PartialEq)]
pub struct SelectionFailed {
    decoder_unsupported: bool,
}

const FAILED: SelectionFailed = SelectionFailed {
    decoder_unsupported: false,
};

/// The mounted index to select, or -1 for a proven video-only file.
pub fn resolve_index(
    plan_index: Option<i64>,
    catalog: Option<&[CatalogTrack]>,
    mounted: &[MountedTrack],
    ffmpeg: bool,
) -> Result<i32, SelectionFailed> {
    if plan_index.is_none() && mounted.is_empty() && catalog.is_none_or(<[_]>::is_empty) {
        return Ok(-1);
    }
    let catalog = catalog.ok_or(FAILED)?;
    let ordinal = plan_index
        .filter(|&i| i >= 0 && (i as u64) < catalog.len() as u64)
        .ok_or(FAILED)? as usize;
    let index = if ffmpeg {
        // FFmpeg may drop streams with no decoder. Never sort the surviving streams into shifted
        // catalog ordinals: this positional proof requires the complete inventory.
        if catalog.len() != mounted.len() {
            return Err(FAILED);
        }
        // FFmpegInteropX inserts av_find_best_stream's audio at position zero. Silo's scanner appends
        // only audio streams in demux order; its audio rows have no global stream id. Reconstruct
        // that order from AudioStreams[].StreamIndex, including non-audio gaps.
        let streams: Vec<i32> = mounted
            .iter()
            .map(|t| t.stream_index.unwrap_or(-1))
            .collect();
        let index = audio_track_index(plan_index, &streams, i32::try_from(catalog.len()).ok())
            .map_err(|_| FAILED)? as usize;
        let mut order: Vec<(i32, usize)> = streams.iter().copied().zip(0..).collect();
        order.sort_by_key(|(stream, _)| *stream);
        if !catalog
            .iter()
            .zip(&order)
            .all(|(track, (_, i))| compatible(track, &mounted[*i]))
        {
            return Err(FAILED);
        }
        index
    } else {
        // Windows AudioTrack.Id is opaque; neither it nor list position is a source stream index
        // contract. Require a unique identity in BOTH inventories, rather than guessing the language
        // of indistinguishable main/commentary tracks from their positions.
        let candidates: Vec<usize> = (0..mounted.len())
            .filter(|&i| compatible(&catalog[ordinal], &mounted[i]))
            .collect();
        let [index] = candidates[..] else {
            return Err(FAILED);
        };
        if catalog
            .iter()
            .filter(|track| compatible(track, &mounted[index]))
            .count()
            != 1
        {
            return Err(FAILED);
        }
        index
    };
    // Unsupported tracks still occupy their inventory positions; don't filter before mapping.
    if !mounted[index].decodable {
        return Err(SelectionFailed {
            decoder_unsupported: true,
        });
    }
    Ok(index as i32)
}

fn compatible(catalog: &CatalogTrack, mounted: &MountedTrack) -> bool {
    agrees(
        language(catalog.language.as_deref()),
        language(mounted.language.as_deref()),
    ) && agrees(
        codec(catalog.codec.as_deref()),
        codec(mounted.codec.as_deref()),
    ) && (!catalog.channels.is_some_and(|c| c > 0)
        || !mounted.channels.is_some_and(|c| c > 0)
        || catalog.channels == mounted.channels)
        && agrees(
            clean(catalog.embedded_title.as_deref()),
            clean(mounted.embedded_title.as_deref()),
        )
}

fn agrees(a: Option<String>, b: Option<String>) -> bool {
    a.is_none() || b.is_none() || a == b
}

fn clean(value: Option<&str>) -> Option<String> {
    if is_blank(value) {
        None
    } else {
        value.map(|v| lower(v.trim()))
    }
}

fn language(value: Option<&str>) -> Option<String> {
    let tag = clean(value)?.replace('_', "-");
    let (first, rest) = match tag.split_once('-') {
        Some((first, rest)) => (first, Some(rest)),
        None => (tag.as_str(), None),
    };
    // FFmpegInteropX's fallback may be a localized display name rather than a language tag.
    if !(2..=3).contains(&first.chars().count()) || !first.chars().all(|c| c.is_ascii_alphabetic())
    {
        return None;
    }
    let primary = subtitle_language_key(Some(first))?;
    Some(match rest {
        Some(rest) => format!("{primary}-{rest}"),
        None => primary,
    })
}

fn codec(value: Option<&str>) -> Option<String> {
    let codec = clean(value)?;
    let folded = match codec.as_str() {
        "unknown" => return None,
        "mpeg4audio" | "mp4a" | "he-aac" | "aac_adts" => "aac",
        "ac-3" | "dolby_ac3" => "ac3",
        "dolby_ddplus" | "dd+" => "eac3",
        // FFmpeg's codec name is dts even when the catalog names the HD profile separately.
        "dts_hd" | "dts-hd" | "dts_hd_ma" | "dts_hd_hra" => "dts",
        "float" => "pcm",
        _ => return normalize_codec(Some(&codec)),
    };
    Some(folded.to_owned())
}
