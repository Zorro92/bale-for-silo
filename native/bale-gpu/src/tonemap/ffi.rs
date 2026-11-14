//! Panic-contained ABI 1. A failure never publishes a partially constructed handle.
use super::types::TONEMAP_ABI_VERSION;
#[cfg(windows)]
use super::{
    gpu::ToneMapGpu,
    types::{BaleToneMapConfig, BaleToneMapFrameInfo},
};
#[cfg(any(windows, test))]
use std::panic::{AssertUnwindSafe, catch_unwind};

pub const S_OK: i32 = 0;
pub const E_INVALIDARG: i32 = super::cpu::E_INVALIDARG;
pub const E_UNEXPECTED: i32 = 0x8000ffffu32 as i32;

#[cfg(any(windows, test))]
fn boundary(f: impl FnOnce() -> i32) -> i32 {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or(E_UNEXPECTED)
}

#[unsafe(no_mangle)]
pub extern "C" fn bale_tonemap_abi_version() -> u32 {
    TONEMAP_ABI_VERSION
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

/// Queue HDR P010 -> SDR NV12 work, restoring the context's pipeline state. No flush.
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

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn version_and_panic_boundary() {
        assert_eq!(bale_tonemap_abi_version(), 1);
        assert_eq!(boundary(|| panic!("boundary test")), E_UNEXPECTED);
    }
}
