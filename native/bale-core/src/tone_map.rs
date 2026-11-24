//! Ported from `ConsoleToneMapPolicy`: the original-file executor's bounded HDR-to-SDR promise,
//! never a sink-HDR claim.

use crate::dotnet::{eq_ignore_case, is_blank, trim_lower};
use crate::formatting;
use crate::models::{DeviceCapabilities, FileVersion, HdrCapabilities, PlaybackPlan};
use crate::software_decode::normalize_codec;

/// `PlaybackProtocol.ClientDolbyVision8BaseLayerReason`.
pub const DOLBY_VISION8_BASE_LAYER_REASON: &str = "client_dv8_base_layer";
/// `PlaybackProtocol.ClientManagedDynamicRangeReason`.
const MANAGED_DYNAMIC_RANGE_REASON: &str = "client_managed_dynamic_range";

pub fn has_hdr_output(hdr: &HdrCapabilities) -> bool {
    hdr.hdr10 || hdr.hdr10_plus || hdr.hlg || !hdr.dolby_vision_profiles.is_empty()
}

/// bale.29 adds HLG eligibility; earlier stages only promised PQ. Keep the whole production claim
/// behind that revision and a runtime ABI/export check in the Xbox app.
pub fn wrapper_supported(version: &str) -> bool {
    // int.TryParse with NumberStyles.None: ASCII digits only, within Int32.
    version.strip_prefix("2.1.0-bale.").is_some_and(|revision| {
        !revision.is_empty()
            && revision.bytes().all(|b| b.is_ascii_digit())
            && revision.parse::<i32>().is_ok_and(|revision| revision >= 29)
    })
}

/// bale.38 adds the display-sized tone-map output keys (`bale_tonemap_output_width/height`).
pub fn downscale_wrapper_supported(version: &str) -> bool {
    version.strip_prefix("2.1.0-bale.").is_some_and(|revision| {
        !revision.is_empty()
            && revision.bytes().all(|b| b.is_ascii_digit())
            && revision.parse::<i32>().is_ok_and(|revision| revision >= 38)
    })
}

/// bale.39 adds the SDR downscale route (BT.709 SDR through bale_gpu's scale-only stage).
pub fn sdr_downscale_wrapper_supported(version: &str) -> bool {
    version.strip_prefix("2.1.0-bale.").is_some_and(|revision| {
        !revision.is_empty()
            && revision.bytes().all(|b| b.is_ascii_digit())
            && revision.parse::<i32>().is_ok_and(|revision| revision >= 39)
    })
}

/// Whether the tone-map stage should hand Media Foundation display-sized output: the video is larger
/// than the current display mode in either dimension. On Xbox, MF's video processor leaks driver
/// state per open when it scales 4K SDR down itself (2026-11-17: dies at ~23 opens; 40/40 when
/// bale_gpu area-downscales to the display first). Unknown sizes keep the video size.
pub fn downscale_to_display(
    video_width: i64,
    video_height: i64,
    display_width: i64,
    display_height: i64,
) -> bool {
    video_width > 0
        && video_height > 0
        && display_width > 0
        && display_height > 0
        && (video_width > display_width || video_height > display_height)
}

