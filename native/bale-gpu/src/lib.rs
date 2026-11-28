//! Bale's native GPU/media layer, called from the C# Xbox app and the FFmpegInteropX wrapper over a
//! narrow C ABI: GPU film grain ([`grain`], grain ABI), the debug frame observer (`frames`), and
//! debug loader/engine probes (`bale_gpu_*`, this file and `engine`), the subtitle overlay
//! ([`subs`], subtitle ABI, `bale_sub_*` in this file, with the bitmap ABI `bale_sub_bitmap_*` for
//! image subtitles), the debug libass subtitle probes (`ass`), in-band closed captions
//! ([`subs::cc`], caption ABI, `bale_cc_*` in this file), the debug audio passthrough probe
//! ([`audio_probe`], `bale_gpu_audio_probe`) and the exclusive-mode audio output ([`audio_out`],
//! audio output ABI, `bale_audio_out_*`).
//!
//! The debug probes and readouts exist only with the `debug-probes` feature (Cargo.toml), which CI enables for
//! debug-server packages alone; production DLLs export none of them (ci.yml's export lists).

#[cfg(windows)]
mod ass;
pub mod audio_out;
pub mod audio_probe;
#[cfg(all(windows, feature = "debug-probes"))]
mod d3d12;
#[cfg(windows)]
mod engine;
pub mod engine_events;
#[cfg(windows)]
mod engine_session;
pub mod engine_target;
#[cfg(windows)]
mod frames;
pub mod grain;
#[cfg(windows)]
pub mod lifecycle;
pub mod memledger;
#[cfg(feature = "debug-probes")]
pub mod memprobe;
#[cfg(windows)]
mod panel;
#[cfg(feature = "debug-probes")]
pub mod png;
pub mod subs;
pub mod tonemap;
pub use tonemap::ffi::bale_tonemap_abi_version;
#[cfg(windows)]
pub use tonemap::ffi::{bale_tonemap_apply, bale_tonemap_create, bale_tonemap_destroy};

#[cfg(feature = "debug-probes")]
use std::sync::{Arc, Mutex};
#[cfg(feature = "debug-probes")]
use std::time::Instant;

#[cfg(all(windows, feature = "debug-probes"))]
use windows::Win32::Foundation::HMODULE;
#[cfg(all(windows, feature = "debug-probes"))]
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_HARDWARE, D3D_FEATURE_LEVEL};
#[cfg(all(windows, feature = "debug-probes"))]
use windows::Win32::Graphics::Direct3D11::{
    D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_CREATE_DEVICE_VIDEO_SUPPORT, D3D11_SDK_VERSION,
    D3D11CreateDevice, ID3D11Device,
};

/// Bumped whenever an export's signature or meaning changes; C# refuses a mismatched DLL. 16: engine
/// playback events carry their QPC time and (debug frame log) engine position; a frame observer can be
/// created without a MediaPlaybackSession and take an engine session's clock
/// (`bale_frame_observer_attach_engine`); `bale_gpu_engine_session_frame_log`.
pub const ABI_VERSION: u32 = 16;

#[unsafe(no_mangle)]
pub extern "C" fn bale_gpu_abi_version() -> u32 {
    ABI_VERSION
}

/// Runs a worker thread that shares a mutex with the caller, and returns the round trip in microseconds
/// (or `u64::MAX` if the thread could not run), proving std threading and synchronisation load. Debug probe.
#[cfg(feature = "debug-probes")]
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
#[cfg(all(windows, feature = "debug-probes"))]
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
            // Probe owns no work after returning; release the driver's temporary allocations too.
            if let Some(device) = device.as_ref() {
                drop(lifecycle::Device(device.clone()));
            }
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
#[cfg(all(windows, feature = "debug-probes"))]
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
        Some(target) => engine::run(
            engine::Input::Url(url),
            seconds.clamp(1, 60),
            target,
            None,
            0.0,
        ),
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
#[cfg(all(windows, feature = "debug-probes"))]
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
        None,
        0.0,
    );
    // SAFETY: the caller guarantees `report_capacity` writable bytes.
    unsafe { write_report(&text, report, report_capacity) }
}

