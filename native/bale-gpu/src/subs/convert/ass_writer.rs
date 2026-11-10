//! The ASS side of the converters: the default style, the script header, and a per-cue text
//! builder that escapes literal text and turns style changes into override blocks.

use std::fmt::Write as _;

use super::style::Style;

/// Zero-width WORD JOINER, written after every literal backslash so that libass cannot read the
/// backslash and the next character as `\N`, `\n`, `\h`, `\{` or `\}`. libass drops it when
/// shaping, so it is never drawn.
pub(crate) const BACKSLASH_GUARD: char = '\u{2060}';

/// The style converted cues are drawn with (the script's `Default` style). Colours are ASS
/// colours, `0xAABBGGRR` with AA = transparency (0 = opaque).
#[derive(Clone, Debug, PartialEq)]
pub struct ConvertOptions {
    pub font_name: String,
    /// Font size in script pixels (at `play_res_y`).
    pub font_size: u32,
    pub play_res_x: u32,
    pub play_res_y: u32,
    /// Text colour; also written as the secondary colour.
    pub primary_colour: u32,
    pub outline_colour: u32,
    /// Shadow colour (the box colour with `border_style` 3).
    pub back_colour: u32,
    pub bold: bool,
    /// 1 = outline and shadow, 3 = opaque box.
    pub border_style: u32,
    pub outline: f32,
    pub shadow: f32,
    /// Numpad alignment, 1..=9 (2 = bottom centre).
    pub alignment: u32,
    pub margin_l: u32,
    pub margin_r: u32,
    pub margin_v: u32,
    pub scaled_border_and_shadow: bool,
    pub wrap_style: u32,
}

impl Default for ConvertOptions {
    fn default() -> Self {
        ConvertOptions {
            font_name: "Arial".to_owned(),
            font_size: 60,
            play_res_x: 1920,
            play_res_y: 1080,
            primary_colour: 0x00FF_FFFF,
            outline_colour: 0x0000_0000,
            back_colour: 0x8000_0000,
            bold: false,
            border_style: 1,
            outline: 3.0,
            shadow: 1.0,
            alignment: 2,
            margin_l: 96,
            margin_r: 96,
            margin_v: 54,
            scaled_border_and_shadow: true,
            wrap_style: 0,
        }
    }
}

impl ConvertOptions {
    /// Script width and height, never 0.
    pub(crate) fn resolution(&self) -> (f32, f32) {
        (self.play_res_x.max(1) as f32, self.play_res_y.max(1) as f32)
    }

    /// A size given in pixels of a 1080-line picture, in script pixels.
    pub(crate) fn scale_px(&self, px: f32) -> Option<u32> {
        font_size(px * self.resolution().1 / 1080.0)
    }
}

/// A usable `\fs` value from `size`, or `None` for nonsense.
pub(crate) fn font_size(size: f32) -> Option<u32> {
    (size.is_finite() && size >= 0.5).then(|| size.round().clamp(1.0, 2000.0) as u32)
}

/// A font name that is safe in a style line and in `\fn`: no backslash, braces, commas or
/// control characters, and no surrounding quotes. `None` when nothing is left.
pub(crate) fn clean_font_name(name: &str) -> Option<String> {
    let cleaned: String = name
        .chars()
        .filter(|c| !matches!(c, '\\' | '{' | '}' | ',' | '"' | '\'') && !c.is_control())
        .collect();
    let cleaned = cleaned.trim();
    (!cleaned.is_empty()).then(|| cleaned.to_owned())
}

/// One converted cue: times in milliseconds and its text as an ASS event text.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct Cue {
    pub start_ms: u64,
    pub end_ms: u64,
    pub text: String,
}

fn timestamp(ms: u64) -> String {
    let cs = ms / 10;
    format!(
        "{}:{:02}:{:02}.{:02}",
        cs / 360_000,
        cs / 6000 % 60,
        cs / 100 % 60,
        cs % 100
    )
}

fn finite(value: f32) -> f32 {
    if value.is_finite() {
        value.max(0.0)
    } else {
        0.0
    }
}

