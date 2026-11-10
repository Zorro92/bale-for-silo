//! Zero-copy repack (D3D12 compute into shared P010 landing textures) on WARP, through the C ABI;
//! no feature skips.
#![cfg(windows)]

mod common;

use std::ffi::c_void;
use std::ptr;

use bale_gpu::d3d12_repack::{
    BaleZcStats, BaleZeroCopy, S_FALSE, S_OK, ZERO_COPY_ABI_VERSION, bale_zc_abi_version,
    bale_zc_alloc, bale_zc_create, bale_zc_destroy, bale_zc_register_d3d12, bale_zc_release,
    bale_zc_repack, bale_zc_repack_into, bale_zc_repack_into_bits, bale_zc_stats,
    create_with_landing_copies, diagnose_create,
};
use bale_gpu::grain::BaleGrainParams;
use bale_gpu::grain::params::PARAMS_SIZE;
use bale_gpu::zc_frame::{BaleZcFrame, MAX_BYTES, fill_test_planes_bits, grain_p010};
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

    /// Allocates a padded `width` x `height` 10-bit test picture filled with `seed` and returns
    /// it with its layout and source bytes.
    fn picture(&self, width: u32, height: u32, seed: u32) -> (Picture, BaleZcFrame, Vec<u8>) {
        self.picture_bits(width, height, seed, 10)
    }

    /// [`picture`](Self::picture) at `depth` (8 or 10).
    fn picture_bits(
        &self,
        width: u32,
        height: u32,
        seed: u32,
        depth: u32,
    ) -> (Picture, BaleZcFrame, Vec<u8>) {
        let (frame, bytes) = BaleZcFrame::test_layout_bits(width, height, depth);
        self.picture_in(frame, bytes, seed, depth)
    }

    /// A test picture of `depth` in the given layout, filled with `seed` (garbage everywhere
    /// outside the samples).
    fn picture_in(
        &self,
        frame: BaleZcFrame,
        bytes: u64,
        seed: u32,
        depth: u32,
    ) -> (Picture, BaleZcFrame, Vec<u8>) {
        let mut source = vec![0u8; bytes as usize];
        fill_test_planes_bits(&frame, &mut source, seed, depth);
        (self.upload(&source), frame, source)
    }

    /// Allocates a padded picture holding a fixture's planar frame (8- or 10-bit), with seeded
    /// garbage in the padding and, at 10 bits, in the top 6 bits of every sample.
    fn fixture_picture(&self, params: &BaleGrainParams, planar: &[u8]) -> (Picture, BaleZcFrame) {
        let depth = u32::from(params.bit_depth);
        let (frame, bytes) = BaleZcFrame::test_layout_bits(params.width, params.height, depth);
        let mut source = vec![0u8; bytes as usize];
        fill_test_planes_bits(&frame, &mut source, params.seed, depth);
        let (w, h) = (params.width as usize, params.height as usize);
        let (cw, ch) = (w / 2, h / 2);
        let planes = [
            (0, w, h, frame.luma_stride),
            (w * h, cw, ch, frame.chroma_stride),
            (w * h + cw * ch, cw, ch, frame.chroma_stride),
        ];
        for (offset, (start, pw, ph, stride)) in frame.offsets.into_iter().zip(planes) {
            for row in 0..ph {
                for x in 0..pw {
                    let value =
                        common::planar_sample(planar, start + row * pw + x, params.bit_depth);
                    let row = offset as usize + row * stride as usize;
                    if depth == 8 {
                        source[row + x] = value as u8;
                    } else {
                        let at = row + 2 * x;
                        let garbage = u16::from_le_bytes([source[at], source[at + 1]]) & 0xfc00;
                        source[at..at + 2].copy_from_slice(&(value | garbage).to_le_bytes());
                    }
                }
            }
        }
        assert_eq!(
            frame.reference_p010_bits(&source, depth),
            p010(planar, w, h, params.bit_depth)
        );
        (self.upload(&source), frame)
    }

    fn upload(&self, source: &[u8]) -> Picture {
        let picture = self.alloc(source.len() as u64).expect("alloc");
        // SAFETY: the buffer holds at least `source.len()` bytes and no repack reads it yet.
        unsafe { ptr::copy_nonoverlapping(source.as_ptr(), picture.data, source.len()) };
        picture
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

    /// A direct write with fused film grain.
    fn repack_grain(
        &self,
        id: *mut c_void,
        frame: &BaleZcFrame,
        grain: &BaleGrainParams,
        target: &ID3D11Texture2D,
    ) -> i32 {
        // SAFETY: live handle, readable frame and full grain record, live texture; one test
        // thread per handle.
        unsafe { bale_zc_repack_into(self.0, id, frame, grain, target.as_raw()) }
    }

    /// `bale_zc_repack_into_bits` with optional grain.
    fn repack_into_bits(
        &self,
        id: *mut c_void,
        frame: &BaleZcFrame,
        depth: u32,
        grain: Option<&BaleGrainParams>,
        target: &ID3D11Texture2D,
    ) -> i32 {
        let grain = grain.map_or(ptr::null(), ptr::from_ref);
        // SAFETY: live handle, readable frame and full grain record or null, live texture; one
        // test thread per handle.
        unsafe { bale_zc_repack_into_bits(self.0, id, frame, depth, grain, target.as_raw()) }
    }

    /// A direct write at `depth`: through `bale_zc_repack_into` at 10 bits (the original export
    /// stays covered), `bale_zc_repack_into_bits` at 8.
    fn direct(
        &self,
        id: *mut c_void,
        frame: &BaleZcFrame,
        depth: u32,
        grain: Option<&BaleGrainParams>,
        target: &ID3D11Texture2D,
    ) -> i32 {
        match (depth, grain) {
            (10, None) => self.repack_into(id, frame, target),
            (10, Some(grain)) => self.repack_grain(id, frame, grain, target),
            _ => self.repack_into_bits(id, frame, depth, grain, target),
        }
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

/// A planar 4:2:0 frame (even size) of `depth` (8 or 10) as tight P010 bytes.
fn p010(planar: &[u8], width: usize, height: usize, depth: u8) -> Vec<u8> {
    let (cw, ch) = (width / 2, height / 2);
    let at = |sample: usize| {
        (common::planar_sample(planar, sample, depth) << (16 - depth)).to_le_bytes()
    };
    let mut out = Vec::with_capacity(3 * width * height);
    for sample in 0..width * height {
        out.extend_from_slice(&at(sample));
    }
    for sample in 0..cw * ch {
        out.extend_from_slice(&at(width * height + sample));
        out.extend_from_slice(&at(width * height + cw * ch + sample));
    }
    out
}

/// The CPU oracle's film grain on a tight P010 frame at the parameters' depth (8-bit samples in
/// the high byte).
fn grained_p010(params: &BaleGrainParams, plain: &[u8]) -> Vec<u8> {
    let valid = params.validate().expect("valid grain parameters");
    let mut out = plain.to_vec();
    grain_p010(&valid, &mut out).expect("CPU grain");
    out
}

/// Equality of two P010 frames that reports the first difference, not the pixels.
fn assert_frame(actual: &[u8], expected: &[u8], what: &str) {
    assert_eq!(actual.len(), expected.len(), "{what}: frame size");
    let mismatches = actual.iter().zip(expected).filter(|(a, e)| a != e).count();
    let first = actual.iter().zip(expected).position(|(a, e)| a != e);
    assert_eq!(
        mismatches, 0,
        "{what}: {mismatches} bytes differ, first at byte {first:?}"
    );
}

/// The module's fused self-test parameter set (10-bit, overlap, restricted, both chroma planes
/// with their own points and multipliers), at `seed`.
fn grain_params(width: u32, height: u32, seed: u32) -> BaleGrainParams {
    let mut p = BaleGrainParams {
        struct_size: PARAMS_SIZE,
        seed,
        width,
        height,
        bit_depth: 10,
        matrix_identity: 0,
        num_y_points: 4,
        chroma_scaling_from_luma: 0,
        num_uv_points: [3; 2],
        scaling_shift: 8,
        ar_coeff_lag: 3,
        ar_coeff_shift: 7,
        grain_scale_shift: 0,
        overlap_flag: 1,
        clip_to_restricted_range: 1,
        y_points: [[0; 2]; 14],
        uv_points: [[[0; 2]; 10]; 2],
        ar_coeffs_y: std::array::from_fn(|i| ((i * 7) % 17) as i8 - 8),
        ar_coeffs_uv: std::array::from_fn(|plane| {
            std::array::from_fn(|i| ((i * 11 + plane * 5) % 19) as i8 - 9)
        }),
        reserved0: [0; 2],
        uv_mult: [-43, 31],
        uv_luma_mult: [81, -27],
        uv_offset: [-12, 117],
    };
    p.y_points[..4].copy_from_slice(&[[0, 47], [59, 173], [177, 91], [255, 211]]);
    p.uv_points[0][..3].copy_from_slice(&[[0, 139], [103, 39], [255, 217]]);
    p.uv_points[1][..3].copy_from_slice(&[[0, 73], [191, 229], [255, 119]]);
    p
}

/// Shader branches the even 10-bit fixtures cover thinly or not at all, each a valid set.
fn grain_variants(width: u32, height: u32, seed: u32) -> Vec<(&'static str, BaleGrainParams)> {
    grain_variants_bits(width, height, seed, 10)
}

/// [`grain_variants`] at `depth` (8 or 10).
fn grain_variants_bits(
    width: u32,
    height: u32,
    seed: u32,
    depth: u8,
) -> Vec<(&'static str, BaleGrainParams)> {
    let mut base = grain_params(width, height, seed);
    base.bit_depth = depth;
    let mut out = vec![("overlap on", base)];
    let mut p = base;
    p.overlap_flag = 0;
    out.push(("overlap off", p));
    let mut p = base;
    p.chroma_scaling_from_luma = 1;
    p.num_uv_points = [0; 2];
    out.push(("chroma scaling from luma", p));
    let mut p = base;
    p.num_y_points = 0;
    p.ar_coeffs_y = [0; 24];
    p.ar_coeffs_uv[0][24] = 0;
    p.ar_coeffs_uv[1][24] = 0;
    out.push(("luma inactive", p));
    let mut p = base;
    p.matrix_identity = 1;
    out.push(("identity matrix, restricted", p));
    let mut p = base;
    p.clip_to_restricted_range = 0;
    p.scaling_shift = 11;
    out.push(("full range", p));
    let mut p = base;
    p.num_uv_points[1] = 0;
    p.ar_coeffs_uv[1] = [0; 25];
    out.push(("cr inactive", p));
    let mut p = base;
    p.num_uv_points = [0; 2];
    p.ar_coeffs_uv = [[0; 25]; 2];
    out.push(("chroma inactive", p));
    out
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

/// Textures the dispatch cannot write, grain without a 184-byte record, and foreign buffers are
/// declined without GPU work and without refusing later direct writes; null inputs are rejected.
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
    // Zeroed parameters have struct_size 0, which is not 184: still declined, before anything
    // past the size field is read.
    // SAFETY: plain integer fields; all zeros is a valid value.
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

/// Fused grain on every even 8- and 10-bit dav1d fixture, both frames each, on one handle: the
/// direct write of the plain picture with that frame's parameters is dav1d's grained picture
/// (8-bit through `bale_zc_repack_into_bits`, 10-bit through `bale_zc_repack_into`).
#[test]
fn fused_grain_matches_dav1d_fixtures() {
    let warp = Warp::new();
    let zero_copy = warp.zero_copy();
    // Frames compared at 8 and at 10 bits.
    let mut compared = [0; 2];
    for fixture in common::fixtures() {
        let first = &fixture.frames[0].params;
        let (w, h, depth) = (first.width, first.height, first.bit_depth);
        if w % 2 != 0 || h % 2 != 0 {
            continue;
        }
        let count = match depth {
            8 => &mut compared[0],
            10 => &mut compared[1],
            _ => panic!("{}: unexpected bit depth {depth}", fixture.name),
        };
        let target = warp.target(w, h);
        for (index, frame) in fixture.frames.iter().enumerate() {
            let what = format!("{} frame {index}", fixture.name);
            let (picture, layout) = zero_copy.fixture_picture(&frame.params, &frame.plain);
            let hr = zero_copy.direct(
                picture.id,
                &layout,
                depth.into(),
                Some(&frame.params),
                &target,
            );
            assert_eq!(hr, S_OK, "{what}: HRESULT=0x{:08x}", hr as u32);
            assert_eq!(release(picture.id), S_OK);
            let expected = p010(&frame.grained, w as usize, h as usize, depth);
            assert_frame(&warp.read(target.as_raw(), w, h), &expected, &what);
            *count += 1;
        }
    }
    // preset01..preset15 and restricted, at -8 and at -10.
    assert_eq!(compared, [32, 32], "do not silently drop fixtures");
    let stats = zero_copy.stats();
    assert_eq!(stats.frames_repacked, 64);
    assert_eq!(stats.buffers_live, 0);
}

/// Fused grain against the CPU oracle on synthetic parameters for the branches the fixtures
/// lack, at sizes with partial 32x32 blocks, a chroma width that is not a multiple of the
/// shader's chroma pairs, and frames smaller than one block.
#[test]
fn fused_grain_matches_cpu_oracle() {
    grain_matches_cpu_oracle(10);
}

/// [`fused_grain_matches_cpu_oracle`] on 8-bit pictures (one byte per sample, padding garbage,
/// a last chroma word half outside the picture at 66 and 130 wide).
#[test]
fn fused_grain_matches_cpu_oracle_8bit() {
    grain_matches_cpu_oracle(8);
}

fn grain_matches_cpu_oracle(depth: u8) {
    let warp = Warp::new();
    let zero_copy = warp.zero_copy();
    let sizes = [(130, 40), (66, 18), (34, 66), (6, 2), (2, 2)];
    let mut compared = 0;
    for (index, (width, height)) in sizes.into_iter().enumerate() {
        let target = warp.target(width, height);
        for (variant, (name, params)) in grain_variants_bits(width, height, 7 + index as u32, depth)
            .into_iter()
            .enumerate()
        {
            let what = format!("{depth}-bit {width}x{height} {name}");
            let seed = (index * 16 + variant) as u32 + 71;
            let (picture, frame, source) =
                zero_copy.picture_bits(width, height, seed, depth.into());
            let expected = grained_p010(&params, &frame.reference_p010_bits(&source, depth.into()));
            let hr = zero_copy.direct(picture.id, &frame, depth.into(), Some(&params), &target);
            assert_eq!(hr, S_OK, "{what}: HRESULT=0x{:08x}", hr as u32);
            assert_eq!(release(picture.id), S_OK);
            assert_frame(&warp.read(target.as_raw(), width, height), &expected, &what);
            compared += 1;
        }
    }
    assert_eq!(compared, sizes.len() * 8);
    assert_eq!(zero_copy.stats().frames_repacked, compared as u64);
}

/// Grain the fused path does not handle is declined without GPU work and without refusing later
/// direct writes, with or without grain.
#[test]
fn fused_grain_declines_without_poisoning() {
    let warp = Warp::new();
    let zero_copy = warp.zero_copy();
    let (picture, frame, source) = zero_copy.picture(64, 16, 81);
    let target = warp.target(64, 16);
    let good = grain_params(64, 16, 9);
    let mut eight_bit = good;
    eight_bit.bit_depth = 8;
    assert!(
        eight_bit.validate().is_ok(),
        "declined for its depth, not as invalid"
    );
    let declined = [
        (
            "wider than the frame",
            BaleGrainParams { width: 66, ..good },
        ),
        (
            "taller than the frame",
            BaleGrainParams { height: 18, ..good },
        ),
        (
            "narrower than the frame",
            BaleGrainParams { width: 62, ..good },
        ),
        ("8-bit", eight_bit),
        (
            "short struct_size",
            BaleGrainParams {
                struct_size: PARAMS_SIZE - 4,
                ..good
            },
        ),
        (
            "long struct_size",
            BaleGrainParams {
                struct_size: PARAMS_SIZE + 4,
                ..good
            },
        ),
        (
            "invalid parameters",
            BaleGrainParams {
                scaling_shift: 7,
                ..good
            },
        ),
    ];
    let plain = frame.reference_p010(&source);
    for (written, (name, grain)) in declined.iter().enumerate() {
        assert_eq!(
            zero_copy.repack_grain(picture.id, &frame, grain, &target),
            S_FALSE,
            "{name}"
        );
        assert_eq!(zero_copy.stats().frames_repacked, written as u64, "{name}");
        // The handle still writes plain pictures directly.
        assert_eq!(
            zero_copy.repack_into(picture.id, &frame, &target),
            S_OK,
            "{name}"
        );
        assert_frame(&warp.read(target.as_raw(), 64, 16), &plain, name);
    }
    // And still applies grain.
    assert_eq!(
        zero_copy.repack_grain(picture.id, &frame, &good, &target),
        S_OK
    );
    assert_frame(
        &warp.read(target.as_raw(), 64, 16),
        &grained_p010(&good, &plain),
        "after the declines",
    );
    assert_eq!(release(picture.id), S_OK);
    assert_eq!(zero_copy.stats().frames_repacked, declined.len() as u64 + 1);
}

/// Grained, plain direct and landing frames interleaved on both slots and across two sizes: each
/// frame is exactly what its own path produces, whatever ran on the slot before.
#[test]
fn fused_grain_mixes_with_plain_and_landing_frames() {
    #[derive(Clone, Copy, Debug)]
    enum Path {
        Grain,
        Direct,
        Landing,
    }
    let warp = Warp::new();
    let zero_copy = warp.zero_copy();
    let large = [warp.target(128, 72), warp.target(128, 72)];
    let small = warp.target(130, 40);
    let plan = [
        ((128, 72), Path::Grain),
        ((128, 72), Path::Direct),
        ((128, 72), Path::Grain),
        ((128, 72), Path::Landing),
        ((130, 40), Path::Grain),
        ((130, 40), Path::Grain),
        ((130, 40), Path::Landing),
        ((130, 40), Path::Direct),
        ((128, 72), Path::Grain),
        ((128, 72), Path::Landing),
        ((128, 72), Path::Direct),
        ((130, 40), Path::Grain),
    ];
    for (step, ((width, height), path)) in plan.into_iter().enumerate() {
        let what = format!("{step} {path:?} {width}x{height}");
        let (picture, frame, source) = zero_copy.picture(width, height, step as u32 + 91);
        let plain = frame.reference_p010(&source);
        let target = match width {
            128 => &large[step % 2],
            _ => &small,
        };
        let (hr, written, expected) = match path {
            Path::Grain => {
                // Alternate parameter sets so consecutive grained frames differ in more than
                // the seed.
                let (_, params) = grain_variants(width, height, step as u32)[step % 8];
                let hr = zero_copy.repack_grain(picture.id, &frame, &params, target);
                (hr, target.as_raw(), grained_p010(&params, &plain))
            }
            Path::Direct => {
                let hr = zero_copy.repack_into(picture.id, &frame, target);
                (hr, target.as_raw(), plain)
            }
            Path::Landing => {
                let (hr, texture) = zero_copy.repack(picture.id, &frame);
                (hr, texture, plain)
            }
        };
        assert_eq!(hr, S_OK, "{what}: HRESULT=0x{:08x}", hr as u32);
        assert_eq!(release(picture.id), S_OK);
        assert_frame(&warp.read(written, width, height), &expected, &what);
    }
    let stats = zero_copy.stats();
    assert_eq!(stats.frames_repacked, plan.len() as u64);
    assert_eq!(stats.landing_copies, 0, "{stats:?}");
    assert_eq!(stats.buffers_live, 0);
}

/// The tightest layout `fits_bits` accepts at `depth`: strides rounded up to 4 bytes, planes back
/// to back, so a last group's whole-word read reaches into the next row or plane.
fn tight_layout(width: u32, height: u32, depth: u32) -> (BaleZcFrame, u64) {
    let sample = depth.div_ceil(8);
    let luma_stride = (sample * width).next_multiple_of(4);
    let chroma_stride = (sample * width / 2).next_multiple_of(4);
    let u = u64::from(luma_stride) * u64::from(height);
    let v = u + u64::from(chroma_stride) * u64::from(height / 2);
    let frame = BaleZcFrame {
        width,
        height,
        luma_stride,
        chroma_stride,
        offsets: [0, u, v],
    };
    let mut bytes = v + u64::from(chroma_stride) * u64::from(height / 2);
    // The last row's whole-word reads may pass the last plane's end: give them room after it.
    while !frame.fits_bits(bytes, depth) {
        bytes += 4;
    }
    (frame, bytes)
}

/// 8-bit pictures written directly: padded layouts with garbage in the padding and tight ones
/// whose last words reach past the width, at sizes with odd chroma widths, a chroma width of one
/// and thread groups cut by the edge; every target texture is the exact `v << 8` picture.
#[test]
fn eight_bit_direct_writes_padded_and_tight_layouts() {
    let warp = Warp::new();
    let zero_copy = warp.zero_copy();
    let sizes = [
        (64, 64),
        (66, 18),
        (130, 40),
        (34, 66),
        (128, 72),
        (6, 2),
        (2, 2),
    ];
    let mut written = 0;
    for (index, (width, height)) in sizes.into_iter().enumerate() {
        let targets = [warp.target(width, height), warp.target(width, height)];
        let layouts = [
            BaleZcFrame::test_layout_bits(width, height, 8),
            tight_layout(width, height, 8),
        ];
        for (kind, (layout, bytes)) in layouts.into_iter().enumerate() {
            let what = format!("{width}x{height} layout {kind}");
            let seed = 201 + 2 * index as u32 + kind as u32;
            let (picture, frame, source) = zero_copy.picture_in(layout, bytes, seed, 8);
            let target = &targets[kind];
            let hr = zero_copy.repack_into_bits(picture.id, &frame, 8, None, target);
            assert_eq!(hr, S_OK, "{what}: HRESULT=0x{:08x}", hr as u32);
            assert_eq!(release(picture.id), S_OK);
            let expected = frame.reference_p010_bits(&source, 8);
            assert!(
                expected.as_chunks::<2>().0.iter().all(|pair| pair[0] == 0),
                "{what}: 8-bit samples fill only the high byte"
            );
            assert_frame(&warp.read(target.as_raw(), width, height), &expected, &what);
            written += 1;
        }
    }
    assert_eq!(written, 2 * sizes.len());
    let stats = zero_copy.stats();
    assert_eq!(stats.frames_repacked, written as u64);
    assert_eq!(stats.buffers_live, 0);
}

/// Bad bit depths and layouts are rejected, and grain of the other depth is declined, without GPU
/// work and without refusing later 8- or 10-bit direct writes, with or without grain.
#[test]
fn eight_bit_declines_without_poisoning() {
    let warp = Warp::new();
    let zero_copy = warp.zero_copy();
    let (width, height) = (64, 16);
    let target = warp.target(width, height);
    let (eight, frame8, source8) = zero_copy.picture_bits(width, height, 301, 8);
    let (ten, frame10, source10) = zero_copy.picture_bits(width, height, 302, 10);
    let grain10 = grain_params(width, height, 303);
    let grain8 = BaleGrainParams {
        bit_depth: 8,
        ..grain10
    };
    assert!(
        grain8.validate().is_ok(),
        "declined for its depth, not as invalid"
    );

    for depth in [0, 1, 7, 9, 12, 16, u32::MAX] {
        for grain in [None, Some(&grain8), Some(&grain10)] {
            assert_eq!(
                zero_copy.repack_into_bits(eight.id, &frame8, depth, grain, &target),
                E_INVALIDARG.0,
                "bit depth {depth}"
            );
        }
    }
    // Grain of the other depth: declined, through both exports at 10 bits.
    let declined = [
        ("8-bit frame, 10-bit grain", eight.id, &frame8, 8, &grain10),
        ("10-bit frame, 8-bit grain", ten.id, &frame10, 10, &grain8),
        (
            "8-bit grain wider than the frame",
            eight.id,
            &frame8,
            8,
            &BaleGrainParams {
                width: 66,
                ..grain8
            },
        ),
        (
            "8-bit invalid parameters",
            eight.id,
            &frame8,
            8,
            &BaleGrainParams {
                scaling_shift: 7,
                ..grain8
            },
        ),
    ];
    for (name, id, frame, depth, grain) in declined {
        assert_eq!(
            zero_copy.repack_into_bits(id, frame, depth, Some(grain), &target),
            S_FALSE,
            "{name}"
        );
    }
    assert_eq!(
        zero_copy.repack_grain(ten.id, &frame10, &grain8, &target),
        S_FALSE
    );
    // Layouts that do not fit at the given depth: an 8-bit layout read as 10-bit has strides
    // shorter than a row of 16-bit samples.
    let bad = [
        (frame8, 10),
        (
            BaleZcFrame {
                width: 63,
                ..frame8
            },
            8,
        ),
        (
            BaleZcFrame {
                luma_stride: 60,
                ..frame8
            },
            8,
        ),
        (
            BaleZcFrame {
                chroma_stride: 30,
                ..frame8
            },
            8,
        ),
        (
            BaleZcFrame {
                offsets: [frame8.offsets[0], frame8.offsets[1] + 1, frame8.offsets[2]],
                ..frame8
            },
            8,
        ),
    ];
    for (layout, depth) in bad {
        assert_eq!(
            zero_copy.repack_into_bits(eight.id, &layout, depth, None, &target),
            E_INVALIDARG.0,
            "{layout:?} at {depth}"
        );
    }
    // Not a buffer of this handle.
    assert_eq!(
        zero_copy.repack_into_bits(2 as *mut c_void, &frame8, 8, None, &target),
        S_FALSE
    );
    // SAFETY: null pointers are the rejected inputs; the rest is live.
    unsafe {
        assert_eq!(
            bale_zc_repack_into_bits(
                zero_copy.0,
                eight.id,
                &frame8,
                8,
                ptr::null(),
                ptr::null_mut()
            ),
            E_INVALIDARG.0
        );
        assert_eq!(
            bale_zc_repack_into_bits(
                zero_copy.0,
                eight.id,
                ptr::null(),
                8,
                ptr::null(),
                target.as_raw()
            ),
            E_INVALIDARG.0
        );
        assert_eq!(
            bale_zc_repack_into_bits(
                ptr::null(),
                eight.id,
                &frame8,
                8,
                ptr::null(),
                target.as_raw()
            ),
            E_INVALIDARG.0
        );
    }
    assert_eq!(zero_copy.stats().frames_repacked, 0);

    // Still healthy at both depths, with and without grain.
    let plain8 = frame8.reference_p010_bits(&source8, 8);
    let plain10 = frame10.reference_p010(&source10);
    let checks = [
        ("8-bit plain", eight.id, &frame8, 8, None, plain8.clone()),
        (
            "8-bit grain",
            eight.id,
            &frame8,
            8,
            Some(&grain8),
            grained_p010(&grain8, &plain8),
        ),
        ("10-bit plain", ten.id, &frame10, 10, None, plain10.clone()),
        (
            "10-bit grain",
            ten.id,
            &frame10,
            10,
            Some(&grain10),
            grained_p010(&grain10, &plain10),
        ),
    ];
    for (name, id, frame, depth, grain, expected) in &checks {
        assert_eq!(
            zero_copy.repack_into_bits(*id, frame, *depth, *grain, &target),
            S_OK,
            "{name}"
        );
        assert_frame(&warp.read(target.as_raw(), width, height), expected, name);
    }
    assert_eq!(release(eight.id), S_OK);
    assert_eq!(release(ten.id), S_OK);
    assert_eq!(zero_copy.stats().frames_repacked, checks.len() as u64);
}

/// 8- and 10-bit frames, plain and grained, direct and landing, interleaved on one handle across
/// both slots and two sizes; 10-bit through both exports. Each frame is exactly what its own path
/// produces, whatever ran on the slot before.
#[test]
fn eight_and_ten_bit_frames_mix_on_one_handle() {
    #[derive(Clone, Copy, Debug)]
    enum Path {
        /// `bale_zc_repack_into_bits` at this depth, with or without grain.
        Bits(u32, bool),
        /// `bale_zc_repack_into` (10-bit), with or without grain.
        Into(bool),
        Landing,
    }
    let warp = Warp::new();
    let zero_copy = warp.zero_copy();
    let large = [warp.target(128, 72), warp.target(128, 72)];
    let small = warp.target(130, 40);
    let plan = [
        ((128, 72), Path::Bits(8, false)),
        ((128, 72), Path::Bits(10, false)),
        ((128, 72), Path::Bits(8, true)),
        ((128, 72), Path::Into(true)),
        ((128, 72), Path::Landing),
        ((128, 72), Path::Bits(8, true)),
        ((130, 40), Path::Bits(10, true)),
        ((130, 40), Path::Bits(8, false)),
        ((130, 40), Path::Landing),
        ((130, 40), Path::Bits(8, true)),
        ((130, 40), Path::Into(false)),
        ((128, 72), Path::Bits(8, false)),
        ((128, 72), Path::Bits(10, true)),
        ((128, 72), Path::Bits(8, true)),
    ];
    for (step, ((width, height), path)) in plan.into_iter().enumerate() {
        let what = format!("{step} {path:?} {width}x{height}");
        let depth = match path {
            Path::Bits(depth, _) => depth,
            _ => 10,
        };
        let (picture, frame, source) =
            zero_copy.picture_bits(width, height, step as u32 + 401, depth);
        let plain = frame.reference_p010_bits(&source, depth);
        let target = match width {
            128 => &large[step % 2],
            _ => &small,
        };
        let (_, params) = grain_variants_bits(width, height, step as u32, depth as u8)[step % 8];
        let (hr, written, expected) = match path {
            Path::Bits(_, grain) | Path::Into(grain) => {
                let grain = grain.then_some(&params);
                let hr = match path {
                    Path::Into(_) => zero_copy.direct(picture.id, &frame, 10, grain, target),
                    _ => zero_copy.repack_into_bits(picture.id, &frame, depth, grain, target),
                };
                let expected = match grain {
                    Some(params) => grained_p010(params, &plain),
                    None => plain,
                };
                (hr, target.as_raw(), expected)
            }
            Path::Landing => {
                let (hr, texture) = zero_copy.repack(picture.id, &frame);
                (hr, texture, plain)
            }
        };
        assert_eq!(hr, S_OK, "{what}: HRESULT=0x{:08x}", hr as u32);
        assert_eq!(release(picture.id), S_OK);
        assert_frame(&warp.read(written, width, height), &expected, &what);
    }
    let stats = zero_copy.stats();
    assert_eq!(stats.frames_repacked, plan.len() as u64);
    assert_eq!(stats.landing_copies, 0, "{stats:?}");
    assert_eq!(stats.buffers_live, 0);
}