/// Rendering-mode probe (frame-server mode when `attach` is null: every decoded frame is counted and
/// nothing is presented): plays `url` (or, when `source` is non-null, that COM object: a media source
/// through the engine extension when `mime_len` is 0, else an `IRandomAccessStream` of MIME type `mime`
/// through `SetSourceFromByteStream`) with `EnableWindowlessSwapchainMode`, calls `attach(context, handle)` once
/// with the engine's swap chain handle (the caller sets it on a `SwapChainPanel` on the UI thread and
/// returns an HRESULT), sizes the video to `width`x`height` (then to whatever `size`, if set, returns
/// while playing), plays `seconds`, and writes the report.
///
/// # Safety
/// `url` must point to `url_len` readable code units (or be null when `source` is set); `source`, when
/// non-null, a live COM pointer for the call; `mime` `mime_len` code units or null; `target` `target_len` code units or null; `attach` a
/// valid function for the call; `report` `report_capacity` writable bytes.
#[unsafe(no_mangle)]
#[cfg(windows)]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn bale_gpu_engine_present(
    url: *const u16,
    url_len: u32,
    source: *mut core::ffi::c_void,
    mime: *const u16,
    mime_len: u32,
    seconds: u32,
    frame_rate: f64,
    target: *const u16,
    target_len: u32,
    width: u32,
    height: u32,
    attach: Option<unsafe extern "C" fn(*mut core::ffi::c_void, isize) -> i32>,
    size: Option<unsafe extern "C" fn(*mut core::ffi::c_void) -> u64>,
    context: *mut core::ffi::c_void,
    report: *mut u8,
    report_capacity: u32,
) -> u32 {
    // No `attach`: frame-server mode (every decoded frame counted), nothing presented.
    if report.is_null() || (attach.is_some() && (width == 0 || height == 0)) {
        return 0;
    }
    let target = if target.is_null() || target_len == 0 {
        Some(engine_target::Target::Bgra)
    } else {
        // SAFETY: the caller guarantees `target_len` readable code units.
        engine_target::Target::parse_utf16(unsafe {
            std::slice::from_raw_parts(target, target_len as usize)
        })
    };
    let Some(target) = target else {
        // SAFETY: the caller guarantees `report_capacity` writable bytes.
        return unsafe {
            write_report(
                "Error: target must be bgra, fp16 or rgb10\n",
                report,
                report_capacity,
            )
        };
    };
    let present = attach.map(|attach| engine::Present {
        attach,
        context,
        width,
        height,
        size,
    });
    let seconds = seconds.clamp(1, 120);
    let text = if source.is_null() {
        if url.is_null() || url_len == 0 {
            return 0;
        }
        // SAFETY: the caller guarantees `url_len` readable code units.
        let url = unsafe { std::slice::from_raw_parts(url, url_len as usize) };
        engine::run(
            engine::Input::Url(url),
            seconds,
            target,
            present.as_ref(),
            frame_rate,
        )
    } else {
        // SAFETY: the caller guarantees a live COM pointer; clone takes our own reference.
        let Some(object) = (unsafe {
            <windows::core::IUnknown as windows::core::Interface>::from_raw_borrowed(&source)
        })
        .cloned() else {
            return 0;
        };
        let input = if mime.is_null() || mime_len == 0 {
            engine::Input::Source(object)
        } else {
            engine::Input::Stream {
                stream: object,
                // SAFETY: the caller guarantees `mime_len` readable code units.
                mime: unsafe { std::slice::from_raw_parts(mime, mime_len as usize) },
            }
        };
        engine::run(input, seconds, target, present.as_ref(), frame_rate)
    };
    // SAFETY: the caller guarantees `report_capacity` writable bytes.
    unsafe { write_report(&text, report, report_capacity) }
}

/// The swap-chain format later [`bale_gpu_engine_session_open`] calls request, as
/// `MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT` (0 bgra, the default; 1 fp16; 2 rgb10). Bale passes the format
/// per open with [`bale_gpu_engine_session_open_target`] instead (ABI 14); this setter stays for older
/// callers and debug tools. Returns 1 when accepted, 0 for an unknown value.
#[cfg(windows)]
static SESSION_TARGET: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);

#[unsafe(no_mangle)]
#[cfg(windows)]
pub extern "C" fn bale_gpu_engine_session_set_target(target: u32) -> u32 {
    if target > 2 {
        return 0;
    }
    SESSION_TARGET.store(target, std::sync::atomic::Ordering::Relaxed);
    1
}

#[cfg(windows)]
fn session_target() -> engine_target::Target {
    target_from_code(SESSION_TARGET.load(std::sync::atomic::Ordering::Relaxed))
        .unwrap_or(engine_target::Target::Bgra)
}

