//! Ported from `PlaybackFormatting` (src/Bale.Core/Detail): version ranking and Auto's choice,
//! the decode check Bale's version matching shares with the server, and Dolby Vision detection.
//! The Detail page's label text stays in C#.

use serde::{Deserialize, Serialize};

use crate::dotnet::{
    is_letter_or_digit, is_regex_boundary_word, is_regex_digit, lower, non_empty, parse_float,
    trim_lower,
};
use crate::models::{DecodeClaim, FileVersion, PlaybackVariant};
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
    #[serde(default)]
    variants: Vec<PlaybackVariant>,
    /// The viewer's Edition pick (an edition id, see [`playback_editions`]).
    #[serde(default)]
    edition_id: Option<String>,
    /// `user_data.last_edition_key`; a present "" names the unnamed edition.
    #[serde(default)]
    last_edition_key: Option<String>,
    /// The item has a resume point (a saved position above 0).
    #[serde(default)]
    in_progress: bool,
}

#[derive(Deserialize)]
pub struct EditionsArgs {
    pub versions: Vec<FileVersion>,
    #[serde(default)]
    pub variants: Vec<PlaybackVariant>,
}

/// One edition (cut) with every presentation of it merged (PlaybackEditions.kt). `versions` are
/// indexes into the caller's versions in server order: each presentation's first part, else every
/// file of the edition. `file_ids` are every file of the edition, all parts.
#[derive(Debug, Serialize, PartialEq)]
pub struct Edition {
    pub id: String,
    pub label: String,
    pub versions: Vec<usize>,
    pub file_ids: Vec<String>,
}

/// An edition's label: its raw name, else the key's words capitalised (IMAX kept upper case).
pub fn edition_label(raw: Option<&str>, key: Option<&str>) -> Option<String> {
    if let Some(raw) = raw.map(str::trim).filter(|raw| !raw.is_empty()) {
        return Some(raw.to_string());
    }
    let key = key.map(str::trim).filter(|key| !key.is_empty())?;
    let words: Vec<String> = key
        .split(['_', '-'])
        .filter(|word| !word.is_empty())
        .map(|word| {
            if word.eq_ignore_ascii_case("imax") {
                "IMAX".to_string()
            } else {
                let mut chars = word.chars();
                chars.next().map_or_else(String::new, |first| {
                    first.to_uppercase().collect::<String>() + chars.as_str()
                })
            }
        })
        .collect();
    Some(words.join(" "))
}

fn edition_key(key: Option<&str>) -> String {
    key.map(str::trim).unwrap_or_default().to_string()
}

