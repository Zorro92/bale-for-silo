//! Picture layout checks, shader constants and a CPU reference for the zero-copy repack
//! ([`crate::d3d12_repack`], zero-copy ABI 1). Platform-independent so every CI runner tests them.

use crate::grain::ValidGrainParams;
use crate::grain::cpu::{self, ApplyError, PixelFormat};

/// Largest buffer the repack reads: a raw D3D12 view holds at most 2^27 32-bit elements, and the
/// shader's byte addresses are 32-bit.
pub const MAX_BYTES: u64 = 4 << 27;
/// Largest picture side (D3D12_REQ_TEXTURE2D_U_OR_V_DIMENSION).
pub const MAX_SIDE: u32 = 16384;

/// Bytes per sample of a picture of `bit_depth` (8 or 10).
fn sample_bytes(bit_depth: u32) -> Option<u64> {
    match bit_depth {
        8 => Some(1),
        10 => Some(2),
        _ => None,
    }
}

/// One 4:2:0 dav1d picture inside a zero-copy buffer. 10-bit (the default): little-endian 16-bit
/// samples with the value in the low 10 bits (the top 6 bits are ignored). 8-bit (only through
/// `bale_zc_repack_into_bits`): one byte per sample. `offsets` are the Y, U and V plane starts in
/// bytes from the start of the buffer; U and V share `chroma_stride`; strides are in bytes.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct BaleZcFrame {
    pub width: u32,
    pub height: u32,
    pub luma_stride: u32,
    pub chroma_stride: u32,
    pub offsets: [u64; 3],
}

impl BaleZcFrame {
    /// True when the shader handles this 10-bit layout and every byte it reads lies inside a
    /// buffer of `bytes` bytes: [`fits_bits`](Self::fits_bits) at bit depth 10.
    pub fn fits(&self, bytes: u64) -> bool {
        self.fits_bits(bytes, 10)
    }

    /// True when the shader for `bit_depth` (8 or 10; anything else never fits) handles this
    /// layout and every byte it reads lies inside a buffer of `bytes` bytes. Each thread reads
    /// whole words: at 10 bits a 4-sample luma group (8 bytes) and a 2-sample chroma group
    /// (4 bytes); at 8 bits a 4-sample luma group (4 bytes) and the word holding its 2-sample
    /// chroma pair, which it shares with the neighbouring thread (so a chroma row is read in
    /// whole words up to 4 * ceil(groups / 2) bytes). The last group of a row may therefore reach
    /// past `width` into the row padding (or the next row).
    pub fn fits_bits(&self, bytes: u64, bit_depth: u32) -> bool {
        let Some(sample) = sample_bytes(bit_depth) else {
            return false;
        };
        let (w, h) = (u64::from(self.width), u64::from(self.height));
        let (luma, chroma) = (u64::from(self.luma_stride), u64::from(self.chroma_stride));
        if w == 0
            || h == 0
            || !w.is_multiple_of(2)
            || !h.is_multiple_of(2)
            || self.width > MAX_SIDE
            || self.height > MAX_SIDE
            || luma < sample * w
            || chroma < sample * w / 2
            || !luma.is_multiple_of(4)
            || !chroma.is_multiple_of(4)
            || self
                .offsets
                .iter()
                .any(|&offset| !offset.is_multiple_of(4) || offset > MAX_BYTES)
        {
            return false;
        }
        // No overflow: offsets <= 2^29, rows <= 2^14, strides < 2^32.
        let groups = w.div_ceil(4);
        let (luma_read, chroma_read) = match sample {
            2 => (groups * 8, groups * 4),
            _ => (groups * 4, groups.div_ceil(2) * 4),
        };
        let limit = bytes.min(MAX_BYTES);
        self.offsets[0] + (h - 1) * luma + luma_read <= limit
            && self.offsets[1..]
                .iter()
                .all(|&offset| offset + (h / 2 - 1) * chroma + chroma_read <= limit)
    }

