//! The handle ABI for state that lives in Rust between calls: the per-frame delivery pipeline and
//! the network meters. These are called per frame or per network read, so they take plain
//! `#[repr(C)]` structs and numbers, never JSON. Each handle owns a `Mutex`, so callers on several
//! threads are safe; a panic is caught and the call does nothing (returns its "none" value).
//!
//! "None" for an `i64` result is `i64::MIN`. Text comes back in a buffer released with
//! `bale_core_free`.

use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::{Mutex, MutexGuard, PoisonError};

use crate::frames::{
    DeliveryMeter, DeliverySnapshot, Environment, FrameProcessor, FrameRecord, LateFrameTrace,
};
use crate::meters::{SpeedMeter, SpeedResult, ThroughputMeter};

pub const NONE: i64 = i64::MIN;

pub(crate) fn create<T>(value: T) -> *mut Mutex<T> {
    Box::into_raw(Box::new(Mutex::new(value)))
}

/// # Safety
///
/// `handle` is null or came from the matching `create` and was not destroyed.
pub(crate) unsafe fn destroy<T>(handle: *mut Mutex<T>) {
    if !handle.is_null() {
        // SAFETY: the caller guarantees a live handle from `create`, released once.
        drop(unsafe { Box::from_raw(handle) });
    }
}

/// Runs `f` on the locked value, or returns `fallback` for a null handle or a panic.
///
/// # Safety
///
/// `handle` is null or a live handle from the matching `create`.
pub(crate) unsafe fn with<T, R>(
    handle: *mut Mutex<T>,
    fallback: R,
    f: impl FnOnce(&mut T) -> R,
) -> R {
    // SAFETY: the caller guarantees a live handle; the Mutex serializes access.
    let Some(mutex) = (unsafe { handle.as_ref() }) else {
        return fallback;
    };
    catch_unwind(AssertUnwindSafe(|| {
        let mut guard: MutexGuard<'_, T> = mutex.lock().unwrap_or_else(PoisonError::into_inner);
        f(&mut guard)
    }))
    .unwrap_or(fallback)
}

/// Hands `text` to the caller as a buffer for `bale_core_free`.
///
/// # Safety
///
/// `output` and `output_len` are valid for writes.
unsafe fn give(text: String, output: *mut *mut u8, output_len: *mut usize) {
    if output.is_null() || output_len.is_null() {
        return;
    }
    let bytes = text.into_bytes().into_boxed_slice();
    let len = bytes.len();
    // SAFETY: checked non-null; the caller guarantees they are writable.
    unsafe {
        *output_len = len;
        *output = Box::into_raw(bytes).cast();
    }
}

fn write<T>(out: *mut T, value: Option<T>) -> u8 {
    match (value, out.is_null()) {
        (Some(value), false) => {
            // SAFETY: non-null, and the caller guarantees it is writable.
            unsafe { out.write(value) };
            1
        }
        _ => 0,
    }
}

// ---- Frame record processor ----

#[unsafe(no_mangle)]
pub extern "C" fn bale_core_frames_create(
    capacity: u32,
    collect_ahead: u8,
) -> *mut Mutex<FrameProcessor> {
    create(FrameProcessor::new(capacity as usize, collect_ahead != 0))
}

/// # Safety
/// A handle from `bale_core_frames_create`, released once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_frames_destroy(handle: *mut Mutex<FrameProcessor>) {
    unsafe { destroy(handle) }
}

/// # Safety
/// A live handle; `snapshot` points to one readable `Environment`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_frames_add_snapshot(
    handle: *mut Mutex<FrameProcessor>,
    snapshot: *const Environment,
) {
    let Some(&snapshot) = (unsafe { snapshot.as_ref() }) else {
        return;
    };
    unsafe { with(handle, (), |p| p.add_snapshot(snapshot)) }
}

/// # Safety
/// A live handle; `out` writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_frames_snapshot_for(
    handle: *mut Mutex<FrameProcessor>,
    now_ms: f64,
    out: *mut Environment,
) {
    let snapshot = unsafe { with(handle, None, |p| Some(p.snapshot_for(now_ms))) };
    write(out, snapshot);
}

