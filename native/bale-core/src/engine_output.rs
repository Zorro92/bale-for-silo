//! The IMFMediaEngine swap-chain format of one mount (owner's bit-depth rule, AGENTS.md): work at
//! source depth, deliver at display depth. P010 gets R10G10B10A2 (`rgb10`) for HDR passthrough or a
//! 30+ bpp SDR display, otherwise B8G8R8A8 (`bgra`). AV1 temporarily keeps 10-bit output until rav1g
//! is stable. NV12 and unknown frames keep `bgra`.
//!
//! Phase 2 (2026-11-19): on an 8-bit SDR display the console stage (tone map or SDR downscale) is
//! asked for dithered 8-bit NV12 (`stage_output_depth` 8, the fork's `bale_tonemap_output_depth`)
//! when bale_gpu dithers it, except for AV1. Otherwise no stage output depth changes here.

use serde::{Deserialize, Serialize};

use crate::dotnet::{eq_ignore_case, is_blank, trim_lower};
use crate::models::{FileVersion, PlaybackPlan};
use crate::software_decode::normalize_codec;

/// Deliveries whose video is the source file's own stream (no server encode).
const COPIED_VIDEO_DELIVERIES: [&str; 4] = [
    "original_http",
    "progressive",
    "server_remux_progressive",
    "server_remux_hls",
];

/// Owner's temporary exception: retain realistic 10-bit resource use while developing rav1g.
const AV1_KEEPS_10_BIT_OUTPUT_UNTIL_RAV1G_IS_STABLE: bool = true;

#[derive(Deserialize, Default, Clone, Copy, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
enum Ceiling {
    #[default]
    Display,
    Source,
}

/// The `PlaybackRouteDecision` fields this reads (C# writes the whole record; serde skips the rest).
#[derive(Deserialize, Debug, Default, Clone)]
pub struct EngineRoute {
    pub use_ffmpeg: bool,
    pub software_video: bool,
    pub video_codec: Option<String>,
    pub console_tone_map: bool,
    pub console_downscale: bool,
    pub dolby_vision7_base_layer: bool,
}

#[derive(Deserialize)]
pub struct DecideArgs {
    plan: PlaybackPlan,
    versions: Option<Vec<FileVersion>>,
    route: EngineRoute,
    /// bale_gpu's console stage writes P010 (`ConsoleToneMapRuntime.SupportsP010`); without it the
    /// tone map and the downscale write NV12.
    stage_p010: bool,
    /// bale_gpu dithers the stage's NV12 output (`ConsoleToneMapRuntime.SupportsDitheredNv12`,
    /// BALE_TONEMAP_OUTPUT_NV12_DITHERED); only then is 8-bit stage output requested.
    #[serde(default)]
    stage_nv12_dithered: bool,
    /// Current HDMI mode, not the display's supported modes. Unknown caps SDR at 8 bits.
    display_bits_per_pixel: Option<i32>,
    #[serde(default)]
    ceiling: Ceiling,
}

/// `EngineOutputDecision`.
#[derive(Serialize, Debug, PartialEq, Eq)]
pub struct EngineOutput {
    /// `bgra` or `rgb10` (`engine_target::Target` names in bale_gpu).
    pub target: &'static str,
    /// The frames the engine gets: `nv12`, `p010` or `unknown`.
    pub frames: &'static str,
    /// Fixed text for the log: why.
    pub reason: &'static str,
    /// The console stage's requested output depth: 8 (dithered NV12, the fork key
    /// `bale_tonemap_output_depth=8`), 10 (today's P010, key unset), or 0 without a stage.
    pub stage_output_depth: u8,
    /// Fixed text for the log: why that stage depth.
    pub stage_reason: &'static str,
}

fn output(frames: &'static str, reason: &'static str) -> EngineOutput {
    EngineOutput {
        target: if frames == "p010" { "rgb10" } else { "bgra" },
        frames,
        reason,
        stage_output_depth: 0,
        stage_reason: "no console stage",
    }
}

