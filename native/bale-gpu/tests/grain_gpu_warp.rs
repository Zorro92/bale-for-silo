//! Real NV12/P010 plane views and embedded SM5 shaders on WARP; no feature skips.
#![cfg(windows)]

mod common;

use std::ptr;

use bale_gpu::grain::BaleGrainParams;
use bale_gpu::grain::ffi::{
    E_INVALIDARG, S_OK, bale_grain_gpu_apply, bale_grain_gpu_create, bale_grain_gpu_destroy,
};
use bale_gpu::grain::gpu::BaleGrainGpu;
use windows::Win32::Foundation::HMODULE;
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
            .expect("WARP FL11_0 is required; GPU grain tests must not skip");
        }
        let device = device.expect("WARP device");
        let context = context.expect("WARP immediate context");
        let multithread: ID3D10Multithread =
            context.cast().expect("WARP must expose ID3D10Multithread");
        // SAFETY: configure the newly created private context before any other use.
        let _ = unsafe { multithread.SetMultithreadProtected(true) };
        Self { device, context }
    }

    fn grain(&self) -> Grain {
        let mut handle = ptr::null_mut();
        // SAFETY: live COM interfaces and writable out slot, borrowed on this one test thread.
        let hr = unsafe {
            bale_grain_gpu_create(self.device.as_raw(), self.context.as_raw(), &mut handle)
        };
        assert_eq!(
            hr, S_OK,
            "GPU create (including NV12/P010, explicit UV + csfl self-tests) must succeed on WARP: HRESULT=0x{:08x}",
            hr as u32
        );
        assert!(!handle.is_null());
        Grain(handle)
    }

    fn texture(&self, desc: &D3D11_TEXTURE2D_DESC, data: Option<&[u8]>) -> ID3D11Texture2D {
        let pitch = desc.Width
            * if desc.Format == DXGI_FORMAT_P010 {
                2
            } else {
                1
            };
        let initial = data.map(|bytes| {
            let rows = desc.Height
                + if matches!(desc.Format, DXGI_FORMAT_NV12 | DXGI_FORMAT_P010) {
                    desc.Height / 2
                } else {
                    0
                };
            assert_eq!(bytes.len(), (pitch * rows) as usize);
            D3D11_SUBRESOURCE_DATA {
                pSysMem: bytes.as_ptr().cast(),
                SysMemPitch: pitch,
                SysMemSlicePitch: 0,
            }
        });
        let mut texture = None;
        // SAFETY: descriptor has one subresource; data (when present) covers its full planar
        // extent with the declared pitch and stays live through this synchronous copy.
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
        assert!(matches!(desc.Format, DXGI_FORMAT_NV12 | DXGI_FORMAT_P010));
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
        let _unmap = Unmap {
            context: &self.context,
            texture: &staging,
        };
        let row_bytes = desc.Width as usize
            * if desc.Format == DXGI_FORMAT_P010 {
                2
            } else {
                1
            };
        let rows = (desc.Height + desc.Height / 2) as usize;
        assert!(!mapped.pData.is_null());
        assert!(mapped.RowPitch as usize >= row_bytes);
        // SAFETY: NV12/P010 maps have Height luma rows then Height/2 chroma rows at RowPitch
        // each. The tiny test dimensions keep this complete extent below isize::MAX.
        let data = unsafe {
            std::slice::from_raw_parts(mapped.pData.cast::<u8>(), mapped.RowPitch as usize * rows)
        };
        data.chunks_exact(mapped.RowPitch as usize)
            .flat_map(|row| row[..row_bytes].iter().copied())
            .collect()
    }
}

struct Grain(*mut BaleGrainGpu);

impl Grain {
    fn apply(&self, params: &BaleGrainParams, src: &ID3D11Texture2D, dst: &ID3D11Texture2D) -> i32 {
        // SAFETY: this test owns the handle exclusively on its creating thread, with live
        // stable parameters and live, unmapped same-device textures for the duration.
        unsafe { bale_grain_gpu_apply(self.0, params, src.as_raw(), dst.as_raw()) }
    }
}

impl Drop for Grain {
    fn drop(&mut self) {
        // SAFETY: unique live handle returned by create, destroyed exactly once on this thread.
        unsafe { bale_grain_gpu_destroy(self.0) };
    }
}

