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

//! In-place ports of fgy_32x32xn_c / fguv_32x32xn_c and apply_grain_row.
//! Integer operations, overlap order and clipping match dav1d 1.5.4. All memory
//! access is safe Rust, including unaligned P010 and negative byte pitches.

use std::cell::RefCell;

use super::lfsr::round2;
use super::offsets::{FG_BLOCK_SIZE, Offsets};
use super::params::ValidGrainParams;
use super::scaling;
use super::template::Templates;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u32)]
pub enum PixelFormat {
    Nv12 = 1,
    P010 = 2,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ApplyError {
    InvalidFormat,
    InvalidLayout,
    InvalidThreads,
}

impl std::fmt::Display for ApplyError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(match self {
            Self::InvalidFormat => "invalid grain pixel format",
            Self::InvalidLayout => "invalid grain plane layout",
            Self::InvalidThreads => "invalid grain thread count",
        })
    }
}

impl std::error::Error for ApplyError {}

impl TryFrom<u32> for PixelFormat {
    type Error = ApplyError;
    fn try_from(value: u32) -> Result<Self, Self::Error> {
        match value {
            1 => Ok(Self::Nv12),
            2 => Ok(Self::P010),
            _ => Err(ApplyError::InvalidFormat),
        }
    }
}

/// Checked byte-span geometry, also used before the FFI constructs any slices.
#[derive(Clone, Copy)]
pub(super) struct PlaneLayout {
    pitch: usize,
    rows: usize,
    row_bytes: usize,
    reversed: bool,
    pub(super) span: usize,
}

impl PlaneLayout {
    fn new(pitch: isize, rows: usize, row_bytes: usize) -> Result<Self, ApplyError> {
        let magnitude = pitch.unsigned_abs();
        let span = rows
            .checked_sub(1)
            .and_then(|n| n.checked_mul(magnitude))
            .and_then(|n| n.checked_add(row_bytes));
        let Some(span) = span.filter(|&n| n <= isize::MAX as usize) else {
            return Err(ApplyError::InvalidLayout);
        };
        if magnitude < row_bytes {
            return Err(ApplyError::InvalidLayout);
        }
        Ok(Self {
            pitch: magnitude,
            rows,
            row_bytes,
            reversed: pitch < 0,
            span,
        })
    }

    pub(super) fn first_offset(self) -> usize {
        self.row_start(0)
    }

    fn row_start(self, row: usize) -> usize {
        let physical_row = if self.reversed {
            self.rows - 1 - row
        } else {
            row
        };
        physical_row * self.pitch
    }
}

pub(super) fn layouts(
    params: &ValidGrainParams,
    format: PixelFormat,
    y_pitch: isize,
    uv_pitch: isize,
) -> Result<[PlaneLayout; 2], ApplyError> {
    let bytes = match (format, params.as_raw().bit_depth) {
        (PixelFormat::Nv12, 8) => 1,
        (PixelFormat::P010, 10) => 2,
        _ => return Err(ApplyError::InvalidFormat),
    };
    Ok([
        PlaneLayout::new(y_pitch, params.height(), params.width() * bytes)?,
        PlaneLayout::new(
            uv_pitch,
            params.height().div_ceil(2),
            params.width().div_ceil(2) * 2 * bytes,
        )?,
    ])
}

struct PlaneMut<'a> {
    data: &'a mut [u8],
    layout: PlaneLayout,
}

impl PlaneMut<'_> {
    fn row(&self, row: usize) -> &[u8] {
        let start = self.layout.row_start(row);
        &self.data[start..start + self.layout.row_bytes]
    }

    fn row_mut(&mut self, row: usize) -> &mut [u8] {
        let start = self.layout.row_start(row);
        &mut self.data[start..start + self.layout.row_bytes]
    }

    /// Partition by logical rows without aliasing, independently for each pitch's sign.
    fn chunks(self, rows_per_chunk: usize) -> Vec<Self> {
        let mut rest = self.data;
        let mut rows = self.layout.rows;
        let mut chunks = Vec::new();
        while rows != 0 {
            let n = rows.min(rows_per_chunk);
            let split = if self.layout.reversed {
                (rows - n) * self.layout.pitch
            } else {
                (n * self.layout.pitch).min(rest.len())
            };
            let (lo, hi) = rest.split_at_mut(split);
            let data;
            if self.layout.reversed {
                data = hi;
                rest = lo;
            } else {
                data = lo;
                rest = hi;
            }
            chunks.push(Self {
                data,
                layout: PlaneLayout {
                    rows: n,
                    ..self.layout
                },
            });
            rows -= n;
        }
        chunks
    }
}

