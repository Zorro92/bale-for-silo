//! Closed captions end to end on WARP through the caption ABI: a decoder registered under a token
//! and attached to the overlay, H.264 packets carrying CEA-608 pushed as the FFmpegInteropX tap
//! pushes them, the caption drawn at the media clock, gone after its erase and after a flush; and
//! in a boxed appearance, rows with different indents whose backgrounds meet without overlapping.
#![cfg(windows)]

use bale_gpu::subs::bitmap::E_NOT_FOUND;
use bale_gpu::subs::cc::{BaleCcStats, Captions, SEEN_CC_DATA, SEEN_CC1};
use bale_gpu::subs::convert::TextStyle;
use bale_gpu::subs::overlay::{Capture, Overlay};
use bale_gpu::subs::{Layout, S_OK};
use bale_gpu::{
    bale_cc_attach, bale_cc_create, bale_cc_destroy, bale_cc_detach, bale_cc_flush, bale_cc_push,
    bale_cc_register, bale_cc_seen, bale_cc_stats,
};
use windows::Win32::Graphics::Direct3D::D3D_DRIVER_TYPE_WARP;

const WIDTH: u32 = 640;
const HEIGHT: u32 = 360;
/// 30 fps in 100 ns.
const FRAME: i64 = 333_333;
const TOKEN: u64 = 0xCC_0001;

/// Rows `rows` of `capture` that hold at least one pixel with alpha.
fn covered_rows(capture: &Capture, rows: std::ops::Range<u32>) -> usize {
    let row_bytes = capture.width as usize * 4;
    rows.filter(|&y| {
        let row = &capture.bgra[y as usize * row_bytes..][..row_bytes];
        row.iter().skip(3).step_by(4).any(|&alpha| alpha > 0)
    })
    .count()
}

fn parity(b: u8) -> u8 {
    if b.count_ones().is_multiple_of(2) {
        b | 0x80
    } else {
        b
    }
}

/// The field-1 pair of `frame`: a pop-on "HELLO" on row 15, shown at frame 20 (End of Caption)
/// and erased at frame 50 (Erase Displayed Memory); control codes twice, as encoders send them.
fn pair_at(frame: i64) -> [u8; 2] {
    let [a, b] = match frame {
        0 | 1 => [0x14, 0x20],
        2 | 3 => [0x14, 0x60],
        4 => *b"HE",
        5 => *b"LL",
        6 => [b'O', 0],
        20 | 21 => [0x14, 0x2F],
        50 | 51 => [0x14, 0x2C],
        _ => return [0x80, 0x80],
    };
    [parity(a), if b == 0 { 0x80 } else { parity(b) }]
}

/// An avcC (4-byte lengths) H.264 packet: an A/53 SEI with one field-1 and one null field-2
/// pair, then a slice. No byte pair of it is zero, so it needs no emulation prevention.
fn packet(frame: i64) -> Vec<u8> {
    packet_with(pair_at(frame))
}

fn packet_with([a, b]: [u8; 2]) -> Vec<u8> {
    let mut payload = vec![
        0xB5,
        0x00,
        0x31,
        b'G',
        b'A',
        b'9',
        b'4',
        0x03,
        0x40 | 2,
        0xFF,
    ];
    payload.extend_from_slice(&[0xFC, a, b, 0xFD, 0x80, 0x80, 0xFF]);
    let mut sei = vec![0x06, 0x04, payload.len() as u8];
    sei.extend(payload);
    sei.push(0x80);
    let slice = [0x65, 0x88, 0x84, 0x21];
    let mut out = Vec::new();
    for nal in [&sei[..], &slice[..]] {
        out.extend_from_slice(&(nal.len() as u32).to_be_bytes());
        out.extend_from_slice(nal);
    }
    out
}

fn push(frame: i64) -> i32 {
    let data = packet(frame);
    // SAFETY: `data` is live for the call and as long as declared.
    unsafe { bale_cc_push(TOKEN, data.as_ptr(), data.len(), frame * FRAME, 1, 4) }
}

