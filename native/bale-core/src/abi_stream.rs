//! The handle ABI for the Rust range stream (range_stream.rs). Reads take a raw buffer and return a
//! status code, never JSON. Status 0 is success; other values are `StreamError` codes, and -1 means
//! invalid arguments.
//!
//! Lifetime: the handle is one shared reference to the stream (`Arc`). `close` wakes any read blocked
//! on a fetch and may run while reads are in progress; `destroy` drops the handle's reference after
//! every other call has returned (the C# SafeHandle guarantees that). The WinRT stream from
//! `bale_core_range_winrt` holds its own reference, so it outlives the handle safely.

use std::panic::{AssertUnwindSafe, catch_unwind};

use std::sync::Arc;

use crate::range_stream::{RangeStats, RangeStream, StreamError};

const INVALID: i32 = -1;

fn code(error: StreamError) -> i32 {
    error as i32
}

/// # Safety
/// `pointer` must point to `len` readable bytes unless `len` is 0.
unsafe fn bytes<'a>(pointer: *const u8, len: usize) -> Option<&'a [u8]> {
    if len == 0 {
        Some(&[])
    } else if pointer.is_null() {
        None
    } else {
        // SAFETY: non-null, and the caller guarantees `len` readable bytes.
        Some(unsafe { std::slice::from_raw_parts(pointer, len) })
    }
}

fn headers(json: &[u8]) -> Option<Vec<(String, String)>> {
    serde_json::from_slice(json).ok()
}

/// Opens a stream: an open-ended range request, then three fetch workers when ranges are supported.
///
/// # Safety
/// `url` and `headers_json` point to readable UTF-8 bytes of the given lengths (headers as
/// `[[name, value], ...]`); every out-pointer is writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_range_open(
    url: *const u8,
    url_len: usize,
    headers_json: *const u8,
    headers_len: usize,
    prefetch_blocks: u32,
    out_handle: *mut *mut RangeStream,
    out_length: *mut u64,
    out_supports_ranges: *mut u8,
) -> i32 {
    if out_handle.is_null() || out_length.is_null() || out_supports_ranges.is_null() {
        return INVALID;
    }
    // SAFETY: the caller guarantees both ranges are readable.
    let (Some(url), Some(json)) = (unsafe { bytes(url, url_len) }, unsafe {
        bytes(headers_json, headers_len)
    }) else {
        return INVALID;
    };
    let (Ok(url), Some(headers)) = (std::str::from_utf8(url), headers(json)) else {
        return INVALID;
    };
    let url = url.to_owned();
    match catch_unwind(AssertUnwindSafe(|| {
        RangeStream::open(url, headers, prefetch_blocks)
    })) {
        Ok(Ok(stream)) => {
            // SAFETY: checked non-null; the caller guarantees they are writable.
            unsafe {
                *out_length = stream.length();
                *out_supports_ranges = u8::from(stream.supports_ranges());
                *out_handle = Arc::into_raw(Arc::new(stream)).cast_mut();
            }
            0
        }
        Ok(Err(error)) => code(error),
        Err(_) => code(StreamError::Network),
    }
}

/// Reads at `position` into `buffer`; `*out_read` is 0 at the end of the stream.
///
/// # Safety
/// A live handle; `buffer` points to `len` writable bytes; `out_read` writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_range_read(
    handle: *const RangeStream,
    position: u64,
    buffer: *mut u8,
    len: usize,
    out_read: *mut usize,
) -> i32 {
    // SAFETY: the caller guarantees a live handle.
    let Some(stream) = (unsafe { handle.as_ref() }) else {
        return INVALID;
    };
    if out_read.is_null() || (buffer.is_null() && len != 0) {
        return INVALID;
    }
    let buffer: &mut [u8] = if len == 0 {
        &mut []
    } else {
        // SAFETY: non-null, and the caller guarantees `len` writable bytes.
        unsafe { std::slice::from_raw_parts_mut(buffer, len) }
    };
    match catch_unwind(AssertUnwindSafe(|| stream.read(position, buffer))) {
        Ok(Ok(count)) => {
            // SAFETY: checked non-null.
            unsafe { *out_read = count };
            0
        }
        Ok(Err(error)) => code(error),
        Err(_) => code(StreamError::Network),
    }
}

