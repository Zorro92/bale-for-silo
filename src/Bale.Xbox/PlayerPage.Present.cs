#if DEBUG_SERVER
using System.Runtime.InteropServices;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.Storage.Streams;
using Windows.UI.Core;

namespace Bale.Xbox;

/// <summary>
/// Debug: `present-engine` hands this mount's routed source to IMFMediaEngine (DebugEnginePresent)
/// instead of MediaPlayer, records the report, then leaves. It runs after routing, so it plays what
/// Bale would: the FFmpegInteropX source (tone map on SDR, Dolby Vision profile 8 base layer, software
/// decode) through the engine extension, or the authenticated stream through Windows' own reader for
/// direct play. HDMI was already set up by the mount like any playback.
/// </summary>
public sealed partial class PlayerPage
{
    private async Task PresentThroughEngineAsync(Mount media, string mime, int seconds, Bale.Core.Api.Models.PlaybackPlan plan)
    {
        var present = DebugPresentEngineSwitch.Mode != "frames";
        var frameRate = PlaybackStats.FromPlan(plan).FrameRate is { } fps && double.IsFinite(fps) && fps > 0 ? fps : 0;
        var route = media.Route;
        var routeText = route.UseFfmpeg
            ? "ffmpeg " + (route.ConsoleToneMap ? "d3d11va + console tone map" : route.DolbyVision7BaseLayer ? "DV7 base layer"
                : route.SoftwareVideo ? "software decode" : route.DolbyVisionBaseLayer ? "system decoder, DV8 base layer" : "system decoder passthrough")
            : "Windows reader (direct)";
        var report = $"Library playback through the engine ({(present ? "rendering mode" : "frame-server, decoded frames counted")}; route {routeText}; {mime}; "
            + $"plan frame rate {(frameRate > 0 ? frameRate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "unknown")})\n";
        // The output guard would replan this attempt from the engine's frames; the probe owns the mount now.
        media.VideoOutputGuard?.Dispose();
        media.VideoOutputGuard = null;
        var host = new DebugEnginePresent();
        var owned = new List<nint>();
        try
        {
            Log.Info("player: present-engine start");
            if (present) report += await host.OpenAsync(hdr: false);
            if (media.FfmpegSource is { } ffmpeg)
            {
                // The MSS exists once CreateMediaPlaybackItem installed its handlers (the mount did that).
                var mss = ffmpeg.GetMediaStreamSource();
                var pointer = ((WinRT.IWinRTObject)mss).NativeObject.ThisPtr;
                var body = await RunEngineAsync(() => host.RunSource(pointer, seconds, frameRate, present));
                if (body.Contains("MF_MEDIASOURCE_SERVICE): fails", StringComparison.Ordinal) || body.Contains("at QI IMFGetService", StringComparison.Ordinal))
                {
                    // MediaPlayer consumes the WinRT MediaSource wrapper; its IMFGetService can expose the IMFMediaSource.
                    using var wrapper = Windows.Media.Core.MediaSource.CreateFromMediaStreamSource(mss);
                    var wrapperPointer = ((WinRT.IWinRTObject)wrapper).NativeObject.ThisPtr;
                    body = "Attempt 1 (MediaStreamSource) failed; attempt 2 (MediaSource wrapper)\n"
                        + await RunEngineAsync(() => host.RunSource(wrapperPointer, seconds, frameRate, present));
                    GC.KeepAlive(wrapper);
                }
                GC.KeepAlive(mss);
                report += body;
            }
            else if (media.RandomStream is { } random)
            {
                var pointer = WinRT.MarshalInterface<IRandomAccessStream>.FromManaged(random);
                owned.Add(pointer);
                report += await RunEngineAsync(() => host.RunStream(pointer, mime, seconds, frameRate, present));
            }
            else report += "Route not covered by the probe (HLS / adaptive source)\n";
        }
        catch (Exception ex) { report += $"Present error: {ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8}\n"; }
        finally
        {
            if (present) report += await host.CloseAsync();
            foreach (var pointer in owned) Marshal.Release(pointer);
        }
        DebugPresentEngineSwitch.Report(report);
        // Step names, HRESULTs and counts only: the report never holds the URL.
        foreach (var line in report.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Log.Info("present-engine: " + line);
        // Leave once this mount (and the start lifecycle holding it) has returned.
        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => OnBack(closeControls: false));
    }

    private static Task<string> RunEngineAsync(Func<string> run) =>
        Task.Factory.StartNew(run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
#endif
