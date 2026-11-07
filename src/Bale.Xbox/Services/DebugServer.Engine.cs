#if DEBUG_SERVER
namespace Bale.Xbox.Services;

internal sealed partial class DebugServer
{
    // Debug-only IMFMediaEngine spike (DebugMediaEngine); shares the playtest gate so probes never overlap.
    // mode=cs (default) runs the C# probe, mode=rust its Rust port in bale_gpu.dll.
    private static async Task<(string Status, string Body)> PlaytestEngineAsync(string? url, string? seconds, string? mode = null)
    {
        if (!DebugMediaEngine.TryParse(url, seconds, out var uri, out var duration, out var error))
            return ("400 Bad Request", error + "\n");
        if (mode is not (null or "cs" or "rust"))
            return ("400 Bad Request", "mode must be cs or rust\n");
        var rust = mode == "rust";
        if (Interlocked.CompareExchange(ref _playtestBusy, 1, 0) != 0)
            return ("409 Conflict", "a playtest is already running\n");
        try
        {
            Log.Info(rust ? "Debug engine probe (rust): start" : "Debug engine probe: start");
            // A dedicated thread: the poll loop sleeps and must not tie up a thread-pool worker.
            var body = await Task.Factory.StartNew(() => rust ? DebugRustProbe.RunEngine(uri, duration) : DebugMediaEngine.Run(uri, duration),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Log.Info("Debug engine probe: done");
            return ("200 OK", body);
        }
        finally
        {
            Interlocked.Exchange(ref _playtestBusy, 0);
        }
    }
}
#endif
