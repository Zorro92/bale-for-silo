//! Bale's native GPU/media layer, called from the C# Xbox app over a narrow C ABI.
//!
//! Step 1 is only a loader check: the DLL must load inside the Xbox UWP app container, Rust std
//! (threads, synchronisation, clocks) must work there, and the `windows` crate's D3D11 bindings must
//! reach the hardware device. Media Engine and compute work build on this once it is proven.

use std::sync::{Arc, Mutex};
use std::time::Instant;

use windows::Win32::Foundation::HMODULE;
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_HARDWARE, D3D_FEATURE_LEVEL};
use windows::Win32::Graphics::Direct3D11::{
    D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_CREATE_DEVICE_VIDEO_SUPPORT, D3D11_SDK_VERSION, D3D11CreateDevice,
    ID3D11Device,
};

/// Bumped whenever an export's signature or meaning changes; C# refuses a mismatched DLL.
pub const ABI_VERSION: u32 = 1;

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
        std::thread::Builder::new().name("bale-gpu-probe".into()).spawn(move || {
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
