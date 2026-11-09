//! Zero-copy repack (D3D12 compute into shared P010 landing textures) on WARP, through the C ABI;
//! no feature skips.
#![cfg(windows)]

use std::ffi::c_void;
use std::ptr;

use bale_gpu::d3d12_repack::{
    BaleZcStats, BaleZeroCopy, S_FALSE, S_OK, ZERO_COPY_ABI_VERSION, bale_zc_abi_version,
    bale_zc_alloc, bale_zc_create, bale_zc_destroy, bale_zc_register_d3d12, bale_zc_release,
    bale_zc_repack, bale_zc_repack_into, bale_zc_stats, create_with_landing_copies,
    diagnose_create,
};
use bale_gpu::grain::BaleGrainParams;
use bale_gpu::zc_frame::{BaleZcFrame, MAX_BYTES, fill_test_planes};
use windows::Win32::Foundation::{E_INVALIDARG, E_OUTOFMEMORY, HMODULE};
use windows::Win32::Graphics::Direct3D::{
    D3D_DRIVER_TYPE_UNKNOWN, D3D_DRIVER_TYPE_WARP, D3D_FEATURE_LEVEL_11_0,
};
use windows::Win32::Graphics::Direct3D10::ID3D10Multithread;
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::Win32::Graphics::Dxgi::{CreateDXGIFactory1, IDXGIAdapter, IDXGIFactory4};
use windows::Win32::System::LibraryLoader::LoadLibraryW;
use windows::core::{Interface, w};

/// Buffers per handle (`MAX_BUFFERS` in the module).
const MAX_BUFFERS: usize = 16;

struct Warp {
    device: ID3D11Device,
    context: ID3D11DeviceContext,
}

/// How the test's D3D11 device reaches WARP.
#[derive(Clone, Copy, Debug)]
enum Kind {
    /// `D3D_DRIVER_TYPE_WARP`: D3D11's in-process software rasterizer.
    DriverType,
    /// `D3D_DRIVER_TYPE_UNKNOWN` on DXGI's WARP adapter (Microsoft Basic Render Driver).
    Adapter,
}

impl Warp {
    fn new() -> Self {
        Self::with(Kind::DriverType)
            .expect("WARP FL11_0 is required; zero-copy tests must not skip")
    }

    fn with(kind: Kind) -> windows::core::Result<Self> {
        // SAFETY: loads a system DLL by name; it stays loaded for the test process.
        let module =
            unsafe { LoadLibraryW(w!("d3d12.dll")) }.expect("d3d12.dll ships with Windows");
        // SAFETY: a module that is never freed.
        assert_eq!(unsafe { bale_zc_register_d3d12(module.0) }, S_OK);
        let adapter: Option<IDXGIAdapter> = match kind {
            Kind::DriverType => None,
            // SAFETY: plain factory creation and enumeration.
            Kind::Adapter => {
                Some(unsafe { CreateDXGIFactory1::<IDXGIFactory4>()?.EnumWarpAdapter()? })
            }
        };
        let driver = match kind {
            Kind::DriverType => D3D_DRIVER_TYPE_WARP,
            Kind::Adapter => D3D_DRIVER_TYPE_UNKNOWN,
        };
        let mut device = None;
        let mut context = None;
        // SAFETY: a live adapter or none, no software module; out-pointers are live local slots.
        unsafe {
            D3D11CreateDevice(
                adapter.as_ref(),
                driver,
                HMODULE::default(),
                D3D11_CREATE_DEVICE_FLAG(0),
                Some(&[D3D_FEATURE_LEVEL_11_0]),
                D3D11_SDK_VERSION,
                Some(&mut device),
                None,
                Some(&mut context),
            )?;
        }
        let device = device.expect("WARP device");
        let context = context.expect("WARP immediate context");
        let multithread: ID3D10Multithread =
            context.cast().expect("WARP must expose ID3D10Multithread");
        // SAFETY: configure the newly created private context before any other use.
        let _ = unsafe { multithread.SetMultithreadProtected(true) };
        Ok(Self { device, context })
    }

