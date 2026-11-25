//! Direct play's retry window: how long the original's range stream (range_stream.rs) keeps retrying a
//! block the player waits for through an outage, and when a later outage gets a window of its own.
//!
//! A port of silo-android's `MediaLoadRetryPolicy.kt` (`SiloMediaLoadErrorHandlingPolicy`, with
//! `bb59172f8` "renew direct play retries after sustained progress"): a transient failure waits 1, 2, 4,
//! then 8 s between attempts while the outage's window lasts (90 s, measured or summed from the waits,
//! whichever is longer). One window used to cover a whole load task, which for a progressive original is
//! the whole film: an outage an hour after an earlier one found that window long spent and gave up at
//! once. Now a failure that follows sustained progress opens a fresh window: at least 512 KiB delivered,
//! over at least 20 s of successful reads, ending at least 512 KiB away from where the last substantial
//! run ended. Short bursts between failures, or re-reading the same bytes, stay one outage.
//!
//! Android measures one long HTTP request per load; Bale fetches 1 MiB blocks, so the run of successful
//! block fetches since the last failure stands in for the failed request's reads, and the failing
//! block's offset for the request's end offset. Times are milliseconds on the caller's monotonic clock.

/// Bytes a run must deliver to count as substantial (Android `RECOVERY_BYTES`).
pub const RECOVERY_BYTES: u64 = 512 * 1024;
/// How long a run's successful reads must span to count as sustained (Android `RECOVERY_INTERVAL_MS`).
pub const RECOVERY_INTERVAL_MS: u64 = 20_000;
/// One outage's window (Android `MEDIA_LOAD_RETRY_WINDOW_MS`).
pub const WINDOW_MS: u64 = 90_000;
/// The waits before attempts 1, 2, 3 and every later one (Android `mediaLoadBackoffDelayMs`).
pub const BACKOFF_MS: [u64; 4] = [1_000, 2_000, 4_000, 8_000];

/// The window's thresholds: [`Policy::DIRECT_PLAY`] in the app; tests scale them down.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Policy {
    pub window_ms: u64,
    pub recovery_bytes: u64,
    pub recovery_interval_ms: u64,
    pub backoff_ms: [u64; 4],
}

impl Policy {
    pub const DIRECT_PLAY: Policy = Policy {
        window_ms: WINDOW_MS,
        recovery_bytes: RECOVERY_BYTES,
        recovery_interval_ms: RECOVERY_INTERVAL_MS,
        backoff_ms: BACKOFF_MS,
    };

    fn backoff(&self, attempt: u32) -> u64 {
        self.backoff_ms[(attempt.max(1) as usize - 1).min(self.backoff_ms.len() - 1)]
    }
}

/// The current outage: when its window opened, how many failures it has seen, and where the last
/// substantial run before a failure ended (Android `RetryWindow`).
#[derive(Debug, Clone, Copy)]
struct Window {
    started_ms: u64,
    last_substantial_end: Option<u64>,
    failures: u32,
}

/// Successful block fetches since the last failure (Android `ReadProgress` of the failing open).
#[derive(Debug, Default, Clone, Copy)]
struct Run {
    bytes: u64,
    first_ms: Option<u64>,
    last_ms: u64,
}

/// What one failure decided, for the caller and its diagnostics.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Decision {
    /// The wait before the next attempt, or None: this outage's window is spent.
    pub delay_ms: Option<u64>,
    /// The failure opened a fresh window after sustained progress (not the first window).
    pub renewed: bool,
}

#[derive(Debug)]
pub struct RetryWindow {
    policy: Policy,
    window: Option<Window>,
    run: Run,
}

impl RetryWindow {
    pub fn new(policy: Policy) -> Self {
        RetryWindow {
            policy,
            window: None,
            run: Run::default(),
        }
    }

    /// A block of `bytes` arrived at `now_ms` (prefetched or demanded).
    pub fn delivered(&mut self, now_ms: u64, bytes: u64) {
        let run = &mut self.run;
        run.bytes = run.bytes.saturating_add(bytes);
        run.first_ms.get_or_insert(now_ms);
        run.last_ms = run.last_ms.max(now_ms);
    }