struct Prepared {
    params: ValidGrainParams,
    templates: Templates,
    scaling: [Vec<u8>; 3],
    offsets: Offsets,
}

impl Prepared {
    fn new(params: &ValidGrainParams) -> Self {
        Self {
            params: params.clone(),
            templates: Templates::generate(params),
            scaling: std::array::from_fn(|plane| scaling::generate(params, plane)),
            offsets: Offsets::generate(params),
        }
    }
}

thread_local! {
    // One bounded entry per decode/calling thread; full equality includes every parameter and seed.
    static CACHE: RefCell<Option<Prepared>> = const { RefCell::new(None) };
}

/// Apply grain in place. Slices begin at the lowest addressed byte of each plane;
/// a negative pitch places logical row zero at the high end of the slice.
/// Only visible samples are written, never pitch padding or trailing buffer bytes.
/// `threads` is 0 (auto, capped at 4), or 1..=4. Errors leave both planes untouched.
pub fn apply(
    params: &ValidGrainParams,
    format: PixelFormat,
    y: &mut [u8],
    y_pitch: isize,
    uv: &mut [u8],
    uv_pitch: isize,
    threads: u32,
) -> Result<(), ApplyError> {
    let [yl, uvl] = layouts(params, format, y_pitch, uv_pitch)?;
    if y.len() < yl.span || uv.len() < uvl.span {
        return Err(ApplyError::InvalidLayout);
    }
    if threads > 4 {
        return Err(ApplyError::InvalidThreads);
    }
    let y = PlaneMut {
        data: &mut y[..yl.span],
        layout: yl,
    };
    let uv = PlaneMut {
        data: &mut uv[..uvl.span],
        layout: uvl,
    };
    let workers = if threads == 0 {
        std::thread::available_parallelism().map_or(1, |n| n.get().min(4))
    } else {
        threads as usize
    };
    CACHE.with_borrow_mut(|cache| {
        if cache.as_ref().is_none_or(|entry| entry.params != *params) {
            *cache = Some(Prepared::new(params));
        }
        let prepared = cache.as_ref().unwrap();
        match format {
            PixelFormat::Nv12 => parallel_apply::<Nv12>(prepared, y, uv, workers),
            PixelFormat::P010 => parallel_apply::<P010>(prepared, y, uv, workers),
        }
    });
    Ok(())
}

trait Pixel {
    fn read(row: &[u8], x: usize) -> i32;
    fn write(row: &mut [u8], x: usize, value: i32);
}
struct Nv12;
struct P010;
impl Pixel for Nv12 {
    fn read(row: &[u8], x: usize) -> i32 {
        i32::from(row[x])
    }
    fn write(row: &mut [u8], x: usize, value: i32) {
        row[x] = value as u8;
    }
}
impl Pixel for P010 {
    fn read(row: &[u8], x: usize) -> i32 {
        i32::from(u16::from_le_bytes([row[2 * x], row[2 * x + 1]]) >> 6)
    }
    fn write(row: &mut [u8], x: usize, value: i32) {
        row[2 * x..2 * x + 2].copy_from_slice(&((value as u16) << 6).to_le_bytes());
    }
}

fn parallel_apply<P: Pixel>(
    prepared: &Prepared,
    y: PlaneMut<'_>,
    uv: PlaneMut<'_>,
    workers: usize,
) {
    let block_rows = prepared.params.height().div_ceil(FG_BLOCK_SIZE);
    if workers == 1 || block_rows == 1 {
        apply_rows::<P>(prepared, y, uv, 0);
        return;
    }
    let per_worker = block_rows.div_ceil(workers);
    let ys = y.chunks(per_worker * FG_BLOCK_SIZE);
    let uvs = uv.chunks(per_worker * (FG_BLOCK_SIZE / 2));
    std::thread::scope(|scope| {
        for (group, (y, uv)) in ys.into_iter().zip(uvs).enumerate() {
            scope.spawn(move || apply_rows::<P>(prepared, y, uv, group * per_worker));
        }
    });
}