    /// The shader's root constants: Y, U, V offsets, 0, luma stride, chroma stride, width, height.
    /// Only meaningful for a layout that [`fits_bits`](Self::fits_bits).
    pub fn constants(&self) -> [u32; 8] {
        [
            self.offsets[0] as u32,
            self.offsets[1] as u32,
            self.offsets[2] as u32,
            0,
            self.luma_stride,
            self.chroma_stride,
            self.width,
            self.height,
        ]
    }

    /// Thread groups for the shader's 8x8 groups, one thread per 4x2 luma block (both depths).
    pub fn groups(&self) -> [u32; 2] {
        [self.width.div_ceil(32), self.height.div_ceil(16)]
    }

    /// The P010 bytes the 10-bit shader must produce from `buffer`:
    /// [`reference_p010_bits`](Self::reference_p010_bits) at bit depth 10.
    ///
    /// # Panics
    /// If the frame does not [`fit`](Self::fits) in `buffer`.
    pub fn reference_p010(&self, buffer: &[u8]) -> Vec<u8> {
        self.reference_p010_bits(buffer, 10)
    }

    /// The P010 bytes the shader for `bit_depth` must produce from `buffer`, tightly packed:
    /// `height` luma rows then `height / 2` interleaved UV rows, `2 * width` bytes each. A 10-bit
    /// sample `v` (low 10 bits of its word) becomes `v << 6`, an 8-bit one `v << 8`.
    ///
    /// # Panics
    /// If the frame does not [`fit`](Self::fits_bits) in `buffer` at `bit_depth`.
    pub fn reference_p010_bits(&self, buffer: &[u8], bit_depth: u32) -> Vec<u8> {
        assert!(
            self.fits_bits(buffer.len() as u64, bit_depth),
            "frame does not fit"
        );
        let (w, h) = (self.width as usize, self.height as usize);
        let sample = |offset: u64, row: usize, stride: u32, x: usize| {
            let row = offset as usize + row * stride as usize;
            match bit_depth {
                8 => u16::from(buffer[row + x]) << 8,
                _ => {
                    (u16::from_le_bytes([buffer[row + 2 * x], buffer[row + 2 * x + 1]]) & 0x3ff)
                        << 6
                }
            }
        };
        let mut out = Vec::with_capacity(2 * w * (h + h / 2));
        for y in 0..h {
            for x in 0..w {
                out.extend_from_slice(
                    &sample(self.offsets[0], y, self.luma_stride, x).to_le_bytes(),
                );
            }
        }
        for y in 0..h / 2 {
            for x in 0..w / 2 {
                for plane in [1, 2] {
                    let value = sample(self.offsets[plane], y, self.chroma_stride, x);
                    out.extend_from_slice(&value.to_le_bytes());
                }
            }
        }
        out
    }

    /// A padded 10-bit layout: [`test_layout_bits`](Self::test_layout_bits) at bit depth 10.
    pub fn test_layout(width: u32, height: u32) -> (Self, u64) {
        Self::test_layout_bits(width, height, 10)
    }

    /// A padded layout for a `width` x `height` test picture of `bit_depth` (strides and plane
    /// gaps larger than needed) and the buffer size it needs.
    ///
    /// # Panics
    /// If `bit_depth` is not 8 or 10.
    pub fn test_layout_bits(width: u32, height: u32, bit_depth: u32) -> (Self, u64) {
        let sample = sample_bytes(bit_depth).expect("bit depth 8 or 10") as u32;
        let luma_stride = (sample * width).next_multiple_of(4) + 16;
        let chroma_stride = (sample * width / 2).next_multiple_of(4) + 8;
        let y = 256u64;
        let u = y + u64::from(luma_stride) * u64::from(height) + 64;
        let v = u + u64::from(chroma_stride) * u64::from(height / 2) + 32;
        let bytes = v + u64::from(chroma_stride) * u64::from(height / 2) + 64;
        let frame = Self {
            width,
            height,
            luma_stride,
            chroma_stride,
            offsets: [y, u, v],
        };
        (frame, bytes)
    }
}

