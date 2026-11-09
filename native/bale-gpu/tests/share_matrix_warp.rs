//! Diagnostic only: which shared textures move between D3D12 and D3D11 on WARP. Prints a matrix
//! of formats, flags and HRESULTs (`--nocapture` in CI) and never fails on the results.
#![cfg(windows)]

use std::ffi::c_void;

use windows::Win32::Foundation::{CloseHandle, E_FAIL, HANDLE, HMODULE};
use windows::Win32::Graphics::Direct3D::{
    D3D_DRIVER_TYPE, D3D_DRIVER_TYPE_UNKNOWN, D3D_DRIVER_TYPE_WARP, D3D_FEATURE_LEVEL,
    D3D_FEATURE_LEVEL_11_0,
};
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Graphics::Direct3D12::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::Win32::Graphics::Dxgi::{
    CreateDXGIFactory1, DXGI_SHARED_RESOURCE_READ, DXGI_SHARED_RESOURCE_WRITE, IDXGIAdapter,
    IDXGIDevice, IDXGIFactory4, IDXGIResource1,
};
use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};
use windows::core::{GUID, HRESULT, Interface, PCWSTR, Result, s, w};

const GENERIC_ALL: u32 = 0x1000_0000;

type CreateDevice = unsafe extern "system" fn(
    *mut c_void,
    D3D_FEATURE_LEVEL,
    *const GUID,
    *mut *mut c_void,
) -> HRESULT;

struct Devices {
    d3d11: ID3D11Device,
    d3d11_1: ID3D11Device1,
    d3d12: ID3D12Device,
}

fn fails(step: &str, error: &windows::core::Error) -> String {
    format!("{step} FAILS 0x{:08x}", error.code().0 as u32)
}

fn devices(
    module: HMODULE,
    adapter: Option<&IDXGIAdapter>,
    driver: D3D_DRIVER_TYPE,
) -> Result<Devices> {
    let (mut device, mut context) = (None, None);
    // SAFETY: a live adapter or none, no software module; out-pointers are live local slots.
    unsafe {
        D3D11CreateDevice(
            adapter,
            driver,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_FLAG(0),
            Some(&[D3D_FEATURE_LEVEL_11_0]),
            D3D11_SDK_VERSION,
            Some(&mut device),
            None,
            Some(&mut context),
        )?
    };
    let d3d11: ID3D11Device = device.ok_or(E_FAIL)?;
    // SAFETY: live device; the adapter reference is ours.
    let adapter = unsafe { d3d11.cast::<IDXGIDevice>()?.GetAdapter()? };
    // SAFETY: a loaded d3d12.dll; the name is a static C string.
    let proc = unsafe { GetProcAddress(module, s!("D3D12CreateDevice")) }.ok_or(E_FAIL)?;
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
    // SAFETY: a successful create returned an owned reference.
    let d3d12 = unsafe { ID3D12Device::from_raw(raw) };
    Ok(Devices {
        d3d11_1: d3d11.cast()?,
        d3d11,
        d3d12,
    })
}

fn close(handle: HANDLE) {
    // SAFETY: an NT handle this test owns, closed once.
    let _ = unsafe { CloseHandle(handle) };
}

