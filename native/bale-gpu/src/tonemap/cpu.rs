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
//! transfer, rather than its scene-referred camera OETF. No integer LUTs. P010 output is plainly
//! rounded; NV12 output is ordered-dithered (static 8x8 Bayer, `quantize_luma_dithered`).
use super::types::{BaleToneMapConfig, BaleToneMapFrameInfo, MAX_DOWNSCALE};

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
            || self.reserved[2..] != [0; 2]
        {
            return Err(E_INVALIDARG);
        }
        // reserved[0..2]: visible source size for an area downscale (both zero = same as output).
        let [sw, sh] = [self.reserved[0], self.reserved[1]];
        if (sw, sh) != (0, 0)
            && (sw == 0
                || sh == 0
                || !sw.is_multiple_of(2)
                || !sh.is_multiple_of(2)
                || sw < self.width
                || sh < self.height
                || sw > self.width.saturating_mul(MAX_DOWNSCALE)
                || sh > self.height.saturating_mul(MAX_DOWNSCALE))
        {
            return Err(E_INVALIDARG);
        }
        Ok(())
    }

    /// Visible source size: `reserved[0..2]` when set, else the output size.
    pub fn source_size(&self) -> (u32, u32) {
        match [self.reserved[0], self.reserved[1]] {
            [0, 0] => (self.width, self.height),
            [w, h] => (w, h),
        }
    }
}
impl BaleToneMapFrameInfo {
    pub fn validate(&self) -> Result<(), i32> {
        // Metadata is intentionally ignored, including reserved fields, in ABI 1.
        // PQ (16) is tone mapped from BT.2020 or BT.709. HLG (18, BT.2020) goes through the
        // BT.2100 OOTF at the 1000-nit nominal peak, then the same curve. SDR on the
        // BT.709/BT.1886 curve (H.273 1, 6, 14, 15) with BT.2020 primaries only needs the
        // gamut conversion.
        let supported = match self.transfer {
            16 => matches!((self.primaries, self.matrix), (9, 9) | (1, 1)),
            18 => (self.primaries, self.matrix) == (9, 9),
            // SDR: BT.2020 gamut conversion, or BT.709 scale only (BALE_TONEMAP_SDR709).
            1 | 6 | 14 | 15 => matches!((self.primaries, self.matrix), (9, 9) | (1, 1)),
            _ => false,
        };
        if !supported || self.full_range > 1 {
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
/// SDR sources are display-referred: decode with the BT.1886 EOTF (power 2.4) to linear 0..1.
pub fn sdr_decode(signal: f32) -> f32 {
    signal.clamp(0.0, 1.0).powf(2.4)
}
/// Sign-preserving BT.1886 decode/encode for the BT.709 scale-only pass: Y'CbCr that maps outside
/// 0..1 RGB (saturated colours) survives the round trip instead of being clipped.
pub fn sdr_decode_signed(signal: f32) -> f32 {
    signal.signum() * signal.abs().min(1.5).powf(2.4)
}
pub fn sdr_encode_signed(linear: f32) -> f32 {
    linear.signum() * linear.abs().min(3.0).powf(1.0 / 2.4)
}
/// Source transfer modes, matching the shader's flags.w.
pub const MODE_PQ: u32 = 0;
pub const MODE_SDR: u32 = 1;
pub const MODE_HLG: u32 = 2;
pub fn mode(transfer: u8) -> u32 {
    match transfer {
        16 => MODE_PQ,
        18 => MODE_HLG,
        _ => MODE_SDR,
    }
}
/// HLG nominal display peak (BT.2100): the OOTF's system gamma is 1.2 at 1000 nits.
pub const HLG_PEAK_NITS: f32 = 1000.0;
/// BT.2100 HLG inverse OETF: non-linear signal to normalized scene light 0..1.
pub fn hlg_inverse_oetf(signal: f32) -> f32 {
    let (a, b, c) = (0.178_832_77, 0.284_668_92, 0.559_910_7);
    let e = signal.clamp(0.0, 1.0);
    if e <= 0.5 {
        e * e / 3.0
    } else {
        (((e - c) / a).exp() + b) / 12.0
    }
}
/// BT.2100 HLG OOTF at the nominal peak: display nits = Lw * Ys^(gamma-1) * E per channel.
pub fn hlg_ootf(scene: [f32; 3]) -> [f32; 3] {
    let ys = dot(luma_weights(true), scene).max(0.0);
    let gain = HLG_PEAK_NITS * ys.powf(0.2);
    scene.map(|v| v * gain)
}
pub fn source_decode(y: f32, uv: [f32; 2], bt2020: bool, full_range: bool, mode: u32) -> [f32; 3] {
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
    match mode {
        MODE_SDR if bt2020 => [r, g, b].map(sdr_decode),
        MODE_SDR => [r, g, b].map(sdr_decode_signed),
        MODE_HLG => hlg_ootf([r, g, b].map(hlg_inverse_oetf)),
        _ => [r, g, b].map(pq_eotf),
    }
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
    mode: u32,
) -> [f32; 3] {
    encode_linear(tone_map_linear(
        y,
        uv,
        source_peak_nits,
        target_white_nits,
        desat,
        curve,
        bt2020,
        full_range,
        mode,
    ))
}
/// BT.709 display-linear RGB (0..1) to Y'CbCr through the inverse BT.1886 curve.
pub fn encode_linear(rgb: [f32; 3]) -> [f32; 3] {
    // Identical to sdr_encode inside 0..1 (every tone-mapped and gamut-converted value);
    // only the unclipped BT.709 scale-only pass reaches outside it.
    rgb709_to_ycbcr(rgb.map(sdr_encode_signed))
}
/// Tone-mapped display-linear BT.709 RGB in 0..1: the light domain the area downscale averages in.
#[allow(clippy::too_many_arguments)]
pub fn tone_map_linear(
    y: f32,
    uv: [f32; 2],
    source_peak_nits: f32,
    target_white_nits: f32,
    desat: f32,
    curve: u32,
    bt2020: bool,
    full_range: bool,
    mode: u32,
) -> [f32; 3] {
    if mode == MODE_SDR {
        // BT.2020 SDR: no tone curve, only BT.2020 -> BT.709 in linear light, clip, re-encode.
        let rgb = source_decode(y, uv, bt2020, full_range, MODE_SDR);
        // BT.709 SDR is scale only: no clip, so the final quantization alone limits the range.
        return if bt2020 { gamut_to_709(rgb, true) } else { rgb };
    }
    // PQ and HLG both arrive here as display light in nits.
    let mut rgb = source_decode(y, uv, bt2020, full_range, mode);
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
    gamut_to_709(rgb.map(|v| v * scale), bt2020 && curve == 2)
}
pub fn pixel_linear(
    config: &BaleToneMapConfig,
    frame: &BaleToneMapFrameInfo,
    y: f32,
    uv: [f32; 2],
) -> [f32; 3] {
    tone_map_linear(
        y,
        uv,
        config.source_peak_nits,
        config.target_white_nits,
        config.desat,
        config.curve,
        frame.matrix == 9,
        frame.full_range == 1,
        mode(frame.transfer),
    )
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
        mode(frame.transfer),
    )
}
pub fn quantize_luma(y: f32) -> f32 {
    (16.0 + 219.0 * y + 0.5).floor().clamp(16.0, 235.0)
}
pub fn quantize_chroma(uv: [f32; 2]) -> [f32; 2] {
    uv.map(|v| (128.0 + 224.0 * v + 0.5).floor().clamp(16.0, 240.0))
}
/// 10-bit limited-range codes for P010 output (ABI 2).
pub fn quantize_luma10(y: f32) -> f32 {
    (64.0 + 876.0 * y + 0.5).floor().clamp(64.0, 940.0)
}
pub fn quantize_chroma10(uv: [f32; 2]) -> [f32; 2] {
    uv.map(|v| (512.0 + 896.0 * v + 0.5).floor().clamp(64.0, 960.0))
}
/// The 8x8 Bayer index (0..64) of an output pixel, as the shader's `bayer8`: each level adds the 2x2
/// matrix [0 2; 3 1] below the previous one, so the finest bit pair is the most significant.
pub fn bayer8(x: u32, y: u32) -> u32 {
    (0..3).fold(0, |v, i| {
        let (bx, by) = ((x >> i) & 1, (y >> i) & 1);
        v * 4 + ((bx ^ by) << 1) + by
    })
}
/// Rounding offset in (0, 1) with mean 0.5: `floor(x + t)` replaces plain `floor(x + 0.5)`. Exact in
/// f32 (k/128 values), so the CPU and the shader add the same number.
pub fn dither_threshold(index: u32) -> f32 {
    (index as f32 + 0.5) / 64.0
}
/// NV12 luma with a static ordered dither (BALE_TONEMAP_OUTPUT_NV12_DITHERED); see the shader's
/// comment in tonemap_math.hlsli for why a constant Bayer pattern. Never more than one code from
/// the exact value (plain rounding: half a code).
pub fn quantize_luma_dithered(y: f32, x: u32, row: u32) -> f32 {
    (16.0 + 219.0 * y + dither_threshold(bayer8(x, row)))
        .floor()
        .clamp(16.0, 235.0)
}
/// NV12 chroma at chroma-plane position (x, row); Cr uses the mirrored threshold.
pub fn quantize_chroma_dithered(uv: [f32; 2], x: u32, row: u32) -> [f32; 2] {
    let index = bayer8(x, row);
    let t = [dither_threshold(index), dither_threshold(63 - index)];
    std::array::from_fn(|c| (128.0 + 224.0 * uv[c] + t[c]).floor().clamp(16.0, 240.0))
}

/// Tight P010 words (high 10 bits), input may include decoder alignment beyond visible size.
/// Chroma upsampling: bilinear, centres at (2*x, 2*y+0.5) in luma-centre coordinates.
/// Downsampling: horizontal [1 2 1]/4 centred at 2*x, vertical [1 1]/2. Clamp visible
/// edges, never sample alignment padding. This is left-sited, unlike a centred 2x2 box.
/// NV12 8-bit codes, the ABI 1 output, ordered-dithered like the shader.
pub fn reference(
    config: &BaleToneMapConfig,
    frame: &BaleToneMapFrameInfo,
    input: &[u16],
    width: u32,
    height: u32,
) -> Result<Vec<u8>, i32> {
    Ok(reference_codes(config, frame, input, width, height, false)?
        .into_iter()
        .map(|code| code as u8)
        .collect())
}

/// P010 10-bit codes (unshifted, 64-940/960), the ABI 2 output.
pub fn reference_p010(
    config: &BaleToneMapConfig,
    frame: &BaleToneMapFrameInfo,
    input: &[u16],
    width: u32,
    height: u32,
) -> Result<Vec<u16>, i32> {
    reference_codes(config, frame, input, width, height, true)
}

fn reference_codes(
    config: &BaleToneMapConfig,
    frame: &BaleToneMapFrameInfo,
    input: &[u16],
    width: u32,
    height: u32,
    ten_bit: bool,
) -> Result<Vec<u16>, i32> {
    config.validate()?;
    frame.validate()?;
    let (w, h, iw, ih) = (
        config.width as usize,
        config.height as usize,
        width as usize,
        height as usize,
    );
    let (sw, sh) = config.source_size();
    let (sw, sh) = (sw as usize, sh as usize);
    let len = iw
        .checked_mul(ih)
        .and_then(|n| n.checked_add(n / 2))
        .ok_or(E_INVALIDARG)?;
    if !width.is_multiple_of(2)
        || !height.is_multiple_of(2)
        || iw < sw
        || ih < sh
        || input.len() != len
    {
        return Err(E_INVALIDARG);
    }
    let uv_at = |x: i32, y: i32, plane: usize| {
        let x = x.clamp(0, sw as i32 / 2 - 1) as usize;
        let y = y.clamp(0, sh as i32 / 2 - 1) as usize;
        (input[iw * ih + y * iw + 2 * x + plane] >> 6) as f32
    };
    let sample = |x: i32, y: i32| {
        let x = x.clamp(0, sw as i32 - 1);
        let y = y.clamp(0, sh as i32 - 1);
        let cx = x as f32 * 0.5;
        let cy = (y as f32 - 0.5) * 0.5;
        let (ix, iy) = (cx.floor() as i32, cy.floor() as i32);
        let (fx, fy) = (cx - ix as f32, cy - iy as f32);
        let uv = std::array::from_fn(|p| {
            let a = uv_at(ix, iy, p) * (1.0 - fx) + uv_at(ix + 1, iy, p) * fx;
            let b = uv_at(ix, iy + 1, p) * (1.0 - fx) + uv_at(ix + 1, iy + 1, p) * fx;
            a * (1.0 - fy) + b * fy
        });
        pixel_linear(
            config,
            frame,
            (input[y as usize * iw + x as usize] >> 6) as f32,
            uv,
        )
    };
    // Area downscale in display-linear light, mirroring the shader's output_pixel.
    let (scale_x, scale_y) = (sw as f32 / w as f32, sh as f32 / h as f32);
    let output_pixel = |ox: i32, oy: i32| {
        let ox = ox.clamp(0, w as i32 - 1);
        let oy = oy.clamp(0, h as i32 - 1);
        let (lo_x, lo_y) = (ox as f32 * scale_x, oy as f32 * scale_y);
        let (hi_x, hi_y) = (lo_x + scale_x, lo_y + scale_y);
        let last_x = (hi_x.ceil() as i32 - 1).min(sw as i32 - 1);
        let last_y = (hi_y.ceil() as i32 - 1).min(sh as i32 - 1);
        let (mut sum, mut total) = ([0.0f32; 3], 0.0f32);
        for sy in lo_y.floor() as i32..=last_y {
            let wy = (sy as f32 + 1.0).min(hi_y) - (sy as f32).max(lo_y);
            for sx in lo_x.floor() as i32..=last_x {
                let weight = wy * ((sx as f32 + 1.0).min(hi_x) - (sx as f32).max(lo_x));
                let rgb = sample(sx, sy);
                for c in 0..3 {
                    sum[c] += weight * rgb[c];
                }
                total += weight;
            }
        }
        encode_linear(sum.map(|v| v / total.max(1e-6)))
    };
    let mut out = vec![0; w * h * 3 / 2];
    for y in 0..h {
        for x in 0..w {
            let luma = output_pixel(x as i32, y as i32)[0];
            out[y * w + x] = if ten_bit {
                quantize_luma10(luma)
            } else {
                quantize_luma_dithered(luma, x as u32, y as u32)
            } as u16;
        }
    }
    for y in 0..h / 2 {
        for x in 0..w / 2 {
            let mut uv = [0.0; 2];
            for dy in 0..2 {
                for (dx, weight) in [(-1, 0.125), (0, 0.25), (1, 0.125)] {
                    let p = output_pixel((2 * x) as i32 + dx, (2 * y + dy) as i32);
                    for c in 0..2 {
                        uv[c] += weight * p[c + 1];
                    }
                }
            }
            let codes = if ten_bit {
                quantize_chroma10(uv)
            } else {
                quantize_chroma_dithered(uv, x as u32, y as u32)
            };
            for c in 0..2 {
                out[w * h + y * w + 2 * x + c] = codes[c] as u16;
            }
        }
    }
    Ok(out)
}

/// A smooth BT.709 SDR picture (P010 words) for dither tests: luma rises from `from` to `to`
/// (10-bit codes) left to right, chroma from 500 to 530 top to bottom; a slow ramp that plain
/// 8-bit rounding turns into a few flat bands.
pub fn sdr_ramp(width: u32, height: u32, from: f32, to: f32) -> Vec<u16> {
    let (w, h) = (width as usize, height as usize);
    let mut data = vec![0; w * h * 3 / 2];
    let step = |i: usize, n: usize| i as f32 / (n.max(2) - 1) as f32;
    for y in 0..h {
        for x in 0..w {
            data[y * w + x] = ((from + (to - from) * step(x, w)).round() as u16) << 6;
        }
    }
    for y in 0..h / 2 {
        let v = (500.0 + 30.0 * step(y, h / 2)).round() as u16;
        for x in 0..w / 2 {
            data[w * h + y * w + 2 * x] = v << 6;
            data[w * h + y * w + 2 * x + 1] = (1030 - v) << 6;
        }
    }
    data
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
    fn bt2020_sdr_is_gamut_converted_without_a_tone_curve() {
        let c = config(1);
        let sdr = BaleToneMapFrameInfo {
            transfer: 14,
            primaries: 9,
            matrix: 9,
            ..frame()
        };
        assert!(sdr.validate().is_ok());
        assert!(
            BaleToneMapFrameInfo { transfer: 1, ..sdr }
                .validate()
                .is_ok()
        );
        // BT.709 SDR is accepted for the scale-only route (BALE_TONEMAP_SDR709); mixed tags are not.
        assert!(
            BaleToneMapFrameInfo {
                primaries: 1,
                matrix: 1,
                ..sdr
            }
            .validate()
            .is_ok()
        );
        for (primaries, matrix) in [(1, 9), (9, 1), (2, 2)] {
            assert!(
                BaleToneMapFrameInfo {
                    primaries,
                    matrix,
                    ..sdr
                }
                .validate()
                .is_err()
            );
        }
        assert!(
            BaleToneMapFrameInfo {
                transfer: 17,
                ..sdr
            }
            .validate()
            .is_err()
        );
        // Neutral grey keeps its level: the matrix rows sum to 1 and no curve applies.
        for level in [64.0, 300.0, 600.0, 940.0] {
            let out = pixel(&c, &sdr, level, [512.0, 512.0]);
            assert!(
                (out[0] - (level - 64.0) / 876.0).abs() < 0.002,
                "level {level}: {out:?}"
            );
            assert!(out[1].abs() < 0.002 && out[2].abs() < 0.002);
        }
        // A BT.2020 colour shown unconverted reads less saturated than its BT.709 conversion.
        let converted = pixel(&c, &sdr, 400.0, [470.0, 650.0]);
        let naive_v: f32 = (650.0 - 512.0) / 896.0;
        assert!(converted[2].abs() > naive_v.abs(), "{converted:?}");
    }
    #[test]
    fn hlg_reference_white_lands_where_pq_203_nits_does() {
        let c = config(1);
        let hlg = BaleToneMapFrameInfo {
            transfer: 18,
            primaries: 9,
            matrix: 9,
            ..frame()
        };
        assert!(hlg.validate().is_ok());
        assert!(
            BaleToneMapFrameInfo {
                primaries: 1,
                matrix: 1,
                ..hlg
            }
            .validate()
            .is_err()
        );
        // BT.2408: HLG 75% signal is 203-nit reference white on a 1000-nit display.
        let scene = hlg_inverse_oetf(0.75);
        let nits = hlg_ootf([scene; 3]);
        assert!((nits[0] - 203.0).abs() < 1.5, "{nits:?}");
        // So a neutral HLG 75% grey maps like a neutral PQ grey at the same nits.
        let code = 64.0 + 876.0 * 0.75;
        let out_hlg = pixel(&c, &hlg, code, [512.0, 512.0]);
        let pq_code = 64.0 + 876.0 * pq_encode(nits[0]);
        let pq = BaleToneMapFrameInfo {
            transfer: 16,
            ..hlg
        };
        let out_pq = pixel(&c, &pq, pq_code, [512.0, 512.0]);
        assert!(
            (out_hlg[0] - out_pq[0]).abs() < 0.002,
            "{out_hlg:?} vs {out_pq:?}"
        );
        // Black stays black, and brighter signals stay brighter.
        assert!(pixel(&c, &hlg, 64.0, [512.0, 512.0])[0] < 0.01);
        assert!(pixel(&c, &hlg, 900.0, [512.0, 512.0])[0] > out_hlg[0]);
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
    /// BT.709 SDR at 1:1 is a scale-only pass: every code comes back (within rounding), so a
    /// display-sized 8- or 10-bit SDR picture is not altered by going through the stage.
    #[test]
    fn bt709_sdr_scale_only_round_trips_at_one_to_one() {
        let c = config(1);
        let f = BaleToneMapFrameInfo {
            transfer: 1,
            primaries: 1,
            matrix: 1,
            ..frame()
        };
        let input = test_pattern(16, 8, false, false);
        let out = reference_p010(&c, &f, &input, 16, 8).unwrap();
        for (i, &code) in out[..16 * 8].iter().enumerate() {
            let source = (input[i] >> 6).clamp(64, 940);
            assert!(code.abs_diff(source) <= 2, "luma {i}: {source} -> {code}");
        }
    }
    #[test]
    fn downscale_source_size_validation() {
        let c = config(1); // 16x8 output
        let with = |reserved| BaleToneMapConfig { reserved, ..c };
        for good in [
            [0, 0, 0, 0],
            [32, 16, 0, 0],
            [16, 8, 0, 0],
            [64, 32, 0, 0],
            [30, 12, 0, 0],
        ] {
            assert!(with(good).validate().is_ok(), "{good:?}");
        }
        for bad in [
            [0, 16, 0, 0],
            [32, 0, 0, 0],
            [33, 16, 0, 0],
            [14, 8, 0, 0],
            [66, 16, 0, 0],
            [32, 16, 1, 0],
            [32, 16, 0, 1],
        ] {
            assert!(with(bad).validate().is_err(), "{bad:?}");
        }
        assert_eq!(with([32, 16, 0, 0]).source_size(), (32, 16));
        assert_eq!(c.source_size(), (16, 8));
    }
    /// A uniform picture stays the same picture at any area-downscale ratio.
    #[test]
    fn downscale_of_a_uniform_picture_matches_one_to_one() {
        let f = frame();
        let (w, h) = (32usize, 16usize);
        let mut input = vec![0u16; w * h * 3 / 2];
        input[..w * h].fill(600 << 6);
        input[w * h..]
            .iter_mut()
            .enumerate()
            .for_each(|(i, v)| *v = if i % 2 == 0 { 540 << 6 } else { 470 << 6 });
        let one = BaleToneMapConfig {
            width: 32,
            height: 16,
            ..config(1)
        };
        let half = BaleToneMapConfig {
            reserved: [32, 16, 0, 0],
            ..config(1)
        };
        let third = BaleToneMapConfig {
            width: 10,
            height: 6,
            reserved: [30, 16, 0, 0],
            ..config(1)
        };
        let reference_one = reference_p010(&one, &f, &input, 32, 16).unwrap();
        for c in [half, third] {
            let scaled = reference_p010(&c, &f, &input, 32, 16).unwrap();
            let (ow, oh) = (c.width as usize, c.height as usize);
            assert!(
                scaled[..ow * oh]
                    .iter()
                    .all(|&v| v.abs_diff(reference_one[0]) <= 1)
            );
            assert!(scaled[ow * oh..].as_chunks::<2>().0.iter().all(|uv| {
                uv[0].abs_diff(reference_one[w * h]) <= 1
                    && uv[1].abs_diff(reference_one[w * h + 1]) <= 1
            }));
        }
    }
    /// Blending happens in display-linear light: a 2x2 block of one bright and three black pixels
    /// encodes to more than the average of the four encoded values (a gamma-space blend).
    #[test]
    fn downscale_blends_in_linear_light() {
        let f = frame();
        let (w, h) = (4usize, 4usize);
        let mut input = vec![512u16 << 6; w * h * 3 / 2]; // neutral chroma
        let luma = |x: usize, y: usize| {
            if x.is_multiple_of(2) && y.is_multiple_of(2) {
                700u16
            } else {
                64
            }
        };
        for y in 0..h {
            for x in 0..w {
                input[y * w + x] = luma(x, y) << 6;
            }
        }
        let one = BaleToneMapConfig {
            width: 4,
            height: 4,
            ..config(1)
        };
        let half = BaleToneMapConfig {
            width: 2,
            height: 2,
            reserved: [4, 4, 0, 0],
            ..config(1)
        };
        let full = reference_p010(&one, &f, &input, 4, 4).unwrap();
        let scaled = reference_p010(&half, &f, &input, 4, 4).unwrap();
        let gamma_mean = (full[0] + full[1] + full[4] + full[5]) as f32 / 4.0;
        assert!(
            scaled[0] as f32 > gamma_mean + 20.0,
            "linear {} vs gamma mean {gamma_mean}",
            scaled[0]
        );
        assert!(
            scaled[0] < full[0],
            "the average is darker than the bright pixel"
        );
    }
    #[test]
    fn p010_output_is_the_same_picture_at_ten_bits() {
        for curve in [1, 2] {
            let (c, f) = (config(curve), frame());
            let input = test_pattern(16, 8, true, false);
            let eight = reference(&c, &f, &input, 16, 8).unwrap();
            let ten = reference_p010(&c, &f, &input, 16, 8).unwrap();
            assert_eq!(eight.len(), ten.len());
            // Same limited-range value quantized at 8 and 10 bits: 4x the 8-bit code within rounding.
            // The dithered 8-bit code is under one code (4 at 10 bits) from the exact value, the
            // 10-bit one within half a 10-bit code.
            for (&a, &b) in eight.iter().zip(&ten) {
                assert!((i32::from(a) * 4 - i32::from(b)).abs() <= 4, "{a} vs {b}");
            }
            assert!(ten.iter().all(|&code| (64..=960).contains(&code)));
            assert!(
                ten.iter().any(|&code| code % 4 != 0),
                "10-bit output must keep sub-8-bit steps"
            );
        }
    }
    #[test]
    fn bayer8_is_a_permutation_with_zero_mean_offset() {
        let mut seen = [false; 64];
        for y in 0..8 {
            for x in 0..8 {
                let v = bayer8(x, y) as usize;
                assert!(!seen[v], "({x},{y}) repeats {v}");
                seen[v] = true;
                // Periodic: only the low three bits of each coordinate matter.
                assert_eq!(bayer8(x + 8, y + 16), bayer8(x, y));
            }
        }
        assert_eq!(bayer8(0, 0), 0);
        assert_eq!(bayer8(1, 1), 16);
        let mean = (0..64).map(dither_threshold).sum::<f32>() / 64.0;
        assert!((mean - 0.5).abs() < 1e-6);
        // Every threshold is strictly inside (0, 1): never two codes away from plain rounding.
        assert!(
            (0..64).all(|v| (0.0..1.0).contains(&dither_threshold(v)) && dither_threshold(v) > 0.0)
        );
    }
    /// A slow ramp: dithered codes stay within one code of the exact value, the mean error over
    /// the ramp is ~0, and local averages resolve many more levels than plain rounding's bands.
    #[test]
    fn dither_removes_banding_without_biasing_a_smooth_gradient() {
        let (w, h) = (512u32, 8u32);
        // 0.40..0.44 of the luma range: under nine 8-bit codes across 512 columns.
        let exact = |x: u32| 16.0 + 219.0 * (0.40 + 0.04 * x as f32 / (w - 1) as f32);
        let mut error = 0.0f64;
        let (mut plain_levels, mut dithered_levels) = (Vec::new(), Vec::new());
        for x in 0..w {
            let y = (exact(x) - 16.0) / 219.0;
            let mut column = 0.0;
            for row in 0..h {
                let d = quantize_luma_dithered(y, x, row);
                assert!(
                    (d - exact(x)).abs() < 1.0,
                    "x={x} row={row}: {d} vs {}",
                    exact(x)
                );
                assert!((d - quantize_luma(y)).abs() <= 1.0);
                error += f64::from(d - exact(x));
                column += d;
            }
            dithered_levels.push((column / h as f32 * 64.0).round() as i32);
            plain_levels.push(quantize_luma(y) as i32);
        }
        let mean_error = error / f64::from(w * h);
        assert!(mean_error.abs() < 0.02, "mean error {mean_error}");
        plain_levels.dedup();
        dithered_levels.dedup();
        assert!(plain_levels.len() <= 10, "{}", plain_levels.len());
        assert!(
            dithered_levels.len() >= 4 * plain_levels.len(),
            "dithered {} vs plain {} levels",
            dithered_levels.len(),
            plain_levels.len()
        );
        // Chroma: both planes unbiased over an 8x8 tile, Cr on the mirrored pattern.
        for uv in [[0.013f32, -0.021], [0.1, 0.2], [-0.3, 0.004]] {
            let mut sum = [0.0f32; 2];
            for row in 0..8 {
                for x in 0..8 {
                    let q = quantize_chroma_dithered(uv, x, row);
                    for c in 0..2 {
                        sum[c] += q[c] - (128.0 + 224.0 * uv[c]);
                    }
                }
            }
            assert!(
                sum.iter().all(|s| (s / 64.0).abs() < 1.0 / 64.0 + 1e-4),
                "{sum:?}"
            );
        }
    }
    /// The full reference on a smooth SDR ramp: NV12 codes within one code of the P010 result
    /// (exact at 8 bits), unbiased, and more local levels than plainly rounding that same result.
    #[test]
    fn dithered_nv12_reference_tracks_p010_on_a_smooth_ramp() {
        let c = BaleToneMapConfig {
            width: 256,
            height: 16,
            ..config(1)
        };
        let f = BaleToneMapFrameInfo {
            transfer: 1,
            primaries: 1,
            matrix: 1,
            ..frame()
        };
        let input = sdr_ramp(256, 16, 400.0, 416.0);
        let eight = reference(&c, &f, &input, 256, 16).unwrap();
        let ten = reference_p010(&c, &f, &input, 256, 16).unwrap();
        let mut error = 0.0f64;
        for (i, (&a, &b)) in eight.iter().zip(&ten).enumerate() {
            let exact = f32::from(b) / 4.0;
            assert!(
                (f32::from(a) - exact).abs() <= 1.0 + 0.125,
                "{i}: {a} vs {exact}"
            );
            error += f64::from(f32::from(a) - exact);
        }
        assert!((error / eight.len() as f64).abs() < 0.05);
        let levels = |codes: &[u8]| {
            let mut means: Vec<u32> = (0..256)
                .step_by(8)
                .map(|x| {
                    (0..16)
                        .flat_map(|y| (0..8).map(move |dx| (x + dx, y)))
                        .map(|(x, y)| u32::from(codes[y * 256 + x]))
                        .sum()
                })
                .collect();
            means.dedup();
            means.len()
        };
        let plain: Vec<u8> = ten[..256 * 16]
            .iter()
            .map(|&b| ((f32::from(b) / 4.0) + 0.5).floor() as u8)
            .collect();
        assert!(
            levels(&eight[..256 * 16]) >= 3 * levels(&plain),
            "{} vs {}",
            levels(&eight[..256 * 16]),
            levels(&plain)
        );
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
            BaleToneMapFrameInfo { transfer: 17, ..f },
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
