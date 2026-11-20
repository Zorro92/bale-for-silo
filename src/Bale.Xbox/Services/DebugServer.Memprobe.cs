#if DEBUG_SERVER
namespace Bale.Xbox.Services;

internal sealed partial class DebugServer
{
    private static async Task<(string Status, string Body)> MemprobeAsync(IReadOnlyDictionary<string, string> query)
    {
        if (!DebugMemprobeOptions.TryParse(query, out var options))
            return ("400 Bad Request", DebugMemprobeOptions.Usage);
        // Share the media-probe gate: no two memory probes, or memory and playtest, may overlap.
        if (Interlocked.CompareExchange(ref _playtestBusy, 1, 0) != 0)
            return ("409 Conflict", "a probe is already running\n");
        try
        {
            var inPlayer = false;
            await OnUiAsync(() =>
            {
                inPlayer = CurrentPage(out _) is PlayerPage;
                return string.Empty;
            });
            if (inPlayer) return ("409 Conflict", "leave the player first\n");
            return ("200 OK", await Task.Factory.StartNew(() => DebugMemoryProbe.Run(options),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
        }
        finally { Interlocked.Exchange(ref _playtestBusy, 0); }
    }
}
#endif
