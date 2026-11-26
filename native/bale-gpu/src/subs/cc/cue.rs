//! What a caption decoder shows, as data: each change of displayed memory starts a [`Cue`] that
//! lasts until the next change, with the rows it shows, their 608 columns and styled runs.

use super::cea608::{Background, COLUMNS, Color, Memory, Mode};

/// What a run of text looks like (608 flash is drawn steady, as Media3 and FFmpeg draw it).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub struct CueStyle {
    pub foreground: Color,
    pub italic: bool,
    pub underline: bool,
    pub background: Background,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Segment {
    pub text: String,
    pub style: CueStyle,
}

/// One displayed row: from its first to its last visible character (spaces at either end and
/// empty cells left out; empty cells inside become spaces).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CueRow {
    /// 1..=15.
    pub row: u8,
    /// First and last column of the text, 0..=31.
    pub first: u8,
    pub last: u8,
    pub segments: Vec<Segment>,
}

impl CueRow {
    pub fn text(&self) -> String {
        self.segments.iter().map(|s| s.text.as_str()).collect()
    }
}

/// How the shown caption came about; only pop-on captions are laid out by their padding.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CueKind {
    PopOn,
    RollUp,
    PaintOn,
}

impl CueKind {
    pub fn of(mode: Mode) -> CueKind {
        match mode {
            Mode::RollUp => CueKind::RollUp,
            Mode::PaintOn => CueKind::PaintOn,
            Mode::PopOn | Mode::Unknown => CueKind::PopOn,
        }
    }
}

/// A displayed caption, in milliseconds of the tap's timeline.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Cue {
    pub start_ms: i64,
    /// `None` while it is still displayed.
    pub end_ms: Option<i64>,
    pub kind: CueKind,
    /// Ascending by row, at least one.
    pub rows: Vec<CueRow>,
}

impl Cue {
    /// The rows' text, one line per row (tests and diagnostics).
    pub fn text(&self) -> String {
        self.rows
            .iter()
            .map(CueRow::text)
            .collect::<Vec<_>>()
            .join("\n")
    }
}

/// The visible rows of `memory`.
pub fn displayed_rows(memory: &Memory) -> Vec<CueRow> {
    let mut rows = Vec::new();
    for (index, cells) in memory.0.iter().enumerate() {
        let visible = |col: &usize| cells[*col].is_some_and(|cell| !cell.ch.is_whitespace());
        let (Some(first), Some(last)) = ((0..COLUMNS).find(visible), (0..COLUMNS).rfind(visible))
        else {
            continue;
        };
        let mut segments: Vec<Segment> = Vec::new();
        for cell in &cells[first..=last] {
            let (ch, style) = match cell {
                Some(cell) => (
                    cell.ch,
                    CueStyle {
                        foreground: cell.pen.foreground,
                        italic: cell.pen.italic,
                        underline: cell.pen.underline,
                        background: cell.pen.background,
                    },
                ),
                None => (' ', CueStyle::default()),
            };
            match segments.last_mut() {
                Some(segment) if segment.style == style => segment.text.push(ch),
                _ => segments.push(Segment {
                    text: ch.to_string(),
                    style,
                }),
            }
        }
        rows.push(CueRow {
            row: index as u8 + 1,
            first: first as u8,
            last: last as u8,
            segments,
        });
    }
    rows
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::subs::cc::cea608::{Cell, Pen};

    fn put(memory: &mut Memory, row: usize, col: usize, text: &str, pen: Pen) {
        for (i, ch) in text.chars().enumerate() {
            memory.0[row - 1][col + i] = Some(Cell { ch, pen });
        }
    }

    #[test]
    fn rows_trim_spaces_and_merge_equal_styles() {
        let mut memory = Memory::EMPTY;
        let green = Pen {
            foreground: Color::Green,
            ..Pen::default()
        };
        put(&mut memory, 15, 2, "  ab", Pen::default());
        put(&mut memory, 15, 6, "cd", green);
        put(&mut memory, 15, 10, "e  ", Pen::default());
        put(&mut memory, 3, 0, "   ", green);
        let rows = displayed_rows(&memory);
        assert_eq!(rows.len(), 1, "a row of spaces shows nothing");
        let row = &rows[0];
        assert_eq!((row.row, row.first, row.last), (15, 4, 10));
        assert_eq!(row.text(), "abcd  e");
        let texts: Vec<(&str, Color)> = row
            .segments
            .iter()
            .map(|s| (s.text.as_str(), s.style.foreground))
            .collect();
        // The two empty cells between d and e take the default style.
        assert_eq!(
            texts,
            [
                ("ab", Color::White),
                ("cd", Color::Green),
                ("  e", Color::White)
            ]
        );
    }

    #[test]
    fn cue_text_joins_rows_and_kinds_follow_modes() {
        let mut memory = Memory::EMPTY;
        put(&mut memory, 1, 0, "top", Pen::default());
        put(&mut memory, 14, 31, "x", Pen::default());
        let cue = Cue {
            start_ms: 0,
            end_ms: None,
            kind: CueKind::of(Mode::Unknown),
            rows: displayed_rows(&memory),
        };
        assert_eq!(cue.text(), "top\nx");
        assert_eq!(cue.rows[1].first, 31);
        assert_eq!(cue.kind, CueKind::PopOn);
        assert_eq!(CueKind::of(Mode::RollUp), CueKind::RollUp);
        assert_eq!(CueKind::of(Mode::PaintOn), CueKind::PaintOn);
        assert!(displayed_rows(&Memory::EMPTY).is_empty());
    }
}
