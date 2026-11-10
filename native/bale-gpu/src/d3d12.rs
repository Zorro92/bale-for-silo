//! D3D12 helpers shared by the zero-copy repack ([`crate::d3d12_repack`]).
//!
//! `d3d12.dll` is never imported: the caller registers the loaded module and its
//! `D3D12CreateDevice` is called through [`CreateDevice`], so a console without it cannot stop
//! `bale_gpu.dll` from loading.

use std::ffi::c_void;

use windows::Win32::Graphics::Direct3D::D3D_FEATURE_LEVEL;
use windows::Win32::Graphics::Direct3D12::*;
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT, DXGI_SAMPLE_DESC};
use windows::core::{GUID, HRESULT};

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