/// The editions of an item, one per edition key, in server order (the order of `variants`, else of
/// first appearance in `versions`). PlaybackEditions.kt `playbackEditions`.
pub fn playback_editions(versions: &[FileVersion], variants: &[PlaybackVariant]) -> Vec<Edition> {
    let mut keys: Vec<String> = Vec::new();
    let mut add_key = |key: String| {
        if !keys.contains(&key) {
            keys.push(key);
        }
    };
    let make =
        |key: &String, raws: Vec<Option<&str>>, mut indexes: Vec<usize>, file_ids: Vec<String>| {
            indexes.sort_unstable();
            indexes.dedup();
            let raw = raws
                .into_iter()
                .flatten()
                .map(str::trim)
                .find(|raw| !raw.is_empty());
            Edition {
                id: key.clone(),
                label: edition_label(raw, Some(key)).unwrap_or_else(|| "Standard".to_string()),
                versions: indexes,
                file_ids,
            }
        };
    if !variants.is_empty() {
        // Kotlin's associateBy keeps the last catalog object for an id.
        let by_id = |id: &str| {
            versions
                .iter()
                .rposition(|v| v.file_id.as_deref() == Some(id))
        };
        variants
            .iter()
            .for_each(|v| add_key(edition_key(v.edition_key.as_deref())));
        return keys
            .iter()
            .filter_map(|key| {
                let cut: Vec<&PlaybackVariant> = variants
                    .iter()
                    .filter(|v| edition_key(v.edition_key.as_deref()) == *key)
                    .collect();
                let choices: Vec<usize> = cut
                    .iter()
                    .filter_map(|v| {
                        v.parts
                            .iter()
                            .flatten()
                            .min_by_key(|part| part.part_index.unwrap_or(0))
                    })
                    .flat_map(|part| part.versions.iter().flatten())
                    .filter_map(|file| file.file_id.as_deref().and_then(by_id))
                    .collect();
                if choices.is_empty() {
                    return None;
                }
                let mut file_ids: Vec<String> = Vec::new();
                for id in cut
                    .iter()
                    .flat_map(|v| v.parts.iter().flatten())
                    .flat_map(|part| part.versions.iter().flatten())
                    .filter_map(|file| file.file_id.clone())
                {
                    if !file_ids.contains(&id) {
                        file_ids.push(id);
                    }
                }
                let raws = cut.iter().map(|v| v.edition_raw.as_deref()).collect();
                Some(make(key, raws, choices, file_ids))
            })
            .collect();
    }
    versions
        .iter()
        .for_each(|v| add_key(edition_key(v.edition_key.as_deref())));
    keys.iter()
        .map(|key| {
            let indexes: Vec<usize> = (0..versions.len())
                .filter(|&i| edition_key(versions[i].edition_key.as_deref()) == *key)
                .collect();
            let raws = indexes
                .iter()
                .map(|&i| versions[i].edition_raw.as_deref())
                .collect();
            let file_ids = indexes
                .iter()
                .filter_map(|&i| versions[i].file_id.clone())
                .collect();
            make(key, raws, indexes, file_ids)
        })
        .collect()
}

/// How Auto ranks inside an edition: the official quality cap (None is no cap), or Bale's display
/// resolution matching.
enum AutoMode<'a> {
    Cap(Option<i64>),
    Display(&'a DisplayMatch),
}

/// The file a detail page shows and Play starts (DetailVersionSelector.kt). Edition is the cut and
/// Version Auto picks a file inside it, never changing it:
/// - `selected_file_id`, an explicit Version pick, wins.
/// - The edition is `edition_id`, else the last played one (`last_edition_key`, then the edition
///   of `last_file_id`), else the preferred cut by name whose Auto file compares best.
/// - History picks only the edition, except a later part of a multi-part item while it is
///   `in_progress`, so Resume continues that part.
/// - Auto inside the edition ranks like the server's version order (resolution, HDR, file size;
///   no bitrate) among first parts. Plain Auto with a display match applies Bale's display
///   resolution matching; a quality cap or Original uses the official rule.
///
/// Returns the chosen index into `versions`.
pub fn select_version(args: &SelectArgs) -> Option<usize> {
    let versions = &args.versions;
    if versions.is_empty() {
        return None;
    }
    let find = |id: Option<&str>| {
        id.and_then(|id| {
            versions
                .iter()
                .position(|v| v.file_id.as_deref() == Some(id))
        })
    };
    if let Some(index) = find(args.selected_file_id.as_deref()) {
        return Some(index);
    }
    let quality = non_empty(args.preferred_quality.as_deref()).map(lower);
    let mode = match quality.as_deref() {
        None | Some("auto") => args
            .display_match
            .as_ref()
            .map_or(AutoMode::Cap(None), AutoMode::Display),
        Some("original" | "source" | "max") => AutoMode::Cap(None),
        Some(other) => {
            AutoMode::Cap(Some(i64::from(resolution_rank(Some(other)))).filter(|&cap| cap > 0))
        }
    };
    let editions = playback_editions(versions, &args.variants);
    if editions.is_empty() {
        let all: Vec<usize> = (0..versions.len()).collect();
        return Some(auto_file(versions, &first_parts(versions, &all), &mode));
    }
    let by_id = |id: &str| editions.iter().position(|e| e.id == id);
    let contains = |e: &Edition, id: &str| e.file_ids.iter().any(|f| f == id);
    let edition = args
        .edition_id
        .as_deref()
        .and_then(by_id)
        .or_else(|| {
            args.last_edition_key
                .as_deref()
                .map(str::trim)
                .and_then(by_id)
        })
        .or_else(|| {
            let last = args.last_file_id.as_deref()?;
            editions.iter().position(|e| contains(e, last))
        })
        .unwrap_or_else(|| opening_edition(versions, &editions, &mode));
    let edition = &editions[edition];
    if args.in_progress
        && let Some(last) = find(args.last_file_id.as_deref())
        && versions[last]
            .file_id
            .as_deref()
            .is_some_and(|id| contains(edition, id))
        && first_playback_part(versions, last) != last
    {
        return Some(last);
    }
    Some(auto_file(
        versions,
        &first_parts(versions, &edition.versions),
        &mode,
    ))
}