/// Whether the console stage writes dithered 8-bit NV12 for this mount, and why (fixed log text).
/// Only on an SDR display whose current HDMI mode is below 30 bpp (unknown counts as 8-bit, as for
/// the engine output), never for AV1 (temporary exception) or HDR passthrough, and only when
/// bale_gpu dithers its NV12. Debug `output-depth source` keeps today's P010.
fn stage_output(args: &DecideArgs, depth: &Depth) -> (u8, &'static str) {
    let route = &args.route;
    if !route.console_tone_map && !route.console_downscale {
        return (0, "no console stage");
    }
    if args.ceiling == Ceiling::Source {
        return (10, "debug output-depth source keeps P010");
    }
    if !args.stage_nv12_dithered {
        return (10, "bale_gpu without dithered NV12");
    }
    if AV1_KEEPS_10_BIT_OUTPUT_UNTIL_RAV1G_IS_STABLE && is_av1(args) {
        return (10, "AV1 keeps 10-bit output (until rav1g is stable)");
    }
    if !route.console_tone_map && matches!(depth, Depth::Hdr | Depth::Known { hdr: true, .. }) {
        return (10, "HDR passthrough");
    }
    if args.display_bits_per_pixel.is_some_and(|bpp| bpp >= 30) {
        return (10, "SDR 10-bit display");
    }
    (8, "SDR 8-bit display")
}

fn is_av1(args: &DecideArgs) -> bool {
    [
        args.route.video_codec.as_deref(),
        args.plan
            .source
            .as_ref()
            .and_then(|s| s.video_codec.as_deref()),
    ]
    .iter()
    .any(|&codec| normalize_codec(codec).as_deref() == Some("av1"))
}

pub fn decide(args: &DecideArgs) -> EngineOutput {
    let route = &args.route;
    let depth = delivered_depth(&args.plan, args.versions.as_deref(), route);
    let (stage_output_depth, stage_reason) = stage_output(args, &depth);
    let mut decision = with_stage(
        decide_frames(args, &depth),
        stage_output_depth,
        stage_reason,
    );
    if stage_output_depth == 8 {
        // Phase 2: the stage itself rounds to 8 bits once, dithered; the engine gets NV12.
        decision.frames = "nv12";
        decision.target = "bgra";
        decision.reason = "console stage writes dithered NV12 (SDR 8-bit display)";
    }
    decision
}

fn with_stage(mut decision: EngineOutput, depth: u8, reason: &'static str) -> EngineOutput {
    decision.stage_output_depth = depth;
    decision.stage_reason = reason;
    decision
}

fn decide_frames(args: &DecideArgs, depth: &Depth) -> EngineOutput {
    let route = &args.route;
    let depth = *depth;
    // bale_gpu's stage: the tone map's inputs are all 10-bit; the SDR area downscale writes P010 for
    // 8-bit sources too (the rule's one exception). An old bale_gpu without P010 writes NV12.
    let mut decision = if route.console_tone_map {
        if args.stage_p010 {
            output("p010", "console tone map writes P010")
        } else {
            output(
                "nv12",
                "console tone map writes NV12 (bale_gpu without P010)",
            )
        }
    } else if route.console_downscale {
        if args.stage_p010 {
            output("p010", "console downscale writes P010")
        } else {
            output(
                "nv12",
                "console downscale writes NV12 (bale_gpu without P010)",
            )
        }
    } else {
        // Every other route keeps the delivered video's depth: the system decoder (Windows' source or
        // FFmpeg passthrough, DV base layers) and FFmpeg software decode output NV12 for 8-bit and P010
        // above 8 bits. Both dav1d CPU output on D3D11 and rav1g take NV12 for 8-bit AV1.
        match depth {
            Depth::Known { bits, .. } if bits > 8 => output("p010", "source above 8 bits"),
            Depth::Known { .. } => output("nv12", "8-bit source"),
            Depth::Hdr => output("p010", "HDR source (above 8 bits)"),
            Depth::Transcode => output("nv12", "server transcode (8-bit)"),
            Depth::Unknown => output("unknown", "unknown depth"),
        }
    };
    // Never raise NV12/unknown frames to rgb10, even for HDR or a deep display. Source mode is the
    // old presentation behaviour, including the old reasons, without changing any stage's output.
    if decision.frames != "p010" || args.ceiling == Ceiling::Source {
        return decision;
    }
    if !route.console_tone_map && matches!(depth, Depth::Hdr | Depth::Known { hdr: true, .. }) {
        decision.reason = "HDR passthrough: 10-bit output";
    } else if AV1_KEEPS_10_BIT_OUTPUT_UNTIL_RAV1G_IS_STABLE && is_av1(args) {
        decision.reason = "AV1 keeps 10-bit output (until rav1g is stable)";
    } else if args.display_bits_per_pixel.is_some_and(|bpp| bpp >= 30) {
        decision.reason = "SDR 10-bit display: 10-bit output";
    } else {
        decision.target = "bgra";
        decision.reason = "SDR 8-bit display: 8-bit output";
    }
    decision
}