/// The C ABI's target codes: 0 bgra, 1 fp16, 2 rgb10.
#[cfg(windows)]
fn target_from_code(code: u32) -> Option<engine_target::Target> {
    match code {
        0 => Some(engine_target::Target::Bgra),
        1 => Some(engine_target::Target::Fp16),
        2 => Some(engine_target::Target::Rgb10),
        _ => None,
    }
}

/// Opens an engine **session** (Bale's engine player) in rendering mode (see `engine_session`): `url`,
/// or `source` (a COM media source when `mime_len` is 0, else an `IRandomAccessStream` of type `mime`),
/// attached through `attach(context, handle)` like [`bale_gpu_engine_present`], seeked to `start` and
/// playing unless `paused`, in the format of [`bale_gpu_engine_session_set_target`]. On success
/// `*out_session` is the session (close it with [`bale_gpu_engine_session_close`]); the open report is
/// written either way.
///
/// # Safety
/// As [`bale_gpu_engine_present`]; `out_session` must be writable. `context` need only be valid for this call.
#[unsafe(no_mangle)]
#[cfg(windows)]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn bale_gpu_engine_session_open(
    url: *const u16,
    url_len: u32,
    source: *mut core::ffi::c_void,
    mime: *const u16,
    mime_len: u32,
    width: u32,
    height: u32,
    attach: Option<unsafe extern "C" fn(*mut core::ffi::c_void, isize) -> i32>,
    context: *mut core::ffi::c_void,
    start: f64,
    paused: u32,
    muted: u32,
    out_session: *mut *mut core::ffi::c_void,
    report: *mut u8,
    report_capacity: u32,
) -> u32 {
    // SAFETY: the caller's guarantees, unchanged.
    unsafe {
        session_open(
            url,
            url_len,
            source,
            mime,
            mime_len,
            width,
            height,
            attach,
            context,
            start,
            paused,
            muted,
            session_target(),
            out_session,
            report,
            report_capacity,
        )
    }
}

/// As [`bale_gpu_engine_session_open`], with the swap-chain format for this open: `target` 0 bgra
/// (B8G8R8A8), 1 fp16, 2 rgb10 (R10G10B10A2) as `MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT`. Bale picks it per
/// mount from the frames it hands the engine (owner's bit-depth rule, AGENTS.md). 0 for an unknown
/// `target`, with `*out_session` null and no report.
///
/// # Safety
/// As [`bale_gpu_engine_session_open`].
#[unsafe(no_mangle)]
#[cfg(windows)]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn bale_gpu_engine_session_open_target(
    url: *const u16,
    url_len: u32,
    source: *mut core::ffi::c_void,
    mime: *const u16,
    mime_len: u32,
    width: u32,
    height: u32,
    attach: Option<unsafe extern "C" fn(*mut core::ffi::c_void, isize) -> i32>,
    context: *mut core::ffi::c_void,
    start: f64,
    paused: u32,
    muted: u32,
    target: u32,
    out_session: *mut *mut core::ffi::c_void,
    report: *mut u8,
    report_capacity: u32,
) -> u32 {
    let Some(target) = target_from_code(target) else {
        if !out_session.is_null() {
            // SAFETY: the caller guarantees a writable out pointer.
            unsafe { *out_session = core::ptr::null_mut() };
        }
        return 0;
    };
    // SAFETY: the caller's guarantees, unchanged.
    unsafe {
        session_open(
            url,
            url_len,
            source,
            mime,
            mime_len,
            width,
            height,
            attach,
            context,
            start,
            paused,
            muted,
            target,
            out_session,
            report,
            report_capacity,
        )
    }
}

