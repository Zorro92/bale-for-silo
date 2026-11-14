#if DEBUG_SERVER
using Bale.Core.Api.Models;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>Memory-only SDR tone-mapping comparison; capture before planning, never in production.</summary>
internal static class DebugHdrPlayback
{
    private static string _mode = "off";

    internal static string Mode => Volatile.Read(ref _mode);
    internal static bool Pretend => Mode == "pretend";

    internal static bool TrySet(string? mode)
    {
        if (mode is not ("pretend" or "off")) return false;
        Interlocked.Exchange(ref _mode, mode);
        return true;
    }

    internal static DeviceCapabilities Capabilities(bool pretend, DeviceCapabilities capabilities) => !pretend
        ? capabilities
        : capabilities with { Hdr = new() { Hdr10 = true }, DisplayHdrEvidence = "exact" };

    internal static string Output(bool pretend, PlaybackPlan? plan, bool consoleToneMap = false)
    {
        if (plan is null) return pretend ? "console tone map (debug; awaiting plan)" : "detected (awaiting plan)";
        var output = consoleToneMap ? "SDR / console tone map"
        : DisplayHdrPolicy.RequiresHdr10(plan)
            ? pretend ? "console tone map (debug)" : "HDR10 HDMI"
            : pretend ? "SDR (HDR pretend debug armed)" : "SDR / server tone map";
        return DolbyVision7Policy.RequiresBaseLayer(plan) ? output + " · Dolby Vision 7 → HDR10 base layer" : output;
    }
}
#endif
