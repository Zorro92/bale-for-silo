//! Plain C ABI records; keep synchronized with include/bale_tonemap.h.
use std::mem::{align_of, offset_of, size_of};

pub const TONEMAP_ABI_VERSION: u32 = 1;
/// bale_tonemap_output_formats bits: apply accepts these output texture formats.
pub const BALE_TONEMAP_OUTPUT_NV12: u32 = 1;
pub const BALE_TONEMAP_OUTPUT_P010: u32 = 2;
/// The config's `reserved[0..2]` may carry a larger visible source size: apply area-downscales
/// in display-linear light to `width` x `height`.
pub const BALE_TONEMAP_OUTPUT_DOWNSCALE: u32 = 4;
/// The input may be NV12 (8-bit codes read as code << 2); output stays NV12 or P010 as chosen.
pub const BALE_TONEMAP_INPUT_NV12: u32 = 8;
/// BT.709 SDR sources (transfer 1/6/14/15, primaries 1, matrix 1) are accepted: decode BT.1886 to
/// linear light, area-downscale, re-encode; no tone curve and no gamut change (scale only).
pub const BALE_TONEMAP_SDR709: u32 = 16;
/// NV12 output is ordered-dithered (static 8x8 Bayer threshold, luma and chroma) before the 8-bit
/// quantisation, from the same float result P010 rounds to 10 bits. P010 output is unchanged.
pub const BALE_TONEMAP_OUTPUT_NV12_DITHERED: u32 = 32;
// Bit 64 is retired (2026-11-24) and stays unused: wrappers 2.1.0-bale.64 to bale.75 read it as
// permission for shader-readable decoder pools. Decoder arrays are always copied first.
/// Largest source/output ratio per axis the area downscale accepts.
pub const MAX_DOWNSCALE: u32 = 4;
pub const BALE_TONEMAP_E_SELFTEST: i32 = 0x8004BA01u32 as i32;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct BaleToneMapConfig {
    pub width: u32,
    pub height: u32,
    pub curve: u32,
    /// The tone curve's source peak without stream metadata (0 = the 1000-nit default). PQ frames with
    /// MaxCLL or mastering metadata use that instead (`cpu::source_peak`).
    pub source_peak_nits: f32,
    pub target_white_nits: f32,
    pub desat: f32,
    pub reserved: [u32; 4],
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct BaleToneMapFrameInfo {
    pub transfer: u8,
    pub primaries: u8,
    pub matrix: u8,
    pub full_range: u8,
    pub chroma_location: u8,
    pub has_mastering: u8,
    pub has_content_light: u8,
    pub reserved0: u8,
    pub mastering_primaries: [[f32; 2]; 3],
    pub mastering_white: [f32; 2],
    pub mastering_min_nits: f32,
    pub mastering_max_nits: f32,
    pub max_cll: u32,
    pub max_fall: u32,
    pub reserved: [u32; 4],
}

const _: () = {
    assert!(size_of::<BaleToneMapConfig>() == 40);
    assert!(align_of::<BaleToneMapConfig>() == 4);
    assert!(offset_of!(BaleToneMapConfig, width) == 0);
    assert!(offset_of!(BaleToneMapConfig, height) == 4);
    assert!(offset_of!(BaleToneMapConfig, curve) == 8);
    assert!(offset_of!(BaleToneMapConfig, source_peak_nits) == 12);
    assert!(offset_of!(BaleToneMapConfig, target_white_nits) == 16);
    assert!(offset_of!(BaleToneMapConfig, desat) == 20);
    assert!(offset_of!(BaleToneMapConfig, reserved) == 24);
    assert!(size_of::<BaleToneMapFrameInfo>() == 72);
    assert!(align_of::<BaleToneMapFrameInfo>() == 4);
    assert!(offset_of!(BaleToneMapFrameInfo, transfer) == 0);
    assert!(offset_of!(BaleToneMapFrameInfo, primaries) == 1);
    assert!(offset_of!(BaleToneMapFrameInfo, matrix) == 2);
    assert!(offset_of!(BaleToneMapFrameInfo, full_range) == 3);
    assert!(offset_of!(BaleToneMapFrameInfo, chroma_location) == 4);
    assert!(offset_of!(BaleToneMapFrameInfo, has_mastering) == 5);
    assert!(offset_of!(BaleToneMapFrameInfo, has_content_light) == 6);
    assert!(offset_of!(BaleToneMapFrameInfo, reserved0) == 7);
    assert!(offset_of!(BaleToneMapFrameInfo, mastering_primaries) == 8);
    assert!(offset_of!(BaleToneMapFrameInfo, mastering_white) == 32);
    assert!(offset_of!(BaleToneMapFrameInfo, mastering_min_nits) == 40);
    assert!(offset_of!(BaleToneMapFrameInfo, mastering_max_nits) == 44);
    assert!(offset_of!(BaleToneMapFrameInfo, max_cll) == 48);
    assert!(offset_of!(BaleToneMapFrameInfo, max_fall) == 52);
    assert!(offset_of!(BaleToneMapFrameInfo, reserved) == 56);
};

/// The source peak the most recently created stage applies (additive export `bale_tonemap_peak_read`,
/// 2026-11-24, in every build). Layout is checked against C and C#.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct BaleToneMapPeakInfo {
    /// That stage's create number (1, 2, ...); 0 = no stage created yet.
    pub stage: u64,
    /// How often its peak or peak source changed; 0 = no PQ or HLG frame applied yet.
    pub changes: u32,
    /// 1 = config, 2 = MaxCLL, 3 = mastering display maximum (`cpu::PeakSource`).
    pub source: u32,
    /// The source peak the tone curve uses.
    pub peak_nits: f32,
    /// The stage's `config.source_peak_nits` (0 = from the metadata, 1000 without).
    pub config_peak_nits: f32,
    /// The frame's MaxCLL; 0 = none.
    pub max_cll: u32,
    /// The frame's mastering display maximum; 0 = none (or not a positive finite value).
    pub mastering_max_nits: f32,
    /// That frame's H.273 transfer: 16 PQ, 18 HLG.
    pub transfer: u32,
    /// Bit 0: debug `fixedpeak` (the config peak despite the metadata).
    pub flags: u32,
}
const _: () = {
    assert!(size_of::<BaleToneMapPeakInfo>() == 40);
    assert!(align_of::<BaleToneMapPeakInfo>() == 8);
    assert!(offset_of!(BaleToneMapPeakInfo, changes) == 8);
    assert!(offset_of!(BaleToneMapPeakInfo, source) == 12);
    assert!(offset_of!(BaleToneMapPeakInfo, peak_nits) == 16);
    assert!(offset_of!(BaleToneMapPeakInfo, config_peak_nits) == 20);
    assert!(offset_of!(BaleToneMapPeakInfo, max_cll) == 24);
    assert!(offset_of!(BaleToneMapPeakInfo, mastering_max_nits) == 28);
    assert!(offset_of!(BaleToneMapPeakInfo, transfer) == 32);
    assert!(offset_of!(BaleToneMapPeakInfo, flags) == 36);
};

/// Optional debug snapshot, additive to ABI 1. Layout is checked against C and C#.
/// avg_ms/max_ms: total, input copy, luma, chroma. Counters describe one completed window.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct BaleToneMapTimingStats {
    pub session: u64,
    pub window: u64,
    /// 0 = no measured stage, 1 = collecting, 2 = timing disabled, 3 = stage destroyed.
    pub state: u32,
    pub frames: u32,
    pub valid: u32,
    pub disjoint: u32,
    pub unavailable: u32,
    pub pending: u32,
    pub error: i32,
    /// Additive failure reason (see bale_tonemap.h); zero in older DLLs.
    pub reserved: u32,
    pub avg_ms: [f64; 4],
    pub max_ms: [f64; 4],
}
const _: () = {
    assert!(size_of::<BaleToneMapTimingStats>() == 112);
    assert!(align_of::<BaleToneMapTimingStats>() == 8);
    assert!(offset_of!(BaleToneMapTimingStats, state) == 16);
    assert!(offset_of!(BaleToneMapTimingStats, avg_ms) == 48);
    assert!(offset_of!(BaleToneMapTimingStats, max_ms) == 80);
};
