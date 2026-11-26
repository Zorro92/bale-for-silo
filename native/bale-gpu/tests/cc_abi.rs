//! The caption ABI on every platform, without an overlay: a decoder created, registered and
//! switched between channels through the `bale_cc_*` exports, fed Annex B H.264 packets as the
//! FFmpegInteropX tap feeds them. CEA-608 carries CC1 and CC2 in field 1 (`cc_type` 0) and CC3 and
//! CC4 in field 2 (`cc_type` 1), the two channels of a field told apart by the data channel bit of
//! their control codes. One stream carries a different caption on CC1, CC3 and CC4, and each
//! channel the player can pick decodes only its own.

use bale_gpu::subs::bitmap::E_NOT_FOUND;
use bale_gpu::subs::cc::{BaleCcStats, Captions, SEEN_CC_DATA, SEEN_CC1, SEEN_CC3, SEEN_CC4};
use bale_gpu::subs::{E_INVALIDARG, E_NOTIMPL, S_OK};
use bale_gpu::{
    bale_cc_create, bale_cc_destroy, bale_cc_flush, bale_cc_push, bale_cc_register, bale_cc_seen,
    bale_cc_select, bale_cc_stats,
};

/// 30 fps in 100 ns.
const FRAME: i64 = 333_333;
const FRAMES: i64 = 60;
const TOKEN: u64 = 0xCC_0003;

fn parity(b: u8) -> u8 {
    if b.count_ones().is_multiple_of(2) {
        b | 0x80
    } else {
        b
    }
}

/// Field 1 of `frame`: CC1's pop-on "ONE" on row 15, shown at frame 10 (End of Caption); control
/// codes twice, as encoders send them.
fn field1(frame: i64) -> [u8; 2] {
    match frame {
        0 | 1 => [0x14, 0x20],
        2 | 3 => [0x14, 0x60],
        4 => *b"ON",
        5 => [b'E', 0],
        10 | 11 => [0x14, 0x2F],
        _ => [0, 0],
    }
}

/// Field 2 of `frame`: CC3's pop-on "THREE", shown at frame 20 (its miscellaneous codes on 15, the
/// field-2 form), then CC4's "FOUR" on data channel 2 (1D, and 1C for its preamble), shown at
/// frame 40. CC3's caption stays up: CC4's codes are not CC3's.
fn field2(frame: i64) -> [u8; 2] {
    match frame {
        0 | 1 => [0x15, 0x20],
        2 | 3 => [0x14, 0x60],
        4 => *b"TH",
        5 => *b"RE",
        6 => [b'E', 0],
        20 | 21 => [0x15, 0x2F],
        30 | 31 => [0x1D, 0x20],
        32 | 33 => [0x1C, 0x60],
        34 => *b"FO",
        35 => *b"UR",
        40 | 41 => [0x1D, 0x2F],
        _ => [0, 0],
    }
}

/// An Annex B H.264 packet: an A/53 SEI with `frame`'s field-1 and field-2 pairs, then a slice. No
/// two bytes of it in a row are zero, so it needs no emulation prevention.
fn packet(frame: i64) -> Vec<u8> {
    let odd = |[a, b]: [u8; 2]| [parity(a), if b == 0 { 0x80 } else { parity(b) }];
    let ([a1, b1], [a2, b2]) = (odd(field1(frame)), odd(field2(frame)));
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
    payload.extend_from_slice(&[0xFC, a1, b1, 0xFD, a2, b2, 0xFF]);
    let mut sei = vec![0x06, 0x04, payload.len() as u8];
    sei.extend(payload);
    sei.push(0x80);
    let slice = [0x65, 0x88, 0x84, 0x21];
    let mut out = Vec::new();
    for nal in [&sei[..], &slice[..]] {
        out.extend_from_slice(&[0, 0, 0, 1]);
        out.extend_from_slice(nal);
    }
    out
}