#[derive(Clone, Copy)]
enum Depth {
    Known {
        bits: i64,
        hdr: bool,
    },
    /// No depth given, but HDR/Dolby Vision video needs more than 8 bits.
    Hdr,
    /// Silo's encoders write 8-bit yuv420p SDR (transcode.go `-pix_fmt yuv420p`).
    Transcode,
    Unknown,
}

fn delivered_depth(
    plan: &PlaybackPlan,
    versions: Option<&[FileVersion]>,
    route: &EngineRoute,
) -> Depth {
    let source = plan.source.as_ref();
    let recipe_video = normalize_codec(
        plan.effective_recipe
            .as_ref()
            .and_then(|r| r.video_codec.as_deref()),
    );
    let source_video = normalize_codec(source.and_then(|s| s.video_codec.as_deref()));
    let routed_video = normalize_codec(route.video_codec.as_deref());
    let copied = COPIED_VIDEO_DELIVERIES
        .iter()
        .any(|delivery| eq_ignore_case(plan.delivery.as_deref(), delivery))
        && (eq_ignore_case(plan.delivery.as_deref(), "original_http")
            || recipe_video.is_none()
            || recipe_video == source_video);
    let recipe_hdr = plan
        .effective_recipe
        .as_ref()
        .is_some_and(|r| is_hdr(r.dynamic_range.as_deref()));
    if !copied {
        // The server encoded this video: HDR kept through an encode is 10-bit; Silo's encodes are
        // otherwise 8-bit SDR. No delivery at all is unknown.
        return if recipe_hdr {
            Depth::Hdr
        } else if plan
            .delivery
            .as_deref()
            .is_some_and(|d| !d.trim().is_empty())
        {
            Depth::Transcode
        } else {
            Depth::Unknown
        };
    }
    // The routed codec must still be the source's, or the source's facts describe other video.
    if source_video.is_some() && routed_video.is_some() && source_video != routed_video {
        return Depth::Unknown;
    }
    let track = version_track(plan, versions);
    let depth = source
        .and_then(|s| s.bit_depth)
        .or(track.and_then(|t| t.bit_depth))
        .filter(|&d| d > 0)
        .or_else(|| pixel_format_depth(track.and_then(|t| t.pixel_format.as_deref())));
    let hdr = source.is_some_and(|s| {
        is_hdr(s.dynamic_range.as_deref())
            || s.dolby_vision_profile.is_some_and(|p| p > 0)
            || s.hdr10_plus == Some(true)
    }) || recipe_hdr
        || route.dolby_vision7_base_layer
        || track.is_some_and(|t| is_hdr(t.video_range.as_deref()));
    if let Some(bits) = depth {
        return Depth::Known { bits, hdr };
    }
    if hdr { Depth::Hdr } else { Depth::Unknown }
}

