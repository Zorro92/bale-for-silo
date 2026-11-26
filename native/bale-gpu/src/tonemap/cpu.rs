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
//! rounded; NV12 output is ordered-dithered (static 8x8 Bayer, `quantize_luma_dithered`; debug
//! `r2dither`: rav1g's R2 threshold, [`Dither::R2`]).
use super::types::{BaleToneMapConfig, BaleToneMapFrameInfo, MAX_DOWNSCALE};

pub const E_INVALIDARG: i32 = 0x80070057u32 as i32;

impl BaleToneMapConfig {
    pub fn validate(&self) -> Result<(), i32> {
        if self.width == 0
            || self.height == 0
            || !self.width.is_multiple_of(2)
            || !self.height.is_multiple_of(2)
            || !matches!(self.curve, 1 | 2)
            // 0 = no configured peak: the stream's metadata, else DEFAULT_PEAK_NITS (2026-11-24).
            || !self.source_peak_nits.is_finite()
            || self.source_peak_nits < 0.0
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
        // Metadata never fails validation (reserved fields are ignored); PQ frames take their source
        // peak from it ([`source_peak`]), with unusable values treated as absent.
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
/// Extended (sign-preserving) PQ: a signal below 0 (footroom, chroma overshoot at a saturated edge)
/// becomes negative light instead of black, so the area average keeps it and the one late clip
/// (`finish_linear`) decides. Magnitude capped at 1 (10000 nits) by `pq_eotf`'s clamp.
pub fn pq_eotf_signed(signal: f32) -> f32 {
    signal.signum() * pq_eotf(signal.abs())
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
/// The source peak when neither the config nor the stream names one: the server's default
/// (tonemapx peak=100).
pub const DEFAULT_PEAK_NITS: f32 = 1000.0;
/// A metadata peak is clamped to this range: below 400 nits MaxCLL or a mastering maximum is broken
/// metadata (no HDR grade peaks that low), and 10000 nits is PQ's ceiling.
pub const METADATA_PEAK_MIN_NITS: f32 = 400.0;
pub const METADATA_PEAK_MAX_NITS: f32 = 10000.0;
/// Where a frame's source peak comes from (`BaleToneMapPeakInfo.source`; Bale's log line uses
/// [`Self::name`]).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u32)]
pub enum PeakSource {
    /// `source_peak_nits` (0: [`DEFAULT_PEAK_NITS`]): HLG, PQ without metadata, debug `fixedpeak`.
    Config = 1,
    /// The stream's MaxCLL (its brightest pixel).
    MaxCll = 2,
    /// The stream's mastering display maximum luminance.
    Mastering = 3,
}
impl PeakSource {
    pub fn name(self) -> &'static str {
        match self {
            Self::Config => "config",
            Self::MaxCll => "maxcll",
            Self::Mastering => "mastering",
        }
    }
}
/// The configured peak: `source_peak_nits`, or [`DEFAULT_PEAK_NITS`] when the config leaves it 0.
pub fn config_peak(config: &BaleToneMapConfig) -> f32 {
    if config.source_peak_nits > 0.0 {
        config.source_peak_nits
    } else {
        DEFAULT_PEAK_NITS
    }
}
/// A frame's usable metadata peaks (MaxCLL, mastering maximum): signalled and above 0 (the mastering
/// value also finite). Anything else counts as absent.
pub fn metadata_peaks(frame: &BaleToneMapFrameInfo) -> (Option<f32>, Option<f32>) {
    let cll = (frame.has_content_light != 0 && frame.max_cll > 0).then_some(frame.max_cll as f32);
    let mastering = (frame.has_mastering != 0
        && frame.mastering_max_nits.is_finite()
        && frame.mastering_max_nits > 0.0)
        .then_some(frame.mastering_max_nits);
    (cll, mastering)
}
/// The source peak a frame is tone-mapped from, and where it comes from (2026-11-24). PQ only: the
/// stream's MaxCLL, else its mastering display maximum, else the config peak; a MaxCLL above the
/// mastering maximum (broken metadata: no pixel is brighter than the display it was graded on) gives
/// the mastering maximum. A metadata peak is clamped to [400, 10000] nits; the config peak is used as
/// configured. HLG keeps the config peak (its OOTF already renders for a 1000-nit display) and SDR
/// has no tone curve. `fixed` (debug `fixedpeak`) always takes the config peak. The metadata is per
/// stream in practice, so the result changes only when the stream's metadata does.
pub fn source_peak(
    config: &BaleToneMapConfig,
    frame: &BaleToneMapFrameInfo,
    fixed: bool,
) -> (f32, PeakSource) {
    let configured = (config_peak(config), PeakSource::Config);
    if fixed || mode(frame.transfer) != MODE_PQ {
        return configured;
    }
    let (nits, source) = match metadata_peaks(frame) {
        (Some(cll), Some(mastering)) if cll > mastering => (mastering, PeakSource::Mastering),
        (Some(cll), _) => (cll, PeakSource::MaxCll),
        (None, Some(mastering)) => (mastering, PeakSource::Mastering),
        (None, None) => return configured,
    };
    (
        nits.clamp(METADATA_PEAK_MIN_NITS, METADATA_PEAK_MAX_NITS),
        source,
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
/// 0..1 RGB (saturated colours) survives the round trip instead of being clipped. The decode is
/// also the extended BT.2020 SDR decode (magnitude capped at 1.5).
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
/// Extended HLG inverse OETF: sign-preserving, magnitude capped at 1.
pub fn hlg_inverse_oetf_signed(signal: f32) -> f32 {
    signal.signum() * hlg_inverse_oetf(signal.abs())
}
/// BT.2100 HLG OOTF at the nominal peak: display nits = Lw * Ys^(gamma-1) * E per channel.
pub fn hlg_ootf(scene: [f32; 3]) -> [f32; 3] {
    let ys = dot(luma_weights(true), scene).max(0.0);
    let gain = HLG_PEAK_NITS * ys.powf(0.2);
    scene.map(|v| v * gain)
}
/// `extended`: sign-preserving transfer decode (the current math); false is the per-pixel clip at 0
/// of [`Method::Legacy`]. BT.709 SDR is signed either way.
pub fn source_decode(
    y: f32,
    uv: [f32; 2],
    bt2020: bool,
    full_range: bool,
    mode: u32,
    extended: bool,
) -> [f32; 3] {
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
    match (mode, extended) {
        (MODE_SDR, false) if bt2020 => [r, g, b].map(sdr_decode),
        (MODE_SDR, _) => [r, g, b].map(sdr_decode_signed),
        (MODE_HLG, true) => hlg_ootf([r, g, b].map(hlg_inverse_oetf_signed)),
        (MODE_HLG, false) => hlg_ootf([r, g, b].map(hlg_inverse_oetf)),
        (_, true) => [r, g, b].map(pq_eotf_signed),
        (_, false) => [r, g, b].map(pq_eotf),
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
/// The one late clip of the current math: applied to an averaged output value (an output pixel's
/// area, or a chroma footprint), never per source pixel. BT.709 SDR (mode SDR, BT.709 matrix) is
/// scale only and never clipped: the final quantization alone limits its range, as before.
pub fn finish_linear(rgb: [f32; 3], bt2020: bool, mode: u32) -> [f32; 3] {
    if mode == MODE_SDR && !bt2020 {
        rgb
    } else {
        gamut_clip(rgb)
    }
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
    // One pixel at 1:1 with the current math: extended decode, then the late clip on that pixel.
    encode_linear(finish_linear(
        tone_map_linear(
            y,
            uv,
            source_peak_nits,
            target_white_nits,
            desat,
            curve,
            bt2020,
            full_range,
            mode,
            true,
        ),
        bt2020,
        mode,
    ))
}
/// BT.709 display-linear RGB (0..1) to Y'CbCr through the inverse BT.1886 curve.
pub fn encode_linear(rgb: [f32; 3]) -> [f32; 3] {
    // Identical to sdr_encode inside 0..1 (every tone-mapped and gamut-converted value);
    // only the unclipped BT.709 scale-only pass reaches outside it.
    rgb709_to_ycbcr(rgb.map(sdr_encode_signed))
}
/// Tone-mapped display-linear BT.709 RGB: the light domain the area downscale averages in.
/// `extended` (current math): unclipped, may leave 0..1 (out-of-gamut colours, sub-black);
/// [`finish_linear`] clips the average once. `!extended` ([`Method::Legacy`]): clipped to 0..1
/// here, per source pixel. Negative and >1 channels are safe: the curves clamp their input at 0
/// and the peak, the scale divides by max(sig, 1e-6), desaturate guards its divisions, and no
/// pow/log ever sees a negative (the signed decodes take the magnitude).
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
    extended: bool,
) -> [f32; 3] {
    if mode == MODE_SDR {
        // BT.2020 SDR: no tone curve, only BT.2020 -> BT.709 in linear light, clip, re-encode.
        let rgb = source_decode(y, uv, bt2020, full_range, MODE_SDR, extended);
        // BT.709 SDR is scale only: no clip, so the final quantization alone limits the range.
        return match (bt2020, extended) {
            (false, _) => rgb,
            (true, true) => rgb2020_to_709(rgb),
            (true, false) => gamut_to_709(rgb, true),
        };
    }
    // PQ and HLG both arrive here as display light in nits.
    let mut rgb = source_decode(y, uv, bt2020, full_range, mode, extended);
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
    let rgb = rgb.map(|v| v * scale);
    let rgb = if bt2020 && curve == 2 {
        rgb2020_to_709(rgb)
    } else {
        rgb
    };
    if extended { rgb } else { gamut_clip(rgb) }
}
/// Which downscale math the stage runs. `Linear` is the current one; `Legacy` is the
/// pre-2026-11-22 math kept for console A/B (debug switch `gammachroma`): per-source-pixel clip,
/// input chroma always left-sited, output chroma filtered from encoded Cb/Cr.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Method {
    #[default]
    Linear,
    Legacy,
}
/// The threshold of NV12's 8-bit quantisation (`BALE_TONEMAP_OUTPUT_NV12_DITHERED`); P010 is always
/// plainly rounded. `Bayer` is the default; `R2` is rav1g's display-pass threshold (debug `r2dither`,
/// 2026-11-26), so that the stage writes the codes rav1g writes for the same value. The GPU builds the
/// plane shaders once per dither (`BALE_DITHER_R2`) and creates the stage's set.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Dither {
    /// Static 8x8 Bayer at the output pixel; Cr on the mirrored index ([`bayer8`]).
    #[default]
    Bayer,
    /// The R2 sequence at the output sample; Cr at the transposed sample ([`r2_threshold`]).
    R2,
}
impl Dither {
    /// The luma threshold `t` of `floor(v + t)` at output pixel (x, row).
    pub fn luma_threshold(self, x: u32, row: u32) -> f32 {
        match self {
            Self::Bayer => dither_threshold(bayer8(x, row)),
            Self::R2 => r2_threshold(x, row),
        }
    }
    /// The Cb and Cr thresholds at chroma sample (x, row) of the half-size plane: Bayer mirrors the
    /// index for Cr (63 - index), R2 transposes the position (rav1g's `threshold(uint2(p.y, p.x))`).
    pub fn chroma_thresholds(self, x: u32, row: u32) -> [f32; 2] {
        match self {
            Self::Bayer => {
                let index = bayer8(x, row);
                [dither_threshold(index), dither_threshold(63 - index)]
            }
            Self::R2 => [r2_threshold(x, row), r2_threshold(row, x)],
        }
    }
}
/// One stage's math choices: the defaults, or the debug A/B words of `tonemap-debug`. Each word is
/// independent; a [`Method`] alone converts to these options with the others at their defaults.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Options {
    /// [`Method::Legacy`] under `gammachroma`.
    pub method: Method,
    /// `fixedpeak`: the config's source peak even when the stream's metadata names one
    /// ([`source_peak`]).
    pub fixed_peak: bool,
    /// `bilinearchroma`: bilinear input chroma upsampling (before 2026-11-24) instead of Catmull-Rom.
    pub bilinear_chroma: bool,
    /// `r2dither`: [`Dither::R2`], rav1g's threshold, instead of Bayer for NV12 output.
    pub dither: Dither,
}
impl From<Method> for Options {
    fn from(method: Method) -> Self {
        Self {
            method,
            ..Self::default()
        }
    }
}
/// Input chroma sample (i, j) sits at luma position (2i, 2j) + this offset, from the stream's
/// AVChromaLocation: 1 LEFT (and 0 unspecified, and any unknown value): co-sited horizontally,
/// halfway between two rows; 2 CENTER: halfway both ways; 3 TOPLEFT: co-sited both ways; 4 TOP:
/// halfway horizontally, on the top row; 5 BOTTOMLEFT / 6 BOTTOM: on the bottom row of the pair.
/// Mirrors the shader's `chroma_offset`.
pub fn chroma_offset(location: u8) -> [f32; 2] {
    let x = if matches!(location, 2 | 4 | 6) {
        0.5
    } else {
        0.0
    };
    let y = match location {
        3 | 4 => 0.0,
        5 | 6 => 1.0,
        _ => 0.5,
    };
    [x, y]
}
/// The cbuffer's `output.w` for a frame: bits 0-7 the chroma location, bit 8 the legacy math, bit 9
/// bilinear input chroma (bits 10-11: [`cosited_bits`], GPU only).
pub fn method_word(frame: &BaleToneMapFrameInfo, options: impl Into<Options>) -> u32 {
    let options = options.into();
    u32::from(frame.chroma_location)
        | if options.method == Method::Legacy {
            0x100
        } else {
            0
        }
        | if options.bilinear_chroma { 0x200 } else { 0 }
}
/// cbuffer `output.w` bit 10 (11): the visible source is exactly 2 or 4 times the output wide
/// (high). In every step of the area loop all lanes of a shader wave then read the same chroma phase
/// on that axis, so the shader's cubic skips the zero-weight taps of a co-sited phase (fraction 0)
/// behind a uniform branch: same result, fewer loads. GPU only; the reference always reads all taps.
pub fn cosited_bits(config: &BaleToneMapConfig) -> u32 {
    let (source_width, source_height) = config.source_size();
    let even = |source: u32, output: u32| source == 2 * output || source == 4 * output;
    (u32::from(even(source_width, config.width)) << 10)
        | (u32::from(even(source_height, config.height)) << 11)
}
/// Catmull-Rom (Keys cubic, a = -0.5) weights of the taps at -1, 0, +1 and +2 for a sample the
/// fraction `t` (0 <= t < 1) past tap 0. Interpolating (t = 0 gives 0, 1, 0, 0), they sum to 1 and
/// reproduce linear ramps. At the quarter phases chroma upsampling uses (0, 1/4, 1/2, 3/4) each weight
/// is a multiple of 1/128, exact in f32. Mirrors the shader's `catmull_rom`.
pub fn catmull_rom(t: f32) -> [f32; 4] {
    let t2 = t * t;
    let t3 = t2 * t;
    [
        0.5 * (-t3 + 2.0 * t2 - t),
        0.5 * (3.0 * t3 - 5.0 * t2) + 1.0,
        0.5 * (-3.0 * t3 + 4.0 * t2 + t),
        0.5 * (t3 - t2),
    ]
}
/// Valid 10-bit chroma codes, the cubic's clamp: 64-960 in limited range (16-240 at 8 bits), 0-1023
/// in full range.
pub fn chroma_code_range(full_range: bool) -> (f32, f32) {
    if full_range {
        (0.0, 1023.0)
    } else {
        (64.0, 960.0)
    }
}
/// The visible chroma plane of a tight P010 picture, read as 10-bit codes.
struct ChromaPlane<'a> {
    input: &'a [u16],
    /// Words per row (the picture's width) and the plane's first word.
    stride: usize,
    start: usize,
    /// Visible chroma size: reads clamp to it and never touch decoder padding.
    width: i32,
    height: i32,
    full_range: bool,
}
impl ChromaPlane<'_> {
    /// Plane `p` (0 Cb, 1 Cr) at chroma sample (x, y), clamped to the visible plane.
    fn code(&self, x: i32, y: i32, p: usize) -> f32 {
        let x = x.clamp(0, self.width - 1) as usize;
        let y = y.clamp(0, self.height - 1) as usize;
        (self.input[self.start + y * self.stride + 2 * x + p] >> 6) as f32
    }
    /// Bilinear at chroma-sample position `c` (debug `bilinearchroma`), as the shader's
    /// `chroma_bilinear`.
    fn bilinear(&self, c: [f32; 2]) -> [f32; 2] {
        let (ix, iy) = (c[0].floor() as i32, c[1].floor() as i32);
        let (fx, fy) = (c[0] - ix as f32, c[1] - iy as f32);
        std::array::from_fn(|p| {
            let a = self.code(ix, iy, p) * (1.0 - fx) + self.code(ix + 1, iy, p) * fx;
            let b = self.code(ix, iy + 1, p) * (1.0 - fx) + self.code(ix + 1, iy + 1, p) * fx;
            a * (1.0 - fy) + b * fy
        })
    }
    /// Separable 4x4 Catmull-Rom at chroma-sample position `c`, rows first, clamped to the valid code
    /// range against the filter's overshoot at sharp edges; as the shader's `chroma_cubic`.
    fn cubic(&self, c: [f32; 2]) -> [f32; 2] {
        let (ix, iy) = (c[0].floor() as i32, c[1].floor() as i32);
        let (wx, wy) = (catmull_rom(c[0] - ix as f32), catmull_rom(c[1] - iy as f32));
        let (lo, hi) = chroma_code_range(self.full_range);
        std::array::from_fn(|p| {
            let row = |y: i32| {
                (0..4).fold(0.0f32, |sum, i| {
                    sum + wx[i] * self.code(ix - 1 + i as i32, y, p)
                })
            };
            (0..4)
                .fold(0.0f32, |sum, j| sum + wy[j] * row(iy - 1 + j as i32))
                .clamp(lo, hi)
        })
    }
}
pub fn pixel_linear(
    config: &BaleToneMapConfig,
    frame: &BaleToneMapFrameInfo,
    y: f32,
    uv: [f32; 2],
    method: Method,
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
        method == Method::Linear,
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
/// rav1g's R2 lattice in 32-bit fixed point (display_pass.hlsl `R2_X`, `R2_Y`): round(2^32 / g) and
/// round(2^32 / g^2) for the plastic number g = 1.3247..., the real root of g^3 = g + 1.
pub const R2_X: u32 = 0xc13f_a9a9;
pub const R2_Y: u32 = 0x91e1_0da6;
/// The R2 level (0..256) at sample (x, y): the top 8 bits of `x * R2_X + y * R2_Y` mod 2^32, i.e.
/// frac(x / g + y / g^2) in 8 bits (M. Roberts, "The unreasonable effectiveness of quasirandom
/// sequences", 2018).
pub fn r2_level(x: u32, y: u32) -> u32 {
    x.wrapping_mul(R2_X).wrapping_add(y.wrapping_mul(R2_Y)) >> 24
}
/// rav1g's display-pass threshold at sample (x, y): (level + 1/2) / 256, in (0, 1) with mean 1/2.
/// Exact in f32 (an odd multiple of 1/512), as the shader's `r2_threshold`. Luma takes it at its
/// pixel, Cb at its chroma sample, Cr at the transposed sample ([`Dither::chroma_thresholds`]).
pub fn r2_threshold(x: u32, y: u32) -> f32 {
    (r2_level(x, y) as f32 + 0.5) / 256.0
}
/// NV12 luma quantised with threshold `t`: `floor(16 + 219 y + t)`, clamped to 16..235. Rounding is
/// `t = 0.5`; a dither's `t` keeps every code within one of the exact value.
pub fn quantize_luma_with(y: f32, t: f32) -> f32 {
    (16.0 + 219.0 * y + t).floor().clamp(16.0, 235.0)
}
/// NV12 chroma quantised with the Cb and Cr thresholds `t`: `floor(128 + 224 c + t)`, 16..240.
pub fn quantize_chroma_with(uv: [f32; 2], t: [f32; 2]) -> [f32; 2] {
    std::array::from_fn(|c| (128.0 + 224.0 * uv[c] + t[c]).floor().clamp(16.0, 240.0))
}
/// NV12 luma with a static ordered dither (BALE_TONEMAP_OUTPUT_NV12_DITHERED); see the shader's
/// comment in tonemap_math.hlsli for why a constant Bayer pattern. Never more than one code from
/// the exact value (plain rounding: half a code).
pub fn quantize_luma_dithered(y: f32, x: u32, row: u32) -> f32 {
    quantize_luma_with(y, Dither::Bayer.luma_threshold(x, row))
}
/// NV12 chroma at chroma-plane position (x, row); Cr uses the mirrored threshold.
pub fn quantize_chroma_dithered(uv: [f32; 2], x: u32, row: u32) -> [f32; 2] {
    quantize_chroma_with(uv, Dither::Bayer.chroma_thresholds(x, row))
}

/// Tight P010 words (high 10 bits), input may include decoder alignment beyond visible size.
/// Chroma upsampling: separable 4x4 Catmull-Rom clamped to the valid code range (bilinear under
/// `bilinear_chroma`) at the stream's chroma location ([`chroma_offset`]; left when unspecified).
/// Area downscale in display-linear light, then output chroma (left-sited): a [1 2 1]/4 x [1 1]/2
/// footprint centred at (2x, 2y + 0.5) that mixes the output pixels' linear RGB, then clips and
/// encodes once. Clamp visible edges, never sample alignment padding.
/// NV12 8-bit codes, the ABI 1 output, ordered-dithered like the shader. Current math
/// ([`Options::default`]: the stream's metadata peak, Bayer); see [`reference_with`] for the debug
/// options (`options.dither` picks the NV12 threshold).
pub fn reference(
    config: &BaleToneMapConfig,
    frame: &BaleToneMapFrameInfo,
    input: &[u16],
    width: u32,
    height: u32,
) -> Result<Vec<u8>, i32> {
    reference_with(config, frame, input, width, height, Options::default())
}

/// [`reference`] with explicit [`Options`] (or just a [`Method`]).
pub fn reference_with(
    config: &BaleToneMapConfig,
    frame: &BaleToneMapFrameInfo,
    input: &[u16],
    width: u32,
    height: u32,
    options: impl Into<Options>,
) -> Result<Vec<u8>, i32> {
    Ok(
        reference_codes(config, frame, input, width, height, false, options.into())?
            .into_iter()
            .map(|code| code as u8)
            .collect(),
    )
}

/// P010 10-bit codes (unshifted, 64-940/960), the ABI 2 output.
pub fn reference_p010(
    config: &BaleToneMapConfig,
    frame: &BaleToneMapFrameInfo,
    input: &[u16],
    width: u32,
    height: u32,
) -> Result<Vec<u16>, i32> {
    reference_p010_with(config, frame, input, width, height, Options::default())
}

/// [`reference_p010`] with explicit [`Options`] (or just a [`Method`]).
pub fn reference_p010_with(
    config: &BaleToneMapConfig,
    frame: &BaleToneMapFrameInfo,
    input: &[u16],
    width: u32,
    height: u32,
    options: impl Into<Options>,
) -> Result<Vec<u16>, i32> {
    reference_codes(config, frame, input, width, height, true, options.into())
}

fn reference_codes(
    config: &BaleToneMapConfig,
    frame: &BaleToneMapFrameInfo,
    input: &[u16],
    width: u32,
    height: u32,
    ten_bit: bool,
    options: Options,
) -> Result<Vec<u16>, i32> {
    let encoded = reference_encoded(config, frame, input, width, height, options)?;
    let (w, h) = (encoded.width, encoded.height);
    let mut out = vec![0; w * h * 3 / 2];
    for y in 0..h {
        for x in 0..w {
            let luma = encoded.luma[y * w + x];
            out[y * w + x] = if ten_bit {
                quantize_luma10(luma)
            } else {
                quantize_luma_with(luma, options.dither.luma_threshold(x as u32, y as u32))
            } as u16;
        }
    }
    for y in 0..h / 2 {
        for x in 0..w / 2 {
            let uv = encoded.chroma[y * (w / 2) + x];
            let codes = if ten_bit {
                quantize_chroma10(uv)
            } else {
                quantize_chroma_with(uv, options.dither.chroma_thresholds(x as u32, y as u32))
            };
            for c in 0..2 {
                out[w * h + y * w + 2 * x + c] = codes[c] as u16;
            }
        }
    }
    Ok(out)
}

/// The stage's result before its last quantisation, as the luma and chroma passes compute it: the
/// encoded Y' of each output pixel (row-major, `width` x `height`) and the encoded [Cb, Cr] of each
/// chroma sample (row-major, `width / 2` x `height / 2`). [`reference_with`] and
/// [`reference_p010_with`] quantise exactly these values; a test can hold another rounding model to them.
#[derive(Clone, Debug)]
pub struct Encoded {
    pub width: usize,
    pub height: usize,
    pub luma: Vec<f32>,
    pub chroma: Vec<[f32; 2]>,
}

/// [`Encoded`] values of the reference for a P010 input (`options.dither` does not change them).
pub fn reference_encoded(
    config: &BaleToneMapConfig,
    frame: &BaleToneMapFrameInfo,
    input: &[u16],
    width: u32,
    height: u32,
    options: impl Into<Options>,
) -> Result<Encoded, i32> {
    let options = options.into();
    config.validate()?;
    frame.validate()?;
    // The peak the stage puts in its cbuffer for this frame: the stream's metadata (PQ), else the
    // config's.
    let config = &BaleToneMapConfig {
        source_peak_nits: source_peak(config, frame, options.fixed_peak).0,
        ..*config
    };
    let method = options.method;
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
    let legacy = method == Method::Legacy;
    let (bt2020, mode) = (frame.matrix == 9, mode(frame.transfer));
    let offset = chroma_offset(if legacy { 1 } else { frame.chroma_location });
    let chroma = ChromaPlane {
        input,
        stride: iw,
        start: iw * ih,
        width: sw as i32 / 2,
        height: sh as i32 / 2,
        full_range: frame.full_range == 1,
    };
    let sample = |x: i32, y: i32| {
        let x = x.clamp(0, sw as i32 - 1);
        let y = y.clamp(0, sh as i32 - 1);
        // This luma pixel's position in chroma-sample coordinates.
        let c = [(x as f32 - offset[0]) * 0.5, (y as f32 - offset[1]) * 0.5];
        let uv = if options.bilinear_chroma {
            chroma.bilinear(c)
        } else {
            chroma.cubic(c)
        };
        pixel_linear(
            config,
            frame,
            (input[y as usize * iw + x as usize] >> 6) as f32,
            uv,
            method,
        )
    };
    // Area downscale in display-linear light, mirroring the shader's area_linear.
    let (scale_x, scale_y) = (sw as f32 / w as f32, sh as f32 / h as f32);
    let area_linear = |ox: i32, oy: i32| {
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
        sum.map(|v| v / total.max(1e-6))
    };
    // The shader's area_value: linear (current) or encoded (legacy) per output pixel, computed once
    // per pixel like the staged intermediate.
    let encode_output = |rgb: [f32; 3]| encode_linear(finish_linear(rgb, bt2020, mode));
    let mut values = Vec::with_capacity(w * h);
    for y in 0..h {
        for x in 0..w {
            let rgb = area_linear(x as i32, y as i32);
            values.push(if legacy { encode_linear(rgb) } else { rgb });
        }
    }
    let value = |x: i32, y: i32| {
        values[y.clamp(0, h as i32 - 1) as usize * w + x.clamp(0, w as i32 - 1) as usize]
    };
    let mut luma = Vec::with_capacity(w * h);
    for y in 0..h {
        for x in 0..w {
            let v = value(x as i32, y as i32);
            luma.push(if legacy { v[0] } else { encode_output(v)[0] });
        }
    }
    let mut chroma = Vec::with_capacity(w / 2 * (h / 2));
    for y in 0..h / 2 {
        for x in 0..w / 2 {
            let mut acc = [0.0f32; 3];
            for dy in 0..2 {
                for (dx, weight) in [(-1, 0.125), (0, 0.25), (1, 0.125)] {
                    let p = value((2 * x) as i32 + dx, (2 * y + dy) as i32);
                    for c in 0..3 {
                        acc[c] += weight * p[c];
                    }
                }
            }
            let encoded = if legacy { acc } else { encode_output(acc) };
            chroma.push([encoded[1], encoded[2]]);
        }
    }
    Ok(Encoded {
        width: w,
        height: h,
        luma,
        chroma,
    })
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
    /// rav1g's display-pass threshold, transcribed on its own from rav1g's float32 model
    /// (testing/display_pass/model.py `threshold`: wide products masked to 32 bits) and shader
    /// (libav1/dx/shaders/display_pass.hlsl `threshold`: `mad(t8, 1/256, 0.5/256)`), not from
    /// [`r2_level`].
    fn rav1g_threshold(x: u32, y: u32) -> f32 {
        let sum = u128::from(x) * 0xc13f_a9a9 + u128::from(y) * 0x91e1_0da6;
        let t8 = ((sum & 0xffff_ffff) >> 24) as f32;
        t8.mul_add(1.0 / 256.0, 0.5 / 256.0)
    }
    /// rav1g's planes: luma at the output pixel, Cb at the chroma sample, Cr at the transposed
    /// sample (display_pass.hlsl `chroma_code`: `threshold(p)`, `threshold(uint2(p.y, p.x))`).
    #[test]
    fn r2_threshold_is_rav1g_s_at_every_position_and_plane() {
        // The constants are the plastic number's lattice: round(2^32 / g), round(2^32 / g^2).
        let g = (0..64).fold(1.3f64, |g, _| {
            g - (g * g * g - g - 1.0) / (3.0 * g * g - 1.0)
        });
        assert!((g * g * g - g - 1.0).abs() < 1e-15);
        assert_eq!(((1u64 << 32) as f64 / g).round() as u32, R2_X);
        assert_eq!(((1u64 << 32) as f64 / (g * g)).round() as u32, R2_Y);
        // Levels worked out by hand from rav1g's formula (Python big integers).
        for ((x, y), level) in [
            ((0, 0), 0),
            ((1, 0), 193),
            ((0, 1), 145),
            ((1, 1), 83),
            ((2, 3), 56),
            ((63, 63), 117),
            ((1919, 1079), 119),
            ((3839, 2159), 66),
            ((u32::MAX, u32::MAX), 172),
            ((65536, 1), 59),
        ] {
            assert_eq!(r2_level(x, y), level, "({x}, {y})");
            assert_eq!(r2_threshold(x, y), (level as f32 + 0.5) / 256.0);
        }
        // Every position of a 64x64 tile at the origin and at odd offsets: 1080p and 4K edges, past
        // 16 bits, and the wrap at 2^32 (rav1g's uint arithmetic). Bit-equal, every plane.
        let origins = [
            (0, 0),
            (1, 1),
            (63, 17),
            (1857, 1017),
            (3777, 2097),
            (65535, 3),
            (1 << 31, 12345),
            (u32::MAX - 63, u32::MAX - 63),
            (u32::MAX - 31, 5),
        ];
        let mut levels = [0u32; 256];
        for (ox, oy) in origins {
            for dy in 0..64 {
                for dx in 0..64 {
                    let (x, y) = (ox.wrapping_add(dx), oy.wrapping_add(dy));
                    let luma = Dither::R2.luma_threshold(x, y);
                    assert_eq!(
                        luma.to_bits(),
                        rav1g_threshold(x, y).to_bits(),
                        "({x}, {y})"
                    );
                    let [cb, cr] = Dither::R2.chroma_thresholds(x, y);
                    assert_eq!(
                        cb.to_bits(),
                        rav1g_threshold(x, y).to_bits(),
                        "Cb ({x}, {y})"
                    );
                    assert_eq!(
                        cr.to_bits(),
                        rav1g_threshold(y, x).to_bits(),
                        "Cr ({x}, {y})"
                    );
                    // An odd multiple of 1/512 in (0, 1): never exactly a rounding tie.
                    let scaled = luma * 512.0;
                    assert!(scaled.fract() == 0.0 && scaled as u32 % 2 == 1 && luma < 1.0);
                    if (ox, oy) == (0, 0) {
                        levels[r2_level(x, y) as usize] += 1;
                    }
                }
            }
        }
        // The 64x64 tile at the origin reaches every one of the 256 levels.
        assert!(levels.iter().all(|&n| n > 0), "{levels:?}");
        // Bayer is unchanged: the same thresholds as before the Dither type.
        for (x, y) in [(0, 0), (1, 1), (5, 2), (7, 7), (8, 3)] {
            let index = bayer8(x, y);
            assert_eq!(Dither::Bayer.luma_threshold(x, y), dither_threshold(index));
            assert_eq!(
                Dither::Bayer.chroma_thresholds(x, y),
                [dither_threshold(index), dither_threshold(63 - index)]
            );
        }
    }
    /// What rav1g's documentation claims for its threshold, at a 64x64 tile: a flat area keeps its
    /// level (bias under 0.003 of a code at every fraction; measured 0.0007 at the k/256 ones), every
    /// code is within one of the exact value, both chroma planes are unbiased, and the column means of
    /// a slow ramp stay close to the exact value where Bayer's period-8 columns are a third of a code
    /// off (64 rows: R2 0.05, Bayer 0.33).
    #[test]
    fn r2_dither_is_unbiased_on_flat_areas_and_ramp_columns() {
        let (w, h) = (64u32, 64u32);
        let tile = |f: &dyn Fn(u32, u32) -> f32| -> f32 {
            (0..h)
                .flat_map(|y| (0..w).map(move |x| (x, y)))
                .map(|(x, y)| f(x, y))
                .sum::<f32>()
                / (w * h) as f32
        };
        for k in 1..512 {
            for base in [16.5f32, 100.0, 233.0] {
                let v = base + k as f32 / 512.0 + 0.0003;
                let y = (v - 16.0) / 219.0;
                // The exact value as the quantiser sees it (16 + 219 y rounds in f32 too).
                let exact = 16.0 + 219.0 * y;
                let mean = tile(&|x, row| {
                    let code = quantize_luma_with(y, Dither::R2.luma_threshold(x, row));
                    assert!((code - exact).abs() < 1.0, "{code} vs {exact}");
                    code
                });
                assert!((mean - exact).abs() < 0.003, "v={v}: mean {mean}");
            }
        }
        for uv in [[0.013f32, -0.021], [0.1, 0.2], [-0.3, 0.004], [0.45, -0.45]] {
            for c in 0..2 {
                let exact = 128.0 + 224.0 * uv[c];
                let mean = tile(&|x, row| {
                    quantize_chroma_with(uv, Dither::R2.chroma_thresholds(x, row))[c]
                });
                assert!(
                    (mean - exact).abs() < 0.003,
                    "{uv:?} plane {c}: {mean} vs {exact}"
                );
            }
        }
        // A slow ramp, 0.40..0.44 of the luma range across 512 columns, 64 rows.
        let worst_column = |dither: Dither| {
            (0..512u32)
                .map(|x| {
                    let y = 0.40 + 0.04 * x as f32 / 511.0;
                    let exact = 16.0 + 219.0 * y;
                    let sum: f32 = (0..64)
                        .map(|row| quantize_luma_with(y, dither.luma_threshold(x, row)))
                        .sum();
                    (sum / 64.0 - exact).abs()
                })
                .fold(0.0f32, f32::max)
        };
        let (r2, bayer) = (worst_column(Dither::R2), worst_column(Dither::Bayer));
        assert!(
            r2 < 0.06 && bayer > 0.3,
            "column bias R2 {r2}, Bayer {bayer}"
        );
    }
    /// The reference quantises its encoded values with rav1g's rounding model when `dither` is R2,
    /// code for code: luma `floor(16 + 219 Y' + t(x, y))`, Cb `floor(128 + 224 Cb + t(i, j))`, Cr with
    /// `t(j, i)`, clamped. Bayer keeps its codes, and the two dithers are never more than a code apart.
    #[test]
    fn r2_reference_rounds_the_encoded_values_like_rav1g() {
        let r2 = Options {
            dither: Dither::R2,
            ..Options::default()
        };
        let sdr = BaleToneMapFrameInfo {
            transfer: 1,
            primaries: 1,
            matrix: 1,
            ..frame()
        };
        for (curve, f, reserved, input, (iw, ih)) in [
            (
                1,
                frame(),
                [0; 4],
                test_pattern(16, 8, true, false),
                (16, 8),
            ),
            (
                2,
                frame(),
                [32, 16, 0, 0],
                test_pattern(32, 16, true, false),
                (32, 16),
            ),
            (1, sdr, [0; 4], sdr_ramp(16, 8, 60.0, 980.0), (16, 8)),
            (
                1,
                sdr,
                [40, 20, 0, 0],
                sdr_ramp(40, 20, 300.0, 700.0),
                (40, 20),
            ),
        ] {
            let c = BaleToneMapConfig {
                reserved,
                ..config(curve)
            };
            let encoded = reference_encoded(&c, &f, &input, iw, ih, r2).unwrap();
            let (w, h) = (encoded.width, encoded.height);
            let codes = reference_with(&c, &f, &input, iw, ih, r2).unwrap();
            for y in 0..h {
                for x in 0..w {
                    let t = rav1g_threshold(x as u32, y as u32);
                    let model = (16.0 + 219.0 * encoded.luma[y * w + x] + t)
                        .floor()
                        .clamp(16.0, 235.0);
                    assert_eq!(f32::from(codes[y * w + x]), model, "luma ({x}, {y})");
                }
            }
            for y in 0..h / 2 {
                for x in 0..w / 2 {
                    let [cb, cr] = encoded.chroma[y * (w / 2) + x];
                    let (tb, tr) = (
                        rav1g_threshold(x as u32, y as u32),
                        rav1g_threshold(y as u32, x as u32),
                    );
                    let model = |v: f32, t: f32| (128.0 + 224.0 * v + t).floor().clamp(16.0, 240.0);
                    let at = w * h + y * w + 2 * x;
                    assert_eq!(f32::from(codes[at]), model(cb, tb), "Cb ({x}, {y})");
                    assert_eq!(f32::from(codes[at + 1]), model(cr, tr), "Cr ({x}, {y})");
                }
            }
            let bayer = reference(&c, &f, &input, iw, ih).unwrap();
            assert_eq!(
                reference_with(&c, &f, &input, iw, ih, Options::default()).unwrap(),
                bayer
            );
            assert!(codes.iter().zip(&bayer).all(|(a, b)| a.abs_diff(*b) <= 1));
            assert!(codes != bayer, "R2 and Bayer must differ somewhere");
            // P010 is rounded, whichever dither the stage has.
            assert_eq!(
                reference_p010_with(&c, &f, &input, iw, ih, r2).unwrap(),
                reference_p010(&c, &f, &input, iw, ih).unwrap()
            );
        }
    }
    #[test]
    fn visible_edges_ignore_decoder_padding_and_unknown_chroma_locations_read_as_left() {
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
        // Unspecified, left and unknown locations all read as left; the others move the chroma.
        for loc in [0, 1, 7, 255] {
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
                expected,
                "location {loc}"
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
                source_peak_nits: -1.0,
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
        // Peak 0: none configured (the stream's metadata, else 1000 nits).
        assert!(
            BaleToneMapConfig {
                source_peak_nits: 0.0,
                ..c
            }
            .validate()
            .is_ok()
        );
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

    fn with_metadata(
        f: BaleToneMapFrameInfo,
        cll: Option<u32>,
        mastering: Option<f32>,
    ) -> BaleToneMapFrameInfo {
        BaleToneMapFrameInfo {
            has_content_light: u8::from(cll.is_some()),
            max_cll: cll.unwrap_or(0),
            has_mastering: u8::from(mastering.is_some()),
            mastering_max_nits: mastering.unwrap_or(0.0),
            ..f
        }
    }
    /// PQ: MaxCLL, else the mastering maximum, else the config peak; MaxCLL above the mastering
    /// maximum gives the mastering maximum; metadata peaks clamp to 400..10000 nits (compared first,
    /// clamped after); the config peak is used as configured, 0 meaning 1000 nits.
    #[test]
    fn source_peak_takes_pq_metadata_with_clamps() {
        use PeakSource::*;
        let c = config(1);
        for (cll, mastering, expected) in [
            (Some(1500), Some(4000.0), (1500.0, MaxCll)),
            (Some(1500), None, (1500.0, MaxCll)),
            (None, Some(4000.0), (4000.0, Mastering)),
            (None, None, (1000.0, Config)),
            (Some(4000), Some(1000.0), (1000.0, Mastering)),
            (Some(1000), Some(1000.0), (1000.0, MaxCll)),
            (Some(120), None, (400.0, MaxCll)),
            (Some(20000), None, (10000.0, MaxCll)),
            (None, Some(100.0), (400.0, Mastering)),
            (None, Some(12000.0), (10000.0, Mastering)),
            (Some(300), Some(350.0), (400.0, MaxCll)),
            (Some(500), Some(300.0), (400.0, Mastering)),
            (Some(u32::MAX), None, (10000.0, MaxCll)),
        ] {
            let f = with_metadata(frame(), cll, mastering);
            assert_eq!(
                source_peak(&c, &f, false),
                expected,
                "{cll:?} {mastering:?}"
            );
            assert_eq!(
                expected.1.name(),
                ["config", "maxcll", "mastering"][expected.1 as usize - 1]
            );
        }
        // The config peak is never clamped (a 100-nit config stays 100, as before); 0 is the default.
        for (configured, expected) in [(4000.0, 4000.0), (100.0, 100.0), (0.0, DEFAULT_PEAK_NITS)] {
            let c = BaleToneMapConfig {
                source_peak_nits: configured,
                ..c
            };
            assert_eq!(config_peak(&c), expected);
            assert_eq!(source_peak(&c, &frame(), false), (expected, Config));
        }
    }
    /// Unusable metadata counts as absent: a flag without a value, a value without its flag, a
    /// mastering maximum that is zero, negative or not finite. HLG and SDR keep the config peak;
    /// `fixed` (debug `fixedpeak`) always does.
    #[test]
    fn source_peak_ignores_unusable_metadata_and_non_pq_frames() {
        use PeakSource::*;
        let c = config(1);
        let unflagged = BaleToneMapFrameInfo {
            max_cll: 1500,
            mastering_max_nits: 4000.0,
            ..frame()
        };
        assert_eq!(metadata_peaks(&unflagged), (None, None));
        assert_eq!(source_peak(&c, &unflagged, false), (1000.0, Config));
        for mastering in [0.0, -5.0, f32::NAN, f32::INFINITY, f32::NEG_INFINITY] {
            let f = with_metadata(frame(), Some(0), Some(mastering));
            assert_eq!(metadata_peaks(&f), (None, None), "{mastering}");
            assert_eq!(source_peak(&c, &f, false), (1000.0, Config));
            // A usable MaxCLL is used alone; nothing to compare it with.
            let f = with_metadata(frame(), Some(1500), Some(mastering));
            assert_eq!(source_peak(&c, &f, false), (1500.0, MaxCll), "{mastering}");
        }
        let rich = with_metadata(frame(), Some(1500), Some(4000.0));
        assert_eq!(source_peak(&c, &rich, true), (1000.0, Config));
        for transfer in [18, 1, 14] {
            let f = BaleToneMapFrameInfo { transfer, ..rich };
            assert_eq!(
                source_peak(&c, &f, false),
                (1000.0, Config),
                "transfer {transfer}"
            );
        }
    }
    /// The reference (and so the self-test and WARP comparisons) tone-maps a PQ frame from its
    /// metadata peak exactly like a config with that peak; `fixed_peak` and HLG ignore it.
    #[test]
    fn reference_tone_maps_pq_from_the_metadata_peak() {
        let input = test_pattern(16, 8, true, false);
        let c = config(1);
        let codes = |c: &BaleToneMapConfig, f: &BaleToneMapFrameInfo, options: Options| {
            reference_p010_with(c, f, &input, 16, 8, options).unwrap()
        };
        let bright = with_metadata(frame(), Some(4000), Some(4000.0));
        let at_4000 = codes(
            &BaleToneMapConfig {
                source_peak_nits: 4000.0,
                ..c
            },
            &frame(),
            Options::default(),
        );
        let at_1000 = codes(&c, &frame(), Options::default());
        assert_ne!(
            at_4000, at_1000,
            "the pattern's 1000- and 4000-nit patches move"
        );
        assert_eq!(codes(&c, &bright, Options::default()), at_4000);
        let fixed = Options {
            fixed_peak: true,
            ..Options::default()
        };
        assert_eq!(codes(&c, &bright, fixed), at_1000);
        let unset = BaleToneMapConfig {
            source_peak_nits: 0.0,
            ..c
        };
        assert_eq!(codes(&unset, &frame(), Options::default()), at_1000);
        assert_eq!(codes(&unset, &bright, Options::default()), at_4000);
        let hlg = BaleToneMapFrameInfo {
            transfer: 18,
            ..frame()
        };
        let hlg_bright = BaleToneMapFrameInfo {
            transfer: 18,
            ..bright
        };
        assert_eq!(
            codes(&c, &hlg_bright, Options::default()),
            codes(&c, &hlg, Options::default())
        );
    }

    /// BT.709 SDR picture (P010 words): flat luma 500, neutral chroma except the chroma samples
    /// `hot` marks, which get a strong Cr (red).
    fn chroma_probe(w: usize, h: usize, hot: impl Fn(usize, usize) -> bool) -> Vec<u16> {
        let mut data = vec![500u16 << 6; w * h * 3 / 2];
        for y in 0..h / 2 {
            for x in 0..w / 2 {
                let (u, v) = if hot(x, y) { (440, 760) } else { (512, 512) };
                data[w * h + y * w + 2 * x] = u << 6;
                data[w * h + y * w + 2 * x + 1] = v << 6;
            }
        }
        data
    }
    fn sdr709() -> BaleToneMapFrameInfo {
        BaleToneMapFrameInfo {
            transfer: 1,
            primaries: 1,
            matrix: 1,
            ..frame()
        }
    }
    #[test]
    fn chroma_offsets_follow_av_chroma_location() {
        assert_eq!(chroma_offset(0), [0.0, 0.5]);
        assert_eq!(chroma_offset(1), [0.0, 0.5]);
        assert_eq!(chroma_offset(2), [0.5, 0.5]);
        assert_eq!(chroma_offset(3), [0.0, 0.0]);
        assert_eq!(chroma_offset(4), [0.5, 0.0]);
        assert_eq!(chroma_offset(5), [0.0, 1.0]);
        assert_eq!(chroma_offset(6), [0.5, 1.0]);
        assert_eq!(chroma_offset(7), [0.0, 0.5]);
        assert_eq!(chroma_offset(255), [0.0, 0.5]);
        assert_eq!(
            method_word(
                &BaleToneMapFrameInfo {
                    chroma_location: 3,
                    ..frame()
                },
                Method::Legacy
            ),
            0x103
        );
        assert_eq!(method_word(&frame(), Method::Linear), 0);
        let bilinear = Options {
            bilinear_chroma: true,
            ..Options::default()
        };
        assert_eq!(method_word(&frame(), bilinear), 0x200);
        // Fast-path bits: exactly 2:1 or 4:1 per axis, never 1:1, 3:1 or fractional ratios.
        for (source, output, bits) in [
            ([64, 32], [32, 16], 0xc00),
            ([128, 64], [32, 16], 0xc00),
            ([64, 24], [32, 16], 0x400),
            ([48, 32], [32, 16], 0x800),
            ([96, 48], [32, 16], 0),
            ([0, 0], [32, 16], 0),
        ] {
            let c = BaleToneMapConfig {
                width: output[0],
                height: output[1],
                reserved: [source[0], source[1], 0, 0],
                ..config(1)
            };
            assert_eq!(cosited_bits(&c), bits, "{source:?} -> {output:?}");
        }
    }
    /// Catmull-Rom at the four chroma phases: interpolating, unit sum, linear ramps reproduced,
    /// mirror-symmetric, and exact multiples of 1/128 (so the shader computes the same weights).
    #[test]
    fn catmull_rom_weights_are_exact_at_the_quarter_phases() {
        assert_eq!(catmull_rom(0.0), [0.0, 1.0, 0.0, 0.0]);
        assert_eq!(
            catmull_rom(0.25),
            [-9.0, 111.0, 29.0, -3.0].map(|k| k / 128.0)
        );
        assert_eq!(
            catmull_rom(0.5),
            [-8.0, 72.0, 72.0, -8.0].map(|k| k / 128.0)
        );
        assert_eq!(
            catmull_rom(0.75),
            [-3.0, 29.0, 111.0, -9.0].map(|k| k / 128.0)
        );
        for t in [0.0f32, 0.25, 0.5, 0.75] {
            let w = catmull_rom(t);
            assert_eq!(w.iter().sum::<f32>(), 1.0, "t={t}");
            assert_eq!(-w[0] + w[2] + 2.0 * w[3], t, "t={t}: linear ramp");
            let mut mirrored = catmull_rom(1.0 - t);
            mirrored.reverse();
            assert_eq!(mirrored, w, "t={t}");
        }
    }
    /// A chroma plane (P010 words) whose Cb and Cr both follow `f(x)` across, constant down.
    fn chroma_columns(f: impl Fn(usize) -> f32) -> Vec<u16> {
        let (w, h) = (32usize, 8usize);
        let mut data = vec![512u16 << 6; w * h * 3 / 2];
        for y in 0..h / 2 {
            for x in 0..w / 2 {
                let code = (f(x).round() as u16) << 6;
                data[w * h + y * w + 2 * x] = code;
                data[w * h + y * w + 2 * x + 1] = code;
            }
        }
        data
    }
    fn plane(data: &[u16], full_range: bool) -> ChromaPlane<'_> {
        ChromaPlane {
            input: data,
            stride: 32,
            start: 32 * 8,
            width: 16,
            height: 4,
            full_range,
        }
    }
    /// Away from the picture edges the cubic reproduces linear and quadratic chroma exactly at every
    /// phase (bilinear misses a curve's midpoints by a quarter code here), and at a sharp edge its
    /// overshoot is clamped to the valid code range on both sides.
    #[test]
    fn cubic_chroma_is_exact_on_curves_and_clamped_at_sharp_edges() {
        let ramp = chroma_columns(|x| 300.0 + 10.0 * x as f32);
        let curve = chroma_columns(|x| 200.0 + (x * x) as f32);
        for i in 2..12 {
            for t in [0.0f32, 0.25, 0.5, 0.75] {
                let x = i as f32 + t;
                for y in [0.0, 0.25, 1.75] {
                    let c = [x, y];
                    assert_eq!(plane(&ramp, false).cubic(c), [300.0 + 10.0 * x; 2]);
                    assert_eq!(plane(&ramp, false).bilinear(c), [300.0 + 10.0 * x; 2]);
                    assert_eq!(plane(&curve, false).cubic(c), [200.0 + x * x; 2], "x={x}");
                }
            }
            let mid = [i as f32 + 0.5, 1.0];
            let exact = 200.0 + mid[0] * mid[0];
            assert_eq!(plane(&curve, false).bilinear(mid), [exact + 0.25; 2]);
        }
        for (full_range, low, high) in [(false, 64.0, 960.0), (true, 0.0, 1023.0)] {
            let step = chroma_columns(|x| if x < 8 { low } else { high });
            let p = plane(&step, full_range);
            let (mut clamped_low, mut clamped_high) = (false, false);
            for i in 0..16 {
                for t in [0.0f32, 0.25, 0.5, 0.75] {
                    let [cb, cr] = p.cubic([i as f32 + t, 1.0]);
                    assert_eq!(cb, cr);
                    assert!((low..=high).contains(&cb), "{i}+{t}: {cb}");
                    clamped_low |= cb == low && i == 6;
                    clamped_high |= cb == high && i == 8;
                }
            }
            // Unclamped, 6.25 would read 43 (or -24) and 8.75 981 (or 1047).
            assert!(clamped_low && clamped_high, "full range {full_range}");
            assert_eq!(chroma_code_range(full_range), (low, high));
        }
    }
    /// The full reference: the cubic changes sharp chroma edges (the test pattern's colour blocks),
    /// not flat areas, and `bilinear_chroma` restores the bilinear read.
    #[test]
    fn reference_upsamples_chroma_with_catmull_rom_unless_bilinear_chroma() {
        let c = config(1);
        let f = frame();
        let input = test_pattern(16, 8, true, false);
        let bilinear = Options {
            bilinear_chroma: true,
            ..Options::default()
        };
        let cubic = reference_p010(&c, &f, &input, 16, 8).unwrap();
        let old = reference_p010_with(&c, &f, &input, 16, 8, bilinear).unwrap();
        assert_ne!(cubic, old);
        let mut flat = vec![0u16; 16 * 8 * 3 / 2];
        flat[..128].fill(600 << 6);
        for (i, v) in flat[128..].iter_mut().enumerate() {
            *v = if i % 2 == 0 { 540 << 6 } else { 470 << 6 };
        }
        assert_eq!(
            reference_p010(&c, &f, &flat, 16, 8).unwrap(),
            reference_p010_with(&c, &f, &flat, 16, 8, bilinear).unwrap()
        );
    }
    /// One red chroma row (or column) at 1:1: with the input sited like the output (left), the
    /// output chroma rows (columns) on both sides get the same amount of red. A location that
    /// sits the input elsewhere shifts it to that side; the legacy math ignores the location.
    #[test]
    fn input_chroma_siting_moves_the_chroma_where_the_stream_says() {
        let (w, h) = (16usize, 8usize);
        let c = config(1);
        let red = |out: &[u16], x: usize, y: usize| i32::from(out[w * h + y * w + 2 * x + 1]) - 512;
        let at = |input: &[u16], loc: u8, method: Method| {
            let f = BaleToneMapFrameInfo {
                chroma_location: loc,
                ..sdr709()
            };
            reference_p010_with(&c, &f, input, 16, 8, method).unwrap()
        };
        // Vertical: chroma row 1 red; output chroma rows 0 and 2 are its neighbours.
        let row = chroma_probe(w, h, |_, y| y == 1);
        let left = at(&row, 1, Method::Linear);
        assert!(red(&left, 3, 1) > 100, "{}", red(&left, 3, 1));
        assert!(red(&left, 3, 0).abs_diff(red(&left, 3, 2)) <= 1);
        for (loc, up) in [(3, true), (4, true), (5, false), (6, false)] {
            let out = at(&row, loc, Method::Linear);
            let (above, below) = (red(&out, 3, 0), red(&out, 3, 2));
            assert!(
                if up {
                    above > below + 20
                } else {
                    below > above + 20
                },
                "location {loc}: above {above}, below {below}"
            );
            assert_eq!(at(&row, loc, Method::Legacy), at(&row, 1, Method::Legacy));
        }
        // CENTER keeps the vertical siting of LEFT.
        let center = at(&row, 2, Method::Linear);
        assert!(red(&center, 3, 0).abs_diff(red(&center, 3, 2)) <= 1);
        // Horizontal: chroma column 3 red; output chroma columns 2 and 4 are its neighbours.
        let column = chroma_probe(w, h, |x, _| x == 3);
        let left = at(&column, 1, Method::Linear);
        assert!(red(&left, 2, 1).abs_diff(red(&left, 4, 1)) <= 1);
        for loc in [2, 4, 6] {
            let out = at(&column, loc, Method::Linear);
            let (l, r) = (red(&out, 2, 1), red(&out, 4, 1));
            assert!(r > l + 20, "location {loc}: left {l}, right {r}");
        }
        for loc in [3, 5] {
            let out = at(&column, loc, Method::Linear);
            assert!(
                red(&out, 2, 1).abs_diff(red(&out, 4, 1)) <= 1,
                "location {loc}"
            );
        }
    }
    /// A pixel checkerboard of saturated red and green at 1:1: luma stays per pixel, and each
    /// chroma sample mixes red and green. Mixing their linear light gives the colour the eye
    /// sees (yellow, encoded once); mixing encoded Cb/Cr gives a much weaker, greyer yellow.
    #[test]
    fn linear_chroma_keeps_the_saturation_of_fine_red_green_detail() {
        let (w, h) = (16usize, 8usize);
        let c = config(1);
        let f = sdr709();
        let mut input = vec![0u16; w * h * 3 / 2];
        // Per-pixel luma of encoded BT.709 red / green; the 4:2:0 chroma is what an encoder stores
        // for each 2x2 (the mix of its red and green Cb/Cr).
        let colour = |x: usize, y: usize| {
            if (x + y).is_multiple_of(2) {
                [1.0f32, 0.0, 0.0]
            } else {
                [0.0, 1.0, 0.0]
            }
        };
        for y in 0..h {
            for x in 0..w {
                let ycc = rgb709_to_ycbcr(colour(x, y));
                input[y * w + x] = ((64.0 + 876.0 * ycc[0]).round() as u16) << 6;
            }
        }
        // Both methods see the same input, so only the output chroma math differs.
        for y in 0..h / 2 {
            for x in 0..w / 2 {
                let mix = [0, 1].map(|c| {
                    let a = rgb709_to_ycbcr(colour(2 * x, 2 * y))[c + 1];
                    let b = rgb709_to_ycbcr(colour(2 * x + 1, 2 * y))[c + 1];
                    (a + b) / 2.0
                });
                for p in 0..2 {
                    input[w * h + y * w + 2 * x + p] =
                        ((512.0 + 896.0 * mix[p]).round() as u16) << 6;
                }
            }
        }
        let linear = reference_p010_with(&c, &f, &input, 16, 8, Method::Linear).unwrap();
        let legacy = reference_p010_with(&c, &f, &input, 16, 8, Method::Legacy).unwrap();
        // Luma is per output pixel in both: identical (no out-of-range values for BT.709 SDR).
        assert_eq!(linear[..w * h], legacy[..w * h]);
        let strength = |out: &[u16]| {
            out[w * h..]
                .iter()
                .map(|&code| i32::from(code) - 512)
                .map(i32::abs)
                .sum::<i32>()
        };
        let (s_linear, s_legacy) = (strength(&linear), strength(&legacy));
        assert!(
            s_linear * 10 > s_legacy * 11,
            "linear chroma {s_linear} vs gamma chroma {s_legacy}"
        );
        // Reconstructed light: decode each output pixel back to linear BT.709; the linear chroma
        // keeps at least the mean luminance of the gamma one (here it is equal or brighter).
        let light = |out: &[u16]| {
            let mut sum = 0.0f32;
            for y in 0..h {
                for x in 0..w {
                    let yy = (f32::from(out[y * w + x]) - 64.0) / 876.0;
                    let ci = w * h + (y / 2) * w + 2 * (x / 2);
                    let cb = (f32::from(out[ci]) - 512.0) / 896.0;
                    let cr = (f32::from(out[ci + 1]) - 512.0) / 896.0;
                    let r = yy + 1.5748 * cr;
                    let b = yy + 1.8556 * cb;
                    let g = (yy - 0.2126 * r - 0.0722 * b) / 0.7152;
                    let rgb = [r, g, b].map(|v| v.clamp(0.0, 1.0).powf(2.4));
                    sum += dot(luma_weights(false), rgb);
                }
            }
            sum / (w * h) as f32
        };
        assert!(
            light(&linear) >= light(&legacy) - 0.002,
            "linear {} vs gamma {}",
            light(&linear),
            light(&legacy)
        );
    }
    /// An out-of-gamut colour next to a neutral one: the current math averages the unclipped light
    /// and clips the average once; the legacy math clipped each pixel first.
    #[test]
    fn out_of_range_pairs_average_before_the_one_clip() {
        // Saturated BT.2020 green at ~200 nits: outside BT.709, so its BT.709 red/blue are negative.
        let rgb2020 = [0.0f32, 200.0, 0.0].map(pq_encode);
        let kw = luma_weights(true);
        let luma = dot(kw, rgb2020);
        let uv = [
            512.0 + 896.0 * (rgb2020[2] - luma) / (2.0 * (1.0 - kw[2])),
            512.0 + 896.0 * (rgb2020[0] - luma) / (2.0 * (1.0 - kw[0])),
        ];
        let y = 64.0 + 876.0 * luma;
        for curve in [1, 2] {
            let tm = |y, uv, extended| {
                tone_map_linear(
                    y, uv, 1000.0, 100.0, 0.0, curve, true, false, MODE_PQ, extended,
                )
            };
            let green = tm(y, uv, true);
            assert!(
                green[0] < -0.01 && green[2] < 0.0,
                "curve {curve}: {green:?}"
            );
            let green_clipped = tm(y, uv, false);
            assert_eq!(green_clipped, gamut_clip(green));
            let grey = tm(64.0 + 876.0 * pq_encode(100.0), [512.0; 2], true);
            let mean = |a: [f32; 3], b: [f32; 3]| {
                std::array::from_fn::<f32, 3, _>(|i| (a[i] + b[i]) / 2.0)
            };
            let late = finish_linear(mean(green, grey), true, MODE_PQ);
            let early = mean(green_clipped, grey);
            // The green's negative red takes light out of the pair's red channel.
            assert!(
                late[0] < early[0] - 0.005,
                "curve {curve}: {late:?} vs {early:?}"
            );
            assert!((late[1] - early[1]).abs() < 1e-6);
        }
        // BT.709 SDR stays unclipped through finish_linear; everything else is clipped there.
        assert_eq!(
            finish_linear([-0.2, 1.3, 0.5], false, MODE_SDR),
            [-0.2, 1.3, 0.5]
        );
        assert_eq!(
            finish_linear([-0.2, 1.3, 0.5], true, MODE_SDR),
            [0.0, 1.0, 0.5]
        );
        assert_eq!(
            finish_linear([-0.2, 1.3, 0.5], false, MODE_PQ),
            [0.0, 1.0, 0.5]
        );
    }
    /// In-range content is the same picture under both methods: they differ only where a value
    /// leaves 0..1 (clip) or chroma varies (linear vs encoded chroma mix).
    #[test]
    fn methods_agree_on_a_flat_in_gamut_picture() {
        let (w, h) = (32usize, 16usize);
        let mut input = vec![0u16; w * h * 3 / 2];
        input[..w * h].fill(600 << 6);
        input[w * h..]
            .iter_mut()
            .enumerate()
            .for_each(|(i, v)| *v = if i % 2 == 0 { 540 << 6 } else { 470 << 6 });
        let c = BaleToneMapConfig {
            reserved: [32, 16, 0, 0],
            ..config(1)
        };
        for f in [frame(), sdr709()] {
            let linear = reference_p010_with(&c, &f, &input, 32, 16, Method::Linear).unwrap();
            let legacy = reference_p010_with(&c, &f, &input, 32, 16, Method::Legacy).unwrap();
            assert!(linear.iter().zip(&legacy).all(|(a, b)| a.abs_diff(*b) <= 1));
        }
    }
    /// The extended math never produces NaN or infinity: every luma code with extreme chroma, all
    /// curves, desat on and off, every source mode and range.
    #[test]
    fn extended_math_stays_finite_for_out_of_range_inputs() {
        for curve in [1, 2] {
            for desat in [0.0, 2.0] {
                for (mode, bt2020) in [
                    (MODE_PQ, true),
                    (MODE_PQ, false),
                    (MODE_HLG, true),
                    (MODE_SDR, true),
                    (MODE_SDR, false),
                ] {
                    for full_range in [false, true] {
                        for y in (0..=1023).step_by(31) {
                            for uv in [
                                [0.0, 0.0],
                                [0.0, 1023.0],
                                [1023.0, 0.0],
                                [1023.0, 1023.0],
                                [512.0, 512.0],
                            ] {
                                let rgb = tone_map_linear(
                                    y as f32, uv, 1000.0, 100.0, desat, curve, bt2020, full_range,
                                    mode, true,
                                );
                                let out = encode_linear(finish_linear(rgb, bt2020, mode));
                                assert!(
                                    rgb.iter().chain(&out).all(|v| v.is_finite()),
                                    "curve {curve} desat {desat} mode {mode} 2020 {bt2020} y {y} uv {uv:?}: {rgb:?} {out:?}"
                                );
                            }
                        }
                    }
                }
            }
        }
    }
}
