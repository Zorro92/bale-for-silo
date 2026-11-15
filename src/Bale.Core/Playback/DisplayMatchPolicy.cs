using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <summary>
/// Content-resolution/refresh selection, independent of WinRT and HDMI requests. Implemented in Rust
/// (native/bale-core/src/display.rs).
/// </summary>
public static class DisplayMatchPolicy
{
    /// <summary>
    /// Returns an index into the supported modes, never a synthesized HDMI mode: the exact
    /// resolution, else one that holds it, else the largest (HdrDisplayController.kt); then integer
    /// refresh multiples over other cadences, the closest refresh, 16:9, area and the current mode
    /// (RefreshRateSelector.kt). At most 60 Hz, never stereo, never losing required PQ.
    /// </summary>
    public static int? PickMode(DisplayModeFacts current, IReadOnlyList<DisplayModeFacts> modes,
        long videoWidth, long videoHeight, double frameRate, bool requiresHdr10 = false) =>
        BaleCoreNative.Call("display.pick_mode", writer =>
        {
            writer.Record("current", current, BaleCoreJson.Default.DisplayModeFacts);
            writer.Records("modes", modes, BaleCoreJson.Default.DisplayModeFacts);
            writer.WriteNumber("video_width", videoWidth);
            writer.WriteNumber("video_height", videoHeight);
            writer.Double("frame_rate", frameRate);
            writer.WriteBoolean("requires_hdr10", requiresHdr10);
        }, BaleCoreJson.Default.NullableInt32);
}
