//! Bounded, device-independent accounting for the optional stage GPU timing: D3D11 event queries
//! whose completion a poll thread sees on the CPU (timing_gpu.rs says why timestamps were dropped).
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

    /// One measurement (a start and an end event) in flight at a time. An event is never ended
    /// again before the previous measurement completed and was accounted; applies meanwhile skip
    /// measuring and count as unavailable.
    pub const RING_SIZE: usize = 1;
    /// A finished measurement is accounted no sooner than four applies after it was issued, so at
    /// most one apply in four pays for the measurement's two extra submissions.
    pub const READ_DELAY: u64 = 4;
    pub const WINDOW_FRAMES: u32 = 240;
    /// The poll thread's period while a measurement is in flight. std's sleep uses a
    /// high-resolution waitable timer on Windows 10 1803+ (else whole milliseconds).
    pub const POLL_INTERVAL_US: u64 = 250;
    /// An end event still pending after this many polls (about 1 s) disables timing: polling a
    /// set that never completes would only cost CPU and device-lock time. Counted in polls, not
    /// wall time, so an app suspension mid-measurement cannot trigger it.
    pub const GIVE_UP_POLLS: u32 = 4000;
    /// No stage frame takes this long on the GPU: a longer reading spans an app suspension or a
    /// starved poll thread and is accounted as unavailable, not as a time.
    pub const GIVE_UP_MS: f64 = 1000.0;

    const E_FAIL: i32 = 0x80004005u32 as i32;
    /// HRESULT_FROM_WIN32(ERROR_TIMEOUT).
    const E_TIMEOUT: i32 = 0x800705B4u32 as i32;

    /// Additive diagnostic codes in TimingStats.reserved; never contain driver/stream strings.
    #[derive(Clone, Copy, Debug, PartialEq)]
    #[repr(u32)]
    pub enum FailureReason {
        Create = 1,
        Size = 2,
        Read = 3,
        /// QueryPerformanceFrequency/Counter (was the timestamp frequency).
        Frequency = 4,
        Sequence = 5,
        Panic = 6,
        Context = 7,
        Device = 8,
        /// No poll thread: no immediate context or multithread protection, or no thread.
        Poller = 9,
        /// An end event still pending after GIVE_UP_POLLS polls.
        Timeout = 10,
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
                code: if code < 0 { code } else { E_FAIL },
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

    /// Validate an event's GetData result before it means anything. Only S_OK with TRUE is
    /// complete (D3D11_QUERY_EVENT's documented result) and S_FALSE is pending. Anything else,
    /// including S_OK with FALSE, is a failure, never a guess.
    pub fn event_state(code: i32, value: i32) -> Result<bool, Failure> {
        match (code, value) {
            (0, 0) => Err(Failure::new(FailureReason::Read, E_FAIL)),
            (0, _) => Ok(true),
            (1, _) => Ok(false),
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
        /// Milliseconds: [total (upper bound), end resolution, 0, 0]. The ABI's last two slots
        /// carried luma and chroma under the timestamp queries; events report no passes.
        Valid([f64; 4]),
        Unavailable,
    }

    /// One measurement's observations in QueryPerformanceCounter ticks. Every edge is taken
    /// conservatively, so the result never under-states the stage's elapsed GPU time:
    /// - the stage cannot start on the GPU before `issued` (read after the start event's
    ///   submission request, before the stage's first command is recorded), nor before the
    ///   start event completed, which was after the last read that still saw it pending;
    /// - the stage had finished by the end of the first read that saw the end event complete.
    #[derive(Debug, PartialEq)]
    pub struct Flight {
        issued: u64,
        start_done: bool,
        start_pending: u64,
        end_pending: u64,
        polls: u32,
        frequency: u64,
    }
    impl Flight {
        /// `issued`: QPC after the start event's submission request; `start_done`: that read
        /// already saw it complete. `end_issued`: QPC before the end event's End.
        pub fn new(issued: u64, start_done: bool, end_issued: u64, frequency: u64) -> Self {
            Self {
                issued,
                start_done,
                start_pending: issued,
                end_pending: end_issued,
                polls: 0,
                frequency,
            }
        }
        pub fn start_done(&self) -> bool {
            self.start_done
        }
        /// `before`: QPC read just before the GetData that returned `done` for the start event.
        pub fn start_polled(&mut self, before: u64, done: bool) {
            if done {
                self.start_done = true;
            } else {
                self.start_pending = self.start_pending.max(before);
            }
        }
        /// `before` / `after`: QPC read just before / after the GetData that returned `done` for
        /// the end event. Some once the measurement finished (or timed out).
        pub fn end_polled(
            &mut self,
            before: u64,
            after: u64,
            done: bool,
        ) -> Option<Result<Sample, Failure>> {
            if done {
                // The end completing implies the start did; its last pending read stays the edge.
                return Some(sample(
                    self.issued.max(self.start_pending),
                    self.end_pending,
                    after,
                    self.frequency,
                ));
            }
            self.end_pending = self.end_pending.max(before);
            self.polls = self.polls.saturating_add(1);
            (self.polls >= GIVE_UP_POLLS)
                .then(|| Err(Failure::new(FailureReason::Timeout, E_TIMEOUT)))
        }
    }

    /// Milliseconds from conservative edges: no stage GPU work before `begin`, the end event
    /// still pending (or not yet ended) at `end_pending`, complete by `end`. Resolution is how
    /// long before `end` the stage could have finished. Reject edges out of order and a zero
    /// frequency instead of turning them into zero-ms results.
    pub fn sample(
        begin: u64,
        end_pending: u64,
        end: u64,
        frequency: u64,
    ) -> Result<Sample, Failure> {
        if frequency == 0 {
            return Err(Failure::new(FailureReason::Frequency, E_FAIL));
        }
        if end < begin || end < end_pending {
            return Err(Failure::new(FailureReason::Sequence, E_FAIL));
        }
        let ms = |ticks: u64| ticks as f64 * 1000.0 / frequency as f64;
        let total = ms(end - begin);
        if total > GIVE_UP_MS {
            return Ok(Sample::Unavailable);
        }
        // The stage cannot have finished before it began.
        Ok(Sample::Valid([
            total,
            ms(end - end_pending.max(begin)),
            0.0,
            0.0,
        ]))
    }

    /// What the poll thread hands back to the apply side.
    #[derive(Debug, PartialEq)]
    pub enum Outcome {
        Measured(Sample),
        Failed(Failure),
    }

    /// The apply side ↔ poll thread hand-off. Neither side holds it across a D3D call or a sleep.
    #[derive(Default)]
    pub struct Mailbox {
        /// Issued by the apply side, taken by the poll thread.
        pub flight: Option<Flight>,
        /// Posted by the poll thread (or by apply when the end flush already saw it complete).
        pub outcome: Option<Outcome>,
    }
    impl Mailbox {
        /// Apply side: a failure at once; a measurement only when the ring's read delay passed.
        pub fn take(&mut self, ready: bool) -> Result<Option<Sample>, Failure> {
            match self.outcome.take() {
                Some(Outcome::Failed(failure)) => Err(failure),
                Some(Outcome::Measured(sample)) if ready => Ok(Some(sample)),
                other => {
                    self.outcome = other;
                    Ok(None)
                }
            }
        }
    }

    #[derive(Default)]
    pub struct Window {
        frames: u32,
        valid: u32,
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
            // Events have no disjoint intervals; the ABI field stays for layout.
            stats.disjoint = 0;
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
    fn converts_ticks_to_an_upper_bound_and_rejects_zero_frequency_and_reversed_edges() {
        // 10 MHz QPC: begin 1000, end seen pending at 9000, complete by 26000.
        assert_eq!(
            sample(1_000, 9_000, 26_000, 10_000_000),
            Ok(Sample::Valid([2.5, 1.7, 0.0, 0.0]))
        );
        // A pending read before the start edge cannot place the stage's end before its begin.
        assert_eq!(
            sample(5_000, 1_000, 7_000, 1_000_000),
            Ok(Sample::Valid([2.0, 2.0, 0.0, 0.0]))
        );
        for (begin, pending, end, frequency, reason) in [
            (0, 0, 0, 0, FailureReason::Frequency),
            (10, 0, 9, 1_000, FailureReason::Sequence),
            (0, 10, 9, 1_000, FailureReason::Sequence),
        ] {
            let failure = sample(begin, pending, end, frequency).unwrap_err();
            assert_eq!(failure.reason, reason);
            assert!(failure.code < 0);
        }
        // Over GIVE_UP_MS (e.g. suspended mid-measurement): unavailable, never a time.
        assert_eq!(
            sample(0, 0, 10_000_001, 10_000_000),
            Ok(Sample::Unavailable)
        );
        assert!(matches!(
            sample(0, 0, 10_000_000, 10_000_000),
            Ok(Sample::Valid(_))
        ));
    }
    #[test]
    fn windows_average_only_valid_frames_and_reset_every_counter() {
        let mut window = Window::default();
        for _ in 0..119 {
            assert!(!window.record(Sample::Valid([2.0, 0.5, 0.0, 0.0])));
        }
        assert!(!window.record(Sample::Valid([6.0, 1.0, 0.0, 0.0])));
        for _ in 0..119 {
            assert!(!window.record(Sample::Unavailable));
        }
        assert!(window.record(Sample::Unavailable));
        let mut stats = BaleToneMapTimingStats {
            disjoint: 7,
            ..Default::default()
        };
        assert!(window.finish(&mut stats));
        assert_eq!(
            (
                stats.window,
                stats.frames,
                stats.valid,
                stats.disjoint,
                stats.unavailable
            ),
            (1, 240, 120, 0, 120)
        );
        assert_eq!(stats.max_ms, [6.0, 1.0, 0.0, 0.0]);
        assert!((stats.avg_ms[0] - 244.0 / 120.0).abs() < 1e-9);
        assert!((stats.avg_ms[1] - 60.5 / 120.0).abs() < 1e-9);
        assert_eq!(stats.avg_ms[2..], [0.0, 0.0]);
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
        // The poll thread itself gives up after about a second of pending polls.
        let mut flight = Flight::new(0, true, 0, 10_000_000);
        for poll in 1..GIVE_UP_POLLS {
            let at = u64::from(poll) * POLL_INTERVAL_US * 10;
            assert_eq!(flight.end_polled(at, at + 1, false), None);
        }
        let failure = flight.end_polled(u64::MAX - 1, u64::MAX, false);
        assert!(matches!(
            failure,
            Some(Err(Failure {
                reason: FailureReason::Timeout,
                code
            })) if code < 0
        ));
        let budget_us = u64::from(GIVE_UP_POLLS) * POLL_INTERVAL_US;
        assert!((500_000..=2_000_000).contains(&budget_us));
    }

    #[test]
    fn the_start_edge_is_the_later_of_the_submission_and_the_last_pending_start_read() {
        let frequency = 1_000_000; // 1 tick = 1 µs
        // Start complete at its own submission request, end complete at its flush: begin is the
        // submission request, end the flush read.
        let mut flight = Flight::new(100, true, 400, frequency);
        assert!(flight.start_done());
        assert_eq!(
            flight.end_polled(400, 450, true),
            Some(Ok(Sample::Valid([0.35, 0.05, 0.0, 0.0])))
        );

        // The GPU was busy: the poll thread saw the start pending twice, then complete, then the
        // end pending until the fourth read.
        let mut flight = Flight::new(100, false, 400, frequency);
        assert_eq!(flight.end_polled(400, 401, false), None);
        flight.start_polled(600, false);
        assert_eq!(flight.end_polled(601, 602, false), None);
        flight.start_polled(900, false);
        assert_eq!(flight.end_polled(901, 902, false), None);
        flight.start_polled(1_200, true);
        assert!(flight.start_done());
        assert_eq!(flight.end_polled(1_201, 1_202, false), None);
        assert_eq!(
            flight.end_polled(1_500, 1_510, true),
            Some(Ok(Sample::Valid([0.61, 0.309, 0.0, 0.0])))
        );

        // First read after apply: the start already done, so only its submission bounds it.
        let mut flight = Flight::new(100, false, 400, frequency);
        flight.start_polled(700, true);
        assert_eq!(
            flight.end_polled(701, 1_100, true),
            Some(Ok(Sample::Valid([1.0, 0.7, 0.0, 0.0])))
        );

        // Start seen pending and the end complete in the same pass: both happened after the
        // start read, so resolution never exceeds the total.
        let mut flight = Flight::new(100, false, 400, frequency);
        flight.start_polled(2_000, false);
        let Some(Ok(Sample::Valid([total, resolution, rest @ ..]))) =
            flight.end_polled(2_001, 2_010, true)
        else {
            panic!("expected a measurement");
        };
        assert!((total - 0.01).abs() < 1e-12);
        assert!((resolution - total).abs() < 1e-12);
        assert_eq!(rest, [0.0, 0.0]);
    }

    #[test]
    fn a_flight_handed_to_the_poll_thread_comes_back_once_as_a_measurement() {
        // What timing_gpu does across its two threads, without a device: apply issues a flight
        // whose end was still pending at its flush, the poll thread takes it and posts the result,
        // and apply accounts it after the read delay.
        let mut report = collecting();
        let mut mailbox = Mailbox::default();
        report.ring.advance();
        assert_eq!(report.ring.reserve(), Some(0));
        let mut flight = Flight::new(100, false, 400, 1_000_000);
        assert_eq!(flight.end_polled(400, 410, false), None);
        mailbox.flight = Some(flight);

        let mut polled = mailbox.flight.take().expect("handed over");
        assert_eq!(mailbox.flight, None);
        polled.start_polled(500, true);
        let mut at = 500;
        let outcome = loop {
            at += POLL_INTERVAL_US;
            if let Some(result) = polled.end_polled(at, at + 2, at >= 1_500) {
                break result;
            }
        };
        mailbox.outcome = Some(match outcome {
            Ok(sample) => Outcome::Measured(sample),
            Err(failure) => Outcome::Failed(failure),
        });

        for _ in 1..READ_DELAY {
            report.ring.advance();
            assert_eq!(mailbox.take(report.ring.ready_to_poll(0)), Ok(None));
            assert_eq!(report.ring.reserve(), None);
            report.record(Sample::Unavailable);
        }
        report.ring.advance();
        let sample = mailbox
            .take(report.ring.ready_to_poll(0))
            .expect("no failure")
            .expect("ready");
        assert!(report.ring.release(0));
        // Poll reads at 500 µs + n × 250 µs: the end is seen at 1500 µs, last pending at 1250 µs.
        assert_eq!(sample, Sample::Valid([1.402, 0.252, 0.0, 0.0]));
        report.record(sample);
        assert_eq!(mailbox.take(true), Ok(None));
        report.close();
        assert_eq!(
            (report.stats.frames, report.stats.valid, report.stats.state),
            (4, 1, 3)
        );
    }

    #[test]
    fn the_mailbox_hands_failures_back_at_once_and_measurements_after_the_read_delay() {
        let mut mailbox = Mailbox::default();
        assert_eq!(mailbox.take(true), Ok(None));
        mailbox.outcome = Some(Outcome::Measured(Sample::Valid([1.0, 0.25, 0.0, 0.0])));
        assert_eq!(mailbox.take(false), Ok(None));
        assert!(mailbox.outcome.is_some());
        assert_eq!(
            mailbox.take(true),
            Ok(Some(Sample::Valid([1.0, 0.25, 0.0, 0.0])))
        );
        assert_eq!(mailbox.outcome, None);
        let failure = Failure::new(FailureReason::Read, 0x887A0005u32 as i32);
        mailbox.outcome = Some(Outcome::Failed(failure));
        assert_eq!(mailbox.take(false), Err(failure));
        assert_eq!(mailbox.take(true), Ok(None));
    }

    fn collecting() -> Report {
        Report::new(BaleToneMapTimingStats {
            session: 1,
            state: 1,
            ..Default::default()
        })
    }

    #[test]
    fn get_data_accepts_only_complete_or_pending_and_rejects_mismatched_sizes() {
        assert_eq!(event_state(0, 1), Ok(true));
        assert_eq!(event_state(0, -1), Ok(true));
        assert_eq!(event_state(1, 0), Ok(false));
        assert_eq!(event_state(1, 1), Ok(false));
        for (code, value) in [
            (0x80004005u32 as i32, 1),
            (0x887A0005u32 as i32, 0),
            (2, 1),
            (0, 0),
        ] {
            let failure = event_state(code, value).unwrap_err();
            assert_eq!(failure.reason, FailureReason::Read);
            assert!(failure.code < 0);
        }
        assert_eq!(data_size(4, size_of::<i32>()), Ok(()));
        for size in [0, 1, 8, 16, u32::MAX] {
            assert_eq!(data_size(size, 4).unwrap_err().reason, FailureReason::Size);
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
            FailureReason::Poller,
            FailureReason::Timeout,
        ] {
            let mut report = collecting();
            report.record(Sample::Valid([4.0, 0.5, 0.0, 0.0]));
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
    fn a_pending_measurement_stays_owned_until_release_and_close_publishes_partial_window() {
        let mut report = collecting();
        let mut mailbox = Mailbox::default();
        assert_eq!(report.attempt(|r| Ok(r.ring.reserve())), Some(Some(0)));
        // The poll thread finishes early; apply still waits for the read delay.
        mailbox.outcome = Some(Outcome::Measured(Sample::Valid([1.0, 0.25, 0.0, 0.0])));
        for _ in 1..READ_DELAY {
            report.attempt(|r| {
                r.ring.advance();
                assert_eq!(mailbox.take(r.ring.ready_to_poll(0))?, None);
                assert_eq!(r.ring.reserve(), None);
                r.record(Sample::Unavailable);
                Ok(())
            });
        }
        assert_eq!(report.ring.pending(), 1);
        report.attempt(|r| {
            r.ring.advance();
            let sample = mailbox.take(r.ring.ready_to_poll(0))?.expect("ready");
            assert!(r.ring.release(0));
            r.record(sample);
            assert_eq!(r.ring.reserve(), Some(0));
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
        assert_eq!(report.stats.max_ms, [1.0, 0.25, 0.0, 0.0]);
    }
}
