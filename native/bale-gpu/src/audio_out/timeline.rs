//! What each device frame carries: the render thread appends what it hands the device every period (queued
//! frames with their pts, or silence) and asks which pts plays at the device position.
//!
//! Frames count from the last flush, like `IAudioClock`'s position, which `IAudioClient::Reset` sets back to
//! 0. Silence holds the pts where the last data before it ended: while it plays the content does not move,
//! so audio falls behind the video by the silence's length and the sync controller sees it.

use std::collections::VecDeque;

use super::sync::hns_from_frames;

/// Segments kept at most; the oldest go first (normally pruning behind the device keeps a handful).
pub const MAX_SEGMENTS: usize = 512;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Kind {
    /// Queued frames; `None` = written without any pts yet.
    Data(Option<i64>),
    Silence,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Segment {
    start: u64,
    frames: u64,
    kind: Kind,
}

impl Segment {
    fn end(&self) -> u64 {
        self.start + self.frames
    }
}

pub struct Timeline {
    rate: u32,
    segments: VecDeque<Segment>,
    submitted: u64,
    /// Where the newest dropped data segment ended: what silence after it holds.
    stalled: Option<i64>,
}

impl Timeline {
    pub fn new(rate: u32) -> Self {
        Self {
            rate,
            segments: VecDeque::new(),
            submitted: 0,
            stalled: None,
        }
    }

    /// Frames handed to the device since the last reset: the next frame's index.
    pub fn submitted(&self) -> u64 {
        self.submitted
    }

    /// Appends `frames` queued frames timed from `pts` (`None` = untimed). Data that continues the previous
    /// segment's timeline (within 1 hns of rounding) extends it.
    pub fn push_data(&mut self, frames: u64, pts: Option<i64>) {
        if frames == 0 {
            return;
        }
        if let Some(last) = self.segments.back_mut() {
            let continues = match (last.kind, pts) {
                (Kind::Data(None), None) => true,
                (Kind::Data(Some(start)), Some(pts)) => {
                    start
                        .saturating_add(hns_from_frames(last.frames, self.rate))
                        .abs_diff(pts)
                        <= 1
                }
                _ => false,
            };
            if continues {
                last.frames += frames;
                self.submitted += frames;
                return;
            }
        }
        self.push(frames, Kind::Data(pts));
    }

    /// Appends `frames` of silence (correction padding or an underrun).
    pub fn push_silence(&mut self, frames: u64) {
        if frames == 0 {
            return;
        }
        if let Some(last) = self.segments.back_mut()
            && last.kind == Kind::Silence
        {
            last.frames += frames;
            self.submitted += frames;
            return;
        }
        self.push(frames, Kind::Silence);
    }

    fn push(&mut self, frames: u64, kind: Kind) {
        self.segments.push_back(Segment {
            start: self.submitted,
            frames,
            kind,
        });
        self.submitted += frames;
        while self.segments.len() > MAX_SEGMENTS {
            self.pop_front();
        }
    }

    fn pop_front(&mut self) {
        if let Some(segment) = self.segments.pop_front() {
            match segment.kind {
                Kind::Data(Some(pts)) => {
                    self.stalled =
                        Some(pts.saturating_add(hns_from_frames(segment.frames, self.rate)));
                }
                Kind::Data(None) => self.stalled = None,
                Kind::Silence => {}
            }
        }
    }

    /// Forgets segments that ended at or before frame `before` (keeping the newest one).
    pub fn prune(&mut self, before: u64) {
        while self.segments.len() > 1 && self.segments[0].end() <= before {
            self.pop_front();
        }
    }

    /// The pts playing at device frame `frame`: data counts on from its pts (past the last segment too),
    /// silence holds where the data before it ended. `None` before the first kept segment, for untimed data,
    /// or when nothing has been handed over yet.
    pub fn pts_at(&self, frame: u64) -> Option<i64> {
        let index = self
            .segments
            .partition_point(|segment| segment.start <= frame)
            .checked_sub(1)?;
        let segment = self.segments[index];
        match segment.kind {
            Kind::Data(pts) => {
                pts.map(|pts| pts.saturating_add(hns_from_frames(frame - segment.start, self.rate)))
            }
            Kind::Silence => self.ended_before(index),
        }
    }

    /// Where the data segment before `index` ended.
    fn ended_before(&self, index: usize) -> Option<i64> {
        for segment in self.segments.range(..index).rev() {
            match segment.kind {
                Kind::Data(Some(pts)) => {
                    return Some(pts.saturating_add(hns_from_frames(segment.frames, self.rate)));
                }
                Kind::Data(None) => return None,
                Kind::Silence => {}
            }
        }
        self.stalled
    }

    /// A flush: the device restarts at frame 0 with nothing timed.
    pub fn reset(&mut self) {
        self.segments.clear();
        self.submitted = 0;
        self.stalled = None;
    }

    #[cfg(test)]
    fn segments(&self) -> usize {
        self.segments.len()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn data_counts_on_and_silence_holds_the_end_of_the_data_before_it() {
        // 1 kHz: a frame is 1 ms (10000 hns).
        let mut timeline = Timeline::new(1_000);
        assert_eq!(timeline.pts_at(0), None);
        timeline.push_data(100, Some(5_000_000));
        timeline.push_silence(50);
        timeline.push_data(100, Some(6_000_000));
        assert_eq!(timeline.submitted(), 250);
        assert_eq!(timeline.pts_at(0), Some(5_000_000));
        assert_eq!(timeline.pts_at(99), Some(5_990_000));
        // During the silence the content stands still at the end of the first data.
        assert_eq!(timeline.pts_at(100), Some(6_000_000));
        assert_eq!(timeline.pts_at(149), Some(6_000_000));
        assert_eq!(timeline.pts_at(150), Some(6_000_000));
        assert_eq!(timeline.pts_at(249), Some(6_990_000));
        // Past what was handed over, data keeps counting (the device position never gets there in practice).
        assert_eq!(timeline.pts_at(260), Some(7_100_000));
    }

    #[test]
    fn continuing_data_and_silence_merge_into_one_segment() {
        let mut timeline = Timeline::new(48_000);
        // 480 frames = 100000 hns; the next pts may differ by 1 hns of rounding.
        timeline.push_data(480, Some(0));
        timeline.push_data(480, Some(100_001));
        timeline.push_data(480, Some(200_000));
        timeline.push_silence(10);
        timeline.push_silence(0);
        timeline.push_silence(10);
        timeline.push_data(0, Some(99));
        assert_eq!(timeline.segments(), 2);
        assert_eq!(timeline.pts_at(1_439), Some(299_792));
        assert_eq!(timeline.pts_at(1_450), Some(300_000));
        // A jump starts a new segment; so does untimed data, and untimed data merges with itself.
        timeline.push_data(480, Some(900_000));
        timeline.push_data(480, None);
        timeline.push_data(480, None);
        assert_eq!(timeline.segments(), 4);
        assert_eq!(timeline.pts_at(1_460), Some(900_000));
        assert_eq!(timeline.pts_at(2_000), None);
    }

    #[test]
    fn untimed_data_and_silence_after_it_have_no_pts() {
        let mut timeline = Timeline::new(1_000);
        timeline.push_silence(10);
        assert_eq!(
            timeline.pts_at(5),
            None,
            "silence before anything was timed"
        );
        timeline.push_data(10, None);
        timeline.push_silence(10);
        assert_eq!(timeline.pts_at(15), None);
        assert_eq!(timeline.pts_at(25), None);
    }

    #[test]
    fn pruning_keeps_what_silence_holds() {
        let mut timeline = Timeline::new(1_000);
        timeline.push_data(100, Some(0));
        timeline.push_silence(100);
        timeline.prune(100);
        assert_eq!(timeline.segments(), 1);
        assert_eq!(timeline.pts_at(150), Some(1_000_000));
        // Frames before the oldest kept segment are gone.
        assert_eq!(timeline.pts_at(50), None);
        // The newest segment always stays.
        timeline.prune(10_000);
        assert_eq!(timeline.segments(), 1);
        assert_eq!(timeline.pts_at(199), Some(1_000_000));
        timeline.reset();
        assert_eq!((timeline.submitted(), timeline.pts_at(0)), (0, None));
        timeline.push_silence(5);
        assert_eq!(timeline.pts_at(0), None, "a reset forgets the stall");
    }

    #[test]
    fn segments_are_bounded() {
        let mut timeline = Timeline::new(1_000);
        for n in 0..(MAX_SEGMENTS as i64) {
            timeline.push_data(1, Some(n * 10_000_000));
            timeline.push_silence(1);
        }
        assert_eq!(timeline.segments(), MAX_SEGMENTS);
        let last = (MAX_SEGMENTS as i64 - 1) * 10_000_000;
        assert_eq!(
            timeline.pts_at(timeline.submitted() - 1),
            Some(last + 10_000)
        );
    }
}
