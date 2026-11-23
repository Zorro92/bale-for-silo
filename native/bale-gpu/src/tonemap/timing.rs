//! Bounded, device-independent accounting for the optional stage timestamps.
use super::types::BaleToneMapTimingStats;

#[cfg(feature = "stage-timing")]
use std::sync::{Mutex, atomic::AtomicBool};

#[cfg(feature = "stage-timing")]
pub(super) static ENABLED: AtomicBool = AtomicBool::new(false);
#[cfg(feature = "stage-timing")]
static SNAPSHOT: Mutex<BaleToneMapTimingStats> = Mutex::new(BaleToneMapTimingStats {
    session: 0,
    window: 0,
    state: 0,
    frames: 0,
    valid: 0,
    disjoint: 0,
    unavailable: 0,
    pending: 0,
    error: 0,
    reserved: 0,
    avg_ms: [0.0; 4],
    max_ms: [0.0; 4],
});

/// No GPU access, no wrapper/source lock. Contention returns S_FALSE; never blocks the UI.
#[cfg(feature = "stage-timing")]
pub(super) fn snapshot() -> Option<BaleToneMapTimingStats> {
    SNAPSHOT.try_lock().ok().map(|stats| *stats)
}

#[cfg(all(windows, feature = "stage-timing"))]
pub(super) fn start() -> Option<BaleToneMapTimingStats> {
    if !ENABLED.load(std::sync::atomic::Ordering::Relaxed) {
        return None;
    }
    let mut stats = SNAPSHOT.lock().unwrap_or_else(|e| e.into_inner());
    *stats = BaleToneMapTimingStats {
        session: stats.session + 1,
        state: 1,
        ..Default::default()
    };
    Some(*stats)
}

#[cfg(all(windows, feature = "stage-timing"))]
pub(super) fn publish(value: BaleToneMapTimingStats) {
    let mut stats = SNAPSHOT.lock().unwrap_or_else(|e| e.into_inner());
    // An old stage finishing/closing cannot replace a newer stage's report.
    if stats.session == value.session {
        *stats = value;
    }
}

#[cfg(any(windows, test))]
pub(super) mod accounting {
    use super::*;
    pub const RING_SIZE: usize = 8;
    pub const READ_DELAY: u64 = 4;
    pub const WINDOW_FRAMES: u32 = 240;

    #[derive(Default)]
    pub struct Ring {
        frame: u64,
        pending: [Option<u64>; RING_SIZE],
    }
    impl Ring {
        pub fn advance(&mut self) {
            self.frame += 1;
        }
        pub fn ready_to_poll(&self, slot: usize) -> bool {
            self.pending[slot].is_some_and(|frame| self.frame - frame >= READ_DELAY)
        }
        pub fn reserve(&mut self) -> Option<usize> {
            let slot = self.pending.iter().position(Option::is_none)?;
            self.pending[slot] = Some(self.frame);
            Some(slot)
        }
        pub fn retire(&mut self) -> u32 {
            let count = self.pending();
            self.pending.fill(None);
            count
        }
        pub fn release(&mut self, slot: usize) {
            self.pending[slot] = None;
        }
        pub fn pending(&self) -> u32 {
            self.pending.iter().filter(|s| s.is_some()).count() as u32
        }
    }

    #[derive(Debug, PartialEq)]
    pub enum Sample {
        Valid([f64; 4]),
        Disjoint,
        Unavailable,
    }

    /// Reject invalid/discontinuous counters before subtraction; never turn them into zero-ms results.
    pub fn sample(ticks: [u64; 6], frequency: u64, disjoint: bool, copied: bool) -> Sample {
        if disjoint {
            return Sample::Disjoint;
        }
        if frequency == 0 || ticks.windows(2).any(|w| w[1] < w[0]) {
            return Sample::Unavailable;
        }
        let ms = |a: usize, b: usize| (ticks[b] - ticks[a]) as f64 * 1000.0 / frequency as f64;
        Sample::Valid([
            ms(0, 5),
            if copied { ms(1, 2) } else { 0.0 },
            ms(2, 3),
            ms(3, 4),
        ])
    }

    #[derive(Default)]
    pub struct Window {
        frames: u32,
        valid: u32,
        disjoint: u32,
        unavailable: u32,
        sum: [f64; 4],
        max: [f64; 4],
    }
    impl Window {
        pub fn record(&mut self, sample: Sample) -> bool {
            self.frames += 1;
            match sample {
                Sample::Valid(ms) => {
                    self.valid += 1;
                    for (i, value) in ms.into_iter().enumerate() {
                        self.sum[i] += value;
                        self.max[i] = self.max[i].max(value);
                    }
                }
                Sample::Disjoint => self.disjoint += 1,
                Sample::Unavailable => self.unavailable += 1,
            }
            self.frames == WINDOW_FRAMES
        }
        pub fn finish(&mut self, stats: &mut BaleToneMapTimingStats) -> bool {
            if self.frames == 0 {
                return false;
            }
            stats.window += 1;
            stats.frames = self.frames;
            stats.valid = self.valid;
            stats.disjoint = self.disjoint;
            stats.unavailable = self.unavailable;
            stats.avg_ms = self.sum.map(|s| {
                if self.valid > 0 {
                    s / f64::from(self.valid)
                } else {
                    0.0
                }
            });
            stats.max_ms = self.max;
            *self = Self::default();
            true
        }
    }
}

