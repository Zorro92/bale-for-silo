//! Bale's native GPU/media layer, called from the C# Xbox app and the FFmpegInteropX wrapper over a
//! narrow C ABI: the zero-copy software AV1 repack (`d3d12_repack`, zero-copy ABI), GPU film
//! grain ([`grain`], grain ABI), the debug frame observer (`frames`), and debug loader/engine
//! probes (`bale_gpu_*`, this file and `engine`), and the debug libass subtitle probes (`ass`).

#[cfg(windows)]
mod ass;
#[cfg(windows)]
mod d3d12;
#[cfg(windows)]
pub mod d3d12_repack;
#[cfg(windows)]
mod engine;
#[cfg(windows)]
mod frames;
pub mod grain;
pub mod png;
pub mod zc_frame;

use std::sync::{Arc, Mutex};
use std::time::Instant;

#[cfg(windows)]
use windows::Win32::Foundation::HMODULE;
#[cfg(windows)]
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_HARDWARE, D3D_FEATURE_LEVEL};
#[cfg(windows)]
use windows::Win32::Graphics::Direct3D11::{
    D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_CREATE_DEVICE_VIDEO_SUPPORT, D3D11_SDK_VERSION,
    D3D11CreateDevice, ID3D11Device,
};

/// Bumped whenever an export's signature or meaning changes; C# refuses a mismatched DLL.
pub const ABI_VERSION: u32 = 5;

#[unsafe(no_mangle)]
pub extern "C" fn bale_gpu_abi_version() -> u32 {
    ABI_VERSION
}

/// Runs a worker thread that shares a mutex with the caller, and returns the round trip in microseconds
/// (or `u64::MAX` if the thread could not run), proving std threading and synchronisation load.
#[unsafe(no_mangle)]
pub extern "C" fn bale_gpu_std_probe() -> u64 {
    let start = Instant::now();
    let counter = Arc::new(Mutex::new(0u64));
    let worker = {
        let counter = Arc::clone(&counter);
        std::thread::Builder::new()
            .name("bale-gpu-probe".into())
            .spawn(move || {
                for i in 0..1000u64 {
                    if let Ok(mut value) = counter.lock() {
                        *value += i;
                    }
                }
            })
    };
    let joined = worker.map(|handle| handle.join().is_ok()).unwrap_or(false);
    let total = counter.lock().map(|value| *value).unwrap_or(0);
    if !joined || total != 499_500 {
        return u64::MAX;
    }
    start.elapsed().as_micros().min(u64::MAX as u128 - 1) as u64
}

/// Creates a hardware D3D11 device with video support (never WARP) and returns its HRESULT.
/// On success the feature level is written to `feature_level` when it is non-null.
///
/// # Safety
/// `feature_level` must be null or point to writable memory for one `u32`.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_gpu_d3d11_probe(feature_level: *mut u32) -> i32 {
    let mut device: Option<ID3D11Device> = None;
    let mut level = D3D_FEATURE_LEVEL::default();
    // SAFETY: all out-pointers are valid locals; no adapter or software module is passed.
    let result = unsafe {
        D3D11CreateDevice(
            None,
            D3D_DRIVER_TYPE_HARDWARE,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            None,
            D3D11_SDK_VERSION,
            Some(&mut device),
            Some(&mut level),
            None,
        )
    };
    match result {
        Ok(()) => {
            if !feature_level.is_null() {
                // SAFETY: the caller guarantees a non-null pointer is writable.
                unsafe { *feature_level = level.0 as u32 };
            }
            0
        }
        Err(error) => error.code().0,
    }
}

/// Runs the debug IMFMediaEngine frame-server probe on `url` (UTF-16, `url_len` code units) for
/// `seconds` (clamped to 1–60) and writes the plain-text report as UTF-8 into `report`. Returns the
/// bytes written; a report longer than `report_capacity` is cut at a character boundary. The report
/// never contains the URL or system error text. Blocks the calling thread (an MTA thread) for the run.
///
/// # Safety
/// `url` must point to `url_len` readable `u16`s; `report` must point to `report_capacity` writable bytes.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_gpu_engine_probe(
    url: *const u16,
    url_len: u32,
    seconds: u32,
    report: *mut u8,
    report_capacity: u32,
) -> u32 {
    if url.is_null() || report.is_null() || url_len == 0 {
        return 0;
    }
    // SAFETY: the caller guarantees `url_len` readable code units.
    let url = unsafe { std::slice::from_raw_parts(url, url_len as usize) };
    let text = engine::run(engine::Input::Url(url), seconds.clamp(1, 60));
    // SAFETY: the caller guarantees `report_capacity` writable bytes.
    unsafe { write_report(&text, report, report_capacity) }
}

