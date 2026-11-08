//! Committed synthetic dav1d 1.5.4 oracles. No ffmpeg, C compiler or network in CI.

use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};

use bale_gpu::grain::BaleGrainParams;
use bale_gpu::grain::cpu::{self, PixelFormat};
use flate2::read::GzDecoder;

struct ParamReader<'a> {
    data: &'a [u8],
    pos: usize,
}

impl ParamReader<'_> {
    fn byte(&mut self) -> u8 {
        let value = self.data[self.pos];
        self.pos += 1;
        value
    }

    fn uint(&mut self) -> u32 {
        u32::from_le_bytes(std::array::from_fn(|_| self.byte()))
    }

    fn short(&mut self) -> i16 {
        i16::from_le_bytes(std::array::from_fn(|_| self.byte()))
    }

    fn params(&mut self) -> BaleGrainParams {
        BaleGrainParams {
            struct_size: self.uint(),
            seed: self.uint(),
            width: self.uint(),
            height: self.uint(),
            bit_depth: self.byte(),
            matrix_identity: self.byte(),
            num_y_points: self.byte(),
            chroma_scaling_from_luma: self.byte(),
            num_uv_points: std::array::from_fn(|_| self.byte()),
            scaling_shift: self.byte(),
            ar_coeff_lag: self.byte(),
            ar_coeff_shift: self.byte(),
            grain_scale_shift: self.byte(),
            overlap_flag: self.byte(),
            clip_to_restricted_range: self.byte(),
            y_points: std::array::from_fn(|_| std::array::from_fn(|_| self.byte())),
            uv_points: std::array::from_fn(|_| {
                std::array::from_fn(|_| std::array::from_fn(|_| self.byte()))
            }),
            ar_coeffs_y: std::array::from_fn(|_| self.byte() as i8),
            ar_coeffs_uv: std::array::from_fn(|_| std::array::from_fn(|_| self.byte() as i8)),
            reserved0: std::array::from_fn(|_| self.byte()),
            uv_mult: std::array::from_fn(|_| self.short()),
            uv_luma_mult: std::array::from_fn(|_| self.short()),
            uv_offset: std::array::from_fn(|_| self.short()),
        }
    }
}

fn inflate(path: &Path) -> Vec<u8> {
    let mut bytes = Vec::new();
    GzDecoder::new(fs::File::open(path).unwrap())
        .read_to_end(&mut bytes)
        .unwrap();
    bytes
}

fn planar_sample(bytes: &[u8], sample: usize, depth: u8) -> u16 {
    if depth == 8 {
        u16::from(bytes[sample])
    } else {
        u16::from_le_bytes([bytes[2 * sample], bytes[2 * sample + 1]])
    }
}

/// One-byte guard before the base deliberately makes P010's base unaligned.
/// Odd padded pitches exercise unaligned words on alternate rows too.
struct Plane {
    bytes: Vec<u8>,
    pitch: usize,
    rows: usize,
    row_bytes: usize,
    reverse: bool,
}

impl Plane {
    fn new(row_bytes: usize, rows: usize, padding: usize, reverse: bool) -> Self {
        let pitch = row_bytes + padding;
        Self {
            bytes: vec![0xa5; 1 + pitch * rows + 7],
            pitch,
            rows,
            row_bytes,
            reverse,
        }
    }

    fn pitch(&self) -> isize {
        (self.pitch as isize) * if self.reverse { -1 } else { 1 }
    }

    fn index(&self, x: usize, y: usize, depth: u8) -> usize {
        let physical_row = if self.reverse { self.rows - 1 - y } else { y };
        1 + physical_row * self.pitch + x * if depth == 8 { 1 } else { 2 }
    }

    fn put(&mut self, x: usize, y: usize, depth: u8, value: u16) {
        let index = self.index(x, y, depth);
        if depth == 8 {
            self.bytes[index] = value as u8;
        } else {
            self.bytes[index..index + 2].copy_from_slice(&(value << 6).to_le_bytes());
        }
    }

