/*
 * Copyright © 2018, Niklas Haas
 * Copyright © 2018, VideoLAN and dav1d authors
 * Copyright © 2018, Two Orioles, LLC
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *
 * 1. Redistributions of source code must retain the above copyright notice, this
 *    list of conditions and the following disclaimer.
 *
 * 2. Redistributions in binary form must reproduce the above copyright notice,
 *    this list of conditions and the following disclaimer in the documentation
 *    and/or other materials provided with the distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
 * ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
 * ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

//! The grain ABI is independent of both dav1d's structs and the existing GPU ABI.

/// An untrusted C-ABI parameter record. See `docs/gpu-film-grain.md` for the layout.
/// Unused coefficient slots and `reserved0` must be zero. Unused points are ignored.
#[repr(C)]
#[derive(Clone, Copy, PartialEq, Eq)]
pub struct BaleGrainParams {
    pub struct_size: u32,
    pub seed: u32,
    pub width: u32,
    pub height: u32,
    pub bit_depth: u8,
    pub matrix_identity: u8,
    pub num_y_points: u8,
    pub chroma_scaling_from_luma: u8,
    pub num_uv_points: [u8; 2],
    pub scaling_shift: u8,
    pub ar_coeff_lag: u8,
    pub ar_coeff_shift: u8,
    pub grain_scale_shift: u8,
    pub overlap_flag: u8,
    pub clip_to_restricted_range: u8,
    pub y_points: [[u8; 2]; 14],
    pub uv_points: [[[u8; 2]; 10]; 2],
    pub ar_coeffs_y: [i8; 24],
    pub ar_coeffs_uv: [[i8; 25]; 2],
    pub reserved0: [u8; 2],
    pub uv_mult: [i16; 2],
    pub uv_luma_mult: [i16; 2],
    pub uv_offset: [i16; 2],
}

pub const PARAMS_SIZE: u32 = 184;

/// A fixed diagnostic, deliberately without parameter contents.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct InvalidGrainParams;

impl std::fmt::Display for InvalidGrainParams {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("invalid AV1 film grain parameters")
    }
}

impl std::error::Error for InvalidGrainParams {}

/// An immutable, owned snapshot; it cannot be constructed without validation.
/// Deliberately not `Debug`: grain parameter contents must not enter logs.
#[derive(Clone, PartialEq, Eq)]
pub struct ValidGrainParams(BaleGrainParams);

impl ValidGrainParams {
    pub fn as_raw(&self) -> &BaleGrainParams {
        &self.0
    }

    pub fn width(&self) -> usize {
        self.0.width as usize
    }

    pub fn height(&self) -> usize {
        self.0.height as usize
    }

    pub(crate) fn bitdepth_min_8(&self) -> u32 {
        u32::from(self.0.bit_depth - 8)
    }

    pub(crate) fn pixel_max(&self) -> i32 {
        (1 << self.0.bit_depth) - 1
    }

    pub(crate) fn chroma_active(&self, plane: usize) -> bool {
        self.0.num_uv_points[plane] != 0 || self.0.chroma_scaling_from_luma != 0
    }
}

impl BaleGrainParams {
    pub fn validate(&self) -> Result<ValidGrainParams, InvalidGrainParams> {
        if self.struct_size != PARAMS_SIZE
            || !(1..=65536).contains(&self.width)
            || !(1..=65536).contains(&self.height)
            || !matches!(self.bit_depth, 8 | 10)
            || self.num_y_points > 14
            || self.num_uv_points.iter().any(|&n| n > 10)
            || !(8..=11).contains(&self.scaling_shift)
            || self.ar_coeff_lag > 3
            || !(6..=9).contains(&self.ar_coeff_shift)
            || self.grain_scale_shift > 3
            || [
                self.matrix_identity,
                self.chroma_scaling_from_luma,
                self.overlap_flag,
                self.clip_to_restricted_range,
            ]
            .iter()
            .any(|&flag| flag > 1)
            || self.reserved0 != [0; 2]
            || self
                .uv_mult
                .iter()
                .chain(&self.uv_luma_mult)
                .any(|v| !(-128..=127).contains(v))
            || self.uv_offset.iter().any(|v| !(-256..=255).contains(v))
        {
            return Err(InvalidGrainParams);
        }

        let increasing = |points: &[[u8; 2]]| points.windows(2).all(|pair| pair[0][0] < pair[1][0]);
        if !increasing(&self.y_points[..usize::from(self.num_y_points)]) {
            return Err(InvalidGrainParams);
        }
        let lag = usize::from(self.ar_coeff_lag);
        let count = 2 * lag * (lag + 1);
        let y_count = if self.num_y_points != 0 { count } else { 0 };
        if self.ar_coeffs_y[y_count..].iter().any(|&v| v != 0) {
            return Err(InvalidGrainParams);
        }
        for plane in 0..2 {
            if !increasing(&self.uv_points[plane][..usize::from(self.num_uv_points[plane])]) {
                return Err(InvalidGrainParams);
            }
            let active = self.num_uv_points[plane] != 0 || self.chroma_scaling_from_luma != 0;
            let uv_count = if active {
                count + usize::from(self.num_y_points != 0)
            } else {
                0
            };
            if self.ar_coeffs_uv[plane][uv_count..].iter().any(|&v| v != 0) {
                return Err(InvalidGrainParams);
            }
        }
        Ok(ValidGrainParams(*self))
    }
}

#[cfg(test)]
pub(crate) fn test_params() -> BaleGrainParams {
    BaleGrainParams {
        struct_size: PARAMS_SIZE,
        seed: 12345,
        width: 65,
        height: 67,
        bit_depth: 8,
        matrix_identity: 0,
        num_y_points: 2,
        chroma_scaling_from_luma: 0,
        num_uv_points: [2; 2],
        scaling_shift: 8,
        ar_coeff_lag: 0,
        ar_coeff_shift: 6,
        grain_scale_shift: 0,
        overlap_flag: 1,
        clip_to_restricted_range: 0,
        y_points: std::array::from_fn(|i| if i == 1 { [255, 255] } else { [0, 255] }),
        uv_points: [std::array::from_fn(|i| if i == 1 { [255, 255] } else { [0, 255] }); 2],
        ar_coeffs_y: [0; 24],
        ar_coeffs_uv: [[0; 25]; 2],
        reserved0: [0; 2],
        uv_mult: [0; 2],
        uv_luma_mult: [64; 2],
        uv_offset: [0; 2],
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::mem::{align_of, offset_of, size_of};

    #[test]
    fn abi_size_and_every_field_offset() {
        assert_eq!(size_of::<BaleGrainParams>(), 184);
        assert_eq!(align_of::<BaleGrainParams>(), 4);
        macro_rules! offset {
            ($field:ident, $offset:expr) => {
                assert_eq!(offset_of!(BaleGrainParams, $field), $offset);
            };
        }
        offset!(struct_size, 0);
        offset!(seed, 4);
        offset!(width, 8);
        offset!(height, 12);
        offset!(bit_depth, 16);
        offset!(matrix_identity, 17);
        offset!(num_y_points, 18);
        offset!(chroma_scaling_from_luma, 19);
        offset!(num_uv_points, 20);
        offset!(scaling_shift, 22);
        offset!(ar_coeff_lag, 23);
        offset!(ar_coeff_shift, 24);
        offset!(grain_scale_shift, 25);
        offset!(overlap_flag, 26);
        offset!(clip_to_restricted_range, 27);
        offset!(y_points, 28);
        offset!(uv_points, 56);
        offset!(ar_coeffs_y, 96);
        offset!(ar_coeffs_uv, 120);
        offset!(reserved0, 170);
        offset!(uv_mult, 172);
        offset!(uv_luma_mult, 176);
        offset!(uv_offset, 180);
    }

    #[test]
    fn rejects_invalid_ranges() {
        let bad: &[fn(&mut BaleGrainParams)] = &[
            |p| p.struct_size = 183,
            |p| p.struct_size = 185,
            |p| p.width = 0,
            |p| p.height = 0,
            |p| p.width = 65537,
            |p| p.height = 65537,
            |p| p.bit_depth = 12,
            |p| p.matrix_identity = 2,
            |p| p.num_y_points = 15,
            |p| p.num_uv_points[0] = 11,
            |p| p.num_uv_points[1] = 11,
            |p| p.chroma_scaling_from_luma = 2,
            |p| p.scaling_shift = 7,
            |p| p.scaling_shift = 12,
            |p| p.ar_coeff_lag = 4,
            |p| p.ar_coeff_shift = 5,
            |p| p.ar_coeff_shift = 10,
            |p| p.grain_scale_shift = 4,
            |p| p.overlap_flag = 2,
            |p| p.clip_to_restricted_range = 2,
            |p| p.reserved0[0] = 1,
            |p| p.reserved0[1] = 1,
            |p| p.uv_mult[0] = -129,
            |p| p.uv_mult[1] = 128,
            |p| p.uv_luma_mult[0] = -129,
            |p| p.uv_luma_mult[1] = 128,
            |p| p.uv_offset[0] = -257,
            |p| p.uv_offset[1] = 256,
        ];
        assert!(test_params().validate().is_ok());
        for (index, mutate) in bad.iter().enumerate() {
            let mut p = test_params();
            mutate(&mut p);
            assert!(p.validate().is_err(), "invalid range {index}");
        }
    }

    #[test]
    fn active_points_are_strictly_increasing() {
        for plane in 0..3 {
            for value in [0, 10] {
                let mut p = test_params();
                let points = if plane == 0 {
                    &mut p.y_points[..]
                } else {
                    &mut p.uv_points[plane - 1][..]
                };
                points[0][0] = 10;
                points[1][0] = value;
                assert!(p.validate().is_err());
            }
        }
    }

    #[test]
    fn coefficient_tails_match_lag_and_plane_presence() {
        for lag in 0..=3 {
            for has_y in [false, true] {
                let mut p = test_params();
                p.ar_coeff_lag = lag;
                p.num_y_points = if has_y { 2 } else { 0 };
                let count = 2 * usize::from(lag) * usize::from(lag + 1);
                let y_count = if has_y { count } else { 0 };
                let uv_count = count + usize::from(has_y);
                p.ar_coeffs_y[..y_count].fill(-128);
                for c in &mut p.ar_coeffs_uv {
                    c[..uv_count].fill(127);
                }
                assert!(p.validate().is_ok());
                if y_count < 24 {
                    let mut bad = p;
                    bad.ar_coeffs_y[y_count] = 1;
                    assert!(bad.validate().is_err());
                }
                if uv_count < 25 {
                    for plane in 0..2 {
                        let mut bad = p;
                        bad.ar_coeffs_uv[plane][uv_count] = 1;
                        assert!(bad.validate().is_err());
                    }
                }
                p.num_uv_points = [0; 2];
                p.ar_coeffs_uv = [[0; 25]; 2];
                assert!(p.validate().is_ok());
                p.ar_coeffs_uv[0][0] = 1;
                assert!(p.validate().is_err());
            }
        }
    }

    #[test]
    fn validated_snapshot_cannot_change_with_the_input() {
        let mut p = test_params();
        let valid = p.validate().unwrap();
        p.width = 0;
        assert_eq!(valid.width(), 65);
        assert!(p.validate().is_err());
    }
}
