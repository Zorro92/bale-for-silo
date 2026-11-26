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
    /// A stage created with debug switch words (BALE_TONEMAP_DEBUG's syntax, e.g. `bayerdither`), its
    /// create self-test included, without touching the environment other tests read.
    fn create_with(&self, config: &BaleToneMapConfig, words: &str) -> Handle {
        let gpu =
            ToneMapGpu::create_with_switches(&self.device, *config, words).unwrap_or_else(|e| {
                panic!(
                    "create '{words}' + self-test HRESULT=0x{:08x}",
                    e.code().0 as u32
                )
            });
        // The handle bale_tonemap_create would return: Handle's drop gives it to bale_tonemap_destroy.
        Handle(Box::into_raw(Box::new(gpu)))
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
/// and is the same on a second apply (static pattern: still content stays still), with the default
/// threshold (rav1g's R2 since 2026-11-26, from bale_tonemap_create) and with debug `bayerdither`'s
/// Bayer. P010 output keeps the plain-rounded 10-bit result.
#[test]
fn nv12_output_is_dithered_like_the_reference_and_p010_is_unchanged() {
    assert_ne!(
        bale_tonemap_output_formats() & BALE_TONEMAP_OUTPUT_NV12_DITHERED,
        0
    );
    let warp = Warp::new();
    let bayer = cpu::Options {
        dither: cpu::Dither::Bayer,
        ..cpu::Options::default()
    };
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
        // The default stage (bale_tonemap_create: R2) and a debug `bayerdither` one.
        for (handle, options) in [
            (warp.create(&c), cpu::Options::default()),
            (warp.create_with(&c, "bayerdither"), bayer),
        ] {
            // SAFETY: the test owns the live handle; no apply is in flight.
            assert_eq!(unsafe { (*handle.0).dither() }, options.dither);
            let dither = options.dither;
            for (f, input) in &inputs {
                let source = warp.texture(
                    &desc(
                        iw,
                        ih,
                        DXGI_FORMAT_P010,
                        D3D11_BIND_SHADER_RESOURCE.0 as u32,
                    ),
                    Some(input.as_slice()),
                );
                let nv12 = warp.texture(&output_desc(&c), None);
                assert_eq!(handle.apply(&warp, &source, 0, &nv12, f), S_OK);
                let actual = warp.readback(&nv12);
                let expected = cpu::reference_with(&c, f, input, iw, ih, options).unwrap();
                // The smooth SDR ramp only differs by a threshold flip; the PQ pattern's saturated
                // edges keep the tone-map tests' tolerance (3 codes; 12 at 10 bits, as the self-test).
                let (tolerance, tolerance10) = if f.transfer == 1 { (1, 1) } else { (3, 12) };
                assert_eq!(actual.len(), expected.len());
                for (i, (&a, &b)) in actual.iter().zip(&expected).enumerate() {
                    assert!(
                        a.abs_diff(b) <= tolerance,
                        "NV12 {dither:?} curve={curve} transfer={} code={i}: GPU {a}, CPU {b}",
                        f.transfer
                    );
                }
                assert_eq!(handle.apply(&warp, &source, 0, &nv12, f), S_OK);
                assert_eq!(warp.readback(&nv12), actual, "the dither is static");
                let p010 = warp.texture(&output_desc_as(&c, DXGI_FORMAT_P010), None);
                assert_eq!(handle.apply(&warp, &source, 0, &p010, f), S_OK);
                let actual10 = warp.readback_p010(&p010);
                let expected10 = cpu::reference_p010(&c, f, input, iw, ih).unwrap();
                assert_eq!(actual10.len(), expected10.len());
                for (i, (&a, &b)) in actual10.iter().zip(&expected10).enumerate() {
                    assert!(
                        a.abs_diff(b) <= tolerance10,
                        "P010 {dither:?} curve={curve} transfer={} code={i}: GPU {a}, CPU {b}",
                        f.transfer
                    );
                }
                if f.transfer == 1 {
                    // On the ramp, 8x8-block sums of the dithered luma resolve the 10-bit steps
                    // that plain rounding of the P010 result collapses into a few bands.
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
                    assert!(
                        dithered >= 3 * plain,
                        "{dither:?}: {dithered} vs {plain} levels"
                    );
                }
            }
        }
    }
}

/// rav1g's display-pass threshold, transcribed on its own from rav1g (testing/display_pass/model.py
/// `threshold`: wide products masked to 32 bits; libav1/dx/shaders/display_pass.hlsl `threshold`:
/// `mad(t8, 1/256, 0.5/256)`), not taken from bale_gpu.
fn rav1g_threshold(x: u32, y: u32) -> f32 {
    let sum = u128::from(x) * 0xc13f_a9a9 + u128::from(y) * 0x91e1_0da6;
    (((sum & 0xffff_ffff) >> 24) as f32).mul_add(1.0 / 256.0, 0.5 / 256.0)
}

