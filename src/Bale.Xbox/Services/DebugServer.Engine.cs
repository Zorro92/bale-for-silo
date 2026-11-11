#if DEBUG_SERVER
using FFmpegInteropX;

namespace Bale.Xbox.Services;

internal sealed partial class DebugServer
{
    // Debug-only IMFMediaEngine spike; shares the playtest gate so probes never overlap.
    // mode=cs (default) runs the C# probe (DebugMediaEngine), mode=rust its Rust port in bale_gpu.dll, and
    // mode=rust-ffmpeg plays an FFmpegInteropX software-decoded source through the Rust engine
    // (its MediaStreamSource handed over as IMFMediaSource via an IMFMediaEngineExtension).
    // target=bgra|fp16|rgb10 (rust only; default bgra) picks the engine output format and destination
    // texture; fp16 and rgb10 add the HDR readback (phase 0 of GPU tone mapping, docs/player-plan.md).
    private static async Task<(string Status, string Body)> PlaytestEngineAsync(string? url, string? seconds, string? mode = null,
        string? target = null)
    {
        if (!DebugMediaEngine.TryParse(url, seconds, out var uri, out var duration, out var error))
            return ("400 Bad Request", error + "\n");
        if (mode is not (null or "cs" or "rust" or "rust-ffmpeg"))
            return ("400 Bad Request", "mode must be cs, rust or rust-ffmpeg\n");
        if (target is not (null or "bgra" or "fp16" or "rgb10"))
            return ("400 Bad Request", "target must be bgra, fp16 or rgb10\n");
        if (target is not null && mode != "rust")
            return ("400 Bad Request", "target is only for mode=rust\n");
        if (Interlocked.CompareExchange(ref _playtestBusy, 1, 0) != 0)
            return ("409 Conflict", "a playtest is already running\n");
        var releaseGate = true;
        try
        {
            Log.Info($"Debug engine probe ({mode ?? "cs"}{(target is null ? string.Empty : ", target " + target)}): start");
            string body;
            if (mode == "rust-ffmpeg")
            {
                var config = DebugFfmpegSource.Configuration("software", new DebugFfmpegTuning());
                var creation = FFmpegMediaSource.CreateFromUriAsync(uri.AbsoluteUri, config).AsTask();
                if (await Task.WhenAny(creation, Task.Delay(DebugFfmpegPlaytest.CreateTimeoutMs)) != creation)
                {
                    // Native creation may ignore cancellation; the late disposal releases the gate.
                    releaseGate = false;
                    _ = DisposeLateFfmpegSourceAsync(creation);
                    return ("200 OK", $"FFmpeg create timed out after {DebugFfmpegPlaytest.CreateTimeoutMs} ms\n");
                }
                using var source = await creation;
                // CreateMediaPlaybackItem installs the MediaStreamSource's sample handlers (see DebugFrameDelivery).
                var item = source.CreateMediaPlaybackItem();
                var streamSource = source.GetMediaStreamSource();
                var pointer = ((WinRT.IWinRTObject)streamSource).NativeObject.ThisPtr;
                body = "FFmpeg source: software decoder mode, release tuning\nAttempt 1: MediaStreamSource\n"
                    + await RunEngineThreadAsync(() => DebugRustProbe.RunEngineSource(pointer, duration));
                if (body.Contains("MF_MEDIASOURCE_SERVICE: fails", StringComparison.Ordinal)
                    || body.Contains("at QI IMFGetService", StringComparison.Ordinal))
                {
                    // The WinRT MediaSource wrapper is what MediaPlayer consumes; try its IMFGetService instead.
                    using var wrapper = Windows.Media.Core.MediaSource.CreateFromMediaStreamSource(streamSource);
                    var wrapperPointer = ((WinRT.IWinRTObject)wrapper).NativeObject.ThisPtr;
                    body += "Attempt 2: MediaSource.CreateFromMediaStreamSource\n"
                        + await RunEngineThreadAsync(() => DebugRustProbe.RunEngineSource(wrapperPointer, duration));
                    GC.KeepAlive(wrapper);
                }
                GC.KeepAlive(streamSource);
                GC.KeepAlive(item);
            }
            else
            {
                var rust = mode == "rust";
                body = await RunEngineThreadAsync(() => rust ? DebugRustProbe.RunEngine(uri, duration, target) : DebugMediaEngine.Run(uri, duration));
            }
            Log.Info("Debug engine probe: done");
            return ("200 OK", body);
        }
        catch (Exception ex)
        {
            // Type and HRESULT only: never the message (it may echo media details).
            return ("200 OK", $"Engine probe error: {ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8}\n");
        }
        finally
        {
            if (releaseGate) Interlocked.Exchange(ref _playtestBusy, 0);
        }
    }

    // A dedicated thread: the poll loop sleeps and must not tie up a thread-pool worker.
    private static Task<string> RunEngineThreadAsync(Func<string> probe)
        => Task.Factory.StartNew(probe, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
#endif