/// Fills `buffer` for a 10-bit `frame`: [`fill_test_planes_bits`] at bit depth 10.
///
/// # Panics
/// If the frame does not [`fit`](BaleZcFrame::fits) in `buffer`.
pub fn fill_test_planes(frame: &BaleZcFrame, buffer: &mut [u8], seed: u32) {
    fill_test_planes_bits(frame, buffer, seed, 10);
}

/// Fills `buffer` with seeded garbage, then writes a sample of `bit_depth` for every pixel of
/// `frame` (10-bit: garbage in the top 6 bits of each word), so a correct repack must mask and
/// must not read the padding.
///
/// # Panics
/// If the frame does not [`fit`](BaleZcFrame::fits_bits) in `buffer` at `bit_depth`.
pub fn fill_test_planes_bits(frame: &BaleZcFrame, buffer: &mut [u8], seed: u32, bit_depth: u32) {
    assert!(
        frame.fits_bits(buffer.len() as u64, bit_depth),
        "frame does not fit"
    );
    let noise = |index: usize| ((index as u32 ^ seed).wrapping_mul(0x9E37_79B1) >> 16) as u16;
    for (index, pair) in buffer.as_chunks_mut::<2>().0.iter_mut().enumerate() {
        *pair = noise(index).to_le_bytes();
    }
    let mut put = |offset: u64, row: usize, stride: u32, x: usize, plane: usize| {
        let value = x * 37 + row * 19 + plane * 83 + (x * row) % 29;
        let row = offset as usize + row * stride as usize;
        if bit_depth == 8 {
            buffer[row + x] = value as u8;
        } else {
            let at = row + 2 * x;
            let word = (value & 0x3ff) as u16 | (noise(at / 2 + 7) & 0xfc00);
            buffer[at..at + 2].copy_from_slice(&word.to_le_bytes());
        }
    };
    let (w, h) = (frame.width as usize, frame.height as usize);
    for y in 0..h {
        for x in 0..w {
            put(frame.offsets[0], y, frame.luma_stride, x, 0);
        }
    }
    for y in 0..h / 2 {
        for x in 0..w / 2 {
            put(frame.offsets[1], y, frame.chroma_stride, x, 1);
            put(frame.offsets[2], y, frame.chroma_stride, x, 2);
        }
    }
}

