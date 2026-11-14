//! Idle-only driver cleanup. Registries contain weak references, never device-owning singletons.
//! The app stops middleware before calling trim; a lease's final drop also trims its device.

use std::ffi::c_void;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, Weak};

use windows::Win32::Foundation::{E_POINTER, E_UNEXPECTED};
use windows::Win32::Graphics::Direct3D10::ID3D10Multithread;
use windows::Win32::Graphics::Direct3D11::ID3D11Device;
use windows::Win32::Graphics::Dxgi::IDXGIDevice3;
use windows::Win32::Media::MediaFoundation::IMFDXGIDeviceManagerSource;
use windows::core::{IUnknown, Interface, Result};

static DEVICES: Mutex<Vec<Weak<Device>>> = Mutex::new(Vec::new());
static TRIMMED: AtomicU64 = AtomicU64::new(0);

/// Declare this after GPU resources so its final trim runs after their Release calls.
pub struct Device(pub(crate) ID3D11Device);

impl Device {
    pub(crate) fn track(device: &ID3D11Device) -> Arc<Self> {
        let tracked = Arc::new(Self(device.clone()));
        let mut devices = DEVICES.lock().unwrap_or_else(|e| e.into_inner());
        devices.retain(|entry| entry.strong_count() != 0);
        devices.push(Arc::downgrade(&tracked));
        tracked
    }

    fn trim(&self) -> Result<()> {
        trim_device(&self.0)
    }
}

fn trim_device(device: &ID3D11Device) -> Result<()> {
    // SAFETY: live interfaces. The app has stopped playback; multithread protection also
    // serializes with any last MF callback. No pipeline state is cleared during playback.
    unsafe {
        let context = device.GetImmediateContext()?;
        let multithread = context.cast::<ID3D10Multithread>()?;
        multithread.Enter();
        context.ClearState();
        context.Flush();
        let result = device.cast::<IDXGIDevice3>().map(|device| device.Trim());
        multithread.Leave();
        result?;
    }
    TRIMMED.fetch_add(1, Ordering::Relaxed);
    Ok(())
}

impl Drop for Device {
    fn drop(&mut self) {
        let _ = self.trim();
    }
}

/// Independent additive lifecycle ABI; the existing GPU, subtitle, grain and zero-copy ABIs stay put.
#[unsafe(no_mangle)]
pub extern "C" fn bale_gpu_lifecycle_abi_version() -> u32 {
    1
}

/// Driver trim count, including final device releases; diagnostic only, not memory accounting.
#[unsafe(no_mangle)]
pub extern "C" fn bale_gpu_trimmed_count() -> u64 {
    TRIMMED.load(Ordering::Relaxed)
}

/// Number of live registered D3D11 owners. The weak registry does not own their devices.
#[unsafe(no_mangle)]
pub extern "C" fn bale_gpu_device_count() -> u32 {
    DEVICES
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .iter()
        .filter(|entry| entry.strong_count() != 0)
        .count() as u32
}

/// Retires idle zero-copy runtimes and trims all remaining registered D3D11 devices.
/// The app must have stopped every playback/probe first. Never call on an active player.
#[unsafe(no_mangle)]
pub extern "C" fn bale_gpu_trim() -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        crate::d3d12_repack::release_idle();
        let devices: Vec<_> = DEVICES
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .iter()
            .filter_map(Weak::upgrade)
            .collect();
        let mut hr = 0;
        for device in devices {
            if let Err(error) = device.trim() {
                hr = error.code().0;
            }
        }
        hr
    }))
    .unwrap_or(E_UNEXPECTED.0)
}

/// Keeps the renderer's MSS device long enough to trim AFTER player/source/uploader Close.
/// This works for compressed system decoding too, without inspecting samples or changing decode.
/// # Safety
/// `source` must be a live MediaStreamSource COM pointer. `out` must be writable for one pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_gpu_media_device_hold(
    source: *mut c_void,
    out: *mut *mut Device,
) -> i32 {
    if out.is_null() {
        return E_POINTER.0;
    }
    // SAFETY: the caller guarantees writable output storage.
    unsafe { out.write(std::ptr::null_mut()) };
    let result = catch_unwind(AssertUnwindSafe(|| -> Result<()> {
        // SAFETY: source is live per the caller; cast takes its own reference.
        let source = unsafe { IUnknown::from_raw_borrowed(&source) }.ok_or(E_POINTER)?;
        let manager_source: IMFDXGIDeviceManagerSource = source.cast()?;
        // SAFETY: live manager. Close the temporary handle on success AND GetVideoService failure.
        let manager = unsafe { manager_source.GetManager()? };
        let handle = unsafe { manager.OpenDeviceHandle()? };
        let mut device = None::<ID3D11Device>;
        let result = unsafe {
            manager.GetVideoService(handle, &ID3D11Device::IID, &mut device as *mut _ as *mut _)
        };
        let _ = unsafe { manager.CloseDeviceHandle(handle) };
        result?;
        let device = device.ok_or(E_POINTER)?;
        // A private lease, destroyed after the middleware, not registered as a process singleton.
        unsafe { out.write(Box::into_raw(Box::new(Device(device)))) };
        Ok(())
    }));
    match result {
        Ok(Ok(())) => 0,
        Ok(Err(error)) => error.code().0,
        Err(_) => E_UNEXPECTED.0,
    }
}

/// Trims a borrowed D3D11 device (the short-lived C# decode probe).
/// # Safety
/// `device` must be a live ID3D11Device pointer, with no work running on it.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_gpu_trim_device(device: *mut c_void) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: caller guarantees a live borrowed interface; the clone is released at return.
        match unsafe { ID3D11Device::from_raw_borrowed(&device) } {
            Some(device) => trim_device(device)
                .map(|()| 0)
                .unwrap_or_else(|e| e.code().0),
            None => E_POINTER.0,
        }
    }))
    .unwrap_or(E_UNEXPECTED.0)
}

/// # Safety
/// Null or an unconsumed result of media_device_hold. Middleware must already be stopped.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_gpu_media_device_release(device: *mut Device) {
    if !device.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| {
            // SAFETY: exclusive ownership of this once-returned box returns to us.
            drop(unsafe { Box::from_raw(device) });
        }));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn lifecycle_abi_and_null_contracts() {
        assert_eq!(bale_gpu_lifecycle_abi_version(), 1);
        // SAFETY: null inputs are explicitly allowed and rejected without dereference.
        unsafe {
            assert_eq!(bale_gpu_trim_device(std::ptr::null_mut()), E_POINTER.0);
            assert_eq!(
                bale_gpu_media_device_hold(std::ptr::null_mut(), std::ptr::null_mut()),
                E_POINTER.0
            );
            let mut device = std::ptr::dangling_mut();
            assert_eq!(
                bale_gpu_media_device_hold(std::ptr::null_mut(), &mut device),
                E_POINTER.0
            );
            assert!(device.is_null());
            bale_gpu_media_device_release(device);
        }
    }
}
