//! What the SRT and WebVTT converters share on the input side: inline style state, colours,
//! character references and timestamps.

/// Inline style changes relative to the script's style; `None` leaves a property alone.
#[derive(Clone, Debug, Default, PartialEq)]
pub(crate) struct Style {
    /// Red, green, blue.
    pub color: Option<(u8, u8, u8)>,
    pub bold: Option<bool>,
    pub italic: Option<bool>,
    pub underline: Option<bool>,
    pub strike: Option<bool>,
    pub font: Option<String>,
    /// `\fs` value in script pixels.
    pub size: Option<u32>,
}

impl Style {
    /// Takes every property `over` sets.
    pub fn apply(&mut self, over: &Style) {
        if over.color.is_some() {
            self.color = over.color;
        }
        self.bold = over.bold.or(self.bold);
        self.italic = over.italic.or(self.italic);
        self.underline = over.underline.or(self.underline);
        self.strike = over.strike.or(self.strike);
        if over.font.is_some() {
            self.font.clone_from(&over.font);
        }
        self.size = over.size.or(self.size);
    }
}

/// Text without a byte order mark and with every line ending as `\n`.
pub(crate) fn normalize(text: &str) -> String {
    text.strip_prefix('\u{feff}')
        .unwrap_or(text)
        .replace("\r\n", "\n")
        .replace('\r', "\n")
}

/// `[hours:]minutes:seconds[.,fraction]` in milliseconds. Hours may have any width; the fraction
/// is read as a count of milliseconds like Media3 and FFmpeg do (`,5` = 5 ms), and digits after
/// the third are ignored.
pub(crate) fn parse_time(text: &str) -> Option<u64> {
    let text = text.trim();
    let (clock, fraction) = match text.rfind([',', '.']) {
        Some(at) => (&text[..at], &text[at + 1..]),
        None => (text, ""),
    };
    let number = |digits: &str| -> Option<u64> {
        let digits = digits.trim();
        if digits.is_empty() || digits.len() > 9 || !digits.bytes().all(|b| b.is_ascii_digit()) {
            return None;
        }
        digits.parse().ok()
    };
    let parts: Vec<&str> = clock.split(':').collect();
    let (hours, minutes, seconds) = match parts[..] {
        [minutes, seconds] => (0, number(minutes)?, number(seconds)?),
        [hours, minutes, seconds] => (number(hours)?, number(minutes)?, number(seconds)?),
        _ => return None,
    };
    let fraction = fraction.trim();
    let millis = if fraction.is_empty() {
        0
    } else {
        if !fraction.bytes().all(|b| b.is_ascii_digit()) {
            return None;
        }
        number(&fraction[..fraction.len().min(3)])?
    };
    Some(((hours * 60 + minutes) * 60 + seconds) * 1000 + millis)
}

/// A `start --> end [rest]` line: both times and what follows the end time.
pub(crate) fn parse_timing(line: &str) -> Option<(u64, u64, &str)> {
    let (start, rest) = line.split_once("-->")?;
    let rest = rest.trim_start();
    let end_len = rest.find(char::is_whitespace).unwrap_or(rest.len());
    Some((
        parse_time(start)?,
        parse_time(&rest[..end_len])?,
        &rest[end_len..],
    ))
}

/// The character reference at the start of `text` (which starts with `&`): the character and the
/// reference's length in bytes.
pub(crate) fn decode_entity(text: &str) -> Option<(char, usize)> {
    let end = text.bytes().take(12).position(|b| b == b';')?;
    let name = text.get(1..end)?;
    let c = match name {
        "amp" => '&',
        "lt" => '<',
        "gt" => '>',
        "quot" => '"',
        "apos" => '\'',
        "nbsp" => '\u{a0}',
        "lrm" => '\u{200e}',
        "rlm" => '\u{200f}',
        _ => {
            let number = name.strip_prefix('#')?;
            let code = match number.strip_prefix(['x', 'X']) {
                Some(hex) => u32::from_str_radix(hex, 16).ok()?,
                None => number.parse().ok()?,
            };
            char::from_u32(code).filter(|&c| c != '\0')?
        }
    };
    Some((c, end + 1))
}

