//! Planar-to-NV12/P010 repack shaders on WARP, through the C ABI; no feature skips.
#![cfg(windows)]

use std::ptr;

use bale_gpu::repack::{
    BaleRepackGpu, REPACK_ABI_VERSION, S_OK, bale_repack_abi_version, bale_repack_gpu_apply,
    bale_repack_gpu_create, bale_repack_gpu_destroy, self_test_frames,
};
use windows::Win32::Foundation::{E_INVALIDARG, HMODULE};
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_WARP, D3D_FEATURE_LEVEL_11_0};
use windows::Win32::Graphics::Direct3D10::ID3D10Multithread;
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::core::Interface;

struct Warp {
    device: ID3D11Device,
    context: ID3D11DeviceContext,
}

impl Warp {
    fn new() -> Self {
        let mut device = None;
        let mut context = None;
        // SAFETY: no adapter/software module; out-pointers are live local COM slots.
        unsafe {
            D3D11CreateDevice(
                None,
                D3D_DRIVER_TYPE_WARP,
                HMODULE::default(),
                D3D11_CREATE_DEVICE_FLAG(0),
                Some(&[D3D_FEATURE_LEVEL_11_0]),
                D3D11_SDK_VERSION,
                Some(&mut device),
                None,
                Some(&mut context),
            )
            .expect("WARP FL11_0 is required; GPU repack tests must not skip");
        }
        let device = device.expect("WARP device");
        let context = context.expect("WARP immediate context");
        let multithread: ID3D10Multithread =
            context.cast().expect("WARP must expose ID3D10Multithread");
        // SAFETY: configure the newly created private context before any other use.
        let _ = unsafe { multithread.SetMultithreadProtected(true) };
        Self { device, context }
    }

    fn repack(&self) -> Repack {
        let mut handle = ptr::null_mut();
        // SAFETY: live COM interfaces and writable out slot, borrowed on this one test thread.
        let hr = unsafe {
            bale_repack_gpu_create(self.device.as_raw(), self.context.as_raw(), &mut handle)
        };
        assert_eq!(
            hr, S_OK,
            "repack create (including NV12/P010 self-tests) must succeed on WARP: HRESULT=0x{:08x}",
            hr as u32
        );
        assert!(!handle.is_null());
        Repack(handle)
    }

    fn texture(&self, desc: &D3D11_TEXTURE2D_DESC, data: Option<&[u8]>) -> ID3D11Texture2D {
        let pitch = desc.Width * bytes(desc.Format) as u32;
        let initial = data.map(|bytes| {
            assert_eq!(bytes.len(), (pitch * (desc.Height * 3 / 2)) as usize);
            D3D11_SUBRESOURCE_DATA {
                pSysMem: bytes.as_ptr().cast(),
                SysMemPitch: pitch,
                SysMemSlicePitch: 0,
            }
        });
        let mut texture = None;
        // SAFETY: one subresource; data (when present) covers both planes at the declared pitch
        // and stays live through this synchronous copy.
        unsafe {
            self.device.CreateTexture2D(
                desc,
                initial.as_ref().map(ptr::from_ref),
                Some(&mut texture),
            )
        }
        .expect("WARP must create the requested texture; no unsupported-feature skips");
        texture.unwrap()
    }

    fn readback(&self, texture: &ID3D11Texture2D) -> Vec<u8> {
        let mut desc = D3D11_TEXTURE2D_DESC::default();
        // SAFETY: live interface and writable local descriptor.
        unsafe { texture.GetDesc(&mut desc) };
        desc.Usage = D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
        let staging = self.texture(&desc, None);
        let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
        // SAFETY: same-device, compatible, unmapped resources. Map synchronizes without Flush.
        unsafe {
            self.context.CopyResource(&staging, texture);
            self.context
                .Map(&staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped))
                .expect("map WARP readback");
        }
        let row_bytes = desc.Width as usize * bytes(desc.Format);
        let rows = (desc.Height * 3 / 2) as usize;
        assert!(!mapped.pData.is_null());
        assert!(mapped.RowPitch as usize >= row_bytes);
        // SAFETY: NV12/P010 maps have Height luma rows then Height/2 chroma rows at RowPitch
        // each; the tiny test dimensions keep this extent small.
        let data = unsafe {
            std::slice::from_raw_parts(mapped.pData.cast::<u8>(), mapped.RowPitch as usize * rows)
        };
        let result = data
            .chunks_exact(mapped.RowPitch as usize)
            .flat_map(|row| row[..row_bytes].iter().copied())
            .collect();
        // SAFETY: subresource zero was mapped above and the slice is no longer used.
        unsafe { self.context.Unmap(&staging, 0) };
        result
    }
}

