//! Bale's native GPU/media layer, called from the C# Xbox app and the FFmpegInteropX wrapper over a
//! narrow C ABI: the zero-copy software AV1 repack (`d3d12_repack`, zero-copy ABI), GPU film
//! grain ([`grain`], grain ABI), the debug frame observer (`frames`), and debug loader/engine
//! probes (`bale_gpu_*`, this file and `engine`), the subtitle overlay ([`subs`], subtitle ABI,
//! `bale_sub_*` in this file, with the bitmap ABI `bale_sub_bitmap_*` for image subtitles) and the
//! debug libass subtitle probes (`ass`).

#[cfg(windows)]
mod ass;
#[cfg(windows)]
mod d3d12;
#[cfg(windows)]
pub mod d3d12_repack;
#[cfg(windows)]
mod engine;
pub mod engine_target;
#[cfg(windows)]
mod frames;
pub mod grain;
pub mod png;
pub mod subs;
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
pub const ABI_VERSION: u32 = 6;

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
/// `target` (UTF-16, `target_len` code units) names the output format: `bgra` (also null or empty),
/// `fp16` or `rgb10`; see [`engine_target::Target`]. An unknown name is reported, not played.
///
/// # Safety
/// `url` must point to `url_len` readable `u16`s and `target` to `target_len` (or be null with 0);
/// `report` must point to `report_capacity` writable bytes.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_gpu_engine_probe(
    url: *const u16,
    url_len: u32,
    seconds: u32,
    target: *const u16,
    target_len: u32,
    report: *mut u8,
    report_capacity: u32,
) -> u32 {
    if url.is_null() || report.is_null() || url_len == 0 {
        return 0;
    }
    // SAFETY: the caller guarantees `url_len` readable code units.
    let url = unsafe { std::slice::from_raw_parts(url, url_len as usize) };
    let target = if target.is_null() || target_len == 0 {
        Some(engine_target::Target::Bgra)
    } else {
        // SAFETY: the caller guarantees `target_len` readable code units.
        engine_target::Target::parse_utf16(unsafe {
            std::slice::from_raw_parts(target, target_len as usize)
        })
    };
    let text = match target {
        Some(target) => engine::run(engine::Input::Url(url), seconds.clamp(1, 60), target),
        None => "Error: target must be bgra, fp16 or rgb10\n".to_owned(),
    };
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
    let text = engine::run(
        engine::Input::Source(object),
        seconds.clamp(1, 60),
        engine_target::Target::Bgra,
    );
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

/// Version of the subtitle ABI (the `bale_sub_*` exports); C# refuses a mismatched DLL.
#[unsafe(no_mangle)]
pub extern "C" fn bale_sub_abi_version() -> u32 {
    subs::SUB_ABI_VERSION
}

/// Runs `call` on the overlay behind `overlay`: `E_POINTER` for null, `E_UNEXPECTED` for a panic.
///
/// # Safety
/// `overlay` must be null or a live handle from [`bale_sub_overlay_create`].
#[cfg(windows)]
unsafe fn with_overlay(
    overlay: *mut subs::overlay::Overlay,
    call: impl FnOnce(&subs::overlay::Overlay) -> i32,
) -> i32 {
    // SAFETY: the caller guarantees a live handle when non-null; only shared access is taken.
    let Some(overlay) = (unsafe { overlay.as_ref() }) else {
        return subs::E_POINTER;
    };
    std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| call(overlay)))
        .unwrap_or(subs::E_UNEXPECTED)
}

/// Creates a subtitle overlay: a D3D11 device, a transparent composition swap chain of
/// `width`×`height` physical pixels (each >= 1) and its render thread. `panel` is the `IInspectable*`
/// of a XAML `SwapChainPanel` (borrowed; the overlay takes its own reference and sets the swap chain
/// on it), or null to render offscreen. Returns an HRESULT; on failure `*out` is null.
///
/// # Safety
/// `panel` must be null or a live COM pointer, and then this must run on the UI thread. `out` must
/// be null or writable for one pointer.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_sub_overlay_create(
    panel: *mut core::ffi::c_void,
    width: u32,
    height: u32,
    out: *mut *mut subs::overlay::Overlay,
) -> i32 {
    if out.is_null() {
        return subs::E_POINTER;
    }
    // SAFETY: the caller guarantees writable output storage.
    unsafe { *out = std::ptr::null_mut() };
    // SAFETY: the caller guarantees `panel` is null or live.
    let created = std::panic::catch_unwind(|| unsafe {
        subs::overlay::Overlay::create(panel, width, height)
    });
    match created {
        Ok(Ok(overlay)) => {
            // SAFETY: as above.
            unsafe { *out = Box::into_raw(overlay) };
            subs::S_OK
        }
        Ok(Err(error)) => error.code().0,
        Err(_) => subs::E_UNEXPECTED,
    }
}

