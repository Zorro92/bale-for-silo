//! Caption cues as ASS events on the 608 grid, drawn in the subtitle appearance: the script header's
//! `Default` style is the SubRip/WebVTT converters' (`ConvertOptions`, so font, size, colours and
//! outline are the profile's), and every event is positioned on the grid.
//!
//! - The grid is 608's safe caption area, 80% of the picture centred (47 CFR 15.119 (n)(12), as
//!   FFmpeg's `ccaption_dec` and Media3 place it): 32 columns, 15 rows. Rows keep their place; the
//!   appearance's position (bottom, lower third, top) does not move them, as on Android TV, where
//!   Media3's caption cues carry their own line and position.
//! - Every displayed row is a cell band, as 608 draws its character cells (15.119 (g)): one event
//!   draws the row's text, centred in the band, and with the appearance's box (border style 4) one
//!   event under it draws the row's background over exactly that band, as wide as the row's text
//!   and the box's padding. Bands stack edge to edge, so two rows' backgrounds never overlap or
//!   leave a gap, whatever their indents, and each row's background is its own.
//! - The bands are 608's rows (1/15 of the safe area, 57.6 script pixels) unless the appearance's
//!   text is taller (libass's line height is the font size): then the row pitch is that line height,
//!   so a band is always as tall as its text and its box. A block of adjacent rows keeps the row
//!   nearest the edge of the picture on its 608 line (in the lower half, rows 8-15, its last row's
//!   bottom, else its first row's top) and grows towards the middle, as Media3 counts 608 rows from
//!   the bottom or the top by the middle (`Cea608Decoder.CueBuilder`, `row > BASE_ROW / 2`); a
//!   block that would overlap the next one towards the edge moves to meet it instead.
//! - Pop-on captions are aligned by their padding as Media3 does it (`Cea608Decoder.CueBuilder`,
//!   Apache-2.0, ported: see `native/bale-gpu/NOTICE`): about as much space either side is centred,
//!   less space after than before is right-aligned, the rest left-aligned at their first column, and
//!   every row of a caption takes the first of left, centre and right that any row needs. Roll-up
//!   and paint-on start at their column.
//! - 608 colours, italics and underline are inline overrides, as FFmpeg, VLC and mpv keep them;
//!   white is the appearance's text colour (Media3 draws white as the default colour too). Android
//!   TV drops them (`SubtitleManager.kt` sets `setApplyEmbeddedStyles(false)`), which
//!   [`FLAG_PLAIN`] does. With the box a row's 608 background that is not the default opaque black
//!   colours its box (`\4c`, `\4a`; semi-transparent is half), and only then is black text drawn
//!   black: without a box it takes the text colour so that it stays readable.
//! - Flashing characters (608 Flash On) blink, as captions are authored: 15.119 (h)(2) has the
//!   character "blink from the display at least once per second", so they show for half a second
//!   and are gone for half a second, on the media timeline (even half seconds on), so that every
//!   flashing run blinks together however often a roll-up caption changes. Only the characters go
//!   (fill, outline and shadow, `\t` steps of `\1a`, `\3a`, `\4a` written over the event's
//!   time); their row's background stays, as the rule names only the character. Media3 and FFmpeg
//!   draw flash steady, which [`FLAG_PLAIN`] keeps.
//!
//! How a box event covers exactly its band (libass 0.17.5, `ass_render.c`): border style 4 draws one
//! rectangle per event, the text's bounding box padded by the shadow (`add_background`), and ignores
//! `\clip`. The event writes the row's text again with the same overrides, so its advances, and the
//! rectangle's width, are the text's; `\fscy` near zero takes the glyphs' ascender and descender
//! away (`get_outline_glyph` scales them by it), and a drawing that only moves the pen
//! (`m 0 0 n 0 h`: nothing drawn, a bounding box `h` high) makes the line exactly the band's height.
//! With no vertical padding the rectangle is the band, in whole pixels where the picture is a whole
//! multiple of the 1080-line script; elsewhere a band can end one pixel short (`EventImages` holds
//! whole pixels), never overlapping the next. The `Box` style makes its glyphs transparent.

use std::fmt::Write as _;

