//! The handle ABI for the seek watchdog (`seek_watchdog.rs`). The page updates it on every UI tick, so
//! it takes the clock, a bit set and the player position and returns a byte, no JSON. The handle owns
//! a `Mutex`; a panic is caught and the call does nothing (an update reports no action).

use std::sync::Mutex;

use crate::abi_meters::{NONE, create, destroy, with};
use crate::seek_watchdog::{Facts, SeekWatchdog};

/// One attempt's watchdog.
#[unsafe(no_mangle)]
pub extern "C" fn bale_core_seek_watchdog_create() -> *mut Mutex<SeekWatchdog> {
    create(SeekWatchdog::new())
}

/// # Safety
/// A handle from `bale_core_seek_watchdog_create`, released once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_seek_watchdog_destroy(handle: *mut Mutex<SeekWatchdog>) {
    unsafe { destroy(handle) }
}

/// A local seek from `from_s` to `target_s` (player seconds) was issued at `now`, with `budget_ms` to
/// land (the default, [`crate::seek_watchdog::BUDGET_MS`], unless a debug override asks for another).
///
/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_seek_watchdog_begin(
    handle: *mut Mutex<SeekWatchdog>,
    now: i64,
    budget_ms: i64,
    from_s: f64,
    target_s: f64,
) {
    unsafe {
        with(handle, (), |dog| {
            dog.begin(now, budget_ms, from_s, target_s)
        })
    }
}

/// Records the facts (bits as [`Facts::from_bits`]) and the player clock (NaN when unknown) at
/// `now`: 0 nothing, 1 remount, 2 fail ([`crate::seek_watchdog::Action`]).
///
/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_seek_watchdog_update(
    handle: *mut Mutex<SeekWatchdog>,
    now: i64,
    bits: u32,
    clock_s: f64,
) -> u8 {
    unsafe {
        with(handle, 0, |dog| {
            dog.update(now, Facts::from_bits(bits), clock_s) as u8
        })
    }
}

/// What is left of the watched seek's budget at `now`, or `i64::MIN` when no seek is watched (or for
/// a null handle).
///
/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_seek_watchdog_remaining(
    handle: *mut Mutex<SeekWatchdog>,
    now: i64,
) -> i64 {
    unsafe { with(handle, NONE, |dog| dog.remaining_ms(now).unwrap_or(NONE)) }
}

/// 1 when the attempt's one remount was used, else 0.
///
/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_seek_watchdog_remount_spent(
    handle: *mut Mutex<SeekWatchdog>,
) -> u8 {
    unsafe { with(handle, 0, |dog| u8::from(dog.remount_spent())) }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_handle_remounts_then_fails() {
        let handle = bale_core_seek_watchdog_create();
        unsafe {
            assert_eq!(bale_core_seek_watchdog_remaining(handle, 0), NONE);
            bale_core_seek_watchdog_begin(handle, 0, 1_000, 10.0, 40.0);
            assert_eq!(bale_core_seek_watchdog_update(handle, 999, 0, 10.0), 0);
            assert_eq!(bale_core_seek_watchdog_remaining(handle, 999), 1);
            assert_eq!(bale_core_seek_watchdog_update(handle, 1_000, 0, 10.0), 1);
            assert_eq!(bale_core_seek_watchdog_remount_spent(handle), 1);
            bale_core_seek_watchdog_begin(handle, 5_000, 1_000, 40.0, 70.0);
            assert_eq!(
                bale_core_seek_watchdog_update(handle, 5_500, 0, f64::NAN),
                0
            );
            assert_eq!(
                bale_core_seek_watchdog_update(handle, 6_000, 0, f64::NAN),
                2
            );
            assert_eq!(
                bale_core_seek_watchdog_update(handle, 9_000, 0, f64::NAN),
                0
            );
            bale_core_seek_watchdog_destroy(handle);
            let null = std::ptr::null_mut();
            bale_core_seek_watchdog_begin(null, 0, 1_000, 0.0, 1.0);
            assert_eq!(bale_core_seek_watchdog_update(null, 0, 0, 0.0), 0);
            assert_eq!(bale_core_seek_watchdog_remaining(null, 0), NONE);
            assert_eq!(bale_core_seek_watchdog_remount_spent(null), 0);
        }
    }
}
