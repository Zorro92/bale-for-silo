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

//! Integer primitives from dav1d 1.5.4 `filmgrain_tmpl.c`.

pub(super) struct Lfsr(u16);

impl Lfsr {
    pub(super) fn new(seed: u16) -> Self {
        Self(seed)
    }

    pub(super) fn next(&mut self, bits: u32) -> u16 {
        let r = self.0;
        let bit = (r ^ (r >> 1) ^ (r >> 3) ^ (r >> 12)) & 1;
        self.0 = (r >> 1) | (bit << 15);
        (self.0 >> (16 - bits)) & ((1 << bits) - 1)
    }
}

/// dav1d's round2, including ties toward positive infinity and shift zero.
/// Rust's signed right shift is arithmetic, also for negative intermediate sums.
pub(super) fn round2(value: i32, shift: u32) -> i32 {
    (value + ((1 << shift) >> 1)) >> shift
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn known_draws_and_zero_seed() {
        let mut lfsr = Lfsr::new(1);
        let draws: Vec<_> = (0..9).map(|_| lfsr.next(8)).collect();
        assert_eq!(draws, [128, 64, 32, 16, 136, 68, 34, 17, 136]);
        assert_eq!(Lfsr::new(0).next(11), 0);
        let mut wide = Lfsr::new(45231);
        let mut narrow = Lfsr::new(45231);
        for _ in 0..1000 {
            assert_eq!(wide.next(11) >> 3, narrow.next(8));
        }
    }

    #[test]
    fn signed_rounding_is_not_symmetric() {
        assert_eq!(round2(-3, 1), -1);
        assert_eq!(round2(-1, 1), 0);
        assert_eq!(round2(-4, 1), -2);
        assert_eq!(round2(3, 1), 2);
        assert_eq!(round2(-17, 0), -17);
    }
}