use super::cea608::{Background, COLUMNS, Color, Opacity, ROWS};
use super::cue::{Cue, CueKind, CueRow, CueStyle};
use crate::subs::convert::ConvertOptions;
use crate::subs::convert::ass_writer::{Cue as ScriptCue, Line, write_script_styles};
use crate::subs::convert::style::Style;

/// `bale_cc_attach` flag: draw every caption in the appearance alone, without 608 colours, italics,
/// underline or backgrounds (Android TV's look).
pub const FLAG_PLAIN: u32 = 1;

/// The libass layer of the box events: every caption's backgrounds under all of its text.
pub const BOX_LAYER: i32 = 0;
/// The libass layer of the text events.
pub const TEXT_LAYER: i32 = 1;

/// The style a box event switches to with `\r`: border style 4 in the appearance's box colour, with
/// transparent glyphs and no outline or padding of its own.
const BOX_STYLE: &str = "Box";
/// A colour libass does not draw (ASS alpha FF).
const TRANSPARENT: u32 = 0xFF00_0000;
/// `\fscy` of a box event's text: its glyphs keep their advances and lose their height.
const FLAT: &str = "0.001";
/// Half of 608's flash cycle: shown for this long, then gone for as long.
pub const FLASH_HALF_MS: i64 = 500;
/// Alpha of every part of a character that a flash hides: fill, outline and shadow.
const FLASH_OFF: &str = "\\1a&HFF&\\3a&HFF&\\4a&HFF&";
/// The most blink steps one event carries (an hour).
const MAX_FLASH_STEPS: usize = 7200;

/// The fraction of the picture outside the safe caption area on each side.
const SAFE_MARGIN: f32 = 0.1;
const SAFE_SIZE: f32 = 0.8;
/// The first row of the lower half, whose blocks keep their bottom on the grid (Media3:
/// `row > BASE_ROW / 2`).
const LOWER_HALF: u8 = 8;

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
enum Anchor {
    Start,
    Middle,
    End,
}

/// One libass event of a caption.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Event {
    /// [`BOX_LAYER`] or [`TEXT_LAYER`].
    pub layer: i32,
    /// The event text, overrides included.
    pub text: String,
    /// It blinks flashing characters, written for the time it was made for: a longer stretch needs
    /// another event from where this one's steps end.
    pub flashes: bool,
}

/// The script header a live caption track starts from: the `Default` style the text is drawn in,
/// and with a box the `Box` style the backgrounds are.
pub fn header(opts: &ConvertOptions) -> String {
    write(opts, Vec::new())
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
            events(cue, opts, flags, start as i64, end as i64)
                .into_iter()
                .map(move |event| ScriptCue {
                    start_ms: start,
                    end_ms: end,
                    layer: event.layer,
                    text: event.text,
                })
        })
        .collect();
    write(opts, events)
}

fn write(opts: &ConvertOptions, events: Vec<ScriptCue>) -> String {
    let text = text_options(opts);
    match box_options(opts) {
        Some(boxes) => write_script_styles(&text, &[(BOX_STYLE, &boxes)], events),
        None => write_script_styles(&text, &[], events),
    }
}

/// The appearance draws a box behind the text (border style 4).
fn has_box(opts: &ConvertOptions) -> bool {
    opts.border_style == 4
}

/// The `Default` style: the appearance, but a box is drawn by box events, not by the text: border
/// style 4 becomes 1 without a shadow (4's shadow is the box's padding), which draws the glyphs and
/// their outline as 4 does. A row never wraps (WrapStyle 2): it is one line however wide.
fn text_options(opts: &ConvertOptions) -> ConvertOptions {
    let mut text = opts.clone();
    text.wrap_style = 2;
    if has_box(opts) {
        text.border_style = 1;
        text.shadow = 0.0;
    }
    text
}

/// The `Box` style: border style 4 in the appearance's box colour, the glyphs and their outline
/// transparent, no padding of its own (each box event sets its sides with `\xshad`).
fn box_options(opts: &ConvertOptions) -> Option<ConvertOptions> {
    has_box(opts).then(|| ConvertOptions {
        primary_colour: TRANSPARENT,
        outline_colour: TRANSPARENT,
        outline: 0.0,
        shadow: 0.0,
        wrap_style: 2,
        ..opts.clone()
    })
}