fn apply_rows<P: Pixel>(
    prepared: &Prepared,
    mut y: PlaneMut<'_>,
    mut uv: PlaneMut<'_>,
    first_block_row: usize,
) {
    let params = &prepared.params;
    for local_row in (0..y.layout.rows).step_by(FG_BLOCK_SIZE) {
        let row_num = first_block_row + local_row / FG_BLOCK_SIZE;
        let bh = (y.layout.rows - local_row).min(FG_BLOCK_SIZE);
        // Chroma must read the original luma; block rows have no cross-row source dependencies.
        for plane in 0..2 {
            if params.chroma_active(plane) {
                apply_chroma::<P>(
                    prepared,
                    &y,
                    &mut uv,
                    local_row,
                    bh.div_ceil(2),
                    row_num,
                    plane,
                );
            }
        }
        if params.as_raw().num_y_points != 0 {
            apply_luma::<P>(prepared, &mut y, local_row, bh, row_num);
        }
    }
}

/// sample_lut, with the previous-column/row compensation in plane-sized blocks.
fn sample_lut<const W: usize, const H: usize>(
    lut: &[[i16; W]; H],
    offsets: &[[u8; 2]; 2],
    sub: usize,
    previous: (usize, usize),
    x: usize,
    y: usize,
) -> i32 {
    let (bx, by) = previous;
    let randval = usize::from(offsets[bx][by]);
    let offx = 3 + (2 >> sub) * (3 + (randval >> 4));
    let offy = 3 + (2 >> sub) * (3 + (randval & 15));
    i32::from(lut[offy + y + (FG_BLOCK_SIZE >> sub) * by][offx + x + (FG_BLOCK_SIZE >> sub) * bx])
}

fn block_offsets(prepared: &Prepared, col: usize, row: usize) -> [[u8; 2]; 2] {
    let mut offsets = [[0; 2]; 2];
    offsets[0][0] = prepared.offsets.get(col, row);
    if prepared.params.as_raw().overlap_flag != 0 {
        if col != 0 {
            offsets[1][0] = prepared.offsets.get(col - 1, row);
        }
        if row != 0 {
            offsets[0][1] = prepared.offsets.get(col, row - 1);
            if col != 0 {
                offsets[1][1] = prepared.offsets.get(col - 1, row - 1);
            }
        }
    }
    offsets
}

/// The four regions and the intermediate corner clips follow fgy/fguv literally.
/// Only the region's final store differs between Y and UV.
#[allow(clippy::too_many_arguments)]
fn grain_block<const W: usize, const H: usize>(
    params: &ValidGrainParams,
    lut: &[[i16; W]; H],
    offsets: &[[u8; 2]; 2],
    sub: usize,
    bw: usize,
    bh: usize,
    col: usize,
    row_num: usize,
    mut add_noise: impl FnMut(usize, usize, i32),
) {
    let overlap = params.as_raw().overlap_flag != 0;
    let ystart = if overlap && row_num != 0 {
        (2 >> sub).min(bh)
    } else {
        0
    };
    let xstart = if overlap && col != 0 {
        (2 >> sub).min(bw)
    } else {
        0
    };
    let weights = if sub == 0 {
        [[27, 17], [17, 27]]
    } else {
        [[23, 22], [0, 0]]
    };
    let ctr = 128 << params.bitdepth_min_8();
    let blend = |old: i32, grain: i32, offset: usize| {
        round2(old * weights[offset][0] + grain * weights[offset][1], 5).clamp(-ctr, ctr - 1)
    };
    let sample = |bx, by, x, y| sample_lut(lut, offsets, sub, (bx, by), x, y);

    for y in ystart..bh {
        for x in xstart..bw {
            add_noise(x, y, sample(0, 0, x, y));
        }
        for x in 0..xstart {
            let grain = sample(0, 0, x, y);
            let old = sample(1, 0, x, y);
            add_noise(x, y, blend(old, grain, x));
        }
    }
    for y in 0..ystart {
        for x in xstart..bw {
            let grain = sample(0, 0, x, y);
            let old = sample(0, 1, x, y);
            add_noise(x, y, blend(old, grain, y));
        }
        for x in 0..xstart {
            let top = sample(0, 1, x, y);
            let old = sample(1, 1, x, y);
            let top = blend(old, top, x);
            let grain = sample(0, 0, x, y);
            let old = sample(1, 0, x, y);
            let grain = blend(old, grain, x);
            add_noise(x, y, blend(top, grain, y));
        }
    }
}