/// The body of both session opens.
///
/// # Safety
/// As [`bale_gpu_engine_session_open`].
#[cfg(windows)]
#[allow(clippy::too_many_arguments)]
unsafe fn session_open(
    url: *const u16,
    url_len: u32,
    source: *mut core::ffi::c_void,
    mime: *const u16,
    mime_len: u32,
    width: u32,
    height: u32,
    attach: Option<unsafe extern "C" fn(*mut core::ffi::c_void, isize) -> i32>,
    context: *mut core::ffi::c_void,
    start: f64,
    paused: u32,
    muted: u32,
    target: engine_target::Target,
    out_session: *mut *mut core::ffi::c_void,
    report: *mut u8,
    report_capacity: u32,
) -> u32 {
    let Some(attach) = attach else { return 0 };
    if out_session.is_null() || report.is_null() || width == 0 || height == 0 {
        return 0;
    }
    // SAFETY: the caller guarantees a writable out pointer.
    unsafe { *out_session = core::ptr::null_mut() };
    let present = engine::Present {
        attach,
        context,
        width,
        height,
        size: None,
    };
    let input = if source.is_null() {
        if url.is_null() || url_len == 0 {
            return 0;
        }
        // SAFETY: the caller guarantees `url_len` readable code units.
        engine::Input::Url(unsafe { std::slice::from_raw_parts(url, url_len as usize) })
    } else {
        // SAFETY: the caller guarantees a live COM pointer; clone takes our own reference.
        let Some(object) = (unsafe {
            <windows::core::IUnknown as windows::core::Interface>::from_raw_borrowed(&source)
        })
        .cloned() else {
            return 0;
        };
        if mime.is_null() || mime_len == 0 {
            engine::Input::Source(object)
        } else {
            engine::Input::Stream {
                stream: object,
                // SAFETY: the caller guarantees `mime_len` readable code units.
                mime: unsafe { std::slice::from_raw_parts(mime, mime_len as usize) },
            }
        }
    };
    let (session, text) = engine_session::Session::open(
        input,
        target,
        &present,
        if start.is_finite() {
            start.max(0.0)
        } else {
            0.0
        },
        paused != 0,
        muted != 0,
    );
    if let Some(session) = session {
        let handle = Box::new(engine_session::SessionHandle(Some(session)));
        // SAFETY: the caller guarantees a writable out pointer.
        unsafe { *out_session = Box::into_raw(handle).cast() };
    }
    // SAFETY: the caller guarantees `report_capacity` writable bytes.
    unsafe { write_report(&text, report, report_capacity) }
}

/// The session behind a pointer from [`bale_gpu_engine_session_open`].
///
/// # Safety
/// `session` must be null or a live, unclosed session pointer.
#[cfg(windows)]
unsafe fn session_ref<'a>(session: *mut core::ffi::c_void) -> Option<&'a engine_session::Session> {
    // SAFETY: forwarded caller guarantee.
    unsafe { session.cast::<engine_session::SessionHandle>().as_ref() }?
        .0
        .as_deref()
}

/// Play. Returns an HRESULT.
///
/// # Safety
/// `session` must be null or a live, unclosed session pointer.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_gpu_engine_session_play(session: *mut core::ffi::c_void) -> i32 {
    // SAFETY: forwarded caller guarantee.
    unsafe { session_ref(session) }.map_or(E_POINTER, |s| s.play().0)
}

/// Pause. Returns an HRESULT.
///
/// # Safety
/// `session` must be null or a live, unclosed session pointer.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_gpu_engine_session_pause(session: *mut core::ffi::c_void) -> i32 {
    // SAFETY: forwarded caller guarantee.
    unsafe { session_ref(session) }.map_or(E_POINTER, |s| s.pause().0)
}

/// Seek to `seconds` (engine time); `approximate` non-zero allows a keyframe landing. Returns an HRESULT.
///
/// # Safety
/// `session` must be null or a live, unclosed session pointer.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_gpu_engine_session_seek(
    session: *mut core::ffi::c_void,
    seconds: f64,
    approximate: u32,
) -> i32 {
    // SAFETY: forwarded caller guarantee.
    unsafe { session_ref(session) }.map_or(E_POINTER, |s| s.seek(seconds, approximate != 0).0)
}

/// Volume 0..1 and mute. Returns an HRESULT.
///
/// # Safety
/// `session` must be null or a live, unclosed session pointer.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_gpu_engine_session_volume(
    session: *mut core::ffi::c_void,
    volume: f64,
    muted: u32,
) -> i32 {
    // SAFETY: forwarded caller guarantee.
    unsafe { session_ref(session) }.map_or(E_POINTER, |s| s.set_volume(volume, muted != 0).0)
}

/// New panel size in raw pixels. Returns an HRESULT.
///
/// # Safety
/// `session` must be null or a live, unclosed session pointer.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_gpu_engine_session_resize(
    session: *mut core::ffi::c_void,
    width: u32,
    height: u32,
) -> i32 {
    // SAFETY: forwarded caller guarantee.
    unsafe { session_ref(session) }.map_or(E_POINTER, |s| s.resize(width, height).0)
}

/// Live state and statistics into `*out`. Returns an HRESULT.
///
/// # Safety
/// `session` as [`session_ref`]; `out` must be a writable `SessionState`.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_gpu_engine_session_state(
    session: *mut core::ffi::c_void,
    out: *mut engine_session::SessionState,
) -> i32 {
    // SAFETY: the caller guarantees a writable struct (or null).
    let Some(out) = (unsafe { out.as_mut() }) else {
        return E_POINTER;
    };
    // SAFETY: forwarded caller guarantee.
    unsafe { session_ref(session) }.map_or(E_POINTER, |s| s.state(out).0)
}