/// # Safety
/// A live handle; `out` writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_range_stats(
    handle: *const RangeStream,
    out: *mut RangeStats,
) -> i32 {
    // SAFETY: the caller guarantees a live handle and a writable `out`.
    match (unsafe { handle.as_ref() }, out.is_null()) {
        (Some(stream), false) => {
            unsafe { out.write(stream.stats()) };
            0
        }
        _ => INVALID,
    }
}

/// Replaces the request headers (`[[name, value], ...]`), e.g. after a token refresh.
///
/// # Safety
/// A live handle; `headers_json` points to `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_range_set_headers(
    handle: *const RangeStream,
    headers_json: *const u8,
    len: usize,
) -> i32 {
    // SAFETY: the caller guarantees a live handle and readable JSON.
    let (Some(stream), Some(json)) = (unsafe { handle.as_ref() }, unsafe {
        bytes(headers_json, len)
    }) else {
        return INVALID;
    };
    match headers(json) {
        Some(headers) => {
            stream.set_headers(headers);
            0
        }
        None => INVALID,
    }
}

/// The owner's refresh callback: `(context, generation)` of the headers a 401 rejected. It runs on a
/// stream thread, must return quickly, and answers later with `bale_core_range_set_headers`.
pub type RefreshCallback = unsafe extern "C" fn(context: *mut std::ffi::c_void, generation: u64);

pub(crate) struct RefreshContext(*mut std::ffi::c_void);

// SAFETY: the owner guarantees the context is usable from any thread until `close` returns.
unsafe impl Send for RefreshContext {}
// SAFETY: as above; the stream only passes the pointer back.
unsafe impl Sync for RefreshContext {}

impl RefreshContext {
    pub(crate) fn new(context: *mut std::ffi::c_void) -> Self {
        RefreshContext(context)
    }

    pub(crate) fn get(&self) -> *mut std::ffi::c_void {
        self.0
    }
}

/// Installs the callback a 401 calls for fresh headers. After `bale_core_range_close` returns, the
/// callback is never called again, so the owner may then free `context`. Returns 0, or
/// `Disposed` when the stream is already closed (the callback is not kept).
///
/// # Safety
/// A live handle; `context` stays valid for `callback` until `close` returns.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_range_set_refresh(
    handle: *const RangeStream,
    callback: Option<RefreshCallback>,
    context: *mut std::ffi::c_void,
) -> i32 {
    // SAFETY: the caller guarantees a live handle.
    let (Some(stream), Some(callback)) = (unsafe { handle.as_ref() }, callback) else {
        return INVALID;
    };
    let context = RefreshContext(context);
    let installed = stream.set_refresher(Box::new(move |generation| {
        // SAFETY: the owner keeps `context` valid until `close`, which clears this callback first.
        unsafe { callback(context.get(), generation) }
    }));
    if installed {
        0
    } else {
        code(StreamError::Disposed)
    }
}

/// Stops the stream and wakes blocked reads; they return `Disposed`.
///
/// # Safety
/// A live handle (other calls may be in progress).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_range_close(handle: *const RangeStream) {
    // SAFETY: the caller guarantees a live handle.
    if let Some(stream) = unsafe { handle.as_ref() } {
        let _ = catch_unwind(AssertUnwindSafe(|| stream.close()));
    }
}

/// # Safety
/// A handle from `bale_core_range_open`, released once, after every other call has returned.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_range_destroy(handle: *mut RangeStream) {
    if !handle.is_null() {
        // SAFETY: the handle's own reference, released once, after every other call has returned.
        let _ = catch_unwind(AssertUnwindSafe(|| {
            drop(unsafe { Arc::from_raw(handle.cast_const()) })
        }));
    }
}

/// A new WinRT `IRandomAccessStream` over the stream, as a raw ABI pointer owning one reference
/// (the caller releases it). It holds its own reference to the range stream and reads on the
/// Windows thread pool. Null off Windows or for a null handle.
///
/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_range_winrt(
    handle: *const RangeStream,
) -> *mut std::ffi::c_void {
    if handle.is_null() {
        return std::ptr::null_mut();
    }
    // SAFETY: the handle holds a reference; take another for the WinRT object.
    let stream = unsafe {
        Arc::increment_strong_count(handle);
        Arc::from_raw(handle)
    };
    catch_unwind(AssertUnwindSafe(|| crate::winrt_stream::create(stream)))
        .unwrap_or(std::ptr::null_mut())
}