fn clip_range(params: &ValidGrainParams, chroma: bool) -> (i32, i32) {
    if params.as_raw().clip_to_restricted_range != 0 {
        let max = if chroma && params.as_raw().matrix_identity == 0 {
            240
        } else {
            235
        };
        (
            16 << params.bitdepth_min_8(),
            max << params.bitdepth_min_8(),
        )
    } else {
        (0, params.pixel_max())
    }
}

fn apply_luma<P: Pixel>(
    prepared: &Prepared,
    dst: &mut PlaneMut<'_>,
    start: usize,
    bh: usize,
    row_num: usize,
) {
    let params = &prepared.params;
    let data = params.as_raw();
    let (min, max) = clip_range(params, false);
    for bx in (0..params.width()).step_by(FG_BLOCK_SIZE) {
        let col = bx / FG_BLOCK_SIZE;
        let bw = FG_BLOCK_SIZE.min(params.width() - bx);
        let offsets = block_offsets(prepared, col, row_num);
        grain_block(
            params,
            &prepared.templates.y,
            &offsets,
            0,
            bw,
            bh,
            col,
            row_num,
            |x, y, grain| {
                let row = dst.row_mut(start + y);
                let src = P::read(row, bx + x);
                let noise = round2(
                    i32::from(prepared.scaling[0][src as usize]) * grain,
                    u32::from(data.scaling_shift),
                );
                P::write(row, bx + x, (src + noise).clamp(min, max));
            },
        );
    }
}

