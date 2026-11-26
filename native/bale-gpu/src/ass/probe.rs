//! The libass debug probes (DEBUG_SERVER `GET /ass-probe`, `debug-probes` feature): font discovery through
//! DirectWrite and render timings as a text report, and the probe script's frame as a PNG. Output never contains
//! paths, URLs or system error text.

use std::fmt::Write as _;

use super::*;
use crate::png;

struct Canvas {
    width: i32,
    height: i32,
    rgb: Vec<u8>,
}

impl Canvas {
    fn new(width: i32, height: i32) -> Self {
        // Neutral dark grey so both white text and black outlines/shadows are visible.
        let rgb = [0x3A; 3].repeat(width as usize * height as usize);
        Canvas { width, height, rgb }
    }

    fn blend(&mut self, image: &Image) {
        let [r, g, b, transparency] = image.color.to_be_bytes();
        let opacity = 255 - u32::from(transparency);
        for y in 0..image.h {
            let dy = image.dst_y + y;
            if dy < 0 || dy >= self.height {
                continue;
            }
            for (x, &coverage) in image.row(y).iter().enumerate() {
                let dx = image.dst_x + x as i32;
                let alpha = u32::from(coverage) * opacity / 255;
                if alpha == 0 || dx < 0 || dx >= self.width {
                    continue;
                }
                let at = (dy as usize * self.width as usize + dx as usize) * 3;
                for (pixel, source) in self.rgb[at..at + 3].iter_mut().zip([r, g, b]) {
                    *pixel = ((u32::from(*pixel) * (255 - alpha) + u32::from(source) * alpha) / 255)
                        as u8;
                }
            }
        }
    }
}

const WIDTH: i32 = 1920;
const HEIGHT: i32 = 1080;
const PROBE_SCRIPT: &str = include_str!("../ass_probe.ass");
const EMBEDDED_NAME: &str = "InterTight-Black.ttf";

/// (label, `\fn` family, text): each case gets its own second in a generated script.
const FONT_CASES: [(&str, &str, &str); 14] = [
    ("Arial", "Arial", "Arial: The quick brown fox"),
    ("Segoe UI", "Segoe UI", "Segoe UI: The quick brown fox"),
    (
        "Times New Roman",
        "Times New Roman",
        "Times New Roman: The quick brown fox",
    ),
    (
        "Courier New",
        "Courier New",
        "Courier New: The quick brown fox",
    ),
    (
        "Missing family",
        "Bale Missing Font",
        "Unknown family falls back",
    ),
    ("Japanese", "Arial", "日本語の字幕テスト、句読点。"),
    ("Korean", "Arial", "한국어 자막 테스트"),
    ("Chinese", "Arial", "简体中文 / 繁體中文"),
    ("Arabic", "Arial", "العربية من اليمين إلى اليسار 123"),
    ("Hebrew", "Arial", "עברית מימין לשמאל"),
    ("Greek/Cyrillic", "Arial", "Ελληνικά / Кириллица"),
    ("Emoji", "Segoe UI Emoji", "😀🎬🍿"),
    ("Vertical @", "@Arial", "縦書き"),
    (
        "Embedded font",
        "Inter Tight Black",
        "Inter Tight Black (attached)",
    ),
];

fn font_case_script() -> String {
    let mut script = String::from(
        "[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\nScaledBorderAndShadow: yes\n\n\
         [V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, \
         BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, \
         Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n\
         Style: Default,Arial,64,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,3,2,2,40,40,60,1\n\n\
         [Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n",
    );
    for (index, (_, family, text)) in FONT_CASES.iter().enumerate() {
        let _ = writeln!(
            script,
            "Dialogue: 0,0:00:{index:02}.00,0:00:{:02}.00,Default,,0,0,0,,{{\\fn{family}}}{text}",
            index + 1
        );
    }
    script
}

fn micros(duration: Duration) -> u128 {
    duration.as_micros()
}

fn append_log(report: &mut String, session: &Session, indent: &str, limit: usize) {
    let messages = session.drain_log();
    for message in messages.iter().take(limit) {
        let marker = if message.level <= 2 { "!" } else { "" };
        let text: String = message.text.trim_end().chars().take(200).collect();
        let _ = writeln!(report, "{indent}{marker}{text}");
    }
    if messages.len() > limit {
        let _ = writeln!(
            report,
            "{indent}… {} more log lines",
            messages.len() - limit
        );
    }
}

fn describe(stats: &RenderStats) -> String {
    let bounds = stats.bounds.map_or_else(
        || "nothing drawn".to_owned(),
        |(a, b, c, d)| format!("bounds {a},{b}–{c},{d}"),
    );
    format!(
        "{:>7} µs, {:>3} images, {bounds}",
        micros(stats.elapsed),
        stats.images
    )
}

