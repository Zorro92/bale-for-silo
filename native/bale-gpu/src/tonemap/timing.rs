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
        session: stats.session.wrapping_add(1).max(1),
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
    use std::panic::{AssertUnwindSafe, catch_unwind};

    // The Xbox probe has validated one outstanding disjoint query, not several unresolved
    // intervals on MF's shared device. Keep playback asynchronous by skipping busy applies.
    pub const RING_SIZE: usize = 1;
    pub const READ_DELAY: u64 = 4;
    pub const WINDOW_FRAMES: u32 = 240;

    /// Additive diagnostic codes in TimingStats.reserved; never contain driver/stream strings.
    #[derive(Clone, Copy, Debug, PartialEq)]
    #[repr(u32)]
    pub enum FailureReason {
        Create = 1,
        Size = 2,
        Read = 3,
        Frequency = 4,
        Sequence = 5,
        Panic = 6,
        Context = 7,
        Device = 8,
    }

    #[derive(Clone, Copy, Debug, PartialEq)]
    pub struct Failure {
        pub reason: FailureReason,
        pub code: i32,
    }
    impl Failure {
        pub fn new(reason: FailureReason, code: i32) -> Self {
            Self {
                reason,
                code: if code < 0 { code } else { 0x80004005u32 as i32 },
            }
        }
    }

    /// Device-independent failure containment, also exercised with injected failures on Linux.
    /// An export-level catch alone would return an apply error and stop the stream. Timing's
    /// own catch disables only instrumentation and lets the rest of the draw continue.
    pub struct Report {
        pub stats: BaleToneMapTimingStats,
        pub ring: Ring,
        window: Window,
    }
    impl Report {
        pub fn new(stats: BaleToneMapTimingStats) -> Self {
            Self {
                stats,
                ring: Ring::default(),
                window: Window::default(),
            }
        }
        pub fn attempt<T>(
            &mut self,
            operation: impl FnOnce(&mut Self) -> Result<T, Failure>,
        ) -> Option<T> {
            if self.stats.state != 1 {
                return None;
            }
            match catch_unwind(AssertUnwindSafe(|| operation(self))) {
                Ok(Ok(value)) => Some(value),
                outcome => {
                    let failure = match outcome {
                        Ok(Err(failure)) => failure,
                        _ => Failure::new(FailureReason::Panic, 0x8000ffffu32 as i32),
                    };
                    self.stats.state = 2;
                    self.stats.error = failure.code;
                    self.stats.reserved = failure.reason as u32;
                    self.retire_pending();
                    self.window.finish(&mut self.stats);
                    None
                }
            }
        }
        pub fn record(&mut self, sample: Sample) {
            if self.window.record(sample) {
                self.window.finish(&mut self.stats);
                self.stats.pending = self.ring.pending();
            }
        }
        fn retire_pending(&mut self) {
            for _ in 0..self.ring.retire() {
                self.record(Sample::Unavailable);
            }
            self.stats.pending = 0;
        }
        pub fn close(&mut self) {
            self.retire_pending();
            self.window.finish(&mut self.stats);
            if self.stats.state != 2 {
                self.stats.state = 3;
            }
        }
    }

    /// Validate before GetData ever receives a pointer. Unexpected positive HRESULTs are not
    /// data, either: only S_OK is ready, and S_FALSE must leave the entire query set pending.
    pub fn data_ready(code: i32) -> Result<bool, Failure> {
        match code {
            0 => Ok(true),
            1 => Ok(false),
            _ => Err(Failure::new(FailureReason::Read, code)),
        }
    }

    pub fn data_size(actual: u32, expected: usize) -> Result<(), Failure> {
        if actual != 0 && actual as usize == expected {
            Ok(())
        } else {
            Err(Failure::new(FailureReason::Size, 0x80070057u32 as i32))
        }
    }

    #[derive(Default)]
    pub struct Ring {
        frame: u64,
        pending: [Option<u64>; RING_SIZE],
    }
    impl Ring {
        pub fn advance(&mut self) {
            self.frame = self.frame.wrapping_add(1);
        }
        pub fn ready_to_poll(&self, slot: usize) -> bool {
            self.pending.get(slot).is_some_and(|pending| {
                pending.is_some_and(|frame| self.frame.wrapping_sub(frame) >= READ_DELAY)
            })
        }
        pub fn reserve(&mut self) -> Option<usize> {
            let slot = self.pending.iter().position(Option::is_none)?;
            *self.pending.get_mut(slot)? = Some(self.frame);
            Some(slot)
        }
        pub fn retire(&mut self) -> u32 {
            let count = self.pending();
            self.pending.fill(None);
            count
        }
        pub fn release(&mut self, slot: usize) -> bool {
            match self.pending.get_mut(slot) {
                Some(pending) => pending.take().is_some(),
                None => false,
            }
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
        if frequency == 0 || ticks.iter().zip(ticks.iter().skip(1)).any(|(a, b)| b < a) {
            return Sample::Unavailable;
        }
        let [start, copy_start, copy_end, luma_end, chroma_end, end] = ticks;
        let ms = |a, b| (b - a) as f64 * 1000.0 / frequency as f64;
        Sample::Valid([
            ms(start, end),
            if copied {
                ms(copy_start, copy_end)
            } else {
                0.0
            },
            ms(copy_end, luma_end),
            ms(luma_end, chroma_end),
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
                    for ((sum, max), value) in self.sum.iter_mut().zip(&mut self.max).zip(ms) {
                        *sum += value;
                        *max = max.max(value);
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
            stats.window = stats.window.saturating_add(1);
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
        assert_eq!(ring.pending(), 1);
        assert_eq!(ring.reserve(), None);
        assert!(!ring.ready_to_poll(0));
        for _ in 0..READ_DELAY {
            ring.advance();
        }
        assert!(ring.ready_to_poll(0));
        assert!(!ring.ready_to_poll(usize::MAX));
        assert!(!ring.release(usize::MAX));
        assert!(ring.release(0));
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
        assert_eq!((stats.frames, stats.unavailable), (1, 1));
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
        assert_eq!(ring.pending(), 1);
        assert_eq!(
            (stats.frames, stats.valid, stats.unavailable),
            (240, 0, 240)
        );
    }

    fn collecting() -> Report {
        Report::new(BaleToneMapTimingStats {
            session: 1,
            state: 1,
            ..Default::default()
        })
    }

    #[test]
    fn get_data_accepts_only_ready_or_not_ready_and_rejects_mismatched_sizes() {
        assert_eq!(data_ready(0), Ok(true));
        assert_eq!(data_ready(1), Ok(false));
        for code in [0x80004005u32 as i32, 0x887A0005u32 as i32, 2] {
            let failure = data_ready(code).unwrap_err();
            assert_eq!(failure.reason, FailureReason::Read);
            assert!(failure.code < 0);
        }
        assert_eq!(data_size(8, size_of::<u64>()), Ok(()));
        assert_eq!(data_size(16, 16), Ok(()));
        for size in [0, 4, 12, 24, u32::MAX] {
            assert_eq!(data_size(size, 16).unwrap_err().reason, FailureReason::Size);
        }
    }

    #[test]
    fn every_failure_disables_once_retires_pending_and_never_calls_the_backend_again() {
        for reason in [
            FailureReason::Create,
            FailureReason::Size,
            FailureReason::Read,
            FailureReason::Frequency,
            FailureReason::Sequence,
            FailureReason::Panic,
            FailureReason::Context,
            FailureReason::Device,
        ] {
            let mut report = collecting();
            report.record(Sample::Valid([4.0, 1.0, 2.0, 1.0]));
            assert_eq!(report.ring.reserve(), Some(0));
            assert_eq!(
                report.attempt::<()>(|_| Err(Failure::new(reason, -1))),
                None
            );
            assert_eq!(
                (
                    report.stats.state,
                    report.stats.error,
                    report.stats.reserved
                ),
                (2, -1, reason as u32)
            );
            assert_eq!(
                (
                    report.stats.frames,
                    report.stats.valid,
                    report.stats.unavailable,
                    report.stats.pending
                ),
                (2, 1, 1, 0)
            );
            let failed = report.stats;
            assert_eq!(
                report.attempt::<()>(|_| panic!("disabled backend must not run")),
                None
            );
            report.close();
            assert_eq!(
                (
                    report.stats.state,
                    report.stats.error,
                    report.stats.reserved,
                    report.stats.window,
                    report.stats.frames,
                    report.stats.pending
                ),
                (
                    failed.state,
                    failed.error,
                    failed.reserved,
                    failed.window,
                    failed.frames,
                    failed.pending
                )
            );
            assert_eq!(report.stats.avg_ms, failed.avg_ms);
        }
    }

    #[test]
    fn a_timing_panic_is_contained_before_the_apply_boundary_and_playback_can_continue() {
        let mut report = collecting();
        report.ring.reserve();
        assert_eq!(
            report.attempt::<()>(|_| panic!("injected timing panic")),
            None
        );
        assert_eq!(report.stats.state, 2);
        assert_eq!(report.stats.reserved, FailureReason::Panic as u32);
        assert_eq!(report.stats.error, 0x8000ffffu32 as i32);
        assert_eq!((report.stats.unavailable, report.stats.pending), (1, 0));
        assert_eq!(report.attempt(|_| Ok(())), None);
    }

    #[test]
    fn a_pending_read_stays_owned_until_release_and_close_publishes_partial_window() {
        let mut report = collecting();
        assert_eq!(report.attempt(|r| Ok(r.ring.reserve())), Some(Some(0)));
        for _ in 0..READ_DELAY {
            report.attempt(|r| {
                r.ring.advance();
                assert!(!data_ready(1)?);
                assert_eq!(r.ring.reserve(), None);
                r.record(Sample::Unavailable);
                Ok(())
            });
        }
        assert_eq!(report.ring.pending(), 1);
        report.attempt(|r| {
            assert!(data_ready(0)?);
            assert!(r.ring.release(0));
            r.record(Sample::Valid([1.0, 0.0, 0.5, 0.5]));
            Ok(())
        });
        report.close();
        assert_eq!(
            (
                report.stats.state,
                report.stats.frames,
                report.stats.valid,
                report.stats.unavailable,
                report.stats.pending
            ),
            (3, 5, 1, 4, 0)
        );
    }
}
