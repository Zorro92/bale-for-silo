//! Output target of the debug IMFMediaEngine probe (`engine`) and the statistics of its HDR readback.
//!
//! Phase 0 of GPU tone mapping asks one question: does `TransferVideoFrame` hand over HDR10 frames
//! with their range intact when the output format can hold it? This module is the platform-neutral
//! part (option parsing, pixel statistics, the verdict), so it is unit-tested on Linux; `engine`
//! does the D3D11 work.

use std::fmt::Write as _;
use std::sync::OnceLock;

/// PQ code value of 100 nits on a full-range 10-bit scale (SMPTE ST 2084: 0.508 × 1023).
pub const PQ_SDR_WHITE_CODE: u16 = 520;
/// 10-bit codes at or above this count as "pinned at white": 940 is limited-range white, so it
/// catches both limited- and full-range SDR highlights. As PQ it would be about 4650 nits, above
/// the usual mastering peaks (1000 nits is code 769, 4000 nits is 923).
pub const TOP_CODE: u16 = 940;
/// Most pixels a tone-mapped SDR picture is allowed to have pinned at white before the rgb10
/// heuristic stops calling it PQ: 0.1%.
pub const TOP_SHARE_LIMIT: f64 = 0.001;
/// scRGB value above which fp16 output counts as HDR: 1.5 is 120 nits, clear of SDR white (1.0)
/// plus any filter overshoot.
pub const FP16_HDR_THRESHOLD: f32 = 1.5;
/// scRGB 1.0 is 80 nits.
pub const SCRGB_NITS: f32 = 80.0;

/// The format requested for both `MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT` and the destination texture.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Target {
    /// `DXGI_FORMAT_B8G8R8A8_UNORM`: the probe's original behaviour, no readback.
    Bgra,
    /// `DXGI_FORMAT_R16G16B16A16_FLOAT`: scRGB if the engine keeps the range.
    Fp16,
    /// `DXGI_FORMAT_R10G10B10A2_UNORM`: PQ code values if the engine keeps the range.
    Rgb10,
}

impl Target {
    /// Parses the `target=` option. Empty means the default, bgra; anything else unknown is `None`.
    pub fn parse(text: &str) -> Option<Self> {
        match text {
            "" | "bgra" => Some(Self::Bgra),
            "fp16" => Some(Self::Fp16),
            "rgb10" => Some(Self::Rgb10),
            _ => None,
        }
    }

    /// Parses the option as the C ABI passes it: UTF-16 code units.
    pub fn parse_utf16(text: &[u16]) -> Option<Self> {
        Self::parse(&String::from_utf16(text).ok()?)
    }

    pub fn name(self) -> &'static str {
        match self {
            Self::Bgra => "bgra",
            Self::Fp16 => "fp16",
            Self::Rgb10 => "rgb10",
        }
    }

    /// The `DXGI_FORMAT` value.
    pub fn dxgi_format(self) -> u32 {
        match self {
            Self::Bgra => 87,
            Self::Fp16 => 10,
            Self::Rgb10 => 24,
        }
    }

    pub fn format_name(self) -> &'static str {
        format_name(self.dxgi_format())
    }

    pub fn bytes_per_pixel(self) -> usize {
        match self {
            Self::Bgra | Self::Rgb10 => 4,
            Self::Fp16 => 8,
        }
    }
}

/// Names the formats the probe can meet; anything else is shown by number.
pub fn format_name(format: u32) -> &'static str {
    match format {
        87 => "B8G8R8A8_UNORM",
        10 => "R16G16B16A16_FLOAT",
        24 => "R10G10B10A2_UNORM",
        _ => "other",
    }
}

/// Side of the centred readback crop: the frame dimension, capped so a 4K readback stays cheap.
pub const CROP_LIMIT: u32 = 1024;

/// The centred crop of a `width` x `height` frame as `(left, top, crop_width, crop_height)`.
pub fn centred_crop(width: u32, height: u32) -> (u32, u32, u32, u32) {
    let crop_width = width.min(CROP_LIMIT);
    let crop_height = height.min(CROP_LIMIT);
    (
        (width - crop_width) / 2,
        (height - crop_height) / 2,
        crop_width,
        crop_height,
    )
}

