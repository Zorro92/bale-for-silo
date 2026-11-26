//! Debug media faults for console checks (`bale-debug.sh media-fault`) and the recovery counters that
//! command reports. Nothing here changes playback until the debug server arms a fault: faults are memory
//! only and off at start, and the counters only count.
//!
//! - **outage**: for the next N seconds every Rust media request (range opens and blocks, HLS playlists and
//!   segments, the progressive open) fails as a network error, and reads of bodies already open fail too,
//!   as a server that went away mid-response would.
//! - **empty tail**: the next HLS mount answers a complete playlist's last N segments itself with segments
//!   that hold no media, as Silo answers segments past a finished transcode's real end (hls.rs).
//!
//! Arming exists only with the `debug-probes` feature (debug-server builds, and the Linux test library): without it
//! there is no `media_fault.set` call, [`outage`] is a constant `false` and [`take_empty_tail`] a constant 0, so the
//! hooks in the streams compile to nothing. The counters count in every build.

#[cfg(feature = "debug-probes")]
use std::sync::OnceLock;
#[cfg(feature = "debug-probes")]
use std::sync::atomic::AtomicU32;
use std::sync::atomic::{AtomicU64, Ordering};
#[cfg(feature = "debug-probes")]
use std::time::Instant;

#[cfg(feature = "debug-probes")]
use serde::{Deserialize, Serialize};

#[cfg(feature = "debug-probes")]
fn now_ms() -> u64 {
    static EPOCH: OnceLock<Instant> = OnceLock::new();
    let epoch = *EPOCH.get_or_init(Instant::now);
    epoch.elapsed().as_millis().min(u128::from(u64::MAX)) as u64
}

/// The end of the armed outage on [`now_ms`]'s clock, or 0 for none.
#[cfg(feature = "debug-probes")]
static OUTAGE_UNTIL_MS: AtomicU64 = AtomicU64::new(0);

/// Range stream (original_http): waits taken inside an outage window (retry_window.rs).
pub static RANGE_RETRIES: AtomicU64 = AtomicU64::new(0);
/// Range stream: fresh windows opened after sustained progress.
pub static RANGE_RENEWALS: AtomicU64 = AtomicU64::new(0);
/// Range stream: outage windows spent (the read then fails).
pub static RANGE_GIVEN_UP: AtomicU64 = AtomicU64::new(0);
/// HLS: media segments that held no media (segment_media.rs), the server's or the debug tail's.
pub static HLS_EMPTY_SEGMENTS: AtomicU64 = AtomicU64::new(0);
/// HLS: segments the debug empty tail answered.
pub static HLS_EMPTY_SERVED: AtomicU64 = AtomicU64::new(0);
/// HLS: mounts whose segments to the playlist's end held no media (started past the media's end).
pub static HLS_NO_MEDIA: AtomicU64 = AtomicU64::new(0);
/// HLS: waits taken through an outage (outage_budget.rs).
pub static HLS_RETRIES: AtomicU64 = AtomicU64::new(0);
/// HLS: bodies cut mid-way and requested again.
pub static HLS_RESUMES: AtomicU64 = AtomicU64::new(0);
/// HLS: outage budgets spent (the stream then ends as a fault once its buffer runs out).
pub static HLS_GIVEN_UP: AtomicU64 = AtomicU64::new(0);

/// The debug empty tail armed for the next HLS mount.
#[cfg(feature = "debug-probes")]
static EMPTY_TAIL: AtomicU32 = AtomicU32::new(0);

/// The armed debug empty tail, for the HLS mount opening now (then disarmed).
#[cfg(feature = "debug-probes")]
pub fn take_empty_tail() -> usize {
    EMPTY_TAIL.swap(0, Ordering::Relaxed) as usize
}

/// Production builds: no debug server, so nothing is ever armed.
#[cfg(not(feature = "debug-probes"))]
pub const fn take_empty_tail() -> usize {
    0
}

/// True while a debug outage is armed; always false in normal playback.
#[cfg(feature = "debug-probes")]
pub fn outage() -> bool {
    let until = OUTAGE_UNTIL_MS.load(Ordering::Relaxed);
    until != 0 && now_ms() < until
}

