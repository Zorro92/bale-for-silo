//! Independent fp32 reference, mirrored by shaders/tonemap_math.hlsli.
//!
//! Parameter semantics were checked against Jellyfin FFmpeg's tonemapx patch and FFmpeg
//! vf_tonemap (LGPL-2.1+); this implements the published curves, not their LUT/SIMD code.
//! tonemapx peak=100 means 1000 nits; its internal reference white is 203 nits. Thus ABI
//! target_white_nits=100 maps to the tonemapx 203-nit EETF endpoint, normalized to SDR 1.
//! BT.2390 uses its default knee offset 1, max ORIGINAL RGB signal, and desat=0 disables
//! desaturation. Hable uses npl=100 and max RGB AFTER desaturation, like vf_tonemap.
//! Hable's FFmpeg default desat is 2, but explicit ABI desat=0 disables it on both curves.
//! Output uses BT.1886 inverse EOTF (power 1/2.4): zscale t=bt709's default display-referred
//! transfer, rather than its scene-referred camera OETF. No dithering or integer LUTs.
use super::types::{BaleToneMapConfig, BaleToneMapFrameInfo};

pub const E_INVALIDARG: i32 = 0x80070057u32 as i32;

impl BaleToneMapConfig {
    pub fn validate(&self) -> Result<(), i32> {
        if self.width == 0
            || self.height == 0
            || !self.width.is_multiple_of(2)
            || !self.height.is_multiple_of(2)
            || !matches!(self.curve, 1 | 2)
            || !self.source_peak_nits.is_finite()
            || self.source_peak_nits <= 0.0
            || !self.target_white_nits.is_finite()
            || self.target_white_nits <= 0.0
            || !self.desat.is_finite()
            || self.desat < 0.0
            || self.reserved != [0; 4]
        {
            return Err(E_INVALIDARG);
        }
        Ok(())
    }
}
impl BaleToneMapFrameInfo {
    pub fn validate(&self) -> Result<(), i32> {
        // Metadata is intentionally ignored, including reserved fields, in ABI 1.
        if self.transfer != 16
            || !matches!((self.primaries, self.matrix), (9, 9) | (1, 1))
            || self.full_range > 1
        {
            return Err(E_INVALIDARG);
        }
        Ok(())
    }
}

