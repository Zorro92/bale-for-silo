//! WebVTT to ASS.
//!
//! - `NOTE` and `REGION` blocks are skipped; `STYLE` blocks are read for a small CSS subset:
//!   the selectors `::cue`, `::cue(.class)`, `::cue(tag)`, `::cue(tag.class)` (matched by the
//!   class alone), `::cue(v)` and `::cue(v[voice="Name"])`, with `color`, `font-weight`,
//!   `font-style`, `text-decoration`, `font-family` (first family) and `font-size` (px of a
//!   1080-line picture, em, %). Rules apply in tag, class, voice order; other selectors and
//!   properties are ignored.
//! - The spec's colour classes (`.white .lime .cyan .red .yellow .magenta .blue .black`) colour
//!   the text; the `bg_*` classes are ignored.
//! - Cue settings: `align` gives the horizontal part of `\an`. Without `line` and `position` the
//!   cue sits at the bottom like the default style. `line:-1` is the bottom, other negative
//!   numbers count lines up from it, `line:N` counts lines down from the top, `line:NN%` gives
//!   `\pos` at that height (anchored by the line alignment, top by default). `position:NN%`
//!   gives the horizontal `\pos`, anchored by its own anchor or else by `align`. A positioned
//!   cue is moved back inside the picture by its explicit line count. `size` is ignored.
//! - TODO: `vertical:rl|lr` cues are drawn horizontally.
//! - `X-TIMESTAMP-MAP` (HLS) is read with the header and ignored: cue times are used as written.
//! - `<b> <i> <u> <c.class> <v Name>` are styled, `<lang>` is ignored, `<ruby>base<rt>reading`
//!   becomes `base(reading)`, inline timestamps and unknown tags are dropped, text is kept.

use std::fmt::Write as _;

use super::ass_writer::{ConvertOptions, Cue, Line, clean_font_name, font_size, write_script};
use super::style::{Style, decode_entity, normalize, parse_color, parse_timing};

/// Line height as a multiple of the font size, for `line:N` offsets.
const LINE_HEIGHT: f32 = 1.2;

/// Converts WebVTT text to an ASS script. Cues that cannot be read are skipped.
pub fn vtt_to_ass(text: &str, opts: &ConvertOptions) -> String {
    let text = normalize(text);
    let mut rules = Vec::new();
    let mut cues = Vec::new();
    let mut lines = text.split('\n').peekable();
    // The header block (`WEBVTT`, `X-TIMESTAMP-MAP=...`) runs to the first blank line. A file
    // that starts with a cue instead is still read.
    if lines.peek().is_some_and(|line| line.starts_with("WEBVTT")) {
        while lines.next().is_some_and(|line| !line.trim().is_empty()) {}
    }
    loop {
        while lines.next_if(|line| line.trim().is_empty()).is_some() {}
        let mut block = Vec::new();
        while let Some(line) = lines.next_if(|line| !line.trim().is_empty()) {
            block.push(line);
        }
        let Some(&first) = block.first() else {
            break;
        };
        let keyword = first.split_whitespace().next().unwrap_or("");
        if !first.contains("-->") {
            match keyword {
                "NOTE" | "REGION" => continue,
                "STYLE" => {
                    parse_css(&block[1..].join("\n"), opts, &mut rules);
                    continue;
                }
                _ => {}
            }
        }
        // The timing line is the first line, or the second after a cue identifier.
        let Some((timing_at, (start_ms, end_ms, settings))) = block
            .iter()
            .take(2)
            .enumerate()
            .find_map(|(at, line)| Some((at, parse_timing(line)?)))
        else {
            continue;
        };
        if end_ms < start_ms {
            continue;
        }
        let body = block[timing_at + 1..].join("\n");
        let body = body.trim();
        let line_count = body.split('\n').count();
        let placement = placement(&Settings::parse(settings), opts, line_count);
        if let Some(text) = cue_text(body, placement.as_deref(), &rules, opts) {
            cues.push(Cue {
                start_ms,
                end_ms,
                text,
            });
        }
    }
    write_script(opts, cues)
}

#[derive(Clone, Copy, Debug, PartialEq)]
enum Horizontal {
    Left = 1,
    Center = 2,
    Right = 3,
}