const SEQUENTIAL_PART_KINDS: [&str; 2] = ["multipart_movie", "split_episode"];

fn is_sequential_part(v: &FileVersion) -> bool {
    v.presentation_part_index.is_some()
        && v.presentation_kind
            .as_deref()
            .is_some_and(|k| SEQUENTIAL_PART_KINDS.contains(&k))
}

/// The first part of `selected`'s presentation, preferring its resolution (PlaybackParts.kt).
fn first_playback_part(versions: &[FileVersion], selected: usize) -> usize {
    let current = &versions[selected];
    if !is_sequential_part(current) {
        return selected;
    }
    let parts: Vec<usize> = (0..versions.len())
        .filter(|&i| {
            let v = &versions[i];
            is_sequential_part(v)
                && v.presentation_kind == current.presentation_kind
                && v.presentation_group_key == current.presentation_group_key
                && v.edition_key == current.edition_key
        })
        .collect();
    let Some(first) = parts
        .iter()
        .filter_map(|&i| versions[i].presentation_part_index)
        .min()
    else {
        return selected;
    };
    if current.presentation_part_index == Some(first) {
        return selected;
    }
    let candidates: Vec<usize> = parts
        .into_iter()
        .filter(|&i| versions[i].presentation_part_index == Some(first))
        .collect();
    candidates
        .iter()
        .copied()
        .find(|&i| versions[i].resolution == current.resolution)
        .unwrap_or(candidates[0])
}

/// Auto starts a multi-part item at its first part.
fn first_parts(versions: &[FileVersion], candidates: &[usize]) -> Vec<usize> {
    let firsts: Vec<usize> = candidates
        .iter()
        .copied()
        .filter(|&i| first_playback_part(versions, i) == i)
        .collect();
    if firsts.is_empty() {
        candidates.to_vec()
    } else {
        firsts
    }
}

/// The server's version order: resolution, HDR before SDR, larger file; stable otherwise.
fn server_order(versions: &[FileVersion], candidates: &[usize]) -> Vec<usize> {
    let mut ordered = candidates.to_vec();
    ordered.sort_by_key(|&i| {
        let v = &versions[i];
        std::cmp::Reverse((
            version_height(v),
            v.hdr.unwrap_or(false),
            v.file_size.unwrap_or(0),
        ))
    });
    ordered
}

/// Version Auto among one edition's first-part `candidates`. A file of unknown resolution is picked
/// only when no file's resolution is known.
fn auto_file(versions: &[FileVersion], candidates: &[usize], mode: &AutoMode) -> usize {
    let ordered = server_order(versions, candidates);
    let known: Vec<usize> = ordered
        .iter()
        .copied()
        .filter(|&i| version_height(&versions[i]) > 0)
        .collect();
    if known.is_empty() {
        return ordered[0];
    }
    match mode {
        AutoMode::Cap(None) => known[0],
        AutoMode::Cap(Some(cap)) => known
            .iter()
            .copied()
            .find(|&i| version_height(&versions[i]) <= *cap)
            .unwrap_or_else(|| smallest(versions, &known, |_| true)),
        AutoMode::Display(display_match) => match_display(versions, &known, display_match),
    }
}