pub fn pq_eotf(signal: f32) -> f32 {
    let p = signal.clamp(0.0, 1.0).powf(1.0 / 78.84375);
    ((p - 0.8359375).max(0.0) / (18.851563 - 18.6875 * p)).powf(1.0 / 0.15930176) * 10000.0
}
pub fn pq_encode(nits: f32) -> f32 {
    let p = (nits.clamp(0.0, 10000.0) / 10000.0).powf(0.15930176);
    ((0.8359375 + 18.851563 * p) / (1.0 + 18.6875 * p)).powf(78.84375)
}
// HLG EOTF belongs here when a later ABI supports transfer 18.
pub fn hable(x: f32) -> f32 {
    (x * (0.15 * x + 0.05) + 0.004) / (x * (0.15 * x + 0.5) + 0.06) - 0.02 / 0.3
}
pub fn hable_tonemap(nits: f32, source_peak_nits: f32, target_white_nits: f32) -> f32 {
    hable((nits.max(0.0).min(source_peak_nits) / target_white_nits).min(1e6))
        / hable((source_peak_nits / target_white_nits).clamp(1e-3, 1e6))
}
/// BT.2390 Hermite EETF with a knee offset of 1. The server's 203-nit internal
/// reference is represented by ABI target_white_nits=100; defaults are 1000/100.
/// Clamp to the source domain rather than extrapolating the cubic above its endpoint.
pub fn bt2390_eetf(nits: f32, source_peak_nits: f32, target_white_nits: f32) -> f32 {
    let nits = nits.max(0.0).min(source_peak_nits);
    let target = (target_white_nits * 2.03).min(10000.0);
    if source_peak_nits <= target {
        return (nits / target).min(1.0);
    }
    let peak = pq_encode(source_peak_nits);
    let s = pq_encode(nits) / peak;
    let max_lum = pq_encode(target) / peak;
    let knee = 2.0 * max_lum - 1.0;
    let mapped = if s < knee {
        s
    } else {
        let t = (s - knee) / (1.0 - knee);
        let t2 = t * t;
        let t3 = t2 * t;
        (2.0 * t3 - 3.0 * t2 + 1.0) * knee
            + (t3 - 2.0 * t2 + t) * (1.0 - knee)
            + (-2.0 * t3 + 3.0 * t2) * max_lum
    };
    (pq_eotf(mapped * peak) / target).clamp(0.0, 1.0)
}
pub fn tone_curve(nits: f32, source_peak_nits: f32, target_white_nits: f32, curve: u32) -> f32 {
    if curve == 2 {
        hable_tonemap(nits, source_peak_nits, target_white_nits)
    } else {
        bt2390_eetf(nits, source_peak_nits, target_white_nits)
    }
}
pub fn curve(config: &BaleToneMapConfig, nits: f32) -> f32 {
    tone_curve(
        nits,
        config.source_peak_nits,
        config.target_white_nits,
        config.curve,
    )
}
pub fn luma_weights(bt2020: bool) -> [f32; 3] {
    if bt2020 {
        [0.2627, 0.6780, 0.0593]
    } else {
        [0.2126, 0.7152, 0.0722]
    }
}
fn dot(a: [f32; 3], b: [f32; 3]) -> f32 {
    a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
}
pub fn source_decode(y: f32, uv: [f32; 2], bt2020: bool, full_range: bool) -> [f32; 3] {
    let [kr, kg, kb] = luma_weights(bt2020);
    let y = if full_range {
        y / 1023.0
    } else {
        (y - 64.0) / 876.0
    };
    let uv = uv.map(|v| (v - 512.0) / if full_range { 1023.0 } else { 896.0 });
    let r = y + 2.0 * (1.0 - kr) * uv[1];
    let b = y + 2.0 * (1.0 - kb) * uv[0];
    let g = (y - kr * r - kb * b) / kg;
    [r, g, b].map(pq_eotf)
}
pub fn rgb2020_to_709(rgb: [f32; 3]) -> [f32; 3] {
    [
        dot([1.660491, -0.5876411, -0.07284986], rgb),
        dot([-0.1245505, 1.1328999, -0.00834942], rgb),
        dot([-0.01815076, -0.1005789, 1.1187297], rgb),
    ]
}
pub fn gamut_clip(rgb: [f32; 3]) -> [f32; 3] {
    rgb.map(|v| v.clamp(0.0, 1.0))
}
pub fn gamut_to_709(rgb: [f32; 3], bt2020: bool) -> [f32; 3] {
    gamut_clip(if bt2020 { rgb2020_to_709(rgb) } else { rgb })
}
/// Scene-referred BT.709 camera OETF, available to other consumers. The server uses
/// display-referred SDR instead, so tone_map_pixel calls sdr_encode below.
pub fn bt709_oetf(linear: f32) -> f32 {
    let x = linear.max(0.0);
    if x < 0.018_053_97 {
        x * 4.5
    } else {
        1.0992968 * x.powf(0.45) - 0.09929683
    }
}
pub fn sdr_encode(linear: f32) -> f32 {
    linear.clamp(0.0, 1.0).powf(1.0 / 2.4)
}
pub fn rgb709_to_ycbcr(rgb: [f32; 3]) -> [f32; 3] {
    let y = dot(luma_weights(false), rgb);
    [
        y,
        (rgb[2] - y) / (2.0 * (1.0 - 0.0722)),
        (rgb[0] - y) / (2.0 * (1.0 - 0.2126)),
    ]
}
pub fn desaturate(rgb: [f32; 3], bt2020: bool, threshold_nits: f32) -> [f32; 3] {
    let luma = dot(luma_weights(bt2020), rgb);
    let over = ((luma - threshold_nits).max(1e-6) / luma.max(1e-6)).clamp(0.0, 1.0);
    rgb.map(|v| v * (1.0 - over) + luma * over)
}
#[allow(clippy::too_many_arguments)]
pub fn tone_map_pixel(
    y: f32,
    uv: [f32; 2],
    source_peak_nits: f32,
    target_white_nits: f32,
    desat: f32,
    curve: u32,
    bt2020: bool,
    full_range: bool,
) -> [f32; 3] {
    let mut rgb = source_decode(y, uv, bt2020, full_range);
    let original = rgb.into_iter().fold(0.0f32, f32::max);
    // Match tonemapx's order for nonzero desat: convert primaries before desaturating,
    // but derive the scale from the original RGB. The scalar scale commutes with the matrix.
    if curve == 1 && bt2020 {
        rgb = rgb2020_to_709(rgb);
    }
    if desat > 0.0 {
        let white = if curve == 1 {
            target_white_nits * 2.03
        } else {
            target_white_nits
        };
        rgb = desaturate(rgb, bt2020, desat * white);
    }
    let sig = if curve == 1 {
        original
    } else {
        rgb.into_iter().fold(0.0f32, f32::max)
    };
    let scale = tone_curve(sig, source_peak_nits, target_white_nits, curve) / sig.max(1e-6);
    let rgb = gamut_to_709(rgb.map(|v| v * scale), bt2020 && curve == 2).map(sdr_encode);
    rgb709_to_ycbcr(rgb)
}
pub fn pixel(
    config: &BaleToneMapConfig,
    frame: &BaleToneMapFrameInfo,
    y: f32,
    uv: [f32; 2],
) -> [f32; 3] {
    tone_map_pixel(
        y,
        uv,
        config.source_peak_nits,
        config.target_white_nits,
        config.desat,
        config.curve,
        frame.matrix == 9,
        frame.full_range == 1,
    )
}
pub fn quantize_luma(y: f32) -> f32 {
    (16.0 + 219.0 * y + 0.5).floor().clamp(16.0, 235.0)
}
pub fn quantize_chroma(uv: [f32; 2]) -> [f32; 2] {
    uv.map(|v| (128.0 + 224.0 * v + 0.5).floor().clamp(16.0, 240.0))
}