/// IEEE 754 binary16 to binary32, exact for every input including subnormals, infinities and NaN.
pub fn half_to_f32(bits: u16) -> f32 {
    let sign = u32::from(bits >> 15) << 31;
    let exponent = u32::from(bits >> 10) & 0x1F;
    let mantissa = u32::from(bits) & 0x3FF;
    let magnitude = match exponent {
        // Zero and subnormals: mantissa × 2^-24, which f32 holds exactly.
        0 => (mantissa as f32 * (1.0 / 16_777_216.0)).to_bits(),
        31 => 0x7F80_0000 | (mantissa << 13),
        _ => ((exponent + 112) << 23) | (mantissa << 13),
    };
    f32::from_bits(sign | magnitude)
}

fn half_table() -> &'static [f32] {
    static TABLE: OnceLock<Vec<f32>> = OnceLock::new();
    TABLE.get_or_init(|| (0..=u16::MAX).map(half_to_f32).collect())
}

/// The smallest bin whose cumulative count reaches `fraction` of `total` (nearest-rank percentile).
/// An empty histogram gives bin 0.
pub fn percentile_bin(histogram: &[u32], total: u64, fraction: f64) -> usize {
    if total == 0 {
        return 0;
    }
    let rank = ((fraction * total as f64).ceil() as u64).clamp(1, total);
    let mut seen = 0u64;
    for (bin, count) in histogram.iter().enumerate() {
        seen += u64::from(*count);
        if seen >= rank {
            return bin;
        }
    }
    histogram.len().saturating_sub(1)
}

/// Light level of a full-range 10-bit PQ code value in nits (SMPTE ST 2084 EOTF).
pub fn pq_code_to_nits(code: u16) -> f64 {
    const M1: f64 = 2610.0 / 16384.0;
    const M2: f64 = 2523.0 / 4096.0 * 128.0;
    const C1: f64 = 3424.0 / 4096.0;
    const C2: f64 = 2413.0 / 4096.0 * 32.0;
    const C3: f64 = 2392.0 / 4096.0 * 32.0;
    let signal = (f64::from(code.min(1023)) / 1023.0).powf(1.0 / M2);
    ((signal - C1).max(0.0) / (C2 - C3 * signal)).powf(1.0 / M1) * 10_000.0
}

/// Bins of the fp16 histogram: positive half bit patterns up to +infinity (0x7C00), which sort
/// like their values.
const HALF_BINS: usize = 0x7C00 + 1;

/// Statistics of fp16 (scRGB) pixels.
#[derive(Clone)]
pub struct Fp16Stats {
    pub pixels: u64,
    sum: [f64; 3],
    pub max: [f32; 3],
    pub min: [f32; 3],
    /// Pixels with any of R, G, B above 1.0 (SDR white, 80 nits).
    pub above_white: u64,
    /// Pixels with a NaN channel; they count as pixels but not towards sums, maxima or minima.
    pub nan: u64,
    /// max(R, G, B) per pixel, floored at zero, by half bit pattern.
    histogram: Vec<u32>,
}

impl Default for Fp16Stats {
    fn default() -> Self {
        Self {
            pixels: 0,
            sum: [0.0; 3],
            max: [f32::NEG_INFINITY; 3],
            min: [f32::INFINITY; 3],
            above_white: 0,
            nan: 0,
            histogram: vec![0; HALF_BINS],
        }
    }
}

impl Fp16Stats {
    /// Adds one row of R16G16B16A16_FLOAT pixels (8 bytes each, little-endian; a trailing partial pixel is ignored).
    pub fn add_row(&mut self, row: &[u8]) {
        let table = half_table();
        for pixel in row.as_chunks::<8>().0 {
            self.pixels += 1;
            let mut bin = 0u16;
            let mut above = false;
            let mut nan = false;
            for channel in 0..3 {
                let bits = u16::from_le_bytes([pixel[channel * 2], pixel[channel * 2 + 1]]);
                let value = table[usize::from(bits)];
                if value.is_nan() {
                    nan = true;
                    continue;
                }
                self.sum[channel] += f64::from(value);
                self.max[channel] = self.max[channel].max(value);
                self.min[channel] = self.min[channel].min(value);
                above |= value > 1.0;
                // Positive, non-NaN halfs sort like their bit patterns; negatives floor at zero.
                if bits <= 0x7C00 {
                    bin = bin.max(bits);
                }
            }
            self.above_white += u64::from(above);
            self.nan += u64::from(nan);
            let count = &mut self.histogram[usize::from(bin)];
            *count = count.saturating_add(1);
        }
    }