/// D3D12 creates a shared texture; D3D11 opens it.
fn d3d12_to_d3d11(devices: &Devices, format: DXGI_FORMAT, flags: D3D12_RESOURCE_FLAGS) -> String {
    let desc = D3D12_RESOURCE_DESC {
        Dimension: D3D12_RESOURCE_DIMENSION_TEXTURE2D,
        Alignment: 0,
        Width: 64,
        Height: 64,
        DepthOrArraySize: 1,
        MipLevels: 1,
        Format: format,
        SampleDesc: DXGI_SAMPLE_DESC {
            Count: 1,
            Quality: 0,
        },
        Layout: D3D12_TEXTURE_LAYOUT_UNKNOWN,
        Flags: flags,
    };
    let heap = D3D12_HEAP_PROPERTIES {
        Type: D3D12_HEAP_TYPE_DEFAULT,
        ..Default::default()
    };
    let mut texture: Option<ID3D12Resource> = None;
    // SAFETY: live device; descriptors outlive the call; `texture` is our slot.
    if let Err(error) = unsafe {
        devices.d3d12.CreateCommittedResource(
            &heap,
            D3D12_HEAP_FLAG_SHARED,
            &desc,
            D3D12_RESOURCE_STATE_COMMON,
            None,
            &mut texture,
        )
    } {
        return fails("create", &error);
    }
    let Some(texture) = texture else {
        return "create returned nothing".into();
    };
    // SAFETY: live device and texture; default security, unnamed.
    let handle = match unsafe {
        devices
            .d3d12
            .CreateSharedHandle(&texture, None, GENERIC_ALL, PCWSTR::null())
    } {
        Ok(handle) => handle,
        Err(error) => return fails("handle", &error),
    };
    // SAFETY: an NT handle from CreateSharedHandle, open for the call.
    let opened = unsafe {
        devices
            .d3d11_1
            .OpenSharedResource1::<ID3D11Texture2D>(handle)
    };
    close(handle);
    match opened {
        Ok(opened) => {
            let mut desc = D3D11_TEXTURE2D_DESC::default();
            // SAFETY: live texture; local out-slot.
            unsafe { opened.GetDesc(&mut desc) };
            format!("OK bind=0x{:x} misc=0x{:x}", desc.BindFlags, desc.MiscFlags)
        }
        Err(error) => fails("open", &error),
    }
}

/// D3D11 creates an NT-handle shared texture; D3D12 opens it.
fn d3d11_to_d3d12(devices: &Devices, format: DXGI_FORMAT, bind: u32) -> String {
    let desc = D3D11_TEXTURE2D_DESC {
        Width: 64,
        Height: 64,
        MipLevels: 1,
        ArraySize: 1,
        Format: format,
        SampleDesc: DXGI_SAMPLE_DESC {
            Count: 1,
            Quality: 0,
        },
        Usage: D3D11_USAGE_DEFAULT,
        BindFlags: bind,
        CPUAccessFlags: 0,
        MiscFlags: (D3D11_RESOURCE_MISC_SHARED_NTHANDLE.0 | D3D11_RESOURCE_MISC_SHARED.0) as u32,
    };
    let mut texture = None;
    // SAFETY: live device; no initial data; `texture` is our slot.
    if let Err(error) = unsafe {
        devices
            .d3d11
            .CreateTexture2D(&desc, None, Some(&mut texture))
    } {
        return fails("create", &error);
    }
    let Some(texture) = texture else {
        return "create returned nothing".into();
    };
    // SAFETY: live texture; default security, unnamed.
    let handle = match texture
        .cast::<IDXGIResource1>()
        .and_then(|resource| unsafe {
            resource.CreateSharedHandle(
                None,
                DXGI_SHARED_RESOURCE_READ.0 | DXGI_SHARED_RESOURCE_WRITE.0,
                PCWSTR::null(),
            )
        }) {
        Ok(handle) => handle,
        Err(error) => return fails("handle", &error),
    };
    let mut opened: Option<ID3D12Resource> = None;
    // SAFETY: an NT handle from CreateSharedHandle, open for the call; `opened` is our slot.
    let result = unsafe { devices.d3d12.OpenSharedHandle(handle, &mut opened) };
    close(handle);
    match (result, opened) {
        (Ok(()), Some(opened)) => {
            // SAFETY: live resource.
            let desc = unsafe { opened.GetDesc() };
            format!("OK d3d12 flags=0x{:x}", desc.Flags.0)
        }
        (Ok(()), None) => "open returned nothing".into(),
        (Err(error), _) => fails("open", &error),
    }
}

