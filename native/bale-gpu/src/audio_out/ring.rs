//! The bounded queue between the writer (`bale_audio_out_write`: the app's test or the fork's feeder) and the
//! render thread: whole frames in a circular buffer, plus marks that time them.
//!
//! A write's pts times its first frame; frames after it are timed by counting at the stream rate. A pts that
//! agrees with that count within [`MARK_TOLERANCE_HNS`] adds nothing (container timestamps are often rounded
//! to milliseconds), so only real discontinuities add marks. The ring never grows: a full ring takes nothing
//! and the writer retries later, which keeps the feeder from reading the file far ahead of the video.

use std::collections::VecDeque;

use super::sync::hns_from_frames;

/// A pts within this of the counted one continues the timeline (2 ms; corrections start at 20 ms).
pub const MARK_TOLERANCE_HNS: u64 = 20_000;
/// Marks queued at most; more (a writer giving every tiny write a jumping pts) are ignored.
pub const MAX_MARKS: usize = 256;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Mark {
    /// Frame index since the last clear.
    at: u64,
    pts: i64,
}

pub struct Ring {
    data: Vec<u8>,
    block: usize,
    capacity: usize,
    rate: u32,
    /// Index (in frames) of the oldest queued frame in `data`.
    start: usize,
    len: usize,
    /// Frames read or discarded since the last clear: the frame index of the oldest queued frame.
    consumed: u64,
    /// Ascending; the first may lie before `consumed` (it times the next frame).
    marks: VecDeque<Mark>,
}

impl Ring {
    /// A ring of `capacity` frames of `block` bytes at `rate` frames a second (all nonzero).
    pub fn new(capacity: usize, block: usize, rate: u32) -> Self {
        debug_assert!(capacity > 0 && block > 0 && rate > 0);
        Self {
            data: vec![0; capacity * block],
            block,
            capacity,
            rate,
            start: 0,
            len: 0,
            consumed: 0,
            marks: VecDeque::new(),
        }
    }

    pub fn capacity(&self) -> usize {
        self.capacity
    }

    /// Frames queued.
    pub fn len(&self) -> usize {
        self.len
    }

    pub fn is_empty(&self) -> bool {
        self.len == 0
    }

    /// Queues the whole frames of `data` that fit and returns the bytes taken: 0 when the ring is full (or
    /// `data` is shorter than a frame). `pts` times the first frame taken; `None` continues the timeline.
    pub fn write(&mut self, data: &[u8], pts: Option<i64>) -> usize {
        let frames = (data.len() / self.block).min(self.capacity - self.len);
        if frames == 0 {
            return 0;
        }
        if let Some(pts) = pts {
            self.mark(self.consumed + self.len as u64, pts);
        }
        let block = self.block;
        let end = (self.start + self.len) % self.capacity;
        let first = frames.min(self.capacity - end);
        self.data[end * block..(end + first) * block].copy_from_slice(&data[..first * block]);
        self.data[..(frames - first) * block].copy_from_slice(&data[first * block..frames * block]);
        self.len += frames;
        frames * block
    }

    fn mark(&mut self, at: u64, pts: i64) {
        if self
            .pts_at(at)
            .is_some_and(|counted| counted.abs_diff(pts) <= MARK_TOLERANCE_HNS)
        {
            return;
        }
        if self.marks.len() < MAX_MARKS {
            self.marks.push_back(Mark { at, pts });
        }
    }

    /// The pts of frame `at` (frames since the last clear), counted from the newest mark at or before it.
    pub fn pts_at(&self, at: u64) -> Option<i64> {
        let mark = self.marks.iter().rev().find(|mark| mark.at <= at)?;
        Some(
            mark.pts
                .saturating_add(hns_from_frames(at - mark.at, self.rate)),
        )
    }

    /// The pts of the next frame to be read: queued, or (with the ring empty) the one the timeline expects next.
    pub fn next_pts(&self) -> Option<i64> {
        self.pts_at(self.consumed)
    }

