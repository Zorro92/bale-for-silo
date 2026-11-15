//! Ported from `PlaybackFrameDeliveryMeter`, `PlaybackLateFrameTrace` and
//! `PlaybackFrameRecordProcessor`: per-frame delivery diagnostics. These run for every decoded
//! video sample, so they keep their state here behind a handle and take plain structs (abi_meters),
//! never JSON. Times are .NET ticks (100 ns) or milliseconds, as in the C# originals.

use std::collections::VecDeque;

use crate::dotnet::format_fixed;

const TICKS_PER_MILLISECOND: f64 = 10_000.0;
const TICKS_PER_SECOND: f64 = 10_000_000.0;
pub const RECENT_WINDOW_MILLISECONDS: i64 = 10_000;

/// `TimeSpan.TotalMilliseconds` / `TotalSeconds`.
fn ms(ticks: i64) -> f64 {
    ticks as f64 / TICKS_PER_MILLISECOND
}

fn seconds(ticks: i64) -> f64 {
    ticks as f64 / TICKS_PER_SECOND
}

/// `PlaybackFrameDeliverySnapshot` (without the derived per-second rate).
#[repr(C)]
#[derive(Debug, Default, Clone, Copy, PartialEq)]
pub struct DeliverySnapshot {
    pub late_frames: i64,
    pub total_frames_delivered: i64,
    pub recent_late_frames: i32,
    /// Bit 0: delivered_ahead set; bit 1: min/median set.
    pub flags: u32,
    pub delivered_ahead_seconds: f64,
    pub min_delivered_ahead_seconds: f64,
    pub median_delivered_ahead_seconds: f64,
}

/// Observes decoded video sample delivery, not rendering. A late frame has a presentation timestamp
/// behind the player clock when delivered. Display cadence and actual renderer drops are not measured.
#[derive(Debug)]
pub struct DeliveryMeter {
    recent: VecDeque<i64>,
    // Per-frame horizons only for a bounded debug probe, never the long-running player.
    ahead: Option<Vec<f64>>,
    delivered: i64,
    late: i64,
    observed: bool,
    seeking: bool,
    ignore_next: bool,
    delivered_end: Option<i64>,
}

impl DeliveryMeter {
    pub fn new(collect_ahead_statistics: bool) -> Self {
        Self {
            recent: VecDeque::new(),
            ahead: collect_ahead_statistics.then(Vec::new),
            delivered: 0,
            late: 0,
            observed: false,
            seeking: false,
            ignore_next: true,
            delivered_end: None,
        }
    }

    pub fn set_seeking(&mut self, seeking: bool) {
        self.seeking = seeking;
        self.invalidate_timeline();
    }

    pub fn invalidate_timeline(&mut self) {
        self.delivered_end = None;
        self.ignore_next = true;
    }

    /// Whether this sample counted as a late frame. `now_ms` is monotonic wall time, shared with `read`.
    pub fn observe(
        &mut self,
        timestamp: i64,
        duration: i64,
        position: i64,
        playing: bool,
        discontinuous: bool,
        now_ms: i64,
    ) -> bool {
        if self.seeking {
            return false;
        }
        let mut late = false;
        self.observed = true;
        self.delivered += 1;
        let end = timestamp.wrapping_add(duration);
        self.delivered_end = Some(end);
        // Exclude preroll, the first frame after a timeline reset, and non-playing delivery.
        if playing && !self.ignore_next && !discontinuous {
            if let Some(ahead) = &mut self.ahead {
                ahead.push(seconds(end.wrapping_sub(position)).max(0.0));
            }
            if timestamp < position {
                self.late += 1;
                self.recent.push_back(now_ms);
                late = true;
            }
        }
        self.ignore_next = !playing;
        self.trim(now_ms);
        late
    }

    pub fn read(&mut self, position: i64, now_ms: i64) -> Option<DeliverySnapshot> {
        self.trim(now_ms);
        if !self.observed {
            return None;
        }
        let mut snapshot = DeliverySnapshot {
            late_frames: self.late,
            total_frames_delivered: self.delivered,
            recent_late_frames: self.recent.len() as i32,
            ..DeliverySnapshot::default()
        };
        if let Some(end) = self.delivered_end {
            snapshot.flags |= 1;
            snapshot.delivered_ahead_seconds = seconds(end.wrapping_sub(position)).max(0.0);
        }
        if let Some(ahead) = self.ahead.as_ref().filter(|a| !a.is_empty()) {
            let mut sorted = ahead.clone();
            sorted.sort_by(f64::total_cmp);
            let middle = sorted.len() / 2;
            snapshot.flags |= 2;
            snapshot.min_delivered_ahead_seconds = sorted[0];
            snapshot.median_delivered_ahead_seconds = if sorted.len() % 2 == 0 {
                (sorted[middle - 1] + sorted[middle]) / 2.0
            } else {
                sorted[middle]
            };
        }
        Some(snapshot)
    }

