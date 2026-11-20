use super::{COPY_BYTES, MIB, Sample, Segment, d11::D11, d12::D12};
use std::panic::{AssertUnwindSafe, catch_unwind};
use windows::Win32::Foundation::{E_INVALIDARG, E_POINTER, E_UNEXPECTED, HMODULE};
use windows::Win32::Graphics::Dxgi::*;
use windows::core::{Interface, Result};

pub struct Probe {
    backend: Backend,
    adapter: IDXGIAdapter3,
    bytes: u64,
}
enum Backend {
    D11(D11),
    D12(D12),
}

fn segment(adapter: &IDXGIAdapter3, group: DXGI_MEMORY_SEGMENT_GROUP) -> Segment {
    let mut info = DXGI_QUERY_VIDEO_MEMORY_INFO::default();
    // SAFETY: the adapter belongs to our device; node zero is the default single-node adapter.
    let hr = unsafe { adapter.QueryVideoMemoryInfo(0, group, &mut info) }
        .err()
        .map_or(0, |e| e.code().0);
    Segment {
        budget: info.Budget,
        usage: info.CurrentUsage,
        available: info.AvailableForReservation,
        reservation: info.CurrentReservation,
        hr,
        reserved: 0,
    }
}

impl Probe {
    fn create(api: u32, module: HMODULE) -> Result<Self> {
        if !matches!(api, 11 | 12) || (api == 12 && module.is_invalid()) {
            return Err(E_INVALIDARG.into());
        }
        // Use the same default hardware adapter as the engine/churn, never WARP.
        let device = crate::tonemap::churn::create_device()?;
        let adapter = unsafe { device.cast::<IDXGIDevice>()?.GetAdapter()? };
        let backend = match api {
            11 => Backend::D11(D11::new(device)?),
            12 if !module.is_invalid() => {
                // SAFETY: caller keeps d3d12.dll loaded until destroy; fresh device on this adapter.
                let d12 = unsafe { crate::d3d12_repack::create_device(module, &adapter)? };
                drop(device);
                Backend::D12(D12::new(d12)?)
            }
            _ => return Err(E_INVALIDARG.into()),
        };
        Ok(Self {
            backend,
            adapter: adapter.cast()?,
            bytes: 0,
        })
    }

    fn allocate(&mut self, bytes: u64, sample: &mut Sample) -> Result<()> {
        sample.stage = 1;
        match &mut self.backend {
            Backend::D11(gpu) => gpu.allocate(bytes)?,
            Backend::D12(gpu) => gpu.allocate(bytes)?,
        }
        self.bytes += bytes;
        sample.stage = 2;
        // Every newly created resource is written and waited for, even the timing pair.
        match &mut self.backend {
            Backend::D11(gpu) => gpu.touch_last(),
            Backend::D12(gpu) => gpu.touch_last(),
        }
    }

    fn step(&mut self, bytes: u64, sample: &mut Sample) -> Result<()> {
        let baseline = self.bytes == 0;
        // The first call seeds the fixed copy pair; zero payload is allowed for that baseline row.
        while self.bytes < super::WORKING_BYTES {
            self.allocate(COPY_BYTES, sample)?;
        }
        let mut left = bytes;
        while left != 0 {
            let chunk = left.min(COPY_BYTES);
            self.allocate(chunk, sample)?;
            left -= chunk;
        }
        sample.stage = 3;
        sample.copy_ms = match &mut self.backend {
            Backend::D11(gpu) => gpu.copy_ms(baseline)?,
            Backend::D12(gpu) => gpu.copy_ms(baseline)?,
        };
        Ok(())
    }
}

/// Independent additive ABI, called only from DEBUG_SERVER. No ledger accounting.
#[unsafe(no_mangle)]
pub extern "C" fn bale_memprobe_abi_version() -> u32 {
    1
}

/// # Safety
/// `out` is writable for one pointer; for api=12 `module` is a loaded d3d12.dll kept until destroy.
/// Use the returned handle on one worker thread; destroy it exactly once, including after step failure.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_memprobe_create(
    api: u32,
    module: HMODULE,
    out: *mut *mut Probe,
) -> i32 {
    if out.is_null() {
        return E_POINTER.0;
    }
    unsafe { out.write(std::ptr::null_mut()) };
    match catch_unwind(AssertUnwindSafe(|| Probe::create(api, module))) {
        Ok(Ok(probe)) => {
            unsafe { out.write(Box::into_raw(Box::new(probe))) };
            0
        }
        Ok(Err(e)) => e.code().0,
        Err(_) => E_UNEXPECTED.0,
    }
}