    fn word(&self, x: usize, y: usize, depth: u8) -> u16 {
        let index = self.index(x, y, depth);
        if depth == 8 {
            u16::from(self.bytes[index])
        } else {
            u16::from_le_bytes([self.bytes[index], self.bytes[index + 1]])
        }
    }

    fn assert_padding_unchanged(&self) {
        assert_eq!(self.bytes[0], 0xa5);
        for row in 0..self.rows {
            let start = 1 + row * self.pitch + self.row_bytes;
            assert!(
                self.bytes[start..1 + (row + 1) * self.pitch]
                    .iter()
                    .all(|&b| b == 0xa5),
                "modified pitch padding"
            );
        }
        assert!(
            self.bytes[1 + self.rows * self.pitch..]
                .iter()
                .all(|&b| b == 0xa5),
            "modified trailing guard"
        );
    }
}

fn compare_frame(
    name: &str,
    frame: usize,
    params: &BaleGrainParams,
    plain: &[u8],
    expected: &[u8],
    threads: u32,
    reverse: (bool, bool),
) {
    let (reverse_y, reverse_uv) = reverse;
    let w = params.width as usize;
    let h = params.height as usize;
    let cw = w.div_ceil(2);
    let ch = h.div_ceil(2);
    let depth = params.bit_depth;
    let bytes = if depth == 8 { 1 } else { 2 };
    let mut y = Plane::new(w * bytes, h, 13, reverse_y);
    let mut uv = Plane::new(cw * 2 * bytes, ch, 17, reverse_uv);
    for row in 0..h {
        for x in 0..w {
            y.put(x, row, depth, planar_sample(plain, row * w + x, depth));
        }
    }
    for plane in 0..2 {
        for row in 0..ch {
            for x in 0..cw {
                uv.put(
                    2 * x + plane,
                    row,
                    depth,
                    planar_sample(plain, w * h + plane * cw * ch + row * cw + x, depth),
                );
            }
        }
    }
    let yp = y.pitch();
    let uvp = uv.pitch();
    let format = if depth == 8 {
        PixelFormat::Nv12
    } else {
        PixelFormat::P010
    };
    cpu::apply(
        &params.validate().expect("fixture params must validate"),
        format,
        &mut y.bytes[1..],
        yp,
        &mut uv.bytes[1..],
        uvp,
        threads,
    )
    .unwrap();
    for plane in 0..3 {
        let (pw, ph) = if plane == 0 { (w, h) } else { (cw, ch) };
        let offset = if plane == 0 {
            0
        } else {
            w * h + (plane - 1) * cw * ch
        };
        for row in 0..ph {
            for x in 0..pw {
                let value = planar_sample(expected, offset + row * pw + x, depth);
                let expected_word = if depth == 8 { value } else { value << 6 };
                let actual_word = if plane == 0 {
                    y.word(x, row, depth)
                } else {
                    uv.word(2 * x + plane - 1, row, depth)
                };
                assert_eq!(
                    actual_word,
                    expected_word,
                    "{name} frame={frame} threads={threads} reverse_y={reverse_y} reverse_uv={reverse_uv}: plane={} x={x} y={row} expected={value} actual={} (stored expected={expected_word}, actual={actual_word})",
                    ["Y", "U", "V"][plane],
                    if depth == 8 {
                        actual_word
                    } else {
                        actual_word >> 6
                    }
                );
            }
        }
    }
    y.assert_padding_unchanged();
    uv.assert_padding_unchanged();
}

fn fixture_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/grain")
}