    fn trim(&mut self, now_ms: i64) {
        while self
            .recent
            .front()
            .is_some_and(|&time| now_ms.wrapping_sub(time) >= RECENT_WINDOW_MILLISECONDS)
        {
            self.recent.pop_front();
        }
    }
}

/// The trace's environment at a delivery: cumulative counters the C# side samples (GC is .NET's).
#[repr(C)]
#[derive(Debug, Default, Clone, Copy, PartialEq)]
pub struct Environment {
    pub now_milliseconds: f64,
    pub gc_pause_total_ticks: i64,
    pub demand_waits: i64,
    pub demand_wait_ticks: i64,
    pub gen0: i32,
    pub gen1: i32,
    pub gen2: i32,
    /// Bit 0: demand_waits set; bit 1: demand_wait_ticks set.
    pub flags: u32,
}

impl Environment {
    fn demand_waits(&self) -> Option<i64> {
        (self.flags & 1 != 0).then_some(self.demand_waits)
    }

    fn demand_wait_ticks(&self) -> Option<i64> {
        (self.flags & 2 != 0).then_some(self.demand_wait_ticks)
    }
}

#[derive(Debug, Clone, Copy)]
struct Observation {
    wall_ms: f64,
    end: i64,
    environment: Environment,
}

#[derive(Debug, Clone)]
struct LateEvent {
    time_ms: f64,
    pts_ms: f64,
    lateness_ms: f64,
    ahead_ms: f64,
    gap_ms: f64,
    demand_waits: Option<i64>,
    demand_wait_ms: Option<f64>,
    gc_ms: f64,
    gen0: i32,
    gen1: i32,
    gen2: i32,
    class: &'static str,
}

const TRACE_CAPACITY: usize = 300;

/// Bounded delivery diagnostics correlating late samples with stream waits, GC pauses and delivery
/// gaps. Classes describe concurrent evidence, not proven causes or renderer drops.
#[derive(Debug, Default)]
pub struct LateFrameTrace {
    events: VecDeque<LateEvent>,
    previous: Option<Observation>,
    start_ms: f64,
    samples: i64,
    late: i64,
    stream: i64,
    gc: i64,
    gap: i64,
    other: i64,
    max_gap_ms: f64,
    long_gaps: i64,
}

impl LateFrameTrace {
    /// `late` is whether the delivery meter counted this sample as late; the first sample sets the start.
    pub fn sample(
        &mut self,
        now_ms: f64,
        timestamp: i64,
        duration: i64,
        position: i64,
        late: bool,
        environment: Environment,
    ) {
        self.samples += 1;
        if let Some(previous) = self.previous {
            let gap = now_ms - previous.wall_ms;
            let long_gap = gap > 2.0 * ms(duration);
            // Math.Max: NaN wins.
            self.max_gap_ms = if gap.is_nan() || self.max_gap_ms.is_nan() {
                f64::NAN
            } else {
                self.max_gap_ms.max(gap)
            };
            if long_gap {
                self.long_gaps += 1;
            }
            if late {
                let before = previous.environment;
                let waits = environment
                    .demand_waits()
                    .zip(before.demand_waits())
                    .map(|(a, b)| a.wrapping_sub(b));
                let wait_ms = environment
                    .demand_wait_ticks()
                    .zip(before.demand_wait_ticks())
                    .map(|(a, b)| ms(a.wrapping_sub(b)));
                let gc_ms = ms(environment
                    .gc_pause_total_ticks
                    .wrapping_sub(before.gc_pause_total_ticks));
                let class = if wait_ms.is_some_and(|w| w > 0.0) {
                    "stream"
                } else if gc_ms >= 4.0 {
                    "gc"
                } else if long_gap {
                    "gap"
                } else {
                    "other"
                };
                self.late += 1;
                match class {
                    "stream" => self.stream += 1,
                    "gc" => self.gc += 1,
                    "gap" => self.gap += 1,
                    _ => self.other += 1,
                }
                // Drop the oldest first so the ring never grows, even on late bursts.
                if self.events.len() == TRACE_CAPACITY {
                    self.events.pop_front();
                }
                self.events.push_back(LateEvent {
                    time_ms: now_ms - self.start_ms,
                    pts_ms: ms(timestamp),
                    lateness_ms: ms(position.wrapping_sub(timestamp)),
                    ahead_ms: ms(previous.end.wrapping_sub(position)),
                    gap_ms: gap,
                    demand_waits: waits,
                    demand_wait_ms: wait_ms,
                    gc_ms,
                    gen0: environment.gen0.wrapping_sub(before.gen0),
                    gen1: environment.gen1.wrapping_sub(before.gen1),
                    gen2: environment.gen2.wrapping_sub(before.gen2),
                    class,
                });
            }
        } else {
            // A baseline counts as a sample, but cannot produce a classified late event or gap.
            self.start_ms = now_ms;
        }
        // Every delivery advances the baseline, not just the deliveries that were late.
        self.previous = Some(Observation {
            wall_ms: now_ms,
            end: timestamp.wrapping_add(duration),
            environment,
        });
    }