/// Whether the console tone map can take this version on an SDR display: a hardware-decoded 10-bit
/// 4:2:0 HEVC/VP9 PQ or HLG track with BT.2020 NCL (PQ also BT.709) colour, and for Dolby Vision only
/// a profile 7 HDR10 base layer or a profile 8.1/8.4 base layer.
pub fn can_manage(version: &FileVersion, device: &DeviceCapabilities) -> bool {
    if !device.original_http_supported
        || !device.original_dynamic_range_mapping_supported
        || device.display_hdr_evidence != "exact"
        || has_hdr_output(&device.hdr)
    {
        return false;
    }
    let Some(tracks) = version
        .video_tracks
        .as_ref()
        .filter(|tracks| !tracks.is_empty())
    else {
        return false;
    };
    let track = &tracks[0];
    // DV7 explicitly selects the first video in demux order, including dual-track BL+EL.
    // Other range-management routes still require an unambiguous single video stream.
    if if track.dv_profile == Some(7) {
        !device.original_dolby_vision7_base_layer_supported
    } else {
        tracks.len() != 1
    } {
        return false;
    }
    let codec = normalize_codec(if is_blank(track.codec.as_deref()) {
        version.codec_video.as_deref()
    } else {
        track.codec.as_deref()
    });
    // AV1 joins only where rav1g's display output maps it (debug rav1g route); never AV1 Dolby Vision.
    let av1 = codec.as_deref() == Some("av1") && device.av1_dynamic_range_mapping_supported;
    let Some(codec) = codec.filter(|codec| codec == "hevc" || codec == "vp9" || av1) else {
        return false;
    };
    // HEVC/VP9 need a hardware decode claim (d3d11va feeds the stage); AV1 is rav1g's own (software-class) claim.
    let decoders: Vec<_> = device
        .video_decode
        .iter()
        .filter(|claim| claim.hardware || av1)
        .cloned()
        .collect();
    if track.bit_depth != Some(10)
        || !device.video_codecs.contains(&codec)
        || !formatting::decodes(version, &decoders)
        || av1 && formatting::is_dolby_vision(version)
    {
        return false;
    }
    // d3d11va supplies 4:2:0 P010. Do not promise the stage for known 4:2:2/4:4:4 input.
    if !four_two_zero_ten_bit(track.pixel_format.as_deref()) {
        return false;
    }
    let transfer = trim_lower(track.color_transfer.as_deref());
    let pq = matches!(transfer.as_deref(), Some("smpte2084" | "16"));
    let hlg = matches!(transfer.as_deref(), Some("arib-std-b67" | "18"));
    if !pq && !hlg {
        return false;
    }
    // Mirror bale_gpu's frame eligibility: PQ supports BT.2020 NCL or BT.709; HLG needs
    // BT.2020 NCL. Unknown/mixed primaries and matrix cannot establish a safe conversion.
    let primaries = trim_lower(track.color_primaries.as_deref());
    let matrix = trim_lower(track.color_space.as_deref());
    let bt2020 = matches!(primaries.as_deref(), Some("bt2020" | "9"))
        && matches!(matrix.as_deref(), Some("bt2020nc" | "9"));
    let bt709 = matches!(primaries.as_deref(), Some("bt709" | "1"))
        && matches!(matrix.as_deref(), Some("bt709" | "1"));
    if !bt2020 && !(pq && bt709) {
        return false;
    }
    if !formatting::is_dolby_vision(version) {
        return true;
    }
    if codec != "hevc" || track.dv_bl_present != Some(true) || track.dv_config_present != Some(true)
    {
        return false;
    }
    // Profile 7 defines an HDR10 base layer; neither MEL nor FEL enhancement is presented.
    // Profile 8 needs its explicit compatibility id: 8.1 = HDR10, 8.4 = HLG, 8.2 = SDR.
    match track.dv_profile {
        Some(7) => pq && bt2020 && matches!(track.dv_bl_compat_id, None | Some(0 | 1)),
        Some(8) => {
            track.dv_el_present != Some(true)
                && track.dv_bl_compat_id_present == Some(true)
                && (pq && track.dv_bl_compat_id == Some(1)
                    || hlg && track.dv_bl_compat_id == Some(4))
        }
        _ => false,
    }
}

/// Deliveries whose video is the source's own, never re-encoded: the original file, or the server's
/// forward-only remux (a stream copy). Silo only remuxes HDR for an SDR output when it doesn't know
/// the source is HDR (packaged deliveries stay output-gated), e.g. HLG in a container tagged SDR, so
/// the console must map those itself. The HLS remux counts only when FFmpeg plays it (the Rust HLS
/// reader); Windows' own HLS source can't take the tone map.
const COPIED_VIDEO_DELIVERIES: [&str; 2] = ["original_http", "server_remux_progressive"];
const HLS_REMUX: &str = "server_remux_hls";

/// Protects even unexpected HDR video the server did not re-encode; never lets it reach an SDR
/// display unmapped.
pub fn requires_tone_map(
    plan: &PlaybackPlan,
    sdr_output: bool,
    versions: Option<&[FileVersion]>,
    hls_through_ffmpeg: bool,
) -> bool {
    let copied = COPIED_VIDEO_DELIVERIES
        .iter()
        .any(|delivery| eq_ignore_case(plan.delivery.as_deref(), delivery))
        || hls_through_ffmpeg && eq_ignore_case(plan.delivery.as_deref(), HLS_REMUX);
    if !sdr_output || !copied {
        return false;
    }
    let source = plan.source.as_ref();
    // The existing DV 8.2 fallback explicitly promises an SDR base layer, not HDR pixels.
    if eq_ignore_case(
        plan.decision_reason.as_deref(),
        DOLBY_VISION8_BASE_LAYER_REASON,
    ) && source
        .is_some_and(|s| s.dolby_vision_profile == Some(8) && s.dv_bl_compat_id == Some(2))
        && eq_ignore_case(
            plan.effective_recipe
                .as_ref()
                .and_then(|r| r.dynamic_range.as_deref()),
            "sdr",
        )
    {
        return false;
    }
    let video = plan.claims.as_ref().and_then(|c| c.video.as_ref());
    eq_ignore_case(
        plan.decision_reason.as_deref(),
        MANAGED_DYNAMIC_RANGE_REASON,
    ) || !matches!(
        trim_lower(source.and_then(|s| s.dynamic_range.as_deref())).as_deref(),
        None | Some("" | "sdr")
    ) || source
        .and_then(|s| s.dolby_vision_profile)
        .is_some_and(|p| p > 0)
        || source.and_then(|s| s.hdr10_plus) == Some(true)
        || video.is_some_and(|v| {
            v.hdr10 == Some(true)
                || v.hdr10_plus == Some(true)
                || v.hlg == Some(true)
                || v.dolby_vision == Some(true)
        })
        || is_wide_gamut_sdr_original(plan, versions)
}

