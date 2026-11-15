//! .NET string semantics the C# originals relied on, so ported comparisons match exactly.

/// `ToLowerInvariant`: one character to one. A character whose lowercase expands (only U+0130 İ)
/// stays as is, and Σ never becomes final ς, as .NET does (checked by BaleCoreNativeTests).
pub fn lower(value: &str) -> String {
    value.chars().map(simple_lower).collect()
}

/// `?.Trim().ToLowerInvariant()`. Rust's and .NET's whitespace sets are both Unicode White_Space.
pub fn trim_lower(value: Option<&str>) -> Option<String> {
    value.map(|value| lower(value.trim()))
}

/// `string.Equals(a, b, StringComparison.OrdinalIgnoreCase)`; a null `a` is never equal.
pub fn eq_ignore_case(a: Option<&str>, b: &str) -> bool {
    a.is_some_and(|a| a.chars().map(simple_upper).eq(b.chars().map(simple_upper)))
}

/// `a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)`.
pub fn starts_with_ignore_case(value: &str, prefix: &str) -> bool {
    let mut chars = value.chars();
    prefix.chars().all(|p| {
        chars
            .next()
            .is_some_and(|c| simple_upper(c) == simple_upper(p))
    })
}

/// `string.IsNullOrWhiteSpace`.
pub fn is_blank(value: Option<&str>) -> bool {
    value.is_none_or(|value| value.trim().is_empty())
}

/// `value?.Trim() is { Length: > 0 } trimmed ? trimmed : null`.
pub fn non_empty(value: Option<&str>) -> Option<&str> {
    value.map(str::trim).filter(|value| !value.is_empty())
}

/// A double as System.Text.Json writes it with `AllowNamedFloatingPointLiterals`: a number, or
/// "NaN", "Infinity" or "-Infinity".
pub fn double<'de, D: serde::Deserializer<'de>>(deserializer: D) -> Result<f64, D::Error> {
    #[derive(serde::Deserialize)]
    #[serde(untagged)]
    enum Double<'a> {
        Number(f64),
        Named(&'a str),
    }
    match serde::Deserialize::deserialize(deserializer)? {
        Double::Number(value) => Ok(value),
        Double::Named("NaN") => Ok(f64::NAN),
        Double::Named("Infinity") => Ok(f64::INFINITY),
        Double::Named("-Infinity") => Ok(f64::NEG_INFINITY),
        Double::Named(_) => Err(serde::de::Error::custom("not a double")),
    }
}

/// `value.ToString("0.0")` (more generally `"0." + decimals zeros`) with the invariant culture, as
/// .NET Core 3.0+ formats a double for a custom format: 15 significant digits first, then the
/// digit string rounded half away from zero. Rust's `{:.1}` rounds the exact binary value instead,
/// so 0.15 would print "0.1" where .NET prints "0.2". Checked against .NET by BaleCoreNativeTests.
pub fn format_fixed(value: f64, decimals: usize) -> String {
    if value.is_nan() {
        return "NaN".to_owned();
    }
    if value.is_infinite() {
        // The invariant culture's symbols, not "∞".
        return if value > 0.0 { "Infinity" } else { "-Infinity" }.to_owned();
    }
    let negative = value.is_sign_negative();
    // 15 significant digits, correctly rounded, as d.dddddddddddddde±x.
    let (mut digits, point) = if value == 0.0 {
        (vec![0u8], 1i64)
    } else {
        let text = format!("{:.14e}", value.abs());
        let (mantissa, exponent) = text.split_once('e').unwrap_or((&text, "0"));
        let digits: Vec<u8> = mantissa
            .bytes()
            .filter(u8::is_ascii_digit)
            .map(|b| b - b'0')
            .collect();
        (digits, exponent.parse::<i64>().unwrap_or(0) + 1)
    };
    // Round the digit string to `decimals` places after the point, half up on the next digit.
    let keep = point + decimals as i64;
    let mut point = point;
    if keep < 0 {
        digits = vec![0];
        point = 1;
    } else if (keep as usize) < digits.len() {
        let round_up = digits[keep as usize] >= 5;
        digits.truncate(keep as usize);
        if round_up {
            let mut i = digits.len();
            loop {
                if i == 0 {
                    digits.insert(0, 1);
                    point += 1;
                    break;
                }
                i -= 1;
                if digits[i] == 9 {
                    digits[i] = 0;
                } else {
                    digits[i] += 1;
                    break;
                }
            }
        }
    }
    let digit = |i: i64| {
        if i >= 0 && (i as usize) < digits.len() {
            char::from(b'0' + digits[i as usize])
        } else {
            '0'
        }
    };
    let mut integer: String = (0..point.max(0))
        .map(digit)
        .collect::<String>()
        .trim_start_matches('0')
        .to_owned();
    if integer.is_empty() {
        integer.push('0');
    }
    let fraction: String = (point..point + decimals as i64).map(digit).collect();
    let mut text = String::new();
    if negative {
        text.push('-');
    }
    text.push_str(&integer);
    if decimals > 0 {
        text.push('.');
        text.push_str(&fraction);
    }
    text
}