    pub fn reset(&mut self) {
        *self = Self::default();
    }

    pub fn describe(&self) -> String {
        let f = |value: f64| format_fixed(value, 1);
        let mut text = format!(
            "samples={} late={} stream={} gc={} gap={} other={} max-gap={} long-gaps={}\n",
            self.samples,
            self.late,
            self.stream,
            self.gc,
            self.gap,
            self.other,
            f(self.max_gap_ms),
            self.long_gaps
        );
        for e in &self.events {
            let waits = match (e.demand_waits, e.demand_wait_ms) {
                (Some(count), Some(milliseconds)) => format!("{count}/{}", f(milliseconds)),
                _ => "-".to_owned(),
            };
            text += &format!(
                "t={} pts={} late={} ahead={} gap={} dw={waits} gc={} g0={} g1={} g2={} class={}\n",
                f(e.time_ms),
                f(e.pts_ms),
                f(e.lateness_ms),
                f(e.ahead_ms),
                f(e.gap_ms),
                f(e.gc_ms),
                e.gen0,
                e.gen1,
                e.gen2,
                e.class
            );
        }
        text
    }
}

/// `PlaybackFrameRecord`: kind 1 = video sample, 2 = Starting, 3 = seeking on, 4 = seeking off;
/// flags bit 0 = playing, bit 1 = discontinuous; times in ticks, `now` in QPC milliseconds.
#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct FrameRecord {
    pub kind: u32,
    pub flags: u32,
    pub now_milliseconds: f64,
    pub timestamp: i64,
    pub duration: i64,
    pub position: i64,
}

/// Processes a native observer's ordered timeline. The caller serializes snapshot writes, record
/// processing and reads (the C# wrapper also locks).
#[derive(Debug)]
pub struct FrameProcessor {
    meter: DeliveryMeter,
    trace: LateFrameTrace,
    snapshots: Vec<Environment>,
    next: usize,
    count: usize,
    seeking: bool,
}

impl FrameProcessor {
    pub fn new(capacity: usize, collect_ahead_statistics: bool) -> Self {
        Self {
            meter: DeliveryMeter::new(collect_ahead_statistics),
            trace: LateFrameTrace::default(),
            snapshots: vec![Environment::default(); capacity.max(1)],
            next: 0,
            count: 0,
            seeking: false,
        }
    }

    pub fn add_snapshot(&mut self, snapshot: Environment) {
        let capacity = self.snapshots.len();
        self.snapshots[self.next] = snapshot;
        self.next = (self.next + 1) % capacity;
        self.count = (self.count + 1).min(capacity);
    }

    /// Latest snapshot at or before delivery + 50 ms, allowing the managed sampling thread to resume
    /// just after a GC pause. If delivery predates retained history, use the oldest available baseline.
    pub fn snapshot_for(&self, now_ms: f64) -> Environment {
        let capacity = self.snapshots.len();
        for i in 0..self.count {
            let index = (self.next + capacity - 1 - i) % capacity;
            if self.snapshots[index].now_milliseconds <= now_ms + 50.0 {
                return self.snapshots[index];
            }
        }
        if self.count == 0 {
            Environment::default()
        } else {
            self.snapshots[(self.next + capacity - self.count) % capacity]
        }
    }

    pub fn process(&mut self, record: &FrameRecord) {
        match record.kind {
            1 => {
                // `(long)record.NowMilliseconds`: truncation toward zero, saturating like .NET 9+.
                let now = record.now_milliseconds as i64;
                let late = self.meter.observe(
                    record.timestamp,
                    record.duration,
                    record.position,
                    record.flags & 1 != 0,
                    record.flags & 2 != 0,
                    now,
                );
                if !self.seeking {
                    let snapshot = self.snapshot_for(record.now_milliseconds);
                    self.trace.sample(
                        record.now_milliseconds,
                        record.timestamp,
                        record.duration,
                        record.position,
                        late,
                        snapshot,
                    );
                }
            }
            2 => {
                self.meter.invalidate_timeline();
                self.trace.reset();
            }
            3 | 4 => {
                self.seeking = record.kind == 3;
                self.meter.set_seeking(self.seeking);
                self.trace.reset();
            }
            _ => {}
        }
    }

    pub fn read(&mut self, position: i64, now_ms: i64) -> Option<DeliverySnapshot> {
        self.meter.read(position, now_ms)
    }

    pub fn describe_trace(&self) -> String {
        self.trace.describe()
    }

    pub fn reset_trace(&mut self) {
        self.trace.reset();
    }
}