fn is_wide_gamut_sdr_original(plan: &PlaybackPlan, versions: Option<&[FileVersion]>) -> bool {
    let Some(source) = plan.source.as_ref() else {
        return false;
    };
    if !eq_ignore_case(source.dynamic_range.as_deref().map(str::trim), "sdr") {
        return false;
    }
    // The plan has codec/depth but no primaries/matrix. Join catalog colour facts to the file
    // actually delivered, never the requested version when the planner substituted one.
    let file_id = plan
        .effective_media_file_id
        .as_deref()
        .or(source.media_file_id.as_deref());
    let version =
        file_id.and_then(|id| versions?.iter().find(|v| v.file_id.as_deref() == Some(id)));
    let Some((version, track)) = version.and_then(|v| match v.video_tracks.as_deref() {
        Some([track]) => Some((v, track)),
        _ => None,
    }) else {
        return false;
    };
    let codec = normalize_codec(
        source
            .video_codec
            .as_deref()
            .or(track.codec.as_deref())
            .or(version.codec_video.as_deref()),
    );
    if !matches!(codec.as_deref(), Some("hevc" | "vp9"))
        || source.bit_depth.or(track.bit_depth) != Some(10)
        || !matches!(track.bit_depth, None | Some(10))
    {
        return false;
    }
    if !four_two_zero_ten_bit(track.pixel_format.as_deref()) {
        return false;
    }
    let primaries = trim_lower(track.color_primaries.as_deref());
    let matrix = trim_lower(track.color_space.as_deref());
    // bale.29 chooses PQ/HLG tone mapping or true SDR gamut conversion from each frame. Either
    // explicit BT.2020 primaries or an uncontradicted NCL matrix arms that stage; the existing live
    // output guard rejects any format the wrapper cannot convert.
    matches!(primaries.as_deref(), Some("bt2020" | "9"))
        && matches!(matrix.as_deref(), None | Some("" | "bt2020nc" | "9"))
        || matches!(primaries.as_deref(), None | Some(""))
            && matches!(matrix.as_deref(), Some("bt2020nc" | "9"))
}

/// A blank pixel format is unknown and allowed; a known one must be 4:2:0 10-bit.
fn four_two_zero_ten_bit(pixel_format: Option<&str>) -> bool {
    is_blank(pixel_format)
        || matches!(
            trim_lower(pixel_format).as_deref(),
            Some("yuv420p10le" | "p010le")
        )
}

#[cfg(test)]
mod downscale_tests {
    #[test]
    fn downscales_only_when_the_video_is_larger_than_the_display() {
        assert!(super::downscale_to_display(3840, 2160, 1920, 1080));
        assert!(super::downscale_to_display(3840, 1600, 1920, 1080));
        assert!(super::downscale_to_display(1920, 1200, 1920, 1080));
        assert!(!super::downscale_to_display(3840, 2160, 3840, 2160));
        assert!(!super::downscale_to_display(1920, 1080, 1920, 1080));
        assert!(!super::downscale_to_display(1280, 720, 1920, 1080));
        assert!(!super::downscale_to_display(3840, 2160, 0, 0));
        assert!(!super::downscale_to_display(0, 0, 1920, 1080));
    }
    #[test]
    fn downscale_keys_need_wrapper_bale_38() {
        assert!(super::downscale_wrapper_supported("2.1.0-bale.38"));
        assert!(super::downscale_wrapper_supported("2.1.0-bale.140"));
        assert!(!super::downscale_wrapper_supported("2.1.0-bale.37"));
        assert!(!super::downscale_wrapper_supported("2.1.0-bale.38-beta"));
        assert!(!super::downscale_wrapper_supported("2.1.0"));
    }
}
