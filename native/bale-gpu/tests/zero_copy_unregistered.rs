//! Zero-copy create before `bale_zc_register_d3d12` (its own test process: registration is global).
#![cfg(windows)]

use std::ptr;

use bale_gpu::d3d12_repack::bale_zc_create;
use windows::Win32::Foundation::{E_INVALIDARG, E_NOINTERFACE, HMODULE};
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_WARP, D3D_FEATURE_LEVEL_11_0};
use windows::Win32::Graphics::Direct3D11::*;
use windows::core::Interface;

#[test]
fn create_needs_a_registered_d3d12_module() {
    let mut device = None;
    let mut context = None;
    // SAFETY: no adapter/software module; out-pointers are live local COM slots.
    unsafe {
        D3D11CreateDevice(
            None,
            D3D_DRIVER_TYPE_WARP,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_FLAG(0),
            Some(&[D3D_FEATURE_LEVEL_11_0]),
            D3D11_SDK_VERSION,
            Some(&mut device),
            None,
            Some(&mut context),
        )
        .expect("WARP FL11_0 is required; zero-copy tests must not skip");
    }
    let (device, context) = (device.expect("device"), context.expect("context"));
    let mut handle = ptr::dangling_mut();
    // SAFETY: live COM interfaces and a writable out slot.
    let hr = unsafe { bale_zc_create(device.as_raw(), context.as_raw(), &mut handle) };
    assert_eq!(hr, E_NOINTERFACE.0);
    assert!(handle.is_null(), "failure clears the output");
    // SAFETY: null interfaces are the rejected input.
    let hr = unsafe { bale_zc_create(ptr::null_mut(), context.as_raw(), &mut handle) };
    assert_eq!(hr, E_INVALIDARG.0);
    // SAFETY: a null output is the rejected input.
    let hr = unsafe { bale_zc_create(device.as_raw(), context.as_raw(), ptr::null_mut()) };
    assert_eq!(hr, E_INVALIDARG.0);
}