/// Production builds: no debug server, so the outage hook is always off.
#[cfg(not(feature = "debug-probes"))]
pub const fn outage() -> bool {
    false
}

pub fn count(counter: &AtomicU64) {
    counter.fetch_add(1, Ordering::Relaxed);
}

#[cfg(feature = "debug-probes")]
#[derive(Deserialize, Default)]
#[serde(default)]
pub struct SetArgs {
    /// Seconds of outage from now; 0 ends an armed one.
    pub outage_seconds: Option<u64>,
    /// Empty segments for the next HLS mount's playlist end; 0 disarms.
    pub empty_tail: Option<u32>,
}

#[cfg(feature = "debug-probes")]
#[derive(Serialize, Debug, PartialEq, Eq)]
pub struct State {
    pub outage_remaining_ms: u64,
    pub empty_tail_armed: u32,
    pub range_retries: u64,
    pub range_renewals: u64,
    pub range_given_up: u64,
    pub hls_empty_segments: u64,
    pub hls_empty_served: u64,
    pub hls_no_media: u64,
    pub hls_retries: u64,
    pub hls_resumes: u64,
    pub hls_given_up: u64,
}

#[cfg(feature = "debug-probes")]
pub fn set(args: SetArgs) -> State {
    if let Some(seconds) = args.outage_seconds {
        let until = if seconds == 0 {
            0
        } else {
            now_ms()
                .saturating_add(seconds.saturating_mul(1_000))
                .max(1)
        };
        OUTAGE_UNTIL_MS.store(until, Ordering::Relaxed);
    }
    if let Some(segments) = args.empty_tail {
        EMPTY_TAIL.store(segments, Ordering::Relaxed);
    }
    state()
}

#[cfg(feature = "debug-probes")]
pub fn state() -> State {
    let until = OUTAGE_UNTIL_MS.load(Ordering::Relaxed);
    let get = |counter: &AtomicU64| counter.load(Ordering::Relaxed);
    State {
        outage_remaining_ms: until.saturating_sub(now_ms()),
        empty_tail_armed: EMPTY_TAIL.load(Ordering::Relaxed),
        range_retries: get(&RANGE_RETRIES),
        range_renewals: get(&RANGE_RENEWALS),
        range_given_up: get(&RANGE_GIVEN_UP),
        hls_empty_segments: get(&HLS_EMPTY_SEGMENTS),
        hls_empty_served: get(&HLS_EMPTY_SERVED),
        hls_no_media: get(&HLS_NO_MEDIA),
        hls_retries: get(&HLS_RETRIES),
        hls_resumes: get(&HLS_RESUMES),
        hls_given_up: get(&HLS_GIVEN_UP),
    }
}

#[cfg(test)]
pub(crate) mod tests {
    #[cfg(feature = "debug-probes")]
    use super::*;
    use std::sync::{Mutex, MutexGuard, PoisonError};

    /// Tests that arm a fault, or that make media requests while one could be armed, take this lock: the
    /// fault is process-wide.
    pub(crate) fn exclusive() -> MutexGuard<'static, ()> {
        static LOCK: Mutex<()> = Mutex::new(());
        LOCK.lock().unwrap_or_else(PoisonError::into_inner)
    }

    #[cfg(feature = "debug-probes")]
    #[test]
    fn an_outage_is_armed_for_its_seconds_and_can_be_ended() {
        let _faults = exclusive();
        assert!(!outage());
        let state = set(SetArgs {
            outage_seconds: Some(30),
            ..Default::default()
        });
        assert!(outage());
        assert!((29_000..=30_000).contains(&state.outage_remaining_ms));
        // No key leaves the armed outage alone.
        assert_ne!(set(SetArgs::default()).outage_remaining_ms, 0);
        assert_eq!(
            set(SetArgs {
                outage_seconds: Some(0),
                ..Default::default()
            })
            .outage_remaining_ms,
            0
        );
        assert!(!outage());
    }
}
