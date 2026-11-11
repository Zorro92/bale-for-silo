use super::*;
use crate::subs::{Layout, S_OK};

#[path = "../../tests/common/pgs.rs"]
mod fixture;
use fixture::{fixture, object, pcs, segment};

fn queue(data: &[u8]) -> bitmap::Queue {
    let mut q = bitmap::Queue::default();
    q.replace(decode(data, 6000, Some((1920, 1040))).unwrap());
    q
}

#[test]
fn display_sets_use_pcs_time_palette_forced_placement_and_explicit_clear() {
    let data = fixture();
    let events = decode(&data, 6000, Some((1920, 1040))).unwrap();
    assert_eq!(events.len(), 2);
    let e = &events[0];
    assert_eq!(e.start_ms, 1000);
    assert_eq!(e.canvas, Some((1920, 1080)));
    assert_eq!(e.video, Some((1920, 1040)));
    let rect = &e.rects[0];
    assert!(rect.forced);
    assert_eq!((rect.x, rect.y, rect.w, rect.h), (10, 20, 3, 2));
    assert_eq!(
        rect.rgba(),
        [
            255, 255, 255, 255, 0, 0, 0, 128, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255
        ]
    );
    assert_eq!(
        e.placement(&Layout::full(1920, 1040)).rect(10, 20, 3, 2),
        [10.0, 0.0, 3.0, 2.0]
    );
    let mut q = queue(&data);
    assert!(q.visible(999).is_none());
    assert!(q.visible(1000).is_some());
    assert!(q.visible(2999).is_some());
    assert!(q.visible(3000).is_none());
    assert_eq!(q.next_boundary(999), Some(1000));
    assert_eq!(q.next_boundary(1000), Some(3000));
    q.prune(5000); // Sidecar history survives render-thread prune and both seek directions.
    assert!(q.visible(1500).is_some());
    assert!(q.visible(6000).is_none());
}

#[test]
fn all_rle_escape_forms_and_transparent_line_padding() {
    let data = [5, 0, 2, 0, 0x40, 2, 0, 0x82, 7, 0, 0xc0, 2, 9, 0, 0];
    assert_eq!(rle(&data, 9, 1).unwrap(), [5, 0, 0, 0, 0, 7, 7, 9, 9]);
    assert_eq!(rle(&[1, 0, 0, 2, 0, 0], 3, 2).unwrap(), [1, 0, 0, 2, 0, 0]);
    assert_eq!(rle(&[1, 1], 2, 1).unwrap(), [1, 1]);
    for data in [&[0][..], &[0, 0x40], &[0, 0x80], &[0, 0x83, 1], &[1]] {
        assert!(rle(data, 2, 1).is_err(), "{data:?}");
    }
}

#[test]
fn fragmented_objects_partial_palette_updates_and_reused_objects() {
    let mut data = pcs(1000, 0x80, &[0, 7, 0, 0, 0, 10, 0, 20], 1);
    data.extend(segment(0x14, 900, &[0, 0, 1, 235, 128, 128, 255]));
    // First ODS: 4 bytes total RLE, only 2 here. Continuation's version must match.
    data.extend(segment(
        0x15,
        900,
        &[0, 7, 0, 0x80, 0, 0, 8, 0, 2, 0, 1, 1, 1],
    ));
    data.extend(segment(0x15, 900, &[0, 7, 0, 0x40, 0, 0]));
    data.extend(segment(0x80, 900, &[]));
    data.extend(pcs(2000, 0, &[0, 7, 0, 0, 0, 10, 0, 20], 1));
    data.extend(segment(0x14, 1900, &[0, 1, 1, 16, 128, 128, 255]));
    data.extend(segment(0x80, 1900, &[]));
    let events = decode(&data, 6000, None).unwrap();
    assert_eq!(events[0].rects[0].rgba(), [255; 8]);
    assert_eq!(events[1].rects[0].rgba(), [0, 0, 0, 255, 0, 0, 0, 255]);
    assert!(!events[0].rects[0].forced);
    let q = queue(&data);
    assert_eq!(q.visible(2500).unwrap().start_ms, 2000);
    assert!(q.visible(6000).is_none());
}

#[test]
fn pcs_crop_and_wds_clip_keep_canvas_coordinates() {
    let mut data = pcs(
        1000,
        0x80,
        &[0, 7, 0, 0x80, 0, 10, 0, 20, 0, 1, 0, 0, 0, 2, 0, 2],
        1,
    );
    data.extend(segment(0x17, 900, &[1, 0, 0, 11, 0, 20, 0, 1, 0, 2]));
    data.extend(segment(0x14, 900, &[0, 0, 1, 235, 128, 128, 255]));
    data.extend(object(900, 7, 3, 2, &[1, 2, 1, 0, 0, 1, 2, 1, 0, 0]));
    data.extend(segment(0x80, 900, &[]));
    let events = decode(&data, 6000, None).unwrap();
    let rect = &events[0].rects[0];
    assert_eq!((rect.x, rect.y, rect.w, rect.h), (11, 20, 1, 2));
    assert_eq!(rect.rgba(), [255; 8]);
}

#[test]
fn acquisition_resets_resources_and_orphan_window_prefix_is_ignored() {
    let mut data = segment(0x15, 100, &[0, 7, 0, 0x40, 1, 1]);
    data.extend(segment(0x80, 100, &[]));
    data.extend(fixture());
    data.extend(pcs(4000, 0x40, &[0, 7, 0, 0, 0, 0, 0, 0], 1));
    data.extend(segment(0x80, 4000, &[]));
    let events = decode(&data, 6000, None).unwrap();
    assert_eq!(events.len(), 3);
    assert!(events[2].rects.is_empty());
}