fn push(frame: i64) -> i32 {
    let data = packet(frame);
    // SAFETY: `data` is live for the call and as long as declared.
    unsafe { bale_cc_push(TOKEN, data.as_ptr(), data.len(), frame * FRAME, 1, 0) }
}

/// The stream from its start, as after a seek to 0.
fn play(from_start: bool) {
    if from_start {
        assert_eq!(bale_cc_flush(TOKEN), S_OK);
    }
    for frame in 0..FRAMES {
        assert_eq!(push(frame), S_OK, "frame {frame}");
    }
}

/// What the decoder showed and shows: each caption's start (ms) and text.
fn shown(captions: *const Captions) -> Vec<(i64, String)> {
    // SAFETY: a live handle from `bale_cc_create`, an `Arc` the test still holds.
    let captions = unsafe { &*captions };
    captions
        .cues()
        .iter()
        .map(|cue| (cue.start_ms, cue.text()))
        .collect()
}

#[test]
fn each_channel_decodes_its_own_field_and_data_channel() {
    let mut captions: *const Captions = std::ptr::null();
    // SAFETY: a writable out pointer; `captions` is a live handle until the destroy below.
    unsafe {
        assert_eq!(bale_cc_create(&mut captions), S_OK);
        assert_eq!(bale_cc_register(captions, TOKEN), S_OK);
    }
    // A new decoder is on CC1: field 1's caption only. Every channel's caption commands are seen.
    play(false);
    assert_eq!(shown(captions), [(333, "ONE".to_owned())]);
    // SAFETY: a live handle.
    let seen = unsafe { bale_cc_seen(captions) };
    assert_eq!(seen, SEEN_CC1 | SEEN_CC3 | SEEN_CC4 | SEEN_CC_DATA);

    // The player's picks, each followed by the same stream from the start: CC3 and CC4 read field
    // 2, and CC1 field 1 again. A change drops what the previous channel showed at once.
    for (channel, start_ms, text) in [(3, 667, "THREE"), (4, 1333, "FOUR"), (1, 333, "ONE")] {
        // SAFETY: a live handle.
        assert_eq!(unsafe { bale_cc_select(captions, channel, 0) }, S_OK);
        assert!(
            shown(captions).is_empty(),
            "cc{channel} kept the old channel"
        );
        play(true);
        assert_eq!(
            shown(captions),
            [(start_ms, text.to_owned())],
            "cc{channel}"
        );
    }
    // CC2 (field 1, data channel 2) carries nothing in this stream; picking the same channel again
    // keeps what it shows.
    // SAFETY: a live handle.
    unsafe {
        assert_eq!(bale_cc_select(captions, 1, 0), S_OK);
        assert_eq!(shown(captions), [(333, "ONE".to_owned())]);
        assert_eq!(bale_cc_select(captions, 2, 0), S_OK);
    }
    play(true);
    assert!(shown(captions).is_empty());

    let mut stats = BaleCcStats::default();
    // SAFETY: a live handle and a writable struct; then the handle is not used again.
    unsafe {
        assert_eq!(bale_cc_stats(captions, &mut stats), S_OK);
        // Out of range channels and CEA-708 services are refused.
        assert_eq!(bale_cc_select(captions, 0, 0), E_INVALIDARG);
        assert_eq!(bale_cc_select(captions, 5, 0), E_INVALIDARG);
        assert_eq!(bale_cc_select(captions, 3, 1), E_NOTIMPL);
        bale_cc_destroy(captions);
    }
    assert_eq!(
        (
            stats.packets,
            stats.caption_packets,
            stats.late,
            stats.flushes
        ),
        (5 * FRAMES as u64, 5 * FRAMES as u64, 0, 4)
    );
    // ONE, THREE, FOUR and ONE again.
    assert_eq!(stats.cues, 4);
    assert_eq!(stats.seen, seen);
    assert_eq!(push(0), E_NOT_FOUND);
}