#[derive(Clone, Copy, Debug, PartialEq)]
enum Vertical {
    Bottom = 0,
    Middle = 3,
    Top = 6,
}

#[derive(Clone, Copy, Debug, PartialEq)]
enum LinePosition {
    Number(i32),
    Percent(f32),
}

#[derive(Debug, Default, PartialEq)]
struct Settings {
    align: Option<Horizontal>,
    /// Percentage and anchor.
    position: Option<(f32, Option<Horizontal>)>,
    /// Line and its alignment.
    line: Option<(LinePosition, Vertical)>,
}

fn percent(text: &str) -> Option<f32> {
    let value: f32 = text.strip_suffix('%')?.parse().ok()?;
    (0.0..=100.0).contains(&value).then_some(value)
}

impl Settings {
    fn parse(text: &str) -> Settings {
        let mut settings = Settings::default();
        for setting in text.split_whitespace() {
            let Some((name, value)) = setting.split_once(':') else {
                continue;
            };
            let (value, extra) = match value.split_once(',') {
                Some((value, extra)) => (value, Some(extra)),
                None => (value, None),
            };
            match name {
                "align" => {
                    settings.align = match value {
                        "start" | "left" => Some(Horizontal::Left),
                        "center" | "middle" => Some(Horizontal::Center),
                        "end" | "right" => Some(Horizontal::Right),
                        _ => settings.align,
                    }
                }
                "position" => {
                    let anchor = match extra {
                        Some("line-left" | "start") => Some(Horizontal::Left),
                        Some("center" | "middle") => Some(Horizontal::Center),
                        Some("line-right" | "end") => Some(Horizontal::Right),
                        _ => None,
                    };
                    if let Some(position) = percent(value) {
                        settings.position = Some((position, anchor));
                    }
                }
                "line" => {
                    let align = match extra {
                        Some("center" | "middle") => Vertical::Middle,
                        Some("end") => Vertical::Bottom,
                        _ => Vertical::Top,
                    };
                    let line = match percent(value) {
                        Some(position) => Some(LinePosition::Percent(position)),
                        None => value
                            .parse::<i32>()
                            .ok()
                            .map(|n| LinePosition::Number(n.clamp(-1000, 1000))),
                    };
                    if let Some(line) = line {
                        settings.line = Some((line, align));
                    }
                }
                // `size`, `vertical` and `region` are not used.
                _ => {}
            }
        }
        settings
    }
}

/// The override block that places a cue (`{\an8}`, `{\an7\pos(96,540)}`), or `None` for the
/// default place. `line_count` is the number of lines the cue text has.
fn placement(settings: &Settings, opts: &ConvertOptions, line_count: usize) -> Option<String> {
    if *settings == Settings::default() {
        return None;
    }
    let (width, height) = opts.resolution();
    let line_height = opts.font_size.max(1) as f32 * LINE_HEIGHT;
    let margin_v = opts.margin_v as f32;
    let align = settings.align.unwrap_or(Horizontal::Center);
    let anchor = settings.position.and_then(|(_, anchor)| anchor);
    let horizontal = anchor.unwrap_or(align);
    let (vertical, mut y) = match settings.line {
        None | Some((LinePosition::Number(-1), _)) => (Vertical::Bottom, None),
        Some((LinePosition::Number(0), _)) => (Vertical::Top, None),
        Some((LinePosition::Number(n), _)) if n < 0 => (
            Vertical::Bottom,
            Some(height - margin_v - (-n - 1) as f32 * line_height),
        ),
        Some((LinePosition::Number(n), _)) => {
            (Vertical::Top, Some(margin_v + n as f32 * line_height))
        }
        Some((LinePosition::Percent(position), align)) => (align, Some(height * position / 100.0)),
    };
    let mut x = settings
        .position
        .map(|(position, _)| width * position / 100.0);
    if x.is_some() && y.is_none() {
        y = Some(match vertical {
            Vertical::Top => margin_v,
            _ => height - margin_v,
        });
    }
    if y.is_some() && x.is_none() {
        x = Some(match horizontal {
            Horizontal::Left => opts.margin_l as f32,
            Horizontal::Center => width / 2.0,
            Horizontal::Right => width - opts.margin_r as f32,
        });
    }
    let mut block = format!("{{\\an{}", vertical as u32 + horizontal as u32);
    if let (Some(x), Some(y)) = (x, y) {
        // Keep the box inside the picture, as far as its explicit lines tell its height.
        let box_height = (line_count.clamp(1, 100) as f32 * line_height).min(height);
        let above = match vertical {
            Vertical::Top => 0.0,
            Vertical::Middle => box_height / 2.0,
            Vertical::Bottom => box_height,
        };
        let y = (y - above).clamp(0.0, height - box_height) + above;
        let _ = write!(block, "\\pos({},{})", x.round() as i32, y.round() as i32);
    }
    block.push('}');
    Some(block)
}

