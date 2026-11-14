using Bale.Core.Detail;
using Bale.Core.Playback;
using Windows.Graphics.Display.Core;

namespace Bale.Xbox.Services;

/// <summary>View-scoped HDMI operations. Call on the UI thread, serialized by the player's lifecycle.</summary>
internal sealed partial class DisplayHdrOutput
{
    private HdmiDisplayInformation? _hdmi;
    private bool _restoreRequired;

    /// <summary>The given claims (default: the fixed baseline) with the connected display's HDR evidence.</summary>
    internal static DeviceCapabilities DetectCapabilities(DeviceCapabilities? claims = null)
    {
        var read = ReadSupportedModes();
        var succeeded = read is { Length: > 0 };
        var modes = read ?? [];
        var (hdr, evidence) = DisplayHdrPolicy.FromModes(succeeded, modes);
        Log.Info($"display HDR: read={succeeded} modes={modes.Length} pq={modes.Count(mode => mode.Smpte2084)}"
            + $" metadata={modes.Count(mode => mode.Metadata2086)} stereo={modes.Count(mode => mode.Stereo)} hdr10={hdr.Hdr10}");
        return (claims ?? ConsoleEvidence.Default) with { Hdr = hdr, DisplayHdrEvidence = evidence };
    }

    /// <summary>
    /// Auto's display resolution matching for the connected display's largest HDMI mode and these
    /// decode claims. Null, the official highest-first Auto, when the modes can't be read or there
    /// are none. Reads only; never changes the display.
    /// </summary>
    internal static DisplayVersionMatch? DetectVersionMatch(IReadOnlyList<VideoDecodeCapability> videoDecode) =>
        ReadSupportedModes() is { } modes ? DisplayVersionMatch.FromModes(modes, videoDecode) : null;

    /// <summary>The display's supported HDMI modes; null when there is no HDMI view or the read fails.</summary>
    private static DisplayModeFacts[]? ReadSupportedModes()
    {
        try
        {
            return HdmiDisplayInformation.GetForCurrentView()?.GetSupportedDisplayModes().Select(Facts).ToArray();
        }
        catch
        {
            // Fail closed even if enumeration/property access stopped after some modes.
            return null;
        }
    }

    internal async Task<bool> TryEnableHdr10Async()
    {
        var switched = false;
        try
        {
            var hdmi = HdmiDisplayInformation.GetForCurrentView();
            var current = hdmi?.GetCurrentDisplayMode();
            if (hdmi is not null && current is not null)
            {
                // Re-read at mount time: a display/settings change can invalidate the start snapshot.
                var modes = hdmi.GetSupportedDisplayModes().ToArray();
                var index = DisplayHdrPolicy.PickHdr10Mode(Facts(current), modes.Select(Facts).ToArray());
                if (index is { } selected)
                {
                    _hdmi = hdmi;
                    // Even a false result/exception might have partially changed HDMI. Always restore.
                    _restoreRequired = true;
                    switched = await hdmi.RequestSetCurrentDisplayModeAsync(modes[selected], HdmiDisplayHdrOption.Eotf2084);
                }
            }
        }
        catch { }
        Log.Info($"display HDR: switched={switched}");
        return switched;
    }

    internal async Task RestoreAsync()
    {
#if DEBUG_SERVER
        _lastMatch = null;
#endif
        if (!_restoreRequired || _hdmi is not { } hdmi) return;
        var restored = false;
        try
        {
            // HdrDisplayController.kt restores on dismissal; also restore before any fallback.
            await hdmi.SetDefaultDisplayModeAsync();
            _restoreRequired = false;
            _hdmi = null;
            restored = true;
        }
        catch { } // Retain restore authority so the next teardown can retry.
        Log.Info($"display HDR: restored={restored}");
    }

    private static DisplayModeFacts Facts(HdmiDisplayMode mode) => new(mode.IsSmpte2084Supported,
        mode.Is2086MetadataSupported, mode.StereoEnabled)
    {
        Width = mode.ResolutionWidthInRawPixels,
        Height = mode.ResolutionHeightInRawPixels,
        RefreshRate = mode.RefreshRate,
    };
}
