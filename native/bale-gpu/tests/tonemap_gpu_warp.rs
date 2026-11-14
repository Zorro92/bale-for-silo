//! Exercise the actual ABI and embedded SM5 shaders on WARP; no feature skips.
#![cfg(windows)]
use bale_gpu::tonemap::{cpu, ffi::*, gpu::ToneMapGpu, types::*};
use std::ptr;
use windows::Win32::{
    Foundation::HMODULE,
    Graphics::{
        Direct3D::{D3D_DRIVER_TYPE_WARP, D3D_FEATURE_LEVEL_11_0},
        Direct3D10::ID3D10Multithread,
        Direct3D11::*,
        Dxgi::Common::*,
    },
};
use windows::core::Interface;

struct Warp {
    device: ID3D11Device,
    context: ID3D11DeviceContext,
}
impl Warp {
    fn new() -> Self {
        let (mut device, mut context) = (None, None);
        // SAFETY: local output slots; no adapter/module; requested WARP feature level is supported.
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
            .unwrap();
        }
        let context = context.unwrap();
        let mt: ID3D10Multithread = context.cast().unwrap();
        // SAFETY: new private context, configured before use on the test's sole thread.
        let _ = unsafe { mt.SetMultithreadProtected(true) };
        Self {
            device: device.unwrap(),
            context,
        }
    }
    fn create(&self, config: &BaleToneMapConfig) -> Handle {
        let mut handle = ptr::null_mut();
        // SAFETY: live interfaces/record, exclusive writable output; serialized test ownership.
        let hr = unsafe { bale_tonemap_create(self.device.as_raw(), config, &mut handle) };
        assert_eq!(hr, S_OK, "create + self-test HRESULT=0x{:08x}", hr as u32);
        assert!(!handle.is_null());
        Handle(handle)
    }
    fn texture(&self, desc: &D3D11_TEXTURE2D_DESC, data: Option<&[u16]>) -> ID3D11Texture2D {
        let initial = data.map(|bytes| D3D11_SUBRESOURCE_DATA {
            pSysMem: bytes.as_ptr().cast(),
            SysMemPitch: desc.Width * 2,
            SysMemSlicePitch: 0,
        });
        let mut out = None;
        // SAFETY: initial data only supplied for a single slice and covers full P010 extent.
        unsafe {
            self.device
                .CreateTexture2D(desc, initial.as_ref().map(ptr::from_ref), Some(&mut out))
                .unwrap_or_else(|e| {
                    panic!(
                        "WARP texture descriptor {desc:?}: HRESULT=0x{:08x}",
                        e.code().0 as u32
                    )
                });
        }
        out.unwrap()
    }
    fn readback(&self, output: &ID3D11Texture2D) -> Vec<u8> {
        let mut d = D3D11_TEXTURE2D_DESC::default();
        // SAFETY: live interface and local descriptor storage.
        unsafe { output.GetDesc(&mut d) };
        d.Usage = D3D11_USAGE_STAGING;
        d.BindFlags = 0;
        d.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
        let read = self.texture(&d, None);
        let mut m = D3D11_MAPPED_SUBRESOURCE::default();
        // SAFETY: private compatible textures; Map waits for queued draws without Flush.
        unsafe {
            self.context.CopyResource(&read, output);
            self.context
                .Map(&read, 0, D3D11_MAP_READ, 0, Some(&mut m))
                .unwrap();
        }
        let _unmap = Unmap(&self.context, &read);
        assert!(!m.pData.is_null());
        assert!(m.RowPitch >= d.Width);
        // SAFETY: mapped NV12 extent is RowPitch*(Height + Height/2), at these tiny sizes.
        let data = unsafe {
            std::slice::from_raw_parts(
                m.pData.cast::<u8>(),
                (m.RowPitch * (d.Height + d.Height / 2)) as usize,
            )
        };
        data.chunks_exact(m.RowPitch as usize)
            .flat_map(|r| r[..d.Width as usize].iter().copied())
            .collect()
    }
}
struct Unmap<'a>(&'a ID3D11DeviceContext, &'a ID3D11Texture2D);
impl Drop for Unmap<'_> {
    fn drop(&mut self) {
        unsafe { self.0.Unmap(self.1, 0) };
    }
}
struct Handle(*mut ToneMapGpu);
impl Handle {
    fn apply(
        &self,
        warp: &Warp,
        src: &ID3D11Texture2D,
        slice: u32,
        dst: &ID3D11Texture2D,
        frame: &BaleToneMapFrameInfo,
    ) -> i32 {
        // SAFETY: live serialized handle and records, unmapped same-device textures owned by test.
        unsafe {
            bale_tonemap_apply(
                self.0,
                warp.context.as_raw(),
                src.as_raw(),
                slice,
                dst.as_raw(),
                frame,
            )
        }
    }
}
impl Drop for Handle {
    fn drop(&mut self) {
        unsafe { bale_tonemap_destroy(self.0) };
    }
}
fn config(curve: u32) -> BaleToneMapConfig {
    BaleToneMapConfig {
        width: 12,
        height: 6,
        curve,
        source_peak_nits: 1000.0,
        target_white_nits: 100.0,
        ..Default::default()
    }
}
fn desc(width: u32, height: u32, format: DXGI_FORMAT, bind: u32) -> D3D11_TEXTURE2D_DESC {
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
        BindFlags: bind,
        ..Default::default()
    }
}
fn output_desc(c: &BaleToneMapConfig) -> D3D11_TEXTURE2D_DESC {
    desc(
        c.width,
        c.height,
        DXGI_FORMAT_NV12,
        (D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE).0 as u32,
    )
}

