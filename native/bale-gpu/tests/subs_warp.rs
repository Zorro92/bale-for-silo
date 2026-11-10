//! Subtitle overlay end to end on WARP: load a script, render at the media clock, read back.
#![cfg(windows)]

use std::time::{Duration, Instant};

use bale_gpu::subs::bitmap::{BaleSubBitmapEvent, BaleSubBitmapRect, E_NOT_FOUND};
use bale_gpu::subs::convert::{ConvertOptions, FORMAT_SRT, FORMAT_VTT, to_ass_script};
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

const SRT: &str = "1\r\n00:00:01,000 --> 00:00:03,000\r\n{\\an8}<i>Top</i> {curly} C:\\New\r\n\r\n\
2\r\n00:00:01,500 --> 00:00:04,000\r\n<font color=\"#FFFF00\" size=\"48\">Yellow</font> &amp; white\r\n\r\n\
3\r\n00:00:09,000 --> 00:00:08,000\r\nskipped\r\n";

const VTT: &str = "WEBVTT\n\nSTYLE\n::cue(.loud) { color: red; font-weight: bold }\n\n\
NOTE not a cue\n\n\
00:01.000 --> 00:03.000 line:0 align:start\n<v Esme>Top <c.loud>left</c></v>\n\n\
intro\n00:01.500 --> 00:04.000 position:50% line:80%\n<ruby>a<rt>b</rt></ruby> {x} <00:02.000>later\n\n\
00:05.000 --> 00:06.000\nlast\n";

#[test]
fn converted_srt_and_vtt_load_through_libass() {
    // SAFETY: a null panel renders offscreen.
    let overlay = unsafe {
        Overlay::create_with_driver(std::ptr::null_mut(), WIDTH, HEIGHT, D3D_DRIVER_TYPE_WARP)
    }
    .expect("create overlay on WARP");
    assert_eq!(overlay.set_layout(Layout::full(WIDTH, HEIGHT)), S_OK);
    assert_eq!(overlay.set_clock(2000, 1.0, false), S_OK);
    let options = ConvertOptions::default();
    for (format, text, events) in [(FORMAT_SRT, SRT, 2), (FORMAT_VTT, VTT, 3)] {
        let script = to_ass_script(format, text.as_bytes(), &options).expect("known format");
        assert_eq!(overlay.load(&script), S_OK, "format {format}");
        let shown = overlay.capture().expect("capture the converted cues");
        let stats = overlay.stats();
        assert_eq!(stats.events, events, "format {format}");
        assert!(stats.images > 0, "format {format}: nothing drawn");
        // Both tracks show one cue at the top and one in the lower half at 2 s.
        assert!(covered_rows(&shown, 0..HEIGHT / 4) > 0, "format {format}");
        assert!(
            covered_rows(&shown, HEIGHT / 2..HEIGHT) > 0,
            "format {format}"
        );
    }
    assert_eq!(overlay.clear(), S_OK);
}

/// The premultiplied BGRA pixel at (`x`, `y`).
fn pixel(capture: &Capture, x: u32, y: u32) -> [u8; 4] {
    let at = (y as usize * capture.width as usize + x as usize) * 4;
    capture.bgra[at..at + 4].try_into().expect("four bytes")
}

/// Pushes one event with a single `w`×`h` rectangle at (`x`, `y`) through the C ABI.
#[allow(clippy::too_many_arguments)]
fn push_rect(
    token: u64,
    (start_ms, end_ms): (i64, i64),
    canvas: (i32, i32),
    (x, y, w, h): (i32, i32, i32, i32),
    indices: &[u8],
    palette: &[u32],
) -> i32 {
    assert_eq!(indices.len(), (w * h) as usize);
    let rect = BaleSubBitmapRect {
        x,
        y,
        w,
        h,
        linesize: w,
        nb_colors: palette.len() as i32,
        indices: indices.as_ptr(),
        palette: palette.as_ptr(),
    };
    let event = BaleSubBitmapEvent {
        start_ms,
        end_ms,
        canvas_w: canvas.0,
        canvas_h: canvas.1,
        video_w: 0,
        video_h: 0,
        rects: &rect,
        count: 1,
        reserved: 0,
    };
    // SAFETY: the event, its rectangle and their buffers are live and as large as declared.
    unsafe { bale_gpu::bale_sub_bitmap_push(token, &event) }
}