/// Tight P010 words (high 10 bits), input may include decoder alignment beyond visible size.
/// Chroma upsampling: bilinear, centres at (2*x, 2*y+0.5) in luma-centre coordinates.
/// Downsampling: horizontal [1 2 1]/4 centred at 2*x, vertical [1 1]/2. Clamp visible
/// edges, never sample alignment padding. This is left-sited, unlike a centred 2x2 box.
pub fn reference(
    config: &BaleToneMapConfig,
    frame: &BaleToneMapFrameInfo,
    input: &[u16],
    width: u32,
    height: u32,
) -> Result<Vec<u8>, i32> {
    config.validate()?;
    frame.validate()?;
    let (w, h, iw, ih) = (
        config.width as usize,
        config.height as usize,
        width as usize,
        height as usize,
    );
    let len = iw
        .checked_mul(ih)
        .and_then(|n| n.checked_add(n / 2))
        .ok_or(E_INVALIDARG)?;
    if !width.is_multiple_of(2)
        || !height.is_multiple_of(2)
        || iw < w
        || ih < h
        || input.len() != len
    {
        return Err(E_INVALIDARG);
    }
    let uv_at = |x: i32, y: i32, plane: usize| {
        let x = x.clamp(0, w as i32 / 2 - 1) as usize;
        let y = y.clamp(0, h as i32 / 2 - 1) as usize;
        (input[iw * ih + y * iw + 2 * x + plane] >> 6) as f32
    };
    let sample = |x: i32, y: i32| {
        let x = x.clamp(0, w as i32 - 1);
        let y = y.clamp(0, h as i32 - 1);
        let cx = x as f32 * 0.5;
        let cy = (y as f32 - 0.5) * 0.5;
        let (ix, iy) = (cx.floor() as i32, cy.floor() as i32);
        let (fx, fy) = (cx - ix as f32, cy - iy as f32);
        let uv = std::array::from_fn(|p| {
            let a = uv_at(ix, iy, p) * (1.0 - fx) + uv_at(ix + 1, iy, p) * fx;
            let b = uv_at(ix, iy + 1, p) * (1.0 - fx) + uv_at(ix + 1, iy + 1, p) * fx;
            a * (1.0 - fy) + b * fy
        });
        pixel(
            config,
            frame,
            (input[y as usize * iw + x as usize] >> 6) as f32,
            uv,
        )
    };
    let mut out = vec![0; w * h * 3 / 2];
    for y in 0..h {
        for x in 0..w {
            out[y * w + x] = quantize_luma(sample(x as i32, y as i32)[0]) as u8;
        }
    }
    for y in 0..h / 2 {
        for x in 0..w / 2 {
            let mut uv = [0.0; 2];
            for dy in 0..2 {
                for (dx, weight) in [(-1, 0.125), (0, 0.25), (1, 0.125)] {
                    let p = sample((2 * x) as i32 + dx, (2 * y + dy) as i32);
                    for c in 0..2 {
                        uv[c] += weight * p[c + 1];
                    }
                }
            }
            for c in 0..2 {
                out[w * h + y * w + 2 * x + c] = quantize_chroma(uv)[c] as u8;
            }
        }
    }
    Ok(out)
}

