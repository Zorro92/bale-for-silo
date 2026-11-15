//! Ported from `PlaybackThroughputMeter` and `ConnectionSpeedMeter`. Both are fed per network
//! sample or read, so they live behind handles (abi_meters) and take plain numbers.

use std::collections::VecDeque;

const TICKS_PER_SECOND: f64 = 10_000_000.0;

/// Samples cumulative network counters once a second, keeping the last five samples. Idle playback
/// from cache expires the estimate instead of misreporting zero as the network's capacity.
#[derive(Debug, Default)]
pub struct ThroughputMeter {
    window: VecDeque<(i64, i64)>,
    last_bytes: i64,
    last_ticks: i64,
}

const WINDOW_SAMPLES: usize = 5;

impl ThroughputMeter {
    /// Bits per second, or `None` when nothing moved in the window.
    pub fn sample(&mut self, downloaded_bytes: i64, download_ticks: i64) -> Option<i64> {
        if downloaded_bytes < self.last_bytes || download_ticks < self.last_ticks {
            // A replacement transport starts fresh counters; never mix its rate with the old mount.
            self.window.clear();
            self.last_bytes = 0;
            self.last_ticks = 0;
        }
        self.window.push_back((
            downloaded_bytes.wrapping_sub(self.last_bytes),
            download_ticks.wrapping_sub(self.last_ticks),
        ));
        self.last_bytes = downloaded_bytes;
        self.last_ticks = download_ticks;
        if self.window.len() > WINDOW_SAMPLES {
            self.window.pop_front();
        }
        let bytes: f64 = self.window.iter().map(|(b, _)| *b as f64).sum();
        let seconds: f64 = self
            .window
            .iter()
            .map(|(_, t)| *t as f64 / TICKS_PER_SECOND)
            .sum();
        if bytes <= 0.0 || seconds <= 0.0 || bytes.is_nan() || seconds.is_nan() {
            return None;
        }
        // Math.Round rounds half to even; `(long)` saturates at long.MaxValue.
        let bps = (bytes * 8.0 / seconds).round_ties_even();
        Some(if bps >= i64::MAX as f64 {
            i64::MAX
        } else {
            bps as i64
        })
    }
}

pub const WINDOW_MS: i64 = 500;
pub const MIN_KBPS: i64 = 100;
pub const MAX_KBPS: i64 = 1_000_000;
pub const QUICK_BYTES: i64 = 100_000_000;
pub const ACCURATE_BYTES: i64 = 1_000_000_000;

/// `ConnectionSpeedResult`.
#[repr(C)]
#[derive(Debug, Default, Clone, Copy, PartialEq)]
pub struct SpeedResult {
    pub kbps: i32,
    pub duration_ms: i32,
    pub bytes: i64,
    pub lower_bound: u8,
}

/// The connection speed test's arithmetic, from silo-server's buffering-quality design
/// (docs/design/2026-10-17-buffering-quality.md, "Measuring"; vectors in schemas/buffering-quality/v1).
/// The clock starts at the first byte of the body. Bytes count in the 500 ms window in which they
/// arrive; window 0 is ramp-up and never counts; a window in which nothing arrived counts at 0.
#[derive(Debug)]
pub struct SpeedMeter {
    quick: bool,
    target_kbps: Option<i32>,
    windows: Vec<i64>,
    total_bytes: i64,
    hit_window: Option<i64>,
}

impl SpeedMeter {
    /// Quick runs automatically; Accurate only from Test speed. A target under 100 can never be
    /// reported, so it measures nothing; keep it to fail at the end.
    pub fn new(quick: bool, target_kbps: Option<i32>) -> Self {
        Self {
            quick,
            target_kbps,
            windows: Vec::new(),
            total_bytes: 0,
            hit_window: None,
        }
    }

    fn requested_bytes(&self) -> i64 {
        if self.quick {
            QUICK_BYTES
        } else {
            ACCURATE_BYTES
        }
    }

    fn limit_ms(&self) -> i64 {
        if self.quick { 3_000 } else { 10_000 }
    }

    fn settled_windows(&self) -> i64 {
        if self.quick { 2 } else { 6 }
    }