    /// The failing create step on this device, `Ok` when create succeeds.
    fn diagnose(&self) -> Result<(), String> {
        // SAFETY: live COM interfaces.
        unsafe { diagnose_create(self.device.as_raw(), self.context.as_raw()) }
    }

    fn zero_copy(&self) -> ZeroCopy {
        let mut handle = ptr::null_mut();
        // SAFETY: live COM interfaces and a writable out slot.
        let hr =
            unsafe { bale_zc_create(self.device.as_raw(), self.context.as_raw(), &mut handle) };
        if hr != S_OK {
            let report: Vec<String> = [Kind::DriverType, Kind::Adapter]
                .into_iter()
                .map(|kind| match Warp::with(kind) {
                    Ok(warp) => format!("{kind:?}: {:?}", warp.diagnose()),
                    Err(error) => format!("{kind:?}: no D3D11 device ({error})"),
                })
                .collect();
            panic!(
                "zero-copy create (including its readback self-test) must succeed on WARP: \
                 HRESULT=0x{:08x}; {}",
                hr as u32,
                report.join("; ")
            );
        }
        assert!(!handle.is_null());
        ZeroCopy(handle)
    }

    /// A handle that always repacks into a private texture and copies it to the shared one.
    fn zero_copy_with_landing_copies(&self) -> ZeroCopy {
        let mut handle = ptr::null_mut();
        // SAFETY: live COM interfaces and a writable out slot.
        let hr = unsafe {
            create_with_landing_copies(self.device.as_raw(), self.context.as_raw(), &mut handle)
        };
        assert_eq!(hr, S_OK, "HRESULT=0x{:08x}", hr as u32);
        assert!(!handle.is_null());
        ZeroCopy(handle)
    }

    /// A P010 texture like MF's shareable pool textures (render target, shader resource, UAV,
    /// NT shared handle); without the render-target bind where WARP refuses it.
    fn target(&self, width: u32, height: u32) -> ID3D11Texture2D {
        let bind = D3D11_BIND_RENDER_TARGET.0
            | D3D11_BIND_SHADER_RESOURCE.0
            | D3D11_BIND_UNORDERED_ACCESS.0;
        let misc = D3D11_RESOURCE_MISC_SHARED_NTHANDLE.0 | D3D11_RESOURCE_MISC_SHARED.0;
        self.texture(width, height, DXGI_FORMAT_P010, bind as u32, misc as u32)
            .or_else(|_| {
                let bind = D3D11_BIND_SHADER_RESOURCE.0 | D3D11_BIND_UNORDERED_ACCESS.0;
                self.texture(width, height, DXGI_FORMAT_P010, bind as u32, misc as u32)
            })
            .expect("shared UAV P010 texture")
    }

    fn texture(
        &self,
        width: u32,
        height: u32,
        format: DXGI_FORMAT,
        bind: u32,
        misc: u32,
    ) -> windows::core::Result<ID3D11Texture2D> {
        let desc = D3D11_TEXTURE2D_DESC {
            Width: width,
            Height: height,
            MipLevels: 1,
            ArraySize: 1,
            Format: format,
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            Usage: D3D11_USAGE_DEFAULT,
            BindFlags: bind,
            MiscFlags: misc,
            ..Default::default()
        };
        let mut texture = None;
        // SAFETY: a complete description, no initial data, local out-slot.
        unsafe {
            self.device
                .CreateTexture2D(&desc, None, Some(&mut texture))?
        };
        Ok(texture.expect("texture"))
    }