/// Shared fixed pattern for create-time self-test and WARP: black, reference white, peak,
/// over-peak and saturated source colours, with edges to exercise chroma phase and filtering.
pub fn test_pattern(width: u32, height: u32, bt2020: bool, full: bool) -> Vec<u16> {
    let (w, h) = (width as usize, height as usize);
    let mut data = vec![0; w * h * 3 / 2];
    let coeffs = luma_weights(bt2020);
    let colours = [
        [0.0; 3],
        [100.0; 3],
        [203.0; 3],
        [1000.0; 3],
        [4000.0; 3],
        [1000.0, 0.0, 0.0],
        [0.0, 1000.0, 0.0],
        [0.0, 0.0, 1000.0],
    ];
    for y in 0..h / 2 {
        for x in 0..w / 2 {
            let rgb = colours[(x + y * 3) % colours.len()].map(pq_encode);
            let luma = dot(coeffs, rgb);
            let uv = [
                (rgb[2] - luma) / (2.0 * (1.0 - coeffs[2])),
                (rgb[0] - luma) / (2.0 * (1.0 - coeffs[0])),
            ];
            let code = |v: f32| (v.round().clamp(0.0, 1023.0) as u16) << 6;
            for dy in 0..2 {
                for dx in 0..2 {
                    data[(2 * y + dy) * w + 2 * x + dx] = code(if full {
                        1023.0 * luma
                    } else {
                        64.0 + 876.0 * luma
                    });
                }
            }
            for c in 0..2 {
                data[w * h + y * w + 2 * x + c] =
                    code(512.0 + if full { 1023.0 } else { 896.0 } * uv[c]);
            }
        }
    }
    data
}