/// Drains timestamped playback transitions (not frame/progress events). Returns an HRESULT.
/// `seconds` is the event clock now; `lost` counts overflow if the caller stopped polling. Each event
/// also carries its QueryPerformanceCounter milliseconds and, while the session's debug frame log is on
/// ([`bale_gpu_engine_session_frame_log`]), the engine position at the event (ABI 16).
///
/// # Safety
/// `session` as [`session_ref`]; `out` has `capacity` writable entries (1..=256),
/// and `count`, `seconds`, `lost` each point to a writable value.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_gpu_engine_session_events(
    session: *mut core::ffi::c_void,
    out: *mut engine_events::PlaybackEvent,
    capacity: u32,
    count: *mut u32,
    seconds: *mut f64,
    lost: *mut u32,
) -> i32 {
    if out.is_null() || capacity == 0 || capacity > 256 {
        return E_POINTER;
    }
    // SAFETY: writable scalar pointers and an output slice are the caller's guarantee.
    let (Some(count), Some(seconds), Some(lost)) =
        (unsafe { (count.as_mut(), seconds.as_mut(), lost.as_mut()) })
    else {
        return E_POINTER;
    };
    *count = 0;
    // SAFETY: validated non-null and bounded length; caller owns the storage.
    let out = unsafe { core::slice::from_raw_parts_mut(out, capacity as usize) };
    // SAFETY: forwarded caller guarantee.
    unsafe { session_ref(session) }.map_or(E_POINTER, |s| {
        s.playback_events(out, count, seconds, lost).0
    })
}

/// Debug frame log on (`on` non-zero) or off for this session: while on, each playback event
/// ([`bale_gpu_engine_session_events`]) also records the engine position at the event, read in the engine's
/// callback without the session lock. Only debug builds call it. Returns an HRESULT.
///
/// # Safety
/// `session` must be null or a live, unclosed session pointer.
#[unsafe(no_mangle)]
#[cfg(all(windows, feature = "debug-probes"))]
pub unsafe extern "C" fn bale_gpu_engine_session_frame_log(
    session: *mut core::ffi::c_void,
    on: u32,
) -> i32 {
    // SAFETY: forwarded caller guarantee.
    unsafe { session_ref(session) }.map_or(E_POINTER, |s| s.set_frame_log(on != 0).0)
}

/// Number of streams in the session's media resource into `*out`. Returns an HRESULT.
///
/// # Safety
/// `session` as [`session_ref`]; `out` must be writable.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_gpu_engine_session_stream_count(
    session: *mut core::ffi::c_void,
    out: *mut u32,
) -> i32 {
    // SAFETY: the caller guarantees a writable u32 (or null).
    let Some(out) = (unsafe { out.as_mut() }) else {
        return E_POINTER;
    };
    // SAFETY: forwarded caller guarantee.
    unsafe { session_ref(session) }.map_or(E_POINTER, |s| s.stream_count(out).0)
}

/// Stream `index` described into `*out` (kind, selection, channels, subtype, language, name).
/// Returns an HRESULT.
///
/// # Safety
/// `session` as [`session_ref`]; `out` must be a writable `StreamInfo`.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_gpu_engine_session_stream_info(
    session: *mut core::ffi::c_void,
    index: u32,
    out: *mut engine_session::StreamInfo,
) -> i32 {
    // SAFETY: the caller guarantees a writable struct (or null).
    let Some(out) = (unsafe { out.as_mut() }) else {
        return E_POINTER;
    };
    // SAFETY: forwarded caller guarantee.
    unsafe { session_ref(session) }.map_or(E_POINTER, |s| s.stream_info(index, out).0)
}

/// Selects stream `index`, deselecting the other streams of its major type. Returns an HRESULT.
///
/// # Safety
/// `session` must be null or a live, unclosed session pointer.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_gpu_engine_session_select_stream(
    session: *mut core::ffi::c_void,
    index: u32,
) -> i32 {
    // SAFETY: forwarded caller guarantee.
    unsafe { session_ref(session) }.map_or(E_POINTER, |s| s.select_stream(index).0)
}

