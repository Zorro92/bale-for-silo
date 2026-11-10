//! Grain inputs of the fused zero-copy repack (`shaders/repack_grain_zc_cs.hlsl`, compiled once
//! per bit depth, 8 and 10): one raw buffer per frame, bound as root SRV t1, built from exactly the
//! prepared inputs the CPU oracle consumes ([`cpu::with_prepared`]). The layout is the same at
//! both depths; everything depth-dependent (clip ranges, grain range, offsets, pixel_max) is in
//! the header.
//!
//! Layout (little-endian; every section starts 4-byte aligned; the shader comment repeats it):
//! - header, 32 words (128 bytes), i32 or u32:
//!   0 width, 1 height, 2 scaling_shift, 3 overlap, 4 csfl, 5 luma_active,
//!   6..=9 cb: mult, luma_mult, offset << (bit_depth - 8), active,
//!   10..=13 cr: the same, 14 luma_min, 15 luma_max, 16 chroma_min, 17 chroma_max,
//!   18 grain_min, 19 grain_max, 20 offset row pitch in 32x32 blocks,
//!   21..=24 byte offsets of template_y, template_uv, scaling and offsets, 25 pixel_max,
//!   26..=31 zero
//! - template_y: 82x73 i16, row-major (entry y * 82 + x), two per word, low half first
//! - template_uv: 44x38 words, row-major; cb i16 in the low half, cr in the high half (zeros for
//!   an inactive plane)
//! - scaling: three rows of 1024 u8 (Y, Cb, Cr), entry row * 1024 + value; an 8-bit row has 256
//!   entries and is zero-padded to 1024
//! - offsets: one u8 per 32x32 luma block, row-major, zero-padded to a whole word

use super::cpu;
use super::offsets::FG_BLOCK_SIZE;
use super::params::{BaleGrainParams, PARAMS_SIZE, ValidGrainParams};
use super::template::{GRAIN_HEIGHT, GRAIN_WIDTH, SUB_GRAIN_HEIGHT, SUB_GRAIN_WIDTH};

const HEADER_WORDS: usize = 32;
const TEMPLATE_Y_AT: usize = HEADER_WORDS * 4;
const TEMPLATE_UV_AT: usize = TEMPLATE_Y_AT + GRAIN_WIDTH * GRAIN_HEIGHT * 2;
/// The scaling row width: the 10-bit LUT's (an 8-bit LUT is zero-padded to it).
const SCALING_WIDTH: usize = 1024;
const SCALING_AT: usize = TEMPLATE_UV_AT + SUB_GRAIN_WIDTH * SUB_GRAIN_HEIGHT * 4;
const OFFSETS_AT: usize = SCALING_AT + 3 * SCALING_WIDTH;

/// Bytes of the grain buffer for a `width` x `height` frame.
pub(crate) fn bytes(width: usize, height: usize) -> usize {
    OFFSETS_AT
        + (width.div_ceil(FG_BLOCK_SIZE) * height.div_ceil(FG_BLOCK_SIZE)).next_multiple_of(4)
}

/// The grain buffer for `params`; `None` unless 8- or 10-bit (the fused shader's depths).
pub(crate) fn build(params: &ValidGrainParams) -> Option<Vec<u8>> {
    if !matches!(params.as_raw().bit_depth, 8 | 10) {
        return None;
    }
    let raw = params.as_raw();
    let bd8 = params.bitdepth_min_8();
    let (luma_min, luma_max) = cpu::clip_range(params, false);
    let (chroma_min, chroma_max) = cpu::clip_range(params, true);
    let center = 128 << bd8;
    let plane = |p: usize| {
        [
            i32::from(raw.uv_mult[p]),
            i32::from(raw.uv_luma_mult[p]),
            i32::from(raw.uv_offset[p]) * (1 << bd8),
            i32::from(params.chroma_active(p)),
        ]
    };
    let cols = params.width().div_ceil(FG_BLOCK_SIZE);
    let mut header = [0i32; HEADER_WORDS];
    header[..6].copy_from_slice(&[
        raw.width as i32,
        raw.height as i32,
        i32::from(raw.scaling_shift),
        i32::from(raw.overlap_flag),
        i32::from(raw.chroma_scaling_from_luma),
        i32::from(raw.num_y_points != 0),
    ]);
    header[6..10].copy_from_slice(&plane(0));
    header[10..14].copy_from_slice(&plane(1));
    header[14..26].copy_from_slice(&[
        luma_min,
        luma_max,
        chroma_min,
        chroma_max,
        -center,
        center - 1,
        cols as i32,
        TEMPLATE_Y_AT as i32,
        TEMPLATE_UV_AT as i32,
        SCALING_AT as i32,
        OFFSETS_AT as i32,
        params.pixel_max(),
    ]);

    let mut out = Vec::with_capacity(bytes(params.width(), params.height()));
    out.extend(header.iter().flat_map(|word| word.to_le_bytes()));
    cpu::with_prepared(params, |prepared| {
        let templates = &prepared.templates;
        out.extend(templates.y.iter().flatten().flat_map(|v| v.to_le_bytes()));
        for y in 0..SUB_GRAIN_HEIGHT {
            for x in 0..SUB_GRAIN_WIDTH {
                for lut in &templates.uv {
                    out.extend(lut.as_ref().map_or(0, |lut| lut[y][x]).to_le_bytes());
                }
            }
        }
        for row in &prepared.scaling {
            debug_assert_eq!(row.len(), 1 << raw.bit_depth);
            out.extend_from_slice(row);
            out.resize(out.len() + SCALING_WIDTH - row.len(), 0);
        }
        out.extend_from_slice(prepared.offsets.as_bytes());
    });
    out.resize(bytes(params.width(), params.height()), 0);
    Some(out)
}