    /// Copies up to `frames` queued frames to the front of `out` and returns how many. `run(frames, pts)` is
    /// called for each stretch on one timeline, in order (a mark inside the copy starts a new stretch);
    /// `pts` is `None` while nothing has been timed.
    pub fn read(
        &mut self,
        out: &mut [u8],
        frames: usize,
        mut run: impl FnMut(u64, Option<i64>),
    ) -> usize {
        let frames = frames.min(self.len).min(out.len() / self.block);
        if frames == 0 {
            return 0;
        }
        let block = self.block;
        let first = frames.min(self.capacity - self.start);
        out[..first * block]
            .copy_from_slice(&self.data[self.start * block..(self.start + first) * block]);
        out[first * block..frames * block].copy_from_slice(&self.data[..(frames - first) * block]);
        let mut at = self.consumed;
        let end = at + frames as u64;
        while at < end {
            let next = self
                .marks
                .iter()
                .map(|mark| mark.at)
                .find(|&mark| mark > at && mark < end)
                .unwrap_or(end);
            run(next - at, self.pts_at(at));
            at = next;
        }
        self.advance(frames);
        frames
    }

    /// Discards up to `frames` queued frames (a drop correction) and returns how many.
    pub fn discard(&mut self, frames: u64) -> u64 {
        let frames = frames.min(self.len as u64) as usize;
        self.advance(frames);
        frames as u64
    }

    fn advance(&mut self, frames: usize) {
        self.start = (self.start + frames) % self.capacity;
        self.len -= frames;
        self.consumed += frames as u64;
        // Keep the newest mark at or before the read position: it times the next frame.
        while self.marks.len() > 1 && self.marks[1].at <= self.consumed {
            self.marks.pop_front();
        }
    }

    /// Drops everything queued and the timeline (a flush): the next write's pts starts a new one.
    pub fn clear(&mut self) {
        self.start = 0;
        self.len = 0;
        self.consumed = 0;
        self.marks.clear();
    }

