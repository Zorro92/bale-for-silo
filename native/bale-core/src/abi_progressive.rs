//! The handle ABI for the Rust progressive stream (progressive_stream.rs), the same shape as
//! abi_stream.rs: status 0 is success, other values are `StreamError` codes, -1 invalid arguments.
//! The handle is one `Arc` reference; `close` may run while a read is in progress, `destroy` runs
//! after every other call has returned, and the WinRT input stream holds its own reference.

use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::Arc;

use crate::progressive_stream::{ProgressiveStats, ProgressiveStream};
use crate::range_stream::StreamError;

const INVALID: i32 = -1;

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

/// Opens the response and starts its pump.
///
/// # Safety
/// `url` and `headers_json` point to readable UTF-8 bytes of the given lengths (headers as
/// `[[name, value], ...]`); `out_handle` is writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_progressive_open(
    url: *const u8,
    url_len: usize,
    headers_json: *const u8,
    headers_len: usize,
    out_handle: *mut *mut ProgressiveStream,
) -> i32 {
    if out_handle.is_null() {
        return INVALID;
    }
    // SAFETY: the caller guarantees both ranges are readable.
    let (Some(url), Some(json)) = (unsafe { bytes(url, url_len) }, unsafe {
        bytes(headers_json, headers_len)
    }) else {
        return INVALID;
    };
    let (Ok(url), Ok(headers)) = (
        std::str::from_utf8(url),
        serde_json::from_slice::<Vec<(String, String)>>(json),
    ) else {
        return INVALID;
    };
    match catch_unwind(AssertUnwindSafe(|| ProgressiveStream::open(url, &headers))) {
        Ok(Ok(stream)) => {
            // SAFETY: checked non-null; the caller guarantees it is writable.
            unsafe { *out_handle = Arc::into_raw(Arc::new(stream)).cast_mut() };
            0
        }
        Ok(Err(error)) => error as i32,
        Err(_) => StreamError::Network as i32,
    }
}

/// Reads the next bytes; `*out_read` is 0 at the end of the data, after a fault, or once closed.
///
/// # Safety
/// A live handle; `buffer` points to `len` writable bytes; `out_read` writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_progressive_read(
    handle: *const ProgressiveStream,
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
    let count = catch_unwind(AssertUnwindSafe(|| stream.read(buffer))).unwrap_or(0);
    // SAFETY: checked non-null.
    unsafe { *out_read = count };
    0
}

/// # Safety
/// A live handle; `out` writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_progressive_stats(
    handle: *const ProgressiveStream,
    out: *mut ProgressiveStats,
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

/// Ends the data and stops the pump; a blocked read returns 0.
///
/// # Safety
/// A live handle (other calls may be in progress).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_progressive_close(handle: *const ProgressiveStream) {
    // SAFETY: the caller guarantees a live handle.
    if let Some(stream) = unsafe { handle.as_ref() } {
        let _ = catch_unwind(AssertUnwindSafe(|| stream.close()));
    }
}

/// # Safety
/// A handle from `bale_core_progressive_open`, released once, after every other call has returned.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_progressive_destroy(handle: *mut ProgressiveStream) {
    if !handle.is_null() {
        // SAFETY: the handle's own reference, released once, after every other call has returned.
        let _ = catch_unwind(AssertUnwindSafe(|| {
            drop(unsafe { Arc::from_raw(handle.cast_const()) })
        }));
    }
}

/// Warms a pooled connection to `url`'s host in the background (`http::warm`), e.g. while a seek's
/// reanchor request runs. Returns at once; -1 for invalid arguments.
///
/// # Safety
/// `url` points to `url_len` readable UTF-8 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_http_warm(url: *const u8, url_len: usize) -> i32 {
    // SAFETY: the caller guarantees a readable range.
    let Some(Ok(url)) = (unsafe { bytes(url, url_len) }).map(std::str::from_utf8) else {
        return INVALID;
    };
    let url = url.to_owned();
    match catch_unwind(AssertUnwindSafe(|| crate::http::warm(url))) {
        Ok(()) => 0,
        Err(_) => INVALID,
    }
}

/// A new WinRT `IInputStream` over the stream, as a raw ABI pointer owning one reference (the
/// caller releases it). Its reads never fail. Null off Windows or for a null handle.
///
/// # Safety
/// A live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_core_progressive_winrt(
    handle: *const ProgressiveStream,
) -> *mut std::ffi::c_void {
    if handle.is_null() {
        return std::ptr::null_mut();
    }
    // SAFETY: the handle holds a reference; take another for the WinRT object.
    let stream = unsafe {
        Arc::increment_strong_count(handle);
        Arc::from_raw(handle)
    };
    catch_unwind(AssertUnwindSafe(|| {
        crate::winrt_stream::create_progressive(stream)
    }))
    .unwrap_or(std::ptr::null_mut())
}
