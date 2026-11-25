//! Panic-contained ABI 1. A failure never publishes a partially constructed handle.
use super::types::{
    BALE_TONEMAP_INPUT_NV12, BALE_TONEMAP_OUTPUT_DOWNSCALE, BALE_TONEMAP_OUTPUT_NV12,
    BALE_TONEMAP_OUTPUT_NV12_DITHERED, BALE_TONEMAP_OUTPUT_P010, BALE_TONEMAP_SDR709,
    TONEMAP_ABI_VERSION,
};
#[cfg(windows)]
use super::{
    gpu::ToneMapGpu,
    types::{BaleToneMapConfig, BaleToneMapFrameInfo},
};
use std::panic::{AssertUnwindSafe, catch_unwind};

pub const S_OK: i32 = 0;
pub const S_FALSE: i32 = 1;
pub const E_INVALIDARG: i32 = super::cpu::E_INVALIDARG;
pub const E_UNEXPECTED: i32 = 0x8000ffffu32 as i32;

fn boundary(f: impl FnOnce() -> i32) -> i32 {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or(E_UNEXPECTED)
}

#[unsafe(no_mangle)]
pub extern "C" fn bale_tonemap_abi_version() -> u32 {
    TONEMAP_ABI_VERSION
}

/// Output formats `bale_tonemap_apply` accepts (additive to ABI 1; absent in older DLLs = NV12
/// only). P010 holds the same BT.709 limited-range SDR result as 10-bit codes; apply picks the
/// depth from the output texture's format. DOWNSCALE: the config may name a larger visible
/// source size in `reserved[0..2]`, area-downscaled in display-linear light. INPUT_NV12: the input
/// may be NV12. SDR709: BT.709 SDR sources are accepted (scale only, no tone curve or gamut change).
/// NV12_DITHERED: NV12 output is ordered-dithered to 8 bits (static 8x8 Bayer), so a caller may ask
/// for 8-bit stage output on an 8-bit display without banding (Bale's `bale_tonemap_output_depth`).
/// Bit 64 is retired (2026-11-24) and never set: decoder arrays are always copied first.
#[unsafe(no_mangle)]
pub extern "C" fn bale_tonemap_output_formats() -> u32 {
    BALE_TONEMAP_OUTPUT_NV12
        | BALE_TONEMAP_OUTPUT_P010
        | BALE_TONEMAP_OUTPUT_DOWNSCALE
        | BALE_TONEMAP_INPUT_NV12
        | BALE_TONEMAP_SDR709
        | BALE_TONEMAP_OUTPUT_NV12_DITHERED
}

/// Create on the decoder thread, with caller-held D3D multithread protection. Includes a
/// blocking tiny-pattern self-test. The handle retains the device, never caller textures.
/// # Safety
/// Non-null device is a live ID3D11Device; config points to a readable complete ABI 1 record.
/// out is writable for one handle pointer and must not alias config. It is nulled on failure.
/// Calls on one handle, including destroy, must be externally serialized.
#[cfg(windows)]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_tonemap_create(
    device: *mut core::ffi::c_void,
    config: *const BaleToneMapConfig,
    out: *mut *mut ToneMapGpu,
) -> i32 {
    use windows::{Win32::Graphics::Direct3D11::ID3D11Device, core::Interface};
    boundary(|| {
        if out.is_null() {
            return E_INVALIDARG;
        }
        // SAFETY: caller supplies writable output and complete readable config storage.
        unsafe { out.write(std::ptr::null_mut()) };
        if config.is_null() {
            return E_INVALIDARG;
        }
        let config = unsafe { config.read_unaligned() };
        if let Err(hr) = config.validate() {
            return hr;
        }
        // SAFETY: borrow without consuming caller's COM reference.
        let Some(device) = (unsafe { ID3D11Device::from_raw_borrowed(&device) }) else {
            return E_INVALIDARG;
        };
        match ToneMapGpu::create(device, config) {
            Ok(gpu) => {
                // SAFETY: transfer sole Box ownership to validated output slot.
                unsafe { out.write(Box::into_raw(Box::new(gpu))) };
                S_OK
            }
            Err(error) => error.code().0,
        }
    })
}

/// Queue HDR P010 -> SDR NV12 work, restoring the context's pipeline state. No flush (a measured
/// apply of a stage-timing build with timing on requests two submissions; it never waits for the GPU).
/// Input readiness/synchronization and post-submit device-loss detection belong to caller.
/// # Safety
/// handle is live/exclusive; other non-null pointers are live COM interfaces or a readable
/// frame record. Resources must be unmapped, same-device, and exclusively available until
/// their queued GPU uses finish. Caller holds multithread protection; no concurrent destroy.
#[cfg(windows)]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_tonemap_apply(
    handle: *mut ToneMapGpu,
    context: *mut core::ffi::c_void,
    input: *mut core::ffi::c_void,
    input_subresource: u32,
    output: *mut core::ffi::c_void,
    frame: *const BaleToneMapFrameInfo,
) -> i32 {
    use windows::{
        Win32::Graphics::Direct3D11::{ID3D11DeviceContext, ID3D11Texture2D},
        core::Interface,
    };
    boundary(|| {
        // SAFETY: caller guarantees a live exclusive handle and complete readable frame record.
        let Some(gpu) = (unsafe { handle.as_mut() }) else {
            return E_INVALIDARG;
        };
        if frame.is_null() {
            return E_INVALIDARG;
        }
        let frame = unsafe { frame.read_unaligned() };
        if let Err(hr) = frame.validate() {
            return hr;
        }
        // SAFETY: all non-null interface pointers are borrowed, never consumed.
        let (Some(context), Some(input), Some(output)) = (unsafe {
            (
                ID3D11DeviceContext::from_raw_borrowed(&context),
                ID3D11Texture2D::from_raw_borrowed(&input),
                ID3D11Texture2D::from_raw_borrowed(&output),
            )
        }) else {
            return E_INVALIDARG;
        };
        gpu.apply(context, input, input_subresource, output, &frame)
            .map_or_else(|e| e.code().0, |()| S_OK)
    })
}

