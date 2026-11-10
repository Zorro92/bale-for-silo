//! Grain ABI 2: ABI 1 without `bale_grain_cpu_apply` (CPU grain); the remaining exports are
//! unchanged. All raw-pointer access and unwinding containment live here.

use std::panic::{AssertUnwindSafe, catch_unwind};

use super::params::{BaleGrainParams, PARAMS_SIZE, ValidGrainParams};

pub const GRAIN_ABI_VERSION: u32 = 2;
pub const S_OK: i32 = 0;
pub const E_INVALIDARG: i32 = 0x80070057u32 as i32;
pub const E_UNEXPECTED: i32 = 0x8000ffffu32 as i32;

fn boundary(f: impl FnOnce() -> i32) -> i32 {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or(E_UNEXPECTED)
}

#[unsafe(no_mangle)]
pub extern "C" fn bale_grain_abi_version() -> u32 {
    catch_unwind(|| GRAIN_ABI_VERSION).unwrap_or(E_UNEXPECTED as u32)
}

/// Validate a parameter record without logging its contents.
///
/// # Safety
/// A non-null `params` must point to a readable four-byte `struct_size`. When it
/// equals 184, the complete record must be readable and stable for this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_grain_validate(params: *const BaleGrainParams) -> i32 {
    boundary(|| {
        // SAFETY: the caller supplies readable parameter storage as documented above.
        match unsafe { read_params(params) } {
            Ok(_) => S_OK,
            Err(hr) => hr,
        }
    })
}

/// Create a serialized-owner GPU grain handle. Success includes the NV12/P010 self-test.
/// The device/context are retained; caller textures are never retained by the handle.
///
/// # Safety
/// Non-null device/context pointers must be live ID3D11Device/ID3D11DeviceContext interfaces.
/// `out` must be null or writable for one aligned handle pointer. All pointers stay live for
/// this call; on failure a non-null `out` is always set to null. The caller must serialize
/// all handle operations, including destruction; sole ownership may move between threads.
#[cfg(windows)]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_grain_gpu_create(
    device: *mut core::ffi::c_void,
    context: *mut core::ffi::c_void,
    out: *mut *mut super::gpu::BaleGrainGpu,
) -> i32 {
    use windows::Win32::Graphics::Direct3D11::{ID3D11Device, ID3D11DeviceContext};
    use windows::core::Interface;

    boundary(|| {
        if out.is_null() {
            return E_INVALIDARG;
        }
        // SAFETY: the caller guarantees writable, aligned output storage.
        unsafe { out.write(std::ptr::null_mut()) };
        // SAFETY: non-null inputs are live borrowed interfaces for this call. The handle's
        // QueryInterface calls acquire its own references without consuming the caller's.
        let (Some(device), Some(context)) = (unsafe {
            (
                ID3D11Device::from_raw_borrowed(&device),
                ID3D11DeviceContext::from_raw_borrowed(&context),
            )
        }) else {
            return E_INVALIDARG;
        };
        match super::gpu::BaleGrainGpu::create(device, context) {
            Ok(gpu) => {
                // SAFETY: the output is valid; ownership transfers to the serialized C-ABI owner.
                unsafe { out.write(Box::into_raw(Box::new(gpu))) };
                S_OK
            }
            Err(error) => error.code().0,
        }
    })
}

/// Apply GPU grain without flushing or changing the caller's pipeline state.
/// A panic or device loss permanently poisons this stream's handle.
///
/// # Safety
/// A non-null handle must be live and exclusively used, with no concurrent handle calls or
/// destruction (no permanent thread affinity). Parameters follow [`bale_grain_validate`]'s contract. Non-null
/// source/destination pointers must be live ID3D11Texture2D interfaces on the handle's device,
/// unmapped and exclusively available for this call (including their underlying resources).
#[cfg(windows)]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_grain_gpu_apply(
    handle: *mut super::gpu::BaleGrainGpu,
    params: *const BaleGrainParams,
    src_staging: *mut core::ffi::c_void,
    dst_pooled: *mut core::ffi::c_void,
) -> i32 {
    use windows::Win32::Graphics::Direct3D11::ID3D11Texture2D;
    use windows::core::Interface;

    boundary(|| {
        // SAFETY: a non-null handle is exclusively borrowed under the caller's serialization.
        let Some(gpu) = (unsafe { handle.as_mut() }) else {
            return E_INVALIDARG;
        };
        if let Some(hr) = gpu.failure() {
            return hr;
        }
        let hr = boundary(|| {
            // SAFETY: parameters are readable as documented; validation produces an owned copy.
            let params = match unsafe { read_params(params) } {
                Ok(params) => params,
                Err(hr) => return hr,
            };
            // SAFETY: the caller supplies live interfaces. Borrow only, never consume references.
            let (Some(src), Some(dst)) = (unsafe {
                (
                    ID3D11Texture2D::from_raw_borrowed(&src_staging),
                    ID3D11Texture2D::from_raw_borrowed(&dst_pooled),
                )
            }) else {
                return E_INVALIDARG;
            };
            gpu.apply(&params, src, dst)
                .map_or_else(|error| error.code().0, |()| S_OK)
        });
        if hr == E_UNEXPECTED {
            // The inner boundary runs after ContextGuard unwinds/restores state and releases its lock.
            gpu.poison(hr);
        }
        hr
    })
}