const NAMED_COLORS: [(&str, u32); 46] = [
    ("aqua", 0x00FFFF),
    ("beige", 0xF5F5DC),
    ("black", 0x000000),
    ("blue", 0x0000FF),
    ("brown", 0xA52A2A),
    ("coral", 0xFF7F50),
    ("crimson", 0xDC143C),
    ("cyan", 0x00FFFF),
    ("darkblue", 0x00008B),
    ("darkgray", 0xA9A9A9),
    ("darkgreen", 0x006400),
    ("darkgrey", 0xA9A9A9),
    ("darkorange", 0xFF8C00),
    ("darkred", 0x8B0000),
    ("fuchsia", 0xFF00FF),
    ("gold", 0xFFD700),
    ("gray", 0x808080),
    ("green", 0x008000),
    ("grey", 0x808080),
    ("indigo", 0x4B0082),
    ("ivory", 0xFFFFF0),
    ("khaki", 0xF0E68C),
    ("lavender", 0xE6E6FA),
    ("lightblue", 0xADD8E6),
    ("lightgray", 0xD3D3D3),
    ("lightgreen", 0x90EE90),
    ("lightgrey", 0xD3D3D3),
    ("lightyellow", 0xFFFFE0),
    ("lime", 0x00FF00),
    ("magenta", 0xFF00FF),
    ("maroon", 0x800000),
    ("navy", 0x000080),
    ("olive", 0x808000),
    ("orange", 0xFFA500),
    ("pink", 0xFFC0CB),
    ("purple", 0x800080),
    ("red", 0xFF0000),
    ("salmon", 0xFA8072),
    ("silver", 0xC0C0C0),
    ("skyblue", 0x87CEEB),
    ("tan", 0xD2B48C),
    ("teal", 0x008080),
    ("turquoise", 0x40E0D0),
    ("violet", 0xEE82EE),
    ("white", 0xFFFFFF),
    ("yellow", 0xFFFF00),
];

fn rgb(value: u32) -> (u8, u8, u8) {
    let [_, r, g, b] = value.to_be_bytes();
    (r, g, b)
}

fn hex_color(digits: &str) -> Option<(u8, u8, u8)> {
    if !digits.bytes().all(|b| b.is_ascii_hexdigit()) {
        return None;
    }
    let value = u32::from_str_radix(digits, 16).ok()?;
    match digits.len() {
        // #RGB and #RGBA: every digit doubled; alpha is ignored.
        3 | 4 => {
            let value = if digits.len() == 4 { value >> 4 } else { value };
            let wide = |nibble: u32| ((nibble & 0xF) * 17) as u8;
            Some((wide(value >> 8), wide(value >> 4), wide(value)))
        }
        6 => Some(rgb(value)),
        8 => Some(rgb(value >> 8)),
        _ => None,
    }
}