fn support(devices: &Devices, format: DXGI_FORMAT) -> String {
    // SAFETY: plain capability queries with matching, exactly sized structs.
    unsafe {
        let support1 = devices.d3d11.CheckFormatSupport(format).unwrap_or(0);
        let mut support2 = D3D11_FEATURE_DATA_FORMAT_SUPPORT2 {
            InFormat: format,
            OutFormatSupport2: 0,
        };
        let _ = devices.d3d11.CheckFeatureSupport(
            D3D11_FEATURE_FORMAT_SUPPORT2,
            (&mut support2 as *mut D3D11_FEATURE_DATA_FORMAT_SUPPORT2).cast(),
            size_of::<D3D11_FEATURE_DATA_FORMAT_SUPPORT2>() as u32,
        );
        let mut support12 = D3D12_FEATURE_DATA_FORMAT_SUPPORT {
            Format: format,
            ..Default::default()
        };
        let _ = devices.d3d12.CheckFeatureSupport(
            D3D12_FEATURE_FORMAT_SUPPORT,
            (&mut support12 as *mut D3D12_FEATURE_DATA_FORMAT_SUPPORT).cast(),
            size_of::<D3D12_FEATURE_DATA_FORMAT_SUPPORT>() as u32,
        );
        format!(
            "d3d11 support=0x{support1:x} support2=0x{:x} shareable={}; d3d12 support1=0x{:x} support2=0x{:x}",
            support2.OutFormatSupport2,
            support2.OutFormatSupport2 & D3D11_FORMAT_SUPPORT2_SHAREABLE.0 as u32 != 0,
            support12.Support1.0,
            support12.Support2.0
        )
    }
}

fn report(label: &str, devices: &Devices) -> String {
    let mut lines = vec![format!("share matrix ({label}):")];
    let mut options4 = D3D11_FEATURE_DATA_D3D11_OPTIONS4::default();
    // SAFETY: matching, exactly sized struct.
    let options4 = unsafe {
        devices.d3d11.CheckFeatureSupport(
            D3D11_FEATURE_D3D11_OPTIONS4,
            (&mut options4 as *mut D3D11_FEATURE_DATA_D3D11_OPTIONS4).cast(),
            size_of::<D3D11_FEATURE_DATA_D3D11_OPTIONS4>() as u32,
        )
    }
    .map(|()| options4.ExtendedNV12SharedTextureSupported.as_bool());
    lines.push(format!("  ExtendedNV12SharedTextureSupported {options4:?}"));
    let formats = [
        ("P010", DXGI_FORMAT_P010),
        ("NV12", DXGI_FORMAT_NV12),
        ("R16_UNORM", DXGI_FORMAT_R16_UNORM),
        ("RGBA8", DXGI_FORMAT_R8G8B8A8_UNORM),
    ];
    for (name, format) in formats {
        lines.push(format!("  {name:9} {}", support(devices, format)));
    }
    let uav = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
    let simultaneous = D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS;
    for (name, format) in formats {
        for (flags_name, flags) in [
            ("none", D3D12_RESOURCE_FLAG_NONE),
            ("uav", uav),
            ("uav+simultaneous", uav | simultaneous),
        ] {
            lines.push(format!(
                "  12->11 {name:9} {flags_name:16} {}",
                d3d12_to_d3d11(devices, format, flags)
            ));
        }
    }
    let srv = D3D11_BIND_SHADER_RESOURCE.0 as u32;
    for (name, format) in formats {
        for (bind_name, bind) in [
            ("srv", srv),
            ("srv+uav", srv | D3D11_BIND_UNORDERED_ACCESS.0 as u32),
            ("srv+rtv", srv | D3D11_BIND_RENDER_TARGET.0 as u32),
        ] {
            lines.push(format!(
                "  11->12 {name:9} {bind_name:16} {}",
                d3d11_to_d3d12(devices, format, bind)
            ));
        }
    }
    lines.join("\n")
}

#[test]
fn print_warp_share_matrix() {
    // SAFETY: loads a system DLL by name; it stays loaded for the test process.
    let module = unsafe { LoadLibraryW(w!("d3d12.dll")) }.expect("d3d12.dll ships with Windows");
    let driver_type = devices(module, None, D3D_DRIVER_TYPE_WARP);
    // SAFETY: plain factory creation and enumeration.
    let adapter =
        unsafe { CreateDXGIFactory1::<IDXGIFactory4>().and_then(|f| f.EnumWarpAdapter()) };
    let adapter = adapter
        .and_then(|adapter: IDXGIAdapter| devices(module, Some(&adapter), D3D_DRIVER_TYPE_UNKNOWN));
    for (label, devices) in [("DriverType", driver_type), ("Adapter", adapter)] {
        match devices {
            Ok(devices) => eprintln!("{}", report(label, &devices)),
            Err(error) => eprintln!("share matrix ({label}): {}", fails("devices", &error)),
        }
    }
}