/// Closes and frees the session (engine shutdown, then MF), writing the closing report.
///
/// # Safety
/// `session` must be a live session pointer, never used again; `report` as for the other reports.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_gpu_engine_session_close(
    session: *mut core::ffi::c_void,
    report: *mut u8,
    report_capacity: u32,
) -> u32 {
    if session.is_null() {
        return 0;
    }
    // SAFETY: the caller hands over the pointer from `bale_gpu_engine_session_open`.
    let handle = unsafe { Box::from_raw(session.cast::<engine_session::SessionHandle>()) };
    let text = handle.0.as_ref().map(|s| s.close()).unwrap_or_default();
    drop(handle);
    if report.is_null() {
        return 0;
    }
    // SAFETY: the caller guarantees `report_capacity` writable bytes.
    unsafe { write_report(&text, report, report_capacity) }
}

#[cfg(windows)]
const E_POINTER: i32 = 0x8000_4003u32 as i32;

/// Sets an IMFMediaEngine windowless swap chain `handle` on `panel` (any interface of a XAML
/// `SwapChainPanel`) through `ISwapChainPanelNative2`; returns the HRESULT.
///
/// # Safety
/// `panel` must be a live COM pointer for the call; call on the panel's UI thread.
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_gpu_panel_set_swap_chain_handle(
    panel: *mut core::ffi::c_void,
    handle: isize,
) -> i32 {
    // SAFETY: forwarded caller guarantees.
    unsafe { panel::set_swap_chain_handle(panel, handle) }.0
}

/// Runs the libass probe (font discovery through DirectWrite, render timings) and writes the
/// plain-text report as UTF-8 into `report`; returns the bytes written. `font` (`font_len` bytes, may
/// be null when 0) is added as an embedded font, standing in for an MKV attachment.
///
/// # Safety
/// `font` must point to `font_len` readable bytes (or be null with 0); `report` must point to
/// `report_capacity` writable bytes.
#[unsafe(no_mangle)]
#[cfg(all(windows, feature = "debug-probes"))]
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
#[cfg(all(windows, feature = "debug-probes"))]
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
/// format, for empty data, or if libass cannot parse the script. Converted cues use the default
/// style; [`bale_sub_overlay_load_text_styled`] takes one.
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
    // SAFETY: forwarded contract; a null style and font name are allowed.
    unsafe {
        bale_sub_overlay_load_text_styled(
            overlay,
            format,
            data,
            len,
            std::ptr::null(),
            std::ptr::null(),
            0,
        )
    }
}