/// An HTML/CSS colour as red, green, blue: a name, `#RGB`, `#RRGGBB` (with or without `#`, alpha
/// digits ignored) or `rgb(r, g, b)` / `rgba(...)` with numbers or percentages.
pub(crate) fn parse_color(text: &str) -> Option<(u8, u8, u8)> {
    let text = text.trim().to_ascii_lowercase();
    if let Some(digits) = text.strip_prefix('#') {
        return hex_color(digits);
    }
    if let Ok(at) = NAMED_COLORS.binary_search_by(|(name, _)| (*name).cmp(text.as_str())) {
        return Some(rgb(NAMED_COLORS[at].1));
    }
    if let Some(inner) = text
        .strip_prefix("rgba(")
        .or_else(|| text.strip_prefix("rgb("))
    {
        let inner = inner.strip_suffix(')')?;
        let mut parts = inner
            .split([',', ' ', '/'])
            .filter(|part| !part.is_empty())
            .map(|part| {
                let value = match part.strip_suffix('%') {
                    Some(percent) => percent.parse::<f32>().ok()? * 2.55,
                    None => part.parse::<f32>().ok()?,
                };
                value
                    .is_finite()
                    .then(|| value.round().clamp(0.0, 255.0) as u8)
            });
        return Some((parts.next()??, parts.next()??, parts.next()??));
    }
    if text.len() == 6 {
        return hex_color(&text);
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn named_colors_are_sorted_for_the_binary_search() {
        assert!(NAMED_COLORS.windows(2).all(|pair| pair[0].0 < pair[1].0));
    }

    #[test]
    fn colors_parse_in_every_form() {
        assert_eq!(parse_color("Red"), Some((255, 0, 0)));
        assert_eq!(parse_color(" navy "), Some((0, 0, 128)));
        assert_eq!(parse_color("#1A2b3C"), Some((0x1A, 0x2B, 0x3C)));
        assert_eq!(parse_color("#f80"), Some((0xFF, 0x88, 0x00)));
        assert_eq!(parse_color("#f80c"), Some((0xFF, 0x88, 0x00)));
        assert_eq!(parse_color("#11223344"), Some((0x11, 0x22, 0x33)));
        assert_eq!(parse_color("00ff7f"), Some((0, 255, 127)));
        assert_eq!(parse_color("rgb(1, 2,300)"), Some((1, 2, 255)));
        assert_eq!(parse_color("rgba(100% 0% 50% / 0.5)"), Some((255, 0, 128)));
        for bad in [
            "",
            "#",
            "#12",
            "#gggggg",
            "nocolor",
            "rgb(1,2)",
            "rgb(a,b,c)",
            "+12345",
        ] {
            assert_eq!(parse_color(bad), None, "{bad}");
        }
    }

    #[test]
    fn times_parse_with_and_without_hours() {
        assert_eq!(parse_time("00:00:01,000"), Some(1000));
        assert_eq!(parse_time("01:02:03.456"), Some(3_723_456));
        assert_eq!(parse_time("1:02:03,4"), Some(3_723_004));
        assert_eq!(parse_time("123:00:00,000"), Some(442_800_000));
        assert_eq!(parse_time("02:03.45"), Some(123_045));
        assert_eq!(parse_time("00:00:01,99999"), Some(1999));
        assert_eq!(parse_time(" 00:00:07 "), Some(7000));
        for bad in [
            "",
            "1",
            "a:b:c",
            "00:00:-1,000",
            "1:2:3:4",
            "00:00:01,x",
            "::",
            "0:0:,5",
        ] {
            assert_eq!(parse_time(bad), None, "{bad}");
        }
        assert_eq!(parse_time("99999999999999999999:00:00,000"), None);
    }

    #[test]
    fn timing_lines_split_into_times_and_the_rest() {
        assert_eq!(
            parse_timing("00:00:01,000 --> 00:00:02,500"),
            Some((1000, 2500, ""))
        );
        assert_eq!(
            parse_timing("00:01.000-->00:02.000 align:start"),
            Some((1000, 2000, " align:start"))
        );
        assert_eq!(parse_timing("00:00:01,000 -> 00:00:02,000"), None);
        assert_eq!(parse_timing("text --> more text"), None);
    }

    #[test]
    fn entities_decode() {
        assert_eq!(decode_entity("&amp; x"), Some(('&', 5)));
        assert_eq!(decode_entity("&lt;"), Some(('<', 4)));
        assert_eq!(decode_entity("&nbsp;"), Some(('\u{a0}', 6)));
        assert_eq!(decode_entity("&rlm;"), Some(('\u{200f}', 5)));
        assert_eq!(decode_entity("&#233;"), Some(('é', 6)));
        assert_eq!(decode_entity("&#x1F600;"), Some(('😀', 9)));
        for bad in [
            "&",
            "& b;",
            "&bogus;",
            "&#;",
            "&#0;",
            "&#xD800;",
            "&#99999999;",
            "&é",
        ] {
            assert_eq!(decode_entity(bad), None, "{bad}");
        }
    }

    #[test]
    fn line_endings_and_bom_are_normalized() {
        assert_eq!(normalize("\u{feff}a\r\nb\rc\n"), "a\nb\nc\n");
    }
}
