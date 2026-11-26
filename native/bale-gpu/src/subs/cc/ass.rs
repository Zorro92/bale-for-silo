//! Caption cues as ASS events on the 608 grid, drawn in the subtitle appearance: the script header
//! and `Default` style are the SubRip/WebVTT converters' (`ConvertOptions`, so font, size, colours,
//! outline, shadow and box are the profile's), and every event is positioned on the grid.
//!
//! - The grid is 608's safe caption area, 80% of the picture centred (47 CFR 15.119 (n)(12), as
//!   FFmpeg's `ccaption_dec` and Media3 place it): 32 columns, 15 rows. Rows keep their place; the
//!   appearance's position (bottom, lower third, top) does not move them, as on Android TV, where
//!   Media3's caption cues carry their own line and position.
//! - Vertically adjacent rows that line up become one event, so a box (border style 4) is one box
//!   behind them, as Media3 draws one window behind a cue; the event's bottom line sits on the
//!   bottom of its last row and libass stacks the lines above at the font's line height.
//! - Pop-on captions are aligned by their padding as Media3 does it (`Cea608Decoder.CueBuilder`,
//!   Apache-2.0, ported: see `native/bale-gpu/NOTICE`): about as much space either side is centred,
//!   less space after than before is right-aligned, the rest left-aligned at their first column, and
//!   every row of a caption takes the first of left, centre and right that any row needs. Roll-up
//!   and paint-on start at their column.
//! - 608 colours, italics and underline are inline overrides, as FFmpeg, VLC and mpv keep them;
//!   white is the appearance's text colour (Media3 draws white as the default colour too). Android
//!   TV drops them (`SubtitleManager.kt` sets `setApplyEmbeddedStyles(false)`), which
//!   [`FLAG_PLAIN`] does. With the box (border style 4) a row's 608 background that is not the
//!   default opaque black colours its box (`\4c`, `\4a`; semi-transparent is half), and only then
//!   is black text drawn black: without a box it takes the text colour so that it stays readable.

use std::fmt::Write as _;

use super::cea608::{Background, COLUMNS, Color, Opacity, ROWS};
use super::cue::{Cue, CueKind, CueRow, CueStyle};
use crate::subs::convert::ConvertOptions;
use crate::subs::convert::ass_writer::{Cue as ScriptCue, Line, write_script};
use crate::subs::convert::style::Style;

/// `bale_cc_attach` flag: draw every caption in the appearance alone, without 608 colours, italics,
/// underline or backgrounds (Android TV's look).
pub const FLAG_PLAIN: u32 = 1;

/// The fraction of the picture outside the safe caption area on each side.
const SAFE_MARGIN: f32 = 0.1;
const SAFE_SIZE: f32 = 0.8;

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
enum Anchor {
    Start,
    Middle,
    End,
}

/// The script header (with the `Default` style) a live caption track starts from.
pub fn header(opts: &ConvertOptions) -> String {
    write_script(opts, Vec::new())
}

/// A whole script of `cues` (debug dumps and tests); an open cue runs `open_ms`.
pub fn script(cues: &[Cue], opts: &ConvertOptions, flags: u32, open_ms: i64) -> String {
    let events = cues
        .iter()
        .flat_map(|cue| {
            let start = cue.start_ms.max(0) as u64;
            let end = cue
                .end_ms
                .unwrap_or(cue.start_ms.saturating_add(open_ms))
                .max(0) as u64;
            events(cue, opts, flags)
                .into_iter()
                .map(move |text| ScriptCue {
                    start_ms: start,
                    end_ms: end,
                    text,
                })
        })
        .collect();
    write_script(opts, events)
}

/// The ASS event texts (overrides included) that draw `cue`.
pub fn events(cue: &Cue, opts: &ConvertOptions, flags: u32) -> Vec<String> {
    let plain = flags & FLAG_PLAIN != 0;
    let anchor = match cue.kind {
        CueKind::PopOn => cue
            .rows
            .iter()
            .map(anchor_of)
            .min()
            .unwrap_or(Anchor::Start),
        CueKind::RollUp | CueKind::PaintOn => Anchor::Start,
    };
    let (width, height) = opts.resolution();
    let boxed = opts.border_style == 4 && !plain;
    let placed: Vec<(&CueRow, i32, Option<Background>)> = cue
        .rows
        .iter()
        .map(|row| {
            let x = match anchor {
                Anchor::Start => column_x(f32::from(row.first), width),
                Anchor::Middle => width / 2.0,
                Anchor::End => column_x(f32::from(row.last) + 1.0, width),
            };
            let background = row
                .segments
                .first()
                .map(|segment| segment.style.background)
                .filter(|background| boxed && *background != Background::default());
            (row, x.round() as i32, background)
        })
        .collect();
    let mut out = Vec::new();
    let mut at = 0;
    while at < placed.len() {
        let (_, x, background) = placed[at];
        let mut end = at + 1;
        while end < placed.len()
            && placed[end].0.row == placed[end - 1].0.row + 1
            && placed[end].1 == x
            && placed[end].2 == background
        {
            end += 1;
        }
        let bottom = placed[end - 1].0.row;
        let y = height * (SAFE_MARGIN + SAFE_SIZE * f32::from(bottom) / ROWS as f32);
        let alignment = match anchor {
            Anchor::Start => 1,
            Anchor::Middle => 2,
            Anchor::End => 3,
        };
        let mut block = format!("{{\\an{alignment}\\pos({x},{})", y.round() as i32);
        if let Some(background) = background {
            let (r, g, b) = background.color.rgb();
            let alpha = match background.opacity {
                Opacity::Opaque => 0x00,
                Opacity::SemiTransparent => 0x80,
                Opacity::Transparent => 0xFF,
            };
            let _ = write!(block, "\\4c&H{b:02X}{g:02X}{r:02X}&\\4a&H{alpha:02X}&");
        }
        block.push('}');
        let mut line = Line::new(opts);
        line.raw_block(&block);
        for (i, (row, _, _)) in placed[at..end].iter().enumerate() {
            if i > 0 {
                line.ch('\n');
            }
            for segment in &row.segments {
                line.set_style(style(&segment.style, plain, background.is_some()));
                for ch in segment.text.chars() {
                    line.ch(ch);
                }
            }
        }
        out.extend(line.finish());
        at = end;
    }
    out
}

