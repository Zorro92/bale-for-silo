//! The handle ABI for the Rust range stream (range_stream.rs). Reads take a raw buffer and return a
//! status code, never JSON. Status 0 is success; other values are `StreamError` codes, and -1 means
//! invalid arguments.
//!
//! Lifetime: `close` wakes any read blocked on a fetch and may run while reads are in progress;
//! `destroy` frees the handle and must come after every other call has returned (the C# SafeHandle
//! guarantees that).

use std::panic::{AssertUnwindSafe, catch_unwind};

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
                *out_handle = Box::into_raw(Box::new(stream));
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
        // SAFETY: the caller guarantees ownership and that no other call is in progress.
        let _ = catch_unwind(AssertUnwindSafe(|| drop(unsafe { Box::from_raw(handle) })));
    }
}