/// [`double`] for a nullable double.
pub fn optional_double<'de, D: serde::Deserializer<'de>>(
    deserializer: D,
) -> Result<Option<f64>, D::Error> {
    #[derive(serde::Deserialize)]
    struct Wrapped(#[serde(deserialize_with = "double")] f64);
    Ok(
        <Option<Wrapped> as serde::Deserialize>::deserialize(deserializer)?
            .map(|Wrapped(value)| value),
    )
}

/// `double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture)`: optional white space
/// (tab to carriage return and space) around an optional sign, digits with an optional point, an
/// optional exponent; or the invariant culture's "Infinity" / "NaN", case-insensitively.
/// .NET Core 3.0+ parses correctly rounded and overflows to infinity, as Rust's parser does.
pub fn parse_float(text: &str) -> Option<f64> {
    let text = text.trim_matches(|c: char| matches!(c, '\t'..='\r' | ' '));
    let (sign, body) = match text.as_bytes().first() {
        Some(b'+') => (1.0, &text[1..]),
        Some(b'-') => (-1.0, &text[1..]),
        _ => (1.0, text),
    };
    if body.eq_ignore_ascii_case("infinity") {
        return Some(sign * f64::INFINITY);
    }
    if body.eq_ignore_ascii_case("nan") {
        return Some(f64::NAN);
    }
    let bytes = body.as_bytes();
    let mut i = 0;
    let digits = |i: &mut usize| {
        let start = *i;
        while *i < bytes.len() && bytes[*i].is_ascii_digit() {
            *i += 1;
        }
        *i - start
    };
    let mut mantissa = digits(&mut i);
    if i < bytes.len() && bytes[i] == b'.' {
        i += 1;
        mantissa += digits(&mut i);
    }
    if mantissa == 0 {
        return None;
    }
    if i < bytes.len() && matches!(bytes[i], b'e' | b'E') {
        i += 1;
        if i < bytes.len() && matches!(bytes[i], b'+' | b'-') {
            i += 1;
        }
        if digits(&mut i) == 0 {
            return None;
        }
    }
    if i != bytes.len() {
        return None;
    }
    body.parse::<f64>().ok().map(|value| sign * value)
}

/// One UTF-16 code unit's general category, as .NET's char-based checks see it: characters outside
/// the BMP are two surrogates (category Cs), never letters or digits.
fn bmp_category(c: char) -> Option<unicode_general_category::GeneralCategory> {
    (u32::from(c) <= 0xFFFF).then(|| unicode_general_category::get_general_category(c))
}

/// `char.IsLetterOrDigit`: a letter (Lu, Ll, Lt, Lm, Lo) or a decimal digit (Nd).
pub fn is_letter_or_digit(c: char) -> bool {
    use unicode_general_category::GeneralCategory::*;
    matches!(
        bmp_category(c),
        Some(
            UppercaseLetter
                | LowercaseLetter
                | TitlecaseLetter
                | ModifierLetter
                | OtherLetter
                | DecimalNumber
        )
    )
}

/// Regex `\d`: a decimal digit (Nd).
pub fn is_regex_digit(c: char) -> bool {
    matches!(
        bmp_category(c),
        Some(unicode_general_category::GeneralCategory::DecimalNumber)
    )
}

/// What .NET's regex `\b` counts as a word character: `\w` (L, Mn, Nd, Pc) plus ZWNJ and ZWJ.
pub fn is_regex_boundary_word(c: char) -> bool {
    use unicode_general_category::GeneralCategory::*;
    matches!(c, '\u{200C}' | '\u{200D}')
        || matches!(
            bmp_category(c),
            Some(
                UppercaseLetter
                    | LowercaseLetter
                    | TitlecaseLetter
                    | ModifierLetter
                    | OtherLetter
                    | NonspacingMark
                    | DecimalNumber
                    | ConnectorPunctuation
            )
        )
}

fn simple_lower(c: char) -> char {
    let mut lower = c.to_lowercase();
    match (lower.next(), lower.next()) {
        (Some(single), None) => single,
        _ => c,
    }
}

fn simple_upper(c: char) -> char {
    let mut upper = c.to_uppercase();
    match (upper.next(), upper.next()) {
        (Some(single), None) => single,
        _ => c,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn matches_dotnet() {
        assert_eq!(lower("ΣΑΣ İX"), "σασ İx");
        assert!(eq_ignore_case(Some("ORIGINAL_http"), "original_http"));
        assert!(!eq_ignore_case(None, ""));
        assert!(starts_with_ignore_case("xbox one x", "Xbox One"));
        assert!(!starts_with_ignore_case("Xbox", "Xbox One"));
        assert!(is_blank(Some(" \u{a0}")) && is_blank(None) && !is_blank(Some("a")));
        assert_eq!(non_empty(Some("  x ")), Some("x"));
    }
}
