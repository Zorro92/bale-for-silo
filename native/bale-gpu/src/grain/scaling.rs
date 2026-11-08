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

//! generate_scaling from dav1d 1.5.4 `fg_apply_tmpl.c`.

use super::params::ValidGrainParams;

pub(super) fn generate(params: &ValidGrainParams, plane: usize) -> Vec<u8> {
    let data = params.as_raw();
    let points = if plane == 0 {
        &data.y_points[..usize::from(data.num_y_points)]
    } else {
        &data.uv_points[plane - 1][..usize::from(data.num_uv_points[plane - 1])]
    };
    let shift_x = params.bitdepth_min_8();
    let scaling_size = 1 << data.bit_depth;
    let mut scaling = vec![0; scaling_size];
    if points.is_empty() {
        return scaling;
    }

    scaling[..usize::from(points[0][0]) << shift_x].fill(points[0][1]);
    for pair in points.windows(2) {
        let [bx, by] = pair[0].map(i32::from);
        let [ex, ey] = pair[1].map(i32::from);
        let dx = ex - bx;
        let dy = ey - by;
        let delta = dy * ((0x10000 + (dx >> 1)) / dx);
        let mut d = 0x8000;
        for x in 0..dx {
            scaling[((bx + x) << shift_x) as usize] = (by + (d >> 16)) as u8;
            d += delta;
        }
    }
    let last = points[points.len() - 1];
    scaling[usize::from(last[0]) << shift_x..].fill(last[1]);

    if shift_x != 0 {
        let pad = 1 << shift_x;
        let rnd = pad >> 1;
        for pair in points.windows(2) {
            let bx = usize::from(pair[0][0]) << shift_x;
            let ex = usize::from(pair[1][0]) << shift_x;
            let dx = ex - bx;
            for x in (0..dx).step_by(pad) {
                let range = i32::from(scaling[bx + x + pad]) - i32::from(scaling[bx + x]);
                let mut r = rnd as i32;
                for n in 1..pad {
                    r += range;
                    scaling[bx + x + n] = (i32::from(scaling[bx + x]) + (r >> shift_x)) as u8;
                }
            }
        }
    }
    scaling
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::grain::params::test_params;

    #[test]
    fn empty_single_and_endpoint_extension() {
        for bd in [8, 10] {
            let mut p = test_params();
            p.bit_depth = bd;
            p.num_y_points = 0;
            assert!(generate(&p.validate().unwrap(), 0).iter().all(|&v| v == 0));
            p.num_y_points = 1;
            p.y_points[0] = [73, 219];
            assert!(
                generate(&p.validate().unwrap(), 0)
                    .iter()
                    .all(|&v| v == 219)
            );
            p.num_y_points = 2;
            p.y_points[1] = [193, 15];
            let lut = generate(&p.validate().unwrap(), 0);
            let shift = bd - 8;
            assert!(lut[..73 << shift].iter().all(|&v| v == 219));
            assert!(lut[193 << shift..].iter().all(|&v| v == 15));
        }
    }

    #[test]
    fn ten_bit_substeps_interpolate_rounded_eight_bit_anchors() {
        let mut p = test_params();
        p.bit_depth = 10;
        p.y_points[..2].copy_from_slice(&[[10, 20], [12, 15]]);
        let lut = generate(&p.validate().unwrap(), 0);
        // Anchors are 20, 18, 15, not a single direct high-bit-depth interpolation.
        assert_eq!(&lut[40..=48], &[20, 20, 19, 19, 18, 17, 17, 16, 15]);
        p.y_points[..2].copy_from_slice(&[[10, 15], [12, 20]]);
        assert_eq!(
            &generate(&p.validate().unwrap(), 0)[40..=48],
            &[15, 16, 17, 17, 18, 19, 19, 20, 20]
        );
    }
}
