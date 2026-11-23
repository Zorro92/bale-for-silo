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
    fn texture8(&self, desc: &D3D11_TEXTURE2D_DESC, data: &[u8]) -> ID3D11Texture2D {
        let initial = D3D11_SUBRESOURCE_DATA {
            pSysMem: data.as_ptr().cast(),
            SysMemPitch: desc.Width,
            SysMemSlicePitch: 0,
        };
        let mut out = None;
        // SAFETY: initial data covers the full single-slice NV12 extent (Width bytes per row).
        unsafe {
            self.device
                .CreateTexture2D(desc, Some(&initial), Some(&mut out))
                .unwrap_or_else(|e| {
                    panic!(
                        "WARP NV12 texture {desc:?}: HRESULT=0x{:08x}",
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
        // P010 rows are two bytes per sample.
        let row_bytes = if d.Format == DXGI_FORMAT_P010 {
            d.Width * 2
        } else {
            d.Width
        };
        assert!(!m.pData.is_null());
        assert!(m.RowPitch >= row_bytes);
        // SAFETY: mapped NV12 extent is RowPitch*(Height + Height/2), at these tiny sizes.
        let data = unsafe {
            std::slice::from_raw_parts(
                m.pData.cast::<u8>(),
                (m.RowPitch * (d.Height + d.Height / 2)) as usize,
            )
        };
        data.chunks_exact(m.RowPitch as usize)
            .flat_map(|r| r[..row_bytes as usize].iter().copied())
            .collect()
    }
    /// P010 readback as unshifted 10-bit codes; the six padding bits must be zero.
    fn readback_p010(&self, output: &ID3D11Texture2D) -> Vec<u16> {
        self.readback(output)
            .as_chunks::<2>()
            .0
            .iter()
            .map(|&b| {
                let word = u16::from_le_bytes(b);
                assert_eq!(word & 0x3F, 0, "P010 padding bits");
                word >> 6
            })
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
    output_desc_as(c, DXGI_FORMAT_NV12)
}
fn output_desc_as(c: &BaleToneMapConfig, format: DXGI_FORMAT) -> D3D11_TEXTURE2D_DESC {
    desc(
        c.width,
        c.height,
        format,
        (D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE).0 as u32,
    )
}

/// NV12 output is ordered-dithered exactly like the CPU reference (at most one code apart, from
/// float differences at a threshold), resolves a slow ramp into more levels than plain rounding,
/// and is the same on a second apply (static pattern: still content stays still). P010 output
/// keeps the plain-rounded 10-bit result.
#[test]
fn nv12_output_is_dithered_like_the_reference_and_p010_is_unchanged() {
    assert_ne!(
        bale_tonemap_output_formats() & BALE_TONEMAP_OUTPUT_NV12_DITHERED,
        0
    );
    let warp = Warp::new();
    let (w, h) = (256u32, 16u32);
    let sdr = BaleToneMapFrameInfo {
        transfer: 1,
        primaries: 1,
        matrix: 1,
        ..Default::default()
    };
    let pq = BaleToneMapFrameInfo {
        transfer: 16,
        primaries: 9,
        matrix: 9,
        ..Default::default()
    };
    let ramp = cpu::sdr_ramp(w, h, 400.0, 416.0);
    let pattern = cpu::test_pattern(w, h, true, false);
    for (curve, reserved) in [(1, [0; 4]), (2, [0; 4]), (1, [512, 32, 0, 0])] {
        let c = BaleToneMapConfig {
            width: w,
            height: h,
            reserved,
            ..config(curve)
        };
        // The downscale case reads a 512x32 picture: same ramp, twice the size.
        let (iw, ih) = c.source_size();
        let inputs = if reserved[0] == 0 {
            [(&sdr, ramp.clone()), (&pq, pattern.clone())]
        } else {
            [
                (&sdr, cpu::sdr_ramp(iw, ih, 400.0, 416.0)),
                (&pq, cpu::test_pattern(iw, ih, true, false)),
            ]
        };
        let handle = warp.create(&c);
        for (f, input) in inputs {
            let source = warp.texture(
                &desc(
                    iw,
                    ih,
                    DXGI_FORMAT_P010,
                    D3D11_BIND_SHADER_RESOURCE.0 as u32,
                ),
                Some(&input),
            );
            let nv12 = warp.texture(&output_desc(&c), None);
            assert_eq!(handle.apply(&warp, &source, 0, &nv12, f), S_OK);
            let actual = warp.readback(&nv12);
            let expected = cpu::reference(&c, f, &input, iw, ih).unwrap();
            // The smooth SDR ramp only differs by a threshold flip; the PQ pattern's saturated
            // edges keep the tone-map tests' tolerance (3 codes; 12 at 10 bits, as the self-test).
            let (tolerance, tolerance10) = if f.transfer == 1 { (1, 1) } else { (3, 12) };
            assert_eq!(actual.len(), expected.len());
            for (i, (&a, &b)) in actual.iter().zip(&expected).enumerate() {
                assert!(
                    a.abs_diff(b) <= tolerance,
                    "NV12 curve={curve} transfer={} code={i}: GPU {a}, CPU {b}",
                    f.transfer
                );
            }
            assert_eq!(handle.apply(&warp, &source, 0, &nv12, f), S_OK);
            assert_eq!(warp.readback(&nv12), actual, "the dither is static");
            let p010 = warp.texture(&output_desc_as(&c, DXGI_FORMAT_P010), None);
            assert_eq!(handle.apply(&warp, &source, 0, &p010, f), S_OK);
            let actual10 = warp.readback_p010(&p010);
            let expected10 = cpu::reference_p010(&c, f, &input, iw, ih).unwrap();
            assert_eq!(actual10.len(), expected10.len());
            for (i, (&a, &b)) in actual10.iter().zip(&expected10).enumerate() {
                assert!(
                    a.abs_diff(b) <= tolerance10,
                    "P010 curve={curve} transfer={} code={i}: GPU {a}, CPU {b}",
                    f.transfer
                );
            }
            if f.transfer == 1 {
                // On the ramp, 8x8-block sums of the dithered luma resolve the 10-bit steps that
                // plain rounding of the P010 result collapses into a few bands.
                let levels = |codes: Vec<u32>| {
                    let mut sums: Vec<u32> = (0..w as usize)
                        .step_by(8)
                        .map(|x| {
                            (0..8)
                                .flat_map(|y| (0..8).map(move |dx| (y, x + dx)))
                                .map(|(y, x)| codes[y * w as usize + x])
                                .sum()
                        })
                        .collect();
                    sums.dedup();
                    sums.len()
                };
                let dithered = levels(actual.iter().map(|&a| u32::from(a)).collect());
                let plain = levels(actual10.iter().map(|&b| (u32::from(b) + 2) / 4).collect());
                assert!(dithered >= 3 * plain, "{dithered} vs {plain} levels");
            }
        }
    }
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
            for (transfer, matrix) in [(16, 1), (16, 9), (14, 9), (18, 9)] {
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
fn area_downscale_matches_reference_at_two_to_one_and_uneven_ratios() {
    let warp = Warp::new();
    for curve in [1, 2] {
        // 24x12 visible source (in a padded 28x14 picture) to 12x6 (2:1) and 10x4 (2.4:1, 3:1).
        for (width, height) in [(12, 6), (10, 4)] {
            let c = BaleToneMapConfig {
                width,
                height,
                reserved: [24, 12, 0, 0],
                ..config(curve)
            };
            let handle = warp.create(&c);
            let f = BaleToneMapFrameInfo {
                transfer: 16,
                primaries: 9,
                matrix: 9,
                ..Default::default()
            };
            let pattern = cpu::test_pattern(28, 14, true, false);
            let expected = cpu::reference(&c, &f, &pattern, 28, 14).unwrap();
            let source = warp.texture(
                &desc(
                    28,
                    14,
                    DXGI_FORMAT_P010,
                    D3D11_BIND_SHADER_RESOURCE.0 as u32,
                ),
                Some(&pattern),
            );
            let output = warp.texture(&output_desc(&c), None);
            assert_eq!(handle.apply(&warp, &source, 0, &output, &f), S_OK);
            let actual = warp.readback(&output);
            assert_eq!(actual.len(), expected.len());
            for (i, (&a, &b)) in actual.iter().zip(&expected).enumerate() {
                assert!(
                    a.abs_diff(b) <= 3,
                    "curve={curve} {width}x{height} code={i}: GPU {a}, CPU {b}"
                );
            }
        }
    }
}

#[test]
fn nv12_bt709_sdr_scale_only_downscale_matches_reference() {
    let warp = Warp::new();
    // 8-bit BT.709 SDR in a padded 28x14 NV12 picture, visible 24x12, to 12x6 and 10x4.
    let words = cpu::test_pattern(28, 14, false, false);
    let bytes: Vec<u8> = words.iter().map(|&w| (w >> 8) as u8).collect();
    // The CPU reference reads P010 words; an NV12 code c is the 10-bit code c << 2, i.e. word c << 8.
    let as_p010: Vec<u16> = bytes.iter().map(|&b| u16::from(b) << 8).collect();
    let f = BaleToneMapFrameInfo {
        transfer: 1,
        primaries: 1,
        matrix: 1,
        ..Default::default()
    };
    for (width, height, reserved) in [
        (12, 6, [24, 12, 0, 0]),
        (10, 4, [24, 12, 0, 0]),
        (24, 12, [0; 4]),
    ] {
        let c = BaleToneMapConfig {
            width,
            height,
            reserved,
            ..config(1)
        };
        let handle = warp.create(&c);
        let expected = cpu::reference(&c, &f, &as_p010, 28, 14).unwrap();
        let source = warp.texture8(
            &desc(
                28,
                14,
                DXGI_FORMAT_NV12,
                D3D11_BIND_SHADER_RESOURCE.0 as u32,
            ),
            &bytes,
        );
        let output = warp.texture(&output_desc(&c), None);
        assert_eq!(handle.apply(&warp, &source, 0, &output, &f), S_OK);
        let actual = warp.readback(&output);
        assert_eq!(actual.len(), expected.len());
        for (i, (&a, &b)) in actual.iter().zip(&expected).enumerate() {
            assert!(
                a.abs_diff(b) <= 3,
                "{width}x{height} code={i}: GPU {a}, CPU {b}"
            );
        }
    }
}

/// Representative picture: the self-test's saturated 2x2 colour blocks (sharp chroma edges) in the
/// top half, a luma ramp with pseudo-random detail and slowly turning chroma in the bottom half.
/// Limited-range P010 words; an NV12 copy takes each word's high byte.
fn mixed_pattern(width: u32, height: u32, bt2020: bool) -> Vec<u16> {
    let (w, h) = (width as usize, height as usize);
    let mut data = cpu::test_pattern(width, height, bt2020, false);
    let mut seed = 0x2545_f491_u32;
    let mut noise = |range: u32| {
        seed ^= seed << 13;
        seed ^= seed >> 17;
        seed ^= seed << 5;
        seed % range
    };
    for y in h / 2..h {
        for x in 0..w {
            let ramp = 64 + (876 * x / w.max(1)) as u32;
            data[y * w + x] = ((ramp + noise(48)).clamp(64, 940) as u16) << 6;
        }
    }
    for y in h / 4..h / 2 {
        for x in 0..w / 2 {
            let u = 512 + ((x * 7 + y * 3) % 400) as u32 - 200 + noise(16);
            let v = 512 + ((x * 3 + y * 5) % 400) as u32 - 200 + noise(16);
            data[w * h + y * w + 2 * x] = (u.clamp(64, 960) as u16) << 6;
            data[w * h + y * w + 2 * x + 1] = (v.clamp(64, 960) as u16) << 6;
        }
    }
    data
}

/// The staged downscale (one area pass into an fp32 intermediate, plane passes read it) must give
/// the same codes as the per-plane path it replaces (each plane evaluating the area sum itself).
/// Both run on one handle; set_staged_downscale switches between them. Prints one summary line
/// per case (`staged identity:`) for the CI log; allows at most one code of float noise.
#[test]
fn staged_downscale_matches_the_per_plane_path() {
    let warp = Warp::new();
    let frames = [
        (
            "pq",
            BaleToneMapFrameInfo {
                transfer: 16,
                primaries: 9,
                matrix: 9,
                ..Default::default()
            },
        ),
        (
            "sdr709",
            BaleToneMapFrameInfo {
                transfer: 1,
                primaries: 1,
                matrix: 1,
                ..Default::default()
            },
        ),
        (
            "hlg",
            BaleToneMapFrameInfo {
                transfer: 18,
                primaries: 9,
                matrix: 9,
                ..Default::default()
            },
        ),
    ];
    let mut worst = 0u16;
    let mut summary = Vec::new();
    for (sw, sh, w, h) in [
        (3840, 2160, 1920, 1080),
        (3840, 2160, 1280, 720),
        (1920, 1080, 1280, 720),
    ] {
        let c = BaleToneMapConfig {
            width: w,
            height: h,
            reserved: [sw, sh, 0, 0],
            ..config(1)
        };
        let handle = warp.create(&c);
        // SAFETY (each deref below): the test owns the live handle; no apply is in flight, and the
        // reference ends with the call.
        assert!(
            unsafe { (*handle.0).staged_downscale() },
            "downscale creates the intermediate"
        );
        for (name, f) in &frames {
            let pattern = mixed_pattern(sw, sh, f.matrix == 9);
            let bytes: Vec<u8> = pattern.iter().map(|&w| (w >> 8) as u8).collect();
            let bind = D3D11_BIND_SHADER_RESOURCE.0 as u32;
            for input in ["p010", "nv12"] {
                let source = match input {
                    "p010" => warp.texture(&desc(sw, sh, DXGI_FORMAT_P010, bind), Some(&pattern)),
                    _ => warp.texture8(&desc(sw, sh, DXGI_FORMAT_NV12, bind), &bytes),
                };
                for (output, format) in [("nv12", DXGI_FORMAT_NV12), ("p010", DXGI_FORMAT_P010)] {
                    let target = warp.texture(&output_desc_as(&c, format), None);
                    let render = |staged: bool| {
                        unsafe { (*handle.0).set_staged_downscale(staged) }.unwrap();
                        assert_eq!(unsafe { (*handle.0).staged_downscale() }, staged);
                        assert_eq!(handle.apply(&warp, &source, 0, &target, f), S_OK);
                        if format == DXGI_FORMAT_P010 {
                            warp.readback_p010(&target)
                        } else {
                            warp.readback(&target).into_iter().map(u16::from).collect()
                        }
                    };
                    let old = render(false);
                    let new = render(true);
                    assert_eq!(old.len(), new.len());
                    let differing = old.iter().zip(&new).filter(|(a, b)| a != b).count();
                    let max = old
                        .iter()
                        .zip(&new)
                        .map(|(a, b)| a.abs_diff(*b))
                        .max()
                        .unwrap_or(0);
                    worst = worst.max(max);
                    let line = format!(
                        "staged identity: {sw}x{sh}->{w}x{h} {name} in={input} out={output}: \
                         {differing} of {} codes differ, max {max}",
                        old.len()
                    );
                    println!("{line}");
                    summary.push(line);
                }
            }
        }
    }
    assert!(
        worst <= 1,
        "staged downscale changed the output:\n{}",
        summary.join("\n")
    );
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
        BaleToneMapFrameInfo { transfer: 17, ..f },
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

/// Exercise the real query lifetime across context-state swaps and asynchronous apply calls.
/// Only the test's texture readback waits; the instrumented apply never flushes or waits.
#[cfg(feature = "stage-timing")]
#[test]
fn stage_timestamps_exclude_self_test_and_survive_state_restore() {
    struct Disable;
    impl Drop for Disable {
        fn drop(&mut self) {
            assert_eq!(bale_tonemap_debug_timing_enable(0), S_OK);
        }
    }
    fn snapshot() -> BaleToneMapTimingStats {
        let mut stats = BaleToneMapTimingStats::default();
        // SAFETY: writable complete snapshot, no retained pointer or concurrent stage calls.
        assert_eq!(unsafe { bale_tonemap_debug_timing_read(&mut stats) }, S_OK);
        stats
    }
    let warp = Warp::new();
    let c = config(1);
    let frame = BaleToneMapFrameInfo {
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
    assert_eq!(bale_tonemap_debug_timing_enable(1), S_OK);
    let _disable = Disable;
    let handle = warp.create(&c);
    // Captured once per create: turning the switch off must not stop this handle's measurement.
    assert_eq!(bale_tonemap_debug_timing_enable(0), S_OK);
    let stats = snapshot();
    assert_eq!(stats.state, 1, "timestamp query creation failed: {stats:?}");
    assert_eq!((stats.window, stats.frames, stats.valid), (0, 0, 0));
    for _ in 0..244 {
        assert_eq!(handle.apply(&warp, &input, 0, &output, &frame), S_OK);
        // Synchronise in the TEST so subsequent applies can read old query results deterministically.
        let _pixels = warp.readback(&output);
    }
    let stats = snapshot();
    assert_eq!((stats.state, stats.window, stats.frames), (1, 1, 240));
    assert_eq!(
        stats.valid + stats.disjoint + stats.unavailable,
        stats.frames
    );
    assert!(stats.valid > 0, "no reliable WARP timestamps: {stats:?}");
    assert!(stats.pending <= 1);
    assert!(stats.avg_ms.iter().all(|ms| ms.is_finite() && *ms >= 0.0));
    assert!(
        stats
            .avg_ms
            .iter()
            .zip(stats.max_ms)
            .all(|(avg, max)| *avg <= max)
    );
    drop(handle);
    let final_stats = snapshot();
    assert_eq!((final_stats.state, final_stats.pending), (3, 0));
    assert!(final_stats.window > stats.window);
    // Off-by-default create owns no queries and must not replace the last measured session.
    let _off = warp.create(&c);
    assert_eq!(snapshot().session, final_stats.session);
}

#[cfg(feature = "stage-timing")]
#[test]
fn stage_timestamps_survive_a_burst_without_per_apply_readback() {
    struct Disable;
    impl Drop for Disable {
        fn drop(&mut self) {
            assert_eq!(bale_tonemap_debug_timing_enable(0), S_OK);
        }
    }
    let warp = Warp::new();
    let c = config(1);
    let input = warp.texture(
        &desc(16, 8, DXGI_FORMAT_P010, D3D11_BIND_SHADER_RESOURCE.0 as u32),
        Some(&cpu::test_pattern(16, 8, true, false)),
    );
    let output = warp.texture(&output_desc_as(&c, DXGI_FORMAT_P010), None);
    let frame = BaleToneMapFrameInfo {
        transfer: 16,
        primaries: 9,
        matrix: 9,
        ..Default::default()
    };
    assert_eq!(bale_tonemap_debug_timing_enable(1), S_OK);
    let _disable = Disable;
    let handle = warp.create(&c);
    // The old test read back after every apply, masking unresolved-query behavior. Submit a
    // shader-readable P010 burst with state restoration but no Flush or readback between frames.
    for _ in 0..244 {
        assert_eq!(handle.apply(&warp, &input, 0, &output, &frame), S_OK);
    }
    let mut stats = BaleToneMapTimingStats::default();
    // SAFETY: complete writable snapshot; no GPU queries or caller state are touched by read.
    assert_eq!(unsafe { bale_tonemap_debug_timing_read(&mut stats) }, S_OK);
    assert_eq!((stats.state, stats.window, stats.frames), (1, 1, 240));
    assert!(
        stats.pending <= 1,
        "more than one unresolved disjoint interval: {stats:?}"
    );
    assert!(
        stats.unavailable > 0,
        "busy intervals must skip measurement: {stats:?}"
    );
    assert_eq!(
        stats.valid + stats.disjoint + stats.unavailable,
        stats.frames
    );
    // Read back only after the burst; then exercise recovery/reuse of the single query set.
    let _pixels = warp.readback(&output);
    for _ in 0..244 {
        assert_eq!(handle.apply(&warp, &input, 0, &output, &frame), S_OK);
        let _pixels = warp.readback(&output);
    }
    assert_eq!(unsafe { bale_tonemap_debug_timing_read(&mut stats) }, S_OK);
    assert_eq!(stats.state, 1);
    assert!(stats.window >= 2);
    assert!(
        stats.valid > 0,
        "completed query set was not reused: {stats:?}"
    );
}