#[test]
fn captions_draw_through_the_live_track() {
    // SAFETY: a null panel renders offscreen.
    let overlay = unsafe {
        Overlay::create_with_driver(std::ptr::null_mut(), WIDTH, HEIGHT, D3D_DRIVER_TYPE_WARP)
    }
    .expect("create overlay on WARP");
    assert_eq!(overlay.set_layout(Layout::full(WIDTH, HEIGHT)), S_OK);
    assert_eq!(overlay.set_clock(1000, 1.0, false), S_OK);
    let mut captions: *const Captions = std::ptr::null();
    // SAFETY: a writable out pointer; `captions` is a live handle until the destroy below, and
    // `overlay` outlives every call that is handed it.
    unsafe {
        assert_eq!(bale_cc_create(&mut captions), S_OK);
        assert_eq!(bale_cc_register(captions, TOKEN), S_OK);
        let overlay = (&*overlay as *const Overlay).cast_mut();
        assert_eq!(
            bale_cc_attach(captions, overlay, std::ptr::null(), std::ptr::null(), 0, 0),
            S_OK
        );
    }
    for frame in 0..60 {
        assert_eq!(push(frame), S_OK, "frame {frame}");
    }
    let shown = overlay.capture().expect("capture the caption");
    assert!(covered_rows(&shown, HEIGHT / 2..HEIGHT) > 0, "no caption");
    assert_eq!(covered_rows(&shown, 0..HEIGHT / 2), 0, "row 15 sits low");
    assert_eq!(overlay.stats().events, 1);
    // Before its End of Caption (667 ms) and after its erase (1667 ms) nothing shows.
    assert_eq!(overlay.set_clock(500, 1.0, false), S_OK);
    let before = overlay.capture().expect("capture before");
    assert_eq!(covered_rows(&before, 0..HEIGHT), 0, "shown early");
    assert_eq!(overlay.set_clock(2000, 1.0, false), S_OK);
    let after = overlay.capture().expect("capture after");
    assert_eq!(covered_rows(&after, 0..HEIGHT), 0, "still shown");
    // A seek flushes the stream: the live track empties.
    assert_eq!(overlay.set_clock(1000, 1.0, false), S_OK);
    assert_eq!(bale_cc_flush(TOKEN), S_OK);
    let flushed = overlay.capture().expect("capture after the flush");
    assert_eq!(covered_rows(&flushed, 0..HEIGHT), 0, "a flush leaves it up");
    assert_eq!(overlay.stats().events, 0);
    let mut stats = BaleCcStats::default();
    // SAFETY: a live handle and a writable struct.
    unsafe {
        assert_eq!(bale_cc_stats(captions, &mut stats), S_OK);
        assert_eq!(bale_cc_seen(captions), SEEN_CC1 | SEEN_CC_DATA);
    }
    assert_eq!((stats.packets, stats.cues, stats.flushes), (60, 1, 1));
    // The same stream again after the seek, then detached: the caption goes with the track.
    for frame in 0..30 {
        assert_eq!(push(frame), S_OK);
    }
    assert!(covered_rows(&overlay.capture().expect("capture again"), 0..HEIGHT) > 0);
    // SAFETY: a live handle.
    assert_eq!(unsafe { bale_cc_detach(captions) }, S_OK);
    assert_eq!(
        covered_rows(&overlay.capture().expect("capture detached"), 0..HEIGHT),
        0
    );
    // SAFETY: the handle is not used again.
    unsafe { bale_cc_destroy(captions) };
    assert_eq!(push(0), E_NOT_FOUND);
    assert_eq!(overlay.clear(), S_OK);
}

/// The field-1 pair of `frame` for a pop-on caption of two rows that start in different columns, as
/// on the console: row 14 indented to column 4 ("AB CD"), row 15 at column 0 ("EF GH"), shown at
/// frame 20.
fn indented_rows_at(frame: i64) -> [u8; 2] {
    let [a, b] = match frame {
        0 | 1 => [0x14, 0x20],
        // PAC row 14, indent 4 (white).
        2 | 3 => [0x14, 0x52],
        4 => *b"AB",
        5 => *b" C",
        6 => [b'D', 0],
        // PAC row 15, column 0.
        7 | 8 => [0x14, 0x60],
        9 => *b"EF",
        10 => *b" G",
        11 => [b'H', 0],
        20 | 21 => [0x14, 0x2F],
        _ => return [0x80, 0x80],
    };
    [parity(a), if b == 0 { 0x80 } else { parity(b) }]
}

