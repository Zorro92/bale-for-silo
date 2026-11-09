//! Debug-only D3D12 availability probe.
//!
//! Answers whether an Xbox app (not a game) gets a hardware D3D12 device, and whether D3D12 can wrap
//! ordinary CPU memory as a GPU heap (`OpenExistingHeapFromAddress`). That second part is the
//! zero-copy route for software-decoded frames: dav1d would decode into memory the GPU reads
//! directly, so no per-frame copy into a STAGING texture is needed.
//!
//! `d3d12.dll` is never imported: the caller loads it and passes the module, so a console without it
//! cannot stop `bale_gpu.dll` from loading. Nothing here is used by playback.

use std::ffi::c_void;
use std::fmt::Write as _;

use windows::Win32::Foundation::HMODULE;
use windows::Win32::Graphics::Direct3D::*;
use windows::Win32::Graphics::Direct3D12::*;
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_FORMAT, DXGI_FORMAT_R16_UNORM, DXGI_FORMAT_UNKNOWN, DXGI_SAMPLE_DESC,
};
use windows::Win32::System::LibraryLoader::GetProcAddress;
use windows::Win32::System::Memory::{
    MEM_COMMIT, MEM_RELEASE, MEM_RESERVE, PAGE_READWRITE, VirtualAlloc, VirtualFree,
};
use windows::core::{GUID, HRESULT, Interface, s};

pub(crate) type CreateDevice = unsafe extern "system" fn(
    *mut c_void,
    D3D_FEATURE_LEVEL,
    *const GUID,
    *mut *mut c_void,
) -> HRESULT;

/// Wrapped CPU memory: one 4K 10-bit frame as R16 (3840 x 3240 x 2 = 24.9 MB) fits, 64 KB aligned.
const HEAP_BYTES: usize = 32 << 20;
const FRAME_WIDTH: u64 = 3840;
const FRAME_ROWS: u32 = 3240;

pub(crate) fn fails(error: &windows::core::Error) -> String {
    format!("FAILS 0x{:08X}", error.code().0 as u32)
}

fn level(value: D3D_FEATURE_LEVEL) -> String {
    format!("{}_{}", (value.0 >> 12) & 0xF, (value.0 >> 8) & 0xF)
}

/// Creates a hardware FL11_0 device through `module`'s `D3D12CreateDevice`, or returns the report
/// line (label and result, no indent) that says why not.
///
/// # Safety
/// `module` must be null or a module handle that stays loaded for the call.
pub(crate) unsafe fn create_device(module: HMODULE) -> Result<ID3D12Device, String> {
    if module.0.is_null() {
        return Err("d3d12.dll                not loadable".to_owned());
    }
    // SAFETY: the caller guarantees a loaded module; the name is a static C string.
    let Some(proc) = (unsafe { GetProcAddress(module, s!("D3D12CreateDevice")) }) else {
        return Err("D3D12CreateDevice        not exported".to_owned());
    };
    // SAFETY: D3D12CreateDevice's documented signature; both are plain `system` fn pointers.
    let create: CreateDevice = unsafe { std::mem::transmute(proc) };
    let mut raw = std::ptr::null_mut();
    // SAFETY: null adapter means the default hardware adapter; `raw` is a live out-slot.
    let created = unsafe {
        create(
            std::ptr::null_mut(),
            D3D_FEATURE_LEVEL_11_0,
            &ID3D12Device::IID,
            &mut raw,
        )
    };
    if let Err(error) = created.ok() {
        return Err(format!("hardware device FL11_0   {}", fails(&error)));
    }
    if raw.is_null() {
        return Err("hardware device FL11_0   FAILS (no device)".to_owned());
    }
    // SAFETY: a successful create returned an owned ID3D12Device reference.
    Ok(unsafe { ID3D12Device::from_raw(raw) })
}

/// Runs the probe with `module` (a loaded `d3d12.dll`, or null when it would not load) and returns
/// the plain-text report. Errors are HRESULTs only.
///
/// # Safety
/// `module` must be null or a module handle that stays loaded for the call.
pub unsafe fn run(module: HMODULE) -> String {
    let mut report = String::from("D3D12 probe:\n");
    // SAFETY: forwarded caller guarantee.
    let device = match unsafe { create_device(module) } {
        Ok(device) => device,
        Err(line) => {
            let _ = writeln!(report, "  {line}");
            return report;
        }
    };
    report.push_str("  hardware device FL11_0   OK\n");

    let requested = [
        D3D_FEATURE_LEVEL_12_1,
        D3D_FEATURE_LEVEL_12_0,
        D3D_FEATURE_LEVEL_11_1,
        D3D_FEATURE_LEVEL_11_0,
    ];
    let mut levels = D3D12_FEATURE_DATA_FEATURE_LEVELS {
        NumFeatureLevels: requested.len() as u32,
        pFeatureLevelsRequested: requested.as_ptr(),
        MaxSupportedFeatureLevel: D3D_FEATURE_LEVEL(0),
    };
    let line = feature(&device, D3D12_FEATURE_FEATURE_LEVELS, &mut levels)
        .map(|()| level(levels.MaxSupportedFeatureLevel));
    let _ = writeln!(
        report,
        "  max feature level        {}",
        line.unwrap_or_else(|e| fails(&e))
    );

    let mut architecture = D3D12_FEATURE_DATA_ARCHITECTURE::default();
    let line = feature(&device, D3D12_FEATURE_ARCHITECTURE, &mut architecture).map(|()| {
        format!(
            "UMA {} cache-coherent UMA {}",
            architecture.UMA.as_bool(),
            architecture.CacheCoherentUMA.as_bool()
        )
    });
    let _ = writeln!(
        report,
        "  architecture             {}",
        line.unwrap_or_else(|e| fails(&e))
    );

    let mut options = D3D12_FEATURE_DATA_D3D12_OPTIONS::default();
    let line = feature(&device, D3D12_FEATURE_D3D12_OPTIONS, &mut options)
        .map(|()| format!("{}", options.CrossAdapterRowMajorTextureSupported.as_bool()));
    let _ = writeln!(
        report,
        "  row-major cross-adapter  {}",
        line.unwrap_or_else(|e| fails(&e))
    );

    let mut existing = D3D12_FEATURE_DATA_EXISTING_HEAPS::default();
    let line = feature(&device, D3D12_FEATURE_EXISTING_HEAPS, &mut existing)
        .map(|()| format!("{}", existing.Supported.as_bool()));
    let _ = writeln!(
        report,
        "  existing heaps           {}",
        line.unwrap_or_else(|e| fails(&e))
    );

    // SAFETY: owned device; the heap tests release everything before returning.
    unsafe { wrap_memory(&device, &mut report) };
    report
}

