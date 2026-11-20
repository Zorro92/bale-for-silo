//! Debug-only opens-to-failure churn for the tone map (2026-11-17 crash work): each iteration
//! makes a fresh hardware D3D11 device like a player open, a 20-slice 4K P010 decoder-style
//! array, five NV12 outputs, creates the tone map, applies five frames, destroys everything.
//! No Media Foundation, no FFmpeg. Device lifetimes are counted by the lifecycle release marker.
use super::{ffi, types::*};
use windows::Win32::Foundation::{E_POINTER, HMODULE};
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_HARDWARE, D3D_FEATURE_LEVEL};
use windows::Win32::Graphics::Direct3D10::ID3D10Multithread;
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::core::{HRESULT, Interface, Result};

/// Skip bale_gpu entirely: devices and textures only (the control).
pub const CHURN_NO_TONEMAP: u32 = 1;

const WIDTH: u32 = 3840;
const HEIGHT: u32 = 2160;
const SURFACES: u32 = 20;
const OUTPUTS: usize = 5;

fn desc(height: u32, format: DXGI_FORMAT, array: u32, bind: u32) -> D3D11_TEXTURE2D_DESC {
    D3D11_TEXTURE2D_DESC {
        Width: WIDTH,
        Height: height,
        MipLevels: 1,
        ArraySize: array,
        Format: format,
        SampleDesc: DXGI_SAMPLE_DESC {
            Count: 1,
            Quality: 0,
        },
        Usage: D3D11_USAGE_DEFAULT,
        BindFlags: bind,
        CPUAccessFlags: 0,
        MiscFlags: 0,
    }
}

fn texture(device: &ID3D11Device, desc: &D3D11_TEXTURE2D_DESC) -> Result<ID3D11Texture2D> {
    let mut out = None;
    // SAFETY: live device, complete descriptor, no initial data.
    unsafe { device.CreateTexture2D(desc, None, Some(&mut out))? };
    let texture = out.ok_or(E_POINTER)?;
    crate::memledger::track_texture(&texture, crate::memledger::TONE_MAP)?;
    Ok(texture)
}

fn one(flags: u32) -> Result<()> {
    let mut device = None;
    // SAFETY: plain hardware device creation, as the engine session does per open.
    unsafe {
        D3D11CreateDevice(
            None,
            D3D_DRIVER_TYPE_HARDWARE,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            None,
            D3D11_SDK_VERSION,
            Some(&mut device),
            Some(&mut D3D_FEATURE_LEVEL::default()),
            None,
        )?
    };
    let device = device.ok_or(E_POINTER)?;
    crate::lifecycle::watch_release(&device);
    let multithread: ID3D10Multithread = device.cast()?;
    // SAFETY: live interfaces; this thread is the only user of the fresh device.
    let context = unsafe {
        let _ = multithread.SetMultithreadProtected(true);
        device.GetImmediateContext()?
    };
    let input = texture(
        &device,
        &desc(
            2176,
            DXGI_FORMAT_P010,
            SURFACES,
            D3D11_BIND_DECODER.0 as u32,
        ),
    )?;
    let outputs = (0..OUTPUTS)
        .map(|_| {
            texture(
                &device,
                &desc(
                    HEIGHT,
                    DXGI_FORMAT_NV12,
                    1,
                    (D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE).0 as u32,
                ),
            )
        })
        .collect::<Result<Vec<_>>>()?;
    let mut result = Ok(());
    if flags & CHURN_NO_TONEMAP == 0 {
        let config = BaleToneMapConfig {
            width: WIDTH,
            height: HEIGHT,
            curve: 1,
            source_peak_nits: 1000.0,
            target_white_nits: 100.0,
            ..Default::default()
        };
        let frame = BaleToneMapFrameInfo {
            transfer: 16,
            primaries: 9,
            matrix: 9,
            ..Default::default()
        };
        let mut handle = std::ptr::null_mut();
        // SAFETY: the same calls the fork makes, under multithread protection, on one thread.
        unsafe {
            multithread.Enter();
            let hr = ffi::bale_tonemap_create(device.as_raw(), &config, &mut handle);
            if hr < 0 {
                result = Err(HRESULT(hr).into());
            } else {
                for (index, output) in outputs.iter().enumerate() {
                    let hr = ffi::bale_tonemap_apply(
                        handle,
                        context.as_raw(),
                        input.as_raw(),
                        index as u32,
                        output.as_raw(),
                        &frame,
                    );
                    context.Flush();
                    if hr < 0 {
                        result = Err(HRESULT(hr).into());
                        break;
                    }
                }
            }
            ffi::bale_tonemap_destroy(handle);
            multithread.Leave();
        }
    }
    // SAFETY: live context; nothing else uses this device.
    unsafe {
        context.ClearState();
        context.Flush();
    }
    drop(outputs);
    drop(input);
    drop(context);
    drop(multithread);
    drop(device);
    result
}

/// Runs `iterations` device + tone-map lifetimes; stops at the first failure.
/// # Safety
/// `completed` is null or writable for one u32. Debug use only, never during playback.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_tonemap_debug_churn(
    iterations: u32,
    flags: u32,
    completed: *mut u32,
) -> i32 {
    let mut done = 0u32;
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        for _ in 0..iterations {
            one(flags)?;
            done += 1;
        }
        Ok::<(), windows::core::Error>(())
    }));
    // SAFETY: caller guarantees writable storage when non-null.
    if let Some(out) = unsafe { completed.as_mut() } {
        *out = done;
    }
    match result {
        Ok(Ok(())) => 0,
        Ok(Err(error)) => error.code().0,
        Err(_) => windows::Win32::Foundation::E_UNEXPECTED.0,
    }
}