/// Allocates and writes `bytes` (whole MiB, <=4096 MiB), then times one 64 MiB copy.
/// On failure, the snapshot still includes all successfully created textures and both DXGI groups.
/// # Safety
/// Exclusive live handle from create; `out` writable for one Sample. Never retry a failed step.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_memprobe_step(
    probe: *mut Probe,
    bytes: u64,
    out: *mut Sample,
) -> i32 {
    if probe.is_null() || out.is_null() {
        return E_POINTER.0;
    }
    if !bytes.is_multiple_of(MIB) || bytes > 4096 * MIB {
        return E_INVALIDARG.0;
    }
    let probe = unsafe { &mut *probe };
    let mut sample = Sample::default();
    let hr = match catch_unwind(AssertUnwindSafe(|| probe.step(bytes, &mut sample))) {
        Ok(Ok(())) => 0,
        Ok(Err(e)) => e.code().0,
        Err(_) => E_UNEXPECTED.0,
    };
    sample.bytes = probe.bytes;
    sample.local = segment(&probe.adapter, DXGI_MEMORY_SEGMENT_GROUP_LOCAL);
    sample.non_local = segment(&probe.adapter, DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL);
    sample.removed_hr = match &probe.backend {
        Backend::D11(gpu) => gpu.removed(),
        Backend::D12(gpu) => gpu.removed(),
    };
    unsafe { out.write(sample) };
    hr
}

/// # Safety
/// Null or an exclusively owned handle from create, after the final step has returned.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bale_memprobe_destroy(probe: *mut Probe) {
    if !probe.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| drop(unsafe { Box::from_raw(probe) })));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Exercise real commands on CI's software adapter, without touching a console or changing
    // the production path's hardware-only device selection.
    fn warp(api: u32) {
        use windows::Win32::Foundation::FreeLibrary;
        use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_UNKNOWN, D3D_FEATURE_LEVEL_11_0};
        use windows::Win32::Graphics::Direct3D11::*;
        use windows::Win32::System::LibraryLoader::LoadLibraryW;
        use windows::core::w;

        // SAFETY: standard WARP adapter, valid local outputs, no application device is used.
        unsafe {
            let factory: IDXGIFactory4 = CreateDXGIFactory1().unwrap();
            let adapter: IDXGIAdapter = factory.EnumWarpAdapter().unwrap();
            let module = LoadLibraryW(w!("d3d12.dll")).unwrap();
            let backend = if api == 11 {
                let mut device = None;
                D3D11CreateDevice(
                    Some(&adapter),
                    D3D_DRIVER_TYPE_UNKNOWN,
                    HMODULE::default(),
                    D3D11_CREATE_DEVICE_FLAG(0),
                    Some(&[D3D_FEATURE_LEVEL_11_0]),
                    D3D11_SDK_VERSION,
                    Some(&mut device),
                    None,
                    None,
                )
                .unwrap();
                Backend::D11(D11::new(device.unwrap()).unwrap())
            } else {
                Backend::D12(
                    D12::new(crate::d3d12_repack::create_device(module, &adapter).unwrap())
                        .unwrap(),
                )
            };
            let mut probe = Probe {
                backend,
                adapter: adapter.cast().unwrap(),
                bytes: 0,
            };
            let mut sample = Sample::default();
            probe.step(0, &mut sample).unwrap();
            assert_eq!(probe.bytes, super::super::WORKING_BYTES);
            assert!(sample.copy_ms.is_finite() && sample.copy_ms > 0.0);
            // Split a 100 MiB increment into 64+36 MiB, then reuse query/fence/command allocator.
            probe.step(100 * MIB, &mut sample).unwrap();
            assert_eq!(probe.bytes, 228 * MIB);
            assert!(sample.copy_ms.is_finite() && sample.copy_ms > 0.0);
            probe.step(MIB, &mut sample).unwrap();
            assert_eq!(probe.bytes, 229 * MIB);
            drop(probe);
            FreeLibrary(module).unwrap();
        }
    }

    #[test]
    fn d3d11_warp_writes_waits_and_timestamps_multiple_steps() {
        warp(11);
    }

    #[test]
    fn d3d12_warp_writes_waits_and_timestamps_multiple_steps() {
        warp(12);
    }

    #[test]
    fn invalid_abi_requests_do_not_create_a_device() {
        assert_eq!(bale_memprobe_abi_version(), 1);
        // SAFETY: null arguments are explicitly rejected, other outputs are valid local slots.
        unsafe {
            let mut handle = std::ptr::dangling_mut();
            assert_eq!(
                bale_memprobe_create(0, HMODULE::default(), &mut handle),
                E_INVALIDARG.0
            );
            assert!(handle.is_null());
            assert_eq!(
                bale_memprobe_create(12, HMODULE::default(), &mut handle),
                E_INVALIDARG.0
            );
            assert!(handle.is_null());
            assert_eq!(
                bale_memprobe_create(11, HMODULE::default(), std::ptr::null_mut()),
                E_POINTER.0
            );
            assert_eq!(
                bale_memprobe_step(std::ptr::null_mut(), 0, &mut Sample::default()),
                E_POINTER.0
            );
            bale_memprobe_destroy(std::ptr::null_mut());
        }
    }
}
