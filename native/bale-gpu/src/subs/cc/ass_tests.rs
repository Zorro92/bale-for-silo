use super::*;
use crate::subs::cc::cue::Segment;
use crate::subs::convert::TextStyle;
use crate::subs::convert::ass_writer::write_script;

/// `super::events` over the cue's own time (an open cue for five seconds).
fn events(cue: &Cue, opts: &ConvertOptions, flags: u32) -> Vec<Event> {
    let end = cue.end_ms.unwrap_or(cue.start_ms + 5000);
    super::events(cue, opts, flags, cue.start_ms, end)
}

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

/// Bale.Core SubtitleTextStyle's Box at `font_size` (padding an eighth of it), no outline.
fn boxed_at(font_size: u32) -> ConvertOptions {
    ConvertOptions::styled(
        &TextStyle {
            font_size,
            primary_colour: 0x00FF_FFFF,
            outline_colour: 0,
            back_colour: 0x4000_0000,
            border_style: 4,
            outline: 0.0,
            shadow: font_size as f32 / 8.0,
            alignment: 2,
            margin_v: 54,
        },
        None,
    )
}

fn boxed() -> ConvertOptions {
    boxed_at(60)
}

fn texts(events: &[Event], layer: i32) -> Vec<&str> {
    events
        .iter()
        .filter(|event| event.layer == layer)
        .map(|event| event.text.as_str())
        .collect()
}

/// What an event puts on screen vertically: `(\an, x, centre)` and, for a box event, the height of
/// its line (the drawing's).
struct Placed {
    alignment: u32,
    x: f32,
    centre: f32,
    height: Option<f32>,
}

fn placed(text: &str) -> Placed {
    let number = |after: &str, until: char| -> f32 {
        let start = text.find(after).expect(after) + after.len();
        let rest = &text[start..];
        rest[..rest.find(until).expect("end")]
            .parse()
            .expect("number")
    };
    let position = &text[text.find("\\pos(").expect("pos") + 5..];
    let (x, rest) = position.split_once(',').expect("x");
    let centre = &rest[..rest.find(')').expect(")")];
    Placed {
        alignment: number("\\an", '\\') as u32,
        x: x.parse().expect("x"),
        centre: centre.parse().expect("y"),
        height: text.contains("\\p1}").then(|| number("m 0 0 n 0 ", '{')),
    }
}

/// The bands the box events cover, `(top, bottom)` in script pixels, in row order.
fn box_bands(events: &[Event]) -> Vec<(f32, f32)> {
    texts(events, BOX_LAYER)
        .into_iter()
        .map(|text| {
            let placed = placed(text);
            let height = placed.height.expect("a box event's line height");
            (placed.centre - height / 2.0, placed.centre + height / 2.0)
        })
        .collect()
}

#[test]
fn rows_sit_on_their_bands_and_each_row_is_its_own_event() {
    let opts = ConvertOptions::default();
    // Rows 14 and 15 start in column 0 (left-aligned); row 1 is separate. The default text (60
    // script pixels) is taller than a 608 row (57.6), so rows are 60 apart: 14 and 15 from the
    // bottom of the safe area (972), row 1 from its top (108).
    let cue = cue(
        CueKind::RollUp,
        vec![
            plain_row(1, 4, "top"),
            plain_row(14, 0, "one"),
            plain_row(15, 0, "two"),
        ],
    );
    let events = events(&cue, &opts, 0);
    assert!(events.iter().all(|event| event.layer == TEXT_LAYER));
    assert_eq!(
        texts(&events, TEXT_LAYER),
        [
            // x = 1920 * (0.1 + 0.8 * 4 / 32) = 384; row 1 is 108..168.
            "{\\an4\\pos(384,138)}top",
            // Row 14 is 852..912, row 15 912..972.
            "{\\an4\\pos(192,882)}one",
            "{\\an4\\pos(192,942)}two",
        ]
    );
}