/// [`bale_sub_overlay_load_text`] with SubRip and WebVTT cues drawn in `style` and the font family
/// `font_name` (UTF-8, `font_name_len` bytes, decoded lossily): the profile's subtitle appearance
/// (`subs::convert::TextStyle`, `ConvertOptions::styled`, which keeps the default for any value
/// libass could not use). A null `style` is the default style and ignores the name; a null or
/// empty name keeps Arial. ASS/SSA keeps its own styles, as on the official clients.
///
/// # Safety
/// `overlay` and `data` as in [`bale_sub_overlay_load_text`]; `style` must be null or readable;
/// `font_name` must point to `font_name_len` readable bytes (or be null with 0).
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_sub_overlay_load_text_styled(
    overlay: *mut subs::overlay::Overlay,
    format: u32,
    data: *const u8,
    len: usize,
    style: *const subs::convert::TextStyle,
    font_name: *const u8,
    font_name_len: usize,
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
    // SAFETY: the caller guarantees `style` is null or readable; it is copied.
    let style = unsafe { style.as_ref() }.copied();
    let font_name = if font_name.is_null() || font_name_len == 0 {
        None
    } else {
        // SAFETY: the caller guarantees `font_name_len` readable bytes.
        Some(unsafe { std::slice::from_raw_parts(font_name, font_name_len) })
    };
    // SAFETY: forwarded contract.
    unsafe {
        with_overlay(overlay, |overlay| {
            let options = match style {
                Some(style) => {
                    let family = font_name.map(String::from_utf8_lossy);
                    subs::convert::ConvertOptions::styled(&style, family.as_deref())
                }
                None => subs::convert::ConvertOptions::default(),
            };
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

/// Version of the caption ABI (`bale_cc_*`). The FFmpegInteropX tap (`bale_cc_push`,
/// `bale_cc_flush`) requires 1; the app's exports are only ever added under it.
#[unsafe(no_mangle)]
pub extern "C" fn bale_cc_abi_version() -> u32 {
    subs::cc::CC_ABI_VERSION
}

/// The fork's tap: one compressed packet of the playing video stream of the media source opened with
/// `bale_cc_sink=<token>`, in decode order, on its sample (or GPU decoder feeder) thread. `codec` is
/// 1 = H.264, 2 = HEVC, 3 = MPEG-2; `nal_length_size` 1, 2 or 4 for length-prefixed H.264/HEVC
/// (avcC/hvcC) and 0 for Annex B and MPEG-2; `pts_100ns` the `MediaStreamSample` timeline,
/// `INT64_MIN` when the packet has none. `data` is only read during the call. Returns quickly and
/// never blocks: 0, `E_NOT_FOUND` when no decoder holds the token, `E_INVALIDARG` for an unknown
/// codec or size, `E_POINTER` for null data with a length.
///
/// # Safety
/// `data` must point to `len` readable bytes (or be null with 0).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_cc_push(
    token: u64,
    data: *const u8,
    len: usize,
    pts_100ns: i64,
    codec: u32,
    nal_length_size: u32,
) -> i32 {
    if data.is_null() && len != 0 {
        return subs::E_POINTER;
    }
    let data: &[u8] = if len == 0 {
        &[]
    } else {
        // SAFETY: the caller guarantees `len` readable bytes.
        unsafe { std::slice::from_raw_parts(data, len) }
    };
    std::panic::catch_unwind(|| subs::cc::push(token, data, pts_100ns, codec, nal_length_size))
        .unwrap_or(subs::E_UNEXPECTED)
}

/// The fork's tap: the stream was flushed (seek, stream switch, close). What the decoder held and
/// shows is dropped. 0, or `E_NOT_FOUND` when no decoder holds the token.
#[unsafe(no_mangle)]
pub extern "C" fn bale_cc_flush(token: u64) -> i32 {
    std::panic::catch_unwind(|| subs::cc::flush(token)).unwrap_or(subs::E_UNEXPECTED)
}

/// Runs `call` on the decoder behind `captions`: `E_POINTER` for null, `E_UNEXPECTED` for a panic.
///
/// # Safety
/// `captions` must be null or a live handle from [`bale_cc_create`].
unsafe fn with_captions(
    captions: *const subs::cc::Captions,
    call: impl FnOnce(&std::sync::Arc<subs::cc::Captions>) -> i32,
) -> i32 {
    if captions.is_null() {
        return subs::E_POINTER;
    }
    // SAFETY: a live handle is an `Arc::into_raw` the caller still owns; this borrows it.
    let captions = std::mem::ManuallyDrop::new(unsafe { std::sync::Arc::from_raw(captions) });
    std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| call(&captions)))
        .unwrap_or(subs::E_UNEXPECTED)
}

/// Creates a caption decoder on CC1, registered under no token and attached to no overlay. Returns
/// an HRESULT; on failure `*out` is null.
///
/// # Safety
/// `out` must be null or writable for one pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_cc_create(out: *mut *const subs::cc::Captions) -> i32 {
    if out.is_null() {
        return subs::E_POINTER;
    }
    match std::panic::catch_unwind(subs::cc::Captions::new) {
        Ok(captions) => {
            // SAFETY: the caller guarantees writable output storage.
            unsafe { *out = std::sync::Arc::into_raw(captions) };
            subs::S_OK
        }
        Err(_) => {
            // SAFETY: as above.
            unsafe { *out = std::ptr::null() };
            subs::E_UNEXPECTED
        }
    }
}

/// Takes the tap's packets for `token` (the media source's `bale_cc_sink`, 1..999999999) as a new
/// stream, dropping what was decoded, seen and counted; 0 takes none. A token registered to
/// another decoder moves here. A remount registers its new token.
///
/// # Safety
/// `captions` must be null or a live handle from [`bale_cc_create`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_cc_register(captions: *const subs::cc::Captions, token: u64) -> i32 {
    // SAFETY: forwarded contract.
    unsafe { with_captions(captions, |captions| captions.register(token)) }
}

/// Decodes `channel` 1..=4 (CC1..CC4) from now on; `service` is the CEA-708 service number, 0 for
/// none (`E_NOTIMPL` for 1..=63 until 708 is decoded). A change starts afresh and clears an
/// attached overlay.
///
/// # Safety
/// `captions` must be null or a live handle from [`bale_cc_create`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_cc_select(
    captions: *const subs::cc::Captions,
    channel: u32,
    service: u32,
) -> i32 {
    // SAFETY: forwarded contract.
    unsafe { with_captions(captions, |captions| captions.select(channel, service)) }
}

