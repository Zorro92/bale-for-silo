//! The handle ABI for the per-mount start budget (`start_budget.rs`). The page updates it on every UI
//! tick, so it takes the clock and a bit set and returns a byte, no JSON. The handle owns a `Mutex`; a
//! panic is caught and the call does nothing (an update reports no expiry).

use std::sync::Mutex;

use crate::abi_meters::{NONE, create, destroy, with};
use crate::start_budget::{Facts, StartBudget};

/// A budget of `budget_ms` (the default, [`crate::start_budget::BUDGET_MS`], unless a debug override
/// asks for less) for a mount that begins at `now`.
#[unsafe(no_mangle)]
pub extern "C" fn bale_core_start_budget_create(
    budget_ms: i64,
    now: i64,
) -> *mut Mutex<StartBudget> {
    create(StartBudget::new(budget_ms, now))
}

/// # Safety
/// A handle from `bale_core_start_budget_create`, released once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_start_budget_destroy(handle: *mut Mutex<StartBudget>) {
    unsafe { destroy(handle) }
}

/// Records the facts (bits as [`Facts::from_bits`]) at `now`: 1 on the update that runs the budget
/// out, else 0.
///
/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_start_budget_update(
    handle: *mut Mutex<StartBudget>,
    now: i64,
    bits: u32,
) -> u8 {
    unsafe {
        with(handle, 0, |budget| {
            u8::from(budget.update(now, Facts::from_bits(bits)))
        })
    }
}

/// What is left of the budget at `now`, or `i64::MIN` for a null handle.
///
/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_start_budget_remaining(
    handle: *mut Mutex<StartBudget>,
    now: i64,
) -> i64 {
    unsafe { with(handle, NONE, |budget| budget.remaining_ms(now)) }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_handle_runs_out_once() {
        let handle = bale_core_start_budget_create(1_000, 0);
        unsafe {
            assert_eq!(bale_core_start_budget_update(handle, 999, 0), 0);
            assert_eq!(bale_core_start_budget_remaining(handle, 999), 1);
            assert_eq!(bale_core_start_budget_update(handle, 1_000, 0), 1);
            assert_eq!(bale_core_start_budget_update(handle, 2_000, 0), 0);
            bale_core_start_budget_destroy(handle);
            assert_eq!(bale_core_start_budget_update(std::ptr::null_mut(), 0, 0), 0);
            assert_eq!(
                bale_core_start_budget_remaining(std::ptr::null_mut(), 0),
                NONE
            );
        }
    }
}