    pub fn merge(&mut self, other: &Self) {
        self.pixels += other.pixels;
        self.above_white += other.above_white;
        self.nan += other.nan;
        for channel in 0..3 {
            self.sum[channel] += other.sum[channel];
            self.max[channel] = self.max[channel].max(other.max[channel]);
            self.min[channel] = self.min[channel].min(other.min[channel]);
        }
        for (count, added) in self.histogram.iter_mut().zip(&other.histogram) {
            *count = count.saturating_add(*added);
        }
    }

    pub fn mean(&self, channel: usize) -> f64 {
        if self.pixels == 0 {
            0.0
        } else {
            self.sum[channel] / self.pixels as f64
        }
    }

    /// The largest of the three channel maxima; 0 with no pixels.
    pub fn peak(&self) -> f32 {
        if self.pixels == 0 {
            return 0.0;
        }
        self.max.iter().copied().fold(f32::NEG_INFINITY, f32::max)
    }

    /// Nearest-rank percentile of max(R, G, B), floored at zero.
    pub fn percentile(&self, fraction: f64) -> f32 {
        half_to_f32(percentile_bin(&self.histogram, self.pixels, fraction) as u16)
    }

    pub fn above_white_share(&self) -> f64 {
        share(self.above_white, self.pixels)
    }

    /// HDR range survived if anything is clearly above SDR white.
    pub fn preserved(&self) -> bool {
        self.peak() > FP16_HDR_THRESHOLD
    }

    fn line(&self) -> String {
        if self.pixels == 0 {
            return "no pixels".to_owned();
        }
        let mut text = format!(
            "{} px; max R={:.3} G={:.3} B={:.3}; mean R={:.4} G={:.4} B={:.4}; min R={:.3} G={:.3} B={:.3}; \
             max(R,G,B) p50={:.3} p99={:.3} max={:.3} ({:.0} nits); above 1.0: {:.3}% of pixels",
            self.pixels,
            self.max[0],
            self.max[1],
            self.max[2],
            self.mean(0),
            self.mean(1),
            self.mean(2),
            self.min[0],
            self.min[1],
            self.min[2],
            self.percentile(0.5),
            self.percentile(0.99),
            self.peak(),
            self.peak() * SCRGB_NITS,
            self.above_white_share() * 100.0
        );
        if self.nan > 0 {
            let _ = write!(text, "; NaN pixels: {}", self.nan);
        }
        text
    }

    fn verdict(&self) -> String {
        let peak = self.peak();
        format!(
            "hdr-range={} (fp16 max {:.3} = {:.0} nits, threshold {FP16_HDR_THRESHOLD}; p99 {:.3}; {:.3}% of pixels above 1.0)",
            if self.preserved() {
                "preserved"
            } else {
                "clipped/sdr"
            },
            peak,
            peak * SCRGB_NITS,
            self.percentile(0.99),
            self.above_white_share() * 100.0
        )
    }
}

/// Statistics of R10G10B10A2 pixels as raw 10-bit code values.
#[derive(Clone)]
pub struct Rgb10Stats {
    pub pixels: u64,
    sum: [u64; 3],
    pub max: [u16; 3],
    /// max(R, G, B) per pixel.
    histogram: Vec<u32>,
}

impl Default for Rgb10Stats {
    fn default() -> Self {
        Self {
            pixels: 0,
            sum: [0; 3],
            max: [0; 3],
            histogram: vec![0; 1024],
        }
    }
}

impl Rgb10Stats {
    /// Adds one row of R10G10B10A2_UNORM pixels (little-endian u32: R in bits 0–9, G 10–19, B 20–29).
    pub fn add_row(&mut self, row: &[u8]) {
        for pixel in row.as_chunks::<4>().0 {
            let packed = u32::from_le_bytes([pixel[0], pixel[1], pixel[2], pixel[3]]);
            let channels = [
                (packed & 0x3FF) as u16,
                ((packed >> 10) & 0x3FF) as u16,
                ((packed >> 20) & 0x3FF) as u16,
            ];
            self.pixels += 1;
            let mut top = 0u16;
            for (channel, value) in channels.into_iter().enumerate() {
                self.sum[channel] += u64::from(value);
                self.max[channel] = self.max[channel].max(value);
                top = top.max(value);
            }
            let count = &mut self.histogram[usize::from(top)];
            *count = count.saturating_add(1);
        }
    }

