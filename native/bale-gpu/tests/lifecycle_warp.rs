//! Renderer device lease and driver trim through the C ABI, on WARP; no feature skips.
#![cfg(windows)]

use std::ptr;

use bale_gpu::lifecycle::{
    bale_gpu_media_device_hold, bale_gpu_media_device_release, bale_gpu_trim, bale_gpu_trim_device,
    bale_gpu_trimmed_count,
};
use windows::Win32::Foundation::HMODULE;
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_WARP, D3D_FEATURE_LEVEL_11_0};
use windows::Win32::Graphics::Direct3D10::ID3D10Multithread;
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Media::MediaFoundation::{
    IMFDXGIDeviceManager, IMFDXGIDeviceManagerSource, IMFDXGIDeviceManagerSource_Impl,
    MFCreateDXGIDeviceManager,
};
use windows::core::{Interface, implement};

const S_OK: i32 = 0;

/// A WARP device with multithread protection, as the app's renderer device has.
fn warp_device() -> ID3D11Device {
    let mut device = None;
    let mut context = None;
    // SAFETY: no adapter and no software module; out-pointers are live local slots.
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
    }
    .expect("WARP FL11_0 is required; lifecycle tests must not skip");
    let device = device.expect("WARP device");
    let context: ID3D11DeviceContext = context.expect("WARP immediate context");
    let multithread: ID3D10Multithread =
        context.cast().expect("WARP must expose ID3D10Multithread");
    // SAFETY: configure the newly created private context before any other use.
    let _ = unsafe { multithread.SetMultithreadProtected(true) };
    device
}

// Same manager-source QI contract MSS exposes, without opening real media in the test.
#[implement(IMFDXGIDeviceManagerSource)]
struct ManagerSource(IMFDXGIDeviceManager);

impl IMFDXGIDeviceManagerSource_Impl for ManagerSource_Impl {
    fn GetManager(&self) -> windows::core::Result<IMFDXGIDeviceManager> {
        Ok(self.0.clone())
    }
}

#[test]
fn renderer_device_lease_survives_manager_close_and_trims_on_release() {
    // This exercises manager/device ownership, not video decoding. Hosted runners' WARP
    // refuses VIDEO_SUPPORT at FL11_0; the ordinary raster device is sufficient for GetVideoService.
    let device = warp_device();
    let mut token = 0;
    let mut manager = None;
    // SAFETY: writable locals, then live device and manager. No media has been opened.
    unsafe { MFCreateDXGIDeviceManager(&mut token, &mut manager) }.expect("device manager");
    let manager = manager.expect("manager");
    unsafe { manager.ResetDevice(&device, token) }.expect("set renderer device");
    let source: IMFDXGIDeviceManagerSource = ManagerSource(manager.clone()).into();
    let before = bale_gpu_trimmed_count();
    let mut held = ptr::null_mut();
    // SAFETY: live COM source, writable output pointer.
    assert_eq!(
        unsafe { bale_gpu_media_device_hold(source.as_raw(), &mut held) },
        S_OK
    );
    assert!(!held.is_null());
    assert_eq!(bale_gpu_trimmed_count(), before); // Merely holding must never trim playback.
    drop(source);
    drop(manager);
    // SAFETY: lease is still live and consumed exactly once after middleware Close.
    unsafe { bale_gpu_media_device_release(held) };
    assert_eq!(bale_gpu_trimmed_count(), before + 1);
    assert_eq!(unsafe { bale_gpu_trim_device(device.as_raw()) }, S_OK);
    assert_eq!(bale_gpu_trimmed_count(), before + 2);
    // No device is registered in this process, so the app's background trim is a no-op.
    assert_eq!(bale_gpu_trim(), S_OK);
    assert_eq!(bale_gpu_trimmed_count(), before + 2);
}