struct Repack(*mut BaleRepackGpu);

impl Repack {
    fn apply(&self, src: &ID3D11Texture2D, dst: &ID3D11Texture2D) -> i32 {
        // SAFETY: this test owns the handle exclusively, with live unmapped same-device textures.
        unsafe { bale_repack_gpu_apply(self.0, src.as_raw(), dst.as_raw()) }
    }
}

impl Drop for Repack {
    fn drop(&mut self) {
        // SAFETY: unique live handle returned by create, destroyed exactly once on this thread.
        unsafe { bale_repack_gpu_destroy(self.0) };
    }
}

fn bytes(format: DXGI_FORMAT) -> usize {
    if format == DXGI_FORMAT_P010 { 2 } else { 1 }
}

fn staging(width: u32, height: u32, format: DXGI_FORMAT) -> D3D11_TEXTURE2D_DESC {
    D3D11_TEXTURE2D_DESC {
        Usage: D3D11_USAGE_STAGING,
        BindFlags: 0,
        CPUAccessFlags: D3D11_CPU_ACCESS_WRITE.0 as u32,
        ..target(width, height, format)
    }
}

fn target(width: u32, height: u32, format: DXGI_FORMAT) -> D3D11_TEXTURE2D_DESC {
    D3D11_TEXTURE2D_DESC {
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
        BindFlags: (D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE).0 as u32,
        ..Default::default()
    }
}

fn assert_bytes(actual: &[u8], expected: &[u8], name: &str, width: usize, format: DXGI_FORMAT) {
    assert_eq!(actual.len(), expected.len(), "{name}: length");
    let pitch = width * bytes(format);
    if let Some(index) = actual.iter().zip(expected).position(|(a, e)| a != e) {
        panic!(
            "{name} {format:?}: first mismatch at row={} byte={}: expected=0x{:02x} actual=0x{:02x}",
            index / pitch,
            index % pitch,
            expected[index],
            actual[index]
        );
    }
}

#[test]
fn abi_version_is_one() {
    assert_eq!(bale_repack_abi_version(), REPACK_ABI_VERSION);
    assert_eq!(REPACK_ABI_VERSION, 1);
}

#[test]
fn nv12_and_p010_match_the_cpu_reference_on_warp() {
    let warp = Warp::new();
    let repack = warp.repack();
    // Two sizes in a row also exercise the intermediate being rebuilt for a new key.
    for (width, height) in [(132, 70), (64, 8), (132, 70)] {
        for format in [DXGI_FORMAT_NV12, DXGI_FORMAT_P010] {
            let (planar, expected) = self_test_frames(format, width, height);
            let src = warp.texture(&staging(width as u32, height as u32, format), Some(&planar));
            // Distinct initial contents prove every destination texel is written.
            let dst = warp.texture(
                &target(width as u32, height as u32, format),
                Some(&vec![0x69; planar.len()]),
            );
            assert_eq!(
                repack.apply(&src, &dst),
                S_OK,
                "{width}x{height} {format:?}"
            );
            assert_bytes(&warp.readback(&dst), &expected, "repacked", width, format);
            assert_bytes(
                &warp.readback(&src),
                &planar,
                "source unchanged",
                width,
                format,
            );
        }
    }
}