/// rav1g's last NV12 quantisation (display_pass.hlsl `luma_code`, `chroma_code`) of the value `v`
/// (16 + 219 Y', or 128 + 224 C) with threshold `t`: the code, and how far `v + t` lies from the
/// nearest step that would change it (0.5 where the clamp gives both sides the same code).
fn rav1g_round(v: f32, t: f32, lo: f32, hi: f32) -> (u8, f32) {
    let sum = v + t;
    let step = sum.round();
    let margin = if (step - 1.0).clamp(lo, hi) == step.clamp(lo, hi) {
        0.5
    } else {
        (sum - step).abs()
    };
    (sum.floor().clamp(lo, hi) as u8, margin)
}

/// The NV12 codes rav1g's pass writes for the reference's encoded values: luma at its pixel, Cb at
/// its chroma sample, Cr at the transposed sample; with each code's margin ([`rav1g_round`]).
fn rav1g_nv12(encoded: &cpu::Encoded) -> (Vec<u8>, Vec<f32>) {
    let (w, h) = (encoded.width, encoded.height);
    let mut codes = vec![0; w * h * 3 / 2];
    let mut margins = vec![0.0; w * h * 3 / 2];
    for y in 0..h {
        for x in 0..w {
            let v = 16.0 + 219.0 * encoded.luma[y * w + x];
            let t = rav1g_threshold(x as u32, y as u32);
            (codes[y * w + x], margins[y * w + x]) = rav1g_round(v, t, 16.0, 235.0);
        }
    }
    for y in 0..h / 2 {
        for x in 0..w / 2 {
            let [cb, cr] = encoded.chroma[y * (w / 2) + x];
            let (tb, tr) = (
                rav1g_threshold(x as u32, y as u32),
                rav1g_threshold(y as u32, x as u32),
            );
            let at = w * h + y * w + 2 * x;
            (codes[at], margins[at]) = rav1g_round(128.0 + 224.0 * cb, tb, 16.0, 240.0);
            (codes[at + 1], margins[at + 1]) = rav1g_round(128.0 + 224.0 * cr, tr, 16.0, 240.0);
        }
    }
    (codes, margins)
}

/// Where NV12 byte `i` of a `w` x `h` picture is, for failure messages.
fn nv12_position(i: usize, w: usize, h: usize) -> String {
    if i < w * h {
        format!("luma ({}, {})", i % w, i / w)
    } else {
        let c = i - w * h;
        let plane = if c.is_multiple_of(2) { "Cb" } else { "Cr" };
        format!("{plane} ({}, {})", c % w / 2, c / w)
    }
}

/// A uniform limited-range P010 picture: every luma word `luma << 6`, every chroma pair `cb, cr`.
fn uniform_p010(w: u32, h: u32, luma: u16, cb: u16, cr: u16) -> Vec<u16> {
    let (w, h) = (w as usize, h as usize);
    let mut data = vec![luma << 6; w * h];
    for _ in 0..w * h / 4 {
        data.extend([cb << 6, cr << 6]);
    }
    data
}