/// Processes `count` records in order.
///
/// # Safety
/// A live handle; `records` points to `count` readable records (or is dangling with 0).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_frames_process(
    handle: *mut Mutex<FrameProcessor>,
    records: *const FrameRecord,
    count: usize,
) {
    if count == 0 || records.is_null() {
        return;
    }
    // SAFETY: the caller guarantees `count` readable records.
    let records = unsafe { std::slice::from_raw_parts(records, count) };
    unsafe { with(handle, (), |p| records.iter().for_each(|r| p.process(r))) }
}

/// Writes the delivery snapshot and returns 1, or 0 before any frame was observed.
///
/// # Safety
/// A live handle; `out` writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_frames_read(
    handle: *mut Mutex<FrameProcessor>,
    position: i64,
    now_ms: i64,
    out: *mut DeliverySnapshot,
) -> u8 {
    write(out, unsafe {
        with(handle, None, |p| p.read(position, now_ms))
    })
}

/// # Safety
/// A live handle; `output`/`output_len` writable; release the buffer with `bale_core_free`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_frames_describe(
    handle: *mut Mutex<FrameProcessor>,
    output: *mut *mut u8,
    output_len: *mut usize,
) {
    let text = unsafe { with(handle, String::new(), |p| p.describe_trace()) };
    unsafe { give(text, output, output_len) }
}

/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_frames_reset_trace(handle: *mut Mutex<FrameProcessor>) {
    unsafe { with(handle, (), FrameProcessor::reset_trace) }
}

// ---- Delivery meter (debug frame delivery and tests) ----

#[unsafe(no_mangle)]
pub extern "C" fn bale_core_delivery_create(collect_ahead: u8) -> *mut Mutex<DeliveryMeter> {
    create(DeliveryMeter::new(collect_ahead != 0))
}

/// # Safety
/// A handle from `bale_core_delivery_create`, released once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_delivery_destroy(handle: *mut Mutex<DeliveryMeter>) {
    unsafe { destroy(handle) }
}

/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_delivery_set_seeking(
    handle: *mut Mutex<DeliveryMeter>,
    seeking: u8,
) {
    unsafe { with(handle, (), |m| m.set_seeking(seeking != 0)) }
}

/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_delivery_invalidate(handle: *mut Mutex<DeliveryMeter>) {
    unsafe { with(handle, (), DeliveryMeter::invalidate_timeline) }
}

/// Returns 1 when the sample counted as late.
///
/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_delivery_observe(
    handle: *mut Mutex<DeliveryMeter>,
    timestamp: i64,
    duration: i64,
    position: i64,
    playing: u8,
    discontinuous: u8,
    now_ms: i64,
) -> u8 {
    unsafe {
        with(handle, 0, |m| {
            u8::from(m.observe(
                timestamp,
                duration,
                position,
                playing != 0,
                discontinuous != 0,
                now_ms,
            ))
        })
    }
}

/// # Safety
/// A live handle; `out` writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_delivery_read(
    handle: *mut Mutex<DeliveryMeter>,
    position: i64,
    now_ms: i64,
    out: *mut DeliverySnapshot,
) -> u8 {
    write(out, unsafe {
        with(handle, None, |m| m.read(position, now_ms))
    })
}

// ---- Late frame trace (debug frame delivery and tests) ----

#[unsafe(no_mangle)]
pub extern "C" fn bale_core_trace_create() -> *mut Mutex<LateFrameTrace> {
    create(LateFrameTrace::default())
}

/// # Safety
/// A handle from `bale_core_trace_create`, released once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_trace_destroy(handle: *mut Mutex<LateFrameTrace>) {
    unsafe { destroy(handle) }
}