/// Premultiplied BGRA of `capture` at (`x`, `y`).
fn pixel(capture: &Capture, x: u32, y: u32) -> [u8; 4] {
    let at = (y as usize * capture.width as usize + x as usize) * 4;
    capture.bgra[at..at + 4].try_into().expect("four bytes")
}

/// A pixel of the blue box with nothing over it: blue alone, any alpha.
fn is_box(pixel: [u8; 4]) -> bool {
    let [blue, green, red, alpha] = pixel;
    blue > 0 && green == 0 && red == 0 && alpha > 0
}

#[test]
fn rows_with_different_indents_have_backgrounds_that_meet_without_overlapping() {
    const SIZE: u32 = 1920;
    const LINES: u32 = 1080;
    const ROWS_TOKEN: u64 = 0xCC_0002;
    // SAFETY: a null panel renders offscreen.
    let overlay = unsafe {
        Overlay::create_with_driver(std::ptr::null_mut(), SIZE, LINES, D3D_DRIVER_TYPE_WARP)
    }
    .expect("create overlay on WARP");
    // The 1920x1080 script on a 1920x1080 picture: script pixels are pixels.
    assert_eq!(overlay.set_layout(Layout::full(SIZE, LINES)), S_OK);
    assert_eq!(overlay.set_clock(1000, 1.0, false), S_OK);
    // Bale.Core SubtitleTextStyle's Box at Large (60 script pixels, padding 7.5), white text on a
    // blue box at half opacity (ASS alpha 0x80), so one box over another would show a darker band.
    let style = TextStyle {
        font_size: 60,
        primary_colour: 0x00FF_FFFF,
        outline_colour: 0,
        back_colour: 0x80FF_0000,
        border_style: 4,
        outline: 0.0,
        shadow: 7.5,
        alignment: 2,
        margin_v: 54,
    };
    let mut captions: *const Captions = std::ptr::null();
    // SAFETY: a writable out pointer; `captions` is a live handle until the destroy below, `style`
    // is read during the call, and `overlay` outlives every call that is handed it.
    unsafe {
        assert_eq!(bale_cc_create(&mut captions), S_OK);
        assert_eq!(bale_cc_register(captions, ROWS_TOKEN), S_OK);
        let overlay = (&*overlay as *const Overlay).cast_mut();
        assert_eq!(
            bale_cc_attach(captions, overlay, &style, std::ptr::null(), 0, 0),
            S_OK
        );
    }
    for frame in 0..40 {
        let data = packet_with(indented_rows_at(frame));
        // SAFETY: `data` is live for the call and as long as declared.
        let hr =
            unsafe { bale_cc_push(ROWS_TOKEN, data.as_ptr(), data.len(), frame * FRAME, 1, 4) };
        assert_eq!(hr, S_OK, "frame {frame}");
    }
    let shown = overlay.capture().expect("capture the caption");
    // A box and a text event per row.
    assert_eq!(overlay.stats().events, 4);
    // The pixel rows holding box pixels: the two rows' bands, 60 each (the text's line height is
    // taller than a 608 row), edge to edge from row 14's top (852) to row 15's bottom (972).
    let boxed: Vec<u32> = (0..LINES)
        .filter(|&y| (0..SIZE).any(|x| is_box(pixel(&shown, x, y))))
        .collect();
    assert_eq!(boxed.first(), Some(&852), "row 14's band starts at its top");
    assert_eq!(
        boxed.last(),
        Some(&971),
        "row 15's band ends at the safe area"
    );
    assert_eq!(boxed.len(), 120, "no gap between the bands");
    // One alpha everywhere: where two boxes overlapped, blue at half alpha twice is about 191.
    let doubled = (0..LINES)
        .flat_map(|y| (0..SIZE).map(move |x| (x, y)))
        .filter(|&(x, y)| {
            let pixel = pixel(&shown, x, y);
            is_box(pixel) && pixel[3] > 140
        })
        .count();
    assert_eq!(doubled, 0, "a band where both rows' boxes overlap");
    // Each band is its own row's box: row 14's starts at its indent (column 4, x = 384, less the
    // padding), row 15's at column 0 (x = 192).
    let left = |y: u32| (0..SIZE).find(|&x| is_box(pixel(&shown, x, y)));
    assert!(
        left(860).is_some_and(|x| x.abs_diff(376) <= 2),
        "{:?}",
        left(860)
    );
    assert!(
        left(964).is_some_and(|x| x.abs_diff(184) <= 2),
        "{:?}",
        left(964)
    );
    // SAFETY: the handle is not used again.
    unsafe { bale_cc_destroy(captions) };
    assert_eq!(overlay.clear(), S_OK);
}