/// Release a GPU handle and its retained device/context references. Null is a no-op.
///
/// # Safety
/// A non-null pointer must be a live handle returned by create, with sole ownership and no
/// concurrent calls. Destruction may run on a different thread after synchronizing with the
/// previous owner. It must happen exactly once and the handle must never be used afterwards.
#[cfg(windows)]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_grain_gpu_destroy(handle: *mut super::gpu::BaleGrainGpu) {
    boundary(|| {
        if !handle.is_null() {
            // SAFETY: ownership of this once-created Box returns exclusively to this call.
            unsafe { drop(Box::from_raw(handle)) };
        }
        S_OK
    });
}

/// Run synthetic hardware grain correctness/timing checks, blocking the calling thread.
/// Writes UTF-8 without a trailing NUL, truncating only at character boundaries.
/// A panic writes "panic" (capacity permitting); null/empty output returns zero.
///
/// # Safety
/// A non-null `report` must point to `capacity` exclusively writable bytes for this call.
#[cfg(windows)]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_grain_gpu_probe(report: *mut u8, capacity: u32) -> u32 {
    // SAFETY: the caller supplies writable report storage as documented above.
    unsafe { probe_report_boundary(report, capacity, super::probe::run) }
}

#[cfg(any(windows, test))]
unsafe fn probe_report_boundary(
    report: *mut u8,
    capacity: u32,
    run: impl FnOnce() -> String,
) -> u32 {
    if report.is_null() || capacity == 0 {
        return 0;
    }
    // All GPU resources unwind before the panic fallback is written; no exception text is exposed.
    let text = catch_unwind(AssertUnwindSafe(run));
    let text = text.as_deref().unwrap_or("panic");
    // SAFETY: the caller guarantees capacity writable bytes; write_report bounds the UTF-8 copy.
    unsafe { crate::write_report(text, report, capacity) }
}

unsafe fn read_params(params: *const BaleGrainParams) -> Result<ValidGrainParams, i32> {
    if params.is_null() || (params as usize).checked_add(4).is_none() {
        return Err(E_INVALIDARG);
    }
    // SAFETY: a non-null parameter pointer has at least a readable four-byte header.
    // Unaligned reads deliberately do not require the caller's pointer to be aligned.
    let size = unsafe { params.cast::<u32>().read_unaligned() };
    if size != PARAMS_SIZE
        || (params as usize)
            .checked_add(PARAMS_SIZE as usize)
            .is_none()
    {
        return Err(E_INVALIDARG);
    }
    // SAFETY: once struct_size matches, the caller guarantees the full record is readable.
    // All bit patterns of the C record's integer fields are valid Rust values.
    let raw = unsafe { params.read_unaligned() };
    raw.validate().map_err(|_| E_INVALIDARG)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::grain::params::test_params;

    #[test]
    fn versions_and_panic_containment() {
        assert_eq!(crate::ABI_VERSION, 5);
        assert_eq!(bale_grain_abi_version(), 2);
        assert_eq!(boundary(|| panic!("test boundary")), E_UNEXPECTED);
    }

    #[test]
    fn probe_report_truncates_utf8_without_overwriting_guards() {
        let text = "ok µ grain";
        for capacity in 0..=16 {
            let mut buffer = [0xa5; 18];
            // SAFETY: capacity bytes starting at index one are writable, with guards on both sides.
            let written = unsafe {
                probe_report_boundary(buffer.as_mut_ptr().add(1), capacity, || text.into())
            } as usize;
            let mut expected = text.len().min(capacity as usize);
            while !text.is_char_boundary(expected) {
                expected -= 1;
            }
            assert_eq!(written, expected);
            assert_eq!(&buffer[1..1 + written], &text.as_bytes()[..written]);
            assert_eq!(buffer[0], 0xa5);
            assert!(buffer[1 + written..].iter().all(|&byte| byte == 0xa5));
        }
    }

    #[test]
    fn probe_report_contains_panics_and_skips_null_or_empty_outputs() {
        let mut buffer = [0xa5; 8];
        // SAFETY: non-null storage covers each requested capacity; null/empty outputs are rejected.
        unsafe {
            assert_eq!(
                probe_report_boundary(std::ptr::null_mut(), 8, || panic!("not called")),
                0
            );
            assert_eq!(
                probe_report_boundary(buffer.as_mut_ptr(), 0, || panic!("not called")),
                0
            );
            assert_eq!(
                probe_report_boundary(buffer.as_mut_ptr(), 8, || panic!("test boundary")),
                5
            );
            assert_eq!(&buffer[..5], b"panic");
            assert_eq!(&buffer[5..], &[0xa5; 3]);
            assert_eq!(
                probe_report_boundary(buffer.as_mut_ptr(), 3, || panic!("test boundary")),
                3
            );
            assert_eq!(&buffer[..3], b"pan");
        }
    }

    #[test]
    fn validate_rejects_null_and_short_records() {
        let p = test_params();
        let short = 4u32;
        // SAFETY: non-null pointers refer to live locals; a short size is rejected before reading more.
        unsafe {
            assert_eq!(bale_grain_validate(std::ptr::null()), E_INVALIDARG);
            assert_eq!(
                bale_grain_validate((&short as *const u32).cast()),
                E_INVALIDARG
            );
            assert_eq!(bale_grain_validate(&p), S_OK);
        }
    }
}
