//! SubRip (`.srt`) to ASS, modelled on Media3's `SubripParser` with FFmpeg's extras (`<font>`,
//! `<s>`, any `{\...}` override block).
//!
//! - A cue starts at a timing line; the line before it (the index) is never needed. Its text
//!   runs to the next blank line or the next timing line.
//! - `<b> <i> <u> <s>` and `<font color face size>` become overrides; `<br>` breaks the line;
//!   any other tag is dropped and its text kept. `<font size>` is read as pixels of a 1080-line
//!   picture.
//! - `{\...}` blocks (`{\an8}`, `{\pos(...)}`, ...) go to libass unchanged. Every other `{`, `}`
//!   and backslash is literal text and is escaped.

use super::ass_writer::{ConvertOptions, Cue, Line, clean_font_name, write_script};
use super::style::{Style, decode_entity, normalize, parse_color, parse_timing};

/// Converts SubRip text to an ASS script. Cues that cannot be read are skipped.
pub fn srt_to_ass(text: &str, opts: &ConvertOptions) -> String {
    let text = normalize(text);
    let lines: Vec<&str> = text.split('\n').collect();
    let mut cues = Vec::new();
    let mut at = 0;
    while at < lines.len() {
        let Some((start_ms, end_ms, _coordinates)) = parse_timing(lines[at]) else {
            at += 1;
            continue;
        };
        at += 1;
        let first = at;
        while at < lines.len() && !lines[at].trim().is_empty() && parse_timing(lines[at]).is_none()
        {
            at += 1;
        }
        let mut body = &lines[first..at];
        // No blank line before the next cue: its index line is not text.
        if at < lines.len()
            && !lines[at].trim().is_empty()
            && body.last().is_some_and(|line| is_index(line))
        {
            body = &body[..body.len() - 1];
        }
        if end_ms < start_ms {
            continue;
        }
        if let Some(text) = cue_text(body.join("\n").trim(), opts) {
            cues.push(Cue {
                start_ms,
                end_ms,
                text,
            });
        }
    }
    write_script(opts, cues)
}

fn is_index(line: &str) -> bool {
    let line = line.trim();
    !line.is_empty() && line.bytes().all(|b| b.is_ascii_digit())
}

#[derive(Clone, Copy, PartialEq)]
enum Kind {
    Bold,
    Italic,
    Underline,
    Strike,
    Font,
}

/// The tag between `<` and `>`: whether it closes, its lower-case name and its attributes.
/// `None` when it does not look like a tag (`a < b`, `<3`).
fn parse_tag(inner: &str) -> Option<(bool, String, &str)> {
    let (close, inner) = match inner.strip_prefix('/') {
        Some(rest) => (true, rest),
        None => (false, inner),
    };
    let name_len = inner
        .find(|c: char| !c.is_ascii_alphanumeric())
        .unwrap_or(inner.len());
    let (name, rest) = inner.split_at(name_len);
    let starts_with_letter = name.starts_with(|c: char| c.is_ascii_alphabetic());
    let ends_cleanly = rest.is_empty() || rest.starts_with(|c: char| c.is_whitespace() || c == '/');
    (starts_with_letter && ends_cleanly).then(|| (close, name.to_ascii_lowercase(), rest))
}

/// `name=value` pairs of a tag; values may be quoted with `"` or `'`.
fn attributes(mut rest: &str) -> Vec<(String, &str)> {
    let mut found = Vec::new();
    loop {
        rest = rest.trim_start();
        let name_len = rest
            .find(|c: char| c == '=' || c.is_whitespace())
            .unwrap_or(rest.len());
        if name_len == 0 {
            // Stray `=` or the end.
            match rest.strip_prefix('=') {
                Some(after) => rest = after,
                None => return found,
            }
            continue;
        }
        let name = rest[..name_len].to_ascii_lowercase();
        rest = rest[name_len..].trim_start();
        let Some(after) = rest.strip_prefix('=') else {
            continue;
        };
        rest = after.trim_start();
        let value;
        if let Some(quote) = rest.chars().next().filter(|c| matches!(c, '"' | '\'')) {
            let quoted = &rest[1..];
            let end = quoted.find(quote).unwrap_or(quoted.len());
            value = &quoted[..end];
            rest = quoted.get(end + 1..).unwrap_or("");
        } else {
            let end = rest.find(char::is_whitespace).unwrap_or(rest.len());
            value = &rest[..end];
            rest = &rest[end..];
        }
        found.push((name, value));
    }
}