/// The catalog's one video track of the file actually delivered (as `tone_map` joins it).
fn version_track<'a>(
    plan: &PlaybackPlan,
    versions: Option<&'a [FileVersion]>,
) -> Option<&'a crate::models::VideoTrack> {
    let file_id = plan.effective_media_file_id.as_deref().or(plan
        .source
        .as_ref()
        .and_then(|s| s.media_file_id.as_deref()))?;
    let version = versions?
        .iter()
        .find(|v| v.file_id.as_deref() == Some(file_id))?;
    match version.video_tracks.as_deref() {
        Some([track]) => Some(track),
        _ => None,
    }
}

/// FFmpeg pixel format names: `...p10le`, `...p12be`, `p010le`, `p016le` are above 8 bits; the
/// plain 8-bit 4:2:0/4:2:2/4:4:4 ones are 8. Others are unknown.
fn pixel_format_depth(pixel_format: Option<&str>) -> Option<i64> {
    if is_blank(pixel_format) {
        return None;
    }
    let format = trim_lower(pixel_format)?;
    let name = format.trim_end_matches("le").trim_end_matches("be");
    if name.ends_with("p10") || name.ends_with("p12") || name.ends_with("p16") {
        return Some(if name.ends_with("p10") { 10 } else { 12 });
    }
    match name {
        "p010" => Some(10),
        "p016" => Some(12),
        "yuv420p" | "yuvj420p" | "yuv422p" | "yuvj422p" | "yuv444p" | "yuvj444p" | "nv12"
        | "gray" => Some(8),
        _ => None,
    }
}

