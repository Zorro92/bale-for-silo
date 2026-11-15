//! Ported from `PlaybackFormatting` (src/Bale.Core/Detail): version ranking and Auto's choice,
//! the decode check Bale's version matching shares with the server, and Dolby Vision detection.
//! The Detail page's label text stays in C#.

use serde::Deserialize;

use crate::dotnet::{
    is_letter_or_digit, is_regex_boundary_word, is_regex_digit, lower, non_empty, parse_float,
    trim_lower,
};
use crate::models::{DecodeClaim, FileVersion};
use crate::software_decode::normalize_codec;

/// A version label's height: the first `\b(\d{3,4})(?:p|i)?\b` number, else named tiers
/// (8K, 4K/UHD, QHD, FHD, HD, SD), else 0.
pub fn resolution_rank(value: Option<&str>) -> i32 {
    let normalized = trim_lower(value).unwrap_or_default();
    let chars: Vec<char> = normalized.chars().collect();
    if let Some((start, len)) = resolution_height(&chars) {
        // int.TryParse with the invariant culture reads ASCII digits only; a match of other digits
        // falls through to the named tiers.
        let digits: String = chars[start..start + len].iter().collect();
        if digits.bytes().all(|b| b.is_ascii_digit()) {
            return digits.parse().unwrap_or(0);
        }
    }
    let has = |needle: &str| normalized.contains(needle);
    if has("4320") || has("8k") {
        4320
    } else if has("2160") || has("4k") || has("uhd") {
        2160
    } else if has("1440") || has("qhd") {
        1440
    } else if has("1080") || has("fhd") {
        1080
    } else if has("720") || has("hd") {
        720
    } else if has("576") {
        576
    } else if has("480") || has("sd") {
        480
    } else {
        0
    }
}

/// The leftmost match of .NET's `\b(\d{3,4})(?:p|i)?\b`, as (start, digit count) in chars.
fn resolution_height(chars: &[char]) -> Option<(usize, usize)> {
    let n = chars.len();
    let word = |i: usize| i < n && is_regex_boundary_word(chars[i]);
    let boundary = |pos: usize| (pos > 0 && word(pos - 1)) != word(pos);
    for start in 0..n {
        if !is_regex_digit(chars[start]) || (start > 0 && word(start - 1)) {
            continue;
        }
        let mut run = 0;
        while run < 4 && start + run < n && is_regex_digit(chars[start + run]) {
            run += 1;
        }
        // Greedy: four digits before three, the optional p/i before none.
        for len in (3..=run).rev() {
            let end = start + len;
            if end < n && matches!(chars[end], 'p' | 'i') && boundary(end + 1) {
                return Some((start, len));
            }
            if boundary(end) {
                return Some((start, len));
            }
        }
    }
    None
}

/// The label height (what the readout shows), else the first video track's height.
fn version_height(version: &FileVersion) -> i64 {
    let rank = resolution_rank(version.resolution.as_deref());
    if rank > 0 {
        return i64::from(rank);
    }
    version
        .video_tracks
        .iter()
        .flatten()
        .find_map(|track| track.height.filter(|&h| h > 0))
        .map_or(0, |height| height.min(i64::from(i32::MAX)))
}

/// Whether any claim admits the version's first video track. Mirrors the bounds the server checks
/// (silo-server internal/playback/capabilities_v3.go videoDecodeTakesSourceV3: codec, size, bit
/// depth, frame rate, bitrate, profile); unknown facts don't reject.
pub fn decodes(version: &FileVersion, claims: &[DecodeClaim]) -> bool {
    let track = version
        .video_tracks
        .as_ref()
        .and_then(|tracks| tracks.first());
    // SourceDescriptorFromFileV3: the track's codec before the file's, and the video track's own
    // bitrate before the file's, which includes audio.
    let Some(codec) = normalize_codec(
        non_empty(track.and_then(|t| t.codec.as_deref())).or(version.codec_video.as_deref()),
    ) else {
        return false;
    };
    let mut bitrate = track
        .and_then(|t| t.bitrate)
        .filter(|&b| b > 0)
        .or(version.bitrate.filter(|&b| b > 0));
    // normalizeBitrateKbpsV3: a value this large is bits per second, not kbps.
    if let Some(b) = bitrate.filter(|&b| b > 10_000_000) {
        bitrate = Some(b / 1000);
    }
    let width = track.and_then(|t| t.width).filter(|&w| w > 0);
    let height = track
        .and_then(|t| t.height)
        .filter(|&h| h > 0)
        .or_else(|| Some(version_height(version)).filter(|&h| h > 0));
    let fps = frame_rate(track.and_then(|t| t.frame_rate.as_deref()));
    let profile = non_empty(track.and_then(|t| t.profile.as_deref())).map(lower);
    let depth = track.and_then(|t| t.bit_depth);
    claims.iter().any(|claim| {
        claim.codec == codec
            && width.is_none_or(|w| claim.max_width.is_none_or(|max| w <= i64::from(max)))
            && height.is_none_or(|h| claim.max_height.is_none_or(|max| h <= i64::from(max)))
            && depth.is_none_or(|d| claim.bit_depths.is_empty() || claim.bit_depths.contains(&(d as i32)))
            && fps.is_none_or(|f| claim.max_frame_rate.is_none_or(|max| f <= max + 0.01))
            && bitrate.is_none_or(|b| claim.max_bitrate_kbps.is_none_or(|max| b <= i64::from(max)))
            // The server fails an empty or unknown profile against a profile list.
            && (claim.profiles.is_empty() || profile.as_ref().is_some_and(|p| claim.profiles.contains(p)))
    })
}