#[test]
fn bitmap_events_draw_under_the_track_and_end_on_time() {
    const TOKEN: u64 = 0x5AB_0001;
    // Opaque red, half-transparent green, and index 2 is past the palette (transparent).
    let palette = [0xFFFF_0000u32, 0x8000_FF00];
    #[rustfmt::skip]
    let indices = [
        0, 0, 0, 0, 1, 1, 1, 2,
        0, 0, 0, 0, 1, 1, 1, 2,
    ];
    let frame = (WIDTH as i32, HEIGHT as i32);
    assert_eq!(
        push_rect(
            TOKEN,
            (1000, 3000),
            frame,
            (100, 50, 8, 2),
            &indices,
            &palette
        ),
        E_NOT_FOUND,
        "nothing is bound yet"
    );

    // SAFETY: a null panel renders offscreen.
    let overlay = unsafe {
        Overlay::create_with_driver(std::ptr::null_mut(), WIDTH, HEIGHT, D3D_DRIVER_TYPE_WARP)
    }
    .expect("create overlay on WARP");
    assert_eq!(overlay.set_layout(Layout::full(WIDTH, HEIGHT)), S_OK);
    assert_eq!(overlay.set_clock(2000, 1.0, false), S_OK);
    assert_eq!(overlay.bind_bitmaps(TOKEN), S_OK);
    assert_eq!(
        push_rect(
            TOKEN,
            (1000, 3000),
            frame,
            (100, 50, 8, 2),
            &indices,
            &palette
        ),
        S_OK
    );

    let shown = overlay.capture().expect("capture the bitmap");
    assert_eq!(pixel(&shown, 100, 50), [0, 0, 255, 255], "red, as BGRA");
    assert_eq!(pixel(&shown, 103, 51), [0, 0, 255, 255]);
    assert_eq!(
        pixel(&shown, 104, 50),
        [0, 128, 0, 128],
        "premultiplied green"
    );
    assert_eq!(pixel(&shown, 107, 50), [0; 4], "index past the palette");
    assert_eq!(pixel(&shown, 99, 50), [0; 4]);
    assert_eq!(pixel(&shown, 100, 52), [0; 4]);
    assert_eq!(covered_rows(&shown, 0..HEIGHT), 2);
    let stats = overlay.stats();
    assert_eq!((stats.events, stats.images), (1, 1));

    // An unchanged picture is not presented again.
    let presents = overlay.stats().presents;
    assert_eq!(overlay.set_clock(2500, 1.0, false), S_OK);
    std::thread::sleep(Duration::from_millis(200));
    assert_eq!(overlay.stats().presents, presents);

    // A text track is drawn as well, on top; the stats stay those of the bitmaps.
    assert_eq!(overlay.load(SCRIPT.as_bytes()), S_OK);
    let both = overlay.capture().expect("capture bitmap and line");
    assert_eq!(pixel(&both, 100, 50), [0, 0, 255, 255]);
    assert!(covered_rows(&both, HEIGHT / 2..HEIGHT) > 0, "no line");
    assert_eq!(overlay.stats().images, 1);
    assert_eq!(overlay.clear(), S_OK);

    // At its end the event is gone from the screen and from the queue.
    assert_eq!(overlay.set_clock(3000, 1.0, false), S_OK);
    let gone = overlay.capture().expect("capture after the event");
    assert_eq!(covered_rows(&gone, 0..HEIGHT), 0, "bitmap still shown");
    let stats = overlay.stats();
    assert_eq!((stats.events, stats.images), (0, 0));

    // A canvas of half the frame size is scaled up; an open-ended event stays until a clear.
    let white = [0xFFFF_FFFFu32];
    assert_eq!(
        push_rect(
            TOKEN,
            (4000, -1),
            (320, 180),
            (10, 10, 2, 2),
            &[0; 4],
            &white
        ),
        S_OK
    );
    assert_eq!(overlay.set_clock(60_000, 1.0, false), S_OK);
    let scaled = overlay.capture().expect("capture the scaled bitmap");
    assert_eq!(pixel(&scaled, 21, 21), [255; 4]);
    assert_eq!(pixel(&scaled, 22, 22), [255; 4]);
    assert_eq!(pixel(&scaled, 25, 21)[3], 0);
    assert_eq!(covered_rows(&scaled, 0..HEIGHT), 4);

    // Flush (a seek) shows nothing.
    assert_eq!(bale_gpu::bale_sub_bitmap_flush(TOKEN), S_OK);
    let flushed = overlay.capture().expect("capture after the flush");
    assert_eq!(covered_rows(&flushed, 0..HEIGHT), 0, "flush left a bitmap");
    assert_eq!(overlay.stats().events, 0);

    // Unbound, and later destroyed, overlays take no more events.
    assert_eq!(overlay.bind_bitmaps(0), S_OK);
    assert_eq!(
        push_rect(TOKEN, (4000, -1), frame, (0, 0, 2, 2), &[0; 4], &white),
        E_NOT_FOUND
    );
    assert_eq!(overlay.bind_bitmaps(TOKEN), S_OK);
    assert_eq!(
        push_rect(TOKEN, (4000, -1), frame, (0, 0, 2, 2), &[0; 4], &white),
        S_OK
    );
    drop(overlay);
    assert_eq!(bale_gpu::bale_sub_bitmap_flush(TOKEN), E_NOT_FOUND);
}