fn is_hdr(dynamic_range: Option<&str>) -> bool {
    !matches!(
        trim_lower(dynamic_range).as_deref(),
        None | Some("" | "sdr")
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::models::{EffectiveRecipe, PlaybackSource, VideoTrack};

    fn plan(delivery: &str, codec: &str, depth: Option<i64>) -> PlaybackPlan {
        PlaybackPlan {
            delivery: Some(delivery.into()),
            effective_media_file_id: Some("f1".into()),
            source: Some(PlaybackSource {
                media_file_id: Some("f1".into()),
                video_codec: Some(codec.into()),
                bit_depth: depth,
                dynamic_range: Some("sdr".into()),
                ..Default::default()
            }),
            ..Default::default()
        }
    }

    fn route(codec: &str) -> EngineRoute {
        EngineRoute {
            use_ffmpeg: true,
            video_codec: Some(codec.into()),
            ..Default::default()
        }
    }

    fn run(plan: PlaybackPlan, route: EngineRoute, stage_p010: bool) -> EngineOutput {
        decide(&DecideArgs {
            plan,
            versions: None,
            route,
            stage_p010,
            stage_nv12_dithered: false,
            display_bits_per_pixel: Some(30),
            ceiling: Ceiling::Display,
        })
    }

    fn display_args(plan: PlaybackPlan, route: EngineRoute, bpp: Option<i32>) -> DecideArgs {
        DecideArgs {
            plan,
            route,
            versions: None,
            stage_p010: true,
            stage_nv12_dithered: false,
            display_bits_per_pixel: bpp,
            ceiling: Ceiling::Display,
        }
    }

    #[test]
    fn sdr_p010_is_capped_at_the_current_display_depth() {
        for bits in [10, 12] {
            for bpp in [None, Some(0), Some(24), Some(29), Some(30), Some(36)] {
                let args = display_args(
                    plan("original_http", "hevc", Some(bits)),
                    route("hevc"),
                    bpp,
                );
                let out = decide(&args);
                let (target, reason) = if bpp.is_some_and(|b| b >= 30) {
                    ("rgb10", "SDR 10-bit display: 10-bit output")
                } else {
                    ("bgra", "SDR 8-bit display: 8-bit output")
                };
                assert_eq!(
                    (out.target, out.frames, out.reason),
                    (target, "p010", reason)
                );
            }
        }
    }

    #[test]
    fn hdr_passthrough_is_rgb10_even_without_a_deep_current_sdr_mode() {
        for range in ["hdr10", "hlg", "dolby_vision"] {
            for bits in [None, Some(10)] {
                let mut p = plan("original_http", "hevc", bits);
                p.source.as_mut().unwrap().dynamic_range = Some(range.into());
                for bpp in [None, Some(24), Some(30)] {
                    let out = decide(&display_args(p.clone(), route("hevc"), bpp));
                    assert_eq!((out.target, out.frames), ("rgb10", "p010"));
                    assert_eq!(out.reason, "HDR passthrough: 10-bit output");
                }
            }
        }
    }

    #[test]
    fn tone_mapped_hdr_uses_the_sdr_display_ceiling() {
        for range in ["hdr10", "hlg"] {
            let mut p = plan("original_http", "hevc", Some(10));
            p.source.as_mut().unwrap().dynamic_range = Some(range.into());
            let mut tone = route("hevc");
            tone.console_tone_map = true;
            for (bpp, target) in [(None, "bgra"), (Some(24), "bgra"), (Some(30), "rgb10")] {
                let out = decide(&display_args(p.clone(), tone.clone(), bpp));
                assert_eq!((out.target, out.frames), (target, "p010"));
                assert!(out.reason.starts_with("SDR "));
            }
        }
    }

    #[test]
    fn av1_on_the_route_or_source_keeps_10_bit_output_until_rav1g_is_stable() {
        let mut no_route_codec = route("av1");
        no_route_codec.video_codec = None;
        let mut no_source_codec = plan("original_http", "hevc", Some(10));
        no_source_codec.source.as_mut().unwrap().video_codec = None;
        for (p, r) in [
            (plan("original_http", " AV1 ", Some(10)), route("av1")),
            (plan("original_http", "av1", Some(10)), no_route_codec),
            (no_source_codec, route(" AV1 ")),
        ] {
            let out = decide(&display_args(p, r, Some(24)));
            assert_eq!((out.target, out.frames), ("rgb10", "p010"));
            assert_eq!(
                out.reason,
                "AV1 keeps 10-bit output (until rav1g is stable)"
            );
        }
    }

    #[test]
    fn nv12_and_unknown_frames_never_get_raised_to_rgb10() {
        for ceiling in [Ceiling::Display, Ceiling::Source] {
            for codec in ["hevc", "av1"] {
                for bpp in [None, Some(24), Some(30)] {
                    for bits in [None, Some(8)] {
                        let mut args =
                            display_args(plan("original_http", codec, bits), route(codec), bpp);
                        args.ceiling = ceiling;
                        let out = decide(&args);
                        let frames = if bits.is_some() { "nv12" } else { "unknown" };
                        assert_eq!((out.target, out.frames), ("bgra", frames));
                    }
                }
            }
        }
        let mut p = plan("original_http", "hevc", Some(10));
        p.source.as_mut().unwrap().dynamic_range = Some("hdr10".into());
        let mut args = display_args(p, route("hevc"), Some(30));
        args.route.console_tone_map = true;
        args.stage_p010 = false;
        assert_eq!(decide(&args).target, "bgra");
        // Even contradictory HDR facts must not raise explicitly 8-bit frames.
        args.route.console_tone_map = false;
        args.plan.source.as_mut().unwrap().bit_depth = Some(8);
        assert_eq!(decide(&args).target, "bgra");
    }

    #[test]
    fn source_ceiling_keeps_the_previous_p010_targets_and_reasons() {
        let mut tone = route("hevc");
        tone.console_tone_map = true;
        let mut down = route("hevc");
        down.console_downscale = true;
        for (r, reason) in [
            (route("hevc"), "source above 8 bits"),
            (tone, "console tone map writes P010"),
            (down, "console downscale writes P010"),
        ] {
            let mut args = display_args(plan("original_http", "hevc", Some(10)), r, Some(24));
            args.ceiling = Ceiling::Source;
            let out = decide(&args);
            assert_eq!(
                (out.target, out.frames, out.reason),
                ("rgb10", "p010", reason)
            );
        }
    }

    #[test]
    fn missing_display_inputs_default_to_the_8_bit_sdr_ceiling() {
        let args = serde_json::from_value(serde_json::json!({
            "plan": { "delivery": "original_http", "source": { "video_codec": "hevc", "bit_depth": 10 } },
            "route": { "use_ffmpeg": true, "software_video": false, "video_codec": "hevc",
                "console_tone_map": false, "console_downscale": false, "dolby_vision7_base_layer": false },
            "stage_p010": true
        }))
        .unwrap();
        let out = decide(&args);
        assert_eq!((out.target, out.frames), ("bgra", "p010"));
    }

    #[test]
    fn system_decoder_follows_the_source_depth() {
        // FFmpeg system decoder passthrough and Windows' own MP4 source alike.
        let out = run(plan("original_http", "hevc", Some(10)), route("hevc"), true);
        assert_eq!((out.target, out.frames), ("rgb10", "p010"));
        let mut windows = route("h264");
        windows.use_ffmpeg = false;
        let out = run(plan("original_http", "h264", Some(8)), windows, true);
        assert_eq!((out.target, out.frames), ("bgra", "nv12"));
    }

    #[test]
    fn software_decode_follows_the_source_depth_av1_8_bit_included() {
        let mut software = route("av1");
        software.software_video = true;
        let ten = run(
            plan("original_http", "av1", Some(10)),
            software.clone(),
            true,
        );
        assert_eq!(ten.target, "rgb10");
        // dav1d CPU output and rav1g both deliver NV12 for 8-bit AV1.
        let eight = run(
            plan("original_http", "av1", Some(8)),
            software.clone(),
            true,
        );
        assert_eq!(eight.target, "bgra");
        software.video_codec = Some("hevc".into());
        let twelve = run(plan("original_http", "hevc", Some(12)), software, true);
        assert_eq!(twelve.target, "rgb10");
    }

    #[test]
    fn console_stages_follow_bale_gpu_output() {
        let mut tone = route("hevc");
        tone.console_tone_map = true;
        assert_eq!(
            run(plan("original_http", "hevc", Some(10)), tone.clone(), true).target,
            "rgb10"
        );
        assert_eq!(
            run(plan("original_http", "hevc", Some(10)), tone, false).target,
            "bgra"
        );
        // The downscale writes P010 for 8-bit sources too (the rule's exception).
        let mut down = route("vp9");
        down.console_downscale = true;
        let out = run(plan("original_http", "vp9", Some(8)), down.clone(), true);
        assert_eq!((out.target, out.frames), ("rgb10", "p010"));
        assert_eq!(
            run(plan("original_http", "vp9", Some(8)), down, false).target,
            "bgra"
        );
    }

    #[test]
    fn remuxes_keep_the_source_depth() {
        for delivery in [
            "server_remux_progressive",
            "server_remux_hls",
            "progressive",
        ] {
            let mut p = plan(delivery, "hevc", Some(10));
            p.effective_recipe = Some(EffectiveRecipe {
                video_codec: Some("hevc".into()),
                ..Default::default()
            });
            assert_eq!(run(p, route("hevc"), true).target, "rgb10", "{delivery}");
        }
    }

    #[test]
    fn transcodes_are_8_bit_unless_hdr_is_kept() {
        let mut p = plan("server_transcode_hls", "hevc", Some(10));
        p.effective_recipe = Some(EffectiveRecipe {
            video_codec: Some("h264".into()),
            dynamic_range: Some("sdr".into()),
            ..Default::default()
        });
        let out = run(p.clone(), route("h264"), true);
        assert_eq!((out.target, out.frames), ("bgra", "nv12"));
        // A remux delivery whose recipe re-encodes the video is a transcode too.
        p.delivery = Some("server_remux_progressive".into());
        assert_eq!(run(p.clone(), route("h264"), true).target, "bgra");
        p.effective_recipe.as_mut().unwrap().dynamic_range = Some("hdr10".into());
        p.effective_recipe.as_mut().unwrap().video_codec = Some("hevc".into());
        p.source.as_mut().unwrap().video_codec = Some("av1".into());
        assert_eq!(run(p, route("hevc"), true).target, "rgb10");
    }

    #[test]
    fn missing_depth_falls_back_to_the_catalog_then_hdr_then_unknown() {
        let mut p = plan("original_http", "hevc", None);
        let versions = vec![FileVersion {
            file_id: Some("f1".into()),
            video_tracks: Some(vec![VideoTrack {
                pixel_format: Some("yuv420p10le".into()),
                ..Default::default()
            }]),
            ..Default::default()
        }];
        let out = decide(&DecideArgs {
            plan: p.clone(),
            versions: Some(versions),
            route: route("hevc"),
            stage_p010: true,
            stage_nv12_dithered: false,
            display_bits_per_pixel: Some(30),
            ceiling: Ceiling::Display,
        });
        assert_eq!(out.target, "rgb10");
        let out = run(p.clone(), route("hevc"), true);
        assert_eq!((out.target, out.frames), ("bgra", "unknown"));
        // HDR10 passthrough on an HDR display: never worse than bgra.
        p.source.as_mut().unwrap().dynamic_range = Some("hdr10".into());
        assert_eq!(run(p, route("hevc"), true).target, "rgb10");
    }

    #[test]
    fn a_routed_codec_other_than_the_source_is_unknown() {
        let out = run(plan("original_http", "hevc", Some(10)), route("h264"), true);
        assert_eq!((out.target, out.frames), ("bgra", "unknown"));
    }

    fn stage_args(
        codec: &str,
        bits: i64,
        range: &str,
        tone_map: bool,
        bpp: Option<i32>,
    ) -> DecideArgs {
        let mut p = plan("original_http", codec, Some(bits));
        p.source.as_mut().unwrap().dynamic_range = Some(range.into());
        let mut r = route(codec);
        r.console_tone_map = tone_map;
        r.console_downscale = !tone_map;
        let mut args = display_args(p, r, bpp);
        args.stage_nv12_dithered = true;
        args
    }

    fn stage(args: &DecideArgs) -> (&'static str, &'static str, u8, &'static str) {
        let out = decide(args);
        (
            out.target,
            out.frames,
            out.stage_output_depth,
            out.stage_reason,
        )
    }

    const DITHERED: &str = "console stage writes dithered NV12 (SDR 8-bit display)";

    #[test]
    fn eight_bit_sdr_display_gets_dithered_nv12_from_both_stages() {
        for (codec, bits, range, tone_map) in [
            ("hevc", 10, "hdr10", true),
            ("hevc", 10, "hlg", true),
            ("vp9", 10, "hdr10", true),
            ("hevc", 8, "sdr", false),
            ("vp9", 10, "sdr", false),
        ] {
            for bpp in [Some(24), Some(29), Some(0), None] {
                let args = stage_args(codec, bits, range, tone_map, bpp);
                let out = decide(&args);
                assert_eq!(
                    (out.target, out.frames, out.reason),
                    ("bgra", "nv12", DITHERED),
                    "{codec} {range} {bpp:?}"
                );
                assert_eq!(
                    (out.stage_output_depth, out.stage_reason),
                    (8, "SDR 8-bit display")
                );
            }
        }
    }

    #[test]
    fn a_10_bit_sdr_display_keeps_p010_from_the_stage() {
        for bpp in [30, 36] {
            for tone_map in [true, false] {
                let args = stage_args(
                    "hevc",
                    10,
                    if tone_map { "hdr10" } else { "sdr" },
                    tone_map,
                    Some(bpp),
                );
                assert_eq!(stage(&args), ("rgb10", "p010", 10, "SDR 10-bit display"));
            }
        }
    }

    #[test]
    fn hdr_passthrough_never_asks_the_stage_for_8_bits() {
        // A downscale route over HDR video (not tone-mapped) is passthrough: never 8-bit.
        for range in ["hdr10", "dolby_vision"] {
            let args = stage_args("hevc", 10, range, false, Some(24));
            let (target, frames, depth, reason) = stage(&args);
            assert_eq!((frames, depth, reason), ("p010", 10, "HDR passthrough"));
            assert_eq!(target, "rgb10");
        }
    }

    #[test]
    fn av1_keeps_the_10_bit_stage_until_rav1g_is_stable() {
        for tone_map in [true, false] {
            let range = if tone_map { "hdr10" } else { "sdr" };
            let args = stage_args(" AV1 ", 10, range, tone_map, Some(24));
            let out = decide(&args);
            assert_eq!(
                (
                    out.target,
                    out.frames,
                    out.stage_output_depth,
                    out.stage_reason
                ),
                (
                    "rgb10",
                    "p010",
                    10,
                    "AV1 keeps 10-bit output (until rav1g is stable)"
                )
            );
            // AV1 known only from the source (route codec missing) counts too.
            let mut source_only = stage_args("av1", 10, range, tone_map, Some(24));
            source_only.route.video_codec = None;
            assert_eq!(decide(&source_only).stage_output_depth, 10);
        }
    }

    #[test]
    fn without_the_dither_bit_the_stage_output_is_unchanged() {
        let mut args = stage_args("hevc", 10, "hdr10", true, Some(24));
        args.stage_nv12_dithered = false;
        let out = decide(&args);
        assert_eq!(
            (
                out.target,
                out.frames,
                out.reason,
                out.stage_output_depth,
                out.stage_reason
            ),
            (
                "bgra",
                "p010",
                "SDR 8-bit display: 8-bit output",
                10,
                "bale_gpu without dithered NV12"
            )
        );
    }

    #[test]
    fn debug_source_mode_keeps_p010_from_the_stage() {
        for tone_map in [true, false] {
            let range = if tone_map { "hdr10" } else { "sdr" };
            let mut args = stage_args("hevc", 10, range, tone_map, Some(24));
            args.ceiling = Ceiling::Source;
            let (target, frames, depth, reason) = stage(&args);
            assert_eq!(
                (target, frames, depth, reason),
                ("rgb10", "p010", 10, "debug output-depth source keeps P010")
            );
        }
    }

    #[test]
    fn routes_without_a_stage_report_no_stage_depth() {
        let mut args = display_args(
            plan("original_http", "hevc", Some(10)),
            route("hevc"),
            Some(24),
        );
        args.stage_nv12_dithered = true;
        let out = decide(&args);
        assert_eq!(
            (
                out.target,
                out.frames,
                out.stage_output_depth,
                out.stage_reason
            ),
            ("bgra", "p010", 0, "no console stage")
        );
        // Older callers omit the bit: serde defaults it to false (no 8-bit request).
        let args: DecideArgs = serde_json::from_value(serde_json::json!({
            "plan": { "delivery": "original_http", "source": { "video_codec": "hevc", "bit_depth": 10 } },
            "route": { "use_ffmpeg": true, "software_video": false, "video_codec": "hevc",
                "console_tone_map": true, "console_downscale": false, "dolby_vision7_base_layer": false },
            "stage_p010": true, "display_bits_per_pixel": 24
        }))
        .unwrap();
        assert_eq!(decide(&args).stage_output_depth, 10);
        let json = serde_json::to_value(decide(&args)).unwrap();
        assert_eq!(json["stage_output_depth"], 10);
        assert_eq!(json["stage_reason"], "bale_gpu without dithered NV12");
    }

    #[test]
    fn pixel_format_depths() {
        assert_eq!(pixel_format_depth(Some("yuv420p10le")), Some(10));
        assert_eq!(pixel_format_depth(Some("P010LE")), Some(10));
        assert_eq!(pixel_format_depth(Some("yuv444p12le")), Some(12));
        assert_eq!(pixel_format_depth(Some("yuv420p")), Some(8));
        assert_eq!(pixel_format_depth(Some("rgb24")), None);
        assert_eq!(pixel_format_depth(None), None);
    }
}
