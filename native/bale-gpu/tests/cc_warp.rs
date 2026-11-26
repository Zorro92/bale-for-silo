//! Closed captions end to end on WARP through the caption ABI: a decoder registered under a token
//! and attached to the overlay, H.264 packets carrying CEA-608 pushed as the FFmpegInteropX tap
//! pushes them, the caption drawn at the media clock, gone after its erase and after a flush.
#![cfg(windows)]

use bale_gpu::subs::bitmap::E_NOT_FOUND;
use bale_gpu::subs::cc::{BaleCcStats, Captions, SEEN_CC_DATA, SEEN_CC1};
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
    let [a, b] = pair_at(frame);
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