/// Sets the swap chain size in physical pixels, the panel's composition scale (the swap chain gets
/// the inverse transform) and the displayed video rectangle inside the panel, in physical pixels.
/// The render thread applies it. `E_INVALIDARG` for a zero or oversized frame, a non-positive scale
/// or non-finite values.
///
/// # Safety
/// `overlay` must be null or a live handle from [`bale_sub_overlay_create`].
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_sub_overlay_set_layout(
    overlay: *mut subs::overlay::Overlay,
    width: u32,
    height: u32,
    scale_x: f32,
    scale_y: f32,
    video_x: f32,
    video_y: f32,
    video_w: f32,
    video_h: f32,
) -> i32 {
    let layout = subs::Layout {
        width,
        height,
        scale_x,
        scale_y,
        video_x,
        video_y,
        video_w,
        video_h,
    };
    // SAFETY: forwarded contract.
    unsafe { with_overlay(overlay, |overlay| overlay.set_layout(layout)) }
}

/// Replaces the current track with UTF-8 ASS/SSA script text (copied). Waits for the render thread
/// to parse it; `E_INVALIDARG` if libass cannot (no track is loaded then).
///
/// # Safety
/// `overlay` as in [`bale_sub_overlay_set_layout`]; `data` must point to `len` readable bytes.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_sub_overlay_load_ass(
    overlay: *mut subs::overlay::Overlay,
    data: *const u8,
    len: usize,
) -> i32 {
    if data.is_null() || len == 0 {
        return if overlay.is_null() {
            subs::E_POINTER
        } else {
            subs::E_INVALIDARG
        };
    }
    // SAFETY: the caller guarantees `len` readable bytes.
    let script = unsafe { std::slice::from_raw_parts(data, len) };
    // SAFETY: forwarded contract.
    unsafe { with_overlay(overlay, |overlay| overlay.load(script)) }
}

/// Replaces the current track with subtitle file text in `format`: 0 = ASS/SSA (the same as
/// [`bale_sub_overlay_load_ass`]), 1 = SubRip, 2 = WebVTT. SubRip and WebVTT are converted to an
/// ASS script first (`subs::convert`; bytes that are not UTF-8 are decoded lossily) and then
/// loaded the same way, so this also waits for the render thread. `E_INVALIDARG` for any other
/// format, for empty data, or if libass cannot parse the script.
///
/// # Safety
/// `overlay` as in [`bale_sub_overlay_set_layout`]; `data` must point to `len` readable bytes.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_sub_overlay_load_text(
    overlay: *mut subs::overlay::Overlay,
    format: u32,
    data: *const u8,
    len: usize,
) -> i32 {
    if data.is_null() || len == 0 {
        return if overlay.is_null() {
            subs::E_POINTER
        } else {
            subs::E_INVALIDARG
        };
    }
    // SAFETY: the caller guarantees `len` readable bytes.
    let text = unsafe { std::slice::from_raw_parts(data, len) };
    // SAFETY: forwarded contract.
    unsafe {
        with_overlay(overlay, |overlay| {
            let options = subs::convert::ConvertOptions::default();
            match subs::convert::to_ass_script(format, text, &options) {
                Some(script) => overlay.load(&script),
                None => subs::E_INVALIDARG,
            }
        })
    }
}