#[derive(Debug, PartialEq)]
enum Selector {
    /// `::cue`
    All,
    /// `::cue(.name)`
    Class(String),
    /// `::cue(b)`
    Tag(String),
    /// `::cue(v)` or `::cue(v[voice="Name"])`
    Voice(Option<String>),
}

fn parse_selector(text: &str) -> Option<Selector> {
    let at = text.find("::cue")?;
    let rest = text[at + "::cue".len()..].trim();
    if rest.is_empty() {
        return Some(Selector::All);
    }
    let inner = rest.strip_prefix('(')?.strip_suffix(')')?.trim();
    if let Some(at) = inner.find("[voice") {
        let value = inner[at..].split_once('=')?.1;
        let value = value.split_once(']')?.0.trim();
        let value = value.trim_matches(['"', '\'']);
        return Some(Selector::Voice(Some(value.to_owned())));
    }
    let simple = |name: &str| {
        !name.is_empty()
            && name
                .chars()
                .all(|c| c.is_alphanumeric() || matches!(c, '-' | '_'))
    };
    match inner.split_once('.') {
        Some((_tag, classes)) => {
            let class = classes.split('.').next().unwrap_or("");
            simple(class).then(|| Selector::Class(class.to_owned()))
        }
        None if inner == "v" => Some(Selector::Voice(None)),
        None => simple(inner).then(|| Selector::Tag(inner.to_ascii_lowercase())),
    }
}

fn parse_declarations(text: &str, opts: &ConvertOptions) -> Style {
    let mut style = Style::default();
    for declaration in text.split(';') {
        let Some((name, value)) = declaration.split_once(':') else {
            continue;
        };
        let value = value.trim().to_ascii_lowercase();
        let value = value.trim_end_matches("!important").trim();
        match name.trim().to_ascii_lowercase().as_str() {
            "color" => style.color = parse_color(value).or(style.color),
            "font-weight" => match value {
                "bold" | "bolder" => style.bold = Some(true),
                "normal" | "lighter" => style.bold = Some(false),
                _ => {
                    if let Ok(weight) = value.parse::<u32>() {
                        style.bold = Some(weight >= 600);
                    }
                }
            },
            "font-style" => match value {
                "italic" | "oblique" => style.italic = Some(true),
                "normal" => style.italic = Some(false),
                _ => {}
            },
            "text-decoration" | "text-decoration-line" => {
                for word in value.split_whitespace() {
                    match word {
                        "underline" => style.underline = Some(true),
                        "line-through" => style.strike = Some(true),
                        "none" => {
                            style.underline = Some(false);
                            style.strike = Some(false);
                        }
                        _ => {}
                    }
                }
            }
            "font-family" => {
                // The declaration's own text: font names keep their case.
                let families = declaration.split_once(':').map_or("", |(_, value)| value);
                let first = families.split(',').next().unwrap_or("");
                let first = first.trim_end_matches("!important");
                let generic = matches!(
                    first.trim().to_ascii_lowercase().as_str(),
                    "serif" | "sans-serif" | "monospace" | "cursive" | "fantasy" | "inherit"
                );
                if !generic {
                    style.font = clean_font_name(first).or(style.font);
                }
            }
            "font-size" => {
                let number = |unit: &str| value.strip_suffix(unit)?.trim().parse::<f32>().ok();
                let base = opts.font_size.max(1) as f32;
                let size = if let Some(px) = number("px") {
                    opts.scale_px(px)
                } else if let Some(em) = number("em") {
                    font_size(em * base)
                } else if let Some(percent) = number("%") {
                    font_size(percent / 100.0 * base)
                } else {
                    None
                };
                style.size = size.or(style.size);
            }
            _ => {}
        }
    }
    style
}