struct Unmap<'a> {
    context: &'a ID3D11DeviceContext,
    texture: &'a ID3D11Texture2D,
}

impl Drop for Unmap<'_> {
    fn drop(&mut self) {
        // SAFETY: guard is constructed only after a successful Map of subresource zero.
        unsafe { self.context.Unmap(self.texture, 0) };
    }
}

fn desc(
    width: u32,
    height: u32,
    format: DXGI_FORMAT,
    staging: bool,
    bind: u32,
) -> D3D11_TEXTURE2D_DESC {
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
        Usage: if staging {
            D3D11_USAGE_STAGING
        } else {
            D3D11_USAGE_DEFAULT
        },
        BindFlags: bind,
        CPUAccessFlags: if staging {
            D3D11_CPU_ACCESS_WRITE.0 as u32
        } else {
            0
        },
        ..Default::default()
    }
}

fn fixture_desc(params: &BaleGrainParams, staging: bool) -> D3D11_TEXTURE2D_DESC {
    desc(
        params.width.next_multiple_of(2),
        params.height.next_multiple_of(2),
        if params.bit_depth == 8 {
            DXGI_FORMAT_NV12
        } else {
            DXGI_FORMAT_P010
        },
        staging,
        if staging {
            0
        } else {
            (D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE).0 as u32
        },
    )
}

fn packed(planar: &[u8], params: &BaleGrainParams) -> Vec<u8> {
    let w = params.width as usize;
    let h = params.height as usize;
    let (cw, ch) = (w.div_ceil(2), h.div_ceil(2));
    let (tw, th) = (w.next_multiple_of(2), h.next_multiple_of(2));
    let bytes = if params.bit_depth == 8 { 1 } else { 2 };
    let pitch = tw * bytes;
    let mut result = vec![0xa5; pitch * (th + th / 2)];
    for plane in 0..3 {
        let (pw, ph, offset) = if plane == 0 {
            (w, h, 0)
        } else {
            (cw, ch, w * h + (plane - 1) * cw * ch)
        };
        for y in 0..ph {
            for x in 0..pw {
                let sample = common::planar_sample(planar, offset + y * pw + x, params.bit_depth);
                let index = if plane == 0 {
                    y * pitch + x * bytes
                } else {
                    th * pitch + y * pitch + (x * 2 + plane - 1) * bytes
                };
                if bytes == 1 {
                    result[index] = sample as u8;
                } else {
                    result[index..index + 2].copy_from_slice(&(sample << 6).to_le_bytes());
                }
            }
        }
    }
    result
}

fn assert_bytes(
    actual: &[u8],
    expected: &[u8],
    name: &str,
    frame: usize,
    params: &BaleGrainParams,
) {
    assert_eq!(actual.len(), expected.len());
    let bytes = if params.bit_depth == 8 { 1 } else { 2 };
    let pitch = params.width.next_multiple_of(2) as usize * bytes;
    let height = params.height.next_multiple_of(2) as usize;
    for (index, (&a, &e)) in actual.iter().zip(expected).enumerate() {
        let row = index / pitch;
        let plane = if row < height { "Y" } else { "UV" };
        let y = if row < height { row } else { row - height };
        assert_eq!(
            a,
            e,
            "{name} frame={frame} plane={plane} x_word={} byte={} y={y}: expected=0x{e:02x} actual=0x{a:02x} (including padding)",
            (index % pitch) / bytes,
            (index % pitch) % bytes
        );
    }
}