#[allow(clippy::too_many_arguments)]
fn apply_chroma<P: Pixel>(
    prepared: &Prepared,
    luma: &PlaneMut<'_>,
    dst: &mut PlaneMut<'_>,
    start: usize,
    bh: usize,
    row_num: usize,
    plane: usize,
) {
    let params = &prepared.params;
    let data = params.as_raw();
    let (min, max) = clip_range(params, true);
    let pw = params.width().div_ceil(2);
    let lut = prepared.templates.uv[plane].as_ref().unwrap();
    let scaling = &prepared.scaling[if data.chroma_scaling_from_luma != 0 {
        0
    } else {
        plane + 1
    }];
    for bx in (0..pw).step_by(FG_BLOCK_SIZE / 2) {
        let col = bx / (FG_BLOCK_SIZE / 2);
        let bw = (FG_BLOCK_SIZE / 2).min(pw - bx);
        let offsets = block_offsets(prepared, col, row_num);
        grain_block(
            params,
            lut,
            &offsets,
            1,
            bw,
            bh,
            col,
            row_num,
            |x, y, grain| {
                let lx = (bx + x) << 1;
                let luma = luma.row(start + (y << 1));
                // dav1d extends the odd-width source's padding to its last visible sample.
                let avg =
                    (P::read(luma, lx) + P::read(luma, (lx + 1).min(params.width() - 1)) + 1) >> 1;
                let row = dst.row_mut((start >> 1) + y);
                let sample = (bx + x) * 2 + plane;
                let src = P::read(row, sample);
                let mut val = avg;
                if data.chroma_scaling_from_luma == 0 {
                    let combined = avg * i32::from(data.uv_luma_mult[plane])
                        + src * i32::from(data.uv_mult[plane]);
                    val = ((combined >> 6)
                        + (i32::from(data.uv_offset[plane]) * (1 << params.bitdepth_min_8())))
                    .clamp(0, params.pixel_max());
                }
                let noise = round2(
                    i32::from(scaling[val as usize]) * grain,
                    u32::from(data.scaling_shift),
                );
                P::write(row, sample, (src + noise).clamp(min, max));
            },
        );
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::grain::params::test_params;

    #[test]
    fn restricted_ranges_include_identity_chroma_exception() {
        for bd in [8, 10] {
            for identity in [0, 1] {
                let mut p = test_params();
                p.bit_depth = bd;
                p.matrix_identity = identity;
                p.clip_to_restricted_range = 1;
                let p = p.validate().unwrap();
                assert_eq!(clip_range(&p, false), (16 << (bd - 8), 235 << (bd - 8)));
                assert_eq!(
                    clip_range(&p, true),
                    (
                        16 << (bd - 8),
                        (if identity == 0 { 240 } else { 235 }) << (bd - 8)
                    )
                );
                let format = if bd == 8 {
                    PixelFormat::Nv12
                } else {
                    PixelFormat::P010
                };
                let bytes = if bd == 8 { 1 } else { 2 };
                let yp = p.width() * bytes + 9;
                let uvp = p.width().div_ceil(2) * 2 * bytes + 13;
                let mut y = vec![255; yp * p.height()];
                let mut uv = vec![255; uvp * p.height().div_ceil(2)];
                apply(&p, format, &mut y, yp as isize, &mut uv, uvp as isize, 1).unwrap();
                let chroma_max = if bd == 8 {
                    i32::from(uv[0])
                } else {
                    P010::read(&uv, 0)
                };
                assert!(chroma_max <= (if identity == 0 { 240 } else { 235 }) << (bd - 8));
            }
        }
    }

    #[test]
    fn invalid_layout_or_threads_does_not_write() {
        let p = test_params().validate().unwrap();
        let mut y = vec![73; 80 * 67];
        let mut uv = vec![91; 80 * 34];
        for (format, yp, uvp, threads) in [
            (PixelFormat::P010, 80, 80, 1),
            (PixelFormat::Nv12, 64, 80, 1),
            (PixelFormat::Nv12, 80, 65, 1),
            (PixelFormat::Nv12, isize::MIN, 80, 1),
            (PixelFormat::Nv12, 80, 80, 5),
        ] {
            assert!(apply(&p, format, &mut y, yp, &mut uv, uvp, threads).is_err());
            assert!(y.iter().all(|&v| v == 73));
            assert!(uv.iter().all(|&v| v == 91));
        }
        assert_eq!(
            apply(&p, PixelFormat::Nv12, &mut y[..10], 80, &mut uv, 80, 1),
            Err(ApplyError::InvalidLayout)
        );
    }

    #[test]
    fn absent_planes_pass_through_even_with_restricted_clipping() {
        let mut p = test_params();
        p.num_y_points = 0;
        p.num_uv_points = [0; 2];
        p.clip_to_restricted_range = 1;
        let mut y = vec![0; 80 * 67];
        let mut uv = vec![255; 80 * 34];
        apply(
            &p.validate().unwrap(),
            PixelFormat::Nv12,
            &mut y,
            80,
            &mut uv,
            80,
            4,
        )
        .unwrap();
        assert!(y.iter().all(|&v| v == 0));
        assert!(uv.iter().all(|&v| v == 255));
    }

    #[test]
    #[ignore = "manual CPU timing; run with --ignored --nocapture"]
    fn timing_1080p_10bit() {
        let mut p = test_params();
        p.width = 1920;
        p.height = 1080;
        p.bit_depth = 10;
        p.ar_coeff_lag = 2;
        p.ar_coeffs_y[..12]
            .copy_from_slice(&[10, -30, -20, -39, 1, -24, 12, 103, 60, -9, -24, 113]);
        p.ar_coeff_shift = 8;
        let pitch = 1920 * 2 + 64;
        let mut y = vec![128; pitch * 1080];
        let mut uv = vec![128; pitch * 540];
        for threads in [1, 4] {
            let start = std::time::Instant::now();
            let frames = 30;
            for frame in 0..frames {
                p.seed = 45231 + frame;
                apply(
                    &p.validate().unwrap(),
                    PixelFormat::P010,
                    &mut y,
                    pitch as isize,
                    &mut uv,
                    pitch as isize,
                    threads,
                )
                .unwrap();
            }
            println!(
                "1920x1080 P010 threads={threads}: {:.3} ms/frame (includes templates, LUTs, offsets; {frames} changing seeds)",
                start.elapsed().as_secs_f64() * 1000.0 / f64::from(frames)
            );
        }
    }
}