    /// Copies a landing texture to a staging texture and returns its tight P010 bytes.
    fn read(&self, landing: *mut c_void, width: u32, height: u32) -> Vec<u8> {
        // SAFETY: a borrowed texture pointer from bale_zc_repack, valid until the next repack.
        let landing = unsafe { ID3D11Texture2D::from_raw_borrowed(&landing) }.expect("texture");
        let mut desc = D3D11_TEXTURE2D_DESC::default();
        // SAFETY: live texture; local out-slot.
        unsafe { landing.GetDesc(&mut desc) };
        assert_eq!(
            (
                desc.Width,
                desc.Height,
                desc.Format,
                desc.MipLevels,
                desc.ArraySize
            ),
            (width, height, DXGI_FORMAT_P010, 1, 1)
        );
        let staging_desc = D3D11_TEXTURE2D_DESC {
            Width: width,
            Height: height,
            MipLevels: 1,
            ArraySize: 1,
            Format: DXGI_FORMAT_P010,
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            Usage: D3D11_USAGE_STAGING,
            CPUAccessFlags: D3D11_CPU_ACCESS_READ.0 as u32,
            ..Default::default()
        };
        let mut staging = None;
        let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
        // SAFETY: matching P010 textures on this device; one Map/Unmap pair on the staging copy.
        unsafe {
            self.device
                .CreateTexture2D(&staging_desc, None, Some(&mut staging))
                .expect("staging texture");
            let staging = staging.expect("staging texture");
            self.context.CopyResource(&staging, landing);
            self.context
                .Map(&staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped))
                .expect("map staging");
            let pitch = mapped.RowPitch as usize;
            let rows = (height + height / 2) as usize;
            let row_bytes = 2 * width as usize;
            assert!(pitch >= row_bytes);
            let all = std::slice::from_raw_parts(mapped.pData.cast::<u8>(), pitch * rows);
            let bytes = all
                .chunks_exact(pitch)
                .flat_map(|row| &row[..row_bytes])
                .copied()
                .collect();
            self.context.Unmap(&staging, 0);
            bytes
        }
    }
}

struct ZeroCopy(*mut BaleZeroCopy);

impl Drop for ZeroCopy {
    fn drop(&mut self) {
        // SAFETY: created once by bale_zc_create, destroyed once here.
        unsafe { bale_zc_destroy(self.0) };
    }
}

struct Picture {
    data: *mut u8,
    id: *mut c_void,
}

impl ZeroCopy {
    fn alloc(&self, bytes: u64) -> Result<Picture, i32> {
        let (mut data, mut id) = (ptr::null_mut(), ptr::null_mut());
        // SAFETY: live handle; writable out slots.
        let hr = unsafe { bale_zc_alloc(self.0, bytes, &mut data, &mut id) };
        if hr != S_OK {
            assert!(data.is_null() && id.is_null());
            return Err(hr);
        }
        assert!(!data.is_null() && !id.is_null());
        Ok(Picture { data, id })
    }

    /// Allocates a padded `width` x `height` test picture filled with `seed` and returns it with
    /// its layout and source bytes.
    fn picture(&self, width: u32, height: u32, seed: u32) -> (Picture, BaleZcFrame, Vec<u8>) {
        let (frame, bytes) = BaleZcFrame::test_layout(width, height);
        let picture = self.alloc(bytes).expect("alloc");
        let mut source = vec![0u8; bytes as usize];
        fill_test_planes(&frame, &mut source, seed);
        // SAFETY: the buffer holds at least `bytes` and no repack reads it yet.
        unsafe { ptr::copy_nonoverlapping(source.as_ptr(), picture.data, source.len()) };
        (picture, frame, source)
    }

    fn repack(&self, id: *mut c_void, frame: &BaleZcFrame) -> (i32, *mut c_void) {
        let mut texture = ptr::null_mut();
        // SAFETY: live handle, readable frame, writable out slot; one test thread per handle.
        let hr = unsafe { bale_zc_repack(self.0, id, frame, &mut texture) };
        (hr, texture)
    }

    fn repack_into(&self, id: *mut c_void, frame: &BaleZcFrame, target: &ID3D11Texture2D) -> i32 {
        // SAFETY: live handle, readable frame, live texture; one test thread per handle.
        unsafe { bale_zc_repack_into(self.0, id, frame, ptr::null(), target.as_raw()) }
    }