/// Like [`bale_gpu_engine_probe`], but plays `source`, a COM object that should expose `IMFMediaSource`
/// (FFmpegInteropX's MediaStreamSource), through an `IMFMediaEngineExtension`. The pointer is borrowed:
/// the probe takes its own reference and releases it before returning.
///
/// # Safety
/// `source` must be a live COM pointer for the call; `report` must point to `report_capacity` writable bytes.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_gpu_engine_probe_source(
    source: *mut core::ffi::c_void,
    seconds: u32,
    report: *mut u8,
    report_capacity: u32,
) -> u32 {
    if source.is_null() || report.is_null() {
        return 0;
    }
    // SAFETY: the caller guarantees a live COM pointer; clone takes our own reference.
    let Some(object) = (unsafe {
        <windows::core::IUnknown as windows::core::Interface>::from_raw_borrowed(&source)
    })
    .cloned() else {
        return 0;
    };
    let text = engine::run(engine::Input::Source(object), seconds.clamp(1, 60));
    // SAFETY: the caller guarantees `report_capacity` writable bytes.
    unsafe { write_report(&text, report, report_capacity) }
}

/// Runs the libass probe (font discovery through DirectWrite, render timings) and writes the
/// plain-text report as UTF-8 into `report`; returns the bytes written. `font` (`font_len` bytes, may
/// be null when 0) is added as an embedded font, standing in for an MKV attachment.
///
/// # Safety
/// `font` must point to `font_len` readable bytes (or be null with 0); `report` must point to
/// `report_capacity` writable bytes.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_ass_probe(
    font: *const u8,
    font_len: u32,
    report: *mut u8,
    report_capacity: u32,
) -> u32 {
    if report.is_null() {
        return 0;
    }
    // SAFETY: the caller guarantees `font_len` readable bytes when non-null.
    let font = unsafe { borrowed_bytes(font, font_len) };
    let text = std::panic::catch_unwind(|| ass::probe(font))
        .unwrap_or_else(|_| "libass probe: panicked\n".to_owned());
    // SAFETY: the caller guarantees `report_capacity` writable bytes.
    unsafe { write_report(&text, report, report_capacity) }
}

/// Renders the libass probe script at `time_ms` onto a 1920x1080 frame and writes it as a PNG into
/// `out`. Returns the PNG length, or 0 if rendering failed or `out_capacity` is too small.
///
/// # Safety
/// As [`bale_ass_probe`], with `out` pointing to `out_capacity` writable bytes.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_ass_probe_png(
    font: *const u8,
    font_len: u32,
    time_ms: u32,
    out: *mut u8,
    out_capacity: u32,
) -> u32 {
    if out.is_null() {
        return 0;
    }
    // SAFETY: the caller guarantees `font_len` readable bytes when non-null.
    let font = unsafe { borrowed_bytes(font, font_len) };
    let Ok(Some(png)) = std::panic::catch_unwind(|| ass::probe_png(font, time_ms)) else {
        return 0;
    };
    if png.len() > out_capacity as usize {
        return 0;
    }
    // SAFETY: `png.len()` <= `out_capacity`, which the caller guarantees is writable.
    unsafe { std::ptr::copy_nonoverlapping(png.as_ptr(), out, png.len()) };
    png.len() as u32
}

#[cfg(windows)]
unsafe fn borrowed_bytes<'a>(data: *const u8, length: u32) -> &'a [u8] {
    if data.is_null() || length == 0 {
        return &[];
    }
    // SAFETY: the caller guarantees `length` readable bytes.
    unsafe { std::slice::from_raw_parts(data, length as usize) }
}

#[cfg(any(windows, test))]
unsafe fn write_report(text: &str, report: *mut u8, report_capacity: u32) -> u32 {
    let mut length = text.len().min(report_capacity as usize);
    while !text.is_char_boundary(length) {
        length -= 1;
    }
    // SAFETY: `length` <= `report_capacity`, which the caller guarantees is writable.
    unsafe { std::ptr::copy_nonoverlapping(text.as_ptr(), report, length) };
    length as u32
}