/// "23.976", "24000/1001" or "29.97 fps"; `None` when unreadable.
fn frame_rate(value: Option<&str>) -> Option<f64> {
    let text = non_empty(value)?.split(' ').next().unwrap_or_default();
    let parts: Vec<&str> = text.split('/').collect();
    match parts[..] {
        [numerator, denominator] => {
            let (numerator, denominator) = (parse_float(numerator), parse_float(denominator));
            if let (Some(numerator), Some(denominator)) = (numerator, denominator)
                && denominator > 0.0
            {
                return Some(numerator / denominator);
            }
            None
        }
        [rate] => parse_float(rate).filter(|&rate| rate > 0.0),
        _ => None,
    }
}

pub fn is_dolby_vision(version: &FileVersion) -> bool {
    is_dolby_vision_codec(version.codec_video.as_deref())
        || version.video_tracks.iter().flatten().any(|track| {
            non_empty(track.dolby_vision.as_deref()).is_some()
                || track.dv_profile.is_some()
                || hdr_format_indicates_dolby_vision(track.video_range_type.as_deref())
                || hdr_format_indicates_dolby_vision(track.video_range.as_deref())
                || is_dolby_vision_codec(track.codec.as_deref())
        })
}

fn hdr_format_indicates_dolby_vision(value: Option<&str>) -> bool {
    let Some(lowered) = trim_lower(value) else {
        return false;
    };
    lowered.contains("dolby")
        || lowered.contains("dovi")
        || lowered
            .split(|c: char| !(c.is_ascii_lowercase() || c.is_ascii_digit()))
            .any(|token| token == "dv")
}

fn is_dolby_vision_codec(value: Option<&str>) -> bool {
    let token = lower(
        &value
            .unwrap_or_default()
            .chars()
            .filter(|&c| is_letter_or_digit(c))
            .collect::<String>(),
    );
    token.contains("dolbyvision")
        || ["dvhe", "dvh1", "dva1", "dvav"]
            .iter()
            .any(|prefix| token.starts_with(prefix))
}

/// Bale's display resolution matching: the display's height and the decode claims to match against.
#[derive(Deserialize)]
pub struct DisplayMatch {
    display_max_height: i32,
    video_decode: Vec<DecodeClaim>,
}

#[derive(Deserialize)]
pub struct SelectArgs {
    versions: Vec<FileVersion>,
    #[serde(rename = "match")]
    display_match: Option<DisplayMatch>,
    selected_file_id: Option<String>,
    last_file_id: Option<String>,
    preferred_quality: Option<String>,
}

/// TvDetailVersionSelection.kt / PlaybackVersionSelector.kt: explicit file, last played, then the
/// best resolution at or below the quality cap; bitrate and size break ties. Auto with a display
/// match picks per `match_display`. Returns the chosen index into `versions`.
pub fn select_version(args: &SelectArgs) -> Option<usize> {
    let versions = &args.versions;
    let find = |id: &Option<String>| {
        id.as_ref()
            .and_then(|id| versions.iter().position(|v| v.file_id.as_ref() == Some(id)))
    };
    if let Some(index) = find(&args.selected_file_id).or_else(|| find(&args.last_file_id)) {
        return Some(index);
    }
    // OrderByDescending(rank).ThenByDescending(bitrate ?? 0).ThenByDescending(size ?? 0), stable.
    let mut ranked: Vec<usize> = (0..versions.len()).collect();
    ranked.sort_by_key(|&i| {
        let v = &versions[i];
        std::cmp::Reverse((
            resolution_rank(v.resolution.as_deref()),
            v.bitrate.unwrap_or(0),
            v.file_size.unwrap_or(0),
        ))
    });
    let quality = non_empty(args.preferred_quality.as_deref()).map(lower);
    let auto = matches!(quality.as_deref(), None | Some("auto"));
    // Only plain Auto matches the display; an "original"-style choice asks for the best.
    if auto && let Some(display_match) = &args.display_match {
        return match_display(versions, &ranked, display_match);
    }
    if auto || matches!(quality.as_deref(), Some("original" | "source" | "max")) {
        return ranked.first().copied();
    }
    let cap = resolution_rank(quality.as_deref());
    ranked
        .iter()
        .copied()
        .find(|&i| resolution_rank(versions[i].resolution.as_deref()) <= cap)
        .or_else(|| ranked.first().copied())
}

/// Auto with display matching: the best version at or below the display that the decoder claims
/// admit, else the best at or below the display (the server transcodes it), else the smallest
/// version above the display. Ties keep the official resolution/bitrate/size order.
fn match_display(
    versions: &[FileVersion],
    ranked: &[usize],
    display_match: &DisplayMatch,
) -> Option<usize> {
    let fits =
        |i: usize| version_height(&versions[i]) <= i64::from(display_match.display_max_height);
    let decodable = |i: usize| decodes(&versions[i], &display_match.video_decode);
    ranked
        .iter()
        .copied()
        .find(|&i| fits(i) && decodable(i))
        .or_else(|| ranked.iter().copied().find(|&i| fits(i)))
        .or_else(|| {
            let mut by_height = ranked.to_vec();
            // OrderBy(height).ThenByDescending(decodes), stable.
            by_height.sort_by_key(|&i| {
                (
                    version_height(&versions[i]),
                    std::cmp::Reverse(decodable(i)),
                )
            });
            by_height.first().copied()
        })
}