/// The field-1 pair of `frame` for a pop-on caption on row 15 whose "CD" flashes: "AB", Flash On
/// (a space), "CD", shown at frame 20.
fn flashing_at(frame: i64) -> [u8; 2] {
    let [a, b] = match frame {
        0 | 1 => [0x14, 0x20],
        2 | 3 => [0x14, 0x60],
        4 => *b"AB",
        // Flash On.
        5 | 6 => [0x14, 0x28],
        7 => *b"CD",
        20 | 21 => [0x14, 0x2F],
        _ => return [0x80, 0x80],
    };
    [parity(a), if b == 0 { 0x80 } else { parity(b) }]
}

#[test]
fn flashing_characters_blink_and_their_box_stays() {
    const SIZE: u32 = 1920;
    const LINES: u32 = 1080;
    const FLASH_TOKEN: u64 = 0xCC_0003;
    // SAFETY: a null panel renders offscreen.
    let overlay = unsafe {
        Overlay::create_with_driver(std::ptr::null_mut(), SIZE, LINES, D3D_DRIVER_TYPE_WARP)
    }
    .expect("create overlay on WARP");
    assert_eq!(overlay.set_layout(Layout::full(SIZE, LINES)), S_OK);
    let style = TextStyle {
        font_size: 60,
        primary_colour: 0x00FF_FFFF,
        outline_colour: 0,
        back_colour: 0x80FF_0000,
        border_style: 4,
        outline: 0.0,
        shadow: 7.5,
        alignment: 2,
        margin_v: 54,
    };
    let mut captions: *const Captions = std::ptr::null();
    // SAFETY: as in the test above.
    unsafe {
        assert_eq!(bale_cc_create(&mut captions), S_OK);
        assert_eq!(bale_cc_register(captions, FLASH_TOKEN), S_OK);
        let overlay = (&*overlay as *const Overlay).cast_mut();
        assert_eq!(
            bale_cc_attach(captions, overlay, &style, std::ptr::null(), 0, 0),
            S_OK
        );
    }
    for frame in 0..40 {
        let data = packet_with(flashing_at(frame));
        // SAFETY: `data` is live for the call and as long as declared.
        let hr =
            unsafe { bale_cc_push(FLASH_TOKEN, data.as_ptr(), data.len(), frame * FRAME, 1, 4) };
        assert_eq!(hr, S_OK, "frame {frame}");
    }
    let white = |capture: &Capture| {
        (0..LINES)
            .flat_map(|y| (0..SIZE).map(move |x| (x, y)))
            .filter(|&(x, y)| {
                let [_, green, red, _] = pixel(capture, x, y);
                green > 200 && red > 200
            })
            .count()
    };
    let box_rows = |capture: &Capture| {
        (0..LINES)
            .filter(|&y| (0..SIZE).any(|x| is_box(pixel(capture, x, y))))
            .count()
    };
    // The caption is up from about 667 ms. 1250 ms is in an even half second (shown), 1750 ms in
    // an odd one (its flashing characters gone).
    assert_eq!(overlay.set_clock(1250, 1.0, false), S_OK);
    let shown = overlay.capture().expect("capture shown");
    assert_eq!(overlay.set_clock(1750, 1.0, false), S_OK);
    let hidden = overlay.capture().expect("capture hidden");
    assert_eq!(overlay.set_clock(2250, 1.0, false), S_OK);
    let again = overlay.capture().expect("capture shown again");
    let (on, off) = (white(&shown), white(&hidden));
    assert!(off > 0, "AB stays");
    assert!(
        on > off + off / 2,
        "CD blinks: {on} white pixels shown, {off} hidden"
    );
    assert_eq!(white(&again), on);
    // The background does not blink: the same band either way.
    assert_eq!(box_rows(&shown), 60);
    assert_eq!(box_rows(&hidden), 60);
    // SAFETY: the handle is not used again.
    unsafe { bale_cc_destroy(captions) };
    assert_eq!(overlay.clear(), S_OK);
}
