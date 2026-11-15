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

    /// <summary>
    /// Whether the console tone map can take this version on an SDR display: a hardware-decoded
    /// 10-bit 4:2:0 HEVC/VP9 PQ or HLG track with BT.2020 NCL (PQ also BT.709) colour, and for Dolby
    /// Vision only a profile 7 HDR10 base layer or a profile 8.1/8.4 base layer.
    /// </summary>
    public static bool CanManage(FileVersion version, DeviceCapabilities device) =>
        BaleCoreNative.Call("tone_map.can_manage", writer =>
        {
            writer.Server("version", version);
            writer.Record("device", device, BaleCoreJson.Default.DeviceCapabilities);
        }, BaleCoreJson.Default.Boolean);

    /// <summary>Protect even unexpected HDR originals; never let the native container source present them on SDR.</summary>
    public static bool RequiresToneMap(PlaybackPlan plan, bool sdrOutput, IReadOnlyList<FileVersion>? versions = null) =>
        BaleCoreNative.Call("tone_map.requires_tone_map", writer =>
        {
            writer.Server("plan", plan);
            writer.WriteBoolean("sdr_output", sdrOutput);
            writer.Server("versions", versions);
        }, BaleCoreJson.Default.Boolean);
}