/// Deterministic 10-bit grain for a `width` x `height` frame: AR lag 3, overlap, restricted
/// clipping, both chroma planes with their own points (no chroma-from-luma). For the zero-copy
/// self-test and the layout tests; valid at bit depth 8 too.
pub(crate) fn self_test_params(width: u32, height: u32) -> BaleGrainParams {
    let mut p = BaleGrainParams {
        struct_size: PARAMS_SIZE,
        seed: 45231,
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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::zc_frame::grain_p010;

    /// The buffer as the shader sees it: word loads only, at 4-byte aligned addresses, and every
    /// load must stay inside the buffer (root SRVs are not bounds-checked).
    struct Buffer<'a>(&'a [u8]);

    impl Buffer<'_> {
        fn load(&self, address: u32) -> u32 {
            assert_eq!(address % 4, 0, "unaligned load");
            let at = address as usize;
            assert!(at + 4 <= self.0.len(), "load past the buffer at {at}");
            u32::from_le_bytes(self.0[at..at + 4].try_into().unwrap())
        }
        fn byte(&self, address: u32) -> u32 {
            (self.load(address & !3) >> ((address & 3) * 8)) & 0xff
        }
    }

    struct Header {
        scaling_shift: i32,
        overlap: bool,
        csfl: bool,
        luma_active: bool,
        cb: [i32; 4],
        cr: [i32; 4],
        luma: (i32, i32),
        chroma: (i32, i32),
        grain: (i32, i32),
        cols: u32,
        template_y: u32,
        template_uv: u32,
        scaling: u32,
        offsets: u32,
        pixel_max: i32,
    }

    fn header(b: &Buffer<'_>) -> Header {
        let w = |i: u32| b.load(4 * i);
        let s = |i: u32| w(i) as i32;
        Header {
            scaling_shift: s(2),
            overlap: w(3) != 0,
            csfl: w(4) != 0,
            luma_active: w(5) != 0,
            cb: [s(6), s(7), s(8), s(9)],
            cr: [s(10), s(11), s(12), s(13)],
            luma: (s(14), s(15)),
            chroma: (s(16), s(17)),
            grain: (s(18), s(19)),
            cols: w(20),
            template_y: w(21),
            template_uv: w(22),
            scaling: w(23),
            offsets: w(24),
            pixel_max: s(25),
        }
    }

    fn round2(value: i32, shift: i32) -> i32 {
        (value + ((1 << shift) >> 1)) >> shift
    }

    /// A transliteration of `repack_grain_zc_cs.hlsl`, one pixel at a time. Like a shader whose
    /// branches are flattened, it also evaluates every overlap sample the pixel does not use, and
    /// the buffer asserts that those loads stay inside it too. `shift` is the shader's OUT_SHIFT:
    /// 6 for the 10-bit build, 8 for the 8-bit one (`BALE_8BIT`).
    struct Model<'a> {
        b: Buffer<'a>,
        h: Header,
        width: i32,
        shift: u32,
    }

    impl Model<'_> {
        fn blend(&self, old: i32, current: i32, w: (i32, i32)) -> i32 {
            round2(old * w.0 + current * w.1, 5).clamp(self.h.grain.0, self.h.grain.1)
        }

        fn block_offset(&self, block: (i32, i32)) -> u32 {
            let address = self.h.offsets as i32 + block.1 * self.h.cols as i32 + block.0;
            self.b.byte(address as u32)
        }

        fn luma_template(&self, at: (i32, i32)) -> i32 {
            let address = self.h.template_y + ((at.1 * 82 + at.0) * 2) as u32;
            let word = self.b.load(address & !3);
            if address & 2 != 0 {
                (word as i32) >> 16
            } else {
                ((word << 16) as i32) >> 16
            }
        }

        fn chroma_template(&self, at: (i32, i32)) -> (i32, i32) {
            let word = self
                .b
                .load(self.h.template_uv + ((at.1 * 44 + at.0) * 4) as u32);
            (((word << 16) as i32) >> 16, (word as i32) >> 16)
        }

        fn luma_sample(&self, block: (i32, i32), local: (i32, i32), prev: (i32, i32)) -> i32 {
            let r = self.block_offset((block.0 - prev.0, block.1 - prev.1));
            let origin = (3 + 2 * (3 + (r >> 4) as i32), 3 + 2 * (3 + (r & 15) as i32));
            self.luma_template((
                origin.0 + local.0 + 32 * prev.0,
                origin.1 + local.1 + 32 * prev.1,
            ))
        }

        fn chroma_sample(
            &self,
            block: (i32, i32),
            local: (i32, i32),
            prev: (i32, i32),
        ) -> (i32, i32) {
            let r = self.block_offset((block.0 - prev.0, block.1 - prev.1));
            let origin = (3 + (3 + (r >> 4) as i32), 3 + (3 + (r & 15) as i32));
            self.chroma_template((
                origin.0 + local.0 + 16 * prev.0,
                origin.1 + local.1 + 16 * prev.1,
            ))
        }

        fn scale(&self, row: u32, index: i32) -> i32 {
            self.b.byte(self.h.scaling + row * 1024 + index as u32) as i32
        }

        fn luma(&self, pixel: (i32, i32), value: u32) -> u32 {
            if pixel.0 >= self.width || !self.h.luma_active {
                return value << self.shift;
            }
            let block = (pixel.0 >> 5, pixel.1 >> 5);
            let local = (pixel.0 & 31, pixel.1 & 31);
            let horizontal = self.h.overlap && block.0 > 0 && local.0 < 2;
            let vertical = self.h.overlap && block.1 > 0 && local.1 < 2;
            let wx = if local.0 == 0 { (27, 17) } else { (17, 27) };
            let wy = if local.1 == 0 { (27, 17) } else { (17, 27) };
            let speculative =
                [(0, 1), (1, 1), (1, 0)].map(|prev| self.luma_sample(block, local, prev));
            let mut n = self.luma_sample(block, local, (0, 0));
            if vertical {
                let mut top = speculative[0];
                if horizontal {
                    top = self.blend(speculative[1], top, wx);
                    n = self.blend(speculative[2], n, wx);
                }
                n = self.blend(top, n, wy);
            } else if horizontal {
                n = self.blend(speculative[2], n, wx);
            }
            let src = value as i32;
            let result = (src + round2(self.scale(0, src) * n, self.h.scaling_shift))
                .clamp(self.h.luma.0, self.h.luma.1);
            (result as u32) << self.shift
        }

        fn chroma_plane(&self, value: u32, avg: i32, n: i32, plane: [i32; 4], row: u32) -> u32 {
            if plane[3] == 0 {
                return value << self.shift;
            }
            let src = value as i32;
            let (row, index) = if self.h.csfl {
                (0, avg)
            } else {
                let index = ((avg * plane[1] + src * plane[0]) >> 6) + plane[2];
                (row, index.clamp(0, self.h.pixel_max))
            };
            let result = (src + round2(self.scale(row, index) * n, self.h.scaling_shift))
                .clamp(self.h.chroma.0, self.h.chroma.1);
            (result as u32) << self.shift
        }

        fn chroma(&self, pixel: (i32, i32), value: (u32, u32), avg: i32) -> (u32, u32) {
            if pixel.0 >= self.width >> 1 || (self.h.cb[3] == 0 && self.h.cr[3] == 0) {
                return (value.0 << self.shift, value.1 << self.shift);
            }
            let block = (pixel.0 >> 4, pixel.1 >> 4);
            let local = (pixel.0 & 15, pixel.1 & 15);
            let horizontal = self.h.overlap && block.0 > 0 && local.0 == 0;
            let vertical = self.h.overlap && block.1 > 0 && local.1 == 0;
            let w = (23, 22);
            let speculative =
                [(0, 1), (1, 1), (1, 0)].map(|prev| self.chroma_sample(block, local, prev));
            let blend = |old: (i32, i32), current: (i32, i32)| {
                (
                    self.blend(old.0, current.0, w),
                    self.blend(old.1, current.1, w),
                )
            };
            let mut n = self.chroma_sample(block, local, (0, 0));
            if vertical {
                let mut top = speculative[0];
                if horizontal {
                    top = blend(speculative[1], top);
                    n = blend(speculative[2], n);
                }
                n = blend(top, n);
            } else if horizontal {
                n = blend(speculative[2], n);
            }
            (
                self.chroma_plane(value.0, avg, n.0, self.h.cb, 1),
                self.chroma_plane(value.1, avg, n.1, self.h.cr, 2),
            )
        }
    }

    /// The fused shader for `bit_depth` over a tight P010 frame (`y`: height rows of width
    /// samples, `uv`: height/2 rows of width interleaved samples, each sample in the high
    /// `bit_depth` bits of its word), thread by thread like the dispatch.
    fn model_frame(
        buffer: &[u8],
        (width, height, bit_depth): (usize, usize, u32),
        y: &[u16],
        uv: &[u16],
    ) -> Vec<u16> {
        let b = Buffer(buffer);
        let h = header(&b);
        let shift = 16 - bit_depth;
        let model = Model {
            b,
            h,
            width: width as i32,
            shift,
        };
        let mut out_y = y.to_vec();
        let mut out_uv = uv.to_vec();
        let luma_at =
            |x: usize, row: usize| u32::from(y.get(row * width + x).copied().unwrap_or(0) >> shift);
        for ty in 0..height / 2 {
            for tx in 0..width.div_ceil(4) {
                for k in 0..2 {
                    let cx = 2 * tx + k;
                    let at = |plane: usize| {
                        if cx < width / 2 {
                            u32::from(uv[ty * width + 2 * cx + plane] >> shift)
                        } else {
                            0
                        }
                    };
                    let avg =
                        ((luma_at(2 * cx, 2 * ty) + luma_at(2 * cx + 1, 2 * ty) + 1) >> 1) as i32;
                    let (cb, cr) = model.chroma((cx as i32, ty as i32), (at(0), at(1)), avg);
                    if cx < width / 2 {
                        out_uv[ty * width + 2 * cx] = cb as u16;
                        out_uv[ty * width + 2 * cx + 1] = cr as u16;
                    }
                }
                for r in 0..2 {
                    let row = 2 * ty + r;
                    for k in 0..4 {
                        let x = 4 * tx + k;
                        let value = model.luma((x as i32, row as i32), luma_at(x, row));
                        if x < width {
                            out_y[row * width + x] = value as u16;
                        }
                    }
                }
            }
        }
        out_y.extend(out_uv);
        out_y
    }

    /// Random `bit_depth` samples in the high bits of P010 words.
    fn frame(width: usize, height: usize, seed: u32, bit_depth: u32) -> Vec<u16> {
        let mut state = seed.wrapping_mul(2_654_435_761).wrapping_add(1);
        (0..width * height * 3 / 2)
            .map(|_| {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                (((state >> 7) & ((1 << bit_depth) - 1)) as u16) << (16 - bit_depth)
            })
            .collect()
    }

    /// The CPU oracle at `params`' depth over the same P010 form ([`grain_p010`]).
    fn oracle(params: &ValidGrainParams, plain: &[u16]) -> Vec<u16> {
        let mut bytes: Vec<u8> = plain.iter().flat_map(|v| v.to_le_bytes()).collect();
        grain_p010(params, &mut bytes).unwrap();
        bytes
            .as_chunks::<2>()
            .0
            .iter()
            .map(|&pair| u16::from_le_bytes(pair))
            .collect()
    }

    /// Branch variants the even-size fixtures lack, each a valid parameter set at `bit_depth`.
    fn variants(width: u32, height: u32, bit_depth: u8) -> Vec<(&'static str, BaleGrainParams)> {
        let mut base = self_test_params(width, height);
        base.bit_depth = bit_depth;
        let mut out = vec![("self-test", base)];
        let mut p = base;
        p.overlap_flag = 0;
        out.push(("no overlap", p));
        let mut p = base;
        p.chroma_scaling_from_luma = 1;
        p.num_uv_points = [0; 2];
        out.push(("chroma from luma", p));
        let mut p = base;
        p.num_y_points = 0;
        p.ar_coeffs_y = [0; 24];
        p.ar_coeffs_uv[0][24] = 0;
        p.ar_coeffs_uv[1][24] = 0;
        out.push(("luma inactive", p));
        let mut p = base;
        p.matrix_identity = 1;
        out.push(("identity, restricted", p));
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
        let mut p = base;
        p.num_y_points = 0;
        p.num_uv_points = [0; 2];
        p.ar_coeffs_y = [0; 24];
        p.ar_coeffs_uv = [[0; 25]; 2];
        out.push(("no grain", p));
        out
    }

    #[test]
    fn layout_is_word_aligned_and_sized() {
        for at in [TEMPLATE_Y_AT, TEMPLATE_UV_AT, SCALING_AT, OFFSETS_AT] {
            assert_eq!(at % 4, 0);
        }
        assert_eq!(OFFSETS_AT, 21860);
        // Per depth: grain range, luma and chroma clip ranges, cb and cr words, pixel_max.
        let expected = [
            (10, (-512, 511), ((64, 940), (64, 960)), [-48, 468], 1023),
            (8, (-128, 127), ((16, 235), (16, 240)), [-12, 117], 255),
        ];
        let mut checked = 0;
        for (depth, grain, clip, offsets, pixel_max) in expected {
            for (width, height) in [(2, 2), (66, 18), (130, 40), (1920, 1080), (3840, 2160)] {
                let mut p = self_test_params(width, height);
                p.bit_depth = depth;
                let p = p.validate().unwrap();
                let buffer = build(&p).unwrap();
                assert_eq!(buffer.len(), bytes(width as usize, height as usize));
                assert_eq!(buffer.len() % 4, 0);
                let b = Buffer(&buffer);
                let h = header(&b);
                assert_eq!((b.load(0), b.load(4)), (width, height));
                assert_eq!(h.cols, width.div_ceil(32));
                assert_eq!(h.grain, grain);
                assert_eq!((h.luma, h.chroma), clip);
                assert_eq!(h.cb, [-43, 81, offsets[0], 1]);
                assert_eq!(h.cr, [31, -27, offsets[1], 1]);
                assert_eq!(h.pixel_max, pixel_max);
                assert_eq!(
                    (h.template_y, h.template_uv, h.scaling, h.offsets),
                    (
                        TEMPLATE_Y_AT as u32,
                        TEMPLATE_UV_AT as u32,
                        SCALING_AT as u32,
                        OFFSETS_AT as u32
                    )
                );
                assert!((26..32).all(|i| b.load(4 * i) == 0));
                // An 8-bit LUT fills the first 256 entries of each row; the rest stays zero.
                let used = 1usize << depth;
                for row in 0..3 {
                    let at = SCALING_AT + row * SCALING_WIDTH;
                    assert!(
                        buffer[at + used..at + SCALING_WIDTH]
                            .iter()
                            .all(|&v| v == 0)
                    );
                    assert!(buffer[at..at + used].iter().any(|&v| v != 0));
                }
                checked += 1;
            }
        }
        assert_eq!(checked, 2 * 5);
        let mut p = self_test_params(64, 64);
        p.bit_depth = 12;
        assert!(p.validate().is_err(), "12-bit params never reach build");
    }

    #[test]
    fn shader_model_matches_cpu_oracle() {
        let mut compared = [0; 2];
        for (count, depth) in compared.iter_mut().zip([10u8, 8]) {
            for (width, height) in [(130, 40), (66, 18), (128, 96), (130, 98), (34, 66), (2, 2)] {
                for (name, params) in variants(width, height, depth) {
                    for seed in [0, 7, 45231, 0xffff_fffe] {
                        let mut params = params;
                        params.seed = seed;
                        let params = params.validate().unwrap();
                        let plain =
                            frame(width as usize, height as usize, seed ^ width, depth.into());
                        let buffer = build(&params).unwrap();
                        let (y, uv) = plain.split_at((width * height) as usize);
                        let size = (width as usize, height as usize, u32::from(depth));
                        let actual = model_frame(&buffer, size, y, uv);
                        assert!(
                            actual == oracle(&params, &plain),
                            "{depth}-bit {name} at {width}x{height}, seed {seed}"
                        );
                        if name == "no grain" {
                            assert_eq!(actual, plain);
                        }
                        *count += 1;
                    }
                }
            }
        }
        assert_eq!(compared, [6 * 9 * 4; 2]);
    }
}
