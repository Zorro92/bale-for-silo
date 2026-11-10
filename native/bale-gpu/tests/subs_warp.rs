//! Subtitle overlay end to end on WARP: load a script, render at the media clock, read back.
#![cfg(windows)]

use std::time::{Duration, Instant};

use bale_gpu::subs::overlay::{Capture, Overlay};
use bale_gpu::subs::{Layout, S_OK};
use windows::Win32::Graphics::Direct3D::D3D_DRIVER_TYPE_WARP;

const WIDTH: u32 = 640;
const HEIGHT: u32 = 360;

const SCRIPT: &str = "[Script Info]
ScriptType: v4.00+
PlayResX: 640
PlayResY: 360

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Arial,36,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,20,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,{\\an2}Overlay test line
";

/// Rows `rows` of `capture` that hold at least one pixel with alpha.
fn covered_rows(capture: &Capture, rows: std::ops::Range<u32>) -> usize {
    let row_bytes = capture.width as usize * 4;
    rows.filter(|&y| {
        let row = &capture.bgra[y as usize * row_bytes..][..row_bytes];
        row.iter().skip(3).step_by(4).any(|&alpha| alpha > 0)
    })
    .count()
}

fn wait_for_present(overlay: &Overlay, after: u64) {
    let deadline = Instant::now() + Duration::from_secs(30);
    while overlay.stats().presents <= after {
        assert!(Instant::now() < deadline, "no present within 30 s");
        std::thread::sleep(Duration::from_millis(10));
    }
}

#[test]
fn renders_a_line_at_the_clock_and_clears_after_it() {
    // SAFETY: a null panel renders offscreen.
    let overlay = unsafe {
        Overlay::create_with_driver(std::ptr::null_mut(), WIDTH, HEIGHT, D3D_DRIVER_TYPE_WARP)
    }
    .expect("create overlay on WARP");
    assert_eq!(overlay.set_layout(Layout::full(WIDTH, HEIGHT)), S_OK);
    assert_eq!(overlay.set_clock(2000, 1.0, false), S_OK);
    let before = overlay.stats().presents;
    assert_eq!(overlay.load(SCRIPT.as_bytes()), S_OK);
    wait_for_present(&overlay, before);

    let shown = overlay.capture().expect("capture the shown line");
    assert_eq!((shown.width, shown.height), (WIDTH, HEIGHT));
    assert!(covered_rows(&shown, HEIGHT / 2..HEIGHT) > 0, "no line");
    assert_eq!(covered_rows(&shown, 0..HEIGHT / 4), 0, "pixels on top");
    let stats = overlay.stats();
    assert_eq!((stats.width, stats.height), (WIDTH, HEIGHT));
    assert_eq!(stats.events, 1);
    assert!(stats.images > 0 && stats.renders > 0);

    assert_eq!(overlay.set_clock(5000, 1.0, false), S_OK);
    let gone = overlay.capture().expect("capture after the line");
    assert_eq!(covered_rows(&gone, 0..HEIGHT), 0, "line still shown");
    assert_eq!(overlay.stats().images, 0);

    assert_eq!(
        overlay.load(b"not a subtitle script"),
        bale_gpu::subs::E_INVALIDARG
    );
    assert_eq!(overlay.clear(), S_OK);
}