    pub fn merge(&mut self, other: &Self) {
        self.pixels += other.pixels;
        for channel in 0..3 {
            self.sum[channel] += other.sum[channel];
            self.max[channel] = self.max[channel].max(other.max[channel]);
        }
        for (count, added) in self.histogram.iter_mut().zip(&other.histogram) {
            *count = count.saturating_add(*added);
        }
    }

    pub fn mean(&self, channel: usize) -> f64 {
        if self.pixels == 0 {
            0.0
        } else {
            self.sum[channel] as f64 / self.pixels as f64
        }
    }

    /// Nearest-rank percentile of max(R, G, B); 1.0 is the maximum.
    pub fn percentile(&self, fraction: f64) -> u16 {
        percentile_bin(&self.histogram, self.pixels, fraction) as u16
    }

    /// Share of pixels whose max(R, G, B) is at least `code`.
    pub fn share_at_or_above(&self, code: u16) -> f64 {
        let count: u64 = self.histogram[usize::from(code.min(1023))..]
            .iter()
            .map(|count| u64::from(*count))
            .sum();
        share(count, self.pixels)
    }

    /// Whether the picture looks PQ-coded rather than SDR gamma. A simple, deliberately readable
    /// heuristic, to be judged together with the printed numbers:
    ///
    /// - the median of max(R,G,B) is below [`PQ_SDR_WHITE_CODE`]: most of a PQ picture sits under
    ///   100 nits, in the lower half of the code range;
    /// - the 99th percentile is above it: there is a tail of highlights brighter than SDR white;
    /// - fewer than [`TOP_SHARE_LIMIT`] of the pixels reach [`TOP_CODE`]: PQ highlights stop near
    ///   the mastering peak, while highlights tone-mapped or clipped to SDR pile up at white
    ///   (940 limited range, 1023 full range).
    ///
    /// A dim SDR picture can pass and a pattern brighter than 4650 nits can fail, so compare with a bgra or
    /// fp16 run of the same clip before trusting it.
    pub fn looks_pq(&self) -> bool {
        self.pixels > 0
            && self.percentile(0.5) < PQ_SDR_WHITE_CODE
            && self.percentile(0.99) > PQ_SDR_WHITE_CODE
            && self.share_at_or_above(TOP_CODE) < TOP_SHARE_LIMIT
    }

    fn line(&self) -> String {
        if self.pixels == 0 {
            return "no pixels".to_owned();
        }
        let top = self.percentile(1.0);
        format!(
            "{} px; max R={} G={} B={}; mean R={:.1} G={:.1} B={:.1}; max(R,G,B) p50={} p99={} max={} \
             ({:.0} nits if full-range PQ); above code {PQ_SDR_WHITE_CODE} (100 nits if PQ): {:.3}% of pixels; \
             at or above {TOP_CODE}: {:.3}%",
            self.pixels,
            self.max[0],
            self.max[1],
            self.max[2],
            self.mean(0),
            self.mean(1),
            self.mean(2),
            self.percentile(0.5),
            self.percentile(0.99),
            top,
            pq_code_to_nits(top),
            self.share_at_or_above(PQ_SDR_WHITE_CODE + 1) * 100.0,
            self.share_at_or_above(TOP_CODE) * 100.0
        )
    }

    fn verdict(&self) -> String {
        let pq = self.looks_pq();
        format!(
            "hdr-range={} (rgb10 {}: p50 {} {} {PQ_SDR_WHITE_CODE}, p99 {} {} {PQ_SDR_WHITE_CODE}, {:.3}% of pixels at or above code {TOP_CODE} (limit {:.1}%), max {}; heuristic)",
            if pq { "preserved" } else { "clipped/sdr" },
            if pq { "looks PQ" } else { "does not look PQ" },
            self.percentile(0.5),
            if self.percentile(0.5) < PQ_SDR_WHITE_CODE {
                "<"
            } else {
                ">="
            },
            self.percentile(0.99),
            if self.percentile(0.99) > PQ_SDR_WHITE_CODE {
                ">"
            } else {
                "<="
            },
            self.share_at_or_above(TOP_CODE) * 100.0,
            TOP_SHARE_LIMIT * 100.0,
            self.percentile(1.0)
        )
    }
}

fn share(count: u64, total: u64) -> f64 {
    if total == 0 {
        0.0
    } else {
        count as f64 / total as f64
    }
}

#[derive(Clone)]
enum Stats {
    Fp16(Fp16Stats),
    Rgb10(Rgb10Stats),
}

