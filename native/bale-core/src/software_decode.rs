//! Ported from `SoftwareDecodePolicy` (src/Bale.Core/Playback/SoftwareDecodePolicy.cs).

/// The planner's spelling of a codec name: trimmed, lower-cased, with common aliases folded
/// (silo-server capabilities_v3.go normalizeCodecV3 / protocol_v3.go spellings). Every `pcm_*`
/// becomes "pcm", the route's name for LPCM. `None` for a missing or blank name.
pub fn normalize_codec(codec: Option<&str>) -> Option<String> {
    let value = to_lower_invariant(codec?.trim());
    let normalized = match value.as_str() {
        "" => return None,
        "h265" | "h.265" | "x265" => "hevc",
        "h.264" | "avc" | "x264" => "h264",
        "vp09" => "vp9",
        "e-ac-3" | "ec-3" => "eac3",
        "mlp fba" => "truehd",
        value if value.starts_with("pcm_") => "pcm",
        _ => return Some(value),
    };
    Some(normalized.to_owned())
}

/// .NET's `ToLowerInvariant`: one character to one, so Σ never becomes final ς (as Rust's
/// `str::to_lowercase` does), and a character whose lowercase expands (only U+0130 İ) is left as is,
/// which is what .NET does (checked by BaleCoreNativeTests).
fn to_lower_invariant(value: &str) -> String {
    value
        .chars()
        .map(|c| {
            let mut lower = c.to_lowercase();
            match (lower.next(), lower.next()) {
                (Some(single), None) => single,
                _ => c,
            }
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::normalize_codec;

    fn n(codec: &str) -> Option<String> {
        normalize_codec(Some(codec))
    }

    #[test]
    fn folds_aliases() {
        for (from, to) in [
            ("h265", "hevc"),
            ("H.265", "hevc"),
            ("x265", "hevc"),
            ("HEVC", "hevc"),
            ("h.264", "h264"),
            ("AVC", "h264"),
            ("x264", "h264"),
            ("VP09", "vp9"),
            (" vp9 ", "vp9"),
            ("E-AC-3", "eac3"),
            ("ec-3", "eac3"),
            ("MLP FBA", "truehd"),
            ("pcm_s24le", "pcm"),
            ("PCM_BLURAY", "pcm"),
            ("pcm", "pcm"),
            ("mpeg4", "mpeg4"),
            ("dts_hd", "dts_hd"),
        ] {
            assert_eq!(n(from).as_deref(), Some(to), "{from}");
        }
    }

    #[test]
    fn blank_is_none() {
        assert_eq!(normalize_codec(None), None);
        assert_eq!(n(""), None);
        assert_eq!(n(" \t\u{a0}"), None);
    }

    #[test]
    fn lower_cases_like_dotnet_invariant() {
        assert_eq!(n("ΣΑΣ").as_deref(), Some("σασ"));
        assert_eq!(n("\u{130}X").as_deref(), Some("\u{130}x"));
        assert_eq!(n("Ǆẞ").as_deref(), Some("ǆß"));
    }
}