/// The x of the left edge of 608 column `column` (0..=32) in script pixels.
fn column_x(column: f32, width: f32) -> f32 {
    width * (SAFE_MARGIN + SAFE_SIZE * column / COLUMNS as f32)
}

/// Media3's alignment of one pop-on row from its padding.
fn anchor_of(row: &CueRow) -> Anchor {
    let start = i32::from(row.first);
    let end = COLUMNS as i32 - 1 - i32::from(row.last);
    let delta = start - end;
    if delta.abs() < 3 || end < 0 {
        Anchor::Middle
    } else if delta > 0 {
        Anchor::End
    } else {
        Anchor::Start
    }
}

fn style(style: &CueStyle, plain: bool, boxed: bool) -> Style {
    if plain {
        return Style::default();
    }
    let color = match style.foreground {
        Color::White => None,
        Color::Black if !boxed => None,
        color => Some(color.rgb()),
    };
    Style {
        color,
        italic: style.italic.then_some(true),
        underline: style.underline.then_some(true),
        ..Style::default()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::subs::cc::cue::Segment;
    use crate::subs::convert::TextStyle;

    fn row(row: u8, first: u8, segments: &[(&str, CueStyle)]) -> CueRow {
        let len: usize = segments.iter().map(|(text, _)| text.chars().count()).sum();
        CueRow {
            row,
            first,
            last: first + len as u8 - 1,
            segments: segments
                .iter()
                .map(|(text, style)| Segment {
                    text: (*text).to_owned(),
                    style: *style,
                })
                .collect(),
        }
    }

    fn plain_row(number: u8, first: u8, text: &str) -> CueRow {
        row(number, first, &[(text, CueStyle::default())])
    }

    fn cue(kind: CueKind, rows: Vec<CueRow>) -> Cue {
        Cue {
            start_ms: 1000,
            end_ms: Some(2000),
            kind,
            rows,
        }
    }

    fn boxed() -> ConvertOptions {
        ConvertOptions::styled(
            &TextStyle {
                font_size: 60,
                primary_colour: 0x00FF_FFFF,
                outline_colour: 0,
                back_colour: 0x4000_0000,
                border_style: 4,
                outline: 0.0,
                shadow: 7.5,
                alignment: 2,
                margin_v: 54,
            },
            None,
        )
    }

    #[test]
    fn rows_sit_on_the_grid_and_adjacent_rows_share_one_event() {
        let opts = ConvertOptions::default();
        // Rows 14 and 15 start in column 0 (left-aligned); row 1 is separate.
        let cue = cue(
            CueKind::RollUp,
            vec![
                plain_row(1, 4, "top"),
                plain_row(14, 0, "one"),
                plain_row(15, 0, "two"),
            ],
        );
        assert_eq!(
            events(&cue, &opts, 0),
            [
                // x = 1920 * (0.1 + 0.8 * 4 / 32) = 384; y = 1080 * (0.1 + 0.8 / 15) = 166.
                "{\\an1\\pos(384,166)}top",
                // y = 1080 * 0.9 = 972: the bottom of row 15.
                "{\\an1\\pos(192,972)}one\\Ntwo",
            ]
        );
    }

    #[test]
    fn pop_on_alignment_follows_media3() {
        let opts = ConvertOptions::default();
        // Centred: 11 columns either side of a 10-column row.
        let centred = cue(CueKind::PopOn, vec![plain_row(15, 11, "0123456789")]);
        assert_eq!(
            events(&centred, &opts, 0),
            ["{\\an2\\pos(960,972)}0123456789"]
        );
        // Right: column 20 to 30 (less space after than before).
        let right = cue(CueKind::PopOn, vec![plain_row(15, 20, "abcdefghijk")]);
        // x = 1920 * (0.1 + 0.8 * 31 / 32) = 1680.
        assert_eq!(
            events(&right, &opts, 0),
            ["{\\an3\\pos(1680,972)}abcdefghijk"]
        );
        // Every row takes the first alignment any row needs: one left row makes both left.
        let mixed = cue(
            CueKind::PopOn,
            vec![plain_row(14, 11, "0123456789"), plain_row(15, 2, "abc")],
        );
        assert_eq!(
            events(&mixed, &opts, 0),
            [
                "{\\an1\\pos(720,914)}0123456789",
                "{\\an1\\pos(288,972)}abc"
            ]
        );
        // A row wider than the grid allows is centred.
        let wide = cue(CueKind::PopOn, vec![plain_row(15, 0, &"w".repeat(32))]);
        assert!(events(&wide, &opts, 0)[0].starts_with("{\\an2\\pos(960,972)}"));
        // Roll-up and paint-on never centre.
        let paint = cue(CueKind::PaintOn, vec![plain_row(8, 11, "0123456789")]);
        assert_eq!(
            events(&paint, &opts, 0),
            ["{\\an1\\pos(720,569)}0123456789"]
        );
    }

    #[test]
    fn colours_italics_and_underline_are_overrides_and_white_is_the_appearance() {
        let opts = ConvertOptions::default();
        let yellow = CueStyle {
            foreground: Color::Yellow,
            ..CueStyle::default()
        };
        let italic = CueStyle {
            italic: true,
            ..CueStyle::default()
        };
        let underline_red = CueStyle {
            foreground: Color::Red,
            underline: true,
            ..CueStyle::default()
        };
        let black = CueStyle {
            foreground: Color::Black,
            ..CueStyle::default()
        };
        let cue = cue(
            CueKind::RollUp,
            vec![row(
                15,
                0,
                &[
                    ("a ", CueStyle::default()),
                    ("yellow", yellow),
                    (" ", CueStyle::default()),
                    ("it", italic),
                    ("ul", underline_red),
                    ("k", black),
                ],
            )],
        );
        assert_eq!(
            events(&cue, &opts, 0),
            [
                "{\\an1\\pos(192,972)}a {\\c&H00FFFF&}yellow {\\i1\\c}it{\\i0\\u1\\c&H0000FF&}ul{\\u0\\c}k"
            ]
        );
        assert_eq!(
            events(&cue, &opts, FLAG_PLAIN),
            ["{\\an1\\pos(192,972)}a yellow itulk"]
        );
    }

    #[test]
    fn a_608_background_colours_the_box_and_splits_rows() {
        let opts = boxed();
        let on_white = CueStyle {
            foreground: Color::Black,
            background: Background {
                color: Color::White,
                opacity: Opacity::Opaque,
            },
            ..CueStyle::default()
        };
        let semi_red = CueStyle {
            background: Background {
                color: Color::Red,
                opacity: Opacity::SemiTransparent,
            },
            ..CueStyle::default()
        };
        let clear = CueStyle {
            background: Background {
                color: Color::Black,
                opacity: Opacity::Transparent,
            },
            ..CueStyle::default()
        };
        let cue = cue(
            CueKind::RollUp,
            vec![
                row(12, 0, &[("clear", clear)]),
                row(13, 0, &[("black on white", on_white)]),
                row(14, 0, &[("semi red", semi_red)]),
                plain_row(15, 0, "default"),
            ],
        );
        assert_eq!(
            events(&cue, &opts, 0),
            [
                "{\\an1\\pos(192,799)\\4c&H000000&\\4a&HFF&}clear",
                "{\\an1\\pos(192,857)\\4c&HFFFFFF&\\4a&H00&}{\\c&H000000&}black on white",
                "{\\an1\\pos(192,914)\\4c&H0000FF&\\4a&H80&}semi red",
                "{\\an1\\pos(192,972)}default",
            ]
        );
        // Without the box style, or plain, backgrounds go and black text takes the text colour.
        let unboxed = ConvertOptions::default();
        let texts = events(&cue, &unboxed, 0);
        assert_eq!(texts.len(), 1);
        assert_eq!(
            texts[0],
            "{\\an1\\pos(192,972)}clear\\Nblack on white\\Nsemi red\\Ndefault"
        );
        assert_eq!(events(&cue, &opts, FLAG_PLAIN), texts);
    }

    #[test]
    fn literal_text_is_escaped_and_the_script_holds_every_event() {
        let opts = ConvertOptions::default();
        let cue = Cue {
            start_ms: 1234,
            end_ms: None,
            kind: CueKind::PaintOn,
            rows: vec![plain_row(15, 0, "{x} \\N")],
        };
        let texts = events(&cue, &opts, 0);
        assert_eq!(texts, ["{\\an1\\pos(192,972)}\\{x\\} \\\u{2060}N"]);
        let script = script(&[cue], &opts, 0, 5000);
        assert!(script.starts_with("[Script Info]\n"));
        assert!(script.ends_with(&format!(
            "Dialogue: 0,0:00:01.23,0:00:06.23,Default,,0,0,0,,{}\n",
            texts[0]
        )));
        assert_eq!(header(&opts), write_script(&opts, Vec::new()));
        assert!(header(&opts).ends_with("Effect, Text\n"));
    }
}