#[test]
fn all_44_fixtures_match_dav1d_on_warp() {
    let warp = Warp::new();
    let grain = warp.grain();
    println!(
        "WARP create self-test passed: NV12 + P010, explicit UV + csfl, visible 63x61, overlap and padding"
    );
    let mut cases = 0;
    let mut frames = 0;
    for fixture in common::fixtures() {
        for (frame, data) in fixture.frames.iter().enumerate() {
            let plain = packed(&data.plain, &data.params);
            let expected = packed(&data.grained, &data.params);
            let src = warp.texture(&fixture_desc(&data.params, true), Some(&plain));
            // Different initial padding proves the shaders write pass-through pixels too.
            let dst = warp.texture(
                &fixture_desc(&data.params, false),
                Some(&vec![0x69; plain.len()]),
            );
            let hr = grain.apply(&data.params, &src, &dst);
            assert_eq!(
                hr, S_OK,
                "{} frame={frame}: apply HRESULT=0x{:08x}",
                fixture.name, hr as u32
            );
            assert_bytes(
                &warp.readback(&dst),
                &expected,
                &fixture.name,
                frame,
                &data.params,
            );
            assert_bytes(
                &warp.readback(&src),
                &plain,
                "source must remain unchanged",
                frame,
                &data.params,
            );
            frames += 1;
        }
        cases += 1;
    }
    assert_eq!(cases, 44, "do not silently drop GPU fixtures");
    assert_eq!(frames, 88);
    println!(
        "WARP: all 44 fixtures / 88 frames passed byte-for-byte against dav1d; padding and sources unchanged"
    );
}

fn assert_state(
    warp: &Warp,
    viewport: D3D11_VIEWPORT,
    shader: &ID3D11PixelShader,
    target: &ID3D11RenderTargetView,
) {
    let mut count = 1;
    let mut actual_viewport = D3D11_VIEWPORT::default();
    let mut actual_shader = None;
    let mut actual_targets = [None];
    // SAFETY: writable local outputs, live private immediate context; queries submit no work.
    unsafe {
        warp.context
            .RSGetViewports(&mut count, Some(&mut actual_viewport));
        warp.context.PSGetShader(&mut actual_shader, None, None);
        warp.context
            .OMGetRenderTargets(Some(&mut actual_targets), None);
    }
    assert_eq!(count, 1);
    assert_eq!(actual_viewport, viewport);
    assert_eq!(actual_shader.as_ref(), Some(shader));
    assert_eq!(actual_targets[0].as_ref(), Some(target));
}

#[test]
fn create_and_apply_preserve_the_callers_pipeline_state() {
    let warp = Warp::new();
    let mut shader = None;
    let mut target = None;
    let target_texture = warp.texture(
        &desc(
            64,
            64,
            DXGI_FORMAT_R8_UINT,
            false,
            D3D11_BIND_RENDER_TARGET.0 as u32,
        ),
        None,
    );
    // SAFETY: embedded real shader, live same-device renderable texture and local output slots.
    unsafe {
        warp.device
            .CreatePixelShader(
                include_bytes!(concat!(env!("OUT_DIR"), "/grain_luma_ps.cso")),
                None,
                Some(&mut shader),
            )
            .unwrap();
        warp.device
            .CreateRenderTargetView(&target_texture, None, Some(&mut target))
            .unwrap();
    }
    let shader = shader.unwrap();
    let target = target.unwrap();
    let viewport = D3D11_VIEWPORT {
        TopLeftX: 3.0,
        TopLeftY: 5.0,
        Width: 17.0,
        Height: 23.0,
        MinDepth: 0.2,
        MaxDepth: 0.8,
    };
    // SAFETY: all state belongs to this private context/device and remains live through the test.
    unsafe {
        warp.context.RSSetViewports(Some(&[viewport]));
        warp.context.PSSetShader(&shader, None);
        warp.context
            .OMSetRenderTargets(Some(&[Some(target.clone())]), None);
    }
    let grain = warp.grain();
    assert_state(&warp, viewport, &shader, &target);
    let fixtures = common::fixtures();
    let data = &fixtures[0].frames[0];
    let plain = packed(&data.plain, &data.params);
    let src = warp.texture(&fixture_desc(&data.params, true), Some(&plain));
    let dst = warp.texture(&fixture_desc(&data.params, false), None);
    for _ in 0..2 {
        assert_eq!(grain.apply(&data.params, &src, &dst), S_OK);
        assert_state(&warp, viewport, &shader, &target);
    }
    drop(grain);
    assert_state(&warp, viewport, &shader, &target);
    println!("WARP state preservation passed: create self-test, repeated apply and destroy");
}