#[cfg(test)]
mod tests {
    use super::accounting::*;
    use super::*;
    #[test]
    fn ring_delays_reads_and_never_reuses_an_unfinished_query() {
        let mut ring = Ring::default();
        for slot in 0..RING_SIZE {
            ring.advance();
            assert_eq!(ring.reserve(), Some(slot));
            assert!(!ring.ready_to_poll(slot));
        }
        assert_eq!(ring.pending(), 8);
        assert_eq!(ring.reserve(), None);
        assert!(ring.ready_to_poll(0));
        assert!(!ring.ready_to_poll(7));
        ring.release(0);
        assert_eq!(ring.reserve(), Some(0));
        assert!(!ring.ready_to_poll(0));
        for _ in 0..READ_DELAY {
            ring.advance();
        }
        assert!(ring.ready_to_poll(0));
    }
    #[test]
    fn teardown_accounts_each_unread_query_once_without_a_read_or_wait() {
        let mut ring = Ring::default();
        ring.reserve();
        ring.advance();
        ring.reserve();
        let mut window = Window::default();
        for _ in 0..ring.retire() {
            window.record(Sample::Unavailable);
        }
        assert_eq!(ring.pending(), 0);
        assert_eq!(ring.retire(), 0);
        let mut stats = BaleToneMapTimingStats::default();
        window.finish(&mut stats);
        assert_eq!((stats.frames, stats.unavailable), (2, 2));
    }
    #[test]
    fn converts_frequency_and_excludes_disjoint_zero_frequency_and_reversed_ticks() {
        assert_eq!(
            sample([100, 100, 102, 108, 110, 110], 1000, false, true),
            Sample::Valid([10.0, 2.0, 6.0, 2.0])
        );
        assert_eq!(
            sample([100, 100, 102, 108, 110, 110], 2000, false, false),
            Sample::Valid([5.0, 0.0, 3.0, 1.0])
        );
        assert_eq!(sample([0; 6], 0, true, true), Sample::Disjoint);
        assert_eq!(sample([0; 6], 0, false, true), Sample::Unavailable);
        assert_eq!(
            sample([100, 100, 99, 108, 110, 110], 1000, false, true),
            Sample::Unavailable
        );
    }
    #[test]
    fn windows_average_only_valid_frames_and_reset_every_counter() {
        let mut window = Window::default();
        for _ in 0..119 {
            assert!(!window.record(Sample::Valid([2.0, 0.0, 1.5, 0.5])));
        }
        assert!(!window.record(Sample::Valid([6.0, 2.0, 3.0, 1.0])));
        for _ in 0..119 {
            assert!(!window.record(Sample::Disjoint));
        }
        assert!(window.record(Sample::Unavailable));
        let mut stats = BaleToneMapTimingStats::default();
        assert!(window.finish(&mut stats));
        assert_eq!(
            (
                stats.window,
                stats.frames,
                stats.valid,
                stats.disjoint,
                stats.unavailable
            ),
            (1, 240, 120, 119, 1)
        );
        assert_eq!(stats.max_ms, [6.0, 2.0, 3.0, 1.0]);
        assert!((stats.avg_ms[0] - 244.0 / 120.0).abs() < 1e-9);
        assert!(!window.finish(&mut stats));
        assert!(!window.record(Sample::Unavailable));
        assert!(window.finish(&mut stats));
        assert_eq!(
            (
                stats.window,
                stats.frames,
                stats.valid,
                stats.disjoint,
                stats.unavailable
            ),
            (2, 1, 0, 0, 1)
        );
        assert_eq!(stats.avg_ms, [0.0; 4]);
        assert_eq!(stats.max_ms, [0.0; 4]);
    }
    #[test]
    fn a_gpu_that_never_finishes_has_bounded_queries_and_reports_unavailable() {
        let mut ring = Ring::default();
        let mut window = Window::default();
        let mut stats = BaleToneMapTimingStats::default();
        for _ in 0..RING_SIZE + WINDOW_FRAMES as usize {
            ring.advance();
            if ring.reserve().is_none() && window.record(Sample::Unavailable) {
                window.finish(&mut stats);
            }
        }
        assert_eq!(ring.pending(), 8);
        assert_eq!(
            (stats.frames, stats.valid, stats.unavailable),
            (240, 0, 240)
        );
    }
}
