//! Grain ABI 1. All raw-pointer access and unwinding containment live here.

use std::panic::{AssertUnwindSafe, catch_unwind};

use super::cpu::{self, PixelFormat};
use super::params::{BaleGrainParams, PARAMS_SIZE, ValidGrainParams};

pub const GRAIN_ABI_VERSION: u32 = 1;
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

/// Apply grain in place to NV12 (format 1) or P010 (format 2).
/// Pitches are signed bytes; each pointer addresses its first logical row.
/// Threads is 0 (auto, capped at four) or 1..=4. No parameter contents are logged.
///
/// # Safety
/// `params` has the same contract as [`bale_grain_validate`]. Each plane must be
/// exclusively writable for the complete visible span implied by the validated
/// dimensions and pitch, within one allocation. Negative pitches extend backwards
/// from the first-row pointer. The allocations must remain live throughout the
/// call, with no concurrent access. The two plane spans must not overlap.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_grain_cpu_apply(
    params: *const BaleGrainParams,
    format: u32,
    y: *mut u8,
    y_pitch: isize,
    uv: *mut u8,
    uv_pitch: isize,
    threads: u32,
) -> i32 {
    boundary(|| {
        if y.is_null() || uv.is_null() || threads > 4 {
            return E_INVALIDARG;
        }
        // SAFETY: the caller supplies readable parameter storage. The returned snapshot is owned,
        // so no parameter reference can alias the mutable image slices constructed below.
        let params = match unsafe { read_params(params) } {
            Ok(params) => params,
            Err(hr) => return hr,
        };
        let Ok(format) = PixelFormat::try_from(format) else {
            return E_INVALIDARG;
        };
        let Ok([yl, uvl]) = cpu::layouts(&params, format, y_pitch, uv_pitch) else {
            return E_INVALIDARG;
        };
        let Some(ys) = (y as usize)
            .checked_sub(yl.first_offset())
            .filter(|&a| a != 0)
        else {
            return E_INVALIDARG;
        };
        let Some(uvs) = (uv as usize)
            .checked_sub(uvl.first_offset())
            .filter(|&a| a != 0)
        else {
            return E_INVALIDARG;
        };
        let Some(ye) = ys.checked_add(yl.span) else {
            return E_INVALIDARG;
        };
        let Some(uve) = uvs.checked_add(uvl.span) else {
            return E_INVALIDARG;
        };
        if ys < uve && uvs < ye {
            return E_INVALIDARG;
        }
        // SAFETY: layouts checked stride, row sizes, multiplication and isize::MAX bounds;
        // address arithmetic was checked above and the spans do not overlap. The caller
        // guarantees exclusive live allocations covering both spans, including negative strides.
        let (y, uv) = unsafe {
            (
                std::slice::from_raw_parts_mut(y.wrapping_sub(yl.first_offset()), yl.span),
                std::slice::from_raw_parts_mut(uv.wrapping_sub(uvl.first_offset()), uvl.span),
            )
        };
        match cpu::apply(&params, format, y, y_pitch, uv, uv_pitch, threads) {
            Ok(()) => S_OK,
            Err(_) => E_INVALIDARG,
        }
    })
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
        assert_eq!(crate::ABI_VERSION, 4);
        assert_eq!(bale_grain_abi_version(), 1);
        assert_eq!(boundary(|| panic!("test boundary")), E_UNEXPECTED);
    }

    #[test]
    fn null_size_format_pitch_and_alias_checks() {
        let p = test_params();
        let short = 4u32;
        let mut y = vec![0; 80 * 67];
        let mut uv = vec![0; 80 * 34];
        // SAFETY: all non-null pointers refer to live locals with their documented extents;
        // invalid layouts/aliases are rejected before any mutable slices are formed.
        unsafe {
            assert_eq!(bale_grain_validate(std::ptr::null()), E_INVALIDARG);
            assert_eq!(
                bale_grain_validate((&short as *const u32).cast()),
                E_INVALIDARG
            );
            assert_eq!(bale_grain_validate(&p), S_OK);
            for (format, yp, uvp, threads) in [
                (0, 80, 80, 1),
                (2, 80, 80, 1),
                (1, 0, 80, 1),
                (1, 80, -1, 1),
                (1, isize::MAX, 80, 1),
                (1, 80, 80, 5),
            ] {
                assert_eq!(
                    bale_grain_cpu_apply(
                        &p,
                        format,
                        y.as_mut_ptr(),
                        yp,
                        uv.as_mut_ptr(),
                        uvp,
                        threads
                    ),
                    E_INVALIDARG
                );
            }
            assert_eq!(
                bale_grain_cpu_apply(&p, 1, std::ptr::null_mut(), 80, uv.as_mut_ptr(), 80, 1),
                E_INVALIDARG
            );
            assert_eq!(
                bale_grain_cpu_apply(&p, 1, y.as_mut_ptr(), 80, std::ptr::null_mut(), 80, 1),
                E_INVALIDARG
            );
            assert_eq!(
                bale_grain_cpu_apply(&p, 1, y.as_mut_ptr(), 80, y.as_mut_ptr(), 80, 1),
                E_INVALIDARG
            );
        }
        assert!(y.iter().chain(&uv).all(|&v| v == 0));
    }

    #[test]
    fn ffi_signed_unaligned_p010_matches_safe_api() {
        let mut p = test_params();
        p.bit_depth = 10;
        let mut y = vec![128; 151 * 67 + 1];
        let mut uv = vec![128; 155 * 34 + 1];
        let mut expected_y = y.clone();
        let mut expected_uv = uv.clone();
        cpu::apply(
            &p.validate().unwrap(),
            PixelFormat::P010,
            &mut expected_y[1..],
            -151,
            &mut expected_uv[1..],
            155,
            1,
        )
        .unwrap();
        // SAFETY: pointers address first logical rows inside disjoint live allocations;
        // both allocations cover the checked signed-pitch spans for this call.
        let hr = unsafe {
            bale_grain_cpu_apply(
                &p,
                2,
                y.as_mut_ptr().add(1 + 151 * 66),
                -151,
                uv.as_mut_ptr().add(1),
                155,
                4,
            )
        };
        assert_eq!(hr, S_OK);
        assert_eq!(y, expected_y);
        assert_eq!(uv, expected_uv);
    }
}
