using Bale.Core.Api.Models;
using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <summary>
/// The original-file executor's bounded HDR-to-SDR promise, never a sink-HDR claim. Implemented in
/// Rust (native/bale-core/src/tone_map.rs).
/// </summary>
public static class ConsoleToneMapPolicy
{
    public static bool HasHdrOutput(HdrCapabilities hdr) =>
        BaleCoreNative.Call("tone_map.has_hdr_output", hdr, BaleCoreJson.Default.HdrCapabilities, BaleCoreJson.Default.Boolean);

    /// <summary>
    /// bale.29 adds HLG eligibility; earlier stages only promised PQ. Keep the whole production
    /// claim behind that revision and a runtime ABI/export check in the Xbox app.
    /// </summary>
    public static bool WrapperSupported(string version) =>
        BaleCoreNative.Call("tone_map.wrapper_supported", version, BaleCoreJson.Default.String, BaleCoreJson.Default.Boolean);

    /// <summary>bale.39 adds the SDR downscale route (BT.709 SDR through bale_gpu's scale-only stage).</summary>
    public static bool SdrDownscaleWrapperSupported(string version) =>
        BaleCoreNative.Call("tone_map.sdr_downscale_wrapper_supported", version, BaleCoreJson.Default.String, BaleCoreJson.Default.Boolean);

    /// <summary>
    /// bale.80 runs the stage on FFmpeg software frames too (tone map, downscale, output depth, white nits), through a
    /// staged D3D11 upload (docs/software-decode.md phase B).
    /// </summary>
    public static bool SoftwareStageWrapperSupported(string version) =>
        BaleCoreNative.Call("tone_map.software_stage_wrapper_supported", version, BaleCoreJson.Default.String, BaleCoreJson.Default.Boolean);

    /// <summary>bale.38 adds the display-sized tone-map output keys.</summary>
    public static bool DownscaleWrapperSupported(string version) =>
        BaleCoreNative.Call("tone_map.downscale_wrapper_supported", version, BaleCoreJson.Default.String, BaleCoreJson.Default.Boolean);

    /// <summary>
    /// Whether the tone-map stage should output display-sized video: the video is larger than the
    /// current display mode. On Xbox, MF's video processor leaks driver state per open when it scales
    /// 4K SDR down itself; bale_gpu's area downscale in linear light avoids it (40/40 opens).
    /// </summary>
    public static bool DownscaleToDisplay(long? videoWidth, long? videoHeight, int? displayWidth, int? displayHeight) =>
        BaleCoreNative.Call("tone_map.downscale_to_display", writer =>
        {
            writer.WriteNumber("video_width", videoWidth ?? 0);
            writer.WriteNumber("video_height", videoHeight ?? 0);
            writer.WriteNumber("display_width", displayWidth ?? 0);
            writer.WriteNumber("display_height", displayHeight ?? 0);
        }, BaleCoreJson.Default.Boolean);

    /// <summary>
    /// Whether the console tone map can take this version on an SDR display: a hardware-decoded
    /// 10-bit 4:2:0 HEVC/VP9 PQ or HLG track with BT.2020 NCL (PQ also BT.709) colour, and for Dolby
    /// Vision only a profile 7 HDR10 base layer or a profile 8.1/8.4 base layer. With
    /// <see cref="DeviceCapabilities.SoftwareDynamicRangeMappingSupported"/> (phase B) also a 10/12-bit
    /// H.264/HEVC/VP9 PQ or HLG track of any chroma inside a claimed software envelope, never Dolby Vision.
    /// </summary>
    public static bool CanManage(FileVersion version, DeviceCapabilities device) =>
        BaleCoreNative.Call("tone_map.can_manage", writer =>
        {
            writer.Server("version", version);
            writer.Record("device", device, BaleCoreJson.Default.DeviceCapabilities);
        }, BaleCoreJson.Default.Boolean);

    /// <summary>
    /// Protect even unexpected HDR video the server did not re-encode (the original file or the forward-only
    /// remux); never let it reach an SDR display unmapped.
    /// </summary>
    /// <param name="hlsThroughFfmpeg">The HLS remux plays through the Rust reader and FFmpeg, so it can be mapped too.</param>
    public static bool RequiresToneMap(PlaybackPlan plan, bool sdrOutput, IReadOnlyList<FileVersion>? versions = null,
        bool hlsThroughFfmpeg = false) =>
        BaleCoreNative.Call("tone_map.requires_tone_map", writer =>
        {
            writer.Server("plan", plan);
            writer.WriteBoolean("sdr_output", sdrOutput);
            writer.Server("versions", versions);
            writer.WriteBoolean("hls_through_ffmpeg", hlsThroughFfmpeg);
        }, BaleCoreJson.Default.Boolean);
}
