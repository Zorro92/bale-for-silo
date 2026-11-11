using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>
/// The production capability claims, shared by the player and Auto's display resolution matching
/// so the detail readout picks the file the player's claims can play. Debug overrides stay in
/// PlayerPage. Call on the UI thread: the HDMI modes belong to the view.
/// </summary>
internal static class PlaybackClaims
{
    private static string? _loggedMatch;

    /// <summary>The connected display's HDR claims plus the decode claims this console backs.</summary>
    internal static (DeviceCapabilities Capabilities, bool SoftwareVideo) Detect()
    {
        var capabilities = DisplayHdrOutput.DetectCapabilities();
        var softwareVideo = SoftwareVideo();
        return (softwareVideo ? capabilities : SoftwareDecodePolicy.WithoutSoftwareVideo(capabilities), softwareVideo);
    }

    /// <summary>
    /// Sets <see cref="DisplayVersionMatch.Current"/> from the connected display, at launch and
    /// whenever a detail page opens (the TV can change). Display resolution matching is always on
    /// until the 0.2.x settings menu adds its toggle; off will leave Current null here. Unreadable
    /// display modes also leave it null, which is the official highest-first Auto.
    /// </summary>
    internal static void RefreshVersionMatch()
    {
        DisplayVersionMatch? match = null;
        try
        {
            // The decode claims don't depend on the display, so this skips the HDR detection.
            var claims = SoftwareVideo() ? ConsoleEvidence.Default : SoftwareDecodePolicy.WithoutSoftwareVideo(ConsoleEvidence.Default);
            match = DisplayHdrOutput.DetectVersionMatch(claims.VideoDecode);
        }
        catch
        {
            // Fall back to the official Auto; a version choice must never stop a page from opening.
        }

        DisplayVersionMatch.Current = match;
        var line = match is null ? "version match: off (no display modes)"
            : $"version match: display={match.DisplayMaxHeight}p claims={match.VideoDecode.Count}";
        // One line per change, not one per detail page.
        if (line == _loggedMatch) return;
        _loggedMatch = line;
        Log.Info(line);
    }

    /// <summary>
    /// Software AV1 has one path (zero-copy D3D12). Xbox One (no AVX2), or a console where
    /// bale_gpu.dll can't register d3d12.dll, claims no software AV1, so the server transcodes it.
    /// </summary>
    private static bool SoftwareVideo() => DeviceIdentity.SoftwareVideoSupported && ZeroCopyRuntime.Register();
}
