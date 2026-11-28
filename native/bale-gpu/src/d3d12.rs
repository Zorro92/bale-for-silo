//! D3D12 helpers for the debug memory probe (`memprobe`, api 12).
//!
//! `d3d12.dll` is never imported: the caller registers the loaded module and its
//! `D3D12CreateDevice` is called through [`CreateDevice`], so a console without it cannot stop
//! `bale_gpu.dll` from loading.

use std::ffi::c_void;

use windows::Win32::Foundation::{E_FAIL, E_NOINTERFACE, HMODULE};
use windows::Win32::Graphics::Direct3D::{D3D_FEATURE_LEVEL, D3D_FEATURE_LEVEL_11_0};
use windows::Win32::Graphics::Direct3D12::*;
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT, DXGI_SAMPLE_DESC};
use windows::Win32::Graphics::Dxgi::IDXGIAdapter;
use windows::Win32::System::LibraryLoader::GetProcAddress;
use windows::core::{GUID, HRESULT, Interface, Result, s};

/// `D3D12CreateDevice`'s signature.
pub(crate) type CreateDevice = unsafe extern "system" fn(
    *mut c_void,
    D3D_FEATURE_LEVEL,
    *const GUID,
    *mut *mut c_void,
) -> HRESULT;

/// A row-major, single-sample resource description.
pub(crate) fn resource(
    dimension: D3D12_RESOURCE_DIMENSION,
    width: u64,
    height: u32,
    format: DXGI_FORMAT,
    flags: D3D12_RESOURCE_FLAGS,
) -> D3D12_RESOURCE_DESC {
    D3D12_RESOURCE_DESC {
        Dimension: dimension,
        Alignment: 0,
        Width: width,
        Height: height,
        DepthOrArraySize: 1,
        MipLevels: 1,
        Format: format,
        SampleDesc: DXGI_SAMPLE_DESC {
            Count: 1,
            Quality: 0,
        },
        Layout: D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
        Flags: flags,
    }
}

/// Creates a FL11_0 D3D12 device on the D3D11 device's adapter.
///
/// # Safety
/// `module` must be a loaded `d3d12.dll`.
pub(crate) unsafe fn create_device(
    module: HMODULE,
    adapter: &IDXGIAdapter,
) -> Result<ID3D12Device> {
    // SAFETY: the caller guarantees a loaded module; the name is a static C string.
    let proc = unsafe { GetProcAddress(module, s!("D3D12CreateDevice")) }.ok_or(E_NOINTERFACE)?;
    // SAFETY: D3D12CreateDevice's documented signature; both are plain `system` fn pointers.
    let create: CreateDevice = unsafe { std::mem::transmute(proc) };
    let mut raw = std::ptr::null_mut();
    // SAFETY: a live adapter borrowed for the call; `raw` is a live out-slot.
    unsafe {
        create(
            adapter.as_raw(),
            D3D_FEATURE_LEVEL_11_0,
            &ID3D12Device::IID,
            &mut raw,
        )
        .ok()?
    };
    if raw.is_null() {
        return Err(E_FAIL.into());
    }
    // SAFETY: a successful create returned an owned ID3D12Device reference.
    Ok(unsafe { ID3D12Device::from_raw(raw) })
}
