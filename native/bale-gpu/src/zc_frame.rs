//! Picture layout checks, shader constants and a CPU reference for the zero-copy repack
//! ([`crate::d3d12_repack`], zero-copy ABI 1). Platform-independent so every CI runner tests them.

/// Largest buffer the repack reads: a raw D3D12 view holds at most 2^27 32-bit elements, and the
/// shader's byte addresses are 32-bit.
pub const MAX_BYTES: u64 = 4 << 27;
/// Largest picture side (D3D12_REQ_TEXTURE2D_U_OR_V_DIMENSION).
pub const MAX_SIDE: u32 = 16384;

/// One 10-bit 4:2:0 dav1d picture inside a zero-copy buffer: little-endian 16-bit samples with the
/// value in the low 10 bits (the top 6 bits are ignored). `offsets` are the Y, U and V plane starts
/// in bytes from the start of the buffer; U and V share `chroma_stride`.
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
    /// True when the shader handles this layout and every byte it reads lies inside a buffer of
    /// `bytes` bytes. The shader reads whole 4-sample luma groups and 2-sample chroma groups, so the
    /// last group of a row may reach past `width` into the row padding (or the next row).
    pub fn fits(&self, bytes: u64) -> bool {
        let (w, h) = (u64::from(self.width), u64::from(self.height));
        let (luma, chroma) = (u64::from(self.luma_stride), u64::from(self.chroma_stride));
        if w == 0
            || h == 0
            || !w.is_multiple_of(2)
            || !h.is_multiple_of(2)
            || self.width > MAX_SIDE
            || self.height > MAX_SIDE
            || luma < 2 * w
            || chroma < w
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
        let limit = bytes.min(MAX_BYTES);
        self.offsets[0] + (h - 1) * luma + groups * 8 <= limit
            && self.offsets[1..]
                .iter()
                .all(|&offset| offset + (h / 2 - 1) * chroma + groups * 4 <= limit)
    }

    /// The shader's root constants: Y, U, V offsets, 0, luma stride, chroma stride, width, height.
    /// Only meaningful for a layout that [`fits`](Self::fits).
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

    /// Thread groups for the shader's 8x8 groups, one thread per 4x2 luma block.
    pub fn groups(&self) -> [u32; 2] {
        [self.width.div_ceil(32), self.height.div_ceil(16)]
    }

    /// The P010 bytes the shader must produce from `buffer`, tightly packed: `height` luma rows then
    /// `height / 2` interleaved UV rows, `2 * width` bytes each.
    ///
    /// # Panics
    /// If the frame does not [`fit`](Self::fits) in `buffer`.
    pub fn reference_p010(&self, buffer: &[u8]) -> Vec<u8> {
        assert!(self.fits(buffer.len() as u64), "frame does not fit");
        let (w, h) = (self.width as usize, self.height as usize);
        let sample = |offset: u64, row: usize, stride: u32, x: usize| {
            let at = offset as usize + row * stride as usize + 2 * x;
            (u16::from_le_bytes([buffer[at], buffer[at + 1]]) & 0x3ff) << 6
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

    /// A padded layout for a `width` x `height` test picture (strides and plane gaps larger than
    /// needed) and the buffer size it needs.
    pub fn test_layout(width: u32, height: u32) -> (Self, u64) {
        let luma_stride = (2 * width).next_multiple_of(4) + 16;
        let chroma_stride = width.next_multiple_of(4) + 8;
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

/// Fills `buffer` with seeded garbage, then writes a 10-bit sample (garbage in the top 6 bits) for
/// every pixel of `frame`, so a correct repack must mask and must not read the padding.
///
/// # Panics
/// If the frame does not [`fit`](BaleZcFrame::fits) in `buffer`.
pub fn fill_test_planes(frame: &BaleZcFrame, buffer: &mut [u8], seed: u32) {
    assert!(frame.fits(buffer.len() as u64), "frame does not fit");
    let noise = |index: usize| ((index as u32 ^ seed).wrapping_mul(0x9E37_79B1) >> 16) as u16;
    for (index, pair) in buffer.as_chunks_mut::<2>().0.iter_mut().enumerate() {
        *pair = noise(index).to_le_bytes();
    }
    let mut put = |offset: u64, row: usize, stride: u32, x: usize, plane: usize| {
        let at = offset as usize + row * stride as usize + 2 * x;
        let value = ((x * 37 + row * 19 + plane * 83 + (x * row) % 29) & 0x3ff) as u16;
        let word = value | (noise(at / 2 + 7) & 0xfc00);
        buffer[at..at + 2].copy_from_slice(&word.to_le_bytes());
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
}