/// The lowest file in `ranked`, ties broken by `prefer` then rank order.
fn smallest(versions: &[FileVersion], ranked: &[usize], prefer: impl Fn(usize) -> bool) -> usize {
    let mut by_height = ranked.to_vec();
    by_height.sort_by_key(|&i| (version_height(&versions[i]), std::cmp::Reverse(prefer(i))));
    by_height[0]
}

/// Auto with display matching: the best version at or below the display that the decoder claims
/// admit, else the best at or below the display (the server transcodes it), else the smallest
/// version above the display. Ties keep the server order.
fn match_display(
    versions: &[FileVersion],
    ranked: &[usize],
    display_match: &DisplayMatch,
) -> usize {
    let fits =
        |i: usize| version_height(&versions[i]) <= i64::from(display_match.display_max_height);
    // Display matching ranks versions the console plays comfortably: hardware entries and measured
    // (bounded) software ones. The unbounded software fallbacks declared since 2026-11-17 admit any
    // file, so they would pick a 4K software decode over a hardware 1080p version.
    let comfortable: Vec<DecodeClaim> = display_match
        .video_decode
        .iter()
        .filter(|claim| claim.hardware || claim.max_width.is_some() || claim.max_height.is_some())
        .cloned()
        .collect();
    let decodable = |i: usize| decodes(&versions[i], &comfortable);
    ranked
        .iter()
        .copied()
        .find(|&i| fits(i) && decodable(i))
        .or_else(|| ranked.iter().copied().find(|&i| fits(i)))
        .unwrap_or_else(|| smallest(versions, ranked, decodable))
}

/// The edition a page opens on with no history, chosen by name first (DetailVersionSelector.kt,
/// Android e15879514; Apple b54fefb1, Roku 3e7ee3a, server 66105aa): only the cuts in the most
/// preferred `edition_preference` class compete (unnamed or standard, then theatrical, then any
/// other), so a standard cut is never passed over for a longer one in better quality. Among those,
/// each cut's Auto file is compared by fit (within the cap or display, above it, unknown
/// resolution), then resolution (higher within, lower above), then HDR, then server order. File
/// size is not compared across cuts.
fn opening_edition(versions: &[FileVersion], editions: &[Edition], mode: &AutoMode) -> usize {
    let cap = match mode {
        AutoMode::Cap(cap) => *cap,
        AutoMode::Display(display_match) => Some(i64::from(display_match.display_max_height)),
    };
    let best_class = editions.iter().map(edition_preference).min().unwrap_or(0);
    (0..editions.len())
        .filter(|&index| edition_preference(&editions[index]) == best_class)
        .min_by_key(|&index| {
            let edition = &editions[index];
            let auto =
                &versions[auto_file(versions, &first_parts(versions, &edition.versions), mode)];
            let height = version_height(auto);
            let fit = match cap {
                _ if height == 0 => 2,
                Some(cap) if height > cap => 1,
                _ => 0,
            };
            let resolution = if fit == 1 { height } else { -height };
            (fit, resolution, !auto.hdr.unwrap_or(false), index)
        })
        .unwrap_or(0)
}

/// 0 for the unnamed or standard edition, 1 for a theatrical one, 2 for any other cut.
fn edition_preference(edition: &Edition) -> u8 {
    let names: Vec<String> = [Some(edition.id.as_str()), Some(edition.label.as_str())]
        .into_iter()
        .flatten()
        .map(|name| name.trim().to_lowercase())
        .filter(|name| !name.is_empty())
        .collect();
    if edition.id.trim().is_empty()
        || names.iter().any(|n| {
            n == "standard"
                || n == "default"
                || n.starts_with("standard ")
                || n.ends_with(" standard")
        })
    {
        0
    } else if names.iter().any(|n| n.contains("theatrical")) {
        1
    } else {
        2
    }
}