    fn stats(&self) -> BaleZcStats {
        let mut stats = BaleZcStats::default();
        // SAFETY: live handle; writable out slot.
        assert_eq!(unsafe { bale_zc_stats(self.0, &mut stats) }, S_OK);
        stats
    }
}

fn release(id: *mut c_void) -> i32 {
    // SAFETY: the test no longer touches the picture's memory.
    unsafe { bale_zc_release(id) }
}

#[test]
fn abi_version_and_null_registration() {
    assert_eq!(bale_zc_abi_version(), ZERO_COPY_ABI_VERSION);
    assert_eq!(ZERO_COPY_ABI_VERSION, 1);
    // SAFETY: null is the rejected input.
    assert_eq!(
        unsafe { bale_zc_register_d3d12(ptr::null_mut()) },
        E_INVALIDARG.0
    );
}

/// Repacks through both slots and several size changes, odd chroma widths included, and checks
/// every landing texture against the CPU reference.
fn repack_sizes(warp: &Warp, zero_copy: &ZeroCopy) -> BaleZcStats {
    let sizes = [
        (64, 64),
        (128, 72),
        (128, 72),
        (130, 40),
        (130, 40),
        (64, 64),
        (2, 2),
    ];
    for (seed, (width, height)) in sizes.into_iter().enumerate() {
        let (picture, frame, source) = zero_copy.picture(width, height, seed as u32 + 11);
        let (hr, texture) = zero_copy.repack(picture.id, &frame);
        assert_eq!(hr, S_OK, "{width}x{height}: HRESULT=0x{:08x}", hr as u32);
        // Released before the copy: the GPU still reads it, so it must not be reused yet.
        assert_eq!(release(picture.id), S_OK);
        assert_eq!(
            warp.read(texture, width, height),
            frame.reference_p010(&source),
            "{width}x{height}"
        );
    }
    let stats = zero_copy.stats();
    assert_eq!(stats.frames_repacked, sizes.len() as u64);
    assert_eq!(stats.buffers_live, 0);
    assert_eq!(stats.alloc_failures, 0);
    stats
}

#[test]
fn repacks_through_both_slots_and_size_changes() {
    let warp = Warp::new();
    let stats = repack_sizes(&warp, &warp.zero_copy());
    // WARP shares D3D11-created UAV P010 textures, so the dispatch writes them in place.
    assert_eq!(stats.landing_copies, 0, "{stats:?}");
}

#[test]
fn landing_copies_repack_through_both_slots_and_size_changes() {
    let warp = Warp::new();
    let stats = repack_sizes(&warp, &warp.zero_copy_with_landing_copies());
    assert_eq!(stats.landing_copies, stats.frames_repacked, "{stats:?}");
}

#[test]
fn idle_buffers_are_reused() {
    let warp = Warp::new();
    let zero_copy = warp.zero_copy();
    let (first, frame, source) = zero_copy.picture(96, 32, 5);
    let (hr, texture) = zero_copy.repack(first.id, &frame);
    assert_eq!(hr, S_OK);
    // The readback waits for the copy, which waited for the repack: the buffer is idle.
    assert_eq!(warp.read(texture, 96, 32), frame.reference_p010(&source));
    assert_eq!(release(first.id), S_OK);
    let before = zero_copy.stats();
    let (second, _, _) = zero_copy.picture(96, 32, 6);
    assert_eq!(
        second.data, first.data,
        "an idle buffer of the same size is reused"
    );
    assert_ne!(second.id, first.id, "ids are never reused");
    let after = zero_copy.stats();
    assert_eq!(after.buffers_created, before.buffers_created);
    assert_eq!(after.buffers_live, 1);
    assert_eq!(release(second.id), S_OK);
}