#[test]
fn pq_to_nv12_matches_reference_for_both_curves_ranges_matrices_and_input_paths() {
    let warp = Warp::new();
    for curve in [1, 2] {
        for desat in [0.0, 2.0] {
            let c = BaleToneMapConfig {
                desat,
                ..config(curve)
            };
            let handle = warp.create(&c);
            // PQ from BT.709 and BT.2020, and BT.2020 SDR (transfer 14: gamut conversion only).
            for (transfer, matrix) in [(16, 1), (16, 9), (14, 9)] {
                for full_range in [0, 1] {
                    let f = BaleToneMapFrameInfo {
                        transfer,
                        primaries: matrix,
                        matrix,
                        full_range,
                        chroma_location: 1,
                        ..Default::default()
                    };
                    // A padded decoder picture; only the coded visible top-left is read.
                    // Size changes on the same handle exercise scratch recreation.
                    for (width, height) in [(16, 8), (20, 10)] {
                        let pattern =
                            cpu::test_pattern(width, height, matrix == 9, full_range == 1);
                        let expected = cpu::reference(&c, &f, &pattern, width, height).unwrap();
                        let output = warp.texture(&output_desc(&c), None);
                        for bind in [0, D3D11_BIND_SHADER_RESOURCE.0 as u32] {
                            let source = warp.texture(
                                &desc(width, height, DXGI_FORMAT_P010, bind),
                                Some(&pattern),
                            );
                            // WARP cannot create P010 arrays (including DECODER-bound arrays).
                            // Real decoder slice bounds are covered by gpu::tests; this exercises
                            // both the same copy operation and the in-place SRV shader path.
                            assert_eq!(handle.apply(&warp, &source, 0, &output, &f), S_OK);
                            let actual = warp.readback(&output);
                            for (i, (&a, &b)) in actual.iter().zip(&expected).enumerate() {
                                assert!(
                                    a.abs_diff(b) <= 3,
                                    "curve={curve} desat={desat} transfer={transfer} matrix={matrix} full={full_range} bind={bind} code={i}: GPU {a}, CPU {b}"
                                );
                            }
                        }
                    }
                }
            }
        }
    }
}

#[test]
fn invalid_arguments_leave_output_and_context_state_unchanged() {
    let warp = Warp::new();
    let c = config(1);
    let handle = warp.create(&c);
    let f = BaleToneMapFrameInfo {
        transfer: 16,
        primaries: 9,
        matrix: 9,
        ..Default::default()
    };
    let input = warp.texture(
        &desc(16, 8, DXGI_FORMAT_P010, 0),
        Some(&cpu::test_pattern(16, 8, true, false)),
    );
    let output = warp.texture(&output_desc(&c), None);
    let viewport = D3D11_VIEWPORT {
        TopLeftX: 3.0,
        TopLeftY: 4.0,
        Width: 5.0,
        Height: 6.0,
        MinDepth: 0.1,
        MaxDepth: 0.9,
    };
    unsafe { warp.context.RSSetViewports(Some(&[viewport])) };
    assert_eq!(handle.apply(&warp, &input, 0, &output, &f), S_OK);
    let before = warp.readback(&output);
    for bad in [
        BaleToneMapFrameInfo { transfer: 18, ..f },
        BaleToneMapFrameInfo { primaries: 1, ..f },
        BaleToneMapFrameInfo { full_range: 2, ..f },
    ] {
        assert_eq!(handle.apply(&warp, &input, 0, &output, &bad), E_INVALIDARG);
        assert_eq!(warp.readback(&output), before);
    }
    assert_eq!(handle.apply(&warp, &input, 1, &output, &f), E_INVALIDARG);
    assert_eq!(warp.readback(&output), before);
    let mut actual = [D3D11_VIEWPORT::default()];
    let mut n = 1;
    unsafe {
        warp.context
            .RSGetViewports(&mut n, Some(actual.as_mut_ptr()))
    };
    assert_eq!(actual[0], viewport);
    let foreign = Warp::new();
    let foreign_output = foreign.texture(&output_desc(&c), None);
    assert_eq!(
        handle.apply(&warp, &input, 0, &foreign_output, &f),
        E_INVALIDARG
    );
    assert_eq!(handle.apply(&foreign, &input, 0, &output, &f), E_INVALIDARG);
    let invalid = BaleToneMapConfig {
        source_peak_nits: 0.0,
        ..c
    };
    let mut ptr = std::ptr::dangling_mut();
    assert_eq!(
        unsafe { bale_tonemap_create(warp.device.as_raw(), &invalid, &mut ptr) },
        E_INVALIDARG
    );
    assert!(ptr.is_null());
    unsafe { bale_tonemap_destroy(ptr::null_mut()) };
}
