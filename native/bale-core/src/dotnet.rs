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