#[test]
fn rejects_bad_input_without_poisoning() {
    let warp = Warp::new();
    let zero_copy = warp.zero_copy();
    let other = warp.zero_copy();
    let (picture, frame, source) = zero_copy.picture(64, 16, 9);

    // Not a buffer of this handle: the caller takes its ordinary path.
    for id in [ptr::null_mut(), 2 as *mut c_void, usize::MAX as *mut c_void] {
        let (hr, texture) = zero_copy.repack(id, &frame);
        assert_eq!((hr, texture), (S_FALSE, ptr::null_mut()));
    }
    assert_eq!(other.repack(picture.id, &frame), (S_FALSE, ptr::null_mut()));

    // Layouts that do not fit the buffer, or that the shader does not handle.
    // Allocations are rounded up to 64 KiB, so the V plane at 64 KiB is past the end.
    for bad in [
        BaleZcFrame { width: 63, ..frame },
        BaleZcFrame {
            luma_stride: 64,
            ..frame
        },
        BaleZcFrame {
            offsets: [frame.offsets[0], frame.offsets[1], 64 << 10],
            ..frame
        },
        BaleZcFrame {
            height: 1 << 20,
            ..frame
        },
    ] {
        let (hr, texture) = zero_copy.repack(picture.id, &bad);
        assert_eq!((hr, texture), (E_INVALIDARG.0, ptr::null_mut()), "{bad:?}");
    }
    // SAFETY: null pointers are the rejected inputs.
    unsafe {
        assert_eq!(
            bale_zc_repack(zero_copy.0, picture.id, &frame, ptr::null_mut()),
            E_INVALIDARG.0
        );
        let mut texture = ptr::null_mut();
        assert_eq!(
            bale_zc_repack(zero_copy.0, picture.id, ptr::null(), &mut texture),
            E_INVALIDARG.0
        );
        assert_eq!(
            bale_zc_repack(ptr::null(), picture.id, &frame, &mut texture),
            E_INVALIDARG.0
        );
        let (mut data, mut id) = (ptr::null_mut(), ptr::null_mut());
        assert_eq!(
            bale_zc_alloc(ptr::null(), 64, &mut data, &mut id),
            E_INVALIDARG.0
        );
        assert_eq!(
            bale_zc_alloc(zero_copy.0, 64, ptr::null_mut(), &mut id),
            E_INVALIDARG.0
        );
        assert_eq!(bale_zc_stats(zero_copy.0, ptr::null_mut()), E_INVALIDARG.0);
    }
    assert_eq!(zero_copy.alloc(0).err(), Some(E_INVALIDARG.0));
    assert_eq!(zero_copy.alloc(MAX_BYTES + 1).err(), Some(E_INVALIDARG.0));

    // Still healthy: the good layout repacks.
    let (hr, texture) = zero_copy.repack(picture.id, &frame);
    assert_eq!(hr, S_OK);
    assert_eq!(warp.read(texture, 64, 16), frame.reference_p010(&source));
    assert_eq!(release(picture.id), S_OK);
    assert_eq!(release(picture.id), E_INVALIDARG.0, "double release");
    assert_eq!(release(2 as *mut c_void), E_INVALIDARG.0, "never-issued id");
    assert_eq!(zero_copy.stats().frames_repacked, 1);
    assert_eq!(other.stats().frames_repacked, 0);
}

#[test]
fn pool_limit_fails_fast_and_recovers() {
    let warp = Warp::new();
    let zero_copy = warp.zero_copy();
    let held: Vec<Picture> = (0..MAX_BUFFERS)
        .map(|_| zero_copy.alloc(4096).expect("within the limit"))
        .collect();
    assert_eq!(zero_copy.alloc(4096).err(), Some(E_OUTOFMEMORY.0));
    let stats = zero_copy.stats();
    assert_eq!(stats.buffers_live, MAX_BUFFERS as u64);
    assert_eq!(stats.buffers_peak, MAX_BUFFERS as u64);
    assert!(stats.alloc_failures >= 1);
    assert_eq!(release(held[0].id), S_OK);
    let again = zero_copy
        .alloc(4096)
        .expect("a released buffer is available again");
    for picture in held.iter().skip(1).chain([&again]) {
        assert_eq!(release(picture.id), S_OK);
    }
    assert_eq!(zero_copy.stats().buffers_live, 0);
}