/// The CPU oracle's film grain applied in place to a tight P010 `frame` as
/// [`BaleZcFrame::reference_p010_bits`] produces it at `params`' bit depth (`params`' size, even
/// width): 10-bit samples in the high 10 bits of each word, 8-bit ones in the high byte (the low
/// byte zero). The result keeps that form, so it is what a fused direct write must produce.
pub fn grain_p010(params: &ValidGrainParams, frame: &mut [u8]) -> Result<(), ApplyError> {
    let (w, h) = (params.width(), params.height());
    if !w.is_multiple_of(2) || frame.len() != 2 * w * (h + h.div_ceil(2)) {
        return Err(ApplyError::InvalidLayout);
    }
    if params.as_raw().bit_depth == 10 {
        let (y, uv) = frame.split_at_mut(2 * w * h);
        let pitch = (2 * w) as isize;
        return cpu::apply(params, PixelFormat::P010, y, pitch, uv, pitch, 1);
    }
    let mut nv12: Vec<u8> = frame
        .as_chunks::<2>()
        .0
        .iter()
        .map(|pair| pair[1])
        .collect();
    let (y, uv) = nv12.split_at_mut(w * h);
    cpu::apply(params, PixelFormat::Nv12, y, w as isize, uv, w as isize, 1)?;
    for (pair, value) in frame.as_chunks_mut::<2>().0.iter_mut().zip(&nv12) {
        *pair = (u16::from(*value) << 8).to_le_bytes();
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A tight 4K layout like dav1d's (strides already multiples of 64 bytes).
    fn uhd() -> (BaleZcFrame, u64) {
        let (w, h) = (3840u64, 2160u64);
        let frame = BaleZcFrame {
            width: w as u32,
            height: h as u32,
            luma_stride: 2 * w as u32,
            chroma_stride: w as u32,
            offsets: [0, 2 * w * h, 2 * w * h + w * h / 2],
        };
        (frame, 2 * w * h + w * h)
    }

    #[test]
    fn accepts_exact_and_padded_layouts() {
        let (frame, bytes) = uhd();
        assert!(frame.fits(bytes));
        assert!(!frame.fits(bytes - 1));
        for (w, h) in [(64, 64), (66, 18), (2, 2), (3840, 2160)] {
            let (frame, bytes) = BaleZcFrame::test_layout(w, h);
            assert!(frame.fits(bytes), "{w}x{h}");
        }
    }

    #[test]
    fn last_group_may_read_past_the_width_but_not_the_buffer() {
        // 66 samples: the last luma group reads samples 64..68 (8 bytes from 128) and the last
        // chroma group samples 32..34 (4 bytes from 64).
        let frame = BaleZcFrame {
            width: 66,
            height: 2,
            luma_stride: 132,
            chroma_stride: 68,
            offsets: [0, 272, 340],
        };
        assert!(frame.fits(340 + 68));
        assert!(!frame.fits(340 + 67));
        let luma_short = BaleZcFrame {
            offsets: [0, 1000, 1100],
            ..frame
        };
        // Luma row 1 reads up to 132 + 136 = 268, past its 264 bytes of samples: still inside.
        assert!(luma_short.fits(1168));
        // A stride shorter than one row of samples never fits.
        assert!(
            !BaleZcFrame {
                luma_stride: 128,
                ..luma_short
            }
            .fits(1168)
        );
    }

    #[test]
    fn rejects_bad_layouts() {
        let (good, bytes) = uhd();
        for mutate in [
            (|f: &mut BaleZcFrame| f.width = 0) as fn(&mut BaleZcFrame),
            |f| f.height = 0,
            |f| f.width = 3839,
            |f| f.height = 2159,
            |f| f.width = MAX_SIDE + 2,
            |f| f.luma_stride = 7676,
            |f| f.luma_stride = 7682,
            |f| f.chroma_stride = 3836,
            |f| f.chroma_stride = 3842,
            |f| f.offsets[0] = 2,
            |f| f.offsets[2] += 2,
            |f| f.offsets[1] = MAX_BYTES + 4,
            |f| f.offsets[2] = u64::MAX - 3,
        ] {
            let mut bad = good;
            mutate(&mut bad);
            assert!(!bad.fits(bytes), "{bad:?}");
        }
        // Nothing past the raw-view limit, whatever the buffer says.
        let far = BaleZcFrame {
            offsets: [MAX_BYTES - 16, 0, 0],
            ..BaleZcFrame::test_layout(4, 2).0
        };
        assert!(!far.fits(u64::MAX));
    }

    #[test]
    fn constants_and_groups_match_the_shader() {
        let (frame, _) = BaleZcFrame::test_layout(66, 18);
        let c = frame.constants();
        assert_eq!(
            c[..3],
            [256, frame.offsets[1] as u32, frame.offsets[2] as u32]
        );
        assert_eq!(c[3..], [0, 148, 76, 66, 18]);
        // 66 wide: 17 luma groups of 4 -> 3 thread groups of 8; 9 chroma rows -> 2 groups of 8.
        assert_eq!(frame.groups(), [3, 2]);
        assert_eq!(uhd().0.groups(), [120, 135]);
    }

    #[test]
    fn reference_masks_shifts_and_interleaves() {
        let (frame, bytes) = BaleZcFrame::test_layout(8, 4);
        let mut buffer = vec![0u8; bytes as usize];
        fill_test_planes(&frame, &mut buffer, 3);
        let word = |b: &[u8], at: usize| u16::from_le_bytes([b[at], b[at + 1]]);
        let p010 = frame.reference_p010(&buffer);
        assert_eq!(p010.len(), 16 * 6);
        let source = word(&buffer, 256 + frame.luma_stride as usize + 2 * 3);
        assert_eq!(word(&p010, 16 + 2 * 3), (source & 0x3ff) << 6);
        let (u, v) = (frame.offsets[1] as usize, frame.offsets[2] as usize);
        let row = frame.chroma_stride as usize;
        assert_eq!(
            word(&p010, 16 * 5 + 4),
            (word(&buffer, u + row + 2) & 0x3ff) << 6
        );
        assert_eq!(
            word(&p010, 16 * 5 + 6),
            (word(&buffer, v + row + 2) & 0x3ff) << 6
        );
        // The fill puts garbage in the top bits, which the reference must drop.
        assert!((0..4).any(|x| word(&buffer, 256 + 2 * x) & 0xfc00 != 0));
    }

    #[test]
    fn eight_bit_accepts_exact_and_padded_layouts() {
        let (w, h) = (3840u64, 2160u64);
        let tight = BaleZcFrame {
            width: w as u32,
            height: h as u32,
            luma_stride: w as u32,
            chroma_stride: w as u32 / 2,
            offsets: [0, w * h, w * h + w * h / 4],
        };
        let bytes = w * h + w * h / 2;
        assert!(tight.fits_bits(bytes, 8));
        assert!(!tight.fits_bits(bytes - 1, 8));
        // Half the strides of a 10-bit picture: never a valid 10-bit layout.
        assert!(!tight.fits(u64::MAX));
        for (w, h) in [(64, 64), (66, 18), (2, 2), (6, 2), (34, 66), (3840, 2160)] {
            let (frame, bytes) = BaleZcFrame::test_layout_bits(w, h, 8);
            assert!(frame.fits_bits(bytes, 8), "{w}x{h}");
            assert_eq!(
                BaleZcFrame::test_layout_bits(w, h, 10),
                BaleZcFrame::test_layout(w, h)
            );
        }
    }

    #[test]
    fn eight_bit_last_group_may_read_past_the_width_but_not_the_buffer() {
        // 66 samples: the last luma group reads bytes 64..68 and the last chroma thread the word
        // at 32..36 (samples 32 and 33 plus two padding bytes).
        let frame = BaleZcFrame {
            width: 66,
            height: 2,
            luma_stride: 68,
            chroma_stride: 36,
            offsets: [0, 136, 172],
        };
        assert!(frame.fits_bits(208, 8));
        assert!(!frame.fits_bits(207, 8));
        // 6 samples: two luma groups (8 bytes), one chroma word shared by both threads.
        let small = BaleZcFrame {
            width: 6,
            height: 2,
            luma_stride: 8,
            chroma_stride: 4,
            offsets: [0, 16, 20],
        };
        assert!(small.fits_bits(24, 8));
        assert!(!small.fits_bits(23, 8));
        // A V plane whose last word would pass the end, although its 3 samples fit.
        assert!(
            !BaleZcFrame {
                offsets: [0, 16, 24],
                ..small
            }
            .fits_bits(27, 8)
        );
    }

    #[test]
    fn eight_bit_rejects_bad_layouts_and_depths() {
        let (good, bytes) = BaleZcFrame::test_layout_bits(66, 18, 8);
        for depth in [0, 1, 9, 12, 16, u32::MAX] {
            assert!(!good.fits_bits(bytes, depth), "{depth}");
            assert!(
                !BaleZcFrame::test_layout(66, 18)
                    .0
                    .fits_bits(u64::MAX, depth)
            );
        }
        for mutate in [
            (|f: &mut BaleZcFrame| f.width = 65) as fn(&mut BaleZcFrame),
            |f| f.height = 17,
            |f| f.luma_stride = 64,
            |f| f.luma_stride = 82,
            |f| f.chroma_stride = 32,
            |f| f.chroma_stride = 42,
            |f| f.offsets[1] += 2,
            |f| f.offsets[1] = MAX_BYTES + 4,
        ] {
            let mut bad = good;
            mutate(&mut bad);
            assert!(!bad.fits_bits(bytes, 8), "{bad:?}");
        }
    }

    #[test]
    fn ten_bit_fits_is_fits_bits_at_ten() {
        let (uhd, bytes) = uhd();
        for (frame, bytes) in [
            (uhd, bytes),
            (uhd, bytes - 1),
            BaleZcFrame::test_layout(66, 18),
            BaleZcFrame::test_layout_bits(66, 18, 8),
        ] {
            assert_eq!(frame.fits(bytes), frame.fits_bits(bytes, 10));
        }
    }

    #[test]
    fn eight_bit_reference_shifts_bytes_into_the_high_byte() {
        let (frame, bytes) = BaleZcFrame::test_layout_bits(8, 4, 8);
        let mut buffer = vec![0u8; bytes as usize];
        fill_test_planes_bits(&frame, &mut buffer, 3, 8);
        let word = |b: &[u8], at: usize| u16::from_le_bytes([b[at], b[at + 1]]);
        let p010 = frame.reference_p010_bits(&buffer, 8);
        assert_eq!(p010.len(), 16 * 6);
        let source = buffer[256 + frame.luma_stride as usize + 3];
        assert_eq!(word(&p010, 16 + 2 * 3), u16::from(source) << 8);
        let (u, v) = (frame.offsets[1] as usize, frame.offsets[2] as usize);
        let row = frame.chroma_stride as usize;
        assert_eq!(word(&p010, 16 * 5 + 4), u16::from(buffer[u + row + 1]) << 8);
        assert_eq!(word(&p010, 16 * 5 + 6), u16::from(buffer[v + row + 1]) << 8);
        // Limited-range black and white: 16 -> 0x1000 (64 << 6), 235 -> 0xeb00 (940 << 6).
        buffer[256] = 16;
        buffer[257] = 235;
        let p010 = frame.reference_p010_bits(&buffer, 8);
        assert_eq!(word(&p010, 0), 0x1000);
        assert_eq!(word(&p010, 0), 64 << 6);
        assert_eq!(word(&p010, 2), 0xeb00);
        assert_eq!(word(&p010, 2) >> 6, 940);
    }

    #[test]
    fn grain_p010_matches_the_oracle_at_both_depths() {
        for depth in [8u8, 10] {
            let mut params = crate::grain::fused::self_test_params(16, 8);
            params.bit_depth = depth;
            let params = params.validate().expect("valid params");
            let (frame, bytes) = BaleZcFrame::test_layout_bits(16, 8, u32::from(depth));
            let mut buffer = vec![0u8; bytes as usize];
            fill_test_planes_bits(&frame, &mut buffer, 9, u32::from(depth));
            let plain = frame.reference_p010_bits(&buffer, u32::from(depth));
            let mut grained = plain.clone();
            grain_p010(&params, &mut grained).expect("grain applies");
            assert_ne!(grained, plain, "{depth}-bit");
            let low = (1u16 << (16 - depth)) - 1;
            assert!(
                grained
                    .as_chunks::<2>()
                    .0
                    .iter()
                    .all(|pair| u16::from_le_bytes(*pair) & low == 0),
                "{depth}-bit"
            );
            assert_eq!(
                grain_p010(&params, &mut grained[2..]),
                Err(ApplyError::InvalidLayout)
            );
        }
    }
}