/// The default stage's NV12 output (bale_tonemap_create, R2 since 2026-11-26) takes rav1g's
/// display-pass threshold and rounding (`floor(v + t)`; luma at the pixel, Cb at the chroma sample,
/// Cr transposed) code for code, from the stage's own value `v`; P010 stays bit-identical to a debug
/// `bayerdither` stage's, whose NV12 keeps matching the Bayer reference.
///
/// Exact everywhere on uniform BT.709 SDR frames at 1:1: each value is a quarter code (a 10-bit
/// code over 4) and each threshold an odd multiple of 1/512, so `v + t` lies at least 1/512 from a
/// step, about six times the largest WARP-vs-CPU float difference expected from D3D11's exp/log
/// precision (~3e-4 of a code). Three frames give each plane the fractions 1/4, 1/2 and 3/4, so every
/// position is checked against the cuts 3/4, 1/2 and 1/4 of its threshold, over a 128x128 picture (a
/// 64x64 chroma tile), a 3840-wide strip and a 2160-tall strip; two more frames hit the luma clamps.
/// Downscaled ramps reach every threshold level (arbitrary fractions): exact wherever the value
/// lies more than one threshold level (1/256) from a step, one code at most otherwise, on the
/// staged and the per-plane path. Prints `r2 rounding:` lines.
#[test]
fn r2_nv12_rounds_like_rav1g_code_for_code() {
    let warp = Warp::new();
    let srv = D3D11_BIND_SHADER_RESOURCE.0 as u32;
    let r2 = cpu::Options::default();
    assert_eq!(r2.dither, cpu::Dither::R2, "R2 is the default dither");
    let bayer = cpu::Options {
        dither: cpu::Dither::Bayer,
        ..r2
    };
    let sdr = BaleToneMapFrameInfo {
        transfer: 1,
        primaries: 1,
        matrix: 1,
        ..Default::default()
    };
    for (w, h) in [(128u32, 128u32), (3840, 16), (16, 2160)] {
        let c = BaleToneMapConfig {
            width: w,
            height: h,
            ..config(1)
        };
        let handle = warp.create(&c);
        // SAFETY: the test owns the live handle; no apply is in flight.
        assert_eq!(unsafe { (*handle.0).dither() }, cpu::Dither::R2);
        let output = warp.texture(&output_desc(&c), None);
        // (luma, Cb, Cr) 10-bit codes: luma 64 + 4n + r and chroma 512 + 4n + r have fraction r/4.
        for (luma, cb, cr) in [
            (465, 554, 435),
            (466, 555, 433),
            (467, 553, 434),
            // Luma 15.5 and 235.5: every code clamps to 16 / 235.
            (62, 512, 512),
            (942, 512, 512),
        ] {
            let input = uniform_p010(w, h, luma, cb, cr);
            let source = warp.texture(&desc(w, h, DXGI_FORMAT_P010, srv), Some(&input));
            assert_eq!(handle.apply(&warp, &source, 0, &output, &sdr), S_OK);
            let actual = warp.readback(&output);
            let encoded = cpu::reference_encoded(&c, &sdr, &input, w, h, r2).unwrap();
            let (model, margins) = rav1g_nv12(&encoded);
            let case = format!("{w}x{h} uniform ({luma}, {cb}, {cr})");
            // The premise of an exact comparison: no value within 1/1024 of a step on the CPU.
            if let Some(i) = margins.iter().position(|&m| m < 1.0 / 1024.0) {
                panic!(
                    "{case}: {} is {} from a step",
                    nv12_position(i, w as usize, h as usize),
                    margins[i]
                );
            }
            assert_eq!(
                cpu::reference(&c, &sdr, &input, w, h).unwrap(),
                model,
                "{case}: the CPU reference rounds like rav1g"
            );
            let differing = actual.iter().zip(&model).filter(|(a, b)| a != b).count();
            if let Some(i) = actual.iter().zip(&model).position(|(a, b)| a != b) {
                panic!(
                    "{case}: {differing} codes differ from rav1g's rounding, first {}: GPU {}, model {}",
                    nv12_position(i, w as usize, h as usize),
                    actual[i],
                    model[i]
                );
            }
            println!(
                "r2 rounding: {case}: all {} codes equal rav1g's model",
                actual.len()
            );
        }
    }
    // Downscaled smooth SDR ramps (staged and per-plane): area averages in linear light have any
    // fraction, so the comparison reaches every threshold level.
    const NEAR: f32 = 1.0 / 256.0;
    for (sw, sh, w, h) in [
        (512u32, 32u32, 256u32, 16u32),
        (384, 24, 256, 16),
        (600, 40, 250, 16),
    ] {
        let c = BaleToneMapConfig {
            width: w,
            height: h,
            reserved: [sw, sh, 0, 0],
            ..config(1)
        };
        let input = cpu::sdr_ramp(sw, sh, 100.0, 900.0);
        let source = warp.texture(&desc(sw, sh, DXGI_FORMAT_P010, srv), Some(&input));
        let encoded = cpu::reference_encoded(&c, &sdr, &input, sw, sh, r2).unwrap();
        let (model, margins) = rav1g_nv12(&encoded);
        assert_eq!(cpu::reference(&c, &sdr, &input, sw, sh).unwrap(), model);
        let handle = warp.create(&c);
        let output = warp.texture(&output_desc(&c), None);
        for staged in [true, false] {
            // SAFETY: the test owns the live handle; no apply is in flight.
            unsafe { (*handle.0).set_staged_downscale(staged) }.unwrap();
            assert_eq!(handle.apply(&warp, &source, 0, &output, &sdr), S_OK);
            let actual = warp.readback(&output);
            let case = format!("{sw}x{sh}->{w}x{h} ramp staged={staged}");
            let (mut exact, mut near, mut near_differing) = (0usize, 0usize, 0usize);
            for (i, ((&a, &b), &m)) in actual.iter().zip(&model).zip(&margins).enumerate() {
                let at = || nv12_position(i, w as usize, h as usize);
                if m >= NEAR {
                    assert_eq!(a, b, "{case} {} (margin {m}): GPU {a}, model {b}", at());
                    exact += 1;
                } else {
                    assert!(a.abs_diff(b) <= 1, "{case} {}: GPU {a}, model {b}", at());
                    near += 1;
                    near_differing += usize::from(a != b);
                }
            }
            assert!(
                exact * 100 >= actual.len() * 97,
                "{case}: only {exact} of {} codes away from a step",
                actual.len()
            );
            println!(
                "r2 rounding: {case}: {exact} codes equal rav1g's model, {near} within 1/256 of a step ({near_differing} of them one code apart)"
            );
        }
    }
    // Against a debug `bayerdither` stage on the same frames: P010 bit-identical (the permutations
    // differ only in the 8-bit threshold), NV12 within one code and not the same picture, and each
    // stage's NV12 matches its own dither's reference. Switching the dither of a live handle
    // (set_options) gives exactly the other created stage's output. PQ keeps the tone map's usual
    // tolerance against the reference (3 codes; saturated edges).
    let pq = BaleToneMapFrameInfo {
        transfer: 16,
        primaries: 9,
        matrix: 9,
        ..Default::default()
    };
    for (f, reserved, input, (iw, ih)) in [
        (
            &sdr,
            [512, 32, 0, 0],
            cpu::sdr_ramp(512, 32, 100.0, 900.0),
            (512, 32),
        ),
        (
            &pq,
            [0; 4],
            cpu::test_pattern(256, 16, true, false),
            (256, 16),
        ),
        (
            &pq,
            [512, 32, 0, 0],
            mixed_pattern(512, 32, true),
            (512, 32),
        ),
    ] {
        let c = BaleToneMapConfig {
            width: 256,
            height: 16,
            reserved,
            ..config(1)
        };
        let case = format!("transfer {} {iw}x{ih}->256x16", f.transfer);
        let source = warp.texture(&desc(iw, ih, DXGI_FORMAT_P010, srv), Some(&input));
        let render = |handle: &Handle, format: DXGI_FORMAT| {
            let output = warp.texture(&output_desc_as(&c, format), None);
            assert_eq!(handle.apply(&warp, &source, 0, &output, f), S_OK);
            if format == DXGI_FORMAT_P010 {
                warp.readback_p010(&output)
            } else {
                warp.readback(&output).into_iter().map(u16::from).collect()
            }
        };
        let (bayer_stage, r2_stage) = (warp.create_with(&c, "bayerdither"), warp.create(&c));
        // SAFETY (each deref below): the test owns the live handles; no apply is in flight.
        assert_eq!(unsafe { (*bayer_stage.0).dither() }, cpu::Dither::Bayer);
        assert_eq!(unsafe { (*r2_stage.0).dither() }, cpu::Dither::R2);
        let r2_nv12 = render(&r2_stage, DXGI_FORMAT_NV12);
        let bayer_nv12 = render(&bayer_stage, DXGI_FORMAT_NV12);
        assert_eq!(
            render(&r2_stage, DXGI_FORMAT_P010),
            render(&bayer_stage, DXGI_FORMAT_P010),
            "{case}: P010 must not depend on the dither"
        );
        assert!(
            r2_nv12
                .iter()
                .zip(&bayer_nv12)
                .all(|(a, b)| a.abs_diff(*b) <= 1),
            "{case}: R2 and Bayer more than a code apart"
        );
        let differing = r2_nv12
            .iter()
            .zip(&bayer_nv12)
            .filter(|(a, b)| a != b)
            .count();
        assert!(differing > 0, "{case}: the R2 stage wrote Bayer's codes");
        let tolerance = if f.transfer == 1 { 1 } else { 3 };
        for (options, actual) in [(r2, &r2_nv12), (bayer, &bayer_nv12)] {
            let reference = cpu::reference_with(&c, f, &input, iw, ih, options).unwrap();
            for (i, (&a, &b)) in actual.iter().zip(&reference).enumerate() {
                assert!(
                    a.abs_diff(u16::from(b)) <= tolerance,
                    "{case} {:?} {}: GPU {a}, CPU {b}",
                    options.dither,
                    nv12_position(i, 256, 16)
                );
            }
        }
        unsafe { (*bayer_stage.0).set_options(r2) }.unwrap();
        assert_eq!(unsafe { (*bayer_stage.0).dither() }, cpu::Dither::R2);
        assert_eq!(
            render(&bayer_stage, DXGI_FORMAT_NV12),
            r2_nv12,
            "{case}: set_options"
        );
        unsafe { (*bayer_stage.0).set_options(bayer) }.unwrap();
        assert_eq!(unsafe { (*bayer_stage.0).dither() }, cpu::Dither::Bayer);
        assert_eq!(
            render(&bayer_stage, DXGI_FORMAT_NV12),
            bayer_nv12,
            "{case}: back to Bayer"
        );
        println!(
            "r2 rounding: {case}: {differing} of {} NV12 codes differ from Bayer by one, P010 identical",
            r2_nv12.len()
        );
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
                            // Real decoder slice bounds are covered by gpu::tests. Bind 0 takes the
                            // copy every decoder array takes (with or without an SRV bind); the SRV
                            // bind takes the in-place path of other shader-readable sources.
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
            let pattern = cpu::test_pattern(28, 14, true, false);
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
            // Current math at every input chroma location, the legacy math (`gammachroma`) and the
            // bilinear input chroma (`bilinearchroma`).
            let (linear, legacy) = (
                cpu::Options::default(),
                cpu::Options::from(cpu::Method::Legacy),
            );
            let bilinear = |options: cpu::Options| cpu::Options {
                bilinear_chroma: true,
                ..options
            };
            for (options, chroma_location) in [
                (linear, 0),
                (linear, 2),
                (linear, 3),
                (linear, 4),
                (linear, 6),
                (legacy, 3),
                (bilinear(linear), 0),
                (bilinear(linear), 3),
                (bilinear(legacy), 3),
            ] {
                let f = BaleToneMapFrameInfo {
                    transfer: 16,
                    primaries: 9,
                    matrix: 9,
                    chroma_location,
                    ..Default::default()
                };
                // SAFETY: the test owns the live handle; no apply is in flight.
                unsafe { (*handle.0).set_options(options) }.unwrap();
                let expected = cpu::reference_with(&c, &f, &pattern, 28, 14, options).unwrap();
                assert_eq!(handle.apply(&warp, &source, 0, &output, &f), S_OK);
                let actual = warp.readback(&output);
                assert_eq!(actual.len(), expected.len());
                for (i, (&a, &b)) in actual.iter().zip(&expected).enumerate() {
                    assert!(
                        a.abs_diff(b) <= 3,
                        "curve={curve} {width}x{height} {options:?} location={chroma_location} code={i}: GPU {a}, CPU {b}"
                    );
                }
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

/// Catmull-Rom input chroma (the default) at 1:1, 2:1 and 4:1 for every siting class (left, center,
/// topleft, bottom), PQ and BT.709 SDR, P010 and NV12 in and out: the co-sited fast path (2:1 and 4:1)
/// gives exactly the full 16-tap filter's codes, both filters match the CPU reference, and debug
/// `bilinearchroma` matches the reference's bilinear read. Prints how many codes the cubic changes
/// against bilinear (`cubic chroma:` lines).
#[test]
fn cubic_chroma_fast_path_is_exact_and_both_filters_match_the_reference() {
    let warp = Warp::new();
    let cubic = cpu::Options::default();
    let bilinear = cpu::Options {
        bilinear_chroma: true,
        ..cubic
    };
    for (sw, sh, w, h) in [(32, 16, 32, 16), (64, 32, 32, 16), (128, 64, 32, 16)] {
        let c = BaleToneMapConfig {
            width: w,
            height: h,
            reserved: [sw, sh, 0, 0],
            ..config(1)
        };
        let handle = warp.create(&c);
        for (name, transfer, matrix) in [("pq", 16, 9), ("sdr709", 1, 1)] {
            let pattern = mixed_pattern(sw, sh, matrix == 9);
            let bytes: Vec<u8> = pattern.iter().map(|&w| (w >> 8) as u8).collect();
            // The reference reads P010 words; an NV12 code b is the 10-bit code b << 2, word b << 8.
            let as_p010: Vec<u16> = bytes.iter().map(|&b| u16::from(b) << 8).collect();
            let bind = D3D11_BIND_SHADER_RESOURCE.0 as u32;
            for chroma_location in [0u8, 2, 3, 6] {
                let f = BaleToneMapFrameInfo {
                    transfer,
                    primaries: matrix,
                    matrix,
                    chroma_location,
                    ..Default::default()
                };
                for input in ["p010", "nv12"] {
                    let (source, words) = match input {
                        "p010" => (
                            warp.texture(&desc(sw, sh, DXGI_FORMAT_P010, bind), Some(&pattern)),
                            &pattern,
                        ),
                        _ => (
                            warp.texture8(&desc(sw, sh, DXGI_FORMAT_NV12, bind), &bytes),
                            &as_p010,
                        ),
                    };
                    for format in [DXGI_FORMAT_NV12, DXGI_FORMAT_P010] {
                        let ten_bit = format == DXGI_FORMAT_P010;
                        let target = warp.texture(&output_desc_as(&c, format), None);
                        let render = |options: cpu::Options, fast: bool| -> Vec<u16> {
                            // SAFETY: the test owns the live handle; no apply is in flight.
                            unsafe {
                                (*handle.0).set_options(options).unwrap();
                                (*handle.0).set_cosited_fast_path(fast);
                            }
                            assert_eq!(handle.apply(&warp, &source, 0, &target, &f), S_OK);
                            if ten_bit {
                                warp.readback_p010(&target)
                            } else {
                                warp.readback(&target).into_iter().map(u16::from).collect()
                            }
                        };
                        let expected = |options: cpu::Options| -> Vec<u16> {
                            if ten_bit {
                                cpu::reference_p010_with(&c, &f, words, sw, sh, options).unwrap()
                            } else {
                                cpu::reference_with(&c, &f, words, sw, sh, options)
                                    .unwrap()
                                    .into_iter()
                                    .map(u16::from)
                                    .collect()
                            }
                        };
                        let case = format!(
                            "{sw}x{sh}->{w}x{h} {name} location={chroma_location} in={input} out={}",
                            if ten_bit { "p010" } else { "nv12" }
                        );
                        let fast = render(cubic, true);
                        assert_eq!(fast, render(cubic, false), "fast path changed {case}");
                        let old = render(bilinear, true);
                        let tolerance = if ten_bit { 12 } else { 3 };
                        for (options, actual) in [(cubic, &fast), (bilinear, &old)] {
                            let reference = expected(options);
                            assert_eq!(actual.len(), reference.len());
                            for (i, (&a, &b)) in actual.iter().zip(&reference).enumerate() {
                                assert!(
                                    a.abs_diff(b) <= tolerance,
                                    "{case} bilinear={} code={i}: GPU {a}, CPU {b}",
                                    options.bilinear_chroma
                                );
                            }
                        }
                        let differing = fast.iter().zip(&old).filter(|(a, b)| a != b).count();
                        println!(
                            "cubic chroma: {case}: {differing} of {} codes differ from bilinear",
                            fast.len()
                        );
                    }
                }
            }
        }
    }
}

/// The staged downscale (one area pass into an fp32 intermediate, plane passes read it) must give
/// the same codes as the per-plane path (each plane evaluating the area sum itself), under the
/// current math and the legacy one (`gammachroma`). Both run on one handle; set_staged_downscale and
/// set_method switch. Prints one summary line per case (`staged identity:`) for the CI log; allows
/// at most one code of float noise. Also prints how many luma codes the current math changes
/// against the legacy math (`method luma:`): none may change for BT.709 SDR (no clip either way,
/// same siting), PQ/HLG change only where a value left 0..1 before the clip.
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
                    let render = |staged: bool, method: cpu::Method| {
                        unsafe { (*handle.0).set_method(method) };
                        unsafe { (*handle.0).set_staged_downscale(staged) }.unwrap();
                        assert_eq!(unsafe { (*handle.0).staged_downscale() }, staged);
                        assert_eq!(handle.apply(&warp, &source, 0, &target, f), S_OK);
                        if format == DXGI_FORMAT_P010 {
                            warp.readback_p010(&target)
                        } else {
                            warp.readback(&target).into_iter().map(u16::from).collect()
                        }
                    };
                    let mut staged_luma = Vec::new();
                    for method in [cpu::Method::Linear, cpu::Method::Legacy] {
                        let old = render(false, method);
                        let new = render(true, method);
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
                            "staged identity: {sw}x{sh}->{w}x{h} {name} {method:?} in={input} \
                             out={output}: {differing} of {} codes differ, max {max}",
                            old.len()
                        );
                        println!("{line}");
                        summary.push(line);
                        staged_luma.push(new[..(w * h) as usize].to_vec());
                    }
                    let (linear, legacy) = (&staged_luma[0], &staged_luma[1]);
                    let differing = linear.iter().zip(legacy).filter(|(a, b)| a != b).count();
                    println!(
                        "method luma: {sw}x{sh}->{w}x{h} {name} in={input} out={output}: \
                         {differing} of {} luma codes differ from gammachroma",
                        linear.len()
                    );
                    if *name == "sdr709" {
                        assert_eq!(differing, 0, "BT.709 SDR luma must not change");
                    }
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

/// PQ frames take their source peak from the stream's MaxCLL / mastering metadata exactly as the CPU
/// reference does, the debug `fixedpeak` option restores the config peak, `bale_tonemap_peak_read`
/// reports each change once with its source, and the constant buffer is uploaded only when a frame
/// needs other constants (the same frame again uploads nothing). The readout is a debug probe.
#[cfg(feature = "debug-probes")]
#[test]
fn pq_peak_follows_the_stream_metadata_and_constants_upload_only_on_change() {
    let warp = Warp::new();
    let c = BaleToneMapConfig {
        width: 16,
        height: 8,
        ..config(1)
    };
    let handle = warp.create(&c);
    let pattern = cpu::test_pattern(16, 8, true, false);
    let source = warp.texture(
        &desc(16, 8, DXGI_FORMAT_P010, D3D11_BIND_SHADER_RESOURCE.0 as u32),
        Some(&pattern),
    );
    let output = warp.texture(&output_desc_as(&c, DXGI_FORMAT_P010), None);
    let pq = BaleToneMapFrameInfo {
        transfer: 16,
        primaries: 9,
        matrix: 9,
        ..Default::default()
    };
    let meta = |max_cll: u32, mastering_max_nits: f32| BaleToneMapFrameInfo {
        has_content_light: u8::from(max_cll > 0),
        max_cll,
        has_mastering: u8::from(mastering_max_nits > 0.0),
        mastering_max_nits,
        ..pq
    };
    let read = || {
        let mut info = BaleToneMapPeakInfo::default();
        // SAFETY: complete writable record; tests run serially, so apply never holds the report here.
        assert_eq!(unsafe { bale_tonemap_peak_read(&mut info) }, S_OK);
        info
    };
    // SAFETY (each deref below): the test owns the live handle and no apply is in flight.
    let stage = unsafe { (*handle.0).peak_stage() };
    let uploads = || unsafe { (*handle.0).constant_uploads() };
    let render = |f: &BaleToneMapFrameInfo| {
        assert_eq!(handle.apply(&warp, &source, 0, &output, f), S_OK);
        warp.readback_p010(&output)
    };
    let matches = |actual: &[u16], expected: &[u16], what: &str| {
        assert_eq!(actual.len(), expected.len());
        for (i, (&a, &b)) in actual.iter().zip(expected).enumerate() {
            assert!(a.abs_diff(b) <= 12, "{what} code={i}: GPU {a}, CPU {b}");
        }
    };
    assert!(stage > 0);
    assert_eq!(
        (read().stage, read().changes),
        (stage, 0),
        "create starts a fresh report"
    );
    let plain = render(&pq);
    matches(
        &plain,
        &cpu::reference_p010(&c, &pq, &pattern, 16, 8).unwrap(),
        "no metadata",
    );
    let info = read();
    assert_eq!((info.stage, info.changes, info.source), (stage, 1, 1));
    assert_eq!(
        (info.peak_nits, info.config_peak_nits, info.transfer),
        (1000.0, 1000.0, 16)
    );
    let settled = uploads();
    render(&pq);
    render(&pq);
    assert_eq!(uploads(), settled, "the same frame needs no new constants");
    assert_eq!(read().changes, 1, "the same peak is reported once");
    let mut changes = 1;
    // Each step changes the peak value: one upload and one report each.
    for (f, source, peak) in [
        (meta(4000, 4000.0), 2, 4000.0),
        (meta(0, 600.0), 3, 600.0),
        (meta(4000, 1000.0), 3, 1000.0),
        (meta(1500, 0.0), 2, 1500.0),
    ] {
        let before = uploads();
        let actual = render(&f);
        matches(
            &actual,
            &cpu::reference_p010(&c, &f, &pattern, 16, 8).unwrap(),
            "metadata",
        );
        if peak == 4000.0 {
            assert_ne!(actual, plain, "a 4000-nit peak must change the picture");
        }
        assert_eq!(uploads(), before + 1);
        render(&f);
        assert_eq!(uploads(), before + 1);
        changes += 1;
        let info = read();
        assert_eq!(
            (info.stage, info.changes, info.source),
            (stage, changes, source)
        );
        assert_eq!(info.peak_nits, peak);
        assert_eq!(
            (info.max_cll, info.mastering_max_nits),
            (f.max_cll, f.mastering_max_nits)
        );
        assert_eq!(info.flags, 0);
    }
    // NV12 output takes the same peak.
    let bright = meta(4000, 4000.0);
    let nv12 = warp.texture(&output_desc(&c), None);
    assert_eq!(handle.apply(&warp, &source, 0, &nv12, &bright), S_OK);
    let expected = cpu::reference(&c, &bright, &pattern, 16, 8).unwrap();
    for (i, (&a, &b)) in warp.readback(&nv12).iter().zip(&expected).enumerate() {
        assert!(a.abs_diff(b) <= 3, "NV12 code={i}: GPU {a}, CPU {b}");
    }
    // Debug fixedpeak: the config peak again, reported with its flag.
    let fixed = cpu::Options {
        fixed_peak: true,
        ..Default::default()
    };
    unsafe { (*handle.0).set_options(fixed) }.unwrap();
    let actual = render(&bright);
    matches(
        &actual,
        &cpu::reference_p010_with(&c, &bright, &pattern, 16, 8, fixed).unwrap(),
        "fixedpeak",
    );
    matches(&actual, &plain, "fixedpeak against no metadata");
    let info = read();
    assert_eq!((info.source, info.peak_nits, info.flags), (1, 1000.0, 1));
    // Another stage takes over the report; this one's later changes no longer show.
    let newer = warp.create(&c);
    let newer_stage = unsafe { (*newer.0).peak_stage() };
    assert!(newer_stage > stage);
    unsafe { (*handle.0).set_options(cpu::Options::default()) }.unwrap();
    render(&bright);
    assert_eq!((read().stage, read().changes), (newer_stage, 0));
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
    // Peak 0 means "none configured" since 2026-11-24 (metadata, else 1000 nits); negative stays invalid.
    let _unset = warp.create(&BaleToneMapConfig {
        source_peak_nits: 0.0,
        ..c
    });
    let invalid = BaleToneMapConfig {
        source_peak_nits: -1.0,
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

/// Event timing reports only the total (an upper bound) and its end resolution: the pass slots
/// and `disjoint` stay zero, and every accounted outcome is valid or unavailable.
#[cfg(feature = "stage-timing")]
fn assert_event_window(stats: &BaleToneMapTimingStats) {
    assert_eq!(stats.valid + stats.unavailable, stats.frames, "{stats:?}");
    assert_eq!(stats.disjoint, 0, "{stats:?}");
    assert_eq!(
        [
            stats.avg_ms[2],
            stats.avg_ms[3],
            stats.max_ms[2],
            stats.max_ms[3]
        ],
        [0.0; 4],
        "{stats:?}"
    );
    assert!(
        stats
            .avg_ms
            .iter()
            .chain(&stats.max_ms)
            .all(|ms| ms.is_finite() && *ms >= 0.0),
        "{stats:?}"
    );
    assert!(
        stats
            .avg_ms
            .iter()
            .zip(stats.max_ms)
            .all(|(avg, max)| *avg <= max),
        "{stats:?}"
    );
    // Sample by sample the end resolution is part of the total.
    assert!(
        stats.avg_ms[1] <= stats.avg_ms[0] && stats.max_ms[1] <= stats.max_ms[0],
        "{stats:?}"
    );
    if stats.valid > 0 {
        assert!(stats.avg_ms[0] > 0.0, "{stats:?}");
    }
}

#[cfg(feature = "stage-timing")]
fn timing_snapshot() -> BaleToneMapTimingStats {
    let mut stats = BaleToneMapTimingStats::default();
    // SAFETY: writable complete snapshot, no retained pointer; read touches no GPU or caller state.
    assert_eq!(unsafe { bale_tonemap_debug_timing_read(&mut stats) }, S_OK);
    stats
}

/// Exercise the real event queries and poll thread across context-state swaps and asynchronous
/// apply calls. Only the test's texture readback waits; the instrumented apply never waits.
#[cfg(feature = "stage-timing")]
#[test]
fn stage_events_exclude_self_test_and_survive_state_restore() {
    struct Disable;
    impl Drop for Disable {
        fn drop(&mut self) {
            assert_eq!(bale_tonemap_debug_timing_enable(0), S_OK);
        }
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
    let stats = timing_snapshot();
    assert_eq!(
        stats.state, 1,
        "event query or poll thread creation failed: {stats:?}"
    );
    assert_eq!((stats.window, stats.frames, stats.valid), (0, 0, 0));
    for _ in 0..244 {
        assert_eq!(handle.apply(&warp, &input, 0, &output, &frame), S_OK);
        // Synchronise in the TEST so each measurement completes before its read delay passes.
        let _pixels = warp.readback(&output);
    }
    let stats = timing_snapshot();
    assert_eq!((stats.state, stats.window, stats.frames), (1, 1, 240));
    assert_event_window(&stats);
    assert!(stats.valid > 0, "no completed WARP event pairs: {stats:?}");
    assert!(stats.pending <= 1);
    drop(handle);
    let final_stats = timing_snapshot();
    assert_eq!((final_stats.state, final_stats.pending), (3, 0));
    assert!(final_stats.window > stats.window);
    assert_event_window(&final_stats);
    // Off-by-default create owns no queries and must not replace the last measured session.
    let _off = warp.create(&c);
    assert_eq!(timing_snapshot().session, final_stats.session);
}

#[cfg(feature = "stage-timing")]
#[test]
fn stage_events_survive_a_burst_without_per_apply_readback() {
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
    // A readback after every apply would mask unresolved-measurement behaviour. Submit a
    // shader-readable P010 burst with state restoration but no readback between frames.
    for _ in 0..244 {
        assert_eq!(handle.apply(&warp, &input, 0, &output, &frame), S_OK);
    }
    let stats = timing_snapshot();
    assert_eq!((stats.state, stats.window, stats.frames), (1, 1, 240));
    assert!(
        stats.pending <= 1,
        "more than one measurement in flight: {stats:?}"
    );
    assert!(
        stats.unavailable > 0,
        "busy measurements must skip applies: {stats:?}"
    );
    assert_event_window(&stats);
    // Read back only after the burst; then exercise recovery/reuse of the single event pair.
    let _pixels = warp.readback(&output);
    for _ in 0..244 {
        assert_eq!(handle.apply(&warp, &input, 0, &output, &frame), S_OK);
        let _pixels = warp.readback(&output);
    }
    let stats = timing_snapshot();
    assert_eq!(stats.state, 1, "{stats:?}");
    assert!(stats.window >= 2);
    assert!(
        stats.valid > 0,
        "completed event pair was not reused: {stats:?}"
    );
    assert_event_window(&stats);
}

/// Destroy with a measurement in flight: no read and no wait (the poll thread is detached, never
/// joined), the stage closes with nothing pending, and the next stage on the device measures.
#[cfg(feature = "stage-timing")]
#[test]
fn stage_events_tear_down_with_a_measurement_in_flight() {
    struct Disable;
    impl Drop for Disable {
        fn drop(&mut self) {
            assert_eq!(bale_tonemap_debug_timing_enable(0), S_OK);
        }
    }
    let warp = Warp::new();
    let c = config(1);
    let input = warp.texture(
        &desc(16, 8, DXGI_FORMAT_P010, 0),
        Some(&cpu::test_pattern(16, 8, true, false)),
    );
    let output = warp.texture(&output_desc(&c), None);
    let frame = BaleToneMapFrameInfo {
        transfer: 16,
        primaries: 9,
        matrix: 9,
        ..Default::default()
    };
    assert_eq!(bale_tonemap_debug_timing_enable(1), S_OK);
    let _disable = Disable;
    let first = warp.create(&c);
    let session = timing_snapshot().session;
    // The first apply issues a measurement, the second finds it in flight.
    for _ in 0..2 {
        assert_eq!(first.apply(&warp, &input, 0, &output, &frame), S_OK);
    }
    drop(first);
    let closed = timing_snapshot();
    assert_eq!(
        (closed.session, closed.state, closed.pending, closed.frames),
        (session, 3, 0, 2),
        "{closed:?}"
    );
    assert_event_window(&closed);
    let second = warp.create(&c);
    assert!(timing_snapshot().session > session);
    for _ in 0..24 {
        assert_eq!(second.apply(&warp, &input, 0, &output, &frame), S_OK);
        let _pixels = warp.readback(&output);
    }
    drop(second);
    let stats = timing_snapshot();
    assert_eq!(stats.state, 3, "{stats:?}");
    assert!(
        stats.valid > 0,
        "the second stage measured nothing: {stats:?}"
    );
    assert_event_window(&stats);
}