/// The whole script: header, the `Default` style from `opts`, and `cues` in start order (cues
/// with the same start keep their input order).
pub(crate) fn write_script(opts: &ConvertOptions, mut cues: Vec<Cue>) -> String {
    cues.sort_by_key(|cue| cue.start_ms);
    let font = clean_font_name(&opts.font_name).unwrap_or_else(|| "Arial".to_owned());
    let mut out = String::with_capacity(1024 + cues.len() * 96);
    let _ = write!(
        out,
        "[Script Info]\n\
         ScriptType: v4.00+\n\
         PlayResX: {}\n\
         PlayResY: {}\n\
         WrapStyle: {}\n\
         ScaledBorderAndShadow: {}\n\
         YCbCr Matrix: None\n\
         \n\
         [V4+ Styles]\n\
         Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, \
         BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, \
         BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n\
         Style: Default,{font},{},&H{:08X},&H{:08X},&H{:08X},&H{:08X},{},0,0,0,100,100,0,0,\
         {},{},{},{},{},{},{},1\n\
         \n\
         [Events]\n\
         Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n",
        opts.play_res_x.max(1),
        opts.play_res_y.max(1),
        opts.wrap_style.min(3),
        if opts.scaled_border_and_shadow {
            "yes"
        } else {
            "no"
        },
        opts.font_size.max(1),
        opts.primary_colour,
        opts.primary_colour,
        opts.outline_colour,
        opts.back_colour,
        if opts.bold { -1 } else { 0 },
        if opts.border_style == 3 { 3 } else { 1 },
        finite(opts.outline),
        finite(opts.shadow),
        if (1..=9).contains(&opts.alignment) {
            opts.alignment
        } else {
            2
        },
        opts.margin_l,
        opts.margin_r,
        opts.margin_v,
    );
    for cue in &cues {
        let _ = writeln!(
            out,
            "Dialogue: 0,{},{},Default,,0,0,0,,{}",
            timestamp(cue.start_ms),
            timestamp(cue.end_ms),
            cue.text
        );
    }
    out
}

/// Builds one event text. Literal characters are escaped; the wanted style is remembered and
/// written as one override block right before the next visible character, so style changes that
/// no text follows cost nothing.
pub(crate) struct Line<'a> {
    opts: &'a ConvertOptions,
    out: String,
    /// What the overrides written so far add up to.
    written: Style,
    wanted: Style,
    visible: bool,
}

impl<'a> Line<'a> {
    pub fn new(opts: &'a ConvertOptions) -> Self {
        Line {
            opts,
            out: String::new(),
            written: Style::default(),
            wanted: Style::default(),
            visible: false,
        }
    }

    /// The style for the text that follows (`Style::default()` = the script's style).
    pub fn set_style(&mut self, style: Style) {
        self.wanted = style;
    }

    fn flush_style(&mut self) {
        if self.wanted == self.written {
            return;
        }
        let (old, new) = (&self.written, &self.wanted);
        let mut block = String::new();
        if old.bold != new.bold {
            let bold = new.bold.unwrap_or(self.opts.bold);
            let _ = write!(block, "\\b{}", u8::from(bold));
        }
        for (tag, before, after) in [
            ("i", old.italic, new.italic),
            ("u", old.underline, new.underline),
            ("s", old.strike, new.strike),
        ] {
            if before != after {
                let _ = write!(block, "\\{tag}{}", u8::from(after.unwrap_or(false)));
            }
        }
        // A tag without a value restores the style's value.
        if old.color != new.color {
            block.push_str("\\c");
            if let Some((r, g, b)) = new.color {
                let _ = write!(block, "&H{b:02X}{g:02X}{r:02X}&");
            }
        }
        if old.font != new.font {
            block.push_str("\\fn");
            block.push_str(new.font.as_deref().unwrap_or(""));
        }
        if old.size != new.size {
            block.push_str("\\fs");
            if let Some(size) = new.size {
                let _ = write!(block, "{size}");
            }
        }
        let _ = write!(self.out, "{{{block}}}");
        self.written = self.wanted.clone();
    }

    /// One literal character. A line break becomes `\N`; a backslash gets [`BACKSLASH_GUARD`]
    /// after it; `{` and `}` are written as libass's `\{` and `\}` escapes; other control
    /// characters are dropped.
    pub fn ch(&mut self, c: char) {
        match c {
            '\n' => self.out.push_str("\\N"),
            '\t' => self.out.push(' '),
            c if c.is_control() => {}
            c => {
                if !c.is_whitespace() {
                    self.flush_style();
                    self.visible = true;
                }
                match c {
                    '\\' => {
                        self.out.push('\\');
                        self.out.push(BACKSLASH_GUARD);
                    }
                    '{' => self.out.push_str("\\{"),
                    '}' => self.out.push_str("\\}"),
                    c => self.out.push(c),
                }
            }
        }
    }