impl Stats {
    fn new(target: Target) -> Option<Self> {
        match target {
            Target::Bgra => None,
            Target::Fp16 => Some(Self::Fp16(Fp16Stats::default())),
            Target::Rgb10 => Some(Self::Rgb10(Rgb10Stats::default())),
        }
    }

    fn line(&self) -> String {
        match self {
            Self::Fp16(stats) => stats.line(),
            Self::Rgb10(stats) => stats.line(),
        }
    }
}

/// Collects readback samples: the statistics of the latest one and of all of them together.
pub struct Sampler {
    target: Target,
    total: Stats,
    last: Stats,
    samples: u32,
}

impl Sampler {
    /// `None` for bgra, which has no readback.
    pub fn new(target: Target) -> Option<Self> {
        Some(Self {
            target,
            total: Stats::new(target)?,
            last: Stats::new(target)?,
            samples: 0,
        })
    }

    /// Starts a new sample, forgetting the previous "last" one.
    pub fn begin_sample(&mut self) {
        if let Some(fresh) = Stats::new(self.target) {
            self.last = fresh;
        }
    }

    /// Adds one row of the mapped crop: exactly the crop width times the pixel size, without row padding.
    pub fn add_row(&mut self, row: &[u8]) {
        match &mut self.last {
            Stats::Fp16(stats) => stats.add_row(row),
            Stats::Rgb10(stats) => stats.add_row(row),
        }
    }

    /// Folds the finished sample into the totals.
    pub fn end_sample(&mut self) {
        match (&mut self.total, &self.last) {
            (Stats::Fp16(total), Stats::Fp16(last)) => total.merge(last),
            (Stats::Rgb10(total), Stats::Rgb10(last)) => total.merge(last),
            _ => {}
        }
        self.samples += 1;
    }

    pub fn samples(&self) -> u32 {
        self.samples
    }

