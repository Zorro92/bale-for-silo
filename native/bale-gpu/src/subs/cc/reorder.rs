//! Decode order to presentation order. The tap hands packets over as the decoder takes them, so with
//! B-frames their PTS go back and forth, while CEA-608 is a byte stream that must be decoded in
//! display order (roll-up text garbles otherwise).
//!
//! Frames are held sorted by PTS, one entry per PTS (field pairs that share one keep their arrival
//! order), and the earliest leaves when:
//! - more than [`Reorder::depth`] PTS values are held (androidx Media3's `ReorderingBufferQueue.java`,
//!   Copyright 2024 The Android Open Source Project, Apache License 2.0, changed for Bale: see
//!   `native/bale-gpu/NOTICE`; Media3 sizes it from the SPS's `max_num_reorder_frames`, which the
//!   tap does not pass: Bale starts at [`DEFAULT_DEPTH`] and grows to what the stream shows, up to
//!   [`MAX_DEPTH`]); or
//! - it is the next frame after the last one released: no more than a quarter step more than the
//!   smallest PTS step seen lately, so no frame can still come in between. That releases streams
//!   without B-frames at once and B-frame streams as soon as the frame they wait for arrives.
//!
//! A packet whose PTS was already passed (the depth was too small) is released at once, as Media3
//! does, and the depth grows by the frames it was overtaken by. A packet without a PTS joins the
//! frame that came just before it (the second field of the same picture) or, when that is gone,
//! goes out at the last time known.

use std::collections::VecDeque;

use super::extract::Triplet;

/// PTS values held before the earliest goes out, until the stream shows it needs more.
pub const DEFAULT_DEPTH: usize = 4;
/// H.264's `max_num_reorder_frames` is at most 16 (HEVC's `sps_max_num_reorder_pics` 15).
pub const MAX_DEPTH: usize = 16;
/// Recent PTS steps the frame step estimate takes the minimum of.
const STEPS: usize = 32;
/// Recent releases kept to tell how far a late packet was overtaken.
const RECENT: usize = MAX_DEPTH;

/// The caption data of one presentation time, in arrival order.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Frame {
    /// 100 ns units, the tap's timeline.
    pub pts: i64,
    pub triplets: Vec<Triplet>,
}

#[derive(Debug)]
pub struct Reorder {
    /// Ascending, one entry per PTS.
    pending: VecDeque<Frame>,
    depth: usize,
    last_out: Option<i64>,
    /// The PTS of the last packet in (decode order), for a packet without one.
    last_in: Option<i64>,
    /// Positive PTS differences between neighbours, newest last.
    steps: VecDeque<i64>,
    /// The PTS of the latest releases, newest last.
    recent: VecDeque<i64>,
    late: u64,
}

impl Default for Reorder {
    fn default() -> Self {
        Reorder {
            pending: VecDeque::new(),
            depth: DEFAULT_DEPTH,
            last_out: None,
            last_in: None,
            steps: VecDeque::with_capacity(STEPS),
            recent: VecDeque::with_capacity(RECENT),
            late: 0,
        }
    }
}

impl Reorder {
    pub fn depth(&self) -> usize {
        self.depth
    }

    /// Packets that came after their PTS had been passed.
    pub fn late(&self) -> u64 {
        self.late
    }

    /// Takes one packet's caption data (possibly none: every packet moves time on) and calls
    /// `release` with each frame that is now safe to decode, in presentation order.
    pub fn push(
        &mut self,
        pts: Option<i64>,
        triplets: Vec<Triplet>,
        release: &mut impl FnMut(Frame),
    ) {
        let Some(pts) = pts else {
            let held = self
                .last_in
                .and_then(|last| self.pending.iter_mut().find(|frame| frame.pts == last));
            match held {
                Some(frame) => frame.triplets.extend(triplets),
                None => {
                    if let Some(pts) = self.last_out.or(self.last_in) {
                        release(Frame { pts, triplets });
                    }
                }
            }
            return;
        };
        self.last_in = Some(pts);
        if let Some(last) = self.last_out
            && pts <= last
        {
            if pts < last {
                // Released too early: the frames that overtook it are how much deeper to hold.
                let overtaken = self.recent.iter().filter(|&&out| out > pts).count();
                self.depth = (self.depth + overtaken.max(1)).min(MAX_DEPTH);
                self.late += 1;
                if let Some(step) = self.recent.iter().map(|&out| (out - pts).abs()).min() {
                    self.note_step(step);
                }
            }
            release(Frame { pts, triplets });
            return;
        }
        let at = self.pending.partition_point(|frame| frame.pts < pts);
        match self.pending.get_mut(at) {
            Some(frame) if frame.pts == pts => frame.triplets.extend(triplets),
            _ => {
                let before = match at {
                    0 => self.last_out,
                    _ => Some(self.pending[at - 1].pts),
                };
                if let Some(before) = before {
                    self.note_step(pts - before);
                }
                if let Some(after) = self.pending.get(at) {
                    self.note_step(after.pts - pts);
                }
                self.pending.insert(at, Frame { pts, triplets });
            }
        }
        self.release_ready(release);
    }