/// Release all handle-owned resources. Null is a no-op.
/// # Safety
/// handle is null or returned by create, relinquished exactly once, with no concurrent calls.
#[cfg(windows)]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_tonemap_destroy(handle: *mut ToneMapGpu) {
    boundary(|| {
        if !handle.is_null() {
            // SAFETY: caller transfers the Box's unique ownership back exactly once.
            unsafe { drop(Box::from_raw(handle)) };
        }
        S_OK
    });
}

/// The source peak the most recently created stage applies: from the stream's HDR metadata for PQ
/// (`cpu::source_peak`), else the config peak (additive to ABI 1, 2026-11-24, every build). For Bale's
/// once-per-stream log line and stats row. No GPU work and no wait: S_FALSE while apply updates the
/// record (output untouched; read again later).
/// # Safety
/// out is writable for one complete BaleToneMapPeakInfo record.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_tonemap_peak_read(
    out: *mut super::types::BaleToneMapPeakInfo,
) -> i32 {
    boundary(|| {
        if out.is_null() {
            return E_INVALIDARG;
        }
        let Some(info) = super::peak::snapshot() else {
            return S_FALSE;
        };
        // SAFETY: complete local record and caller-provided writable output; no retained pointer.
        unsafe { out.write_unaligned(info) };
        S_OK
    })
}

/// Debug-package-only, additive exports. The flag is captured after each stage's create self-test;
/// an active stage keeps its setting. No query objects exist in builds without stage-timing.
#[cfg(feature = "stage-timing")]
#[unsafe(no_mangle)]
pub extern "C" fn bale_tonemap_debug_timing_enable(enabled: u32) -> i32 {
    boundary(|| {
        if enabled > 1 {
            return E_INVALIDARG;
        }
        super::timing::ENABLED.store(enabled != 0, std::sync::atomic::Ordering::Relaxed);
        S_OK
    })
}

/// Copy the latest measured stage's completed window (or collecting/error/closed state).
/// Returns S_FALSE on snapshot contention without changing the output; no D3D or source lock.
/// # Safety
/// out is writable for one complete BaleToneMapTimingStats record.
#[cfg(feature = "stage-timing")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_tonemap_debug_timing_read(
    out: *mut super::types::BaleToneMapTimingStats,
) -> i32 {
    boundary(|| {
        if out.is_null() {
            return E_INVALIDARG;
        }
        let Some(stats) = super::timing::snapshot() else {
            return S_FALSE;
        };
        // SAFETY: complete local record and caller-provided writable output; no retained pointer.
        unsafe {
            out.write_unaligned(stats);
        }
        S_OK
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    #[cfg(feature = "stage-timing")]
    #[test]
    fn debug_snapshot_validates_arguments_without_touching_a_device() {
        use super::super::types::BaleToneMapTimingStats;
        assert_eq!(bale_tonemap_debug_timing_enable(2), E_INVALIDARG);
        // SAFETY: null is explicitly rejected before any write; the second output is complete storage.
        unsafe {
            assert_eq!(
                bale_tonemap_debug_timing_read(std::ptr::null_mut()),
                E_INVALIDARG
            );
            let mut stats = BaleToneMapTimingStats::default();
            assert_eq!(bale_tonemap_debug_timing_read(&mut stats), S_OK);
            assert_eq!(stats.state, 0);
            assert_eq!(stats.session, 0);
        }
    }
    #[test]
    fn peak_snapshot_validates_arguments_and_never_waits() {
        // SAFETY: null is rejected before any write; the second output is complete storage.
        unsafe {
            assert_eq!(bale_tonemap_peak_read(std::ptr::null_mut()), E_INVALIDARG);
            let mut info = super::super::types::BaleToneMapPeakInfo {
                changes: 7,
                ..Default::default()
            };
            // S_FALSE (another test holds the record) leaves the output untouched.
            match bale_tonemap_peak_read(&mut info) {
                S_OK => assert_eq!(info.stage, 0, "unit tests create no stage"),
                S_FALSE => assert_eq!(info.changes, 7),
                hr => panic!("HRESULT 0x{:08x}", hr as u32),
            }
        }
    }
    #[test]
    fn version_and_panic_boundary() {
        assert_eq!(bale_tonemap_abi_version(), 1);
        assert_eq!(bale_tonemap_output_formats(), 63);
        assert_eq!(boundary(|| panic!("boundary test")), E_UNEXPECTED);
    }
}