fn font_style(attrs: &str, opts: &ConvertOptions) -> Style {
    let mut style = Style::default();
    for (name, value) in attributes(attrs) {
        match name.as_str() {
            "color" => style.color = parse_color(value).or(style.color),
            "face" => style.font = clean_font_name(value).or(style.font),
            "size" => {
                let pixels = value.trim().trim_end_matches("px").parse::<f32>().ok();
                style.size = pixels.and_then(|px| opts.scale_px(px)).or(style.size);
            }
            _ => {}
        }
    }
    style
}

/// One cue's text as an ASS event text; `None` when it shows nothing.
fn cue_text(body: &str, opts: &ConvertOptions) -> Option<String> {
    let mut line = Line::new(opts);
    let mut open: Vec<(Kind, Style)> = Vec::new();
    let mut rest = body;
    while let Some(c) = rest.chars().next() {
        match c {
            '<' => {
                let tag = rest[1..]
                    .find(['>', '<', '\n'])
                    .filter(|&end| rest.as_bytes()[end + 1] == b'>')
                    .and_then(|end| Some((parse_tag(&rest[1..=end])?, end)));
                if let Some(((close, name, attrs), end)) = tag {
                    rest = &rest[end + 2..];
                    let kind = match name.as_str() {
                        "b" => Kind::Bold,
                        "i" => Kind::Italic,
                        "u" => Kind::Underline,
                        "s" | "strike" => Kind::Strike,
                        "font" => Kind::Font,
                        "br" => {
                            line.ch('\n');
                            continue;
                        }
                        _ => continue,
                    };
                    if close {
                        // The innermost open tag of this kind; closing restores what was
                        // there before it.
                        if let Some(at) = open.iter().rposition(|(open, _)| *open == kind) {
                            open.remove(at);
                        }
                    } else {
                        let on = Some(true);
                        let style = match kind {
                            Kind::Bold => Style {
                                bold: on,
                                ..Style::default()
                            },
                            Kind::Italic => Style {
                                italic: on,
                                ..Style::default()
                            },
                            Kind::Underline => Style {
                                underline: on,
                                ..Style::default()
                            },
                            Kind::Strike => Style {
                                strike: on,
                                ..Style::default()
                            },
                            Kind::Font => font_style(attrs, opts),
                        };
                        open.push((kind, style));
                    }
                    let mut style = Style::default();
                    open.iter().for_each(|(_, over)| style.apply(over));
                    line.set_style(style);
                    continue;
                }
            }
            '{' if rest[1..].starts_with('\\') => {
                let end = rest.find(['}', '\n']);
                if let Some(end) = end.filter(|&end| rest.as_bytes()[end] == b'}') {
                    line.raw_block(&rest[..=end]);
                    rest = &rest[end + 1..];
                    continue;
                }
            }
            '&' => {
                if let Some((decoded, len)) = decode_entity(rest) {
                    line.ch(decoded);
                    rest = &rest[len..];
                    continue;
                }
            }
            _ => {}
        }
        line.ch(c);
        rest = &rest[c.len_utf8()..];
    }
    line.finish()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The `(start, end, text)` of every event in the converted script.
    fn events(srt: &str) -> Vec<(String, String, String)> {
        srt_to_ass(srt, &ConvertOptions::default())
            .lines()
            .filter_map(|line| line.strip_prefix("Dialogue: 0,"))
            .map(|event| {
                let mut fields = event.splitn(9, ',');
                let start = fields.next().unwrap().to_owned();
                let end = fields.next().unwrap().to_owned();
                (start, end, fields.nth(6).unwrap().to_owned())
            })
            .collect()
    }

    fn texts(srt: &str) -> Vec<String> {
        events(srt).into_iter().map(|event| event.2).collect()
    }

    fn one(body: &str) -> String {
        let mut texts = texts(&format!("1\n00:00:01,000 --> 00:00:02,000\n{body}\n"));
        assert_eq!(texts.len(), 1, "{body}");
        texts.remove(0)
    }

    #[test]
    fn plain_cues_convert() {
        let srt = "1\n00:00:01,000 --> 00:00:02,500\nHello\nworld\n\n\
                   2\n01:02:03,456 --> 01:02:04,000\nSecond\n";
        assert_eq!(
            events(srt),
            [
                (
                    "0:00:01.00".to_owned(),
                    "0:00:02.50".to_owned(),
                    "Hello\\Nworld".to_owned()
                ),
                (
                    "1:02:03.45".to_owned(),
                    "1:02:04.00".to_owned(),
                    "Second".to_owned()
                ),
            ]
        );
    }

    #[test]
    fn bom_and_every_line_ending_are_accepted() {
        for newline in ["\r\n", "\n", "\r"] {
            let srt = "\u{feff}1\n00:00:01,000 --> 00:00:02,000\na\nb\n\n\n\n2\n\
                       00:00:03,000 --> 00:00:04,000\nc\n\n"
                .replace('\n', newline);
            assert_eq!(texts(&srt), ["a\\Nb", "c"], "{newline:?}");
        }
    }

    #[test]
    fn timing_variants_are_accepted() {
        let srt = "00:00:01.000 --> 00:00:02.000\ndot\n\n\
                   x\n00:03,5 --> 00:04,50\nno hours\n\n\
                   3\n100:00:05,000 --> 100:00:06,000  X1:10 X2:600 Y1:20 Y2:80\ncoordinates\n\n\
                   4\n  0:0:7,0-->0:0:8,0\ntight\n";
        assert_eq!(
            events(srt)
                .iter()
                .map(|(start, end, text)| (start.as_str(), end.as_str(), text.as_str()))
                .collect::<Vec<_>>(),
            [
                ("0:00:01.00", "0:00:02.00", "dot"),
                ("0:00:03.00", "0:00:04.05", "no hours"),
                ("0:00:07.00", "0:00:08.00", "tight"),
                ("100:00:05.00", "100:00:06.00", "coordinates"),
            ]
        );
    }

    #[test]
    fn malformed_blocks_are_skipped() {
        let srt = "1\n00:00:01,000 --> oops\nbad end\n\n\
                   2\n00:00:05,000 --> 00:00:04,000\nends before it starts\n\n\
                   3\nno timing at all\n\n\
                   4\n00:00:06,000 --> 00:00:07,000\n\n\
                   5\n00:00:08,000 --> 00:00:09,000\n<i></i>  \n\n\
                   6\n00:00:10,000 --> 00:00:11,000\ngood\n\n\
                   trailing garbage --> here";
        assert_eq!(texts(srt), ["good"]);
    }

    #[test]
    fn a_missing_blank_line_does_not_swallow_the_next_cue() {
        let srt = "1\n00:00:01,000 --> 00:00:02,000\none\n2\n\
                   00:00:03,000 --> 00:00:04,000\ntwo\n00:00:05,000 --> 00:00:06,000\n42\n";
        assert_eq!(texts(srt), ["one", "two", "42"]);
    }

    #[test]
    fn basic_tags_become_overrides() {
        assert_eq!(one("<b>bold</b> plain"), "{\\b1}bold {\\b0}plain");
        assert_eq!(one("<I>it</I>"), "{\\i1}it");
        assert_eq!(one("<u>un</u>x"), "{\\u1}un{\\u0}x");
        assert_eq!(one("<s>gone</s>x"), "{\\s1}gone{\\s0}x");
        assert_eq!(one("<strike>gone</strike>x"), "{\\s1}gone{\\s0}x");
    }

    #[test]
    fn nested_and_unclosed_tags_keep_their_state() {
        assert_eq!(one("<b>a<i>b</b>c</i>d"), "{\\b1}a{\\i1}b{\\b0}c{\\i0}d");
        assert_eq!(one("<b>a<b>b</b>c</b>d"), "{\\b1}abc{\\b0}d");
        assert_eq!(one("<i>never closed\nnext"), "{\\i1}never closed\\Nnext");
        assert_eq!(one("stray</i> close"), "stray close");
    }

    #[test]
    fn font_tags_set_colour_face_and_size_and_restore_them() {
        assert_eq!(
            one("<font color=\"#FF8000\">a</font>b"),
            "{\\c&H0080FF&}a{\\c}b"
        );
        assert_eq!(one("<font color=red>a"), "{\\c&H0000FF&}a");
        assert_eq!(one("<FONT COLOR='#abc'>a"), "{\\c&HCCBBAA&}a");
        assert_eq!(one("<font color=00ff00>a"), "{\\c&H00FF00&}a");
        assert_eq!(
            one("<font face=\"Comic Sans MS\" size=\"40\">a</font>b"),
            "{\\fnComic Sans MS\\fs40}a{\\fn\\fs}b"
        );
        assert_eq!(
            one("<font color=\"yellow\">a<font color=\"blue\" size=30>b</font>c</font>d"),
            "{\\c&H00FFFF&}a{\\c&HFF0000&\\fs30}b{\\c&H00FFFF&\\fs}c{\\c}d"
        );
        // Nothing usable in the tag: no override at all.
        assert_eq!(one("<font color=\"nope\" size=x face=\"{}\">a</font>"), "a");
    }

    #[test]
    fn font_size_scales_with_the_script_height() {
        let opts = ConvertOptions {
            play_res_y: 540,
            ..ConvertOptions::default()
        };
        let script = srt_to_ass("00:00:01,000 --> 00:00:02,000\n<font size=40>a\n", &opts);
        assert!(script.ends_with(",,{\\fs20}a\n"));
    }

    #[test]
    fn unknown_tags_are_dropped_and_non_tags_are_kept() {
        assert_eq!(one("<span class=\"x\">a</span> <br/>b<br>c"), "a \\Nb\\Nc");
        assert_eq!(
            one("1 < 2 and 3 > 2, <3 <<b>x"),
            "1 < 2 and 3 > 2, <3 <{\\b1}x"
        );
        assert_eq!(one("a <b\nc"), "a <b\\Nc");
    }

    #[test]
    fn entities_decode_to_literal_text() {
        assert_eq!(
            one("&lt;b&gt; &amp;amp; &quot;q&quot; &apos;a&apos; x&nbsp;y &#65;&#x42; &bogus; &"),
            "<b> &amp; \"q\" 'a' x\u{a0}y AB &bogus; &"
        );
        assert_eq!(one("&#123;\\b1&#125;x"), "\\{\\\u{2060}b1\\}x");
        assert_eq!(one("a&lrm;&rlm;b"), "a\u{200e}\u{200f}b");
    }

    #[test]
    fn backslashes_and_braces_are_escaped() {
        assert_eq!(
            one("C:\\New\\nope\\here"),
            "C:\\\u{2060}New\\\u{2060}nope\\\u{2060}here"
        );
        assert_eq!(one("a {note} b"), "a \\{note\\} b");
        assert_eq!(one("} {"), "\\} \\{");
        assert_eq!(one("x {\\unclosed"), "x \\{\\\u{2060}unclosed");
        assert_eq!(
            one("x {\\split\nover} lines"),
            "x \\{\\\u{2060}split\\Nover\\} lines"
        );
    }

    #[test]
    fn override_blocks_pass_through() {
        assert_eq!(one("{\\an8}Top"), "{\\an8}Top");
        assert_eq!(
            one("{\\an1}<i>left</i> {\\pos(10,20)\\fad(100,100)}x"),
            "{\\an1}{\\i1}left {\\pos(10,20)\\fad(100,100)}{\\i0}x"
        );
        assert_eq!(texts("00:00:01,000 --> 00:00:02,000\n{\\an8}\n"), [""; 0]);
    }

    #[test]
    fn hostile_input_never_panics() {
        let pieces = [
            "<",
            ">",
            "</",
            "<b",
            "<font",
            "<font color=",
            "=\"",
            "'",
            "{",
            "{\\",
            "}",
            "\\",
            "&",
            "&#",
            "&#x;",
            ";",
            "-->",
            "00:00:01,000",
            ":",
            ",",
            "\n",
            "\r",
            " ",
            "é",
            "😀",
            "\0",
            "\u{feff}",
            "9",
        ];
        let mut seed = 0x2545_F491u32;
        for _ in 0..3000 {
            let mut input = String::new();
            for _ in 0..12 {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                input.push_str(pieces[seed as usize % pieces.len()]);
            }
            let with_cue = format!("1\n00:00:01,000 --> 00:00:02,000\n{input}\n");
            for text in [input.as_str(), with_cue.as_str()] {
                let script = srt_to_ass(text, &ConvertOptions::default());
                assert!(script.lines().all(|line| !line.contains('\0')));
            }
        }
    }
}