fn feature<T>(
    device: &ID3D12Device,
    kind: D3D12_FEATURE,
    data: &mut T,
) -> windows::core::Result<()> {
    // SAFETY: `data` is the matching D3D12_FEATURE_DATA_* struct for `kind`, sized exactly.
    unsafe {
        device.CheckFeatureSupport(
            kind,
            (data as *mut T).cast(),
            std::mem::size_of::<T>() as u32,
        )
    }
}

/// Wraps a fresh VirtualAlloc region as a D3D12 heap, then tries a placed buffer and a placed
/// row-major R16 4K texture in it (the texture would let shaders read dav1d's output in place).
///
/// # Safety
/// `device` must be a live hardware device.
unsafe fn wrap_memory(device: &ID3D12Device, report: &mut String) {
    let device3 = match device.cast::<ID3D12Device3>() {
        Ok(device3) => device3,
        Err(error) => {
            let _ = writeln!(report, "  ID3D12Device3            {}", fails(&error));
            return;
        }
    };
    // SAFETY: a fresh private allocation; released below after every D3D12 object on it is gone.
    let memory =
        unsafe { VirtualAlloc(None, HEAP_BYTES, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE) };
    if memory.is_null() {
        report.push_str("  VirtualAlloc 32 MB       FAILS\n");
        return;
    }
    {
        // SAFETY: `memory` is a committed VirtualAlloc region, 64 KB aligned, alive until freed below.
        let heap = match unsafe { device3.OpenExistingHeapFromAddress::<ID3D12Heap>(memory) } {
            Ok(heap) => heap,
            Err(error) => {
                let _ = writeln!(report, "  heap from address        {}", fails(&error));
                // SAFETY: nothing references the region.
                let _ = unsafe { VirtualFree(memory, 0, MEM_RELEASE) };
                return;
            }
        };
        // SAFETY: live heap.
        let desc = unsafe { heap.GetDesc() };
        let _ = writeln!(
            report,
            "  heap from address        OK {:.1} MB type {} cpu page {} pool {} flags 0x{:X}",
            desc.SizeInBytes as f64 / 1e6,
            desc.Properties.Type.0,
            desc.Properties.CPUPageProperty.0,
            desc.Properties.MemoryPoolPreference.0,
            desc.Flags.0,
        );
        let cross = if desc.Flags.0 & D3D12_HEAP_FLAG_SHARED_CROSS_ADAPTER.0 != 0 {
            D3D12_RESOURCE_FLAG_ALLOW_CROSS_ADAPTER
        } else {
            D3D12_RESOURCE_FLAG_NONE
        };
        let buffer = resource(
            D3D12_RESOURCE_DIMENSION_BUFFER,
            HEAP_BYTES as u64,
            1,
            DXGI_FORMAT_UNKNOWN,
            cross,
        );
        let texture = resource(
            D3D12_RESOURCE_DIMENSION_TEXTURE2D,
            FRAME_WIDTH,
            FRAME_ROWS,
            DXGI_FORMAT_R16_UNORM,
            cross,
        );
        for (label, desc) in [
            ("placed buffer", buffer),
            ("placed R16 4K row-major", texture),
        ] {
            let mut placed: Option<ID3D12Resource> = None;
            // SAFETY: live heap of HEAP_BYTES; both descriptions fit at offset 0; `placed` is our slot.
            let result = unsafe {
                device.CreatePlacedResource(
                    &heap,
                    0,
                    &desc,
                    D3D12_RESOURCE_STATE_COMMON,
                    None,
                    &mut placed,
                )
            };
            let line = match (result, placed) {
                (Ok(()), Some(_)) => "OK".to_owned(),
                (Ok(()), None) => "FAILS (no resource)".to_owned(),
                (Err(error), _) => fails(&error),
            };
            let _ = writeln!(report, "  {label:<24} {line}");
        }
    }
    // SAFETY: the heap and every placed resource were released when the block above ended.
    let _ = unsafe { VirtualFree(memory, 0, MEM_RELEASE) };
}

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
