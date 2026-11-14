//! Plain C ABI records; keep synchronized with include/bale_tonemap.h.
use std::mem::{align_of, offset_of, size_of};

pub const TONEMAP_ABI_VERSION: u32 = 1;
pub const BALE_TONEMAP_E_SELFTEST: i32 = 0x8004BA01u32 as i32;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct BaleToneMapConfig {
    pub width: u32,
    pub height: u32,
    pub curve: u32,
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
