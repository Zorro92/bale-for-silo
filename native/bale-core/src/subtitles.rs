//! Ported from `PlaybackSubtitleResolver` (src/Bale.Core/Playback/PlaybackSubtitles.cs): the format
//! and embedded-track rules. URL checks stay in C#: they must use .NET's own `Uri` parser, the one
//! that later fetches the URL (PlaybackMediaUrl).

use crate::dotnet::lower;
use crate::models::PlaybackPlan;

/// Overlay formats, `PlaybackSubtitleSource`'s constants.
pub const ASS: u32 = 0;
pub const SRT: u32 = 1;
pub const WEBVTT: u32 = 2;
pub const PGS: u32 = 3;
/// The wrapper's bale_subtitle_stream range.
pub const MAX_STREAM_INDEX: i64 = 65535;

/// The in-container image track this plan asks the client to draw.
pub fn resolve_embedded(plan: &PlaybackPlan) -> Option<i64> {
    let subtitle = plan.subtitle.as_ref()?;
    let index = subtitle.embedded.as_ref()?.stream_index?;
    (subtitle.mode.as_deref() == Some("render") && (0..=MAX_STREAM_INDEX).contains(&index))
        .then_some(index)
}

/// The declared format wins; the MIME type decides when it is absent or unknown.
pub fn format(format: Option<&str>, mime_type: Option<&str>) -> Option<u32> {
    named(format)
        .or_else(|| named(mime_type.map(|mime| mime.split(';').next().unwrap_or_default())))
}

fn named(name: Option<&str>) -> Option<u32> {
    // `?.Trim().TrimStart('.').ToLowerInvariant()`: no second trim after the dots.
    let name = lower(name?.trim().trim_start_matches('.'));
    match name.as_str() {
        "ass" | "ssa" | "text/x-ssa" | "text/x-ass" => Some(ASS),
        "srt" | "subrip" | "application/x-subrip" => Some(SRT),
        "vtt" | "webvtt" | "text/vtt" => Some(WEBVTT),
        "sup" | "pgs" | "hdmv_pgs_subtitle" | "application/pgs" | "application/x-pgs" => Some(PGS),
        _ => None,
    }
}
