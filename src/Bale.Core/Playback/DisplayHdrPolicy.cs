using Bale.Core.Api.Models;
using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <summary>HDMI facts only; no WinRT dependency so capability and mode selection stay testable.</summary>
public sealed record DisplayModeFacts(bool Smpte2084, bool Metadata2086, bool Stereo)
{
    public uint Width { get; init; }
    public uint Height { get; init; }
    public double RefreshRate { get; init; }
}

/// <summary>Implemented in Rust (native/bale-core/src/display.rs).</summary>
public static class DisplayHdrPolicy
{
    /// <summary>
    /// HDR10 when any non-stereo mode carries SMPTE 2084; HDR10+, HLG and Dolby Vision stay
    /// unclaimed (Xbox Dolby Vision is low-latency profile 5 only). Evidence is "exact" when the
    /// modes could be read.
    /// </summary>
    public static (HdrCapabilities Hdr, string Evidence) FromModes(bool readSucceeded, IReadOnlyList<DisplayModeFacts> modes)
    {
        var result = BaleCoreNative.Call("display.from_modes", writer =>
        {
            writer.WriteBoolean("read_succeeded", readSucceeded);
            writer.Records("modes", modes, BaleCoreJson.Default.DisplayModeFacts);
        }, BaleCoreJson.Default.DisplayHdr);
        return (result.Hdr, result.Evidence);
    }

    /// <summary>
    /// Returns an index into the supported modes, never a synthesized HDMI mode: the smallest HDR10
    /// resolution that holds the current one, then the closest refresh (at most 60 Hz, never stereo).
    /// </summary>
    public static int? PickHdr10Mode(DisplayModeFacts current, IReadOnlyList<DisplayModeFacts> modes) =>
        BaleCoreNative.Call("display.pick_hdr10_mode", writer =>
        {
            writer.Record("current", current, BaleCoreJson.Default.DisplayModeFacts);
            writer.Records("modes", modes, BaleCoreJson.Default.DisplayModeFacts);
        }, BaleCoreJson.Default.NullableInt32);

    /// <summary>A direct or remux delivery whose effective range is HDR10, or a DV7 base layer.</summary>
    public static bool RequiresHdr10(PlaybackPlan plan) =>
        BaleCoreNative.Call("display.requires_hdr10", writer => writer.Server("plan", plan), BaleCoreJson.Default.Boolean);
}