/// Adds the rules of one `STYLE` block.
fn parse_css(css: &str, opts: &ConvertOptions, rules: &mut Vec<(Selector, Style)>) {
    let mut clean = String::with_capacity(css.len());
    let mut rest = css;
    while let Some(at) = rest.find("/*") {
        clean.push_str(&rest[..at]);
        rest = rest[at + 2..]
            .split_once("*/")
            .map_or("", |(_, after)| after);
    }
    clean.push_str(rest);
    let mut rest = clean.as_str();
    while let Some((selectors, after)) = rest.split_once('{') {
        let (declarations, after) = after.split_once('}').unwrap_or((after, ""));
        rest = after;
        let style = parse_declarations(declarations, opts);
        if style == Style::default() {
            continue;
        }
        for selector in selectors.split(',').filter_map(parse_selector) {
            rules.push((selector, style.clone()));
        }
    }
}

/// The colour of one of the spec's default text colour classes.
fn class_color(class: &str) -> Option<(u8, u8, u8)> {
    match class {
        "white" | "lime" | "cyan" | "red" | "yellow" | "magenta" | "blue" | "black" => {
            parse_color(class)
        }
        _ => None,
    }
}

fn add_rules(style: &mut Style, rules: &[(Selector, Style)], wanted: impl Fn(&Selector) -> bool) {
    for (_, over) in rules.iter().filter(|(selector, _)| wanted(selector)) {
        style.apply(over);
    }
}

/// What an opening tag adds: its own meaning, then the `STYLE` rules for the tag, its classes
/// and its voice.
fn tag_style(name: &str, classes: &[&str], voice: &str, rules: &[(Selector, Style)]) -> Style {
    let on = Some(true);
    let mut style = Style::default();
    match name {
        "b" => style.bold = on,
        "i" => style.italic = on,
        "u" => style.underline = on,
        _ => {}
    }
    add_rules(
        &mut style,
        rules,
        |selector| matches!(selector, Selector::Tag(tag) if tag == name),
    );
    for class in classes {
        if let Some(color) = class_color(class) {
            style.color = Some(color);
        }
        add_rules(
            &mut style,
            rules,
            |selector| matches!(selector, Selector::Class(name) if name == class),
        );
    }
    if name == "v" {
        add_rules(&mut style, rules, |selector| match selector {
            Selector::Voice(None) => true,
            Selector::Voice(Some(name)) => name == voice,
            _ => false,
        });
    }
    style
}