#[test]
fn buffers_outlive_their_handle() {
    let warp = Warp::new();
    let zero_copy = warp.zero_copy();
    let (picture, frame, source) = zero_copy.picture(64, 8, 21);
    let (hr, texture) = zero_copy.repack(picture.id, &frame);
    assert_eq!(hr, S_OK);
    assert_eq!(warp.read(texture, 64, 8), frame.reference_p010(&source));
    drop(zero_copy);
    // The decoder may still write into and then release a picture after the handle is gone.
    // SAFETY: the buffer stays mapped until released.
    unsafe { picture.data.write_bytes(0x5a, 64) };
    assert_eq!(release(picture.id), S_OK);
    assert_eq!(release(picture.id), E_INVALIDARG.0);
}

/// Direct writes into the caller's textures, mixed with landing frames on the same slots and with
/// size changes; a reused texture is written again in place.
#[test]
fn direct_writes_into_shared_uav_textures() {
    let warp = Warp::new();
    let zero_copy = warp.zero_copy();
    let pool = [warp.target(128, 72), warp.target(128, 72)];
    let small = warp.target(130, 40);
    // (size, Some(target) for a direct write, None for a landing frame)
    let plan: [((u32, u32), Option<&ID3D11Texture2D>); 8] = [
        ((128, 72), Some(&pool[0])),
        ((128, 72), Some(&pool[1])),
        ((128, 72), None),
        ((128, 72), Some(&pool[0])),
        ((130, 40), Some(&small)),
        ((130, 40), None),
        ((130, 40), None),
        ((128, 72), Some(&pool[1])),
    ];
    for (seed, ((width, height), target)) in plan.into_iter().enumerate() {
        let (picture, frame, source) = zero_copy.picture(width, height, seed as u32 + 31);
        let expected = frame.reference_p010(&source);
        match target {
            Some(target) => {
                let hr = zero_copy.repack_into(picture.id, &frame, target);
                assert_eq!(hr, S_OK, "{seed}: HRESULT=0x{:08x}", hr as u32);
                assert_eq!(release(picture.id), S_OK);
                assert_eq!(
                    warp.read(target.as_raw(), width, height),
                    expected,
                    "{seed}"
                );
            }
            None => {
                let (hr, texture) = zero_copy.repack(picture.id, &frame);
                assert_eq!(hr, S_OK, "{seed}: HRESULT=0x{:08x}", hr as u32);
                assert_eq!(release(picture.id), S_OK);
                assert_eq!(warp.read(texture, width, height), expected, "{seed}");
            }
        }
    }
    let stats = zero_copy.stats();
    assert_eq!(stats.frames_repacked, plan.len() as u64);
    assert_eq!(stats.landing_copies, 0, "{stats:?}");
    assert_eq!(stats.buffers_live, 0);
}