    /// The player's block at `offset` failed with a transient error (after the stream's immediate
    /// retry, which covers a stale pooled connection): wait `delay_ms` and try again, or give up.
    pub fn failed(&mut self, now_ms: u64, offset: u64) -> Decision {
        let policy = self.policy;
        let run = std::mem::take(&mut self.run);
        // A file can stay on one stream for a whole film. Repeated short runs, or re-reading the same
        // part of the file, are one outage (bb59172f8).
        let substantial = run.bytes >= policy.recovery_bytes;
        let sustained = run
            .first_ms
            .is_some_and(|first| run.last_ms.saturating_sub(first) >= policy.recovery_interval_ms);
        let mut renewed = false;
        let window = match self.window {
            None => Window {
                started_ms: now_ms,
                last_substantial_end: substantial.then_some(offset),
                failures: 1,
            },
            Some(previous) => {
                let moved = previous
                    .last_substantial_end
                    .is_none_or(|end| end.abs_diff(offset) >= policy.recovery_bytes);
                let end = if substantial {
                    Some(offset)
                } else {
                    previous.last_substantial_end
                };
                if substantial && sustained && moved {
                    renewed = true;
                    Window {
                        started_ms: now_ms,
                        last_substantial_end: end,
                        failures: 1,
                    }
                } else {
                    Window {
                        last_substantial_end: end,
                        failures: previous.failures.saturating_add(1),
                        ..previous
                    }
                }
            }
        };
        self.window = Some(window);
        let delay = policy.backoff(window.failures);
        // Whichever is larger: the time that has passed, or the waits intended so far (Android: the
        // measured value includes time inside failed attempts, the derived one bounds callers that
        // cannot measure).
        let intended: u64 = (1..window.failures).map(|n| policy.backoff(n)).sum();
        let elapsed = now_ms.saturating_sub(window.started_ms).max(intended);
        Decision {
            delay_ms: (elapsed.saturating_add(delay) <= policy.window_ms).then_some(delay),
            renewed,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const MB: u64 = 1_000_000;

    /// Delivers `bytes` in two blocks, the first at `from_ms` and the last at `to_ms`.
    fn deliver(window: &mut RetryWindow, bytes: u64, from_ms: u64, to_ms: u64) {
        window.delivered(from_ms, bytes / 2);
        window.delivered(to_ms, bytes - bytes / 2);
    }

    fn delay(decision: Decision) -> Option<u64> {
        decision.delay_ms
    }

    // DirectPlayRetryWindowTest.laterOutageGetsFreshWindowAfterSustainedProgress
    #[test]
    fn a_later_outage_gets_a_fresh_window_after_sustained_progress() {
        let mut w = RetryWindow::new(Policy::DIRECT_PLAY);
        deliver(&mut w, MB, 0, 0);
        assert_eq!(delay(w.failed(0, MB)), Some(1_000));
        // Recovered: 1000 MB over eleven minutes, then the next outage far into the file.
        deliver(&mut w, 1_000 * MB, 1_000, 660_000);
        let renewed = w.failed(660_000, 1_001 * MB);
        assert_eq!(
            renewed,
            Decision {
                delay_ms: Some(1_000),
                renewed: true
            }
        );
        assert_eq!(delay(w.failed(690_000, 1_001 * MB)), Some(2_000));
        // 90 s after the fresh window opened, the next wait no longer fits.
        assert_eq!(delay(w.failed(750_000, 1_001 * MB)), None);
    }

    // The same outages without the renewal: the first window, opened at 0, is long spent.
    #[test]
    fn without_progress_a_late_failure_finds_the_window_spent() {
        let mut w = RetryWindow::new(Policy::DIRECT_PLAY);
        assert_eq!(delay(w.failed(0, 0)), Some(1_000));
        assert_eq!(delay(w.failed(660_000, MB)), None);
    }

    // DirectPlayRetryWindowTest.rereadingSameBytesAfterFailedReopenDoesNotRenewWindow
    #[test]
    fn re_reading_the_same_bytes_does_not_renew_the_window() {
        let mut w = RetryWindow::new(Policy::DIRECT_PLAY);
        deliver(&mut w, 2 * MB, 0, 0);
        assert_eq!(delay(w.failed(0, 2 * MB)), Some(1_000));
        assert_eq!(delay(w.failed(60_000, 2 * MB)), Some(2_000));
        // A sustained, substantial run that fails at the same place again is the same outage.
        deliver(&mut w, 2 * MB, 61_000, 100_000);
        let decision = w.failed(100_000, 2 * MB);
        assert!(!decision.renewed);
        assert_eq!(decision.delay_ms, None);
    }

    // DirectPlayRetryWindowTest.burstsSeparatedByReadTimeoutsExhaustTheOriginalWindow
    #[test]
    fn bursts_separated_by_timeouts_exhaust_the_original_window() {
        let mut w = RetryWindow::new(Policy::DIRECT_PLAY);
        for burst in 0..3u64 {
            let at = burst * 31_000;
            deliver(&mut w, MB, at, at + 10);
            w.failed(at + 30_000, (burst + 1) * MB);
        }
        deliver(&mut w, MB, 93_000, 93_010);
        assert_eq!(delay(w.failed(123_000, 4 * MB)), None);
    }

    // DirectPlayRetryWindowTest.frequentCutsExhaustWindowEvenWhenTheyDeliverData
    #[test]
    fn frequent_cuts_exhaust_the_window_even_when_they_deliver_data() {
        let mut w = RetryWindow::new(Policy::DIRECT_PLAY);
        for cut in 0..9u64 {
            deliver(&mut w, 2 * MB, cut * 10_000, cut * 10_000);
            w.failed(cut * 10_000, (cut + 1) * 2 * MB);
        }
        deliver(&mut w, 2 * MB, 90_000, 90_000);
        assert_eq!(delay(w.failed(90_000, 20 * MB)), None);
    }

    // DirectPlayRetryWindowTest.fewBytesOrLargeNonProgressiveLoadDoNotRenewWindow
    #[test]
    fn a_few_bytes_do_not_renew_the_window() {
        let mut w = RetryWindow::new(Policy::DIRECT_PLAY);
        deliver(&mut w, 2 * MB, 0, 0);
        assert_eq!(delay(w.failed(0, 2 * MB)), Some(1_000));
        deliver(&mut w, 4_096, 5_000, 100_000);
        assert_eq!(delay(w.failed(100_000, 3 * MB)), None);
    }

    #[test]
    fn the_waits_follow_android_and_stop_inside_the_window() {
        let mut w = RetryWindow::new(Policy::DIRECT_PLAY);
        let mut now = 0;
        let mut waits = Vec::new();
        while let Some(wait) = w.failed(now, 7 * MB).delay_ms {
            waits.push(wait);
            now += wait;
        }
        // 1 + 2 + 4 + 8 × 10 = 87 s of waits; another 8 s would pass 90 s.
        assert_eq!(&waits[..5], &[1_000, 2_000, 4_000, 8_000, 8_000]);
        assert_eq!(waits.iter().sum::<u64>(), 87_000);
        // Time spent inside failed attempts counts too: slow failures leave fewer attempts.
        let mut slow = RetryWindow::new(Policy::DIRECT_PLAY);
        assert_eq!(delay(slow.failed(0, 0)), Some(1_000));
        assert_eq!(delay(slow.failed(31_000, 0)), Some(2_000));
        assert_eq!(delay(slow.failed(63_000, 0)), Some(4_000));
        assert_eq!(delay(slow.failed(87_000, 0)), None);
    }

    #[test]
    fn progress_resets_at_every_failure() {
        let mut w = RetryWindow::new(Policy::DIRECT_PLAY);
        deliver(&mut w, 10 * MB, 0, 30_000);
        assert_eq!(delay(w.failed(30_000, 10 * MB)), Some(1_000));
        // The run before the first failure opened that window; it can't renew the next one.
        assert_eq!(delay(w.failed(31_000, 10 * MB)), Some(2_000));
        assert!(!w.failed(33_000, 40 * MB).renewed);
    }
}
