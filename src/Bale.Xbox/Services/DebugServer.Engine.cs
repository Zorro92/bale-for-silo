#if DEBUG_SERVER
namespace Bale.Xbox.Services;

internal sealed partial class DebugServer
{
    // Debug-only IMFMediaEngine spike (DebugMediaEngine); shares the playtest gate so probes never overlap.
    private static async Task<(string Status, string Body)> PlaytestEngineAsync(string? url, string? seconds)
    {
        if (!DebugMediaEngine.TryParse(url, seconds, out var uri, out var duration, out var error))
            return ("400 Bad Request", error + "\n");
        if (Interlocked.CompareExchange(ref _playtestBusy, 1, 0) != 0)
            return ("409 Conflict", "a playtest is already running\n");
        try
        {
            Log.Info("Debug engine probe: start");
            // A dedicated thread: the poll loop sleeps and must not tie up a thread-pool worker.
            var body = await Task.Factory.StartNew(() => DebugMediaEngine.Run(uri, duration),
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