/// What the stream has carried since registration: bit 0..3 = CC1..CC4 had captions, bit 8 = A/53
/// cc_data at all, bit 9 = CEA-708 data. 0 for a null handle.
///
/// # Safety
/// `captions` must be null or a live handle from [`bale_cc_create`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_cc_seen(captions: *const subs::cc::Captions) -> u32 {
    let mut seen = 0;
    // SAFETY: forwarded contract.
    unsafe {
        with_captions(captions, |captions| {
            seen = captions.seen();
            subs::S_OK
        })
    };
    seen
}

/// Copies the decoder's counters to `out`.
///
/// # Safety
/// `captions` must be null or a live handle from [`bale_cc_create`]; `out` must be null or writable
/// for one [`subs::cc::BaleCcStats`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_cc_stats(
    captions: *const subs::cc::Captions,
    out: *mut subs::cc::BaleCcStats,
) -> i32 {
    if out.is_null() {
        return subs::E_POINTER;
    }
    // SAFETY: forwarded contract; `out` is writable (checked non-null above).
    unsafe {
        with_captions(captions, |captions| {
            out.write_unaligned(captions.stats());
            subs::S_OK
        })
    }
}

/// Draws the decoder's captions on `overlay` from now on: the overlay's track becomes a live caption
/// track (replacing a loaded one) in `style` and the font family `font_name` (UTF-8, as
/// [`bale_sub_overlay_load_text_styled`]; null is the default style), starting with the captions
/// kept and the one shown now. `flags` 1 draws them in the appearance alone, without 608 colours,
/// italics, underline and backgrounds. Does not wait for the render thread. Another load or clear
/// of the overlay, or another attach, ends it.
///
/// # Safety
/// `captions` and `overlay` must be null or live handles; `style` must be null or readable;
/// `font_name` must point to `font_name_len` readable bytes (or be null with 0).
#[unsafe(no_mangle)]
#[cfg(windows)]
pub unsafe extern "C" fn bale_cc_attach(
    captions: *const subs::cc::Captions,
    overlay: *mut subs::overlay::Overlay,
    style: *const subs::convert::TextStyle,
    font_name: *const u8,
    font_name_len: usize,
    flags: u32,
) -> i32 {
    if flags & !subs::cc::ass::FLAG_PLAIN != 0 || (font_name.is_null() && font_name_len != 0) {
        return subs::E_INVALIDARG;
    }
    // SAFETY: the caller guarantees `style` is null or readable; it is copied.
    let style = unsafe { style.as_ref() }.copied();
    let font_name = if font_name.is_null() || font_name_len == 0 {
        None
    } else {
        // SAFETY: the caller guarantees `font_name_len` readable bytes.
        Some(unsafe { std::slice::from_raw_parts(font_name, font_name_len) })
    };
    let options = match style {
        Some(style) => {
            let family = font_name.map(String::from_utf8_lossy);
            subs::convert::ConvertOptions::styled(&style, family.as_deref())
        }
        None => subs::convert::ConvertOptions::default(),
    };
    // SAFETY: forwarded contract.
    unsafe {
        with_captions(captions, |captions| {
            with_overlay(overlay, |overlay| {
                match overlay.begin_live(subs::cc::header(&options).into_bytes()) {
                    Ok((generation, sink)) => captions.attach(sink, generation, options, flags),
                    Err(hr) => hr,
                }
            })
        })
    }
}

/// Stops drawing the decoder's captions and drops the overlay's live caption track if it is still
/// this decoder's.
///
/// # Safety
/// `captions` must be null or a live handle from [`bale_cc_create`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_cc_detach(captions: *const subs::cc::Captions) -> i32 {
    // SAFETY: forwarded contract.
    unsafe {
        with_captions(captions, |captions| {
            captions.detach();
            subs::S_OK
        })
    }
}

/// Unregisters and detaches the decoder and releases the handle; a push already running finishes
/// first on its own reference. Null is a no-op.
///
/// # Safety
/// `captions` must be null or a handle from [`bale_cc_create`] that is not used again.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_cc_destroy(captions: *const subs::cc::Captions) {
    if captions.is_null() {
        return;
    }
    // SAFETY: the caller hands back its reference from `Arc::into_raw`.
    let captions = unsafe { std::sync::Arc::from_raw(captions) };
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        subs::cc::unregister(&captions);
        captions.detach();
        drop(captions);
    }));
}

#[cfg(all(windows, feature = "debug-probes"))]
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
