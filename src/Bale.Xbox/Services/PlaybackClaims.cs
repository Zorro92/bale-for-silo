using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>
/// The production capability claims, shared by the player and Auto's display resolution matching
/// so the detail readout picks the file the player's claims can play. Hardware decode claims
/// follow the console's decoder probe (HardwareDecodeProbe), which runs once per process off the
/// UI thread. Debug overrides stay in PlayerPage. Call on the UI thread: the HDMI modes belong to
/// the view.
/// </summary>
internal static class PlaybackClaims
{
    private static string? _loggedMatch;

    /// <summary>
    /// The connected display's HDR claims plus the decode claims this console backs. Waits for
    /// the decoder probe if it is still running, then continues on the calling (UI) thread.
    /// </summary>
    internal static async Task<(DeviceCapabilities Capabilities, bool SoftwareVideo)> DetectAsync()
    {
        var hardware = await HardwareDecodeProbe.Limits;
        var capabilities = DisplayHdrOutput.DetectCapabilities(ConsoleEvidence.ForHardware(hardware));
        var softwareVideo = SoftwareVideo();
        return (softwareVideo ? capabilities : SoftwareDecodePolicy.WithoutSoftwareVideo(capabilities), softwareVideo);
    }

    /// <summary>
    /// Sets <see cref="DisplayVersionMatch.Current"/> from the connected display, at launch and
    /// whenever a detail page opens (the TV can change). Display resolution matching is always on
    /// until the 0.2.x settings menu adds its toggle; off will leave Current null here. Unreadable
    /// display modes also leave it null, which is the official highest-first Auto.
    /// The launch call starts the decoder probe; until it finishes (well before a detail page can
    /// open) Current keeps its last value, and the match is then set on this thread.
    /// </summary>
    internal static void RefreshVersionMatch()
    {
        var probe = HardwareDecodeProbe.Limits;
        if (probe.IsCompleted) SetVersionMatch(probe.Result);
        else _ = SetVersionMatchWhenProbedAsync(probe);
    }

    private static async Task SetVersionMatchWhenProbedAsync(Task<HardwareDecodeLimits?> probe) =>
        SetVersionMatch(await probe);

    private static void SetVersionMatch(HardwareDecodeLimits? hardware)
    {
        DisplayVersionMatch? match = null;
        try
        {
            // The decode claims don't depend on the display, so this skips the HDR detection.
            var claims = ConsoleEvidence.ForHardware(hardware);
            if (!SoftwareVideo()) claims = SoftwareDecodePolicy.WithoutSoftwareVideo(claims);
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