/// Adds an embedded font (MKV attachment / Silo font bundle; bytes copied). `name` is UTF-8 and may
/// be empty. Takes effect for the next load.
///
/// # Safety
/// `overlay` as in [`bale_sub_overlay_set_layout`]; `name` must point to `name_len` readable bytes
/// (or be null with 0) and `data` to `len` readable bytes.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_sub_overlay_add_font(
    overlay: *mut subs::overlay::Overlay,
    name: *const u8,
    name_len: usize,
    data: *const u8,
    len: usize,
) -> i32 {
    if overlay.is_null() {
        return subs::E_POINTER;
    }
    if data.is_null() || len == 0 || (name.is_null() && name_len != 0) {
        return subs::E_INVALIDARG;
    }
    // SAFETY: the caller guarantees the lengths; a null or empty name is an empty slice.
    let (name, data) = unsafe {
        let name: &[u8] = if name_len == 0 {
            &[]
        } else {
            std::slice::from_raw_parts(name, name_len)
        };
        (name, std::slice::from_raw_parts(data, len))
    };
    // SAFETY: forwarded contract.
    unsafe { with_overlay(overlay, |overlay| overlay.add_font(name, data)) }
}

/// Drops the track and presents an empty (transparent) frame.
///
/// # Safety
/// `overlay` must be null or a live handle from [`bale_sub_overlay_create`].
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_sub_overlay_clear(overlay: *mut subs::overlay::Overlay) -> i32 {
    // SAFETY: forwarded contract.
    unsafe { with_overlay(overlay, subs::overlay::Overlay::clear) }
}

/// Media clock: the subtitle/source time in ms at the moment of the call, the playback rate and
/// whether playback is running (non-zero). While playing the render thread extrapolates
/// position + elapsed × rate. `E_INVALIDARG` for a non-finite rate.
///
/// # Safety
/// `overlay` must be null or a live handle from [`bale_sub_overlay_create`].
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_sub_overlay_set_clock(
    overlay: *mut subs::overlay::Overlay,
    position_ms: i64,
    rate: f64,
    playing: u32,
) -> i32 {
    // SAFETY: forwarded contract.
    unsafe {
        with_overlay(overlay, |overlay| {
            overlay.set_clock(position_ms, rate, playing != 0)
        })
    }
}

/// Copies the overlay's counters and timings to `out`.
///
/// # Safety
/// `overlay` as in [`bale_sub_overlay_set_layout`]; `out` must be null or writable for one
/// [`subs::BaleSubStats`].
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_sub_overlay_stats(
    overlay: *mut subs::overlay::Overlay,
    out: *mut subs::BaleSubStats,
) -> i32 {
    if out.is_null() {
        return subs::E_POINTER;
    }
    // SAFETY: forwarded contract; `out` is writable (checked non-null above).
    unsafe {
        with_overlay(overlay, |overlay| {
            out.write_unaligned(overlay.stats());
            subs::S_OK
        })
    }
}

/// Binds the overlay to `token`: image subtitle events pushed with that token
/// ([`bale_sub_bitmap_push`]) are drawn by it, under a loaded text track. 0 unbinds; another token
/// replaces the binding (and drops what was queued); a token bound elsewhere moves here.
/// Destroying the overlay unbinds it.
///
/// # Safety
/// `overlay` must be null or a live handle from [`bale_sub_overlay_create`].
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_sub_overlay_bind_bitmaps(
    overlay: *mut subs::overlay::Overlay,
    token: u64,
) -> i32 {
    // SAFETY: forwarded contract.
    unsafe { with_overlay(overlay, |overlay| overlay.bind_bitmaps(token)) }
}

/// Version of the bitmap subtitle ABI (`bale_sub_bitmap_*`, `bale_sub_overlay_bind_bitmaps` and
/// their structs); the FFmpegInteropX wrapper refuses a mismatch.
#[unsafe(no_mangle)]
pub extern "C" fn bale_sub_bitmap_abi_version() -> u32 {
    subs::bitmap::BITMAP_ABI_VERSION
}

/// Queues one decoded image subtitle event for the overlay bound to `token`. Everything `event`
/// points to is copied before this returns, and nothing is rendered here (the caller is the demux
/// thread): the overlay's render thread is woken to show it. Returns 0, or a negative HRESULT when
/// no overlay is bound to `token` (the event is dropped) or the event is invalid: more than 64
/// rectangles, one over 4096×4096 pixels, more than 256 colours, a `linesize` below the width, a
/// non-zero `reserved`, or a missing pointer.
///
/// # Safety
/// `event` must be null or point to a readable [`subs::bitmap::BaleSubBitmapEvent`] whose `rects`
/// point to `count` rectangles, each with `(h - 1) * linesize + w` readable index bytes and
/// `nb_colors` readable palette entries.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_sub_bitmap_push(
    token: u64,
    event: *const subs::bitmap::BaleSubBitmapEvent,
) -> i32 {
    // SAFETY: forwarded contract.
    std::panic::catch_unwind(|| unsafe { subs::bitmap::push(token, event) })
        .unwrap_or(subs::E_UNEXPECTED)
}