/// The events that draw `cue` from `start_ms` to `end_ms` (the tap's timeline): with a box, one box
/// event per row (on [`BOX_LAYER`], first), then one text event per row (on [`TEXT_LAYER`]). Only
/// flashing text depends on the times (its blink steps cover them).
pub fn events(
    cue: &Cue,
    opts: &ConvertOptions,
    flags: u32,
    start_ms: i64,
    end_ms: i64,
) -> Vec<Event> {
    let plain = flags & FLAG_PLAIN != 0;
    // Plain draws the appearance alone, as Media3 and FFmpeg draw flash: steady.
    let flash = cue
        .rows
        .iter()
        .flat_map(|row| &row.segments)
        .any(|segment| segment.style.flash);
    let blink = (flash && !plain).then(|| Blink::new(&text_options(opts), start_ms, end_ms));
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
    let boxed = has_box(opts);
    let bands = bands(&cue.rows, height, pitch(opts, height));
    let mut boxes = Vec::new();
    let mut texts = Vec::new();
    for (row, &(top, bottom)) in cue.rows.iter().zip(&bands) {
        let (x, alignment) = match anchor {
            Anchor::Start => (column_x(f32::from(row.first), width), 4),
            Anchor::Middle => (width / 2.0, 5),
            Anchor::End => (column_x(f32::from(row.last) + 1.0, width), 6),
        };
        // Middle alignment centres the line on the band (libass get_base_point).
        let centre = (top + bottom) as f32 / 2.0;
        let position = format!("\\an{alignment}\\pos({},{centre})", x.round() as i32);
        let background = row
            .segments
            .first()
            .map(|segment| segment.style.background)
            .filter(|background| boxed && !plain && *background != Background::default());
        let mut line = Line::new(opts);
        line.raw_block(&format!("{{{position}}}"));
        let flashes = write_row(&mut line, row, plain, background.is_some(), blink.as_ref());
        let Some(text) = line.finish() else {
            continue;
        };
        texts.push(Event {
            layer: TEXT_LAYER,
            text,
            flashes,
        });
        if !boxed {
            continue;
        }
        // The same text, flat, as wide as the text's and exactly the band's height (module docs).
        let mut block = format!(
            "{{\\r{BOX_STYLE}{position}\\fscy{FLAT}\\xshad{}",
            box_padding(opts)
        );
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
        // The background stays while its characters blink.
        write_row(&mut line, row, plain, background.is_some(), None);
        line.raw_block(&format!(
            "{{\\fscy100\\p1}}m 0 0 n 0 {}{{\\p0}}",
            bottom - top
        ));
        if let Some(text) = line.finish() {
            boxes.push(Event {
                layer: BOX_LAYER,
                text,
                flashes: false,
            });
        }
    }
    boxes.extend(texts);
    boxes
}

/// The row's styled runs, flashing ones blinking with `blink`; whether any did.
fn write_row(
    line: &mut Line<'_>,
    row: &CueRow,
    plain: bool,
    coloured_box: bool,
    blink: Option<&Blink>,
) -> bool {
    let mut flashing = false;
    let mut flashed = false;
    for segment in &row.segments {
        if let Some(blink) = blink
            && segment.style.flash != flashing
        {
            flashing = segment.style.flash;
            flashed |= flashing;
            line.raw_block(if flashing { &blink.steps } else { &blink.on });
        }
        line.set_style(style(&segment.style, plain, coloured_box));
        for ch in segment.text.chars() {
            line.ch(ch);
        }
    }
    flashed
}

/// The override blocks that make a run blink from `start_ms` to `end_ms`, and end it.
struct Blink {
    /// The run's state at the start, then a `\t` step at every half second of the media timeline
    /// in between (libass applies `\t(t,t,...)` at once at `t` after the event's start).
    steps: String,
    /// Back to the text style's alphas, after the run.
    on: String,
}

