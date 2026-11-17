#if DEBUG_SERVER
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.Storage.Streams;
using Windows.UI.Core;

namespace Bale.Xbox;

/// <summary>
/// Debug: `present-engine` hands this mount's authenticated progressive stream to IMFMediaEngine
/// rendering mode (DebugEnginePresent) instead of MediaPlayer, records the report, then leaves.
/// HDMI was already set up by the mount like any playback.
/// </summary>
public sealed partial class PlayerPage
{
    private async Task PresentThroughEngineAsync(MediaRangeStream stream, string mime, int seconds, Bale.Core.Api.Models.PlaybackPlan plan)
    {
        var present = DebugPresentEngineSwitch.Mode != "frames";
        var frameRate = PlaybackStats.FromPlan(plan).FrameRate is { } fps && double.IsFinite(fps) && fps > 0 ? fps : 0;
        var random = NativeRandomAccessStream(stream) ?? stream.AsRandomAccessStream();
        var pointer = WinRT.MarshalInterface<IRandomAccessStream>.FromManaged(random);
        var host = new DebugEnginePresent();
        var report = $"Library playback through the engine ({(present ? "rendering mode" : "frame-server, decoded frames counted")}; {mime}; "
            + $"stream adapter {(stream is RustRangeStream ? "rust" : "managed")}; plan frame rate {(frameRate > 0 ? frameRate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "unknown")})\n";
        try
        {
            Log.Info("player: present-engine start");
            if (present) report += await host.OpenAsync(hdr: false);
            report += await Task.Factory.StartNew(() => host.RunStream(pointer, mime, seconds, frameRate, present), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch (Exception ex) { report += $"Present error: {ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8}\n"; }
        finally
        {
            if (present) report += await host.CloseAsync();
            Marshal.Release(pointer);
        }
        DebugPresentEngineSwitch.Report(report);
        // Step names, HRESULTs and counts only: the report never holds the URL.
        foreach (var line in report.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Log.Info("present-engine: " + line);
        // Leave once this mount (and the start lifecycle holding it) has returned.
        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => OnBack(closeControls: false));
    }
}
#endif