    /// Records `bytes` arriving `elapsed_ms` after the first byte (both non-negative; C# checks).
    pub fn add(&mut self, elapsed_ms: i64, bytes: i64) {
        let window = (elapsed_ms / WINDOW_MS) as usize;
        if self.windows.len() <= window {
            self.windows.resize(window + 1, 0);
        }
        self.windows[window] = self.windows[window].wrapping_add(bytes);
        self.total_bytes = self.total_bytes.wrapping_add(bytes);
    }

    /// The time the test stops if it should stop by `now_ms`. A quick test with a target stops at
    /// the end of the first window after window 0 whose rate reaches it; every test stops at its limit.
    pub fn stop_at(&mut self, now_ms: i64) -> Option<i64> {
        if let (true, Some(target)) = (self.quick, self.target_kbps) {
            let closed = (now_ms / WINDOW_MS).min(self.limit_ms() / WINDOW_MS);
            for window in 1..closed {
                if rate(self.window(window), WINDOW_MS) >= i64::from(target) {
                    self.hit_window = Some(window);
                    return Some((window + 1) * WINDOW_MS);
                }
            }
        }
        (now_ms >= self.limit_ms()).then(|| self.limit_ms())
    }

    /// The result when the test stopped at `stop_ms` (or the clean end of the body, `ended_ms`), or
    /// `None` when it measured nothing.
    pub fn result(&self, stop_ms: i64, ended_ms: Option<i64>) -> Option<SpeedResult> {
        let stop_ms = ended_ms.map_or(stop_ms, |ended| stop_ms.min(ended));
        if let Some(hit) = self
            .hit_window
            .filter(|hit| (hit + 1) * WINDOW_MS <= stop_ms)
        {
            let target = i64::from(self.target_kbps.unwrap_or_default());
            return bound(clamp_high(target), true, self.window(hit), WINDOW_MS);
        }
        // Complete windows after window 0 that fully elapsed before the stop; a window cut short doesn't count.
        let complete = stop_ms / WINDOW_MS - 1;
        if complete < 1 {
            // The whole probe arrived before the first complete window after window 0: average it from
            // the first byte to the end. The ramp is inside, so it can only read low: a lower bound.
            let end = ended_ms.filter(|&end| end > 0)?;
            if self.total_bytes < self.requested_bytes() {
                return None;
            }
            let average = rate(self.total_bytes, end);
            if let (true, Some(target)) = (self.quick, self.target_kbps)
                && average >= i64::from(target)
            {
                return bound(target, true, self.total_bytes, end);
            }
            return bound(clamp_high(average), true, self.total_bytes, end);
        }
        let count = self.settled_windows().min(complete);
        let bytes: i64 = (complete - count + 1..=complete)
            .map(|w| self.window(w))
            .sum();
        let duration = count * WINDOW_MS;
        let rate = rate(bytes, duration);
        bound(clamp_high(rate), rate > MAX_KBPS, bytes, duration)
    }

    fn window(&self, window: i64) -> i64 {
        usize::try_from(window)
            .ok()
            .and_then(|w| self.windows.get(w))
            .copied()
            .unwrap_or(0)
    }

    /// Diagnostics: kbps per 500 ms window so far (window 0 first).
    pub fn window_kbps(&self) -> Vec<i64> {
        self.windows
            .iter()
            .map(|&bytes| rate(bytes, WINDOW_MS))
            .collect()
    }
}

fn rate(bytes: i64, duration_ms: i64) -> i64 {
    if duration_ms <= 0 {
        0
    } else {
        bytes.wrapping_mul(8) / duration_ms
    }
}

fn clamp_high(kbps: i64) -> i32 {
    kbps.min(MAX_KBPS) as i32
}

// Results under 100 measured nothing (above 1,000,000 is clamped by the callers, as a lower bound).
fn bound(kbps: i32, lower_bound: bool, bytes: i64, duration_ms: i64) -> Option<SpeedResult> {
    (i64::from(kbps) >= MIN_KBPS).then_some(SpeedResult {
        kbps,
        duration_ms: duration_ms as i32,
        bytes,
        lower_bound: u8::from(lower_bound),
    })
}