/// One cue's text as an ASS event text; `None` when it shows nothing.
fn cue_text(
    body: &str,
    placement: Option<&str>,
    rules: &[(Selector, Style)],
    opts: &ConvertOptions,
) -> Option<String> {
    let mut base = Style::default();
    for (_, over) in rules.iter().filter(|rule| rule.0 == Selector::All) {
        base.apply(over);
    }
    let mut line = Line::new(opts);
    if let Some(placement) = placement {
        line.raw_block(placement);
    }
    line.set_style(base.clone());
    let mut open: Vec<(String, Style)> = Vec::new();
    let mut in_rt = false;
    let mut rest = body;
    while let Some(c) = rest.chars().next() {
        match c {
            '<' => {
                if let Some(end) = rest.find('>') {
                    let inner = &rest[1..end];
                    rest = &rest[end + 1..];
                    let (close, inner) = match inner.strip_prefix('/') {
                        Some(inner) => (true, inner),
                        None => (false, inner),
                    };
                    let (head, annotation) =
                        inner.split_once(char::is_whitespace).unwrap_or((inner, ""));
                    let mut parts = head.split('.');
                    let name = parts.next().unwrap_or("").to_ascii_lowercase();
                    let classes: Vec<&str> = parts.filter(|class| !class.is_empty()).collect();
                    match (name.as_str(), close) {
                        ("rt", false) if !in_rt => {
                            in_rt = true;
                            line.ch('(');
                        }
                        ("rt" | "ruby", true) if in_rt => {
                            in_rt = false;
                            line.ch(')');
                        }
                        _ => {}
                    }
                    if !matches!(name.as_str(), "b" | "i" | "u" | "c" | "v" | "lang" | "ruby") {
                        // `<rt>`, an inline timestamp or an unknown tag: nothing to style.
                        continue;
                    }
                    if close {
                        if let Some(at) = open.iter().rposition(|(open, _)| *open == name) {
                            open.truncate(at);
                        }
                    } else {
                        let style = tag_style(&name, &classes, annotation.trim(), rules);
                        open.push((name, style));
                    }
                    let mut style = base.clone();
                    open.iter().for_each(|(_, over)| style.apply(over));
                    line.set_style(style);
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
    if in_rt {
        line.ch(')');
    }
    line.finish()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The `(start, end, text)` of every event in the converted script.
    fn events(vtt: &str) -> Vec<(String, String, String)> {
        vtt_to_ass(vtt, &ConvertOptions::default())
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

    fn texts(vtt: &str) -> Vec<String> {
        events(vtt).into_iter().map(|event| event.2).collect()
    }

    /// The text of a single cue with `settings` on its timing line and `style` as a STYLE block.
    fn styled(style: &str, settings: &str, body: &str) -> String {
        let style = if style.is_empty() {
            String::new()
        } else {
            format!("STYLE\n{style}\n\n")
        };
        let vtt = format!("WEBVTT\n\n{style}00:01.000 --> 00:02.000 {settings}\n{body}\n");
        let mut texts = texts(&vtt);
        assert_eq!(texts.len(), 1, "{vtt}");
        texts.remove(0)
    }

    fn one(body: &str) -> String {
        styled("", "", body)
    }

    #[test]
    fn header_notes_regions_and_identifiers_are_handled() {
        let vtt = "\u{feff}WEBVTT - a title\r\nX-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000\r\n\
                   \r\nNOTE\r\na comment\r\nover lines\r\n\r\nNOTE inline --> not a cue\r\n\r\n\
                   REGION\r\nid:fred\r\nwidth:40%\r\n\r\n\
                   intro\r\n00:01.000 --> 00:02.500\r\nFirst\r\nline two\r\n\r\n\r\n\
                   01:02:03.456 --> 01:02:04.000 region:fred\r\nSecond\r\n";
        assert_eq!(
            events(vtt),
            [
                (
                    "0:00:01.00".to_owned(),
                    "0:00:02.50".to_owned(),
                    "First\\Nline two".to_owned()
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
    fn cues_are_emitted_in_start_order_and_bad_ones_are_skipped() {
        let vtt = "WEBVTT\n\n00:05.000 --> 00:06.000\nlater\n\n\
                   00:03.000 --> 00:02.000\nends before it starts\n\n\
                   id\nnot a timing\n00:07.000 --> 00:08.000\ntoo deep\n\n\
                   00:09.000 --> oops\nbad end\n\n\
                   00:01.000 --> 00:04.000\nearlier\n\n\
                   00:10.000 --> 00:11.000\n\n\
                   00:12.000 --> 00:13.000\n<i></i>\n";
        assert_eq!(texts(vtt), ["earlier", "later"]);
        // No header at all is tolerated.
        assert_eq!(texts("00:01.000 --> 00:02.000\nbare\n"), ["bare"]);
        assert_eq!(texts(""), [""; 0]);
    }

    #[test]
    fn inline_tags_become_overrides() {
        assert_eq!(
            one("<b>bold</b> <i>it</i> <u>un</u> x"),
            "{\\b1}bold {\\b0\\i1}it {\\i0\\u1}un {\\u0}x"
        );
        assert_eq!(one("<b>a<i>b</b>c"), "{\\b1}a{\\i1}b{\\b0\\i0}c");
        assert_eq!(one("<i>never closed"), "{\\i1}never closed");
        assert_eq!(
            one("<lang en-GB>hi</lang> <blink>x</blink> </i>y"),
            "hi x y"
        );
    }

    #[test]
    fn timestamps_ruby_and_entities() {
        assert_eq!(
            one("One <00:01.500>two <00:00:01.800><c>three</c>"),
            "One two three"
        );
        assert_eq!(
            one("<ruby>漢<rt>kan</rt>字<rt>ji</ruby> x <ruby>a<rt>b"),
            "漢(kan)字(ji) x a(b)"
        );
        assert_eq!(
            one("a &lt;b&gt; &amp; c&nbsp;d &#x41; &lrm;"),
            "a <b> & c\u{a0}d A \u{200e}"
        );
    }

    #[test]
    fn backslashes_and_braces_are_literal() {
        assert_eq!(
            one("{\\an8} C:\\Notes \\h {x}"),
            "\\{\\\u{2060}an8\\} C:\\\u{2060}Notes \\\u{2060}h \\{x\\}"
        );
        assert_eq!(one("a < b"), "a < b");
    }

    #[test]
    fn default_colour_classes_apply() {
        assert_eq!(
            one("<c.yellow>a</c> <c.lime.bg_blue>b</c> <c.red>r<c.cyan>c</c>r</c>"),
            "{\\c&H00FFFF&}a {\\c&H00FF00&}b {\\c&H0000FF&}r{\\c&HFFFF00&}c{\\c&H0000FF&}r"
        );
    }

    #[test]
    fn style_blocks_style_classes_tags_and_voices() {
        let css = "/* a comment */\n\
                   ::cue(.loud) { color: #FF0000; font-weight: bold; font-size: 150% }\n\
                   ::cue(c.quiet), ::cue(.aside) {\n  font-style: italic;\n  \
                   text-decoration: underline line-through;\n}\n\
                   ::cue(b) { color: rgb(0, 0, 255) }\n\
                   ::cue(v) { font-family: \"Trebuchet MS\", sans-serif; }\n\
                   ::cue(v[voice=\"Esme\"]) { color: lime !important }\n\
                   ::cue(v[voice=Bob]) { color: cyan; font-size: 54px }\n\
                   ::cue(#id) { color: red }\n\
                   video::cue(i) { font-size: 0.5em; font-style: normal }";
        assert_eq!(
            styled(css, "", "<c.loud>A</c>b"),
            "{\\b1\\c&H0000FF&\\fs90}A{\\b0\\c\\fs}b"
        );
        assert_eq!(
            styled(css, "", "<c.quiet>a</c><c.aside>b</c>c"),
            "{\\i1\\u1\\s1}ab{\\i0\\u0\\s0}c"
        );
        assert_eq!(styled(css, "", "<b>a"), "{\\b1\\c&HFF0000&}a");
        assert_eq!(styled(css, "", "<i>a"), "{\\i0\\fs30}a");
        assert_eq!(
            styled(
                css,
                "",
                "<v Esme>Hi</v> <v.loud Bob>Yo</v> <v Someone Else>Eh"
            ),
            "{\\c&H00FF00&\\fnTrebuchet MS}Hi \
             {\\b1\\c&HFFFF00&\\fs54}Yo {\\b0\\c\\fs}Eh"
        );
    }

    #[test]
    fn the_cue_rule_styles_every_cue_and_is_what_closing_tags_return_to() {
        let css = "::cue { color: yellow; font-family: Georgia }\n::cue(.w) { color: white }";
        assert_eq!(
            styled(css, "", "a<c.w>b</c>c"),
            "{\\c&H00FFFF&\\fnGeorgia}a{\\c&HFFFFFF&}b{\\c&H00FFFF&}c"
        );
    }

    #[test]
    fn several_style_blocks_and_broken_css_are_tolerated() {
        let vtt = "WEBVTT\n\nSTYLE\n::cue(.a) { color: red }\n\nSTYLE\n::cue(.b) { color: blue\n\n\
                   STYLE\n}}{{ nonsense ::cue( { : ; }\n\n\
                   00:01.000 --> 00:02.000\n<c.a>a</c><c.b>b</c>\n";
        assert_eq!(texts(vtt), ["{\\c&H0000FF&}a{\\c&HFF0000&}b"]);
    }

    #[test]
    fn align_alone_picks_the_bottom_alignment() {
        assert_eq!(one("x"), "x");
        assert_eq!(styled("", "align:start", "x"), "{\\an1}x");
        assert_eq!(styled("", "align:left", "x"), "{\\an1}x");
        assert_eq!(styled("", "align:center", "x"), "{\\an2}x");
        assert_eq!(styled("", "align:end", "x"), "{\\an3}x");
        assert_eq!(
            styled("", "align:right size:50% vertical:rl", "x"),
            "{\\an3}x"
        );
        assert_eq!(styled("", "size:50% bogus align:nowhere", "x"), "x");
    }

    #[test]
    fn line_numbers_count_from_the_top_or_the_bottom() {
        assert_eq!(styled("", "line:-1", "x"), "{\\an2}x");
        assert_eq!(styled("", "line:0", "x"), "{\\an8}x");
        assert_eq!(styled("", "line:0 align:end", "x"), "{\\an9}x");
        // 54 + 2 x 72 from the top; 1080 - 54 - 2 x 72 from the bottom.
        assert_eq!(styled("", "line:2", "x"), "{\\an8\\pos(960,198)}x");
        assert_eq!(
            styled("", "line:-3 align:start", "x"),
            "{\\an1\\pos(96,882)}x"
        );
        assert_eq!(
            styled("", "line:1 align:right", "x"),
            "{\\an9\\pos(1824,126)}x"
        );
    }

    #[test]
    fn line_percentages_and_positions_give_pos() {
        assert_eq!(styled("", "line:10%", "x"), "{\\an8\\pos(960,108)}x");
        assert_eq!(styled("", "line:50%,center", "x"), "{\\an5\\pos(960,540)}x");
        assert_eq!(
            styled("", "line:80%,end align:start", "x"),
            "{\\an1\\pos(96,864)}x"
        );
        assert_eq!(styled("", "position:25%", "x"), "{\\an2\\pos(480,1026)}x");
        assert_eq!(
            styled("", "position:10%,line-left align:end", "x"),
            "{\\an1\\pos(192,1026)}x"
        );
        assert_eq!(
            styled("", "position:90%,line-right line:0", "x"),
            "{\\an9\\pos(1728,54)}x"
        );
        assert_eq!(
            styled("", "line:20% position:30%,center align:start", "x"),
            "{\\an8\\pos(576,216)}x"
        );
        assert_eq!(styled("", "line:150% position:-5%", "x"), "x");
    }

    #[test]
    fn positioned_cues_stay_inside_the_picture() {
        // Two lines of 72 need 144: a top anchor at 95% (1026) moves up to 936.
        assert_eq!(styled("", "line:95%", "a\nb"), "{\\an8\\pos(960,936)}a\\Nb");
        assert_eq!(styled("", "line:2%,end", "a"), "{\\an2\\pos(960,72)}a");
        assert_eq!(styled("", "line:1%,center", "a"), "{\\an5\\pos(960,36)}a");
        assert_eq!(styled("", "line:900", "a"), "{\\an8\\pos(960,1008)}a");
        assert_eq!(styled("", "line:-900", "a"), "{\\an2\\pos(960,72)}a");
    }

    #[test]
    fn placement_comes_before_the_cue_style() {
        assert_eq!(
            styled("::cue { color: red }", "line:0", "<i>x"),
            "{\\an8}{\\i1\\c&H0000FF&}x"
        );
    }

    #[test]
    fn hostile_input_never_panics() {
        let pieces = [
            "WEBVTT",
            "STYLE",
            "NOTE",
            "REGION",
            "::cue",
            "(",
            ")",
            "{",
            "}",
            "[voice=",
            "\"",
            "color:",
            "red",
            ";",
            "<",
            ">",
            "</",
            "<v ",
            "<c.",
            ".",
            "<ruby>",
            "<rt>",
            "&",
            "&#",
            "-->",
            "00:01.000",
            " line:",
            "-",
            "5",
            "%",
            ",",
            " position:",
            " align:",
            "\n",
            "\n\n",
            "\r",
            " ",
            "\\",
            "é",
            "😀",
            "\0",
            "/*",
            "*/",
        ];
        let mut seed = 0x9E37_79B9u32;
        for _ in 0..3000 {
            let mut input = String::new();
            for _ in 0..16 {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                input.push_str(pieces[seed as usize % pieces.len()]);
            }
            let with_cue = format!("WEBVTT\n\n00:01.000 --> 00:02.000{input}\n{input}\n");
            for text in [input.as_str(), with_cue.as_str()] {
                let script = vtt_to_ass(text, &ConvertOptions::default());
                assert!(script.lines().all(|line| !line.contains('\0')));
            }
        }
    }
}
