using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

/// <summary>HDMI facts only; no WinRT dependency so capability and mode selection stay testable.</summary>
public sealed record DisplayModeFacts(bool Smpte2084, bool Metadata2086, bool Stereo)
{
    public uint Width { get; init; }
    public uint Height { get; init; }
    public double RefreshRate { get; init; }
}

public static class DisplayHdrPolicy
{
    public static (HdrCapabilities Hdr, string Evidence) FromModes(bool readSucceeded, IReadOnlyList<DisplayModeFacts> modes)
    {
        // HDR10+, HLG and Dolby Vision are unverified. Xbox Dolby Vision is low-latency DV
        // (profile 5) only; revisit later rather than claiming support from the HDMI flag.
        var hdr = new HdrCapabilities { Hdr10 = readSucceeded && modes.Any(mode => !mode.Stereo && mode.Smpte2084) };
        return (hdr, readSucceeded ? "exact" : "unknown");
    }

    /// <summary>Return an index into the supported modes, never a synthesized HDMI mode.</summary>
    public static int? PickHdr10Mode(DisplayModeFacts current, IReadOnlyList<DisplayModeFacts> modes)
    {
        if (current.Width == 0 || current.Height == 0 || !double.IsFinite(current.RefreshRate) || current.RefreshRate <= 0)
            return null;
        // Keep the current resolution/refresh when possible. Otherwise prefer the smallest
        // resolution that holds it, then the closest refresh; never shrink or enter stereo.
        // HdrDisplayController.kt also prioritizes matching/holding resolution over refresh.
        return modes.Select((mode, index) => (Mode: mode, Index: index))
            .Where(pair => !pair.Mode.Stereo && pair.Mode.Smpte2084
                && pair.Mode.Width >= current.Width && pair.Mode.Height >= current.Height
                && double.IsFinite(pair.Mode.RefreshRate) && pair.Mode.RefreshRate > 0)
            .OrderBy(pair => (ulong)pair.Mode.Width * pair.Mode.Height)
            .ThenBy(pair => Math.Abs(pair.Mode.RefreshRate - current.RefreshRate))
            .ThenByDescending(pair => pair.Mode.Metadata2086)
            .Select(pair => (int?)pair.Index).FirstOrDefault();
    }

    public static bool RequiresHdr10(PlaybackPlan plan)
    {
        // Direct and remux deliveries preserve the picture; a tone-mapped HLS plan must not
        // inherit the original source's HDR range. Follow the effective output when provided.
        if (plan.Delivery?.ToLowerInvariant() is not ("original_http" or "progressive" or "server_remux_progressive")
            || string.Equals(plan.Stream?.Protocol, "hls", StringComparison.OrdinalIgnoreCase)) return false;
        var range = string.IsNullOrWhiteSpace(plan.EffectiveRecipe?.DynamicRange)
            ? plan.Source?.DynamicRange : plan.EffectiveRecipe.DynamicRange;
        // The server can also validate HDR10 base-layer playback of HDR10+ / legacy hdr_unknown.
        return string.Equals(range?.Trim(), "hdr10", StringComparison.OrdinalIgnoreCase) || plan.Claims?.Video?.Hdr10 == true;
    }
}