    /// Drops everything held: a seek, flush or stream switch starts a new run. The learnt depth
    /// stays (it is the stream's); the step estimate is learnt again.
    pub fn flush(&mut self) {
        self.pending.clear();
        self.last_out = None;
        self.last_in = None;
        self.steps.clear();
        self.recent.clear();
    }

    /// Releases everything held, earliest first.
    pub fn drain(&mut self, release: &mut impl FnMut(Frame)) {
        while let Some(frame) = self.pending.pop_front() {
            self.released(frame.pts);
            release(frame);
        }
    }

    fn note_step(&mut self, step: i64) {
        if step <= 0 {
            return;
        }
        if self.steps.len() == STEPS {
            self.steps.pop_front();
        }
        self.steps.push_back(step);
    }

    fn released(&mut self, pts: i64) {
        self.last_out = Some(pts);
        if self.recent.len() == RECENT {
            self.recent.pop_front();
        }
        self.recent.push_back(pts);
    }

    fn release_ready(&mut self, release: &mut impl FnMut(Frame)) {
        while let Some(front) = self.pending.front() {
            let next_in_line = match (self.last_out, self.steps.iter().min()) {
                (Some(last), Some(&step)) => front.pts - last <= step + step / 4,
                _ => false,
            };
            if self.pending.len() <= self.depth && !next_in_line {
                return;
            }
            let Some(frame) = self.pending.pop_front() else {
                return;
            };
            self.released(frame.pts);
            release(frame);
        }
    }

