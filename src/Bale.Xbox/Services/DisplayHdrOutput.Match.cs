#if DEBUG_SERVER
using System.Globalization;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Windows.Graphics.Display;
using Windows.Graphics.Display.Core;

namespace Bale.Xbox.Services;

internal sealed partial class DisplayHdrOutput
{
    private (long Width, long Height, double MilliFrameRate, bool Hdr10)? _lastMatch;

    internal async Task<bool> TryMatchContentAsync(PlaybackPlan plan)
    {
        var hdr10 = DisplayHdrPolicy.RequiresHdr10(plan);
        // ResolveFrameRate uses the recipe/source for HLS, never its placeholder native track FPS.
        var frameRate = PlaybackStats.FromPlan(plan).FrameRate;
        var original = string.Equals(plan.Delivery, "original_http", StringComparison.OrdinalIgnoreCase);
        var width = original ? plan.Source?.Width : plan.EffectiveRecipe?.Width ?? plan.Source?.Width;
        var height = original ? plan.Source?.Height : plan.EffectiveRecipe?.Height ?? plan.Source?.Height;
        if (frameRate is not { } fps || !double.IsFinite(fps) || fps <= 0 || width is not > 0 || height is not > 0)
        {
            _lastMatch = null;
            Log.Info(frameRate is not > 0 || !double.IsFinite(frameRate.Value)
                ? "display match: skipped (unknown frame rate)" : "display match: skipped (unknown video size)");
            // Unknown timing must not disable the existing production HDR negotiation.
            if (hdr10) return await TryEnableHdr10Async();
            await RestoreAsync();
            return true;
        }
        // HdrDisplayController.kt coalesces WxH@fps*1000. Include HDR so a new output range is not skipped.
        var signature = (width.Value, height.Value, Math.Truncate(fps * 1000), hdr10);
        if (_lastMatch == signature) return true;
        _lastMatch = null; // A new request may partially change HDMI even when it fails.
        var switched = false;
        var applied = false;
        var picked = "none";
        try
        {
            var hdmi = HdmiDisplayInformation.GetForCurrentView();
            var current = hdmi?.GetCurrentDisplayMode();
            if (hdmi is not null && current is not null)
            {
                var modes = hdmi.GetSupportedDisplayModes().ToArray();
                var index = DisplayMatchPolicy.PickMode(Facts(current), modes.Select(Facts).ToArray(),
                    width.Value, height.Value, fps, hdr10);
                if (index is { } selected)
                {
                    var mode = modes[selected];
                    picked = ModeText(mode);
                    // PQ support is not active HDR. The same transport may still need its HDR option changed.
                    if (mode.IsEqual(current) && (!hdr10 || CurrentHdr() == true)) applied = true;
                    else
                    {
                        _hdmi = hdmi;
                        // A false/throwing request may have changed HDMI; share every existing restore path.
                        _restoreRequired = true;
                        applied = switched = await hdmi.RequestSetCurrentDisplayModeAsync(mode,
                            hdr10 ? HdmiDisplayHdrOption.Eotf2084 : HdmiDisplayHdrOption.None);
                    }
                }
            }
        }
        catch { }
        if (applied) _lastMatch = signature;
        Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"display match: content={fps:0.###} {width.Value}x{height.Value} picked={picked} switched={switched}"));
        return applied;
    }

    internal static string CurrentModeText()
    {
        try
        {
            if (HdmiDisplayInformation.GetForCurrentView()?.GetCurrentDisplayMode() is { } current)
            {
                var range = CurrentHdr() switch { true => "HDR", false => "SDR", null => "HDR/SDR unavailable" };
                return ModeText(current) + "Hz · " + range;
            }
        }
        catch { }
        return "unavailable";
    }

    private static string ModeText(HdmiDisplayMode mode) => string.Create(CultureInfo.InvariantCulture,
        $"{mode.ResolutionWidthInRawPixels}x{mode.ResolutionHeightInRawPixels}@{mode.RefreshRate:0.###}");

    private static bool? CurrentHdr()
    {
        try
        {
            // Do not label a PQ-capable SDR mode as HDR merely because it supports SMPTE 2084.
            var color = DisplayInformation.GetForCurrentView().GetAdvancedColorInfo();
            return color is null ? null : color.CurrentAdvancedColorKind == AdvancedColorKind.HighDynamicRange;
        }
        catch { return null; }
    }
}
#endif
