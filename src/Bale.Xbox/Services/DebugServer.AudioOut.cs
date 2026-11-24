#if DEBUG_SERVER
namespace Bale.Xbox.Services;

internal sealed partial class DebugServer
{
    /// <summary>
    /// GET /audio-out-test (docs/audio-passthrough.md, "Exclusive output"): bale_gpu's exclusive-mode output plays a
    /// tone for 1-30 s and reports what the device did; with variants=1 it only tries Initialize per variant on fresh
    /// clients, never started. Refused while a film plays (Media Foundation holds the endpoint in shared mode, and an
    /// exclusive client must not take it away) and while another media probe runs.
    /// </summary>
    private static async Task<(string Status, string Body)> AudioOutTestAsync(IReadOnlyDictionary<string, string> query)
    {
        if (!DebugAudioOutOptions.TryParse(query, out var options))
            return ("400 Bad Request", DebugAudioOutOptions.Usage);
        // Share the media-probe gate: never alongside a playtest or memory probe, which hold players of their own.
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
            if (inPlayer) return ("409 Conflict", "leave the player first: playback holds the audio endpoint\n");
            // A dedicated thread for up to ~40 s: create waits for the activation, then the tone plays. The variants
            // take one activation per row (milliseconds each).
            var report = await Task.Factory.StartNew(
                () => options.Variants ? DebugAudioOutTest.RunVariants() : DebugAudioOutTest.Run(options),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var summary = report.Split('\n').FirstOrDefault(line => line.StartsWith(DebugAudioOutReport.SummaryPrefix, StringComparison.Ordinal));
            var label = options.Variants ? "Debug audio-out-test variants: " : "Debug audio-out-test: ";
            Log.Info(label + (summary is null ? "no summary" : summary[DebugAudioOutReport.SummaryPrefix.Length..]));
            return ("200 OK", report);
        }
        finally { Interlocked.Exchange(ref _playtestBusy, 0); }
    }
}
#endif
