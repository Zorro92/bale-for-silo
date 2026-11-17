#if DEBUG_SERVER

namespace Bale.Xbox.Services;

/// <summary>
/// IMFMediaEngine rendering mode (windowless swap chain) on a full-screen SwapChainPanel: the one
/// lower-level path that keeps the system HDR10 / Dolby Vision presentation (docs/player-plan.md).
/// Debug only. The engine runs on its own thread in bale_gpu.dll; its swap chain handle is set on the
/// panel on the UI thread, also by bale_gpu.dll (ISwapChainPanelNative2). hdr=on switches HDMI to HDR10 first, as the player does, and restores after.
/// </summary>
internal sealed partial class DebugServer
{
    internal const string PresentUsage =
        "usage: GET /playtest-present?url=...&seconds=10 (1–60)&target=bgra|fp16|rgb10&hdr=on|off\n";

    private static async Task<(string Status, string Body)> PlaytestPresentAsync(IReadOnlyDictionary<string, string> query)
    {
        if (query.Keys.Any(key => key is not ("url" or "seconds" or "target" or "hdr")))
            return ("400 Bad Request", PresentUsage);
        if (!DebugMediaEngine.TryParse(query.GetValueOrDefault("url"), query.GetValueOrDefault("seconds") ?? "10", out var uri,
                out var duration, out var error))
            return ("400 Bad Request", error + "\n");
        var target = query.GetValueOrDefault("target");
        if (target is not (null or "bgra" or "fp16" or "rgb10")) return ("400 Bad Request", PresentUsage);
        var hdr = query.GetValueOrDefault("hdr");
        if (hdr is not (null or "on" or "off")) return ("400 Bad Request", PresentUsage);
        if (Interlocked.CompareExchange(ref _playtestBusy, 1, 0) != 0)
            return ("409 Conflict", "a playtest is already running\n");
        var host = new DebugEnginePresent();
        try
        {
            Log.Info($"Debug present probe (target {target ?? "bgra"}, hdr {hdr ?? "off"}): start");
            var opened = await OnUiTaskAsync(() => host.OpenAsync(hdr == "on"));
            var middle = Task.Delay(TimeSpan.FromSeconds(duration / 2.0 + 1))
                .ContinueWith(_ => OnUiAsync(DisplayHdrOutput.CurrentModeText)).Unwrap();
            var body = await Task.Factory.StartNew(() => host.Run(uri, duration, target), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var during = await middle;
            var closed = await OnUiTaskAsync(host.CloseAsync);
            Log.Info("Debug present probe: done");
            return ("200 OK", opened + body + $"Display during playback: {during}\n" + closed);
        }
        catch (Exception ex)
        {
            // Type and HRESULT only: never the message.
            return ("200 OK", $"Present probe error: {ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8}\n");
        }
        finally
        {
            if (host.IsOpen) await OnUiTaskAsync(host.CloseAsync);
            Interlocked.Exchange(ref _playtestBusy, 0);
        }
    }

    internal const string PresentEngineUsage =
        "usage: GET /present-engine (last report) | POST /present-engine?seconds=0..120 (0 = off; the next library playback presents through the engine)\n";

    private static (string Status, string Body) PresentEngine(string method, IReadOnlyDictionary<string, string> query)
    {
        if (method == "GET")
            return query.Count == 0 ? ("200 OK", $"present-engine: {DebugPresentEngineSwitch.Seconds} s\n{DebugPresentEngineSwitch.LastReport}")
                : ("400 Bad Request", PresentEngineUsage);
        if (query.Count != 1 || !int.TryParse(query.GetValueOrDefault("seconds"), out var seconds) || seconds is < 0 or > 120)
            return ("400 Bad Request", PresentEngineUsage);
        DebugPresentEngineSwitch.Set(seconds);
        Log.Info($"Debug present-engine: {seconds} s; applies to the next playback attempt");
        return ("200 OK", $"present-engine: {seconds} s; applies to the next playback attempt\n");
    }
}
#endif