    /// The statistics lines and the verdict line, each newline-terminated. The verdict uses all
    /// samples, so a fade to black on the last frame cannot hide an HDR picture.
    pub fn report(&self) -> String {
        let scale = match self.target {
            Target::Fp16 => "fp16 scRGB, 1.0 = 80 nits",
            _ => "rgb10 code values 0-1023",
        };
        if self.samples == 0 {
            return format!(
                "hdr-range=unknown ({} target, no readback samples)\n",
                self.target.name()
            );
        }
        let verdict = match &self.total {
            Stats::Fp16(stats) => stats.verdict(),
            Stats::Rgb10(stats) => stats.verdict(),
        };
        format!(
            "HDR readback ({scale}), all {} samples: {}\nHDR readback ({scale}), last sample: {}\n{verdict}\n",
            self.samples,
            self.total.line(),
            self.last.line()
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fp16_row(pixels: &[[f32; 3]]) -> Vec<u8> {
        let mut row = Vec::new();
        for pixel in pixels {
            for value in pixel {
                row.extend_from_slice(&f32_to_half(*value).to_le_bytes());
            }
            row.extend_from_slice(&0x3C00u16.to_le_bytes()); // alpha 1.0
        }
        row
    }

    /// Test-only inverse for values a half holds exactly: found by search over the positive patterns.
    fn f32_to_half(value: f32) -> u16 {
        let sign = if value.is_sign_negative() { 0x8000 } else { 0 };
        let magnitude = value.abs();
        let bits = (0..=0x7C00u16)
            .find(|bits| half_to_f32(*bits) >= magnitude)
            .expect("value fits a half");
        assert_eq!(
            half_to_f32(bits),
            magnitude,
            "{value} is not exact in a half"
        );
        sign | bits
    }

    fn rgb10_row(pixels: &[[u16; 3]]) -> Vec<u8> {
        pixels
            .iter()
            .flat_map(|[r, g, b]| {
                (u32::from(*r) | (u32::from(*g) << 10) | (u32::from(*b) << 20) | (3 << 30))
                    .to_le_bytes()
            })
            .collect()
    }

    #[test]
    fn half_conversion_matches_known_values() {
        for (bits, value) in [
            (0x0000u16, 0.0f32),
            (0x3C00, 1.0),
            (0xBC00, -1.0),
            (0x4000, 2.0),
            (0x3800, 0.5),
            (0x3E00, 1.5),
            (0x4900, 10.0),
            (0x7BFF, 65504.0),
            (0x0400, 1.0 / 16_384.0),        // smallest normal, 2^-14
            (0x0001, 1.0 / 16_777_216.0),    // smallest subnormal, 2^-24
            (0x03FF, 1023.0 / 16_777_216.0), // largest subnormal
            (0x3555, 0.333_251_95),
        ] {
            assert_eq!(half_to_f32(bits), value, "0x{bits:04X}");
        }
        assert_eq!(half_to_f32(0x8000).to_bits(), (-0.0f32).to_bits());
        assert_eq!(half_to_f32(0x7C00), f32::INFINITY);
        assert_eq!(half_to_f32(0xFC00), f32::NEG_INFINITY);
        assert!(half_to_f32(0x7E00).is_nan());
        assert!(half_to_f32(0xFC01).is_nan());
    }

    #[test]
    fn half_conversion_is_monotonic_over_positive_patterns() {
        let mut previous = half_to_f32(0);
        for bits in 1..=0x7C00u16 {
            let value = half_to_f32(bits);
            assert!(value > previous, "0x{bits:04X}");
            previous = value;
        }
    }

    #[test]
    fn target_option_parses_the_three_names_and_defaults_to_bgra() {
        assert_eq!(Target::parse(""), Some(Target::Bgra));
        assert_eq!(Target::parse("bgra"), Some(Target::Bgra));
        assert_eq!(Target::parse("fp16"), Some(Target::Fp16));
        assert_eq!(Target::parse("rgb10"), Some(Target::Rgb10));
        for bad in [
            "FP16",
            "fp16 ",
            " rgb10",
            "rgba16f",
            "p010",
            "10",
            "bgra,fp16",
        ] {
            assert_eq!(Target::parse(bad), None, "{bad:?}");
        }
        let wide: Vec<u16> = "rgb10".encode_utf16().collect();
        assert_eq!(Target::parse_utf16(&wide), Some(Target::Rgb10));
        assert_eq!(Target::parse_utf16(&[]), Some(Target::Bgra));
        assert_eq!(Target::parse_utf16(&[0xD800]), None); // lone surrogate
    }

    #[test]
    fn target_formats_are_the_dxgi_values() {
        assert_eq!(
            (
                Target::Bgra.dxgi_format(),
                Target::Bgra.format_name(),
                Target::Bgra.bytes_per_pixel()
            ),
            (87, "B8G8R8A8_UNORM", 4)
        );
        assert_eq!(
            (
                Target::Fp16.dxgi_format(),
                Target::Fp16.format_name(),
                Target::Fp16.bytes_per_pixel()
            ),
            (10, "R16G16B16A16_FLOAT", 8)
        );
        assert_eq!(
            (
                Target::Rgb10.dxgi_format(),
                Target::Rgb10.format_name(),
                Target::Rgb10.bytes_per_pixel()
            ),
            (24, "R10G10B10A2_UNORM", 4)
        );
        assert_eq!(format_name(103), "other");
        for target in [Target::Bgra, Target::Fp16, Target::Rgb10] {
            assert_eq!(Target::parse(target.name()), Some(target));
        }
    }

    #[test]
    fn crop_is_centred_and_capped() {
        assert_eq!(centred_crop(3840, 2160), (1408, 568, 1024, 1024));
        assert_eq!(centred_crop(1920, 1080), (448, 28, 1024, 1024));
        assert_eq!(centred_crop(1280, 720), (128, 0, 1024, 720));
        assert_eq!(centred_crop(320, 240), (0, 0, 320, 240));
    }

    #[test]
    fn percentiles_use_nearest_rank() {
        // Values 1..=100, one each: p50 is 50, p99 is 99, p100 is 100.
        let mut histogram = vec![0u32; 128];
        for count in &mut histogram[1..=100] {
            *count = 1;
        }
        assert_eq!(percentile_bin(&histogram, 100, 0.5), 50);
        assert_eq!(percentile_bin(&histogram, 100, 0.99), 99);
        assert_eq!(percentile_bin(&histogram, 100, 1.0), 100);
        assert_eq!(percentile_bin(&histogram, 100, 0.0), 1);
        // A heavy bin holds the median.
        let skewed = [0u32, 90, 0, 9, 1];
        assert_eq!(percentile_bin(&skewed, 100, 0.5), 1);
        assert_eq!(percentile_bin(&skewed, 100, 0.9), 1);
        assert_eq!(percentile_bin(&skewed, 100, 0.91), 3);
        assert_eq!(percentile_bin(&skewed, 100, 0.99), 3);
        assert_eq!(percentile_bin(&skewed, 100, 1.0), 4);
        assert_eq!(percentile_bin(&[0, 0, 0], 0, 0.5), 0);
    }

    #[test]
    fn pq_codes_map_to_known_light_levels() {
        assert_eq!(pq_code_to_nits(0), 0.0);
        assert!((pq_code_to_nits(1023) - 10_000.0).abs() < 1e-6);
        assert!((pq_code_to_nits(PQ_SDR_WHITE_CODE) - 100.0).abs() < 1.0);
        assert!((pq_code_to_nits(769) - 1000.0).abs() < 10.0);
        assert!((pq_code_to_nits(923) - 4000.0).abs() < 60.0);
    }

    #[test]
    fn fp16_statistics_and_verdict() {
        let mut hdr = Fp16Stats::default();
        hdr.add_row(&fp16_row(&[
            [0.25, 0.5, 0.125],
            [12.5, 2.0, 0.5],
            [-0.25, 0.5, 0.75],
            [1.0, 1.0, 1.0],
        ]));
        assert_eq!(hdr.pixels, 4);
        assert_eq!(hdr.max, [12.5, 2.0, 1.0]);
        assert_eq!(hdr.min, [-0.25, 0.5, 0.125]);
        assert_eq!(hdr.mean(0), (0.25 + 12.5 - 0.25 + 1.0) / 4.0);
        assert_eq!(hdr.above_white, 1); // exactly 1.0 is not above white
        assert_eq!(hdr.peak(), 12.5);
        // max(R,G,B) per pixel: 0.5, 12.5, 0.75, 1.0.
        assert_eq!(hdr.percentile(0.5), 0.75);
        assert_eq!(hdr.percentile(1.0), 12.5);
        assert!(hdr.preserved());
        let verdict = hdr.verdict();
        assert!(
            verdict.starts_with("hdr-range=preserved (fp16 max 12.500 = 1000 nits"),
            "{verdict}"
        );
        assert!(verdict.contains("25.000% of pixels above 1.0"), "{verdict}");

        let mut sdr = Fp16Stats::default();
        sdr.add_row(&fp16_row(&[
            [1.0, 1.0, 1.0],
            [0.5, 0.25, 0.0],
            [1.25, 0.0, 0.0],
        ]));
        assert!(!sdr.preserved(), "1.25 is overshoot, not HDR");
        assert!(
            sdr.verdict()
                .starts_with("hdr-range=clipped/sdr (fp16 max 1.250 = 100 nits")
        );
        let mut edge = Fp16Stats::default();
        edge.add_row(&fp16_row(&[[1.5, 0.0, 0.0]]));
        assert!(!edge.preserved(), "the threshold itself is not above it");
    }

    #[test]
    fn fp16_ignores_nan_and_floors_negative_pixels_in_the_histogram() {
        let mut stats = Fp16Stats::default();
        let mut row = fp16_row(&[[-1.0, -0.5, -0.25], [0.5, 0.5, 0.5]]);
        row.extend_from_slice(&[0x00, 0x7E, 0x00, 0x3C, 0x00, 0x3C, 0x00, 0x3C]); // R = NaN, G = B = 1.0
        row.extend_from_slice(&[1, 2, 3]); // partial pixel, ignored
        stats.add_row(&row);
        assert_eq!((stats.pixels, stats.nan), (3, 1));
        assert_eq!(stats.max, [0.5, 1.0, 1.0]);
        assert_eq!(stats.min, [-1.0, -0.5, -0.25]);
        assert_eq!(stats.percentile(0.01), 0.0); // the all-negative pixel
        assert_eq!(stats.percentile(1.0), 1.0);
        assert!(stats.line().contains("NaN pixels: 1"));
        assert_eq!(Fp16Stats::default().peak(), 0.0);
        assert!(!Fp16Stats::default().preserved());
    }

    #[test]
    fn rgb10_statistics_unpack_the_channels() {
        let mut stats = Rgb10Stats::default();
        let mut row = rgb10_row(&[[1023, 0, 0], [0, 512, 0], [0, 0, 64], [100, 200, 300]]);
        row.push(0xFF); // partial pixel, ignored
        stats.add_row(&row);
        assert_eq!(stats.pixels, 4);
        assert_eq!(stats.max, [1023, 512, 300]);
        assert_eq!(stats.mean(0), (1023.0 + 100.0) / 4.0);
        assert_eq!(stats.mean(1), (512.0 + 200.0) / 4.0);
        assert_eq!(stats.mean(2), (64.0 + 300.0) / 4.0);
        // max(R,G,B): 1023, 512, 64, 300.
        assert_eq!(stats.percentile(0.5), 300);
        assert_eq!(stats.percentile(1.0), 1023);
        assert_eq!(stats.share_at_or_above(TOP_CODE), 0.25);
        assert_eq!(stats.share_at_or_above(0), 1.0);
    }

    /// `count` pixels of grey at `code`.
    fn grey(stats: &mut Rgb10Stats, code: u16, count: usize) {
        stats.add_row(&rgb10_row(&vec![[code, code, code]; count]));
    }

    #[test]
    fn rgb10_verdict_separates_pq_from_sdr_gamma() {
        // PQ: bulk around 20 nits, a 3% highlight tail near 1000 nits, nothing at full scale.
        let mut pq = Rgb10Stats::default();
        grey(&mut pq, 370, 9_700);
        grey(&mut pq, 769, 300);
        assert!(pq.looks_pq());
        let verdict = pq.verdict();
        assert!(
            verdict
                .starts_with("hdr-range=preserved (rgb10 looks PQ: p50 370 < 520, p99 769 > 520"),
            "{verdict}"
        );

        // Tone-mapped SDR: the same picture with its highlights pinned at white.
        let mut clipped = Rgb10Stats::default();
        grey(&mut clipped, 370, 9_700);
        grey(&mut clipped, 1023, 300);
        assert!(!clipped.looks_pq());
        assert!(
            clipped
                .verdict()
                .starts_with("hdr-range=clipped/sdr (rgb10 does not look PQ")
        );

        // A bright SDR picture: the median is already above the PQ SDR-white code.
        let mut bright = Rgb10Stats::default();
        grey(&mut bright, 600, 9_000);
        grey(&mut bright, 900, 1_000);
        assert!(!bright.looks_pq());

        // No tail above SDR white at all.
        let mut flat = Rgb10Stats::default();
        grey(&mut flat, 300, 10_000);
        assert!(!flat.looks_pq());

        // A handful of full-scale pixels (below the 0.1% limit) does not flip a PQ picture.
        grey(&mut pq, 1023, 5);
        assert!(pq.looks_pq());
        assert!(!Rgb10Stats::default().looks_pq());
    }

    #[test]
    fn sampler_keeps_the_last_sample_and_the_total_apart() {
        assert!(Sampler::new(Target::Bgra).is_none());
        let mut sampler = Sampler::new(Target::Fp16).expect("fp16 has a readback");
        assert_eq!(
            sampler.report(),
            "hdr-range=unknown (fp16 target, no readback samples)\n"
        );
        sampler.begin_sample();
        sampler.add_row(&fp16_row(&[[8.0, 0.5, 0.5], [0.5, 0.5, 0.5]]));
        sampler.end_sample();
        // The last frame is black: the verdict must still see the earlier highlight.
        sampler.begin_sample();
        sampler.add_row(&fp16_row(&[[0.0, 0.0, 0.0], [0.0, 0.0, 0.0]]));
        sampler.end_sample();
        assert_eq!(sampler.samples(), 2);
        let report = sampler.report();
        let lines: Vec<&str> = report.lines().collect();
        assert_eq!(lines.len(), 3, "{report}");
        assert!(
            lines[0].starts_with(
                "HDR readback (fp16 scRGB, 1.0 = 80 nits), all 2 samples: 4 px; max R=8.000"
            ),
            "{report}"
        );
        assert!(
            lines[1].starts_with(
                "HDR readback (fp16 scRGB, 1.0 = 80 nits), last sample: 2 px; max R=0.000"
            ),
            "{report}"
        );
        assert!(
            lines[2].starts_with("hdr-range=preserved (fp16 max 8.000 = 640 nits"),
            "{report}"
        );

        let mut rgb10 = Sampler::new(Target::Rgb10).expect("rgb10 has a readback");
        rgb10.begin_sample();
        rgb10.add_row(&rgb10_row(&[[940, 940, 940], [64, 64, 64]]));
        rgb10.end_sample();
        let report = rgb10.report();
        assert!(report.contains("HDR readback (rgb10 code values 0-1023), all 1 samples: 2 px; max R=940 G=940 B=940"), "{report}");
        assert!(report.ends_with("heuristic)\n"), "{report}");
        assert!(report.contains("hdr-range=clipped/sdr"), "{report}");
    }
}