impl Blink {
    fn new(text: &ConvertOptions, start_ms: i64, end_ms: i64) -> Blink {
        let alpha = |colour: u32| colour >> 24;
        let on = format!(
            "\\1a&H{:02X}&\\3a&H{:02X}&\\4a&H{:02X}&",
            alpha(text.primary_colour),
            alpha(text.outline_colour),
            alpha(text.back_colour)
        );
        let shown = |half: i64| half.rem_euclid(2) == 0;
        let mut steps = String::from("{");
        let mut half = start_ms.div_euclid(FLASH_HALF_MS);
        if !shown(half) {
            steps.push_str(FLASH_OFF);
        }
        half += 1;
        // Every step is after the start (a \t at 0 would run over the whole event). The live track
        // asks for at most PROVISIONAL_MS at a time; the cap only bounds a pathological span.
        for _ in 0..MAX_FLASH_STEPS {
            let Some(at) = half.checked_mul(FLASH_HALF_MS).filter(|&at| at < end_ms) else {
                break;
            };
            let state = if shown(half) { on.as_str() } else { FLASH_OFF };
            let _ = write!(steps, "\\t({0},{0},{state})", at.saturating_sub(start_ms));
            half += 1;
        }
        steps.push('}');
        Blink {
            steps,
            on: format!("{{{on}}}"),
        }
    }
}

/// A box's padding either side of its text: the appearance's (border style 4's shadow) and the
/// glyph outline, which the appearance's box took in too.
fn box_padding(opts: &ConvertOptions) -> f32 {
    let width = |value: f32| {
        if value.is_finite() {
            value.max(0.0)
        } else {
            0.0
        }
    };
    width(opts.shadow) + width(opts.outline)
}

/// The distance between two rows in script pixels: one 608 row, or the text's line height (the
/// font size) when that is taller.
fn pitch(opts: &ConvertOptions, height: f32) -> f32 {
    (height * SAFE_SIZE / ROWS as f32).max(opts.font_size as f32)
}

/// Each row's band, top and bottom edge in whole script pixels, in `rows`' order (ascending rows).
///
/// Rows come in blocks of adjacent rows, `pitch` apart. A block in the lower half (its last row 8
/// or below) keeps its bottom on that row's 608 grid line and grows upwards; any other block keeps
/// its top on its first row's grid line and grows downwards. At 608's own pitch that is the grid;
/// with taller text a block that would overlap the next one towards the edge of the picture moves
/// to meet it. A block is placed by its top edge and every edge is rounded once, so neighbours
/// share theirs.
fn bands(rows: &[CueRow], height: f32, pitch: f32) -> Vec<(i32, i32)> {
    // The edge below row `k` on 608's grid (`k` = 0: the top of the safe area).
    let grid =
        |k: u8| height * (SAFE_MARGIN + SAFE_SIZE * f32::from(k.min(ROWS as u8)) / ROWS as f32);
    let mut blocks: Vec<(usize, usize, f32)> = Vec::new();
    let mut start = 0;
    while start < rows.len() {
        let mut end = start + 1;
        while end < rows.len() && rows[end].row == rows[end - 1].row.saturating_add(1) {
            end += 1;
        }
        blocks.push((start, end, 0.0));
        start = end;
    }
    let lower = |&(_, end, _): &(usize, usize, f32)| rows[end - 1].row >= LOWER_HALF;
    // The upper half from the top down, the lower half from the bottom up.
    let mut floor = f32::MIN;
    for block in blocks.iter_mut().filter(|block| !lower(block)) {
        let top = grid(rows[block.0].row.saturating_sub(1)).max(floor);
        block.2 = top;
        floor = top + (block.1 - block.0) as f32 * pitch;
    }
    let mut ceiling = f32::MAX;
    for block in blocks.iter_mut().rev().filter(|block| lower(block)) {
        let bottom = grid(rows[block.1 - 1].row).min(ceiling);
        block.2 = bottom - (block.1 - block.0) as f32 * pitch;
        ceiling = block.2;
    }
    let mut bands = Vec::with_capacity(rows.len());
    for (start, end, top) in blocks {
        // A block taller than the picture's room is moved back into it.
        let bottom = top + (end - start) as f32 * pitch;
        let top = if top < 0.0 {
            0.0
        } else if bottom > height {
            (top - (bottom - height)).max(0.0)
        } else {
            top
        };
        let edge = |k: usize| (top + k as f32 * pitch).round() as i32;
        bands.extend((0..end - start).map(|k| (edge(k), edge(k + 1))));
    }
    bands
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

fn style(style: &CueStyle, plain: bool, coloured_box: bool) -> Style {
    if plain {
        return Style::default();
    }
    let color = match style.foreground {
        Color::White => None,
        Color::Black if !coloured_box => None,
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
#[path = "ass_tests.rs"]
mod tests;