#[test]
fn invalid_arguments_do_not_write_textures() {
    let warp = Warp::new();
    let grain = warp.grain();
    let fixtures = common::fixtures();
    let data = &fixtures[0].frames[0];
    let p = &data.params;
    assert_eq!(p.bit_depth, 8);
    let plain = packed(&data.plain, p);
    let source_desc = fixture_desc(p, true);
    let target_desc = fixture_desc(p, false);
    let src = warp.texture(&source_desc, Some(&plain));
    let dst = warp.texture(&target_desc, Some(&plain));
    let mut bad_size = source_desc;
    bad_size.Width += 2;
    let bad_size = warp.texture(&bad_size, None);
    let mut bad_format = source_desc;
    bad_format.Format = DXGI_FORMAT_P010;
    let bad_format = warp.texture(&bad_format, None);
    let mut odd_size = source_desc;
    odd_size.Width -= 1;
    // D3D itself disallows odd NV12/P010 resources. Exercise rejection through the ABI with
    // a creatable odd R8 texture; gpu.rs also tests odd planar descriptors directly.
    odd_size.Format = DXGI_FORMAT_R8_UINT;
    let odd_size = warp.texture(&odd_size, None);
    for bad in [&bad_size, &bad_format, &odd_size] {
        assert_eq!(grain.apply(p, bad, &dst), E_INVALIDARG);
        assert_bytes(
            &warp.readback(&dst),
            &plain,
            "invalid source must not write destination",
            0,
            p,
        );
    }
    let mut no_bind = target_desc;
    no_bind.BindFlags = D3D11_BIND_SHADER_RESOURCE.0 as u32;
    let no_bind = warp.texture(&no_bind, Some(&plain));
    assert_eq!(grain.apply(p, &src, &no_bind), E_INVALIDARG);
    assert_bytes(
        &warp.readback(&no_bind),
        &plain,
        "missing render-target bind",
        0,
        p,
    );
    assert_eq!(grain.apply(p, &dst, &dst), E_INVALIDARG);
    for mutate in [
        (|p: &mut BaleGrainParams| p.struct_size = 0) as fn(&mut BaleGrainParams),
        |p| p.bit_depth = 10,
        |p| p.width += 2,
        |p| p.height += 2,
        |p| p.overlap_flag = 2,
    ] {
        let mut bad = *p;
        mutate(&mut bad);
        assert_eq!(grain.apply(&bad, &src, &dst), E_INVALIDARG);
        assert_bytes(
            &warp.readback(&dst),
            &plain,
            "invalid parameters must not write",
            0,
            p,
        );
    }
    // SAFETY: nulls are explicitly rejected. Other arguments remain live, unmapped and
    // exclusively owned. Destroy(null) is specified as a no-op.
    unsafe {
        for (handle, params, source, target) in [
            (ptr::null_mut(), p as *const _, src.as_raw(), dst.as_raw()),
            (grain.0, ptr::null(), src.as_raw(), dst.as_raw()),
            (grain.0, p, ptr::null_mut(), dst.as_raw()),
            (grain.0, p, src.as_raw(), ptr::null_mut()),
        ] {
            assert_eq!(
                bale_grain_gpu_apply(handle, params, source, target),
                E_INVALIDARG
            );
        }
        bale_grain_gpu_destroy(ptr::null_mut());
    }
    assert_bytes(
        &warp.readback(&dst),
        &plain,
        "null/alias arguments must not write",
        0,
        p,
    );
    assert_bytes(
        &warp.readback(&src),
        &plain,
        "argument rejection preserves source",
        0,
        p,
    );
    println!("WARP invalid-argument rejection passed; no texture writes");
}

#[test]
fn create_clears_output_on_failure() {
    let warp = Warp::new();
    // A never-dereferenced sentinel proves the failure path initializes the output.
    let sentinel = ptr::dangling_mut::<BaleGrainGpu>();
    // SAFETY: null interface arguments are rejected; non-null interfaces/output locals are live.
    unsafe {
        for (device, context) in [
            (ptr::null_mut(), warp.context.as_raw()),
            (warp.device.as_raw(), ptr::null_mut()),
        ] {
            let mut out = sentinel;
            assert_eq!(
                bale_grain_gpu_create(device, context, &mut out),
                E_INVALIDARG
            );
            assert!(out.is_null());
        }
        assert_eq!(
            bale_grain_gpu_create(warp.device.as_raw(), warp.context.as_raw(), ptr::null_mut()),
            E_INVALIDARG
        );
    }
}
