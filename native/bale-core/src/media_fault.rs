//! Debug media faults for console checks (`bale-debug.sh media-fault`) and the recovery counters that
//! command reports. Nothing here changes playback until the debug server arms a fault: faults are memory
//! only and off at start, and the counters only count.
//!
//! - **outage**: for the next N seconds every Rust media request (range opens and blocks, HLS playlists and
//!   segments, the progressive open) fails as a network error, and reads of bodies already open fail too,
//!   as a server that went away mid-response would.

use std::sync::OnceLock;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Instant;

use serde::{Deserialize, Serialize};

fn now_ms() -> u64 {
    static EPOCH: OnceLock<Instant> = OnceLock::new();
    let epoch = *EPOCH.get_or_init(Instant::now);
    epoch.elapsed().as_millis().min(u128::from(u64::MAX)) as u64
}

/// The end of the armed outage on [`now_ms`]'s clock, or 0 for none.
static OUTAGE_UNTIL_MS: AtomicU64 = AtomicU64::new(0);

/// Range stream (original_http): waits taken inside an outage window (retry_window.rs).
pub static RANGE_RETRIES: AtomicU64 = AtomicU64::new(0);
/// Range stream: fresh windows opened after sustained progress.
pub static RANGE_RENEWALS: AtomicU64 = AtomicU64::new(0);
/// Range stream: outage windows spent (the read then fails).
pub static RANGE_GIVEN_UP: AtomicU64 = AtomicU64::new(0);

/// True while a debug outage is armed; always false in normal playback.
pub fn outage() -> bool {
    let until = OUTAGE_UNTIL_MS.load(Ordering::Relaxed);
    until != 0 && now_ms() < until
}

pub fn count(counter: &AtomicU64) {
    counter.fetch_add(1, Ordering::Relaxed);
}

#[derive(Deserialize, Default)]
#[serde(default)]
pub struct SetArgs {
    /// Seconds of outage from now; 0 ends an armed one.
    pub outage_seconds: Option<u64>,
}

#[derive(Serialize, Debug, PartialEq, Eq)]
pub struct State {
    pub outage_remaining_ms: u64,
    pub range_retries: u64,
    pub range_renewals: u64,
    pub range_given_up: u64,
}

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
    state()
}

pub fn state() -> State {
    let until = OUTAGE_UNTIL_MS.load(Ordering::Relaxed);
    let get = |counter: &AtomicU64| counter.load(Ordering::Relaxed);
    State {
        outage_remaining_ms: until.saturating_sub(now_ms()),
        range_retries: get(&RANGE_RETRIES),
        range_renewals: get(&RANGE_RENEWALS),
        range_given_up: get(&RANGE_GIVEN_UP),
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use std::sync::{Mutex, MutexGuard, PoisonError};

    /// Tests that arm a fault, or that make media requests while one could be armed, take this lock: the
    /// fault is process-wide.
    pub(crate) fn exclusive() -> MutexGuard<'static, ()> {
        static LOCK: Mutex<()> = Mutex::new(());
        LOCK.lock().unwrap_or_else(PoisonError::into_inner)
    }

    #[test]
    fn an_outage_is_armed_for_its_seconds_and_can_be_ended() {
        let _faults = exclusive();
        assert!(!outage());
        let state = set(SetArgs {
            outage_seconds: Some(30),
        });
        assert!(outage());
        assert!((29_000..=30_000).contains(&state.outage_remaining_ms));
        // No key leaves the armed outage alone.
        assert_ne!(set(SetArgs::default()).outage_remaining_ms, 0);
        assert_eq!(
            set(SetArgs {
                outage_seconds: Some(0)
            })
            .outage_remaining_ms,
            0
        );
        assert!(!outage());
    }
}