#[cfg(test)]
mod tests {
    use serde_json::{Value, json};

    /// `select_version`'s pick as the file id, from a JSON `SelectArgs` (the C# wrapper's shape).
    fn select(args: Value) -> Option<String> {
        let args: super::SelectArgs = serde_json::from_value(args).unwrap();
        super::select_version(&args).and_then(|index| args.versions[index].file_id.clone())
    }

    fn cut(id: &str, key: Option<&str>, resolution: &str, hdr: bool, size: i64) -> Value {
        json!({ "file_id": id, "edition_key": key, "edition_raw": key, "resolution": resolution,
                "hdr": hdr, "file_size": size })
    }

    fn opening(files: Vec<Value>, quality: &str) -> Option<String> {
        select(json!({ "versions": files, "preferred_quality": quality }))
    }

    // DetailVersionSelectorTest.kt withNoHistoryTheCutIsPickedByNameThenQuality (Android e15879514).
    #[test]
    fn with_no_history_the_cut_is_picked_by_name_then_quality() {
        let q = "2160p";
        // Name first (unnamed or standard, then theatrical, then any other cut), even over better quality.
        let standard = cut("1", Some("standard"), "1080p", false, 1);
        let extended = cut("2", Some("extended"), "2160p", true, 1);
        assert_eq!(opening(vec![standard, extended], q).as_deref(), Some("1"));
        let theatrical = || cut("1", Some("theatrical"), "1080p", false, 1);
        let unnamed = cut("2", None, "1080p", false, 1);
        assert_eq!(
            opening(vec![theatrical(), unnamed], q).as_deref(),
            Some("2")
        );
        let standard_edition = cut("2", Some("standard edition"), "1080p", false, 1);
        assert_eq!(
            opening(vec![theatrical(), standard_edition], q).as_deref(),
            Some("2")
        );
        let directors = cut("1", Some("directors_cut"), "1080p", false, 9_000);
        let theatrical2 = cut("2", Some("theatrical"), "1080p", false, 1);
        assert_eq!(
            opening(vec![directors, theatrical2], q).as_deref(),
            Some("2")
        );
        // Among equally named cuts, a file within the cap beats one only above it.
        let imax = || cut("1", Some("imax"), "2160p", false, 1);
        let extended720 = || cut("2", Some("extended"), "720p", false, 1);
        assert_eq!(
            opening(vec![imax(), extended720()], "1080p").as_deref(),
            Some("2")
        );
        // Above the cap, the closer one wins.
        assert_eq!(
            opening(vec![imax(), extended720()], "480p").as_deref(),
            Some("2")
        );
        // Same resolution: HDR; never file size.
        let big = cut("1", Some("extended"), "1080p", false, 9_000);
        let hdr = cut("2", Some("directors_cut"), "1080p", true, 1);
        assert_eq!(opening(vec![big, hdr], q).as_deref(), Some("2"));
        // A full tie keeps server order.
        let first = cut("1", Some("extended"), "1080p", false, 1);
        let second = cut("2", Some("directors_cut"), "1080p", false, 9_000);
        assert_eq!(opening(vec![first, second], q).as_deref(), Some("1"));
    }