/// Textures the dispatch cannot write, grain, and foreign buffers are declined without GPU work
/// and without refusing later direct writes; null inputs are rejected.
#[test]
fn direct_declines_without_poisoning() {
    let warp = Warp::new();
    let zero_copy = warp.zero_copy();
    let (picture, frame, source) = zero_copy.picture(64, 16, 41);
    let uav = (D3D11_BIND_SHADER_RESOURCE.0 | D3D11_BIND_UNORDERED_ACCESS.0) as u32;
    let nt = (D3D11_RESOURCE_MISC_SHARED_NTHANDLE.0 | D3D11_RESOURCE_MISC_SHARED.0) as u32;
    let other = Warp::new();
    let unfit = [
        warp.target(64, 18),
        warp.target(66, 16),
        warp.texture(
            64,
            16,
            DXGI_FORMAT_P010,
            D3D11_BIND_SHADER_RESOURCE.0 as u32,
            nt,
        )
        .expect("shared P010 without UAV"),
        warp.texture(64, 16, DXGI_FORMAT_P010, uav, 0)
            .expect("unshared UAV P010"),
        warp.texture(64, 16, DXGI_FORMAT_R16_UINT, uav, nt)
            .expect("shared UAV R16"),
        other.target(64, 16),
    ];
    for (index, target) in unfit.iter().enumerate() {
        assert_eq!(
            zero_copy.repack_into(picture.id, &frame, target),
            S_FALSE,
            "{index}"
        );
    }
    let target = warp.target(64, 16);
    for id in [ptr::null_mut(), 2 as *mut c_void] {
        assert_eq!(zero_copy.repack_into(id, &frame, &target), S_FALSE);
    }
    // SAFETY: plain integer fields; all zeros is a valid value (never read: grain is declined).
    let grain: BaleGrainParams = unsafe { std::mem::zeroed() };
    // SAFETY: live handle, readable frame and grain, live texture; null target is rejected.
    unsafe {
        assert_eq!(
            bale_zc_repack_into(zero_copy.0, picture.id, &frame, &grain, target.as_raw()),
            S_FALSE
        );
        assert_eq!(
            bale_zc_repack_into(
                zero_copy.0,
                picture.id,
                &frame,
                ptr::null(),
                ptr::null_mut()
            ),
            E_INVALIDARG.0
        );
        assert_eq!(
            bale_zc_repack_into(
                zero_copy.0,
                picture.id,
                ptr::null(),
                ptr::null(),
                target.as_raw()
            ),
            E_INVALIDARG.0
        );
        assert_eq!(
            bale_zc_repack_into(
                ptr::null(),
                picture.id,
                &frame,
                ptr::null(),
                target.as_raw()
            ),
            E_INVALIDARG.0
        );
    }
    let bad = BaleZcFrame { width: 63, ..frame };
    assert_eq!(
        zero_copy.repack_into(picture.id, &bad, &target),
        E_INVALIDARG.0
    );
    assert_eq!(zero_copy.stats().frames_repacked, 0);

    assert_eq!(zero_copy.repack_into(picture.id, &frame, &target), S_OK);
    assert_eq!(
        warp.read(target.as_raw(), 64, 16),
        frame.reference_p010(&source)
    );
    assert_eq!(release(picture.id), S_OK);
    assert_eq!(zero_copy.stats().frames_repacked, 1);
}

/// At most 32 caller textures are open; one idle for 64 frames is closed, which frees a place.
#[test]
fn direct_targets_are_capped_and_closed_when_idle() {
    const MAX_TARGETS: usize = 32;
    const IDLE_FRAMES: usize = 64;
    let warp = Warp::new();
    let zero_copy = warp.zero_copy();
    let (picture, frame, source) = zero_copy.picture(64, 8, 51);
    let targets: Vec<ID3D11Texture2D> = (0..=MAX_TARGETS).map(|_| warp.target(64, 8)).collect();
    for target in &targets[..MAX_TARGETS] {
        assert_eq!(zero_copy.repack_into(picture.id, &frame, target), S_OK);
    }
    let extra = &targets[MAX_TARGETS];
    assert_eq!(zero_copy.repack_into(picture.id, &frame, extra), S_FALSE);
    let busy = &targets[0];
    for _ in 0..IDLE_FRAMES {
        assert_eq!(zero_copy.repack_into(picture.id, &frame, busy), S_OK);
    }
    // Idle textures are closed once the GPU is done with them.
    let _ = warp.read(busy.as_raw(), 64, 8);
    assert_eq!(zero_copy.repack_into(picture.id, &frame, extra), S_OK);
    assert_eq!(
        warp.read(extra.as_raw(), 64, 8),
        frame.reference_p010(&source)
    );
    assert_eq!(release(picture.id), S_OK);
}