    #[cfg(test)]
    pub(crate) fn held(&self) -> usize {
        self.pending.len()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const FRAME: i64 = 333_667; // 29.97 fps in 100 ns

    fn tagged(tag: u8) -> Vec<Triplet> {
        vec![Triplet([0xFC, tag, 0])]
    }

    /// Pushes frame indices in `order` (decode order), drains what is left and returns the frame
    /// indices in release order.
    fn run(reorder: &mut Reorder, order: &[i64]) -> Vec<i64> {
        let mut out = Vec::new();
        for &index in order {
            reorder.push(Some(index * FRAME), tagged(index as u8), &mut |frame| {
                assert_eq!(frame.triplets, tagged((frame.pts / FRAME) as u8));
                out.push(frame.pts / FRAME);
            });
        }
        reorder.drain(&mut |frame| out.push(frame.pts / FRAME));
        out
    }

    fn sorted(order: &[i64]) -> Vec<i64> {
        let mut sorted = order.to_vec();
        sorted.sort_unstable();
        sorted
    }

    #[test]
    fn frames_without_reordering_flow_after_the_first_few() {
        let mut reorder = Reorder::default();
        let mut released = Vec::new();
        for index in 0..20i64 {
            reorder.push(Some(index * FRAME), Vec::new(), &mut |frame| {
                released.push(frame.pts / FRAME)
            });
            if index >= DEFAULT_DEPTH as i64 {
                // Steady state: each packet goes out as it arrives.
                assert_eq!(released.last(), Some(&index), "frame {index}");
                assert_eq!(reorder.held(), 0);
            }
        }
        assert_eq!(released, (0..20).collect::<Vec<_>>());
        assert_eq!(reorder.late(), 0);
    }

    #[test]
    fn b_frames_and_pyramids_come_out_in_display_order() {
        // I P B B, then a pyramid I P4 B2 b1 b3, an open GOP's leading pictures and 4 consecutive B.
        let orders: [&[i64]; 4] = [
            &[0, 3, 1, 2, 6, 4, 5, 9, 7, 8, 12, 10, 11],
            &[0, 4, 2, 1, 3, 8, 6, 5, 7, 12, 10, 9, 11],
            &[3, 1, 2, 6, 4, 5, 9, 7, 8],
            &[0, 5, 1, 2, 3, 4, 10, 6, 7, 8, 9],
        ];
        for order in orders {
            let mut reorder = Reorder::default();
            assert_eq!(run(&mut reorder, order), sorted(order), "{order:?}");
            assert_eq!(reorder.late(), 0, "{order:?}");
        }
    }

    #[test]
    fn a_b_frame_is_released_as_soon_as_its_turn_comes() {
        let mut reorder = Reorder::default();
        let mut push = |index: i64| {
            let mut now = Vec::new();
            reorder.push(Some(index * FRAME), Vec::new(), &mut |frame| {
                now.push(frame.pts / FRAME)
            });
            now
        };
        for index in [0, 3, 1, 2, 6] {
            push(index);
        }
        // Warmed up: 0..=3 are out, 6 waits for 4 and 5.
        assert_eq!(push(4), [4]);
        assert_eq!(push(5), [5, 6]);
        assert_eq!(push(9), Vec::<i64>::new());
        assert_eq!(push(7), [7]);
        assert_eq!(push(8), [8, 9]);
    }

    /// Decode order of a dyadic B pyramid between the anchors `base` and `base + len`.
    fn pyramid(base: i64, len: i64) -> Vec<i64> {
        fn split(lo: i64, hi: i64, out: &mut Vec<i64>) {
            if hi - lo < 2 {
                return;
            }
            let mid = (lo + hi) / 2;
            out.push(mid);
            split(lo, mid, out);
            split(mid, hi, out);
        }
        let mut out = vec![base + len];
        split(base, base + len, &mut out);
        out
    }

    #[test]
    fn a_deep_pyramid_grows_the_depth_after_its_first_late_frames() {
        let mut order = vec![0];
        for gop in 0..3 {
            order.extend(pyramid(gop * 16, 16));
        }
        assert_eq!(&order[..6], [0, 16, 8, 4, 2, 1]);
        let mut reorder = Reorder::default();
        let out = run(&mut reorder, &order);
        assert_eq!(sorted(&out), sorted(&order));
        let late = reorder.late();
        assert!(late >= 1);
        assert!(reorder.depth() > DEFAULT_DEPTH);
        // Only the start was out of order.
        let tail = &out[16..];
        assert!(tail.windows(2).all(|pair| pair[0] < pair[1]), "{out:?}");
        // The same structure again (after a seek) is now held deep enough.
        reorder.flush();
        let again: Vec<i64> = order.iter().map(|index| index + 100).collect();
        assert_eq!(run(&mut reorder, &again), sorted(&again));
        assert_eq!(reorder.late(), late);
        // A frame overtaken by more than the depth: released at once, the depth grows past it.
        let mut reorder = Reorder::default();
        let out = run(&mut reorder, &[0, 6, 5, 4, 3, 2, 1]);
        assert_eq!(out, [0, 2, 3, 4, 5, 6, 1]);
        assert_eq!(reorder.late(), 1);
        assert_eq!(reorder.depth(), DEFAULT_DEPTH + 5);
    }

    #[test]
    fn same_pts_packets_keep_arrival_order_and_ptsless_ones_join_their_frame() {
        let mut reorder = Reorder::default();
        let mut out: Vec<Frame> = Vec::new();
        let mut release = |frame: Frame| out.push(frame);
        reorder.push(Some(0), tagged(1), &mut release);
        reorder.push(Some(0), tagged(2), &mut release);
        reorder.push(None, tagged(3), &mut release);
        reorder.push(Some(FRAME), tagged(4), &mut release);
        reorder.drain(&mut release);
        assert_eq!(out.len(), 2);
        assert_eq!(out[0].pts, 0);
        assert_eq!(out[0].triplets, [tagged(1), tagged(2), tagged(3)].concat());
        assert_eq!(out[1].triplets, tagged(4));
        // Without a held frame, a PTS-less packet goes out at the last time known.
        let mut out = Vec::new();
        reorder.push(None, tagged(5), &mut |frame| out.push(frame));
        assert_eq!(
            out,
            [Frame {
                pts: FRAME,
                triplets: tagged(5)
            }]
        );
        // Nothing known at all: dropped.
        let mut fresh = Reorder::default();
        let mut count = 0;
        fresh.push(None, tagged(6), &mut |_| count += 1);
        assert_eq!(count, 0);
    }

    #[test]
    fn flush_drops_what_is_held_and_restarts() {
        let mut reorder = Reorder::default();
        let mut out = Vec::new();
        for index in [0, 3, 1] {
            reorder.push(Some(index * FRAME), Vec::new(), &mut |frame| {
                out.push(frame.pts / FRAME)
            });
        }
        reorder.flush();
        assert_eq!(reorder.held(), 0);
        // An earlier PTS after the flush is not late.
        for index in [-10, -7, -9, -8] {
            reorder.push(Some(index * FRAME), Vec::new(), &mut |frame| {
                out.push(frame.pts / FRAME)
            });
        }
        reorder.drain(&mut |frame| out.push(frame.pts / FRAME));
        assert_eq!(out, [-10, -9, -8, -7]);
        assert_eq!(reorder.late(), 0);
    }

    #[test]
    fn irregular_steps_never_release_early() {
        // 3:2 cadence (steps of 2 and 3 fields) with one B-frame between anchors.
        let field = FRAME / 2;
        let times: Vec<i64> = [0, 5, 2, 10, 7, 15, 12, 20, 17]
            .iter()
            .map(|t| t * field)
            .collect();
        let mut reorder = Reorder::default();
        let mut out = Vec::new();
        for &pts in &times {
            reorder.push(Some(pts), Vec::new(), &mut |frame| out.push(frame.pts));
        }
        reorder.drain(&mut |frame| out.push(frame.pts));
        let mut expected = times.clone();
        expected.sort_unstable();
        assert_eq!(out, expected);
        assert_eq!(reorder.late(), 0);
    }
}