/// Text report: libass version, font provider start-up, font lookups per case (with libass's
/// `fontselect` lines), cold/warm render times for the probe script and an animated stretch at 60 fps.
pub fn probe(font: &[u8]) -> String {
    let mut report = String::new();
    // SAFETY: plain query.
    let version = unsafe { ass_library_version() } as u32;
    let _ = writeln!(
        report,
        "libass {:x}.{:x}.{:x} (0x{version:08X}), static, WinRT DirectWrite provider",
        version >> 28,
        (version >> 20) & 0xFF,
        (version >> 12) & 0xFF
    );
    let embedded: Vec<(&str, &[u8])> = if font.is_empty() {
        Vec::new()
    } else {
        vec![(EMBEDDED_NAME, font)]
    };
    let total = Instant::now();
    let session = match Session::new(WIDTH, HEIGHT, &embedded) {
        Ok(created) => created,
        Err(error) => {
            let _ = writeln!(report, "  {error}");
            return report;
        }
    };
    let fonts_init = session.ensure_fonts();
    let _ = writeln!(
        report,
        "  ass_set_fonts (provider start-up): {:.1} ms; embedded fonts: {}",
        fonts_init.as_secs_f64() * 1000.0,
        if font.is_empty() {
            "none".to_owned()
        } else {
            format!("{EMBEDDED_NAME}, {} bytes", font.len())
        }
    );
    append_log(&mut report, &session, "    ", 12);

    report.push_str("Font cases (first render includes lookup and fallback):\n");
    match session.track(font_case_script().as_bytes()) {
        Some(track) => {
            append_log(&mut report, &session, "    ", 4);
            for (index, (label, _, _)) in FONT_CASES.iter().enumerate() {
                let stats = session.render(&track, index as i64 * 1000 + 500, true, |_| {});
                let _ = writeln!(report, "  {label:<16} {}", describe(&stats));
                append_log(&mut report, &session, "      ", 8);
            }
        }
        None => report.push_str("  ass_read_memory failed\n"),
    }

    report.push_str(
        "Probe script 1920x1080 (cold = first render of each group, warm = 10 ms later):\n",
    );
    let Some(track) = session.track(PROBE_SCRIPT.as_bytes()) else {
        report.push_str("  ass_read_memory failed\n");
        return report;
    };
    append_log(&mut report, &session, "    ", 4);
    for ms in [1500i64, 2500, 3500, 4000, 5000, 6500, 7500] {
        let cold = session.render(&track, ms, true, |_| {});
        let warm = session.render(&track, ms + 10, true, |_| {});
        let _ = writeln!(
            report,
            "  t={:.1}s cold {} | warm {:>6} µs (changed {})",
            ms as f64 / 1000.0,
            describe(&cold),
            micros(warm.elapsed),
            warm.changed
        );
        append_log(&mut report, &session, "      ", 6);
    }

    // Karaoke, \move, \t and \fad all animate between 4 and 6 s: what playback would ask per frame.
    let frames = 120;
    let (mut sum, mut worst, mut changed) = (Duration::ZERO, Duration::ZERO, 0);
    for frame in 0..frames {
        let stats = session.render(&track, 4000 + frame * 1000 / 60, true, |_| {});
        sum += stats.elapsed;
        worst = worst.max(stats.elapsed);
        changed += i32::from(stats.changed != 0);
    }
    let _ = writeln!(
        report,
        "Animated 4.0–6.0 s at 60 fps: {frames} frames, avg {} µs, max {} µs, {changed} changed",
        micros(sum) / frames as u128,
        micros(worst)
    );
    append_log(&mut report, &session, "    ", 6);
    drop(track);
    drop(session);
    let _ = writeln!(
        report,
        "Total {:.1} ms",
        total.elapsed().as_secs_f64() * 1000.0
    );
    report
}

/// The probe script rendered at `time_ms` over a dark grey 1920x1080 frame, as a PNG.
pub fn probe_png(font: &[u8], time_ms: u32) -> Option<Vec<u8>> {
    let embedded: Vec<(&str, &[u8])> = if font.is_empty() {
        Vec::new()
    } else {
        vec![(EMBEDDED_NAME, font)]
    };
    let session = Session::new(WIDTH, HEIGHT, &embedded).ok()?;
    session.ensure_fonts();
    let track = session.track(PROBE_SCRIPT.as_bytes())?;
    let mut canvas = Canvas::new(WIDTH, HEIGHT);
    session.render(&track, i64::from(time_ms), true, |image| {
        canvas.blend(image)
    });
    Some(png::encode_rgb(
        canvas.width as u32,
        canvas.height as u32,
        &canvas.rgb,
    ))
}

#[cfg(test)]
mod tests {
    /// Runs the real probe against the CI runner's DirectWrite fonts (desktop Windows, but the same
    /// app-partition libass build the Xbox gets), so a broken link or font provider fails CI.
    #[test]
    fn probe_renders_with_system_fonts() {
        let report = super::probe(&[]);
        println!("{report}");
        assert!(
            !report.contains("ass_read_memory failed") && !report.contains("_init failed"),
            "libass probe failed:\n{report}"
        );
        let arial = report
            .lines()
            .find(|line| line.trim_start().starts_with("Arial "))
            .expect("Arial case line");
        assert!(
            !arial.contains("nothing drawn"),
            "Arial drew nothing: {arial}"
        );
        let png = super::probe_png(&[], 1500).expect("probe frame renders");
        assert!(png.len() > 1920 * 1080 * 3, "PNG too small: {}", png.len());
    }
}
