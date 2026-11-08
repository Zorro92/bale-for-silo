//! Shared, independently generated dav1d fixture loading (CPU and WARP tests).

use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};

use bale_gpu::grain::BaleGrainParams;
use flate2::read::GzDecoder;

pub struct Fixture {
    pub name: String,
    pub frames: Vec<Frame>,
}

pub struct Frame {
    pub params: BaleGrainParams,
    pub plain: Vec<u8>,
    pub grained: Vec<u8>,
}

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

pub fn planar_sample(bytes: &[u8], sample: usize, depth: u8) -> u16 {
    if depth == 8 {
        u16::from(bytes[sample])
    } else {
        u16::from_le_bytes([bytes[2 * sample], bytes[2 * sample + 1]])
    }
}

pub fn fixture_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/grain")
}

pub fn fixtures() -> Vec<Fixture> {
    let root = fixture_root();
    let manifest = fs::read_to_string(root.join("manifest.tsv")).unwrap();
    let mut fixtures = Vec::new();
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
        let grained = inflate(&dir.join("grained.yuv.gz"));
        assert_eq!(plain.len(), grained.len());
        assert_eq!(fields[4], "2");
        let mut last_seed = None;
        let mut frames = Vec::new();
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
            frames.push(Frame {
                params,
                plain: plain[frame * len..(frame + 1) * len].to_vec(),
                grained: grained[frame * len..(frame + 1) * len].to_vec(),
            });
        }
        assert_eq!(frames.len(), 2);
        fixtures.push(Fixture {
            name: name.to_owned(),
            frames,
        });
    }
    assert_eq!(fixtures.len(), 44, "do not silently drop fixtures");
    fixtures
}