#[test]
fn small_text_keeps_608s_own_rows_in_whole_pixels() {
    // 42 script pixels fit a 608 row: the bands are the grid's rows, rounded (row r ends at
    // 1080 * (0.1 + 0.8 * r / 15)), whichever half a row is in.
    let opts = ConvertOptions {
        font_size: 42,
        ..ConvertOptions::default()
    };
    let cue = cue(
        CueKind::RollUp,
        vec![
            plain_row(1, 0, "a"),
            plain_row(7, 0, "b"),
            plain_row(9, 0, "c"),
            plain_row(14, 0, "d"),
            plain_row(15, 0, "e"),
        ],
    );
    let centres: Vec<f32> = texts(&events(&cue, &opts, 0), TEXT_LAYER)
        .into_iter()
        .map(|text| placed(text).centre)
        .collect();
    let grid = |r: f32| (1080.0 * (0.1 + 0.8 * r / 15.0)).round();
    let want: Vec<f32> = [1.0, 7.0, 9.0, 14.0, 15.0]
        .iter()
        .map(|&r| (grid(r - 1.0) + grid(r)) / 2.0)
        .collect();
    assert_eq!(centres, want);
    assert_eq!(
        want,
        [
            (108.0 + 166.0) / 2.0,
            (454.0 + 511.0) / 2.0,
            (569.0 + 626.0) / 2.0,
            (857.0 + 914.0) / 2.0,
            (914.0 + 972.0) / 2.0
        ]
    );
}

#[test]
fn adjacent_rows_with_different_indents_have_abutting_boxes_at_every_size() {
    // The console case: an indented row next to rows that start elsewhere, which used to give two
    // libass boxes taller than the row pitch that overlapped (a double-alpha band).
    for size in [42, 51, 60, 74, 92] {
        let opts = boxed_at(size);
        let cue = cue(
            CueKind::RollUp,
            vec![
                plain_row(11, 0, "PAC green blue cyan"),
                plain_row(12, 8, "indent 8"),
                plain_row(13, 0, "underline italic+ul plain"),
                plain_row(14, 1, "flashing text"),
                plain_row(15, 0, "608 A - colors, ul, flash"),
            ],
        );
        let events = events(&cue, &opts, 0);
        // Every box under every text.
        let layers: Vec<i32> = events.iter().map(|event| event.layer).collect();
        assert_eq!(layers, [0, 0, 0, 0, 0, 1, 1, 1, 1, 1], "{size}");
        let bands = box_bands(&events);
        assert_eq!(bands.len(), 5);
        for pair in bands.windows(2) {
            // No gap and no overlap: one row's background ends where the next begins.
            assert_eq!(pair[0].1, pair[1].0, "{size}: {bands:?}");
        }
        for &(top, bottom) in &bands {
            assert_eq!(top.fract(), 0.0, "whole pixels");
            assert_eq!(bottom.fract(), 0.0, "whole pixels");
            // A band is as tall as the text it holds, and a 608 row at least.
            assert!(bottom - top >= (size as f32).min(57.0), "{size}: {bands:?}");
            assert!(bottom - top <= (size as f32).max(58.0), "{size}: {bands:?}");
        }
        // The last row ends on the safe area's bottom; the text is centred on its band, at the
        // box's x and alignment, so the box is as wide as the text.
        assert_eq!(bands[4].1, 972.0, "{size}");
        for (text, band) in texts(&events, TEXT_LAYER).into_iter().zip(&bands) {
            let placed = placed(text);
            assert_eq!(placed.centre, (band.0 + band.1) / 2.0);
            assert_eq!(placed.alignment, 4);
        }
        for (boxed, text) in texts(&events, BOX_LAYER)
            .into_iter()
            .zip(texts(&events, TEXT_LAYER))
        {
            assert_eq!(placed(boxed).x, placed(text).x);
        }
    }
}

#[test]
fn large_text_grows_blocks_towards_the_middle_and_keeps_them_apart() {
    let opts = boxed_at(92);
    let caption = cue(
        CueKind::PopOn,
        vec![
            plain_row(1, 0, "one"),
            plain_row(2, 0, "two"),
            plain_row(7, 0, "seven"),
            plain_row(8, 0, "eight"),
            plain_row(14, 0, "fourteen"),
            plain_row(15, 0, "fifteen"),
        ],
    );
    assert_eq!(
        box_bands(&events(&caption, &opts, 0)),
        [
            // Rows 1-2 keep their top on row 1's line (108) and grow down, 92 apart.
            (108.0, 200.0),
            (200.0, 292.0),
            // Rows 7-8 reach the lower half: their bottom stays on row 8's line (568.8).
            (385.0, 477.0),
            (477.0, 569.0),
            // Rows 14-15 end on the safe area's bottom (972).
            (788.0, 880.0),
            (880.0, 972.0),
        ]
    );
    // Rows 12-13 grown up from row 13's line (856.8) would cover row 10's line (684): row 10
    // moves up to meet them instead of overlapping.
    let apart = cue(
        CueKind::PopOn,
        vec![
            plain_row(10, 0, "ten"),
            plain_row(12, 0, "twelve"),
            plain_row(13, 0, "thirteen"),
        ],
    );
    assert_eq!(
        box_bands(&events(&apart, &opts, 0)),
        [(581.0, 673.0), (673.0, 765.0), (765.0, 857.0)]
    );
    // A block taller than the room above it moves down into the picture.
    let mut huge = boxed();
    huge.font_size = 400;
    let tall = cue(
        CueKind::RollUp,
        vec![
            plain_row(13, 0, "a"),
            plain_row(14, 0, "b"),
            plain_row(15, 0, "c"),
        ],
    );
    assert_eq!(
        box_bands(&events(&tall, &huge, 0)),
        [(0.0, 400.0), (400.0, 800.0), (800.0, 1200.0)]
    );
}