    // DetailEditionSelectionTests.swift testWithNothingPlayedThePageComparesEachCutsAutoFile (Apple b54fefb1).
    #[test]
    fn the_standard_cut_opens_before_a_better_longer_one() {
        let files = || {
            vec![
                json!({ "file_id": "1", "edition_key": "theatrical", "edition_raw": "Theatrical",
                        "resolution": "1080p", "file_size": 90 }),
                json!({ "file_id": "2", "resolution": "1080p", "file_size": 10 }),
                json!({ "file_id": "3", "edition_key": "extended", "edition_raw": "Extended",
                        "resolution": "2160p", "hdr": true }),
                json!({ "file_id": "4", "edition_key": "imax", "edition_raw": "IMAX", "resolution": "" }),
            ]
        };
        let pick = |ids: &[&str], quality: &str| {
            let chosen = files()
                .into_iter()
                .filter(|f| ids.contains(&f["file_id"].as_str().unwrap()))
                .collect();
            opening(chosen, quality)
        };
        // The unnamed cut over Theatrical, though its file is smaller.
        assert_eq!(pick(&["1", "2"], "auto").as_deref(), Some("2"));
        // The 1080p SDR standard cut over a 2160p HDR Extended one.
        assert_eq!(pick(&["2", "3"], "auto").as_deref(), Some("2"));
        // Above the cap before unknown.
        assert_eq!(pick(&["3", "4"], "720p").as_deref(), Some("3"));
        // versionRankingUtils.test.ts (server 66105aa): with no standard cut, quality decides.
        let directors =
            json!({ "file_id": "5", "edition_key": "directors", "resolution": "1080p" });
        let extended = json!({ "file_id": "6", "edition_key": "extended", "resolution": "2160p" });
        assert_eq!(
            opening(vec![directors, extended], "auto").as_deref(),
            Some("6")
        );
    }

    // choice.spec.bs (Roku 3e7ee3a): a last played edition still wins, and Auto stays inside it.
    #[test]
    fn a_last_played_edition_still_wins_over_the_name() {
        let files = json!([
            { "file_id": "201", "edition_key": "standard", "edition_raw": "Standard", "resolution": "1080p" },
            { "file_id": "202", "edition_key": "extended", "edition_raw": "Extended", "resolution": "2160p",
              "hdr": true },
            { "file_id": "203", "edition_key": "extended", "edition_raw": "Extended", "resolution": "720p" },
        ]);
        let with = |extra: Value| {
            let mut args = json!({ "versions": files, "preferred_quality": "auto" });
            args.as_object_mut()
                .unwrap()
                .extend(extra.as_object().unwrap().clone());
            select(args)
        };
        assert_eq!(with(json!({})).as_deref(), Some("201"));
        assert_eq!(
            with(json!({ "last_edition_key": "extended" })).as_deref(),
            Some("202")
        );
        assert_eq!(
            with(json!({ "last_file_id": "203" })).as_deref(),
            Some("202")
        );
        assert_eq!(
            with(json!({ "edition_id": "extended" })).as_deref(),
            Some("202")
        );
    }

    // Bale's display matching keeps the official Edition rule: the name class comes before the display fit.
    #[test]
    fn display_matching_ranks_only_inside_the_preferred_cut() {
        let display = json!({ "display_max_height": 1080, "video_decode": [{
            "codec": "hevc", "profiles": [], "bit_depths": [8, 10], "max_width": 3840, "max_height": 2160,
            "max_frame_rate": 60, "max_bitrate_kbps": null, "hardware": true }] });
        let files = json!([
            { "file_id": "1", "edition_key": "extended", "edition_raw": "Extended", "resolution": "1080p",
              "codec_video": "hevc" },
            { "file_id": "2", "edition_key": "theatrical", "edition_raw": "Theatrical", "resolution": "2160p",
              "codec_video": "hevc" },
            { "file_id": "3", "edition_key": "theatrical", "edition_raw": "Theatrical", "resolution": "720p",
              "codec_video": "hevc" },
        ]);
        // Theatrical outranks Extended by name; the display then picks inside it (no 1080p there: 720p).
        let args = json!({ "versions": files, "match": display, "preferred_quality": null });
        assert_eq!(select(args).as_deref(), Some("3"));
    }
}