#[cfg(test)]
mod tests {
    use super::*;
    fn config(curve: u32) -> BaleToneMapConfig {
        BaleToneMapConfig {
            width: 16,
            height: 8,
            curve,
            source_peak_nits: 1000.0,
            target_white_nits: 100.0,
            ..Default::default()
        }
    }
    fn frame() -> BaleToneMapFrameInfo {
        BaleToneMapFrameInfo {
            transfer: 16,
            primaries: 9,
            matrix: 9,
            ..Default::default()
        }
    }
    #[test]
    fn st2084_and_key_curve_values_match_independent_double_precision_vectors() {
        // Double-precision ST 2084 + BT.2390 Hermite evaluation, knee offset 1;
        // Hable evaluated with npl=100. Vectors deliberately include the 203-nit reference.
        for (nits, bt2390, hable) in [
            (0.0, 0.0, 0.0),
            (100.0, 0.42740307, 0.3124507),
            (203.0, 0.6612126, 0.5107893),
            (1000.0, 1.0, 1.0),
            (4000.0, 1.0, 1.0),
        ] {
            assert!((pq_eotf(pq_encode(nits)) - nits).abs() < 0.25);
            assert!(
                (curve(&config(1), nits) - bt2390).abs() < 0.0002,
                "BT.2390 at {nits}"
            );
            assert!(
                (curve(&config(2), nits) - hable).abs() < 0.00001,
                "Hable at {nits}"
            );
        }
        for alg in [1, 2] {
            let c = config(alg);
            let mut previous = 0.0;
            for nits in 0..=10000 {
                let v = curve(&c, nits as f32);
                assert!(v >= previous - 0.0001 && (0.0..=1.0001).contains(&v));
                previous = v;
            }
        }
    }
    #[test]
    fn linear_gamut_transform_preserves_neutrals_and_clips_saturated_colours() {
        for grey in [0.0, 0.1, 0.5, 1.0] {
            for value in gamut_to_709([grey; 3], true) {
                assert!((value - grey).abs() < 1e-6);
            }
        }
        assert_eq!(gamut_to_709([1.0, 0.0, 0.0], true), [1.0, 0.0, 0.0]);
        assert_eq!(gamut_to_709([0.0, 1.0, 0.0], true), [0.0, 1.0, 0.0]);
        assert_eq!(gamut_to_709([0.0, 0.0, 1.0], true), [0.0, 0.0, 1.0]);
        assert_eq!(gamut_to_709([0.2, 0.3, 0.4], false), [0.2, 0.3, 0.4]);
        let c = config(1);
        // Black and over-range peak have legal limited NV12 values after both matrices.
        assert_eq!(pixel(&c, &frame(), 64.0, [512.0; 2]), [0.0; 3]);
    }
    #[test]
    fn limited_and_full_range_represent_the_same_light() {
        for alg in [1, 2] {
            for matrix in [1, 9] {
                let c = config(alg);
                let mut f = frame();
                f.matrix = matrix;
                f.primaries = matrix;
                for nits in [0.0, 100.0, 203.0, 1000.0, 4000.0] {
                    let signal = pq_encode(nits);
                    let limited = pixel(&c, &f, 64.0 + 876.0 * signal, [512.0; 2]);
                    f.full_range = 1;
                    let full = pixel(&c, &f, 1023.0 * signal, [512.0; 2]);
                    f.full_range = 0;
                    for i in 0..3 {
                        assert!((limited[i] - full[i]).abs() < 0.0001);
                    }
                }
            }
        }
    }
    #[test]
    fn default_hable_desat_is_explicitly_zero_and_positive_desat_neutralizes_highlights() {
        let f = frame();
        let c = config(2);
        let vivid = pixel(&c, &f, 500.0, [400.0, 850.0]);
        let neutral = pixel(
            &BaleToneMapConfig { desat: 0.0001, ..c },
            &f,
            500.0,
            [400.0, 850.0],
        );
        assert!(neutral[1].abs() + neutral[2].abs() < vivid[1].abs() + vivid[2].abs());
    }
    #[test]
    fn visible_edges_ignore_decoder_padding_and_chroma_location_defaults_to_left() {
        let mut c = config(1);
        c.width = 12;
        c.height = 6;
        let f = frame();
        let input = test_pattern(16, 8, true, false);
        let expected = reference(&c, &f, &input, 16, 8).unwrap();
        let mut padded = input.clone();
        for y in 0..8 {
            for x in 0..16 {
                if x >= 12 || y >= 6 {
                    padded[y * 16 + x] = 65535;
                }
            }
        }
        for y in 0..4 {
            for x in 0..16 {
                if x >= 12 || y >= 3 {
                    padded[16 * 8 + y * 16 + x] = 65535;
                }
            }
        }
        assert_eq!(reference(&c, &f, &padded, 16, 8).unwrap(), expected);
        for loc in [0, 1, 2, 3, 4, 5, 255] {
            assert_eq!(
                reference(
                    &c,
                    &BaleToneMapFrameInfo {
                        chroma_location: loc,
                        ..f
                    },
                    &input,
                    16,
                    8
                )
                .unwrap(),
                expected
            );
        }
        assert!(expected[..72].iter().all(|&v| (16..=235).contains(&v)));
        assert!(expected[72..].iter().all(|&v| (16..=240).contains(&v)));
    }
    #[test]
    fn config_and_frame_validation_reject_unsupported_values() {
        let c = config(1);
        for bad in [
            BaleToneMapConfig { width: 0, ..c },
            BaleToneMapConfig { height: 3, ..c },
            BaleToneMapConfig { curve: 0, ..c },
            BaleToneMapConfig { curve: 3, ..c },
            BaleToneMapConfig {
                source_peak_nits: 0.0,
                ..c
            },
            BaleToneMapConfig {
                target_white_nits: 0.0,
                ..c
            },
            BaleToneMapConfig { desat: -1.0, ..c },
            BaleToneMapConfig {
                reserved: [0, 1, 0, 0],
                ..c
            },
        ] {
            assert_eq!(bad.validate(), Err(E_INVALIDARG));
        }
        for nonfinite in [f32::NAN, f32::INFINITY, f32::NEG_INFINITY] {
            for bad in [
                BaleToneMapConfig {
                    source_peak_nits: nonfinite,
                    ..c
                },
                BaleToneMapConfig {
                    target_white_nits: nonfinite,
                    ..c
                },
                BaleToneMapConfig {
                    desat: nonfinite,
                    ..c
                },
            ] {
                assert_eq!(bad.validate(), Err(E_INVALIDARG));
            }
        }
        let f = frame();
        for bad in [
            BaleToneMapFrameInfo { transfer: 18, ..f },
            BaleToneMapFrameInfo { primaries: 1, ..f },
            BaleToneMapFrameInfo { matrix: 1, ..f },
            BaleToneMapFrameInfo { full_range: 2, ..f },
        ] {
            assert_eq!(bad.validate(), Err(E_INVALIDARG));
        }
        assert!(
            BaleToneMapFrameInfo {
                has_mastering: 1,
                has_content_light: 1,
                mastering_max_nits: f32::NAN,
                max_cll: 4000,
                max_fall: 1000,
                ..f
            }
            .validate()
            .is_ok()
        );
    }
}
