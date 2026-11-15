//! Ported from `HardwareDecodeLimits`: the console's hardware decoder probe folded into sizes.

use serde::{Deserialize, Serialize};

// D3D11_DECODER_PROFILE_* from the Windows SDK's d3d11.h, as Guid.ToString() writes them. The C#
// Guid constants must match (BaleCoreNativeTests checks ProfileName against each).
const H264: &str = "1b81be68-a0c7-11d3-b984-00c04f2e73c5";
const HEVC_MAIN: &str = "5b11d51b-2f4c-4452-bcc3-09f2a1160cc0";
const HEVC_MAIN10: &str = "107af0e0-ef1a-4d19-aba8-67a163073d13";
const VP9_PROFILE0: &str = "463707f8-a1d0-4585-876d-83aa6d60b89e";
const VP9_PROFILE2: &str = "a4c749ef-6ecf-48aa-8448-50a7a1165ff7";
const VC1: &str = "1b81bea3-a0c7-11d3-b984-00c04f2e73c5";
const MPEG2: &str = "ee27417f-5e28-4e65-beea-1d26b508adc9";

// DXGI_FORMAT_NV12 and DXGI_FORMAT_P010 (dxgiformat.h).
const NV12: i32 = 103;
const P010: i32 = 104;

/// A short log name for a claimable decoder profile, or "other".
pub fn profile_name(profile: &str) -> &'static str {
    match profile {
        H264 => "h264",
        HEVC_MAIN => "hevc-main",
        HEVC_MAIN10 => "hevc-main10",
        VP9_PROFILE0 => "vp9-profile0",
        VP9_PROFILE2 => "vp9-profile2",
        VC1 => "vc1",
        MPEG2 => "mpeg2",
        _ => "other",
    }
}

#[derive(Deserialize)]
pub struct AcceptedSize {
    profile: String,
    format: i32,
    width: i32,
    height: i32,
}

#[derive(Serialize, Debug, PartialEq, Clone, Copy)]
pub struct DecodeSize {
    width: i32,
    height: i32,
}

#[derive(Serialize, Debug, PartialEq)]
pub struct Limits {
    h264: Option<DecodeSize>,
    hevc_main: Option<DecodeSize>,
    hevc_main10: Option<DecodeSize>,
    vp9_profile0: Option<DecodeSize>,
    vp9_profile2: Option<DecodeSize>,
    vc1: Option<DecodeSize>,
    mpeg2: Option<DecodeSize>,
}

/// Folds the probe's accepted (profile, output format, size) answers into the largest size per
/// profile. An 8-bit profile counts only with NV12 output and a 10-bit one only with P010, the
/// formats the playback pipeline uses for those depths. Other profiles are ignored.
pub fn from_accepted_sizes(accepted: &[AcceptedSize]) -> Limits {
    let largest = |profile: &str, format: i32| {
        let mut largest: Option<DecodeSize> = None;
        for size in accepted {
            if size.profile != profile
                || size.format != format
                || size.width <= 0
                || size.height <= 0
            {
                continue;
            }
            let pixels = |w: i32, h: i32| i64::from(w) * i64::from(h);
            if largest.is_none_or(|current| {
                pixels(size.width, size.height) > pixels(current.width, current.height)
            }) {
                largest = Some(DecodeSize {
                    width: size.width,
                    height: size.height,
                });
            }
        }
        largest
    };
    Limits {
        h264: largest(H264, NV12),
        hevc_main: largest(HEVC_MAIN, NV12),
        hevc_main10: largest(HEVC_MAIN10, P010),
        vp9_profile0: largest(VP9_PROFILE0, NV12),
        vp9_profile2: largest(VP9_PROFILE2, P010),
        vc1: largest(VC1, NV12),
        mpeg2: largest(MPEG2, NV12),
    }
}
