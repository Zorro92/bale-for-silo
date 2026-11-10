//! Text subtitle formats as ASS scripts, so that the overlay draws every text subtitle with
//! libass: SubRip ([`srt_to_ass`]) and WebVTT ([`vtt_to_ass`]). Pure Rust and platform
//! independent; nothing here panics on any input, and a cue that cannot be read is skipped.
//!
//! Escaping of literal cue text (see `ass_writer::Line`):
//! - a line break becomes `\N`;
//! - a backslash is followed by a zero-width WORD JOINER (U+2060), which keeps libass from
//!   reading it as the start of `\N`, `\n`, `\h`, `\{` or `\}` and is not drawn;
//! - `{` and `}` are written as libass's `\{` and `\}` escapes, so they never open an override
//!   block. Only SubRip's own `{\...}` blocks are passed through as overrides.

mod ass_writer;
mod srt;
mod style;
mod vtt;

use std::borrow::Cow;

pub use ass_writer::ConvertOptions;
pub use srt::srt_to_ass;
pub use vtt::vtt_to_ass;

/// `format` values of `bale_sub_overlay_load_text`.
pub const FORMAT_ASS: u32 = 0;
pub const FORMAT_SRT: u32 = 1;
pub const FORMAT_VTT: u32 = 2;

/// Subtitle file bytes in `format` as ASS script bytes: ASS/SSA as it is, SubRip and WebVTT
/// converted (bytes that are not UTF-8 are decoded lossily). `None` for an unknown format.
pub fn to_ass_script<'a>(
    format: u32,
    data: &'a [u8],
    opts: &ConvertOptions,
) -> Option<Cow<'a, [u8]>> {
    let convert: fn(&str, &ConvertOptions) -> String = match format {
        FORMAT_ASS => return Some(Cow::Borrowed(data)),
        FORMAT_SRT => srt_to_ass,
        FORMAT_VTT => vtt_to_ass,
        _ => return None,
    };
    let text = String::from_utf8_lossy(data);
    Some(Cow::Owned(convert(&text, opts).into_bytes()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn formats_dispatch_to_their_converter() {
        let opts = ConvertOptions::default();
        let ass = b"[Script Info]\n";
        assert!(matches!(
            to_ass_script(FORMAT_ASS, ass, &opts),
            Some(Cow::Borrowed(bytes)) if bytes == ass
        ));
        let srt = to_ass_script(FORMAT_SRT, b"00:00:01,000 --> 00:00:02,000\n<i>x\n", &opts);
        let srt = String::from_utf8(srt.unwrap().into_owned()).unwrap();
        assert!(srt.ends_with("Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\i1}x\n"));
        let vtt = to_ass_script(
            FORMAT_VTT,
            b"WEBVTT\n\n00:01.000 --> 00:02.000\n<i>x\n",
            &opts,
        );
        let vtt = String::from_utf8(vtt.unwrap().into_owned()).unwrap();
        assert!(vtt.ends_with("Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\i1}x\n"));
        assert!(to_ass_script(3, b"x", &opts).is_none());
        assert!(to_ass_script(u32::MAX, b"x", &opts).is_none());
    }

    #[test]
    fn bytes_that_are_not_utf8_are_decoded_lossily() {
        let opts = ConvertOptions::default();
        let srt = to_ass_script(
            FORMAT_SRT,
            b"00:00:01,000 --> 00:00:02,000\ncaf\xe9\n",
            &opts,
        );
        let srt = String::from_utf8(srt.unwrap().into_owned()).unwrap();
        assert!(srt.ends_with(",,caf\u{fffd}\n"));
    }
}
