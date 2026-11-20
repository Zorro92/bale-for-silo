//! Idle debug memory probe, driven step-wise by DEBUG_SERVER C# (never by playback).
//! Allocations deliberately bypass memledger: probe totals include its 128 MiB timing pair,
//! but exclude driver padding, query storage and device overhead (visible in DXGI/app usage).

#[cfg(windows)]
mod d11;
#[cfg(windows)]
mod d12;
#[cfg(windows)]
mod gpu;

pub const MIB: u64 = 1024 * 1024;
pub const COPY_BYTES: u64 = 64 * MIB;
pub const WORKING_BYTES: u64 = 2 * COPY_BYTES;

/// Exact MiB steps, split into at most 64 MiB RGBA8 textures. No rounding of the request.
pub fn texture_height(bytes: u64) -> Option<u32> {
    if bytes == 0 || bytes > COPY_BYTES || !bytes.is_multiple_of(MIB) {
        return None;
    }
    Some((bytes / (4096 * 4)) as u32)
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct Segment {
    pub budget: u64,
    pub usage: u64,
    pub available: u64,
    pub reservation: u64,
    pub hr: i32,
    pub reserved: u32,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Sample {
    pub bytes: u64,
    pub copy_ms: f64,
    pub local: Segment,
    pub non_local: Segment,
    pub removed_hr: i32,
    /// 1=create texture, 2=write/wait, 3=timed copy. HRESULT returned by step identifies failure.
    pub stage: u32,
}
impl Default for Sample {
    fn default() -> Self {
        Self {
            bytes: 0,
            copy_ms: f64::NAN,
            local: Segment::default(),
            non_local: Segment::default(),
            removed_hr: 0,
            stage: 0,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn step_textures_have_exact_size_and_bounded_dimensions() {
        for mib in 1..=64 {
            let height = texture_height(mib * MIB).unwrap();
            assert_eq!(u64::from(height) * 4096 * 4, mib * MIB);
            assert!(height <= 4096);
        }
        for bytes in [0, 1, MIB - 1, COPY_BYTES + 1, 100 * MIB, u64::MAX] {
            assert_eq!(texture_height(bytes), None);
        }
    }

    #[test]
    fn abi_layout_matches_the_blittable_csharp_snapshot() {
        assert_eq!(std::mem::size_of::<Segment>(), 40);
        assert_eq!(std::mem::size_of::<Sample>(), 104);
        assert_eq!(std::mem::offset_of!(Sample, local), 16);
        assert_eq!(std::mem::offset_of!(Sample, removed_hr), 96);
        assert!(Sample::default().copy_ms.is_nan());
    }
}
