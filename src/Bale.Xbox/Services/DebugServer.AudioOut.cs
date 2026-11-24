#if DEBUG_SERVER
using Windows.UI.Xaml;

namespace Bale.Xbox.Services;

internal sealed partial class DebugServer
{
    /// <summary>
    /// GET /audio-out-test (docs/audio-passthrough.md, "Exclusive output"): bale_gpu's exclusive-mode output plays a
    /// tone for 1-30 s and reports what the device did; with variants=1 it only tries Initialize per variant on fresh
    /// clients, never started. Refused while a film plays (Media Foundation holds the endpoint in shared mode, and an
    /// exclusive client must not take it away) and while another media probe runs. The report starts with the app's
    /// XAML UI sound state: they may hold a shared stream on the endpoint (AUDCLNT_E_DEVICE_IN_USE), and uisounds=off
    /// switches them off for the run, restored afterwards.
    /// </summary>
    private static async Task<(string Status, string Body)> AudioOutTestAsync(IReadOnlyDictionary<string, string> query)
    {
        if (!DebugAudioOutOptions.TryParse(query, out var options))
            return ("400 Bad Request", DebugAudioOutOptions.Usage);
        // Share the media-probe gate: never alongside a playtest or memory probe, which hold players of their own.
        if (Interlocked.CompareExchange(ref _playtestBusy, 1, 0) != 0)
            return ("409 Conflict", "a probe is already running\n");
        ElementSoundPlayerState? restore = null;
        try
        {
            var inPlayer = false;
            var sounds = string.Empty;
            await OnUiAsync(() =>
            {
                inPlayer = CurrentPage(out _) is PlayerPage;
                sounds = ElementSoundPlayer.State.ToString();
                if (!inPlayer && options.UiSoundsOff)
                {
                    restore = ElementSoundPlayer.State;
                    ElementSoundPlayer.State = ElementSoundPlayerState.Off;
                }
                return string.Empty;
            });
            if (inPlayer) return ("409 Conflict", "leave the player first: playback holds the audio endpoint\n");
            // A dedicated thread for up to ~50 s: create waits for the activations, then the tone plays. The variants
            // take one activation and a 150 ms pause per row (about 12 s).
            var report = await Task.Factory.StartNew(
                () => options.Variants ? DebugAudioOutTest.RunVariants() : DebugAudioOutTest.Run(options),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            report = DebugAudioOutReport.UiSounds(sounds, restore is not null) + report;
            var summary = report.Split('\n').FirstOrDefault(line => line.StartsWith(DebugAudioOutReport.SummaryPrefix, StringComparison.Ordinal));
            var label = options.Variants ? "Debug audio-out-test variants: " : "Debug audio-out-test: ";
            Log.Info(label + (summary is null ? "no summary" : summary[DebugAudioOutReport.SummaryPrefix.Length..]));
            return ("200 OK", report);
        }
        finally
        {
            try
            {
                if (restore is { } state)
                    await OnUiAsync(() =>
                    {
                        ElementSoundPlayer.State = state;
                        return string.Empty;
                    });
            }
            finally { Interlocked.Exchange(ref _playtestBusy, 0); }
        }
    }
}
#endif
