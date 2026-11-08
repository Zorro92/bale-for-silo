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

//! Ports of generate_grain_y_c / generate_grain_uv_c (4:2:0 only).

use super::gaussian::GAUSSIAN_SEQUENCE;
use super::lfsr::{Lfsr, round2};
use super::params::ValidGrainParams;

pub(super) const GRAIN_WIDTH: usize = 82;
pub(super) const GRAIN_HEIGHT: usize = 73;
pub(super) const SUB_GRAIN_WIDTH: usize = 44;
pub(super) const SUB_GRAIN_HEIGHT: usize = 38;
pub(super) type LumaTemplate = [[i16; GRAIN_WIDTH]; GRAIN_HEIGHT];
pub(super) type ChromaTemplate = [[i16; SUB_GRAIN_WIDTH]; SUB_GRAIN_HEIGHT];

pub(super) struct Templates {
    pub(super) y: LumaTemplate,
    pub(super) uv: [Option<ChromaTemplate>; 2],
}

impl Templates {
    pub(super) fn generate(params: &ValidGrainParams) -> Self {
        // prep_grain always generates luma, even if the output luma passes through.
        let y = generate_grain_y(params);
        let uv = std::array::from_fn(|plane| {
            params
                .chroma_active(plane)
                .then(|| generate_grain_uv(params, &y, plane))
        });
        Self { y, uv }
    }
}

fn generate_grain_y(params: &ValidGrainParams) -> LumaTemplate {
    let data = params.as_raw();
    let mut seed = Lfsr::new(data.seed as u16);
    let shift = 4 - params.bitdepth_min_8() + u32::from(data.grain_scale_shift);
    let grain_ctr = 128 << params.bitdepth_min_8();
    let mut buf = [[0; GRAIN_WIDTH]; GRAIN_HEIGHT];
    for row in &mut buf {
        for pixel in row {
            let value = usize::from(seed.next(11));
            *pixel = round2(i32::from(GAUSSIAN_SEQUENCE[value]), shift) as i16;
        }
    }

    let ar_pad = 3;
    let ar_lag = isize::from(data.ar_coeff_lag);
    for y in ar_pad..GRAIN_HEIGHT {
        for x in ar_pad..GRAIN_WIDTH - ar_pad {
            let mut coeff = data.ar_coeffs_y.iter();
            let mut sum = 0;
            for dy in -ar_lag..=0 {
                for dx in -ar_lag..=ar_lag {
                    if dx == 0 && dy == 0 {
                        break;
                    }
                    sum += i32::from(*coeff.next().unwrap())
                        * i32::from(buf[y.wrapping_add_signed(dy)][x.wrapping_add_signed(dx)]);
                }
            }
            let grain = i32::from(buf[y][x]) + round2(sum, u32::from(data.ar_coeff_shift));
            buf[y][x] = grain.clamp(-grain_ctr, grain_ctr - 1) as i16;
        }
    }
    buf
}

fn generate_grain_uv(
    params: &ValidGrainParams,
    buf_y: &LumaTemplate,
    plane: usize,
) -> ChromaTemplate {
    let data = params.as_raw();
    let mut seed = Lfsr::new(data.seed as u16 ^ if plane == 0 { 0xb524 } else { 0x49d8 });
    let shift = 4 - params.bitdepth_min_8() + u32::from(data.grain_scale_shift);
    let grain_ctr = 128 << params.bitdepth_min_8();
    let mut buf = [[0; SUB_GRAIN_WIDTH]; SUB_GRAIN_HEIGHT];
    for row in &mut buf {
        for pixel in row {
            let value = usize::from(seed.next(11));
            *pixel = round2(i32::from(GAUSSIAN_SEQUENCE[value]), shift) as i16;
        }
    }

    let ar_pad = 3;
    let ar_lag = isize::from(data.ar_coeff_lag);
    for y in ar_pad..SUB_GRAIN_HEIGHT {
        for x in ar_pad..SUB_GRAIN_WIDTH - ar_pad {
            let mut coeff = data.ar_coeffs_uv[plane].iter();
            let mut sum = 0;
            for dy in -ar_lag..=0 {
                for dx in -ar_lag..=ar_lag {
                    if dx == 0 && dy == 0 {
                        if data.num_y_points == 0 {
                            break;
                        }
                        let luma_x = ((x - ar_pad) << 1) + ar_pad;
                        let luma_y = ((y - ar_pad) << 1) + ar_pad;
                        let mut luma = 0;
                        for i in 0..=1 {
                            for j in 0..=1 {
                                luma += i32::from(buf_y[luma_y + i][luma_x + j]);
                            }
                        }
                        luma = round2(luma, 2);
                        sum += luma * i32::from(*coeff.next().unwrap());
                        break;
                    }
                    sum += i32::from(*coeff.next().unwrap())
                        * i32::from(buf[y.wrapping_add_signed(dy)][x.wrapping_add_signed(dx)]);
                }
            }
            let grain = i32::from(buf[y][x]) + round2(sum, u32::from(data.ar_coeff_shift));
            buf[y][x] = grain.clamp(-grain_ctr, grain_ctr - 1) as i16;
        }
    }
    buf
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::grain::params::test_params;

    #[test]
    fn plane_presence_matches_prep_grain() {
        let mut p = test_params();
        p.num_y_points = 0;
        p.num_uv_points = [0, 2];
        let templates = Templates::generate(&p.validate().unwrap());
        assert!(templates.uv[0].is_none());
        assert!(templates.uv[1].is_some());
        assert!(templates.y.iter().flatten().any(|&v| v != 0));
        p.chroma_scaling_from_luma = 1;
        assert!(
            Templates::generate(&p.validate().unwrap())
                .uv
                .iter()
                .all(Option::is_some)
        );
    }

    #[test]
    fn grain_limits_and_low_seed_bits() {
        for bd in [8, 10] {
            for lag in 0..=3 {
                let mut p = test_params();
                p.bit_depth = bd;
                p.ar_coeff_lag = lag;
                let n = 2 * usize::from(lag) * usize::from(lag + 1);
                p.ar_coeffs_y[..n].fill(-128);
                for c in &mut p.ar_coeffs_uv {
                    c[..n + 1].fill(127);
                }
                let a = Templates::generate(&p.validate().unwrap());
                let ctr = 128 << (bd - 8);
                assert!(a.y.iter().flatten().all(|&v| (-ctr..ctr).contains(&v)));
                for t in a.uv.iter().flatten() {
                    assert!(t.iter().flatten().all(|&v| (-ctr..ctr).contains(&v)));
                }
                p.seed |= 0xffff0000;
                let b = Templates::generate(&p.validate().unwrap());
                assert_eq!(a.y, b.y);
                assert_eq!(a.uv, b.uv);
            }
        }
    }
}