    #[cfg(test)]
    fn marks(&self) -> usize {
        self.marks.len()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Two-channel 16-bit frames whose bytes count up: frame `n` holds `n` in every byte pair.
    fn frames(first: u16, count: u16) -> Vec<u8> {
        (first..first + count)
            .flat_map(|n| {
                let [lo, hi] = n.to_le_bytes();
                [lo, hi, lo, hi]
            })
            .collect()
    }

    fn read_all(ring: &mut Ring, count: usize) -> (Vec<u8>, Vec<(u64, Option<i64>)>) {
        let mut out = vec![0xEE; count * 4];
        let mut runs = Vec::new();
        let read = ring.read(&mut out, count, |frames, pts| runs.push((frames, pts)));
        out.truncate(read * 4);
        (out, runs)
    }

    #[test]
    fn whole_frames_go_round_and_a_full_ring_takes_nothing() {
        let mut ring = Ring::new(8, 4, 48_000);
        assert!(ring.is_empty());
        // A trailing partial frame is not taken: the caller resends it with the next data.
        let mut data = frames(0, 6);
        data.push(0xAB);
        assert_eq!(ring.write(&data, None), 24);
        let (out, runs) = read_all(&mut ring, 2);
        assert_eq!(out, frames(0, 2));
        assert_eq!(runs, [(2, None)]);
        // Wraps around the end of the buffer on write and on read.
        assert_eq!(ring.write(&frames(6, 10), None), 16, "only 4 frames fit");
        assert_eq!(ring.len(), ring.capacity());
        assert_eq!(ring.write(&frames(10, 1), None), 0, "full");
        assert_eq!(ring.write(&[1, 2, 3], None), 0, "shorter than a frame");
        let (out, _) = read_all(&mut ring, 100);
        assert_eq!(out, frames(2, 8));
        assert!(ring.is_empty());
        assert_eq!(read_all(&mut ring, 4).0, Vec::<u8>::new());
    }

    #[test]
    fn counted_pts_add_no_marks_and_jumps_start_a_stretch() {
        // 48 kHz: 480 frames are 10 ms (100000 hns).
        let mut ring = Ring::new(4_800, 4, 48_000);
        let chunk = vec![0u8; 480 * 4];
        ring.write(&chunk, Some(1_000_000));
        // Millisecond-rounded container timestamps continue the timeline.
        ring.write(&chunk, Some(1_100_000 + 9_999));
        ring.write(&chunk, None);
        ring.write(&chunk, Some(1_300_000 - 20_000));
        assert_eq!(ring.marks(), 1);
        // A 50 ms jump is a discontinuity.
        ring.write(&chunk, Some(1_900_000));
        assert_eq!(ring.marks(), 2);
        assert_eq!(ring.pts_at(0), Some(1_000_000));
        assert_eq!(ring.pts_at(1_440), Some(1_300_000));
        assert_eq!(ring.pts_at(1_920), Some(1_900_000));
        let mut out = vec![0; 2_400 * 4];
        let mut runs = Vec::new();
        assert_eq!(ring.read(&mut out, 2_400, |f, p| runs.push((f, p))), 2_400);
        assert_eq!(runs, [(1_920, Some(1_000_000)), (480, Some(1_900_000))]);
        // The mark that times the next frame stays after its first frames are read.
        assert_eq!(ring.marks(), 1);
        ring.write(&chunk, None);
        let (_, runs) = read_all(&mut ring, 480);
        assert_eq!(runs, [(480, Some(2_000_000))]);
    }

    #[test]
    fn a_read_splits_at_a_mark_inside_it_and_discard_keeps_the_timeline() {
        let mut ring = Ring::new(100, 4, 1_000);
        ring.write(&frames(0, 10), Some(0));
        ring.write(&frames(10, 10), Some(50_000_000)); // 5 s later: a jump
        assert_eq!(ring.discard(4), 4);
        let (_, runs) = read_all(&mut ring, 10);
        // 1 kHz: frame 4 is 4 ms (40000 hns) into the first stretch.
        assert_eq!(runs, [(6, Some(40_000)), (4, Some(50_000_000))]);
        assert_eq!(ring.discard(100), 6);
        assert!(ring.is_empty());
        // Empty, the ring still knows which pts comes next if the stream continues.
        assert_eq!(ring.next_pts(), Some(50_000_000 + 100_000));
        // Untimed data after the last mark keeps counting from it.
        ring.write(&frames(0, 2), None);
        let (_, runs) = read_all(&mut ring, 2);
        assert_eq!(runs, [(2, Some(50_000_000 + 100_000))]);
    }

    #[test]
    fn clear_starts_a_new_timeline() {
        let mut ring = Ring::new(16, 4, 48_000);
        ring.write(&frames(0, 8), Some(123));
        ring.clear();
        assert!(ring.is_empty());
        assert_eq!((ring.pts_at(0), ring.next_pts()), (None, None));
        ring.write(&frames(0, 4), None);
        let (out, runs) = read_all(&mut ring, 4);
        assert_eq!(out, frames(0, 4));
        assert_eq!(runs, [(4, None)]);
        ring.write(&frames(0, 4), Some(7_000_000));
        let (_, runs) = read_all(&mut ring, 4);
        assert_eq!(runs, [(4, Some(7_000_000))]);
    }

    #[test]
    fn marks_are_bounded() {
        let mut ring = Ring::new(1_000, 4, 48_000);
        for n in 0..(MAX_MARKS as i64 + 50) {
            // One frame each, every pts a second apart: all discontinuities.
            ring.write(&frames(0, 1), Some(n * 10_000_000));
        }
        assert_eq!(ring.marks(), MAX_MARKS);
        assert_eq!(ring.len(), MAX_MARKS + 50);
        // Frames past the last mark count on from it.
        let last = (MAX_MARKS as i64 - 1) * 10_000_000;
        assert_eq!(
            ring.pts_at(MAX_MARKS as u64 + 9),
            Some(last + hns_from_frames(10, 48_000))
        );
    }
}
