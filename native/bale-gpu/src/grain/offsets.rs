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

//! One rand8 per 32×32 luma block, shared by all three planes.

use super::lfsr::Lfsr;
use super::params::ValidGrainParams;

pub(super) const FG_BLOCK_SIZE: usize = 32;

pub(super) struct Offsets {
    cols: usize,
    values: Vec<u8>,
}

impl Offsets {
    pub(super) fn generate(params: &ValidGrainParams) -> Self {
        let cols = params.width().div_ceil(FG_BLOCK_SIZE);
        let rows = params.height().div_ceil(FG_BLOCK_SIZE);
        let mut values = Vec::with_capacity(cols * rows);
        for row in 0..rows {
            let mut seed = params.as_raw().seed as u16;
            seed ^= (((row * 37 + 178) & 0xff) << 8) as u16;
            seed ^= ((row * 173 + 105) & 0xff) as u16;
            let mut lfsr = Lfsr::new(seed);
            for _ in 0..cols {
                values.push(lfsr.next(8) as u8);
            }
        }
        Self { cols, values }
    }

    pub(super) fn get(&self, col: usize, row: usize) -> u8 {
        self.values[row * self.cols + col]
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::grain::params::test_params;

    #[test]
    fn grid_equals_dav1d_current_and_previous_row_draws_for_every_plane() {
        for seed in [0, 1, 45231, 65535, 0xffff1234] {
            for (width, height) in [(1, 1), (32, 32), (33, 33), (333, 197), (128, 8257)] {
                let mut p = test_params();
                p.seed = seed;
                p.width = width;
                p.height = height;
                let p = p.validate().unwrap();
                let grid = Offsets::generate(&p);
                for overlap in [false, true] {
                    // Independent literal model of fgy/fguv's per-row offset cache.
                    for sub in [0, 1] {
                        let pw = p.width().div_ceil(1 << sub);
                        for row_num in 0..p.height().div_ceil(32) {
                            let rows = 1 + usize::from(overlap && row_num > 0);
                            let mut seeds = [0u32; 2];
                            for (i, state) in seeds.iter_mut().enumerate().take(rows) {
                                *state = p.as_raw().seed & 0xffff;
                                *state ^= ((((row_num - i) * 37 + 178) & 0xff) as u32) << 8;
                                *state ^= (((row_num - i) * 173 + 105) & 0xff) as u32;
                            }
                            let mut offsets = [[0; 2]; 2];
                            for bx in (0..pw).step_by(32 >> sub) {
                                if overlap && bx != 0 {
                                    offsets[1] = offsets[0];
                                }
                                for i in 0..rows {
                                    let r = seeds[i];
                                    let bit = (r ^ (r >> 1) ^ (r >> 3) ^ (r >> 12)) & 1;
                                    seeds[i] = (r >> 1) | (bit << 15);
                                    offsets[0][i] = (seeds[i] >> 8) & 255;
                                    let col = bx / (32 >> sub);
                                    assert_eq!(
                                        u32::from(grid.get(col, row_num - i)),
                                        offsets[0][i]
                                    );
                                    if overlap && bx != 0 {
                                        assert_eq!(
                                            u32::from(grid.get(col - 1, row_num - i)),
                                            offsets[1][i]
                                        );
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
