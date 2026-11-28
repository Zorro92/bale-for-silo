using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>
/// The production capability claims, shared by the player and Auto's display resolution matching
/// so the detail readout picks the file the player's claims can play. Hardware decode claims
/// follow the console's decoder probe (HardwareDecodeProbe), and the AV1 claim rav1g.dll's check
/// (Rav1gLibrary), both run once per process off the UI thread. Debug overrides stay in PlayerPage.
/// Call on the UI thread: the HDMI modes belong to the view.
/// </summary>
internal static class PlaybackClaims
{
    private static string? _loggedMatch;

    /// <summary>
    /// The connected display's HDR claims plus the decode claims this console backs. Waits for
    /// the decoder probe and the rav1g.dll check if they are still running, then continues on the
    /// calling (UI) thread. <c>Rav1g</c>: the claims carry rav1g's AV1 entry.
    /// </summary>
    /// <param name="rav1g">
    /// The attempt may claim rav1g's AV1 entry: up to 2160p60 at the 40 Mbps soft ceiling, beside dav1d's 1080p60, and the
    /// HDR AV1 tone map of rav1g's display pass (owner, 2026-11-26: rav1g is the default AV1 decoder in every build). It
    /// applies only where rav1g.dll is usable and software video is measured; debug av1-decoder dav1d / av1-claim 1080 pass
    /// false.
    /// </param>
    internal static async Task<(DeviceCapabilities Capabilities, bool SoftwareVideo, bool Rav1g)> DetectAsync(bool rav1g = true)
    {
        var hardware = await HardwareDecodeProbe.Limits;
        var softwareVideo = SoftwareVideo();
        rav1g = rav1g && softwareVideo && (await Rav1gLibrary.Status).Usable;
        var capabilities = DisplayHdrOutput.DetectCapabilities(ConsoleEvidence.ForHardware(hardware, rav1g));
        // Phase B (docs/software-decode.md): the stage takes FFmpeg software frames with wrapper bale.80+, so HDR software
        // video may be tone-mapped here, and the 4K software entry needs the stage's SDR downscale.
        var softwareStage = ConsoleToneMapRuntime.IsAvailable && ConsoleToneMapPolicy.SoftwareStageWrapperSupported(FfmpegWrapperVersion.Value);
        capabilities = capabilities with
        {
            OriginalDynamicRangeMappingSupported = ConsoleToneMapRuntime.IsAvailable,
            SoftwareDynamicRangeMappingSupported = softwareStage,
            // rav1g's display pass tone-maps HDR AV1 to SDR itself (wrapper bale.74+, the rav1g route's default since
            // 2026-11-24), so HDR AV1 originals are promised like HEVC/VP9 ones wherever rav1g decodes them.
            Av1DynamicRangeMappingSupported = rav1g,
        };
        capabilities = SoftwareDecodePolicy.ForSoftwareStage(capabilities, softwareStage && ConsoleToneMapRuntime.SupportsSdrDownscale);
        return (SoftwareDecodePolicy.ForSoftwareVideo(capabilities, softwareVideo), softwareVideo, rav1g);
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
        // The player's claims carry rav1g's AV1 entry where rav1g.dll is usable; the match counts what they admit.
        var rav1g = Rav1gLibrary.Status;
        if (probe.IsCompleted && rav1g.IsCompleted) SetVersionMatch(probe.Result, rav1g.Result.Usable);
        else _ = SetVersionMatchWhenProbedAsync(probe, rav1g);
    }

    private static async Task SetVersionMatchWhenProbedAsync(Task<HardwareDecodeLimits?> probe, Task<Rav1gLibraryStatus> rav1g)
    {
        var hardware = await probe;
        SetVersionMatch(hardware, (await rav1g).Usable);
    }

    private static void SetVersionMatch(HardwareDecodeLimits? hardware, bool rav1g)
    {
        DisplayVersionMatch? match = null;
        try
        {
            // The decode claims don't depend on the display, so this skips the HDR detection.
            var softwareVideo = SoftwareVideo();
            var claims = ConsoleEvidence.ForHardware(hardware, rav1g && softwareVideo);
            claims = SoftwareDecodePolicy.ForSoftwareVideo(claims, softwareVideo);
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
    /// Software video keeps the measured Series/AVX2 device gate, rav1g's AV1 entry included (rav1g is
    /// built and measured for Series consoles). dav1d uses CPU buffers and grain on D3D11, so its AV1
    /// claim does not load or depend on a D3D12 zero-copy runtime (removed 2026-11-27).
    /// </summary>
    private static bool SoftwareVideo() => DeviceIdentity.SoftwareVideoSupported;
}