/// Seek/flush: drops every event queued for `token`, so nothing is shown until the next push.
/// Returns 0, or a negative HRESULT when no overlay is bound to `token`.
#[unsafe(no_mangle)]
pub extern "C" fn bale_sub_bitmap_flush(token: u64) -> i32 {
    std::panic::catch_unwind(|| subs::bitmap::flush(token)).unwrap_or(subs::E_UNEXPECTED)
}

/// Stops and joins the render thread, detaches the swap chain from the panel and releases
/// everything. Null is a no-op.
///
/// # Safety
/// `overlay` must be null or a handle from [`bale_sub_overlay_create`] that is not used again; no
/// other call on it may be running. Call on the UI thread.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_sub_overlay_destroy(overlay: *mut subs::overlay::Overlay) {
    if overlay.is_null() {
        return;
    }
    // SAFETY: the caller hands back sole ownership of a handle made by `Box::into_raw`.
    let overlay = unsafe { Box::from_raw(overlay) };
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| drop(overlay)));
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

/// An owned PGS window, decoded off the UI thread and committed only after the caller checks
/// mount/request identity. A separate handle avoids publishing a stale decode after a seek.
pub struct PgsWindow(Vec<subs::bitmap::Event>, i64);

/// Decode a bounded .sup window (16 MiB input, 64 MiB decoded indices). Empty windows are valid.
/// `end_ms` is the source-time coverage end, not the remux player's local clock.
/// # Safety
/// `data` points to `len` readable bytes (may be null for zero length); `out` is writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_sub_pgs_decode(
    data: *const u8,
    len: usize,
    end_ms: i64,
    video_w: u32,
    video_h: u32,
    out: *mut *mut PgsWindow,
) -> i32 {
    if out.is_null() {
        return subs::E_POINTER;
    }
    // SAFETY: the caller provides a writable output slot.
    unsafe {
        *out = std::ptr::null_mut();
    }
    if len > subs::pgs::MAX_INPUT || (len > 0 && data.is_null()) {
        return subs::E_INVALIDARG;
    }
    std::panic::catch_unwind(|| {
        // SAFETY: validated null/length; the caller provides readable data.
        let data = if len == 0 {
            &[]
        } else {
            unsafe { std::slice::from_raw_parts(data, len) }
        };
        let video = (video_w > 0 && video_h > 0).then_some((video_w, video_h));
        match subs::pgs::decode(data, end_ms, video) {
            Ok(events) => {
                // SAFETY: the caller provides a writable output slot.
                unsafe {
                    *out = Box::into_raw(Box::new(PgsWindow(events, end_ms)));
                }
                subs::S_OK
            }
            Err(hr) => hr,
        }
    })
    .unwrap_or(subs::E_UNEXPECTED)
}

/// Move the window into the bitmap sink atomically. Call at most once per decoded handle.
/// # Safety
/// `window` is a live handle from `bale_sub_pgs_decode`, exclusively used for this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_sub_pgs_bind(token: u64, window: *mut PgsWindow, now_ms: i64) -> i32 {
    if window.is_null() {
        return subs::E_POINTER;
    }
    std::panic::catch_unwind(|| {
        // SAFETY: the caller guarantees a live, exclusively borrowed handle.
        let window = unsafe { &mut *window };
        let events = std::mem::take(&mut window.0);
        subs::bitmap::replace(token, events, now_ms, window.1)
    })
    .unwrap_or(subs::E_UNEXPECTED)
}

/// Release a PGS decode handle. Null is a no-op.
/// # Safety
/// `window` is null or an owned handle from `bale_sub_pgs_decode`, never used again.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_sub_pgs_destroy(window: *mut PgsWindow) {
    if !window.is_null() {
        // SAFETY: the caller relinquishes the owned handle.
        unsafe {
            drop(Box::from_raw(window));
        }
    }
}