#[test]
fn invalid_arguments_do_not_write_textures() {
    let warp = Warp::new();
    let repack = warp.repack();
    let (planar, _) = self_test_frames(DXGI_FORMAT_NV12, 132, 70);
    let src = warp.texture(&staging(132, 70, DXGI_FORMAT_NV12), Some(&planar));
    let dst = warp.texture(&target(132, 70, DXGI_FORMAT_NV12), Some(&planar));
    // 130 is a valid NV12 width but its V half would start mid-texel.
    let narrow = warp.texture(&staging(130, 70, DXGI_FORMAT_NV12), None);
    let narrow_dst = warp.texture(&target(130, 70, DXGI_FORMAT_NV12), None);
    let p010 = warp.texture(&staging(132, 70, DXGI_FORMAT_P010), None);
    let mut no_rt = target(132, 70, DXGI_FORMAT_NV12);
    no_rt.BindFlags = D3D11_BIND_SHADER_RESOURCE.0 as u32;
    let no_rt = warp.texture(&no_rt, Some(&planar));
    for (name, s, d) in [
        ("width not a multiple of 4", &narrow, &narrow_dst),
        ("format mismatch", &p010, &dst),
        ("size mismatch", &narrow, &dst),
        ("aliased", &dst, &dst),
        ("destination without render target", &src, &no_rt),
    ] {
        assert_eq!(repack.apply(s, d), E_INVALIDARG.0, "{name}");
    }
    assert_bytes(
        &warp.readback(&dst),
        &planar,
        "rejected destination",
        132,
        DXGI_FORMAT_NV12,
    );
    assert_bytes(
        &warp.readback(&no_rt),
        &planar,
        "rejected no-RT",
        132,
        DXGI_FORMAT_NV12,
    );
    // SAFETY: nulls are explicitly rejected; other arguments are live and unmapped.
    // Destroy(null) is specified as a no-op.
    unsafe {
        for (handle, source, target) in [
            (ptr::null_mut(), src.as_raw(), dst.as_raw()),
            (repack.0, ptr::null_mut(), dst.as_raw()),
            (repack.0, src.as_raw(), ptr::null_mut()),
        ] {
            assert_eq!(
                bale_repack_gpu_apply(handle, source, target),
                E_INVALIDARG.0
            );
        }
        bale_repack_gpu_destroy(ptr::null_mut());
    }
    // Rejections must not poison: a valid call still succeeds afterwards.
    assert_eq!(repack.apply(&src, &dst), S_OK);
}

#[test]
fn create_and_apply_preserve_the_callers_viewport() {
    let warp = Warp::new();
    let viewport = D3D11_VIEWPORT {
        TopLeftX: 3.0,
        TopLeftY: 5.0,
        Width: 17.0,
        Height: 23.0,
        MinDepth: 0.2,
        MaxDepth: 0.8,
    };
    let current = || {
        let mut count = 1;
        let mut actual = D3D11_VIEWPORT::default();
        // SAFETY: writable locals on a live private context; the query submits no work.
        unsafe { warp.context.RSGetViewports(&mut count, Some(&mut actual)) };
        (count, actual)
    };
    // SAFETY: private context owned by this test.
    unsafe { warp.context.RSSetViewports(Some(&[viewport])) };
    let repack = warp.repack();
    assert_eq!(current(), (1, viewport));
    let (planar, _) = self_test_frames(DXGI_FORMAT_P010, 64, 64);
    let src = warp.texture(&staging(64, 64, DXGI_FORMAT_P010), Some(&planar));
    let dst = warp.texture(&target(64, 64, DXGI_FORMAT_P010), None);
    assert_eq!(repack.apply(&src, &dst), S_OK);
    assert_eq!(current(), (1, viewport));
}

#[test]
fn create_clears_output_on_failure() {
    let warp = Warp::new();
    let sentinel = ptr::dangling_mut::<BaleRepackGpu>();
    // SAFETY: null interface arguments are rejected; non-null interfaces/output locals are live.
    unsafe {
        for (device, context) in [
            (ptr::null_mut(), warp.context.as_raw()),
            (warp.device.as_raw(), ptr::null_mut()),
        ] {
            let mut out = sentinel;
            assert_eq!(
                bale_repack_gpu_create(device, context, &mut out),
                E_INVALIDARG.0
            );
            assert!(out.is_null());
        }
        assert_eq!(
            bale_repack_gpu_create(warp.device.as_raw(), warp.context.as_raw(), ptr::null_mut()),
            E_INVALIDARG.0
        );
    }
}