#[test]
fn palette_ycbcr_matches_limited_range_sd_and_hd_and_keeps_alpha() {
    assert_eq!(color(16, 128, 128, 255, true), 0xff000000);
    assert_eq!(color(235, 128, 128, 128, true), 0x80ffffff);
    assert_eq!(color(81, 240, 90, 255, false), 0xfffe0000);
    assert_eq!(color(63, 240, 102, 255, true), 0xffff0100);
    assert_eq!(color(0, 0, 0, 0, true) >> 24, 0);
}

#[test]
fn malformed_segments_and_truncations_never_panic() {
    let valid = fixture();
    for len in 0..valid.len() {
        assert!(std::panic::catch_unwind(|| decode(&valid[..len], 6000, None)).is_ok());
    }
    for i in 0..valid.len() {
        for byte in [0, 1, 0x7f, 0xff] {
            let mut data = valid.clone();
            data[i] = byte;
            assert!(std::panic::catch_unwind(|| decode(&data, 6000, None)).is_ok());
        }
    }
    assert!(decode(b"not sup", 6000, None).is_err());
    assert!(decode(&segment(0x19, 0, &[]), 6000, None).is_err());
    assert!(decode(&pcs(0, 0x80, &[], 255), 6000, None).is_err());
    assert!(decode(&vec![0; MAX_INPUT + 1], 6000, None).is_err());
    assert!(decode(&valid, 0, None).is_err());
    let oversized = segment(0x15, 0, &[0, 7, 0, 0xc0, 0xff, 0xff, 0xff, 0, 1, 0, 1, 1]);
    assert!(decode(&oversized, 6000, None).is_err());
}

#[test]
fn event_and_decoded_memory_limits_fail_instead_of_evicting_early_cues() {
    let mut many = Vec::new();
    for i in 0..=MAX_EVENTS {
        many.extend(pcs(i as u32, 0, &[], 0));
        many.extend(segment(0x80, i as u32, &[]));
    }
    assert!(decode(&many, 10_000, None).is_err());
    let mut data = pcs(1000, 0x80, &[0, 7, 0, 0, 0, 0, 0, 0], 1);
    data.extend(segment(0x14, 900, &[0, 0, 1, 235, 128, 128, 255]));
    let mut rle = Vec::new();
    for _ in 0..4096 {
        rle.extend_from_slice(&[0, 0xd0, 0, 1, 0, 0]);
    }
    data.extend(object(900, 7, 4096, 4096, &rle));
    data.extend(segment(0x80, 900, &[]));
    for i in 2..7 {
        data.extend(pcs(i * 1000, 0, &[0, 7, 0, 0, 0, 0, 0, 0], 1));
        data.extend(segment(0x80, i * 1000, &[]));
    }
    assert!(decode(&data, 10_000, None).is_err());
}

#[test]
fn decode_handle_checks_bounds_and_retired_token_cannot_publish() {
    let data = fixture();
    let mut handle = std::ptr::null_mut();
    unsafe {
        assert_eq!(
            crate::bale_sub_pgs_decode(data.as_ptr(), data.len(), 6000, 1920, 1080, &mut handle),
            S_OK
        );
        assert!(!handle.is_null());
        assert_eq!(
            crate::bale_sub_pgs_bind(0, handle, 1500),
            bitmap::E_NOT_FOUND
        );
        crate::bale_sub_pgs_destroy(handle);
        assert_eq!(
            crate::bale_sub_pgs_decode(std::ptr::null(), 0, 6000, 0, 0, &mut handle),
            S_OK
        );
        crate::bale_sub_pgs_destroy(handle);
        assert_eq!(
            crate::bale_sub_pgs_decode(std::ptr::null(), 1, 6000, 0, 0, &mut handle),
            E_INVALIDARG
        );
        assert!(handle.is_null());
    }
}

#[test]
fn two_composition_objects_keep_individual_placement_and_forced_flags() {
    let mut data = pcs(
        1000,
        0x80,
        &[0, 7, 0, 0, 0, 10, 0, 20, 0, 8, 1, 0x40, 0, 30, 0, 40],
        2,
    );
    data.extend(segment(0x14, 900, &[0, 0, 1, 235, 128, 128, 255]));
    data.extend(object(900, 7, 1, 1, &[1, 0, 0]));
    data.extend(object(900, 8, 1, 1, &[1, 0, 0]));
    data.extend(segment(0x80, 900, &[]));
    let events = decode(&data, 6000, None).unwrap();
    let rects = &events[0].rects;
    assert_eq!(rects.len(), 2);
    assert_eq!((rects[0].x, rects[0].y, rects[0].forced), (10, 20, false));
    assert_eq!((rects[1].x, rects[1].y, rects[1].forced), (30, 40, true));
}

#[test]
fn overlap_preserves_long_holds_until_the_authored_clear_without_retaining_unbounded_history() {
    let events = decode(&fixture(), 6000, None).unwrap();
    let mut q = bitmap::Queue::default();
    q.replace(events.clone());
    // The next positioned extract starts inside this composition and contains only its clear.
    q.replace_window(vec![events[1].clone()], 2000, 10_000);
    assert!(q.visible(2500).is_some());
    assert!(q.visible(3000).is_none());
    assert_eq!(q.len(), 2);
    // Without an authored clear, extend the hold beyond the previous window's synthetic cap.
    q.replace(vec![events[0].clone()]);
    q.replace_window(vec![], 5000, 12_000);
    assert!(q.visible(7000).is_some());
    assert!(q.visible(12_000).is_none());
    q.replace_window(vec![], 8000, 15_000);
    assert_eq!(q.len(), 1);
    assert!(q.visible(14_000).is_some());
    // A known clear in the new stream always wins over the previous picture.
    q.replace_window(vec![events[1].clone()], 9000, 15_000);
    assert!(q.visible(9000).is_none());
}