#[test]
fn pop_on_alignment_follows_media3() {
    let opts = ConvertOptions::default();
    // Centred: 11 columns either side of a 10-column row.
    let centred = cue(CueKind::PopOn, vec![plain_row(15, 11, "0123456789")]);
    assert_eq!(
        texts(&events(&centred, &opts, 0), TEXT_LAYER),
        ["{\\an5\\pos(960,942)}0123456789"]
    );
    // Right: column 20 to 30 (less space after than before).
    let right = cue(CueKind::PopOn, vec![plain_row(15, 20, "abcdefghijk")]);
    // x = 1920 * (0.1 + 0.8 * 31 / 32) = 1680.
    assert_eq!(
        texts(&events(&right, &opts, 0), TEXT_LAYER),
        ["{\\an6\\pos(1680,942)}abcdefghijk"]
    );
    // Every row takes the first alignment any row needs: one left row makes both left.
    let mixed = cue(
        CueKind::PopOn,
        vec![plain_row(14, 11, "0123456789"), plain_row(15, 2, "abc")],
    );
    assert_eq!(
        texts(&events(&mixed, &opts, 0), TEXT_LAYER),
        [
            "{\\an4\\pos(720,882)}0123456789",
            "{\\an4\\pos(288,942)}abc"
        ]
    );
    // A row wider than the grid allows is centred.
    let wide = cue(CueKind::PopOn, vec![plain_row(15, 0, &"w".repeat(32))]);
    assert!(
        events(&wide, &opts, 0)[0]
            .text
            .starts_with("{\\an5\\pos(960,942)}")
    );
    // Roll-up and paint-on never centre; a lone row 8 keeps its bottom on its 608 line (568.8).
    let paint = cue(CueKind::PaintOn, vec![plain_row(8, 11, "0123456789")]);
    assert_eq!(
        texts(&events(&paint, &opts, 0), TEXT_LAYER),
        ["{\\an4\\pos(720,539)}0123456789"]
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
    let styled = "a {\\c&H00FFFF&}yellow {\\i1\\c}it{\\i0\\u1\\c&H0000FF&}ul{\\u0\\c}k";
    assert_eq!(
        texts(&events(&cue, &opts, 0), TEXT_LAYER),
        [format!("{{\\an4\\pos(192,942)}}{styled}")]
    );
    assert_eq!(
        texts(&events(&cue, &opts, FLAG_PLAIN), TEXT_LAYER),
        ["{\\an4\\pos(192,942)}a yellow itulk"]
    );
    // The box event writes the same runs (italics change advances), flattened and transparent.
    assert_eq!(
        texts(&events(&cue, &boxed(), 0), BOX_LAYER),
        [format!(
            "{{\\rBox\\an4\\pos(192,942)\\fscy0.001\\xshad7.5}}{styled}{{\\fscy100\\p1}}m 0 0 n 0 60{{\\p0}}"
        )]
    );
}

#[test]
fn a_608_background_colours_its_rows_box() {
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
    let drawn = events(&cue, &opts, 0);
    let height = "{\\fscy100\\p1}m 0 0 n 0 60{\\p0}";
    assert_eq!(
        texts(&drawn, BOX_LAYER),
        [
            format!(
                "{{\\rBox\\an4\\pos(192,762)\\fscy0.001\\xshad7.5\\4c&H000000&\\4a&HFF&}}clear{height}"
            ),
            format!(
                "{{\\rBox\\an4\\pos(192,822)\\fscy0.001\\xshad7.5\\4c&HFFFFFF&\\4a&H00&}}{{\\c&H000000&}}black on white{height}"
            ),
            format!(
                "{{\\rBox\\an4\\pos(192,882)\\fscy0.001\\xshad7.5\\4c&H0000FF&\\4a&H80&}}semi red{height}"
            ),
            format!("{{\\rBox\\an4\\pos(192,942)\\fscy0.001\\xshad7.5}}default{height}"),
        ]
    );
    // The text draws no box of its own; black text on a coloured box stays black.
    assert_eq!(
        texts(&drawn, TEXT_LAYER),
        [
            "{\\an4\\pos(192,762)}clear",
            "{\\an4\\pos(192,822)}{\\c&H000000&}black on white",
            "{\\an4\\pos(192,882)}semi red",
            "{\\an4\\pos(192,942)}default",
        ]
    );
    // Without the box style there are no box events and black text takes the text colour; plain
    // keeps the appearance's box but no 608 background or colour.
    let unboxed = events(&cue, &ConvertOptions::default(), 0);
    assert!(texts(&unboxed, BOX_LAYER).is_empty());
    assert_eq!(
        texts(&unboxed, TEXT_LAYER),
        [
            "{\\an4\\pos(192,762)}clear",
            "{\\an4\\pos(192,822)}black on white",
            "{\\an4\\pos(192,882)}semi red",
            "{\\an4\\pos(192,942)}default",
        ]
    );
    let plain = events(&cue, &opts, FLAG_PLAIN);
    assert_eq!(texts(&plain, TEXT_LAYER), texts(&unboxed, TEXT_LAYER));
    assert_eq!(texts(&plain, BOX_LAYER).len(), 4);
    assert!(
        texts(&plain, BOX_LAYER)
            .iter()
            .all(|text| !text.contains("\\4c") && !text.contains("\\c&"))
    );
}

#[test]
fn the_box_pads_by_the_appearance_and_the_outline() {
    let mut opts = boxed_at(51);
    opts.outline = 3.0;
    let cue = cue(CueKind::RollUp, vec![plain_row(15, 0, "x")]);
    // 51 / 8 = 6.375, and the 3-pixel outline the appearance's box took in.
    assert!(texts(&events(&cue, &opts, 0), BOX_LAYER)[0].contains("\\xshad9.375}"));
}

#[test]
fn the_header_has_the_text_style_and_with_a_box_the_box_style() {
    // No box: the appearance as it is, rows never wrapping.
    let plain = header(&ConvertOptions::default());
    assert!(plain.contains("WrapStyle: 2\n"));
    assert!(plain.contains(
        "Style: Default,Arial,60,&H00FFFFFF,&H00FFFFFF,&H00000000,&H80000000,\
         0,0,0,0,100,100,0,0,1,3,1,2,96,96,54,1\n"
    ));
    assert!(!plain.contains("Style: Box"));
    assert!(plain.ends_with("Effect, Text\n"));
    // A box: the text without it (border style 1, no shadow), and the box style.
    let with_box = header(&boxed());
    assert!(with_box.contains(
        "Style: Default,Arial,60,&H00FFFFFF,&H00FFFFFF,&H00000000,&H40000000,\
         0,0,0,0,100,100,0,0,1,0,0,2,96,96,54,1\n"
    ));
    assert!(with_box.contains(
        "Style: Box,Arial,60,&HFF000000,&HFF000000,&HFF000000,&H40000000,\
         0,0,0,0,100,100,0,0,4,0,0,2,96,96,54,1\n"
    ));
    assert_eq!(
        header(&ConvertOptions::default()),
        write_script(&text_options(&ConvertOptions::default()), Vec::new())
    );
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
    let events = events(&cue, &opts, 0);
    assert_eq!(
        texts(&events, TEXT_LAYER),
        ["{\\an4\\pos(192,942)}\\{x\\} \\\u{2060}N"]
    );
    let whole = script(std::slice::from_ref(&cue), &opts, 0, 5000);
    assert!(whole.starts_with("[Script Info]\n"));
    assert!(whole.ends_with(&format!(
        "Dialogue: 1,0:00:01.23,0:00:06.23,Default,,0,0,0,,{}\n",
        events[0].text
    )));
    // Boxes go on layer 0, before the text.
    let boxed_script = script(&[cue], &boxed(), 0, 5000);
    let lines = script_lines(&boxed_script);
    assert_eq!(lines.len(), 2);
    assert!(lines[0].starts_with("Dialogue: 0,0:00:01.23,0:00:06.23,Default,,0,0,0,,{\\rBox"));
    assert!(lines[1].starts_with("Dialogue: 1,"));
}

fn script_lines(script: &str) -> Vec<&str> {
    script
        .lines()
        .filter(|line| line.starts_with("Dialogue:"))
        .collect()
}

/// A row of "now " and " off" around flashing "flash".
fn flashing_row() -> CueRow {
    let flash = CueStyle {
        flash: true,
        ..CueStyle::default()
    };
    row(
        15,
        0,
        &[
            ("now ", CueStyle::default()),
            ("flash", flash),
            (" off", CueStyle::default()),
        ],
    )
}

#[test]
fn flashing_text_blinks_every_half_second_of_the_media_clock() {
    let opts = ConvertOptions::default();
    let cue = cue(CueKind::PopOn, vec![flashing_row()]);
    // The default style: text and outline opaque, the shadow at 0x80.
    let on = "\\1a&H00&\\3a&H00&\\4a&H80&";
    let off = "\\1a&HFF&\\3a&HFF&\\4a&HFF&";
    // From 1000 (an even half second: shown) to 3200: steps at 1500 (gone), 2000 (back), 2500,
    // 3000, written after the event's start.
    let drawn = super::events(&cue, &opts, 0, 1000, 3200);
    assert_eq!(drawn.len(), 1);
    assert!(drawn[0].flashes);
    assert_eq!(
        drawn[0].text,
        format!(
            "{{\\an4\\pos(192,942)}}now {{\\t(500,500,{off})\\t(1000,1000,{on})\\t(1500,1500,{off})\
             \\t(2000,2000,{on})}}flash{{{on}}} off"
        )
    );
    // A stretch that starts in an odd half second starts hidden, and its first step is where the
    // half second ends, never at 0 (libass runs a \t from 0 over the whole event).
    let late = super::events(&cue, &opts, 0, 1700, 2600);
    assert_eq!(
        late[0].text,
        format!(
            "{{\\an4\\pos(192,942)}}now {{{off}\\t(300,300,{on})\\t(800,800,{off})}}flash{{{on}}} off"
        )
    );
    // The overlay sleeps between steps instead of rendering every frame.
    assert_eq!(
        crate::subs::schedule::motion(drawn[0].text.as_bytes(), b""),
        crate::subs::schedule::Motion::Steps(vec![500, 1000, 1500, 2000])
    );
    let exact = super::events(&cue, &opts, 0, 2000, 2400);
    assert!(exact[0].text.contains("now {}flash"), "{}", exact[0].text);
    // Plain draws it steady, as Media3 and FFmpeg do.
    let plain = super::events(&cue, &opts, FLAG_PLAIN, 1000, 3200);
    assert_eq!(plain[0].text, "{\\an4\\pos(192,942)}now flash off");
    assert!(!plain[0].flashes);
}

#[test]
fn only_the_characters_blink_and_their_background_stays() {
    let mut opts = boxed();
    // Text at 80% (alpha 0x33) with an outline: the restore brings back the appearance's alphas.
    opts.primary_colour = 0x33FF_FFFF;
    let cue = cue(CueKind::RollUp, vec![flashing_row()]);
    let drawn = super::events(&cue, &opts, 0, 1000, 2000);
    let boxes = texts(&drawn, BOX_LAYER);
    assert_eq!(boxes.len(), 1);
    assert!(!boxes[0].contains("\\t("), "{}", boxes[0]);
    assert!(
        drawn
            .iter()
            .filter(|event| event.layer == BOX_LAYER)
            .all(|event| !event.flashes)
    );
    let text = texts(&drawn, TEXT_LAYER)[0];
    assert!(
        text.contains(
            "\\t(500,500,\\1a&HFF&\\3a&HFF&\\4a&HFF&)}flash{\\1a&H33&\\3a&H00&\\4a&H40&} off"
        ),
        "{text}"
    );
    // A caption without flashing text has no steps at all.
    let steady = cue_with(vec![plain_row(15, 0, "steady")]);
    let drawn = super::events(&steady, &opts, 0, 1000, 60_000);
    assert!(
        drawn
            .iter()
            .all(|event| !event.flashes && !event.text.contains("\\t("))
    );
}

fn cue_with(rows: Vec<CueRow>) -> Cue {
    cue(CueKind::PopOn, rows)
}