    #[cfg(test)]
    pub fn text(&mut self, text: &str) {
        text.chars().for_each(|c| self.ch(c));
    }

    /// An override block (`{\...}`) written as it is, less any control characters.
    pub fn raw_block(&mut self, block: &str) {
        self.out.extend(block.chars().filter(|c| !c.is_control()));
    }

    /// The event text, or `None` when the cue shows nothing.
    pub fn finish(self) -> Option<String> {
        if !self.visible {
            return None;
        }
        let mut text = self.out.as_str();
        loop {
            let trimmed = text.trim_end();
            let trimmed = trimmed.strip_suffix("\\N").unwrap_or(trimmed);
            if trimmed.len() == text.len() {
                break;
            }
            text = trimmed;
        }
        Some(text.to_owned())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn header_carries_the_default_style() {
        let script = write_script(&ConvertOptions::default(), Vec::new());
        assert!(script.starts_with("[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\n"));
        assert!(script.contains("PlayResY: 1080\nWrapStyle: 0\nScaledBorderAndShadow: yes\n"));
        assert!(script.contains("YCbCr Matrix: None\n"));
        assert!(script.contains(
            "Style: Default,Arial,60,&H00FFFFFF,&H00FFFFFF,&H00000000,&H80000000,\
             0,0,0,0,100,100,0,0,1,3,1,2,96,96,54,1\n"
        ));
        assert!(script.ends_with("MarginV, Effect, Text\n"));
    }

    #[test]
    fn options_change_the_style_line_and_bad_values_fall_back() {
        let opts = ConvertOptions {
            font_name: "My, {Font}\n".to_owned(),
            bold: true,
            border_style: 3,
            outline: f32::NAN,
            shadow: 0.5,
            alignment: 42,
            ..ConvertOptions::default()
        };
        let script = write_script(&opts, Vec::new());
        assert!(script.contains("Style: Default,My Font,60,"));
        assert!(script.contains(",-1,0,0,0,100,100,0,0,3,0,0.5,2,96,96,54,1\n"));
    }

    #[test]
    fn cues_are_written_in_start_order_with_centisecond_times() {
        let cue = |start_ms, end_ms, text: &str| Cue {
            start_ms,
            end_ms,
            text: text.to_owned(),
        };
        let script = write_script(
            &ConvertOptions::default(),
            vec![
                cue(3_723_456, 3_725_009, "b"),
                cue(1000, 2000, "a"),
                cue(1000, 1500, "a2"),
            ],
        );
        let events: Vec<&str> = script
            .lines()
            .filter(|line| line.starts_with("Dialogue:"))
            .collect();
        assert_eq!(
            events,
            [
                "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,a",
                "Dialogue: 0,0:00:01.00,0:00:01.50,Default,,0,0,0,,a2",
                "Dialogue: 0,1:02:03.45,1:02:05.00,Default,,0,0,0,,b",
            ]
        );
    }

    #[test]
    fn literal_text_is_escaped() {
        let opts = ConvertOptions::default();
        let mut line = Line::new(&opts);
        line.text("a\\Nb {x} \\h\tc\u{7}\nd");
        assert_eq!(
            line.finish().unwrap(),
            "a\\\u{2060}Nb \\{x\\} \\\u{2060}h c\\Nd"
        );
    }

    #[test]
    fn style_is_written_lazily_and_restored_to_the_defaults() {
        let opts = ConvertOptions::default();
        let mut line = Line::new(&opts);
        line.set_style(Style {
            bold: Some(true),
            ..Style::default()
        });
        line.set_style(Style {
            italic: Some(true),
            color: Some((0x11, 0x22, 0x33)),
            font: Some("Verdana".to_owned()),
            size: Some(40),
            ..Style::default()
        });
        line.text("x ");
        line.set_style(Style::default());
        line.text("y");
        line.set_style(Style {
            underline: Some(true),
            ..Style::default()
        });
        assert_eq!(
            line.finish().unwrap(),
            "{\\i1\\c&H332211&\\fnVerdana\\fs40}x {\\i0\\c\\fn\\fs}y"
        );
    }

    #[test]
    fn a_line_without_visible_text_is_dropped_and_trailing_breaks_are_trimmed() {
        let opts = ConvertOptions::default();
        let mut line = Line::new(&opts);
        line.raw_block("{\\an8}");
        line.text(" \n ");
        assert_eq!(line.finish(), None);
        let mut line = Line::new(&opts);
        line.text("x \n\n ");
        assert_eq!(line.finish().unwrap(), "x");
    }
}