/// # Safety
/// A live handle; `environment` points to one readable `Environment`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_trace_sample(
    handle: *mut Mutex<LateFrameTrace>,
    now_ms: f64,
    timestamp: i64,
    duration: i64,
    position: i64,
    late: u8,
    environment: *const Environment,
) {
    let Some(&environment) = (unsafe { environment.as_ref() }) else {
        return;
    };
    unsafe {
        with(handle, (), |t| {
            t.sample(
                now_ms,
                timestamp,
                duration,
                position,
                late != 0,
                environment,
            )
        })
    }
}

/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_trace_reset(handle: *mut Mutex<LateFrameTrace>) {
    unsafe { with(handle, (), LateFrameTrace::reset) }
}

/// # Safety
/// A live handle; `output`/`output_len` writable; release the buffer with `bale_core_free`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_trace_describe(
    handle: *mut Mutex<LateFrameTrace>,
    output: *mut *mut u8,
    output_len: *mut usize,
) {
    let text = unsafe { with(handle, String::new(), |t| t.describe()) };
    unsafe { give(text, output, output_len) }
}

// ---- Throughput meter ----

#[unsafe(no_mangle)]
pub extern "C" fn bale_core_throughput_create() -> *mut Mutex<ThroughputMeter> {
    create(ThroughputMeter::default())
}

/// # Safety
/// A handle from `bale_core_throughput_create`, released once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_throughput_destroy(handle: *mut Mutex<ThroughputMeter>) {
    unsafe { destroy(handle) }
}

/// Bits per second, or `NONE`.
///
/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_throughput_sample(
    handle: *mut Mutex<ThroughputMeter>,
    bytes: i64,
    ticks: i64,
) -> i64 {
    unsafe { with(handle, NONE, |m| m.sample(bytes, ticks).unwrap_or(NONE)) }
}

// ---- Connection speed meter ----

/// `target_kbps` is ignored unless `has_target`.
#[unsafe(no_mangle)]
pub extern "C" fn bale_core_speed_create(
    quick: u8,
    target_kbps: i32,
    has_target: u8,
) -> *mut Mutex<SpeedMeter> {
    create(SpeedMeter::new(
        quick != 0,
        (has_target != 0).then_some(target_kbps),
    ))
}

/// # Safety
/// A handle from `bale_core_speed_create`, released once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_speed_destroy(handle: *mut Mutex<SpeedMeter>) {
    unsafe { destroy(handle) }
}

/// # Safety
/// A live handle; both values non-negative (the C# wrapper checks).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_speed_add(
    handle: *mut Mutex<SpeedMeter>,
    elapsed_ms: i64,
    bytes: i64,
) {
    unsafe { with(handle, (), |m| m.add(elapsed_ms, bytes)) }
}

/// The stop time, or `NONE`.
///
/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_speed_stop_at(
    handle: *mut Mutex<SpeedMeter>,
    now_ms: i64,
) -> i64 {
    unsafe { with(handle, NONE, |m| m.stop_at(now_ms).unwrap_or(NONE)) }
}

/// Writes the result and returns 1, or 0 when the test measured nothing. `ended_ms` is ignored
/// unless `has_ended`.
///
/// # Safety
/// A live handle; `out` writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_speed_result(
    handle: *mut Mutex<SpeedMeter>,
    stop_ms: i64,
    ended_ms: i64,
    has_ended: u8,
    out: *mut SpeedResult,
) -> u8 {
    let ended = (has_ended != 0).then_some(ended_ms);
    write(out, unsafe {
        with(handle, None, |m| m.result(stop_ms, ended))
    })
}

/// The per-window kbps as a JSON array, in a buffer for `bale_core_free`.
///
/// # Safety
/// A live handle; `output`/`output_len` writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_speed_window_kbps(
    handle: *mut Mutex<SpeedMeter>,
    output: *mut *mut u8,
    output_len: *mut usize,
) {
    let windows = unsafe { with(handle, Vec::new(), |m| m.window_kbps()) };
    let text = serde_json::to_string(&windows).unwrap_or_else(|_| "[]".to_owned());
    unsafe { give(text, output, output_len) }
}