#[test]
fn every_fixture_matches_dav1d_byte_for_byte() {
    let root = fixture_root();
    let manifest = fs::read_to_string(root.join("manifest.tsv")).unwrap();
    let mut cases = 0;
    let mut frames = 0;
    for line in manifest
        .lines()
        .filter(|line| !line.starts_with('#') && !line.is_empty())
    {
        let fields: Vec<_> = line.split('\t').collect();
        assert_eq!(fields.len(), 8);
        let name = fields[0];
        let dir = root.join(name);
        let params = fs::read(dir.join("params.bin")).unwrap();
        assert_eq!(params.len(), 2 * 184);
        let plain = inflate(&dir.join("plain.yuv.gz"));
        let expected = inflate(&dir.join("grained.yuv.gz"));
        assert_eq!(plain.len(), expected.len());
        assert_eq!(fields[4], "2");
        let mut last_seed = None;
        for (frame, record) in params.as_chunks::<184>().0.iter().enumerate() {
            let mut reader = ParamReader {
                data: record,
                pos: 0,
            };
            let params = reader.params();
            assert_eq!(reader.pos, 184);
            assert_eq!(params.width.to_string(), fields[1]);
            assert_eq!(params.height.to_string(), fields[2]);
            assert_eq!(params.bit_depth.to_string(), fields[3]);
            assert_ne!(
                last_seed,
                Some(params.seed),
                "{name}: frame seeds must differ"
            );
            last_seed = Some(params.seed);
            let w = params.width as usize;
            let h = params.height as usize;
            let len = (w * h + 2 * w.div_ceil(2) * h.div_ceil(2))
                * if params.bit_depth == 8 { 1 } else { 2 };
            assert_eq!(plain.len(), len * 2);
            let plain = &plain[frame * len..(frame + 1) * len];
            let expected = &expected[frame * len..(frame + 1) * len];
            for (threads, ry, ruv) in [
                (1, false, false),
                (4, false, false),
                (0, false, false),
                (4, true, false),
                (4, false, true),
                (4, true, true),
            ] {
                compare_frame(name, frame, &params, plain, expected, threads, (ry, ruv));
            }
            frames += 1;
        }
        cases += 1;
    }
    assert_eq!(cases, 44, "do not silently drop fixtures");
    assert_eq!(frames, 88);
}

#[test]
fn manifest_pins_required_branch_coverage_and_presets() {
    let root = fixture_root();
    let manifest = fs::read_to_string(root.join("manifest.tsv")).unwrap();
    let lines: Vec<_> = manifest
        .lines()
        .filter(|s| !s.starts_with('#') && !s.is_empty())
        .collect();
    for depth in [8, 10] {
        for preset in 1..=16 {
            let name = format!("preset{preset:02}-{depth}\t");
            assert!(
                lines.iter().any(|line| line.starts_with(&name)),
                "missing {name}"
            );
        }
        for branch in [
            "csfl",
            "restricted",
            "matrix-identity",
            "lag0",
            "lag3",
            "no-y-points",
            "chroma-pass-through",
            "negative-uv-offset",
            "negative-uv-mult",
            "scaling8",
            "scaling11",
            "overlap-off",
            "overlap-on",
            "odd-width-height",
            "one-pixel-edge-blocks",
            "max-points",
            "nonconstant-scaling",
        ] {
            assert!(
                lines.iter().any(|line| {
                    let fields: Vec<_> = line.split('\t').collect();
                    fields[3] == depth.to_string() && fields[5].split(',').any(|tag| tag == branch)
                }),
                "missing {branch} at {depth}-bit"
            );
        }
    }
    assert!(lines.iter().any(|line| {
        line.split('\t')
            .nth(5)
            .unwrap()
            .split(',')
            .any(|tag| tag == "10bit-substeps")
    }));
    fn total_bytes(path: &Path) -> u64 {
        fs::read_dir(path)
            .unwrap()
            .map(|entry| {
                let entry = entry.unwrap();
                if entry.file_type().unwrap().is_dir() {
                    total_bytes(&entry.path())
                } else {
                    entry.metadata().unwrap().len()
                }
            })
            .sum()
    }
    assert!(total_bytes(&root) <= 4_000_000, "fixture budget exceeded");
}
